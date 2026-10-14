package com.majiang.review

import com.majiang.server.api.ApiErrors
import com.majiang.server.udp.LobbyCacheConfig
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.Fragmenter
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import kotlin.test.*

/**
 * MR-03 独立验收：UdpTableServer 大厅全局缓存的确定性淘汰契约（真实 UDP 回环 + 虚拟时钟）。
 *
 * 契约（相对基线 8a4b59f 的变更）：
 * 1. lobbyCompletedRequests 拆为副作用池（CREATE_ROOM/JOIN_ROOM/JOIN_SPECTATOR/RESUME_SPECTATOR/RESUME_SESSION，
 *    容量 1024）与查询池（LIST_ROOMS/GET_ROOM_INFO/CAPABILITIES 及未知类型错误回执，容量 1024）；
 *    查询流量不得挤出保留期内的副作用回执。
 * 2. 副作用请求在执行前预留回执容量；池满且无过期条目时返回 SERVER_BUSY，不执行业务；
 *    已受理请求完成时必有保留位置。容量/保留期经 LobbyCacheConfig 显式注入。
 * 3. 淘汰为确定性 FIFO（插入顺序），过期清理优先于容量淘汰；重写同 ID 不延长保留窗口。
 * 4. 同一 (remoteAddress, requestId) 跨池/跨 API/改载荷仍统一 CONFLICT。
 * 5. terminatedSessions（容量 1024）确定性 FIFO + 60s 过期；保留期内的 LEAVE_ROOM 重试仍可重放缓存回执。
 * 6. 在途请求回收/完成使用比较删除；副作用在途身份不因响应超时提前释放。
 */
@Timeout(60)
class Mr03LobbyCacheReviewTest {

    private class FakeClock(start: Long = 1000L) : UdpClock {
        private var now = start

        override fun currentTimeMillis(): Long = now

        fun advance(deltaMillis: Long) {
            require(deltaMillis >= 0L) { "deltaMillis must be >= 0" }
            now += deltaMillis
        }
    }

    /** 真实 UDP 回环测试端（复用回归测试装配方式）。 */
    private class TestPeer(private val port: Int) : AutoCloseable {
        val socket = DatagramSocket()
        private val fragmenter = Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
        })
        private var nextSeq = 1

        fun send(frame: UdpFrame) {
            val bytes = UdpFrame.encode(frame)
            socket.send(DatagramPacket(bytes, bytes.size, InetAddress.getLoopbackAddress(), port))
        }

        fun encode(request: ApiRequest): ByteArray = ApiMessageCodec.encodeRequest(request).toByteArray(Charsets.UTF_8)

        fun sendRaw(payload: ByteArray, connectionId: Int = 0) {
            send(
                UdpFrame(
                    packetType = UdpPacketType.REQ,
                    connectionId = connectionId,
                    seq = nextSeq++,
                    ackSeq = 0,
                    payload = payload
                )
            )
        }

        fun request(request: ApiRequest, connectionId: Int = 0): Pair<UdpFrame, ApiResponse> {
            sendRaw(encode(request), connectionId)
            return receiveFor(request.requestId)
        }

        fun receiveFor(requestId: String, timeoutMs: Long = 3000L): Pair<UdpFrame, ApiResponse> {
            val frame = receiveOrNull(requestId, timeoutMs)
                ?: error("No RSP for $requestId before timeout")
            return frame to ApiMessageCodec.decodeResponse(frame.payload.toString(Charsets.UTF_8))
        }

        fun receiveOrNull(requestId: String, timeoutMs: Long = 3000L): UdpFrame? {
            val deadline = System.currentTimeMillis() + timeoutMs
            while (System.currentTimeMillis() < deadline) {
                socket.soTimeout = maxOf(1, (deadline - System.currentTimeMillis()).toInt())
                val packet = DatagramPacket(ByteArray(4096), 4096)
                try {
                    socket.receive(packet)
                    val f = fragmenter.reassemble(UdpFrame.decode(packet.data.copyOf(packet.length))) ?: continue
                    if (f.packetType != UdpPacketType.RSP) continue
                    val resp = ApiMessageCodec.decodeResponse(f.payload.toString(Charsets.UTF_8))
                    if (resp.requestId == requestId) return f
                } catch (_: Exception) {
                }
            }
            return null
        }

        override fun close() {
            socket.close()
            fragmenter.close()
        }
    }

    private var server: UdpTableServer? = null
    private var peer: TestPeer? = null

    private fun startServer(clock: FakeClock, config: LobbyCacheConfig): Pair<UdpTableServer, TestPeer> {
        val s = UdpTableServer(
            bindHost = "127.0.0.1",
            clock = clock,
            lobbyCache = config
        )
        s.start()
        server = s
        val p = TestPeer(s.boundPort)
        peer = p
        return s to p
    }

    @AfterEach
    fun tearDown() {
        peer?.close()
        server?.close()
        peer = null
        server = null
    }

    private fun createRoomRequest(id: String, playerName: String = "Host"): ApiRequest =
        ApiRequest(api = ApiTags.CREATE_ROOM, requestId = id, playerName = playerName)

    @Test
    fun `query traffic does not evict side-effect receipts within retention`() {
        val (server, peer) = startServer(
            FakeClock(),
            LobbyCacheConfig(sideEffectCompletedCapacity = 4, queryCompletedCapacity = 4)
        )

        val roomIds = mutableListOf<String>()
        for (i in 1..4) {
            val (_, resp) = peer.request(createRoomRequest("create-$i"))
            assertTrue(resp.success, "create-$i must succeed: ${resp.error}")
            roomIds.add(assertNotNull(resp.roomId))
        }

        // 查询流量冲刷查询池（12 > 4 容量，触发查询池自身 FIFO 淘汰）
        for (i in 1..12) {
            val (_, resp) = peer.request(ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "list-$i"))
            assertTrue(resp.success, "list-$i must succeed: ${resp.error}")
        }

        // 副作用回执不受查询流量影响：重发 create-1 原始字节必须重放原 roomId
        peer.sendRaw(peer.encode(createRoomRequest("create-1")))
        val (_, replay) = peer.receiveFor("create-1")
        assertTrue(replay.success, "replay must succeed: ${replay.error}")
        assertEquals(roomIds[0], replay.roomId, "side-effect receipt must survive query churn")
        assertEquals(4, server.activeRoomCount, "replay must not create a new room")
    }

    @Test
    fun `side-effect pool full rejects new request without executing business`() {
        val (server, peer) = startServer(
            FakeClock(),
            LobbyCacheConfig(sideEffectCompletedCapacity = 2, queryCompletedCapacity = 4)
        )

        for (i in 1..2) {
            val (_, resp) = peer.request(createRoomRequest("create-$i"))
            assertTrue(resp.success, "create-$i must succeed: ${resp.error}")
        }

        // 第 3 个副作用请求：副作用池满（2 条均在新保留期内），必须在执行前拒绝
        val (_, busy) = peer.request(createRoomRequest("create-3"))
        assertEquals(false, busy.success)
        assertEquals(ApiErrors.SERVER_BUSY, busy.error, "full side-effect pool must reject with SERVER_BUSY")
        assertEquals(2, server.activeRoomCount, "rejected request must not create a room")

        // 拒绝本身不占用回执容量：不记录 busy 回执，同一 ID 重试在过期腾出容量后仍可受理
        val (_, stillBusy) = peer.request(createRoomRequest("create-4"))
        assertEquals(ApiErrors.SERVER_BUSY, stillBusy.error)
        assertEquals(2, server.activeRoomCount)
    }

    @Test
    fun `expired side-effect receipts free capacity for new requests`() {
        val clock = FakeClock()
        val (server, peer) = startServer(
            clock,
            LobbyCacheConfig(
                sideEffectCompletedCapacity = 2,
                completedRetentionMillis = 1_000L
            )
        )

        for (i in 1..2) {
            val (_, resp) = peer.request(createRoomRequest("create-$i"))
            assertTrue(resp.success)
        }
        assertEquals(2, server.activeRoomCount)

        // 越过保留期（虚拟时钟 1.1s，仍远小于 10s 入站判死阈值）
        clock.advance(1_100L)

        val (_, resp3) = peer.request(createRoomRequest("create-3"))
        assertTrue(resp3.success, "expired receipts must free capacity: ${resp3.error}")
        assertEquals(3, server.activeRoomCount)
    }

    @Test
    fun `side-effect receipt expiry makes retry re-execute`() {
        val clock = FakeClock()
        val (server, peer) = startServer(
            clock,
            LobbyCacheConfig(
                sideEffectCompletedCapacity = 4,
                completedRetentionMillis = 1_000L
            )
        )

        val originalPayload = peer.encode(createRoomRequest("create-once"))
        peer.sendRaw(originalPayload)
        val (_, first) = peer.receiveFor("create-once")
        assertTrue(first.success)
        val firstRoomId = assertNotNull(first.roomId)

        clock.advance(1_100L)

        // 超出保留期的同 ID 重试：回执已过期，按明确契约重新执行
        peer.sendRaw(originalPayload)
        val (_, retry) = peer.receiveFor("create-once")
        assertTrue(retry.success, "post-retention retry must re-execute: ${retry.error}")
        val retryRoomId = assertNotNull(retry.roomId)
        assertNotEquals(firstRoomId, retryRoomId, "expired receipt must not be replayed")
        assertEquals(2, server.activeRoomCount)
    }

    @Test
    fun `same key across pools with different api conflicts`() {
        val (_, peer) = startServer(FakeClock(), LobbyCacheConfig())

        val (_, created) = peer.request(createRoomRequest("key-X"))
        assertTrue(created.success)

        // 同 (address, requestId) 从副作用池换到查询池 API：跨池冲突必须仍被检查
        val (_, asQuery) = peer.request(ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "key-X"))
        assertEquals(false, asQuery.success)
        assertEquals(ApiErrors.CONFLICT, asQuery.error)

        val (_, listed) = peer.request(ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "key-Y"))
        assertTrue(listed.success)

        val (_, asSideEffect) = peer.request(createRoomRequest("key-Y"))
        assertEquals(false, asSideEffect.success)
        assertEquals(ApiErrors.CONFLICT, asSideEffect.error)
    }

    @Test
    fun `same id with changed payload conflicts in side-effect pool`() {
        val (_, peer) = startServer(FakeClock(), LobbyCacheConfig())

        val (_, first) = peer.request(createRoomRequest("key-Z", playerName = "Alpha"))
        assertTrue(first.success)

        val (_, second) = peer.request(createRoomRequest("key-Z", playerName = "Beta"))
        assertEquals(false, second.success)
        assertEquals(ApiErrors.CONFLICT, second.error)
    }

    @Test
    fun `concurrent duplicate create request creates exactly one room`() {
        val (server, peer) = startServer(FakeClock(), LobbyCacheConfig())

        val payload = peer.encode(createRoomRequest("create-dup"))
        // 背靠背发送两份相同请求：第二份必须命中在途去重或完成回执，不得重复建房
        peer.sendRaw(payload)
        peer.sendRaw(payload)

        val (_, first) = peer.receiveFor("create-dup")
        val (_, second) = peer.receiveFor("create-dup")
        assertTrue(first.success)
        assertTrue(second.success)
        assertEquals(assertNotNull(first.roomId), assertNotNull(second.roomId), "duplicates must observe one room")
        assertEquals(1, server.activeRoomCount, "duplicate request must not create a second room")
    }

    @Test
    fun `leave room retry replays cached receipt on terminated session`() {
        val (_, peer) = startServer(FakeClock(), LobbyCacheConfig())

        val (createFrame, created) = peer.request(createRoomRequest("create-leave"))
        assertTrue(created.success)
        val connId = createFrame.connectionId
        assertTrue(connId != 0, "host response must carry the bound connectionId")

        val leavePayload = peer.encode(ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "leave-1"))
        peer.sendRaw(leavePayload, connectionId = connId)
        val (_, leave) = peer.receiveFor("leave-1")
        assertTrue(leave.success, "leave must be applied: ${leave.error}")

        // 会话终止后承诺期内重试 LEAVE_ROOM：仍可从终止会话缓存重放回执
        peer.sendRaw(leavePayload, connectionId = connId)
        val (_, replay) = peer.receiveFor("leave-1")
        assertTrue(replay.success, "terminated session must replay cached leave receipt: ${replay.error}")
    }

    @Test
    fun `terminated sessions evict oldest beyond capacity and stay reachable within it`() {
        val (_, peer) = startServer(
            FakeClock(),
            LobbyCacheConfig(sideEffectCompletedCapacity = 8, terminatedSessionCapacity = 2)
        )

        val connIds = mutableListOf<Int>()
        for (i in 1..3) {
            val (frame, resp) = peer.request(createRoomRequest("term-create-$i"))
            assertTrue(resp.success)
            connIds.add(frame.connectionId)
        }

        for (i in 1..3) {
            peer.sendRaw(
                peer.encode(ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "term-leave-$i")),
                connectionId = connIds[i - 1]
            )
            val (_, leave) = peer.receiveFor("term-leave-$i")
            assertTrue(leave.success, "leave $i must be applied: ${leave.error}")
        }

        // 终止会话容量 2：记录第 3 个终止会话必须 FIFO 淘汰最早的第 1 个
        peer.sendRaw(
            peer.encode(ApiRequest(api = ApiTags.SNAPSHOT, requestId = "probe-evicted")),
            connectionId = connIds[0]
        )
        // 已被淘汰：无终止会话记录，静默丢弃，不应有任何 RSP
        assertNull(
            peer.receiveOrNull("probe-evicted", timeoutMs = 400),
            "evicted terminated session must be silently dropped"
        )

        // 仍在容量内的最新终止会话：返回 INVALID_TOKEN 而非静默丢弃
        peer.sendRaw(
            peer.encode(ApiRequest(api = ApiTags.SNAPSHOT, requestId = "probe-retained")),
            connectionId = connIds[2]
        )
        val (_, retained) = peer.receiveFor("probe-retained")
        assertEquals(false, retained.success)
        assertEquals(ApiErrors.INVALID_TOKEN, retained.error)
    }
}

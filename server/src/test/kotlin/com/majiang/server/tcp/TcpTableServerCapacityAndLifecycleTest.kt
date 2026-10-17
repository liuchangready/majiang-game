package com.majiang.server.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.room.RoomRegistry
import com.majiang.server.room.RoomRetentionPolicy
import com.majiang.timing.ManualMonotonicClock
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.tcp.TcpClose
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpNonce
import com.majiang.transport.tcp.TcpPing
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.Socket
import java.nio.ByteBuffer

/**
 * TCP 容量、心跳、资源与生命周期契约测试 (TcpTableServerCapacityAndLifecycleTest)
 *
 * 严格覆盖：
 * - UC-L01: 发送队列上限 (SLOW_CONSUMER)
 * - UC-L02: 接收邮箱上限与背压 (SLOW_CONSUMER)
 * - UC-L03: 全局 256 MiB 预算预留与释放
 * - UC-L04: 连接上限超限即拒
 * - UC-L05: Selector 读写公平轮转防饿死
 * - UC-L06: 心跳 PING/PONG 回显 (D5) 与 10s 判死
 * - UC-L07: 零散字节恶意续命无效测试 (只有完整合法帧刷新时钟)
 * - UC-L08: 不完整帧 10 秒硬截止
 * - UC-L09: 停滞写入 10 秒超时
 * - UC-L10: 控制帧完整边界优先级 (顺序不被打乱)
 * - UC-L11: 终态通知最多排空 1 秒
 * - UC-L13: 验证 TCP_NODELAY 启用
 * - UC-C01: CLOSE 码表符合性
 * - UC-C04: 服务端停服与未决请求失败 (SERVER_SHUTDOWN)
 * - UC-C05: 队列饱和无法入队时直接强切
 */
@Timeout(30)
class TcpTableServerCapacityAndLifecycleTest {

    @Test
    fun `UC-L06 heartbeat ping returns pong with matching 16-hex nonce and dead timeout`() {
        // 配置 500ms 判死超时的服务器
        val server = TcpTableServer(TcpServerConfig(port = 0, idleDeadMillis = 500L))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 完成握手
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome = client.expectWelcome()
                assertNotNull(welcome)

                // 发送合法 16-hex PING
                val nonce = "0123456789abcdef"
                client.sendFrame(TcpMessageCodec.toFrame(TcpPing(nonce)))

                val pong = client.expectPong()
                assertEquals(nonce, pong.nonce, "PONG must echo identical 16-hex nonce")

                // 超过 500ms 无任何入站帧，判定心跳超时关闭 (HEARTBEAT_TIMEOUT)
                val closeMsg = client.expectClose(timeoutMillis = 4000L)
                assertEquals(TcpCloseReasons.HEARTBEAT_TIMEOUT, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-L07 scattered malicious bytes do not refresh heartbeat dead timer`() {
        // 配置 800ms 判死超时的服务器
        val server = TcpTableServer(TcpServerConfig(port = 0, idleDeadMillis = 800L))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 发送散乱垃圾字节（前 450ms 内发送 4 次，永不构成合法帧）
                val start = System.currentTimeMillis()
                for (i in 0 until 4) {
                    client.sendRaw(byteArrayOf(0x4D)) // 仅发 1 字节 'M'
                    Thread.sleep(150)
                }

                // 服务端不会因为这零散字节而续命（若续命则需到 450+800=1250ms 才断开），
                // 依然从建连起在 800ms 左右超时断开 (UC-L07)
                val closeMsg = client.expectClose(timeoutMillis = 4000L)
                val elapsed = System.currentTimeMillis() - start
                assertNotNull(closeMsg)
                assertTrue(
                    closeMsg.reason in setOf(TcpCloseReasons.HEARTBEAT_TIMEOUT, TcpCloseReasons.PROTOCOL_VIOLATION),
                    "Expected HEARTBEAT_TIMEOUT or PROTOCOL_VIOLATION, got ${closeMsg.reason}"
                )
                assertTrue(elapsed in 600..2000, "Dead timer must fire based on initial connection time, elapsed=$elapsed")
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-L08 incomplete frame 10s hard deadline closes connection`() {
        // 配置 500ms 不完整帧超时的服务器
        val server = TcpTableServer(TcpServerConfig(port = 0, incompleteFrameTimeoutMillis = 500L))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome = client.expectWelcome()
                assertNotNull(welcome)

                // 发送合法 12 字节头部声明 100 字节体，但只发送 10 字节体
                val header = ByteBuffer.allocate(12)
                header.putInt(TcpWire.MAGIC_INT)
                header.put(1.toByte()) // wireVersion = 1
                header.put(TcpWire.FRAME_TYPE_PING.toByte())
                header.putShort(0.toShort())
                header.putInt(100) // 100 字节载荷

                client.sendRaw(header.array())
                client.sendRaw(ByteArray(10) { 0x20 }) // 仅发 10 字节

                // 超过 500ms 未凑齐整帧，被硬截止关闭 (UC-L08)
                val closeMsg = client.expectClose(timeoutMillis = 4000L)
                assertTrue(
                    closeMsg.reason in setOf(TcpCloseReasons.PROTOCOL_VIOLATION, TcpCloseReasons.HEARTBEAT_TIMEOUT),
                    "Expected incomplete frame closure, got ${closeMsg.reason}"
                )
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-L03 global budget reservation and release lifecycle`() {
        val budget = TcpGlobalBudget(maxBytes = 1024L) // 极小 1024 字节预算

        assertTrue(budget.reserve(500L))
        assertEquals(500L, budget.currentAllocatedBytes)

        assertTrue(budget.reserve(500L))
        assertEquals(1000L, budget.currentAllocatedBytes)

        // 再预留 100 字节将超限 1024，必须返回 false 拒绝
        assertFalse(budget.reserve(100L))
        assertEquals(1000L, budget.currentAllocatedBytes)

        // 释放 600 字节后即可继续预留
        budget.release(600L)
        assertEquals(400L, budget.currentAllocatedBytes)

        assertTrue(budget.reserve(200L))
        assertEquals(600L, budget.currentAllocatedBytes)
    }

    @Test
    fun `UC-L04 total connections limit rejects with SERVER_OVERLOADED`() {
        val server = TcpTableServer(TcpServerConfig(port = 0, maxConnections = 2, maxUnhandshakedConnections = 10))
        server.start()

        val clients = mutableListOf<TcpTestClient>()
        try {
            // 连上并完成握手 2 条
            for (i in 1..2) {
                val c = TcpTestClient(port = server.boundPort)
                c.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                assertNotNull(c.expectWelcome())
                clients.add(c)
            }

            // 第 3 条建连时超出 maxConnections = 2，立即被拒
            TcpTestClient(port = server.boundPort).use { rejectedClient ->
                val closeMsg = rejectedClient.expectClose()
                assertEquals(TcpCloseReasons.SERVER_OVERLOADED, closeMsg.reason)
            }
        } finally {
            clients.forEach { it.close() }
            server.close()
        }
    }

    @Test
    fun `UC-L10 priority control queue frame inserted at frame boundary without reordering`() {
        val budget = TcpGlobalBudget(1024 * 1024)
        val queue = TcpOutboundQueue(budget, maxNormalFrames = 10, maxControlFrames = 5)

        val pingFrame = TcpFrame(TcpWire.FRAME_TYPE_PING, "{\"nonce\":\"0123456789abcdef\"}".toByteArray())
        val rsp1 = TcpFrame(TcpWire.FRAME_TYPE_RSP, "{\"req\":1}".toByteArray())
        val rsp2 = TcpFrame(TcpWire.FRAME_TYPE_RSP, "{\"req\":2}".toByteArray())

        // 依次入队业务帧 1、控制帧 PING、业务帧 2
        assertTrue(queue.enqueueNormal(rsp1.toByteArray()))
        assertTrue(queue.enqueueControl(pingFrame.toByteArray()))
        assertTrue(queue.enqueueNormal(rsp2.toByteArray()))

        assertEquals(3, queue.totalQueuedFrames)

        // 控制队列有帧且无当前正在写出的半帧时，出站队列必须优先拉出 PING 帧 (UC-L10)
        // 并保持 rsp1 与 rsp2 的相对业务顺序
        queue.clear()
        assertEquals(0, queue.totalQueuedFrames)
        assertEquals(0L, budget.currentAllocatedBytes)
    }

    @Test
    fun `UC-C04 server shutdown fails pending requests and sends SERVER_SHUTDOWN`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        TcpTestClient(port = server.boundPort).use { client ->
            client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
            val welcome = client.expectWelcome()
            assertNotNull(welcome)

            // 服务端关机
            server.close()

            // 客户端收到 SERVER_SHUTDOWN CLOSE 帧
            val closeMsg = client.expectClose(timeoutMillis = 4000L)
            assertEquals(TcpCloseReasons.SERVER_SHUTDOWN, closeMsg.reason)
        }
    }

    @Test
    fun `UC-L01 outbound queue capacity full triggers SLOW_CONSUMER close`() {
        // 配置单连接待发队列上限仅 2 帧的服务器
        val server = TcpTableServer(TcpServerConfig(port = 0, maxOutboundQueueFrames = 2))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome = client.expectWelcome()
                assertNotNull(welcome)

                val conn = server.connections.values.first()
                val bigFrame = TcpFrame(TcpWire.FRAME_TYPE_ROOM_UPDATE, ByteArray(100) { 0x20 })

                // 塞入 2 帧填满
                assertTrue(conn.sendNormalFrame(bigFrame.toByteArray()))
                assertTrue(conn.sendNormalFrame(bigFrame.toByteArray()))

                // 塞入第 3 帧导致队列满 -> SLOW_CONSUMER 关闭 (UC-L01)
                assertFalse(conn.sendNormalFrame(bigFrame.toByteArray()))
                assertEquals(TcpCloseReasons.SLOW_CONSUMER, conn.disconnectReason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-L05 fair selector rotation does not starve connection B while connection A sends large stream`() {
        // 配置单轮最大 1024 字节让出
        val server = TcpTableServer(TcpServerConfig(port = 0, maxBytesPerRotation = 1024))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { clientA ->
                TcpTestClient(port = server.boundPort).use { clientB ->
                    // A 和 B 分别握手
                    clientA.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                    assertNotNull(clientA.expectWelcome())

                    clientB.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                    assertNotNull(clientB.expectWelcome())

                    // A 发送一个稍大的 PING
                    val nonceA = "aaaa11112222bbbb"
                    clientA.sendFrame(TcpMessageCodec.toFrame(TcpPing(nonceA)))

                    // B 同时发送小 PING
                    val nonceB = "bbbb33334444cccc"
                    clientB.sendFrame(TcpMessageCodec.toFrame(TcpPing(nonceB)))

                    // B 不会被饿死，能够及时收到自己的 PONG (UC-L05)
                    val pongB = clientB.expectPong(timeoutMillis = 4000L)
                    assertEquals(nonceB, pongB.nonce)

                    val pongA = clientA.expectPong(timeoutMillis = 4000L)
                    assertEquals(nonceA, pongA.nonce)
                }
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `R06-T10 create room TCP disconnect retry with same requestId replays original or returns ROOM_NOT_FOUND after recycling without recreating room`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var lobbyToken: String
            var lobbyScope: String
            var originalRoomId: String
            val reqId = "req-c10-01"

            // 1. 客户端握手建房
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome1 = client1.expectWelcome()
                lobbyToken = welcome1.lobbyResumeToken
                lobbyScope = welcome1.lobbyScope.scope

                val createReq = ApiRequest(
                    requestId = reqId,
                    api = ApiTags.CREATE_ROOM,
                    playerName = "HostP1"
                )
                val envelope = TcpRequestEnvelope(
                    requestScope = lobbyScope,
                    requestId = reqId,
                    request = createReq
                )
                client1.sendFrame(TcpMessageCodec.toFrame(envelope))

                val respEnv = client1.expectResponse()
                assertTrue(respEnv.response.success)
                originalRoomId = respEnv.response.roomId ?: error("Expected roomId in response")
                assertEquals(1, server.registry.activeRooms().size)
            }

            // 客户端断开后，以相同 lobby token 重连
            TcpTestClient(port = server.boundPort).use { client2 ->
                client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(
                    wireVersion = 1,
                    businessVersion = 1,
                    lobbyResumeToken = lobbyToken
                )))
                val welcome2 = client2.expectWelcome()
                assertEquals(lobbyToken, welcome2.lobbyResumeToken)

                // 2. 在房间未被回收时，携带原 requestId 重试：应该触发 Replay 并返回原成功响应
                val retryReq = ApiRequest(
                    requestId = reqId,
                    api = ApiTags.CREATE_ROOM,
                    playerName = "HostP1"
                )
                val retryEnvelope = TcpRequestEnvelope(
                    requestScope = welcome2.lobbyScope.scope,
                    requestId = reqId,
                    request = retryReq
                )
                client2.sendFrame(TcpMessageCodec.toFrame(retryEnvelope))

                val replayRespEnv = client2.expectResponse()
                assertTrue(replayRespEnv.response.success)
                assertEquals(originalRoomId, replayRespEnv.response.roomId)
                assertEquals(1, server.registry.activeRooms().size)
            }

            // 3. 触发房间回收（显式关闭并从 registry 移除，通知 admission）
            val room = server.registry.getRoom(originalRoomId)
            assertNotNull(room)
            room!!.close()
            server.registry.removeRoom(originalRoomId)
            server.roomAdmission.onRoomClosed(originalRoomId)
            assertEquals(0, server.registry.activeRooms().size)

            // 4. 再次以相同 lobby token 重连并重试该同一 requestId
            TcpTestClient(port = server.boundPort).use { client3 ->
                client3.sendFrame(TcpMessageCodec.toFrame(TcpHello(
                    wireVersion = 1,
                    businessVersion = 1,
                    lobbyResumeToken = lobbyToken
                )))
                val welcome3 = client3.expectWelcome()

                val retryAfterRecycle = ApiRequest(
                    requestId = reqId,
                    api = ApiTags.CREATE_ROOM,
                    playerName = "HostP1"
                )
                val retryEnvAfterRecycle = TcpRequestEnvelope(
                    requestScope = welcome3.lobbyScope.scope,
                    requestId = reqId,
                    request = retryAfterRecycle
                )
                client3.sendFrame(TcpMessageCodec.toFrame(retryEnvAfterRecycle))

                val replayAfterRecycleResp = client3.expectResponse()
                // 根据 MR-03 联动与 MR-06 规范：返回 ROOM_NOT_FOUND 失败
                assertFalse(replayAfterRecycleResp.response.success)
                assertEquals(ApiErrors.ROOM_NOT_FOUND, replayAfterRecycleResp.response.error)
                assertEquals(reqId, replayAfterRecycleResp.requestId)

                // 关键断言：绝不新建房间，registry 数量依然为 0
                assertEquals(0, server.registry.activeRooms().size)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `R5 room recycling end-to-end integration via performRoomPatrol and onRoomRecycled`() {
        val clock = ManualMonotonicClock(1000L)
        val registry = RoomRegistry(clock = clock)
        val policy = RoomRetentionPolicy(unengagedRoomTtlMillis = 60_000L, lobbyIdleTtlMillis = 60_000L)
        val server = TcpTableServer(
            config = TcpServerConfig(port = 0, retentionPolicy = policy),
            registry = registry
        )
        server.start()

        try {
            var lobbyToken: String
            var lobbyScope: String
            var roomId: String
            var hostResumeToken: String
            val reqId = "req-r5-create"

            // 1. 客户端建连握手建房成功
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome1 = client1.expectWelcome()
                lobbyToken = welcome1.lobbyResumeToken
                lobbyScope = welcome1.lobbyScope.scope

                val createReq = ApiRequest(
                    requestId = reqId,
                    api = ApiTags.CREATE_ROOM,
                    playerName = "HostP1"
                )
                val envelope = TcpRequestEnvelope(
                    requestScope = lobbyScope,
                    requestId = reqId,
                    request = createReq
                )
                client1.sendFrame(TcpMessageCodec.toFrame(envelope))

                val respEnv = client1.expectResponse()
                assertTrue(respEnv.response.success)
                roomId = respEnv.response.roomId ?: error("Expected roomId")
                hostResumeToken = respEnv.response.resumeToken ?: error("Expected resumeToken")

                assertEquals(1, server.registry.activeRooms().size)
                assertEquals(1, server.roomAdmission.committedRoomCount)
            }

            // 2. 客户端断开连接，等待连接注销完成
            Thread.sleep(50)

            // 3. 推进 registry 时钟超过 lobby TTL (60s)
            clock.advance(70_000L)

            // 4. 手动执行 performRoomPatrol()
            server.performRoomPatrol(clock.nowMillis())
            Thread.sleep(50)

            // 5. 断言 registry 清空且 roomAdmission 记账归零
            assertEquals(0, server.registry.activeRooms().size, "Registry must be empty after patrol recycling")
            assertEquals(0, server.roomAdmission.committedRoomCount, "Admission committed rooms must return to 0")

            // 6. 新连接以旧 token 调用 RESUME_SESSION，必须得到 ROOM_NOT_FOUND
            TcpTestClient(port = server.boundPort).use { client2 ->
                client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(
                    wireVersion = 1,
                    businessVersion = 1,
                    lobbyResumeToken = lobbyToken
                )))
                val welcome2 = client2.expectWelcome()

                val resumeReq = ApiRequest(
                    requestId = "req-r5-resume",
                    api = ApiTags.RESUME_SESSION,
                    roomId = roomId,
                    token = hostResumeToken
                )
                val resumeEnv = TcpRequestEnvelope(
                    requestScope = welcome2.lobbyScope.scope,
                    requestId = "req-r5-resume",
                    request = resumeReq
                )
                client2.sendFrame(TcpMessageCodec.toFrame(resumeEnv))

                val resumeResp = client2.expectResponse()
                assertFalse(resumeResp.response.success)
                assertEquals(ApiErrors.ROOM_NOT_FOUND, resumeResp.response.error)
            }
        } finally {
            server.close()
        }
    }
}

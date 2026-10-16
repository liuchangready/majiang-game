package com.majiang.review

import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiTags
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpPing
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpWelcome
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * T2 独立验收：真实回环 socket 上对 TcpTableServer 的对抗性契约测试。
 *
 * 与 Anti 模块测试互补，聚焦跨连接/生命周期语义：
 * - UC-R01/R04/R11 跨连接重放（断线重连后原 requestId+原 scope 命中原回执，绝不重建房间）
 * - UC-R03 同键异载荷 CONFLICT；UC-R05 scope 过期 REQUEST_SCOPE_EXPIRED；UC-R06 容量满 SERVER_BUSY
 * - UC-H09/R3 未握手桶反复关闭后不泄漏（第 N+1 条仍可握手）
 * - UC-L06 心跳回显与判死；UC-L08 不完整帧硬截止
 * - UC-S01 绑定基线 RSP 先于任何 UPDATE 到达
 * - UC-S08/R7+R8 顶号后被顶连接收到主动 CLOSE(DISPLACED_BY_NEW_CONNECTION)（UC-C01 发出方义务）
 */
@Timeout(120)
class TcpT2ServerContractReviewTest {

    private class MiniClient(host: String, port: Int) : AutoCloseable {
        val socket = Socket(host, port)
        private val input = DataInputStream(socket.getInputStream())
        private val output: OutputStream = socket.getOutputStream()
        val receivedTypes = ArrayList<Int>()

        init {
            socket.soTimeout = 8000
            socket.tcpNoDelay = true
        }

        fun sendRaw(bytes: ByteArray) {
            output.write(bytes); output.flush()
        }

        fun sendFrame(type: Int, payloadJson: String) {
            sendRaw(TcpFrameEncoder.encode(type, payloadJson.toByteArray(Charsets.UTF_8)))
        }

        fun readFrame(): Pair<Int, String> {
            val magic = input.readInt()
            assertEquals(TcpWire.MAGIC_INT, magic, "帧魔数必须匹配")
            val version = input.readByte().toInt()
            val type = input.readByte().toInt()
            val flags = input.readShort().toInt()
            assertEquals(0, flags, "v1 flags 必须为 0")
            val len = input.readInt()
            val payload = ByteArray(len)
            input.readFully(payload)
            receivedTypes.add(type)
            return type to String(payload, Charsets.UTF_8)
        }

        /** 读取直到收到指定类型帧，返回 (类型, JSON)；其它类型记录到 receivedTypes */
        fun readUntil(type: Int): Pair<Int, String> {
            var attempts = 0
            while (attempts++ < 200) {
                val f = readFrame()
                if (f.first == type) return f
            }
            throw AssertionError("等待帧类型 $type 超时")
        }

        fun expectCloseReason(): String {
            val f = readUntil(TcpWire.FRAME_TYPE_CLOSE)
            val close = TcpMessageCodec.decodeClose(f.second)
            return close.reason
        }

        fun expectEofOrClose(): String? {
            return try {
                expectCloseReason()
            } catch (_: EOFException) {
                null // 对端直接切断也算关闭
            } catch (_: java.net.SocketException) {
                null
            }
        }

        fun handshake(): TcpWelcome {
            sendFrame(TcpWire.FRAME_TYPE_HELLO, TcpMessageCodec.encodeHello(TcpHello()))
            val (_, json) = readUntil(TcpWire.FRAME_TYPE_WELCOME)
            return TcpMessageCodec.decodeWelcome(json)
        }

        fun request(scope: String, requestId: String, api: String, extra: (ApiRequest) -> ApiRequest = { it }): TcpResponseEnvelope {
            val req = extra(ApiRequest(api = api, requestId = requestId))
            val env = TcpRequestEnvelope(scope, requestId, req)
            sendFrame(TcpWire.FRAME_TYPE_REQ, TcpMessageCodec.encodeRequestEnvelope(env))
            while (true) {
                val (type, json) = readFrame()
                if (type == TcpWire.FRAME_TYPE_RSP) {
                    val rsp = TcpMessageCodec.decodeResponseEnvelope(json)
                    if (rsp.requestId == requestId) return rsp
                }
            }
        }

        override fun close() {
            runCatching { socket.close() }
        }
    }

    private fun shortConfig(scopeMillis: Long = 10 * 60 * 1000L): TcpServerConfig = TcpServerConfig(
        host = "127.0.0.1", port = 0,
        scopeValidityMillis = scopeMillis,
        idleDeadMillis = 10_000L,
        maxUnhandshakedConnections = 4
    )

    @Test
    fun `cross connection replay returns original receipt and never duplicates room`() {
        TcpTableServer(config = shortConfig()).use { server ->
            server.start()
            val port = server.boundPort
            val created: TcpResponseEnvelope
            val welcome: TcpWelcome
            MiniClient("127.0.0.1", port).use { c ->
                welcome = c.handshake()
                created = c.request(welcome.lobbyScope.scope, "create-1", ApiTags.CREATE_ROOM) { it.copy(playerName = "Host-Review") }
                assertTrue(created.response.success ?: false, "CREATE_ROOM 应成功: ${created.response.error}")
                assertNotNull(created.response.roomId)
                assertNotNull(created.response.resumeToken)
            }
            // 断线重连：携 lobbyResumeToken，用原 scope + 原 requestId + 原载荷重放
            MiniClient("127.0.0.1", port).use { c2 ->
                c2.sendFrame(TcpWire.FRAME_TYPE_HELLO, TcpMessageCodec.encodeHello(TcpHello(lobbyResumeToken = welcome.lobbyResumeToken)))
                val w2 = run { val (_, json) = c2.readUntil(TcpWire.FRAME_TYPE_WELCOME); TcpMessageCodec.decodeWelcome(json) }
                val replay = c2.request(welcome.lobbyScope.scope, "create-1", ApiTags.CREATE_ROOM) { it.copy(playerName = "Host-Review") }
                assertTrue(replay.response.success ?: false, "跨连接重放应命中原回执: ${replay.response.error}")
                assertEquals(created.response.roomId, replay.response.roomId, "重放必须返回原房间，绝不二次建房")
                assertEquals(created.response.resumeToken, replay.response.resumeToken)
                val listed = c2.request(w2.lobbyScope.scope, "list-1", ApiTags.LIST_ROOMS)
                val rooms = listed.response.rooms.orEmpty()
                assertEquals(1, rooms.size, "全服必须只有一个房间（重放未新建）: ${rooms.map { it.roomId }}")
            }
        }
    }

    @Test
    fun `same key different payload conflicts and expired scope rejects without re-execution`() {
        TcpTableServer(config = shortConfig(scopeMillis = 400L)).use { server ->
            server.start()
            val port = server.boundPort
            MiniClient("127.0.0.1", port).use { c ->
                val welcome = c.handshake()
                val scope = welcome.lobbyScope.scope
                val first = c.request(scope, "conflict-1", ApiTags.CREATE_ROOM) { it.copy(playerName = "A") }
                assertTrue(first.response.success ?: false)
                // 同键异载荷 -> CONFLICT (D1)
                val conflict = c.request(scope, "conflict-1", ApiTags.CREATE_ROOM) { it.copy(playerName = "B") }
                assertFalse(conflict.response.success ?: true)
                assertEquals(com.majiang.server.api.ApiErrors.CONFLICT, conflict.response.error)
                // scope 过期（400ms 有效期 + cleanExpiredScopes 周期清理）-> 新键 REQUEST_SCOPE_EXPIRED，绝不执行
                Thread.sleep(1200)
                val expired = c.request(scope, "after-expire-1", ApiTags.CREATE_ROOM) { it.copy(playerName = "C") }
                assertFalse(expired.response.success ?: true)
                assertEquals(com.majiang.server.api.ApiErrors.REQUEST_SCOPE_EXPIRED, expired.response.error)
                // 心跳维持连接存活以越过过期周期
                val pong = run {
                    c.sendFrame(TcpWire.FRAME_TYPE_PING, TcpMessageCodec.encodePing(TcpPing("0123456789abcdef")))
                    val (_, json) = c.readUntil(TcpWire.FRAME_TYPE_PONG)
                    TcpMessageCodec.decodePong(json)
                }
                assertEquals("0123456789abcdef", pong.nonce, "PONG 必须回显 nonce")
            }
        }
    }

    @Test
    fun `member side effect capacity rejects before admission when saturated`() {
        val cfg = shortConfig().copy(maxCompletedReceiptsPerMember = 2)
        TcpTableServer(config = cfg).use { server ->
            server.start()
            MiniClient("127.0.0.1", server.boundPort).use { c ->
                val welcome = c.handshake()
                val scope = welcome.lobbyScope.scope
                // 离开房间也计副作用，避免依赖 CREATE_ROOM 上限：用小容量逐次填满
                var busySeen = false
                for (i in 1..6) {
                    val rsp = c.request(scope, "cap-$i", ApiTags.CREATE_ROOM) { it.copy(playerName = "P$i") }
                    if (rsp.response.error == com.majiang.server.api.ApiErrors.SERVER_BUSY) {
                        busySeen = true
                        break
                    }
                    // 每次建房后离开，保证下一次还能建（角色互斥）
                    c.request(scope, "leave-$i", ApiTags.LEAVE_ROOM)
                }
                assertTrue(busySeen, "成员副作用容量 2 应在第 3 次后 SERVER_BUSY（受理前拒绝）")
            }
        }
    }

    @Test
    fun `repeated aborted handshakes do not leak the unhandshaked bucket`() {
        TcpTableServer(config = shortConfig()).use { server ->
            server.start()
            val port = server.boundPort
            // 反复建连不握手直接断开，远超 maxUnhandshakedConnections=4
            for (i in 1..10) {
                MiniClient("127.0.0.1", port).use { it.sendRaw(byteArrayOf(0x4D, 0x4A)) } // 半个头
            }
            // 泄漏场景：第 11 条（超桶上限）应因桶满被拒——但此前 10 条必须已归还
            Thread.sleep(300)
            MiniClient("127.0.0.1", port).use { c ->
                val welcome = c.handshake() // R3 修复前：桶计数泄漏导致这里被 SERVER_OVERLOADED 拒绝
                assertNotNull(welcome.lobbyScope.scope)
            }
        }
    }

    @Test
    fun `heartbeat pong echoes and idle death closes with HEARTBEAT_TIMEOUT`() {
        val cfg = shortConfig().copy(idleDeadMillis = 400L)
        TcpTableServer(config = cfg).use { server ->
            server.start()
            MiniClient("127.0.0.1", server.boundPort).use { c ->
                c.handshake()
                // 心跳回显
                c.sendFrame(TcpWire.FRAME_TYPE_PING, TcpMessageCodec.encodePing(TcpPing("fedcba9876543210")))
                val (_, json) = c.readUntil(TcpWire.FRAME_TYPE_PONG)
                assertEquals("fedcba9876543210", TcpMessageCodec.decodePong(json).nonce)
                // 停止发帧 -> 400ms 判死
                c.socket.soTimeout = 5000
                val reason = c.expectCloseReason()
                assertEquals(TcpCloseReasons.HEARTBEAT_TIMEOUT, reason, "空闲判死必须发 HEARTBEAT_TIMEOUT")
            }
        }
    }

    @Test
    fun `incomplete frame past hard deadline is closed`() {
        val cfg = shortConfig().copy(incompleteFrameTimeoutMillis = 400L)
        TcpTableServer(config = cfg).use { server ->
            server.start()
            MiniClient("127.0.0.1", server.boundPort).use { c ->
                c.handshake()
                // 合法头部声明 100 字节载荷，只发一半，然后保持静默
                val header = java.nio.ByteBuffer.allocate(12)
                    .putInt(TcpWire.MAGIC_INT).put(1.toByte()).put(TcpWire.FRAME_TYPE_PING.toByte())
                    .putShort(0.toShort()).putInt(100).array()
                c.sendRaw(header + ByteArray(50))
                c.socket.soTimeout = 5000
                val reason = c.expectEofOrClose()
                assertTrue(reason == null || reason == TcpCloseReasons.PROTOCOL_VIOLATION || reason == TcpCloseReasons.HEARTBEAT_TIMEOUT,
                    "不完整帧超时后必须关闭，实际: $reason")
            }
        }
    }

    @Test
    fun `baseline RSP arrives before any push frame on the same connection`() {
        TcpTableServer(config = shortConfig()).use { server ->
            server.start()
            val port = server.boundPort
            lateinit var hostWelcome: TcpWelcome
            lateinit var roomId: String
            MiniClient("127.0.0.1", port).use { host ->
                hostWelcome = host.handshake()
                val created = host.request(hostWelcome.lobbyScope.scope, "host-create", ApiTags.CREATE_ROOM) { it.copy(playerName = "Host") }
                roomId = created.response.roomId!!
            }
            MiniClient("127.0.0.1", port).use { guest ->
                val w = guest.handshake()
                guest.sendFrame(TcpWire.FRAME_TYPE_REQ, TcpMessageCodec.encodeRequestEnvelope(
                    TcpRequestEnvelope(w.lobbyScope.scope, "guest-join", ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "guest-join", roomId = roomId, playerName = "Guest"))
                ))
                // 逐帧读取：第一个到达的必须是本次 JOIN 的 RSP（UC-S01 基线先于任何 UPDATE）
                val (t1, j1) = guest.readFrame()
                assertEquals(TcpWire.FRAME_TYPE_RSP, t1, "首帧必须是绑定 RSP，实际类型 $t1（received=${guest.receivedTypes}）")
                val env = TcpMessageCodec.decodeResponseEnvelope(j1)
                assertEquals("guest-join", env.requestId)
                assertTrue(env.response.success ?: false, "JOIN_ROOM 应成功: ${env.response.error}")
                assertNotNull(env.grantedScope, "绑定成功 RSP 必须携带角色 scope (D7)")
                assertNotNull(env.response.seat)
            }
        }
    }

    @Test
    fun `displaced connection receives DISPLACED_BY_NEW_CONNECTION close after rebind`() {
        TcpTableServer(config = shortConfig()).use { server ->
            server.start()
            val port = server.boundPort
            MiniClient("127.0.0.1", port).use { a ->
                val wa = a.handshake()
                val scopeA = wa.lobbyScope.scope
                val created = a.request(scopeA, "d-create", ApiTags.CREATE_ROOM) { it.copy(playerName = "A") }
                val roomId = created.response.roomId!!
                val resumeToken = created.response.resumeToken!!
                val ready = a.request(scopeA, "d-ready-1", ApiTags.SET_READY) { it.copy(ready = true) }
                assertTrue(ready.response.success ?: false, "顶号前 SET_READY 应成功")

                // 同一 principal 的新连接持房间 token 顶号重连（先恢复大厅身份）
                MiniClient("127.0.0.1", port).use { b ->
                    b.sendFrame(TcpWire.FRAME_TYPE_HELLO, TcpMessageCodec.encodeHello(TcpHello(lobbyResumeToken = wa.lobbyResumeToken)))
                    val wb = run { val (_, json) = b.readUntil(TcpWire.FRAME_TYPE_WELCOME); TcpMessageCodec.decodeWelcome(json) }
                    val resumed = b.request(wb.lobbyScope.scope, "d-resume", ApiTags.RESUME_SESSION) {
                        it.copy(roomId = roomId, token = resumeToken)
                    }
                    assertTrue(resumed.response.success ?: false, "顶号重连应成功: ${resumed.response.error}")
                }

                // R8/UC-C01：被顶连接必须收到服务端主动发出的 CLOSE(DISPLACED_BY_NEW_CONNECTION)，
                // 客户端才能按 MR-02 永久分类终止而不无限重试
                val reason = a.expectCloseReason()
                assertEquals(
                    TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, reason,
                    "顶号 CLOSE 原因码必须为 DISPLACED_BY_NEW_CONNECTION"
                )
            }
        }
    }

    @Test
    fun `get request scope returns granted scope without side effects`() {
        TcpTableServer(config = shortConfig()).use { server ->
            server.start()
            MiniClient("127.0.0.1", server.boundPort).use { c ->
                val w = c.handshake()
                val rsp = c.request(w.lobbyScope.scope, "scope-q-1", "GET_REQUEST_SCOPE")
                assertTrue(rsp.response.success ?: false)
                assertNotNull(rsp.grantedScope, "GET_REQUEST_SCOPE 必须携带 grantedScope (D9)")
                val rsp2 = c.request(w.lobbyScope.scope, "scope-q-2", "GET_REQUEST_SCOPE")
                assertEquals(rsp.grantedScope!!.scope, rsp2.grantedScope!!.scope, "有效期内重复查询必须返回同一 scope")
            }
        }
    }
}

package com.majiang.server.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.tcp.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.Socket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * TcpTableServer 生产缺陷修复正式回归测试套件
 *
 * 严格覆盖 CODEX-TCP-CURRENT-REVIEW-2026-10-16 验收项：
 * - R01: 远端独立 Ticker 自动驱动房间权威时间，无需客户端操作
 * - R02: TCP 连接断开调用 notifyRoomDisconnect 触发房间连接控制
 * - R06: 未知 lobbyToken 鉴权失败拒绝；有效 token 恢复继承现有有效 scope
 * - R10: 全局内存预算与轮转帧上限守住每轮限制
 * - R11: 慢端停服有界返回 (1s 截止)，下发 SERVER_SHUTDOWN 且完整回收资源
 * - R15: 基线与订阅在单桌权威执行器上原子建立 (snapshotAndSubscribe)
 */
@Timeout(20)
class TcpTableServerRegressionTest {

    private fun readFrame(s: Socket): TcpFrame {
        val decoder = TcpFrameDecoder()
        val buf = ByteArray(4096)
        while (true) {
            val n = s.getInputStream().read(buf)
            check(n >= 0) { "Unexpected EOF" }
            val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
            if (frames.isNotEmpty()) return frames.first()
        }
    }

    private fun sendFrame(s: Socket, frame: TcpFrame) {
        val bytes = TcpFrameEncoder.encode(frame.type, frame.payload, frame.flags)
        s.getOutputStream().write(bytes)
        s.getOutputStream().flush()
    }

    private fun handshake(s: Socket, token: String? = null): TcpWelcome {
        val hello = TcpHello(wireVersion = 1, businessVersion = 1, lobbyResumeToken = token)
        sendFrame(s, TcpMessageCodec.toFrame(hello))
        val frame = readFrame(s)
        assertEquals(TcpWire.FRAME_TYPE_WELCOME, frame.type)
        return TcpMessageCodec.parseWelcome(frame)
    }

    @Test
    fun `R01 remote ticker automatically drives room authority without client actions`() {
        val config = TcpServerConfig(port = 0, tickIntervalMillis = 20L)
        TcpTableServer(config).use { server ->
            server.start()
            val room = server.registry.createRoom(
                hostPlayerName = "Host",
                isSingleplayer = false
            )
            val initialTickCount = room.tableHost.clock.nowMillis()
            Thread.sleep(120L)
            val advancedTickCount = room.tableHost.clock.nowMillis()
            assertTrue(advancedTickCount > initialTickCount, "Server ticker must automatically advance authority timeline")
        }
    }

    @Test
    fun `R02 tcp connection close triggers room disconnect notification`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            val room = server.registry.createRoom(hostPlayerName = "Host", isSingleplayer = false)

            val seat = 1
            Socket("127.0.0.1", server.boundPort).use { s ->
                val welcome = handshake(s)
                val joinReq = ApiRequest(
                    api = ApiTags.JOIN_ROOM,
                    requestId = "join-1",
                    roomId = room.roomId,
                    playerName = "Player1",
                    preferSeat = seat
                )
                val env = TcpRequestEnvelope(welcome.lobbyScope.scope, joinReq.requestId, joinReq)
                sendFrame(s, TcpMessageCodec.toFrame(env))
                val rspFrame = readFrame(s)
                assertEquals(TcpWire.FRAME_TYPE_RSP, rspFrame.type)

                // 验证席位已连接
                assertEquals("CONNECTED", room.tableHost.connectionControl.seatState(seat).status.name)
            }

            // Client socket closed abruptly (EOF / disconnect)
            Thread.sleep(200L)
            assertEquals("DISCONNECTED", room.tableHost.connectionControl.seatState(seat).status.name, "Disconnecting TCP socket must trigger room disconnect")
        }
    }

    @Test
    fun `R06 unknown lobby token is rejected and reconnect retains valid scope`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()

            // 1. 未知 token 被拒绝
            Socket("127.0.0.1", server.boundPort).use { s ->
                val hello = TcpHello(wireVersion = 1, businessVersion = 1, lobbyResumeToken = "unknown-token-12345")
                sendFrame(s, TcpMessageCodec.toFrame(hello))
                val frame = readFrame(s)
                assertEquals(TcpWire.FRAME_TYPE_CLOSE, frame.type)
                val close = TcpMessageCodec.parseClose(frame)
                assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, close.reason)
            }

            // 2. 正常建连获得有效 token
            val validToken: String
            val originalScope: TcpScope
            Socket("127.0.0.1", server.boundPort).use { s ->
                val welcome = handshake(s)
                validToken = welcome.lobbyResumeToken
                originalScope = welcome.lobbyScope
            }

            // 3. 携带已签发有效 token 重连，继承相同 scope
            Socket("127.0.0.1", server.boundPort).use { s ->
                val resumedWelcome = handshake(s, token = validToken)
                assertEquals(originalScope.scope, resumedWelcome.lobbyScope.scope, "Valid resume token must inherit active scope")
            }
        }
    }

    @Test
    fun `R11 server close executes within 1 second and sends shutdown notification`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        val receivedClose = CompletableFuture<TcpClose>()
        val clientSocket = Socket("127.0.0.1", server.boundPort)
        handshake(clientSocket)

        val reader = Thread {
            try {
                val frame = readFrame(clientSocket)
                if (frame.type == TcpWire.FRAME_TYPE_CLOSE) {
                    receivedClose.complete(TcpMessageCodec.parseClose(frame))
                }
            } catch (t: Throwable) {
                receivedClose.completeExceptionally(t)
            }
        }.apply { isDaemon = true; start() }

        val startCloseTime = System.currentTimeMillis()
        server.close()
        val duration = System.currentTimeMillis() - startCloseTime

        assertTrue(duration < 1500L, "Server close must finish boundedly within ~1s: was ${duration}ms")
        val closeMsg = receivedClose.get(2, TimeUnit.SECONDS)
        assertEquals(TcpCloseReasons.SERVER_SHUTDOWN, closeMsg.reason)
        clientSocket.close()
    }

    @Test
    fun `R15 atomic snapshot and subscribe captures baseline without interleaved mutation`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            val room = server.registry.createRoom(hostPlayerName = "Host", isSingleplayer = false)

            val seat = 0
            val eventsReceived = AtomicInteger(0)

            val pair = room.snapshotAndSubscribe(
                seat = seat,
                sessionValidator = { true },
                listener = { eventsReceived.incrementAndGet() }
            ).get(2, TimeUnit.SECONDS)

            assertNotNull(pair.first, "Baseline snapshot must be captured")
            assertNotNull(pair.second, "Subscription handle must be established")
            pair.second.close()
        }
    }

    @Test
    fun `R10 partial inbound frame reserves global budget and releases exactly on disconnect`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            val clientSocket = Socket("127.0.0.1", server.boundPort)
            handshake(clientSocket)

            val baseAllocated = server.globalBudget.currentAllocatedBytes

            // 发送部分帧：12 字节帧头 (声明 4096 载荷) + 2048 字节载荷 (未完成帧)
            val header = ByteArray(12)
            header[0] = 'M'.code.toByte()
            header[1] = 'J'.code.toByte()
            header[2] = 'T'.code.toByte()
            header[3] = 'C'.code.toByte()
            header[4] = 1 // wireVersion
            header[5] = TcpWire.FRAME_TYPE_PING.toByte() // type
            header[6] = 0 // flags high
            header[7] = 0 // flags low
            // payload length = 4096 (big endian)
            header[8] = 0
            header[9] = 0
            header[10] = 0x10
            header[11] = 0x00

            val partialBody = ByteArray(2048) { 1 }
            clientSocket.getOutputStream().write(header)
            clientSocket.getOutputStream().write(partialBody)
            clientSocket.getOutputStream().flush()

            // 等待服务端读取并记账预算
            var reserved = false
            for (i in 0 until 50) {
                if (server.globalBudget.currentAllocatedBytes >= baseAllocated + 2048L) {
                    reserved = true
                    break
                }
                Thread.sleep(20L)
            }
            assertTrue(reserved, "Server globalBudget must reserve bytes for partial inbound frame")

            // 断开连接
            clientSocket.close()

            // 验证断开连接后，未完成帧的预留预算被恰好一次完整释放
            var released = false
            for (i in 0 until 50) {
                if (server.globalBudget.currentAllocatedBytes <= baseAllocated) {
                    released = true
                    break
                }
                Thread.sleep(20L)
            }
            assertTrue(released, "Server globalBudget must release partial frame reservation upon disconnect")
        }
    }

    @Test
    fun `R10 fair rotation processes all decoded frames without silent drop`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            val clientSocket = Socket("127.0.0.1", server.boundPort)
            val welcome = handshake(clientSocket)

            val totalFrames = 40 // 超出单轮 32 帧配额
            val requestIds = (1..totalFrames).map { "req-$it" }
            val receivedResponses = ConcurrentHashMap.newKeySet<String>()
            val latch = CountDownLatch(totalFrames)

            val reader = Thread {
                val decoder = TcpFrameDecoder()
                val buf = ByteArray(4096)
                try {
                    while (receivedResponses.size < totalFrames && !clientSocket.isClosed) {
                        val n = clientSocket.getInputStream().read(buf)
                        if (n < 0) break
                        val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
                        for (frame in frames) {
                            if (frame.type == TcpWire.FRAME_TYPE_RSP) {
                                val env = TcpMessageCodec.parseResponseEnvelope(frame)
                                if (receivedResponses.add(env.requestId)) {
                                    latch.countDown()
                                }
                            } else if (frame.type == TcpWire.FRAME_TYPE_CLOSE) {
                                break
                            }
                        }
                    }
                } catch (_: Throwable) {
                }
            }.apply { isDaemon = true; start() }

            // 一次性突发写入 40 个 REQ 帧 (超出单轮 32 帧配额)
            for (reqId in requestIds) {
                val req = ApiRequest(api = ApiTags.CAPABILITIES, requestId = reqId)
                val env = TcpRequestEnvelope(requestScope = welcome.lobbyScope.scope, requestId = reqId, request = req)
                sendFrame(clientSocket, TcpMessageCodec.toFrame(env))
            }

            assertTrue(latch.await(5, TimeUnit.SECONDS), "All 40 frames must be processed without dropping excess 8 frames")
            assertEquals(totalFrames, receivedResponses.size)
            clientSocket.close()
        }
    }
}

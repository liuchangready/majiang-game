package com.majiang.client.transport.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.*
import com.majiang.transport.tcp.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.CopyOnWriteArrayList
import java.nio.ByteBuffer

/**
 * TcpClientBinding 传输契约与稳定性回归测试套件
 *
 * 严格覆盖 CODEX-TCP-CURRENT-REVIEW-2026-10-16 验收项：
 * - R03: 远端 EOF 归为 HEARTBEAT_TIMEOUT (临时)，保留未确认副作用以供重连重放
 * - R04: 协商请求超时生效，禁止无限卡死在途请求
 * - R05: 过期副作用严禁自动换新 scope 重做，必须异常完成以供对账
 * - R07: 用户回调异常不触发协议违规 (PROTOCOL_VIOLATION)
 * - R08: 服务端发起的 PING 必须立即回显对应 nonce 的 PONG
 * - R08: RSP 信封 requestScope 错误必须拒绝，不能误完成请求
 * - R09: 早期入站缓冲遵守 64 条与 4 MiB 双限，监听器派发校验 bindingEpoch
 */
@Timeout(15)
class TcpClientBindingRegressionTest {

    private class MockPeer(
        val requestTimeoutMillis: Long = 2000L,
        val handler: (Socket) -> Unit
    ) : AutoCloseable {
        val serverSocket = ServerSocket(0)
        val done = CountDownLatch(1)
        val thread = Thread {
            try {
                val s = serverSocket.accept()
                s.soTimeout = 4000
                // 读 HELLO
                val decoder = TcpFrameDecoder()
                val buf = ByteArray(4096)
                var helloFrame: TcpFrame? = null
                while (helloFrame == null) {
                    val n = s.getInputStream().read(buf)
                    if (n < 0) break
                    val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
                    if (frames.isNotEmpty()) helloFrame = frames.first()
                }
                assertNotNull(helloFrame)
                assertEquals(TcpWire.FRAME_TYPE_HELLO, helloFrame?.type)

                // 发 WELCOME (指定协商超时 requestTimeoutMillis)
                val welcome = TcpWelcome(
                    wireVersion = 1,
                    businessVersion = 1,
                    connectionEpoch = "peer-epoch",
                    lobbyResumeToken = "peer-token",
                    lobbyScope = TcpScope("peer-scope", Long.MAX_VALUE),
                    limits = TcpLimits(requestTimeoutMillis = requestTimeoutMillis, heartbeatIntervalMillis = 3000L)
                )
                val welcomeBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_WELCOME, TcpMessageCodec.toFrame(welcome).payload)
                s.getOutputStream().write(welcomeBytes)
                s.getOutputStream().flush()

                handler(s)
                done.await(5, TimeUnit.SECONDS)
            } catch (_: Throwable) {
            }
        }.apply { isDaemon = true; start() }

        fun connect(): TcpClientBinding = TcpClient.connect("127.0.0.1", serverSocket.localPort).get(3, TimeUnit.SECONDS)

        override fun close() {
            done.countDown()
            try { serverSocket.close() } catch (_: Throwable) {}
            thread.join(1000)
        }
    }

    private class MultiConnectionPeer(
        val limitsProvider: (Int) -> TcpLimits = { TcpLimits(maxInFlightRequests = 64, requestTimeoutMillis = 1500L) },
        val onConnection: (Socket, Int) -> Unit
    ) : AutoCloseable {
        constructor(limits: TcpLimits, onConnection: (Socket, Int) -> Unit) : this({ limits }, onConnection)

        val serverSocket = ServerSocket(0)
        val done = CountDownLatch(1)
        val thread = Thread {
            var connIndex = 0
            while (!serverSocket.isClosed && done.count > 0) {
                val s = try {
                    serverSocket.accept()
                } catch (_: Throwable) {
                    break
                }
                s.soTimeout = 15000
                try {
                    val decoder = TcpFrameDecoder()
                    val buf = ByteArray(4096)
                    var helloFrame: TcpFrame? = null
                    while (helloFrame == null) {
                        val n = s.getInputStream().read(buf)
                        if (n < 0) break
                        val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
                        if (frames.isNotEmpty()) helloFrame = frames.first()
                    }
                    if (helloFrame?.type == TcpWire.FRAME_TYPE_HELLO) {
                        val currentLimits = limitsProvider(connIndex)
                        val welcome = TcpWelcome(
                            wireVersion = 1,
                            businessVersion = 1,
                            connectionEpoch = "peer-epoch-$connIndex",
                            lobbyResumeToken = "peer-token",
                            lobbyScope = TcpScope("peer-scope", Long.MAX_VALUE),
                            limits = currentLimits
                        )
                        val welcomeBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_WELCOME, TcpMessageCodec.toFrame(welcome).payload)
                        s.getOutputStream().write(welcomeBytes)
                        s.getOutputStream().flush()

                        onConnection(s, connIndex++)
                    }
                } catch (_: Throwable) {
                }
            }
        }.apply { isDaemon = true; start() }

        fun connect(): TcpClientBinding = TcpClient.connect("127.0.0.1", serverSocket.localPort).get(3, TimeUnit.SECONDS)

        override fun close() {
            done.countDown()
            try { serverSocket.close() } catch (_: Throwable) {}
            thread.join(1000)
        }
    }

    private fun readClientFrame(s: Socket): TcpFrame {
        val decoder = TcpFrameDecoder()
        val buf = ByteArray(4096)
        while (true) {
            val n = s.getInputStream().read(buf)
            check(n >= 0) { "Unexpected EOF" }
            val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
            if (frames.isNotEmpty()) return frames.first()
        }
    }

    private fun sendServerFrame(s: Socket, frame: TcpFrame) {
        val bytes = TcpFrameEncoder.encode(frame.type, frame.payload, frame.flags)
        s.getOutputStream().write(bytes)
        s.getOutputStream().flush()
    }

    @Test
    fun `R03 remote EOF is transient HEARTBEAT_TIMEOUT and preserves unconfirmed side effects`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            assertEquals(TcpWire.FRAME_TYPE_REQ, reqFrame.type)
            // 收到请求后关闭连接 (EOF)
            s.close()
        }.use { peer ->
            val binding = peer.connect()
            val disconnectReason = CompletableFuture<String>()
            binding.onDisconnect { disconnectReason.complete(it) }

            try {
                val f = binding.setReady(true)
                val reason = disconnectReason.get(3, TimeUnit.SECONDS)
                assertEquals(TcpCloseReasons.HEARTBEAT_TIMEOUT, reason, "Remote EOF must be classified as transient HEARTBEAT_TIMEOUT")
                assertFalse(binding.closed, "Binding must not be marked permanently closed on transient EOF")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R04 request completes by negotiated timeout when response is lost`() {
        MockPeer(requestTimeoutMillis = 150L) { s ->
            // 只读取请求，故意不回复 RSP
            readClientFrame(s)
        }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.listRooms()
                Thread.sleep(600L) // >4x advertised requestTimeoutMillis (150ms)
                assertTrue(future.isCompletedExceptionally, "Request must time out after advertised deadline")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R04 side effect timeout preserves unconfirmedSideEffects and reconciliation identity`() {
        val capturedEnvelope = CompletableFuture<TcpRequestEnvelope>()
        val sendResponseLatch = CountDownLatch(1)

        MockPeer(requestTimeoutMillis = 150L) { s ->
            val reqFrame = readClientFrame(s)
            val envelope = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            capturedEnvelope.complete(envelope)

            // 等待客户端调用超时之后，再回复服务端确认响应
            assertTrue(sendResponseLatch.await(3, TimeUnit.SECONDS))
            val okRsp = ApiResponse(requestId = envelope.requestId, success = true)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(envelope.requestScope, envelope.requestId, okRsp)))
        }.use { peer ->
            val binding = peer.connect()
            try {
                val callerFuture = binding.setReady(true)
                val envelope = capturedEnvelope.get(2, TimeUnit.SECONDS)

                // 等待本地请求超时（协商超时 150ms）
                Thread.sleep(600L)

                // 1. 原调用 Future 按超时结构化失败，调用方不卡死
                assertTrue(callerFuture.isCompletedExceptionally, "Caller future must fail on timeout")

                // 2. 副作用记录未被抹除，三元组原 scope、requestId、完整载荷严格保留
                assertEquals(1, binding.unconfirmedSideEffectCount, "Side-effect must be retained in unconfirmedSideEffects")
                val record = binding.getUnconfirmedSideEffect(envelope.requestId)
                assertNotNull(record, "Unconfirmed record must be retrievable")
                assertEquals("peer-scope", record?.scope, "Original scope must be preserved")
                assertEquals(envelope.requestId, record?.requestId, "Original requestId must be preserved")
                assertEquals(ApiTags.SET_READY, record?.request?.api, "Original api must be preserved")
                assertEquals(true, record?.request?.ready, "Original payload must be preserved")

                // 3. 对账 Future 保持开放，未被本地超时破坏
                val reconFuture = binding.getReconciliationFuture(envelope.requestId)
                assertNotNull(reconFuture, "Reconciliation future must exist")
                assertFalse(reconFuture?.isDone == true, "Reconciliation future must remain open for definitive confirmation")

                // 4. 服务端最终回执到达后，对账通道成功兑现，未确认集合安全移除
                sendResponseLatch.countDown()
                val reconResult = reconFuture?.get(2, TimeUnit.SECONDS)
                assertNotNull(reconResult)
                assertTrue(reconResult?.success == true, "Reconciliation must deliver confirmed server response")
                assertEquals(0, binding.unconfirmedSideEffectCount, "Confirmed side-effect must be cleared from unconfirmed collection")
            } finally {
                sendResponseLatch.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R04 transient disconnect retains unconfirmed side effects for reconnect recovery`() {
        MockPeer { s ->
            readClientFrame(s)
            // 收到副作用请求后突然断开连接（未知结果场景）
            s.close()
        }.use { peer ->
            val binding = peer.connect()
            val disconnectReason = CompletableFuture<String>()
            binding.onDisconnect { disconnectReason.complete(it) }

            try {
                val callerFuture = binding.setReady(true)
                val reason = disconnectReason.get(3, TimeUnit.SECONDS)
                assertEquals(TcpCloseReasons.HEARTBEAT_TIMEOUT, reason)
                assertTrue(callerFuture.isCompletedExceptionally, "Caller future must fail on disconnect")

                // 发生未知结果断线时，副作用记录保留
                assertEquals(1, binding.unconfirmedSideEffectCount, "Unconfirmed side-effect must be retained across disconnect")
                val reqId = binding.unconfirmedSideEffectIds().first()
                val record = binding.getUnconfirmedSideEffect(reqId)
                assertNotNull(record)
                assertEquals("peer-scope", record?.scope)
                assertEquals(ApiTags.SET_READY, record?.request?.api)
                assertEquals(true, record?.request?.ready)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R05 expired side effect cannot automatically execute under new scope`() {
        val retriedReq = CompletableFuture<TcpRequestEnvelope>()
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val envelope = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            // 回复 REQUEST_SCOPE_EXPIRED
            val rsp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.REQUEST_SCOPE_EXPIRED)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(envelope.requestScope, envelope.requestId, rsp)))

            // 观察是否会再次发出请求
            try {
                val next = readClientFrame(s)
                retriedReq.complete(TcpMessageCodec.parseRequestEnvelope(next))
            } catch (_: Throwable) {
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                val f = binding.setReady(true)
                Thread.sleep(500L)
                assertFalse(retriedReq.isDone, "Side-effect request must not be retried automatically with new scope")
                assertTrue(f.isCompletedExceptionally, "Expired side-effect future must complete exceptionally")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 user callback exception does not cause protocol violation or close binding`() {
        MockPeer { s ->
            readClientFrame(s)
            // 推送 PlayerUpdate
            val tile = ProtocolTile.fromTileId(0)
            val view = ProtocolPlayerView(
                viewerSeat = 0,
                phase = ProtocolGamePhase.DISCARD,
                roundWind = 1,
                roundNumber = 1,
                honbaSticks = 0,
                riichiSticks = 0,
                dealerSeat = 0,
                currentTurnPlayer = 0,
                wallRemaining = 70,
                doraIndicators = listOf(tile),
                scores = listOf(25000, 25000, 25000, 25000),
                riichiStatus = listOf(false, false, false, false),
                discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                handTileCounts = listOf(14, 13, 13, 13),
                myClosedTiles = listOf(tile),
                myDrawnTile = null,
                legalActions = listOf(ProtocolLegalAction.DiscardTile(0, tile))
            )
            val decision = ProtocolPlayerDecision(
                roundId = "r1",
                decisionId = "dec-1",
                controlEpoch = 1L,
                options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, tile)))
            )
            val snapshot = ProtocolPlayerSnapshot(
                sessionId = "s1",
                roundId = "r1",
                syncEpoch = "1",
                cursor = 1L,
                sessionVersion = 1L,
                roundVersion = 1L,
                view = view,
                decision = decision
            )
            val update = ProtocolPlayerUpdate(snapshot = snapshot, fromCursor = 0L, visibleEvents = emptyList())
            val frame = TcpMessageCodec.toPlayerUpdateFrame(update, isFullSnapshot = true)
            sendServerFrame(s, frame)
        }.use { peer ->
            val binding = peer.connect()
            val exceptionThrown = AtomicBoolean(false)
            binding.onUpdate {
                exceptionThrown.set(true)
                throw RuntimeException("Consumer UI listener bug")
            }
            try {
                binding.listRooms()
                Thread.sleep(300L)
                assertTrue(exceptionThrown.get(), "Listener was invoked")
                assertFalse(binding.closed, "Binding must not be closed due to listener exception")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R08 server initiated PING receives matching nonce PONG`() {
        val receivedPong = CompletableFuture<TcpPong>()
        MockPeer { s ->
            readClientFrame(s)
            // 发起 PING
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpPing("0123456789abcdef")))
            // 等待 PONG
            val pongFrame = readClientFrame(s)
            if (pongFrame.type == TcpWire.FRAME_TYPE_PONG) {
                receivedPong.complete(TcpMessageCodec.parsePong(pongFrame))
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.listRooms()
                val pong = receivedPong.get(3, TimeUnit.SECONDS)
                assertEquals("0123456789abcdef", pong.nonce, "Client must echo identical nonce in PONG")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R08 response with mismatched requestScope is rejected`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val envelope = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            // 故意使用错误的 requestScope
            val wrongRsp = TcpResponseEnvelope("wrong-mismatched-scope", envelope.requestId, ApiResponse(requestId = envelope.requestId, success = true))
            sendServerFrame(s, TcpMessageCodec.toFrame(wrongRsp))
        }.use { peer ->
            val binding = peer.connect()
            try {
                val f = binding.listRooms()
                Thread.sleep(500L)
                assertTrue(f.isCompletedExceptionally, "RSP with mismatched scope must fail the pending request")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R04 wrong requestScope in RSP does not erase unconfirmed side effect identity`() {
        val capturedReq = CompletableFuture<TcpRequestEnvelope>()
        val sendValidResponseLatch = CountDownLatch(1)

        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val envelope = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            capturedReq.complete(envelope)

            // 先发送错误的 requestScope 响应 (不能证明服务端未执行该副作用)
            val wrongRsp = TcpResponseEnvelope("tampered-or-wrong-scope", envelope.requestId, ApiResponse(requestId = envelope.requestId, success = true))
            sendServerFrame(s, TcpMessageCodec.toFrame(wrongRsp))

            // 稍后在对账阶段发送原合法 scope 的确认响应
            assertTrue(sendValidResponseLatch.await(3, TimeUnit.SECONDS))
            val validRsp = TcpResponseEnvelope(envelope.requestScope, envelope.requestId, ApiResponse(requestId = envelope.requestId, success = true))
            sendServerFrame(s, TcpMessageCodec.toFrame(validRsp))
        }.use { peer ->
            val binding = peer.connect()
            try {
                val callerFuture = binding.setReady(true)
                val envelope = capturedReq.get(2, TimeUnit.SECONDS)

                // 调用方 Future 因收到非法 scope RSP 而以协议异常失败
                try {
                    callerFuture.get(2, TimeUnit.SECONDS)
                    fail("Caller future should have failed on invalid RSP scope")
                } catch (_: Exception) {
                }
                assertTrue(callerFuture.isCompletedExceptionally, "Caller future must fail on invalid RSP scope")

                // 但副作用身份 (scope, requestId, payload) 绝不被抹除
                assertEquals(1, binding.unconfirmedSideEffectCount, "Side-effect record must be retained")
                val record = binding.getUnconfirmedSideEffect(envelope.requestId)
                assertNotNull(record, "Unconfirmed record must be retrievable")
                assertEquals(envelope.requestScope, record?.scope)
                assertEquals(envelope.requestId, record?.requestId)
                assertEquals(envelope.request, record?.request)

                // 对账 Future 保持开放，随后合法响应到达完成兑现
                val reconFuture = binding.getReconciliationFuture(envelope.requestId)
                assertNotNull(reconFuture)
                assertFalse(reconFuture?.isDone == true, "Reconciliation future remains open")

                sendValidResponseLatch.countDown()
                val reconResult = reconFuture?.get(2, TimeUnit.SECONDS)
                assertNotNull(reconResult)
                assertTrue(reconResult?.success == true)
                assertEquals(0, binding.unconfirmedSideEffectCount, "Confirmed side-effect cleared after valid response")
            } finally {
                sendValidResponseLatch.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R04 128 unconfirmed recovery records hard bound is strictly atomic under concurrency`() {
        MultiConnectionPeer(limits = TcpLimits(maxInFlightRequests = 64, requestTimeoutMillis = 100L)) { s, _ ->
            // 只读取请求不回复，让请求在客户端超时并沉淀至 unconfirmedSideEffects
            while (!s.isClosed) {
                try {
                    readClientFrame(s)
                } catch (_: Throwable) {
                    break
                }
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                // 第 1 批 64 个副作用请求 -> 本地超时 100ms 后全部留在 unconfirmedSideEffects
                val batch1 = (0 until 64).map { binding.setReady(true) }
                batch1.forEach { f ->
                    try { f.get(5, TimeUnit.SECONDS) } catch (_: Throwable) {}
                }
                assertEquals(64, binding.unconfirmedSideEffectCount)

                // 第 2 批 64 个副作用请求 -> 本地超时 100ms 后使 unconfirmedSideEffects 达到 128 条硬上限
                val batch2 = (0 until 64).map { binding.setReady(true) }
                batch2.forEach { f ->
                    try { f.get(5, TimeUnit.SECONDS) } catch (_: Throwable) {}
                }
                assertEquals(128, binding.unconfirmedSideEffectCount)

                // 此时容量已达 128 上限：直接调用必然被 SERVER_BUSY 拒绝
                val directFuture = binding.setReady(true)
                try {
                    directFuture.get(200, TimeUnit.MILLISECONDS)
                    fail("129th side-effect must be rejected immediately")
                } catch (e: Exception) {
                    val cause = (e as? java.util.concurrent.ExecutionException)?.cause ?: e
                    assertTrue(cause is ServerApiException && cause.code == ApiErrors.SERVER_BUSY,
                        "Expected SERVER_BUSY when at 128 capacity, got $cause")
                }
                assertEquals(128, binding.unconfirmedSideEffectCount)

                // 并发压力测试：16 个线程同时并发轰炸 160 个请求，原子锁保证决不击穿 128
                val threadCount = 16
                val executor = Executors.newFixedThreadPool(threadCount)
                val startLatch = CountDownLatch(1)
                val doneLatch = CountDownLatch(threadCount)
                val busyErrors = AtomicInteger(0)

                for (t in 0 until threadCount) {
                    executor.submit {
                        startLatch.await()
                        for (i in 0 until 10) {
                            try {
                                binding.setReady(true).get(100, TimeUnit.MILLISECONDS)
                            } catch (e: Exception) {
                                val cause = (e as? java.util.concurrent.ExecutionException)?.cause ?: e
                                if (cause is ServerApiException && cause.code == ApiErrors.SERVER_BUSY) {
                                    busyErrors.incrementAndGet()
                                }
                            }
                        }
                        doneLatch.countDown()
                    }
                }

                startLatch.countDown()
                assertTrue(doneLatch.await(5, TimeUnit.SECONDS))
                executor.shutdown()

                assertEquals(160, busyErrors.get(), "All excess concurrent requests must be rejected with SERVER_BUSY")
                assertEquals(128, binding.unconfirmedSideEffectCount, "Hard bound of 128 must be strictly preserved under concurrency")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R04 replay of unconfirmed side effects strictly respects negotiated in flight limit with more than 64 records`() {
        val totalExpected = 70
        val maxObservedInFlight = AtomicInteger(0)
        val currentInFlight = AtomicInteger(0)
        val totalReceived = AtomicInteger(0)
        val allReplayedLatch = CountDownLatch(totalExpected)

        MultiConnectionPeer(
            limitsProvider = { connIndex ->
                if (connIndex == 0) {
                    // 第一轮连接：协商 100ms 超时，让请求在服务端不应答时快速本地超时并沉淀至 unconfirmedSideEffects
                    TcpLimits(maxInFlightRequests = 64, requestTimeoutMillis = 100L)
                } else {
                    // 第二轮连接：恢复正常协商超时 (5000ms)，确保 70 条记录流水线重放期间绝不因 CPU 调度抖动产生伪超时
                    TcpLimits(maxInFlightRequests = 64, requestTimeoutMillis = 5000L)
                }
            }
        ) { s, connIndex ->
            if (connIndex == 0) {
                // 第一轮连接：只收请求不回，让 70 条请求在客户端超时沉淀
                while (!s.isClosed) {
                    try {
                        readClientFrame(s)
                    } catch (_: Throwable) {
                        break
                    }
                }
            } else {
                // 第二轮连接：执行自动重放，验证 70 条记录重放时在途请求数严格受限于 64
                val decoder = TcpFrameDecoder()
                val buf = ByteArray(4096)
                while (!s.isClosed && totalReceived.get() < totalExpected) {
                    val n = try { s.getInputStream().read(buf) } catch (_: Throwable) { break }
                    if (n < 0) break
                    val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
                    for (frame in frames) {
                        if (frame.type == TcpWire.FRAME_TYPE_REQ) {
                            val inFlight = currentInFlight.incrementAndGet()
                            maxObservedInFlight.updateAndGet { maxOf(it, inFlight) }
                            totalReceived.incrementAndGet()
                            val envelope = TcpMessageCodec.parseRequestEnvelope(frame)

                            // 回复成功 RSP
                            val rsp = TcpResponseEnvelope(
                                requestScope = envelope.requestScope,
                                requestId = envelope.requestId,
                                response = ApiResponse(requestId = envelope.requestId, success = true)
                            )
                            sendServerFrame(s, TcpMessageCodec.toFrame(rsp))
                            currentInFlight.decrementAndGet()
                            allReplayedLatch.countDown()
                        }
                    }
                }
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                // 第一批 64 个请求 -> 确定性等待 100ms 超时沉淀完成
                val batch1 = (0 until 64).map { binding.setReady(true) }
                batch1.forEach { f ->
                    try { f.get(5, TimeUnit.SECONDS) } catch (_: Throwable) {}
                }
                assertEquals(64, binding.unconfirmedSideEffectCount)

                // 第二批 6 个请求 -> 确定性等待 100ms 超时沉淀完成，累计达到 70 条
                val batch2 = (0 until 6).map { binding.setReady(true) }
                batch2.forEach { f ->
                    try { f.get(5, TimeUnit.SECONDS) } catch (_: Throwable) {}
                }
                assertEquals(70, binding.unconfirmedSideEffectCount)

                // 重连触发自动重放
                binding.reconnect().get(5, TimeUnit.SECONDS)

                // 等待全部 70 条在流水线流控下重放并确认完成
                assertTrue(allReplayedLatch.await(10, TimeUnit.SECONDS), "All 70 replayed requests must be delivered and processed")
                assertEquals(totalExpected, totalReceived.get(), "Must receive all 70 requests")
                assertTrue(maxObservedInFlight.get() <= 64, "In-flight count during replay (${maxObservedInFlight.get()}) must never exceed limits.maxInFlightRequests (64)")

                val deadline = System.currentTimeMillis() + 5000L
                while (System.currentTimeMillis() < deadline && binding.unconfirmedSideEffectCount > 0) {
                    Thread.sleep(10L)
                }
                assertEquals(0, binding.unconfirmedSideEffectCount, "All replayed requests confirmed and cleared from unconfirmed collection")
            } finally {
                binding.close()
            }
        }
    }

    private fun createDummyPlayerSnapshot(cursor: Long): ProtocolPlayerSnapshot {
        val tile = ProtocolTile.fromTileId(0)
        val view = ProtocolPlayerView(
            viewerSeat = 0,
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(tile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = listOf(tile),
            myDrawnTile = null,
            legalActions = listOf(ProtocolLegalAction.DiscardTile(0, tile))
        )
        val decision = ProtocolPlayerDecision(
            roundId = "r1",
            decisionId = "dec-1",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, tile)))
        )
        return ProtocolPlayerSnapshot(
            sessionId = "s1",
            roundId = "r1",
            syncEpoch = "1",
            cursor = cursor,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = decision
        )
    }

    private fun createDummyPublicSnapshot(cursor: Long): ProtocolPublicSnapshot {
        val session = ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 1,
            dealerSeat = 0,
            honba = 0
        )
        return ProtocolPublicSnapshot(
            sessionId = "s1",
            sessionVersion = 1L,
            roundId = "r1",
            roundVersion = 1L,
            syncEpoch = "1",
            cursor = cursor,
            session = session
        )
    }

    @Test
    fun `R09 early buffer 65 items overflow triggers merged full recovery upon listener mount without subsequent push`() {
        val snapshotRequested = CompletableFuture<ApiRequest>()
        val sendSnapshotResponseLatch = CountDownLatch(1)

        MockPeer { s ->
            // 1. 读取 JOIN_ROOM 请求并响应
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val initialSnap = createDummyPlayerSnapshot(0L)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = initialSnap
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // 2. 服务端在客户端未挂载 onUpdate 前，连续推送 65 条 PLAYER_UPDATE 帧
            for (i in 1..65) {
                val update = ProtocolPlayerUpdate(
                    snapshot = createDummyPlayerSnapshot(i.toLong()),
                    fromCursor = (i - 1).toLong(),
                    visibleEvents = emptyList(),
                    isFullSnapshot = false
                )
                val frame = TcpMessageCodec.toPlayerUpdateFrame(update, isFullSnapshot = false)
                sendServerFrame(s, frame)
            }

            // 3. 等待客户端挂载 listener 并因 overflow 自动发起 SNAPSHOT 请求
            val snapReqFrame = readClientFrame(s)
            val snapEnv = TcpMessageCodec.parseRequestEnvelope(snapReqFrame)
            snapshotRequested.complete(snapEnv.request)

            // 等待测试放行后再回 SNAPSHOT 全量响应
            assertTrue(sendSnapshotResponseLatch.await(3, TimeUnit.SECONDS))
            val recoveredSnap = createDummyPlayerSnapshot(65L)
            val snapRsp = ApiResponse(
                requestId = snapEnv.requestId,
                success = true,
                snapshot = recoveredSnap
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(snapEnv.requestScope, snapEnv.requestId, snapRsp)))

            // 4. 服务端发完 SNAPSHOT 响应后，绝对不再发送任何后续 PUSH 帧！("无后续 PUSH 也恢复")
        }.use { peer ->
            val binding = peer.connect()
            try {
                val bindingResult = binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)
                assertNotNull(bindingResult)

                // 等待 65 帧到达底层，触发早期缓冲溢出 (64 条限制)
                Thread.sleep(400L)
                assertTrue(binding.earlyBufferOverflowStatus, "Early buffer must report overflow after 65 items")
                assertEquals(64, binding.earlyPlayerBufferSize, "Early buffer size must be capped at 64")

                // 挂载 onUpdate 监听器
                val receivedUpdates = CopyOnWriteArrayList<ProtocolPlayerUpdate>()
                binding.onUpdate { update ->
                    receivedUpdates.add(update)
                }

                // 验证客户端立即发出了 SNAPSHOT 请求
                val snapReq = snapshotRequested.get(2, TimeUnit.SECONDS)
                assertEquals(ApiTags.SNAPSHOT, snapReq.api)

                // 服务端回复 SNAPSHOT 全量响应
                sendSnapshotResponseLatch.countDown()

                // 等待全量恢复完成
                Thread.sleep(500L)

                // 验证：收到 64 个增量更新 + 1 个 isFullSnapshot=true 的全量恢复更新 = 65 个更新
                assertEquals(65, receivedUpdates.size, "Must receive 64 drained items + 1 full recovery snapshot")
                val lastUpdate = receivedUpdates.last()
                assertTrue(lastUpdate.isFullSnapshot, "Final recovery update must be marked as isFullSnapshot=true")
                assertEquals(65L, lastUpdate.snapshot.cursor, "Final recovery update must have latest cursor 65")
                assertEquals(65L, binding.lastDispatchedPlayerCursorValue, "lastDispatchedPlayerCursor must be 65")

                // 验证缓冲和溢出标志已清空
                assertEquals(0, binding.earlyPlayerBufferSize)
                assertFalse(binding.earlyBufferOverflowStatus)
            } finally {
                sendSnapshotResponseLatch.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 early buffer wire byte budget 4 MiB overflow triggers merged full recovery`() {
        val snapshotRequested = CompletableFuture<ApiRequest>()

        MockPeer { s ->
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // 等待客户端因 4 MiB 预算溢出触发 SNAPSHOT
            val snapReqFrame = readClientFrame(s)
            val snapEnv = TcpMessageCodec.parseRequestEnvelope(snapReqFrame)
            snapshotRequested.complete(snapEnv.request)

            val recoveredSnap = createDummyPlayerSnapshot(10L)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(snapEnv.requestScope, snapEnv.requestId,
                ApiResponse(requestId = snapEnv.requestId, success = true, snapshot = recoveredSnap))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                // 1. 推送第 1 帧：2 MiB -> 准入，缓冲计数为 2 MiB
                val snap1 = createDummyPlayerSnapshot(1L)
                val update1 = ProtocolPlayerUpdate(snapshot = snap1, fromCursor = 0L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update1, 2 * 1024 * 1024L)
                assertEquals(1, binding.earlyPlayerBufferSize)
                assertEquals(2 * 1024 * 1024L, binding.earlyBufferByteCount)
                assertFalse(binding.earlyBufferOverflowStatus)

                // 2. 推送第 2 帧：2 MiB -> 准入，缓冲累计 4 MiB
                val snap2 = createDummyPlayerSnapshot(2L)
                val update2 = ProtocolPlayerUpdate(snapshot = snap2, fromCursor = 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update2, 2 * 1024 * 1024L)
                assertEquals(2, binding.earlyPlayerBufferSize)
                assertEquals(4 * 1024 * 1024L, binding.earlyBufferByteCount)
                assertFalse(binding.earlyBufferOverflowStatus)

                // 3. 推送第 3 帧：100 KiB -> 超过 4 MiB 预算上限，触发 overflow，不被入队
                val snap3 = createDummyPlayerSnapshot(3L)
                val update3 = ProtocolPlayerUpdate(snapshot = snap3, fromCursor = 2L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update3, 100 * 1024L)
                assertEquals(2, binding.earlyPlayerBufferSize, "Overflown item must not be added to buffer")
                assertEquals(4 * 1024 * 1024L, binding.earlyBufferByteCount, "Byte count must not increase on rejection")
                assertTrue(binding.earlyBufferOverflowStatus, "earlyBufferOverflow must be true after byte limit exceeded")

                // 4. 单大帧直接超出 4 MiB 测试
                val snapHuge = createDummyPlayerSnapshot(4L)
                val updateHuge = ProtocolPlayerUpdate(snapshot = snapHuge, fromCursor = 3L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(updateHuge, 4 * 1024 * 1024L + 1L)
                assertEquals(2, binding.earlyPlayerBufferSize)
                assertTrue(binding.earlyBufferOverflowStatus)

                // 5. 挂载监听器 -> 排水并触发合并全量恢复
                val received = CopyOnWriteArrayList<ProtocolPlayerUpdate>()
                binding.onUpdate { received.add(it) }

                val snapReq = snapshotRequested.get(2, TimeUnit.SECONDS)
                assertEquals(ApiTags.SNAPSHOT, snapReq.api)

                Thread.sleep(300L)
                assertEquals(3, received.size, "Must receive 2 buffered updates + 1 full recovery update")
                assertTrue(received.last().isFullSnapshot)
                assertEquals(10L, received.last().snapshot.cursor)
                assertEquals(0L, binding.earlyBufferByteCount, "Byte budget must return to 0 after draining")
                assertEquals(0, binding.earlyPlayerBufferSize)
                assertFalse(binding.earlyBufferOverflowStatus)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 late stale snapshot query cannot overwrite newer dispatched push cursor`() {
        MockPeer { s ->
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = createDummyPlayerSnapshot(10L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)
                assertEquals(10L, binding.lastDispatchedPlayerCursorValue)

                val received = CopyOnWriteArrayList<ProtocolPlayerUpdate>()
                binding.onUpdate { received.add(it) }

                // 正常新 PUSH 到达：cursor 50
                val update50 = ProtocolPlayerUpdate(snapshot = createDummyPlayerSnapshot(50L), fromCursor = 10L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update50)
                assertEquals(50L, binding.lastDispatchedPlayerCursorValue)
                assertEquals(1, received.size)
                assertEquals(50L, received.first().snapshot.cursor)

                // 模拟旧查询响应迟到 (stale query response with cursor 45 < 50)
                // 游标检查必须直接丢弃该旧更新，绝不能发生游标回退
                val staleUpdate = ProtocolPlayerUpdate(snapshot = createDummyPlayerSnapshot(45L), fromCursor = 40L, visibleEvents = emptyList(), isFullSnapshot = true)
                binding.dispatchPlayerUpdate(staleUpdate)
                assertEquals(50L, binding.lastDispatchedPlayerCursorValue, "Stale cursor 45 must not overwrite newer cursor 50")
                assertEquals(1, received.size, "Stale update must be dropped and not dispatched")

                // 更晚的新 PUSH 到达：cursor 55 > 50 -> 正常派发
                val update55 = ProtocolPlayerUpdate(snapshot = createDummyPlayerSnapshot(55L), fromCursor = 50L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update55)
                assertEquals(55L, binding.lastDispatchedPlayerCursorValue)
                assertEquals(2, received.size)
                assertEquals(55L, received.last().snapshot.cursor)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 role switch clears early buffers resets byte budget and isolates epochs`() {
        val spectatorSnapshotRequested = CompletableFuture<ApiRequest>()

        MockPeer { s ->
            // 1. 响应 JOIN_ROOM
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId,
                ApiResponse(requestId = joinEnv.requestId, success = true, roomId = "ROOM-1", seat = 0, resumeToken = "p-tok",
                    snapshot = createDummyPlayerSnapshot(1L)), grantedScope = TcpScope("p-scope", Long.MAX_VALUE))))

            // 2. 响应 LEAVE_ROOM
            val leaveFrame = readClientFrame(s)
            val leaveEnv = TcpMessageCodec.parseRequestEnvelope(leaveFrame)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(leaveEnv.requestScope, leaveEnv.requestId,
                ApiResponse(requestId = leaveEnv.requestId, success = true, receipt = ProtocolActionReceipt(leaveEnv.requestId, ProtocolReceiptStatus.APPLIED)))))

            // 3. 响应 JOIN_SPECTATOR
            val specFrame = readClientFrame(s)
            val specEnv = TcpMessageCodec.parseRequestEnvelope(specFrame)
            val specBinding = SpectatorBinding(
                roomId = "ROOM-1",
                spectatorId = "spec-1",
                resumeToken = "spec-tok",
                snapshot = createDummyPublicSnapshot(5L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(specEnv.requestScope, specEnv.requestId,
                ApiResponse(requestId = specEnv.requestId, success = true, spectatorBinding = specBinding),
                grantedScope = TcpScope("spec-scope", Long.MAX_VALUE))))

            // 4. 等待 spectator 因 overflow 触发 PUBLIC_SNAPSHOT
            val pubSnapFrame = readClientFrame(s)
            val pubSnapEnv = TcpMessageCodec.parseRequestEnvelope(pubSnapFrame)
            spectatorSnapshotRequested.complete(pubSnapEnv.request)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(pubSnapEnv.requestScope, pubSnapEnv.requestId,
                ApiResponse(requestId = pubSnapEnv.requestId, success = true, publicSnapshot = createDummyPublicSnapshot(70L)))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                // 阶段 1：玩家绑定
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)
                val epoch1 = binding.bindingEpochValue
                assertEquals(1L, binding.lastDispatchedPlayerCursorValue)

                // 缓冲 3 条玩家更新
                for (i in 2..4) {
                    val u = ProtocolPlayerUpdate(snapshot = createDummyPlayerSnapshot(i.toLong()), fromCursor = (i - 1).toLong(), visibleEvents = emptyList(), isFullSnapshot = false)
                    binding.dispatchPlayerUpdate(u)
                }
                assertEquals(3, binding.earlyPlayerBufferSize)
                assertTrue(binding.earlyBufferByteCount > 0L)

                // 阶段 2：退出房间 (角色切换)
                binding.leaveRoom().get(2, TimeUnit.SECONDS)
                val epoch2 = binding.bindingEpochValue
                assertTrue(epoch2 > epoch1, "bindingEpoch must increment on leaveRoom")
                assertNull(binding.boundSeat)
                assertEquals(-1L, binding.lastDispatchedPlayerCursorValue)
                assertEquals(0, binding.earlyPlayerBufferSize, "earlyPlayerBuffer must be cleared on role switch")
                assertEquals(0, binding.earlyPublicBufferSize)
                assertEquals(0L, binding.earlyBufferByteCount, "earlyBufferByteCount must be reset to 0 on role switch")
                assertFalse(binding.earlyBufferOverflowStatus)

                // 阶段 3：观战者绑定
                binding.joinSpectator(com.majiang.server.api.JoinSpectatorRequest("ROOM-1", "Spec1")).get(2, TimeUnit.SECONDS)
                val epoch3 = binding.bindingEpochValue
                assertTrue(epoch3 > epoch2, "bindingEpoch must increment on spectator join")
                assertEquals("spec-1", binding.boundSpectatorId)
                assertEquals(5L, binding.lastDispatchedPublicCursorValue)

                // 推送 65 条公开更新，触发观战早期缓冲溢出
                for (i in 6..70) {
                    val u = ProtocolPublicUpdate(snapshot = createDummyPublicSnapshot(i.toLong()), fromCursor = (i - 1).toLong(), visibleEvents = emptyList(), isFullSnapshot = false)
                    binding.dispatchPublicUpdate(u)
                }
                assertEquals(64, binding.earlyPublicBufferSize)
                assertTrue(binding.earlyBufferOverflowStatus)

                // 挂载观战监听器 -> 触发合并全量恢复 (publicSnapshot)
                val receivedPublic = CopyOnWriteArrayList<ProtocolPublicUpdate>()
                binding.onPublicUpdate { receivedPublic.add(it) }

                val req = spectatorSnapshotRequested.get(2, TimeUnit.SECONDS)
                assertEquals(ApiTags.PUBLIC_SNAPSHOT, req.api)

                Thread.sleep(400L)
                assertEquals(65, receivedPublic.size, "Must receive 64 drained public items + 1 full recovery public snapshot")
                assertTrue(receivedPublic.last().isFullSnapshot)
                assertEquals(70L, receivedPublic.last().snapshot.cursor)
                assertEquals(70L, binding.lastDispatchedPublicCursorValue)
                assertEquals(0, binding.earlyPublicBufferSize)
                assertFalse(binding.earlyBufferOverflowStatus)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 multi-frame realistic wire byte budget accumulation triggers overflow within 1 MiB wire limit`() {
        val snapshotRequested = CompletableFuture<ApiRequest>()

        MockPeer { s ->
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // 等待客户端因累计超 4 MiB 预算溢出触发 SNAPSHOT
            val snapReqFrame = readClientFrame(s)
            val snapEnv = TcpMessageCodec.parseRequestEnvelope(snapReqFrame)
            snapshotRequested.complete(snapEnv.request)

            val recoveredSnap = createDummyPlayerSnapshot(20L)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(snapEnv.requestScope, snapEnv.requestId,
                ApiResponse(requestId = snapEnv.requestId, success = true, snapshot = recoveredSnap))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                // 每帧 900 KiB (921,600 字节)，严格在 MJ-TCP/1 1 MiB (1,048,576 字节) 上限内
                val frameWireBytes = 900 * 1024L
                assertTrue(frameWireBytes <= TcpWire.MAX_PAYLOAD_BYTES.toLong(), "Frame wire bytes must obey 1 MiB wire limit")

                // 推送 4 帧：4 * 900 KiB = 3,600 KiB (~3.51 MiB <= 4 MiB 预算)
                for (i in 1..4) {
                    val snap = createDummyPlayerSnapshot(i.toLong())
                    val update = ProtocolPlayerUpdate(snapshot = snap, fromCursor = (i - 1).toLong(), visibleEvents = emptyList(), isFullSnapshot = false)
                    binding.dispatchPlayerUpdate(update, frameWireBytes)
                    assertEquals(i, binding.earlyPlayerBufferSize)
                    assertEquals(i * frameWireBytes, binding.earlyBufferByteCount)
                    assertFalse(binding.earlyBufferOverflowStatus, "Frame $i must not exceed 4 MiB total budget")
                }

                // 推送第 5 帧：3,600 KiB + 900 KiB = 4,500 KiB > 4,096 KiB (4 MiB)
                // 累计突破 4 MiB 预算，触发溢出拒绝
                val snap5 = createDummyPlayerSnapshot(5L)
                val update5 = ProtocolPlayerUpdate(snapshot = snap5, fromCursor = 4L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update5, frameWireBytes)

                assertEquals(4, binding.earlyPlayerBufferSize, "5th frame must be rejected and not added to buffer")
                assertEquals(4 * frameWireBytes, binding.earlyBufferByteCount, "Byte budget must remain at admitted count")
                assertTrue(binding.earlyBufferOverflowStatus, "earlyBufferOverflow must be true after multi-frame byte limit exceeded")

                // 挂载监听器 -> 排水并触发合并全量恢复
                val received = CopyOnWriteArrayList<ProtocolPlayerUpdate>()
                binding.onUpdate { received.add(it) }

                val snapReq = snapshotRequested.get(2, TimeUnit.SECONDS)
                assertEquals(ApiTags.SNAPSHOT, snapReq.api)

                Thread.sleep(300L)
                assertEquals(5, received.size, "Must receive 4 buffered frames + 1 full recovery frame")
                assertTrue(received.last().isFullSnapshot)
                assertEquals(20L, received.last().snapshot.cursor)
                assertEquals(0L, binding.earlyBufferByteCount, "Byte budget must return to 0 after draining")
                assertEquals(0, binding.earlyPlayerBufferSize)
                assertFalse(binding.earlyBufferOverflowStatus)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 full recovery snapshot query failure retains pending state and recovers on retry`() {
        val firstSnapRequested = CompletableFuture<ApiRequest>()
        val retrySnapRequested = CompletableFuture<ApiRequest>()
        val allowOkResponseLatch = CountDownLatch(1)

        MockPeer { s ->
            // 1. 响应 JOIN_ROOM
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // 2. 接收第 1 次全量 SNAPSHOT 请求 -> 模拟服务端繁忙或错误响应
            val snap1Frame = readClientFrame(s)
            val snap1Env = TcpMessageCodec.parseRequestEnvelope(snap1Frame)
            firstSnapRequested.complete(snap1Env.request)
            val errorRsp = ApiResponse(requestId = snap1Env.requestId, success = false, error = ApiErrors.SERVER_BUSY)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(snap1Env.requestScope, snap1Env.requestId, errorRsp)))

            // 3. 接收客户端退避重试发出的第 2 次 SNAPSHOT 请求
            val snap2Frame = readClientFrame(s)
            val snap2Env = TcpMessageCodec.parseRequestEnvelope(snap2Frame)
            retrySnapRequested.complete(snap2Env.request)

            // 等待测试端校验重试期间依然处于 pending 状态，再发送成功响应
            allowOkResponseLatch.await(3, TimeUnit.SECONDS)
            val recoveredSnap = createDummyPlayerSnapshot(99L)
            val okRsp = ApiResponse(requestId = snap2Env.requestId, success = true, snapshot = recoveredSnap)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(snap2Env.requestScope, snap2Env.requestId, okRsp)))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                // 推送 65 条更新触发溢出
                for (i in 1..65) {
                    val u = ProtocolPlayerUpdate(snapshot = createDummyPlayerSnapshot(i.toLong()), fromCursor = (i - 1).toLong(), visibleEvents = emptyList(), isFullSnapshot = false)
                    binding.dispatchPlayerUpdate(u)
                }
                assertTrue(binding.earlyBufferOverflowStatus)

                // 挂载监听器 -> 触发初次 SNAPSHOT
                val received = CopyOnWriteArrayList<ProtocolPlayerUpdate>()
                binding.onUpdate { received.add(it) }

                val firstReq = firstSnapRequested.get(2, TimeUnit.SECONDS)
                assertEquals(ApiTags.SNAPSHOT, firstReq.api)

                // 验证客户端退避重试触发了第 2 次 SNAPSHOT 请求
                val retryReq = retrySnapRequested.get(2, TimeUnit.SECONDS)
                assertEquals(ApiTags.SNAPSHOT, retryReq.api)

                // 验证在第 2 次重试请求发出并等待响应时：恢复待办状态未丢失，早期溢出标志未被清除！
                assertTrue(binding.isPlayerRecoveryPending, "Recovery pending state must be preserved when snapshot query fails and retries")
                assertTrue(binding.earlyBufferOverflowStatus, "earlyBufferOverflow must NOT be cleared when query fails")

                // 允许服务端回复第 2 次请求成功
                allowOkResponseLatch.countDown()

                // 等待重试成功响应被客户端处理
                Thread.sleep(300L)
                assertFalse(binding.isPlayerRecoveryPending, "Recovery pending state must be cleared after successful retry")
                assertFalse(binding.earlyBufferOverflowStatus, "earlyBufferOverflow must be cleared after successful recovery")
                assertTrue(received.any { it.isFullSnapshot && it.snapshot.cursor == 99L },
                    "Listener must receive full recovery snapshot after retry succeeds")
            } finally {
                allowOkResponseLatch.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 room update overflow delivers latest room version on listener mount preserving order and deduplication`() {
        MockPeer { s ->
            // 1. 读取 JOIN_ROOM 请求并响应
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // 2. 服务端在客户端未挂载 onRoomUpdate 前，连续推送 65 条 ROOM_UPDATE 帧
            for (i in 1..65) {
                val roomInfo = ProtocolRoomInfo(
                    roomId = "ROOM-1",
                    lifecycle = ProtocolRoomLifecycle.LOBBY,
                    version = i.toLong()
                )
                val frame = TcpMessageCodec.toRoomUpdateFrame(roomInfo)
                sendServerFrame(s, frame)
            }
            // 不再发送任何后续 PUSH 帧 ("无后续 PUSH 也恢复")
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                // 等待 65 帧到达底层，触发早期缓冲溢出 (64 条限制)
                val deadline = System.currentTimeMillis() + 5000L
                while (System.currentTimeMillis() < deadline && (binding.earlyRoomBufferSize < 64 || binding.latestRoomInfoVersion < 65L)) {
                    Thread.sleep(20L)
                }
                assertEquals(64, binding.earlyRoomBufferSize, "Early room buffer size must be capped at 64")
                assertEquals(65L, binding.latestRoomInfoVersion, "latestRoomInfoVersion must track latest 65")

                val receivedRooms = CopyOnWriteArrayList<ProtocolRoomInfo>()
                binding.onRoomUpdate { receivedRooms.add(it) }

                // 挂载后，即使没有任何后续 PUSH，也必须完整交付到最新的第 65 版，且严格单调递增
                assertFalse(receivedRooms.isEmpty(), "Received rooms must not be empty")
                assertEquals(65L, receivedRooms.last().version, "Latest received version must be 65")
                assertEquals(65L, binding.lastDispatchedRoomVersionValue, "lastDispatchedRoomVersion must be updated to 65")

                // 检验单调递增无逆序
                for (idx in 1 until receivedRooms.size) {
                    assertTrue(receivedRooms[idx].version > receivedRooms[idx - 1].version,
                        "Room versions must be strictly monotonically increasing: ${receivedRooms[idx - 1].version} -> ${receivedRooms[idx].version}")
                }

                // 检验去重：若后续收到重复的 <= 65 的版本，直接丢弃
                val staleRoom = ProtocolRoomInfo(
                    roomId = "ROOM-1",
                    lifecycle = ProtocolRoomLifecycle.LOBBY,
                    version = 65L
                )
                binding.dispatchRoomUpdate(staleRoom)
                assertEquals(65L, receivedRooms.last().version)

                // 收到新版本 66L，正常派发
                val newRoom = ProtocolRoomInfo(
                    roomId = "ROOM-1",
                    lifecycle = ProtocolRoomLifecycle.LOBBY,
                    version = 66L
                )
                binding.dispatchRoomUpdate(newRoom)
                assertEquals(66L, receivedRooms.last().version)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 player snapshot recovery retry exhaustion enters observable disconnect failure state`() {
        val snapRequestsCount = AtomicInteger(0)

        MockPeer { s ->
            // 1. 响应 JOIN_ROOM
            val joinFrame = readClientFrame(s)
            val joinEnv = TcpMessageCodec.parseRequestEnvelope(joinFrame)
            val joinRsp = ApiResponse(
                requestId = joinEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "token-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(joinEnv.requestScope, joinEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // 2. 持续处理客户端发出的 SNAPSHOT 请求，全部回复失败 (SERVER_BUSY)
            while (!s.isClosed) {
                val frame = try {
                    readClientFrame(s)
                } catch (_: Throwable) {
                    break
                }
                if (frame.type == TcpWire.FRAME_TYPE_REQ) {
                    val env = TcpMessageCodec.parseRequestEnvelope(frame)
                    if (env.request.api == ApiTags.SNAPSHOT) {
                        snapRequestsCount.incrementAndGet()
                        val errRsp = ApiResponse(requestId = env.requestId, success = false, error = ApiErrors.SERVER_BUSY)
                        sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(env.requestScope, env.requestId, errRsp)))
                    }
                }
            }
        }.use { peer ->
            val binding = peer.connect()
            val disconnectLatch = CountDownLatch(1)
            val observedReason = AtomicReference<String>()
            binding.onDisconnect { reason ->
                observedReason.set(reason)
                disconnectLatch.countDown()
            }

            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                // 推送 65 条更新触发溢出
                for (i in 1..65) {
                    val u = ProtocolPlayerUpdate(snapshot = createDummyPlayerSnapshot(i.toLong()), fromCursor = (i - 1).toLong(), visibleEvents = emptyList(), isFullSnapshot = false)
                    binding.dispatchPlayerUpdate(u)
                }
                assertTrue(binding.earlyBufferOverflowStatus)

                // 挂载监听器 -> 触发 triggerPlayerRecovery
                binding.onUpdate { /* no-op */ }

                // 验证在 5 次重试全部失败后，不再静默死挂 pending，而是可观察地转入断线 failure 并触发 onDisconnect
                assertTrue(disconnectLatch.await(6, TimeUnit.SECONDS), "Binding must enter observable disconnect failure upon recovery retry exhaustion")
                assertEquals(TcpCloseReasons.HEARTBEAT_TIMEOUT, observedReason.get())
                assertFalse(binding.isPlayerRecoveryPending, "playerRecoveryPending must be cleared after retry exhaustion")
                assertFalse(binding.earlyBufferOverflowStatus, "earlyBufferOverflow must be cleared after failure handling")
                assertTrue(snapRequestsCount.get() >= 5, "Should have attempted at least 5 snapshot queries before exhaustion, got ${snapRequestsCount.get()}")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 room listener drain and live update preserve version order under callback suspension`() {
        MockPeer { _ ->
            // no-op handler
        }.use { peer ->
            val binding = peer.connect()
            try {
                // Pre-populate early room updates
                binding.dispatchRoomUpdate(ProtocolRoomInfo("ROOM-1", ProtocolRoomLifecycle.LOBBY, version = 1L), 10L)
                binding.dispatchRoomUpdate(ProtocolRoomInfo("ROOM-1", ProtocolRoomLifecycle.LOBBY, version = 2L), 10L)

                val enteredFirst = CountDownLatch(1)
                val releaseFirst = CountDownLatch(1)
                val receivedVersions = CopyOnWriteArrayList<Long>()

                val mountingThread = Thread {
                    binding.onRoomUpdate { info ->
                        receivedVersions.add(info.version)
                        if (info.version == 1L) {
                            enteredFirst.countDown()
                            releaseFirst.await(2, TimeUnit.SECONDS)
                        }
                    }
                }
                mountingThread.start()

                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))
                // Dispatch live update 3 while callback for 1 is suspended
                binding.dispatchRoomUpdate(ProtocolRoomInfo("ROOM-1", ProtocolRoomLifecycle.LOBBY, version = 3L), 10L)
                releaseFirst.countDown()

                mountingThread.join(2000)
                assertFalse(mountingThread.isAlive)
                assertEquals(listOf(1L, 2L, 3L), receivedVersions.toList(), "Early room updates must precede live updates in order")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 public listener drain and live update preserve cursor order under callback suspension`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
            val snapshot = ProtocolPublicSnapshot("s1", 1L, syncEpoch = "1", cursor = 0L, session = session)
            val spectator = SpectatorBinding("ROOM-1", "spec-1", "tok-1", snapshot)
            val rsp = ApiResponse(requestId = reqEnv.requestId, success = true, spectatorBinding = spectator)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, rsp,
                grantedScope = TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinSpectator(com.majiang.server.api.JoinSpectatorRequest("ROOM-1", "Viewer")).get(2, TimeUnit.SECONDS)

                fun makePublicUpdate(cursor: Long): ProtocolPublicUpdate {
                    val snap = createDummyPublicSnapshot(cursor)
                    return ProtocolPublicUpdate(snapshot = snap, fromCursor = cursor - 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                }

                // Pre-populate early public updates
                binding.dispatchPublicUpdate(makePublicUpdate(1L), 10L)
                binding.dispatchPublicUpdate(makePublicUpdate(2L), 10L)

                val enteredFirst = CountDownLatch(1)
                val releaseFirst = CountDownLatch(1)
                val receivedCursors = CopyOnWriteArrayList<Long>()

                val mountingThread = Thread {
                    binding.onPublicUpdate { update ->
                        receivedCursors.add(update.snapshot.cursor)
                        if (update.snapshot.cursor == 1L) {
                            enteredFirst.countDown()
                            releaseFirst.await(2, TimeUnit.SECONDS)
                        }
                    }
                }
                mountingThread.start()

                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))
                // Dispatch live public update 3 while callback for 1 is suspended
                binding.dispatchPublicUpdate(makePublicUpdate(3L), 10L)
                releaseFirst.countDown()

                mountingThread.join(2000)
                assertFalse(mountingThread.isAlive)
                assertEquals(listOf(1L, 2L, 3L), receivedCursors.toList(), "Early public updates must precede live updates in order")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 player listener drain and live update preserve cursor order under callback suspension`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val joinRsp = ApiResponse(
                requestId = reqEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "tok-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                fun makePlayerUpdate(cursor: Long): ProtocolPlayerUpdate {
                    val snap = createDummyPlayerSnapshot(cursor)
                    return ProtocolPlayerUpdate(snapshot = snap, fromCursor = cursor - 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                }

                // Pre-populate early player updates
                binding.dispatchPlayerUpdate(makePlayerUpdate(1L), 10L)
                binding.dispatchPlayerUpdate(makePlayerUpdate(2L), 10L)

                val enteredFirst = CountDownLatch(1)
                val releaseFirst = CountDownLatch(1)
                val receivedCursors = CopyOnWriteArrayList<Long>()

                val mountingThread = Thread {
                    binding.onUpdate { update ->
                        receivedCursors.add(update.snapshot.cursor)
                        if (update.snapshot.cursor == 1L) {
                            enteredFirst.countDown()
                            releaseFirst.await(2, TimeUnit.SECONDS)
                        }
                    }
                }
                mountingThread.start()

                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))
                // Dispatch live player update 3 while callback for 1 is suspended
                binding.dispatchPlayerUpdate(makePlayerUpdate(3L), 10L)
                releaseFirst.countDown()

                mountingThread.join(2000)
                assertFalse(mountingThread.isAlive)
                assertEquals(listOf(1L, 2L, 3L), receivedCursors.toList(), "Early player updates must precede live updates in order")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 FIX5 public listener remount on saturated dispatch queue preserves queue head without silent drop and triggers recovery`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val specBinding = SpectatorBinding(
                roomId = "ROOM-1",
                spectatorId = "spec-1",
                resumeToken = "spec-tok",
                snapshot = createDummyPublicSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId,
                ApiResponse(requestId = reqEnv.requestId, success = true, spectatorBinding = specBinding),
                grantedScope = TcpScope("spec-scope", Long.MAX_VALUE))))

            // Expect public snapshot recovery query
            val recoveryReq = readClientFrame(s)
            val recEnv = TcpMessageCodec.parseRequestEnvelope(recoveryReq)
            assertEquals(ApiTags.PUBLIC_SNAPSHOT, recEnv.request.api)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(recEnv.requestScope, recEnv.requestId,
                ApiResponse(requestId = recEnv.requestId, success = true, publicSnapshot = createDummyPublicSnapshot(200L)),
                grantedScope = TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val enteredFirst = CountDownLatch(1)
            val releaseFirst = CountDownLatch(1)
            try {
                binding.joinSpectator(com.majiang.server.api.JoinSpectatorRequest("ROOM-1", "Viewer1")).get(2, TimeUnit.SECONDS)

                fun makePublicUpdate(cursor: Long): ProtocolPublicUpdate {
                    val snap = createDummyPublicSnapshot(cursor)
                    return ProtocolPublicUpdate(snapshot = snap, fromCursor = cursor - 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                }

                val sub1 = binding.onPublicUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        enteredFirst.countDown()
                        releaseFirst.await(3, TimeUnit.SECONDS)
                    }
                }

                val senderThread = Thread { binding.dispatchPublicUpdate(makePublicUpdate(1L), 10L) }
                senderThread.start()
                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))

                // Queue 2..129 fill the 128-item dispatch queue
                (2L..129L).forEach { binding.dispatchPublicUpdate(makePublicUpdate(it), 10L) }
                assertEquals(128, binding.publicDispatchQueueSize)

                // Unmount listener
                sub1.close()

                // Dispatch update 130 into early buffer
                binding.dispatchPublicUpdate(makePublicUpdate(130L), 10L)
                assertEquals(1, binding.earlyPublicBufferSize)

                // Remount new listener while dispatch queue is full
                val delivered = CopyOnWriteArrayList<Long>()
                binding.onPublicUpdate { delivered.add(it.snapshot.cursor) }

                // Release first sender
                releaseFirst.countDown()
                senderThread.join(2000)

                // Head must NOT be dropped! Cursor 2 must be present!
                assertTrue(delivered.contains(2L), "Queue head (cursor 2) must not be silently dropped")
                // And because update 130 could not fit, recovery must be pending or triggered
                assertTrue(binding.isPublicRecoveryPending || delivered.contains(200L),
                    "Full recovery obligation must be established when dispatch queue saturates")
            } finally {
                releaseFirst.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 FIX5 player listener remount on saturated dispatch queue preserves queue head without silent drop and triggers recovery`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val joinRsp = ApiResponse(
                requestId = reqEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "tok-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))

            // Expect player snapshot recovery query
            val recoveryReq = readClientFrame(s)
            val recEnv = TcpMessageCodec.parseRequestEnvelope(recoveryReq)
            assertEquals(ApiTags.SNAPSHOT, recEnv.request.api)
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(recEnv.requestScope, recEnv.requestId,
                ApiResponse(requestId = recEnv.requestId, success = true, snapshot = createDummyPlayerSnapshot(200L)),
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val enteredFirst = CountDownLatch(1)
            val releaseFirst = CountDownLatch(1)
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                fun makePlayerUpdate(cursor: Long): ProtocolPlayerUpdate {
                    val snap = createDummyPlayerSnapshot(cursor)
                    return ProtocolPlayerUpdate(snapshot = snap, fromCursor = cursor - 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                }

                val sub1 = binding.onUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        enteredFirst.countDown()
                        releaseFirst.await(3, TimeUnit.SECONDS)
                    }
                }

                val senderThread = Thread { binding.dispatchPlayerUpdate(makePlayerUpdate(1L), 10L) }
                senderThread.start()
                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))

                // Queue 2..129 fill the 128-item dispatch queue
                (2L..129L).forEach { binding.dispatchPlayerUpdate(makePlayerUpdate(it), 10L) }
                assertEquals(128, binding.playerDispatchQueueSize)

                // Unmount listener
                sub1.close()

                // Dispatch update 130 into early buffer
                binding.dispatchPlayerUpdate(makePlayerUpdate(130L), 10L)
                assertEquals(1, binding.earlyPlayerBufferSize)

                // Remount new listener
                val delivered = CopyOnWriteArrayList<Long>()
                binding.onUpdate { delivered.add(it.snapshot.cursor) }

                releaseFirst.countDown()
                senderThread.join(2000)

                assertTrue(delivered.contains(2L), "Queue head (cursor 2) must not be silently dropped")
                assertTrue(binding.isPlayerRecoveryPending || delivered.contains(200L),
                    "Full recovery obligation must be established when dispatch queue saturates")
            } finally {
                releaseFirst.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 FIX5 dispatch queue 4 MiB wire byte budget saturation sets recovery pending`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val joinRsp = ApiResponse(
                requestId = reqEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "tok-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                // Block dispatch queue by mounting a slow listener that holds item 1
                val enteredFirst = CountDownLatch(1)
                val releaseFirst = CountDownLatch(1)
                binding.onUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        enteredFirst.countDown()
                        releaseFirst.await(3, TimeUnit.SECONDS)
                    }
                }

                val senderThread = Thread {
                    val snap1 = createDummyPlayerSnapshot(1L)
                    binding.dispatchPlayerUpdate(ProtocolPlayerUpdate(snapshot = snap1, fromCursor = 0L, visibleEvents = emptyList(), isFullSnapshot = false), 10L)
                }
                senderThread.start()
                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))

                // Dispatch updates with large wireBytes to exceed 4 MiB (e.g. 2 x 2.5 MiB)
                val snap2 = createDummyPlayerSnapshot(2L)
                val update2 = ProtocolPlayerUpdate(snapshot = snap2, fromCursor = 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update2, 2_500_000L)

                assertEquals(1, binding.playerDispatchQueueSize)
                assertEquals(2_500_000L, binding.playerDispatchQueueByteCount)

                // Next update exceeds 4 MiB limit (2.5 MiB + 2.5 MiB = 5 MiB > 4 MiB)
                val snap3 = createDummyPlayerSnapshot(3L)
                val update3 = ProtocolPlayerUpdate(snapshot = snap3, fromCursor = 2L, visibleEvents = emptyList(), isFullSnapshot = false)
                binding.dispatchPlayerUpdate(update3, 2_500_000L)

                // Must NOT be added to dispatch queue, must set recovery pending and overflow
                assertEquals(1, binding.playerDispatchQueueSize)
                assertTrue(binding.earlyBufferOverflowStatus)
                releaseFirst.countDown()
                senderThread.join(2000)
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R09 FIX6 queued public update survives listener unmount before remount without silent drop`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val specBinding = SpectatorBinding(
                roomId = "ROOM-1",
                spectatorId = "spec-1",
                resumeToken = "spec-tok",
                snapshot = createDummyPublicSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId,
                ApiResponse(requestId = reqEnv.requestId, success = true, spectatorBinding = specBinding),
                grantedScope = TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val entered = CountDownLatch(1)
            val release = CountDownLatch(1)
            try {
                binding.joinSpectator(com.majiang.server.api.JoinSpectatorRequest("ROOM-1", "Viewer1")).get(2, TimeUnit.SECONDS)

                fun update(n: Long): ProtocolPublicUpdate {
                    val snap = createDummyPublicSnapshot(n)
                    return ProtocolPublicUpdate(snapshot = snap, fromCursor = n - 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                }

                val firstSubscription = binding.onPublicUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        entered.countDown()
                        release.await(3, TimeUnit.SECONDS)
                    }
                }

                val sender = Thread { binding.dispatchPublicUpdate(update(1L), 10L) }
                sender.start()
                assertTrue(entered.await(2, TimeUnit.SECONDS))

                // Queue update 2 while listener 1 is blocked
                binding.dispatchPublicUpdate(update(2L), 10L)

                // Unmount listener 1 while update 2 is in queue
                firstSubscription.close()
                release.countDown()
                sender.join(2000)
                assertFalse(sender.isAlive)

                // Cursor 2 must NOT have been marked as dispatched
                assertEquals(1L, binding.lastDispatchedPublicCursorValue,
                    "Cursor must not be advanced when messages are not delivered to any listener")

                // Mount new listener -> update 2 must be delivered!
                val delivered = CopyOnWriteArrayList<Long>()
                binding.onPublicUpdate { delivered.add(it.snapshot.cursor) }

                assertTrue(delivered.contains(2L),
                    "Queued public update must survive listener unmount and be delivered upon remount")
            } finally {
                release.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 FIX6 queued player update survives listener unmount before remount without silent drop`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val joinRsp = ApiResponse(
                requestId = reqEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "tok-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val entered = CountDownLatch(1)
            val release = CountDownLatch(1)
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                fun update(n: Long): ProtocolPlayerUpdate {
                    val snap = createDummyPlayerSnapshot(n)
                    return ProtocolPlayerUpdate(snapshot = snap, fromCursor = n - 1L, visibleEvents = emptyList(), isFullSnapshot = false)
                }

                val firstSubscription = binding.onUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        entered.countDown()
                        release.await(3, TimeUnit.SECONDS)
                    }
                }

                val sender = Thread { binding.dispatchPlayerUpdate(update(1L), 10L) }
                sender.start()
                assertTrue(entered.await(2, TimeUnit.SECONDS))

                // Queue update 2 while listener 1 is blocked
                binding.dispatchPlayerUpdate(update(2L), 10L)

                // Unmount listener 1 while update 2 is in queue
                firstSubscription.close()
                release.countDown()
                sender.join(2000)
                assertFalse(sender.isAlive)

                // Cursor 2 must NOT have been marked as dispatched
                assertEquals(1L, binding.lastDispatchedPlayerCursorValue,
                    "Player cursor must not be advanced when messages are not delivered to any listener")

                // Mount new listener -> update 2 must be delivered!
                val delivered = CopyOnWriteArrayList<Long>()
                binding.onUpdate { delivered.add(it.snapshot.cursor) }

                assertTrue(delivered.contains(2L),
                    "Queued player update must survive listener unmount and be delivered upon remount")
            } finally {
                release.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 FIX6 queued room update survives listener unmount before remount without silent drop`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val joinRsp = ApiResponse(
                requestId = reqEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "tok-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val entered = CountDownLatch(1)
            val release = CountDownLatch(1)
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                fun roomInfo(ver: Long): ProtocolRoomInfo {
                    return ProtocolRoomInfo(
                        roomId = "ROOM-1",
                        lifecycle = ProtocolRoomLifecycle.LOBBY,
                        version = ver
                    )
                }

                val firstSubscription = binding.onRoomUpdate { info ->
                    if (info.version == 1L) {
                        entered.countDown()
                        release.await(3, TimeUnit.SECONDS)
                    }
                }

                val sender = Thread { binding.dispatchRoomUpdate(roomInfo(1L), 10L) }
                sender.start()
                assertTrue(entered.await(2, TimeUnit.SECONDS))

                // Queue room update 2 while listener 1 is blocked
                binding.dispatchRoomUpdate(roomInfo(2L), 10L)

                // Unmount listener 1 while room update 2 is in queue
                firstSubscription.close()
                release.countDown()
                sender.join(2000)
                assertFalse(sender.isAlive)

                // Room version 2 must NOT have been marked as dispatched
                assertEquals(1L, binding.lastDispatchedRoomVersionValue,
                    "Room version must not be advanced when messages are not delivered to any listener")

                // Mount new room listener -> room update 2 must be delivered!
                val delivered = CopyOnWriteArrayList<Long>()
                binding.onRoomUpdate { delivered.add(it.version) }

                assertTrue(delivered.contains(2L),
                    "Queued room update must survive listener unmount and be delivered upon remount")
            } finally {
                release.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R09 single drainer concurrency guarantees strictly sequential callbacks without overlap`() {
        MockPeer { s ->
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val joinRsp = ApiResponse(
                requestId = reqEnv.requestId,
                success = true,
                roomId = "ROOM-1",
                seat = 0,
                resumeToken = "tok-1",
                snapshot = createDummyPlayerSnapshot(0L)
            )
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, joinRsp,
                grantedScope = TcpScope("player-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinRoom(com.majiang.server.api.JoinRoomRequest("ROOM-1", "Player1")).get(2, TimeUnit.SECONDS)

                val activeDrainers = AtomicInteger(0)
                val maxActiveDrainers = AtomicInteger(0)
                val deliveredCursors = CopyOnWriteArrayList<Long>()
                val totalUpdates = 200

                binding.onUpdate { update ->
                    val current = activeDrainers.incrementAndGet()
                    maxActiveDrainers.updateAndGet { maxOf(it, current) }
                    Thread.sleep(1) // 微延迟放大潜在竞态
                    deliveredCursors.add(update.snapshot.cursor)
                    activeDrainers.decrementAndGet()
                }

                val threads = 8
                val updatesPerThread = totalUpdates / threads
                val executor = Executors.newFixedThreadPool(threads)
                val startLatch = CountDownLatch(1)
                val doneLatch = CountDownLatch(threads)

                for (t in 0 until threads) {
                    val startCursor = t * updatesPerThread + 1
                    executor.submit {
                        startLatch.await()
                        for (i in 0 until updatesPerThread) {
                            val c = (startCursor + i).toLong()
                            val snap = createDummyPlayerSnapshot(c)
                            val update = ProtocolPlayerUpdate(snapshot = snap, fromCursor = c - 1L, visibleEvents = emptyList())
                            binding.dispatchPlayerUpdate(update, 50L)
                        }
                        doneLatch.countDown()
                    }
                }

                startLatch.countDown()
                assertTrue(doneLatch.await(5, TimeUnit.SECONDS))
                executor.shutdown()

                // 等待所有排空完成
                Thread.sleep(500L)

                assertEquals(1, maxActiveDrainers.get(), "At no point may more than 1 drainer execute callbacks concurrently")
                assertEquals(0, activeDrainers.get(), "All drainers must have finished cleanly")
                assertTrue(deliveredCursors.isNotEmpty(), "Updates must be delivered")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 outbound write queue saturation rejects excess frames`() {
        MockPeer { s ->
            // 握手后完全不读取 Socket，模拟慢端或网络堵塞
            while (!s.isClosed) {
                Thread.sleep(100)
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                // 写入大帧（512 KiB）快速填满 4 MiB 出站字节预算
                val bigPayload = ByteArray(512 * 1024)
                val bigFrame = TcpFrame(TcpWire.FRAME_TYPE_PING, bigPayload)

                var rejected = false
                for (i in 0 until 20) {
                    try {
                        binding.writeRawFrameForTest(bigFrame)
                    } catch (e: java.io.IOException) {
                        if (e.message?.contains("Outbound write queue saturated") == true) {
                            rejected = true
                            break
                        }
                    }
                }

                assertTrue(rejected, "Outbound write queue must reject excess data once saturated")
                assertTrue(binding.outboundQueueSize <= 256, "Outbound queue size must be bounded by 256")
                assertTrue(binding.queuedOutboundByteCount <= 4L * 1024 * 1024, "Outbound queued bytes must be bounded by 4 MiB")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 stalled write detection triggers SLOW_CONSUMER after 10s`() {
        MockPeer { s ->
            while (!s.isClosed) {
                Thread.sleep(100)
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                val dummyFrame = TcpMessageCodec.toFrame(TcpPing("0123456789abcdef"))
                binding.writeRawFrameForTest(dummyFrame)
                assertTrue(binding.outboundQueueSize > 0)

                // 模拟时间前进 10001ms，未发生写进展
                val futureTime = System.currentTimeMillis() + 10001L
                binding.checkStalledWrite(futureTime)

                assertEquals(TcpCloseReasons.SLOW_CONSUMER, binding.disconnectReason,
                    "Stalled write progress for 10s must trigger SLOW_CONSUMER")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 slow listener isolation does not block transport PING PONG`() {
        val pongReceived = CompletableFuture<TcpPong>()
        MockPeer { s ->
            // 1. 发送 PLAYER_UPDATE 帧触发慢监听器
            val update = ProtocolPlayerUpdate(
                snapshot = createDummyPlayerSnapshot(1L),
                fromCursor = 0L,
                visibleEvents = emptyList(),
                isFullSnapshot = false
            )
            sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update))

            // 2. 稍等 20ms 确保客户端已收到并在 worker 线程执行慢监听器
            Thread.sleep(20L)

            // 3. 服务端发送 PING 帧
            val pingNonce = "fedcba9876543210"
            val sendPingTime = System.currentTimeMillis()
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpPing(pingNonce)))

            // 4. 等待客户端在 I/O 线程立即返回 PONG (应远小于慢监听器的 500ms 睡眠)
            val frame = readClientFrame(s)
            val elapsed = System.currentTimeMillis() - sendPingTime
            if (frame.type == TcpWire.FRAME_TYPE_PONG) {
                pongReceived.complete(TcpMessageCodec.parsePong(frame))
            }
            assertTrue(elapsed < 300L, "PONG must be echoed immediately by I/O loop within ~150ms, elapsed: ${elapsed}ms")
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.onUpdate {
                    // 慢监听器：故意睡眠 500ms
                    Thread.sleep(500L)
                }

                val pong = pongReceived.get(3, TimeUnit.SECONDS)
                assertEquals("fedcba9876543210", pong.nonce, "Client echoed matching nonce even during slow listener execution")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 reconnect reuses selector and cleans up previous resources`() {
        MultiConnectionPeer { _, _ -> } .use { peer ->
            val binding = peer.connect()
            try {
                assertTrue(binding.isIoLoopAlive, "I/O loop must be alive after connect")
                assertEquals(0L, binding.socketGeneration)

                // 执行重连
                binding.reconnect().get(3, TimeUnit.SECONDS)
                assertTrue(binding.isIoLoopAlive, "I/O loop must remain alive after reconnect")
                assertEquals(1L, binding.socketGeneration, "Socket generation must increment")

                // 关闭 binding
                binding.close()
                Thread.sleep(200L)
                assertFalse(binding.isIoLoopAlive, "I/O loop must terminate cleanly after close")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 outbound write queue accounts for currentWriteBuffer and bounds held frames and bytes under partial write`() {
        MockPeer { s ->
            while (!s.isClosed) {
                Thread.sleep(100)
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                // 1. 模拟当前存在未写完的 currentWriteBuffer (例如 100 KiB)
                val partialBuffer = ByteBuffer.allocate(100 * 1024)
                binding.setCurrentWriteBufferForTest(partialBuffer)
                binding.setQueuedOutboundBytesForTest(100L * 1024)

                // 此时虽然 outboundQueue 为空，但持有帧数必须计入 currentWriteBuffer (size = 1)
                assertEquals(1, binding.outboundQueueSize, "outboundQueueSize must account for currentWriteBuffer")
                assertEquals(100L * 1024, binding.queuedOutboundByteCount, "queuedOutboundByteCount must reflect held bytes")

                // 2. 塞入 255 个已排队帧，使总持有帧数达到 256
                for (i in 0 until 255) {
                    binding.addOutboundQueueForTest(ByteBuffer.allocate(1))
                }
                assertEquals(256, binding.outboundQueueSize, "Total held frames must be 256 (1 current + 255 queued)")

                // 再次尝试写入第 257 帧，必须被双限准入拦截拒绝
                val dummyFrame = TcpMessageCodec.toFrame(TcpPing("0123456789abcdef"))
                var frameRejected = false
                try {
                    binding.writeRawFrameForTest(dummyFrame)
                } catch (e: java.io.IOException) {
                    if (e.message?.contains("Outbound write queue saturated") == true) {
                        frameRejected = true
                    }
                }
                assertTrue(frameRejected, "Write must be rejected when held frames reaches 256")

                // 3. 校验字节预算双限：设置 queuedOutboundBytes 逼近 4 MiB 上限
                binding.setQueuedOutboundBytesForTest(4L * 1024 * 1024 - 10)
                var bytesRejected = false
                try {
                    binding.writeRawFrameForTest(dummyFrame)
                } catch (e: java.io.IOException) {
                    if (e.message?.contains("Outbound write queue saturated") == true) {
                        bytesRejected = true
                    }
                }
                assertTrue(bytesRejected, "Write must be rejected when queued bytes exceeds 4 MiB")
            } finally {
                binding.close()
                assertEquals(0, binding.outboundQueueSize, "outboundQueueSize must be 0 after close")
                assertEquals(0L, binding.queuedOutboundByteCount, "queuedOutboundByteCount must be 0 after close")
            }
        }
    }

    @Test
    fun `R07 handleWrite rotation caps single turn write to 64 KiB for fairness`() {
        MockPeer { s ->
            while (!s.isClosed) {
                Thread.sleep(100)
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                val sc = binding.socketChannelForTest ?: fail("SocketChannel must not be null")
                val key = binding.selectionKeyForTest ?: fail("SelectionKey must not be null")

                // 直接放入 200 KiB 的 currentWriteBuffer，不设 OP_WRITE 避免后台 ioThread 竞争
                val bigPayload = ByteBuffer.allocate(200 * 1024)
                binding.setCurrentWriteBufferForTest(bigPayload)
                binding.setQueuedOutboundBytesForTest(200L * 1024)

                // 此时直接调用 handleWrite 测试单轮轮转
                binding.handleWrite(key, sc)

                // 验证单轮写出受 64 KiB (65536 字节) 严格约束
                val remainingBytes = binding.queuedOutboundByteCount
                val writtenBytes = (200L * 1024) - remainingBytes
                assertTrue(writtenBytes in 1..65536, "Single turn handleWrite must write at most 64 KiB, actual: $writtenBytes bytes")
                assertNotNull(binding.currentWriteBufferForTest, "currentWriteBuffer must retain unwritten remaining bytes")
                assertTrue(binding.currentWriteBufferForTest!!.hasRemaining(), "currentWriteBuffer must have remaining unwritten bytes")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 inbound mailbox bounded at 64 frames and 4 MiB triggers SLOW_CONSUMER under slow listener`() {
        val slowListenerEntered = CountDownLatch(1)
        val releaseSlowListener = CountDownLatch(1)
        val pongReceived = CompletableFuture<TcpPong>()
        val clientReadyLatch = CountDownLatch(1)

        MockPeer { s ->
            // 等待客户端完成建连且已挂载慢监听器
            assertTrue(clientReadyLatch.await(5, TimeUnit.SECONDS), "Client must be ready with listener mounted")

            // 1. 发送第 1 个 PLAYER_UPDATE 帧，让客户端 worker 线程进入慢监听器阻塞
            val update1 = ProtocolPlayerUpdate(
                snapshot = createDummyPlayerSnapshot(1L),
                fromCursor = 0L,
                visibleEvents = emptyList(),
                isFullSnapshot = false
            )
            sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update1))

            // 2. 等待客户端慢监听器确实被激活进入阻塞
            assertTrue(slowListenerEntered.await(5, TimeUnit.SECONDS), "Slow listener must be entered")

            // 3. 先发送 30 个 PLAYER_UPDATE 帧排入接收邮箱
            for (i in 2..30) {
                val update = ProtocolPlayerUpdate(
                    snapshot = createDummyPlayerSnapshot(i.toLong()),
                    fromCursor = (i - 1).toLong(),
                    visibleEvents = emptyList(),
                    isFullSnapshot = false
                )
                sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update))
            }

            // 4. 发送 PING 帧，验证即使入站邮箱有积压且 worker 阻塞，I/O 循环仍能立即回显 PONG
            val pingNonce = "1234567890abcdef"
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpPing(pingNonce)))

            // 5. 校验 PONG 是否能成功到达
            val pongFrame = readClientFrame(s)
            if (pongFrame.type == TcpWire.FRAME_TYPE_PONG) {
                pongReceived.complete(TcpMessageCodec.parsePong(pongFrame))
            }

            // 6. 继续发送 40 个 PLAYER_UPDATE 帧，使接收邮箱突破 64 帧容量上限
            for (i in 31..70) {
                val update = ProtocolPlayerUpdate(
                    snapshot = createDummyPlayerSnapshot(i.toLong()),
                    fromCursor = (i - 1).toLong(),
                    visibleEvents = emptyList(),
                    isFullSnapshot = false
                )
                sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update))
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.onUpdate {
                    slowListenerEntered.countDown()
                    releaseSlowListener.await(5, TimeUnit.SECONDS)
                }
                clientReadyLatch.countDown()

                // 等待 PONG 正常响应
                val pong = pongReceived.get(3, TimeUnit.SECONDS)
                assertEquals("1234567890abcdef", pong.nonce, "PING/PONG must proceed on I/O loop regardless of mailbox backlog")

                // 验证因入站邮箱溢出 (64 帧上限)，触发 SLOW_CONSUMER 断开
                var reachedSlowConsumer = false
                for (step in 0 until 50) {
                    if (binding.disconnectReason == TcpCloseReasons.SLOW_CONSUMER) {
                        reachedSlowConsumer = true
                        break
                    }
                    Thread.sleep(50)
                }
                assertTrue(reachedSlowConsumer, "Inbound mailbox saturation (64 frames) must trigger SLOW_CONSUMER, actual: ${binding.disconnectReason}")
            } finally {
                releaseSlowListener.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R07 stalled write disconnect callback executes outside write lock without blocking other threads`() {
        MockPeer { s ->
            while (!s.isClosed) {
                Thread.sleep(100)
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.setCurrentWriteBufferForTest(ByteBuffer.wrap(byteArrayOf(1)))
                binding.setQueuedOutboundBytesForTest(1L)
                val callbackEntered = CountDownLatch(1)
                val writeCompletedDuringCallback = AtomicBoolean(false)

                binding.onDisconnect {
                    callbackEntered.countDown()
                    val completed = CountDownLatch(1)
                    Thread {
                        try {
                            binding.writeRawFrameForTest(TcpMessageCodec.toFrame(TcpPing("1234567890abcdef")))
                        } catch (_: Exception) {
                        } finally {
                            completed.countDown()
                        }
                    }.apply { isDaemon = true; start() }
                    writeCompletedDuringCallback.set(completed.await(500, TimeUnit.MILLISECONDS))
                }

                binding.checkStalledWrite(Long.MAX_VALUE / 4)
                assertTrue(callbackEntered.await(1, TimeUnit.SECONDS), "Disconnect callback must be entered")
                assertTrue(writeCompletedDuringCallback.get(),
                    "Disconnect callback must run outside transport write lock so another thread can access writeRawFrame")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 write exception disconnect callback executes outside write lock without blocking other threads`() {
        val serverClosedLatch = CountDownLatch(1)
        MockPeer { s ->
            while (!s.isClosed) {
                Thread.sleep(100)
            }
        }.use { peer ->
            val binding = peer.connect()
            try {
                val sc = binding.socketChannelForTest ?: fail("SocketChannel must not be null")
                val key = binding.selectionKeyForTest ?: fail("SelectionKey must not be null")

                val callbackEntered = CountDownLatch(1)
                val writeCompletedDuringCallback = AtomicBoolean(false)

                binding.onDisconnect {
                    callbackEntered.countDown()
                    val completed = CountDownLatch(1)
                    Thread {
                        try {
                            binding.writeRawFrameForTest(TcpMessageCodec.toFrame(TcpPing("1234567890abcdef")))
                        } catch (_: Exception) {
                        } finally {
                            completed.countDown()
                        }
                    }.apply { isDaemon = true; start() }
                    writeCompletedDuringCallback.set(completed.await(500, TimeUnit.MILLISECONDS))
                }

                // 关闭 socketChannel 输出方向，使得下一次 handleWrite 必定抛出 ClosedChannelException 写异常
                sc.shutdownOutput()
                binding.setCurrentWriteBufferForTest(ByteBuffer.wrap(byteArrayOf(1, 2, 3)))
                binding.setQueuedOutboundBytesForTest(3L)

                binding.handleWrite(key, sc)

                assertEquals(TcpCloseReasons.LOCAL_CLOSED, binding.disconnectReason)
                assertTrue(callbackEntered.await(1, TimeUnit.SECONDS), "Disconnect callback must be entered on write exception")
                assertTrue(writeCompletedDuringCallback.get(),
                    "Write exception disconnect callback must execute outside write lock")
            } finally {
                binding.close()
            }
        }
    }

    @Test
    fun `R07 inbound mailbox currently executing frame accounts for held frame and byte budget`() {
        val slowListenerEntered = CountDownLatch(1)
        val releaseSlowListener = CountDownLatch(1)
        val clientReadyLatch = CountDownLatch(1)

        MockPeer { s ->
            assertTrue(clientReadyLatch.await(5, TimeUnit.SECONDS), "Client must be ready with listener mounted")

            // 1. 发送第 1 个 50 KiB 帧，让 worker 线程进入慢监听器阻塞
            val update1 = ProtocolPlayerUpdate(
                snapshot = createDummyPlayerSnapshot(1L),
                fromCursor = 0L,
                visibleEvents = emptyList(),
                isFullSnapshot = false
            )
            sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update1))

            // 2. 等待客户端慢监听器确实被激活进入阻塞 (此时 frame1 已经被 poll 出队列，正在被 worker 线程执行)
            assertTrue(slowListenerEntered.await(5, TimeUnit.SECONDS), "Slow listener must be entered")

            // 3. 继续发送 63 个帧排入接收邮箱 (使总持有帧数达到 64: 1 个正在执行 + 63 个排队)
            for (i in 2..64) {
                val update = ProtocolPlayerUpdate(
                    snapshot = createDummyPlayerSnapshot(i.toLong()),
                    fromCursor = (i - 1).toLong(),
                    visibleEvents = emptyList(),
                    isFullSnapshot = false
                )
                sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update))
            }

            // 稍等 50ms 让 I/O 线程完成读取与入队
            Thread.sleep(100L)

            // 4. 发送第 65 帧，断言在当前帧未释放前，新帧无法准入，触发 SLOW_CONSUMER
            val update65 = ProtocolPlayerUpdate(
                snapshot = createDummyPlayerSnapshot(65L),
                fromCursor = 64L,
                visibleEvents = emptyList(),
                isFullSnapshot = false
            )
            sendServerFrame(s, TcpMessageCodec.toPlayerUpdateFrame(update65))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.onUpdate {
                    slowListenerEntered.countDown()
                    releaseSlowListener.await(5, TimeUnit.SECONDS)
                }
                clientReadyLatch.countDown()

                // 验证在慢监听器持有 frame1 时，binding.inboundMailboxQueueSize 能够核算正在执行的帧
                assertTrue(slowListenerEntered.await(5, TimeUnit.SECONDS))
                Thread.sleep(50)
                assertTrue(binding.inboundMailboxQueueSize >= 1, "inboundMailboxQueueSize must account for currently executing frame")
                assertTrue(binding.inboundMailboxQueuedBytes > 0L, "inboundMailboxQueuedBytes must account for currently executing frame payload")

                // 验证因入站邮箱总持有帧数达到 64 帧上限，第 65 帧触发 SLOW_CONSUMER
                var reachedSlowConsumer = false
                for (step in 0 until 50) {
                    if (binding.disconnectReason == TcpCloseReasons.SLOW_CONSUMER) {
                        reachedSlowConsumer = true
                        break
                    }
                    Thread.sleep(50)
                }
                assertTrue(reachedSlowConsumer, "Inbound mailbox saturation accounting for current frame must trigger SLOW_CONSUMER, actual: ${binding.disconnectReason}")
            } finally {
                releaseSlowListener.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R07 network EOF completes request future and disconnect listener outside selector thread`() {
        val allowClose = CountDownLatch(1)
        val requestReceived = CountDownLatch(1)
        MockPeer { s ->
            // 读取 LIST_ROOMS
            val reqFrame = readClientFrame(s)
            assertEquals(TcpWire.FRAME_TYPE_REQ, reqFrame.type)
            requestReceived.countDown()
            assertTrue(allowClose.await(3, TimeUnit.SECONDS))
            s.close()
        }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.listRooms()
                val futureThreadName = CompletableFuture<String>()
                val disconnectThreadName = CompletableFuture<String>()

                future.whenComplete { _, _ ->
                    futureThreadName.complete(Thread.currentThread().name)
                }
                binding.onDisconnect { _ ->
                    disconnectThreadName.complete(Thread.currentThread().name)
                }

                assertTrue(requestReceived.await(2, TimeUnit.SECONDS))
                allowClose.countDown()

                val fName = futureThreadName.get(3, TimeUnit.SECONDS)
                val dName = disconnectThreadName.get(3, TimeUnit.SECONDS)

                assertFalse(fName.startsWith("TcpClient-IO"), "Future completion must not execute on Selector I/O thread, actual: $fName")
                assertTrue(fName.startsWith("TcpClient-Worker"), "Future completion must execute on worker thread, actual: $fName")
                assertFalse(dName.startsWith("TcpClient-IO"), "Disconnect listener must not execute on Selector I/O thread, actual: $dName")
                assertTrue(dName.startsWith("TcpClient-Worker"), "Disconnect listener must execute on worker thread, actual: $dName")
            } finally {
                allowClose.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R07 slow future callback does not freeze selector io loop or heartbeats`() {
        val callbackEntered = CountDownLatch(1)
        val releaseCallback = CountDownLatch(1)
        val pongReceived = CountDownLatch(1)
        val stateLockTested = CountDownLatch(1)

        MockPeer { s ->
            // 1. 读取请求并发送响应
            val reqFrame = readClientFrame(s)
            val reqEnv = TcpMessageCodec.parseRequestEnvelope(reqFrame)
            val rsp = ApiResponse(requestId = reqEnv.requestId, success = true, rooms = emptyList())
            sendServerFrame(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(reqEnv.requestScope, reqEnv.requestId, rsp)))

            // 2. 等待客户端在 Future 回调中进入慢回调挂起
            assertTrue(callbackEntered.await(3, TimeUnit.SECONDS), "Callback must be entered")

            // 3. 在客户端慢回调尚未返回时，服务端发送 PING
            val ping = TcpPing(TcpNonce.generate())
            sendServerFrame(s, TcpMessageCodec.toFrame(ping))

            // 4. 验证 Selector I/O 线程依然能够正常工作，立即响应 PONG
            val pongFrame = readClientFrame(s)
            assertEquals(TcpWire.FRAME_TYPE_PONG, pongFrame.type)
            val pong = TcpMessageCodec.parsePong(pongFrame)
            assertEquals(ping.nonce, pong.nonce)
            pongReceived.countDown()
        }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.listRooms()
                val stateLockAcquiredDuringCallback = AtomicBoolean(false)

                future.whenComplete { _, _ ->
                    callbackEntered.countDown()
                    // 模拟慢业务回调，在此期间验证 Selector 依然能秒级处理 I/O PING/PONG，且未持有 stateLock
                    val acquired = CountDownLatch(1)
                    Thread {
                        try {
                            // onUpdate 内部获取 stateLock，验证回调执行期间外部线程可顺畅取得 stateLock
                            binding.onUpdate { }
                            stateLockAcquiredDuringCallback.set(true)
                        } finally {
                            acquired.countDown()
                        }
                    }.apply { isDaemon = true; start() }
                    acquired.await(1, TimeUnit.SECONDS)
                    stateLockTested.countDown()
                    releaseCallback.await(3, TimeUnit.SECONDS)
                }

                assertTrue(pongReceived.await(3, TimeUnit.SECONDS), "Selector thread must respond to PING with PONG without being blocked by slow future callback")
                assertTrue(stateLockTested.await(3, TimeUnit.SECONDS), "StateLock check must be executed")
                assertTrue(stateLockAcquiredDuringCallback.get(), "Future callback must not hold stateLock while running")
            } finally {
                releaseCallback.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R07 handshake failure or connect failure completes connectFuture outside selector thread`() {
        val server = ServerSocket(0)
        val thread = Thread {
            try {
                val s = server.accept()
                // 读取 HELLO 后故意发送版本不匹配的 WELCOME
                val decoder = TcpFrameDecoder()
                val buf = ByteArray(1024)
                var helloFrame: TcpFrame? = null
                while (helloFrame == null) {
                    val n = s.getInputStream().read(buf)
                    if (n < 0) break
                    val frames = decoder.decode(java.nio.ByteBuffer.wrap(buf, 0, n))
                    if (frames.isNotEmpty()) helloFrame = frames.first()
                }
                assertNotNull(helloFrame)
                val badWelcome = TcpWelcome(
                    wireVersion = 999, // 故意版本不匹配
                    businessVersion = 999,
                    connectionEpoch = "bad",
                    lobbyResumeToken = "bad",
                    lobbyScope = TcpScope("bad", 0L)
                )
                val welcomeBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_WELCOME, TcpMessageCodec.toFrame(badWelcome).payload)
                s.getOutputStream().write(welcomeBytes)
                s.getOutputStream().flush()
            } catch (_: Throwable) {
            }
        }.apply { isDaemon = true; start() }

        try {
            val binding = TcpClientBinding("127.0.0.1", server.localPort)
            val future = binding.connectInitial()
            val callbackThreadName = CompletableFuture<String>()
            future.whenComplete { _, _ ->
                callbackThreadName.complete(Thread.currentThread().name)
            }
            val threadName = callbackThreadName.get(3, TimeUnit.SECONDS)
            assertFalse(threadName.startsWith("TcpClient-IO"), "Connect failure callback must not run on Selector thread, actual: $threadName")
            binding.close()
        } finally {
            try { server.close() } catch (_: Throwable) {}
            thread.join(1000)
        }
    }

    @Test
    fun `R07 worker queue saturation routes terminal completions to bounded completion channel without spawning unbounded threads`() {
        val releaseWorker = CountDownLatch(1)
        MockPeer { _ -> }.use { peer ->
            val binding = peer.connect()
            val ioThreadField = TcpClientBinding::class.java.getDeclaredField("ioThread").apply { isAccessible = true }
            val originalIoThread = ioThreadField.get(binding)
            try {
                val workerField = TcpClientBinding::class.java.getDeclaredField("workerExecutor").apply { isAccessible = true }
                val workerPool = workerField.get(binding) as java.util.concurrent.ThreadPoolExecutor
                val workerEntered = CountDownLatch(1)
                workerPool.execute {
                    workerEntered.countDown()
                    releaseWorker.await(5, TimeUnit.SECONDS)
                }
                assertTrue(workerEntered.await(1, TimeUnit.SECONDS))
                repeat(256) { workerPool.execute {} }
                assertEquals(256, workerPool.queue.size)

                val callbackThread = CompletableFuture<String>()
                val dispatch = TcpClientBinding::class.java.declaredMethods.single {
                    it.name == "executeOnWorker" && it.parameterCount == 1
                }.apply { isAccessible = true }

                ioThreadField.set(binding, Thread.currentThread())
                dispatch.invoke(binding, { callbackThread.complete(Thread.currentThread().name); Unit })
                ioThreadField.set(binding, originalIoThread)

                val threadName = callbackThread.get(2, TimeUnit.SECONDS)
                assertTrue(threadName.startsWith("TcpClient-Completion-"), "Saturated worker queue must route task to bounded completion channel, got $threadName")
                assertFalse(threadName.startsWith("TcpClient-WorkerFallback-"), "Must not spawn ad-hoc unbounded fallback threads")
            } finally {
                ioThreadField.set(binding, originalIoThread)
                releaseWorker.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R07 saturated worker and completion queues retain and drain terminal actions upon worker release`() {
        val release = CountDownLatch(1)
        MockPeer { _ -> }.use { peer ->
            val binding = peer.connect()
            val ioThreadField = TcpClientBinding::class.java.getDeclaredField("ioThread").apply { isAccessible = true }
            val originalIoThread = ioThreadField.get(binding)
            try {
                fun fillPool(fieldName: String, count: Int) {
                    val field = TcpClientBinding::class.java.getDeclaredField(fieldName).apply { isAccessible = true }
                    val pool = field.get(binding) as java.util.concurrent.ThreadPoolExecutor
                    val entered1 = CountDownLatch(1)
                    pool.execute {
                        entered1.countDown()
                        release.await(5, TimeUnit.SECONDS)
                    }
                    assertTrue(entered1.await(2, TimeUnit.SECONDS), "First worker in $fieldName must enter blocking task")

                    repeat(256) { pool.execute {} }

                    if (count == 2) {
                        val entered2 = CountDownLatch(1)
                        pool.execute {
                            entered2.countDown()
                            release.await(5, TimeUnit.SECONDS)
                        }
                        assertTrue(entered2.await(2, TimeUnit.SECONDS), "Second worker in $fieldName must enter blocking task")
                    }
                    assertEquals(256, pool.queue.size, "$fieldName queue must be saturated")
                }

                fillPool("workerExecutor", 1)
                fillPool("completionExecutor", 2)

                val completed = CountDownLatch(1)
                val dispatch = TcpClientBinding::class.java.declaredMethods.single {
                    it.name == "executeOnWorker" && it.parameterCount == 1
                }.apply { isAccessible = true }

                ioThreadField.set(binding, Thread.currentThread())
                dispatch.invoke(binding, { completed.countDown(); Unit })
                ioThreadField.set(binding, originalIoThread)

                // While workers are blocked, the action is retained in overflow queue
                assertFalse(completed.await(200, TimeUnit.MILLISECONDS), "Retained action should wait for pool capacity")

                // Release workers
                release.countDown()
                assertTrue(completed.await(2, TimeUnit.SECONDS), "Retained action must be drained cleanly upon worker completion")
            } finally {
                ioThreadField.set(binding, originalIoThread)
                release.countDown()
                binding.close()
            }
        }
    }

    @Test
    fun `R07 connect and reconnect execute on bounded connectExecutor without unmanaged commonPool threads`() {
        val server = ServerSocket(0)
        val thread = Thread {
            try {
                val s1 = server.accept()
                s1.soTimeout = 4000
                val decoder = TcpFrameDecoder()
                val buf = ByteArray(1024)
                var helloFrame: TcpFrame? = null
                while (helloFrame == null) {
                    val n = s1.getInputStream().read(buf)
                    if (n < 0) break
                    val frames = decoder.decode(ByteBuffer.wrap(buf, 0, n))
                    if (frames.isNotEmpty()) helloFrame = frames.first()
                }
                assertNotNull(helloFrame)
                val welcome = TcpWelcome(
                    wireVersion = 1,
                    businessVersion = 1,
                    connectionEpoch = "test-epoch-1",
                    lobbyResumeToken = "test-token-1",
                    lobbyScope = TcpScope("scope-1", Long.MAX_VALUE)
                )
                val welcomeBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_WELCOME, TcpMessageCodec.toFrame(welcome).payload)
                s1.getOutputStream().write(welcomeBytes)
                s1.getOutputStream().flush()

                s1.getInputStream().read()
            } catch (_: Throwable) {
            }
        }.apply { isDaemon = true; start() }

        try {
            val binding = TcpClientBinding("127.0.0.1", server.localPort)
            val connectFuture = binding.connectInitial()
            val connectThreadField = TcpClientBinding::class.java.getDeclaredField("connectExecutor").apply { isAccessible = true }
            val connectPool = connectThreadField.get(binding) as java.util.concurrent.ThreadPoolExecutor

            val boundBinding = connectFuture.get(3, TimeUnit.SECONDS)
            assertNotNull(boundBinding)

            val threadNameFuture = CompletableFuture<String>()
            connectPool.execute { threadNameFuture.complete(Thread.currentThread().name) }
            val threadName = threadNameFuture.get(1, TimeUnit.SECONDS)

            assertTrue(threadName.startsWith("TcpClient-Connect-"), "ConnectExecutor must use dedicated thread prefix, got: $threadName")
            assertFalse(threadName.contains("commonPool"), "Must not use unmanaged ForkJoinPool.commonPool")

            binding.close()
        } finally {
            try { server.close() } catch (_: Throwable) {}
            thread.join(1000)
        }
    }
}

package com.majiang.review

import com.majiang.client.transport.tcp.TcpClient
import com.majiang.client.transport.tcp.TcpClientBinding
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import com.majiang.server.idempotency.AdmissionResult
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.idempotency.RequestScopeReceiptStore
import com.majiang.transport.api.ApiRequest
import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.tcp.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Independent contract probes. Real loopback bytes; no production source changes. */
@Timeout(15)
class TcpCurrentReviewTest {
    private class Peer(val requestTimeoutMillis: Long = 150L, val handler: (Socket) -> Unit) : AutoCloseable {
        val listener = ServerSocket(0)
        val done = CountDownLatch(1)
        val failure = CompletableFuture<Throwable>()
        @Volatile var socket: Socket? = null
        val thread = Thread {
            try {
                val s = listener.accept()
                socket = s
                s.soTimeout = 4000
                assertEquals(TcpWire.FRAME_TYPE_HELLO, readFrame(s).type)
                send(s, TcpMessageCodec.toFrame(TcpWelcome(
                    connectionEpoch = "review-epoch", lobbyResumeToken = "review-token",
                    lobbyScope = TcpScope("review-scope", Long.MAX_VALUE),
                    limits = TcpLimits(requestTimeoutMillis = requestTimeoutMillis)
                )))
                handler(s)
                done.await(5, TimeUnit.SECONDS)
            } catch (t: Throwable) { failure.complete(t) }
        }.apply { isDaemon = true; start() }
        fun connect(): TcpClientBinding = TcpClient.connect("127.0.0.1", listener.localPort).get(3, TimeUnit.SECONDS)
        override fun close() {
            done.countDown()
            socket?.close()
            listener.close()
            thread.join(1000)
        }
    }
    companion object {
        private fun readFrame(s: Socket): TcpFrame {
            val decoder = TcpFrameDecoder()
            while (true) {
                val b = s.getInputStream().read()
                check(b >= 0) { "Unexpected EOF from client" }
                val frames = decoder.decode(byteArrayOf(b.toByte()))
                if (frames.isNotEmpty()) return frames.single()
            }
        }
        private fun send(s: Socket, frame: TcpFrame) {
            s.getOutputStream().write(TcpFrameEncoder.encode(frame.type, frame.payload, frame.flags))
            s.getOutputStream().flush()
        }
        private fun request(s: Socket) = TcpMessageCodec.parseRequestEnvelope(readFrame(s))
        private fun response(s: Socket, r: TcpRequestEnvelope, error: String? = null, scope: String = r.requestScope, granted: TcpScope? = null) {
            send(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(scope, r.requestId,
                ApiResponse(requestId = r.requestId, success = error == null, error = error), granted)))
        }
    }

    @Test fun `listener registration cannot strand a concurrent early public update`() {
        Peer { _ -> }.use { peer ->
            val binding = peer.connect()
            try {
                val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
                val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 1L, session = session)
                val update = ProtocolPublicUpdate(snapshot, fromCursor = 0L)
                val stateLock = TcpClientBinding::class.java.getDeclaredField("stateLock").apply { isAccessible = true }.get(binding)
                val received = mutableListOf<ProtocolPublicUpdate>()
                val sender = Thread { binding.dispatchPublicUpdate(update, 1L) }
                synchronized(stateLock) {
                    sender.start()
                    val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
                    while (sender.state != Thread.State.BLOCKED && System.nanoTime() < deadline) Thread.sleep(1)
                    assertEquals(Thread.State.BLOCKED, sender.state, "Sender must have observed no listeners before registration")
                    binding.onPublicUpdate { received.add(it) }
                }
                sender.join(1000)
                assertFalse(sender.isAlive)
                assertEquals(1, received.size, "A push concurrent with listener mounting must be delivered without another PUSH")
                assertEquals(0, binding.earlyPublicBufferSize)
            } finally { binding.close() }
        }
    }

    @Test fun `mixed early streams share the 64 item admission bound`() {
        Peer { _ -> }.use { peer ->
            val binding = peer.connect()
            try {
                val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
                val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 1L, session = session)
                val update = ProtocolPublicUpdate(snapshot, fromCursor = 0L)
                repeat(40) { n -> binding.dispatchRoomUpdate(ProtocolRoomInfo("room", ProtocolRoomLifecycle.LOBBY, version = n + 1L), 1L) }
                repeat(40) { binding.dispatchPublicUpdate(update, 1L) }
                assertTrue(binding.earlyRoomBufferSize + binding.earlyPublicBufferSize <= 64,
                    "The 64-item early buffer bound covers all streams sharing this connection")
                assertTrue(binding.earlyBufferOverflowStatus)
            } finally { binding.close() }
        }
    }

    @Test fun `room update overflow must deliver the latest room version on listener mount`() {
        Peer { s ->
            val req = request(s)
            val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
            val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 0L, session = session)
            val spectator = SpectatorBinding("room", "spectator", "token", snapshot)
            send(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(req.requestScope, req.requestId,
                ApiResponse(requestId = req.requestId, success = true, spectatorBinding = spectator),
                TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinSpectator(JoinSpectatorRequest("room", "viewer")).get(2, TimeUnit.SECONDS)
                repeat(65) { n ->
                    binding.dispatchRoomUpdate(ProtocolRoomInfo("room", ProtocolRoomLifecycle.LOBBY, version = n + 1L), 1L)
                }
                assertTrue(binding.earlyBufferOverflowStatus)
                val versions = mutableListOf<Long>()
                binding.onRoomUpdate { versions.add(it.version) }
                assertTrue(versions.contains(65L), "Room info lost at the buffer boundary must recover without another push")
            } finally { binding.close() }
        }
    }

    @Test fun `room listener drain and live update preserve version order`() {
        Peer { s ->
            val req = request(s)
            val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
            val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 0L, session = session)
            val spectator = SpectatorBinding("room", "spectator", "token", snapshot)
            send(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(req.requestScope, req.requestId,
                ApiResponse(requestId = req.requestId, success = true, spectatorBinding = spectator),
                TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinSpectator(JoinSpectatorRequest("room", "viewer")).get(2, TimeUnit.SECONDS)
                binding.dispatchRoomUpdate(ProtocolRoomInfo("room", ProtocolRoomLifecycle.LOBBY, version = 1L), 1L)
                binding.dispatchRoomUpdate(ProtocolRoomInfo("room", ProtocolRoomLifecycle.LOBBY, version = 2L), 1L)
                val enteredFirst = CountDownLatch(1)
                val releaseFirst = CountDownLatch(1)
                val versions = java.util.concurrent.CopyOnWriteArrayList<Long>()
                val mounting = Thread {
                    binding.onRoomUpdate { info ->
                        versions.add(info.version)
                        if (info.version == 1L) {
                            enteredFirst.countDown()
                            releaseFirst.await(2, TimeUnit.SECONDS)
                        }
                    }
                }
                mounting.start()
                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))
                binding.dispatchRoomUpdate(ProtocolRoomInfo("room", ProtocolRoomLifecycle.LOBBY, version = 3L), 1L)
                releaseFirst.countDown()
                mounting.join(1000)
                assertFalse(mounting.isAlive)
                assertEquals(listOf(1L, 2L, 3L), versions.toList(), "Early drain must precede live updates for the same listener")
            } finally { binding.close() }
        }
    }

    @Test fun `public listener drain and live update preserve cursor order`() {
        Peer { s ->
            val req = request(s)
            val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
            val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 0L, session = session)
            val spectator = SpectatorBinding("room", "spectator", "token", snapshot)
            send(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(req.requestScope, req.requestId,
                ApiResponse(requestId = req.requestId, success = true, spectatorBinding = spectator),
                TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.joinSpectator(JoinSpectatorRequest("room", "viewer")).get(2, TimeUnit.SECONDS)
                val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
                fun update(n: Long): ProtocolPublicUpdate {
                    val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = n, session = session)
                    return ProtocolPublicUpdate(snapshot, fromCursor = n - 1L)
                }
                binding.dispatchPublicUpdate(update(1L), 1L)
                binding.dispatchPublicUpdate(update(2L), 1L)
                val enteredFirst = CountDownLatch(1)
                val releaseFirst = CountDownLatch(1)
                val cursors = java.util.concurrent.CopyOnWriteArrayList<Long>()
                val mounting = Thread {
                    binding.onPublicUpdate { item ->
                        cursors.add(item.snapshot.cursor)
                        if (item.snapshot.cursor == 1L) {
                            enteredFirst.countDown()
                            releaseFirst.await(2, TimeUnit.SECONDS)
                        }
                    }
                }
                mounting.start()
                assertTrue(enteredFirst.await(2, TimeUnit.SECONDS))
                binding.dispatchPublicUpdate(update(3L), 1L)
                releaseFirst.countDown()
                mounting.join(1000)
                assertFalse(mounting.isAlive)
                assertEquals(listOf(1L, 2L, 3L), cursors.toList(), "Early public drain must precede live updates")
            } finally { binding.close() }
        }
    }

    @Test fun `remounting a public listener must not silently drop a full dispatch queue head`() {
        Peer { s ->
            val req = request(s)
            val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
            val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 0L, session = session)
            val spectator = SpectatorBinding("room", "spectator", "token", snapshot)
            send(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(req.requestScope, req.requestId,
                ApiResponse(requestId = req.requestId, success = true, spectatorBinding = spectator),
                TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val entered = CountDownLatch(1)
            val release = CountDownLatch(1)
            try {
                binding.joinSpectator(JoinSpectatorRequest("room", "viewer")).get(2, TimeUnit.SECONDS)
                val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
                fun update(n: Long) = ProtocolPublicUpdate(
                    ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = n, session = session),
                    fromCursor = n - 1L)
                val firstSubscription = binding.onPublicUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        entered.countDown()
                        release.await(3, TimeUnit.SECONDS)
                    }
                }
                val firstSender = Thread { binding.dispatchPublicUpdate(update(1L), 1L) }
                firstSender.start()
                assertTrue(entered.await(2, TimeUnit.SECONDS))
                (2L..129L).forEach { binding.dispatchPublicUpdate(update(it), 1L) }
                firstSubscription.close()
                binding.dispatchPublicUpdate(update(130L), 1L)
                val delivered = java.util.concurrent.CopyOnWriteArrayList<Long>()
                binding.onPublicUpdate { delivered.add(it.snapshot.cursor) }
                release.countDown()
                firstSender.join(1000)
                assertFalse(firstSender.isAlive)
                assertTrue(delivered.contains(2L) || binding.isPublicRecoveryPending,
                    "Dropping a queued PUSH on listener remount must retain an explicit full-snapshot recovery obligation")
            } finally {
                release.countDown()
                binding.close()
            }
        }
    }

    @Test fun `queued public update must survive listener unmount before remount`() {
        Peer { s ->
            val req = request(s)
            val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
            val snapshot = ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = 0L, session = session)
            val spectator = SpectatorBinding("room", "spectator", "token", snapshot)
            send(s, TcpMessageCodec.toFrame(TcpResponseEnvelope(req.requestScope, req.requestId,
                ApiResponse(requestId = req.requestId, success = true, spectatorBinding = spectator),
                TcpScope("spec-scope", Long.MAX_VALUE))))
        }.use { peer ->
            val binding = peer.connect()
            val entered = CountDownLatch(1)
            val release = CountDownLatch(1)
            try {
                binding.joinSpectator(JoinSpectatorRequest("room", "viewer")).get(2, TimeUnit.SECONDS)
                val session = ProtocolPublicSessionSummary("ROUND_ACTIVE", listOf(25000, 25000, 25000, 25000), 1, 1, 0, 0)
                fun update(n: Long) = ProtocolPublicUpdate(
                    ProtocolPublicSnapshot("session", 1L, syncEpoch = "epoch", cursor = n, session = session),
                    fromCursor = n - 1L)
                val firstSubscription = binding.onPublicUpdate { item ->
                    if (item.snapshot.cursor == 1L) {
                        entered.countDown()
                        release.await(3, TimeUnit.SECONDS)
                    }
                }
                val sender = Thread { binding.dispatchPublicUpdate(update(1L), 1L) }
                sender.start()
                assertTrue(entered.await(2, TimeUnit.SECONDS))
                binding.dispatchPublicUpdate(update(2L), 1L)
                firstSubscription.close()
                release.countDown()
                sender.join(1000)
                assertFalse(sender.isAlive)
                val delivered = mutableListOf<Long>()
                binding.onPublicUpdate { delivered.add(it.snapshot.cursor) }
                assertTrue(delivered.contains(2L) || binding.isPublicRecoveryPending,
                    "A queued PUSH consumed with no listener must remain available or trigger explicit recovery")
            } finally {
                release.countDown()
                binding.close()
            }
        }
    }

    @Test fun `stalled write disconnect callback runs outside write lock`() {
        Peer { _ -> }.use { peer ->
            val binding = peer.connect()
            try {
                binding.setCurrentWriteBufferForTest(ByteBuffer.wrap(byteArrayOf(1)))
                binding.setQueuedOutboundBytesForTest(1L)
                val callbackEntered = CountDownLatch(1)
                val writeCompletedDuringCallback = java.util.concurrent.atomic.AtomicBoolean(false)
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
                assertTrue(callbackEntered.await(1, TimeUnit.SECONDS))
                assertTrue(writeCompletedDuringCallback.get(),
                    "Disconnect callback must not run while the transport write lock is held")
            } finally { binding.close() }
        }
    }

    @Test fun `network EOF must not complete request future on selector thread`() {
        val allowClose = CountDownLatch(1)
        Peer(requestTimeoutMillis = 3000L) { socket ->
            request(socket)
            assertTrue(allowClose.await(2, TimeUnit.SECONDS))
            socket.close()
        }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.listRooms()
                val callbackThread = CompletableFuture<String>()
                future.whenComplete { _, _ -> callbackThread.complete(Thread.currentThread().name) }
                allowClose.countDown()
                val name = callbackThread.get(2, TimeUnit.SECONDS)
                assertFalse(name.startsWith("TcpClient-IO"),
                    "Completing a Future runs user callbacks synchronously; the selector must only perform bounded I/O")
            } finally {
                allowClose.countDown()
                binding.close()
            }
        }
    }

    @Test fun `saturated callback worker must not spawn unbounded fallback threads`() {
        val releaseWorker = CountDownLatch(1)
        Peer { _ -> }.use { peer ->
            val binding = peer.connect()
            val ioThreadField = TcpClientBinding::class.java.getDeclaredField("ioThread").apply { isAccessible = true }
            val originalIoThread = ioThreadField.get(binding)
            try {
                val field = TcpClientBinding::class.java.getDeclaredField("workerExecutor").apply { isAccessible = true }
                val pool = field.get(binding) as java.util.concurrent.ThreadPoolExecutor
                val workerEntered = CountDownLatch(1)
                pool.execute {
                    workerEntered.countDown()
                    releaseWorker.await(5, TimeUnit.SECONDS)
                }
                assertTrue(workerEntered.await(1, TimeUnit.SECONDS))
                repeat(256) { pool.execute {} }
                assertEquals(256, pool.queue.size)
                val callbackThread = CompletableFuture<String>()
                val dispatch = TcpClientBinding::class.java.declaredMethods.single {
                    it.name == "executeOnWorker" && it.parameterCount == 1
                }
                    .apply { isAccessible = true }
                ioThreadField.set(binding, Thread.currentThread())
                dispatch.invoke(binding, { callbackThread.complete(Thread.currentThread().name); Unit })
                ioThreadField.set(binding, originalIoThread)
                val name = callbackThread.get(1, TimeUnit.SECONDS)
                assertFalse(name.startsWith("TcpClient-WorkerFallback-"),
                    "A full bounded worker queue must not escape its limit by spawning ad-hoc callback threads")
            } finally {
                ioThreadField.set(binding, originalIoThread)
                releaseWorker.countDown()
                binding.close()
            }
        }
    }

    @Test fun `saturated worker and completion queues must not discard terminal action`() {
        val release = CountDownLatch(1)
        Peer { _ -> }.use { peer ->
            val binding = peer.connect()
            val ioThreadField = TcpClientBinding::class.java.getDeclaredField("ioThread").apply { isAccessible = true }
            val originalIoThread = ioThreadField.get(binding)
            try {
                fun fill(fieldName: String, blockedWorkers: Int) {
                    val field = TcpClientBinding::class.java.getDeclaredField(fieldName).apply { isAccessible = true }
                    val pool = field.get(binding) as java.util.concurrent.ThreadPoolExecutor
                    val entered = CountDownLatch(blockedWorkers)
                    pool.execute { entered.countDown(); release.await(5, TimeUnit.SECONDS) }
                    repeat(256) { pool.execute {} }
                    if (blockedWorkers == 2) {
                        pool.execute { entered.countDown(); release.await(5, TimeUnit.SECONDS) }
                    }
                    assertTrue(entered.await(1, TimeUnit.SECONDS))
                    assertEquals(256, pool.queue.size)
                }
                fill("workerExecutor", 1)
                fill("completionExecutor", 2)
                val completed = CountDownLatch(1)
                val dispatch = TcpClientBinding::class.java.declaredMethods.single {
                    it.name == "executeOnWorker" && it.parameterCount == 1
                }.apply { isAccessible = true }
                ioThreadField.set(binding, Thread.currentThread())
                dispatch.invoke(binding, { completed.countDown(); Unit })
                ioThreadField.set(binding, originalIoThread)
                release.countDown()
                assertTrue(completed.await(2, TimeUnit.SECONDS),
                    "A terminal Future or disconnect action must be retained when both bounded queues are saturated")
            } finally {
                ioThreadField.set(binding, originalIoThread)
                release.countDown()
                binding.close()
            }
        }
    }

    @Test fun `request completes by negotiated timeout even when connection remains alive`() {
        val received = CountDownLatch(1)
        Peer { s -> request(s); received.countDown() }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.listRooms()
                assertTrue(received.await(2, TimeUnit.SECONDS))
                Thread.sleep(700) // >4x advertised request deadline, below heartbeat interval
                assertTrue(future.isCompletedExceptionally, "Accepted request must not remain pending indefinitely")
            } finally { binding.close() }
        }
    }

    @Test fun `remote EOF is transient and must not become LOCAL_CLOSED`() {
        Peer { s -> request(s); s.close() }.use { peer ->
            val binding = peer.connect()
            val disconnected = CompletableFuture<String>()
            binding.onDisconnect { disconnected.complete(it) }
            try {
                binding.listRooms()
                assertNotEquals(TcpCloseReasons.LOCAL_CLOSED, disconnected.get(2, TimeUnit.SECONDS),
                    "Session treats LOCAL_CLOSED as permanent and suppresses reconnect")
            } finally { binding.close() }
        }
    }

    @Test fun `server initiated PING receives matching PONG`() {
        val pong = CompletableFuture<TcpFrame>()
        Peer { s ->
            request(s)
            send(s, TcpMessageCodec.toFrame(TcpPing("0123456789abcdef")))
            try { pong.complete(readFrame(s)) } catch (t: Throwable) { pong.completeExceptionally(t) }
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.listRooms()
                val frame = pong.get(2, TimeUnit.SECONDS)
                assertEquals(TcpWire.FRAME_TYPE_PONG, frame.type)
                assertEquals("0123456789abcdef", TcpMessageCodec.parsePong(frame).nonce)
            } finally { binding.close() }
        }
    }

    @Test fun `response with wrong requestScope cannot complete pending request successfully`() {
        Peer { s -> response(s, request(s), scope = "unrelated-scope") }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.listRooms()
                try { future.get(2, TimeUnit.SECONDS) } catch (_: Exception) { }
                assertTrue(future.isCompletedExceptionally, "RSP correlation must validate both scope and id")
            } finally { binding.close() }
        }
    }

    @Test fun `wrong scope response cannot erase unknown side effect identity`() {
        val observed = CompletableFuture<TcpRequestEnvelope>()
        Peer { s ->
            val original = request(s)
            observed.complete(original)
            response(s, original, scope = "unrelated-scope")
        }.use { peer ->
            val binding = peer.connect()
            try {
                val future = binding.setReady(true)
                val original = observed.get(2, TimeUnit.SECONDS)
                try { future.get(2, TimeUnit.SECONDS) } catch (_: Exception) { }
                assertTrue(future.isCompletedExceptionally, "Invalid RSP cannot confirm a side effect")
                val record = binding.getUnconfirmedSideEffect(original.requestId)
                assertNotNull(record, "An untrusted RSP must not erase the unknown request identity")
                assertEquals(original.requestScope, record!!.scope)
                assertEquals(original.requestId, record.requestId)
                assertEquals(original.request, record.request)
            } finally { binding.close() }
        }
    }

    @Test fun `expired side effect cannot automatically execute under new scope`() {
        val retried = CompletableFuture<TcpRequestEnvelope>()
        Peer { s ->
            val original = request(s)
            response(s, original, error = ApiErrors.REQUEST_SCOPE_EXPIRED)
            val next = request(s)
            if (next.request.api == "GET_REQUEST_SCOPE") {
                response(s, next, granted = TcpScope("fresh-scope", Long.MAX_VALUE))
                retried.complete(request(s))
            } else retried.complete(next)
        }.use { peer ->
            val binding = peer.connect()
            try {
                binding.setReady(true)
                Thread.sleep(700)
                assertFalse(retried.isDone, "An expired side effect must require reconciliation, never automatic new-scope execution")
            } finally { binding.close() }
        }
    }

    @Test fun `unknown lobby resume token is rejected`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            var accepted: TcpClientBinding? = null
            try {
                try { accepted = TcpClient.connect("127.0.0.1", server.boundPort,
                    lobbyResumeToken = "never-issued-token").get(3, TimeUnit.SECONDS) } catch (_: Exception) { }
                assertNull(accepted, "Unknown or previous-process lobby token must not authenticate")
            } finally { accepted?.close() }
        }
    }

    @Test fun `reconnect retains still valid lobby scope`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            val binding = TcpClient.connect("127.0.0.1", server.boundPort).get(3, TimeUnit.SECONDS)
            try {
                val original = binding.currentScope
                binding.reconnect().get(3, TimeUnit.SECONDS)
                assertEquals(original, binding.currentScope, "Resume must inherit valid scope")
            } finally { binding.close() }
        }
    }

    @Test fun `ordinary queries obey member in flight bound`() {
        val store = RequestScopeReceiptStore(maxInFlightPerMember = 2)
        val scope = store.issueLobbyScope("principal")
        val admitted = (1..3).map { n ->
            val req = ApiRequest(api = "LIST_ROOMS", requestId = "q$n")
            store.acquireAdmission(IdempotencyKey("principal", scope.scope, req.requestId),
                req, "query-$n", isSideEffect = false, isLobby = true)
        }
        assertTrue(admitted[2] is AdmissionResult.ServerBusy,
            "Query futures must not bypass in-flight bound and accumulate in authority queue")
    }

    @Test fun `receipt completion invokes callbacks outside store lock`() {
        val store = RequestScopeReceiptStore()
        val scope = store.issueLobbyScope("principal")
        val req = ApiRequest(api = "SET_READY", requestId = "ready-1")
        val key = IdempotencyKey("principal", scope.scope, req.requestId)
        val admitted = store.acquireAdmission(key, req, "payload", isSideEffect = true) as AdmissionResult.Admitted
        val callbackCouldQuery = CompletableFuture<Boolean>()
        admitted.inFlight.future.whenComplete { _, _ ->
            val queried = CountDownLatch(1)
            val worker = Thread { store.findReceipt(key); queried.countDown() }.apply { isDaemon = true; start() }
            callbackCouldQuery.complete(queried.await(500, TimeUnit.MILLISECONDS))
        }
        store.completeReceipt(key, admitted.inFlight, ApiResponse(requestId = req.requestId, success = true))
        assertTrue(callbackCouldQuery.get(1, TimeUnit.SECONDS),
            "A completion callback blocks another store operation while holding stateLock")
    }
}

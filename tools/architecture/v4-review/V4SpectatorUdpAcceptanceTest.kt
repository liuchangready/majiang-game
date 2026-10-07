package com.majiang.v4lobby

import com.google.gson.JsonParser
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.ServerConnectionSession
import com.majiang.client.transport.udp.UdpClient
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ServerConnection
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.udp.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Real sockets and complete-message losses; no fabricated PUBLIC snapshots or client-only permission checks. */
@Timeout(55)
class V4SpectatorUdpAcceptanceTest {
    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    private class PublicProxy(serverPort: Int) : AutoCloseable {
        private val socket = DatagramSocket(0, InetAddress.getLoopbackAddress())
        private val serverAddress = InetSocketAddress(InetAddress.getLoopbackAddress(), serverPort)
        private val timer = Executors.newSingleThreadScheduledExecutor { runnable ->
            Thread(runnable, "review-public-fragment-timer").apply { isDaemon = true }
        }
        private val fragmenter = Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
                val future = timer.schedule(task, delayMillis, TimeUnit.MILLISECONDS)
                return Cancellable { future.cancel(false) }
            }
        })
        private val fragments = mutableMapOf<Pair<Int, Int>, MutableList<ByteArray>>()
        @Volatile private var clientAddress: SocketAddress? = null
        @Volatile var disconnected = false
        @Volatile var dropPausedOnce = false
        @Volatile var loseFirstPublicAck = false
        @Volatile var dropAllPublic = false
        @Volatile var dropOrdinaryPublic = false
        @Volatile var holdCursor: Long? = null
        private val didDropPaused = AtomicBoolean(false)
        private val droppedAck = AtomicBoolean(false)
        private val pausedSequences = ConcurrentHashMap.newKeySet<Int>()
        private val heldLock = Any()
        private var held: List<ByteArray>? = null
        @Volatile private var lastFullDatagrams: List<ByteArray>? = null
        @Volatile private var lastFullSequence: Int? = null
        val fullAcknowledgements = CountDownLatch(3)
        val droppedPaused = CountDownLatch(1)
        val heldPublic = CountDownLatch(1)
        val publicResend = CountDownLatch(1)
        val repeatedAckForwarded = CountDownLatch(1)
        val pausedMessages = AtomicInteger()
        val spectatorResumes = AtomicInteger()
        val playerResumes = AtomicInteger()
        val failure = CompletableFuture<Throwable>()
        val rawReplies = ConcurrentHashMap<String, CompletableFuture<Pair<ApiResponse, UdpFrame>>>()
        val port: Int get() = socket.localPort

        private fun forward(bytes: ByteArray, target: SocketAddress) {
            socket.send(DatagramPacket(bytes, bytes.size, target))
        }

        private val worker = Thread({
            while (!socket.isClosed) {
                try {
                    val packet = DatagramPacket(ByteArray(2048), 2048)
                    socket.receive(packet)
                    val bytes = packet.data.copyOfRange(packet.offset, packet.offset + packet.length)
                    val fromServer = packet.socketAddress == serverAddress
                    if (!fromServer) clientAddress = packet.socketAddress
                    if (disconnected) continue
                    val destination = if (fromServer) clientAddress ?: continue else serverAddress
                    val frame = UdpFrame.decode(bytes)
                    if (!fromServer) {
                        if (frame.packetType == UdpPacketType.ACK && frame.ackSeq == lastFullSequence) {
                            fullAcknowledgements.countDown()
                        }
                        if (frame.packetType == UdpPacketType.ACK && frame.ackSeq in pausedSequences) {
                            if (loseFirstPublicAck && droppedAck.compareAndSet(false, true)) continue
                            forward(bytes, destination)
                            if (droppedAck.get()) repeatedAckForwarded.countDown()
                            continue
                        }
                        if (frame.packetType == UdpPacketType.REQ || frame.packetType == UdpPacketType.RESEND) {
                            val obj = JsonParser.parseString(frame.payload.toString(Charsets.UTF_8)).asJsonObject
                            when (obj.get("api")?.asString) {
                                "RESUME_SPECTATOR" -> spectatorResumes.incrementAndGet()
                                "RESUME_SESSION" -> playerResumes.incrementAndGet()
                            }
                            if (frame.packetType == UdpPacketType.RESEND && obj.get("type")?.asString == "PUBLIC_RESEND") {
                                publicResend.countDown()
                            }
                        }
                        forward(bytes, destination)
                        continue
                    }

                    val complete: UdpFrame
                    val datagrams: List<ByteArray>
                    if (frame.packetType == UdpPacketType.FRAG) {
                        val chunk = Fragmenter.decodeSubheader(frame.payload)
                        val key = frame.connectionId to chunk.msgId
                        fragments.getOrPut(key) { mutableListOf() }.add(bytes)
                        complete = fragmenter.reassemble(frame, serverAddress) ?: continue
                        datagrams = fragments.remove(key)!!
                    } else {
                        complete = frame
                        datagrams = listOf(bytes)
                    }
                    if (complete.packetType == UdpPacketType.RSP) {
                        val response = ApiMessageCodec.decodeResponse(complete.payload.toString(Charsets.UTF_8))
                        response.requestId?.let { rawReplies[it]?.complete(response to complete) }
                    }
                    if (complete.packetType == UdpPacketType.PUSH) {
                        val obj = JsonParser.parseString(complete.payload.toString(Charsets.UTF_8)).asJsonObject
                        if (obj.get("type")?.asString == "PUBLIC_UPDATE") {
                            val snap = obj.getAsJsonObject("update").getAsJsonObject("snapshot")
                            if (obj.getAsJsonObject("update").get("isFullSnapshot")?.asBoolean == true) {
                                lastFullDatagrams = datagrams
                                lastFullSequence = complete.seq
                            } else if (dropOrdinaryPublic) {
                                continue
                            }
                            if (snap.get("paused").asBoolean) {
                                pausedSequences.add(complete.seq)
                                pausedMessages.incrementAndGet()
                                if (dropPausedOnce && didDropPaused.compareAndSet(false, true)) {
                                    droppedPaused.countDown()
                                    continue
                                }
                            }
                            if (dropAllPublic) continue
                            if (snap.get("cursor").asLong == holdCursor) {
                                synchronized(heldLock) { if (held == null) held = datagrams }
                                heldPublic.countDown()
                                continue
                            }
                        }
                    }
                    datagrams.forEach { forward(it, destination) }
                } catch (error: Throwable) {
                    if (!socket.isClosed) failure.complete(error)
                    break
                }
            }
        }, "review-public-message-proxy").apply { isDaemon = true; start() }

        fun releaseHeldTwice() {
            holdCursor = null
            val datagrams = synchronized(heldLock) { held.also { held = null } }
                ?: error("Proxy did not capture a complete PUBLIC update")
            val target = requireNotNull(clientAddress)
            repeat(2) { datagrams.forEach { forward(it, target) } }
        }

        fun replayFullTwice() {
            val datagrams = requireNotNull(lastFullDatagrams) { "No genuine full PUBLIC message captured" }
            val target = requireNotNull(clientAddress)
            repeat(2) { datagrams.forEach { forward(it, target) } }
        }

        override fun close() {
            socket.close()
            worker.join(1000)
            fragmenter.close()
            timer.shutdownNow()
        }
    }

    @Test fun typedPublicAndRoomUpdatesRecoverFinalPublicLossAndLostAckWithoutAnotherOperation() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                val room = host.createRoom(CreateRoomRequest()).await()
                PublicProxy(server.boundPort).use { proxy ->
                    UdpClient.connect("127.0.0.1", proxy.port).await().use { transport ->
                        val spectator: ServerConnection = transport
                        val baseline = spectator.joinSpectator(JoinSpectatorRequest(room.roomId, "Review viewer")).await()
                        assertEquals(room.snapshot.sessionId, baseline.snapshot.sessionId)
                        val readyRoom = CompletableFuture<Boolean>()
                        spectator.onRoomUpdate { info ->
                            if (info.roomId == room.roomId && info.members.any { it.seat == 0 && it.isReady }) readyRoom.complete(true)
                        }.use {
                            assertEquals(ProtocolReceiptStatus.APPLIED, host.setReady(true).await().status)
                            assertTrue(readyRoom.await(), "ROOM_UPDATE must still reach a spectator")
                        }
                        val updates = CopyOnWriteArrayList<ProtocolPublicUpdate>()
                        val paused = CompletableFuture<ProtocolPublicUpdate>()
                        spectator.onPublicUpdate {
                            if (it.snapshot.paused) { updates.add(it); paused.complete(it) }
                        }.use {
                            proxy.dropPausedOnce = true
                            proxy.loseFirstPublicAck = true
                            assertTrue(server.registry.getRoom(room.roomId)!!.tableHost.pause().await())
                            assertTrue(proxy.droppedPaused.await(3, TimeUnit.SECONDS), "Must drop the entire final PUBLIC message")
                            // From this point no query, game action, room operation, or extra mutation may heal the stream.
                            val recovered = paused.await()
                            assertTrue(recovered.snapshot.cursor > baseline.snapshot.cursor)
                            assertTrue(proxy.repeatedAckForwarded.await(4, TimeUnit.SECONDS), "Duplicate PUBLIC must ACK after the first ACK is lost")
                            assertTrue(proxy.pausedMessages.get() >= 3, "Initial loss and ACK loss require actual retransmissions")
                            assertEquals(1, updates.size, "Reliable retransmissions must not duplicate public callbacks")
                            assertFalse(proxy.failure.isDone)
                        }
                    }
                }
            }
        }
    }

    @Test fun exactlyOneMissingPublicUpdateRequestsResendAndDuplicateReplayDoesNotSkipOrRepeat() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                val room = host.createRoom(CreateRoomRequest()).await()
                PublicProxy(server.boundPort).use { proxy ->
                    UdpClient.connect("127.0.0.1", proxy.port).await().use { transport ->
                        val spectator: ServerConnection = transport
                        val baseline = spectator.joinSpectator(JoinSpectatorRequest(room.roomId)).await().snapshot
                        val delivered = CopyOnWriteArrayList<Long>()
                        val completed = CountDownLatch(1)
                        spectator.onPublicUpdate {
                            if (it.snapshot.cursor > baseline.cursor) delivered.add(it.snapshot.cursor)
                            if (it.snapshot.cursor == baseline.cursor + 2) completed.countDown()
                        }.use {
                            proxy.holdCursor = baseline.cursor + 1
                            val authority = server.registry.getRoom(room.roomId)!!.tableHost
                            assertTrue(authority.pause().await())
                            assertTrue(proxy.heldPublic.await(3, TimeUnit.SECONDS))
                            assertTrue(authority.resumeClock().await())
                            assertTrue(proxy.publicResend.await(3, TimeUnit.SECONDS), "A one-update gap must trigger PUBLIC_RESEND")
                            assertTrue(delivered.isEmpty(), "Future cursor must not become visible across a gap")
                            proxy.releaseHeldTwice()
                            assertTrue(completed.await(5, TimeUnit.SECONDS))
                            assertEquals(listOf(baseline.cursor + 1, baseline.cursor + 2), delivered.toList())
                            assertFalse(proxy.failure.isDone)
                        }
                    }
                }
            }
        }
    }

    @Test fun spectatorPrivateSnapshotRequestIsRejectedByActualServerEvenWhenBypassingClientFacade() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                val room = host.createRoom(CreateRoomRequest()).await()
                PublicProxy(server.boundPort).use { proxy ->
                    UdpClient.connect("127.0.0.1", proxy.port).await().use { transport ->
                        (transport as ServerConnection).joinSpectator(JoinSpectatorRequest(room.roomId)).await()
                        val request = ApiRequest(ApiTags.SNAPSHOT, "review-raw-private-snapshot", roomId = room.roomId)
                        val response = CompletableFuture<Pair<ApiResponse, UdpFrame>>()
                        proxy.rawReplies[request.requestId] = response
                        transport.sendFrame(UdpFrame(UdpPacketType.REQ, transport.connectionId, transport.nextSeq(),
                            payload = ApiMessageCodec.encodeRequest(request).toByteArray(Charsets.UTF_8)))
                        val (reply, frame) = response.await()
                        transport.sendFrame(UdpFrame(UdpPacketType.ACK, transport.connectionId, transport.nextSeq(), ackSeq = frame.seq))
                        assertTrue(!reply.success || reply.receipt?.status == ProtocolReceiptStatus.REJECTED)
                        assertNull(reply.snapshot)
                        assertNull(reply.decision)
                        assertNull(reply.events)
                        assertFalse(proxy.failure.isDone)
                    }
                }
            }
        }
    }

    @Test fun publicResendOutsideRetainedWindowArrivesAsExplicitFullBaseline() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                val room = host.createRoom(CreateRoomRequest()).await()
                PublicProxy(server.boundPort).use { proxy ->
                    UdpClient.connect("127.0.0.1", proxy.port).await().use { transport ->
                        val spectator: ServerConnection = transport
                        val baseline = spectator.joinSpectator(JoinSpectatorRequest(room.roomId)).await().snapshot
                        proxy.dropAllPublic = true
                        // Keep ordinary retries blocked when reopening the full recovery path below.
                        proxy.dropOrdinaryPublic = true
                        val authority = server.registry.getRoom(room.roomId)!!.tableHost
                        repeat(UdpProtocol.RETRANSMIT_WINDOW_SIZE + 2) { index ->
                            assertTrue((if (index % 2 == 0) authority.pause() else authority.resumeClock()).await())
                        }
                        val restored = CompletableFuture<ProtocolPublicUpdate>()
                        val fullCallbacks = AtomicInteger()
                        spectator.onPublicUpdate {
                            if (it.isFullSnapshot && it.snapshot.cursor > baseline.cursor) {
                                fullCallbacks.incrementAndGet()
                                restored.complete(it)
                            }
                        }.use {
                            proxy.dropAllPublic = false
                            val checkpoint = PublicCheckpoint(baseline.sessionId, baseline.syncEpoch, baseline.cursor)
                            transport.sendFrame(UdpFrame(UdpPacketType.RESEND, transport.connectionId, transport.nextSeq(),
                                payload = ApiMessageCodec.encodePublicResendRequest(checkpoint).toByteArray(Charsets.UTF_8)))
                            val full = restored.await()
                            assertTrue(full.snapshot.cursor > baseline.cursor + UdpProtocol.RETRANSMIT_WINDOW_SIZE)
                            assertEquals(full.snapshot.cursor, full.fromCursor)
                            assertEquals(baseline.sessionId, full.snapshot.sessionId)
                            assertEquals(baseline.syncEpoch, full.snapshot.syncEpoch)
                            proxy.replayFullTwice()
                            assertTrue(proxy.fullAcknowledgements.await(3, TimeUnit.SECONDS),
                                "Repeated full messages must be ACKed, including identical seq/fragments")
                            // A subsequent response is a receive-loop barrier after duplicate ACKs, not a state mutation.
                            assertEquals(full.snapshot.cursor, spectator.publicSnapshot().await().cursor)
                            assertEquals(1, fullCallbacks.get(), "A repeated full baseline must not repeat public callbacks")
                            assertFalse(proxy.failure.isDone)
                        }
                    }
                }
            }
        }
    }

    @Test fun spectatorRuntimeRecoversTwiceThenLeavesRejoinsAndClosesItsReceiverOnServerShutdown() {
        val server = UdpTableServer()
        server.start()
        try {
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                val room = host.createRoom(CreateRoomRequest()).await()
                PublicProxy(server.boundPort).use { proxy ->
                    val existingThreads = Thread.getAllStackTraces().keys.map { it.id }.toSet()
                    ServerConnectionSession.connectUdp("127.0.0.1", proxy.port).await().use { runtime ->
                        val initial = runtime.joinSpectator(room.roomId).await()
                        assertNull(runtime.currentSnapshot, "Spectator cannot own a player cache")
                        val restored = AtomicReference<CountDownLatch?>()
                        val reconnects = AtomicInteger()
                        runtime.onStateChange { state ->
                            when (state) {
                                ConnectionState.RECONNECTING -> { reconnects.incrementAndGet(); proxy.disconnected = false }
                                ConnectionState.CONNECTED -> restored.get()?.countDown()
                                else -> Unit
                            }
                        }.use {
                            repeat(2) { cycle ->
                                val oldConnection = runtime.currentConnection
                                val ready = CountDownLatch(1)
                                restored.set(ready)
                                proxy.disconnected = true
                                assertTrue(ready.await(18, TimeUnit.SECONDS), "Spectator recovery ${cycle + 1} did not complete")
                                assertNotSame(oldConnection, runtime.currentConnection)
                                assertTrue(oldConnection.closed, "Replaced transport must be unusable")
                                assertEquals(initial.sessionId, runtime.publicSnapshot().await().sessionId)
                                assertNull(runtime.currentSnapshot)
                                if (cycle == 0) {
                                    val cleanupWindow = CountDownLatch(1)
                                    CompletableFuture.delayedExecutor(2300, TimeUnit.MILLISECONDS).execute { cleanupWindow.countDown() }
                                    assertTrue(cleanupWindow.await(4, TimeUnit.SECONDS))
                                }
                            }
                            assertEquals(2, reconnects.get())
                            assertEquals(2, proxy.spectatorResumes.get())
                            assertEquals(0, proxy.playerResumes.get())
                        }
                        assertEquals(com.majiang.client.api.ReceiptStatus.APPLIED, runtime.leaveRoom().await().status)
                        assertNull(runtime.currentPublicSnapshot)
                        assertFalse(runtime.closed)
                        assertEquals(initial.sessionId, runtime.joinSpectator(room.roomId).await().sessionId)
                        val ownedReceivers = Thread.getAllStackTraces().keys.filter {
                            it.name == "udp-client-receiver" && it.id !in existingThreads
                        }
                        assertTrue(ownedReceivers.isNotEmpty(), "Must observe the spectator receiver before shutdown")
                        val stopped = CompletableFuture<String>()
                        runtime.onDisconnect { stopped.complete(it) }.use {
                            server.close()
                            assertEquals(ApiProtocol.SERVER_SHUTDOWN, stopped.await())
                        }
                        ownedReceivers.forEach { it.join(3500) }
                        assertTrue(ownedReceivers.none { it.isAlive }, "Spectator receiver leaked after shutdown")
                        assertFalse(proxy.failure.isDone)
                    }
                }
            }
        } finally { server.close() }
    }
}

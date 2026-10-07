package com.majiang.review

import com.majiang.transport.api.ProtocolPlayerUpdate as PlayerUpdate
import com.majiang.transport.api.ProtocolReceiptStatus as ReceiptStatus
import com.majiang.client.transport.udp.UdpClient
import com.majiang.client.transport.udp.UdpClientBinding
import com.majiang.server.api.*
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.*
import com.majiang.transport.udp.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Review-owned contract tests. Assertions describe intended behavior, not known defects. */
@Timeout(15)
class V4ReviewAcceptanceTest {
    private val noTimers = object : UdpScheduler {
        override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
    }
    private fun <T> CompletableFuture<T>.await(): T = get(4, TimeUnit.SECONDS)

    private fun bindPlayerForPushReview(
        client: UdpClientBinding,
        outgoing: MutableList<UdpFrame>,
        baseline: ProtocolPlayerSnapshot
    ) {
        val joined = client.createRoom(CreateRoomRequest("Host"))
        val frame = outgoing.last { it.packetType == UdpPacketType.REQ }
        val request = ApiMessageCodec.decodeRequest(frame.payload.toString(Charsets.UTF_8))
        client.onInboundFrame(UdpFrame(UdpPacketType.RSP, 42, 100, ackSeq = frame.seq,
            payload = ApiMessageCodec.encodeResponse(ApiResponse(
                requestId = request.requestId, success = true, roomId = "review-room", seat = 0,
                resumeToken = "review-token", snapshot = baseline
            )).toByteArray(Charsets.UTF_8)))
        joined.await()
        outgoing.clear()
    }

    private class Peer(private val port: Int) : AutoCloseable {
        val socket = DatagramSocket()
        private val fragments = Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
        })
        fun send(frame: UdpFrame) {
            val bytes = UdpFrame.encode(frame)
            socket.send(DatagramPacket(bytes, bytes.size, InetAddress.getLoopbackAddress(), port))
        }
        fun request(request: ApiRequest, connectionId: Int = 0, seq: Int = 1): Pair<UdpFrame, ApiResponse> {
            send(UdpFrame(UdpPacketType.REQ, connectionId, seq,
                payload = ApiMessageCodec.encodeRequest(request).toByteArray()))
            val frame = receive { f -> f.packetType == UdpPacketType.RSP &&
                ApiMessageCodec.decodeResponse(f.payload.toString(Charsets.UTF_8)).requestId == request.requestId }
            return frame to ApiMessageCodec.decodeResponse(frame.payload.toString(Charsets.UTF_8))
        }
        fun receive(predicate: (UdpFrame) -> Boolean): UdpFrame {
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
            while (System.nanoTime() < deadline) {
                socket.soTimeout = maxOf(1, TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime()).toInt())
                val packet = DatagramPacket(ByteArray(2048), 2048)
                socket.receive(packet)
                val f = fragments.reassemble(UdpFrame.decode(packet.data.copyOf(packet.length))) ?: continue
                if (predicate(f)) return f
            }
            error("No matching packet before deadline")
        }
        override fun close() { socket.close(); fragments.close() }
    }

    @Test fun realClientServerCreateJoinStartSnapshotAndResume() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                UdpClient.connect("127.0.0.1", server.boundPort).await().use { guest ->
                    val created = host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                    val joined = guest.joinRoom(JoinRoomRequest(created.roomId)).await()
                    assertEquals(1, joined.seat)
                    val push = CompletableFuture<PlayerUpdate>()
                    guest.onUpdate { if (it.snapshot.cursor > joined.snapshot.cursor) push.complete(it) }
                    assertEquals(ReceiptStatus.APPLIED, host.setReady(true).await().status)
                    assertEquals(ReceiptStatus.APPLIED, guest.setReady(true).await().status)
                    assertNotEquals(ReceiptStatus.REJECTED, host.start().await().status)
                    assertEquals(0, host.snapshot().await().viewerSeat)
                    assertEquals(1, push.await().snapshot.viewerSeat)
                    UdpClient.connect("127.0.0.1", server.boundPort).await().use { replacement ->
                        val kicked = CompletableFuture<String>()
                        guest.onDisconnect { kicked.complete(it) }
                        val restored = replacement.resumeSession(ResumeRequest(created.roomId, joined.resumeToken)).await()
                        assertEquals(1, restored.seat)
                        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, kicked.await())
                        assertEquals(1, replacement.snapshot().await().viewerSeat)
                    }
                }
            }
        }
    }

    @Test fun lostCreateResponseRetryMustReuseRoomAndConnection() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { peer ->
                val request = ApiRequest(ApiTags.CREATE_ROOM, "same-create")
                val first = peer.request(request) // Receive but deliberately treat response as lost.
                val retry = peer.request(request)
                assertEquals(first.second.roomId, retry.second.roomId, "CREATE_ROOM retry created another room")
                assertEquals(first.first.connectionId, retry.first.connectionId)
                assertEquals(1, server.activeRoomCount)
            }
        }
    }

    @Test fun hostLeaveMustCompleteReceiptAndKeepLobbyConnectionOpen() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                assertEquals(ReceiptStatus.APPLIED, host.leaveRoom().await().status)
                assertFalse(host.closed)
                assertTrue(host.listRooms().await().isEmpty())
            }
        }
    }

    @Test fun lostGuestLeaveResponseMustBeReplayable() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { host ->
                val created = host.request(ApiRequest(ApiTags.CREATE_ROOM, "host")).second
                Peer(server.boundPort).use { guest ->
                    val joined = guest.request(ApiRequest(ApiTags.JOIN_ROOM, "guest", roomId = created.roomId))
                    val req = ApiRequest(ApiTags.LEAVE_ROOM, "same-leave")
                    val first = guest.request(req, joined.first.connectionId, 2)
                    val retry = guest.request(req, joined.first.connectionId, 2)
                    assertEquals(first.second.receipt, retry.second.receipt)
                }
            }
        }
    }

    @Test fun malformedBoundRequestMustNotKillServerReceiver() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { peer ->
                val created = peer.request(ApiRequest(ApiTags.CREATE_ROOM, "host"))
                peer.send(UdpFrame(UdpPacketType.REQ, created.first.connectionId, 2,
                    payload = """{"api":"SNAPSHOT"}""".toByteArray()))
                peer.send(UdpFrame(UdpPacketType.PING, 0, 3,
                    payload = HeartbeatPacer.encodePingPayload(123)))
                assertEquals(UdpPacketType.PONG, peer.receive { it.packetType == UdpPacketType.PONG }.packetType)
            }
        }
    }

    @Test fun fragmentsWithSameMessageIdFromDifferentConnectionsStayIsolated() {
        Fragmenter(UdpClock.SYSTEM, noTimers).use { receiver ->
            fun part(connection: Int, index: Int, value: String) = UdpFrame(
                UdpPacketType.FRAG, connection, index + 1, payload = Fragmenter.encodeSubheader(
                    1, index, 2, UdpPacketType.REQ, value.toByteArray()))
            assertNull(receiver.reassemble(part(11, 0, "A0")))
            assertNull(receiver.reassemble(part(22, 1, "B1")), "Must not reassemble A0+B1 under connection 22")
            assertEquals("A0A1", receiver.reassemble(part(11, 1, "A1"))?.payload?.toString(Charsets.UTF_8))
            assertEquals("B0B1", receiver.reassemble(part(22, 0, "B0"))?.payload?.toString(Charsets.UTF_8))
        }
    }

    @Test fun boundClientMustRejectZeroConnectionKick() {
        UdpClientBinding({}, scheduler = noTimers, initialConnectionId = 42).use { client ->
            client.onInboundFrame(UdpFrame(UdpPacketType.KICK, 0, 1,
                payload = ApiMessageCodec.encodeKick(ApiProtocol.ROOM_CLOSED).toByteArray()))
            assertFalse(client.closed, "connectionId=0 cannot terminate bound connection 42")
        }
    }

    @Test fun windowFallbackFullPushMustBeAdoptedWithoutAnotherResend() {
        InProcessServer().use { server ->
            val snap = server.createLocalTable().snapshot().await().copy(cursor = 10)
            val outgoing = mutableListOf<UdpFrame>()
            UdpClientBinding({ outgoing.add(UdpFrame.decode(it)) }, scheduler = noTimers,
                initialConnectionId = 42).use { client ->
                bindPlayerForPushReview(client, outgoing, snap.copy(cursor = 0))
                val received = mutableListOf<PlayerUpdate>()
                client.onUpdate { received.add(it) }
                val full = PlayerUpdate(snap, snap.cursor, emptyList())
                client.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 42, 1,
                    payload = ApiMessageCodec.encodePush(full).toByteArray()))
                assertEquals(listOf(10L), received.map { it.snapshot.cursor },
                    "Full PUSH from retransmit window fallback must restore the client")
                assertTrue(outgoing.none { it.packetType == UdpPacketType.RESEND })
            }
        }
    }

    @Test fun serverShutdownMustSendDisconnectReasonBeforeClosingSocket() {
        val server = UdpTableServer()
        server.start()
        try {
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                val reason = CompletableFuture<String>()
                host.onDisconnect { reason.complete(it) }
                server.close()
                assertEquals(ApiProtocol.SERVER_SHUTDOWN, reason.await())
            }
        } finally { server.close() }
    }

    private fun verifyControlSemantics(connection: ServerConnection) {
        assertEquals("HUMAN", connection.snapshot().await().controls.first { it.seat == 0 }.role)
        assertEquals(ReceiptStatus.APPLIED, connection.releaseControl().await().status)
        assertEquals("TAKEOVER", connection.snapshot().await().controls.first { it.seat == 0 }.role,
            "Releasing control hands the seat to AI")
        assertEquals(ReceiptStatus.APPLIED, connection.claimControl().await().status)
        assertEquals("HUMAN", connection.snapshot().await().controls.first { it.seat == 0 }.role,
            "Claiming control returns the seat to the human")
    }

    @Test fun inProcessControlMatchesV4Contract() {
        InProcessServer().use { server -> verifyControlSemantics(server.createLocalTable()) }
    }

    @Test fun udpControlMatchesV4Contract() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { client ->
                client.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                verifyControlSemantics(client)
            }
        }
    }

    @Test fun reorderedDistinctRequestsStillReceiveResponses() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { peer ->
                val created = peer.request(ApiRequest(ApiTags.CREATE_ROOM, "reorder-host"))
                val conn = created.first.connectionId
                assertTrue(peer.request(ApiRequest(ApiTags.CAPABILITIES, "later"), conn, 20).second.success)
                assertTrue(peer.request(ApiRequest(ApiTags.CAPABILITIES, "earlier"), conn, 19).second.success,
                    "A distinct valid request arriving late must not become permanently unanswerable")
            }
        }
    }

    @Test fun joinRetryDoesNotAllocateAnotherSeat() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { host ->
                val created = host.request(ApiRequest(ApiTags.CREATE_ROOM, "join-host")).second
                Peer(server.boundPort).use { guest ->
                    val req = ApiRequest(ApiTags.JOIN_ROOM, "same-join", roomId = created.roomId)
                    val first = guest.request(req)
                    val retry = guest.request(req)
                    assertEquals(first.second.seat, retry.second.seat)
                    assertEquals(first.first.connectionId, retry.first.connectionId)
                    assertEquals(first.second.resumeToken, retry.second.resumeToken)
                }
            }
        }
    }

    @Test fun fullSnapshotCannotRollBackAnExistingStream() {
        val sequencer = PushSequencer(initialCursor = 20)
        sequencer.adoptFullSnapshot(19)
        assertEquals(20L, sequencer.getCurrentCursor(), "Late SNAPSHOT response must not roll back an existing stream")
        assertTrue(sequencer.offer(18, 18, isFullSnapshot = true) is PushOfferResult.Stale)
        assertEquals(20L, sequencer.getCurrentCursor())
        // New room/epoch must explicitly reset the sequence; an old full snapshot is not an epoch transition.
        sequencer.reset(0)
        assertTrue(sequencer.offer(1, 1, isFullSnapshot = true) is PushOfferResult.Applied)
    }

    @Test fun changedPayloadCannotReuseCompletedCreateRequestId() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { peer ->
                val request = ApiRequest(ApiTags.CREATE_ROOM, "conflicting-create", playerName = "first")
                assertTrue(peer.request(request).second.success)
                val conflict = peer.request(request.copy(playerName = "different")).second
                assertFalse(conflict.success, "Changed payload with the same requestId must be rejected")
                assertEquals(1, server.activeRoomCount)
            }
        }
    }

    @Test fun boundResponseMustMatchConnectionBeforeCompletingPendingCall() {
        val outgoing = mutableListOf<UdpFrame>()
        UdpClientBinding({ outgoing.add(UdpFrame.decode(it)) }, scheduler = noTimers,
            initialConnectionId = 42).use { client ->
            val future = client.listRooms()
            val requestFrame = outgoing.single { it.packetType == UdpPacketType.REQ }
            val request = ApiMessageCodec.decodeRequest(requestFrame.payload.toString(Charsets.UTF_8))
            val response = ApiMessageCodec.encodeResponse(ApiResponse(requestId = request.requestId,
                rooms = emptyList())).toByteArray()
            client.onInboundFrame(UdpFrame(UdpPacketType.RSP, 0, 10, ackSeq = requestFrame.seq, payload = response))
            assertFalse(future.isDone, "A zero-connection response must not complete a bound request")
            client.onInboundFrame(UdpFrame(UdpPacketType.RSP, 42, 11, ackSeq = requestFrame.seq, payload = response))
            assertTrue(future.await().isEmpty())
        }
    }

    @Test fun explicitGuestLeaveFreesSeatAndRevokesResumeToken() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { host ->
                val created = host.request(ApiRequest(ApiTags.CREATE_ROOM, "leave-host")).second
                Peer(server.boundPort).use { guest ->
                    val joined = guest.request(ApiRequest(ApiTags.JOIN_ROOM, "leave-guest", roomId = created.roomId))
                    assertTrue(guest.request(ApiRequest(ApiTags.LEAVE_ROOM, "leave"), joined.first.connectionId, 2).second.success)
                    val resumed = guest.request(ApiRequest(ApiTags.RESUME_SESSION, "revoked-resume",
                        roomId = created.roomId, token = joined.second.resumeToken), seq = 3).second
                    assertFalse(resumed.success, "Explicit LEAVE must revoke the former resume token")
                    val newGuest = guest.request(ApiRequest(ApiTags.JOIN_ROOM, "new-guest",
                        roomId = created.roomId, preferSeat = joined.second.seat), seq = 4).second
                    assertTrue(newGuest.success)
                    assertEquals(joined.second.seat, newGuest.seat)
                }
            }
        }
    }

    @Test fun terminalKickIsRetriedUntilAcknowledged() {
        UdpTableServer().use { server ->
            server.start()
            Peer(server.boundPort).use { peer ->
                val created = peer.request(ApiRequest(ApiTags.CREATE_ROOM, "kick-host"))
                val session = assertNotNull(server.getSession(created.first.connectionId))
                server.sendKick(session, ApiProtocol.ROOM_CLOSED)
                val first = peer.receive { it.packetType == UdpPacketType.KICK }
                // Deliberately lose the first ACK, as on a real UDP link.
                val retry = peer.receive { it.packetType == UdpPacketType.KICK }
                assertEquals(first.connectionId, retry.connectionId)
                assertContentEquals(first.payload, retry.payload)
                peer.send(UdpFrame(UdpPacketType.ACK, retry.connectionId, 3, ackSeq = retry.seq))
            }
        }
    }

    @Test fun staleFullPushNeverReachesApplicationObservers() {
        InProcessServer().use { server ->
            val snap = server.createLocalTable().snapshot().await()
            val outgoing = mutableListOf<UdpFrame>()
            UdpClientBinding({ outgoing.add(UdpFrame.decode(it)) }, scheduler = noTimers,
                initialConnectionId = 42).use { client ->
                bindPlayerForPushReview(client, outgoing, snap.copy(cursor = 0))
                val cursors = mutableListOf<Long>()
                client.onUpdate { cursors.add(it.snapshot.cursor) }
                for ((seq, cursor) in listOf(1 to 20L, 2 to 19L, 3 to 20L)) {
                    val update = PlayerUpdate(snap.copy(cursor = cursor), cursor, emptyList())
                    client.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 42, seq,
                        payload = ApiMessageCodec.encodePush(update, isFullSnapshot = true).toByteArray()))
                }
                assertEquals(listOf(20L), cursors, "Neither stale nor duplicate full payload may be dispatched")
            }
        }
    }

    @Test fun fullBaselineMustBeExplicitInWireEnvelope() {
        InProcessServer().use { server ->
            val snap = server.createLocalTable().snapshot().await().copy(cursor = 10)
            val update = PlayerUpdate(snap, snap.cursor, emptyList())
            assertFalse(ApiMessageCodec.decodePushEnvelope(
                ApiMessageCodec.encodePush(update, isFullSnapshot = false)).isFullSnapshot,
                "Cursor shape alone does not authorize adopting a replacement baseline")
            assertTrue(ApiMessageCodec.decodePushEnvelope(
                ApiMessageCodec.encodePush(update, isFullSnapshot = true)).isFullSnapshot)
        }
    }
}

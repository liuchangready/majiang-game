package com.majiang.server.api

import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShuffleSelection
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

class FakeServerConnectionSmokeTest {

    private fun <T> java.util.concurrent.CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test
    fun `initial state is not closed with null disconnectReason`() {
        val conn = FakeServerConnection()
        assertFalse(conn.closed)
        assertNull(conn.disconnectReason)
    }

    @Test
    fun `LobbyApi methods succeed and return expected values`() {
        val conn = FakeServerConnection()
        val roomSummary = RoomSummary("ROOM1", "Room 1", "Host", 1, 4, false)
        conn.roomsList = listOf(roomSummary)

        val rooms = conn.listRooms().await()
        assertEquals(1, rooms.size)
        assertEquals("ROOM1", rooms[0].roomId)

        val createReq = CreateRoomRequest("Host", null, 4)
        val binding = conn.createRoom(createReq).await()
        assertEquals("ROOM0001", binding.roomId)
        assertEquals(0, binding.seat)
        assertNotNull(binding.snapshot)

        val joinReq = JoinRoomRequest("ROOM1", "Bob", 2)
        val joinBinding = conn.joinRoom(joinReq).await()
        assertEquals("ROOM1", joinBinding.roomId)
        assertEquals(2, joinBinding.seat)

        val resumeReq = ResumeRequest("ROOM1", "token-player")
        val resumeBinding = conn.resumeSession(resumeReq).await()
        assertEquals("ROOM1", resumeBinding.roomId)
        assertEquals("token-player", resumeBinding.resumeToken)

        val leaveReceipt = conn.leaveRoom().await()
        assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
    }

    @Test
    fun `SessionApi methods succeed and record submitted actions`() {
        val conn = FakeServerConnection()

        val caps = conn.capabilities().await()
        assertEquals(ApiProtocol.PROTOCOL_VERSION, caps.protocolVersion)

        val snap = conn.snapshot().await()
        assertNotNull(snap)

        val dec = conn.decision().await()
        assertEquals("round-1", dec.roundId)

        val choice = ProtocolActionChoice(
            requestId = "req-1",
            roundId = "round-1",
            decisionId = "dec-1",
            controlEpoch = 1L,
            optionId = "opt-0"
        )
        val receipt = conn.submit(choice).await()
        assertEquals("req-1", receipt.requestId)
        assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
        assertEquals(1, conn.submittedChoices.size)
        assertEquals(choice, conn.submittedChoices[0])

        val plan = ProtocolShufflePlan(
            requestId = "plan-1",
            roundId = "round-1",
            controlEpoch = 1L,
            selection = ProtocolShuffleSelection()
        )
        val planReceipt = conn.submitShufflePlan(plan).await()
        assertEquals("plan-1", planReceipt.requestId)
        assertEquals(ProtocolReceiptStatus.RECORDED, planReceipt.status)
        assertEquals(1, conn.submittedShufflePlans.size)

        val startReceipt = conn.start().await()
        assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

        val nextRoundReceipt = conn.nextRound().await()
        assertEquals(ProtocolReceiptStatus.APPLIED, nextRoundReceipt.status)

        val resumedSnap = conn.resume(5L).await()
        assertNotNull(resumedSnap)

        val claimReceipt = conn.claimControl().await()
        assertEquals(ProtocolReceiptStatus.APPLIED, claimReceipt.status)
        assertEquals(true, conn.lastClaimed)

        val releaseReceipt = conn.releaseControl().await()
        assertEquals(ProtocolReceiptStatus.APPLIED, releaseReceipt.status)
        assertEquals(false, conn.lastClaimed)

        val roundResult = conn.queryRoundResult("round-1").await()
        assertTrue(roundResult.isEmpty())
    }

    @Test
    fun `PushChannel dispatches updates and unregisters cleanly`() {
        val conn = FakeServerConnection()
        val received = CopyOnWriteArrayList<ProtocolPlayerUpdate>()

        val handle = conn.onUpdate { received.add(it) }
        val dummySnapshot = FakeServerConnection.createDefaultSnapshot(cursor = 1L)
        val update = ProtocolPlayerUpdate(snapshot = dummySnapshot, fromCursor = 0L)

        conn.emitUpdate(update)
        assertEquals(1, received.size)
        assertEquals(1L, received[0].snapshot.cursor)

        handle.close()
        conn.emitUpdate(ProtocolPlayerUpdate(snapshot = dummySnapshot.copy(cursor = 2L), fromCursor = 1L))
        assertEquals(1, received.size, "Closed handle must no longer receive updates")
    }

    @Test
    fun `close triggers onDisconnect with LOCAL_CLOSED and is idempotent`() {
        val conn = FakeServerConnection()
        val disconnectReasons = CopyOnWriteArrayList<String>()

        conn.onDisconnect { disconnectReasons.add(it) }
        assertFalse(conn.closed)

        conn.close()
        assertTrue(conn.closed)
        assertEquals(ApiProtocol.LOCAL_CLOSED, conn.disconnectReason)
        assertEquals(listOf(ApiProtocol.LOCAL_CLOSED), disconnectReasons)

        // Idempotent: second close() has no additional side-effects
        conn.close()
        assertEquals(1, disconnectReasons.size)
    }

    @Test
    fun `emitDisconnect triggers listener with specified reason and marks closed`() {
        val conn = FakeServerConnection()
        val disconnectReasons = CopyOnWriteArrayList<String>()

        conn.onDisconnect { disconnectReasons.add(it) }

        conn.emitDisconnect(ApiProtocol.DISPLACED_BY_NEW_CONNECTION)
        assertTrue(conn.closed)
        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, conn.disconnectReason)
        assertEquals(listOf(ApiProtocol.DISPLACED_BY_NEW_CONNECTION), disconnectReasons)
    }

    @Test
    fun `SpectatorApi methods succeed, record requests, and support public update emission`() {
        val conn = FakeServerConnection()

        val joinReq = JoinSpectatorRequest("ROOM1", "Watcher", 4)
        val joinBinding = conn.joinSpectator(joinReq).await()
        assertEquals("ROOM1", joinBinding.roomId)
        assertEquals("spectator-1", joinBinding.spectatorId)
        assertEquals("token-spectator", joinBinding.resumeToken)
        assertNotNull(joinBinding.snapshot)
        assertEquals(1, conn.submittedJoinSpectatorRequests.size)
        assertEquals(joinReq, conn.submittedJoinSpectatorRequests[0])

        val checkpoint = PublicCheckpoint("session-1", "epoch-1", 5L)
        val resumeReq = ResumeSpectatorRequest("ROOM1", "spectator-1", "token-spectator", checkpoint)
        val resumeBinding = conn.resumeSpectator(resumeReq).await()
        assertEquals("ROOM1", resumeBinding.roomId)
        assertEquals("spectator-1", resumeBinding.spectatorId)
        assertEquals("token-spectator", resumeBinding.resumeToken)
        assertEquals(1, conn.submittedResumeSpectatorRequests.size)
        assertEquals(resumeReq, conn.submittedResumeSpectatorRequests[0])

        val pubSnap = conn.publicSnapshot().await()
        assertNotNull(pubSnap)

        val resumedPubSnap = conn.resumePublic(checkpoint).await()
        assertNotNull(resumedPubSnap)
        assertEquals(1, conn.submittedPublicCheckpoints.size)
        assertEquals(checkpoint, conn.submittedPublicCheckpoints[0])

        val publicEvents = conn.queryPublicRoundResult("round-1").await()
        assertTrue(publicEvents.isEmpty())
        assertEquals(1, conn.queriedPublicRoundIds.size)
        assertEquals("round-1", conn.queriedPublicRoundIds[0])

        val received = CopyOnWriteArrayList<com.majiang.transport.api.ProtocolPublicUpdate>()
        val handle = conn.onPublicUpdate { received.add(it) }
        val dummyPublicSnapshot = FakeServerConnection.createDefaultPublicSnapshot(cursor = 2L)
        val publicUpdate = com.majiang.transport.api.ProtocolPublicUpdate(snapshot = dummyPublicSnapshot, fromCursor = 1L)

        conn.emitPublicUpdate(publicUpdate)
        assertEquals(1, received.size)
        assertEquals(2L, received[0].snapshot.cursor)

        handle.close()
        conn.emitPublicUpdate(com.majiang.transport.api.ProtocolPublicUpdate(snapshot = dummyPublicSnapshot.copy(cursor = 3L), fromCursor = 2L))
        assertEquals(1, received.size, "Closed handle must no longer receive public updates")
    }
}

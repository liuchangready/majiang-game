package com.majiang.client.api

import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.client.session.ServerConnectionSession
import com.majiang.server.api.ServerConnection
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.IOException
import java.util.concurrent.CompletableFuture

/** Codex-owned client contracts; the local decorator avoids modifying shared fake fixtures. */
class BeamSwapClientContractTest {
    private class BeamConnection(
        val base: ServerConnectionSessionTest.FakeServerConnection = ServerConnectionSessionTest.FakeServerConnection()
    ) : ServerConnection by base {
        var activationFuture: CompletableFuture<ProtocolActionReceipt>? = null
        var choiceFuture: CompletableFuture<ProtocolActionReceipt>? = null
        var synchronousFailure: String? = null
        var lastActivation: ProtocolBeamSwapRequest? = null
        var lastChoice: ProtocolBeamSwapChoiceRequest? = null
        var activationCount = 0
        var choiceCount = 0

        override fun submitBeamSwap(request: ProtocolBeamSwapRequest): CompletableFuture<ProtocolActionReceipt> {
            if (synchronousFailure == "activate") throw IOException("offline")
            activationCount++
            lastActivation = request
            return activationFuture ?: CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED, cursor = 11))
        }

        override fun submitBeamSwapChoice(request: ProtocolBeamSwapChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
            if (synchronousFailure == "choose") throw IOException("offline")
            choiceCount++
            lastChoice = request
            return choiceFuture ?: CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED, cursor = 12))
        }
    }

    private fun activation(id: String = "activate") = BeamSwapRequest(id, "session-1", "round-1", "dec-cursor-10", 1)
    private fun selection(id: String = "choose", slot: BeamSwapSlot = BeamSwapSlot.WallSlot(32)) =
        BeamSwapChoiceRequest(id, "session-1", "round-1", "dec-cursor-11", 1, "offer", BeamSwapAction.SWAP, 0, slot)
    private fun keep() = BeamSwapChoiceRequest("keep", "session-1", "round-1", "dec-cursor-11", 1,
        "offer", BeamSwapAction.KEEP_ORIGINAL)

    private fun pending(fake: ServerConnectionSessionTest.FakeServerConnection, cursor: Long): ProtocolPlayerSnapshot {
        val base = fake.createSnapshot(cursor)
        val offer = ProtocolBeamSwapOfferView("offer", ProtocolBeamSwapWindow.WALL,
            listOf(ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.WallSlot(32), false),
                ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.WallSlot(33), true, ProtocolTile(134))),
            listOf(0), 25_000, base.decision.decisionId, base.decision.controlEpoch)
        return base.copy(view = base.view.copy(phase = ProtocolGamePhase.CHEAT_WINDOW,
            currentTurnPlayer = base.view.viewerSeat, doraIndicators = listOf(ProtocolTile(120)), myDrawnTile = ProtocolTile(135)),
            beamSwap = ProtocolBeamSwapView(false, "BEAM_SWAP_ALREADY_PENDING", 10, ProtocolBeamSwapWindow.WALL,
                base.decision.decisionId, base.decision.controlEpoch, cursor, offer))
    }

    @Test fun `activation and choice share ordinary action inflight lock without optimistic state`() {
        val fake = BeamConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.activationFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val before = session.currentSnapshot
            val future = session.submitBeamSwap(activation())
            assertTrue(session.isPending)
            assertEquals(before, session.currentSnapshot)
            assertEquals("activate", fake.lastActivation!!.requestId)
            assertEquals("ACTION_IN_FLIGHT", session.submitBeamSwapChoice(selection()).join().reason)
            assertEquals("ACTION_IN_FLIGHT", session.submit(ActionChoice("discard", "round-1", "dec-cursor-10", 1, "opt-1")).join().reason)
            assertEquals("ACTION_IN_FLIGHT", session.submitSleeveBloom(
                SleeveBloomRequest("bloom", "session-1", "round-1", "dec-cursor-10", 1)).join().reason)
            assertEquals(0, fake.choiceCount)
            delayed.complete(ProtocolActionReceipt("activate", ProtocolReceiptStatus.APPLIED, cursor = 11))
            assertEquals(ReceiptStatus.APPLIED, future.join().status)
            assertFalse(session.isPending)
            assertEquals(before, session.currentSnapshot, "A receipt cannot invent a candidate or charge points")

            val choiceDelay = CompletableFuture<ProtocolActionReceipt>()
            fake.choiceFuture = choiceDelay
            val selected = session.submitBeamSwapChoice(selection())
            assertTrue(session.isPending)
            assertEquals("ACTION_IN_FLIGHT", session.submitBeamSwap(activation("again")).join().reason)
            choiceDelay.complete(ProtocolActionReceipt("choose", ProtocolReceiptStatus.REJECTED, reason = "BEAM_SWAP_INVALID_CHOICE"))
            assertEquals("BEAM_SWAP_INVALID_CHOICE", selected.join().reason)
            assertFalse(session.isPending)
            assertEquals(before, session.currentSnapshot)
        }
    }

    @Test fun `ordinary request also excludes both beam operations`() {
        val fake = BeamConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.base.customSubmitFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val discard = session.submit(ActionChoice("discard", "round-1", "dec-cursor-10", 1, "opt-1"))
            assertEquals("ACTION_IN_FLIGHT", session.submitBeamSwap(activation()).join().reason)
            assertEquals("ACTION_IN_FLIGHT", session.submitBeamSwapChoice(keep()).join().reason)
            assertEquals(0, fake.activationCount)
            assertEquals(0, fake.choiceCount)
            delayed.complete(ProtocolActionReceipt("discard", ProtocolReceiptStatus.APPLIED, cursor = 11))
            discard.join()
            assertFalse(session.isPending)
        }
    }

    @Test fun `choice maps every slot type and keep original has no fabricated selection`() {
        val fake = BeamConnection()
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            for ((source, expected) in listOf(
                BeamSwapSlot.WallSlot(32) to ProtocolBeamSwapSlot.WallSlot(32),
                BeamSwapSlot.DeadWallSlot(8) to ProtocolBeamSwapSlot.DeadWallSlot(8),
                BeamSwapSlot.ReplacementSlot(0) to ProtocolBeamSwapSlot.ReplacementSlot(0))) {
                session.submitBeamSwapChoice(selection(slot = source)).join()
                val request = fake.lastChoice!!
                assertEquals(ProtocolBeamSwapAction.SWAP, request.action)
                assertEquals(expected, request.targetSlot)
                assertEquals(0, request.handTileId)
                assertEquals("session-1", request.sessionId)
                assertEquals("offer", request.offerId)
                assertEquals("dec-cursor-11", request.decisionId)
                assertEquals(1L, request.controlEpoch)
            }
            session.submitBeamSwapChoice(keep()).join()
            assertEquals(ProtocolBeamSwapAction.KEEP_ORIGINAL, fake.lastChoice!!.action)
            assertNull(fake.lastChoice!!.handTileId)
            assertNull(fake.lastChoice!!.targetSlot)
        }
    }

    @Test fun `offer push before receipt and later clearing snapshot resist stale resurrection`() {
        val fake = BeamConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.activationFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val result = session.submitBeamSwap(activation())
            fake.base.emitUpdate(ProtocolPlayerUpdate(pending(fake.base, 11), 10, emptyList()))
            val frozen = session.currentSnapshot!!
            assertNull(frozen.beamSwap!!.pendingOffer!!.candidates[0].tile)
            assertEquals(134, frozen.beamSwap!!.pendingOffer!!.candidates[1].tile!!.tileId)
            assertEquals(listOf(0), frozen.beamSwap!!.pendingOffer!!.selectableHandTileIds)
            fake.base.emitUpdate(ProtocolPlayerUpdate(fake.base.createSnapshot(12), 11, emptyList()))
            delayed.complete(ProtocolActionReceipt("activate", ProtocolReceiptStatus.APPLIED, cursor = 11))
            assertEquals(ReceiptStatus.APPLIED, result.join().status)
            fake.base.emitUpdate(ProtocolPlayerUpdate(pending(fake.base, 11), 10, emptyList()))
            assertEquals(12L, session.currentSnapshot!!.cursor)
            assertNull(session.currentSnapshot!!.beamSwap)
            assertNotNull(frozen.beamSwap!!.pendingOffer)
            assertEquals(frozen, frozen.deepCopy())
        }
    }

    @Test fun `both synchronous and asynchronous failures release the inflight lock`() {
        val fake = BeamConnection()
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            fake.synchronousFailure = "activate"
            assertTrue(session.submitBeamSwap(activation()).isCompletedExceptionally)
            assertFalse(session.isPending)
            fake.synchronousFailure = "choose"
            assertTrue(session.submitBeamSwapChoice(selection()).isCompletedExceptionally)
            assertFalse(session.isPending)
            fake.synchronousFailure = null
            fake.activationFuture = CompletableFuture.failedFuture(IOException("offline"))
            assertTrue(session.submitBeamSwap(activation()).isCompletedExceptionally)
            assertFalse(session.isPending)
            fake.choiceFuture = CompletableFuture.failedFuture(IOException("offline"))
            assertTrue(session.submitBeamSwapChoice(keep()).isCompletedExceptionally)
            assertFalse(session.isPending)
        }
    }

    @Test fun `cancelled old completion cannot release new pending request and close terminates it`() {
        val fake = BeamConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.createRoom("Host", null).join()
            val old = CompletableFuture<ProtocolActionReceipt>()
            fake.choiceFuture = old
            assertTrue(session.submitBeamSwapChoice(selection()).cancel(false))
            assertFalse(session.isPending)
            val fresh = CompletableFuture<ProtocolActionReceipt>()
            fake.activationFuture = fresh
            val request = session.submitBeamSwap(activation("fresh"))
            old.complete(ProtocolActionReceipt("choose", ProtocolReceiptStatus.APPLIED, cursor = 12))
            assertTrue(session.isPending)
            assertFalse(request.isDone)
            session.close()
            assertTrue(request.isCompletedExceptionally)
            assertFalse(session.isPending)
            fresh.complete(ProtocolActionReceipt("fresh", ProtocolReceiptStatus.APPLIED, cursor = 13))
            assertFalse(session.isPending)
        } finally {
            session.close()
        }
    }

    @Test fun `rebinding terminates old choice and its later receipt cannot mutate the new generation`() {
        val fake = BeamConnection()
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val old = CompletableFuture<ProtocolActionReceipt>()
            fake.choiceFuture = old
            val retired = session.submitBeamSwapChoice(selection())
            session.resumeSession("ROOM1234", "token-player", 10L).join()
            assertTrue(retired.isCompletedExceptionally)
            assertFalse(session.isPending)
            val fresh = CompletableFuture<ProtocolActionReceipt>()
            fake.activationFuture = fresh
            val current = session.submitBeamSwap(activation("new-binding"))
            val snapshot = session.currentSnapshot
            old.complete(ProtocolActionReceipt("choose", ProtocolReceiptStatus.APPLIED, cursor = 999))
            assertTrue(session.isPending)
            assertFalse(current.isDone)
            assertEquals(snapshot, session.currentSnapshot)
            fresh.complete(ProtocolActionReceipt("new-binding", ProtocolReceiptStatus.REJECTED, reason = "WRONG_PHASE"))
            assertEquals("WRONG_PHASE", current.join().reason)
            assertFalse(session.isPending)
        }
    }

    @Test fun `config capability and private candidate mapping preserve enabled and hidden state`() {
        val config = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
            beamSwapConfig = ProtocolBeamSwapConfig(true))
        assertEquals(config, ProtocolViewMapper.toProtocolMatchConfig(ProtocolViewMapper.mapMatchConfig(config)))
        assertTrue(ProtocolViewMapper.mapCapabilities(ProtocolAccessCapabilities(beamSwapSupported = true)).beamSwapSupported)
        assertFalse(ProtocolViewMapper.mapCapabilities(ProtocolAccessCapabilities()).beamSwapSupported)
        assertFalse(ProtocolViewMapper.mapMatchConfig(ProtocolMatchConfig()).beamSwapConfig.enabled)
        val source = pending(ServerConnectionSessionTest.FakeServerConnection(), 11)
        val mapped = ProtocolViewMapper.mapSnapshot(source)
        val offer = mapped.beamSwap!!.pendingOffer!!
        assertEquals(BeamSwapWindow.WALL, offer.window)
        assertEquals(source.beamSwap!!.decisionId, mapped.beamSwap!!.decisionId)
        assertEquals(source.beamSwap!!.pendingOffer!!.deadlineMillis, offer.deadlineMillis)
        assertEquals(BeamSwapSlot.WallSlot(32), offer.candidates[0].slot)
        assertFalse(offer.candidates[0].marked)
        assertNull(offer.candidates[0].tile)
        assertTrue(offer.candidates[1].marked)
        assertEquals(134, offer.candidates[1].tile!!.tileId)
        assertEquals(listOf(0), offer.selectableHandTileIds)
        assertEquals(135, mapped.view.myDrawnTile!!.tileId)
    }

    @Test fun `dead wall projection maps rinshan indicator and replacement absolute slots`() {
        val base = pending(ServerConnectionSessionTest.FakeServerConnection(), 11)
        val candidates = listOf(
            ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.DeadWallSlot(2), false),
            ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.DeadWallSlot(6), true, ProtocolTile(132)),
            ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.ReplacementSlot(0), false))
        val beam = base.beamSwap!!
        val source = base.copy(beamSwap = beam.copy(window = ProtocolBeamSwapWindow.DEAD_WALL,
            pendingOffer = beam.pendingOffer!!.copy(window = ProtocolBeamSwapWindow.DEAD_WALL, candidates = candidates)))
        val offer = ProtocolViewMapper.mapSnapshot(source).beamSwap!!.pendingOffer!!
        assertEquals(BeamSwapWindow.DEAD_WALL, offer.window)
        assertEquals(listOf(BeamSwapSlot.DeadWallSlot(2), BeamSwapSlot.DeadWallSlot(6), BeamSwapSlot.ReplacementSlot(0)),
            offer.candidates.map { it.slot })
        assertNull(offer.candidates[0].tile)
        assertEquals(132, offer.candidates[1].tile!!.tileId)
        assertNull(offer.candidates[2].tile)
    }

    @Test fun `client offer and snapshot copies isolate caller owned lists`() {
        val mapped = ProtocolViewMapper.mapSnapshot(pending(ServerConnectionSessionTest.FakeServerConnection(), 11))
        val candidates = mapped.beamSwap!!.pendingOffer!!.candidates.toMutableList()
        val hand = mutableListOf(0)
        val offer = mapped.beamSwap!!.pendingOffer!!.copy(candidates = candidates, selectableHandTileIds = hand)
        val snapshot = mapped.copy(beamSwap = mapped.beamSwap!!.copy(pendingOffer = offer))
        val copied = snapshot.deepCopy()
        candidates.clear()
        hand.clear()
        assertEquals(2, offer.candidates.size)
        assertEquals(listOf(0), offer.selectableHandTileIds)
        assertEquals(snapshot, copied)
        assertThrows<UnsupportedOperationException> { (offer.candidates as MutableList<*>).clear() }
        assertThrows<UnsupportedOperationException> { (offer.selectableHandTileIds as MutableList<*>).clear() }
    }
}

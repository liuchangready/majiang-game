package com.majiang.client.api

import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.client.session.ServerConnectionSession
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.IOException
import java.util.concurrent.CompletableFuture

class SleeveBloomClientContractTest {
    private fun activation(id: String = "activate") = SleeveBloomRequest(id, "session-1", "round-1", "dec-cursor-10", 1)
    private fun selection(id: String = "choose") = SleeveBloomChoiceRequest(id, "session-1", "round-1", "dec-cursor-11", 1, "offer", SleeveBloomKeep.SECOND)
    private fun pending(fake: ServerConnectionSessionTest.FakeServerConnection, cursor: Long): ProtocolPlayerSnapshot {
        val base = fake.createSnapshot(cursor)
        return base.copy(view = base.view.copy(phase = ProtocolGamePhase.CHEAT_WINDOW, currentTurnPlayer = base.view.viewerSeat,
            myDrawnTile = ProtocolTile(135)), sleeveBloom = ProtocolSleeveBloomView(false, "SLEEVE_BLOOM_ALREADY_PENDING", 10,
            ProtocolSleeveBloomDrawKind.NORMAL, base.decision.decisionId, base.decision.controlEpoch, cursor,
            ProtocolSleeveBloomOfferView("offer", ProtocolTile(135), ProtocolTile(134), ProtocolSleeveBloomDrawKind.NORMAL,
                25_000, base.decision.decisionId, base.decision.controlEpoch)))
    }

    @Test fun `activation and choice share pending lock with ordinary actions and do not optimistically change cache`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitBloomFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val before = session.currentSnapshot
            val future = session.submitSleeveBloom(activation())
            assertTrue(session.isPending)
            assertEquals(before, session.currentSnapshot)
            assertEquals("activate", fake.lastSubmittedBloom!!.requestId)
            assertEquals("ACTION_IN_FLIGHT", session.submitSleeveBloomChoice(selection()).join().reason)
            assertEquals("ACTION_IN_FLIGHT", session.submit(ActionChoice("discard", "round-1", "dec-cursor-10", 1, "opt-1")).join().reason)
            delayed.complete(ProtocolActionReceipt("activate", ProtocolReceiptStatus.APPLIED, cursor = 11))
            assertEquals(ReceiptStatus.APPLIED, future.join().status)
            assertFalse(session.isPending)
            assertEquals(before, session.currentSnapshot, "Receipt alone must not invent a candidate or charge")
            val choiceDelay = CompletableFuture<ProtocolActionReceipt>()
            fake.customSubmitBloomChoiceFuture = choiceDelay
            val chosen = session.submitSleeveBloomChoice(selection())
            assertTrue(session.isPending)
            assertEquals(ProtocolSleeveBloomKeep.SECOND, fake.lastSubmittedBloomChoice!!.keep)
            assertEquals("ACTION_IN_FLIGHT", session.submitSleeveBloom(activation("again")).join().reason)
            choiceDelay.complete(ProtocolActionReceipt("choose", ProtocolReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED"))
            assertEquals("DEADLINE_EXPIRED", chosen.join().reason)
            assertFalse(session.isPending)
        }
    }

    @Test fun `offer push before receipt and later clearing snapshot cannot be resurrected by stale traffic`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitBloomFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val result = session.submitSleeveBloom(activation())
            fake.emitUpdate(ProtocolPlayerUpdate(pending(fake, 11), 10, emptyList()))
            val frozen = session.currentSnapshot!!
            assertEquals(134, frozen.sleeveBloom!!.pendingOffer!!.secondTile.tileId)
            fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(12), 11, emptyList()))
            delayed.complete(ProtocolActionReceipt("activate", ProtocolReceiptStatus.APPLIED, cursor = 11))
            assertEquals(ReceiptStatus.APPLIED, result.join().status)
            fake.emitUpdate(ProtocolPlayerUpdate(pending(fake, 11), 10, emptyList()))
            assertEquals(12L, session.currentSnapshot!!.cursor)
            assertNull(session.currentSnapshot!!.sleeveBloom)
            assertNotNull(frozen.sleeveBloom!!.pendingOffer)
            assertEquals(frozen, frozen.deepCopy())
        }
    }

    @Test fun `synchronous asynchronous and close failures release only their own pending request`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        fake.synchronousFailure = "submitSleeveBloom"
        assertTrue(session.submitSleeveBloom(activation()).isCompletedExceptionally)
        assertFalse(session.isPending)
        fake.synchronousFailure = null
        fake.customSubmitBloomFuture = CompletableFuture.failedFuture(IOException("offline"))
        assertTrue(session.submitSleeveBloom(activation()).isCompletedExceptionally)
        assertFalse(session.isPending)
        val old = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitBloomChoiceFuture = old
        assertTrue(session.submitSleeveBloomChoice(selection()).cancel(false))
        assertFalse(session.isPending)
        val fresh = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitBloomFuture = fresh
        val request = session.submitSleeveBloom(activation("fresh"))
        old.complete(ProtocolActionReceipt("choose", ProtocolReceiptStatus.APPLIED, cursor = 12))
        assertTrue(session.isPending)
        assertFalse(request.isDone)
        session.close()
        assertTrue(request.isCompletedExceptionally)
        assertFalse(session.isPending)
    }

    @Test fun `mapping roundtrips explicit feature config capability and private offer`() {
        val config = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("SLEEVE_BLOOM" to 10)),
            sleeveBloomConfig = ProtocolSleeveBloomConfig(true))
        assertEquals(config, ProtocolViewMapper.toProtocolMatchConfig(ProtocolViewMapper.mapMatchConfig(config)))
        assertTrue(ProtocolViewMapper.mapCapabilities(ProtocolAccessCapabilities(sleeveBloomSupported = true)).sleeveBloomSupported)
        val source = pending(ServerConnectionSessionTest.FakeServerConnection(), 11)
        val mapped = ProtocolViewMapper.mapSnapshot(source)
        assertEquals(source.sleeveBloom!!.decisionId, mapped.sleeveBloom!!.decisionId)
        assertEquals(135, mapped.sleeveBloom!!.pendingOffer!!.firstTile.tileId)
        assertEquals(source.sleeveBloom!!.pendingOffer!!.deadlineMillis, mapped.sleeveBloom!!.pendingOffer!!.deadlineMillis)
    }
}

package com.majiang.server.inprocess

import com.majiang.server.api.*
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

@Timeout(30)
class SleeveBloomInProcessContractTest {
    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test fun `local binding delivers private offers rejects spectators and publishes eventless first choice`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val connections = (0..3).map { server.createConnection() }
            val spectator = server.createConnection()
            try {
                val config = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("SLEEVE_BLOOM" to 10)),
                    sleeveBloomConfig = ProtocolSleeveBloomConfig(true))
                val bound = connections[0].createRoom(CreateRoomRequest(config = config)).await()
                for (seat in 1..3) connections[seat].joinRoom(JoinRoomRequest(bound.roomId, "Guest-$seat")).await()
                spectator.joinSpectator(JoinSpectatorRequest(bound.roomId, spectatorName = "Observer")).await()
                for (connection in connections) connection.setReady(true).await()
                assertEquals(ProtocolReceiptStatus.APPLIED, connections[0].start().await().status)
                val dealt = connections[0].snapshot().await()
                val discard = dealt.decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
                connections[0].submit(ProtocolActionChoice("discard", dealt.roundId, dealt.decision.decisionId,
                    dealt.decision.controlEpoch, discard.optionId)).await()
                for (seat in 1..3) {
                    val snapshot = connections[seat].snapshot().await()
                    snapshot.decision.options.firstOrNull { it.action is ProtocolLegalAction.Pass }?.let { pass ->
                        connections[seat].submit(ProtocolActionChoice("pass-$seat", snapshot.roundId, snapshot.decision.decisionId,
                            snapshot.decision.controlEpoch, pass.optionId)).await()
                    }
                }
                val before = connections[1].snapshot().await()
                val request = ProtocolSleeveBloomRequest("local-activate", before.sessionId, before.roundId!!,
                    before.decision.decisionId, before.decision.controlEpoch)
                assertEquals(ProtocolReceiptStatus.REJECTED, spectator.submitSleeveBloom(request).await().status)
                assertEquals(ProtocolReceiptStatus.APPLIED, connections[1].submitSleeveBloom(request).await().status)
                val offered = connections[1].snapshot().await()
                val offer = offered.sleeveBloom!!.pendingOffer!!
                for (seat in listOf(0, 2, 3)) assertNull(connections[seat].snapshot().await().sleeveBloom!!.pendingOffer)
                // Public snapshots have no private sleeve-bloom field, including after activation.
                val publicJson = ApiMessageCodec.encodeResponse(ApiResponse(publicSnapshot = spectator.publicSnapshot().await()))
                assertFalse(publicJson.contains("pendingOffer"))
                assertFalse(publicJson.contains("secondTile"))
                val choice = ProtocolSleeveBloomChoiceRequest("local-choice", offered.sessionId, offered.roundId!!,
                    offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId, ProtocolSleeveBloomKeep.FIRST)
                assertEquals(ProtocolReceiptStatus.REJECTED, spectator.submitSleeveBloomChoice(choice).await().status)
                val pushed = CompletableFuture<ProtocolPlayerSnapshot>()
                connections[1].onUpdate { update ->
                    if (update.snapshot.cursor > offered.cursor && update.snapshot.sleeveBloom?.pendingOffer == null) pushed.complete(update.snapshot)
                }.use {
                    assertEquals(ProtocolReceiptStatus.APPLIED, connections[1].submitSleeveBloomChoice(choice).await().status)
                    val cleared = pushed.await()
                    assertEquals(offer.firstTile, cleared.view.myDrawnTile)
                    assertEquals(10, cleared.cheatPoints[1])
                    assertEquals(before.clocks.first { it.seat == 1 }.deadline, cleared.clocks.first { it.seat == 1 }.deadline)
                }
            } finally { spectator.close(); connections.forEach { it.close() } }
        }
    }
}

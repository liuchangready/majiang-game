package com.majiang.server.tcp

import com.majiang.client.api.*
import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.transport.tcp.TcpClient
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/** Four real human clients ensure an ordinary draw, rather than the excluded dealer deal tile. */
@Timeout(45)
class BeamSwapClientTcpIntegrationTest {
    private fun <T> CompletableFuture<T>.await(): T = get(7, TimeUnit.SECONDS)
    @Test fun `real TCP activation private push binding replacement and eventless choice complete end to end`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            val connections = (0..3).map { TcpClient.connect("127.0.0.1", server.boundPort).await() }
            val sessions = connections.map { ServerConnectionSession(it) }
            try {
                val config = MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
                    beamSwapConfig = BeamSwapConfig(true))
                assertTrue(connections[0].capabilities().await().beamSwapSupported)
                sessions[0].createRoom("Host", config).await()
                val roomId = connections[0].boundRoomId!!
                for (seat in 1..3) sessions[seat].joinRoom(roomId, "Guest-$seat").await()
                for (session in sessions) session.setReady(true).await()
                assertEquals(ReceiptStatus.APPLIED, sessions[0].start().await().status)
                val dealt = sessions[0].snapshot().await()
                assertFalse(dealt.beamSwap!!.canApply)
                assertEquals("BEAM_SWAP_DEALER_EXTRA_EXCLUDED", dealt.beamSwap!!.disabledReason)
                val discard = dealt.decision.options.first { it.action is LegalActionView.DiscardTile }
                assertNotEquals(ReceiptStatus.REJECTED, sessions[0].submit(ActionChoice("first-discard", dealt.roundId,
                    dealt.decision.decisionId, dealt.decision.controlEpoch, discard.optionId)).await().status)
                for (seat in 1..3) {
                    val snapshot = sessions[seat].snapshot().await()
                    val pass = snapshot.decision.options.firstOrNull { it.action is LegalActionView.Pass }
                    if (pass != null) assertNotEquals(ReceiptStatus.REJECTED, sessions[seat].submit(ActionChoice("pass-$seat",
                        snapshot.roundId, snapshot.decision.decisionId, snapshot.decision.controlEpoch, pass.optionId)).await().status)
                }
                val before = sessions[1].snapshot().await()
                assertTrue(before.beamSwap!!.canApply, before.beamSwap!!.disabledReason)
                val offeredPush = CompletableFuture<GameSnapshotView>()
                sessions[1].onUpdate { if (it.snapshot.beamSwap?.pendingOffer != null) offeredPush.complete(it.snapshot) }.use {
                    val activation = BeamSwapRequest("tcp-beam", before.sessionId, before.roundId!!,
                        before.decision.decisionId, before.decision.controlEpoch)
                    val receipt = sessions[1].submitBeamSwap(activation).await()
                    assertEquals(ReceiptStatus.APPLIED, receipt.status)
                    val offered = offeredPush.await()
                    assertEquals(10, offered.cheatPoints[1])
                    assertTrue(offered.beamSwap!!.pendingOffer!!.candidates.all { !it.marked && it.tile == null })
                    assertEquals(before.view.myClosedTiles.map { it.tileId }.toSet(), offered.beamSwap!!.pendingOffer!!.selectableHandTileIds.toSet())
                    assertEquals(receipt, sessions[1].submitBeamSwap(activation).await())
                    for (seat in listOf(0, 2, 3)) assertNull(sessions[seat].snapshot().await().beamSwap!!.pendingOffer)
                    val replacement = TcpClient.connect("127.0.0.1", server.boundPort).await()
                    ServerConnectionSession(replacement).use { resumed ->
                        resumed.resumeSession(roomId, connections[1].resumeToken!!).await()
                        val restored = resumed.snapshot().await()
                        val offer = restored.beamSwap!!.pendingOffer!!
                        assertEquals(offered.beamSwap!!.pendingOffer!!.offerId, offer.offerId)
                        val choice = BeamSwapChoiceRequest("tcp-choice", restored.sessionId, restored.roundId!!,
                            restored.decision.decisionId, restored.decision.controlEpoch, offer.offerId, BeamSwapAction.SWAP, offer.selectableHandTileIds.first(), offer.candidates.first().slot)
                        val clearPush = CompletableFuture<GameSnapshotView>()
                        resumed.onUpdate { if (it.snapshot.cursor > restored.cursor && it.snapshot.beamSwap?.pendingOffer == null) clearPush.complete(it.snapshot) }.use {
                            val chosen = resumed.submitBeamSwapChoice(choice).await()
                            assertEquals(ReceiptStatus.APPLIED, chosen.status)
                            val cleared = clearPush.await()
                            assertEquals(before.view.myDrawnTile, cleared.view.myDrawnTile)
                            assertEquals(before.view.melds, cleared.view.melds)
                            assertFalse(cleared.view.myClosedTiles.any { it.tileId == choice.handTileId })
                            assertEquals(before.view.myClosedTiles.size, cleared.view.myClosedTiles.size)
                            assertEquals(1, cleared.view.myClosedTiles.count { it !in before.view.myClosedTiles })
                            assertEquals(10, cleared.cheatPoints[1])
                            assertEquals(before.clocks.first { it.seat == 1 }.deadline, cleared.clocks.first { it.seat == 1 }.deadline)
                            assertEquals(chosen, resumed.submitBeamSwapChoice(choice).await())
                            assertNull(resumed.resume(0).await().beamSwap!!.pendingOffer)
                        }
                    }
                }
            } finally { sessions.forEach { it.close() } }
        }
    }
}

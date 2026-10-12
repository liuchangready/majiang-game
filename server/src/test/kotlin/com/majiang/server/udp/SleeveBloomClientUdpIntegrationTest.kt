package com.majiang.server.udp

import com.majiang.client.api.*
import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.transport.udp.UdpClient
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/** Four real human clients ensure an ordinary draw, rather than the excluded dealer deal tile. */
@Timeout(45)
class SleeveBloomClientUdpIntegrationTest {
    private fun <T> CompletableFuture<T>.await(): T = get(7, TimeUnit.SECONDS)
    @Test fun `real UDP activation private push binding replacement and eventless choice complete end to end`() {
        val clock = FakeUdpClock()
        UdpTableServer(clock = clock, scheduler = FakeUdpScheduler(clock)).use { server ->
            server.start()
            val connections = (0..3).map { UdpClient.connect("127.0.0.1", server.boundPort, clock, FakeUdpScheduler(clock)).await() }
            val sessions = connections.map { ServerConnectionSession(it) }
            try {
                val config = MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 20 }, mapOf("SLEEVE_BLOOM" to 10)),
                    sleeveBloomConfig = SleeveBloomConfig(true))
                assertTrue(connections[0].capabilities().await().sleeveBloomSupported)
                sessions[0].createRoom("Host", config).await()
                val roomId = connections[0].boundRoomId!!
                for (seat in 1..3) sessions[seat].joinRoom(roomId, "Guest-$seat").await()
                for (session in sessions) session.setReady(true).await()
                assertEquals(ReceiptStatus.APPLIED, sessions[0].start().await().status)
                val dealt = sessions[0].snapshot().await()
                assertFalse(dealt.sleeveBloom!!.canApply)
                assertEquals("SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED", dealt.sleeveBloom!!.disabledReason)
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
                assertTrue(before.sleeveBloom!!.canApply, before.sleeveBloom!!.disabledReason)
                val offeredPush = CompletableFuture<GameSnapshotView>()
                sessions[1].onUpdate { if (it.snapshot.sleeveBloom?.pendingOffer != null) offeredPush.complete(it.snapshot) }.use {
                    val activation = SleeveBloomRequest("udp-bloom", before.sessionId, before.roundId!!,
                        before.decision.decisionId, before.decision.controlEpoch)
                    val receipt = sessions[1].submitSleeveBloom(activation).await()
                    assertEquals(ReceiptStatus.APPLIED, receipt.status)
                    val offered = offeredPush.await()
                    assertEquals(10, offered.cheatPoints[1])
                    assertEquals(before.view.myDrawnTile, offered.sleeveBloom!!.pendingOffer!!.firstTile)
                    assertEquals(receipt, sessions[1].submitSleeveBloom(activation).await())
                    for (seat in listOf(0, 2, 3)) assertNull(sessions[seat].snapshot().await().sleeveBloom!!.pendingOffer)
                    val replacement = UdpClient.connect("127.0.0.1", server.boundPort, clock, FakeUdpScheduler(clock)).await()
                    ServerConnectionSession(replacement).use { resumed ->
                        resumed.resumeSession(roomId, connections[1].resumeToken!!).await()
                        val restored = resumed.snapshot().await()
                        val offer = restored.sleeveBloom!!.pendingOffer!!
                        assertEquals(offered.sleeveBloom!!.pendingOffer!!.offerId, offer.offerId)
                        val choice = SleeveBloomChoiceRequest("udp-choice", restored.sessionId, restored.roundId!!,
                            restored.decision.decisionId, restored.decision.controlEpoch, offer.offerId, SleeveBloomKeep.SECOND)
                        val clearPush = CompletableFuture<GameSnapshotView>()
                        resumed.onUpdate { if (it.snapshot.cursor > restored.cursor && it.snapshot.sleeveBloom?.pendingOffer == null) clearPush.complete(it.snapshot) }.use {
                            val chosen = resumed.submitSleeveBloomChoice(choice).await()
                            assertEquals(ReceiptStatus.APPLIED, chosen.status)
                            val cleared = clearPush.await()
                            assertEquals(offer.secondTile, cleared.view.myDrawnTile)
                            assertEquals(10, cleared.cheatPoints[1])
                            assertEquals(before.clocks.first { it.seat == 1 }.deadline, cleared.clocks.first { it.seat == 1 }.deadline)
                            assertEquals(chosen, resumed.submitSleeveBloomChoice(choice).await())
                            assertNull(resumed.resume(0).await().sleeveBloom!!.pendingOffer)
                        }
                    }
                }
            } finally { sessions.forEach { it.close() } }
        }
    }
}

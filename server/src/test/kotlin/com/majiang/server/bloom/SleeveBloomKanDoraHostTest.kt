package com.majiang.server.bloom

import com.majiang.access.ReceiptStatus
import com.majiang.application.ActionChoice
import com.majiang.cheat.bloom.*
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.model.*
import com.majiang.server.host.ServerTableHost
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

@Timeout(40)
class SleeveBloomKanDoraHostTest {
    private fun <T> CompletableFuture<T>.await(): T = get(10, TimeUnit.SECONDS)

    @Test fun `serial host and wire snapshots reveal delayed dora only on discard and replay never duplicates it`() {
        val quad = (0..3).map { Tile(Suit.SOU, 8, it) }
        val s = TestTableBuilder.create(melds = mapOf(0 to listOf(Meld(MeldType.PON, quad.take(3), 3, quad[0]))),
            drawn = 0 to quad[3], phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        s.cheatPoints.fill(100)
        val config = MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 100 }, mapOf("SLEEVE_BLOOM" to 10)),
            sleeveBloomConfig = SleeveBloomConfig(true))
        ServerTableHost(config, clock = MonotonicClock { 1_000L },
            controllers = List(4) { SeatController.RemoteHuman(false) }, initialState = s).use { host ->
            fun action(id: String, predicate: (GameAction) -> Boolean): ActionChoice {
                val snap = host.snapshot(0).await()
                return ActionChoice(id, snap.roundId, snap.decision.decisionId, snap.decision.controlEpoch,
                    snap.decision.options.first { predicate(it.action) }.optionId)
            }
            fun assertCount(count: Int) {
                for (seat in 0..3) {
                    val wire = EngineProtocolMapper.toProtocolSnapshot(host.snapshot(seat).await())
                    val restored = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = wire))).snapshot!!
                    assertEquals(count, restored.view.doraIndicators.size)
                }
                assertEquals(count, host.publicSnapshot().await().board!!.doraIndicators.size)
            }
            val publicUpdates = CopyOnWriteArrayList<ProtocolPublicUpdate>()
            val revealed = CompletableFuture<ProtocolPublicUpdate>()
            host.subscribePublic {
                publicUpdates.add(it)
                if (it.visibleEvents.any { event -> event is ProtocolGameEvent.DoraRevealed }) revealed.complete(it)
            }.use {
                val kan = action("kan") { it is GameAction.DeclareKan && it.kanType == MeldType.ADDED_KAN }
                assertEquals(ReceiptStatus.APPLIED, host.submitChoice(0, kan).await().status)
                for (seat in 1..3) {
                    val current = host.snapshot(seat).await()
                    current.decision.options.firstOrNull { it.action is GameAction.Pass }?.let { option ->
                        host.submitChoice(seat, ActionChoice("pass-$seat", current.roundId, current.decision.decisionId,
                            current.decision.controlEpoch, option.optionId)).await()
                    }
                }
                assertCount(1)
                val before = host.snapshot(0).await()
                val activation = SleeveBloomRequest("bloom", before.sessionId, before.roundId!!,
                    before.decision.decisionId, before.decision.controlEpoch)
                assertEquals(ReceiptStatus.APPLIED, host.submitSleeveBloom(0, activation).await().status)
                val offered = host.snapshot(0).await()
                val choice = SleeveBloomChoiceRequest("choose", offered.sessionId, offered.roundId!!,
                    offered.decision.decisionId, offered.decision.controlEpoch,
                    offered.sleeveBloom!!.pendingOffer!!.offerId, SleeveBloomKeep.SECOND)
                val chosen = host.submitSleeveBloomChoice(0, choice).await()
                assertEquals(ReceiptStatus.APPLIED, chosen.status)
                assertCount(1)
                assertTrue(publicUpdates.flatMap { it.visibleEvents }.none { it is ProtocolGameEvent.DoraRevealed })
                val discard = action("discard") { it is GameAction.DiscardTile }
                val receipt = host.submitChoice(0, discard).await()
                assertEquals(ReceiptStatus.APPLIED, receipt.status)
                assertCount(2)
                assertEquals(2, revealed.await().snapshot.board!!.doraIndicators.size)
                val publicCursor = host.publicSnapshot().await().cursor
                assertEquals(chosen, host.submitSleeveBloomChoice(0, choice).await())
                assertEquals(receipt, host.submitChoice(0, discard).await())
                assertCount(2)
                assertEquals(publicCursor, host.publicSnapshot().await().cursor)
                assertEquals(1, publicUpdates.flatMap { it.visibleEvents }.filterIsInstance<ProtocolGameEvent.DoraRevealed>().size)
            }
        }
    }
}

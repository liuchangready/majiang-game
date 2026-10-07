package com.majiang.server.host

import com.majiang.application.ActionChoice
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.server.api.ResumeRequest
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GameAction
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

@Timeout(15)
class V4RecoveryProjectionPreservationTest {
    private class TestClock(var now: Long = 0L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    private fun winningState(): GameState {
        fun tiles(suit: Suit, ranks: String): List<Tile> {
            val copies = mutableMapOf<Int, Int>()
            return ranks.map { ch ->
                val rank = ch.digitToInt()
                Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
            }
        }
        val hand = tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") +
            tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")
        return TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.SOU, 4)).also {
            it.riichiDeclared[0] = true
            it.players[0].riichiDeclared = true
            it.scores[0] -= 1000
            it.riichiSticks++
        }
    }

    private fun settleByTsumo(host: ServerTableHost): Pair<String, ActionChoice> {
        val decision = host.decision(0).await()
        val win = decision.options.first { it.action is GameAction.DeclareTsumo }
        val oldChoice = ActionChoice("settle-${decision.roundId}", decision.roundId,
            decision.decisionId, decision.controlEpoch, win.optionId)
        assertEquals(com.majiang.access.ReceiptStatus.APPLIED, host.submitChoice(0, oldChoice).await().status)
        return requireNotNull(decision.roundId) to oldChoice
    }

    @Test
    fun `configured nondefault cheat points reach real room player snapshots`() {
        val balances = listOf(7, 11, 19, 23)
        val config = MatchConfig(
            cheatEconomy = CheatEconomyConfig(enabled = true, initialPoints = balances),
            shuffleConfig = ShuffleConfig(enabled = false)
        )
        InProcessServer().use { server ->
            val player = server.createLocalTable(config = config)
            assertEquals(balances, player.snapshot().await().cheatPoints)
            assertEquals(ProtocolReceiptStatus.APPLIED, player.start().await().status)
            val dealt = player.snapshot().await()
            assertEquals(balances, dealt.cheatPoints)
            assertEquals(14, dealt.view.myClosedTiles.size + listOfNotNull(dealt.view.myDrawnTile).size)
            assertEquals(dealt.sessionId, server.currentHost!!.publicSnapshot().await().sessionId)
        }
    }

    @Test
    fun `new round clears terminal projection while preserving prior public record and rejects old choice`() {
        val state = winningState()
        val host = ServerTableHost(
            config = MatchConfig(allowAgariYame = false, shuffleConfig = ShuffleConfig(enabled = false)),
            initialState = state,
            clock = TestClock(),
            controllers = List(4) { SeatController.RemoteHuman(false) }
        )
        host.use {
            val (oldRoundId, oldChoice) = settleByTsumo(host)
            val terminal = host.snapshot(0).await()
            assertEquals(oldRoundId, terminal.settlementRoundId)
            assertTrue(terminal.settlementEvents.any { it is GameEvent.ScoreSettled })
            assertTrue(terminal.session!!.roundRecords.any { it.roundId == oldRoundId })

            for (seat in 0..3) {
                val receipt = host.nextRound(seat).await()
                assertTrue(receipt.status != com.majiang.access.ReceiptStatus.REJECTED)
            }
            val fresh = host.snapshot(0).await()
            val public = host.publicSnapshot().await()
            assertNotEquals(oldRoundId, fresh.roundId)
            assertNull(fresh.settlementRoundId)
            assertTrue(fresh.settlementEvents.isEmpty())
            assertNull(public.settlementRoundId)
            assertTrue(public.settlementEvents.isEmpty())
            assertTrue(fresh.session!!.roundRecords.any { it.roundId == oldRoundId })
            assertTrue(public.session!!.roundRecords.any { it.roundId == oldRoundId })
            assertEquals(com.majiang.access.ReceiptStatus.REJECTED,
                host.submitChoice(0, oldChoice.copy(requestId = "stale-old-round")).await().status)
            assertEquals(fresh.roundId, host.snapshot(0).await().roundId)
        }
    }

    @Test
    fun `fresh paused snapshots freeze authority time and resume without gifting time`() {
        val clock = TestClock()
        ServerTableHost(clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) }).use { host ->
            assertEquals(com.majiang.access.ReceiptStatus.APPLIED, host.start(0).await().status)
            clock.now = 5000L
            val before = host.snapshot(0).await().clocks.first { it.seat == 0 }
            assertEquals(23000L, before.deadline)
            assertEquals(5000L, before.serverNow)
            assertTrue(host.pause().await())
            clock.now = 55000L
            val paused = host.snapshot(0).await()
            val publicPaused = host.publicSnapshot().await()
            assertTrue(paused.paused)
            assertTrue(publicPaused.paused)
            assertEquals(5000L, paused.clocks.first { it.seat == 0 }.serverNow)
            assertEquals(5000L, publicPaused.clocks.first { it.seat == 0 }.serverNow)
            assertEquals(before.deadline, paused.clocks.first { it.seat == 0 }.deadline)

            assertTrue(host.resumeClock().await())
            val resumed = host.snapshot(0).await().clocks.first { it.seat == 0 }
            assertEquals(73000L, resumed.deadline)
            assertEquals(55000L, resumed.serverNow)
            assertEquals(before.deadline - before.serverNow, resumed.deadline - resumed.serverNow)
        }
    }

    @Test
    fun `late terminal player resume retains private and public results without leaking ledger reason`() {
        val state = winningState().also {
            it.scores[0] = 48900
            it.scores[1] = 100
            it.cheatPoints[0] = 10
        }
        val config = MatchConfig(
            cheatEconomy = CheatEconomyConfig(enabled = true, initialPoints = listOf(10, 0, 0, 0)),
            shuffleConfig = ShuffleConfig(enabled = false)
        )
        InProcessServer().use { server ->
            val room = server.registry.createRoom(
                isSingleplayer = true,
                initialState = state,
                initialConfig = ProtocolRoomConfig(matchConfig = EngineProtocolMapper.toProtocolMatchConfig(config))
            )
            val host = room.tableHost
            val secretReason = "private-ledger-reason-should-stay-off-wire"
            assertTrue(host.applyCheatLedgerEntry("private-debit", 0, -3, secretReason).await()
                is com.majiang.state.ActionResult.Accepted)
            val (roundId, _) = settleByTsumo(host)

            val late = server.createConnection()
            val bound = late.resumeSession(ResumeRequest(room.roomId, room.resumeTokens[0])).await()
            val privateSnapshot = late.snapshot().await()
            val publicSnapshot = host.publicSnapshot().await()
            assertEquals(0, bound.seat)
            assertEquals(roundId, privateSnapshot.settlementRoundId)
            assertEquals(roundId, publicSnapshot.settlementRoundId)
            assertEquals(7, privateSnapshot.cheatPoints[0])
            assertNotNull(privateSnapshot.session!!.finalResult)
            assertNotNull(publicSnapshot.session!!.finalResult)
            assertEquals(privateSnapshot.session!!.finalResult!!.finalScores,
                publicSnapshot.session!!.finalResult!!.finalScores)
            assertEquals(privateSnapshot.session!!.roundRecords.map { it.roundId },
                publicSnapshot.session!!.roundRecords.map { it.roundId })
            assertTrue(privateSnapshot.settlementEvents.any { it is ProtocolGameEvent.TsumoWin && it.breakdown != null })
            assertTrue(publicSnapshot.settlementEvents.any { it is ProtocolGameEvent.TsumoWin && it.breakdown != null })
            assertTrue(late.queryRoundResult(roundId).await().any { it is ProtocolGameEvent.ScoreSettled })
            assertTrue(host.queryPublicRoundResultEvents(roundId).await().any { it is ProtocolGameEvent.ScoreSettled })
            assertTrue(late.queryRoundResult("unknown-round").await().isEmpty())
            assertTrue(host.queryPublicRoundResultEvents("unknown-round").await().isEmpty())
            assertFalse(ApiMessageCodec.encodePublicSnapshot(publicSnapshot).contains(secretReason))
            late.close()
        }
    }
}

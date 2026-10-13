package com.majiang.presentation.architecture

import com.badlogic.gdx.Gdx
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.ActionChoice
import com.majiang.client.api.LegalActionView
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.SuitView
import com.majiang.ai.AIStrategy
import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.presentation.common.render.DiscardAnimation
import com.majiang.presentation.game.GameScreen
import com.majiang.session.PlayerView
import com.majiang.state.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.testing.TestTableBuilder
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

class RiichiPacingAcceptanceTest : GdxHeadlessTestRunner() {
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }
    private fun waiting() = tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") +
        tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")
    private val miss = Tile(Suit.WIND, 4)
    private val passAndDiscard = object : AIStrategy {
        override val name = "deterministic pacing acceptance"
        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? =
            legalActions.firstOrNull { it is GameAction.Pass }
                ?: legalActions.filterIsInstance<GameAction.DiscardTile>().firstOrNull { it.tile == view.myDrawnTile }
                ?: legalActions.firstOrNull { it is GameAction.DiscardTile }
    }
    private fun fixture(draw: Tile = miss, deferred: Boolean = true, riichi: Boolean = true): ApplicationCoordinator {
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), drawn = 0 to draw,
            wallPrefix = (1..3).flatMap { rank -> (0..3).map { Tile(Suit.WIND, rank, it) } })
        if (riichi) {
            s.riichiDeclared[0] = true
            s.players[0].riichiDeclared = true
            s.scores[0] -= 1000
            s.riichiSticks++
        }
        return ApplicationCoordinator(initialState = s, deferAutomaticActions = deferred).also {
            it.initializeMatch(listOf(SeatController.LocalHuman()) + List(3) { SeatController.Computer(passAndDiscard) })
        }
    }
    private fun accepted(result: ActionResult): List<GameEvent> {
        assertTrue(result is ActionResult.Accepted, "$result")
        return (result as ActionResult.Accepted).events
    }

    /** 排空 libGDX UI 队列：GameScreen.runOnUi 投递的快照/动画更新在 headless 后台线程异步落地 */
    private fun flushUi() {
        val done = CompletableFuture<Unit>()
        Gdx.app.postRunnable {
            Gdx.app.postRunnable {
                done.complete(Unit)
            }
        }
        done.get(3, TimeUnit.SECONDS)
    }

    @Test fun `legacy eager mode demonstrates why one submission could run multiple riichi laps`() {
        val c = fixture(deferred = false)
        val events = accepted(c.submitPlayerAction(0, GameAction.DiscardTile(0, miss)))
        assertTrue(events.filterIsInstance<GameEvent.TileDiscarded>().count { it.player == 0 } > 1)
    }

    @Test fun `paced submission and each host step discard at most once in real turn order`() {
        val c = fixture()
        val initial = accepted(c.submitPlayerAction(0, GameAction.DiscardTile(0, miss)))
        assertEquals(listOf(0), initial.filterIsInstance<GameEvent.TileDiscarded>().map { it.player })
        val discards = initial.filterIsInstance<GameEvent.TileDiscarded>().toMutableList()
        var attempts = 0
        while (discards.size < 12) {
            assertTrue(attempts++ < 100, "Paced game stalled before three laps")
            val before = c.activeRound!!.version
            val events = accepted(c.advanceAutomaticActions())
            assertTrue(c.activeRound!!.version - before <= 1, "A host tick must execute at most one action")
            val batch = events.filterIsInstance<GameEvent.TileDiscarded>()
            assertTrue(batch.size <= 1)
            discards.addAll(batch)
            TestTableBuilder.assertPhysical(c.activeRound!!.readState())
        }
        assertEquals(List(12) { it % 4 }, discards.map { it.player })
        assertEquals(12, discards.map { it.tile }.toSet().size)
        assertTrue(discards.filter { it.player == 0 }.all { it.isTsumogiri })
    }

    @Test fun `paced riichi self draw and legal concealed kan remain available until explicit choice`() {
        for (draw in listOf(Tile(Suit.SOU, 4), Tile(Suit.DRAGON, 1, 3))) {
            val c = fixture(draw)
            val version = c.activeRound!!.version
            repeat(20) { c.advanceAutomaticActions() }
            assertEquals(version, c.activeRound!!.version)
            assertEquals(draw, c.queryView(0).myDrawnTile)
            if (draw.suit == Suit.SOU) assertTrue(c.legalActions(0).any { it is GameAction.DeclareTsumo })
            else assertTrue(c.legalActions(0).any { it is GameAction.DeclareKan })
            assertTrue(c.eventBus.getHistory().none { it is GameEvent.TileDiscarded })
        }
    }

    @Test fun `paced automatic discard retains another human ron window`() {
        val opponent = tiles(Suit.MAN, "123789").map { it.copy(copyIndex = 1) } +
            tiles(Suit.PIN, "123") + tiles(Suit.DRAGON, "222") + tiles(Suit.WIND, "4")
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting(), 1 to opponent), drawn = 0 to Tile(Suit.WIND, 4, 1))
        s.riichiDeclared[0] = true
        s.players[0].riichiDeclared = true
        s.scores[0] -= 1000
        s.riichiSticks++
        val c = ApplicationCoordinator(initialState = s, deferAutomaticActions = true)
        c.initializeMatch(listOf(SeatController.LocalHuman(), SeatController.RemoteHuman(),
            SeatController.Computer(passAndDiscard), SeatController.Computer(passAndDiscard)))
        repeat(20) { c.advanceAutomaticActions() }
        assertEquals(GamePhase.RESPONSE_WINDOW, c.queryView(1).phase)
        assertTrue(GameAction.DeclareRon(1) in c.legalActions(1))
        val version = c.activeRound!!.version
        repeat(5) { c.advanceAutomaticActions() }
        assertEquals(version, c.activeRound!!.version)
        accepted(c.submitPlayerAction(1, GameAction.DeclareRon(1)))
        repeat(5) { c.advanceAutomaticActions() }
        assertEquals(1, c.eventBus.getHistory().filterIsInstance<GameEvent.RonWin>().size)
    }

    @Test fun `real endpoint and screen play riichi laps one discard at a time without advancing during animation`() {
        val session = FakeGameSession(initialView = TestViewBuilder.playerView(
            viewerSeat = 0,
            currentTurnPlayer = 0,
            phase = GamePhaseView.DISCARD,
            legalActions = listOf(LegalActionView.DiscardTile(0, TestViewBuilder.tile(SuitView.WIND, 4)))
        ))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        val decision = session.currentSnapshot.decision
        val discardOption = decision.options.single { it.action is LegalActionView.DiscardTile }
        val receipt = session.submit(ActionChoice(
            requestId = "riichi-pacing-discard",
            roundId = session.currentSnapshot.roundId,
            decisionId = decision.decisionId,
            controlEpoch = decision.controlEpoch,
            optionId = discardOption.optionId
        )).join()
        assertEquals(ReceiptStatus.APPLIED, receipt.status)
        flushUi()
        val played = mutableListOf<Int>()
        val sequence = listOf(0, 1, 2, 3, 0, 1, 2, 3)
        var seqIdx = 0
        while (played.size < 8 && seqIdx < sequence.size) {
            val seat = sequence[seqIdx++]
            val tile = TestViewBuilder.tile(SuitView.WIND, seat + 1, if (seqIdx <= 4) 0 else 1)
            val currentDiscards = session.snapshot.view.discards.map { it.toMutableList() }
            currentDiscards[seat].add(tile)
            session.update(session.snapshot.view.copy(discards = currentDiscards))
            flushUi()
            screen.updateDiscardAnimation(0f)
            val animation = screen.currentDiscardAnim ?: continue
            played.add(animation.seat)
            screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        }
        assertEquals(listOf(0, 1, 2, 3, 0, 1, 2, 3), played)
        screen.dispose()
    }
}

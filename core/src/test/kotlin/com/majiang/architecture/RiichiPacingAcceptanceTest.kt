package com.majiang.architecture

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.ai.AIStrategy
import com.majiang.application.ApplicationCoordinator
import com.majiang.application.LocalTableRuntime
import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.session.PlayerView
import com.majiang.screen.GameScreen
import com.majiang.render.DiscardAnimation
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

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

    @Test fun `local runtime waits for playback and never catches up multiple steps in a long frame`() {
        val c = fixture()
        val runtime = LocalTableRuntime(c)
        val version = c.activeRound!!.version
        repeat(5) { runtime.update(10f, true) }
        runtime.update(0.14f, false)
        assertEquals(version, c.activeRound!!.version)
        runtime.update(0.02f, false)
        assertEquals(version + 1, c.activeRound!!.version)
        repeat(5) { runtime.update(10f, true) }
        assertEquals(version + 1, c.activeRound!!.version)
        runtime.update(0.01f, false)
        assertEquals(version + 1, c.activeRound!!.version, "Busy time must not become scheduler debt")
        val beforeLongFrame = c.activeRound!!.version
        runtime.update(500f, false)
        assertTrue(c.activeRound!!.version - beforeLongFrame in 0..1)
        val beforeInvalid = c.activeRound!!.version
        listOf(Float.NaN, Float.POSITIVE_INFINITY, -1f, 0f).forEach { runtime.update(it, false) }
        assertEquals(beforeInvalid, c.activeRound!!.version)
        runtime.close()
        repeat(10) { runtime.update(1f, false) }
        assertEquals(beforeInvalid, c.activeRound!!.version)
    }

    @Test fun `paced riichi self draw and legal concealed kan remain available until explicit choice`() {
        for (draw in listOf(Tile(Suit.SOU, 4), Tile(Suit.DRAGON, 1, 3))) {
            val c = fixture(draw)
            val runtime = LocalTableRuntime(c)
            val version = c.activeRound!!.version
            repeat(20) { runtime.update(1f, false) }
            assertEquals(version, c.activeRound!!.version)
            assertEquals(draw, runtime.endpoint.view().myDrawnTile)
            if (draw.suit == Suit.SOU) assertTrue(runtime.endpoint.legalActions().any { it is GameAction.DeclareTsumo })
            else assertTrue(runtime.endpoint.legalActions().any { it is GameAction.DeclareKan })
            assertTrue(c.eventBus.getHistory().none { it is GameEvent.TileDiscarded })
            runtime.close()
        }
    }

    @Test fun `runtime never discards for an ordinary human or advances a closed table`() {
        val c = fixture(riichi = false)
        val runtime = LocalTableRuntime(c)
        repeat(10) { runtime.update(1f, false) }
        assertEquals(0L, c.activeRound!!.version)
        assertEquals(miss, runtime.endpoint.view().myDrawnTile)
        runtime.close()
        assertTrue(runtime.endpoint.submit(GameAction.DiscardTile(0, miss)) is ActionResult.Rejected)
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
        val runtime = LocalTableRuntime(c)
        repeat(20) { runtime.update(1f, false) }
        assertEquals(GamePhase.RESPONSE_WINDOW, c.queryView(1).phase)
        assertTrue(GameAction.DeclareRon(1) in c.legalActions(1))
        val version = c.activeRound!!.version
        repeat(5) { runtime.update(1f, false) }
        assertEquals(version, c.activeRound!!.version)
        accepted(c.submitPlayerAction(1, GameAction.DeclareRon(1)))
        repeat(5) { runtime.update(1f, false) }
        assertEquals(1, c.eventBus.getHistory().filterIsInstance<GameEvent.RonWin>().size)
        runtime.close()
    }

    @Test fun `real endpoint and screen play riichi laps one discard at a time without advancing during animation`() {
        val c = fixture()
        val runtime = LocalTableRuntime(c)
        val screen = GameScreen(MahjongGame(), runtime.endpoint)
        accepted(runtime.endpoint.submit(GameAction.DiscardTile(0, miss)))
        val played = mutableListOf<Int>()
        var steps = 0
        while (played.size < 8) {
            assertTrue(steps++ < 80)
            runtime.update(1f, screen.isDiscardPlaybackBusy)
            screen.updateDiscardAnimation(0f)
            val animation = screen.currentDiscardAnim ?: continue
            assertTrue(screen.discardQueue.isEmpty(), "Local host must not precompute future discard animations")
            played.add(animation.seat)
            val before = c.activeRound!!.version
            runtime.update(10f, screen.isDiscardPlaybackBusy)
            assertEquals(before, c.activeRound!!.version)
            screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        }
        assertEquals(listOf(0, 1, 2, 3, 0, 1, 2, 3), played)
        screen.dispose()
        runtime.close()
    }
}

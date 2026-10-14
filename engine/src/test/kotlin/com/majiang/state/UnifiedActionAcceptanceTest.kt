package com.majiang.state

import com.majiang.ai.AIStrategy
import com.majiang.application.ApplicationCoordinator
import com.majiang.event.*
import com.majiang.model.*
import com.majiang.session.PlayerView
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Adversarial strategies verify actual authority boundaries, not just interface signatures. */
class UnifiedActionAcceptanceTest {
    private fun humans(): MutableList<SeatController> = MutableList(4) { SeatController.RemoteHuman(false) }
    private fun strategy(block: (PlayerView, List<GameAction>) -> GameAction?) = object : AIStrategy {
        override val name = "adversarial acceptance"
        override fun decideAction(view: PlayerView, legalActions: List<GameAction>) = block(view, legalActions)
    }
    private fun loose(copy: Int = 0) = listOf(1, 5, 9).map { Tile(Suit.MAN, it, copy) } +
        listOf(1, 3, 9).map { Tile(Suit.SOU, it, copy) } +
        listOf(1, 5, 9).map { Tile(Suit.PIN, it, copy) } +
        (1..3).map { Tile(Suit.WIND, it, copy) } + Tile(Suit.DRAGON, 3, copy)

    private fun accepted(result: ActionResult): List<GameEvent> {
        assertTrue(result is ActionResult.Accepted, "$result")
        return (result as ActionResult.Accepted).events
    }

    private fun fingerprint(e: ApplicationCoordinator, bus: GameEventBus): String {
        val s = e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!
        return listOf(
            (0..3).map { PlayerView.fromGameState(s, it, e.legalActions(it)) },
            s.wall.toList(), s.deadWall.toList(), s.wallPointer, s.rinshanPointer,
            s.sameTurnFuriten.toList(), s.riichiFuriten.toList(), s.riichiSticks,
            e.activeRound?.responseWindow?.responses?.toMap(), bus.getHistory()
        ).toString()
    }

    @Test fun `invalid AI turn commands are dropped and replaced by one legal discard`() {
        val badActions: List<(GameState) -> GameAction> = listOf(
            { s -> GameAction.DiscardTile(1, s.wall[s.wallPointer]) },
            { s -> GameAction.DiscardTile(0, s.players[0].closedTiles.first()) },
            { s -> GameAction.DeclareRiichi(1, s.players[1].drawnTile!!) },
            { GameAction.DeclareTsumo(1) },
            { GameAction.DeclareAbortiveDraw(1) },
            { s -> GameAction.DeclareKan(1, listOf(s.players[1].drawnTile!!), MeldType.ADDED_KAN) },
            { GameAction.StartStacking() }
        )
        for ((index, bad) in badActions.withIndex()) {
            val draw = Tile(Suit.WIND, 4)
            val s = TestTableBuilder.create(hands = mapOf(1 to loose()), phase = GamePhase.DRAW,
                currentPlayer = 1, wallPrefix = listOf(draw))
            val bus = GameEventBus()
            val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = true)
            var calls = 0
            val ai = strategy { _, legal ->
                calls++
                val cur = e.activeRound!!.readState()
                bad(cur).also { assertFalse(it in legal, "Bad test fixture for case $index") }
            }
            e.initializeMatch(humans().apply { this[1] = SeatController.Computer(ai) })
            val events = accepted(e.handleAction(GameAction.DrawTile(1)))
            assertEquals(1, calls, "case=$index")
            assertEquals(listOf(draw), events.filterIsInstance<GameEvent.TileDiscarded>().filter { it.player == 1 }.map { it.tile })
            assertTrue(events.none { it is GameEvent.RiichiDeclared || it is GameEvent.TsumoWin || it is GameEvent.KanDeclared || it is GameEvent.StackingStarted })
            val postState = e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!
            assertFalse(postState.riichiDeclared[1])
            assertEquals(0, postState.totalKanCount)
            assertEquals(0, postState.riichiSticks)
            assertEquals(events, bus.getHistory())
            TestTableBuilder.assertPhysical(postState)
        }
    }

    @Test fun `null and exception AI decisions safely fallback without retrying the strategy`() {
        for (throws in listOf(false, true)) {
            val s = TestTableBuilder.create(hands = mapOf(1 to loose()), phase = GamePhase.DRAW,
                currentPlayer = 1, wallPrefix = listOf(Tile(Suit.WIND, 4)))
            val e = ApplicationCoordinator.createForTest(s, GameEventBus(), autoPlayAi = true)
            var calls = 0
            val ai = strategy { _, _ -> calls++; if (throws) error("strategy failure") else null }
            e.initializeMatch(humans().apply { this[1] = SeatController.Takeover(ai) })
            val events = accepted(e.handleAction(GameAction.DrawTile(1)))
            assertEquals(1, calls)
            assertEquals(1, events.filterIsInstance<GameEvent.TileDiscarded>().count { it.player == 1 })
            TestTableBuilder.assertPhysical(e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!)
        }
    }

    private fun ready(copy: Int) = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
        (2..4).map { Tile(suit, it, copy) }
    } + (6..8).map { Tile(Suit.SOU, it, copy) } + Tile(Suit.MAN, 5, copy)

    @Test fun `AI cannot submit a different eligible human seat's legal ron`() {
        val called = Tile(Suit.MAN, 5, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to ready(0), 3 to ready(1)), drawn = 2 to called)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = true)
        var calls = 0
        e.initializeMatch(humans().apply {
            this[3] = SeatController.Computer(strategy { _, _ -> calls++; GameAction.DeclareRon(0) })
        })
        accepted(e.submitPlayerAction(2, GameAction.DiscardTile(2, called)))
        assertEquals(1, calls)
        val window = e.activeRound?.responseWindow!!
        assertFalse(window.hasResponded(0))
        assertEquals(GameAction.Pass(3), window.responses[3])
        val post1 = e.activeRound!!.readState()
        assertFalse(post1.sameTurnFuriten[0])
        assertTrue(post1.sameTurnFuriten[3], "Safe fallback declines this AI's own legal ron")
        assertTrue(bus.getHistory().none { it is GameEvent.RonWin })
        accepted(e.submitPlayerAction(0, GameAction.DeclareRon(0)))
        if (GameAction.Pass(1) in e.legalActions(1)) accepted(e.submitPlayerAction(1, GameAction.Pass(1)))
        assertEquals(0, bus.getHistory().filterIsInstance<GameEvent.RonWin>().single().winner)
        TestTableBuilder.assertPhysical(e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!)
    }

    @Test fun `AI ron waits for remote human choices and preserves nearest winner priority`() {
        val called = Tile(Suit.MAN, 5, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to ready(0), 3 to ready(1)), drawn = 2 to called)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = true)
        e.initializeMatch(humans().apply {
            this[3] = SeatController.Computer(strategy { _, actions -> actions.first { it is GameAction.DeclareRon } })
        })
        accepted(e.submitPlayerAction(2, GameAction.DiscardTile(2, called)))
        assertEquals(GameAction.DeclareRon(3), e.activeRound!!.responseWindow!!.responses[3])
        assertFalse(e.activeRound!!.responseWindow!!.hasResponded(0))
        assertTrue(bus.getHistory().none { it is GameEvent.ScoreSettled })
        accepted(e.submitPlayerAction(0, GameAction.DeclareRon(0)))
        if (GameAction.Pass(1) in e.legalActions(1)) accepted(e.submitPlayerAction(1, GameAction.Pass(1)))
        assertEquals(3, bus.getHistory().filterIsInstance<GameEvent.RonWin>().single().winner)
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().size)
        TestTableBuilder.assertPhysical(e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!)
    }

    @Test fun `human illegal commands reject without mutating state events or response rights`() {
        for (remote in listOf(false, true)) {
            val s = TestTableBuilder.create(hands = mapOf(1 to loose()), drawn = 1 to Tile(Suit.WIND, 4))
            val bus = GameEventBus()
            val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = true)
            e.initializeMatch(humans().apply { this[1] = if (remote) SeatController.RemoteHuman() else SeatController.LocalHuman() })
            val bad = listOf(GameAction.DiscardTile(1, s.wall.first()), GameAction.DeclareTsumo(1),
                GameAction.DeclareRiichi(1, s.players[1].drawnTile!!),
                GameAction.DeclareKan(1, listOf(s.players[1].drawnTile!!), MeldType.ADDED_KAN), GameAction.Pass(1))
            for (action in bad) {
                val before = fingerprint(e, bus)
                assertTrue(e.submitPlayerAction(1, action) is ActionResult.Rejected, "$action")
                assertEquals(before, fingerprint(e, bus))
            }
        }
    }

    @Test fun `response window itself rejects forged or cross seat choices before recording`() {
        val tile = Tile(Suit.DRAGON, 1)
        val pass = GameAction.Pass(1)
        val window = ResponseWindow(12, 0, tile, mapOf(1 to listOf(pass), 2 to listOf(GameAction.Pass(2))))
        assertFalse(window.recordResponse(1, GameAction.DeclareRon(1)))
        assertFalse(window.recordResponse(1, GameAction.Pass(2)))
        assertFalse(window.recordResponse(0, GameAction.Pass(0)))
        assertTrue(window.responses.isEmpty())
        assertTrue(window.recordResponse(1, pass))
        assertFalse(window.recordResponse(1, pass))
        assertEquals(mapOf(1 to pass), window.responses)
    }

    @Test fun `response normalization accepts reordered legal physical tiles but rejects duplicate copies`() {
        val a = Tile(Suit.DRAGON, 1)
        val b = Tile(Suit.DRAGON, 1, 1)
        val called = Tile(Suit.DRAGON, 1, 2)
        val canonical = GameAction.DeclarePon(1, listOf(a, b))
        val window = ResponseWindow(12, 0, called, mapOf(1 to listOf(canonical, GameAction.Pass(1))))
        assertFalse(window.recordResponse(1, GameAction.DeclarePon(1, listOf(a, a))))
        assertTrue(window.responses.isEmpty())
        assertTrue(window.recordResponse(1, canonical.copy(tiles = listOf(b, a))))
        assertEquals(canonical, window.responses[1])
    }

    @Test fun `AI can only inspect a detached read only view of its own tiles`() {
        val s = TestTableBuilder.create(hands = mapOf(1 to loose()), phase = GamePhase.DRAW, currentPlayer = 1)
        val e = ApplicationCoordinator.createForTest(s, GameEventBus(), autoPlayAi = true)
        var captured: PlayerView? = null
        var savedTiles: List<Tile>? = null
        val ai = strategy { view, actions ->
            assertEquals(1, view.viewerSeat)
            assertEquals(e.activeRound!!.readState().players[1].allClosedTiles, view.myAllClosedTiles)
            assertThrows(UnsupportedOperationException::class.java) { (view.myClosedTiles as MutableList<Tile>).clear() }
            assertThrows(UnsupportedOperationException::class.java) { (actions as MutableList<GameAction>).clear() }
            captured = view
            savedTiles = view.myAllClosedTiles.toList()
            actions.filterIsInstance<GameAction.DiscardTile>().first { it.tile == view.myDrawnTile }
        }
        e.initializeMatch(humans().apply { this[1] = SeatController.Computer(ai) })
        accepted(e.handleAction(GameAction.DrawTile(1)))
        assertNotNull(captured)
        assertEquals(savedTiles, captured!!.myAllClosedTiles)
        assertNotEquals(savedTiles, e.activeRound!!.readState().players[1].allClosedTiles)
        TestTableBuilder.assertPhysical(e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!)
    }

    @Test fun `AI response action nested tile lists cannot mutate authority candidates`() {
        val called = Tile(Suit.DRAGON, 1, 2)
        val pair = listOf(Tile(Suit.DRAGON, 1), Tile(Suit.DRAGON, 1, 1))
        val s = TestTableBuilder.create(hands = mapOf(1 to pair), drawn = 0 to called)
        val e = ApplicationCoordinator.createForTest(s, GameEventBus(), autoPlayAi = true)
        var attempted = false
        val ai = strategy { _, actions ->
            val pon = actions.filterIsInstance<GameAction.DeclarePon>().first()
            attempted = true
            assertThrows(UnsupportedOperationException::class.java) { (pon.tiles as MutableList<Tile>).clear() }
            actions.first { it is GameAction.Pass }
        }
        e.initializeMatch(humans().apply { this[1] = SeatController.Computer(ai) })
        accepted(e.submitPlayerAction(0, GameAction.DiscardTile(0, called)))
        assertTrue(attempted)
        assertTrue(e.activeRound!!.readState().players[1].closedTiles.containsAll(pair))
        TestTableBuilder.assertPhysical(e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!)
    }

    @Test fun `valid AI added kan waits for remote chankan before modifying meld or rinshan`() {
        val ponTiles = (0..2).map { Tile(Suit.MAN, 5, it) }
        fun ts(suit: Suit, ranks: String): List<Tile> {
            val seen = mutableMapOf<Int, Int>()
            return ranks.map { c -> val rank = c.digitToInt()
                Tile(suit, rank, seen.getOrDefault(rank, 0)).also { seen[rank] = it.copyIndex + 1 } }
        }
        val waiting = ts(Suit.MAN, "46") + ts(Suit.PIN, "123789") + ts(Suit.SOU, "12366")
        val s = TestTableBuilder.create(hands = mapOf(1 to waiting), drawn = 0 to Tile(Suit.MAN, 5, 3),
            melds = mapOf(0 to listOf(Meld(MeldType.PON, ponTiles, 3, ponTiles[0]))))
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = true)
        e.initializeMatch(humans().apply {
            this[0] = SeatController.Computer(strategy { _, actions ->
                actions.filterIsInstance<GameAction.DeclareKan>().first { it.kanType == MeldType.ADDED_KAN }
            })
        })
        accepted(e.advanceAutomaticActions())
        val post1 = e.activeRound!!.readState()
        assertEquals(GamePhase.CHANKAN_WINDOW, post1.phase)
        assertEquals(MeldType.PON, post1.players[0].melds.single().type)
        assertEquals(0, post1.rinshanPointer)
        assertEquals(0, post1.totalKanCount)
        assertTrue(GameAction.DeclareRon(1) in e.legalActions(1))
        accepted(e.submitPlayerAction(1, GameAction.DeclareRon(1)))
        for (seat in 2..3) if (GameAction.Pass(seat) in e.legalActions(seat)) accepted(e.submitPlayerAction(seat, GameAction.Pass(seat)))
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.RonWin>().single().winner)
        assertEquals(0, (e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!).rinshanPointer)
        assertTrue(bus.getHistory().none { it is GameEvent.KanDeclared || it is GameEvent.RinshanDraw })
        TestTableBuilder.assertPhysical(e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!)
    }
}

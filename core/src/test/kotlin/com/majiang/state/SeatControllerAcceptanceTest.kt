package com.majiang.state

import com.majiang.ai.*
import com.majiang.event.*
import com.majiang.model.*
import com.majiang.session.PlayerView
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.random.Random

/** Codex-owned acceptance tests: controllers must not change a seat's rule rights. */
class SeatControllerAcceptanceTest {
    private fun humans(): MutableList<SeatController> = mutableListOf(
        SeatController.LocalHuman(false), SeatController.RemoteHuman(false),
        SeatController.RemoteHuman(false), SeatController.RemoteHuman(false))

    private fun strategy(block: (PlayerView, List<GameAction>) -> GameAction?) = object : AIStrategy {
        override val name = "acceptance strategy"
        override fun decideAction(view: PlayerView, legalActions: List<GameAction>) = block(view, legalActions)
    }

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val r = c.digitToInt()
            Tile(suit, r, copies.getOrDefault(r, 0)).also { copies[r] = it.copyIndex + 1 }
        }
    }

    private fun waiting() = tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") +
        tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")

    private fun markRiichi(s: GameState, seat: Int) {
        s.riichiDeclared[seat] = true
        s.players[seat].riichiDeclared = true
        s.scores[seat] -= 1000
        s.riichiSticks++
    }

    private fun checked(s: GameState) {
        TestTableBuilder.assertPhysical(s)
        StateInvariants.assertValid(s)
        assertEquals(100000, s.scores.sum() + s.riichiSticks * 1000)
    }

    private fun accepted(result: ActionResult): List<GameEvent> {
        assertTrue(result is ActionResult.Accepted, "Expected Accepted, got $result")
        return (result as ActionResult.Accepted).events
    }

    @Test fun `four human seats complete a round only through their own explicit commands`() {
        val s = GameState()
        val e = GameStateMachine(s, GameEventBus(), random = Random(742))
        e.initializeMatch(humans())
        val initial = accepted(e.startNewRound())
        assertTrue(initial.none { it is GameEvent.TileDiscarded })
        var steps = 0
        while (s.phase !in setOf(GamePhase.ROUND_END, GamePhase.MATCH_END)) {
            assertTrue(steps++ < 600)
            val actor = (0..3).firstOrNull { e.legalActions(it).isNotEmpty() }
            assertNotNull(actor)
            val actions = e.legalActions(actor!!)
            val chosen = actions.firstOrNull { it is GameAction.Pass }
                ?: actions.filterIsInstance<GameAction.DiscardTile>().firstOrNull { it.tile == s.players[actor].drawnTile }
                ?: actions.first { it is GameAction.DiscardTile }
            val events = accepted(e.submitPlayerAction(actor, chosen))
            assertEquals(if (chosen is GameAction.DiscardTile) 1 else 0,
                events.filterIsInstance<GameEvent.TileDiscarded>().size)
            checked(s)
        }
        assertTrue(steps > 20)
    }

    @Test fun `configuration is copied and invalid initialization leaves old configuration intact`() {
        val e = GameStateMachine(GameState(), GameEventBus())
        assertFalse(e.hasControllerConfiguration)
        val config = humans()
        e.initializeMatch(config)
        config[1] = SeatController.Computer()
        assertEquals(SeatController.RemoteHuman(false), e.controllerFor(1))
        val before = (0..3).map(e::controllerFor)
        assertThrows(IllegalArgumentException::class.java) { e.initializeMatch(before.take(3)) }
        assertThrows(IllegalArgumentException::class.java) { e.initializeMatch(humanIndex = 4) }
        assertThrows(IllegalArgumentException::class.java) {
            e.initializeMatch(0, mapOf(1 to AIPlayer(2)))
        }
        assertEquals(before, (0..3).map(e::controllerFor))
        assertThrows(IllegalArgumentException::class.java) { e.controllerFor(-1) }
        assertTrue(e.hasControllerConfiguration)
        assertEquals(-1, e.humanPlayerIndex)
    }

    @Test fun `legacy initialization retains seat and selected strategy`() {
        val chosen = TrivialAIStrategy()
        val e = GameStateMachine(GameState(), GameEventBus())
        e.initializeMatch(2, mapOf(1 to AIPlayer(1, chosen)))
        assertEquals(2, e.humanPlayerIndex)
        assertTrue(e.controllerFor(2) is SeatController.LocalHuman)
        assertSame(chosen, (e.controllerFor(1) as SeatController.Computer).strategy)
        e.initializeMatch(-1)
        assertEquals(-1, e.humanPlayerIndex)
        assertTrue((0..3).all { e.controllerFor(it) is SeatController.Computer })
    }

    @Test fun `remote human is never automatically played even with AI automation enabled`() {
        var calls = 0
        val ai = strategy { _, actions -> calls++; actions.firstOrNull() }
        val s = TestTableBuilder.create(phase = GamePhase.DRAW, currentPlayer = 1)
        val e = GameStateMachine(s, GameEventBus())
        e.initializeMatch(listOf(SeatController.LocalHuman(), SeatController.RemoteHuman(),
            SeatController.Computer(ai), SeatController.Takeover(ai)))
        val events = accepted(e.submitPlayerAction(1, GameAction.DrawTile(1)))
        assertEquals(1, s.currentPlayer)
        assertNotNull(s.players[1].drawnTile)
        assertTrue(events.none { it is GameEvent.TileDiscarded })
        assertEquals(0, calls)
        assertTrue(e.legalActions(1).any { it is GameAction.DiscardTile })
        checked(s)
    }

    @Test fun `bound player submission rejects impersonation systems and controller owned seats`() {
        val s = TestTableBuilder.create(drawn = 1 to Tile(Suit.WIND, 4))
        val bus = GameEventBus()
        val e = GameStateMachine(s, bus, autoPlayAi = false)
        val config = humans().apply { this[1] = SeatController.Takeover() }
        e.initializeMatch(config)
        val action = GameAction.DiscardTile(1, s.players[1].drawnTile!!)
        val before = PlayerView.fromGameState(s, 1, e.legalActions(1))
        assertEquals(ActionResult.Rejected(RejectionReason.INVALID_PLAYER), e.submitPlayerAction(0, action))
        assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), e.submitPlayerAction(1, action))
        assertEquals(ActionResult.Rejected(RejectionReason.UNSUPPORTED_ACTION), e.submitPlayerAction(0, GameAction.StartStacking()))
        assertEquals(ActionResult.Rejected(RejectionReason.INVALID_PLAYER), e.submitPlayerAction(4, GameAction.Pass(4)))
        assertEquals(before, PlayerView.fromGameState(s, 1, e.legalActions(1)))
        assertTrue(bus.getHistory().isEmpty())
        accepted(e.handleAction(action)) // privileged simulation remains possible with autoPlayAi=false
        checked(s)
    }

    @Test fun `all human seats independently apply their riichi automation preference`() {
        for (seat in 0..3) for (automatic in listOf(false, true)) {
            val miss = Tile(Suit.WIND, 4)
            val s = TestTableBuilder.create(hands = mapOf(seat to waiting()), phase = GamePhase.DRAW,
                currentPlayer = seat, wallPrefix = listOf(miss))
            markRiichi(s, seat)
            val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
            val config = humans().apply {
                this[seat] = if (seat % 2 == 0) SeatController.LocalHuman(automatic) else SeatController.RemoteHuman(automatic)
            }
            e.initializeMatch(config)
            val events = accepted(e.submitPlayerAction(seat, GameAction.DrawTile(seat)))
            assertEquals(if (automatic) 1 else 0, events.filterIsInstance<GameEvent.TileDiscarded>().count { it.player == seat })
            if (!automatic) assertEquals(miss, s.players[seat].drawnTile)
            checked(s)
        }
    }

    @Test fun `remote riichi automation retains tsumo and legal closed kan choices`() {
        for (drawn in listOf(Tile(Suit.SOU, 4), Tile(Suit.DRAGON, 1, 3))) {
            val s = TestTableBuilder.create(hands = mapOf(2 to waiting()), phase = GamePhase.DRAW,
                currentPlayer = 2, wallPrefix = listOf(drawn))
            markRiichi(s, 2)
            val e = GameStateMachine(s, GameEventBus())
            e.initializeMatch(humans().apply { this[2] = SeatController.RemoteHuman(true) })
            val events = accepted(e.submitPlayerAction(2, GameAction.DrawTile(2)))
            assertEquals(drawn, s.players[2].drawnTile)
            assertTrue(events.none { it is GameEvent.TileDiscarded || it is GameEvent.TsumoWin || it is GameEvent.KanDeclared })
            assertTrue(e.legalActions(2).any { it is GameAction.DeclareTsumo || it is GameAction.DeclareKan })
            checked(s)
        }
    }

    @Test fun `global riichi switch overrides per seat automation`() {
        val s = TestTableBuilder.create(hands = mapOf(1 to waiting()), phase = GamePhase.DRAW,
            currentPlayer = 1, wallPrefix = listOf(Tile(Suit.WIND, 4)))
        markRiichi(s, 1)
        val e = GameStateMachine(s, GameEventBus(), autoRiichiDiscard = false)
        e.initializeMatch(humans().apply { this[1] = SeatController.RemoteHuman(true) })
        assertTrue(accepted(e.submitPlayerAction(1, GameAction.DrawTile(1))).none { it is GameEvent.TileDiscarded })
        checked(s)
    }

    @Test fun `takeover plays current seat once and restoring another seat preserves its hand`() {
        val drawn = Tile(Suit.WIND, 4)
        val s = TestTableBuilder.create(drawn = 1 to drawn)
        val e = GameStateMachine(s, GameEventBus())
        e.initializeMatch(humans())
        val events = accepted(e.setSeatController(1, SeatController.Takeover(TrivialAIStrategy())))
        assertEquals(listOf(drawn), events.filterIsInstance<GameEvent.TileDiscarded>().filter { it.player == 1 }.map { it.tile })
        assertTrue(e.controllerFor(1) is SeatController.Takeover)
        val before = PlayerView.fromGameState(s, 1, e.legalActions(1))
        assertTrue(accepted(e.setSeatController(1, SeatController.RemoteHuman(false))).isEmpty())
        assertEquals(before, PlayerView.fromGameState(s, 1, e.legalActions(1)))
        assertEquals(ActionResult.Rejected(RejectionReason.INVALID_PLAYER), e.setSeatController(8, SeatController.Computer()))
        checked(s)
    }

    @Test fun `controller restoration preserves riichi furiten and a recorded response`() {
        val called = Tile(Suit.MAN, 5, 3)
        fun ready(copy: Int) = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
            (2..4).map { Tile(suit, it, copy) }
        } + (6..8).map { Tile(Suit.SOU, it, copy) } + Tile(Suit.MAN, 5, copy)
        val s = TestTableBuilder.create(hands = mapOf(0 to ready(0), 3 to ready(1)), drawn = 2 to called)
        markRiichi(s, 3)
        val e = GameStateMachine(s, GameEventBus())
        e.initializeMatch(humans())
        accepted(e.submitPlayerAction(2, GameAction.DiscardTile(2, called)))
        val pass = strategy { _, actions -> actions.first { it is GameAction.Pass } }
        accepted(e.setSeatController(3, SeatController.Takeover(pass)))
        assertEquals(GameAction.Pass(3), e.currentResponseWindow!!.responses[3])
        assertTrue(s.riichiFuriten[3])
        val deposits = s.riichiSticks
        accepted(e.setSeatController(3, SeatController.RemoteHuman()))
        assertEquals(GameAction.Pass(3), e.currentResponseWindow!!.responses[3])
        assertTrue(s.riichiFuriten[3])
        assertTrue(s.riichiDeclared[3])
        assertEquals(deposits, s.riichiSticks)
        assertTrue(e.submitPlayerAction(3, GameAction.DeclareRon(3)) is ActionResult.Rejected)
        checked(s)
    }

    @Test fun `concurrent and listener reentry cannot interleave with an in progress command`() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val s = TestTableBuilder.create(phase = GamePhase.DRAW, currentPlayer = 1)
        val bus = GameEventBus()
        val e = GameStateMachine(s, bus)
        val blocking = strategy { view, actions ->
            entered.countDown()
            check(release.await(5, TimeUnit.SECONDS))
            actions.filterIsInstance<GameAction.DiscardTile>().first { it.tile == view.myDrawnTile }
        }
        e.initializeMatch(humans().apply { this[1] = SeatController.Computer(blocking) })
        var nested: ActionResult? = null
        bus.subscribe<GameEvent.TileDiscarded> { nested = e.setSeatController(0, SeatController.Computer()) }
        val worker = Executors.newSingleThreadExecutor()
        try {
            val running = worker.submit<ActionResult> { e.handleAction(GameAction.DrawTile(1)) }
            assertTrue(entered.await(5, TimeUnit.SECONDS))
            assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), e.advanceAutomaticActions())
            assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), e.setSeatController(2, SeatController.Computer()))
            assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), e.submitPlayerAction(0, GameAction.Pass(0)))
            assertThrows(IllegalStateException::class.java) { e.initializeMatch(humans()) }
            release.countDown()
            val events = accepted(running.get(5, TimeUnit.SECONDS))
            assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), nested)
            assertEquals(1, events.filterIsInstance<GameEvent.TileDiscarded>().count { it.player == 1 })
            assertTrue(e.controllerFor(0) is SeatController.LocalHuman)
            assertTrue(e.controllerFor(2) is SeatController.RemoteHuman)
            checked(s)
        } finally {
            release.countDown()
            worker.shutdownNow()
        }
    }
}

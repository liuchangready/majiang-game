package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.rules.RiichiRuleEngine
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class WinContextIntegrationContractTest {
    @Test
    fun `four identical first wind discards abort the round before the next draw`() {
        val state = TestTableBuilder.create(hands = (1..3).associateWith { listOf(Tile(Suit.WIND, 1, it)) },
            drawn = 0 to Tile(Suit.WIND, 1, 0))
        state.isFirstUninterruptedGo = true
        state.turnNumber = 0
        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
        for (seat in 0..3) {
            assertTrue(engine.handleAction(GameAction.DiscardTile(seat, Tile(Suit.WIND, 1, seat))) is ActionResult.Accepted)
            for (responder in 0..3) if (GameAction.Pass(responder) in engine.legalActions(responder)) {
                assertTrue(engine.handleAction(GameAction.Pass(responder)) is ActionResult.Accepted)
            }
        }
        val postState = engine.activeRound?.readState() ?: engine.lastEndedRoundSnapshot!!
        assertEquals(GamePhase.ROUND_END, postState.phase)
        assertEquals(com.majiang.event.DrawReason.FOUR_WINDS,
            bus.getHistory().filterIsInstance<com.majiang.event.GameEvent.DrawGame>().single().reason)
        assertEquals(1, bus.getHistory().filterIsInstance<com.majiang.event.GameEvent.ScoreSettled>().size)
        assertEquals(listOf(25000, 25000, 25000, 25000), postState.scores.toList())
        TestTableBuilder.assertPhysical(postState)
    }
    @Test
    fun `interrupted first circuit cannot produce earthly hand from an empty personal river`() {
        val state = TestTableBuilder.create(drawn = 1 to Tile(Suit.DRAGON, 1), currentPlayer = 1)
        state.isFirstUninterruptedGo = false
        val context = RiichiRuleEngine.INSTANCE.buildWinContext(state, 1, state.players[1].drawnTile!!, true)
        assertFalse(context.isChiihou)
        assertFalse(context.isTenhou)
    }

    @Test
    fun `closed kan cancels all ippatsu and retains rinshan context after reaching discard phase`() {
        val kanTiles = (0..3).map { Tile(Suit.MAN, 9, it) }
        val state = TestTableBuilder.create(hands = mapOf(0 to kanTiles), drawn = 0 to Tile(Suit.DRAGON, 1))
        state.ippatsuActive.fill(true)
        state.isFirstUninterruptedGo = true
        val engine = ApplicationCoordinator.createForTest(state, GameEventBus(), autoPlayAi = false)
        val action = engine.legalActions(0).filterIsInstance<GameAction.DeclareKan>().first { it.tiles.toSet() == kanTiles.toSet() }
        assertTrue(engine.handleAction(action) is ActionResult.Accepted)
        val post = engine.activeRound!!.readState()
        assertTrue(post.phase in setOf(GamePhase.DISCARD, GamePhase.CHEAT_WINDOW))
        assertTrue(post.ippatsuActive.none { it })
        assertFalse(post.isFirstUninterruptedGo)
        val context = RiichiRuleEngine.INSTANCE.buildWinContext(post, 0, post.players[0].drawnTile!!, true)
        assertTrue(context.isRinshan)
        assertFalse(context.isTenhou)
        TestTableBuilder.assertPhysical(post)
    }

    @Test
    fun `double riichi flag survives the declaration discard`() {
        val waiting = listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3),
            Tile(Suit.MAN, 4), Tile(Suit.MAN, 5), Tile(Suit.MAN, 6),
            Tile(Suit.PIN, 7), Tile(Suit.PIN, 8), Tile(Suit.PIN, 9),
            Tile(Suit.SOU, 2), Tile(Suit.SOU, 3), Tile(Suit.SOU, 4), Tile(Suit.SOU, 5)
        )
        val discard = Tile(Suit.WIND, 4)
        val state = TestTableBuilder.create(hands = mapOf(0 to waiting), drawn = 0 to discard)
        state.isFirstUninterruptedGo = true
        state.turnNumber = 0
        val engine = ApplicationCoordinator.createForTest(state, GameEventBus(), autoPlayAi = false)
        assertTrue(engine.handleAction(GameAction.DeclareRiichi(0, discard)) is ActionResult.Accepted)
        val post = engine.activeRound!!.readState()
        assertTrue(post.players[0].discardHistory.isNotEmpty())
        val context = RiichiRuleEngine.INSTANCE.buildWinContext(post, 0, Tile(Suit.SOU, 5, 1), true)
        assertTrue(context.isDoubleRiichi)
        assertTrue(context.isRiichi)
    }

    @Test
    fun `unsupported stacking plan cannot silently start a normal game`() {
        val state = GameState()
        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
        val result = engine.handleAction(GameAction.StartStacking(listOf("unsupported plan")))
        assertTrue(result is ActionResult.Rejected)
        assertTrue(engine.activeRound!!.readState().wall.isEmpty())
        assertTrue(bus.getHistory().isEmpty())
    }
}

package com.majiang.state

import com.majiang.event.*
import com.majiang.model.*
import com.majiang.session.LocalGameSession
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MatchIntegrationContractTest {
    private fun winningState(winner: Int = 0, dealer: Int = 0, dora: Tile? = null): GameState {
        val hand = listOf(
            Tile(Suit.MAN, 2), Tile(Suit.MAN, 3), Tile(Suit.MAN, 4),
            Tile(Suit.PIN, 2), Tile(Suit.PIN, 3), Tile(Suit.PIN, 4),
            Tile(Suit.SOU, 2), Tile(Suit.SOU, 3), Tile(Suit.SOU, 4),
            Tile(Suit.SOU, 6), Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.MAN, 5)
        )
        return TestTableBuilder.create(hands = mapOf(winner to hand), drawn = winner to Tile(Suit.MAN, 5, 1), dealer = dealer,
            deadWallPrefix = listOfNotNull(dora)).also { if (dora != null) it.doraIndicators.add(dora) }
    }

    @Test
    fun `win event reports total han including dora consistently with paid points`() {
        val state = winningState(dora = Tile(Suit.MAN, 4, 3))
        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false)
        assertTrue(engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Accepted)
        val win = bus.getHistory().filterIsInstance<GameEvent.TsumoWin>().single()
        assertEquals(6, win.han, "Tanyao + menzen tsumo + sanshoku = 4, plus two dora = 6")
        assertEquals(30, win.fu)
        assertEquals(18000, win.score)
        assertEquals(43000, state.scores[0])
    }

    @Test
    fun `round continuation applies dealer honba and wind metadata through either entry point`() {
        for (viaNextRound in listOf(true, false)) {
            val state = winningState(winner = 0, dealer = 3)
            state.roundNumber = 3
            state.honbaSticks = 2
            val bus = GameEventBus()
            val engine = GameStateMachine(state, bus, autoPlayAi = false)
            assertTrue(engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Accepted)
            assertEquals(GamePhase.ROUND_END, state.phase)
            val result = if (viaNextRound) engine.nextRound() else engine.handleAction(GameAction.StartStacking())
            assertTrue(result is ActionResult.Accepted)
            assertEquals(2, state.roundWind, "viaNextRound=$viaNextRound")
            assertEquals(0, state.roundNumber)
            assertEquals(0, state.dealerIndex)
            assertEquals(0, state.honbaSticks)
            assertEquals(14, state.players[0].allClosedTiles.size)
            TestTableBuilder.assertPhysical(state)
        }
    }

    @Test
    fun `south four nondealer win ends match once and rejects all continuation`() {
        val state = winningState(winner = 0, dealer = 3)
        state.roundWind = 2
        state.roundNumber = 3
        intArrayOf(35000, 22000, 22000, 21000).copyInto(state.scores)
        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false)
        assertTrue(engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Accepted)
        assertEquals(GamePhase.MATCH_END, state.phase)
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.MatchEnded>().size)
        val before = state.scores.toList()
        assertTrue(engine.nextRound() is ActionResult.Rejected)
        assertTrue(engine.handleAction(GameAction.StartStacking()) is ActionResult.Rejected)
        assertTrue(engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Rejected)
        assertEquals(before, state.scores.toList())
        assertEquals(100000, state.scores.sum() + state.riichiSticks * 1000)
    }

    @Test
    fun `final exhaustive draw awards leftover deposits once and clears the pot`() {
        val noten = (1..4).map { Tile(Suit.WIND, it) } + (1..3).map { Tile(Suit.DRAGON, it) } +
            listOf(Tile(Suit.MAN, 2), Tile(Suit.MAN, 4), Tile(Suit.PIN, 1), Tile(Suit.PIN, 7), Tile(Suit.SOU, 1), Tile(Suit.SOU, 9))
        val state = TestTableBuilder.create(hands = mapOf(3 to noten), drawn = 0 to Tile(Suit.DRAGON, 3, 3), dealer = 3)
        state.roundWind = 2
        state.roundNumber = 3
        state.riichiSticks = 3
        intArrayOf(32000, 22000, 22000, 21000).copyInto(state.scores)
        // Complete late-round fixture: consume the remaining live wall into historical discards.
        for (tile in state.wall.subList(state.wallPointer, state.wallEndExclusive).toList()) {
            state.tilePool.transfer(tile, TileLocation.WALL, TileLocation.discard(0))
            state.players[0].discards.add(tile)
            state.players[0].discardHistory.add(tile)
        }
        state.wallPointer = state.wallEndExclusive
        TestTableBuilder.assertPhysical(state)
        StateInvariants.assertValid(state)
        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false)
        assertTrue(engine.handleAction(GameAction.DiscardTile(0, state.players[0].drawnTile!!)) is ActionResult.Accepted)
        for (seat in 1..3) if (GameAction.Pass(seat) in engine.legalActions(seat)) {
            assertTrue(engine.handleAction(GameAction.Pass(seat)) is ActionResult.Accepted)
        }
        assertEquals(GamePhase.MATCH_END, state.phase)
        assertEquals(0, state.riichiSticks)
        assertEquals(100000, state.scores.sum())
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().size)
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.MatchEnded>().size)
        assertTrue(engine.nextRound() is ActionResult.Rejected)
        assertEquals(100000, state.scores.sum())
        TestTableBuilder.assertPhysical(state)
    }

    @Test
    fun `session observers and command results have independent settlement arrays`() {
        val state = winningState()
        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false)
        val session = LocalGameSession(0, engine, bus)
        var secondScores: List<Int>? = null
        session.subscribe { update -> update.visibleEvents.filterIsInstance<GameEvent.ScoreSettled>().forEach { it.newScores[0] = -999 } }
        session.subscribe { update -> update.visibleEvents.filterIsInstance<GameEvent.ScoreSettled>().forEach { secondScores = it.newScores.toList() } }
        val result = session.submit(GameAction.DeclareTsumo(0)) as ActionResult.Accepted
        assertEquals(state.scores.toList(), secondScores)
        val returned = result.events.filterIsInstance<GameEvent.ScoreSettled>().single()
        assertEquals(state.scores.toList(), returned.newScores.toList())
        returned.newScores[1] = -999
        assertEquals(state.scores.toList(), (bus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().single()).newScores.toList())
    }

    @Test
    fun `win event reports DORA and RED DORA breakdown in yakuList`() {
        // Hand with red 5万 (copyIndex 3) + normal 5万 (copyIndex 0)
        val hand = listOf(
            Tile(Suit.MAN, 2), Tile(Suit.MAN, 3), Tile(Suit.MAN, 4),
            Tile(Suit.PIN, 2), Tile(Suit.PIN, 3), Tile(Suit.PIN, 4),
            Tile(Suit.SOU, 2), Tile(Suit.SOU, 3), Tile(Suit.SOU, 4),
            Tile(Suit.SOU, 6), Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.MAN, 5, 3)
        )
        val dora = Tile(Suit.MAN, 4, 2) // 指示 5万
        val state = TestTableBuilder.create(
            hands = mapOf(0 to hand),
            drawn = 0 to Tile(Suit.MAN, 5, 0),
            dealer = 0,
            deadWallPrefix = listOf(dora)
        ).also { it.doraIndicators.add(dora) }

        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false)
        assertTrue(engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Accepted)
        val win = bus.getHistory().filterIsInstance<GameEvent.TsumoWin>().single()

        // 4 翻役 (断幺1 + 门清自摸1 + 三色同顺2) + 2 翻指示宝牌 + 1 翻赤宝牌 = 7 翻
        assertEquals(7, win.han)
        assertTrue(win.yakuList.contains("DORA +2"), "指示宝牌明细应列出 DORA +2, actual: ${win.yakuList}")
        assertTrue(win.yakuList.contains("RED DORA +1"), "赤宝牌明细应列出 RED DORA +1, actual: ${win.yakuList}")
    }

    @Test
    fun `ron win event reports DORA and RED DORA breakdown in yakuList`() {
        val winnerHand = listOf(
            Tile(Suit.MAN, 2), Tile(Suit.MAN, 3), Tile(Suit.MAN, 4),
            Tile(Suit.PIN, 2), Tile(Suit.PIN, 3), Tile(Suit.PIN, 4),
            Tile(Suit.SOU, 2), Tile(Suit.SOU, 3), Tile(Suit.SOU, 4),
            Tile(Suit.SOU, 6), Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.MAN, 5, 3)
        )
        val dora = Tile(Suit.MAN, 4, 1)
        val discardTile = Tile(Suit.MAN, 5, 0)
        val state = TestTableBuilder.create(
            hands = mapOf(0 to winnerHand),
            drawn = 1 to discardTile,
            currentPlayer = 1,
            phase = GamePhase.DISCARD,
            dealer = 0,
            deadWallPrefix = listOf(dora)
        ).also { it.doraIndicators.add(dora) }

        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false)
        val discardRes = engine.handleAction(GameAction.DiscardTile(1, discardTile))
        assertTrue(discardRes is ActionResult.Accepted)

        for (seat in listOf(2, 3)) {
            if (GameAction.Pass(seat) in engine.legalActions(seat)) {
                engine.handleAction(GameAction.Pass(seat))
            }
        }
        val ronRes = engine.handleAction(GameAction.DeclareRon(0))
        assertTrue(ronRes is ActionResult.Accepted)

        val win = bus.getHistory().filterIsInstance<GameEvent.RonWin>().single()
        assertTrue(win.yakuList.contains("DORA +2"), "Ron 结算明细应含 DORA +2, actual: ${win.yakuList}")
        assertTrue(win.yakuList.contains("RED DORA +1"), "Ron 结算明细应含 RED DORA +1, actual: ${win.yakuList}")
    }
}

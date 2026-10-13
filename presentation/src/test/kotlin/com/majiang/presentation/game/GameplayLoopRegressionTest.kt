package com.majiang.presentation.game

import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.ai.AIPlayer
import com.majiang.ai.TrivialAIStrategy
import com.majiang.application.ApplicationCoordinator
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.LegalActionView
import com.majiang.client.api.TileView
import com.majiang.event.GameEvent
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.rules.RiichiRuleEngine
import com.majiang.state.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Original five regression behaviors preserved across the V2 authority boundary. */
class GameplayLoopRegressionTest : GdxHeadlessTestRunner() {
    private val ruleEngine = RiichiRuleEngine.INSTANCE
    private fun noYakuRonTiles() = listOf(
        Tile(Suit.SOU, 1, 0), Tile(Suit.SOU, 1, 1), Tile(Suit.SOU, 1, 2),
        Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0), Tile(Suit.MAN, 4, 0),
        Tile(Suit.MAN, 4, 1), Tile(Suit.MAN, 5, 0), Tile(Suit.MAN, 6, 0),
        Tile(Suit.PIN, 6, 0), Tile(Suit.PIN, 7, 0), Tile(Suit.PIN, 8, 0), Tile(Suit.SOU, 5, 0)
    )

    @Test
    fun `无役荣和被拒 - DeclareRon提交后不产生结算事件且对局停留在RESPONSE_WINDOW`() {
        val discard = Tile(Suit.SOU, 5, 1)
        val state = TestTableBuilder.create(hands = mapOf(0 to noYakuRonTiles()),
            discards = mapOf(1 to listOf(discard)), phase = GamePhase.RESPONSE_WINDOW, currentPlayer = 1)
        val bus = GameEventBus()
        val fsm = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
        val hand = state.players[0]
        assertTrue(ruleEngine.isWinningHand(hand.allClosedTiles + discard))
        assertFalse(ruleEngine.hasAtLeastOneYaku(hand, discard, false, state, 0))
        assertTrue(fsm.handleAction(GameAction.DeclareRon(0)) is ActionResult.Rejected)
        assertTrue(bus.getHistory().isEmpty())
        assertEquals(GamePhase.RESPONSE_WINDOW, fsm.activeRound!!.readState().phase)
        TestTableBuilder.assertPhysical(fsm.activeRound!!.readState())
    }

    @Test
    fun `无役自摸被拒 - DeclareTsumo提交后不产生结算事件`() {
        val chiTiles = listOf(Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3))
        val meld = Meld(MeldType.CHI, chiTiles, 3, chiTiles.first())
        val hand = listOf(
            Tile(Suit.PIN, 7), Tile(Suit.PIN, 8), Tile(Suit.PIN, 9),
            Tile(Suit.SOU, 1), Tile(Suit.SOU, 2), Tile(Suit.SOU, 3),
            Tile(Suit.SOU, 2, 1), Tile(Suit.SOU, 3, 1), Tile(Suit.SOU, 4), Tile(Suit.SOU, 5)
        )
        val drawn = Tile(Suit.SOU, 5, 1)
        val state = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to drawn, melds = mapOf(0 to listOf(meld)))
        val bus = GameEventBus()
        val fsm = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
        assertFalse(state.players[0].isClosed)
        assertTrue(ruleEngine.isWinningHand(state.players[0].allClosedTiles))
        assertFalse(ruleEngine.hasAtLeastOneYaku(state.players[0], drawn, true, state, 0))
        assertTrue(fsm.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Rejected)
        assertTrue(bus.getHistory().isEmpty())
        assertEquals(GamePhase.DISCARD, fsm.activeRound!!.readState().phase)
        TestTableBuilder.assertPhysical(fsm.activeRound!!.readState())
    }

    @Test
    fun `UI与规则引擎一致性 - 无役时不显示RON与TSUMO按钮，有役时正确判定`() {
        val discard = Tile(Suit.SOU, 5, 1)
        val state = TestTableBuilder.create(hands = mapOf(0 to noYakuRonTiles()),
            discards = mapOf(1 to listOf(discard)), phase = GamePhase.RESPONSE_WINDOW, currentPlayer = 1)
        state.turnNumber = 5
        state.isFirstUninterruptedGo = false
        state.lastDiscard = discard
        state.lastDiscardPlayer = 1
        state.phase = GamePhase.RESPONSE_WINDOW
        fun view() = TestViewBuilder.playerView(
            viewerSeat = 0,
            phase = GamePhaseView.RESPONSE_WINDOW,
            currentTurnPlayer = 1,
            myClosedTiles = state.players[0].closedTiles.map { TileView.fromTileId(it.tileId) },
            myDrawnTile = null,
            lastDiscard = TileView.fromTileId(discard.tileId),
            lastDiscardPlayer = 1,
            legalActions = if (ruleEngine.hasAtLeastOneYaku(state.players[0], discard, false, state, 0))
                listOf(LegalActionView.DeclareRon(0), LegalActionView.Pass(0)) else emptyList()
        )
        val session = FakeGameSession(initialView = view())
        val screen = GameScreen(TestUiAssets.create(), session, FakeTableNavigation())
        try {
            assertFalse(screen.canHumanRon())
            assertFalse(screen.canHumanTsumo())
            state.players[0].closedTiles.removeAll { it.suit == Suit.SOU && it.rank == 1 }
            state.players[0].closedTiles.addAll((0..2).map { Tile(Suit.SOU, 3, it) })
            assertTrue(ruleEngine.hasAtLeastOneYaku(state.players[0], discard, false, state, 0))
            session.update(view())
            assertTrue(screen.canHumanRon())
        } finally {
            screen.dispose()
        }
    }

    @Test
    fun `PASS不吞AI荣和 - 人类跳过时待决AI荣和正常结算`() {
        val discard = Tile(Suit.MAN, 5, 0)
        val p0 = listOf(
            Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0), Tile(Suit.MAN, 4, 0),
            Tile(Suit.PIN, 3, 0), Tile(Suit.PIN, 4, 0), Tile(Suit.PIN, 5, 0),
            Tile(Suit.SOU, 4, 0), Tile(Suit.SOU, 5, 0), Tile(Suit.SOU, 6, 0),
            Tile(Suit.SOU, 6, 1), Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 8, 0), Tile(Suit.MAN, 5, 1)
        )
        val p1 = listOf(
            Tile(Suit.PIN, 2, 1), Tile(Suit.PIN, 3, 1), Tile(Suit.PIN, 4, 1),
            Tile(Suit.MAN, 3, 1), Tile(Suit.MAN, 4, 1), Tile(Suit.MAN, 5, 2),
            Tile(Suit.SOU, 4, 1), Tile(Suit.SOU, 5, 1), Tile(Suit.SOU, 6, 2),
            Tile(Suit.SOU, 7, 1), Tile(Suit.SOU, 7, 2), Tile(Suit.MAN, 4, 2), Tile(Suit.MAN, 6, 1)
        )
        val p3 = (1..4).map { Tile(Suit.WIND, it) } + (1..3).map { Tile(Suit.DRAGON, it) } +
            listOf(1, 7, 8, 9).map { Tile(Suit.MAN, it, 1) } + listOf(Tile(Suit.SOU, 1, 1), Tile(Suit.SOU, 9, 1))
        val state = TestTableBuilder.create(hands = mapOf(0 to p0, 1 to p1, 3 to p3), drawn = 2 to discard)
        val bus = GameEventBus()
        val fsm = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = true)
        fsm.initializeMatch(0, (1..3).associateWith { AIPlayer(it, TrivialAIStrategy()) })
        assertTrue(ruleEngine.hasAtLeastOneYaku(state.players[0], discard, false, state, 0))
        assertTrue(ruleEngine.hasAtLeastOneYaku(state.players[1], discard, false, state, 1))
        val wins = mutableListOf<GameEvent.RonWin>()
        bus.subscribe<GameEvent.RonWin> { wins.add(it) }
        assertTrue(fsm.handleAction(GameAction.DiscardTile(2, discard)) is ActionResult.Accepted)
        assertEquals(GamePhase.RESPONSE_WINDOW, fsm.activeRound!!.readState().phase)
        assertTrue(GameAction.DeclareRon(0) in fsm.legalActions(0))
        assertTrue(wins.isEmpty(), "Must wait for human response")
        val passResult = fsm.handleAction(GameAction.Pass(0))
        assertTrue(passResult is ActionResult.Accepted)
        assertEquals(1, (passResult as ActionResult.Accepted).events.filterIsInstance<GameEvent.RonWin>().size)
        assertEquals(1, passResult.events.filterIsInstance<GameEvent.ScoreSettled>().size)
        assertEquals(1, wins.size)
        assertEquals(1, wins.single().winner)
        assertEquals(2, wins.single().loser)
        assertEquals(discard, wins.single().winTile)
        val live = fsm.lastEndedRoundSnapshot ?: fsm.activeRound?.readState()!!
        assertEquals(GamePhase.ROUND_END, live.phase)
        TestTableBuilder.assertPhysical(live)
    }

    @Test
    fun `tsumogiri标志 - 打drawnTile时为true且打闭手牌为false`() {
        for (tsumogiri in listOf(true, false)) {
            val closed = Tile(Suit.MAN, 1, 0)
            val drawn = Tile(Suit.DRAGON, 1, 0)
            val state = TestTableBuilder.create(hands = mapOf(0 to listOf(closed)), drawn = 0 to drawn)
            val bus = GameEventBus()
            val fsm = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
            val events = mutableListOf<GameEvent.TileDiscarded>()
            bus.subscribe<GameEvent.TileDiscarded> { if (it.player == 0) events.add(it) }
            val tile = if (tsumogiri) drawn else closed
            assertTrue(fsm.handleAction(GameAction.DiscardTile(0, tile)) is ActionResult.Accepted)
            assertEquals(1, events.size)
            assertEquals(tile, events.single().tile)
            assertEquals(tsumogiri, events.single().isTsumogiri)
            TestTableBuilder.assertPhysical(fsm.activeRound!!.readState())
        }
    }
}

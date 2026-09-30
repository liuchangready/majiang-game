package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.session.PlayerView
import com.majiang.state.*
import com.majiang.testing.FakeGameSession
import com.majiang.testing.MeldChoiceFixtures
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class GameScreenSessionContractTest : GdxHeadlessTestRunner() {
    private fun scene(phase: GamePhase, actions: List<GameAction>): Pair<GameScreen, FakeGameSession> {
        val state = GameState()
        state.phase = phase
        state.currentPlayer = 2
        // Intentionally only the view is needed: the UI is not another rules engine.
        state.players[2].closedTiles.addAll((1..9).map { Tile(Suit.SOU, it) } + Tile(Suit.PIN, 5))
        state.players[2].drawnTile = Tile(Suit.PIN, 5, 1)
        val session = FakeGameSession(PlayerView.fromGameState(state, 2, actions))
        val screen = GameScreen(MahjongGame(), session)
        screen.startGame()
        return screen to session
    }

    @Test
    fun `exposed hand with eleven closed tiles still displays legal tsumo`() {
        val (screen, session) = scene(GamePhase.DISCARD, listOf(GameAction.DeclareTsumo(2)))
        assertEquals(11, session.view().myAllClosedTiles.size)
        assertTrue(screen.canHumanTsumo())
        assertTrue(screen.touchDown(900, 160, 0, 0))
        assertEquals(listOf(GameAction.DeclareTsumo(2)), session.submitted)
    }

    @Test
    fun `ron button submits the viewer seat rather than hardcoded zero`() {
        val (screen, session) = scene(GamePhase.RESPONSE_WINDOW,
            listOf(GameAction.DeclareRon(2), GameAction.Pass(2)))
        assertTrue(screen.canHumanRon())
        assertTrue(screen.touchDown(750, 160, 0, 0))
        assertEquals(listOf(GameAction.DeclareRon(2)), session.submitted)
    }

    @Test
    fun `removing legal win action immediately removes UI availability`() {
        val (screen, session) = scene(GamePhase.DISCARD, listOf(GameAction.DeclareTsumo(2)))
        assertTrue(screen.canHumanTsumo())
        session.update(session.view().copy(legalActions = emptyList()))
        assertFalse(screen.canHumanTsumo())
        screen.touchDown(900, 160, 0, 0)
        assertTrue(session.submitted.isEmpty())
    }

    @Test
    fun `meld and pass controls submit the exact authorized action for viewer two`() {
        // Real authoritative snapshots supply the tile ownership and public target now needed by previews.
        val views = listOf(MeldChoiceFixtures.openKan().view(), MeldChoiceFixtures.chi().view(), MeldChoiceFixtures.ownKans().view())
        for (view in views) {
            for (action in view.legalActions) {
                val x = when (action) {
                    is GameAction.Pass -> 900
                    is GameAction.DeclarePon -> 620
                    is GameAction.DeclareChi -> 490
                    is GameAction.DeclareKan -> 350
                    else -> continue
                }
                val session = FakeGameSession(view.copy(legalActions = listOf(action)))
                val screen = GameScreen(MahjongGame(), session)
                assertTrue(screen.touchDown(x, 160, 0, 0), "No control for $action")
                if (action !is GameAction.Pass) {
                    assertTrue(session.submitted.isEmpty(), "Preview must not execute $action")
                    assertEquals(action, screen.meldSelectionModel.confirmationAction())
                    assertTrue(screen.touchDown(720, 160, 0, 0))
                }
                assertEquals(listOf(action), session.submitted)
                screen.dispose()
            }
        }
    }

    @Test
    fun `settlement modal captures TsumoWin with DORA and RED DORA breakdown`() {
        val (screen, session) = scene(GamePhase.ROUND_END, emptyList())
        val winEvent = GameEvent.TsumoWin(
            turnNumber = 1,
            player = 2,
            winTile = Tile(Suit.PIN, 5, 3),
            yakuList = listOf("立直 1翻", "断幺九 1翻", "DORA +1", "RED DORA +1"),
            han = 4,
            fu = 30,
            score = 7700
        )
        session.update(session.view(), listOf(winEvent))

        assertTrue(screen.isSettlementShown, "结算弹窗应开启显示")
        val data = screen.currentSettlementData
        assertNotNull(data)
        assertEquals(listOf("立直 1翻", "断幺九 1翻", "DORA +1", "RED DORA +1"), data?.yakuList)
        assertEquals(Tile(Suit.PIN, 5, 3), data?.winTile)
        assertTrue(data?.winTile?.isRedFive == true)
        assertNull(data?.breakdown, "无 breakdown 时回退旧数据，breakdown 为 null")
        screen.dispose()
    }

    @Test
    fun `settlement modal captures full ScoreBreakdown when provided`() {
        val (screen, session) = scene(GamePhase.ROUND_END, emptyList())
        val breakdown = com.majiang.rules.ScoreBreakdown(
            yakuLines = listOf(Pair("断幺九", 1)),
            yakumanCount = 0,
            dora = com.majiang.rules.DoraBreakdown(1, 0, 1, 2),
            fu = com.majiang.rules.FuBreakdown(listOf(com.majiang.rules.FuLine("底符", 30)), 30, 30, 30, null),
            score = com.majiang.rules.ScoreCalculator.ScoreResult(30, 3, 1000, "3翻30符", false, 0),
            paymentLines = listOf("自摸：庄家付 2000、闲家各付 1000"),
            honbaAddition = 300,
            riichiAddition = 1000,
            totalPoints = 5300
        )
        val winEvent = GameEvent.TsumoWin(
            turnNumber = 1,
            player = 0,
            winTile = Tile(Suit.PIN, 5, 3),
            yakuList = listOf("断幺九 1翻", "DORA +1", "RED DORA +1"),
            han = 3,
            fu = 30,
            score = 4000,
            breakdown = breakdown
        )
        session.update(session.view(), listOf(winEvent))

        assertTrue(screen.isSettlementShown)
        val data = screen.currentSettlementData
        assertNotNull(data)
        assertNotNull(data?.breakdown)
        assertEquals(breakdown, data?.breakdown)
        assertEquals(5300, data?.breakdown?.totalPoints)
        screen.dispose()
    }
}

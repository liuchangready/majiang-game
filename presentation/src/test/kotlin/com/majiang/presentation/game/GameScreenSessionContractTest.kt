package com.majiang.presentation.game

import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class GameScreenSessionContractTest : GdxHeadlessTestRunner() {

    private fun scene(phase: GamePhaseView, actions: List<LegalActionView>): Pair<GameScreen, FakeGameSession> {
        val closed = (1..9).map { TestViewBuilder.tile(SuitView.SOU, it) } + TestViewBuilder.tile(SuitView.PIN, 5, 0)
        val drawn = TestViewBuilder.tile(SuitView.PIN, 5, 1)
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            phase = phase,
            myClosedTiles = closed,
            myDrawnTile = drawn,
            legalActions = actions
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        return screen to session
    }

    @Test
    fun `exposed hand with eleven closed tiles still displays legal tsumo`() {
        val (screen, session) = scene(GamePhaseView.DISCARD, listOf(LegalActionView.DeclareTsumo(2)))
        assertEquals(11, session.snapshot.view.myAllClosedTiles.size)
        assertTrue(screen.canHumanTsumo())
        assertTrue(screen.touchDown(900, 160, 0, 0))
        assertEquals(listOf(LegalActionView.DeclareTsumo(2)), session.submitted)
        screen.dispose()
    }

    @Test
    fun `ron button submits the viewer seat rather than hardcoded zero`() {
        val (screen, session) = scene(
            GamePhaseView.RESPONSE_WINDOW,
            listOf(LegalActionView.DeclareRon(2), LegalActionView.Pass(2))
        )
        assertTrue(screen.canHumanRon())
        assertTrue(screen.touchDown(750, 160, 0, 0))
        assertEquals(listOf(LegalActionView.DeclareRon(2)), session.submitted)
        screen.dispose()
    }

    @Test
    fun `removing legal win action immediately removes UI availability`() {
        val (screen, session) = scene(GamePhaseView.DISCARD, listOf(LegalActionView.DeclareTsumo(2)))
        assertTrue(screen.canHumanTsumo())
        session.update(session.snapshot.view.copy(legalActions = emptyList()))
        assertFalse(screen.canHumanTsumo())
        screen.touchDown(900, 160, 0, 0)
        assertTrue(session.submitted.isEmpty())
        screen.dispose()
    }

    @Test
    fun `meld and pass controls submit the exact authorized action for viewer two`() {
        val actions = listOf(
            LegalActionView.Pass(2),
            LegalActionView.DeclarePon(2, listOf(TestViewBuilder.tile(SuitView.PIN, 1, 0), TestViewBuilder.tile(SuitView.PIN, 1, 1))),
            LegalActionView.DeclareChi(2, TestViewBuilder.meld(MeldTypeView.CHI, listOf(TestViewBuilder.tile(SuitView.MAN, 1), TestViewBuilder.tile(SuitView.MAN, 2), TestViewBuilder.tile(SuitView.MAN, 3)), 1, TestViewBuilder.tile(SuitView.MAN, 1))),
            LegalActionView.DeclareKan(2, (0..3).map { TestViewBuilder.tile(SuitView.DRAGON, 1, it) }, MeldTypeView.CLOSED_KAN)
        )
        for (action in actions) {
            val x = when (action) {
                is LegalActionView.Pass -> 900
                is LegalActionView.DeclarePon -> 620
                is LegalActionView.DeclareChi -> 490
                is LegalActionView.DeclareKan -> 350
                else -> continue
            }
            val discardedTile = when (action) {
                is LegalActionView.DeclarePon -> TestViewBuilder.tile(SuitView.PIN, 1, 2)
                is LegalActionView.DeclareChi -> TestViewBuilder.tile(SuitView.MAN, 1)
                else -> null
            }
            val view = TestViewBuilder.playerView(
                viewerSeat = 2,
                phase = if (action is LegalActionView.DeclareKan) GamePhaseView.DISCARD else GamePhaseView.RESPONSE_WINDOW,
                currentTurnPlayer = if (action is LegalActionView.DeclareKan) 2 else 1,
                lastDiscard = discardedTile,
                lastDiscardPlayer = if (discardedTile != null) 1 else null,
                legalActions = listOf(action)
            )
            val session = FakeGameSession(initialView = view)
            val nav = FakeTableNavigation()
            val screen = GameScreen(TestUiAssets.create(), session, nav)
            assertTrue(screen.touchDown(x, 160, 0, 0), "No control for $action")
            if (action !is LegalActionView.Pass) {
                assertTrue(session.submitted.isEmpty(), "Preview must not execute $action")
                assertEquals(action, screen.meldSelectionModel.confirmationOption()?.action)
                assertTrue(screen.touchDown(720, 160, 0, 0))
            }
            assertEquals(listOf(action), session.submitted)
            screen.dispose()
        }
    }

    @Test
    fun `settlement modal captures TsumoWin with DORA and RED DORA breakdown`() {
        val (screen, session) = scene(GamePhaseView.ROUND_END, emptyList())
        val winEvent = GameEventView.TsumoWin(
            turnNumber = 1,
            player = 2,
            winTile = TestViewBuilder.tile(SuitView.PIN, 5, 3),
            yakuList = listOf("立直 1翻", "断幺九 1翻", "DORA +1", "RED DORA +1"),
            han = 4,
            fu = 30,
            score = 7700
        )
        session.update(session.snapshot.view, listOf(winEvent))

        assertTrue(screen.isSettlementShown, "结算弹窗应开启显示")
        val data = screen.currentSettlementData
        assertNotNull(data)
        assertEquals(listOf("立直 1翻", "断幺九 1翻", "DORA +1", "RED DORA +1"), data?.yakuList)
        assertEquals(TestViewBuilder.tile(SuitView.PIN, 5, 3), data?.winTile)
        assertTrue(data?.winTile?.isRedFive == true)
        assertNull(data?.breakdown, "无 breakdown 时回退旧数据，breakdown 为 null")
        screen.dispose()
    }
}

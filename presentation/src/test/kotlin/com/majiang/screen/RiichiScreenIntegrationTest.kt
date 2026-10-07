package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiScreenIntegrationTest : GdxHeadlessTestRunner() {

    @Test fun `real kan button preserves the next tsumo choice and real tsumo button settles`() {
        val fourth = TestViewBuilder.tile(SuitView.DRAGON, 1, 3)
        val win = TestViewBuilder.tile(SuitView.SOU, 4)
        val kanAction = LegalActionView.DeclareKan(2, (0..3).map { TestViewBuilder.tile(SuitView.DRAGON, 1, it) }, MeldTypeView.CLOSED_KAN)
        val tsumoAction = LegalActionView.DeclareTsumo(2)

        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            phase = GamePhaseView.DRAW,
            myDrawnTile = fourth,
            legalActions = listOf(kanAction)
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()

        assertTrue(screen.canHumanKan())
        assertFalse(screen.canHumanRon())
        assertTrue(screen.touchDown(350, 160, 0, 0))
        assertTrue(screen.meldSelectionModel.isSelecting)
        assertEquals(fourth, session.snapshot.view.myDrawnTile)
        assertTrue(screen.touchDown(720, 160, 0, 0))
        session.flushUi()

        // After kan draw, drawn tile is win, legal actions become DeclareTsumo
        val afterKan = session.snapshot.view.copy(
            myDrawnTile = win,
            legalActions = listOf(tsumoAction)
        )
        val winEvent = GameEventView.TsumoWin(
            turnNumber = 2,
            player = 2,
            winTile = win,
            yakuList = listOf("岭上开花", "立直"),
            han = 2,
            fu = 40,
            score = 2600
        )
        session.update(afterKan, listOf(winEvent))

        assertTrue(screen.canHumanTsumo())
        assertFalse(screen.canHumanKan())
        assertTrue(screen.touchDown(900, 160, 0, 0))
        session.flushUi()

        assertTrue(screen.isSettlementShown, "自摸和牌后结算面板必须显示")
        assertNotNull(screen.currentSettlementData, "结算面板必须包含详细数据")

        screen.dispose()
    }
}

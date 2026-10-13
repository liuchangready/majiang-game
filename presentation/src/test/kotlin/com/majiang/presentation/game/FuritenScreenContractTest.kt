package com.majiang.presentation.game

import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FuritenScreenContractTest : GdxHeadlessTestRunner() {

    @Test fun `real furiten session exposes tsumo button and settles exactly once for each cause`() {
        for (cause in 0..2) {
            val furitenStatus = when (cause) {
                0 -> FuritenStatusView(discard = true)
                1 -> FuritenStatusView(temporary = true)
                else -> FuritenStatusView(riichi = true)
            }
            val view = TestViewBuilder.playerView(
                viewerSeat = 2,
                currentTurnPlayer = 2,
                myFuriten = furitenStatus,
                legalActions = listOf(LegalActionView.DeclareTsumo(2))
            )
            val session = FakeGameSession(initialView = view)
            val nav = FakeTableNavigation()
            val screen = GameScreen(TestUiAssets.create(), session, nav)
            screen.startGame()

            assertNotNull(screen.currentFuritenText())
            assertTrue(screen.canHumanTsumo())
            assertFalse(screen.canHumanRon())
            assertTrue(screen.touchDown(900, 160, 0, 0))
            assertEquals(listOf(LegalActionView.DeclareTsumo(2)), session.submitted)

            screen.dispose()
        }
    }

    @Test fun `hover preview changes warning while current furiten and confirmed discard remain independent`() {
        val drawn = TestViewBuilder.tile(SuitView.SOU, 6)
        val riichiAction = LegalActionView.DeclareRiichi(2, drawn)
        val furitenStatus = FuritenStatusView(discard = true)
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            myDrawnTile = drawn,
            myFuriten = furitenStatus,
            legalActions = listOf(riichiAction)
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()

        assertNotNull(screen.currentFuritenText())
        assertTrue(screen.touchDown(720, 160, 0, 0))
        assertTrue(screen.touchDown(1000, 80, 0, 0))
        assertNotNull(screen.previewFuritenText())

        screen.mouseMoved(767, 80)
        assertNull(screen.previewFuritenText())
        assertNotNull(screen.currentFuritenText())

        assertTrue(screen.touchDown(720, 160, 0, 0))
        assertEquals(listOf(riichiAction), session.submitted)
        assertNull(screen.previewFuritenText())

        screen.dispose()
    }
}

package com.majiang.presentation.common.render

import com.badlogic.gdx.Gdx
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.game.GameScreen
import com.majiang.presentation.menu.MenuScreen
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestViewBuilder as T
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test

/** Skin switching, table input and borrowed asset lifetime through V4 client API. */
class GameScreenSkinSmokeTest : GdxHeadlessTestRunner() {
    @BeforeEach fun clearPreferences() {
        Gdx.app?.getPreferences("majiang")?.let { it.clear(); it.flush() }
    }

    private fun verifyStartAndDiscard(skinId: SkinId) {
        val manager = SkinManager(InMemorySkinPreferences(skinId))
        val assets = UiAssets.createDefault(manager)
        val session = FakeGameSession(initialView = T.playerView(
            viewerSeat = 0,
            phase = GamePhaseView.STACKING,
            myClosedTiles = emptyList(),
            myDrawnTile = null,
            legalActions = emptyList()
        ))
        val screen = GameScreen(assets, session, FakeTableNavigation())
        try {
            assertEquals(skinId, screen.tileSkin.id)
            assertTrue(screen.touchDown(640, 350, 0, 0), "Start button should consume click")
            assertTrue(session.started)

            val closed = (1..9).map { T.tile(SuitView.MAN, it) } +
                (1..4).map { T.tile(SuitView.WIND, it) }
            val drawn = T.tile(SuitView.DRAGON, 1)
            val dealt = T.playerView(
                viewerSeat = 0,
                currentTurnPlayer = 0,
                myClosedTiles = closed,
                myDrawnTile = drawn,
                handTileCounts = listOf(14, 13, 13, 13),
                legalActions = listOf(LegalActionView.DiscardTile(0, drawn))
            )
            session.update(dealt)
            assertEquals(14, screen.currentView.myAllClosedTiles.size)
            assertEquals(listOf(14, 13, 13, 13), screen.currentView.handTileCounts)
            assertEquals(0, screen.currentView.currentTurnPlayer)
            val drawnSlot = screen.refreshHandTileRects().single { it.tile == drawn }
            assertTrue(screen.touchDown((drawnSlot.rect.x + drawnSlot.rect.width / 2).toInt(),
                (drawnSlot.rect.y + drawnSlot.rect.height / 2).toInt(), 0, 0))
            assertEquals(listOf(LegalActionView.DiscardTile(0, drawn)), session.submitted)

            // Receipt alone does not invent state; the authoritative view moves the exact physical tile.
            assertEquals(14, screen.currentView.myAllClosedTiles.size)
            session.update(dealt.copy(myDrawnTile = null, handTileCounts = listOf(13, 13, 13, 13),
                discards = listOf(listOf(drawn), emptyList(), emptyList(), emptyList()), legalActions = emptyList()))
            assertEquals(13, screen.currentView.myAllClosedTiles.size)
            assertEquals(listOf(drawn), screen.currentView.discards[0])
            assertEquals(skinId, screen.tileSkin.id)
            assertDoesNotThrow { screen.dispose() }
            assertEquals(skinId, manager.currentSkin.id, "Screen must not dispose shared skin")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test fun `Wikipedia skin starts V4 table and submits physical discard`() = verifyStartAndDiscard(SkinId.WIKIPEDIA)
    @Test fun `Classic skin starts V4 table and submits physical discard`() = verifyStartAndDiscard(SkinId.CLASSIC)

    @Test fun `MenuScreen skin toggle and shared resource release are safe`() {
        val manager = SkinManager(InMemorySkinPreferences(SkinId.WIKIPEDIA))
        val assets = UiAssets.createDefault(manager)
        val menu = MenuScreen(assets, FakeTableNavigation())
        try {
            assertEquals(SkinId.WIKIPEDIA, manager.currentSkinId)
            assertTrue(menu.touchDown(550, 405, 0, 0))
            assertEquals(SkinId.CLASSIC, manager.currentSkinId)
            assertTrue(menu.touchDown(550, 405, 0, 0))
            assertEquals(SkinId.WIKIPEDIA, manager.currentSkinId)
            assertDoesNotThrow { menu.dispose() }
            assertEquals(SkinId.WIKIPEDIA, manager.currentSkin.id)
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }
}

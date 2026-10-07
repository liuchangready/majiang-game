package com.majiang.screen

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.LegalActionView
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.TileArranger
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

class RiichiChoiceScreenContractTest : GdxHeadlessTestRunner() {
    private val drawn = TestViewBuilder.tile(SuitView.SOU, 6, 0)
    private val alternate = TestViewBuilder.tile(SuitView.SOU, 2, 0)

    private fun fakeScene(
        actions: List<LegalActionView> = listOf(
            LegalActionView.DeclareRiichi(2, alternate),
            LegalActionView.DeclareRiichi(2, drawn),
            LegalActionView.DiscardTile(2, alternate),
            LegalActionView.DiscardTile(2, drawn)
        )
    ): Pair<GameScreen, FakeGameSession> {
        val closed = (1..6).map { TestViewBuilder.tile(SuitView.MAN, it) } +
            (7..9).map { TestViewBuilder.tile(SuitView.PIN, it) } +
            (2..5).map { TestViewBuilder.tile(SuitView.SOU, it) }
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
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

    private fun riichi(screen: GameScreen) = screen.touchDown(720, 160, 0, 0)

    private fun clickTile(screen: GameScreen, tile: TileView) {
        val v = screen.session.currentSnapshot!!.view
        val x = if (screen.isAutoSort) {
            220 + TileArranger.sortForDisplay(v.myClosedTiles, v.myDrawnTile).tiles.indexOf(tile) * 58
        } else if (v.myDrawnTile == tile) {
            220 + v.myClosedTiles.size * 58 + 24
        } else {
            220 + v.myClosedTiles.indexOf(tile) * 58
        }
        assertTrue(screen.touchDown(x + 25, 80, 0, 0), "Tile not handled: $tile")
    }

    @Test fun `enter and empty confirm never immediately declare riichi`() {
        val (screen, session) = fakeScene()
        assertTrue(riichi(screen))
        riichi(screen)
        assertTrue(session.submitted.isEmpty())
        assertEquals(listOf(25000, 25000, 25000, 25000), session.snapshot.view.scores)
        screen.dispose()
    }

    @Test fun `tile click only previews and confirm submits the last deliberate choice`() {
        val (screen, session) = fakeScene()
        riichi(screen)
        clickTile(screen, alternate)
        clickTile(screen, drawn)
        clickTile(screen, drawn)
        assertTrue(session.submitted.isEmpty())
        // Hovering another option must preview it without changing the chosen physical discard.
        screen.mouseMoved(767, 80)
        riichi(screen)
        assertEquals(listOf(LegalActionView.DeclareRiichi(2, drawn)), session.submitted)
        // An unchanged fake session still must not allow a second stale confirm.
        riichi(screen)
        assertEquals(1, session.submitted.size)
        screen.dispose()
    }

    @Test fun `invalid tile is swallowed and cannot replace chosen riichi discard`() {
        val (screen, session) = fakeScene()
        riichi(screen); clickTile(screen, alternate)
        clickTile(screen, TestViewBuilder.tile(SuitView.MAN, 1))
        assertTrue(session.submitted.isEmpty())
        riichi(screen)
        assertEquals(listOf(LegalActionView.DeclareRiichi(2, alternate)), session.submitted)
        screen.dispose()
    }

    @Test fun `cancel escape and android back do not submit and restore ordinary discard`() {
        for (cancel in 0..2) {
            val (screen, session) = fakeScene()
            riichi(screen); clickTile(screen, alternate)
            when (cancel) {
                0 -> assertTrue(screen.touchDown(1100, 160, 0, 0))
                1 -> assertTrue(screen.keyDown(Input.Keys.ESCAPE))
                else -> assertTrue(screen.keyDown(Input.Keys.BACK))
            }
            assertTrue(session.submitted.isEmpty())
            clickTile(screen, drawn)
            assertEquals(listOf(LegalActionView.DiscardTile(2, drawn)), session.submitted)
            screen.dispose()
        }
    }

    @Test fun `sort toggle preserves selected physical tile and uses fresh hit boxes`() {
        val (screen, session) = fakeScene()
        riichi(screen); clickTile(screen, drawn)
        assertTrue(screen.touchDown(1140, 75, 0, 0))
        assertTrue(screen.isAutoSort)
        // No render call between sorting and clicking: layout must already be correct.
        clickTile(screen, alternate)
        riichi(screen)
        assertEquals(listOf(LegalActionView.DeclareRiichi(2, alternate)), session.submitted)
        screen.dispose()
    }

    @Test fun `session update removes stale selection before confirm`() {
        val (screen, session) = fakeScene()
        riichi(screen); clickTile(screen, alternate)
        session.update(session.snapshot.view.copy(legalActions = listOf(LegalActionView.DiscardTile(2, drawn))))
        riichi(screen)
        assertTrue(session.submitted.isEmpty())
        clickTile(screen, drawn)
        assertEquals(listOf(LegalActionView.DiscardTile(2, drawn)), session.submitted)
        screen.dispose()
    }

    @Test fun `rejected confirmation exits selection and leaves ordinary actions usable`() {
        val (screen, session) = fakeScene()
        session.submitResult = CompletableFuture.completedFuture(
            Receipt("choose", ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
        )
        riichi(screen); clickTile(screen, alternate); riichi(screen)
        assertEquals(listOf(LegalActionView.DeclareRiichi(2, alternate)), session.submitted,
            "Rejected receipt still records the exact attempted option")
        session.flushUi()
        clickTile(screen, drawn)
        assertEquals(
            listOf(LegalActionView.DeclareRiichi(2, alternate), LegalActionView.DiscardTile(2, drawn)),
            session.submitted,
            "Rejected riichi must exit selection and leave ordinary discard usable"
        )
        screen.dispose()
    }

    @Test fun `choosing drawn copy among identical tiles preserves that physical discard`() {
        val east = TestViewBuilder.tile(SuitView.WIND, 1, 1)
        val closed = (1..6).map { TestViewBuilder.tile(SuitView.MAN, it) } +
            (7..9).map { TestViewBuilder.tile(SuitView.PIN, it) } +
            (1..3).map { TestViewBuilder.tile(SuitView.SOU, it) } +
            TestViewBuilder.tile(SuitView.WIND, 1, 0)
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            myClosedTiles = closed,
            myDrawnTile = east,
            legalActions = listOf(LegalActionView.DeclareRiichi(2, east))
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        riichi(screen); clickTile(screen, east); riichi(screen)
        assertEquals(listOf(LegalActionView.DeclareRiichi(2, east)), session.submitted)
        screen.dispose()
    }

    @Test fun `android back catch setting is restored when screen leaves`() {
        val original = Gdx.input
        try {
            for (previous in listOf(false, true)) {
                var caught = previous
                Gdx.input = object : Input by original {
                    override fun setCatchKey(keycode: Int, catchKey: Boolean) {
                        if (keycode == Input.Keys.BACK) caught = catchKey
                    }
                    override fun isCatchKey(keycode: Int): Boolean = if (keycode == Input.Keys.BACK) caught else original.isCatchKey(keycode)
                }
                val (unshown, _) = fakeScene()
                unshown.dispose()
                assertEquals(previous, caught)
                val (screen, _) = fakeScene()
                screen.show()
                assertTrue(caught)
                screen.hide()
                assertEquals(previous, caught)
                // A later screen can change BACK handling; disposing this hidden screen must not overwrite it.
                caught = !previous
                screen.dispose()
                assertEquals(!previous, caught)
                caught = previous
                val (nextScreen, _) = fakeScene()
                nextScreen.show()
                nextScreen.show() // duplicate show must not overwrite the saved pre-screen setting
                assertTrue(caught)
                nextScreen.dispose()
                assertEquals(previous, caught)
            }
        } finally {
            Gdx.input = original
        }
    }
}

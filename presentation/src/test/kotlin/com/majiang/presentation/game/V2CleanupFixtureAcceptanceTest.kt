package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

class V2CleanupFixtureAcceptanceTest : GdxHeadlessTestRunner() {

    @Test fun `riichi confirm reads latest client snapshot before its UI notification arrives`() {
        val drawn = TestViewBuilder.tile(SuitView.SOU, 6)
        val closed = (1..6).map { TestViewBuilder.tile(SuitView.MAN, it) } +
            (7..9).map { TestViewBuilder.tile(SuitView.PIN, it) } +
            (2..5).map { TestViewBuilder.tile(SuitView.SOU, it) }
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            myClosedTiles = closed,
            myDrawnTile = drawn,
            legalActions = listOf(LegalActionView.DeclareRiichi(2, drawn), LegalActionView.DiscardTile(2, drawn))
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            assertTrue(screen.touchDown(720, 160, 0, 0))
            val slot = screen.refreshHandTileRects().first { it.tile == drawn }.rect
            assertTrue(screen.touchDown((slot.x + slot.width / 2).toInt(), 80, 0, 0))
            assertNotNull(screen.riichiSelectionModel.confirmationOption())
            // Directly replace session snapshot with updated legal actions (riichi removed)
            val updated = session.snapshot.copy(
                view = session.snapshot.view.copy(legalActions = listOf(LegalActionView.DiscardTile(2, drawn)))
            )
            session.publish(updated)
            // Stale confirm attempt
            screen.touchDown(720, 160, 0, 0)
            session.flushUi()
            assertFalse(screen.riichiSelectionModel.isSelecting)
        } finally { screen.dispose() }
    }

    @Test fun `UI thread can publish a snapshot without waiting for itself`() {
        val session = FakeGameSession(initialView = TestViewBuilder.playerView())
        val done = CompletableFuture<Unit>()
        Gdx.app.postRunnable {
            try {
                assertTrue(isUiThread())
                session.update(TestViewBuilder.playerView())
                session.publish(session.snapshot)
                done.complete(Unit)
            } catch (t: Throwable) {
                done.completeExceptionally(t)
            }
        }
        done.get(3, TimeUnit.SECONDS)
    }
}

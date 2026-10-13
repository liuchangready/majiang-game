package com.majiang.presentation.architecture

import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.game.GameScreen
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

class ClientSelectionAcceptanceTest : GdxHeadlessTestRunner() {

    @Test fun `UI never replaces a selected physical tile with another copy of the same type`() {
        val t0 = TestViewBuilder.tile(SuitView.PIN, 5, 0)
        val t1 = TestViewBuilder.tile(SuitView.PIN, 5, 1)
        val action0 = LegalActionView.DiscardTile(0, t0)
        val action1 = LegalActionView.DiscardTile(0, t1)

        val view = TestViewBuilder.playerView(
            viewerSeat = 0,
            currentTurnPlayer = 0,
            phase = GamePhaseView.DISCARD,
            myClosedTiles = listOf(t0, t1),
            myDrawnTile = null,
            legalActions = listOf(action0, action1)
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()

        // Find the slot for t0
        val slot0 = screen.refreshHandTileRects().first { it.tile == t0 }.rect
        screen.touchDown((slot0.x + slot0.width / 2).toInt(), 80, 0, 0)

        // Must submit t0, not t1
        assertEquals(listOf(action0), session.submitted)
        screen.dispose()
    }

    @Test fun `choice transport exception does not trigger a second legacy submission`() {
        val tile = TestViewBuilder.tile(SuitView.PIN, 5, 0)
        val action = LegalActionView.DiscardTile(0, tile)
        val view = TestViewBuilder.playerView(
            viewerSeat = 0,
            currentTurnPlayer = 0,
            phase = GamePhaseView.DISCARD,
            myClosedTiles = listOf(tile),
            myDrawnTile = null,
            legalActions = listOf(action)
        )
        val session = FakeGameSession(initialView = view)
        val future = CompletableFuture<Receipt>()
        future.completeExceptionally(IllegalStateException("Simulated response loss after remote acceptance"))
        session.submitResult = future

        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()

        val slot = screen.refreshHandTileRects().first { it.tile == tile }.rect
        screen.touchDown((slot.x + slot.width / 2).toInt(), 80, 0, 0)

        assertEquals(1, session.submittedChoices.size)
        screen.dispose()
    }
}

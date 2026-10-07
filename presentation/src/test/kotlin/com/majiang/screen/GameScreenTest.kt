package com.majiang.screen

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.math.Vector3
import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.ServerConnectionSession
import com.majiang.client.api.ShuffleConfig
import com.majiang.client.api.TileView
import com.majiang.presentation.game.GameScreen as V4GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

class GameScreenTest : GdxHeadlessTestRunner() {
    private data class RealFixture(
        val game: MahjongGame,
        val screen: V4GameScreen,
        val session: ServerConnectionSession
    ) : AutoCloseable {
        override fun close() = game.dispose()
    }

    private fun flushUi() {
        val done = CompletableFuture<Unit>()
        Gdx.app.postRunnable { Gdx.app.postRunnable { done.complete(Unit) } }
        done.get(3, TimeUnit.SECONDS)
    }

    private fun realScreen(): RealFixture {
        val game = MahjongGame()
        game.create()
        try {
            val lobby = game.openLocalLobby().get(4, TimeUnit.SECONDS)
            lobby.createRoom(
                playerName = "screen-test",
                config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false))
            ).get(4, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, lobby.start().get(4, TimeUnit.SECONDS).status)
            game.showPlayerTable()
            flushUi()
            return RealFixture(game, game.screen as V4GameScreen, game.currentSession!!)
        } catch (t: Throwable) {
            game.dispose()
            throw t
        }
    }

    private fun awaitView(session: ServerConnectionSession, condition: (com.majiang.client.api.PlayerView) -> Boolean)
        : com.majiang.client.api.PlayerView {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(4)
        while (System.nanoTime() < deadline) {
            flushUi()
            val view = session.currentSnapshot?.view
            if (view != null && condition(view)) return view
            Thread.sleep(10)
        }
        throw AssertionError("Timed out waiting for the authoritative client view")
    }

    private fun clickWorld(screen: V4GameScreen, x: Float, y: Float): Boolean {
        if (Gdx.graphics == null || Gdx.graphics.width == 0) {
            return screen.touchDown(x.toInt(), y.toInt(), 0, 0)
        }
        val pixel = screen.camera.project(Vector3(x, y, 0f))
        return screen.touchDown(pixel.x.toInt(), (Gdx.graphics.height - pixel.y).toInt(), 0, 0)
    }

    @Test
    fun `start button submits and real local V4 room deals physical tiles`() {
        val waiting = TestViewBuilder.playerView(
            viewerSeat = 0,
            phase = GamePhaseView.STACKING,
            myClosedTiles = emptyList(),
            myDrawnTile = null,
            handTileCounts = listOf(0, 0, 0, 0)
        )
        val fake = FakeGameSession(initialView = waiting)
        val screen = V4GameScreen(TestUiAssets.create(), fake, FakeTableNavigation())
        try {
            assertTrue(clickWorld(screen, 640f, 350f))
            assertTrue(fake.started, "Start button must call GameSession.start")
        } finally {
            screen.dispose()
        }

        realScreen().use { (_, _, session) ->
            val view = awaitView(session) { it.myAllClosedTiles.size == 14 }
            assertEquals(14, view.myAllClosedTiles.size)
            assertEquals(listOf(14, 13, 13, 13), view.handTileCounts)
            assertEquals(53, view.handTileCounts.sum(), "Initial deal must account for exactly 53 physical tiles")
        }
    }

    @Test
    fun `physical hand tile click submits exact V4 option and advances real local table`() {
        realScreen().use { (_, screen, session) ->
            val view = awaitView(session) { it.myAllClosedTiles.size == 14 && it.currentTurnPlayer == 0 }
            val tile: TileView = view.myDrawnTile ?: view.myClosedTiles.first()
            val before = view.discards[0].size
            val slot = screen.refreshHandTileRects().first { it.tile.tileId == tile.tileId }.rect
            assertTrue(clickWorld(screen, slot.x + slot.width / 2f, 80f), "Physical tile click must be handled")

            val after = awaitView(session) { it.discards[0].size == before + 1 }
            assertEquals(13, after.myAllClosedTiles.size)
            assertEquals(before + 1, after.discards[0].size)
            assertEquals(tile.tileId, after.discards[0].last().tileId,
                "The selected physical tile, not merely its face, must enter the river")
        }
    }

    @Test
    fun `auto sort starts off and toggles on each click`() {
        val fake = FakeGameSession(initialView = TestViewBuilder.playerView())
        val screen = V4GameScreen(TestUiAssets.create(), fake, FakeTableNavigation())
        try {
            assertFalse(screen.isAutoSort)
            assertTrue(screen.btnSortRect.height >= 44f)
            val rect = screen.btnSortRect
            assertTrue(clickWorld(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f))
            assertTrue(screen.isAutoSort)
            assertTrue(clickWorld(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f))
            assertFalse(screen.isAutoSort)
        } finally {
            screen.dispose()
        }
    }

    @Test
    fun `auto sort hit area stays inside viewport and clear of hand`() {
        val screen = V4GameScreen(TestUiAssets.create(), FakeGameSession(), FakeTableNavigation())
        try {
            val rect = screen.btnSortRect
            assertTrue(rect.x >= 0f && rect.x + rect.width <= 1280f)
            assertTrue(rect.y >= 0f && rect.y + rect.height <= 720f)
            assertTrue(rect.x >= 1052f, "Sort button must not cover the physical hand tiles")
        } finally {
            screen.dispose()
        }
    }
}

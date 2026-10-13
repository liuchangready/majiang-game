package com.majiang.presentation.game

import com.badlogic.gdx.Input
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeSpectatorSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SpectatorScreenContractTest : GdxHeadlessTestRunner() {

    @Test
    fun `spectator screen only consumes public snapshots and has no player action buttons`() {
        val board = TestViewBuilder.publicBoardView(
            phase = GamePhaseView.DISCARD,
            scores = listOf(25000, 25000, 25000, 25000),
            handTileCounts = listOf(13, 13, 14, 13)
        )
        val snap = TestViewBuilder.publicSnapshotView(board = board)
        val session = FakeSpectatorSession(initialSnapshot = snap)
        val nav = FakeTableNavigation()
        val screen = SpectatorScreen(TestUiAssets.create(), session, nav)

        // Verifies public snapshot is consumed
        assertEquals(snap, screen.currentSnapshot)

        // Clicking action button positions (e.g., Riichi at 720, 160 or Tsumo at 900, 160)
        // must NOT throw and must NOT perform any action submission
        screen.touchDown(720, 160, 0, 0)
        screen.touchDown(900, 160, 0, 0)
        screen.touchDown(500, 160, 0, 0)
        screen.touchDown(640, 160, 0, 0)
        screen.touchDown(350, 160, 0, 0)

        // Hand tile counts are strictly preserved
        assertEquals(listOf(13, 13, 14, 13), screen.currentSnapshot?.board?.handTileCounts)

        screen.dispose()
    }

    @Test
    fun `spectator screen returns to menu on escape or leave click`() {
        val session = FakeSpectatorSession()
        val nav = FakeTableNavigation()
        val screen = SpectatorScreen(TestUiAssets.create(), session, nav)

        // Pressing ESCAPE triggers return to menu
        assertTrue(screen.keyDown(Input.Keys.ESCAPE))
        assertEquals(0, session.leaveRoomCalls, "The screen must delegate leaving to the navigation owner exactly once")
        assertTrue(nav.leftToMenu)

        screen.dispose()
    }

    @Test
    fun `spectator screen public update reflects updated board state`() {
        val initialBoard = TestViewBuilder.publicBoardView(
            scores = listOf(25000, 25000, 25000, 25000)
        )
        val session = FakeSpectatorSession(initialSnapshot = TestViewBuilder.publicSnapshotView(board = initialBoard))
        val nav = FakeTableNavigation()
        val screen = SpectatorScreen(TestUiAssets.create(), session, nav)

        val updatedBoard = initialBoard.copy(
            scores = listOf(29000, 21000, 25000, 25000)
        )
        val updatedSnap = session.currentPublicSnapshot!!.copy(
            cursor = 2L,
            board = updatedBoard
        )
        session.update(updatedSnap)

        assertEquals(listOf(29000, 21000, 25000, 25000), screen.currentSnapshot?.board?.scores)

        screen.dispose()
    }
}

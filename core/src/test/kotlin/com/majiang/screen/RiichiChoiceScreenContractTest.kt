package com.majiang.screen

import com.badlogic.gdx.Input
import com.badlogic.gdx.Gdx
import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.render.TileArranger
import com.majiang.session.*
import com.majiang.state.*
import com.majiang.testing.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiChoiceScreenContractTest : GdxHeadlessTestRunner() {
    private val drawn = Tile(Suit.SOU, 6)
    private val alternate = Tile(Suit.SOU, 2)
    private fun state(seat: Int = 2): GameState = TestTableBuilder.create(hands = mapOf(seat to (
        (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } + (2..5).map { Tile(Suit.SOU, it) })), drawn = seat to drawn)
    private fun fakeScene(): Pair<GameScreen, FakeGameSession> {
        val s = state()
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        val session = FakeGameSession(PlayerView.fromGameState(s, 2, e.legalActions(2)))
        val screen = GameScreen(MahjongGame(), session)
        screen.startGame()
        return screen to session
    }
    private fun riichi(screen: GameScreen) = screen.touchDown(720, 160, 0, 0)
    private fun clickTile(screen: GameScreen, tile: Tile) {
        val v = screen.session.view()
        val x = if (screen.isAutoSort) {
            220 + TileArranger.sortForDisplay(v.myClosedTiles, v.myDrawnTile).tiles.indexOf(tile) * 58
        } else if (v.myDrawnTile == tile) 220 + v.myClosedTiles.size * 58 + 24
        else 220 + v.myClosedTiles.indexOf(tile) * 58
        assertTrue(screen.touchDown(x + 25, 80, 0, 0), "Tile not handled: $tile")
    }

    @Test fun `enter and empty confirm never immediately declare riichi`() {
        val (screen, session) = fakeScene()
        assertTrue(riichi(screen))
        riichi(screen)
        assertTrue(session.submitted.isEmpty())
        assertEquals(listOf(25000, 25000, 25000, 25000), session.view().scores)
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
        assertEquals(listOf(GameAction.DeclareRiichi(2, drawn)), session.submitted)
        // An unchanged fake session still must not allow a second stale confirm.
        riichi(screen)
        assertEquals(1, session.submitted.size)
    }

    @Test fun `invalid tile is swallowed and cannot replace chosen riichi discard`() {
        val (screen, session) = fakeScene()
        riichi(screen); clickTile(screen, alternate)
        clickTile(screen, Tile(Suit.MAN, 1))
        assertTrue(session.submitted.isEmpty())
        riichi(screen)
        assertEquals(listOf(GameAction.DeclareRiichi(2, alternate)), session.submitted)
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
            assertEquals(listOf(GameAction.DiscardTile(2, drawn)), session.submitted)
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
        assertEquals(listOf(GameAction.DeclareRiichi(2, alternate)), session.submitted)
    }

    @Test fun `session update removes stale selection before confirm`() {
        val (screen, session) = fakeScene()
        riichi(screen); clickTile(screen, alternate)
        session.update(session.view().copy(legalActions = listOf(GameAction.DiscardTile(2, drawn))))
        riichi(screen)
        assertTrue(session.submitted.isEmpty())
        clickTile(screen, drawn)
        assertEquals(listOf(GameAction.DiscardTile(2, drawn)), session.submitted)
    }

    @Test fun `rejected confirmation exits selection and leaves ordinary actions usable`() {
        val s = state()
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        val base = FakeGameSession(PlayerView.fromGameState(s, 2, e.legalActions(2)))
        var attempts = 0
        val rejecting = object : GameSession by base {
            override fun submit(action: GameAction): ActionResult {
                if (action is GameAction.DeclareRiichi) {
                    attempts++
                    return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                }
                return base.submit(action)
            }
        }
        val screen = GameScreen(MahjongGame(), rejecting)
        screen.startGame()
        riichi(screen); clickTile(screen, alternate); riichi(screen)
        assertEquals(1, attempts)
        assertTrue(base.submitted.isEmpty())
        clickTile(screen, drawn)
        assertEquals(listOf(GameAction.DiscardTile(2, drawn)), base.submitted)
    }

    @Test fun `real session changes neither score nor tiles until confirmation then commits exact choice`() {
        val s = state()
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        e.initializeMatch(2)
        val session = LocalGameSession(2, e)
        val screen = GameScreen(MahjongGame(), session)
        screen.startGame()
        riichi(screen); clickTile(screen, alternate)
        assertEquals(25000, s.scores[2])
        assertFalse(s.riichiDeclared[2])
        assertEquals(14, s.players[2].tileCount)
        assertTrue(s.players[2].discards.isEmpty())
        assertEquals(drawn, s.players[2].drawnTile)
        riichi(screen)
        assertEquals(24000, s.scores[2])
        assertEquals(1, s.riichiSticks)
        assertTrue(s.riichiDeclared[2])
        assertEquals(alternate, s.players[2].discardHistory.single())
        assertEquals(100000, s.scores.sum() + s.riichiSticks * 1000)
        TestTableBuilder.assertPhysical(s)
        screen.dispose()
    }

    @Test fun `choosing drawn copy among identical tiles preserves that physical discard`() {
        val east = Tile(Suit.WIND, 1, 1)
        val closed = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } +
            (1..3).map { Tile(Suit.SOU, it) } + Tile(Suit.WIND, 1)
        val s = TestTableBuilder.create(hands = mapOf(2 to closed), drawn = 2 to east)
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        val session = FakeGameSession(PlayerView.fromGameState(s, 2, e.legalActions(2)))
        val screen = GameScreen(MahjongGame(), session)
        screen.startGame()
        riichi(screen); clickTile(screen, east); riichi(screen)
        assertEquals(listOf(GameAction.DeclareRiichi(2, east)), session.submitted)
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

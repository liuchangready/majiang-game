package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.session.LocalGameSession
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Actual screen -> LocalGameSession -> rules engine, without a fake action provider. */
class RiichiScreenIntegrationTest : GdxHeadlessTestRunner() {
    private fun waiting(): List<Tile> = (0..2).map { Tile(Suit.DRAGON, 1, it) } +
        (2..4).map { Tile(Suit.MAN, it) } + (6..8).map { Tile(Suit.PIN, it) } +
        listOf(Tile(Suit.SOU, 2), Tile(Suit.SOU, 2, 1), Tile(Suit.SOU, 5), Tile(Suit.SOU, 6))

    @Test fun `real kan button preserves the next tsumo choice and real tsumo button settles`() {
        val fourth = Tile(Suit.DRAGON, 1, 3)
        val win = Tile(Suit.SOU, 4)
        val state = TestTableBuilder.create(hands = mapOf(2 to waiting()), wallPrefix = listOf(fourth),
            deadWallPrefix = listOf(win), phase = GamePhase.DRAW, currentPlayer = 2)
        state.riichiDeclared[2] = true
        state.players[2].riichiDeclared = true
        state.scores[2] -= 1000
        state.riichiSticks = 1
        val engine = GameStateMachine(state, GameEventBus(), autoPlayAi = false)
        engine.initializeMatch(2)
        val session = LocalGameSession(viewerSeat = 2, engine = engine)
        val screen = GameScreen(MahjongGame(), session)
        screen.startGame()
        assertTrue(session.submit(GameAction.DrawTile(2)) is ActionResult.Accepted)
        assertTrue(screen.canHumanKan())
        assertFalse(screen.canHumanTsumo())
        assertTrue(screen.touchDown(350, 160, 0, 0))
        assertTrue(screen.meldSelectionModel.isSelecting)
        assertEquals(fourth, session.view().myDrawnTile)
        assertTrue(screen.touchDown(720, 160, 0, 0))
        assertEquals(win, session.view().myDrawnTile)
        assertTrue(screen.canHumanTsumo())
        assertFalse(screen.canHumanKan())
        assertTrue(screen.touchDown(900, 160, 0, 0))
        assertEquals(GamePhase.ROUND_END, session.view().phase)
        TestTableBuilder.assertPhysical(state)
        screen.dispose()
    }
}

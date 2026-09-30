package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.session.*
import com.majiang.state.*
import com.majiang.testing.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FuritenScreenContractTest : GdxHeadlessTestRunner() {
    @Test fun `real furiten session exposes tsumo button and settles exactly once for each cause`() {
        val waiting = (0..2).map { Tile(Suit.DRAGON, 1, it) } + (2..4).map { Tile(Suit.MAN, it) } +
            (6..8).map { Tile(Suit.PIN, it) } + listOf(Tile(Suit.SOU, 2), Tile(Suit.SOU, 2, 1), Tile(Suit.SOU, 5), Tile(Suit.SOU, 6))
        for (cause in 0..2) {
            val s = TestTableBuilder.create(hands = mapOf(2 to waiting), drawn = 2 to Tile(Suit.SOU, 7))
            when (cause) {
                0 -> s.players[2].discardHistory.add(Tile(Suit.SOU, 4))
                1 -> s.sameTurnFuriten[2] = true
                2 -> {
                    s.riichiDeclared[2] = true; s.players[2].riichiDeclared = true
                    s.riichiFuriten[2] = true; s.scores[2] -= 1000; s.riichiSticks = 1
                }
            }
            val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
            e.initializeMatch(2)
            val session = LocalGameSession(2, e)
            val screen = GameScreen(MahjongGame(), session)
            screen.startGame()
            assertNotNull(screen.currentFuritenText())
            assertTrue(screen.canHumanTsumo())
            assertFalse(screen.canHumanRon())
            assertTrue(screen.touchDown(900, 160, 0, 0))
            assertEquals(GamePhase.ROUND_END, s.phase)
            assertNull(screen.currentFuritenText())
            val scores = s.scores.toList()
            assertTrue(session.submit(GameAction.DeclareTsumo(2)) is ActionResult.Rejected)
            assertEquals(scores, s.scores.toList())
            TestTableBuilder.assertPhysical(s)
            screen.dispose()
        }
    }

    @Test fun `hover preview changes warning while current furiten and confirmed discard remain independent`() {
        val drawn = Tile(Suit.SOU, 6)
        val s = TestTableBuilder.create(hands = mapOf(2 to ((1..6).map { Tile(Suit.MAN, it) } +
            (7..9).map { Tile(Suit.PIN, it) } + (2..5).map { Tile(Suit.SOU, it) })), drawn = 2 to drawn)
        s.players[2].discardHistory.add(Tile(Suit.SOU, 5, 1))
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        val session = FakeGameSession(PlayerView.fromGameState(s, 2, e.legalActions(2)))
        val screen = GameScreen(MahjongGame(), session)
        screen.startGame()
        assertNotNull(screen.currentFuritenText())
        assertTrue(screen.touchDown(720, 160, 0, 0))
        assertTrue(screen.touchDown(1000, 80, 0, 0))
        assertNotNull(screen.previewFuritenText())
        screen.mouseMoved(767, 80)
        assertNull(screen.previewFuritenText())
        assertNotNull(screen.currentFuritenText())
        assertTrue(screen.touchDown(720, 160, 0, 0))
        assertEquals(listOf(GameAction.DeclareRiichi(2, drawn)), session.submitted)
        assertNull(screen.previewFuritenText())
        screen.dispose()
    }
}

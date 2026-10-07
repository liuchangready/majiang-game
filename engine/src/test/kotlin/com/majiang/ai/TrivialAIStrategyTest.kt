package com.majiang.ai

import com.majiang.model.*
import com.majiang.session.PlayerView
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TrivialAIStrategyTest {
    private val strategy = TrivialAIStrategy()

    @Test
    fun `摸切测试 - drawnTile存在时必打drawnTile`() {
        val state = GameState()
        for (rank in 1..9) state.players[1].closedTiles.add(Tile(Suit.MAN, rank))
        val drawn = Tile(Suit.PIN, 5)
        state.players[1].drawnTile = drawn
        val legal = state.players[1].allClosedTiles.map { GameAction.DiscardTile(1, it) }
        assertEquals(GameAction.DiscardTile(1, drawn), strategy.decideAction(PlayerView.fromGameState(state, 1, legal), legal))
    }

    @Test
    fun `摸切健壮性 - drawnTile为空时安全返回手牌任意一张`() {
        val state = GameState()
        state.players[1].closedTiles.addAll(listOf(Tile(Suit.MAN, 1), Tile(Suit.MAN, 2)))
        val legal = state.players[1].closedTiles.map { GameAction.DiscardTile(1, it) }
        val chosen = strategy.decideAction(PlayerView.fromGameState(state, 1, legal), legal)
        assertTrue(chosen in legal)
    }

    @Test
    fun `永不副露与立直`() {
        val state = GameState()
        val tile = Tile(Suit.MAN, 1)
        state.players[1].closedTiles.add(tile)
        val chi = Meld(MeldType.CHI, listOf(tile, Tile(Suit.MAN, 2), Tile(Suit.MAN, 3)), 0, tile)
        val responses = listOf(GameAction.DeclareChi(1, chi), GameAction.DeclarePon(1, listOf(tile)),
            GameAction.DeclareKan(1, listOf(tile), MeldType.OPEN_KAN), GameAction.Pass(1))
        assertEquals(GameAction.Pass(1), strategy.decideAction(PlayerView.fromGameState(state, 1, responses), responses))
        val turn = listOf(GameAction.DeclareRiichi(1, tile), GameAction.DeclareAbortiveDraw(1), GameAction.DiscardTile(1, tile))
        assertEquals(GameAction.DiscardTile(1, tile), strategy.decideAction(PlayerView.fromGameState(state, 1, turn), turn))
    }

    @Test
    fun `能和就和 - 保证对局能结束`() {
        val legal = listOf(GameAction.Pass(1), GameAction.DeclareRon(1))
        assertEquals(GameAction.DeclareRon(1), strategy.decideAction(PlayerView.fromGameState(GameState(), 1, legal), legal))
    }
}
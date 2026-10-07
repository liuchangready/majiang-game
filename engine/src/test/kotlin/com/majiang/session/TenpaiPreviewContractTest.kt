package com.majiang.session

import com.majiang.model.*
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TenpaiPreviewContractTest {
    private fun waiting() = (1..6).map { Tile(Suit.MAN, it) } +
        (7..9).map { Tile(Suit.PIN, it) } + (2..5).map { Tile(Suit.SOU, it) }
    private fun view(tiles: List<Tile> = waiting(), melds: List<Meld> = emptyList()): PlayerView {
        val s = GameState().also { it.phase = GamePhase.RESPONSE_WINDOW; it.currentPlayer = 1 }
        s.players[2].closedTiles.addAll(tiles)
        s.players[2].melds.addAll(melds)
        return PlayerView.fromGameState(s, 2, emptyList())
    }
    private fun waits(v: PlayerView) = TenpaiPreviewProvider.waits(v).map { it.typeIndex }

    @Test fun `silent tenpai is visible off turn without legal riichi and is immutable`() {
        val v = view()
        val result = TenpaiPreviewProvider.waits(v)
        assertEquals(listOf(10, 13), result.map { it.typeIndex })
        assertTrue(v.legalActions.isEmpty())
        assertFalse(v.isMyTurn)
        assertThrows(UnsupportedOperationException::class.java) { (result as MutableList).clear() }
        assertEquals(waiting(), v.myClosedTiles)
        assertEquals(waits(v), waits(v.copy(riichiStatus = listOf(false, false, true, false))))
    }
    @Test fun `draw preserves previous waits and discard changes waits immediately`() {
        val old = view().copy(phase = GamePhase.DISCARD, currentTurnPlayer = 2, myDrawnTile = Tile(Suit.SOU, 6))
        assertEquals(listOf(10, 13), waits(old))
        val after = old.copy(myDrawnTile = null,
            myClosedTiles = old.myAllClosedTiles.filterNot { it == Tile(Suit.SOU, 2) })
        assertEquals(listOf(11, 14), waits(after))
        assertTrue(waits(old.copy(myClosedTiles = old.myAllClosedTiles, myDrawnTile = null)).isEmpty())
    }
    @Test fun `open hand waits and kan ownership filter use shared rules`() {
        val chiTiles = (1..3).map { Tile(Suit.MAN, it) }
        val chi = Meld(MeldType.CHI, chiTiles, 1, chiTiles[0])
        assertEquals(listOf(10, 13), waits(view(waiting().drop(3), listOf(chi))))
        val kanTiles = (0..3).map { Tile(Suit.MAN, 1, it) }
        val kan = Meld(MeldType.CLOSED_KAN, kanTiles, -1, kanTiles[0])
        val closed = listOf(Tile(Suit.MAN, 2), Tile(Suit.MAN, 3)) +
            (6..8).map { Tile(Suit.PIN, it) } + (6..8).map { Tile(Suit.SOU, it) } +
            listOf(Tile(Suit.WIND, 1), Tile(Suit.WIND, 1, 1))
        assertEquals(listOf(Tile(Suit.MAN, 4).typeIndex), waits(view(closed, listOf(kan))))
    }
    @Test fun `seven pairs and all thirteen orphan waits are complete`() {
        val pairs = listOf(Tile(Suit.MAN, 1), Tile(Suit.MAN, 4), Tile(Suit.SOU, 2), Tile(Suit.SOU, 7),
            Tile(Suit.PIN, 3), Tile(Suit.WIND, 2))
        val seven = pairs.flatMap { listOf(it, it.copy(copyIndex = 1)) } + Tile(Suit.DRAGON, 1)
        assertEquals(listOf(Tile(Suit.DRAGON, 1).typeIndex), waits(view(seven)))
        val orphans = listOf(Suit.MAN, Suit.SOU, Suit.PIN).flatMap { listOf(Tile(it, 1), Tile(it, 9)) } +
            (1..4).map { Tile(Suit.WIND, it) } + (1..3).map { Tile(Suit.DRAGON, it) }
        assertEquals(orphans.map { it.typeIndex }.sorted(), waits(view(orphans)))
    }
    @Test fun `terminal preparation and malformed hand never retain wait hints`() {
        for (phase in listOf(GamePhase.STACKING, GamePhase.DEALING, GamePhase.ROUND_END, GamePhase.MATCH_END)) {
            assertTrue(waits(view().copy(phase = phase)).isEmpty(), "$phase")
        }
        assertTrue(waits(view(waiting().drop(1))).isEmpty())
        val v = view()
        val publicDiscards = List(4) { (0..3).map { Tile(Suit.SOU, 2, it) } }
        assertEquals(waits(v), waits(v.copy(discards = publicDiscards, wallRemaining = 0)))
    }
}

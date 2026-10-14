package com.majiang.model

import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TileMovementContractTest {
    private fun verify(s: GameState) {
        TestTableBuilder.assertPhysical(s)
        StateInvariants.assertValid(s)
    }

    @Test
    fun `normal draw and discard preserve exact identities and discard history`() {
        val s = TestTableBuilder.create(phase = GamePhase.DRAW)
        val top = s.wall[s.wallPointer]
        assertEquals(top, TileMovement.draw(s, 0))
        s.phase = GamePhase.DISCARD
        verify(s)
        TileMovement.discard(s, 0, top)
        s.phase = GamePhase.RESPONSE_WINDOW
        assertEquals(listOf(top), s.players[0].discardHistory)
        verify(s)
    }

    @Test
    fun `pon transfers all three tiles and retains original discard history`() {
        val copies = (0..3).map { Tile(Suit.PIN, 5, it) }
        val s = TestTableBuilder.create(hands = mapOf(0 to copies.take(2)),
            discards = mapOf(2 to listOf(copies[3])), phase = GamePhase.RESPONSE_WINDOW, currentPlayer = 2)
        TileMovement.pon(s, 0, copies[3], 2, copies.take(2))
        s.phase = GamePhase.DISCARD
        s.currentPlayer = 0
        assertTrue(s.players[2].discards.isEmpty())
        assertEquals(listOf(copies[3]), s.players[2].discardHistory)
        verify(s)
    }

    @Test
    fun `chi transfers both hand tiles and called tile`() {
        val handTiles = listOf(Tile(Suit.MAN, 2, 2), Tile(Suit.MAN, 3, 2))
        val called = Tile(Suit.MAN, 4, 2)
        val s = TestTableBuilder.create(hands = mapOf(0 to handTiles),
            discards = mapOf(3 to listOf(called)), phase = GamePhase.RESPONSE_WINDOW, currentPlayer = 3)
        TileMovement.chi(s, 0, called, 3, handTiles)
        s.phase = GamePhase.DISCARD
        s.currentPlayer = 0
        verify(s)
    }

    @Test
    fun `closed kan preserves an unrelated drawn tile and replenishes dead wall`() {
        val four = (0..3).map { Tile(Suit.SOU, 8, it) }
        val unrelatedDraw = Tile(Suit.MAN, 9, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to four), drawn = 0 to unrelatedDraw)
        val beforeLive = s.wallRemaining
        val originalIndicators = s.deadWall.drop(4).toList()
        TileMovement.closedKan(s, 0, four)
        s.phase = GamePhase.KAN_DRAW
        assertTrue(unrelatedDraw in s.players[0].closedTiles)
        assertNull(s.players[0].drawnTile)
        verify(s)
        TileMovement.drawRinshan(s, 0)
        s.phase = GamePhase.DISCARD
        assertEquals(beforeLive - 1, s.wallRemaining)
        assertEquals(14, s.deadWall.size - s.rinshanPointer + s.deadWallReplacements.size)
        assertEquals(originalIndicators, s.deadWall.drop(4))
        verify(s)
    }

    @Test
    fun `added kan consumes the just drawn fourth copy exactly once`() {
        val copies = (0..3).map { Tile(Suit.PIN, 6, it) }
        val pon = Meld(MeldType.PON, copies.take(3), 3, copies[0])
        val s = TestTableBuilder.create(melds = mapOf(0 to listOf(pon)), drawn = 0 to copies[3])
        TileMovement.addedKan(s, 0, copies[3])
        s.phase = GamePhase.KAN_DRAW
        assertNull(s.players[0].drawnTile)
        assertEquals(MeldType.ADDED_KAN, s.players[0].melds.single().type)
        verify(s)
        TileMovement.drawRinshan(s, 0)
        s.phase = GamePhase.DISCARD
        verify(s)
    }

    @Test
    fun `open kan removes discard and transfers all four tiles`() {
        val copies = (0..3).map { Tile(Suit.SOU, 7, it) }
        val s = TestTableBuilder.create(hands = mapOf(0 to copies.take(3)),
            discards = mapOf(2 to listOf(copies[3])), phase = GamePhase.RESPONSE_WINDOW, currentPlayer = 2)
        TileMovement.openKan(s, 0, copies[3], 2, copies.take(3))
        s.currentPlayer = 0
        s.phase = GamePhase.KAN_DRAW
        verify(s)
        TileMovement.drawRinshan(s, 0)
        s.phase = GamePhase.DISCARD
        verify(s)
    }

    @Test
    fun `wall swap preserves ownership and exact physical set`() {
        val hand = Tile(Suit.MAN, 9, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to listOf(hand)), phase = GamePhase.DRAW)
        val wallTile = s.wall[s.wallPointer]
        TileMovement.swapHandWithWall(s, 0, hand)
        assertTrue(wallTile in s.players[0].closedTiles)
        assertEquals(hand, s.wall[s.wallPointer])
        verify(s)
    }

    @Test
    fun `duplicate physical copies in a pon fail before mutation`() {
        val a = Tile(Suit.MAN, 5, 0)
        val discard = Tile(Suit.MAN, 5, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to listOf(a)), discards = mapOf(2 to listOf(discard)),
            phase = GamePhase.RESPONSE_WINDOW, currentPlayer = 2)
        val before = TestTableBuilder.actualTiles(s)
        assertThrows(IllegalArgumentException::class.java) { TileMovement.pon(s, 0, discard, 2, listOf(a, a)) }
        assertEquals(before, TestTableBuilder.actualTiles(s))
        verify(s)
    }

    @Test
    fun `fifth rinshan draw cannot consume a dora indicator`() {
        val s = TestTableBuilder.create(phase = GamePhase.DRAW)
        repeat(4) {
            val tile = TileMovement.drawRinshan(s, 0)
            TileMovement.discard(s, 0, tile)
        }
        val before = TestTableBuilder.actualTiles(s)
        assertThrows(IllegalArgumentException::class.java) { TileMovement.drawRinshan(s, 0) }
        assertEquals(before, TestTableBuilder.actualTiles(s))
        verify(s)
    }

    @Test
    fun `initial state audit does not overlook a hidden replacement tile`() {
        val state = GameState()
        state.deadWallReplacements.add(Tile(Suit.MAN, 1))
        assertThrows(RuntimeException::class.java) { StateInvariants.assertValid(state) }
    }
}

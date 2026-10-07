package com.majiang.rules

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Independently specified shapes: no production validator is used as the oracle. */
class RiichiKanValidatorContractTest {
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c -> val r = c.digitToInt(); Tile(suit, r, copies.getOrDefault(r, 0).also { copies[r] = it + 1 }) }
    }
    private fun hand(closed: List<Tile>, draw: Tile) = PlayerHand().apply {
        closedTiles.addAll(closed); drawnTile = draw; riichiDeclared = true
    }
    private fun isolated() = hand(tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") +
        tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256"), Tile(Suit.DRAGON, 1, 3))
    private fun quad(suit: Suit, rank: Int) = (0..3).map { Tile(suit, rank, it) }

    @Test fun `isolated honor triplet permits the freshly drawn fourth copy without mutation`() {
        val h = isolated()
        val before = h.allClosedTiles.toList()
        assertTrue(RiichiKanValidator.canDeclare(h, quad(Suit.DRAGON, 1)))
        assertEquals(before, h.allClosedTiles)
        assertTrue(h.melds.isEmpty())
    }

    @Test fun `isolated number triplet also permits a kan`() {
        val h = hand(tiles(Suit.MAN, "111") + tiles(Suit.PIN, "234678") +
            tiles(Suit.SOU, "2256"), Tile(Suit.MAN, 1, 3))
        assertTrue(RiichiKanValidator.canDeclare(h, quad(Suit.MAN, 1)))
    }

    @Test fun `same waits do not permit removing alternative sequence interpretations`() {
        val before = tiles(Suit.MAN, "666777888") + tiles(Suit.SOU, "56") + tiles(Suit.PIN, "11")
        val after = before.filterNot { it.suit == Suit.MAN && it.rank == 6 }
        assertEquals(setOf(12, 15), HandDecomposer.INSTANCE.calculateWaits(before))
        assertEquals(setOf(12, 15), HandDecomposer.INSTANCE.calculateWaits(after))
        assertFalse(RiichiKanValidator.canDeclare(hand(before, Tile(Suit.MAN, 6, 3)), quad(Suit.MAN, 6)))
    }

    @Test fun `kan that removes a waiting tile or pair interpretation is rejected`() {
        val before = tiles(Suit.MAN, "11123") + tiles(Suit.DRAGON, "111222") + tiles(Suit.WIND, "11")
        assertTrue(27 in HandDecomposer.INSTANCE.calculateWaits(before))
        assertFalse(RiichiKanValidator.canDeclare(hand(before, Tile(Suit.MAN, 1, 3)), quad(Suit.MAN, 1)))
    }

    @Test fun `quad already held before drawing an unrelated tile is not allowed`() {
        val h = hand(tiles(Suit.MAN, "1111234567899"), Tile(Suit.PIN, 9))
        assertFalse(RiichiKanValidator.canDeclare(h, quad(Suit.MAN, 1)))
    }

    @Test fun `existing closed kan allows another independent closed kan`() {
        val h = isolated()
        h.closedTiles.removeAll { it.suit == Suit.MAN }
        val prior = quad(Suit.WIND, 2)
        h.melds.add(Meld(MeldType.CLOSED_KAN, prior, -1, prior.last()))
        assertTrue(RiichiKanValidator.canDeclare(h, quad(Suit.DRAGON, 1)))
    }

    @Test fun `fifth copy of an existing kan cannot create a fictitious wait`() {
        val h = hand(tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "123456") + tiles(Suit.WIND, "2"),
            Tile(Suit.DRAGON, 1, 3))
        val prior = quad(Suit.WIND, 2)
        // Intentionally impossible ownership: a mathematical fifth-copy completion must not authorize a kan.
        h.melds.add(Meld(MeldType.CLOSED_KAN, prior, -1, prior.last()))
        assertFalse(RiichiKanValidator.canDeclare(h, quad(Suit.DRAGON, 1)))
    }

    @Test fun `duplicate foreign incomplete and mixed tile lists are rejected`() {
        val h = isolated()
        val q = quad(Suit.DRAGON, 1)
        for (bad in listOf(emptyList(), q.take(3), listOf(q[0], q[0], q[2], q[3]),
            quad(Suit.DRAGON, 2), q.take(3) + Tile(Suit.WIND, 1))) {
            assertFalse(RiichiKanValidator.canDeclare(h, bad), bad.toString())
        }
    }

    @Test fun `open hand missing draw and non tenpai are rejected`() {
        val h = isolated()
        h.drawnTile = null
        assertFalse(RiichiKanValidator.canDeclare(h, quad(Suit.DRAGON, 1)))
        h.drawnTile = Tile(Suit.DRAGON, 1, 3)
        val pon = tiles(Suit.WIND, "222")
        h.melds.add(Meld(MeldType.PON, pon, 1, pon.last()))
        assertFalse(RiichiKanValidator.canDeclare(h, quad(Suit.DRAGON, 1)))
        val nonTenpai = hand(tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "124") +
            tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256"), Tile(Suit.DRAGON, 1, 3))
        assertFalse(RiichiKanValidator.canDeclare(nonTenpai, quad(Suit.DRAGON, 1)))
    }
}

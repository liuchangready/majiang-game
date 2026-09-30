package com.majiang.rules

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FuritenRulesContractTest {
    @Test fun `incomplete fragments without enough melds cannot produce waits or completion`() {
        val h = PlayerHand().apply { closedTiles.add(Tile(Suit.WIND, 1)) }
        assertTrue(FuritenRules.waits(h).isEmpty())
        assertFalse(FuritenRules.completesHand(h, Tile(Suit.WIND, 1, 1)))
        val complete = hand()
        assertTrue(FuritenRules.waits(complete, complete.closedTiles.take(10)).isEmpty())
    }

    private fun hand() = PlayerHand().apply {
        closedTiles.addAll((0..2).map { Tile(Suit.DRAGON, 1, it) } + (2..4).map { Tile(Suit.MAN, it) } +
            (6..8).map { Tile(Suit.PIN, it) } + listOf(Tile(Suit.SOU, 2), Tile(Suit.SOU, 2, 1), Tile(Suit.SOU, 5), Tile(Suit.SOU, 6)))
    }

    @Test fun `discard furiten covers full wait set including the unthrown side`() {
        val h = hand()
        h.discardHistory.add(Tile(Suit.SOU, 4))
        assertEquals(setOf(12, 15), FuritenRules.waits(h))
        assertTrue(FuritenRules.status(h).discard)
        assertTrue(FuritenRules.status(h).isFuriten)
        assertTrue(FuritenRules.completesHand(h, Tile(Suit.SOU, 7)))
    }

    @Test fun `changing waits clears discard furiten without erasing history`() {
        val h = hand()
        h.discardHistory.add(Tile(Suit.SOU, 4))
        assertTrue(FuritenRules.status(h).discard)
        h.closedTiles.remove(Tile(Suit.SOU, 6))
        h.closedTiles.add(Tile(Suit.SOU, 4, 1))
        assertEquals(setOf(11, 14), FuritenRules.waits(h))
        assertFalse(FuritenRules.status(h).discard)
        assertEquals(listOf(Tile(Suit.SOU, 4)), h.discardHistory)
    }

    @Test fun `all reasons coexist and pure status does not mutate hand`() {
        val h = hand()
        h.discardHistory.add(Tile(Suit.SOU, 4))
        val before = h.closedTiles.toList()
        val status = FuritenRules.status(h, temporary = true, riichi = true)
        assertEquals(FuritenStatus(discard = true, temporary = true, riichi = true), status)
        assertEquals(before, h.closedTiles)
        assertTrue(h.melds.isEmpty())
    }

    @Test fun `wait calculation excludes fifth copy already used in a closed kan`() {
        val h = PlayerHand()
        val kan = (0..3).map { Tile(Suit.MAN, 1, it) }
        h.melds.add(Meld(MeldType.CLOSED_KAN, kan, -1, kan.last()))
        h.closedTiles.addAll(listOf(Tile(Suit.MAN, 2), Tile(Suit.MAN, 3)) + (4..6).map { Tile(Suit.PIN, it) } +
            (7..9).map { Tile(Suit.SOU, it) } + listOf(Tile(Suit.WIND, 2), Tile(Suit.WIND, 2, 1)))
        assertEquals(setOf(3), FuritenRules.waits(h))
        assertFalse(FuritenRules.completesHand(h, Tile(Suit.MAN, 1)))
        assertTrue(FuritenRules.completesHand(h, Tile(Suit.MAN, 4)))
    }

    @Test fun `non winning discard cannot cause furiten and empty history falls back to physical river`() {
        val h = hand()
        h.discards.add(Tile(Suit.MAN, 9))
        assertFalse(FuritenRules.status(h).isFuriten)
        assertFalse(FuritenRules.completesHand(h, Tile(Suit.WIND, 1)))
        h.discards.add(Tile(Suit.SOU, 7))
        assertTrue(FuritenRules.status(h).discard)
        assertFalse(FuritenStatus().isFuriten)
    }
}

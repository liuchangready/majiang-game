package com.majiang.state

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ResponseResolverContractTest {
    private val resolver = ResponseResolver()
    private val tile = Tile(Suit.MAN, 1)

    @Test
    fun `head bump follows distance from every possible discarder`() {
        for (discarder in 0..3) {
            val others = (0..3).filter { it != discarder }
            for (order in listOf(others, others.reversed(), others.drop(1) + others.first())) {
                val actions = order.map { GameAction.DeclareRon(it) }
                assertEquals(GameAction.DeclareRon((discarder + 1) % 4), resolver.resolve(discarder, actions))
            }
        }
    }

    @Test
    fun `ron beats closer pon regardless of arrival order`() {
        val ron = GameAction.DeclareRon(1)
        val pon = GameAction.DeclarePon(3, listOf(tile, tile.copy(copyIndex = 1)))
        assertEquals(ron, resolver.resolve(2, listOf(pon, ron)))
        assertEquals(ron, resolver.resolve(2, listOf(ron, pon)))
    }

    @Test
    fun `pon and open kan have equal priority and closer seat wins`() {
        val pon = GameAction.DeclarePon(3, listOf(tile, tile.copy(copyIndex = 1)))
        val kan = GameAction.DeclareKan(0, (0..3).map { tile.copy(copyIndex = it) }, MeldType.OPEN_KAN)
        assertEquals(resolver.priority(pon), resolver.priority(kan))
        assertEquals(pon, resolver.resolve(2, listOf(kan, pon)))
    }

    @Test
    fun `pon beats chi and all passes produce no selected action`() {
        val chi = GameAction.DeclareChi(3, Meld(MeldType.CHI,
            listOf(tile, Tile(Suit.MAN, 2), Tile(Suit.MAN, 3)), 2, tile))
        val pon = GameAction.DeclarePon(1, listOf(tile, tile.copy(copyIndex = 1)))
        assertEquals(pon, resolver.resolve(2, listOf(chi, pon)))
        assertNull(resolver.resolve(2, listOf(GameAction.Pass(0), GameAction.Pass(1), GameAction.Pass(3))))
    }

    @Test
    fun `duplicate response cannot replace the first response`() {
        val first = mapOf<Int, GameAction>(1 to GameAction.Pass(1))
        assertEquals(first, resolver.recordResponse(first, 1, GameAction.DeclareRon(1)))
        assertEquals(mapOf<Int, GameAction>(1 to GameAction.Pass(1)), first)
    }

    @Test
    fun `response window waits for all eligible seats`() {
        val eligible = setOf(0, 1, 3)
        assertFalse(resolver.isWindowComplete(eligible, mapOf(0 to GameAction.Pass(0))))
        assertFalse(resolver.isWindowComplete(eligible, mapOf(0 to GameAction.Pass(0), 2 to GameAction.Pass(2))))
        assertTrue(resolver.isWindowComplete(eligible, eligible.associateWith { GameAction.Pass(it) }))
        assertTrue(resolver.isWindowComplete(emptySet(), emptyMap()))
    }
}

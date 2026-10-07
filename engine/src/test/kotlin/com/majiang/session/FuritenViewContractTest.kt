package com.majiang.session

import com.majiang.application.ApplicationCoordinator
import com.majiang.model.*
import com.majiang.rules.FuritenStatus
import com.majiang.state.*
import com.majiang.event.GameEventBus
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FuritenViewContractTest {
    private fun waiting() = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } + (2..5).map { Tile(Suit.SOU, it) }

    @Test fun `projection exposes only own furiten and immutable historical discards`() {
        val s = TestTableBuilder.create(hands = mapOf(2 to waiting()), drawn = 2 to Tile(Suit.SOU, 6))
        s.players[2].discardHistory.add(Tile(Suit.SOU, 5, 1))
        s.sameTurnFuriten[2] = true; s.riichiFuriten[2] = true
        val v = PlayerView.fromGameState(s, 2, emptyList())
        assertEquals(FuritenStatus(true, true, true), v.myFuriten)
        assertEquals(listOf(Tile(Suit.SOU, 5, 1)), v.myDiscardHistory)
        s.players[2].discardHistory.clear(); s.sameTurnFuriten[2] = false
        assertTrue(v.myFuriten.temporary)
        assertEquals(1, v.myDiscardHistory.size)
        assertThrows(UnsupportedOperationException::class.java) { (v.myDiscardHistory as MutableList).clear() }
        val other = PlayerView.fromGameState(s, 0, emptyList())
        assertFalse(other.myFuriten.temporary)
        assertFalse(other.myFuriten.riichi)
        assertTrue(other.myDiscardHistory.isEmpty())
    }

    @Test fun `different riichi discards preview different furiten and remain selectable`() {
        val s = TestTableBuilder.create(hands = mapOf(2 to waiting()), drawn = 2 to Tile(Suit.SOU, 6))
        s.players[2].discardHistory.add(Tile(Suit.SOU, 5, 1))
        val e = ApplicationCoordinator.createForTest(s, GameEventBus(), autoPlayAi = false)
        val live = e.activeRound!!.readState()
        val v = PlayerView.fromGameState(live, 2, e.legalActions(2))
        val options = RiichiPreviewProvider.options(v)
        val discard2 = options.single { it.action.discardTile == Tile(Suit.SOU, 2) }
        val discard6 = options.single { it.action.discardTile == Tile(Suit.SOU, 6) }
        assertFalse(discard2.furiten.isFuriten)
        assertTrue(discard6.furiten.discard)
        assertEquals(listOf(10, 13), discard6.waits.map { it.typeIndex })
        assertTrue(discard6.action in e.legalActions(2), "Furiten riichi remains legal")
        assertEquals(25000, live.scores[2])
        assertEquals(14, live.players[2].tileCount)
    }

    @Test fun `preview includes the newly discarded tile when determining furiten`() {
        val east0 = Tile(Suit.WIND, 1)
        val east1 = Tile(Suit.WIND, 1, 1)
        val closed = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } + (1..3).map { Tile(Suit.SOU, it) } + east0
        val s = TestTableBuilder.create(hands = mapOf(0 to closed), drawn = 0 to east1)
        val e = ApplicationCoordinator.createForTest(s, GameEventBus(), autoPlayAi = false)
        val live = e.activeRound!!.readState()
        val options = RiichiPreviewProvider.options(PlayerView.fromGameState(live, 0, e.legalActions(0)))
        for (east in listOf(east0, east1)) assertTrue(options.single { it.action.discardTile == east }.furiten.discard)
        assertTrue(live.players[0].discardHistory.isEmpty())
    }
}

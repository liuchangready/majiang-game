package com.majiang.session

import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SessionSnapshotContractTest {
    private fun view(state: GameState = GameState(), actions: List<GameAction> = emptyList()): PlayerView =
        PlayerView.fromGameState(state, 0, actions)

    @Test
    fun `view never shares a meld's tile collection with authoritative state`() {
        val state = GameState()
        val source = mutableListOf(Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3))
        state.players[1].melds.add(Meld(MeldType.CHI, source, 0, source.first()))
        val snapshot = view(state)
        source.clear()
        assertEquals(3, snapshot.melds[1].single().tiles.size)
    }

    @Test
    fun `view snapshots the nested tile list of a legal action`() {
        val state = GameState()
        val source = mutableListOf(Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 1, 1))
        val snapshot = view(state, listOf(GameAction.DeclarePon(0, source)))
        source.clear()
        assertEquals(2, (snapshot.legalActions.single() as GameAction.DeclarePon).tiles.size)
    }

    @Test
    fun `view scores and hand stay unchanged when state advances`() {
        val state = GameState()
        state.players[0].closedTiles.add(Tile(Suit.MAN, 1))
        val snapshot = view(state)
        state.scores[0] = 12345
        state.players[0].closedTiles.clear()
        assertEquals(25000, snapshot.scores[0])
        assertEquals(listOf(Tile(Suit.MAN, 1)), snapshot.myClosedTiles)
    }

    @Test
    fun `session update does not retain the producer's mutable event list`() {
        val source = mutableListOf<GameEvent>(GameEvent.DealingComplete())
        val update = SessionUpdate(view(), source)
        source.clear()
        assertEquals(1, update.visibleEvents.size)
    }

    @Test
    fun `session score event snapshots mutable scores and payment map`() {
        val scores = intArrayOf(25000, 26000, 24000, 25000)
        val payments = mutableMapOf(0 to 0, 1 to 1000, 2 to -1000, 3 to 0)
        val update = SessionUpdate(view(), listOf(GameEvent.ScoreSettled(5, payments, scores)))
        scores[0] = -999
        payments[1] = 99999
        val event = update.visibleEvents.single() as GameEvent.ScoreSettled
        assertEquals(25000, event.newScores[0])
        assertEquals(1000, event.payments[1])
    }

    @Test
    fun `private dealt drawn and rinshan tiles only reach their owner`() {
        val tile = Tile(Suit.PIN, 5)
        val privateEvents = listOf(
            GameEvent.TileDealt(0, 1, tile),
            GameEvent.TileDrawn(1, 1, tile, 50),
            GameEvent.RinshanDraw(1, 1, tile)
        )
        for (seat in 0..3) {
            val visible = SessionUpdate.filterVisibleEvents(privateEvents, seat)
            assertEquals(if (seat == 1) 3 else 0, visible.size, "viewer=$seat")
        }
    }

    @Test
    fun `unpublished ura dora cannot use the default public event branch`() {
        val hidden = GameEvent.DoraRevealed(0, Tile(Suit.PIN, 5), true)
        assertTrue(SessionUpdate.filterVisibleEvents(listOf(hidden), 1).isEmpty())
    }

    @Test
    fun `public discard remains visible to all seats`() {
        val discard = GameEvent.TileDiscarded(3, 2, Tile(Suit.SOU, 7))
        for (seat in 0..3) {
            assertEquals(listOf(discard), SessionUpdate.filterVisibleEvents(listOf(discard), seat))
        }
    }
}

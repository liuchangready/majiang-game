package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEvent
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.render.DiscardAnimation
import com.majiang.session.PlayerView
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.testing.FakeGameSession
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiDiscardOrderAcceptanceTest : GdxHeadlessTestRunner() {
    private fun fixture(): Pair<GameScreen, FakeGameSession> {
        val state = GameState().also { it.phase = GamePhase.DISCARD }
        val session = FakeGameSession(PlayerView.fromGameState(state, 0, emptyList()))
        return GameScreen(MahjongGame(), session) to session
    }

    @Test fun `batched riichi discards follow event chronology across rounds instead of seat grouping`() {
        val (screen, session) = fixture()
        val seats = listOf(2, 3, 0, 1, 2, 3, 0, 1)
        val events = seats.mapIndexed { i, seat ->
            GameEvent.TileDiscarded(i, seat, Tile(Suit.MAN, i + 1), isTsumogiri = true)
        }
        val rivers = List(4) { seat -> events.filter { it.player == seat }.map { it.tile } }
        session.update(session.view().copy(discards = rivers), events)
        screen.checkDiscardsDiff()
        assertEquals(seats, screen.discardQueue.map { it.seat })
        assertEquals(events.map { it.tile }, screen.discardQueue.map { it.tile })
        // Repeated snapshot reconciliation must never add the same physical discard again.
        repeat(3) { screen.checkDiscardsDiff() }
        assertEquals(8, screen.discardQueue.size)
    }

    @Test fun `incoming batch stays behind active animation and retains its event order`() {
        val (screen, session) = fixture()
        val first = Tile(Suit.PIN, 1)
        val rivers = List(4) { mutableListOf<Tile>() }
        rivers[1].add(first)
        session.update(session.view().copy(discards = rivers.map { it.toList() }),
            listOf(GameEvent.TileDiscarded(1, 1, first)))
        screen.updateDiscardAnimation(0.01f)
        val next = listOf(2, 3, 0).mapIndexed { i, seat ->
            GameEvent.TileDiscarded(i + 2, seat, Tile(Suit.PIN, i + 2))
        }
        next.forEach { rivers[it.player].add(it.tile) }
        session.update(session.view().copy(discards = rivers.map { it.toList() }), next)
        assertEquals(first, screen.currentDiscardAnim?.tile)
        assertEquals(listOf(2, 3, 0), screen.discardQueue.map { it.seat })
    }

    @Test fun `claimed discards in event history are not resurrected into playback`() {
        val (screen, session) = fixture()
        val taken = Tile(Suit.SOU, 1)
        val retained = Tile(Suit.SOU, 2)
        val events = listOf(GameEvent.TileDiscarded(1, 3, taken), GameEvent.TileDiscarded(2, 1, retained))
        session.update(session.view().copy(discards = listOf(emptyList(), listOf(retained), emptyList(), emptyList())), events)
        assertEquals(listOf(retained), screen.discardQueue.map { it.tile })
    }

    @Test fun `duplicate event delivery does not replay a completed physical discard`() {
        val (screen, session) = fixture()
        val tile = Tile(Suit.PIN, 8)
        val events = listOf(GameEvent.TileDiscarded(1, 2, tile))
        val view = session.view().copy(discards = listOf(emptyList(), emptyList(), listOf(tile), emptyList()))
        session.update(view, events)
        screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        assertFalse(screen.isDiscardPlaybackBusy)
        session.update(view, events)
        assertFalse(screen.isDiscardPlaybackBusy, "An unchanged snapshot plus repeated event is not a new discard")
    }
}

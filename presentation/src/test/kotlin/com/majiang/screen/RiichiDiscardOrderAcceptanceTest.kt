package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.GameEventView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiDiscardOrderAcceptanceTest : GdxHeadlessTestRunner() {

    private fun fixture(): Pair<GameScreen, FakeGameSession> {
        val view = TestViewBuilder.playerView(viewerSeat = 0)
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        return screen to session
    }

    @Test fun `batched riichi discards follow event chronology across rounds instead of seat grouping`() {
        val (screen, session) = fixture()
        val seats = listOf(2, 3, 0, 1, 2, 3, 0, 1)
        val events = seats.mapIndexed { i, seat ->
            GameEventView.TileDiscarded(i, seat, TestViewBuilder.tile(SuitView.MAN, i + 1), isTsumogiri = true)
        }
        val rivers = List(4) { seat -> events.filter { it.player == seat }.map { it.tile } }
        session.update(session.snapshot.view.copy(discards = rivers), events)
        screen.checkDiscardsDiff()
        assertEquals(seats, screen.discardQueue.map { it.seat })
        assertEquals(events.map { it.tile }, screen.discardQueue.map { it.tile })
        // Repeated snapshot reconciliation must never add the same physical discard again.
        repeat(3) { screen.checkDiscardsDiff() }
        assertEquals(8, screen.discardQueue.size)
        screen.dispose()
    }

    @Test fun `incoming batch stays behind active animation and retains its event order`() {
        val (screen, session) = fixture()
        val first = TestViewBuilder.tile(SuitView.PIN, 1)
        val rivers = List(4) { mutableListOf<TileView>() }
        rivers[1].add(first)
        session.update(session.snapshot.view.copy(discards = rivers.map { it.toList() }),
            listOf(GameEventView.TileDiscarded(1, 1, first)))
        screen.updateDiscardAnimation(0.01f)
        val next = listOf(2, 3, 0).mapIndexed { i, seat ->
            GameEventView.TileDiscarded(i + 2, seat, TestViewBuilder.tile(SuitView.PIN, i + 2))
        }
        next.forEach { rivers[it.player].add(it.tile) }
        session.update(session.snapshot.view.copy(discards = rivers.map { it.toList() }), next)
        assertEquals(first, screen.currentDiscardAnim?.tile)
        assertEquals(listOf(2, 3, 0), screen.discardQueue.map { it.seat })
        screen.dispose()
    }

    @Test fun `claimed discards in event history are not resurrected into playback`() {
        val (screen, session) = fixture()
        val taken = TestViewBuilder.tile(SuitView.SOU, 1)
        val retained = TestViewBuilder.tile(SuitView.SOU, 2)
        val events = listOf(
            GameEventView.TileDiscarded(1, 3, taken),
            GameEventView.TileDiscarded(2, 1, retained)
        )
        val rivers = List(4) { seat ->
            if (seat == 1) listOf(retained) else emptyList()
        }
        session.update(session.snapshot.view.copy(discards = rivers), events)
        screen.checkDiscardsDiff()
        assertEquals(listOf(1), screen.discardQueue.map { it.seat })
        assertEquals(listOf(retained), screen.discardQueue.map { it.tile })
        screen.dispose()
    }
}

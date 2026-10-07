package com.majiang.v3

import com.majiang.timing.*
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TimeControlAcceptanceTest {
    private class TestClock(var now: Long = 0) : MonotonicClock { override fun nowMillis() = now }

    @Test fun `each decision grants three seconds before consuming round reserve`() {
        val source = TestClock()
        val clock = DecisionClock(clock = source)
        clock.beginRound("r1")
        val first = DecisionKey("r1", 0, "first")
        assertEquals(23000L, clock.open(first).deadlineMillis)
        source.now = 5000
        assertTrue(clock.complete(first))
        assertEquals(18000L, clock.remainingReserve(0))
        val next = DecisionKey("r1", 0, "next")
        assertEquals(26000L, clock.open(next).deadlineMillis)
        source.now = 7000
        assertTrue(clock.complete(next))
        assertEquals(18000L, clock.remainingReserve(0))
        assertEquals(20000L, clock.remainingReserve(1))
    }

    @Test fun `duplicate opens and round notifications cannot reset a running deadline`() {
        val source = TestClock()
        val clock = DecisionClock(clock = source)
        clock.beginRound("r1")
        val key = DecisionKey("r1", 0, "choice")
        val opened = clock.open(key)
        source.now = 12000
        clock.beginRound("r1")
        assertEquals(opened, clock.open(key))
        assertThrows(RuntimeException::class.java) { clock.open(DecisionKey("r1", 0, "another")) }
        assertEquals(opened, clock.timing(0))
        source.now = 23000
        assertFalse(clock.complete(key), "Exactly at deadline the new choice is late")
        assertEquals(1, clock.expired().size)
        assertTrue(clock.expire(key, opened.generation))
        assertFalse(clock.expire(key, opened.generation))
        assertEquals(0L, clock.remainingReserve(0))
        assertEquals(26000L, clock.open(DecisionKey("r1", 0, "after-timeout")).deadlineMillis)
    }

    @Test fun `cancelled window spends elapsed reserve and old timer cannot consume a new decision`() {
        val source = TestClock()
        val clock = DecisionClock(clock = source)
        clock.beginRound("r1")
        val old = DecisionKey("r1", 1, "old")
        val timing = clock.open(old)
        source.now = 5000
        clock.cancel(old)
        assertEquals(18000L, clock.remainingReserve(1))
        val current = clock.open(DecisionKey("r1", 1, "new"))
        source.now = 23000
        assertFalse(clock.expire(old, timing.generation))
        assertEquals(current, clock.timing(1))
        assertEquals(18000L, clock.remainingReserve(1))
    }

    @Test fun `round and session reserve scopes differ only at fresh deal`() {
        for (scope in ReserveScope.entries) {
            val source = TestClock()
            val clock = DecisionClock(TimeControlConfig(reserveScope = scope), source)
            clock.beginRound("r1")
            val key = DecisionKey("r1", 2, "turn")
            clock.open(key)
            source.now = 13000
            assertTrue(clock.complete(key))
            assertEquals(10000L, clock.remainingReserve(2))
            clock.beginRound("r2")
            assertEquals(if (scope == ReserveScope.ROUND) 20000L else 10000L, clock.remainingReserve(2))
            assertFalse(clock.complete(key))
        }
    }

    @Test fun `simultaneous response seats have independent deadlines and balances`() {
        val source = TestClock()
        val clock = DecisionClock(clock = source)
        clock.beginRound("r1")
        val one = DecisionKey("r1", 1, "window-1")
        val two = DecisionKey("r1", 2, "window-2")
        clock.open(one)
        clock.open(two)
        source.now = 8000
        assertTrue(clock.complete(one))
        assertEquals(15000L, clock.remainingReserve(1))
        source.now = 23000
        assertEquals(listOf(two), clock.expired().map { it.key })
        assertEquals(15000L, clock.remainingReserve(1))
    }

    @Test fun `response timeout passes ron and own turn timeout only picks a legal discard`() {
        val state = GameState()
        state.phase = GamePhase.RESPONSE_WINDOW
        val pass = GameAction.Pass(0)
        var view = PlayerView.fromGameState(state, 0, listOf(GameAction.DeclareRon(0), pass))
        assertEquals(pass, TimeoutPolicy.choose(view, view.legalActions))
        val drawn = Tile(Suit.MAN, 4)
        val other = Tile(Suit.PIN, 5)
        state.phase = GamePhase.DISCARD
        state.players[0].drawnTile = drawn
        val discard = GameAction.DiscardTile(0, drawn)
        view = PlayerView.fromGameState(state, 0, listOf(GameAction.DeclareTsumo(0), discard, GameAction.DiscardTile(0, other)))
        assertEquals(discard, TimeoutPolicy.choose(view, view.legalActions))
        state.players[0].drawnTile = null
        view = PlayerView.fromGameState(state, 0, listOf(GameAction.DiscardTile(0, other)))
        assertEquals(GameAction.DiscardTile(0, other), TimeoutPolicy.choose(view, view.legalActions))
        assertNull(TimeoutPolicy.choose(view, emptyList()))
    }

    @Test fun `early or paused timeout callbacks must not consume a live reserve`() {
        val source = TestClock()
        val clock = DecisionClock(clock = source)
        clock.beginRound("r1")
        val key = DecisionKey("r1", 0, "turn")
        val timing = clock.open(key)
        source.now = 2000
        assertFalse(clock.expire(key, timing.generation))
        assertEquals(20000L, clock.remainingReserve(0))
        assertNotNull(clock.timing(0))
    }

    @Test fun `decision for an old round cannot be opened against the new round budget`() {
        val clock = DecisionClock(clock = TestClock())
        clock.beginRound("new-round")
        assertThrows(RuntimeException::class.java) { clock.open(DecisionKey("old-round", 0, "late")) }
        assertNull(clock.timing(0))
        assertEquals(20000L, clock.remainingReserve(0))
    }

    @Test fun `pause freezes remainder and resume does not award a new three seconds`() {
        val source = TestClock()
        val clock = DecisionClock(clock = source)
        clock.beginRound("r1")
        val key = DecisionKey("r1", 0, "turn")
        val timing = clock.open(key)
        source.now = 5000
        clock.pause()
        source.now = 50000
        assertTrue(clock.expired().isEmpty())
        assertFalse(clock.expire(key, timing.generation))
        assertFalse(clock.complete(key))
        clock.resume()
        assertEquals(68000L, clock.timing(0)!!.deadlineMillis)
        source.now = 52000
        assertTrue(clock.complete(key))
        assertEquals(16000L, clock.remainingReserve(0))
    }

    @Test fun `session bank settles unfinished decision before starting another deal`() {
        val source = TestClock()
        val clock = DecisionClock(TimeControlConfig(reserveScope = ReserveScope.SESSION), source)
        clock.beginRound("r1")
        clock.open(DecisionKey("r1", 0, "turn"))
        source.now = 8000
        clock.beginRound("r2")
        assertEquals(15000L, clock.remainingReserve(0))
        assertNull(clock.timing(0))
    }
}

package com.majiang.event

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class EventIsolationContractTest {
    @Test
    fun `listener and publisher mutation cannot corrupt history or other listeners`() {
        val bus = GameEventBus()
        var secondScores: List<Int>? = null
        bus.subscribe<GameEvent.ScoreSettled>(priority = 0) { it.newScores[0] = -999 }
        bus.subscribe<GameEvent.ScoreSettled>(priority = 1) { secondScores = it.newScores.toList() }
        val scores = intArrayOf(26000, 24000, 25000, 25000)
        val payments = mutableMapOf(0 to 1000, 1 to -1000)
        bus.publish(GameEvent.ScoreSettled(1, payments, scores))
        scores[1] = -999
        payments.clear()
        assertEquals(listOf(26000, 24000, 25000, 25000), secondScores)
        val history = bus.getHistory().single() as GameEvent.ScoreSettled
        assertEquals(listOf(26000, 24000, 25000, 25000), history.newScores.toList())
        assertEquals(mapOf(0 to 1000, 1 to -1000), history.payments)
        history.newScores[2] = -999
        assertEquals(25000, (bus.getHistory().single() as GameEvent.ScoreSettled).newScores[2])
    }

    @Test
    fun `throwing observer does not prevent later observers or later publications`() {
        val bus = GameEventBus()
        var delivered = 0
        bus.subscribe<GameEvent.StackingStarted>(priority = 0) { error("broken observer") }
        bus.subscribe<GameEvent.StackingStarted>(priority = 1) { delivered++ }
        assertDoesNotThrow { bus.publish(GameEvent.StackingStarted()) }
        assertDoesNotThrow { bus.publish(GameEvent.StackingStarted()) }
        assertEquals(2, delivered)
        assertEquals(2, bus.getHistory().size)
    }
}

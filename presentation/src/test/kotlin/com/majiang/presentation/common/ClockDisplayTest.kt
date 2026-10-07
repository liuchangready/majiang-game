package com.majiang.presentation.common

import com.majiang.client.api.ClockInfo
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ClockDisplayTest {

    @Test
    fun `null clockInfo returns inactive clock with dashes`() {
        val formatted = ClockDisplay.format(null, 0L)
        assertFalse(formatted.hasActiveTiming)
        assertEquals(0L, formatted.freeSeconds)
        assertEquals(0f, formatted.reserveSeconds)
        assertEquals("--+--", formatted.displayText)
        assertFalse(formatted.isUrgent)
    }

    @Test
    fun `idle timing returns inactive clock with reserve formatted`() {
        val clock = ClockInfo(
            seat = 0,
            opensAt = 10000L,
            deadline = 10000L,
            reserve = 20000L,
            serverNow = 10000L
        )
        val formatted = ClockDisplay.format(clock, 0L)
        assertFalse(formatted.hasActiveTiming)
        assertEquals(0L, formatted.freeSeconds)
        assertEquals(20f, formatted.reserveSeconds)
        assertEquals("0+20", formatted.displayText)
        assertFalse(formatted.isUrgent)
    }

    @Test
    fun `active timing decrements free time before reserve`() {
        val opensAt = 10000L
        val reserve = 20000L // 20s
        val free = 3000L // 3s
        val deadline = opensAt + free + reserve // 33000L
        val clock = ClockInfo(
            seat = 0,
            opensAt = opensAt,
            deadline = deadline,
            reserve = reserve,
            serverNow = opensAt
        )

        // At start (elapsed 0): 3s free + 20s reserve
        val atStart = ClockDisplay.format(clock, 0L)
        assertTrue(atStart.hasActiveTiming)
        assertEquals(3L, atStart.freeSeconds)
        assertEquals(20f, atStart.reserveSeconds)
        assertEquals("3+20", atStart.displayText)
        assertFalse(atStart.isUrgent)

        // Elapsed 1.5s: 1.5s free remaining -> 2s ceil, 20s reserve
        val at1500 = ClockDisplay.format(clock, 1500L)
        assertEquals(2L, at1500.freeSeconds)
        assertEquals(20f, at1500.reserveSeconds)
        assertEquals("2+20", at1500.displayText)
        assertFalse(at1500.isUrgent)

        // Elapsed 3.0s: 0s free remaining, 20s reserve
        val at3000 = ClockDisplay.format(clock, 3000L)
        assertEquals(0L, at3000.freeSeconds)
        assertEquals(20f, at3000.reserveSeconds)
        assertEquals("0+20", at3000.displayText)
        assertFalse(at3000.isUrgent)

        // Elapsed 4.0s: 0s free remaining, 19s reserve
        val at4000 = ClockDisplay.format(clock, 4000L)
        assertEquals(0L, at4000.freeSeconds)
        assertEquals(19f, at4000.reserveSeconds)
        assertEquals("0+19", at4000.displayText)
        assertFalse(at4000.isUrgent)
    }

    @Test
    fun `urgent flag triggers when free time is zero and reserve is below 5 seconds`() {
        val opensAt = 10000L
        val reserve = 20000L
        val free = 3000L
        val deadline = opensAt + free + reserve
        val clock = ClockInfo(
            seat = 0,
            opensAt = opensAt,
            deadline = deadline,
            reserve = reserve,
            serverNow = opensAt
        )

        // Elapsed 18.5s: total elapsed 18.5s -> remaining 4.5s -> free 0, reserve 4.5s -> ceil 5s, reserveRemaining < 5000ms -> urgent!
        val at18500 = ClockDisplay.format(clock, 18500L)
        assertEquals(0L, at18500.freeSeconds)
        assertEquals(4.5f, at18500.reserveSeconds)
        assertEquals("0+5", at18500.displayText)
        assertTrue(at18500.isUrgent)

        // Elapsed 22.5s: reserve 0.5s -> ceil 1s -> urgent
        val at22500 = ClockDisplay.format(clock, 22500L)
        assertEquals(0L, at22500.freeSeconds)
        assertEquals(0.5f, at22500.reserveSeconds)
        assertEquals("0+1", at22500.displayText)
        assertTrue(at22500.isUrgent)

        // Elapsed 25s (beyond deadline): 0+0 -> urgent
        val at25000 = ClockDisplay.format(clock, 25000L)
        assertEquals(0L, at25000.freeSeconds)
        assertEquals(0f, at25000.reserveSeconds)
        assertEquals("0+0", at25000.displayText)
        assertTrue(at25000.isUrgent)
    }
}

package com.majiang.presentation.common

import com.majiang.client.api.PublicSessionResultView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FinalResultRendererTest {

    @Test
    fun `PublicSessionResultView encapsulates 4 player ranking and scores`() {
        val result = PublicSessionResultView(
            finalScores = listOf(35000, 25000, 22000, 18000),
            rankedSeats = listOf(0, 1, 2, 3),
            reason = "NORMAL_COMPLETION",
            finalCheatPoints = listOf(10, 0, 5, 0)
        )

        assertEquals(4, result.finalScores.size)
        assertEquals(4, result.rankedSeats.size)
        assertEquals("NORMAL_COMPLETION", result.reason)
        assertEquals(0, result.rankedSeats[0])
        assertEquals(35000, result.finalScores[0])
    }

    @Test
    fun `bankruptcy final result has negative score for bust seat`() {
        val result = PublicSessionResultView(
            finalScores = listOf(42000, 31000, 29000, -2000),
            rankedSeats = listOf(0, 1, 2, 3),
            reason = "BANKRUPTCY",
            finalCheatPoints = listOf(20, 10, 0, 0)
        )

        assertEquals("BANKRUPTCY", result.reason)
        assertTrue(result.finalScores[3] < 0)
    }

    @Test
    fun `final result renderer can be instantiated safely`() {
        val renderer = FinalResultRenderer()
        assertNotNull(renderer)
    }
}

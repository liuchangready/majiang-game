package com.majiang.settlement

import com.majiang.rules.ScoreCalculator
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Fixed monetary oracles authored by Codex before the implementation. */
class SettlementServiceContractTest {
    private val service = SettlementService()
    private val score = ScoreCalculator.INSTANCE.calculate(30, 3)
    private val funded = SettlementInput(listOf(25000, 25000, 25000, 25000), 0, 2, 3)

    private fun checkLedger(input: SettlementInput, result: SettlementResult, expected: List<Int>, sticks: Int) {
        assertEquals(expected, result.deltas)
        assertEquals(input.scores.zip(expected) { old, delta -> old + delta }, result.newScores)
        assertEquals(sticks, result.remainingRiichiSticks)
        assertEquals(input.scores.sum() + input.riichiSticks * 1000,
            result.newScores.sum() + result.remainingRiichiSticks * 1000)
        assertEquals(4, result.deltas.size)
        assertEquals(4, result.newScores.size)
    }

    @Test
    fun `nondealer ron debits honba from discarder and awards all deposits`() {
        checkLedger(funded, service.ron(funded, 1, 2, score), listOf(0, 7500, -4500, 0), 0)
    }

    @Test
    fun `dealer ron uses dealer rate and includes honba and deposits`() {
        checkLedger(funded, service.ron(funded, 0, 2, score), listOf(9400, 0, -6400, 0), 0)
    }

    @Test
    fun `nondealer tsumo charges each opponent a honba payment`() {
        checkLedger(funded, service.tsumo(funded, 1, score), listOf(-2200, 7600, -1200, -1200), 0)
    }

    @Test
    fun `dealer tsumo charges three equal payments`() {
        checkLedger(funded, service.tsumo(funded, 0, score), listOf(9600, -2200, -2200, -2200), 0)
    }

    @Test
    fun `zero honba and deposits do not introduce extra points`() {
        val input = funded.copy(honba = 0, riichiSticks = 0)
        checkLedger(input, service.ron(input, 1, 2, score), listOf(0, 3900, -3900, 0), 0)
        checkLedger(input, service.tsumo(input, 1, score), listOf(-2000, 4000, -1000, -1000), 0)
    }

    @Test
    fun `all sixteen exhaustive draw seat combinations preserve deposits and balance`() {
        for (mask in 0..15) {
            val tenpai = (0..3).filter { mask and (1 shl it) != 0 }.toSet()
            val n = tenpai.size
            val expected = (0..3).map { seat ->
                when {
                    n == 0 || n == 4 -> 0
                    seat in tenpai -> 3000 / n
                    else -> -3000 / (4 - n)
                }
            }
            checkLedger(funded, service.exhaustiveDraw(funded, tenpai), expected, 3)
        }
    }

    @Test
    fun `abortive draw transfers no money even with honba and deposits`() {
        checkLedger(funded, service.abortiveDraw(funded), listOf(0, 0, 0, 0), 3)
    }

    @Test
    fun `service never mutates its input score list`() {
        val scores = mutableListOf(25000, 23000, 27000, 21000)
        val input = funded.copy(scores = scores)
        val before = scores.toList()
        service.ron(input, 1, 2, score)
        service.tsumo(input, 1, score)
        service.exhaustiveDraw(input, setOf(0, 1))
        service.abortiveDraw(input)
        assertEquals(before, scores)
    }

    @Test
    fun `changing dealer seat changes who pays the higher tsumo amount`() {
        val input = funded.copy(dealer = 3)
        checkLedger(input, service.tsumo(input, 1, score), listOf(-1200, 7600, -1200, -2200), 0)
    }

    @Test
    fun `settlement is repeatable as a pure calculation`() {
        val first = service.ron(funded, 1, 2, score)
        val second = service.ron(funded, 1, 2, score)
        assertEquals(first, second)
        assertEquals(listOf(25000, 25000, 25000, 25000), funded.scores)
    }
}

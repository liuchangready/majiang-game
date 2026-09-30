package com.majiang.match

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MatchProgressionContractTest {
    private val service = MatchProgression()
    private fun input(outcome: RoundOutcome = RoundOutcome.Tsumo(1)) = ProgressionInput(
        currentDealer = 0, currentRoundWind = 1, currentRoundNumber = 0,
        currentHonba = 2, scores = listOf(25000, 25000, 25000, 25000), outcome = outcome
    )
    private fun next(input: ProgressionInput): ProgressionResult.NextRound {
        val result = service.nextRound(input)
        assertTrue(result is ProgressionResult.NextRound)
        return result as ProgressionResult.NextRound
    }

    @Test
    fun `dealer win repeats the same round and increments honba`() {
        val result = next(input(RoundOutcome.Tsumo(0)))
        assertEquals(listOf(1, 0, 0, 3), listOf(result.roundWind, result.roundNumber, result.dealer, result.honba))
    }

    @Test
    fun `nondealer win rotates dealer and clears honba`() {
        val result = next(input())
        assertEquals(listOf(1, 1, 1, 0), listOf(result.roundWind, result.roundNumber, result.dealer, result.honba))
    }

    @Test
    fun `exhaustive draw increments honba whether dealer is tenpai or not`() {
        val continuing = next(input(RoundOutcome.ExhaustiveDraw(setOf(0, 2))))
        val rotating = next(input(RoundOutcome.ExhaustiveDraw(setOf(1, 2))))
        assertEquals(listOf(0, 0, 3), listOf(continuing.roundNumber, continuing.dealer, continuing.honba))
        assertEquals(listOf(1, 1, 3), listOf(rotating.roundNumber, rotating.dealer, rotating.honba))
    }

    @Test
    fun `abortive draw repeats dealer and adds honba`() {
        val result = next(input(RoundOutcome.AbortiveDraw("NINE_TERMINALS")))
        assertEquals(listOf(0, 0, 3), listOf(result.roundNumber, result.dealer, result.honba))
    }

    @Test
    fun `east four rotates into south one`() {
        val result = next(input().copy(currentDealer = 3, currentRoundNumber = 3))
        assertEquals(listOf(2, 0, 0), listOf(result.roundWind, result.roundNumber, result.dealer))
    }

    @Test
    fun `south four ends when a player reaches the target`() {
        val result = service.nextRound(input().copy(currentRoundWind = 2, currentRoundNumber = 3,
            scores = listOf(20000, 30000, 25000, 25000)))
        assertTrue(result is ProgressionResult.MatchFinished)
    }

    @Test
    fun `south four without target extends to west one`() {
        val result = next(input().copy(currentRoundWind = 2, currentRoundNumber = 3))
        assertEquals(listOf(3, 0, 1), listOf(result.roundWind, result.roundNumber, result.dealer))
    }

    @Test
    fun `west ends on target even when dealer would continue`() {
        val result = service.nextRound(input(RoundOutcome.Tsumo(0)).copy(currentRoundWind = 3,
            scores = listOf(30000, 25000, 25000, 20000)))
        assertTrue(result is ProgressionResult.MatchFinished)
    }

    @Test
    fun `west four rotation ends instead of producing north wind`() {
        val result = service.nextRound(input().copy(currentRoundWind = 3, currentRoundNumber = 3))
        assertTrue(result is ProgressionResult.MatchFinished)
    }

    @Test
    fun `bankruptcy requires strictly negative points`() {
        val zero = input().copy(scores = listOf(0, 40000, 30000, 30000))
        assertTrue(service.nextRound(zero) is ProgressionResult.NextRound)
        assertTrue(service.nextRound(zero.copy(scores = listOf(-100, 40100, 30000, 30000))) is ProgressionResult.MatchFinished)
    }

    @Test
    fun `final dealer can choose to finish but a tied dealer cannot`() {
        val final = input(RoundOutcome.Tsumo(0)).copy(currentRoundWind = 2, currentRoundNumber = 3,
            scores = listOf(35000, 25000, 20000, 20000))
        assertTrue(next(final).canDealerDeclareAgariYame)
        assertTrue(service.nextRound(final.copy(dealerDeclaredAgariYame = true)) is ProgressionResult.MatchFinished)
        val tied = final.copy(scores = listOf(30000, 30000, 20000, 20000), dealerDeclaredAgariYame = true)
        assertFalse(next(tied).canDealerDeclareAgariYame)
    }

    @Test
    fun `remaining deposits awarded to tied first place by initial seat order`() {
        val result = service.nextRound(input().copy(currentRoundWind = 2, currentRoundNumber = 3,
            scores = listOf(30000, 30000, 20000, 20000), remainingRiichiSticks = 2))
        assertTrue(result is ProgressionResult.MatchFinished)
        result as ProgressionResult.MatchFinished
        assertEquals(listOf(32000, 30000, 20000, 20000), result.finalScores)
        assertEquals(listOf(0, 1, 2, 3), result.rankings.map { it.seat })
        assertEquals(102000, result.finalScores.sum())
    }

    @Test
    fun `east only without extension finishes after east four`() {
        val short = MatchProgression(MatchRuleConfig(length = MatchLength.TONPUUSEN, allowWestExtension = false))
        assertTrue(short.nextRound(input().copy(currentRoundNumber = 3)) is ProgressionResult.MatchFinished)
    }
}

package com.majiang.v3

import com.majiang.match.*
import com.majiang.economy.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MatchEconomyAcceptanceTest {
    @Test fun `unsupported three player setup and malformed schedule fail before a game is created`() {
        assertThrows(IllegalArgumentException::class.java) { MatchConfig(playerCount = 3).validateSupported() }
        assertThrows(IllegalArgumentException::class.java) { MatchConfig(handsPerWind = 0).validateSupported() }
        assertThrows(IllegalArgumentException::class.java) { MatchConfig(tieBreakSeats = listOf(0, 0, 1, 2)).validateSupported() }
        assertThrows(IllegalArgumentException::class.java) { MatchConfig(winds = listOf(2, 1)).validateSupported() }
        MatchConfig(handsPerWind = 3).validateSupported()
    }

    @Test fun `four player short schedule ends at its configured final hand but permits dealer continuation`() {
        val config = MatchConfig(handsPerWind = 3, winds = listOf(1), allowExtension = false)
        val progression = MatchProgression(config.toRuleConfig())
        val scores = listOf(25000, 25000, 25000, 25000)
        val ended = progression.nextRound(2, 1, 2, 0, scores, RoundOutcome.NonDealerWin(1))
        assertTrue(ended is ProgressionResult.MatchFinished)
        val continuation = progression.nextRound(2, 1, 2, 0, scores, RoundOutcome.DealerWin(2))
        assertTrue(continuation is ProgressionResult.NextRound)
        continuation as ProgressionResult.NextRound
        assertEquals(2, continuation.roundNumber)
        assertEquals(2, continuation.dealer)
        assertEquals(1, continuation.honba)
    }

    @Test fun `south short schedule advances at configured boundary instead of a hardcoded fourth hand`() {
        val progression = MatchProgression(MatchConfig(handsPerWind = 3, allowExtension = false).toRuleConfig())
        val result = progression.nextRound(2, 1, 2, 0, List(4) { 25000 }, RoundOutcome.NonDealerWin(0))
        assertTrue(result is ProgressionResult.NextRound)
        result as ProgressionResult.NextRound
        assertEquals(2, result.roundWind)
        assertEquals(0, result.roundNumber)
        assertEquals(3, result.dealer)
    }

    @Test fun `bankruptcy threshold and tie break order are explicit match policies`() {
        val policy = MatchProgression(MatchConfig(bankruptcyInclusive = true, tieBreakSeats = listOf(2, 3, 0, 1)).toRuleConfig())
        val ended = policy.nextRound(0, 1, 0, 0, listOf(0, 20000, 40000, 40000), RoundOutcome.NonDealerWin(1))
        assertTrue(ended is ProgressionResult.MatchFinished)
        ended as ProgressionResult.MatchFinished
        assertEquals(MatchFinishReason.BANKRUPTCY, ended.reason)
        assertEquals(listOf(2, 3, 1, 0), ended.rankings.map { it.seat })
        val strict = MatchProgression(MatchConfig(bankruptcyInclusive = false).toRuleConfig())
        assertTrue(strict.nextRound(0, 1, 0, 0, listOf(0, 20000, 40000, 40000), RoundOutcome.NonDealerWin(1)) is ProgressionResult.NextRound)
    }

    @Test fun `cheat resource charge retries once and rejects conflicts and insufficient balance atomically`() {
        val ledger = CheatLedger(CheatEconomyConfig(enabled = true, initialPoints = listOf(10, 20, 30, 40)))
        val charge = ledger.apply("cost-1", 0, -7, "skill-cost")
        assertEquals(listOf(3, 20, 30, 40), ledger.balances())
        assertEquals(charge, ledger.apply("cost-1", 0, -7, "skill-cost"))
        assertThrows(IllegalArgumentException::class.java) { ledger.apply("cost-1", 0, -1, "skill-cost") }
        assertThrows(RuntimeException::class.java) { ledger.apply("cost-2", 0, -4, "skill-cost") }
        assertThrows(IllegalArgumentException::class.java) { ledger.apply("cost-3", 4, 100, "reward") }
        assertEquals(listOf(3, 20, 30, 40), ledger.balances())
        ledger.apply("reward-1", 0, 5, "round-reward")
        assertEquals(8, ledger.balances()[0])
    }

    @Test fun `resource configuration collections cannot be mutated into a live ledger`() {
        val initial = mutableListOf(10, 20, 30, 40)
        val ledger = CheatLedger(CheatEconomyConfig(enabled = true, initialPoints = initial))
        initial[0] = 999
        assertEquals(10, ledger.balances()[0])
        val before = ledger.balances()
        ledger.apply("spend", 0, -5, "cost")
        assertEquals(10, before[0])
        assertEquals(5, ledger.balances()[0])
    }
}

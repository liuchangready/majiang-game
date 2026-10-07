package com.majiang.v3

import com.majiang.application.ApplicationCoordinator
import com.majiang.state.*
import com.majiang.model.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class CheatLedgerIntegrationAcceptanceTest {
    private fun table(): ApplicationCoordinator {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit -> (2..4).map { Tile(suit, it) } } +
            (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val state = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5))
        listOf(10, 20, 30, 40).forEachIndexed { seat, amount -> state.cheatPoints[seat] = amount }
        return ApplicationCoordinator(initialState = state, autoPlayAi = false, autoRiichiDiscard = false).also {
            it.initializeMatch(List(4) { SeatController.RemoteHuman(false) })
        }
    }

    @Test fun `approved resource debit survives settlement next deal and cross-round retry without altering mahjong scores`() {
        val c = table()
        val round = c.activeRound!!
        val scores = round.readState().scores.toList()
        assertTrue(c.applyCheatLedgerEntry("skill-1", 0, -7, "skill cost") is ActionResult.Accepted)
        assertEquals(listOf(3, 20, 30, 40), round.readState().cheatPoints.toList())
        assertEquals(scores, round.readState().scores.toList())
        TestTableBuilder.assertPhysical(round.readState())
        assertTrue(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)) is ActionResult.Accepted)
        val result = round.result!!
        assertEquals(listOf(10, 20, 30, 40), result.startingCheatPoints)
        assertEquals(listOf(3, 20, 30, 40), result.finalCheatPoints)
        assertEquals(listOf(-7, 0, 0, 0), result.cheatPointDeltas)
        assertTrue(c.nextRound() is ActionResult.Accepted)
        val next = c.activeRound!!
        assertNotEquals(round.roundId, next.roundId)
        assertEquals(result.finalCheatPoints, next.readState().cheatPoints.toList())
        val version = next.version
        assertTrue(c.applyCheatLedgerEntry("skill-1", 0, -7, "skill cost") is ActionResult.Accepted)
        assertEquals(version, next.version)
        assertEquals(result.finalCheatPoints, next.readState().cheatPoints.toList())
    }

    @Test fun `invalid ledger operations leave round version resources tiles scores and published events unchanged`() {
        val c = table()
        val round = c.activeRound!!
        val events = mutableListOf<com.majiang.event.GameEvent>()
        c.subscribe { events.addAll(it) }
        val version = round.version
        val before = round.readState()
        for ((id, seat, amount) in listOf(Triple("overdraw", 0, -11), Triple("overflow", 0, Int.MAX_VALUE), Triple("seat", 4, 1))) {
            assertTrue(c.applyCheatLedgerEntry(id, seat, amount, "invalid") is ActionResult.Rejected)
            assertEquals(version, round.version)
            assertEquals(before.cheatPoints.toList(), round.readState().cheatPoints.toList())
            assertEquals(before.scores.toList(), round.readState().scores.toList())
            TestTableBuilder.assertPhysical(round.readState())
        }
        assertTrue(events.isEmpty())
    }

    @Test fun `duplicate ledger id cannot be changed and exposed state cannot mutate authority`() {
        val c = table()
        assertTrue(c.applyCheatLedgerEntry("reward", 2, 5, "round achievement") is ActionResult.Accepted)
        val round = c.activeRound!!
        val version = round.version
        assertTrue(c.applyCheatLedgerEntry("reward", 2, 6, "round achievement") is ActionResult.Rejected)
        assertEquals(version, round.version)
        round.readState().cheatPoints[2] = 999
        assertEquals(35, round.readState().cheatPoints[2])
    }

    @Test fun `final session result includes resource balance and income spending summary separate from rankings`() {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit -> (2..4).map { Tile(suit, it) } } +
            (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val state = TestTableBuilder.create(hands = mapOf(1 to hand), drawn = 1 to Tile(Suit.MAN, 5), dealer = 0)
        listOf(10, 20, 30, 40).forEachIndexed { seat, amount -> state.cheatPoints[seat] = amount }
        val config = com.majiang.match.MatchConfig(handsPerWind = 1, winds = listOf(1), allowExtension = false)
        val c = ApplicationCoordinator(initialState = state,
            matchProgression = com.majiang.match.MatchProgression(config.toRuleConfig()),
            autoPlayAi = false, autoRiichiDiscard = false)
        c.initializeMatch(List(4) { SeatController.RemoteHuman(false) })
        assertTrue(c.applyCheatLedgerEntry("spend", 0, -7, "skill") is ActionResult.Accepted)
        assertTrue(c.applyCheatLedgerEntry("earn", 2, 5, "achievement") is ActionResult.Accepted)
        assertTrue(c.submitPlayerAction(1, GameAction.DeclareTsumo(1)) is ActionResult.Accepted)
        val result = c.session.result!!
        assertEquals(listOf(3, 20, 35, 40), result.finalCheatPoints)
        assertEquals(7, result.cheatSummary!!.seatStatistics[0].totalSpent)
        assertEquals(5, result.cheatSummary!!.seatStatistics[2].totalGained)
        assertEquals(1, result.rankings.first().seat)
        assertEquals(100000, result.finalScores.sum())
    }
}

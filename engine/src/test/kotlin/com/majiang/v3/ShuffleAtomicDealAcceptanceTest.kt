package com.majiang.v3

import com.majiang.application.ApplicationCoordinator
import com.majiang.model.TilePool
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ShuffleAtomicDealAcceptanceTest {
    @Test fun `invalid physical wall or insufficient points installs neither fee nor partial deal`() {
        val initial = GameState().apply { cheatPoints.fill(50) }
        val c = ApplicationCoordinator(initialState = initial, autoPlayAi = false, shuffleConfig = ShuffleConfig(enabled = true))
        val round = c.activeRound!!
        val version = round.version
        val good = TilePool.buildStandardSet()
        val broken = good.toMutableList().apply { this[135] = this[0] }
        assertTrue(c.executeDealWithPreparation(round.roundId, broken, mapOf(0 to 40)) is ActionResult.Rejected)
        assertTrue(c.executeDealWithPreparation(round.roundId, good, mapOf(0 to 40, 1 to 60)) is ActionResult.Rejected)
        assertEquals(version, round.version)
        assertEquals(listOf(50, 50, 50, 50), round.readState().cheatPoints.toList())
        assertTrue(round.readState().cheatEntries.isEmpty())
        assertTrue(round.readState().wall.isEmpty())
        assertTrue(round.readState().players.all { it.closedTiles.isEmpty() })
        assertTrue(c.executeDealWithPreparation(round.roundId, good, mapOf(0 to 40)) is ActionResult.Accepted)
        val state = round.readState()
        assertEquals(10, state.cheatPoints[0])
        assertEquals(-40, state.cheatEntries.single().amount, "Deal reset must not erase its own fee ledger")
        assertEquals(50, round.frozenStartingCheatPoints[0])
        assertTrue(c.executeDealWithPreparation(round.roundId, good, mapOf(0 to 40)) is ActionResult.Rejected)
        assertEquals(10, round.readState().cheatPoints[0])
        StateInvariants.assertValid(state)
    }
}

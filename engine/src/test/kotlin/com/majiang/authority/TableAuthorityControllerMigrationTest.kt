package com.majiang.authority

import com.majiang.access.ReceiptStatus
import com.majiang.ai.TrivialAIStrategy
import com.majiang.application.ActionChoice
import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEventBus
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.state.GameAction
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** V4 authority coverage retained from the old LocalGameSession controller tests. */
class TableAuthorityControllerMigrationTest {
    private fun humans(): MutableList<SeatController> = mutableListOf(
        SeatController.LocalHuman(false),
        SeatController.RemoteHuman(false),
        SeatController.RemoteHuman(false),
        SeatController.RemoteHuman(false)
    )

    @Test
    fun `start preserves four explicit human controllers and does not execute their first turn`() {
        TableAuthority(controllers = humans()).use { authority ->
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, authority.clock.nowMillis()).receipt.status)
            assertEquals(humans(), (0..3).map(authority::controllerFor))
            assertTrue(authority.coordinator.activeRound!!.readState().players.all { it.discards.isEmpty() })
            assertTrue(authority.queryDecision(0, false).options.any { it.action is GameAction.DiscardTile })
            TestTableBuilder.assertPhysical(authority.coordinator.activeRound!!.readState())
        }
    }

    @Test
    fun `authority start preserves a coordinator configured before composition`() {
        val coordinator = ApplicationCoordinator.createForTest(GameState(), GameEventBus(), autoPlayAi = true)
        val configured = humans()
        coordinator.initializeMatch(configured)
        TableAuthority(initialCoordinator = coordinator).use { authority ->
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, authority.clock.nowMillis()).receipt.status)
            assertEquals(configured, (0..3).map(authority::controllerFor))
            assertTrue(coordinator.activeRound!!.readState().players.all { it.discards.isEmpty() })
            TestTableBuilder.assertPhysical(coordinator.activeRound!!.readState())
        }
    }

    @Test
    fun `explicit AI strategy and nonzero human seat survive authority start`() {
        val strategy = TrivialAIStrategy()
        val configured = listOf(
            SeatController.Computer(), SeatController.Computer(strategy),
            SeatController.LocalHuman(), SeatController.Computer()
        )
        TableAuthority(controllers = configured, adminSeat = 2).use { authority ->
            assertEquals(ReceiptStatus.APPLIED, authority.start(2, authority.clock.nowMillis()).receipt.status)
            assertSame(strategy, (authority.controllerFor(1) as SeatController.Computer).strategy)
            assertTrue(authority.controllerFor(2) is SeatController.LocalHuman)
            assertTrue(listOf(0, 1, 3).all { authority.controllerFor(it) is SeatController.Computer })
        }
    }

    @Test
    fun `preconfigured AI strategy is not replaced when authority starts`() {
        val strategy = TrivialAIStrategy()
        val coordinator = ApplicationCoordinator.createForTest(GameState(), GameEventBus(), autoPlayAi = true)
        coordinator.initializeMatch(listOf(
            SeatController.LocalHuman(), SeatController.Computer(strategy),
            SeatController.Computer(), SeatController.Computer()
        ))
        TableAuthority(initialCoordinator = coordinator).use { authority ->
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, authority.clock.nowMillis()).receipt.status)
            assertSame(strategy, (authority.controllerFor(1) as SeatController.Computer).strategy)
        }
    }

    @Test
    fun `controller input list is detached and prestart seat assignment is not reset`() {
        val configured = humans()
        TableAuthority(controllers = configured).use { authority ->
            configured[1] = SeatController.Computer()
            assertEquals(SeatController.RemoteHuman(false), authority.controllers[1])
            authority.setSeatController(3, SeatController.RemoteHuman(false))
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, authority.clock.nowMillis()).receipt.status)
            assertEquals(SeatController.RemoteHuman(false), authority.controllerFor(1))
            assertEquals(SeatController.RemoteHuman(false), authority.controllerFor(3))
            assertEquals(0, authority.coordinator.activeRound!!.readState().currentPlayer)
        }
    }

    @Test
    fun `takeover hides candidate and rejects stale command without changing physical hand`() {
        val state = TestTableBuilder.create(drawn = 1 to Tile(Suit.WIND, 4))
        TableAuthority(initialState = state, controllers = humans()).use { authority ->
            val initial = authority.queryDecision(1, false)
            val option = initial.options.first { it.action is GameAction.DiscardTile }
            val old = ActionChoice("stale-human", initial.roundId, initial.decisionId,
                initial.controlEpoch, option.optionId)
            val tiles = authority.queryView(1).myAllClosedTiles

            authority.setSeatController(1, SeatController.Takeover())
            assertTrue(authority.queryDecision(1, true).options.isEmpty())
            assertEquals(tiles, authority.queryView(1).myAllClosedTiles)
            assertEquals(ReceiptStatus.REJECTED,
                authority.submitChoice(1, old, authority.clock.nowMillis()).receipt.status)

            authority.setSeatController(1, SeatController.RemoteHuman(false))
            val renewed = authority.queryDecision(1, false)
            val fresh = renewed.options.first { it.action is GameAction.DiscardTile }
            assertNotEquals(initial.controlEpoch, renewed.controlEpoch)
            assertNotEquals(ReceiptStatus.REJECTED, authority.submitChoice(1,
                ActionChoice("fresh-human", renewed.roundId, renewed.decisionId,
                    renewed.controlEpoch, fresh.optionId), authority.clock.nowMillis()).receipt.status)
            TestTableBuilder.assertPhysical(authority.coordinator.activeRound!!.readState())
        }
    }
}

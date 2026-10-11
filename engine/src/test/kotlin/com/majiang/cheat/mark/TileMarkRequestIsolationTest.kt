package com.majiang.cheat.mark

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.authority.TableAuthority
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.state.GameAction
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/** Codex regression: request IDs are scoped by seat; ledger and group IDs must preserve that scope. */
@Timeout(30)
class TileMarkRequestIsolationTest {
    @Test
    fun `two seats may independently mark using the same client request id`() {
        val state = TestTableBuilder.create().also { it.cheatPoints.fill(40) }
        val controls = List(4) { SeatControlView(it, true, "HUMAN", 0L) }
        val clock = object : MonotonicClock { override fun nowMillis() = 1_000L }
        TableAuthority(
            config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to 10)),
                tileMarkConfig = TileMarkConfig(true)
            ),
            clock = clock,
            controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = state
        ).use { a ->
            fun cast(seat: Int) {
                val snapshot = a.querySnapshot(seat, "test-sync", 0L, controls)
                val ids = (snapshot.view.myClosedTiles + listOfNotNull(snapshot.view.myDrawnTile)).take(4).map { it.tileId }
                val request = TileMarkRequest("same-client-id", snapshot.sessionId, snapshot.roundId!!,
                    snapshot.decision.decisionId, snapshot.decision.controlEpoch, ids)
                val result = a.submitTileMark(seat, request, clock.nowMillis())
                assertEquals(ReceiptStatus.APPLIED, result.receipt.status, result.receipt.toString())
            }
            a.syncDecisionsAndClocks(clock.nowMillis(), emptySet())
            cast(0)
            var secondSeat: Int? = null
            for (step in 0 until 20) {
                a.coordinator.advanceAutomaticActions()
                a.syncDecisionsAndClocks(clock.nowMillis(), emptySet())
                secondSeat = (1..3).firstOrNull { a.querySnapshot(it, "test-sync", 0L, controls).tileMark!!.canApply }
                if (secondSeat != null) break
                var acted = false
                for (seat in 0..3) {
                    val decision = a.queryDecision(seat, false)
                    val option = decision.options.firstOrNull { it.action is GameAction.Pass }
                        ?: decision.options.firstOrNull { it.action is GameAction.DiscardTile }
                        ?: continue
                    val result = a.submitChoice(seat, ActionChoice("advance-$step-$seat", decision.roundId,
                        decision.decisionId, decision.controlEpoch, option.optionId), clock.nowMillis())
                    assertTrue(result.receipt.status in setOf(ReceiptStatus.APPLIED, ReceiptStatus.RECORDED))
                    acted = true
                    break
                }
                assertTrue(acted, "Could not advance to another player's discard turn")
            }
            assertNotNull(secondSeat, "Expected another player's discard turn")
            val targetSeat = requireNotNull(secondSeat)
            cast(targetSeat)
            val groups = a.coordinator.session.tileMarkState.groups
            assertEquals(setOf(0, targetSeat), groups.map { it.ownerSeat }.toSet())
            assertEquals(2, groups.map { it.groupId }.distinct().size)
            val committed = a.coordinator.activeRound!!.readState()
            assertEquals(30, committed.cheatPoints[0])
            assertEquals(30, committed.cheatPoints[targetSeat])
            val entries = committed.cheatEntries.filter { it.reason == "TILE_MARK" }
            assertEquals(2, entries.size)
            assertEquals(2, entries.map { it.entryId }.distinct().size)
            TestTableBuilder.assertPhysical(committed)
        }
    }
}

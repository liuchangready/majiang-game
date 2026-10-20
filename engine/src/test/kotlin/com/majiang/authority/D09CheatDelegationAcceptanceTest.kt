package com.majiang.authority

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.cheat.beam.BeamSwapAction
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.cheat.beam.BeamSwapConfig
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomConfig
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.bloom.SleeveBloomKeep
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TilePool
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.state.StateInvariants
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/**
 * D-09 行为等价回归：全部通过真实 TableAuthority 公共入口与物理牌局状态验证，
 * 不以“调用是否转到 Handler”这类委托镜像断言替代业务结果。
 */
@Timeout(60)
class D09CheatDelegationAcceptanceTest {

    private class Clock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private val humans = List(4) { SeatController.RemoteHuman(false) }
    private fun controls(a: TableAuthority) = List(4) {
        SeatControlView(it, true, "HUMAN", a.controlEpochFor(it))
    }

    private fun config(points: Int, shuffle: Boolean = false) = MatchConfig(
        cheatEconomy = CheatEconomyConfig(
            enabled = true,
            initialPoints = List(4) { points },
            costs = mapOf(
                "TILE_MARK" to 10,
                "SLEEVE_BLOOM" to 10,
                "BEAM_SWAP" to 10,
                "shuffle_target" to 30
            )
        ),
        shuffleConfig = ShuffleConfig(enabled = shuffle, selectionMillis = 10_000L),
        tileMarkConfig = TileMarkConfig(true),
        sleeveBloomConfig = SleeveBloomConfig(true),
        beamSwapConfig = BeamSwapConfig(true)
    )

    private fun normalState(points: Int = 40): GameState = TestTableBuilder.create(
        drawn = 0 to Tile(Suit.SOU, 9, 3),
        wallPrefix = listOf(Tile(Suit.PIN, 9, 3)),
        phase = GamePhase.CHEAT_WINDOW
    ).also {
        it.lastDrawKind = SleeveBloomDrawKind.NORMAL
        it.cheatPoints.fill(points)
    }

    private fun markTable(points: Int = 40): TableAuthority = TableAuthority(
        config = config(points),
        clock = Clock(),
        controllers = humans,
        initialState = normalState(points)
    ).also { it.syncDecisionsAndClocks(it.clock.nowMillis(), emptySet()) }

    private fun snapshot(a: TableAuthority, seat: Int = 0) =
        a.querySnapshot(seat, "d09", 0L, controls(a))

    private fun markRequest(a: TableAuthority, id: String, seat: Int = 0): TileMarkRequest {
        val s = snapshot(a, seat)
        val ids = (s.view.myClosedTiles + listOfNotNull(s.view.myDrawnTile)).take(4).map { it.tileId }
        return TileMarkRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch, ids)
    }

    private fun bloomRequest(a: TableAuthority, id: String, seat: Int = 0): SleeveBloomRequest {
        val s = snapshot(a, seat)
        return SleeveBloomRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch)
    }

    private fun beamRequest(a: TableAuthority, id: String, seat: Int = 0): BeamSwapRequest {
        val s = snapshot(a, seat)
        return BeamSwapRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch)
    }

    private fun bloomChoice(a: TableAuthority, id: String, keep: SleeveBloomKeep): SleeveBloomChoiceRequest {
        val s = snapshot(a)
        return SleeveBloomChoiceRequest(
            id, s.sessionId, s.roundId!!, s.decision.decisionId,
            s.decision.controlEpoch, s.sleeveBloom!!.pendingOffer!!.offerId, keep
        )
    }

    private fun beamChoice(a: TableAuthority, id: String, swap: Boolean): BeamSwapChoiceRequest {
        val s = snapshot(a)
        val offer = s.beamSwap!!.pendingOffer!!
        return BeamSwapChoiceRequest(
            id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch,
            offer.offerId,
            if (swap) BeamSwapAction.SWAP else BeamSwapAction.KEEP_ORIGINAL,
            if (swap) offer.selectableHandTileIds.first() else null,
            if (swap) offer.candidates.first().slot else null
        )
    }

    private fun discardChoice(a: TableAuthority, id: String): ActionChoice {
        val d = a.queryDecision(0, false)
        val option = d.options.first { it.action is GameAction.DiscardTile }
        return ActionChoice(id, d.roundId, d.decisionId, d.controlEpoch, option.optionId)
    }

    private fun assertPhysical(state: GameState) {
        TestTableBuilder.assertPhysical(state)
        StateInvariants.assertValid(state)
    }

    @Test
    fun `tile mark replay and every cross operation sharing the request id charge once`() {
        markTable().use { a ->
            val request = markRequest(a, "shared-id")
            val first = a.submitTileMark(0, request, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, first.receipt.status)
            assertEquals(30, snapshot(a).cheatPoints[0])
            assertEquals(1, snapshot(a).tileMark!!.activeGroups.size)

            val replay = a.submitTileMark(0, request.copy(tileIds = request.tileIds.reversed()), a.clock.nowMillis())
            assertEquals(first.receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(30, snapshot(a).cheatPoints[0])

            assertEquals(
                "REQUEST_ID_CONFLICT",
                a.submitSleeveBloom(0, bloomRequest(a, "shared-id"), a.clock.nowMillis()).receipt.reason
            )
            assertEquals(
                "REQUEST_ID_CONFLICT",
                a.submitBeamSwap(0, beamRequest(a, "shared-id"), a.clock.nowMillis()).receipt.reason
            )
            assertEquals(
                "REQUEST_ID_CONFLICT",
                a.submitChoice(0, discardChoice(a, "shared-id"), a.clock.nowMillis()).receipt.reason
            )
            assertEquals(30, snapshot(a).cheatPoints[0])

            for (seat in 1..3) {
                assertTrue(snapshot(a, seat).tileMark!!.activeGroups.isEmpty())
                assertTrue(snapshot(a, seat).tileMark!!.selectableTileIds.isEmpty())
            }
            assertTrue(
                a.filterPublicEvents(
                    listOf(GameEvent.CheatPointsChanged(0, 0, -10, 30))
                ).isEmpty(),
                "Private cheat ledger events must never enter the public projection"
            )
            assertPhysical(a.coordinator.activeRound!!.readState())
        }
    }

    @Test
    fun `sleeve bloom epoch change rejects old choice while reclaimed choice charges no second fee`() {
        markTable().use { a ->
            val activation = bloomRequest(a, "bloom-activate")
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, activation, a.clock.nowMillis()).receipt.status)
            assertEquals(30, snapshot(a).cheatPoints[0])
            val oldChoice = bloomChoice(a, "old-epoch-choice", SleeveBloomKeep.SECOND)

            a.setSeatController(0, SeatController.Takeover())
            a.syncDecisionsAndClocks(a.clock.nowMillis(), setOf(0))
            assertEquals(ReceiptStatus.REJECTED, a.submitSleeveBloomChoice(0, oldChoice, a.clock.nowMillis()).receipt.status)
            assertNotNull(snapshot(a).sleeveBloom!!.pendingOffer)
            assertEquals(30, snapshot(a).cheatPoints[0])

            a.setSeatController(0, SeatController.RemoteHuman(false))
            a.syncDecisionsAndClocks(a.clock.nowMillis(), emptySet())
            val freshChoice = bloomChoice(a, "fresh-choice", SleeveBloomKeep.SECOND)
            assertNotEquals(oldChoice.controlEpoch, freshChoice.controlEpoch)
            val applied = a.submitSleeveBloomChoice(0, freshChoice, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, applied.receipt.status)
            assertEquals(30, snapshot(a).cheatPoints[0])
            assertNull(snapshot(a).sleeveBloom!!.pendingOffer)
            assertPhysical(a.coordinator.activeRound!!.readState())
        }

        markTable().use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, bloomRequest(a, "stale"), a.clock.nowMillis()).receipt.status)
            val request = bloomChoice(a, "stale-choice", SleeveBloomKeep.SECOND)
            val live = a.coordinator.activeRound!!.state
            val index = live.wallPointer
            val old = live.wall[index]
            live.wall[index] = live.wall[index + 1]
            live.wall[index + 1] = old
            val before = TilePool.buildStandardSet().associate { it.tileId to live.tilePool.getLocation(it) }
            val result = a.submitSleeveBloomChoice(0, request, a.clock.nowMillis())
            assertEquals("SLEEVE_BLOOM_STALE_OFFER", result.receipt.reason)
            assertTrue(result.stateChanged)
            assertNull(snapshot(a).sleeveBloom!!.pendingOffer)
            assertEquals(30, snapshot(a).cheatPoints[0])
            assertEquals(before, TilePool.buildStandardSet().associate { it.tileId to a.coordinator.activeRound!!.readState().tilePool.getLocation(it) })
        }
    }

    @Test
    fun `beam swap accepted before deadline clears the offer while the separate timeout path resolves once`() {
        markTable().use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.submitBeamSwap(0, beamRequest(a, "beam"), a.clock.nowMillis()).receipt.status)
            val drawn = a.coordinator.activeRound!!.readState().players[0].drawnTile!!
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            (a.clock as Clock).now = deadline - 1
            val accepted = a.submitBeamSwapChoice(0, beamChoice(a, "beam-choice", swap = true), a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, accepted.receipt.status)
            assertEquals(30, snapshot(a).cheatPoints[0])
            assertNull(snapshot(a).beamSwap!!.pendingOffer)
            assertFalse(a.coordinator.activeRound!!.readState().players[0].discardHistory.contains(drawn))
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            assertPhysical(a.coordinator.activeRound!!.readState())
        }

        markTable().use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.submitBeamSwap(0, beamRequest(a, "beam-timeout"), a.clock.nowMillis()).receipt.status)
            val drawn = a.coordinator.activeRound!!.readState().players[0].drawnTile!!
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            (a.clock as Clock).now = deadline
            assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwapChoice(0, beamChoice(a, "late", swap = false), deadline).receipt.status)
            assertNotNull(a.checkTimeout(deadline))
            assertNull(snapshot(a).beamSwap!!.pendingOffer)
            assertTrue(a.coordinator.activeRound!!.readState().players[0].discardHistory.contains(drawn))
            assertEquals(30, snapshot(a).cheatPoints[0])
            assertNull(a.checkTimeout(deadline))
        }
    }

    @Test
    fun `shuffle preparation keeps identity deadline pause and automatic deal semantics`() {
        val clock = Clock()
        TableAuthority(
            config = config(100, shuffle = true),
            clock = clock,
            controllers = humans,
            adminSeat = 0
        ).use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status)
            assertTrue(a.isPreparationActive())
            val view = a.buildPreparationViewForSeat(0)!!
            val roundId = view.roundId
            val plan = ShufflePlanRequest(
                requestId = "prep-1",
                roundId = roundId,
                controlEpoch = a.controlEpochFor(0),
                selection = ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN)
            )
            assertEquals(
                "STALE_EPOCH",
                a.submitShufflePlan(0, plan.copy(controlEpoch = plan.controlEpoch + 1), clock.now).receipt.reason
            )
            val recorded = a.submitShufflePlan(0, plan, clock.now)
            assertEquals(ReceiptStatus.RECORDED, recorded.receipt.status)
            assertEquals(70, a.buildPreparationViewForSeat(0)!!.availablePoints)
            assertEquals(recorded.receipt, a.submitShufflePlan(0, plan, clock.now).receipt)
            assertEquals(
                "ALREADY_CONFIRMED",
                a.submitShufflePlan(0, plan.copy(requestId = "prep-2"), clock.now).receipt.reason
            )
            assertEquals(
                "PREPARATION_EXPIRED",
                a.submitShufflePlan(
                    1,
                    ShufflePlanRequest("prep-seat1", roundId, a.controlEpochFor(1), ShuffleSelection()),
                    view.deadlineMillis
                ).receipt.reason
            )

            val deadlineBeforePause = a.preparationDeadline()
            clock.now = 2_000L
            a.pause(clock.now)
            clock.now = 7_000L
            a.resume(clock.now)
            assertEquals(deadlineBeforePause + 5_000L, a.preparationDeadline())
            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status, "Already started preparation remains idempotent")

            clock.now = a.preparationDeadline()
            val dealt = a.checkPreparationStep(clock.now, setOf(0, 1, 2, 3), emptySet())
            assertNotNull(dealt)
            assertTrue(dealt is TableAuthority.AutoStepResult.PreparationDealt)
            assertFalse(a.isPreparationActive())
            assertEquals(70, snapshot(a).cheatPoints[0])
            assertEquals(100, snapshot(a).cheatPoints[1])
            assertEquals(1, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            assertPhysical(a.coordinator.activeRound!!.readState())
            assertEquals(
                "WRONG_PHASE",
                a.submitShufflePlan(
                    1,
                    ShufflePlanRequest("late-old-round", roundId, a.controlEpochFor(1), ShuffleSelection()),
                    clock.now
                ).receipt.reason
            )
        }
    }

    @Test
    fun `closing authority cancels preparation and pending cheat operations without late side effects`() {
        val clock = Clock()
        val authority = TableAuthority(
            config = config(100, shuffle = true),
            clock = clock,
            controllers = humans,
            adminSeat = 0
        )
        authority.start(0, clock.now)
        val prepRoundId = authority.buildPreparationViewForSeat(0)!!.roundId
        val liveSessionId = authority.sessionId
        val plan = ShufflePlanRequest(
            "prep-close",
            prepRoundId,
            authority.controlEpochFor(0),
            ShuffleSelection(target = ShuffleTarget.DAISANGEN)
        )
        assertEquals(ReceiptStatus.RECORDED, authority.submitShufflePlan(0, plan, clock.now).receipt.status)
        val before = authority.coordinator.activeRound!!.readState().cheatPoints.toList()
        authority.close()
        assertEquals(0, authority.getSeatReceiptCount(0), "Close must clear the single receipt namespace")

        assertEquals("SESSION_CLOSED", authority.submitShufflePlan(0, plan, clock.now).receipt.reason)
        assertEquals(
            "SESSION_CLOSED",
            authority.submitTileMark(0, TileMarkRequest("late", "s", "r", "d", 0, listOf(0, 1, 2, 3)), clock.now).receipt.reason
        )
        assertEquals(
            "SESSION_CLOSED",
            authority.submitSleeveBloom(0, SleeveBloomRequest("late-bloom", liveSessionId, prepRoundId, "d", 0), clock.now).receipt.reason
        )
        assertEquals(before, authority.coordinator.queryCheatPoints())
    }
}

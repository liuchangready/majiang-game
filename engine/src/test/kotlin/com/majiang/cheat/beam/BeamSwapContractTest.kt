package com.majiang.cheat.beam

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.authority.TableAuthority
import com.majiang.cheat.bloom.*
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.*
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows

/** Codex-owned authority contracts; implementation must not rewrite assertions. */
@Timeout(40)
class BeamSwapContractTest {
    private class Clock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis() = now
    }
    private fun normal() = TestTableBuilder.create(
        drawn = 0 to Tile(Suit.SOU, 9, 3),
        wallPrefix = listOf(Tile(Suit.PIN, 9, 3)), phase = GamePhase.CHEAT_WINDOW
    ).also { it.lastDrawKind = SleeveBloomDrawKind.NORMAL }
    private fun config(points: Int = 40, enabled: Boolean = true) = MatchConfig(
        cheatEconomy = CheatEconomyConfig(true, List(4) { points },
            mapOf("BEAM_SWAP" to 10, "SLEEVE_BLOOM" to 10, "TILE_MARK" to 10)),
        beamSwapConfig = BeamSwapConfig(enabled), sleeveBloomConfig = SleeveBloomConfig(true),
        tileMarkConfig = TileMarkConfig(true)
    )
    private fun table(s: GameState = normal(), points: Int = 40, enabled: Boolean = true): TableAuthority {
        s.cheatPoints.fill(points)
        return TableAuthority(config = config(points, enabled), clock = Clock(),
            controllers = List(4) { SeatController.RemoteHuman(false) }, initialState = s)
    }
    private fun snap(a: TableAuthority, seat: Int = 0) = a.querySnapshot(seat, "beam-test", 0L,
        List(4) { SeatControlView(it, true, if (a.controllerFor(it) is SeatController.Takeover) "TAKEOVER" else "HUMAN", a.controlEpochFor(it)) })
    private fun state(a: TableAuthority) = a.coordinator.activeRound!!.readState()
    private fun req(a: TableAuthority, id: String = "activate", seat: Int = 0): BeamSwapRequest {
        val s = snap(a, seat)
        return BeamSwapRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch)
    }
    private fun choose(a: TableAuthority, swap: Boolean = true, id: String = "choose"): BeamSwapChoiceRequest {
        val s = snap(a)
        val o = s.beamSwap!!.pendingOffer!!
        return BeamSwapChoiceRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId,
            s.decision.controlEpoch, o.offerId, if (swap) BeamSwapAction.SWAP else BeamSwapAction.KEEP_ORIGINAL,
            if (swap) o.selectableHandTileIds.first() else null, if (swap) o.candidates.first().slot else null)
    }
    private fun bloom(a: TableAuthority, id: String = "bloom"): SleeveBloomRequest {
        val s = snap(a)
        return SleeveBloomRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch)
    }
    private fun activate(a: TableAuthority) = a.submitBeamSwap(0, req(a), a.clock.nowMillis()).also {
        assertEquals(ReceiptStatus.APPLIED, it.receipt.status, it.receipt.reason)
    }
    private fun locations(s: GameState) = TilePool.buildStandardSet().associate { it.tileId to s.tilePool.getLocation(it) }
    private fun physical(s: GameState) { TestTableBuilder.assertPhysical(s); StateInvariants.assertValid(s) }

    @Test fun `opt in requires enabled economy and explicit exact ten point cost`() {
        assertFalse(BeamSwapConfig().enabled)
        for (cost in listOf<Int?>(null, 0, 9, 11)) assertThrows<IllegalArgumentException> {
            config().copy(cheatEconomy = CheatEconomyConfig(true, List(4) { 40 },
                mapOf("SLEEVE_BLOOM" to 10, "TILE_MARK" to 10) + (cost?.let { mapOf("BEAM_SWAP" to it) } ?: emptyMap())))
                .validateSupported()
        }
        assertThrows<IllegalArgumentException> { config().copy(cheatEconomy = CheatEconomyConfig()).validateSupported() }
        assertDoesNotThrow { config().validateSupported() }
    }

    @Test fun `activation commits fee offer and used flag together without moving tiles or extending deadline`() {
        table(points = 10).use { a ->
            val old = snap(a)
            assertTrue(old.beamSwap!!.canApply, old.beamSwap!!.disabledReason)
            val before = state(a)
            val timing = a.decisionClock.timing(0)!!
            val observed = mutableListOf<Pair<Int, Boolean>>()
            a.coordinator.subscribe { events ->
                if (events.any { it is GameEvent.CheatPointsChanged }) observed += snap(a).let {
                    it.cheatPoints[0] to (it.beamSwap!!.pendingOffer != null)
                }
            }
            val result = activate(a)
            assertTrue(result.stateChanged)
            assertEquals(1, result.events.filterIsInstance<GameEvent.CheatPointsChanged>().size)
            assertTrue(observed.isNotEmpty())
            assertTrue(observed.all { it == (0 to true) }, "Fee and offer must publish atomically: $observed")
            val after = state(a)
            assertEquals(after.turnNumber, after.beamSwapUsedOnTurn)
            assertEquals(locations(before), locations(after))
            assertEquals(before.wall, after.wall)
            assertEquals(before.turnNumber, after.turnNumber)
            assertEquals(before.scores.toList(), after.scores.toList())
            assertNull(old.beamSwap!!.pendingOffer)
            for (seat in 1..3) assertNull(snap(a, seat).beamSwap!!.pendingOffer)
            val current = snap(a)
            val offer = current.beamSwap!!.pendingOffer!!
            assertTrue(offer.candidates.all { !it.marked && it.tile == null })
            assertEquals(current.decision.decisionId, offer.decisionId)
            assertEquals(current.decision.controlEpoch, offer.controlEpoch)
            val newTiming = a.decisionClock.timing(0)!!
            assertEquals(timing.copy(key = newTiming.key), newTiming)
            assertNotEquals(old.decision.decisionId, current.decision.decisionId)
            physical(after)
        }
    }

    @Test fun `swap refreshes legal discards invalidates old options and preserves drawn slot and clocks`() {
        table(points = 10).use { a ->
            val before = state(a)
            val hand = before.players[0].closedTiles.first()
            val target = before.wall[before.wallPointer]
            val d = a.queryDecision(0, false)
            val old = ActionChoice("old-discard", d.roundId, d.decisionId, d.controlEpoch,
                d.options.first { (it.action as? GameAction.DiscardTile)?.tile == hand }.optionId)
            activate(a)
            val frozen = snap(a)
            val timing = a.decisionClock.timing(0)!!
            val request = choose(a).copy(handTileId = hand.tileId, targetSlot = BeamSwapSlot.WallSlot(before.wallPointer))
            val result = a.submitBeamSwapChoice(0, request, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, result.receipt.status, result.receipt.reason)
            assertTrue(result.stateChanged)
            assertTrue(result.events.none { it is GameEvent.CheatPointsChanged })
            val after = state(a)
            assertEquals(before.players[0].drawnTile, after.players[0].drawnTile)
            assertEquals(before.players[0].melds, after.players[0].melds)
            assertEquals(hand, after.wall[before.wallPointer])
            assertTrue(target in after.players[0].closedTiles)
            assertFalse(hand in after.players[0].closedTiles)
            assertEquals(before.wallPointer, after.wallPointer)
            assertEquals(before.wallEndExclusive, after.wallEndExclusive)
            assertEquals(before.wallRemaining, after.wallRemaining)
            assertEquals(before.wall.drop(before.wallPointer + 1), after.wall.drop(after.wallPointer + 1))
            assertEquals(0, after.cheatPoints[0])
            assertNull(snap(a).beamSwap!!.pendingOffer)
            assertNotNull(frozen.beamSwap!!.pendingOffer)
            val discards = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DiscardTile>()
            assertTrue(discards.any { it.tile == target })
            assertTrue(discards.none { it.tile == hand })
            assertEquals(ReceiptStatus.REJECTED, a.submitChoice(0, old, a.clock.nowMillis()).receipt.status)
            val newTiming = a.decisionClock.timing(0)!!
            assertEquals(timing.copy(key = newTiming.key), newTiming)
            physical(after)
        }
    }

    @Test fun `keep original spends opportunity blocks both cheats and never refunds`() {
        table().use { a ->
            activate(a)
            val before = state(a)
            assertEquals(ReceiptStatus.APPLIED, a.submitBeamSwapChoice(0, choose(a, false), a.clock.nowMillis()).receipt.status)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.wall, state(a).wall)
            assertNull(snap(a).beamSwap!!.pendingOffer)
            assertEquals(30, state(a).cheatPoints[0])
            assertFalse(snap(a).beamSwap!!.canApply)
            assertFalse(snap(a).sleeveBloom!!.canApply)
            assertEquals("BEAM_SWAP_ALREADY_USED_THIS_DRAW", a.submitBeamSwap(0, req(a, "again"), a.clock.nowMillis()).receipt.reason)
            assertEquals(ReceiptStatus.REJECTED, a.submitSleeveBloom(0, bloom(a), a.clock.nowMillis()).receipt.status)
        }
        table().use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, bloom(a), a.clock.nowMillis()).receipt.status)
            assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwap(0, req(a), a.clock.nowMillis()).receipt.status)
            val s = snap(a)
            val keep = SleeveBloomChoiceRequest("bloom-keep", s.sessionId, s.roundId!!, s.decision.decisionId,
                s.decision.controlEpoch, s.sleeveBloom!!.pendingOffer!!.offerId, SleeveBloomKeep.FIRST)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, keep, a.clock.nowMillis()).receipt.status)
            assertFalse(snap(a).beamSwap!!.canApply)
            assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwap(0, req(a, "after-bloom"), a.clock.nowMillis()).receipt.status)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `replay survives changed decision deadline and pause while changed payload conflicts`() {
        table().use { a ->
            val activation = req(a)
            val receipt = a.submitBeamSwap(0, activation, a.clock.nowMillis()).receipt
            val selection = choose(a)
            val selected = a.submitBeamSwapChoice(0, selection, a.clock.nowMillis()).receipt
            assertEquals(ReceiptStatus.APPLIED, selected.status)
            val after = locations(state(a))
            a.pause(a.clock.nowMillis())
            val late = Long.MAX_VALUE
            val replay = a.submitBeamSwap(0, activation, late)
            assertEquals(receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(selected, a.submitBeamSwapChoice(0, selection, late).receipt)
            assertEquals(after, locations(state(a)))
            assertEquals(30, state(a).cheatPoints[0])
            assertEquals("REQUEST_ID_CONFLICT", a.submitBeamSwapChoice(0,
                selection.copy(action = BeamSwapAction.KEEP_ORIGINAL, handTileId = null, targetSlot = null), late).receipt.reason)
            assertEquals("REQUEST_ID_CONFLICT", a.submitBeamSwap(0, activation.copy(decisionId = "changed"), late).receipt.reason)
        }
    }

    @Test fun `all operations share request id namespace`() {
        table().use { a ->
            activate(a)
            assertEquals("REQUEST_ID_CONFLICT", a.submitBeamSwapChoice(0, choose(a, false, "activate"), a.clock.nowMillis()).receipt.reason)
            assertEquals("REQUEST_ID_CONFLICT", a.submitSleeveBloom(0, bloom(a, "activate"), a.clock.nowMillis()).receipt.reason)
            val s = snap(a)
            val mark = TileMarkRequest("activate", s.sessionId, s.roundId!!, s.decision.decisionId,
                s.decision.controlEpoch, s.view.myClosedTiles.take(4).map { it.tileId })
            assertEquals("REQUEST_ID_CONFLICT", a.submitTileMark(0, mark, a.clock.nowMillis()).receipt.reason)
            val d = a.queryDecision(0, false)
            val discard = d.options.first { it.action is GameAction.DiscardTile }
            assertEquals("REQUEST_ID_CONFLICT", a.submitChoice(0, ActionChoice("activate", d.roundId,
                d.decisionId, d.controlEpoch, discard.optionId), a.clock.nowMillis()).receipt.reason)
        }
    }

    @Test fun `pending blocks ordinary actions marking and repeat activation without extra fee`() {
        table().use { a ->
            activate(a)
            val s = snap(a)
            val d = a.queryDecision(0, false)
            val discard = d.options.first { it.action is GameAction.DiscardTile }
            assertEquals("BEAM_SWAP_CHOICE_PENDING", a.submitChoice(0, ActionChoice("blocked", d.roundId,
                d.decisionId, d.controlEpoch, discard.optionId), a.clock.nowMillis()).receipt.reason)
            val mark = TileMarkRequest("mark", s.sessionId, s.roundId!!, s.decision.decisionId,
                s.decision.controlEpoch, s.view.myClosedTiles.take(4).map { it.tileId })
            assertEquals("BEAM_SWAP_CHOICE_PENDING", a.submitTileMark(0, mark, a.clock.nowMillis()).receipt.reason)
            for (action in listOf(GameAction.DeclareTsumo(0), GameAction.DeclareRiichi(0, s.view.myDrawnTile!!),
                GameAction.DeclareKan(0, s.view.myClosedTiles.take(4), MeldType.CLOSED_KAN))) {
                val rejected = a.coordinator.handleAction(action)
                assertTrue(rejected is ActionResult.Rejected)
                assertEquals(RejectionReason.BEAM_SWAP_CHOICE_PENDING, (rejected as ActionResult.Rejected).reason)
            }
            assertEquals("BEAM_SWAP_ALREADY_PENDING", a.submitBeamSwap(0, req(a, "again"), a.clock.nowMillis()).receipt.reason)
            assertNotNull(state(a).beamSwapOffer)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `wrong offer nonmember hand and foreign slot preserve valid offer and physical state`() {
        table().use { a ->
            activate(a)
            val selection = choose(a)
            val before = state(a)
            val requests = listOf(selection.copy(requestId = "wrong-offer", offerId = "wrong"),
                selection.copy(requestId = "drawn", handTileId = before.players[0].drawnTile!!.tileId),
                selection.copy(requestId = "foreign-hand", handTileId = before.players[1].closedTiles.first().tileId),
                selection.copy(requestId = "far-wall", targetSlot = BeamSwapSlot.WallSlot(before.wallPointer + 8)),
                selection.copy(requestId = "dead-wall", targetSlot = BeamSwapSlot.DeadWallSlot(0)))
            for (request in requests) {
                assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwapChoice(0, request, a.clock.nowMillis()).receipt.status)
                assertEquals(selection.offerId, snap(a).beamSwap!!.pendingOffer!!.offerId)
                assertEquals(locations(before), locations(state(a)))
                assertEquals(before.wall, state(a).wall)
            }
            assertEquals(ReceiptStatus.APPLIED, a.submitBeamSwapChoice(0, selection, a.clock.nowMillis()).receipt.status)
        }
    }

    @Test fun `actual candidate drift clears stale offer without moving discarding refunding or new budget`() {
        for (swap in listOf(true, false)) table().use { a ->
            activate(a)
            val selection = choose(a, swap)
            val live = a.coordinator.activeRound!!.state
            // Drift an UNSELECTED candidate: validation must cover the whole offered baseline.
            val i = live.wallPointer + 1
            val tmp = live.wall[i]
            live.wall[i] = live.wall[i + 1]
            live.wall[i + 1] = tmp
            val before = state(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            val result = a.submitBeamSwapChoice(0, selection, a.clock.nowMillis())
            assertEquals("BEAM_SWAP_STALE_OFFER", result.receipt.reason)
            assertTrue(result.stateChanged)
            assertNull(state(a).beamSwapOffer)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.wall, state(a).wall)
            assertEquals(before.players[0].discardHistory, state(a).players[0].discardHistory)
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(30, state(a).cheatPoints[0])
            assertFalse(snap(a).beamSwap!!.canApply)
            physical(state(a))
        }
    }

    @Test fun `exact deadline rejects choice and timeout keeps then discards once`() {
        table().use { a ->
            val drawn = state(a).players[0].drawnTile!!
            activate(a)
            val selection = choose(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            (a.clock as Clock).now = deadline
            assertEquals("DEADLINE_EXPIRED", a.submitBeamSwapChoice(0, selection, deadline).receipt.reason)
            assertNotNull(a.checkTimeout(deadline))
            assertNull(state(a).beamSwapOffer)
            assertTrue(drawn in state(a).players[0].discardHistory)
            assertEquals(30, state(a).cheatPoints[0])
            assertNull(a.checkTimeout(deadline))
            physical(state(a))
        }
    }

    @Test fun `changed draw source invalidates offer even when physical identities match`() {
        for (swap in listOf(true, false)) table().use { a ->
            activate(a)
            val request = choose(a, swap)
            a.coordinator.activeRound!!.state.lastDrawKind = SleeveBloomDrawKind.RINSHAN
            val before = state(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            val result = a.submitBeamSwapChoice(0, request, a.clock.nowMillis())
            assertEquals("BEAM_SWAP_STALE_OFFER", result.receipt.reason)
            assertTrue(result.stateChanged)
            assertNull(state(a).beamSwapOffer)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.wall, state(a).wall)
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `changed phase clears stale offer without forcing a discard`() {
        table().use { a ->
            activate(a)
            val request = choose(a, false)
            a.coordinator.activeRound!!.state.phase = GamePhase.DISCARD
            val before = state(a)
            val result = a.submitBeamSwapChoice(0, request, a.clock.nowMillis())
            assertEquals("BEAM_SWAP_STALE_OFFER", result.receipt.reason)
            assertTrue(result.stateChanged)
            assertNull(state(a).beamSwapOffer)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.players[0].discardHistory, state(a).players[0].discardHistory)
            assertEquals(GamePhase.DISCARD, state(a).phase)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `coordinator lock repeats full baseline validation rather than trusting an outer gate`() {
        for (swap in listOf(true, false)) table().use { a ->
            activate(a)
            val request = choose(a, swap)
            val live = a.coordinator.activeRound!!.state
            val i = live.wallPointer + 1
            val temp = live.wall[i]
            live.wall[i] = live.wall[i + 1]
            live.wall[i + 1] = temp
            val before = state(a)
            val result = a.coordinator.submitBeamSwapChoice(0, request)
            assertTrue(result is ActionResult.Rejected, "Coordinator must validate the whole baseline under its write lock")
            assertNull(state(a).beamSwapOffer, "Real baseline drift must clear the invalid offer")
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.wall, state(a).wall)
            assertEquals(before.players[0].discardHistory, state(a).players[0].discardHistory)
            assertEquals(30, state(a).cheatPoints[0])
            physical(state(a))
        }
    }

    @Test fun `pause and reclaim preserve pending selection and original remaining deadline`() {
        table().use { a ->
            activate(a)
            val old = choose(a)
            val clock = a.clock as Clock
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            clock.now += 100
            a.pause(clock.now)
            clock.now += 100_000
            assertNull(a.checkTimeout(clock.now))
            assertNotNull(state(a).beamSwapOffer)
            assertEquals("HOST_PAUSED", a.submitBeamSwapChoice(0, old, clock.now).receipt.reason)
            a.resume(clock.now)
            assertEquals(deadline + 100_000, a.decisionClock.timing(0)!!.deadlineMillis)
            a.setSeatController(0, SeatController.Takeover())
            a.syncDecisionsAndClocks(clock.now, setOf(0))
            assertNotNull(state(a).beamSwapOffer)
            a.setSeatController(0, SeatController.RemoteHuman(false))
            a.syncDecisionsAndClocks(clock.now, emptySet())
            val fresh = choose(a, id = "reclaimed")
            assertNotEquals(old.controlEpoch, fresh.controlEpoch)
            assertEquals(deadline + 100_000, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwapChoice(0, old.copy(requestId = "old-epoch"), clock.now).receipt.status)
            assertEquals(ReceiptStatus.APPLIED, a.submitBeamSwapChoice(0, fresh, clock.now).receipt.status)
        }
    }

    @Test fun `takeover schedules no AI while offer waits and resolves once at deadline`() {
        table().use { a ->
            val drawn = state(a).players[0].drawnTile!!
            activate(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            a.setSeatController(0, SeatController.Takeover())
            val jobs = a.syncDecisionsAndClocks(a.clock.nowMillis(), setOf(0))
            assertTrue(jobs.isEmpty(), "AI must not bypass or fight the pending private offer")
            assertNull(a.checkTimeout(deadline - 1))
            assertNotNull(state(a).beamSwapOffer)
            (a.clock as Clock).now = deadline
            assertNotNull(a.checkTimeout(deadline))
            assertNull(state(a).beamSwapOffer)
            assertTrue(drawn in state(a).players[0].discardHistory)
            assertEquals(30, state(a).cheatPoints[0])
            physical(state(a))
        }
    }

    @Test fun `low funds feature disabled and illegal turn fail without fee or offer`() {
        table(points = 9).use { a ->
            val request = req(a)
            val result = a.submitBeamSwap(0, request, a.clock.nowMillis())
            assertEquals("INSUFFICIENT_FUNDS", result.receipt.reason)
            assertEquals(result.receipt, a.submitBeamSwap(0, request, Long.MAX_VALUE).receipt)
            assertEquals(9, state(a).cheatPoints[0])
            assertNull(state(a).beamSwapOffer)
        }
        table(enabled = false).use { a ->
            assertNull(snap(a).beamSwap)
            assertEquals("BEAM_SWAP_DISABLED", a.submitBeamSwap(0, req(a), a.clock.nowMillis()).receipt.reason)
        }
        val cases = listOf(normal().also { it.lastDrawKind = SleeveBloomDrawKind.DEAL_EXTRA; it.turnNumber = 0 },
            normal().also { it.riichiDeclared[0] = true }, normal().also { it.phase = GamePhase.DISCARD },
            TestTableBuilder.create(phase = GamePhase.CHEAT_WINDOW).also { it.lastDrawKind = SleeveBloomDrawKind.NORMAL })
        for (s in cases) table(s).use { a ->
            assertFalse(snap(a).beamSwap!!.canApply)
            assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwap(0, req(a), a.clock.nowMillis()).receipt.status)
            assertNull(state(a).beamSwapOffer)
            assertEquals(40, state(a).cheatPoints[0])
        }
        table().use { a ->
            assertEquals(ReceiptStatus.REJECTED, a.submitBeamSwap(1, req(a, seat = 1), a.clock.nowMillis()).receipt.status)
        }
    }

    @Test fun `missing clock and exact activation deadline cannot open a choice`() {
        table().use { a ->
            val request = req(a)
            val timing = a.decisionClock.timing(0)!!
            assertEquals("DEADLINE_EXPIRED", a.submitBeamSwap(0, request, timing.deadlineMillis).receipt.reason)
            a.decisionClock.cancel(timing.key, a.clock.nowMillis())
            assertFalse(snap(a).beamSwap!!.canApply)
            assertEquals("NO_ACTIVE_DECISION", a.submitBeamSwap(0, request.copy(requestId = "no-clock"), a.clock.nowMillis()).receipt.reason)
            assertNull(state(a).beamSwapOffer)
            assertEquals(40, state(a).cheatPoints[0])
        }
    }

    @Test fun `enabled beam projects safely before the wall is dealt`() {
        table(GameState()).use { a ->
            for (seat in 0..3) {
                val before = snap(a, seat)
                assertFalse(before.beamSwap!!.canApply)
                assertNull(before.beamSwap!!.pendingOffer)
            }
        }
    }

    @Test fun `private snapshot cross checks original hand decision and viewer`() {
        table().use { a ->
            activate(a)
            val s = snap(a)
            val view = s.beamSwap!!
            val offer = view.pendingOffer!!
            assertThrows<IllegalArgumentException> {
                s.copy(beamSwap = view.copy(pendingOffer = offer.copy(selectableHandTileIds = listOf(s.view.myDrawnTile!!.tileId))))
            }
            assertThrows<IllegalArgumentException> {
                s.copy(beamSwap = view.copy(pendingOffer = offer.copy(selectableHandTileIds = listOf(state(a).players[1].closedTiles.first().tileId))))
            }
            assertThrows<IllegalArgumentException> { s.copy(beamSwap = view.copy(pendingOffer = offer.copy(decisionId = "foreign"))) }
            assertThrows<IllegalArgumentException> { snap(a, 1).copy(beamSwap = view) }
            val candidates = offer.candidates.toMutableList()
            val hand = offer.selectableHandTileIds.toMutableList()
            val copied = offer.copy(candidates = candidates, selectableHandTileIds = hand)
            candidates.clear(); hand.clear()
            assertEquals(offer, copied)
            assertThrows<UnsupportedOperationException> { (copied.candidates as MutableList).clear() }
            assertThrows<UnsupportedOperationException> { (copied.selectableHandTileIds as MutableList).clear() }
        }
    }

    @Test fun `request identities and branch structure cannot be malformed`() {
        val request = BeamSwapRequest("id", "session", "round", "decision", 0)
        for (invalid in listOf<() -> Unit>({ request.copy(requestId = " ") }, { request.copy(sessionId = "") },
            { request.copy(roundId = "") }, { request.copy(decisionId = "") }, { request.copy(controlEpoch = -1) }))
            assertThrows<IllegalArgumentException> { invalid() }
        val keep = BeamSwapChoiceRequest("choice", "session", "round", "decision", 0, "offer", BeamSwapAction.KEEP_ORIGINAL)
        assertThrows<IllegalArgumentException> { keep.copy(offerId = "") }
        assertThrows<IllegalArgumentException> { keep.copy(handTileId = 0) }
        assertThrows<IllegalArgumentException> { keep.copy(targetSlot = BeamSwapSlot.WallSlot(0)) }
        assertThrows<IllegalArgumentException> { keep.copy(action = BeamSwapAction.SWAP) }
    }
}

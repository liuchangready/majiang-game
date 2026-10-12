package com.majiang.cheat.bloom

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.authority.TableAuthority
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

/** Codex-owned behavioral contracts. Anti may run but must not edit these assertions. */
@Timeout(40)
class SleeveBloomContractTest {
    private class Clock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis() = now
    }
    private fun normal() = TestTableBuilder.create(
        drawn = 0 to Tile(Suit.SOU, 9, 3),
        wallPrefix = listOf(Tile(Suit.PIN, 9, 3)),
        phase = GamePhase.CHEAT_WINDOW
    ).also { it.lastDrawKind = SleeveBloomDrawKind.NORMAL }

    private fun config(points: Int = 40, enabled: Boolean = true) = MatchConfig(
        cheatEconomy = CheatEconomyConfig(true, List(4) { points }, mapOf("SLEEVE_BLOOM" to 10, "TILE_MARK" to 10)),
        sleeveBloomConfig = SleeveBloomConfig(enabled), tileMarkConfig = TileMarkConfig(true)
    )
    private fun table(state: GameState = normal(), points: Int = 40, enabled: Boolean = true): TableAuthority {
        state.cheatPoints.fill(points)
        return TableAuthority(config = config(points, enabled), clock = Clock(),
            controllers = List(4) { SeatController.RemoteHuman(false) }, initialState = state)
    }
    private fun snap(a: TableAuthority, seat: Int = 0) = a.querySnapshot(seat, "bloom-test", 0L,
        List(4) { SeatControlView(it, true, if (a.controllerFor(it) is SeatController.Takeover) "TAKEOVER" else "HUMAN", a.controlEpochFor(it)) })
    private fun state(a: TableAuthority) = a.coordinator.activeRound!!.readState()
    private fun req(a: TableAuthority, id: String = "activate", seat: Int = 0): SleeveBloomRequest {
        val s = snap(a, seat)
        return SleeveBloomRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch)
    }
    private fun choose(a: TableAuthority, keep: SleeveBloomKeep, id: String = "choose"): SleeveBloomChoiceRequest {
        val s = snap(a)
        return SleeveBloomChoiceRequest(id, s.sessionId, s.roundId!!, s.decision.decisionId,
            s.decision.controlEpoch, s.sleeveBloom!!.pendingOffer!!.offerId, keep)
    }
    private fun activate(a: TableAuthority) = a.submitSleeveBloom(0, req(a), a.clock.nowMillis()).also {
        assertEquals(ReceiptStatus.APPLIED, it.receipt.status, it.receipt.reason)
    }
    private fun locations(s: GameState) = TilePool.buildStandardSet().associate { it.tileId to s.tilePool.getLocation(it) }
    private fun physical(s: GameState) { TestTableBuilder.assertPhysical(s); StateInvariants.assertValid(s) }
    private fun action(a: TableAuthority, id: String, predicate: (GameAction) -> Boolean): ActionChoice {
        val d = a.queryDecision(0, false)
        return ActionChoice(id, d.roundId, d.decisionId, d.controlEpoch, d.options.first { predicate(it.action) }.optionId)
    }

    @Test fun `opt in requires enabled economy and explicit exact cost`() {
        assertFalse(SleeveBloomConfig().enabled)
        for (cost in listOf<Int?>(null, 0, 9, 11)) {
            assertThrows<IllegalArgumentException> {
                config().copy(cheatEconomy = CheatEconomyConfig(true, List(4) { 40 },
                    cost?.let { mapOf("SLEEVE_BLOOM" to it, "TILE_MARK" to 10) } ?: mapOf("TILE_MARK" to 10))).validateSupported()
            }
        }
        assertThrows<IllegalArgumentException> { config().copy(cheatEconomy = CheatEconomyConfig()).validateSupported() }
        assertDoesNotThrow { config().validateSupported() }
    }

    @Test fun `activation charges atomically reveals only owner and never moves a tile`() {
        table(points = 10).use { a ->
            val old = snap(a)
            assertTrue(old.sleeveBloom!!.canApply, old.sleeveBloom!!.disabledReason)
            val before = state(a)
            val clock = a.decisionClock.timing(0)!!
            val observed = mutableListOf<Pair<Int, Boolean>>()
            a.coordinator.subscribe { events ->
                if (events.any { it is GameEvent.CheatPointsChanged })
                    observed += snap(a).let { it.cheatPoints[0] to (it.sleeveBloom!!.pendingOffer != null) }
            }
            val result = activate(a)
            assertTrue(result.stateChanged)
            assertEquals(1, result.events.filterIsInstance<GameEvent.CheatPointsChanged>().size)
            assertTrue(observed.isNotEmpty())
            assertTrue(observed.all { it == (0 to true) }, "Fee published before offer: $observed")
            val after = state(a)
            val offer = snap(a).sleeveBloom!!.pendingOffer!!
            assertEquals(before.players[0].drawnTile, offer.firstTile)
            assertEquals(before.wall[before.wallPointer], offer.secondTile)
            assertEquals(locations(before), locations(after))
            assertEquals(before.wall, after.wall)
            assertEquals(before.turnNumber, after.turnNumber)
            assertEquals(before.scores.toList(), after.scores.toList())
            assertEquals(0, after.cheatPoints[0])
            assertNull(old.sleeveBloom!!.pendingOffer)
            for (seat in 1..3) assertNull(snap(a, seat).sleeveBloom!!.pendingOffer)
            val current = snap(a)
            assertEquals(current.decision.decisionId, offer.decisionId)
            assertEquals(current.decision.controlEpoch, offer.controlEpoch)
            val newClock = a.decisionClock.timing(0)!!
            assertEquals(clock.copy(key = newClock.key), newClock)
            assertNotEquals(old.decision.decisionId, current.decision.decisionId)
            physical(after)
        }
    }

    @Test fun `second choice swaps exact physical tiles without advancing wall and refreshes discards`() {
        table(points = 10).use { a ->
            val before = state(a)
            val first = before.players[0].drawnTile!!
            val second = before.wall[before.wallPointer]
            val oldAction = action(a, "old-discard") { it is GameAction.DiscardTile && it.tile == first }
            activate(a)
            val frozen = snap(a)
            val timing = a.decisionClock.timing(0)!!
            val result = a.submitSleeveBloomChoice(0, choose(a, SleeveBloomKeep.SECOND), a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, result.receipt.status)
            assertTrue(result.stateChanged)
            assertTrue(result.events.none { it is GameEvent.CheatPointsChanged })
            val after = state(a)
            assertEquals(second, after.players[0].drawnTile)
            assertEquals(first, after.wall[after.wallPointer])
            assertEquals(before.wallPointer, after.wallPointer)
            assertEquals(before.wallEndExclusive, after.wallEndExclusive)
            assertEquals(before.wallRemaining, after.wallRemaining)
            assertEquals(before.wall.drop(before.wallPointer + 1), after.wall.drop(after.wallPointer + 1))
            assertEquals(0, after.cheatPoints[0])
            assertNull(snap(a).sleeveBloom!!.pendingOffer)
            assertNotNull(frozen.sleeveBloom!!.pendingOffer)
            val discards = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DiscardTile>()
            assertTrue(discards.any { it.tile == second })
            assertTrue(discards.none { it.tile == first })
            assertEquals(ReceiptStatus.REJECTED, a.submitChoice(0, oldAction, a.clock.nowMillis()).receipt.status)
            val newClock = a.decisionClock.timing(0)!!
            assertEquals(timing.copy(key = newClock.key), newClock)
            physical(after)
        }
    }

    @Test fun `first choice costs no more and leaves all physical positions intact`() {
        table(points = 10).use { a ->
            activate(a)
            val before = state(a)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, choose(a, SleeveBloomKeep.FIRST), a.clock.nowMillis()).receipt.status)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.players[0].drawnTile, state(a).players[0].drawnTile)
            assertEquals(before.turnNumber, state(a).turnNumber)
            assertNull(snap(a).sleeveBloom!!.pendingOffer)
            assertEquals(0, state(a).cheatPoints[0])
            assertEquals("SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW", a.submitSleeveBloom(0, req(a, "again"), a.clock.nowMillis()).receipt.reason)
        }
    }

    @Test fun `activation and choice retransmit original receipt after rekey and resolution`() {
        table().use { a ->
            val activation = req(a)
            val receipt = a.submitSleeveBloom(0, activation, a.clock.nowMillis()).receipt
            assertEquals(ReceiptStatus.APPLIED, receipt.status)
            val replay = a.submitSleeveBloom(0, activation, a.clock.nowMillis())
            assertEquals(receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            val selection = choose(a, SleeveBloomKeep.SECOND)
            val selected = a.submitSleeveBloomChoice(0, selection, a.clock.nowMillis()).receipt
            assertEquals(ReceiptStatus.APPLIED, selected.status)
            val after = locations(state(a))
            val late = a.decisionClock.timing(0)!!.deadlineMillis + 1
            assertEquals(receipt, a.submitSleeveBloom(0, activation, late).receipt)
            assertEquals(selected, a.submitSleeveBloomChoice(0, selection, late).receipt)
            assertEquals(30, state(a).cheatPoints[0])
            assertEquals(after, locations(state(a)))
            assertEquals("REQUEST_ID_CONFLICT", a.submitSleeveBloomChoice(0, selection.copy(keep = SleeveBloomKeep.FIRST), late).receipt.reason)
            assertEquals("REQUEST_ID_CONFLICT", a.submitSleeveBloom(0, activation.copy(decisionId = "changed"), late).receipt.reason)
        }
    }

    @Test fun `operation types share request identity namespace`() {
        table().use { a ->
            activate(a)
            val conflict = choose(a, SleeveBloomKeep.FIRST, "activate")
            assertEquals("REQUEST_ID_CONFLICT", a.submitSleeveBloomChoice(0, conflict, a.clock.nowMillis()).receipt.reason)
            val s = snap(a)
            val mark = TileMarkRequest("activate", s.sessionId, s.roundId!!, s.decision.decisionId,
                s.decision.controlEpoch, s.view.myClosedTiles.take(4).map { it.tileId })
            assertEquals("REQUEST_ID_CONFLICT", a.submitTileMark(0, mark, a.clock.nowMillis()).receipt.reason)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `pending offer blocks discard kan win riichi and marking without another fee`() {
        table().use { a ->
            activate(a)
            val s = snap(a)
            val discard = action(a, "blocked-discard") { it is GameAction.DiscardTile }
            assertEquals("SLEEVE_BLOOM_CHOICE_PENDING", a.submitChoice(0, discard, a.clock.nowMillis()).receipt.reason)
            val mark = TileMarkRequest("blocked-mark", s.sessionId, s.roundId!!, s.decision.decisionId,
                s.decision.controlEpoch, s.view.myClosedTiles.take(4).map { it.tileId })
            assertEquals("SLEEVE_BLOOM_CHOICE_PENDING", a.submitTileMark(0, mark, a.clock.nowMillis()).receipt.reason)
            val blocked = listOf(
                GameAction.DeclareTsumo(0),
                GameAction.DeclareRiichi(0, s.view.myDrawnTile!!),
                GameAction.DeclareKan(0, s.view.myClosedTiles.take(4), MeldType.CLOSED_KAN)
            )
            for (gameAction in blocked) assertTrue(a.coordinator.handleAction(gameAction) is ActionResult.Rejected)
            assertEquals("SLEEVE_BLOOM_ALREADY_PENDING", a.submitSleeveBloom(0, req(a, "again"), a.clock.nowMillis()).receipt.reason)
            assertEquals(30, state(a).cheatPoints[0])
            assertNotNull(snap(a).sleeveBloom!!.pendingOffer)
        }
    }

    @Test fun `invalid offer and old identity cannot change candidates`() {
        table().use { a ->
            val old = req(a)
            activate(a)
            val selection = choose(a, SleeveBloomKeep.SECOND)
            assertEquals("SLEEVE_BLOOM_OFFER_MISMATCH", a.submitSleeveBloomChoice(0, selection.copy(offerId = "wrong"), a.clock.nowMillis()).receipt.reason)
            assertEquals(ReceiptStatus.REJECTED, a.submitSleeveBloomChoice(0, selection.copy(requestId = "stale", decisionId = old.decisionId), a.clock.nowMillis()).receipt.status)
            assertNotNull(snap(a).sleeveBloom!!.pendingOffer)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `deadline first keeps original then discards with no stuck pending state`() {
        table().use { a ->
            val original = state(a).players[0].drawnTile!!
            activate(a)
            val selection = choose(a, SleeveBloomKeep.SECOND)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            (a.clock as Clock).now = deadline
            assertEquals("DEADLINE_EXPIRED", a.submitSleeveBloomChoice(0, selection, deadline).receipt.reason)
            assertNotNull(a.checkTimeout(deadline))
            assertNull(state(a).sleeveBloomOffer)
            assertTrue(state(a).players[0].discardHistory.contains(original))
            assertEquals(30, state(a).cheatPoints[0])
            assertNull(a.checkTimeout(deadline))
            physical(state(a))
        }
    }

    @Test fun `pause freezes offer and resumes remaining decision budget`() {
        table().use { a ->
            activate(a)
            val selection = choose(a, SleeveBloomKeep.FIRST)
            val clock = a.clock as Clock
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            clock.now += 100
            a.pause(clock.now)
            assertFalse(snap(a).sleeveBloom!!.canApply)
            clock.now += 100_000
            assertNull(a.checkTimeout(clock.now))
            assertNotNull(state(a).sleeveBloomOffer)
            assertEquals("HOST_PAUSED", a.submitSleeveBloomChoice(0, selection, clock.now).receipt.reason)
            a.resume(clock.now)
            assertEquals(deadline + 100_000, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, choose(a, SleeveBloomKeep.FIRST, "after-pause"), clock.now).receipt.status)
        }
    }

    @Test fun `takeover then reclaim retains offer and deadline but rejects unexecuted old epoch`() {
        table().use { a ->
            activate(a)
            val oldChoice = choose(a, SleeveBloomKeep.SECOND)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            a.setSeatController(0, SeatController.Takeover())
            a.syncDecisionsAndClocks(a.clock.nowMillis(), setOf(0))
            assertNotNull(state(a).sleeveBloomOffer)
            assertEquals(ReceiptStatus.REJECTED, a.submitSleeveBloomChoice(0, oldChoice, a.clock.nowMillis()).receipt.status)
            a.setSeatController(0, SeatController.RemoteHuman(false))
            a.syncDecisionsAndClocks(a.clock.nowMillis(), emptySet())
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            val newChoice = choose(a, SleeveBloomKeep.SECOND, "reclaimed")
            assertNotEquals(oldChoice.controlEpoch, newChoice.controlEpoch)
            assertEquals(snap(a).decision.controlEpoch, snap(a).sleeveBloom!!.pendingOffer!!.controlEpoch)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, newChoice, a.clock.nowMillis()).receipt.status)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `low funds rejection is repeatable and feature off reveals no offer`() {
        table(points = 9).use { a ->
            val request = req(a)
            val version = a.coordinator.activeRound!!.version
            val rejected = a.submitSleeveBloom(0, request, a.clock.nowMillis())
            assertEquals("INSUFFICIENT_FUNDS", rejected.receipt.reason)
            assertEquals(rejected.receipt, a.submitSleeveBloom(0, request, Long.MAX_VALUE).receipt)
            assertEquals(version, a.coordinator.activeRound!!.version)
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(9, state(a).cheatPoints[0])
        }
        table(enabled = false).use { a ->
            assertNull(snap(a).sleeveBloom)
            assertEquals("SLEEVE_BLOOM_DISABLED", a.submitSleeveBloom(0, req(a), a.clock.nowMillis()).receipt.reason)
        }
    }

    @Test fun `dealer extra riichi no draw wrong phase and wrong seat reject activation`() {
        val cases = listOf(
            normal().also { it.lastDrawKind = SleeveBloomDrawKind.DEAL_EXTRA; it.turnNumber = 0 } to "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED",
            normal().also { it.riichiDeclared[0] = true } to "SLEEVE_BLOOM_RIICHI_LOCKED",
            TestTableBuilder.create(phase = GamePhase.CHEAT_WINDOW).also { it.lastDrawKind = SleeveBloomDrawKind.NORMAL } to "SLEEVE_BLOOM_NO_DRAWN_TILE",
            normal().also { it.phase = GamePhase.DISCARD } to "WRONG_PHASE"
        )
        for ((s, reason) in cases) table(s).use { a ->
            assertEquals(reason, a.submitSleeveBloom(0, req(a), a.clock.nowMillis()).receipt.reason)
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(40, state(a).cheatPoints[0])
        }
        table().use { a ->
            assertEquals(ReceiptStatus.REJECTED, a.submitSleeveBloom(1, req(a, seat = 1), a.clock.nowMillis()).receipt.status)
        }
    }

    @Test fun `marking before exchange follows original physical tile into hidden wall`() {
        table().use { a ->
            val s = snap(a)
            val ids = listOf(s.view.myDrawnTile!!.tileId) + s.view.myClosedTiles.take(3).map { it.tileId }
            val mark = TileMarkRequest("mark-first", s.sessionId, s.roundId!!, s.decision.decisionId, s.decision.controlEpoch, ids)
            assertEquals(ReceiptStatus.APPLIED, a.submitTileMark(0, mark, a.clock.nowMillis()).receipt.status)
            activate(a)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, choose(a, SleeveBloomKeep.SECOND), a.clock.nowMillis()).receipt.status)
            assertEquals("NOT_VISIBLE", snap(a).tileMark!!.activeGroups.single().tiles.first { it.tileId == ids.first() }.area.name)
            assertEquals(20, state(a).cheatPoints[0])
        }
    }

    @Test fun `missing clock and exact activation deadline cannot grant a free choice window`() {
        table().use { a ->
            val request = req(a)
            val timing = a.decisionClock.timing(0)!!
            assertEquals("DEADLINE_EXPIRED", a.submitSleeveBloom(0, request, timing.deadlineMillis).receipt.reason)
            a.decisionClock.cancel(timing.key, a.clock.nowMillis())
            assertFalse(snap(a).sleeveBloom!!.canApply, "No clock must disable entry in the authoritative projection")
            assertEquals("NO_ACTIVE_DECISION", a.submitSleeveBloom(0, request.copy(requestId = "no-clock"), a.clock.nowMillis()).receipt.reason)
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(40, state(a).cheatPoints[0])
        }
    }

    @Test fun `request value objects reject missing identity and negative control epoch`() {
        val valid = SleeveBloomRequest("id", "session", "round", "decision", 0)
        assertEquals(valid, valid.copy())
        for (invalid in listOf<() -> Unit>(
            { valid.copy(requestId = " ") }, { valid.copy(sessionId = "") },
            { valid.copy(roundId = "") }, { valid.copy(decisionId = "") }, { valid.copy(controlEpoch = -1) }
        )) assertThrows<IllegalArgumentException> { invalid() }
        val selection = SleeveBloomChoiceRequest("choice", "session", "round", "decision", 0, "offer", SleeveBloomKeep.FIRST)
        assertEquals(selection, selection.copy())
        assertThrows<IllegalArgumentException> { selection.copy(offerId = "") }
        assertThrows<IllegalArgumentException> { selection.copy(controlEpoch = -1) }
    }

    @Test fun `stale candidate cleans offer without exchanging discarding or gifting time`() {
        for (changeFirst in listOf(true, false)) table().use { a ->
            activate(a)
            val request = choose(a, SleeveBloomKeep.SECOND)
            val live = a.coordinator.activeRound!!.state
            if (changeFirst) {
                TileMovement.swapHandWithWall(live, 0, live.players[0].drawnTile!!)
            } else {
                val index = live.wallPointer
                val old = live.wall[index]
                live.wall[index] = live.wall[index + 1]
                live.wall[index + 1] = old
            }
            val before = state(a)
            val clock = a.decisionClock.timing(0)!!
            val result = a.submitSleeveBloomChoice(0, request, a.clock.nowMillis())
            assertEquals("SLEEVE_BLOOM_STALE_OFFER", result.receipt.reason)
            assertTrue(result.stateChanged, "Cleanup must notify the host even with a rejected receipt")
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(before.wall, state(a).wall)
            assertEquals(before.players[0].discardHistory, state(a).players[0].discardHistory)
            assertEquals(before.players[0].drawnTile, state(a).players[0].drawnTile)
            assertEquals(clock.deadlineMillis, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(30, state(a).cheatPoints[0])
            physical(state(a))
        }
    }

    @Test fun `completed activation replay while paused returns original receipt without changing state`() {
        table().use { a ->
            val request = req(a)
            val receipt = a.submitSleeveBloom(0, request, a.clock.nowMillis()).receipt
            assertEquals(ReceiptStatus.APPLIED, receipt.status)
            a.pause(a.clock.nowMillis())
            val replay = a.submitSleeveBloom(0, request, a.clock.nowMillis())
            assertEquals(receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }

    @Test fun `activation rejected during pause remains rejected after resume`() {
        table().use { a ->
            val request = req(a)
            a.pause(a.clock.nowMillis())
            val rejected = a.submitSleeveBloom(0, request, a.clock.nowMillis())
            assertEquals("HOST_PAUSED", rejected.receipt.reason)
            a.resume(a.clock.nowMillis())
            val replay = a.submitSleeveBloom(0, request, a.clock.nowMillis())
            assertEquals(rejected.receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(40, state(a).cheatPoints[0])
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, request.copy(requestId = "fresh-after-resume"), a.clock.nowMillis()).receipt.status)
        }
    }

    @Test fun `choice rejected during pause cannot execute later under the same request id`() {
        table().use { a ->
            activate(a)
            val request = choose(a, SleeveBloomKeep.SECOND)
            val before = state(a)
            a.pause(a.clock.nowMillis())
            val rejected = a.submitSleeveBloomChoice(0, request, a.clock.nowMillis())
            assertEquals("HOST_PAUSED", rejected.receipt.reason)
            a.resume(a.clock.nowMillis())
            val replay = a.submitSleeveBloomChoice(0, request, a.clock.nowMillis())
            assertEquals(rejected.receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(before.sleeveBloomOffer, state(a).sleeveBloomOffer)
            assertEquals(locations(before), locations(state(a)))
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, request.copy(requestId = "fresh-choice-after-resume"), a.clock.nowMillis()).receipt.status)
        }
    }

    @Test fun `wrong session scope cannot poison the current sessions request identity`() {
        table().use { a ->
            val valid = req(a)
            assertEquals(ReceiptStatus.REJECTED, a.submitSleeveBloom(0, valid.copy(sessionId = "foreign-session"), a.clock.nowMillis()).receipt.status)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, valid, a.clock.nowMillis()).receipt.status)
            assertEquals(30, state(a).cheatPoints[0])
        }
    }
}

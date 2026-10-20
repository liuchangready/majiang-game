package com.majiang.cheat.mark

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.authority.TableAuthority
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TilePool
import com.majiang.state.GameAction
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.stacking.ShuffleConfig
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows

/** Codex-owned integration tests through real authority operations and physical round state. */
@Timeout(40)
class TileMarkAuthorityContractTest {
    private class ManualClock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis() = now
    }

    private val controls = List(4) { SeatControlView(it, true, "HUMAN", 0L) }
    private fun snapshot(a: TableAuthority, seat: Int = 0) = a.querySnapshot(seat, "test-sync", 0L, controls)

    private fun table(points: Int = 40, enabled: Boolean = true, state: GameState = TestTableBuilder.create(), shuffleEnabled: Boolean = false): TableAuthority {
        state.cheatPoints.fill(points)
        return TableAuthority(
            config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(true, List(4) { points }, mapOf("TILE_MARK" to 10)),
                shuffleConfig = ShuffleConfig(enabled = shuffleEnabled),
                tileMarkConfig = TileMarkConfig(enabled)
            ),
            clock = ManualClock(),
            controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = state
        ).also { it.syncDecisionsAndClocks(it.clock.nowMillis(), emptySet()) }
    }

    private fun ownIds(a: TableAuthority, seat: Int = 0): List<Int> {
        val view = snapshot(a, seat).view
        return (view.myClosedTiles + listOfNotNull(view.myDrawnTile)).map { it.tileId }
    }

    private fun request(a: TableAuthority, id: String = "mark", ids: List<Int> = ownIds(a).take(4), seat: Int = 0): TileMarkRequest {
        val snap = snapshot(a, seat)
        return TileMarkRequest(id, snap.sessionId, snap.roundId!!, snap.decision.decisionId, snap.decision.controlEpoch, ids)
    }

    private fun choice(a: TableAuthority, seat: Int, id: String, predicate: (GameAction) -> Boolean): ActionChoice {
        val decision = a.queryDecision(seat, false)
        val option = decision.options.first { predicate(it.action) }
        return ActionChoice(id, decision.roundId, decision.decisionId, decision.controlEpoch, option.optionId)
    }

    @Test
    fun `successful marking atomically costs ten and leaves tiles scores turn and deadline unchanged`() {
        table(points = 10).use { a ->
            val before = a.coordinator.activeRound!!.readState()
            val timing = a.decisionClock.timing(0)!!
            val req = request(a)
            val oldSnapshot = snapshot(a)
            val oldLocations = TilePool.buildStandardSet().associate { it.tileId to before.tilePool.getLocation(it) }
            val result = a.submitTileMark(0, req, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, result.receipt.status)
            assertTrue(result.stateChanged)
            val after = a.coordinator.activeRound!!.readState()
            val current = snapshot(a)
            assertEquals(0, after.cheatPoints[0])
            assertEquals(before.scores.toList(), after.scores.toList())
            assertEquals(before.currentPlayer, after.currentPlayer)
            assertEquals(oldLocations, TilePool.buildStandardSet().associate { it.tileId to after.tilePool.getLocation(it) })
            TestTableBuilder.assertPhysical(after)
            assertEquals(req.tileIds.toSet(), current.tileMark!!.activeGroups.single().tiles.map { it.tileId }.toSet())
            assertTrue(oldSnapshot.tileMark!!.activeGroups.isEmpty())
            val afterTiming = a.decisionClock.timing(0)!!
            assertEquals(timing.copy(key = afterTiming.key), afterTiming)
            assertNotEquals(req.decisionId, current.decision.decisionId)
            assertTrue(a.submitChoice(0, choice(a, 0, "discard") { it is GameAction.DiscardTile }, a.clock.nowMillis()).receipt.status in setOf(ReceiptStatus.APPLIED, ReceiptStatus.RECORDED))
        }
    }

    @Test
    fun `three groups cost thirty and a fourth attempt neither replaces nor charges`() {
        table().use { a ->
            val ids = ownIds(a)
            for (batch in 0..2) {
                assertEquals(ReceiptStatus.APPLIED, a.submitTileMark(0, request(a, "m$batch", ids.drop(batch * 4).take(4)), a.clock.nowMillis()).receipt.status)
                assertEquals((batch + 1) * 4, snapshot(a).tileMark!!.activeTileCount)
            }
            val state = a.coordinator.session.tileMarkState
            val rejected = a.submitTileMark(0, request(a, "fourth", ids.take(4)), a.clock.nowMillis())
            assertEquals(ReceiptStatus.REJECTED, rejected.receipt.status)
            assertFalse(rejected.stateChanged)
            assertEquals(10, snapshot(a).cheatPoints[0])
            assertSame(state, a.coordinator.session.tileMarkState)
            assertFalse(snapshot(a).tileMark!!.canApply)
        }
    }

    @Test
    fun `rejected selection or low balance leaves no fee group or partial version`() {
        table().use { a ->
            val ids = ownIds(a)
            val foreign = ownIds(a, 1).first()
            val invalid = listOf(ids.take(3), ids.take(5), listOf(ids[0], ids[0], ids[1], ids[2]), listOf(-1) + ids.take(3), listOf(foreign) + ids.take(3))
            val version = a.coordinator.activeRound!!.version
            val stateVersion = a.coordinator.session.tileMarkState.version
            for ((i, selection) in invalid.withIndex()) {
                assertEquals(ReceiptStatus.REJECTED, a.submitTileMark(0, request(a, "bad-$i", selection), a.clock.nowMillis()).receipt.status)
                assertEquals(40, snapshot(a).cheatPoints[0])
                assertTrue(snapshot(a).tileMark!!.activeGroups.isEmpty())
                assertEquals(version, a.coordinator.activeRound!!.version)
                assertEquals(stateVersion, a.coordinator.session.tileMarkState.version)
            }
        }
        table(points = 9).use { a ->
            val result = a.submitTileMark(0, request(a), a.clock.nowMillis())
            assertEquals("INSUFFICIENT_FUNDS", result.receipt.reason)
            assertEquals(9, snapshot(a).cheatPoints[0])
            assertTrue(snapshot(a).tileMark!!.activeGroups.isEmpty())
        }
    }

    @Test
    fun `deadline and stale request checks cannot be bypassed with marking`() {
        table().use { a ->
            val req = request(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            val result = a.submitTileMark(0, req, deadline)
            assertEquals("DEADLINE_EXPIRED", result.receipt.reason)
            assertFalse(result.stateChanged)
            assertEquals(40, snapshot(a).cheatPoints[0])
        }
        table().use { a ->
            val req = request(a)
            for ((i, stale) in listOf(req.copy(sessionId = "wrong"), req.copy(roundId = "wrong"), req.copy(decisionId = "wrong"), req.copy(controlEpoch = req.controlEpoch + 1)).withIndex()) {
                assertEquals(ReceiptStatus.REJECTED, a.submitTileMark(0, stale.copy(requestId = "stale-$i"), a.clock.nowMillis()).receipt.status)
            }
            a.pause(a.clock.nowMillis())
            assertEquals("HOST_PAUSED", a.submitTileMark(0, req, a.clock.nowMillis()).receipt.reason)
        }
    }

    @Test
    fun `missing decision clock rejects marking instead of providing an unlimited window`() {
        table().use { a ->
            val req = request(a)
            a.decisionClock.cancel(a.decisionClock.timing(0)!!.key, a.clock.nowMillis())
            assertEquals(ReceiptStatus.REJECTED, a.submitTileMark(0, req, a.clock.nowMillis()).receipt.status)
            assertEquals(40, snapshot(a).cheatPoints[0])
            assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
        }
    }

    @Test
    fun `riichi and another players turn cannot start a new marking group`() {
        val locked = TestTableBuilder.create(drawn = 0 to Tile(Suit.SOU, 9, 3))
        locked.riichiDeclared[0] = true
        table(state = locked).use { a ->
            assertEquals("TILE_MARK_RIICHI_LOCKED", a.submitTileMark(0, request(a), a.clock.nowMillis()).receipt.reason)
            assertEquals(40, snapshot(a).cheatPoints[0])
            assertFalse(snapshot(a).tileMark!!.canApply)
        }
        table().use { a ->
            val req = request(a, ids = ownIds(a, 1).take(4), seat = 1)
            assertEquals(ReceiptStatus.REJECTED, a.submitTileMark(1, req, a.clock.nowMillis()).receipt.status)
            assertEquals(40, snapshot(a, 1).cheatPoints[1])
            assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
        }
    }

    @Test
    fun `fee event observers see the committed mark in the same publication`() {
        table().use { a ->
            val observed = mutableListOf<Pair<Int, Int>>()
            a.coordinator.subscribe { events ->
                if (events.any { it is GameEvent.CheatPointsChanged }) {
                    observed.add(snapshot(a).cheatPoints[0] to a.coordinator.session.tileMarkState.groupsForSeat(0).size)
                }
            }
            assertEquals(ReceiptStatus.APPLIED, a.submitTileMark(0, request(a), a.clock.nowMillis()).receipt.status)
            assertTrue(observed.isNotEmpty())
            assertTrue(observed.all { it == (30 to 1) }, "Fee must not publish before mark installation: $observed")
        }
    }

    @Test
    fun `takeover rejects human marking without clearing existing effects and close clears groups`() {
        val a = table()
        try {
            val ids = ownIds(a)
            a.submitTileMark(0, request(a, ids = ids.take(4)), a.clock.nowMillis())
            val existing = a.coordinator.session.tileMarkState
            a.setSeatController(0, SeatController.Takeover())
            val rejected = a.submitTileMark(0, request(a, "under-takeover", ids.drop(4).take(4)), a.clock.nowMillis())
            assertEquals(ReceiptStatus.REJECTED, rejected.receipt.status)
            assertSame(existing, a.coordinator.session.tileMarkState)
            a.setSeatController(0, SeatController.RemoteHuman(false))
            assertSame(existing, a.coordinator.session.tileMarkState)
        } finally {
            a.close()
        }
        assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
    }

    @Test
    fun `preparation does not count a round until an actual wall is dealt`() {
        val clock = ManualClock()
        TableAuthority(
            config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to 10)),
                shuffleConfig = ShuffleConfig(enabled = true),
                tileMarkConfig = TileMarkConfig(true)
            ),
            clock = clock,
            controllers = List(4) { SeatController.RemoteHuman(false) }
        ).use { a ->
            assertEquals(0, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status)
            assertEquals(0, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            // Preparation waits for its authority deadline even after every automatic seat confirms.
            assertNull(a.checkPreparationStep(clock.now, setOf(0, 1, 2, 3), emptySet()))
            assertEquals(0, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            clock.now = snapshot(a).preparation!!.deadlineMillis
            assertNotNull(a.checkPreparationStep(clock.now, setOf(0, 1, 2, 3), emptySet()))
            assertEquals(1, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            a.checkPreparationStep(clock.now, setOf(0, 1, 2, 3), emptySet())
            assertEquals(1, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            TestTableBuilder.assertPhysical(a.coordinator.activeRound!!.readState())
        }
    }

    @Test
    fun `successful replay is order independent and cannot be reused as a discard operation`() {
        table().use { a ->
            val req = request(a)
            val success = a.submitTileMark(0, req, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, success.receipt.status)
            val replay = a.submitTileMark(0, req.copy(tileIds = req.tileIds.reversed()), a.clock.nowMillis())
            assertEquals(success.receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(30, snapshot(a).cheatPoints[0])
            assertEquals("REQUEST_ID_CONFLICT", a.submitTileMark(0, req.copy(tileIds = ownIds(a).takeLast(4)), a.clock.nowMillis()).receipt.reason)
            for (changedIdentity in listOf(
                req.copy(sessionId = "other-session"),
                req.copy(roundId = "other-round"),
                req.copy(decisionId = "other-decision"),
                req.copy(controlEpoch = req.controlEpoch + 1)
            )) {
                val conflict = a.submitTileMark(0, changedIdentity, a.clock.nowMillis())
                assertEquals("REQUEST_ID_CONFLICT", conflict.receipt.reason, "Every identity field participates in the request fingerprint")
                assertFalse(conflict.stateChanged)
            }
            val discard = choice(a, 0, req.requestId) { it is GameAction.DiscardTile }
            assertEquals("REQUEST_ID_CONFLICT", a.submitChoice(0, discard, a.clock.nowMillis()).receipt.reason)
        }
    }

    @Test
    fun `choice during next preparation cannot overwrite a successful marking receipt`() {
        table(shuffleEnabled = true).use { a ->
            val req = request(a)
            val applied = a.submitTileMark(0, req, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, applied.receipt.status)
            playToSettlement(a)
            assertNotNull(a.checkAutoNextRound(a.clock.nowMillis(), true))
            assertTrue(a.isPreparationActive())
            val decision = a.queryDecision(0, false)
            val collidingChoice = ActionChoice(req.requestId, decision.roundId, decision.decisionId,
                decision.controlEpoch, "unavailable-option")
            assertEquals("REQUEST_ID_CONFLICT", a.submitChoice(0, collidingChoice, a.clock.nowMillis()).receipt.reason)
            val replay = a.submitTileMark(0, req, a.clock.nowMillis())
            assertEquals(applied.receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(1, a.coordinator.session.tileMarkState.dealtRoundOrdinal)
            assertEquals(1, a.coordinator.session.tileMarkState.groups.size)
        }
    }

    @Test
    fun `disabled config rejects marking and enabled config requires explicit positive fee`() {
        table(enabled = false).use { a ->
            assertNull(snapshot(a).tileMark)
            assertEquals("TILE_MARK_DISABLED", a.submitTileMark(0, request(a), a.clock.nowMillis()).receipt.reason)
        }
        for (costs in listOf(emptyMap(), mapOf("TILE_MARK" to 0), mapOf("TILE_MARK" to -1))) {
            assertThrows<IllegalArgumentException> {
                MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, costs), tileMarkConfig = TileMarkConfig(true)).validateSupported()
            }
        }
        for (cost in listOf(5, 9, 11)) {
            MatchConfig(
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to cost)),
                tileMarkConfig = TileMarkConfig(true)
            ).validateSupported()
        }
        assertThrows<IllegalArgumentException> { MatchConfig(tileMarkConfig = TileMarkConfig(true)).validateSupported() }
    }

    @Test
    fun `marked discard carries exact current river index then loses it when called into meld`() {
        val called = Tile(Suit.MAN, 5, 0)
        val fixture = TestTableBuilder.create(
            hands = mapOf(1 to listOf(Tile(Suit.MAN, 5, 1), Tile(Suit.MAN, 5, 2))),
            drawn = 0 to called,
            discards = mapOf(0 to (1..7).map { Tile(Suit.PIN, it, 3) })
        )
        table(state = fixture).use { a ->
            val selected = listOf(called.tileId) + ownIds(a).filter { it != called.tileId }.take(3)
            assertEquals(ReceiptStatus.APPLIED, a.submitTileMark(0, request(a, ids = selected), a.clock.nowMillis()).receipt.status)
            a.submitChoice(0, choice(a, 0, "discard-marked") { it is GameAction.DiscardTile && it.tile == called }, a.clock.nowMillis())
            val inRiver = snapshot(a).tileMark!!.activeGroups.single().tiles.single { it.tileId == called.tileId }
            assertEquals(TileMarkArea.DISCARD, inRiver.area)
            assertEquals(0, inRiver.seat)
            assertEquals(RiverPosition(snapshot(a).roundId!!, 0, 7), inRiver.riverPosition)
            assertEquals(called, snapshot(a).view.discards[0][7])
            val pon = choice(a, 1, "pon-marked") { it is GameAction.DeclarePon }
            a.submitChoice(1, pon, a.clock.nowMillis())
            // Higher priority responses, if any, must finish before the physical call is installed.
            for (seat in listOf(2, 3)) {
                val decision = a.queryDecision(seat, false)
                val pass = decision.options.firstOrNull { it.action is GameAction.Pass } ?: continue
                a.submitChoice(seat, ActionChoice("pass-$seat", decision.roundId, decision.decisionId, decision.controlEpoch, pass.optionId), a.clock.nowMillis())
            }
            a.coordinator.advanceAutomaticActions()
            val inMeld = snapshot(a).tileMark!!.activeGroups.single().tiles.single { it.tileId == called.tileId }
            assertEquals(TileMarkArea.EXPOSED_MELD, inMeld.area)
            assertEquals(1, inMeld.seat)
            assertNull(inMeld.riverPosition)
            assertFalse(snapshot(a).view.discards[0].contains(called))
            assertEquals(TileMarkArea.DISCARD, inRiver.area, "Old snapshot remains immutable")
        }
    }

    @Test
    fun `private groups are never included for another observer or in public schema`() {
        table().use { a ->
            a.submitTileMark(0, request(a), a.clock.nowMillis())
            assertEquals(1, snapshot(a).tileMark!!.activeGroups.size)
            for (seat in 1..3) {
                assertTrue(snapshot(a, seat).tileMark!!.activeGroups.isEmpty())
                assertTrue(snapshot(a, seat).tileMark!!.selectableTileIds.isEmpty())
                assertFalse(snapshot(a, seat).tileMark!!.canApply)
            }
            assertFalse(a.queryPublicProjection().javaClass.declaredFields.any { it.name.contains("tileMark", ignoreCase = true) })
        }
    }

    @Test
    fun `snapshot deep copy retains immutable marks and does not follow later casts`() {
        table().use { a ->
            val ids = ownIds(a)
            a.submitTileMark(0, request(a, "first", ids.take(4)), a.clock.nowMillis())
            val original = snapshot(a)
            val copied = original.deepCopy()
            assertNotNull(copied.tileMark)
            assertEquals(ids.take(4).toSet(), copied.tileMark!!.activeGroups.single().tiles.map { it.tileId }.toSet())
            assertThrows<UnsupportedOperationException> { (copied.tileMark!!.activeGroups as MutableList<*>).clear() }
            assertThrows<UnsupportedOperationException> { (copied.tileMark!!.activeGroups.single().tiles as MutableList<*>).clear() }
            assertThrows<UnsupportedOperationException> { (copied.tileMark!!.selectableTileIds as MutableList<*>).clear() }
            a.submitTileMark(0, request(a, "second", ids.drop(4).take(4)), a.clock.nowMillis())
            assertEquals(2, snapshot(a).tileMark!!.activeGroups.size)
            assertEquals(1, original.tileMark!!.activeGroups.size)
            assertEquals(1, copied.tileMark!!.activeGroups.size)
        }
    }

    @Test
    fun `true four round progression expires marks and late replay never revives them`() {
        table().use { a ->
            val req = request(a)
            val applied = a.submitTileMark(0, req, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, applied.receipt.status)
            val oldGroup = snapshot(a).tileMark!!.activeGroups.single()
            for (round in 1..4) {
                val state = a.coordinator.session.tileMarkState
                assertEquals(round, state.dealtRoundOrdinal)
                assertEquals(1, snapshot(a).tileMark!!.activeGroups.size)
                assertEquals(4 - round, snapshot(a).tileMark!!.activeGroups.single().futureRoundsRemaining)
                verifyMarkedLocations(a)
                playToSettlement(a)
                assertEquals(if (round == 4) 0 else 1, a.coordinator.session.tileMarkState.groups.size)
                if (round < 4) {
                    assertNotNull(a.checkAutoNextRound(a.clock.nowMillis(), true))
                    a.coordinator.advanceAutomaticActions()
                    a.syncDecisionsAndClocks(a.clock.nowMillis(), emptySet())
                }
            }
            val balance = snapshot(a).cheatPoints[0]
            val replay = a.submitTileMark(0, req, a.clock.nowMillis())
            assertEquals(applied.receipt, replay.receipt)
            assertFalse(replay.stateChanged)
            assertEquals(balance, snapshot(a).cheatPoints[0])
            assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
            assertEquals(4, oldGroup.tiles.size)
        }
    }

    private fun playToSettlement(a: TableAuthority) {
        repeat(1200) { step ->
            if (a.coordinator.activeRound == null) return
            a.coordinator.advanceAutomaticActions()
            if (a.coordinator.activeRound == null) return
            verifyMarkedLocations(a)
            a.syncDecisionsAndClocks(a.clock.nowMillis(), emptySet())
            var acted = false
            for (seat in 0..3) {
                val decision = a.queryDecision(seat, false)
                val option = decision.options.firstOrNull { it.action is GameAction.Pass }
                    ?: decision.options.firstOrNull { it.action is GameAction.DiscardTile }
                    ?: decision.options.firstOrNull { it.action is GameAction.DeclareTsumo }
                    ?: continue
                val result = a.submitChoice(seat, ActionChoice("play-${decision.roundId}-$step-$seat", decision.roundId, decision.decisionId, decision.controlEpoch, option.optionId), a.clock.nowMillis())
                assertTrue(result.receipt.status in setOf(ReceiptStatus.APPLIED, ReceiptStatus.RECORDED), result.receipt.toString())
                acted = true
                break
            }
            assertTrue(acted || a.coordinator.activeRound == null, "No action before settlement")
        }
        fail<Unit>("Round did not settle within bounded legal play")
    }

    private fun verifyMarkedLocations(a: TableAuthority) {
        val round = a.coordinator.activeRound ?: return
        val state = round.readState()
        val snap = snapshot(a)
        for (mark in snap.tileMark!!.activeGroups.flatMap { it.tiles }) {
            val handOwner = (0..3).firstOrNull { seat -> state.players[seat].allClosedTiles.any { it.tileId == mark.tileId } }
            val riverOwner = (0..3).firstOrNull { seat -> state.players[seat].discards.any { it.tileId == mark.tileId } }
            val meldOwner = (0..3).firstOrNull { seat -> state.players[seat].melds.flatMap { it.tiles }.any { it.tileId == mark.tileId } }
            when {
                handOwner != null -> {
                    assertEquals(if (handOwner == 0) TileMarkArea.OWN_HAND else TileMarkArea.OPPONENT_HAND, mark.area)
                    assertEquals(handOwner, mark.seat)
                    assertNull(mark.riverPosition)
                }
                riverOwner != null -> {
                    val index = state.players[riverOwner].discards.indexOfFirst { it.tileId == mark.tileId }
                    assertEquals(TileMarkArea.DISCARD, mark.area)
                    assertEquals(riverOwner, mark.seat)
                    assertEquals(RiverPosition(round.roundId, riverOwner, index), mark.riverPosition)
                    assertEquals(mark.tileId, snap.view.discards[riverOwner][index].tileId)
                }
                meldOwner != null -> {
                    assertEquals(TileMarkArea.EXPOSED_MELD, mark.area)
                    assertEquals(meldOwner, mark.seat)
                    assertNull(mark.riverPosition)
                }
                state.doraIndicators.any { it.tileId == mark.tileId } -> {
                    assertEquals(TileMarkArea.EXPOSED_INDICATOR, mark.area)
                    assertNull(mark.seat)
                    assertNull(mark.riverPosition)
                }
                else -> {
                    assertEquals(TileMarkArea.NOT_VISIBLE, mark.area)
                    assertNull(mark.seat)
                    assertNull(mark.riverPosition)
                }
            }
        }
    }
}

package com.majiang.cheat.bloom

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.authority.TableAuthority
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.model.Tile
import com.majiang.model.TileLocation
import com.majiang.model.TileMovement
import com.majiang.model.Zone
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
 * 立直与他家回合扩展：windowId 激活、10 秒权威截止、预留交付与物理守恒。
 */
@Timeout(40)
class SleeveBloomOffTurnTest {
    private class Clock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis() = now
    }

    private fun config(points: Int = 30) = MatchConfig(
        cheatEconomy = CheatEconomyConfig(true, List(4) { points }, mapOf("SLEEVE_BLOOM" to 10, "TILE_MARK" to 10)),
        sleeveBloomConfig = SleeveBloomConfig(enabled = true, offTurnChoiceMillis = 10_000L)
    )

    private fun baseState(points: Int = 30): GameState {
        val state = TestTableBuilder.create(
            discards = mapOf(0 to listOf(Tile.fromTypeIndex(0, 0))),
            phase = GamePhase.DRAW,
            currentPlayer = 1
        )
        state.cheatPoints.fill(points)
        state.openSleeveBloomWindow(0)
        StateInvariants.assertValid(state)
        return state
    }

    private fun table(state: GameState, points: Int = 30, clock: Clock = Clock()): TableAuthority =
        TableAuthority(
            config = config(points),
            clock = clock,
            controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = state
        )

    private fun snap(a: TableAuthority, seat: Int = 0) = a.querySnapshot(
        seat, "off-turn-test", 0L,
        List(4) {
            SeatControlView(
                it, true,
                if (a.controllerFor(it) is SeatController.Takeover) "TAKEOVER" else "HUMAN",
                a.controlEpochFor(it)
            )
        }
    )

    private fun request(a: TableAuthority, state: GameState, id: String = "off-1"): SleeveBloomOffTurnRequest =
        SleeveBloomOffTurnRequest(
            requestId = id,
            sessionId = a.coordinator.session.sessionId,
            roundId = a.coordinator.activeRound!!.roundId,
            windowId = state.sleeveBloomWindowId(0)!!,
            controlEpoch = a.controlEpochFor(0)
        )

    private fun choice(
        a: TableAuthority,
        offerId: String,
        keep: SleeveBloomKeep,
        id: String = "off-choice"
    ): SleeveBloomOffTurnChoiceRequest = SleeveBloomOffTurnChoiceRequest(
        requestId = id,
        sessionId = a.coordinator.session.sessionId,
        roundId = a.coordinator.activeRound!!.roundId,
        controlEpoch = a.controlEpochFor(0),
        offerId = offerId,
        keep = keep
    )

    @Test
    fun `off-turn activation charges once reveals only owner and replays idempotently`() {
        val initial = baseState()
        table(initial).use { a ->
            val currentTiming = a.decisionClock.timing(1)!!
            val request = request(a, initial)
            val first = a.submitSleeveBloomOffTurn(0, request, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, first.receipt.status, first.receipt.reason)
            assertTrue(first.stateChanged)
            assertEquals(
                currentTiming.deadlineMillis,
                a.decisionClock.timing(1)!!.deadlineMillis,
                "off-turn activation must not gift the current player time"
            )

            val live = a.coordinator.activeRound!!.readState()
            assertEquals(20, live.cheatPoints[0])
            val offer = live.sleeveBloomOffTurnOffer(0)
            assertNotNull(offer)
            assertEquals(SleeveBloomOfferMode.OFF_TURN, offer!!.mode)
            assertEquals(initial.wall[initial.wallPointer], offer.firstTile)
            assertEquals(initial.wall[initial.wallPointer + 1], offer.secondTile)

            val owner = snap(a, 0)
            assertEquals(offer.offerId, owner.sleeveBloom!!.pendingOffer!!.offerId)
            assertEquals(SleeveBloomOfferMode.OFF_TURN, owner.sleeveBloom.pendingOffer!!.mode)
            for (seat in 1..3) {
                assertNull(snap(a, seat).sleeveBloom!!.pendingOffer, "private candidates leaked to seat $seat")
            }
            assertEquals(20, snap(a, 1).cheatPoints[0], "public balance is visible")

            val replay = a.submitSleeveBloomOffTurn(0, request, a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, replay.receipt.status)
            assertFalse(replay.stateChanged)
            assertEquals(20, a.coordinator.activeRound!!.readState().cheatPoints[0])
            TestTableBuilder.assertPhysical(a.coordinator.activeRound!!.readState())
        }
    }

    @Test
    fun `second choice reserves selected entity without a second charge and unlocks the other`() {
        val initial = baseState()
        table(initial).use { a ->
            val activated = a.submitSleeveBloomOffTurn(0, request(a, initial), a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, activated.receipt.status)
            val currentDeadline = a.decisionClock.timing(1)!!.deadlineMillis
            val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!

            val selected = a.submitSleeveBloomOffTurnChoice(
                0, choice(a, offer.offerId, SleeveBloomKeep.SECOND), a.clock.nowMillis()
            )
            assertEquals(ReceiptStatus.APPLIED, selected.receipt.status, selected.receipt.reason)
            assertEquals(
                currentDeadline,
                a.decisionClock.timing(1)!!.deadlineMillis,
                "off-turn choice must not gift the current player time"
            )

            val live = a.coordinator.activeRound!!.readState()
            assertEquals(20, live.cheatPoints[0], "selection must not charge again")
            assertNull(live.sleeveBloomOffTurnOffer(0))
            val reservation = live.sleeveBloomReservation(0)
            assertNotNull(reservation)
            assertEquals(offer.secondTile, reservation!!.tile)
            assertEquals(TileLocation.WALL, live.tilePool.getLocation(reservation.tile))
            assertEquals(TileLocation.WALL, live.tilePool.getLocation(offer.firstTile), "unselected tile must unlock")
            assertTrue(live.wall.contains(reservation.tile))
            TestTableBuilder.assertPhysical(live)
            StateInvariants.assertValid(live)
        }
    }

    @Test
    fun `authority deadline settles FIRST and keeps the fee without blocking other seats`() {
        val initial = baseState()
        val clock = Clock()
        table(initial, clock = clock).use { a ->
            val offer = a.submitSleeveBloomOffTurn(0, request(a, initial), clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, offer.receipt.status)
            val initialTiming = a.decisionClock.timing(1)!!.deadlineMillis
            val firstTile = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!.firstTile

            val tooEarly = a.checkTimeout(clock.nowMillis() + 9_999L)
            assertNull(tooEarly, "offer must remain until its own 10s deadline")
            assertNotNull(a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0))

            val expired = a.checkTimeout(clock.nowMillis() + 10_000L)
            assertNotNull(expired)
            assertTrue(expired is TableAuthority.AutoStepResult.SleeveBloomOffTurnExpired)
            assertEquals(
                initialTiming,
                a.decisionClock.timing(1)!!.deadlineMillis,
                "offer timeout must not gift the current player time"
            )

            val live = a.coordinator.activeRound!!.readState()
            assertEquals(20, live.cheatPoints[0], "timeout never refunds or double charges")
            assertNull(live.sleeveBloomOffTurnOffer(0))
            assertEquals(firstTile, live.sleeveBloomReservation(0)!!.tile)
            TestTableBuilder.assertPhysical(live)
        }
    }

    @Test
    fun `reservation survives other draws and is delivered only on the owner next normal draw`() {
        val initial = baseState()
        table(initial).use { a ->
            val activated = a.submitSleeveBloomOffTurn(0, request(a, initial), a.clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, activated.receipt.status)
            val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            a.submitSleeveBloomOffTurnChoice(0, choice(a, offer.offerId, SleeveBloomKeep.SECOND), a.clock.nowMillis())
            val reserved = offer.secondTile

            val copy = a.coordinator.activeRound!!.readState()
            copy.phase = GamePhase.DRAW
            copy.currentPlayer = 2
            copy.players[2].drawnTile = null
            val otherDraw = TileMovement.drawNormal(copy, 2)
            assertNotEquals(reserved, otherDraw.tile, "other seats must skip the reserved entity")
            assertTrue(copy.sleeveBloomReservation(0) != null)
            assertEquals(TileLocation.WALL, copy.tilePool.getLocation(reserved))
            TileMovement.discard(copy, 2, otherDraw.tile)

            copy.phase = GamePhase.DRAW
            copy.currentPlayer = 0
            copy.players[0].drawnTile = null
            val ownDraw = TileMovement.drawNormal(copy, 0)
            assertTrue(ownDraw.fromReservation)
            assertEquals(reserved, ownDraw.tile)
            assertNull(copy.sleeveBloomReservation(0))
            assertNull(copy.sleeveBloomWindowId(0), "window closes on the owner next normal draw")
            assertEquals(TileLocation.hand(0), copy.tilePool.getLocation(reserved))
            copy.phase = GamePhase.CHEAT_WINDOW
            TestTableBuilder.assertPhysical(copy)
            StateInvariants.assertValid(copy)
        }
    }

    @Test
    fun `rinshan replacement never steals a reserved live wall tail`() {
        val state = TestTableBuilder.create(
            phase = GamePhase.KAN_DRAW,
            currentPlayer = 0
        )
        val reserved = state.wall[state.wallEndExclusive - 1]
        state.putSleeveBloomReservation(SleeveBloomReservation(3, "tail", reserved, null, 1L))
        state.totalKanCount = 1

        val drawn = TileMovement.drawRinshan(state, 0)
        assertNotEquals(reserved, drawn)
        assertEquals(TileLocation.WALL, state.tilePool.getLocation(reserved))
        assertTrue(state.wall.contains(reserved))
        assertTrue(state.deadWallReplacements.none { it == reserved })
        TestTableBuilder.assertPhysical(state)
    }

    @Test
    fun `delivered reservation consumes the draw sleeve bloom slot and disables off-turn re-entry`() {
        val initial = baseState()
        table(initial).use { a ->
            val live = a.coordinator.activeRound!!.state
            val reserved = live.wall[live.wallPointer + 1]
            live.currentPlayer = 0
            live.phase = GamePhase.DRAW
            live.players[0].drawnTile = null
            live.openSleeveBloomWindow(0)
            live.putSleeveBloomReservation(
                SleeveBloomReservation(0, "reservation", reserved, live.sleeveBloomWindowId(0), 1L)
            )
            TestTableBuilder.assertPhysical(live)

            val draw = a.coordinator.handleAction(GameAction.DrawTile(0))
            assertTrue(draw is com.majiang.state.ActionResult.Accepted, draw.toString())
            val after = a.coordinator.activeRound!!.readState()
            assertEquals(reserved, after.players[0].drawnTile)
            assertEquals(after.turnNumber, after.sleeveBloomUsedOnTurn)
            assertNull(after.sleeveBloomReservation(0))
            assertEquals(30, after.cheatPoints[0], "delivery itself is free")
            TestTableBuilder.assertPhysical(after)
            StateInvariants.assertValid(after)
        }
    }

    @Test
    fun `immediate offer previews tsumo for the actual candidate hand only`() {
        val closed = listOf(
            Tile.fromTypeIndex(0, 0), Tile.fromTypeIndex(1, 0), Tile.fromTypeIndex(2, 0),
            Tile.fromTypeIndex(3, 0), Tile.fromTypeIndex(4, 0), Tile.fromTypeIndex(5, 0),
            Tile.fromTypeIndex(6, 0), Tile.fromTypeIndex(7, 0), Tile.fromTypeIndex(8, 0),
            Tile.fromTypeIndex(18, 0), Tile.fromTypeIndex(19, 0), Tile.fromTypeIndex(20, 0),
            Tile.fromTypeIndex(13, 0)
        )
        val state = TestTableBuilder.create(
            hands = mapOf(0 to closed),
            drawn = 0 to Tile.fromTypeIndex(13, 1),
            wallPrefix = listOf(Tile.fromTypeIndex(33, 0)),
            phase = GamePhase.CHEAT_WINDOW
        ).also {
            it.lastDrawKind = SleeveBloomDrawKind.NORMAL
            it.cheatPoints.fill(30)
        }
        table(state).use { a ->
            val request = SleeveBloomRequest(
                "preview",
                a.coordinator.session.sessionId,
                a.coordinator.activeRound!!.roundId,
                a.coordinator.queryDecision(0).decisionId,
                a.controlEpochFor(0)
            )
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, request, a.clock.nowMillis()).receipt.status)
            val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffer!!
            assertTrue(offer.firstCanTsumo, "the actual drawn tile completes a legal self-draw hand")
            assertFalse(offer.secondCanTsumo, "preview must not promise the unrelated wall tile")
            val view = snap(a, 0).sleeveBloom!!.pendingOffer!!
            assertTrue(view.firstCanTsumo)
            assertFalse(view.secondCanTsumo)
        }
    }

    @Test
    fun `pause freezes off-turn choice time and resume preserves the remaining budget`() {
        val initial = baseState()
        val clock = Clock(1_000L)
        table(initial, clock = clock).use { a ->
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurn(0, request(a, initial), clock.nowMillis()).receipt.status
            )
            val before = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            assertEquals(11_000L, before.deadlineMillis)

            assertTrue(a.pause(clock.nowMillis()))
            clock.now = 6_000L
            assertTrue(a.resume(clock.nowMillis()))
            val resumed = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            assertEquals(16_000L, resumed.deadlineMillis)

            assertNull(a.checkTimeout(15_999L))
            assertNotNull(a.checkTimeout(16_000L))
        }
    }

    @Test
    fun `riichi auto discard waits for the paid immediate choice until authority deadline`() {
        val state = TestTableBuilder.create(
            drawn = 0 to Tile.fromTypeIndex(1, 0),
            wallPrefix = listOf(Tile.fromTypeIndex(2, 0)),
            phase = GamePhase.CHEAT_WINDOW
        ).also {
            it.lastDrawKind = SleeveBloomDrawKind.NORMAL
            it.riichiDeclared[0] = true
            it.cheatPoints.fill(30)
        }
        table(state).use { a ->
            val request = SleeveBloomRequest(
                "riichi-bloom",
                a.coordinator.session.sessionId,
                a.coordinator.activeRound!!.roundId,
                a.coordinator.queryDecision(0).decisionId,
                a.controlEpochFor(0)
            )
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, request, a.clock.nowMillis()).receipt.status)
            val timing = a.decisionClock.timing(0)!!
            assertNull(
                a.checkRiichiAutoDiscard(a.clock.nowMillis(), setOf(0)),
                "auto discard must not steal the paid two-card choice"
            )
            assertNotNull(a.coordinator.activeRound!!.readState().sleeveBloomOffer)

            val timeout = a.checkTimeout(timing.deadlineMillis)
            assertNotNull(timeout)
            assertTrue(timeout is TableAuthority.AutoStepResult.TimeoutResolved)
            assertNull(a.coordinator.activeRound!!.readState().sleeveBloomOffer)
        }
    }

    @Test
    fun `unlocked candidates skipped behind pointer remain drawable after FIRST or SECOND choice`() {
        for (keep in listOf(SleeveBloomKeep.FIRST, SleeveBloomKeep.SECOND)) {
            val initial = baseState()
            table(initial).use { a ->
                assertEquals(
                    ReceiptStatus.APPLIED,
                    a.submitSleeveBloomOffTurn(0, request(a, initial), a.clock.nowMillis()).receipt.status
                )
                val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
                val live = a.coordinator.activeRound!!.state
                val skipped = TileMovement.drawNormal(live, 1)
                assertNotEquals(offer.firstTile, skipped.tile)
                assertNotEquals(offer.secondTile, skipped.tile)
                TileMovement.discard(live, 1, skipped.tile)
                assertTrue(live.wallPointer > live.wallTileIndex(offer.secondTile))

                assertEquals(
                    ReceiptStatus.APPLIED,
                    a.submitSleeveBloomOffTurnChoice(
                        0, choice(a, offer.offerId, keep), a.clock.nowMillis()
                    ).receipt.status
                )
                val expectedReserved = if (keep == SleeveBloomKeep.FIRST) offer.firstTile else offer.secondTile
                val expectedUnchosen = if (keep == SleeveBloomKeep.FIRST) offer.secondTile else offer.firstTile
                val after = a.coordinator.activeRound!!.readState()
                assertEquals(expectedReserved, after.sleeveBloomReservation(0)!!.tile)
                assertTrue(
                    after.availableWallIndices().contains(after.wallTileIndex(expectedUnchosen)),
                    "unlocked hole must re-enter the drawable queue"
                )
                StateInvariants.assertValid(after)
                TestTableBuilder.assertPhysical(after)

                val probe = after.deepCopy()
                probe.phase = GamePhase.DRAW
                probe.currentPlayer = 2
                probe.players[2].drawnTile = null
                val next = TileMovement.drawNormal(probe, 2)
                assertEquals(expectedUnchosen, next.tile, "the unlocked candidate must be drawn next")
                probe.phase = GamePhase.CHEAT_WINDOW
                TestTableBuilder.assertPhysical(probe)
                StateInvariants.assertValid(probe)
            }
        }
    }

    @Test
    fun `timeout FIRST behind pointer unlocks the second candidate without losing it`() {
        val initial = baseState()
        val clock = Clock()
        table(initial, clock = clock).use { a ->
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurn(0, request(a, initial), clock.nowMillis()).receipt.status
            )
            val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            val live = a.coordinator.activeRound!!.state
            val skipped = TileMovement.drawNormal(live, 1)
            TileMovement.discard(live, 1, skipped.tile)

            assertNotNull(a.checkTimeout(clock.nowMillis() + 10_000L))
            val after = a.coordinator.activeRound!!.readState()
            assertNull(after.sleeveBloomOffTurnOffer(0))
            assertEquals(offer.firstTile, after.sleeveBloomReservation(0)!!.tile)

            val probe = after.deepCopy()
            probe.phase = GamePhase.DRAW
            probe.currentPlayer = 2
            probe.players[2].drawnTile = null
            val next = TileMovement.drawNormal(probe, 2)
            assertEquals(offer.secondTile, next.tile)
            probe.phase = GamePhase.CHEAT_WINDOW
            TestTableBuilder.assertPhysical(probe)
            StateInvariants.assertValid(probe)
        }
    }

    @Test
    fun `reservation behind pointer is still delivered on the owner next normal draw`() {
        val initial = baseState()
        table(initial).use { a ->
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurn(0, request(a, initial), a.clock.nowMillis()).receipt.status
            )
            val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            val live = a.coordinator.activeRound!!.state
            val skipped = TileMovement.drawNormal(live, 1)
            TileMovement.discard(live, 1, skipped.tile)
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurnChoice(
                    0, choice(a, offer.offerId, SleeveBloomKeep.SECOND), a.clock.nowMillis()
                ).receipt.status
            )
            val before = a.coordinator.activeRound!!.readState()
            assertTrue(before.wallPointer > before.wallTileIndex(offer.secondTile))
            val probe = before.deepCopy()
            probe.phase = GamePhase.DRAW
            probe.currentPlayer = 0
            probe.players[0].drawnTile = null
            val draw = TileMovement.drawNormal(probe, 0)
            assertTrue(draw.fromReservation)
            assertEquals(offer.secondTile, draw.tile)
            assertNull(probe.sleeveBloomReservation(0))
            probe.phase = GamePhase.CHEAT_WINDOW
            TestTableBuilder.assertPhysical(probe)
            StateInvariants.assertValid(probe)
        }
    }

    @Test
    fun `cross seat offers interleave without consuming each other holes or reservations`() {
        val state = TestTableBuilder.create(
            discards = mapOf(
                0 to listOf(Tile.fromTypeIndex(0, 0)),
                1 to listOf(Tile.fromTypeIndex(1, 0))
            ),
            phase = GamePhase.DRAW,
            currentPlayer = 2
        ).also {
            it.cheatPoints.fill(30)
            it.openSleeveBloomWindow(0)
            it.openSleeveBloomWindow(1)
            StateInvariants.assertValid(it)
        }
        table(state).use { a ->
            fun requestFor(seat: Int, id: String) = SleeveBloomOffTurnRequest(
                id,
                a.coordinator.session.sessionId,
                a.coordinator.activeRound!!.roundId,
                state.sleeveBloomWindowId(seat)!!,
                a.controlEpochFor(seat)
            )
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurn(0, requestFor(0, "off-0"), a.clock.nowMillis()).receipt.status
            )
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurn(1, requestFor(1, "off-1"), a.clock.nowMillis()).receipt.status
            )
            val s0 = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            val s1 = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(1)!!
            assertNotEquals(s0.firstTile, s1.firstTile)
            assertNotEquals(s0.secondTile, s1.secondTile)

            val live = a.coordinator.activeRound!!.state
            val skipped = TileMovement.drawNormal(live, 2)
            assertTrue(skipped.tile != s0.firstTile && skipped.tile != s0.secondTile &&
                skipped.tile != s1.firstTile && skipped.tile != s1.secondTile)
            TileMovement.discard(live, 2, skipped.tile)

            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurnChoice(
                    0,
                    SleeveBloomOffTurnChoiceRequest(
                        "choose-0",
                        a.coordinator.session.sessionId,
                        a.coordinator.activeRound!!.roundId,
                        a.controlEpochFor(0),
                        s0.offerId,
                        SleeveBloomKeep.FIRST
                    ),
                    a.clock.nowMillis()
                ).receipt.status
            )
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurnChoice(
                    1,
                    SleeveBloomOffTurnChoiceRequest(
                        "choose-1",
                        a.coordinator.session.sessionId,
                        a.coordinator.activeRound!!.roundId,
                        a.controlEpochFor(1),
                        s1.offerId,
                        SleeveBloomKeep.SECOND
                    ),
                    a.clock.nowMillis()
                ).receipt.status
            )

            val after = a.coordinator.activeRound!!.readState()
            assertEquals(s0.firstTile, after.sleeveBloomReservation(0)!!.tile)
            assertEquals(s1.secondTile, after.sleeveBloomReservation(1)!!.tile)
            val expectedOrder = listOf(s0.secondTile, s1.firstTile)
            var probe = after
            for ((index, expected) in expectedOrder.withIndex()) {
                probe = probe.deepCopy()
                probe.phase = GamePhase.DRAW
                probe.currentPlayer = 2
                probe.players[2].drawnTile = null
                val draw = TileMovement.drawNormal(probe, 2)
                assertEquals(expected, draw.tile, "interleaved unlocked hole $index order")
                TileMovement.discard(probe, 2, draw.tile)
            }
            probe.phase = GamePhase.DRAW
            TestTableBuilder.assertPhysical(probe)
            StateInvariants.assertValid(probe)
        }
    }

    @Test
    fun `clear all sleeve bloom state at round end leaves every tile physical and reachable`() {
        val initial = baseState()
        table(initial).use { a ->
            assertEquals(
                ReceiptStatus.APPLIED,
                a.submitSleeveBloomOffTurn(0, request(a, initial), a.clock.nowMillis()).receipt.status
            )
            val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
            val live = a.coordinator.activeRound!!.state
            val skipped = TileMovement.drawNormal(live, 1)
            TileMovement.discard(live, 1, skipped.tile)
            live.resolveSleeveBloomOffTurnOffer(0, SleeveBloomKeep.FIRST)
            live.clearAllSleeveBloomState()
            assertTrue(live.sleeveBloomOffTurnOffers.isEmpty())
            assertTrue(live.sleeveBloomReservations.isEmpty())
            assertTrue(live.availableWallIndices().contains(live.wallTileIndex(offer.secondTile)))
            assertTrue(live.wallRemaining > 0)
            TestTableBuilder.assertPhysical(live)
            StateInvariants.assertValid(live)
        }
    }

    @Test
    fun `immediate normal bloom uses the second unlocked hole even at exhausted pointer`() {
        for (keep in listOf(SleeveBloomKeep.FIRST, SleeveBloomKeep.SECOND)) {
            val state = TestTableBuilder.create(
                discards = mapOf(
                    0 to listOf(Tile.fromTypeIndex(0, 0)),
                    1 to listOf(Tile.fromTypeIndex(1, 0))
                ),
                phase = GamePhase.DRAW,
                currentPlayer = 2
            ).also {
                it.cheatPoints.fill(30)
                it.openSleeveBloomWindow(0)
                it.openSleeveBloomWindow(1)
                StateInvariants.assertValid(it)
            }
            table(state).use { a ->
                fun requestFor(seat: Int, id: String) = SleeveBloomOffTurnRequest(
                    id,
                    a.coordinator.session.sessionId,
                    a.coordinator.activeRound!!.roundId,
                    state.sleeveBloomWindowId(seat)!!,
                    a.controlEpochFor(seat)
                )
                assertEquals(
                    ReceiptStatus.APPLIED,
                    a.submitSleeveBloomOffTurn(0, requestFor(0, "hole-0"), a.clock.nowMillis()).receipt.status
                )
                assertEquals(
                    ReceiptStatus.APPLIED,
                    a.submitSleeveBloomOffTurn(1, requestFor(1, "hole-1"), a.clock.nowMillis()).receipt.status
                )
                val s0 = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(0)!!
                val s1 = a.coordinator.activeRound!!.readState().sleeveBloomOffTurnOffer(1)!!
                val live = a.coordinator.activeRound!!.state
                val skipped = TileMovement.drawNormal(live, 2)
                TileMovement.discard(live, 2, skipped.tile)

                fun choose(seat: Int, offerId: String, keepValue: SleeveBloomKeep) =
                    a.submitSleeveBloomOffTurnChoice(
                        seat,
                        SleeveBloomOffTurnChoiceRequest(
                            "resolve-$seat",
                            a.coordinator.session.sessionId,
                            a.coordinator.activeRound!!.roundId,
                            a.controlEpochFor(seat),
                            offerId,
                            keepValue
                        ),
                        a.clock.nowMillis()
                    )
                assertEquals(ReceiptStatus.APPLIED, choose(0, s0.offerId, SleeveBloomKeep.FIRST).receipt.status)
                assertEquals(ReceiptStatus.APPLIED, choose(1, s1.offerId, SleeveBloomKeep.FIRST).receipt.status)

                val holeState = a.coordinator.activeRound!!.state
                holeState.phase = GamePhase.DRAW
                holeState.currentPlayer = 2
                holeState.players[2].drawnTile = null
                val firstHole = TileMovement.drawNormal(holeState, 2)
                assertEquals(s0.secondTile, firstHole.tile)
                holeState.turnNumber++
                holeState.lastDrawKind = SleeveBloomDrawKind.NORMAL
                holeState.phase = GamePhase.CHEAT_WINDOW
                // 只剩洞：指针已到末尾，第二候选仍在 wallPointer 之前。
                holeState.wallPointer = holeState.wallEndExclusive
                val secondHoleIndex = holeState.wallTileIndex(s1.secondTile)
                assertEquals(secondHoleIndex, holeState.nextAvailableWallIndex())
                assertTrue(secondHoleIndex < holeState.wallPointer)

                a.syncDecisionsAndClocks(a.clock.nowMillis(), emptySet())
                val activation = SleeveBloomRequest(
                    "hole-immediate-$keep",
                    a.coordinator.session.sessionId,
                    a.coordinator.activeRound!!.roundId,
                    a.coordinator.queryDecision(2).decisionId,
                    a.controlEpochFor(2)
                )
                assertEquals(
                    ReceiptStatus.APPLIED,
                    a.submitSleeveBloom(2, activation, a.clock.nowMillis()).receipt.status
                )
                val offer = a.coordinator.activeRound!!.readState().sleeveBloomOffer!!
                assertEquals(firstHole.tile, offer.firstTile)
                assertEquals(s1.secondTile, offer.secondTile)

                val currentDecision = a.coordinator.queryDecision(2)
                val choice = SleeveBloomChoiceRequest(
                    "hole-choice-$keep",
                    a.coordinator.session.sessionId,
                    a.coordinator.activeRound!!.roundId,
                    currentDecision.decisionId,
                    currentDecision.controlEpoch,
                    offer.offerId,
                    keep
                )
                val chosen = a.submitSleeveBloomChoice(2, choice, a.clock.nowMillis())
                assertEquals(ReceiptStatus.APPLIED, chosen.receipt.status, chosen.receipt.reason)
                val after = a.coordinator.activeRound!!.readState()
                assertNull(after.sleeveBloomOffer)
                assertEquals(
                    if (keep == SleeveBloomKeep.FIRST) firstHole.tile else s1.secondTile,
                    after.players[2].drawnTile
                )
                TestTableBuilder.assertPhysical(after)
                StateInvariants.assertValid(after)
            }
        }
    }

    @Test
    fun `wrong window other seat and duplicate reservation requests are rejected without charge`() {
        val initial = baseState()
        table(initial).use { a ->
            val wrongWindow = request(a, initial).copy(requestId = "wrong-window", windowId = "not-a-window")
            val rejected = a.submitSleeveBloomOffTurn(0, wrongWindow, a.clock.nowMillis())
            assertEquals(ReceiptStatus.REJECTED, rejected.receipt.status)
            assertEquals("SLEEVE_BLOOM_NO_WINDOW", rejected.receipt.reason)
            assertEquals(30, a.coordinator.activeRound!!.readState().cheatPoints[0])

            val otherSeat = SleeveBloomOffTurnRequest(
                "other-seat",
                a.coordinator.session.sessionId,
                a.coordinator.activeRound!!.roundId,
                initial.sleeveBloomWindowId(0)!!,
                a.controlEpochFor(1)
            )
            val wrongSeat = a.submitSleeveBloomOffTurn(1, otherSeat, a.clock.nowMillis())
            assertEquals("SLEEVE_BLOOM_NO_WINDOW", wrongSeat.receipt.reason)
            assertEquals(30, a.coordinator.activeRound!!.readState().cheatPoints[1])
        }
    }
}

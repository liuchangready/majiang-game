package com.majiang.presentation.game

import com.majiang.client.api.*
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Codex-owned pure UI acceptance. No engine, server, protocol or GL dependency. */
class BeamSwapChoiceModelContractTest {
    private fun snapshot(offered: Boolean = true, deadWall: Boolean = false): GameSnapshotView {
        val window = if (deadWall) BeamSwapWindow.DEAD_WALL else BeamSwapWindow.WALL
        val slots: List<BeamSwapSlot> = if (deadWall) {
            (1..13).filter { it != 4 }.map { BeamSwapSlot.DeadWallSlot(it) } + BeamSwapSlot.ReplacementSlot(0)
        } else (32..39).map { BeamSwapSlot.WallSlot(it) }
        val candidates = slots.mapIndexed { index, slot ->
            BeamSwapCandidateView(slot, index == 1, if (index == 1) TestViewBuilder.tile(134) else null)
        }
        val offer = if (offered) BeamSwapOfferView("offer", window, candidates,
            (0..12).toList(), 24_000, "decision", 3) else null
        return GameSnapshotView("session", "round", "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, myClosedTiles = (0..12).map(TestViewBuilder::tile),
                myDrawnTile = TestViewBuilder.tile(13), phase = GamePhaseView.CHEAT_WINDOW, currentTurnPlayer = 0),
            DecisionView("round", "decision", 3), clocks = listOf(ClockInfo(0, 1_000, 24_000, 20_000, 1_000)),
            cheatPoints = listOf(if (offered) 0 else 10, 10, 10, 10),
            controls = listOf(ControlsInfo(0, true, "HUMAN", 3)),
            beamSwap = BeamSwapView(!offered, if (offered) "BEAM_SWAP_ALREADY_PENDING" else null,
                10, window, "decision", 3, 1, offer))
    }
    private fun selectBoth(model: BeamSwapChoiceModel, source: Int = 0,
                           slot: BeamSwapSlot = BeamSwapSlot.WallSlot(32)) {
        assertTrue(model.selectHand(source))
        assertTrue(model.selectTarget(slot))
        assertTrue(model.canConfirm)
    }
    private fun assertUnselected(model: BeamSwapChoiceModel) {
        assertNull(model.selectedHandTileId)
        assertNull(model.selectedTargetSlot)
        assertFalse(model.canConfirm)
    }

    @Test fun `activation is read only and zero balance authoritative offer opens unselected`() {
        val model = BeamSwapChoiceModel { 100_000L }
        val before = snapshot(false)
        model.update(before)
        assertTrue(model.canActivate)
        assertFalse(model.isChoosing)
        assertEquals(BeamSwapRequest("activate", "session", "round", "decision", 3),
            model.buildActivateRequest("activate"))
        assertFalse(model.isChoosing)
        assertEquals(10, before.cheatPoints[0])
        assertNull(model.buildChoiceRequest("premature", BeamSwapAction.KEEP_ORIGINAL))
        model.update(snapshot())
        assertTrue(model.isChoosing)
        assertFalse(model.canActivate)
        assertUnselected(model)
        assertNotNull(model.buildChoiceRequest("cancel", BeamSwapAction.KEEP_ORIGINAL))
    }

    @Test fun `cached canApply cannot authorize activation at or after the decision deadline`() {
        var time = 100_000L
        val model = BeamSwapChoiceModel { time }
        val original = snapshot(false)
        model.update(original)
        assertNotNull(model.buildActivateRequest("before-deadline"))
        time += 22_999
        model.update(original)
        assertTrue(model.canActivate)
        time += 1
        assertFalse(model.canActivate, "The cached server permission expires at its authority deadline")
        assertNull(model.buildActivateRequest("at-deadline"))
        time += 5_000
        model.update(original)
        assertFalse(model.canActivate)
        assertNull(model.buildActivateRequest("after-deadline"))
        assertFalse(model.isChoosing)
    }

    @Test fun `swap requires both selections in either order and keep never carries either field`() {
        for (handFirst in listOf(true, false)) {
            val model = BeamSwapChoiceModel { 100_000L }
            model.update(snapshot())
            assertNull(model.buildChoiceRequest("empty", BeamSwapAction.SWAP))
            assertEquals(BeamSwapChoiceRequest("keep-empty", "session", "round", "decision", 3,
                "offer", BeamSwapAction.KEEP_ORIGINAL), model.buildChoiceRequest("keep-empty", BeamSwapAction.KEEP_ORIGINAL))
            if (handFirst) assertTrue(model.selectHand(0)) else assertTrue(model.selectTarget(BeamSwapSlot.WallSlot(32)))
            assertFalse(model.canConfirm)
            assertNull(model.buildChoiceRequest("partial", BeamSwapAction.SWAP))
            if (handFirst) assertTrue(model.selectTarget(BeamSwapSlot.WallSlot(32))) else assertTrue(model.selectHand(0))
            assertTrue(model.canConfirm)
            assertEquals(BeamSwapChoiceRequest("swap", "session", "round", "decision", 3,
                "offer", BeamSwapAction.SWAP, 0, BeamSwapSlot.WallSlot(32)),
                model.buildChoiceRequest("swap", BeamSwapAction.SWAP))
            val keep = model.buildChoiceRequest("keep-selected", BeamSwapAction.KEEP_ORIGINAL)!!
            assertNull(keep.handTileId)
            assertNull(keep.targetSlot)
            assertTrue(model.isChoosing, "Building a request cannot optimistically close the authority offer")
            assertTrue(model.selectHand(12))
            assertTrue(model.selectTarget(BeamSwapSlot.WallSlot(39)))
            val replacement = model.buildChoiceRequest("replacement", BeamSwapAction.SWAP)!!
            assertEquals(12, replacement.handTileId)
            assertEquals(BeamSwapSlot.WallSlot(39), replacement.targetSlot)
        }
    }

    @Test fun `all thirteen hidden or marked dead wall slots are equally selectable`() {
        val original = snapshot(deadWall = true)
        val candidates = original.beamSwap!!.pendingOffer!!.candidates
        assertEquals(13, candidates.size)
        assertEquals(13, original.beamSwap!!.pendingOffer!!.selectableHandTileIds.size)
        assertEquals(1, candidates.count { it.marked })
        val model = BeamSwapChoiceModel { 100_000L }
        model.update(original)
        assertTrue(model.selectHand(12))
        for ((index, candidate) in candidates.withIndex()) {
            assertTrue(model.selectTarget(candidate.slot), "Candidate $index must remain selectable without a face")
            assertEquals(candidate.slot, model.buildChoiceRequest("target-$index", BeamSwapAction.SWAP)!!.targetSlot)
        }
        assertFalse(model.selectTarget(BeamSwapSlot.DeadWallSlot(0)), "Consumed rinshan prefix is not a target")
        assertFalse(model.selectTarget(BeamSwapSlot.DeadWallSlot(4)), "Revealed indicator is not a target")
        assertFalse(model.selectTarget(BeamSwapSlot.ReplacementSlot(1)))
        assertFalse(model.selectTarget(BeamSwapSlot.WallSlot(1)))
        assertEquals(candidates.last().slot, model.selectedTargetSlot)
        assertTrue(candidates.filterNot { it.marked }.all { it.tile == null })
    }

    @Test fun `current draw all meld kinds foreign tile and noncandidate slot cannot enter selection`() {
        for (kind in MeldTypeView.entries) {
            val tiles = when (kind) {
                MeldTypeView.CHI -> listOf(40, 44, 48)
                MeldTypeView.PON -> listOf(40, 41, 42)
                else -> listOf(40, 41, 42, 43)
            }.map(TestViewBuilder::tile)
            val original = snapshot()
            val offered = original.beamSwap!!.pendingOffer!!
            val changed = original.copy(view = original.view.copy(
                myClosedTiles = original.view.myClosedTiles.take(10),
                melds = listOf(listOf(TestViewBuilder.meld(kind, tiles))) + List(3) { emptyList() }),
                beamSwap = original.beamSwap!!.copy(pendingOffer = offered.copy(selectableHandTileIds = (0..9).toList())))
            val model = BeamSwapChoiceModel { 100_000L }
            model.update(changed)
            selectBoth(model)
            for (id in listOf(13, 120, -1, 136) + tiles.map { it.tileId }) {
                assertFalse(model.selectHand(id), "$kind forbidden source $id")
                assertEquals(0, model.selectedHandTileId)
            }
            for (slot in listOf(BeamSwapSlot.WallSlot(31), BeamSwapSlot.WallSlot(40),
                BeamSwapSlot.DeadWallSlot(1), BeamSwapSlot.ReplacementSlot(0))) {
                assertFalse(model.selectTarget(slot))
                assertEquals(BeamSwapSlot.WallSlot(32), model.selectedTargetSlot)
            }
            assertEquals(0, model.buildChoiceRequest("valid-after-reject", BeamSwapAction.SWAP)!!.handTileId)
        }
    }

    @Test fun `every identity hand or candidate change clears both selections and requests use fresh authority`() {
        val original = snapshot()
        val beam = original.beamSwap!!
        val offer = beam.pendingOffer!!
        val variations = listOf(
            original.copy(sessionId = "new-session"),
            original.copy(syncEpoch = "new-sync"),
            original.copy(roundId = "new-round", decision = original.decision.copy(roundId = "new-round")),
            original.copy(decision = original.decision.copy(decisionId = "new-decision"),
                beamSwap = beam.copy(decisionId = "new-decision", pendingOffer = offer.copy(decisionId = "new-decision"))),
            original.copy(decision = original.decision.copy(controlEpoch = 4), controls = listOf(ControlsInfo(0, true, "HUMAN", 4)),
                beamSwap = beam.copy(controlEpoch = 4, pendingOffer = offer.copy(controlEpoch = 4))),
            original.copy(beamSwap = beam.copy(pendingOffer = offer.copy(offerId = "new-offer"))),
            original.copy(view = original.view.copy(myClosedTiles = original.view.myClosedTiles.dropLast(1) + TestViewBuilder.tile(14)),
                beamSwap = beam.copy(pendingOffer = offer.copy(selectableHandTileIds = (0..11).toList() + 14))),
            original.copy(beamSwap = beam.copy(pendingOffer = offer.copy(candidates = offer.candidates.mapIndexed { i, candidate ->
                if (i == 1) candidate.copy(tile = TestViewBuilder.tile(135)) else candidate
            }))),
            original.copy(beamSwap = beam.copy(pendingOffer = offer.copy(candidates = offer.candidates.mapIndexed { i, candidate ->
                if (i == 1) candidate.copy(marked = false, tile = null) else candidate
            }))),
            original.copy(beamSwap = beam.copy(pendingOffer = offer.copy(candidates = offer.candidates.drop(1))))
        )
        for ((index, changed) in variations.withIndex()) {
            val model = BeamSwapChoiceModel { 100_000L }
            model.update(original)
            selectBoth(model)
            model.update(changed)
            assertTrue(model.isChoosing, "Variation $index still has an authority offer")
            assertUnselected(model)
            assertNull(model.buildChoiceRequest("stale-swap", BeamSwapAction.SWAP))
            val request = model.buildChoiceRequest("fresh-keep", BeamSwapAction.KEEP_ORIGINAL)!!
            assertEquals(changed.sessionId, request.sessionId)
            assertEquals(changed.roundId, request.roundId)
            assertEquals(changed.decision.decisionId, request.decisionId)
            assertEquals(changed.decision.controlEpoch, request.controlEpoch)
            assertEquals(changed.beamSwap!!.pendingOffer!!.offerId, request.offerId)
        }
    }

    @Test fun `same offer and repeated frame updates preserve selections but never renew authority budget`() {
        var time = 100_000L
        val model = BeamSwapChoiceModel { time }
        val original = snapshot()
        model.update(original)
        selectBoth(model)
        assertEquals(23_000L, model.remainingMillis)
        time += 10_000
        model.update(original)
        assertEquals(13_000L, model.remainingMillis)
        assertTrue(model.canConfirm)
        assertEquals(0, model.selectedHandTileId)
        assertEquals(BeamSwapSlot.WallSlot(32), model.selectedTargetSlot)
        val freshClock = original.copy(cursor = 2, clocks = listOf(original.clocks.single().copy(serverNow = 11_000)))
        model.update(freshClock)
        assertEquals(13_000L, model.remainingMillis)
        assertTrue(model.canConfirm)
        time += 13_000
        assertEquals(0L, model.remainingMillis)
        assertTrue(model.isChoosing, "Expiry leaves the modal occupied until the authority clears its offer")
        assertFalse(model.canConfirm)
        assertFalse(model.selectHand(1))
        assertFalse(model.selectTarget(BeamSwapSlot.WallSlot(33)))
        assertNull(model.buildChoiceRequest("late-swap", BeamSwapAction.SWAP))
        assertNull(model.buildChoiceRequest("late-keep", BeamSwapAction.KEEP_ORIGINAL))
    }

    @Test fun `disconnect pause takeover and absent snapshot hide and invalidate local choices`() {
        val original = snapshot()
        for (mode in listOf("disconnect", "pause", "takeover", "missing")) {
            val model = BeamSwapChoiceModel { 100_000L }
            model.update(original)
            selectBoth(model)
            val changed = when (mode) {
                "pause" -> original.copy(paused = true)
                "takeover" -> original.copy(controls = listOf(ControlsInfo(0, true, "TAKEOVER", 3)))
                "missing" -> null
                else -> original
            }
            model.update(changed, connected = mode != "disconnect")
            assertFalse(model.isChoosing)
            assertFalse(model.canActivate)
            assertUnselected(model)
            assertNull(model.buildChoiceRequest("stale-keep", BeamSwapAction.KEEP_ORIGINAL))
            assertNull(model.buildChoiceRequest("stale-swap", BeamSwapAction.SWAP))
            model.update(original)
            assertTrue(model.isChoosing)
            assertUnselected(model)
            assertNotNull(model.buildChoiceRequest("fresh-keep", BeamSwapAction.KEEP_ORIGINAL))
        }
    }

    @Test fun `pause compensation restores only authority remaining duration without old selection`() {
        var time = 100_000L
        val model = BeamSwapChoiceModel { time }
        val original = snapshot()
        model.update(original)
        selectBoth(model)
        time += 5_000
        val paused = original.copy(cursor = 2, paused = true,
            clocks = listOf(original.clocks.single().copy(serverNow = 6_000)))
        model.update(paused)
        assertFalse(model.isChoosing)
        assertUnselected(model)
        time += 100_000
        model.update(paused)
        assertNull(model.buildChoiceRequest("paused", BeamSwapAction.KEEP_ORIGINAL))
        val resumed = original.copy(cursor = 3,
            clocks = listOf(original.clocks.single().copy(serverNow = 106_000, deadline = 124_000)),
            beamSwap = original.beamSwap!!.copy(pendingOffer = original.beamSwap!!.pendingOffer!!.copy(deadlineMillis = 124_000)))
        model.update(resumed)
        assertTrue(model.isChoosing)
        assertUnselected(model)
        assertEquals(18_000L, model.remainingMillis)
        selectBoth(model)
        time += 18_000
        assertEquals(0L, model.remainingMillis)
        assertFalse(model.canConfirm)
        assertNull(model.buildChoiceRequest("expired", BeamSwapAction.SWAP))
    }

    @Test fun `reconnecting with the same cached snapshot cannot restart the old decision budget`() {
        var time = 100_000L
        val model = BeamSwapChoiceModel { time }
        val original = snapshot()
        model.update(original)
        selectBoth(model)
        time += 5_000
        model.update(original, connected = false)
        assertFalse(model.isChoosing)
        assertUnselected(model)
        time += 7_000
        model.update(original, connected = true)
        assertTrue(model.isChoosing)
        assertUnselected(model)
        assertEquals(11_000L, model.remainingMillis,
            "Reusing an authority sample after reconnect must include elapsed disconnected time")
        time += 11_000
        assertNull(model.buildChoiceRequest("expired-cache", BeamSwapAction.KEEP_ORIGINAL))
        assertEquals(0L, model.remainingMillis)
    }

    @Test fun `missing own clock never grants an unlimited choice window`() {
        val model = BeamSwapChoiceModel { 100_000L }
        val original = snapshot()
        model.update(original)
        selectBoth(model)
        model.update(original.copy(cursor = 2, clocks = listOf(ClockInfo(1, 1_000, 24_000, 20_000, 1_000))))
        assertTrue(model.isChoosing, "The authority still owns the modal until it clears the offer")
        assertEquals(0L, model.remainingMillis)
        assertFalse(model.canConfirm)
        assertFalse(model.selectHand(1))
        assertFalse(model.selectTarget(BeamSwapSlot.WallSlot(33)))
        assertNull(model.buildChoiceRequest("no-clock-swap", BeamSwapAction.SWAP))
        assertNull(model.buildChoiceRequest("no-clock-keep", BeamSwapAction.KEEP_ORIGINAL))
        val beforeOffer = snapshot(offered = false)
        for (unavailableClock in listOf(emptyList(),
            listOf(ClockInfo(1, 1_000, 24_000, 20_000, 1_000)),
            listOf(ClockInfo(0, 0, 0, 0, 0)))) {
            val activationModel = BeamSwapChoiceModel { 100_000L }
            activationModel.update(beforeOffer)
            assertTrue(activationModel.canActivate)
            activationModel.update(beforeOffer.copy(cursor = 2, clocks = unavailableClock))
            assertEquals(0L, activationModel.remainingMillis)
            assertFalse(activationModel.canActivate, "Missing or already expired own clock cannot authorize activation")
            assertNull(activationModel.buildActivateRequest("no-clock-activate"))
        }
    }

    @Test fun `shared pending keeps modal visible but blocks all input and submissions until fresh snapshot`() {
        val model = BeamSwapChoiceModel { 100_000L }
        val original = snapshot()
        model.update(original)
        selectBoth(model)
        model.update(original, isPending = true)
        assertTrue(model.isChoosing)
        assertFalse(model.canActivate)
        assertFalse(model.canConfirm)
        assertFalse(model.selectHand(1))
        assertFalse(model.selectTarget(BeamSwapSlot.WallSlot(33)))
        assertNull(model.buildActivateRequest("duplicate-activate"))
        assertNull(model.buildChoiceRequest("duplicate-swap", BeamSwapAction.SWAP))
        assertNull(model.buildChoiceRequest("duplicate-keep", BeamSwapAction.KEEP_ORIGINAL))
        model.update(original, isPending = false)
        assertTrue(model.isChoosing)
        assertNotNull(model.buildChoiceRequest("retry-keep", BeamSwapAction.KEEP_ORIGINAL))
        val cleared = original.copy(cursor = 2, beamSwap = original.beamSwap!!.copy(pendingOffer = null,
            disabledReason = "BEAM_SWAP_ALREADY_USED_THIS_DRAW"))
        model.update(cleared)
        assertFalse(model.isChoosing)
        assertFalse(model.canActivate)
        assertUnselected(model)
        assertNull(model.buildChoiceRequest("gone", BeamSwapAction.SWAP))
    }

    @Test fun `feature absent disabled insufficient funds and local lifecycle block activation`() {
        val original = snapshot(false)
        val variants = listOf(
            original.copy(beamSwap = null),
            original.copy(beamSwap = original.beamSwap!!.copy(canApply = false, disabledReason = "BEAM_SWAP_DISABLED")),
            original.copy(cheatPoints = listOf(9, 10, 10, 10)),
            original.copy(paused = true),
            original.copy(controls = listOf(ControlsInfo(0, true, "TAKEOVER", 3)))
        )
        for (changed in variants) {
            val model = BeamSwapChoiceModel { 100_000L }
            model.update(changed)
            assertFalse(model.canActivate)
            assertNull(model.buildActivateRequest("disabled"))
            assertFalse(model.isChoosing)
        }
        val model = BeamSwapChoiceModel { 100_000L }
        model.update(original, connected = false)
        assertNull(model.buildActivateRequest("disconnected"))
        model.update(original, isPending = true)
        assertNull(model.buildActivateRequest("pending"))
        model.update(original)
        assertNotNull(model.buildActivateRequest("enabled"))
    }
}

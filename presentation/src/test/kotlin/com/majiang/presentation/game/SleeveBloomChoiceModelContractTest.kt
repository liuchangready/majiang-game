package com.majiang.presentation.game

import com.majiang.client.api.*
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SleeveBloomChoiceModelContractTest {
    private fun snapshot(offered: Boolean = true): GameSnapshotView {
        val first = TestViewBuilder.tile(13)
        val second = TestViewBuilder.tile(135)
        val offer = if (offered) SleeveBloomOfferView("offer", first, second, SleeveBloomDrawKind.NORMAL, 24_000, "decision", 3) else null
        return GameSnapshotView("session", "round", "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, myClosedTiles = (0..12).map { TestViewBuilder.tile(it) }, myDrawnTile = first)
                .copy(phase = GamePhaseView.CHEAT_WINDOW, currentTurnPlayer = 0),
            DecisionView("round", "decision", 3), clocks = listOf(ClockInfo(0, 1000, 24000, 20000, 1000)),
            cheatPoints = listOf(if (offered) 0 else 10, 10, 10, 10), controls = listOf(ControlsInfo(0, true, "HUMAN", 3)),
            sleeveBloom = SleeveBloomView(!offered, if (offered) "SLEEVE_BLOOM_ALREADY_PENDING" else null,
                10, SleeveBloomDrawKind.NORMAL, "decision", 3, 1, offer))
    }

    @Test fun `activation request is read only and offer alone opens choice even at zero balance`() {
        val model = SleeveBloomChoiceModel { 100_000L }
        val before = snapshot(false)
        model.update(before)
        assertTrue(model.canActivate)
        assertFalse(model.isChoosing)
        assertEquals(SleeveBloomRequest("activate", "session", "round", "decision", 3), model.buildActivateRequest("activate"))
        assertFalse(model.isChoosing)
        assertEquals(10, before.cheatPoints[0])
        model.update(snapshot())
        assertTrue(model.isChoosing)
        assertNull(model.selected)
        assertFalse(model.canActivate)
        assertFalse(model.canConfirm)
        assertNotNull(model.buildChoiceRequest("cancel", SleeveBloomKeep.FIRST))
        assertTrue(model.select(SleeveBloomKeep.SECOND))
        assertTrue(model.canConfirm)
        assertEquals(SleeveBloomKeep.SECOND, model.selected)
        assertEquals(SleeveBloomChoiceRequest("choose", "session", "round", "decision", 3, "offer", SleeveBloomKeep.SECOND),
            model.buildChoiceRequest("choose", SleeveBloomKeep.SECOND))
        assertTrue(model.select(SleeveBloomKeep.FIRST))
        assertEquals(SleeveBloomKeep.FIRST, model.selected)
    }

    @Test fun `deadline uses authoritative sample without refreshing budget on repeated frame updates`() {
        var time = 100_000L
        val model = SleeveBloomChoiceModel { time }
        val received = snapshot()
        model.update(received)
        assertEquals(23_000L, model.remainingMillis)
        model.select(SleeveBloomKeep.SECOND)
        time += 10_000
        model.update(received)
        assertEquals(13_000L, model.remainingMillis)
        time += 13_000
        assertEquals(0L, model.remainingMillis)
        assertTrue(model.isChoosing, "Clock expiry blocks input while the authoritative offer still owns the modal")
        assertFalse(model.canConfirm)
        assertNull(model.buildChoiceRequest("late", SleeveBloomKeep.FIRST))
        assertNull(model.buildChoiceRequest("late-2", SleeveBloomKeep.SECOND))
        assertFalse(model.select(SleeveBloomKeep.FIRST))
    }

    @Test fun `identity and candidate changes reset local selection while same offer stays visible`() {
        val original = snapshot()
        val variations = listOf(
            original.copy(sessionId = "new-session"), original.copy(syncEpoch = "new-sync"),
            original.copy(roundId = "new-round", decision = original.decision.copy(roundId = "new-round")),
            original.copy(decision = original.decision.copy(decisionId = "new-decision"), sleeveBloom = original.sleeveBloom!!.copy(
                decisionId = "new-decision", pendingOffer = original.sleeveBloom!!.pendingOffer!!.copy(decisionId = "new-decision"))),
            original.copy(decision = original.decision.copy(controlEpoch = 4), controls = listOf(ControlsInfo(0, true, "HUMAN", 4)),
                sleeveBloom = original.sleeveBloom!!.copy(controlEpoch = 4, pendingOffer = original.sleeveBloom!!.pendingOffer!!.copy(controlEpoch = 4))),
            original.copy(sleeveBloom = original.sleeveBloom!!.copy(pendingOffer = original.sleeveBloom!!.pendingOffer!!.copy(offerId = "new-offer"))),
            original.copy(sleeveBloom = original.sleeveBloom!!.copy(pendingOffer = original.sleeveBloom!!.pendingOffer!!.copy(secondTile = TestViewBuilder.tile(134))))
        )
        for (changed in variations) {
            val model = SleeveBloomChoiceModel { 100_000L }
            model.update(original)
            model.select(SleeveBloomKeep.SECOND)
            model.update(changed)
            assertTrue(model.isChoosing)
            assertNull(model.selected)
            assertFalse(model.canConfirm)
            val request = model.buildChoiceRequest("fresh", SleeveBloomKeep.FIRST)!!
            assertEquals(changed.sessionId, request.sessionId)
            assertEquals(changed.decision.decisionId, request.decisionId)
            assertEquals(changed.decision.controlEpoch, request.controlEpoch)
        }
    }

    @Test fun `disconnect pause and takeover hide and invalidate choice but later authority offer restores unselected`() {
        val original = snapshot()
        val model = SleeveBloomChoiceModel { 100_000L }
        for (mode in listOf("disconnect", "pause", "takeover")) {
            model.update(original)
            model.select(SleeveBloomKeep.SECOND)
            val changed = when (mode) {
                "pause" -> original.copy(paused = true)
                "takeover" -> original.copy(controls = listOf(ControlsInfo(0, true, "TAKEOVER", 3)))
                else -> original
            }
            model.update(changed, connected = mode != "disconnect")
            assertFalse(model.isChoosing)
            assertNull(model.selected)
            assertNull(model.buildChoiceRequest("stale", SleeveBloomKeep.FIRST))
            model.update(original)
            assertTrue(model.isChoosing)
            assertNull(model.selected)
        }
    }

    @Test fun `resume uses compensated authority deadline and never includes paused wall time`() {
        var time = 100_000L
        val model = SleeveBloomChoiceModel { time }
        val original = snapshot()
        model.update(original)
        time += 5_000
        val paused = original.copy(cursor = 2, paused = true, clocks = listOf(original.clocks.single().copy(serverNow = 6_000)))
        model.update(paused)
        assertFalse(model.isChoosing)
        time += 100_000
        model.update(paused)
        assertNull(model.buildChoiceRequest("paused", SleeveBloomKeep.FIRST))
        val resumed = original.copy(cursor = 3, clocks = listOf(original.clocks.single().copy(serverNow = 106_000, deadline = 124_000)),
            sleeveBloom = original.sleeveBloom!!.copy(pendingOffer = original.sleeveBloom!!.pendingOffer!!.copy(deadlineMillis = 124_000)))
        model.update(resumed)
        assertTrue(model.isChoosing)
        assertEquals(18_000L, model.remainingMillis)
        assertNotNull(model.buildChoiceRequest("resumed", SleeveBloomKeep.FIRST))
        time += 18_000
        assertEquals(0L, model.remainingMillis)
        assertNull(model.buildChoiceRequest("expired", SleeveBloomKeep.FIRST))
    }

    @Test fun `pending blocks every submission and authoritative offer clearing closes modal`() {
        val model = SleeveBloomChoiceModel { 100_000L }
        val original = snapshot()
        model.update(original)
        model.select(SleeveBloomKeep.SECOND)
        model.update(original, pending = true)
        assertFalse(model.canConfirm)
        assertNull(model.buildChoiceRequest("duplicate", SleeveBloomKeep.FIRST))
        assertNull(model.buildActivateRequest("duplicate-activate"))
        val cleared = original.copy(cursor = 2, sleeveBloom = original.sleeveBloom!!.copy(pendingOffer = null,
            disabledReason = "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW"))
        model.update(cleared)
        assertFalse(model.isChoosing)
        assertNull(model.selected)
        assertNull(model.buildChoiceRequest("gone", SleeveBloomKeep.SECOND))
    }
}

package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.badlogic.gdx.math.Rectangle
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

/** Actual screen input, delayed replies, authoritative pushes and maximum-size layout. */
class BeamSwapScreenContractTest : GdxHeadlessTestRunner() {
    class BeamSession(val fake: FakeGameSession) : GameSession by fake {
        val activations = mutableListOf<BeamSwapRequest>()
        val choices = mutableListOf<BeamSwapChoiceRequest>()
        private fun result(id: String): CompletableFuture<Receipt> {
            val result = fake.submitResult ?: CompletableFuture.completedFuture(Receipt(id, ReceiptStatus.APPLIED))
            fake.isPending = true
            result.whenComplete { _, _ -> fake.isPending = false }
            return result
        }
        override fun submitBeamSwap(request: BeamSwapRequest): CompletableFuture<Receipt> {
            activations += request
            return result(request.requestId)
        }
        override fun submitBeamSwapChoice(request: BeamSwapChoiceRequest): CompletableFuture<Receipt> {
            choices += request
            return result(request.requestId)
        }
    }
    fun snapshot(offered: Boolean = false): GameSnapshotView {
        val tiles = (0..13).map { TestViewBuilder.tile(it) }
        val actions = tiles.map { LegalActionView.DiscardTile(0, it) }
        val decision = DecisionView("round", if (offered) "choice" else "activation", 0, actions,
            actions.mapIndexed { i, action -> ActionOptionView("discard-$i", action) })
        val slots = (1..13).filter { it != 4 }.map { BeamSwapSlot.DeadWallSlot(it) } + BeamSwapSlot.ReplacementSlot(0)
        val offer = if (offered) BeamSwapOfferView("offer", BeamSwapWindow.DEAD_WALL,
            slots.mapIndexed { i, slot -> BeamSwapCandidateView(slot, i == 3, if (i == 3) TestViewBuilder.tile(135) else null) },
            tiles.take(13).map { it.tileId }, 24_000, decision.decisionId, 0) else null
        return GameSnapshotView("session", "round", "epoch", if (offered) 2 else 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, phase = GamePhaseView.CHEAT_WINDOW,
                myClosedTiles = tiles.take(13), myDrawnTile = tiles.last(), legalActions = actions), decision,
            clocks = listOf(ClockInfo(0, 1000, 24000, 20000, 1000)), cheatPoints = List(4) { if (offered) 0 else 10 },
            controls = listOf(ControlsInfo(0, true, "HUMAN", 0)),
            beamSwap = BeamSwapView(!offered, if (offered) "BEAM_SWAP_ALREADY_PENDING" else null, 10,
                BeamSwapWindow.DEAD_WALL, decision.decisionId, 0, if (offered) 2 else 1, offer))
    }
    private fun click(screen: GameScreen, rect: Rectangle) {
        val x = rect.x + rect.width / 2
        val y = rect.y + rect.height / 2
        if (Gdx.graphics == null || Gdx.graphics.width == 0) screen.touchDown(x.toInt(), y.toInt(), 0, 0)
        else screen.touchDown((x * Gdx.graphics.width / 1280f).toInt(), ((720f - y) * Gdx.graphics.height / 720f).toInt(), 0, 0)
    }

    @Test fun `real entry double selection and eventless push work even when pushes precede receipts`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val session = BeamSession(fake)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, session, FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.beamSwapPanel
            val delayed = CompletableFuture<Receipt>()
            fake.submitResult = delayed
            click(screen, panel.btnOpenRect)
            click(screen, panel.btnOpenRect)
            assertEquals(1, session.activations.size)
            assertFalse(panel.model.isChoosing)
            assertEquals(10, screen.currentSnapshot!!.cheatPoints[0])
            fake.publish(snapshot(true)); fake.flushUi()
            assertTrue(panel.model.isChoosing)
            delayed.complete(Receipt(session.activations.single().requestId, ReceiptStatus.APPLIED)); fake.flushUi()
            val target = panel.targetCardRects.keys.first()
            click(screen, panel.targetCardRects.getValue(target))
            assertFalse(panel.model.canConfirm)
            val hand = panel.handCardRects.keys.first()
            click(screen, panel.handCardRects.getValue(hand))
            assertTrue(panel.model.canConfirm)
            val choosing = CompletableFuture<Receipt>()
            fake.submitResult = choosing
            click(screen, panel.btnConfirmRect)
            click(screen, panel.btnConfirmRect)
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(1, session.choices.size)
            assertEquals(BeamSwapAction.SWAP, session.choices.single().action)
            assertEquals(hand, session.choices.single().handTileId)
            assertEquals(target, session.choices.single().targetSlot)
            assertTrue(fake.submittedChoices.isEmpty())
            val offered = snapshot(true)
            val cleared = offered.copy(cursor = 3,
                view = offered.view.copy(myClosedTiles = offered.view.myClosedTiles.map {
                    if (it.tileId == hand) TestViewBuilder.tile(134) else it
                }), beamSwap = offered.beamSwap!!.copy(pendingOffer = null, disabledReason = "BEAM_SWAP_ALREADY_USED_THIS_DRAW"))
            fake.publish(cleared); fake.flushUi()
            choosing.complete(Receipt(session.choices.single().requestId, ReceiptStatus.APPLIED)); fake.flushUi()
            assertFalse(panel.model.isChoosing)
            assertEquals(offered.view.myDrawnTile, screen.currentSnapshot!!.view.myDrawnTile)
            assertEquals(0, screen.currentSnapshot!!.cheatPoints[0])
        } finally { screen.dispose(); assertTrue(screen.beamSwapPanel.isDisposed); assets.dispose() }
    }

    @Test fun `maximum candidate and hand hit boxes are distinct contained and exclude drawn tile`() {
        val fake = FakeGameSession(initialSnapshot = snapshot(true))
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, BeamSession(fake), FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.beamSwapPanel
            assertEquals(13, panel.targetCardRects.size)
            assertEquals(13, panel.handCardRects.size)
            assertFalse(panel.handCardRects.containsKey(13))
            val targetKey = panel.targetCardRects.keys.first()
            val originalTargetRect = Rectangle(panel.targetCardRects.getValue(targetKey))
            panel.targetCardRects.getValue(targetKey).set(-100f, -100f, 1f, 1f)
            assertEquals(originalTargetRect, panel.targetCardRects.getValue(targetKey), "Layout readers must not mutate target hit boxes")
            val handKey = panel.handCardRects.keys.first()
            val originalHandRect = Rectangle(panel.handCardRects.getValue(handKey))
            panel.handCardRects.getValue(handKey).set(-100f, -100f, 1f, 1f)
            assertEquals(originalHandRect, panel.handCardRects.getValue(handKey), "Layout readers must not mutate hand hit boxes")
            val originalConfirm = Rectangle(panel.btnConfirmRect)
            panel.btnConfirmRect.set(-100f, -100f, 1f, 1f)
            assertEquals(originalConfirm, panel.btnConfirmRect, "Layout readers must not move the submit button")
            val rectangles = panel.targetCardRects.values + panel.handCardRects.values + listOf(panel.btnConfirmRect, panel.btnKeepRect)
            val viewport = Rectangle(0f, 0f, 1280f, 720f)
            for ((i, rect) in rectangles.withIndex()) {
                assertTrue(viewport.contains(rect), "Out of bounds $rect")
                assertTrue(rect.width >= 44f && rect.height >= 44f, "Touch target too small: $rect")
                for (other in rectangles.drop(i + 1)) assertFalse(rect.overlaps(other), "$rect overlaps $other")
            }
            for (slot in panel.targetCardRects.keys.toList()) {
                click(screen, panel.targetCardRects.getValue(slot))
                assertEquals(slot, panel.model.selectedTargetSlot)
            }
            for (hand in panel.handCardRects.keys.toList()) {
                click(screen, panel.handCardRects.getValue(hand))
                assertEquals(hand, panel.model.selectedHandTileId)
            }
            val buttons = listOf(panel.btnOpenRect, screen.sleeveBloomPanel.btnOpenRect, screen.tileMarkPanel.btnOpenRect,
                screen.btnSortRect, screen.btnTakeoverRect)
            for ((i, rect) in buttons.withIndex()) for (other in buttons.drop(i + 1))
                assertFalse(rect.overlaps(other), "Action buttons overlap: $rect $other")
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test fun `actual server rejection codes have Chinese explanations`() {
        val reasons = listOf(
            "BEAM_SWAP_DISABLED", "BEAM_SWAP_RIICHI_LOCKED", "BEAM_SWAP_DEALER_EXTRA_EXCLUDED",
            "BEAM_SWAP_NO_DRAWN_TILE", "BEAM_SWAP_NO_HAND_TILES", "BEAM_SWAP_ALREADY_PENDING",
            "BEAM_SWAP_ALREADY_USED_THIS_DRAW", "BEAM_SWAP_NO_CANDIDATES", "BEAM_SWAP_NO_PENDING_OFFER",
            "BEAM_SWAP_OFFER_MISMATCH", "BEAM_SWAP_STALE_OFFER", "BEAM_SWAP_CHOICE_PENDING",
            "BEAM_SWAP_INVALID_CHOICE", "BEAM_SWAP_INVALID_HAND_TILE", "BEAM_SWAP_INVALID_TARGET_SLOT",
            "SLEEVE_BLOOM_ALREADY_PENDING", "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW",
            "INSUFFICIENT_FUNDS", "DEADLINE_EXPIRED", "DECISION_MISMATCH", "NOT_YOUR_TURN",
            "SEAT_NOT_HUMAN", "ACTION_IN_FLIGHT", "SESSION_CLOSED", "STALE_CONTROL_EPOCH",
            "WRONG_PHASE", "NO_ACTIVE_DECISION", "HOST_PAUSED", "REQUEST_ID_CONFLICT"
        )
        for (reason in reasons) {
            val text = BeamSwapPanel.translateServerReason(reason)
            assertNotEquals(reason, text)
            assertTrue(text.any { it.code > 127 }, "Missing Chinese explanation: $reason")
        }
    }

    @Test fun `modal locks table hand other cheats and sorting while escape requests keep exactly once`() {
        val fake = FakeGameSession(initialSnapshot = snapshot(true))
        val session = BeamSession(fake)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, session, FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.beamSwapPanel
            val sorted = screen.isAutoSort
            click(screen, panel.btnConfirmRect)
            click(screen, screen.refreshHandTileRects().first().rect)
            click(screen, screen.btnSortRect)
            click(screen, screen.sleeveBloomPanel.btnOpenRect)
            click(screen, screen.tileMarkPanel.btnOpenRect)
            click(screen, Rectangle(1f, 1f, 10f, 10f))
            assertTrue(session.choices.isEmpty())
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(fake.submittedBlooms.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
            assertFalse(screen.tileMarkPanel.model.isSelecting)
            assertEquals(sorted, screen.isAutoSort)
            assertTrue(panel.model.isChoosing)
            val pending = CompletableFuture<Receipt>()
            fake.submitResult = pending
            screen.keyDown(Input.Keys.ESCAPE)
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(1, session.choices.size)
            assertEquals(BeamSwapAction.KEEP_ORIGINAL, session.choices.single().action)
            assertNull(session.choices.single().handTileId)
            assertNull(session.choices.single().targetSlot)
            assertFalse(screen.isDisposed)
            pending.complete(Receipt(session.choices.single().requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")); fake.flushUi()
            assertTrue(panel.model.isChoosing, "Rejected receipt cannot locally clear authority offer")
        } finally { screen.dispose(); assets.dispose() }
    }
}

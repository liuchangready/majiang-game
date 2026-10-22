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

class SleeveBloomScreenContractTest : GdxHeadlessTestRunner() {
    private fun snapshot(offered: Boolean = false): GameSnapshotView {
        val tiles = (0..13).map { TestViewBuilder.tile(it) }
        val actions = tiles.map { LegalActionView.DiscardTile(0, it) }
        val decision = DecisionView("round", if (offered) "choice" else "activation", 0, actions,
            actions.mapIndexed { i, action -> ActionOptionView("discard-$i", action) })
        val offer = if (offered) SleeveBloomOfferView("offer", tiles.last(), TestViewBuilder.tile(135),
            SleeveBloomDrawKind.NORMAL, 24_000, decision.decisionId, 0) else null
        return GameSnapshotView("session", "round", "epoch", if (offered) 2 else 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, phase = GamePhaseView.CHEAT_WINDOW,
                myClosedTiles = tiles.take(13), myDrawnTile = tiles.last(), legalActions = actions), decision,
            clocks = listOf(ClockInfo(0, 1000, 24000, 20000, 1000)), cheatPoints = List(4) { if (offered) 0 else 10 },
            controls = listOf(ControlsInfo(0, true, "HUMAN", 0)),
            sleeveBloom = SleeveBloomView(!offered, if (offered) "SLEEVE_BLOOM_ALREADY_PENDING" else null, 10,
                SleeveBloomDrawKind.NORMAL, decision.decisionId, 0, if (offered) 2 else 1, offer))
    }

    private fun click(screen: GameScreen, rect: Rectangle) {
        val x = rect.x + rect.width / 2
        val y = rect.y + rect.height / 2
        if (Gdx.graphics == null || Gdx.graphics.width == 0) screen.touchDown(x.toInt(), y.toInt(), 0, 0)
        else screen.touchDown((x * Gdx.graphics.width / 1280f).toInt(), ((720f - y) * Gdx.graphics.height / 720f).toInt(), 0, 0)
    }

    @Test fun `actual entry submits only once and private push opens choice before activation receipt`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.sleeveBloomPanel
            assertFalse(panel.btnOpenRect.overlaps(screen.btnTakeoverRect))
            val delayed = CompletableFuture<Receipt>()
            fake.submitResult = delayed
            click(screen, panel.btnOpenRect)
            click(screen, panel.btnOpenRect)
            assertEquals(1, fake.submittedBlooms.size)
            assertEquals(0, fake.claimControlCalls)
            assertEquals(0, fake.releaseControlCalls)
            assertFalse(panel.model.isChoosing)
            assertEquals(10, screen.currentSnapshot!!.cheatPoints[0])
            fake.publish(snapshot(true)); fake.flushUi()
            assertTrue(panel.model.isChoosing)
            assertTrue(fake.submittedChoices.isEmpty())
            delayed.complete(Receipt(fake.submittedBlooms.single().requestId, ReceiptStatus.APPLIED))
            fake.flushUi()
            click(screen, panel.secondCardRect)
            assertEquals(SleeveBloomKeep.SECOND, panel.model.selected)
            val choosing = CompletableFuture<Receipt>()
            fake.submitResult = choosing
            click(screen, panel.btnConfirmRect)
            click(screen, panel.btnConfirmRect)
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(1, fake.submittedBloomChoices.size)
            assertEquals(SleeveBloomKeep.SECOND, fake.submittedBloomChoices.single().keep)
            assertTrue(fake.submittedChoices.isEmpty())
            val offered = snapshot(true)
            val cleared = offered.copy(cursor = 3, view = offered.view.copy(myDrawnTile = TestViewBuilder.tile(135)),
                sleeveBloom = offered.sleeveBloom!!.copy(pendingOffer = null, disabledReason = "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW"))
            fake.publish(cleared); fake.flushUi()
            choosing.complete(Receipt(fake.submittedBloomChoices.single().requestId, ReceiptStatus.APPLIED))
            fake.flushUi()
            assertFalse(panel.model.isChoosing)
            assertEquals(135, screen.currentSnapshot!!.view.myDrawnTile!!.tileId)
            assertEquals(0, screen.currentSnapshot!!.cheatPoints[0])
        } finally { screen.dispose(); assertTrue(screen.sleeveBloomPanel.isDisposed); assets.dispose() }
    }

    @Test fun `pending offer intercepts hand and outside clicks and escape chooses first without quitting`() {
        val fake = FakeGameSession(initialSnapshot = snapshot(true))
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.sleeveBloomPanel
            assertTrue(panel.model.isChoosing)
            click(screen, panel.btnConfirmRect)
            assertTrue(fake.submittedBloomChoices.isEmpty())
            click(screen, screen.refreshHandTileRects().first().rect)
            click(screen, Rectangle(1f, 1f, 10f, 10f))
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(panel.model.isChoosing)
            val pending = CompletableFuture<Receipt>()
            fake.submitResult = pending
            screen.keyDown(Input.Keys.ESCAPE)
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(1, fake.submittedBloomChoices.size)
            assertEquals(SleeveBloomKeep.FIRST, fake.submittedBloomChoices.single().keep)
            assertFalse(screen.isDisposed)
            assertTrue(fake.submittedChoices.isEmpty())
            pending.complete(Receipt(fake.submittedBloomChoices.single().requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED"))
            fake.flushUi()
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test fun `disabled bloom button cannot switch control and the separate control button still works`() {
        val initial = snapshot().let { it.copy(sleeveBloom = it.sleeveBloom!!.copy(canApply = false, disabledReason = "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW")) }
        val fake = FakeGameSession(initialSnapshot = initial)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            assertFalse(screen.sleeveBloomPanel.btnOpenRect.overlaps(screen.btnTakeoverRect))
            assertFalse(screen.sleeveBloomPanel.btnOpenRect.overlaps(screen.btnSortRect))
            assertFalse(screen.btnSortRect.overlaps(screen.btnTakeoverRect))
            click(screen, screen.sleeveBloomPanel.btnOpenRect)
            assertTrue(fake.submittedBlooms.isEmpty())
            assertEquals(0, fake.releaseControlCalls)
            assertEquals(0, fake.claimControlCalls)
            assertTrue(fake.submittedChoices.isEmpty())
            assertFalse(screen.isAutoSort)
            click(screen, screen.btnSortRect)
            assertTrue(screen.isAutoSort)
            assertEquals(0, fake.releaseControlCalls)
            click(screen, screen.btnTakeoverRect)
            fake.flushUi()
            assertEquals(1, fake.releaseControlCalls)
            fake.publish(initial.copy(cursor = 2, controls = listOf(ControlsInfo(0, true, "TAKEOVER", 1)),
                decision = initial.decision.copy(controlEpoch = 1), sleeveBloom = initial.sleeveBloom!!.copy(controlEpoch = 1)))
            fake.flushUi()
            click(screen, screen.btnTakeoverRect)
            fake.flushUi()
            assertEquals(1, fake.claimControlCalls)
        } finally { screen.dispose(); assets.dispose() }
    }

    private fun toast(screen: GameScreen): String? = GameScreen::class.java.getDeclaredField("toastMessage").let {
        it.isAccessible = true
        it.get(screen) as String?
    }

    @Test fun `disabled entry explains authority reason without sending an action`() {
        val initial = snapshot().let { it.copy(sleeveBloom = it.sleeveBloom!!.copy(
            canApply = false, disabledReason = "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW")) }
        val fake = FakeGameSession(initialSnapshot = initial)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            click(screen, screen.sleeveBloomPanel.btnOpenRect)
            assertTrue(fake.submittedBlooms.isEmpty())
            assertTrue(toast(screen)?.contains("本次摸牌已施展过") == true,
                "A disabled entry must explain its authoritative reason")
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test fun `authority timeout closes offer and explains kept first without refund`() {
        // The authority can clear the offer on the deadline tick without ever publishing
        // an intermediate snapshot that contains both an expired clock and the offer.
        val offered = snapshot(true)
        val fake = FakeGameSession(initialSnapshot = offered)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            assertTrue(screen.sleeveBloomPanel.model.isChoosing)
            assertNull(toast(screen), "An open offer must not announce settlement")
            val tile = offered.view.myDrawnTile!!
            val resolved = offered.copy(cursor = 3,
                view = offered.view.copy(phase = GamePhaseView.RESPONSE_WINDOW, myDrawnTile = null,
                    legalActions = emptyList(), discards = listOf(listOf(tile), emptyList(), emptyList(), emptyList()),
                    lastDiscard = tile, lastDiscardPlayer = 0),
                decision = DecisionView("round", "response", 0),
                clocks = listOf(ClockInfo(1, 24_000, 47_000, 20_000, 24_001)),
                sleeveBloom = null)
            fake.publish(resolved)
            assertFalse(screen.sleeveBloomPanel.model.isChoosing)
            val message = toast(screen).orEmpty()
            assertTrue(message.contains("保留第一张") && message.contains("不退"), message)
            assertEquals(0, screen.currentSnapshot!!.cheatPoints[0])
            assertTrue(fake.submittedBloomChoices.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test fun `manual completion or new round never announces offer timeout`() {
        for (newRound in listOf(false, true)) {
            val offered = snapshot(true).let { if (newRound) it.copy(
                clocks = listOf(it.clocks.single().copy(serverNow = 24_001))) else it }
            val fake = FakeGameSession(initialSnapshot = offered)
            val assets = TestUiAssets.create()
            val screen = GameScreen(assets, fake, FakeTableNavigation())
            try {
                screen.startGame()
                if (!newRound) click(screen, screen.sleeveBloomPanel.btnCancelRect)
                val resolved = if (newRound) offered.copy(cursor = 3, roundId = "next-round",
                    decision = DecisionView("next-round", "next-decision", 0), sleeveBloom = null)
                else offered.copy(cursor = 3, sleeveBloom = offered.sleeveBloom!!.copy(pendingOffer = null))
                fake.publish(resolved)
                assertFalse(screen.sleeveBloomPanel.model.isChoosing)
                assertFalse(toast(screen).orEmpty().contains("超时"))
            } finally { screen.dispose(); assets.dispose() }
        }
    }

    @Test fun `terminal stale or binding reset offer clearing never masquerades as timeout`() {
        for (cause in listOf("terminal", "stale", "stale-after-deadline", "binding")) {
            val offered = snapshot(true)
            val fake = FakeGameSession(initialSnapshot = offered)
            val assets = TestUiAssets.create()
            val screen = GameScreen(assets, fake, FakeTableNavigation())
            try {
                screen.startGame()
                val cleared = offered.copy(cursor = 3, sleeveBloom = null)
                val resolved = when (cause) {
                    "terminal" -> cleared.copy(view = cleared.view.copy(phase = GamePhaseView.ROUND_END),
                        decision = DecisionView("round", "terminal", 0), clocks = emptyList())
                    "stale" -> cleared.copy(view = cleared.view.copy(myDrawnTile = TestViewBuilder.tile(134)))
                    "stale-after-deadline" -> cleared.copy(view = cleared.view.copy(myDrawnTile = TestViewBuilder.tile(134)),
                        clocks = listOf(ClockInfo(1, 24_000, 47_000, 20_000, 24_001)))
                    else -> cleared.copy(sessionId = "replacement", syncEpoch = "replacement-epoch")
                }
                fake.publish(resolved)
                assertFalse(screen.sleeveBloomPanel.model.isChoosing)
                assertFalse(toast(screen).orEmpty().contains("超时"), cause)
            } finally { screen.dispose(); assets.dispose() }
        }
    }

    @Test fun `panel geometry separates cards buttons and entry and actual server rejection codes have Chinese explanations`() {
        val panel = SleeveBloomPanel(FakeGameSession(initialSnapshot = snapshot(true)))
        try {
            assertEquals(Rectangle(1078f, 110f, 174f, 46f), panel.btnOpenRect)
            assertTrue(panel.panelRect.contains(panel.firstCardRect))
            assertTrue(panel.panelRect.contains(panel.secondCardRect))
            assertTrue(panel.panelRect.contains(panel.btnConfirmRect))
            assertTrue(panel.panelRect.contains(panel.btnCancelRect))
            assertFalse(panel.firstCardRect.overlaps(panel.secondCardRect))
            for (card in listOf(panel.firstCardRect, panel.secondCardRect)) {
                assertFalse(card.overlaps(panel.btnConfirmRect))
                assertFalse(card.overlaps(panel.btnCancelRect))
            }
            val reasons = listOf(
                "SLEEVE_BLOOM_DISABLED", "SLEEVE_BLOOM_RIICHI_LOCKED", "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED",
                "SLEEVE_BLOOM_NO_DRAWN_TILE", "SLEEVE_BLOOM_ALREADY_PENDING", "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW",
                "SLEEVE_BLOOM_WALL_EMPTY", "SLEEVE_BLOOM_NO_PENDING_OFFER", "SLEEVE_BLOOM_OFFER_MISMATCH",
                "SLEEVE_BLOOM_STALE_OFFER", "SLEEVE_BLOOM_CHOICE_PENDING", "INSUFFICIENT_FUNDS",
                "DEADLINE_EXPIRED", "DECISION_MISMATCH", "NOT_YOUR_TURN", "SEAT_NOT_HUMAN",
                "ACTION_IN_FLIGHT", "SESSION_CLOSED", "STALE_CONTROL_EPOCH", "WRONG_PHASE",
                "NO_ACTIVE_DECISION", "HOST_PAUSED", "REQUEST_ID_CONFLICT"
            )
            for (reason in reasons) {
                val text = SleeveBloomPanel.translateServerReason(reason)
                assertNotEquals(reason, text)
                assertTrue(text.any { it.code > 127 }, "Missing Chinese explanation: $reason")
            }
        } finally { panel.dispose() }
    }
}

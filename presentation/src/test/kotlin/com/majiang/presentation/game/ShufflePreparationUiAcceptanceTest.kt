package com.majiang.presentation.game

import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

class ShufflePreparationUiAcceptanceTest {

    private fun view(round: String = "one", balance: Int = 50) = ShufflePreparationView(
        roundId = round,
        status = ShufflePreparationStatusView.SELECTING,
        opensAtMillis = 0,
        deadlineMillis = 10000,
        serverNowMillis = 0,
        confirmed = false,
        selection = null,
        reservedPoints = 0,
        availablePoints = balance
    )

    @Test fun `server error translation covers standard server reasons and invalid reason fallbacks`() {
        val requiredReasons = listOf(
            "UNSUPPORTED", "TIMEOUT", "TIMED_OUT", "INSUFFICIENT", "NOT_AVAILABLE",
            "STALE", "ROUND", "DISCONNECTED", "CLOSED", "ALREADY", "ILLEGAL",
            "INVALID", "REJECT", "PAUSED", "EXPIRED", "ERROR", "网络异常", "断开连接"
        )
        for (reason in requiredReasons) {
            val translated = ShufflePreparationPanel.translateServerReason(reason)
            assertTrue(translated.isNotBlank(), "Translation for $reason must not be blank")
            assertFalse(translated.contains("?"), "Translation for $reason must not contain placeholder")
        }
        assertEquals("请求被拒绝", ShufflePreparationPanel.translateServerReason(null))
        assertEquals("请求被拒绝", ShufflePreparationPanel.translateServerReason(""))
        assertEquals("请求被拒绝", ShufflePreparationPanel.translateServerReason("   "))
    }

    @Test fun `two techniques cost per technique with mixed targets and insufficient balance`() {
        val m = ShufflePreparationModel { 0L }
        m.update(view(), nowSystemMillis = 0)
        m.toggleInitialTarget(ShuffleTarget.KOKUSHI)
        m.toggleInitialTarget(ShuffleTarget.DAISUUSHII)
        assertEquals(40, m.totalCost)
        assertEquals(10, m.remainingPointsPreview)
        assertTrue(m.hasMixedTargetsNotice)
        m.toggleDrawTarget(ShuffleTarget.DAISANGEN)
        assertEquals(60, m.totalCost)
        assertFalse(m.canConfirm)
        assertNull(m.buildPlanRequest())
        assertTrue(m.canSkip)
        assertEquals(0, m.buildSkipRequest()!!.selection.cost())
        m.toggleInitialTarget(ShuffleTarget.KOKUSHI)
        m.toggleInitialTarget(ShuffleTarget.DAISUUSHII)
        assertEquals(20, m.totalCost)
        assertTrue(m.canConfirm)
    }

    @Test fun `same round preserves draft and fresh round clears it`() {
        val m = ShufflePreparationModel { 0L }
        m.update(view(), epoch = 3, nowSystemMillis = 100)
        m.toggleInitialTarget(ShuffleTarget.CHINITSU_SOU)
        m.update(view().copy(serverNowMillis = 1000), epoch = 4, nowSystemMillis = 1100)
        assertEquals(setOf(ShuffleTarget.CHINITSU_SOU), m.initialTargets)
        assertEquals(4L, m.buildPlanRequest()!!.controlEpoch)
        m.update(view("two"), epoch = 5, nowSystemMillis = 1200)
        assertTrue(m.initialTargets.isEmpty())
        assertEquals("two", m.buildPlanRequest()!!.roundId)
    }

    @Test fun `reconnect restores locked choice without subtracting reservation twice`() {
        val selection = ShuffleSelectionView(initialTargets = setOf(ShuffleTargetView.KOKUSHI))
        val m = ShufflePreparationModel { 0L }
        m.update(view(balance = 10).copy(confirmed = true, selection = selection, reservedPoints = 40), nowSystemMillis = 0)
        assertEquals(ShuffleSelection(initialTargets = setOf(ShuffleTarget.KOKUSHI)), m.confirmedSelection)
        assertTrue(m.isLocked)
        assertFalse(m.toggleInitialTarget(ShuffleTarget.DAISANGEN))
        assertEquals(10, m.remainingPointsPreview, "availablePoints already excludes the reserved cost")
    }

    @Test fun `countdown uses authority sample and freezes while paused`() {
        val m = ShufflePreparationModel { 0L }
        m.update(view().copy(serverNowMillis = 2100), nowSystemMillis = 100000)
        assertEquals(8L, m.remainingSeconds(100000))
        assertEquals(7L, m.remainingSeconds(101100))
        m.update(view().copy(serverNowMillis = 3200), paused = true, nowSystemMillis = 101100)
        assertEquals(7L, m.remainingSeconds(201100))
        assertFalse(m.canConfirm, "paused setup cannot submit")
        assertFalse(m.canSkip)
    }

    @Test fun `timeout never submits draft and generation locks editing`() {
        val m = ShufflePreparationModel { 0L }
        m.update(view(), nowSystemMillis = 0)
        m.toggleDrawTarget(ShuffleTarget.DAISANGEN)
        m.update(view().copy(status = ShufflePreparationStatusView.GENERATING, serverNowMillis = 10000), nowSystemMillis = 10000)
        assertEquals(0L, m.remainingSeconds(10000))
        assertFalse(m.isAuthorityConfirmed)
        assertNull(m.buildPlanRequest())
        assertFalse(m.toggleDrawTarget(ShuffleTarget.KOKUSHI))
        m.update(null, nowSystemMillis = 10001)
        assertFalse(m.isActive)
    }

    @Test fun `skip receipt clears drafted targets and old request cannot unlock current submission`() {
        val m = ShufflePreparationModel { 0L }
        m.update(view(), nowSystemMillis = 0)
        m.toggleInitialTarget(ShuffleTarget.KOKUSHI)
        val skip = m.buildSkipRequest("skip")!!
        m.markSubmitting(skip.requestId)
        m.onSubmissionAccepted(skip.requestId, skip.selection)
        assertTrue(m.isLocked)
        assertTrue(m.initialTargets.isEmpty())
        assertEquals(50, m.remainingPointsPreview)
        m.update(view("next"), nowSystemMillis = 0)
        m.markSubmitting("new")
        m.onSubmissionRejected("skip", "old failure")
        assertTrue(m.isSubmitting)
        assertNull(m.errorMessage)
    }

    @Test fun `repeated cached sample cannot restart countdown and expiry blocks confirmation`() {
        var now = 0L
        val m = ShufflePreparationModel { now }
        val prep = view()
        m.update(prep)
        now = 9000
        m.update(prep)
        assertEquals(1L, m.remainingSeconds())
        now = 10000
        assertFalse(m.canConfirm)
        assertFalse(m.canSkip)
        assertFalse(m.toggleInitialTarget(ShuffleTarget.KOKUSHI))
    }

    @Test fun `panel captures outside clicks and pending confirm submits once`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.model.update(view(), nowSystemMillis = System.nanoTime() / 1_000_000L)
            assertTrue(panel.touchDown(0f, 0f))
            val target = panel.btnInitialTargets.getValue(ShuffleTarget.KOKUSHI)
            panel.touchDown(target.x + 2, target.y + 2)
            val confirm = panel.btnConfirmRect
            repeat(2) { panel.touchDown(confirm.x + 2, confirm.y + 2) }
            assertEquals(1, session.submittedPlans.size)
            assertEquals(40, session.submittedPlans.single().selection.cost())
            completion.complete(Receipt(session.submittedPlans.single().requestId, ReceiptStatus.RECORDED))
            assertTrue(panel.model.isLocked)
        } finally { panel.dispose() }
    }

    @Test fun `panel borrows 17px font from UiAssets and headless safe without GL`() {
        val session = FakeGameSession()
        val headlessPanel = ShufflePreparationPanel(session)
        assertNull(headlessPanel.getOrInitChineseFont(), "Headless without GL/assets must safely return null")
        headlessPanel.dispose()
        assertTrue(headlessPanel.isDisposed)

        val font17 = com.badlogic.gdx.graphics.g2d.BitmapFont()
        val font18 = com.badlogic.gdx.graphics.g2d.BitmapFont()
        val assets = com.majiang.presentation.testing.TestUiAssets.createWithFonts(
            chineseFont = font18,
            chineseFont17 = font17
        )
        val panel = ShufflePreparationPanel(session, assets)
        assertSame(font17, panel.getOrInitChineseFont(), "Must borrow 17px font from UiAssets")
        panel.dispose()
        assertTrue(panel.isDisposed)
        // Ensure shared font is not destroyed by panel.dispose()
        assertEquals(1.0f, font17.data.scaleX)
        assets.dispose()
    }
}

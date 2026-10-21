package com.majiang.presentation.game

import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleSelectionView
import com.majiang.client.api.ShuffleTargetView
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 开局准备阶段纯状态模型契约测试 (ShufflePreparationModelTest)。
 *
 * 覆盖正式 UI 契约：默认无目标卡、点偷天换日才展开、费用 0/10/30/40、资金不足、
 * 失败恢复原页、抽屉返回清目标保留看破、新局/重连复位、deadline 不被重置、迟到回执不污染。
 */
class ShufflePreparationModelTest {

    private fun view(
        round: String = "one",
        balance: Int = 100,
        seeThrough: Boolean = false,
        confirmed: Boolean = false,
        selection: ShuffleSelectionView? = null,
        serverNow: Long = 0L,
        deadline: Long = 10_000L,
        status: ShufflePreparationStatusView = ShufflePreparationStatusView.SELECTING
    ) = ShufflePreparationView(
        roundId = round,
        status = status,
        opensAtMillis = 0L,
        deadlineMillis = deadline,
        serverNowMillis = serverNow,
        confirmed = confirmed,
        selection = selection,
        reservedPoints = 0,
        availablePoints = balance,
        seeThroughEnabled = seeThrough,
        seeThroughCost = 10
    )

    @Test
    fun `default entry shows the compact panel with no target and zero cost`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(), nowSystemMillis = 0L)

        assertEquals(ShufflePreparationModel.PreparationPage.PANEL, model.page)
        assertNull(model.target, "默认不得预选任何目标牌型")
        assertFalse(model.isSeeThroughSelected)
        assertEquals(0, model.totalCost)
        assertEquals("开始本局 · 0点", model.primaryActionLabel)
        assertEquals(ShufflePreparationModel.PreparationPrimaryAction.START_ZERO_COST, model.primaryAction)
        assertTrue(model.canSubmitPrimary)

        val plan = model.buildPrimaryRequest("zero")!!
        assertNull(plan.selection.target)
        assertFalse(plan.selection.seeThrough)
        assertEquals(0, plan.selection.cost())
    }

    @Test
    fun `opening the target drawer does not select a target and never submits`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(), nowSystemMillis = 0L)

        assertTrue(model.openTargetDrawer())
        assertEquals(ShufflePreparationModel.PreparationPage.DRAWER, model.page)
        assertNull(model.target, "抽屉打开时不得默认清一色")
        assertEquals(0, model.totalCost)
        assertFalse(model.canConfirmTarget, "没有目标时禁止确认施术")
        assertEquals("先选择一个目标牌型", model.targetConfirmDisabledReason)
    }

    @Test
    fun `drawer exposes exactly the six existing targets and toggles a single selection`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(), nowSystemMillis = 0L)
        model.openTargetDrawer()

        val targets = com.majiang.client.api.ShuffleTarget.entries
        assertEquals(6, targets.size)
        for (target in targets) {
            assertTrue(model.selectTarget(target))
            assertEquals(target, model.target)
            assertEquals(30, model.totalCost)
            // 重复点击同一张卡即取消。
            assertTrue(model.selectTarget(target))
            assertNull(model.target)
            assertEquals(0, model.totalCost)
        }
    }

    @Test
    fun `costs cover zero ten thirty and forty point combinations`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 100, seeThrough = true), nowSystemMillis = 0L)

        assertEquals(0, model.totalCost)
        assertTrue(model.toggleSeeThrough())
        assertEquals(10, model.totalCost)
        assertEquals("确认看破 · 10点", model.primaryActionLabel)

        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.KOKUSHI)
        assertEquals(40, model.totalCost)
        assertEquals("确认施术 · 40点", model.drawerConfirmLabel)

        model.toggleSeeThrough()
        assertEquals(30, model.totalCost)
        assertEquals("确认施术 · 30点", model.drawerConfirmLabel)
    }

    @Test
    fun `insufficient funds blocks submission but keeps editing available`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 20), nowSystemMillis = 0L)
        model.openTargetDrawer()

        assertTrue(model.selectTarget(com.majiang.client.api.ShuffleTarget.DAISANGEN))
        assertFalse(model.isAffordable)
        assertFalse(model.canConfirmTarget, "点数不足必须阻止提交")
        assertEquals("点数不足", model.confirmDisabledReason)
        assertNull(model.buildDrawerConfirmRequest("poor"))
        // 仍然允许编辑草稿。
        assertTrue(model.selectTarget(com.majiang.client.api.ShuffleTarget.DAISANGEN))
        assertNull(model.target)
        assertEquals(0, model.totalCost)
        assertTrue(model.canSkip)
    }

    @Test
    fun `closing the drawer clears the target keeps see through and never submits`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 100, seeThrough = true), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.CHINITSU_PIN)
        model.toggleSeeThrough()

        assertTrue(model.closeTargetDrawer())
        assertEquals(ShufflePreparationModel.PreparationPage.PANEL, model.page)
        assertNull(model.target, "返回必须清空目标")
        assertTrue(model.isSeeThroughSelected, "返回必须保留看破")
        assertEquals(10, model.totalCost)
        assertNull(model.submittingRequestId, "返回不得提交任何请求")
        assertFalse(model.isSubmitting)
    }

    @Test
    fun `reopening the drawer never restores a default target`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.CHINITSU_MAN)
        model.closeTargetDrawer()
        model.openTargetDrawer()
        assertNull(model.target)
    }

    @Test
    fun `failed submission restores the originating page and the legal draft`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 100, seeThrough = true), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.KOKUSHI)

        val plan = model.buildDrawerConfirmRequest("drawer-req")!!
        model.markSubmitting(plan.requestId, model.page)
        model.onSubmissionRejected(plan.requestId, "点数不足")

        assertEquals(ShufflePreparationModel.PreparationPage.DRAWER, model.page, "抽屉提交失败必须留在抽屉")
        assertEquals(com.majiang.client.api.ShuffleTarget.KOKUSHI, model.target, "失败必须保留合法草稿")
        assertEquals("点数不足", model.errorMessage)
        assertFalse(model.isSubmitting)
        assertTrue(model.canConfirmTarget)
    }

    @Test
    fun `late callbacks from an older request never pollute the current draft`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.CHINITSU_SOU)

        val stale = model.buildDrawerConfirmRequest("stale")!!
        model.markSubmitting("current")
        model.onSubmissionRejected(stale.requestId, "旧请求失败")

        assertTrue(model.isSubmitting, "旧回执不得解除当前提交态")
        assertNull(model.errorMessage, "旧回执不得污染当前错误提示")
        assertEquals(com.majiang.client.api.ShuffleTarget.CHINITSU_SOU, model.target)
    }

    @Test
    fun `a callback arriving after the round was reset is ignored`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view("one"), nowSystemMillis = 0L)
        val plan = model.buildSkipRequest("old-round")!!
        model.markSubmitting(plan.requestId)

        // 新局复位：草稿与提交态全部作废。
        model.update(view("two"), nowSystemMillis = 0L)
        assertFalse(model.isSubmitting)
        assertNull(model.submittingRequestId)

        model.onSubmissionAccepted(plan.requestId, plan.selection)
        assertFalse(model.isLocallyConfirmed, "旧局回执不得确认新局")
        assertNull(model.errorMessage)
    }

    @Test
    fun `a new round resets the draft and the page`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view("one", balance = 100, seeThrough = true), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.DAISUUSHII)
        model.toggleSeeThrough()

        model.update(view("two", balance = 100, seeThrough = true), nowSystemMillis = 0L)
        assertEquals(ShufflePreparationModel.PreparationPage.PANEL, model.page)
        assertNull(model.target)
        assertFalse(model.isSeeThroughSelected)
        assertEquals(0, model.totalCost)
        assertEquals("two", model.buildPrimaryRequest()!!.roundId)
    }

    @Test
    fun `reconnect returns to the default panel without stitching old selections`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 100, seeThrough = true), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.CHINITSU_MAN)

        model.onReconnect()
        assertEquals(ShufflePreparationModel.PreparationPage.PANEL, model.page)
        assertNull(model.target)
        assertFalse(model.isSeeThroughSelected)
    }

    @Test
    fun `reconnect with an authority confirmed selection shows the locked receipt`() {
        val model = ShufflePreparationModel { 0L }
        model.update(
            view(balance = 70, confirmed = true, selection = ShuffleSelectionView(target = ShuffleTargetView.KOKUSHI)),
            nowSystemMillis = 0L
        )
        assertTrue(model.isAuthorityConfirmed)
        assertTrue(model.isLocked)
        assertEquals(com.majiang.client.api.ShuffleTarget.KOKUSHI, model.confirmedSelection!!.target)
        assertEquals(70, model.remainingPointsPreview, "权威已扣除预留，不得二次扣减")
        assertEquals("方案已确认", model.confirmedReceiptTitle)
        assertEquals("等待其他玩家完成准备", model.confirmedFeedbackText)
    }

    @Test
    fun `same round refresh drawer return and pause never restart the deadline`() {
        var now = 0L
        val model = ShufflePreparationModel { now }
        model.update(view(), nowSystemMillis = 0L)
        val deadline = model.deadlineMillis

        now = 3_000L
        model.update(view(serverNow = 3_000L), nowSystemMillis = 3_000L)
        assertEquals(deadline, model.deadlineMillis)
        assertEquals(7L, model.remainingSeconds(3_000L))

        model.openTargetDrawer()
        model.closeTargetDrawer()
        assertEquals(deadline, model.deadlineMillis)
        assertEquals(7L, model.remainingSeconds(3_000L))

        model.update(view(serverNow = 3_000L), paused = true, nowSystemMillis = 3_000L)
        assertEquals(deadline, model.deadlineMillis)
        assertEquals(7L, model.remainingSeconds(60_000L), "暂停时倒计时冻结")
        assertFalse(model.canConfirm)
        assertFalse(model.canSkip)
    }

    @Test
    fun `expired preparation blocks editing and submission`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(serverNow = 9_999L), nowSystemMillis = 9_999L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.DAISANGEN)

        model.update(view(serverNow = 10_000L), nowSystemMillis = 10_000L)
        assertTrue(model.isTimeExpired)
        assertFalse(model.canEditDraft)
        assertFalse(model.canConfirmTarget)
        assertFalse(model.selectTarget(com.majiang.client.api.ShuffleTarget.KOKUSHI))
        assertNull(model.buildPrimaryRequest("late"))
    }

    @Test
    fun `generating status locks editing until the authority ends preparation`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(), nowSystemMillis = 0L)
        model.update(view(status = ShufflePreparationStatusView.GENERATING), nowSystemMillis = 0L)
        assertFalse(model.canEditDraft)
        assertFalse(model.canConfirm)
        assertEquals("正在发牌…", model.confirmedFeedbackText)

        model.update(null, nowSystemMillis = 1L)
        assertFalse(model.isActive)
        assertNull(model.confirmedFeedbackText)
    }

    @Test
    fun `primary request switches between zero cost see through and target plans`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 100, seeThrough = true), nowSystemMillis = 0L)

        val skip = model.buildPrimaryRequest("a")!!
        assertEquals(0, skip.selection.cost())

        model.toggleSeeThrough()
        val seeThrough = model.buildPrimaryRequest("b")!!
        assertTrue(seeThrough.selection.seeThrough)
        assertNull(seeThrough.selection.target)
        assertEquals(10, seeThrough.selection.cost())

        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.CHINITSU_SOU)
        val target = model.buildPrimaryRequest("c")!!
        assertEquals(com.majiang.client.api.ShuffleTarget.CHINITSU_SOU, target.selection.target)
        assertEquals(40, target.selection.cost())
    }

    @Test
    fun `confirmed receipt keeps the submitted plan read only`() {
        val model = ShufflePreparationModel { 0L }
        model.update(view(balance = 100), nowSystemMillis = 0L)
        model.openTargetDrawer()
        model.selectTarget(com.majiang.client.api.ShuffleTarget.CHINITSU_MAN)
        val plan = model.buildDrawerConfirmRequest("confirm")!!
        model.markSubmitting(plan.requestId, model.page)
        model.onSubmissionAccepted(plan.requestId, plan.selection)

        assertTrue(model.isLocallyConfirmed)
        assertEquals(ShufflePreparationModel.PreparationPage.PANEL, model.page, "成功后抽屉收起为回执")
        assertEquals(com.majiang.client.api.ShuffleTarget.CHINITSU_MAN, model.confirmedSelection!!.target)
        assertEquals(30, model.confirmedSelection!!.cost())
        assertEquals("等待其他玩家完成准备", model.confirmedFeedbackText)
        assertFalse(model.canEditDraft)
        assertNotNull(model.confirmedReceiptTitle)
    }
}

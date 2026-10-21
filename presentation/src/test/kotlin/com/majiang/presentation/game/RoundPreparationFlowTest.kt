package com.majiang.presentation.game

import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleTarget
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.presentation.testing.FakeGameSession
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

/**
 * 正式 APP 开局准备阶段的端到端交互流程测试 (RoundPreparationFlowTest)。
 *
 * 覆盖：默认无目标卡、点偷天换日才展开抽屉、六种目标、抽屉返回 / 零费跳过、
 * 费用 0/10/30/40 与资金不足、失败恢复原页、新局 / 同局 / 重连、
 * deadline 不被重置、迟到回执、按下松开与拖出取消、键盘确认与 Esc 回栏、
 * 动画命中一致与减少动画直达终态。
 */
class RoundPreparationFlowTest : GdxHeadlessTestRunner() {

    private fun view(
        round: String = "one",
        balance: Int = 100,
        seeThrough: Boolean = false,
        confirmed: Boolean = false,
        deadline: Long = 10_000L,
        serverNow: Long = 0L
    ) = ShufflePreparationView(
        roundId = round,
        status = ShufflePreparationStatusView.SELECTING,
        opensAtMillis = 0L,
        deadlineMillis = deadline,
        serverNowMillis = serverNow,
        confirmed = confirmed,
        selection = null,
        reservedPoints = 0,
        availablePoints = balance,
        seeThroughEnabled = seeThrough,
        seeThroughCost = 10
    )

    private fun center(rect: com.badlogic.gdx.math.Rectangle) = Pair(rect.x + rect.width / 2f, rect.y + rect.height / 2f)

    private fun press(panel: ShufflePreparationPanel, rect: com.badlogic.gdx.math.Rectangle) {
        val (x, y) = center(rect)
        panel.pressDown(x, y)
        panel.pressUp(x, y)
    }

    @Test
    fun `default entry shows the compact panel and no target cards`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(), paused = false, epoch = 0L)
            assertTrue(panel.isActive)
            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel.model.page)
            assertNull(panel.model.target)
            // 默认状态下抽屉内的目标卡不可命中（抽屉尚未展开）。
            val card = panel.btnTargets.getValue(ShuffleTarget.KOKUSHI)
            panel.pressDown(card.x + 4f, card.y + 4f)
            panel.pressUp(card.x + 4f, card.y + 4f)
            assertNull(panel.model.target, "默认不得展开六选一")
            assertEquals(0, panel.model.totalCost)
            assertEquals("开始本局 · 0点", panel.model.primaryActionLabel)
        } finally { panel.dispose() }
    }

    @Test
    fun `clicking the cheat entry expands the six target drawer`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            assertEquals(ShufflePreparationModel.PreparationPage.DRAWER, panel.model.page)
            assertNull(panel.model.target, "抽屉默认不得预选目标")
            assertEquals(6, panel.btnTargets.size)

            // 六种目标都可以手选，且重复点击取消。
            for (target in ShuffleTarget.entries) {
                press(panel, panel.btnTargets.getValue(target))
                assertEquals(target, panel.model.target)
                press(panel, panel.btnTargets.getValue(target))
                assertNull(panel.model.target)
            }
        } finally { panel.dispose() }
    }

    @Test
    fun `primary action submits a zero cost plan in one click`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnPrimaryRect)
            assertEquals(1, session.submittedPlans.size)
            val plan = session.submittedPlans.single()
            assertNull(plan.selection.target)
            assertFalse(plan.selection.seeThrough)
            assertEquals(0, plan.selection.cost())
            assertTrue(panel.model.isLocked)
        } finally { panel.dispose() }
    }

    @Test
    fun `see through alone costs ten points and stacks with a target for forty`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(seeThrough = true), paused = false, epoch = 0L)
            press(panel, panel.btnSeeThroughRect)
            assertTrue(panel.model.isSeeThroughSelected)
            assertEquals(10, panel.model.totalCost)
            assertEquals("确认看破 · 10点", panel.model.primaryActionLabel)

            press(panel, panel.btnPrimaryRect)
            assertEquals(1, session.submittedPlans.size)
            assertEquals(10, session.submittedPlans.single().selection.cost())
            assertTrue(session.submittedPlans.single().selection.seeThrough)
        } finally { panel.dispose() }

        val session2 = FakeGameSession()
        val panel2 = ShufflePreparationPanel(session2)
        try {
            panel2.update(view(seeThrough = true), paused = false, epoch = 0L)
            press(panel2, panel2.btnOpenTargetRect)
            press(panel2, panel2.btnTargets.getValue(ShuffleTarget.DAISANGEN))
            press(panel2, panel2.btnDrawerSeeThroughRect)
            assertEquals(40, panel2.model.totalCost)
            press(panel2, panel2.btnDrawerConfirmRect)
            assertEquals(1, session2.submittedPlans.size)
            assertEquals(40, session2.submittedPlans.single().selection.cost())
            assertTrue(session2.submittedPlans.single().selection.seeThrough)
            assertEquals(ShuffleTarget.DAISANGEN, session2.submittedPlans.single().selection.target)
        } finally { panel2.dispose() }
    }

    @Test
    fun `drawer confirm is disabled until a target is chosen`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(0, session.submittedPlans.size, "没有目标时确认必须禁用")
            assertEquals("先选择一个目标牌型", panel.model.targetConfirmDisabledReason)

            press(panel, panel.btnTargets.getValue(ShuffleTarget.CHINITSU_PIN))
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(1, session.submittedPlans.size)
            assertEquals(30, session.submittedPlans.single().selection.cost())
        } finally { panel.dispose() }
    }

    @Test
    fun `drawer back clears the target keeps see through and submits nothing`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(seeThrough = true), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.CHINITSU_MAN))
            press(panel, panel.btnDrawerSeeThroughRect)
            press(panel, panel.btnDrawerBackRect)

            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel.model.page)
            assertNull(panel.model.target)
            assertTrue(panel.model.isSeeThroughSelected)
            assertEquals(0, session.submittedPlans.size, "返回不得提交")
        } finally { panel.dispose() }
    }

    @Test
    fun `drawer direct start clears every draft and submits the zero cost skip`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(seeThrough = true), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            press(panel, panel.btnDrawerSeeThroughRect)
            press(panel, panel.btnDrawerSkipRect)

            assertEquals(1, session.submittedPlans.size)
            val plan = session.submittedPlans.single()
            assertNull(plan.selection.target)
            assertFalse(plan.selection.seeThrough)
            assertEquals(0, plan.selection.cost())
        } finally { panel.dispose() }
    }

    @Test
    fun `insufficient funds blocks the submission but keeps the draft editable`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(balance = 20), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.DAISANGEN))
            assertFalse(panel.model.isAffordable)
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(0, session.submittedPlans.size, "点数不足必须阻止提交")

            // 仍可编辑：取消目标后可以零费用开局。
            press(panel, panel.btnTargets.getValue(ShuffleTarget.DAISANGEN))
            assertNull(panel.model.target)
            press(panel, panel.btnDrawerSkipRect)
            assertEquals(1, session.submittedPlans.size)
            assertEquals(0, session.submittedPlans.single().selection.cost())
        } finally { panel.dispose() }
    }

    @Test
    fun `a rejected submission restores the originating page and the draft`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(1, session.submittedPlans.size)
            assertTrue(panel.model.isSubmitting)

            completion.complete(
                Receipt(
                    session.submittedPlans.single().requestId,
                    ReceiptStatus.REJECTED,
                    reason = "INSUFFICIENT_FUNDS"
                )
            )
            assertFalse(panel.model.isSubmitting)
            assertEquals(ShufflePreparationModel.PreparationPage.DRAWER, panel.model.page, "失败必须回到发起页")
            assertEquals(ShuffleTarget.KOKUSHI, panel.model.target, "失败必须保留合法草稿")
            assertNotNull(panel.model.errorMessage)
        } finally { panel.dispose() }
    }

    @Test
    fun `repeated submissions are blocked while a request is in flight`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnPrimaryRect)
            press(panel, panel.btnPrimaryRect)
            assertEquals(1, session.submittedPlans.size, "提交中必须阻止重复提交")
            completion.complete(Receipt(session.submittedPlans.single().requestId, ReceiptStatus.RECORDED))
            assertTrue(panel.model.isLocked)
        } finally { panel.dispose() }
    }

    @Test
    fun `a new round resets the draft while the same round keeps it`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view("one"), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.CHINITSU_SOU))
            assertEquals(ShuffleTarget.CHINITSU_SOU, panel.model.target)

            // 同局刷新：草稿保留。
            panel.update(view("one", serverNow = 1_000L), paused = false, epoch = 1L)
            assertEquals(ShuffleTarget.CHINITSU_SOU, panel.model.target)

            // 新局：草稿与页面全部复位。
            panel.update(view("two", serverNow = 1_000L), paused = false, epoch = 2L)
            assertNull(panel.model.target)
            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel.model.page)
            assertEquals("two", panel.model.buildPrimaryRequest()!!.roundId)
        } finally { panel.dispose() }
    }

    @Test
    fun `reconnect shows the locked receipt or the default panel`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.DAISUUSHII))

            panel.onReconnect()
            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel.model.page)
            assertNull(panel.model.target)

            panel.update(
                view(confirmed = true).copy(selection = com.majiang.client.api.ShuffleSelectionView()),
                paused = false,
                epoch = 3L
            )
            assertTrue(panel.model.isAuthorityConfirmed)
            assertTrue(panel.model.isLocked)
        } finally { panel.dispose() }
    }

    @Test
    fun `the preparation deadline is never restarted by drawer toggles or a pause`() {
        var now = 0L
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(deadline = 10_000L), paused = false, epoch = 0L)
            val deadline = panel.model.deadlineMillis
            now = 4_000L
            panel.update(view(deadline = 10_000L, serverNow = 4_000L), paused = false, epoch = 1L)
            assertEquals(deadline, panel.model.deadlineMillis)
            assertEquals(6L, panel.model.remainingSeconds(now))

            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnDrawerBackRect)
            assertEquals(deadline, panel.model.deadlineMillis)
            assertEquals(6L, panel.model.remainingSeconds(now))

            panel.update(view(deadline = 10_000L, serverNow = 4_000L), paused = true, epoch = 2L)
            assertEquals(deadline, panel.model.deadlineMillis)
            assertEquals(6L, panel.model.remainingSeconds(60_000L))
        } finally { panel.dispose() }
    }

    @Test
    fun `a late receipt after the round was reset never confirms the new round`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view("one"), paused = false, epoch = 0L)
            press(panel, panel.btnPrimaryRect)
            val staleRequestId = session.submittedPlans.single().requestId

            panel.update(view("two"), paused = false, epoch = 1L)
            completion.complete(Receipt(staleRequestId, ReceiptStatus.RECORDED))

            assertFalse(panel.model.isLocallyConfirmed, "旧局回执不得确认新局")
            assertFalse(panel.model.isAuthorityConfirmed)
            assertNull(panel.model.confirmedSelection)
        } finally { panel.dispose() }
    }

    @Test
    fun `a late success receipt from the previous round leaves no trace in the new round`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view("one", seeThrough = true), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            press(panel, panel.btnDrawerSeeThroughRect)
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(1, session.submittedPlans.size)
            val staleRequestId = session.submittedPlans.single().requestId

            // 新局开始：旧局草稿与回执全部作废。
            panel.update(view("two", seeThrough = true), paused = false, epoch = 1L)
            panel.syncGeometry()
            assertNull(panel.model.target, "新局不得继承旧局目标")
            assertFalse(panel.model.isSeeThroughSelected, "新局不得继承旧局看破")
            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel.model.page)

            // 旧局的成功回执迟到：必须完全没有副作用。
            completion.complete(Receipt(staleRequestId, ReceiptStatus.RECORDED))
            panel.syncGeometry()

            assertFalse(panel.isReceiptVisible, "旧局回执不得在新局锁存回执")
            assertFalse(panel.model.isLocallyConfirmed, "旧局回执不得确认新局")
            assertFalse(panel.model.isAuthorityConfirmed)
            assertNull(panel.model.confirmedSelection, "旧局回执不得把旧方案写进新局")
            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel.model.page, "旧局回执不得收回新局抽屉")
            assertNull(panel.model.target, "旧局回执不得写回新局目标")
            assertFalse(panel.model.isSeeThroughSelected, "旧局回执不得写回新局看破")
            assertNull(panel.model.errorMessage, "旧局回执不得在新局显示错误")
        } finally { panel.dispose() }
    }

    @Test
    fun `a late rejected direct start from the previous round cannot roll a draft into the new round`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view("one", seeThrough = true), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.CHINITSU_PIN))
            press(panel, panel.btnDrawerSeeThroughRect)
            press(panel, panel.btnDrawerSkipRect)
            assertEquals(1, session.submittedPlans.size)
            val staleRequestId = session.submittedPlans.single().requestId

            // 新局开始。
            panel.update(view("two", seeThrough = true), paused = false, epoch = 1L)
            panel.syncGeometry()

            // 旧局的失败回执迟到：不得把旧局草稿回滚进新局。
            completion.complete(
                Receipt(staleRequestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            )
            panel.syncGeometry()

            assertNull(panel.model.target, "旧局草稿不得回滚进新局")
            assertFalse(panel.model.isSeeThroughSelected, "旧局看破草稿不得回滚进新局")
            assertEquals(0, panel.model.totalCost)
            assertNull(panel.model.errorMessage, "旧局失败不得在新局显示错误")
            assertFalse(panel.isReceiptVisible, "旧局失败回执不得锁存回执")
            assertFalse(panel.model.isSubmitting)
        } finally { panel.dispose() }
    }

    @Test
    fun `the see-through entry is hidden entirely when the room disables it`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(seeThrough = false), paused = false, epoch = 0L)
            assertFalse(panel.isSeeThroughEntryVisible, "禁用房间不得提供看破入口")
            press(panel, panel.btnSeeThroughRect)
            assertFalse(panel.model.isSeeThroughSelected)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnDrawerSeeThroughRect)
            assertFalse(panel.model.isSeeThroughSelected, "抽屉内同样不得提供看破入口")
            assertEquals(0, panel.model.totalCost)
        } finally { panel.dispose() }

        val enabled = ShufflePreparationPanel(FakeGameSession())
        try {
            enabled.update(view(seeThrough = true), paused = false, epoch = 0L)
            assertTrue(enabled.isSeeThroughEntryVisible)
        } finally { enabled.dispose() }
    }

    @Test
    fun `the receipt never claims to wait once the authority ended the preparation`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnPrimaryRect)
            assertTrue(panel.model.isLocallyConfirmed)
            panel.syncGeometry()
            assertTrue(panel.isReceiptVisible, "确认后必须出现只读回执")
            assertTrue(
                panel.receiptFeedbackText.contains("等待其他玩家完成准备"),
                "权威仍在准备阶段时才显示等待: " + panel.receiptFeedbackText
            )

            // 权威准备阶段结束（单机立即发牌）。
            panel.update(null, paused = false, epoch = 1L)
            panel.syncGeometry()
            assertFalse(
                panel.receiptFeedbackText.contains("等待"),
                "发牌后不得继续宣称等待: " + panel.receiptFeedbackText
            )
            assertEquals("本局准备已完成", panel.receiptFeedbackText)
        } finally { panel.dispose() }
    }

    @Test
    fun `a receipt that arrives before the first render stays fully opaque on the first real frame`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        var now = 1_000_000L
        val panel = ShufflePreparationPanel(session, clock = { now })
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnPrimaryRect)
            assertEquals(1, session.submittedPlans.size)

            // 提交成功回执与 preparation=null 快照都在**首次渲染之前**到达。
            completion.complete(Receipt(session.submittedPlans.single().requestId, ReceiptStatus.RECORDED))
            panel.update(null, paused = false, epoch = 1L)
            panel.syncGeometry()

            // 模拟窗口暂停 / 掉帧 / 后台恢复：权威快照之后隔了很久才真正渲染。
            now += 5_000L
            panel.syncGeometry()

            assertTrue(
                panel.isReceiptVisible,
                "首个真实渲染帧之前回执不能过期，否则用户根本看不到「方案已确认」"
            )
            assertEquals(1.0f, panel.receiptAlpha, 0.0001f, "首个真实渲染帧必须完全不透明")
            assertFalse(
                panel.receiptFeedbackText.contains("等待"),
                "已发牌后回执不得宣称等待: " + panel.receiptFeedbackText
            )

            // 首个真实渲染帧：回执被画出，淡出时钟从此刻开始。
            panel.markReceiptRendered()
            assertEquals(1.0f, panel.receiptAlpha, 0.0001f, "刚绘制完的回执必须完全不透明")

            // 之后按约 150ms 淡出并及时消失。
            now += ShufflePreparationPanel.RECEIPT_FADE_MS.toLong() + 40L
            assertFalse(panel.isReceiptVisible, "回执必须在约 150ms 内淡出消失")
            assertEquals(0.0f, panel.receiptAlpha, 0.0001f)
        } finally { panel.dispose() }
    }

    @Test
    fun `a receipt drawn while the preparation is still active fades only after the authority ends`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        var now = 2_000_000L
        val panel = ShufflePreparationPanel(session, clock = { now })
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnPrimaryRect)
            completion.complete(Receipt(session.submittedPlans.single().requestId, ReceiptStatus.RECORDED))
            panel.syncGeometry()

            // 权威仍在准备阶段（其他玩家未确认）：回执常驻且完全不透明。
            assertTrue(panel.isReceiptVisible)
            assertEquals(1.0f, panel.receiptAlpha, 0.0001f)
            panel.markReceiptRendered()
            now += 10_000L
            panel.syncGeometry()
            assertTrue(panel.isReceiptVisible, "准备阶段内回执必须常驻")
            assertEquals(1.0f, panel.receiptAlpha, 0.0001f, "准备阶段内回执必须完全不透明")
            assertTrue(panel.receiptFeedbackText.contains("等待其他玩家完成准备"))

            // 权威准备阶段结束：从结束时刻起约 150ms 淡出。
            panel.update(null, paused = false, epoch = 1L)
            panel.syncGeometry()
            assertTrue(panel.isReceiptVisible, "准备刚结束时回执仍要显示")
            now += ShufflePreparationPanel.RECEIPT_FADE_MS.toLong() + 40L
            assertFalse(panel.isReceiptVisible, "回执必须在约 150ms 内淡出消失")
        } finally { panel.dispose() }
    }

    @Test
    fun `releasing outside the pressed control cancels the action`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            val (x, y) = center(panel.btnPrimaryRect)
            panel.pressDown(x, y)
            panel.pressUp(x + 400f, y + 300f)
            assertEquals(0, session.submittedPlans.size, "拖出按钮必须取消")
        } finally { panel.dispose() }
    }

    @Test
    fun `dragging out of a control cancels the pending press`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            val (x, y) = center(panel.btnPrimaryRect)
            panel.pressDown(x, y)
            panel.pressDragged(x + 300f, y)
            panel.pressUp(x + 300f, y)
            assertEquals(0, session.submittedPlans.size, "拖出后松开不得触发")
        } finally { panel.dispose() }
    }

    @Test
    fun `keyboard confirms the current page and escape returns from the drawer`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            assertTrue(panel.handleConfirmKey())
            assertEquals(1, session.submittedPlans.size)
            assertEquals(0, session.submittedPlans.single().selection.cost())
        } finally { panel.dispose() }

        val session2 = FakeGameSession()
        val panel2 = ShufflePreparationPanel(session2)
        try {
            panel2.update(view(seeThrough = true), paused = false, epoch = 0L)
            press(panel2, panel2.btnOpenTargetRect)
            press(panel2, panel2.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            press(panel2, panel2.btnDrawerSeeThroughRect)
            assertTrue(panel2.model.isSeeThroughSelected)
            assertTrue(panel2.handleEscape())
            assertEquals(ShufflePreparationModel.PreparationPage.PANEL, panel2.model.page)
            assertNull(panel2.model.target, "Esc 必须清空目标")
            assertTrue(panel2.model.isSeeThroughSelected, "Esc 必须保留看破")
            assertEquals(0, session2.submittedPlans.size, "Esc 不得提交")
        } finally { panel2.dispose() }
    }

    @Test
    fun `animated hit rectangles follow the drawn position`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(), paused = false, epoch = 0L)
            val settled = panel.btnPrimaryRect.x
            panel.motion.revealPanel()
            panel.syncGeometry()
            assertTrue(panel.motion.isAnimating || panel.motion.panelOffsetX == 0f)
            // 动画中的命中矩形与绘制使用同一偏移。
            val base = panel.layout.primaryActionRect.x
            assertEquals(base + panel.motion.panelOffsetX, panel.btnPrimaryRect.x, 0.01f)
            panel.motion.update(System.currentTimeMillis() + 5_000L)
            panel.syncGeometry()
            assertEquals(0f, panel.motion.panelOffsetX, 0.01f, "动画结束后必须落在终态")
            assertEquals(base, panel.btnPrimaryRect.x, 0.01f)
            assertTrue(panel.btnPrimaryRect.x <= settled + 0.01f)
        } finally { panel.dispose() }
    }

    @Test
    fun `reduced motion lands on the final state immediately`() {
        val panel = ShufflePreparationPanel(FakeGameSession(), motionEnabled = false)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            panel.syncGeometry()
            assertEquals(0f, panel.motion.panelOffsetX, 0.01f)
            assertEquals(1f, panel.motion.panelAlpha, 0.01f)
            press(panel, panel.btnOpenTargetRect)
            panel.syncGeometry()
            assertEquals(0f, panel.motion.drawerOffsetX, 0.01f, "减少动画时抽屉直达终态")
            assertTrue(panel.model.page == ShufflePreparationModel.PreparationPage.DRAWER)
        } finally { panel.dispose() }
    }

    @Test
    fun `the background stays visible and gameplay input never passes through`() {
        val panel = ShufflePreparationPanel(FakeGameSession())
        try {
            panel.update(view(), paused = false, epoch = 0L)
            // 准备栏之外的桌面点击不得触发任何准备动作。
            panel.pressDown(120f, 300f)
            panel.pressUp(120f, 300f)
            assertNull(panel.model.target)
            assertFalse(panel.model.isSubmitting)
        } finally { panel.dispose() }
    }

    // ------------------------------------------------------------------
    // 「直接开局 · 0点」边界：先校验再清草稿，非法状态下不得变更草稿
    // ------------------------------------------------------------------

    @Test
    fun `drawer direct start never mutates the draft while paused`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            assertEquals(ShuffleTarget.KOKUSHI, panel.model.target)

            // 进入暂停：抽屉保持打开，但一切编辑与提交都被阻断。
            panel.update(view(), paused = true, epoch = 1L)
            assertFalse(panel.model.canSkip)
            press(panel, panel.btnDrawerSkipRect)
            assertEquals(0, session.submittedPlans.size, "暂停时不得提交")
            assertEquals(ShuffleTarget.KOKUSHI, panel.model.target, "暂停时不得清空草稿")
            assertEquals(30, panel.model.totalCost)
        } finally { panel.dispose() }
    }

    @Test
    fun `drawer direct start never mutates the draft after the deadline`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.DAISANGEN))
            assertEquals(ShuffleTarget.DAISANGEN, panel.model.target)

            // 权威倒计时到期。
            panel.update(view(deadline = 10_000L, serverNow = 10_000L), paused = false, epoch = 1L)
            assertTrue(panel.model.isTimeExpired)
            press(panel, panel.btnDrawerSkipRect)
            assertEquals(0, session.submittedPlans.size, "到期后不得提交")
            assertEquals(ShuffleTarget.DAISANGEN, panel.model.target, "到期后不得清空草稿")
        } finally { panel.dispose() }
    }

    @Test
    fun `drawer direct start never mutates the draft while a request is in flight`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            // 先提交一次（停在途），此时面板处于锁定态。
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(1, session.submittedPlans.size)
            assertTrue(panel.model.isSubmitting)

            press(panel, panel.btnDrawerSkipRect)
            assertEquals(1, session.submittedPlans.size, "在途请求期间不得再次提交")
            assertEquals(ShuffleTarget.KOKUSHI, panel.model.target, "在途请求期间不得清空草稿")

            completion.complete(Receipt(session.submittedPlans.single().requestId, ReceiptStatus.RECORDED))
            assertTrue(panel.model.isLocked)
        } finally { panel.dispose() }
    }

    @Test
    fun `drawer direct start never mutates the draft after the plan is confirmed`() {
        val session = FakeGameSession()
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.KOKUSHI))
            press(panel, panel.btnDrawerConfirmRect)
            assertEquals(1, session.submittedPlans.size)
            assertTrue(panel.model.isLocked)

            press(panel, panel.btnDrawerSkipRect)
            assertEquals(1, session.submittedPlans.size, "已确认后不得再次提交")
            assertEquals(ShuffleTarget.KOKUSHI, panel.model.target, "已确认后不得清空方案")
        } finally { panel.dispose() }
    }

    @Test
    fun `a rejected direct start restores the originating drawer and the legal draft`() {
        val completion = CompletableFuture<Receipt>()
        val session = FakeGameSession()
        session.submitResult = completion
        val panel = ShufflePreparationPanel(session)
        try {
            panel.update(view(seeThrough = true), paused = false, epoch = 0L)
            press(panel, panel.btnOpenTargetRect)
            press(panel, panel.btnTargets.getValue(ShuffleTarget.CHINITSU_SOU))
            press(panel, panel.btnDrawerSeeThroughRect)
            assertEquals(40, panel.model.totalCost)

            press(panel, panel.btnDrawerSkipRect)
            assertEquals(1, session.submittedPlans.size)
            assertEquals(0, session.submittedPlans.single().selection.cost())
            assertNull(panel.model.target, "直接开局先清空草稿")
            assertFalse(panel.model.isSeeThroughSelected)

            completion.complete(
                Receipt(
                    session.submittedPlans.single().requestId,
                    ReceiptStatus.REJECTED,
                    reason = "WRONG_PHASE"
                )
            )
            assertEquals(
                ShufflePreparationModel.PreparationPage.DRAWER,
                panel.model.page,
                "失败必须恢复发起页（抽屉）"
            )
            assertEquals(ShuffleTarget.CHINITSU_SOU, panel.model.target, "失败必须恢复合法草稿")
            assertTrue(panel.model.isSeeThroughSelected, "失败必须恢复看破草稿")
            assertEquals(40, panel.model.totalCost)
            assertNotNull(panel.model.errorMessage)
        } finally { panel.dispose() }
    }
}

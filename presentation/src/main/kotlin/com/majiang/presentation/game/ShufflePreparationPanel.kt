package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleTarget
import com.majiang.presentation.common.UiAssets

/**
 * 洗牌千术准备阶段展示面板 (V4 presentation.game)。
 *
 * 正式 APP 开局交互（设计见 [doc/ui/round-preparation/DESIGN.md](file:///D:/majiang/doc/ui/round-preparation/DESIGN.md)）：
 * 1. 新局直接进入麻将桌，右侧只显示紧凑「本局准备」栏，**默认不展示六种目标**；
 * 2. 主要动作「开始本局 · 0点」一次点击即提交 target=null / seeThrough=false；
 * 3. 只有点「偷天换日 · 30点」才从右局部展开六选一抽屉，目标必须用户手选（不默认清一色）；
 * 4. 看破可在准备栏独立勾选（房间禁用时不提供可交互入口）；
 * 5. 抽屉「返回」/Esc 清目标、保留看破、不提交；「直接开局 · 0点」清全部草稿并立刻提交 skip；
 * 6. 提交中阻止重复；失败恢复发起页与合法草稿并显示错误；
 * 7. 成功后收为只读「方案已确认」回执，等待权威 preparation 结束才解除准备态；
 * 8. 桌面始终可见：不绘制全屏遮黑，不把设计 PNG 当交互背景；
 * 9. 面板只依赖 [GameSession]（client.api），不越层访问引擎或服务端。
 */
class ShufflePreparationPanel @JvmOverloads constructor(
    val session: GameSession,
    private val assets: UiAssets? = null,
    motionEnabled: Boolean = true,
    /** 回执淡出时序使用的时钟（可注入，供无头回归确定性断言）。 */
    private val clock: () -> Long = { System.currentTimeMillis() }
) : Disposable {

    /** 纯表现层状态模型（供无头测试与展示共享） */
    val model: ShufflePreparationModel = ShufflePreparationModel()

    /** 局部滑入时序模型（准备栏 72px/220ms、抽屉 160px/280ms easeOutCubic）。 */
    val motion: RoundPreparationMotion = RoundPreparationMotion(motionEnabled = motionEnabled)

    /** 当前准备弹窗是否处于活动状态 */
    val isActive: Boolean get() = model.isActive

    /** 面板是否已被释放 */
    var isDisposed: Boolean = false
        private set

    // ---------------------------------------------------------------------
    // 几何：真实绘制 / 命中矩形（含动画位移，与所见一致）
    // ---------------------------------------------------------------------

    private var visibleBounds: Rectangle = Rectangle(RoundPreparationLayout.DEFAULT_VISIBLE)
    private var baseLayout: RoundPreparationLayout = RoundPreparationLayout.DEFAULT

    /** 当前基础布局（不含动画位移）。 */
    val layout: RoundPreparationLayout get() = baseLayout

    /** 右侧准备栏。 */
    val panelRect: Rectangle = Rectangle()
    /** 目标抽屉。 */
    val drawerRect: Rectangle = Rectangle()
    /** 倒计时显示框。 */
    val countdownRect: Rectangle = Rectangle()
    /** 「偷天换日 · 30点」入口。 */
    val btnOpenTargetRect: Rectangle = Rectangle()
    /** 准备栏「看破 · 10点」勾选。 */
    val btnSeeThroughRect: Rectangle = Rectangle()
    /** 准备栏主要动作（开始本局 / 确认看破）。 */
    val btnPrimaryRect: Rectangle = Rectangle()
    /** 抽屉「返回」。 */
    val btnDrawerBackRect: Rectangle = Rectangle()
    /** 抽屉「看破 · 10点」勾选。 */
    val btnDrawerSeeThroughRect: Rectangle = Rectangle()
    /** 抽屉「直接开局 · 0点」。 */
    val btnDrawerSkipRect: Rectangle = Rectangle()
    /** 抽屉「确认施术」。 */
    val btnDrawerConfirmRect: Rectangle = Rectangle()

    /** 六张目标卡。 */
    val btnTargets: Map<ShuffleTarget, Rectangle> = ShuffleTarget.entries.associateWith { Rectangle() }

    /**
     * 兼容别名：零费用开局入口。
     *
     * 准备栏主要动作在未勾选看破时即为「开始本局 · 0点」，因此沿用既有名称。
     */
    val btnSkipRect: Rectangle get() = btnPrimaryRect

    /** 兼容别名：确认施术入口（位于目标抽屉内）。 */
    val btnConfirmRect: Rectangle get() = btnDrawerConfirmRect

    // 悬停状态
    var hoveredTarget: ShuffleTarget? = null
        private set
    var isPrimaryHovered: Boolean = false
        private set
    var isOpenTargetHovered: Boolean = false
        private set
    var isSeeThroughHovered: Boolean = false
        private set
    var isDrawerConfirmHovered: Boolean = false
        private set
    var isDrawerSkipHovered: Boolean = false
        private set
    var isDrawerBackHovered: Boolean = false
        private set
    var isDrawerSeeThroughHovered: Boolean = false
        private set

    // 中文字体与字形管理（借用 UiAssets 管理的 17px 共享字体与生命周期）
    private val glyphLayout = GlyphLayout()

    private sealed interface Control {
        object OpenTarget : Control
        object SeeThrough : Control
        object Primary : Control
        object DrawerBack : Control
        object DrawerSeeThrough : Control
        object DrawerSkip : Control
        object DrawerConfirm : Control
        data class TargetCard(val target: ShuffleTarget) : Control
    }

    private var pressedControl: Control? = null

    companion object {
        fun translateServerReason(reason: String?): String {
            if (reason.isNullOrBlank()) return "请求被拒绝"
            val trimmed = reason.trim()
            return when {
                trimmed.contains("UNSUPPORTED", ignoreCase = true) -> "当前不支持该操作"
                trimmed.contains("TIMEOUT", ignoreCase = true) || trimmed.contains("TIMED_OUT", ignoreCase = true) -> "请求已超时"
                trimmed.contains("INSUFFICIENT", ignoreCase = true) -> "点数不足"
                trimmed.contains("NOT_AVAILABLE", ignoreCase = true) -> "操作不可用"
                trimmed.contains("STALE", ignoreCase = true) || trimmed.contains("EPOCH", ignoreCase = true) -> "代次过期"
                trimmed.contains("ROUND", ignoreCase = true) -> "对局已变更"
                trimmed.contains("DISCONNECTED", ignoreCase = true) || trimmed.contains("CLOSED", ignoreCase = true) -> "连接已断开"
                trimmed.contains("ALREADY", ignoreCase = true) -> "千术方案已确认"
                trimmed.contains("ILLEGAL", ignoreCase = true) || trimmed.contains("INVALID", ignoreCase = true) -> "请求不合法"
                trimmed.contains("REJECT", ignoreCase = true) -> "请求被拒绝"
                trimmed.contains("网络") || trimmed.contains("断开") -> "网络连接异常"
                trimmed.contains("超时") -> "请求已超时"
                trimmed.contains("点数") -> "点数不足"
                trimmed.contains("拒绝") -> "请求被拒绝"
                else -> "请求被拒绝或异常"
            }
        }

        /** 六种目标的显示名（与现有枚举严格一致）。 */
        fun targetDisplayName(target: ShuffleTarget): String = when (target) {
            ShuffleTarget.CHINITSU_MAN -> "清一色（万）"
            ShuffleTarget.CHINITSU_SOU -> "清一色（条）"
            ShuffleTarget.CHINITSU_PIN -> "清一色（筒）"
            ShuffleTarget.DAISANGEN -> "大三元"
            ShuffleTarget.KOKUSHI -> "国士无双"
            ShuffleTarget.DAISUUSHII -> "大四喜"
        }

        /** 目标卡的示意牌纹字形（示意，不是玩家实际手牌）。 */
        fun targetMotifGlyph(target: ShuffleTarget): String = when (target) {
            ShuffleTarget.CHINITSU_MAN -> "萬"
            ShuffleTarget.CHINITSU_SOU -> "索"
            ShuffleTarget.CHINITSU_PIN -> "筒"
            ShuffleTarget.DAISANGEN -> "中"
            ShuffleTarget.KOKUSHI -> "幺"
            ShuffleTarget.DAISUUSHII -> "風"
        }

        /** 目标卡的示意牌纹颜色。 */
        fun targetMotifColor(target: ShuffleTarget): Color = when (target) {
            ShuffleTarget.CHINITSU_MAN -> Color(0.86f, 0.26f, 0.24f, 1f)
            ShuffleTarget.CHINITSU_SOU -> Color(0.26f, 0.68f, 0.36f, 1f)
            ShuffleTarget.CHINITSU_PIN -> Color(0.28f, 0.48f, 0.86f, 1f)
            ShuffleTarget.DAISANGEN -> Color(0.92f, 0.38f, 0.22f, 1f)
            ShuffleTarget.KOKUSHI -> Color(0.86f, 0.70f, 0.28f, 1f)
            ShuffleTarget.DAISUUSHII -> Color(0.62f, 0.78f, 0.88f, 1f)
        }

        /** 目标卡副标题（事实性描述，不承诺成型）。 */
        fun targetHint(target: ShuffleTarget): String = when (target) {
            ShuffleTarget.CHINITSU_MAN -> "全万子"
            ShuffleTarget.CHINITSU_SOU -> "全索子"
            ShuffleTarget.CHINITSU_PIN -> "全筒子"
            ShuffleTarget.DAISANGEN -> "三元牌"
            ShuffleTarget.KOKUSHI -> "十三幺"
            ShuffleTarget.DAISUUSHII -> "四风牌"
        }

        /** 规则短句（DESIGN §3，固定文案）。 */
        const val TARGET_RULE_HINT = "影响起手及后续摸牌，不保证成型"

        /** 看破说明（DESIGN §4，完整文案）。 */
        const val SEE_THROUGH_HINT = "抵消对手本局码牌并清除其既有落汗；双方费用照付"

        /** 看破说明第一行（准备栏宽度有限，按两行排版）。 */
        const val SEE_THROUGH_HINT_LINE_1 = "抵消对手本局码牌并清除其既有落汗"

        /** 看破说明第二行。 */
        const val SEE_THROUGH_HINT_LINE_2 = "双方费用照付"

        /** 确认回执的淡出时长（毫秒，DESIGN §7「回执淡出约 150ms」）。 */
        const val RECEIPT_FADE_MS = 150f
    }

    fun getOrInitChineseFont(): BitmapFont? {
        val uiAssets = assets ?: UiAssets.activeInstance
        return uiAssets?.chineseFont17 ?: uiAssets?.chineseFont
    }

    // ---------------------------------------------------------------------
    // 生命周期与状态同步
    // ---------------------------------------------------------------------

    /**
     * 确认回执状态。
     *
     * 本人方案确认后必须收成一张**独立、只读**的回执：
     * 1. 隐藏全部已失效的输入控件与装饰框（倒计时框、看破勾选框、主按钮、偷天换日入口）；
     * 2. 权威准备阶段结束（单机立即发牌）后回执仍短暂保留并淡出，
     *    允许与真实发牌衔接重叠（DESIGN §7「回执淡出约 150ms，可与真实发牌动画重叠」）；
     * 3. 权威结束前的准备阶段里回执常驻，等待文案不得与任何控件或文字重叠。
     */
    private var receiptLatched = false
    private var wasPreparing = false

    /**
     * 权威准备阶段结束的时刻。
     *
     * 权威结束后回执才开始淡出；在这之前回执常驻（等待其他玩家）。
     */
    private var preparationEndedAtMs = -1L

    /**
     * 回执**真正被绘制**的时刻。
     *
     * 只能在 [render] 成功画出回执之后记录（[markReceiptRendered]），
     * 绝不能在 `syncGeometry()` / `update()` 里提前记——那时 GL 还没画，
     * 若权威快照与下一次真实渲染相隔超过淡出时长（例如窗口暂停、掉帧、后台恢复），
     * 回执会在用户看到之前就过期。
     */
    private var receiptDrawnAtMs = -1L

    /**
     * 回执淡出起点 = max(权威准备结束, 回执首次真实绘制)。
     *
     * 这样既保证**首个真实渲染帧完全不透明可读**，又不会在发牌后挂一张长时间不透明的等待卡。
     */
    private var receiptFadeStartMs = -1L

    /**
     * 本地提交成功回执。
     *
     * 权威可能先发来 `preparation = null` 的快照再送达回执，此时模型里的本地确认标记会被清掉，
     * 因此回执状态由面板自己锁存，保证「方案已确认」一定出现过。
     * 只有通过 [ShufflePreparationModel.isCurrentSubmissionFor] 校验的回执才会写入这里，
     * 跨局迟到回执不会污染新局。
     */
    private var localReceiptAccepted = false
    private var receiptSelectionSnapshot: com.majiang.client.api.ShuffleSelection? = null

    private fun syncReceiptState() {
        val preparing = model.isActive
        if (preparing && !wasPreparing) {
            // 新的一局：上一局的回执作废。
            receiptLatched = false
            localReceiptAccepted = false
            receiptSelectionSnapshot = null
            receiptFadeStartMs = -1L
            receiptDrawnAtMs = -1L
            preparationEndedAtMs = -1L
        }
        wasPreparing = preparing
        if (!receiptLatched && (localReceiptAccepted || model.isPlanConfirmed)) {
            receiptLatched = true
        }
        if (receiptLatched && !preparing && preparationEndedAtMs < 0L) {
            preparationEndedAtMs = nowMs()
        }
        updateReceiptFadeStart()
    }

    /**
     * 计算回执淡出起点。
     *
     * 必须同时满足「回执已经真正绘制过」与「权威准备阶段已经结束」才启动淡出：
     * - 只在准备结束后才淡出（准备阶段内回执常驻，等待其他玩家）；
     * - 从两者较晚的时刻算起，保证首个真实渲染帧是**完全不透明**的。
     */
    private fun updateReceiptFadeStart() {
        if (!receiptLatched) return
        if (receiptDrawnAtMs < 0L) return
        if (preparationEndedAtMs < 0L) return
        if (receiptFadeStartMs < 0L) {
            receiptFadeStartMs = maxOf(preparationEndedAtMs, receiptDrawnAtMs)
        }
    }

    /**
     * 通知面板：回执已在本帧**真正绘制**。
     *
     * 由 [render] 在画出回执之后调用；无头回归测试可直接调用以模拟一次真实渲染帧。
     */
    internal fun markReceiptRendered() {
        if (!receiptLatched) return
        if (receiptDrawnAtMs < 0L) receiptDrawnAtMs = nowMs()
        updateReceiptFadeStart()
    }

    /** 回执当前是否应当绘制。 */
    val isReceiptVisible: Boolean
        get() {
            if (!receiptLatched) return false
            if (model.isActive) return true
            // 还没启动淡出（首个真实渲染帧之前）→ 仍然要绘制，且完全不透明。
            if (receiptFadeStartMs < 0L) return true
            return (nowMs() - receiptFadeStartMs) < RECEIPT_FADE_MS
        }

    /** 回执不透明度：准备阶段内与首个真实渲染帧恒为 1，权威结束后在 150ms 内淡出。 */
    val receiptAlpha: Float
        get() {
            if (!receiptLatched) return 0f
            if (model.isActive) return 1f
            if (receiptFadeStartMs < 0L) return 1f
            return (1f - (nowMs() - receiptFadeStartMs) / RECEIPT_FADE_MS).coerceIn(0f, 1f)
        }

    /**
     * 回执当前显示的阶段文案。
     *
     * 「等待其他玩家完成准备」**只在权威仍处于准备阶段时**出现；
     * 权威准备结束（已发牌）后必须换成不再宣称等待的文案。
     */
    val receiptFeedbackText: String
        get() = when {
            !model.isActive -> "本局准备已完成"
            model.status == ShufflePreparationStatusView.GENERATING -> "正在发牌…"
            else -> model.confirmedFeedbackText ?: "等待其他玩家完成准备"
        }

    /** 面板是否需要绘制（准备阶段中，或回执仍在淡出）。 */
    val isVisible: Boolean
        get() = isActive || isReceiptVisible

    /**
     * 看破勾选入口当前是否可见。
     *
     * 房间禁用看破时**不提供任何可交互入口，也不画勾选框与装饰边框**，
     * 只留一行说明文字，避免出现「看起来能点其实不能点」的假控件。
     */
    val isSeeThroughEntryVisible: Boolean
        get() = model.seeThroughEnabled

    private fun nowMs(): Long = clock()

    fun update(snapshot: GameSnapshotView) {
        val wasActive = model.isActive
        val previousRound = model.roundId
        model.update(snapshot)
        handleRoundChange(previousRound)
        syncLifecycle(wasActive)
        syncGeometry()
    }

    fun update(prep: ShufflePreparationView?, paused: Boolean = false, epoch: Long = 0L) {
        val wasActive = model.isActive
        val previousRound = model.roundId
        model.update(prep, paused, epoch)
        handleRoundChange(previousRound)
        syncLifecycle(wasActive)
        syncGeometry()
    }

    /** 新局：准备栏重新从右滑入，旧的抽屉状态与草稿一律作废。 */
    private fun handleRoundChange(previousRound: String?) {
        val currentRound = model.roundId ?: return
        if (previousRound == null || previousRound == currentRound) return
        motion.reset()
        motion.revealPanel()
    }

    /** 重连 / 全量基线重建：回到默认准备栏，不从旧局缓存拼接选择。 */
    fun onReconnect() {
        model.onReconnect()
        motion.closeDrawer()
    }

    private fun syncLifecycle(wasActive: Boolean) {
        val isNowActive = model.isActive
        if (isNowActive && !wasActive) {
            motion.revealPanel()
        } else if (!isNowActive && wasActive) {
            motion.reset()
        }
        syncMotionWithModel()
    }

    /**
     * 抽屉动画与模型页面保持一致。
     *
     * 必须在**每帧**同步（而不只是收到权威快照时）：本地提交回执是异步到达的，
     * 权威快照可能晚于回执甚至根本不再包含准备阶段，若只在 update 里同步，
     * 抽屉会在回执之后仍然留在屏幕上，验收要求的“已收起的只读回执”就不成立。
     */
    private fun syncMotionWithModel() {
        val wantsDrawer = model.page == ShufflePreparationModel.PreparationPage.DRAWER &&
            !model.isPlanConfirmed &&
            !receiptLatched
        if (wantsDrawer) {
            if (!motion.isDrawerOpen) motion.openDrawer()
        } else if (motion.isDrawerOpen) {
            if (receiptLatched) {
                // 确认回执必须是一帧「已收起」的只读状态：抽屉立刻收起，不做滑出动画。
                motion.snapDrawerClosed()
            } else {
                motion.closeDrawer()
            }
        }
    }

    /** 更新可见虚拟区域（超宽 / 窄屏适配）。 */
    fun setVisibleBounds(bounds: Rectangle) {
        if (bounds.width <= 0f || bounds.height <= 0f) return
        if (visibleBounds == bounds) return
        visibleBounds = Rectangle(bounds)
        // 只有可见区变化时才重建布局，避免每帧分配几何对象。
        baseLayout = RoundPreparationLayout(visibleBounds)
        syncGeometry()
    }

    /** 推进动画并刷新真实命中矩形。 */
    fun syncGeometry() {
        motion.update()
        syncReceiptState()
        syncMotionWithModel()
        val panelOffset = motion.panelOffsetX
        val drawerOffset = motion.drawerOffsetX
        panelRect.set(baseLayout.panelRect)
        panelRect.x += panelOffset
        drawerRect.set(baseLayout.drawerRect)
        drawerRect.x += drawerOffset
        countdownRect.set(baseLayout.countdownRect)
        countdownRect.x += panelOffset
        btnOpenTargetRect.set(baseLayout.openTargetRect)
        btnOpenTargetRect.x += panelOffset
        btnSeeThroughRect.set(baseLayout.seeThroughRect)
        btnSeeThroughRect.x += panelOffset
        btnPrimaryRect.set(baseLayout.primaryActionRect)
        btnPrimaryRect.x += panelOffset
        btnDrawerBackRect.set(baseLayout.drawerBackRect)
        btnDrawerBackRect.x += drawerOffset
        btnDrawerSeeThroughRect.set(baseLayout.drawerSeeThroughRect)
        btnDrawerSeeThroughRect.x += drawerOffset
        btnDrawerSkipRect.set(baseLayout.drawerSkipRect)
        btnDrawerSkipRect.x += drawerOffset
        btnDrawerConfirmRect.set(baseLayout.drawerConfirmRect)
        btnDrawerConfirmRect.x += drawerOffset
        for ((target, rect) in baseLayout.targetCardRects) {
            val cached = btnTargets.getValue(target)
            cached.set(rect)
            cached.x += drawerOffset
        }
    }

    // ---------------------------------------------------------------------
    // 输入
    // ---------------------------------------------------------------------

    /**
     * 兼容入口：按下即激活。
     *
     * 既有冒烟与单测直接调用本方法；正式交互路径请使用 [pressDown] / [pressUp]，
     * 以获得「按下与松开位置一致才确认、拖出取消」的语义。
     */
    fun touchDown(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        val control = hitTest(x, y)
        if (control != null) {
            activate(control)
        }
        return true
    }

    /** 记录按下（不激活）。返回 true 表示输入已被准备阶段消费。 */
    fun pressDown(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        pressedControl = hitTest(x, y)
        return true
    }

    /** 松开：只有与按下位置仍在同一控件内才激活；拖出按钮即取消。 */
    fun pressUp(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        val pressed = pressedControl
        pressedControl = null
        if (pressed == null) return true
        val current = hitTest(x, y)
        if (current != null && current == pressed) {
            activate(pressed)
        }
        return true
    }

    /** 拖动：离开原控件即取消按下状态。 */
    fun pressDragged(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        val pressed = pressedControl ?: return true
        if (hitTest(x, y) != pressed) {
            pressedControl = null
        }
        return true
    }

    /** 取消当前按下（例如指针被抢走或窗口失焦）。 */
    fun pressCancel() {
        pressedControl = null
    }

    /** Esc / BACK：抽屉回准备栏（清目标、保留看破、不提交）；已在准备栏则不退出牌桌。 */
    fun handleEscape(): Boolean {
        if (!isActive) return false
        syncGeometry()
        if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
            closeDrawer()
            return true
        }
        return true
    }

    /** Enter / Space：确认当前页面的主要动作。 */
    fun handleConfirmKey(): Boolean {
        if (!isActive) return false
        syncGeometry()
        if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
            activate(Control.DrawerConfirm)
        } else {
            activate(Control.Primary)
        }
        return true
    }

    /** 键盘焦点移动：抽屉内按六目标 + 底部动作顺序移动。 */
    fun moveFocus(step: Int): Boolean {
        if (!isActive) return false
        syncGeometry()
        if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
            val order = ShuffleTarget.entries.toList()
            val current = order.indexOf(hoveredTarget)
            val next = if (current < 0) {
                if (step >= 0) 0 else order.lastIndex
            } else {
                (current + step + order.size) % order.size
            }
            hoveredTarget = order[next]
            return true
        }
        return false
    }

    fun mouseMoved(x: Float, y: Float) {
        if (!isActive) return
        syncGeometry()
        val control = hitTest(x, y)
        hoveredTarget = (control as? Control.TargetCard)?.target
        isPrimaryHovered = control == Control.Primary
        isOpenTargetHovered = control == Control.OpenTarget
        isSeeThroughHovered = control == Control.SeeThrough
        isDrawerConfirmHovered = control == Control.DrawerConfirm
        isDrawerSkipHovered = control == Control.DrawerSkip
        isDrawerBackHovered = control == Control.DrawerBack
        isDrawerSeeThroughHovered = control == Control.DrawerSeeThrough
    }

    private fun hitTest(x: Float, y: Float): Control? {
        if (!isActive) return null
        // 确认回执是只读的：不再有任何可交互控件。
        if (model.isPlanConfirmed) return null
        val inDrawer = model.page == ShufflePreparationModel.PreparationPage.DRAWER
        if (inDrawer) {
            for ((target, rect) in btnTargets) {
                if (rect.contains(x, y)) return Control.TargetCard(target)
            }
            if (btnDrawerBackRect.contains(x, y)) return Control.DrawerBack
            if (btnDrawerSeeThroughRect.contains(x, y)) return Control.DrawerSeeThrough
            if (btnDrawerSkipRect.contains(x, y)) return Control.DrawerSkip
            if (btnDrawerConfirmRect.contains(x, y)) return Control.DrawerConfirm
            return null
        }
        if (btnOpenTargetRect.contains(x, y)) return Control.OpenTarget
        if (btnSeeThroughRect.contains(x, y)) return Control.SeeThrough
        if (btnPrimaryRect.contains(x, y)) return Control.Primary
        return null
    }

    private fun activate(control: Control) {
        when (control) {
            is Control.TargetCard -> model.selectTarget(control.target)
            Control.OpenTarget -> {
                if (model.openTargetDrawer()) motion.openDrawer()
            }
            Control.SeeThrough -> model.toggleSeeThrough()
            Control.DrawerSeeThrough -> model.toggleSeeThrough()
            Control.DrawerBack -> closeDrawer()
            Control.DrawerSkip -> {
                // 「直接开局 · 0点」：**先确认可以提交**，再清空草稿并提交。
                // 暂停 / 截止 / 提交中 / 已确认时必须原样保留草稿，不得先清空再被拒绝。
                if (!model.canSkip) return
                val plan = model.buildSkipRequest() ?: return
                val savedDraft = model.captureDraft()
                model.clearAllDraft()
                submit(plan, ShufflePreparationModel.PreparationPage.DRAWER, savedDraft)
            }
            Control.DrawerConfirm -> {
                submit(model.buildDrawerConfirmRequest(), ShufflePreparationModel.PreparationPage.DRAWER)
            }
            Control.Primary -> {
                val origin = model.page
                submit(model.buildPrimaryRequest(), origin)
            }
        }
    }

    private fun closeDrawer() {
        if (model.closeTargetDrawer()) motion.closeDrawer()
    }

    /**
     * 提交方案。
     *
     * 所有异步回调副作用都先经过 [ShufflePreparationModel.isCurrentSubmissionFor] 校验：
     * 跨局（roundId 不一致）或已被别的请求取代的**迟到回执必须完全没有副作用**，
     * 既不能锁存旧局回执、不能收回新局抽屉，也不能把旧局草稿回滚进新局。
     *
     * @param restoreDraft 「先清空再提交」的动作（直接开局）需要保留的草稿快照；
     *   提交失败时回滚到合法草稿并恢复发起页面。
     */
    private fun submit(
        plan: com.majiang.client.api.ShufflePlan?,
        originPage: ShufflePreparationModel.PreparationPage,
        restoreDraft: com.majiang.client.api.ShuffleSelection? = null
    ) {
        if (plan == null) return
        val requestId = plan.requestId
        val roundId = plan.roundId
        model.markSubmitting(requestId, originPage)
        session.submitShufflePlan(plan).whenComplete { receipt, error ->
            // 跨局 / 过期回执：直接丢弃，不产生任何副作用。
            if (!model.isCurrentSubmissionFor(requestId, roundId)) return@whenComplete
            if (error != null) {
                model.onSubmissionRejected(requestId, translateServerReason(error.message))
                restoreDraft?.let { model.restoreDraft(it, roundId) }
            } else if (receipt == null) {
                model.onSubmissionRejected(requestId, "请求被拒绝")
                restoreDraft?.let { model.restoreDraft(it, roundId) }
            } else if (receipt.status == ReceiptStatus.APPLIED || receipt.status == ReceiptStatus.RECORDED) {
                // 回执先锁存：即使权威随后发来 preparation=null 的快照，回执也一定出现过。
                localReceiptAccepted = true
                receiptSelectionSnapshot = plan.selection
                model.collapseToReceiptPage()
                model.onSubmissionAccepted(requestId, plan.selection)
            } else {
                model.onSubmissionRejected(requestId, translateServerReason(receipt.reason))
                restoreDraft?.let { model.restoreDraft(it, roundId) }
            }
        }
    }

    // ---------------------------------------------------------------------
    // 绘制
    // ---------------------------------------------------------------------

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        fallbackFont: BitmapFont
    ) {
        if (!isVisible) return

        syncGeometry()

        val textFont = getOrInitChineseFont() ?: fallbackFont
        val isCustomFont = (textFont !== fallbackFont)

        // 确认回执是独立的只读层：隐藏全部已失效输入控件与装饰框，抽屉也不再绘制。
        val isReceipt = isReceiptVisible
        val receiptAlpha = receiptAlpha

        val panelAlpha = if (isReceipt) 0f else motion.panelAlpha
        val drawerAlpha = if (isReceipt) 0f else motion.drawerAlpha
        if (!isReceipt && panelAlpha <= 0.001f && drawerAlpha <= 0.001f) return
        if (isReceipt && receiptAlpha <= 0.001f) return

        // 桌面始终可见：只做局部遮罩，绝不全屏遮黑。
        val blendWasEnabled = Gdx.gl.glIsEnabled(GL20.GL_BLEND)
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)

        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)

        try {
            val buttonPatches = assets?.obtainPreparationButtonPatches()
            val useNinePatch = buttonPatches != null

            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            if (isReceipt) {
                drawReceiptBackground(shape, receiptAlpha)
            } else {
                drawPanelBackground(shape, panelAlpha, useNinePatch)
                drawDrawerBackground(shape, drawerAlpha, useNinePatch)
            }
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            if (isReceipt) {
                drawReceiptBorder(shape, receiptAlpha)
            } else {
                drawPanelBorders(shape, panelAlpha)
                drawDrawerBorders(shape, drawerAlpha)
            }
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            try {
                if (isReceipt) {
                    drawReceiptContent(batch, textFont, receiptAlpha)
                } else {
                    drawNinePatchButtons(batch, buttonPatches, panelAlpha, drawerAlpha)
                    if (!isCustomFont) textFont.data.setScale(1f)
                    drawPanelContent(batch, textFont, panelAlpha)
                    drawDrawerContent(batch, textFont, drawerAlpha)
                }
            } finally {
                if (batch.isDrawing) batch.end()
            }
            // 回执已在本帧**真正绘制**：从这一刻起才开始计算淡出，
            // 保证首个真实渲染帧完全不透明可读（即使权威快照与本次渲染相隔很久）。
            if (isReceipt) {
                markReceiptRendered()
            }
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
            if (batch.isDrawing) batch.end()
            Gdx.gl.glEnable(GL20.GL_BLEND)
            Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
            if (!blendWasEnabled) Gdx.gl.glDisable(GL20.GL_BLEND)
        }
    }

    /** 确认回执卡片（独立只读，不与准备栏任何控件重叠）。 */
    private fun receiptCardRect(): Rectangle = Rectangle(
        panelRect.x,
        panelRect.y + 400f,
        panelRect.width,
        200f
    )

    private fun drawReceiptBackground(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        val card = receiptCardRect()
        shape.color = Color(0.02f, 0.05f, 0.04f, 0.32f * alpha)
        shape.rect(card.x - 8f, card.y - 8f, card.width + 16f, card.height + 16f)
        shape.color = Color(0.05f, 0.12f, 0.09f, 0.95f * alpha)
        shape.rect(card.x, card.y, card.width, card.height)
    }

    private fun drawReceiptBorder(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        val card = receiptCardRect()
        shape.color = Color(0.55f, 0.92f, 0.66f, 0.95f * alpha)
        shape.rect(card.x, card.y, card.width, card.height)
    }

    /**
     * 确认回执文案：独立排版，逐行固定基线，互不重叠。
     */
    private fun drawReceiptContent(batch: SpriteBatch, font: BitmapFont, alpha: Float) {
        if (alpha <= 0.001f) return
        val card = receiptCardRect()
        val left = card.x + 18f
        val receiptSelection = receiptSelectionSnapshot
            ?: model.confirmedSelection
            ?: model.draftSelection()

        font.data.setScale(1.15f)
        font.color = Color(0.45f, 0.98f, 0.62f, alpha)
        font.draw(batch, model.confirmedReceiptTitle, left, card.y + 160f)

        font.data.setScale(0.95f)
        font.color = Color(0.94f, 0.96f, 0.94f, alpha)
        font.draw(batch, "本人方案：${describeSelection(receiptSelection)}", left, card.y + 124f)
        font.draw(batch, "预留费用：${receiptSelection.cost()}点", left, card.y + 96f)

        val feedback = receiptFeedbackText
        font.data.setScale(0.88f)
        font.color = Color(1.0f, 0.85f, 0.40f, alpha)
        font.draw(batch, feedback, left, card.y + 62f)

        font.data.setScale(0.74f)
        font.color = Color(0.72f, 0.80f, 0.74f, alpha)
        font.draw(batch, "本局方案已锁定，不能撤销或更换牌型。", left, card.y + 30f)
    }

    private fun drawPanelBackground(shape: ShapeRenderer, alpha: Float, useNinePatch: Boolean) {
        if (alpha <= 0.001f) return
        // 面板外沿的柔和压暗（仅右侧局部，桌面保持可见）。
        shape.color = Color(0.02f, 0.05f, 0.04f, 0.30f * alpha)
        shape.rect(panelRect.x - 10f, panelRect.y - 10f, panelRect.width + 20f, panelRect.height + 20f)

        shape.color = Color(0.05f, 0.12f, 0.09f, 0.93f * alpha)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)

        shape.color = Color(0.08f, 0.19f, 0.14f, 0.95f * alpha)
        shape.rect(countdownRect.x, countdownRect.y, countdownRect.width, countdownRect.height)

        if (isSeeThroughEntryVisible) {
            val isSelected = model.isSeeThroughSelected
            shape.color = if (isSelected) {
                Color(0.18f, 0.42f, 0.26f, 0.95f * alpha)
            } else {
                Color(0.09f, 0.18f, 0.13f, 0.85f * alpha)
            }
            shape.rect(btnSeeThroughRect.x, btnSeeThroughRect.y, btnSeeThroughRect.width, btnSeeThroughRect.height)
            // 原生勾选框（不依赖字体里的特殊符号）。
            shape.color = if (isSelected) Color(0.92f, 0.80f, 0.40f, alpha) else Color(0.36f, 0.56f, 0.42f, 0.9f * alpha)
            shape.rect(btnSeeThroughRect.x + 12f, btnSeeThroughRect.y + 16f, 18f, 18f)
        }

        if (!useNinePatch) {
            shape.color = Color(0.16f, 0.34f, 0.24f, 0.95f * alpha)
            shape.rect(btnOpenTargetRect.x, btnOpenTargetRect.y, btnOpenTargetRect.width, btnOpenTargetRect.height)
            shape.color = Color(0.62f, 0.24f, 0.14f, 0.95f * alpha)
            shape.rect(btnPrimaryRect.x, btnPrimaryRect.y, btnPrimaryRect.width, btnPrimaryRect.height)
        }
    }

    private fun drawDrawerBackground(shape: ShapeRenderer, alpha: Float, useNinePatch: Boolean) {
        if (alpha <= 0.001f) return
        shape.color = Color(0.02f, 0.05f, 0.04f, 0.32f * alpha)
        shape.rect(drawerRect.x - 10f, drawerRect.y - 10f, drawerRect.width + 20f, drawerRect.height + 20f)

        shape.color = Color(0.05f, 0.12f, 0.09f, 0.95f * alpha)
        shape.rect(drawerRect.x, drawerRect.y, drawerRect.width, drawerRect.height)

        if (isSeeThroughEntryVisible) {
            shape.color = Color(0.09f, 0.18f, 0.13f, 0.85f * alpha)
            shape.rect(
                btnDrawerSeeThroughRect.x,
                btnDrawerSeeThroughRect.y,
                btnDrawerSeeThroughRect.width,
                btnDrawerSeeThroughRect.height
            )
            // 原生勾选框。
            shape.color = if (model.isSeeThroughSelected) {
                Color(0.92f, 0.80f, 0.40f, alpha)
            } else {
                Color(0.36f, 0.56f, 0.42f, 0.9f * alpha)
            }
            shape.rect(btnDrawerSeeThroughRect.x + 12f, btnDrawerSeeThroughRect.y + 15f, 18f, 18f)
        }

        for ((target, rect) in btnTargets) {
            val isSelected = model.target == target
            shape.color = if (isSelected) {
                Color(0.20f, 0.42f, 0.27f, 0.96f * alpha)
            } else {
                Color(0.08f, 0.17f, 0.12f, 0.90f * alpha)
            }
            shape.rect(rect.x, rect.y, rect.width, rect.height)

            // 示意牌纹：三张小花牌（只是纹样，不是玩家实际手牌）。
            for (i in 0..2) {
                val sx = rect.x + 14f + i * 28f
                val sy = rect.y + 24f
                shape.color = Color(0.94f, 0.91f, 0.82f, 0.95f * alpha)
                shape.rect(sx, sy, 24f, 34f)
                val motif = targetMotifColor(target)
                shape.color = Color(motif.r, motif.g, motif.b, 0.95f * alpha)
                shape.rect(sx, sy + 26f, 24f, 8f)
            }

            if (isSelected) {
                shape.color = Color(0.92f, 0.80f, 0.40f, alpha)
                shape.rect(rect.x + rect.width - 30f, rect.y + rect.height - 30f, 16f, 16f)
            }
        }

        if (!useNinePatch) {
            shape.color = Color(0.16f, 0.34f, 0.24f, 0.95f * alpha)
            shape.rect(btnDrawerSkipRect.x, btnDrawerSkipRect.y, btnDrawerSkipRect.width, btnDrawerSkipRect.height)
            shape.rect(btnDrawerBackRect.x, btnDrawerBackRect.y, btnDrawerBackRect.width, btnDrawerBackRect.height)
            shape.color = Color(0.62f, 0.24f, 0.14f, 0.95f * alpha)
            shape.rect(btnDrawerConfirmRect.x, btnDrawerConfirmRect.y, btnDrawerConfirmRect.width, btnDrawerConfirmRect.height)
        }
    }

    private fun drawPanelBorders(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f * alpha)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)

        val remSec = model.remainingSeconds()
        shape.color = if (remSec in 1..3) Color(0.95f, 0.35f, 0.32f, alpha) else Color(0.42f, 0.85f, 0.55f, 0.9f * alpha)
        shape.rect(countdownRect.x, countdownRect.y, countdownRect.width, countdownRect.height)

        if (isSeeThroughEntryVisible) {
            shape.color = if (model.isSeeThroughSelected) {
                Color(0.45f, 0.95f, 0.60f, alpha)
            } else {
                Color(0.34f, 0.52f, 0.40f, 0.85f * alpha)
            }
            shape.rect(btnSeeThroughRect.x, btnSeeThroughRect.y, btnSeeThroughRect.width, btnSeeThroughRect.height)
        }
    }

    private fun drawDrawerBorders(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f * alpha)
        shape.rect(drawerRect.x, drawerRect.y, drawerRect.width, drawerRect.height)

        shape.color = if (model.isSeeThroughSelected) {
            Color(0.45f, 0.95f, 0.60f, alpha)
        } else {
            Color(0.34f, 0.52f, 0.40f, 0.85f * alpha)
        }
        if (isSeeThroughEntryVisible) {
            // 房间禁用看破时不画任何装饰边框，避免出现「看起来能点其实不能点」的假控件。
            shape.rect(
                btnDrawerSeeThroughRect.x,
                btnDrawerSeeThroughRect.y,
                btnDrawerSeeThroughRect.width,
                btnDrawerSeeThroughRect.height
            )
        }

        for ((target, rect) in btnTargets) {
            val isSelected = model.target == target
            shape.color = if (isSelected) {
                Color(0.92f, 0.80f, 0.40f, alpha)
            } else {
                Color(0.30f, 0.45f, 0.35f, 0.75f * alpha)
            }
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }
    }

    private fun drawNinePatchButtons(
        batch: SpriteBatch,
        patches: UiAssets.PreparationButtonPatches?,
        panelAlpha: Float,
        drawerAlpha: Float
    ) {
        if (patches == null) return
        val prior = Color(batch.color)
        try {
            if (panelAlpha > 0.001f) {
                batch.color = Color(1f, 1f, 1f, panelAlpha)
                patches.secondary.draw(
                    batch,
                    btnOpenTargetRect.x, btnOpenTargetRect.y,
                    btnOpenTargetRect.width, btnOpenTargetRect.height
                )
                patches.primary.draw(
                    batch,
                    btnPrimaryRect.x, btnPrimaryRect.y,
                    btnPrimaryRect.width, btnPrimaryRect.height
                )
            }
            if (drawerAlpha > 0.001f) {
                batch.color = Color(1f, 1f, 1f, drawerAlpha)
                patches.secondary.draw(
                    batch,
                    btnDrawerSkipRect.x, btnDrawerSkipRect.y,
                    btnDrawerSkipRect.width, btnDrawerSkipRect.height
                )
                patches.primary.draw(
                    batch,
                    btnDrawerConfirmRect.x, btnDrawerConfirmRect.y,
                    btnDrawerConfirmRect.width, btnDrawerConfirmRect.height
                )
                patches.secondary.draw(
                    batch,
                    btnDrawerBackRect.x, btnDrawerBackRect.y,
                    btnDrawerBackRect.width, btnDrawerBackRect.height
                )
            }
        } finally {
            batch.color = prior
        }
    }

    private fun drawPanelContent(batch: SpriteBatch, font: BitmapFont, alpha: Float) {
        if (alpha <= 0.001f) return
        val left = panelRect.x + 20f

        font.data.setScale(1.15f)
        font.color = Color(1.0f, 0.85f, 0.35f, alpha)
        font.draw(batch, "本局准备", left, panelRect.y + 592f)

        // 倒计时
        val remSec = model.remainingSeconds()
        val timerText = when {
            model.isPaused -> "暂停"
            model.status == ShufflePreparationStatusView.GENERATING -> "发牌"
            remSec <= 0L -> "截止"
            else -> "${remSec}s"
        }
        font.data.setScale(0.95f)
        font.color = if (remSec in 1..3 && !model.isPaused) Color(1.0f, 0.45f, 0.42f, alpha) else Color(0.45f, 0.98f, 0.66f, alpha)
        glyphLayout.setText(font, timerText)
        font.draw(
            batch,
            timerText,
            countdownRect.x + (countdownRect.width - glyphLayout.width) / 2f,
            countdownRect.y + (countdownRect.height + glyphLayout.height) / 2f - 3f
        )

        // 状态 / 错误提示
        font.data.setScale(0.82f)
        val error = model.errorMessage
        if (error != null) {
            font.color = Color(1.0f, 0.42f, 0.38f, alpha)
            font.draw(batch, error, left, panelRect.y + 552f)
        } else {
            font.color = Color(0.80f, 0.88f, 0.82f, alpha)
            val status = model.confirmedFeedbackText ?: "在倒计时内完成准备即可开局"
            font.draw(batch, status, left, panelRect.y + 552f)
        }

        // 偷天换日入口
        font.data.setScale(1.0f)
        font.color = Color(0.96f, 0.96f, 0.92f, alpha)
        glyphLayout.setText(font, "偷天换日 · 30点")
        font.draw(
            batch,
            "偷天换日 · 30点",
            btnOpenTargetRect.x + (btnOpenTargetRect.width - glyphLayout.width) / 2f,
            btnOpenTargetRect.y + btnOpenTargetRect.height / 2f + 6f
        )
        font.data.setScale(0.78f)
        font.color = Color(0.78f, 0.86f, 0.80f, alpha)
        font.draw(batch, "选择后再挑牌型", btnOpenTargetRect.x + 16f, btnOpenTargetRect.y - 12f)

        // 看破勾选
        if (isSeeThroughEntryVisible) {
            font.data.setScale(0.95f)
            font.color = if (model.isSeeThroughSelected) Color(0.55f, 1.0f, 0.68f, alpha) else Color(0.92f, 0.94f, 0.92f, alpha)
            font.draw(batch, "看破 · ${model.seeThroughPrice}点", btnSeeThroughRect.x + 38f, btnSeeThroughRect.y + 30f)
            font.data.setScale(0.74f)
            font.color = Color(0.74f, 0.82f, 0.76f, alpha)
            font.draw(batch, SEE_THROUGH_HINT_LINE_1, btnSeeThroughRect.x + 12f, btnSeeThroughRect.y - 4f)
            font.draw(batch, SEE_THROUGH_HINT_LINE_2, btnSeeThroughRect.x + 12f, btnSeeThroughRect.y - 22f)
        } else {
            font.data.setScale(0.82f)
            font.color = Color(0.55f, 0.58f, 0.55f, alpha)
            font.draw(batch, "看破未启用（房间不支持）", btnSeeThroughRect.x + 12f, btnSeeThroughRect.y + 18f)
        }

        // 当前方案与费用
        font.data.setScale(0.82f)
        font.color = Color(0.78f, 0.86f, 0.80f, alpha)
        font.draw(batch, "当前方案", left, panelRect.y + 292f)
        font.data.setScale(0.95f)
        font.color = Color(0.96f, 0.96f, 0.92f, alpha)
        font.draw(batch, describeSelection(model.draftSelection()), left, panelRect.y + 268f)

        font.data.setScale(0.82f)
        font.color = Color(1.0f, 0.85f, 0.40f, alpha)
        font.draw(batch, "预计消耗 ${model.totalCost}点", left, panelRect.y + 236f)
        font.color = Color(0.82f, 0.88f, 0.82f, alpha)
        font.draw(batch, "剩余出千点 ${model.remainingPointsPreview}点", left, panelRect.y + 212f)

        // 主要动作
        val canSubmit = model.canSubmitPrimary
        font.data.setScale(1.0f)
        font.color = if (canSubmit) Color(1.0f, 1.0f, 1.0f, alpha) else Color(0.62f, 0.64f, 0.62f, alpha)
        val primaryLabel = if (model.isSubmitting) "正在确认方案…" else model.primaryActionLabel
        glyphLayout.setText(font, primaryLabel)
        font.draw(
            batch,
            primaryLabel,
            btnPrimaryRect.x + (btnPrimaryRect.width - glyphLayout.width) / 2f,
            btnPrimaryRect.y + btnPrimaryRect.height / 2f + 6f
        )
        // 按钮下方的说明/禁用理由：必须落在主按钮**之外**（主按钮占 panelRect.y+114..+178）。
        val disabledReason = model.confirmDisabledReason
        font.data.setScale(0.78f)
        if (!canSubmit && disabledReason != null) {
            font.color = Color(1.0f, 0.55f, 0.45f, alpha)
            font.draw(batch, disabledReason, left, panelRect.y + 92f)
        } else {
            font.color = Color(0.70f, 0.78f, 0.72f, alpha)
            font.draw(batch, "点击后立即提交，不做二次确认", left, panelRect.y + 92f)
        }
    }

    private fun drawDrawerContent(batch: SpriteBatch, font: BitmapFont, alpha: Float) {
        if (alpha <= 0.001f) return
        val left = drawerRect.x + 20f

        font.data.setScale(1.1f)
        font.color = Color(1.0f, 0.85f, 0.35f, alpha)
        font.draw(batch, "偷天换日 · 选择目标", left, drawerRect.y + 608f)

        font.data.setScale(0.95f)
        font.color = Color(0.96f, 0.96f, 0.92f, alpha)
        glyphLayout.setText(font, "返回")
        font.draw(
            batch,
            "返回",
            btnDrawerBackRect.x + (btnDrawerBackRect.width - glyphLayout.width) / 2f,
            btnDrawerBackRect.y + btnDrawerBackRect.height / 2f + 6f
        )

        font.data.setScale(0.88f)
        font.color = Color(0.82f, 0.88f, 0.82f, alpha)
        font.draw(batch, TARGET_RULE_HINT, left, drawerRect.y + 564f)

        // 六张目标卡
        for ((target, rect) in btnTargets) {
            val isSelected = model.target == target
            font.data.setScale(0.98f)
            font.color = if (isSelected) Color(0.98f, 0.90f, 0.55f, alpha) else Color(0.94f, 0.94f, 0.92f, alpha)
            font.draw(batch, targetDisplayName(target), rect.x + 104f, rect.y + 56f)
            font.data.setScale(0.76f)
            font.color = if (isSelected) Color(1.0f, 0.88f, 0.50f, alpha) else Color(0.72f, 0.80f, 0.74f, alpha)
            font.draw(batch, targetHint(target), rect.x + 104f, rect.y + 30f)

            // 示意牌纹上的花色字（只是纹样，不是玩家实际手牌）。
            val motif = targetMotifColor(target)
            font.data.setScale(0.82f)
            font.color = Color(motif.r, motif.g, motif.b, alpha)
            for (i in 0..2) {
                font.draw(batch, targetMotifGlyph(target), rect.x + 18f + i * 28f, rect.y + 48f)
            }

            if (isSelected) {
                font.data.setScale(0.8f)
                font.color = Color(1.0f, 0.88f, 0.45f, alpha)
                font.draw(batch, "已选", rect.x + rect.width - 50f, rect.y + 24f)
            }
        }

        // 看破
        if (isSeeThroughEntryVisible) {
            font.data.setScale(0.95f)
            font.color = if (model.isSeeThroughSelected) Color(0.55f, 1.0f, 0.68f, alpha) else Color(0.92f, 0.94f, 0.92f, alpha)
            font.draw(
                batch,
                "看破 · ${model.seeThroughPrice}点",
                btnDrawerSeeThroughRect.x + 38f,
                btnDrawerSeeThroughRect.y + 29f
            )
        } else {
            font.data.setScale(0.82f)
            font.color = Color(0.55f, 0.58f, 0.55f, alpha)
            font.draw(
                batch,
                "看破未启用",
                btnDrawerSeeThroughRect.x + 12f,
                btnDrawerSeeThroughRect.y + 28f
            )
        }

        // 费用预览
        font.data.setScale(0.85f)
        font.color = Color(1.0f, 0.85f, 0.40f, alpha)
        font.draw(
            batch,
            "预计消耗 ${model.totalCost}点 · 剩余出千点 ${model.remainingPointsPreview}点",
            left,
            drawerRect.y + 156f
        )

        val error = model.errorMessage
        if (error != null) {
            font.color = Color(1.0f, 0.42f, 0.38f, alpha)
            font.draw(batch, error, drawerRect.x + 288f, drawerRect.y + 158f)
        } else if (model.target == null) {
            font.color = Color(0.80f, 0.86f, 0.82f, alpha)
            font.draw(batch, "先选择一个目标牌型", drawerRect.x + 288f, drawerRect.y + 158f)
        }

        // 底部两个动作
        font.data.setScale(0.95f)
        font.color = Color(0.96f, 0.96f, 0.92f, alpha)
        glyphLayout.setText(font, "直接开局 · 0点")
        font.draw(
            batch,
            "直接开局 · 0点",
            btnDrawerSkipRect.x + (btnDrawerSkipRect.width - glyphLayout.width) / 2f,
            btnDrawerSkipRect.y + btnDrawerSkipRect.height / 2f + 6f
        )

        val canConfirm = model.canConfirmTarget
        font.color = if (canConfirm) Color(1.0f, 1.0f, 1.0f, alpha) else Color(0.62f, 0.64f, 0.62f, alpha)
        val confirmLabel = when {
            model.isSubmitting -> "正在确认方案…"
            !canConfirm -> model.targetConfirmDisabledReason ?: "确认施术"
            else -> model.drawerConfirmLabel
        }
        glyphLayout.setText(font, confirmLabel)
        font.draw(
            batch,
            confirmLabel,
            btnDrawerConfirmRect.x + (btnDrawerConfirmRect.width - glyphLayout.width) / 2f,
            btnDrawerConfirmRect.y + btnDrawerConfirmRect.height / 2f + 6f
        )
    }

    private fun describeSelection(selection: com.majiang.client.api.ShuffleSelection): String {
        val parts = mutableListOf<String>()
        selection.target?.let { parts += targetDisplayName(it) }
        if (selection.seeThrough) parts += "看破"
        return if (parts.isEmpty()) "不使用千术" else parts.joinToString(" + ")
    }

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        // 借用 UiAssets 管理的字体与生命周期，组件不自行释放共享生成器与共享字体
    }
}

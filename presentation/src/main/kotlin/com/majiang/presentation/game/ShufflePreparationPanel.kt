package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
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
import com.majiang.client.api.TileView
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.StageFont
import com.majiang.presentation.common.render.TableStageFonts
import com.majiang.presentation.common.render.TableVisualAssets
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 洗牌千术准备阶段展示面板 (V4 presentation.game)。
 *
 * 正式 APP 开局交互（设计见
 * [doc/ui/round-preparation/VISUAL-COMPOSITION.md](file:///D:/majiang/doc/ui/round-preparation/VISUAL-COMPOSITION.md)）：
 * 1. 新局直接进入麻将桌，右侧显示深翡翠金饰准备栏，默认不展示六种目标；
 * 2. 主要动作「开始本局 · 0点」一次点击即提交 target=null / seeThrough=false；
 * 3. 只有点「偷天换日 · 30点」才从右局部展开六选一插画抽屉，目标必须用户手选；
 * 4. 看破可在准备栏独立勾选（房间禁用时整行装饰与交互隐藏）；
 * 5. 抽屉「返回」/Esc 清目标、保留看破、不提交；「直接开局 · 0点」清全部草稿并立刻提交；
 * 6. 提交中阻止重复；失败恢复发起页与合法草稿并显示错误；
 * 7. 成功后收为只读「方案已确认」回执，等待权威 preparation 结束才解除准备态；
 * 8. 背景/桌面由 TableStageRenderer 始终绘制，本面板只局部滑入，不平移相机或压缩桌面；
 * 9. 六张目标卡使用 manifest 插画切片 + 当前 TileSkin 的独立临时牌面示例，不进入权威牌状态；
 * 10. 只依赖 [GameSession]（client.api），不越层访问引擎或服务端。
 */
class ShufflePreparationPanel @JvmOverloads constructor(
    val session: GameSession,
    private val assets: UiAssets? = null,
    motionEnabled: Boolean = true,
    /** 回执淡出时序使用的时钟（可注入，供无头回归确定性断言）。 */
    private val clock: () -> Long = { System.currentTimeMillis() }
) : Disposable {

    /** 纯表现层状态模型（供无头测试与展示共享）。 */
    val model: ShufflePreparationModel = ShufflePreparationModel()

    /** 局部滑入时序模型（准备栏 72px/220ms、抽屉 160px/280ms easeOutCubic）。 */
    val motion: RoundPreparationMotion = RoundPreparationMotion(motionEnabled = motionEnabled)

    val isActive: Boolean get() = model.isActive

    var isDisposed: Boolean = false
        private set

    // ---------------------------------------------------------------------
    // 几何：真实绘制 / 命中矩形（含动画位移，与所见一致）
    // ---------------------------------------------------------------------

    private var visibleBounds: Rectangle = Rectangle(RoundPreparationLayout.DEFAULT_VISIBLE)
    private var baseLayout: RoundPreparationLayout = RoundPreparationLayout.DEFAULT

    /** 当前基础布局（不含动画位移）。 */
    val layout: RoundPreparationLayout get() = baseLayout

    val panelRect: Rectangle = Rectangle()
    val drawerRect: Rectangle = Rectangle()
    val countdownRect: Rectangle = Rectangle()
    val btnOpenTargetRect: Rectangle = Rectangle()
    val btnSeeThroughRect: Rectangle = Rectangle()
    val btnPrimaryRect: Rectangle = Rectangle()
    val btnDrawerBackRect: Rectangle = Rectangle()
    val btnDrawerSeeThroughRect: Rectangle = Rectangle()
    val btnDrawerSkipRect: Rectangle = Rectangle()
    val btnDrawerConfirmRect: Rectangle = Rectangle()
    val summaryRect: Rectangle = Rectangle()
    val balanceRect: Rectangle = Rectangle()
    val btnDrawerCostRect: Rectangle = Rectangle()

    /** 六张目标卡。 */
    val btnTargets: Map<ShuffleTarget, Rectangle> =
        ShuffleTarget.entries.associateWith { Rectangle() }

    /** 兼容别名。 */
    val btnSkipRect: Rectangle get() = btnPrimaryRect
    val btnConfirmRect: Rectangle get() = btnDrawerConfirmRect

    /** 抽屉动画进度：0=关闭，1=完全展开。只驱动右侧公开 HUD 的局部插值。 */
    val drawerProgress: Float
        get() = (1f - motion.drawerOffsetX / RoundPreparationMotion.DRAWER_OFFSET_X).coerceIn(0f, 1f)

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

    private data class FontSet(
        val title: StageFont,
        val drawerTitle: StageFont,
        val primary: StageFont,
        val card: StageFont,
        val body: StageFont,
        val small: StageFont,
        val name: StageFont,
        val score: StageFont,
        val receipt: StageFont,
        val fallback: BitmapFont
    )

    /** 独立临时绘制缓冲：目标卡示例牌不接触玩家实体列表或权威状态。 */
    private val cardTileBuffer = TileDrawBuffer()

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

    /**
     * 键盘焦点。默认落在「开始本局」，抽屉内按六卡、看破、直接开局、返回、确认施术
     * 的顺序移动；焦点框与按钮/目标卡使用同一 stage 绘制坐标与命中矩形。
     */
    private var focusedControl: Control? = Control.Primary

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

        /** 规则短句（玩家可读，不含实现说明）。 */
        const val TARGET_RULE_HINT = "影响起手及后续摸牌，不保证成型"

        /** 看破说明（玩家可读短句）。 */
        const val SEE_THROUGH_HINT = "抵消对手本局码牌并清除其既有落汗"

        /** 确认回执的淡出时长（毫秒）。 */
        const val RECEIPT_FADE_MS = 150f

        private val GOLD = Color(0.906f, 0.725f, 0.325f, 1f)
        private val INK = Color(0.973f, 0.902f, 0.667f, 1f)
        private val MUTED = Color(0.827f, 0.788f, 0.659f, 1f)
        private val JADE = Color(0.584f, 0.871f, 0.651f, 1f)
        private val DANGER = Color(1.0f, 0.608f, 0.514f, 1f)

        /** 目标卡示例牌：只用于绘制，不写入任何物理牌/权威容器。 */
        private val TARGET_SAMPLE_TILES: Map<ShuffleTarget, List<TileView>> = mapOf(
            ShuffleTarget.CHINITSU_MAN to listOf(
                TileView(0, com.majiang.client.api.SuitView.MAN, 1),
                TileView(4, com.majiang.client.api.SuitView.MAN, 2),
                TileView(8, com.majiang.client.api.SuitView.MAN, 3),
                TileView(12, com.majiang.client.api.SuitView.MAN, 4),
                TileView(16, com.majiang.client.api.SuitView.MAN, 5)
            ),
            ShuffleTarget.CHINITSU_SOU to listOf(
                TileView(36, com.majiang.client.api.SuitView.SOU, 1),
                TileView(40, com.majiang.client.api.SuitView.SOU, 2),
                TileView(44, com.majiang.client.api.SuitView.SOU, 3),
                TileView(48, com.majiang.client.api.SuitView.SOU, 4),
                TileView(52, com.majiang.client.api.SuitView.SOU, 5)
            ),
            ShuffleTarget.CHINITSU_PIN to listOf(
                TileView(72, com.majiang.client.api.SuitView.PIN, 1),
                TileView(76, com.majiang.client.api.SuitView.PIN, 2),
                TileView(80, com.majiang.client.api.SuitView.PIN, 3),
                TileView(84, com.majiang.client.api.SuitView.PIN, 4),
                TileView(88, com.majiang.client.api.SuitView.PIN, 5)
            ),
            ShuffleTarget.DAISANGEN to listOf(
                TileView(124, com.majiang.client.api.SuitView.DRAGON, 1),
                TileView(128, com.majiang.client.api.SuitView.DRAGON, 2),
                TileView(132, com.majiang.client.api.SuitView.DRAGON, 3)
            ),
            ShuffleTarget.KOKUSHI to listOf(
                TileView(0, com.majiang.client.api.SuitView.MAN, 1),
                TileView(32, com.majiang.client.api.SuitView.MAN, 9),
                TileView(36, com.majiang.client.api.SuitView.SOU, 1),
                TileView(68, com.majiang.client.api.SuitView.SOU, 9),
                TileView(72, com.majiang.client.api.SuitView.PIN, 1)
            ),
            ShuffleTarget.DAISUUSHII to listOf(
                TileView(108, com.majiang.client.api.SuitView.WIND, 1),
                TileView(112, com.majiang.client.api.SuitView.WIND, 2),
                TileView(116, com.majiang.client.api.SuitView.WIND, 3),
                TileView(120, com.majiang.client.api.SuitView.WIND, 4)
            )
        )
    }

    fun getOrInitChineseFont(): BitmapFont? {
        val uiAssets = assets ?: UiAssets.activeInstance
        return uiAssets?.chineseFont17 ?: uiAssets?.chineseFont
    }

    // ---------------------------------------------------------------------
    // 生命周期与状态同步
    // ---------------------------------------------------------------------

    private var receiptLatched = false
    private var wasPreparing = false
    private var preparationEndedAtMs = -1L
    private var receiptDrawnAtMs = -1L
    private var receiptFadeStartMs = -1L
    private var localReceiptAccepted = false
    private var receiptSelectionSnapshot: com.majiang.client.api.ShuffleSelection? = null
    private var selectedCardObserved: ShuffleTarget? = null
    private var selectedCardChangedAtMs = -1L

    private fun syncReceiptState() {
        val preparing = model.isActive
        if (preparing && !wasPreparing) {
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

    private fun updateReceiptFadeStart() {
        if (!receiptLatched) return
        if (receiptDrawnAtMs < 0L) return
        if (preparationEndedAtMs < 0L) return
        if (receiptFadeStartMs < 0L) {
            receiptFadeStartMs = maxOf(preparationEndedAtMs, receiptDrawnAtMs)
        }
    }

    internal fun markReceiptRendered() {
        if (!receiptLatched) return
        if (receiptDrawnAtMs < 0L) receiptDrawnAtMs = nowMs()
        updateReceiptFadeStart()
    }

    val isReceiptVisible: Boolean
        get() {
            if (!receiptLatched) return false
            if (model.isActive) return true
            if (receiptFadeStartMs < 0L) return true
            return (nowMs() - receiptFadeStartMs) < RECEIPT_FADE_MS
        }

    val receiptAlpha: Float
        get() {
            if (!receiptLatched) return 0f
            if (model.isActive) return 1f
            if (receiptFadeStartMs < 0L) return 1f
            return (1f - (nowMs() - receiptFadeStartMs) / RECEIPT_FADE_MS).coerceIn(0f, 1f)
        }

    val receiptFeedbackText: String
        get() = when {
            !model.isActive -> "本局准备已完成"
            model.status == ShufflePreparationStatusView.GENERATING -> "正在发牌…"
            else -> model.confirmedFeedbackText ?: "等待其他玩家完成准备"
        }

    val isVisible: Boolean get() = isActive || isReceiptVisible

    val isSeeThroughEntryVisible: Boolean get() = model.seeThroughEnabled

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

    private fun handleRoundChange(previousRound: String?) {
        val currentRound = model.roundId ?: return
        if (previousRound == null || previousRound == currentRound) return
        motion.reset()
        motion.revealPanel()
    }

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
        if (isNowActive && focusedControl?.let { it in focusOrder() } != true) {
            focusedControl = if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
                Control.TargetCard(ShuffleTarget.entries.first())
            } else {
                Control.Primary
            }
        }
        syncMotionWithModel()
    }

    private fun syncMotionWithModel() {
        val wantsDrawer = model.page == ShufflePreparationModel.PreparationPage.DRAWER &&
            !model.isPlanConfirmed &&
            !receiptLatched
        if (wantsDrawer) {
            if (!motion.isDrawerOpen) motion.openDrawer()
        } else if (motion.isDrawerOpen) {
            if (receiptLatched) motion.snapDrawerClosed() else motion.closeDrawer()
        }
    }

    fun setVisibleBounds(bounds: Rectangle) {
        if (bounds.width <= 0f || bounds.height <= 0f) return
        if (visibleBounds == bounds) return
        visibleBounds = Rectangle(bounds)
        baseLayout = RoundPreparationLayout(visibleBounds)
        syncGeometry()
    }

    /** 推进动画并刷新真实命中矩形。 */
    fun syncGeometry() {
        motion.update()
        syncReceiptState()
        syncMotionWithModel()
        if (selectedCardObserved != model.target) {
            selectedCardObserved = model.target
            selectedCardChangedAtMs = nowMs()
        }
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
        summaryRect.set(baseLayout.summaryRect)
        summaryRect.x += panelOffset
        balanceRect.set(baseLayout.balanceRect)
        balanceRect.x += panelOffset
        btnDrawerBackRect.set(baseLayout.drawerBackRect)
        btnDrawerBackRect.x += drawerOffset
        btnDrawerSeeThroughRect.set(baseLayout.drawerSeeThroughRect)
        btnDrawerSeeThroughRect.x += drawerOffset
        btnDrawerSkipRect.set(baseLayout.drawerSkipRect)
        btnDrawerSkipRect.x += drawerOffset
        btnDrawerConfirmRect.set(baseLayout.drawerConfirmRect)
        btnDrawerConfirmRect.x += drawerOffset
        btnDrawerCostRect.set(baseLayout.drawerCostRect)
        btnDrawerCostRect.x += drawerOffset
        for ((target, rect) in baseLayout.targetCardRects) {
            val cached = btnTargets.getValue(target)
            cached.set(rect)
            cached.x += drawerOffset
        }
    }

    // ---------------------------------------------------------------------
    // 输入
    // ---------------------------------------------------------------------

    fun touchDown(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        val control = hitTest(x, y)
        if (control != null) activate(control)
        return true
    }

    fun pressDown(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        pressedControl = hitTest(x, y)
        return true
    }

    fun pressUp(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        val pressed = pressedControl
        pressedControl = null
        if (pressed == null) return true
        val current = hitTest(x, y)
        if (current != null && current == pressed) activate(pressed)
        return true
    }

    fun pressDragged(x: Float, y: Float): Boolean {
        if (!isActive) return false
        syncGeometry()
        val pressed = pressedControl ?: return true
        if (hitTest(x, y) != pressed) pressedControl = null
        return true
    }

    fun pressCancel() {
        pressedControl = null
    }

    fun handleEscape(): Boolean {
        if (!isActive) return false
        syncGeometry()
        if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
            closeDrawer()
            return true
        }
        return true
    }

    fun handleConfirmKey(): Boolean {
        if (!isActive) return false
        syncGeometry()
        val focused = focusedControl?.takeIf { it in focusOrder() }
        if (focused != null) {
            activate(focused)
        } else if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
            activate(Control.DrawerConfirm)
        } else {
            activate(Control.Primary)
        }
        return true
    }

    fun moveFocus(step: Int): Boolean {
        if (!isActive) return false
        syncGeometry()
        val order = focusOrder()
        if (order.isEmpty()) return true
        val current = order.indexOf(focusedControl)
        val next = if (current < 0) {
            if (step >= 0) 0 else order.lastIndex
        } else {
            (current + step + order.size) % order.size
        }
        focusedControl = order[next]
        hoveredTarget = (focusedControl as? Control.TargetCard)?.target
        return true
    }

    /** 键盘焦点矩形（stage 设计坐标），供无头回归与渲染共用。 */
    fun focusedRect(): Rectangle? {
        val focused = focusedControl?.takeIf { it in focusOrder() } ?: return null
        return controlRect(focused)
    }

    private fun focusOrder(): List<Control> {
        if (!isActive || model.isPlanConfirmed) return emptyList()
        return if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) {
            buildList {
                addAll(ShuffleTarget.entries.map { Control.TargetCard(it) })
                if (isSeeThroughEntryVisible) add(Control.DrawerSeeThrough)
                add(Control.DrawerSkip)
                add(Control.DrawerBack)
                add(Control.DrawerConfirm)
            }
        } else {
            buildList {
                add(Control.OpenTarget)
                if (isSeeThroughEntryVisible) add(Control.SeeThrough)
                add(Control.Primary)
            }
        }
    }

    private fun controlRect(control: Control): Rectangle? = when (control) {
        is Control.TargetCard -> btnTargets[control.target]?.let {
            Rectangle(it.x, it.y + selectionLift(control.target), it.width, it.height)
        }
        Control.OpenTarget -> btnOpenTargetRect
        Control.SeeThrough -> btnSeeThroughRect
        Control.Primary -> btnPrimaryRect
        Control.DrawerBack -> btnDrawerBackRect
        Control.DrawerSeeThrough -> btnDrawerSeeThroughRect
        Control.DrawerSkip -> btnDrawerSkipRect
        Control.DrawerConfirm -> btnDrawerConfirmRect
    }

    fun mouseMoved(x: Float, y: Float) {
        if (!isActive) return
        syncGeometry()
        val control = hitTest(x, y)
        if (control != null) focusedControl = control
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
        if (model.isPlanConfirmed) return null
        val inDrawer = model.page == ShufflePreparationModel.PreparationPage.DRAWER
        if (inDrawer) {
            for ((target, rect) in btnTargets) {
                if (rect.contains(x, y)) return Control.TargetCard(target)
            }
            // 返回按钮命中区扩展为 44 高。
            if (btnDrawerBackRect.contains(x, y) ||
                Rectangle(
                    btnDrawerBackRect.x,
                    btnDrawerBackRect.y - 2f,
                    btnDrawerBackRect.width,
                    44f
                ).contains(x, y)
            ) {
                return Control.DrawerBack
            }
            if (btnDrawerSeeThroughRect.contains(x, y)) return Control.DrawerSeeThrough
            if (btnDrawerSkipRect.contains(x, y)) return Control.DrawerSkip
            if (btnDrawerConfirmRect.contains(x, y)) return Control.DrawerConfirm
            return null
        }
        if (btnOpenTargetRect.contains(x, y)) return Control.OpenTarget
        if (isSeeThroughEntryVisible && btnSeeThroughRect.contains(x, y)) return Control.SeeThrough
        if (btnPrimaryRect.contains(x, y)) return Control.Primary
        return null
    }

    private fun activate(control: Control) {
        when (control) {
            is Control.TargetCard -> model.selectTarget(control.target)
            Control.OpenTarget -> {
                if (model.openTargetDrawer()) {
                    motion.openDrawer()
                    focusedControl = Control.TargetCard(ShuffleTarget.entries.first())
                    hoveredTarget = ShuffleTarget.entries.first()
                }
            }
            Control.SeeThrough -> model.toggleSeeThrough()
            Control.DrawerSeeThrough -> model.toggleSeeThrough()
            Control.DrawerBack -> closeDrawer()
            Control.DrawerSkip -> {
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
                submit(model.buildPrimaryRequest(), model.page)
            }
        }
    }

    private fun closeDrawer() {
        if (model.closeTargetDrawer()) {
            motion.closeDrawer()
            focusedControl = Control.OpenTarget
            hoveredTarget = null
        }
    }

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
            if (!model.isCurrentSubmissionFor(requestId, roundId)) return@whenComplete
            if (error != null) {
                model.onSubmissionRejected(requestId, translateServerReason(error.message))
                restoreDraft?.let { model.restoreDraft(it, roundId) }
            } else if (receipt == null) {
                model.onSubmissionRejected(requestId, "请求被拒绝")
                restoreDraft?.let { model.restoreDraft(it, roundId) }
            } else if (receipt.status == ReceiptStatus.APPLIED || receipt.status == ReceiptStatus.RECORDED) {
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
        fallbackFont: BitmapFont,
        tileSkin: TileSkin? = null,
        stageFonts: TableStageFonts? = null
    ) {
        if (!isVisible) return

        syncGeometry()
        val visuals = assets?.obtainTableVisualAssets()
        val fonts = resolveFonts(fallbackFont, stageFonts)

        val isReceipt = isReceiptVisible
        val receiptAlpha = receiptAlpha
        // 抽屉与准备栏不做重叠 crossfade：抽屉一开始滑入，准备栏立即让位，避免两层文字叠字。
        val panelAlpha = if (isReceipt) {
            0f
        } else if (motion.drawerOffsetX >= RoundPreparationMotion.DRAWER_OFFSET_X - 0.5f) {
            1f
        } else {
            0f
        }
        val drawerAlpha = if (isReceipt) 0f else motion.drawerAlpha
        if (!isReceipt && panelAlpha <= 0.001f && drawerAlpha <= 0.001f) return
        if (isReceipt && receiptAlpha <= 0.001f) return

        val blendWasEnabled = Gdx.gl.glIsEnabled(GL20.GL_BLEND)
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)

        val origBatchColor = Color(batch.color)
        val origProjection = com.badlogic.gdx.math.Matrix4(batch.projectionMatrix)
        try {
            shape.projectionMatrix = camera.combined
            batch.projectionMatrix = camera.combined

            // 1. 轻量投影与面板底色。纹理本身负责锦纹与金边，形状只做局部压暗。
            shape.begin(ShapeRenderer.ShapeType.Filled)
            if (isReceipt) {
                drawReceiptShadow(shape, receiptAlpha)
            } else {
                drawPanelShadow(shape, panelAlpha)
                drawDrawerShadow(shape, drawerAlpha)
                drawFallbackBars(shape, visuals == null, panelAlpha, drawerAlpha)
            }
            shape.end()

            // 2. 九宫格面板 / 插画卡 / 按钮。
            batch.begin()
            try {
                if (isReceipt) {
                    if (visuals != null) {
                        batch.color = Color(1f, 1f, 1f, receiptAlpha)
                        // 源切片上下修正后，回执恢复主面板的完整 44px 金框，四边连续。
                        visuals.panelMain.draw(batch, baseLayout.receiptRect.x, baseLayout.receiptRect.y, 304f, 260f)
                    }
                } else {
                    drawPanelSurfaces(batch, visuals, panelAlpha)
                    drawDrawerSurfaces(batch, visuals, drawerAlpha)
                }
            } finally {
                if (batch.isDrawing) batch.end()
            }

            // 3. 卡片底部名称带、复选框与抬升后的暗底（在插画之上、文字之下）。
            shape.begin(ShapeRenderer.ShapeType.Filled)
            if (!isReceipt) {
                drawInnerSurfaceFills(shape, panelAlpha, drawerAlpha)
                drawCardBands(shape, drawerAlpha)
                drawSelectionFill(shape, drawerAlpha)
            }
            shape.end()

            // 4. 目标卡示例牌（独立缓冲，当前 TileSkin）+ 全部动态文字。
            batch.begin()
            try {
                if (isReceipt) {
                    drawReceiptText(batch, fonts, receiptAlpha)
                } else {
                    drawSamples(batch, shape, tileSkin, fonts.fallback, panelAlpha, drawerAlpha)
                    drawPanelText(batch, fonts, panelAlpha)
                    drawDrawerText(batch, fonts, drawerAlpha)
                }
            } finally {
                if (batch.isDrawing) batch.end()
            }

            // 5. Z60 选择金框 / 勾选 / 眼睛 / 回执勾与等待点。
            shape.begin(ShapeRenderer.ShapeType.Filled)
            if (!isReceipt) {
                drawSeeThroughControls(shape, panelAlpha, drawerAlpha)
            } else {
                drawReceiptDecorations(shape, receiptAlpha)
            }
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            if (!isReceipt) {
                drawSelectionOutlines(shape, drawerAlpha)
                drawFocusOutline(shape, if (model.page == ShufflePreparationModel.PreparationPage.DRAWER) drawerAlpha else panelAlpha)
            }
            shape.end()

            if (isReceipt) markReceiptRendered()
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = origBatchColor
            batch.projectionMatrix = origProjection
            Gdx.gl.glEnable(GL20.GL_BLEND)
            Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
            if (!blendWasEnabled) Gdx.gl.glDisable(GL20.GL_BLEND)
        }
    }

    private fun resolveFonts(fallback: BitmapFont, stageFonts: TableStageFonts?): FontSet {
        val ui = assets
        if (stageFonts != null) {
            return FontSet(
                title = stageFonts.title,
                drawerTitle = stageFonts.drawer,
                primary = stageFonts.primary,
                card = stageFonts.card,
                body = stageFonts.body,
                small = stageFonts.small,
                name = stageFonts.name,
                score = stageFonts.card,
                receipt = stageFonts.receipt,
                fallback = fallback
            )
        }
        fun raw(font: BitmapFont?): StageFont = StageFont(font ?: fallback, 19, 19, 1f)
        return FontSet(
            title = raw(ui?.effectiveTableTitleFont),
            drawerTitle = raw(ui?.effectiveTableDrawerFont),
            primary = raw(ui?.effectiveTablePrimaryFont),
            card = raw(ui?.effectiveTableCardFont),
            body = raw(ui?.effectiveTableBodyFont),
            small = raw(ui?.effectiveTableSmallFont),
            name = raw(ui?.effectiveTableNameFont),
            score = raw(ui?.effectiveTableCardFont),
            receipt = raw(ui?.effectiveTableReceiptFont),
            fallback = fallback
        )
    }

    private fun drawPanelShadow(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        shape.color = Color(0.01f, 0.03f, 0.02f, 0.42f * alpha)
        shape.rect(panelRect.x - 10f, panelRect.y - 10f, panelRect.width + 20f, panelRect.height + 20f)
    }

    private fun drawDrawerShadow(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        shape.color = Color(0.01f, 0.03f, 0.02f, 0.48f * alpha)
        shape.rect(drawerRect.x - 12f, drawerRect.y - 12f, drawerRect.width + 24f, drawerRect.height + 24f)
    }

    private fun drawReceiptShadow(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        val card = baseLayout.receiptRect
        shape.color = Color(0.01f, 0.03f, 0.02f, 0.44f * alpha)
        shape.rect(card.x - 9f, card.y - 9f, card.width + 18f, card.height + 18f)
    }

    /** 正常资源路径不会调用；缺少纹理时保留可读的轻量色块，不伪装成新素材。 */
    private fun drawFallbackBars(
        shape: ShapeRenderer,
        active: Boolean,
        panelAlpha: Float,
        drawerAlpha: Float
    ) {
        if (!active) return
        if (panelAlpha > 0.001f) {
            shape.color = Color(0.04f, 0.11f, 0.08f, 0.94f * panelAlpha)
            shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        }
        if (drawerAlpha > 0.001f) {
            shape.color = Color(0.04f, 0.11f, 0.08f, 0.95f * drawerAlpha)
            shape.rect(drawerRect.x, drawerRect.y, drawerRect.width, drawerRect.height)
        }
    }

    private fun drawPanelSurfaces(batch: SpriteBatch, visuals: TableVisualAssets?, alpha: Float) {
        if (alpha <= 0.001f) return
        batch.color = Color(1f, 1f, 1f, alpha)
        visuals?.panelMain?.draw(batch, panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        // 计时、看破、方案条使用设计稿的 1px 金框 + 深翡翠内衬（见 drawInnerSurfaceFills），
        // 不再叠一层会重复金线的 NinePatch。
        visuals?.panelCard?.draw(
            batch,
            btnOpenTargetRect.x,
            btnOpenTargetRect.y,
            btnOpenTargetRect.width,
            btnOpenTargetRect.height
        )
        batch.color = if (model.canSubmitPrimary) {
            Color(1f, 1f, 1f, alpha)
        } else {
            Color(0.58f, 0.58f, 0.58f, alpha)
        }
        visuals?.buttonPrimary?.draw(
            batch,
            btnPrimaryRect.x,
            btnPrimaryRect.y,
            btnPrimaryRect.width,
            btnPrimaryRect.height
        )
    }

    private fun drawDrawerSurfaces(batch: SpriteBatch, visuals: TableVisualAssets?, alpha: Float) {
        if (alpha <= 0.001f) return
        batch.color = Color(1f, 1f, 1f, alpha)
        visuals?.panelMain?.draw(batch, drawerRect.x, drawerRect.y, drawerRect.width, drawerRect.height)
        for ((target, rect) in btnTargets) {
            val lift = selectionLift(target)
            val scene = visuals?.targetScenes?.get(target)
            if (scene != null) {
                batch.color = Color(1f, 1f, 1f, alpha)
                batch.draw(scene, rect.x, rect.y + lift, rect.width, rect.height)
            }
        }
        visuals?.buttonSecondary?.draw(
            batch,
            btnDrawerSkipRect.x,
            btnDrawerSkipRect.y,
            btnDrawerSkipRect.width,
            btnDrawerSkipRect.height
        )
        visuals?.buttonSecondary?.draw(
            batch,
            btnDrawerBackRect.x,
            btnDrawerBackRect.y,
            btnDrawerBackRect.width,
            btnDrawerBackRect.height
        )
        batch.color = if (model.canConfirmTarget) {
            Color(1f, 1f, 1f, alpha)
        } else {
            Color(0.58f, 0.58f, 0.58f, alpha)
        }
        visuals?.buttonPrimary?.draw(
            batch,
            btnDrawerConfirmRect.x,
            btnDrawerConfirmRect.y,
            btnDrawerConfirmRect.width,
            btnDrawerConfirmRect.height
        )
    }

    /**
     * 主面板/抽屉保留完整九宫格外框；内部时间栏、目标入口、看破和费用条压一层
     * 深翡翠内衬，避免 340px 源边饰缩到 12px 后出现零碎短金线压住正文。
     */
    private fun drawInnerSurfaceFills(shape: ShapeRenderer, panelAlpha: Float, drawerAlpha: Float) {
        fun inset(rect: Rectangle, alpha: Float) {
            if (alpha <= 0.001f) return
            // 设计稿的 "inner"：一条 1px 暖金边 + 深翡翠内衬；不再叠 NinePatch 金线。
            shape.color = Color(0.906f, 0.725f, 0.325f, 0.86f * alpha)
            shape.rect(rect.x, rect.y, rect.width, rect.height)
            shape.color = Color(0.020f, 0.070f, 0.050f, 0.94f * alpha)
            shape.rect(rect.x + 1.5f, rect.y + 1.5f, rect.width - 3f, rect.height - 3f)
        }
        if (panelAlpha > 0.001f) {
            for (rect in listOf(countdownRect, btnSeeThroughRect, summaryRect)) {
                inset(rect, panelAlpha)
            }
            shape.color = Color(0.018f, 0.050f, 0.036f, 0.44f * panelAlpha)
            shape.rect(
                btnOpenTargetRect.x + 4f,
                btnOpenTargetRect.y + 4f,
                btnOpenTargetRect.width - 8f,
                btnOpenTargetRect.height - 8f
            )
        }
        if (drawerAlpha > 0.001f) {
            for (rect in listOf(btnDrawerCostRect, btnDrawerSeeThroughRect)) {
                inset(rect, drawerAlpha)
            }
        }
    }

    private fun drawCardBands(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        for ((target, rect) in btnTargets) {
            val lift = selectionLift(target)
            val selected = model.target == target
            shape.color = if (selected) {
                Color(0.10f, 0.055f, 0.018f, 0.88f * alpha)
            } else {
                Color(0.015f, 0.035f, 0.025f, 0.82f * alpha)
            }
            shape.rect(rect.x + 2f, rect.y + lift + 2f, rect.width - 4f, 34f)
            shape.color = if (selected) Color(0.906f, 0.725f, 0.325f, alpha) else Color(0.45f, 0.37f, 0.18f, 0.7f * alpha)
            shape.rect(rect.x + 2f, rect.y + lift + 35f, rect.width - 4f, 1.5f)
        }
    }

    private fun drawSelectionFill(shape: ShapeRenderer, alpha: Float) {
        val target = model.target ?: return
        val rect = btnTargets[target] ?: return
        val lift = selectionLift(target)
        // 选中不覆盖插画，只保留完整卡图 + 右上金底勾选。
        val checkCx = rect.x + rect.width - 21f
        val checkCy = rect.y + lift + rect.height - 21f
        shape.color = Color(0.055f, 0.035f, 0.015f, 0.82f * alpha)
        shape.circle(checkCx, checkCy, 15f, 20)
        shape.color = Color(0.906f, 0.725f, 0.325f, alpha)
        shape.circle(checkCx, checkCy, 13.5f, 20)
        shape.color = Color(0.055f, 0.10f, 0.07f, alpha)
        shape.rectLine(checkCx - 6f, checkCy + 1f, checkCx - 1f, checkCy - 5f, 3.2f)
        shape.rectLine(checkCx - 1f, checkCy - 5f, checkCx + 8f, checkCy + 7f, 3.2f)
    }

    /**
     * 抽屉底部按钮的 48px 高度内，金饰横线会穿过标签。这里在按钮中心压一条
     * 深翡翠文字带，让 19px 标签稳定可比，同时保留外围金边与按钮造型。
     */
    private fun drawFooterLabelBackplates(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        shape.color = Color(0.025f, 0.065f, 0.048f, 0.78f * alpha)
        for (rect in listOf(btnDrawerSkipRect, btnDrawerConfirmRect)) {
            shape.rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, 24f)
        }
    }

    private fun drawPanelText(batch: SpriteBatch, fonts: FontSet, alpha: Float) {
        if (alpha <= 0.001f) return
        val panelCenter = panelRect.x + panelRect.width / 2f

        drawCentered(batch, fonts.title, "本局准备", panelCenter, baseLayout.panelTitleTop, GOLD, alpha, embolden = true)

        val remSec = model.remainingSeconds()
        val timerText = when {
            model.isPaused -> "剩余时间 暂停"
            model.status == ShufflePreparationStatusView.GENERATING -> "剩余时间 发牌"
            remSec <= 0L -> "剩余时间 0秒"
            else -> "剩余时间 ${remSec}秒"
        }
        val timerColor = if (remSec in 1..3 && !model.isPaused) DANGER else INK
        drawLeftInBox(batch, fonts.card, timerText, countdownRect.x + 12f, countdownRect, timerColor, alpha)

        // 偷天换日入口：插画式大卡 + 当前牌皮示例牌。
        drawLeft(batch, fonts.card, "偷天换日", btnOpenTargetRect.x + 108f, btnOpenTargetRect.y + 118f, GOLD, alpha, embolden = true)
        drawLeft(batch, fonts.body, "◉ 30点", btnOpenTargetRect.x + 108f, btnOpenTargetRect.y + 84f, INK, alpha)
        drawLeft(
            batch,
            fonts.small,
            "选择后再挑牌型",
            btnOpenTargetRect.x + 108f,
            btnOpenTargetRect.y + 54f,
            Color(0.96f, 0.91f, 0.77f, 1f),
            alpha
        )

        if (isSeeThroughEntryVisible) {
            drawLeftInBox(
                batch,
                fonts.body,
                "看破 · ${model.seeThroughPrice}点",
                btnSeeThroughRect.x + 48f,
                btnSeeThroughRect,
                INK,
                alpha
            )
        } else {
            drawLeftInBox(batch, fonts.small, "看破未启用", btnSeeThroughRect.x + 8f, btnSeeThroughRect, MUTED, alpha)
        }

        drawLeft(
            batch,
            fonts.body,
            describeSelection(model.draftSelection()),
            summaryRect.x + 12f,
            summaryRect.y + 48f,
            INK,
            alpha
        )
        drawLeft(
            batch,
            fonts.small,
            "预计消耗 ${model.totalCost}点 · 剩余 ${model.remainingPointsPreview}点",
            summaryRect.x + 12f,
            summaryRect.y + 24f,
            GOLD,
            alpha
        )

        val canSubmit = model.canSubmitPrimary
        val label = if (model.isSubmitting) "正在确认方案…" else model.primaryActionLabel.replace("·", "")
        drawCentered(
            batch,
            fonts.primary,
            label,
            btnPrimaryRect.x + btnPrimaryRect.width / 2f,
            btnPrimaryRect.y + 47f,
            if (canSubmit) Color.WHITE else Color(0.68f, 0.68f, 0.64f, 1f),
            alpha,
            embolden = true
        )
        val error = model.errorMessage
        if (error != null) {
            drawLeft(batch, fonts.small, error, panelRect.x + 20f, panelRect.y + 182f, DANGER, alpha)
        }

        drawLeft(
            batch,
            fonts.small,
            "剩余出千点 ${model.remainingPointsPreview}点",
            balanceRect.x,
            balanceRect.y + 17f,
            MUTED,
            alpha
        )
    }

    private fun drawDrawerText(batch: SpriteBatch, fonts: FontSet, alpha: Float) {
        if (alpha <= 0.001f) return
        drawLeft(batch, fonts.drawerTitle, "偷天换日 · 选择目标", drawerRect.x + 24f, baseLayout.drawerTitleTop, GOLD, alpha, embolden = true)
        drawCentered(
            batch,
            fonts.small,
            "‹ 返回",
            btnDrawerBackRect.x + btnDrawerBackRect.width / 2f,
            btnDrawerBackRect.y + 31f,
            INK,
            alpha
        )
        val hint = model.errorMessage ?: TARGET_RULE_HINT
        drawLeft(
            batch,
            fonts.small,
            hint,
            baseLayout.drawerHintRect.x,
            baseLayout.drawerHintRect.y + 21f,
            if (model.errorMessage != null) DANGER else Color(0.94f, 0.89f, 0.76f, 1f),
            alpha,
            outline = true
        )

        for ((target, rect) in btnTargets) {
            val lift = selectionLift(target)
            val selected = model.target == target
            val label = targetDisplayName(target)
            drawLeft(
                batch,
                fonts.card,
                label,
                rect.x + 12f,
                rect.y + lift + 26f,
                if (selected) Color(1.0f, 0.88f, 0.50f, 1f) else Color(0.96f, 0.86f, 0.64f, 1f),
                alpha,
                embolden = selected,
                outline = true
            )
        }

        if (isSeeThroughEntryVisible) {
            drawLeftInBox(
                batch,
                fonts.body,
                "看破 · ${model.seeThroughPrice}点",
                btnDrawerSeeThroughRect.x + 48f,
                btnDrawerSeeThroughRect,
                if (model.isSeeThroughSelected) JADE else INK,
                alpha
            )
        } else {
            drawLeftInBox(
                batch,
                fonts.small,
                "看破未启用",
                btnDrawerSeeThroughRect.x + 12f,
                btnDrawerSeeThroughRect,
                MUTED,
                alpha
            )
        }

        if (model.target == null) {
            drawLeftInBox(
                batch,
                fonts.body,
                "请选择目标牌型",
                btnDrawerCostRect.x + 14f,
                btnDrawerCostRect,
                DANGER,
                alpha
            )
        } else {
            drawLeftInBox(
                batch,
                fonts.body,
                "本次消耗 ${model.totalCost}点",
                btnDrawerCostRect.x + 14f,
                btnDrawerCostRect,
                GOLD,
                alpha,
                embolden = true
            )
        }

        drawCentered(
            batch,
            fonts.body,
            "直接开局 · 0点",
            btnDrawerSkipRect.x + btnDrawerSkipRect.width / 2f,
            btnDrawerSkipRect.y + 32f,
            INK,
            alpha
        )
        val canConfirm = model.canConfirmTarget
        val confirmLabel = when {
            model.isSubmitting -> "正在确认…"
            !canConfirm -> "确认施术"
            else -> "确认施术 · ${model.totalCost}点"
        }
        drawCentered(
            batch,
            fonts.body,
            confirmLabel,
            btnDrawerConfirmRect.x + btnDrawerConfirmRect.width / 2f,
            btnDrawerConfirmRect.y + 32f,
            if (canConfirm) Color.WHITE else Color(0.66f, 0.66f, 0.62f, 1f),
            alpha,
            embolden = true
        )
    }

    private fun drawReceiptText(batch: SpriteBatch, fonts: FontSet, alpha: Float) {
        if (alpha <= 0.001f) return
        val card = baseLayout.receiptRect
        drawCentered(batch, fonts.receipt, model.confirmedReceiptTitle, card.x + card.width / 2f, card.y + 222f, GOLD, alpha, embolden = true)

        val selection = receiptSelectionSnapshot ?: model.confirmedSelection ?: model.draftSelection()
        drawLeft(batch, fonts.small, "本人方案", card.x + 56f, card.y + 176f, INK, alpha, outline = true)
        val plan = describeSelection(selection)
        val planFont = if (plan.length > 10) fonts.body else fonts.card
        drawLeft(batch, planFont, plan, card.x + 56f, card.y + 150f, JADE, alpha, embolden = true)
        drawLeft(batch, fonts.body, "预留费用 ${selection.cost()}点", card.x + 56f, card.y + 118f, INK, alpha)

        drawLeft(batch, fonts.body, receiptFeedbackText, card.x + 22f, card.y + 88f, INK, alpha)
        drawLeft(
            batch,
            fonts.small,
            "方案已锁定，不可撤销",
            card.x + 22f,
            card.y + 60f,
            Color(0.96f, 0.90f, 0.76f, 1f),
            alpha
        )
    }

    private fun drawSamples(
        batch: SpriteBatch,
        shape: ShapeRenderer,
        tileSkin: TileSkin?,
        fallbackFont: BitmapFont,
        panelAlpha: Float,
        drawerAlpha: Float
    ) {
        if (tileSkin == null) return
        if (panelAlpha <= 0.001f && drawerAlpha <= 0.001f) return
        cardTileBuffer.clear()
        val prior = Color(batch.color)
        try {
            batch.color = Color.WHITE
            // 偷天换日大卡左斜示例牌：只随准备栏淡出，不叠加到抽屉层。
            // 滑入淡出期间不绘制示例牌，避免 TileDrawBuffer 的线框 pass 与已开启的 batch 交错。
            if (panelAlpha > 0.999f) {
                drawSampleRow(
                    tileSkin,
                    TARGET_SAMPLE_TILES.getValue(ShuffleTarget.CHINITSU_SOU).take(3),
                    btnOpenTargetRect.x + 60f,
                    btnOpenTargetRect.y + 42f,
                    38f,
                    56f,
                    0f,
                    16f,
                    panelAlpha,
                    22f
                )
            }
            if (drawerAlpha > 0.999f) {
                for ((target, rect) in btnTargets) {
                    val tiles = TARGET_SAMPLE_TILES[target] ?: continue
                    val lift = selectionLift(target)
                    drawSampleRow(
                        tileSkin,
                        tiles,
                        rect.x + rect.width / 2f,
                        rect.y + lift + 38f,
                        30f,
                        45f,
                        0f,
                        6f,
                        drawerAlpha
                    )
                }
            }
            if (!cardTileBuffer.isEmpty()) {
                cardTileBuffer.flush(batch, shape, fallbackFont)
            }
        } finally {
            batch.color = prior
        }
    }

    private fun drawSampleRow(
        tileSkin: TileSkin,
        tiles: List<TileView>,
        centerX: Float,
        baseY: Float,
        width: Float,
        height: Float,
        lift: Float,
        fanStep: Float,
        alpha: Float,
        stepX: Float = width + 3f
    ) {
        if (tiles.isEmpty()) return
        val step = stepX
        val total = (tiles.size - 1) * step
        val start = centerX - total / 2f
        val middle = (tiles.size - 1) / 2f
        for (i in tiles.indices) {
            val angle = (i - middle) * fanStep
            val x = start + i * step - width / 2f
            tileSkin.appendRotatedTileDraw(
                buf = cardTileBuffer,
                tile = tiles[i],
                x = x,
                y = baseY + lift,
                width = width,
                height = height,
                isHover = false,
                isDrawn = false,
                alpha = alpha,
                rotationDeg = angle
            )
        }
    }

    private fun drawSelectionOutlines(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        for ((target, rect) in btnTargets) {
            val lift = selectionLift(target)
            val selected = model.target == target
            if (selected) {
                shape.color = Color(0.98f, 0.82f, 0.38f, alpha)
                shape.rect(rect.x + 1f, rect.y + lift + 1f, rect.width - 2f, rect.height - 2f)
                shape.rect(rect.x + 4f, rect.y + lift + 4f, rect.width - 8f, rect.height - 8f)
            } else {
                shape.color = Color(0.52f, 0.42f, 0.22f, 0.65f * alpha)
                shape.rect(rect.x, rect.y, rect.width, rect.height)
            }
        }
    }

    /** 键盘焦点框：与按钮/目标卡共用同一组 stage 矩形，不另建输入坐标。 */
    private fun drawFocusOutline(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        val rect = focusedRect() ?: return
        shape.color = Color(1.0f, 0.84f, 0.42f, 0.95f * alpha)
        shape.rect(rect.x - 3f, rect.y - 3f, rect.width + 6f, rect.height + 6f)
        shape.rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f)
    }

    private fun drawPanelOutlines(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        if (isSeeThroughEntryVisible) {
            shape.color = if (model.isSeeThroughSelected) JADE else Color(0.45f, 0.55f, 0.42f, alpha)
            shape.rect(btnSeeThroughRect.x, btnSeeThroughRect.y, btnSeeThroughRect.width, btnSeeThroughRect.height)
        }
        shape.color = Color(0.906f, 0.725f, 0.325f, 0.65f * alpha)
        shape.rect(btnPrimaryRect.x, btnPrimaryRect.y, btnPrimaryRect.width, btnPrimaryRect.height)
    }

    private fun drawDrawerOutlines(shape: ShapeRenderer, alpha: Float) {
        if (alpha <= 0.001f) return
        if (isSeeThroughEntryVisible) {
            shape.color = if (model.isSeeThroughSelected) JADE else Color(0.45f, 0.55f, 0.42f, alpha)
            shape.rect(
                btnDrawerSeeThroughRect.x,
                btnDrawerSeeThroughRect.y,
                btnDrawerSeeThroughRect.width,
                btnDrawerSeeThroughRect.height
            )
        }
    }

    private fun drawSeeThroughControls(shape: ShapeRenderer, panelAlpha: Float, drawerAlpha: Float) {
        if (isSeeThroughEntryVisible) {
            if (panelAlpha > 0.001f) {
                drawCheckbox(
                    shape,
                    btnSeeThroughRect.x + btnSeeThroughRect.width - 32f,
                    btnSeeThroughRect.y + 22f,
                    model.isSeeThroughSelected,
                    panelAlpha
                )
            }
            if (drawerAlpha > 0.001f) {
                drawCheckbox(
                    shape,
                    btnDrawerSeeThroughRect.x + btnDrawerSeeThroughRect.width - 32f,
                    btnDrawerSeeThroughRect.y + 20f,
                    model.isSeeThroughSelected,
                    drawerAlpha
                )
            }
        }
        // 眼睛形金饰，不依赖字体里的特殊符号。
        if (panelAlpha > 0.001f && isSeeThroughEntryVisible) {
            drawEye(shape, btnSeeThroughRect.x + 16f, btnSeeThroughRect.y + 27f, panelAlpha)
        }
        if (drawerAlpha > 0.001f && isSeeThroughEntryVisible) {
            drawEye(shape, btnDrawerSeeThroughRect.x + 16f, btnDrawerSeeThroughRect.y + 27f, drawerAlpha)
        }
        if (drawerAlpha > 0.001f && model.target == null) {
            // 短状态点：只提示需要选择，不把长句挤进按钮。
            shape.color = Color(0.98f, 0.45f, 0.35f, drawerAlpha)
            shape.circle(btnDrawerCostRect.x + 7f, btnDrawerCostRect.y + 44f, 2.5f, 10)
        }
    }

    private fun drawCheckbox(shape: ShapeRenderer, x: Float, y: Float, checked: Boolean, alpha: Float) {
        shape.color = if (checked) Color(0.906f, 0.725f, 0.325f, alpha) else Color(0.10f, 0.16f, 0.12f, 0.9f * alpha)
        shape.rect(x, y, 18f, 18f)
        if (checked) {
            shape.color = Color(0.055f, 0.10f, 0.07f, alpha)
            shape.rectLine(x + 4f, y + 9f, x + 8f, y + 4.5f, 2.6f)
            shape.rectLine(x + 8f, y + 4.5f, x + 15f, y + 14f, 2.6f)
        }
    }

    private fun drawEye(shape: ShapeRenderer, x: Float, y: Float, alpha: Float) {
        shape.color = Color(0.906f, 0.725f, 0.325f, alpha)
        // 用两个三角拼成简洁眼形，避免依赖纹理。
        shape.triangle(x, y, x + 13f, y + 7f, x, y + 14f)
        shape.triangle(x + 26f, y, x + 13f, y + 7f, x + 26f, y + 14f)
        shape.color = Color(0.08f, 0.14f, 0.10f, alpha)
        shape.circle(x + 13f, y + 7f, 3.2f, 12)
    }

    private fun drawReceiptDecorations(shape: ShapeRenderer, alpha: Float) {
        val card = baseLayout.receiptRect
        shape.color = Color(0.906f, 0.725f, 0.325f, alpha)
        // 金色勾选。
        shape.rectLine(card.x + 26f, card.y + 124f, card.x + 34f, card.y + 114f, 5f)
        shape.rectLine(card.x + 34f, card.y + 114f, card.x + 50f, card.y + 140f, 5f)

        if (receiptFeedbackText.contains("等待")) {
            // 四点跟在等待句尾，不另起一行，也不漂到右侧空白。
            val dotY = card.y + 90f
            for (i in 0..3) {
                shape.circle(card.x + 220f + i * 10f, dotY, 2.4f, 10)
            }
        }
    }

    private fun selectionLift(target: ShuffleTarget): Float {
        if (model.target != target) return 0f
        if (selectedCardChangedAtMs < 0L) return 4f
        val elapsed = (nowMs() - selectedCardChangedAtMs).coerceIn(0L, 120L)
        val p = elapsed.toFloat() / 120f
        return 4f * (1f - (1f - p) * (1f - p) * (1f - p))
    }

    private fun drawLeft(
        batch: SpriteBatch,
        font: StageFont,
        text: String,
        x: Float,
        top: Float,
        color: Color,
        alpha: Float,
        embolden: Boolean = false,
        outline: Boolean = false
    ) {
        font.draw(
            batch = batch,
            text = text,
            x = x,
            top = top,
            color = color,
            shadow = true,
            embolden = embolden,
            outline = outline,
            alpha = alpha
        )
    }

    private fun drawLeftInBox(
        batch: SpriteBatch,
        font: StageFont,
        text: String,
        x: Float,
        box: Rectangle,
        color: Color,
        alpha: Float,
        embolden: Boolean = false,
        outline: Boolean = false
    ) {
        val top = box.y + (box.height + font.height(text)) / 2f
        drawLeft(batch, font, text, x, top, color, alpha, embolden, outline)
    }

    private fun drawCentered(
        batch: SpriteBatch,
        font: StageFont,
        text: String,
        centerX: Float,
        top: Float,
        color: Color,
        alpha: Float,
        embolden: Boolean = false,
        outline: Boolean = false
    ) {
        font.drawCentered(
            batch = batch,
            text = text,
            centerX = centerX,
            top = top,
            color = color,
            shadow = true,
            embolden = embolden,
            outline = outline,
            alpha = alpha
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
        // 借用 UiAssets 管理的字体/纹理与生命周期；本组件不释放共享资源。
    }
}

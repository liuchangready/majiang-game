package com.majiang.presentation.game

import com.badlogic.gdx.utils.TimeUtils

/**
 * 开局准备阶段的局部滑入时序模型 (RoundPreparationMotion)。
 *
 * 设计来源：[doc/ui/round-preparation/DESIGN.md](file:///D:/majiang/doc/ui/round-preparation/DESIGN.md) §7「动画与输入」。
 *
 * 核心契约：
 * 1. 右侧准备栏从右移入 72 单位 / 220ms；
 * 2. 目标抽屉从右移入 160 单位 / 280ms easeOutCubic，原准备栏同步淡出；
 * 3. 动画位移同时作用于绘制与命中矩形（见 [ShufflePreparationPanel]），
 *    绝不允许「看到的按钮」与「点得到的按钮」错位；
 * 4. `motionEnabled = false`（减少动画）时直接进入终态，功能与请求时序完全不变；
 * 5. 纯时序模型，不持有 GL / 会话 / 权威状态，可无头断言。
 */
class RoundPreparationMotion(
    private val motionEnabled: Boolean = true,
    private val clock: () -> Long = { TimeUtils.millis() }
) {

    companion object {
        /** 准备栏入场的右侧位移（逻辑像素）。 */
        const val PANEL_OFFSET_X = 72f

        /** 准备栏入场时长（毫秒）。 */
        const val PANEL_DURATION_MS = 220L

        /** 目标抽屉入场的右侧位移（逻辑像素）。 */
        const val DRAWER_OFFSET_X = 160f

        /** 目标抽屉入场时长（毫秒）。 */
        const val DRAWER_DURATION_MS = 280L
    }

    private val panelTrack = SlideTrack(PANEL_OFFSET_X, PANEL_DURATION_MS)
    private val drawerTrack = SlideTrack(DRAWER_OFFSET_X, DRAWER_DURATION_MS)

    init {
        // 初始为「抽屉关闭」终态：准备栏完全可见，抽屉停在屏外。
        drawerTrack.snapTo(DRAWER_OFFSET_X)
    }

    /** 准备栏是否已经出现在准备阶段（用于区分「首次入场」与「仅抽屉切换」）。 */
    var isPanelRevealed: Boolean = false
        private set

    /** 抽屉是否展开（与模型页同步，动画期间也保持 true）。 */
    var isDrawerOpen: Boolean = false
        private set

    /** 准备栏当前绘制位移（仅在入场动画中非 0）。 */
    val panelOffsetX: Float get() = panelTrack.offset

    /** 抽屉当前绘制位移（仅在展开/收起动画中非 0）。 */
    val drawerOffsetX: Float get() = drawerTrack.offset

    /** 准备栏不透明度（抽屉展开时淡出）。 */
    val panelAlpha: Float get() = (drawerTrack.offset / DRAWER_OFFSET_X).coerceIn(0f, 1f)

    /** 抽屉不透明度。 */
    val drawerAlpha: Float get() = (1f - drawerTrack.offset / DRAWER_OFFSET_X).coerceIn(0f, 1f)

    /** 是否仍有动画在跑（此时命中矩形仍随位移移动，不需要额外门控）。 */
    val isAnimating: Boolean get() = panelTrack.isAnimating || drawerTrack.isAnimating

    /** 准备阶段首次出现：准备栏自右滑入。 */
    fun revealPanel(nowMs: Long = clock()) {
        isPanelRevealed = true
        if (!motionEnabled) {
            panelTrack.snapTo(0f)
            drawerTrack.snapTo(DRAWER_OFFSET_X)
            return
        }
        if (panelTrack.offset <= 0f && !panelTrack.isAnimating) {
            panelTrack.snapTo(PANEL_OFFSET_X)
        }
        panelTrack.moveTo(0f, nowMs)
        drawerTrack.moveTo(DRAWER_OFFSET_X, nowMs)
    }

    /** 抽屉展开：准备栏同步淡出。 */
    fun openDrawer(nowMs: Long = clock()) {
        isDrawerOpen = true
        if (!motionEnabled) {
            drawerTrack.snapTo(0f)
            return
        }
        if (drawerTrack.offset >= DRAWER_OFFSET_X && !drawerTrack.isAnimating) {
            drawerTrack.snapTo(DRAWER_OFFSET_X)
        }
        drawerTrack.moveTo(0f, nowMs)
    }

    /** 抽屉收起：抽屉退出，准备栏恢复。 */
    fun closeDrawer(nowMs: Long = clock()) {
        isDrawerOpen = false
        if (!motionEnabled) {
            drawerTrack.snapTo(DRAWER_OFFSET_X)
            return
        }
        drawerTrack.moveTo(DRAWER_OFFSET_X, nowMs)
    }

    /** 准备阶段结束：立刻落到终态（不再保留任何位移）。 */
    fun reset() {
        isPanelRevealed = false
        isDrawerOpen = false
        panelTrack.snapTo(0f)
        drawerTrack.snapTo(DRAWER_OFFSET_X)
    }

    /**
     * 立刻收起抽屉（不做滑出动画）。
     *
     * 确认回执必须是一帧「已收起」的只读状态：如果抽屉还在滑出，回执就会被
     * 旧抽屉控件（返回 / 直接开局）遮住，验收要求的只读回执不成立。
     */
    fun snapDrawerClosed() {
        isDrawerOpen = false
        drawerTrack.snapTo(DRAWER_OFFSET_X)
    }

    /** 推进动画时钟。 */
    fun update(nowMs: Long = clock()) {
        if (!motionEnabled) {
            panelTrack.snapTo(0f)
            drawerTrack.snapTo(if (isDrawerOpen) 0f else DRAWER_OFFSET_X)
            return
        }
        panelTrack.update(nowMs)
        drawerTrack.update(nowMs)
    }

    /**
     * 单条滑轨：从 from 偏移插值到 to 偏移，easeOutCubic。
     */
    private class SlideTrack(private val maxOffset: Float, private val durationMs: Long) {
        private var startedAtMs: Long? = null
        private var fromOffset = 0f
        private var targetOffset = 0f

        var offset: Float = 0f
            private set

        val isAnimating: Boolean get() = startedAtMs != null

        fun moveTo(target: Float, nowMs: Long) {
            if (target == targetOffset && startedAtMs == null) return
            fromOffset = offset
            targetOffset = target.coerceIn(0f, maxOffset)
            startedAtMs = nowMs
        }

        fun snapTo(target: Float) {
            targetOffset = target.coerceIn(0f, maxOffset)
            offset = targetOffset
            fromOffset = targetOffset
            startedAtMs = null
        }

        fun update(nowMs: Long) {
            val started = startedAtMs ?: return
            val elapsed = (nowMs - started).coerceIn(0L, durationMs)
            val p = elapsed.toFloat() / durationMs.toFloat()
            val eased = 1f - (1f - p) * (1f - p) * (1f - p)
            offset = fromOffset + (targetOffset - fromOffset) * eased
            if (elapsed >= durationMs) {
                offset = targetOffset
                startedAtMs = null
            }
        }
    }
}

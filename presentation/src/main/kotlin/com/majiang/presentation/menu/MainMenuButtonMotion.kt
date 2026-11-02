package com.majiang.presentation.menu

import com.badlogic.gdx.utils.TimeUtils

/**
 * 被点击菜单项自身的“向右滑出 → 收回”时序模型（纯 Kotlin，无 GL、无会话依赖）。
 *
 * 用户语义（2026-10-22 更正）：点击时应当是“被点的按钮自己向右滑”，
 * 背景、标题、吉祥物、其余按钮与全屏遮罩必须保持屏幕坐标稳定。
 * 因此本模型只产出**单个菜单项的正 X 位移**，绝不驱动相机或投影平移。
 *
 * 设计：
 * - 滑出：0 → [MAX_OFFSET_X]（正 X），持续 [OUTBOUND_MS]，easeOutCubic；
 * - 收回：[MAX_OFFSET_X] → 0，持续 [RETRACT_MS]，easeInOutCubic（两端速度为 0，无跳变）；
 * - 单次点击反馈总时长 [TOTAL_MS]；
 * - [motionEnabled] = false 时 [start] 直接落到终态：不位移、不阻塞、无任何视觉等待；
 * - 位移期间该菜单项禁止再次激活（由 [MainMenuLayout] 的命中门禁与
 *   [MainMenuView.activate] 双重保证），落位后立即恢复可点。
 *
 * 与首次入场序列完全独立：入场由 [MainMenuLayout.frame] 的 delay/ease 负责，
 * 点击反馈只由本模型负责，两者互不重播。
 */
class MainMenuButtonMotion(
    private val motionEnabled: Boolean = true,
    private val clock: () -> Long = { TimeUtils.millis() }
) {
    companion object {
        /** 滑出峰值位移（逻辑像素，正 X）。 */
        const val MAX_OFFSET_X = 56f

        /** 滑出时长（毫秒）。 */
        const val OUTBOUND_MS = 160L

        /** 收回时长（毫秒）。 */
        const val RETRACT_MS = 140L

        /** 单次点击反馈总时长（毫秒）。 */
        const val TOTAL_MS = OUTBOUND_MS + RETRACT_MS

        fun easeOutCubic(p: Float): Float {
            val clamped = p.coerceIn(0f, 1f)
            return 1f - (1f - clamped) * (1f - clamped) * (1f - clamped)
        }

        fun easeInOutCubic(p: Float): Float {
            val clamped = p.coerceIn(0f, 1f)
            return if (clamped < 0.5f) {
                4f * clamped * clamped * clamped
            } else {
                val shifted = -2f * clamped + 2f
                1f - (shifted * shifted * shifted) / 2f
            }
        }
    }

    /** 点击反馈的阶段机。 */
    enum class Phase { IDLE, OUTBOUND, RETRACT, DONE }

    private var phase: Phase = Phase.IDLE
    private var target: MainMenuItemId? = null
    private var startedAtMs: Long = 0L
    private var lastNowMs: Long = 0L
    private var pausedAtMs: Long? = null
    private var offsetValue: Float = 0f

    /** 当前阶段。 */
    val currentPhase: Phase get() = phase

    /** 位移进行中（此期间该项禁止再次激活）。 */
    val isAnimating: Boolean get() = phase == Phase.OUTBOUND || phase == Phase.RETRACT

    /** 当前被点项（仅位移期间非空）。 */
    val activeItemId: MainMenuItemId? get() = if (isAnimating) target else null

    /** 当前正 X 位移（逻辑像素）；非位移期间恒为 0。 */
    val offsetX: Float get() = if (isAnimating) offsetValue else 0f

    /** 距离本次点击反馈开始的毫秒数。 */
    val elapsedMs: Long get() = if (isAnimating) (lastNowMs - startedAtMs).coerceAtLeast(0L) else 0L

    /**
     * 启动一次点击反馈。重复点击同一项会重置计时（但 [MainMenuView] 已在
     * 位移期间禁止重复激活，正常路径不会走到这里）。
     */
    fun start(id: MainMenuItemId, nowMs: Long = clock()) {
        if (!motionEnabled) {
            reset()
            return
        }
        target = id
        startedAtMs = nowMs
        pausedAtMs = null
        phase = Phase.OUTBOUND
        offsetValue = 0f
    }

    /** 推进到 [nowMs]。暂停期间不推进。 */
    fun update(nowMs: Long = clock()) {
        if (pausedAtMs != null) return
        lastNowMs = nowMs
        if (phase != Phase.OUTBOUND && phase != Phase.RETRACT) {
            offsetValue = 0f
            return
        }
        val elapsed = nowMs - startedAtMs
        when {
            elapsed <= 0L -> {
                phase = Phase.OUTBOUND
                offsetValue = 0f
            }
            elapsed < OUTBOUND_MS -> {
                phase = Phase.OUTBOUND
                offsetValue = MAX_OFFSET_X * easeOutCubic(elapsed.toFloat() / OUTBOUND_MS.toFloat())
            }
            elapsed < TOTAL_MS -> {
                phase = Phase.RETRACT
                val p = (elapsed - OUTBOUND_MS).toFloat() / RETRACT_MS.toFloat()
                offsetValue = MAX_OFFSET_X * (1f - easeInOutCubic(p))
            }
            else -> {
                phase = Phase.DONE
                offsetValue = 0f
            }
        }
    }

    /** 立即回到静止态（关闭覆盖层、返回主菜单、暂停或隐藏时调用）。 */
    fun reset() {
        phase = Phase.IDLE
        target = null
        startedAtMs = 0L
        lastNowMs = 0L
        pausedAtMs = null
        offsetValue = 0f
    }

    /** 暂停：冻结时间轴，恢复时整体平移，保证不出现跳变。 */
    fun pause(nowMs: Long = clock()) {
        if (isAnimating) pausedAtMs = nowMs
    }

    /** 恢复：把暂停期间流逝的时间从时间轴上扣掉。 */
    fun resume(nowMs: Long = clock()) {
        val pausedAt = pausedAtMs ?: return
        pausedAtMs = null
        if (isAnimating) {
            startedAtMs += (nowMs - pausedAt).coerceAtLeast(0L)
        }
    }
}

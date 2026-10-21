package com.majiang.presentation.menu

import com.badlogic.gdx.utils.TimeUtils

/**
 * 目标内容（联机连接框、秘籍/设置及其子面板）从右轻滑入的纯时序模型。
 *
 * 正式主菜单六项的首次/返回入场仍由 [MainMenuLayout] 独立负责；本模型只作用于
 * “点击某个入口后新出现的目标内容”，主菜单底图与吉祥物不参与位移。
 *
 * 设计：
 * - 位移 +220 逻辑像素，持续 280ms，easeOutCubic；
 * - `motionEnabled = false` 时 [start] 直接落到终态，立即可操作；
 * - 只有 [isActive] 为 true 时 [offsetX] 才非 0。MenuScreen 在 offset 非 0 时
 *   门控目标输入，落位后 offset 恒为 0，因此绘制位移与命中坐标在可点击时完全一致。
 */
class MenuContentMotion(
    private val motionEnabled: Boolean = true,
    private val clock: () -> Long = { TimeUtils.millis() }
) {
    companion object {
        /** 目标内容起始右侧偏移（逻辑像素）。 */
        const val OFFSET_X = 220f

        /** 目标内容滑入时长（毫秒）。 */
        const val DURATION_MS = 280L
    }

    private var startedAtMs: Long? = null
    private var elapsedMs: Long = DURATION_MS
    private var progressValue: Float = 1f
    private var offsetValue: Float = 0f

    /** 是否仍处于滑入过程；true 时目标输入应被门控。 */
    var isActive: Boolean = false
        private set

    /** 0..1 的线性时间进度（easeOutCubic 前的 p）。 */
    val progress: Float get() = progressValue

    /** 已流逝时间（毫秒），落位后等于 [DURATION_MS]。 */
    val elapsed: Long get() = elapsedMs

    /** 当前绘制/命中位移；非动画期间恒为 0。 */
    val offsetX: Float get() = if (isActive) offsetValue else 0f

    /**
     * 开始一次新的目标内容滑入。
     *
     * 同一帧内重复调用不会累加位移；只有新的状态切换才应调用 [start]。
     */
    fun start(nowMs: Long = clock()) {
        if (!motionEnabled) {
            snap()
            return
        }
        startedAtMs = nowMs
        elapsedMs = 0L
        progressValue = 0f
        offsetValue = OFFSET_X
        isActive = true
    }

    /** 直接进入终态（减少动画、返回 MAIN、关闭覆盖层时使用）。 */
    fun snap() {
        startedAtMs = null
        elapsedMs = DURATION_MS
        progressValue = 1f
        offsetValue = 0f
        isActive = false
    }

    /** 推进到 [nowMs]；到达时长后自动落位。 */
    fun update(nowMs: Long = clock()) {
        if (!motionEnabled) {
            snap()
            return
        }
        val startedAt = startedAtMs ?: return
        elapsedMs = (nowMs - startedAt).coerceIn(0L, DURATION_MS)
        val p = elapsedMs.toFloat() / DURATION_MS.toFloat()
        progressValue = p
        val eased = 1f - (1f - p) * (1f - p) * (1f - p)
        if (elapsedMs >= DURATION_MS) {
            snap()
        } else {
            offsetValue = OFFSET_X * (1f - eased)
        }
    }
}

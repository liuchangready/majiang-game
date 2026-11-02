package com.majiang.presentation.menu

import com.badlogic.gdx.utils.TimeUtils

/**
 * 覆盖弹层（千术秘籍 / 游戏设置）的淡入模型（纯时序，无位移）。
 *
 * 2026-10-22 用户更正后的语义：点击入口时“向右滑”的是被点的按钮本身，
 * 目标弹层必须固定在原位。因此这里只做 alpha 淡入，
 * 绝不再平移相机或投影，背景/标题/吉祥物/其余按钮的屏幕坐标保持不变。
 *
 * - 遮罩 alpha 与弹窗面板的入场时长均为 [DURATION_MS]；
 * - [motionEnabled] = false 时立即为终态（alpha = 1），无任何视觉等待；
 * - 该模型只影响绘制，不延迟任何请求或权威 tick。
 */
class MenuOverlayReveal(
    private val motionEnabled: Boolean = true,
    private val clock: () -> Long = { TimeUtils.millis() }
) {
    companion object {
        /** 淡入时长（毫秒）。 */
        const val DURATION_MS = 140L
    }

    private var startedAtMs: Long? = null
    private var alphaValue: Float = 1f

    /** 淡入是否进行中。 */
    var isActive: Boolean = false
        private set

    /** 当前 alpha（0..1）；非淡入期间恒为 1。 */
    val alpha: Float get() = alphaValue

    /** 开始一次淡入。 */
    fun start(nowMs: Long = clock()) {
        if (!motionEnabled) {
            snap()
            return
        }
        startedAtMs = nowMs
        alphaValue = 0f
        isActive = true
    }

    /** 推进到 [nowMs]。 */
    fun update(nowMs: Long = clock()) {
        if (!motionEnabled) {
            snap()
            return
        }
        val startedAt = startedAtMs ?: return
        val elapsed = (nowMs - startedAt).coerceIn(0L, DURATION_MS)
        val p = elapsed.toFloat() / DURATION_MS.toFloat()
        // smoothstep：两端速度为 0，遮罩不会突兀地压暗菜单。
        alphaValue = p * p * (3f - 2f * p)
        if (elapsed >= DURATION_MS) {
            snap()
        }
    }

    /** 立即进入终态。 */
    fun snap() {
        startedAtMs = null
        alphaValue = 1f
        isActive = false
    }
}

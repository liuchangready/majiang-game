package com.majiang.presentation.common.render

import com.badlogic.gdx.math.Vector2

/**
 * 正式 APP 唯一舞台变换 (TableStageViewport)。
 *
 * 全部可交互内容都绘制在中央 16:9 设计画布 1280×720 上，再等比 fit 到窗口安全区。
 * 窗口环境只作为无输入的背景，由 screen 投影单独 cover 铺满；牌桌、HUD、准备栏、
 * 目标抽屉、回执、结算和千术二级面板共用本类的 stage 变换。
 *
 * 坐标约定：
 * - [stageLeft]/[stageTop] 是窗口逻辑坐标，左上角原点；
 * - 输入坐标同样来自 Gdx.input（窗口逻辑坐标，Y 向下）；
 * - 设计局部坐标是 libGDX 左下角原点，Y 向上；
 * - 帧缓冲视口/裁剪使用 framebuffer 像素，不与输入坐标混用。
 *
 * 该类是纯几何，不读写 Gdx，便于无头回归直接断言。
 */
class TableStageViewport private constructor(
    val windowWidth: Int,
    val windowHeight: Int,
    val framebufferWidth: Int,
    val framebufferHeight: Int,
    val scale: Float,
    val stageLeft: Float,
    val stageTop: Float,
    val stageWidth: Float,
    val stageHeight: Float
) {

    companion object {
        const val DESIGN_WIDTH = 1280f
        const val DESIGN_HEIGHT = 720f
        const val MIN_SCALE = 0.0001f

        val EMPTY = TableStageViewport(0, 0, 0, 0, 0f, 0f, 0f, 0f, 0f)

        /**
         * `available = window - safeInsets`，随后 `scale = min(availableW/1280, availableH/720)`。
         * 零宽高或安全区耗尽时返回不可绘制 view，绝不做除零。
         */
        fun compute(
            windowWidth: Int,
            windowHeight: Int,
            framebufferWidth: Int = windowWidth,
            framebufferHeight: Int = windowHeight,
            safeLeft: Float = 0f,
            safeTop: Float = 0f,
            safeRight: Float = 0f,
            safeBottom: Float = 0f
        ): TableStageViewport {
            val w = windowWidth.coerceAtLeast(0)
            val h = windowHeight.coerceAtLeast(0)
            if (w <= 0 || h <= 0) return EMPTY

            val availableW = (w - safeLeft - safeRight).coerceAtLeast(0f)
            val availableH = (h - safeTop - safeBottom).coerceAtLeast(0f)
            val scale = minOf(availableW / DESIGN_WIDTH, availableH / DESIGN_HEIGHT)
            if (scale <= MIN_SCALE) return EMPTY

            val stageW = DESIGN_WIDTH * scale
            val stageH = DESIGN_HEIGHT * scale
            val stageX = safeLeft + (availableW - stageW) / 2f
            val stageY = safeTop + (availableH - stageH) / 2f
            return TableStageViewport(
                windowWidth = w,
                windowHeight = h,
                framebufferWidth = framebufferWidth.coerceAtLeast(1),
                framebufferHeight = framebufferHeight.coerceAtLeast(1),
                scale = scale,
                stageLeft = stageX,
                stageTop = stageY,
                stageWidth = stageW,
                stageHeight = stageH
            )
        }
    }

    val drawable: Boolean
        get() = scale > MIN_SCALE && stageWidth > 0f && stageHeight > 0f && windowWidth > 0 && windowHeight > 0

    /** 帧缓冲相对窗口的像素比；高 DPI 只用于 GL 视口、裁剪和字体输出，不再次乘进输入。 */
    val framebufferRatio: Float
        get() = if (windowWidth <= 0 || windowHeight <= 0) 1f else minOf(
            framebufferWidth.toFloat() / windowWidth.toFloat(),
            framebufferHeight.toFloat() / windowHeight.toFloat()
        )

    val stageRight: Float get() = stageLeft + stageWidth
    val stageBottomFromTop: Float get() = stageTop + stageHeight

    /** GL viewport/scissor 左下角 Y（帧缓冲像素）。 */
    val framebufferStageY: Int
        get() = Math.round((windowHeight - stageTop - stageHeight) * framebufferRatio)

    val framebufferStageX: Int
        get() = Math.round(stageLeft * framebufferRatio)

    val framebufferStageWidth: Int
        get() = Math.max(1, Math.round(stageWidth * framebufferRatio))

    val framebufferStageHeight: Int
        get() = Math.max(1, Math.round(stageHeight * framebufferRatio))

    fun containsScreen(screenX: Float, screenY: Float): Boolean =
        drawable && screenX >= stageLeft && screenX < stageRight &&
            screenY >= stageTop && screenY < stageBottomFromTop

    /**
     * 窗口输入 → 设计局部。舞台外返回 null；调用方不得对舞台外环境执行按钮命中。
     */
    fun toStage(screenX: Float, screenY: Float, out: Vector2 = Vector2()): Vector2? {
        if (!containsScreen(screenX, screenY)) return null
        out.set(
            (screenX - stageLeft) / scale,
            DESIGN_HEIGHT - (screenY - stageTop) / scale
        )
        return out
    }

    /** 设计局部 → 窗口逻辑坐标（Y 向下）。 */
    fun toWindow(localX: Float, localY: Float, out: Vector2 = Vector2()): Vector2 {
        out.set(
            stageLeft + localX * scale,
            stageTop + (DESIGN_HEIGHT - localY) * scale
        )
        return out
    }

    /** 设计局部矩形 → 窗口物理像素矩形（左上角原点），用于像素证据与截图断言。 */
    fun localRectToWindow(
        localX: Float,
        localY: Float,
        localWidth: Float,
        localHeight: Float,
        out: com.badlogic.gdx.math.Rectangle = com.badlogic.gdx.math.Rectangle()
    ): com.badlogic.gdx.math.Rectangle {
        out.set(
            stageLeft + localX * scale,
            stageTop + (DESIGN_HEIGHT - localY - localHeight) * scale,
            localWidth * scale,
            localHeight * scale
        )
        return out
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TableStageViewport) return false
        return windowWidth == other.windowWidth &&
            windowHeight == other.windowHeight &&
            framebufferWidth == other.framebufferWidth &&
            framebufferHeight == other.framebufferHeight &&
            scale == other.scale &&
            stageLeft == other.stageLeft &&
            stageTop == other.stageTop &&
            stageWidth == other.stageWidth &&
            stageHeight == other.stageHeight
    }

    override fun hashCode(): Int {
        var result = windowWidth
        result = 31 * result + windowHeight
        result = 31 * result + framebufferWidth
        result = 31 * result + framebufferHeight
        result = 31 * result + scale.hashCode()
        result = 31 * result + stageLeft.hashCode()
        result = 31 * result + stageTop.hashCode()
        result = 31 * result + stageWidth.hashCode()
        result = 31 * result + stageHeight.hashCode()
        return result
    }

    override fun toString(): String =
        "stage(window=${windowWidth}x$windowHeight, fb=${framebufferWidth}x$framebufferHeight, " +
            "scale=$scale, left=$stageLeft, top=$stageTop, w=$stageWidth, h=$stageHeight)"
}

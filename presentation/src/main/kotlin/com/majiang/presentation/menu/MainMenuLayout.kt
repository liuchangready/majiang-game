package com.majiang.presentation.menu

import com.badlogic.gdx.math.Rectangle

/**
 * 正式主菜单六个真实入口。
 *
 * 顺序即视觉顺序与键盘 Tab/上下顺序；坐标来自
 * `assets/ui/main-menu/manifest.json`，原点为 libGDX 左下角。
 */
enum class MainMenuItemId(
    val label: String,
    val primary: Boolean,
    val delayMs: Long,
    val iconIndex: Int
) {
    SOLO("单机对局", true, 120L, 0),
    ONLINE("联机对战", true, 200L, 1),
    SKIN("牌面皮肤", false, 280L, 2),
    SECRETS("千术秘籍", false, 360L, 3),
    SETTINGS("游戏设置", false, 440L, 4),
    EXIT("退出游戏", false, 520L, 5)
}

/**
 * 单个入口的静态规格（最终落位矩形）。
 */
data class MainMenuItemSpec(
    val id: MainMenuItemId,
    val baseRect: Rectangle
)

/**
 * 被点击菜单项的正 X 位移（点击反馈）。
 *
 * 与首次入场序列是两个独立事件：入场由 [MainMenuLayout.frame] 的 delay/ease 负责，
 * 本状态只描述“被点的那一项”额外的正 X 位移，绝不作用于其它项或背景。
 *
 * @param itemId 位移作用项；null 表示没有点击反馈。
 * @param offsetX 正 X 位移（逻辑像素）。
 * @param blocking 位移进行中（该项暂时不可再次激活，也不参与命中）。
 */
data class MainMenuButtonOffset(
    val itemId: MainMenuItemId? = null,
    val offsetX: Float = 0f,
    val blocking: Boolean = false
) {
    companion object {
        val NONE = MainMenuButtonOffset()
    }
}

/**
 * 某一帧的入口布局。绘制与命中必须共用同一个 [MainMenuFrame]，
 * 不能一个用 [MainMenuItemSpec.baseRect]、另一个用动画中间值。
 */
data class MainMenuItemFrame(
    val spec: MainMenuItemSpec,
    val rect: Rectangle,
    val alpha: Float,
    val progress: Float,
    val settled: Boolean,
    /** 该项当前的正 X 位移（逻辑像素，0 表示未位移）。 */
    val motionOffsetX: Float = 0f,
    /** 该项是否正处于点击反馈位移中（期间禁止再次激活）。 */
    val motionActive: Boolean = false
) {
    val id: MainMenuItemId get() = spec.id
}

data class MainMenuFrame(
    val elapsedMs: Long,
    val items: List<MainMenuItemFrame>
) {
    fun item(id: MainMenuItemId): MainMenuItemFrame? = items.firstOrNull { it.id == id }

    /**
     * 只有已落位且不在点击反馈位移中的入口参与命中；
     * 移动中的最终矩形与位移中的按钮都不接收点击，杜绝重复触发与命中终点空白。
     */
    fun hitTest(x: Float, y: Float): MainMenuItemId? =
        items.firstOrNull { it.settled && !it.motionActive && it.rect.contains(x, y) }?.id

    val allSettled: Boolean get() = items.all { it.settled }
}

/**
 * 主菜单纯布局/时序模型：无 GL、无会话、无平台依赖，可直接在无头测试中验证。
 *
 * 设计公式（见 doc/ui/main-menu/DESIGN.md）：
 * `p = clamp((elapsedMs - delayMs) / 420, 0, 1)`
 * `ease = 1 - (1 - p)^3`
 * `drawX = baseX + 96 * (1 - ease)`，`alpha = p`
 * 未达到 `delayMs + 420` 的入口不接受激活。
 */
object MainMenuLayout {
    const val DESIGN_WIDTH = 1280f
    const val DESIGN_HEIGHT = 720f

    const val SAFE_LEFT = 64f
    const val SAFE_RIGHT = 1192f
    const val SAFE_BOTTOM = 58f
    const val SAFE_TOP = 688f

    const val ENTRY_DURATION_MS = 420L
    const val ENTRY_OFFSET_X = 96f
    const val TOTAL_ENTRY_MS = 940L

    val titleRect = Rectangle(64f, 612f, 620f, 76f)
    val subtitleRect = Rectangle(72f, 574f, 600f, 26f)
    val mascotRect = Rectangle(96f, 64f, 530f, 506f)
    val statusRect = Rectangle(752f, 58f, 440f, 38f)

    val items: List<MainMenuItemSpec> = listOf(
        MainMenuItemSpec(MainMenuItemId.SOLO, Rectangle(752f, 480f, 440f, 80f)),
        MainMenuItemSpec(MainMenuItemId.ONLINE, Rectangle(752f, 388f, 440f, 80f)),
        MainMenuItemSpec(MainMenuItemId.SKIN, Rectangle(752f, 318f, 440f, 58f)),
        MainMenuItemSpec(MainMenuItemId.SECRETS, Rectangle(752f, 248f, 440f, 58f)),
        MainMenuItemSpec(MainMenuItemId.SETTINGS, Rectangle(752f, 178f, 440f, 58f)),
        MainMenuItemSpec(MainMenuItemId.EXIT, Rectangle(752f, 108f, 440f, 58f))
    )

    fun spec(id: MainMenuItemId): MainMenuItemSpec = items.first { it.id == id }

    /**
     * 单项允许的最大正 X 位移：保证位移后的右边界不超过设计安全宽度 1280。
     */
    fun maxButtonOffsetX(id: MainMenuItemId): Float {
        val rect = spec(id).baseRect
        return (DESIGN_WIDTH - (rect.x + rect.width)).coerceAtLeast(0f)
    }

    /**
     * 某一帧的六项布局。
     *
     * @param elapsedMs 入场时间轴。
     * @param motionEnabled false 时直接进入最终落位态（无入场、无位移、无等待）。
     * @param buttonOffset 被点项的正 X 位移；只作用于该项，背景与其它项不受影响。
     */
    fun frame(
        elapsedMs: Long,
        motionEnabled: Boolean = true,
        buttonOffset: MainMenuButtonOffset = MainMenuButtonOffset.NONE
    ): MainMenuFrame {
        val effectiveElapsed = if (!motionEnabled) TOTAL_ENTRY_MS else elapsedMs.coerceAtLeast(0L)
        val frames = items.map { spec ->
            val local = (effectiveElapsed - spec.id.delayMs).coerceIn(0L, ENTRY_DURATION_MS)
            val progress = if (!motionEnabled) 1f else local.toFloat() / ENTRY_DURATION_MS.toFloat()
            val eased = if (!motionEnabled) 1f else 1f - (1f - progress) * (1f - progress) * (1f - progress)
            val entryOffset = if (!motionEnabled) 0f else ENTRY_OFFSET_X * (1f - eased)
            val moving = buttonOffset.blocking && buttonOffset.itemId == spec.id
            val motionOffset = if (moving) {
                buttonOffset.offsetX.coerceIn(0f, maxButtonOffsetX(spec.id))
            } else {
                0f
            }
            val drawX = spec.baseRect.x + entryOffset + motionOffset
            val settled = if (!motionEnabled) {
                true
            } else {
                effectiveElapsed >= spec.id.delayMs + ENTRY_DURATION_MS
            }
            MainMenuItemFrame(
                spec = spec,
                rect = Rectangle(drawX, spec.baseRect.y, spec.baseRect.width, spec.baseRect.height),
                alpha = if (!motionEnabled) 1f else progress,
                progress = progress,
                settled = settled,
                motionOffsetX = motionOffset,
                motionActive = moving
            )
        }
        return MainMenuFrame(effectiveElapsed, frames)
    }

    fun skinDisplayName(skinName: String): String = when (skinName.uppercase()) {
        "WIKIPEDIA" -> "维基百科"
        "CLASSIC" -> "经典牌面"
        else -> skinName
    }
}

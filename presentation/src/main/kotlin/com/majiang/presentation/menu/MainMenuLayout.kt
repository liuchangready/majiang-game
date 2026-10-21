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
 * 某一帧的入口布局。绘制与命中必须共用同一个 [MainMenuFrame]，
 * 不能一个用 [MainMenuItemSpec.baseRect]、另一个用动画中间值。
 */
data class MainMenuItemFrame(
    val spec: MainMenuItemSpec,
    val rect: Rectangle,
    val alpha: Float,
    val progress: Float,
    val settled: Boolean
) {
    val id: MainMenuItemId get() = spec.id
}

data class MainMenuFrame(
    val elapsedMs: Long,
    val items: List<MainMenuItemFrame>
) {
    fun item(id: MainMenuItemId): MainMenuItemFrame? = items.firstOrNull { it.id == id }

    /** 只有已落位入口参与命中；移动中的最终矩形不接收点击。 */
    fun hitTest(x: Float, y: Float): MainMenuItemId? =
        items.firstOrNull { it.settled && it.rect.contains(x, y) }?.id

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

    fun frame(elapsedMs: Long, motionEnabled: Boolean = true): MainMenuFrame {
        val effectiveElapsed = if (!motionEnabled) TOTAL_ENTRY_MS else elapsedMs.coerceAtLeast(0L)
        val frames = items.map { spec ->
            val local = (effectiveElapsed - spec.id.delayMs).coerceIn(0L, ENTRY_DURATION_MS)
            val progress = if (!motionEnabled) 1f else local.toFloat() / ENTRY_DURATION_MS.toFloat()
            val eased = if (!motionEnabled) 1f else 1f - (1f - progress) * (1f - progress) * (1f - progress)
            val drawX = spec.baseRect.x + if (!motionEnabled) 0f else ENTRY_OFFSET_X * (1f - eased)
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
                settled = settled
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

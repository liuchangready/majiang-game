package com.majiang.presentation.common.render

import com.badlogic.gdx.math.Rectangle

/**
 * 正式牌桌持续场景几何 (TableStageLayout)。
 *
 * 全部常量来自 `RESPONSIVE-DESIGN.md` 与 `responsive-spec.json` 的 1280×720 左上角
 * 设计坐标，此处统一转换为 libGDX 左下角原点：`y = 720 - top - height`。
 *
 * 背景、桌面、罗盘、四席 HUD 和托盘都固定在这张 16:9 设计画布上；窗口变化只由
 * [TableStageViewport] 做整体 fit，不再扩大 worldW/worldH，也不把面板锚到物理窗口右沿。
 */
object TableStageLayout {

    const val DESIGN_WIDTH = 1280f
    const val DESIGN_HEIGHT = 720f

    /** Z0 全屏环境（screen 空间 cover）与 Z10 固定桌面。 */
    val ENVIRONMENT = Rectangle(0f, 0f, 1280f, 720f)
    val BOARD = Rectangle(32f, 24f, 1216f, 620f)

    /** 罗盘：准备阶段 204，发牌后 188。 */
    val PREP_COMPASS = Rectangle(328f, 262f, 204f, 204f)
    val PLAY_COMPASS = Rectangle(546f, 271f, 188f, 188f)

    /** 发牌后手牌托盘与对家手牌托盘（左上角 208,596 与 436,8）。 */
    val HAND_TRAY = Rectangle(208f, 14f, 852f, 110f)
    val TOP_TRAY = Rectangle(436f, 658f, 408f, 54f)

    /** 发牌后右下操作底板。按钮本身见 RightActionColumnLayout。 */
    val ACTION_PLATE = Rectangle(1068f, 4f, 194f, 252f)

    /** 准备阶段底部短提示（左上角 208,626,448×50）。 */
    val PREP_HINT = Rectangle(208f, 44f, 448f, 50f)

    enum class SeatSlot {
        SELF,
        RIGHT,
        TOP,
        LEFT
    }

    data class SeatHud(
        val portrait: Rectangle,
        val plate: Rectangle
    )

    /**
     * 四席 HUD 是“头像精灵 + 独立名牌”两个矩形，名牌不再塞进头像旁 72px 缝隙。
     * 转换为 libGDX Y：`720 - top - height`。
     */
    private val PREP_SELF = SeatHud(Rectangle(16f, 106f, 144f, 144f), Rectangle(16f, 38f, 176f, 60f))
    private val PREP_RIGHT = SeatHud(Rectangle(800f, 414f, 128f, 128f), Rectangle(796f, 350f, 140f, 60f))
    private val PREP_TOP = SeatHud(Rectangle(368f, 592f, 120f, 120f), Rectangle(496f, 630f, 176f, 60f))
    private val PREP_LEFT = SeatHud(Rectangle(16f, 392f, 144f, 144f), Rectangle(16f, 328f, 176f, 60f))

    private val PLAY_SELF = SeatHud(Rectangle(16f, 56f, 112f, 112f), Rectangle(16f, 4f, 176f, 52f))
    private val PLAY_RIGHT = SeatHud(Rectangle(1148f, 380f, 112f, 112f), Rectangle(1084f, 316f, 184f, 60f))
    private val PLAY_TOP = SeatHud(Rectangle(308f, 604f, 112f, 112f), Rectangle(132f, 638f, 176f, 60f))
    private val PLAY_LEFT = SeatHud(Rectangle(16f, 380f, 112f, 112f), Rectangle(12f, 316f, 176f, 60f))

    /** 抽屉展开时只移动右家公开 HUD；抽屉左缘 688，名牌右缘 676。 */
    private val DRAWER_RIGHT = SeatHud(Rectangle(544f, 436f, 124f, 124f), Rectangle(536f, 372f, 140f, 60f))

    fun preparationHud(slot: SeatSlot): SeatHud = when (slot) {
        SeatSlot.SELF -> PREP_SELF
        SeatSlot.RIGHT -> PREP_RIGHT
        SeatSlot.TOP -> PREP_TOP
        SeatSlot.LEFT -> PREP_LEFT
    }

    fun playingHud(slot: SeatSlot): SeatHud = when (slot) {
        SeatSlot.SELF -> PLAY_SELF
        SeatSlot.RIGHT -> PLAY_RIGHT
        SeatSlot.TOP -> PLAY_TOP
        SeatSlot.LEFT -> PLAY_LEFT
    }

    /** 抽屉进度 0..1 时右家 HUD 在桌面位置与抽屉左侧位置间插值。 */
    fun drawerRightHud(progress: Float): SeatHud {
        val p = progress.coerceIn(0f, 1f)
        return SeatHud(
            lerp(PREP_RIGHT.portrait, DRAWER_RIGHT.portrait, p),
            lerp(PREP_RIGHT.plate, DRAWER_RIGHT.plate, p)
        )
    }

    fun absoluteSeat(viewerSeat: Int, slot: SeatSlot): Int {
        val relative = when (slot) {
            SeatSlot.SELF -> 0
            SeatSlot.RIGHT -> 1
            SeatSlot.TOP -> 2
            SeatSlot.LEFT -> 3
        }
        return (viewerSeat + relative + 4) % 4
    }

    private fun lerp(a: Rectangle, b: Rectangle, p: Float): Rectangle = Rectangle(
        a.x + (b.x - a.x) * p,
        a.y + (b.y - a.y) * p,
        a.width + (b.width - a.width) * p,
        a.height + (b.height - a.height) * p
    )
}

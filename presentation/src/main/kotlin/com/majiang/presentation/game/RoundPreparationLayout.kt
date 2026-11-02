package com.majiang.presentation.game

import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.ShuffleTarget

/**
 * 正式 APP 开局准备阶段的几何布局 (RoundPreparationLayout)。
 *
 * 设计来源：[doc/ui/round-preparation/RESPONSIVE-DESIGN.md] 与
 * `responsive-validation/responsive-spec.json`。所有常量均为 1280×720 设计坐标，
 * 在此转换为 libGDX 左下角原点：`y = 720 - top - height`。
 *
 * 准备栏、抽屉、回执是 stage 内固定设计矩形；窗口变化由 TableStageViewport 整体
 * fit，不再按可见窗口宽度平移，也不再压窄桌面。
 */
class RoundPreparationLayout(
    /** 保留给纯几何断言的可见设计区，正式路径恒为 1280×720。 */
    val visible: Rectangle = DEFAULT_VISIBLE
) {

    companion object {
        const val DESIGN_WIDTH = 1280f
        const val DESIGN_HEIGHT = 720f

        /** 右侧准备栏。 */
        const val PANEL_WIDTH = 312f
        const val PANEL_HEIGHT = 624f
        const val PANEL_X = 936f
        const val PANEL_Y = 48f

        /** 目标抽屉。 */
        const val DRAWER_WIDTH = 560f
        const val DRAWER_HEIGHT = 640f
        const val DRAWER_X = 688f
        const val DRAWER_Y = 40f

        /** 主按钮与目标卡尺寸。 */
        const val PRIMARY_WIDTH = 272f
        const val PRIMARY_HEIGHT = 76f
        const val TARGET_CARD_WIDTH = 252f
        const val TARGET_CARD_HEIGHT = 114f

        /** 抽屉底部动作。 */
        const val DRAWER_ACTION_WIDTH = 252f
        const val DRAWER_ACTION_HEIGHT = 48f

        const val RIGHT_MARGIN = 32f
        const val EDGE_MARGIN = 8f

        val DEFAULT_VISIBLE = Rectangle(0f, 0f, DESIGN_WIDTH, DESIGN_HEIGHT)
        val DEFAULT = RoundPreparationLayout(DEFAULT_VISIBLE)

        /** 仅保留给旧调用方的纯设计区别名；正式 APP 不再扩展世界。 */
        fun designVisibleRect(): Rectangle = Rectangle(DEFAULT_VISIBLE)
    }

    /** 右侧准备栏（不含动画位移）。 */
    val panelRect: Rectangle = Rectangle(PANEL_X, PANEL_Y, PANEL_WIDTH, PANEL_HEIGHT)

    /** 目标抽屉（不含动画位移）。 */
    val drawerRect: Rectangle = Rectangle(DRAWER_X, DRAWER_Y, DRAWER_WIDTH, DRAWER_HEIGHT)

    /** 倒计时条：设计 `(960,124,264,40)`。 */
    val countdownRect: Rectangle = panelLocal(24f, 508f, 264f, 40f)

    /** 偷天换日入口：设计 `(956,188,272,156)`。 */
    val openTargetRect: Rectangle = panelLocal(20f, 328f, 272f, 156f)

    /** 看破整行：设计 `(956,360,272,64)`。 */
    val seeThroughRect: Rectangle = panelLocal(20f, 248f, 272f, 64f)

    /** 当前方案 / 费用：设计 `(956,464,272,66)`。 */
    val summaryRect: Rectangle = panelLocal(20f, 142f, 272f, 66f)

    /** 主要按钮：设计 `(956,552,272,76)`。 */
    val primaryActionRect: Rectangle = panelLocal(20f, 44f, PRIMARY_WIDTH, PRIMARY_HEIGHT)

    /** 剩余出千点：设计 `(956,638,272,20)`。 */
    val balanceRect: Rectangle = panelLocal(20f, 14f, 272f, 20f)

    /** 准备栏标题中心与顶部：34px 标题在 (956,68,272,48) 内。 */
    val panelTitleCenterX: Float get() = 1092f
    val panelTitleTop: Float get() = 720f - 80f

    /** 抽屉标题顶部：28px 标题在 (708,57,412,43) 内。 */
    val drawerTitleTop: Float get() = 720f - 68f

    /** 返回：设计 `(1132,60,96,44)`。 */
    val drawerBackRect: Rectangle = drawerLocal(444f, 576f, 96f, 44f)

    /** 抽屉提示：设计 `(708,112,520,24)`。 */
    val drawerHintRect: Rectangle = drawerLocal(20f, 544f, 520f, 24f)

    /** 看破/费用条：左 `(708,544,252,58)`，右 `(976,544,252,58)`。 */
    val drawerSeeThroughRect: Rectangle = drawerLocal(20f, 78f, 252f, 58f)
    val drawerCostRect: Rectangle = drawerLocal(288f, 78f, 252f, 58f)

    /** 底部动作：直接开局 `(708,616,252,48)`，确认施术 `(976,616,252,48)`。 */
    val drawerSkipRect: Rectangle = drawerLocal(20f, 16f, DRAWER_ACTION_WIDTH, DRAWER_ACTION_HEIGHT)
    val drawerConfirmRect: Rectangle = drawerLocal(288f, 16f, DRAWER_ACTION_WIDTH, DRAWER_ACTION_HEIGHT)

    /** 六张目标卡（两列三行，左上角 y=154/284/414）。 */
    val targetCardRects: Map<ShuffleTarget, Rectangle> = linkedMapOf(
        ShuffleTarget.CHINITSU_MAN to drawerLocal(20f, 412f, TARGET_CARD_WIDTH, TARGET_CARD_HEIGHT),
        ShuffleTarget.CHINITSU_SOU to drawerLocal(288f, 412f, TARGET_CARD_WIDTH, TARGET_CARD_HEIGHT),
        ShuffleTarget.CHINITSU_PIN to drawerLocal(20f, 282f, TARGET_CARD_WIDTH, TARGET_CARD_HEIGHT),
        ShuffleTarget.DAISANGEN to drawerLocal(288f, 282f, TARGET_CARD_WIDTH, TARGET_CARD_HEIGHT),
        ShuffleTarget.KOKUSHI to drawerLocal(20f, 152f, TARGET_CARD_WIDTH, TARGET_CARD_HEIGHT),
        ShuffleTarget.DAISUUSHII to drawerLocal(288f, 152f, TARGET_CARD_WIDTH, TARGET_CARD_HEIGHT)
    )

    /** 本人确认回执：设计 `(944,160,304,260)`。 */
    val receiptRect: Rectangle = Rectangle(944f, 300f, 304f, 260f)

    /**
     * 准备阶段剩余安全区。只供公开 HUD / 可交互区参考；背景和桌面从不使用它。
     * 现在右缘是设计坐标中的面板/抽屉左缘，不再随物理窗口宽度移动。
     */
    fun safeTableRect(drawerVisible: Boolean): Rectangle {
        val right = if (drawerVisible) drawerRect.x else panelRect.x
        return Rectangle(
            EDGE_MARGIN,
            EDGE_MARGIN,
            (right - EDGE_MARGIN * 2f).coerceAtLeast(120f),
            (DESIGN_HEIGHT - EDGE_MARGIN * 2f).coerceAtLeast(120f)
        )
    }

    private fun panelLocal(offsetX: Float, offsetY: Float, width: Float, height: Float): Rectangle =
        Rectangle(panelRect.x + offsetX, panelRect.y + offsetY, width, height)

    private fun drawerLocal(offsetX: Float, offsetY: Float, width: Float, height: Float): Rectangle =
        Rectangle(drawerRect.x + offsetX, drawerRect.y + offsetY, width, height)
}

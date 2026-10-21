package com.majiang.presentation.game

import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.ShuffleTarget

/**
 * 正式 APP 开局准备阶段的几何布局 (RoundPreparationLayout)。
 *
 * 设计来源：[doc/ui/round-preparation/DESIGN.md](file:///D:/majiang/doc/ui/round-preparation/DESIGN.md) §6「图层与区域」。
 *
 * 核心契约：
 * 1. 纯几何，无 GL / 会话 / 权威依赖，可直接被无头单测断言；
 * 2. 1280×720 参考分辨率下与设计坐标逐项一致：
 *    - 右侧准备栏 `(936, 48, 312, 624)`；
 *    - 目标抽屉 `(688, 40, 560, 640)`；
 *    - 准备栏主要动作 `(956, 162, 272, 64)`；
 *    - 目标卡两列三行，各 `252×94`，列 x `708/976`、行 y `462/352/242`；
 *    - 抽屉底部两动作各 `252×54`，x `708/976`、y `64`（右侧确认、左侧零费用直接开始）；
 * 3. 可见区域（超宽 / 窄屏）由 [visible] 给出，准备栏与抽屉始终锚定在可见右缘，
 *    纵向以可见区中心对齐，因此 960×540 与超宽都不会把控件推出屏幕；
 * 4. [safeTableRect] 给出准备阶段剩余安全桌区，公开四席与桌面只允许落在该区域内，
 *    不改变座位、实体牌或引擎规则；
 * 5. 本类只描述几何，不描述交互状态；交互状态见 [ShufflePreparationModel]。
 */
class RoundPreparationLayout(
    /** 当前可见虚拟世界区域（libGDX 左下角原点）。 */
    val visible: Rectangle = DEFAULT_VISIBLE
) {

    companion object {
        /** 设计参考分辨率。 */
        const val DESIGN_WIDTH = 1280f
        const val DESIGN_HEIGHT = 720f

        /** 右侧准备栏尺寸（DESIGN §6）。 */
        const val PANEL_WIDTH = 312f
        const val PANEL_HEIGHT = 624f

        /** 目标抽屉尺寸（DESIGN §6）。 */
        const val DRAWER_WIDTH = 560f
        const val DRAWER_HEIGHT = 640f

        /** 准备栏主要动作尺寸（DESIGN §6）。 */
        const val PRIMARY_WIDTH = 272f
        const val PRIMARY_HEIGHT = 64f

        /** 目标卡尺寸（DESIGN §6）。 */
        const val TARGET_CARD_WIDTH = 252f
        const val TARGET_CARD_HEIGHT = 94f

        /** 抽屉底部动作尺寸（DESIGN §6）。 */
        const val DRAWER_ACTION_WIDTH = 252f
        const val DRAWER_ACTION_HEIGHT = 54f

        /** 面板与抽屉相对可见右缘的留白。 */
        const val RIGHT_MARGIN = 32f

        /** 可见区与面板之间的最小留白。 */
        const val EDGE_MARGIN = 8f

        /** 准备栏内容左内边距。 */
        const val PANEL_PAD = 20f

        /** 抽屉内容左内边距。 */
        const val DRAWER_PAD = 20f

        /** 抽屉两列的列 x（相对抽屉左缘）。 */
        const val DRAWER_COLUMN_X = 20f
        const val DRAWER_COLUMN_X_RIGHT = 288f

        /** 抽屉三行的行 y（相对抽屉下缘）。 */
        const val DRAWER_ROW_Y_TOP = 422f
        const val DRAWER_ROW_Y_MIDDLE = 312f
        const val DRAWER_ROW_Y_BOTTOM = 202f

        /** 抽屉底部动作 y（相对抽屉下缘）。 */
        const val DRAWER_ACTION_Y = 24f

        /** 准备栏主要动作 y（相对面板下缘）。 */
        const val PRIMARY_Y = 114f

        /** 1280×720 参考可见区。 */
        val DEFAULT_VISIBLE = Rectangle(0f, 0f, DESIGN_WIDTH, DESIGN_HEIGHT)

        /** 参考分辨率下的默认布局（单测与设计断言使用）。 */
        val DEFAULT = RoundPreparationLayout(DEFAULT_VISIBLE)

        /**
         * 由相机反投影推算当前可见的虚拟世界区域。
         *
         * libGDX 的 [com.badlogic.gdx.graphics.OrthographicCamera] 会按窗口宽高比调整视锥，
         * 因此不能假定可见区域恒为 1280×720；这里直接用与输入完全相同的反投影
         * 把屏幕四角映射回世界坐标，保证准备栏在 960×540 与超宽窗口都贴住真实右缘，
         * 并且绘制坐标与命中坐标始终一致。
         */
        fun visibleWorldRect(
            camera: com.badlogic.gdx.graphics.OrthographicCamera,
            screenWidth: Int,
            screenHeight: Int
        ): Rectangle {
            if (screenWidth <= 0 || screenHeight <= 0) return Rectangle(DEFAULT_VISIBLE)
            val topLeft = camera.unproject(com.badlogic.gdx.math.Vector3(0f, 0f, 0f))
            val bottomRight = camera.unproject(
                com.badlogic.gdx.math.Vector3(screenWidth.toFloat(), screenHeight.toFloat(), 0f)
            )
            val minX = minOf(topLeft.x, bottomRight.x)
            val maxX = maxOf(topLeft.x, bottomRight.x)
            val minY = minOf(topLeft.y, bottomRight.y)
            val maxY = maxOf(topLeft.y, bottomRight.y)
            if (maxX - minX <= 0f || maxY - minY <= 0f) return Rectangle(DEFAULT_VISIBLE)
            return Rectangle(minX, minY, maxX - minX, maxY - minY)
        }
    }

    private val visibleCenterX: Float get() = visible.x + visible.width / 2f
    private val visibleCenterY: Float get() = visible.y + visible.height / 2f

    /** 右侧紧凑准备栏（不含动画位移）。 */
    val panelRect: Rectangle = Rectangle(
        visible.x + visible.width - RIGHT_MARGIN - PANEL_WIDTH,
        alignY(PANEL_HEIGHT),
        PANEL_WIDTH,
        PANEL_HEIGHT
    )

    /** 目标抽屉（不含动画位移）。 */
    val drawerRect: Rectangle = Rectangle(
        visible.x + visible.width - RIGHT_MARGIN - DRAWER_WIDTH,
        alignY(DRAWER_HEIGHT),
        DRAWER_WIDTH,
        DRAWER_HEIGHT
    )

    /** 准备栏倒计时显示框。 */
    val countdownRect: Rectangle = Rectangle(
        panelRect.x + PANEL_WIDTH - 20f - 68f,
        panelRect.y + PANEL_HEIGHT - 54f,
        68f,
        32f
    )

    /** “偷天换日 · 30点”入口（只展开抽屉，不扣点不提交）。 */
    val openTargetRect: Rectangle = Rectangle(
        panelRect.x + PANEL_PAD - 4f,
        panelRect.y + 438f,
        280f,
        60f
    )

    /** 准备栏“看破 · 10点”勾选入口。 */
    val seeThroughRect: Rectangle = Rectangle(
        panelRect.x + PANEL_PAD - 4f,
        panelRect.y + 356f,
        280f,
        52f
    )

    /** 准备栏主要动作（开始本局 / 确认看破）。 */
    val primaryActionRect: Rectangle = Rectangle(
        panelRect.x + PANEL_PAD,
        panelRect.y + PRIMARY_Y,
        PRIMARY_WIDTH,
        PRIMARY_HEIGHT
    )

    /** 抽屉顶部“返回”。 */
    val drawerBackRect: Rectangle = Rectangle(
        drawerRect.x + DRAWER_WIDTH - 20f - 124f,
        drawerRect.y + DRAWER_HEIGHT - 54f,
        124f,
        44f
    )

    /** 抽屉“看破 · 10点”勾选入口。 */
    val drawerSeeThroughRect: Rectangle = Rectangle(
        drawerRect.x + DRAWER_PAD,
        drawerRect.y + 90f,
        252f,
        48f
    )

    /** 抽屉“直接开局 · 0点”。 */
    val drawerSkipRect: Rectangle = Rectangle(
        drawerRect.x + DRAWER_COLUMN_X,
        drawerRect.y + DRAWER_ACTION_Y,
        DRAWER_ACTION_WIDTH,
        DRAWER_ACTION_HEIGHT
    )

    /** 抽屉“确认施术”。 */
    val drawerConfirmRect: Rectangle = Rectangle(
        drawerRect.x + DRAWER_COLUMN_X_RIGHT,
        drawerRect.y + DRAWER_ACTION_Y,
        DRAWER_ACTION_WIDTH,
        DRAWER_ACTION_HEIGHT
    )

    /** 六张目标卡（两列三行，DESIGN §6）。 */
    val targetCardRects: Map<ShuffleTarget, Rectangle> = linkedMapOf(
        ShuffleTarget.CHINITSU_MAN to cardRect(DRAWER_COLUMN_X, DRAWER_ROW_Y_TOP),
        ShuffleTarget.CHINITSU_SOU to cardRect(DRAWER_COLUMN_X_RIGHT, DRAWER_ROW_Y_TOP),
        ShuffleTarget.CHINITSU_PIN to cardRect(DRAWER_COLUMN_X, DRAWER_ROW_Y_MIDDLE),
        ShuffleTarget.DAISANGEN to cardRect(DRAWER_COLUMN_X_RIGHT, DRAWER_ROW_Y_MIDDLE),
        ShuffleTarget.KOKUSHI to cardRect(DRAWER_COLUMN_X, DRAWER_ROW_Y_BOTTOM),
        ShuffleTarget.DAISUUSHII to cardRect(DRAWER_COLUMN_X_RIGHT, DRAWER_ROW_Y_BOTTOM)
    )

    /**
     * 准备阶段剩余安全桌区。
     *
     * 准备栏可见时右缘为 [panelRect] 的左缘；抽屉展开时右缘为 [drawerRect] 的左缘，
     * 保证公开席位与桌面永远不会被准备栏/抽屉遮没。
     */
    fun safeTableRect(drawerVisible: Boolean): Rectangle {
        val right = if (drawerVisible) drawerRect.x else panelRect.x
        return Rectangle(
            visible.x + EDGE_MARGIN,
            visible.y + EDGE_MARGIN,
            (right - visible.x - EDGE_MARGIN * 2f).coerceAtLeast(120f),
            (visible.height - EDGE_MARGIN * 2f).coerceAtLeast(120f)
        )
    }

    private fun alignY(height: Float): Float {
        val centered = visibleCenterY - height / 2f
        val min = visible.y + EDGE_MARGIN
        val max = visible.y + visible.height - height - EDGE_MARGIN
        if (max < min) return centered
        return centered.coerceIn(min, max)
    }

    private fun cardRect(columnX: Float, rowY: Float) = Rectangle(
        drawerRect.x + columnX,
        drawerRect.y + rowY,
        TARGET_CARD_WIDTH,
        TARGET_CARD_HEIGHT
    )
}

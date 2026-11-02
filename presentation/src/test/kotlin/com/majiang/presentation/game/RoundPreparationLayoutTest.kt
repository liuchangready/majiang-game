package com.majiang.presentation.game

import com.majiang.client.api.ShuffleTarget
import com.badlogic.gdx.math.Rectangle
import com.majiang.presentation.common.render.TableStageLayout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 正式牌桌与准备阶段几何契约测试。
 *
 * 以 RESPONSIVE-DESIGN.md / responsive-spec.json 的左上角设计坐标为唯一来源，
 * 逐项验证 libGDX 转换后的坐标、目标卡、准备栏、抽屉、回执和持续 HUD。
 */
class RoundPreparationLayoutTest {

    private val layout = RoundPreparationLayout.DEFAULT

    @Test
    fun `panel and drawer match the responsive design`() {
        assertEquals(936f, layout.panelRect.x, 0.01f)
        assertEquals(48f, layout.panelRect.y, 0.01f)
        assertEquals(312f, layout.panelRect.width, 0.01f)
        assertEquals(624f, layout.panelRect.height, 0.01f)

        assertEquals(688f, layout.drawerRect.x, 0.01f)
        assertEquals(40f, layout.drawerRect.y, 0.01f)
        assertEquals(560f, layout.drawerRect.width, 0.01f)
        assertEquals(640f, layout.drawerRect.height, 0.01f)
    }

    @Test
    fun `preparation entries use the new vertical rhythm`() {
        assertEquals(Rectangle(960f, 556f, 264f, 40f), layout.countdownRect)
        assertEquals(Rectangle(956f, 376f, 272f, 156f), layout.openTargetRect)
        assertEquals(Rectangle(956f, 296f, 272f, 64f), layout.seeThroughRect)
        assertEquals(Rectangle(956f, 190f, 272f, 66f), layout.summaryRect)
        assertEquals(Rectangle(956f, 92f, 272f, 76f), layout.primaryActionRect)
        assertEquals(Rectangle(956f, 62f, 272f, 20f), layout.balanceRect)
        assertTrue(layout.panelRect.contains(layout.primaryActionRect))
    }

    @Test
    fun `six target cards use the 252 by 114 illustration grid`() {
        val expected = mapOf(
            ShuffleTarget.CHINITSU_MAN to Rectangle(708f, 452f, 252f, 114f),
            ShuffleTarget.CHINITSU_SOU to Rectangle(976f, 452f, 252f, 114f),
            ShuffleTarget.CHINITSU_PIN to Rectangle(708f, 322f, 252f, 114f),
            ShuffleTarget.DAISANGEN to Rectangle(976f, 322f, 252f, 114f),
            ShuffleTarget.KOKUSHI to Rectangle(708f, 192f, 252f, 114f),
            ShuffleTarget.DAISUUSHII to Rectangle(976f, 192f, 252f, 114f)
        )
        assertEquals(6, layout.targetCardRects.size)
        for ((target, rect) in expected) {
            val actual = layout.targetCardRects.getValue(target)
            assertEquals(rect.x, actual.x, 0.01f, "$target x")
            assertEquals(rect.y, actual.y, 0.01f, "$target y")
            assertEquals(rect.width, actual.width, 0.01f, "$target width")
            assertEquals(rect.height, actual.height, 0.01f, "$target height")
            assertTrue(layout.drawerRect.contains(actual), "$target 必须落在抽屉内")
        }
    }

    @Test
    fun `drawer footer and back button match the responsive design`() {
        assertEquals(Rectangle(1132f, 616f, 96f, 44f), layout.drawerBackRect)
        assertEquals(Rectangle(708f, 118f, 252f, 58f), layout.drawerSeeThroughRect)
        assertEquals(Rectangle(976f, 118f, 252f, 58f), layout.drawerCostRect)
        assertEquals(Rectangle(708f, 56f, 252f, 48f), layout.drawerSkipRect)
        assertEquals(Rectangle(976f, 56f, 252f, 48f), layout.drawerConfirmRect)
    }

    @Test
    fun `receipt is upper right and independent of the old green card`() {
        assertEquals(Rectangle(944f, 300f, 304f, 260f), layout.receiptRect)
        assertTrue(layout.receiptRect.x > 900f)
        assertTrue(layout.receiptRect.y + layout.receiptRect.height < 600f)
    }

    @Test
    fun `scene background board and compass never depend on safe table area`() {
        assertEquals(Rectangle(0f, 0f, 1280f, 720f), TableStageLayout.ENVIRONMENT)
        assertEquals(Rectangle(32f, 24f, 1216f, 620f), TableStageLayout.BOARD)
        assertEquals(Rectangle(328f, 262f, 204f, 204f), TableStageLayout.PREP_COMPASS)
        assertEquals(Rectangle(546f, 271f, 188f, 188f), TableStageLayout.PLAY_COMPASS)
        assertEquals(Rectangle(208f, 14f, 852f, 110f), TableStageLayout.HAND_TRAY)
        assertEquals(Rectangle(436f, 658f, 408f, 54f), TableStageLayout.TOP_TRAY)
        assertEquals(Rectangle(208f, 44f, 448f, 50f), TableStageLayout.PREP_HINT)
    }

    @Test
    fun `four absolute seats map to new portrait and independent plate positions`() {
        assertEquals(0, TableStageLayout.absoluteSeat(0, TableStageLayout.SeatSlot.SELF))
        assertEquals(1, TableStageLayout.absoluteSeat(0, TableStageLayout.SeatSlot.RIGHT))
        assertEquals(2, TableStageLayout.absoluteSeat(0, TableStageLayout.SeatSlot.TOP))
        assertEquals(3, TableStageLayout.absoluteSeat(0, TableStageLayout.SeatSlot.LEFT))
        assertEquals(2, TableStageLayout.absoluteSeat(1, TableStageLayout.SeatSlot.RIGHT))

        assertEquals(Rectangle(16f, 106f, 144f, 144f), TableStageLayout.preparationHud(TableStageLayout.SeatSlot.SELF).portrait)
        assertEquals(Rectangle(16f, 38f, 176f, 60f), TableStageLayout.preparationHud(TableStageLayout.SeatSlot.SELF).plate)
        assertEquals(Rectangle(16f, 56f, 112f, 112f), TableStageLayout.playingHud(TableStageLayout.SeatSlot.SELF).portrait)
        assertEquals(Rectangle(16f, 4f, 176f, 52f), TableStageLayout.playingHud(TableStageLayout.SeatSlot.SELF).plate)
        assertEquals(Rectangle(1084f, 316f, 184f, 60f), TableStageLayout.playingHud(TableStageLayout.SeatSlot.RIGHT).plate)
        assertEquals(Rectangle(132f, 638f, 176f, 60f), TableStageLayout.playingHud(TableStageLayout.SeatSlot.TOP).plate)
        assertEquals(Rectangle(12f, 316f, 176f, 60f), TableStageLayout.playingHud(TableStageLayout.SeatSlot.LEFT).plate)
    }

    @Test
    fun `drawer progress moves only the right hud`() {
        val closed = TableStageLayout.drawerRightHud(0f)
        val open = TableStageLayout.drawerRightHud(1f)
        assertEquals(800f, closed.portrait.x, 0.01f)
        assertEquals(544f, open.portrait.x, 0.01f)
        assertEquals(536f, open.plate.x, 0.01f)
        assertEquals(536f + 140f, open.plate.x + open.plate.width, 0.01f)
        assertTrue(open.plate.x + open.plate.width <= layout.drawerRect.x - 12f + 0.01f)
    }

    @Test
    fun `every interactive rectangle stays inside the design stage`() {
        val rects = listOf(
            layout.panelRect,
            layout.drawerRect,
            layout.countdownRect,
            layout.openTargetRect,
            layout.seeThroughRect,
            layout.summaryRect,
            layout.primaryActionRect,
            layout.balanceRect,
            layout.drawerBackRect,
            layout.drawerSeeThroughRect,
            layout.drawerCostRect,
            layout.drawerSkipRect,
            layout.drawerConfirmRect,
            layout.receiptRect
        ) + layout.targetCardRects.values
        for (rect in rects) {
            assertTrue(rect.x >= 0f && rect.y >= 0f, "$rect 越出左下边界")
            assertTrue(rect.x + rect.width <= 1280f + 0.01f, "$rect 越出右边界")
            assertTrue(rect.y + rect.height <= 720f + 0.01f, "$rect 越出上边界")
        }
    }

    @Test
    fun `panel controls never overlap each other`() {
        assertFalse(layout.openTargetRect.overlaps(layout.seeThroughRect))
        assertFalse(layout.seeThroughRect.overlaps(layout.summaryRect))
        assertFalse(layout.summaryRect.overlaps(layout.primaryActionRect))
        assertFalse(layout.primaryActionRect.overlaps(layout.balanceRect))
        assertFalse(layout.primaryActionRect.overlaps(layout.countdownRect))
        assertTrue(layout.panelRect.contains(layout.openTargetRect))
        assertTrue(layout.panelRect.contains(layout.seeThroughRect))
        assertTrue(layout.panelRect.contains(layout.summaryRect))
        assertTrue(layout.panelRect.contains(layout.countdownRect))
    }

    @Test
    fun `safe table area never reaches under the fixed design panel or drawer`() {
        val panelSafe = layout.safeTableRect(drawerVisible = false)
        assertTrue(panelSafe.x + panelSafe.width <= layout.panelRect.x + 0.01f)
        assertTrue(panelSafe.x >= 0f)
        assertTrue(panelSafe.height > 0f)

        val drawerSafe = layout.safeTableRect(drawerVisible = true)
        assertTrue(drawerSafe.x + drawerSafe.width <= layout.drawerRect.x + 0.01f)
        assertTrue(drawerSafe.width < panelSafe.width, "抽屉展开后安全桌区必须更窄")
    }

    @Test
    fun `right action column matches the new 174 by 46 design`() {
        assertEquals(Rectangle(1078f, 208f, 174f, 46f), RightActionColumnLayout.tileMarkRect())
        assertEquals(Rectangle(1078f, 159f, 174f, 46f), RightActionColumnLayout.beamSwapRect())
        assertEquals(Rectangle(1078f, 110f, 174f, 46f), RightActionColumnLayout.sleeveBloomRect())
        assertEquals(Rectangle(1078f, 61f, 174f, 46f), RightActionColumnLayout.sortRect())
        assertEquals(Rectangle(1078f, 12f, 174f, 46f), RightActionColumnLayout.takeoverRect())
    }
}

package com.majiang.presentation.game

import com.majiang.client.api.ShuffleTarget
import com.badlogic.gdx.math.Rectangle
import com.majiang.presentation.common.render.PreparationTableRenderer
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 开局准备阶段几何布局契约测试 (RoundPreparationLayoutTest)。
 *
 * 断言 DESIGN §6 的建议坐标在 1280×720 下逐项成立，并保证超宽 / 窄屏时
 * 准备栏与抽屉始终锚定在可见右缘、公开席位不会落在准备栏或抽屉之下。
 */
class RoundPreparationLayoutTest {

    private val layout = RoundPreparationLayout.DEFAULT

    @Test
    fun `panel and drawer match the design coordinates`() {
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
    fun `primary action matches the design coordinates`() {
        assertEquals(956f, layout.primaryActionRect.x, 0.01f)
        assertEquals(162f, layout.primaryActionRect.y, 0.01f)
        assertEquals(272f, layout.primaryActionRect.width, 0.01f)
        assertEquals(64f, layout.primaryActionRect.height, 0.01f)
        assertTrue(layout.panelRect.contains(layout.primaryActionRect))
    }

    @Test
    fun `six target cards use the two column three row design grid`() {
        val expected = mapOf(
            ShuffleTarget.CHINITSU_MAN to Rectangle(708f, 462f, 252f, 94f),
            ShuffleTarget.CHINITSU_SOU to Rectangle(976f, 462f, 252f, 94f),
            ShuffleTarget.CHINITSU_PIN to Rectangle(708f, 352f, 252f, 94f),
            ShuffleTarget.DAISANGEN to Rectangle(976f, 352f, 252f, 94f),
            ShuffleTarget.KOKUSHI to Rectangle(708f, 242f, 252f, 94f),
            ShuffleTarget.DAISUUSHII to Rectangle(976f, 242f, 252f, 94f)
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
    fun `drawer bottom actions match the design coordinates`() {
        assertEquals(708f, layout.drawerSkipRect.x, 0.01f)
        assertEquals(976f, layout.drawerConfirmRect.x, 0.01f)
        assertEquals(64f, layout.drawerSkipRect.y, 0.01f)
        assertEquals(252f, layout.drawerSkipRect.width, 0.01f)
        assertEquals(54f, layout.drawerSkipRect.height, 0.01f)
        assertFalse(layout.drawerSkipRect.overlaps(layout.drawerConfirmRect))
        assertTrue(layout.drawerRect.contains(layout.drawerSkipRect))
        assertTrue(layout.drawerRect.contains(layout.drawerConfirmRect))
    }

    @Test
    fun `every interactive rectangle stays inside the reference viewport`() {
        val rects = listOf(
            layout.panelRect,
            layout.drawerRect,
            layout.countdownRect,
            layout.openTargetRect,
            layout.seeThroughRect,
            layout.primaryActionRect,
            layout.drawerBackRect,
            layout.drawerSeeThroughRect,
            layout.drawerSkipRect,
            layout.drawerConfirmRect
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
        assertFalse(layout.seeThroughRect.overlaps(layout.primaryActionRect))
        assertFalse(layout.primaryActionRect.overlaps(layout.countdownRect))
        assertTrue(layout.panelRect.contains(layout.openTargetRect))
        assertTrue(layout.panelRect.contains(layout.seeThroughRect))
        assertTrue(layout.panelRect.contains(layout.countdownRect))
    }

    @Test
    fun `safe table area never reaches under the panel or the drawer`() {
        val panelSafe = layout.safeTableRect(drawerVisible = false)
        assertTrue(panelSafe.x + panelSafe.width <= layout.panelRect.x + 0.01f)
        assertTrue(panelSafe.x >= 0f)
        assertTrue(panelSafe.height > 0f)

        val drawerSafe = layout.safeTableRect(drawerVisible = true)
        assertTrue(drawerSafe.x + drawerSafe.width <= layout.drawerRect.x + 0.01f)
        assertTrue(drawerSafe.width < panelSafe.width, "抽屉展开后安全桌区必须更窄")
    }

    @Test
    fun `ultra wide viewport keeps the panel anchored to the visible right edge`() {
        val visible = Rectangle(0f, 0f, 1920f, 720f)
        val wide = RoundPreparationLayout(visible)
        assertEquals(1920f - 32f - 312f, wide.panelRect.x, 0.01f)
        assertEquals(1920f - 32f - 560f, wide.drawerRect.x, 0.01f)
        assertTrue(wide.panelRect.x + wide.panelRect.width <= visible.x + visible.width)
        assertTrue(wide.drawerRect.x + wide.drawerRect.width <= visible.x + visible.width)
        assertTrue(wide.safeTableRect(drawerVisible = true).x + wide.safeTableRect(true).width <= wide.drawerRect.x + 0.01f)
    }

    @Test
    fun `narrow viewport shifts the panel but keeps it fully visible`() {
        val visible = Rectangle(-740f, 0f, 1280f, 720f)
        val narrow = RoundPreparationLayout(visible)
        assertTrue(narrow.panelRect.x >= visible.x - 0.01f)
        assertTrue(narrow.panelRect.x + narrow.panelRect.width <= visible.x + visible.width + 0.01f)
        assertTrue(narrow.drawerRect.x >= visible.x - 0.01f)
        assertTrue(narrow.drawerRect.x + narrow.drawerRect.width <= visible.x + visible.width + 0.01f)
    }

    // ------------------------------------------------------------------
    // 准备阶段占位文案与四席铭牌不得碰撞（1280 抽屉 / 1280 准备栏 / 960×540）
    // ------------------------------------------------------------------

    private fun assertPlaceholderTextAvoidsSeats(safeArea: Rectangle, caseName: String) {
        val band = PreparationTableRenderer.centralTextRect(safeArea)
        val plates = PreparationTableRenderer.seatPlates(safeArea)
        for (plate in plates) {
            assertFalse(
                band.overlaps(plate),
                "$caseName: 占位文字区 $band 与席位铭牌 $plate 碰撞"
            )
        }

        val titleWidth = PreparationTableRenderer.TITLE_TEXT.length *
            PreparationTableRenderer.CHINESE_FONT_SIZE * PreparationTableRenderer.TITLE_FONT_SCALE
        assertTrue(
            titleWidth <= band.width + 0.01f,
            "$caseName: 标题放不下（需要 $titleWidth，中央文字区 ${band.width}）"
        )
        for (line in PreparationTableRenderer.PLACEHOLDER_LINES) {
            val lineWidth = line.length *
                PreparationTableRenderer.CHINESE_FONT_SIZE * PreparationTableRenderer.PLACEHOLDER_FONT_SCALE
            assertTrue(
                lineWidth <= band.width + 0.01f,
                "$caseName: 占位说明「$line」放不下（需要 $lineWidth，中央文字区 ${band.width}）"
            )
        }
    }

    @Test
    fun `placeholder text never overlaps the seat plates at 1280 with the drawer open`() {
        val layout = RoundPreparationLayout(Rectangle(0f, 0f, 1280f, 720f))
        assertPlaceholderTextAvoidsSeats(layout.safeTableRect(drawerVisible = true), "1280 抽屉展开")
    }

    @Test
    fun `placeholder text never overlaps the seat plates at 1280 with the panel only`() {
        val layout = RoundPreparationLayout(Rectangle(0f, 0f, 1280f, 720f))
        assertPlaceholderTextAvoidsSeats(layout.safeTableRect(drawerVisible = false), "1280 准备栏")
    }

    @Test
    fun `placeholder text stays inside the band and readable at 960 by 540`() {
        val layout = RoundPreparationLayout(Rectangle(0f, 0f, 1280f, 720f))
        for (drawerVisible in listOf(false, true)) {
            val safeArea = layout.safeTableRect(drawerVisible)
            assertPlaceholderTextAvoidsSeats(safeArea, "960×540 视口")

            // 960×540 只是把 1280×720 的世界整体缩到 0.75，文字仍必须可读。
            val scale = 960f / 1280f
            val onScreen = PreparationTableRenderer.PLACEHOLDER_FONT_SCALE *
                PreparationTableRenderer.CHINESE_FONT_SIZE * scale
            assertTrue(
                onScreen >= 12f,
                "960×540 下占位说明只有 $onScreen 像素高，必须至少 12 像素才可读"
            )
            val titleOnScreen = PreparationTableRenderer.TITLE_FONT_SCALE *
                PreparationTableRenderer.CHINESE_FONT_SIZE * scale
            assertTrue(
                titleOnScreen >= 12f,
                "960×540 下占位标题只有 $titleOnScreen 像素高，必须至少 12 像素才可读"
            )
        }
    }
}

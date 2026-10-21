package com.majiang.presentation.menu

import com.badlogic.gdx.math.Rectangle
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 主菜单布局/时序/命中纯模型测试。
 *
 * 锁定 manifest 的六个最终矩形、120..520ms 延迟、420ms easeOutCubic、
 * 96 单位右侧移入与“未落位不可命中”的硬门禁。
 */
class MainMenuLayoutTest {

    private val delta = 0.01f

    @Test
    fun `six entries match manifest final rectangles and safe content area`() {
        assertEquals(Rectangle(752f, 480f, 440f, 80f), MainMenuLayout.spec(MainMenuItemId.SOLO).baseRect)
        assertEquals(Rectangle(752f, 388f, 440f, 80f), MainMenuLayout.spec(MainMenuItemId.ONLINE).baseRect)
        assertEquals(Rectangle(752f, 318f, 440f, 58f), MainMenuLayout.spec(MainMenuItemId.SKIN).baseRect)
        assertEquals(Rectangle(752f, 248f, 440f, 58f), MainMenuLayout.spec(MainMenuItemId.SECRETS).baseRect)
        assertEquals(Rectangle(752f, 178f, 440f, 58f), MainMenuLayout.spec(MainMenuItemId.SETTINGS).baseRect)
        assertEquals(Rectangle(752f, 108f, 440f, 58f), MainMenuLayout.spec(MainMenuItemId.EXIT).baseRect)
        assertEquals(Rectangle(64f, 612f, 620f, 76f), MainMenuLayout.titleRect)
        assertEquals(Rectangle(72f, 574f, 600f, 26f), MainMenuLayout.subtitleRect)
        assertEquals(Rectangle(96f, 64f, 530f, 506f), MainMenuLayout.mascotRect)
        assertEquals(Rectangle(752f, 58f, 440f, 38f), MainMenuLayout.statusRect)

        val expectedDelays = listOf(120L, 200L, 280L, 360L, 440L, 520L)
        assertEquals(expectedDelays, MainMenuLayout.items.map { it.id.delayMs })
        assertEquals(420L, MainMenuLayout.ENTRY_DURATION_MS)
        assertEquals(940L, MainMenuLayout.TOTAL_ENTRY_MS)

        MainMenuLayout.items.forEach { spec ->
            val rect = spec.baseRect
            assertTrue(rect.x >= MainMenuLayout.SAFE_LEFT, "${spec.id} must stay inside left safe edge")
            assertTrue(rect.x + rect.width <= MainMenuLayout.SAFE_RIGHT, "${spec.id} must stay inside right safe edge")
            assertTrue(rect.y >= MainMenuLayout.SAFE_BOTTOM, "${spec.id} must stay inside bottom safe edge")
            assertTrue(rect.y + rect.height <= MainMenuLayout.SAFE_TOP, "${spec.id} must stay inside top safe edge")
        }
    }

    @Test
    fun `entry starts at plus 96 and follows delayed easeOutCubic`() {
        val initial = MainMenuLayout.frame(0L)
        initial.items.forEach { item ->
            assertFalse(item.settled, "nothing is settled at 0ms")
            assertEquals(0f, item.alpha, delta)
            assertEquals(item.spec.baseRect.x + 96f, item.rect.x, delta)
        }

        // 单机 120ms 才开始移动。
        assertEquals(0f, MainMenuLayout.frame(120L).item(MainMenuItemId.SOLO)!!.alpha, delta)
        val at240 = MainMenuLayout.frame(240L).item(MainMenuItemId.SOLO)!!
        val expectedP = (240L - 120L).toFloat() / 420f
        val expectedEase = 1f - (1f - expectedP) * (1f - expectedP) * (1f - expectedP)
        assertEquals(expectedP, at240.alpha, delta)
        assertEquals(752f + 96f * (1f - expectedEase), at240.rect.x, delta)
        assertFalse(at240.settled, "solo is still moving at 240ms")

        // 联机 200ms 延迟：240ms 时只移动了 40ms。
        val online240 = MainMenuLayout.frame(240L).item(MainMenuItemId.ONLINE)!!
        assertEquals(40f / 420f, online240.alpha, delta)

        // 540ms 单机落位，其他入口仍在移动。
        val at540 = MainMenuLayout.frame(540L)
        assertTrue(at540.item(MainMenuItemId.SOLO)!!.settled)
        assertEquals(752f, at540.item(MainMenuItemId.SOLO)!!.rect.x, delta)
        assertEquals(1f, at540.item(MainMenuItemId.SOLO)!!.alpha, delta)
        assertFalse(at540.item(MainMenuItemId.ONLINE)!!.settled)
        assertFalse(at540.item(MainMenuItemId.EXIT)!!.settled)

        val landings = listOf(
            540L to MainMenuItemId.SOLO,
            620L to MainMenuItemId.ONLINE,
            700L to MainMenuItemId.SKIN,
            780L to MainMenuItemId.SECRETS,
            860L to MainMenuItemId.SETTINGS,
            940L to MainMenuItemId.EXIT
        )
        for ((atMs, id) in landings) {
            assertFalse(MainMenuLayout.frame(atMs - 1L).item(id)!!.settled, "$id must not settle before $atMs")
            assertTrue(MainMenuLayout.frame(atMs).item(id)!!.settled, "$id must settle at $atMs")
        }
        val finalFrame = MainMenuLayout.frame(940L)
        assertTrue(finalFrame.allSettled)
        finalFrame.items.forEach { item ->
            assertEquals(item.spec.baseRect.x, item.rect.x, delta)
            assertEquals(item.spec.baseRect.y, item.rect.y, delta)
            assertEquals(1f, item.alpha, delta)
        }
    }

    @Test
    fun `moving final rectangle never accepts hit and settled items enable independently`() {
        val at240 = MainMenuLayout.frame(240L)
        // 最终矩形中心在 240ms 时仍被移动中的按钮占据，但不得命中。
        assertNull(at240.hitTest(972f, 520f), "final solo rectangle must not be hittable during slide")
        assertNull(at240.hitTest(972f, 428f), "final online rectangle must not be hittable during slide")
        assertNull(at240.hitTest(at240.item(MainMenuItemId.SOLO)!!.rect.x + 10f, 520f))

        val at540 = MainMenuLayout.frame(540L)
        assertEquals(MainMenuItemId.SOLO, at540.hitTest(972f, 520f))
        assertNull(at540.hitTest(972f, 428f), "online remains disabled at 540ms")
        assertNull(at540.hitTest(972f, 347f), "skin remains disabled at 540ms")

        val at620 = MainMenuLayout.frame(620L)
        assertEquals(MainMenuItemId.ONLINE, at620.hitTest(972f, 428f))
        assertNull(at620.hitTest(972f, 347f), "skin remains disabled at 620ms")
        assertEquals(MainMenuItemId.SOLO, at620.hitTest(760f, 520f))

        val settled = MainMenuLayout.frame(940L)
        assertEquals(MainMenuItemId.EXIT, settled.hitTest(972f, 137f))
        assertEquals(MainMenuItemId.SETTINGS, settled.hitTest(760f, 207f))
        assertNull(settled.hitTest(700f, 137f), "outside all buttons")
    }

    @Test
    fun `reduced motion is immediate final state and fully hittable`() {
        val frame = MainMenuLayout.frame(0L, motionEnabled = false)
        assertTrue(frame.allSettled)
        frame.items.forEach { item ->
            assertEquals(item.spec.baseRect.x, item.rect.x, delta)
            assertEquals(1f, item.alpha, delta)
            assertEquals(1f, item.progress, delta)
        }
        assertEquals(MainMenuItemId.SOLO, frame.hitTest(972f, 520f))
        assertEquals(MainMenuItemId.EXIT, frame.hitTest(972f, 137f))
    }

    @Test
    fun `cover and contain preserve art aspect ratios`() {
        val cover = MainMenuAssets.coverRect(1672f, 941f, 1280f, 720f)
        assertTrue(cover.width >= 1280f - delta)
        assertTrue(cover.height >= 720f - delta)
        assertEquals(1672f / 941f, cover.width / cover.height, 0.001f)

        val ultrawide = MainMenuAssets.coverRect(1672f, 941f, 2340f, 1080f)
        assertTrue(ultrawide.width >= 2340f - delta)
        assertTrue(ultrawide.height >= 1080f - delta)
        assertEquals(1672f / 941f, ultrawide.width / ultrawide.height, 0.001f)

        val mascot = MainMenuAssets.containRect(1284f, 1225f, MainMenuLayout.mascotRect)
        assertEquals(1284f / 1225f, mascot.width / mascot.height, 0.001f)
        assertTrue(mascot.width <= MainMenuLayout.mascotRect.width + delta)
        assertTrue(mascot.height <= MainMenuLayout.mascotRect.height + delta)
        assertEquals(MainMenuLayout.mascotRect.x + (MainMenuLayout.mascotRect.width - mascot.width) / 2f, mascot.x, delta)
    }

    @Test
    fun `skin names are localized without changing the entry geometry`() {
        assertEquals("维基百科", MainMenuLayout.skinDisplayName("WIKIPEDIA"))
        assertEquals("经典牌面", MainMenuLayout.skinDisplayName("CLASSIC"))
        assertEquals("维基百科", MainMenuLayout.skinDisplayName("wikipedia"))
        assertEquals("未来皮肤", MainMenuLayout.skinDisplayName("未来皮肤"))
    }
}

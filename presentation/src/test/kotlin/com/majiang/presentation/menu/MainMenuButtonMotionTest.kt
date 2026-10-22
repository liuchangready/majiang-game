package com.majiang.presentation.menu

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 被点击菜单项“向右滑出 → 收回”时序纯模型回归。
 *
 * 锁定用户更正后的语义：位移是**被点按钮自身**的正 X 移动，
 * 时长、滑出峰值、落位与减少动画分支都被显式断言。
 */
class MainMenuButtonMotionTest {

    private val delta = 0.01f

    @Test
    fun `timing and peak offset stay inside the user requested window`() {
        assertTrue(
            MainMenuButtonMotion.OUTBOUND_MS in 140L..180L,
            "outbound must be 140..180ms, got ${MainMenuButtonMotion.OUTBOUND_MS}"
        )
        assertTrue(
            MainMenuButtonMotion.TOTAL_MS in 260L..320L,
            "total must be 260..320ms, got ${MainMenuButtonMotion.TOTAL_MS}"
        )
        assertTrue(
            MainMenuButtonMotion.MAX_OFFSET_X in 48f..64f,
            "peak offset must be 48..64 logical px, got ${MainMenuButtonMotion.MAX_OFFSET_X}"
        )
    }

    @Test
    fun `offset is zero at the start positive in flight and back to zero at the end`() {
        val motion = MainMenuButtonMotion(motionEnabled = true) { 0L }
        motion.start(MainMenuItemId.SKIN, 1_000L)
        motion.update(1_000L)

        assertEquals(0f, motion.offsetX, delta)
        assertTrue(motion.isAnimating)
        assertEquals(MainMenuItemId.SKIN, motion.activeItemId)

        motion.update(1_080L)
        val mid = motion.offsetX
        assertTrue(mid > 0f, "mid offset must be positive, got $mid")
        assertTrue(mid < MainMenuButtonMotion.MAX_OFFSET_X, "mid offset must still be in flight, got $mid")

        motion.update(1_160L)
        assertEquals(MainMenuButtonMotion.MAX_OFFSET_X, motion.offsetX, delta)

        motion.update(1_300L)
        assertEquals(0f, motion.offsetX, delta)
        assertFalse(motion.isAnimating)
        assertNull(motion.activeItemId)
    }

    @Test
    fun `motion never moves left and never exceeds the design right boundary`() {
        val motion = MainMenuButtonMotion(motionEnabled = true) { 0L }
        motion.start(MainMenuItemId.EXIT, 0L)
        var previous = 0f
        var now = 0L
        while (now <= MainMenuButtonMotion.TOTAL_MS + 50L) {
            motion.update(now)
            val offset = motion.offsetX
            assertTrue(offset >= 0f, "offset must never be negative, got $offset")
            assertTrue(offset <= MainMenuButtonMotion.MAX_OFFSET_X + delta, "offset above peak, got $offset")
            if (now <= MainMenuButtonMotion.OUTBOUND_MS) {
                assertTrue(offset >= previous - delta, "outbound must be monotonic, got $offset")
            }
            previous = offset
            now += 10L
        }
    }

    @Test
    fun `reduced motion starts already settled and never blocks the item`() {
        val motion = MainMenuButtonMotion(motionEnabled = false) { 0L }
        motion.start(MainMenuItemId.SKIN, 0L)
        motion.update(0L)

        assertFalse(motion.isAnimating)
        assertEquals(0f, motion.offsetX, delta)
        assertNull(motion.activeItemId)
    }

    @Test
    fun `pause freezes the timeline and resume continues without a jump`() {
        val motion = MainMenuButtonMotion(motionEnabled = true) { 0L }
        motion.start(MainMenuItemId.SKIN, 0L)
        motion.update(80L)
        val frozen = motion.offsetX
        assertTrue(frozen > 0f)

        motion.pause(80L)
        motion.update(5_000L)
        assertEquals(frozen, motion.offsetX, delta, "paused motion must not advance")

        motion.resume(6_000L)
        motion.update(6_000L)
        assertEquals(frozen, motion.offsetX, delta, "resume must keep the frozen progress")
        motion.update(6_080L)
        assertEquals(MainMenuButtonMotion.MAX_OFFSET_X, motion.offsetX, delta)
    }

    @Test
    fun `reset clears the offset and the target item`() {
        val motion = MainMenuButtonMotion(motionEnabled = true) { 0L }
        motion.start(MainMenuItemId.SETTINGS, 0L)
        motion.update(80L)
        assertTrue(motion.offsetX > 0f)

        motion.reset()
        assertEquals(0f, motion.offsetX, delta)
        assertFalse(motion.isAnimating)
        assertNull(motion.activeItemId)
    }

    @Test
    fun `layout moves only the clicked item and keeps the right edge inside the design width`() {
        val settled = MainMenuLayout.frame(MainMenuLayout.TOTAL_ENTRY_MS)
        val moved = MainMenuLayout.frame(
            MainMenuLayout.TOTAL_ENTRY_MS,
            motionEnabled = true,
            buttonOffset = MainMenuButtonOffset(
                itemId = MainMenuItemId.SKIN,
                offsetX = MainMenuButtonMotion.MAX_OFFSET_X,
                blocking = true
            )
        )

        val skinBefore = settled.item(MainMenuItemId.SKIN)!!
        val skinAfter = moved.item(MainMenuItemId.SKIN)!!
        assertEquals(
            skinBefore.rect.x + MainMenuButtonMotion.MAX_OFFSET_X,
            skinAfter.rect.x,
            delta,
            "the clicked item must move to positive X"
        )
        assertTrue(skinAfter.motionActive)
        assertTrue(
            skinAfter.rect.x + skinAfter.rect.width <= MainMenuLayout.DESIGN_WIDTH + delta,
            "moved item must stay inside the 1280 design boundary"
        )

        settled.items.filter { it.id != MainMenuItemId.SKIN }.forEach { before ->
            val after = moved.item(before.id)!!
            assertEquals(before.rect.x, after.rect.x, delta, "${before.id} must not move")
            assertEquals(before.rect.y, after.rect.y, delta, "${before.id} must not move")
            assertFalse(after.motionActive)
        }
    }

    @Test
    fun `moving item is not hittable so the endpoint cannot retrigger`() {
        val moving = MainMenuLayout.frame(
            MainMenuLayout.TOTAL_ENTRY_MS,
            motionEnabled = true,
            buttonOffset = MainMenuButtonOffset(
                itemId = MainMenuItemId.SKIN,
                offsetX = MainMenuButtonMotion.MAX_OFFSET_X,
                blocking = true
            )
        )
        val baseX = MainMenuLayout.spec(MainMenuItemId.SKIN).baseRect.x
        val midY = MainMenuLayout.spec(MainMenuItemId.SKIN).baseRect.y + 29f

        assertNull(moving.hitTest(baseX + 200f, midY), "the vacated start position must not hit")
        assertNull(
            moving.hitTest(baseX + MainMenuButtonMotion.MAX_OFFSET_X + 200f, midY),
            "the moved button must not accept another activation"
        )
        assertNotNull(
            MainMenuLayout.frame(MainMenuLayout.TOTAL_ENTRY_MS).hitTest(baseX + 200f, midY),
            "a settled button must be hittable again"
        )
    }

    @Test
    fun `layout clamps the offset so the item never leaves the design width`() {
        val clamped = MainMenuLayout.frame(
            MainMenuLayout.TOTAL_ENTRY_MS,
            motionEnabled = true,
            buttonOffset = MainMenuButtonOffset(
                itemId = MainMenuItemId.SKIN,
                offsetX = 9_999f,
                blocking = true
            )
        ).item(MainMenuItemId.SKIN)!!
        assertEquals(MainMenuLayout.maxButtonOffsetX(MainMenuItemId.SKIN), clamped.motionOffsetX, delta)
        assertTrue(clamped.rect.x + clamped.rect.width <= MainMenuLayout.DESIGN_WIDTH + delta)
    }
}

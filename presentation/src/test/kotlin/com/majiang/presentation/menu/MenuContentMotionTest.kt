package com.majiang.presentation.menu

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 目标内容（弹窗/子面板）轻滑入纯时序回归。
 *
 * 锁定 +220 / 280ms / easeOutCubic、减少动画直接终态，以及“非动画期间 offset 恒为 0”
 * 这一绘制与命中一致性前提。
 */
class MenuContentMotionTest {

    private val delta = 0.01f

    @Test
    fun `target content starts 220px right and lands after 280ms with easeOutCubic`() {
        val motion = MenuContentMotion(motionEnabled = true) { 1_000L }
        motion.start(1_000L)
        assertTrue(motion.isActive)
        assertEquals(MenuContentMotion.OFFSET_X, motion.offsetX, delta)
        assertEquals(0f, motion.progress, delta)

        motion.update(1_080L)
        val p = 80f / MenuContentMotion.DURATION_MS.toFloat()
        val eased = 1f - (1f - p) * (1f - p) * (1f - p)
        assertEquals(p, motion.progress, delta)
        assertEquals(MenuContentMotion.OFFSET_X * (1f - eased), motion.offsetX, delta)
        assertTrue(motion.offsetX > 0f && motion.offsetX < MenuContentMotion.OFFSET_X)

        motion.update(1_140L)
        assertTrue(motion.offsetX < 80f, "easeOutCubic must decelerate quickly")
        assertTrue(motion.isActive)

        motion.update(1_280L)
        assertFalse(motion.isActive)
        assertEquals(0f, motion.offsetX, delta)
        assertEquals(1f, motion.progress, delta)
    }

    @Test
    fun `reduced motion snaps to final immediately and never blocks input`() {
        val motion = MenuContentMotion(motionEnabled = false) { 0L }
        motion.start(0L)
        assertFalse(motion.isActive)
        assertEquals(0f, motion.offsetX, delta)
        assertEquals(1f, motion.progress, delta)

        motion.start(500L)
        assertFalse(motion.isActive)
        assertEquals(0f, motion.offsetX, delta)
    }

    @Test
    fun `snap ends an active slide without waiting for the clock`() {
        val motion = MenuContentMotion(motionEnabled = true) { 0L }
        motion.start(0L)
        motion.update(40L)
        assertTrue(motion.isActive)
        motion.snap()
        assertFalse(motion.isActive)
        assertEquals(0f, motion.offsetX, delta)
    }
}

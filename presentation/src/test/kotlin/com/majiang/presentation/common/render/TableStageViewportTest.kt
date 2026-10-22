package com.majiang.presentation.common.render

import com.badlogic.gdx.math.Vector2
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 1280×720 内容区 fit 的独立 golden 与输入/帧缓冲证据。
 *
 * 这些数值不是绘制代码常量自等：窗口尺寸来自设计 REPORT 的已知样例，
 * 输入点用正/逆变换往返，帧缓冲用独立比例计算。
 */
class TableStageViewportTest {

    @Test
    fun `16 by 9 fills the window`() {
        val v = TableStageViewport.compute(1280, 720)
        assertTrue(v.drawable)
        assertEquals(1f, v.scale, 0.0001f)
        assertEquals(0f, v.stageLeft, 0.0001f)
        assertEquals(0f, v.stageTop, 0.0001f)
        assertEquals(1280f, v.stageWidth, 0.0001f)
        assertEquals(720f, v.stageHeight, 0.0001f)
        assertEquals(0, v.framebufferStageX)
        assertEquals(0, v.framebufferStageY)
    }

    @Test
    fun `960x540 uses three quarter scale`() {
        val v = TableStageViewport.compute(960, 540)
        assertEquals(0.75f, v.scale, 0.0001f)
        assertEquals(0f, v.stageLeft, 0.0001f)
        assertEquals(0f, v.stageTop, 0.0001f)
        assertEquals(960f, v.stageWidth, 0.0001f)
        assertEquals(540f, v.stageHeight, 0.0001f)
    }

    @Test
    fun `4 by 3 letterboxes vertically only with environment`() {
        val v = TableStageViewport.compute(1024, 768)
        assertEquals(0.8f, v.scale, 0.0001f)
        assertEquals(0f, v.stageLeft, 0.0001f)
        assertEquals(96f, v.stageTop, 0.0001f)
        assertEquals(1024f, v.stageWidth, 0.0001f)
        assertEquals(576f, v.stageHeight, 0.0001f)
    }

    @Test
    fun `ultra wide pillars are environment only`() {
        val v = TableStageViewport.compute(1600, 600)
        assertEquals(600f / 720f, v.scale, 0.0001f)
        assertEquals(266.66667f, v.stageLeft, 0.01f)
        assertEquals(0f, v.stageTop, 0.0001f)
        assertEquals(1066.6667f, v.stageWidth, 0.01f)
        assertEquals(600f, v.stageHeight, 0.01f)

        val superWide = TableStageViewport.compute(2560, 1080)
        assertEquals(1.5f, superWide.scale, 0.0001f)
        assertEquals(320f, superWide.stageLeft, 0.0001f)
        assertEquals(1920f, superWide.stageWidth, 0.0001f)
        assertTrue(superWide.containsScreen(321f, 540f))
        assertFalse(superWide.containsScreen(319f, 540f))
    }

    @Test
    fun `inverse input round trips and rejects gutters`() {
        val v = TableStageViewport.compute(1600, 600)
        val local = Vector2()
        // 目标卡「国士无双」中心 (834, 249) 在 1280 设计坐标。
        val screen = v.toWindow(834f, 249f)
        assertTrue(v.toStage(screen.x, screen.y, local) != null)
        assertEquals(834f, local.x, 0.01f)
        assertEquals(249f, local.y, 0.01f)
        assertNull(v.toStage(10f, 300f, local))
        assertNull(v.toStage(800f, 615f, local))
    }

    @Test
    fun `framebuffer viewport uses ratio without multiplying input twice`() {
        val v = TableStageViewport.compute(
            windowWidth = 1280,
            windowHeight = 720,
            framebufferWidth = 1920,
            framebufferHeight = 1080
        )
        assertEquals(1.5f, v.framebufferRatio, 0.0001f)
        assertEquals(0, v.framebufferStageX)
        assertEquals(0, v.framebufferStageY)
        assertEquals(1920, v.framebufferStageWidth)
        assertEquals(1080, v.framebufferStageHeight)
        // 输入坐标仍是窗口逻辑坐标，没有乘 1.5。
        val local = v.toStage(1280f, 720f)
        assertNull(local, "右下边界属于半开区间之外")
        val inside = v.toStage(640f, 360f)
        assertEquals(640f, inside!!.x, 0.0001f)
        assertEquals(360f, inside.y, 0.0001f)
    }

    @Test
    fun `4 by 3 high density viewport uses framebuffer pixels for scissor but logical input`() {
        val v = TableStageViewport.compute(
            windowWidth = 1024,
            windowHeight = 768,
            framebufferWidth = 2048,
            framebufferHeight = 1536
        )
        assertEquals(2f, v.framebufferRatio, 0.0001f)
        assertEquals(0, v.framebufferStageX)
        assertEquals(192, v.framebufferStageY)
        assertEquals(2048, v.framebufferStageWidth)
        assertEquals(1152, v.framebufferStageHeight)
        val local = v.toStage(512f, 384f)
        assertEquals(640f, local!!.x, 0.01f)
        assertEquals(360f, local.y, 0.01f)
    }

    @Test
    fun `zero size never divides and restores to drawable`() {
        val zero = TableStageViewport.compute(0, 0)
        assertFalse(zero.drawable)
        assertNull(zero.toStage(0f, 0f))
        val restored = TableStageViewport.compute(1280, 720)
        assertTrue(restored.drawable)
    }

    @Test
    fun `safe insets fit inside the remaining window`() {
        val v = TableStageViewport.compute(
            windowWidth = 1280,
            windowHeight = 720,
            safeLeft = 48f,
            safeTop = 17f,
            safeRight = 28f,
            safeBottom = 26f
        )
        assertEquals((720f - 17f - 26f) / 720f, v.scale, 0.0001f)
        // 竖向成为限制边：内容区宽 1280×scale，横向剩余 0.4444 均分到左右。
        assertEquals(48.22223f, v.stageLeft, 0.01f)
        assertEquals(17f, v.stageTop, 0.01f)
        assertFalse(v.containsScreen(40f, 300f))
    }
}

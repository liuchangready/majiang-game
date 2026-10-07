package com.majiang.render

import com.majiang.presentation.common.render.*

import com.majiang.GdxHeadlessTestRunner
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * GhostHandTexture 程序化纹理生成与资源生命周期单元测试。
 */
class GhostHandTextureTest : GdxHeadlessTestRunner() {

    @Test
    fun `createPixmap 尺寸精确且像素具有预期半透明与柔边`() {
        val pixmap = GhostHandTexture.createPixmap()
        try {
            assertEquals(GhostHandTexture.WIDTH, pixmap.width)
            assertEquals(GhostHandTexture.HEIGHT, pixmap.height)

            // 检查中心掌部区域有绘制 (RGBA8888 格式像素不为 0)
            val centerPixel = pixmap.getPixel(40, 36)
            assertNotEquals(0, centerPixel, "手掌中心区域像素不应为全透明")

            // 提取 Alpha 通道
            val centerAlpha = (centerPixel and 0xFF) / 255f
            assertTrue(centerAlpha in 0.60f..0.85f, "手掌核心主体 Alpha 应在 ~0.75 附近，实际为 $centerAlpha")

            // 检查食指和拇指区域
            val fingerPixel = pixmap.getPixel(34, 60)
            assertNotEquals(0, fingerPixel, "食指区域像素不应为空")

            // 检查四角为全透明 (0, 0)
            assertEquals(0, pixmap.getPixel(0, 0), "边缘外侧应保持全透明")
            assertEquals(0, pixmap.getPixel(pixmap.width - 1, 0), "边缘外侧应保持全透明")
        } finally {
            pixmap.dispose()
        }
    }

    @Test
    fun `GhostHandTexture 懒加载与 dispose 安全性`() {
        // 先确保 dispose 不会抛出异常
        assertDoesNotThrow { GhostHandTexture.dispose() }

        // 获取纹理
        val tex1 = GhostHandTexture.getTexture()
        assertNotNull(tex1)

        // 缓存复用测试
        val tex2 = GhostHandTexture.getTexture()
        assertSame(tex1, tex2, "再次获取应当直接返回缓存纹理")

        // 释放
        assertDoesNotThrow { GhostHandTexture.dispose() }

        // 释放后重新获取应当生成新实例
        val tex3 = GhostHandTexture.getTexture()
        assertNotNull(tex3)
        assertDoesNotThrow { GhostHandTexture.dispose() }
    }
}

package com.majiang.render

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.Pixmap
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.utils.Disposable

/**
 * 幽灵白手套程序化纹理（零外部素材）。
 *
 * 采用 Pixmap 程序化绘制一次性生成手部纹理并缓存（懒加载 + dispose）：
 * - 掌部：圆角椭圆（约 64×80 px 区域），白色半透明 alpha ≈ 0.75
 * - 捏取双指：拇指 + 食指两条 capsule（fillCircle × N 沿路径拼合），指尖朝下捏住牌
 * - 柔边：边缘用低 alpha 逐层描边制造虚化幽灵虚影轮廓
 * - 姿态固定，动态感全部由位置/旋转/缩放/alpha 表达。
 */
object GhostHandTexture : Disposable {
    const val WIDTH = 80
    const val HEIGHT = 96

    private var cachedTexture: Texture? = null

    /**
     * 获取或懒加载生成手套纹理。
     */
    fun getTexture(): Texture {
        cachedTexture?.let { return it }
        val tex = createHandTexture()
        cachedTexture = tex
        return tex
    }

    /**
     * 程序化生成手部 Pixmap 像素数据（纯 CPU 内存计算，无头环境可安全运行）。
     */
    fun createPixmap(): Pixmap {
        val pixmap = Pixmap(WIDTH, HEIGHT, Pixmap.Format.RGBA8888)
        pixmap.blending = Pixmap.Blending.None

        // 背景全透明
        pixmap.setColor(0f, 0f, 0f, 0f)
        pixmap.fill()

        // 柔边虚化轮廓层 (alpha ~0.20)
        drawHandShapes(pixmap, glowOffset = 3, Color(1f, 1f, 1f, 0.20f))
        // 中间羽化层 (alpha ~0.45)
        drawHandShapes(pixmap, glowOffset = 1, Color(1f, 1f, 1f, 0.45f))
        // 核心半透明主体 (alpha ~0.75)
        drawHandShapes(pixmap, glowOffset = 0, Color(1f, 1f, 1f, 0.75f))

        return pixmap
    }

    private fun drawHandShapes(pixmap: Pixmap, glowOffset: Int, color: Color) {
        pixmap.setColor(color)

        // 1. 掌部：圆角椭圆 (中心约 40, 36)
        val palmCenterX = 40
        val palmCenterY = 36
        val rX = 18 + glowOffset
        val rY = 22 + glowOffset

        for (dy in -rY..rY) {
            val halfW = (rX * Math.sqrt((1.0 - (dy.toDouble() * dy) / (rY * rY)).coerceAtLeast(0.0))).toInt()
            if (halfW > 0) {
                pixmap.drawLine(palmCenterX - halfW, palmCenterY + dy, palmCenterX + halfW, palmCenterY + dy)
            }
        }

        // 2. 食指 capsule (从掌部延伸至下端指尖 34, 76)
        val fingerR = 6 + glowOffset
        drawCapsule(
            pixmap = pixmap,
            x1 = 34, y1 = 44,
            x2 = 34, y2 = 76,
            radius = fingerR
        )

        // 3. 拇指 capsule (从掌部侧面 52, 44 延伸向食指指尖靠拢 44, 74 进行捏取)
        val thumbR = 6 + glowOffset
        drawCapsule(
            pixmap = pixmap,
            x1 = 52, y1 = 44,
            x2 = 44, y2 = 74,
            radius = thumbR
        )
    }

    private fun drawCapsule(pixmap: Pixmap, x1: Int, y1: Int, x2: Int, y2: Int, radius: Int) {
        val dx = x2 - x1
        val dy = y2 - y1
        val dist = Math.hypot(dx.toDouble(), dy.toDouble()).toInt().coerceAtLeast(1)

        for (i in 0..dist) {
            val cx = x1 + (dx * i) / dist
            val cy = y1 + (dy * i) / dist
            pixmap.fillCircle(cx, cy, radius)
        }
    }

    /**
     * 创建真实纹理并释放 Pixmap。
     */
    fun createHandTexture(): Texture {
        val pixmap = createPixmap()
        val texture = Texture(pixmap)
        pixmap.dispose()
        return texture
    }

    override fun dispose() {
        cachedTexture?.dispose()
        cachedTexture = null
    }
}

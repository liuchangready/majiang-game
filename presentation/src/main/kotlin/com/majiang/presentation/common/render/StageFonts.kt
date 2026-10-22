package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.majiang.presentation.common.UiAssets

/**
 * 一个按设计字号 × stageScale × framebufferRatio 原生栅格化的牌桌字体。
 *
 * 字形纹理像素数 [pixelSize] 与设计字号 [designSize] 可能不同，因此绘制时使用
 * `designSize / pixelSize` 把它们换回 1280×720 设计单位；不得再乘一次 DPI。
 * 所有方法只在 SpriteBatch 绘制阶段调用，不改变 UiAssets 的所有权。
 */
class StageFont(
    val font: BitmapFont,
    val designSize: Int,
    val pixelSize: Int,
    val baseScale: Float = 1f
) {
    private val layout = GlyphLayout()

    /** 在绘制前按相对缩放（0.68/1.0/1.2 等）设置字体。 */
    fun applyScale(relativeScale: Float = 1f): Float {
        val scale = (baseScale * relativeScale).coerceAtLeast(0.0001f)
        font.data.setScale(scale, scale)
        return scale
    }

    fun measure(text: String, relativeScale: Float = 1f): GlyphLayout {
        applyScale(relativeScale)
        layout.setText(font, text)
        return layout
    }

    fun width(text: String, relativeScale: Float = 1f): Float = measure(text, relativeScale).width

    fun height(text: String, relativeScale: Float = 1f): Float = measure(text, relativeScale).height

    /**
     * 绘制带深色阴影的文字；[embolden] 用一次亚像素偏移获得标题的轻加粗，
     * 不把 17px 字库强行放大成标题。[outline] 是细小标签的暖白描边。
     */
    fun draw(
        batch: SpriteBatch,
        text: String,
        x: Float,
        top: Float,
        color: Color,
        relativeScale: Float = 1f,
        shadow: Boolean = true,
        embolden: Boolean = false,
        outline: Boolean = false,
        alpha: Float = 1f
    ) {
        applyScale(relativeScale)
        val a = alpha.coerceIn(0f, 1f)
        if (a <= 0.001f) return
        if (outline) {
            font.color = Color(1f, 0.95f, 0.78f, 0.28f * a)
            font.draw(batch, text, x - 0.8f, top)
            font.draw(batch, text, x + 0.8f, top)
            font.draw(batch, text, x, top + 0.8f)
            font.draw(batch, text, x, top - 0.8f)
        }
        if (shadow) {
            font.color = Color(0.012f, 0.027f, 0.020f, 0.92f * a)
            font.draw(batch, text, x + 0.9f, top - 0.9f)
        }
        font.color = Color(color.r, color.g, color.b, color.a * a)
        font.draw(batch, text, x, top)
        if (embolden) {
            font.draw(batch, text, x + 0.42f, top)
        }
    }

    fun drawCentered(
        batch: SpriteBatch,
        text: String,
        centerX: Float,
        top: Float,
        color: Color,
        relativeScale: Float = 1f,
        shadow: Boolean = true,
        embolden: Boolean = false,
        outline: Boolean = false,
        alpha: Float = 1f
    ) {
        val w = width(text, relativeScale)
        draw(batch, text, centerX - w / 2f, top, color, relativeScale, shadow, embolden, outline, alpha)
    }

    fun drawRight(
        batch: SpriteBatch,
        text: String,
        rightX: Float,
        top: Float,
        color: Color,
        relativeScale: Float = 1f,
        shadow: Boolean = true,
        outline: Boolean = false,
        alpha: Float = 1f
    ) {
        draw(batch, text, rightX - width(text, relativeScale), top, color, relativeScale, shadow, false, outline, alpha)
    }
}

/**
 * 正式牌桌的固定字号角色集合 (TableStageFonts)。
 *
 * 角色字号来自 RESPONSIVE-DESIGN.md；实际像素字号由 UiAssets 的有界 LRU 字体缓存
 * 依据当前 stage/framebuffer 生成。Screen 只借用，不持有或释放字体。
 */
class TableStageFonts private constructor(
    val title: StageFont,
    val drawer: StageFont,
    val primary: StageFont,
    val card: StageFont,
    val body: StageFont,
    val small: StageFont,
    val name: StageFont,
    val receipt: StageFont
) {
    companion object {
        fun create(assets: UiAssets, stageScale: Float, framebufferRatio: Float): TableStageFonts {
            val scale = stageScale.coerceAtLeast(0.0001f)
            val ratio = framebufferRatio.coerceAtLeast(0.0001f)
            fun role(designSize: Int, fallback: BitmapFont): StageFont =
                assets.stageFont(designSize, scale, ratio) ?: StageFont(fallback, designSize, designSize, 1f)

            return TableStageFonts(
                title = role(34, assets.effectiveTableTitleFont),
                drawer = role(28, assets.effectiveTableDrawerFont),
                primary = role(25, assets.effectiveTablePrimaryFont),
                card = role(21, assets.effectiveTableCardFont),
                body = role(19, assets.effectiveTableBodyFont),
                small = role(16, assets.effectiveTableSmallFont),
                name = role(17, assets.effectiveTableNameFont),
                receipt = role(32, assets.effectiveTableReceiptFont)
            )
        }
    }
}

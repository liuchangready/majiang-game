package com.majiang.presentation.common.render

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView

/**
 * 麻将牌皮肤枚举标识
 */
enum class SkinId {
    WIKIPEDIA,
    CLASSIC
}

/**
 * 麻将牌皮肤渲染抽象接口。
 *
 * 遵循指令生产者 (Producer) 与缓冲消费者 (Consumer) 模式：
 * 皮肤实现类只负责向 [TileDrawBuffer] 追加图元绘制指令，不直接执行任何 GL 提交或 batch.begin/end。
 */
interface TileSkin : Disposable {
    val id: SkinId
    val displayName: String

    /**
     * 向绘制缓冲追加单张牌面的绘制指令。
     *
     * @param buf 牌面绘制指令缓冲
     * @param tile 客户端牌视图模型
     * @param x 绘制区域左下角 X 坐标
     * @param y 绘制区域左下角 Y 坐标
     * @param width 目标宽度
     * @param height 目标高度
     * @param isHover 鼠标或指针是否悬停
     * @param isDrawn 是否为刚刚摸上来的牌（高亮）
     */
    fun appendTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        isHover: Boolean = false,
        isDrawn: Boolean = false,
        alpha: Float = 1f
    )

    /**
     * 向绘制缓冲追加旋转牌面的绘制指令。
     * 默认实现不进行旋转，直接委托 [appendTileDraw]。
     */
    fun appendRotatedTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        isHover: Boolean = false,
        isDrawn: Boolean = false,
        alpha: Float = 1f,
        rotationDeg: Float = 0f
    ) {
        appendTileDraw(buf, tile, x, y, width, height, isHover, isDrawn, alpha)
    }

    /**
     * 向绘制缓冲追加飞行牌的绘制指令。
     * 默认实现不进行旋转，通过缩放居中绘制；纹理皮肤可重写以实现原生旋转。
     */
    fun appendFlyingTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        scaleX: Float = 1f,
        scaleY: Float = 1f,
        rotationDeg: Float = 0f,
        alpha: Float = 1f
    ) {
        val w = width * scaleX
        val h = height * scaleY
        val drawX = x + (width - w) / 2f
        val drawY = y + (height - h) / 2f
        appendTileDraw(buf, tile, drawX, drawY, w, h, isHover = false, isDrawn = false, alpha = alpha)
    }

    /**
     * 向绘制缓冲追加牌背绘制指令。
     */
    fun appendTileBackDraw(
        buf: TileDrawBuffer,
        x: Float,
        y: Float,
        width: Float,
        height: Float
    )

    override fun dispose() {}
}

/**
 * 维基百科现代麻将插画皮肤 (WikipediaSkin)。
 *
 * 采用来自 Wikimedia Commons 的 34 张独立 PNG 牌面贴图。
 * 绘制时严格保持纵横比等比缩放居中 (letterbox)，不拉伸不变形。
 * 纹理按需懒加载并缓存，可在 Screen.dispose 中统一释放。
 */
class WikipediaSkin(
    private val textureResolver: ((TileView) -> Texture)? = null
) : TileSkin {
    override val id: SkinId = SkinId.WIKIPEDIA
    override val displayName: String = "WIKIPEDIA"

    private val textures = mutableMapOf<String, Texture>()
    private var isLoaded = false

    companion object {
        const val ASSET_BASE_PATH = "skins/wikipedia"

        fun getTileFileName(tile: TileView): String =
            if (tile.isRedFive) {
                when (tile.suit) {
                    SuitView.MAN -> "man5r.png"
                    SuitView.PIN -> "pin5r.png"
                    SuitView.SOU -> "sou5r.png"
                    else -> getTileFileName(tile.suit, tile.rank)
                }
            } else {
                getTileFileName(tile.suit, tile.rank)
            }

        fun getTileFileName(suit: SuitView, rank: Int): String = when (suit) {
            SuitView.MAN -> "man$rank.png"
            SuitView.PIN -> "pin$rank.png"
            SuitView.SOU -> "sou$rank.png"
            SuitView.WIND -> "wind$rank.png"
            SuitView.DRAGON -> "dragon$rank.png"
        }
    }

    /**
     * 确保 34 张标准牌面纹理与 3 张红五纹理全部加载就绪。
     */
    fun ensureLoaded() {
        if (isLoaded) return
        for (suit in SuitView.entries) {
            val maxRank = when (suit) {
                SuitView.MAN, SuitView.PIN, SuitView.SOU -> 9
                SuitView.WIND -> 4
                SuitView.DRAGON -> 3
            }
            for (rank in 1..maxRank) {
                val fileName = getTileFileName(suit, rank)
                val handle = Gdx.files.internal("$ASSET_BASE_PATH/$fileName")
                textures[fileName] = Texture(handle)
            }
        }
        for (fileName in listOf("man5r.png", "pin5r.png", "sou5r.png")) {
            val handle = Gdx.files.internal("$ASSET_BASE_PATH/$fileName")
            textures[fileName] = Texture(handle)
        }
        isLoaded = true
    }

    private fun getTexture(tile: TileView): Texture {
        if (textureResolver != null) {
            return textureResolver.invoke(tile)
        }
        ensureLoaded()
        val fileName = getTileFileName(tile)
        return textures[fileName]
            ?: throw IllegalStateException("Texture not loaded for tile: $tile ($fileName)")
    }

    override fun appendTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        isHover: Boolean,
        isDrawn: Boolean,
        alpha: Float
    ) {
        val texture = getTexture(tile)
        val texW = texture.width.toFloat()
        val texH = texture.height.toFloat()

        // 保持纵横比等比缩放居中 (letterbox)
        val scale = minOf(width / texW, height / texH)
        val drawW = texW * scale
        val drawH = texH * scale
        val drawX = x + (width - drawW) / 2f
        val drawY = y + (height - drawH) / 2f

        // 1. 追加牌面纹理绘制指令 (TextureOp)
        val tintColor = if (alpha >= 1f) Color.WHITE else Color(1f, 1f, 1f, alpha)
        buf.addTexture(texture, drawX, drawY, drawW, drawH, tint = tintColor)

        // 2. 有效半透明标记牌追加细边框，防止在桌布底色上边缘模糊消失
        if (alpha < 1f) {
            buf.addLineRect(drawX, drawY, drawW, drawH, Color(0.20f, 0.70f, 0.85f, 0.75f))
        }

        // 3. 若处于悬停或摸牌状态，追加高亮边框指令 (双层 LineRectOp)
        if (isHover || isDrawn) {
            val color = if (isHover) Color.YELLOW else Color(1.0f, 0.85f, 0.2f, 1f)
            buf.addLineRect(drawX, drawY, drawW, drawH, color)
            buf.addLineRect(drawX - 1f, drawY - 1f, drawW + 2f, drawH + 2f, color)
        }
    }

    override fun appendRotatedTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        isHover: Boolean,
        isDrawn: Boolean,
        alpha: Float,
        rotationDeg: Float
    ) {
        if (rotationDeg == 0f) {
            appendTileDraw(buf, tile, x, y, width, height, isHover, isDrawn, alpha)
            return
        }

        val texture = getTexture(tile)
        val texW = texture.width.toFloat()
        val texH = texture.height.toFloat()

        // 当旋转 ±90° 时，纹理的高在屏幕上呈现为宽，纹理的宽呈现为高
        val effectiveW = texH
        val effectiveH = texW

        val scale = minOf(width / effectiveW, height / effectiveH)
        val drawW = texW * scale
        val drawH = texH * scale

        val centerX = x + width / 2f
        val centerY = y + height / 2f
        val drawX = centerX - drawW / 2f
        val drawY = centerY - drawH / 2f

        val tintColor = if (alpha >= 1f) Color.WHITE else Color(1f, 1f, 1f, alpha)
        buf.addTexture(
            texture = texture,
            x = drawX,
            y = drawY,
            width = drawW,
            height = drawH,
            rotationDeg = rotationDeg,
            originX = drawW / 2f,
            originY = drawH / 2f,
            tint = tintColor
        )

        val visualW = effectiveW * scale
        val visualH = effectiveH * scale
        val boxX = centerX - visualW / 2f
        val boxY = centerY - visualH / 2f

        if (alpha < 1f) {
            buf.addLineRect(boxX, boxY, visualW, visualH, Color(0.20f, 0.70f, 0.85f, 0.75f))
        }

        if (isHover || isDrawn) {
            val color = if (isHover) Color.YELLOW else Color(1.0f, 0.85f, 0.2f, 1f)
            buf.addLineRect(boxX, boxY, visualW, visualH, color)
            buf.addLineRect(boxX - 1f, boxY - 1f, visualW + 2f, visualH + 2f, color)
        }
    }

    override fun appendFlyingTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        scaleX: Float,
        scaleY: Float,
        rotationDeg: Float,
        alpha: Float
    ) {
        val texture = getTexture(tile)
        val texW = texture.width.toFloat()
        val texH = texture.height.toFloat()

        val baseScale = minOf(width / texW, height / texH)
        val drawW = texW * baseScale
        val drawH = texH * baseScale
        val drawX = x + (width - drawW) / 2f
        val drawY = y + (height - drawH) / 2f

        val tintColor = if (alpha >= 1f) Color.WHITE else Color(1f, 1f, 1f, alpha)
        buf.addOverlayTexture(
            texture = texture,
            x = drawX,
            y = drawY,
            width = drawW,
            height = drawH,
            rotationDeg = rotationDeg,
            originX = drawW / 2f,
            originY = drawH / 2f,
            scaleX = scaleX,
            scaleY = scaleY,
            tint = tintColor
        )
    }

    override fun appendTileBackDraw(
        buf: TileDrawBuffer,
        x: Float,
        y: Float,
        width: Float,
        height: Float
    ) {
        // 翡翠深绿底色
        buf.addFilledRect(x, y, width, height, Color(0.06f, 0.35f, 0.24f, 1f))
        // 暗金装饰内边
        buf.addLineRect(x + 2f, y + 2f, width - 4f, height - 4f, Color(0.85f, 0.75f, 0.35f, 0.9f))
    }

    override fun dispose() {
        textures.values.forEach { it.dispose() }
        textures.clear()
        isLoaded = false
    }
}

/**
 * 经典色块文字皮肤 (ClassicSkin)。
 *
 * 纯几何图元与文字矢量排版：
 * 牌体背景与内阴影 (2 FilledRectOp) + 边框 (1 LineRectOp) + 牌面文字 (1 TextOp)。
 */
class ClassicSkin : TileSkin {
    override val id: SkinId = SkinId.CLASSIC
    override val displayName: String = "CLASSIC"

    companion object {
        fun getTileText(tile: TileView): String =
            if (tile.isRedFive) {
                when (tile.suit) {
                    SuitView.MAN -> "0m"
                    SuitView.PIN -> "0p"
                    SuitView.SOU -> "0s"
                    else -> "${tile.rank}?"
                }
            } else when (tile.suit) {
                SuitView.MAN -> "${tile.rank}m"
                SuitView.PIN -> "${tile.rank}p"
                SuitView.SOU -> "${tile.rank}s"
                SuitView.WIND -> when (tile.rank) {
                    1 -> "E"; 2 -> "S"; 3 -> "W"; 4 -> "N"; else -> "?"
                }
                SuitView.DRAGON -> when (tile.rank) {
                    1 -> "Wh"; 2 -> "Gr"; 3 -> "Rd"; else -> "?"
                }
            }

        fun getTileTextColor(tile: TileView): Color =
            if (tile.isRedFive) {
                Color(0.85f, 0.15f, 0.15f, 1f)
            } else when (tile.suit) {
                SuitView.MAN -> Color(0.85f, 0.15f, 0.15f, 1f)
                SuitView.PIN -> Color(0.15f, 0.45f, 0.90f, 1f)
                SuitView.SOU -> Color(0.12f, 0.65f, 0.25f, 1f)
                SuitView.WIND -> Color(0.38f, 0.22f, 0.60f, 1f)
                SuitView.DRAGON -> when (tile.rank) {
                    1 -> Color(0.55f, 0.55f, 0.60f, 1f)
                    2 -> Color(0.12f, 0.70f, 0.25f, 1f)
                    3 -> Color(0.90f, 0.18f, 0.18f, 1f)
                    else -> Color.DARK_GRAY
                }
            }
    }

    override fun appendTileDraw(
        buf: TileDrawBuffer,
        tile: TileView,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        isHover: Boolean,
        isDrawn: Boolean,
        alpha: Float
    ) {
        val isSmall = height < 60f
        val margin = if (isSmall) 2f else 3f
        val a = alpha.coerceIn(0f, 1f)

        // 1. 牌体背景与内阴影 (2 FilledRectOp)
        if (isDrawn) {
            buf.addFilledRect(x, y, width, height, Color(1.0f, 0.98f, 0.85f, a))
            buf.addFilledRect(x + margin, y + margin, width - margin * 2f, height - margin * 2f, Color(0.95f, 0.90f, 0.72f, a))
        } else if (isSmall) {
            buf.addFilledRect(x, y, width, height, Color(0.95f, 0.93f, 0.88f, a))
            buf.addFilledRect(x + margin, y + margin, width - margin * 2f, height - margin * 2f, Color(0.85f, 0.82f, 0.74f, a))
        } else {
            buf.addFilledRect(x, y, width, height, Color(0.96f, 0.94f, 0.89f, a))
            buf.addFilledRect(x + margin, y + margin, width - margin * 2f, height - margin * 2f, Color(0.88f, 0.85f, 0.78f, a))
        }

        // 2. 边框 (1 LineRectOp)
        val borderColor = when {
            isHover -> Color.YELLOW
            isDrawn -> Color(1.0f, 0.85f, 0.2f, 1f)
            alpha < 1f -> Color(0.20f, 0.70f, 0.85f, 0.8f)
            isSmall -> Color(0.4f, 0.35f, 0.25f, 0.8f)
            else -> Color(0.45f, 0.40f, 0.30f, 0.8f)
        }
        buf.addLineRect(x, y, width, height, borderColor)

        // 3. 牌面文字 (1 TextOp)
        val text = getTileText(tile)
        val baseTextColor = getTileTextColor(tile)
        val textColor = Color(baseTextColor.r, baseTextColor.g, baseTextColor.b, baseTextColor.a * a)
        if (isSmall) {
            buf.addText(text, textColor, x + 3f, y + 24f, 0.8f, 0.8f)
        } else {
            buf.addText(text, textColor, x + 8f, y + 52f, 1.4f, 1.4f)
        }
    }

    override fun appendTileBackDraw(
        buf: TileDrawBuffer,
        x: Float,
        y: Float,
        width: Float,
        height: Float
    ) {
        buf.addFilledRect(x, y, width, height, Color(0.05f, 0.38f, 0.22f, 1f))
        buf.addLineRect(x, y, width, height, Color(0.95f, 0.85f, 0.40f, 1f))
    }

    override fun dispose() {}
}

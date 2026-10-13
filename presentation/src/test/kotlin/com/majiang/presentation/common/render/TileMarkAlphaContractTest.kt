package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.Pixmap
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.SuitView
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Actual skin commands and buffer consumption; headless GL comes from the common test runner. */
class TileMarkAlphaContractTest : GdxHeadlessTestRunner() {
    private val markedAlpha = 0.55f

    @Test
    fun `classic marked face fades both body and glyph while retaining visible outline`() {
        val skin = ClassicSkin()
        for (height in listOf(44f, 80f)) for (hover in listOf(false, true)) for (drawn in listOf(false, true)) {
            val buffer = TileDrawBuffer()
            skin.appendTileDraw(buffer, TestViewBuilder.tile(0), 0f, 0f, 54f, height,
                isHover = hover, isDrawn = drawn, alpha = markedAlpha)
            assertTrue(buffer.filledRects.isNotEmpty())
            assertTrue(buffer.filledRects.all { it.color.a == markedAlpha })
            assertEquals(markedAlpha, buffer.textDraws.single().color.a)
            assertTrue(buffer.lineRects.any { it.color.a > markedAlpha })
        }
    }

    @Test
    fun `classic red fives preserve glyph and red color at marked opacity`() {
        val skin = ClassicSkin()
        for ((suit, glyph) in listOf(SuitView.MAN to "0m", SuitView.PIN to "0p", SuitView.SOU to "0s")) {
            val buffer = TileDrawBuffer()
            skin.appendTileDraw(buffer, TestViewBuilder.tile(suit, 5, 3), 0f, 0f, 54f, 80f, alpha = markedAlpha)
            val text = buffer.textDraws.single()
            assertEquals(glyph, text.text)
            assertEquals(markedAlpha, text.color.a)
            assertTrue(text.color.r > text.color.g && text.color.r > text.color.b)
        }
    }

    @Test
    fun `ordinary classic face and both skin backs stay opaque after a marked face`() {
        val skin = ClassicSkin()
        val marked = TileDrawBuffer()
        skin.appendTileDraw(marked, TestViewBuilder.tile(0), 0f, 0f, 54f, 80f, alpha = markedAlpha)
        val ordinary = TileDrawBuffer()
        skin.appendTileDraw(ordinary, TestViewBuilder.tile(4), 0f, 0f, 54f, 80f)
        assertTrue(ordinary.filledRects.all { it.color.a == 1f })
        assertEquals(1f, ordinary.textDraws.single().color.a)
        for (backSkin in listOf<TileSkin>(skin, WikipediaSkin())) {
            val backs = TileDrawBuffer()
            backSkin.appendTileBackDraw(backs, 0f, 0f, 54f, 80f)
            assertTrue(backs.filledRects.isNotEmpty())
            assertTrue(backs.filledRects.all { it.color.a == 1f }, "${backSkin.id} ordinary concealed backs must be opaque")
            assertTrue(backs.textureDraws.isEmpty())
            assertTrue(backs.textDraws.isEmpty())
        }
    }

    @Test
    fun `wikipedia resolver keeps physical red tile identity and tint is local to marked draw`() {
        val texture = Texture(2, 3, Pixmap.Format.RGBA8888)
        val resolvedIds = mutableListOf<Int>()
        val skin = WikipediaSkin { tile -> resolvedIds.add(tile.tileId); texture }
        try {
            val red = TestViewBuilder.tile(SuitView.PIN, 5, 3)
            val ordinary = TestViewBuilder.tile(SuitView.PIN, 5, 0)
            val buffer = TileDrawBuffer()
            skin.appendTileDraw(buffer, red, 0f, 0f, 54f, 80f, alpha = markedAlpha)
            skin.appendTileDraw(buffer, ordinary, 60f, 0f, 54f, 80f)
            assertEquals(listOf(red.tileId, ordinary.tileId), resolvedIds)
            assertEquals(listOf(markedAlpha, 1f), buffer.textureDraws.map { it.tint.a })
            assertTrue(buffer.textureDraws.all { it.tint.r == 1f && it.tint.g == 1f && it.tint.b == 1f })
            assertTrue(buffer.lineRects.any { it.color.a > markedAlpha })
            assertEquals(1f, Color.WHITE.a)
        } finally { texture.dispose(); skin.dispose() }
    }

    @Test
    fun `classic flying marked face forwards opacity and ordinary flying face stays opaque`() {
        val skin = ClassicSkin()
        for (alpha in listOf(markedAlpha, 1f)) {
            val buffer = TileDrawBuffer()
            skin.appendFlyingTileDraw(buffer, TestViewBuilder.tile(19), 0f, 0f, 54f, 80f,
                scaleX = 0.7f, scaleY = 0.8f, rotationDeg = 90f, alpha = alpha)
            assertTrue(buffer.filledRects.all { it.color.a == alpha })
            assertEquals(alpha, buffer.textDraws.single().color.a)
            assertEquals("0m", buffer.textDraws.single().text)
        }
    }

    @Test
    fun `wikipedia flying face preserves rotation scale and per entity opacity`() {
        val texture = Texture(2, 3, Pixmap.Format.RGBA8888)
        val skin = WikipediaSkin { texture }
        try {
            val buffer = TileDrawBuffer()
            skin.appendFlyingTileDraw(buffer, TestViewBuilder.tile(19), 4f, 5f, 54f, 80f,
                scaleX = 0.7f, scaleY = 0.8f, rotationDeg = 90f, alpha = markedAlpha)
            skin.appendFlyingTileDraw(buffer, TestViewBuilder.tile(16), 60f, 5f, 54f, 80f)
            val marked = buffer.overlayTextures.first()
            assertEquals(markedAlpha, marked.tint.a)
            assertEquals(90f, marked.rotationDeg)
            assertEquals(0.7f, marked.scaleX)
            assertEquals(0.8f, marked.scaleY)
            assertEquals(1f, buffer.overlayTextures.last().tint.a)
            assertEquals(1f, Color.WHITE.a)
        } finally { texture.dispose(); skin.dispose() }
    }

    @Test
    fun `side seat marked faces rotate within their horizontal slot without narrowing or losing red identity`() {
        val texture = Texture(2, 3, Pixmap.Format.RGBA8888)
        val resolvedIds = mutableListOf<Int>()
        val skin = WikipediaSkin { tile -> resolvedIds.add(tile.tileId); texture }
        try {
            for (angle in listOf(-90f, 90f)) {
                val buffer = TileDrawBuffer()
                skin.appendRotatedTileDraw(buffer, TestViewBuilder.tile(19), 10f, 20f, 36f, 24f,
                    alpha = markedAlpha, rotationDeg = angle)
                val op = buffer.textureDraws.single()
                assertEquals(angle, op.rotationDeg)
                assertEquals(markedAlpha, op.tint.a)
                val radians = Math.toRadians(angle.toDouble())
                val points = listOf(0f to 0f, op.width to 0f, op.width to op.height, 0f to op.height).map { (x, y) ->
                    val dx = x - op.originX
                    val dy = y - op.originY
                    (op.x + op.originX + dx * kotlin.math.cos(radians) - dy * kotlin.math.sin(radians)) to
                        (op.y + op.originY + dx * kotlin.math.sin(radians) + dy * kotlin.math.cos(radians))
                }
                assertEquals(10.0, points.minOf { it.first }, 0.001)
                assertEquals(46.0, points.maxOf { it.first }, 0.001)
                assertEquals(20.0, points.minOf { it.second }, 0.001)
                assertEquals(44.0, points.maxOf { it.second }, 0.001)
            }
            assertEquals(listOf(19, 19), resolvedIds)
        } finally { texture.dispose(); skin.dispose() }
    }

    @Test
    fun `flushing transparent regular and overlay textures restores callers batch color`() {
        val texture = Texture(2, 3, Pixmap.Format.RGBA8888)
        val batch = SpriteBatch()
        val shape = ShapeRenderer()
        val font = BitmapFont()
        val initialColor = Color(0.2f, 0.4f, 0.6f, 0.9f)
        try {
            for (overlay in listOf(false, true)) {
                batch.color = initialColor
                val buffer = TileDrawBuffer()
                if (overlay) buffer.addOverlayTexture(texture, 0f, 0f, 20f, 30f, tint = Color(1f, 1f, 1f, markedAlpha))
                else buffer.addTexture(texture, 0f, 0f, 20f, 30f, tint = Color(1f, 1f, 1f, markedAlpha))
                buffer.flush(batch, shape, font)
                assertEquals(initialColor, batch.color, "Opacity must not leak to the next UI draw (overlay=$overlay)")
                assertFalse(batch.isDrawing)
                assertTrue(buffer.isEmpty())
            }
        } finally { font.dispose(); shape.dispose(); batch.dispose(); texture.dispose() }
    }

    @Test
    fun `flushing marked glyph restores shared font color and scale for subsequent labels`() {
        val batch = SpriteBatch()
        val shape = ShapeRenderer()
        val font = BitmapFont()
        val initialColor = Color(0.3f, 0.6f, 0.9f, 0.8f)
        try {
            font.color = initialColor
            font.data.setScale(0.9f, 1.1f)
            val buffer = TileDrawBuffer()
            ClassicSkin().appendTileDraw(buffer, TestViewBuilder.tile(19), 0f, 0f, 54f, 80f, alpha = markedAlpha)
            buffer.flush(batch, shape, font)
            assertEquals(initialColor, font.color, "Marked tile must not fade or recolor later labels")
            assertEquals(0.9f, font.data.scaleX)
            assertEquals(1.1f, font.data.scaleY)
            assertFalse(batch.isDrawing)
            assertTrue(buffer.isEmpty())
        } finally { font.dispose(); shape.dispose(); batch.dispose() }
    }
}

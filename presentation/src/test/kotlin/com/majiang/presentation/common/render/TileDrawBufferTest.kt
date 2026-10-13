package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * TileDrawBuffer 纯逻辑单元测试（Headless 运行）。
 */
class TileDrawBufferTest : GdxHeadlessTestRunner() {

    private fun tile(suit: SuitView, rank: Int, copyIndex: Int = 0): TileView {
        val base = when (suit) {
            SuitView.MAN -> (rank - 1) * 4
            SuitView.SOU -> 36 + (rank - 1) * 4
            SuitView.PIN -> 72 + (rank - 1) * 4
            SuitView.WIND -> 108 + (rank - 1) * 4
            SuitView.DRAGON -> 124 + (rank - 1) * 4
        }
        return TileView(base + copyIndex, suit, rank)
    }

    @Test
    fun `TileDrawBuffer 基础指令收集与 clear 幂等性`() {
        val buffer = TileDrawBuffer()
        assertTrue(buffer.isEmpty())
        assertEquals(0, buffer.filledRects.size)
        assertEquals(0, buffer.lineRects.size)
        assertEquals(0, buffer.textureDraws.size)
        assertEquals(0, buffer.textDraws.size)

        buffer.addFilledRect(10f, 20f, 30f, 40f, Color.RED)
        buffer.addLineRect(15f, 25f, 35f, 45f, Color.BLUE)
        buffer.addText("1m", Color.GREEN, 5f, 5f, 1.0f, 1.0f)

        assertFalse(buffer.isEmpty())
        assertEquals(1, buffer.filledRects.size)
        assertEquals(1, buffer.lineRects.size)
        assertEquals(0, buffer.textureDraws.size)
        assertEquals(1, buffer.textDraws.size)

        // 验证指令数据
        val filled = buffer.filledRects[0]
        assertEquals(10f, filled.x)
        assertEquals(20f, filled.y)
        assertEquals(30f, filled.width)
        assertEquals(40f, filled.height)
        assertEquals(Color.RED, filled.color)

        // clear 一次
        buffer.clear()
        assertTrue(buffer.isEmpty())
        assertEquals(0, buffer.filledRects.size)
        assertEquals(0, buffer.lineRects.size)
        assertEquals(0, buffer.textureDraws.size)
        assertEquals(0, buffer.textDraws.size)

        // clear 幂等性
        buffer.clear()
        assertTrue(buffer.isEmpty())
    }

    @Test
    fun `ClassicSkin 追加单张牌图元指令分桶断言`() {
        val buffer = TileDrawBuffer()
        val skin = ClassicSkin()

        // 1. 常规手牌（无悬停，无摸牌高亮）
        skin.appendTileDraw(
            buf = buffer,
            tile = tile(SuitView.MAN, 1),
            x = 100f,
            y = 50f,
            width = 54f,
            height = 80f,
            isHover = false,
            isDrawn = false
        )

        // 一张常规牌 = 2 Filled (底色+内阴影) + 1 Line (边框) + 1 Text (文字)
        assertEquals(2, buffer.filledRects.size, "ClassicSkin 一张牌应有 2 个 Filled 矩形")
        assertEquals(1, buffer.lineRects.size, "ClassicSkin 一张牌应有 1 个 Line 矩形")
        assertEquals(0, buffer.textureDraws.size, "ClassicSkin 不生成纹理指令")
        assertEquals(1, buffer.textDraws.size, "ClassicSkin 一张牌应有 1 个 Text 指令")

        val textOp = buffer.textDraws[0]
        assertEquals("1m", textOp.text)
        assertEquals(1.4f, textOp.scaleX)
        assertEquals(1.4f, textOp.scaleY)
    }

    @Test
    fun `ClassicSkin 悬停与摸牌高亮状态测试`() {
        val skin = ClassicSkin()

        // 悬停牌
        val hoverBuffer = TileDrawBuffer()
        skin.appendTileDraw(
            buf = hoverBuffer,
            tile = tile(SuitView.PIN, 5),
            x = 0f,
            y = 0f,
            width = 54f,
            height = 80f,
            isHover = true,
            isDrawn = false
        )
        assertEquals(Color.YELLOW, hoverBuffer.lineRects[0].color)

        // 摸牌高亮
        val drawnBuffer = TileDrawBuffer()
        skin.appendTileDraw(
            buf = drawnBuffer,
            tile = tile(SuitView.SOU, 9),
            x = 0f,
            y = 0f,
            width = 54f,
            height = 80f,
            isHover = false,
            isDrawn = true
        )
        assertEquals(Color(1.0f, 0.85f, 0.2f, 1f), drawnBuffer.lineRects[0].color)
    }

    @Test
    fun `ClassicSkin 小尺寸牌（牌河尺寸）文字缩放测试`() {
        val buffer = TileDrawBuffer()
        val skin = ClassicSkin()

        skin.appendTileDraw(
            buf = buffer,
            tile = tile(SuitView.MAN, 2),
            x = 460f,
            y = 280f,
            width = 28f,
            height = 36f,
            isHover = false,
            isDrawn = false
        )

        assertEquals(1, buffer.textDraws.size)
        val textOp = buffer.textDraws[0]
        assertEquals("2m", textOp.text)
        assertEquals(0.8f, textOp.scaleX, "小尺寸牌 scaleX 应为 0.8f")
        assertEquals(0.8f, textOp.scaleY, "小尺寸牌 scaleY 应为 0.8f")
    }

    @Test
    fun `ClassicSkin 追加14张手牌指令批次桶总量测试`() {
        val buffer = TileDrawBuffer()
        val skin = ClassicSkin()

        val hand = (1..9).map { tile(SuitView.MAN, it) } +
            (1..4).map { tile(SuitView.WIND, it) } +
            tile(SuitView.DRAGON, 1)

        assertEquals(14, hand.size)

        for (t in hand) {
            skin.appendTileDraw(
                buf = buffer,
                tile = t,
                x = 0f,
                y = 0f,
                width = 54f,
                height = 80f,
                isHover = false,
                isDrawn = false
            )
        }

        // 14 张牌预期指令：14 * 2 = 28 Filled, 14 * 1 = 14 Line, 0 Texture, 14 * 1 = 14 Text
        assertEquals(28, buffer.filledRects.size)
        assertEquals(14, buffer.lineRects.size)
        assertEquals(0, buffer.textureDraws.size)
        assertEquals(14, buffer.textDraws.size)
    }

    @Test
    fun `WikipediaSkin 追加单张牌与高亮指令分桶断言`() {
        val buffer = TileDrawBuffer()
        val skin = WikipediaSkin()

        // 常规牌：一张 = 1 Texture, 0 Filled, 0 Line, 0 Text
        skin.appendTileDraw(
            buf = buffer,
            tile = tile(SuitView.MAN, 1),
            x = 100f,
            y = 50f,
            width = 54f,
            height = 80f,
            isHover = false,
            isDrawn = false
        )

        assertEquals(0, buffer.filledRects.size)
        assertEquals(0, buffer.lineRects.size)
        assertEquals(1, buffer.textureDraws.size)
        assertEquals(0, buffer.textDraws.size)

        // 悬停牌：一张 = 1 Texture, 2 Line (双层高亮边框)
        val hoverBuffer = TileDrawBuffer()
        skin.appendTileDraw(
            buf = hoverBuffer,
            tile = tile(SuitView.PIN, 1),
            x = 100f,
            y = 50f,
            width = 54f,
            height = 80f,
            isHover = true,
            isDrawn = false
        )

        assertEquals(0, hoverBuffer.filledRects.size)
        assertEquals(2, hoverBuffer.lineRects.size, "WikipediaSkin 悬停状态应追加 2 个 Line 描边矩形")
        assertEquals(1, hoverBuffer.textureDraws.size)
        assertEquals(0, hoverBuffer.textDraws.size)
        assertEquals(Color.YELLOW, hoverBuffer.lineRects[0].color)

        // 摸牌高亮：一张 = 1 Texture, 2 Line (双层金色边框)
        val drawnBuffer = TileDrawBuffer()
        skin.appendTileDraw(
            buf = drawnBuffer,
            tile = tile(SuitView.SOU, 1),
            x = 100f,
            y = 50f,
            width = 54f,
            height = 80f,
            isHover = false,
            isDrawn = true
        )

        assertEquals(2, drawnBuffer.lineRects.size)
        assertEquals(Color(1.0f, 0.85f, 0.2f, 1f), drawnBuffer.lineRects[0].color)
    }

    @Test
    fun `TileSkin 牌背绘制指令分桶断言`() {
        val classicSkin = ClassicSkin()
        val wikiSkin = WikipediaSkin()

        val buf1 = TileDrawBuffer()
        classicSkin.appendTileBackDraw(buf1, 10f, 20f, 54f, 80f)
        assertEquals(1, buf1.filledRects.size)
        assertEquals(1, buf1.lineRects.size)
        assertEquals(0, buf1.textureDraws.size)
        assertEquals(0, buf1.textDraws.size)

        val buf2 = TileDrawBuffer()
        wikiSkin.appendTileBackDraw(buf2, 10f, 20f, 54f, 80f)
        assertEquals(1, buf2.filledRects.size)
        assertEquals(1, buf2.lineRects.size)
        assertEquals(0, buf2.textureDraws.size)
        assertEquals(0, buf2.textDraws.size)
    }

    @Test
    fun `34种牌型全量追加分桶测试`() {
        val all34 = mutableListOf<TileView>()
        for (suit in listOf(SuitView.MAN, SuitView.PIN, SuitView.SOU)) {
            for (rank in 1..9) all34.add(tile(suit, rank))
        }
        for (rank in 1..4) all34.add(tile(SuitView.WIND, rank))
        for (rank in 1..3) all34.add(tile(SuitView.DRAGON, rank))

        assertEquals(34, all34.size)

        // 1. ClassicSkin 34 张牌
        val classicBuffer = TileDrawBuffer()
        val classicSkin = ClassicSkin()
        for (t in all34) {
            classicSkin.appendTileDraw(classicBuffer, t, 0f, 0f, 54f, 80f)
        }
        assertEquals(68, classicBuffer.filledRects.size, "34 张牌应有 68 个 Filled 矩形")
        assertEquals(34, classicBuffer.lineRects.size, "34 张牌应有 34 个 Line 矩形")
        assertEquals(0, classicBuffer.textureDraws.size)
        assertEquals(34, classicBuffer.textDraws.size, "34 张牌应有 34 个 Text")

        // 2. WikipediaSkin 34 张牌
        val wikiBuffer = TileDrawBuffer()
        val wikiSkin = WikipediaSkin()
        for (t in all34) {
            wikiSkin.appendTileDraw(wikiBuffer, t, 0f, 0f, 54f, 80f)
        }
        assertEquals(0, wikiBuffer.filledRects.size)
        assertEquals(0, wikiBuffer.lineRects.size)
        assertEquals(34, wikiBuffer.textureDraws.size, "34 张牌应有 34 个 Texture 指令")
        assertEquals(0, wikiBuffer.textDraws.size)

        // 3. WikipediaSkin 34 张全部处于悬停状态
        val wikiHoverBuffer = TileDrawBuffer()
        for (t in all34) {
            wikiSkin.appendTileDraw(wikiHoverBuffer, t, 0f, 0f, 54f, 80f, isHover = true)
        }
        assertEquals(68, wikiHoverBuffer.lineRects.size, "34 张牌悬停时应有 34*2=68 个 Line 矩形")
    }

    @Test
    fun `ClassicSkin 赤宝牌渲染为红色 0m 0p 0s 断言`() {
        val buffer = TileDrawBuffer()
        val skin = ClassicSkin()

        val redMan = tile(SuitView.MAN, 5, 3)
        val redPin = tile(SuitView.PIN, 5, 3)
        val redSou = tile(SuitView.SOU, 5, 3)

        assertEquals("0m", ClassicSkin.getTileText(redMan))
        assertEquals("0p", ClassicSkin.getTileText(redPin))
        assertEquals("0s", ClassicSkin.getTileText(redSou))

        val redColor = Color(0.85f, 0.15f, 0.15f, 1f)
        assertEquals(redColor, ClassicSkin.getTileTextColor(redMan))
        assertEquals(redColor, ClassicSkin.getTileTextColor(redPin))
        assertEquals(redColor, ClassicSkin.getTileTextColor(redSou))

        // 普通 5 号牌为非赤宝牌文字与颜色
        assertEquals("5m", ClassicSkin.getTileText(tile(SuitView.MAN, 5, 0)))
        assertEquals("5p", ClassicSkin.getTileText(tile(SuitView.PIN, 5, 0)))
        assertEquals("5s", ClassicSkin.getTileText(tile(SuitView.SOU, 5, 0)))
        assertEquals(Color(0.15f, 0.45f, 0.90f, 1f), ClassicSkin.getTileTextColor(tile(SuitView.PIN, 5, 0)))
        assertEquals(Color(0.12f, 0.65f, 0.25f, 1f), ClassicSkin.getTileTextColor(tile(SuitView.SOU, 5, 0)))

        // 追加到 buffer
        skin.appendTileDraw(buffer, redPin, 0f, 0f, 54f, 80f)
        val textOp = buffer.textDraws.single()
        assertEquals("0p", textOp.text)
        assertEquals(redColor, textOp.color)
    }

    @Test
    fun `TileDrawBuffer overlayTextures 指令收集与 clear 幂等性断言`() {
        val buffer = TileDrawBuffer()
        val mockTexture = GhostHandTexture.getTexture()

        assertTrue(buffer.isEmpty())
        assertEquals(0, buffer.overlayTextures.size)

        buffer.addOverlayTexture(
            texture = mockTexture,
            x = 100f,
            y = 200f,
            width = 56f,
            height = 68f,
            rotationDeg = 8f,
            originX = 28f,
            originY = 34f,
            tint = Color(1f, 1f, 1f, 0.75f),
            scaleX = 1.1f,
            scaleY = 0.95f
        )

        assertFalse(buffer.isEmpty())
        assertEquals(1, buffer.overlayTextures.size)

        val op = buffer.overlayTextures[0]
        assertEquals(mockTexture, op.texture)
        assertEquals(100f, op.x)
        assertEquals(200f, op.y)
        assertEquals(56f, op.width)
        assertEquals(68f, op.height)
        assertEquals(8f, op.rotationDeg)
        assertEquals(28f, op.originX)
        assertEquals(34f, op.originY)
        assertEquals(Color(1f, 1f, 1f, 0.75f), op.tint)
        assertEquals(1.1f, op.scaleX)
        assertEquals(0.95f, op.scaleY)

        buffer.clear()
        assertTrue(buffer.isEmpty())
        assertEquals(0, buffer.overlayTextures.size)
    }

    @Test
    fun `ClassicSkin 与 WikipediaSkin appendFlyingTileDraw 指令分桶断言`() {
        val classicSkin = ClassicSkin()
        val wikiSkin = WikipediaSkin()
        val t = tile(SuitView.MAN, 1, 0)

        // 1. ClassicSkin appendFlyingTileDraw: 不产生纹理指令，按 scale 调整尺寸后生成 2 Filled + 1 Line + 1 Text
        val classicBuf = TileDrawBuffer()
        classicSkin.appendFlyingTileDraw(
            buf = classicBuf,
            tile = t,
            x = 100f,
            y = 100f,
            width = 28f,
            height = 36f,
            scaleX = 1.1f,
            scaleY = 0.92f,
            rotationDeg = 8f
        )
        assertEquals(2, classicBuf.filledRects.size)
        assertEquals(1, classicBuf.lineRects.size)
        assertEquals(1, classicBuf.textDraws.size)
        assertEquals(0, classicBuf.textureDraws.size)
        assertEquals(0, classicBuf.overlayTextures.size)

        // 2. WikipediaSkin appendFlyingTileDraw: 产生 1 个 overlayTextures 指令，携带旋转与缩放
        val wikiBuf = TileDrawBuffer()
        wikiSkin.appendFlyingTileDraw(
            buf = wikiBuf,
            tile = t,
            x = 100f,
            y = 100f,
            width = 28f,
            height = 36f,
            scaleX = 1.1f,
            scaleY = 0.92f,
            rotationDeg = 8f
        )
        assertEquals(1, wikiBuf.overlayTextures.size)
        val wikiOp = wikiBuf.overlayTextures[0]
        assertEquals(8f, wikiOp.rotationDeg)
        assertEquals(1.1f, wikiOp.scaleX)
        assertEquals(0.92f, wikiOp.scaleY)
        assertEquals(0, wikiBuf.textureDraws.size)
    }
}

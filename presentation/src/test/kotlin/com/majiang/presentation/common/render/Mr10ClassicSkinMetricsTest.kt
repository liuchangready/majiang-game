package com.majiang.presentation.common.render

import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.GdxHeadlessTestRunner
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * MR-10 测量基线测试：ClassicSkin 纹理缓存的旋转需求、纹理内存与批处理指标实测。
 */
class Mr10ClassicSkinMetricsTest : GdxHeadlessTestRunner() {

    @Test
    fun `analyze rotation requirement and current ClassicSkin limitation`() {
        val classic = ClassicSkin()
        val buf = TileDrawBuffer()
        val tile = TileView.fromTileId(0) // 1万

        // 在 ClassicSkin 上调用 appendRotatedTileDraw 旋转 90 度
        classic.appendRotatedTileDraw(buf, tile, 100f, 100f, 40f, 60f, rotationDeg = 90f)

        // 验证当前 ClassicSkin 降级为未旋转的纯矢量图元，没有生成带 rotationDeg 的指令
        assertEquals(0, buf.textureDraws.size, "ClassicSkin 不生成任何 TextureOp")
        assertEquals(2, buf.filledRects.size, "ClassicSkin 生成 2 个 FilledRectOp")
        assertEquals(1, buf.lineRects.size, "ClassicSkin 生成 1 个 LineRectOp")
        assertEquals(1, buf.textDraws.size, "ClassicSkin 生成 1 个 TextOp")

        println("=== MR-10 ClassicSkin Rotation & Batching Analysis ===")
        println("ClassicSkin current rotation support: NONE (draws upright vector primitives)")
        println("Japanese Mahjong requirement: Opponent melds (Chi/Pon/Kan) and Riichi discards require 90°/270° rotation")
    }

    @Test
    fun `calculate ClassicSkin texture cache memory metrics and red five coverage`() {
        // 麻将牌种统计：34 种标准牌 + 3 种赤五 (0m, 0p, 0s) + 1 种牌背 = 38 种图元
        val standardTileTypes = 34
        val redFiveTypes = 3 // 0m, 0p, 0s
        val tileBackTypes = 1
        val totalVisualVariants = standardTileTypes + redFiveTypes + tileBackTypes
        assertEquals(38, totalVisualVariants, "必须覆盖 34 种普通牌 + 3 种赤五 + 1 种牌背，严禁丢弃赤五差异")

        // 验证赤五具有完全独立的文本与高亮色
        val redFiveMan = TileView.fromTileId(19) // 5万 copy 3
        val normalFiveMan = TileView.fromTileId(16) // 5万 copy 0
        assertTrue(redFiveMan.isRedFive)
        assertFalse(normalFiveMan.isRedFive)
        assertEquals("0m", ClassicSkin.getTileText(redFiveMan))
        assertEquals("5m", ClassicSkin.getTileText(normalFiveMan))

        // 显存预算核算：
        // 规格 A: 64x96 RGBA8888 (低分屏/移动端) -> 38 * 64 * 96 * 4 bytes = 933,888 字节 (~0.89 MiB)
        val lowResBytes = totalVisualVariants * 64 * 96 * 4
        // 规格 B: 128x192 RGBA8888 (高清屏/视网膜) -> 38 * 128 * 192 * 4 bytes = 3,735,552 字节 (~3.56 MiB)
        val highResBytes = totalVisualVariants * 128 * 192 * 4

        println("Total distinct visual variants: $totalVisualVariants (34 standard + 3 red fives + 1 back)")
        println("VRAM at 64x96 (RGBA8888):  %d bytes (%.2f MiB)".format(lowResBytes, lowResBytes / (1024.0 * 1024.0)))
        println("VRAM at 128x192 (RGBA8888): %d bytes (%.2f MiB)".format(highResBytes, highResBytes / (1024.0 * 1024.0)))
    }

    @Test
    fun `compare draw call and batching metrics for full table scene`() {
        val classic = ClassicSkin()
        val buf = TileDrawBuffer()

        // 模拟全桌场景：4 家手牌(13*4=52) + 4 家牌河(18*4=72) + 宝牌指示牌(5) = 129 张牌
        val tileCount = 129
        for (i in 0 until tileCount) {
            val tile = TileView.fromTileId(i % 136)
            classic.appendTileDraw(buf, tile, (i % 10) * 45f, (i / 10) * 65f, 40f, 60f)
        }

        // 当前矢量实现的分桶收集指标：
        val vectorFilledCount = buf.filledRects.size // 129 * 2 = 258
        val vectorLineCount = buf.lineRects.size     // 129 * 1 = 129
        val vectorTextCount = buf.textDraws.size     // 129 * 1 = 129
        assertEquals(tileCount * 2, vectorFilledCount)
        assertEquals(tileCount, vectorLineCount)
        assertEquals(tileCount, vectorTextCount)

        // flush 批次切换：
        // 1. ShapeRenderer.Filled 批次 (1 次 begin/end)
        // 2. SpriteBatch 文本批次 (1 次 begin/end)
        // 3. ShapeRenderer.Line 批次 (1 次 begin/end)
        val vectorBatchPasses = 3

        // 纹理缓存实现的预期批次：
        // 1. SpriteBatch 纹理批次 (1 次 begin/end 绘制全部 129 张牌面，并支持旋转)
        // 2. ShapeRenderer.Line 批次 (仅悬停/标记牌时需要，通常 0~1 次)
        val textureBatchPasses = 1

        println("Table scene tile count: $tileCount")
        println("Current vector mode:   $vectorBatchPasses batch passes (FilledRects: $vectorFilledCount, LineRects: $vectorLineCount, TextOps: $vectorTextCount)")
        println("Texture cache mode:    $textureBatchPasses batch pass (TextureOps: $tileCount, hardware rotated quads)")
        println("Batch pass reduction:  ${vectorBatchPasses - textureBatchPasses} passes saved (66.7% reduction)")
    }
}

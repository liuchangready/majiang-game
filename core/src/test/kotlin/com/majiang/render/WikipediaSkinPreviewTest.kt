package com.majiang.render

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.Pixmap
import com.badlogic.gdx.graphics.PixmapIO
import com.majiang.GdxHeadlessTestRunner
import com.majiang.model.Suit
import com.majiang.model.Tile
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 维基百科皮肤验收工具与健全性测试。
 *
 * 1. 纯 CPU (Pixmap) 加载 34 张 PNG 贴图；
 * 2. 断言 34 张贴图均存在、合法且宽高均 > 100px；
 * 3. 拼接生成 9列 × 4行 的预览联系表，等比缩放并留白；
 * 4. 输出到 build/skin_preview.png 供人工/自动化视觉验收。
 */
class WikipediaSkinPreviewTest : GdxHeadlessTestRunner() {

    @Test
    fun `生成维基皮肤联系表并验证34张牌资源健全性`() {
        val grid = listOf(
            (1..9).map { Tile(Suit.MAN, it) },
            (1..9).map { Tile(Suit.PIN, it) },
            (1..9).map { Tile(Suit.SOU, it) },
            (1..4).map { Tile(Suit.WIND, it) } + (1..3).map { Tile(Suit.DRAGON, it) },
            listOf(Tile(Suit.MAN, 5, 3), Tile(Suit.PIN, 5, 3), Tile(Suit.SOU, 5, 3))
        )

        val cols = 9
        val rows = 5
        val cellW = 120
        val cellH = 160
        val padding = 12

        val sheetW = cols * cellW + (cols + 1) * padding
        val sheetH = rows * cellH + (rows + 1) * padding

        val sheet = Pixmap(sheetW, sheetH, Pixmap.Format.RGBA8888)
        sheet.setFilter(Pixmap.Filter.BiLinear)

        // 牌桌深墨绿底色，衬托象牙白麻将牌
        sheet.setColor(Color(0.08f, 0.20f, 0.13f, 1f))
        sheet.fill()

        var loadedCount = 0

        for (r in 0 until rows) {
            val rowTiles = grid[r]
            for (c in rowTiles.indices) {
                val tile = rowTiles[c]
                val fileName = WikipediaSkin.getTileFileName(tile)
                val fileHandle = Gdx.files.internal("${WikipediaSkin.ASSET_BASE_PATH}/$fileName")

                // 断言文件存在
                assertTrue(fileHandle.exists(), "皮肤贴图文件不存在: ${fileHandle.path()}")

                val srcPixmap = Pixmap(fileHandle)
                assertNotNull(srcPixmap, "无法以 Pixmap 加载贴图: $fileName")

                // 断言原始宽高 > 100px
                assertTrue(srcPixmap.width > 100, "$fileName 原始宽度应 > 100px, 实际: ${srcPixmap.width}")
                assertTrue(srcPixmap.height > 100, "$fileName 原始高度应 > 100px, 实际: ${srcPixmap.height}")

                // 计算单元格位置与等比缩放
                val cellX = padding + c * (cellW + padding)
                val cellY = padding + r * (cellH + padding)

                val scale = minOf(cellW.toFloat() / srcPixmap.width, cellH.toFloat() / srcPixmap.height)
                val drawW = (srcPixmap.width * scale).toInt()
                val drawH = (srcPixmap.height * scale).toInt()
                val dstX = cellX + (cellW - drawW) / 2
                val dstY = cellY + (cellH - drawH) / 2

                sheet.drawPixmap(srcPixmap, 0, 0, srcPixmap.width, srcPixmap.height, dstX, dstY, drawW, drawH)
                srcPixmap.dispose()
                loadedCount++
            }
        }

        assertTrue(loadedCount == 37, "应成功加载全部 34 张麻将单牌 + 3 张赤宝牌，实际加载: $loadedCount")

        val outputFile = Gdx.files.local("build/skin_preview.png")
        outputFile.file().parentFile?.mkdirs()
        PixmapIO.writePNG(outputFile, sheet)

        // 同步输出到项目根目录 build/skin_preview.png
        val rootBuildDir = java.io.File("../build")
        if (rootBuildDir.exists() || rootBuildDir.mkdirs()) {
            val rootFile = java.io.File(rootBuildDir, "skin_preview.png")
            PixmapIO.writePNG(Gdx.files.absolute(rootFile.canonicalPath), sheet)
        }

        sheet.dispose()

        assertTrue(outputFile.exists(), "build/skin_preview.png 应成功生成")
        assertTrue(outputFile.length() > 0, "build/skin_preview.png 文件大小应大于 0")
        val rootOutputFile = java.io.File("../build/skin_preview.png")
        assertTrue(rootOutputFile.exists() && rootOutputFile.length() > 0, "根目录 build/skin_preview.png 应存在")
    }
}

package com.majiang.presentation.common.render

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.Pixmap
import com.badlogic.gdx.graphics.PixmapIO
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 维基百科皮肤验收工具与健全性测试。
 */
class WikipediaSkinPreviewTest : GdxHeadlessTestRunner() {

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
    fun `生成维基皮肤联系表并验证34张牌资源健全性`() {
        val grid = listOf(
            (1..9).map { tile(SuitView.MAN, it) },
            (1..9).map { tile(SuitView.PIN, it) },
            (1..9).map { tile(SuitView.SOU, it) },
            (1..4).map { tile(SuitView.WIND, it) } + (1..3).map { tile(SuitView.DRAGON, it) },
            listOf(tile(SuitView.MAN, 5, 3), tile(SuitView.PIN, 5, 3), tile(SuitView.SOU, 5, 3))
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

        sheet.setColor(Color(0.08f, 0.20f, 0.13f, 1f))
        sheet.fill()

        var loadedCount = 0

        for (r in 0 until rows) {
            val rowTiles = grid[r]
            for (c in rowTiles.indices) {
                val t = rowTiles[c]
                val fileName = WikipediaSkin.getTileFileName(t)
                val fileHandle = Gdx.files.internal("${WikipediaSkin.ASSET_BASE_PATH}/$fileName")

                assertTrue(fileHandle.exists(), "皮肤贴图文件不存在: ${fileHandle.path()}")

                val srcPixmap = Pixmap(fileHandle)
                assertNotNull(srcPixmap, "无法以 Pixmap 加载贴图: $fileName")

                assertTrue(srcPixmap.width > 100, "$fileName 原始宽度应 > 100px, 实际: ${srcPixmap.width}")
                assertTrue(srcPixmap.height > 100, "$fileName 原始高度应 > 100px, 实际: ${srcPixmap.height}")

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

        val rootBuildDir = java.io.File("../build")
        if (rootBuildDir.exists() || rootBuildDir.mkdirs()) {
            val rootFile = java.io.File(rootBuildDir, "skin_preview.png")
            PixmapIO.writePNG(Gdx.files.absolute(rootFile.canonicalPath), sheet)
        }

        sheet.dispose()

        assertTrue(outputFile.exists(), "build/skin_preview.png 应成功生成")
        assertTrue(outputFile.length() > 0, "build/skin_preview.png 文件大小应大于 0")
    }
}

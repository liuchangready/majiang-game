package com.majiang.presentation.common.render

import com.badlogic.gdx.Gdx
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * 维基皮肤文件名映射完备性与正确性单测。
 */
class WikipediaSkinMappingTest : GdxHeadlessTestRunner() {

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
    fun `34种牌型全覆盖且映射文件名无重复`() {
        val allTiles = mutableListOf<TileView>()

        // 27 张数牌 (万筒索 1~9)
        for (suit in listOf(SuitView.MAN, SuitView.PIN, SuitView.SOU)) {
            for (rank in 1..9) {
                allTiles.add(tile(suit, rank))
            }
        }

        // 4 张风牌 (东南西北 1~4)
        for (rank in 1..4) {
            allTiles.add(tile(SuitView.WIND, rank))
        }

        // 3 张三元牌 (白发中 1~3)
        for (rank in 1..3) {
            allTiles.add(tile(SuitView.DRAGON, rank))
        }

        assertEquals(34, allTiles.size, "总牌种数应严格等于 34")

        val fileNames = allTiles.map { WikipediaSkin.getTileFileName(it) }
        val uniqueFileNames = fileNames.toSet()

        assertEquals(34, uniqueFileNames.size, "34种牌型映射生成的文件名应完全互异，无重复")

        // 验证每一个文件都在 assets/skins/wikipedia 目录下存在
        for (fileName in uniqueFileNames) {
            val handle = Gdx.files.internal("${WikipediaSkin.ASSET_BASE_PATH}/$fileName")
            assertTrue(handle.exists(), "映射的文件名 $fileName 必须真实存在于资源目录")
        }
    }

    @Test
    fun `字牌与三元牌映射顺序精确符合规范`() {
        // 风牌：1=東, 2=南, 3=西, 4=北
        assertEquals("wind1.png", WikipediaSkin.getTileFileName(SuitView.WIND, 1))
        assertEquals("wind2.png", WikipediaSkin.getTileFileName(SuitView.WIND, 2))
        assertEquals("wind3.png", WikipediaSkin.getTileFileName(SuitView.WIND, 3))
        assertEquals("wind4.png", WikipediaSkin.getTileFileName(SuitView.WIND, 4))

        // 三元牌：1=白, 2=發, 3=中
        assertEquals("dragon1.png", WikipediaSkin.getTileFileName(SuitView.DRAGON, 1))
        assertEquals("dragon2.png", WikipediaSkin.getTileFileName(SuitView.DRAGON, 2))
        assertEquals("dragon3.png", WikipediaSkin.getTileFileName(SuitView.DRAGON, 3))
    }

    @Test
    fun `数牌映射精确符合规范`() {
        for (i in 1..9) {
            assertEquals("man$i.png", WikipediaSkin.getTileFileName(SuitView.MAN, i))
            assertEquals("pin$i.png", WikipediaSkin.getTileFileName(SuitView.PIN, i))
            assertEquals("sou$i.png", WikipediaSkin.getTileFileName(SuitView.SOU, i))
        }
    }

    @Test
    fun `赤宝牌红五映射精确符合规范且物理资源存在`() {
        val redMan = tile(SuitView.MAN, 5, 3)
        val redPin = tile(SuitView.PIN, 5, 3)
        val redSou = tile(SuitView.SOU, 5, 3)

        assertEquals("man5r.png", WikipediaSkin.getTileFileName(redMan))
        assertEquals("pin5r.png", WikipediaSkin.getTileFileName(redPin))
        assertEquals("sou5r.png", WikipediaSkin.getTileFileName(redSou))

        // 普通 5 号牌应仍映射到非红五文件
        for (copyIndex in 0..2) {
            assertEquals("man5.png", WikipediaSkin.getTileFileName(tile(SuitView.MAN, 5, copyIndex)))
            assertEquals("pin5.png", WikipediaSkin.getTileFileName(tile(SuitView.PIN, 5, copyIndex)))
            assertEquals("sou5.png", WikipediaSkin.getTileFileName(tile(SuitView.SOU, 5, copyIndex)))
        }

        // 验证 3 张红五资源文件在 assets/skins/wikipedia 中真实存在
        for (fileName in listOf("man5r.png", "pin5r.png", "sou5r.png")) {
            val handle = Gdx.files.internal("${WikipediaSkin.ASSET_BASE_PATH}/$fileName")
            assertTrue(handle.exists(), "红五资源文件 $fileName 必须真实存在于资源目录")
        }
    }
}

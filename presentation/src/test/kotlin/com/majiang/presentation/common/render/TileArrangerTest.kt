package com.majiang.presentation.common.render

import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.random.Random

class TileArrangerTest {

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

    private fun displayName(tile: TileView): String = when (tile.suit) {
        SuitView.MAN -> "${tile.rank}万"
        SuitView.PIN -> "${tile.rank}筒"
        SuitView.SOU -> "${tile.rank}索"
        SuitView.WIND -> when (tile.rank) {
            1 -> "東"
            2 -> "南"
            3 -> "西"
            4 -> "北"
            else -> "?"
        }
        SuitView.DRAGON -> when (tile.rank) {
            1 -> "白"
            2 -> "發"
            3 -> "中"
            else -> "?"
        }
    }

    @Test
    fun `全顺序断言：全部34种牌型乱序输入后严格符合标准总序`() {
        // 1. 构造全部 34 种牌型的标准预期顺序：
        // 万1~9 → 索1~9 → 筒1~9 → 東南西北 → 中發白
        val expectedOrder = listOf(
            // 万子 1~9
            tile(SuitView.MAN, 1), tile(SuitView.MAN, 2), tile(SuitView.MAN, 3),
            tile(SuitView.MAN, 4), tile(SuitView.MAN, 5), tile(SuitView.MAN, 6),
            tile(SuitView.MAN, 7), tile(SuitView.MAN, 8), tile(SuitView.MAN, 9),
            // 索子 1~9
            tile(SuitView.SOU, 1), tile(SuitView.SOU, 2), tile(SuitView.SOU, 3),
            tile(SuitView.SOU, 4), tile(SuitView.SOU, 5), tile(SuitView.SOU, 6),
            tile(SuitView.SOU, 7), tile(SuitView.SOU, 8), tile(SuitView.SOU, 9),
            // 筒子 1~9
            tile(SuitView.PIN, 1), tile(SuitView.PIN, 2), tile(SuitView.PIN, 3),
            tile(SuitView.PIN, 4), tile(SuitView.PIN, 5), tile(SuitView.PIN, 6),
            tile(SuitView.PIN, 7), tile(SuitView.PIN, 8), tile(SuitView.PIN, 9),
            // 风牌 東(1) 南(2) 西(3) 北(4)
            tile(SuitView.WIND, 1), tile(SuitView.WIND, 2), tile(SuitView.WIND, 3), tile(SuitView.WIND, 4),
            // 三元牌 中(3) 發(2) 白(1) 降序
            tile(SuitView.DRAGON, 3), // 中
            tile(SuitView.DRAGON, 2), // 發
            tile(SuitView.DRAGON, 1)  // 白
        )
        assertEquals(34, expectedOrder.size)

        // 验证可读名称符合预期
        val displayNames = expectedOrder.map { displayName(it) }
        val expectedNames = listOf(
            "1万", "2万", "3万", "4万", "5万", "6万", "7万", "8万", "9万",
            "1索", "2索", "3索", "4索", "5索", "6索", "7索", "8索", "9索",
            "1筒", "2筒", "3筒", "4筒", "5筒", "6筒", "7筒", "8筒", "9筒",
            "東", "南", "西", "北",
            "中", "發", "白"
        )
        assertEquals(expectedNames, displayNames)

        // 2. 打乱顺序多次测试，输出必须完全一致
        for (seed in 1..10) {
            val shuffled = expectedOrder.shuffled(Random(seed))
            val sorted = TileArranger.sortTiles(shuffled)
            assertEquals(expectedOrder, sorted, "Random seed $seed 打乱后排序必须严格等于标准总序")

            // 同时验证 sortForDisplay（无摸牌）
            val arranged = TileArranger.sortForDisplay(shuffled, null)
            assertEquals(expectedOrder, arranged.tiles)
            assertEquals(-1, arranged.drawnTileIndex)
        }
    }

    @Test
    fun `同种多张相邻且稳定：4张同型牌按copyIndex升序排列`() {
        val pin5Copies = listOf(
            tile(SuitView.PIN, 5, copyIndex = 2),
            tile(SuitView.PIN, 5, copyIndex = 0),
            tile(SuitView.PIN, 5, copyIndex = 3),
            tile(SuitView.PIN, 5, copyIndex = 1)
        )
        val otherTiles = listOf(
            tile(SuitView.MAN, 1, copyIndex = 0),
            tile(SuitView.DRAGON, 3, copyIndex = 0), // 中
            tile(SuitView.SOU, 9, copyIndex = 0)
        )

        val allTiles = (pin5Copies + otherTiles).shuffled(Random(123))
        val sorted = TileArranger.sortTiles(allTiles)

        // 找到 5筒 的起始位置
        val startIdx = sorted.indexOfFirst { it.suit == SuitView.PIN && it.rank == 5 }
        assertTrue(startIdx >= 0)

        // 验证 4 张 5筒 连续相邻且 copyIndex 为 0, 1, 2, 3
        for (i in 0 until 4) {
            val t = sorted[startIdx + i]
            assertEquals(SuitView.PIN, t.suit)
            assertEquals(5, t.rank)
            assertEquals(i, t.copyIndex, "第 $i 张同型牌 copyIndex 应为 $i")
        }
    }

    @Test
    fun `drawnTile参与排序且能被准确识别`() {
        // 场景 A：摸牌插入到中间
        val closedA = listOf(
            tile(SuitView.MAN, 1),
            tile(SuitView.MAN, 3),
            tile(SuitView.MAN, 5),
            tile(SuitView.SOU, 2),
            tile(SuitView.PIN, 8)
        )
        val drawnA = tile(SuitView.MAN, 2)
        val arrangedA = TileArranger.sortForDisplay(closedA, drawnA)

        assertEquals(6, arrangedA.tiles.size)
        // 1m, 2m, 3m, 5m, 2s, 8p
        assertEquals(tile(SuitView.MAN, 2), arrangedA.tiles[1])
        assertEquals(1, arrangedA.drawnTileIndex)

        // 场景 B：摸牌与手中已有牌同型但 copyIndex 不同
        val closedB = listOf(
            tile(SuitView.MAN, 1, copyIndex = 0),
            tile(SuitView.MAN, 1, copyIndex = 1),
            tile(SuitView.MAN, 1, copyIndex = 3)
        )
        val drawnB = tile(SuitView.MAN, 1, copyIndex = 2)
        val arrangedB = TileArranger.sortForDisplay(closedB, drawnB)

        assertEquals(4, arrangedB.tiles.size)
        assertEquals(listOf(0, 1, 2, 3), arrangedB.tiles.map { it.copyIndex })
        assertEquals(2, arrangedB.drawnTileIndex)
        assertEquals(drawnB, arrangedB.tiles[arrangedB.drawnTileIndex])

        // 场景 C：摸牌为全手牌最小（万1）排在最前
        val closedC = listOf(
            tile(SuitView.MAN, 3),
            tile(SuitView.SOU, 1),
            tile(SuitView.DRAGON, 1) // 白
        )
        val drawnC = tile(SuitView.MAN, 1)
        val arrangedC = TileArranger.sortForDisplay(closedC, drawnC)

        assertEquals(0, arrangedC.drawnTileIndex)
        assertEquals(drawnC, arrangedC.tiles[0])

        // 场景 D：摸牌为全手牌最大（白=DRAGON 1）排在最后
        val closedD = listOf(
            tile(SuitView.MAN, 1),
            tile(SuitView.SOU, 9),
            tile(SuitView.DRAGON, 3) // 中
        )
        val drawnD = tile(SuitView.DRAGON, 1) // 白
        val arrangedD = TileArranger.sortForDisplay(closedD, drawnD)

        assertEquals(3, arrangedD.drawnTileIndex)
        assertEquals(drawnD, arrangedD.tiles.last())
    }

    @Test
    fun `空手牌与仅摸牌边界情况`() {
        // 1. 空手牌且无摸牌
        val emptyArranged = TileArranger.sortForDisplay(emptyList(), null)
        assertTrue(emptyArranged.tiles.isEmpty())
        assertEquals(-1, emptyArranged.drawnTileIndex)

        // 2. 空手牌但有摸牌
        val drawnOnly = tile(SuitView.SOU, 7)
        val singleArranged = TileArranger.sortForDisplay(emptyList(), drawnOnly)
        assertEquals(listOf(drawnOnly), singleArranged.tiles)
        assertEquals(0, singleArranged.drawnTileIndex)

        // 3. 有手牌但摸牌为 null
        val closedOnly = listOf(tile(SuitView.SOU, 5), tile(SuitView.MAN, 2))
        val closedArranged = TileArranger.sortForDisplay(closedOnly, null)
        assertEquals(listOf(tile(SuitView.MAN, 2), tile(SuitView.SOU, 5)), closedArranged.tiles)
        assertEquals(-1, closedArranged.drawnTileIndex)
    }

    @Test
    fun `三元牌中發白严格降序排列`() {
        val dragons = listOf(
            tile(SuitView.DRAGON, 1), // 白
            tile(SuitView.DRAGON, 3), // 中
            tile(SuitView.DRAGON, 2)  // 發
        )
        val sorted = TileArranger.sortTiles(dragons)
        assertEquals(
            listOf(tile(SuitView.DRAGON, 3), tile(SuitView.DRAGON, 2), tile(SuitView.DRAGON, 1)),
            sorted
        )
        assertEquals(listOf("中", "發", "白"), sorted.map { displayName(it) })
    }

    @Test
    fun `花色组顺序万索筒风三元严格符合要求`() {
        // 每种花色各选一张
        val sample = listOf(
            tile(SuitView.PIN, 5),
            tile(SuitView.DRAGON, 3),
            tile(SuitView.SOU, 5),
            tile(SuitView.MAN, 5),
            tile(SuitView.WIND, 1)
        )
        val sorted = TileArranger.sortTiles(sample)
        val expected = listOf(
            tile(SuitView.MAN, 5),
            tile(SuitView.SOU, 5),
            tile(SuitView.PIN, 5),
            tile(SuitView.WIND, 1),
            tile(SuitView.DRAGON, 3)
        )
        assertEquals(expected, sorted, "万 → 索 → 筒 → 风牌 → 三元牌")
    }
}

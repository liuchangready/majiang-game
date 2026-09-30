package com.majiang.model

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * 赤宝牌（红五）领域派生属性单测
 *
 * 遵循 DESIGN.md 测试要求第 1 组：
 * 1. isRedFive：全 136 张中恰好 3 张；
 * 2. 每数牌花色各 1 张且 copyIndex == 3；
 * 3. 字牌（风牌/三元牌）永假；
 * 4. 非 5 数牌永假；
 * 5. 数牌 5 的 copyIndex 0..2 永假。
 */
class RedFiveTest {

    @Test
    fun `全 136 张标准牌中恰好有 3 张赤宝牌`() {
        val all136 = TilePool.buildStandardSet()
        assertEquals(136, all136.size)

        val redFives = all136.filter { it.isRedFive }
        assertEquals(3, redFives.size, "全 136 张麻将牌中应恰好有 3 张赤宝牌")

        // 验证这 3 张牌精确对应万/筒/索的 copyIndex=3 的 5
        val expected = setOf(
            Tile(Suit.MAN, 5, 3),
            Tile(Suit.PIN, 5, 3),
            Tile(Suit.SOU, 5, 3)
        )
        assertEquals(expected, redFives.toSet(), "赤宝牌必须且仅包含 5万/5筒/5索 的第4张物理副本(copyIndex=3)")
    }

    @Test
    fun `数牌 5 的前三张副本 (copyIndex 0 到 2) 永假`() {
        for (suit in listOf(Suit.MAN, Suit.PIN, Suit.SOU)) {
            for (copyIndex in 0..2) {
                val tile = Tile(suit, 5, copyIndex)
                assertFalse(tile.isRedFive, "$suit 5 copyIndex=$copyIndex 不应为赤宝牌")
            }
        }
    }

    @Test
    fun `非 5 数牌即便 copyIndex 为 3 也永假`() {
        for (suit in listOf(Suit.MAN, Suit.PIN, Suit.SOU)) {
            for (rank in 1..9) {
                if (rank == 5) continue
                for (copyIndex in 0..3) {
                    val tile = Tile(suit, rank, copyIndex)
                    assertFalse(tile.isRedFive, "$suit $rank copyIndex=$copyIndex 不应为赤宝牌")
                }
            }
        }
    }

    @Test
    fun `字牌（风牌与三元牌）所有副本永假`() {
        for (rank in 1..4) {
            for (copyIndex in 0..3) {
                assertFalse(Tile(Suit.WIND, rank, copyIndex).isRedFive, "风牌不应为赤宝牌")
            }
        }
        for (rank in 1..3) {
            for (copyIndex in 0..3) {
                assertFalse(Tile(Suit.DRAGON, rank, copyIndex).isRedFive, "三元牌不应为赤宝牌")
            }
        }
    }
}

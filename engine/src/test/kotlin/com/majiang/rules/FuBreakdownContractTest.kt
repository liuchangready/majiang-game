package com.majiang.rules

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FuBreakdownContractTest {
    private val fuCalc = FuCalculator.INSTANCE
    private val decomposer = HandDecomposer.INSTANCE

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }

    @Test
    fun `equivalence regression - calculate delegates to calculateBreakdown finalFu`() {
        val closedTiles = tiles(Suit.MAN, "123") + tiles(Suit.PIN, "456") + tiles(Suit.SOU, "789") +
                tiles(Suit.MAN, "888") + tiles(Suit.PIN, "99")
        val decomps = decomposer.decompose(closedTiles)
        val decomp = decomps.first()
        val winTile = Tile(Suit.PIN, 9, 1)

        val breakdown = fuCalc.calculateBreakdown(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = true,
            isClosed = true,
            seatWind = 1,
            roundWind = 1
        )
        val oldFu = fuCalc.calculate(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = true,
            isClosed = true,
            seatWind = 1,
            roundWind = 1
        )
        assertEquals(oldFu, breakdown.finalFu, "calculate() 必须全等于 calculateBreakdown().finalFu")
    }

    @Test
    fun `structural invariant - lines sum equals subtotal and rounding is correct`() {
        // 门前自摸暗刻手牌: 888m 456p 789s 123m + 99p (单骑听9p)
        val closedTiles = tiles(Suit.MAN, "123") + tiles(Suit.PIN, "456") + tiles(Suit.SOU, "789") +
                tiles(Suit.MAN, "888") + tiles(Suit.PIN, "99")
        val decomp = decomposer.decompose(closedTiles).first()
        val winTile = Tile(Suit.PIN, 9, 1)

        val breakdown = fuCalc.calculateBreakdown(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = true,
            isClosed = true,
            seatWind = 1,
            roundWind = 2
        )

        // 底符20 + 中张暗刻8m×4 + 听牌单骑2 + 自摸2 = 28 -> 切上30
        assertEquals(breakdown.subtotal, breakdown.lines.sumOf { it.fu }, "各构成行符数之和必须严格等于小计")
        assertEquals(28, breakdown.subtotal)
        assertEquals(30, breakdown.roundedUpTo, "28符向上切上到10的倍数应为30符")
        assertEquals(30, breakdown.finalFu)
        assertNull(breakdown.specialNote)
    }

    @Test
    fun `chiitoitsu has fixed 25 fu with special note`() {
        val closedTiles = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 1, 1),
            Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 2, 1),
            Tile(Suit.MAN, 3, 0), Tile(Suit.MAN, 3, 1),
            Tile(Suit.PIN, 4, 0), Tile(Suit.PIN, 4, 1),
            Tile(Suit.PIN, 5, 0), Tile(Suit.PIN, 5, 1),
            Tile(Suit.SOU, 6, 0), Tile(Suit.SOU, 6, 1),
            Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 7, 1)
        )
        val decomp = decomposer.decompose(closedTiles).first { it.pattern == HandDecomposer.DecompPattern.CHIITOITSU }
        val winTile = Tile(Suit.SOU, 7, 1)

        val breakdown = fuCalc.calculateBreakdown(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = true,
            isClosed = true,
            seatWind = 1,
            roundWind = 1
        )
        assertEquals(25, breakdown.finalFu)
        assertEquals(25, breakdown.subtotal)
        assertEquals(25, breakdown.roundedUpTo)
        assertEquals(25, breakdown.lines.sumOf { it.fu })
        assertEquals("七对子 25符", breakdown.specialNote)
        assertEquals(25, fuCalc.calculate(decomp, winTile, true, true, 1, 1))
    }

    @Test
    fun `pinfu tsumo has fixed 20 fu with special note`() {
        // 平和自摸: 123m 456m 234p 678s + 99s (两面听牌 2-5p 或两面听6-9s等，雀头非役牌)
        val closedTiles = tiles(Suit.MAN, "123456") + tiles(Suit.PIN, "234") + tiles(Suit.SOU, "678") + tiles(Suit.SOU, "99")
        val decomp = decomposer.decompose(closedTiles).first { it.pattern == HandDecomposer.DecompPattern.REGULAR }
        val winTile = Tile(Suit.PIN, 4, 0) // 两面和牌

        val breakdown = fuCalc.calculateBreakdown(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = true,
            isClosed = true,
            seatWind = 2,
            roundWind = 2
        )
        assertEquals(20, breakdown.finalFu, "门前平和自摸固定20符")
        assertEquals(20, breakdown.subtotal)
        assertEquals(20, breakdown.roundedUpTo)
        assertEquals("平和自摸 20符", breakdown.specialNote)
        assertEquals(20, fuCalc.calculate(decomp, winTile, true, true, 2, 2))
    }

    @Test
    fun `open pinfu ron guarantees 30 fu with special note`() {
        // 喰い平和荣和 (副露且全是顺子、雀头非役牌、两面和): 副底20切上20 -> 保底30
        val closedTiles = tiles(Suit.MAN, "123") + tiles(Suit.PIN, "234") + tiles(Suit.SOU, "22")
        val decomp = decomposer.decompose(closedTiles).first()
        val winTile = Tile(Suit.PIN, 4, 0)
        val melds = listOf(
            Meld(MeldType.CHI, tiles(Suit.MAN, "456"), 1, Tile(Suit.MAN, 4)),
            Meld(MeldType.CHI, tiles(Suit.SOU, "789"), 1, Tile(Suit.SOU, 7))
        )

        val breakdown = fuCalc.calculateBreakdown(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = false,
            isClosed = false,
            seatWind = 1,
            roundWind = 2,
            openMelds = melds
        )
        assertEquals(20, breakdown.subtotal, "副露荣和底符20且无其它加符")
        assertEquals(20, breakdown.roundedUpTo)
        assertEquals(30, breakdown.finalFu, "荣和最低30符保底")
        assertEquals("荣和 30符保底", breakdown.specialNote)
        assertEquals(30, fuCalc.calculate(decomp, winTile, false, false, 1, 2, melds))
    }

    @Test
    fun `complex hand accurately breaks down triplets, kans, honors, wait and tsumo`() {
        // 1m暗刻(幺九8) + 8s暗刻(中张4) + 2p雀头(0) + 456s顺子 + 嵌张听5m
        // 加上副露中暗杠中(32)
        val closedTiles = tiles(Suit.MAN, "111") + tiles(Suit.SOU, "888") + tiles(Suit.PIN, "22") + tiles(Suit.SOU, "456") + tiles(Suit.MAN, "46")
        // 门前46m 嵌张听 5m
        val handTiles = closedTiles + Tile(Suit.MAN, 5, 0)
        val decomp = decomposer.decompose(handTiles).first()
        val winTile = Tile(Suit.MAN, 5, 0)

        val darkKanChun = Meld(
            type = MeldType.CLOSED_KAN,
            tiles = listOf(Tile(Suit.DRAGON, 3, 0), Tile(Suit.DRAGON, 3, 1), Tile(Suit.DRAGON, 3, 2), Tile(Suit.DRAGON, 3, 3)),
            fromPlayer = 0,
            calledTile = Tile(Suit.DRAGON, 3, 0)
        )

        val breakdown = fuCalc.calculateBreakdown(
            decomposition = decomp,
            winTile = winTile,
            isTsumo = true,
            isClosed = false, // 有暗杠副露
            seatWind = 1,
            roundWind = 1,
            openMelds = listOf(darkKanChun)
        )

        // 底符: 自摸20
        // 暗刻1m: 幺九8
        // 暗刻8s: 中张4
        // 暗杠中: 幺九暗杠32
        // 听牌: 嵌张2
        // 自摸: 2
        // 小计: 20 + 8 + 4 + 32 + 2 + 2 = 68
        // 切上: 70符
        assertEquals(68, breakdown.subtotal)
        assertEquals(70, breakdown.roundedUpTo)
        assertEquals(70, breakdown.finalFu)
        assertEquals(breakdown.subtotal, breakdown.lines.sumOf { it.fu })
        assertNull(breakdown.specialNote)

        val descriptions = breakdown.lines.map { it.description }
        assertTrue(descriptions.contains("底符"))
        assertTrue(descriptions.any { it.contains("暗刻1m") })
        assertTrue(descriptions.any { it.contains("暗刻8s") })
        assertTrue(descriptions.any { it.contains("暗杠中") })
        assertTrue(descriptions.any { it.contains("听牌嵌张") })
        assertTrue(descriptions.contains("自摸"))
    }
}

package com.majiang.rules

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class DoraBreakdownContractTest {
    private val evaluator = WinningScoreEvaluator()

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }

    @Test
    fun `equivalence regression - totalHan equals countDora on all combinations`() {
        val hand = PlayerHand()
        val redFiveMan = Tile(Suit.MAN, 5, 3)
        val normalFiveMan = Tile(Suit.MAN, 5, 0)
        hand.closedTiles.addAll(listOf(redFiveMan, normalFiveMan) + tiles(Suit.PIN, "123456") + tiles(Suit.SOU, "789"))
        val indicators = listOf(Tile(Suit.MAN, 4, 0), Tile(Suit.PIN, 1, 0))
        val uraIndicators = listOf(Tile(Suit.SOU, 6, 0))

        val breakdown = evaluator.breakdownDora(hand, indicators, uraIndicators)
        val oldCount = evaluator.countDora(hand, indicators, uraIndicators)

        assertEquals(oldCount, breakdown.totalHan, "totalHan 必须与 countDora() 完全一致")
        assertEquals(breakdown.totalHan, breakdown.total, "兼容属性 total 必须全等于 totalHan")
    }

    @Test
    fun `three-way split separates omote, ura and red dora with hit tile lists`() {
        val hand = PlayerHand()
        val redFiveMan = Tile(Suit.MAN, 5, 3)
        val normalFiveMan = Tile(Suit.MAN, 5, 0)
        val normalTwoPin = Tile(Suit.PIN, 2, 0)
        val normalSevenSou = Tile(Suit.SOU, 7, 0)

        hand.closedTiles.addAll(listOf(redFiveMan, normalFiveMan, normalTwoPin, normalSevenSou))

        // 表指示牌: 4万 (指示5万)
        val omote = listOf(Tile(Suit.MAN, 4, 0))
        // 里指示牌: 1筒 (指示2筒), 6索 (指示7索)
        val ura = listOf(Tile(Suit.PIN, 1, 0), Tile(Suit.SOU, 6, 0))

        val breakdown = evaluator.breakdownDora(hand, omote, ura)

        // 表宝牌: 命中2张5万 (普通5万 + 红5万)
        assertEquals(2, breakdown.indicatorCount)
        assertEquals(2, breakdown.indicatorTiles.size)
        assertTrue(breakdown.indicatorTiles.contains(normalFiveMan))
        assertTrue(breakdown.indicatorTiles.contains(redFiveMan))

        // 里宝牌: 命中2筒与7索
        assertEquals(2, breakdown.uraCount)
        assertEquals(2, breakdown.uraTiles.size)
        assertTrue(breakdown.uraTiles.contains(normalTwoPin))
        assertTrue(breakdown.uraTiles.contains(normalSevenSou))

        // 赤宝牌: 包含红5万
        assertEquals(1, breakdown.redCount)
        assertEquals(1, breakdown.redTiles.size)
        assertTrue(breakdown.redTiles.contains(redFiveMan))

        // 总翻数: 2 (表) + 2 (里) + 1 (赤) = 5
        assertEquals(5, breakdown.totalHan)
        assertEquals(5, evaluator.countDora(hand, omote, ura))
    }

    @Test
    fun `ura dora is only included when isRiichi is true in explain and evaluate`() {
        val hand = PlayerHand()
        val winTile = Tile(Suit.MAN, 1, 0)
        // 平和手牌
        hand.closedTiles.addAll(tiles(Suit.MAN, "23") + tiles(Suit.PIN, "234567") + tiles(Suit.SOU, "23488"))

        val dora = listOf(Tile(Suit.PIN, 1, 0)) // 指示 2筒 (手牌有1张2筒)
        val uraDora = listOf(Tile(Suit.SOU, 1, 0)) // 指示 2索 (手牌有1张2索)

        // 1. 未立直: 里宝牌不计入
        val nonRiichiCtx = YakuEvaluator.WinContext(winTile, false, true, 2, 1, isRiichi = false)
        val nonRiichiBreakdown = evaluator.explain(hand, nonRiichiCtx, dora, uraDora)!!
        assertEquals(1, nonRiichiBreakdown.dora.indicatorCount)
        assertEquals(0, nonRiichiBreakdown.dora.uraCount, "未立直时里宝牌计数必须为0")
        assertTrue(nonRiichiBreakdown.dora.uraTiles.isEmpty(), "未立直时里宝牌命中列表必须为空")
        assertEquals(1, nonRiichiBreakdown.dora.totalHan)

        // 2. 立直: 里宝牌计入
        val riichiCtx = YakuEvaluator.WinContext(winTile, false, true, 2, 1, isRiichi = true)
        val riichiBreakdown = evaluator.explain(hand, riichiCtx, dora, uraDora)!!
        assertEquals(1, riichiBreakdown.dora.indicatorCount)
        assertEquals(1, riichiBreakdown.dora.uraCount, "立直时里宝牌计数正确计入")
        assertEquals(1, riichiBreakdown.dora.uraTiles.size)
        assertEquals(2, riichiBreakdown.dora.totalHan)
    }

    @Test
    fun `melds containing red five and matching indicators are included in tile lists`() {
        val hand = PlayerHand()
        val redFivePin = Tile(Suit.PIN, 5, 3)
        hand.closedTiles.add(redFivePin)

        val redFiveSou = Tile(Suit.SOU, 5, 3)
        val chiMeld = Meld(
            type = MeldType.CHI,
            tiles = listOf(Tile(Suit.SOU, 3), Tile(Suit.SOU, 4), redFiveSou),
            fromPlayer = 1,
            calledTile = Tile(Suit.SOU, 3)
        )
        hand.melds.add(chiMeld)

        // 表指示牌 4索 (指示 5索)
        val dora = listOf(Tile(Suit.SOU, 4, 0))
        val breakdown = evaluator.breakdownDora(hand, dora, emptyList())

        assertEquals(1, breakdown.indicatorCount, "副露中红5索命中指示牌")
        assertEquals(listOf(redFiveSou), breakdown.indicatorTiles)
        assertEquals(2, breakdown.redCount, "手牌红5筒 + 副露红5索")
        assertTrue(breakdown.redTiles.contains(redFivePin))
        assertTrue(breakdown.redTiles.contains(redFiveSou))
        assertEquals(3, breakdown.totalHan)
    }
}

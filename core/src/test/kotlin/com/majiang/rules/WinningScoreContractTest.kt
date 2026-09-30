package com.majiang.rules

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class WinningScoreContractTest {
    private val evaluator = WinningScoreEvaluator()
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }

    @Test
    fun `regular interpretation beats seven pairs with a consistent four han thirty fu score`() {
        val hand = PlayerHand()
        val winning = Tile(Suit.PIN, 6, 1)
        hand.closedTiles.addAll((tiles(Suit.MAN, "112233") + tiles(Suit.PIN, "445566") + tiles(Suit.SOU, "77")).filter { it != winning })
        val before = hand.closedTiles.toList()
        val result = evaluator.evaluate(hand, YakuEvaluator.WinContext(winning, false, true, 2, 1))!!
        assertEquals(HandDecomposer.DecompPattern.REGULAR, result.decomposition.pattern)
        assertEquals(4, result.yakuResult.totalHan)
        assertEquals(30, result.fu)
        assertEquals(7700, result.totalPoints)
        assertEquals(before, hand.closedTiles)
        assertNull(hand.drawnTile)
    }

    @Test
    fun `repeated dora indicators each count their matching tiles including melds`() {
        val hand = PlayerHand()
        hand.closedTiles.addAll(tiles(Suit.MAN, "55"))
        hand.melds.add(Meld(MeldType.PON, tiles(Suit.PIN, "333"), 1, Tile(Suit.PIN, 3)))
        val indicators = listOf(Tile(Suit.MAN, 4, 0), Tile(Suit.MAN, 4, 1), Tile(Suit.PIN, 2))
        assertEquals(7, evaluator.countDora(hand, indicators))
        assertEquals(9, evaluator.countDora(hand, indicators, listOf(Tile(Suit.MAN, 4, 2))))
    }

    @Test
    fun `dora alone never makes an open no yaku hand legal`() {
        val hand = PlayerHand()
        hand.melds.add(Meld(MeldType.CHI, tiles(Suit.MAN, "123"), 3, Tile(Suit.MAN, 1)))
        hand.closedTiles.addAll(tiles(Suit.MAN, "456") + tiles(Suit.PIN, "78955") + tiles(Suit.SOU, "123"))
        val result = evaluator.evaluate(hand, YakuEvaluator.WinContext(Tile(Suit.PIN, 5, 1), false, false, 2, 1), extraHan = 10)
        assertNull(result)
    }

    @Test
    fun `breakdownDora and countDora stack indicator and red dora for 2 han on same physical tile`() {
        val hand = PlayerHand()
        val redFiveMan = Tile(Suit.MAN, 5, 3)
        val normalFiveMan = Tile(Suit.MAN, 5, 0)
        hand.closedTiles.addAll(listOf(redFiveMan, normalFiveMan))

        // 1. 无任何指示牌：仅红五计 1 翻
        val noIndicator = evaluator.breakdownDora(hand, emptyList())
        assertEquals(0, noIndicator.indicatorCount)
        assertEquals(1, noIndicator.redCount)
        assertEquals(1, noIndicator.total)
        assertEquals(1, evaluator.countDora(hand, emptyList()))

        // 2. 指示牌为 4万（指示 5万）：
        // normalFiveMan 命中 1 次指示牌 = 1 翻
        // redFiveMan 命中 1 次指示牌 + 1 翻赤宝牌 = 2 翻
        // 总计 indicatorCount=2, redCount=1, total=3 翻
        val indicator4m = listOf(Tile(Suit.MAN, 4, 0))
        val with4m = evaluator.breakdownDora(hand, indicator4m)
        assertEquals(2, with4m.indicatorCount, "2张5万命中指示牌")
        assertEquals(1, with4m.redCount, "包含1张红5万")
        assertEquals(3, with4m.total, "总翻数应为 3 (普通5万1翻 + 红5万2翻)")
        assertEquals(3, evaluator.countDora(hand, indicator4m))

        // 3. 仅有红5万自身（无普通5万）：单张红5万被指示时叠加计 2 翻
        val soloRedHand = PlayerHand().apply { closedTiles.add(redFiveMan) }
        val soloBreakdown = evaluator.breakdownDora(soloRedHand, indicator4m)
        assertEquals(1, soloBreakdown.indicatorCount, "红5万命中指示牌1次")
        assertEquals(1, soloBreakdown.redCount, "红5万自身计1翻赤宝牌")
        assertEquals(2, soloBreakdown.total, "红5万被指示5时共计2翻")
        assertEquals(2, evaluator.countDora(soloRedHand, indicator4m))
    }

    @Test
    fun `melds containing red five are counted in breakdown and countDora`() {
        val hand = PlayerHand()
        // 门前暗牌包含红五筒
        val redFivePin = Tile(Suit.PIN, 5, 3)
        hand.closedTiles.add(redFivePin)

        // 副露吃包含红五索 (3索, 4索, 红5索)
        val redFiveSou = Tile(Suit.SOU, 5, 3)
        val meld = Meld(
            type = MeldType.CHI,
            tiles = listOf(Tile(Suit.SOU, 3), Tile(Suit.SOU, 4), redFiveSou),
            fromPlayer = 1,
            calledTile = Tile(Suit.SOU, 3)
        )
        hand.melds.add(meld)

        // 无指示牌：暗牌红5筒 + 副露红5索 = 2 翻赤宝牌
        val breakdown = evaluator.breakdownDora(hand, emptyList())
        assertEquals(0, breakdown.indicatorCount)
        assertEquals(2, breakdown.redCount)
        assertEquals(2, breakdown.total)
        assertEquals(2, evaluator.countDora(hand, emptyList()))

        // 表指示牌 4索：副露中红5索命中指示牌 (1 翻表宝牌 + 1 翻赤宝牌 = 2 翻)
        // 加上暗牌红5筒 (1 翻赤宝牌) = 总计 3 翻
        val with4s = evaluator.breakdownDora(hand, listOf(Tile(Suit.SOU, 4, 0)))
        assertEquals(1, with4s.indicatorCount)
        assertEquals(2, with4s.redCount)
        assertEquals(3, with4s.total)
        assertEquals(3, evaluator.countDora(hand, listOf(Tile(Suit.SOU, 4, 0))))
    }

    @Test
    fun `ura dora is only included when isRiichi is true`() {
        val hand = PlayerHand()
        val winning = Tile(Suit.MAN, 1, 1)
        // 平和听 1-4万
        hand.closedTiles.addAll(tiles(Suit.MAN, "23") + tiles(Suit.PIN, "234567") + tiles(Suit.SOU, "23488"))

        val dora = listOf(Tile(Suit.PIN, 1, 0)) // 指示 2筒 (手牌有1张2筒)
        val uraDora = listOf(Tile(Suit.SOU, 1, 0)) // 指示 2索 (手牌有1张2索)

        // 1. 未立直和牌：里宝牌不计入
        val nonRiichiCtx = YakuEvaluator.WinContext(winning, false, true, 2, 1, isRiichi = false)
        val nonRiichiResult = evaluator.evaluate(hand, nonRiichiCtx, doraIndicators = dora, uraDoraIndicators = uraDora)!!
        // yaku: 平和 (1) + dora (1) = 2翻
        assertEquals(2, nonRiichiResult.scoreResult.han)

        // 2. 立直和牌：里宝牌计入
        val riichiCtx = YakuEvaluator.WinContext(winning, false, true, 2, 1, isRiichi = true)
        val riichiResult = evaluator.evaluate(hand, riichiCtx, doraIndicators = dora, uraDoraIndicators = uraDora)!!
        // yaku: 立直 (1) + 平和 (1) + 表dora (1) + 里dora (1) = 4翻
        assertEquals(4, riichiResult.scoreResult.han)
    }

    @Test
    fun `red five alone cannot make an open hand with no yaku legal`() {
        val hand = PlayerHand()
        // 副露包含老头牌 123万
        hand.melds.add(Meld(MeldType.CHI, tiles(Suit.MAN, "123"), 3, Tile(Suit.MAN, 1)))
        // 手牌含有红五筒，但无任何合法役种 (役牌、断幺等均无)
        val redFivePin = Tile(Suit.PIN, 5, 3)
        hand.closedTiles.addAll(listOf(redFivePin, Tile(Suit.PIN, 6, 0), Tile(Suit.PIN, 7, 0)) +
                tiles(Suit.MAN, "456") + tiles(Suit.SOU, "12377"))
        val result = evaluator.evaluate(
            hand = hand,
            context = YakuEvaluator.WinContext(Tile(Suit.SOU, 7, 1), false, false, 2, 1),
            doraIndicators = emptyList()
        )
        assertNull(result, "赤宝牌不作为起和役，无役手牌即便拥有红五也绝不能和牌")
    }
}

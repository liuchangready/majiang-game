package com.majiang.rules

import com.majiang.model.PlayerHand
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.settlement.SettlementService
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 第一阶段「算分格式化边界」行为等价与边界契约。
 *
 * 期望值全部写成提取前的字面量与独立算术（不调用 [ScoreTextFormatter] 自身推导期望值），
 * 因此这些用例能真实捕捉文案漂移；同时保留既有的数值、支付与守恒断言。
 */
class ScoreTextFormatterContractTest {

    private val scoreCalc = ScoreCalculator.INSTANCE
    private val settlementService = SettlementService.INSTANCE
    private val evaluator = WinningScoreEvaluator()

    private data class TierCase(
        val fu: Int,
        val han: Int,
        val base: Int,
        val title: String,
        val capReason: String?,
        val formula: String
    )

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }

    private fun pinfu() = PlayerHand().apply {
        closedTiles.addAll(tiles(Suit.MAN, "23") + tiles(Suit.PIN, "234567") + tiles(Suit.SOU, "23488"))
    }

    private fun pinfuContext(tsumo: Boolean = false, riichi: Boolean = false) =
        YakuEvaluator.WinContext(Tile(Suit.MAN, 1), tsumo, true, 2, 1, isRiichi = riichi)

    // ------------------------------------------------------------------ 数值档位文案

    @Test
    fun `calculator keeps pre-refactor title capReason and formula at every tier boundary`() {
        val cases = listOf(
            // 普通分：未封顶时 title 为「N翻M符」，无封顶原因
            TierCase(30, 2, 480, "2翻30符", null, "30符 × 2^(2+2) = 480"),
            TierCase(30, 3, 960, "3翻30符", null, "30符 × 2^(2+3) = 960"),
            TierCase(30, 4, 1920, "4翻30符", null, "30符 × 2^(2+4) = 1920"),
            TierCase(40, 3, 1280, "3翻40符", null, "40符 × 2^(2+3) = 1280"),
            // 满贯边界：1920 未封顶，2240/2560/3840 封顶
            TierCase(60, 3, 1920, "3翻60符", null, "60符 × 2^(2+3) = 1920"),
            TierCase(70, 3, 2000, "満貫", "満貫封頂", "70符 × 2^(2+3) = 2240 → 満貫封頂 2000"),
            TierCase(40, 4, 2000, "満貫", "満貫封頂", "40符 × 2^(2+4) = 2560 → 満貫封頂 2000"),
            TierCase(30, 5, 2000, "満貫", "満貫封頂", "30符 × 2^(2+5) = 3840 → 満貫封頂 2000"),
            // 跳满
            TierCase(30, 6, 3000, "跳満", "跳満", "30符 × 2^(2+6) = 7680 → 跳満 3000"),
            TierCase(30, 7, 3000, "跳満", "跳満", "30符 × 2^(2+7) = 15360 → 跳満 3000"),
            // 倍满
            TierCase(30, 8, 4000, "倍満", "倍満", "30符 × 2^(2+8) = 30720 → 倍満 4000"),
            TierCase(30, 10, 4000, "倍満", "倍満", "30符 × 2^(2+10) = 122880 → 倍満 4000"),
            // 三倍满
            TierCase(30, 11, 6000, "三倍満", "三倍満", "30符 × 2^(2+11) = 245760 → 三倍満 6000"),
            TierCase(30, 12, 6000, "三倍満", "三倍満", "30符 × 2^(2+12) = 491520 → 三倍満 6000"),
            // 累计役满
            TierCase(30, 13, 8000, "数え役満", "数え役満", "30符 × 2^(2+13) = 983040 → 数え役満 8000"),
            TierCase(30, 25, 8000, "数え役満", "数え役満", "30符 × 2^(2+25) = 4026531840 → 数え役満 8000")
        )
        for (c in cases) {
            val r = scoreCalc.calculate(c.fu, c.han)
            val label = "${c.fu}符${c.han}翻"
            assertEquals(c.base, r.basePoints, "$label basePoints")
            assertEquals(c.title, r.title, "$label title")
            assertEquals(c.capReason, r.capReason, "$label capReason")
            assertEquals(c.formula, r.formulaText, "$label formulaText")
            assertFalse(r.isYakuman, "$label 非役满")
        }
    }

    @Test
    fun `yakuman titles keep single double and multi yakuman wording`() {
        val one = scoreCalc.calculate(30, 0, yakumanCount = 1)
        assertEquals("役満", one.title)
        assertEquals("役満", one.capReason)
        assertEquals("役満 基本点 8000", one.formulaText)
        assertEquals(8000, one.basePoints)
        assertEquals(0, one.han)
        assertEquals("8000", one.rawBasePointsExact)
        assertTrue(one.isYakuman)

        val two = scoreCalc.calculate(25, 0, yakumanCount = 2)
        assertEquals("ダブル役満", two.title)
        assertEquals("ダブル役満", two.capReason)
        assertEquals("ダブル役満 基本点 16000", two.formulaText)
        assertEquals(16000, two.basePoints)
        assertEquals("16000", two.rawBasePointsExact)

        val three = scoreCalc.calculate(30, 0, yakumanCount = 3)
        assertEquals("3倍役満", three.title)
        assertEquals("3倍役満 基本点 24000", three.formulaText)
        assertEquals(24000, three.basePoints)

        val four = scoreCalc.calculate(30, 0, yakumanCount = 4)
        assertEquals("4倍役満", four.title)
        assertEquals("4倍役満 基本点 32000", four.formulaText)
        assertEquals(32000, four.basePoints)
    }

    @Test
    fun `cap table renders every limit tier with the pre-refactor wording`() {
        val expected = listOf(
            Triple(ScoreCap.MANGAN, "満貫", "満貫封頂"),
            Triple(ScoreCap.HANEMAN, "跳満", "跳満"),
            Triple(ScoreCap.BAIMAN, "倍満", "倍満"),
            Triple(ScoreCap.SANBAIMAN, "三倍満", "三倍満"),
            Triple(ScoreCap.KAZOE_YAKUMAN, "数え役満", "数え役満")
        )
        for ((cap, title, reason) in expected) {
            val text = ScoreTextFormatter.normalText(30, 13, "983040", cap, 8000)
            assertEquals(title, text.title, "$cap title")
            assertEquals(reason, text.capReason, "$cap capReason")
            assertEquals("30符 × 2^(2+13) = 983040 → $reason 8000", text.formulaText, "$cap formulaText")
        }

        val uncapped = ScoreTextFormatter.normalText(30, 2, "480", null, 480)
        assertEquals("2翻30符", uncapped.title)
        assertNull(uncapped.capReason)
        assertEquals("30符 × 2^(2+2) = 480", uncapped.formulaText)
    }

    // ------------------------------------------------------------------ 支付文案与兼容入口

    @Test
    fun `payment lines keep exact wording and the settlement entry point delegates identically`() {
        val score = scoreCalc.calculate(30, 3) // 基本点 30 × 2^5 = 960
        assertEquals(2000, score.dealerTsumoPayment())
        assertEquals(2000 to 1000, score.nonDealerTsumoPayment())
        assertEquals(5800, score.dealerRonPayment())
        assertEquals(3900, score.nonDealerRonPayment())

        val dealerTsumo = listOf("自摸：闲家各付 2000")
        val nonDealerTsumo = listOf("自摸：庄家付 2000、闲家各付 1000")
        val dealerRon = listOf("荣和：P2 支付 5800")
        val noLoserRon = listOf("荣和：放铳者 支付 3900")

        assertEquals(dealerTsumo, ScoreTextFormatter.paymentLines(score, isDealer = true, isTsumo = true))
        assertEquals(nonDealerTsumo, ScoreTextFormatter.paymentLines(score, isDealer = false, isTsumo = true))
        assertEquals(dealerRon, ScoreTextFormatter.paymentLines(score, isDealer = true, isTsumo = false, loser = 2))
        assertEquals(noLoserRon, ScoreTextFormatter.paymentLines(score, isDealer = false, isTsumo = false))

        // SettlementService 的公开入口保持兼容，逐字委托同一实现
        assertEquals(dealerTsumo, settlementService.formatPaymentLines(score, isDealer = true, isTsumo = true))
        assertEquals(nonDealerTsumo, settlementService.formatPaymentLines(score, isDealer = false, isTsumo = true))
        assertEquals(dealerRon, settlementService.formatPaymentLines(score, isDealer = true, isTsumo = false, loser = 2))
        assertEquals(noLoserRon, settlementService.formatPaymentLines(score, isDealer = false, isTsumo = false))
        assertEquals(
            settlementService.formatPaymentLines(score, isDealer = false, isTsumo = false, loser = 0),
            ScoreTextFormatter.paymentLines(score, isDealer = false, isTsumo = false, loser = 0)
        )

        // 座位占位：显式 0/3 号与「放铳者」占位互不影响
        assertEquals(listOf("荣和：P0 支付 5800"), ScoreTextFormatter.paymentLines(score, true, false, 0))
        assertEquals(listOf("荣和：P3 支付 3900"), ScoreTextFormatter.paymentLines(score, false, false, 3))

        // 役满档位支付文案
        val yakuman = scoreCalc.calculate(30, 0, yakumanCount = 1) // 基本点 8000
        assertEquals(listOf("自摸：闲家各付 16000"), ScoreTextFormatter.paymentLines(yakuman, true, true))
        assertEquals(listOf("自摸：庄家付 16000、闲家各付 8000"), ScoreTextFormatter.paymentLines(yakuman, false, true))
        assertEquals(listOf("荣和：P1 支付 48000"), ScoreTextFormatter.paymentLines(yakuman, true, false, 1))
        assertEquals(listOf("荣和：放铳者 支付 32000"), ScoreTextFormatter.paymentLines(yakuman, false, false))
    }

    @Test
    fun `yaku lines map display names preserving order duplicates and zero-han yakuman entries`() {
        val input = listOf(Yaku.RIICHI to 1, Yaku.PINFU to 1, Yaku.DAISANGEN to 0, Yaku.PINFU to 1)
        assertEquals(
            listOf("立直" to 1, "平和" to 1, "大三元" to 0, "平和" to 1),
            ScoreTextFormatter.yakuLines(input)
        )
        assertTrue(ScoreTextFormatter.yakuLines(emptyList()).isEmpty())
    }

    // ------------------------------------------------------------------ explain 端到端展示

    @Test
    fun `ordinary ron explanation keeps yaku payment honba and riichi stick text stable`() {
        val b = evaluator.explain(pinfu(), pinfuContext(), emptyList(), honba = 2, riichiSticks = 1, loser = 3)!!
        assertEquals(listOf("平和" to 1), b.yakuLines)
        assertEquals(listOf("荣和：P3 支付 1000"), b.paymentLines)
        assertEquals("1翻30符", b.score.title)
        assertNull(b.score.capReason)
        assertEquals("30符 × 2^(2+1) = 240", b.score.formulaText)
        assertEquals(600, b.honbaAddition)
        assertEquals(1000, b.riichiAddition)
        assertEquals(2600, b.totalPoints)
        assertFalse(b.uraEligible)
        assertFalse(b.isDealer)
        assertFalse(b.isTsumo)
    }

    @Test
    fun `mangan explanation exposes capped title reason and formula while payment stays numeric`() {
        // 指示 4索，门前手牌含一张 4索 → 4 翻宝牌，合计 平和1 + 宝牌4 = 5 翻
        val indicators = List(4) { Tile(Suit.SOU, 3, 0) }
        val b = evaluator.explain(pinfu(), pinfuContext(), indicators, loser = 0)!!
        assertEquals(listOf("平和" to 1), b.yakuLines)
        assertEquals(5, b.score.han)
        assertEquals(2000, b.score.basePoints)
        assertEquals("満貫", b.score.title)
        assertEquals("満貫封頂", b.score.capReason)
        assertEquals("30符 × 2^(2+5) = 3840 → 満貫封頂 2000", b.score.formulaText)
        assertEquals(listOf("荣和：P0 支付 8000"), b.paymentLines)
        assertEquals(8000, b.totalPoints)
    }

    @Test
    fun `kazoe yakuman explanation labels counted yakuman without changing the numeric payment`() {
        // 13 张重复指示牌各命中门前唯一一张 4索 → 13 翻宝牌，合计 平和1 + 宝牌13 = 14 翻
        val indicators = List(13) { Tile(Suit.SOU, 3, 0) }
        val b = evaluator.explain(pinfu(), pinfuContext(), indicators, loser = 1)!!
        assertEquals(listOf("平和" to 1), b.yakuLines)
        assertEquals(14, b.score.han)
        assertEquals(8000, b.score.basePoints)
        assertEquals("数え役満", b.score.title)
        assertEquals("数え役満", b.score.capReason)
        assertEquals("30符 × 2^(2+14) = 1966080 → 数え役満 8000", b.score.formulaText)
        assertEquals(0, b.yakumanCount)
        assertFalse(b.score.isYakuman)
        assertEquals(listOf("荣和：P1 支付 32000"), b.paymentLines)
        assertEquals(32000, b.totalPoints)
    }

    @Test
    fun `yakuman explanation keeps multi-yakuman wording and riichi-only ura eligibility`() {
        val hand = PlayerHand().apply {
            closedTiles.addAll(tiles(Suit.DRAGON, "11122233") + tiles(Suit.WIND, "11122"))
        }
        val ctx = YakuEvaluator.WinContext(Tile(Suit.DRAGON, 3, 2), true, true, 2, 1, isRiichi = true)
        val b = evaluator.explain(hand, ctx, listOf(Tile(Suit.DRAGON, 2)), listOf(Tile(Suit.WIND, 4)))!!
        assertEquals(setOf("四暗刻", "大三元", "字一色"), b.yakuLines.map { it.first }.toSet())
        assertEquals(3, b.yakumanCount)
        assertEquals(0, b.score.han)
        assertEquals("3倍役満", b.score.title)
        assertEquals("3倍役満", b.score.capReason)
        assertEquals("3倍役満 基本点 24000", b.score.formulaText)
        assertEquals(24000, b.score.basePoints)
        assertEquals(listOf("自摸：庄家付 48000、闲家各付 24000"), b.paymentLines)
        assertEquals(96000, b.totalPoints)
        assertTrue(b.uraEligible)
        assertTrue(b.dora.uraHits.isNotEmpty())
        assertTrue(b.isTsumo)
    }

    @Test
    fun `ura eligibility and ura payment text follow the riichi flag exactly`() {
        val omote = listOf(Tile(Suit.MAN, 9)) // 指示 1万，命中和了牌
        val ura = listOf(Tile(Suit.SOU, 7))   // 指示 8索，命中手牌两张 8索

        val notRiichi = evaluator.explain(pinfu(), pinfuContext(), omote, ura, loser = 0)!!
        assertFalse(notRiichi.uraEligible)
        assertTrue(notRiichi.dora.uraHits.isEmpty())
        assertTrue(notRiichi.dora.uraTiles.isEmpty())
        assertEquals(2, notRiichi.score.han)
        assertEquals(listOf("荣和：P0 支付 2000"), notRiichi.paymentLines)
        assertEquals(2000, notRiichi.totalPoints)

        val riichi = evaluator.explain(pinfu(), pinfuContext(riichi = true), omote, ura, loser = 0)!!
        assertEquals(setOf("立直" to 1, "平和" to 1), riichi.yakuLines.toSet())
        assertTrue(riichi.uraEligible)
        assertEquals(2, riichi.dora.uraHits.single().tiles.size)
        assertEquals(5, riichi.score.han)
        assertEquals("満貫", riichi.score.title)
        assertEquals("満貫封頂", riichi.score.capReason)
        assertEquals(listOf("荣和：P0 支付 8000"), riichi.paymentLines)
        assertEquals(8000, riichi.totalPoints)
    }
}

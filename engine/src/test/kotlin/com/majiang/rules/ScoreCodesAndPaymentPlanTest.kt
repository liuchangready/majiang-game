package com.majiang.rules

import com.majiang.model.PlayerHand
import com.majiang.model.Suit
import com.majiang.model.Tile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 稳定役种码、封顶原因码与结构化支付计划的契约测试。
 *
 * 这些断言只覆盖 engine 内部数据契约；既有 yakuLines / paymentLines / title /
 * capReason / formulaText 字符串仍由 ScoreTextFormatterContractTest 逐字守卫。
 */
class ScoreCodesAndPaymentPlanTest {

    private val calculator = ScoreCalculator.INSTANCE
    private val evaluator = WinningScoreEvaluator()

    private val expectedYakuCodes = setOf(
        "RIICHI", "IPPATSU", "MENZEN_TSUMO", "TANYAO", "PINFU", "IIPEIKO",
        "YAKUHAI_HAKU", "YAKUHAI_HATSU", "YAKUHAI_CHUN", "YAKUHAI_BAKAZE", "YAKUHAI_JIKAZE",
        "HAITEI", "HOUTEI", "RINSHAN", "CHANKAN",
        "DOUBLE_RIICHI", "CHANTA", "ITTSU", "SAN_SHOKU_DOUJUN", "SAN_SHOKU_DOUKOU",
        "TOITOI", "SAN_ANKOU", "SAN_KANTSU", "SHOUSANGEN", "HONROUTOU", "CHIITOITSU",
        "HONITSU", "JUNCHAN", "RYANPEIKOU", "CHINITSU",
        "KOKUSHI", "SUUANKOU", "DAISANGEN", "SHOUSUUSHII", "DAISUUSHII", "TSUUIISOU",
        "RYUUIISOU", "CHINROUTOU", "CHURENPOUTOU", "SUUKANTSU", "TENHOU", "CHIIHOU"
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

    @Test
    fun `yaku codes are unique stable uppercase identifiers and legacy display names are unchanged`() {
        val actual = Yaku.values().map { it.code }
        assertEquals(expectedYakuCodes, actual.toSet(), "稳定役种码集合必须与契约一致")
        assertEquals(actual.size, actual.toSet().size, "稳定役种码不可重复")
        assertEquals(Yaku.values().size, expectedYakuCodes.size, "新增役种必须同时登记稳定码")
        assertEquals(
            Yaku.values().size,
            Yaku.values().map { it.displayName }.toSet().size,
            "旧展示名必须唯一，否则 legacy 反查会歧义"
        )
        for (yaku in Yaku.values()) {
            assertTrue(
                Regex("[A-Z][A-Z0-9_]*").matches(yaku.code),
                "稳定役种码必须是大写 ASCII 标识符: ${yaku.name} -> ${yaku.code}"
            )
        }

        // 关键展示名逐字锁定；枚举名或码调整不得改变既有显示。
        assertEquals("立直", Yaku.RIICHI.displayName)
        assertEquals("门前清自摸和", Yaku.MENZEN_TSUMO.displayName)
        assertEquals("役牌 白", Yaku.YAKUHAI_HAKU.displayName)
        assertEquals("两杯口", Yaku.RYANPEIKOU.displayName)
        assertEquals("四暗刻", Yaku.SUUANKOU.displayName)
        assertEquals("九莲宝灯", Yaku.CHURENPOUTOU.displayName)

        assertEquals(Yaku.RIICHI, Yaku.fromCode("RIICHI"))
        assertEquals(Yaku.SUUANKOU, Yaku.fromDisplayName("四暗刻"))
        assertNull(Yaku.fromCode("NO_SUCH_YAKU"))
        assertNull(Yaku.fromDisplayName("不存在的役"))
        assertTrue(Yaku.isKnownCode("CHINITSU"))
    }

    @Test
    fun `cap codes are stable and cover every non-yakuman limit plus yakuman`() {
        assertEquals(
            setOf("MANGAN", "HANEMAN", "BAIMAN", "SANBAIMAN", "KAZOE_YAKUMAN"),
            ScoreCap.values().map { it.code }.toSet()
        )

        assertNull(calculator.calculate(30, 2).capCode)
        assertEquals("MANGAN", calculator.calculate(30, 5).capCode)
        assertEquals("MANGAN", calculator.calculate(40, 4).capCode)
        assertEquals("MANGAN", calculator.calculate(70, 3).capCode)
        assertEquals("HANEMAN", calculator.calculate(30, 6).capCode)
        assertEquals("HANEMAN", calculator.calculate(30, 7).capCode)
        assertEquals("BAIMAN", calculator.calculate(30, 8).capCode)
        assertEquals("BAIMAN", calculator.calculate(30, 10).capCode)
        assertEquals("SANBAIMAN", calculator.calculate(30, 11).capCode)
        assertEquals("SANBAIMAN", calculator.calculate(30, 12).capCode)
        assertEquals("KAZOE_YAKUMAN", calculator.calculate(30, 13).capCode)
        assertEquals("KAZOE_YAKUMAN", calculator.calculate(30, 25).capCode)
        assertEquals(ScoreTextFormatter.YAKUMAN_CODE, calculator.calculate(30, 0, 1).capCode)
        assertEquals(ScoreTextFormatter.YAKUMAN_CODE, calculator.calculate(25, 0, 2).capCode)

        // 稳定码与既有 title/capReason 并存；字符串路径不受影响。
        val mangan = calculator.calculate(30, 5)
        assertEquals("満貫", mangan.title)
        assertEquals("満貫封頂", mangan.capReason)
        assertEquals("30符 × 2^(2+5) = 3840 → 満貫封頂 2000", mangan.formulaText)
    }

    @Test
    fun `payment plan amounts roles counts and totals equal the legacy payment functions`() {
        val scores = listOf(
            calculator.calculate(30, 1),
            calculator.calculate(30, 3),
            calculator.calculate(30, 5),
            calculator.calculate(30, 6),
            calculator.calculate(30, 8),
            calculator.calculate(30, 11),
            calculator.calculate(30, 13),
            calculator.calculate(30, 0, 1),
            calculator.calculate(30, 0, 2)
        )

        for (score in scores) {
            for (isDealer in listOf(true, false)) {
                for (isTsumo in listOf(true, false)) {
                    val plan = score.paymentPlan(isDealer = isDealer, isTsumo = isTsumo)
                    val label = "fu=${score.fu} han=${score.han} yakuman=${score.yakumanMultiplier} dealer=$isDealer tsumo=$isTsumo"
                    assertEquals(score.totalPoints(isDealer, isTsumo), plan.total, "$label total")
                    assertEquals(plan.components.sumOf { it.subtotal }, plan.total, "$label components")

                    when {
                        isTsumo && isDealer -> {
                            assertEquals(PaymentKind.DEALER_TSUMO, plan.kind, label)
                            assertEquals(1, plan.components.size, label)
                            val c = plan.components.single()
                            assertEquals(PayerRole.NON_DEALER, c.payerRole, label)
                            assertEquals(score.dealerTsumoPayment(), c.amount, label)
                            assertEquals(3, c.count, label)
                        }
                        isTsumo -> {
                            assertEquals(PaymentKind.NON_DEALER_TSUMO, plan.kind, label)
                            assertEquals(2, plan.components.size, label)
                            val (dealerPay, otherPay) = score.nonDealerTsumoPayment()
                            assertEquals(
                                PaymentComponent(PayerRole.DEALER, dealerPay, 1),
                                plan.components.first { it.payerRole == PayerRole.DEALER },
                                label
                            )
                            assertEquals(
                                PaymentComponent(PayerRole.NON_DEALER, otherPay, 2),
                                plan.components.first { it.payerRole == PayerRole.NON_DEALER },
                                label
                            )
                        }
                        isDealer -> {
                            assertEquals(PaymentKind.DEALER_RON, plan.kind, label)
                            val c = plan.components.single()
                            assertEquals(PayerRole.DISCARDER, c.payerRole, label)
                            assertEquals(score.dealerRonPayment(), c.amount, label)
                            assertEquals(1, c.count, label)
                        }
                        else -> {
                            assertEquals(PaymentKind.NON_DEALER_RON, plan.kind, label)
                            val c = plan.components.single()
                            assertEquals(PayerRole.DISCARDER, c.payerRole, label)
                            assertEquals(score.nonDealerRonPayment(), c.amount, label)
                            assertEquals(1, c.count, label)
                        }
                    }
                }
            }
        }
    }

    @Test
    fun `explain publishes authoritative yaku entries while keeping legacy yaku lines identical`() {
        val b = evaluator.explain(pinfu(), pinfuContext(), emptyList(), honba = 1, riichiSticks = 2, loser = 3)!!
        assertEquals(listOf("平和" to 1), b.yakuLines)
        assertEquals(listOf(YakuEntry("PINFU", "平和", 1)), b.yakuEntries)
        assertEquals(b.yakuLines.map { it.first }, b.yakuEntries.map { it.displayName })
        assertEquals(b.yakuLines.map { it.second }, b.yakuEntries.map { it.han })

        assertEquals(300, b.honbaAddition)
        assertEquals(2000, b.riichiAddition)
        assertEquals(PaymentKind.NON_DEALER_RON, b.paymentPlan.kind)
        assertEquals(1000, b.paymentPlan.total)
        assertEquals(listOf("荣和：P3 支付 1000"), b.paymentLines)
    }

    @Test
    fun `unknown legacy yaku display name keeps original text and gets a deterministic fallback code`() {
        val score = calculator.calculate(30, 1)
        val b = ScoreBreakdown(
            yakuLines = listOf("未登记役种" to 1),
            yakumanCount = 0,
            dora = DoraBreakdown(indicatorCount = 0, redCount = 0, totalHan = 0),
            fu = FuBreakdown(emptyList(), 30, 30, 30),
            score = score,
            paymentLines = listOf("荣和：放铳者 支付 1000"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 1000
        )
        assertEquals(
            listOf(YakuEntry(ScoreTextFormatter.UNKNOWN_YAKU_CODE, "未登记役种", 1)),
            b.yakuEntries
        )
        assertEquals(listOf("未登记役种" to 1), b.yakuLines)
    }

    @Test
    fun `snapshot isolates yaku entries and rejects mismatched copied lines`() {
        val b = evaluator.explain(pinfu(), pinfuContext(), emptyList(), loser = 3)!!
        val snapshot = com.majiang.event.snapshotScoreBreakdown(b)!!
        assertEquals(b.yakuEntries, snapshot.yakuEntries)
        assertThrows(UnsupportedOperationException::class.java) {
            (snapshot.yakuEntries as MutableList<*>).clear()
        }
        assertThrows(IllegalArgumentException::class.java) {
            b.copy(yakuLines = listOf("立直" to 1))
        }
    }
}

package com.majiang.presentation.common

import com.majiang.client.api.DoraBreakdownView
import com.majiang.client.api.FuBreakdownView
import com.majiang.client.api.PaymentComponentView
import com.majiang.client.api.PaymentPlanView
import com.majiang.client.api.ScoreBreakdownView
import com.majiang.client.api.ScorePaymentCodesView
import com.majiang.client.api.ScoreResultView
import com.majiang.client.api.YakuEntryView
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class StructuredSettlementDetailPresenterTest {

    private fun breakdown(
        yakuEntries: List<YakuEntryView> = listOf(
            YakuEntryView("PINFU", "平和(结构化)", 1),
            YakuEntryView("TANYAO", "断幺九(结构化)", 1)
        ),
        capCode: String? = "MANGAN",
        capReason: String? = "旧封顶文本",
        plan: PaymentPlanView? = PaymentPlanView(
            ScorePaymentCodesView.NON_DEALER_RON,
            listOf(PaymentComponentView(ScorePaymentCodesView.DISCARDER, 1234, 1)),
            1234
        )
    ): ScoreBreakdownView = ScoreBreakdownView(
        yakuLines = listOf("平和" to 1, "断幺九" to 1),
        yakumanCount = 0,
        dora = DoraBreakdownView(
            indicatorCount = 0, uraCount = 0, redCount = 0, totalHan = 0,
            indicatorTiles = emptyList(), uraTiles = emptyList(), redTiles = emptyList(),
            indicatorHits = emptyList(), uraHits = emptyList()
        ),
        fu = FuBreakdownView(emptyList(), 30, 30, 30),
        score = ScoreResultView(
            fu = 30, han = 2, basePoints = 2000, title = "満貫",
            isYakuman = false, yakumanMultiplier = 0, rawBasePoints = 2000,
            capReason = capReason, formulaText = "30符", rawBasePointsExact = "2000",
            capCode = capCode
        ),
        paymentLines = listOf("旧支付行：放铳者 支付 2000"),
        honbaAddition = 300,
        riichiAddition = 1000,
        totalPoints = 1234 + 300 + 1000,
        uraEligible = false,
        isDealer = false,
        isTsumo = false,
        yakuEntries = yakuEntries,
        paymentPlan = plan
    )

    @Test
    fun `structured yaku cap and plan are preferred over legacy text`() {
        val b = breakdown()
        val yaku = SettlementDetailPresenter.rows(b, SettlementSection.YAKU).joinToString("\n") { it.text }
        assertTrue(yaku.contains("平和(结构化) 1 番"), yaku)
        assertFalse(yaku.contains("平和 1 番\n"), yaku)

        val calc = SettlementDetailPresenter.rows(b, SettlementSection.CALC)
        val calcText = calc.joinToString("\n") { it.text }
        assertTrue(calcText.contains("満貫封頂"), "known capCode must be localized from code: $calcText")
        assertFalse(calcText.contains("旧封顶文本"), calcText)
        assertTrue(calcText.contains("1234"), "payment amount must come from paymentPlan: $calcText")
        assertFalse(calcText.contains("2000 点 → 向上取整至百位：2000"), calcText)
        assertTrue(calcText.contains("基础支付分配：放铳者 支付 1234 点 (计划合计 1234 点)"), calcText)
        assertTrue(calcText.contains("最终结算总得点：1234 点"), calcText)
        assertTrue(calc.any { it.text.contains("基础支付分配") && it.text.contains("1234") })
    }

    @Test
    fun `missing or unknown structured values fall back to legacy text`() {
        val b = breakdown(yakuEntries = emptyList(), capCode = null, plan = null)
        val yaku = SettlementDetailPresenter.rows(b, SettlementSection.YAKU).joinToString("\n") { it.text }
        assertTrue(yaku.contains("平和 1 番"), yaku)

        val calc = SettlementDetailPresenter.rows(b, SettlementSection.CALC).joinToString("\n") { it.text }
        assertTrue(calc.contains("旧封顶文本"), calc)
        assertTrue(calc.contains("旧支付行：放铳者 支付 2000"), calc)
        assertTrue(calc.contains("2000 × 4 = 8000 点 → 向上取整至百位：8000 点"), calc)
    }

    @Test
    fun `yakuman structured plan drives fixed payment rows`() {
        val plan = PaymentPlanView(
            ScorePaymentCodesView.NON_DEALER_TSUMO,
            listOf(
                PaymentComponentView(ScorePaymentCodesView.DEALER, 16000, 1),
                PaymentComponentView(ScorePaymentCodesView.NON_DEALER, 8000, 2)
            ),
            32000
        )
        val b = breakdown(
            yakuEntries = listOf(YakuEntryView("DAISANGEN", "大三元", 0)),
            capCode = "YAKUMAN",
            plan = plan
        ).copy(
            yakumanCount = 1,
            score = bScoreYakuman(),
            isTsumo = true,
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 32000
        )
        val calc = SettlementDetailPresenter.rows(b, SettlementSection.CALC).joinToString("\n") { it.text }
        assertTrue(calc.contains("庄家付 基本点×2 = 16000 点"), calc)
        assertTrue(calc.contains("两闲家各付 基本点×1 = 8000 点"), calc)
        assertTrue(calc.contains("基础支付合计：庄家 16000 点 + 闲家 8000 × 2 点 = 32000 点"), calc)
        assertTrue(calc.contains("计划合计 32000 点"), calc)
    }

    @Test
    fun `all four payment roles consume structured plan amounts`() {
        data class Case(
            val dealer: Boolean,
            val tsumo: Boolean,
            val plan: PaymentPlanView
        )
        val cases = listOf(
            Case(
                dealer = true, tsumo = true,
                plan = PaymentPlanView(
                    ScorePaymentCodesView.DEALER_TSUMO,
                    listOf(PaymentComponentView(ScorePaymentCodesView.NON_DEALER, 4000, 3)),
                    12000
                )
            ),
            Case(
                dealer = false, tsumo = true,
                plan = PaymentPlanView(
                    ScorePaymentCodesView.NON_DEALER_TSUMO,
                    listOf(
                        PaymentComponentView(ScorePaymentCodesView.DEALER, 4000, 1),
                        PaymentComponentView(ScorePaymentCodesView.NON_DEALER, 2000, 2)
                    ),
                    8000
                )
            ),
            Case(
                dealer = true, tsumo = false,
                plan = PaymentPlanView(
                    ScorePaymentCodesView.DEALER_RON,
                    listOf(PaymentComponentView(ScorePaymentCodesView.DISCARDER, 12000, 1)),
                    12000
                )
            ),
            Case(
                dealer = false, tsumo = false,
                plan = PaymentPlanView(
                    ScorePaymentCodesView.NON_DEALER_RON,
                    listOf(PaymentComponentView(ScorePaymentCodesView.DISCARDER, 8000, 1)),
                    8000
                )
            )
        )
        for (case in cases) {
            val b = breakdown(plan = case.plan).copy(
                isDealer = case.dealer,
                isTsumo = case.tsumo,
                totalPoints = case.plan.total
            )
            val text = SettlementDetailPresenter.rows(b, SettlementSection.CALC).joinToString("\n") { it.text }
            for (component in case.plan.components) {
                assertTrue(text.contains(component.amount.toString()), "dealer=${case.dealer} tsumo=${case.tsumo}: $text")
            }
            assertTrue(text.contains("计划合计 ${case.plan.total} 点"), text)
        }
    }

    private fun bScoreYakuman() = ScoreResultView(
        fu = 30, han = 0, basePoints = 8000, title = "役満",
        isYakuman = true, yakumanMultiplier = 1, rawBasePoints = 8000,
        capReason = "役満", formulaText = "役满", rawBasePointsExact = "8000",
        capCode = "YAKUMAN"
    )
}

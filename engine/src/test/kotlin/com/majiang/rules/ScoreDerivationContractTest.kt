package com.majiang.rules

import com.majiang.settlement.SettlementService
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ScoreDerivationContractTest {
    private val scoreCalc = ScoreCalculator.INSTANCE
    private val settlementService = SettlementService.INSTANCE

    @Test
    fun `rawBasePoints matches exact formula across all tiers`() {
        // 40符 4翻: raw = 40 * 2^6 = 2560
        val r40_4 = scoreCalc.calculate(40, 4)
        assertEquals(2560, r40_4.rawBasePoints)
        assertEquals(2000, r40_4.basePoints)
        assertEquals("満貫封頂", r40_4.capReason)
        assertEquals("40符 × 2^(2+4) = 2560 → 満貫封頂 2000", r40_4.formulaText)

        // 30符 2翻: raw = 30 * 2^4 = 480 (未封顶)
        val r30_2 = scoreCalc.calculate(30, 2)
        assertEquals(480, r30_2.rawBasePoints)
        assertEquals(480, r30_2.basePoints)
        assertNull(r30_2.capReason)
        assertEquals("30符 × 2^(2+2) = 480", r30_2.formulaText)
    }

    @Test
    fun `cap reasons correctly label all mangan, haneman, baiman, sanbaiman, kazoe and yakuman`() {
        // 5翻 (满贯)
        val mangan = scoreCalc.calculate(30, 5)
        assertEquals(2000, mangan.basePoints)
        assertEquals("満貫封頂", mangan.capReason)

        // 3翻 70符 (满贯)
        val mangan3_70 = scoreCalc.calculate(70, 3)
        assertEquals(2000, mangan3_70.basePoints)
        assertEquals("満貫封頂", mangan3_70.capReason)

        // 6翻 (跳满)
        val haneman = scoreCalc.calculate(30, 6)
        assertEquals(3000, haneman.basePoints)
        assertEquals("跳満", haneman.capReason)
        assertEquals("30符 × 2^(2+6) = 7680 → 跳満 3000", haneman.formulaText)

        // 7翻 (跳满)
        val haneman7 = scoreCalc.calculate(30, 7)
        assertEquals(3000, haneman7.basePoints)
        assertEquals("跳満", haneman7.capReason)

        // 8翻 (倍满)
        val baiman8 = scoreCalc.calculate(30, 8)
        assertEquals(4000, baiman8.basePoints)
        assertEquals("倍満", baiman8.capReason)
        assertEquals("30符 × 2^(2+8) = 30720 → 倍満 4000", baiman8.formulaText)

        // 10翻 (倍满)
        val baiman10 = scoreCalc.calculate(30, 10)
        assertEquals(4000, baiman10.basePoints)
        assertEquals("倍満", baiman10.capReason)

        // 11翻 (三倍满)
        val sanbaiman11 = scoreCalc.calculate(30, 11)
        assertEquals(6000, sanbaiman11.basePoints)
        assertEquals("三倍満", sanbaiman11.capReason)
        assertEquals("30符 × 2^(2+11) = 245760 → 三倍満 6000", sanbaiman11.formulaText)

        // 13翻 (数え役满)
        val kazoe = scoreCalc.calculate(30, 13)
        assertEquals(8000, kazoe.basePoints)
        assertEquals("数え役満", kazoe.capReason)
        assertEquals("30符 × 2^(2+13) = 983040 → 数え役満 8000", kazoe.formulaText)

        // 役满 (1倍)
        val yakuman1 = scoreCalc.calculate(0, 0, yakumanCount = 1)
        assertEquals(8000, yakuman1.basePoints)
        assertEquals("役満", yakuman1.capReason)
        assertEquals("役満 基本点 8000", yakuman1.formulaText)

        // 双倍役满 (2倍)
        val yakuman2 = scoreCalc.calculate(0, 0, yakumanCount = 2)
        assertEquals(16000, yakuman2.basePoints)
        assertEquals("ダブル役満", yakuman2.capReason)
        assertEquals("ダブル役満 基本点 16000", yakuman2.formulaText)
    }

    @Test
    fun `paymentLines is strictly consistent with ScoreResult payment functions`() {
        val score = scoreCalc.calculate(30, 3)

        // 1. 庄家自摸
        val dealerTsumoLines = settlementService.formatPaymentLines(score, isDealer = true, isTsumo = true)
        val expectedDealerTsumoPay = score.dealerTsumoPayment()
        assertEquals(listOf("自摸：闲家各付 $expectedDealerTsumoPay"), dealerTsumoLines)

        // 2. 闲家自摸
        val nonDealerTsumoLines = settlementService.formatPaymentLines(score, isDealer = false, isTsumo = true)
        val (expDealerPay, expOtherPay) = score.nonDealerTsumoPayment()
        assertEquals(listOf("自摸：庄家付 $expDealerPay、闲家各付 $expOtherPay"), nonDealerTsumoLines)

        // 3. 庄家荣和
        val dealerRonLines = settlementService.formatPaymentLines(score, isDealer = true, isTsumo = false, loser = 2)
        val expDealerRonPay = score.dealerRonPayment()
        assertEquals(listOf("荣和：P2 支付 $expDealerRonPay"), dealerRonLines)

        // 4. 闲家荣和
        val nonDealerRonLines = settlementService.formatPaymentLines(score, isDealer = false, isTsumo = false, loser = 1)
        val expNonDealerRonPay = score.nonDealerRonPayment()
        assertEquals(listOf("荣和：P1 支付 $expNonDealerRonPay"), nonDealerRonLines)
    }
}

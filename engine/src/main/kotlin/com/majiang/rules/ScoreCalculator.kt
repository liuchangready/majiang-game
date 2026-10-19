package com.majiang.rules

import java.math.BigInteger

/**
 * 日麻计分器
 *
 * 点数计算公式：
 * - 基本点 = 符 × 2^(2+翻)
 * - 满贯封顶规则：
 *   - 3翻70符 / 4翻40符 以上 → 满贯 (基本点 2000)
 *   - 6~7翻 → 跳满 (基本点 3000)
 *   - 8~10翻 → 倍满 (基本点 4000)
 *   - 11~12翻 → 三倍满 (基本点 6000)
 *   - 13翻以上 → 累计役满 (基本点 8000)
 *   - 役满 → 基本点 8000 × N
 *
 * 支付规则（基于基本点）：
 * - 自摸：
 *   - 庄家：每位闲家付 基本点×2（向上取整到百位）
 *   - 闲家：庄家付 基本点×2，另两闲家各付 基本点×1（向上取整到百位）
 * - 荣和：
 *   - 庄家：放铳者付 基本点×6
 *   - 闲家：放铳者付 基本点×4
 */
class ScoreCalculator {

    /**
     * 计分结果
     */
    data class ScoreResult(
        val fu: Int,
        val han: Int,
        val basePoints: Int,
        val title: String,
        val isYakuman: Boolean,
        val yakumanMultiplier: Int,
        val rawBasePoints: Int = basePoints,
        val capReason: String? = null,
        val formulaText: String = "",
        val rawBasePointsExact: String = rawBasePoints.toString(),
        /**
         * 稳定封顶原因码：未封顶为 null，役满为 `YAKUMAN`，其余见 [ScoreCap.code]。
         * 仅供 engine 领域与未来结构化线路使用，当前 MJ-TCP/1 不序列化。
         */
        val capCode: String? = null
    ) {
        /**
         * 计算庄家自摸时每位闲家应付的点数
         */
        fun dealerTsumoPayment(): Int = roundUpTo100(basePoints * 2)

        /**
         * 计算闲家自摸时：
         * @return Pair(庄家付的点数, 每位闲家付的点数)
         */
        fun nonDealerTsumoPayment(): Pair<Int, Int> =
            Pair(roundUpTo100(basePoints * 2), roundUpTo100(basePoints * 1))

        /**
         * 计算庄家荣和时放铳者应付的点数
         */
        fun dealerRonPayment(): Int = roundUpTo100(basePoints * 6)

        /**
         * 计算闲家荣和时放铳者应付的点数
         */
        fun nonDealerRonPayment(): Int = roundUpTo100(basePoints * 4)

        /**
         * 基础支付结构（不含本场棒与供托立直棒）
         *
         * 金额完全复用四个既有支付函数，不在这里另写乘数或取整规则。
         */
        fun paymentPlan(isDealer: Boolean, isTsumo: Boolean): PaymentPlan = when {
            isTsumo && isDealer -> {
                val each = dealerTsumoPayment()
                PaymentPlan(
                    kind = PaymentKind.DEALER_TSUMO,
                    components = listOf(PaymentComponent(PayerRole.NON_DEALER, each, 3)),
                    total = each * 3
                )
            }
            isTsumo -> {
                val (dealerPay, otherPay) = nonDealerTsumoPayment()
                PaymentPlan(
                    kind = PaymentKind.NON_DEALER_TSUMO,
                    components = listOf(
                        PaymentComponent(PayerRole.DEALER, dealerPay, 1),
                        PaymentComponent(PayerRole.NON_DEALER, otherPay, 2)
                    ),
                    total = dealerPay + otherPay * 2
                )
            }
            isDealer -> {
                val pay = dealerRonPayment()
                PaymentPlan(
                    kind = PaymentKind.DEALER_RON,
                    components = listOf(PaymentComponent(PayerRole.DISCARDER, pay, 1)),
                    total = pay
                )
            }
            else -> {
                val pay = nonDealerRonPayment()
                PaymentPlan(
                    kind = PaymentKind.NON_DEALER_RON,
                    components = listOf(PaymentComponent(PayerRole.DISCARDER, pay, 1)),
                    total = pay
                )
            }
        }

        /**
         * 获取总得点（含本场棒加算前）
         *
         * 与 [paymentPlan] 的 total 使用同一结构，避免两套支付汇总规则漂移。
         */
        fun totalPoints(isDealer: Boolean, isTsumo: Boolean): Int =
            paymentPlan(isDealer = isDealer, isTsumo = isTsumo).total
    }

    /**
     * 根据符数和翻数计算得点
     *
     * @param fu 符数
     * @param han 翻数（非役满时）
     * @param yakumanCount 役满倍数（0 = 非役满）
     */
    fun calculate(fu: Int, han: Int, yakumanCount: Int = 0): ScoreResult {
        if (yakumanCount > 0) {
            val basePoints = 8000 * yakumanCount
            val text = ScoreTextFormatter.yakumanText(yakumanCount, basePoints)
            return ScoreResult(
                fu = fu,
                han = 0,
                basePoints = basePoints,
                title = text.title,
                isYakuman = true,
                yakumanMultiplier = yakumanCount,
                rawBasePoints = basePoints,
                capReason = text.capReason,
                formulaText = text.formulaText,
                rawBasePointsExact = basePoints.toString(),
                capCode = text.capCode
            )
        }

        val rawExact = BigInteger.valueOf(fu.toLong()).multiply(BigInteger.valueOf(2).pow(2 + han))
        val rawBasePointsExact = rawExact.toString()
        val maxInt = BigInteger.valueOf(Int.MAX_VALUE.toLong())
        // 旧 rawBasePoints:Int 保持类型和小数值语义，超界可饱和 Int.MAX_VALUE 并注释，不用于公式展示/支付计算
        val rawBasePoints: Int = if (rawExact <= maxInt) rawExact.toInt() else Int.MAX_VALUE
        val basePoints: Int
        val cap: ScoreCap?

        when {
            han >= 13 -> {
                basePoints = 8000
                cap = ScoreCap.KAZOE_YAKUMAN
            }
            han >= 11 -> {
                basePoints = 6000
                cap = ScoreCap.SANBAIMAN
            }
            han >= 8 -> {
                basePoints = 4000
                cap = ScoreCap.BAIMAN
            }
            han >= 6 -> {
                basePoints = 3000
                cap = ScoreCap.HANEMAN
            }
            han == 5 || (han == 4 && fu >= 40) || (han == 3 && fu >= 70) -> {
                basePoints = 2000
                cap = ScoreCap.MANGAN
            }
            else -> {
                // 基本点 = 符 × 2^(2+翻)
                // 封顶满贯
                if (rawExact > BigInteger.valueOf(2000)) {
                    basePoints = 2000
                    cap = ScoreCap.MANGAN
                } else {
                    basePoints = rawBasePoints
                    cap = null
                }
            }
        }

        val text = ScoreTextFormatter.normalText(fu, han, rawBasePointsExact, cap, basePoints)

        return ScoreResult(
            fu = fu,
            han = han,
            basePoints = basePoints,
            title = text.title,
            isYakuman = false,
            yakumanMultiplier = 0,
            rawBasePoints = rawBasePoints,
            capReason = text.capReason,
            formulaText = text.formulaText,
            rawBasePointsExact = rawBasePointsExact,
            capCode = text.capCode
        )
    }

    companion object {
        /** 向上取整到百位 */
        fun roundUpTo100(value: Int): Int =
            ((value + 99) / 100) * 100

        /** 单例 */
        val INSTANCE = ScoreCalculator()
    }
}


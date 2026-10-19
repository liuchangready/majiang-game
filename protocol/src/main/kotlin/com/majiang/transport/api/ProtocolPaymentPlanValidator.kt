package com.majiang.transport.api

/**
 * D-07 v5 结构化支付计划的跨字段一致性校验。
 *
 * 这里不信任 `paymentPlan` 自身：必须同时与权威旧字段
 * `basePoints`、`isDealer`、`isTsumo`、`honbaAddition`、`riichiAddition`、
 * `totalPoints` 以及 `paymentLines` 对齐。任何不一致都只降级 `paymentPlan`，
 * 由消费方回退 `paymentLines`，不拒绝整条结算快照。
 *
 * 冻结的四种形态：
 * - `DEALER_TSUMO`：庄家自摸，`NON_DEALER` × 3，每家 `round100(base×2)`
 * - `NON_DEALER_TSUMO`：闲家自摸，`DEALER` × 1（`round100(base×2)`）+
 *   `NON_DEALER` × 2（`round100(base×1)`）
 * - `DEALER_RON`：庄家荣和，`DISCARDER` × 1，`round100(base×6)`
 * - `NON_DEALER_RON`：闲家荣和，`DISCARDER` × 1，`round100(base×4)`
 *
 * `totalPoints` 必须等于 `paymentPlan.total + honbaAddition + riichiAddition`；
 * 基础支付计划本身不含本场棒与供托立直棒。
 */
object ProtocolPaymentPlanValidator {

    private val DEALER_TSUMO_LINE = Regex("""^自摸：闲家各付 (\d+)$""")
    private val NON_DEALER_TSUMO_LINE = Regex("""^自摸：庄家付 (\d+)、闲家各付 (\d+)$""")
    private val RON_LINE = Regex("""^荣和：(?:P\d+|放铳者) 支付 (\d+)$""")

    private data class GroupSpec(val count: Int, val amount: Long)

    /**
     * 校验已知 kind 的计划与所有权威字段一致。
     *
     * 未知 `kind`/`payerRole` 不在这里判定为“损坏”：是否可用由调用方决定回退。
     * 因此本方法对未知 kind 返回 false（不可消费），但解析器不会因此拒绝快照。
     */
    fun isConsistent(
        plan: ProtocolPaymentPlan,
        score: ProtocolScoreResult,
        paymentLines: List<String>,
        honbaAddition: Int,
        riichiAddition: Int,
        totalPoints: Int,
        isDealer: Boolean,
        isTsumo: Boolean
    ): Boolean {
        if (!plan.isStructurallyConsistent()) return false
        if (honbaAddition < 0 || riichiAddition < 0) return false
        if (score.basePoints < 0) return false

        val kind = plan.kind
        if (!ProtocolScoreCodes.isKnownPaymentKind(kind)) return false

        val base = score.basePoints.toLong()
        val expectedGroups: Map<String, GroupSpec>
        val expectedDealer: Boolean
        val expectedTsumo: Boolean
        when (kind) {
            ProtocolScoreCodes.PAYMENT_KIND_DEALER_TSUMO -> {
                expectedGroups = mapOf(
                    ProtocolScoreCodes.PAYER_ROLE_NON_DEALER to
                        GroupSpec(3, roundUp100(base * 2L))
                )
                expectedDealer = true
                expectedTsumo = true
            }
            ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_TSUMO -> {
                expectedGroups = mapOf(
                    ProtocolScoreCodes.PAYER_ROLE_DEALER to
                        GroupSpec(1, roundUp100(base * 2L)),
                    ProtocolScoreCodes.PAYER_ROLE_NON_DEALER to
                        GroupSpec(2, roundUp100(base * 1L))
                )
                expectedDealer = false
                expectedTsumo = true
            }
            ProtocolScoreCodes.PAYMENT_KIND_DEALER_RON -> {
                expectedGroups = mapOf(
                    ProtocolScoreCodes.PAYER_ROLE_DISCARDER to
                        GroupSpec(1, roundUp100(base * 6L))
                )
                expectedDealer = true
                expectedTsumo = false
            }
            ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_RON -> {
                expectedGroups = mapOf(
                    ProtocolScoreCodes.PAYER_ROLE_DISCARDER to
                        GroupSpec(1, roundUp100(base * 4L))
                )
                expectedDealer = false
                expectedTsumo = false
            }
            else -> return false
        }
        if (isDealer != expectedDealer || isTsumo != expectedTsumo) return false

        if (plan.components.size != expectedGroups.size) return false
        val seen = HashSet<String>(expectedGroups.size)
        for (component in plan.components) {
            if (!ProtocolScoreCodes.isKnownPayerRole(component.payerRole)) return false
            val expected = expectedGroups[component.payerRole] ?: return false
            if (!seen.add(component.payerRole)) return false
            if (component.count != expected.count) return false
            if (component.amount.toLong() != expected.amount) return false
        }
        if (seen.size != expectedGroups.size) return false

        val totalWithAdditions = plan.total.toLong() +
            honbaAddition.toLong() + riichiAddition.toLong()
        if (totalWithAdditions != totalPoints.toLong()) return false

        return paymentLinesMatch(kind, paymentLines, expectedGroups)
    }

    /**
     * 旧 `paymentLines` 是权威回退文本；v5 的四种冻结格式必须与计划金额一致。
     * 任何额外行、缺失行、不同金额或不同付款方文本都不一致。
     */
    private fun paymentLinesMatch(
        kind: String,
        paymentLines: List<String>,
        expectedGroups: Map<String, GroupSpec>
    ): Boolean {
        if (paymentLines.size != 1) return false
        val line = paymentLines[0]
        return when (kind) {
            ProtocolScoreCodes.PAYMENT_KIND_DEALER_TSUMO -> {
                val match = DEALER_TSUMO_LINE.matchEntire(line) ?: return false
                val amount = match.groupValues[1].toLongOrNull() ?: return false
                amount == expectedGroups.getValue(ProtocolScoreCodes.PAYER_ROLE_NON_DEALER).amount
            }
            ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_TSUMO -> {
                val match = NON_DEALER_TSUMO_LINE.matchEntire(line) ?: return false
                val dealerAmount = match.groupValues[1].toLongOrNull() ?: return false
                val nonDealerAmount = match.groupValues[2].toLongOrNull() ?: return false
                dealerAmount == expectedGroups.getValue(ProtocolScoreCodes.PAYER_ROLE_DEALER).amount &&
                    nonDealerAmount == expectedGroups.getValue(ProtocolScoreCodes.PAYER_ROLE_NON_DEALER).amount
            }
            ProtocolScoreCodes.PAYMENT_KIND_DEALER_RON,
            ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_RON -> {
                val match = RON_LINE.matchEntire(line) ?: return false
                val amount = match.groupValues[1].toLongOrNull() ?: return false
                amount == expectedGroups.getValue(ProtocolScoreCodes.PAYER_ROLE_DISCARDER).amount
            }
            else -> false
        }
    }

    private fun roundUp100(value: Long): Long =
        if (value < 0L) Long.MIN_VALUE else ((value + 99L) / 100L) * 100L
}

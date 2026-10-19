package com.majiang.rules

/**
 * 结构化役种行（D-07 数据/文案分离）
 *
 * @property code 稳定役种码，见 [Yaku.code]；未知旧文案使用 [UNKNOWN_YAKU_CODE]
 * @property displayName 既有展示名，保持与重构前逐字一致
 * @property han 该役种实际计番（役满条目可为 0）
 */
data class YakuEntry(
    val code: String,
    val displayName: String,
    val han: Int
) {
    init {
        require(code.isNotBlank()) { "YakuEntry.code cannot be blank" }
        require(displayName.isNotBlank()) { "YakuEntry.displayName cannot be blank" }
        require(han >= 0) { "YakuEntry.han cannot be negative: $han" }
    }
}

/**
 * 支付场景稳定码
 *
 * 与 [ScoreCalculator.ScoreResult] 的四个支付函数一一对应。该码只描述支付结构，
 * 不参与当前 MJ-TCP/1 线路序列化。
 */
enum class PaymentKind(val code: String) {
    /** 庄家自摸：三家闲家各付 */
    DEALER_TSUMO("DEALER_TSUMO"),

    /** 闲家自摸：庄家与两闲家分别支付 */
    NON_DEALER_TSUMO("NON_DEALER_TSUMO"),

    /** 庄家荣和：放铳者独付 */
    DEALER_RON("DEALER_RON"),

    /** 闲家荣和：放铳者独付 */
    NON_DEALER_RON("NON_DEALER_RON")
}

/**
 * 付款方角色稳定码
 *
 * 座位号只在荣和且已知放铳者时作为展示上下文使用；基础支付结构只依赖角色与人数。
 */
enum class PayerRole(val code: String) {
    DEALER("DEALER"),
    NON_DEALER("NON_DEALER"),
    DISCARDER("DISCARDER")
}

/**
 * 单类付款方应付金额
 *
 * @property payerRole 付款方角色
 * @property amount 每个付款方应付点数（已按百位向上取整）
 * @property count 该角色付款人数；合计 = amount × count
 */
data class PaymentComponent(
    val payerRole: PayerRole,
    val amount: Int,
    val count: Int
) {
    init {
        require(amount >= 0) { "PaymentComponent.amount cannot be negative: $amount" }
        require(count >= 1) { "PaymentComponent.count must be >= 1: $count" }
    }

    val subtotal: Int get() = amount * count
}

/**
 * 基础支付结构
 *
 * 仅包含 [ScoreCalculator.ScoreResult] 的基础支付（不含本场棒与供托立直棒）；
 * 本场棒/立直棒继续由 ScoreBreakdown 的 honbaAddition / riichiAddition 数值字段承载。
 */
data class PaymentPlan(
    val kind: PaymentKind,
    val components: List<PaymentComponent>,
    val total: Int
) {
    init {
        require(components.isNotEmpty()) { "PaymentPlan.components cannot be empty" }
        require(total >= 0) { "PaymentPlan.total cannot be negative: $total" }
        require(total == components.sumOf { it.subtotal }) {
            "PaymentPlan.total $total must equal component subtotals ${components.sumOf { it.subtotal }}"
        }
    }
}

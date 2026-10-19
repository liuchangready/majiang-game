package com.majiang.rules

/**
 * 算分封顶档位
 *
 * 该枚举承载 [ScoreCalculator] 已判定的封顶档位，交给 [ScoreTextFormatter] 渲染
 * title / capReason / formulaText，同时提供稳定封顶原因码 [code]。
 *
 * [code] 是与展示文案解耦的稳定码，供未来结构化线路字段使用；当前 MJ-TCP/1
 * 协议 DTO、client 视图与 presentation 仍只消费格式化后的字符串字段，本枚举
 * 不参与序列化，也不改变任何数值与支付结果。
 */
enum class ScoreCap(val code: String) {
    /** 满贯：5 翻、4 翻 40 符、3 翻 70 符，以及基本点超过 2000 的封顶 */
    MANGAN("MANGAN"),

    /** 跳满：6~7 翻 */
    HANEMAN("HANEMAN"),

    /** 倍满：8~10 翻 */
    BAIMAN("BAIMAN"),

    /** 三倍满：11~12 翻 */
    SANBAIMAN("SANBAIMAN"),

    /** 累计役满：13 翻以上 */
    KAZOE_YAKUMAN("KAZOE_YAKUMAN")
}

/**
 * 算分文案三元组 + 稳定封顶码
 *
 * title / capReason / formulaText 与 [ScoreCalculator.ScoreResult] 的既有字段一一对应。
 * capCode 为附加的稳定码：未封顶为 null，役满为 [ScoreTextFormatter.YAKUMAN_CODE]，
 * 其余为 [ScoreCap.code]。该字段不进入当前线路。
 */
data class ScoreText(
    val title: String,
    val capReason: String?,
    val formulaText: String,
    val capCode: String? = null
)

/**
 * 算分文案格式化器（无状态纯组件）
 *
 * 职责边界（D-07）：
 * - 只把已确定的计分事实（封顶档位、符/翻、基本点、和牌上下文）渲染为既有展示文本；
 * - 不做点数计算、不做支付取整、不判定役种、不持有任何跨调用状态；
 * - 支付行直接读取 [ScoreCalculator.ScoreResult.paymentPlan] 的数值结构，避免展示层
 *   另写一套乘数与取整规则；
 * - 输出必须与提取前的 ScoreCalculator / SettlementService.formatPaymentLines 逐字一致，
 *   protocol DTO、client 视图与 presentation 的显示路径继续消费同样的字符串字段。
 *
 * 稳定码（[Yaku.code]、[ScoreCap.code]、[PaymentKind.code]、[PayerRole.code]）在当前
 * 版本只进入 engine 领域模型与契约测试；engine → protocol → client → presentation 的
 * 结构化字段迁移受严格未知字段解析与 TCP businessVersion 约束，需独立协议立项。
 */
object ScoreTextFormatter {

    /** 役满的稳定封顶原因码；具体倍数由 yakumanMultiplier 承载。 */
    const val YAKUMAN_CODE: String = "YAKUMAN"

    /** 未知旧展示名反查不到 [Yaku] 时使用的稳定占位码；展示仍保留原字符串。 */
    const val UNKNOWN_YAKU_CODE: String = "UNKNOWN_YAKU"

    /** 役满标题：1 倍「役満」、2 倍「ダブル役満」、其余为 N 倍役满 */
    fun yakumanTitle(yakumanCount: Int): String = when (yakumanCount) {
        1 -> "役満"
        2 -> "ダブル役満"
        else -> "${yakumanCount}倍役満"
    }

    /** 役满完整文案：capReason 与 title 相同，公式只展示基本点 */
    fun yakumanText(yakumanCount: Int, basePoints: Int): ScoreText {
        val title = yakumanTitle(yakumanCount)
        return ScoreText(
            title = title,
            capReason = title,
            formulaText = "$title 基本点 $basePoints",
            capCode = YAKUMAN_CODE
        )
    }

    /** 封顶档位对应的 title */
    fun capTitle(cap: ScoreCap): String = when (cap) {
        ScoreCap.MANGAN -> "満貫"
        ScoreCap.HANEMAN -> "跳満"
        ScoreCap.BAIMAN -> "倍満"
        ScoreCap.SANBAIMAN -> "三倍満"
        ScoreCap.KAZOE_YAKUMAN -> "数え役満"
    }

    /** 封顶档位对应的 capReason（仅满贯额外标注「封頂」） */
    fun capReason(cap: ScoreCap): String = when (cap) {
        ScoreCap.MANGAN -> "満貫封頂"
        ScoreCap.HANEMAN -> "跳満"
        ScoreCap.BAIMAN -> "倍満"
        ScoreCap.SANBAIMAN -> "三倍満"
        ScoreCap.KAZOE_YAKUMAN -> "数え役満"
    }

    /**
     * 非役满的文案
     *
     * @param fu 符数
     * @param han 翻数
     * @param rawBasePointsExact 封顶前的精确基本点（字符串，避免大数溢出）
     * @param cap 封顶档位；null 表示未封顶，title 退回「N翻M符」
     * @param basePoints 封顶后的实际基本点，仅用于封顶公式尾部展示
     */
    fun normalText(
        fu: Int,
        han: Int,
        rawBasePointsExact: String,
        cap: ScoreCap?,
        basePoints: Int
    ): ScoreText {
        val formulaBase = "${fu}符 × 2^(2+${han}) = $rawBasePointsExact"
        return if (cap == null) {
            ScoreText(
                title = "${han}翻${fu}符",
                capReason = null,
                formulaText = formulaBase,
                capCode = null
            )
        } else {
            val reason = capReason(cap)
            ScoreText(
                title = capTitle(cap),
                capReason = reason,
                formulaText = "$formulaBase → $reason $basePoints",
                capCode = cap.code
            )
        }
    }

    /** 结构化役种行：保留稳定码、既有展示名与计番。 */
    fun yakuEntries(yakuList: List<Pair<Yaku, Int>>): List<YakuEntry> =
        yakuList.map { (yaku, han) -> YakuEntry(code = yaku.code, displayName = yaku.displayName, han = han) }

    /** 兼容旧字符串路径：按稳定展示名反查稳定码；未知文案保留原文并使用 UNKNOWN_YAKU。 */
    fun yakuEntriesFromLegacy(yakuLines: List<Pair<String, Int>>): List<YakuEntry> =
        yakuLines.map { (displayName, han) ->
            val yaku = Yaku.fromDisplayName(displayName)
            YakuEntry(
                code = yaku?.code ?: UNKNOWN_YAKU_CODE,
                displayName = displayName,
                han = han
            )
        }

    /** 役种展示行：把役种枚举渲染为既有协议的 (役名, 翻数) 文本行 */
    fun yakuLines(yakuList: List<Pair<Yaku, Int>>): List<Pair<String, Int>> =
        yakuEntries(yakuList).map { Pair(it.displayName, it.han) }

    /**
     * 支付分配说明行
     *
     * 金额与人数直接来自 [ScoreCalculator.ScoreResult.paymentPlan]，与四个支付函数
     * dealerTsumoPayment / nonDealerTsumoPayment / dealerRonPayment / nonDealerRonPayment 完全一致。
     *
     * @param loser 放铳玩家座位；为 null 时使用「放铳者」占位
     */
    fun paymentLines(
        score: ScoreCalculator.ScoreResult,
        isDealer: Boolean,
        isTsumo: Boolean,
        loser: Int? = null
    ): List<String> {
        val plan = score.paymentPlan(isDealer = isDealer, isTsumo = isTsumo)
        return when (plan.kind) {
            PaymentKind.DEALER_TSUMO -> {
                val each = plan.components.single()
                listOf("自摸：闲家各付 ${each.amount}")
            }
            PaymentKind.NON_DEALER_TSUMO -> {
                val dealer = plan.components.first { it.payerRole == PayerRole.DEALER }
                val other = plan.components.first { it.payerRole == PayerRole.NON_DEALER }
                listOf("自摸：庄家付 ${dealer.amount}、闲家各付 ${other.amount}")
            }
            PaymentKind.DEALER_RON, PaymentKind.NON_DEALER_RON -> {
                val pay = plan.components.single().amount
                val target = if (loser != null) "P$loser" else "放铳者"
                listOf("荣和：$target 支付 $pay")
            }
        }
    }
}

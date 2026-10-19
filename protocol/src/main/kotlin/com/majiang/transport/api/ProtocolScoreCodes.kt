package com.majiang.transport.api

/**
 * 算分结构化字段的稳定码契约（D-07）
 *
 * 该对象只冻结未来线路迁移所需的标识，不改变当前 MJ-TCP/1 的任何 DTO、字段或
 * 序列化行为。当前 `ApiProtocol.PROTOCOL_VERSION = 4` 仍只传既有字符串与数值字段；
 * 结构化字段必须等独立协议版本立项后再加入 DTO，并由 capability/版本协商控制。
 *
 * 稳定码规则：
 * - 已发布的码只增不改；Kotlin 枚举名或展示文案变化不得改变码。
 * - 客户端遇到未知码必须回退到旧字符串字段，禁止因未知码丢弃整条结算。
 */
object ProtocolScoreCodes {

    /** 结构化算分字段契约版本；与 `scoreDetailVersion` capability 对应。 */
    const val CONTRACT_VERSION: Int = 1

    /** 首次允许携带结构化算分字段的业务协议版本（当前仍为 4）。 */
    const val STRUCTURED_SCORE_MIN_PROTOCOL_VERSION: Int = 5

    /** 未来结构字段名，供 v5 编解码与兼容测试引用。 */
    const val FIELD_CAP_CODE: String = "capCode"
    const val FIELD_YAKU_ENTRIES: String = "yakuEntries"
    const val FIELD_PAYMENT_PLAN: String = "paymentPlan"
    const val FIELD_SCORE_DETAIL_VERSION: String = "scoreDetailVersion"

    /** 封顶原因码（未封顶不写该字段；役满写 YAKUMAN + yakumanMultiplier）。 */
    const val CAP_MANGAN: String = "MANGAN"
    const val CAP_HANEMAN: String = "HANEMAN"
    const val CAP_BAIMAN: String = "BAIMAN"
    const val CAP_SANBAIMAN: String = "SANBAIMAN"
    const val CAP_KAZOE_YAKUMAN: String = "KAZOE_YAKUMAN"
    const val CAP_YAKUMAN: String = "YAKUMAN"

    val CAP_CODES: Set<String> = setOf(
        CAP_MANGAN,
        CAP_HANEMAN,
        CAP_BAIMAN,
        CAP_SANBAIMAN,
        CAP_KAZOE_YAKUMAN,
        CAP_YAKUMAN
    )

    /** 役种稳定码；与 engine `Yaku.code` 逐项一致，由 server 跨模块契约测试守卫。 */
    val YAKU_CODES: Set<String> = setOf(
        "RIICHI",
        "IPPATSU",
        "MENZEN_TSUMO",
        "TANYAO",
        "PINFU",
        "IIPEIKO",
        "YAKUHAI_HAKU",
        "YAKUHAI_HATSU",
        "YAKUHAI_CHUN",
        "YAKUHAI_BAKAZE",
        "YAKUHAI_JIKAZE",
        "HAITEI",
        "HOUTEI",
        "RINSHAN",
        "CHANKAN",
        "DOUBLE_RIICHI",
        "CHANTA",
        "ITTSU",
        "SAN_SHOKU_DOUJUN",
        "SAN_SHOKU_DOUKOU",
        "TOITOI",
        "SAN_ANKOU",
        "SAN_KANTSU",
        "SHOUSANGEN",
        "HONROUTOU",
        "CHIITOITSU",
        "HONITSU",
        "JUNCHAN",
        "RYANPEIKOU",
        "CHINITSU",
        "KOKUSHI",
        "SUUANKOU",
        "DAISANGEN",
        "SHOUSUUSHII",
        "DAISUUSHII",
        "TSUUIISOU",
        "RYUUIISOU",
        "CHINROUTOU",
        "CHURENPOUTOU",
        "SUUKANTSU",
        "TENHOU",
        "CHIIHOU"
    )

    /** 支付场景稳定码。 */
    val PAYMENT_KINDS: Set<String> = setOf(
        "DEALER_TSUMO",
        "NON_DEALER_TSUMO",
        "DEALER_RON",
        "NON_DEALER_RON"
    )

    /** 付款方角色稳定码。 */
    val PAYER_ROLES: Set<String> = setOf("DEALER", "NON_DEALER", "DISCARDER")

    /** 未知役种码的展示回退占位；真实展示名仍由旧字符串字段提供。 */
    const val UNKNOWN_YAKU_CODE: String = "UNKNOWN_YAKU"

    fun isKnownYakuCode(code: String): Boolean = code in YAKU_CODES

    fun isKnownCapCode(code: String): Boolean = code in CAP_CODES
}

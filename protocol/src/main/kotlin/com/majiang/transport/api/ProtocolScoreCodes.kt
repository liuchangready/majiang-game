package com.majiang.transport.api

/**
 * 算分结构化字段的稳定码契约（D-07 / 业务协议 v5）
 *
 * 自 `ApiProtocol.PROTOCOL_VERSION = 5`、`TcpWire.BUSINESS_VERSION = 2` 起，
 * `ProtocolScoreResult.capCode`、`ProtocolScoreBreakdown.yakuEntries`、
 * `ProtocolScoreBreakdown.paymentPlan` 与 `ProtocolAccessCapabilities.scoreDetailVersion`
 * 正式进入线路。v4 业务版本不再被双端接受（HELLO/WELCOME 明确 VERSION_MISMATCH），
 * 因此不存在“把 v5 字段偷偷塞进 v4”的兼容路径。
 *
 * 稳定码规则：
 * - 已发布的码只增不改；Kotlin 枚举名或展示文案变化不得改变码。
 * - 客户端遇到未知码必须回退到旧字符串字段，禁止因未知码丢弃整条结算。
 * - 未知字段仍然严格拒绝；只有“码值”是向前兼容的扩展点。
 */
object ProtocolScoreCodes {

    /** 结构化算分字段契约版本；与 `scoreDetailVersion` capability 对应。 */
    const val CONTRACT_VERSION: Int = 1

    /** 首次允许携带结构化算分字段的业务协议版本。 */
    const val STRUCTURED_SCORE_MIN_PROTOCOL_VERSION: Int = 5

    /** 结构字段名，供 v5 编解码、测试与审计引用。 */
    const val FIELD_CAP_CODE: String = "capCode"
    const val FIELD_YAKU_ENTRIES: String = "yakuEntries"
    const val FIELD_PAYMENT_PLAN: String = "paymentPlan"
    const val FIELD_SCORE_DETAIL_VERSION: String = "scoreDetailVersion"

    // === 严格编解码上限（防止恶意/损坏载荷放大内存与整数溢出） ===

    /** 单条结算最多役种条目数。 */
    const val MAX_YAKU_ENTRIES: Int = 64

    /** 单条支付计划最多付款方分组数。 */
    const val MAX_PAYMENT_COMPONENTS: Int = 8

    /** 稳定码最大长度（ASCII 大写/下划线）。 */
    const val MAX_CODE_LENGTH: Int = 64

    /** 展示名最大长度（UTF-16 字符数）。 */
    const val MAX_DISPLAY_NAME_LENGTH: Int = 256

    /** 单役种最大番数；役满条目可为 0。 */
    const val MAX_YAKU_HAN: Int = 1000

    /** 单付款方金额上限。 */
    const val MAX_PAYMENT_AMOUNT: Int = 10_000_000

    /** 单付款方人数上限。 */
    const val MAX_PAYMENT_COUNT: Int = 8

    /** 支付计划总额上限。 */
    const val MAX_PAYMENT_TOTAL: Int = 100_000_000

    /** capability 中允许的 scoreDetailVersion 上限。 */
    const val MAX_SCORE_DETAIL_VERSION: Int = 1000

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
    const val PAYMENT_KIND_DEALER_TSUMO: String = "DEALER_TSUMO"
    const val PAYMENT_KIND_NON_DEALER_TSUMO: String = "NON_DEALER_TSUMO"
    const val PAYMENT_KIND_DEALER_RON: String = "DEALER_RON"
    const val PAYMENT_KIND_NON_DEALER_RON: String = "NON_DEALER_RON"

    val PAYMENT_KINDS: Set<String> = setOf(
        PAYMENT_KIND_DEALER_TSUMO,
        PAYMENT_KIND_NON_DEALER_TSUMO,
        PAYMENT_KIND_DEALER_RON,
        PAYMENT_KIND_NON_DEALER_RON
    )

    /** 付款方角色稳定码。 */
    const val PAYER_ROLE_DEALER: String = "DEALER"
    const val PAYER_ROLE_NON_DEALER: String = "NON_DEALER"
    const val PAYER_ROLE_DISCARDER: String = "DISCARDER"

    val PAYER_ROLES: Set<String> = setOf(
        PAYER_ROLE_DEALER,
        PAYER_ROLE_NON_DEALER,
        PAYER_ROLE_DISCARDER
    )

    /** 未知役种码的展示回退占位；真实展示名仍由 `displayName` / 旧字符串字段提供。 */
    const val UNKNOWN_YAKU_CODE: String = "UNKNOWN_YAKU"

    fun isKnownYakuCode(code: String): Boolean = code in YAKU_CODES

    fun isKnownCapCode(code: String): Boolean = code in CAP_CODES

    fun isKnownPaymentKind(code: String): Boolean = code in PAYMENT_KINDS

    fun isKnownPayerRole(code: String): Boolean = code in PAYER_ROLES
}

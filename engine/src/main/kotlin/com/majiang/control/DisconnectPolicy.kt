package com.majiang.control

/**
 * 掉线处理模式 (V3 架构 §8.1)
 */
enum class DisconnectMode {
    /** 掉线确认后立即转给配置的 AI 托管接管 */
    IMMEDIATE_AI,

    /** 宽限期内原决策计时继续（若超时执行超时默认动作），宽限期结束后转 AI 托管接管 */
    GRACE_THEN_AI,

    /** 不启用 AI 接管，掉线期间继续纯依赖超时默认动作执行 */
    TIMEOUT_ONLY
}

/**
 * 掉线与接管策略配置
 *
 * @property mode 掉线处理模式，默认 [DisconnectMode.IMMEDIATE_AI]
 * @property graceMillis 宽限期毫秒数（仅在 [DisconnectMode.GRACE_THEN_AI] 下生效），默认 10000ms (10秒)
 * @property aiProfileId 接管使用的 AI 策略配置标识，默认 "basic"
 */
data class DisconnectPolicy(
    val mode: DisconnectMode = DisconnectMode.IMMEDIATE_AI,
    val graceMillis: Long = 10000L,
    val aiProfileId: String = "basic"
) {
    init {
        require(graceMillis >= 0L) { "graceMillis must be non-negative, got $graceMillis" }
        require(aiProfileId.isNotBlank()) { "aiProfileId cannot be blank" }
    }
}

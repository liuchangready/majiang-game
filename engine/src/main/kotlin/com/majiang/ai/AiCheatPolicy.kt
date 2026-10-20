package com.majiang.ai

import java.util.LinkedHashMap

/**
 * AI 千术策略与预算配置。
 *
 * 默认关闭，保持既有 AI 不主动出千的行为。托管座位默认不花点，必须显式开启
 * [takeoverMaySpend] 且房间策略同时开启才会使用付费千术。
 *
 * 旧的 `ShuffleConfig.aiMaySpend` 语义由权威投影合并：原生电脑在
 * `aiMaySpend = true` 时即使本策略未开启也允许洗牌准备花费，保留旧配置的实际效果；
 * 掉线托管不继承该旧语义。
 *
 * @property enabled 房间/策略级总开关
 * @property maxSpendPerRound 每座位每局最多花费的千术点
 * @property reservePoints 保留不花的点数下限
 * @property riskTolerance 风险容忍度 [0, 1]，越高越愿意承担暴露风险
 * @property candidateLimit 每个能力生成的候选上限
 * @property takeoverMaySpend 托管座位是否允许主动花点（默认 false）
 * @property capabilityToggles 按稳定能力 ID 的单独开关；缺省视为开启
 */
data class AiCheatPolicy @JvmOverloads constructor(
    val enabled: Boolean = false,
    val maxSpendPerRound: Int = 60,
    val reservePoints: Int = 0,
    val riskTolerance: Double = 0.5,
    val candidateLimit: Int = 6,
    val takeoverMaySpend: Boolean = false,
    val capabilityToggles: Map<String, Boolean> = emptyMap()
) {
    init {
        require(maxSpendPerRound >= 0) { "maxSpendPerRound must be non-negative, got: $maxSpendPerRound" }
        require(reservePoints >= 0) { "reservePoints must be non-negative, got: $reservePoints" }
        require(!riskTolerance.isNaN() && riskTolerance in 0.0..1.0) {
            "riskTolerance must be in 0.0..1.0, got: $riskTolerance"
        }
        require(candidateLimit in 1..64) { "candidateLimit must be in 1..64, got: $candidateLimit" }
        require(capabilityToggles.keys.all { it.isNotBlank() }) { "capability toggle id cannot be blank" }
    }

    /** 能力开关的单独取值，不考虑总开关。 */
    fun isCapabilityToggled(capabilityId: String): Boolean = capabilityToggles[capabilityId] ?: true

    /** 总开关与单独开关同时允许。 */
    fun allows(capabilityId: String): Boolean = enabled && isCapabilityToggled(capabilityId)

    fun withCapability(capabilityId: String, allowed: Boolean): AiCheatPolicy {
        require(capabilityId.isNotBlank()) { "capabilityId cannot be blank" }
        val toggles = LinkedHashMap(capabilityToggles)
        toggles[capabilityId] = allowed
        return copy(capabilityToggles = toggles)
    }

    /**
     * 与策略级配置取交集：任何一侧禁止即禁止，预算/候选数取更严格者，保留点取更高者。
     */
    fun restrictWith(other: AiCheatPolicy?): AiCheatPolicy {
        if (other == null) return this
        val keys = LinkedHashSet<String>(capabilityToggles.keys)
        keys.addAll(other.capabilityToggles.keys)
        val toggles = LinkedHashMap<String, Boolean>()
        for (key in keys) {
            toggles[key] = isCapabilityToggled(key) && other.isCapabilityToggled(key)
        }
        return AiCheatPolicy(
            enabled = enabled && other.enabled,
            maxSpendPerRound = minOf(maxSpendPerRound, other.maxSpendPerRound),
            reservePoints = maxOf(reservePoints, other.reservePoints),
            riskTolerance = minOf(riskTolerance, other.riskTolerance),
            candidateLimit = minOf(candidateLimit, other.candidateLimit),
            takeoverMaySpend = takeoverMaySpend && other.takeoverMaySpend,
            capabilityToggles = toggles
        )
    }
}

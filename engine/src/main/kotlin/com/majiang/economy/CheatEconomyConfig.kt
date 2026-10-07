package com.majiang.economy

/**
 * 出千经济配置 (V3)
 *
 * @property enabled 是否启用出千经济系统（默认 false）
 * @property initialPoints 各座位初始出千点数（默认 4 个座位各 0 点）
 * @property costs 各项千术的消耗点数配置（千术标识 -> 消耗点数）
 */
data class CheatEconomyConfig @JvmOverloads constructor(
    val enabled: Boolean = false,
    val initialPoints: List<Int> = List(4) { 0 },
    val costs: Map<String, Int> = emptyMap()
) {
    init {
        require(initialPoints.size == 4) {
            "initialPoints size (${initialPoints.size}) must match 4 players"
        }
        require(initialPoints.all { it >= 0 }) {
            "initialPoints must all be non-negative, got: $initialPoints"
        }
    }

    /**
     * 获取指定千术的配置费用；若未配置则返回 0
     */
    fun costFor(techniqueId: String): Int = costs[techniqueId] ?: 0
}

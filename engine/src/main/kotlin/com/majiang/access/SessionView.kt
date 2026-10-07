package com.majiang.access

import java.util.Collections

/**
 * 客户端整场会话视图 (SessionView)
 *
 * 公开只读不可变快照，所有内部集合均保证防御性隔离。
 *
 * @property lifecycle 整场当前生命周期阶段名称（如 ROUND_ACTIVE, WAITING_READY, FINISHED 等）
 * @property scores 当前各席位权威可见积分
 * @property roundWind 场风 (1=东, 2=南, 3=西, 4=北)
 * @property roundNumber 局号 (0=一局, 1=二局, 2=三局, 3=四局)
 * @property dealerSeat 当前庄家座位 (0..3)
 * @property honba 本场棒数
 * @property roundRecords 单局公开历史记录列表
 * @property finalResult 整场最终结果（若尚未终场则为 null）
 */
data class SessionView(
    val lifecycle: String,
    val scores: List<Int>,
    val roundWind: Int,
    val roundNumber: Int,
    val dealerSeat: Int,
    val honba: Int,
    val roundRecords: List<PublicRoundRecord> = emptyList(),
    val finalResult: PublicSessionResult? = null
) {
    init {
        require(lifecycle.isNotBlank()) { "lifecycle cannot be blank" }
        require(dealerSeat in 0..3) { "dealerSeat must be in 0..3, got $dealerSeat" }
        require(honba >= 0) { "honba must be >= 0, got $honba" }
    }

    fun deepCopy(): SessionView = SessionView(
        lifecycle = lifecycle,
        scores = Collections.unmodifiableList(scores.toList()),
        roundWind = roundWind,
        roundNumber = roundNumber,
        dealerSeat = dealerSeat,
        honba = honba,
        roundRecords = Collections.unmodifiableList(roundRecords.map { it.deepCopy() }),
        finalResult = finalResult?.deepCopy()
    )
}

package com.majiang.access

import java.util.Collections

/**
 * 公开整场终局结果 (PublicSessionResult)
 *
 * @property finalScores 最终各席位得分
 * @property rankedSeats 按领域权威实际排名从优到劣排序的座位号列表 (1st..4th)
 * @property reason 终局原因（如 NORMAL_COMPLETION, BANKRUPTCY 等）
 * @property finalCheatPoints 各席位最终出千点数余额
 */
data class PublicSessionResult(
    val finalScores: List<Int>,
    val rankedSeats: List<Int>,
    val reason: String,
    val finalCheatPoints: List<Int> = emptyList()
) {
    init {
        require(reason.isNotBlank()) { "reason cannot be blank" }
    }

    fun deepCopy(): PublicSessionResult = PublicSessionResult(
        finalScores = Collections.unmodifiableList(finalScores.toList()),
        rankedSeats = Collections.unmodifiableList(rankedSeats.toList()),
        reason = reason,
        finalCheatPoints = Collections.unmodifiableList(finalCheatPoints.toList())
    )
}

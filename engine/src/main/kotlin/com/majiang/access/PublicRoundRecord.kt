package com.majiang.access

import java.util.Collections

/**
 * 公开单局历史结算记录 (PublicRoundRecord)
 *
 * 仅包含结算比分、点数净变动与出千点数汇总，杜绝泄露暗牌或详细出千流水原因。
 * 所有集合均进行严格不可变与防御性隔离。
 *
 * @property roundId 单局唯一标识
 * @property resultId 单局结算结果唯一标识
 * @property finalScores 单局结束时各席位得分
 * @property deltas 各席位分数变化
 * @property finalCheatPoints 单局结束时各席位出千点数余额
 * @property cheatDeltas 各席位出千点数净变动
 */
data class PublicRoundRecord(
    val roundId: String,
    val resultId: String,
    val finalScores: List<Int>,
    val deltas: List<Int>,
    val finalCheatPoints: List<Int>,
    val cheatDeltas: List<Int>
) {
    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(resultId.isNotBlank()) { "resultId cannot be blank" }
    }

    fun deepCopy(): PublicRoundRecord = PublicRoundRecord(
        roundId = roundId,
        resultId = resultId,
        finalScores = Collections.unmodifiableList(finalScores.toList()),
        deltas = Collections.unmodifiableList(deltas.toList()),
        finalCheatPoints = Collections.unmodifiableList(finalCheatPoints.toList()),
        cheatDeltas = Collections.unmodifiableList(cheatDeltas.toList())
    )
}

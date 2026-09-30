package com.majiang.round

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.match.RoundOutcome
import java.util.Collections
import java.util.LinkedHashSet

/**
 * 单局对局结果快照
 *
 * 遵循 DESIGN.md 4.1：
 * @property resultId 结算结果唯一标识
 * @property roundId 本局标识
 * @property outcome 单局胜负描述（庄赢/闲赢/流局等）
 * @property startingScores 本局开始时的四家分数
 * @property finalScores 本局结束时的四家分数
 * @property totalDeltas 各座位的分数净变动（从本局起始积分计算，含立直扣点与供托）
 * @property startingRiichiSticks 本局起始场上立直棒数
 * @property remainingRiichiSticks 本局结束时场上剩余立直棒数
 * @property events 本局自发牌至终了累计发生的全部事件列表
 */
data class RoundResult(
    val resultId: String,
    val roundId: String,
    val outcome: RoundOutcome,
    val startingScores: List<Int>,
    val finalScores: List<Int>,
    val totalDeltas: List<Int>,
    val startingRiichiSticks: Int,
    val remainingRiichiSticks: Int,
    val events: List<GameEvent>
) {
    /**
     * 深度防御复制，递归隔离 GameEvent 内部数组（如 ScoreSettled.newScores）
     * 以及 RoundOutcome.ExhaustiveDraw 中的 tenpaiSeats 集合等可变数据结构
     */
    fun deepCopy(): RoundResult {
        val isolatedOutcome = when (outcome) {
            is RoundOutcome.ExhaustiveDraw -> RoundOutcome.ExhaustiveDraw(
                Collections.unmodifiableSet(LinkedHashSet(outcome.tenpaiSeats))
            )
            else -> outcome
        }
        return copy(
            outcome = isolatedOutcome,
            startingScores = startingScores.toList(),
            finalScores = finalScores.toList(),
            totalDeltas = totalDeltas.toList(),
            events = events.map { snapshotGameEvent(it) }
        )
    }
}

package com.majiang.round

import com.majiang.economy.CheatLedgerEntry
import com.majiang.economy.CheatLedgerSummary
import com.majiang.economy.CheatSeatStatistics
import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.match.RoundOutcome
import java.util.Collections
import java.util.LinkedHashSet

/**
 * 单局对局结果快照 (RoundResult - V3)
 *
 * 遵循 DESIGN.md 4.1 与 V3 D3 契约：
 * @property resultId 结算结果唯一标识
 * @property roundId 本局标识
 * @property outcome 单局胜负描述（庄赢/闲赢/流局等）
 * @property startingScores 本局开始时的四家分数
 * @property finalScores 本局结束时的四家分数
 * @property totalDeltas 各座位的分数净变动（从本局起始积分计算，含立直扣点与供托）
 * @property startingRiichiSticks 本局起始场上立直棒数
 * @property remainingRiichiSticks 本局结束时场上剩余立直棒数
 * @property events 本局自发牌至终了累计发生的全部事件列表
 * @property startingCheatPoints 本局起始出千点数（默认 4 个 0 兼容旧测试）
 * @property finalCheatPoints 本局结束出千点数（默认 4 个 0 兼容旧测试）
 * @property cheatPointDeltas 各座位的出千点数净变动（默认 4 个 0 兼容旧测试）
 * @property cheatEntries 本局已入账的出千流水记录
 */
data class RoundResult @JvmOverloads constructor(
    val resultId: String,
    val roundId: String,
    val outcome: RoundOutcome,
    val startingScores: List<Int>,
    val finalScores: List<Int>,
    val totalDeltas: List<Int>,
    val startingRiichiSticks: Int,
    val remainingRiichiSticks: Int,
    val events: List<GameEvent>,
    val startingCheatPoints: List<Int> = List(4) { 0 },
    val finalCheatPoints: List<Int> = List(4) { 0 },
    val cheatPointDeltas: List<Int> = List(4) { 0 },
    val cheatEntries: List<CheatLedgerEntry> = emptyList()
) {
    /**
     * 单局出千点数与收支流水汇总摘要
     */
    val cheatSummary: CheatLedgerSummary
        get() = CheatLedgerSummary(
            startingBalances = Collections.unmodifiableList(startingCheatPoints.toList()),
            finalBalances = Collections.unmodifiableList(finalCheatPoints.toList()),
            totalDeltas = Collections.unmodifiableList(cheatPointDeltas.toList()),
            entries = Collections.unmodifiableList(cheatEntries.toList()),
            seatStatistics = Collections.unmodifiableList((0 until 4).map { s ->
                val gained = cheatEntries.filter { it.seat == s && it.amount > 0 }.sumOf { it.amount }
                val spent = cheatEntries.filter { it.seat == s && it.amount < 0 }.sumOf { -it.amount }
                CheatSeatStatistics(
                    seat = s,
                    startingPoints = startingCheatPoints.getOrElse(s) { 0 },
                    finalPoints = finalCheatPoints.getOrElse(s) { 0 },
                    totalGained = gained,
                    totalSpent = spent,
                    netDelta = cheatPointDeltas.getOrElse(s) { 0 }
                )
            })
        )

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
            startingScores = Collections.unmodifiableList(startingScores.toList()),
            finalScores = Collections.unmodifiableList(finalScores.toList()),
            totalDeltas = Collections.unmodifiableList(totalDeltas.toList()),
            events = Collections.unmodifiableList(events.map { snapshotGameEvent(it) }),
            startingCheatPoints = Collections.unmodifiableList(startingCheatPoints.toList()),
            finalCheatPoints = Collections.unmodifiableList(finalCheatPoints.toList()),
            cheatPointDeltas = Collections.unmodifiableList(cheatPointDeltas.toList()),
            cheatEntries = Collections.unmodifiableList(cheatEntries.map { it.copy() })
        )
    }
}

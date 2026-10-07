package com.majiang.economy

import java.util.Collections

/**
 * 各座位出千统计数据（单局或整场）
 *
 * @property seat 座位号
 * @property startingPoints 起始点数
 * @property finalPoints 最终点数
 * @property totalGained 累计获得点数 (>= 0)
 * @property totalSpent 累计消耗点数 (>= 0)
 * @property netDelta 净增量 (finalPoints - startingPoints)
 */
data class CheatSeatStatistics(
    val seat: Int,
    val startingPoints: Int,
    val finalPoints: Int,
    val totalGained: Int,
    val totalSpent: Int,
    val netDelta: Int
)

/**
 * 出千账本不可变快照 (Defensive Snapshot)
 *
 * @property balances 各座位当前点数余额列表
 * @property entries 已发生的所有流水记录列表
 */
data class CheatLedgerSnapshot(
    val balances: List<Int>,
    val entries: List<CheatLedgerEntry>
) {
    init {
        require(balances.size == 4) { "balances must contain exactly 4 player entries" }
    }
}

/**
 * 出千账本单局交接与整场结果统计汇总 (Result Handoff Summary)
 *
 * @property startingBalances 起始点数列表
 * @property finalBalances 最终点数列表
 * @property totalDeltas 各座位净变化量列表
 * @property entries 期间所有流水条目列表
 * @property seatStatistics 各座位详细收支统计列表
 */
data class CheatLedgerSummary(
    val startingBalances: List<Int>,
    val finalBalances: List<Int>,
    val totalDeltas: List<Int>,
    val entries: List<CheatLedgerEntry>,
    val seatStatistics: List<CheatSeatStatistics>
)

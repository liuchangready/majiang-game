package com.majiang.economy

import java.util.Collections

/**
 * 独立出千资源账本 (CheatLedger - V3)
 *
 * 核心契约：
 * 1. 独立于麻将点数，专职管理出千点数生命周期与收支流水；
 * 2. 幂等与重试：同 id 同载荷重试返回原 entry；
 * 3. 拒绝无副作用：同 id 冲突载荷、负余额、座位越界、数值溢出全部拒绝且零副作用；
 * 4. 正负流水与原因保留：正数表示获得/奖励，负数表示消耗/处罚；
 * 5. 防御性快照与交接结构：提供 balances()、entries()、snapshot()、summary()；
 * 6. 线程安全保护。
 */
class CheatLedger @JvmOverloads constructor(
    val config: CheatEconomyConfig = CheatEconomyConfig()
) {
    private val lock = Any()
    private val currentBalances: MutableList<Int> = config.initialPoints.toMutableList()
    private val appliedEntries: MutableList<CheatLedgerEntry> = mutableListOf()
    private val entryMap: MutableMap<String, CheatLedgerEntry> = mutableMapOf()
    private val initialBalances: List<Int> = Collections.unmodifiableList(config.initialPoints.toList())

    init {
        require(currentBalances.size == 4) {
            "initialPoints must contain exactly 4 entries, got ${currentBalances.size}"
        }
        require(currentBalances.all { it >= 0 }) {
            "initialPoints must all be non-negative, got $currentBalances"
        }
    }

    /**
     * 获取当前四位玩家的出千点数余额（不可变深拷贝列表）
     */
    fun balances(): List<Int> = synchronized(lock) {
        Collections.unmodifiableList(currentBalances.toList())
    }

    /**
     * 获取指定座位的当前出千点数余额
     */
    fun balance(seat: Int): Int = synchronized(lock) {
        require(seat in currentBalances.indices) {
            "Seat index out of bounds: seat=$seat, valid=0..${currentBalances.size - 1}"
        }
        currentBalances[seat]
    }

    /**
     * 获取所有已入账的流水记录（不可变列表）
     */
    fun entries(): List<CheatLedgerEntry> = synchronized(lock) {
        Collections.unmodifiableList(appliedEntries.toList())
    }

    /**
     * 应用出千点数流水变更
     *
     * @param entryId 幂等流水标识
     * @param seat 座位号 (0..3)
     * @param amount 变更点数（正数增加，负数扣减）
     * @param reason 业务原因
     * @return 成功入账或重试返回的流水条目
     * @throws IllegalArgumentException 冲突、越界、参数非法
     * @throws IllegalStateException 余额不足导致负余额
     * @throws ArithmeticException 整数数值溢出
     */
    fun apply(entryId: String, seat: Int, amount: Int, reason: String): CheatLedgerEntry = synchronized(lock) {
        require(entryId.isNotBlank()) { "entryId must not be blank" }

        // 1. 同 id 查重处理
        val existing = entryMap[entryId]
        if (existing != null) {
            if (existing.seat == seat && existing.amount == amount && existing.reason == reason) {
                // 同 id 同载荷重试返回原 entry
                return existing
            } else {
                // 冲突拒绝无副作用
                throw IllegalArgumentException(
                    "Idempotency conflict for entryId '$entryId': existing=$existing, requested=(seat=$seat, amount=$amount, reason='$reason')"
                )
            }
        }

        // 2. 越界检查
        if (seat !in currentBalances.indices) {
            throw IllegalArgumentException(
                "Seat index out of bounds: seat=$seat, validIndices=0..${currentBalances.size - 1}"
            )
        }

        // 3. 溢出检查 (Integer overflow/underflow)
        val current = currentBalances[seat]
        val nextLong = current.toLong() + amount.toLong()
        if (nextLong > Int.MAX_VALUE || nextLong < Int.MIN_VALUE) {
            throw ArithmeticException(
                "Integer overflow for balance calculation: current=$current, amount=$amount"
            )
        }

        // 4. 负余额检查 (扣减不足)
        val nextBalance = nextLong.toInt()
        if (nextBalance < 0) {
            throw IllegalStateException(
                "Insufficient cheat points for seat $seat: current balance $current, deduction $amount, would result in negative balance $nextBalance"
            )
        }

        // 5. 应用变更并记录流水
        currentBalances[seat] = nextBalance
        val entry = CheatLedgerEntry(
            entryId = entryId,
            seat = seat,
            amount = amount,
            balanceAfter = nextBalance,
            reason = reason
        )
        appliedEntries.add(entry)
        entryMap[entryId] = entry

        entry
    }

    /**
     * 检查某座位是否能承受指定消耗 (cost > 0 或负数 amount)
     */
    fun canAfford(seat: Int, cost: Int): Boolean = synchronized(lock) {
        if (seat !in currentBalances.indices) return false
        val required = if (cost < 0) -cost else cost
        currentBalances[seat] >= required
    }

    /**
     * 生成当前账本的防御性快照
     */
    fun snapshot(): CheatLedgerSnapshot = synchronized(lock) {
        CheatLedgerSnapshot(
            balances = Collections.unmodifiableList(currentBalances.toList()),
            entries = Collections.unmodifiableList(appliedEntries.toList())
        )
    }

    /**
     * 生成供单局结算与整场交接使用的统计汇总
     */
    fun summary(): CheatLedgerSummary = synchronized(lock) {
        val finals = Collections.unmodifiableList(currentBalances.toList())
        val deltas = Collections.unmodifiableList((0 until 4).map { finals[it] - initialBalances[it] })
        val entriesCopy = Collections.unmodifiableList(appliedEntries.toList())

        val seatStats = (0 until 4).map { s ->
            val gained = entriesCopy.filter { it.seat == s && it.amount > 0 }.sumOf { it.amount }
            val spent = entriesCopy.filter { it.seat == s && it.amount < 0 }.sumOf { -it.amount }
            CheatSeatStatistics(
                seat = s,
                startingPoints = initialBalances[s],
                finalPoints = finals[s],
                totalGained = gained,
                totalSpent = spent,
                netDelta = finals[s] - initialBalances[s]
            )
        }

        CheatLedgerSummary(
            startingBalances = initialBalances,
            finalBalances = finals,
            totalDeltas = deltas,
            entries = entriesCopy,
            seatStatistics = Collections.unmodifiableList(seatStats)
        )
    }

    companion object {
        /**
         * 从快照恢复出千账本
         */
        fun fromSnapshot(
            config: CheatEconomyConfig,
            snapshot: CheatLedgerSnapshot,
            initialSessionBalances: List<Int> = config.initialPoints
        ): CheatLedger {
            val ledger = CheatLedger(config)
            synchronized(ledger.lock) {
                ledger.currentBalances.clear()
                ledger.currentBalances.addAll(snapshot.balances)
                ledger.appliedEntries.clear()
                ledger.appliedEntries.addAll(snapshot.entries)
                ledger.entryMap.clear()
                for (entry in snapshot.entries) {
                    ledger.entryMap[entry.entryId] = entry
                }
            }
            return ledger
        }
    }
}

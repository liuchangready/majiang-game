package com.majiang.application.cheat

import com.majiang.economy.CheatEconomyConfig
import com.majiang.economy.CheatLedger
import com.majiang.economy.CheatLedgerEntry
import com.majiang.economy.CheatLedgerSnapshot

/**
 * 千术费用记账的最小类型化能力（D-09）。
 *
 * ApplicationCoordinator 在原有执行位置调用本能力，用当前候选余额/流水重建账本并原子
 * 计算一条扣费流水；能力本身不接触 GameState、不会安装状态、也不会发布事件。
 * 失败（余额不足、同 entryId 冲突、溢出）返回 null，由调用方按原有拒绝路径处理。
 */
internal object CheatLedgerCharge {

    data class Charge(
        val balanceAfter: Int,
        val entry: CheatLedgerEntry
    )

    fun apply(
        config: CheatEconomyConfig,
        balances: List<Int>,
        entries: List<CheatLedgerEntry>,
        entryId: String,
        seat: Int,
        amount: Int,
        reason: String
    ): Charge? {
        val ledger = try {
            CheatLedger.fromSnapshot(
                config = config,
                snapshot = CheatLedgerSnapshot(
                    balances = balances,
                    entries = entries
                )
            )
        } catch (e: Exception) {
            return null
        }
        val applied = try {
            ledger.apply(entryId, seat, amount, reason)
        } catch (e: Exception) {
            return null
        }
        return Charge(balanceAfter = applied.balanceAfter, entry = applied.copy())
    }
}

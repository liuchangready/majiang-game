package com.majiang.economy

/**
 * 出千点数流水条目 (V3)
 *
 * @property entryId 幂等流水唯一标识
 * @property seat 座位编号 (0..3)
 * @property amount 点数变动量（正数表示获得/奖励，负数表示消耗/处罚）
 * @property balanceAfter 变动后该座位的出千点数余额
 * @property reason 业务原因说明（如 "cheat_cost", "round_reward", "detected_penalty"）
 */
data class CheatLedgerEntry(
    val entryId: String,
    val seat: Int,
    val amount: Int,
    val balanceAfter: Int,
    val reason: String
) {
    /** 兼容属性，别名 seat */
    val seatId: Int get() = seat
}

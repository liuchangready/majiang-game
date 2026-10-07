package com.majiang.access

/**
 * 权威时钟视图
 *
 * 记录指定座位的决策计时基准、截止、剩余储备与权威时钟样本。
 * 允许单调时钟源负原点（如 System.nanoTime），只要求 deadline >= opensAt，reserve >= 0，时差合理。
 *
 * @property seat 目标座位 (0..3)
 * @property opensAt 决策开启时刻（权威单调毫秒，允许单调负原点）
 * @property deadline 决策截止时刻（权威单调毫秒，允许单调负原点）
 * @property reserve 当前座位剩余思考储备时间（毫秒）
 * @property serverNow 权威时钟采样时刻（权威单调毫秒，允许单调负原点）
 */
data class ClockView(
    val seat: Int,
    val opensAt: Long,
    val deadline: Long,
    val reserve: Long,
    val serverNow: Long
) {
    init {
        require(seat in 0..3) { "seat must be 0..3, got $seat" }
        require(deadline >= opensAt) { "deadline must be >= opensAt, got deadline=$deadline, opensAt=$opensAt" }
        require(reserve >= 0L) { "reserve must be >= 0, got $reserve" }
        val window = deadline - opensAt
        require(window >= 0L) { "decision window must not overflow: $window" }
    }
}

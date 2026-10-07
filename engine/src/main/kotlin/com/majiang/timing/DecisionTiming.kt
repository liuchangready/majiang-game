package com.majiang.timing

/**
 * 决策唯一标识键
 *
 * @property roundId 局标识
 * @property seat 座位号
 * @property decisionId 决策标识
 */
data class DecisionKey(
    val roundId: String,
    val seat: Int,
    val decisionId: String
) {
    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(seat in 0..3) { "seat must be 0..3, got $seat" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
    }
}

/**
 * 决策计时状态
 */
enum class DecisionTimingStatus {
    SCHEDULED,
    ACTIVE,
    CLOSED,
    PAUSED
}

typealias DecisionStatus = DecisionTimingStatus

/**
 * 决策计时详情 DTO
 *
 * @property key 决策键 (roundId, seat, decisionId)
 * @property generation 定时器代次，防止旧定时器超时任务串线
 * @property opensAtMillis 决策开始计时时刻（单调毫秒）
 * @property deadlineMillis 截止时刻（单调毫秒 = opensAtMillis + baseMillis + reserveAtOpenMillis）
 * @property reserveAtOpenMillis 决策开启时的剩余储备毫秒
 * @property baseMillis 基础思考时间毫秒（默认 3000ms）
 * @property status 计时状态
 */
data class DecisionTiming(
    val key: DecisionKey,
    val generation: Long,
    val opensAtMillis: Long,
    val deadlineMillis: Long,
    val reserveAtOpenMillis: Long,
    val baseMillis: Long = 3000L,
    val status: DecisionTimingStatus = DecisionTimingStatus.ACTIVE
) {
    init {
        require(baseMillis >= 0L) { "baseMillis must be non-negative, got $baseMillis" }
        require(reserveAtOpenMillis >= 0L) { "reserveAtOpenMillis must be non-negative, got $reserveAtOpenMillis" }
        try {
            Math.addExact(baseMillis, reserveAtOpenMillis)
        } catch (e: ArithmeticException) {
            throw IllegalArgumentException("Total duration (baseMillis + reserveAtOpenMillis) overflows Long", e)
        }
    }

    val roundId: String get() = key.roundId
    val seat: Int get() = key.seat
    val decisionId: String get() = key.decisionId

    val totalTimeAllocated: Long get() = Math.addExact(baseMillis, reserveAtOpenMillis)

    /** 判断在给定单调毫秒时刻是否已达到或超过截止时间 */
    fun isExpired(nowMillis: Long): Boolean = nowMillis >= deadlineMillis

    /** 计算在给定单调毫秒时刻若完成决策所消耗的储备毫秒数 */
    fun calculateReserveSpent(atMillis: Long): Long {
        if (atMillis <= opensAtMillis) return 0L
        val elapsed = Math.subtractExact(atMillis, opensAtMillis)
        val overBase = maxOf(0L, elapsed - baseMillis)
        return minOf(reserveAtOpenMillis, overBase)
    }

    /** 计算在给定单调毫秒时刻若完成决策后剩余的储备毫秒数 */
    fun calculateRemainingReserve(atMillis: Long): Long {
        return reserveAtOpenMillis - calculateReserveSpent(atMillis)
    }
}

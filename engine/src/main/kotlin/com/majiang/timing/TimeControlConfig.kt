package com.majiang.timing

/**
 * 储备时间作用域
 *
 * - [ROUND]: 每单局发牌（包括连庄）重置储备时间
 * - [SESSION]: 整场比赛共用储备时间，跨局不重置
 */
enum class ReserveScope {
    ROUND,
    SESSION
}

/**
 * 决策时间控制配置
 *
 * 默认规则：每个决策拥有 3 秒免费时间（baseMillis），单局拥有 20 秒储备（reserveMillis），按局重置。
 *
 * @property baseMillis 每次决策赠送的免费基础思考时间（毫秒），默认 3000ms (3秒)
 * @property reserveMillis 储备思考时间（毫秒），超出基础时间后扣减，默认 20000ms (20秒)
 * @property reserveScope 储备时间的重置作用域，默认单局重置 [ReserveScope.ROUND]
 */
data class TimeControlConfig(
    val baseMillis: Long = 3000L,
    val reserveMillis: Long = 20000L,
    val reserveScope: ReserveScope = ReserveScope.ROUND
) {
    init {
        require(baseMillis >= 0L) { "baseMillis must be non-negative, got $baseMillis" }
        require(reserveMillis >= 0L) { "reserveMillis must be non-negative, got $reserveMillis" }
        try {
            Math.addExact(baseMillis, reserveMillis)
        } catch (e: ArithmeticException) {
            throw IllegalArgumentException("Total duration (baseMillis + reserveMillis) overflows Long", e)
        }
    }
}

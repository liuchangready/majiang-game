package com.majiang.timing

/**
 * 单调时钟接口 (MonotonicClock)
 *
 * 用于决策计时与截止裁决，避免由于系统壁钟修改或夏令时调整导致倒流。
 * 支持在单元测试中注入确定性的单调时钟实现。
 */
fun interface MonotonicClock {
    /** 获取当前单调毫秒时间戳 */
    fun nowMillis(): Long
}

/**
 * 默认系统单调时钟实现
 *
 * 基于 [System.nanoTime] 转换为毫秒，具有单调递增保证。
 * 同时支持单例对象引用 [SystemMonotonicClock] 与构造器调用 [SystemMonotonicClock()]。
 */
open class SystemMonotonicClock : MonotonicClock {
    override fun nowMillis(): Long = System.nanoTime() / 1_000_000L

    companion object : SystemMonotonicClock()
}

/**
 * 手动步进单调时钟（测试与模拟用）
 */
class ManualMonotonicClock(
    private var currentMillis: Long = 0L
) : MonotonicClock {

    override fun nowMillis(): Long = currentMillis

    /** 前进指定毫秒数 */
    fun advance(millis: Long): Long {
        require(millis >= 0L) { "Advance millis must be non-negative, got $millis" }
        currentMillis = Math.addExact(currentMillis, millis)
        return currentMillis
    }

    /** 设置绝对毫秒数（必须单调不退） */
    fun set(millis: Long) {
        require(millis >= currentMillis) { "Cannot set clock backwards from $currentMillis to $millis" }
        currentMillis = millis
    }
}

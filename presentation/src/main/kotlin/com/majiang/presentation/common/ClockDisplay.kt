package com.majiang.presentation.common

import com.majiang.client.api.ClockInfo

/**
 * 格式化后的时钟呈现数据
 *
 * @property hasActiveTiming 是否正处于当前决策计时活动中
 * @property freeSeconds 剩余免费思考时间（秒，向上取整）
 * @property reserveSeconds 剩余思考时间储备（秒，精确到一位小数）
 * @property displayText 呈现文本
 * @property isUrgent 是否进入紧张警戒时间（无免费时间且储备低于 5 秒）
 */
data class FormattedClock(
    val hasActiveTiming: Boolean,
    val freeSeconds: Long,
    val reserveSeconds: Float,
    val displayText: String,
    val isUrgent: Boolean
)

/**
 * 权威时钟视图呈现计算工具 (ClockDisplay)。
 *
 * 遵循契约：
 * 1. 严格由 ClockInfo deadline、opensAt、reserve 与 serverNow 采样及本地增量估算；
 * 2. 客户端严禁发送自认超时命令或重置时钟；
 * 3. 免费思考时间与时间储备含义清晰区分展示；
 * 4. 紧凑文本 displayText 严格采用 `free+reserve` 整秒格式（如 `3+20`, `2+20`, `0+20`, `0+19`, `0+0`, `--+--`）。
 */
object ClockDisplay {

    fun format(clockInfo: ClockInfo?, localElapsedMillis: Long): FormattedClock {
        if (clockInfo == null) {
            return FormattedClock(
                hasActiveTiming = false,
                freeSeconds = 0L,
                reserveSeconds = 0f,
                displayText = "--+--",
                isUrgent = false
            )
        }

        return format(
            opensAt = clockInfo.opensAt,
            deadline = clockInfo.deadline,
            reserve = clockInfo.reserve,
            serverNow = clockInfo.serverNow,
            localElapsedMillis = localElapsedMillis
        )
    }

    fun format(
        opensAt: Long,
        deadline: Long,
        reserve: Long,
        serverNow: Long,
        localElapsedMillis: Long
    ): FormattedClock {
        val reserveAtStart = maxOf(0L, reserve)
        val isIdle = deadline <= opensAt

        if (isIdle) {
            val reserveSec = reserveAtStart / 1000f
            val reserveCeilSec = if (reserveAtStart > 0L) (reserveAtStart + 999L) / 1000L else 0L
            return FormattedClock(
                hasActiveTiming = false,
                freeSeconds = 0L,
                reserveSeconds = reserveSec,
                displayText = "0+$reserveCeilSec",
                isUrgent = false
            )
        }

        val elapsed = maxOf(0L, localElapsedMillis)
        val estServerNow = serverNow + elapsed
        val remainingTotal = maxOf(0L, deadline - estServerNow)

        // 剩余时间中超出储备的部分为免费基础时间
        val freeRemaining = maxOf(0L, remainingTotal - reserveAtStart)
        // 若总剩余小于初始储备，说明免费时间已耗尽，已在消耗储备
        val reserveRemaining = maxOf(0L, if (remainingTotal < reserveAtStart) remainingTotal else reserveAtStart)

        val freeSec = if (freeRemaining > 0L) (freeRemaining + 999L) / 1000L else 0L
        val reserveSec = reserveRemaining / 1000f
        val reserveCeilSec = if (reserveRemaining > 0L) (reserveRemaining + 999L) / 1000L else 0L
        val isUrgent = (freeRemaining == 0L && reserveRemaining < 5000L)

        val text = "$freeSec+$reserveCeilSec"

        return FormattedClock(
            hasActiveTiming = true,
            freeSeconds = freeSec,
            reserveSeconds = reserveSec,
            displayText = text,
            isUrgent = isUrgent
        )
    }
}

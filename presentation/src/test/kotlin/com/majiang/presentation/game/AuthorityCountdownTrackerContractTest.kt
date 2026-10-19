package com.majiang.presentation.game

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * AuthorityCountdownTracker 独立纯契约与边界测试。
 * 无 engine, server, protocol, GL 依赖。
 */
class AuthorityCountdownTrackerContractTest {

    @Test
    fun `initial state has no active sample and returns zero remaining millis`() {
        val tracker = AuthorityCountdownTracker { 100_000L }
        assertFalse(tracker.hasActiveSample)
        assertNull(tracker.sampledLocalMillis)
        assertEquals(0L, tracker.sampledServerNow)
        assertEquals(0L, tracker.cachedDeadline)
        assertEquals(0L, tracker.remainingMillis())
        assertFalse(tracker.isDeadlineValid())
    }

    @Test
    fun `sample captures authoritative deadline and advances remaining millis monotonically`() {
        var now = 100_000L
        val tracker = AuthorityCountdownTracker { now }

        // 权威 serverNow = 1_000, deadline = 24_000, 初始剩余 23_000 ms
        tracker.updateSample(serverNow = 1_000L, deadline = 24_000L, isSameIdentity = false, isPaused = false, nowMillis = now)
        assertTrue(tracker.hasActiveSample)
        assertEquals(100_000L, tracker.sampledLocalMillis)
        assertEquals(1_000L, tracker.sampledServerNow)
        assertEquals(24_000L, tracker.cachedDeadline)
        assertEquals(23_000L, tracker.remainingMillis())
        assertTrue(tracker.isDeadlineValid())

        // 流逝 10 秒
        now += 10_000L
        assertEquals(13_000L, tracker.remainingMillis())
        assertTrue(tracker.isDeadlineValid())

        // 重复刷新同一代次 (isSameIdentity = true)：不重置本地基准
        tracker.updateSample(serverNow = 11_000L, deadline = 24_000L, isSameIdentity = true, isPaused = false, nowMillis = now)
        assertEquals(100_000L, tracker.sampledLocalMillis, "同一代次刷新不得篡改 sampledLocalMillis")
        assertEquals(13_000L, tracker.remainingMillis())

        // 推进到到期前 1 ms
        now += 12_999L
        assertEquals(1L, tracker.remainingMillis())
        assertTrue(tracker.isDeadlineValid())

        // 到期瞬间
        now += 1L
        assertEquals(0L, tracker.remainingMillis())
        assertFalse(tracker.isDeadlineValid())

        // 超时后依然归零
        now += 5_000L
        assertEquals(0L, tracker.remainingMillis())
        assertFalse(tracker.isDeadlineValid())
    }

    @Test
    fun `identity change resets baseline to fresh authoritative sample`() {
        var now = 100_000L
        val tracker = AuthorityCountdownTracker { now }

        tracker.updateSample(serverNow = 1_000L, deadline = 24_000L, isSameIdentity = false, isPaused = false, nowMillis = now)
        assertEquals(23_000L, tracker.remainingMillis())

        now += 10_000L
        // 代次变更 (isSameIdentity = false)
        tracker.updateSample(serverNow = 50_000L, deadline = 65_000L, isSameIdentity = false, isPaused = false, nowMillis = now)
        assertEquals(110_000L, tracker.sampledLocalMillis)
        assertEquals(50_000L, tracker.sampledServerNow)
        assertEquals(65_000L, tracker.cachedDeadline)
        assertEquals(15_000L, tracker.remainingMillis())
    }

    @Test
    fun `pause freezes remaining millis to zero and resume resamples with compensated deadline`() {
        var now = 100_000L
        val tracker = AuthorityCountdownTracker { now }

        tracker.updateSample(serverNow = 1_000L, deadline = 24_000L, isSameIdentity = false, isPaused = false, nowMillis = now)
        assertEquals(23_000L, tracker.remainingMillis())

        // 进入暂停
        now += 5_000L
        tracker.updateSample(serverNow = 6_000L, deadline = 24_000L, isSameIdentity = false, isPaused = true, nowMillis = now)
        assertFalse(tracker.hasActiveSample)
        assertEquals(0L, tracker.remainingMillis())
        assertFalse(tracker.isDeadlineValid())

        // 暂停期间时间流逝
        now += 50_000L
        assertEquals(0L, tracker.remainingMillis(isPaused = true))

        // 恢复：权威补偿后的 serverNow 与 deadline
        tracker.updateSample(serverNow = 106_000L, deadline = 124_000L, isSameIdentity = false, isPaused = false, nowMillis = now)
        assertTrue(tracker.hasActiveSample)
        assertEquals(18_000L, tracker.remainingMillis())
        assertTrue(tracker.isDeadlineValid())
    }

    @Test
    fun `invalid or zero deadline clears sample immediately`() {
        val tracker = AuthorityCountdownTracker { 100_000L }
        tracker.updateSample(serverNow = 1_000L, deadline = 24_000L, isSameIdentity = false, isPaused = false, nowMillis = 100_000L)
        assertTrue(tracker.hasActiveSample)

        tracker.updateSample(serverNow = 25_000L, deadline = 0L, isSameIdentity = false, isPaused = false, nowMillis = 101_000L)
        assertFalse(tracker.hasActiveSample)
        assertNull(tracker.sampledLocalMillis)
        assertEquals(0L, tracker.remainingMillis())

        tracker.updateSample(serverNow = 1_000L, deadline = 24_000L, isSameIdentity = false, isPaused = false, nowMillis = 102_000L)
        assertTrue(tracker.hasActiveSample)

        tracker.clear()
        assertFalse(tracker.hasActiveSample)
        assertNull(tracker.sampledLocalMillis)
        assertEquals(0L, tracker.remainingMillis())
    }

    @Test
    fun `monotonic guard prevents negative elapsed time upon backward clock anomaly`() {
        var now = 100_000L
        val tracker = AuthorityCountdownTracker { now }

        tracker.updateSample(serverNow = 1_000L, deadline = 24_000L, isSameIdentity = false, isPaused = false, nowMillis = now)
        assertEquals(23_000L, tracker.remainingMillis())

        // 模拟异常时钟回退
        now = 90_000L
        assertEquals(23_000L, tracker.remainingMillis(), "时钟回退时 elapsed 必须钳制在 0，不得借此延长预算")
    }
}

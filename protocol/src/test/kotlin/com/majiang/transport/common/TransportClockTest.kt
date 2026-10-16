package com.majiang.transport.common

import org.junit.jupiter.api.Test
import java.util.PriorityQueue
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * 传输中性时钟与调度器单元测试 (契约 5, D10/D11)
 */
class TransportClockTest {

    /**
     * 确定性虚拟时间调度器与时钟实现（供单测使用）
     */
    class FakeTransportClockAndScheduler(
        initialTimeMillis: Long = 1000L,
        initialNanoTime: Long = 1_000_000_000L
    ) : TransportClock, TransportScheduler {

        @Volatile
        private var currentMillis: Long = initialTimeMillis

        @Volatile
        private var currentNanos: Long = initialNanoTime

        private var sequence: Long = 0L

        private data class TaskEntry(
            val id: Long,
            val triggerTimeMillis: Long,
            val task: Runnable,
            @Volatile var cancelled: Boolean = false
        ) : Comparable<TaskEntry> {
            override fun compareTo(other: TaskEntry): Int {
                val timeCmp = triggerTimeMillis.compareTo(other.triggerTimeMillis)
                return if (timeCmp != 0) timeCmp else id.compareTo(other.id)
            }
        }

        private val queue = PriorityQueue<TaskEntry>()

        override fun currentTimeMillis(): Long = currentMillis

        override fun nanoTime(): Long = currentNanos

        @Synchronized
        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val entry = TaskEntry(
                id = ++sequence,
                triggerTimeMillis = currentMillis + Math.max(0L, delayMillis),
                task = task
            )
            queue.add(entry)
            return Cancellable {
                synchronized(this@FakeTransportClockAndScheduler) {
                    entry.cancelled = true
                }
            }
        }

        @Synchronized
        fun advanceTimeBy(millis: Long) {
            require(millis >= 0L) { "millis must be >= 0: $millis" }
            val targetMillis = currentMillis + millis
            currentNanos += millis * 1_000_000L
            while (queue.isNotEmpty() && queue.peek().triggerTimeMillis <= targetMillis) {
                val entry = queue.poll()
                currentMillis = entry.triggerTimeMillis
                if (!entry.cancelled) {
                    entry.task.run()
                }
            }
            currentMillis = targetMillis
        }

        @Synchronized
        fun advanceNanoBy(nanos: Long) {
            require(nanos >= 0L) { "nanos must be >= 0: $nanos" }
            currentNanos += nanos
        }

        @Synchronized
        fun pendingTaskCount(): Int = queue.count { !it.cancelled }
    }

    @Test
    fun `test system clock returns reasonable timestamps`() {
        val clock = TransportClock.SYSTEM
        val t0 = clock.currentTimeMillis()
        val n0 = clock.nanoTime()

        assertTrue(t0 > 1_600_000_000_000L, "currentTimeMillis should be modern epoch")
        assertTrue(n0 > 0L, "nanoTime should be positive")

        val n1 = clock.nanoTime()
        assertTrue(n1 >= n0, "nanoTime must be monotonic non-decreasing")
    }

    @Test
    fun `test fake clock and scheduler advances time and triggers tasks in order`() {
        val clock = FakeTransportClockAndScheduler(initialTimeMillis = 1000L)
        val executed = mutableListOf<String>()

        clock.schedule(100L) { executed.add("task-100") }
        clock.schedule(300L) { executed.add("task-300") }
        clock.schedule(200L) { executed.add("task-200") }

        assertEquals(3, clock.pendingTaskCount())

        // 推进 150ms -> 仅 task-100 触发
        clock.advanceTimeBy(150L)
        assertEquals(listOf("task-100"), executed)
        assertEquals(1150L, clock.currentTimeMillis())

        // 再推进 100ms (累计 250ms) -> task-200 触发
        clock.advanceTimeBy(100L)
        assertEquals(listOf("task-100", "task-200"), executed)

        // 再推进 100ms (累计 350ms) -> task-300 触发
        clock.advanceTimeBy(100L)
        assertEquals(listOf("task-100", "task-200", "task-300"), executed)
        assertEquals(0, clock.pendingTaskCount())
    }

    @Test
    fun `test cancellable prevents task execution`() {
        val clock = FakeTransportClockAndScheduler(initialTimeMillis = 1000L)
        val ran = AtomicBoolean(false)

        val handle = clock.schedule(500L) { ran.set(true) }
        assertEquals(1, clock.pendingTaskCount())

        // 取消任务
        handle.cancel()

        // 推进越过触发时间
        clock.advanceTimeBy(1000L)
        assertFalse(ran.get(), "Cancelled task must not execute")
    }

    @Test
    fun `test monotonic nanoTime advancement`() {
        val clock = FakeTransportClockAndScheduler(initialTimeMillis = 1000L, initialNanoTime = 5_000_000L)
        assertEquals(5_000_000L, clock.nanoTime())

        clock.advanceNanoBy(2_500_000L)
        assertEquals(7_500_000L, clock.nanoTime())

        // advanceTimeBy 也会按比例推进纳秒
        clock.advanceTimeBy(10L) // 10ms = 10_000_000ns
        assertEquals(17_500_000L, clock.nanoTime())
    }
}

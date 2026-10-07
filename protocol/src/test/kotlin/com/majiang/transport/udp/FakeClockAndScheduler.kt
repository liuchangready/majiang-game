package com.majiang.transport.udp

import java.util.PriorityQueue

/**
 * 假时钟与确定性虚拟时间调度器 (FakeClockAndScheduler)
 *
 * 结合 [UdpClock] 和 [UdpScheduler]，在单元测试中完全替代系统时钟与后台执行线程，
 * 通过 [advanceTimeBy] 瞬间推进虚拟时间并触发到期任务，实现毫秒级零休眠、确定性测试。
 */
class FakeClockAndScheduler(initialTimeMillis: Long = 1000L) : UdpClock, UdpScheduler {

    @Volatile
    private var now: Long = initialTimeMillis
    private var sequence: Long = 0L

    private data class ScheduledEntry(
        val id: Long,
        val triggerTime: Long,
        val task: Runnable,
        @Volatile var cancelled: Boolean = false
    ) : Comparable<ScheduledEntry> {
        override fun compareTo(other: ScheduledEntry): Int {
            val timeCmp = triggerTime.compareTo(other.triggerTime)
            return if (timeCmp != 0) timeCmp else id.compareTo(other.id)
        }
    }

    private val queue = PriorityQueue<ScheduledEntry>()

    override fun currentTimeMillis(): Long = now

    @Synchronized
    override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
        val entry = ScheduledEntry(
            id = ++sequence,
            triggerTime = now + Math.max(0L, delayMillis),
            task = task
        )
        queue.add(entry)
        return Cancellable {
            synchronized(this@FakeClockAndScheduler) {
                entry.cancelled = true
            }
        }
    }

    /**
     * 将虚拟时间向前推进 [millis] 毫秒，并依序触发所有期间到期的调度任务
     */
    @Synchronized
    fun advanceTimeBy(millis: Long) {
        val targetTime = now + Math.max(0L, millis)
        while (queue.isNotEmpty() && queue.peek().triggerTime <= targetTime) {
            val entry = queue.poll()
            now = entry.triggerTime
            if (!entry.cancelled) {
                entry.task.run()
            }
        }
        now = targetTime
    }

    /**
     * 运行当前时刻（<= now）已到期但尚未执行的任务
     */
    @Synchronized
    fun runPending() {
        while (queue.isNotEmpty() && queue.peek().triggerTime <= now) {
            val entry = queue.poll()
            if (!entry.cancelled) {
                entry.task.run()
            }
        }
    }

    /**
     * 获取当前等待触发的未取消任务数量
     */
    @Synchronized
    fun getPendingCount(): Int {
        return queue.count { !it.cancelled }
    }

    /**
     * 清理所有剩余任务
     */
    @Synchronized
    fun clear() {
        queue.clear()
    }
}

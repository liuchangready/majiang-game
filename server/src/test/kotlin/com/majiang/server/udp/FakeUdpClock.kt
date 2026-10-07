package com.majiang.server.udp

import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpScheduler
import java.util.concurrent.CopyOnWriteArrayList

/**
 * 测试专用轻量虚拟时钟 (FakeUdpClock)
 *
 * 在测试源集中自实现，保证会话逻辑（超时、RTO、时间戳）确定性测试，
 * 绝不依赖外部线程睡眠或 protocol 测试源集内部类。
 */
class FakeUdpClock(private var currentMillis: Long = 1000L) : UdpClock {

    @Synchronized
    override fun currentTimeMillis(): Long = currentMillis

    @Synchronized
    fun advance(deltaMillis: Long) {
        require(deltaMillis >= 0L) { "deltaMillis must be >= 0: $deltaMillis" }
        currentMillis += deltaMillis
    }

    @Synchronized
    fun setTime(millis: Long) {
        currentMillis = millis
    }
}

/**
 * 测试专用轻量虚拟调度器 (FakeUdpScheduler)
 */
class FakeUdpScheduler(private val clock: FakeUdpClock) : UdpScheduler {

    data class ScheduledItem(
        val targetMillis: Long,
        val task: Runnable,
        @Volatile var cancelled: Boolean = false
    )

    private val scheduledTasks = CopyOnWriteArrayList<ScheduledItem>()

    override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
        val item = ScheduledItem(
            targetMillis = clock.currentTimeMillis() + delayMillis,
            task = task
        )
        scheduledTasks.add(item)
        return Cancellable {
            item.cancelled = true
            scheduledTasks.remove(item)
        }
    }

    /**
     * 运行当前时间及之前的所有已到期任务
     */
    fun runDueTasks() {
        val now = clock.currentTimeMillis()
        val toRun = scheduledTasks.filter { !it.cancelled && it.targetMillis <= now }
        scheduledTasks.removeAll(toRun)
        for (item in toRun) {
            if (!item.cancelled) {
                item.task.run()
            }
        }
    }

    /**
     * 推进时钟并执行期间所有到期任务
     */
    fun advanceTime(deltaMillis: Long) {
        clock.advance(deltaMillis)
        runDueTasks()
    }

    fun getPendingCount(): Int = scheduledTasks.count { !it.cancelled }
}

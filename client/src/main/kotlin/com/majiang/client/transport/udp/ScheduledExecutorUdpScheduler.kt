package com.majiang.client.transport.udp

import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpScheduler
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

/**
 * 基于 Java [ScheduledExecutorService] 的标准 UDP 调度器实现
 *
 * 采用后台守护单线程驱动定时重传与心跳 tick，可在销毁时优雅退出。
 */
class ScheduledExecutorUdpScheduler(
    private val executor: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "udp-scheduler").apply {
            isDaemon = true
        }
    }.apply {
        (this as? ScheduledThreadPoolExecutor)?.executeExistingDelayedTasksAfterShutdownPolicy = true
    }
) : UdpScheduler, AutoCloseable {

    val isShutdown: Boolean
        get() = executor.isShutdown

    override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
        val future = executor.schedule(task, delayMillis, TimeUnit.MILLISECONDS)
        return Cancellable {
            future.cancel(false)
        }
    }

    override fun close() {
        executor.shutdown()
    }
}

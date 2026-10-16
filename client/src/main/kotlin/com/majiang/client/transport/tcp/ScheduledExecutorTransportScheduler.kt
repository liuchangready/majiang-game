package com.majiang.client.transport.tcp

import com.majiang.transport.common.Cancellable
import com.majiang.transport.common.TransportScheduler
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

/**
 * 基于 Java [ScheduledExecutorService] 的标准传输调度器实现 (ScheduledExecutorTransportScheduler)
 *
 * 采用后台守护单线程驱动定时心跳探测、延迟重试与超时判死，可在销毁时优雅退出。
 */
class ScheduledExecutorTransportScheduler(
    private val executor: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "tcp-scheduler").apply {
            isDaemon = true
        }
    }.apply {
        (this as? ScheduledThreadPoolExecutor)?.executeExistingDelayedTasksAfterShutdownPolicy = true
    }
) : TransportScheduler, AutoCloseable {

    val isShutdown: Boolean
        get() = executor.isShutdown

    override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
        val future = executor.schedule(task, delayMillis.coerceAtLeast(0L), TimeUnit.MILLISECONDS)
        return Cancellable {
            future.cancel(false)
        }
    }

    override fun close() {
        executor.shutdown()
    }
}

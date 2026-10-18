package com.majiang.server.host

import java.util.ArrayDeque
import java.util.concurrent.ExecutorService
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadFactory
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * 共享线程池上的串行派发通道 (SerialDispatchChannel)
 *
 * 遵循 MR-07a 规范：
 * 1. 串行保证：通过原子单所有者锁 [drainWip] 保证同一时刻至多一个 drain 任务在底层执行器上排空队列，
 *    确保严格的 FIFO 顺序派发；
 * 2. 独立容量与有界缓冲：每个通道维护独立的条目上限 [maxQueuedItems] 与估算字节上限 [maxQueuedBytes]；
 *    当达到上限时采取"丢新不丢旧"策略，置 [needsRecovery] 为 true 并递增 [overflowCount]；
 * 3. 恢复契约：收到 [isFullSnapshotPredicate] 为 true 的基线条目处理后自动清除 [needsRecovery]；
 *    完整快照基线不含历史事件，丢帧后依赖客户端 cursor 缺口检测触发全量恢复（既有 MR-01 契约）；
 * 4. 调度拒绝防死锁：executor 拒绝执行时立即回退 [drainWip] 并递增 [scheduleRejectCount]，
 *    队列数据不丢失，由下一次入队重新触发调度；
 * 5. 公平性分片：每轮排空最多取出 [drainBatchSize] 条，本轮结束后若队列非空则向执行器重新提交排队，
 *    防止热通道长期霸占共享工作线程；
 * 6. 确定性关闭：close 标记关闭并清空未派发队列，入队立即拒绝，计入丢弃统计，绝不阻塞等待在途任务。
 *
 * @param name 通道标识名，用于日志与指标观察
 * @param executor 底层执行器（共享有界线程池或单通道专属执行器）
 * @param maxQueuedItems 最大队列条目数，0 或 Int.MAX_VALUE 表示无界（拟定默认值，可通过服务端配置调整）
 * @param maxQueuedBytes 最大队列估算字节数，0 或 Long.MAX_VALUE 表示无界（拟定默认值，可通过服务端配置调整）
 * @param drainBatchSize 每轮排空最大条目数（拟定默认值，可通过服务端配置调整）
 * @param byteEstimator 条目估算字节数函数
 * @param isFullSnapshotPredicate 判断条目是否为全量基线快照的判定函数
 * @param consumer 单条目消费回调（在 drain 任务中逐条执行，异常独立捕获）
 */
class SerialDispatchChannel<T>(
    val name: String,
    private val executor: ExecutorService,
    maxQueuedItems: Int = Int.MAX_VALUE,
    maxQueuedBytes: Long = Long.MAX_VALUE,
    drainBatchSize: Int = 16,
    private val byteEstimator: (T) -> Long = { 0L },
    private val isFullSnapshotPredicate: (T) -> Boolean = { false },
    private val consumer: (T) -> Unit
) : AutoCloseable {

    private val effectiveMaxItems: Int = if (maxQueuedItems <= 0) Int.MAX_VALUE else maxQueuedItems
    private val effectiveMaxBytes: Long = if (maxQueuedBytes <= 0L) Long.MAX_VALUE else maxQueuedBytes
    private val effectiveBatchSize: Int = if (drainBatchSize <= 0) 16 else drainBatchSize

    private val lock = Any()
    private val isClosed = AtomicBoolean(false)
    private val drainWip = AtomicBoolean(false)
    private val _needsRecovery = AtomicBoolean(false)

    private val _overflowCount = AtomicLong(0L)
    private val _scheduleRejectCount = AtomicLong(0L)
    private val _closeDiscardedCount = AtomicLong(0L)
    private val _maxWaitNanos = AtomicLong(0L)

    private class Entry<T>(val item: T, val bytes: Long, val enqueuedNanos: Long)
    private val queue = ArrayDeque<Entry<T>>()
    private var currentBytes: Long = 0L

    val closed: Boolean get() = isClosed.get()
    val needsRecovery: Boolean get() = _needsRecovery.get()
    val overflowCount: Long get() = _overflowCount.get()
    val scheduleRejectCount: Long get() = _scheduleRejectCount.get()
    val closeDiscardedCount: Long get() = _closeDiscardedCount.get()
    val maxWaitTimeMillis: Double get() = _maxWaitNanos.get() / 1_000_000.0

    val queuedItems: Int
        get() = synchronized(lock) { queue.size }

    val queuedBytes: Long
        get() = synchronized(lock) { currentBytes }

    val capacityItems: Int get() = effectiveMaxItems
    val capacityBytes: Long get() = effectiveMaxBytes

    private val drainRunnable = Runnable { runDrainLoop() }

    /**
     * 将条目入队并触发异步排空
     *
     * @return true 表示成功入队或触发调度；false 表示已关闭或容量超限被丢弃
     */
    fun enqueue(item: T): Boolean {
        val bytes = byteEstimator(item).coerceAtLeast(0L)
        val nowNanos = System.nanoTime()
        var shouldSchedule = false

        synchronized(lock) {
            if (isClosed.get()) {
                return false
            }
            if (queue.size >= effectiveMaxItems || (currentBytes + bytes > effectiveMaxBytes && queue.isNotEmpty())) {
                _overflowCount.incrementAndGet()
                _needsRecovery.set(true)
                return false
            }
            queue.addLast(Entry(item, bytes, nowNanos))
            currentBytes += bytes

            if (!drainWip.get()) {
                drainWip.set(true)
                shouldSchedule = true
            }
        }

        if (shouldSchedule) {
            scheduleDrain()
        }
        return true
    }

    private fun scheduleDrain() {
        try {
            executor.execute(drainRunnable)
        } catch (_: RejectedExecutionException) {
            synchronized(lock) {
                drainWip.set(false)
            }
            _scheduleRejectCount.incrementAndGet()
        }
    }

    private fun runDrainLoop() {
        val batch = ArrayList<Entry<T>>(effectiveBatchSize)
        synchronized(lock) {
            var count = 0
            while (count < effectiveBatchSize && queue.isNotEmpty()) {
                val entry = queue.removeFirst()
                currentBytes -= entry.bytes
                batch.add(entry)
                count++
            }
        }

        val nowNanos = System.nanoTime()
        for (entry in batch) {
            val waitNanos = (nowNanos - entry.enqueuedNanos).coerceAtLeast(0L)
            updateMaxWait(waitNanos)
            try {
                consumer(entry.item)
                if (isFullSnapshotPredicate(entry.item)) {
                    _needsRecovery.set(false)
                }
            } catch (_: Throwable) {
                // 逐条捕获，不中断当前批次其他条目派发
            }
        }

        var shouldContinue = false
        synchronized(lock) {
            if (isClosed.get() || queue.isEmpty()) {
                drainWip.set(false)
            } else {
                shouldContinue = true
            }
        }

        if (shouldContinue) {
            scheduleDrain()
        }
    }

    private fun updateMaxWait(waitNanos: Long) {
        var current = _maxWaitNanos.get()
        while (waitNanos > current) {
            if (_maxWaitNanos.compareAndSet(current, waitNanos)) break
            current = _maxWaitNanos.get()
        }
    }

    override fun close() {
        val discarded: Int
        synchronized(lock) {
            if (!isClosed.compareAndSet(false, true)) {
                return
            }
            discarded = queue.size
            queue.clear()
            currentBytes = 0L
            _closeDiscardedCount.addAndGet(discarded.toLong())
            drainWip.set(false)
        }
    }
}

/**
 * 宿主派发与完成共享线程池组 (HostDispatchPools)
 *
 * 遵循 MR-07a 规范：
 * 1. 派发线程池 [dispatchExecutor]：固定线程数 [dispatchThreads]，有界阻塞队列容量 [dispatchQueueCapacity]，
 *    饱和策略采用 AbortPolicy（拒绝处理交由 SerialDispatchChannel 回退，严禁 CallerRuns）；
 * 2. 完成线程池 [completionExecutor]：固定线程数 [completionThreads]，有界阻塞队列容量 [completionQueueCapacity]，
 *    饱和策略采用 AbortPolicy（拒绝时由 ServerTableHost 内联完成兜底）；
 * 3. 线程命名：严格采用 "TcpTableServer-Dispatch-N" 与 "TcpTableServer-Completion-N"；
 * 4. 有界平滑关闭：在给定期限 [closeDeadlineMillis] 内依次执行 shutdown、awaitTermination 与 shutdownNow，
 *    并统计暴露残留任务数，保证在期限内返回。
 *
 * @param dispatchThreads 派发池固定线程数（拟定默认值 8，可通过服务端配置调整）
 * @param dispatchQueueCapacity 派发池队列容量（拟定默认值 1024，可通过服务端配置调整）
 * @param completionThreads 完成池固定线程数（拟定默认值 4，可通过服务端配置调整）
 * @param completionQueueCapacity 完成池队列容量（拟定默认值 1024，可通过服务端配置调整）
 * @param closeDeadlineMillis 关闭最大等待期限（毫秒，拟定默认值 1000，可通过服务端配置调整）
 * @param seatChannelCapacityItems 席位通道容量（条目数，拟定默认值 64，可通过服务端配置调整）
 * @param seatChannelCapacityBytes 席位通道容量（字节数，拟定默认值 1 MiB，可通过服务端配置调整）
 * @param publicChannelCapacityItems 公开通道容量（条目数，拟定默认值 64，可通过服务端配置调整）
 * @param publicChannelCapacityBytes 公开通道容量（字节数，拟定默认值 1 MiB，可通过服务端配置调整）
 * @param channelDrainBatchSize 通道单轮排空批次大小（拟定默认值 16，可通过服务端配置调整）
 */
class HostDispatchPools(
    val dispatchThreads: Int = 8,
    val dispatchQueueCapacity: Int = 1024,
    val completionThreads: Int = 4,
    val completionQueueCapacity: Int = 1024,
    val closeDeadlineMillis: Long = 1000L,
    val seatChannelCapacityItems: Int = 64,
    val seatChannelCapacityBytes: Long = 1024L * 1024L,
    val publicChannelCapacityItems: Int = 64,
    val publicChannelCapacityBytes: Long = 1024L * 1024L,
    val channelDrainBatchSize: Int = 16
) : AutoCloseable {

    private val isClosed = AtomicBoolean(false)
    val closed: Boolean get() = isClosed.get()

    private val dispatchThreadCounter = AtomicInteger(1)
    private val completionThreadCounter = AtomicInteger(1)

    val dispatchExecutor: ThreadPoolExecutor = ThreadPoolExecutor(
        dispatchThreads,
        dispatchThreads,
        0L,
        TimeUnit.MILLISECONDS,
        LinkedBlockingQueue(dispatchQueueCapacity),
        ThreadFactory { r ->
            Thread(r, "TcpTableServer-Dispatch-${dispatchThreadCounter.getAndIncrement()}").apply {
                isDaemon = true
            }
        },
        ThreadPoolExecutor.AbortPolicy()
    )

    val completionExecutor: ThreadPoolExecutor = ThreadPoolExecutor(
        completionThreads,
        completionThreads,
        0L,
        TimeUnit.MILLISECONDS,
        LinkedBlockingQueue(completionQueueCapacity),
        ThreadFactory { r ->
            Thread(r, "TcpTableServer-Completion-${completionThreadCounter.getAndIncrement()}").apply {
                isDaemon = true
            }
        },
        ThreadPoolExecutor.AbortPolicy()
    )

    /**
     * 有界关闭线程池组，在指定期限内完成，并返回残留任务总数
     *
     * @param deadlineMillis 最大等待超时时间（毫秒）
     * @return 无法被排空执行的残留任务总数
     */
    fun close(deadlineMillis: Long = closeDeadlineMillis): Int {
        if (!isClosed.compareAndSet(false, true)) {
            return 0
        }
        val effectiveDeadline = deadlineMillis.coerceAtLeast(10L)
        val deadlineNanos = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(effectiveDeadline)

        fun remainingNanos(): Long = (deadlineNanos - System.nanoTime()).coerceAtLeast(0L)

        dispatchExecutor.shutdown()
        completionExecutor.shutdown()

        try {
            val waitNanos = remainingNanos() / 2
            if (!dispatchExecutor.awaitTermination(waitNanos, TimeUnit.NANOSECONDS)) {
                dispatchExecutor.shutdownNow()
            }
        } catch (_: InterruptedException) {
            dispatchExecutor.shutdownNow()
            Thread.currentThread().interrupt()
        }

        try {
            val waitNanos = remainingNanos()
            if (!completionExecutor.awaitTermination(waitNanos, TimeUnit.NANOSECONDS)) {
                completionExecutor.shutdownNow()
            }
        } catch (_: InterruptedException) {
            completionExecutor.shutdownNow()
            Thread.currentThread().interrupt()
        }

        val unexecutedDispatch = dispatchExecutor.shutdownNow().size
        val unexecutedCompletion = completionExecutor.shutdownNow().size
        return unexecutedDispatch + unexecutedCompletion + dispatchExecutor.activeCount + completionExecutor.activeCount
    }

    override fun close() {
        close(closeDeadlineMillis)
    }
}


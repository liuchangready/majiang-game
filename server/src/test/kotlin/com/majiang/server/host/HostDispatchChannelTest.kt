package com.majiang.server.host

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.Collections
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * SerialDispatchChannel 单元测试与契约验证套件 (MR-07a / 任务 E)
 *
 * 覆盖 R07a-T1 ~ R07a-T4:
 * 1. 单通道 FIFO、多线程并发入队无重复无丢失
 * 2. 入队与 close 竞争：所有入队成功项要么全部派发，要么计入 closeDiscardedCount，无第三态
 * 3. 调度拒绝回退：executor 抛出 RejectedExecutionException 时，drainWip 确定性回退并计数，后续入队重新拉起派发无死锁
 * 4. 公平性分片：drainBatchSize 切割后重新向共享池排队，热通道不霸占工作线程，冷通道有限时间内推进
 * 5. 溢出策略：丢新不丢旧、needsRecovery 置位与清除契约
 */
@Timeout(30)
class HostDispatchChannelTest {

    private data class TestMessage(
        val seq: Int,
        val isFullSnapshot: Boolean = false,
        val payloadSize: Long = 100L
    )

    @Test
    fun `single channel maintains strict FIFO order and concurrent enqueues without drops or duplicates`() {
        val executor = Executors.newFixedThreadPool(2)
        try {
            val received = CopyOnWriteArrayList<Int>()
            val totalItems = 1000
            val allDelivered = CountDownLatch(totalItems)

            val channel = SerialDispatchChannel<TestMessage>(
                name = "test-fifo",
                executor = executor,
                drainBatchSize = 16,
                consumer = { msg ->
                    received.add(msg.seq)
                    allDelivered.countDown()
                }
            )

            // 4 个线程并发提交单调序号数据
            val threads = 4
            val itemsPerThread = totalItems / threads
            val startLatch = CountDownLatch(1)
            val workerThreads = (0 until threads).map { t ->
                Thread {
                    startLatch.await()
                    for (i in 0 until itemsPerThread) {
                        val seq = t * itemsPerThread + i
                        assertTrue(channel.enqueue(TestMessage(seq = seq)))
                    }
                }
            }
            workerThreads.forEach { it.start() }
            startLatch.countDown()
            workerThreads.forEach { it.join() }

            assertTrue(allDelivered.await(10, TimeUnit.SECONDS), "All 1000 items must be delivered")
            assertEquals(totalItems, received.size)

            // 校验每个线程各自发送的子序列在接收端严格单调递增 (Per-producer FIFO)
            for (t in 0 until threads) {
                val threadItems = received.filter { it in (t * itemsPerThread until (t + 1) * itemsPerThread) }
                assertEquals(itemsPerThread, threadItems.size)
                for (i in 1 until threadItems.size) {
                    assertTrue(threadItems[i] > threadItems[i - 1], "Producer $t order must be monotonic")
                }
            }
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun `enqueue and close race guarantees all items either delivered or counted as close-discarded`() {
        val executor = Executors.newFixedThreadPool(2)
        try {
            val totalProduced = AtomicInteger(0)
            val totalEnqueued = AtomicInteger(0)
            val delivered = AtomicInteger(0)
            val holdDelivery = CountDownLatch(1)

            val channel = SerialDispatchChannel<TestMessage>(
                name = "test-close-race",
                executor = executor,
                drainBatchSize = 8,
                consumer = {
                    holdDelivery.await(5, TimeUnit.SECONDS)
                    delivered.incrementAndGet()
                }
            )

            val producerThreads = 4
            val startLatch = CountDownLatch(1)
            val doneProducing = CountDownLatch(producerThreads)

            val workers = (0 until producerThreads).map {
                Thread {
                    startLatch.await()
                    for (i in 0 until 500) {
                        totalProduced.incrementAndGet()
                        if (channel.enqueue(TestMessage(seq = i))) {
                            totalEnqueued.incrementAndGet()
                        }
                    }
                    doneProducing.countDown()
                }
            }
            workers.forEach { it.start() }
            startLatch.countDown()

            // 让部分数据入队并开始排队后立即调用 close
            Thread.yield()
            channel.close()
            assertTrue(channel.closed)

            // close 后再尝试入队，必须返回 false
            assertFalse(channel.enqueue(TestMessage(seq = 9999)))

            // 释放派发门闩，等待在途任务完成与入队线程退出
            holdDelivery.countDown()
            workers.forEach { it.join() }

            val discardedOnClose = channel.closeDiscardedCount
            val deliveredCount = delivered.get()
            val enqueuedCount = totalEnqueued.get()

            // 核心契约：已成功入队的条目，要么被正常消费派发，要么计入 closeDiscardedCount，绝对无第三态
            assertEquals(
                enqueuedCount.toLong(),
                deliveredCount.toLong() + discardedOnClose,
                "Enqueued items must equal (delivered + discardedOnClose)"
            )
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun `executor rejection falls back wip, increments reject counter and retries on subsequent enqueue`() {
        // 自定义可控抛出 RejectedExecutionException 的伪执行器
        val shouldReject = AtomicBoolean(true)
        val realExecutor = Executors.newSingleThreadExecutor()

        val mockExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    if (shouldReject.get()) {
                        throw RejectedExecutionException("Injected rejection for testing")
                    } else {
                        realExecutor.execute(args[0] as Runnable)
                        null
                    }
                } else if (method.name == "shutdownNow" || method.name == "shutdown") {
                    realExecutor.shutdownNow()
                } else {
                    null
                }
            }
        )

        try {
            val delivered = CopyOnWriteArrayList<Int>()
            val deliveryLatch = CountDownLatch(3)

            val channel = SerialDispatchChannel<TestMessage>(
                name = "test-reject",
                executor = mockExecutor,
                drainBatchSize = 16,
                consumer = { msg ->
                    delivered.add(msg.seq)
                    deliveryLatch.countDown()
                }
            )

            // 1. 处于拒绝模式，入队应成功进入队列但调度被拒绝
            assertTrue(channel.enqueue(TestMessage(seq = 1)))
            assertTrue(channel.scheduleRejectCount >= 1L, "Reject count must be incremented")
            assertEquals(1, channel.queuedItems, "Item must remain in queue")
            assertEquals(0, delivered.size, "Nothing delivered yet due to rejection")

            // 2. 解除拒绝，再次入队新条目，此时由于 wip 已成功回退，新入队将重新触发排空
            shouldReject.set(false)
            assertTrue(channel.enqueue(TestMessage(seq = 2)))
            assertTrue(channel.enqueue(TestMessage(seq = 3)))

            assertTrue(deliveryLatch.await(5, TimeUnit.SECONDS), "All items including rejected one must be delivered")
            assertEquals(listOf(1, 2, 3), delivered, "Delivery order must remain strictly sequential")
            assertEquals(0, channel.queuedItems, "Queue must be empty after drain")
        } finally {
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `fairness between hot channel and cold channel ensures drainBatchSize prevents starvation`() {
        // 使用单线程执行器模拟共享资源饱和场景
        val sharedExecutor = Executors.newSingleThreadExecutor()
        try {
            val hotProcessed = AtomicInteger(0)
            val coldDeliveredLatch = CountDownLatch(1)
            val hotProcessedBeforeCold = AtomicInteger(0)

            val batchSize = 10

            val hotChannel = SerialDispatchChannel<TestMessage>(
                name = "hot",
                executor = sharedExecutor,
                drainBatchSize = batchSize,
                consumer = {
                    hotProcessed.incrementAndGet()
                }
            )

            val coldChannel = SerialDispatchChannel<TestMessage>(
                name = "cold",
                executor = sharedExecutor,
                drainBatchSize = batchSize,
                consumer = {
                    hotProcessedBeforeCold.set(hotProcessed.get())
                    coldDeliveredLatch.countDown()
                }
            )

            // 热通道塞入 500 条数据
            for (i in 0 until 500) {
                hotChannel.enqueue(TestMessage(seq = i))
            }

            // 冷通道塞入 1 条数据
            coldChannel.enqueue(TestMessage(seq = 999))

            // 冷通道必须在短时间内得到调度，且热通道处理的条数不能超过有限批次（证明热通道排空被分片让出线程）
            assertTrue(coldDeliveredLatch.await(5, TimeUnit.SECONDS), "Cold channel must not be starved")
            assertTrue(
                hotProcessedBeforeCold.get() < 500,
                "Hot channel must have yielded before processing all items (processed: ${hotProcessedBeforeCold.get()})"
            )
        } finally {
            sharedExecutor.shutdownNow()
        }
    }

    @Test
    fun `overflow strategy drops newest items, flags needsRecovery, and full snapshot clears needsRecovery`() {
        val manualTasks = CopyOnWriteArrayList<Runnable>()
        val manualExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    manualTasks.add(args[0] as Runnable)
                    null
                } else if (method.name == "shutdownNow" || method.name == "shutdown") {
                    null
                } else {
                    null
                }
            }
        )

        val consumed = CopyOnWriteArrayList<Int>()
        val capacity = 5
        val channel = SerialDispatchChannel<TestMessage>(
            name = "test-overflow",
            executor = manualExecutor,
            maxQueuedItems = capacity,
            drainBatchSize = 16,
            isFullSnapshotPredicate = { it.isFullSnapshot },
            consumer = { msg ->
                consumed.add(msg.seq)
            }
        )

        // 1. 手动泵未放行调度时，连续入队 5 条填满容量（无后台异步竞态）
        for (i in 1..5) {
            assertTrue(channel.enqueue(TestMessage(seq = i)))
        }
        assertEquals(5, channel.queuedItems)
        assertEquals(0L, channel.overflowCount)
        assertFalse(channel.needsRecovery)

        // 2. 再次灌入 2 条，超过容量限制 5，必须返回 false（丢弃新条目，不是丢旧条目）
        assertFalse(channel.enqueue(TestMessage(seq = 6)))
        assertFalse(channel.enqueue(TestMessage(seq = 7)))

        assertEquals(2L, channel.overflowCount)
        assertTrue(channel.needsRecovery, "needsRecovery must be set upon overflow")
        assertEquals(5, channel.queuedItems)

        // 3. 执行手动泵排空任务，消费旧数据
        while (manualTasks.isNotEmpty()) {
            manualTasks.removeAt(0).run()
        }
        assertEquals(listOf(1, 2, 3, 4, 5), consumed, "Old items must not be dropped; newest items were dropped")
        assertEquals(0, channel.queuedItems)
        assertTrue(channel.needsRecovery, "needsRecovery remains true until full snapshot baseline")

        // 4. 在同一条已置位 needsRecovery 的通道上投递普通条目与全量基线条目
        assertTrue(channel.enqueue(TestMessage(seq = 10, isFullSnapshot = false)))
        assertTrue(channel.needsRecovery, "Ordinary message must not clear needsRecovery")

        assertTrue(channel.enqueue(TestMessage(seq = 11, isFullSnapshot = true)))

        // 5. 驱动手动泵消费
        while (manualTasks.isNotEmpty()) {
            manualTasks.removeAt(0).run()
        }
        assertEquals(listOf(1, 2, 3, 4, 5, 10, 11), consumed)
        assertFalse(channel.needsRecovery, "Full snapshot baseline must clear needsRecovery")
    }
}

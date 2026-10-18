package com.majiang.server.host

import com.majiang.access.PlayerUpdate
import com.majiang.transport.api.ProtocolPublicUpdate
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * MR-07a SerialDispatchChannel 调度拒绝有界重试、显式失败与并发竞态测试
 */
@Timeout(30)
class Mr07aDispatchRejectionTest {

    private data class Msg(val seq: Int)

    @Test
    fun `single trailing message rejected on schedule is eventually delivered when pool recovers without new enqueue`() {
        val shouldReject = AtomicBoolean(true)
        val realExecutor = Executors.newSingleThreadExecutor()

        val mockExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    if (shouldReject.get()) {
                        throw RejectedExecutionException("Injected temporary saturation")
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

        val delivered = CopyOnWriteArrayList<Int>()
        val deliveryLatch = CountDownLatch(1)

        val channel = SerialDispatchChannel<Msg>(
            name = "test-trailing-retry",
            executor = mockExecutor,
            maxScheduleRetries = 10,
            retryDelayMillis = 10L,
            consumer = { msg ->
                delivered.add(msg.seq)
                deliveryLatch.countDown()
            }
        )

        try {
            // 1. 在饱和状态下入队单条末尾消息
            assertTrue(channel.enqueue(Msg(42)))
            assertTrue(channel.scheduleRejectCount >= 1L, "Reject count must be incremented")
            assertEquals(0, delivered.size, "Message cannot be delivered immediately while rejected")

            // 2. 模拟线程池容量释放，恢复接纳能力；绝不调用新的 enqueue
            Thread.sleep(30)
            shouldReject.set(false)

            // 3. 等待重试机制自动将滞留的唯一末尾消息送达
            assertTrue(deliveryLatch.await(5, TimeUnit.SECONDS), "Trailing message must be delivered after pool recovery without new enqueue")
            assertEquals(listOf(42), delivered)
            assertEquals(0, channel.queuedItems, "Queue must be empty after delivery")
            assertTrue(channel.scheduleRetryCount >= 1L, "Schedule retry must have been attempted")
            assertFalse(channel.needsRecovery, "No recovery needed on successful delivery")
        } finally {
            channel.close()
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `single trailing message explicitly fails and sets needsRecovery when retries are exhausted`() {
        val alwaysRejectExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, _ ->
                if (method.name == "execute") {
                    throw RejectedExecutionException("Permanently exhausted pool")
                }
                null
            }
        )

        val delivered = CopyOnWriteArrayList<Int>()
        val channel = SerialDispatchChannel<Msg>(
            name = "test-exhausted-failure",
            executor = alwaysRejectExecutor,
            maxScheduleRetries = 3,
            retryDelayMillis = 5L,
            consumer = { msg -> delivered.add(msg.seq) }
        )

        try {
            assertTrue(channel.enqueue(Msg(99)))
            // 等待重试上限耗尽 (3 次 * 5ms + 缓冲)
            var waited = 0
            while (channel.scheduleFailureCount == 0L && waited < 100) {
                Thread.sleep(10)
                waited++
            }

            assertTrue(channel.scheduleFailureCount >= 1L, "Must record explicit schedule failure")
            assertTrue(channel.needsRecovery, "needsRecovery must be true after schedule failure")
            assertEquals(0, channel.queuedItems, "Queue must be discarded on explicit failure to avoid indefinite leak")
            assertEquals(0, delivered.size)
        } finally {
            channel.close()
        }
    }

    @Test
    fun `concurrent enqueues and rejection race preserves single drain owner and delivers all admitted items`() {
        val rejectCounter = AtomicInteger(0)
        val realExecutor = Executors.newFixedThreadPool(2)

        val intermittentExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    // 前 3 次调度拒绝，后续全部正常执行
                    if (rejectCounter.getAndIncrement() < 3) {
                        throw RejectedExecutionException("Intermittent saturation")
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

        val totalProducers = 4
        val itemsPerProducer = 100
        val totalExpected = totalProducers * itemsPerProducer
        val delivered = CopyOnWriteArrayList<Int>()
        val allDoneLatch = CountDownLatch(totalExpected)

        val channel = SerialDispatchChannel<Msg>(
            name = "test-concurrent-reject-race",
            executor = intermittentExecutor,
            maxScheduleRetries = 10,
            retryDelayMillis = 5L,
            consumer = { msg ->
                delivered.add(msg.seq)
                allDoneLatch.countDown()
            }
        )

        try {
            val startLatch = CountDownLatch(1)
            val threads = (0 until totalProducers).map { p ->
                Thread {
                    startLatch.await()
                    for (i in 0 until itemsPerProducer) {
                        channel.enqueue(Msg(p * itemsPerProducer + i))
                    }
                }
            }
            threads.forEach { it.start() }
            startLatch.countDown()
            threads.forEach { it.join() }

            assertTrue(allDoneLatch.await(10, TimeUnit.SECONDS), "All items must be delivered despite intermittent rejection")
            assertEquals(totalExpected, delivered.size)

            // 验证每个生产者的子序列仍严格保序
            for (p in 0 until totalProducers) {
                val sub = delivered.filter { it in (p * itemsPerProducer until (p + 1) * itemsPerProducer) }
                assertEquals(itemsPerProducer, sub.size)
                for (i in 1 until sub.size) {
                    assertTrue(sub[i] > sub[i - 1], "Producer $p sub-sequence must maintain strictly increasing order")
                }
            }
        } finally {
            channel.close()
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `initial baseline dispatch exhaustion recovers automatically with latest full snapshot when pool restored for seat subscriber`() {
        val shouldReject = AtomicBoolean(true)
        val realExecutor = Executors.newCachedThreadPool()
        val mockExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    if (shouldReject.get()) {
                        throw RejectedExecutionException("Injected saturation")
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

        val host = ServerTableHost(sharedDispatch = mockExecutor)
        val received = CopyOnWriteArrayList<PlayerUpdate>()

        try {
            val sub = host.subscribeSeat(0) { update ->
                received.add(update)
            }

            // 等待重试上限耗尽 (scheduleFailureCount >= 1L 且 needsRecovery 为 true)
            val deadline = System.currentTimeMillis() + 5000L
            while (host.seatChannels[0].scheduleFailureCount == 0L && System.currentTimeMillis() < deadline) {
                Thread.sleep(10)
            }
            assertTrue(host.seatChannels[0].scheduleFailureCount >= 1L, "Schedule retries must have exhausted")
            assertTrue(host.seatChannels[0].needsRecovery, "Seat channel must indicate recovery needed")
            assertTrue(received.isEmpty(), "No updates should be delivered while rejected")

            // 恢复派发线程池；期间不调用任何新业务动作或 tick
            shouldReject.set(false)

            // 验证：受控恢复机制主动向席位 0 补发最新全量基线快照
            val recoverDeadline = System.currentTimeMillis() + 5000L
            while (received.isEmpty() && System.currentTimeMillis() < recoverDeadline) {
                Thread.sleep(10)
            }
            assertTrue(received.isNotEmpty(), "Seat subscriber must reliably receive latest full baseline after pool recovery without new game events")
            val baseline = received.first()
            assertEquals(0L, baseline.snapshot.cursor)
            assertEquals(0L, baseline.fromCursor)
            assertFalse(host.seatChannels[0].needsRecovery, "needsRecovery must be cleared after full snapshot baseline delivered")

            // 验证恢复后后续正常业务更新可继续下发
            val advanceLatch = CountDownLatch(1)
            val subMonitor = host.subscribeSeat(0) { if (it.snapshot.cursor > 0L) advanceLatch.countDown() }
            host.pause().get(5, TimeUnit.SECONDS)
            assertTrue(advanceLatch.await(5, TimeUnit.SECONDS), "Subsequent updates must dispatch normally after recovery")
            subMonitor.close()
            sub.close()
        } finally {
            host.close()
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `trailing update dispatch exhaustion recovers automatically with latest full snapshot when pool restored for seat subscriber`() {
        val shouldReject = AtomicBoolean(false)
        val realExecutor = Executors.newCachedThreadPool()
        val mockExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    if (shouldReject.get()) {
                        throw RejectedExecutionException("Injected saturation")
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

        val host = ServerTableHost(sharedDispatch = mockExecutor)
        val received = CopyOnWriteArrayList<PlayerUpdate>()

        try {
            // 1. 正常建立连接与初始基线
            val sub = host.subscribeSeat(0) { update ->
                received.add(update)
            }
            val initDeadline = System.currentTimeMillis() + 5000L
            while (received.isEmpty() && System.currentTimeMillis() < initDeadline) {
                Thread.sleep(10)
            }
            assertEquals(1, received.size)
            assertEquals(0L, received[0].snapshot.cursor)

            // 2. 注入饱和状态，触发最后一条业务更新（pause）
            shouldReject.set(true)
            host.pause().get(5, TimeUnit.SECONDS)

            // 等待重试上限耗尽清空末尾更新并标记 needsRecovery
            val deadline = System.currentTimeMillis() + 5000L
            while (host.seatChannels[0].scheduleFailureCount == 0L && System.currentTimeMillis() < deadline) {
                Thread.sleep(10)
            }
            assertTrue(host.seatChannels[0].scheduleFailureCount >= 1L)
            assertTrue(host.seatChannels[0].needsRecovery)
            assertEquals(1, received.size, "Trailing update must have been dropped upon exhaustion")

            // 3. 恢复派发线程池；期间不调用任何新业务动作或 tick
            shouldReject.set(false)

            // 4. 等待恢复调度补发最新全量状态
            val recoverDeadline = System.currentTimeMillis() + 5000L
            while (received.size < 2 && System.currentTimeMillis() < recoverDeadline) {
                Thread.sleep(10)
            }
            assertTrue(received.size >= 2, "Subscriber must receive latest full state recovering dropped trailing update")
            val recoveredUpdate = received.last()
            assertTrue(recoveredUpdate.snapshot.paused, "Must reflect latest paused state from dropped update")
            assertEquals(recoveredUpdate.snapshot.cursor, recoveredUpdate.fromCursor, "Full snapshot baseline must have fromCursor == snapshot.cursor")
            assertFalse(host.seatChannels[0].needsRecovery, "needsRecovery must be cleared")
            sub.close()
        } finally {
            host.close()
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `initial baseline dispatch exhaustion recovers automatically with latest full snapshot when pool restored for public subscriber`() {
        val shouldReject = AtomicBoolean(true)
        val realExecutor = Executors.newCachedThreadPool()
        val mockExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    if (shouldReject.get()) {
                        throw RejectedExecutionException("Injected saturation")
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

        val host = ServerTableHost(sharedDispatch = mockExecutor)
        val received = CopyOnWriteArrayList<ProtocolPublicUpdate>()

        try {
            val sub = host.subscribePublic { update ->
                received.add(update)
            }

            // 等待重试上限耗尽 (scheduleFailureCount >= 1L 且 needsRecovery 为 true)
            val deadline = System.currentTimeMillis() + 5000L
            while (host.publicChannel.scheduleFailureCount == 0L && System.currentTimeMillis() < deadline) {
                Thread.sleep(10)
            }
            assertTrue(host.publicChannel.scheduleFailureCount >= 1L)
            assertTrue(host.publicChannel.needsRecovery)
            assertTrue(received.isEmpty())

            // 恢复派发线程池；期间不产生新业务更新
            shouldReject.set(false)

            // 验证恢复机制主动向公开订阅者补发最新全量基线快照
            val recoverDeadline = System.currentTimeMillis() + 5000L
            while (received.isEmpty() && System.currentTimeMillis() < recoverDeadline) {
                Thread.sleep(10)
            }
            assertTrue(received.isNotEmpty(), "Public subscriber must reliably receive latest full baseline after pool recovery")
            val baseline = received.first()
            assertTrue(baseline.isFullSnapshot, "Recovery update must be marked as isFullSnapshot=true")
            assertEquals(baseline.snapshot.cursor, baseline.fromCursor)
            assertFalse(host.publicChannel.needsRecovery)
            sub.close()
        } finally {
            host.close()
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `trailing update dispatch exhaustion recovers automatically with latest full snapshot when pool restored for public subscriber`() {
        val shouldReject = AtomicBoolean(false)
        val realExecutor = Executors.newCachedThreadPool()
        val mockExecutor = ExecutorService::class.java.cast(
            java.lang.reflect.Proxy.newProxyInstance(
                ExecutorService::class.java.classLoader,
                arrayOf(ExecutorService::class.java)
            ) { _, method, args ->
                if (method.name == "execute") {
                    if (shouldReject.get()) {
                        throw RejectedExecutionException("Injected saturation")
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

        val host = ServerTableHost(sharedDispatch = mockExecutor)
        val received = CopyOnWriteArrayList<ProtocolPublicUpdate>()

        try {
            // 1. 正常建立连接与初始基线
            val sub = host.subscribePublic { update ->
                received.add(update)
            }
            val initDeadline = System.currentTimeMillis() + 5000L
            while (received.isEmpty() && System.currentTimeMillis() < initDeadline) {
                Thread.sleep(10)
            }
            assertEquals(1, received.size)

            // 2. 注入饱和状态，触发最后一条业务更新（pause）
            shouldReject.set(true)
            host.pause().get(5, TimeUnit.SECONDS)

            // 等待重试上限耗尽清空末尾更新并标记 needsRecovery
            val deadline = System.currentTimeMillis() + 5000L
            while (host.publicChannel.scheduleFailureCount == 0L && System.currentTimeMillis() < deadline) {
                Thread.sleep(10)
            }
            assertTrue(host.publicChannel.scheduleFailureCount >= 1L)
            assertTrue(host.publicChannel.needsRecovery)
            assertEquals(1, received.size)

            // 3. 恢复派发线程池；期间不调用任何新业务动作或 tick
            shouldReject.set(false)

            // 4. 等待恢复调度补发最新全量状态
            val recoverDeadline = System.currentTimeMillis() + 5000L
            while (received.size < 2 && System.currentTimeMillis() < recoverDeadline) {
                Thread.sleep(10)
            }
            assertTrue(received.size >= 2, "Public subscriber must receive latest full state recovering dropped trailing update")
            val recoveredUpdate = received.last()
            assertTrue(recoveredUpdate.snapshot.paused)
            assertTrue(recoveredUpdate.isFullSnapshot)
            assertFalse(host.publicChannel.needsRecovery)
            sub.close()
        } finally {
            host.close()
            realExecutor.shutdownNow()
        }
    }

    @Test
    fun `single oversized initial baseline is admitted on empty channel and not dropped permanently`() {
        // 配置极小容量（500B），初始快照结构估算远大于 2000B
        val host = ServerTableHost(
            seatChannelCapacityBytes = 500L,
            publicChannelCapacityBytes = 500L
        )
        val seatReceived = CopyOnWriteArrayList<PlayerUpdate>()
        val publicReceived = CopyOnWriteArrayList<ProtocolPublicUpdate>()

        try {
            val seatSub = host.subscribeSeat(0) { seatReceived.add(it) }
            val pubSub = host.subscribePublic { publicReceived.add(it) }

            val deadline = System.currentTimeMillis() + 5000L
            while ((seatReceived.isEmpty() || publicReceived.isEmpty()) && System.currentTimeMillis() < deadline) {
                Thread.sleep(10)
            }

            assertTrue(seatReceived.isNotEmpty(), "Oversized initial seat baseline must be admitted and delivered on empty queue")
            assertTrue(publicReceived.isNotEmpty(), "Oversized initial public baseline must be admitted and delivered on empty queue")
            assertFalse(host.seatChannels[0].needsRecovery)
            assertFalse(host.publicChannel.needsRecovery)

            seatSub.close()
            pubSub.close()
        } finally {
            host.close()
        }
    }
}

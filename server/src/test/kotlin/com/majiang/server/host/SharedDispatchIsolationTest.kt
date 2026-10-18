package com.majiang.server.host

import com.majiang.access.ReceiptStatus
import com.majiang.match.MatchConfig
import com.majiang.server.room.SharedRoom
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.SeatController
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * 共享派发与完成线程池跨房间隔离性测试套件 (MR-07a / 任务 E)
 *
 * 覆盖 R07a-T6 ~ R07a-T8:
 * 1. 慢回调隔离：房 A 回调阻塞，房 B 更新在预算时间内继续派发推进
 * 2. Completion 独立预算：Dispatch 池被完全阻塞占满时，Future 完成路径独立（Completion 池 + 内联兜底）
 * 3. 房间生命周期关闭隔离：关房 A 不影响共享池中房 B 的正常收发，重复 close 幂等，共享池线程仍存活
 * 4. RoomSubscriber 终态通知不丢：调度拒绝发生后保持数据并在下一次触发送达终态 CLOSED
 * 5. 默认模式向后兼容与零回归
 */
@Timeout(30)
class SharedDispatchIsolationTest {

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test
    fun `slow callback in room A does not block room B updates within budget`() {
        val pools = HostDispatchPools(
            dispatchThreads = 4,
            dispatchQueueCapacity = 64,
            completionThreads = 2,
            completionQueueCapacity = 64,
            seatChannelCapacityItems = 64,
            publicChannelCapacityItems = 64
        )

        val roomA = SharedRoom(
            roomId = "ROOM_A",
            hostPlayerName = "Alice",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false,
            hostDispatch = pools
        )

        val roomB = SharedRoom(
            roomId = "ROOM_B",
            hostPlayerName = "Bob",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false,
            hostDispatch = pools
        )

        try {
            val roomABlocked = CountDownLatch(1)
            val roomAProceed = CountDownLatch(1)
            val roomAUpdates = CopyOnWriteArrayList<Long>()
            val roomBActorUpdates = CopyOnWriteArrayList<Long>()

            val subA = roomA.subscribe(0) { update ->
                roomAUpdates.add(update.snapshot.cursor)
                if (update.snapshot.cursor > 0) {
                    roomABlocked.countDown()
                    roomAProceed.await(5, TimeUnit.SECONDS)
                }
            }

            val roomBUpdatedLatch = CountDownLatch(1)
            val subB = roomB.subscribe(0) { update ->
                roomBActorUpdates.add(update.snapshot.cursor)
                if (update.snapshot.cursor > 0) {
                    roomBUpdatedLatch.countDown()
                }
            }

            // 房 A 开赛发布席位更新（cursor > 0），进入慢回调阻塞
            roomA.tableHost.start(0)
            assertTrue(roomABlocked.await(5, TimeUnit.SECONDS), "Room A should enter slow callback")

            // 此时房 A 的回调正在阻塞，验证房 B 仍能在毫秒级预算时间内正常发布并派发席位更新
            val startB = roomB.tableHost.start(0).get(3, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, startB.status)

            assertTrue(
                roomBUpdatedLatch.await(3, TimeUnit.SECONDS),
                "Room B updates must not be blocked by slow callback in Room A"
            )
            assertTrue(roomBActorUpdates.size >= 2, "Room B must have delivered baseline + start update")

            // 释放房 A 的阻塞门闩，验证房 A 追上且游标单调
            roomAProceed.countDown()

            val deadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < deadline && roomAUpdates.size < 2) {
                Thread.sleep(10)
            }
            assertTrue(roomAUpdates.size >= 2, "Room A must catch up after release")
            for (i in 1 until roomAUpdates.size) {
                assertTrue(roomAUpdates[i] >= roomAUpdates[i - 1], "Room A cursor must be monotonic")
            }

            subA.close()
            subB.close()
        } finally {
            roomA.close()
            roomB.close()
            pools.close()
        }
    }

    @Test
    fun `completion pool remains independent when dispatch pool is fully saturated`() {
        // 创建只有 2 个 dispatch 线程但有独立 completion 线程的池
        val pools = HostDispatchPools(
            dispatchThreads = 2,
            dispatchQueueCapacity = 16,
            completionThreads = 2,
            completionQueueCapacity = 16
        )

        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            controllers = List(4) { SeatController.LocalHuman() },
            sharedDispatch = pools.dispatchExecutor,
            sharedCompletion = pools.completionExecutor
        )

        try {
            val dispatchHold = CountDownLatch(1)
            val dispatchThreadsBlocked = CountDownLatch(2)

            // 提交 2 个慢任务占满 dispatch 池全部 2 个线程
            for (i in 0 until 2) {
                pools.dispatchExecutor.execute {
                    dispatchThreadsBlocked.countDown()
                    dispatchHold.await(5, TimeUnit.SECONDS)
                }
            }
            assertTrue(dispatchThreadsBlocked.await(3, TimeUnit.SECONDS), "Dispatch pool must be fully saturated")

            // 对 host 提交 enqueueOperation 操作，依赖 completionExecutor 完成 Future
            val startFuture = host.start(0)
            val receipt = startFuture.get(3, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, receipt.status)

            // 释放占满的 dispatch 任务
            dispatchHold.countDown()
        } finally {
            host.close()
            pools.close()
        }
    }

    @Test
    fun `closing room A does not affect room B on shared pools and pool remains alive`() {
        val pools = HostDispatchPools(
            dispatchThreads = 4,
            completionThreads = 2
        )

        val roomA = SharedRoom(
            roomId = "CLOSE_A",
            hostPlayerName = "Alice",
            initialConfig = ProtocolRoomConfig(),
            hostDispatch = pools
        )

        val roomB = SharedRoom(
            roomId = "CLOSE_B",
            hostPlayerName = "Bob",
            initialConfig = ProtocolRoomConfig(),
            hostDispatch = pools
        )

        try {
            assertFalse(roomA.closed)
            assertFalse(roomB.closed)
            assertFalse(pools.closed)

            // 关闭房 A
            roomA.close()
            assertTrue(roomA.closed)
            // 重复 close 幂等
            roomA.close()
            assertTrue(roomA.closed)

            // 共享池绝不被房 A 关闭，仍处于存活可用状态
            assertFalse(pools.closed)
            assertFalse(pools.dispatchExecutor.isShutdown)
            assertFalse(pools.completionExecutor.isShutdown)

            // 房 B 仍可正常执行动作和派发更新
            val bUpdates = CopyOnWriteArrayList<ProtocolRoomInfo>()
            val bSub = roomB.onRoomUpdate { bUpdates.add(it) }

            val joinB = roomB.join("Charlie").await()
            assertEquals(1, joinB.first)

            // RoomSubscriber 为"最新视图合并"语义（任务 C：房间通知属可合并的完整状态通知）：
            // 订阅基线 v1 与 join 的 v2 可能合并为一条投递，不能断言必然收到 2 条；
            // 收敛断言：至少收到一条更新，且最后一条版本 >= join 后的最新房间版本。
            val latestVersion = roomB.getRoomInfoSnapshot().version
            val deadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < deadline &&
                (bUpdates.isEmpty() || bUpdates.last().version < latestVersion)
            ) {
                Thread.sleep(10)
            }
            assertTrue(bUpdates.isNotEmpty(), "Room B must continue receiving updates after room A close")
            assertTrue(bUpdates.last().version >= latestVersion, "Room B must converge to the latest room version (coalescing allowed)")
            for (i in 1 until bUpdates.size) {
                assertTrue(bUpdates[i].version > bUpdates[i - 1].version, "Observed room versions must be strictly increasing")
            }

            bSub.close()
        } finally {
            roomA.close()
            roomB.close()
            pools.close()
            assertTrue(pools.closed)
        }
    }

    @Test
    fun `RoomSubscriber does not drop terminal CLOSED notification upon temporary executor rejection`() {
        val pools = HostDispatchPools(
            dispatchThreads = 1,
            dispatchQueueCapacity = 1,
            completionThreads = 1
        )

        val room = SharedRoom(
            roomId = "REJECT_ROOM",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false,
            hostDispatch = pools
        )

        try {
            val observed = CopyOnWriteArrayList<ProtocolRoomInfo>()
            val terminalLatch = CountDownLatch(1)

            // 先 onRoomUpdate 订阅（基线正常送达）
            val sub = room.onRoomUpdate { info ->
                observed.add(info)
                if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
                    terminalLatch.countDown()
                }
            }

            // 等待基线版本送达
            val baselineDeadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < baselineDeadline && observed.isEmpty()) {
                Thread.sleep(10)
            }
            assertTrue(observed.isNotEmpty(), "Baseline must be delivered")

            // 向 dispatchExecutor 提交 1 个持闩任务（占住唯一线程）+ 1 个填队任务（占满容量 1 的队列），等待确认占满
            val holdEntered = CountDownLatch(1)
            val holdRelease = CountDownLatch(1)
            val queueFillCompleted = CountDownLatch(1)

            pools.dispatchExecutor.execute {
                holdEntered.countDown()
                holdRelease.await(5, TimeUnit.SECONDS)
            }
            assertTrue(holdEntered.await(3, TimeUnit.SECONDS), "Dispatch worker thread must be occupied")

            // 占满容量为 1 的队列
            pools.dispatchExecutor.execute {
                queueFillCompleted.countDown()
            }

            // room.join("Guest") -> RoomInfo 更新 enqueue 被拒
            val joinPair = room.join("Guest").await()
            val guestSeat = joinPair.first

            // 断言 room.roomSubscriberRejectCount >= 1，且订阅者尚未收到该版本
            assertTrue(room.roomSubscriberRejectCount >= 1L, "roomSubscriberRejectCount must be >= 1 after saturation")
            assertEquals(1, observed.size, "Subscriber must not have received rejected version while saturated")

            // 释放持闩、等填队任务执行完（池空闲，pending 仍保留）
            holdRelease.countDown()
            assertTrue(queueFillCompleted.await(3, TimeUnit.SECONDS), "Queue fill task must complete")

            // room.leave(guestSeat) -> 新版本 enqueue 重触发 drain
            val leaveReceipt = room.leave(guestSeat).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)

            // 等待重触发派发，断言版本严格递增且不丢最新视图（被拒版本允许被更高版本合并）
            val drainDeadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < drainDeadline && observed.size < 2) {
                Thread.sleep(10)
            }
            assertTrue(observed.size >= 2, "Subscriber must receive updates after drain re-triggered")
            for (i in 1 until observed.size) {
                assertTrue(
                    observed[i].version > observed[i - 1].version,
                    "Observed versions must be strictly monotonic: ${observed.map { it.version }}"
                )
            }

            // room.close() -> 断言 CLOSED 终态最后送达
            room.close()
            assertTrue(terminalLatch.await(3, TimeUnit.SECONDS), "Terminal CLOSED notification must be delivered")
            assertTrue(observed.last().lifecycle == ProtocolRoomLifecycle.CLOSED, "Final observed event must be CLOSED")

            sub.close()
        } finally {
            room.close()
            pools.close()
        }
    }

    @Test
    fun `default mode regression ensures exact single-thread per seat semantics without shared pools`() {
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            controllers = List(4) { SeatController.LocalHuman() }
        )

        try {
            // 验证默认模式下通道存在且指标正常
            for (s in 0..3) {
                val ch = host.getSeatChannel(s)
                assertTrue(ch != null)
                assertEquals(0L, ch!!.overflowCount)
                assertFalse(ch.needsRecovery)
            }
            val pubCh = host.getPublicDispatchChannel()
            assertEquals(0L, pubCh.overflowCount)
            assertFalse(pubCh.needsRecovery)

            val delivered = AtomicInteger(0)
            val latch = CountDownLatch(1)
            val sub = host.subscribeSeat(0) {
                delivered.incrementAndGet()
                latch.countDown()
            }

            assertTrue(latch.await(3, TimeUnit.SECONDS), "Default mode baseline must be delivered")
            assertTrue(delivered.get() >= 1)
            sub.close()
        } finally {
            host.close()
            assertTrue(host.closed)
        }
    }
}

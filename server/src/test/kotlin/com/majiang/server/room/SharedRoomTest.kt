package com.majiang.server.room

import com.majiang.access.ReceiptStatus
import com.majiang.application.ActionChoice
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

@Timeout(15)
class SharedRoomTest {

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test
    fun `singleplayer create and local-start flow`() {
        val room = SharedRoom(
            roomId = "SP1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )

        try {
            assertFalse(room.closed)
            assertEquals(0, room.hostSeat)
            assertTrue(room.seatOccupied[0])
            assertTrue(room.isHuman[0])
            assertTrue(room.isReady[0])

            for (s in 1..3) {
                assertTrue(room.seatOccupied[s])
                assertFalse(room.isHuman[s])
                assertTrue(room.isReady[s])
            }

            val startReceipt = room.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

            val snap = room.snapshot(0).await()
            assertEquals(0, snap.viewerSeat)

            val decision = room.decision(0).await()
            assertTrue(decision.options.isNotEmpty())

            val info = room.getRoomInfo().await()
            assertTrue(info.lifecycle == ProtocolRoomLifecycle.PLAYING || info.lifecycle == ProtocolRoomLifecycle.PREPARING)
            assertEquals(4, info.members.size)
        } finally {
            room.close()
            assertTrue(room.closed)
        }
    }

    @Test
    fun `host-only config and network policy readiness gate before match start`() {
        val room = SharedRoom(
            roomId = "MP1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )

        try {
            val joinPair = room.join("Guest1").await()
            val guestSeat = joinPair.first
            assertEquals(1, guestSeat)

            // 1. 非房主更新配置拒绝
            val guestUpdate = room.updateConfig(guestSeat, ProtocolRoomConfig()).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, guestUpdate.status)
            assertEquals("PERMISSION_DENIED", guestUpdate.reason)

            // 2. 房主在准备未齐时开赛拒绝
            val unreadyStart = room.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, unreadyStart.status)
            assertEquals("NOT_READY", unreadyStart.reason)

            // 3. 房主更新配置成功
            val hostUpdate1 = room.updateConfig(0, ProtocolRoomConfig()).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, hostUpdate1.status)

            // 4. 成员准备，房主未准备 -> 开赛仍拒绝
            val guestReady = room.setReady(guestSeat, true).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, guestReady.status)
            val hostStillUnreadyStart = room.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, hostStillUnreadyStart.status)
            assertEquals("NOT_READY", hostStillUnreadyStart.reason)

            // 5. 房主也准备就绪
            val hostReady = room.setReady(0, true).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, hostReady.status)

            // 6. 配置更新重置就绪状态
            val hostUpdate2 = room.updateConfig(0, ProtocolRoomConfig()).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, hostUpdate2.status)
            val infoAfterConfig = room.getRoomInfo().await()
            assertFalse(infoAfterConfig.members.first { it.seat == guestSeat }.isReady)
            assertFalse(infoAfterConfig.members.first { it.seat == 0 }.isReady)

            val startAfterConfigReset = room.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, startAfterConfigReset.status)
            assertEquals("NOT_READY", startAfterConfigReset.reason)

            // 7. 双方重新准备后开赛成功，未占用席位自动由 AI 补位
            room.setReady(0, true).await()
            room.setReady(guestSeat, true).await()

            val successStart = room.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, successStart.status)

            val infoStarted = room.getRoomInfo().await()
            assertEquals(4, infoStarted.members.size)
            assertTrue(infoStarted.members.any { it.seat == 2 && !it.role.equals("HUMAN", ignoreCase = true) })
            assertTrue(infoStarted.members.any { it.seat == 3 && !it.role.equals("HUMAN", ignoreCase = true) })

            // 8. 开赛后修改配置拒绝
            val lateConfig = room.updateConfig(0, ProtocolRoomConfig()).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, lateConfig.status)
            assertEquals("INVALID_PHASE", lateConfig.reason)
        } finally {
            room.close()
        }
    }

    @Test
    fun `stable session on lobby config update`() {
        val room = SharedRoom(
            roomId = "CFG1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )

        try {
            val snapBefore = room.snapshot(0).await()
            val initialSessionId = snapBefore.sessionId
            val initialSyncEpoch = snapBefore.syncEpoch

            val updateReceipt = room.updateConfig(0, ProtocolRoomConfig()).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, updateReceipt.status)

            val snapAfter = room.snapshot(0).await()
            assertEquals(initialSessionId, snapAfter.sessionId, "SessionId must remain stable on config update")
            assertEquals(initialSyncEpoch, snapAfter.syncEpoch, "SyncEpoch must remain stable on config update")
        } finally {
            room.close()
        }
    }

    @Test
    fun `blocking room observer does not block authoritative queries or operations`() {
        val room = SharedRoom(
            roomId = "SLOW1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )

        val enteredLatch = CountDownLatch(1)
        val releaseLatch = CountDownLatch(1)
        val armed = AtomicBoolean(false)

        val callers = Executors.newFixedThreadPool(2)

        try {
            val sub = room.onRoomUpdate {
                if (armed.compareAndSet(true, false)) {
                    enteredLatch.countDown()
                    check(releaseLatch.await(8, TimeUnit.SECONDS)) { "Observer release timed out" }
                }
            }

            try {
                armed.set(true)
                // 触发通知
                val readyFuture = room.setReady(0, false)
                assertTrue(enteredLatch.await(3, TimeUnit.SECONDS), "Observer must have entered")

                // 在观察者阻塞时，权威查询和快照必须能无阻塞返回
                val infoFuture = CompletableFuture.supplyAsync({ room.getRoomInfo().await() }, callers)
                val info = infoFuture.get(2, TimeUnit.SECONDS)
                assertNotNull(info)

                val snapFuture = CompletableFuture.supplyAsync({ room.snapshot(0).await() }, callers)
                val snap = snapFuture.get(2, TimeUnit.SECONDS)
                assertNotNull(snap)

                // 释放阻塞观察者
                releaseLatch.countDown()
                val receipt = readyFuture.await()
                assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
            } finally {
                releaseLatch.countDown()
                sub.close()
            }
        } finally {
            callers.shutdownNow()
            room.close()
        }
    }

    @Test
    fun `start versus join config and leave operations are serialized on authority queue`() {
        val room = SharedRoom(
            roomId = "SERIAL1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )

        val callers = Executors.newFixedThreadPool(4)
        try {
            val f1 = CompletableFuture.supplyAsync({ room.setReady(0, true).await() }, callers)
            val f2 = CompletableFuture.supplyAsync({ room.join("Guest1").await() }, callers)
            val f3 = CompletableFuture.supplyAsync({ room.updateConfig(0, ProtocolRoomConfig()).await() }, callers)

            CompletableFuture.allOf(f1, f2, f3).await()

            // 权威状态自洽无崩溃
            val info = room.getRoomInfo().await()
            assertEquals(2, info.members.size)
            assertFalse(info.members.first { it.seat == 0 }.isReady) // 被配置更新重置为 false
        } finally {
            callers.shutdownNow()
            room.close()
        }
    }

    @Test
    fun `stale queued binding rejected by session validator`() {
        val room = SharedRoom(
            roomId = "STALE1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )

        try {
            val staleValidator: () -> Boolean = { false }

            // 动作回执拒绝并携带 STALE_SESSION
            val startReceipt = room.startMatch(0, sessionValidator = staleValidator).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, startReceipt.status)
            assertEquals("STALE_SESSION", startReceipt.reason)

            val readyReceipt = room.setReady(0, true, sessionValidator = staleValidator).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, readyReceipt.status)
            assertEquals("STALE_SESSION", readyReceipt.reason)

            val configReceipt = room.updateConfig(0, ProtocolRoomConfig(), sessionValidator = staleValidator).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, configReceipt.status)
            assertEquals("STALE_SESSION", configReceipt.reason)

            val leaveReceipt = room.leave(0, sessionValidator = staleValidator).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, leaveReceipt.status)
            assertEquals("STALE_SESSION", leaveReceipt.reason)

            // 查询异常完成
            val snapEx = assertThrows<ExecutionException> {
                room.snapshot(0, sessionValidator = staleValidator).await()
            }
            assertTrue(snapEx.cause is IllegalStateException)
            assertEquals("STALE_SESSION", snapEx.cause?.message)

            val decEx = assertThrows<ExecutionException> {
                room.decision(0, sessionValidator = staleValidator).await()
            }
            assertTrue(decEx.cause is IllegalStateException)
            assertEquals("STALE_SESSION", decEx.cause?.message)

            val infoEx = assertThrows<ExecutionException> {
                room.getRoomInfo(sessionValidator = staleValidator).await()
            }
            assertTrue(infoEx.cause is IllegalStateException)
            assertEquals("STALE_SESSION", infoEx.cause?.message)
        } finally {
            room.close()
        }
    }

    @Test
    fun `close terminates pending futures and resolves room`() {
        val pausedExecutor = Executors.newSingleThreadExecutor()
        val pauseLatch = CountDownLatch(1)
        pausedExecutor.submit {
            pauseLatch.await(5, TimeUnit.SECONDS)
        }

        val room = SharedRoom(
            roomId = "CLOSE1",
            hostPlayerName = "Host",
            serialExecutor = pausedExecutor
        )

        val pendingStart = room.startMatch(0)
        assertFalse(pendingStart.isDone)

        room.close()
        assertTrue(room.closed)
        assertTrue(pendingStart.isCompletedExceptionally)

        pauseLatch.countDown()
        pausedExecutor.shutdownNow()
    }

    @Test
    fun `host leave dissolves room for other members and unbinds caller`() {
        val room = SharedRoom(
            roomId = "LEAVE1",
            hostPlayerName = "HostPlayer",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )

        try {
            val joinPair = room.join("Guest1").await()
            assertEquals(1, joinPair.first)

            val receivedLifecycles = CopyOnWriteArrayList<ProtocolRoomLifecycle>()
            val sub = room.onRoomUpdate { receivedLifecycles.add(it.lifecycle) }

            try {
                // 房主退出解散房间
                val leaveReceipt = room.leave(0).await()
                assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
                assertTrue(room.closed)
                assertEquals(ProtocolRoomLifecycle.CLOSED, room.lifecycle)

                // 再次操作被拒绝
                val afterLeaveJoin = assertThrows<ExecutionException> {
                    room.join("Guest2").await()
                }
                assertTrue(afterLeaveJoin.cause is IllegalStateException)
            } finally {
                sub.close()
            }
        } finally {
            room.close()
        }
    }

    @Test
    fun `explicit leave revokes token and increments generation in every phase`() {
        val room = SharedRoom(
            roomId = "REVOKE1",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            val joinPair = room.join("Guest1").await()
            val guestSeat = joinPair.first
            val originalToken = joinPair.second
            val originalGen = room.bindingGenerations[guestSeat]

            // 1. 大厅期退出
            val leaveReceipt = room.leave(guestSeat).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)

            assertFalse(room.seatOccupied[guestSeat])
            val rotatedToken = room.resumeTokens[guestSeat]
            val incrementedGen = room.bindingGenerations[guestSeat]
            assertTrue(rotatedToken != originalToken, "Resume token must be rotated upon leave")
            assertTrue(incrementedGen > originalGen, "Binding generation must increment upon leave")
        } finally {
            room.close()
        }
    }

    @Test
    fun `rejected paused start does not consume admission or mutate occupants to ai`() {
        val room = SharedRoom(
            roomId = "PAUSE_START1",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            room.join("Guest1").await()
            room.setReady(0, true).await()
            room.setReady(1, true).await()

            // 暂停宿主时钟
            room.tableHost.pause().await()

            // 发起开赛，应被防御性拦截并返回 REJECTED(HOST_PAUSED)
            val receipt = room.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, receipt.status)
            assertEquals("HOST_PAUSED", receipt.reason)

            // 验证未占用席位 2, 3 绝未被破坏性修改为 AI 补位，准入空间完整保留
            val info = room.getRoomInfo().await()
            assertEquals(ProtocolRoomLifecycle.LOBBY, info.lifecycle)
            assertEquals(2, info.members.size)
            assertFalse(room.seatOccupied[2])
            assertFalse(room.seatOccupied[3])
        } finally {
            room.close()
        }
    }

    @Test
    fun `room observer receives strictly monotonic versions and eventual latest full view`() {
        val room = SharedRoom(
            roomId = "ORDER1",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            // 目标在订阅与任何变更之前确定：基线版本 + 两次就绪翻转各恰好递增一次；
            // 不使用事后回填的捕获 var，避免错过已送达通知与数据竞争
            val baselineVersion = room.version
            val targetVersion = baselineVersion + 2L
            val observed = CopyOnWriteArrayList<ProtocolRoomInfo>()
            val baselineArrived = CountDownLatch(1)
            val latestArrived = CountDownLatch(1)
            val sub = room.onRoomUpdate { info ->
                observed.add(info)
                if (info.version == baselineVersion) baselineArrived.countDown()
                if (info.version >= targetVersion && info.members.first { it.seat == 0 }.isReady) {
                    latestArrived.countDown()
                }
            }
            try {
                // 先确定性等待初始基线送达，再触发变更，保证首个观察到的必为基线
                assertTrue(baselineArrived.await(3, TimeUnit.SECONDS),
                    "Initial baseline must be delivered upon subscription")

                room.setReady(0, false).await()
                room.setReady(0, true).await()

                assertTrue(latestArrived.await(3, TimeUnit.SECONDS),
                    "Observer must eventually receive the latest ready=true full view")
                assertTrue(observed.isNotEmpty())
                // 首个送达必须是订阅时的初始基线版本
                assertEquals(baselineVersion, observed.first().version)
                // 观察到的版本严格单调递增（允许合并跳过中间版本，禁止回退/重复）
                for (i in 0 until observed.size - 1) {
                    assertTrue(observed[i].version < observed[i + 1].version,
                        "Observed versions must be strictly increasing: $observed")
                }
                // 最终视图与权威查询一致
                val last = observed.last()
                val authoritative = room.getRoomInfo().await()
                assertEquals(authoritative.version, last.version)
                assertTrue(last.members.first { it.seat == 0 }.isReady)
            } finally {
                sub.close()
            }
        } finally {
            room.close()
        }
    }

    @Test
    fun `resumeSession validates token and rejects wrong or revoked identity`() {
        val room = SharedRoom(
            roomId = "RESUME_TEST1",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            val joinPair = room.join("Guest1").await()
            val guestSeat = joinPair.first
            val validToken = joinPair.second

            // 1. 错误 token 被拒绝
            val wrongTokenEx = assertThrows<ExecutionException> {
                room.resumeSession("WRONG_TOKEN").await()
            }
            assertTrue(wrongTokenEx.cause is IllegalStateException)
            assertEquals("INVALID_RESUME_TOKEN", wrongTokenEx.cause?.message)

            // 2. 正确 token 恢复成功
            val res = room.resumeSession(validToken).await()
            assertEquals(guestSeat, res.seat)
            assertNotNull(res.snapshot)
            assertTrue(res.generation > 1L)

            // 3. 显式退出后，token 被轮转注销，原 token 恢复被拒绝
            room.leave(guestSeat).await()
            val revokedEx = assertThrows<ExecutionException> {
                room.resumeSession(validToken).await()
            }
            assertTrue(revokedEx.cause is IllegalStateException)
            assertEquals("INVALID_RESUME_TOKEN", revokedEx.cause?.message)
        } finally {
            room.close()
        }
    }

    @Test
    fun `host leave cleans up tableHost resources idempotently without leaking executor`() {
        val room = SharedRoom(
            roomId = "LEAVE_CLEANUP",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        val leaveReceipt = room.leave(0).await()
        assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
        assertTrue(room.closed)

        // 重复调用 close 幂等无异常
        room.close()
        assertTrue(room.closed)
    }

    @Test
    fun `shared room with injected host dispatch preserves pools on room close`() {
        val pools = com.majiang.server.host.HostDispatchPools(
            dispatchThreads = 2,
            dispatchQueueCapacity = 16,
            completionThreads = 2,
            completionQueueCapacity = 16
        )
        try {
            val room = SharedRoom(
                roomId = "SHARED_POOL_TEST",
                hostPlayerName = "Host",
                initialConfig = ProtocolRoomConfig(),
                isSingleplayer = true,
                hostDispatch = pools
            )
            assertFalse(room.closed)
            val info = room.getRoomInfo().await()
            assertEquals("SHARED_POOL_TEST", info.roomId)

            // 关闭房间
            room.close()
            assertTrue(room.closed)

            // 共享池绝不被房间关闭
            assertFalse(pools.dispatchExecutor.isShutdown, "Dispatch executor must remain active after room close")
            assertFalse(pools.completionExecutor.isShutdown, "Completion executor must remain active after room close")
        } finally {
            pools.close(1000L)
        }
    }
}


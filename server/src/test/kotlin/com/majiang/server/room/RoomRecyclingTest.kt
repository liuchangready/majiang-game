package com.majiang.server.room

import com.majiang.access.ReceiptStatus
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ServerApiException
import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import com.majiang.server.tcp.TcpTestClient
import com.majiang.timing.ManualMonotonicClock
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit

/**
 * 房间空闲回收、观战上限与时钟推进验收测试 (RoomRecyclingTest)
 *
 * 覆盖验收规范：
 * - R06-T1: TTL 边界（TTL-1 不回收、TTL 到达回收；LOBBY/进行中/FINISHED 三档与未参与短期限）
 * - R06-T2: 竞争（idleSince 到达阈值瞬间并发 resume 与 closeIfIdleOnQueue 权威串行裁决）
 * - R06-T3: 观战者占位与存活（有已连接观战者时不回收，观战者全部离开或断开后进入空闲计时）
 * - R06-T4: AI 不延长无人房间寿命（全员 AI 托管时 tick 持续运行但 idleSince 不刷新，TTL 到达照常回收）
 * - R06-T5: 观战人数上限（第 32 个成功，第 33 个被拒 ROOM_FULL/SPECTATOR_LIMIT，断线占位，resume 不受限，leave 释放）
 * - R06-T11: 驱逐后 token 作废（被回收房间旧 token 无法复活房间，返回 ROOM_NOT_FOUND）
 * - R06-T13: InProcess 进程内房间回归（无网络连接时不自建调度器、不被回收）
 */
@Timeout(15)
class RoomRecyclingTest {

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test
    fun `R06-T1 TTL boundary for LOBBY, in-progress, FINISHED and unengaged`() {
        val clock = ManualMonotonicClock(1000L)
        val policy = RoomRetentionPolicy(
            lobbyIdleTtlMillis = 300_000L,       // 5 min
            inProgressIdleTtlMillis = 1_800_000L, // 30 min
            finishedIdleTtlMillis = 600_000L,     // 10 min
            unengagedRoomTtlMillis = 60_000L      // 60 sec
        )

        // 1. 未形成有效参与房间 (unengaged): 刚建房从未有任何真人连接
        val unengagedRoom = SharedRoom(roomId = "UNENGAGED", clock = clock)
        try {
            assertFalse(unengagedRoom.everConnectedHuman)
            assertNotNull(unengagedRoom.idleSinceMillis)
            val idleSince = unengagedRoom.idleSinceMillis!!

            // TTL - 1: 不回收
            val notRecycled = unengagedRoom.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = unengagedRoom.idleEpoch,
                nowMillis = idleSince + policy.unengagedRoomTtlMillis - 1L
            ).await()
            assertFalse(notRecycled)
            assertFalse(unengagedRoom.closed)

            // TTL 到达: 成功回收
            val recycled = unengagedRoom.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = unengagedRoom.idleEpoch,
                nowMillis = idleSince + policy.unengagedRoomTtlMillis
            ).await()
            assertTrue(recycled)
            assertTrue(unengagedRoom.closed)
            assertEquals(ProtocolRoomLifecycle.CLOSED, unengagedRoom.lifecycle)
        } finally {
            unengagedRoom.close()
        }

        // 2. LOBBY 阶段真人断线回收 (5 分钟)
        val lobbyRoom = SharedRoom(roomId = "LOBBY_ROOM", clock = clock)
        try {
            // 房主连接建立
            lobbyRoom.markSeatConnected(0, lobbyRoom.bindingGenerations[0]).await()
            assertTrue(lobbyRoom.everConnectedHuman)
            assertNull(lobbyRoom.idleSinceMillis)

            // 房主断开连接
            clock.advance(10_000L)
            lobbyRoom.disconnectSeat(0, lobbyRoom.bindingGenerations[0]).await()
            assertNotNull(lobbyRoom.idleSinceMillis)
            val lobbyIdleStart = lobbyRoom.idleSinceMillis!!

            // TTL - 1: 不回收
            val lobbyNotRecycled = lobbyRoom.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = lobbyRoom.idleEpoch,
                nowMillis = lobbyIdleStart + policy.lobbyIdleTtlMillis - 1L
            ).await()
            assertFalse(lobbyNotRecycled)
            assertFalse(lobbyRoom.closed)

            // TTL 到达: 成功回收
            val lobbyRecycled = lobbyRoom.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = lobbyRoom.idleEpoch,
                nowMillis = lobbyIdleStart + policy.lobbyIdleTtlMillis
            ).await()
            assertTrue(lobbyRecycled)
            assertTrue(lobbyRoom.closed)
        } finally {
            lobbyRoom.close()
        }

        // 3. 进行中阶段 (PLAYING) 全员断线回收 (30 分钟)
        val playRoom = SharedRoom(roomId = "PLAY_ROOM", clock = clock)
        try {
            playRoom.markSeatConnected(0, playRoom.bindingGenerations[0]).await()
            for (s in 1..3) {
                val pair = playRoom.join("P$s").await()
                playRoom.markSeatConnected(pair.first, playRoom.bindingGenerations[pair.first]).await()
                playRoom.setReady(pair.first, true).await()
            }
            playRoom.setReady(0, true).await()
            val startReceipt = playRoom.startMatch(0).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)
            assertTrue(playRoom.lifecycle == ProtocolRoomLifecycle.PLAYING || playRoom.lifecycle == ProtocolRoomLifecycle.PREPARING)

            // 全员断开
            clock.advance(5_000L)
            for (s in 0..3) {
                playRoom.disconnectSeat(s, playRoom.bindingGenerations[s]).await()
            }
            assertNotNull(playRoom.idleSinceMillis)
            val playIdleStart = playRoom.idleSinceMillis!!

            // TTL - 1: 不回收
            val playNotRecycled = playRoom.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = playRoom.idleEpoch,
                nowMillis = playIdleStart + policy.inProgressIdleTtlMillis - 1L
            ).await()
            assertFalse(playNotRecycled)
            assertFalse(playRoom.closed)

            // TTL 到达: 成功回收
            val playRecycled = playRoom.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = playRoom.idleEpoch,
                nowMillis = playIdleStart + policy.inProgressIdleTtlMillis
            ).await()
            assertTrue(playRecycled)
            assertTrue(playRoom.closed)
        } finally {
            playRoom.close()
        }

        // 4. 终局阶段 (FINISHED) 回收 TTL 选取（纯函数单测验证）
        assertEquals(
            policy.finishedIdleTtlMillis,
            ttlFor(ProtocolRoomLifecycle.FINISHED, everConnectedHuman = true, creatorActive = false, policy = policy)
        )
        assertEquals(
            policy.finishedIdleTtlMillis,
            ttlFor(ProtocolRoomLifecycle.FINISHED, everConnectedHuman = false, creatorActive = false, policy = policy)
        )
    }

    @Test
    fun `R06-T2 race condition between resumeSession and closeIfIdleOnQueue`() {
        val clock = ManualMonotonicClock(1000L)
        val policy = RoomRetentionPolicy(lobbyIdleTtlMillis = 10_000L)

        // Case A: resumeSession 先入队 -> 恢复连接并清除空闲起点，后续 closeIfIdleOnQueue 失败
        val roomA = SharedRoom(roomId = "RACE_A", clock = clock)
        try {
            roomA.markSeatConnected(0, roomA.bindingGenerations[0]).await()
            roomA.disconnectSeat(0, roomA.bindingGenerations[0]).await()
            val token = roomA.resumeTokens[0]
            val capturedEpoch = roomA.idleEpoch

            clock.advance(10_000L)

            // 并发提交：resumeSession 先于 closeIfIdleOnQueue 入队
            val resumeFuture = roomA.resumeSession(token)
            val closeFuture = roomA.closeIfIdleOnQueue(policy, capturedEpoch, clock.nowMillis())

            val resumeResult = resumeFuture.await()
            val closeResult = closeFuture.await()

            assertEquals(0, resumeResult.seat)
            assertFalse(closeResult, "closeIfIdleOnQueue must fail because resumeSession cleared idle status")
            assertFalse(roomA.closed)
            assertNull(roomA.idleSinceMillis)
        } finally {
            roomA.close()
        }

        // Case B: closeIfIdleOnQueue 先入队 -> 成功回收，后续 resumeSession 抛 ROOM_NOT_FOUND (ROOM_CLOSED)
        val roomB = SharedRoom(roomId = "RACE_B", clock = clock)
        try {
            roomB.markSeatConnected(0, roomB.bindingGenerations[0]).await()
            roomB.disconnectSeat(0, roomB.bindingGenerations[0]).await()
            val token = roomB.resumeTokens[0]
            val capturedEpoch = roomB.idleEpoch

            clock.advance(10_000L)

            // 并发提交：closeIfIdleOnQueue 先入队
            val closeFuture = roomB.closeIfIdleOnQueue(policy, capturedEpoch, clock.nowMillis())
            val resumeFuture = roomB.resumeSession(token)

            val closeResult = closeFuture.await()
            assertTrue(closeResult, "closeIfIdleOnQueue must succeed")
            assertTrue(roomB.closed)

            val ex = assertThrows<ExecutionException> {
                resumeFuture.await()
            }
            val cause = ex.cause as? ServerApiException
            assertNotNull(cause)
            assertEquals(ApiErrors.ROOM_NOT_FOUND, cause?.code)
        } finally {
            roomB.close()
        }
    }

    @Test
    fun `R06-T3 spectator retention and idle transition`() {
        val clock = ManualMonotonicClock(1000L)
        val policy = RoomRetentionPolicy(lobbyIdleTtlMillis = 5_000L)
        val room = SharedRoom(roomId = "SPEC_ROOM", clock = clock)

        try {
            // 真人房主连接
            room.markSeatConnected(0, room.bindingGenerations[0]).await()

            // 观战者加入并标记连接
            val specResult = room.joinSpectator("Spectator-1").await()
            room.markSpectatorConnected(specResult.spectatorId).await()
            assertEquals(1, room.connectedSpectatorCount())

            // 真人房主断开
            room.disconnectSeat(0, room.bindingGenerations[0]).await()
            assertEquals(0, room.connectedHumanCount())
            // 依然有已连接观战者 -> 不进入空闲
            assertNull(room.idleSinceMillis, "Room must NOT be idle while connected spectator remains")

            clock.advance(10_000L)
            val notRecycled = room.closeIfIdleOnQueue(policy, room.idleEpoch, clock.nowMillis()).await()
            assertFalse(notRecycled)
            assertFalse(room.closed)

            // 观战者离开 (leaveSpectator)
            room.leaveSpectator(specResult.spectatorId).await()
            assertEquals(0, room.connectedSpectatorCount())
            // 观战者全部离开且无真人 -> 此时进入空闲计时
            assertNotNull(room.idleSinceMillis, "Room must enter idle once all spectators leave and no human remains")

            val idleStart = room.idleSinceMillis!!
            clock.advance(policy.lobbyIdleTtlMillis)

            val recycled = room.closeIfIdleOnQueue(policy, room.idleEpoch, clock.nowMillis()).await()
            assertTrue(recycled)
            assertTrue(room.closed)
        } finally {
            room.close()
        }
    }

    @Test
    fun `R06-T4 AI ticks do not prolong unoccupied room life`() {
        val clock = ManualMonotonicClock(1000L)
        val policy = RoomRetentionPolicy(inProgressIdleTtlMillis = 15_000L)
        val room = SharedRoom(roomId = "AI_NO_EXTEND", clock = clock)

        try {
            room.markSeatConnected(0, room.bindingGenerations[0]).await()
            for (s in 1..3) {
                val pair = room.join("P$s").await()
                room.markSeatConnected(pair.first, room.bindingGenerations[pair.first]).await()
                room.setReady(pair.first, true).await()
            }
            room.setReady(0, true).await()
            room.startMatch(0).await()

            // 所有真人玩家断开，转由 AI 托管
            for (s in 0..3) {
                room.disconnectSeat(s, room.bindingGenerations[s]).await()
            }
            val initialIdleSince = room.idleSinceMillis
            assertNotNull(initialIdleSince)

            // 模拟 AI tick 驱动多次
            for (i in 0 until 50) {
                clock.advance(200L)
                room.tick(presentationBusy = false).await()
                // 验证 tick 绝不更新 idleSinceMillis
                assertEquals(initialIdleSince, room.idleSinceMillis, "AI tick must not refresh idleSinceMillis")
            }

            // 推进到 inProgressIdleTtlMillis
            clock.advance(15_000L)
            val recycled = room.closeIfIdleOnQueue(policy, room.idleEpoch, clock.nowMillis()).await()
            assertTrue(recycled, "Room must be recycled despite continuous AI ticks")
            assertTrue(room.closed)
        } finally {
            room.close()
        }
    }

    @Test
    fun `R06-T5 spectator capacity limit 32 and slot reuse`() {
        val capacityPolicy = RoomCapacityPolicy(maxSpectators = 32)
        val room = SharedRoom(roomId = "SPEC_CAP", capacityPolicy = capacityPolicy)

        try {
            val specList = mutableListOf<SharedRoom.JoinSpectatorResult>()
            // 依次加入 32 个观战者 -> 全部成功
            for (i in 1..32) {
                val res = room.joinSpectator("Spec-$i").await()
                specList.add(res)
            }
            assertEquals(32, room.spectators.size)

            // 第 33 个加入 -> 必须被拒 (ROOM_FULL / SPECTATOR_LIMIT)
            val ex = assertThrows<ExecutionException> {
                room.joinSpectator("Spec-33").await()
            }
            val apiEx = ex.cause as? ServerApiException
            assertNotNull(apiEx)
            assertEquals(ApiErrors.ROOM_FULL, apiEx?.code)
            assertEquals("SPECTATOR_LIMIT", apiEx?.message)

            // 断线成员仍占名额：断开第 1 个观战者
            val firstSpec = specList[0]
            room.disconnectSpectator(firstSpec.spectatorId, firstSpec.generation).await()
            assertEquals(32, room.spectators.size, "Disconnected spectator must retain seat reservation")

            // 此时第 33 个仍然被拒
            val ex2 = assertThrows<ExecutionException> {
                room.joinSpectator("Spec-33-Retry").await()
            }
            val apiEx2 = ex2.cause as? ServerApiException
            assertEquals(ApiErrors.ROOM_FULL, apiEx2?.code)

            // resume 既有成员不受上限影响
            val resumeRes = room.resumeSpectator(firstSpec.spectatorId, firstSpec.resumeToken).await()
            assertEquals(firstSpec.spectatorId, resumeRes.spectatorId)

            // 显式 leave 释放名额
            val leaveReceipt = room.leaveSpectator(firstSpec.spectatorId).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
            assertEquals(31, room.spectators.size)

            // 释放后第 32 个名额重新可用
            val newSpec = room.joinSpectator("Spec-New").await()
            assertNotNull(newSpec)
            assertEquals(32, room.spectators.size)
        } finally {
            room.close()
        }
    }

    @Test
    fun `R06-T11 evicted room resume tokens cannot resurrect room`() {
        val clock = ManualMonotonicClock(1000L)
        val policy = RoomRetentionPolicy(lobbyIdleTtlMillis = 5_000L)
        val room = SharedRoom(roomId = "RESURRECT_TEST", clock = clock)

        try {
            room.markSeatConnected(0, room.bindingGenerations[0]).await()
            val oldHostToken = room.resumeTokens[0]
            room.disconnectSeat(0, room.bindingGenerations[0]).await()

            clock.advance(10_000L)
            val recycled = room.closeIfIdleOnQueue(policy, room.idleEpoch, clock.nowMillis()).await()
            assertTrue(recycled)
            assertTrue(room.closed)

            // 尝试使用旧令牌恢复被回收房间 -> 抛出 ROOM_NOT_FOUND (ROOM_CLOSED)
            val ex = assertThrows<ExecutionException> {
                room.resumeSession(oldHostToken).await()
            }
            val cause = ex.cause as? ServerApiException
            assertNotNull(cause)
            assertEquals(ApiErrors.ROOM_NOT_FOUND, cause?.code)
        } finally {
            room.close()
        }
    }

    @Test
    fun `R06-T13 in-process server room is not automatically recycled`() {
        val clock = ManualMonotonicClock(1000L)
        // 模拟 InProcess 房间：未启动 TcpTableServer 巡检调度器
        val room = SharedRoom(
            roomId = "IN_PROCESS_ROOM",
            isSingleplayer = true,
            clock = clock
        )

        try {
            assertFalse(room.closed)
            // 推进大量虚拟时间（如 10 小时）
            clock.advance(10 * 3600 * 1000L)

            // 房间没有任何自主后台线程，状态保持未关闭
            assertFalse(room.closed, "InProcess room must not be automatically recycled")
            assertEquals(ProtocolRoomLifecycle.LOBBY, room.lifecycle)
        } finally {
            room.close()
        }
    }

    @Test
    fun `R2 regression monotonic and wall clock separation prevents premature patrol recycling`() {
        val monoClock = ManualMonotonicClock(1000L)
        val registry = RoomRegistry(clock = monoClock)
        val policy = RoomRetentionPolicy(
            lobbyIdleTtlMillis = 300_000L,
            unengagedRoomTtlMillis = 60_000L
        )
        val server = TcpTableServer(
            config = TcpServerConfig(port = 0, retentionPolicy = policy),
            registry = registry
        )
        server.start()

        try {
            val room = registry.createRoom()
            assertNotNull(room.idleSinceMillis)
            assertEquals(1000L, room.idleSinceMillis)

            // 推进虚拟单调时钟 10s (未达 60s TTL)
            monoClock.advance(10_000L)

            // 触发巡检（不传参，走默认 now = registry.clock.nowMillis()）：由于以 registry.clock (monoClock) 为基准，10s < 60s，决不误回收
            server.performRoomPatrol()
            Thread.sleep(50)

            assertFalse(room.closed, "Room must not be prematurely recycled before TTL is reached")
            assertEquals(1, registry.activeRooms().size)

            // 推进虚拟单调时钟超过 60s TTL (再推进 60s，总计 70s)
            monoClock.advance(60_000L)

            // 再次触发巡检（不传参，走默认 now = registry.clock.nowMillis()）：到达 TTL，必须被成功回收
            server.performRoomPatrol()
            Thread.sleep(50)

            assertTrue(room.closed, "Room must be recycled after TTL is reached")
            assertEquals(0, registry.activeRooms().size)
        } finally {
            server.close()
        }
    }

    @Test
    fun `R3 regression spectator join disconnect resume and disconnect again resets isConnected and enters idle`() {
        val clock = ManualMonotonicClock(1000L)
        val registry = RoomRegistry(clock = clock)
        val policy = RoomRetentionPolicy(lobbyIdleTtlMillis = 5_000L)
        val server = TcpTableServer(
            config = TcpServerConfig(port = 0, retentionPolicy = policy),
            registry = registry
        )
        server.start()

        val room = registry.createRoom()
        try {
            val roomId = room.roomId

            // 1. 观战者通过 TCP 客户端 1 加入 (generation = 1)
            var spectatorId: String
            var resumeToken: String
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome1 = client1.expectWelcome()

                val joinReq = ApiRequest(
                    requestId = "req-r3-join",
                    api = ApiTags.JOIN_SPECTATOR,
                    roomId = roomId,
                    spectatorName = "SpecA"
                )
                val env = TcpRequestEnvelope(
                    requestScope = welcome1.lobbyScope.scope,
                    requestId = "req-r3-join",
                    request = joinReq
                )
                client1.sendFrame(TcpMessageCodec.toFrame(env))

                val resp = client1.expectResponse()
                assertTrue(resp.response.success)
                spectatorId = resp.response.spectatorBinding?.spectatorId ?: error("Expected spectatorId")
                resumeToken = resp.response.spectatorBinding?.resumeToken ?: error("Expected resumeToken")

                assertEquals(1, room.connectedSpectatorCount())
            }

            // 客户端 1 关闭后，服务端注销连接并触发 notifyRoomDisconnect
            Thread.sleep(100)
            assertEquals(0, room.connectedSpectatorCount(), "Spectator connected count must be 0 after client1 disconnect")
            assertNotNull(room.idleSinceMillis, "Room must enter idle after client1 disconnect")

            // 2. 观战者通过 TCP 客户端 2 恢复观战 (RESUME_SPECTATOR, generation = 2)
            TcpTestClient(port = server.boundPort).use { client2 ->
                client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome2 = client2.expectWelcome()

                val resumeReq = ApiRequest(
                    requestId = "req-r3-resume",
                    api = ApiTags.RESUME_SPECTATOR,
                    roomId = roomId,
                    spectatorId = spectatorId,
                    token = resumeToken
                )
                val env = TcpRequestEnvelope(
                    requestScope = welcome2.lobbyScope.scope,
                    requestId = "req-r3-resume",
                    request = resumeReq
                )
                client2.sendFrame(TcpMessageCodec.toFrame(env))

                val resp = client2.expectResponse()
                assertTrue(resp.response.success)

                assertEquals(1, room.connectedSpectatorCount(), "Spectator must be connected after resume")
                assertNull(room.idleSinceMillis, "Room must not be idle while spectator is connected")
            }

            // 客户端 2 关闭后，此时 generation = 2 必须被正确传递到 disconnectSpectator
            Thread.sleep(100)
            assertEquals(0, room.connectedSpectatorCount(), "Spectator connected count must be 0 after second disconnect")
            assertNotNull(room.idleSinceMillis, "Room must enter idle after second spectator disconnect")
        } finally {
            room.close()
            server.close()
        }
    }

    @Test
    fun `R4 regression unclaimed seat in join window is reconciled by patrol and recycled after TTL`() {
        val clock = ManualMonotonicClock(1000L)
        val registry = RoomRegistry(clock = clock)
        val policy = RoomRetentionPolicy(lobbyIdleTtlMillis = 300_000L, unengagedRoomTtlMillis = 60_000L)
        val server = TcpTableServer(
            config = TcpServerConfig(port = 0, retentionPolicy = policy),
            registry = registry
        )
        server.start()

        try {
            val room = registry.createRoom()
            // 模拟 join 后未完成传输 bind（无人认领）
            val joinRes = room.join("UnclaimedGuest").await()
            val guestSeat = joinRes.first
            assertTrue(room.seatConnected[guestSeat], "join() must set seatConnected to true")
            assertNull(room.idleSinceMillis)

            // 第一次巡检：此时 connections 为空，对账兜底清除未认领的 seatConnected，启动空闲计时
            server.performRoomPatrol(clock.nowMillis())
            Thread.sleep(50)

            assertFalse(room.seatConnected[guestSeat], "Reconciliation must clear seatConnected for unclaimed seat")
            assertNotNull(room.idleSinceMillis, "Room must start idle timing after reconciliation")

            // 推进虚拟时间超过未参与房间短 TTL (60s)
            clock.advance(70_000L)

            // 第二次巡检：到达 TTL，执行回收
            server.performRoomPatrol(clock.nowMillis())
            Thread.sleep(50)

            assertTrue(room.closed, "Unclaimed room must be recycled after TTL")
            assertEquals(0, registry.activeRooms().size)
        } finally {
            server.close()
        }
    }
}

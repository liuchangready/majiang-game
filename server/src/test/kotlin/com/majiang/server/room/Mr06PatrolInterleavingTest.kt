package com.majiang.server.room

import com.majiang.timing.ManualMonotonicClock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/**
 * MR-06 巡检连接快照交错与创建者恢复裁决确定性回归测试
 */
@Timeout(15)
class Mr06PatrolInterleavingTest {

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test
    fun `sample old connection then new bind and reconnect succeeds then old reconcile executes preserves connection and idleSince is null`() {
        val clock = ManualMonotonicClock(1000L)
        val room = SharedRoom(roomId = "INTERLEAVING_ROOM", clock = clock)
        try {
            // 房主先断开连接进入离线
            val hostToken = room.resumeTokens[0]
            room.disconnectSeat(0, room.bindingGenerations[0]).await()
            assertFalse(room.seatConnected[0])
            assertNotNull(room.idleSinceMillis)

            // 1. 取样旧连接（此时席位 0 离线，代次为 1L）
            val sampledGens = mapOf(0 to room.bindingGenerations[0])
            val oldAliveSeats = emptyMap<Int, Long>()

            // 2. 新绑定/重连成功（resumeSession 递增代次至 2L 并恢复 seatConnected）
            val resumeRes = room.resumeSession(hostToken).await()
            val newGen = resumeRes.generation
            assertEquals(2L, newGen)
            room.markSeatConnected(0, newGen).await()
            assertTrue(room.seatConnected[0], "Seat 0 must be connected after reconnect")
            assertNull(room.idleSinceMillis, "idleSinceMillis must be null when reconnected")

            // 3. 旧对账执行（携带旧取样代次 1L）
            room.reconcileConnectivityOnQueue(
                aliveSeats = oldAliveSeats,
                aliveSpectatorIds = emptySet(),
                sampledSeatGenerations = sampledGens
            ).await()

            // 4. 契约断言：真人仍连接、idleSince=null
            assertTrue(room.seatConnected[0], "Human seat 0 must remain connected despite old reconcile executing")
            assertNull(room.idleSinceMillis, "idleSinceMillis must remain null")
        } finally {
            room.close()
        }
    }

    @Test
    fun `subsequent patrol restores connection state when live connection matches current generation`() {
        val clock = ManualMonotonicClock(1000L)
        val room = SharedRoom(roomId = "COMPENSATE_ROOM", clock = clock)
        try {
            // 房主连接曾被置为 false
            room.disconnectSeat(0, room.bindingGenerations[0]).await()
            assertFalse(room.seatConnected[0])
            assertNotNull(room.idleSinceMillis)

            // 后续巡检发现当前代次的存活连接，执行对账"补"回状态
            val currentGens = mapOf(0 to room.bindingGenerations[0])
            room.reconcileConnectivityOnQueue(
                aliveSeats = currentGens,
                aliveSpectatorIds = emptySet(),
                sampledSeatGenerations = currentGens
            ).await()

            assertTrue(room.seatConnected[0], "Seat 0 connection must be compensated and restored to true")
            assertNull(room.idleSinceMillis, "idleSinceMillis must be cleared when connection is restored")
        } finally {
            room.close()
        }
    }

    @Test
    fun `creator active recovery prior to final authority adjudication preserves room`() {
        val clock = ManualMonotonicClock(1000L)
        val policy = RoomRetentionPolicy(
            unengagedRoomTtlMillis = 60_000L,
            lobbyIdleTtlMillis = 300_000L
        )

        val room = SharedRoom(roomId = "UNENGAGED_CREATOR_ROOM", clock = clock)
        try {
            assertNotNull(room.idleSinceMillis)

            // 推进时钟超过 60 秒 (已到达 unengagedRoomTtl 阈值，但未达 lobby 300 秒)
            clock.advance(70_000L)

            // 在终审裁决执行时，创建者连接已恢复 (creatorActiveSupplier 返回 true)
            var creatorIsAlive = true
            val closed = room.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = room.idleEpoch,
                nowMillis = clock.nowMillis(),
                creatorActive = false, // 外部旧取样为 false
                creatorActiveSupplier = { creatorIsAlive } // 权威队列内实时代次与真值
            ).await()

            assertFalse(closed, "Room must NOT be recycled because creator became active before adjudication")
            assertFalse(room.closed, "Room must remain open")

            // 创建者再次离线并推进至超过 lobby TTL (300s)
            creatorIsAlive = false
            clock.advance(240_000L) // 累计 310s > 300s
            val recycled = room.closeIfIdleOnQueue(
                policy = policy,
                capturedIdleEpoch = room.idleEpoch,
                nowMillis = clock.nowMillis(),
                creatorActive = false,
                creatorActiveSupplier = { false }
            ).await()

            assertTrue(recycled, "Room must be recycled after exceeding lobby TTL without active creator")
            assertTrue(room.closed, "Room must be closed")
        } finally {
            room.close()
        }
    }
}

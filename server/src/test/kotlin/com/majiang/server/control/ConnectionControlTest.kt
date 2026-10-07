package com.majiang.server.control

import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ConnectionControlTest {

    private class ManualClock(var current: Long = 1000L) : MonotonicClock {
        override fun nowMillis(): Long = current
    }

    @Test
    fun `immediate ai mode transitions to takeover immediately on disconnect`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.IMMEDIATE_AI, aiProfileId = "test-ai")
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        val before = ctrl.seatState(0)
        assertTrue(before.isConnected)
        assertTrue(before.isHuman)
        assertEquals(0L, before.controlEpoch)

        val state = ctrl.disconnect(0, 1000L)
        assertTrue(state.isDisconnected)
        assertTrue(state.isTakenOver)
        assertEquals(1L, state.controlEpoch)
        assertEquals(1000L, state.disconnectedAtMillis)
        assertNull(state.graceDeadlineMillis)
        assertEquals("test-ai", state.aiProfileId)
    }

    @Test
    fun `grace then ai mode waits for deadline before takeover`() {
        val clock = ManualClock(2000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.GRACE_THEN_AI, graceMillis = 5000L, aiProfileId = "grace-ai")
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        // 1. 断线发生，进入宽限期
        val disconnected = ctrl.disconnect(0, 2000L)
        assertTrue(disconnected.isDisconnected)
        assertTrue(disconnected.isHuman) // 宽限期内保持 HUMAN
        assertEquals(0L, disconnected.controlEpoch)
        assertEquals(2000L, disconnected.disconnectedAtMillis)
        assertEquals(7000L, disconnected.graceDeadlineMillis)

        // 2. 宽限期未到
        assertFalse(ctrl.isTakeoverNeeded(0, 6999L))
        assertFalse(ctrl.checkGraceExpiration(0, 6999L))
        val midState = ctrl.seatState(0)
        assertTrue(midState.isHuman)
        assertEquals(0L, midState.controlEpoch)

        // 3. 宽限期到期
        assertTrue(ctrl.isTakeoverNeeded(0, 7000L))
        val expired = ctrl.checkGraceExpiration(0, 7000L)
        assertTrue(expired)

        val after = ctrl.seatState(0)
        assertTrue(after.isTakenOver)
        assertEquals(1L, after.controlEpoch)
        assertNull(after.graceDeadlineMillis)

        // 4. 重复检查不再触发
        assertFalse(ctrl.checkGraceExpiration(0, 7001L))
    }

    @Test
    fun `pollPendingTakeovers finds all expired seats`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.GRACE_THEN_AI, graceMillis = 3000L)
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        ctrl.disconnect(0, 1000L) // deadline = 4000L
        ctrl.disconnect(1, 2000L) // deadline = 5000L

        val at3500 = ctrl.pollPendingTakeovers(3500L)
        assertTrue(at3500.isEmpty())

        val at4500 = ctrl.pollPendingTakeovers(4500L)
        assertEquals(listOf(0), at4500)
        assertTrue(ctrl.seatState(0).isTakenOver)
        assertTrue(ctrl.seatState(1).isHuman)

        val at5000 = ctrl.pollPendingTakeovers(5000L)
        assertEquals(listOf(1), at5000)
        assertTrue(ctrl.seatState(1).isTakenOver)
    }

    @Test
    fun `timeout only mode never triggers ai takeover`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.TIMEOUT_ONLY)
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        val state = ctrl.disconnect(0, 1000L)
        assertTrue(state.isDisconnected)
        assertTrue(state.isHuman)
        assertEquals(0L, state.controlEpoch)
        assertNull(state.graceDeadlineMillis)

        assertFalse(ctrl.isTakeoverNeeded(0, 99999L))
        assertFalse(ctrl.checkGraceExpiration(0, 99999L))
        assertTrue(ctrl.seatState(0).isHuman)
    }

    @Test
    fun `repeated disconnect is idempotent and does not extend grace deadline`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.GRACE_THEN_AI, graceMillis = 5000L)
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        val first = ctrl.disconnect(0, 1000L)
        assertEquals(1000L, first.disconnectedAtMillis)
        assertEquals(6000L, first.graceDeadlineMillis)

        // 再次断线（可能由网络抖动触发）
        val second = ctrl.disconnect(0, 3000L)
        assertEquals(1000L, second.disconnectedAtMillis)
        assertEquals(6000L, second.graceDeadlineMillis)
        assertEquals(0L, second.controlEpoch)
    }

    @Test
    fun `reconnect without reclaim keeps takeover and requires explicit reclaimControl`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.IMMEDIATE_AI)
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        ctrl.disconnect(0, 1000L)
        assertTrue(ctrl.seatState(0).isTakenOver)
        assertEquals(1L, ctrl.getEpoch(0))

        // 重连，reclaim = false
        val reconnected = ctrl.reconnect(0, 2000L, reclaim = false)
        assertTrue(reconnected.isConnected)
        assertTrue(reconnected.isTakenOver) // 仍然是 TAKEOVER
        assertEquals(1L, reconnected.controlEpoch) // 代次未变

        // 显式收回控制权
        val reclaimed = ctrl.reclaimControl(0)
        assertTrue(reclaimed.isHuman)
        assertEquals(2L, reclaimed.controlEpoch) // 代次自增，废除托管期间决策

        // 重复收回幂等
        val idempotent = ctrl.reclaimControl(0)
        assertEquals(2L, idempotent.controlEpoch)
    }

    @Test
    fun `reconnect with reclaim restores human role and increments epoch`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.IMMEDIATE_AI)
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        ctrl.disconnect(0, 1000L)
        assertEquals(1L, ctrl.getEpoch(0))

        // 重连同时收回
        val reconnected = ctrl.reconnect(0, 2000L, reclaim = true)
        assertTrue(reconnected.isConnected)
        assertTrue(reconnected.isHuman)
        assertEquals(2L, reconnected.controlEpoch)
        assertNull(reconnected.disconnectedAtMillis)
        assertNull(reconnected.graceDeadlineMillis)
    }

    @Test
    fun `stale expected epoch is rejected on grace check`() {
        val clock = ManualClock(1000L)
        val policy = DisconnectPolicy(mode = DisconnectMode.GRACE_THEN_AI, graceMillis = 2000L)
        val ctrl = ConnectionControl(policy = policy, clock = clock, seatCount = 4)

        ctrl.disconnect(0, 1000L)
        // 传入不匹配的 expectedEpoch
        assertFalse(ctrl.checkGraceExpiration(0, 5000L, expectedEpoch = 99L))
        assertFalse(ctrl.isTakeoverNeeded(0, 5000L, expectedEpoch = 99L))
        // 仍为 HUMAN，因为任务过期被拦截
        assertTrue(ctrl.seatState(0).isHuman)

        // 传入匹配的 expectedEpoch
        assertTrue(ctrl.checkGraceExpiration(0, 5000L, expectedEpoch = 0L))
        assertTrue(ctrl.seatState(0).isTakenOver)
    }

    @Test
    fun `requestControl and setRole behave correctly`() {
        val ctrl = ConnectionControl()

        // requestControl(takeover = true)
        val takenOver = ctrl.requestControl(0, takeover = true)
        assertTrue(takenOver.isTakenOver)
        assertEquals(1L, takenOver.controlEpoch)

        // 重复 requestControl(takeover = true) 幂等
        val same = ctrl.requestControl(0, takeover = true)
        assertEquals(1L, same.controlEpoch)

        // requestControl(takeover = false)
        val reclaimed = ctrl.requestControl(0, takeover = false)
        assertTrue(reclaimed.isHuman)
        assertEquals(2L, reclaimed.controlEpoch)

        // setRole 显式设置
        val comp = ctrl.setRole(0, ControllerRole.COMPUTER)
        assertTrue(comp.isComputer)
        assertEquals(3L, comp.controlEpoch)

        // setRole 相同角色幂等
        val compSame = ctrl.setRole(0, ControllerRole.COMPUTER)
        assertEquals(3L, compSame.controlEpoch)

        val all = ctrl.allSeatStates()
        assertEquals(4, all.size)
        assertEquals(ControllerRole.COMPUTER, all[0].role)
        assertEquals(ControllerRole.HUMAN, all[1].role)
    }
}

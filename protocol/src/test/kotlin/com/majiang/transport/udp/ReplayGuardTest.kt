package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReplayGuardTest {

    @Test
    fun `strictly rejects replayed duplicate seq`() {
        val guard = ReplayGuard(initialMaxSeen = 0)

        // 首次到达 seq=1，接受
        assertTrue(guard.accept(1))
        assertEquals(1, guard.getMaxSeen())

        // 重复到达 seq=1，防重放拒绝
        assertFalse(guard.accept(1))
        assertEquals(1, guard.getMaxSeen())

        // 递增到 seq=2，接受
        assertTrue(guard.accept(2))

        // 再次发送 seq=2 或更老 seq=1，拒绝
        assertFalse(guard.accept(2))
        assertFalse(guard.accept(1))
    }

    @Test
    fun `accepts out-of-order seq within window and rejects duplicates and beyond-window packets`() {
        val guard = ReplayGuard(initialMaxSeen = 0, windowSize = 256)

        // 收到 seq=1
        assertTrue(guard.accept(1))

        // 乱序向前跳跃：网络中 2..4 丢失或延迟，直接收到 seq=5，允许接受
        assertTrue(guard.accept(5))
        assertEquals(5, guard.getMaxSeen())

        // 再次向前跳跃到 seq=12，允许接受
        assertTrue(guard.accept(12))
        assertEquals(12, guard.getMaxSeen())

        // 迟到的旧包 seq=3 在窗口内且未见，允许接受
        assertTrue(guard.accept(3))
        // 再次到达已见过的 seq=3，拒绝
        assertFalse(guard.accept(3))

        // 重复到达 seq=5（已在之前接收），拒绝
        assertFalse(guard.accept(5))

        // 迟到的旧包 seq=8 在窗口内且未见，允许接受
        assertTrue(guard.accept(8))
        // 再次到达已见过的 seq=8，拒绝
        assertFalse(guard.accept(8))

        // 重复到达 seq=12，拒绝
        assertFalse(guard.accept(12))

        // 此时到达新包 seq=13，正常接受
        assertTrue(guard.accept(13))
        assertEquals(13, guard.getMaxSeen())

        // 超出窗口大小（windowSize = 256）：向前跃迁至 seq=300
        assertTrue(guard.accept(300))
        // seq=20 小于 (300 - 256 = 44)，超出窗口左边界，拒绝
        assertFalse(guard.accept(20))
    }

    @Test
    fun `connectionId validation behaves correctly`() {
        val boundGuard = ReplayGuard(expectedConnectionId = 12345)
        assertTrue(boundGuard.validateConnectionId(12345))
        assertFalse(boundGuard.validateConnectionId(99999))
        assertFalse(boundGuard.validateConnectionId(0))

        // 未绑定连接允许任意 connectionId
        val unboundGuard = ReplayGuard(expectedConnectionId = null)
        assertTrue(unboundGuard.validateConnectionId(0))
        assertTrue(unboundGuard.validateConnectionId(12345))
        assertTrue(unboundGuard.validateConnectionId(99999))

        val frameMatch = UdpFrame(packetType = UdpPacketType.PING, connectionId = 555, seq = 1)
        val frameMismatch = UdpFrame(packetType = UdpPacketType.PING, connectionId = 666, seq = 1)
        assertTrue(ReplayGuard.isConnectionValid(frameMatch, 555))
        assertFalse(ReplayGuard.isConnectionValid(frameMismatch, 555))
    }

    @Test
    fun `reset sets new maxSeen baseline`() {
        val guard = ReplayGuard(initialMaxSeen = 50)
        assertFalse(guard.accept(40))

        guard.reset(newMaxSeen = 10)
        assertEquals(10, guard.getMaxSeen())
        assertTrue(guard.accept(20))
    }
}

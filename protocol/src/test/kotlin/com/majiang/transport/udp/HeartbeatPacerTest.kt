package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class HeartbeatPacerTest {

    @Test
    fun `periodic ping sent every 3 seconds`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val pings = mutableListOf<Long>()

        val pacer = HeartbeatPacer(
            clock = scheduler,
            scheduler = scheduler,
            pingSender = { pings.add(it) },
            onDead = { }
        )

        pacer.start()
        assertEquals(0, pings.size)

        // 推进 3000ms，触发首个 PING (时刻 4000)
        scheduler.advanceTimeBy(3000L)
        assertEquals(1, pings.size)
        assertEquals(4000L, pings[0])

        // 模拟对端立即回发 PONG
        pacer.onPong(clientMillis = 4000L, serverMillis = 4050L, arrivalTime = 4100L)

        // 再次推进 3000ms，触发第 2 个 PING (时刻 7000)
        scheduler.advanceTimeBy(3000L)
        assertEquals(2, pings.size)
        assertEquals(7000L, pings[1])
    }

    @Test
    fun `rtt calculation with EWMA smoothing`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val pacer = HeartbeatPacer(
            clock = scheduler,
            scheduler = scheduler,
            pingSender = { },
            onDead = { },
            rttEwmaAlpha = 0.875
        )

        pacer.start()

        // 样本 1: 发送 4000，到达 4100 -> sample = 100ms
        pacer.onPong(clientMillis = 4000L, serverMillis = 4050L, arrivalTime = 4100L)
        assertEquals(100.0, pacer.getEstimatedRtt(), 0.001)

        // 样本 2: 发送 7000，到达 7050 -> sample = 50ms
        // EWMA: 0.875 * 100.0 + 0.125 * 50.0 = 87.5 + 6.25 = 93.75
        pacer.onPong(clientMillis = 7000L, serverMillis = 7025L, arrivalTime = 7050L)
        assertEquals(93.75, pacer.getEstimatedRtt(), 0.001)

        // 样本 3: 发送 10000，到达 10200 -> sample = 200ms
        // EWMA: 0.875 * 93.75 + 0.125 * 200.0 = 82.03125 + 25.0 = 107.03125
        pacer.onPong(clientMillis = 10000L, serverMillis = 10100L, arrivalTime = 10200L)
        assertEquals(107.03125, pacer.getEstimatedRtt(), 0.001)
    }

    @Test
    fun `connection dead after 3 consecutive missed pongs`() {
        val scheduler = FakeClockAndScheduler(1000L)
        var deadReason: String? = null

        val pacer = HeartbeatPacer(
            clock = scheduler,
            scheduler = scheduler,
            pingSender = { },
            onDead = { deadReason = it },
            maxMissedPongs = 3
        )

        pacer.start()
        assertFalse(pacer.isDead())

        // T=4000: Tick 1 发送 PING 1 (awaitingPong = true, missed = 0)
        scheduler.advanceTimeBy(3000L)
        assertEquals(0, pacer.getMissedPongs())
        pacer.onInboundPacket(4500L) // 维持入站活跃，避免触发 10s READ_TIMEOUT

        // T=7000: Tick 2 未收到前次 PONG，missed -> 1
        scheduler.advanceTimeBy(3000L)
        assertEquals(1, pacer.getMissedPongs())
        pacer.onInboundPacket(7500L)

        // T=10000: Tick 3 未收到前次 PONG，missed -> 2
        scheduler.advanceTimeBy(3000L)
        assertEquals(2, pacer.getMissedPongs())
        pacer.onInboundPacket(10500L)

        // T=13000: Tick 4 未收到前次 PONG，missed -> 3，达到阈值判死
        scheduler.advanceTimeBy(3000L)
        assertTrue(pacer.isDead())
        assertEquals(UdpProtocol.REASON_MISSED_HEARTBEATS, deadReason)
    }

    @Test
    fun `connection dead after 10s without any inbound packet`() {
        val scheduler = FakeClockAndScheduler(1000L)
        var deadReason: String? = null

        val pacer = HeartbeatPacer(
            clock = scheduler,
            scheduler = scheduler,
            pingSender = { },
            onDead = { deadReason = it },
            inboundTimeoutMillis = 10000L
        )

        pacer.start()

        // T=4000: 正常收到一次 PONG，lastInbound 刷新为 4000
        scheduler.advanceTimeBy(3000L)
        pacer.onPong(4000L, 4000L, 4000L)
        assertEquals(4000L, pacer.getLastInboundTime())

        // 随后链路完全静默，无任何入站包到达
        // 推进 9999ms (时刻 13999)，距 4000 还差 1ms 不到 10s
        scheduler.advanceTimeBy(9999L)
        assertFalse(pacer.isDead())

        // 推进 1ms (时刻 14000)，静默已满 10000ms，但判死检测随 3s 心跳 tick 触发，此刻尚未检测
        scheduler.advanceTimeBy(1L)
        assertFalse(pacer.isDead())

        // 推进至下一个心跳 tick (时刻 16000)，检测到 12000ms 无入站包，判死
        scheduler.advanceTimeBy(2000L)
        assertTrue(pacer.isDead())
        assertEquals(UdpProtocol.REASON_READ_TIMEOUT, deadReason)
    }
}

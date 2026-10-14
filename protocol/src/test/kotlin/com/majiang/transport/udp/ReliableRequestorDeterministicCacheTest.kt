package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReliableRequestorDeterministicCacheTest {

    private fun complete(
        requestor: ReliableRequestor,
        requestId: String,
        rspSeq: Int,
        payload: ByteArray = "test-ok".toByteArray()
    ): UdpFrame {
        val future = requestor.send(requestId, payload)
        val rsp = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 202,
            seq = rspSeq,
            ackSeq = 0,
            payload = payload
        )
        val handled = requestor.onResponse(rsp, requestId = requestId)
        assertTrue(handled, "onResponse for $requestId must be handled")
        assertTrue(future.isDone, "future for $requestId must be done")
        return rsp
    }

    @Test
    fun `test deterministic FIFO eviction of completed records at capacity`() {
        val clock = FakeClockAndScheduler(1000L)
        val acks = mutableListOf<UdpFrame>()
        val requestor = ReliableRequestor(
            sender = {},
            clock = clock,
            scheduler = clock,
            connectionIdSupplier = { 202 },
            ackSender = { acks.add(it) },
            completedCapacity = 3,
            completedRetentionMillis = 60_000L
        )

        complete(requestor, "req-1", 101)
        complete(requestor, "req-2", 102)
        complete(requestor, "req-3", 103)
        assertEquals(3, acks.size)

        // 第 4 个完成，超出容量 3：按 FIFO 淘汰最老的 req-1
        complete(requestor, "req-4", 104)
        assertEquals(4, acks.size)

        // req-1 已被淘汰：按 ID 或 seq 重试均返回 false
        assertFalse(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 101, 0, "dup".toByteArray()),
                requestId = "req-1"
            )
        )
        assertFalse(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 101, 0, "dup".toByteArray())
            )
        )

        // req-2, req-3, req-4 仍在保留池中
        assertTrue(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 102, 0, "dup".toByteArray()),
                requestId = "req-2"
            )
        )
        assertTrue(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 103, 0, "dup".toByteArray())
            )
        )
        assertTrue(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 104, 0, "dup".toByteArray()),
                requestId = "req-4"
            )
        )
    }

    @Test
    fun `test expiration pruning takes precedence over FIFO capacity eviction`() {
        val clock = FakeClockAndScheduler(1000L)
        val acks = mutableListOf<UdpFrame>()
        val requestor = ReliableRequestor(
            sender = {},
            clock = clock,
            scheduler = clock,
            connectionIdSupplier = { 202 },
            ackSender = { acks.add(it) },
            completedCapacity = 3,
            completedRetentionMillis = 5_000L
        )

        complete(requestor, "req-old-1", 201)
        complete(requestor, "req-old-2", 202)

        // 前推 6 秒：此时 req-old-1 与 req-old-2 均已超过 5s 保留期
        clock.advanceTimeBy(6_000L)

        // 插入新记录，触发过期清理
        complete(requestor, "req-new-1", 301)
        complete(requestor, "req-new-2", 302)

        // 老记录已因过期清理被淘汰，seq 索引也被清除
        assertFalse(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 201, 0, "dup".toByteArray()),
                requestId = "req-old-1"
            )
        )
        assertFalse(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 202, 0, "dup".toByteArray())
            )
        )

        // 新记录在保留期内依然生效
        assertTrue(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 301, 0, "dup".toByteArray()),
                requestId = "req-new-1"
            )
        )
        assertTrue(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 302, 0, "dup".toByteArray())
            )
        )
    }

    @Test
    fun `test duplicate hits do not extend retention anchor`() {
        val clock = FakeClockAndScheduler(1000L)
        val acks = mutableListOf<UdpFrame>()
        val requestor = ReliableRequestor(
            sender = {},
            clock = clock,
            scheduler = clock,
            connectionIdSupplier = { 202 },
            ackSender = { acks.add(it) },
            completedCapacity = 10,
            completedRetentionMillis = 10_000L
        )

        complete(requestor, "req-anchor", 401)
        assertEquals(1, acks.size)

        // 在 t=5000 重复命中（在保留期内）
        clock.advanceTimeBy(5_000L)
        assertTrue(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 401, 0, "dup".toByteArray()),
                requestId = "req-anchor"
            )
        )
        assertEquals(2, acks.size)

        // 再前推 5001ms（自首次完成累计 10001ms，已超过 10s）
        clock.advanceTimeBy(5_001L)
        complete(requestor, "req-other", 402)

        // 虽然中间有重放命中，但锚点仍是首次完成时间，必须已过期
        assertFalse(
            requestor.onResponse(
                UdpFrame(UdpPacketType.RSP, 202, 401, 0, "dup".toByteArray()),
                requestId = "req-anchor"
            )
        )
    }
}

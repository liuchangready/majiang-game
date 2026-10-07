package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class PushRetransmitWindowTest {

    @Test
    fun `in-window gap returns sequential records for retransmission`() {
        val window = PushRetransmitWindow(maxCapacityPerConnection = 256)
        val connId = 100

        // 发送游标 1..5 的 5 个包
        for (c in 1L..5L) {
            val payload = "payload-$c".toByteArray(Charsets.UTF_8)
            window.record(connId, cursor = c, fromCursor = c - 1, payload = payload)
        }

        assertEquals(5, window.getWindowSize(connId))

        // 客户端请求补发从 3 开始的缺口
        val fullSnapshot = "FULL_SNAPSHOT".toByteArray(Charsets.UTF_8)
        val res = window.handleResend(connId, requestedFromCursor = 3L) { fullSnapshot }

        assertTrue(res is ResendResponse.Retransmit)
        val records = (res as ResendResponse.Retransmit).records
        assertEquals(3, records.size)
        assertEquals(3L, records[0].cursor)
        assertEquals(4L, records[1].cursor)
        assertEquals(5L, records[2].cursor)
        assertEquals("payload-3", String(records[0].payload, Charsets.UTF_8))
    }

    @Test
    fun `window evicts oldest records when capacity exceeds 256`() {
        val window = PushRetransmitWindow(maxCapacityPerConnection = 256)
        val connId = 200

        // 写入 300 条推送记录 (cursor 1..300)
        for (c in 1L..300L) {
            window.record(connId, cursor = c, fromCursor = c - 1, payload = byteArrayOf(c.toByte()))
        }

        // 内存严格有界，容量恒定为 256
        assertEquals(256, window.getWindowSize(connId))

        // 此时最老的保留记录应为 cursor=45 (1..44 被淘汰)
        // 客户端索要 cursor=100（在 45..300 范围内，窗口内）
        val resInWindow = window.handleResend(connId, requestedFromCursor = 100L) { ByteArray(0) }
        assertTrue(resInWindow is ResendResponse.Retransmit)
        assertEquals(201, (resInWindow as ResendResponse.Retransmit).records.size) // 100..300 共 201 个
        assertEquals(100L, resInWindow.records.first().cursor)
        assertEquals(300L, resInWindow.records.last().cursor)
    }

    @Test
    fun `out-of-window gap returns full snapshot`() {
        val window = PushRetransmitWindow(maxCapacityPerConnection = 256)
        val connId = 300

        // 写入 300 条记录
        for (c in 1L..300L) {
            window.record(connId, cursor = c, fromCursor = c - 1, payload = byteArrayOf(c.toByte()))
        }

        // 客户端索要已淘汰的 cursor=10（当前窗口最老为 45）
        val fullSnapshotBytes = "FULL_SNAPSHOT_DATA".toByteArray(Charsets.UTF_8)
        val res = window.handleResend(connId, requestedFromCursor = 10L) { fullSnapshotBytes }

        assertTrue(res is ResendResponse.FullSnapshot)
        val snapshot = (res as ResendResponse.FullSnapshot).payload
        assertEquals("FULL_SNAPSHOT_DATA", String(snapshot, Charsets.UTF_8))
    }

    @Test
    fun `empty window or non-existing connection returns full snapshot`() {
        val window = PushRetransmitWindow()
        val dummySnapshot = "SNAPSHOT".toByteArray(Charsets.UTF_8)

        val res = window.handleResend(99999, requestedFromCursor = 1L) { dummySnapshot }
        assertTrue(res is ResendResponse.FullSnapshot)
        assertEquals("SNAPSHOT", String((res as ResendResponse.FullSnapshot).payload, Charsets.UTF_8))
    }

    @Test
    fun `removeConnection cleans up resources`() {
        val window = PushRetransmitWindow()
        window.record(123, 1L, 0L, byteArrayOf(1))
        assertEquals(1, window.getWindowSize(123))

        window.removeConnection(123)
        assertEquals(0, window.getWindowSize(123))
    }
}

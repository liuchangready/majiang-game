package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import java.util.concurrent.ExecutionException
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReliableRequestorTest {

    @Test
    fun `successful request response without packet loss`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val sentFrames = mutableListOf<UdpFrame>()
        val acksSent = mutableListOf<UdpFrame>()

        val requestor = ReliableRequestor(
            sender = { sentFrames.add(it) },
            clock = scheduler,
            scheduler = scheduler,
            connectionIdSupplier = { 101 },
            ackSender = { acksSent.add(it) }
        )

        val payload = "{\"api\":\"SUBMIT_CHOICE\"}".toByteArray(Charsets.UTF_8)
        val future = requestor.send("req-001", payload)

        assertFalse(future.isDone)
        assertEquals(1, sentFrames.size)
        val reqFrame = sentFrames[0]
        assertEquals(UdpPacketType.REQ, reqFrame.packetType)
        assertEquals(101, reqFrame.connectionId)

        // 对端响应 RSP
        val rspPayload = "{\"requestId\":\"req-001\",\"status\":\"APPLIED\"}".toByteArray(Charsets.UTF_8)
        val rspFrame = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 101,
            seq = 500,
            ackSeq = reqFrame.seq,
            payload = rspPayload
        )

        val handled = requestor.onResponse(rspFrame, requestId = "req-001")
        assertTrue(handled)
        assertTrue(future.isDone)
        assertEquals(rspFrame, future.get())

        // 自动回发 ACK
        assertEquals(1, acksSent.size)
        assertEquals(UdpPacketType.ACK, acksSent[0].packetType)
        assertEquals(500, acksSent[0].ackSeq)
        assertEquals(0, requestor.getPendingCount())
    }

    @Test
    fun `packet loss on first attempt triggers retransmission and succeeds`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val sentFrames = mutableListOf<UdpFrame>()

        val requestor = ReliableRequestor(
            sender = { sentFrames.add(it) },
            clock = scheduler,
            scheduler = scheduler,
            connectionIdSupplier = { 202 }
        )

        val future = requestor.send("req-002", "hello".toByteArray(Charsets.UTF_8))
        assertEquals(1, sentFrames.size)
        val initialSeq = sentFrames[0].seq

        // 前推 199ms，尚未触发 RTO
        scheduler.advanceTimeBy(199L)
        assertEquals(1, sentFrames.size)
        assertFalse(future.isDone)

        // 前推至 200ms，触发首次重传（RTO = 200ms）
        scheduler.advanceTimeBy(1L)
        assertEquals(2, sentFrames.size)
        val retryFrame = sentFrames[1]
        assertEquals(initialSeq, retryFrame.seq, "Retransmissions must reuse the exact same seq")
        assertEquals(UdpPacketType.REQ, retryFrame.packetType)

        // 重传包得到对端响应
        val rspFrame = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 202,
            seq = 600,
            ackSeq = retryFrame.seq,
            payload = "ok".toByteArray(Charsets.UTF_8)
        )
        val handled = requestor.onResponse(rspFrame)
        assertTrue(handled)
        assertTrue(future.isDone)
        assertEquals(rspFrame, future.get())
        assertEquals(0, requestor.getPendingCount())
    }

    @Test
    fun `connection dead after 5 failed attempts without response`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val sentFrames = mutableListOf<UdpFrame>()
        var deadReason: String? = null

        val requestor = ReliableRequestor(
            sender = { sentFrames.add(it) },
            clock = scheduler,
            scheduler = scheduler,
            connectionIdSupplier = { 303 },
            onConnectionDead = { deadReason = it }
        )

        val future = requestor.send("req-dead", "payload".toByteArray(Charsets.UTF_8))
        assertEquals(1, sentFrames.size) // Attempt 1 at T=1000, next at 1000+200=1200

        scheduler.advanceTimeBy(200L)
        assertEquals(2, sentFrames.size) // Attempt 2 at T=1200, RTO=300ms, next at 1200+300=1500

        scheduler.advanceTimeBy(300L)
        assertEquals(3, sentFrames.size) // Attempt 3 at T=1500, RTO=450ms, next at 1500+450=1950

        scheduler.advanceTimeBy(450L)
        assertEquals(4, sentFrames.size) // Attempt 4 at T=1950, RTO=675ms, next at 1950+675=2625

        scheduler.advanceTimeBy(675L)
        assertEquals(5, sentFrames.size) // Attempt 5 at T=2625, RTO=1012ms, next at 2625+1012=3637

        assertFalse(future.isDone)
        assertEquals(null, deadReason)

        // 推进 1012ms，第 5 次尝试无应答超时，累计 5 次失败，触发判死
        scheduler.advanceTimeBy(1012L)

        assertTrue(future.isDone)
        assertTrue(future.isCompletedExceptionally)
        assertEquals(UdpProtocol.REASON_REQUEST_TIMEOUT, deadReason)
        assertEquals(0, requestor.getPendingCount())

        val ex = assertFailsWith<ExecutionException> { future.get() }
        assertTrue(ex.cause is UdpFrameException)
        assertEquals(UdpProtocol.REASON_REQUEST_TIMEOUT, (ex.cause as UdpFrameException).reasonCode)
    }

    @Test
    fun `duplicate in-flight requestId is rejected immediately`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val sentFrames = mutableListOf<UdpFrame>()

        val requestor = ReliableRequestor(
            sender = { sentFrames.add(it) },
            clock = scheduler,
            scheduler = scheduler
        )

        val future1 = requestor.send("dup-req", "first".toByteArray(Charsets.UTF_8))
        assertFalse(future1.isDone)

        // 相同 requestId 在途再次提交
        val future2 = requestor.send("dup-req", "second".toByteArray(Charsets.UTF_8))
        assertTrue(future2.isDone)
        assertTrue(future2.isCompletedExceptionally)

        val ex = assertFailsWith<ExecutionException> { future2.get() }
        assertTrue(ex.cause is IllegalArgumentException)
        assertTrue(ex.cause!!.message!!.contains(UdpProtocol.REASON_DUPLICATE_REQUEST_ID))

        // 原始请求不受影响
        assertFalse(future1.isDone)
        assertEquals(1, requestor.getPendingCount())
    }

    @Test
    fun `assert RTO exponential backoff sequence and capping at 3000ms`() {
        val scheduler = FakeClockAndScheduler(1000L)
        val sentFrames = mutableListOf<UdpFrame>()

        val requestor = ReliableRequestor(
            sender = { sentFrames.add(it) },
            clock = scheduler,
            scheduler = scheduler,
            maxAttempts = 10,
            maxRtoMillis = 3000L
        )

        val future = requestor.send("rto-test", "data".toByteArray(Charsets.UTF_8))

        // 逐步触发重传并收集 RTO 序列
        // 初始: 200
        scheduler.advanceTimeBy(200L) // -> 300
        scheduler.advanceTimeBy(300L) // -> 450
        scheduler.advanceTimeBy(450L) // -> 675
        scheduler.advanceTimeBy(675L) // -> 1012
        scheduler.advanceTimeBy(1012L) // -> 1518
        scheduler.advanceTimeBy(1518L) // -> 2277
        scheduler.advanceTimeBy(2277L) // -> 3000 (min(3415, 3000))
        scheduler.advanceTimeBy(3000L) // -> 3000 (capped at 3000)

        val rtos = requestor.getRecordedRtos("rto-test")
        assertEquals(listOf(200L, 300L, 450L, 675L, 1012L, 1518L, 2277L, 3000L, 3000L), rtos)
        assertFalse(future.isDone)
    }
}

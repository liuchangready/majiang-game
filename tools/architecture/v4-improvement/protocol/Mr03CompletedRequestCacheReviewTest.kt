package com.majiang.review

import com.majiang.transport.udp.*
import org.junit.jupiter.api.Test
import kotlin.test.*

/**
 * MR-03 独立验收：ReliableRequestor 完成记录缓存的确定性淘汰契约。
 *
 * 契约（相对基线 8a4b59f 的变更）：
 * 1. 完成记录以 requestId 为主记录，另维护 rspSeq 索引；淘汰主记录时同步清理索引。
 * 2. 淘汰策略为确定性 FIFO（插入顺序），不再依赖 ConcurrentHashMap 哈希顺序。
 * 3. 过期清理（completedRetentionMillis，默认 60s）优先于容量淘汰（completedCapacity，默认 256）。
 * 4. 保留窗口内的重复 RSP（按 requestId 或 rspSeq 命中）继续回 ACK；淘汰/过期后返回 false。
 * 5. 重复命中不重写主记录，不延长保留窗口（保留锚点为首次完成时间）。
 */
class Mr03CompletedRequestCacheReviewTest {

    /** 确定性虚拟时钟 + 虚拟调度器：advance 只执行到期任务，任务可再调度。 */
    private class Time(start: Long = 1000L) : UdpClock, UdpScheduler {
        private data class Job(var at: Long, val task: Runnable, var cancelled: Boolean = false)

        private val jobs = mutableListOf<Job>()
        private var now = start

        override fun currentTimeMillis(): Long = now

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val job = Job(now + delayMillis, task)
            jobs.add(job)
            return Cancellable { job.cancelled = true }
        }

        fun advance(deltaMillis: Long) {
            require(deltaMillis >= 0L) { "deltaMillis must be >= 0: $deltaMillis" }
            val target = now + deltaMillis
            while (true) {
                val due = jobs.filter { !it.cancelled && it.at <= target }.minByOrNull { it.at } ?: break
                now = due.at
                due.cancelled = true
                due.task.run()
            }
            now = target
        }
    }

    private fun newRequestor(
        time: Time,
        capacity: Int = 256,
        retentionMillis: Long = 60_000L,
        sent: MutableList<UdpFrame>,
        acks: MutableList<UdpFrame>
    ): ReliableRequestor = ReliableRequestor(
        sender = { sent.add(it) },
        clock = time,
        scheduler = time,
        connectionIdSupplier = { 101 },
        ackSender = { acks.add(it) },
        completedCapacity = capacity,
        completedRetentionMillis = retentionMillis
    )

    /** 完成一个请求并返回其 RSP 帧（seq 唯一）。 */
    private fun complete(
        requestor: ReliableRequestor,
        requestId: String,
        rspSeq: Int,
        payload: ByteArray = "ok".toByteArray()
    ): UdpFrame {
        val future = requestor.send(requestId, payload)
        val rsp = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 101,
            seq = rspSeq,
            ackSeq = 0,
            payload = payload
        )
        assertTrue(requestor.onResponse(rsp, requestId = requestId), "RSP for $requestId must be handled")
        assertTrue(future.isDone, "future for $requestId must complete")
        return rsp
    }

    @Test
    fun `duplicate RSP within retention is re-ACKed by requestId`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, sent = sent, acks = acks)

        val rsp = complete(requestor, "req-1", rspSeq = 500)
        assertEquals(1, acks.size, "completion must ACK once")

        // 保留窗口内重复 RSP：按 requestId 命中主记录，继续回 ACK
        val dup = rsp.copy(seq = 500)
        assertTrue(requestor.onResponse(dup, requestId = "req-1"), "duplicate RSP within retention must be handled")
        assertEquals(2, acks.size, "duplicate RSP must be re-ACKed")
        assertEquals(500, acks[1].ackSeq)
    }

    @Test
    fun `duplicate RSP within retention is re-ACKed by rspSeq index`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, sent = sent, acks = acks)

        complete(requestor, "req-1", rspSeq = 700)

        // 未携带 requestId 的重复 RSP：按 rspSeq 索引命中，继续回 ACK
        val dupBySeq = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 101,
            seq = 700,
            ackSeq = 0,
            payload = "dup".toByteArray()
        )
        assertTrue(requestor.onResponse(dupBySeq), "seq-based duplicate RSP must be handled")
        assertEquals(2, acks.size, "seq-based duplicate RSP must be re-ACKed")
        assertEquals(700, acks[1].ackSeq)
    }

    @Test
    fun `completed record expires after retention window`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, sent = sent, acks = acks)

        val rsp1 = complete(requestor, "req-1", rspSeq = 501)

        // 越过保留窗口（60s）后插入新完成记录：过期清理优先于一切
        time.advance(60_001L)
        complete(requestor, "req-2", rspSeq = 502)

        assertFalse(
            requestor.onResponse(rsp1, requestId = "req-1"),
            "expired completed record must not be replayed"
        )
        // 两次完成各产生一个 ACK（req-1 与 req-2 的完成确认），过期重放不新增 ACK
        assertEquals(2, acks.size, "expired record must not be re-ACKed")

        // req-2 仍在保留窗口内
        val dup2 = UdpFrame(
            packetType = UdpPacketType.RSP, connectionId = 101, seq = 502, ackSeq = 0,
            payload = "dup".toByteArray()
        )
        assertTrue(requestor.onResponse(dup2, requestId = "req-2"), "retained record must still replay")
        assertEquals(3, acks.size)
    }

    @Test
    fun `257th completion evicts oldest completed record deterministically`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, capacity = 256, retentionMillis = 60_000L, sent = sent, acks = acks)

        val firstRsp = complete(requestor, "req-0001", rspSeq = 10_001)
        for (i in 2..257) {
            complete(requestor, "req-%04d".format(i), rspSeq = 10_000 + i)
        }

        // 时间未推进：无过期条目；第 257 条插入必须按插入顺序淘汰最早条目 req-0001
        assertFalse(
            requestor.onResponse(firstRsp, requestId = "req-0001"),
            "oldest completed record must have been evicted at capacity boundary"
        )
        assertFalse(
            requestor.onResponse(
                UdpFrame(
                    packetType = UdpPacketType.RSP, connectionId = 101, seq = 10_001, ackSeq = 0,
                    payload = "dup".toByteArray()
                )
            ),
            "rspSeq index of the evicted main record must be cleaned together"
        )

        // 第二早与最新的记录仍在
        assertTrue(requestor.onResponse(UdpFrame(
            packetType = UdpPacketType.RSP, connectionId = 101, seq = 10_002, ackSeq = 0,
            payload = "dup2".toByteArray()
        ), requestId = "req-0002"), "second-oldest record must still be retained")
        assertTrue(requestor.onResponse(UdpFrame(
            packetType = UdpPacketType.RSP, connectionId = 101, seq = 10_257, ackSeq = 0,
            payload = "dup3".toByteArray()
        ), requestId = "req-0257"), "newest record must still be retained")
    }

    @Test
    fun `replay hit does not extend retention window`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, capacity = 256, retentionMillis = 60_000L, sent = sent, acks = acks)

        val rsp1 = complete(requestor, "req-1", rspSeq = 601)

        // t0+30s 重复命中：只回 ACK，不重写记录
        time.advance(30_000L)
        assertTrue(requestor.onResponse(rsp1, requestId = "req-1"))
        assertEquals(2, acks.size)

        // 到 t0+61s：保留锚点仍是首次完成时间 t0，必须过期
        time.advance(31_000L)
        complete(requestor, "req-2", rspSeq = 602)
        assertFalse(
            requestor.onResponse(rsp1, requestId = "req-1"),
            "replay hits must not extend the retention window beyond the original completion time"
        )
    }

    @Test
    fun `expiry eviction also cleans rspSeq index`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, sent = sent, acks = acks)

        complete(requestor, "req-1", rspSeq = 801)

        time.advance(60_001L)
        complete(requestor, "req-2", rspSeq = 802)

        // 主记录过期清理后，rspSeq 索引必须同步清理：seq 命中不得再回 ACK
        assertFalse(
            requestor.onResponse(UdpFrame(
                packetType = UdpPacketType.RSP, connectionId = 101, seq = 801, ackSeq = 0,
                payload = "dup".toByteArray()
            )),
            "rspSeq index must be cleaned when the main record expires"
        )
        // 两次完成各一个 ACK，过期 seq 重放不新增
        assertEquals(2, acks.size)
    }

    @Test
    fun `close still fails outstanding futures`() {
        val time = Time()
        val sent = mutableListOf<UdpFrame>()
        val acks = mutableListOf<UdpFrame>()
        val requestor = newRequestor(time, sent = sent, acks = acks)

        val future = requestor.send("req-close", "x".toByteArray())
        requestor.close()
        assertTrue(future.isCompletedExceptionally, "close must fail outstanding futures")
    }
}

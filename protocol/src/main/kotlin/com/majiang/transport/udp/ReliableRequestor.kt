package com.majiang.transport.udp

import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * 可靠请求-响应状态机 (ReliableRequestor)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.4-1、§7.6。
 * - 客户端发出 REQ(seq=n, requestId=r)，启动 RTO 定时器；
 * - 收到服务端 RSP(ackSeq=n) 时取消定时器，自动回发 ACK(ackSeq=rspSeq)，并完成 Future；
 * - 若超时未收到响应，按指数退避 (默认 x1.5，上限 3s) 进行重传；
 * - 累计达到最大尝试次数 (默认 5 次) 仍无应答则判死，触发连接死亡回调并异常完成 Future；
 * - 在途同名 requestId 重复提交时立即拒绝 (对齐 DUPLICATE_REQUEST_ID 契约)；
 * - 所有时间与调度操作完全走注入的 [UdpClock] 与 [UdpScheduler]，零真实网络与睡眠。
 */
class ReliableRequestor(
    private val sender: (UdpFrame) -> Unit,
    private val clock: UdpClock,
    private val scheduler: UdpScheduler,
    private val connectionIdSupplier: () -> Int = { 0 },
    private val seqSupplier: () -> Int = { defaultSeqGenerator.incrementAndGet() },
    private val ackSender: ((UdpFrame) -> Unit)? = null,
    private val onConnectionDead: ((reason: String) -> Unit)? = null,
    private val initialRtoMillis: Long = UdpProtocol.INITIAL_RTO_MILLIS,
    private val rtoBackoffFactor: Double = UdpProtocol.RTO_BACKOFF_FACTOR,
    private val maxRtoMillis: Long = UdpProtocol.MAX_RTO_MILLIS,
    private val maxAttempts: Int = UdpProtocol.MAX_REQUEST_ATTEMPTS
) : AutoCloseable {

    companion object {
        private val defaultSeqGenerator = AtomicInteger(0)
    }

    private class PendingRequest(
        val requestId: String,
        val seq: Int,
        val payload: ByteArray,
        val future: CompletableFuture<UdpFrame>,
        var attemptCount: Int = 1,
        var currentRto: Long = 0L,
        val recordedRtos: MutableList<Long> = mutableListOf(),
        var timerHandle: Cancellable? = null
    )

    private val pendingByRequestId = ConcurrentHashMap<String, PendingRequest>()
    private val pendingBySeq = ConcurrentHashMap<Int, PendingRequest>()
    private val completedRspSeqs = ConcurrentHashMap<Int, Long>()
    private val completedRequestIds = ConcurrentHashMap<String, Int>()
    private val closed = AtomicBoolean(false)

    /**
     * 发送可靠请求并返回响应 Future。
     *
     * @param requestId 幂等请求 ID
     * @param payload 请求二进制载荷
     * @return 收到 RSP 帧后完成的 [CompletableFuture]
     */
    fun send(requestId: String, payload: ByteArray): CompletableFuture<UdpFrame> {
        val future = CompletableFuture<UdpFrame>()
        if (closed.get()) {
            future.completeExceptionally(IOException("ReliableRequestor is closed"))
            return future
        }

        val seq = seqSupplier()
        val pending = PendingRequest(
            requestId = requestId,
            seq = seq,
            payload = payload,
            future = future,
            attemptCount = 1,
            currentRto = initialRtoMillis
        )

        var accepted = true
        pendingByRequestId.compute(requestId) { _, existing ->
            if (existing == null || existing.future.isDone) {
                accepted = true
                pending
            } else {
                accepted = false
                existing
            }
        }

        if (!accepted) {
            future.completeExceptionally(
                IllegalArgumentException("${UdpProtocol.REASON_DUPLICATE_REQUEST_ID}: $requestId is still outstanding")
            )
            return future
        }

        pendingBySeq[seq] = pending
        pending.recordedRtos.add(initialRtoMillis)

        // 发送首包 REQ
        val frame = UdpFrame(
            packetType = UdpPacketType.REQ,
            connectionId = connectionIdSupplier(),
            seq = pending.seq,
            ackSeq = 0,
            payload = payload
        )
        sender(frame)

        // 启动首次重传定时器
        scheduleTimeout(pending)

        return future
    }

    /**
     * 收到对端数据包时的响应处理钩子
     *
     * @param rspFrame 收到的帧 (通常为 RSP 类型)
     * @param requestId 可选的显式请求 ID (若上层已从 JSON 解析出)
     * @return 若匹配在途请求并处理成功返回 true，否则返回 false
     */
    fun onResponse(rspFrame: UdpFrame, requestId: String? = null): Boolean {
        if (rspFrame.packetType != UdpPacketType.RSP) {
            return false
        }

        val pending = if (requestId != null) {
            pendingByRequestId[requestId]
        } else {
            pendingBySeq[rspFrame.ackSeq]
        }

        if (pending == null) {
            val wasCompleted = (requestId != null && completedRequestIds.containsKey(requestId)) ||
                    completedRspSeqs.containsKey(rspFrame.seq)
            if (wasCompleted) {
                val ackFrame = UdpFrame(
                    packetType = UdpPacketType.ACK,
                    connectionId = connectionIdSupplier(),
                    seq = seqSupplier(),
                    ackSeq = rspFrame.seq
                )
                val ackTarget = ackSender ?: sender
                ackTarget(ackFrame)
                return true
            }
            return false
        }

        synchronized(pending) {
            if (pending.future.isDone) return false
            pending.timerHandle?.cancel()
            pendingByRequestId.remove(pending.requestId)
            pendingBySeq.remove(pending.seq)

            if (completedRspSeqs.size >= 256) {
                val it = completedRspSeqs.keys.iterator()
                if (it.hasNext()) completedRspSeqs.remove(it.next())
            }
            if (completedRequestIds.size >= 256) {
                val it = completedRequestIds.keys.iterator()
                if (it.hasNext()) completedRequestIds.remove(it.next())
            }
            completedRspSeqs[rspFrame.seq] = clock.currentTimeMillis()
            completedRequestIds[pending.requestId] = rspFrame.seq

            // 自动向对端回发确认包 ACK(seq = nextSeq, ackSeq = rsp.seq)
            val ackFrame = UdpFrame(
                packetType = UdpPacketType.ACK,
                connectionId = connectionIdSupplier(),
                seq = seqSupplier(),
                ackSeq = rspFrame.seq
            )
            val ackTarget = ackSender ?: sender
            ackTarget(ackFrame)

            pending.future.complete(rspFrame)
            return true
        }
    }

    private fun scheduleTimeout(pending: PendingRequest) {
        synchronized(pending) {
            if (pending.future.isDone || closed.get()) return
            pending.timerHandle = scheduler.schedule(pending.currentRto) {
                handleTimeout(pending)
            }
        }
    }

    private fun handleTimeout(pending: PendingRequest) {
        synchronized(pending) {
            if (pending.future.isDone || closed.get()) return

            if (pending.attemptCount >= maxAttempts) {
                // 累计达到最大次数，判死
                pendingByRequestId.remove(pending.requestId)
                pendingBySeq.remove(pending.seq)
                val ex = UdpFrameException(
                    UdpProtocol.REASON_REQUEST_TIMEOUT,
                    "Request ${pending.requestId} failed after $maxAttempts attempts without response"
                )
                pending.future.completeExceptionally(ex)
                onConnectionDead?.invoke(UdpProtocol.REASON_REQUEST_TIMEOUT)
                return
            }

            // 执行下一次重传
            pending.attemptCount++
            val nextRto = Math.min(maxRtoMillis, (pending.currentRto * rtoBackoffFactor).toLong())
            pending.currentRto = nextRto
            pending.recordedRtos.add(nextRto)

            val frame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = connectionIdSupplier(),
                seq = pending.seq,
                ackSeq = 0,
                payload = pending.payload
            )
            sender(frame)

            scheduleTimeout(pending)
        }
    }

    /**
     * 获取指定在途请求当前记录的 RTO 退避序列 (供单测校验)
     */
    fun getRecordedRtos(requestId: String): List<Long> {
        return pendingByRequestId[requestId]?.let {
            synchronized(it) { it.recordedRtos.toList() }
        } ?: emptyList()
    }

    /**
     * 当前挂起的在途请求数量
     */
    fun getPendingCount(): Int = pendingByRequestId.size

    override fun close() {
        if (closed.compareAndSet(false, true)) {
            val ex = IOException("ReliableRequestor has been closed")
            for ((_, pending) in pendingByRequestId) {
                synchronized(pending) {
                    pending.timerHandle?.cancel()
                    pending.future.completeExceptionally(ex)
                }
            }
            pendingByRequestId.clear()
            pendingBySeq.clear()
        }
    }
}

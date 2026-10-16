package com.majiang.server.tcp

import java.nio.ByteBuffer
import java.nio.channels.SocketChannel
import java.util.ArrayDeque

/**
 * 单连接出站写队列 (TcpOutboundQueue, 契约 1 / 契约 3 / UC-L01 / UC-L10 / UC-C05)
 *
 * 核心设计：
 * 1. 结构与缓冲：持有当前正在写出、保持 position 直至写完的 [currentBuffer]，以及待写队列；
 * 2. 双队列机制：
 *    - 业务队列 [normalQueue]：最多 256 帧或 4 MiB，承载 RSP 与 UPDATE 推送；
 *    - 控制队列 [controlQueue]：最多 8 帧或 8 KiB，承载 PING/PONG/CLOSE 等控制帧；
 * 3. 控制帧完整边界优先 (UC-L10)：控制帧仅在上一完整帧完全写出后才被优先取出写入，绝不打断正在写出的半帧；
 *    已排队的 RSP 与 UPDATE 相对业务顺序严格保持不变；
 * 4. 慢端检测与过载保护 (UC-L01 / UC-C05)：
 *    - 业务队列满时拒绝新帧入队，连接标记为 SLOW_CONSUMER；已排队回执继续保留尽量写出；
 *    - 当队列饱和到连 8 帧/8 KiB 控制队列都无法入队时，直接返回 false 指示连接层立即断开。
 */
class TcpOutboundQueue(
    val globalBudget: TcpGlobalBudget,
    val maxNormalFrames: Int = 256,
    val maxNormalBytes: Long = 4L * 1024 * 1024,
    val maxControlFrames: Int = 8,
    val maxControlBytes: Long = 8L * 1024
) {
    private val queueLock = Any()

    private val normalQueue = ArrayDeque<ByteBuffer>()
    private val controlQueue = ArrayDeque<ByteBuffer>()

    @Volatile
    private var currentBuffer: ByteBuffer? = null
    private var currentBufferBytes: Int = 0
    private var currentBufferIsControl: Boolean = false

    private var queuedNormalBytes: Long = 0L
    private var queuedControlBytes: Long = 0L

    /** 队列中等待发出的总帧数 (含当前半帧) */
    val totalQueuedFrames: Int
        get() = synchronized(queueLock) {
            (if (currentBuffer != null) 1 else 0) + normalQueue.size + controlQueue.size
        }

    /** 队列中等待发出的总字节数 (含当前半帧剩余字节) */
    val totalQueuedBytes: Long
        get() = synchronized(queueLock) {
            (currentBuffer?.remaining()?.toLong() ?: 0L) + queuedNormalBytes + queuedControlBytes
        }

    /** 是否有待写出数据 */
    val hasPendingData: Boolean
        get() = synchronized(queueLock) {
            currentBuffer?.hasRemaining() == true || controlQueue.isNotEmpty() || normalQueue.isNotEmpty()
        }

    /**
     * 入队普通业务帧 (RSP / UPDATE)
     *
     * @param frameBytes 编码好的二进制整帧字节
     * @return true 表示成功入队；false 表示队列满或全局预算耗尽 (SLOW_CONSUMER)
     */
    fun enqueueNormal(frameBytes: ByteArray): Boolean {
        require(frameBytes.isNotEmpty()) { "frameBytes cannot be empty" }
        val size = frameBytes.size.toLong()

        synchronized(queueLock) {
            val curNormalFrames = normalQueue.size + (if (currentBuffer != null && !currentBufferIsControl) 1 else 0)
            val curNormalBytes = queuedNormalBytes + (if (currentBuffer != null && !currentBufferIsControl) currentBufferBytes.toLong() else 0L)
            if (curNormalFrames >= maxNormalFrames || curNormalBytes + size > maxNormalBytes) {
                return false
            }
            if (!globalBudget.reserve(size)) {
                return false
            }
            normalQueue.add(ByteBuffer.wrap(frameBytes))
            queuedNormalBytes += size
            return true
        }
    }

    /**
     * 入队控制优先级帧 (PING / PONG / CLOSE)
     *
     * @param frameBytes 编码好的二进制整帧字节
     * @return true 表示成功入队；false 表示控制队列满或预算耗尽 (直接断开)
     */
    fun enqueueControl(frameBytes: ByteArray): Boolean {
        require(frameBytes.isNotEmpty()) { "frameBytes cannot be empty" }
        val size = frameBytes.size.toLong()

        synchronized(queueLock) {
            val curControlFrames = controlQueue.size + (if (currentBuffer != null && currentBufferIsControl) 1 else 0)
            val curControlBytes = queuedControlBytes + (if (currentBuffer != null && currentBufferIsControl) currentBufferBytes.toLong() else 0L)
            if (curControlFrames >= maxControlFrames || curControlBytes + size > maxControlBytes) {
                return false
            }
            if (!globalBudget.reserve(size)) {
                return false
            }
            controlQueue.add(ByteBuffer.wrap(frameBytes))
            queuedControlBytes += size
            return true
        }
    }

    /**
     * 向 SocketChannel 执行单轮有界写出 (契约 1 / UC-L05 公平调度)
     *
     * @param channel 目标 SocketChannel
     * @param maxBytes 单轮最大写出字节数 (默认 64 KiB)
     * @param maxFrames 单轮最大完成帧数 (默认 32)
     * @return 本轮实际写出的字节数
     */
    fun writeToChannel(channel: SocketChannel, maxBytes: Int = 64 * 1024, maxFrames: Int = 32): Int {
        var bytesWrittenTotal = 0
        var framesCompleted = 0

        while (bytesWrittenTotal < maxBytes && framesCompleted < maxFrames) {
            val buf = getOrCreateCurrentBuffer() ?: break

            val maxBytesRemaining = maxBytes - bytesWrittenTotal
            val origLimit = buf.limit()
            if (buf.remaining() > maxBytesRemaining) {
                buf.limit(buf.position() + maxBytesRemaining)
            }

            val written = try {
                channel.write(buf)
            } finally {
                if (buf.hasRemaining() && buf.limit() != origLimit) {
                    buf.limit(origLimit)
                }
            }

            if (written > 0) {
                bytesWrittenTotal += written
            }

            if (!buf.hasRemaining()) {
                // 当前帧写出完毕：归还已写完帧的全局预算与本地记账
                completeCurrentBuffer()
                framesCompleted++
            }

            if (written == 0) {
                // TCP 发送缓冲区已满，让出
                break
            }
        }

        return bytesWrittenTotal
    }

    /**
     * 获取或从就绪队列中拉取下一帧作为 [currentBuffer]
     *
     * 优先级逻辑 (UC-L10)：
     * - 若当前缓冲未写完，继续使用当前缓冲（绝不打断半帧！）；
     * - 当前缓冲已写完时，优先拉取 [controlQueue]，其次拉取 [normalQueue]。
     */
    private fun getOrCreateCurrentBuffer(): ByteBuffer? {
        synchronized(queueLock) {
            val cur = currentBuffer
            if (cur != null && cur.hasRemaining()) {
                return cur
            }

            // 优先检查控制帧
            val nextControl = controlQueue.poll()
            if (nextControl != null) {
                currentBuffer = nextControl
                currentBufferBytes = nextControl.remaining()
                currentBufferIsControl = true
                queuedControlBytes -= currentBufferBytes
                return nextControl
            }

            // 其次取出业务帧
            val nextNormal = normalQueue.poll()
            if (nextNormal != null) {
                currentBuffer = nextNormal
                currentBufferBytes = nextNormal.remaining()
                currentBufferIsControl = false
                queuedNormalBytes -= currentBufferBytes
                return nextNormal
            }

            currentBuffer = null
            currentBufferBytes = 0
            currentBufferIsControl = false
            return null
        }
    }

    /**
     * 标记当前缓冲写出完毕并归还全局预算
     */
    private fun completeCurrentBuffer() {
        synchronized(queueLock) {
            val freedBytes = currentBufferBytes.toLong()
            currentBuffer = null
            currentBufferBytes = 0
            currentBufferIsControl = false
            if (freedBytes > 0L) {
                globalBudget.release(freedBytes)
            }
        }
    }

    /**
     * 释放队列中所有待发送的缓冲区并归还全局预算
     */
    fun clear() {
        synchronized(queueLock) {
            var releasedBytes = 0L
            if (currentBuffer != null) {
                releasedBytes += currentBufferBytes
                currentBuffer = null
                currentBufferBytes = 0
                currentBufferIsControl = false
            }
            while (controlQueue.isNotEmpty()) {
                releasedBytes += controlQueue.poll().remaining()
            }
            while (normalQueue.isNotEmpty()) {
                releasedBytes += normalQueue.poll().remaining()
            }
            queuedControlBytes = 0L
            queuedNormalBytes = 0L
            if (releasedBytes > 0L) {
                globalBudget.release(releasedBytes)
            }
        }
    }
}

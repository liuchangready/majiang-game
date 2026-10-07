package com.majiang.transport.udp

import java.net.SocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * 分片子包解析数据结构
 */
data class FragmentChunk(
    val msgId: Int,
    val fragIndex: Int,
    val fragCount: Int,
    val originalPacketType: UdpPacketType,
    val chunk: ByteArray
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false

        other as FragmentChunk

        if (msgId != other.msgId) return false
        if (fragIndex != other.fragIndex) return false
        if (fragCount != other.fragCount) return false
        if (originalPacketType != other.originalPacketType) return false
        if (!chunk.contentEquals(other.chunk)) return false

        return true
    }

    override fun hashCode(): Int {
        var result = msgId
        result = 31 * result + fragIndex
        result = 31 * result + fragCount
        result = 31 * result + originalPacketType.hashCode()
        result = 31 * result + chunk.contentHashCode()
        return result
    }
}

/**
 * UDP 数据报分片与重组组件 (Fragmenter)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.4-2 分片。
 * - payload > 1090 字节（阈值）拆分成多个 FRAG 包；
 * - 每个分片携带子头：msgId (4B) + fragIndex (2B) + fragCount (2B) + originalPacketTypeId (1B) = 9 字节；
 * - 接收端按 msgId 重组；
 * - 3 秒未齐整条丢弃并上报回调；
 * - 重组缓冲有界：同时最多 8 条消息在途，超额丢弃新分片；
 * - 纯逻辑组件，时间与调度全部走注入的 [UdpClock] 和 [UdpScheduler]。
 */
class Fragmenter(
    private val clock: UdpClock,
    private val scheduler: UdpScheduler,
    private val thresholdBytes: Int = UdpProtocol.FRAGMENT_THRESHOLD_BYTES,
    private val timeoutMillis: Long = UdpProtocol.FRAGMENT_TIMEOUT_MILLIS,
    private val maxConcurrent: Int = UdpProtocol.MAX_CONCURRENT_FRAGMENTS,
    private val onTimeout: ((msgId: Int) -> Unit)? = null
) : AutoCloseable {

    companion object {
        const val SUBHEADER_SIZE: Int = 9
        const val MAX_FRAG_COUNT: Int = 256
        const val MAX_MESSAGE_BYTES: Int = 256 * 1090
        private val nextMsgIdGenerator = AtomicInteger(0)

        /**
         * 编码分片子头部与数据块
         */
        fun encodeSubheader(
            msgId: Int,
            fragIndex: Int,
            fragCount: Int,
            originalType: UdpPacketType,
            chunk: ByteArray
        ): ByteArray {
            val buf = ByteArray(SUBHEADER_SIZE + chunk.size)
            // 0..3: msgId
            buf[0] = (msgId ushr 24).toByte()
            buf[1] = (msgId ushr 16).toByte()
            buf[2] = (msgId ushr 8).toByte()
            buf[3] = msgId.toByte()

            // 4..5: fragIndex
            buf[4] = (fragIndex ushr 8).toByte()
            buf[5] = fragIndex.toByte()

            // 6..7: fragCount
            buf[6] = (fragCount ushr 8).toByte()
            buf[7] = fragCount.toByte()

            // 8: originalTypeId
            buf[8] = originalType.typeId.toByte()

            // 9..: chunk
            if (chunk.isNotEmpty()) {
                System.arraycopy(chunk, 0, buf, SUBHEADER_SIZE, chunk.size)
            }
            return buf
        }

        /**
         * 解码分片载荷中的子头部与数据块
         */
        fun decodeSubheader(payload: ByteArray): FragmentChunk {
            require(payload.size >= SUBHEADER_SIZE) {
                "Fragment payload size ${payload.size} is less than subheader size $SUBHEADER_SIZE"
            }

            val msgId = ((payload[0].toInt() and 0xFF) shl 24) or
                ((payload[1].toInt() and 0xFF) shl 16) or
                ((payload[2].toInt() and 0xFF) shl 8) or
                (payload[3].toInt() and 0xFF)

            val fragIndex = ((payload[4].toInt() and 0xFF) shl 8) or (payload[5].toInt() and 0xFF)
            val fragCount = ((payload[6].toInt() and 0xFF) shl 8) or (payload[7].toInt() and 0xFF)

            val typeId = payload[8].toInt() and 0xFF
            val originalType = UdpPacketType.fromTypeId(typeId)
                ?: throw IllegalArgumentException("Unknown original packet type ID: $typeId in fragment subheader")

            val chunkSize = payload.size - SUBHEADER_SIZE
            val chunk = ByteArray(chunkSize)
            if (chunkSize > 0) {
                System.arraycopy(payload, SUBHEADER_SIZE, chunk, 0, chunkSize)
            }

            return FragmentChunk(
                msgId = msgId,
                fragIndex = fragIndex,
                fragCount = fragCount,
                originalPacketType = originalType,
                chunk = chunk
            )
        }
    }

    private data class InFlightKey(
        val connectionId: Int,
        val msgId: Int,
        val remoteAddress: SocketAddress? = null
    )

    private class InFlightMessage(
        val msgId: Int,
        val fragCount: Int,
        val originalType: UdpPacketType,
        val connectionId: Int,
        val ackSeq: Int,
        val flagZlib: Boolean,
        val flagEncrypted: Boolean,
        val version: Int,
        val chunks: Array<ByteArray?>,
        var receivedCount: Int = 0,
        var timerHandle: Cancellable? = null
    )

    private val inFlight = ConcurrentHashMap<InFlightKey, InFlightMessage>()

    /**
     * 将待发帧按需拆分为一个或多个数据包。
     *
     * @param frame 原始完整数据帧
     * @param seqSupplier 用于为生成的各分片包分配独立 seq 的生成器
     * @return 若载荷不超过阈值返回只包含原始帧的单元素列表；若超额则返回拆解后的 FRAG 包列表
     */
    fun fragment(
        frame: UdpFrame,
        seqSupplier: () -> Int = { frame.seq }
    ): List<UdpFrame> {
        val totalBytes = frame.payload.size
        if (totalBytes <= thresholdBytes) {
            return listOf(frame)
        }

        val msgId = nextMsgIdGenerator.incrementAndGet()
        val fragCount = (totalBytes + thresholdBytes - 1) / thresholdBytes
        val result = ArrayList<UdpFrame>(fragCount)

        var offset = 0
        var fragIndex = 0
        while (offset < totalBytes) {
            val chunkSize = Math.min(thresholdBytes, totalBytes - offset)
            val chunk = ByteArray(chunkSize)
            System.arraycopy(frame.payload, offset, chunk, 0, chunkSize)

            val fragPayload = encodeSubheader(
                msgId = msgId,
                fragIndex = fragIndex,
                fragCount = fragCount,
                originalType = frame.packetType,
                chunk = chunk
            )

            result.add(
                UdpFrame(
                    packetType = UdpPacketType.FRAG,
                    connectionId = frame.connectionId,
                    seq = seqSupplier(),
                    ackSeq = frame.ackSeq,
                    payload = fragPayload,
                    flagZlib = frame.flagZlib,
                    flagEncrypted = frame.flagEncrypted,
                    version = frame.version
                )
            )

            offset += chunkSize
            fragIndex++
        }

        return result
    }

    /**
     * 接收端处理入站帧并执行重组。
     *
     * @param frame 入站数据帧
     * @param remoteAddress 入站来源地址（connectionId=0 时用于多客户端隔离）
     * @return 若是普通非分片包直接原样返回；若是分片包且集齐则返回重组还原后的完整帧；若未集齐或被丢弃则返回 null
     */
    fun reassemble(frame: UdpFrame, remoteAddress: SocketAddress? = null): UdpFrame? {
        if (frame.packetType != UdpPacketType.FRAG) {
            return frame
        }

        val chunk = try {
            decodeSubheader(frame.payload)
        } catch (_: Exception) {
            return null
        }

        if (chunk.fragCount <= 0 || chunk.fragCount > MAX_FRAG_COUNT) {
            return null
        }
        if (chunk.fragIndex < 0 || chunk.fragIndex >= chunk.fragCount) {
            return null
        }

        val key = if (frame.connectionId != 0) {
            InFlightKey(connectionId = frame.connectionId, msgId = chunk.msgId, remoteAddress = null)
        } else {
            InFlightKey(connectionId = 0, msgId = chunk.msgId, remoteAddress = remoteAddress)
        }

        synchronized(inFlight) {
            var msg = inFlight[key]
            if (msg == null) {
                if (inFlight.size >= maxConcurrent) {
                    return null
                }
                val estimatedBytes = chunk.fragCount.toLong() * thresholdBytes
                if (estimatedBytes > MAX_MESSAGE_BYTES) {
                    return null
                }

                msg = InFlightMessage(
                    msgId = chunk.msgId,
                    fragCount = chunk.fragCount,
                    originalType = chunk.originalPacketType,
                    connectionId = frame.connectionId,
                    ackSeq = frame.ackSeq,
                    flagZlib = frame.flagZlib,
                    flagEncrypted = frame.flagEncrypted,
                    version = frame.version,
                    chunks = arrayOfNulls(chunk.fragCount)
                )

                msg.timerHandle = scheduler.schedule(timeoutMillis) {
                    handleTimeout(key)
                }
                inFlight[key] = msg
            } else {
                if (msg.fragCount != chunk.fragCount ||
                    msg.originalType != chunk.originalPacketType ||
                    msg.connectionId != frame.connectionId ||
                    msg.ackSeq != frame.ackSeq ||
                    msg.version != frame.version
                ) {
                    return null
                }
            }

            if (chunk.fragIndex in 0 until msg.fragCount && msg.chunks[chunk.fragIndex] == null) {
                msg.chunks[chunk.fragIndex] = chunk.chunk
                msg.receivedCount++
            }

            if (msg.receivedCount == msg.fragCount) {
                msg.timerHandle?.cancel()
                inFlight.remove(key)

                val totalLength = msg.chunks.sumOf { it?.size ?: 0 }
                if (totalLength > MAX_MESSAGE_BYTES) {
                    return null
                }
                val fullPayload = ByteArray(totalLength)
                var destPos = 0
                for (c in msg.chunks) {
                    if (c != null) {
                        System.arraycopy(c, 0, fullPayload, destPos, c.size)
                        destPos += c.size
                    }
                }

                return UdpFrame(
                    packetType = msg.originalType,
                    connectionId = msg.connectionId,
                    seq = frame.seq,
                    ackSeq = msg.ackSeq,
                    payload = fullPayload,
                    flagZlib = msg.flagZlib,
                    flagEncrypted = msg.flagEncrypted,
                    version = msg.version
                )
            }

            return null
        }
    }

    private fun handleTimeout(key: InFlightKey) {
        val removed = synchronized(inFlight) {
            inFlight.remove(key)
        }
        if (removed != null) {
            onTimeout?.invoke(removed.msgId)
        }
    }

    /**
     * 当前在途重组的消息数量
     */
    fun getInFlightCount(): Int = inFlight.size

    override fun close() {
        synchronized(inFlight) {
            for ((_, msg) in inFlight) {
                msg.timerHandle?.cancel()
            }
            inFlight.clear()
        }
    }
}

package com.majiang.transport.udp

import java.util.ArrayDeque
import java.util.concurrent.ConcurrentHashMap

/**
 * 推送重传包记录项
 */
data class PushRecord(
    val cursor: Long,
    val fromCursor: Long,
    val payload: ByteArray
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false

        other as PushRecord

        if (cursor != other.cursor) return false
        if (fromCursor != other.fromCursor) return false
        if (!payload.contentEquals(other.payload)) return false

        return true
    }

    override fun hashCode(): Int {
        var result = cursor.hashCode()
        result = 31 * result + fromCursor.hashCode()
        result = 31 * result + payload.contentHashCode()
        return result
    }
}

/**
 * RESEND 请求的响应裁决结果
 */
sealed class ResendResponse {
    /** 缺口完全在窗口内，提供按序补发的已发包列表 */
    data class Retransmit(val records: List<PushRecord>) : ResendResponse()

    /** 缺口超出窗口（已被淘汰），需发送全量快照自愈 */
    data class FullSnapshot(val payload: ByteArray) : ResendResponse()

    /** 目标连接不存在 */
    object ConnectionNotFound : ResendResponse()
}

/**
 * 服务端推送重传窗口 (PushRetransmitWindow)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.4-3、§7.6。
 * - 按 connectionId 独立维护最近已发推送包；
 * - 使用定长 [ArrayDeque] 环形窗口，内存严格有界（默认每连接 256 条，超额自动淘汰最老包）；
 * - 响应 RESEND(fromCursor)：
 *   - 缺口在窗口内：按序提取从 fromCursor 开始的所有推送包重发；
 *   - 缺口在窗口外（最老包已被淘汰或窗口为空）：直接产出全量快照 PUSH 自愈；
 *   - 窗口外缺口自愈丢弃中间过程动画，但不丢最终权威对局状态。
 */
class PushRetransmitWindow(
    private val maxCapacityPerConnection: Int = UdpProtocol.RETRANSMIT_WINDOW_SIZE
) {

    private class ConnectionWindow(val capacity: Int) {
        val deque = ArrayDeque<PushRecord>(capacity)

        @Synchronized
        fun add(record: PushRecord) {
            if (deque.size >= capacity) {
                deque.removeFirst() // 淘汰最老包
            }
            deque.addLast(record)
        }

        @Synchronized
        fun getOldestCursor(): Long? = deque.firstOrNull()?.cursor

        @Synchronized
        fun getNewestCursor(): Long? = deque.lastOrNull()?.cursor

        @Synchronized
        fun findFrom(fromCursor: Long): List<PushRecord>? {
            if (deque.isEmpty()) return null
            val oldest = deque.first()
            // 若客户端索要的起始游标早于当前保留的最老记录游标，说明中间帧已被淘汰
            if (fromCursor < oldest.cursor && fromCursor < oldest.fromCursor + 1) {
                return null
            }
            // 提取从该游标起的所有补发包 (按序)
            return deque.filter { it.cursor >= fromCursor }
        }

        @Synchronized
        fun size(): Int = deque.size
    }

    private val windows = ConcurrentHashMap<Int, ConnectionWindow>()

    /**
     * 记录一条新下发的推送包
     */
    fun record(connectionId: Int, cursor: Long, fromCursor: Long, payload: ByteArray) {
        val window = windows.computeIfAbsent(connectionId) {
            ConnectionWindow(maxCapacityPerConnection)
        }
        window.add(PushRecord(cursor = cursor, fromCursor = fromCursor, payload = payload))
    }

    /**
     * 处理客户端发来的 RESEND 请求
     *
     * @param connectionId 目标连接 ID
     * @param requestedFromCursor 客户端请求补发的起始游标
     * @param fullSnapshotSupplier 当缺口在窗口外时生成全量快照的函数
     */
    fun handleResend(
        connectionId: Int,
        requestedFromCursor: Long,
        fullSnapshotSupplier: () -> ByteArray
    ): ResendResponse {
        val window = windows[connectionId] ?: return ResendResponse.FullSnapshot(fullSnapshotSupplier())

        val retransmitList = window.findFrom(requestedFromCursor)
        return if (retransmitList != null) {
            ResendResponse.Retransmit(retransmitList)
        } else {
            // 缺口在窗口外或已淘汰，回退为全量快照
            ResendResponse.FullSnapshot(fullSnapshotSupplier())
        }
    }

    /**
     * 获取指定连接当前已缓冲的包数量
     */
    fun getWindowSize(connectionId: Int): Int {
        return windows[connectionId]?.size() ?: 0
    }

    /**
     * 移除已断开连接的窗口数据，防止内存泄露
     */
    fun removeConnection(connectionId: Int) {
        windows.remove(connectionId)
    }

    /**
     * 清理所有窗口
     */
    fun clear() {
        windows.clear()
    }
}

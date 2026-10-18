package com.majiang.server.tcp

import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.tcp.TcpMessageCodec
import java.util.LinkedHashMap

/**
 * 公开流更新帧编码缓存 (PublicUpdateFrameCache, MR-07b 广播编码复用)
 *
 * 核心设计契约：
 * 1. 只缓存公开流帧 (ProtocolPublicUpdate)；席位流、RSP、RoomInfo 绝不经过本缓存；
 * 2. 完整快照基线不含历史事件；全量快照与连续消息拥有不同键，绝不串缓存；
 * 3. 编码恰好一次：命中返回同一不可变 ByteArray（零重复编码）；未命中在锁内编码一次并计入容量；
 * 4. 字节数组编码后不可变，各连接通过独立 ByteBuffer 视图引用，并发写入安全；
 * 5. 不保留 seq、ACK 或重传索引（TCP 无应用层重传语义）；
 * 6. 双上限 FIFO 淘汰：条目数超限或字节总数超限时依最旧条目淘汰，已返回的字节数组引用不受影响；
 * 7. 按会话释放：房间关闭或回收时显式调用 releaseSession 清理该会话条目。
 *
 * @param maxEntries 缓存最大条目数（拟定默认值 64，可通过服务端配置调整）
 * @param maxBytes 缓存最大字节数（拟定默认值 1 MiB，可通过服务端配置调整）
 */
class PublicUpdateFrameCache(
    val maxEntries: Int = 64,
    val maxBytes: Long = 1024L * 1024L
) {
    init {
        require(maxEntries > 0) { "maxEntries must be > 0: $maxEntries" }
        require(maxBytes > 0L) { "maxBytes must be > 0: $maxBytes" }
    }

    /**
     * 公开更新帧五元组缓存键
     *
     * 严格对应 ProtocolPublicUpdate 与 ProtocolPublicSnapshot 真实协议字段：
     * sessionId、syncEpoch、cursor、fromCursor、isFullSnapshot。
     */
    data class Key(
        val sessionId: String,
        val syncEpoch: String,
        val cursor: Long,
        val fromCursor: Long,
        val isFullSnapshot: Boolean
    ) {
        companion object {
            fun of(update: ProtocolPublicUpdate): Key = Key(
                sessionId = update.snapshot.sessionId,
                syncEpoch = update.snapshot.syncEpoch,
                cursor = update.snapshot.cursor,
                fromCursor = update.fromCursor,
                isFullSnapshot = update.isFullSnapshot
            )
        }
    }

    private val lock = Any()
    private val cacheEntries = LinkedHashMap<Key, ByteArray>(16, 0.75f, false)

    private var _cachedBytes: Long = 0L
    private var _encodeCount: Long = 0L
    private var _hitCount: Long = 0L
    private var _missCount: Long = 0L

    val encodeCount: Long get() = synchronized(lock) { _encodeCount }
    val entryCount: Int get() = synchronized(lock) { cacheEntries.size }
    val cachedBytes: Long get() = synchronized(lock) { _cachedBytes }
    val hitCount: Long get() = synchronized(lock) { _hitCount }
    val missCount: Long get() = synchronized(lock) { _missCount }

    /**
     * 获取指定公开流更新的二进制帧字节
     *
     * 锁内查键：命中返回同一不可变 ByteArray；未命中则恰好编码一次并计入容量，
     * 执行 FIFO 淘汰保证条目与字节双上限，随后返回。
     */
    fun frameBytesFor(update: ProtocolPublicUpdate): ByteArray = synchronized(lock) {
        val key = Key.of(update)
        val existing = cacheEntries[key]
        if (existing != null) {
            _hitCount++
            return existing
        }

        _missCount++
        val frame = TcpMessageCodec.toPublicUpdateFrame(update)
        val bytes = frame.toByteArray()
        _encodeCount++

        val entryBytes = bytes.size.toLong()
        cacheEntries[key] = bytes
        _cachedBytes += entryBytes

        evictWhileExceedingLimit()

        bytes
    }

    /**
     * 释放指定会话对应的全部缓存条目（房间关闭/回收时调用）
     */
    fun releaseSession(sessionId: String) = synchronized(lock) {
        val iterator = cacheEntries.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next()
            if (entry.key.sessionId == sessionId) {
                iterator.remove()
                _cachedBytes -= entry.value.size.toLong()
            }
        }
    }

    /**
     * 清空全部缓存条目并归零字节（服务停机时调用）
     */
    fun clear() = synchronized(lock) {
        cacheEntries.clear()
        _cachedBytes = 0L
    }

    private fun evictWhileExceedingLimit() {
        val iterator = cacheEntries.iterator()
        while (iterator.hasNext() && (cacheEntries.size > maxEntries || _cachedBytes > maxBytes)) {
            val oldest = iterator.next()
            iterator.remove()
            _cachedBytes -= oldest.value.size.toLong()
        }
    }
}

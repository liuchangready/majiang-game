package com.majiang.server.tcp

import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/**
 * 公开流更新帧编码缓存单元测试 (PublicUpdateFrameCacheTest, R07b-T1..T5)
 */
@Timeout(10)
class PublicUpdateFrameCacheTest {

    private fun createPublicSnapshot(
        sessionId: String = "session-test-01",
        syncEpoch: String = "epoch-test-01",
        cursor: Long = 1L,
        roundId: String? = "round-test-01",
        lifecycle: String = "ROUND_ACTIVE"
    ): ProtocolPublicSnapshot {
        val sessionSummary = ProtocolPublicSessionSummary(
            lifecycle = lifecycle,
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 0,
            dealerSeat = 0,
            honba = 0
        )
        return ProtocolPublicSnapshot(
            sessionId = sessionId,
            sessionVersion = 1L,
            roundId = roundId,
            roundVersion = 1L,
            syncEpoch = syncEpoch,
            cursor = cursor,
            board = null,
            session = sessionSummary
        )
    }

    private fun createPublicUpdate(
        sessionId: String = "session-test-01",
        syncEpoch: String = "epoch-test-01",
        cursor: Long = 1L,
        fromCursor: Long = 0L,
        isFullSnapshot: Boolean = false,
        visibleEvents: List<ProtocolGameEvent> = emptyList()
    ): ProtocolPublicUpdate {
        val snapshot = createPublicSnapshot(
            sessionId = sessionId,
            syncEpoch = syncEpoch,
            cursor = cursor
        )
        return ProtocolPublicUpdate(
            snapshot = snapshot,
            fromCursor = fromCursor,
            visibleEvents = visibleEvents,
            isFullSnapshot = isFullSnapshot
        )
    }

    @Test
    fun `R07b-T1 and T2 - same key queried 32 times encodes exactly once and returns identical byte array instance`() {
        val cache = PublicUpdateFrameCache(maxEntries = 64, maxBytes = 1024L * 1024L)
        val update = createPublicUpdate(cursor = 1L, fromCursor = 0L, isFullSnapshot = false)

        val firstBytes = cache.frameBytesFor(update)
        assertTrue(firstBytes.isNotEmpty())
        assertEquals(1L, cache.encodeCount)
        assertEquals(1L, cache.missCount)
        assertEquals(0L, cache.hitCount)
        assertEquals(1, cache.entryCount)
        assertEquals(firstBytes.size.toLong(), cache.cachedBytes)

        // 模拟 32 观战者（其余 31 次查询）
        for (i in 2..32) {
            val subsequentBytes = cache.frameBytesFor(update)
            assertSame(firstBytes, subsequentBytes)
            assertArrayEquals(firstBytes, subsequentBytes)
        }

        assertEquals(1L, cache.encodeCount)
        assertEquals(1L, cache.missCount)
        assertEquals(31L, cache.hitCount)
        assertEquals(1, cache.entryCount)
        assertEquals(firstBytes.size.toLong(), cache.cachedBytes)
    }

    @Test
    fun `R07b-T3 - distinct quintuple attributes produce distinct keys and never collide`() {
        val cache = PublicUpdateFrameCache(maxEntries = 64, maxBytes = 1024L * 1024L)

        val uBase = createPublicUpdate(sessionId = "s1", syncEpoch = "e1", cursor = 1L, fromCursor = 0L, isFullSnapshot = false)
        val uDiffSession = createPublicUpdate(sessionId = "s2", syncEpoch = "e1", cursor = 1L, fromCursor = 0L, isFullSnapshot = false)
        val uDiffEpoch = createPublicUpdate(sessionId = "s1", syncEpoch = "e2", cursor = 1L, fromCursor = 0L, isFullSnapshot = false)
        val uDiffCursor = createPublicUpdate(sessionId = "s1", syncEpoch = "e1", cursor = 2L, fromCursor = 1L, isFullSnapshot = false)
        val uFullSnapshot = createPublicUpdate(sessionId = "s1", syncEpoch = "e1", cursor = 1L, fromCursor = 1L, isFullSnapshot = true)

        val bBase = cache.frameBytesFor(uBase)
        val bDiffSession = cache.frameBytesFor(uDiffSession)
        val bDiffEpoch = cache.frameBytesFor(uDiffEpoch)
        val bDiffCursor = cache.frameBytesFor(uDiffCursor)
        val bFullSnapshot = cache.frameBytesFor(uFullSnapshot)

        assertEquals(5L, cache.encodeCount)
        assertEquals(5L, cache.missCount)
        assertEquals(0L, cache.hitCount)
        assertEquals(5, cache.entryCount)

        // 验证全量快照与连续消息不串缓存
        val kBase = PublicUpdateFrameCache.Key.of(uBase)
        val kFull = PublicUpdateFrameCache.Key.of(uFullSnapshot)
        assertTrue(kBase != kFull)
        assertEquals(false, kBase.isFullSnapshot)
        assertEquals(true, kFull.isFullSnapshot)
        assertEquals(0L, kBase.fromCursor)
        assertEquals(1L, kFull.fromCursor)
    }

    @Test
    fun `R07b-T4 - FIFO eviction by maxEntries keeps entry count at limit and preserves previously returned byte arrays`() {
        val cache = PublicUpdateFrameCache(maxEntries = 2, maxBytes = 10L * 1024L * 1024L)

        val u1 = createPublicUpdate(cursor = 1L, fromCursor = 0L)
        val u2 = createPublicUpdate(cursor = 2L, fromCursor = 1L)
        val u3 = createPublicUpdate(cursor = 3L, fromCursor = 2L)

        val b1 = cache.frameBytesFor(u1)
        val b1Original = b1.clone()
        val b2 = cache.frameBytesFor(u2)
        assertEquals(2, cache.entryCount)

        val b3 = cache.frameBytesFor(u3)
        assertEquals(2, cache.entryCount)

        // 验证最旧的 u1 已被淘汰，重新请求会触发未命中重新编码
        val encodeBefore = cache.encodeCount
        val b1ReEncoded = cache.frameBytesFor(u1)
        assertEquals(encodeBefore + 1L, cache.encodeCount)

        // 验证此前返回给调用方的字节数组不受淘汰影响，内容严格不变
        assertArrayEquals(b1Original, b1)
    }

    @Test
    fun `R07b-T4 - FIFO eviction by maxBytes evicts oldest entries when byte capacity is exceeded`() {
        val probeCache = PublicUpdateFrameCache(maxEntries = 10, maxBytes = 1024L * 1024L)
        val sampleBytes = probeCache.frameBytesFor(createPublicUpdate(cursor = 1L, fromCursor = 0L))
        val singleFrameBytes = sampleBytes.size.toLong()

        // 容量仅容纳 2 个完整帧，第 3 个帧到达时将淘汰最旧帧
        val cache = PublicUpdateFrameCache(maxEntries = 10, maxBytes = singleFrameBytes * 2 + 10L)

        val u1 = createPublicUpdate(cursor = 1L, fromCursor = 0L)
        val u2 = createPublicUpdate(cursor = 2L, fromCursor = 1L)
        val u3 = createPublicUpdate(cursor = 3L, fromCursor = 2L)

        cache.frameBytesFor(u1)
        assertEquals(1, cache.entryCount)
        assertEquals(singleFrameBytes, cache.cachedBytes)

        cache.frameBytesFor(u2)
        assertEquals(2, cache.entryCount)
        assertEquals(singleFrameBytes * 2, cache.cachedBytes)

        cache.frameBytesFor(u3)
        assertEquals(2, cache.entryCount)
        assertTrue(cache.cachedBytes <= cache.maxBytes)
    }

    @Test
    fun `R07b-T5 - releaseSession removes all entries for specified sessionId and cachedBytes drops accordingly`() {
        val cache = PublicUpdateFrameCache(maxEntries = 10, maxBytes = 1024L * 1024L)

        val uA1 = createPublicUpdate(sessionId = "session-A", cursor = 1L, fromCursor = 0L)
        val uA2 = createPublicUpdate(sessionId = "session-A", cursor = 2L, fromCursor = 1L)
        val uB1 = createPublicUpdate(sessionId = "session-B", cursor = 1L, fromCursor = 0L)

        val bA1 = cache.frameBytesFor(uA1)
        val bA2 = cache.frameBytesFor(uA2)
        val bB1 = cache.frameBytesFor(uB1)

        assertEquals(3, cache.entryCount)
        assertEquals((bA1.size + bA2.size + bB1.size).toLong(), cache.cachedBytes)

        cache.releaseSession("session-A")
        assertEquals(1, cache.entryCount)
        assertEquals(bB1.size.toLong(), cache.cachedBytes)

        cache.releaseSession("session-B")
        assertEquals(0, cache.entryCount)
        assertEquals(0L, cache.cachedBytes)
    }

    @Test
    fun `clear resets all entries and cached bytes to zero`() {
        val cache = PublicUpdateFrameCache(maxEntries = 10, maxBytes = 1024L * 1024L)
        cache.frameBytesFor(createPublicUpdate(cursor = 1L, fromCursor = 0L))
        cache.frameBytesFor(createPublicUpdate(cursor = 2L, fromCursor = 1L))
        assertTrue(cache.entryCount > 0)
        assertTrue(cache.cachedBytes > 0L)

        cache.clear()
        assertEquals(0, cache.entryCount)
        assertEquals(0L, cache.cachedBytes)
    }

    @Test
    fun `init validation rejects non-positive bounds`() {
        assertThrows(IllegalArgumentException::class.java) {
            PublicUpdateFrameCache(maxEntries = 0)
        }
        assertThrows(IllegalArgumentException::class.java) {
            PublicUpdateFrameCache(maxEntries = 10, maxBytes = 0L)
        }
    }
}

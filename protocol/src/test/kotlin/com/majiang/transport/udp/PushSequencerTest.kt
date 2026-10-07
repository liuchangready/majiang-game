package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class PushSequencerTest {

    @Test
    fun `consecutive updates applied in order`() {
        val appliedCursors = mutableListOf<Long>()
        val sequencer = PushSequencer(
            initialCursor = 0L,
            onApplied = { appliedCursors.add(it) }
        )

        assertEquals(0L, sequencer.getCurrentCursor())

        val res1 = sequencer.offer(cursor = 1L, fromCursor = 0L)
        assertTrue(res1 is PushOfferResult.Applied)
        assertEquals(1L, (res1 as PushOfferResult.Applied).cursor)
        assertEquals(1L, sequencer.getCurrentCursor())

        val res2 = sequencer.offer(cursor = 2L, fromCursor = 1L)
        assertTrue(res2 is PushOfferResult.Applied)
        assertEquals(2L, (res2 as PushOfferResult.Applied).cursor)
        assertEquals(2L, sequencer.getCurrentCursor())

        val res3 = sequencer.offer(cursor = 3L, fromCursor = 2L)
        assertTrue(res3 is PushOfferResult.Applied)
        assertEquals(3L, (res3 as PushOfferResult.Applied).cursor)
        assertEquals(3L, sequencer.getCurrentCursor())

        assertEquals(listOf(1L, 2L, 3L), appliedCursors)
    }

    @Test
    fun `gap triggers RESEND with missingFromCursor equal to currentCursor plus 1`() {
        var requestedResendFrom: Long? = null
        val sequencer = PushSequencer(
            initialCursor = 2L,
            onResendNeeded = { requestedResendFrom = it }
        )

        // 本端游标 2，收到 fromCursor=5，cursor=6（发生缺口：未收到 3, 4）
        val res = sequencer.offer(cursor = 6L, fromCursor = 5L)
        assertTrue(res is PushOfferResult.GapDetected)
        val gap = res as PushOfferResult.GapDetected
        assertEquals(3L, gap.missingFromCursor)
        assertEquals(6L, gap.updateCursor)
        assertEquals(3L, requestedResendFrom)

        // 游标不应跳跃推进，保持在 2L
        assertEquals(2L, sequencer.getCurrentCursor())
    }

    @Test
    fun `stale and out of order packets discarded`() {
        val sequencer = PushSequencer(initialCursor = 5L)

        // 重复收到 5
        val resDuplicate = sequencer.offer(cursor = 5L, fromCursor = 4L)
        assertTrue(resDuplicate is PushOfferResult.Stale)
        assertEquals(5L, (resDuplicate as PushOfferResult.Stale).cursor)

        // 收到更老的 3
        val resOld = sequencer.offer(cursor = 3L, fromCursor = 2L)
        assertTrue(resOld is PushOfferResult.Stale)
        assertEquals(3L, (resOld as PushOfferResult.Stale).cursor)

        assertEquals(5L, sequencer.getCurrentCursor())
    }

    @Test
    fun `gap beyond window triggers full snapshot callback and self healing`() {
        var fullSnapshotRequested = false
        var recordedCurrent: Long? = null
        var recordedUpdate: Long? = null

        val sequencer = PushSequencer(
            initialCursor = 10L,
            windowSize = 256,
            onFullSnapshotNeeded = { curr, upd ->
                fullSnapshotRequested = true
                recordedCurrent = curr
                recordedUpdate = upd
            }
        )

        // 本端游标 10，新到达的包 fromCursor=300，缺失 11..299 共 289 个包，超出 256 环形窗口
        val res = sequencer.offer(cursor = 301L, fromCursor = 300L)
        assertTrue(res is PushOfferResult.BeyondWindowGap)
        assertTrue(fullSnapshotRequested)
        assertEquals(10L, recordedCurrent)
        assertEquals(301L, recordedUpdate)
        assertEquals(10L, sequencer.getCurrentCursor())

        // 收到带全量快照标记的推送包，客户端直接跃迁自愈
        val healRes = sequencer.offer(cursor = 301L, fromCursor = 300L, isFullSnapshot = true)
        assertTrue(healRes is PushOfferResult.Applied)
        assertEquals(301L, sequencer.getCurrentCursor())
    }

    @Test
    fun `adoptFullSnapshot explicitly advances cursor`() {
        val sequencer = PushSequencer(initialCursor = 10L)
        sequencer.adoptFullSnapshot(50L)
        assertEquals(50L, sequencer.getCurrentCursor())

        // 不应向后倒退
        sequencer.adoptFullSnapshot(30L)
        assertEquals(50L, sequencer.getCurrentCursor())
    }

    @Test
    fun `stale full snapshot is rejected and does not regress cursor`() {
        val sequencer = PushSequencer(initialCursor = 20L)
        val staleResult = sequencer.offer(cursor = 18L, fromCursor = 18L, isFullSnapshot = true)
        assertTrue(staleResult is PushOfferResult.Stale)
        assertEquals(20L, sequencer.getCurrentCursor())

        val duplicateResult = sequencer.offer(cursor = 20L, fromCursor = 20L, isFullSnapshot = true)
        assertTrue(duplicateResult is PushOfferResult.Stale)
        assertEquals(20L, sequencer.getCurrentCursor())

        sequencer.reset(0L)
        assertEquals(0L, sequencer.getCurrentCursor())
        val freshResult = sequencer.offer(cursor = 1L, fromCursor = 1L, isFullSnapshot = true)
        assertTrue(freshResult is PushOfferResult.Applied)
        assertEquals(1L, sequencer.getCurrentCursor())
    }
}

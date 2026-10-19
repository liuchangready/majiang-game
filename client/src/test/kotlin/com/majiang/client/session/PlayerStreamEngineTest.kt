package com.majiang.client.session

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.GameUpdateView
import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolTile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicBoolean

class PlayerStreamEngineTest {

    private fun sampleProtoSnapshot(
        cursor: Long = 10L,
        sessionId: String = "session-1",
        syncEpoch: String = "sync-1",
        viewerSeat: Int = 0,
        roundId: String = "round-1"
    ): ProtocolPlayerSnapshot {
        val tile = ProtocolTile.fromTileId(0)
        val decision = ProtocolPlayerDecision(
            roundId = roundId,
            decisionId = "dec-cursor-$cursor",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, tile)))
        )
        return ProtocolPlayerSnapshot(
            sessionId = sessionId,
            roundId = roundId,
            syncEpoch = syncEpoch,
            cursor = cursor,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = ProtocolPlayerView(
                viewerSeat = viewerSeat,
                phase = ProtocolGamePhase.DISCARD,
                roundWind = 1,
                roundNumber = 1,
                honbaSticks = 0,
                riichiSticks = 0,
                dealerSeat = 0,
                currentTurnPlayer = viewerSeat,
                wallRemaining = 70,
                doraIndicators = listOf(tile),
                scores = listOf(25000, 25000, 25000, 25000),
                riichiStatus = listOf(false, false, false, false),
                discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                handTileCounts = listOf(14, 13, 13, 13),
                myClosedTiles = listOf(tile),
                myDrawnTile = null,
                legalActions = listOf(ProtocolLegalAction.DiscardTile(0, tile))
            ),
            decision = decision
        )
    }

    private fun sampleProtoUpdate(
        cursor: Long,
        fromCursor: Long,
        isFullSnapshot: Boolean = false,
        sessionId: String = "session-1",
        syncEpoch: String = "sync-1",
        viewerSeat: Int = 0
    ): ProtocolPlayerUpdate {
        return ProtocolPlayerUpdate(
            snapshot = sampleProtoSnapshot(cursor, sessionId, syncEpoch, viewerSeat),
            fromCursor = fromCursor,
            visibleEvents = emptyList(),
            isFullSnapshot = isFullSnapshot
        )
    }

    @Test
    fun testInitialSnapshotAndInOrderAdvance() {
        val engine = PlayerStreamEngine()
        assertNull(engine.cachedSnapshot)
        assertFalse(engine.closed)

        val initialSnap = ProtocolViewMapper.mapSnapshot(sampleProtoSnapshot(cursor = 10L))
        val installRes = engine.installInitialSnapshotUnderLock(initialSnap)
        assertEquals(0, installRes.pendingUpdates.size)
        assertFalse(installRes.hadOverflow)
        assertEquals(10L, engine.cachedSnapshot?.cursor)

        // Consecutive update cursor 11 from 10
        val effect1 = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { seat, sId -> seat == 0 && sId == "session-1" }
        )

        assertTrue(effect1 is PlayerUpdateEffect.Dispatch)
        val dispatch1 = effect1 as PlayerUpdateEffect.Dispatch
        assertEquals(1, dispatch1.updates.size)
        assertEquals(11L, dispatch1.updates[0].snapshot.cursor)
        assertFalse(dispatch1.shouldRecover)
        assertEquals(11L, engine.cachedSnapshot?.cursor)
    }

    @Test
    fun testGapDetectionAndReorderBufferDrain() {
        val engine = PlayerStreamEngine()
        val initialSnap = ProtocolViewMapper.mapSnapshot(sampleProtoSnapshot(cursor = 10L))
        engine.installInitialSnapshotUnderLock(initialSnap)

        // Arrive gap update: cursor 13 from 12 (missing 11..12)
        val effectGap = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 13L, fromCursor = 12L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { seat, sId -> seat == 0 && sId == "session-1" }
        )

        assertTrue(effectGap is PlayerUpdateEffect.Dispatch)
        val dispatchGap = effectGap as PlayerUpdateEffect.Dispatch
        assertEquals(0, dispatchGap.updates.size)
        assertTrue(dispatchGap.shouldRecover)
        assertEquals(10L, dispatchGap.recoverCursor)
        assertEquals(1, engine.reorderBufferSize)
        assertTrue(engine.isResuming.get())

        // Now resume response brings cursor up to 12
        val resumeEffect = engine.handleResumeResponse(
            protoSnapshot = sampleProtoSnapshot(cursor = 12L),
            err = null,
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { seat, sId -> seat == 0 && sId == "session-1" }
        )

        assertTrue(resumeEffect is PlayerResumeEffect.Dispatch)
        val dispatchResume = resumeEffect as PlayerResumeEffect.Dispatch
        assertNotNull(dispatchResume.updateToDispatch)
        assertEquals(12L, dispatchResume.updateToDispatch?.snapshot?.cursor)
        // Reorder buffer drained the buffered 13
        assertEquals(1, dispatchResume.subsequentUpdates.size)
        assertEquals(13L, dispatchResume.subsequentUpdates[0].snapshot.cursor)
        assertEquals(13L, engine.cachedSnapshot?.cursor)
        assertEquals(0, engine.reorderBufferSize)
        assertFalse(engine.isResuming.get())
    }

    @Test
    fun testEarlyBufferDrainOnInstall() {
        val engine = PlayerStreamEngine(maxBufferSize = 64)
        synchronized(engine.syncLock) {
            engine.beginBindingUnderLock()
        }

        // Handshake in progress: buffer early updates
        val e1 = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { _, _ -> true }
        )
        assertTrue(e1 is PlayerUpdateEffect.BufferedEarly)
        assertEquals(1, engine.earlyBufferSize)

        val e2 = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 12L, fromCursor = 11L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { _, _ -> true }
        )
        assertTrue(e2 is PlayerUpdateEffect.BufferedEarly)
        assertEquals(2, engine.earlyBufferSize)

        // Now binding established: install initial snapshot cursor 10
        val initialSnap = ProtocolViewMapper.mapSnapshot(sampleProtoSnapshot(cursor = 10L))
        val installRes = engine.installInitialSnapshotUnderLock(initialSnap)

        assertEquals(2, installRes.pendingUpdates.size)
        assertEquals(11L, installRes.pendingUpdates[0].snapshot.cursor)
        assertEquals(12L, installRes.pendingUpdates[1].snapshot.cursor)
        assertEquals(12L, engine.cachedSnapshot?.cursor)
        assertEquals(0, engine.earlyBufferSize)
    }

    @Test
    fun testInterleavedBindingInstallationAndInboundPushDoesNotStrandEarlyBuffer() {
        val engine = PlayerStreamEngine(maxBufferSize = 64)

        // 1. 开始绑定
        synchronized(engine.syncLock) {
            engine.beginBindingUnderLock()
        }
        assertTrue(engine.bindingInProgress)

        // 2. 握手在途时，到达更新 11 (from 10)，进入早期缓冲
        val effect1 = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { _, _ -> true }
        )
        assertTrue(effect1 is PlayerUpdateEffect.BufferedEarly)
        assertEquals(1, engine.earlyBufferSize)

        // 3. 安装快照 (cursor 10)，原子排空缓冲并将 bindingInProgress 置 false
        val initialSnap = ProtocolViewMapper.mapSnapshot(sampleProtoSnapshot(cursor = 10L))
        val installRes = synchronized(engine.syncLock) {
            engine.installInitialSnapshotUnderLock(initialSnap)
        }
        assertFalse(engine.bindingInProgress)
        assertEquals(1, installRes.pendingUpdates.size)
        assertEquals(11L, installRes.pendingUpdates[0].snapshot.cursor)
        assertEquals(11L, engine.cachedSnapshot?.cursor)
        assertEquals(0, engine.earlyBufferSize)

        // 4. 安装后紧接着到达新 PUSH 12 (from 11)：绝不滞留早期缓冲，直接派发并使游标前进到 12
        val effect2 = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 12L, fromCursor = 11L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { _, _ -> true }
        )
        assertTrue(effect2 is PlayerUpdateEffect.Dispatch)
        val dispatch2 = effect2 as PlayerUpdateEffect.Dispatch
        assertEquals(1, dispatch2.updates.size)
        assertEquals(12L, dispatch2.updates[0].snapshot.cursor)
        assertEquals(12L, engine.cachedSnapshot?.cursor)
        assertEquals(0, engine.earlyBufferSize, "Early buffer must remain 0, no updates stranded")
    }

    @Test
    fun testFullSnapshotReplacementBaseline() {
        val engine = PlayerStreamEngine()
        val initialSnap = ProtocolViewMapper.mapSnapshot(sampleProtoSnapshot(cursor = 10L))
        engine.installInitialSnapshotUnderLock(initialSnap)

        // Put a gap in reorder buffer
        engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 15L, fromCursor = 14L),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { _, _ -> true }
        )
        assertEquals(1, engine.reorderBufferSize)

        // Full replacement push with cursor 20
        val fullEffect = engine.handleInboundUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 20L, fromCursor = 20L, isFullSnapshot = true),
            epoch = 1L,
            roomEpoch = 1L,
            isSessionValid = { true },
            isMatchingBinding = { _, _ -> true }
        )

        assertTrue(fullEffect is PlayerUpdateEffect.Dispatch)
        val dispatchFull = fullEffect as PlayerUpdateEffect.Dispatch
        assertEquals(1, dispatchFull.updates.size)
        assertEquals(20L, dispatchFull.updates[0].snapshot.cursor)
        assertEquals(20L, engine.cachedSnapshot?.cursor)
        assertEquals(0, engine.reorderBufferSize)
    }

    @Test
    fun testObserverNotificationAndExceptionIsolation() {
        val engine = PlayerStreamEngine()
        val delivered = mutableListOf<Long>()
        val failingCalled = AtomicBoolean(false)

        val sub1 = engine.onUpdate {
            failingCalled.set(true)
            throw RuntimeException("Observer error")
        }
        val sub2 = engine.onUpdate {
            delivered.add(it.snapshot.cursor)
        }

        val updateView = GameUpdateView(
            snapshot = ProtocolViewMapper.mapSnapshot(sampleProtoSnapshot(cursor = 5L)),
            fromCursor = 4L,
            visibleEvents = emptyList(),
            isFullSnapshot = false
        )

        engine.notifyUpdateListeners(updateView) { true }
        assertTrue(failingCalled.get())
        assertEquals(listOf(5L), delivered)

        // Unregister sub2
        sub2.close()
        sub1.close()
        engine.notifyUpdateListeners(updateView) { true }
        assertEquals(listOf(5L), delivered)
    }
}

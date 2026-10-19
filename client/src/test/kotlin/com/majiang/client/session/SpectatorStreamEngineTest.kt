package com.majiang.client.session

import com.majiang.client.api.PublicCheckpoint
import com.majiang.client.api.PublicSnapshotView
import com.majiang.client.api.PublicUpdateView
import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class SpectatorStreamEngineTest {

    private fun sampleProtoSessionSummary(): ProtocolPublicSessionSummary {
        return ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 1,
            dealerSeat = 0,
            honba = 0
        )
    }

    private fun sampleProtoSnapshot(
        cursor: Long = 10L,
        sessionId: String = "session-spec-1",
        syncEpoch: String = "sync-spec-1",
        sessionVersion: Long = 1L
    ): ProtocolPublicSnapshot {
        return ProtocolPublicSnapshot(
            sessionId = sessionId,
            sessionVersion = sessionVersion,
            syncEpoch = syncEpoch,
            cursor = cursor,
            board = null,
            session = sampleProtoSessionSummary()
        )
    }

    private fun sampleProtoUpdate(
        cursor: Long,
        fromCursor: Long,
        isFullSnapshot: Boolean = false,
        sessionId: String = "session-spec-1",
        syncEpoch: String = "sync-spec-1"
    ): ProtocolPublicUpdate {
        return ProtocolPublicUpdate(
            snapshot = sampleProtoSnapshot(cursor, sessionId, syncEpoch),
            fromCursor = fromCursor,
            visibleEvents = emptyList(),
            isFullSnapshot = isFullSnapshot
        )
    }

    @Test
    fun testInitialSnapshotAndInOrderAdvance() {
        val engine = SpectatorStreamEngine()
        assertNull(engine.cachedPublicSnapshot)
        assertFalse(engine.closed)

        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        val installRes = engine.installInitialSnapshotUnderLock(initialSnap)
        assertEquals(0, installRes.pendingUpdates.size)
        assertFalse(installRes.hadOverflow)
        assertEquals(10L, engine.cachedPublicSnapshot?.cursor)

        // Consecutive update cursor 11 from 10
        val effect1 = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )

        assertTrue(effect1 is SpectatorUpdateEffect.Dispatch)
        val dispatch1 = effect1 as SpectatorUpdateEffect.Dispatch
        assertEquals(1, dispatch1.updates.size)
        assertEquals(11L, dispatch1.updates[0].snapshot.cursor)
        assertFalse(dispatch1.shouldRecover)
        assertEquals("room-1", dispatch1.boundRoomId)
        assertEquals(11L, engine.cachedPublicSnapshot?.cursor)
    }

    @Test
    fun testGapDetectionAndRecoveryTokenGeneration() {
        val engine = SpectatorStreamEngine()
        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        engine.installInitialSnapshotUnderLock(initialSnap)

        // Arrive gap update: cursor 13 from 12 (missing 11..12)
        val effectGap = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 13L, fromCursor = 12L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )

        assertTrue(effectGap is SpectatorUpdateEffect.Dispatch)
        val dispatchGap = effectGap as SpectatorUpdateEffect.Dispatch
        assertEquals(0, dispatchGap.updates.size)
        assertTrue(dispatchGap.shouldRecover)
        assertEquals(10L, dispatchGap.checkpoint?.cursor)
        assertNotNull(dispatchGap.token)
        assertEquals(1, engine.reorderBufferSize)
        assertNotNull(engine.activePublicRecoveryToken.get())

        val token = dispatchGap.token!!

        // Resume public response brings cursor to 12
        val resumeEffect = engine.handleResumePublicResponse(
            protoSnapshot = sampleProtoSnapshot(cursor = 12L),
            err = null,
            token = token,
            roomId = "room-1",
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )

        assertTrue(resumeEffect is SpectatorResumeEffect.Dispatch)
        val dispatchResume = resumeEffect as SpectatorResumeEffect.Dispatch
        assertNotNull(dispatchResume.updateToDispatch)
        assertEquals(12L, dispatchResume.updateToDispatch?.snapshot?.cursor)
        assertTrue(dispatchResume.updateToDispatch?.isFullSnapshot == true)
        // Reorder buffer drained the buffered 13
        assertEquals(1, dispatchResume.subsequentUpdates.size)
        assertEquals(13L, dispatchResume.subsequentUpdates[0].snapshot.cursor)
        assertEquals(13L, engine.cachedPublicSnapshot?.cursor)
        assertEquals(0, engine.reorderBufferSize)
        assertNull(engine.activePublicRecoveryToken.get())
    }

    @Test
    fun testEarlyBufferDrainAndOverflow() {
        val engine = SpectatorStreamEngine(maxBufferSize = 2)
        synchronized(engine.publicSyncLock) {
            engine.beginBindingUnderLock()
        }

        // Handshake in progress: buffer early updates
        val e1 = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertTrue(e1 is SpectatorUpdateEffect.BufferedEarly)
        assertEquals(1, engine.earlyBufferSize)

        val e2 = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 12L, fromCursor = 11L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertTrue(e2 is SpectatorUpdateEffect.BufferedEarly)
        assertEquals(2, engine.earlyBufferSize)

        // 3rd exceeds maxBufferSize = 2
        val e3 = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 13L, fromCursor = 12L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertTrue(e3 is SpectatorUpdateEffect.OverflowEarly)
        assertTrue(engine.earlyPublicUpdateOverflowed.get())

        // Initial snapshot arrives at cursor 10
        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        val installRes = engine.installInitialSnapshotUnderLock(initialSnap)
        assertTrue(installRes.hadOverflow)
        // 11 and 12 were drained and applied in order
        assertEquals(2, installRes.pendingUpdates.size)
        assertEquals(11L, installRes.pendingUpdates[0].snapshot.cursor)
        assertEquals(12L, installRes.pendingUpdates[1].snapshot.cursor)
        assertEquals(12L, engine.cachedPublicSnapshot?.cursor)
        assertEquals(0, engine.earlyBufferSize)
    }

    @Test
    fun testInterleavedSpectatorBindingInstallationAndInboundPushDoesNotStrandEarlyBuffer() {
        val engine = SpectatorStreamEngine(maxBufferSize = 64)

        // 1. 开始观战绑定
        synchronized(engine.publicSyncLock) {
            engine.beginBindingUnderLock()
        }
        assertTrue(engine.spectatorBindingInProgress)

        // 2. 握手在途时，到达更新 11 (from 10)，进入早期缓冲
        val effect1 = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertTrue(effect1 is SpectatorUpdateEffect.BufferedEarly)
        assertEquals(1, engine.earlyBufferSize)

        // 3. 安装快照 (cursor 10)，原子排空缓冲并将 spectatorBindingInProgress 置 false
        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        val installRes = synchronized(engine.publicSyncLock) {
            engine.installInitialSnapshotUnderLock(initialSnap)
        }
        assertFalse(engine.spectatorBindingInProgress)
        assertEquals(1, installRes.pendingUpdates.size)
        assertEquals(11L, installRes.pendingUpdates[0].snapshot.cursor)
        assertEquals(11L, engine.cachedPublicSnapshot?.cursor)
        assertEquals(0, engine.earlyBufferSize)

        // 4. 安装后紧接着到达新 PUSH 12 (from 11)：绝不滞留早期缓冲，直接派发并使游标前进到 12
        val effect2 = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 12L, fromCursor = 11L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertTrue(effect2 is SpectatorUpdateEffect.Dispatch)
        val dispatch2 = effect2 as SpectatorUpdateEffect.Dispatch
        assertEquals(1, dispatch2.updates.size)
        assertEquals(12L, dispatch2.updates[0].snapshot.cursor)
        assertEquals(12L, engine.cachedPublicSnapshot?.cursor)
        assertEquals(0, engine.earlyBufferSize, "Early spectator buffer must remain 0, no updates stranded")
    }

    @Test
    fun testFullSnapshotBaselineReset() {
        val engine = SpectatorStreamEngine()
        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        engine.installInitialSnapshotUnderLock(initialSnap)

        // Add an out-of-order update to reorder buffer
        engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 14L, fromCursor = 13L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertEquals(1, engine.reorderBufferSize)

        // Full snapshot baseline arrives at cursor 20
        val fullUpdate = sampleProtoUpdate(cursor = 20L, fromCursor = 20L, isFullSnapshot = true)
        val effect = engine.handleInboundPublicUpdate(
            protoUpdate = fullUpdate,
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )

        assertTrue(effect is SpectatorUpdateEffect.Dispatch)
        val dispatch = effect as SpectatorUpdateEffect.Dispatch
        assertEquals(1, dispatch.updates.size)
        assertEquals(20L, dispatch.updates[0].snapshot.cursor)
        assertTrue(dispatch.updates[0].isFullSnapshot)
        assertEquals(20L, engine.cachedPublicSnapshot?.cursor)
        // Reorder buffer was cleared because 13 < 20
        assertEquals(0, engine.reorderBufferSize)
    }

    @Test
    fun testAdoptAuthoritativePublicSnapshotUnderLock() {
        val engine = SpectatorStreamEngine()
        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        engine.installInitialSnapshotUnderLock(initialSnap)

        // Adopt newer snapshot
        val newerSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 15L))
        val adopted = engine.adoptAuthoritativePublicSnapshotUnderLock(
            view = newerSnap,
            epoch = 1L,
            roomEpoch = 1L,
            roomId = "room-1",
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertTrue(adopted)
        assertEquals(15L, engine.cachedPublicSnapshot?.cursor)

        // Reject if roomId mismatches
        val snap2 = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 20L))
        val rejectedRoom = engine.adoptAuthoritativePublicSnapshotUnderLock(
            view = snap2,
            epoch = 1L,
            roomEpoch = 1L,
            roomId = "room-1",
            boundRoomIdProvider = { "room-2" },
            isSessionValid = { true }
        )
        assertFalse(rejectedRoom)
        assertEquals(15L, engine.cachedPublicSnapshot?.cursor)

        // Reject if cursor older
        val olderSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 12L))
        val rejectedOld = engine.adoptAuthoritativePublicSnapshotUnderLock(
            view = olderSnap,
            epoch = 1L,
            roomEpoch = 1L,
            roomId = "room-1",
            boundRoomIdProvider = { "room-1" },
            isSessionValid = { true }
        )
        assertFalse(rejectedOld)
        assertEquals(15L, engine.cachedPublicSnapshot?.cursor)
    }

    @Test
    fun testListenerRegistrationAndExceptionIsolation() {
        val engine = SpectatorStreamEngine()
        val callCount = AtomicInteger(0)
        val handledSecond = AtomicBoolean(false)

        val reg1 = engine.onPublicUpdate {
            callCount.incrementAndGet()
            throw RuntimeException("Boom in listener")
        }
        val reg2 = engine.onPublicUpdate {
            handledSecond.set(true)
        }

        val update = PublicUpdateView(
            snapshot = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 11L)),
            fromCursor = 10L,
            visibleEvents = emptyList(),
            isFullSnapshot = false
        )

        // Notification should not throw and second listener should execute
        engine.notifyPublicUpdateListeners(update) { true }
        assertEquals(1, callCount.get())
        assertTrue(handledSecond.get())

        // Close reg1
        reg1.close()
        handledSecond.set(false)
        engine.notifyPublicUpdateListeners(update) { true }
        assertEquals(1, callCount.get())
        assertTrue(handledSecond.get())

        // Close engine
        engine.close()
        assertTrue(engine.closed)
        assertNull(engine.cachedPublicSnapshot)
    }

    @Test
    fun testRebindingRejectionAndRoomIdentity() {
        val engine = SpectatorStreamEngine()
        val initialSnap = ProtocolViewMapper.mapPublicSnapshot(sampleProtoSnapshot(cursor = 10L))
        engine.installInitialSnapshotUnderLock(initialSnap)

        // Inbound update rejected when boundRoomIdProvider returns null
        val effectNull = engine.handleInboundPublicUpdate(
            protoUpdate = sampleProtoUpdate(cursor = 11L, fromCursor = 10L),
            epoch = 1L,
            roomEpoch = 1L,
            boundRoomIdProvider = { null },
            isSessionValid = { true }
        )
        assertTrue(effectNull is SpectatorUpdateEffect.None)

        // Resume response rejected when boundRoomIdProvider returns different room
        val token = engine.allocateRecoveryToken(1L, 1L)
        engine.activePublicRecoveryToken.set(token)
        val resumeMismatch = engine.handleResumePublicResponse(
            protoSnapshot = sampleProtoSnapshot(cursor = 12L),
            err = null,
            token = token,
            roomId = "room-1",
            boundRoomIdProvider = { "room-2" },
            isSessionValid = { true }
        )
        assertTrue(resumeMismatch is SpectatorResumeEffect.None)
    }
}

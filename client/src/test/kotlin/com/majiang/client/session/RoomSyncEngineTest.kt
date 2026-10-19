package com.majiang.client.session

import com.majiang.client.api.RoomInfo
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolRoomMember
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

class RoomSyncEngineTest {

    private fun sampleProtoRoom(roomId: String = "ROOM_101", version: Long = 1L): ProtocolRoomInfo {
        return ProtocolRoomInfo(
            roomId = roomId,
            lifecycle = ProtocolRoomLifecycle.LOBBY,
            version = version,
            members = listOf(ProtocolRoomMember(0, "Alice", true, true, "HUMAN"))
        )
    }

    @Test
    fun testInitialStateAndVersionMonotonicity() {
        val engine = RoomSyncEngine()
        assertNull(engine.cachedInfo)
        assertNull(engine.cachedVersion)
        assertEquals(0L, engine.currentRoomEpoch)
        assertFalse(engine.closed)

        // 1. Initial valid update v1
        val effect1 = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 1L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effect1 is RoomUpdateEffect.Notify)
        val notify1 = effect1 as RoomUpdateEffect.Notify
        assertEquals(1L, notify1.roomInfo.version)
        assertEquals(1L, engine.cachedVersion)
        assertEquals("ROOM_101", engine.cachedInfo?.roomId)

        // 2. Duplicate version 1L should be ignored
        val effectDuplicate = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 1L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effectDuplicate is RoomUpdateEffect.None)
        assertEquals(1L, engine.cachedVersion)

        // 3. Lower version (stale arrival) should be ignored
        val effectStale = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 0L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effectStale is RoomUpdateEffect.None)
        assertEquals(1L, engine.cachedVersion)

        // 4. Higher version 2L should advance cache and produce Notify
        val effect2 = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effect2 is RoomUpdateEffect.Notify)
        assertEquals(2L, engine.cachedVersion)
    }

    @Test
    fun testRoomIdMismatchAndInvalidSessionAreIgnored() {
        val engine = RoomSyncEngine()

        // Mismatched room ID
        val effectMismatch = engine.handleInboundRoomUpdate(
            sampleProtoRoom(roomId = "OTHER_ROOM", version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effectMismatch is RoomUpdateEffect.None)
        assertNull(engine.cachedInfo)

        // Invalid session predicate
        val effectInvalidSession = engine.handleInboundRoomUpdate(
            sampleProtoRoom(roomId = "ROOM_101", version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { false }
        )
        assertTrue(effectInvalidSession is RoomUpdateEffect.None)
        assertNull(engine.cachedInfo)
    }

    @Test
    fun testObserverNotificationAndUnregistration() {
        val engine = RoomSyncEngine()
        val received = mutableListOf<Long>()
        val handle = engine.onRoomUpdate { received.add(it.version) }

        val effect1 = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effect1 is RoomUpdateEffect.Notify)
        val notify1 = effect1 as RoomUpdateEffect.Notify
        assertEquals(1, notify1.observers.size)

        // Dispatch outside engine lock
        for (obs in notify1.observers) {
            engine.notifyObserver(
                observer = obs,
                info = notify1.roomInfo,
                connectionEpoch = notify1.connectionEpoch,
                roomEpoch = notify1.roomEpoch,
                isSessionValidAndMatchingRoom = { true }
            )
        }
        assertEquals(listOf(2L), received)

        // Unregister observer
        handle.close()

        val effect2 = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 3L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effect2 is RoomUpdateEffect.Notify)
        val notify2 = effect2 as RoomUpdateEffect.Notify
        assertEquals(0, notify2.observers.size, "Unregistered observer should not be in snapshot")
    }

    @Test
    fun testUnregisterObserverSuppressesAlreadyQueuedNotification() {
        val engine = RoomSyncEngine()
        val received = mutableListOf<Long>()
        val handle = engine.onRoomUpdate { received.add(it.version) }

        val effect = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        ) as RoomUpdateEffect.Notify

        assertEquals(1, effect.observers.size)
        val observer = effect.observers.single()

        // Unregister observer before dispatch execution
        handle.close()

        engine.notifyObserver(
            observer = observer,
            info = effect.roomInfo,
            connectionEpoch = effect.connectionEpoch,
            roomEpoch = effect.roomEpoch,
            isSessionValidAndMatchingRoom = { true }
        )

        assertTrue(received.isEmpty(), "Unregistered observer must not execute queued notification")
    }

    @Test
    fun testObserverMonotonicityOnOutOfOrderDispatches() {
        val engine = RoomSyncEngine()
        val received = mutableListOf<Long>()
        engine.onRoomUpdate { received.add(it.version) }

        val effectV2 = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        ) as RoomUpdateEffect.Notify

        val effectV3 = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 3L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        ) as RoomUpdateEffect.Notify

        val obsV2 = effectV2.observers.single()
        val obsV3 = effectV3.observers.single()

        // Simulate out-of-order dispatch: deliver v3 first, then v2
        engine.notifyObserver(
            observer = obsV3,
            info = effectV3.roomInfo,
            connectionEpoch = effectV3.connectionEpoch,
            roomEpoch = effectV3.roomEpoch,
            isSessionValidAndMatchingRoom = { true }
        )
        assertEquals(listOf(3L), received)

        // Now deliver stale v2: must be suppressed
        engine.notifyObserver(
            observer = obsV2,
            info = effectV2.roomInfo,
            connectionEpoch = effectV2.connectionEpoch,
            roomEpoch = effectV2.roomEpoch,
            isSessionValidAndMatchingRoom = { true }
        )
        assertEquals(listOf(3L), received, "Stale v2 must not be delivered after v3")
    }

    @Test
    fun testObserverExceptionIsolation() {
        val engine = RoomSyncEngine()
        val received = mutableListOf<Long>()

        // Observer 1 throws
        engine.onRoomUpdate { throw RuntimeException("Simulated observer crash") }
        // Observer 2 should still receive update
        engine.onRoomUpdate { received.add(it.version) }

        val effect = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 5L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        ) as RoomUpdateEffect.Notify

        assertEquals(2, effect.observers.size)
        for (obs in effect.observers) {
            engine.notifyObserver(
                observer = obs,
                info = effect.roomInfo,
                connectionEpoch = effect.connectionEpoch,
                roomEpoch = effect.roomEpoch,
                isSessionValidAndMatchingRoom = { true }
            )
        }
        assertEquals(listOf(5L), received, "Observer 2 must receive update despite Observer 1 throwing")
    }

    @Test
    fun testTrackFutureAndPruneOnGenerationChange() {
        val engine = RoomSyncEngine()
        val f1 = CompletableFuture<String>()
        val f2 = CompletableFuture<String>()

        engine.trackRoomFuture(f1, roomEpoch = 0L)
        assertEquals(0L, engine.currentRoomEpoch)

        engine.advanceRoomEpoch()
        assertEquals(1L, engine.currentRoomEpoch)

        engine.trackRoomFuture(f2, roomEpoch = 1L)

        // Pruning futures older than epoch 1 should collect f1, keeping f2
        val pruned = engine.pruneFuturesOlderThan(1L)
        assertEquals(listOf(f1), pruned)

        // Pruning <= 1 should collect f2
        val pruned2 = engine.pruneFuturesLessOrEqual(1L)
        assertEquals(listOf(f2), pruned2)
    }

    @Test
    fun testRetireRoomClearsCacheAndPrunesFutures() {
        val engine = RoomSyncEngine()
        engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 10L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertEquals(10L, engine.cachedVersion)

        val f = CompletableFuture<String>()
        engine.trackRoomFuture(f, roomEpoch = 0L)

        val toFail = engine.retireRoom()
        assertEquals(1L, engine.currentRoomEpoch)
        assertNull(engine.cachedInfo)
        assertNull(engine.cachedVersion)
        assertEquals(listOf(f), toFail)
    }

    @Test
    fun testAdoptAuthoritativeRoomInfo() {
        val engine = RoomSyncEngine()

        // 1. Initial adoption
        val adopted = engine.adoptAuthoritativeRoomInfo(
            protoInfo = sampleProtoRoom(version = 3L),
            expectedRoomId = "ROOM_101",
            requestRoomEpoch = 0L,
            isSessionValid = { true }
        )
        assertNotNull(adopted)
        assertEquals(3L, adopted?.version)
        assertEquals(3L, engine.cachedVersion)

        // 2. Query with older version 2L returns existing cached 3L without downgrade
        val adoptedStale = engine.adoptAuthoritativeRoomInfo(
            protoInfo = sampleProtoRoom(version = 2L),
            expectedRoomId = "ROOM_101",
            requestRoomEpoch = 0L,
            isSessionValid = { true }
        )
        assertNotNull(adoptedStale)
        assertEquals(3L, adoptedStale?.version)
        assertEquals(3L, engine.cachedVersion)

        // 3. Query with newer version 4L advances cache
        val adoptedNewer = engine.adoptAuthoritativeRoomInfo(
            protoInfo = sampleProtoRoom(version = 4L),
            expectedRoomId = "ROOM_101",
            requestRoomEpoch = 0L,
            isSessionValid = { true }
        )
        assertNotNull(adoptedNewer)
        assertEquals(4L, adoptedNewer?.version)
        assertEquals(4L, engine.cachedVersion)

        // 4. Stale roomEpoch query rejected
        val adoptedWrongEpoch = engine.adoptAuthoritativeRoomInfo(
            protoInfo = sampleProtoRoom(version = 5L),
            expectedRoomId = "ROOM_101",
            requestRoomEpoch = 999L,
            isSessionValid = { true }
        )
        assertNull(adoptedWrongEpoch)
        assertEquals(4L, engine.cachedVersion)
    }

    @Test
    fun testCloseDeactivatesObserversAndCollectsFutures() {
        val engine = RoomSyncEngine()
        var received = false
        engine.onRoomUpdate { received = true }

        val f = CompletableFuture<String>()
        engine.trackRoomFuture(f)

        engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 1L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )

        val remainingFutures = engine.close()
        assertTrue(engine.closed)
        assertEquals(listOf(f), remainingFutures)
        assertNull(engine.cachedInfo)

        // Subsequent update rejected
        val effectAfterClose = engine.handleInboundRoomUpdate(
            sampleProtoRoom(version = 2L),
            connectionEpoch = 1L,
            expectedRoomId = "ROOM_101",
            isSessionValid = { true }
        )
        assertTrue(effectAfterClose is RoomUpdateEffect.None)
        assertFalse(received)
    }

    @Test
    fun testRebindingWhileBlockedOnRoomLockRejectsOldRoomUpdate() {
        val engine = RoomSyncEngine()
        var currentBoundRoom: String? = "ROOM_A"

        // Lock roomLock so incoming update blocks before committing
        val worker = Thread {
            engine.handleInboundRoomUpdate(
                sampleProtoRoom(roomId = "ROOM_A", version = 99L),
                connectionEpoch = 1L,
                currentBoundRoomId = { currentBoundRoom },
                isSessionValid = { true }
            )
        }

        synchronized(engine.roomLock) {
            worker.start()
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
            while (worker.state != Thread.State.BLOCKED && worker.isAlive && System.nanoTime() < deadline) {
                Thread.yield()
            }
            assertEquals(Thread.State.BLOCKED, worker.state)
            // Rebinding occurs while update is waiting on roomLock
            currentBoundRoom = "ROOM_B"
            engine.advanceRoomEpoch()
            engine.resetRoomInfo()
        }

        worker.join(2000)
        assertFalse(worker.isAlive)
        // Cache must NOT contain ROOM_A or version 99L
        assertNull(engine.cachedInfo)
        assertNull(engine.cachedVersion)

        // ROOM_B update is accepted
        val effectB = engine.handleInboundRoomUpdate(
            sampleProtoRoom(roomId = "ROOM_B", version = 1L),
            connectionEpoch = 1L,
            currentBoundRoomId = { currentBoundRoom },
            isSessionValid = { true }
        )
        assertTrue(effectB is RoomUpdateEffect.Notify)
        assertEquals("ROOM_B", engine.cachedInfo?.roomId)
        assertEquals(1L, engine.cachedVersion)
    }
}

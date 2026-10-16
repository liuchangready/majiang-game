package com.majiang.v4runtime

import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.api.ActionChoice
import com.majiang.client.api.ConnectionState
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.ServerConnection
import com.majiang.server.inprocess.InProcessServer
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolRoomMember
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpScheduler
import com.majiang.transport.udp.Cancellable
import java.lang.reflect.Proxy
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Review-owned runtime behavior, independent of Anti's module tests. */
@Timeout(10)
class V4RuntimeAcceptanceTest {
    private fun <T> CompletableFuture<T>.await(): T = get(2, TimeUnit.SECONDS)

    private class FakeConnection(val initial: ProtocolPlayerSnapshot) {
        val updateListeners = mutableListOf<(ProtocolPlayerUpdate) -> Unit>()
        val publicUpdateListeners = mutableListOf<(ProtocolPublicUpdate) -> Unit>()
        val disconnectListeners = mutableListOf<(String) -> Unit>()
        val roomListeners = mutableListOf<(ProtocolRoomInfo) -> Unit>()
        val roomInfo = ProtocolRoomInfo("review-room", ProtocolRoomLifecycle.LOBBY, version = 1L,
            members = listOf(ProtocolRoomMember(0, "Host", true, false, "HUMAN")))
        var roomInfoReply = CompletableFuture.completedFuture(roomInfo)
        var closed = false
        var createReply = CompletableFuture.completedFuture(binding(initial))
        var snapshotReply = CompletableFuture.completedFuture(initial)
        var decisionReply = CompletableFuture.completedFuture(initial.decision)
        var beforeCreate: (() -> Unit)? = null
        var beforeResume: (() -> Unit)? = null
        var resumeBindingReply = CompletableFuture.completedFuture(binding(initial))
        var actionReply = CompletableFuture<ProtocolActionReceipt>()
        var throwingMethod: String? = null
        val calls = mutableMapOf<String, Int>()
        val resumeReplies = mutableListOf<CompletableFuture<ProtocolPlayerSnapshot>>()
        var immediateResumeSnapshot: ProtocolPlayerSnapshot? = null
        val snapshotReplies = mutableListOf<CompletableFuture<ProtocolPlayerSnapshot>>()

        @Suppress("UNCHECKED_CAST")
        val connection = Proxy.newProxyInstance(
            ServerConnection::class.java.classLoader, arrayOf(ServerConnection::class.java)
        ) { proxy, method, args ->
            calls[method.name] = (calls[method.name] ?: 0) + 1
            check(method.name != throwingMethod) { "Synchronous failure in ${method.name}" }
            when (method.name) {
                "getClosed" -> closed
                "getDisconnectReason" -> if (closed) "LOCAL_CLOSED" else null
                "onUpdate" -> {
                    val listener = args!![0] as (ProtocolPlayerUpdate) -> Unit
                    updateListeners.add(listener)
                    AutoCloseable { updateListeners.remove(listener) }
                }
                "onPublicUpdate" -> {
                    val listener = args!![0] as (ProtocolPublicUpdate) -> Unit
                    publicUpdateListeners.add(listener)
                    AutoCloseable { publicUpdateListeners.remove(listener) }
                }
                "onDisconnect" -> {
                    val listener = args!![0] as (String) -> Unit
                    disconnectListeners.add(listener)
                    AutoCloseable { disconnectListeners.remove(listener) }
                }
                "onRoomUpdate" -> {
                    val listener = args!![0] as (ProtocolRoomInfo) -> Unit
                    roomListeners.add(listener)
                    AutoCloseable { roomListeners.remove(listener) }
                }
                "getRoomInfo" -> roomInfoReply
                "createRoom" -> { beforeCreate?.invoke(); createReply }
                "resumeSession" -> { beforeResume?.invoke(); resumeBindingReply }
                "snapshot" -> snapshotReply.also { snapshotReplies.add(it) }
                "decision" -> decisionReply
                "resume" -> (immediateResumeSnapshot?.let { CompletableFuture.completedFuture(it) }
                    ?: CompletableFuture<ProtocolPlayerSnapshot>()).also { resumeReplies.add(it) }
                "submit", "start", "leaveRoom", "claimControl", "releaseControl", "setReady", "updateConfig" -> actionReply
                "close" -> { closed = true; null }
                "hashCode" -> System.identityHashCode(proxy)
                "equals" -> proxy === args!![0]
                "toString" -> "ReviewFakeConnection"
                else -> CompletableFuture.failedFuture<Any>(UnsupportedOperationException(method.name))
            }
        } as ServerConnection

        fun push(snapshot: ProtocolPlayerSnapshot, from: Long, full: Boolean = false) {
            updateListeners.toList().forEach { it(ProtocolPlayerUpdate(snapshot, from, isFullSnapshot = full)) }
        }

        fun drop(reason: String) {
            closed = true
            disconnectListeners.toList().forEach { it(reason) }
        }

        companion object {
            fun binding(snapshot: ProtocolPlayerSnapshot) = RoomBinding("review-room", 0, "review-token", snapshot)
        }
    }

    private class ManualTime : UdpClock, UdpScheduler {
        private data class Job(val deadline: Long, val task: Runnable, var cancelled: Boolean = false)
        private val jobs = mutableListOf<Job>()
        var now = 0L
        val delays = mutableListOf<Long>()
        override fun currentTimeMillis() = now
        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            delays.add(delayMillis)
            val job = Job(now + delayMillis, task)
            jobs.add(job)
            return Cancellable { job.cancelled = true }
        }
        fun next() {
            val job = jobs.filterNot { it.cancelled }.minByOrNull { it.deadline } ?: error("No scheduled task")
            jobs.remove(job)
            now = job.deadline
            job.task.run()
        }
        val activeCount get() = jobs.count { !it.cancelled }
    }

    private fun fixture(block: (ProtocolPlayerSnapshot) -> Unit) {
        InProcessServer().use { server ->
            val original = server.createLocalTable().snapshot().await()
            block(original.copy(sessionId = "review-session", syncEpoch = "review-epoch", cursor = 0))
        }
    }

    @Test fun duplicateAndForeignPushesDoNotReachUi() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            val cursors = mutableListOf<Long>()
            runtime.onUpdate { cursors.add(it.snapshot.cursor) }
            runtime.createRoom().await()
            cursors.clear()
            fake.push(initial.copy(cursor = 1), 0)
            fake.push(initial.copy(cursor = 1), 0)
            fake.push(initial.copy(cursor = 2, syncEpoch = "retired-epoch"), 1)
            fake.push(initial.copy(cursor = 2, sessionId = "foreign-session"), 1)
            assertEquals(listOf(1L), cursors)
        }
    }

    @Test fun gapCannotExposeFutureDecisionAndPredecessorDrainsInOrder() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            val cursors = mutableListOf<Long>()
            runtime.onUpdate { cursors.add(it.snapshot.cursor) }
            runtime.createRoom().await()
            cursors.clear()
            fake.push(initial.copy(cursor = 2), 1)
            assertTrue(cursors.isEmpty(), "A missing predecessor must not expose a future decision")
            fake.push(initial.copy(cursor = 1), 0)
            assertEquals(listOf(1L, 2L), cursors)
        }
    }

    @Test fun earlyPushCannotBeRolledBackByBindingReply() = fixture { initial ->
        val fake = FakeConnection(initial)
        fake.beforeCreate = { fake.push(initial.copy(cursor = 1), 0) }
        ServerConnectionSession(fake.connection).use { runtime ->
            assertEquals(1L, runtime.createRoom().await().cursor,
                "Binding completion must expose the latest reconciled view")
        }
    }

    @Test fun closingRuntimeTerminatesOutstandingRequests() = fixture { initial ->
        val fake = FakeConnection(initial)
        val runtime = ServerConnectionSession(fake.connection)
        runtime.createRoom().await()
        fake.snapshotReply = CompletableFuture()
        val request = runtime.snapshot()
        runtime.close()
        assertTrue(request.isDone, "Closing must terminate every in-flight future")
        assertTrue(request.isCompletedExceptionally || request.isCancelled)
        fake.snapshotReply.complete(initial.copy(cursor = 7))
        assertTrue(request.isCompletedExceptionally || request.isCancelled, "Late response cannot resurrect request")
    }

    @Test fun staleCallbackAfterCloseCannotNotifyUi() = fixture { initial ->
        val fake = FakeConnection(initial)
        val runtime = ServerConnectionSession(fake.connection)
        val cursors = mutableListOf<Long>()
        runtime.onUpdate { cursors.add(it.snapshot.cursor) }
        runtime.createRoom().await()
        val callbacks = fake.updateListeners.toList()
        runtime.close()
        cursors.clear()
        callbacks.forEach { it(ProtocolPlayerUpdate(initial.copy(cursor = 1), 0)) }
        assertTrue(cursors.isEmpty(), "Callback captured before close must be invalidated")
    }

    @Test fun recoveryIsDeduplicatedAndFailureAllowsRetry() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.snapshotReplies.clear()
            fake.snapshotReply = CompletableFuture()
            fake.push(initial.copy(cursor = 3), 2)
            fake.push(initial.copy(cursor = 4), 3)
            val reads = fake.resumeReplies + fake.snapshotReplies
            assertEquals(1, reads.size, "Only one recovery may be in flight")
            reads.single().completeExceptionally(IllegalStateException("temporary transport failure"))
            fake.snapshotReply = CompletableFuture()
            fake.push(initial.copy(cursor = 5), 4)
            assertEquals(2, fake.resumeReplies.size + fake.snapshotReplies.size, "Failed recovery must not permanently suppress retry")
        }
    }

    @Test fun oneFaultyUiListenerCannotBlockAnother() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            var armed = false
            runtime.onUpdate { if (armed) error("review listener failure") }
            val cursors = mutableListOf<Long>()
            runtime.onUpdate { cursors.add(it.snapshot.cursor) }
            cursors.clear()
            armed = true
            fake.push(initial.copy(cursor = 1), 0)
            assertEquals(listOf(1L), cursors)
        }
    }

    @Test fun recoveryContinuesWhenAnotherGapRemains() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.push(initial.copy(cursor = 2), 1)
            fake.push(initial.copy(cursor = 5), 4)
            assertEquals(1, fake.resumeReplies.size)
            fake.resumeReplies[0].complete(initial.copy(cursor = 2))
            assertEquals(2, fake.resumeReplies.size, "Remaining buffered gap must trigger another recovery")
            fake.resumeReplies[1].complete(initial.copy(cursor = 5))
            assertEquals(5L, runtime.currentSnapshot!!.cursor)
        }
    }

    @Test fun synchronousTransportFailureBecomesFailedFuture() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.throwingMethod = "snapshot"
            val failure = runtime.snapshot()
            assertTrue(failure.isCompletedExceptionally)
            fake.throwingMethod = null
            assertEquals(0L, runtime.snapshot().await().cursor)
        }
    }

    @Test fun cancelledActionReleasesGateAndLateReceiptCannotClearNewPending() = fixture { base ->
        val initial = base.copy(decision = base.decision.copy(
            options = listOf(ProtocolActionOption("pass", ProtocolLegalAction.Pass(0)))))
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            val choice = ActionChoice("first", initial.roundId, initial.decision.decisionId,
                initial.decision.controlEpoch, "pass")
            val firstReply = fake.actionReply
            val first = runtime.submit(choice)
            assertTrue(runtime.isPending)
            first.cancel(false)
            assertFalse(runtime.isPending, "Cancellation must release the pending gate")
            fake.actionReply = CompletableFuture()
            val second = runtime.submit(choice.copy(requestId = "second"))
            assertTrue(runtime.isPending)
            firstReply.complete(ProtocolActionReceipt("first", ProtocolReceiptStatus.APPLIED))
            assertTrue(runtime.isPending, "Old receipt must not release a newer action's gate")
            fake.actionReply.complete(ProtocolActionReceipt("second", ProtocolReceiptStatus.REJECTED))
            second.await()
            assertFalse(runtime.isPending)
        }
    }

    @Test fun uiDispatchControlsReceiptsAndDropsQueuedUpdatesAfterClose() = fixture { initial ->
        val fake = FakeConnection(initial)
        val queue = java.util.ArrayDeque<() -> Unit>()
        fun drain() { while (queue.isNotEmpty()) queue.removeFirst().invoke() }
        val runtime = ServerConnectionSession(fake.connection, dispatch = { queue.add(it) })
        val created = runtime.createRoom()
        assertFalse(created.isDone)
        drain()
        created.await()
        val cursors = mutableListOf<Long>()
        runtime.onUpdate { cursors.add(it.snapshot.cursor) }
        fake.push(initial.copy(cursor = 1), 0)
        assertTrue(cursors.isEmpty())
        fake.snapshotReply = CompletableFuture()
        val pending = runtime.snapshot()
        var consumerCalled = false
        pending.whenComplete { _, _ -> consumerCalled = true }
        runtime.close()
        assertFalse(consumerCalled, "Close must dispatch future consumption, too")
        drain()
        assertTrue(pending.isDone)
        assertTrue(consumerCalled)
        assertTrue(cursors.isEmpty(), "Queued update cannot notify after close")
    }

    @Test fun permanentDisconnectTerminatesRuntimeWithoutRetry() = fixture { initial ->
        for (reason in listOf(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, ApiProtocol.ROOM_CLOSED, ApiProtocol.SERVER_SHUTDOWN)) {
            val fake = FakeConnection(initial)
            val time = ManualTime()
            var connects = 0
            ServerConnectionSession(fake.connection, connectionFactory = {
                connects++
                CompletableFuture.completedFuture(FakeConnection(initial).connection)
            }, clock = time, scheduler = time).use { runtime ->
                runtime.createRoom().await()
                fake.snapshotReply = CompletableFuture()
                val pending = runtime.snapshot()
                fake.drop(reason)
                assertTrue(runtime.closed, reason)
                assertEquals(ConnectionState.CLOSED, runtime.connectionState)
                assertEquals(reason, runtime.disconnectReason)
                assertTrue(pending.isDone)
                assertEquals(0, time.activeCount)
                assertEquals(0, connects)
            }
        }
    }

    @Test fun retryBackoffKeepsViewAndNewEpochMayRestartCursorWithoutClaimingControl() = fixture { initial ->
        val fake = FakeConnection(initial)
        val restored = initial.copy(syncEpoch = "new-epoch", cursor = 0)
        val replacement = FakeConnection(restored)
        val time = ManualTime()
        var connects = 0
        ServerConnectionSession(fake.connection, connectionFactory = {
            connects++
            if (connects <= 2) CompletableFuture.failedFuture(java.io.IOException("temporarily unavailable"))
            else CompletableFuture.completedFuture(replacement.connection)
        }, clock = time, scheduler = time).use { runtime ->
            runtime.createRoom().await()
            fake.push(initial.copy(cursor = 1), 0)
            fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
            assertEquals(ConnectionState.RECONNECTING, runtime.connectionState)
            assertEquals(1L, runtime.currentSnapshot!!.cursor)
            time.next()
            time.next()
            assertTrue(time.delays[1] > time.delays[0])
            assertTrue(time.delays[2] > time.delays[1])
            time.next()
            assertEquals(ConnectionState.CONNECTED, runtime.connectionState)
            assertEquals("new-epoch", runtime.currentSnapshot!!.syncEpoch)
            assertEquals(0L, runtime.currentSnapshot!!.cursor)
            assertEquals(0, replacement.calls["claimControl"] ?: 0)
        }
    }

    @Test fun reconnectRetainsLastVisibleSnapshotWhileResumeHandshakeIsPending() = fixture { initial ->
        val fake = FakeConnection(initial)
        val replacement = FakeConnection(initial)
        replacement.resumeBindingReply = CompletableFuture()
        val time = ManualTime()
        ServerConnectionSession(fake.connection,
            connectionFactory = { CompletableFuture.completedFuture(replacement.connection) },
            clock = time, scheduler = time).use { runtime ->
            runtime.createRoom().await()
            fake.push(initial.copy(cursor = 1), 0)
            fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
            time.next()
            assertEquals(1, replacement.calls["resumeSession"] ?: 0)
            assertFalse(replacement.resumeBindingReply.isDone)
            assertEquals(1L, runtime.currentSnapshot?.cursor,
                "An in-flight reconnect handshake must retain the last visible snapshot")
        }
    }

    @Test fun reconnectBuffersPushBeforeResumeBindingReply() = fixture { initial ->
        val fake = FakeConnection(initial)
        val restored = initial.copy(syncEpoch = "new-epoch")
        val replacement = FakeConnection(restored)
        replacement.beforeResume = { replacement.push(restored.copy(cursor = 1), 0) }
        val time = ManualTime()
        ServerConnectionSession(fake.connection,
            connectionFactory = { CompletableFuture.completedFuture(replacement.connection) },
            clock = time, scheduler = time).use { runtime ->
            runtime.createRoom().await()
            fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
            time.next()
            assertEquals("new-epoch", runtime.currentSnapshot!!.syncEpoch)
            assertEquals(1L, runtime.currentSnapshot!!.cursor)
        }
    }

    @Test fun explicitFullReplacementCanSkipGapButNeverAdoptForeignEpoch() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            val updates = mutableListOf<com.majiang.client.api.GameUpdateView>()
            runtime.onUpdate { updates.add(it) }
            fake.push(initial.copy(cursor = 512), 512, full = true)
            assertEquals(512L, runtime.currentSnapshot!!.cursor)
            assertEquals(512L, updates.single().snapshot.cursor, "Full replacement must notify UI")
            assertTrue(updates.single().isFullSnapshot)
            fake.push(initial.copy(cursor = 900, syncEpoch = "retired-epoch"), 900, full = true)
            assertEquals("review-epoch", runtime.currentSnapshot!!.syncEpoch)
            assertEquals(512L, runtime.currentSnapshot!!.cursor)
            assertEquals(1, updates.size)
        }
    }

    @Test fun equalFromAndToCursorDoesNotInventFullReplacementMarker() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.push(initial.copy(cursor = 512), 512, full = false)
            assertEquals(0L, runtime.currentSnapshot!!.cursor)
        }
    }

    @Test fun delayedSnapshotResponseCannotRollBackNewerPush() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.snapshotReply = CompletableFuture()
            val query = runtime.snapshot()
            fake.push(initial.copy(cursor = 1), 0)
            fake.snapshotReply.complete(initial)
            assertEquals(1L, query.await().cursor)
            assertEquals(1L, runtime.currentSnapshot!!.cursor)
        }
    }

    @Test fun duplicateDisconnectCannotCreateParallelReconnectAttempts() = fixture { initial ->
        val fake = FakeConnection(initial)
        val time = ManualTime()
        var connects = 0
        ServerConnectionSession(fake.connection, connectionFactory = {
            connects++
            CompletableFuture<ServerConnection>()
        }, clock = time, scheduler = time).use { runtime ->
            runtime.createRoom().await()
            fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
            fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
            assertEquals(1, time.activeCount)
            time.next()
            assertEquals(1, connects)
            assertEquals(0, time.activeCount)
        }
    }

    @Test fun disconnectWhileExplicitLeaveIsPendingCannotReclaimSeat() = fixture { initial ->
        val fake = FakeConnection(initial)
        val time = ManualTime()
        var connects = 0
        ServerConnectionSession(fake.connection, connectionFactory = {
            connects++
            CompletableFuture<ServerConnection>()
        }, clock = time, scheduler = time).use { runtime ->
            runtime.createRoom().await()
            val leaving = runtime.leaveRoom()
            assertFalse(leaving.isDone)
            fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
            assertEquals(0, time.activeCount, "Explicit leave intent forbids automatic seat resumption")
            assertEquals(0, connects)
        }
    }

    @Test fun rebindingEndsOldRequestsBeforeTheirTransportResponds() = fixture { base ->
        val initial = base.copy(decision = base.decision.copy(
            options = listOf(ProtocolActionOption("pass", ProtocolLegalAction.Pass(0)))))
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.snapshotReply = CompletableFuture()
            val oldRead = runtime.snapshot()
            val choice = ActionChoice("old", initial.roundId, initial.decision.decisionId,
                initial.decision.controlEpoch, "pass")
            val oldReply = fake.actionReply
            val oldAction = runtime.submit(choice)
            fake.resumeBindingReply = CompletableFuture.completedFuture(FakeConnection.binding(
                initial.copy(syncEpoch = "new-binding-epoch")))
            runtime.resumeSession("review-room", "review-token").await()
            assertTrue(oldRead.isDone, "New binding must terminate old generation reads")
            assertTrue(oldAction.isDone, "New binding must terminate old generation actions")
            fake.actionReply = CompletableFuture()
            val newAction = runtime.submit(choice.copy(requestId = "new"))
            assertFalse(newAction.isDone)
            assertTrue(runtime.isPending)
            oldReply.complete(ProtocolActionReceipt("old", ProtocolReceiptStatus.APPLIED))
            assertTrue(runtime.isPending, "Late old receipt must not release new generation pending gate")
        }
    }

    @Test fun delayedDecisionCannotReturnOldOptionsAfterNewPushInSameControlEpoch() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.decisionReply = CompletableFuture()
            val decision = runtime.decision()
            val newer = initial.copy(cursor = 1,
                decision = initial.decision.copy(decisionId = "new-decision"),
                presentationPreview = initial.presentationPreview?.copy(decisionId = "new-decision"))
            fake.push(newer, 0)
            fake.decisionReply.complete(initial.decision)
            assertEquals("new-decision", decision.await().decisionId)
        }
    }

    @Test fun queuedQueryCompletionIsInvalidatedByCloseBeforeUiConsumesIt() = fixture { initial ->
        val fake = FakeConnection(initial)
        val queue = java.util.ArrayDeque<() -> Unit>()
        fun drain() { while (queue.isNotEmpty()) queue.removeFirst().invoke() }
        val runtime = ServerConnectionSession(fake.connection, dispatch = { queue.add(it) })
        val created = runtime.createRoom()
        drain()
        created.await()
        val query = runtime.snapshot()
        assertFalse(query.isDone)
        runtime.close()
        drain()
        assertTrue(query.isCompletedExceptionally || query.isCancelled,
            "A response queued for the old UI lifecycle must not complete successfully after close")
    }

    @Test fun queuedActiveStateCannotBePublishedAfterClose() = fixture { initial ->
        val fake = FakeConnection(initial)
        val queue = java.util.ArrayDeque<() -> Unit>()
        val runtime = ServerConnectionSession(fake.connection, dispatch = { queue.add(it) })
        val states = mutableListOf<ConnectionState>()
        runtime.onStateChange { states.add(it) }
        runtime.close()
        while (queue.isNotEmpty()) queue.removeFirst().invoke()
        assertEquals(listOf(ConnectionState.CLOSED), states)
    }

    @Test fun recoveryWithNoProgressCannotRecursivelyFloodRequestsOrUi() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.immediateResumeSnapshot = initial
            val updates = mutableListOf<Long>()
            runtime.onUpdate { updates.add(it.snapshot.cursor) }
            fake.push(initial.copy(cursor = 2), 1)
            assertEquals(1, fake.resumeReplies.size, "No-progress response must not recursively trigger recovery")
            assertTrue(updates.isEmpty(), "Unchanged baseline must not flood UI notifications")
            fake.push(initial.copy(cursor = 1), 0)
            assertEquals(listOf(1L, 2L), updates)
        }
    }

    @Test fun roomSubscriptionSurvivesReconnectAndRejectsOldTransportCallbacks() = fixture { initial ->
        val fake = FakeConnection(initial)
        val replacement = FakeConnection(initial.copy(syncEpoch = "room-reconnect-epoch"))
        val time = ManualTime()
        ServerConnectionSession(fake.connection,
            connectionFactory = { CompletableFuture.completedFuture(replacement.connection) },
            clock = time, scheduler = time).use { runtime ->
            runtime.createRoom().await()
            val received = mutableListOf<Boolean>()
            runtime.onRoomUpdate { received.add(it.members.first { m -> m.seat == 0 }.isReady) }.use {
                val oldCallbacks = fake.roomListeners.toList()
                fake.drop(ApiProtocol.HEARTBEAT_TIMEOUT)
                time.next()
                assertEquals(ConnectionState.CONNECTED, runtime.connectionState)
                assertTrue(replacement.roomListeners.isNotEmpty(), "Reconnect must restore room subscription")
                received.clear()
                oldCallbacks.forEach { it(fake.roomInfo) }
                assertTrue(received.isEmpty(), "Retired transport must not notify room UI")
                val readyInfo = replacement.roomInfo.copy(version = 2L, members = listOf(
                    ProtocolRoomMember(0, "Host", true, true, "HUMAN")))
                replacement.roomListeners.toList().forEach { it(readyInfo) }
                assertEquals(listOf(true), received)
            }
        }
    }

    @Test fun reorderedAndDuplicateRoomUpdatesCannotRollReadinessBackward() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            val versions = mutableListOf<Long>()
            runtime.onRoomUpdate { versions.add(it.version) }.use {
                versions.clear()
                val ready = fake.roomInfo.copy(version = 3L, members = listOf(
                    ProtocolRoomMember(0, "Host", true, true, "HUMAN")))
                fake.roomListeners.toList().forEach { it(ready) }
                fake.roomListeners.toList().forEach { it(fake.roomInfo.copy(version = 2L)) }
                fake.roomListeners.toList().forEach { it(ready) }
                assertEquals(listOf(3L), versions,
                    "Room UI must ignore older and duplicate authoritative versions")
            }
        }
    }

    @Test fun delayedRoomQueryReturnsLatestAlreadyObservedVersion() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            fake.roomInfoReply = CompletableFuture()
            val query = runtime.getRoomInfo()
            val ready = fake.roomInfo.copy(version = 2L, members = listOf(
                ProtocolRoomMember(0, "Host", true, true, "HUMAN")))
            fake.roomListeners.toList().forEach { it(ready) }
            fake.roomInfoReply.complete(fake.roomInfo)
            val result = query.await()
            assertEquals(2L, result.version, "A delayed query must not return a superseded room view")
            assertTrue(result.members.first { it.seat == 0 }.isReady)
        }
    }

    @Test fun retiredRoomCallbackCannotAttachToNewBindingWithSameRoomId() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            val retiredCallbacks = fake.roomListeners.toList()
            assertTrue(retiredCallbacks.isNotEmpty())
            fake.actionReply = CompletableFuture.completedFuture(
                ProtocolActionReceipt(requestId = "retire-room", status = ProtocolReceiptStatus.APPLIED))
            runtime.leaveRoom().await()
            runtime.createRoom().await()
            val versions = mutableListOf<Long>()
            runtime.onRoomUpdate { versions.add(it.version) }.use {
                retiredCallbacks.forEach { it(fake.roomInfo.copy(version = 90L)) }
                assertTrue(versions.isEmpty(), "Retired room subscription adopted the replacement binding epoch")
                fake.roomListeners.toList().forEach { it(fake.roomInfo.copy(version = 2L)) }
                assertEquals(listOf(2L), versions, "Current room subscription must remain active")
                assertEquals(2L, runtime.getRoomInfo().await().version,
                    "Retired room callback must not poison the replacement cache")
            }
        }
    }

    @Test fun roomCallbackAlreadyInFlightCannotPoisonReplacementCache() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            val oldCallback = fake.roomListeners.single()
            // Hold the cache commit lock so the callback validates its old binding, then pauses.
            val field = ServerConnectionSession::class.java.getDeclaredField("roomLock")
            field.isAccessible = true
            val cacheLock = field.get(runtime)
            val failure = java.util.concurrent.atomic.AtomicReference<Throwable?>()
            val worker = Thread({
                try { oldCallback(fake.roomInfo.copy(version = 90L)) }
                catch (t: Throwable) { failure.set(t) }
            }, "review-retired-room-callback").also { it.isDaemon = true }
            try {
                synchronized(cacheLock) {
                    worker.start()
                    val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
                    while (worker.state != Thread.State.BLOCKED && worker.isAlive && System.nanoTime() < deadline) {
                        Thread.yield()
                    }
                    assertEquals(Thread.State.BLOCKED, worker.state, "Callback never reached its cache commit")
                    fake.actionReply = CompletableFuture.completedFuture(
                        ProtocolActionReceipt(requestId = "retire-inflight", status = ProtocolReceiptStatus.APPLIED))
                    runtime.leaveRoom().await()
                    runtime.createRoom().await()
                }
            } finally {
                worker.join(2000)
            }
            assertFalse(worker.isAlive, "Retired callback failed to finish")
            failure.get()?.let { throw AssertionError("Callback failed unexpectedly", it) }
            fake.roomListeners.toList().forEach { it(fake.roomInfo.copy(version = 2L)) }
            assertEquals(2L, runtime.getRoomInfo().await().version,
                "A callback validated before leave committed into the replacement room cache")
        }
    }

    @Test fun successfulLeaveInvalidatesRoomReadsAndCapturedRoomCallbacks() = fixture { initial ->
        val fake = FakeConnection(initial)
        ServerConnectionSession(fake.connection).use { runtime ->
            runtime.createRoom().await()
            val received = mutableListOf<Long>()
            runtime.onRoomUpdate { received.add(it.version) }.use {
                received.clear()
                val callbacks = fake.roomListeners.toList()
                fake.roomInfoReply = CompletableFuture()
                val query = runtime.getRoomInfo()
                fake.actionReply = CompletableFuture.completedFuture(
                    ProtocolActionReceipt("leave", ProtocolReceiptStatus.APPLIED))
                assertEquals(com.majiang.client.api.ReceiptStatus.APPLIED, runtime.leaveRoom().await().status)
                assertTrue(query.isCompletedExceptionally || query.isCancelled,
                    "Leaving must terminate reads belonging to the retired room binding")
                fake.roomInfoReply.complete(fake.roomInfo)
                callbacks.forEach { it(fake.roomInfo.copy(version = 2L)) }
                assertTrue(received.isEmpty(), "Old room must not notify an unbound lobby")
            }
        }
    }

    @Test fun removingRoomObserverSuppressesAlreadyQueuedUiCallback() = fixture { initial ->
        val fake = FakeConnection(initial)
        val queue = java.util.ArrayDeque<() -> Unit>()
        fun drain() { while (queue.isNotEmpty()) queue.removeFirst().invoke() }
        val runtime = ServerConnectionSession(fake.connection, dispatch = { queue.add(it) })
        try {
            val created = runtime.createRoom()
            drain()
            created.await()
            val received = mutableListOf<String>()
            val observer = runtime.onRoomUpdate { received.add(it.roomId) }
            drain()
            received.clear()
            fake.roomListeners.toList().forEach { it(fake.roomInfo.copy(version = 2L)) }
            assertTrue(queue.isNotEmpty(), "A live observer update should be queued before unregister")
            observer.close()
            drain()
            assertTrue(received.isEmpty(), "Unregistered UI observer cannot receive queued room callbacks")
        } finally {
            runtime.close()
            drain()
        }
    }
}

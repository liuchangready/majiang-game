package com.majiang.v4lobby

import com.majiang.client.api.ServerConnectionSession
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.*
import java.lang.reflect.Proxy
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Runtime-only probes: the real client state machine with controlled transport completion order. */
@Timeout(10)
class V4SpectatorRuntimeIsolationTest {
    private fun snapshot(cursor: Long, stream: String = "public-stream") = ProtocolPublicSnapshot(
        sessionId = "public-session", sessionVersion = cursor, syncEpoch = stream, cursor = cursor,
        session = ProtocolPublicSessionSummary("WAITING_START", List(4) { 25000 }, 1, 0, 0, 0)
    )
    private fun update(cursor: Long, full: Boolean = false, stream: String = "public-stream") =
        ProtocolPublicUpdate(snapshot(cursor, stream), if (full) cursor else cursor - 1, emptyList(), full)
    private fun <T> CompletableFuture<T>.await(): T = get(2, TimeUnit.SECONDS)

    private inner class Transport {
        var closed = false
        val listeners = mutableListOf<(ProtocolPublicUpdate) -> Unit>()
        val recoveries = mutableListOf<CompletableFuture<ProtocolPublicSnapshot>>()
        var delayQueries = false
        val queries = mutableListOf<CompletableFuture<ProtocolPublicSnapshot>>()
        @Suppress("UNCHECKED_CAST")
        val connection = Proxy.newProxyInstance(ServerConnection::class.java.classLoader,
            arrayOf(ServerConnection::class.java)) { proxy, method, args ->
            when (method.name) {
                "getClosed" -> closed
                "getDisconnectReason" -> null
                "onPublicUpdate" -> {
                    val listener = args!![0] as (ProtocolPublicUpdate) -> Unit
                    listeners.add(listener)
                    AutoCloseable { listeners.remove(listener) }
                }
                "onUpdate", "onRoomUpdate", "onDisconnect" -> AutoCloseable { }
                "joinSpectator" -> {
                    val request = args!![0] as JoinSpectatorRequest
                    CompletableFuture.completedFuture(SpectatorBinding(request.roomId, "viewer", "token", snapshot(0)))
                }
                "leaveRoom" -> CompletableFuture.completedFuture(
                    ProtocolActionReceipt("leave", ProtocolReceiptStatus.APPLIED))
                "resumePublic" -> CompletableFuture<ProtocolPublicSnapshot>().also { recoveries.add(it) }
                "publicSnapshot" -> if (delayQueries) {
                    CompletableFuture<ProtocolPublicSnapshot>().also { queries.add(it) }
                } else CompletableFuture.completedFuture(snapshot(0))
                "close" -> { closed = true; null }
                "equals" -> proxy === args!![0]
                "hashCode" -> System.identityHashCode(proxy)
                "toString" -> "ControlledPublicTransport"
                else -> error("Unexpected transport call: ${method.name}")
            }
        } as ServerConnection
        fun emit(update: ProtocolPublicUpdate) { listeners.toList().forEach { it(update) } }
    }

    // As in the independent ROOM race test, acquire (never modify) the cache monitor
    // to hold an already validated callback immediately before its actual commit.
    private fun acrossRebinding(runtime: ServerConnectionSession, callback: () -> Unit, afterRebind: () -> Unit = {}) {
        val field = ServerConnectionSession::class.java.getDeclaredField("publicSyncLock")
        field.isAccessible = true
        val lock = field.get(runtime)
        val failure = java.util.concurrent.atomic.AtomicReference<Throwable?>()
        val worker = Thread({
            try { callback() } catch (t: Throwable) { failure.set(t) }
        }, "review-retired-public-commit").also { it.isDaemon = true }
        try {
            synchronized(lock) {
                worker.start()
                val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
                while (worker.state != Thread.State.BLOCKED && worker.isAlive && System.nanoTime() < deadline) Thread.yield()
                assertEquals(Thread.State.BLOCKED, worker.state, "Callback must reach its cache commit")
                runtime.leaveRoom().await()
                runtime.joinSpectator("room").await()
                afterRebind()
            }
        } finally {
            worker.join(2000)
        }
        assertFalse(worker.isAlive, "Retired PUBLIC callback must finish")
        failure.get()?.let { throw AssertionError("Unexpected callback failure", it) }
    }

    @Test fun publicPushValidatedBeforeLeaveCannotCommitIntoTheReplacementBinding() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            val callback = transport.listeners.single()
            acrossRebinding(runtime, { callback(update(1)) })
            assertEquals(0L, runtime.currentPublicSnapshot!!.cursor,
                "Source binding must be checked again under the commit lock")
        }
    }

    @Test fun recoveryValidatedBeforeLeaveCannotOverwriteNewRecoveryState() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            transport.emit(update(2))
            val old = transport.recoveries.single()
            acrossRebinding(runtime, { old.complete(snapshot(1)) }, { transport.emit(update(2)) })
            assertEquals(0L, runtime.currentPublicSnapshot!!.cursor,
                "Retired recovery must not advance or drain the replacement binding")
            transport.emit(update(3))
            assertEquals(2, transport.recoveries.size, "Old completion must not steal current recovery ownership")
            transport.recoveries.last().complete(snapshot(2))
            assertEquals(3L, runtime.currentPublicSnapshot!!.cursor)
        }
    }

    @Test fun snapshotQueryValidatedBeforeLeaveCannotPoisonReplacementCache() {
        val transport = Transport().also { it.delayQueries = true }
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            runtime.publicSnapshot()
            val reply = transport.queries.single()
            acrossRebinding(runtime, { reply.complete(snapshot(9)) })
            assertEquals(0L, runtime.currentPublicSnapshot!!.cursor)
        }
    }

    @Test fun explicitResumeValidatedBeforeLeaveCannotPoisonReplacementCache() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            runtime.resumePublic(com.majiang.client.api.PublicCheckpoint("public-session", "public-stream", 0))
            val reply = transport.recoveries.single()
            acrossRebinding(runtime, { reply.complete(snapshot(9)) })
            assertEquals(0L, runtime.currentPublicSnapshot!!.cursor)
        }
    }

    @Test fun repeatedFullBaselineNotifiesOnlyOnce() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            val cursors = mutableListOf<Long>()
            runtime.onPublicUpdate { cursors.add(it.snapshot.cursor) }.use {
                transport.emit(update(3, full = true))
                transport.emit(update(3, full = true))
                assertEquals(3L, runtime.currentPublicSnapshot!!.cursor)
                assertEquals(listOf(3L), cursors, "A retransmitted full baseline must not duplicate UI callbacks")
            }
        }
    }

    @Test fun queuedPublicCallbackCannotRunAfterSuccessfulLeave() {
        val transport = Transport()
        val queued = mutableListOf<() -> Unit>()
        fun drain() { while (queued.isNotEmpty()) queued.removeAt(0).invoke() }
        ServerConnectionSession(transport.connection, dispatch = { queued.add(it) }).use { runtime ->
            val joined = runtime.joinSpectator("room")
            drain()
            joined.await()
            val cursors = mutableListOf<Long>()
            runtime.onPublicUpdate { cursors.add(it.snapshot.cursor) }.use {
                transport.emit(update(1))
                val left = runtime.leaveRoom()
                // Complete leave first while the previously queued UI callback remains pending.
                assertTrue(queued.size >= 2)
                queued.removeAt(queued.lastIndex).invoke()
                assertEquals(com.majiang.client.api.ReceiptStatus.APPLIED, left.await().status)
                assertNull(runtime.currentPublicSnapshot)
                drain()
                assertTrue(cursors.isEmpty(), "A retired room must not notify its old PUBLIC update in the lobby")
            }
        }
        drain()
    }

    @Test fun successfulLeaveInsideFirstListenerInvalidatesTheRemainingCallbacks() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            val later = mutableListOf<Long>()
            runtime.onPublicUpdate { runtime.leaveRoom().await() }.use {
                runtime.onPublicUpdate { later.add(it.snapshot.cursor) }.use {
                    transport.emit(update(1))
                    assertNull(runtime.currentPublicSnapshot, "The first listener completed leave")
                    assertTrue(later.isEmpty(), "Callbacks still pending after successful leave are from an invalid room")
                }
            }
        }
    }

    @Test fun oldRecoveryCompletionCannotClearNewBindingsRecoveryOwnership() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            transport.emit(update(2))
            assertEquals(1, transport.recoveries.size)
            runtime.leaveRoom().await()
            runtime.joinSpectator("room").await()
            transport.emit(update(2))
            assertEquals(2, transport.recoveries.size)
            transport.recoveries[0].complete(snapshot(1))
            assertEquals(0L, runtime.currentPublicSnapshot!!.cursor)
            transport.emit(update(3))
            assertEquals(2, transport.recoveries.size, "Old completion must not release the new in-flight recovery")
            transport.recoveries[1].complete(snapshot(2))
            assertEquals(3L, runtime.currentPublicSnapshot!!.cursor)
        }
    }

    @Test fun foreignOrOlderFullPushCannotReplaceCurrentPublicStream() {
        val transport = Transport()
        ServerConnectionSession(transport.connection).use { runtime ->
            runtime.joinSpectator("room").await()
            val cursors = mutableListOf<Long>()
            runtime.onPublicUpdate { cursors.add(it.snapshot.cursor) }.use {
                transport.emit(update(4, full = true))
                transport.emit(update(2, full = true))
                transport.emit(update(8, full = true, stream = "foreign-stream"))
                assertEquals(listOf(4L), cursors)
                assertEquals("public-stream", runtime.currentPublicSnapshot!!.syncEpoch)
                assertEquals(4L, runtime.currentPublicSnapshot!!.cursor)
            }
        }
    }
}

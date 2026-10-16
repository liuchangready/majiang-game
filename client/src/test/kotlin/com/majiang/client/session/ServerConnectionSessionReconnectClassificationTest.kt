package com.majiang.client.session

import com.majiang.client.api.ConnectionState
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMeld
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolRoomMember
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolTile
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.common.Cancellable
import com.majiang.transport.common.TransportClock
import com.majiang.transport.common.TransportScheduler
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CompletionException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutionException
import java.util.concurrent.atomic.AtomicInteger

/**
 * 客户端连接会话重连异常分类与有界退避集成测试 (MR-02 第二批 / T5)
 */
class ServerConnectionSessionReconnectClassificationTest {

    private class LocalFakeClock(var now: Long = 1000L) : TransportClock {
        override fun currentTimeMillis(): Long = now
    }

    private class LocalFakeScheduler(private val clock: LocalFakeClock) : TransportScheduler {
        data class ScheduledTask(val triggerTime: Long, val delayMillis: Long, val task: Runnable, var cancelled: Boolean = false)
        val tasks = mutableListOf<ScheduledTask>()

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val entry = ScheduledTask(clock.now + delayMillis, delayMillis, task)
            tasks.add(entry)
            return Cancellable { entry.cancelled = true }
        }

        fun advanceTime(deltaMillis: Long) {
            clock.now += deltaMillis
            while (true) {
                val nextTask = tasks
                    .filter { !it.cancelled && it.triggerTime <= clock.now }
                    .minByOrNull { it.triggerTime } ?: break
                tasks.remove(nextTask)
                if (!nextTask.cancelled) {
                    nextTask.task.run()
                }
            }
        }
    }

    private open class LocalFakeServerConnection(
        var resumeSessionHook: (ResumeRequest) -> CompletableFuture<RoomBinding> = { req ->
            CompletableFuture.completedFuture(
                RoomBinding(
                    roomId = req.roomId,
                    seat = 0,
                    resumeToken = req.token,
                    snapshot = createDefaultSnapshot()
                )
            )
        }
    ) : ServerConnection {
        var closedState: Boolean = false
        var disconnectReasonState: String? = null

        val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()
        val updateListeners = CopyOnWriteArrayList<(ProtocolPlayerUpdate) -> Unit>()
        val roomListeners = CopyOnWriteArrayList<(ProtocolRoomInfo) -> Unit>()
        val publicListeners = CopyOnWriteArrayList<(ProtocolPublicUpdate) -> Unit>()

        override val closed: Boolean get() = closedState
        override val disconnectReason: String? get() = disconnectReasonState

        override fun close() {
            closedState = true
        }

        fun emitDisconnect(reason: String) {
            disconnectReasonState = reason
            for (l in disconnectListeners) {
                l(reason)
            }
        }

        override fun onDisconnect(listener: (String) -> Unit): AutoCloseable {
            disconnectListeners.add(listener)
            return AutoCloseable { disconnectListeners.remove(listener) }
        }

        override fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable {
            updateListeners.add(listener)
            return AutoCloseable { updateListeners.remove(listener) }
        }

        override fun onRoomUpdate(listener: (ProtocolRoomInfo) -> Unit): AutoCloseable {
            roomListeners.add(listener)
            return AutoCloseable { roomListeners.remove(listener) }
        }

        override fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable {
            publicListeners.add(listener)
            return AutoCloseable { publicListeners.remove(listener) }
        }

        override fun createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding> =
            CompletableFuture.completedFuture(
                RoomBinding(
                    roomId = "ROOM_TEST_1",
                    seat = 0,
                    resumeToken = "token_host",
                    snapshot = createDefaultSnapshot()
                )
            )

        override fun resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding> =
            resumeSessionHook(req)

        override fun joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding> =
            CompletableFuture.failedFuture(UnsupportedOperationException())

        override fun leaveRoom(): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
            )

        override fun listRooms(): CompletableFuture<List<RoomSummary>> =
            CompletableFuture.completedFuture(emptyList())

        override fun getRoomInfo(): CompletableFuture<ProtocolRoomInfo> =
            CompletableFuture.completedFuture(
                ProtocolRoomInfo(
                    roomId = "ROOM_TEST_1",
                    lifecycle = ProtocolRoomLifecycle.PLAYING,
                    version = 1L,
                    members = listOf(ProtocolRoomMember(0, "Host", true, true, "HUMAN"))
                )
            )

        override fun setReady(ready: Boolean): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("setReady", ProtocolReceiptStatus.APPLIED))

        override fun updateConfig(config: ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.APPLIED))

        override fun capabilities(): CompletableFuture<ProtocolAccessCapabilities> =
            CompletableFuture.completedFuture(ProtocolAccessCapabilities())

        override fun snapshot(): CompletableFuture<ProtocolPlayerSnapshot> =
            CompletableFuture.completedFuture(createDefaultSnapshot())

        override fun decision(): CompletableFuture<ProtocolPlayerDecision> =
            CompletableFuture.completedFuture(createDefaultSnapshot().decision)

        override fun submit(choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(choice.requestId, ProtocolReceiptStatus.APPLIED))

        override fun submitShufflePlan(plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(plan.requestId, ProtocolReceiptStatus.APPLIED))

        override fun submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED))

        override fun submitSleeveBloom(request: ProtocolSleeveBloomRequest): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED))

        override fun submitSleeveBloomChoice(request: ProtocolSleeveBloomChoiceRequest): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED))

        override fun submitBeamSwap(request: ProtocolBeamSwapRequest): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED))

        override fun submitBeamSwapChoice(request: ProtocolBeamSwapChoiceRequest): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.APPLIED))

        override fun start(): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("start", ProtocolReceiptStatus.APPLIED))

        override fun nextRound(): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("nextRound", ProtocolReceiptStatus.APPLIED))

        override fun resume(cursor: Long): CompletableFuture<ProtocolPlayerSnapshot> =
            CompletableFuture.completedFuture(createDefaultSnapshot())

        override fun claimControl(): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("claimControl", ProtocolReceiptStatus.APPLIED))

        override fun releaseControl(): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("releaseControl", ProtocolReceiptStatus.APPLIED))

        override fun queryRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> =
            CompletableFuture.completedFuture(emptyList())

        override fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding> =
            CompletableFuture.failedFuture(UnsupportedOperationException())

        override fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding> =
            CompletableFuture.failedFuture(UnsupportedOperationException())

        override fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot> =
            CompletableFuture.failedFuture(UnsupportedOperationException())

        override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot> =
            CompletableFuture.failedFuture(UnsupportedOperationException())

        override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> =
            CompletableFuture.completedFuture(emptyList())

        companion object {
            fun createDefaultSnapshot(): ProtocolPlayerSnapshot {
                val tile = ProtocolTile.fromTileId(0)
                val view = ProtocolPlayerView(
                    viewerSeat = 0,
                    phase = ProtocolGamePhase.DISCARD,
                    roundWind = 1,
                    roundNumber = 1,
                    honbaSticks = 0,
                    riichiSticks = 0,
                    dealerSeat = 0,
                    currentTurnPlayer = 0,
                    wallRemaining = 70,
                    doraIndicators = listOf(tile),
                    scores = listOf(25000, 25000, 25000, 25000),
                    riichiStatus = listOf(false, false, false, false),
                    discards = listOf(emptyList<ProtocolTile>(), emptyList<ProtocolTile>(), emptyList<ProtocolTile>(), emptyList<ProtocolTile>()),
                    melds = listOf(emptyList<ProtocolMeld>(), emptyList<ProtocolMeld>(), emptyList<ProtocolMeld>(), emptyList<ProtocolMeld>()),
                    handTileCounts = listOf(14, 13, 13, 13),
                    myClosedTiles = listOf(tile),
                    myDrawnTile = null,
                    legalActions = listOf(ProtocolLegalAction.DiscardTile(0, tile))
                )
                val decision = ProtocolPlayerDecision(
                    roundId = "round-1",
                    decisionId = "dec-1",
                    controlEpoch = 1L,
                    options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, tile)))
                )
                return ProtocolPlayerSnapshot(
                    sessionId = "session-1",
                    roundId = "round-1",
                    syncEpoch = "sync-1",
                    cursor = 10L,
                    sessionVersion = 1L,
                    roundVersion = 1L,
                    view = view,
                    decision = decision
                )
            }
        }
    }

    @Test
    fun testTransientClosedTextInIoExceptionRetriesWithoutTerminating() {
        val clock = LocalFakeClock(1000L)
        val scheduler = LocalFakeScheduler(clock)
        val initialConn = LocalFakeServerConnection()

        var factoryAttempts = 0
        val factory = {
            factoryAttempts++
            val conn = LocalFakeServerConnection(
                resumeSessionHook = {
                    val f = CompletableFuture<RoomBinding>()
                    // 模拟网络层包含 "CLOSED" 字样的 IOException
                    f.completeExceptionally(IOException("Connection closed prematurely by peer"))
                    f
                }
            )
            CompletableFuture.completedFuture(conn as ServerConnection)
        }

        val session = ServerConnectionSession(
            initialConnection = initialConn,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )
        try {
            session.createRoom("Host", null).join()
            assertEquals(ConnectionState.CONNECTED, session.connectionState)

            // 触发意外掉线
            initialConn.emitDisconnect("NETWORK_LOST")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)

            // 执行第 1 次重连 (attempt 0)
            assertEquals(1, scheduler.tasks.size)
            scheduler.advanceTime(500L)

            // 恢复尝试返回 IOException("Connection closed...")
            // 契约断言：会话不得因文案含 CLOSED 永久终止，必须进入下一轮有界退避重试！
            assertEquals(1, factoryAttempts)
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)
            assertFalse(session.closed)
            assertEquals(1, scheduler.tasks.size) // 下一轮重试已调度

            // 再推进一步 (attempt 1: delay = 1000ms)
            scheduler.advanceTime(1000L)
            assertEquals(2, factoryAttempts)
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)
            assertFalse(session.closed)
        } finally {
            session.close()
        }
    }

    @Test
    fun testTypedInvalidTokenTerminatesImmediatelyWithoutRetrying() {
        val clock = LocalFakeClock(1000L)
        val scheduler = LocalFakeScheduler(clock)
        val initialConn = LocalFakeServerConnection()

        var factoryAttempts = 0
        val factory = {
            factoryAttempts++
            val conn = LocalFakeServerConnection(
                resumeSessionHook = {
                    val f = CompletableFuture<RoomBinding>()
                    // 服务端返回明确结构化 INVALID_TOKEN 业务失败
                    f.completeExceptionally(ServerApiException(code = ApiErrors.INVALID_TOKEN, message = "INVALID_RESUME_TOKEN"))
                    f
                }
            )
            CompletableFuture.completedFuture(conn as ServerConnection)
        }

        val session = ServerConnectionSession(
            initialConnection = initialConn,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )
        try {
            session.createRoom("Host", null).join()

            initialConn.emitDisconnect("HEARTBEAT_TIMEOUT")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)

            // 执行 attempt 0
            scheduler.advanceTime(500L)

            // 契约断言：遇到永久业务错误，必须立即终结会话，不再安排退避任务
            assertEquals(1, factoryAttempts)
            assertEquals(ConnectionState.CLOSED, session.connectionState)
            assertTrue(session.closed)
            assertEquals(ApiErrors.INVALID_TOKEN, session.disconnectReason)
            assertEquals(0, scheduler.tasks.size, "No further retry should be scheduled")
        } finally {
            session.close()
        }
    }

    @Test
    fun testWrappedPermanentExceptionIsUnwrappedAndTerminatesSession() {
        val clock = LocalFakeClock(1000L)
        val scheduler = LocalFakeScheduler(clock)
        val initialConn = LocalFakeServerConnection()

        val factory = {
            val conn = LocalFakeServerConnection(
                resumeSessionHook = {
                    val f = CompletableFuture<RoomBinding>()
                    // 多层包装异常
                    val root = ServerApiException(code = ApiErrors.ROOM_NOT_FOUND, message = "ROOM_CLOSED")
                    f.completeExceptionally(CompletionException(ExecutionException(root)))
                    f
                }
            )
            CompletableFuture.completedFuture(conn as ServerConnection)
        }

        val session = ServerConnectionSession(
            initialConnection = initialConn,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )
        try {
            session.createRoom("Host", null).join()

            initialConn.emitDisconnect("SOCKET_EOF")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)

            // 执行 attempt 0
            scheduler.advanceTime(500L)

            assertEquals(ConnectionState.CLOSED, session.connectionState)
            assertEquals(ApiErrors.ROOM_NOT_FOUND, session.disconnectReason)
            assertEquals(0, scheduler.tasks.size)
        } finally {
            session.close()
        }
    }

    @Test
    fun testUnknownBusinessErrorRetriesWithBoundedExponentialBackoffUpToCap() {
        val clock = LocalFakeClock(1000L)
        val scheduler = LocalFakeScheduler(clock)
        val initialConn = LocalFakeServerConnection()

        val createdConnections = mutableListOf<LocalFakeServerConnection>()
        val scheduledDelays = mutableListOf<Long>()
        val expectedDelays = listOf(500L, 1000L, 2000L, 4000L, 8000L, 8000L, 8000L, 8000L, 8000L, 8000L)

        val factory = {
            val conn = LocalFakeServerConnection(
                resumeSessionHook = {
                    val f = CompletableFuture<RoomBinding>()
                    // 未知或可重试业务错误 SERVER_BUSY
                    f.completeExceptionally(ServerApiException(code = ApiErrors.SERVER_BUSY, message = "Server busy"))
                    f
                }
            )
            createdConnections.add(conn)
            CompletableFuture.completedFuture(conn as ServerConnection)
        }

        val session = ServerConnectionSession(
            initialConnection = initialConn,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )
        try {
            session.createRoom("Host", null).join()

            initialConn.emitDisconnect("CONN_DROP")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)

            // 循环推进调度器，验证前 10 次重试均保持 RECONNECTING 并使用有界指数退避 (封顶 8000ms)
            for (i in 0 until 10) {
                assertEquals(ConnectionState.RECONNECTING, session.connectionState, "Attempt $i must be RECONNECTING")
                assertEquals(1, scheduler.tasks.size, "Task must be scheduled for attempt $i")
                val task = scheduler.tasks[0]
                scheduledDelays.add(task.delayMillis)
                clock.now = task.triggerTime
                scheduler.advanceTime(0L)
            }

            // 验证 10 次退避延迟完全符合 500ms 起步、每次翻倍、封顶 8000ms
            assertEquals(expectedDelays, scheduledDelays, "Delays must match exponential backoff capped at 8000ms")
            assertEquals(10, createdConnections.size, "Exactly 10 connections must have been attempted")

            // 验证所有失败的替换连接均已被安全关闭释放
            assertTrue(createdConnections.all { it.closed }, "All failed replacement connections must be closed")

            // 超过最大上限 (attempt >= 10) 后进入终结，终止原因为契约规定的 RECONNECT_ATTEMPTS_EXHAUSTED
            assertEquals(ConnectionState.CLOSED, session.connectionState)
            assertTrue(session.closed)
            assertEquals("RECONNECT_ATTEMPTS_EXHAUSTED", session.disconnectReason)
            assertEquals(0, scheduler.tasks.size, "All retry tasks must cease after exhaustion")
        } finally {
            session.close()
        }
    }
}

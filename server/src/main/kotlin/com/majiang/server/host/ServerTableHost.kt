package com.majiang.server.host

import com.majiang.access.ActionReceipt
import com.majiang.access.ClockView
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.access.deepCopy
import com.majiang.ai.AIStrategy
import com.majiang.ai.BasicAIStrategy
import com.majiang.application.ActionChoice
import com.majiang.application.PlayerDecision
import com.majiang.application.deepCopy
import com.majiang.authority.TableAuthority
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.server.control.ConnectionControl
import com.majiang.server.control.ControllerRole
import com.majiang.server.control.DisconnectPolicy
import com.majiang.event.GameEvent
import com.majiang.host.ai.TsumogiriAIStrategy
import com.majiang.match.MatchConfig
import com.majiang.session.SessionUpdate
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.state.ActionResult
import com.majiang.state.GameAction
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.mapping.PublicProtocolMapper
import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock
import com.majiang.timing.TimeControlConfig
import com.majiang.transport.api.ProtocolClock
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import java.util.ArrayDeque
import java.util.ArrayList
import java.util.Collections
import java.util.LinkedHashMap
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadFactory
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * 服务端权威单桌异步宿主 (ServerTableHost)
 *
 * 遵循 V4 架构契约与 TASK-ANTI-R4A 规范：
 * 1. 围绕纯领域同步 [TableAuthority] 包装，提供每桌独立的串行执行队列 (serialExecutor，支持依赖注入)；
 * 2. 外部调用（本地进程内、网络 UDP、测试）只入队并立即返回 Future，绝不在调用方或接收循环线程上加持领域锁；
 * 3. 权威计算与下游外部 Future 回调解耦：在独立 completion 线程池完成 Future，防止慢回执消费者/网络发送阻塞权威队列；
 * 4. 拥有网络连接生命周期 (ConnectionControl)、席位投影游标与历史缓存、订阅派发解耦；
 * 5. 监听器通知派发 (seatDispatchers) 移出权威突变临界区，单席位单调有序推进；慢监听器绝不阻塞后续查询或命令入队；
 * 6. 拥有后台 AI 线程池，在突变队列之外推理并在提交时二次验证 roundId、decisionId 与 controlEpoch；
 * 7. 权威超时事实具备优先裁决权，不受展示忙 (presentationBusy) 或玩家操作饥饿策略推迟；
 * 8. 确定性生命周期：close 立即拒绝并完成所有排队中的 Future，零挂起，零调用方阻塞等待。
 */
class ServerTableHost @JvmOverloads constructor(
    config: MatchConfig = MatchConfig(),
    val timeControl: TimeControlConfig = TimeControlConfig(),
    val clock: MonotonicClock = SystemMonotonicClock,
    controllers: List<SeatController> = listOf(
        SeatController.LocalHuman(),
        SeatController.Computer(),
        SeatController.Computer(),
        SeatController.Computer()
    ),
    initialState: GameState? = null,
    val disconnectPolicy: DisconnectPolicy = DisconnectPolicy(),
    val adminSeat: Int = 0,
    aiProfiles: Map<String, () -> AIStrategy> = emptyMap(),
    aiExecutor: ExecutorService? = null,
    val rhythmIntervalMillis: Long = 150L,
    serialExecutor: ExecutorService? = null
) : AutoCloseable {

    private val tableId: String = UUID.randomUUID().toString()
    private val isClosed = AtomicBoolean(false)

    val closed: Boolean get() = isClosed.get()

    val initialControllers: List<SeatController> = Collections.unmodifiableList(controllers.toList())

    private val strategyRegistry = mutableMapOf<String, () -> AIStrategy>(
        "basic" to { BasicAIStrategy() },
        "tsumogiri" to { TsumogiriAIStrategy() }
    )

    init {
        for ((name, factory) in aiProfiles) {
            strategyRegistry[name.lowercase()] = factory
        }
        resolveAiStrategy(disconnectPolicy.aiProfileId)
    }

    fun resolveAiStrategy(profileId: String): AIStrategy {
        val factory = strategyRegistry[profileId.lowercase()]
            ?: throw IllegalArgumentException("Unknown AI profile: $profileId. Supported: ${strategyRegistry.keys}")
        return factory()
    }

    // 纯领域权威入口
    val authority: TableAuthority = TableAuthority(
        config = config,
        timeControl = timeControl,
        clock = clock,
        controllers = controllers,
        initialState = initialState,
        adminSeat = adminSeat
    )

    // 连接与控制权控制器
    val connectionControl: ConnectionControl = ConnectionControl(
        policy = disconnectPolicy,
        clock = clock,
        seatCount = 4,
        initialRoles = (0 until 4).associateWith { seat ->
            when (controllers.getOrNull(seat)) {
                is SeatController.Computer -> ControllerRole.COMPUTER
                is SeatController.Takeover -> ControllerRole.TAKEOVER
                else -> ControllerRole.HUMAN
            }
        }
    )

    // 每桌专属单线程串行执行器（可注入，便于确定性并发与关闭测试）
    private val ownsSerialExecutor: Boolean = (serialExecutor == null)
    private val serialExecutor: ExecutorService = serialExecutor ?: Executors.newSingleThreadExecutor(
        ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Serial").apply { isDaemon = true } }
    )

    // 外部 Future 完成解耦执行器：将领域计算与下游回调（如 UDP 发包、UI 回调）隔离
    internal val completionExecutor: ExecutorService = Executors.newCachedThreadPool(
        ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Completion").apply { isDaemon = true } }
    )

    // 权威状态提交回调（在 serialExecutor 队列上触发，供 SharedRoom 同步生命周期）
    internal var onStateCommitted: (() -> Unit)? = null

    // 跟踪所有未决的外部 Future，以便 close 时确定性拒绝并完成
    private val pendingFutures = ConcurrentHashMap.newKeySet<CompletableFuture<*>>()

    // 每席位独立的单线程派发器（保证按席位单调有序派发，且外部慢监听器完全不阻塞 serialExecutor）
    private val seatDispatchers: Array<ExecutorService> = Array(4) { s ->
        Executors.newSingleThreadExecutor(
            ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Seat-$s-Dispatcher").apply { isDaemon = true } }
        )
    }

    // 席位订阅凭证
    class SeatSubscription(val id: Long, val listener: (PlayerUpdate) -> Unit) : AutoCloseable {
        @Volatile var active: Boolean = true
        override fun close() {
            active = false
        }
    }

    // 公开流订阅凭证
    class PublicSubscription(val id: Long, val listener: (ProtocolPublicUpdate) -> Unit) : AutoCloseable {
        @Volatile var active: Boolean = true
        override fun close() {
            active = false
        }
    }

    private val subscriptionIdCounter = AtomicLong(0L)

    // 座位事件流与游标
    private class SeatStream(
        val seat: Int,
        var syncEpoch: String,
        var cursor: Long = 0L
    ) {
        val history: ArrayDeque<PlayerUpdate> = ArrayDeque()
        val subscriptions: CopyOnWriteArrayList<SeatSubscription> = CopyOnWriteArrayList()
        var lastEmittedSnapshot: PlayerSnapshot? = null

        fun addHistory(update: PlayerUpdate) {
            if (history.size >= 256) {
                history.removeFirst()
            }
            history.addLast(update)
        }

        fun clearHistory() {
            history.clear()
        }
    }

    // 独立公开事件流与游标 (最多保留 256 条历史)
    private class PublicStream(
        var syncEpoch: String,
        var cursor: Long = 0L
    ) {
        val history: ArrayDeque<ProtocolPublicUpdate> = ArrayDeque()
        val subscriptions: CopyOnWriteArrayList<PublicSubscription> = CopyOnWriteArrayList()
        var lastEmittedSnapshot: ProtocolPublicSnapshot? = null

        fun addHistory(update: ProtocolPublicUpdate) {
            if (history.size >= 256) {
                history.removeFirst()
            }
            history.addLast(update)
        }

        fun clearHistory() {
            history.clear()
        }
    }

    private val seatStreams: Array<SeatStream> = Array(4) { seat ->
        SeatStream(seat = seat, syncEpoch = "${authority.sessionId}-$seat")
    }

    private val publicStream: PublicStream = PublicStream(
        syncEpoch = "${authority.sessionId}-public"
    )

    // 独立单线程公开流派发器（保证单调有序派发，且慢消费者完全不阻塞 serialExecutor）
    private val publicDispatcher: ExecutorService = Executors.newSingleThreadExecutor(
        ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Public-Dispatcher").apply { isDaemon = true } }
    )

    // AI 后台计算线程池
    private val ownsAiExecutor: Boolean = (aiExecutor == null)
    private val internalAiExecutor: ExecutorService = aiExecutor ?: ThreadPoolExecutor(
        2, 4,
        60L, TimeUnit.SECONDS,
        ArrayBlockingQueue(32),
        ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-AI").apply { isDaemon = true } },
        ThreadPoolExecutor.DiscardPolicy()
    )

    private data class AiTaskKey(val seat: Int, val roundId: String, val decisionId: String, val epoch: Long)
    private val dispatchedAiTasks = ConcurrentHashMap.newKeySet<AiTaskKey>()

    private data class AiResult(
        val seat: Int,
        val roundId: String,
        val decisionId: String,
        val epoch: Long,
        val action: GameAction?
    )
    private val pendingAiResults = ConcurrentLinkedQueue<AiResult>()

    private var lastActionTimeMillis: Long = clock.nowMillis()
    private val readySeats = Collections.synchronizedSet(mutableSetOf<Int>())

    private sealed class HostReceiptKey {
        data class Action(val roundId: String, val seat: Int, val requestId: String) : HostReceiptKey()
        data class Shuffle(val roundId: String, val seat: Int, val requestId: String) : HostReceiptKey()
        data class Mark(val roundId: String, val seat: Int, val requestId: String) : HostReceiptKey()
    }

    private val maxIssuedReceipts = 1000
    private val issuedReceipts: MutableMap<HostReceiptKey, ActionReceipt> = Collections.synchronizedMap(
        object : LinkedHashMap<HostReceiptKey, ActionReceipt>(128, 0.75f, true) {
            override fun removeEldestEntry(eldest: MutableMap.MutableEntry<HostReceiptKey, ActionReceipt>?): Boolean {
                return size > maxIssuedReceipts
            }
        }
    )

    val currentRoundId: String? get() = authority.currentRoundId
    val isPaused: Boolean get() = authority.isPaused

    init {
        val hadActive = authority.initializeInitialClocks()
        // 同步建立初始快照基线 (cursor = 0)，不向 serialExecutor 提交阻塞式任务，支持注入暂停执行器
        for (s in 0 until 4) {
            val controls = buildSeatControlViews()
            val snap = authority.querySnapshot(s, seatStreams[s].syncEpoch, 0L, controls)
            seatStreams[s].lastEmittedSnapshot = snap
            seatStreams[s].cursor = 0L
        }
        val initialPublicProj = authority.queryPublicProjection()
        val initialPublicSnap = PublicProtocolMapper.toProtocolPublicSnapshot(initialPublicProj, publicStream.syncEpoch, 0L)
        publicStream.lastEmittedSnapshot = initialPublicSnap
        publicStream.cursor = 0L
        // 构造完成后才在 serialExecutor 队列派发异步 AI 决策任务，避免工作线程抢跑未完全初始化的基线与字段
        if (hadActive) {
            try {
                this.serialExecutor.execute {
                    if (!closed) {
                        syncDecisionsAndClocks()
                    }
                }
            } catch (_: RejectedExecutionException) {}
        }
    }

    /**
     * 核心串行入队管道：
     * 1. 提交至 serialExecutor 串行执行；
     * 2. 计算完成后由 completionExecutor 异步驱动外部 Future 完成；
     * 3. 跟踪 pendingFutures，close 时确保全部被通知，无挂起与内存泄露。
     */
    internal fun <T> enqueueOperation(block: () -> T): CompletableFuture<T> {
        val future = CompletableFuture<T>()
        if (closed) {
            future.completeExceptionally(IllegalStateException("ServerTableHost is closed"))
            return future
        }
        pendingFutures.add(future)
        future.whenComplete { _, _ ->
            pendingFutures.remove(future)
        }
        try {
            serialExecutor.execute {
                if (closed) {
                    try {
                        completionExecutor.execute {
                            future.completeExceptionally(IllegalStateException("ServerTableHost is closed"))
                        }
                    } catch (_: RejectedExecutionException) {
                        future.completeExceptionally(IllegalStateException("ServerTableHost is closed"))
                    }
                    return@execute
                }
                val result = try {
                    block()
                } catch (t: Throwable) {
                    try {
                        completionExecutor.execute {
                            future.completeExceptionally(t)
                        }
                    } catch (_: RejectedExecutionException) {
                        future.completeExceptionally(t)
                    }
                    return@execute
                }
                if (closed) {
                    try {
                        completionExecutor.execute {
                            future.completeExceptionally(IllegalStateException("ServerTableHost is closed"))
                        }
                    } catch (_: RejectedExecutionException) {
                        future.completeExceptionally(IllegalStateException("ServerTableHost is closed"))
                    }
                    return@execute
                }
                try {
                    completionExecutor.execute {
                        future.complete(result)
                    }
                } catch (_: RejectedExecutionException) {
                    future.complete(result)
                }
            }
        } catch (e: RejectedExecutionException) {
            future.completeExceptionally(e)
        }
        return future
    }

    // ========================================
    // 队列内原子原语（只能在 serialExecutor 内执行）
    // ========================================

    internal fun performStartOnQueue(seat: Int): ActionReceipt {
        val res = authority.start(seat, clock.nowMillis())
        if (res.stateChanged) {
            syncDecisionsAndClocks()
            publishStateChanges(res.events)
            lastActionTimeMillis = clock.nowMillis()
        }
        return res.receipt.copy(cursor = seatStreams[seat].cursor)
    }

    internal fun performAssignComputerSeatOnQueue(seat: Int, profileId: String? = null) {
        if (seat !in 0..3) return
        val profile = profileId ?: disconnectPolicy.aiProfileId
        val strategy = resolveAiStrategy(profile)
        connectionControl.setRole(seat, ControllerRole.COMPUTER, profile)
        authority.setSeatController(seat, SeatController.Computer(strategy))
    }

    internal fun performResetSeatControllerOnQueue(seat: Int) {
        if (seat !in 0..3) return
        connectionControl.setRole(seat, ControllerRole.HUMAN, null)
        authority.setSeatController(seat, SeatController.LocalHuman())
    }

    internal fun performReconnectOnQueue(seat: Int, reclaim: Boolean = false) {
        if (seat !in 0..3) return
        val wasRole = connectionControl.getRole(seat)
        val state = connectionControl.reconnect(seat, clock.nowMillis(), reclaim = reclaim)
        if (state.role != wasRole && state.role == ControllerRole.HUMAN) {
            authority.setSeatController(seat, SeatController.LocalHuman())
            syncDecisionsAndClocks()
            publishStateChanges(emptyList())
        } else {
            publishStateChanges(emptyList())
        }
    }

    internal fun performUpdateConfigOnQueue(
        newConfig: MatchConfig,
        newTimeControl: TimeControlConfig = this.timeControl
    ): Boolean {
        val applied = authority.updateLobbyConfig(newConfig, newTimeControl)
        if (applied) {
            publishStateChanges(emptyList())
        }
        return applied
    }

    internal fun performDisconnectOnQueue(seat: Int) {
        if (seat in 0..3) {
            val wasRole = connectionControl.getRole(seat)
            val wasDisconnected = connectionControl.seatState(seat).isDisconnected
            val state = connectionControl.disconnect(seat, clock.nowMillis())
            if (state.role != wasRole && state.role == ControllerRole.TAKEOVER) {
                val profile = state.aiProfileId ?: disconnectPolicy.aiProfileId
                val strategy = resolveAiStrategy(profile)
                authority.setSeatController(seat, SeatController.Takeover(strategy))
                syncDecisionsAndClocks()
                publishStateChanges(emptyList())
            } else if (!wasDisconnected) {
                publishStateChanges(emptyList())
            }
        }
    }

    internal fun cursorForSeat(seat: Int): Long =
        if (seat in 0..3) seatStreams[seat].cursor else 0L

    internal fun performQuerySnapshotOnQueue(seat: Int): PlayerSnapshot {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        val stream = seatStreams[seat]
        return authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews()).deepCopy()
    }

    // ========================================
    // 异步公开接口（调用线程零阻塞立即返回 Future）
    // ========================================

    fun start(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt("start", ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else {
                performStartOnQueue(seat)
            }
        }
    }

    fun nextRound(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (seat !in 0..3) {
                ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "INVALID_SEAT")
            } else {
                val preError = authority.validateNextRoundPreconditions()
                if (preError != null) {
                    preError.copy(cursor = seatStreams[seat].cursor)
                } else {
                    readySeats.add(seat)
                    val humanSeats = (0 until 4).filter { connectionControl.isHuman(it) }
                    val allReady = humanSeats.isEmpty() || humanSeats.all { readySeats.contains(it) }
                    if (allReady) {
                        readySeats.clear()
                        val res = authority.nextRound(seat, clock.nowMillis())
                        if (res.stateChanged) {
                            syncDecisionsAndClocks()
                            publishStateChanges(res.events)
                            lastActionTimeMillis = clock.nowMillis()
                        }
                        res.receipt.copy(cursor = seatStreams[seat].cursor)
                    } else {
                        ActionReceipt("nextRound", ReceiptStatus.RECORDED, cursor = seatStreams[seat].cursor)
                    }
                }
            }
        }
    }

    fun submitChoice(
        seat: Int,
        choice: ActionChoice,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (!connectionControl.isHuman(seat)) {
                ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "SEAT_NOT_HUMAN")
            } else {
                val res = authority.submitChoice(seat, choice, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.Action(choice.roundId ?: authority.currentRoundId ?: "", seat, choice.requestId)
                if (!res.stateChanged && res.receipt.status != ReceiptStatus.REJECTED) {
                    issuedReceipts[key] ?: res.receipt.copy(cursor = seatStreams[seat].cursor).also {
                        issuedReceipts[key] = it
                    }
                } else {
                    val r = res.receipt.copy(cursor = seatStreams[seat].cursor)
                    if (r.status != ReceiptStatus.REJECTED) {
                        issuedReceipts[key] = r
                    }
                    r
                }
            }
        }
    }

    fun submitShufflePlan(
        seat: Int,
        request: ShufflePlanRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (connectionControl.seatState(seat).isDisconnected) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SEAT_DISCONNECTED")
            } else if (!connectionControl.isHuman(seat)) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SEAT_NOT_HUMAN")
            } else {
                val res = authority.submitShufflePlan(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    publishStateChanges(emptyList())
                }
                val key = HostReceiptKey.Shuffle(request.roundId, seat, request.requestId)
                if (!res.stateChanged && res.receipt.status != ReceiptStatus.REJECTED) {
                    issuedReceipts[key] ?: res.receipt.copy(cursor = seatStreams[seat].cursor).also {
                        issuedReceipts[key] = it
                    }
                } else {
                    val r = res.receipt.copy(cursor = seatStreams[seat].cursor)
                    if (r.status != ReceiptStatus.REJECTED) {
                        issuedReceipts[key] = r
                    }
                    r
                }
            }
        }
    }

    fun submitTileMark(
        seat: Int,
        request: TileMarkRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (connectionControl.seatState(seat).isDisconnected) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SEAT_DISCONNECTED")
            } else if (!connectionControl.isHuman(seat)) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SEAT_NOT_HUMAN")
            } else {
                val res = authority.submitTileMark(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.Mark(request.roundId, seat, request.requestId)
                if (!res.stateChanged && res.receipt.status != ReceiptStatus.REJECTED) {
                    issuedReceipts[key] ?: res.receipt.copy(cursor = seatStreams[seat].cursor).also {
                        issuedReceipts[key] = it
                    }
                } else {
                    val r = res.receipt.copy(cursor = seatStreams[seat].cursor)
                    if (r.status != ReceiptStatus.REJECTED) {
                        issuedReceipts[key] = r
                    }
                    r
                }
            }
        }
    }

    fun claimControl(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt("claimControl", ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (seat !in 0..3) {
                ActionReceipt("claimControl", ReceiptStatus.REJECTED, reason = "INVALID_SEAT")
            } else if (connectionControl.seatState(seat).isDisconnected) {
                ActionReceipt("claimControl", ReceiptStatus.REJECTED, reason = "SEAT_DISCONNECTED")
            } else if (connectionControl.isHuman(seat)) {
                ActionReceipt("claimControl", ReceiptStatus.APPLIED, cursor = seatStreams[seat].cursor)
            } else {
                val orig = initialControllers.getOrNull(seat)
                if (orig is SeatController.Computer) {
                    ActionReceipt("claimControl", ReceiptStatus.REJECTED, reason = "SEAT_NOT_HUMAN")
                } else {
                    connectionControl.reclaimControl(seat)
                    val restored = when (orig) {
                        is SeatController.LocalHuman -> orig
                        is SeatController.RemoteHuman -> orig
                        else -> SeatController.LocalHuman()
                    }
                    authority.setSeatController(seat, restored)
                    syncDecisionsAndClocks()
                    publishStateChanges(emptyList())
                    ActionReceipt("claimControl", ReceiptStatus.APPLIED, cursor = seatStreams[seat].cursor)
                }
            }
        }
    }

    fun releaseControl(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt("releaseControl", ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (seat !in 0..3) {
                ActionReceipt("releaseControl", ReceiptStatus.REJECTED, reason = "INVALID_SEAT")
            } else if (connectionControl.isTakenOver(seat)) {
                ActionReceipt("releaseControl", ReceiptStatus.APPLIED, cursor = seatStreams[seat].cursor)
            } else {
                val state = connectionControl.requestControl(seat, takeover = true)
                val profile = state.aiProfileId ?: disconnectPolicy.aiProfileId
                val strategy = resolveAiStrategy(profile)
                authority.setSeatController(seat, SeatController.Takeover(strategy))
                syncDecisionsAndClocks()
                publishStateChanges(emptyList())
                ActionReceipt("releaseControl", ReceiptStatus.APPLIED, cursor = seatStreams[seat].cursor)
            }
        }
    }

    fun snapshot(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerSnapshot> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            val stream = seatStreams[seat]
            authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews()).deepCopy()
        }
    }

    fun decision(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerDecision> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            authority.queryDecision(seat, isAutomated = connectionControl.seatState(seat).isAutomated).deepCopy()
        }
    }

    fun resume(seat: Int, cursor: Long, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerSnapshot> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            val stream = seatStreams[seat]
            authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews()).deepCopy()
        }
    }

    fun initialSnapshot(seat: Int): PlayerSnapshot {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        val stream = seatStreams[seat]
        val snap = stream.lastEmittedSnapshot
            ?: authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews())
        return snap.deepCopy()
    }

    fun initialPublicSnapshot(): ProtocolPublicSnapshot {
        return publicStream.lastEmittedSnapshot
            ?: buildProtocolPublicSnapshot(publicStream.cursor)
    }

    fun publicSnapshot(sessionValidator: (() -> Boolean)? = null): CompletableFuture<ProtocolPublicSnapshot> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            performQueryPublicSnapshotOnQueue()
        }
    }

    internal fun performQueryPublicSnapshotOnQueue(): ProtocolPublicSnapshot {
        return buildProtocolPublicSnapshot(publicStream.cursor)
    }

    /**
     * 队列内真实 PUBLIC 投影构造：经 [TableAuthority.queryPublicProjection] 取纯公开投影，
     * 再由 [PublicProtocolMapper] 逐项映射为独立协议快照；绝不回退玩家 snapshot 兜底。
     */
    private fun buildProtocolPublicSnapshot(cursor: Long): ProtocolPublicSnapshot {
        val projection = authority.queryPublicProjection()
        return PublicProtocolMapper.toProtocolPublicSnapshot(projection, publicStream.syncEpoch, cursor)
    }

    /**
     * 完整 PUBLIC checkpoint 权威校验（仅 serialExecutor 队列内调用）：
     * sessionId 与 syncEpoch 必须与当前公开流完全一致，cursor 不得超前于权威游标。
     * 校验失败抛 IllegalStateException，调用方不得据此递增成员代次或替换现有绑定。
     */
    internal fun performValidatePublicCheckpointOnQueue(checkpoint: PublicCheckpoint) {
        val sessionId = authority.sessionId
        if (checkpoint.sessionId != sessionId ||
            checkpoint.syncEpoch != publicStream.syncEpoch ||
            checkpoint.cursor < 0L ||
            checkpoint.cursor > publicStream.cursor ||
            checkpoint.cursor == Long.MAX_VALUE
        ) {
            throw IllegalStateException("INVALID_PUBLIC_CHECKPOINT")
        }
    }

    /**
     * 基于完整公开检查点 (sessionId, syncEpoch, cursor) 的对账恢复：
     * 在权威队列内先校验检查点与当前观战绑定（sessionValidator），
     * 通过后返回权威全量基线；客户端给出的游标绝不决定权威输出基线。
     */
    fun resumePublic(
        checkpoint: PublicCheckpoint,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolPublicSnapshot> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            performValidatePublicCheckpointOnQueue(checkpoint)
            performQueryPublicSnapshotOnQueue()
        }
    }

    fun queryPublicRoundResultEvents(
        roundId: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<List<ProtocolGameEvent>> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            val events = authority.queryPublicRoundResultEvents(roundId)
            EngineProtocolMapper.toProtocolEvents(events)
        }
    }

    fun updateConfig(
        newConfig: MatchConfig,
        newTimeControl: TimeControlConfig = this.timeControl
    ): CompletableFuture<Boolean> {
        return enqueueOperation {
            performUpdateConfigOnQueue(newConfig, newTimeControl)
        }
    }

    fun queryRoundResultEvents(
        seat: Int,
        roundId: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<List<GameEvent>> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw IllegalStateException("STALE_SESSION")
            }
            if (seat !in 0..3) emptyList()
            else authority.queryRoundResultEvents(seat, roundId)
        }
    }

    fun applyCheatLedgerEntry(entryId: String, seat: Int, amount: Int, reason: String): CompletableFuture<ActionResult> {
        return enqueueOperation {
            val res = authority.applyCheatLedgerEntry(entryId, seat, amount, reason, clock.nowMillis())
            if (res.stateChanged) {
                publishStateChanges(res.events)
            }
            if (res.receipt.status == ReceiptStatus.APPLIED) {
                ActionResult.Accepted(res.events)
            } else {
                ActionResult.Rejected(com.majiang.state.RejectionReason.ACTION_NOT_AVAILABLE)
            }
        }
    }

    fun pause(): CompletableFuture<Boolean> {
        return enqueueOperation {
            val changed = authority.pause(clock.nowMillis())
            if (changed) {
                publishStateChanges(emptyList())
            }
            changed
        }
    }

    fun resumeClock(): CompletableFuture<Boolean> {
        return enqueueOperation {
            val changed = authority.resume(clock.nowMillis())
            if (changed) {
                publishStateChanges(emptyList())
            }
            changed
        }
    }

    fun disconnect(seat: Int) {
        if (closed) return
        try {
            serialExecutor.execute {
                if (!closed) {
                    performDisconnectOnQueue(seat)
                }
            }
        } catch (_: RejectedExecutionException) {}
    }

    fun reconnect(seat: Int) {
        if (closed) return
        try {
            serialExecutor.execute {
                if (!closed && seat in 0..3) {
                    if (connectionControl.seatState(seat).isDisconnected) {
                        connectionControl.reconnect(seat, clock.nowMillis(), reclaim = false)
                        publishStateChanges(emptyList())
                    }
                }
            }
        } catch (_: RejectedExecutionException) {}
    }

    /**
     * 订阅座位更新：
     * 1. 注册与基线快照提取串行化在 serialExecutor 内执行，避免与突变竞争；
     * 2. 派发给外部监听器在独立的 seatDispatchers[seat] 线程，慢消费者完全不影响 serialExecutor；
     * 3. 异步派发前重新校验订阅有效性与宿主开启状态，并提供独立的深拷贝。
     */
    fun subscribeSeat(seat: Int, listener: (PlayerUpdate) -> Unit): AutoCloseable {
        if (closed) throw IllegalStateException("ServerTableHost is closed")
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        val sub = SeatSubscription(subscriptionIdCounter.incrementAndGet(), listener)
        val stream = seatStreams[seat]

        try {
            serialExecutor.execute {
                if (closed || !sub.active) return@execute
                stream.subscriptions.add(sub)
                val currentSnap = stream.lastEmittedSnapshot
                    ?: authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews())
                val baseline = PlayerUpdate(
                    snapshot = currentSnap.deepCopy(),
                    fromCursor = stream.cursor,
                    visibleEvents = emptyList()
                )
                try {
                    seatDispatchers[seat].execute {
                        if (!closed && sub.active) {
                            try {
                                sub.listener(baseline.deepCopy())
                            } catch (_: Throwable) {}
                        }
                    }
                } catch (_: RejectedExecutionException) {}
            }
        } catch (e: RejectedExecutionException) {
            if (closed) throw IllegalStateException("ServerTableHost is closed", e)
        }

        return AutoCloseable {
            sub.active = false
            if (!closed) {
                try {
                    serialExecutor.execute {
                        stream.subscriptions.remove(sub)
                    }
                } catch (_: RejectedExecutionException) {}
            }
        }
    }

    /**
     * 订阅公开流更新：
     * 1. 注册与基线快照提取在 serialExecutor 内串行化执行；
     * 2. 派发给外部监听器在独立的 publicDispatcher 线程，慢消费者完全不影响 serialExecutor 与玩家；
     * 3. 初始基线以 isFullSnapshot = true 下发。
     */
    fun subscribePublic(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable {
        if (closed) throw IllegalStateException("ServerTableHost is closed")
        val sub = PublicSubscription(subscriptionIdCounter.incrementAndGet(), listener)

        try {
            serialExecutor.execute {
                if (closed || !sub.active) return@execute
                publicStream.subscriptions.add(sub)
                val currentSnap = publicStream.lastEmittedSnapshot
                    ?: buildProtocolPublicSnapshot(publicStream.cursor)
                val baseline = ProtocolPublicUpdate(
                    snapshot = currentSnap,
                    fromCursor = currentSnap.cursor,
                    visibleEvents = emptyList(),
                    isFullSnapshot = true
                )
                try {
                    publicDispatcher.execute {
                        if (!closed && sub.active) {
                            try {
                                sub.listener(baseline)
                            } catch (_: Throwable) {}
                        }
                    }
                } catch (_: RejectedExecutionException) {}
            }
        } catch (e: RejectedExecutionException) {
            if (closed) throw IllegalStateException("ServerTableHost is closed", e)
        }

        return AutoCloseable {
            sub.active = false
            if (!closed) {
                try {
                    serialExecutor.execute {
                        publicStream.subscriptions.remove(sub)
                    }
                } catch (_: RejectedExecutionException) {}
            }
        }
    }

    /**
     * 异步非阻塞 tick，返回 CompletableFuture<Boolean>，调用方永不被 2 秒锁死。
     */
    fun tick(presentationBusy: Boolean = false): CompletableFuture<Boolean> {
        return enqueueOperation {
            tickInternal(presentationBusy)
        }
    }

    // ========================================
    // 内部串行逻辑（全部在 serialExecutor 内执行）
    // ========================================

    private fun tickInternal(presentationBusy: Boolean): Boolean {
        if (closed || authority.isPaused) return false
        val now = clock.nowMillis()

        // 0. 洗牌千术准备阶段
        if (authority.isPreparationActive()) {
            val automatedSeats = (0 until 4).filter { connectionControl.seatState(it).isAutomated }.toSet()
            val nativeAiSeats = (0 until 4).filter { connectionControl.seatState(it).isComputer && initialControllers.getOrNull(it) is SeatController.Computer }.toSet()
            val dealResult = authority.checkPreparationStep(now, automatedSeats, nativeAiSeats)
            if (dealResult != null) {
                syncDecisionsAndClocks()
                publishStateChanges(dealResult.allEvents)
                lastActionTimeMillis = now
                return true
            }
            return false
        }

        // 1. 宽限期到期接管
        val graceTakeovers = connectionControl.pollPendingTakeovers(now)
        if (graceTakeovers.isNotEmpty()) {
            for (seat in graceTakeovers) {
                val profile = connectionControl.seatState(seat).aiProfileId ?: disconnectPolicy.aiProfileId
                val strategy = resolveAiStrategy(profile)
                authority.setSeatController(seat, SeatController.Takeover(strategy))
            }
            syncDecisionsAndClocks()
            publishStateChanges(emptyList())
        }

        // 2. 超时默认动作优先裁决（不受 presentationBusy 推迟，保持 deadline 权威优先权）
        val timeoutResult = authority.checkTimeout(now)
        if (timeoutResult != null) {
            syncDecisionsAndClocks()
            publishStateChanges(timeoutResult.allEvents)
            lastActionTimeMillis = now
            return true
        }

        // 3. 局间庄家决策推进
        val dealer = authority.currentDealer
        if (authority.sessionLifecycle == com.majiang.match.SessionLifecycle.WAITING_DEALER_CHOICE && connectionControl.seatState(dealer).isAutomated) {
            val elapsed = now - lastActionTimeMillis
            if (!presentationBusy && (rhythmIntervalMillis <= 0L || elapsed !in 0 until rhythmIntervalMillis)) {
                val dealerResult = authority.checkWaitingDealerChoice(now, automatedDealer = true)
                if (dealerResult != null) {
                    syncDecisionsAndClocks()
                    publishStateChanges(dealerResult.allEvents)
                    lastActionTimeMillis = now
                    return true
                }
            }
        }

        // 4. 自动下一局推进
        val humanSeats = (0 until 4).filter { connectionControl.isHuman(it) }
        val allReady = humanSeats.isEmpty() || humanSeats.all { readySeats.contains(it) }
        val elapsed = now - lastActionTimeMillis
        if (allReady && !presentationBusy && (rhythmIntervalMillis <= 0L || elapsed !in 0 until rhythmIntervalMillis)) {
            val nextRoundResult = authority.checkAutoNextRound(now, allReady = true)
            if (nextRoundResult != null) {
                readySeats.clear()
                syncDecisionsAndClocks()
                publishStateChanges(nextRoundResult.allEvents)
                lastActionTimeMillis = now
                return true
            }
        }

        // 5. 动画门控与节奏控制
        if (presentationBusy) return false
        if (rhythmIntervalMillis > 0L && elapsed in 0 until rhythmIntervalMillis) return false

        // 6. 处理已完成的异步 AI 决策 (至多执行一个)
        while (true) {
            val aiResult = pendingAiResults.poll() ?: break
            val (seat, roundId, decisionId, epoch, action) = aiResult
            if (connectionControl.seatState(seat).isAutomated && action != null) {
                val res = authority.submitAiChoice(seat, roundId, decisionId, epoch, action, now)
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = now
                    return true
                }
            }
        }

        // 7. 自动立直摸切
        val humanAutoRiichiSeats = (0 until 4).filter { s ->
            connectionControl.isHuman(s) && when (val c = authority.controllerFor(s)) {
                is SeatController.LocalHuman -> c.autoRiichiDiscard
                is SeatController.RemoteHuman -> c.autoRiichiDiscard
                else -> false
            }
        }.toSet()
        val riichiResult = authority.checkRiichiAutoDiscard(now, humanAutoRiichiSeats)
        if (riichiResult != null) {
            syncDecisionsAndClocks()
            publishStateChanges(riichiResult.allEvents)
            lastActionTimeMillis = now
            return true
        }

        return false
    }

    private fun syncDecisionsAndClocks() {
        val now = clock.nowMillis()
        val automatedSeats = (0 until 4).filter { connectionControl.seatState(it).isAutomated }.toSet()
        val requests = authority.syncDecisionsAndClocks(now, automatedSeats)
        val activeRoundId = authority.currentRoundId

        dispatchedAiTasks.removeIf { it.roundId != (activeRoundId ?: "session") }

        for (req in requests) {
            val taskKey = AiTaskKey(req.seat, req.roundId, req.decisionId, req.controlEpoch)
            if (dispatchedAiTasks.add(taskKey)) {
                val strategy = resolveAiStrategyForSeat(req.seat)
                try {
                    internalAiExecutor.submit {
                        try {
                            val chosen = strategy.decideAction(req.view, req.legalActions)
                            if (chosen != null) {
                                pendingAiResults.add(AiResult(req.seat, req.roundId, req.decisionId, req.controlEpoch, chosen))
                            }
                        } catch (_: Throwable) {
                        }
                    }
                } catch (_: RejectedExecutionException) {
                }
            }
        }
    }

    private fun resolveAiStrategyForSeat(seat: Int): AIStrategy {
        return if (connectionControl.isTakenOver(seat)) {
            val profile = connectionControl.seatState(seat).aiProfileId ?: disconnectPolicy.aiProfileId
            resolveAiStrategy(profile)
        } else {
            when (val c = authority.controllerFor(seat)) {
                is SeatController.Computer -> c.strategy
                is SeatController.Takeover -> c.strategy
                else -> resolveAiStrategy(disconnectPolicy.aiProfileId)
            }
        }
    }

    private fun publishStateChanges(events: List<GameEvent>) {
        val controls = buildSeatControlViews()
        for (s in 0..3) {
            val visibleEvents = SessionUpdate.filterVisibleEvents(events, s)
            val stream = seatStreams[s]
            val prevSnap = stream.lastEmittedSnapshot
            val candidateSnap = authority.querySnapshot(s, stream.syncEpoch, stream.cursor, controls)

            val hasMeaningfulClockChange = prevSnap != null && hasMeaningfulClockDifference(prevSnap.clocks, candidateSnap.clocks)
            val hasVisibleChange = prevSnap == null ||
                visibleEvents.isNotEmpty() ||
                prevSnap.view != candidateSnap.view ||
                prevSnap.decision != candidateSnap.decision ||
                prevSnap.roundVersion != candidateSnap.roundVersion ||
                prevSnap.sessionVersion != candidateSnap.sessionVersion ||
                prevSnap.roundId != candidateSnap.roundId ||
                prevSnap.cheatPoints != candidateSnap.cheatPoints ||
                prevSnap.session != candidateSnap.session ||
                prevSnap.controls != candidateSnap.controls ||
                prevSnap.paused != candidateSnap.paused ||
                prevSnap.settlementRoundId != candidateSnap.settlementRoundId ||
                prevSnap.settlementEvents != candidateSnap.settlementEvents ||
                prevSnap.preparation != candidateSnap.preparation ||
                prevSnap.tileMark != candidateSnap.tileMark ||
                hasMeaningfulClockChange

            if (!hasVisibleChange) {
                continue
            }

            val fromCursor = stream.cursor
            val newCursor = stream.cursor + 1L
            stream.cursor = newCursor
            val snapshot = candidateSnap.copy(cursor = newCursor)
            stream.lastEmittedSnapshot = snapshot

            val update = PlayerUpdate(
                snapshot = snapshot,
                fromCursor = fromCursor,
                visibleEvents = visibleEvents
            )
            stream.addHistory(update)

            val activeSubs = ArrayList(stream.subscriptions)
            for (sub in activeSubs) {
                if (!sub.active) continue
                val copy = update.deepCopy()
                try {
                    seatDispatchers[s].execute {
                        if (!closed && sub.active) {
                            try {
                                sub.listener(copy)
                            } catch (_: Throwable) {}
                        }
                    }
                } catch (_: RejectedExecutionException) {}
            }
        }
        publishPublicChanges(events)
        onStateCommitted?.invoke()
    }

    private fun publishPublicChanges(events: List<GameEvent>) {
        val publicEvents = authority.filterPublicEvents(events)
        val candidateSnap = buildProtocolPublicSnapshot(publicStream.cursor)
        val prevSnap = publicStream.lastEmittedSnapshot

        val hasMeaningfulClockChange = prevSnap != null && hasMeaningfulClockDifferenceProtocol(prevSnap.clocks, candidateSnap.clocks)
        val hasVisibleChange = prevSnap == null ||
            publicEvents.isNotEmpty() ||
            prevSnap.board != candidateSnap.board ||
            prevSnap.session != candidateSnap.session ||
            prevSnap.roundVersion != candidateSnap.roundVersion ||
            prevSnap.sessionVersion != candidateSnap.sessionVersion ||
            prevSnap.roundId != candidateSnap.roundId ||
            prevSnap.paused != candidateSnap.paused ||
            prevSnap.settlementRoundId != candidateSnap.settlementRoundId ||
            prevSnap.settlementEvents != candidateSnap.settlementEvents ||
            hasMeaningfulClockChange

        if (!hasVisibleChange) {
            return
        }

        val fromCursor = publicStream.cursor
        val newCursor = publicStream.cursor + 1L
        publicStream.cursor = newCursor
        val snapshot = candidateSnap.copy(cursor = newCursor)
        publicStream.lastEmittedSnapshot = snapshot

        val protocolEvents = EngineProtocolMapper.toProtocolEvents(publicEvents)
        val update = ProtocolPublicUpdate(
            snapshot = snapshot,
            fromCursor = fromCursor,
            visibleEvents = protocolEvents,
            isFullSnapshot = false
        )
        publicStream.addHistory(update)

        val activeSubs = ArrayList(publicStream.subscriptions)
        for (sub in activeSubs) {
            if (!sub.active) continue
            try {
                publicDispatcher.execute {
                    if (!closed && sub.active) {
                        try {
                            sub.listener(update)
                        } catch (_: Throwable) {}
                    }
                }
            } catch (_: RejectedExecutionException) {}
        }
    }

    private fun hasMeaningfulClockDifference(oldClocks: List<ClockView>, newClocks: List<ClockView>): Boolean {
        if (oldClocks.size != newClocks.size) return true
        for (i in oldClocks.indices) {
            val o = oldClocks[i]
            val n = newClocks[i]
            if (o.seat != n.seat || o.opensAt != n.opensAt || o.deadline != n.deadline || o.reserve != n.reserve) {
                return true
            }
        }
        return false
    }

    private fun hasMeaningfulClockDifferenceProtocol(oldClocks: List<ProtocolClock>, newClocks: List<ProtocolClock>): Boolean {
        if (oldClocks.size != newClocks.size) return true
        for (i in oldClocks.indices) {
            val o = oldClocks[i]
            val n = newClocks[i]
            if (o.seat != n.seat || o.opensAt != n.opensAt || o.deadline != n.deadline || o.reserve != n.reserve) {
                return true
            }
        }
        return false
    }

    private fun buildSeatControlViews(): List<SeatControlView> {
        val list = mutableListOf<SeatControlView>()
        for (s in 0..3) {
            val state = connectionControl.seatState(s)
            val epoch = authority.controlEpochFor(s)
            list.add(
                SeatControlView(
                    seat = s,
                    connected = state.isConnected,
                    role = state.role.name,
                    controlEpoch = epoch
                )
            )
        }
        return Collections.unmodifiableList(list)
    }

    override fun close() {
        if (isClosed.compareAndSet(false, true)) {
            val cancelEx = IllegalStateException("ServerTableHost is closed")
            val futuresToCancel = ArrayList(pendingFutures)
            pendingFutures.clear()
            for (f in futuresToCancel) {
                f.completeExceptionally(cancelEx)
            }

            authority.close()
            if (ownsSerialExecutor) {
                serialExecutor.shutdownNow()
            }
            if (ownsAiExecutor) {
                internalAiExecutor.shutdownNow()
            }
            for (disp in seatDispatchers) {
                disp.shutdownNow()
            }
            publicDispatcher.shutdownNow()
            completionExecutor.shutdownNow()

            for (stream in seatStreams) {
                stream.subscriptions.clear()
                stream.clearHistory()
            }
            publicStream.subscriptions.clear()
            publicStream.clearHistory()
            readySeats.clear()
            issuedReceipts.clear()
            dispatchedAiTasks.clear()
            pendingAiResults.clear()
        }
    }
}

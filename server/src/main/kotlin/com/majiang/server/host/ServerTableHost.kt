package com.majiang.server.host

import com.majiang.access.ActionReceipt
import com.majiang.access.ClockView
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.access.SessionView
import com.majiang.access.PlayerPresentationPreview
import com.majiang.cheat.mark.TileMarkView
import com.majiang.cheat.bloom.SleeveBloomView
import com.majiang.cheat.beam.BeamSwapView
import com.majiang.stacking.ShufflePreparationView
import com.majiang.access.deepCopy
import com.majiang.ai.AIStrategy
import com.majiang.ai.BasicAIStrategy
import com.majiang.application.ActionChoice
import com.majiang.application.PlayerDecision
import com.majiang.application.deepCopy
import com.majiang.authority.TableAuthority
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomOffTurnRequest
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.server.control.ConnectionControl
import com.majiang.server.control.ControllerRole
import com.majiang.server.control.DisconnectPolicy
import com.majiang.event.GameEvent
import com.majiang.ai.TsumogiriAIStrategy
import com.majiang.match.MatchConfig
import com.majiang.session.SessionUpdate
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.state.ActionResult
import com.majiang.state.GameAction
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ServerApiException
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.mapping.PublicProtocolMapper
import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock
import com.majiang.timing.TimeControlConfig
import com.majiang.transport.api.ProtocolClock
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolRoundOutcome
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
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ThreadFactory
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * 服务端权威单桌异步宿主 (ServerTableHost)
 *
 * 遵循 V4 架构契约与 MR-07a 规范：
 * 1. 围绕纯领域同步 [TableAuthority] 包装，提供每桌独立的串行执行队列 (serialExecutor，支持依赖注入)；
 * 2. 外部调用（本地进程内、网络 TCP、测试）只入队并立即返回 Future，绝不在调用方或接收循环线程上加持领域锁；
 * 3. 权威计算与下游外部 Future 回调解耦：在独立 completion 线程池完成 Future，防止慢回执消费者/网络发送阻塞权威队列；
 * 4. 拥有网络连接生命周期 (ConnectionControl)、席位投影游标与历史缓存、订阅派发解耦；
 * 5. 两种运行模式与线程预算归属：
 *    - 默认模式 (sharedDispatch == null)：行为与现状逐点等价，为 4 个席位与 1 个公开流分别创建专属单线程执行器，
 *      通道容量无界，保证向后兼容与既有测试零回归；
 *    - 共享模式 (sharedDispatch != null)：席位与公开流改用共享有界线程池上的 [SerialDispatchChannel]，
 *      通道具有独立的条目与估算字节容量限制；线程预算由服务器统一分配（与房间数 N 无关），杜绝随房间数线性增长的线程开销；
 * 6. 拥有后台 AI 线程池，在突变队列之外推理并在提交时二次验证 roundId、decisionId 与 controlEpoch；
 * 7. 权威超时事实具备优先裁决权，不受展示忙 (presentationBusy) 或玩家操作饥饿策略推迟；
 * 8. 确定性生命周期：close 立即拒绝并完成所有排队中的 Future，关闭自身拥有的资源，共享池绝不越权关闭。
 *
 * @param sharedDispatch 注入的共享派发执行器（null 表示默认模式，创建专属执行器）
 * @param sharedCompletion 注入的共享 Future 完成执行器（null 表示默认模式，创建专属 Cached 池）
 * @param seatChannelCapacityItems 席位通道队列条目容量（拟定默认值 0 表示无界，可通过服务端配置调整）
 * @param seatChannelCapacityBytes 席位通道队列估算字节容量（拟定默认值 0 表示无界，可通过服务端配置调整）
 * @param publicChannelCapacityItems 公开通道队列条目容量（拟定默认值 0 表示无界，可通过服务端配置调整）
 * @param publicChannelCapacityBytes 公开通道队列估算字节容量（拟定默认值 0 表示无界，可通过服务端配置调整）
 * @param channelDrainBatchSize 通道单轮排空批次大小（拟定默认值 16，可通过服务端配置调整）
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
    serialExecutor: ExecutorService? = null,
    sharedDispatch: ExecutorService? = null,
    sharedCompletion: ExecutorService? = null,
    seatChannelCapacityItems: Int = 0,
    seatChannelCapacityBytes: Long = 0L,
    publicChannelCapacityItems: Int = 0,
    publicChannelCapacityBytes: Long = 0L,
    channelDrainBatchSize: Int = 16,
    recoveryScheduler: ScheduledExecutorService? = null
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

    /** 公开会话 ID 只读透传（MR-07b 供公开帧缓存按会话管理与释放） */
    internal val publicSessionId: String
        get() = authority.sessionId

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

    // 外部 Future 完成解耦执行器：将领域计算与下游回调（如 TCP 发包、UI 回调）隔离
    private val ownsCompletionExecutor: Boolean = (sharedCompletion == null)
    internal val completionExecutor: ExecutorService = sharedCompletion ?: Executors.newCachedThreadPool(
        ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Completion").apply { isDaemon = true } }
    )

    // 默认模式下的专属单线程派发器（仅当未注入 sharedDispatch 时创建）
    private val ownsDispatchExecutors: Boolean = (sharedDispatch == null)
    private val defaultSeatDispatchers: Array<ExecutorService>? = if (ownsDispatchExecutors) {
        Array(4) { s ->
            Executors.newSingleThreadExecutor(
                ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Seat-$s-Dispatcher").apply { isDaemon = true } }
            )
        }
    } else null

    private val defaultPublicDispatcher: ExecutorService? = if (ownsDispatchExecutors) {
        Executors.newSingleThreadExecutor(
            ThreadFactory { r -> Thread(r, "ServerTableHost-$tableId-Public-Dispatcher").apply { isDaemon = true } }
        )
    } else null

    // 恢复调度器（用于调度重试耗尽后的受控全量快照恢复）
    private val ownsRecoveryScheduler: Boolean = (recoveryScheduler == null)
    internal val recoveryScheduler: ScheduledExecutorService = recoveryScheduler ?: defaultRecoveryScheduler
    private val pendingRecoveryTasks = ConcurrentHashMap.newKeySet<ScheduledFuture<*>>()
    private val seatRecoveryPending = Array(4) { AtomicBoolean(false) }
    private val publicRecoveryPending = AtomicBoolean(false)

    // 权威状态提交回调（在 serialExecutor 队列上触发，供 SharedRoom 同步生命周期）
    internal var onStateCommitted: (() -> Unit)? = null

    // 跟踪所有未决的外部 Future，以便 close 时确定性拒绝并完成
    private val pendingFutures = ConcurrentHashMap.newKeySet<CompletableFuture<*>>()
    val queuedOperationsCount: Int get() = pendingFutures.size

    // 席位派发条目封装
    internal sealed class SeatDispatchItem {
        abstract val update: PlayerUpdate
        abstract val isFullSnapshot: Boolean

        data class Broadcast(
            override val update: PlayerUpdate,
            override val isFullSnapshot: Boolean = false
        ) : SeatDispatchItem()

        data class TargetSubscriber(
            val subscription: SeatSubscription,
            override val update: PlayerUpdate,
            override val isFullSnapshot: Boolean = true
        ) : SeatDispatchItem()
    }

    // 公开流派发条目封装
    internal sealed class PublicDispatchItem {
        abstract val update: ProtocolPublicUpdate
        abstract val isFullSnapshot: Boolean

        data class Broadcast(
            override val update: ProtocolPublicUpdate,
            override val isFullSnapshot: Boolean = false
        ) : PublicDispatchItem()

        data class TargetSubscriber(
            val subscription: PublicSubscription,
            override val update: ProtocolPublicUpdate,
            override val isFullSnapshot: Boolean = true
        ) : PublicDispatchItem()
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

    // 4 个席位串行派发通道
    internal val seatChannels: Array<SerialDispatchChannel<SeatDispatchItem>> = Array(4) { s ->
        val exec = sharedDispatch ?: defaultSeatDispatchers!![s]
        SerialDispatchChannel(
            name = "ServerTableHost-$tableId-Seat-$s",
            executor = exec,
            maxQueuedItems = seatChannelCapacityItems,
            maxQueuedBytes = seatChannelCapacityBytes,
            drainBatchSize = channelDrainBatchSize,
            byteEstimator = { item -> estimateSeatDispatchBytes(item) },
            isFullSnapshotPredicate = { it.isFullSnapshot },
            retryScheduler = recoveryScheduler,
            allowSingleOversized = true,
            onScheduleFailure = { _, _, _ ->
                scheduleSeatRecovery(s)
            },
            consumer = { item ->
                when (item) {
                    is SeatDispatchItem.Broadcast -> {
                        val activeSubs = ArrayList(seatStreams[s].subscriptions)
                        for (sub in activeSubs) {
                            if (!sub.active || closed) continue
                            val copy = item.update.deepCopy()
                            try {
                                sub.listener(copy)
                            } catch (_: Throwable) {
                            }
                        }
                    }
                    is SeatDispatchItem.TargetSubscriber -> {
                        if (item.subscription.active && !closed) {
                            val copy = item.update.deepCopy()
                            try {
                                item.subscription.listener(copy)
                            } catch (_: Throwable) {
                            }
                        }
                    }
                }
            }
        )
    }

    // 独立公开流串行派发通道
    internal val publicChannel: SerialDispatchChannel<PublicDispatchItem> = SerialDispatchChannel(
        name = "ServerTableHost-$tableId-Public",
        executor = sharedDispatch ?: defaultPublicDispatcher!!,
        maxQueuedItems = publicChannelCapacityItems,
        maxQueuedBytes = publicChannelCapacityBytes,
        drainBatchSize = channelDrainBatchSize,
        byteEstimator = { item -> estimatePublicDispatchBytes(item) },
        isFullSnapshotPredicate = { it.isFullSnapshot },
        retryScheduler = recoveryScheduler,
        allowSingleOversized = true,
        onScheduleFailure = { _, _, _ ->
            schedulePublicRecovery()
        },
        consumer = { item ->
            when (item) {
                is PublicDispatchItem.Broadcast -> {
                    val activeSubs = ArrayList(publicStream.subscriptions)
                    for (sub in activeSubs) {
                        if (!sub.active || closed) continue
                        try {
                            sub.listener(item.update)
                        } catch (_: Throwable) {
                        }
                    }
                }
                is PublicDispatchItem.TargetSubscriber -> {
                    if (item.subscription.active && !closed) {
                        try {
                            item.subscription.listener(item.update)
                        } catch (_: Throwable) {
                        }
                    }
                }
            }
        }
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
        data class Bloom(val roundId: String, val seat: Int, val requestId: String) : HostReceiptKey()
        data class BeamSwap(val roundId: String, val seat: Int, val requestId: String) : HostReceiptKey()
        data class BeamSwapChoice(val roundId: String, val seat: Int, val requestId: String) : HostReceiptKey()
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

    fun submitSleeveBloom(
        seat: Int,
        request: SleeveBloomRequest,
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
                val res = authority.submitSleeveBloom(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.Bloom(request.roundId, seat, request.requestId)
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

    fun submitSleeveBloomChoice(
        seat: Int,
        request: SleeveBloomChoiceRequest,
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
                val res = authority.submitSleeveBloomChoice(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.Bloom(request.roundId, seat, request.requestId)
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

    fun submitSleeveBloomOffTurn(
        seat: Int,
        request: SleeveBloomOffTurnRequest,
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
                val res = authority.submitSleeveBloomOffTurn(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.Bloom(request.roundId, seat, request.requestId)
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

    fun submitSleeveBloomOffTurnChoice(
        seat: Int,
        request: SleeveBloomOffTurnChoiceRequest,
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
                val res = authority.submitSleeveBloomOffTurnChoice(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.Bloom(request.roundId, seat, request.requestId)
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

    fun submitBeamSwap(
        seat: Int,
        request: BeamSwapRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> {        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_SESSION")
            } else if (connectionControl.seatState(seat).isDisconnected) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SEAT_DISCONNECTED")
            } else if (!connectionControl.isHuman(seat)) {
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SEAT_NOT_HUMAN")
            } else {
                val res = authority.submitBeamSwap(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.BeamSwap(request.roundId, seat, request.requestId)
                if (res.receipt.reason == "REQUEST_ID_CONFLICT") {
                    res.receipt.copy(cursor = seatStreams[seat].cursor)
                } else if (!res.stateChanged) {
                    issuedReceipts[key] ?: res.receipt.copy(cursor = seatStreams[seat].cursor).also {
                        issuedReceipts[key] = it
                    }
                } else {
                    val r = res.receipt.copy(cursor = seatStreams[seat].cursor)
                    issuedReceipts[key] = r
                    r
                }
            }
        }
    }

    fun submitBeamSwapChoice(
        seat: Int,
        request: BeamSwapChoiceRequest,
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
                val res = authority.submitBeamSwapChoice(seat, request, clock.nowMillis())
                if (res.stateChanged) {
                    syncDecisionsAndClocks()
                    publishStateChanges(res.events)
                    lastActionTimeMillis = clock.nowMillis()
                }
                val key = HostReceiptKey.BeamSwapChoice(request.roundId, seat, request.requestId)
                if (res.receipt.reason == "REQUEST_ID_CONFLICT") {
                    res.receipt.copy(cursor = seatStreams[seat].cursor)
                } else if (!res.stateChanged) {
                    issuedReceipts[key] ?: res.receipt.copy(cursor = seatStreams[seat].cursor).also {
                        issuedReceipts[key] = it
                    }
                } else {
                    val r = res.receipt.copy(cursor = seatStreams[seat].cursor)
                    issuedReceipts[key] = r
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
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            }
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            val stream = seatStreams[seat]
            authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews()).deepCopy()
        }
    }

    fun snapshotAndSubscribe(
        seat: Int,
        sessionValidator: (() -> Boolean)? = null,
        listener: (PlayerUpdate) -> Unit
    ): CompletableFuture<Pair<PlayerSnapshot, AutoCloseable>> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            }
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            val stream = seatStreams[seat]
            val snap = authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews()).deepCopy()
            val sub = SeatSubscription(subscriptionIdCounter.incrementAndGet(), listener)
            stream.subscriptions.add(sub)
            val handle = AutoCloseable {
                sub.active = false
                if (!closed) {
                    try {
                        serialExecutor.execute {
                            stream.subscriptions.remove(sub)
                        }
                    } catch (_: RejectedExecutionException) {}
                }
            }
            Pair(snap, handle)
        }
    }

    fun decision(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerDecision> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            }
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            authority.queryDecision(seat, isAutomated = connectionControl.seatState(seat).isAutomated).deepCopy()
        }
    }

    fun resume(seat: Int, cursor: Long, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerSnapshot> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
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
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            }
            performQueryPublicSnapshotOnQueue()
        }
    }

    fun publicSnapshotAndSubscribe(
        sessionValidator: (() -> Boolean)? = null,
        listener: (ProtocolPublicUpdate) -> Unit
    ): CompletableFuture<Pair<ProtocolPublicSnapshot, AutoCloseable>> {
        return enqueueOperation {
            if (sessionValidator?.invoke() == false) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            }
            val snap = performQueryPublicSnapshotOnQueue()
            val sub = PublicSubscription(subscriptionIdCounter.incrementAndGet(), listener)
            publicStream.subscriptions.add(sub)
            val handle = AutoCloseable {
                sub.active = false
                if (!closed) {
                    try {
                        serialExecutor.execute {
                            publicStream.subscriptions.remove(sub)
                        }
                    } catch (_: RejectedExecutionException) {}
                }
            }
            Pair(snap, handle)
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
            throw ServerApiException(ApiErrors.INVALID_TOKEN, "INVALID_PUBLIC_CHECKPOINT")
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
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
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
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
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
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
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
                val enqueued = seatChannels[seat].enqueue(
                    SeatDispatchItem.TargetSubscriber(sub, baseline, isFullSnapshot = true)
                )
                if (!enqueued) {
                    if (closed) {
                        sub.active = false
                        stream.subscriptions.remove(sub)
                    } else {
                        scheduleSeatRecovery(seat)
                    }
                }
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
     * 2. 派发给外部监听器经独立串行通道 publicChannel 派发，慢消费者完全不影响 serialExecutor 与玩家；
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
                val enqueued = publicChannel.enqueue(
                    PublicDispatchItem.TargetSubscriber(sub, baseline, isFullSnapshot = true)
                )
                if (!enqueued) {
                    if (closed) {
                        sub.active = false
                        publicStream.subscriptions.remove(sub)
                    } else {
                        schedulePublicRecovery()
                    }
                }
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

    // ========================================
    // 调度失败与溢出主动全量恢复机制 (MR-07a)
    // ========================================

    internal fun scheduleSeatRecovery(seat: Int, delayMillis: Long = 20L) {
        if (closed || seat !in 0..3) return
        if (!seatRecoveryPending[seat].compareAndSet(false, true)) {
            return
        }
        val taskRef = AtomicReference<ScheduledFuture<*>?>()
        val task = try {
            recoveryScheduler.schedule({
                val t = taskRef.get()
                if (t != null) {
                    pendingRecoveryTasks.remove(t)
                }
                if (closed) {
                    seatRecoveryPending[seat].set(false)
                    return@schedule
                }
                try {
                    serialExecutor.execute {
                        seatRecoveryPending[seat].set(false)
                        if (!closed) {
                            performSeatRecoveryOnQueue(seat)
                        }
                    }
                } catch (_: RejectedExecutionException) {
                    seatRecoveryPending[seat].set(false)
                }
            }, delayMillis.coerceAtLeast(0L), TimeUnit.MILLISECONDS)
        } catch (_: RejectedExecutionException) {
            seatRecoveryPending[seat].set(false)
            return
        }
        taskRef.set(task)
        if (closed || task.isDone) {
            pendingRecoveryTasks.remove(task)
            if (closed) {
                task.cancel(false)
            }
        } else {
            pendingRecoveryTasks.add(task)
        }
    }

    private fun performSeatRecoveryOnQueue(seat: Int) {
        if (closed || seat !in 0..3) return
        val stream = seatStreams[seat]
        val activeSubs = stream.subscriptions.filter { it.active }
        if (activeSubs.isEmpty()) {
            return
        }
        val channel = seatChannels[seat]
        if (!channel.needsRecovery && channel.queuedItems > 0) {
            return
        }

        val currentSnap = stream.lastEmittedSnapshot
            ?: authority.querySnapshot(seat, stream.syncEpoch, stream.cursor, buildSeatControlViews())
        val baseline = PlayerUpdate(
            snapshot = currentSnap.deepCopy(),
            fromCursor = currentSnap.cursor,
            visibleEvents = emptyList()
        )
        val enqueued = channel.enqueue(
            SeatDispatchItem.Broadcast(baseline, isFullSnapshot = true)
        )
        if (!enqueued && !closed) {
            scheduleSeatRecovery(seat, delayMillis = 50L)
        }
    }

    internal fun schedulePublicRecovery(delayMillis: Long = 20L) {
        if (closed) return
        if (!publicRecoveryPending.compareAndSet(false, true)) {
            return
        }
        val taskRef = AtomicReference<ScheduledFuture<*>?>()
        val task = try {
            recoveryScheduler.schedule({
                val t = taskRef.get()
                if (t != null) {
                    pendingRecoveryTasks.remove(t)
                }
                if (closed) {
                    publicRecoveryPending.set(false)
                    return@schedule
                }
                try {
                    serialExecutor.execute {
                        publicRecoveryPending.set(false)
                        if (!closed) {
                            performPublicRecoveryOnQueue()
                        }
                    }
                } catch (_: RejectedExecutionException) {
                    publicRecoveryPending.set(false)
                }
            }, delayMillis.coerceAtLeast(0L), TimeUnit.MILLISECONDS)
        } catch (_: RejectedExecutionException) {
            publicRecoveryPending.set(false)
            return
        }
        taskRef.set(task)
        if (closed || task.isDone) {
            pendingRecoveryTasks.remove(task)
            if (closed) {
                task.cancel(false)
            }
        } else {
            pendingRecoveryTasks.add(task)
        }
    }

    private fun performPublicRecoveryOnQueue() {
        if (closed) return
        val activeSubs = publicStream.subscriptions.filter { it.active }
        if (activeSubs.isEmpty()) {
            return
        }
        val channel = publicChannel
        if (!channel.needsRecovery && channel.queuedItems > 0) {
            return
        }

        val currentSnap = publicStream.lastEmittedSnapshot
            ?: buildProtocolPublicSnapshot(publicStream.cursor)
        val baseline = ProtocolPublicUpdate(
            snapshot = currentSnap,
            fromCursor = currentSnap.cursor,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )
        val enqueued = channel.enqueue(
            PublicDispatchItem.Broadcast(baseline, isFullSnapshot = true)
        )
        if (!enqueued && !closed) {
            schedulePublicRecovery(delayMillis = 50L)
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
                prevSnap.sleeveBloom != candidateSnap.sleeveBloom ||
                prevSnap.beamSwap != candidateSnap.beamSwap ||
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

            val enqueued = seatChannels[s].enqueue(SeatDispatchItem.Broadcast(update, isFullSnapshot = false))
            if (!enqueued && !closed) {
                scheduleSeatRecovery(s)
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

        val enqueued = publicChannel.enqueue(PublicDispatchItem.Broadcast(update, isFullSnapshot = false))
        if (!enqueued && !closed) {
            schedulePublicRecovery()
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
            for (t in pendingRecoveryTasks) {
                t.cancel(false)
            }
            pendingRecoveryTasks.clear()

            authority.close()
            if (ownsSerialExecutor) {
                serialExecutor.shutdownNow()
            }
            if (ownsAiExecutor) {
                internalAiExecutor.shutdownNow()
            }
            // 关闭所有派发通道（清空队列、后续入队直接返回 false，绝不阻塞在途任务）
            for (ch in seatChannels) {
                ch.close()
            }
            publicChannel.close()

            // 仅在拥有专属派发执行器时才 shutdown，注入的共享池绝不关闭
            if (ownsDispatchExecutors) {
                defaultSeatDispatchers?.forEach { it.shutdownNow() }
                defaultPublicDispatcher?.shutdownNow()
            }

            // 仅在拥有专属 completion 执行器时才 shutdown，注入的共享池绝不关闭
            if (ownsCompletionExecutor) {
                completionExecutor.shutdownNow()
            }

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

    /** 获取席位派发通道只读引用（供测试与测量工具读取指标） */
    fun getSeatChannel(seat: Int): SerialDispatchChannel<*>? = seatChannels.getOrNull(seat)

    /** 获取公开流派发通道只读引用（供测试与测量工具读取指标） */
    fun getPublicDispatchChannel(): SerialDispatchChannel<*> = publicChannel

    companion object {
        private val defaultRecoveryScheduler: ScheduledExecutorService by lazy {
            Executors.newSingleThreadScheduledExecutor { r ->
                Thread(r, "ServerTableHost-RecoveryScheduler").apply {
                    isDaemon = true
                }
            }
        }

        /**
         * 计算字符串在 JSON 序列化中的 UTF-8 保守上界字节长度（MR-07a）。
         *
         * 纯 CPU 计算，零内存分配，零 JSON 编码调用：
         * - 单字节 ASCII (非转义)：1 字节；
         * - 引号 '"' 与反斜杠 '\\'：转义后 2 字节；
         * - HTML 安全转义字符 ('<', '>', '&', '=', '\'')：Gson 默认 htmlSafe 编码为 \u00XX，计 6 字节；
         * - 行/段分隔符 (\u2028, \u2029)：Gson 转义为 \u2028 / \u2029，计 6 字节；
         * - 控制字符 (< 0x20)：\u00XX 转义 6 字节；
         * - 2 字节 UTF-8：2 字节；
         * - 3 字节 UTF-8 (汉字/日文假名)：3 字节；
         * - 4 字节代理对 (Emoji/扩展字符)：4 字节；
         * - 外层闭合双引号 ""：+2 字节；
         * - null 值：固定 4 字节 ("null")。
         */
        internal fun estimateStringUpperBytes(s: String?): Long {
            if (s == null) return 4L
            var len = 2L
            var i = 0
            val n = s.length
            while (i < n) {
                val c = s[i]
                when {
                    c.code < 0x80 -> {
                        len += when {
                            c == '"' || c == '\\' -> 2L
                            c == '<' || c == '>' || c == '&' || c == '=' || c == '\'' -> 6L
                            c.code < 0x20 -> 6L
                            else -> 1L
                        }
                        i++
                    }
                    c == '\u2028' || c == '\u2029' -> {
                        len += 6L
                        i++
                    }
                    c.code < 0x800 -> {
                        len += 2L
                        i++
                    }
                    Character.isHighSurrogate(c) && i + 1 < n && Character.isLowSurrogate(s[i + 1]) -> {
                        len += 4L
                        i += 2
                    }
                    else -> {
                        len += 3L
                        i++
                    }
                }
            }
            return len
        }

        /**
         * 单个引擎领域游戏事件保守上界估算（MR-07a）
         */
        internal fun estimateGameEventUpperBytes(event: GameEvent): Long {
            return when (event) {
                is GameEvent.TsumoWin -> {
                    var b = 300L + event.yakuList.sumOf { estimateStringUpperBytes(it) + 15L }
                    event.breakdown?.let { bd ->
                        b += 450L
                        b += bd.yakuLines.sumOf { estimateStringUpperBytes(it.first) + 35L }
                        b += bd.paymentLines.sumOf { estimateStringUpperBytes(it) + 15L }
                        b += bd.fu.lines.sumOf { estimateStringUpperBytes(it.description) + 30L }
                        b += estimateStringUpperBytes(bd.fu.specialNote)
                        b += estimateStringUpperBytes(bd.score.title)
                        b += estimateStringUpperBytes(bd.score.capReason)
                        b += estimateStringUpperBytes(bd.score.formulaText)
                        b += estimateStringUpperBytes(bd.score.rawBasePointsExact)
                        b += (bd.dora.indicatorTiles.size + bd.dora.uraTiles.size + bd.dora.redTiles.size) * 50L
                        b += (bd.dora.indicatorHits.size + bd.dora.uraHits.size) * 140L
                    }
                    b
                }
                is GameEvent.RonWin -> {
                    var b = 320L + event.yakuList.sumOf { estimateStringUpperBytes(it) + 15L }
                    event.breakdown?.let { bd ->
                        b += 450L
                        b += bd.yakuLines.sumOf { estimateStringUpperBytes(it.first) + 35L }
                        b += bd.paymentLines.sumOf { estimateStringUpperBytes(it) + 15L }
                        b += bd.fu.lines.sumOf { estimateStringUpperBytes(it.description) + 30L }
                        b += estimateStringUpperBytes(bd.fu.specialNote)
                        b += estimateStringUpperBytes(bd.score.title)
                        b += estimateStringUpperBytes(bd.score.capReason)
                        b += estimateStringUpperBytes(bd.score.formulaText)
                        b += estimateStringUpperBytes(bd.score.rawBasePointsExact)
                        b += (bd.dora.indicatorTiles.size + bd.dora.uraTiles.size + bd.dora.redTiles.size) * 50L
                        b += (bd.dora.indicatorHits.size + bd.dora.uraHits.size) * 140L
                    }
                    b
                }
                is GameEvent.ScoreSettled -> 260L
                is GameEvent.DrawGame -> 220L + event.tenpaiPlayers.size * 20L
                is GameEvent.RoundEnded -> 180L
                is GameEvent.MatchEnded -> 220L
                is GameEvent.ChiDeclared, is GameEvent.PonDeclared, is GameEvent.KanDeclared -> 300L
                is GameEvent.RiichiDeclared -> 220L
                else -> 200L
            }
        }

        /**
         * 单个传输层协议游戏事件保守上界估算（MR-07a）
         */
        internal fun estimateProtocolGameEventUpperBytes(event: ProtocolGameEvent): Long {
            return when (event) {
                is ProtocolGameEvent.TsumoWin -> {
                    var b = 300L + event.yakuList.sumOf { estimateStringUpperBytes(it) + 15L }
                    event.breakdown?.let { bd ->
                        b += 450L
                        b += bd.yakuLines.sumOf { estimateStringUpperBytes(it.first) + 35L }
                        b += bd.paymentLines.sumOf { estimateStringUpperBytes(it) + 15L }
                        b += bd.fu.lines.sumOf { estimateStringUpperBytes(it.description) + 30L }
                        b += estimateStringUpperBytes(bd.fu.specialNote)
                        b += estimateStringUpperBytes(bd.score.title)
                        b += estimateStringUpperBytes(bd.score.capReason)
                        b += estimateStringUpperBytes(bd.score.formulaText)
                        b += estimateStringUpperBytes(bd.score.rawBasePointsExact)
                        b += (bd.dora.indicatorTiles.size + bd.dora.uraTiles.size + bd.dora.redTiles.size) * 50L
                        b += (bd.dora.indicatorHits.size + bd.dora.uraHits.size) * 140L
                    }
                    b
                }
                is ProtocolGameEvent.RonWin -> {
                    var b = 320L + event.yakuList.sumOf { estimateStringUpperBytes(it) + 15L }
                    event.breakdown?.let { bd ->
                        b += 450L
                        b += bd.yakuLines.sumOf { estimateStringUpperBytes(it.first) + 35L }
                        b += bd.paymentLines.sumOf { estimateStringUpperBytes(it) + 15L }
                        b += bd.fu.lines.sumOf { estimateStringUpperBytes(it.description) + 30L }
                        b += estimateStringUpperBytes(bd.fu.specialNote)
                        b += estimateStringUpperBytes(bd.score.title)
                        b += estimateStringUpperBytes(bd.score.capReason)
                        b += estimateStringUpperBytes(bd.score.formulaText)
                        b += estimateStringUpperBytes(bd.score.rawBasePointsExact)
                        b += (bd.dora.indicatorTiles.size + bd.dora.uraTiles.size + bd.dora.redTiles.size) * 50L
                        b += (bd.dora.indicatorHits.size + bd.dora.uraHits.size) * 140L
                    }
                    b
                }
                is ProtocolGameEvent.ScoreSettled -> 260L
                is ProtocolGameEvent.DrawGame -> 220L + event.tenpaiPlayers.size * 20L
                is ProtocolGameEvent.RoundEnded -> 180L
                is ProtocolGameEvent.MatchEnded -> 220L
                is ProtocolGameEvent.ChiDeclared, is ProtocolGameEvent.PonDeclared, is ProtocolGameEvent.KanDeclared -> 300L
                is ProtocolGameEvent.RiichiDeclared -> 220L
                else -> 200L
            }
        }

        /**
         * 席位派发条目结构化保守上界估算（MR-07a）
         *
         * 规避在权威主线程执行昂贵 JSON 编码；根据快照与更新结构完整覆盖所有字段、内部集合
         * 以及动态可变长度字符串（sessionId, roundId, decisionId, options, yaku, breakdown 等），
         * 并以严格大于等于实际 JSON 载荷与对象保留体积的保守系数加权计量。
         *
         * 保证：对于任意合法更新，estimateSeatDispatchBytes(item) >= actualWireBytes，
         * 且紧密贴合实际体积（通常在 1.1x ~ 1.45x 之间），杜绝虚假配额或低估导致缓冲溢出。
         */
        internal fun estimateSeatDispatchBytes(item: SeatDispatchItem): Long {
            val update = item.update
            var bytes = 268L // 12B TCP 帧头 + 256B Push 信封基础开销
            bytes += update.visibleEvents.sumOf { estimateGameEventUpperBytes(it) }

            val snap = update.snapshot
            // 基础结构标量与核心字符串
            bytes += 250L
            bytes += estimateStringUpperBytes(snap.sessionId)
            bytes += estimateStringUpperBytes(snap.roundId)
            bytes += estimateStringUpperBytes(snap.syncEpoch)
            bytes += estimateStringUpperBytes(snap.settlementRoundId)

            val view = snap.view
            bytes += 250L
            bytes += view.doraIndicators.size * 50L
            bytes += view.scores.size * 15L
            bytes += view.riichiStatus.size * 10L
            bytes += view.handTileCounts.size * 10L
            bytes += view.discards.sumOf { it.size } * 50L
            bytes += view.melds.sumOf { it.size } * 180L
            bytes += view.myClosedTiles.size * 50L
            if (view.myDrawnTile != null) bytes += 50L
            if (view.lastDiscard != null) bytes += 50L
            bytes += view.mySleeveTiles.size * 50L
            bytes += view.myDiscardHistory.size * 50L
            bytes += view.legalActions.size * 120L

            // 决策与候选选项（包含动态 decisionId, roundId 与 optionId 字符串）
            bytes += 100L
            bytes += estimateStringUpperBytes(snap.decision.decisionId)
            bytes += estimateStringUpperBytes(snap.decision.roundId)
            bytes += snap.decision.options.sumOf { 80L + estimateStringUpperBytes(it.optionId) }

            // 时钟、出千点数与席位控制
            bytes += snap.clocks.size * 100L
            bytes += snap.cheatPoints.size * 15L
            bytes += snap.controls.size * 80L

            // 整场赛程记录
            snap.session?.let { sess ->
                bytes += 150L
                bytes += sess.roundRecords.sumOf {
                    180L + estimateStringUpperBytes(it.roundId) + estimateStringUpperBytes(it.resultId)
                }
                sess.finalResult?.let { res ->
                    bytes += 300L + estimateStringUpperBytes(res.reason)
                }
            }

            // 洗牌准备阶段
            snap.preparation?.let { prep ->
                bytes += 200L
                prep.selection?.let { sel ->
                    bytes += 100L + (sel.initialTargets.size + sel.drawTargets.size) * 35L
                }
            }

            // 落汗千术私有透视（含内部标记组与牌集合）
            snap.tileMark?.let { tm ->
                bytes += 150L
                bytes += estimateStringUpperBytes(tm.disabledReason)
                bytes += estimateStringUpperBytes(tm.decisionId)
                bytes += tm.selectableTileIds.size * 15L
                bytes += tm.activeGroups.sumOf { 120L + estimateStringUpperBytes(it.groupId) + it.tiles.size * 140L }
            }

            // 袖里藏花私有透视（含可选牌与未决方案）
            snap.sleeveBloom?.let { sb ->
                bytes += 150L
                bytes += estimateStringUpperBytes(sb.disabledReason)
                bytes += estimateStringUpperBytes(sb.decisionId)
                sb.pendingOffer?.let { offer ->
                    bytes += 150L + estimateStringUpperBytes(offer.offerId) + estimateStringUpperBytes(offer.decisionId)
                }
            }

            // 偷梁换柱私有透视（含可选牌与未决方案）
            snap.beamSwap?.let { bs ->
                bytes += 150L
                bytes += estimateStringUpperBytes(bs.disabledReason)
                bytes += estimateStringUpperBytes(bs.decisionId)
                bs.pendingOffer?.let { offer ->
                    bytes += 150L + estimateStringUpperBytes(offer.offerId) + estimateStringUpperBytes(offer.decisionId) +
                             offer.selectableHandTileIds.size * 15L + offer.candidates.size * 80L
                }
            }

            // 听牌与展示预览
            snap.presentationPreview?.let { pp ->
                bytes += 100L
                bytes += estimateStringUpperBytes(pp.roundId)
                bytes += estimateStringUpperBytes(pp.decisionId)
                bytes += pp.waits.size * 50L
                bytes += pp.riichiOptions.sumOf { 150L + estimateStringUpperBytes(it.optionId) + it.waits.size * 50L }
                bytes += pp.meldOptions.sumOf { 180L + estimateStringUpperBytes(it.optionId) + it.tiles.size * 50L }
            }

            // 结算事件（含 ScoreBreakdown 动态字符串）
            bytes += snap.settlementEvents.sumOf { estimateGameEventUpperBytes(it) }
            return bytes
        }

        /**
         * 公开流派发条目结构化保守上界估算（MR-07a）
         */
        internal fun estimatePublicDispatchBytes(item: PublicDispatchItem): Long {
            val update = item.update
            var bytes = 268L // 12B TCP 帧头 + 256B Push 信封基础开销
            bytes += update.visibleEvents.sumOf { estimateProtocolGameEventUpperBytes(it) }

            val snap = update.snapshot
            bytes += 250L
            bytes += estimateStringUpperBytes(snap.sessionId)
            bytes += estimateStringUpperBytes(snap.roundId)
            bytes += estimateStringUpperBytes(snap.syncEpoch)
            bytes += estimateStringUpperBytes(snap.settlementRoundId)

            val board = snap.board
            if (board != null) {
                bytes += 250L
                bytes += board.doraIndicators.size * 50L
                bytes += board.scores.size * 15L
                bytes += board.riichiStatus.size * 10L
                bytes += board.handTileCounts.size * 10L
                bytes += board.discards.sumOf { it.size } * 42L
                bytes += board.melds.sumOf { it.size } * 180L
                if (board.lastDiscard != null) bytes += 50L
            }

            // 公开整场赛程概况（包含局历史记录增长与终局排名）
            bytes += 150L
            bytes += snap.session.roundRecords.sumOf { rec ->
                var recBytes = 180L + estimateStringUpperBytes(rec.roundId) + estimateStringUpperBytes(rec.resultId)
                val outcome = rec.outcome
                if (outcome is ProtocolRoundOutcome.AbortiveDraw) {
                    recBytes += estimateStringUpperBytes(outcome.reason)
                }
                recBytes
            }
            snap.session.finalResult?.let { res ->
                bytes += 300L + estimateStringUpperBytes(res.reason)
            }

            // 权威时钟视图
            bytes += snap.clocks.size * 100L

            // 结算事件（含 ScoreBreakdown）
            bytes += snap.settlementEvents.sumOf { estimateProtocolGameEventUpperBytes(it) }
            return bytes
        }
    }
}

package com.majiang.server.room

import com.majiang.access.AccessCapabilities
import com.majiang.access.ActionReceipt
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.ReceiptStatus
import com.majiang.application.ActionChoice
import com.majiang.application.PlayerDecision
import com.majiang.event.GameEvent
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomOffTurnRequest
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.match.SessionLifecycle
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerApiException
import com.majiang.server.host.HostDispatchPools
import com.majiang.server.host.ServerTableHost
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolRoomMember
import java.util.Collections
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadFactory
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * 传输中立的权威共享房间服务 (SharedRoom)
 *
 * 遵循 V4 架构契约与 TASK-ANTI-R4B1ROOMFIX1 规范：
 * 1. 业务逻辑下沉与队列共享：进程内单机绑定与远端网络绑定委托完全相同的房间成员、就绪状态、
 *    规则配置、生命周期与 TableAuthority 启动逻辑，所有房间操作严格共享 [ServerTableHost] 的串行权威队列；
 * 2. 零调用者线程锁：严禁在业务方法上加持 @Synchronized，外部监听器通知派发移出权威执行临界区；
 * 3. 确定性生命周期：LOBBY -> PREPARING / PLAYING -> SETTLED -> FINISHED -> CLOSED，
 *    生命周期直接基于权威状态提交原子推导，拒绝非同步延迟查询；
 * 4. 房主在 LOBBY 期就地更新配置：只有领域权威真正接受配置后才原子提交房间配置并重置就绪，广播版本递增；
 * 5. 准入冻结与就绪裁决：开赛前验证全员真人就绪，开赛动作在队列内冻结席位并补位电脑 AI；失败时事务回滚；
 * 6. 离开语义隔离：房主离开解散房间，普通成员大厅期离开释放席位、对局期离开转为 AI 托管；显式离开必然轮转 Token 与代次；
 * 7. 有界合并通知队列：观察者通知采用有界最新视图合并 (latest-full-view coalescing) 与独立单观察者路径，
 *    慢观察者不阻塞权威队列、返回 Future 或其他观察者，且确保 CLOSED 终结事件不被提前丢弃；
 * 8. 确定性房主离开与关闭：调用方优先取得 APPLIED 成功回执，后台安全延后释放资源，杜绝资源竞态与泄漏；
 * 9. 执行期绑定校验：所有包装方法支持 [sessionValidator]，过时绑定的排队任务执行时安全拒绝。
 */
class SharedRoom @JvmOverloads constructor(
    val roomId: String,
    val hostPlayerName: String = "Host",
    initialConfig: ProtocolRoomConfig = ProtocolRoomConfig(),
    val isSingleplayer: Boolean = false,
    val clock: MonotonicClock = SystemMonotonicClock,
    serialExecutor: ExecutorService? = null,
    controllers: List<SeatController>? = null,
    val hostSeat: Int = 0,
    initialState: GameState? = null,
    val capacityPolicy: RoomCapacityPolicy = RoomCapacityPolicy(),
    val hostDispatch: HostDispatchPools? = null
) : AutoCloseable {

    private val isClosed = AtomicBoolean(false)
    val closed: Boolean get() = isClosed.get()

    private val isShutdownDone = AtomicBoolean(false)

    private var roomVersion: Long = 1L
    val version: Long get() = roomVersion

    val seatOccupied = BooleanArray(4) { false }
    val seatConnected = BooleanArray(4) { false }
    val playerNames = arrayOfNulls<String>(4)
    private val seatResumeTokensArray = Array(4) { UUID.randomUUID().toString() }
    val isReady = BooleanArray(4) { false }
    val isHuman = BooleanArray(4) { false }
    private val seatMemberGenerationsArray = LongArray(4) { 1L }
    private val seatDisconnectedGenerationsArray = LongArray(4) { 0L }

    /** 席位当前成员代次集合 (Member Generations, D-03) - 返回防御性副本，禁止外部越权篡改内部代次真值 */
    val seatMemberGenerations: LongArray get() = seatMemberGenerationsArray.copyOf()

    /** 席位已断开成员代次集合 (D-02 / D-03) - 返回防御性副本，禁止外部越权篡改内部断开代次真值 */
    val seatDisconnectedGenerations: LongArray get() = seatDisconnectedGenerationsArray.copyOf()

    /** 席位恢复令牌集合 (D-03) - 返回防御性副本，禁止外部越权篡改内部令牌 */
    val resumeTokens: Array<String> get() = seatResumeTokensArray.copyOf()

    /**
     * @deprecated 历史旧公开入口。现已收敛为只读防御性副本，直接赋值修改元素不影响 SharedRoom 内部代次。
     * 请改用 [seatMemberGeneration(seat)] 或 [seatMemberGenerations]。
     */
    @Deprecated("Use seatMemberGeneration(seat) or seatMemberGenerations instead", ReplaceWith("seatMemberGenerations"))
    val bindingGenerations: LongArray get() = seatMemberGenerationsArray.copyOf()

    /** 获取指定席位当前成员代次 (D-03) */
    fun seatMemberGeneration(seat: Int): Long {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        return seatMemberGenerationsArray[seat]
    }

    /** 获取指定席位已断开代次 (D-03) */
    fun seatDisconnectedGeneration(seat: Int): Long {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        return seatDisconnectedGenerationsArray[seat]
    }

    /** 获取指定席位恢复令牌 (D-03) */
    fun seatResumeToken(seat: Int): String {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        return seatResumeTokensArray[seat]
    }

    /** 获取指定观战者当前成员代次 (D-03) */
    fun spectatorMemberGeneration(spectatorId: String): Long? {
        return spectatorsMap[spectatorId]?.generation
    }

    /** 获取指定观战者已断开代次 (D-03) */
    fun spectatorLastDisconnectedGeneration(spectatorId: String): Long? {
        return spectatorsMap[spectatorId]?.lastDisconnectedGeneration
    }

    /** 检查指定观战者当前是否处于连接状态 (D-03) */
    fun isSpectatorConnected(spectatorId: String): Boolean {
        return spectatorsMap[spectatorId]?.isConnected == true
    }

    /** 获取指定观战者恢复令牌 (模块内授权使用，D-03) */
    internal fun spectatorResumeToken(spectatorId: String): String? {
        return spectatorsMap[spectatorId]?.resumeToken
    }

    @Volatile
    var idleSinceMillis: Long? = null
        private set

    @Volatile
    var idleEpoch: Long = 1L
        private set

    @Volatile
    var everConnectedHuman: Boolean = false
        private set

    /** 观战者成员对外只读视图 (D-03 防篡改投影) - 无任何公共 var 或 token 写入口 */
    data class SpectatorMemberView(
        val spectatorId: String,
        val name: String,
        val generation: Long = 1L,
        val isConnected: Boolean = false,
        val lastDisconnectedGeneration: Long = 0L
    )

    @Deprecated("Use SpectatorMemberView instead", ReplaceWith("SpectatorMemberView"))
    typealias SpectatorMember = SpectatorMemberView

    internal data class InternalSpectatorMember(
        val spectatorId: String,
        val name: String,
        @Volatile var resumeToken: String,
        @Volatile var generation: Long = 1L,
        @Volatile var isConnected: Boolean = false,
        @Volatile var lastDisconnectedGeneration: Long = 0L
    ) {
        fun toView(): SpectatorMemberView = SpectatorMemberView(
            spectatorId = spectatorId,
            name = name,
            generation = generation,
            isConnected = isConnected,
            lastDisconnectedGeneration = lastDisconnectedGeneration
        )
    }

    private val spectatorsMap = ConcurrentHashMap<String, InternalSpectatorMember>()

    /** 观战者成员只读视图映射 (D-03 防篡改) - 返回不可变只读视图映射，禁止外部绕过准入和权威队列修改内部状态 */
    val spectators: Map<String, SpectatorMemberView>
        get() = Collections.unmodifiableMap(spectatorsMap.mapValues { it.value.toView() })

    val spectatorCount: Int get() = spectatorsMap.size

    fun hasSpectator(spectatorId: String): Boolean = spectatorsMap.containsKey(spectatorId)

    fun spectatorMember(spectatorId: String): SpectatorMemberView? = spectatorsMap[spectatorId]?.toView()

    /** 观战绑定被新连接替换的监听器（在权威队列外派发，用于主动终结旧观战连接） */
    private val spectatorDisplacedListeners = CopyOnWriteArrayList<(spectatorId: String, newGeneration: Long) -> Unit>()

    /**
     * 注册观战顶替通知：resumeSpectator 真实替换绑定时主动回调，
     * 传输层/绑定层据此立即终结旧观战连接 (DISPLACED_BY_NEW_CONNECTION)，不等待下一次游戏动作通知。
     */
    fun onSpectatorDisplaced(listener: (spectatorId: String, newGeneration: Long) -> Unit): AutoCloseable {
        spectatorDisplacedListeners.add(listener)
        return AutoCloseable {
            spectatorDisplacedListeners.remove(listener)
        }
    }

    internal fun notifySpectatorDisplaced(spectatorId: String, newGeneration: Long) {
        for (listener in spectatorDisplacedListeners) {
            try {
                listener(spectatorId, newGeneration)
            } catch (_: Throwable) {
            }
        }
    }

    /** 席位绑定被新连接替换的监听器（在权威队列外派发，用于主动终结旧席位连接） */
    private val seatDisplacedListeners = CopyOnWriteArrayList<(seat: Int, newGeneration: Long) -> Unit>()

    /**
     * 注册席位顶替通知：resumeSession 真实替换绑定时主动回调，
     * 传输层/绑定层据此立即终结旧席位连接 (DISPLACED_BY_NEW_CONNECTION)，不等待下一次游戏动作通知。
     */
    fun onSeatDisplaced(listener: (seat: Int, newGeneration: Long) -> Unit): AutoCloseable {
        seatDisplacedListeners.add(listener)
        return AutoCloseable {
            seatDisplacedListeners.remove(listener)
        }
    }

    internal fun notifySeatDisplaced(seat: Int, newGeneration: Long) {
        for (listener in seatDisplacedListeners) {
            try {
                listener(seat, newGeneration)
            } catch (_: Throwable) {
            }
        }
    }

    @Volatile
    var roomConfig: ProtocolRoomConfig = initialConfig
        private set

    @Volatile
    var lifecycle: ProtocolRoomLifecycle = ProtocolRoomLifecycle.LOBBY
        private set

    @Volatile
    var latestRoomInfo: ProtocolRoomInfo
        private set

    private val ownsNotificationExecutor: Boolean = (hostDispatch == null)
    private val defaultNotificationExecutor: ExecutorService? = if (ownsNotificationExecutor) {
        Executors.newCachedThreadPool(
            ThreadFactory { r -> Thread(r, "SharedRoom-$roomId-Notification").apply { isDaemon = true } }
        )
    } else null

    internal val roomNotificationExecutor: ExecutorService = hostDispatch?.dispatchExecutor ?: defaultNotificationExecutor!!

    private val roomSubscribers = CopyOnWriteArrayList<RoomSubscriber>()
    private val subscriberRejectCounter = AtomicLong(0L)
    val roomSubscriberRejectCount: Long get() = subscriberRejectCounter.get()

    val tableHost: ServerTableHost

    init {
        require(hostSeat in 0..3) { "hostSeat must be in 0..3, got $hostSeat" }
        val domainMatchConfig = EngineProtocolMapper.toEngineConfig(initialConfig.matchConfig)
        val domainTimeControl = EngineProtocolMapper.toEngineTimeControlConfig(initialConfig.timeControl)

        val resolvedControllers = controllers ?: if (isSingleplayer) {
            (0..3).map { s ->
                if (s == hostSeat) SeatController.LocalHuman() else SeatController.Computer()
            }
        } else {
            listOf(
                SeatController.LocalHuman(),
                SeatController.LocalHuman(),
                SeatController.LocalHuman(),
                SeatController.LocalHuman()
            )
        }

        seatOccupied[hostSeat] = true
        playerNames[hostSeat] = hostPlayerName
        isHuman[hostSeat] = true
        isReady[hostSeat] = isSingleplayer

        if (isSingleplayer) {
            seatConnected[hostSeat] = true
            everConnectedHuman = true
            idleSinceMillis = null
            idleEpoch = 1L
            for (s in 0..3) {
                if (s != hostSeat) {
                    seatOccupied[s] = true
                    playerNames[s] = "AI-$s"
                    isHuman[s] = false
                    isReady[s] = true
                }
            }
        } else {
            seatConnected[hostSeat] = false
            everConnectedHuman = false
            idleSinceMillis = clock.nowMillis()
            idleEpoch = 1L
        }

        tableHost = ServerTableHost(
            config = domainMatchConfig,
            timeControl = domainTimeControl,
            clock = clock,
            controllers = resolvedControllers,
            adminSeat = hostSeat,
            serialExecutor = serialExecutor,
            initialState = initialState,
            sharedDispatch = hostDispatch?.dispatchExecutor,
            sharedCompletion = hostDispatch?.completionExecutor,
            seatChannelCapacityItems = hostDispatch?.seatChannelCapacityItems ?: 0,
            seatChannelCapacityBytes = hostDispatch?.seatChannelCapacityBytes ?: 0L,
            publicChannelCapacityItems = hostDispatch?.publicChannelCapacityItems ?: 0,
            publicChannelCapacityBytes = hostDispatch?.publicChannelCapacityBytes ?: 0L,
            channelDrainBatchSize = hostDispatch?.channelDrainBatchSize ?: 16
        )

        tableHost.onStateCommitted = {
            updateLifecycleFromHostOnQueue()
        }

        if (initialState != null) {
            val sessionLifecycle = tableHost.authority.sessionLifecycle
            lifecycle = when (sessionLifecycle) {
                SessionLifecycle.ROUND_RESULT,
                SessionLifecycle.WAITING_DEALER_CHOICE,
                SessionLifecycle.WAITING_READY -> ProtocolRoomLifecycle.SETTLED
                SessionLifecycle.FINISHED -> ProtocolRoomLifecycle.FINISHED
                SessionLifecycle.CLOSED -> ProtocolRoomLifecycle.CLOSED
                else -> {
                    val isPrep = tableHost.authority.isPreparationActive()
                    if (isPrep) ProtocolRoomLifecycle.PREPARING else ProtocolRoomLifecycle.PLAYING
                }
            }
        }

        latestRoomInfo = buildRoomInfoOnQueue()
    }

    // ==========================================
    // 观察者有界合并派发器 (Bounded Coalescing Delivery)
    // ==========================================

    private class RoomSubscriber(
        private val listener: (ProtocolRoomInfo) -> Unit,
        private val executor: ExecutorService,
        private val onReject: () -> Unit = {}
    ) {
        @Volatile
        private var unregistered = false
        private val lock = Any()
        private var highestVersion = -1L
        private var pending: ProtocolRoomInfo? = null
        private var terminal: ProtocolRoomInfo? = null
        private var isRunning = false

        fun enqueue(info: ProtocolRoomInfo) {
            val shouldDrain: Boolean
            synchronized(lock) {
                if (unregistered) return
                if (terminal != null) {
                    // terminal 已经是终结状态，不再接受任何新数据；若此前执行器拒绝导致未运行，重新拉起 drain
                    if (!isRunning) {
                        isRunning = true
                        shouldDrain = true
                    } else {
                        shouldDrain = false
                    }
                } else {
                    if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
                        terminal = info
                        pending = null
                    } else {
                        // 防乱序防御：仅接受单调递增的新版本，拒绝过时或相同版本覆盖
                        if (info.version <= highestVersion) return
                        highestVersion = info.version
                        pending = info
                    }
                    if (!isRunning) {
                        isRunning = true
                        shouldDrain = true
                    } else {
                        shouldDrain = false
                    }
                }
            }
            if (shouldDrain) {
                drain()
            }
        }

        private fun drain() {
            try {
                executor.execute {
                    while (true) {
                        val next: ProtocolRoomInfo
                        val isLast: Boolean
                        synchronized(lock) {
                            if (unregistered) {
                                isRunning = false
                                pending = null
                                terminal = null
                                return@execute
                            }
                            if (pending != null) {
                                next = pending!!
                                pending = null
                                isLast = false
                            } else if (terminal != null) {
                                next = terminal!!
                                terminal = null
                                isLast = true
                            } else {
                                isRunning = false
                                return@execute
                            }
                        }
                        try {
                            if (!unregistered) {
                                listener(next)
                            }
                        } catch (_: Throwable) {
                        }
                        if (isLast) {
                            synchronized(lock) {
                                unregistered = true
                                isRunning = false
                                pending = null
                                terminal = null
                                return@execute
                            }
                        }
                    }
                }
            } catch (_: RejectedExecutionException) {
                synchronized(lock) {
                    isRunning = false
                }
                onReject()
            }
        }

        fun unregister() {
            synchronized(lock) {
                unregistered = true
                pending = null
                terminal = null
            }
        }
    }

    // ==========================================
    // 权威队列内生命周期流转与视图构建
    // ==========================================

    private fun updateLifecycleFromHostOnQueue() {
        if (lifecycle == ProtocolRoomLifecycle.CLOSED) return
        if (lifecycle == ProtocolRoomLifecycle.LOBBY) return

        val prevLifecycle = lifecycle
        val sessionLifecycle = tableHost.authority.sessionLifecycle
        val newLifecycle = when (sessionLifecycle) {
            SessionLifecycle.ROUND_RESULT,
            SessionLifecycle.WAITING_DEALER_CHOICE,
            SessionLifecycle.WAITING_READY -> ProtocolRoomLifecycle.SETTLED
            SessionLifecycle.FINISHED -> ProtocolRoomLifecycle.FINISHED
            SessionLifecycle.CLOSED -> ProtocolRoomLifecycle.CLOSED
            else -> {
                val isPrep = tableHost.authority.isPreparationActive()
                if (isPrep) ProtocolRoomLifecycle.PREPARING else ProtocolRoomLifecycle.PLAYING
            }
        }

        if (newLifecycle != prevLifecycle) {
            lifecycle = newLifecycle
            roomVersion++
            val info = buildRoomInfoOnQueue()
            latestRoomInfo = info
            dispatchRoomUpdate(info)
        }
    }

    private fun buildRoomInfoOnQueue(): ProtocolRoomInfo {
        val members = ArrayList<ProtocolRoomMember>(4)
        for (s in 0..3) {
            if (seatOccupied[s]) {
                val name = playerNames[s] ?: if (isHuman[s]) "Player-$s" else "AI-$s"
                val role = if (isHuman[s]) "HUMAN" else "COMPUTER"
                members.add(
                    ProtocolRoomMember(
                        seat = s,
                        playerName = name,
                        isHost = (s == hostSeat),
                        isReady = isReady[s],
                        role = role
                    )
                )
            }
        }
        return ProtocolRoomInfo(
            roomId = roomId,
            lifecycle = lifecycle,
            version = roomVersion,
            hostSeat = hostSeat,
            members = Collections.unmodifiableList(members),
            config = roomConfig
        )
    }

    private fun dispatchRoomUpdate(info: ProtocolRoomInfo) {
        for (sub in roomSubscribers) {
            sub.enqueue(info)
        }
    }

    /**
     * 延后释放房间底层宿主资源 (deferredShutdown)
     *
     * 架构设计说明 (MR-07a 返修 C3)：
     * - 共享模式下 (ownsNotificationExecutor == false)，若将 tableHost.close() 提交到共享 dispatch 池，
     *   在慢监听器占满 8 个派发线程时，leave() 路径的宿主关闭会被无限延迟，导致 pendingFutures 无法及时异常完成、
     *   serial 线程残留。因此在共享模式下直接同步内联执行（tableHost.close()/authority.close() 全程非阻塞，安全）；
     * - 默认模式下 (ownsNotificationExecutor == true)，保持向自有独立 Cached 线程池异步提交并最终关闭该池。
     */
    private fun deferredShutdown() {
        if (isShutdownDone.compareAndSet(false, true)) {
            val shutdownAction = {
                try {
                    tableHost.close()
                } catch (_: Throwable) {
                } finally {
                    if (ownsNotificationExecutor) {
                        defaultNotificationExecutor?.shutdown()
                    }
                }
            }
            if (!ownsNotificationExecutor) {
                // 共享模式：同步内联执行，防止共享 dispatch 池饱和时 leave() 延迟关闭宿主
                shutdownAction()
            } else {
                // 默认模式：提交至专属通知池异步执行
                try {
                    roomNotificationExecutor.execute(shutdownAction)
                } catch (_: RejectedExecutionException) {
                    shutdownAction()
                }
            }
        }
    }

    // ==========================================
    // 连接状态与空闲跟踪（权威队列内）
    // ==========================================

    fun connectedHumanCount(): Int {
        var count = 0
        for (s in 0..3) {
            if (seatOccupied[s] && isHuman[s] && seatConnected[s]) {
                count++
            }
        }
        return count
    }

    fun connectedSpectatorCount(): Int {
        var count = 0
        for (m in spectatorsMap.values) {
            if (m.isConnected) {
                count++
            }
        }
        return count
    }

    private fun recomputeIdleOnQueue(nowMillis: Long = clock.nowMillis()) {
        val anyConnected = connectedHumanCount() > 0 || connectedSpectatorCount() > 0
        if (anyConnected) {
            idleSinceMillis = null
        } else {
            if (idleSinceMillis == null) {
                idleSinceMillis = nowMillis
            }
        }
        idleEpoch++
    }

    /**
     * 传输连接成功绑定席位通知（幂等，在 TableHost 权威队列中串行执行，核对 expectedGeneration 与会话有效性）
     */
    fun markSeatConnected(
        seat: Int,
        expectedGeneration: Long,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<Unit> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation
            if (sessionValidator?.invoke() == false) return@enqueueOperation
            if (seat in 0..3 &&
                seatOccupied[seat] &&
                isHuman[seat] &&
                seatMemberGenerationsArray[seat] == expectedGeneration &&
                expectedGeneration > seatDisconnectedGenerationsArray[seat]
            ) {
                seatConnected[seat] = true
                everConnectedHuman = true
                recomputeIdleOnQueue()
            }
        }
    }

    /**
     * 传输连接成功绑定观战者通知（幂等，在 TableHost 权威队列中串行执行，核对 expectedGeneration 与会话有效性）
     */
    fun markSpectatorConnected(
        spectatorId: String,
        expectedGeneration: Long,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<Unit> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation
            if (sessionValidator?.invoke() == false) return@enqueueOperation
            val member = spectatorsMap[spectatorId] ?: return@enqueueOperation
            if (member.generation == expectedGeneration && expectedGeneration > member.lastDisconnectedGeneration) {
                member.isConnected = true
                everConnectedHuman = true
                recomputeIdleOnQueue()
            }
        }
    }

    /**
     * 权威队列内串行对账席位与观战者连接真值 (MR-06 任务 E / R4 兜底)
     *
     * 以连接表快照为准，清除无人认领的 seatConnected 与 member.isConnected，并重算空闲。
     */
    fun reconcileConnectivityOnQueue(
        aliveSeats: Map<Int, Long>,
        aliveSpectatorIds: Set<String>,
        sampledSeatGenerations: Map<Int, Long>? = null,
        connectivitySupplier: (() -> Pair<Map<Int, Long>, Set<String>>)? = null
    ): CompletableFuture<Unit> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation
            val (effectiveSeats, effectiveSpectators) = if (connectivitySupplier != null) {
                connectivitySupplier()
            } else {
                Pair(aliveSeats, aliveSpectatorIds)
            }
            var changed = false
            for (seat in 0..3) {
                val currentGen = seatMemberGenerationsArray[seat]
                val sampledGen = sampledSeatGenerations?.get(seat)
                // 若提供了采样时代次且当前代次已推进，说明在采样后有新绑定/重连，该旧采样对该席位失效
                if (sampledGen != null && currentGen > sampledGen) {
                    continue
                }
                val expectedGen = effectiveSeats[seat]
                // 若期望代次低于当前代次，说明此条记录来自陈旧代次，不可将当前更新的连接置为断开
                if (expectedGen != null && expectedGen < currentGen) {
                    continue
                }
                if (seatConnected[seat]) {
                    if (expectedGen == null || expectedGen != currentGen) {
                        seatConnected[seat] = false
                        if (currentGen > seatDisconnectedGenerationsArray[seat]) {
                            seatDisconnectedGenerationsArray[seat] = currentGen
                        }
                        changed = true
                    }
                } else {
                    // 只清不补 -> 补：若席位当前被人类占用，且存在匹配当前代次的存活连接，则补回连接状态
                    if (expectedGen != null && expectedGen == currentGen && seatOccupied[seat] && isHuman[seat]) {
                        seatConnected[seat] = true
                        changed = true
                    }
                }
            }
            for ((specId, member) in spectatorsMap) {
                val isAlive = specId in effectiveSpectators
                if (member.isConnected && !isAlive) {
                    member.isConnected = false
                    if (member.generation > member.lastDisconnectedGeneration) {
                        member.lastDisconnectedGeneration = member.generation
                    }
                    changed = true
                } else if (!member.isConnected && isAlive) {
                    member.isConnected = true
                    changed = true
                }
            }
            if (changed) {
                recomputeIdleOnQueue()
            }
        }
    }

    // ==========================================
    // 大厅与房间成员管理（全部串行化在 TableHost 执行队列）
    // ==========================================

    fun join(
        playerName: String,
        preferSeat: Int? = null,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<Pair<Int, String>> {
        if (isClosed.get()) return CompletableFuture.failedFuture<Pair<Int, String>>(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            if (lifecycle != ProtocolRoomLifecycle.LOBBY) throw ServerApiException(ApiErrors.ROOM_FULL, "ROOM_FULL")

            val targetSeat = if (preferSeat != null && preferSeat in 0..3 && preferSeat != hostSeat && !seatOccupied[preferSeat]) {
                preferSeat
            } else {
                val start = if (preferSeat != null && preferSeat in 0..3) preferSeat + 1 else 0
                val candidates = (start..3).toList() + (0 until start).toList()
                candidates.firstOrNull { it != hostSeat && !seatOccupied[it] }
            } ?: throw ServerApiException(ApiErrors.ROOM_FULL, "ROOM_FULL")

            seatOccupied[targetSeat] = true
            playerNames[targetSeat] = playerName
            isHuman[targetSeat] = true
            isReady[targetSeat] = false
            seatConnected[targetSeat] = true
            recomputeIdleOnQueue()
            seatMemberGenerationsArray[targetSeat]++
            roomVersion++

            val info = buildRoomInfoOnQueue()
            latestRoomInfo = info
            dispatchRoomUpdate(info)

            Pair(targetSeat, seatResumeTokensArray[targetSeat])
        }
    }

    fun setReady(
        seat: Int,
        ready: Boolean,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolActionReceipt> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) {
                return@enqueueOperation ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "ROOM_CLOSED")
            }
            if (sessionValidator?.invoke() == false) {
                return@enqueueOperation ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            }
            if (seat !in 0..3 || !seatOccupied[seat]) {
                return@enqueueOperation ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "NOT_MEMBER")
            }
            if (lifecycle != ProtocolRoomLifecycle.LOBBY) {
                return@enqueueOperation ProtocolActionReceipt("setReady", ProtocolReceiptStatus.APPLIED)
            }

            if (isReady[seat] != ready) {
                isReady[seat] = ready
                roomVersion++
                val info = buildRoomInfoOnQueue()
                latestRoomInfo = info
                dispatchRoomUpdate(info)
            }
            ProtocolActionReceipt("setReady", ProtocolReceiptStatus.APPLIED)
        }
    }

    fun updateConfig(
        seat: Int,
        newConfig: ProtocolRoomConfig,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolActionReceipt> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) {
                return@enqueueOperation ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "ROOM_CLOSED")
            }
            if (sessionValidator?.invoke() == false) {
                return@enqueueOperation ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            }
            if (seat != hostSeat) {
                return@enqueueOperation ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "PERMISSION_DENIED")
            }
            if (lifecycle != ProtocolRoomLifecycle.LOBBY) {
                return@enqueueOperation ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "INVALID_PHASE")
            }

            val domainMatchConfig = EngineProtocolMapper.toEngineConfig(newConfig.matchConfig)
            val domainTimeControl = EngineProtocolMapper.toEngineTimeControlConfig(newConfig.timeControl)

            val accepted = tableHost.performUpdateConfigOnQueue(domainMatchConfig, domainTimeControl)
            if (!accepted) {
                return@enqueueOperation ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "INVALID_PHASE")
            }

            this.roomConfig = newConfig

            if (!isSingleplayer) {
                for (s in 0..3) {
                    if (seatOccupied[s] && isHuman[s]) {
                        isReady[s] = false
                    }
                }
            }

            roomVersion++
            val info = buildRoomInfoOnQueue()
            latestRoomInfo = info
            dispatchRoomUpdate(info)

            ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.APPLIED)
        }
    }

    fun startMatch(
        seat: Int,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolActionReceipt> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) {
                return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "ROOM_CLOSED")
            }
            if (sessionValidator?.invoke() == false) {
                return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            }
            if (seat != hostSeat) {
                return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "PERMISSION_DENIED")
            }

            // 若对局已开始，幂等执行（不重复发牌、不改变局号）
            if (lifecycle == ProtocolRoomLifecycle.PLAYING || lifecycle == ProtocolRoomLifecycle.PREPARING) {
                val receipt = tableHost.performStartOnQueue(hostSeat)
                return@enqueueOperation EngineProtocolMapper.toProtocolReceipt(receipt)
            }

            if (lifecycle != ProtocolRoomLifecycle.LOBBY) {
                return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "INVALID_PHASE")
            }

            // 开赛前置条件防御校验（避免在暂停或已结束状态下修改房间成员）
            if (tableHost.isPaused) {
                return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "HOST_PAUSED")
            }
            if (tableHost.authority.sessionLifecycle == SessionLifecycle.FINISHED) {
                return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "MATCH_FINISHED")
            }

            // 联机模式下检查所有在座真人玩家是否均已就绪
            if (!isSingleplayer) {
                for (s in 0..3) {
                    if (seatOccupied[s] && isHuman[s] && !isReady[s]) {
                        return@enqueueOperation ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "NOT_READY")
                    }
                }
            }

            // 暂存空缺席位填补 AI（事务性暂存，若权威 start 失败则无损回滚）
            val newlyFilledAiSeats = mutableListOf<Int>()
            if (!isSingleplayer) {
                for (s in 0..3) {
                    if (s != hostSeat && !seatOccupied[s]) {
                        newlyFilledAiSeats.add(s)
                        seatOccupied[s] = true
                        playerNames[s] = "AI-$s"
                        isHuman[s] = false
                        isReady[s] = true
                        tableHost.performAssignComputerSeatOnQueue(s)
                    }
                }
            }

            val receipt = tableHost.performStartOnQueue(hostSeat)
            if (receipt.status == ReceiptStatus.APPLIED) {
                val isPrep = tableHost.authority.isPreparationActive()
                lifecycle = if (isPrep) ProtocolRoomLifecycle.PREPARING else ProtocolRoomLifecycle.PLAYING
                roomVersion++
                val info = buildRoomInfoOnQueue()
                latestRoomInfo = info
                dispatchRoomUpdate(info)
            } else {
                // 权威开赛失败，回滚暂存填补的 AI 席位，恢复准入空位与成员状态
                for (s in newlyFilledAiSeats) {
                    seatOccupied[s] = false
                    playerNames[s] = null
                    isHuman[s] = false
                    isReady[s] = false
                    tableHost.performResetSeatControllerOnQueue(s)
                }
            }
            EngineProtocolMapper.toProtocolReceipt(receipt)
        }
    }

    fun leave(
        seat: Int,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolActionReceipt> {
        val opFuture = tableHost.enqueueOperation {
            if (isClosed.get()) {
                return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            }
            if (sessionValidator?.invoke() == false) {
                return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            }
            if (seat !in 0..3 || !seatOccupied[seat]) {
                return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "NOT_MEMBER")
            }

            // 显式退出在所有生命周期阶段均作废原会话令牌与连接代次
            seatResumeTokensArray[seat] = UUID.randomUUID().toString()
            seatMemberGenerationsArray[seat]++

            if (seat == hostSeat) {
                // 房主退出：标记解散房间，通知其他成员 ROOM_CLOSED
                if (isClosed.compareAndSet(false, true)) {
                    lifecycle = ProtocolRoomLifecycle.CLOSED
                    roomVersion++
                    val terminalInfo = latestRoomInfo.copy(
                        lifecycle = ProtocolRoomLifecycle.CLOSED,
                        version = roomVersion
                    )
                    latestRoomInfo = terminalInfo
                    dispatchRoomUpdate(terminalInfo)
                }
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
            } else {
                if (lifecycle == ProtocolRoomLifecycle.LOBBY) {
                    // 大厅期普通成员退出：释放席位
                    seatOccupied[seat] = false
                    playerNames[seat] = null
                    isHuman[seat] = false
                    isReady[seat] = false
                    seatConnected[seat] = false
                    recomputeIdleOnQueue()
                    roomVersion++
                    val info = buildRoomInfoOnQueue()
                    latestRoomInfo = info
                    dispatchRoomUpdate(info)
                    ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
                } else {
                    // 对局中普通成员退出：转为 AI 托管
                    tableHost.performDisconnectOnQueue(seat)
                    isHuman[seat] = false
                    seatConnected[seat] = false
                    recomputeIdleOnQueue()
                    ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
                }
            }
        }

        // 确保调用方在 tableHost 彻底关闭前优先收到成功的 leave 回执，
        // 延后在通知线程池异步释放 tableHost 资源，避免关闭取消正在返回的成功回执
        return opFuture.thenApply { receipt ->
            if (seat == hostSeat && receipt.status == ProtocolReceiptStatus.APPLIED) {
                deferredShutdown()
            }
            receipt
        }
    }

    data class ResumeSessionResult(
        val seat: Int,
        val snapshot: PlayerSnapshot,
        val generation: Long
    )

    /**
     * 会话恢复 / 断线重连认证
     *
     * 校验房间是否未关闭、验证会话有效性，并严格比对 resumeToken：
     * 若合法则自增连接代次、重连席位并返回 ResumeSessionResult(seat, snapshot, generation)；
     * 若 token 错误或已被显式离开注销则抛出 IllegalStateException("INVALID_RESUME_TOKEN")。
     * 房间已关闭或宿主已拆除时统一抛 ROOM_NOT_FOUND。
     */
    fun resumeSession(
        resumeToken: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ResumeSessionResult> {
        if (isClosed.get()) return CompletableFuture.failedFuture<ResumeSessionResult>(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")

            val seat = (0..3).firstOrNull { seatOccupied[it] && seatResumeTokensArray[it] == resumeToken }
                ?: throw ServerApiException(ApiErrors.INVALID_TOKEN, "INVALID_RESUME_TOKEN")

            val assignedGeneration = ++seatMemberGenerationsArray[seat]
            tableHost.performReconnectOnQueue(seat, reclaim = false)
            seatConnected[seat] = true
            recomputeIdleOnQueue()

            val snap = tableHost.performQuerySnapshotOnQueue(seat)
            ResumeSessionResult(seat = seat, snapshot = snap, generation = assignedGeneration)
        }.thenApplyAsync({ res ->
            notifySeatDisplaced(res.seat, res.generation)
            res
        }, roomNotificationExecutor).exceptionally { t ->
            // 宿主拆除使排队/后续操作以 IllegalStateException 失败时，房间级契约仍须给出 ROOM_NOT_FOUND
            val unwrapped = (t as? java.util.concurrent.CompletionException)?.cause ?: t
            if ((isClosed.get() || tableHost.closed) && (t is IllegalStateException || unwrapped is IllegalStateException) && unwrapped !is ServerApiException) {
                throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            }
            if (isClosed.get() && t is IllegalStateException && t !is ServerApiException) {
                throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            }
            throw t
        }
    }

    /**
     * 传输连接断开席位调度（严格在 TableHost 权威队列中串行执行）
     *
     * 校验全局席位代次：若代次已被新连接递增（顶号恢复），则忽略过时的断线请求，绝不误断开新连接；
     * 若仍持有有效代次：在单人模式且房主断开时关闭房间，联机模式或访客断开时将席位转交 AI 托管，
     * 保留房间与有效的 resumeToken。
     */
    fun disconnectSeat(seat: Int, expectedSeatGeneration: Long): CompletableFuture<Unit> {
        var shouldShutdown = false
        val opFuture = tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation
            if (seat in 0..3 && seatOccupied[seat] && seatMemberGenerationsArray[seat] == expectedSeatGeneration) {
                if (expectedSeatGeneration > seatDisconnectedGenerationsArray[seat]) {
                    seatDisconnectedGenerationsArray[seat] = expectedSeatGeneration
                }
                seatConnected[seat] = false
                if (seat == hostSeat && isSingleplayer) {
                    if (isClosed.compareAndSet(false, true)) {
                        lifecycle = ProtocolRoomLifecycle.CLOSED
                        roomVersion++
                        val terminalInfo = latestRoomInfo.copy(
                            lifecycle = ProtocolRoomLifecycle.CLOSED,
                            version = roomVersion
                        )
                        latestRoomInfo = terminalInfo
                        dispatchRoomUpdate(terminalInfo)
                    }
                    shouldShutdown = true
                } else {
                    tableHost.performDisconnectOnQueue(seat)
                    recomputeIdleOnQueue()
                }
            }
        }
        return opFuture.thenApply {
            if (shouldShutdown) {
                deferredShutdown()
            }
        }
    }

    data class JoinSpectatorResult(
        val spectatorId: String,
        val resumeToken: String,
        val generation: Long,
        val snapshot: ProtocolPublicSnapshot
    )

    data class ResumeSpectatorResult(
        val spectatorId: String,
        val generation: Long,
        val snapshot: ProtocolPublicSnapshot
    )

    fun joinSpectator(
        spectatorName: String? = null,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<JoinSpectatorResult> {
        if (isClosed.get()) return CompletableFuture.failedFuture<JoinSpectatorResult>(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            if (spectatorsMap.size >= capacityPolicy.maxSpectators) {
                throw ServerApiException(ApiErrors.ROOM_FULL, "SPECTATOR_LIMIT")
            }

            val spectatorId = UUID.randomUUID().toString()
            val token = UUID.randomUUID().toString()
            val name = spectatorName?.takeIf { it.isNotBlank() } ?: "Spectator"
            val member = InternalSpectatorMember(spectatorId, name, token, generation = 1L, isConnected = true)
            spectatorsMap[spectatorId] = member
            recomputeIdleOnQueue()

            val snap = tableHost.performQueryPublicSnapshotOnQueue()
            JoinSpectatorResult(spectatorId, token, 1L, snap)
        }
    }

    fun resumeSpectator(
        spectatorId: String,
        resumeToken: String,
        checkpoint: PublicCheckpoint? = null,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ResumeSpectatorResult> {
        if (isClosed.get()) return CompletableFuture.failedFuture<ResumeSpectatorResult>(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")

            val member = spectatorsMap[spectatorId]
                ?: throw ServerApiException(ApiErrors.INVALID_TOKEN, "INVALID_RESUME_TOKEN")
            if (member.resumeToken != resumeToken) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, "INVALID_RESUME_TOKEN")
            }

            // 完整 PUBLIC checkpoint 先于代次递增校验：错误 checkpoint 不递增成员代次、不替换现有绑定
            if (checkpoint != null) {
                tableHost.performValidatePublicCheckpointOnQueue(checkpoint)
            }

            member.generation++
            member.isConnected = true
            val assignedGen = member.generation
            spectatorsMap[spectatorId] = member
            recomputeIdleOnQueue()
            val snap = tableHost.performQueryPublicSnapshotOnQueue()
            ResumeSpectatorResult(spectatorId, assignedGen, snap)
        }.thenApplyAsync({ res ->
            // 队列外主动通知被替换的旧观战绑定；恢复本身不改变公开牌局，旧连接不能等下一条游戏回调。
            // 移出单线程权威队列以异步执行，防止外部回调（或测试门闩）阻塞 TableHost 权威队列。
            notifySpectatorDisplaced(res.spectatorId, res.generation)
            res
        }, roomNotificationExecutor)
    }

    fun leaveSpectator(
        spectatorId: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolActionReceipt> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) {
                return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            }
            if (sessionValidator?.invoke() == false) {
                return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            }
            val member = spectatorsMap.remove(spectatorId)
                ?: return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "NOT_MEMBER")
            member.isConnected = false
            member.resumeToken = UUID.randomUUID().toString()
            member.generation++
            if (member.generation > member.lastDisconnectedGeneration) {
                member.lastDisconnectedGeneration = member.generation
            }
            recomputeIdleOnQueue()
            ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
        }
    }

    fun disconnectSpectator(spectatorId: String, expectedGeneration: Long): CompletableFuture<Unit> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation
            val member = spectatorsMap[spectatorId] ?: return@enqueueOperation
            if (member.generation == expectedGeneration) {
                if (expectedGeneration > member.lastDisconnectedGeneration) {
                    member.lastDisconnectedGeneration = expectedGeneration
                }
                member.isConnected = false
                recomputeIdleOnQueue()
            }
        }
    }

    fun publicSnapshot(sessionValidator: (() -> Boolean)? = null): CompletableFuture<ProtocolPublicSnapshot> =
        tableHost.publicSnapshot(sessionValidator)

    fun resumePublic(
        checkpoint: PublicCheckpoint,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ProtocolPublicSnapshot> =
        tableHost.resumePublic(checkpoint, sessionValidator)

    fun queryPublicRoundResultEvents(
        roundId: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<List<ProtocolGameEvent>> =
        tableHost.queryPublicRoundResultEvents(roundId, sessionValidator)

    fun subscribePublic(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable =
        tableHost.subscribePublic(listener)

    fun getRoomInfo(sessionValidator: (() -> Boolean)? = null): CompletableFuture<ProtocolRoomInfo> {
        if (isClosed.get()) {
            val f = CompletableFuture<ProtocolRoomInfo>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
            return f
        }
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")
            updateLifecycleFromHostOnQueue()
            latestRoomInfo
        }
    }

    fun getRoomInfoSnapshot(): ProtocolRoomInfo = latestRoomInfo

    fun onRoomUpdate(listener: (ProtocolRoomInfo) -> Unit): AutoCloseable {
        val subscriber = RoomSubscriber(listener, roomNotificationExecutor) {
            subscriberRejectCounter.incrementAndGet()
        }
        roomSubscribers.add(subscriber)
        // 初始基线以严格相同的有序队列参与派发
        subscriber.enqueue(latestRoomInfo)
        return AutoCloseable {
            subscriber.unregister()
            roomSubscribers.remove(subscriber)
        }
    }

    fun toSummary(): RoomSummary {
        val info = latestRoomInfo
        val occupiedCount = info.members.size
        val windText = if (info.config.matchConfig.winds.size == 1) "东风战" else "半庄战"
        return RoomSummary(
            roomId = roomId,
            roomName = "${hostPlayerName}的麻将房",
            hostPlayerName = hostPlayerName,
            playerCount = if (occupiedCount > 0) occupiedCount else 1,
            maxPlayers = 4,
            matchStarted = (info.lifecycle == ProtocolRoomLifecycle.PLAYING ||
                info.lifecycle == ProtocolRoomLifecycle.PREPARING ||
                info.lifecycle == ProtocolRoomLifecycle.SETTLED),
            configSummary = "$windText, 局数 ${info.config.matchConfig.handsPerWind}, 目标点 ${info.config.matchConfig.targetScore}"
        )
    }

    // ==========================================
    // 代理权威操作接口（带 sessionValidator 校验与深拷贝）
    // ==========================================

    fun snapshot(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerSnapshot> =
        tableHost.snapshot(seat, sessionValidator)

    fun resume(seat: Int, cursor: Long, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerSnapshot> =
        tableHost.resume(seat, cursor, sessionValidator)

    fun capabilities(): CompletableFuture<AccessCapabilities> {
        if (isClosed.get()) {
            val f = CompletableFuture<AccessCapabilities>()
            f.completeExceptionally(IllegalStateException("SharedRoom is closed"))
            return f
        }
        return CompletableFuture.completedFuture(
            AccessCapabilities(
                protocolVersion = ApiProtocol.PROTOCOL_VERSION,
                supportedPlayerCounts = setOf(4),
                remoteTransport = !isSingleplayer,
                persistence = false,
                resumption = true
            )
        )
    }

    fun decision(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<PlayerDecision> =
        tableHost.decision(seat, sessionValidator)

    fun submitChoice(
        seat: Int,
        choice: ActionChoice,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitChoice(seat, choice, sessionValidator)

    fun submitShufflePlan(
        seat: Int,
        plan: ShufflePlanRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitShufflePlan(seat, plan, sessionValidator)

    fun submitTileMark(
        seat: Int,
        request: TileMarkRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitTileMark(seat, request, sessionValidator)

    fun submitSleeveBloom(
        seat: Int,
        request: SleeveBloomRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitSleeveBloom(seat, request, sessionValidator)

    fun submitSleeveBloomChoice(
        seat: Int,
        request: SleeveBloomChoiceRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitSleeveBloomChoice(seat, request, sessionValidator)

    fun submitSleeveBloomOffTurn(
        seat: Int,
        request: SleeveBloomOffTurnRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitSleeveBloomOffTurn(seat, request, sessionValidator)

    fun submitSleeveBloomOffTurnChoice(
        seat: Int,
        request: SleeveBloomOffTurnChoiceRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitSleeveBloomOffTurnChoice(seat, request, sessionValidator)

    fun submitBeamSwap(
        seat: Int,
        request: BeamSwapRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitBeamSwap(seat, request, sessionValidator)

    fun submitBeamSwapChoice(
        seat: Int,
        request: BeamSwapChoiceRequest,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ActionReceipt> =
        tableHost.submitBeamSwapChoice(seat, request, sessionValidator)

    fun claimControl(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> =
        tableHost.claimControl(seat, sessionValidator)

    fun releaseControl(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> =
        tableHost.releaseControl(seat, sessionValidator)

    fun queryRoundResultEvents(
        seat: Int,
        roundId: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<List<GameEvent>> =
        tableHost.queryRoundResultEvents(seat, roundId, sessionValidator)

    fun nextRound(seat: Int, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt> =
        tableHost.nextRound(seat, sessionValidator)

    fun subscribe(seat: Int, listener: (PlayerUpdate) -> Unit): AutoCloseable =
        tableHost.subscribeSeat(seat, listener)

    fun snapshotAndSubscribe(
        seat: Int,
        sessionValidator: (() -> Boolean)? = null,
        listener: (PlayerUpdate) -> Unit
    ): CompletableFuture<Pair<PlayerSnapshot, AutoCloseable>> =
        tableHost.snapshotAndSubscribe(seat, sessionValidator, listener)

    fun publicSnapshotAndSubscribe(
        sessionValidator: (() -> Boolean)? = null,
        listener: (ProtocolPublicUpdate) -> Unit
    ): CompletableFuture<Pair<ProtocolPublicSnapshot, AutoCloseable>> =
        tableHost.publicSnapshotAndSubscribe(sessionValidator, listener)

    fun tick(presentationBusy: Boolean = false): CompletableFuture<Boolean> =
        tableHost.tick(presentationBusy)

    /**
     * 在目标房间权威队列中原子裁决空闲回收 (MR-06 任务 E)
     *
     * 重新校验：未关闭 && idleSince!=null && idleEpoch==capturedIdleEpoch &&
     * 已连接人类=0 && 已连接观战=0 && (nowMillis-idleSince >= 按当前 lifecycle 选取的 TTL)；
     * 全部满足才执行关闭标记（CAS isClosed、lifecycle=CLOSED、roomVersion++、
     * 派发终态 RoomInfo——复用现有 close 的派发路径语义）并返回 true。
     * 任一不满足返回 false（下轮巡检再试）。与 resume/join 天然在同一队列串行裁决。
     */
    fun closeIfIdleOnQueue(
        policy: RoomRetentionPolicy,
        capturedIdleEpoch: Long,
        nowMillis: Long,
        creatorActive: Boolean = false,
        creatorActiveSupplier: (() -> Boolean)? = null
    ): CompletableFuture<Boolean> {
        var shouldShutdown = false
        val opFuture = tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation false
            val idleSince = idleSinceMillis ?: return@enqueueOperation false
            if (idleEpoch != capturedIdleEpoch) return@enqueueOperation false
            if (connectedHumanCount() != 0 || connectedSpectatorCount() != 0) return@enqueueOperation false

            val isCreatorAlive = creatorActiveSupplier?.invoke() ?: creatorActive
            val ttl = ttlFor(lifecycle, everConnectedHuman, isCreatorAlive, policy)
            if (ttl <= 0L || nowMillis - idleSince < ttl) {
                return@enqueueOperation false
            }

            if (isClosed.compareAndSet(false, true)) {
                lifecycle = ProtocolRoomLifecycle.CLOSED
                roomVersion++
                val terminalInfo = latestRoomInfo.copy(
                    lifecycle = ProtocolRoomLifecycle.CLOSED,
                    version = roomVersion
                )
                latestRoomInfo = terminalInfo
                dispatchRoomUpdate(terminalInfo)

                for (i in 0..3) {
                    seatResumeTokensArray[i] = UUID.randomUUID().toString()
                    seatConnected[i] = false
                }
                spectatorsMap.clear()
                shouldShutdown = true
                return@enqueueOperation true
            }
            false
        }

        return opFuture.thenApply { closedNow ->
            if (shouldShutdown) {
                tableHost.close()
                deferredShutdown()
            }
            closedNow
        }
    }

    override fun close() {
        if (isClosed.compareAndSet(false, true)) {
            lifecycle = ProtocolRoomLifecycle.CLOSED
            roomVersion++
            val terminalInfo = latestRoomInfo.copy(
                lifecycle = ProtocolRoomLifecycle.CLOSED,
                version = roomVersion
            )
            latestRoomInfo = terminalInfo
            dispatchRoomUpdate(terminalInfo)
            for (i in 0..3) {
                seatResumeTokensArray[i] = UUID.randomUUID().toString()
                seatConnected[i] = false
            }
            spectatorsMap.clear()
        }
        // 立即关闭宿主，确保卡住的执行器任务也能立刻让 pendingFutures 异常完成
        tableHost.close()
        deferredShutdown()
    }
}

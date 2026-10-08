package com.majiang.server.room

import com.majiang.access.AccessCapabilities
import com.majiang.access.ActionReceipt
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.ReceiptStatus
import com.majiang.application.ActionChoice
import com.majiang.application.PlayerDecision
import com.majiang.event.GameEvent
import com.majiang.match.SessionLifecycle
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.RoomSummary
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
    initialState: GameState? = null
) : AutoCloseable {

    private val isClosed = AtomicBoolean(false)
    val closed: Boolean get() = isClosed.get()

    private val isShutdownDone = AtomicBoolean(false)

    private var roomVersion: Long = 1L
    val version: Long get() = roomVersion

    val seatOccupied = BooleanArray(4) { false }
    val playerNames = arrayOfNulls<String>(4)
    val resumeTokens = Array(4) { UUID.randomUUID().toString() }
    val isReady = BooleanArray(4) { false }
    val isHuman = BooleanArray(4) { false }
    val bindingGenerations = LongArray(4) { 1L }

    data class SpectatorMember(
        val spectatorId: String,
        val name: String,
        @Volatile var resumeToken: String,
        @Volatile var generation: Long = 1L
    )

    val spectators = ConcurrentHashMap<String, SpectatorMember>()

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

    private fun notifySpectatorDisplaced(spectatorId: String, newGeneration: Long) {
        for (listener in spectatorDisplacedListeners) {
            try {
                listener(spectatorId, newGeneration)
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

    private val roomNotificationExecutor: ExecutorService = Executors.newCachedThreadPool(
        ThreadFactory { r -> Thread(r, "SharedRoom-$roomId-Notification").apply { isDaemon = true } }
    )

    private val roomSubscribers = CopyOnWriteArrayList<RoomSubscriber>()

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
            for (s in 0..3) {
                if (s != hostSeat) {
                    seatOccupied[s] = true
                    playerNames[s] = "AI-$s"
                    isHuman[s] = false
                    isReady[s] = true
                }
            }
        }

        tableHost = ServerTableHost(
            config = domainMatchConfig,
            timeControl = domainTimeControl,
            clock = clock,
            controllers = resolvedControllers,
            adminSeat = hostSeat,
            serialExecutor = serialExecutor,
            initialState = initialState
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
        private val executor: ExecutorService
    ) {
        @Volatile
        private var unregistered = false
        private val lock = Any()
        private var highestVersion = -1L
        private var pending: ProtocolRoomInfo? = null
        private var terminal: ProtocolRoomInfo? = null
        private var isRunning = false

        fun enqueue(info: ProtocolRoomInfo) {
            synchronized(lock) {
                if (unregistered) return
                // 终结状态为最终态，后续任何事件直接丢弃
                if (terminal != null) return
                if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
                    terminal = info
                    pending = null
                } else {
                    // 防乱序防御：仅接受单调递增的新版本，拒绝过时或相同版本覆盖
                    if (info.version <= highestVersion) return
                    highestVersion = info.version
                    pending = info
                }
                if (isRunning) return
                isRunning = true
            }
            drain()
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
                    pending = null
                    terminal = null
                }
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

    private fun deferredShutdown() {
        if (isShutdownDone.compareAndSet(false, true)) {
            roomNotificationExecutor.execute {
                try {
                    tableHost.close()
                } catch (_: Throwable) {
                } finally {
                    roomNotificationExecutor.shutdown()
                }
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
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw IllegalStateException("ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw IllegalStateException("STALE_SESSION")
            if (lifecycle != ProtocolRoomLifecycle.LOBBY) throw IllegalStateException("ROOM_FULL")

            val targetSeat = if (preferSeat != null && preferSeat in 0..3 && preferSeat != hostSeat && !seatOccupied[preferSeat]) {
                preferSeat
            } else {
                val start = if (preferSeat != null && preferSeat in 0..3) preferSeat + 1 else 0
                val candidates = (start..3).toList() + (0 until start).toList()
                candidates.firstOrNull { it != hostSeat && !seatOccupied[it] }
            } ?: throw IllegalStateException("ROOM_FULL")

            seatOccupied[targetSeat] = true
            playerNames[targetSeat] = playerName
            isHuman[targetSeat] = true
            isReady[targetSeat] = false
            bindingGenerations[targetSeat]++
            roomVersion++

            val info = buildRoomInfoOnQueue()
            latestRoomInfo = info
            dispatchRoomUpdate(info)

            Pair(targetSeat, resumeTokens[targetSeat])
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
            resumeTokens[seat] = UUID.randomUUID().toString()
            bindingGenerations[seat]++

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
                    roomVersion++
                    val info = buildRoomInfoOnQueue()
                    latestRoomInfo = info
                    dispatchRoomUpdate(info)
                    ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
                } else {
                    // 对局中普通成员退出：转为 AI 托管
                    tableHost.performDisconnectOnQueue(seat)
                    isHuman[seat] = false
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
     */
    fun resumeSession(
        resumeToken: String,
        sessionValidator: (() -> Boolean)? = null
    ): CompletableFuture<ResumeSessionResult> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw IllegalStateException("ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw IllegalStateException("STALE_SESSION")

            val seat = (0..3).firstOrNull { seatOccupied[it] && resumeTokens[it] == resumeToken }
                ?: throw IllegalStateException("INVALID_RESUME_TOKEN")

            val assignedGeneration = ++bindingGenerations[seat]
            tableHost.performReconnectOnQueue(seat, reclaim = false)

            val snap = tableHost.performQuerySnapshotOnQueue(seat)
            ResumeSessionResult(seat = seat, snapshot = snap, generation = assignedGeneration)
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
            if (seat in 0..3 && seatOccupied[seat] && bindingGenerations[seat] == expectedSeatGeneration) {
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
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw IllegalStateException("ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw IllegalStateException("STALE_SESSION")

            val spectatorId = UUID.randomUUID().toString()
            val token = UUID.randomUUID().toString()
            val name = spectatorName?.takeIf { it.isNotBlank() } ?: "Spectator"
            val member = SpectatorMember(spectatorId, name, token, generation = 1L)
            spectators[spectatorId] = member

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
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw IllegalStateException("ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw IllegalStateException("STALE_SESSION")

            val member = spectators[spectatorId]
                ?: throw IllegalStateException("INVALID_RESUME_TOKEN")
            if (member.resumeToken != resumeToken) {
                throw IllegalStateException("INVALID_RESUME_TOKEN")
            }

            // 完整 PUBLIC checkpoint 先于代次递增校验：错误 checkpoint 不递增成员代次、不替换现有绑定
            if (checkpoint != null) {
                tableHost.performValidatePublicCheckpointOnQueue(checkpoint)
            }

            member.generation++
            val assignedGen = member.generation
            spectators[spectatorId] = member
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
            val member = spectators.remove(spectatorId)
                ?: return@enqueueOperation ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "NOT_MEMBER")
            member.resumeToken = UUID.randomUUID().toString()
            member.generation++
            ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
        }
    }

    fun disconnectSpectator(spectatorId: String, expectedGeneration: Long): CompletableFuture<Unit> {
        return tableHost.enqueueOperation {
            if (isClosed.get()) return@enqueueOperation
            val member = spectators[spectatorId] ?: return@enqueueOperation
            if (member.generation == expectedGeneration) {
                // 意外断开：保留恢复令牌与资格，代次匹配不自增
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
        return tableHost.enqueueOperation {
            if (isClosed.get()) throw IllegalStateException("ROOM_CLOSED")
            if (sessionValidator?.invoke() == false) throw IllegalStateException("STALE_SESSION")
            updateLifecycleFromHostOnQueue()
            latestRoomInfo
        }
    }

    fun getRoomInfoSnapshot(): ProtocolRoomInfo = latestRoomInfo

    fun onRoomUpdate(listener: (ProtocolRoomInfo) -> Unit): AutoCloseable {
        val subscriber = RoomSubscriber(listener, roomNotificationExecutor)
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

    fun tick(presentationBusy: Boolean = false): CompletableFuture<Boolean> =
        tableHost.tick(presentationBusy)

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
            spectators.clear()
        }
        // 立即关闭宿主，确保卡住的执行器任务也能立刻让 pendingFutures 异常完成
        tableHost.close()
        deferredShutdown()
    }
}

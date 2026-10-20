package com.majiang.client.session

import com.majiang.client.api.*
import com.majiang.client.mapping.ProtocolViewMapper

import com.majiang.client.transport.tcp.ScheduledExecutorTransportScheduler
import com.majiang.client.transport.tcp.TcpClient
import com.majiang.transport.common.Cancellable
import com.majiang.transport.common.TransportClock
import com.majiang.transport.common.TransportScheduler
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint as ProtocolPublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import java.io.IOException
import java.util.Collections
import java.util.LinkedHashSet
import java.util.TreeMap
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * 统一服务端连接会话运行时 (ServerConnectionSession)
 *
 * 包装底层 [ServerConnection]（单机进程内绑定或远端 TCP 绑定），同时实现
 * [LobbySession]、[GameSession] 与 [SpectatorSession] 接口，作为客户端唯一权威运行时。
 *
 * 核心架构与可靠性机制：
 * 1. **全客户端模型出入参**：出参通过 [ProtocolViewMapper] 映射为不可变视图模型，
 *    入参仅接受客户端自有模型，彻底隔离 protocol 与 engine；
 * 2. **不可变视图缓存与有序同步**：本地缓存最新 [currentSnapshot]，严格按
 *    `sessionId + syncEpoch + cursor` 进行快照对账与乱序重排，绝不回滚新视图；
 * 3. **早期推送缓冲与有序消费**：建房/加入/恢复握手期间先注册推送监听，
 *    早期到达的 PUSH 消息进入有界缓冲，并在绑定确立后按序排水合流；
 * 4. **明确全量标记贯穿 (isFullSnapshot)**：协议层显式标记全量快照，
 *    建立新基线并清理陈旧缓冲，旧/异 epoch PUSH 永远无法替换新绑定；
 * 5. **缺口自愈与去重重试**：遇到超前游标 (GAP) 触发恢复并拦截未来决策暴露，
 *    恢复请求在途严格去重，失败安全释放标记以便后续重试；
 * 6. **待确认状态守卫 (isPending)**：动作选择与洗牌方案提交期间置位 [isPending]，
 *    具备代次与动作 ID 级并发守卫；晚到旧回执无法清除新代次/新动作在途状态；遇拒绝、异常、关闭或取消均安全释放并在对应线程结束 Future；
 * 7. **可注入线程调度 (dispatch)**：所有 UI 监听器与 Future 消费均通过 [dispatch] 派发，
 *    持锁期间绝不调用用户代码，关闭或代次变更后旧消息自动作废，单一监听器异常不污染其他监听器；
 * 8. **指数退避自动重连**：配合连接工厂与调度器在意外掉线时保留 UI 视图并尝试断线重连，
 *    显式离房在途、主动退出、被顶号、停服或房间关闭则直接终结，绝不产生抢占循环；
 * 9. **关闭与生命周期安全**：关闭操作立即以异常终结所有在途挂起的请求 Future，
 *    换绑确立时立即使旧代次悬挂请求以异常结束，杜绝调用方无限等待。
 */
class ServerConnectionSession(
    initialConnection: ServerConnection,
    private val connectionFactory: (() -> CompletableFuture<ServerConnection>)? = null,
    val clock: TransportClock = TransportClock.SYSTEM,
    val scheduler: TransportScheduler? = null,
    val dispatch: ((() -> Unit) -> Unit) = { it() },
    private val ownsScheduler: Boolean = false
) : LobbySession, GameSession, SpectatorSession, AutoCloseable {

    @Volatile
    var currentConnection: ServerConnection = initialConnection
        private set

    /**
     * 兼容访问底层活动连接
     */
    val connection: ServerConnection
        get() = currentConnection

    @Volatile
    var currentBinding: RoomBinding? = null
        private set

    @Volatile
    var currentSpectatorBinding: SpectatorBinding? = null
        private set

    // 生命周期与状态
    private val stateLock = Any()
    private val stateRef = AtomicReference(ConnectionState.CONNECTED)
    private val isClosed = AtomicBoolean(false)
    private val disconnectReasonRef = AtomicReference<String?>(null)
    private val pendingState = AtomicBoolean(false)
    private val actionSubmissionSeq = AtomicLong(0L)
    private val activeActionId = AtomicLong(0L)
    private val isLeavingRoom = AtomicBoolean(false)
    private val currentEpoch = AtomicLong(0L)

    override val closed: Boolean
        get() = isClosed.get()

    override val disconnectReason: String?
        get() = disconnectReasonRef.get()

    override val connectionState: ConnectionState
        get() = stateRef.get()

    override val isPending: Boolean
        get() = pendingState.get()

    private val maxAppliedBatchCursors = 256
    private val maxBufferSize = 64

    // 玩家流同步引擎 (D-01: 提取自 ServerConnectionSession)
    private val playerStreamEngine = PlayerStreamEngine(maxBufferSize, maxAppliedBatchCursors)
    private val syncLock: Any = playerStreamEngine.syncLock
    private val isBindingInProgress: Boolean get() = playerStreamEngine.bindingInProgress
    private val isResuming: AtomicBoolean get() = playerStreamEngine.isResuming
    private val earlyUpdateOverflowed: AtomicBoolean get() = playerStreamEngine.earlyUpdateOverflowed
    private val cachedSnapshot: GameSnapshotView?
        get() = playerStreamEngine.cachedSnapshot

    override val currentSnapshot: GameSnapshotView?
        get() = playerStreamEngine.cachedSnapshot

    // 观战流同步引擎 (D-01: 提取自 ServerConnectionSession)
    private val spectatorStreamEngine = SpectatorStreamEngine(maxBufferSize)
    private val publicSyncLock: Any = spectatorStreamEngine.publicSyncLock
    private val isSpectatorBindingInProgress: Boolean get() = spectatorStreamEngine.spectatorBindingInProgress
    private val activePublicRecoveryToken get() = spectatorStreamEngine.activePublicRecoveryToken
    private val earlyPublicUpdateOverflowed get() = spectatorStreamEngine.earlyPublicUpdateOverflowed
    private var cachedPublicSnapshot: PublicSnapshotView?
        get() = spectatorStreamEngine.cachedPublicSnapshot
        set(_) {}

    override val currentPublicSnapshot: PublicSnapshotView?
        get() = spectatorStreamEngine.cachedPublicSnapshot

    private var activePublicConnectionSub: AutoCloseable? = null

    /**
     * 跨玩家与观战流的统一锁顺序辅助入口：始终以 syncLock -> publicSyncLock 顺序加锁，严禁反向嵌套
     */
    private inline fun <T> withBothSyncLocks(action: () -> T): T {
        return synchronized(syncLock) {
            synchronized(publicSyncLock) {
                action()
            }
        }
    }

    private fun resetPlayerSyncStateUnderLock(
        clearEarlyBuffer: Boolean = true,
        clearSnapshot: Boolean = true
    ) {
        playerStreamEngine.resetSyncStateUnderLock(clearEarlyBuffer, clearSnapshot)
    }

    private fun resetSpectatorSyncStateUnderLock(
        clearEarlyBuffer: Boolean = true,
        clearSnapshot: Boolean = true
    ) {
        spectatorStreamEngine.resetSyncStateUnderLock(clearEarlyBuffer, clearSnapshot)
    }

    // 房间同步引擎 (D-01: 提取自 ServerConnectionSession)
    private val roomSyncEngine = RoomSyncEngine()
    private val roomLock: Any = roomSyncEngine.roomLock
    private val currentRoomEpoch: AtomicLong get() = roomSyncEngine.roomEpochRef

    // 监听器集合
    private val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()
    private val stateListeners = CopyOnWriteArrayList<(ConnectionState) -> Unit>()

    // 在途操作 Future 追踪（保证关闭与换绑时全量以异常完成）
    private val outstandingFutures = ConcurrentHashMap<CompletableFuture<*>, Long>()
    private var activeConnectionSub: AutoCloseable? = null
    private var activeDisconnectSub: AutoCloseable? = null
    private var activeRoomSub: AutoCloseable? = null
    private val activeReconnectTimer = AtomicReference<Cancellable?>(null)

    init {
        bindConnectionListeners(initialConnection, currentEpoch.get())
    }

    private fun bindConnectionListeners(conn: ServerConnection, epoch: Long) {
        activeConnectionSub?.close()
        activeDisconnectSub?.close()
        activeRoomSub?.close()
        activePublicConnectionSub?.close()

        activeConnectionSub = conn.onUpdate { protoUpdate ->
            handleInboundUpdate(protoUpdate, epoch)
        }
        activePublicConnectionSub = conn.onPublicUpdate { protoPublicUpdate ->
            handleInboundPublicUpdate(protoPublicUpdate, epoch)
        }
        activeDisconnectSub = conn.onDisconnect { reason ->
            handleInboundDisconnect(reason, epoch)
        }
        activeRoomSub = conn.onRoomUpdate { protoRoomInfo ->
            handleInboundRoomUpdate(protoRoomInfo, epoch)
        }
    }

    private fun <T> trackFuture(future: CompletableFuture<T>, epoch: Long): CompletableFuture<T> {
        outstandingFutures[future] = epoch
        future.whenComplete { _, _ ->
            outstandingFutures.remove(future)
        }
        return future
    }

    private fun <T> trackRoomFuture(future: CompletableFuture<T>, roomEpoch: Long): CompletableFuture<T> {
        return roomSyncEngine.trackRoomFuture(future, roomEpoch)
    }

    // ==========================================
    // 监听器注册与派发机制
    // ==========================================

    override fun onUpdate(listener: (GameUpdateView) -> Unit): AutoCloseable {
        return playerStreamEngine.onUpdate(listener)
    }

    override fun onDisconnect(listener: (String) -> Unit): AutoCloseable {
        disconnectListeners.add(listener)
        return AutoCloseable {
            disconnectListeners.remove(listener)
        }
    }

    override fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable {
        stateListeners.add(listener)
        val epoch = currentEpoch.get()
        val cur = connectionState
        dispatch {
            if (!stateListeners.contains(listener)) return@dispatch
            if (closed) {
                if (cur == ConnectionState.CLOSED) {
                    try {
                        listener(ConnectionState.CLOSED)
                    } catch (_: Throwable) {
                    }
                }
                return@dispatch
            }
            if (epoch == currentEpoch.get()) {
                try {
                    listener(cur)
                } catch (_: Throwable) {
                    // 隔离监听器异常
                }
            }
        }
        return AutoCloseable {
            stateListeners.remove(listener)
        }
    }

    private fun notifyUpdateListeners(update: GameUpdateView) {
        val epoch = currentEpoch.get()
        dispatch {
            playerStreamEngine.notifyUpdateListeners(update) { !closed && epoch == currentEpoch.get() }
        }
    }

    private fun notifyDisconnect(reason: String) {
        for (listener in disconnectListeners) {
            dispatch {
                if (!disconnectListeners.contains(listener)) return@dispatch
                try {
                    listener(reason)
                } catch (_: Throwable) {
                    // 隔离监听器异常
                }
            }
        }
    }

    private fun notifyStateChange(newState: ConnectionState) {
        val epoch = currentEpoch.get()
        for (listener in stateListeners) {
            dispatch {
                if (!stateListeners.contains(listener)) return@dispatch
                if (closed) {
                    if (newState == ConnectionState.CLOSED) {
                        try {
                            listener(ConnectionState.CLOSED)
                        } catch (_: Throwable) {
                            // 隔离监听器异常
                        }
                    }
                    return@dispatch
                }
                if (epoch == currentEpoch.get()) {
                    try {
                        listener(newState)
                    } catch (_: Throwable) {
                        // 隔离监听器异常
                    }
                }
            }
        }
    }

    private fun transitionState(newState: ConnectionState, reason: String?) {
        synchronized(stateLock) {
            if (isClosed.get() && newState != ConnectionState.CLOSED) {
                // 已处于 CLOSED 终态，坚决不允许被旧代次回调复活为 CONNECTING / CONNECTED / RECONNECTING
                return
            }
            stateRef.set(newState)
            if (reason != null) {
                disconnectReasonRef.set(reason)
            }
        }
        notifyStateChange(newState)
    }

    private fun handleInboundRoomUpdate(protoRoomInfo: com.majiang.transport.api.ProtocolRoomInfo, epoch: Long) {
        if (closed || isLeavingRoom.get() || epoch != currentEpoch.get()) return
        val effect = roomSyncEngine.handleInboundRoomUpdate(
            protoRoomInfo = protoRoomInfo,
            connectionEpoch = epoch,
            currentBoundRoomId = { currentBinding?.roomId ?: currentSpectatorBinding?.roomId },
            isSessionValid = { !closed && !isLeavingRoom.get() && epoch == currentEpoch.get() }
        )
        if (effect is RoomUpdateEffect.Notify) {
            for (observer in effect.observers) {
                dispatch {
                    roomSyncEngine.notifyObserver(
                        observer = observer,
                        info = effect.roomInfo,
                        connectionEpoch = effect.connectionEpoch,
                        roomEpoch = effect.roomEpoch,
                        isSessionValidAndMatchingRoom = {
                            !closed && !isLeavingRoom.get() &&
                                epoch == currentEpoch.get() &&
                                effect.roomEpoch == currentRoomEpoch.get() &&
                                (currentBinding?.roomId ?: currentSpectatorBinding?.roomId) == effect.roomInfo.roomId
                        }
                    )
                }
            }
        }
    }

    // ==========================================
    // 入站更新同步裁决与乱序重排处理
    // ==========================================

    private fun handleInboundUpdate(protoUpdate: ProtocolPlayerUpdate, epoch: Long) {
        if (closed || epoch != currentEpoch.get()) return
        val roomEpoch = currentRoomEpoch.get()
        val effect = playerStreamEngine.handleInboundUpdate(
            protoUpdate = protoUpdate,
            epoch = epoch,
            roomEpoch = roomEpoch,
            isSessionValid = { !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get() },
            isMatchingBinding = { viewerSeat, sessionId ->
                val b = currentBinding
                b == null || (b.seat == viewerSeat && b.snapshot.sessionId == sessionId)
            }
        )
        if (effect is PlayerUpdateEffect.Dispatch) {
            if (effect.shouldRecover) {
                triggerGapRecovery(effect.recoverCursor, epoch, roomEpoch)
            }
            for (u in effect.updates) {
                notifyUpdateListeners(u)
            }
        }
    }

    private fun triggerGapRecovery(cursor: Long, epoch: Long, roomEpoch: Long = currentRoomEpoch.get()) {
        try {
            currentConnection.resume(cursor).whenComplete { protoSnapshot, err ->
                try {
                    val effect = playerStreamEngine.handleResumeResponse(
                        protoSnapshot = protoSnapshot,
                        err = err,
                        epoch = epoch,
                        roomEpoch = roomEpoch,
                        isSessionValid = { !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get() },
                        isMatchingBinding = { viewerSeat, sessionId ->
                            val b = currentBinding
                            b == null || (b.seat == viewerSeat && b.snapshot.sessionId == sessionId)
                        }
                    )
                    if (effect is PlayerResumeEffect.Dispatch) {
                        effect.updateToDispatch?.let { notifyUpdateListeners(it) }
                        for (u in effect.subsequentUpdates) {
                            notifyUpdateListeners(u)
                        }
                        if (effect.shouldTriggerNextRecovery) {
                            triggerGapRecovery(effect.nextCursor, epoch, roomEpoch)
                        }
                    }
                } catch (_: Throwable) {
                    playerStreamEngine.isResuming.set(false)
                }
            }
        } catch (_: Throwable) {
            playerStreamEngine.isResuming.set(false)
        }
    }

    // ==========================================
    // 观战入站更新同步裁决与乱序重排处理
    // ==========================================

    // ==========================================
    // 观战入站更新同步裁决与乱序重排处理
    // ==========================================

    private fun handleInboundPublicUpdate(protoUpdate: ProtocolPublicUpdate, epoch: Long) {
        if (closed || epoch != currentEpoch.get()) return
        val roomEpoch = currentRoomEpoch.get()
        val effect = spectatorStreamEngine.handleInboundPublicUpdate(
            protoUpdate = protoUpdate,
            epoch = epoch,
            roomEpoch = roomEpoch,
            boundRoomIdProvider = { currentSpectatorBinding?.roomId },
            isSessionValid = { !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get() }
        )
        if (effect is SpectatorUpdateEffect.Dispatch) {
            if (effect.shouldRecover && effect.checkpoint != null && effect.token != null) {
                triggerPublicGapRecovery(effect.checkpoint, effect.token, effect.boundRoomId)
            }
            for (u in effect.updates) {
                notifyPublicUpdateListeners(u, epoch, roomEpoch, effect.boundRoomId)
            }
        }
    }

    private fun triggerPublicGapRecovery(
        checkpoint: PublicCheckpoint,
        token: PublicRecoveryToken,
        roomId: String
    ) {
        val epoch = token.connectionEpoch
        val roomEpoch = token.roomEpoch
        if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
            return
        }
        val conn = currentConnection
        val protoCheckpoint = com.majiang.server.api.PublicCheckpoint(
            sessionId = checkpoint.sessionId,
            syncEpoch = checkpoint.syncEpoch,
            cursor = checkpoint.cursor
        )
        try {
            conn.resumePublic(protoCheckpoint).whenComplete { protoSnapshot, err ->
                try {
                    val effect = spectatorStreamEngine.handleResumePublicResponse(
                        protoSnapshot = protoSnapshot,
                        err = err,
                        token = token,
                        roomId = roomId,
                        boundRoomIdProvider = { currentSpectatorBinding?.roomId },
                        isSessionValid = { !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get() }
                    )
                    if (effect is SpectatorResumeEffect.Dispatch) {
                        effect.updateToDispatch?.let { notifyPublicUpdateListeners(it, epoch, roomEpoch, roomId) }
                        for (u in effect.subsequentUpdates) {
                            notifyPublicUpdateListeners(u, epoch, roomEpoch, roomId)
                        }
                        if (effect.shouldTriggerNextRecovery && effect.nextCheckpoint != null && effect.nextToken != null) {
                            triggerPublicGapRecovery(effect.nextCheckpoint, effect.nextToken, roomId)
                        }
                    }
                } catch (_: Throwable) {
                    spectatorStreamEngine.activePublicRecoveryToken.compareAndSet(token, null)
                }
            }
        } catch (_: Throwable) {
            spectatorStreamEngine.activePublicRecoveryToken.compareAndSet(token, null)
        }
    }

    private fun notifyPublicUpdateListeners(
        update: PublicUpdateView,
        epoch: Long,
        roomEpoch: Long,
        roomId: String
    ) {
        dispatch {
            spectatorStreamEngine.notifyPublicUpdateListeners(update) {
                !closed && epoch == currentEpoch.get() &&
                    roomEpoch == currentRoomEpoch.get() &&
                    currentSpectatorBinding?.roomId == roomId &&
                    spectatorStreamEngine.cachedPublicSnapshot != null
            }
        }
    }

    // ==========================================
    // 掉线判定与自动重连
    // ==========================================

    private fun handleInboundDisconnect(reason: String, epoch: Long) {
        if (closed || epoch != currentEpoch.get()) return

        if (isLeavingRoom.get()) {
            // 显式离房在途期间掉线：禁止自动恢复旧席位，丢弃可恢复绑定
            currentBinding = null
            currentSpectatorBinding = null
            terminateSession(reason)
            return
        }

        val isPermanent = when (reason) {
            ApiProtocol.DISPLACED_BY_NEW_CONNECTION,
            ApiProtocol.DISCONNECTED_BY_NEW_CONNECTION,
            ApiProtocol.ROOM_CLOSED,
            ApiProtocol.SERVER_SHUTDOWN,
            ApiProtocol.LOCAL_CLOSED,
            "DISPLACED_BY_NEW_CONNECTION",
            "DISCONNECTED_BY_NEW_CONNECTION",
            "ROOM_CLOSED",
            "SERVER_SHUTDOWN",
            "KICKED",
            "KICK",
            "LOCAL_CLOSED",
            "ROOM_NOT_FOUND",
            "TOKEN_INVALID",
            "INVALID_TOKEN" -> true
            else -> false
        }

        if (isPermanent || connectionFactory == null || (currentBinding == null && currentSpectatorBinding == null)) {
            terminateSession(reason)
            return
        }

        // 意外掉线且具备重连工厂与绑定席位：进入重连中状态
        // 重复掉线通知不得开启并发重连循环
        if (stateRef.compareAndSet(ConnectionState.CONNECTED, ConnectionState.RECONNECTING)) {
            notifyStateChange(ConnectionState.RECONNECTING)
            scheduleReconnect(attempt = 0, initialDelay = 500L)
        }
    }

    private fun scheduleReconnect(attempt: Int, initialDelay: Long) {
        if (closed || connectionState != ConnectionState.RECONNECTING) return
        if (attempt >= 10) {
            terminateSession("RECONNECT_ATTEMPTS_EXHAUSTED")
            return
        }

        val sched = scheduler
        if (sched == null) {
            terminateSession("NO_SCHEDULER_FOR_RECONNECT")
            return
        }

        val delay = minOf(8000L, (initialDelay * Math.pow(2.0, attempt.toDouble())).toLong())
        val cancellable = sched.schedule(delay) {
            doReconnect(attempt, initialDelay)
        }
        activeReconnectTimer.set(cancellable)
    }

    private fun doReconnect(attempt: Int, initialDelay: Long) {
        if (closed || connectionState != ConnectionState.RECONNECTING) return
        val factory = connectionFactory ?: return
        val binding = currentBinding
        val spectatorBinding = currentSpectatorBinding
        if (binding == null && spectatorBinding == null) return

        factory().whenComplete { newConn, err ->
            if (closed || connectionState != ConnectionState.RECONNECTING) {
                newConn?.close()
                return@whenComplete
            }
            if (err != null || newConn == null) {
                scheduleReconnect(attempt + 1, initialDelay)
                return@whenComplete
            }

            if (spectatorBinding != null) {
                val curCp = synchronized(publicSyncLock) {
                    cachedPublicSnapshot?.let {
                        com.majiang.server.api.PublicCheckpoint(it.sessionId, it.syncEpoch, it.cursor)
                    }
                }
                val resumeReq = ResumeSpectatorRequest(
                    roomId = spectatorBinding.roomId,
                    spectatorId = spectatorBinding.spectatorId,
                    resumeToken = spectatorBinding.resumeToken,
                    checkpoint = curCp
                )
                val newEpoch = currentEpoch.incrementAndGet()
                withBothSyncLocks {
                    resetPlayerSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
                    spectatorStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = false)
                }
                activeActionId.set(0L)
                pendingState.set(false)
                bindConnectionListeners(newConn, newEpoch)
                try {
                    newConn.resumeSpectator(resumeReq).whenComplete { newSpecBinding, resumeErr ->
                        if (closed || connectionState != ConnectionState.RECONNECTING || newEpoch != currentEpoch.get()) {
                            withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                            newConn.close()
                            return@whenComplete
                        }
                        if (resumeErr != null || newSpecBinding == null) {
                            withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                            val isPermanentErr = ReconnectErrorClassifier.isPermanent(resumeErr)
                            if (isPermanentErr || attempt >= 10 || newConn !== currentConnection) {
                                newConn.close()
                            }
                            if (isPermanentErr || attempt >= 10) {
                                val finalReason = ReconnectErrorClassifier.extractReason(resumeErr)
                                terminateSession(finalReason)
                            } else {
                                scheduleReconnect(attempt + 1, initialDelay)
                            }
                            return@whenComplete
                        }

                        currentConnection = newConn
                        onSpectatorBindingEstablished(newSpecBinding, newEpoch, null)
                    }
                } catch (t: Throwable) {
                    withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                    val isPermanentErr = ReconnectErrorClassifier.isPermanent(t)
                    if (isPermanentErr || attempt >= 10 || newConn !== currentConnection) {
                        newConn.close()
                    }
                    if (isPermanentErr || attempt >= 10) {
                        val finalReason = ReconnectErrorClassifier.extractReason(t)
                        terminateSession(finalReason)
                    } else {
                        scheduleReconnect(attempt + 1, initialDelay)
                    }
                }
                return@whenComplete
            }

            // 玩家重连
            val curCursor = synchronized(syncLock) { cachedSnapshot?.cursor ?: 0L }
            val resumeReq = ResumeRequest(roomId = binding!!.roomId, token = binding.resumeToken, cursor = curCursor)
            val newEpoch = currentEpoch.incrementAndGet()

            // 标记重连绑定进行中，重置旧重排/批次/在途状态
            withBothSyncLocks {
                playerStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = false)
                resetSpectatorSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
            }
            activeActionId.set(0L)
            pendingState.set(false)

            bindConnectionListeners(newConn, newEpoch)

            try {
                newConn.resumeSession(resumeReq).whenComplete { newBinding, resumeErr ->
                    if (closed || connectionState != ConnectionState.RECONNECTING || newEpoch != currentEpoch.get()) {
                        withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                        newConn.close()
                        return@whenComplete
                    }
                    if (resumeErr != null || newBinding == null) {
                        withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                        val isPermanentErr = ReconnectErrorClassifier.isPermanent(resumeErr)
                        if (isPermanentErr || attempt >= 10 || newConn !== currentConnection) {
                            newConn.close()
                        }
                        if (isPermanentErr || attempt >= 10) {
                            val finalReason = ReconnectErrorClassifier.extractReason(resumeErr)
                            terminateSession(finalReason)
                        } else {
                            scheduleReconnect(attempt + 1, initialDelay)
                        }
                        return@whenComplete
                    }

                    // 绑定恢复成功，确立新连接与席位，复用 onBindingEstablished 排水并刷新
                    currentConnection = newConn
                    onBindingEstablished(newBinding, newEpoch, null)
                }
            } catch (t: Throwable) {
                withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                val isPermanentErr = ReconnectErrorClassifier.isPermanent(t)
                if (isPermanentErr || attempt >= 10 || newConn !== currentConnection) {
                    newConn.close()
                }
                if (isPermanentErr || attempt >= 10) {
                    val finalReason = ReconnectErrorClassifier.extractReason(t)
                    terminateSession(finalReason)
                } else {
                    scheduleReconnect(attempt + 1, initialDelay)
                }
            }
        }
    }

    // ==========================================
    // 绑定确立与早期推送排水
    // ==========================================

    private fun onBindingEstablished(
        binding: RoomBinding,
        epoch: Long,
        resultFuture: CompletableFuture<GameSnapshotView>?
    ) {
        var finalSnapshot: GameSnapshotView? = null
        val pendingUpdatesToDispatch = mutableListOf<GameUpdateView>()

        val isNewRoom = (resultFuture != null)
        var roomEpoch = roomSyncEngine.currentRoomEpoch

        val overflowed = withBothSyncLocks {
            if (closed || epoch != currentEpoch.get()) {
                playerStreamEngine.cancelBindingUnderLock()
                false
            } else {
                currentBinding = binding
                currentSpectatorBinding = null // 互斥：清除观战绑定
                if (isNewRoom) {
                    roomEpoch = roomSyncEngine.advanceRoomEpoch()
                    roomSyncEngine.resetRoomInfo()
                }

                // 清除观战缓存与状态（统一复位）
                resetSpectatorSyncStateUnderLock(clearEarlyBuffer = true)

                val initialSnapshotView = ProtocolViewMapper.mapSnapshot(binding.snapshot)
                val installResult = playerStreamEngine.installInitialSnapshotUnderLock(initialSnapshotView)
                pendingUpdatesToDispatch.addAll(installResult.pendingUpdates)
                finalSnapshot = playerStreamEngine.cachedSnapshot
                installResult.hadOverflow
            }
        }

        val snapshotToDeliver = finalSnapshot
        if (snapshotToDeliver == null) {
            if (resultFuture != null) {
                dispatch {
                    resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                }
            }
            return
        }

        // 绑定成功后，使旧代次尚未完成的请求 Future 全部以代次变更异常结束
        val oldFutures = outstandingFutures.entries
            .filter { it.value < epoch }
            .map { it.key }
        for (f in oldFutures) {
            outstandingFutures.remove(f)
            roomSyncEngine.untrackRoomFuture(f)
            dispatch {
                f.completeExceptionally(IOException("Generation changed: new binding established"))
            }
        }
        if (isNewRoom) {
            val oldRoomFutures = roomSyncEngine.pruneFuturesOlderThan(roomEpoch)
            for (f in oldRoomFutures) {
                outstandingFutures.remove(f)
                dispatch {
                    f.completeExceptionally(IOException("Generation changed: new room binding established"))
                }
            }
        }
        activeActionId.set(0L)
        pendingState.set(false)

        synchronized(stateLock) {
            stateRef.set(ConnectionState.CONNECTED)
        }
        notifyStateChange(ConnectionState.CONNECTED)

        // 如果是重连 (resultFuture == null) 且没有早期推送更新，通知全量快照以刷新 UI
        if (resultFuture == null && pendingUpdatesToDispatch.isEmpty()) {
            notifyUpdateListeners(GameUpdateView(snapshotToDeliver, snapshotToDeliver.cursor, emptyList(), isFullSnapshot = true))
        } else {
            for (u in pendingUpdatesToDispatch) {
                notifyUpdateListeners(u)
            }
        }

        if (overflowed && !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get()) {
            val cur = synchronized(syncLock) { cachedSnapshot?.cursor ?: 0L }
            triggerGapRecovery(cur, epoch, roomEpoch)
        }

        if (resultFuture != null) {
            dispatch {
                if (closed || epoch != currentEpoch.get()) {
                    resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                } else {
                    resultFuture.complete(snapshotToDeliver)
                }
            }
        }
    }

    private fun onSpectatorBindingEstablished(
        binding: SpectatorBinding,
        epoch: Long,
        resultFuture: CompletableFuture<PublicSnapshotView>?
    ) {
        var finalSnapshot: PublicSnapshotView? = null
        val pendingUpdatesToDispatch = mutableListOf<PublicUpdateView>()

        val isNewRoom = (resultFuture != null)
        var roomEpoch = roomSyncEngine.currentRoomEpoch

        val overflowed = withBothSyncLocks {
            if (closed || epoch != currentEpoch.get()) {
                spectatorStreamEngine.cancelBindingUnderLock()
                false
            } else {
                currentSpectatorBinding = binding
                currentBinding = null // 互斥：清除玩家绑定
                if (isNewRoom) {
                    roomEpoch = roomSyncEngine.advanceRoomEpoch()
                    roomSyncEngine.resetRoomInfo()
                }

                // 清除玩家缓存与状态（统一复位）
                resetPlayerSyncStateUnderLock(clearEarlyBuffer = true)

                val initialSnapshotView = ProtocolViewMapper.mapPublicSnapshot(binding.snapshot)
                val installResult = spectatorStreamEngine.installInitialSnapshotUnderLock(initialSnapshotView)
                pendingUpdatesToDispatch.addAll(installResult.pendingUpdates)
                finalSnapshot = spectatorStreamEngine.cachedPublicSnapshot
                installResult.hadOverflow
            }
        }

        val snapshotToDeliver = finalSnapshot
        if (snapshotToDeliver == null) {
            if (resultFuture != null) {
                dispatch {
                    resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                }
            }
            return
        }

        // 绑定成功后，使旧代次尚未完成的请求 Future 全部以代次变更异常结束
        val oldFutures = outstandingFutures.entries
            .filter { it.value < epoch }
            .map { it.key }
        for (f in oldFutures) {
            outstandingFutures.remove(f)
            roomSyncEngine.untrackRoomFuture(f)
            dispatch {
                f.completeExceptionally(IOException("Generation changed: new spectator binding established"))
            }
        }
        if (isNewRoom) {
            val oldRoomFutures = roomSyncEngine.pruneFuturesOlderThan(roomEpoch)
            for (f in oldRoomFutures) {
                outstandingFutures.remove(f)
                dispatch {
                    f.completeExceptionally(IOException("Generation changed: new room binding established"))
                }
            }
        }
        activeActionId.set(0L)
        pendingState.set(false)

        synchronized(stateLock) {
            stateRef.set(ConnectionState.CONNECTED)
        }
        notifyStateChange(ConnectionState.CONNECTED)

        // 如果是重连 (resultFuture == null) 且没有早期推送更新，通知全量快照以刷新 UI
        if (resultFuture == null && pendingUpdatesToDispatch.isEmpty()) {
            notifyPublicUpdateListeners(
                PublicUpdateView(snapshotToDeliver, snapshotToDeliver.cursor, emptyList(), isFullSnapshot = true),
                epoch,
                roomEpoch,
                binding.roomId
            )
        } else {
            for (u in pendingUpdatesToDispatch) {
                notifyPublicUpdateListeners(u, epoch, roomEpoch, binding.roomId)
            }
        }

        if (overflowed) {
            // 早期缓冲曾溢出：按排水后的当前基线立即发起权威对账恢复，绝不静默停留在旧视图
            val overflowCheckpoint = synchronized(publicSyncLock) {
                cachedPublicSnapshot?.let { PublicCheckpoint(it.sessionId, it.syncEpoch, it.cursor) }
            }
            if (overflowCheckpoint != null && !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get()) {
                val token = spectatorStreamEngine.allocateRecoveryToken(epoch, roomEpoch)
                if (activePublicRecoveryToken.compareAndSet(null, token)) {
                    triggerPublicGapRecovery(overflowCheckpoint, token, binding.roomId)
                }
            }
        }

        if (resultFuture != null) {
            dispatch {
                if (closed || epoch != currentEpoch.get()) {
                    resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                } else {
                    resultFuture.complete(snapshotToDeliver)
                }
            }
        }
    }

    // ==========================================
    // LobbySession 大厅期门面实现
    // ==========================================

    override fun listRooms(): CompletableFuture<List<RoomSummaryView>> {
        val resultFuture = CompletableFuture<List<RoomSummaryView>>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        trackFuture(resultFuture, epoch)

        try {
            currentConnection.listRooms().whenComplete { rooms, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed"))
                    } else if (err != null) {
                        resultFuture.completeExceptionally(err)
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapRoomSummaries(rooms))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun createRoom(
        playerName: String?,
        config: MatchConfig?
    ): CompletableFuture<GameSnapshotView> {
        val resultFuture = CompletableFuture<GameSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.incrementAndGet()
        trackFuture(resultFuture, epoch)
        withBothSyncLocks {
            playerStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
            resetSpectatorSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
        }
        activeActionId.set(0L)
        pendingState.set(false)
        transitionState(ConnectionState.CONNECTING, null)

        bindConnectionListeners(currentConnection, epoch)

        val req = CreateRoomRequest(
            playerName = playerName,
            config = config?.let { ProtocolViewMapper.toProtocolMatchConfig(it) },
            protocolVersion = ApiProtocol.PROTOCOL_VERSION
        )
        try {
            currentConnection.createRoom(req).whenComplete { binding, err ->
                if (closed || epoch != currentEpoch.get()) {
                    withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                    dispatch {
                        if (closed || epoch != currentEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Create room failed"))
                        }
                    }
                } else {
                    onBindingEstablished(binding, epoch, resultFuture)
                }
            }
        } catch (t: Throwable) {
            withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun joinRoom(
        roomId: String,
        playerName: String?,
        preferSeat: Int?
    ): CompletableFuture<GameSnapshotView> {
        val resultFuture = CompletableFuture<GameSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.incrementAndGet()
        trackFuture(resultFuture, epoch)
        withBothSyncLocks {
            playerStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
            resetSpectatorSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
        }
        activeActionId.set(0L)
        pendingState.set(false)
        transitionState(ConnectionState.CONNECTING, null)

        bindConnectionListeners(currentConnection, epoch)

        val req = JoinRoomRequest(
            roomId = roomId,
            playerName = playerName,
            preferSeat = preferSeat
        )
        try {
            currentConnection.joinRoom(req).whenComplete { binding, err ->
                if (closed || epoch != currentEpoch.get()) {
                    withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                    dispatch {
                        if (closed || epoch != currentEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Join room failed"))
                        }
                    }
                } else {
                    onBindingEstablished(binding, epoch, resultFuture)
                }
            }
        } catch (t: Throwable) {
            withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun resumeSession(
        roomId: String,
        token: String,
        cursor: Long?
    ): CompletableFuture<GameSnapshotView> {
        val resultFuture = CompletableFuture<GameSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.incrementAndGet()
        trackFuture(resultFuture, epoch)
        withBothSyncLocks {
            playerStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = false)
            resetSpectatorSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
        }
        activeActionId.set(0L)
        pendingState.set(false)
        transitionState(ConnectionState.CONNECTING, null)

        bindConnectionListeners(currentConnection, epoch)

        val req = ResumeRequest(roomId = roomId, token = token, cursor = cursor)
        try {
            currentConnection.resumeSession(req).whenComplete { binding, err ->
                if (closed || epoch != currentEpoch.get()) {
                    withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
                    dispatch {
                        if (closed || epoch != currentEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Resume session failed"))
                        }
                    }
                } else {
                    onBindingEstablished(binding, epoch, resultFuture)
                }
            }
        } catch (t: Throwable) {
            withBothSyncLocks { playerStreamEngine.cancelBindingUnderLock() }
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun joinSpectator(
        roomId: String,
        spectatorName: String?
    ): CompletableFuture<PublicSnapshotView> {
        val resultFuture = CompletableFuture<PublicSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.incrementAndGet()
        trackFuture(resultFuture, epoch)
        withBothSyncLocks {
            resetPlayerSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
            spectatorStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
        }
        activeActionId.set(0L)
        pendingState.set(false)
        transitionState(ConnectionState.CONNECTING, null)

        bindConnectionListeners(currentConnection, epoch)

        val req = JoinSpectatorRequest(
            roomId = roomId,
            spectatorName = spectatorName,
            protocolVersion = ApiProtocol.PROTOCOL_VERSION
        )
        try {
            currentConnection.joinSpectator(req).whenComplete { binding, err ->
                if (closed || epoch != currentEpoch.get()) {
                    withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                    dispatch {
                        if (closed || epoch != currentEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Join spectator failed"))
                        }
                    }
                } else {
                    onSpectatorBindingEstablished(binding, epoch, resultFuture)
                }
            }
        } catch (t: Throwable) {
            withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun resumeSpectator(
        roomId: String,
        spectatorId: String,
        resumeToken: String,
        checkpoint: PublicCheckpoint?
    ): CompletableFuture<PublicSnapshotView> {
        val resultFuture = CompletableFuture<PublicSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.incrementAndGet()
        trackFuture(resultFuture, epoch)
        withBothSyncLocks {
            resetPlayerSyncStateUnderLock(clearEarlyBuffer = true, clearSnapshot = true)
            spectatorStreamEngine.beginBindingUnderLock(clearEarlyBuffer = true, clearSnapshot = false)
        }
        activeActionId.set(0L)
        pendingState.set(false)
        transitionState(ConnectionState.CONNECTING, null)

        bindConnectionListeners(currentConnection, epoch)

        val protoCp = checkpoint?.let {
            com.majiang.server.api.PublicCheckpoint(it.sessionId, it.syncEpoch, it.cursor)
        }
        val req = ResumeSpectatorRequest(
            roomId = roomId,
            spectatorId = spectatorId,
            resumeToken = resumeToken,
            checkpoint = protoCp,
            protocolVersion = ApiProtocol.PROTOCOL_VERSION
        )
        try {
            currentConnection.resumeSpectator(req).whenComplete { binding, err ->
                if (closed || epoch != currentEpoch.get()) {
                    withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
                    dispatch {
                        if (closed || epoch != currentEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Resume spectator failed"))
                        }
                    }
                } else {
                    onSpectatorBindingEstablished(binding, epoch, resultFuture)
                }
            }
        } catch (t: Throwable) {
            withBothSyncLocks { spectatorStreamEngine.cancelBindingUnderLock() }
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun leaveRoom(): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        trackFuture(resultFuture, epoch)

        // 显式离房在途：标记 isLeavingRoom，禁止在途期间网络掉线触发自动重连抢座
        isLeavingRoom.set(true)

        try {
            currentConnection.leaveRoom().whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get()) {
                        isLeavingRoom.set(false)
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        isLeavingRoom.set(false)
                        resultFuture.completeExceptionally(err ?: IOException("Leave room failed"))
                    } else {
                        try {
                            val receipt = ProtocolViewMapper.mapReceipt(protoReceipt)
                            if (receipt.status == ReceiptStatus.APPLIED) {
                                isLeavingRoom.set(false)
                                currentBinding = null
                                currentSpectatorBinding = null
                                activeActionId.set(0L)
                                pendingState.set(false)
                                withBothSyncLocks {
                                    resetPlayerSyncStateUnderLock(clearEarlyBuffer = true)
                                    resetSpectatorSyncStateUnderLock(clearEarlyBuffer = true)
                                }
                                val roomFuturesToFail = roomSyncEngine.retireRoom()
                                for (fut in roomFuturesToFail) {
                                    outstandingFutures.remove(fut)
                                    fut.completeExceptionally(IOException("Room left: binding retired"))
                                }
                            } else {
                                // 离开被拒绝 (REJECTED)：保留现有有效席位绑定，仅复位离房标记
                                isLeavingRoom.set(false)
                            }
                            resultFuture.complete(receipt)
                        } catch (t: Throwable) {
                            isLeavingRoom.set(false)
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            isLeavingRoom.set(false)
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun getRoomInfo(): CompletableFuture<RoomInfo> {
        val resultFuture = CompletableFuture<RoomInfo>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        roomSyncEngine.trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.getRoomInfo().whenComplete { protoInfo, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        if (!resultFuture.isDone) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        }
                    } else if (err != null || protoInfo == null) {
                        if (!resultFuture.isDone) {
                            resultFuture.completeExceptionally(err ?: IOException("getRoomInfo failed"))
                        }
                    } else {
                        try {
                            val finalInfo = roomSyncEngine.adoptAuthoritativeRoomInfo(
                                protoInfo = protoInfo,
                                currentBoundRoomId = { currentBinding?.roomId ?: currentSpectatorBinding?.roomId },
                                requestRoomEpoch = roomEpoch,
                                isSessionValid = {
                                    !closed && !isLeavingRoom.get() &&
                                        epoch == currentEpoch.get() &&
                                        roomEpoch == currentRoomEpoch.get()
                                }
                            )
                            if (finalInfo == null) {
                                if (!resultFuture.isDone) {
                                    resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                                }
                            } else {
                                if (!resultFuture.isDone) {
                                    resultFuture.complete(finalInfo)
                                }
                            }
                        } catch (t: Throwable) {
                            if (!resultFuture.isDone) {
                                resultFuture.completeExceptionally(t)
                            }
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun setReady(ready: Boolean): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.setReady(ready).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("setReady failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun updateConfig(config: RoomConfig): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            val protoConfig = ProtocolViewMapper.toProtocolRoomConfig(config)
            currentConnection.updateConfig(protoConfig).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("updateConfig failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable {
        return roomSyncEngine.onRoomUpdate(listener)
    }

    // ==========================================
    // GameSession 对局期门面实现
    // ==========================================

    override fun snapshot(): CompletableFuture<GameSnapshotView> {
        val resultFuture = CompletableFuture<GameSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.snapshot().whenComplete { protoSnap, err ->
                if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || protoSnap == null) {
                    dispatch {
                        if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Query snapshot failed"))
                        }
                    }
                } else {
                    var finalSnap: GameSnapshotView? = null
                    var mapError: Throwable? = null
                    synchronized(syncLock) {
                        try {
                            finalSnap = playerStreamEngine.adoptQuerySnapshotUnderLock(protoSnap)
                        } catch (t: Throwable) {
                            mapError = t
                        }
                    }
                    if (mapError != null) {
                        dispatch {
                            if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                                resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                            } else {
                                resultFuture.completeExceptionally(mapError)
                            }
                        }
                    } else {
                        val snapToDeliver = finalSnap!!
                        dispatch {
                            if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                                resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                            } else {
                                resultFuture.complete(snapToDeliver)
                            }
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun decision(): CompletableFuture<DecisionView> {
        val resultFuture = CompletableFuture<DecisionView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }

        // 若当前处于缺口修复中，绝不暴露未来决策
        val earlyDecision = synchronized(syncLock) {
            if (playerStreamEngine.reorderBufferNotEmpty && cachedSnapshot != null) {
                cachedSnapshot!!.decision
            } else {
                null
            }
        }
        if (earlyDecision != null) {
            val epoch = currentEpoch.get()
            val roomEpoch = currentRoomEpoch.get()
            trackFuture(resultFuture, epoch)
            trackRoomFuture(resultFuture, roomEpoch)
            dispatch {
                if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                    resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                } else {
                    resultFuture.complete(earlyDecision)
                }
            }
            return resultFuture
        }

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        val requestCursor = synchronized(syncLock) { cachedSnapshot?.cursor ?: -1L }
        val requestRoundId = synchronized(syncLock) { cachedSnapshot?.roundId }

        try {
            currentConnection.decision().whenComplete { protoDec, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoDec == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Query decision failed"))
                    } else {
                        try {
                            val mapped = ProtocolViewMapper.mapDecision(protoDec)
                            val confirmed = synchronized(syncLock) {
                                val cur = cachedSnapshot
                                if (cur != null) {
                                    val updatesAdvanced = cur.cursor > requestCursor || cur.roundId != requestRoundId
                                    val newerDecision = cur.decision.decisionId != mapped.decisionId && cur.cursor >= requestCursor
                                    val newerEpoch = cur.decision.controlEpoch > mapped.controlEpoch
                                    if (updatesAdvanced || newerDecision || newerEpoch) {
                                        cur.decision
                                    } else {
                                        mapped
                                    }
                                } else {
                                    mapped
                                }
                            }
                            resultFuture.complete(confirmed)
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun submit(choice: ActionChoice): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = choice.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoChoice = ProtocolViewMapper.toProtocolActionChoice(choice)
            currentConnection.submit(protoChoice).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit action failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            dispatch { resultFuture.completeExceptionally(e) }
        }
        return resultFuture
    }

    override fun submitShufflePlan(plan: ShufflePlan): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = plan.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoPlan = ProtocolViewMapper.toProtocolShufflePlan(plan)
            currentConnection.submitShufflePlan(protoPlan).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit shuffle plan failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            dispatch { resultFuture.completeExceptionally(e) }
        }
        return resultFuture
    }

    override fun submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolTileMarkRequest(request)
            currentConnection.submitTileMark(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit tile mark failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            dispatch { resultFuture.completeExceptionally(e) }
        }
        return resultFuture
    }

    override fun submitSleeveBloom(request: SleeveBloomRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolSleeveBloomRequest(request)
            currentConnection.submitSleeveBloom(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit sleeve bloom failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            resultFuture.completeExceptionally(e)
        }
        return resultFuture
    }

    override fun submitSleeveBloomChoice(request: SleeveBloomChoiceRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolSleeveBloomChoiceRequest(request)
            currentConnection.submitSleeveBloomChoice(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit sleeve bloom choice failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            resultFuture.completeExceptionally(e)
        }
        return resultFuture
    }

    override fun submitSleeveBloomOffTurn(request: SleeveBloomOffTurnRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolSleeveBloomOffTurnRequest(request)
            currentConnection.submitSleeveBloomOffTurn(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit off-turn sleeve bloom failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            resultFuture.completeExceptionally(e)
        }
        return resultFuture
    }

    override fun submitSleeveBloomOffTurnChoice(request: SleeveBloomOffTurnChoiceRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolSleeveBloomOffTurnChoiceRequest(request)
            currentConnection.submitSleeveBloomOffTurnChoice(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit off-turn sleeve bloom choice failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            resultFuture.completeExceptionally(e)
        }
        return resultFuture
    }

    override fun submitBeamSwap(request: BeamSwapRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolBeamSwapRequest(request)
            currentConnection.submitBeamSwap(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit beam swap failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            resultFuture.completeExceptionally(e)
        }
        return resultFuture
    }

    override fun submitBeamSwapChoice(request: BeamSwapChoiceRequest): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        if (!pendingState.compareAndSet(false, true)) {
            val rejected = Receipt(
                requestId = request.requestId,
                status = ReceiptStatus.REJECTED,
                reason = "ACTION_IN_FLIGHT"
            )
            dispatch { resultFuture.complete(rejected) }
            return resultFuture
        }

        val actionId = actionSubmissionSeq.incrementAndGet()
        activeActionId.set(actionId)

        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)
        resultFuture.whenComplete { _, _ ->
            if (activeActionId.compareAndSet(actionId, 0L)) {
                pendingState.set(false)
            }
        }

        try {
            val protoRequest = ProtocolViewMapper.toProtocolBeamSwapChoiceRequest(request)
            currentConnection.submitBeamSwapChoice(protoRequest).whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        return@dispatch
                    }
                    if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Submit beam swap choice failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (e: Throwable) {
            resultFuture.completeExceptionally(e)
        }
        return resultFuture
    }

    override fun start(): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.start().whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Start match failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun nextRound(): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.nextRound().whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Next round failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun resume(cursor: Long): CompletableFuture<GameSnapshotView> {
        val resultFuture = CompletableFuture<GameSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.resume(cursor).whenComplete { protoSnap, err ->
                if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || protoSnap == null) {
                    dispatch {
                        if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                            resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                        } else {
                            resultFuture.completeExceptionally(err ?: IOException("Resume failed"))
                        }
                    }
                } else {
                    var finalSnap: GameSnapshotView? = null
                    var mapError: Throwable? = null
                    synchronized(syncLock) {
                        try {
                            finalSnap = playerStreamEngine.adoptQuerySnapshotUnderLock(protoSnap)
                        } catch (t: Throwable) {
                            mapError = t
                        }
                    }
                    if (mapError != null) {
                        dispatch {
                            if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                                resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                            } else {
                                resultFuture.completeExceptionally(mapError)
                            }
                        }
                    } else {
                        val snapToDeliver = finalSnap!!
                        dispatch {
                            if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                                resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                            } else {
                                resultFuture.complete(snapToDeliver)
                            }
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun claimControl(): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.claimControl().whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Claim control failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun releaseControl(): CompletableFuture<Receipt> {
        val resultFuture = CompletableFuture<Receipt>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.releaseControl().whenComplete { protoReceipt, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || protoReceipt == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Release control failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapReceipt(protoReceipt))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun queryRoundResult(roundId: String): CompletableFuture<List<GameEventView>> {
        val resultFuture = CompletableFuture<List<GameEventView>>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentSpectatorBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call player API while in SPECTATOR mode")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.queryRoundResult(roundId).whenComplete { events, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null || events == null) {
                        resultFuture.completeExceptionally(err ?: IOException("Query round result failed"))
                    } else {
                        try {
                            resultFuture.complete(ProtocolViewMapper.mapEvents(events))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    // ==========================================
    // SpectatorSession 观战期门面实现
    // ==========================================

    override fun publicSnapshot(): CompletableFuture<PublicSnapshotView> {
        val resultFuture = CompletableFuture<PublicSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call spectator API while in PLAYER mode")) }
            return resultFuture
        }
        val boundRoomId = currentSpectatorBinding?.roomId ?: run {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Not bound to a spectator session")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        try {
            currentConnection.publicSnapshot().whenComplete { protoSnap, err ->
                dispatch {
                    val generationValid = !closed && epoch == currentEpoch.get() &&
                        roomEpoch == currentRoomEpoch.get() && currentSpectatorBinding != null &&
                        currentSpectatorBinding?.roomId == boundRoomId
                    if (!generationValid) {
                        // 离房/换绑/换角色后迟到的旧查询：直接失败，绝不把旧流写回新缓存
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null) {
                        resultFuture.completeExceptionally(err)
                    } else if (protoSnap == null) {
                        resultFuture.completeExceptionally(IOException("Null public snapshot received"))
                    } else {
                        try {
                            val view = ProtocolViewMapper.mapPublicSnapshot(protoSnap)
                            val adopted = adoptAuthoritativePublicSnapshotUnderLock(view, epoch, roomEpoch, boundRoomId)
                            if (!adopted) {
                                resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                            } else {
                                resultFuture.complete(view)
                            }
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    /**
     * 受当前绑定代次保护的权威恢复/查询响应写回：
     * 必须在 publicSyncLock 内严格复核发起时的 epoch、roomEpoch 与 boundRoomId。
     * 若在等待锁期间主线程已离房、换绑或代次变更，拒绝采纳并返回 false。
     */
    private fun adoptAuthoritativePublicSnapshotUnderLock(
        view: PublicSnapshotView,
        epoch: Long,
        roomEpoch: Long,
        roomId: String
    ): Boolean {
        return spectatorStreamEngine.adoptAuthoritativePublicSnapshotUnderLock(
            view = view,
            epoch = epoch,
            roomEpoch = roomEpoch,
            roomId = roomId,
            boundRoomIdProvider = { currentSpectatorBinding?.roomId },
            isSessionValid = { !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get() }
        )
    }

    override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<PublicSnapshotView> {
        val resultFuture = CompletableFuture<PublicSnapshotView>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call spectator API while in PLAYER mode")) }
            return resultFuture
        }
        val boundRoomId = currentSpectatorBinding?.roomId ?: run {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Not bound to a spectator session")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        val roomEpoch = currentRoomEpoch.get()
        trackFuture(resultFuture, epoch)
        trackRoomFuture(resultFuture, roomEpoch)

        val protoCp = com.majiang.server.api.PublicCheckpoint(
            sessionId = checkpoint.sessionId,
            syncEpoch = checkpoint.syncEpoch,
            cursor = checkpoint.cursor
        )
        try {
            currentConnection.resumePublic(protoCp).whenComplete { protoSnap, err ->
                dispatch {
                    val generationValid = !closed && epoch == currentEpoch.get() &&
                        roomEpoch == currentRoomEpoch.get() && currentSpectatorBinding != null &&
                        currentSpectatorBinding?.roomId == boundRoomId
                    if (!generationValid) {
                        // 离房/换绑/换角色后迟到的旧恢复：直接失败，绝不把旧流写回新缓存
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null) {
                        resultFuture.completeExceptionally(err)
                    } else if (protoSnap == null) {
                        resultFuture.completeExceptionally(IOException("Null public snapshot received"))
                    } else {
                        try {
                            val view = ProtocolViewMapper.mapPublicSnapshot(protoSnap)
                            val adopted = adoptAuthoritativePublicSnapshotUnderLock(view, epoch, roomEpoch, boundRoomId)
                            if (!adopted) {
                                resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                            } else {
                                resultFuture.complete(view)
                            }
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<GameEventView>> {
        val resultFuture = CompletableFuture<List<GameEventView>>()
        if (closed) {
            dispatch { resultFuture.completeExceptionally(IOException("Session closed")) }
            return resultFuture
        }
        if (currentBinding != null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Cannot call spectator API while in PLAYER mode")) }
            return resultFuture
        }
        if (currentSpectatorBinding == null) {
            dispatch { resultFuture.completeExceptionally(IllegalStateException("Not bound to a spectator session")) }
            return resultFuture
        }
        val epoch = currentEpoch.get()
        trackFuture(resultFuture, epoch)

        try {
            currentConnection.queryPublicRoundResult(roundId).whenComplete { events, err ->
                dispatch {
                    if (closed || epoch != currentEpoch.get()) {
                        resultFuture.completeExceptionally(IOException("Session closed or generation changed"))
                    } else if (err != null) {
                        resultFuture.completeExceptionally(err)
                    } else {
                        try {
                            val mapped = events.orEmpty().map {
                                val view = ProtocolViewMapper.mapGameEvent(it)
                                validatePublicGameEventView(view)
                                view
                            }
                            resultFuture.complete(Collections.unmodifiableList(mapped))
                        } catch (t: Throwable) {
                            resultFuture.completeExceptionally(t)
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            dispatch { resultFuture.completeExceptionally(t) }
        }
        return resultFuture
    }

    override fun onPublicUpdate(listener: (PublicUpdateView) -> Unit): AutoCloseable {
        return spectatorStreamEngine.onPublicUpdate(listener)
    }

    private fun terminateSession(reason: String) {
        if (isClosed.compareAndSet(false, true)) {
            activeReconnectTimer.getAndSet(null)?.cancel()
            val wasLeaving = isLeavingRoom.getAndSet(false)
            if (wasLeaving) {
                currentBinding = null
                currentSpectatorBinding = null
            }
            transitionState(ConnectionState.CLOSED, reason)

            // 全量在途 Future 均通过 dispatch 派发异常终结
            val futuresToFail = outstandingFutures.keys.toList()
            outstandingFutures.clear()
            val roomFuturesToFail = roomSyncEngine.close()
            for (fut in roomFuturesToFail) {
                outstandingFutures.remove(fut)
            }
            for (fut in futuresToFail) {
                dispatch {
                    fut.completeExceptionally(IOException("Session closed: $reason"))
                }
            }
            for (fut in roomFuturesToFail) {
                dispatch {
                    fut.completeExceptionally(IOException("Session closed: $reason"))
                }
            }
            activeActionId.set(0L)
            pendingState.set(false)

            activeConnectionSub?.close()
            activeDisconnectSub?.close()
            activeRoomSub?.close()
            activeRoomSub = null
            activePublicConnectionSub?.close()
            activePublicConnectionSub = null

            try {
                currentConnection.close()
            } catch (_: Throwable) {
            }

            playerStreamEngine.close()
            spectatorStreamEngine.close()

            if (ownsScheduler && scheduler is AutoCloseable) {
                try {
                    scheduler.close()
                } catch (_: Throwable) {
                }
            }

            notifyDisconnect(reason)
        }
    }

    override fun close() {
        val reason = disconnectReasonRef.get() ?: ApiProtocol.LOCAL_CLOSED
        terminateSession(reason)
    }

    companion object {
        /**
         * 建立远端 TCP 连接并装配统一运行时与自动重连支持 (T3/T5)
         *
         * TcpClientBinding 实例在重连全程复用；factory 闭包首次返回初始绑定，
         * 后续重连调用同一绑定的 [com.majiang.client.transport.tcp.TcpClientBinding.reconnect]。
         *
         * @param host 远端服务器主机名
         * @param port 远端服务器端口
         * @param clock 注入的传输时钟实现
         * @param scheduler 注入的传输调度器实现（为 null 时内部自建 ScheduledExecutorTransportScheduler）
         * @param dispatch 注入的 UI/主线程派发器
         */
        fun connectTcp(
            host: String,
            port: Int,
            clock: TransportClock = TransportClock.SYSTEM,
            scheduler: TransportScheduler? = null,
            dispatch: ((() -> Unit) -> Unit) = { it() }
        ): CompletableFuture<ServerConnectionSession> {
            val resolvedTcpScheduler = scheduler ?: ScheduledExecutorTransportScheduler()
            val ownsScheduler = (scheduler == null)

            // 首次建连（异步）
            return try {
                TcpClient.connect(
                    host = host,
                    port = port,
                    clock = clock,
                    scheduler = resolvedTcpScheduler
                ).thenApply { initialBinding ->
                    // factory 闭包：重连时建立新 socket 并完成握手（契约3 / R03）
                    val factory: () -> CompletableFuture<ServerConnection> = {
                        initialBinding.reconnect().thenApply { it as ServerConnection }
                    }
                    ServerConnectionSession(
                        initialConnection = initialBinding,
                        connectionFactory = factory,
                        clock = clock,
                        scheduler = resolvedTcpScheduler,
                        dispatch = dispatch,
                        ownsScheduler = ownsScheduler
                    )
                }.whenComplete { _, err ->
                    if (err != null && ownsScheduler && resolvedTcpScheduler is AutoCloseable) {
                        try {
                            resolvedTcpScheduler.close()
                        } catch (_: Throwable) {
                        }
                    }
                }
            } catch (t: Throwable) {
                if (ownsScheduler && resolvedTcpScheduler is AutoCloseable) {
                    try {
                        resolvedTcpScheduler.close()
                    } catch (_: Throwable) {
                    }
                }
                val failed = CompletableFuture<ServerConnectionSession>()
                failed.completeExceptionally(t)
                failed
            }
        }
    }
}

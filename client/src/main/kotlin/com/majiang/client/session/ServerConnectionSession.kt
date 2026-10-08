package com.majiang.client.session

import com.majiang.client.api.*
import com.majiang.client.mapping.ProtocolViewMapper

import com.majiang.client.transport.udp.ScheduledExecutorUdpScheduler
import com.majiang.client.transport.udp.UdpClient
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
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpScheduler
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
 * 包装底层 [ServerConnection]（单机进程内绑定或远端 UDP 绑定），同时实现
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
    val clock: UdpClock = UdpClock.SYSTEM,
    val scheduler: UdpScheduler? = null,
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

    // 玩家快照与同步重排状态
    private val syncLock = Any()
    private var cachedSnapshot: GameSnapshotView? = null
    private val reorderBuffer = TreeMap<Long, GameUpdateView>()
    private val isResuming = AtomicBoolean(false)
    private val isBindingInProgress = AtomicBoolean(false)
    private val earlyUpdateBuffer = ConcurrentLinkedQueue<ProtocolPlayerUpdate>()
    private val appliedBatchCursors = LinkedHashSet<Long>()
    private val maxAppliedBatchCursors = 256
    private val maxBufferSize = 64

    override val currentSnapshot: GameSnapshotView?
        get() = synchronized(syncLock) { cachedSnapshot }

    // 观战快照与同步重排状态
    private val publicSyncLock = Any()
    private var cachedPublicSnapshot: PublicSnapshotView? = null
    private val publicReorderBuffer = TreeMap<Long, PublicUpdateView>()
    private data class PublicRecoveryToken(
        val connectionEpoch: Long,
        val roomEpoch: Long,
        val id: Long
    )
    private val publicRecoverySeq = AtomicLong(0L)
    private val activePublicRecoveryToken = AtomicReference<PublicRecoveryToken?>(null)
    private val isSpectatorBindingInProgress = AtomicBoolean(false)
    private val earlyPublicUpdateBuffer = ConcurrentLinkedQueue<ProtocolPublicUpdate>()
    private val earlyPublicUpdateOverflowed = AtomicBoolean(false)
    private val appliedPublicBatchCursors = LinkedHashSet<Long>()
    private val publicUpdateListeners = CopyOnWriteArrayList<(PublicUpdateView) -> Unit>()
    private var activePublicConnectionSub: AutoCloseable? = null

    override val currentPublicSnapshot: PublicSnapshotView?
        get() = synchronized(publicSyncLock) { cachedPublicSnapshot }

    // 房间版本与信息缓存
    private val roomLock = Any()
    private var cachedRoomInfo: RoomInfo? = null
    private var cachedRoomVersion: Long? = null
    private val currentRoomEpoch = AtomicLong(0L)

    private class RoomObserver(val listener: (RoomInfo) -> Unit) {
        @Volatile
        var active: Boolean = true
        val listenerLock = Any()
        var lastDeliveredRoomEpoch: Long = -1L
        var lastDeliveredVersion: Long = -1L
    }

    // 监听器集合
    private val updateListeners = CopyOnWriteArrayList<(GameUpdateView) -> Unit>()
    private val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()
    private val stateListeners = CopyOnWriteArrayList<(ConnectionState) -> Unit>()
    private val roomObservers = CopyOnWriteArrayList<RoomObserver>()

    // 在途操作 Future 追踪（保证关闭与换绑时全量以异常完成）
    private val outstandingFutures = ConcurrentHashMap<CompletableFuture<*>, Long>()
    private val outstandingRoomFutures = ConcurrentHashMap<CompletableFuture<*>, Long>()
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
        outstandingRoomFutures[future] = roomEpoch
        future.whenComplete { _, _ ->
            outstandingRoomFutures.remove(future)
        }
        return future
    }

    // ==========================================
    // 监听器注册与派发机制
    // ==========================================

    override fun onUpdate(listener: (GameUpdateView) -> Unit): AutoCloseable {
        updateListeners.add(listener)
        return AutoCloseable {
            updateListeners.remove(listener)
        }
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
        for (listener in updateListeners) {
            dispatch {
                if (!updateListeners.contains(listener)) return@dispatch
                if (!closed && epoch == currentEpoch.get()) {
                    try {
                        listener(update)
                    } catch (_: Throwable) {
                        // 隔离监听器异常
                    }
                }
            }
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
        val currentRoom = currentBinding?.roomId ?: currentSpectatorBinding?.roomId ?: return
        if (protoRoomInfo.roomId != currentRoom) {
            return
        }
        val mapped = try {
            ProtocolViewMapper.mapRoomInfo(protoRoomInfo)
        } catch (_: Throwable) {
            return
        }
        val roomEpoch = currentRoomEpoch.get()
        val shouldNotify = synchronized(roomLock) {
            if (closed || isLeavingRoom.get() || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                return@synchronized false
            }
            // 当前房间身份判定统一为合法玩家绑定或观战绑定，观战同样允许接收 ROOM 更新
            val curRoomId = currentBinding?.roomId ?: currentSpectatorBinding?.roomId
                ?: return@synchronized false
            if (protoRoomInfo.roomId != curRoomId) {
                return@synchronized false
            }
            val curVer = cachedRoomVersion
            if (curVer == null || mapped.version > curVer) {
                cachedRoomVersion = mapped.version
                cachedRoomInfo = mapped
                true
            } else {
                false
            }
        }
        if (shouldNotify) {
            notifyRoomUpdateListeners(mapped, epoch, roomEpoch)
        }
    }

    private fun notifyRoomUpdateListeners(info: RoomInfo, epoch: Long, roomEpoch: Long) {
        for (observer in roomObservers) {
            dispatch {
                if (!observer.active || !roomObservers.contains(observer)) return@dispatch
                if (closed) return@dispatch
                if (epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) return@dispatch
                val outerRoomId = currentBinding?.roomId ?: currentSpectatorBinding?.roomId
                if (outerRoomId == null || outerRoomId != info.roomId) return@dispatch

                synchronized(observer.listenerLock) {
                    if (!observer.active || !roomObservers.contains(observer)) return@dispatch
                    if (closed) return@dispatch
                    if (epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) return@dispatch
                    val innerRoomId = currentBinding?.roomId ?: currentSpectatorBinding?.roomId
                    if (innerRoomId == null || innerRoomId != info.roomId) return@dispatch

                    val isStale = synchronized(roomLock) {
                        if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                            true
                        } else {
                            val staleRoomId = currentBinding?.roomId ?: currentSpectatorBinding?.roomId
                            if (staleRoomId == null || staleRoomId != info.roomId) {
                                true
                            } else {
                                val curVer = cachedRoomVersion
                                curVer != null && info.version < curVer
                            }
                        }
                    }
                    if (isStale) return@dispatch

                    if (observer.lastDeliveredRoomEpoch == roomEpoch && info.version <= observer.lastDeliveredVersion) {
                        return@dispatch
                    }

                    observer.lastDeliveredRoomEpoch = roomEpoch
                    observer.lastDeliveredVersion = info.version

                    try {
                        observer.listener(info)
                    } catch (_: Throwable) {
                        // 隔离监听器异常
                    }
                }
            }
        }
    }

    // ==========================================
    // 入站更新同步裁决与乱序重排处理
    // ==========================================

    private fun handleInboundUpdate(protoUpdate: ProtocolPlayerUpdate, epoch: Long) {
        if (closed || epoch != currentEpoch.get()) return

        val updatesToDispatch = mutableListOf<GameUpdateView>()
        var shouldRecover = false
        var recoverCursor = 0L

        synchronized(syncLock) {
            // 绑定建立前（建房/加房/重连握手期），新连接推送进入早期缓冲
            if (isBindingInProgress.get()) {
                if (earlyUpdateBuffer.size < maxBufferSize) {
                    earlyUpdateBuffer.add(protoUpdate)
                }
                return
            }

            val current = cachedSnapshot ?: return

            val updateView = ProtocolViewMapper.mapUpdate(protoUpdate)
            val updateSnap = updateView.snapshot

            // 1. 验证会话 ID 与观察者席位
            if (updateSnap.sessionId != current.sessionId ||
                updateSnap.view.viewerSeat != current.view.viewerSeat
            ) {
                return
            }

            // 2. 旧/异 epoch PUSH 永远不能自行替换当前绑定（不同 epoch 只能由有效绑定/恢复响应建立）
            if (updateSnap.syncEpoch != current.syncEpoch) {
                return
            }

            // 3. 检查是否具备显式全量标记
            if (updateView.isFullSnapshot) {
                if (updateSnap.cursor >= current.cursor) {
                    cachedSnapshot = updateSnap
                    reorderBuffer.headMap(updateSnap.cursor).clear()
                    isResuming.set(false)
                    updatesToDispatch.add(updateView)
                }
            } else if (updateSnap.cursor <= current.cursor) {
                // 游标已落后或重复，安全丢弃
                return
            } else if (updateView.fromCursor > current.cursor) {
                // 发现 GAP：暂存乱序缓冲，拦截未来决策
                if (reorderBuffer.size < maxBufferSize) {
                    reorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                }
                if (isResuming.compareAndSet(false, true)) {
                    shouldRecover = true
                    recoverCursor = current.cursor
                }
            } else {
                // 连续到达或重叠向前推进
                applyUpdateUnderLock(updateView, updatesToDispatch)
                drainReorderBufferUnderLock(updatesToDispatch)

                // 检查排水后是否仍有后续缺口需要继续触发恢复
                val curAfterDrain = cachedSnapshot?.cursor ?: 0L
                if (reorderBuffer.isNotEmpty() && reorderBuffer.firstKey() > curAfterDrain) {
                    if (isResuming.compareAndSet(false, true)) {
                        shouldRecover = true
                        recoverCursor = curAfterDrain
                    }
                }
            }
        }

        if (shouldRecover) {
            triggerGapRecovery(recoverCursor, epoch)
        }

        for (u in updatesToDispatch) {
            notifyUpdateListeners(u)
        }
    }

    private fun applyUpdateUnderLock(
        update: GameUpdateView,
        outList: MutableList<GameUpdateView>
    ) {
        val cursor = update.snapshot.cursor
        if (!appliedBatchCursors.add(cursor)) {
            return
        }
        if (appliedBatchCursors.size > maxAppliedBatchCursors) {
            val it = appliedBatchCursors.iterator()
            if (it.hasNext()) {
                it.next()
                it.remove()
            }
        }
        cachedSnapshot = update.snapshot
        outList.add(update)
    }

    private fun drainReorderBufferUnderLock(outList: MutableList<GameUpdateView>) {
        while (true) {
            val cur = cachedSnapshot ?: break
            val next = reorderBuffer.remove(cur.cursor) ?: break
            applyUpdateUnderLock(next, outList)
        }
    }

    private fun triggerGapRecovery(cursor: Long, epoch: Long) {
        try {
            currentConnection.resume(cursor).whenComplete { protoSnapshot, err ->
                try {
                    if (closed || epoch != currentEpoch.get()) {
                        isResuming.set(false)
                        return@whenComplete
                    }
                    if (err != null || protoSnapshot == null) {
                        // 恢复失败释放标记，后续收包可继续重试，杜绝挂死
                        isResuming.set(false)
                        return@whenComplete
                    }

                    var updateToDispatch: GameUpdateView? = null
                    val subsequentUpdates = mutableListOf<GameUpdateView>()
                    var shouldTriggerNextRecovery = false
                    var nextCursor = 0L

                    synchronized(syncLock) {
                        isResuming.set(false)
                        val current = cachedSnapshot
                        if (current != null && protoSnapshot.sessionId == current.sessionId &&
                            protoSnapshot.view.viewerSeat == current.view.viewerSeat &&
                            protoSnapshot.cursor >= current.cursor
                        ) {
                            val initialCursor = current.cursor
                            val restored = ProtocolViewMapper.mapSnapshot(protoSnapshot)

                            val snapshotAdvanced = restored.cursor > initialCursor
                            if (snapshotAdvanced) {
                                cachedSnapshot = restored
                                reorderBuffer.headMap(restored.cursor).clear()
                                updateToDispatch = GameUpdateView(restored, restored.cursor, emptyList(), isFullSnapshot = true)
                            }

                            drainReorderBufferUnderLock(subsequentUpdates)

                            val curAfterDrain = cachedSnapshot?.cursor ?: initialCursor
                            val madeProgress = curAfterDrain > initialCursor

                            // 仅当本次恢复取得了实际进展，且排水后仍有后续未解决缺口时，才继续触发下一次恢复
                            if (madeProgress && reorderBuffer.isNotEmpty() && reorderBuffer.firstKey() > curAfterDrain) {
                                if (isResuming.compareAndSet(false, true)) {
                                    shouldTriggerNextRecovery = true
                                    nextCursor = curAfterDrain
                                }
                            }
                        }
                    }

                    updateToDispatch?.let { notifyUpdateListeners(it) }
                    for (u in subsequentUpdates) {
                        notifyUpdateListeners(u)
                    }

                    if (shouldTriggerNextRecovery) {
                        triggerGapRecovery(nextCursor, epoch)
                    }
                } catch (_: Throwable) {
                    isResuming.set(false)
                }
            }
        } catch (_: Throwable) {
            isResuming.set(false)
        }
    }

    // ==========================================
    // 观战入站更新同步裁决与乱序重排处理
    // ==========================================

    private fun handleInboundPublicUpdate(protoUpdate: ProtocolPublicUpdate, epoch: Long) {
        if (closed || epoch != currentEpoch.get()) return

        val updatesToDispatch = mutableListOf<PublicUpdateView>()
        var shouldRecover = false
        var checkpointToRecover: PublicCheckpoint? = null
        var recoveryToken: PublicRecoveryToken? = null
        val roomEpoch = currentRoomEpoch.get()
        val boundRoomId: String

        synchronized(publicSyncLock) {
            if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                return
            }

            // 绑定建立前（观战加入/恢复握手期），新连接推送进入早期缓冲
            if (isSpectatorBindingInProgress.get()) {
                if (earlyPublicUpdateBuffer.size < maxBufferSize) {
                    earlyPublicUpdateBuffer.add(protoUpdate)
                } else {
                    // 有界缓冲溢出：标记后由绑定确立流程发起权威对账恢复，绝不静默丢弃最后一次变化
                    earlyPublicUpdateOverflowed.set(true)
                }
                return
            }

            val current = cachedPublicSnapshot ?: return
            // 观战绑定已失效（离房/换角色后）的迟到公开推送不消费，也不污染新缓存
            val curBinding = currentSpectatorBinding ?: return
            boundRoomId = curBinding.roomId

            val updateView = ProtocolViewMapper.mapPublicUpdate(protoUpdate)
            val updateSnap = updateView.snapshot

            // 1. 验证会话 ID 与 syncEpoch 代次
            if (updateSnap.sessionId != current.sessionId || updateSnap.syncEpoch != current.syncEpoch) {
                return
            }

            // 2. 检查是否具备显式全量标记
            if (updateView.isFullSnapshot) {
                // full 仅当前流且不回退；旧 full / 同游标重复 full 是合法重复，仅更新/清理重排缓冲，不重复派发
                if (updateSnap.cursor > current.cursor) {
                    cachedPublicSnapshot = updateSnap
                    publicReorderBuffer.headMap(updateSnap.cursor).clear()
                    activePublicRecoveryToken.set(null)
                    updatesToDispatch.add(updateView)
                    drainPublicReorderBufferUnderLock(updatesToDispatch)
                } else {
                    publicReorderBuffer.headMap(updateSnap.cursor).clear()
                }
            } else if (updateSnap.cursor <= current.cursor) {
                // 游标已落后或重复，安全丢弃
                return
            } else if (updateView.fromCursor != current.cursor) {
                // 严格公开顺序：普通更新的 fromCursor 必须严格等于当前基线 cursor
                if (updateView.fromCursor > current.cursor) {
                    if (publicReorderBuffer.size < maxBufferSize) {
                        publicReorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                    }
                    val curToken = activePublicRecoveryToken.get()
                    if (curToken == null || curToken.connectionEpoch != epoch || curToken.roomEpoch != roomEpoch) {
                        val token = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
                        activePublicRecoveryToken.set(token)
                        recoveryToken = token
                        shouldRecover = true
                        checkpointToRecover = PublicCheckpoint(current.sessionId, current.syncEpoch, current.cursor)
                    }
                }
            } else {
                // updateView.fromCursor == current.cursor 严格连续到达
                applyPublicUpdateUnderLock(updateView, updatesToDispatch)
                drainPublicReorderBufferUnderLock(updatesToDispatch)

                val curAfterDrain = cachedPublicSnapshot?.cursor ?: 0L
                if (publicReorderBuffer.isNotEmpty() && publicReorderBuffer.firstKey() > curAfterDrain) {
                    val curToken = activePublicRecoveryToken.get()
                    if (curToken == null || curToken.connectionEpoch != epoch || curToken.roomEpoch != roomEpoch) {
                        val token = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
                        activePublicRecoveryToken.set(token)
                        recoveryToken = token
                        shouldRecover = true
                        checkpointToRecover = PublicCheckpoint(current.sessionId, current.syncEpoch, curAfterDrain)
                    }
                }
            }
        }

        if (shouldRecover && checkpointToRecover != null && recoveryToken != null) {
            triggerPublicGapRecovery(checkpointToRecover, recoveryToken, boundRoomId)
        }

        for (u in updatesToDispatch) {
            notifyPublicUpdateListeners(u, epoch, roomEpoch, boundRoomId)
        }
    }

    private fun applyPublicUpdateUnderLock(
        update: PublicUpdateView,
        outList: MutableList<PublicUpdateView>
    ) {
        val current = cachedPublicSnapshot ?: return
        if (update.fromCursor != current.cursor) return
        if (update.snapshot.cursor <= current.cursor) return

        cachedPublicSnapshot = update.snapshot
        outList.add(update)
    }

    private fun drainPublicReorderBufferUnderLock(outList: MutableList<PublicUpdateView>) {
        while (true) {
            val current = cachedPublicSnapshot ?: break
            val nextUpdate = publicReorderBuffer[current.cursor] ?: break
            publicReorderBuffer.remove(current.cursor)
            applyPublicUpdateUnderLock(nextUpdate, outList)
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
                if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                    // 旧连接/旧房间代次的迟到恢复响应：不写回缓存，也不清除新代次的恢复在途标记
                    return@whenComplete
                }
                if (err != null || protoSnapshot == null) {
                    // 恢复失败：仅当当前 activePublicRecoveryToken 仍为本次 token 时释放标记
                    activePublicRecoveryToken.compareAndSet(token, null)
                    return@whenComplete
                }
                try {
                    var updateToDispatch: PublicUpdateView? = null
                    val subsequentUpdates = mutableListOf<PublicUpdateView>()
                    var shouldTriggerNextRecovery = false
                    var nextCheckpoint: PublicCheckpoint? = null
                    var nextToken: PublicRecoveryToken? = null

                    synchronized(publicSyncLock) {
                        if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                            return@synchronized
                        }
                        val curSpec = currentSpectatorBinding
                        if (curSpec == null || curSpec.roomId != roomId) {
                            return@synchronized
                        }
                        if (!activePublicRecoveryToken.compareAndSet(token, null)) {
                            return@synchronized
                        }
                        val current = cachedPublicSnapshot
                        if (current != null &&
                            protoSnapshot.sessionId == current.sessionId &&
                            protoSnapshot.syncEpoch == current.syncEpoch &&
                            protoSnapshot.cursor >= current.cursor
                        ) {
                            val initialCursor = current.cursor
                            val restored = ProtocolViewMapper.mapPublicSnapshot(protoSnapshot)

                            val snapshotAdvanced = restored.cursor > initialCursor
                            if (snapshotAdvanced) {
                                cachedPublicSnapshot = restored
                                publicReorderBuffer.headMap(restored.cursor).clear()
                                updateToDispatch = PublicUpdateView(
                                    snapshot = restored,
                                    fromCursor = restored.cursor,
                                    visibleEvents = emptyList(),
                                    isFullSnapshot = true
                                )
                            }

                            drainPublicReorderBufferUnderLock(subsequentUpdates)

                            val curAfterDrain = cachedPublicSnapshot?.cursor ?: initialCursor
                            val madeProgress = curAfterDrain > initialCursor

                            if (madeProgress && publicReorderBuffer.isNotEmpty() && publicReorderBuffer.firstKey() > curAfterDrain) {
                                val nToken = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
                                activePublicRecoveryToken.set(nToken)
                                nextToken = nToken
                                shouldTriggerNextRecovery = true
                                nextCheckpoint = PublicCheckpoint(current.sessionId, current.syncEpoch, curAfterDrain)
                            }
                        }
                    }

                    updateToDispatch?.let { notifyPublicUpdateListeners(it, epoch, roomEpoch, roomId) }
                    for (u in subsequentUpdates) {
                        notifyPublicUpdateListeners(u, epoch, roomEpoch, roomId)
                    }

                    if (shouldTriggerNextRecovery && nextCheckpoint != null && nextToken != null) {
                        triggerPublicGapRecovery(nextCheckpoint, nextToken, roomId)
                    }
                } catch (_: Throwable) {
                    activePublicRecoveryToken.compareAndSet(token, null)
                }
            }
        } catch (_: Throwable) {
            activePublicRecoveryToken.compareAndSet(token, null)
        }
    }

    private fun notifyPublicUpdateListeners(
        update: PublicUpdateView,
        epoch: Long,
        roomEpoch: Long,
        roomId: String
    ) {
        dispatch {
            for (listener in publicUpdateListeners) {
                if (closed) break
                if (epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) break
                val currentSpec = currentSpectatorBinding
                if (currentSpec == null || currentSpec.roomId != roomId) break
                if (cachedPublicSnapshot == null) break
                try {
                    listener(update)
                } catch (_: Throwable) {
                    // 隔离监听器异常
                }
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
                isSpectatorBindingInProgress.set(true)
                synchronized(publicSyncLock) {
                    earlyPublicUpdateBuffer.clear()
                    earlyPublicUpdateOverflowed.set(false)
                    appliedPublicBatchCursors.clear()
                    publicReorderBuffer.clear()
                    activePublicRecoveryToken.set(null)
                }
                activeActionId.set(0L)
                pendingState.set(false)
                bindConnectionListeners(newConn, newEpoch)
                try {
                    newConn.resumeSpectator(resumeReq).whenComplete { newSpecBinding, resumeErr ->
                        if (closed || connectionState != ConnectionState.RECONNECTING || newEpoch != currentEpoch.get()) {
                            newConn.close()
                            return@whenComplete
                        }
                        if (resumeErr != null || newSpecBinding == null) {
                            newConn.close()
                            val msg = resumeErr?.message.orEmpty()
                            val isPermanentErr = msg.contains("NOT_FOUND") || msg.contains("INVALID") ||
                                msg.contains("CLOSED") || msg.contains("REJECTED") || msg.contains("KICK")
                            if (isPermanentErr || attempt >= 10) {
                                val finalReason = if (msg.isNotEmpty()) msg else "RESUME_FAILED"
                                terminateSession(finalReason)
                            } else {
                                scheduleReconnect(attempt + 1, initialDelay)
                            }
                            return@whenComplete
                        }

                        currentConnection = newConn
                        onSpectatorBindingEstablished(newSpecBinding, newEpoch, null)
                    }
                } catch (_: Throwable) {
                    newConn.close()
                    scheduleReconnect(attempt + 1, initialDelay)
                }
                return@whenComplete
            }

            // 玩家重连
            val curCursor = synchronized(syncLock) { cachedSnapshot?.cursor ?: 0L }
            val resumeReq = ResumeRequest(roomId = binding!!.roomId, token = binding.resumeToken, cursor = curCursor)
            val newEpoch = currentEpoch.incrementAndGet()

            // 标记重连绑定进行中，重置旧重排/批次/在途状态
            isBindingInProgress.set(true)
            synchronized(syncLock) {
                earlyUpdateBuffer.clear()
                appliedBatchCursors.clear()
                reorderBuffer.clear()
                isResuming.set(false)
            }
            activeActionId.set(0L)
            pendingState.set(false)

            bindConnectionListeners(newConn, newEpoch)

            try {
                newConn.resumeSession(resumeReq).whenComplete { newBinding, resumeErr ->
                    if (closed || connectionState != ConnectionState.RECONNECTING || newEpoch != currentEpoch.get()) {
                        newConn.close()
                        return@whenComplete
                    }
                    if (resumeErr != null || newBinding == null) {
                        newConn.close()
                        val msg = resumeErr?.message.orEmpty()
                        val isPermanentErr = msg.contains("NOT_FOUND") || msg.contains("INVALID") ||
                            msg.contains("CLOSED") || msg.contains("REJECTED") || msg.contains("KICK")
                        if (isPermanentErr || attempt >= 10) {
                            val finalReason = if (msg.isNotEmpty()) msg else "RESUME_FAILED"
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
            } catch (_: Throwable) {
                newConn.close()
                scheduleReconnect(attempt + 1, initialDelay)
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
        val roomEpoch = if (isNewRoom) {
            currentRoomEpoch.incrementAndGet()
        } else {
            currentRoomEpoch.get()
        }

        if (isNewRoom) {
            synchronized(roomLock) {
                cachedRoomInfo = null
                cachedRoomVersion = null
            }
        }

        synchronized(syncLock) {
            if (closed || epoch != currentEpoch.get()) {
                isBindingInProgress.set(false)
            } else {
                currentBinding = binding
                currentSpectatorBinding = null // 互斥：清除观战绑定
                isBindingInProgress.set(false)

                // 清除观战缓存与状态
                synchronized(publicSyncLock) {
                    cachedPublicSnapshot = null
                    publicReorderBuffer.clear()
                    earlyPublicUpdateBuffer.clear()
                    appliedPublicBatchCursors.clear()
                    activePublicRecoveryToken.set(null)
                }

                val initialSnapshotView = ProtocolViewMapper.mapSnapshot(binding.snapshot)
                cachedSnapshot = initialSnapshotView
                var currentBestSnap = initialSnapshotView
                reorderBuffer.clear()
                appliedBatchCursors.clear()
                isResuming.set(false)

                // 排水早期缓冲更新
                val earlyList = mutableListOf<ProtocolPlayerUpdate>()
                while (true) {
                    val u = earlyUpdateBuffer.poll() ?: break
                    earlyList.add(u)
                }
                earlyList.sortBy { it.fromCursor }

                for (protoUpdate in earlyList) {
                    val updateView = ProtocolViewMapper.mapUpdate(protoUpdate)
                    val updateSnap = updateView.snapshot
                    val c = cachedSnapshot ?: break
                    if (updateSnap.sessionId != c.sessionId || updateSnap.view.viewerSeat != c.view.viewerSeat) continue
                    if (updateSnap.syncEpoch != c.syncEpoch) continue

                    if (updateView.isFullSnapshot) {
                        if (updateSnap.cursor >= c.cursor) {
                            cachedSnapshot = updateSnap
                            currentBestSnap = updateSnap
                            reorderBuffer.headMap(updateSnap.cursor).clear()
                            pendingUpdatesToDispatch.add(updateView)
                        }
                        continue
                    }

                    if (updateSnap.cursor <= c.cursor) continue
                    if (updateView.fromCursor > c.cursor) {
                        reorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                    } else {
                        applyUpdateUnderLock(updateView, pendingUpdatesToDispatch)
                        drainReorderBufferUnderLock(pendingUpdatesToDispatch)
                        currentBestSnap = cachedSnapshot ?: currentBestSnap
                    }
                }
                finalSnapshot = currentBestSnap
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
            outstandingRoomFutures.remove(f)
            dispatch {
                f.completeExceptionally(IOException("Generation changed: new binding established"))
            }
        }
        if (isNewRoom) {
            val oldRoomFutures = outstandingRoomFutures.entries
                .filter { it.value < roomEpoch }
                .map { it.key }
            for (f in oldRoomFutures) {
                outstandingFutures.remove(f)
                outstandingRoomFutures.remove(f)
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
        val roomEpoch = if (isNewRoom) {
            currentRoomEpoch.incrementAndGet()
        } else {
            currentRoomEpoch.get()
        }

        if (isNewRoom) {
            synchronized(roomLock) {
                cachedRoomInfo = null
                cachedRoomVersion = null
            }
        }

        synchronized(publicSyncLock) {
            if (closed || epoch != currentEpoch.get()) {
                isSpectatorBindingInProgress.set(false)
            } else {
                currentSpectatorBinding = binding
                currentBinding = null // 互斥：清除玩家绑定
                isSpectatorBindingInProgress.set(false)

                // 清除玩家缓存与状态
                synchronized(syncLock) {
                    cachedSnapshot = null
                    reorderBuffer.clear()
                    earlyUpdateBuffer.clear()
                    appliedBatchCursors.clear()
                    isResuming.set(false)
                }

                val initialSnapshotView = ProtocolViewMapper.mapPublicSnapshot(binding.snapshot)
                cachedPublicSnapshot = initialSnapshotView
                var currentBestSnap = initialSnapshotView
                publicReorderBuffer.clear()
                appliedPublicBatchCursors.clear()
                activePublicRecoveryToken.set(null)

                // 排水早期缓冲更新
                val earlyList = mutableListOf<ProtocolPublicUpdate>()
                while (true) {
                    val u = earlyPublicUpdateBuffer.poll() ?: break
                    earlyList.add(u)
                }

                for (protoUpdate in earlyList) {
                    val updateView = ProtocolViewMapper.mapPublicUpdate(protoUpdate)
                    val updateSnap = updateView.snapshot

                    if (updateSnap.sessionId != currentBestSnap.sessionId ||
                        updateSnap.syncEpoch != currentBestSnap.syncEpoch
                    ) {
                        continue
                    }

                    if (updateView.isFullSnapshot) {
                        if (updateSnap.cursor > currentBestSnap.cursor) {
                            cachedPublicSnapshot = updateSnap
                            currentBestSnap = updateSnap
                            publicReorderBuffer.headMap(updateSnap.cursor).clear()
                            pendingUpdatesToDispatch.add(updateView)
                            drainPublicReorderBufferUnderLock(pendingUpdatesToDispatch)
                            cachedPublicSnapshot?.let { currentBestSnap = it }
                        }
                    } else if (updateSnap.cursor <= currentBestSnap.cursor) {
                        continue
                    } else if (updateView.fromCursor != currentBestSnap.cursor) {
                        if (updateView.fromCursor > currentBestSnap.cursor) {
                            if (publicReorderBuffer.size < maxBufferSize) {
                                publicReorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                            }
                        }
                    } else {
                        applyPublicUpdateUnderLock(updateView, pendingUpdatesToDispatch)
                        drainPublicReorderBufferUnderLock(pendingUpdatesToDispatch)
                        cachedPublicSnapshot?.let { currentBestSnap = it }
                    }
                }

                finalSnapshot = currentBestSnap
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
            outstandingRoomFutures.remove(f)
            dispatch {
                f.completeExceptionally(IOException("Generation changed: new spectator binding established"))
            }
        }
        if (isNewRoom) {
            val oldRoomFutures = outstandingRoomFutures.entries
                .filter { it.value < roomEpoch }
                .map { it.key }
            for (f in oldRoomFutures) {
                outstandingFutures.remove(f)
                outstandingRoomFutures.remove(f)
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

        if (earlyPublicUpdateOverflowed.getAndSet(false)) {
            // 早期缓冲曾溢出：按排水后的当前基线立即发起权威对账恢复，绝不静默停留在旧视图
            val overflowCheckpoint = synchronized(publicSyncLock) {
                cachedPublicSnapshot?.let { PublicCheckpoint(it.sessionId, it.syncEpoch, it.cursor) }
            }
            if (overflowCheckpoint != null && !closed && epoch == currentEpoch.get() && roomEpoch == currentRoomEpoch.get()) {
                val token = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
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
        isBindingInProgress.set(true)
        synchronized(syncLock) {
            earlyUpdateBuffer.clear()
            appliedBatchCursors.clear()
            reorderBuffer.clear()
            isResuming.set(false)
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
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    isBindingInProgress.set(false)
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
            isBindingInProgress.set(false)
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
        isBindingInProgress.set(true)
        synchronized(syncLock) {
            earlyUpdateBuffer.clear()
            appliedBatchCursors.clear()
            reorderBuffer.clear()
            isResuming.set(false)
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
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    isBindingInProgress.set(false)
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
            isBindingInProgress.set(false)
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
        isBindingInProgress.set(true)
        synchronized(syncLock) {
            earlyUpdateBuffer.clear()
            appliedBatchCursors.clear()
            reorderBuffer.clear()
            isResuming.set(false)
        }
        activeActionId.set(0L)
        pendingState.set(false)
        transitionState(ConnectionState.CONNECTING, null)

        bindConnectionListeners(currentConnection, epoch)

        val req = ResumeRequest(roomId = roomId, token = token, cursor = cursor)
        try {
            currentConnection.resumeSession(req).whenComplete { binding, err ->
                if (closed || epoch != currentEpoch.get()) {
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    isBindingInProgress.set(false)
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
            isBindingInProgress.set(false)
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
        isSpectatorBindingInProgress.set(true)
        synchronized(publicSyncLock) {
            earlyPublicUpdateBuffer.clear()
            earlyPublicUpdateOverflowed.set(false)
            appliedPublicBatchCursors.clear()
            publicReorderBuffer.clear()
            activePublicRecoveryToken.set(null)
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
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    isSpectatorBindingInProgress.set(false)
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
            isSpectatorBindingInProgress.set(false)
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
        isSpectatorBindingInProgress.set(true)
        synchronized(publicSyncLock) {
            earlyPublicUpdateBuffer.clear()
            earlyPublicUpdateOverflowed.set(false)
            appliedPublicBatchCursors.clear()
            publicReorderBuffer.clear()
            activePublicRecoveryToken.set(null)
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
                    dispatch { resultFuture.completeExceptionally(IOException("Session closed or generation changed")) }
                    return@whenComplete
                }
                if (err != null || binding == null) {
                    isSpectatorBindingInProgress.set(false)
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
            isSpectatorBindingInProgress.set(false)
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
                                val retiredRoomEpoch = currentRoomEpoch.incrementAndGet()
                                activeActionId.set(0L)
                                pendingState.set(false)
                                synchronized(syncLock) {
                                    cachedSnapshot = null
                                    reorderBuffer.clear()
                                    earlyUpdateBuffer.clear()
                                    appliedBatchCursors.clear()
                                    isResuming.set(false)
                                }
                                synchronized(publicSyncLock) {
                                    cachedPublicSnapshot = null
                                    publicReorderBuffer.clear()
                                    earlyPublicUpdateBuffer.clear()
                                    earlyPublicUpdateOverflowed.set(false)
                                    appliedPublicBatchCursors.clear()
                                    activePublicRecoveryToken.set(null)
                                }
                                synchronized(roomLock) {
                                    cachedRoomInfo = null
                                    cachedRoomVersion = null
                                }
                                val roomFuturesToFail = mutableListOf<CompletableFuture<*>>()
                                val it = outstandingRoomFutures.entries.iterator()
                                while (it.hasNext()) {
                                    val entry = it.next()
                                    if (entry.value <= retiredRoomEpoch) {
                                        it.remove()
                                        outstandingFutures.remove(entry.key)
                                        roomFuturesToFail.add(entry.key)
                                    }
                                }
                                for (fut in roomFuturesToFail) {
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
        trackRoomFuture(resultFuture, roomEpoch)

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
                            val mapped = ProtocolViewMapper.mapRoomInfo(protoInfo)
                            val finalInfo = synchronized(roomLock) {
                                if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                                    null
                                } else {
                                    val curBindingRoomId = currentBinding?.roomId ?: currentSpectatorBinding?.roomId
                                    if (curBindingRoomId == null || curBindingRoomId != mapped.roomId) {
                                        null
                                    } else {
                                        val cur = cachedRoomInfo
                                        if (cur != null && cur.roomId == mapped.roomId && cur.version >= mapped.version) {
                                            cur
                                        } else {
                                            cachedRoomInfo = mapped
                                            cachedRoomVersion = mapped.version
                                            mapped
                                        }
                                    }
                                }
                            }
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
        val observer = RoomObserver(listener)
        roomObservers.add(observer)
        return AutoCloseable {
            observer.active = false
            roomObservers.remove(observer)
        }
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
                            val mapped = ProtocolViewMapper.mapSnapshot(protoSnap)
                            val cur = cachedSnapshot
                            if (cur == null || cur.sessionId != mapped.sessionId ||
                                mapped.syncEpoch != cur.syncEpoch || mapped.cursor >= cur.cursor
                            ) {
                                cachedSnapshot = mapped
                                finalSnap = mapped
                            } else {
                                finalSnap = cur
                            }
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
            if (reorderBuffer.isNotEmpty() && cachedSnapshot != null) {
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
                            val mapped = ProtocolViewMapper.mapSnapshot(protoSnap)
                            val cur = cachedSnapshot
                            if (cur == null || cur.sessionId != mapped.sessionId ||
                                mapped.syncEpoch != cur.syncEpoch || mapped.cursor >= cur.cursor
                            ) {
                                cachedSnapshot = mapped
                                reorderBuffer.headMap(mapped.cursor).clear()
                                finalSnap = mapped
                            } else {
                                finalSnap = cur
                            }
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
        synchronized(publicSyncLock) {
            if (closed || epoch != currentEpoch.get() || roomEpoch != currentRoomEpoch.get()) {
                return false
            }
            val curSpec = currentSpectatorBinding
            if (curSpec == null || curSpec.roomId != roomId) {
                return false
            }
            val cur = cachedPublicSnapshot
            if (cur == null || cur.sessionId != view.sessionId || cur.syncEpoch != view.syncEpoch) {
                cachedPublicSnapshot = view
                publicReorderBuffer.clear()
                return true
            } else if (view.cursor > cur.cursor) {
                cachedPublicSnapshot = view
                publicReorderBuffer.headMap(view.cursor).clear()
                return true
            } else if (view.cursor == cur.cursor) {
                return true
            }
            return false
        }
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
        publicUpdateListeners.add(listener)
        return AutoCloseable {
            publicUpdateListeners.remove(listener)
        }
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
            outstandingRoomFutures.clear()
            for (fut in futuresToFail) {
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
            for (obs in roomObservers) {
                obs.active = false
            }
            roomObservers.clear()
            synchronized(roomLock) {
                cachedRoomInfo = null
                cachedRoomVersion = null
            }

            try {
                currentConnection.close()
            } catch (_: Throwable) {
            }

            synchronized(syncLock) {
                reorderBuffer.clear()
                earlyUpdateBuffer.clear()
            }

            synchronized(publicSyncLock) {
                cachedPublicSnapshot = null
                publicReorderBuffer.clear()
                earlyPublicUpdateBuffer.clear()
                earlyPublicUpdateOverflowed.set(false)
                appliedPublicBatchCursors.clear()
                activePublicRecoveryToken.set(null)
            }

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
         * 建立远端 UDP 连接并装配统一运行时与自动重连支持
         *
         * @param host 远端服务器主机名
         * @param port 远端服务器端口
         * @param clock 注入的协议时钟实现
         * @param scheduler 注入的异步调度器实现
         * @param dispatch 注入的 UI/主线程派发器
         */
        fun connectUdp(
            host: String,
            port: Int,
            clock: UdpClock = UdpClock.SYSTEM,
            scheduler: UdpScheduler? = null,
            dispatch: ((() -> Unit) -> Unit) = { it() }
        ): CompletableFuture<ServerConnectionSession> {
            val resolvedScheduler = scheduler ?: ScheduledExecutorUdpScheduler()
            val ownsScheduler = (scheduler == null)
            val factory = {
                UdpClient.connect(host, port, clock, resolvedScheduler, closeSchedulerOnShutdown = false)
                    .thenApply { it as ServerConnection }
            }
            return try {
                factory().thenApply { initialConn ->
                    ServerConnectionSession(
                        initialConnection = initialConn,
                        connectionFactory = factory,
                        clock = clock,
                        scheduler = resolvedScheduler,
                        dispatch = dispatch,
                        ownsScheduler = ownsScheduler
                    )
                }.whenComplete { _, err ->
                    if (err != null && ownsScheduler && resolvedScheduler is AutoCloseable) {
                        try {
                            resolvedScheduler.close()
                        } catch (_: Throwable) {
                        }
                    }
                }
            } catch (t: Throwable) {
                if (ownsScheduler && resolvedScheduler is AutoCloseable) {
                    try {
                        resolvedScheduler.close()
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

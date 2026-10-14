package com.majiang.server.udp

import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.deepCopy
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.udp.UdpFrameException
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.udp.ResendResponse
import com.majiang.transport.udp.UdpClock
import java.io.IOException
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicReferenceArray

/**
 * UDP 房间传输适配器 (UdpRoom)
 *
 * TASK-GLM-R4B1UDP 规范：本类只保留传输职责，房间业务全部委托给与进程内绑定
 * 完全相同的 [SharedRoom]（成员、准备、配置、开赛、离房、恢复、生命周期与权威队列）：
 * 1. 网络会话管理：维护每座位活跃 [UdpClientSession] 绑定与连接代次校验；
 * 2. 绑定身份：不可猜测的 resumeToken 绑定 (roomId, seat, generation)，
 *    命令执行时校验当前活跃 session 身份与 [SharedRoom.bindingGenerations] 一致；
 *    观战者绑定独立索引 (spectatorId, generation)，不占用四座、不受 seat 语义约束；
 * 3. 重传与终端 ACK：席位推送快照缓存供 RESEND 自愈；顶号 KICK 与房间关闭 KICK
 *    由注入的 kickSender（有界重传 + ACK 追踪）保证必达；
 *    观战公开流经独立 [publicUpdateSender] 可靠下发，PUBLIC_RESEND 严格按
 *    sessionId+syncEpoch+cursor 校验后从公开窗口补发；
 * 4. 房间更新可靠下发：[roomUpdateSender] 携带完整权威房间视图（版本与对局游标分离），
 *    由服务端 ACK/重试机制保证丢失最后一条也能恢复；
 * 5. 顶号换发：[resume]/[resumeSpectator] 恢复原绑定时先向旧连接派发 KICK 再建立新绑定；
 * 6. 显式离开语义：房主离开经 SharedRoom 解散房间并可靠通知其他成员 ROOM_CLOSED；
 *    普通玩家离开后按生命周期释放席位或转交 AI 托管；观战离开撤销成员资格并返回大厅。
 */
class UdpRoom(
    val sharedRoom: SharedRoom,
    val udpClock: UdpClock = UdpClock.SYSTEM,
    private val pushSender: (session: UdpClientSession, update: PlayerUpdate) -> Unit = { _, _ -> },
    private val kickSender: (session: UdpClientSession, reason: String) -> Unit = { _, _ -> },
    private val roomUpdateSender: (session: UdpClientSession, info: ProtocolRoomInfo) -> Unit = { _, _ -> },
    private val publicUpdateSender: (session: UdpClientSession, update: ProtocolPublicUpdate) -> Unit = { _, _ -> }
) : AutoCloseable {

    val roomId: String get() = sharedRoom.roomId
    val hostPlayerName: String get() = sharedRoom.hostPlayerName
    val tableHost get() = sharedRoom.tableHost

    private val isClosed = AtomicBoolean(false)

    /** 对局是否已经开始（由权威房间生命周期推导，非独立状态副本） */
    val isMatchStarted: Boolean
        get() = sharedRoom.lifecycle.let {
            it == ProtocolRoomLifecycle.PLAYING || it == ProtocolRoomLifecycle.PREPARING ||
                it == ProtocolRoomLifecycle.SETTLED
        }

    /**
     * 每座位活跃绑定记录：会话状态
     */
    class SeatBinding(val session: UdpClientSession)

    private val activeBindings = AtomicReferenceArray<SeatBinding?>(4)
    private val lastPublishedSnapshots = AtomicReferenceArray<PlayerSnapshot?>(4)
    private val seatLocks = Array(4) { Any() }
    private val installedGenerations = LongArray(4) { 0L }

    /** 观战者活跃绑定（spectatorId -> 会话）；与四座绑定完全分离 */
    private val spectatorSessions = ConcurrentHashMap<String, UdpClientSession>()
    private val spectatorLock = Any()
    private val installedSpectatorGenerations = ConcurrentHashMap<String, Long>()

    /** 已发布的不可变 PUBLIC 完整基线缓存（供快照查询与 PUBLIC_RESEND 全量自愈读取） */
    private val lastPublishedPublicSnapshot = AtomicReference<ProtocolPublicSnapshot?>(null)

    /** 房主显式离房时对其自身连接的 KICK 豁免（离房者收成功回执，不收 ROOM_CLOSED） */
    private val leaveExclusion = AtomicReference<Int?>(null)
    private val leavingConnectionIds = ConcurrentHashMap.newKeySet<Int>()
    private val asyncExecutor: ExecutorService = Executors.newCachedThreadPool { r ->
        Thread(r, "UdpRoom-$roomId-Async").apply { isDaemon = true }
    }

    private val seatSubscriptions: List<AutoCloseable>
    private val roomUpdateSubscription: AutoCloseable
    private val publicUpdateSubscription: AutoCloseable
    private val spectatorDisplacedSubscription: AutoCloseable

    val closed: Boolean get() = isClosed.get() || sharedRoom.closed

    init {
        for (seat in 0..3) {
            updatePublishedSnapshot(seat, sharedRoom.tableHost.initialSnapshot(seat))
            installedGenerations[seat] = sharedRoom.bindingGenerations[seat]
        }
        updatePublishedPublicSnapshot(sharedRoom.tableHost.initialPublicSnapshot())
        seatSubscriptions = (0..3).map { seat ->
            sharedRoom.subscribe(seat) { update ->
                onSeatUpdate(seat, update)
            }
        }
        roomUpdateSubscription = sharedRoom.onRoomUpdate { info ->
            onSharedRoomUpdate(info)
        }
        publicUpdateSubscription = sharedRoom.subscribePublic { update ->
            onPublicUpdate(update)
        }
        // 权威观战顶替通知兜底：即使替换并非经本传输层发起（并发/跨绑定路径），
        // 也立即终结旧观战会话，绝不等待下一次游戏动作回调
        spectatorDisplacedSubscription = sharedRoom.onSpectatorDisplaced { spectatorId, _ ->
            val old = spectatorSessions[spectatorId] ?: return@onSpectatorDisplaced
            if (sharedRoom.spectators[spectatorId]?.generation == old.generation) return@onSpectatorDisplaced
            if (!old.markDisplaced()) return@onSpectatorDisplaced
            asyncExecutor.execute {
                try {
                    kickSender(old, ApiProtocol.DISPLACED_BY_NEW_CONNECTION)
                } catch (_: Throwable) {
                }
                try {
                    old.close()
                } catch (_: Throwable) {
                }
            }
        }
    }

    private fun updatePublishedSnapshot(seat: Int, snapshot: PlayerSnapshot) {
        val isolated = snapshot.deepCopy()
        while (true) {
            val current = lastPublishedSnapshots[seat]
            if (current != null) {
                if (current.syncEpoch == isolated.syncEpoch && current.cursor > isolated.cursor) {
                    return
                }
            }
            if (lastPublishedSnapshots.compareAndSet(seat, current, isolated)) {
                return
            }
        }
    }

    /**
     * 宿主座位流更新回调（运行于权威席位派发线程）：
     * 仅做无锁读取并派发给当前活跃客户端会话，绝不获取本类 monitor 锁。
     */
    private fun onSeatUpdate(seat: Int, update: PlayerUpdate) {
        val isolatedUpdate = update.deepCopy()
        updatePublishedSnapshot(seat, isolatedUpdate.snapshot)
        val binding = activeBindings[seat] ?: return
        val session = binding.session
        if (session.isClosed || session.isDisplaced) return
        if (session.generation != sharedRoom.bindingGenerations[seat]) return
        try {
            pushSender(session, isolatedUpdate)
        } catch (ignored: Throwable) {
        }
    }

    /**
     * 共享房间权威视图更新回调（运行于 SharedRoom 通知派发线程）：
     * 终结视图触发传输层关闭（其他成员可靠 ROOM_CLOSED），普通视图可靠下发给全部活跃绑定
     * （四座玩家与独立观战者；观战者同样支持 roomInfo/roomUpdate 生命周期契约）。
     */
    private fun onSharedRoomUpdate(info: ProtocolRoomInfo) {
        if (isClosed.get()) return
        if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
            closeTransport(excludeConnectionId = leaveExclusion.get())
            return
        }
        for (seat in 0..3) {
            val binding = activeBindings[seat] ?: continue
            val session = binding.session
            if (session.isClosed || session.isDisplaced) continue
            if (session.generation != sharedRoom.bindingGenerations[seat]) continue
            try {
                roomUpdateSender(session, info)
            } catch (ignored: Throwable) {
            }
        }
        for (session in spectatorSessions.values) {
            if (session.isClosed || session.isDisplaced) continue
            val specId = session.spectatorId ?: continue
            if (sharedRoom.spectators[specId]?.generation != session.generation) continue
            try {
                roomUpdateSender(session, info)
            } catch (ignored: Throwable) {
            }
        }
    }

    /**
     * 宿主独立公开流更新回调（运行于 publicDispatcher 派发线程）：
     * 仅做无锁读取并派发给当前活跃观战会话；先刷新不可变 PUBLIC 基线缓存供
     * 快照查询与 PUBLIC_RESEND 全量自愈读取，绝不进入权威队列。
     */
    private fun onPublicUpdate(update: ProtocolPublicUpdate) {
        updatePublishedPublicSnapshot(update.snapshot)
        for (session in spectatorSessions.values) {
            if (session.isClosed || session.isDisplaced) continue
            val specId = session.spectatorId ?: continue
            if (sharedRoom.spectators[specId]?.generation != session.generation) continue
            try {
                publicUpdateSender(session, update)
            } catch (ignored: Throwable) {
            }
        }
    }

    /** 刷新已发布 PUBLIC 完整基线缓存：仅接受游标单调前进的权威视图，绝不回退 */
    private fun updatePublishedPublicSnapshot(snapshot: ProtocolPublicSnapshot) {
        while (true) {
            val current = lastPublishedPublicSnapshot.get()
            if (current != null && snapshot.cursor <= current.cursor) {
                return
            }
            if (lastPublishedPublicSnapshot.compareAndSet(current, snapshot)) {
                return
            }
        }
    }

    /** 当前已发布的不可变 PUBLIC 完整基线（观战快照查询与 PUBLIC_RESEND 自愈读取） */
    fun getPublishedPublicSnapshot(): ProtocolPublicSnapshot? = lastPublishedPublicSnapshot.get()

    private fun buildSession(
        connectionId: Int,
        remoteAddress: SocketAddress,
        seat: Int,
        token: String,
        generation: Long
    ): UdpClientSession = UdpClientSession(
        connectionId = connectionId,
        remoteAddress = remoteAddress,
        roomId = roomId,
        seat = seat,
        token = token,
        generation = generation,
        clock = udpClock,
        snapshotSupplier = {
            val snap = lastPublishedSnapshots[seat]
                ?: error("No published snapshot cached for seat $seat")
            snap.deepCopy()
        }
    )

    private fun installBinding(seat: Int, session: UdpClientSession): SeatBinding {
        val binding = SeatBinding(session)
        activeBindings[seat] = binding
        // 新绑定立即获得权威房间基线视图（版本与对局游标分离的可靠房间流）
        try {
            roomUpdateSender(session, sharedRoom.getRoomInfoSnapshot())
        } catch (ignored: Throwable) {
        }
        return binding
    }

    private fun snapshotBinding(
        seat: Int,
        token: String,
        session: UdpClientSession
    ): CompletableFuture<Pair<UdpClientSession, RoomBinding>> =
        sharedRoom.snapshot(seat).thenApply { snap ->
            updatePublishedSnapshot(seat, snap)
            val binding = RoomBinding(
                roomId = roomId,
                seat = seat,
                resumeToken = token,
                snapshot = EngineProtocolMapper.toProtocolSnapshot(snap)
            )
            val initialUpdate = PlayerUpdate(
                snapshot = snap.deepCopy(),
                fromCursor = snap.cursor,
                visibleEvents = emptyList()
            )
            val protocolInitialUpdate = EngineProtocolMapper.toProtocolUpdate(initialUpdate)
            val payload = ApiMessageCodec.encodePush(protocolInitialUpdate, isFullSnapshot = true).toByteArray(Charsets.UTF_8)
            session.recordPush(snap.cursor, snap.cursor, payload)

            Pair(session, binding)
        }

    /**
     * 房主初次绑定 (房主座位)
     */
    fun bindHost(
        connectionId: Int,
        remoteAddress: SocketAddress,
        @Suppress("UNUSED_PARAMETER") playerName: String = hostPlayerName
    ): CompletableFuture<Pair<UdpClientSession, RoomBinding>> {
        if (closed) {
            val f = CompletableFuture<Pair<UdpClientSession, RoomBinding>>()
            f.completeExceptionally(IllegalStateException("ROOM_CLOSED"))
            return f
        }
        val seat = sharedRoom.hostSeat
        val token = sharedRoom.resumeTokens[seat]
        val generation = sharedRoom.bindingGenerations[seat]
        val session: UdpClientSession
        synchronized(seatLocks[seat]) {
            installedGenerations[seat] = maxOf(installedGenerations[seat], generation)
            session = buildSession(connectionId, remoteAddress, seat, token, generation)
            installBinding(seat, session)
        }
        return snapshotBinding(seat, token, session)
    }

    /**
     * 加入房间空闲席位（支持指定期望座位 preferSeat，被占用时自动顺延）
     */
    fun join(
        connectionId: Int,
        remoteAddress: SocketAddress,
        playerName: String,
        preferSeat: Int? = null
    ): CompletableFuture<Pair<UdpClientSession, RoomBinding>> {
        if (closed) {
            val f = CompletableFuture<Pair<UdpClientSession, RoomBinding>>()
            f.completeExceptionally(IllegalStateException("ROOM_CLOSED"))
            return f
        }
        return sharedRoom.join(playerName, preferSeat).thenCompose { pair ->
            val (targetSeat, token) = pair
            val generation = sharedRoom.bindingGenerations[targetSeat]
            val session: UdpClientSession
            synchronized(seatLocks[targetSeat]) {
                installedGenerations[targetSeat] = maxOf(installedGenerations[targetSeat], generation)
                session = buildSession(connectionId, remoteAddress, targetSeat, token, generation)
                installBinding(targetSeat, session)
            }
            snapshotBinding(targetSeat, token, session)
        }
    }

    /**
     * 凭 token 恢复已有席位连接（顶替旧连接）。
     */
    fun resume(
        newConnectionId: Int,
        newRemoteAddress: SocketAddress,
        token: String,
        cursor: Long? = null
    ): CompletableFuture<Pair<UdpClientSession, RoomBinding>> {
        if (closed) {
            val f = CompletableFuture<Pair<UdpClientSession, RoomBinding>>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
            return f
        }
        // 恢复必须使用服务端真实权威状态；客户端请求游标仅作参考，绝不作为输出基线
        return sharedRoom.resumeSession(token).exceptionally { err ->
            val realErr = ServerApiException.unwrap(err) ?: err
            if (realErr is ServerApiException && realErr.code == ApiErrors.INVALID_TOKEN) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, ApiErrors.INVALID_TOKEN, realErr)
            }
            if (realErr is ServerApiException) {
                throw realErr
            }
            if (realErr is IOException) {
                throw realErr
            }
            val msg = realErr.message?.trim().orEmpty()
            if (msg == "INVALID_RESUME_TOKEN" || msg == ApiErrors.INVALID_TOKEN || msg == "TOKEN_INVALID") {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, ApiErrors.INVALID_TOKEN, realErr)
            }
            throw realErr
        }.thenApply { res ->
            val seat = res.seat
            val targetGeneration = res.generation
            val snap = res.snapshot

            val oldSessionToKick: UdpClientSession?
            val session: UdpClientSession

            synchronized(seatLocks[seat]) {
                if (targetGeneration < installedGenerations[seat]) {
                    // 本次恢复已经被更新的代次覆盖（例如并发恢复乱序到达），不得装回旧绑定
                    throw ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, "DISPLACED_BY_NEW_CONNECTION")
                }
                installedGenerations[seat] = targetGeneration

                val oldBinding = activeBindings[seat]
                if (oldBinding != null && oldBinding.session.connectionId != newConnectionId) {
                    oldBinding.session.markDisplaced()
                    oldSessionToKick = oldBinding.session
                } else {
                    oldSessionToKick = null
                }

                session = buildSession(newConnectionId, newRemoteAddress, seat, token, targetGeneration)
                installBinding(seat, session)
            }

            // 在锁外异步执行旧连接的 KICK 与关闭，绝不阻塞席位锁或被缓慢 KICK 拖累
            if (oldSessionToKick != null) {
                asyncExecutor.execute {
                    try {
                        kickSender(oldSessionToKick, ApiProtocol.DISPLACED_BY_NEW_CONNECTION)
                    } catch (ignored: Throwable) {
                    }
                    try {
                        oldSessionToKick.close()
                    } catch (ignored: Throwable) {
                    }
                }
            }

            updatePublishedSnapshot(seat, snap)
            val binding = RoomBinding(
                roomId = roomId,
                seat = seat,
                resumeToken = token,
                snapshot = EngineProtocolMapper.toProtocolSnapshot(snap)
            )
            val initialUpdate = PlayerUpdate(
                snapshot = snap.deepCopy(),
                fromCursor = snap.cursor,
                visibleEvents = emptyList()
            )
            val protocolInitialUpdate = EngineProtocolMapper.toProtocolUpdate(initialUpdate)
            val payload = ApiMessageCodec.encodePush(protocolInitialUpdate, isFullSnapshot = true).toByteArray(Charsets.UTF_8)
            session.recordPush(snap.cursor, snap.cursor, payload)

            // 客户端声明的超前游标（cursor）不可信：恢复输出一律以服务端真实游标 snap.cursor 为准
            Pair(session, binding)
        }
    }

    /**
     * 校验连接是否仍为座位的当前有效绑定（执行期校验，传递至权威队列实际执行期）
     */
    private fun commandTarget(session: UdpClientSession): SeatBinding? {
        if (closed) return null
        val seat = session.seat
        if (seat !in 0..3) return null
        val binding = activeBindings[seat] ?: return null
        if (binding.session !== session) return null
        if (session.generation != sharedRoom.bindingGenerations[seat]) return null
        if (session.isDisplaced || session.isClosed) return null
        return binding
    }

    private fun sessionValidator(session: UdpClientSession): () -> Boolean =
        { commandTarget(session) != null }

    // ==========================================
    // 独立观战绑定（spectatorId 索引，与四座完全分离）
    // ==========================================

    /** 校验观战会话是否仍为该 spectatorId 的当前有效绑定（代次一致且未被顶替/关闭） */
    fun isSpectatorBindingCurrent(session: UdpClientSession): Boolean {
        if (closed) return false
        val specId = session.spectatorId ?: return false
        if (spectatorSessions[specId] !== session) return false
        if (session.isDisplaced || session.isClosed) return false
        return sharedRoom.spectators[specId]?.generation == session.generation
    }

    fun getSpectatorSession(spectatorId: String): UdpClientSession? = spectatorSessions[spectatorId]

    fun getSpectatorGeneration(spectatorId: String): Long =
        sharedRoom.spectators[spectatorId]?.generation ?: -1L

    private fun spectatorValidator(session: UdpClientSession): () -> Boolean =
        { isSpectatorBindingCurrent(session) }

    /** 按会话角色返回执行期校验器；绑定已失效返回 null */
    private fun bindingValidatorFor(session: UdpClientSession): (() -> Boolean)? =
        if (session.spectatorId != null) {
            if (isSpectatorBindingCurrent(session)) spectatorValidator(session) else null
        } else {
            if (commandTarget(session) != null) sessionValidator(session) else null
        }

    private fun buildSpectatorSession(
        connectionId: Int,
        remoteAddress: SocketAddress,
        spectatorId: String,
        token: String,
        generation: Long
    ): UdpClientSession = UdpClientSession(
        connectionId = connectionId,
        remoteAddress = remoteAddress,
        roomId = roomId,
        seat = UdpClientSession.SPECTATOR_SEAT,
        token = token,
        generation = generation,
        clock = udpClock,
        spectatorId = spectatorId
    )

    private fun installSpectatorBinding(spectatorId: String, session: UdpClientSession) {
        synchronized(spectatorLock) {
            if (closed) {
                throw IllegalStateException("ROOM_CLOSED")
            }
            val mem = sharedRoom.spectators[spectatorId]
            if (mem == null || mem.generation != session.generation) {
                throw IllegalStateException("DISPLACED_BY_NEW_CONNECTION")
            }
            val installed = installedSpectatorGenerations[spectatorId] ?: 0L
            if (session.generation < installed) {
                // 本次恢复已被更高有效代次覆盖（并发恢复乱序完成），不得装回旧绑定
                throw IllegalStateException("DISPLACED_BY_NEW_CONNECTION")
            }
            installedSpectatorGenerations[spectatorId] = session.generation
            spectatorSessions[spectatorId] = session
        }
    }

    private fun removeSpectatorBinding(spectatorId: String, session: UdpClientSession) {
        synchronized(spectatorLock) {
            if (spectatorSessions[spectatorId] === session) {
                spectatorSessions.remove(spectatorId)
            }
        }
    }

    /** 会话注册完成后下发初始可靠房间基线与最新公开流基线（观战者同样遵守 roomInfo 生命周期契约） */
    fun sendSpectatorBaseline(session: UdpClientSession) {
        if (closed || session.isClosed || session.isDisplaced) return
        val specId = session.spectatorId ?: return
        if (spectatorSessions[specId] !== session) return
        try {
            roomUpdateSender(session, sharedRoom.getRoomInfoSnapshot())
        } catch (ignored: Throwable) {
        }
        val latestPublic = getPublishedPublicSnapshot()
        if (latestPublic != null) {
            val fullUpdate = ProtocolPublicUpdate(
                snapshot = latestPublic,
                fromCursor = latestPublic.cursor,
                visibleEvents = emptyList(),
                isFullSnapshot = true
            )
            try {
                publicUpdateSender(session, fullUpdate)
            } catch (ignored: Throwable) {
            }
        }
    }

    /**
     * 加入观战：业务全部在 SharedRoom 权威队列内裁决，满四座房间正常可观战；
     * 传输层只建立独立 spectatorId 绑定，不影响四座控制/AI/准备/资源。
     */
    fun joinSpectator(
        connectionId: Int,
        remoteAddress: SocketAddress,
        spectatorName: String?
    ): CompletableFuture<Pair<UdpClientSession, SpectatorBinding>> {
        if (closed) {
            val f = CompletableFuture<Pair<UdpClientSession, SpectatorBinding>>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
            return f
        }
        return sharedRoom.joinSpectator(spectatorName).thenApply { res ->
            if (closed) {
                try {
                    sharedRoom.leaveSpectator(res.spectatorId)
                } catch (_: Throwable) {
                }
                throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
            }
            val session = buildSpectatorSession(connectionId, remoteAddress, res.spectatorId, res.resumeToken, res.generation)
            try {
                installSpectatorBinding(res.spectatorId, session)
            } catch (t: Throwable) {
                try {
                    sharedRoom.leaveSpectator(res.spectatorId)
                } catch (_: Throwable) {
                }
                throw t
            }
            updatePublishedPublicSnapshot(res.snapshot)
            val latestSnapshot = getPublishedPublicSnapshot() ?: res.snapshot
            session to SpectatorBinding(
                roomId = roomId,
                spectatorId = res.spectatorId,
                resumeToken = res.resumeToken,
                snapshot = latestSnapshot
            )
        }
    }

    /**
     * 凭 token 恢复观战会话（顶替旧连接）：权威队列内校验成员/令牌/完整 checkpoint，
     * 通过后代次递增；旧观战连接被主动 KICK 终结，绝不等待下一次游戏动作通知。
     * 旧连接稍后 close 不会撤销新绑定（安装代次高于旧代次）。
     */
    fun resumeSpectator(
        newConnectionId: Int,
        newRemoteAddress: SocketAddress,
        spectatorId: String,
        token: String,
        checkpoint: PublicCheckpoint? = null
    ): CompletableFuture<Pair<UdpClientSession, SpectatorBinding>> {
        if (closed) {
            val f = CompletableFuture<Pair<UdpClientSession, SpectatorBinding>>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
            return f
        }
        // 恢复必须使用服务端真实权威状态；客户端 checkpoint 仅作对账校验，绝不决定输出基线
        return sharedRoom.resumeSpectator(spectatorId, token, checkpoint).exceptionally { err ->
            val realErr = ServerApiException.unwrap(err) ?: err
            if (realErr is ServerApiException && realErr.code == ApiErrors.INVALID_TOKEN) {
                throw ServerApiException(ApiErrors.INVALID_TOKEN, ApiErrors.INVALID_TOKEN, realErr)
            }
            if (realErr is ServerApiException) {
                throw realErr
            }
            if (realErr is IOException) {
                throw realErr
            }
            val msg = realErr.message?.trim().orEmpty()
            when (msg) {
                "INVALID_RESUME_TOKEN", ApiErrors.INVALID_TOKEN, "TOKEN_INVALID" ->
                    throw ServerApiException(ApiErrors.INVALID_TOKEN, ApiErrors.INVALID_TOKEN, realErr)
                "INVALID_PUBLIC_CHECKPOINT" ->
                    throw ServerApiException(ApiErrors.INVALID_TOKEN, "INVALID_PUBLIC_CHECKPOINT", realErr)
                else -> throw realErr
            }
        }.thenApply { res ->
            val oldSession: UdpClientSession?
            val session: UdpClientSession
            synchronized(spectatorLock) {
                if (closed) {
                    throw ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
                }
                val mem = sharedRoom.spectators[spectatorId]
                if (mem == null || mem.generation != res.generation) {
                    throw ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, "DISPLACED_BY_NEW_CONNECTION")
                }
                val installed = installedSpectatorGenerations[spectatorId] ?: 0L
                if (res.generation < installed) {
                    // 并发恢复乱序完成：本次已被更高代次覆盖，不得安装
                    throw ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, "DISPLACED_BY_NEW_CONNECTION")
                }
                installedSpectatorGenerations[spectatorId] = res.generation
                oldSession = spectatorSessions[spectatorId]?.takeIf { it.connectionId != newConnectionId }
                session = buildSpectatorSession(newConnectionId, newRemoteAddress, spectatorId, token, res.generation)
                spectatorSessions[spectatorId] = session
            }

            // 主动终结被替换的旧观战连接（在锁外异步执行，绝不阻塞观战锁）
            if (oldSession != null && oldSession.markDisplaced()) {
                asyncExecutor.execute {
                    try {
                        kickSender(oldSession, ApiProtocol.DISPLACED_BY_NEW_CONNECTION)
                    } catch (ignored: Throwable) {
                    }
                    try {
                        oldSession.close()
                    } catch (ignored: Throwable) {
                    }
                }
            }

            updatePublishedPublicSnapshot(res.snapshot)
            val latestSnapshot = getPublishedPublicSnapshot() ?: res.snapshot
            session to SpectatorBinding(
                roomId = roomId,
                spectatorId = spectatorId,
                resumeToken = token,
                snapshot = latestSnapshot
            )
        }
    }

    /**
     * 观战显式离开：权威队列撤销成员资格与恢复令牌，传输层解绑；成功后返回大厅。
     */
    fun leaveSpectator(session: UdpClientSession): CompletableFuture<ProtocolActionReceipt> {
        val specId = session.spectatorId
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        if (!isSpectatorBindingCurrent(session)) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        }
        leavingConnectionIds.add(session.connectionId)
        return sharedRoom.leaveSpectator(specId, spectatorValidator(session)).whenComplete { receipt, err ->
            if (err == null && receipt != null && receipt.status == ProtocolReceiptStatus.APPLIED) {
                removeSpectatorBinding(specId, session)
            } else {
                leavingConnectionIds.remove(session.connectionId)
            }
        }
    }

    /** 观战公开全量快照查询 */
    fun publicSnapshotFor(session: UdpClientSession): CompletableFuture<ProtocolPublicSnapshot> {
        val validator = bindingValidatorFor(session)
            ?: run {
                val f = CompletableFuture<ProtocolPublicSnapshot>()
                f.completeExceptionally(ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION"))
                return f
            }
        return sharedRoom.publicSnapshot(validator)
    }

    /** 观战公开 checkpoint 对账恢复（权威队列内校验 sessionId/syncEpoch/cursor 后返回全量基线） */
    fun resumePublicFor(
        session: UdpClientSession,
        checkpoint: PublicCheckpoint
    ): CompletableFuture<ProtocolPublicSnapshot> {
        val validator = bindingValidatorFor(session)
            ?: run {
                val f = CompletableFuture<ProtocolPublicSnapshot>()
                f.completeExceptionally(ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION"))
                return f
            }
        return sharedRoom.resumePublic(checkpoint, validator)
    }

    /** 观战公开单局结算事件查询（PUBLIC 白名单） */
    fun queryPublicRoundResult(
        session: UdpClientSession,
        roundId: String
    ): CompletableFuture<List<com.majiang.transport.api.ProtocolGameEvent>> {
        val validator = bindingValidatorFor(session)
            ?: return CompletableFuture.completedFuture(emptyList())
        return sharedRoom.queryPublicRoundResultEvents(roundId, validator)
    }

    /**
     * 严格 PUBLIC_RESEND 处理：
     * 1. 先验证当前观战绑定角色与代次（非法/过时流请求不查询新流窗口）；
     * 2. checkpoint 的 sessionId/syncEpoch 必须与当前已发布公开基线完全一致；
     * 3. cursor 为客户端已应用基线：256 历史窗口内自 cursor+1 起按序补发，窗口外回全量自愈；
     * 校验失败返回 null（静默丢弃，绝不回退玩家快照）。
     */
    fun handlePublicResend(session: UdpClientSession, checkpoint: PublicCheckpoint): ResendResponse? {
        if (!isSpectatorBindingCurrent(session)) return null
        val published = lastPublishedPublicSnapshot.get() ?: return null
        if (checkpoint.sessionId != published.sessionId) return null
        if (checkpoint.syncEpoch != published.syncEpoch) return null
        if (checkpoint.cursor < 0L || checkpoint.cursor > published.cursor || checkpoint.cursor == Long.MAX_VALUE) {
            return null
        }
        val fullSnapshotSupplier: () -> ByteArray = {
            val fullUpdate = ProtocolPublicUpdate(
                snapshot = published,
                fromCursor = published.cursor,
                visibleEvents = emptyList(),
                isFullSnapshot = true
            )
            ApiMessageCodec.encodePublicUpdatePush(fullUpdate).toByteArray(Charsets.UTF_8)
        }
        return session.publicWindow.handleResend(
            connectionId = session.connectionId,
            requestedFromCursor = checkpoint.cursor + 1L,
            fullSnapshotSupplier = fullSnapshotSupplier
        )
    }

    /**
     * 显式离开房间（业务语义全部在 SharedRoom 权威队列内裁决）
     */
    fun leave(session: UdpClientSession): CompletableFuture<ProtocolActionReceipt> {
        val binding = commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val seat = session.seat
        leavingConnectionIds.add(session.connectionId)
        if (seat == sharedRoom.hostSeat) {
            leaveExclusion.set(session.connectionId)
        }
        return sharedRoom.leave(seat, sessionValidator(session)).whenComplete { receipt, err ->
            if (err == null && receipt != null && receipt.status == ProtocolReceiptStatus.APPLIED) {
                removeBinding(seat, session)
                if (seat == sharedRoom.hostSeat) {
                    // 房主离开：房间已由 SharedRoom 解散，传输层可靠通知其他成员并收尾
                    closeTransport(excludeConnectionId = session.connectionId)
                }
            } else {
                leavingConnectionIds.remove(session.connectionId)
                if (seat == sharedRoom.hostSeat) {
                    leaveExclusion.set(null)
                }
            }
        }
    }

    private fun removeBinding(seat: Int, session: UdpClientSession) {
        synchronized(seatLocks[seat]) {
            val current = activeBindings[seat] ?: return
            if (current.session === session) {
                activeBindings[seat] = null
            }
        }
    }

    /**
     * 房主发起比赛开始（联机要求所有在座真人准备就绪，由 SharedRoom 权威校验）
     */
    fun startMatch(session: UdpClientSession): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        if (session.seat != sharedRoom.hostSeat) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "PERMISSION_DENIED")
            )
        }
        return sharedRoom.startMatch(session.seat, sessionValidator(session))
    }

    /**
     * 设置准备就绪状态（大厅期真实业务，由 SharedRoom 权威校验与广播）
     */
    fun setReady(session: UdpClientSession, ready: Boolean): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        return sharedRoom.setReady(session.seat, ready, sessionValidator(session))
    }

    /**
     * 房主更新房间配置（仅开局前允许，成功后重置真人准备状态）
     */
    fun updateConfig(session: UdpClientSession, config: ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        return sharedRoom.updateConfig(session.seat, config, sessionValidator(session))
    }

    /**
     * 查询当前权威完整房间视图（版本、成员、配置与生命周期）；玩家与观战绑定均允许
     */
    fun getRoomInfo(session: UdpClientSession): CompletableFuture<ProtocolRoomInfo> {
        val validator = bindingValidatorFor(session)
            ?: run {
                val f = CompletableFuture<ProtocolRoomInfo>()
                f.completeExceptionally(ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION"))
                return f
            }
        return sharedRoom.getRoomInfo(validator)
    }

    fun submitChoice(session: UdpClientSession, choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(choice.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val engineChoice = EngineProtocolMapper.toEngineActionChoice(choice)
        return sharedRoom.submitChoice(session.seat, engineChoice, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun submitShufflePlan(session: UdpClientSession, plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(plan.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val enginePlan = EngineProtocolMapper.toEngineShufflePlan(plan)
        return sharedRoom.submitShufflePlan(session.seat, enginePlan, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun submitTileMark(session: UdpClientSession, request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val engineRequest = EngineProtocolMapper.toEngineTileMarkRequest(request)
        return sharedRoom.submitTileMark(session.seat, engineRequest, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun submitSleeveBloom(session: UdpClientSession, request: ProtocolSleeveBloomRequest): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val engineRequest = EngineProtocolMapper.toEngineSleeveBloomRequest(request)
        return sharedRoom.submitSleeveBloom(session.seat, engineRequest, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun submitSleeveBloomChoice(session: UdpClientSession, request: ProtocolSleeveBloomChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val engineRequest = EngineProtocolMapper.toEngineSleeveBloomChoiceRequest(request)
        return sharedRoom.submitSleeveBloomChoice(session.seat, engineRequest, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun submitBeamSwap(session: UdpClientSession, request: ProtocolBeamSwapRequest): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val engineRequest = EngineProtocolMapper.toEngineBeamSwapRequest(request)
        return sharedRoom.submitBeamSwap(session.seat, engineRequest, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun submitBeamSwapChoice(session: UdpClientSession, request: ProtocolBeamSwapChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        val engineRequest = EngineProtocolMapper.toEngineBeamSwapChoiceRequest(request)
        return sharedRoom.submitBeamSwapChoice(session.seat, engineRequest, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun nextRound(session: UdpClientSession): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("nextRound", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        return sharedRoom.nextRound(session.seat, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun querySnapshot(session: UdpClientSession): CompletableFuture<ProtocolPlayerSnapshot> {
        commandTarget(session)
            ?: run {
                val f = CompletableFuture<ProtocolPlayerSnapshot>()
                f.completeExceptionally(ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION"))
                return f
            }
        return sharedRoom.snapshot(session.seat, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolSnapshot(it) }
    }

    fun queryDecision(session: UdpClientSession): CompletableFuture<ProtocolPlayerDecision> {
        commandTarget(session)
            ?: run {
                val f = CompletableFuture<ProtocolPlayerDecision>()
                f.completeExceptionally(ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION"))
                return f
            }
        return sharedRoom.decision(session.seat, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolDecision(it) }
    }

    fun resumeCursor(session: UdpClientSession, cursor: Long): CompletableFuture<ProtocolPlayerSnapshot> {
        commandTarget(session)
            ?: run {
                val f = CompletableFuture<ProtocolPlayerSnapshot>()
                f.completeExceptionally(ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION"))
                return f
            }
        return sharedRoom.resume(session.seat, cursor, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolSnapshot(it) }
    }

    fun claimControl(session: UdpClientSession): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("claimControl", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        return sharedRoom.claimControl(session.seat, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun releaseControl(session: UdpClientSession): CompletableFuture<ProtocolActionReceipt> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("releaseControl", ProtocolReceiptStatus.REJECTED, reason = "STALE_SESSION")
            )
        return sharedRoom.releaseControl(session.seat, sessionValidator(session))
            .thenApply { EngineProtocolMapper.toProtocolReceipt(it) }
    }

    fun queryRoundResult(session: UdpClientSession, roundId: String): CompletableFuture<List<com.majiang.transport.api.ProtocolGameEvent>> {
        commandTarget(session)
            ?: return CompletableFuture.completedFuture(emptyList())
        return sharedRoom.queryRoundResultEvents(session.seat, roundId, sessionValidator(session))
            .thenApply { events ->
                EngineProtocolMapper.toProtocolEvents(events)
            }
    }

    /**
     * 连接异常关闭或超时断开回调（在异步清理线程中执行）：
     * 立即移除传输绑定；玩家掉线席位调度交由 SharedRoom 权威队列按共享代次裁决，
     * 观战意外掉线仅解绑连接、保留恢复资格（resumeToken 不撤销），绝不触发玩家 AI 接管。
     */
    fun onConnectionClosed(session: UdpClientSession) {
        if (sharedRoom.closed) return
        val specId = session.spectatorId
        if (specId != null) {
            val binding = spectatorSessions[specId] ?: return
            if (binding !== session) return
            removeSpectatorBinding(specId, session)
            try {
                sharedRoom.disconnectSpectator(specId, session.generation)
            } catch (ignored: Throwable) {
            }
            return
        }
        val seat = session.seat
        if (seat !in 0..3) return
        val binding = activeBindings[seat] ?: return
        if (binding.session !== session) return
        removeBinding(seat, session)
        try {
            sharedRoom.disconnectSeat(seat, session.generation)
        } catch (ignored: Throwable) {
        }
    }

    fun getConnectionGeneration(seat: Int): Long {
        if (seat !in 0..3) return -1L
        return sharedRoom.bindingGenerations[seat]
    }

    fun getActiveBinding(seat: Int): SeatBinding? {
        if (seat !in 0..3) return null
        return activeBindings[seat]
    }

    fun tick(): CompletableFuture<Boolean> {
        if (closed) return CompletableFuture.completedFuture(false)
        return sharedRoom.tick()
    }

    fun toSummary() = sharedRoom.toSummary()

    /**
     * 传输层收尾：释放订阅、向其他成员（玩家与观战者）可靠下发 ROOM_CLOSED 并关闭全部会话。
     * 不在此处关闭 SharedRoom（房主离房/停服的权威收尾由 SharedRoom 自身延迟关闭，
     * 避免取消正在返回的成功回执）。
     */
    private fun closeTransport(excludeConnectionId: Int? = null) {
        if (!isClosed.compareAndSet(false, true)) return
        for (sub in seatSubscriptions) {
            try {
                sub.close()
            } catch (ignored: Throwable) {
            }
        }
        try {
            roomUpdateSubscription.close()
        } catch (ignored: Throwable) {
        }
        try {
            publicUpdateSubscription.close()
        } catch (ignored: Throwable) {
        }
        try {
            spectatorDisplacedSubscription.close()
        } catch (ignored: Throwable) {
        }
        val stale = ArrayList<SeatBinding>()
        for (s in 0..3) {
            synchronized(seatLocks[s]) {
                activeBindings[s]?.let { stale.add(it) }
                activeBindings[s] = null
            }
        }
        val staleSpectators = ArrayList<UdpClientSession>()
        synchronized(spectatorLock) {
            for ((_, session) in spectatorSessions) {
                staleSpectators.add(session)
            }
            spectatorSessions.clear()
        }
        val excludedId = excludeConnectionId ?: leaveExclusion.get()
        for (binding in stale) {
            val connId = binding.session.connectionId
            val isExcluded = (excludedId != null && connId == excludedId) || leavingConnectionIds.contains(connId)
            if (!isExcluded) {
                try {
                    kickSender(binding.session, ApiProtocol.ROOM_CLOSED)
                } catch (ignored: Throwable) {
                }
            }
            if (!leavingConnectionIds.contains(connId)) {
                try {
                    binding.session.close()
                } catch (ignored: Throwable) {
                }
            }
        }
        for (session in staleSpectators) {
            val connId = session.connectionId
            val isExcluded = (excludedId != null && connId == excludedId) || leavingConnectionIds.contains(connId)
            if (!isExcluded) {
                try {
                    kickSender(session, ApiProtocol.ROOM_CLOSED)
                } catch (ignored: Throwable) {
                }
            }
            if (!leavingConnectionIds.contains(connId)) {
                try {
                    session.close()
                } catch (ignored: Throwable) {
                }
            }
        }
        try {
            asyncExecutor.shutdown()
        } catch (ignored: Throwable) {
        }
    }

    /**
     * 全量关闭：传输收尾 + 共享房间权威资源释放（幂等）
     */
    fun close(excludeConnectionId: Int? = null) {
        closeTransport(excludeConnectionId)
        try {
            sharedRoom.close()
        } catch (ignored: Throwable) {
        }
    }

    override fun close() {
        close(null)
    }

    companion object {
        /**
         * 便捷构造：独立 SharedRoom + UDP 传输适配（模块测试与装配使用；
         * 正式服务统一经 RoomRegistry 创建以共享注册表）。
         */
        fun standalone(
            roomId: String,
            hostPlayerName: String,
            udpClock: UdpClock = UdpClock.SYSTEM,
            clock: com.majiang.timing.MonotonicClock = com.majiang.timing.SystemMonotonicClock,
            pushSender: (session: UdpClientSession, update: PlayerUpdate) -> Unit = { _, _ -> },
            kickSender: (session: UdpClientSession, reason: String) -> Unit = { _, _ -> },
            roomUpdateSender: (session: UdpClientSession, info: ProtocolRoomInfo) -> Unit = { _, _ -> },
            publicUpdateSender: (session: UdpClientSession, update: ProtocolPublicUpdate) -> Unit = { _, _ -> }
        ): UdpRoom {
            val shared = SharedRoom(
                roomId = roomId,
                hostPlayerName = hostPlayerName,
                initialConfig = ProtocolRoomConfig(),
                isSingleplayer = false,
                clock = clock
            )
            return UdpRoom(
                sharedRoom = shared,
                udpClock = udpClock,
                pushSender = pushSender,
                kickSender = kickSender,
                roomUpdateSender = roomUpdateSender,
                publicUpdateSender = publicUpdateSender
            )
        }

        /** 将共享房间错误消息映射为稳定传输错误码 */
        fun mapError(t: Throwable?): String {
            if (t == null) return ApiErrors.INTERNAL_ERROR
            val root = ServerApiException.unwrap(t) ?: return ApiErrors.INTERNAL_ERROR
            if (root is IOException) {
                return ApiErrors.INTERNAL_ERROR
            }
            if (root is ServerApiException) {
                return root.code
            }
            if (root is UdpFrameException) {
                return root.reasonCode
            }
            val msg = root.message?.trim().orEmpty()
            return when (msg) {
                ApiErrors.INVALID_TOKEN, "INVALID_RESUME_TOKEN", "TOKEN_INVALID", "STALE_SESSION", "INVALID_PUBLIC_CHECKPOINT", "DISPLACED_BY_NEW_CONNECTION" -> ApiErrors.INVALID_TOKEN
                ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED" -> ApiErrors.ROOM_NOT_FOUND
                ApiErrors.ROOM_FULL -> ApiErrors.ROOM_FULL
                ApiErrors.SERVER_FULL -> ApiErrors.SERVER_FULL
                ApiErrors.CONFLICT -> ApiErrors.CONFLICT
                "SESSION_TIMEOUT" -> ApiErrors.INTERNAL_ERROR
                ApiErrors.UNAUTHENTICATED_SESSION, "UNAUTHENTICATED" -> ApiErrors.UNAUTHENTICATED_SESSION
                ApiErrors.MALFORMED_REQUEST, "MALFORMED" -> ApiErrors.MALFORMED_REQUEST
                ApiErrors.SERVER_BUSY, "BUSY" -> ApiErrors.SERVER_BUSY
                else -> ApiErrors.INTERNAL_ERROR
            }
        }
    }
}

package com.majiang.server.inprocess

import com.majiang.access.PlayerUpdate
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SpectatorApi
import com.majiang.server.api.SpectatorBinding
import com.majiang.server.host.ServerTableHost
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolScoreCodes
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
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
import com.majiang.transport.api.ProtocolSleeveBloomOffTurnRequest
import com.majiang.transport.api.ProtocolSleeveBloomOffTurnChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.server.idempotency.AdmissionResult
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.idempotency.InFlightReceipt
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpScope
import java.io.IOException
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 进程内本地单机服务端连接绑定 (InProcessBinding)
 *
 * 遵循 V4 架构契约与 TASK-ANTI-R4B1ROOMFIX2 规范：
 * 1. 业务逻辑统一委托给传输中立的 [SharedRoom]，与网络端共享相同成员、规则、就绪与发牌行为；
 * 2. 纯非阻塞异步契约，所有交互方法均提交至宿主单桌串行执行器并返回 [CompletableFuture]；
 * 3. 离开房间 (leaveRoom) 释放当前房间席位并返回 APPLIED，连接自身保持活跃 (closed = false)，
 *    支持在同一直连通道上继续调用 [createRoom] 或 [joinRoom]；
 * 4. 离开解绑严格条件化：仅在操作实际生效 (APPLIED) 且代次匹配时才解绑，拒绝在过时或被拒绝的回执上解除当前房间；
 * 5. 未绑定连接安全加入：[joinRoom] 使用前置代次校验器，在加入成功前不强制要求 currentRoom 已绑定；
 * 6. 会话恢复 (resumeSession) 严格校验目标房间存在性、有效性与 token 合法性，认证失败时不破坏现有连接绑定；
 * 7. 捕获共享席位全局代次 [capturedSeatGeneration]：在执行期与通知派发期严格校验，当新连接抢占/恢复席位时代次更新，
 *    被顶号的旧连接立即被拒绝写操作与接收通知，且旧连接退出绝不误断开新连接；
 * 8. 观察者通知派发在单一有序路径上驱动，严禁使用独立执行器抢跑初始通知，换房或离房后绝不派发旧房间残留事件；
 * 9. 回调双重代次门 + 原子关闭认领 (TASK-GLM-R4B1ROOMFIX5)：所有房间/席位回调先校验捕获的连接本地
 *    [bindingGeneration] 与房间引用，来自旧房间或旧绑定的迟到回调直接丢弃；「校验捕获身份 + 非显式离房 +
 *    认领关闭状态」在单一 [stateLock] 临界区内原子完成，外部通知与资源释放一律在锁外，杜绝校验与关闭
 *    之间的 check/use 窗口。绑定切换 (bindRoom/解绑) 与关闭认领共用同一同步协议。显式离房返回包含全部
 *    收尾（解绑、标志复位、终局补裁）的 stage，在途离房可被并发重复调用复用，旧完成绝不清除新离房标志。
 */
class InProcessBinding(
    val server: InProcessServer? = null,
    initialRoom: SharedRoom? = null,
    viewerSeat: Int = 0
) : ServerConnection {

    @Volatile
    var currentRoom: SharedRoom? = initialRoom
        private set

    @Volatile
    var viewerSeat: Int = viewerSeat
        private set

    enum class InProcessRole {
        UNBOUND,
        PLAYER,
        SPECTATOR
    }

    @Volatile
    var role: InProcessRole = if (initialRoom != null) InProcessRole.PLAYER else InProcessRole.UNBOUND
        private set

    @Volatile
    var spectatorId: String? = null
        private set

    @Volatile
    var capturedSpectatorGeneration: Long = 0L
        private set

    // 连接本地绑定代次：每次 bindRoom/close/leave 解绑单调递增，用于丢弃旧房间或旧绑定的迟到回调。
    // 与 [currentRoom] / [isExplicitlyLeaving] 的读写统一在 [stateLock] 内完成，保证回调门读到一致快照。
    @Volatile
    private var bindingGeneration: Long = 1L

    @Volatile
    private var spectatorRequestSeq: Long = 0L

    private val stateLock = Any()

    @Volatile
    var capturedSeatGeneration: Long = 0L
        private set

    /** 本地连接绑定代次 (Local Binding Generation, D-03) */
    val localBindingGeneration: Long get() = bindingGeneration

    /** 捕获的 SharedRoom 席位成员代次 (D-03) */
    val capturedSeatMemberGeneration: Long get() = capturedSeatGeneration

    /** 捕获的 SharedRoom 观战成员代次 (D-03) */
    val capturedSpectatorMemberGeneration: Long get() = capturedSpectatorGeneration

    val host: ServerTableHost? get() = currentRoom?.tableHost

    private val isClosed = AtomicBoolean(false)

    @Volatile
    private var _disconnectReason: String? = null

    private val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()
    private val updateListeners = CopyOnWriteArrayList<(ProtocolPlayerUpdate) -> Unit>()
    private val publicUpdateListeners = CopyOnWriteArrayList<(ProtocolPublicUpdate) -> Unit>()

    /** 回调门上下文：bindRoom 在锁内捕获的绑定身份（本地代次、房间、角色、座位/观战标识与代次）。 */
    internal class BindingCallbackContext(
        internal val generation: Long,
        internal val room: SharedRoom,
        internal val role: InProcessRole,
        internal val seat: Int = -1,
        internal val seatGeneration: Long = 0L,
        internal val spectatorId: String? = null,
        internal val spectatorGeneration: Long = 0L
    )

    private class BindingRoomObserver(
        val listener: (ProtocolRoomInfo) -> Unit
    ) {
        @Volatile var activeHandle: AutoCloseable? = null
        @Volatile var isDetached: Boolean = false

        fun attach(binding: InProcessBinding, ctx: BindingCallbackContext) {
            detach()
            isDetached = false
            activeHandle = ctx.room.onRoomUpdate { info ->
                if (isDetached) return@onRoomUpdate
                binding.dispatchGatedCallback(ctx) { listener(info) }
            }
        }

        fun detach() {
            isDetached = true
            try {
                activeHandle?.close()
            } catch (_: Throwable) {
            }
            activeHandle = null
        }
    }

    private val roomObservers = CopyOnWriteArrayList<BindingRoomObserver>()
    private var currentSeatSub: AutoCloseable? = null
    private var currentPublicSub: AutoCloseable? = null
    private var currentSpectatorDisplacedSub: AutoCloseable? = null
    private var currentSeatDisplacedSub: AutoCloseable? = null
    private var internalRoomSub: AutoCloseable? = null
    @Volatile
    private var isExplicitlyLeaving: Boolean = false
    @Volatile
    private var inFlightResumes: Int = 0
    @Volatile
    private var currentBindingContext: BindingCallbackContext? = null

    // 在途离房占位 Future（stateLock 保护）：并发重复 leave 复用一个在途 Future，
    // 其完成时刻保证解绑、标志复位与终局补裁已全部结束；旧完成不得清除新离房的标志。
    private var inFlightLeave: CompletableFuture<ProtocolActionReceipt>? = null

    override val closed: Boolean get() = isClosed.get()
    override val disconnectReason: String? get() = _disconnectReason

    init {
        initialRoom?.let { bindRoom(it) }
    }

    internal fun makeSessionValidator(expectedRoom: SharedRoom? = currentRoom): () -> Boolean = makeValidator(expectedRoom)

    private fun makeValidator(expectedRoom: SharedRoom? = currentRoom): () -> Boolean {
        val localGen = bindingGeneration
        val expectedSeatGen = capturedSeatGeneration
        val seat = viewerSeat
        val expectedSpecGen = capturedSpectatorGeneration
        val specId = spectatorId
        val currentRole = role
        return {
            val r = currentRoom
            !closed &&
            bindingGeneration == localGen &&
            (expectedRoom == null || r === expectedRoom) &&
            when (currentRole) {
                InProcessRole.PLAYER -> (r == null || seat !in 0..3 || r.seatMemberGeneration(seat) == expectedSeatGen)
                InProcessRole.SPECTATOR -> (r == null || specId == null || r.spectatorMemberGeneration(specId) == expectedSpecGen)
                InProcessRole.UNBOUND -> true
            }
        }
    }

    private fun bindRoom(room: SharedRoom) {
        val seat = viewerSeat
        val ctx: BindingCallbackContext
        // 先原子切换绑定状态（关闭连接拒绝绑定；本地代次立即递增作废所有旧回调），再释放旧订阅
        synchronized(stateLock) {
            check(!isClosed.get()) { "InProcessBinding is closed" }
            ctx = BindingCallbackContext(
                generation = ++bindingGeneration,
                room = room,
                role = InProcessRole.PLAYER,
                seat = seat,
                seatGeneration = if (seat in 0..3) room.seatMemberGeneration(seat) else 0L
            )
            currentBindingContext = ctx
            currentRoom = room
            role = InProcessRole.PLAYER
            spectatorId = null
            capturedSeatGeneration = ctx.seatGeneration
        }
        detachRoomSubscriptions()

        // 内部权威终端房间订阅：独立于 UI observer，确保房间关闭或顶号必达传输绑定
        internalRoomSub = room.onRoomUpdate { info ->
            if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
                handleRoomClosed(ctx)
            } else {
                dispatchGatedCallback(ctx) { }
            }
        }

        currentSeatDisplacedSub = room.onSeatDisplaced { displacedSeat, newGeneration ->
            if (ctx.seat == displacedSeat && newGeneration > ctx.seatGeneration) {
                if (synchronized(stateLock) { inFlightResumes > 0 }) return@onSeatDisplaced
                handleDisplacedByNewConnection(ctx)
            }
        }

        for (observer in roomObservers) {
            observer.attach(this, ctx)
        }

        currentSeatSub = room.subscribe(seat) { update ->
            dispatchGatedCallback(ctx) { deliverSeatUpdate(update) }
        }

        if (seat in 0..3) {
            room.markSeatConnected(seat, ctx.seatGeneration, makeValidator(room))
        }
    }

    private fun tryClaimSpectatorBinding(
        targetRoom: SharedRoom,
        specId: String,
        specGen: Long,
        reqSeq: Long,
        reqBindingGen: Long
    ): BindingCallbackContext {
        synchronized(stateLock) {
            check(!isClosed.get()) { "InProcessBinding is closed" }
            if (bindingGeneration != reqBindingGen) {
                throw ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, "DISPLACED_BY_NEW_CONNECTION")
            }
            if (spectatorRequestSeq != reqSeq) {
                throw ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, "DISPLACED_BY_NEW_CONNECTION")
            }
            if (role == InProcessRole.PLAYER) {
                throw ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave current room first")
            }
            val active = currentRoom
            if (active != null && !active.closed && active !== targetRoom) {
                throw ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave current room first")
            }
            val currentGen = targetRoom.spectatorMemberGeneration(specId)
            if (currentGen == null || currentGen != specGen) {
                throw ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, "DISPLACED_BY_NEW_CONNECTION")
            }

            val newBindingGen = ++bindingGeneration
            val ctx = BindingCallbackContext(
                generation = newBindingGen,
                room = targetRoom,
                role = InProcessRole.SPECTATOR,
                spectatorId = specId,
                spectatorGeneration = specGen
            )
            currentBindingContext = ctx
            currentRoom = targetRoom
            role = InProcessRole.SPECTATOR
            spectatorId = specId
            viewerSeat = -1
            capturedSpectatorGeneration = specGen
            return ctx
        }
    }

    private fun installSpectatorSubscriptions(ctx: BindingCallbackContext) {
        detachRoomSubscriptions()

        val room = ctx.room
        val specId = ctx.spectatorId

        if (closed || bindingGeneration != ctx.generation || currentRoom !== room) {
            return
        }

        internalRoomSub = room.onRoomUpdate { info ->
            if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
                handleRoomClosed(ctx)
            } else {
                dispatchGatedCallback(ctx) { }
            }
        }

        currentSpectatorDisplacedSub = room.onSpectatorDisplaced { displacedId, newGeneration ->
            if (ctx.spectatorId == displacedId && newGeneration > ctx.spectatorGeneration) {
                if (synchronized(stateLock) { inFlightResumes > 0 }) return@onSpectatorDisplaced
                handleDisplacedByNewConnection(ctx)
            }
        }

        for (observer in roomObservers) {
            observer.attach(this, ctx)
        }

        currentPublicSub = room.subscribePublic { update ->
            dispatchGatedCallback(ctx) { deliverPublicUpdate(update) }
        }

        if (specId != null) {
            room.markSpectatorConnected(specId, ctx.spectatorGeneration, makeValidator(room))
        }

        // 订阅安装后门控检查：避免在安装期间发生顶替或关闭通知丢失
        val currentGen = if (specId != null) room.spectatorMemberGeneration(specId) else null
        if (closed || bindingGeneration != ctx.generation || currentRoom !== room ||
            currentGen == null || currentGen > ctx.spectatorGeneration) {
            detachRoomSubscriptions()
            if (currentGen != null && currentGen > ctx.spectatorGeneration) {
                handleDisplacedByNewConnection(ctx)
            }
        }
    }

    private fun deliverSeatUpdate(update: PlayerUpdate) {
        val proto = EngineProtocolMapper.toProtocolUpdate(update)
        for (listener in updateListeners) {
            try {
                listener(proto)
            } catch (_: Throwable) {
            }
        }
    }

    private fun deliverPublicUpdate(update: ProtocolPublicUpdate) {
        for (listener in publicUpdateListeners) {
            try {
                listener(update)
            } catch (_: Throwable) {
            }
        }
    }

    private fun detachRoomSubscriptions() {
        currentSeatSub?.close()
        currentSeatSub = null
        currentPublicSub?.close()
        currentPublicSub = null
        currentSpectatorDisplacedSub?.close()
        currentSpectatorDisplacedSub = null
        currentSeatDisplacedSub?.close()
        currentSeatDisplacedSub = null
        internalRoomSub?.close()
        internalRoomSub = null
        for (observer in roomObservers) {
            observer.detach()
        }
    }

    /**
     * 回调门的最小处理入口（模块内 internal，生产回调与回归测试共用同一判定路径）：
     * 1. 显式离房窗口静默；2. 捕获的本地代次/房间引用校验，旧绑定回调直接丢弃；
     * 3. 共享席位代次漂移时经原子关闭认领判定被顶替；4. 其余情况锁外执行 [deliver]。
     */
    internal fun dispatchGatedCallback(ctx: BindingCallbackContext, deliver: () -> Unit) {
        if (synchronized(stateLock) { isExplicitlyLeaving }) return
        if (closed) return
        if (bindingGeneration != ctx.generation || currentRoom !== ctx.room) return
        if (ctx.role == InProcessRole.PLAYER && ctx.seat in 0..3 && ctx.room.seatMemberGeneration(ctx.seat) != ctx.seatGeneration) {
            handleDisplacedByNewConnection(ctx)
            return
        }
        if (ctx.role == InProcessRole.SPECTATOR && ctx.spectatorId != null) {
            val specGen = ctx.room.spectatorMemberGeneration(ctx.spectatorId)
            if (specGen == null || specGen > ctx.spectatorGeneration) {
                handleDisplacedByNewConnection(ctx)
                return
            }
        }
        deliver()
    }

    /** 捕获当前绑定身份的回调门上下文（模块内回归复现旧绑定迟到回调使用）。 */
    internal fun captureCurrentCallbackContext(): BindingCallbackContext? = synchronized(stateLock) {
        val room = currentRoom ?: return null
        BindingCallbackContext(
            generation = bindingGeneration,
            room = room,
            role = role,
            seat = viewerSeat,
            seatGeneration = capturedSeatGeneration,
            spectatorId = spectatorId,
            spectatorGeneration = capturedSpectatorGeneration
        )
    }

    /** 房间权威终结：校验（非离房窗口 + 捕获身份 + 房间确已关闭）与关闭认领原子完成。 */
    internal fun handleRoomClosed(ctx: BindingCallbackContext) {
        claimClosure(ApiProtocol.ROOM_CLOSED) {
            !isExplicitlyLeaving && bindingGeneration == ctx.generation && currentRoom === ctx.room &&
                ctx.room.closed
        }?.let { runCloseTeardown(it) }
    }

    /** 席位被新连接顶替：校验（非离房窗口 + 捕获身份 + 共享代次漂移）与关闭认领原子完成。 */
    internal fun handleDisplacedByNewConnection(ctx: BindingCallbackContext) {
        claimClosure(ApiProtocol.DISPLACED_BY_NEW_CONNECTION) {
            !isExplicitlyLeaving && inFlightResumes == 0 && bindingGeneration == ctx.generation && currentRoom === ctx.room &&
                ((ctx.role == InProcessRole.PLAYER && ctx.seat in 0..3 && ctx.room.seatMemberGeneration(ctx.seat) != ctx.seatGeneration) ||
                 (ctx.role == InProcessRole.SPECTATOR && ctx.spectatorId != null &&
                     (ctx.room.spectatorMemberGeneration(ctx.spectatorId)?.let { it > ctx.spectatorGeneration } ?: true)))
        }?.let { runCloseTeardown(it) }
    }

    /**
     * 在恢复请求计数归零或状态变更后，可靠复核当前绑定是否已被权威顶号 (D-03)。
     * 解决在途恢复期间被其他连接顶号导致通知丢失、连接未能及时关闭的并发缺陷。
     */
    private fun checkCurrentBindingDisplacement() {
        val ctx = synchronized(stateLock) {
            if (isClosed.get() || isExplicitlyLeaving || inFlightResumes > 0) return
            currentBindingContext
        } ?: return

        val room = ctx.room
        if (room.closed) {
            handleRoomClosed(ctx)
            return
        }

        when (ctx.role) {
            InProcessRole.PLAYER -> {
                if (ctx.seat in 0..3 && room.seatMemberGeneration(ctx.seat) != ctx.seatGeneration) {
                    handleDisplacedByNewConnection(ctx)
                }
            }
            InProcessRole.SPECTATOR -> {
                if (ctx.spectatorId != null) {
                    val specGen = room.spectatorMemberGeneration(ctx.spectatorId)
                    if (specGen == null || specGen > ctx.spectatorGeneration) {
                        handleDisplacedByNewConnection(ctx)
                    }
                }
            }
            InProcessRole.UNBOUND -> { /* 未绑定无需检测顶号 */ }
        }
    }

    /** 关闭认领快照：锁内捕获的旧绑定信息，供锁外收尾（席位断开调度）使用。 */
    private class CloseClaim(
        val room: SharedRoom?,
        val role: InProcessRole,
        val seat: Int,
        val seatGeneration: Long,
        val spectatorId: String?,
        val spectatorGeneration: Long
    )

    /**
     * 原子关闭认领：在单一 [stateLock] 临界区内完成校验并认领关闭
     * （置位原因与 closed、递增本地代次作废全部回调、清空 currentRoom），
     * 返回锁外收尾所需的旧绑定快照；已关闭或校验失败返回 null，调用方直接丢弃。
     * [validate] 仅做字段读取，严禁在其中调用用户回调或阻塞。
     */
    private fun claimClosure(reason: String, validate: (() -> Boolean)? = null): CloseClaim? {
        synchronized(stateLock) {
            if (isClosed.get()) return null
            if (validate != null && !validate()) return null
            if (_disconnectReason == null) {
                _disconnectReason = reason
            }
            isClosed.set(true)
            bindingGeneration++
            val room = currentRoom
            val curRole = role
            val seat = viewerSeat
            val seatGeneration = capturedSeatGeneration
            val specId = spectatorId
            val specGen = capturedSpectatorGeneration
            currentBindingContext = null
            currentRoom = null
            role = InProcessRole.UNBOUND
            spectatorId = null
            return CloseClaim(room, curRole, seat, seatGeneration, specId, specGen)
        }
    }

    /** 关闭收尾（认领成功的调用方在锁外执行至多一次）：通知断连、释放订阅、按认领快照调度断开。 */
    private fun runCloseTeardown(claim: CloseClaim) {
        val reason = _disconnectReason ?: ApiProtocol.LOCAL_CLOSED
        for (listener in disconnectListeners) {
            try {
                listener(reason)
            } catch (_: Throwable) {
            }
        }
        disconnectListeners.clear()
        updateListeners.clear()
        publicUpdateListeners.clear()
        detachRoomSubscriptions()
        roomObservers.clear()
        if (claim.room != null && !claim.room.closed) {
            try {
                if (claim.role == InProcessRole.PLAYER) {
                    claim.room.disconnectSeat(claim.seat, claim.seatGeneration)
                } else if (claim.role == InProcessRole.SPECTATOR && claim.spectatorId != null) {
                    claim.room.disconnectSpectator(claim.spectatorId, claim.spectatorGeneration)
                }
            } catch (_: Throwable) {
            }
        }
    }

    // ========================================
    // LobbyApi 实现
    // ========================================

    override fun listRooms(): CompletableFuture<List<RoomSummary>> {
        if (closed) {
            return CompletableFuture.completedFuture(emptyList())
        }
        val s = server
        if (s != null) {
            return CompletableFuture.completedFuture(s.listRooms())
        }
        val r = currentRoom
        if (r != null && !r.closed) {
            return CompletableFuture.completedFuture(listOf(r.toSummary()))
        }
        return CompletableFuture.completedFuture(emptyList())
    }

    override fun createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding> {
        if (closed) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.SPECTATOR) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave spectator room first"))
            return f
        }
        val active = currentRoom
        if (active != null && !active.closed) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave current room first"))
            return f
        }

        val s = server ?: InProcessServer()
        val roomConfig = req.config?.let { ProtocolRoomConfig(matchConfig = it) } ?: ProtocolRoomConfig()
        val singleplayerPolicy = s.isSingleplayer
        val newRoom = s.registry.createRoom(
            hostPlayerName = req.playerName ?: "Host",
            initialConfig = roomConfig,
            isSingleplayer = singleplayerPolicy
        )
        viewerSeat = 0
        try {
            bindRoom(newRoom)
        } catch (e: IllegalStateException) {
            // 连接在房间创建与绑定之间被并发关闭：回收孤儿房间，按关闭语义异常完成
            try {
                newRoom.close()
            } catch (_: Throwable) {
            }
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(e)
            return f
        }
        s.onRoomActivated(newRoom, this)
        return newRoom.snapshot(viewerSeat, makeValidator(newRoom)).thenApply { snap ->
            RoomBinding(
                roomId = newRoom.roomId,
                seat = viewerSeat,
                resumeToken = newRoom.seatResumeToken(viewerSeat),
                snapshot = EngineProtocolMapper.toProtocolSnapshot(snap)
            )
        }
    }

    override fun joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding> {
        if (closed) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.SPECTATOR) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave spectator room first"))
            return f
        }
        val active = currentRoom
        if (active != null && !active.closed) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave current room first"))
            return f
        }

        val s = server
        val targetRoom = s?.registry?.getRoom(req.roomId)
        if (targetRoom == null || targetRoom.closed) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_NOT_FOUND: ${req.roomId}"))
            return f
        }

        // 加入前只校验当前连接是否存活且代次未漂移，不要求当前已指向 targetRoom
        val joinGen = bindingGeneration
        val preJoinValidator = { !closed && bindingGeneration == joinGen }

        return targetRoom.join(req.playerName ?: "Player", req.preferSeat, preJoinValidator).thenCompose { pair ->
            val seat = pair.first
            val token = pair.second
            viewerSeat = seat
            try {
                bindRoom(targetRoom)
            } catch (e: IllegalStateException) {
                // 连接在加入完成与绑定之间被并发关闭：释放刚占用的席位并异常完成
                try {
                    targetRoom.leave(seat)
                } catch (_: Throwable) {
                }
                throw e
            }
            s?.onRoomActivated(targetRoom, this)
            targetRoom.snapshot(viewerSeat, makeValidator(targetRoom)).thenApply { snap ->
                RoomBinding(
                    roomId = targetRoom.roomId,
                    seat = viewerSeat,
                    resumeToken = token,
                    snapshot = EngineProtocolMapper.toProtocolSnapshot(snap)
                )
            }
        }
    }

    override fun resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding> {
        if (closed) {
            val f = CompletableFuture<RoomBinding>()
            val code = if (disconnectReason == ApiProtocol.ROOM_CLOSED) ApiErrors.ROOM_NOT_FOUND else ApiErrors.INTERNAL_ERROR
            f.completeExceptionally(ServerApiException(code, "InProcessBinding is closed: $disconnectReason"))
            return f
        }
        if (role == InProcessRole.SPECTATOR) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave spectator room first"))
            return f
        }
        val s = server
        val targetRoom = if (currentRoom?.roomId == req.roomId) currentRoom else s?.registry?.getRoom(req.roomId)
        if (targetRoom == null || targetRoom.closed) {
            val f = CompletableFuture<RoomBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_NOT_FOUND: ${req.roomId}"))
            return f
        }

        synchronized(stateLock) { inFlightResumes++ }
        val resumeGen = bindingGeneration
        val preResumeValidator = { !closed && bindingGeneration == resumeGen }

        return targetRoom.resumeSession(req.token, preResumeValidator).thenApply { res ->
            val seat = res.seat
            val snap = res.snapshot
            viewerSeat = seat
            // 并发关闭窗口内 bindRoom 抛 IllegalStateException 时本 stage 异常完成；
            // resumeToken 保持有效，席位可由后续连接再次恢复
            bindRoom(targetRoom)
            s?.onRoomActivated(targetRoom, this)
            RoomBinding(
                roomId = targetRoom.roomId,
                seat = viewerSeat,
                resumeToken = req.token,
                snapshot = EngineProtocolMapper.toProtocolSnapshot(snap)
            )
        }.whenComplete { _, _ ->
            synchronized(stateLock) { inFlightResumes = maxOf(0, inFlightResumes - 1) }
            checkCurrentBindingDisplacement()
        }
    }

    override fun leaveRoom(): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        val room = currentRoom
            ?: return CompletableFuture.completedFuture(
                ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
            )

        // 返回给调用方的占位 Future：其完成时刻保证离房收尾（解绑、标志复位、终局补裁）已全部结束
        val leaveFuture = CompletableFuture<ProtocolActionReceipt>()
        val leaveGen: Long
        val curRole: InProcessRole
        val leaveSeat: Int
        val leaveSeatGen: Long
        val leaveSpecId: String?
        val leaveSpecGen: Long
        synchronized(stateLock) {
            // 并发重复离房：复用在途 Future（其完成即含全部收尾），不重复入队，不互相清标志
            inFlightLeave?.let { return it }
            inFlightLeave = leaveFuture
            leaveGen = bindingGeneration
            curRole = role
            leaveSeat = viewerSeat
            leaveSeatGen = capturedSeatGeneration
            leaveSpecId = spectatorId
            leaveSpecGen = capturedSpectatorGeneration
            // 显式离房窗口覆盖整个 leave 生命周期（入队 -> 回执 -> 解绑 -> 收尾），
            // 期间所有回调静默，避免自身 leave 造成的席位代次变更被误判为被顶号或房间关闭
            isExplicitlyLeaving = true
        }

        // 执行期校验完全基于锁内快照：连接存活、本地代次未漂移、仍指向离房目标、共享代次未漂移
        val validator = {
            val r = currentRoom
            if (r == null || closed || bindingGeneration != leaveGen || r !== room) {
                false
            } else {
                when (curRole) {
                    InProcessRole.PLAYER -> leaveSeat !in 0..3 || r.seatMemberGeneration(leaveSeat) == leaveSeatGen
                    InProcessRole.SPECTATOR -> leaveSpecId == null || r.spectatorMemberGeneration(leaveSpecId) == leaveSpecGen
                    InProcessRole.UNBOUND -> true
                }
            }
        }

        val leaveOp = if (curRole == InProcessRole.SPECTATOR && leaveSpecId != null) {
            room.leaveSpectator(leaveSpecId, validator)
        } else {
            room.leave(leaveSeat, validator)
        }

        leaveOp.whenComplete { receipt, error ->
            var unbound = false
            if (receipt != null && receipt.status == ProtocolReceiptStatus.APPLIED) {
                synchronized(stateLock) {
                    // 仅在操作生效且绑定未被关闭认领/换绑取代时解绑（与关闭认领互斥）
                    if (bindingGeneration == leaveGen && currentRoom === room) {
                        currentBindingContext = null
                        currentRoom = null
                        role = InProcessRole.UNBOUND
                        spectatorId = null
                        bindingGeneration++
                        unbound = true
                    }
                }
            }
            if (unbound) {
                detachRoomSubscriptions()
                server?.onRoomDeactivated(room)
            }
            // 收尾：仅本次离房仍持有占位时复位标志并释放占位，旧完成绝不清除新离房
            synchronized(stateLock) {
                if (inFlightLeave === leaveFuture) {
                    inFlightLeave = null
                    isExplicitlyLeaving = false
                }
            }
            // 离房窗口内被抑制的终局在此确定性补裁（原子认领，仅在仍绑定原房时生效）
            val leaveCtx = BindingCallbackContext(
                generation = leaveGen,
                room = room,
                role = curRole,
                seat = leaveSeat,
                seatGeneration = leaveSeatGen,
                spectatorId = leaveSpecId,
                spectatorGeneration = leaveSpecGen
            )
            when {
                room.closed -> handleRoomClosed(leaveCtx)
                curRole == InProcessRole.PLAYER && leaveSeat in 0..3 && room.seatMemberGeneration(leaveSeat) != leaveSeatGen ->
                    handleDisplacedByNewConnection(leaveCtx)
                curRole == InProcessRole.SPECTATOR && leaveSpecId != null && room.spectatorMemberGeneration(leaveSpecId) != leaveSpecGen ->
                    handleDisplacedByNewConnection(leaveCtx)
            }
            if (error != null) {
                leaveFuture.completeExceptionally(error)
            } else {
                // CompletableFuture 契约：error 为空时 receipt 必非空
                leaveFuture.complete(receipt!!)
            }
        }
        return leaveFuture
    }

    override fun getRoomInfo(): CompletableFuture<ProtocolRoomInfo> {
        if (closed) {
            val f = CompletableFuture<ProtocolRoomInfo>()
            val code = if (disconnectReason == ApiProtocol.ROOM_CLOSED) ApiErrors.ROOM_NOT_FOUND else ApiErrors.INTERNAL_ERROR
            f.completeExceptionally(ServerApiException(code, "InProcessBinding is closed: $disconnectReason"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            if (room.closed) {
                val f = CompletableFuture<ProtocolRoomInfo>()
                f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED"))
                return f
            }
            return room.getRoomInfo(makeValidator(room))
        }
        val f = CompletableFuture<ProtocolRoomInfo>()
        f.completeExceptionally(ServerApiException(ApiErrors.UNAUTHENTICATED_SESSION, "Not in a room"))
        return f
    }

    override fun setReady(ready: Boolean): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            return room.setReady(viewerSeat, ready, makeValidator(room))
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt("setReady", ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun updateConfig(config: ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            return room.updateConfig(viewerSeat, config, makeValidator(room))
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun onRoomUpdate(listener: (ProtocolRoomInfo) -> Unit): AutoCloseable {
        if (closed) return AutoCloseable {}
        val observer = BindingRoomObserver(listener)
        roomObservers.add(observer)
        val ctx = captureCurrentCallbackContext()
        if (ctx != null && !ctx.room.closed) {
            observer.attach(this, ctx)
        }
        return AutoCloseable {
            observer.detach()
            roomObservers.remove(observer)
        }
    }

    // ========================================
    // SessionApi 实现
    // ========================================

    override fun capabilities(): CompletableFuture<ProtocolAccessCapabilities> {
        if (closed) {
            val f = CompletableFuture<ProtocolAccessCapabilities>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        return CompletableFuture.completedFuture(
            ProtocolAccessCapabilities(
                protocolVersion = ApiProtocol.PROTOCOL_VERSION,
                supportedPlayerCounts = setOf(4),
                remoteTransport = false,
                persistence = false,
                resumption = true,
                tileMarkSupported = true,
                sleeveBloomSupported = true,
                beamSwapSupported = true,
                scoreDetailVersion = ProtocolScoreCodes.CONTRACT_VERSION,
                seeThroughSupported = true
            )
        )
    }

    private fun <T> CompletableFuture<T>.mapInProcessErrors(): CompletableFuture<T> {
        val out = CompletableFuture<T>()
        this.whenComplete { res, err ->
            if (err != null) {
                val root = ServerApiException.unwrap(err) ?: err
                if (root is ServerApiException) {
                    out.completeExceptionally(root)
                } else if (root is IOException) {
                    out.completeExceptionally(root)
                } else {
                    val msg = root.message?.trim().orEmpty()
                    val mappedCode = when (msg) {
                        "STALE_SESSION", "INVALID_TOKEN", "INVALID_RESUME_TOKEN", "TOKEN_INVALID" -> ApiErrors.INVALID_TOKEN
                        "ROOM_NOT_FOUND", "ROOM_CLOSED" -> ApiErrors.ROOM_NOT_FOUND
                        "ROOM_FULL" -> ApiErrors.ROOM_FULL
                        "DISPLACED_BY_NEW_CONNECTION" -> ApiProtocol.DISPLACED_BY_NEW_CONNECTION
                        "ALREADY_IN_ROOM: Must leave current room first" -> ApiErrors.ALREADY_BOUND
                        "UNAUTHENTICATED", "UNAUTHENTICATED_SESSION" -> ApiErrors.UNAUTHENTICATED_SESSION
                        "MALFORMED", "MALFORMED_REQUEST" -> ApiErrors.MALFORMED_REQUEST
                        else -> ApiErrors.INTERNAL_ERROR
                    }
                    out.completeExceptionally(ServerApiException(mappedCode, msg.ifBlank { mappedCode }, root))
                }
            } else {
                out.complete(res)
            }
        }
        return out
    }

    override fun snapshot(): CompletableFuture<ProtocolPlayerSnapshot> {
        if (closed) {
            val f = CompletableFuture<ProtocolPlayerSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.SPECTATOR) {
            val f = CompletableFuture<ProtocolPlayerSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "SPECTATOR_READ_ONLY"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.snapshot(viewerSeat, makeValidator(room)).thenApply { snap ->
                EngineProtocolMapper.toProtocolSnapshot(snap)
            }.mapInProcessErrors()
        }
        val f = CompletableFuture<ProtocolPlayerSnapshot>()
        f.completeExceptionally(ServerApiException(ApiErrors.UNAUTHENTICATED_SESSION, "Not in a room"))
        return f
    }

    override fun decision(): CompletableFuture<ProtocolPlayerDecision> {
        if (closed) {
            val f = CompletableFuture<ProtocolPlayerDecision>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.SPECTATOR) {
            val f = CompletableFuture<ProtocolPlayerDecision>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "SPECTATOR_READ_ONLY"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.decision(viewerSeat, makeValidator(room)).thenApply { dec ->
                EngineProtocolMapper.toProtocolDecision(dec)
            }.mapInProcessErrors()
        }
        val f = CompletableFuture<ProtocolPlayerDecision>()
        f.completeExceptionally(ServerApiException(ApiErrors.UNAUTHENTICATED_SESSION, "Not in a room"))
        return f
    }

    override fun submit(choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(choice.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(choice.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineChoice = EngineProtocolMapper.toEngineActionChoice(choice)
            return room.submitChoice(viewerSeat, engineChoice, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(choice.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitShufflePlan(plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(plan.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(plan.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val enginePlan = EngineProtocolMapper.toEngineShufflePlan(plan)
            return room.submitShufflePlan(viewerSeat, enginePlan, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(plan.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineTileMarkRequest(request)
            return room.submitTileMark(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitSleeveBloom(request: ProtocolSleeveBloomRequest): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineSleeveBloomRequest(request)
            return room.submitSleeveBloom(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitSleeveBloomChoice(request: ProtocolSleeveBloomChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineSleeveBloomChoiceRequest(request)
            return room.submitSleeveBloomChoice(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitSleeveBloomOffTurn(request: ProtocolSleeveBloomOffTurnRequest): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineSleeveBloomOffTurnRequest(request)
            return room.submitSleeveBloomOffTurn(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitSleeveBloomOffTurnChoice(request: ProtocolSleeveBloomOffTurnChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineSleeveBloomOffTurnChoiceRequest(request)
            return room.submitSleeveBloomOffTurnChoice(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitBeamSwap(request: ProtocolBeamSwapRequest): CompletableFuture<ProtocolActionReceipt> {        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineBeamSwapRequest(request)
            return room.submitBeamSwap(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun submitBeamSwapChoice(request: ProtocolBeamSwapChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            val engineRequest = EngineProtocolMapper.toEngineBeamSwapChoiceRequest(request)
            return room.submitBeamSwapChoice(viewerSeat, engineRequest, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun start(): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            return room.startMatch(viewerSeat, makeValidator(room))
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt("start", ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun nextRound(): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("nextRound", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("nextRound", ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            return room.nextRound(viewerSeat, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt("nextRound", ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun resume(cursor: Long): CompletableFuture<ProtocolPlayerSnapshot> {
        if (closed) {
            val f = CompletableFuture<ProtocolPlayerSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.SPECTATOR) {
            val f = CompletableFuture<ProtocolPlayerSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "SPECTATOR_READ_ONLY"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.resume(viewerSeat, cursor, makeValidator(room)).thenApply { snap ->
                EngineProtocolMapper.toProtocolSnapshot(snap)
            }.mapInProcessErrors()
        }
        val f = CompletableFuture<ProtocolPlayerSnapshot>()
        f.completeExceptionally(ServerApiException(ApiErrors.UNAUTHENTICATED_SESSION, "Not in a room"))
        return f
    }

    override fun claimControl(): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("claimControl", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("claimControl", ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            return room.claimControl(viewerSeat, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt("claimControl", ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun releaseControl(): CompletableFuture<ProtocolActionReceipt> {
        if (closed) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("releaseControl", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            )
        }
        if (role == InProcessRole.SPECTATOR) {
            return CompletableFuture.completedFuture(
                ProtocolActionReceipt("releaseControl", ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")
            )
        }
        val room = currentRoom
        if (room != null) {
            return room.releaseControl(viewerSeat, makeValidator(room)).thenApply { receipt ->
                EngineProtocolMapper.toProtocolReceipt(receipt)
            }
        }
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt("releaseControl", ProtocolReceiptStatus.REJECTED, reason = "NOT_IN_ROOM")
        )
    }

    override fun queryRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
        if (closed) {
            return CompletableFuture.completedFuture(emptyList())
        }
        if (role == InProcessRole.SPECTATOR) {
            // 观战私有查询必须明确拒绝：私有结算事件按座位过滤，观战无权取得任何玩家视图
            val f = CompletableFuture<List<ProtocolGameEvent>>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "SPECTATOR_READ_ONLY"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.queryRoundResultEvents(viewerSeat, roundId, makeValidator(room)).thenApply { events ->
                EngineProtocolMapper.toProtocolEvents(events)
            }.mapInProcessErrors()
        }
        return CompletableFuture.completedFuture(emptyList())
    }

    // ========================================
    // SpectatorApi 实现
    // ========================================

    override fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding> {
        val reqSeq: Long
        val reqBindingGen: Long
        synchronized(stateLock) {
            if (isClosed.get()) {
                val f = CompletableFuture<SpectatorBinding>()
                f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
                return f
            }
            if (role == InProcessRole.PLAYER) {
                val f = CompletableFuture<SpectatorBinding>()
                f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Player cannot join as spectator without leaving"))
                return f
            }
            val active = currentRoom
            if (active != null && !active.closed) {
                val f = CompletableFuture<SpectatorBinding>()
                f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave current room first"))
                return f
            }
            reqSeq = ++spectatorRequestSeq
            reqBindingGen = bindingGeneration
        }

        val s = server
        val targetRoom = s?.registry?.getRoom(req.roomId)
        if (targetRoom == null || targetRoom.closed) {
            val f = CompletableFuture<SpectatorBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_NOT_FOUND: ${req.roomId}"))
            return f
        }

        val preJoinValidator = { !closed && bindingGeneration == reqBindingGen && spectatorRequestSeq == reqSeq }

        return targetRoom.joinSpectator(req.spectatorName, preJoinValidator).thenApply { res ->
            val ctx = try {
                tryClaimSpectatorBinding(targetRoom, res.spectatorId, res.generation, reqSeq, reqBindingGen)
            } catch (t: Throwable) {
                try {
                    targetRoom.leaveSpectator(res.spectatorId)
                } catch (_: Throwable) {
                }
                throw t
            }
            installSpectatorSubscriptions(ctx)
            s?.onRoomActivated(targetRoom, this)
            SpectatorBinding(
                roomId = targetRoom.roomId,
                spectatorId = res.spectatorId,
                resumeToken = res.resumeToken,
                snapshot = res.snapshot
            )
        }
    }

    override fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding> {
        val reqSeq: Long
        val reqBindingGen: Long
        synchronized(stateLock) {
            if (isClosed.get()) {
                val f = CompletableFuture<SpectatorBinding>()
                f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
                return f
            }
            if (role == InProcessRole.PLAYER) {
                val f = CompletableFuture<SpectatorBinding>()
                f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Player cannot resume as spectator without leaving"))
                return f
            }
            val active = currentRoom
            if (active != null && !active.closed && (active.roomId != req.roomId || role != InProcessRole.SPECTATOR)) {
                val f = CompletableFuture<SpectatorBinding>()
                f.completeExceptionally(ServerApiException(ApiErrors.ALREADY_BOUND, "ALREADY_IN_ROOM: Must leave current room first"))
                return f
            }
            reqSeq = ++spectatorRequestSeq
            reqBindingGen = bindingGeneration
        }

        val s = server
        val targetRoom = if (currentRoom?.roomId == req.roomId) currentRoom else s?.registry?.getRoom(req.roomId)
        if (targetRoom == null || targetRoom.closed) {
            val f = CompletableFuture<SpectatorBinding>()
            f.completeExceptionally(ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_NOT_FOUND: ${req.roomId}"))
            return f
        }

        synchronized(stateLock) { inFlightResumes++ }
        val preResumeValidator = { !closed && bindingGeneration == reqBindingGen && spectatorRequestSeq == reqSeq }

        return targetRoom.resumeSpectator(req.spectatorId, req.resumeToken, req.checkpoint, preResumeValidator).thenApply { res ->
            val ctx = tryClaimSpectatorBinding(targetRoom, res.spectatorId, res.generation, reqSeq, reqBindingGen)
            installSpectatorSubscriptions(ctx)
            s?.onRoomActivated(targetRoom, this)
            SpectatorBinding(
                roomId = targetRoom.roomId,
                spectatorId = res.spectatorId,
                resumeToken = req.resumeToken,
                snapshot = res.snapshot
            )
        }.whenComplete { _, _ ->
            synchronized(stateLock) { inFlightResumes = maxOf(0, inFlightResumes - 1) }
            checkCurrentBindingDisplacement()
        }
    }

    override fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot> {
        if (closed) {
            val f = CompletableFuture<ProtocolPublicSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.PLAYER) {
            val f = CompletableFuture<ProtocolPublicSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "ROLE_PLAYER: Player cannot call publicSnapshot"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.publicSnapshot(makeValidator(room)).mapInProcessErrors()
        }
        val f = CompletableFuture<ProtocolPublicSnapshot>()
        f.completeExceptionally(ServerApiException(ApiErrors.UNAUTHENTICATED_SESSION, "Not in a room"))
        return f
    }

    override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot> {
        if (closed) {
            val f = CompletableFuture<ProtocolPublicSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.INTERNAL_ERROR, "InProcessBinding is closed"))
            return f
        }
        if (role == InProcessRole.PLAYER) {
            val f = CompletableFuture<ProtocolPublicSnapshot>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "ROLE_PLAYER: Player cannot call resumePublic"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.resumePublic(checkpoint, makeValidator(room)).mapInProcessErrors()
        }
        val f = CompletableFuture<ProtocolPublicSnapshot>()
        f.completeExceptionally(ServerApiException(ApiErrors.UNAUTHENTICATED_SESSION, "Not in a room"))
        return f
    }

    override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
        if (closed) {
            return CompletableFuture.completedFuture(emptyList())
        }
        if (role == InProcessRole.PLAYER) {
            val f = CompletableFuture<List<ProtocolGameEvent>>()
            f.completeExceptionally(ServerApiException(ApiErrors.MALFORMED_REQUEST, "ROLE_PLAYER: Player cannot call queryPublicRoundResult"))
            return f
        }
        val room = currentRoom
        if (room != null) {
            return room.queryPublicRoundResultEvents(roundId, makeValidator(room)).mapInProcessErrors()
        }
        return CompletableFuture.completedFuture(emptyList())
    }

    override fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable {
        if (closed) {
            return AutoCloseable { }
        }
        publicUpdateListeners.add(listener)
        return AutoCloseable {
            publicUpdateListeners.remove(listener)
        }
    }

    // ========================================
    // PushChannel 实现
    // ========================================

    override fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable {
        if (closed) {
            return AutoCloseable { }
        }
        updateListeners.add(listener)
        return AutoCloseable {
            updateListeners.remove(listener)
        }
    }

    override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
        if (closed) {
            try {
                listener(disconnectReason ?: ApiProtocol.LOCAL_CLOSED)
            } catch (_: Throwable) {
            }
            return AutoCloseable { }
        }
        disconnectListeners.add(listener)
        return AutoCloseable {
            disconnectListeners.remove(listener)
        }
    }

    // ========================================
    // 共享回执与幂等执行 (R14 / F12)
    // ========================================

    val lobbyToken: String = UUID.randomUUID().toString()

    val logicalPrincipal: String
        get() {
            val room = currentRoom
            return when (role) {
                InProcessRole.PLAYER -> if (room != null) "player:${room.roomId}:$viewerSeat" else "lobby:$lobbyToken"
                InProcessRole.SPECTATOR -> if (room != null && spectatorId != null) "spectator:${room.roomId}:$spectatorId" else "lobby:$lobbyToken"
                InProcessRole.UNBOUND -> "lobby:$lobbyToken"
            }
        }

    fun getRequestScope(): CompletableFuture<TcpScope> {
        val s = server
        val f = CompletableFuture<TcpScope>()
        if (s == null) {
            f.complete(TcpScope(scope = "inprocess-scope", expiresAtMillis = Long.MAX_VALUE))
            return f
        }
        val now = System.currentTimeMillis()
        val principal = logicalPrincipal
        val (scope, _) = s.receiptStore.getOrCreateScopeForPrincipal(principal, now, recordQueryTime = false)
        f.complete(scope)
        return f
    }

    private fun isSideEffectApi(api: String): Boolean = when (api) {
        ApiTags.CREATE_ROOM,
        ApiTags.JOIN_ROOM,
        ApiTags.JOIN_SPECTATOR,
        ApiTags.RESUME_SESSION,
        ApiTags.RESUME_SPECTATOR,
        ApiTags.LEAVE_ROOM,
        ApiTags.SET_READY,
        ApiTags.UPDATE_CONFIG,
        ApiTags.START_MATCH,
        ApiTags.NEXT_ROUND,
        ApiTags.SUBMIT_CHOICE,
        ApiTags.SUBMIT_SHUFFLE_PLAN,
        ApiTags.SUBMIT_TILE_MARK,
        ApiTags.SUBMIT_SLEEVE_BLOOM,
        ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE,
        ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN,
        ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE,
        ApiTags.SUBMIT_BEAM_SWAP,
        ApiTags.SUBMIT_BEAM_SWAP_CHOICE,
        ApiTags.CLAIM_CONTROL,
        ApiTags.RELEASE_CONTROL -> true
        else -> false
    }

    fun executeRequest(envelope: TcpRequestEnvelope): CompletableFuture<TcpResponseEnvelope> {
        envelope.validateRequestIdConsistency()
        val s = server ?: throw IllegalStateException("InProcessBinding not associated with InProcessServer")
        val principal = logicalPrincipal
        val req = envelope.request
        val api = req.api

        if (api == "GET_REQUEST_SCOPE") {
            val now = System.currentTimeMillis()
            if (s.receiptStore.isScopeQueryRateLimited(principal, now)) {
                val resp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.SERVER_BUSY)
                return CompletableFuture.completedFuture(
                    TcpResponseEnvelope(envelope.requestScope, envelope.requestId, resp)
                )
            }
            val (scope, _) = s.receiptStore.getOrCreateScopeForPrincipal(principal, now)
            val resp = ApiResponse(requestId = envelope.requestId, success = true)
            return CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, resp, grantedScope = scope)
            )
        }

        val isSideEffect = isSideEffectApi(api)
        val isLobby = (role == InProcessRole.UNBOUND)
        val rawPayload = try {
            ApiMessageCodec.encodeRequest(req)
        } catch (_: Throwable) {
            val resp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
            return CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, resp)
            )
        }

        val key = IdempotencyKey(principal, envelope.requestScope, envelope.requestId)
        val admission = s.receiptStore.acquireAdmission(
            key = key,
            request = req,
            rawPayload = rawPayload,
            isSideEffect = isSideEffect,
            isLobby = isLobby,
            nowMillis = System.currentTimeMillis()
        )

        return when (admission) {
            is AdmissionResult.Replay -> CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, admission.completed.response, admission.completed.grantedScope)
            )
            is AdmissionResult.WaitInFlight -> admission.inFlight.future.thenApply { resp ->
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, resp, admission.inFlight.grantedScope.get())
            }
            is AdmissionResult.Conflict -> CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.CONFLICT))
            )
            is AdmissionResult.ScopeExpired -> CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.REQUEST_SCOPE_EXPIRED))
            )
            is AdmissionResult.ScopeInvalid -> CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION))
            )
            is AdmissionResult.ServerBusy -> CompletableFuture.completedFuture(
                TcpResponseEnvelope(envelope.requestScope, envelope.requestId, ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.SERVER_BUSY))
            )
            is AdmissionResult.Admitted -> {
                val execFuture = executeInternalApi(req)
                execFuture.whenComplete { resp, err ->
                    val finalResp = if (err != null || resp == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        resp
                    }
                    s.receiptStore.completeReceipt(key, admission.inFlight, finalResp, isLobby = isLobby)
                }.thenApply { resp ->
                    TcpResponseEnvelope(envelope.requestScope, envelope.requestId, resp)
                }
            }
        }
    }

    private fun executeInternalApi(req: ApiRequest): CompletableFuture<ApiResponse> {
        val s = server
        return when (req.api) {
            ApiTags.LIST_ROOMS -> {
                val summaries = s?.registry?.listSummaries() ?: emptyList()
                CompletableFuture.completedFuture(ApiResponse(requestId = req.requestId, success = true, rooms = summaries))
            }
            ApiTags.CREATE_ROOM -> {
                createRoom(CreateRoomRequest(playerName = req.playerName, config = req.config)).thenApply { binding ->
                    ApiResponse(
                        requestId = req.requestId,
                        success = true,
                        roomId = binding.roomId,
                        seat = binding.seat,
                        resumeToken = binding.resumeToken,
                        snapshot = binding.snapshot
                    )
                }.exceptionally { ex ->
                    val apiEx = ServerApiException.unwrap(ex) as? ServerApiException
                    ApiResponse(requestId = req.requestId, success = false, error = apiEx?.code ?: ApiErrors.INTERNAL_ERROR)
                }
            }
            ApiTags.JOIN_ROOM -> {
                val roomId = req.roomId ?: ""
                joinRoom(JoinRoomRequest(roomId = roomId, playerName = req.playerName, preferSeat = req.preferSeat)).thenApply { binding ->
                    ApiResponse(
                        requestId = req.requestId,
                        success = true,
                        roomId = binding.roomId,
                        seat = binding.seat,
                        resumeToken = binding.resumeToken,
                        snapshot = binding.snapshot
                    )
                }.exceptionally { ex ->
                    val apiEx = ServerApiException.unwrap(ex) as? ServerApiException
                    ApiResponse(requestId = req.requestId, success = false, error = apiEx?.code ?: ApiErrors.INTERNAL_ERROR)
                }
            }
            ApiTags.LEAVE_ROOM -> {
                leaveRoom().thenApply { receipt ->
                    ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                }.exceptionally { ex ->
                    val apiEx = ServerApiException.unwrap(ex) as? ServerApiException
                    ApiResponse(requestId = req.requestId, success = false, error = apiEx?.code ?: ApiErrors.INTERNAL_ERROR)
                }
            }
            ApiTags.SET_READY -> {
                setReady(req.ready ?: true).thenApply { receipt ->
                    ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                }.exceptionally { ex ->
                    val apiEx = ServerApiException.unwrap(ex) as? ServerApiException
                    ApiResponse(requestId = req.requestId, success = false, error = apiEx?.code ?: ApiErrors.INTERNAL_ERROR)
                }
            }
            else -> {
                CompletableFuture.completedFuture(
                    ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNKNOWN_REQUEST_TYPE)
                )
            }
        }
    }

    // ========================================
    // AutoCloseable 资源释放（幂等）
    // ========================================

    override fun close() {
        // 用户主动关闭与回调关闭认领共用同一原子协议：认领成功后在锁外统一收尾，幂等
        val claim = claimClosure(ApiProtocol.LOCAL_CLOSED) ?: return
        runCloseTeardown(claim)
    }

    override fun toString(): String {
        return "InProcessBinding(seat=$viewerSeat, closed=$closed, disconnectReason=$disconnectReason)"
    }
}

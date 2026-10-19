package com.majiang.server.tcp

import com.majiang.access.PlayerSnapshot
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ServerApiException
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolPublicSnapshot
import java.util.concurrent.CompletableFuture
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 服务端逻辑角色状态接口 (ServerRoleState, D-02 / §9.2)
 *
 * 将可替换的底层物理传输资源 (TransportConnection) 与生命周期更长、具备独立认证
 * 标识的业务角色状态解耦：
 * 1. 业务回执与作用域归属于已验证逻辑身份 (logicalPrincipal)，而非单次 TCP 物理连接；
 * 2. 角色内部订阅 (seatSubscription / publicSubscription / roomSubscription)
 *    收归具体角色状态私有管理，禁止传输层随意读写角色内部状态；
 * 3. 拥有明确的安装、替换 (顶号)、关闭及席位/观战代次校验责任。
 */
sealed interface ServerRoleState {
    val role: ConnectionRole
    val logicalPrincipal: String
    fun onConnectionClosed(connection: TransportConnection)
    fun detach()
}

/**
 * 房间内角色状态抽象 (RoomRoleState)
 */
sealed interface RoomRoleState : ServerRoleState {
    val room: SharedRoom
    val bindingGeneration: Long
    val memberGeneration: Long get() = bindingGeneration
}

/**
 * 未认证角色状态 (握手前连接初始态)
 */
object UnauthenticatedRoleState : ServerRoleState {
    override val role: ConnectionRole get() = ConnectionRole.UNBOUND_LOBBY
    override val logicalPrincipal: String get() = "unauthenticated"
    override fun onConnectionClosed(connection: TransportConnection) {}
    override fun detach() {}
    override fun toString(): String = "UnauthenticatedRoleState"
}

/**
 * 已认证大厅角色状态 (握手完成但未进入任何房间)
 */
class LobbyRoleState(
    val lobbyResumeToken: String
) : ServerRoleState {
    init {
        require(lobbyResumeToken.isNotBlank()) { "lobbyResumeToken cannot be blank" }
    }

    override val role: ConnectionRole get() = ConnectionRole.UNBOUND_LOBBY
    override val logicalPrincipal: String get() = "lobby:$lobbyResumeToken"
    override fun onConnectionClosed(connection: TransportConnection) {}
    override fun detach() {}
    override fun toString(): String = "LobbyRoleState(principal=$logicalPrincipal)"
}

/**
 * 逻辑玩家角色状态 (PlayerState, D-02 / §9.2)
 *
 * 核心契约：
 * 1. 席位严格限定在 0..3，类型安全，彻底杜绝逻辑角色内部出现 seat = -1；
 * 2. 携带快照提供者 [snapshotSupplier]，全量同步与状态查询统一经此入口；
 * 3. 严格私有封装席位私有流订阅与房间更新订阅，对外提供单一生命周期入口；
 * 4. 封装成员代次 [bindingGeneration] 与会话校验器 [makeSessionValidator]；
 * 5. 精确一次的断线通知 [notifyDisconnect]，携带自身捕获的代次通知房间连接控制，
 *    被顶号后断开绝不误伤新连接。
 */
class PlayerState(
    val seat: Int,
    val snapshotSupplier: () -> CompletableFuture<PlayerSnapshot>,
    override val room: SharedRoom,
    override val bindingGeneration: Long = if (seat in 0..3) room.seatMemberGeneration(seat) else 1L
) : RoomRoleState, AutoCloseable {

    init {
        require(seat in 0..3) { "PlayerState seat must be in 0..3, got $seat" }
        require(bindingGeneration > 0L) { "bindingGeneration must be positive, got $bindingGeneration" }
    }

    override val role: ConnectionRole get() = ConnectionRole.PLAYER
    override val logicalPrincipal: String get() = "player:${room.roomId}:$seat"

    @Volatile
    private var seatSubscription: AutoCloseable? = null

    @Volatile
    private var roomSubscription: AutoCloseable? = null

    @Volatile
    private var displacedSubscription: AutoCloseable? = null

    @Volatile
    var isDetached: Boolean = false
        private set

    @Volatile
    var inFlightLeave: CompletableFuture<ProtocolActionReceipt>? = null

    private val disconnectNotified = AtomicBoolean(false)

    constructor(
        seat: Int,
        snapshotSupplier: () -> CompletableFuture<PlayerSnapshot>
    ) : this(
        seat = seat,
        snapshotSupplier = snapshotSupplier,
        room = SharedRoom(roomId = "mock-room", hostPlayerName = "mock"),
        bindingGeneration = 1L
    )

    /**
     * 安装席位私有订阅、可选房间订阅与顶替订阅
     */
    fun attachSubscriptions(seatSub: AutoCloseable?, roomSub: AutoCloseable? = null, displacedSub: AutoCloseable? = null) {
        synchronized(this) {
            if (isDetached) {
                seatSub?.close()
                roomSub?.close()
                displacedSub?.close()
                return
            }
            seatSubscription?.close()
            seatSubscription = seatSub
            if (roomSub != null) {
                roomSubscription?.close()
                roomSubscription = roomSub
            }
            if (displacedSub != null) {
                displacedSubscription?.close()
                displacedSubscription = displacedSub
            }
        }
    }

    /**
     * 更新房间更新广播订阅
     */
    fun updateRoomSubscription(roomSub: AutoCloseable?) {
        synchronized(this) {
            if (isDetached) {
                roomSub?.close()
                return
            }
            roomSubscription?.close()
            roomSubscription = roomSub
        }
    }

    /**
     * 更新席位顶替通知订阅
     */
    fun updateDisplacedSubscription(displacedSub: AutoCloseable?) {
        synchronized(this) {
            if (isDetached) {
                displacedSub?.close()
                return
            }
            displacedSubscription?.close()
            displacedSubscription = displacedSub
        }
    }

    /**
     * 解除并关闭全部订阅句柄
     */
    override fun detach() {
        synchronized(this) {
            isDetached = true
            try {
                seatSubscription?.close()
            } catch (_: Throwable) {
            }
            seatSubscription = null
            try {
                roomSubscription?.close()
            } catch (_: Throwable) {
            }
            roomSubscription = null
            try {
                displacedSubscription?.close()
            } catch (_: Throwable) {
            }
            displacedSubscription = null
        }
    }

    override fun close() {
        detach()
    }

    /**
     * 生成与当前玩家席位及代次强绑定的会话有效性校验器 (R7 / UC-S08)
     */
    fun makeSessionValidator(transport: TransportConnection? = null): () -> Boolean = {
        !isDetached &&
            !disconnectNotified.get() &&
            (transport == null || !transport.isClosingOrClosed) &&
            !room.closed &&
            room.seatMemberGeneration(seat) == bindingGeneration
    }

    /**
     * 物理连接关闭时精确一次通知房间连接控制 (R02)
     */
    override fun onConnectionClosed(connection: TransportConnection) {
        notifyDisconnect()
    }

    /**
     * 显式通知席位断线（带自身捕获代次，防止顶号后误杀新连）
     */
    fun notifyDisconnect(): CompletableFuture<Unit> {
        if (disconnectNotified.compareAndSet(false, true)) {
            return try {
                room.disconnectSeat(seat, bindingGeneration)
            } catch (t: Throwable) {
                CompletableFuture.failedFuture(t)
            }
        }
        return CompletableFuture.completedFuture(Unit)
    }

    override fun toString(): String =
        "PlayerState(room=${room.roomId}, seat=$seat, gen=$bindingGeneration, principal=$logicalPrincipal)"

    companion object {
        fun fromRoom(
            room: SharedRoom,
            seat: Int,
            bindingGeneration: Long? = null,
            snapshotSupplier: (() -> CompletableFuture<PlayerSnapshot>)? = null
        ): PlayerState {
            require(seat in 0..3) { "PlayerState seat must be in 0..3, got $seat" }
            val resolvedGen = bindingGeneration ?: room.seatMemberGeneration(seat)
            return PlayerState(
                seat = seat,
                snapshotSupplier = snapshotSupplier ?: {
                    room.snapshot(seat) { !room.closed && room.seatMemberGeneration(seat) == resolvedGen }
                        .thenApply { it ?: throw ServerApiException(ApiErrors.INTERNAL_ERROR, "Player snapshot unavailable") }
                },
                room = room,
                bindingGeneration = resolvedGen
            )
        }
    }
}

/**
 * 逻辑观战者角色状态 (SpectatorState, D-02 / §9.2)
 *
 * 核心契约：
 * 1. 标识符严格非空，拥有公开快照提供者 [publicSnapshotSupplier]；
 * 2. 严格私有封装公开流订阅与房间更新订阅，与玩家私有流彻底物理隔离；
 * 3. 封装观战成员代次 [bindingGeneration] 与会话校验器 [makeSessionValidator]；
 * 4. 精确一次的断线通知 [notifyDisconnect]，携带捕获代次通知房间。
 */
class SpectatorState(
    val spectatorId: String,
    val publicSnapshotSupplier: () -> CompletableFuture<ProtocolPublicSnapshot>,
    override val room: SharedRoom,
    override val bindingGeneration: Long = room.spectators[spectatorId]?.generation ?: 1L
) : RoomRoleState, AutoCloseable {

    init {
        require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
        require(bindingGeneration > 0L) { "bindingGeneration must be positive, got $bindingGeneration" }
    }

    override val role: ConnectionRole get() = ConnectionRole.SPECTATOR
    override val logicalPrincipal: String get() = "spectator:${room.roomId}:$spectatorId"

    @Volatile
    private var publicSubscription: AutoCloseable? = null

    @Volatile
    private var roomSubscription: AutoCloseable? = null

    @Volatile
    private var displacedSubscription: AutoCloseable? = null

    @Volatile
    var isDetached: Boolean = false
        private set

    @Volatile
    var inFlightLeave: CompletableFuture<ProtocolActionReceipt>? = null

    private val disconnectNotified = AtomicBoolean(false)

    constructor(
        spectatorId: String,
        publicSnapshotSupplier: () -> CompletableFuture<ProtocolPublicSnapshot>
    ) : this(
        spectatorId = spectatorId,
        publicSnapshotSupplier = publicSnapshotSupplier,
        room = SharedRoom(roomId = "mock-room", hostPlayerName = "mock"),
        bindingGeneration = 1L
    )

    /**
     * 安装公开流订阅、可选房间订阅与顶替通知订阅
     */
    fun attachSubscriptions(publicSub: AutoCloseable?, roomSub: AutoCloseable? = null, displacedSub: AutoCloseable? = null) {
        synchronized(this) {
            if (isDetached) {
                publicSub?.close()
                roomSub?.close()
                displacedSub?.close()
                return
            }
            publicSubscription?.close()
            publicSubscription = publicSub
            if (roomSub != null) {
                roomSubscription?.close()
                roomSubscription = roomSub
            }
            if (displacedSub != null) {
                displacedSubscription?.close()
                displacedSubscription = displacedSub
            }
        }
    }

    /**
     * 更新房间更新广播订阅
     */
    fun updateRoomSubscription(roomSub: AutoCloseable?) {
        synchronized(this) {
            if (isDetached) {
                roomSub?.close()
                return
            }
            roomSubscription?.close()
            roomSubscription = roomSub
        }
    }

    /**
     * 更新观战顶替通知订阅
     */
    fun updateDisplacedSubscription(displacedSub: AutoCloseable?) {
        synchronized(this) {
            if (isDetached) {
                displacedSub?.close()
                return
            }
            displacedSubscription?.close()
            displacedSubscription = displacedSub
        }
    }

    /**
     * 解除并关闭全部订阅句柄
     */
    override fun detach() {
        synchronized(this) {
            isDetached = true
            try {
                publicSubscription?.close()
            } catch (_: Throwable) {
            }
            publicSubscription = null
            try {
                roomSubscription?.close()
            } catch (_: Throwable) {
            }
            roomSubscription = null
            try {
                displacedSubscription?.close()
            } catch (_: Throwable) {
            }
            displacedSubscription = null
        }
    }

    override fun close() {
        detach()
    }

    /**
     * 生成与当前观战者强绑定的会话有效性校验器 (拒绝 closing/closed 物理连接并核对当前成员代次)
     */
    fun makeSessionValidator(transport: TransportConnection? = null): () -> Boolean = {
        !isDetached &&
            !disconnectNotified.get() &&
            (transport == null || !transport.isClosingOrClosed) &&
            !room.closed &&
            (room.spectators[spectatorId]?.generation == bindingGeneration)
    }

    /**
     * 物理连接关闭时精确一次通知房间连接控制 (R02)
     */
    override fun onConnectionClosed(connection: TransportConnection) {
        notifyDisconnect()
    }

    /**
     * 显式通知观战者断线（带自身捕获代次，防止顶号后误杀新连）
     */
    fun notifyDisconnect(): CompletableFuture<Unit> {
        if (disconnectNotified.compareAndSet(false, true)) {
            return try {
                room.disconnectSpectator(spectatorId, bindingGeneration)
            } catch (t: Throwable) {
                CompletableFuture.failedFuture(t)
            }
        }
        return CompletableFuture.completedFuture(Unit)
    }

    override fun toString(): String =
        "SpectatorState(room=${room.roomId}, spectatorId=$spectatorId, gen=$bindingGeneration, principal=$logicalPrincipal)"

    companion object {
        fun fromRoom(
            room: SharedRoom,
            spectatorId: String,
            bindingGeneration: Long? = null,
            publicSnapshotSupplier: (() -> CompletableFuture<ProtocolPublicSnapshot>)? = null
        ): SpectatorState {
            require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
            val resolvedGen = bindingGeneration ?: (room.spectators[spectatorId]?.generation ?: 1L)
            return SpectatorState(
                spectatorId = spectatorId,
                publicSnapshotSupplier = publicSnapshotSupplier ?: {
                    room.publicSnapshot { !room.closed && (room.spectators[spectatorId]?.generation == resolvedGen) }
                        .thenApply { it ?: throw ServerApiException(ApiErrors.INTERNAL_ERROR, "Public snapshot unavailable") }
                },
                room = room,
                bindingGeneration = resolvedGen
            )
        }
    }
}

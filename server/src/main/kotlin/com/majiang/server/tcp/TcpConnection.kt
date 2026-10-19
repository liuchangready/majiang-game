package com.majiang.server.tcp

import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.tcp.TcpClose
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpMessageCodec
import java.io.IOException
import java.net.SocketAddress
import java.nio.ByteBuffer
import java.nio.channels.SelectionKey
import java.nio.channels.SocketChannel
import java.util.ArrayDeque
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * 连接角色分类
 */
enum class ConnectionRole {
    UNBOUND_LOBBY,
    PLAYER,
    SPECTATOR
}

/**
 * 连接生命周期状态
 */
enum class ConnectionState {
    AWAITING_HELLO,
    HANDSHAKED,
    CLOSING,
    CLOSED
}

/**
 * 服务端 TCP 客户端物理传输连接对象 (TcpConnection, 契约 1 / 契约 2 / 契约 3 / 契约 5 / D-02)
 *
 * 核心设计：
 * 1. 物理传输资源拥有权：持有底层 [SocketChannel]、[SelectionKey]、独立有状态 [TcpFrameDecoder]、出站队列 [outboundQueue] 与串行接收邮箱；
 * 2. 状态机与原子关闭：AWAITING_HELLO -> HANDSHAKED -> CLOSING -> CLOSED；关闭原子幂等，保证资源安全释放；
 * 3. 角色状态解耦 (D-02)：物理连接资源与逻辑角色状态 (PlayerState/SpectatorState) 分开，业务回执与作用域归属于已验证逻辑身份；
 * 4. 严格时钟与超时监测：
 *    - 握手超时 (5 秒，HANDSHAKE_TIMEOUT)；
 *    - 入站判死 (10 秒无完整合法入站帧，HEARTBEAT_TIMEOUT；零散字节不刷新计时，UC-L07)；
 *    - 不完整帧硬截止 (首字节起 10 秒硬截止，UC-L08)；
 *    - 停滞写入监测 (连续 10 秒写零进展，SLOW_CONSUMER，UC-L09)；
 *    - 终态通知收尾 (最多 1 秒排出 CLOSE 帧后强制关闭 channel，UC-L11)；
 * 5. 串行接收邮箱：有界 64 帧 / 4 MiB，计入全局预算，背压读或慢端断开 (UC-L02)；
 * 6. 安全脱敏：toString 彻底脱敏，绝不输出 token 与敏感业务信息。
 */
class TcpConnection(
    override val connectionId: Long,
    val channel: SocketChannel,
    val config: TcpServerConfig,
    val globalBudget: TcpGlobalBudget
) : TransportConnection, AutoCloseable {

    @Volatile
    var selectionKey: SelectionKey? = null

    val decoder: TcpFrameDecoder = TcpFrameDecoder(
        reserveBudget = { globalBudget.reserve(it) },
        releaseBudget = { globalBudget.release(it) }
    )

    val outboundQueue: TcpOutboundQueue = TcpOutboundQueue(
        globalBudget = globalBudget,
        maxNormalFrames = config.maxOutboundQueueFrames,
        maxNormalBytes = config.maxOutboundQueueBytes,
        maxControlFrames = config.maxControlQueueFrames,
        maxControlBytes = config.maxControlQueueBytes
    )

    val state: AtomicReference<ConnectionState> = AtomicReference(ConnectionState.AWAITING_HELLO)

    override val remoteAddress: SocketAddress = try {
        channel.remoteAddress
    } catch (_: Throwable) {
        object : SocketAddress() {
            override fun toString(): String = "unknown"
        }
    }

    // 握手与逻辑身份信息
    @Volatile
    var lobbyResumeToken: String? = null
        private set

    @Volatile
    var logicalPrincipal: String? = null
        private set

    @Volatile
    override var connectionEpoch: String = ""
        private set

    // 逻辑角色状态引用 (D-02: 物理连接不直接维护业务角色内部状态，委托给 ServerRoleState)
    @Volatile
    var roleState: ServerRoleState = UnauthenticatedRoleState
        private set

    // 角色只读投影访问器 (只读派生，严禁外部直接修改角色内部状态)
    val role: ConnectionRole get() = roleState.role
    val playerState: PlayerState? get() = roleState as? PlayerState
    val spectatorState: SpectatorState? get() = roleState as? SpectatorState
    val lobbyState: LobbyRoleState? get() = roleState as? LobbyRoleState
    val activeRoom: SharedRoom? get() = (roleState as? RoomRoleState)?.room
    val viewerSeat: Int get() = playerState?.seat ?: -1
    val spectatorId: String? get() = spectatorState?.spectatorId
    val currentBindingGeneration: Long get() = (roleState as? RoomRoleState)?.bindingGeneration ?: 1L

    // 接收串行邮箱 (Mailbox: max 64 frames / 4 MiB, UC-L02)
    private val mailboxLock = Any()
    private val serialMailbox = ArrayDeque<TcpFrame>()
    private var mailboxBytes: Long = 0L
    val mailboxProcessing: AtomicBoolean = AtomicBoolean(false)

    // 时间戳与监控
    val createdAtMillis: Long = config.clock.currentTimeMillis()
    val handshakeDeadlineMillis: Long = createdAtMillis + config.handshakeTimeoutMillis

    private val _lastInboundTime = AtomicLong(createdAtMillis)
    val lastInboundTimeMillis: Long get() = _lastInboundTime.get()

    @Volatile
    var incompleteFrameStartTimeMillis: Long? = null

    private val _lastWriteProgressTime = AtomicLong(createdAtMillis)
    val lastWriteProgressTimeMillis: Long get() = _lastWriteProgressTime.get()

    @Volatile
    var closingStartTimeMillis: Long? = null
        private set

    @Volatile
    var disconnectReason: String? = null
        private set

    @Volatile
    var disconnectMessage: String? = null
        private set

    // 未完成的在途请求映射 (以 IdempotencyKey 为键，供断开时结构化失败，R6)
    val inFlightRequests = ConcurrentHashMap<IdempotencyKey, CompletableFuture<ApiResponse>>()

    // 未握手桶计数归还标志 (保证精确一次归还，R3)
    val unhandshakedReturned: AtomicBoolean = AtomicBoolean(false)

    /** 尝试认领归还未握手桶计数，仅首次返回 true (R3) */
    fun markUnhandshakedReturned(): Boolean = unhandshakedReturned.compareAndSet(false, true)

    /** 连接是否已经处于关闭态 */
    override val isClosed: Boolean get() = state.get() == ConnectionState.CLOSED

    /** 连接是否处于正在收尾或已关闭态 */
    override val isClosingOrClosed: Boolean
        get() = state.get().let { it == ConnectionState.CLOSING || it == ConnectionState.CLOSED }

    /** 连接是否已完成握手 */
    val isHandshaked: Boolean get() = state.get() == ConnectionState.HANDSHAKED

    /**
     * 记录收到完整合法入站帧（仅在完整合法帧时刷新判死时钟，UC-L06 / UC-L07）
     */
    fun recordValidInboundFrame(nowMillis: Long = config.clock.currentTimeMillis()) {
        _lastInboundTime.set(nowMillis)
    }

    /**
     * 记录写出进展
     */
    fun recordWriteProgress(nowMillis: Long = config.clock.currentTimeMillis()) {
        _lastWriteProgressTime.set(nowMillis)
    }

    /**
     * 设置握手完成身份并安装 LobbyRoleState
     */
    fun setHandshakedIdentity(
        principal: String,
        token: String,
        epoch: String
    ) {
        require(principal.isNotBlank()) { "principal cannot be blank" }
        require(token.isNotBlank()) { "token cannot be blank" }
        require(epoch.isNotBlank()) { "epoch cannot be blank" }

        val lobbyRole = LobbyRoleState(token)
        val oldRole = synchronized(this) {
            if (isClosingOrClosed) {
                return
            }
            this.lobbyResumeToken = token
            this.connectionEpoch = epoch
            this.state.set(ConnectionState.HANDSHAKED)
            val old = roleState
            roleState = lobbyRole
            logicalPrincipal = lobbyRole.logicalPrincipal
            old
        }
        if (oldRole !== lobbyRole) {
            oldRole.detach()
        }
    }

    /**
     * 在安装锁边界内原子读取当前已验证的逻辑身份与角色快照 (D-08 / §9.2)
     *
     * 保持与 installPlayerRole / installSpectatorRole / unbindToLobbyRole / setHandshakedIdentity
     * 处于同一个 synchronized(this) 原子锁边界：
     * 1. 处于握手完成态 (HANDSHAKED) 且非关闭态；
     * 2. roleState 绝不能是 UnauthenticatedRoleState；
     * 3. logicalPrincipal 严格非空且必须与 roleState.logicalPrincipal 完全一致；
     * 4. 彻底杜绝任何“旧 principal + 新角色”或“新 principal + 旧角色”的交错瞬态泄露。
     */
    fun snapshotLogicalIdentity(): Pair<String, ServerRoleState>? {
        return synchronized(this) {
            if (!isHandshaked || isClosingOrClosed) return null
            val role = roleState
            if (role is UnauthenticatedRoleState) return null
            val principal = logicalPrincipal ?: return null
            if (principal.isBlank() || principal != role.logicalPrincipal) return null
            Pair(principal, role)
        }
    }

    /**
     * 原子安装玩家角色状态 (D-02 / §9.2)
     * 若连接处于 CLOSING 或 CLOSED 状态，拒绝安装并返回 false
     */
    fun installPlayerRole(newPlayerState: PlayerState): Boolean {
        val oldRole = synchronized(this) {
            if (isClosingOrClosed) {
                return false
            }
            val old = roleState
            roleState = newPlayerState
            logicalPrincipal = newPlayerState.logicalPrincipal
            old
        }
        if (oldRole !== newPlayerState) {
            oldRole.detach()
        }
        return true
    }

    /**
     * 原子安装观战者角色状态 (D-02 / §9.2)
     * 若连接处于 CLOSING 或 CLOSED 状态，拒绝安装并返回 false
     */
    fun installSpectatorRole(newSpectatorState: SpectatorState): Boolean {
        val oldRole = synchronized(this) {
            if (isClosingOrClosed) {
                return false
            }
            val old = roleState
            roleState = newSpectatorState
            logicalPrincipal = newSpectatorState.logicalPrincipal
            old
        }
        if (oldRole !== newSpectatorState) {
            oldRole.detach()
        }
        return true
    }

    /**
     * 迁移至经过认证绑定的玩家角色身份 (兼容便捷方法)
     */
    fun bindPlayerRole(
        room: SharedRoom,
        seat: Int,
        bindingGeneration: Long = room.seatMemberGeneration(seat)
    ): Boolean {
        val player = PlayerState.fromRoom(room, seat, bindingGeneration)
        return installPlayerRole(player)
    }

    /**
     * 迁移至经过认证绑定的观战者角色身份 (兼容便捷方法)
     */
    fun bindSpectatorRole(
        room: SharedRoom,
        spectatorId: String,
        bindingGeneration: Long = 1L
    ): Boolean {
        val spectator = SpectatorState.fromRoom(room, spectatorId, bindingGeneration)
        return installSpectatorRole(spectator)
    }

    /**
     * 角色解绑并回退至大厅身份 (R06 / D-02)
     */
    fun unbindToLobbyRole() {
        val token = this.lobbyResumeToken
        val newRole = if (token != null) LobbyRoleState(token) else UnauthenticatedRoleState
        val oldRole = synchronized(this) {
            if (isClosingOrClosed) {
                return
            }
            val old = roleState
            roleState = newRole
            logicalPrincipal = newRole.logicalPrincipal
            old
        }
        if (oldRole !== newRole) {
            oldRole.detach()
        }
    }

    /**
     * 安全更新经过认证绑定的逻辑主体身份
     */
    fun updateLogicalPrincipal(newPrincipal: String) {
        require(newPrincipal.isNotBlank()) { "principal cannot be blank" }
        this.logicalPrincipal = newPrincipal
    }

    /**
     * 入队接收串行邮箱 (UC-L02)
     *
     * @param frame 完整解码出的入站帧
     * @param alreadyReserved 帧载荷预算是否已由解码器预留 (默认 true)
     * @return true 表示成功放入邮箱；false 表示邮箱满或全局预算耗尽 (需背压或断开)
     */
    fun enqueueMailbox(frame: TcpFrame, alreadyReserved: Boolean = true): Boolean {
        val size = frame.payload.size.toLong()
        synchronized(mailboxLock) {
            if (serialMailbox.size >= config.maxMailboxFrames || mailboxBytes + size > config.maxMailboxBytes) {
                if (alreadyReserved) {
                    globalBudget.release(size)
                }
                return false
            }
            if (!alreadyReserved && !globalBudget.reserve(size)) {
                return false
            }
            serialMailbox.add(frame)
            mailboxBytes += size
            return true
        }
    }

    /**
     * 从接收串行邮箱中取出一个帧 (预算在该帧完全处理后由工作线程归还)
     */
    fun pollMailbox(): TcpFrame? {
        synchronized(mailboxLock) {
            val frame = serialMailbox.poll() ?: return null
            val size = frame.payload.size.toLong()
            mailboxBytes -= size
            return frame
        }
    }

    /** 邮箱中当前积压帧数 */
    val mailboxSize: Int get() = synchronized(mailboxLock) { serialMailbox.size }

    /**
     * 发送业务普通帧 (RSP / UPDATE)
     */
    override fun sendNormalFrame(frameBytes: ByteArray): Boolean {
        if (isClosingOrClosed) return false
        val ok = outboundQueue.enqueueNormal(frameBytes)
        if (!ok) {
            // 业务队列已满 -> 标记慢客户端并关闭 (UC-L01)
            close(TcpCloseReasons.SLOW_CONSUMER, "Outbound queue full or budget exceeded", immediate = false)
        } else {
            enableWriteInterest()
        }
        return ok
    }

    /**
     * 发送控制优先级帧 (PING / PONG / CLOSE)
     */
    override fun sendControlFrame(frameBytes: ByteArray): Boolean {
        val ok = outboundQueue.enqueueControl(frameBytes)
        if (!ok) {
            // 连控制帧都无法入队 -> 直接断开 (UC-C05)
            close(TcpCloseReasons.SERVER_OVERLOADED, "Control queue saturated", immediate = true)
        } else {
            enableWriteInterest()
        }
        return ok
    }

    /**
     * 激活 SelectionKey 的写兴趣，促使 selector 循环尽快写出
     */
    fun enableWriteInterest() {
        val key = selectionKey
        if (key != null && key.isValid) {
            try {
                key.interestOpsOr(SelectionKey.OP_WRITE)
                key.selector().wakeup()
            } catch (_: Throwable) {
            }
        }
    }

    /**
     * 请求关闭连接
     *
     * @param reason 稳定关闭原因 (TcpCloseReasons)
     * @param message 可选描述
     * @param immediate 是否跳过收尾立即强切 channel
     */
    override fun close(
        reason: String,
        message: String?,
        immediate: Boolean
    ) {
        if (disconnectReason == null) {
            disconnectReason = reason
            disconnectMessage = message
        }

        if (immediate) {
            forceCloseChannel()
            return
        }

        var shouldForceClose = false
        val enteredClosing = synchronized(this) {
            val cur = state.get()
            if (cur == ConnectionState.AWAITING_HELLO || cur == ConnectionState.HANDSHAKED) {
                // 关键防线：必须在变更状态为 CLOSING 前将 CLOSE 控制帧入队并激活写兴趣！
                // 严防 selector 线程在状态变为 CLOSING 但控制帧尚未入队的微秒级竞态窗口内误判队列已空并提前 shutdownOutput(FIN)！
                val closeFrameBytes = try {
                    TcpMessageCodec.toFrame(TcpClose(reason = reason, message = message)).toByteArray()
                } catch (_: Throwable) {
                    shouldForceClose = true
                    return@synchronized false
                }

                val enqueued = outboundQueue.enqueueControl(closeFrameBytes)
                if (!enqueued) {
                    // 控制队列饱和 (8帧/8KiB) 或全局预算耗尽时，enqueueControl 返回 false。
                    // 依据 UC-C05 契约与资源保护原则，严禁进入 CLOSING 假装入队，必须立即切断连接释放资源。
                    shouldForceClose = true
                    return@synchronized false
                }

                enableWriteInterest()
                state.set(ConnectionState.CLOSING)
                closingStartTimeMillis = config.clock.currentTimeMillis()
                true
            } else {
                false
            }
        }

        if (shouldForceClose) {
            forceCloseChannel()
            return
        }

        if (!enteredClosing && state.get() == ConnectionState.CLOSING) {
            // 已在 CLOSING，检查是否超时需要强切
            val start = closingStartTimeMillis ?: 0L
            if (config.clock.currentTimeMillis() - start >= config.terminalFlushTimeoutMillis) {
                forceCloseChannel()
            }
        }
    }

    /**
     * 强制立即切断底层的 SocketChannel 并彻底释放所有本地与全局资源
     */
    fun forceCloseChannel() {
        val roleToNotify: ServerRoleState? = synchronized(this) {
            if (state.getAndSet(ConnectionState.CLOSED) == ConnectionState.CLOSED) {
                null
            } else {
                roleState
            }
        }
        if (roleToNotify == null) {
            return
        }

        // 1. 取消房间订阅
        detachSubscriptions()

        // 2. 取消 SelectionKey
        try {
            selectionKey?.cancel()
        } catch (_: Throwable) {
        }
        selectionKey = null

        // 3. 关闭底层 SocketChannel (排空接收缓冲残留并关闭输出方向以发出 FIN，防止 RST 抹除 CLOSE 帧)
        try {
            if (channel.isOpen) {
                try {
                    val drainBuf = ByteBuffer.allocate(4096)
                    while (channel.read(drainBuf) > 0) {
                        drainBuf.clear()
                    }
                    channel.shutdownOutput()
                } catch (_: Throwable) {
                }
                channel.close()
            }
        } catch (_: Throwable) {
        }

        // 4. 清理写队列并释放全局预算
        outboundQueue.clear()

        // 5. 清理接收邮箱并释放全局预算
        synchronized(mailboxLock) {
            var releasedBytes = 0L
            while (serialMailbox.isNotEmpty()) {
                releasedBytes += serialMailbox.poll().payload.size
            }
            mailboxBytes = 0L
            if (releasedBytes > 0L) {
                globalBudget.release(releasedBytes)
            }
        }

        // 5.1 清理解码器中暂存的未完整载荷并释放对应预算
        decoder.clearAndRelease()

        // 6. 结构化失败所有挂起的在途请求 (UC-C04)
        val reasonCode = disconnectReason ?: TcpCloseReasons.LOCAL_CLOSED
        val ex = IOException("Connection closed ($reasonCode): ${disconnectMessage ?: reasonCode}")
        val it = inFlightRequests.values.iterator()
        while (it.hasNext()) {
            val f = it.next()
            it.remove()
            f.completeExceptionally(ex)
        }

        // 7. 精确一次通知房间连接控制 (R02 / D-02 委托角色自身处理)
        roleToNotify.onConnectionClosed(this)
    }

    /**
     * 精确一次通知房间连接控制 (R02 / D-02 委托当前角色自身处理)
     */
    fun notifyRoomDisconnect() {
        val role = synchronized(this) { roleState }
        role.onConnectionClosed(this)
    }

    /**
     * 取消房间相关所有订阅
     */
    fun detachSubscriptions() {
        roleState.detach()
    }

    override fun close() {
        close(TcpCloseReasons.LOCAL_CLOSED, "Connection closed locally", immediate = true)
    }

    override fun toString(): String {
        return "TcpConnection(id=$connectionId, state=${state.get()}, principal=***, remote=$remoteAddress)"
    }
}

package com.majiang.server.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.tcp.TcpClose
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpMessageCodec
import java.io.IOException
import java.net.SocketAddress
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
 * 服务端 TCP 客户端连接对象 (TcpConnection, 契约 1 / 契约 2 / 契约 3 / 契约 5)
 *
 * 核心设计：
 * 1. 资源拥有权：持有底层 [SocketChannel]、[SelectionKey]、独立有状态 [TcpFrameDecoder]、出站队列 [outboundQueue] 与串行接收邮箱；
 * 2. 状态机与原子关闭：AWAITING_HELLO -> HANDSHAKED -> CLOSING -> CLOSED；关闭原子幂等，保证资源安全释放；
 * 3. 严格时钟与超时监测：
 *    - 握手超时 (5 秒，HANDSHAKE_TIMEOUT)；
 *    - 入站判死 (10 秒无完整合法入站帧，HEARTBEAT_TIMEOUT；零散字节不刷新计时，UC-L07)；
 *    - 不完整帧硬截止 (首字节起 10 秒硬截止，UC-L08)；
 *    - 停滞写入监测 (连续 10 秒写零进展，SLOW_CONSUMER，UC-L09)；
 *    - 终态通知收尾 (最多 1 秒排出 CLOSE 帧后强制关闭 channel，UC-L11)；
 * 4. 串行接收邮箱：有界 64 帧 / 4 MiB，计入全局预算，背压读或慢端断开 (UC-L02)；
 * 5. 安全脱敏：toString 彻底脱敏，绝不输出 token 与敏感业务信息。
 */
class TcpConnection(
    val connectionId: Long,
    val channel: SocketChannel,
    val config: TcpServerConfig,
    val globalBudget: TcpGlobalBudget
) : AutoCloseable {

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

    val remoteAddress: SocketAddress = try {
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
    var connectionEpoch: String = ""
        private set

    @Volatile
    var role: ConnectionRole = ConnectionRole.UNBOUND_LOBBY

    @Volatile
    var activeRoom: SharedRoom? = null

    @Volatile
    var viewerSeat: Int = -1

    @Volatile
    var spectatorId: String? = null

    @Volatile
    var currentBindingGeneration: Long = 1L

    // 房间订阅句柄
    @Volatile
    var seatSubscription: AutoCloseable? = null

    @Volatile
    var publicSubscription: AutoCloseable? = null

    @Volatile
    var roomSubscription: AutoCloseable? = null

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

    // 断开通知房间连接控制标志 (保证精确一次通知，R02)
    val disconnectNotified: AtomicBoolean = AtomicBoolean(false)

    /** 尝试认领归还未握手桶计数，仅首次返回 true (R3) */
    fun markUnhandshakedReturned(): Boolean = unhandshakedReturned.compareAndSet(false, true)

    /** 连接是否已经处于关闭态 */
    val isClosed: Boolean get() = state.get() == ConnectionState.CLOSED

    /** 连接是否处于正在收尾或已关闭态 */
    val isClosingOrClosed: Boolean
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
     * 设置握手完成身份
     */
    fun setHandshakedIdentity(
        principal: String,
        token: String,
        epoch: String
    ) {
        require(principal.isNotBlank()) { "principal cannot be blank" }
        require(token.isNotBlank()) { "token cannot be blank" }
        require(epoch.isNotBlank()) { "epoch cannot be blank" }

        this.logicalPrincipal = principal
        this.lobbyResumeToken = token
        this.connectionEpoch = epoch
        this.state.set(ConnectionState.HANDSHAKED)
    }

    /**
     * 迁移至经过认证绑定的玩家角色身份 (R06)
     */
    fun bindPlayerRole(
        room: SharedRoom,
        seat: Int,
        bindingGeneration: Long = room.bindingGenerations[seat]
    ) {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        detachSubscriptions()
        this.activeRoom = room
        this.viewerSeat = seat
        this.spectatorId = null
        this.role = ConnectionRole.PLAYER
        this.currentBindingGeneration = bindingGeneration
        this.logicalPrincipal = "player:${room.roomId}:$seat"
    }

    /**
     * 迁移至经过认证绑定的观战者角色身份 (R06)
     */
    fun bindSpectatorRole(
        room: SharedRoom,
        spectatorId: String,
        bindingGeneration: Long = 1L
    ) {
        require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
        detachSubscriptions()
        this.activeRoom = room
        this.spectatorId = spectatorId
        this.viewerSeat = -1
        this.role = ConnectionRole.SPECTATOR
        this.currentBindingGeneration = bindingGeneration
        this.logicalPrincipal = "spectator:${room.roomId}:$spectatorId"
    }

    /**
     * 角色解绑并回退至大厅身份 (R06)
     */
    fun unbindToLobbyRole() {
        detachSubscriptions()
        this.activeRoom = null
        this.viewerSeat = -1
        this.spectatorId = null
        this.role = ConnectionRole.UNBOUND_LOBBY
        val token = this.lobbyResumeToken
        if (token != null) {
            this.logicalPrincipal = "lobby:$token"
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
    fun sendNormalFrame(frameBytes: ByteArray): Boolean {
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
    fun sendControlFrame(frameBytes: ByteArray): Boolean {
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
                key.interestOps(key.interestOps() or SelectionKey.OP_WRITE)
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
    fun close(
        reason: String,
        message: String? = null,
        immediate: Boolean = false
    ) {
        if (disconnectReason == null) {
            disconnectReason = reason
            disconnectMessage = message
        }

        if (immediate) {
            forceCloseChannel()
            return
        }

        // 尝试优雅进入 CLOSING 态
        if (state.compareAndSet(ConnectionState.AWAITING_HELLO, ConnectionState.CLOSING) ||
            state.compareAndSet(ConnectionState.HANDSHAKED, ConnectionState.CLOSING)) {
            closingStartTimeMillis = config.clock.currentTimeMillis()
            // 尽力排入 CLOSE 控制帧
            try {
                val closeFrame = TcpMessageCodec.toFrame(TcpClose(reason = reason, message = message))
                outboundQueue.enqueueControl(closeFrame.toByteArray())
                enableWriteInterest()
            } catch (_: Throwable) {
                forceCloseChannel()
            }
        } else if (state.get() == ConnectionState.CLOSING) {
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
        if (state.getAndSet(ConnectionState.CLOSED) == ConnectionState.CLOSED) {
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

        // 3. 关闭底层 SocketChannel
        try {
            if (channel.isOpen) {
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

        // 7. 精确一次通知房间连接控制 (R02)
        notifyRoomDisconnect()
    }

    /**
     * 精确一次通知房间连接控制 (R02)
     */
    fun notifyRoomDisconnect() {
        if (disconnectNotified.compareAndSet(false, true)) {
            val room = activeRoom ?: return
            val curRole = role
            val seat = viewerSeat
            val gen = currentBindingGeneration
            val specId = spectatorId
            if (curRole == ConnectionRole.PLAYER && seat in 0..3) {
                try {
                    room.disconnectSeat(seat, gen)
                } catch (_: Throwable) {
                }
            } else if (curRole == ConnectionRole.SPECTATOR && specId != null) {
                try {
                    room.disconnectSpectator(specId, gen)
                } catch (_: Throwable) {
                }
            }
        }
    }

    /**
     * 取消房间相关所有订阅
     */
    fun detachSubscriptions() {
        try {
            seatSubscription?.close()
        } catch (_: Throwable) {
        }
        seatSubscription = null

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
    }

    override fun close() {
        close(TcpCloseReasons.LOCAL_CLOSED, "Connection closed locally", immediate = true)
    }

    override fun toString(): String {
        return "TcpConnection(id=$connectionId, state=${state.get()}, principal=***, remote=$remoteAddress)"
    }
}

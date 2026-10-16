package com.majiang.client.transport.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.LobbyApi
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.PushChannel
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SessionApi
import com.majiang.server.api.SpectatorApi
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.common.Cancellable
import com.majiang.transport.common.TransportClock
import com.majiang.transport.common.TransportScheduler
import com.majiang.transport.tcp.TcpClose
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpLimits
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpNonce
import com.majiang.transport.tcp.TcpPing
import com.majiang.transport.tcp.TcpPong
import com.majiang.transport.tcp.TcpProtocolException
import com.majiang.transport.tcp.TcpConnectionException
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpScope
import com.majiang.transport.tcp.TcpWelcome
import com.majiang.transport.tcp.TcpWire
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.StandardSocketOptions
import java.nio.ByteBuffer
import java.nio.channels.ClosedChannelException
import java.nio.channels.ClosedSelectorException
import java.nio.channels.SelectionKey
import java.nio.channels.Selector
import java.nio.channels.SocketChannel
import java.util.ArrayDeque
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * TCP 客户端网络传输绑定实现 (TcpClientBinding, T3 核心契约 1–7)
 *
 * 实现了统一服务端连接契约 [ServerConnection]，封装 MJ-TCP/1 自封装协议栈与可靠传输逻辑。
 *
 * 核心架构特性：
 * 1. **单持久 TCP 承载**：单条持久 TCP 连接承载大厅及当前玩家/观战角色，TCP_NODELAY 必开，单连接专属读线程 (阻塞 SocketChannel/Socket)；
 * 2. **绑定对象跨重连存活 (契约 3)**：TcpClientBinding 实例在重连全程复用，重连由 [reconnect] 完成（重开 socket、以保存的 lobbyResumeToken 重新 HELLO/WELCOME），"重连只替换连接不新建线程池"；
 * 3. **作用域生命周期 (D6/D7/D9)**：持有效 scope 发送 REQ；收到 [ApiErrors.REQUEST_SCOPE_EXPIRED] 时透明调用 GET_REQUEST_SCOPE 续期并自动重试一次；
 * 4. **未决请求与幂等恢复 (UC-R10/UC-R11)**：在途 Future 受 limits.maxInFlightRequests 上限约束；断线时在途请求以结构化异常终结，副作用请求三元组 (原 scope, requestId, payload) 完整保留，会话恢复后按【原 scope】自动重放；
 * 5. **心跳与判死 (D5/D12/UC-L06/UC-L07)**：按 limits.heartbeatIntervalMillis(3s) 发送 PING (16 hex nonce)，服务端回传 PONG；10s 无完整合法帧判死断开；
 * 6. **早期推送缓冲与有序消费**：表现层注册监听器之前到达的 PLAYER_UPDATE/PUBLIC_UPDATE/ROOM_UPDATE 暂存有界早期队列 (上限 64)，注册时按序排出。
 */
class TcpClientBinding(
    val host: String,
    val port: Int,
    val clock: TransportClock = TransportClock.SYSTEM,
    val scheduler: TransportScheduler = ScheduledExecutorTransportScheduler(),
    private val ownsScheduler: Boolean = false,
    initialLobbyResumeToken: String? = null
) : ServerConnection {

    companion object {
        private val connectionCounter = AtomicInteger(1)

        fun isSideEffectApi(api: String): Boolean = when (api) {
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
            ApiTags.SUBMIT_BEAM_SWAP,
            ApiTags.SUBMIT_BEAM_SWAP_CHOICE,
            ApiTags.CLAIM_CONTROL,
            ApiTags.RELEASE_CONTROL -> true
            else -> false
        }
    }

    // 握手身份与作用域凭据 (契约 3: 可观测性暴露)
    @Volatile
    var lobbyResumeToken: String? = initialLobbyResumeToken
        private set

    @Volatile
    var currentScope: TcpScope? = null
        private set

    @Volatile
    var connectionEpoch: String? = null
        private set

    @Volatile
    var limits: TcpLimits = TcpLimits()
        private set

    // 房间绑定状态
    @Volatile
    var boundRoomId: String? = null
        private set

    @Volatile
    var boundSeat: Int? = null
        private set

    @Volatile
    var resumeToken: String? = null
        private set

    @Volatile
    var boundSpectatorId: String? = null
        private set

    @Volatile
    var boundSpectatorToken: String? = null
        private set

    @Volatile
    var socketGeneration: Long = 0L
        private set

    @Volatile
    var bindingEpoch: Long = 0L
        private set

    @Volatile
    var lastRoomInfoVersion: Long = 0L
        private set

    @Volatile
    var lastDispatchedRoomVersion: Long = 0L
        private set

    @Volatile
    var latestRoomInfo: ProtocolRoomInfo? = null
        private set

    // 生命周期状态
    private val isClosed = AtomicBoolean(false)
    private val disconnectReasonRef = AtomicReference<String?>(null)

    override val closed: Boolean
        get() = isClosed.get()

    override val disconnectReason: String?
        get() = disconnectReasonRef.get()

    // 底层 NIO 与 I/O 状态 (R07: 每 Session 可复用 I/O 循环与非阻塞 SocketChannel)
    private var selector: Selector? = null
    private var socketChannel: SocketChannel? = null
    private var selectionKey: SelectionKey? = null
    private var ioThread: Thread? = null
    private val ioLoopRunning = AtomicBoolean(false)
    private val inboundWorkerQueue = LinkedBlockingQueue<Runnable>(256)
    private val overflowActions = ConcurrentLinkedQueue<() -> Unit>()
    private val maxOverflowActions = 256

    private fun drainOverflow() {
        while (true) {
            val action = overflowActions.poll() ?: break
            try {
                action()
            } catch (_: Throwable) {
            }
        }
    }

    private val workerExecutor: ThreadPoolExecutor = object : ThreadPoolExecutor(
        1, 1, 0L, TimeUnit.MILLISECONDS,
        inboundWorkerQueue,
        { r -> Thread(r, "TcpClient-Worker-${connectionCounter.getAndIncrement()}").apply { isDaemon = true } },
        ThreadPoolExecutor.AbortPolicy()
    ) {
        override fun afterExecute(r: Runnable?, t: Throwable?) {
            super.afterExecute(r, t)
            drainOverflow()
        }
    }

    // 受控有界的高优先级完成与断线通知通道 (Design §2 / REVIEW17 / REVIEW18)
    private val completionQueue = LinkedBlockingQueue<Runnable>(256)
    private val completionExecutor: ThreadPoolExecutor = object : ThreadPoolExecutor(
        1, 2, 60L, TimeUnit.SECONDS,
        completionQueue,
        { r -> Thread(r, "TcpClient-Completion-${connectionCounter.getAndIncrement()}").apply { isDaemon = true } },
        ThreadPoolExecutor.AbortPolicy()
    ) {
        override fun afterExecute(r: Runnable?, t: Throwable?) {
            super.afterExecute(r, t)
            drainOverflow()
        }
    }

    // 受控有界的连接与重连工作池，替代公共 ForkJoinPool (Design §2 / REVIEW18)
    private val connectExecutor: ExecutorService = ThreadPoolExecutor(
        1, 1, 0L, TimeUnit.MILLISECONDS,
        LinkedBlockingQueue<Runnable>(4),
        { r -> Thread(r, "TcpClient-Connect-${connectionCounter.getAndIncrement()}").apply { isDaemon = true } },
        ThreadPoolExecutor.AbortPolicy()
    )

    /**
     * 在受控有界工作资源中执行任务 (Design §2 / REVIEW17 / REVIEW18)
     * 保证 Future 完成与监听器通知绝不在 Selector I/O 线程同步执行，且不用公共无界线程池或无界临时线程
     */
    private fun executeOnWorker(action: () -> Unit) {
        if (Thread.currentThread() === ioThread) {
            // 在 Selector I/O 线程中：优先提交至 workerExecutor，严禁在 Selector 线程同步运行用户回调
            if (!workerExecutor.isShutdown) {
                try {
                    workerExecutor.execute {
                        try {
                            action()
                        } catch (_: Throwable) {
                        }
                    }
                    return
                } catch (_: RejectedExecutionException) {
                }
            }
            // 当 workerExecutor 满载被拒绝或已关闭时，转入受控有界的高优先级完成通道 (Design §2 / REVIEW18)
            // 严禁无界创建临时线程，亦严禁在 Selector 线程回退同步运行
            if (!completionExecutor.isShutdown) {
                try {
                    completionExecutor.execute {
                        try {
                            action()
                        } catch (_: Throwable) {
                        }
                    }
                    return
                } catch (_: RejectedExecutionException) {
                }
            }
            // 若两级有界工作队列均满载，有界保留在溢出缓冲区中等待排空，绝不丢弃终态回调，亦绝不无上限新建线程
            if (overflowActions.size < maxOverflowActions) {
                overflowActions.add(action)
            }
            return
        }

        // 不在 Selector I/O 线程中（包括 worker 线程、completion 线程或外部控制/测试线程）：
        // 直接在当前线程同步执行，保持调用顺序与测试栅栏同步语义，且绝对不在写锁内
        try {
            action()
        } catch (_: Throwable) {
        }
    }

    // 出站写队列 (R07: 256 帧 / 4 MiB 双限与慢写截止)
    private val outboundQueue = ArrayDeque<ByteBuffer>()
    private var currentWriteBuffer: ByteBuffer? = null
    private var queuedOutboundBytes: Long = 0L
    private val maxOutboundFrames: Int = 256
    private val maxOutboundBytes: Long = 4L * 1024 * 1024L
    private val lastWriteProgressTimeMillis = AtomicLong(0L)

    // 入站接收串行邮箱 (R07 / Design §4: 64 帧 / 4 MiB 双限与慢消费治理)
    private val inboundMailbox = ArrayDeque<TcpFrame>()
    private var currentDispatchFrame: TcpFrame? = null
    private var inboundMailboxBytes: Long = 0L
    private val maxInboundMailboxFrames: Int = 64
    private val maxInboundMailboxBytes: Long = 4L * 1024 * 1024L
    private val inboundMailboxLock = Any()
    private val inboundMailboxProcessing = AtomicBoolean(false)

    private var decoder: TcpFrameDecoder = TcpFrameDecoder()
    private val writeLock = Any()
    private val stateLock = Any()

    @Volatile
    private var pendingConnectFuture: CompletableFuture<Unit>? = null

    private val lastInboundValidTime = AtomicLong(0L)
    private var heartbeatCancellable: Cancellable? = null
    private var pendingWelcomeFuture: CompletableFuture<TcpWelcome>? = null

    // 在途请求治理 (契约 5 / UC-R10 / UC-R11)
    private class PendingRequest(
        val scope: String,
        val requestId: String,
        val request: ApiRequest,
        val isSideEffect: Boolean,
        @Volatile var future: CompletableFuture<ApiResponse>,
        var retriedExpiredScope: Boolean = false,
        val reconciliationFuture: CompletableFuture<ApiResponse> = CompletableFuture(),
        @Volatile var timeoutCancellable: Cancellable? = null
    )

    private val inFlightRequests = ConcurrentHashMap<String, PendingRequest>()
    private val unconfirmedSideEffects = ConcurrentHashMap<String, PendingRequest>()
    private val unconfirmedSideEffectsLock = Any()
    private val replayQueue = ConcurrentLinkedQueue<PendingRequest>()

    // 推送监听器与早期缓冲 (R09: 64条与4MiB双限)
    private val updateListeners = CopyOnWriteArrayList<(ProtocolPlayerUpdate) -> Unit>()
    private val publicUpdateListeners = CopyOnWriteArrayList<(ProtocolPublicUpdate) -> Unit>()
    private val roomUpdateListeners = CopyOnWriteArrayList<(ProtocolRoomInfo) -> Unit>()
    private val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()

    private val earlyPlayerUpdateBuffer = ConcurrentLinkedQueue<Pair<ProtocolPlayerUpdate, Long>>()
    private val earlyPublicUpdateBuffer = ConcurrentLinkedQueue<Pair<ProtocolPublicUpdate, Long>>()
    private val earlyRoomUpdateBuffer = ConcurrentLinkedQueue<Pair<ProtocolRoomInfo, Long>>()
    private val maxEarlyBufferSize = 64
    private val maxEarlyBufferBytes = 4 * 1024 * 1024L
    private val earlyBufferBytes = java.util.concurrent.atomic.AtomicLong(0L)
    @Volatile private var earlyBufferOverflow: Boolean = false
    @Volatile private var lastDispatchedPlayerCursor: Long = -1L
    @Volatile private var lastDispatchedPublicCursor: Long = -1L
    internal data class DispatchEntry<T>(
        val item: T,
        val epoch: Long,
        val wireBytes: Long
    )

    // 串行派发队列与门控 (R09: 128条与4MiB实际字节双限，有界串行派发)
    private val maxDispatchQueueSize = 128
    private val maxDispatchQueueBytes = 4L * 1024L * 1024L // 4 MiB
    private val playerDispatchQueue = ConcurrentLinkedQueue<DispatchEntry<ProtocolPlayerUpdate>>()
    private val playerDispatchQueueBytes = AtomicLong(0L)
    private val playerDraining = AtomicBoolean(false)
    private val publicDispatchQueue = ConcurrentLinkedQueue<DispatchEntry<ProtocolPublicUpdate>>()
    private val publicDispatchQueueBytes = AtomicLong(0L)
    private val publicDraining = AtomicBoolean(false)
    private val roomDispatchQueue = ConcurrentLinkedQueue<DispatchEntry<ProtocolRoomInfo>>()
    private val roomDispatchQueueBytes = AtomicLong(0L)
    private val roomDraining = AtomicBoolean(false)

    /** 玩家派发队列实际帧数 (R09) */
    val playerDispatchQueueSize: Int
        get() = playerDispatchQueue.size

    /** 玩家派发队列实际字节数 (R09) */
    val playerDispatchQueueByteCount: Long
        get() = playerDispatchQueueBytes.get()

    /** 公开派发队列实际帧数 (R09) */
    val publicDispatchQueueSize: Int
        get() = publicDispatchQueue.size

    /** 公开派发队列实际字节数 (R09) */
    val publicDispatchQueueByteCount: Long
        get() = publicDispatchQueueBytes.get()

    /** 房间派发队列实际帧数 (R09) */
    val roomDispatchQueueSize: Int
        get() = roomDispatchQueue.size

    /** 房间派发队列实际字节数 (R09) */
    val roomDispatchQueueByteCount: Long
        get() = roomDispatchQueueBytes.get()

    private val maxUnconfirmedSideEffects = 128


    /** 早期推送溢出状态 (R09) */
    val earlyBufferOverflowStatus: Boolean
        get() = earlyBufferOverflow

    /** 早期缓冲实际占用字节数 (R09) */
    val earlyBufferByteCount: Long
        get() = earlyBufferBytes.get()

    /** 早期玩家推送缓冲帧数 (R09) */
    val earlyPlayerBufferSize: Int
        get() = earlyPlayerUpdateBuffer.size

    /** 早期公开推送缓冲帧数 (R09) */
    val earlyPublicBufferSize: Int
        get() = earlyPublicUpdateBuffer.size

    /** 早期房间信息缓冲帧数 (R09) */
    val earlyRoomBufferSize: Int
        get() = earlyRoomUpdateBuffer.size

    /** 当前绑定代次 (R09) */
    val bindingEpochValue: Long
        get() = bindingEpoch

    /** 最近一次派发的玩家游标 (R09) */
    val lastDispatchedPlayerCursorValue: Long
        get() = lastDispatchedPlayerCursor

    /** 最近一次派发的公开游标 (R09) */
    val lastDispatchedPublicCursorValue: Long
        get() = lastDispatchedPublicCursor

    /** 最近一次派发的房间信息版本 (R09) */
    val lastDispatchedRoomVersionValue: Long
        get() = lastDispatchedRoomVersion

    /** 最近到达的房间信息版本 (R09) */
    val latestRoomInfoVersion: Long
        get() = synchronized(stateLock) { latestRoomInfo?.version ?: lastRoomInfoVersion }


    // 恢复状态管理 (R09)
    @Volatile private var playerRecoveryPending: Boolean = false
    @Volatile private var publicRecoveryPending: Boolean = false
    @Volatile private var roomRecoveryPending: Boolean = false
    private var playerRecoveryInFlight: CompletableFuture<ProtocolPlayerSnapshot>? = null
    private var publicRecoveryInFlight: CompletableFuture<ProtocolPublicSnapshot>? = null
    private var playerRecoveryRetryCancellable: Cancellable? = null
    private var publicRecoveryRetryCancellable: Cancellable? = null
    private var playerRecoveryRetryCount: Int = 0
    private var publicRecoveryRetryCount: Int = 0
    private val maxRecoveryRetries = 5

    /** 玩家全量恢复待办状态 (R09) */
    val isPlayerRecoveryPending: Boolean
        get() = playerRecoveryPending

    /** 观战全量恢复待办状态 (R09) */
    val isPublicRecoveryPending: Boolean
        get() = publicRecoveryPending

    /** 房间信息恢复待办状态 (R09) */
    val isRoomRecoveryPending: Boolean
        get() = roomRecoveryPending

    /**
     * 初始建连与握手 (由受控连接工作池执行, R07 / REVIEW18)
     */
    fun connectInitial(): CompletableFuture<TcpClientBinding> {
        val future = CompletableFuture<TcpClientBinding>()
        try {
            connectExecutor.execute {
                try {
                    connectInternal(lobbyResumeToken)
                    future.complete(this)
                } catch (t: Throwable) {
                    future.completeExceptionally(t)
                }
            }
        } catch (e: Throwable) {
            future.completeExceptionally(e)
        }
        return future
    }

    /**
     * 重连对接 (契约 3 / UC-S08 / REVIEW18): 跨重连复用绑定对象，由受控连接工作池重开 socket 并重新完成 HELLO/WELCOME
     */
    fun reconnect(): CompletableFuture<TcpClientBinding> {
        val future = CompletableFuture<TcpClientBinding>()
        try {
            connectExecutor.execute {
                try {
                    closeSocketQuietly(failInFlight = true)
                    this.socketGeneration++
                    connectInternal(lobbyResumeToken)
                    if (boundRoomId == null) {
                        replayPendingSideEffects()
                    }
                    future.complete(this)
                } catch (t: Throwable) {
                    future.completeExceptionally(t)
                }
            }
        } catch (e: Throwable) {
            future.completeExceptionally(e)
        }
        return future
    }

    /**
     * 确保客户端 Session 专属可复用 NIO I/O 循环已启动 (R07)
     */
    private fun ensureIoLoop() {
        synchronized(stateLock) {
            if (selector == null || selector?.isOpen != true) {
                selector = Selector.open()
            }
            if (ioThread == null || ioThread?.isAlive != true) {
                ioLoopRunning.set(true)
                val t = Thread({ runIoLoop() }, "TcpClient-IO-${connectionCounter.getAndIncrement()}")
                t.isDaemon = true
                ioThread = t
                t.start()
            }
        }
    }

    /**
     * 内部异步建连与 HELLO/WELCOME 握手 (由异步工作线程执行, R07)
     */
    private fun connectInternal(token: String?) {
        ensureIoLoop()

        val sc = SocketChannel.open()
        sc.configureBlocking(false)
        try {
            sc.setOption(StandardSocketOptions.TCP_NODELAY, true)
        } catch (_: Throwable) {
        }

        val connectFuture = CompletableFuture<Unit>()
        this.pendingConnectFuture = connectFuture

        val connectedImmediately = try {
            sc.connect(InetSocketAddress(host, port))
        } catch (e: Throwable) {
            try { sc.close() } catch (_: Throwable) {}
            throw e
        }

        val now = clock.currentTimeMillis()
        synchronized(stateLock) {
            this.socketChannel = sc
            this.decoder = TcpFrameDecoder()
            this.lastInboundValidTime.set(now)
            this.isClosed.set(false)
            this.disconnectReasonRef.set(null)
            this.lastWriteProgressTimeMillis.set(now)
        }

        synchronized(writeLock) {
            outboundQueue.clear()
            currentWriteBuffer = null
            queuedOutboundBytes = 0L
        }

        synchronized(inboundMailboxLock) {
            inboundMailbox.clear()
            currentDispatchFrame = null
            inboundMailboxBytes = 0L
        }
        inboundWorkerQueue.clear()

        val sel = selector ?: throw IOException("Selector not open")
        sel.wakeup()
        val ops = if (connectedImmediately) {
            connectFuture.complete(Unit)
            SelectionKey.OP_READ
        } else {
            SelectionKey.OP_CONNECT or SelectionKey.OP_READ
        }
        val key = sc.register(sel, ops)
        this.selectionKey = key
        sel.wakeup()

        // 等待底层 TCP 连接完成 (5 秒超时)
        try {
            connectFuture.get(5000L, TimeUnit.MILLISECONDS)
        } catch (e: Exception) {
            closeSocketQuietly(failInFlight = true)
            throw IOException("Connect timed out or failed: ${e.message}", e)
        }

        val welcomeFuture = CompletableFuture<TcpWelcome>()
        this.pendingWelcomeFuture = welcomeFuture

        // 1. 发送 HELLO 握手消息
        val hello = TcpHello(
            wireVersion = TcpWire.WIRE_VERSION,
            businessVersion = TcpWire.BUSINESS_VERSION,
            lobbyResumeToken = token
        )
        writeRawFrame(TcpMessageCodec.toFrame(hello))

        // 2. 等待 WELCOME (5 秒超时)
        val welcome = try {
            welcomeFuture.get(5000L, TimeUnit.MILLISECONDS)
        } catch (e: Exception) {
            closeSocketQuietly(failInFlight = true)
            throw IOException("Handshake failed or timed out", e)
        }

        if (welcome.wireVersion != TcpWire.WIRE_VERSION || welcome.businessVersion != TcpWire.BUSINESS_VERSION) {
            closeSocketQuietly(failInFlight = true)
            throw TcpProtocolException(
                TcpCloseReasons.VERSION_MISMATCH,
                "Incompatible server version: wire=${welcome.wireVersion}, business=${welcome.businessVersion}"
            )
        }

        this.lobbyResumeToken = welcome.lobbyResumeToken
        this.connectionEpoch = welcome.connectionEpoch
        this.limits = welcome.limits

        if (this.currentScope == null || boundRoomId == null) {
            this.currentScope = welcome.lobbyScope
        }

        // 3. 启动心跳探测与判死检查
        startHeartbeat()
    }

    /**
     * 启动心跳 PING 发送与判死时钟检查 (G / UC-L06 / UC-L07)
     */
    private fun startHeartbeat() {
        heartbeatCancellable?.cancel()
        val interval = limits.heartbeatIntervalMillis.coerceAtLeast(1000L)
        scheduleHeartbeatTick(interval)
    }

    private fun scheduleHeartbeatTick(interval: Long) {
        heartbeatCancellable = scheduler.schedule(interval) {
            if (closed || socketChannel == null || socketChannel?.isOpen != true) return@schedule

            val now = clock.currentTimeMillis()
            if (now - lastInboundValidTime.get() >= limits.idleDeadMillis) {
                // 10 秒无入站判死
                handleFailure(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Heartbeat dead timeout (10s idle)")
                return@schedule
            }

            try {
                val ping = TcpPing(nonce = TcpNonce.generate())
                writeRawFrame(TcpMessageCodec.toFrame(ping))
            } catch (_: Throwable) {
            }

            scheduleHeartbeatTick(interval)
        }
    }

    /**
     * Session 专属可复用 NIO Selector 事件循环 (R07)
     */
    private fun runIoLoop() {
        val readBuffer = ByteBuffer.allocate(64 * 1024)
        while (ioLoopRunning.get()) {
            val sel = selector ?: break
            if (!sel.isOpen) break
            try {
                sel.select(50)
                val keys = sel.selectedKeys()
                val it = keys.iterator()
                while (it.hasNext()) {
                    val key = it.next()
                    it.remove()
                    if (!key.isValid) continue

                    val sc = key.channel() as? SocketChannel ?: continue
                    val connGen = socketGeneration

                    if (key.isConnectable) {
                        try {
                            if (sc.finishConnect()) {
                                key.interestOps(key.interestOps() and SelectionKey.OP_CONNECT.inv() or SelectionKey.OP_READ)
                                val cf = pendingConnectFuture
                                if (cf != null) {
                                    executeOnWorker { cf.complete(Unit) }
                                }
                            }
                        } catch (e: Throwable) {
                            val cf = pendingConnectFuture
                            if (cf != null) {
                                executeOnWorker { cf.completeExceptionally(e) }
                            }
                            handleFailure(TcpCloseReasons.LOCAL_CLOSED, "Connect failed: ${e.message}")
                            continue
                        }
                    }

                    if (key.isValid && key.isReadable) {
                        handleRead(sc, readBuffer, connGen)
                    }

                    if (key.isValid && key.isWritable) {
                        handleWrite(key, sc)
                    }
                }

                checkStalledWrite()
            } catch (_: ClosedChannelException) {
                break
            } catch (_: ClosedSelectorException) {
                break
            } catch (e: Throwable) {
                if (!ioLoopRunning.get()) break
            }
        }
    }

    /**
     * 检查写出停滞 (连续 10 秒有待发送数据但零写出进展，判 SLOW_CONSUMER，R07 / Design §4)
     */
    internal fun checkStalledWrite(nowMillis: Long = clock.currentTimeMillis()) {
        val failureToHandle: Pair<String, String>? = synchronized(writeLock) {
            val sc = socketChannel ?: return
            if (!sc.isOpen) return
            val hasData = currentWriteBuffer?.hasRemaining() == true || outboundQueue.isNotEmpty()
            if (hasData) {
                val lastProgress = lastWriteProgressTimeMillis.get()
                if (nowMillis - lastProgress >= 10000L) {
                    TcpCloseReasons.SLOW_CONSUMER to "Stalled write progress for 10s"
                } else null
            } else {
                lastWriteProgressTimeMillis.set(nowMillis)
                null
            }
        }

        if (failureToHandle != null) {
            handleFailure(failureToHandle.first, failureToHandle.second)
        }
    }

    /**
     * 处理通道写出 (OP_WRITE: 保持部分写，受 64 KiB 轮转限额约束，R07 / Design §4)
     */
    internal fun handleWrite(key: SelectionKey, sc: SocketChannel) {
        var bytesWrittenThisTurn = 0
        val maxBytesPerRotation = 64 * 1024
        var writeFailure: Pair<String, String>? = null

        synchronized(writeLock) {
            while (sc.isOpen && bytesWrittenThisTurn < maxBytesPerRotation) {
                if (currentWriteBuffer == null || !currentWriteBuffer!!.hasRemaining()) {
                    val next = outboundQueue.poll()
                    if (next == null) {
                        currentWriteBuffer = null
                        break
                    }
                    currentWriteBuffer = next
                }

                val buf = currentWriteBuffer ?: break
                val remainingQuota = maxBytesPerRotation - bytesWrittenThisTurn
                val oldLimit = buf.limit()
                if (buf.remaining() > remainingQuota) {
                    buf.limit(buf.position() + remainingQuota)
                }

                val written = try {
                    sc.write(buf)
                } catch (e: Throwable) {
                    buf.limit(oldLimit)
                    writeFailure = TcpCloseReasons.LOCAL_CLOSED to "Write failure: ${e.message}"
                    break
                } finally {
                    buf.limit(oldLimit)
                }

                if (written > 0) {
                    bytesWrittenThisTurn += written
                    queuedOutboundBytes -= written
                    if (queuedOutboundBytes < 0L) queuedOutboundBytes = 0L
                    lastWriteProgressTimeMillis.set(clock.currentTimeMillis())
                }

                if (buf.hasRemaining()) {
                    // 部分写出：当前 buffer 仍有未写字节
                    // 若 written == 0 (Socket 缓冲区写满)，或已达到单轮 64 KiB 配额上限，退出本轮循环让出 Selector
                    break
                } else {
                    currentWriteBuffer = null
                }
            }

            if (writeFailure == null && currentWriteBuffer == null && outboundQueue.isEmpty()) {
                lastWriteProgressTimeMillis.set(clock.currentTimeMillis())
                try {
                    if (key.isValid) {
                        key.interestOps(key.interestOps() and SelectionKey.OP_WRITE.inv())
                    }
                } catch (_: Throwable) {
                }
            }
        }

        if (writeFailure != null) {
            handleFailure(writeFailure.first, writeFailure.second)
        }
    }

    /**
     * 处理通道读取与帧解码 (OP_READ: 隔离 JSON 解析与监听器，投递至有界入站邮箱，R07 / Design §4)
     */
    private fun handleRead(sc: SocketChannel, readBuffer: ByteBuffer, connGen: Long) {
        readBuffer.clear()
        val read = try {
            sc.read(readBuffer)
        } catch (e: Exception) {
            if (!closed && socketChannel === sc && socketGeneration == connGen) {
                handleFailure(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Read failure: ${e.message}")
            }
            return
        }

        if (read == -1) {
            if (!closed && socketChannel === sc && socketGeneration == connGen) {
                try {
                    decoder.onEndOfStream()
                } catch (e: TcpProtocolException) {
                    handleFailure(e.reasonCode, e.message)
                    return
                }
                handleFailure(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Server EOF")
            }
            return
        }

        if (read == 0) return

        readBuffer.flip()
        val decodedFrames: List<TcpFrame>
        try {
            decodedFrames = decoder.decode(readBuffer)
        } catch (e: TcpProtocolException) {
            if (socketGeneration == connGen) {
                handleFailure(e.reasonCode, e.message)
            }
            return
        } finally {
            readBuffer.clear()
        }

        if (decodedFrames.isNotEmpty() && socketGeneration == connGen) {
            // 仅收到完整合法入站帧才刷新判死时钟 (UC-L06 / UC-L07 零散字节不刷新)
            lastInboundValidTime.set(clock.currentTimeMillis())

            val framesToDispatch = ArrayList<TcpFrame>(decodedFrames.size)
            for (frame in decodedFrames) {
                if (frame.type == TcpWire.FRAME_TYPE_PING) {
                    // PING 帧在 I/O 线程立即响应 PONG，绝不受 worker 线程慢监听器阻塞 (R07 / R08)
                    val ping = try {
                        TcpMessageCodec.parsePing(frame)
                    } catch (e: Throwable) {
                        handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Malformed PING: ${e.message}")
                        return
                    }
                    val pong = TcpPong(nonce = ping.nonce)
                    try {
                        writeRawFrame(TcpMessageCodec.toFrame(pong))
                    } catch (_: Throwable) {
                    }
                } else {
                    framesToDispatch.add(frame)
                }
            }

            if (framesToDispatch.isNotEmpty()) {
                // 入站帧纳入 64 帧 / 4 MiB 串行接收邮箱双限治理 (Design §4 / R07)
                var saturated = false
                synchronized(inboundMailboxLock) {
                    for (frame in framesToDispatch) {
                        val frameBytes = frame.payload.size.toLong()
                        val heldFrames = inboundMailbox.size + (if (currentDispatchFrame != null) 1 else 0)
                        if (heldFrames >= maxInboundMailboxFrames || inboundMailboxBytes + frameBytes > maxInboundMailboxBytes) {
                            saturated = true
                            break
                        }
                        inboundMailbox.add(frame)
                        inboundMailboxBytes += frameBytes
                    }
                }

                if (saturated) {
                    handleFailure(TcpCloseReasons.SLOW_CONSUMER, "Inbound dispatch mailbox saturated (64 frames / 4 MiB limit)")
                    return
                }

                triggerInboundMailboxProcessing(connGen)
            }
        }
    }

    /**
     * 触发入站邮箱异步串行排空 (单任务门控调度，Design §4 / R07)
     */
    private fun triggerInboundMailboxProcessing(connGen: Long) {
        if (closed || socketGeneration != connGen) return
        if (inboundMailboxProcessing.compareAndSet(false, true)) {
            try {
                workerExecutor.execute {
                    drainInboundMailbox(connGen)
                }
            } catch (e: RejectedExecutionException) {
                inboundMailboxProcessing.set(false)
                handleFailure(TcpCloseReasons.SLOW_CONSUMER, "Inbound dispatch worker rejected execution")
            }
        }
    }

    /**
     * 串行排空入站邮箱中的帧并派发至业务/监听器 (在 workerExecutor 中执行，R07)
     */
    private fun drainInboundMailbox(connGen: Long) {
        try {
            while (true) {
                if (closed || socketGeneration != connGen) break
                val frame = synchronized(inboundMailboxLock) {
                    val f = inboundMailbox.poll()
                    if (f != null) {
                        currentDispatchFrame = f
                    }
                    f
                } ?: break

                try {
                    dispatchFrame(frame)
                } finally {
                    synchronized(inboundMailboxLock) {
                        inboundMailboxBytes -= frame.payload.size.toLong()
                        if (inboundMailboxBytes < 0L) inboundMailboxBytes = 0L
                        currentDispatchFrame = null
                    }
                }
            }
        } finally {
            inboundMailboxProcessing.set(false)
            val hasMore = synchronized(inboundMailboxLock) { inboundMailbox.isNotEmpty() }
            if (hasMore && !closed && socketGeneration == connGen) {
                triggerInboundMailboxProcessing(connGen)
            }
        }
    }

    /**
     * 入站帧分发派发 (R07 / R08)
     */
    private fun dispatchFrame(frame: TcpFrame) {
        when (frame.type) {
            TcpWire.FRAME_TYPE_WELCOME -> {
                val welcome = try {
                    TcpMessageCodec.parseWelcome(frame)
                } catch (t: Throwable) {
                    val f = pendingWelcomeFuture
                    executeOnWorker { f?.completeExceptionally(t) }
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Failed to parse WELCOME: ${t.message}")
                    return
                }
                if (welcome.wireVersion != TcpWire.WIRE_VERSION || welcome.businessVersion != TcpWire.BUSINESS_VERSION) {
                    val ex = TcpProtocolException(TcpCloseReasons.VERSION_MISMATCH, "Version mismatch: wire=${welcome.wireVersion}, business=${welcome.businessVersion}")
                    val f = pendingWelcomeFuture
                    executeOnWorker { f?.completeExceptionally(ex) }
                    handleFailure(TcpCloseReasons.VERSION_MISMATCH, ex.message)
                    return
                }
                val f = pendingWelcomeFuture
                if (f == null || f.isDone) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Unexpected or duplicate WELCOME")
                    return
                }
                executeOnWorker { f.complete(welcome) }
            }
            TcpWire.FRAME_TYPE_RSP -> {
                val rspEnvelope = try {
                    TcpMessageCodec.parseResponseEnvelope(frame)
                } catch (e: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Failed to parse RSP: ${e.message}")
                    return
                }
                try {
                    handleResponseEnvelope(rspEnvelope)
                } catch (e: TcpProtocolException) {
                    handleFailure(e.reasonCode, e.message)
                } catch (_: Throwable) {
                }
            }
            TcpWire.FRAME_TYPE_PLAYER_UPDATE -> {
                val update = try {
                    TcpMessageCodec.parsePlayerUpdate(frame)
                } catch (e: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Failed to parse PLAYER_UPDATE: ${e.message}")
                    return
                }
                try {
                    dispatchPlayerUpdate(update, frame.payload.size.toLong())
                } catch (_: Throwable) {
                }
            }
            TcpWire.FRAME_TYPE_PUBLIC_UPDATE -> {
                val update = try {
                    TcpMessageCodec.parsePublicUpdate(frame)
                } catch (e: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Failed to parse PUBLIC_UPDATE: ${e.message}")
                    return
                }
                try {
                    dispatchPublicUpdate(update, frame.payload.size.toLong())
                } catch (_: Throwable) {
                }
            }
            TcpWire.FRAME_TYPE_ROOM_UPDATE -> {
                val info = try {
                    TcpMessageCodec.parseRoomUpdate(frame)
                } catch (e: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Failed to parse ROOM_UPDATE: ${e.message}")
                    return
                }
                try {
                    dispatchRoomUpdate(info, frame.payload.size.toLong())
                } catch (_: Throwable) {
                }
            }
            TcpWire.FRAME_TYPE_PING -> {
                val ping = try {
                    TcpMessageCodec.parsePing(frame)
                } catch (e: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Malformed PING: ${e.message}")
                    return
                }
                val pong = TcpPong(nonce = ping.nonce)
                try {
                    writeRawFrame(TcpMessageCodec.toFrame(pong))
                } catch (_: Throwable) {
                }
            }
            TcpWire.FRAME_TYPE_PONG -> {
                try {
                    TcpMessageCodec.parsePong(frame)
                } catch (e: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Malformed PONG: ${e.message}")
                }
            }
            TcpWire.FRAME_TYPE_CLOSE -> {
                try {
                    val closeMsg = TcpMessageCodec.parseClose(frame)
                    handleFailure(closeMsg.reason, closeMsg.message)
                } catch (_: Throwable) {
                    handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Malformed CLOSE")
                }
            }
            else -> {
                handleFailure(TcpCloseReasons.PROTOCOL_VIOLATION, "Unexpected frame type: ${frame.type}")
            }
        }
    }

    /**
     * 处理 RSP 信封与作用域续期 (D3 / D7 / D9 / 契约 D / R05 / R08)
     */
    private fun handleResponseEnvelope(envelope: TcpResponseEnvelope) {
        val reqId = envelope.requestId
        val pending = synchronized(unconfirmedSideEffectsLock) {
            inFlightRequests.remove(reqId)
        } ?: unconfirmedSideEffects[reqId]
        if (pending == null) return
        pending.timeoutCancellable?.cancel()

        // 校验 requestScope 是否一致 (R08)
        if (envelope.requestScope != pending.scope) {
            val ex = TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Mismatched requestScope in RSP: expected '${pending.scope}', received '${envelope.requestScope}'"
            )
            pending.future.completeExceptionally(ex)
            if (!pending.isSideEffect) {
                synchronized(unconfirmedSideEffectsLock) {
                    unconfirmedSideEffects.remove(reqId)
                }
                pending.reconciliationFuture.completeExceptionally(ex)
            }
            // R04: 错误 requestScope 的非法 RSP 绝不擦除未知副作用身份与记录，原 (scope, requestId, payload) 完整保留供对账
            pumpReplayQueue()
            return
        }

        val response = envelope.response

        // 1. 采纳服务端新颁发的作用域凭据
        if (envelope.grantedScope != null) {
            this.currentScope = envelope.grantedScope
        }

        // 2. 检测 REQUEST_SCOPE_EXPIRED (R05)
        if (!response.success && response.error == ApiErrors.REQUEST_SCOPE_EXPIRED) {
            if (!pending.isSideEffect && !pending.retriedExpiredScope) {
                // 仅只读查询支持以新 scope 自动重试
                pending.retriedExpiredScope = true
                renewScopeAndRetry(pending)
                return
            }
            // 副作用严禁自动换新 scope 重做 (R05 / D3 / UC-R05): 必须异常完成，由业务层或对账流显式处理
            synchronized(unconfirmedSideEffectsLock) {
                unconfirmedSideEffects.remove(reqId)
            }
            val ex = ServerApiException(code = ApiErrors.REQUEST_SCOPE_EXPIRED, message = "Scope expired for side-effect request; reconciliation required")
            pending.future.completeExceptionally(ex)
            pending.reconciliationFuture.completeExceptionally(ex)
            pumpReplayQueue()
            return
        }

        // 3. 正常完成回执或失败
        if (response.success) {
            synchronized(unconfirmedSideEffectsLock) {
                unconfirmedSideEffects.remove(reqId)
            }
            pending.future.complete(response)
            pending.reconciliationFuture.complete(response)
        } else {
            synchronized(unconfirmedSideEffectsLock) {
                unconfirmedSideEffects.remove(reqId)
            }
            if (response.receipt != null) {
                // 业务拒绝携带合法 receipt
                pending.future.complete(response)
                pending.reconciliationFuture.complete(response)
            } else {
                val errCode = response.error ?: ApiErrors.INTERNAL_ERROR
                val ex = ServerApiException(code = errCode, message = "Server returned error: $errCode")
                pending.future.completeExceptionally(ex)
                pending.reconciliationFuture.completeExceptionally(ex)
            }
        }
        pumpReplayQueue()
    }

    /**
     * 作用域过期时发起 GET_REQUEST_SCOPE 权威查询并自动重试原只读请求 (R05)
     */
    private fun renewScopeAndRetry(pending: PendingRequest) {
        val scopeReq = ApiRequest(
            api = "GET_REQUEST_SCOPE",
            requestId = UUID.randomUUID().toString()
        )
        sendRawRequest(scopeReq).whenComplete { scopeRsp, scopeErr ->
            if (scopeErr != null || scopeRsp == null || !scopeRsp.response.success || scopeRsp.grantedScope == null) {
                synchronized(unconfirmedSideEffectsLock) {
                    unconfirmedSideEffects.remove(pending.requestId)
                }
                val ex = ServerApiException(code = ApiErrors.REQUEST_SCOPE_EXPIRED, message = "Scope expired and failed to renew")
                executeOnWorker {
                    pending.future.completeExceptionally(ex)
                    pending.reconciliationFuture.completeExceptionally(ex)
                }
                pumpReplayQueue()
            } else {
                val newScope = scopeRsp.grantedScope!!
                this.currentScope = newScope

                val retryEnvelope = TcpRequestEnvelope(
                    requestScope = newScope.scope,
                    requestId = pending.requestId, // 原 requestId
                    request = pending.request      // 原载荷
                )

                synchronized(unconfirmedSideEffectsLock) {
                    inFlightRequests[pending.requestId] = pending
                }
                val timeoutMillis = limits.requestTimeoutMillis.coerceAtLeast(50L)
                pending.timeoutCancellable = scheduler.schedule(timeoutMillis) {
                    val removed = synchronized(unconfirmedSideEffectsLock) {
                        inFlightRequests.remove(pending.requestId, pending)
                    }
                    if (removed) {
                        synchronized(unconfirmedSideEffectsLock) {
                            unconfirmedSideEffects.remove(pending.requestId)
                        }
                        val ex = TcpConnectionException("Retried request timed out after ${timeoutMillis}ms (api=${pending.request.api}, id=${pending.requestId})")
                        executeOnWorker {
                            pending.future.completeExceptionally(ex)
                            pending.reconciliationFuture.completeExceptionally(ex)
                        }
                        pumpReplayQueue()
                    }
                }
                try {
                    writeRawFrame(TcpMessageCodec.toFrame(retryEnvelope))
                } catch (t: Throwable) {
                    pending.timeoutCancellable?.cancel()
                    synchronized(unconfirmedSideEffectsLock) {
                        inFlightRequests.remove(pending.requestId)
                        unconfirmedSideEffects.remove(pending.requestId)
                    }
                    executeOnWorker {
                        pending.future.completeExceptionally(t)
                        pending.reconciliationFuture.completeExceptionally(t)
                    }
                    pumpReplayQueue()
                }
            }
        }
    }

    /**
     * 串行排空房间信息更新队列 (所有权明确的单 drainer 状态机)
     */
    private fun drainRoomQueue() {
        synchronized(stateLock) {
            if (!roomDraining.compareAndSet(false, true)) {
                return
            }
        }
        var drainOwner = true
        try {
            while (true) {
                val entry: DispatchEntry<ProtocolRoomInfo>?
                val listeners: List<(ProtocolRoomInfo) -> Unit>
                val currentEpoch: Long

                synchronized(stateLock) {
                    if (closed) {
                        roomDispatchQueue.clear()
                        roomDispatchQueueBytes.set(0L)
                        roomDraining.set(false)
                        drainOwner = false
                        return
                    }
                    listeners = roomUpdateListeners.toList()
                    if (listeners.isEmpty()) {
                        // 无监听器：不能把未交付消息计为已派发，有界保留在队列中等待重挂载后派发
                        roomDraining.set(false)
                        drainOwner = false
                        return
                    }
                    val head = roomDispatchQueue.poll()
                    if (head == null) {
                        // 队列已空，在 stateLock 内安全交出门控所有权
                        roomDraining.set(false)
                        drainOwner = false
                        return
                    }
                    val remainingBytes = roomDispatchQueueBytes.addAndGet(-head.wireBytes)
                    if (remainingBytes < 0L) roomDispatchQueueBytes.set(0L)
                    currentEpoch = bindingEpoch
                    if (currentEpoch != head.epoch || head.item.version <= lastDispatchedRoomVersion) {
                        entry = null
                    } else {
                        lastDispatchedRoomVersion = head.item.version
                        entry = head
                    }
                }

                if (entry != null) {
                    val item = entry.item
                    for (l in listeners) {
                        if (closed || bindingEpoch != currentEpoch) break
                        try {
                            l(item)
                        } catch (_: Throwable) {
                        }
                    }
                }
            }
        } finally {
            if (drainOwner) {
                synchronized(stateLock) {
                    roomDraining.set(false)
                }
            }
        }
    }

    /**
     * 串行排空公开流更新队列 (所有权明确的单 drainer 状态机)
     */
    private fun drainPublicQueue() {
        synchronized(stateLock) {
            if (!publicDraining.compareAndSet(false, true)) {
                return
            }
        }
        var drainOwner = true
        try {
            while (true) {
                val entry: DispatchEntry<ProtocolPublicUpdate>?
                val listeners: List<(ProtocolPublicUpdate) -> Unit>
                val currentEpoch: Long

                synchronized(stateLock) {
                    if (closed) {
                        publicDispatchQueue.clear()
                        publicDispatchQueueBytes.set(0L)
                        publicDraining.set(false)
                        drainOwner = false
                        return
                    }
                    listeners = publicUpdateListeners.toList()
                    if (listeners.isEmpty()) {
                        // 无监听器：不能把未交付消息计为已派发，有界保留在队列中等待重挂载后派发
                        publicDraining.set(false)
                        drainOwner = false
                        return
                    }
                    val head = publicDispatchQueue.poll()
                    if (head == null) {
                        // 队列已空，在 stateLock 内安全交出门控所有权
                        publicDraining.set(false)
                        drainOwner = false
                        return
                    }
                    val remainingBytes = publicDispatchQueueBytes.addAndGet(-head.wireBytes)
                    if (remainingBytes < 0L) publicDispatchQueueBytes.set(0L)
                    currentEpoch = bindingEpoch
                    if (currentEpoch != head.epoch || head.item.snapshot.cursor < lastDispatchedPublicCursor) {
                        entry = null
                    } else {
                        lastDispatchedPublicCursor = head.item.snapshot.cursor
                        entry = head
                    }
                }

                if (entry != null) {
                    val item = entry.item
                    for (l in listeners) {
                        if (closed || bindingEpoch != currentEpoch) break
                        try {
                            l(item)
                        } catch (_: Throwable) {
                        }
                    }
                }
            }
        } finally {
            if (drainOwner) {
                synchronized(stateLock) {
                    publicDraining.set(false)
                }
            }
        }
    }

    /**
     * 串行排空玩家私有流更新队列 (所有权明确的单 drainer 状态机)
     */
    private fun drainPlayerQueue() {
        synchronized(stateLock) {
            if (!playerDraining.compareAndSet(false, true)) {
                return
            }
        }
        var drainOwner = true
        try {
            while (true) {
                val entry: DispatchEntry<ProtocolPlayerUpdate>?
                val listeners: List<(ProtocolPlayerUpdate) -> Unit>
                val currentEpoch: Long

                synchronized(stateLock) {
                    if (closed) {
                        playerDispatchQueue.clear()
                        playerDispatchQueueBytes.set(0L)
                        playerDraining.set(false)
                        drainOwner = false
                        return
                    }
                    listeners = updateListeners.toList()
                    if (listeners.isEmpty()) {
                        // 无监听器：不能把未交付消息计为已派发，有界保留在队列中等待重挂载后派发
                        playerDraining.set(false)
                        drainOwner = false
                        return
                    }
                    val head = playerDispatchQueue.poll()
                    if (head == null) {
                        // 队列已空，在 stateLock 内安全交出门控所有权
                        playerDraining.set(false)
                        drainOwner = false
                        return
                    }
                    val remainingBytes = playerDispatchQueueBytes.addAndGet(-head.wireBytes)
                    if (remainingBytes < 0L) playerDispatchQueueBytes.set(0L)
                    currentEpoch = bindingEpoch
                    if (currentEpoch != head.epoch || head.item.snapshot.cursor < lastDispatchedPlayerCursor) {
                        entry = null
                    } else {
                        lastDispatchedPlayerCursor = head.item.snapshot.cursor
                        entry = head
                    }
                }

                if (entry != null) {
                    val item = entry.item
                    for (l in listeners) {
                        if (closed || bindingEpoch != currentEpoch) break
                        try {
                            l(item)
                        } catch (_: Throwable) {
                        }
                    }
                }
            }
        } finally {
            if (drainOwner) {
                synchronized(stateLock) {
                    playerDraining.set(false)
                }
            }
        }
    }

    /**
     * 派发玩家私有流更新 (R09: 64条/4MiB双限与代次检查，有界串行派发)
     */
    internal fun dispatchPlayerUpdate(update: ProtocolPlayerUpdate, wireBytes: Long = -1L) {
        val capturedEpoch: Long
        val shouldTriggerRecovery: Boolean
        val actualBytes = if (wireBytes > 0L) wireBytes else TcpMessageCodec.toPlayerUpdateFrame(update).payload.size.toLong()
        synchronized(stateLock) {
            capturedEpoch = bindingEpoch
            if (updateListeners.isEmpty()) {
                val totalEarlyCount = earlyPlayerUpdateBuffer.size + earlyPublicUpdateBuffer.size + earlyRoomUpdateBuffer.size
                if (totalEarlyCount < maxEarlyBufferSize && earlyBufferBytes.get() + actualBytes <= maxEarlyBufferBytes) {
                    earlyPlayerUpdateBuffer.add(update to actualBytes)
                    earlyBufferBytes.addAndGet(actualBytes)
                } else {
                    earlyBufferOverflow = true
                    if (boundSeat != null) {
                        playerRecoveryPending = true
                    }
                }
                return
            }

            if (playerDispatchQueue.size < maxDispatchQueueSize && playerDispatchQueueBytes.get() + actualBytes <= maxDispatchQueueBytes) {
                playerDispatchQueue.add(DispatchEntry(update, capturedEpoch, actualBytes))
                playerDispatchQueueBytes.addAndGet(actualBytes)
            } else {
                earlyBufferOverflow = true
                if (boundSeat != null) {
                    playerRecoveryPending = true
                }
            }
            shouldTriggerRecovery = playerRecoveryPending && boundSeat != null
        }

        drainPlayerQueue()

        if (shouldTriggerRecovery) {
            triggerPlayerRecovery(capturedEpoch)
        }
    }

    /**
     * 派发公开流更新 (R09: 64条/4MiB双限与代次检查，有界串行派发)
     */
    internal fun dispatchPublicUpdate(update: ProtocolPublicUpdate, wireBytes: Long = -1L) {
        val capturedEpoch: Long
        val shouldTriggerRecovery: Boolean
        val actualBytes = if (wireBytes > 0L) wireBytes else TcpMessageCodec.toPublicUpdateFrame(update).payload.size.toLong()
        synchronized(stateLock) {
            capturedEpoch = bindingEpoch
            if (publicUpdateListeners.isEmpty()) {
                val totalEarlyCount = earlyPlayerUpdateBuffer.size + earlyPublicUpdateBuffer.size + earlyRoomUpdateBuffer.size
                if (totalEarlyCount < maxEarlyBufferSize && earlyBufferBytes.get() + actualBytes <= maxEarlyBufferBytes) {
                    earlyPublicUpdateBuffer.add(update to actualBytes)
                    earlyBufferBytes.addAndGet(actualBytes)
                } else {
                    earlyBufferOverflow = true
                    if (boundSpectatorId != null) {
                        publicRecoveryPending = true
                    }
                }
                return
            }

            if (publicDispatchQueue.size < maxDispatchQueueSize && publicDispatchQueueBytes.get() + actualBytes <= maxDispatchQueueBytes) {
                publicDispatchQueue.add(DispatchEntry(update, capturedEpoch, actualBytes))
                publicDispatchQueueBytes.addAndGet(actualBytes)
            } else {
                earlyBufferOverflow = true
                if (boundSpectatorId != null) {
                    publicRecoveryPending = true
                }
            }
            shouldTriggerRecovery = publicRecoveryPending && boundSpectatorId != null
        }

        drainPublicQueue()

        if (shouldTriggerRecovery) {
            triggerPublicRecovery(capturedEpoch)
        }
    }

    /**
     * 派发房间信息更新 (R09: 64条/4MiB双限与代次检查，有界串行派发)
     */
    internal fun dispatchRoomUpdate(info: ProtocolRoomInfo, wireBytes: Long = -1L) {
        val capturedEpoch: Long
        val shouldTriggerRecovery: Boolean
        val actualBytes = if (wireBytes > 0L) wireBytes else TcpMessageCodec.toRoomUpdateFrame(info).payload.size.toLong()
        synchronized(stateLock) {
            capturedEpoch = bindingEpoch
            if (info.version > lastRoomInfoVersion) {
                lastRoomInfoVersion = info.version
                latestRoomInfo = info
            }

            if (roomUpdateListeners.isEmpty()) {
                val totalEarlyCount = earlyPlayerUpdateBuffer.size + earlyPublicUpdateBuffer.size + earlyRoomUpdateBuffer.size
                if (totalEarlyCount < maxEarlyBufferSize && earlyBufferBytes.get() + actualBytes <= maxEarlyBufferBytes) {
                    earlyRoomUpdateBuffer.add(info to actualBytes)
                    earlyBufferBytes.addAndGet(actualBytes)
                } else {
                    earlyBufferOverflow = true
                    roomRecoveryPending = true
                }
                return
            }

            if (roomDispatchQueue.size < maxDispatchQueueSize && roomDispatchQueueBytes.get() + actualBytes <= maxDispatchQueueBytes) {
                roomDispatchQueue.add(DispatchEntry(info, capturedEpoch, actualBytes))
                roomDispatchQueueBytes.addAndGet(actualBytes)
            } else {
                earlyBufferOverflow = true
                roomRecoveryPending = true
            }
            shouldTriggerRecovery = (playerRecoveryPending && boundSeat != null && updateListeners.isNotEmpty()) ||
                (publicRecoveryPending && boundSpectatorId != null && publicUpdateListeners.isNotEmpty())
        }

        drainRoomQueue()

        if (shouldTriggerRecovery) {
            if (boundSeat != null) triggerPlayerRecovery(capturedEpoch)
            else if (boundSpectatorId != null) triggerPublicRecovery(capturedEpoch)
        }
    }

    /**
     * 触发玩家合并全量恢复 (R09: 支持合并在途查询、保留待办状态与有界重试，受代次约束)
     */
    private fun triggerPlayerRecovery(targetEpoch: Long) {
        val queryFuture: CompletableFuture<ProtocolPlayerSnapshot>
        synchronized(stateLock) {
            if (closed || bindingEpoch != targetEpoch || boundSeat == null) return
            playerRecoveryPending = true

            val existing = playerRecoveryInFlight
            if (existing != null && !existing.isDone) {
                return
            }
            queryFuture = snapshot()
            playerRecoveryInFlight = queryFuture
        }

        queryFuture.whenComplete { snap, err ->
            val capturedEpoch: Long
            val listeners: List<(ProtocolPlayerUpdate) -> Unit>
            val isSuccess: Boolean
            var failureToHandle: Pair<String, String>? = null
            var retryDelayToSchedule: Long? = null

            synchronized(stateLock) {
                capturedEpoch = bindingEpoch
                if (playerRecoveryInFlight === queryFuture) {
                    playerRecoveryInFlight = null
                }
                if (closed || bindingEpoch != targetEpoch) return@whenComplete

                if (err != null || snap == null) {
                    isSuccess = false
                    if (playerRecoveryRetryCount < maxRecoveryRetries) {
                        playerRecoveryPending = true
                        playerRecoveryRetryCount++
                        val delay = (50L * (1 shl (playerRecoveryRetryCount - 1))).coerceAtMost(500L)
                        retryDelayToSchedule = delay
                    } else {
                        // 有界重试耗尽：转入可观察的断线重连/失败状态，绝不静默停滞
                        resetRecoveryState()
                        earlyBufferOverflow = false
                        failureToHandle = TcpCloseReasons.HEARTBEAT_TIMEOUT to
                            "Player snapshot recovery failed after $maxRecoveryRetries retries: ${err?.message ?: "empty snapshot"}"
                    }
                    listeners = emptyList()
                } else {
                    val recoveryUpdate = ProtocolPlayerUpdate(
                        snapshot = snap,
                        fromCursor = snap.cursor,
                        visibleEvents = emptyList<ProtocolGameEvent>(),
                        isFullSnapshot = true
                    )
                    val wireBytes = TcpMessageCodec.toPlayerUpdateFrame(recoveryUpdate, isFullSnapshot = true).payload.size.toLong()
                    val enqueued = if (!closed && bindingEpoch == capturedEpoch) {
                        if (playerDispatchQueue.size < maxDispatchQueueSize && playerDispatchQueueBytes.get() + wireBytes <= maxDispatchQueueBytes) {
                            playerDispatchQueue.add(DispatchEntry(recoveryUpdate, capturedEpoch, wireBytes))
                            playerDispatchQueueBytes.addAndGet(wireBytes)
                            true
                        } else {
                            false
                        }
                    } else {
                        false
                    }

                    if (enqueued) {
                        isSuccess = true
                        playerRecoveryRetryCount = 0
                        playerRecoveryRetryCancellable?.cancel()
                        playerRecoveryRetryCancellable = null
                        playerRecoveryPending = false
                        if (!publicRecoveryPending && !roomRecoveryPending) {
                            earlyBufferOverflow = false
                        }
                    } else {
                        // 队列满或字节超限导致恢复基线未能入队：必须保留待办状态，绝不静默丢弃基线！
                        isSuccess = false
                        if (playerRecoveryRetryCount < maxRecoveryRetries) {
                            playerRecoveryPending = true
                            playerRecoveryRetryCount++
                            val delay = (50L * (1 shl (playerRecoveryRetryCount - 1))).coerceAtMost(500L)
                            retryDelayToSchedule = delay
                        } else {
                            resetRecoveryState()
                            earlyBufferOverflow = false
                            failureToHandle = TcpCloseReasons.HEARTBEAT_TIMEOUT to
                                "Player snapshot recovery baseline admission failed after $maxRecoveryRetries retries (queue saturated)"
                        }
                    }
                    listeners = emptyList()
                }
            }

            if (retryDelayToSchedule != null) {
                playerRecoveryRetryCancellable?.cancel()
                playerRecoveryRetryCancellable = scheduler.schedule(retryDelayToSchedule) {
                    triggerPlayerRecovery(targetEpoch)
                }
                return@whenComplete
            }

            if (failureToHandle != null) {
                handleFailure(failureToHandle.first, failureToHandle.second)
                return@whenComplete
            }

            if (isSuccess && snap != null) {
                drainPlayerQueue()
            }
        }
    }

    /**
     * 触发观战合并全量恢复 (R09: 支持合并在途查询、保留待办状态与有界重试，受代次约束)
     */
    private fun triggerPublicRecovery(targetEpoch: Long) {
        val queryFuture: CompletableFuture<ProtocolPublicSnapshot>
        synchronized(stateLock) {
            if (closed || bindingEpoch != targetEpoch || boundSpectatorId == null) return
            publicRecoveryPending = true

            val existing = publicRecoveryInFlight
            if (existing != null && !existing.isDone) {
                return
            }
            queryFuture = publicSnapshot()
            publicRecoveryInFlight = queryFuture
        }

        queryFuture.whenComplete { snap, err ->
            val capturedEpoch: Long
            val listeners: List<(ProtocolPublicUpdate) -> Unit>
            val isSuccess: Boolean
            var failureToHandle: Pair<String, String>? = null
            var retryDelayToSchedule: Long? = null

            synchronized(stateLock) {
                capturedEpoch = bindingEpoch
                if (publicRecoveryInFlight === queryFuture) {
                    publicRecoveryInFlight = null
                }
                if (closed || bindingEpoch != targetEpoch) return@whenComplete

                if (err != null || snap == null) {
                    isSuccess = false
                    if (publicRecoveryRetryCount < maxRecoveryRetries) {
                        publicRecoveryPending = true
                        publicRecoveryRetryCount++
                        val delay = (50L * (1 shl (publicRecoveryRetryCount - 1))).coerceAtMost(500L)
                        retryDelayToSchedule = delay
                    } else {
                        // 有界重试耗尽：转入可观察的断线重连/失败状态，绝不静默停滞
                        resetRecoveryState()
                        earlyBufferOverflow = false
                        failureToHandle = TcpCloseReasons.HEARTBEAT_TIMEOUT to
                            "Public snapshot recovery failed after $maxRecoveryRetries retries: ${err?.message ?: "empty snapshot"}"
                    }
                    listeners = emptyList()
                } else {
                    val recoveryUpdate = ProtocolPublicUpdate(
                        snapshot = snap,
                        fromCursor = snap.cursor,
                        visibleEvents = emptyList<ProtocolGameEvent>(),
                        isFullSnapshot = true
                    )
                    val wireBytes = TcpMessageCodec.toPublicUpdateFrame(recoveryUpdate).payload.size.toLong()
                    val enqueued = if (!closed && bindingEpoch == capturedEpoch) {
                        if (publicDispatchQueue.size < maxDispatchQueueSize && publicDispatchQueueBytes.get() + wireBytes <= maxDispatchQueueBytes) {
                            publicDispatchQueue.add(DispatchEntry(recoveryUpdate, capturedEpoch, wireBytes))
                            publicDispatchQueueBytes.addAndGet(wireBytes)
                            true
                        } else {
                            false
                        }
                    } else {
                        false
                    }

                    if (enqueued) {
                        isSuccess = true
                        publicRecoveryRetryCount = 0
                        publicRecoveryRetryCancellable?.cancel()
                        publicRecoveryRetryCancellable = null
                        publicRecoveryPending = false
                        if (!playerRecoveryPending && !roomRecoveryPending) {
                            earlyBufferOverflow = false
                        }
                    } else {
                        // 队列满或字节超限导致恢复基线未能入队：必须保留待办状态，绝不静默丢弃基线！
                        isSuccess = false
                        if (publicRecoveryRetryCount < maxRecoveryRetries) {
                            publicRecoveryPending = true
                            publicRecoveryRetryCount++
                            val delay = (50L * (1 shl (publicRecoveryRetryCount - 1))).coerceAtMost(500L)
                            retryDelayToSchedule = delay
                        } else {
                            resetRecoveryState()
                            earlyBufferOverflow = false
                            failureToHandle = TcpCloseReasons.HEARTBEAT_TIMEOUT to
                                "Public snapshot recovery baseline admission failed after $maxRecoveryRetries retries (queue saturated)"
                        }
                    }
                    listeners = emptyList()
                }
            }

            if (retryDelayToSchedule != null) {
                publicRecoveryRetryCancellable?.cancel()
                publicRecoveryRetryCancellable = scheduler.schedule(retryDelayToSchedule) {
                    triggerPublicRecovery(targetEpoch)
                }
                return@whenComplete
            }

            if (failureToHandle != null) {
                handleFailure(failureToHandle.first, failureToHandle.second)
                return@whenComplete
            }

            if (isSuccess && snap != null) {
                drainPublicQueue()
            }
        }
    }

    /**
     * 重置全量恢复待办与重试状态
     */
    private fun resetRecoveryState() {
        playerRecoveryPending = false
        publicRecoveryPending = false
        roomRecoveryPending = false
        playerRecoveryRetryCancellable?.cancel()
        playerRecoveryRetryCancellable = null
        publicRecoveryRetryCancellable?.cancel()
        publicRecoveryRetryCancellable = null
        playerRecoveryRetryCount = 0
        publicRecoveryRetryCount = 0
        playerRecoveryInFlight = null
        publicRecoveryInFlight = null
    }

    /**
     * 发送原始二进制帧字节 (非阻塞排入写队列并激活 OP_WRITE，R07)
     */
    private fun writeRawFrame(frame: TcpFrame) {
        val bytes = TcpFrameEncoder.encode(frame.type, frame.payload, frame.flags)
        synchronized(writeLock) {
            val sc = socketChannel
            if (closed || sc == null || !sc.isOpen) {
                throw IOException("Not connected to server")
            }
            val heldFrames = outboundQueue.size + (if (currentWriteBuffer != null) 1 else 0)
            if (heldFrames >= maxOutboundFrames || queuedOutboundBytes + bytes.size > maxOutboundBytes) {
                throw IOException("Outbound write queue saturated (frames=$heldFrames, bytes=$queuedOutboundBytes)")
            }
            if (currentWriteBuffer == null && outboundQueue.isEmpty()) {
                lastWriteProgressTimeMillis.set(clock.currentTimeMillis())
            }
            outboundQueue.add(ByteBuffer.wrap(bytes))
            queuedOutboundBytes += bytes.size

            val key = selectionKey
            if (key != null && key.isValid) {
                try {
                    key.interestOps(key.interestOps() or SelectionKey.OP_WRITE)
                    selector?.wakeup()
                } catch (_: Throwable) {
                }
            }
        }
    }

    /**
     * 内部发送低级请求 (带超时关联与 inFlightRequests 登记，R04 / R08)
     */
    private fun sendRawRequest(request: ApiRequest): CompletableFuture<TcpResponseEnvelope> {
        val future = CompletableFuture<TcpResponseEnvelope>()
        if (closed) {
            future.completeExceptionally(IOException("TcpClientBinding is closed: $disconnectReason"))
            return future
        }

        val isSideEffect = isSideEffectApi(request.api)
        val activeScope: String
        val pending: PendingRequest
        val timeoutMillis: Long

        synchronized(unconfirmedSideEffectsLock) {
            if (inFlightRequests.size >= limits.maxInFlightRequests) {
                future.completeExceptionally(ServerApiException(code = ApiErrors.SERVER_BUSY, message = "In-flight request capacity reached"))
                return future
            }

            if (isSideEffect && unconfirmedSideEffects.size >= maxUnconfirmedSideEffects) {
                future.completeExceptionally(ServerApiException(code = ApiErrors.SERVER_BUSY, message = "Unconfirmed side-effects capacity reached"))
                return future
            }

            if (inFlightRequests.containsKey(request.requestId)) {
                future.completeExceptionally(ServerApiException(code = ApiErrors.CONFLICT, message = "Duplicate in-flight requestId: ${request.requestId}"))
                return future
            }

            if (isSideEffect && unconfirmedSideEffects.containsKey(request.requestId)) {
                future.completeExceptionally(ServerApiException(code = ApiErrors.CONFLICT, message = "Duplicate unconfirmed requestId: ${request.requestId}"))
                return future
            }

            activeScope = currentScope?.scope ?: run {
                future.completeExceptionally(IllegalStateException("No active requestScope available"))
                return future
            }

            val respFuture = CompletableFuture<ApiResponse>()
            pending = PendingRequest(
                scope = activeScope,
                requestId = request.requestId,
                request = request,
                isSideEffect = isSideEffect,
                future = respFuture
            )

            inFlightRequests[request.requestId] = pending
            if (isSideEffect) {
                unconfirmedSideEffects[request.requestId] = pending
            }
            timeoutMillis = limits.requestTimeoutMillis.coerceAtLeast(50L)
        }

        val envelope = TcpRequestEnvelope(
            requestScope = activeScope,
            requestId = request.requestId,
            request = request
        )
        envelope.validateRequestIdConsistency()

        // 安排协商请求超时 (R04)
        val timeoutCancellable = scheduler.schedule(timeoutMillis) {
            val removed = synchronized(unconfirmedSideEffectsLock) {
                inFlightRequests.remove(request.requestId, pending)
            }
            if (removed) {
                val ex = TcpConnectionException("Request timed out after ${timeoutMillis}ms (api=${request.api}, id=${request.requestId})")
                executeOnWorker {
                    pending.future.completeExceptionally(ex)
                    if (!isSideEffect) {
                        pending.reconciliationFuture.completeExceptionally(ex)
                    }
                }
                if (!isSideEffect) {
                    synchronized(unconfirmedSideEffectsLock) {
                        unconfirmedSideEffects.remove(request.requestId)
                    }
                }
                // R04: 副作用在超时等未知结果时保留 unconfirmedSideEffects 与 reconciliationFuture，
                // 供重连后自动重放与显式对账
                pumpReplayQueue()
            }
        }
        pending.timeoutCancellable = timeoutCancellable

        pending.future.whenComplete { resp, err ->
            timeoutCancellable.cancel()
            if (err != null) {
                future.completeExceptionally(err)
            } else if (resp != null) {
                future.complete(TcpResponseEnvelope(requestScope = activeScope, requestId = request.requestId, response = resp, grantedScope = currentScope))
            }
        }

        try {
            writeRawFrame(TcpMessageCodec.toFrame(envelope))
        } catch (t: Throwable) {
            timeoutCancellable.cancel()
            synchronized(unconfirmedSideEffectsLock) {
                inFlightRequests.remove(request.requestId, pending)
                if (!isSideEffect) {
                    unconfirmedSideEffects.remove(request.requestId)
                }
            }
            executeOnWorker {
                if (!isSideEffect) {
                    pending.reconciliationFuture.completeExceptionally(t)
                }
                // R04: 写失败时（可能部分字节已送达网卡/对端），副作用保留在 unconfirmedSideEffects 供重连对账
                pending.future.completeExceptionally(t)
            }
            pumpReplayQueue()
        }

        return future
    }

    /**
     * 业务请求通用发送辅助
     */
    private fun <T> sendRequest(
        request: ApiRequest,
        responseMapper: (ApiResponse, TcpResponseEnvelope) -> T
    ): CompletableFuture<T> {
        val future = CompletableFuture<T>()
        sendRawRequest(request).whenComplete { env, err ->
            if (err != null) {
                future.completeExceptionally(err)
            } else if (env != null) {
                try {
                    val result = responseMapper(env.response, env)
                    future.complete(result)
                } catch (t: Throwable) {
                    future.completeExceptionally(t)
                }
            }
        }
        return future
    }

    /**
     * 连接异常或收到服务端断连通知处理
     */
    private fun handleFailure(reason: String, message: String? = null) {
        if (!disconnectReasonRef.compareAndSet(null, reason)) {
            // 已设置断连原因，避免重复处理与重复触发断连通知
            return
        }

        // 1. 在途 Future 全部以结构化未知结果异常失败 (UC-R10 / 契约 5)
        val ex = IOException("Connection lost ($reason): ${message ?: reason}")
        val inFlightList = synchronized(unconfirmedSideEffectsLock) {
            replayQueue.clear()
            val list = inFlightRequests.values.toList()
            inFlightRequests.clear()
            for (req in list) {
                req.timeoutCancellable?.cancel()
                if (!req.isSideEffect) {
                    unconfirmedSideEffects.remove(req.requestId)
                }
            }
            list
        }

        // 2. 安静关闭当前 socket
        closeSocketQuietly(failInFlight = false)

        // 3. 检查是否属于永久错误
        val isPermanent = when (reason) {
            TcpCloseReasons.VERSION_MISMATCH,
            TcpCloseReasons.PROTOCOL_VIOLATION,
            TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION,
            TcpCloseReasons.ROOM_CLOSED,
            TcpCloseReasons.SERVER_SHUTDOWN,
            TcpCloseReasons.LOCAL_CLOSED,
            ApiProtocol.DISPLACED_BY_NEW_CONNECTION,
            ApiProtocol.ROOM_CLOSED,
            ApiProtocol.SERVER_SHUTDOWN,
            ApiProtocol.LOCAL_CLOSED -> true
            else -> false
        }

        val sideEffectsList = if (isPermanent) {
            isClosed.set(true)
            synchronized(unconfirmedSideEffectsLock) {
                val list = unconfirmedSideEffects.values.toList()
                unconfirmedSideEffects.clear()
                for (req in list) {
                    req.timeoutCancellable?.cancel()
                }
                list
            }
        } else {
            emptyList()
        }
        val permEx = if (isPermanent) TcpConnectionException("Session terminated permanently: $reason") else null

        val connectFutureToFail = pendingConnectFuture?.takeIf { !it.isDone }
        val welcomeFutureToFail = pendingWelcomeFuture?.takeIf { !it.isDone }
        val listeners = disconnectListeners.toList()

        // 4. Future 完成及 disconnect 通知统一由有界串行工作资源执行 (Design §2 / R07)
        // 绝不在 Selector I/O 线程同步运行用户回调，慢用户回调不拖累 Selector 读写与心跳
        executeOnWorker {
            try { connectFutureToFail?.completeExceptionally(ex) } catch (_: Throwable) {}
            try { welcomeFutureToFail?.completeExceptionally(ex) } catch (_: Throwable) {}

            for (req in inFlightList) {
                try {
                    req.future.completeExceptionally(ex)
                } catch (_: Throwable) {
                }
                if (!req.isSideEffect) {
                    try {
                        req.reconciliationFuture.completeExceptionally(ex)
                    } catch (_: Throwable) {
                    }
                }
            }

            if (permEx != null) {
                for (req in sideEffectsList) {
                    try {
                        req.reconciliationFuture.completeExceptionally(permEx)
                    } catch (_: Throwable) {
                    }
                }
            }

            for (listener in listeners) {
                try {
                    listener(reason)
                } catch (_: Throwable) {
                }
            }
        }
    }

    private fun notifyDisconnect(reason: String) {
        val listeners = disconnectListeners.toList()
        if (listeners.isEmpty()) return
        executeOnWorker {
            for (listener in listeners) {
                try {
                    listener(reason)
                } catch (_: Throwable) {
                }
            }
        }
    }

    /**
     * 静默关闭 SocketChannel 与清除写队列 (R07)
     */
    private fun closeSocketQuietly(failInFlight: Boolean) {
        heartbeatCancellable?.cancel()
        heartbeatCancellable = null

        val inFlightList = synchronized(unconfirmedSideEffectsLock) {
            replayQueue.clear()
            val list = inFlightRequests.values.toList()
            inFlightRequests.clear()
            list
        }
        if (failInFlight && inFlightList.isNotEmpty()) {
            val ex = IOException("Connection reset locally")
            executeOnWorker {
                for (req in inFlightList) {
                    req.timeoutCancellable?.cancel()
                    try {
                        req.future.completeExceptionally(ex)
                    } catch (_: Throwable) {
                    }
                    if (!req.isSideEffect) {
                        synchronized(unconfirmedSideEffectsLock) {
                            unconfirmedSideEffects.remove(req.requestId)
                        }
                        try {
                            req.reconciliationFuture.completeExceptionally(ex)
                        } catch (_: Throwable) {
                        }
                    }
                }
            }
        } else {
            for (req in inFlightList) {
                req.timeoutCancellable?.cancel()
            }
        }

        synchronized(stateLock) {
            try {
                selectionKey?.cancel()
            } catch (_: Throwable) {
            }
            selectionKey = null

            try {
                socketChannel?.close()
            } catch (_: Throwable) {
            }
            socketChannel = null
        }

        synchronized(writeLock) {
            outboundQueue.clear()
            currentWriteBuffer = null
            queuedOutboundBytes = 0L
        }

        synchronized(inboundMailboxLock) {
            inboundMailbox.clear()
            currentDispatchFrame = null
            inboundMailboxBytes = 0L
        }

        inboundWorkerQueue.clear()
    }

    /**
     * 恢复席位成功后，按原 scope 自动重放未确认副作用请求 (UC-R11 / 契约 3 / 契约 5 / R04)
     * 严格限制并发在途上限不超过 limits.maxInFlightRequests (默认 64)，采用流水线有限调度
     */
    private fun replayPendingSideEffects() {
        val records = synchronized(unconfirmedSideEffectsLock) {
            unconfirmedSideEffects.values.toList()
        }
        if (records.isEmpty()) return

        synchronized(unconfirmedSideEffectsLock) {
            replayQueue.clear()
            for (record in records) {
                if (!inFlightRequests.containsKey(record.requestId)) {
                    replayQueue.add(record)
                }
            }
        }
        pumpReplayQueue()
    }

    /**
     * 调度重放队列：严格遵守 limits.maxInFlightRequests 上限，每完成/超时一项后继续调度下一项 (R04)
     */
    private fun pumpReplayQueue() {
        while (!closed && socketChannel != null && socketChannel?.isOpen == true) {
            val record = synchronized(unconfirmedSideEffectsLock) {
                if (inFlightRequests.size >= limits.maxInFlightRequests) {
                    return
                }
                while (true) {
                    val candidate = replayQueue.poll() ?: return
                    if (unconfirmedSideEffects.containsKey(candidate.requestId) &&
                        !inFlightRequests.containsKey(candidate.requestId)
                    ) {
                        inFlightRequests[candidate.requestId] = candidate
                        return@synchronized candidate
                    }
                }
                @Suppress("UNREACHABLE_CODE")
                null
            } ?: return

            sendReplayEntry(record)
        }
    }

    private fun sendReplayEntry(record: PendingRequest) {
        val env = TcpRequestEnvelope(
            requestScope = record.scope, // 原 scope
            requestId = record.requestId,
            request = record.request
        )
        if (record.future.isDone) {
            record.future = CompletableFuture()
        }
        val timeoutMillis = limits.requestTimeoutMillis.coerceAtLeast(50L)
        record.timeoutCancellable?.cancel()
        record.timeoutCancellable = scheduler.schedule(timeoutMillis) {
            val removed = synchronized(unconfirmedSideEffectsLock) {
                inFlightRequests.remove(record.requestId, record)
            }
            if (removed) {
                val ex = TcpConnectionException("Replayed request timed out after ${timeoutMillis}ms (api=${record.request.api}, id=${record.requestId})")
                executeOnWorker {
                    record.future.completeExceptionally(ex)
                }
                // R04: 重放超时仍属未知结果，继续保留在 unconfirmedSideEffects，不破坏对账 Future
                pumpReplayQueue()
            }
        }

        try {
            writeRawFrame(TcpMessageCodec.toFrame(env))
        } catch (_: Throwable) {
            record.timeoutCancellable?.cancel()
            synchronized(unconfirmedSideEffectsLock) {
                inFlightRequests.remove(record.requestId, record)
            }
            pumpReplayQueue()
        }
    }

    // ==========================================
    // 状态切换辅助
    // ==========================================

    private fun applyPlayerBindingState(newBinding: RoomBinding) {
        this.boundRoomId = newBinding.roomId
        this.boundSeat = newBinding.seat
        this.resumeToken = newBinding.resumeToken
        this.bindingEpoch++

        this.boundSpectatorId = null
        this.boundSpectatorToken = null

        this.lastDispatchedPlayerCursor = newBinding.snapshot.cursor
        this.lastDispatchedPublicCursor = -1L
        this.lastRoomInfoVersion = 0L
        this.lastDispatchedRoomVersion = 0L
        this.latestRoomInfo = null
        this.earlyRoomUpdateBuffer.clear()
        this.earlyPublicUpdateBuffer.clear()
        this.earlyPlayerUpdateBuffer.clear()
        this.roomDispatchQueue.clear()
        this.publicDispatchQueue.clear()
        this.playerDispatchQueue.clear()
        this.roomDispatchQueueBytes.set(0L)
        this.publicDispatchQueueBytes.set(0L)
        this.playerDispatchQueueBytes.set(0L)
        this.earlyBufferBytes.set(0L)
        this.earlyBufferOverflow = false
        resetRecoveryState()
    }

    private fun applySpectatorBindingState(binding: SpectatorBinding) {
        this.boundRoomId = binding.roomId
        this.boundSeat = null
        this.resumeToken = null
        this.boundSpectatorId = binding.spectatorId
        this.boundSpectatorToken = binding.resumeToken
        this.bindingEpoch++

        this.lastDispatchedPlayerCursor = -1L
        this.lastDispatchedPublicCursor = binding.snapshot.cursor
        this.lastRoomInfoVersion = 0L
        this.lastDispatchedRoomVersion = 0L
        this.latestRoomInfo = null
        this.earlyRoomUpdateBuffer.clear()
        this.earlyPlayerUpdateBuffer.clear()
        this.earlyPublicUpdateBuffer.clear()
        this.roomDispatchQueue.clear()
        this.publicDispatchQueue.clear()
        this.playerDispatchQueue.clear()
        this.roomDispatchQueueBytes.set(0L)
        this.publicDispatchQueueBytes.set(0L)
        this.playerDispatchQueueBytes.set(0L)
        this.earlyBufferBytes.set(0L)
        this.earlyBufferOverflow = false
        resetRecoveryState()
    }

    private fun applyLeaveRoomState() {
        this.boundRoomId = null
        this.boundSeat = null
        this.resumeToken = null
        this.boundSpectatorId = null
        this.boundSpectatorToken = null
        this.bindingEpoch++

        this.lastDispatchedPlayerCursor = -1L
        this.lastDispatchedPublicCursor = -1L
        this.lastRoomInfoVersion = 0L
        this.lastDispatchedRoomVersion = 0L
        this.latestRoomInfo = null
        this.earlyRoomUpdateBuffer.clear()
        this.earlyPlayerUpdateBuffer.clear()
        this.earlyPublicUpdateBuffer.clear()
        this.roomDispatchQueue.clear()
        this.publicDispatchQueue.clear()
        this.playerDispatchQueue.clear()
        this.roomDispatchQueueBytes.set(0L)
        this.publicDispatchQueueBytes.set(0L)
        this.playerDispatchQueueBytes.set(0L)
        this.earlyBufferBytes.set(0L)
        this.earlyBufferOverflow = false
        resetRecoveryState()
    }

    // ==========================================
    // LobbyApi 接口实现
    // ==========================================

    override fun listRooms(): CompletableFuture<List<RoomSummary>> {
        val req = ApiRequest(
            api = ApiTags.LIST_ROOMS,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(req) { rsp, _ -> rsp.rooms ?: emptyList() }
    }

    override fun createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding> {
        val apiReq = ApiRequest(
            api = ApiTags.CREATE_ROOM,
            requestId = UUID.randomUUID().toString(),
            playerName = req.playerName,
            config = req.config,
            protocolVersion = req.protocolVersion
        )
        return sendRequest(apiReq) { rsp, env ->
            val newBinding = RoomBinding(
                roomId = rsp.roomId ?: throw IllegalStateException("Missing roomId in CREATE_ROOM response"),
                seat = rsp.seat ?: throw IllegalStateException("Missing seat in CREATE_ROOM response"),
                resumeToken = rsp.resumeToken ?: throw IllegalStateException("Missing resumeToken in CREATE_ROOM response"),
                snapshot = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in CREATE_ROOM response")
            )
            synchronized(stateLock) {
                applyPlayerBindingState(newBinding)
            }
            if (env.grantedScope != null) {
                currentScope = env.grantedScope
            }
            newBinding
        }
    }

    override fun joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding> {
        val apiReq = ApiRequest(
            api = ApiTags.JOIN_ROOM,
            requestId = UUID.randomUUID().toString(),
            roomId = req.roomId,
            playerName = req.playerName,
            preferSeat = req.preferSeat
        )
        return sendRequest(apiReq) { rsp, env ->
            val newBinding = RoomBinding(
                roomId = rsp.roomId ?: throw IllegalStateException("Missing roomId in JOIN_ROOM response"),
                seat = rsp.seat ?: throw IllegalStateException("Missing seat in JOIN_ROOM response"),
                resumeToken = rsp.resumeToken ?: throw IllegalStateException("Missing resumeToken in JOIN_ROOM response"),
                snapshot = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in JOIN_ROOM response")
            )
            synchronized(stateLock) {
                applyPlayerBindingState(newBinding)
            }
            if (env.grantedScope != null) {
                currentScope = env.grantedScope
            }
            newBinding
        }
    }

    override fun resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding> {
        val apiReq = ApiRequest(
            api = ApiTags.RESUME_SESSION,
            requestId = UUID.randomUUID().toString(),
            roomId = req.roomId,
            token = req.token,
            cursor = req.cursor
        )
        return sendRequest(apiReq) { rsp, env ->
            val newBinding = RoomBinding(
                roomId = rsp.roomId ?: throw IllegalStateException("Missing roomId in RESUME_SESSION response"),
                seat = rsp.seat ?: throw IllegalStateException("Missing seat in RESUME_SESSION response"),
                resumeToken = rsp.resumeToken ?: throw IllegalStateException("Missing resumeToken in RESUME_SESSION response"),
                snapshot = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in RESUME_SESSION response")
            )
            synchronized(stateLock) {
                applyPlayerBindingState(newBinding)
            }
            if (env.grantedScope != null) {
                currentScope = env.grantedScope
            }
            // 席位恢复成功后，按原 scope 自动重放未确认副作用请求 (UC-R11)
            replayPendingSideEffects()
            newBinding
        }
    }

    override fun leaveRoom(): CompletableFuture<ProtocolActionReceipt> {
        val (currentRoom, token) = synchronized(stateLock) {
            boundRoomId to (resumeToken ?: boundSpectatorToken)
        }
        val apiReq = ApiRequest(
            api = ApiTags.LEAVE_ROOM,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoom,
            token = token
        )
        return sendRequest(apiReq) { rsp, _ ->
            val receipt = rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = ProtocolReceiptStatus.APPLIED
            )
            if (receipt.status == ProtocolReceiptStatus.APPLIED) {
                synchronized(stateLock) {
                    applyLeaveRoomState()
                }
            }
            receipt
        }
    }

    override fun getRoomInfo(): CompletableFuture<ProtocolRoomInfo> {
        val currentRoomId: String?
        synchronized(stateLock) {
            currentRoomId = boundRoomId
        }
        val apiReq = ApiRequest(
            api = ApiTags.GET_ROOM_INFO,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId
        )
        return sendRequest(apiReq) { rsp, _ ->
            val info = rsp.roomInfo ?: throw IllegalStateException("Missing roomInfo in GET_ROOM_INFO response")
            dispatchRoomUpdate(info)
            info
        }
    }

    override fun setReady(ready: Boolean): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SET_READY,
            requestId = UUID.randomUUID().toString(),
            roomId = boundRoomId,
            ready = ready
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = if (rsp.success) ProtocolReceiptStatus.APPLIED else ProtocolReceiptStatus.REJECTED,
                reason = rsp.error
            )
        }
    }

    override fun updateConfig(config: ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.UPDATE_CONFIG,
            requestId = UUID.randomUUID().toString(),
            roomId = boundRoomId,
            roomConfig = config
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = if (rsp.success) ProtocolReceiptStatus.APPLIED else ProtocolReceiptStatus.REJECTED,
                reason = rsp.error
            )
        }
    }

    override fun onRoomUpdate(listener: (ProtocolRoomInfo) -> Unit): AutoCloseable {
        val earlyItems = mutableListOf<Pair<ProtocolRoomInfo, Long>>()
        val capturedEpoch: Long
        synchronized(stateLock) {
            capturedEpoch = bindingEpoch
            roomUpdateListeners.add(listener)
            while (true) {
                val entry = earlyRoomUpdateBuffer.poll() ?: break
                val wireBytes = entry.second
                val remaining = earlyBufferBytes.addAndGet(-wireBytes)
                if (remaining < 0L) earlyBufferBytes.set(0L)
                earlyItems.add(entry.first to wireBytes)
            }
            val maxDrainedVersion = earlyItems.map { it.first.version }.maxOrNull() ?: lastDispatchedRoomVersion
            val cachedLatest = latestRoomInfo
            if (cachedLatest != null && cachedLatest.version > maxDrainedVersion) {
                val wireBytes = TcpMessageCodec.toRoomUpdateFrame(cachedLatest).payload.size.toLong()
                earlyItems.add(cachedLatest to wireBytes)
            }
            for (item in earlyItems) {
                val wireBytes = item.second
                if (roomDispatchQueue.size < maxDispatchQueueSize && roomDispatchQueueBytes.get() + wireBytes <= maxDispatchQueueBytes) {
                    roomDispatchQueue.offer(DispatchEntry(item.first, capturedEpoch, wireBytes))
                    roomDispatchQueueBytes.addAndGet(wireBytes)
                } else {
                    earlyBufferOverflow = true
                    roomRecoveryPending = true
                }
            }
            if (!earlyBufferOverflow) {
                roomRecoveryPending = false
            }
        }
        drainRoomQueue()
        return AutoCloseable {
            synchronized(stateLock) {
                roomUpdateListeners.remove(listener)
            }
        }
    }

    // ==========================================
    // SessionApi 接口实现
    // ==========================================

    override fun capabilities(): CompletableFuture<ProtocolAccessCapabilities> {
        val apiReq = ApiRequest(
            api = ApiTags.CAPABILITIES,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.capabilities ?: throw IllegalStateException("Missing capabilities in CAPABILITIES response")
        }
    }

    override fun snapshot(): CompletableFuture<ProtocolPlayerSnapshot> {
        val (currentRoomId, currentToken) = synchronized(stateLock) {
            boundRoomId to resumeToken
        }
        val apiReq = ApiRequest(
            api = ApiTags.SNAPSHOT,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.snapshot ?: throw IllegalStateException("Missing snapshot in SNAPSHOT response")
        }
    }

    override fun decision(): CompletableFuture<ProtocolPlayerDecision> {
        val apiReq = ApiRequest(
            api = ApiTags.DECISION,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.decision ?: throw IllegalStateException("Missing decision in DECISION response")
        }
    }

    override fun submit(choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_CHOICE,
            requestId = choice.requestId,
            choice = choice
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = choice.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun submitShufflePlan(plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_SHUFFLE_PLAN,
            requestId = plan.requestId,
            shufflePlan = plan
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = plan.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_TILE_MARK,
            requestId = request.requestId,
            tileMark = request
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = request.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun submitSleeveBloom(request: ProtocolSleeveBloomRequest): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_SLEEVE_BLOOM,
            requestId = request.requestId,
            sleeveBloom = request
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = request.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun submitSleeveBloomChoice(request: ProtocolSleeveBloomChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE,
            requestId = request.requestId,
            sleeveBloomChoice = request
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = request.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun submitBeamSwap(request: ProtocolBeamSwapRequest): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_BEAM_SWAP,
            requestId = request.requestId,
            beamSwap = request
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = request.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun submitBeamSwapChoice(request: ProtocolBeamSwapChoiceRequest): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.SUBMIT_BEAM_SWAP_CHOICE,
            requestId = request.requestId,
            beamSwapChoice = request
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = request.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun start(): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.START_MATCH,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun nextRound(): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.NEXT_ROUND,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun resume(cursor: Long): CompletableFuture<ProtocolPlayerSnapshot> {
        val (currentRoomId, currentToken) = synchronized(stateLock) {
            boundRoomId to resumeToken
        }
        val apiReq = ApiRequest(
            api = ApiTags.RESUME_CURSOR,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken,
            cursor = cursor
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.snapshot ?: throw IllegalStateException("Missing snapshot in RESUME_CURSOR response")
        }
    }

    override fun claimControl(): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.CLAIM_CONTROL,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun releaseControl(): CompletableFuture<ProtocolActionReceipt> {
        val apiReq = ApiRequest(
            api = ApiTags.RELEASE_CONTROL,
            requestId = UUID.randomUUID().toString()
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.receipt ?: ProtocolActionReceipt(
                requestId = apiReq.requestId,
                status = ProtocolReceiptStatus.REJECTED,
                reason = rsp.error ?: ApiErrors.INTERNAL_ERROR
            )
        }
    }

    override fun queryRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
        val apiReq = ApiRequest(
            api = ApiTags.QUERY_ROUND_RESULT,
            requestId = UUID.randomUUID().toString(),
            roundId = roundId
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.events ?: emptyList()
        }
    }

    // ==========================================
    // SpectatorApi 接口实现
    // ==========================================

    override fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding> {
        val apiReq = ApiRequest(
            api = ApiTags.JOIN_SPECTATOR,
            requestId = UUID.randomUUID().toString(),
            roomId = req.roomId,
            spectatorName = req.spectatorName,
            protocolVersion = req.protocolVersion
        )
        return sendRequest(apiReq) { rsp, env ->
            val binding = rsp.spectatorBinding ?: throw IllegalStateException("Missing spectatorBinding in JOIN_SPECTATOR response")
            synchronized(stateLock) {
                applySpectatorBindingState(binding)
            }
            if (env.grantedScope != null) {
                currentScope = env.grantedScope
            }
            binding
        }
    }

    override fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding> {
        val apiReq = ApiRequest(
            api = ApiTags.RESUME_SPECTATOR,
            requestId = UUID.randomUUID().toString(),
            roomId = req.roomId,
            spectatorId = req.spectatorId,
            token = req.resumeToken,
            publicCheckpoint = req.checkpoint,
            protocolVersion = req.protocolVersion
        )
        return sendRequest(apiReq) { rsp, env ->
            val binding = rsp.spectatorBinding ?: throw IllegalStateException("Missing spectatorBinding in RESUME_SPECTATOR response")
            synchronized(stateLock) {
                applySpectatorBindingState(binding)
            }
            if (env.grantedScope != null) {
                currentScope = env.grantedScope
            }
            // 观战恢复成功后，按原 scope 自动重放未确认副作用请求 (UC-R11)
            replayPendingSideEffects()
            binding
        }
    }

    override fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot> {
        val (currentRoomId, currentToken) = synchronized(stateLock) {
            boundRoomId to boundSpectatorToken
        }
        val apiReq = ApiRequest(
            api = ApiTags.PUBLIC_SNAPSHOT,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.publicSnapshot ?: throw IllegalStateException("Missing publicSnapshot in PUBLIC_SNAPSHOT response")
        }
    }

    override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot> {
        val (currentRoomId, currentToken) = synchronized(stateLock) {
            boundRoomId to boundSpectatorToken
        }
        val apiReq = ApiRequest(
            api = ApiTags.RESUME_PUBLIC,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken,
            publicCheckpoint = checkpoint
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.publicSnapshot ?: throw IllegalStateException("Missing publicSnapshot in RESUME_PUBLIC response")
        }
    }

    override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
        val (currentRoomId, currentToken) = synchronized(stateLock) {
            boundRoomId to boundSpectatorToken
        }
        val apiReq = ApiRequest(
            api = ApiTags.QUERY_PUBLIC_ROUND_RESULT,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken,
            roundId = roundId
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.publicEvents ?: emptyList()
        }
    }

    override fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable {
        val capturedEpoch: Long
        val triggerRecovery: Boolean
        synchronized(stateLock) {
            capturedEpoch = bindingEpoch
            publicUpdateListeners.add(listener)
            while (true) {
                val entry = earlyPublicUpdateBuffer.poll() ?: break
                val wireBytes = entry.second
                val remaining = earlyBufferBytes.addAndGet(-wireBytes)
                if (remaining < 0L) earlyBufferBytes.set(0L)
                if (publicDispatchQueue.size < maxDispatchQueueSize && publicDispatchQueueBytes.get() + wireBytes <= maxDispatchQueueBytes) {
                    publicDispatchQueue.offer(DispatchEntry(entry.first, capturedEpoch, wireBytes))
                    publicDispatchQueueBytes.addAndGet(wireBytes)
                } else {
                    earlyBufferOverflow = true
                    if (boundSpectatorId != null) {
                        publicRecoveryPending = true
                    }
                }
            }
            if (earlyBufferOverflow && boundSpectatorId != null) {
                publicRecoveryPending = true
            }
            triggerRecovery = publicRecoveryPending && boundSpectatorId != null
        }
        drainPublicQueue()
        if (triggerRecovery && !closed) {
            triggerPublicRecovery(capturedEpoch)
        }
        return AutoCloseable {
            synchronized(stateLock) {
                publicUpdateListeners.remove(listener)
            }
        }
    }

    // ==========================================
    // PushChannel 接口实现
    // ==========================================

    override fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable {
        val capturedEpoch: Long
        val triggerRecovery: Boolean
        synchronized(stateLock) {
            capturedEpoch = bindingEpoch
            updateListeners.add(listener)
            while (true) {
                val entry = earlyPlayerUpdateBuffer.poll() ?: break
                val wireBytes = entry.second
                val remaining = earlyBufferBytes.addAndGet(-wireBytes)
                if (remaining < 0L) earlyBufferBytes.set(0L)
                if (playerDispatchQueue.size < maxDispatchQueueSize && playerDispatchQueueBytes.get() + wireBytes <= maxDispatchQueueBytes) {
                    playerDispatchQueue.offer(DispatchEntry(entry.first, capturedEpoch, wireBytes))
                    playerDispatchQueueBytes.addAndGet(wireBytes)
                } else {
                    earlyBufferOverflow = true
                    if (boundSeat != null) {
                        playerRecoveryPending = true
                    }
                }
            }
            if (earlyBufferOverflow && boundSeat != null) {
                playerRecoveryPending = true
            }
            triggerRecovery = playerRecoveryPending && boundSeat != null
        }
        drainPlayerQueue()
        if (triggerRecovery && !closed) {
            triggerPlayerRecovery(capturedEpoch)
        }
        return AutoCloseable {
            synchronized(stateLock) {
                updateListeners.remove(listener)
            }
        }
    }

    override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
        disconnectListeners.add(listener)
        val reason = disconnectReasonRef.get()
        if (closed && reason != null) {
            try {
                listener(reason)
            } catch (_: Throwable) {
            }
        }
        return AutoCloseable {
            disconnectListeners.remove(listener)
        }
    }

    // ==========================================
    // 未确认副作用与对账快照查询 (R04)
    // ==========================================

    /**
     * 未确认副作用快照模型 (供外部对账查询与回归测试使用，R04)
     */
    data class UnconfirmedSideEffect(
        val scope: String,
        val requestId: String,
        val request: ApiRequest,
        val isSideEffect: Boolean
    )

    /**
     * 当前保留的未确认副作用记录数量 (R04)
     */
    val unconfirmedSideEffectCount: Int
        get() = synchronized(unconfirmedSideEffectsLock) { unconfirmedSideEffects.size }

    /**
     * 获取指定 requestId 的未确认副作用记录 (R04)
     */
    fun getUnconfirmedSideEffect(requestId: String): UnconfirmedSideEffect? {
        val pending = synchronized(unconfirmedSideEffectsLock) { unconfirmedSideEffects[requestId] } ?: return null
        return UnconfirmedSideEffect(
            scope = pending.scope,
            requestId = pending.requestId,
            request = pending.request,
            isSideEffect = pending.isSideEffect
        )
    }

    /**
     * 获取指定 requestId 的对账 Future (R04)
     */
    fun getReconciliationFuture(requestId: String): CompletableFuture<ApiResponse>? {
        return synchronized(unconfirmedSideEffectsLock) { unconfirmedSideEffects[requestId]?.reconciliationFuture }
    }

    /**
     * 获取所有保留的未确认副作用 requestId 集合 (R04)
     */
    fun unconfirmedSideEffectIds(): Set<String> {
        return synchronized(unconfirmedSideEffectsLock) { unconfirmedSideEffects.keys.toSet() }
    }

    // ==========================================
    // R07 / R09 测试辅助属性 (internal)
    // ==========================================

    internal fun writeRawFrameForTest(frame: TcpFrame) = writeRawFrame(frame)
    internal val outboundQueueSize: Int get() = synchronized(writeLock) {
        outboundQueue.size + (if (currentWriteBuffer != null) 1 else 0)
    }
    internal val queuedOutboundByteCount: Long get() = synchronized(writeLock) { queuedOutboundBytes }
    internal val currentWriteBufferForTest: ByteBuffer? get() = synchronized(writeLock) { currentWriteBuffer }
    internal val isIoLoopAlive: Boolean get() = ioThread?.isAlive == true
    internal val inboundMailboxQueueSize: Int get() = synchronized(inboundMailboxLock) {
        inboundMailbox.size + (if (currentDispatchFrame != null) 1 else 0)
    }
    internal val inboundMailboxQueuedBytes: Long get() = synchronized(inboundMailboxLock) { inboundMailboxBytes }
    internal val currentDispatchFrameForTest: TcpFrame? get() = synchronized(inboundMailboxLock) { currentDispatchFrame }
    internal fun setCurrentDispatchFrameForTest(frame: TcpFrame?) = synchronized(inboundMailboxLock) {
        currentDispatchFrame = frame
    }
    internal fun setInboundMailboxBytesForTest(bytes: Long) = synchronized(inboundMailboxLock) {
        inboundMailboxBytes = bytes
    }
    internal fun setCurrentWriteBufferForTest(buf: ByteBuffer?) = synchronized(writeLock) {
        currentWriteBuffer = buf
    }
    internal fun setQueuedOutboundBytesForTest(bytes: Long) = synchronized(writeLock) {
        queuedOutboundBytes = bytes
    }
    internal fun addOutboundQueueForTest(buf: ByteBuffer) = synchronized(writeLock) {
        outboundQueue.add(buf)
    }
    internal val selectionKeyForTest: SelectionKey? get() = selectionKey
    internal val socketChannelForTest: SocketChannel? get() = socketChannel

    // ==========================================
    // ServerConnection 生命周期
    // ==========================================

    override fun close() {
        isClosed.set(true)
        disconnectReasonRef.compareAndSet(null, TcpCloseReasons.LOCAL_CLOSED)

        val closeEx = TcpConnectionException("Client binding closed locally")
        val sideEffectsList = synchronized(unconfirmedSideEffectsLock) {
            replayQueue.clear()
            val list = unconfirmedSideEffects.values.toList()
            unconfirmedSideEffects.clear()
            list
        }
        if (sideEffectsList.isNotEmpty()) {
            executeOnWorker {
                for (pending in sideEffectsList) {
                    pending.timeoutCancellable?.cancel()
                    try {
                        pending.reconciliationFuture.completeExceptionally(closeEx)
                    } catch (_: Throwable) {
                    }
                }
            }
        }
        closeSocketQuietly(failInFlight = true)
        synchronized(stateLock) {
            earlyPlayerUpdateBuffer.clear()
            earlyPublicUpdateBuffer.clear()
            earlyRoomUpdateBuffer.clear()
            playerDispatchQueue.clear()
            publicDispatchQueue.clear()
            roomDispatchQueue.clear()
            playerDispatchQueueBytes.set(0L)
            publicDispatchQueueBytes.set(0L)
            roomDispatchQueueBytes.set(0L)
            earlyBufferBytes.set(0L)
            earlyBufferOverflow = false
            lastDispatchedPlayerCursor = -1L
            lastDispatchedPublicCursor = -1L
            lastDispatchedRoomVersion = 0L
            lastRoomInfoVersion = 0L
            latestRoomInfo = null
            resetRecoveryState()
        }
        updateListeners.clear()
        publicUpdateListeners.clear()
        roomUpdateListeners.clear()
        disconnectListeners.clear()

        if (ownsScheduler && scheduler is AutoCloseable) {
            try {
                scheduler.close()
            } catch (_: Throwable) {
            }
        }

        ioLoopRunning.set(false)
        try {
            selector?.wakeup()
            selector?.close()
        } catch (_: Throwable) {
        }
        selector = null

        try {
            ioThread?.join(1000)
        } catch (_: Throwable) {
        }
        ioThread = null

        drainOverflow()

        try {
            workerExecutor.shutdown()
            if (!Thread.currentThread().name.startsWith("TcpClient-Worker-")) {
                workerExecutor.awaitTermination(1, TimeUnit.SECONDS)
                workerExecutor.shutdownNow()
            }
        } catch (_: Throwable) {
        }

        try {
            completionExecutor.shutdown()
            if (!Thread.currentThread().name.startsWith("TcpClient-Completion-")) {
                completionExecutor.awaitTermination(1, TimeUnit.SECONDS)
                completionExecutor.shutdownNow()
            }
        } catch (_: Throwable) {
        }

        try {
            connectExecutor.shutdown()
            if (!Thread.currentThread().name.startsWith("TcpClient-Connect-")) {
                connectExecutor.awaitTermination(1, TimeUnit.SECONDS)
                connectExecutor.shutdownNow()
            }
        } catch (_: Throwable) {
        }

        drainOverflow()
    }
}

package com.majiang.server.tcp

import com.majiang.access.ActionReceipt
import com.majiang.access.PlayerSnapshot
import com.majiang.access.ReceiptStatus
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.SpectatorBinding
import com.majiang.server.host.HostDispatchPools
import com.majiang.server.idempotency.AdmissionResult
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.idempotency.InFlightReceipt
import com.majiang.server.idempotency.RequestScopeReceiptStore
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.room.RoomRegistry
import com.majiang.server.room.SharedRoom
import com.majiang.server.room.ttlFor
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.tcp.TcpClose
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpLimits
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpPing
import com.majiang.transport.tcp.TcpPong
import com.majiang.transport.tcp.TcpProtocolException
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpScope
import com.majiang.transport.tcp.TcpWelcome
import com.majiang.transport.tcp.TcpWire
import java.io.IOException
import java.net.InetSocketAddress
import java.net.StandardSocketOptions
import java.nio.ByteBuffer
import java.nio.channels.ClosedChannelException
import java.nio.channels.SelectionKey
import java.nio.channels.Selector
import java.nio.channels.ServerSocketChannel
import java.nio.channels.SocketChannel
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ThreadFactory
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * TCP 权威牌桌服务器 (TcpTableServer, 核心契约 1–6 / Design §2/§4/§5)
 *
 * 核心架构特性：
 * 1. NIO 事件驱动循环：单 ServerSocketChannel + 单 Selector 线程，管理全部客户端连接的 I/O 多路复用；
 * 2. 严格线程分离：I/O 线程专职负责有界读写、成帧与排队，不执行 JSON 大对象编解码、房间权威或用户回调；
 *    编解码与请求分发由有界工作池驱动每连接串行接收邮箱；
 * 3. 内存与过载治理：全局 256 MiB 预算预留制 (TcpGlobalBudget)，单连接待发队列 256 帧 / 4 MiB (SLOW_CONSUMER)，
 *    串行邮箱 64 帧 / 4 MiB，未握手连接桶上限 64，总连接数上限 512 (SERVER_OVERLOADED)；
 * 4. 握手与心跳状态机：HELLO/WELCOME 握手（5 秒超时，业务前置或重复 HELLO 判 PROTOCOL_VIOLATION，版本不符判 VERSION_MISMATCH）；
 *    独立 10 秒入站无完整合法帧判死 (HEARTBEAT_TIMEOUT，零散字节不刷新)；不完整帧首字节起 10 秒硬截止；
 *    停滞写入 10 秒超时；终态通知收尾最多 1 秒后强制释放；控制优先级队列 8 帧/8 KiB；启用 TCP_NODELAY；
 * 5. 跨连接幂等与作用域治理：集成 [RequestScopeReceiptStore] (F12 共享实现)，三元组 (principal, scope, requestId) 去重；
 *    同载荷重放原结果/在途关联，不同载荷/API 判 CONFLICT (D1)；GET_REQUEST_SCOPE 权威查询 (D9)；
 *    作用域过期先查回执再判 REQUEST_SCOPE_EXPIRED (D3)；副作用容量满受理前判 SERVER_BUSY (D2)；
 * 6. 路由与原子基线推送 (UC-S01)：在同桌串行队列内原子获取初始基线快照并安装订阅，含基线的 RSP 优先入队后再放行后续 UPDATE；
 *    推送采用 typed 编解码，停服时未决请求明确结构化失败 (SERVER_SHUTDOWN)。
 */
class TcpTableServer(
    val config: TcpServerConfig = TcpServerConfig(),
    val registry: RoomRegistry = RoomRegistry(),
    val receiptStore: RequestScopeReceiptStore = RequestScopeReceiptStore(
        clock = config.clock,
        maxInFlightPerMember = config.maxInFlightRequestsPerMember,
        maxCompletedPerMember = config.maxCompletedReceiptsPerMember,
        maxLobbySideEffectReceipts = config.maxLobbySideEffectReceipts,
        maxLobbyQueryReceipts = config.maxLobbyQueryReceipts,
        defaultScopeValidityMillis = config.scopeValidityMillis,
        scopeQueryRateLimitMillis = config.scopeQueryRateLimitMillis
    )
) : AutoCloseable {

    val globalBudget: TcpGlobalBudget = TcpGlobalBudget(config.globalBudgetBytes)
    val roomAdmission: CreateRoomAdmission = CreateRoomAdmission(
        registry = registry,
        capacityPolicy = config.capacityPolicy,
        clockMillis = { config.clock.currentTimeMillis() }
    )

    private val isRunning = AtomicBoolean(false)
    private val isClosed = AtomicBoolean(false)

    private val connectionIdGenerator = AtomicLong(1L)
    val connections = ConcurrentHashMap<Long, TcpConnection>()
    val unhandshakedCount = AtomicInteger(0)

    // 已签发的大厅恢复令牌注册表 (R06)
    val issuedLobbyTokens = ConcurrentHashMap<String, Long>()

    private lateinit var selector: Selector
    private lateinit var serverChannel: ServerSocketChannel
    private lateinit var ioThread: Thread
    private lateinit var workerPool: ExecutorService
    private var tickerExecutor: ScheduledExecutorService? = null
    private var patrolExecutor: ScheduledExecutorService? = null

    private val readBuffer: ByteBuffer = ByteBuffer.allocateDirect(config.maxBytesPerRotation)

    /** 绑定的实际本地端口 (若配置为 0，则在 start 后返回操作系统分配的端口) */
    val boundPort: Int
        get() = if (::serverChannel.isInitialized && serverChannel.isOpen) {
            (serverChannel.localAddress as? InetSocketAddress)?.port ?: config.port
        } else {
            config.port
        }

    private var hostDispatchPools: HostDispatchPools? = null
    internal var lastCloseRemainingTasks: Int = 0

    /** 获取宿主共享派发与完成线程池组引用（供测试与度量读取） */
    fun getDispatchPools(): HostDispatchPools? = hostDispatchPools

    private val publicFrameCache: PublicUpdateFrameCache = PublicUpdateFrameCache(
        maxEntries = config.publicFrameCacheMaxEntries,
        maxBytes = config.publicFrameCacheMaxBytes
    )

    /** 获取公开流更新帧编码缓存（供测试与度量读取，MR-07b） */
    internal fun getPublicUpdateFrameCache(): PublicUpdateFrameCache = publicFrameCache

    val closed: Boolean get() = isClosed.get()

    /**
     * 启动 TCP 服务器与 NIO 事件循环
     */
    fun start() {
        if (!isRunning.compareAndSet(false, true)) {
            return
        }

        // 初始化共享派发与完成线程池组 (MR-07a)
        hostDispatchPools = HostDispatchPools(
            dispatchThreads = config.dispatchPoolThreads,
            dispatchQueueCapacity = config.dispatchPoolQueueCapacity,
            completionThreads = config.completionPoolThreads,
            completionQueueCapacity = config.completionPoolQueueCapacity,
            closeDeadlineMillis = config.sharedPoolsCloseDeadlineMillis,
            seatChannelCapacityItems = config.seatChannelCapacityItems,
            seatChannelCapacityBytes = config.seatChannelCapacityBytes,
            publicChannelCapacityItems = config.publicChannelCapacityItems,
            publicChannelCapacityBytes = config.publicChannelCapacityBytes,
            channelDrainBatchSize = config.channelDrainBatchSize
        )

        selector = Selector.open()
        serverChannel = ServerSocketChannel.open()
        serverChannel.configureBlocking(false)
        serverChannel.bind(InetSocketAddress(config.host, config.port))
        serverChannel.register(selector, SelectionKey.OP_ACCEPT)

        val threadFactory = object : ThreadFactory {
            private val counter = AtomicInteger(1)
            override fun newThread(r: Runnable): Thread {
                val t = Thread(r, "TcpTableServer-Worker-${counter.getAndIncrement()}")
                t.isDaemon = true
                return t
            }
        }
        workerPool = Executors.newFixedThreadPool(config.workerThreadPoolSize, threadFactory)

        // 启动独立权威房间 Ticker (R01 / AGENTS §5.2)
        if (config.tickIntervalMillis > 0L) {
            val ticker = Executors.newSingleThreadScheduledExecutor { r ->
                Thread(r, "TcpTableServer-Ticker").apply { isDaemon = true }
            }
            ticker.scheduleWithFixedDelay({
                if (isRunning.get() && !isClosed.get()) {
                    for (room in registry.activeRooms()) {
                        try {
                            room.tick(presentationBusy = false)
                        } catch (_: Throwable) {
                        }
                    }
                }
            }, config.tickIntervalMillis, config.tickIntervalMillis, TimeUnit.MILLISECONDS)
            tickerExecutor = ticker
        }

        // 启动独立房间巡检回收调度器 (MR-06 / 任务 E)
        if (config.retentionPolicy.patrolIntervalMillis > 0L) {
            val patrol = Executors.newSingleThreadScheduledExecutor { r ->
                Thread(r, "TcpTableServer-RoomPatrol").apply { isDaemon = true }
            }
            patrol.scheduleWithFixedDelay({
                if (isRunning.get() && !isClosed.get()) {
                    try {
                        performRoomPatrol()
                    } catch (_: Throwable) {
                    }
                }
            }, config.retentionPolicy.patrolIntervalMillis, config.retentionPolicy.patrolIntervalMillis, TimeUnit.MILLISECONDS)
            patrolExecutor = patrol
        }

        ioThread = Thread({ runEventLoop() }, "TcpTableServer-IO")
        ioThread.isDaemon = true
        ioThread.start()
    }

    /**
     * NIO Selector 核心事件循环
     */
    private fun runEventLoop() {
        var lastTimerCheck = config.clock.currentTimeMillis()

        while (isRunning.get() && selector.isOpen) {
            try {
                // 每 50ms 轮询一次，兼顾 I/O 就绪通知与高精度超时监测
                selector.select(50)

                val selectedKeys = selector.selectedKeys()
                val it = selectedKeys.iterator()
                while (it.hasNext()) {
                    val key = it.next()
                    it.remove()
                    if (!key.isValid) continue

                    try {
                        when {
                            key.isAcceptable -> handleAccept(key)
                            key.isReadable -> handleRead(key)
                            key.isWritable -> handleWrite(key)
                        }
                    } catch (e: Throwable) {
                        val conn = key.attachment() as? TcpConnection
                        if (conn != null) {
                            conn.close(TcpCloseReasons.PROTOCOL_VIOLATION, "I/O error: ${e.message}", immediate = true)
                            cleanupConnection(conn)
                        }
                    }
                }

                val now = config.clock.currentTimeMillis()
                if (now - lastTimerCheck >= 50) {
                    lastTimerCheck = now
                    performTimerChecks(now)
                }
            } catch (_: ClosedChannelException) {
                break
            } catch (e: Throwable) {
                if (!isRunning.get()) break
            }
        }
    }

    /**
     * 处理新入站连接建立 (OP_ACCEPT)
     */
    private fun handleAccept(key: SelectionKey) {
        val ssc = key.channel() as ServerSocketChannel
        while (true) {
            val sc = ssc.accept() ?: break

            // 1. 容量检查 (UC-L04 / UC-H09)
            if (connections.size >= config.maxConnections) {
                rejectConnectionImmediately(sc, TcpCloseReasons.SERVER_OVERLOADED, "Max connections limit reached")
                continue
            }
            if (unhandshakedCount.get() >= config.maxUnhandshakedConnections) {
                rejectConnectionImmediately(sc, TcpCloseReasons.SERVER_OVERLOADED, "Max unhandshaked connections limit reached")
                continue
            }

            try {
                sc.configureBlocking(false)
                sc.setOption(StandardSocketOptions.TCP_NODELAY, true)
            } catch (_: Throwable) {
            }

            val connId = connectionIdGenerator.getAndIncrement()
            val conn = TcpConnection(
                connectionId = connId,
                channel = sc,
                config = config,
                globalBudget = globalBudget
            )
            connections[connId] = conn
            unhandshakedCount.incrementAndGet()

            val sk = sc.register(selector, SelectionKey.OP_READ, conn)
            conn.selectionKey = sk
        }
    }

    /**
     * 直接拒绝超载连接 (UC-C05 / UC-L04)
     */
    private fun rejectConnectionImmediately(sc: SocketChannel, reason: String, message: String) {
        try {
            val closeFrame = TcpMessageCodec.toFrame(TcpClose(reason = reason, message = message))
            val buf = ByteBuffer.wrap(closeFrame.toByteArray())
            sc.write(buf)
        } catch (_: Throwable) {
        } finally {
            try {
                sc.close()
            } catch (_: Throwable) {
            }
        }
    }

    /**
     * 处理通道读取 (OP_READ, 契约 1 / UC-L05 公平调度)
     */
    private fun handleRead(key: SelectionKey) {
        val conn = key.attachment() as? TcpConnection ?: return
        if (conn.isClosingOrClosed) return

        readBuffer.clear()
        var bytesReadThisTurn = 0
        var framesDecodedThisTurn = 0
        val sc = conn.channel
        val now = config.clock.currentTimeMillis()

        while (bytesReadThisTurn < config.maxBytesPerRotation && framesDecodedThisTurn < config.maxFramesPerRotation) {
            readBuffer.clear()
            val read = try {
                sc.read(readBuffer)
            } catch (e: Throwable) {
                conn.close(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Read failure: ${e.message}", immediate = true)
                cleanupConnection(conn)
                return
            }

            if (read == -1) {
                // 对端关闭流 (EOF) (R03)
                try {
                    conn.decoder.onEndOfStream()
                    conn.close(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Client EOF", immediate = true)
                } catch (e: TcpProtocolException) {
                    conn.close(e.reasonCode, e.message, immediate = true)
                }
                cleanupConnection(conn)
                return
            }

            if (read == 0) {
                break
            }

            bytesReadThisTurn += read
            readBuffer.flip()

            val decodedFrames: List<TcpFrame>
            try {
                decodedFrames = conn.decoder.decode(readBuffer)
            } catch (e: TcpProtocolException) {
                // 六类非法进入终态，发送 CLOSE 并优雅断开 (UC-F05)
                conn.close(e.reasonCode, e.message, immediate = false)
                return
            }

            if (decodedFrames.isNotEmpty()) {
                // 仅收到完整合法入站帧才刷新入站判死时钟 (UC-L06 / UC-L07)
                conn.recordValidInboundFrame(now)

                for (i in decodedFrames.indices) {
                    val frame = decodedFrames[i]
                    val enqueued = conn.enqueueMailbox(frame, alreadyReserved = true)
                    if (!enqueued) {
                        for (j in (i + 1) until decodedFrames.size) {
                            globalBudget.release(decodedFrames[j].payload.size.toLong())
                        }
                        // 邮箱超限或全局预算满，触发慢客户端保护 (UC-L02)
                        conn.close(TcpCloseReasons.SLOW_CONSUMER, "Mailbox buffer exceeded", immediate = false)
                        return
                    }
                    framesDecodedThisTurn++
                }

                if (framesDecodedThisTurn >= config.maxFramesPerRotation) {
                    break
                }
            }

            if (conn.decoder.hasIncompleteFrame()) {
                // 记录不完整帧首字节到达时刻 (UC-L08)
                if (conn.incompleteFrameStartTimeMillis == null) {
                    conn.incompleteFrameStartTimeMillis = now
                }
            } else {
                conn.incompleteFrameStartTimeMillis = null
            }
        }

        // 调度工作池消费串行邮箱
        triggerMailboxProcessing(conn)
    }

    /**
     * 处理通道写出 (OP_WRITE, 契约 1 / UC-L05 公平调度)
     */
    private fun handleWrite(key: SelectionKey) {
        val conn = key.attachment() as? TcpConnection ?: return
        val sc = conn.channel
        val now = config.clock.currentTimeMillis()

        val written = try {
            conn.outboundQueue.writeToChannel(sc, config.maxBytesPerRotation, config.maxFramesPerRotation)
        } catch (e: Throwable) {
            conn.close(TcpCloseReasons.LOCAL_CLOSED, "Write failure: ${e.message}", immediate = true)
            cleanupConnection(conn)
            return
        }

        if (written > 0) {
            conn.recordWriteProgress(now)
        }

        // 若队列已完全清空
        if (!conn.outboundQueue.hasPendingData) {
            if (conn.state.get() == ConnectionState.CLOSING) {
                // 终态通知已排空，强制释放底层 channel (UC-L11)
                conn.forceCloseChannel()
                cleanupConnection(conn)
                return
            }
            // 取消写兴趣
            try {
                if (key.isValid) {
                    key.interestOps(key.interestOps() and SelectionKey.OP_WRITE.inv())
                }
            } catch (_: Throwable) {
            }
        }
    }

    /**
     * 定时周期检查与超时判死 (UC-H04 / UC-L06 / UC-L07 / UC-L08 / UC-L09 / UC-L11)
     */
    private fun performTimerChecks(now: Long) {
        val it = connections.values.iterator()
        while (it.hasNext()) {
            val conn = it.next()

            if (conn.isClosed) {
                it.remove()
                cleanupConnection(conn)
                continue
            }

            // 1. 握手超时 (建连 5 秒未完成握手，UC-H04)
            if (conn.state.get() == ConnectionState.AWAITING_HELLO) {
                if (now >= conn.handshakeDeadlineMillis) {
                    conn.close(TcpCloseReasons.HANDSHAKE_TIMEOUT, "Handshake timed out after 5s", immediate = false)
                    continue
                }
            }

            // 2. 入站判死 (10 秒无完整合法帧，UC-L06 / UC-L07 零散字节不刷新)
            if (now - conn.lastInboundTimeMillis >= config.idleDeadMillis) {
                conn.close(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Heartbeat timeout (10s idle)", immediate = false)
                continue
            }

            // 3. 不完整帧首字节起 10 秒硬截止 (UC-L08)
            val incompleteStart = conn.incompleteFrameStartTimeMillis
            if (incompleteStart != null && now - incompleteStart >= config.incompleteFrameTimeoutMillis) {
                conn.close(TcpCloseReasons.PROTOCOL_VIOLATION, "Incomplete frame timed out after 10s", immediate = false)
                continue
            }

            // 4. 停滞写出检查 (连续 10 秒零写出进展，UC-L09)
            if (conn.outboundQueue.hasPendingData && now - conn.lastWriteProgressTimeMillis >= config.stalledWriteTimeoutMillis) {
                conn.close(TcpCloseReasons.SLOW_CONSUMER, "Stalled write progress for 10s", immediate = false)
                continue
            }

            // 5. 终态收尾超时 (最多 1 秒，UC-L11)
            val closingStart = conn.closingStartTimeMillis
            if (closingStart != null && now - closingStart >= config.terminalFlushTimeoutMillis) {
                conn.forceCloseChannel()
                it.remove()
                cleanupConnection(conn)
                continue
            }
        }

        // 定期清理过期的 Scope
        receiptStore.cleanExpiredScopes(now)
    }

    /**
     * 精确一次归还未握手桶计数 (R3)
     */
    private fun returnUnhandshakedBucket(conn: TcpConnection) {
        if (conn.markUnhandshakedReturned()) {
            unhandshakedCount.decrementAndGet()
        }
    }

    /**
     * 连接终结与注销清理 (R3 / R02)
     */
    private fun cleanupConnection(conn: TcpConnection) {
        connections.remove(conn.connectionId)
        returnUnhandshakedBucket(conn)
        conn.notifyRoomDisconnect()
    }

    /**
     * 生成与席位绑定代次强绑定的 sessionValidator (R7 / UC-S08)
     */
    private fun makeSeatValidator(conn: TcpConnection, room: SharedRoom, seat: Int): () -> Boolean =
        { !conn.isClosed && conn.currentBindingGeneration == room.bindingGenerations[seat] }

    /**
     * 触发有界工作池串行处理指定连接的接收邮箱 (保持同连接单任务串行派发)
     */
    private fun triggerMailboxProcessing(conn: TcpConnection) {
        if (conn.isClosingOrClosed) return
        if (conn.mailboxSize == 0) return

        if (conn.mailboxProcessing.compareAndSet(false, true)) {
            try {
                workerPool.execute {
                    drainMailbox(conn)
                }
            } catch (_: Throwable) {
                conn.mailboxProcessing.set(false)
            }
        }
    }

    /**
     * 串行消费接收邮箱中的帧
     */
    private fun drainMailbox(conn: TcpConnection) {
        try {
            while (!conn.isClosingOrClosed) {
                val frame = conn.pollMailbox() ?: break
                val size = frame.payload.size.toLong()
                try {
                    processFrame(conn, frame)
                } finally {
                    globalBudget.release(size)
                }
            }
        } finally {
            conn.mailboxProcessing.set(false)
            // 重新检查在退出瞬间是否又有新帧进入
            if (conn.mailboxSize > 0 && !conn.isClosingOrClosed) {
                triggerMailboxProcessing(conn)
            }
        }
    }

    /**
     * 处理单条入站帧 (工作线程池执行)
     */
    private fun processFrame(conn: TcpConnection, frame: TcpFrame) {
        if (conn.isClosingOrClosed) return

        // 1. 握手阶段处理
        if (conn.state.get() == ConnectionState.AWAITING_HELLO) {
            if (frame.type != TcpWire.FRAME_TYPE_HELLO) {
                // 握手前收到业务请求即 PROTOCOL_VIOLATION (UC-H03)
                conn.close(TcpCloseReasons.PROTOCOL_VIOLATION, "Received frame type ${frame.type} before HELLO", immediate = false)
                return
            }

            handleHello(conn, frame)
            return
        }

        // 2. 握手已完成，处理各业务与控制帧
        when (frame.type) {
            TcpWire.FRAME_TYPE_HELLO -> {
                // 重复 HELLO 立即断开 (UC-H02)
                conn.close(TcpCloseReasons.PROTOCOL_VIOLATION, "Duplicate HELLO rejected", immediate = false)
            }
            TcpWire.FRAME_TYPE_PING -> {
                handlePing(conn, frame)
            }
            TcpWire.FRAME_TYPE_PONG -> {
                // 客户端返回 PONG，无需业务处理
            }
            TcpWire.FRAME_TYPE_CLOSE -> {
                // 客户端主动发起关闭通知
                conn.close(TcpCloseReasons.LOCAL_CLOSED, "Client requested CLOSE", immediate = true)
            }
            TcpWire.FRAME_TYPE_REQ -> {
                handleRequest(conn, frame)
            }
            else -> {
                // 服务端不接受入站 UPDATE 帧或未识别帧
                conn.close(TcpCloseReasons.PROTOCOL_VIOLATION, "Unexpected frame type ${frame.type}", immediate = false)
            }
        }
    }

    /**
     * 处理 HELLO 握手 (UC-H01 / UC-H05 / UC-H06 / UC-H07)
     */
    private fun handleHello(conn: TcpConnection, frame: TcpFrame) {
        val hello = try {
            TcpMessageCodec.parseHello(frame)
        } catch (e: TcpProtocolException) {
            conn.close(e.reasonCode, e.message, immediate = false)
            return
        }

        // 版本匹配核验 (UC-H05)
        if (hello.wireVersion != TcpWire.WIRE_VERSION || hello.businessVersion != TcpWire.BUSINESS_VERSION) {
            conn.close(TcpCloseReasons.VERSION_MISMATCH, "Version mismatch: wire=${hello.wireVersion}, biz=${hello.businessVersion}", immediate = false)
            return
        }

        val principal: String
        val token: String

        if (hello.lobbyResumeToken != null) {
            // 恢复大厅会话 (UC-H06 / R06: 认证已签发身份)
            token = hello.lobbyResumeToken!!
            if (!issuedLobbyTokens.containsKey(token)) {
                conn.close(TcpCloseReasons.PROTOCOL_VIOLATION, "Unknown lobbyResumeToken: $token", immediate = false)
                return
            }
            principal = "lobby:$token"
        } else {
            // 新会话首次建连
            token = "lbt-" + receiptStore.generateSecureToken()
            issuedLobbyTokens[token] = config.clock.currentTimeMillis()
            principal = "lobby:$token"
        }

        val now = config.clock.currentTimeMillis()
        // 恢复时继承现有仍有效的 scope，过期或首次则新建 (R06)
        val (lobbyScope, _) = receiptStore.getOrCreateScopeForPrincipal(principal, now, recordQueryTime = false)
        val epoch = "epoch-" + receiptStore.generateSecureToken()

        conn.setHandshakedIdentity(principal = principal, token = token, epoch = epoch)
        returnUnhandshakedBucket(conn)

        val welcome = TcpWelcome(
            wireVersion = TcpWire.WIRE_VERSION,
            businessVersion = TcpWire.BUSINESS_VERSION,
            connectionEpoch = epoch,
            lobbyResumeToken = token,
            lobbyScope = lobbyScope,
            limits = TcpLimits(
                maxInFlightRequests = config.maxInFlightRequestsPerMember,
                requestTimeoutMillis = 10000L,
                heartbeatIntervalMillis = config.heartbeatIntervalMillis,
                idleDeadMillis = config.idleDeadMillis
            )
        )

        val welcomeFrame = TcpMessageCodec.toFrame(welcome)
        conn.sendNormalFrame(welcomeFrame.toByteArray())
    }

    /**
     * 处理 PING 帧并立即回复回显 PONG (D5 / D12 / UC-L06)
     */
    private fun handlePing(conn: TcpConnection, frame: TcpFrame) {
        val ping = try {
            TcpMessageCodec.parsePing(frame)
        } catch (e: TcpProtocolException) {
            conn.close(e.reasonCode, e.message, immediate = false)
            return
        }

        val pong = TcpPong(nonce = ping.nonce)
        val pongFrame = TcpMessageCodec.toFrame(pong)
        conn.sendControlFrame(pongFrame.toByteArray())
    }

    /**
     * 处理 REQ 业务请求 (核心路由与幂等分发, Design §5 / UC-R01~R11)
     */
    private fun handleRequest(conn: TcpConnection, frame: TcpFrame) {
        val envelope = try {
            TcpMessageCodec.parseRequestEnvelope(frame)
        } catch (e: TcpProtocolException) {
            conn.close(e.reasonCode, e.message, immediate = false)
            return
        }

        // 外层与内层 requestId 一致性强校验 (D7 / 契约 2)
        try {
            envelope.validateRequestIdConsistency()
        } catch (e: TcpProtocolException) {
            conn.close(e.reasonCode, e.message, immediate = false)
            return
        }

        val principal = conn.logicalPrincipal ?: return
        val rawPayload = frame.payloadAsString()
        val api = envelope.request.api

        // D9 特殊控制查询: GET_REQUEST_SCOPE
        if (api == "GET_REQUEST_SCOPE") {
            handleGetRequestScope(conn, envelope)
            return
        }

        val isSideEffect = isSideEffectApi(api)
        val isLobby = conn.role == ConnectionRole.UNBOUND_LOBBY
        val key = IdempotencyKey(
            principal = principal,
            scope = envelope.requestScope,
            requestId = envelope.requestId
        )

        // 准入裁决
        val admission = receiptStore.acquireAdmission(
            key = key,
            request = envelope.request,
            rawPayload = rawPayload,
            isSideEffect = isSideEffect,
            isLobby = isLobby,
            nowMillis = config.clock.currentTimeMillis()
        )

        when (admission) {
            is AdmissionResult.Replay -> {
                // 重放原结果 (UC-R01 / MR-06 回执新鲜度联动)
                val originalResp = admission.completed.response
                val targetRoomId = originalResp.roomId ?: originalResp.spectatorBinding?.roomId
                val responseToSend = if (originalResp.success && targetRoomId != null) {
                    val room = registry.getRoom(targetRoomId)
                    if (room == null || room.closed) {
                        ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                    } else {
                        originalResp
                    }
                } else {
                    originalResp
                }
                sendResponse(conn, envelope.requestScope, envelope.requestId, responseToSend, admission.completed.grantedScope)
            }
            is AdmissionResult.WaitInFlight -> {
                // 关联同一在途 Future (UC-R02)
                admission.inFlight.future.whenComplete { resp, err ->
                    if (resp != null) {
                        sendResponse(conn, envelope.requestScope, envelope.requestId, resp, admission.inFlight.grantedScope.get())
                    } else {
                        val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        sendResponse(conn, envelope.requestScope, envelope.requestId, errResp)
                    }
                }
            }
            is AdmissionResult.Conflict -> {
                // 同键不同载荷/API 冲突 (D1 / UC-R03)
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.CONFLICT)
                sendResponse(conn, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.ScopeExpired -> {
                // Scope 已过期且无历史回执 (D3 / UC-R05)
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.REQUEST_SCOPE_EXPIRED)
                sendResponse(conn, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.ScopeInvalid -> {
                // 伪造或未知 Scope
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                sendResponse(conn, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.ServerBusy -> {
                // 容量饱和受理前拒绝 (D2 / UC-R06)
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.SERVER_BUSY)
                sendResponse(conn, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.Admitted -> {
                // 准入成功，执行真实业务路由 (R6: 以 IdempotencyKey 为键)
                conn.inFlightRequests[key] = admission.inFlight.future
                executeBusinessRequest(conn, key, envelope, admission.inFlight, isSideEffect, isLobby)
            }
        }
    }

    /**
     * GET_REQUEST_SCOPE 权威控制查询处理 (D9 / Design §5 / UC-R07 / R13)
     */
    private fun handleGetRequestScope(conn: TcpConnection, envelope: TcpRequestEnvelope) {
        val principal = conn.logicalPrincipal ?: return
        val now = config.clock.currentTimeMillis()
        if (receiptStore.isScopeQueryRateLimited(principal, now)) {
            val resp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.SERVER_BUSY)
            sendResponse(conn, envelope.requestScope, envelope.requestId, resp)
            return
        }
        val (scope, _) = receiptStore.getOrCreateScopeForPrincipal(principal, now)
        val resp = ApiResponse(requestId = envelope.requestId, success = true)
        sendResponse(conn, envelope.requestScope, envelope.requestId, resp, grantedScope = scope)
    }

    /**
     * 发送响应信封 (RSP)
     */
    private fun sendResponse(
        conn: TcpConnection,
        requestScope: String,
        requestId: String,
        response: ApiResponse,
        grantedScope: TcpScope? = null
    ) {
        val rspEnvelope = TcpResponseEnvelope(
            requestScope = requestScope,
            requestId = requestId,
            response = response,
            grantedScope = grantedScope
        )
        val frame = TcpMessageCodec.toFrame(rspEnvelope)
        conn.sendNormalFrame(frame.toByteArray())
    }

    /**
     * 派发执行真实业务请求并完成回执与订阅
     */
    private fun executeBusinessRequest(
        conn: TcpConnection,
        key: IdempotencyKey,
        envelope: TcpRequestEnvelope,
        inFlight: InFlightReceipt,
        isSideEffect: Boolean,
        isLobby: Boolean
    ) {
        val req = envelope.request

        when (req.api) {
            ApiTags.LIST_ROOMS -> {
                val summaries = registry.listSummaries()
                val resp = ApiResponse(requestId = req.requestId, success = true, rooms = summaries)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            }
            ApiTags.CREATE_ROOM -> {
                handleCreateRoom(conn, key, req, inFlight, isLobby)
            }
            ApiTags.JOIN_ROOM -> {
                handleJoinRoom(conn, key, req, inFlight, isLobby)
            }
            ApiTags.RESUME_SESSION -> {
                handleResumeSession(conn, key, req, inFlight, isLobby)
            }
            ApiTags.JOIN_SPECTATOR -> {
                handleJoinSpectator(conn, key, req, inFlight, isLobby)
            }
            ApiTags.RESUME_SPECTATOR -> {
                handleResumeSpectator(conn, key, req, inFlight, isLobby)
            }
            ApiTags.LEAVE_ROOM -> {
                handleLeaveRoom(conn, key, req, inFlight, isLobby)
            }
            ApiTags.GET_ROOM_INFO -> {
                val room = conn.activeRoom
                if (room == null || room.closed) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.getRoomInfo { !conn.isClosed }.whenComplete { info, err ->
                        val resp = if (err != null || info == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, roomInfo = info)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SET_READY -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.setReady(seat, req.ready ?: true, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.UPDATE_CONFIG -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat != 0) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val effectiveRoomConfig = req.roomConfig ?: (req.config?.let { ProtocolRoomConfig(matchConfig = it) } ?: ProtocolRoomConfig())
                    room.updateConfig(seat, effectiveRoomConfig, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.START_MATCH -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat != 0) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.startMatch(seat, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.NEXT_ROUND -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.nextRound(seat, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_CHOICE -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val choice = req.choice
                if (room == null || seat !in 0..3 || choice == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val engineChoice = EngineProtocolMapper.toEngineActionChoice(choice)
                    room.submitChoice(seat, engineChoice, makeSeatValidator(conn, room, seat)).whenComplete { engineReceipt, err ->
                        val resp = if (err != null || engineReceipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(engineReceipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_SHUFFLE_PLAN -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val plan = req.shufflePlan
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (plan == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val enginePlan = EngineProtocolMapper.toEngineShufflePlan(plan)
                    room.submitShufflePlan(seat, enginePlan, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_TILE_MARK -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val mark = req.tileMark
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (mark == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineTileMarkRequest(mark)
                    room.submitTileMark(seat, engineReq, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_SLEEVE_BLOOM -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val bloom = req.sleeveBloom
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (bloom == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineSleeveBloomRequest(bloom)
                    room.submitSleeveBloom(seat, engineReq, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val choice = req.sleeveBloomChoice
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (choice == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineSleeveBloomChoiceRequest(choice)
                    room.submitSleeveBloomChoice(seat, engineReq, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_BEAM_SWAP -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val swap = req.beamSwap
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (swap == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineBeamSwapRequest(swap)
                    room.submitBeamSwap(seat, engineReq, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_BEAM_SWAP_CHOICE -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val choice = req.beamSwapChoice
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (choice == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineBeamSwapChoiceRequest(choice)
                    room.submitBeamSwapChoice(seat, engineReq, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.CLAIM_CONTROL -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.claimControl(seat, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.RELEASE_CONTROL -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.releaseControl(seat, makeSeatValidator(conn, room, seat)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.RESUME_CURSOR -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.resume(seat, req.cursor ?: 0L, makeSeatValidator(conn, room, seat)).whenComplete { snap, err ->
                        val resp = if (err != null || snap == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, snapshot = EngineProtocolMapper.toProtocolSnapshot(snap))
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SNAPSHOT -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.snapshot(seat, makeSeatValidator(conn, room, seat)).whenComplete { snap, err ->
                        val resp = if (err != null || snap == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, snapshot = EngineProtocolMapper.toProtocolSnapshot(snap))
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.DECISION -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                if (room == null || seat !in 0..3) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.decision(seat, makeSeatValidator(conn, room, seat)).whenComplete { dec, err ->
                        val resp = if (err != null || dec == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, decision = EngineProtocolMapper.toProtocolDecision(dec))
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.PUBLIC_SNAPSHOT -> {
                val room = conn.activeRoom
                if (room == null || conn.role != ConnectionRole.SPECTATOR) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.publicSnapshot { !conn.isClosed }.whenComplete { snap, err ->
                        val resp = if (err != null || snap == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, publicSnapshot = snap)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.RESUME_PUBLIC -> {
                val room = conn.activeRoom
                val checkpoint = req.publicCheckpoint
                if (room == null || conn.role != ConnectionRole.SPECTATOR) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (checkpoint == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.resumePublic(checkpoint, { !conn.isClosed }).whenComplete { snap, err ->
                        val resp = if (err != null || snap == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, publicSnapshot = snap)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.QUERY_ROUND_RESULT -> {
                val room = conn.activeRoom
                val seat = conn.viewerSeat
                val roundId = req.roundId
                if (room == null || seat !in 0..3 || conn.role != ConnectionRole.PLAYER) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (roundId.isNullOrBlank()) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.queryRoundResultEvents(seat, roundId, makeSeatValidator(conn, room, seat)).whenComplete { events, err ->
                        val resp = if (err != null || events == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, events = EngineProtocolMapper.toProtocolEvents(events))
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.QUERY_PUBLIC_ROUND_RESULT -> {
                val room = conn.activeRoom
                val roundId = req.roundId
                if (room == null || conn.role != ConnectionRole.SPECTATOR) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else if (roundId.isNullOrBlank()) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    conn.inFlightRequests.remove(key)
                    sendResponse(conn, key.scope, key.requestId, resp)
                } else {
                    room.queryPublicRoundResultEvents(roundId, { !conn.isClosed }).whenComplete { events, err ->
                        val resp = if (err != null || events == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, publicEvents = events)
                        }
                        receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        conn.inFlightRequests.remove(key)
                        sendResponse(conn, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.CAPABILITIES -> {
                val caps = ProtocolAccessCapabilities(
                    protocolVersion = ApiProtocol.PROTOCOL_VERSION,
                    supportedPlayerCounts = setOf(4),
                    remoteTransport = true,
                    persistence = false,
                    resumption = true,
                    tileMarkSupported = true,
                    sleeveBloomSupported = true,
                    beamSwapSupported = true
                )
                val resp = ApiResponse(requestId = req.requestId, success = true, capabilities = caps)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            }
            else -> {
                // 未知 API
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNKNOWN_REQUEST_TYPE)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            }
        }
    }

    /**
     * 建房处理与原子订阅安装 (UC-S01 / R2 / MR-06 任务 D)
     */
    private fun handleCreateRoom(
        conn: TcpConnection,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val principal = conn.logicalPrincipal ?: "lobby:${conn.lobbyResumeToken ?: conn.connectionId}"
        val remoteIp = CreateRoomAdmission.extractRemoteIp(conn.remoteAddress)

        val outcome = roomAdmission.tryReserveOutcome(principal, remoteIp)
        val reservation = when (outcome) {
            is CreateRoomAdmission.Outcome.Accepted -> outcome.reservation
            is CreateRoomAdmission.Outcome.RateLimited -> {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.SERVER_BUSY)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
                return
            }
            is CreateRoomAdmission.Outcome.QuotaExceeded -> {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.SERVER_FULL)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
                return
            }
        }

        val room = try {
            val effectiveRoomConfig = req.roomConfig ?: (req.config?.let { ProtocolRoomConfig(matchConfig = it) } ?: ProtocolRoomConfig())
            registry.createRoom(
                hostPlayerName = req.playerName ?: "Host",
                initialConfig = effectiveRoomConfig,
                isSingleplayer = false,
                hostSeat = 0,
                hostDispatch = hostDispatchPools
            )
        } catch (ex: ServerApiException) {
            reservation.release()
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ex.code)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        } catch (t: Throwable) {
            reservation.release()
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        reservation.commit(room.roomId)
        bindPlayerToRoom(conn, room, 0, key, req, inFlight, isLobby)
    }

    /**
     * 加入房间处理与原子订阅安装 (UC-S01)
     */
    private fun handleJoinRoom(
        conn: TcpConnection,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        targetRoom.join(req.playerName ?: "Player", req.preferSeat, { !conn.isClosed }).whenComplete { pair, err ->
            if (err != null || pair == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_FULL)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            } else {
                val seat = pair.first
                bindPlayerToRoom(conn, targetRoom, seat, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 会话恢复处理与原子订阅安装 (UC-S01 / UC-S08)
     */
    private fun handleResumeSession(
        conn: TcpConnection,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        val token = req.token
        if (token.isNullOrBlank()) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        targetRoom.resumeSession(token, { !conn.isClosed }).whenComplete { res, err ->
            if (err != null || res == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            } else {
                bindPlayerToRoom(conn, targetRoom, res.seat, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 将玩家绑定到指定房间席位，并在同桌串行队列内原子获取基线快照与安装订阅 (UC-S01 / R7 / R06 / R15)
     */
    private fun bindPlayerToRoom(
        conn: TcpConnection,
        room: SharedRoom,
        seat: Int,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val playerPrincipal = "player:${room.roomId}:$seat"
        conn.bindPlayerRole(room, seat)

        // 顶号扫描：向持有同房间同席位的旧连接主动发出 CLOSE(DISPLACED_BY_NEW_CONNECTION) (UC-C01 / D4 / R8)
        for (other in connections.values) {
            if (other !== conn && other.activeRoom === room && other.role == ConnectionRole.PLAYER && other.viewerSeat == seat) {
                other.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Seat resumed by a new connection", immediate = false)
            }
        }

        val roleScope = receiptStore.issueRoleScope(playerPrincipal)
        inFlight.grantedScope.set(roleScope)

        // 1. 同桌串行队列内原子获取基线快照与安装订阅 (R15 / Design §6)
        val capturedSeatGen = conn.currentBindingGeneration
        room.snapshotAndSubscribe(seat, makeSeatValidator(conn, room, seat)) { update ->
            if (!conn.isClosingOrClosed && conn.currentBindingGeneration == capturedSeatGen) {
                val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
                val frame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate)
                conn.sendNormalFrame(frame.toByteArray())
            }
        }.whenComplete { pair, snapErr ->
            if (snapErr != null || pair == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
                return@whenComplete
            }

            val (snap, subHandle) = pair
            if (conn.isClosingOrClosed || conn.currentBindingGeneration != capturedSeatGen || room.closed || registry.getRoom(room.roomId) == null) {
                subHandle.close()
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
                return@whenComplete
            }
            conn.seatSubscription = subHandle

            // 通知房间席位已成功连接 (MR-06 任务 B)
            room.markSeatConnected(seat, capturedSeatGen)

            val protoSnap = EngineProtocolMapper.toProtocolSnapshot(snap)
            val resp = ApiResponse(
                requestId = req.requestId,
                success = true,
                roomId = room.roomId,
                seat = seat,
                resumeToken = room.resumeTokens[seat],
                snapshot = protoSnap
            )

            // 2. 存入已完成回执
            receiptStore.completeReceipt(key, inFlight, resp, grantedScope = roleScope, isLobby = isLobby)
            conn.inFlightRequests.remove(key)

            // 3. 将携带基线的 RSP 优先排入业务输出队列 (UC-S01)
            sendResponse(conn, key.scope, key.requestId, resp, grantedScope = roleScope)

            // 4. 房间状态更新订阅
            conn.roomSubscription = room.onRoomUpdate { info ->
                if (!conn.isClosingOrClosed && conn.currentBindingGeneration == capturedSeatGen) {
                    val frame = TcpMessageCodec.toRoomUpdateFrame(info)
                    conn.sendNormalFrame(frame.toByteArray())
                }
            }
        }
    }

    /**
     * 加入观战处理 (UC-S01)
     */
    private fun handleJoinSpectator(
        conn: TcpConnection,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        val specName = req.spectatorName ?: "Spectator"
        targetRoom.joinSpectator(specName, { !conn.isClosed }).whenComplete { res, err ->
            if (err != null || res == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            } else {
                bindSpectatorToRoom(conn, targetRoom, res.spectatorId, res.resumeToken, res.generation, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 恢复观战处理 (UC-S01)
     */
    private fun handleResumeSpectator(
        conn: TcpConnection,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        val specId = req.spectatorId ?: ""
        val token = req.token ?: ""
        targetRoom.resumeSpectator(specId, token, req.publicCheckpoint, { !conn.isClosed }).whenComplete { res, err ->
            if (err != null || res == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
            } else {
                val specToken = targetRoom.spectators[res.spectatorId]?.resumeToken ?: token
                bindSpectatorToRoom(conn, targetRoom, res.spectatorId, specToken, res.generation, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 绑定观战者并在同桌串行队列内原子获取公开快照基线与安装订阅 (UC-S01 / R06 / R15)
     */
    private fun bindSpectatorToRoom(
        conn: TcpConnection,
        room: SharedRoom,
        specId: String,
        specToken: String,
        generation: Long,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val spectatorPrincipal = "spectator:${room.roomId}:$specId"
        conn.bindSpectatorRole(room, specId, bindingGeneration = generation)

        // 顶号扫描：向持有同房间同观战者身份的旧连接主动发出 CLOSE(DISPLACED_BY_NEW_CONNECTION) (UC-C01 / D4 / R8)
        for (other in connections.values) {
            if (other !== conn && other.activeRoom === room && other.role == ConnectionRole.SPECTATOR && other.spectatorId == specId) {
                other.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Spectator session resumed by a new connection", immediate = false)
            }
        }

        val roleScope = receiptStore.issueRoleScope(spectatorPrincipal)
        inFlight.grantedScope.set(roleScope)

        // 1. 同桌串行队列内原子获取基线快照与安装订阅 (R15 / Design §6)
        val capturedGen = conn.currentBindingGeneration
        room.publicSnapshotAndSubscribe({ !conn.isClosed }) { pubUpdate ->
            if (!conn.isClosingOrClosed && conn.currentBindingGeneration == capturedGen) {
                val frameBytes = publicFrameCache.frameBytesFor(pubUpdate)
                conn.sendNormalFrame(frameBytes)
            }
        }.whenComplete { pair, snapErr ->
            if (snapErr != null || pair == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
                return@whenComplete
            }

            val (pubSnap, subHandle) = pair
            if (conn.isClosingOrClosed || conn.currentBindingGeneration != capturedGen || room.closed || registry.getRoom(room.roomId) == null) {
                subHandle.close()
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                conn.inFlightRequests.remove(key)
                sendResponse(conn, key.scope, key.requestId, resp)
                return@whenComplete
            }
            conn.publicSubscription = subHandle

            // 通知房间观战者已成功连接 (MR-06 任务 B)
            room.markSpectatorConnected(specId)

            val binding = SpectatorBinding(
                roomId = room.roomId,
                spectatorId = specId,
                resumeToken = specToken,
                snapshot = pubSnap
            )
            val resp = ApiResponse(
                requestId = req.requestId,
                success = true,
                spectatorBinding = binding,
                publicSnapshot = pubSnap
            )

            // 2. 存入已完成回执
            receiptStore.completeReceipt(key, inFlight, resp, grantedScope = roleScope, isLobby = isLobby)
            conn.inFlightRequests.remove(key)

            // 3. 将包含基线的 RSP 先排入出站队列 (UC-S01)
            sendResponse(conn, key.scope, key.requestId, resp, grantedScope = roleScope)

            // 4. 房间状态更新订阅
            conn.roomSubscription = room.onRoomUpdate { info ->
                if (!conn.isClosingOrClosed && conn.currentBindingGeneration == capturedGen) {
                    val frame = TcpMessageCodec.toRoomUpdateFrame(info)
                    conn.sendNormalFrame(frame.toByteArray())
                }
            }
        }
    }

    /**
     * 显式离开房间处理
     */
    private fun handleLeaveRoom(
        conn: TcpConnection,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val room = conn.activeRoom
        if (room == null || room.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
            return
        }

        val leaveFuture = if (conn.role == ConnectionRole.SPECTATOR && conn.spectatorId != null) {
            room.leaveSpectator(conn.spectatorId!!, { !conn.isClosed })
        } else {
            room.leave(conn.viewerSeat, makeSeatValidator(conn, room, conn.viewerSeat))
        }

        leaveFuture.whenComplete { receipt, err ->
            conn.unbindToLobbyRole()
            if (room.closed) {
                try {
                    val sessionId = room.tableHost.publicSessionId
                    publicFrameCache.releaseSession(sessionId)
                } catch (_: Throwable) {
                }
            }

            val resp = if (err != null || receipt == null) {
                ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
            } else {
                ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
            }
            receiptStore.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            conn.inFlightRequests.remove(key)
            sendResponse(conn, key.scope, key.requestId, resp)
        }
    }

    /**
     * 判定是否属于副作用 API
     */
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
        ApiTags.SUBMIT_BEAM_SWAP,
        ApiTags.SUBMIT_BEAM_SWAP_CHOICE,
        ApiTags.CLAIM_CONTROL,
        ApiTags.RELEASE_CONTROL -> true
        else -> false
    }

    /**
     * 执行房间巡检回收循环（单线程 RoomPatrol 定时调度，MR-06 任务 E）
     */
    internal fun performRoomPatrol(now: Long = registry.clock.nowMillis()) {
        // 1. 同步清账与对账 (任务 D)
        roomAdmission.reconcile(registry)

        // 2. 存活房间快照读取，锁外筛选候选 (只筛选，不做最终裁决)
        val activeRooms = registry.activeRooms()
        for (room in activeRooms) {
            if (room.closed) {
                try {
                    publicFrameCache.releaseSession(room.tableHost.publicSessionId)
                } catch (_: Throwable) {
                }
                continue
            }

            // 2.1 对账连接真值：以连接表为准，清除无人认领的 seatConnected 与 member.isConnected (R4 兜底)
            val sampledSeatGenerations = mutableMapOf<Int, Long>()
            for (seat in 0..3) {
                sampledSeatGenerations[seat] = room.bindingGenerations[seat]
            }
            room.reconcileConnectivityOnQueue(
                aliveSeats = emptyMap(),
                aliveSpectatorIds = emptySet(),
                sampledSeatGenerations = sampledSeatGenerations,
                connectivitySupplier = { queryLiveConnectivity(room) }
            )

            val idleSince = room.idleSinceMillis ?: continue
            val capturedEpoch = room.idleEpoch
            val everConnected = room.everConnectedHuman
            val curLifecycle = room.lifecycle

            val creatorActive = isCreatorActiveForRoom(room.roomId)

            val ttl = ttlFor(curLifecycle, everConnected, creatorActive, config.retentionPolicy)
            if (ttl <= 0L || now - idleSince < ttl) {
                continue
            }

            // 3. 候选命中，进入目标房间权威队列执行最终原子裁决
            recycleRoomIfIdle(room, capturedEpoch, now, creatorActive)
        }
    }

    /**
     * 查询指定房间连接真值快照
     */
    private fun queryLiveConnectivity(room: SharedRoom): Pair<Map<Int, Long>, Set<String>> {
        val aliveSeats = mutableMapOf<Int, Long>()
        val aliveSpectators = mutableSetOf<String>()
        for (conn in connections.values) {
            if (!conn.isClosingOrClosed && conn.activeRoom === room) {
                if (conn.role == ConnectionRole.PLAYER && conn.viewerSeat in 0..3) {
                    aliveSeats[conn.viewerSeat] = conn.currentBindingGeneration
                } else if (conn.role == ConnectionRole.SPECTATOR && conn.spectatorId != null) {
                    aliveSpectators.add(conn.spectatorId!!)
                }
            }
        }
        return Pair(aliveSeats, aliveSpectators)
    }

    /**
     * 查询指定房间创建者的连接是否存活
     */
    private fun isCreatorActiveForRoom(roomId: String): Boolean {
        val creatorPrincipal = roomAdmission.getCreatorPrincipal(roomId) ?: return false
        return connections.values.any { conn ->
            !conn.isClosingOrClosed && conn.logicalPrincipal == creatorPrincipal
        }
    }

    /**
     * 在目标房间权威队列提交回收裁决并执行清理
     */
    private fun recycleRoomIfIdle(
        room: SharedRoom,
        capturedEpoch: Long,
        now: Long,
        creatorActive: Boolean
    ) {
        room.closeIfIdleOnQueue(
            policy = config.retentionPolicy,
            capturedIdleEpoch = capturedEpoch,
            nowMillis = now,
            creatorActive = creatorActive,
            creatorActiveSupplier = { isCreatorActiveForRoom(room.roomId) }
        ).whenComplete { closed, err ->
            if (err == null && closed == true) {
                onRoomRecycled(room)
            }
        }
    }

    /**
     * 房间回收成功后的服务端资源清理 (任务 E)
     */
    private fun onRoomRecycled(room: SharedRoom) {
        val roomId = room.roomId
        // 0. 释放该会话全部公开更新帧缓存条目 (MR-07b)
        try {
            val sessionId = room.tableHost.publicSessionId
            publicFrameCache.releaseSession(sessionId)
        } catch (_: Throwable) {
        }

        // 1. 注销
        registry.removeRoom(roomId)

        // 2. 对关联连接发送 ROOM_CLOSED 关闭
        for (conn in connections.values) {
            if (conn.activeRoom === room) {
                conn.close(TcpCloseReasons.ROOM_CLOSED, "Room recycled (idle TTL)", immediate = false)
            }
        }

        // 3. CreateRoomAdmission 清账
        roomAdmission.onRoomClosed(roomId)
    }

    /**
     * 停止服务器并释放所有连接与资源 (UC-C04 / R01 / R11)
     */
    override fun close() {
        if (!isClosed.compareAndSet(false, true)) {
            return
        }
        isRunning.set(false)

        try {
            selector.wakeup()
        } catch (_: Throwable) {
        }

        // 1. 停止权威房间 Ticker 与 RoomPatrol 调度器 (R01 / MR-06 R11)
        patrolExecutor?.let { patrol ->
            try {
                patrol.shutdown()
                patrol.awaitTermination(500, TimeUnit.MILLISECONDS)
                patrol.shutdownNow()
            } catch (_: Throwable) {
            }
        }
        patrolExecutor = null

        tickerExecutor?.let { ticker ->
            try {
                ticker.shutdown()
                ticker.awaitTermination(500, TimeUnit.MILLISECONDS)
                ticker.shutdownNow()
            } catch (_: Throwable) {
            }
        }
        tickerExecutor = null

        // 2. 关闭网络监听服务
        try {
            if (::serverChannel.isInitialized && serverChannel.isOpen) {
                serverChannel.close()
            }
        } catch (_: Throwable) {
        }

        // 3. 关闭所有活跃连接并下发 SERVER_SHUTDOWN (UC-C04 / R11: 经控制帧队列发出，绝不 busy 等待写出)
        for (conn in connections.values) {
            try {
                val closeFrame = TcpMessageCodec.toFrame(TcpClose(reason = TcpCloseReasons.SERVER_SHUTDOWN, message = "Server is shutting down"))
                conn.sendControlFrame(closeFrame.toByteArray())
                if (conn.channel.isOpen) {
                    conn.outboundQueue.writeToChannel(conn.channel)
                }
            } catch (_: Throwable) {
            }
            try {
                conn.forceCloseChannel()
            } catch (_: Throwable) {
            }
        }
        connections.clear()

        // 4. 关闭房间注册表 (释放所有 SharedRoom 宿主) (R11)
        try {
            registry.close()
        } catch (_: Throwable) {
        }

        // 4.1 清空公开更新帧编码缓存 (MR-07b)
        publicFrameCache.clear()

        // 4.5 关闭共享派发与完成线程池 (MR-07a)
        hostDispatchPools?.let { pools ->
            try {
                lastCloseRemainingTasks = pools.close(config.sharedPoolsCloseDeadlineMillis)
            } catch (_: Throwable) {
            }
        }
        hostDispatchPools = null

        // 5. 关闭网络通道与 Selector
        try {
            if (::selector.isInitialized && selector.isOpen) {
                selector.close()
            }
        } catch (_: Throwable) {
        }

        // 6. 关闭工作池并等待终止 (R11)
        if (::workerPool.isInitialized) {
            try {
                workerPool.shutdown()
                workerPool.awaitTermination(1, TimeUnit.SECONDS)
                workerPool.shutdownNow()
            } catch (_: Throwable) {
            }
        }

        // 7. 等待 I/O 线程退出 (R11)
        if (::ioThread.isInitialized) {
            try {
                ioThread.join(1000)
            } catch (_: Throwable) {
            }
        }

        // 8. 重置全局预算与未握手计数
        globalBudget.reset()
        unhandshakedCount.set(0)
    }

    /**
     * 通道指标聚合快照（MR-07a 任务 D/F 供测试与测量工具读取）
     */
    data class ChannelMetricsAggregate(
        val activeRooms: Int,
        val totalQueuedItems: Int,
        val totalQueuedBytes: Long,
        val totalOverflowCount: Long,
        val totalScheduleRejectCount: Long,
        val anyNeedsRecovery: Boolean,
        val maxWaitTimeMillis: Double
    )

    /**
     * 聚合当前所有存活房间的席位与公开派发通道指标
     */
    fun aggregateChannelMetrics(): ChannelMetricsAggregate {
        var totalItems = 0
        var totalBytes = 0L
        var totalOverflow = 0L
        var totalReject = 0L
        var anyRecovery = false
        var maxWait = 0.0
        val rooms = registry.activeRooms()
        for (room in rooms) {
            val host = room.tableHost
            for (seat in 0..3) {
                val ch = host.getSeatChannel(seat) ?: continue
                totalItems += ch.queuedItems
                totalBytes += ch.queuedBytes
                totalOverflow += ch.overflowCount
                totalReject += ch.scheduleRejectCount
                if (ch.needsRecovery) anyRecovery = true
                if (ch.maxWaitTimeMillis > maxWait) maxWait = ch.maxWaitTimeMillis
            }
            val pubCh = host.getPublicDispatchChannel()
            totalItems += pubCh.queuedItems
            totalBytes += pubCh.queuedBytes
            totalOverflow += pubCh.overflowCount
            totalReject += pubCh.scheduleRejectCount
            if (pubCh.needsRecovery) anyRecovery = true
            if (pubCh.maxWaitTimeMillis > maxWait) maxWait = pubCh.maxWaitTimeMillis
        }
        return ChannelMetricsAggregate(
            activeRooms = rooms.size,
            totalQueuedItems = totalItems,
            totalQueuedBytes = totalBytes,
            totalOverflowCount = totalOverflow,
            totalScheduleRejectCount = totalReject,
            anyNeedsRecovery = anyRecovery,
            maxWaitTimeMillis = maxWait
        )
    }
}

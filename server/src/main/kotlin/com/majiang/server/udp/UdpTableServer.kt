package com.majiang.server.udp

import com.majiang.access.PlayerUpdate
import com.majiang.control.DisconnectPolicy
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.RoomSummary
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.room.RoomRegistry
import com.majiang.timing.TimeControlConfig
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.Fragmenter
import com.majiang.transport.udp.HeartbeatPacer
import com.majiang.transport.udp.ResendResponse
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpProtocol
import com.majiang.transport.udp.UdpScheduler
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.net.SocketException
import java.security.SecureRandom
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentSkipListMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ThreadFactory
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * UDP 权威房间服务器 (UdpTableServer)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §3、§7，遵循 V4 规范：
 * 1. 监听 [DatagramSocket]（支持 host/port，port 0 绑定临时端口），纯非阻塞多客户端服务；
 * 2. 帧分发：
 *    - REQ：未绑定 (connectionId=0) 处理大厅期接口；已绑定通过 connectionId 路由到活跃 session。
 *      connectionId 校验失败静默丢弃；业务入参反序列化失败回 MALFORMED_REQUEST RSP；
 *      幂等去重命中直接重放已生成回执；
 *    - PING -> PONG：回显 clientMillis 并注入 serverMillis；
 *    - RESEND：路由至 session 重传窗口，窗口内补发，窗口外全量自愈；
 *    - PUSH：订阅 TableHost 更新，按座位脱敏投影，大包 (>1090B) 经 [Fragmenter] 自动分片；
 *    - KICK：顶号、解散或停服时下发终结通知；顶号时先向旧 connectionId 发 KICK 再建新绑定；
 * 3. 50ms 权威时钟 ticker 驱动各房间 TableHost.tick()，单房间异常隔离；
 * 4. 10s 入站超时检测判死，异步通过 [teardownExecutor] 清理并触发宿主掉线 AI 托管；
 * 5. 房间上限 100，已关闭房间回收，[close] 幂等释放。
 */
class UdpTableServer @JvmOverloads constructor(
    val bindHost: String = "127.0.0.1",
    val port: Int = 0,
    val timeControl: TimeControlConfig = TimeControlConfig(),
    val disconnectPolicy: DisconnectPolicy = DisconnectPolicy(),
    val clock: UdpClock = UdpClock.SYSTEM,
    scheduler: UdpScheduler? = null
) : AutoCloseable {

    private val isClosing = AtomicBoolean(false)
    private val isClosed = AtomicBoolean(false)
    private var socket: DatagramSocket? = null
    private var receiverThread: Thread? = null

    private val rooms = ConcurrentHashMap<String, UdpRoom>()

    /** 与进程内绑定共享的权威房间注册表：UDP 传输适配器仅包装其中的 SharedRoom */
    val registry: RoomRegistry = RoomRegistry(maxRooms = MAX_ROOMS)

    private val sessionsByConnectionId = ConcurrentHashMap<Int, UdpClientSession>()
    private data class TerminatedSessionEntry(
        val session: UdpClientSession,
        val timestamp: Long
    )
    private val terminatedSessions = ConcurrentHashMap<Int, TerminatedSessionEntry>()
    private val unattachedOutboundSeq = java.util.concurrent.atomic.AtomicInteger(1)
    private val random = SecureRandom()

    @Volatile
    var outboundFrameListener: ((frame: UdpFrame, address: SocketAddress) -> Unit)? = null

    private data class LobbyKey(val address: SocketAddress, val requestId: String)
    private data class CompletedLobbyEntry(
        val connectionId: Int,
        val response: ApiResponse,
        val rawPayload: String,
        val api: String,
        val request: ApiRequest,
        val timestamp: Long = 0L
    )
    private data class InFlightLobbyEntry(
        val future: CompletableFuture<Pair<Int, ApiResponse>>,
        val rawPayload: String,
        val api: String,
        val request: ApiRequest,
        val timestamp: Long = 0L
    )
    private val lobbyCompletedRequests = ConcurrentHashMap<LobbyKey, CompletedLobbyEntry>()
    private val lobbyInFlightRequests = ConcurrentHashMap<LobbyKey, InFlightLobbyEntry>()

    data class InFlightKick(
        val connectionId: Int,
        val seq: Int,
        val frame: UdpFrame,
        val address: SocketAddress,
        val future: CompletableFuture<Unit> = CompletableFuture(),
        var attempts: Int = 1,
        var retryCancellable: Cancellable? = null
    )
    private val inFlightKicks = ConcurrentHashMap<Int, InFlightKick>()

    /**
     * 在途可靠房间更新（与对局推送游标完全分离）：
     * 每会话至多一条未确认的完整权威房间视图，ACK 收敛；重试有界；
     * 重试耗尽后保留最新视图等待 ticker 低频刷新，保证丢失最后一条房间更新
     * 也能在不依赖用户操作或 UI 轮询的前提下恢复。
     */
    data class InFlightRoomUpdate(
        val connectionId: Int,
        val seq: Int,
        val version: Long,
        val frame: UdpFrame,
        val future: CompletableFuture<Unit> = CompletableFuture(),
        @Volatile var attempts: Int = 1,
        @Volatile var retryCancellable: Cancellable? = null,
        @Volatile var lastSendMillis: Long = 0L
    )
    private val inFlightRoomUpdates = ConcurrentHashMap<Int, InFlightRoomUpdate>()
    private val highestSentRoomVersion = ConcurrentHashMap<Int, Long>()

    /**
     * 在途可靠公开流推送 (PUBLIC_UPDATE PUSH，仅观战会话)：
     * 与玩家对局推送、ROOM_UPDATE、KICK 的在途状态完全分离：
     * 1. 每条公开更新按稳定消息 seq 分片/重试，ACK 对应消息 seq 收敛该条目；
     * 2. 有界待发集合（窗口容量上限），超限淘汰最老待发（仍保留在会话公开重传窗口供 RESEND 被动补发）；
     * 3. 防回退：仅接受游标单调前进的新更新，旧回调不得覆盖新待发条目；
     * 4. 重试预算耗尽后保留条目由 ticker 低频刷新兜底，保证最后一条公开更新
     *    在无后续用户动作时仍可恢复；close/kick/替换确定性取消全部任务。
     */
    data class InFlightPublicPush(
        val connectionId: Int,
        val seq: Int,
        val cursor: Long,
        val frame: UdpFrame,
        val future: CompletableFuture<Unit> = CompletableFuture(),
        @Volatile var attempts: Int = 1,
        @Volatile var retryCancellable: Cancellable? = null,
        @Volatile var lastSendMillis: Long = 0L
    )
    private val inFlightPublicPushes = ConcurrentHashMap<Int, ConcurrentSkipListMap<Int, InFlightPublicPush>>()
    private val highestSentPublicCursor = ConcurrentHashMap<Int, Long>()

    /**
     * 可靠下发完整权威房间视图 (ROOM_UPDATE PUSH)：
     * 有界指数退避重试直至客户端 ACK；被更新版本替换时旧条目立即作废。
     * 防乱序与防回退：仅允许单调递增的新版本覆盖待发记录，拒绝较旧版本并发回调覆盖新版本。
     * 连接生命周期与待发状态原子联动：在 session 监视器锁内原子校验存活状态与更新 watermark，
     * 会话一旦关闭或被踢除，绝不重新注册新条目或重发定时器。
     */
    fun sendRoomUpdate(session: UdpClientSession, roomInfo: ProtocolRoomInfo) {
        if (session.isClosed || session.isDisplaced || isClosed.get()) return
        val connId = session.connectionId
        val entryToSend: InFlightRoomUpdate?
        val oldToCancel: InFlightRoomUpdate?
        synchronized(session) {
            if (session.isClosed || session.isDisplaced || isClosed.get() ||
                sessionsByConnectionId[connId] !== session || inFlightKicks.containsKey(connId)) {
                return
            }
            val prev = highestSentRoomVersion[connId] ?: 0L
            if (roomInfo.version < prev) {
                // 旧版本并发回调：不得覆盖新版本待发！
                return
            }
            highestSentRoomVersion[connId] = roomInfo.version

            val payload = ApiMessageCodec.encodeRoomUpdatePush(roomInfo).toByteArray(Charsets.UTF_8)
            val seq = session.nextOutboundSeq()
            val frame = UdpFrame(
                packetType = UdpPacketType.PUSH,
                connectionId = session.connectionId,
                seq = seq,
                ackSeq = 0,
                payload = payload
            )
            val entry = InFlightRoomUpdate(
                connectionId = session.connectionId,
                seq = seq,
                version = roomInfo.version,
                frame = frame,
                attempts = 1,
                lastSendMillis = clock.currentTimeMillis()
            )
            oldToCancel = inFlightRoomUpdates.put(session.connectionId, entry)
            entryToSend = entry
        }
        if (oldToCancel != null) {
            terminateRoomUpdate(oldToCancel)
        }
        if (entryToSend != null) {
            send(entryToSend.frame, session.remoteAddress, session, useStableSeq = true)
            scheduleRoomUpdateRetry(entryToSend, attempt = 1, delay = ROOM_UPDATE_RETRY_BASE_MILLIS)
        }
    }

    private fun scheduleRoomUpdateRetry(entry: InFlightRoomUpdate, attempt: Int, delay: Long) {
        if (entry.future.isDone || isClosed.get()) return
        val session = sessionsByConnectionId[entry.connectionId]
        if (session == null || session.isClosed || session.isDisplaced) {
            purgeRoomUpdate(entry)
            return
        }
        try {
            val cancellable = effectiveScheduler.schedule(delay) {
                if (inFlightRoomUpdates[entry.connectionId] !== entry || entry.future.isDone) return@schedule
                if (isClosed.get()) return@schedule
                val s = sessionsByConnectionId[entry.connectionId]
                if (s == null || s.isClosed || s.isDisplaced) {
                    purgeRoomUpdate(entry)
                    return@schedule
                }
                if (attempt < ROOM_UPDATE_MAX_ATTEMPTS) {
                    entry.attempts = attempt + 1
                    entry.lastSendMillis = clock.currentTimeMillis()
                    send(entry.frame, s.remoteAddress, s, useStableSeq = true)
                    scheduleRoomUpdateRetry(entry, attempt + 1, delay * 2)
                }
                // 重试预算耗尽：保留未确认条目，由 ticker 低频刷新继续兜底
            }
            entry.retryCancellable = cancellable
            if (entry.future.isDone || inFlightRoomUpdates[entry.connectionId] !== entry || isClosed.get()) {
                cancellable.cancel()
            }
        } catch (_: RejectedExecutionException) {
        }
    }

    private fun terminateRoomUpdate(entry: InFlightRoomUpdate) {
        entry.retryCancellable?.cancel()
        entry.future.complete(Unit)
    }

    private fun purgeRoomUpdate(entry: InFlightRoomUpdate): Boolean {
        val removed = inFlightRoomUpdates.remove(entry.connectionId, entry)
        if (removed) {
            terminateRoomUpdate(entry)
        }
        return removed
    }

    private fun cancelAndPurgeRoomUpdate(connectionId: Int): InFlightRoomUpdate? {
        val entry = inFlightRoomUpdates.remove(connectionId)
        if (entry != null) {
            terminateRoomUpdate(entry)
        }
        return entry
    }

    private fun terminateKick(entry: InFlightKick) {
        entry.retryCancellable?.cancel()
        if (!entry.future.isDone) {
            entry.future.complete(Unit)
        }
    }

    private fun purgeKick(entry: InFlightKick): Boolean {
        val removed = inFlightKicks.remove(entry.connectionId, entry)
        if (removed) {
            terminateKick(entry)
        }
        return removed
    }

    private fun cancelAndPurgeKick(connectionId: Int): InFlightKick? {
        val entry = inFlightKicks.remove(connectionId)
        if (entry != null) {
            terminateKick(entry)
        }
        return entry
    }

    private fun terminateSession(session: UdpClientSession) {
        synchronized(session) {
            session.close()
            sessionsByConnectionId.remove(session.connectionId, session)
            cancelAndPurgeRoomUpdate(session.connectionId)
            cancelAndPurgeKick(session.connectionId)
            cancelAndPurgePublicPushes(session.connectionId)
            highestSentRoomVersion.remove(session.connectionId)
            highestSentPublicCursor.remove(session.connectionId)
        }
    }

    /**
     * ticker 周期兜底：对重试预算耗尽仍未 ACK 的房间更新限速重发（会话仍存活时）。
     * 单会话单条在途记录，状态有界；限速间隔与心跳同量级。
     */
    private fun refreshPendingRoomUpdates(now: Long) {
        for (entry in inFlightRoomUpdates.values) {
            if (now - entry.lastSendMillis < ROOM_UPDATE_REFRESH_MILLIS) continue
            val session = sessionsByConnectionId[entry.connectionId]
            if (session == null || session.isClosed || session.isDisplaced) {
                purgeRoomUpdate(entry)
                continue
            }
            entry.attempts = 1
            entry.lastSendMillis = now
            send(entry.frame, session.remoteAddress, session, useStableSeq = true)
            scheduleRoomUpdateRetry(entry, attempt = 1, delay = ROOM_UPDATE_RETRY_BASE_MILLIS)
        }
    }

    /**
     * 可靠下发公开流更新 (PUBLIC_UPDATE PUSH)：观战会话专用。
     * 会话监视器锁内原子校验存活状态与公开游标 watermark 并登记待发条目；
     * 发送、重试调度与 Future 完成在临界区外执行。绝不复用玩家 mapper/编码路径。
     */
    fun sendPublicUpdate(session: UdpClientSession, update: ProtocolPublicUpdate) {
        if (session.isClosed || session.isDisplaced || isClosed.get()) return
        val connId = session.connectionId
        var entryToSend: InFlightPublicPush? = null
        synchronized(session) {
            if (session.isClosed || session.isDisplaced || isClosed.get() ||
                sessionsByConnectionId[connId] !== session || inFlightKicks.containsKey(connId)) {
                return
            }
            val prevCursor = highestSentPublicCursor[connId] ?: -1L
            if (update.snapshot.cursor <= prevCursor) {
                // 旧游标并发回调：不得覆盖新待发条目
                return
            }
            highestSentPublicCursor[connId] = update.snapshot.cursor

            val payload = ApiMessageCodec.encodePublicUpdatePush(update).toByteArray(Charsets.UTF_8)
            session.recordPublicPush(
                cursor = update.snapshot.cursor,
                fromCursor = update.fromCursor,
                payload = payload
            )
            val seq = session.nextOutboundSeq()
            val frame = UdpFrame(
                packetType = UdpPacketType.PUSH,
                connectionId = connId,
                seq = seq,
                ackSeq = 0,
                payload = payload
            )
            val entry = InFlightPublicPush(
                connectionId = connId,
                seq = seq,
                cursor = update.snapshot.cursor,
                frame = frame,
                attempts = 1,
                lastSendMillis = clock.currentTimeMillis()
            )
            val pending = inFlightPublicPushes.computeIfAbsent(connId) { ConcurrentSkipListMap() }
            // 有界待发：超窗口容量时淘汰最老待发（公开重传窗口仍保留其载荷供 RESEND 补发）
            while (pending.size >= UdpProtocol.RETRANSMIT_WINDOW_SIZE) {
                val eldest = pending.pollFirstEntry()
                if (eldest != null) {
                    terminatePublicPush(eldest.value)
                } else {
                    break
                }
            }
            pending[seq] = entry
            entryToSend = entry
        }
        entryToSend?.let { entry ->
            send(entry.frame, session.remoteAddress, session, useStableSeq = true)
            schedulePublicPushRetry(entry, attempt = 1, delay = PUBLIC_UPDATE_RETRY_BASE_MILLIS)
        }
    }

    private fun schedulePublicPushRetry(entry: InFlightPublicPush, attempt: Int, delay: Long) {
        if (entry.future.isDone || isClosed.get()) return
        val session = sessionsByConnectionId[entry.connectionId]
        if (session == null || session.isClosed || session.isDisplaced) {
            purgePublicPush(entry)
            return
        }
        try {
            val cancellable = effectiveScheduler.schedule(delay) {
                if (!isPublicPushPending(entry) || entry.future.isDone) return@schedule
                if (isClosed.get()) return@schedule
                val s = sessionsByConnectionId[entry.connectionId]
                if (s == null || s.isClosed || s.isDisplaced) {
                    purgePublicPush(entry)
                    return@schedule
                }
                if (attempt < PUBLIC_UPDATE_MAX_ATTEMPTS) {
                    entry.attempts = attempt + 1
                    entry.lastSendMillis = clock.currentTimeMillis()
                    send(entry.frame, s.remoteAddress, s, useStableSeq = true)
                    schedulePublicPushRetry(entry, attempt + 1, delay * 2)
                }
                // 重试预算耗尽：保留未确认条目，由 ticker 低频刷新继续兜底
            }
            entry.retryCancellable = cancellable
            if (entry.future.isDone || !isPublicPushPending(entry) || isClosed.get()) {
                cancellable.cancel()
            }
        } catch (_: RejectedExecutionException) {
        }
    }

    private fun isPublicPushPending(entry: InFlightPublicPush): Boolean =
        inFlightPublicPushes[entry.connectionId]?.get(entry.seq) === entry

    private fun terminatePublicPush(entry: InFlightPublicPush) {
        entry.retryCancellable?.cancel()
        if (!entry.future.isDone) {
            entry.future.complete(Unit)
        }
    }

    private fun purgePublicPush(entry: InFlightPublicPush): Boolean {
        val removed = inFlightPublicPushes[entry.connectionId]?.remove(entry.seq, entry) ?: false
        if (removed) {
            terminatePublicPush(entry)
        }
        return removed
    }

    private fun cancelAndPurgePublicPushes(connectionId: Int) {
        val pending = inFlightPublicPushes.remove(connectionId) ?: return
        for (entry in pending.values) {
            terminatePublicPush(entry)
        }
    }

    /**
     * ticker 周期兜底：对重试预算耗尽仍未 ACK 的公开更新限速重发（会话仍存活时）。
     * 状态有界（窗口容量上限）；限速间隔与房间更新兜底同量级。
     */
    private fun refreshPendingPublicPushes(now: Long) {
        for (pending in inFlightPublicPushes.values) {
            for (entry in pending.values) {
                if (now - entry.lastSendMillis < PUBLIC_UPDATE_REFRESH_MILLIS) continue
                val session = sessionsByConnectionId[entry.connectionId]
                if (session == null || session.isClosed || session.isDisplaced) {
                    purgePublicPush(entry)
                    continue
                }
                entry.attempts = 1
                entry.lastSendMillis = now
                send(entry.frame, session.remoteAddress, session, useStableSeq = true)
                schedulePublicPushRetry(entry, attempt = 1, delay = PUBLIC_UPDATE_RETRY_BASE_MILLIS)
            }
        }
    }

    private fun recordLobbyCompleted(key: LobbyKey, entry: CompletedLobbyEntry) {
        val now = clock.currentTimeMillis()
        if (lobbyCompletedRequests.size >= 1024) {
            val it = lobbyCompletedRequests.entries.iterator()
            while (it.hasNext()) {
                val item = it.next()
                if (now - item.value.timestamp > 60000L) {
                    it.remove()
                }
            }
            if (lobbyCompletedRequests.size >= 1024) {
                val it2 = lobbyCompletedRequests.keys.iterator()
                if (it2.hasNext()) {
                    lobbyCompletedRequests.remove(it2.next())
                }
            }
        }
        lobbyCompletedRequests[key] = entry
    }

    private fun recordTerminatedSession(connectionId: Int, session: UdpClientSession) {
        val now = clock.currentTimeMillis()
        val it = terminatedSessions.entries.iterator()
        while (it.hasNext()) {
            val item = it.next()
            if (now - item.value.timestamp > 60000L) {
                it.remove()
            }
        }
        if (terminatedSessions.size >= 1024) {
            val it2 = terminatedSessions.keys.iterator()
            if (it2.hasNext()) {
                terminatedSessions.remove(it2.next())
            }
        }
        terminatedSessions[connectionId] = TerminatedSessionEntry(session, now)
    }

    private val ticker: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor(
        ThreadFactory { r -> Thread(r, "UdpTableServer-Ticker").apply { isDaemon = true } }
    )

    private val teardownExecutor: ExecutorService = Executors.newSingleThreadExecutor(
        ThreadFactory { r -> Thread(r, "UdpTableServer-Teardown").apply { isDaemon = true } }
    )

    private val effectiveScheduler: UdpScheduler = scheduler ?: ScheduledExecutorUdpScheduler(ticker)
    val fragmenter: Fragmenter = Fragmenter(clock = clock, scheduler = effectiveScheduler)

    val boundPort: Int
        get() = socket?.localPort ?: port

    val closed: Boolean
        get() = isClosed.get() || isClosing.get()

    val activeSessionCount: Int
        get() = sessionsByConnectionId.size

    val activeRoomCount: Int
        get() = rooms.size

    /**
     * 启动 UDP 监听与调度线程
     */
    fun start() {
        if (isClosed.get()) {
            throw IllegalStateException("UdpTableServer is closed")
        }
        val s = DatagramSocket(InetSocketAddress(InetAddress.getByName(bindHost), port))
        socket = s

        receiverThread = Thread({ receiveLoop(s) }, "UdpTableServer-Receiver-$bindHost:${s.localPort}").apply {
            isDaemon = true
            start()
        }

        ticker.scheduleAtFixedRate(
            {
                try {
                    tickRooms()
                } catch (ignored: Throwable) {
                }
            },
            TICK_INTERVAL_MILLIS,
            TICK_INTERVAL_MILLIS,
            TimeUnit.MILLISECONDS
        )
    }

    private fun receiveLoop(s: DatagramSocket) {
        val buffer = ByteArray(RECEIVE_BUFFER_SIZE)
        while (!isClosed.get() && !s.isClosed) {
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                s.receive(packet)
            } catch (e: SocketException) {
                break
            } catch (t: Throwable) {
                if (isClosed.get()) break else continue
            }

            val datagramBytes = packet.data.copyOfRange(packet.offset, packet.offset + packet.length)
            val frame = try {
                UdpFrame.decode(datagramBytes)
            } catch (t: Throwable) {
                // 校验和失败或畸形帧静默丢弃
                continue
            }

            val remoteAddress = packet.socketAddress
            try {
                handleFrame(frame, remoteAddress)
            } catch (t: Throwable) {
                // 单帧异常严格隔离，禁止退出接收循环
            }
        }
    }

    /**
     * 入站数据报总分发入口
     */
    fun handleFrame(frame: UdpFrame, remoteAddress: SocketAddress) {
        val effectiveFrame = if (frame.packetType == UdpPacketType.FRAG) {
            fragmenter.reassemble(frame, remoteAddress) ?: return
        } else {
            frame
        }

        when (effectiveFrame.packetType) {
            UdpPacketType.PING -> handlePing(effectiveFrame, remoteAddress)
            UdpPacketType.PONG -> handlePong(effectiveFrame, remoteAddress)
            UdpPacketType.ACK -> handleAck(effectiveFrame, remoteAddress)
            UdpPacketType.RESEND -> handleResend(effectiveFrame, remoteAddress)
            UdpPacketType.REQ -> handleReq(effectiveFrame, remoteAddress)
            else -> {
                // 其他服务端入站忽略
            }
        }
    }

    private fun handlePing(frame: UdpFrame, remoteAddress: SocketAddress) {
        val clientMillis = try {
            HeartbeatPacer.decodePingPayload(frame.payload)
        } catch (_: Exception) {
            0L
        }
        val serverMillis = clock.currentTimeMillis()
        val pongPayload = HeartbeatPacer.encodePongPayload(clientMillis, serverMillis)

        val outSeq = if (frame.connectionId == 0) {
            unattachedOutboundSeq.getAndIncrement()
        } else {
            val session = sessionsByConnectionId[frame.connectionId]
            session?.nextOutboundSeq() ?: unattachedOutboundSeq.getAndIncrement()
        }

        if (frame.connectionId != 0) {
            val session = sessionsByConnectionId[frame.connectionId]
            if (session != null) {
                session.remoteAddress = remoteAddress
                session.recordInbound(serverMillis)
            }
        }

        val pongFrame = UdpFrame(
            packetType = UdpPacketType.PONG,
            connectionId = frame.connectionId,
            seq = outSeq,
            ackSeq = frame.seq,
            payload = pongPayload
        )
        send(pongFrame, remoteAddress)
    }

    private fun handlePong(frame: UdpFrame, remoteAddress: SocketAddress) {
        if (frame.connectionId != 0) {
            val session = sessionsByConnectionId[frame.connectionId] ?: return
            session.remoteAddress = remoteAddress
            session.recordInbound(clock.currentTimeMillis())
        }
    }

    private fun handleAck(frame: UdpFrame, remoteAddress: SocketAddress) {
        if (frame.connectionId != 0) {
            val session = sessionsByConnectionId[frame.connectionId]
            if (session != null) {
                session.remoteAddress = remoteAddress
                session.recordInbound(clock.currentTimeMillis())
            }
            val kick = inFlightKicks[frame.connectionId]
            if (kick != null && kick.seq == frame.ackSeq && kick.address == remoteAddress) {
                purgeKick(kick)
            }
            // 可靠房间更新 ACK 收敛（与 KICK 终端 ACK 相互独立）
            val roomUpdate = inFlightRoomUpdates[frame.connectionId]
            if (roomUpdate != null && roomUpdate.seq == frame.ackSeq) {
                purgeRoomUpdate(roomUpdate)
            }
            // 可靠公开更新按稳定消息 seq 收敛：仅清理匹配条目，绝不误清 ROOM/KICK/玩家推送
            val publicPush = inFlightPublicPushes[frame.connectionId]?.get(frame.ackSeq)
            if (publicPush != null) {
                purgePublicPush(publicPush)
            }
        }
    }

    private fun handleResend(frame: UdpFrame, remoteAddress: SocketAddress) {
        if (frame.connectionId == 0) return
        val session = sessionsByConnectionId[frame.connectionId] ?: return
        session.remoteAddress = remoteAddress
        session.recordInbound(clock.currentTimeMillis())

        // 严格 PUBLIC_RESEND 信封分支：观战公开流与玩家裸游标 RESEND 载荷完全分离
        if (isPublicResendPayload(frame.payload)) {
            val checkpoint = try {
                ApiMessageCodec.decodePublicResendRequest(String(frame.payload, Charsets.UTF_8))
            } catch (_: Exception) {
                null
            }
            // 畸形 PUBLIC_RESEND 信封直接丢弃，绝不落入玩家裸游标重传路径
            if (checkpoint == null) return
            // 玩家连接不得查询 PUBLIC 窗口；非法/过时流请求由房间校验后拒绝
            if (session.spectatorId == null) return
            val room = rooms[session.roomId] ?: return
            val resp = try {
                room.handlePublicResend(session, checkpoint)
            } catch (_: Throwable) {
                null
            } ?: return
            when (resp) {
                is ResendResponse.Retransmit -> {
                    for (rec in resp.records) {
                        val pending = inFlightPublicPushes.computeIfAbsent(session.connectionId) { ConcurrentSkipListMap() }
                        val existing = pending.values.firstOrNull { it.cursor == rec.cursor }
                        if (existing != null) {
                            existing.lastSendMillis = clock.currentTimeMillis()
                            send(existing.frame, remoteAddress, session, useStableSeq = true)
                        } else {
                            val seq = session.nextOutboundSeq()
                            val pushFrame = UdpFrame(
                                packetType = UdpPacketType.PUSH,
                                connectionId = session.connectionId,
                                seq = seq,
                                ackSeq = 0,
                                payload = rec.payload
                            )
                            val entry = InFlightPublicPush(
                                connectionId = session.connectionId,
                                seq = seq,
                                cursor = rec.cursor,
                                frame = pushFrame,
                                attempts = 1,
                                lastSendMillis = clock.currentTimeMillis()
                            )
                            while (pending.size >= UdpProtocol.RETRANSMIT_WINDOW_SIZE) {
                                val eldest = pending.pollFirstEntry()
                                if (eldest != null) terminatePublicPush(eldest.value) else break
                            }
                            pending[seq] = entry
                            send(pushFrame, remoteAddress, session, useStableSeq = true)
                            schedulePublicPushRetry(entry, attempt = 1, delay = PUBLIC_UPDATE_RETRY_BASE_MILLIS)
                        }
                    }
                }
                is ResendResponse.FullSnapshot -> {
                    if (resp.payload.isNotEmpty()) {
                        val seq = session.nextOutboundSeq()
                        val pushFrame = UdpFrame(
                            packetType = UdpPacketType.PUSH,
                            connectionId = session.connectionId,
                            seq = seq,
                            ackSeq = 0,
                            payload = resp.payload
                        )
                        val publishedCursor = room.getPublishedPublicSnapshot()?.cursor ?: checkpoint.cursor
                        val entry = InFlightPublicPush(
                            connectionId = session.connectionId,
                            seq = seq,
                            cursor = publishedCursor,
                            frame = pushFrame,
                            attempts = 1,
                            lastSendMillis = clock.currentTimeMillis()
                        )
                        val pending = inFlightPublicPushes.computeIfAbsent(session.connectionId) { ConcurrentSkipListMap() }
                        while (pending.size >= UdpProtocol.RETRANSMIT_WINDOW_SIZE) {
                            val eldest = pending.pollFirstEntry()
                            if (eldest != null) terminatePublicPush(eldest.value) else break
                        }
                        pending[seq] = entry
                        send(pushFrame, remoteAddress, session, useStableSeq = true)
                        schedulePublicPushRetry(entry, attempt = 1, delay = PUBLIC_UPDATE_RETRY_BASE_MILLIS)
                    }
                }
                is ResendResponse.ConnectionNotFound -> {
                }
            }
            return
        }

        // 观战会话不走玩家裸游标重传路径：玩家窗口与观战身份/编码完全分离
        if (session.spectatorId != null) return

        val fromCursor = parseFromCursor(frame.payload)
        when (val resp = session.handleResend(fromCursor)) {
            is ResendResponse.Retransmit -> {
                for (rec in resp.records) {
                    val pushFrame = UdpFrame(
                        packetType = UdpPacketType.PUSH,
                        connectionId = session.connectionId,
                        seq = session.nextOutboundSeq(),
                        ackSeq = 0,
                        payload = rec.payload
                    )
                    send(pushFrame, remoteAddress, session)
                }
            }
            is ResendResponse.FullSnapshot -> {
                if (resp.payload.isNotEmpty()) {
                    val pushFrame = UdpFrame(
                        packetType = UdpPacketType.PUSH,
                        connectionId = session.connectionId,
                        seq = session.nextOutboundSeq(),
                        ackSeq = 0,
                        payload = resp.payload
                    )
                    send(pushFrame, remoteAddress, session)
                }
            }
            is ResendResponse.ConnectionNotFound -> {
            }
        }
    }

    /**
     * 判定 RESEND 载荷是否为严格 PUBLIC_RESEND 信封：
     * 8 字节二进制玩家游标载荷绝不误判；JSON 文本仅在显式包含 PUBLIC_RESEND 标记时进入公开分支。
     */
    private fun isPublicResendPayload(payload: ByteArray): Boolean {
        if (payload.size == 8) return false
        val text = try {
            String(payload, Charsets.UTF_8)
        } catch (_: Exception) {
            return false
        }
        return text.contains("\"PUBLIC_RESEND\"")
    }

    private fun parseFromCursor(payload: ByteArray): Long {
        if (payload.size == 8) {
            var result = 0L
            for (i in 0..7) {
                result = (result shl 8) or (payload[i].toLong() and 0xFF)
            }
            return result
        }
        return try {
            val str = String(payload, Charsets.UTF_8)
            val json = com.google.gson.JsonParser.parseString(str).asJsonObject
            json.get("fromCursor")?.asLong ?: 0L
        } catch (_: Exception) {
            0L
        }
    }

    private fun handleReq(frame: UdpFrame, remoteAddress: SocketAddress) {
        if (frame.connectionId != 0 && !sessionsByConnectionId.containsKey(frame.connectionId) && !terminatedSessions.containsKey(frame.connectionId)) {
            // connectionId 校验失败静默丢弃 (防放大反射攻击)
            return
        }

        val jsonString = try {
            String(frame.payload, Charsets.UTF_8)
        } catch (_: Exception) {
            ""
        }

        if (isClosed.get() || isClosing.get()) {
            val reqId = tryExtractRequestId(jsonString) ?: "unknown"
            val errResp = ApiResponse(requestId = reqId, success = false, error = ApiProtocol.SERVER_SHUTDOWN)
            sendRsp(
                connectionId = frame.connectionId,
                seq = 1,
                ackSeq = frame.seq,
                response = errResp,
                remoteAddress = remoteAddress
            )
            return
        }

        val request = try {
            ApiMessageCodec.decodeRequest(jsonString)
        } catch (t: Throwable) {
            val reqId = tryExtractRequestId(jsonString) ?: "unknown"
            val errResp = ApiResponse(requestId = reqId, success = false, error = ApiErrors.MALFORMED_REQUEST)
            sendRsp(
                connectionId = frame.connectionId,
                seq = 1,
                ackSeq = frame.seq,
                response = errResp,
                remoteAddress = remoteAddress
            )
            return
        }

        if (frame.connectionId == 0) {
            handleLobbyReq(frame, request, remoteAddress, jsonString)
        } else {
            handleSessionReq(frame, request, remoteAddress, jsonString)
        }
    }

    private fun tryExtractRequestId(json: String): String? {
        val match = REGEX_REQUEST_ID.find(json) ?: return null
        return match.groupValues.getOrNull(1)?.takeIf { it.isNotBlank() && it.length <= 128 }
    }

    private fun handleLobbyReq(frame: UdpFrame, request: ApiRequest, remoteAddress: SocketAddress, rawPayload: String) {
        val lobbyKey = LobbyKey(remoteAddress, request.requestId)

        // 1. 幂等命中重放已完成的建房/加房/重连/查房与冲突检测
        val completed = lobbyCompletedRequests[lobbyKey]
        if (completed != null) {
            if (completed.api != request.api || (completed.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && completed.rawPayload != rawPayload) || completed.request != request) {
                sendRsp(
                    connectionId = 0,
                    seq = 1,
                    ackSeq = frame.seq,
                    response = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT),
                    remoteAddress = remoteAddress
                )
                return
            }
            sendRsp(
                connectionId = completed.connectionId,
                seq = 1,
                ackSeq = frame.seq,
                response = completed.response,
                remoteAddress = remoteAddress
            )
            return
        }

        // 2. 并发去重：若当前同一请求已在执行中，等待其完成后附带回包
        val inFlight = lobbyInFlightRequests[lobbyKey]
        if (inFlight != null) {
            if (inFlight.api != request.api || (inFlight.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && inFlight.rawPayload != rawPayload) || inFlight.request != request) {
                sendRsp(
                    connectionId = 0,
                    seq = 1,
                    ackSeq = frame.seq,
                    response = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT),
                    remoteAddress = remoteAddress
                )
                return
            }
            inFlight.future.whenComplete { result, err ->
                if (result != null) {
                    val (connId, resp) = result
                    sendRsp(connId, 1, frame.seq, resp, remoteAddress)
                } else if (err != null) {
                    sendRsp(
                        0,
                        1,
                        frame.seq,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR),
                        remoteAddress
                    )
                }
            }
            return
        }

        val now = clock.currentTimeMillis()
        val lobbyIt = lobbyInFlightRequests.entries.iterator()
        while (lobbyIt.hasNext()) {
            val item = lobbyIt.next()
            if (now - item.value.timestamp > 15000L) {
                lobbyIt.remove()
                item.value.future.completeExceptionally(
                    IllegalStateException("Lobby in-flight request timed out")
                )
            }
        }
        if (lobbyInFlightRequests.size >= 256) {
            sendRsp(
                connectionId = 0,
                seq = 1,
                ackSeq = frame.seq,
                response = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.SERVER_BUSY),
                remoteAddress = remoteAddress
            )
            return
        }

        val promise = CompletableFuture<Pair<Int, ApiResponse>>()
        val inFlightEntry = InFlightLobbyEntry(
            future = promise,
            rawPayload = rawPayload,
            api = request.api,
            request = request,
            timestamp = now
        )
        val prev = lobbyInFlightRequests.putIfAbsent(lobbyKey, inFlightEntry)
        if (prev != null) {
            if (prev.api != request.api || (prev.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && prev.rawPayload != rawPayload) || prev.request != request) {
                sendRsp(
                    connectionId = 0,
                    seq = 1,
                    ackSeq = frame.seq,
                    response = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT),
                    remoteAddress = remoteAddress
                )
                return
            }
            prev.future.whenComplete { result, err ->
                if (result != null) {
                    val (connId, resp) = result
                    sendRsp(connId, 1, frame.seq, resp, remoteAddress)
                } else if (err != null) {
                    sendRsp(
                        0,
                        1,
                        frame.seq,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR),
                        remoteAddress
                    )
                }
            }
            return
        }

        fun completeLobby(connId: Int, resp: ApiResponse, session: UdpClientSession? = null) {
            recordLobbyCompleted(lobbyKey, CompletedLobbyEntry(connId, resp, rawPayload, request.api, request, clock.currentTimeMillis()))
            lobbyInFlightRequests.remove(lobbyKey)
            promise.complete(connId to resp)
            sendRsp(
                connectionId = connId,
                seq = if (session != null) session.nextOutboundSeq() else 1,
                ackSeq = frame.seq,
                response = resp,
                remoteAddress = remoteAddress,
                session = session
            )
        }

        try {
            when (request.api) {
            ApiTags.CREATE_ROOM -> {
                if (request.protocolVersion != ApiProtocol.PROTOCOL_VERSION) {
                    completeLobby(
                        0,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INVALID_PROTOCOL_VERSION)
                    )
                    return
                }
                rooms.entries.removeIf { it.value.closed }
                if (rooms.size >= MAX_ROOMS) {
                    completeLobby(
                        0,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.SERVER_FULL)
                    )
                    return
                }
                val playerName = request.playerName ?: "Host"
                val config = request.config?.let { EngineProtocolMapper.toEngineConfig(it) } ?: MatchConfig()
                val room = try {
                    createRoom(playerName, config)
                } catch (t: Throwable) {
                    val errCode = if (t.message?.contains(ApiErrors.SERVER_FULL) == true) ApiErrors.SERVER_FULL else ApiErrors.INTERNAL_ERROR
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
                    return
                }
                val newConnId = nextConnectionId()
                room.bindHost(newConnId, remoteAddress, playerName).whenComplete { result, err ->
                    if (err != null || result == null) {
                        val errCode = if (err?.message?.contains("ROOM_CLOSED") == true) ApiErrors.ROOM_NOT_FOUND else ApiErrors.INTERNAL_ERROR
                        completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
                    } else {
                        val (session, roomBinding) = result
                        sessionsByConnectionId[session.connectionId] = session
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = true,
                            roomId = roomBinding.roomId,
                            seat = roomBinding.seat,
                            resumeToken = roomBinding.resumeToken,
                            snapshot = roomBinding.snapshot
                        )
                        session.cacheResponse(request.requestId, resp)
                        completeLobby(session.connectionId, resp, session)
                    }
                }
            }
            ApiTags.JOIN_ROOM -> {
                val roomId = request.roomId
                if (roomId.isNullOrBlank()) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_ROOM_ID))
                    return
                }
                val room = rooms[roomId]
                if (room == null || room.closed) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND))
                    return
                }
                val playerName = request.playerName ?: "Player"
                val newConnId = nextConnectionId()
                room.join(newConnId, remoteAddress, playerName, request.preferSeat).whenComplete { result, err ->
                    if (err != null || result == null) {
                        val errMsg = err?.cause?.message ?: err?.message ?: ""
                        val errCode = when {
                            errMsg.contains("ROOM_FULL") -> ApiErrors.ROOM_FULL
                            errMsg.contains("ROOM_CLOSED") -> ApiErrors.ROOM_NOT_FOUND
                            else -> ApiErrors.INTERNAL_ERROR
                        }
                        completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
                    } else {
                        val (session, roomBinding) = result
                        sessionsByConnectionId[session.connectionId] = session
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = true,
                            roomId = roomBinding.roomId,
                            seat = roomBinding.seat,
                            resumeToken = roomBinding.resumeToken,
                            snapshot = roomBinding.snapshot
                        )
                        session.cacheResponse(request.requestId, resp)
                        completeLobby(session.connectionId, resp, session)
                    }
                }
            }
            ApiTags.JOIN_SPECTATOR -> {
                if (request.protocolVersion != ApiProtocol.PROTOCOL_VERSION) {
                    completeLobby(
                        0,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INVALID_PROTOCOL_VERSION)
                    )
                    return
                }
                val roomId = request.roomId
                if (roomId.isNullOrBlank()) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_ROOM_ID))
                    return
                }
                val room = rooms[roomId]
                if (room == null || room.closed) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND))
                    return
                }
                val newConnId = nextConnectionId()
                room.joinSpectator(newConnId, remoteAddress, request.spectatorName).whenComplete { result, err ->
                    if (err != null || result == null) {
                        val realErr = err?.cause ?: err
                        completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = UdpRoom.mapError(realErr)))
                    } else {
                        val (session, spectatorBinding) = result
                        if (isClosed.get() || room.closed || !room.isSpectatorBindingCurrent(session)) {
                            try {
                                session.close()
                            } catch (_: Throwable) {
                            }
                            val errCode = if (isClosed.get()) {
                                ApiProtocol.SERVER_SHUTDOWN
                            } else if (room.closed) {
                                ApiErrors.ROOM_NOT_FOUND
                            } else {
                                ApiErrors.INVALID_TOKEN
                            }
                            completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
                            return@whenComplete
                        }
                        // 先完成会话注册，再下发初始可靠消息（未注册会话会被可靠发送器拒绝）
                        sessionsByConnectionId[session.connectionId] = session
                        room.sendSpectatorBaseline(session)
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = true,
                            spectatorBinding = spectatorBinding
                        )
                        session.cacheResponse(request.requestId, resp, request.api, rawPayload, request)
                        completeLobby(session.connectionId, resp, session)
                    }
                }
            }
            ApiTags.RESUME_SPECTATOR -> {
                val roomId = request.roomId
                val token = request.token
                val spectatorId = request.spectatorId
                if (roomId.isNullOrBlank() || token.isNullOrBlank() || spectatorId.isNullOrBlank()) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_ROOM_OR_TOKEN))
                    return
                }
                val room = rooms[roomId]
                if (room == null || room.closed) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND))
                    return
                }
                val newConnId = nextConnectionId()
                room.resumeSpectator(newConnId, remoteAddress, spectatorId, token, request.publicCheckpoint).whenComplete { result, err ->
                    if (err != null || result == null) {
                        val realErr = err?.cause ?: err
                        completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = UdpRoom.mapError(realErr)))
                    } else {
                        val (session, spectatorBinding) = result
                        if (isClosed.get() || room.closed || !room.isSpectatorBindingCurrent(session)) {
                            try {
                                session.close()
                            } catch (_: Throwable) {
                            }
                            val errCode = if (isClosed.get()) {
                                ApiProtocol.SERVER_SHUTDOWN
                            } else if (room.closed) {
                                ApiErrors.ROOM_NOT_FOUND
                            } else {
                                ApiErrors.INVALID_TOKEN
                            }
                            completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
                            return@whenComplete
                        }
                        sessionsByConnectionId[session.connectionId] = session
                        room.sendSpectatorBaseline(session)
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = true,
                            spectatorBinding = spectatorBinding
                        )
                        session.cacheResponse(request.requestId, resp, request.api, rawPayload, request)
                        completeLobby(session.connectionId, resp, session)
                    }
                }
            }
            ApiTags.LIST_ROOMS -> {
                rooms.entries.removeIf { it.value.closed }
                val summaries = registry.listSummaries()
                val resp = ApiResponse(requestId = request.requestId, success = true, rooms = summaries)
                completeLobby(0, resp)
            }
            ApiTags.RESUME_SESSION -> {
                val roomId = request.roomId
                val token = request.token
                if (roomId.isNullOrBlank() || token.isNullOrBlank()) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_ROOM_OR_TOKEN))
                    return
                }
                val room = rooms[roomId]
                if (room == null || room.closed) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND))
                    return
                }
                val newConnId = nextConnectionId()
                room.resume(newConnId, remoteAddress, token, request.cursor).whenComplete { result, err ->
                    if (err != null || result == null) {
                        val errMsg = err?.cause?.message ?: err?.message ?: ""
                        val errCode = when {
                            errMsg.contains("INVALID_TOKEN") -> ApiErrors.INVALID_TOKEN
                            errMsg.contains("INVALID_RESUME_TOKEN") -> ApiErrors.INVALID_TOKEN
                            errMsg.contains("STALE_SESSION") -> ApiErrors.INVALID_TOKEN
                            errMsg.contains("DISPLACED_BY_NEW_CONNECTION") -> ApiErrors.INVALID_TOKEN
                            errMsg.contains("ROOM_CLOSED") -> ApiErrors.ROOM_NOT_FOUND
                            else -> ApiErrors.INTERNAL_ERROR
                        }
                        completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
                    } else {
                        val (session, roomBinding) = result
                        sessionsByConnectionId[session.connectionId] = session
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = true,
                            roomId = roomBinding.roomId,
                            seat = roomBinding.seat,
                            resumeToken = roomBinding.resumeToken,
                            snapshot = roomBinding.snapshot
                        )
                        session.cacheResponse(request.requestId, resp)
                        completeLobby(session.connectionId, resp, session)
                    }
                }
            }
            ApiTags.GET_ROOM_INFO -> {
                val roomId = request.roomId
                if (roomId.isNullOrBlank()) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_ROOM_ID))
                    return
                }
                val room = rooms[roomId]
                if (room == null || room.closed) {
                    completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND))
                    return
                }
                room.sharedRoom.getRoomInfo().whenComplete { info, err ->
                    val resp = if (err != null || info == null) {
                        val errCode = UdpRoom.mapError(err)
                        ApiResponse(requestId = request.requestId, success = false, error = errCode)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, roomInfo = info)
                    }
                    completeLobby(0, resp)
                }
            }
            else -> {
                val errCode = if (ApiTags.KNOWN.contains(request.api)) {
                    ApiErrors.UNAUTHENTICATED_SESSION
                } else {
                    ApiErrors.UNKNOWN_REQUEST_TYPE
                }
                completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = errCode))
            }
        }
    } catch (t: Throwable) {
        completeLobby(0, ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR))
    }
}

    private fun handleSessionReq(frame: UdpFrame, request: ApiRequest, remoteAddress: SocketAddress, rawPayload: String) {
        val session = sessionsByConnectionId[frame.connectionId]
        if (session == null) {
            val termEntry = terminatedSessions[frame.connectionId]
            val termSession = if (termEntry != null) {
                if (clock.currentTimeMillis() - termEntry.timestamp > 60000L) {
                    terminatedSessions.remove(frame.connectionId)
                    null
                } else {
                    termEntry.session
                }
            } else {
                null
            }
            if (termSession != null) {
                // 如果是已正常终止的会话，且是 LEAVE_ROOM 重试请求，且命中了缓存的响应，则重发缓存
                if (request.api == ApiTags.LEAVE_ROOM) {
                    val cached = termSession.getCachedEntry(request.requestId)
                    if (cached != null) {
                        if (cached.api != request.api || (cached.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && cached.rawPayload != rawPayload) || (cached.request != null && cached.request != request)) {
                            val errResp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT)
                            sendRsp(termSession.connectionId, termSession.nextOutboundSeq(), frame.seq, errResp, remoteAddress, termSession)
                            return
                        }
                        sendRsp(termSession.connectionId, termSession.nextOutboundSeq(), frame.seq, cached.response, remoteAddress, termSession)
                        return
                    }
                }
                // 其他任何对已终止会话的请求，均返回 INVALID_TOKEN
                val errResp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                sendRsp(frame.connectionId, 1, frame.seq, errResp, remoteAddress)
            }
            return
        }

        session.remoteAddress = remoteAddress
        session.recordInbound(clock.currentTimeMillis())

        val room = rooms[session.roomId]
        if (room == null || room.closed) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        // 1. 代次与 token 校验（前置在缓存与去重之前：被顶替者一律拒绝，绝不能操作新绑定座位）
        val expectedGen = if (session.spectatorId != null) {
            room.getSpectatorGeneration(session.spectatorId)
        } else {
            room.getConnectionGeneration(session.seat)
        }
        if (session.isDisplaced || session.isClosed || session.generation != expectedGen) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        if (request.token != null && request.token != session.token) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        // 2. 角色权限与大厅 API 隔离校验（先于缓存命中与防重放，非法角色/代次绝不得复用缓存回执）
        val isSpectator = session.spectatorId != null
        val spectatorForbiddenApis = setOf(
            ApiTags.SET_READY,
            ApiTags.UPDATE_CONFIG,
            ApiTags.SNAPSHOT,
            ApiTags.DECISION,
            ApiTags.SUBMIT_CHOICE,
            ApiTags.SUBMIT_SHUFFLE_PLAN,
            ApiTags.START_MATCH,
            ApiTags.NEXT_ROUND,
            ApiTags.RESUME_CURSOR,
            ApiTags.CLAIM_CONTROL,
            ApiTags.RELEASE_CONTROL,
            ApiTags.QUERY_ROUND_RESULT
        )
        val playerForbiddenApis = setOf(
            ApiTags.PUBLIC_SNAPSHOT,
            ApiTags.RESUME_PUBLIC,
            ApiTags.QUERY_PUBLIC_ROUND_RESULT
        )
        val lobbyApis = setOf(
            ApiTags.CREATE_ROOM,
            ApiTags.JOIN_ROOM,
            ApiTags.RESUME_SESSION,
            ApiTags.JOIN_SPECTATOR,
            ApiTags.RESUME_SPECTATOR,
            ApiTags.LIST_ROOMS
        )

        if (request.api in lobbyApis) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ALREADY_BOUND)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        if (isSpectator && request.api in spectatorForbiddenApis) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        if (!isSpectator && request.api in playerForbiddenApis) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        if (!ApiTags.KNOWN.contains(request.api)) {
            val resp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.UNKNOWN_REQUEST_TYPE)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
            return
        }

        // 2. 幂等缓存命中重放与冲突检查
        val cached = session.getCachedEntry(request.requestId)
        if (cached != null) {
            if (cached.api != request.api || (cached.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && cached.rawPayload != rawPayload) || (cached.request != null && cached.request != request)) {
                val errResp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT)
                sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, errResp, remoteAddress, session)
                return
            }
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, cached.response, remoteAddress, session)
            return
        }

        // 3. 会话内并发中去重与冲突检查
        val inFlight = session.getInFlightEntry(request.requestId)
        if (inFlight != null) {
            if (inFlight.api != request.api || (inFlight.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && inFlight.rawPayload != rawPayload) || (inFlight.request != null && inFlight.request != request)) {
                val errResp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT)
                sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, errResp, remoteAddress, session)
                return
            }
            inFlight.future.whenComplete { resp, err ->
                if (resp != null) {
                    sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
                } else if (err != null) {
                    sendRsp(
                        session.connectionId,
                        session.nextOutboundSeq(),
                        frame.seq,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR),
                        remoteAddress,
                        session
                    )
                }
            }
            return
        }

        // 4. 防重放校验 (滑动窗口允许乱序新到达帧)
        if (!session.inboundReplayGuard.accept(frame.seq)) {
            return
        }

        val inFlightPromise = CompletableFuture<ApiResponse>()
        val newEntry = UdpClientSession.InFlightSessionEntry(
            future = inFlightPromise,
            api = request.api,
            rawPayload = rawPayload,
            request = request,
            timestamp = clock.currentTimeMillis()
        )
        val prevFlight = session.putInFlightEntry(request.requestId, newEntry)
        if (prevFlight != null) {
            if (prevFlight.api != request.api || (prevFlight.rawPayload.isNotEmpty() && rawPayload.isNotEmpty() && prevFlight.rawPayload != rawPayload) || (prevFlight.request != null && prevFlight.request != request)) {
                val errResp = ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.CONFLICT)
                sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, errResp, remoteAddress, session)
                return
            }
            prevFlight.future.whenComplete { resp, err ->
                if (resp != null) {
                    sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
                } else if (err != null) {
                    sendRsp(
                        session.connectionId,
                        session.nextOutboundSeq(),
                        frame.seq,
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR),
                        remoteAddress,
                        session
                    )
                }
            }
            return
        }

        fun replySession(resp: ApiResponse) {
            session.cacheResponse(request.requestId, resp, request.api, rawPayload, request)
            session.removeInFlight(request.requestId)
            inFlightPromise.complete(resp)
            sendRsp(session.connectionId, session.nextOutboundSeq(), frame.seq, resp, remoteAddress, session)
        }

        // 5. 已绑定禁止再调用大厅建房/加房/重连接口
        if (request.api == ApiTags.CREATE_ROOM || request.api == ApiTags.JOIN_ROOM || request.api == ApiTags.RESUME_SESSION) {
            replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.ALREADY_BOUND))
            return
        }

        val respondReceipt: (CompletableFuture<ProtocolActionReceipt>) -> Unit = { future ->
            future.whenComplete { receipt, err ->
                val resp = if (err != null || receipt == null) {
                    ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                } else {
                    ApiResponse(
                        requestId = request.requestId,
                        success = receipt.status != ProtocolReceiptStatus.REJECTED,
                        receipt = receipt,
                        error = if (receipt.status == ProtocolReceiptStatus.REJECTED) receipt.reason else null
                    )
                }
                replySession(resp)
            }
        }

        try {
            when (request.api) {
            ApiTags.LEAVE_ROOM -> {
                val leaveFuture = if (session.spectatorId != null) {
                    room.leaveSpectator(session)
                } else {
                    room.leave(session)
                }
                leaveFuture.whenComplete { receipt, err ->
                    if (err != null) {
                        val errCode = UdpRoom.mapError(err)
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = false,
                            error = errCode
                        )
                        replySession(resp)
                        if (errCode == ApiErrors.ROOM_NOT_FOUND || room.closed) {
                            terminateSession(session)
                        }
                    } else if (receipt != null) {
                        if (receipt.status == ProtocolReceiptStatus.APPLIED) {
                            recordTerminatedSession(session.connectionId, session)
                            val resp = ApiResponse(
                                requestId = request.requestId,
                                success = true,
                                receipt = receipt
                            )
                            replySession(resp)
                            terminateSession(session)
                        } else {
                            // 权威拒绝：保持原有效绑定，不关闭会话，不记录终止，返回拒绝回执
                            val resp = ApiResponse(
                                requestId = request.requestId,
                                success = false,
                                receipt = receipt,
                                error = receipt.reason
                            )
                            replySession(resp)
                        }
                    } else {
                        val resp = ApiResponse(
                            requestId = request.requestId,
                            success = false,
                            error = ApiErrors.INTERNAL_ERROR
                        )
                        replySession(resp)
                    }
                }
            }
            ApiTags.GET_ROOM_INFO -> {
                room.getRoomInfo(session).whenComplete { info, err ->
                    val resp = if (err != null || info == null) {
                        val errCode = UdpRoom.mapError(err)
                        ApiResponse(requestId = request.requestId, success = false, error = errCode)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, roomInfo = info)
                    }
                    replySession(resp)
                }
            }
            ApiTags.SET_READY -> {
                val ready = request.ready
                if (ready == null) {
                    replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST))
                    return
                }
                respondReceipt(room.setReady(session, ready))
            }
            ApiTags.UPDATE_CONFIG -> {
                val roomConfig: ProtocolRoomConfig = request.roomConfig
                    ?: run {
                        replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST))
                        return
                    }
                respondReceipt(room.updateConfig(session, roomConfig))
            }
            ApiTags.CAPABILITIES -> {
                val caps = ProtocolAccessCapabilities(
                    protocolVersion = ApiProtocol.PROTOCOL_VERSION,
                    supportedPlayerCounts = setOf(4),
                    remoteTransport = true,
                    persistence = false,
                    resumption = true
                )
                replySession(ApiResponse(requestId = request.requestId, success = true, capabilities = caps))
            }
            ApiTags.SNAPSHOT -> {
                room.querySnapshot(session).whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, snapshot = snap)
                    }
                    replySession(resp)
                }
            }
            ApiTags.DECISION -> {
                room.queryDecision(session).whenComplete { dec, err ->
                    val resp = if (err != null || dec == null) {
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, decision = dec)
                    }
                    replySession(resp)
                }
            }
            ApiTags.SUBMIT_CHOICE -> {
                val choice = request.choice
                if (choice == null) {
                    replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_ACTION_CHOICE))
                    return
                }
                respondReceipt(room.submitChoice(session, choice))
            }
            ApiTags.SUBMIT_SHUFFLE_PLAN -> {
                val plan = request.shufflePlan
                if (plan == null) {
                    replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MISSING_SHUFFLE_PLAN))
                    return
                }
                respondReceipt(room.submitShufflePlan(session, plan))
            }
            ApiTags.START_MATCH -> {
                respondReceipt(room.startMatch(session))
            }
            ApiTags.NEXT_ROUND -> {
                respondReceipt(room.nextRound(session))
            }
            ApiTags.RESUME_CURSOR -> {
                val cursor = request.cursor ?: 0L
                room.resumeCursor(session, cursor).whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, snapshot = snap)
                    }
                    replySession(resp)
                }
            }
            ApiTags.CLAIM_CONTROL -> {
                respondReceipt(room.claimControl(session))
            }
            ApiTags.RELEASE_CONTROL -> {
                respondReceipt(room.releaseControl(session))
            }
            ApiTags.QUERY_ROUND_RESULT -> {
                val roundId = request.roundId ?: ""
                room.queryRoundResult(session, roundId).whenComplete { events, err ->
                    val resp = if (err != null || events == null) {
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, events = events)
                    }
                    replySession(resp)
                }
            }
            ApiTags.PUBLIC_SNAPSHOT -> {
                room.publicSnapshotFor(session).whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, publicSnapshot = snap)
                    }
                    replySession(resp)
                }
            }
            ApiTags.RESUME_PUBLIC -> {
                val checkpoint = request.publicCheckpoint
                if (checkpoint == null) {
                    replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST))
                    return
                }
                room.resumePublicFor(session, checkpoint).whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        val realErr = err?.cause ?: err
                        ApiResponse(requestId = request.requestId, success = false, error = UdpRoom.mapError(realErr))
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, publicSnapshot = snap)
                    }
                    replySession(resp)
                }
            }
            ApiTags.QUERY_PUBLIC_ROUND_RESULT -> {
                val roundId = request.roundId ?: ""
                room.queryPublicRoundResult(session, roundId).whenComplete { events, err ->
                    val resp = if (err != null || events == null) {
                        ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = request.requestId, success = true, publicEvents = events)
                    }
                    replySession(resp)
                }
            }
            else -> {
                replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.UNKNOWN_REQUEST_TYPE))
            }
        }
    } catch (t: Throwable) {
        replySession(ApiResponse(requestId = request.requestId, success = false, error = ApiErrors.INTERNAL_ERROR))
    }
    }

    /**
     * 发送 UDP 数据帧（若载荷 > 1090 字节自动经 Fragmenter 分片发送）。
     * [useStableSeq] 为 true 时保持原始消息帧的 seq，避免分片或重传时生成新 seq 破坏 ACK 匹配。
     */
    fun send(
        frame: UdpFrame,
        address: SocketAddress,
        session: UdpClientSession? = null,
        useStableSeq: Boolean = false
    ) {
        outboundFrameListener?.invoke(frame, address)
        val s = socket
        if (isClosed.get() || s == null || s.isClosed) return
        val seqSupplier: () -> Int = if (session != null && !useStableSeq) {
            { session.nextOutboundSeq() }
        } else {
            { frame.seq }
        }

        val framesToSend = if (frame.payload.size > UdpProtocol.FRAGMENT_THRESHOLD_BYTES) {
            fragmenter.fragment(frame, seqSupplier)
        } else {
            listOf(frame)
        }

        for (f in framesToSend) {
            val bytes = UdpFrame.encode(f)
            val packet = DatagramPacket(bytes, bytes.size, address)
            try {
                s.send(packet)
            } catch (ignored: Throwable) {
            }
        }
    }

    /**
     * 发送统一响应 (RSP)
     */
    fun sendRsp(
        connectionId: Int,
        seq: Int,
        ackSeq: Int,
        response: ApiResponse,
        remoteAddress: SocketAddress,
        session: UdpClientSession? = null
    ) {
        val json = ApiMessageCodec.encodeResponse(response)
        val payload = json.toByteArray(Charsets.UTF_8)
        val frame = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = connectionId,
            seq = seq,
            ackSeq = ackSeq,
            payload = payload
        )
        send(frame, remoteAddress, session)
    }

    /**
     * 下发推送状态更新 (PUSH)
     */
    private fun sendPushUpdate(session: UdpClientSession, update: PlayerUpdate) {
        if (session.isClosed || session.isDisplaced) return
        val protocolUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val json = ApiMessageCodec.encodePush(protocolUpdate)
        val payload = json.toByteArray(Charsets.UTF_8)

        session.recordPush(
            cursor = update.snapshot.cursor,
            fromCursor = update.fromCursor,
            payload = payload
        )

        val frame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = session.connectionId,
            seq = session.nextOutboundSeq(),
            ackSeq = 0,
            payload = payload
        )
        send(frame, session.remoteAddress, session)
    }

    /**
     * 下发连接终结通知 (KICK) 并进行有界重传与 ACK 追踪
     */
    fun sendKick(session: UdpClientSession, reason: String): CompletableFuture<Unit> {
        val future = CompletableFuture<Unit>()
        if (session.isClosed || isClosed.get()) {
            future.complete(Unit)
            return future
        }
        synchronized(session) {
            cancelAndPurgeRoomUpdate(session.connectionId)
            cancelAndPurgeKick(session.connectionId)
            cancelAndPurgePublicPushes(session.connectionId)
            highestSentRoomVersion.remove(session.connectionId)
            highestSentPublicCursor.remove(session.connectionId)
        }

        val kickPayload = ApiMessageCodec.encodeKick(reason).toByteArray(Charsets.UTF_8)
        val kickSeq = session.nextOutboundSeq()
        val kickFrame = UdpFrame(
            packetType = UdpPacketType.KICK,
            connectionId = session.connectionId,
            seq = kickSeq,
            ackSeq = 0,
            payload = kickPayload
        )
        val kickEntry = InFlightKick(
            connectionId = session.connectionId,
            seq = kickSeq,
            frame = kickFrame,
            address = session.remoteAddress,
            future = future,
            attempts = 1
        )
        val old = inFlightKicks.put(session.connectionId, kickEntry)
        if (old != null) {
            terminateKick(old)
        }
        send(kickFrame, session.remoteAddress, session, useStableSeq = true)

        fun scheduleRetry(attempt: Int, delay: Long) {
            if (isClosed.get() || future.isDone || inFlightKicks[session.connectionId] !== kickEntry) {
                return
            }
            try {
                val cancellable = effectiveScheduler.schedule(delay) {
                    if (inFlightKicks[session.connectionId] === kickEntry && !future.isDone && !isClosed.get()) {
                        if (attempt < 3) {
                            kickEntry.attempts = attempt + 1
                            send(kickFrame, session.remoteAddress, session, useStableSeq = true)
                            scheduleRetry(attempt + 1, delay * 2)
                        } else {
                            purgeKick(kickEntry)
                        }
                    }
                }
                kickEntry.retryCancellable = cancellable
                if (isClosed.get() || future.isDone || inFlightKicks[session.connectionId] !== kickEntry) {
                    cancellable.cancel()
                }
            } catch (_: RejectedExecutionException) {
                purgeKick(kickEntry)
            }
        }

        scheduleRetry(1, 100L)
        return future
    }

    /**
     * 创建全新房间：经共享 [RoomRegistry] 装配权威 SharedRoom，再包装 UDP 传输适配器。
     * 成员、准备、配置、开赛、离房与恢复业务全部复用进程内同一份 SharedRoom 实现。
     */
    fun createRoom(hostPlayerName: String, config: MatchConfig = MatchConfig()): UdpRoom {
        if (isClosed.get() || isClosing.get()) {
            throw IllegalStateException("UdpTableServer is closed")
        }
        rooms.entries.removeIf { it.value.closed }
        if (rooms.size >= MAX_ROOMS) {
            throw IllegalStateException("${ApiErrors.SERVER_FULL}: room limit ($MAX_ROOMS) reached")
        }
        val protocolConfig = ProtocolRoomConfig(
            matchConfig = EngineProtocolMapper.toProtocolMatchConfig(config),
            timeControl = EngineProtocolMapper.toProtocolTimeControlConfig(timeControl)
        )
        val sharedRoom = registry.createRoom(
            hostPlayerName = hostPlayerName,
            initialConfig = protocolConfig,
            isSingleplayer = false
        )
        val room = UdpRoom(
            sharedRoom = sharedRoom,
            udpClock = this@UdpTableServer.clock,
            pushSender = { session, update ->
                sendPushUpdate(session, update)
            },
            kickSender = { session, reason ->
                sendKick(session, reason)
            },
            roomUpdateSender = { session, info ->
                sendRoomUpdate(session, info)
            },
            publicUpdateSender = { session, update ->
                sendPublicUpdate(session, update)
            }
        )
        if (rooms.putIfAbsent(sharedRoom.roomId, room) == null) {
            return room
        }
        // 注册表已生成唯一 roomId，理论不可达；防御性回收
        try {
            room.close()
        } catch (ignored: Throwable) {
        }
        throw IllegalStateException("room id collision: ${sharedRoom.roomId}")
    }

    fun getRoom(roomId: String): UdpRoom? = rooms[roomId]

    fun getSession(connectionId: Int): UdpClientSession? = sessionsByConnectionId[connectionId]

    /**
     * 周期性驱动各房间 TableHost.tick() 并执行入站超时判定
     */
    fun tickRooms() {
        if (isClosed.get() || isClosing.get()) return
        for (room in rooms.values) {
            if (room.closed) continue
            try {
                room.tick()
            } catch (ignored: Throwable) {
            }
        }
        checkSessionTimeouts()
        try {
            refreshPendingRoomUpdates(clock.currentTimeMillis())
            refreshPendingPublicPushes(clock.currentTimeMillis())
        } catch (ignored: Throwable) {
        }
    }

    /**
     * 检查客户端心跳保活与入站超时 (10s)，超时异步触发房间断线清理
     */
    fun checkSessionTimeouts(now: Long = clock.currentTimeMillis()) {
        if (isClosed.get() || isClosing.get()) return
        for (session in sessionsByConnectionId.values) {
            if (session.isClosed || session.isDisplaced) continue
            if (session.isTimedOut(now, UdpProtocol.INBOUND_TIMEOUT_MILLIS)) {
                terminateSession(session)
                recordTerminatedSession(session.connectionId, session)
                val roomId = session.roomId
                postTeardown {
                    rooms[roomId]?.onConnectionClosed(session)
                }
            }
        }
    }

    /**
     * 房间侧连接清理异步投递（绝不在 TableHost 串行锁内同步进入房间锁）
     */
    internal fun postTeardown(task: () -> Unit) {
        try {
            teardownExecutor.execute {
                try {
                    task()
                } catch (ignored: Throwable) {
                }
            }
        } catch (_: RejectedExecutionException) {
        }
    }

    private fun nextConnectionId(): Int {
        while (true) {
            val id = random.nextInt()
            if (id != 0 && !sessionsByConnectionId.containsKey(id)) {
                return id
            }
        }
    }

    internal fun getInFlightRoomUpdate(connectionId: Int): InFlightRoomUpdate? = inFlightRoomUpdates[connectionId]
    internal fun getHighestSentRoomVersion(connectionId: Int): Long? = highestSentRoomVersion[connectionId]
    internal fun registerSession(session: UdpClientSession) {
        sessionsByConnectionId[session.connectionId] = session
    }

    override fun close() {
        if (!isClosing.compareAndSet(false, true)) {
            return
        }

        // 1. 在 Socket 开启且 isClosed=false 时向所有未关闭会话发送 SERVER_SHUTDOWN KICK 并有界等待确认
        val kickFutures = mutableListOf<CompletableFuture<Unit>>()
        val activeSessions = ArrayList(sessionsByConnectionId.values)
        for (session in activeSessions) {
            if (!session.isClosed && !session.isDisplaced) {
                try {
                    val fut = sendKick(session, ApiProtocol.SERVER_SHUTDOWN)
                    kickFutures.add(fut)
                } catch (ignored: Throwable) {
                }
            }
        }

        // 有界等待 ACK/重传排空 (1000ms 覆盖 0/100/300ms 重试及对端响应)
        if (kickFutures.isNotEmpty()) {
            try {
                CompletableFuture.allOf(*kickFutures.toTypedArray()).get(1000, TimeUnit.MILLISECONDS)
            } catch (ignored: Throwable) {
            }
        }

        // 2. KICK 排空后置 isClosed 并停止 ticker 与活跃会话
        isClosed.set(true)
        ticker.shutdownNow()

        for (session in activeSessions) {
            try {
                terminateSession(session)
            } catch (ignored: Throwable) {
            }
        }
        sessionsByConnectionId.clear()
        for (connId in ArrayList(inFlightKicks.keys)) {
            cancelAndPurgeKick(connId)
        }
        inFlightKicks.clear()
        for (connId in ArrayList(inFlightRoomUpdates.keys)) {
            cancelAndPurgeRoomUpdate(connId)
        }
        inFlightRoomUpdates.clear()
        highestSentRoomVersion.clear()
        for (connId in ArrayList(inFlightPublicPushes.keys)) {
            cancelAndPurgePublicPushes(connId)
        }
        inFlightPublicPushes.clear()
        highestSentPublicCursor.clear()

        // 3. 关闭 UDP socket 和接收线程 (防止接收线程自加入死锁)
        val s = socket
        try {
            s?.close()
        } catch (ignored: Throwable) {
        }
        receiverThread?.let { thread ->
            if (Thread.currentThread() != thread) {
                try {
                    thread.join(1000L)
                } catch (ignored: InterruptedException) {
                    Thread.currentThread().interrupt()
                }
            }
        }
        try {
            fragmenter.close()
        } catch (ignored: Throwable) {
        }

        teardownExecutor.shutdown()
        try {
            teardownExecutor.awaitTermination(2, TimeUnit.SECONDS)
        } catch (ignored: InterruptedException) {
            Thread.currentThread().interrupt()
        }

        val snapshot = ArrayList(rooms.values)
        rooms.clear()
        for (room in snapshot) {
            try {
                room.close()
            } catch (ignored: Throwable) {
            }
        }
    }

    companion object {
        const val MAX_ROOMS: Int = 100
        const val TICK_INTERVAL_MILLIS: Long = 50L
        const val RECEIVE_BUFFER_SIZE: Int = 2048
        const val ROOM_UPDATE_RETRY_BASE_MILLIS: Long = 100L
        const val ROOM_UPDATE_MAX_ATTEMPTS: Int = 3
        const val ROOM_UPDATE_REFRESH_MILLIS: Long = 3000L
        const val PUBLIC_UPDATE_RETRY_BASE_MILLIS: Long = 100L
        const val PUBLIC_UPDATE_MAX_ATTEMPTS: Int = 3
        const val PUBLIC_UPDATE_REFRESH_MILLIS: Long = 3000L

        private val REGEX_REQUEST_ID = Regex("\"requestId\"\\s*:\\s*\"([^\"]+)\"")
    }
}

/**
 * 基于 ScheduledExecutorService 的 UdpScheduler 实现
 */
class ScheduledExecutorUdpScheduler(
    private val executor: ScheduledExecutorService
) : UdpScheduler {
    override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
        val future = executor.schedule(task, delayMillis, TimeUnit.MILLISECONDS)
        return Cancellable { future.cancel(false) }
    }
}

package com.majiang.client.transport.udp

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
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.PushType
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.udp.Fragmenter
import com.majiang.transport.udp.HeartbeatPacer
import com.majiang.transport.udp.PushOfferResult
import com.majiang.transport.udp.PushSequencer
import com.majiang.transport.udp.ReliableRequestor
import com.majiang.transport.udp.ReplayGuard
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpFrameCodec
import com.majiang.transport.udp.UdpFrameException
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpProtocol
import com.majiang.transport.udp.UdpScheduler
import java.io.IOException
import java.util.TreeMap
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * UDP 客户端网络传输绑定实现 (UdpClientBinding)
 *
 * 实现了统一服务端契约 [ServerConnection]，封装 UDP 自封装协议栈与可靠传输逻辑。
 *
 * 核心架构与可靠性机制：
 * 1. **请求-响应可靠性**：大厅与会话请求经 [ReliableRequestor]，按 RTO 指数退避重传直至 RSP 响应并回发 ACK；
 * 2. **推送流游标序与自愈**：接收端经 [PushSequencer] 校验游标连续性，窗口内缺口触发 RESEND，超窗缺口触发全量快照自愈；
 * 3. **早期推送缓冲回放**：在表现层监听器注册之前到达的 PUSH 消息进入有序队列缓冲，并在 [onUpdate] 注册时按序回放；
 * 4. **分片重组**：入站/出站大数据报（如全量快照）经 [Fragmenter] 自动按阈值拆分与重组；
 * 5. **链路保活与心跳**：由 [HeartbeatPacer] 每 3 秒发送 PING 并计算平滑 RTT，心跳失步或 10 秒无入站帧自动判死；
 * 6. **防重放与安全隔离**：每方向递增序号经 [ReplayGuard] 校验，非匹配的 connectionId 静默丢弃；
 * 7. **时间解耦**：所有时钟与调度全部经 [UdpClock] 与 [UdpScheduler] 注入，支持零真实等待的单元测试。
 */
class UdpClientBinding(
    private val rawSender: (ByteArray) -> Unit,
    val clock: UdpClock = UdpClock.SYSTEM,
    val scheduler: UdpScheduler = ScheduledExecutorUdpScheduler(),
    initialConnectionId: Int = 0,
    private val onCloseCallback: (() -> Unit)? = null
) : ServerConnection {

    @Volatile
    var connectionId: Int = initialConnectionId
        private set

    @Volatile
    private var terminatingConnectionId: Int = initialConnectionId

    private data class PlayerHealToken(
        val epoch: Long,
        val id: Long
    )
    private val playerHealSeq = AtomicLong(0L)
    private val activePlayerHealToken = AtomicReference<PlayerHealToken?>(null)

    private data class PlayerDispatchItem(
        val epoch: Long,
        val roomId: String,
        val sessionId: String,
        val syncEpoch: String,
        val viewerSeat: Int,
        val update: ProtocolPlayerUpdate
    )

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
    var boundSessionId: String? = null
        private set

    @Volatile
    var boundSyncEpoch: String? = null
        private set

    @Volatile
    override var closed: Boolean = false
        private set

    @Volatile
    override var disconnectReason: String? = null
        private set

    private val seqGenerator = AtomicInteger(0)
    fun nextSeq(): Int = seqGenerator.incrementAndGet()

    private val replayGuard = ReplayGuard()

    private val updateListeners = CopyOnWriteArrayList<(ProtocolPlayerUpdate) -> Unit>()
    private val roomUpdateListeners = CopyOnWriteArrayList<(ProtocolRoomInfo) -> Unit>()
    private val disconnectListeners = CopyOnWriteArrayList<(reason: String) -> Unit>()
    private val earlyPushBuffer = ConcurrentLinkedQueue<PlayerDispatchItem>()
    private val earlyPushOverflowed = AtomicBoolean(false)
    private val earlyPushCapacity = 64
    private val earlyRoomUpdateBuffer = ConcurrentLinkedQueue<ProtocolRoomInfo>()

    @Volatile
    var boundSpectatorId: String? = null
        private set

    @Volatile
    var boundSpectatorToken: String? = null
        private set

    @Volatile
    var lastPublicSessionId: String? = null
        private set

    @Volatile
    var lastPublicSyncEpoch: String? = null
        private set

    @Volatile
    var lastPublicCursor: Long = 0L
        private set

    private data class PublicHealToken(
        val epoch: Long,
        val id: Long
    )
    private val publicHealSeq = AtomicLong(0L)
    private val activePublicHealToken = AtomicReference<PublicHealToken?>(null)

    private data class PublicDispatchItem(
        val epoch: Long,
        val roomId: String,
        val update: ProtocolPublicUpdate
    )

    private val publicUpdateListeners = CopyOnWriteArrayList<(ProtocolPublicUpdate) -> Unit>()
    private val earlyPublicPushBuffer = ConcurrentLinkedQueue<PublicDispatchItem>()
    private val earlyPublicPushOverflowed = AtomicBoolean(false)
    private val earlyPublicPushCapacity = 64

    // 独立严格 PUBLIC 游标的有界重排缓冲：超前更新按 fromCursor 暂存，缺口补齐后按序排水
    private val publicReorderBuffer = TreeMap<Long, ProtocolPublicUpdate>()
    private val publicReorderCapacity = UdpProtocol.RETRANSMIT_WINDOW_SIZE
    @Volatile
    var lastRoomInfoVersion: Long = 0L
        private set
    @Volatile
    var latestRoomInfo: ProtocolRoomInfo? = null
        private set
    @Volatile
    var bindingEpoch: Long = 0L
        private set

    private val stateLock = Any()
    private val drainWip = AtomicInteger(0)
    private val roomDispatchQueue = ConcurrentLinkedQueue<RoomDispatchItem>()
    private val listenerMaxVersions = ConcurrentHashMap<(ProtocolRoomInfo) -> Unit, Long>()

    private data class RoomDispatchItem(
        val epoch: Long,
        val roomId: String,
        val version: Long,
        val roomInfo: ProtocolRoomInfo
    )

    private val fragmenter = Fragmenter(
        clock = clock,
        scheduler = scheduler
    )

    private val pushSequencer = PushSequencer(
        initialCursor = 0L
    )

    private val reliableRequestor = ReliableRequestor(
        sender = { frame -> sendFrame(frame) },
        clock = clock,
        scheduler = scheduler,
        connectionIdSupplier = { connectionId },
        seqSupplier = { nextSeq() },
        ackSender = { ackFrame -> sendFrame(ackFrame) },
        onConnectionDead = { reason ->
            close(reason)
            notifyDisconnect(reason)
        }
    )

    private val heartbeatPacer = HeartbeatPacer(
        clock = clock,
        scheduler = scheduler,
        pingSender = { clientMillis ->
            val pingPayload = HeartbeatPacer.encodePingPayload(clientMillis)
            val pingFrame = UdpFrame(
                packetType = UdpPacketType.PING,
                connectionId = connectionId,
                seq = nextSeq(),
                payload = pingPayload
            )
            sendFrame(pingFrame)
        },
        onDead = { reason ->
            close(reason)
            notifyDisconnect(reason)
        }
    )

    /**
     * 启动心跳检测起搏器
     */
    fun startHeartbeat() {
        heartbeatPacer.start()
    }

    // ==========================================
    // 底层数据帧收发
    // ==========================================

    fun sendFrame(frame: UdpFrame) {
        if (closed && frame.packetType != UdpPacketType.ACK) return
        if (frame.payload.size > UdpProtocol.FRAGMENT_THRESHOLD_BYTES) {
            val fragments = fragmenter.fragment(frame) { nextSeq() }
            for (frag in fragments) {
                sendSingleDatagram(frag)
            }
        } else {
            sendSingleDatagram(frame)
        }
    }

    private fun sendSingleDatagram(frame: UdpFrame) {
        val bytes = UdpFrameCodec.encode(frame)
        rawSender(bytes)
    }

    /**
     * 入站二进制数据报处理入口
     */
    fun onInboundDatagram(bytes: ByteArray) {
        val frame = try {
            UdpFrameCodec.decode(bytes)
        } catch (_: Exception) {
            // 畸形数据报静默忽略，不产生额外网络流量
            return
        }
        onInboundFrame(frame)
    }

    /**
     * 入站 UDP 结构化帧调度核心
     */
    fun onInboundFrame(frame: UdpFrame) {
        val currentConnId = connectionId
        if (closed) {
            // 已关闭状态下仅当收到针对本端终结连接的 KICK 时回发 ACK，不处理任意帧
            val terminatingConnId = terminatingConnectionId
            if (frame.packetType == UdpPacketType.KICK && terminatingConnId != 0 && frame.connectionId == terminatingConnId) {
                val ackFrame = UdpFrame(
                    packetType = UdpPacketType.ACK,
                    connectionId = terminatingConnId,
                    seq = nextSeq(),
                    ackSeq = frame.seq
                )
                sendFrame(ackFrame)
            }
            return
        }

        // 1. connectionId 校验：未绑定时允许为 0 或由握手决定；已绑定时严禁接收非本端连接 ID（含 connectionId=0）
        if (currentConnId == 0) {
            if (frame.packetType == UdpPacketType.PUSH || frame.packetType == UdpPacketType.KICK) {
                return // 未绑定时静默丢弃推送与断开帧
            }
        } else {
            if (frame.connectionId != currentConnId) {
                return
            }
        }

        // 2. 处理分片重组
        val completeFrame = if (frame.packetType == UdpPacketType.FRAG) {
            fragmenter.reassemble(frame) ?: return
        } else {
            frame
        }

        // 3. 校验重组后完整帧身份标识
        if (currentConnId == 0) {
            if (completeFrame.packetType == UdpPacketType.PUSH || completeFrame.packetType == UdpPacketType.KICK) {
                return
            }
        } else {
            if (completeFrame.connectionId != currentConnId) {
                return
            }
        }

        // 4. 身份验证通过后方才刷新链路活跃时间
        heartbeatPacer.onInboundPacket(clock.currentTimeMillis())

        // 5. 按包类型分发
        when (completeFrame.packetType) {
            UdpPacketType.RSP -> {
                val requestId = try {
                    val json = completeFrame.payload.toString(Charsets.UTF_8)
                    ApiMessageCodec.decodeResponse(json).requestId
                } catch (_: Exception) {
                    null
                }
                reliableRequestor.onResponse(completeFrame, requestId)
            }
            UdpPacketType.PUSH -> {
                handleInboundPush(completeFrame)
            }
            UdpPacketType.PONG -> {
                if (!replayGuard.accept(completeFrame.seq)) return
                try {
                    val (cMillis, sMillis) = HeartbeatPacer.decodePongPayload(completeFrame.payload)
                    heartbeatPacer.onPong(cMillis, sMillis, clock.currentTimeMillis())
                } catch (_: Exception) {}
            }
            UdpPacketType.KICK -> {
                // 收到 KICK 一律回发 ACK（保证对端重传的 KICK 能得到确认，覆盖丢失首个 ACK 场景）
                val ackFrame = UdpFrame(
                    packetType = UdpPacketType.ACK,
                    connectionId = connectionId,
                    seq = nextSeq(),
                    ackSeq = completeFrame.seq
                )
                sendFrame(ackFrame)

                if (!replayGuard.accept(completeFrame.seq)) return

                val reason = try {
                    ApiMessageCodec.decodeKick(completeFrame.payload.toString(Charsets.UTF_8))
                } catch (_: Exception) {
                    ApiProtocol.DISPLACED_BY_NEW_CONNECTION
                }

                close(reason)
                notifyDisconnect(reason)
            }
            UdpPacketType.ACK -> {
                // 对方确认包（客户端不维护待发 RSP，忽略）
            }
            UdpPacketType.PING -> {
                // 服务端反向 PING 保活，回发 PONG
                val pongPayload = HeartbeatPacer.encodePongPayload(
                    clientMillis = HeartbeatPacer.decodePingPayload(completeFrame.payload),
                    serverMillis = clock.currentTimeMillis()
                )
                val pongFrame = UdpFrame(
                    packetType = UdpPacketType.PONG,
                    connectionId = connectionId,
                    seq = nextSeq(),
                    ackSeq = completeFrame.seq,
                    payload = pongPayload
                )
                sendFrame(pongFrame)
            }
            else -> {}
        }
    }

    private fun handleInboundPush(frame: UdpFrame) {
        val json = try {
            frame.payload.toString(Charsets.UTF_8)
        } catch (_: Exception) {
            return
        }

        val pushType = try {
            ApiMessageCodec.classifyPush(json)
        } catch (_: Exception) {
            // 未知类型、错误类型字段和非法 JSON 直接拒绝
            return
        }

        when (pushType) {
            PushType.ROOM -> {
                val roomInfo = try {
                    ApiMessageCodec.decodeRoomUpdatePush(json)
                } catch (_: Exception) {
                    // 畸形信封、错误类型或多余字段：拒绝，不给非法数据续命，不回发 ACK
                    return
                }

                // 必须在状态锁内原子校验连接身份代次并提交缓存
                val dispatchItem: RoomDispatchItem?
                val shouldAck: Boolean
                synchronized(stateLock) {
                    if (closed || boundRoomId == null || roomInfo.roomId != boundRoomId) {
                        // 未绑定房间、已关闭或外来 roomId：不给非法/外来身份的数据续命，不回发 ACK
                        return
                    }
                    shouldAck = true
                    if (roomInfo.version <= lastRoomInfoVersion) {
                        // 重复或乱序旧版本不回退，不向 UI 监听器分发
                        dispatchItem = null
                    } else {
                        // 完整房间视图允许跳版本合并，不把跳版本当 game cursor 缺口
                        lastRoomInfoVersion = roomInfo.version
                        latestRoomInfo = roomInfo
                        dispatchItem = RoomDispatchItem(
                            epoch = bindingEpoch,
                            roomId = boundRoomId!!,
                            version = roomInfo.version,
                            roomInfo = roomInfo
                        )
                    }
                }

                // 有效房间更新（含重传）：必须在 ReplayGuard 之前识别并回发 ACK，
                // 使得首个 ACK 丢失或重传场景能自愈收敛
                if (shouldAck) {
                    val ackFrame = UdpFrame(
                        packetType = UdpPacketType.ACK,
                        connectionId = connectionId,
                        seq = nextSeq(),
                        ackSeq = frame.seq
                    )
                    sendFrame(ackFrame)
                    replayGuard.accept(frame.seq)
                }

                if (dispatchItem != null) {
                    enqueueAndDrainRoomUpdate(dispatchItem)
                }
            }

            PushType.PUBLIC -> {
                val update = try {
                    ApiMessageCodec.decodePublicUpdatePush(json)
                } catch (_: Exception) {
                    // 严格 codec：畸形信封、错误类型或多余字段直接拒绝，不回发 ACK
                    return
                }

                val snap = update.snapshot
                val toDispatch = mutableListOf<PublicDispatchItem>()
                var shouldAck = false
                var resendCheckpoint: PublicCheckpoint? = null
                var needSelfHeal = false

                synchronized(stateLock) {
                    // 单一锁检查角色与房间绑定身份：非观战绑定时不消费错角色推送，也不 ACK
                    if (closed || boundRoomId == null || boundSpectatorId == null) {
                        return
                    }

                    val epoch = bindingEpoch
                    val roomId = boundRoomId!!

                    // 流身份确认：仅当前合法流的消息可被 ACK 与消费，外来流不给续命也不改状态
                    val curSessionId = lastPublicSessionId
                    val curSyncEpoch = lastPublicSyncEpoch
                    val curCursor = lastPublicCursor
                    if (curSessionId == null || curSyncEpoch == null ||
                        snap.sessionId != curSessionId || snap.syncEpoch != curSyncEpoch
                    ) {
                        // 外来流/异 epoch：不能使当前绑定回滚或跨流，改由受绑定保护的权威全量自愈收敛
                        needSelfHeal = true
                        return@synchronized
                    }
                    shouldAck = true

                    if (update.isFullSnapshot) {
                        // full 仅当前流且不回退；旧 full / 同游标重复 full 是合法重复，仅 ACK 不派发
                        if (snap.cursor > curCursor) {
                            lastPublicCursor = snap.cursor
                            publicReorderBuffer.headMap(snap.cursor).clear()
                            toDispatch.add(PublicDispatchItem(epoch, roomId, update))
                            drainPublicReorderUnderLock(toDispatch, epoch, roomId)
                        }
                    } else if (snap.cursor <= curCursor) {
                        // 陈旧/重复：合法消息仍 ACK，但用户回调恰好一次
                    } else if (update.fromCursor == curCursor) {
                        // 严格连续基线（fromCursor 必须等于当前游标）
                        lastPublicCursor = snap.cursor
                        toDispatch.add(PublicDispatchItem(epoch, roomId, update))
                        drainPublicReorderUnderLock(toDispatch, epoch, roomId)
                    } else if (update.fromCursor > curCursor) {
                        val gap = update.fromCursor - curCursor
                        if (gap > publicReorderCapacity || publicReorderBuffer.size >= publicReorderCapacity) {
                            // 超出重传窗口或重排缓冲溢出：明确全量自愈，不能只发 RESEND 却无法消费结果
                            needSelfHeal = true
                        } else {
                            // 缺口消息按 fromCursor 有界重排暂存（按原 seq 重发被 ReplayGuard 拒绝也能按序排水）
                            publicReorderBuffer.putIfAbsent(update.fromCursor, update)
                            resendCheckpoint = PublicCheckpoint(
                                sessionId = curSessionId,
                                syncEpoch = curSyncEpoch,
                                cursor = curCursor
                            )
                        }
                    }
                    // fromCursor < 当前游标且 cursor > 当前游标的重叠前跳：协议不产出，保守丢弃（仍 ACK）
                }

                // 当前流有效消息（含重复/重传）必须在 ReplayGuard 之前 ACK，首个 ACK 丢失或重传场景才能自愈收敛
                if (shouldAck) {
                    val ackFrame = UdpFrame(
                        packetType = UdpPacketType.ACK,
                        connectionId = connectionId,
                        seq = nextSeq(),
                        ackSeq = frame.seq
                    )
                    sendFrame(ackFrame)
                    replayGuard.accept(frame.seq)
                }

                if (needSelfHeal) {
                    triggerPublicSnapshotSelfHeal()
                }
                resendCheckpoint?.let { sendPublicResendRequest(it) }
                for (item in toDispatch) {
                    dispatchPublicUpdate(item)
                }
            }

            PushType.PLAYER -> {
                if (!replayGuard.accept(frame.seq)) return

                val decoded = try {
                    ApiMessageCodec.decodePushEnvelope(json)
                } catch (_: Exception) {
                    return
                }

                val update = decoded.update
                val isFull = decoded.isFullSnapshot
                val effectiveUpdate = if (update.isFullSnapshot != isFull) update.copy(isFullSnapshot = isFull) else update
                val snap = effectiveUpdate.snapshot

                var dispatchItem: PlayerDispatchItem? = null
                var resendFromCursor: Long? = null
                var triggerHeal = false

                synchronized(stateLock) {
                    if (closed || boundRoomId == null || boundSeat == null) {
                        return
                    }

                    val curEpoch = bindingEpoch
                    val curRoom = boundRoomId!!
                    val curSeat = boundSeat!!
                    val curSessionId = boundSessionId
                    val curSyncEpoch = boundSyncEpoch

                    if (snap.view.viewerSeat != curSeat) return
                    if (curSessionId != null && snap.sessionId != curSessionId) return
                    if (curSyncEpoch != null && snap.syncEpoch != curSyncEpoch) return

                    val result = pushSequencer.offer(
                        cursor = snap.cursor,
                        fromCursor = effectiveUpdate.fromCursor,
                        isFullSnapshot = isFull
                    )

                    when (result) {
                        is PushOfferResult.Applied -> {
                            if (isFull) {
                                boundSessionId = snap.sessionId
                                boundSyncEpoch = snap.syncEpoch
                                earlyPushOverflowed.set(false)
                            }
                            if (earlyPushOverflowed.get() && !isFull) {
                                triggerHeal = true
                            } else {
                                dispatchItem = PlayerDispatchItem(
                                    epoch = curEpoch,
                                    roomId = curRoom,
                                    sessionId = snap.sessionId,
                                    syncEpoch = snap.syncEpoch,
                                    viewerSeat = curSeat,
                                    update = effectiveUpdate
                                )
                            }
                        }
                        is PushOfferResult.GapDetected -> {
                            if (earlyPushOverflowed.get()) {
                                triggerHeal = true
                            } else if (!isFull) {
                                resendFromCursor = result.missingFromCursor
                            }
                        }
                        is PushOfferResult.BeyondWindowGap -> {
                            triggerHeal = true
                        }
                        is PushOfferResult.Stale -> {
                            if (earlyPushOverflowed.get() && !isFull) {
                                triggerHeal = true
                            }
                        }
                    }
                }

                if (triggerHeal) {
                    triggerFullSnapshotSelfHeal()
                }
                resendFromCursor?.let { sendResendRequest(it) }
                dispatchItem?.let { dispatchUpdate(it) }
            }
        }
    }

    /**
     * 排水 PUBLIC 重排缓冲（必须在 stateLock 内调用）：从当前游标起按 fromCursor 严格连续消费
     */
    private fun drainPublicReorderUnderLock(
        out: MutableList<PublicDispatchItem>,
        epoch: Long,
        roomId: String
    ) {
        while (true) {
            val next = publicReorderBuffer.remove(lastPublicCursor) ?: break
            lastPublicCursor = next.snapshot.cursor
            out.add(PublicDispatchItem(epoch, roomId, next))
        }
    }

    private fun sendResendRequest(missingFromCursor: Long) {
        val payload = """{"fromCursor":$missingFromCursor}""".toByteArray(Charsets.UTF_8)
        val resendFrame = UdpFrame(
            packetType = UdpPacketType.RESEND,
            connectionId = connectionId,
            seq = nextSeq(),
            payload = payload
        )
        sendFrame(resendFrame)
    }

    private fun sendPublicResendRequest(checkpoint: PublicCheckpoint) {
        val json = ApiMessageCodec.encodePublicResendRequest(checkpoint)
        val payload = json.toByteArray(Charsets.UTF_8)
        val resendFrame = UdpFrame(
            packetType = UdpPacketType.RESEND,
            connectionId = connectionId,
            seq = nextSeq(),
            payload = payload
        )
        sendFrame(resendFrame)
    }

    private fun requestHealingSnapshot(
        roomId: String?,
        token: String?
    ): CompletableFuture<ProtocolPlayerSnapshot> {
        val apiReq = ApiRequest(
            api = ApiTags.SNAPSHOT,
            requestId = UUID.randomUUID().toString(),
            roomId = roomId,
            token = token
        )
        return sendRequest(apiReq) { rsp, _ ->
            rsp.snapshot ?: throw IllegalStateException("Missing snapshot in SNAPSHOT response")
        }
    }

    private fun triggerFullSnapshotSelfHeal() {
        val epochAtRequest: Long
        val healToken: PlayerHealToken
        val currentRoomId: String?
        val currentToken: String?
        synchronized(stateLock) {
            if (closed || boundRoomId == null || boundSeat == null) return
            epochAtRequest = bindingEpoch
            val inFlightToken = activePlayerHealToken.get()
            if (inFlightToken != null && inFlightToken.epoch == epochAtRequest) {
                // 当前绑定代次已有在途自愈请求，去重避免并发洪泛
                return
            }
            val token = PlayerHealToken(epochAtRequest, playerHealSeq.incrementAndGet())
            activePlayerHealToken.set(token)
            healToken = token
            currentRoomId = boundRoomId
            currentToken = resumeToken
        }

        requestHealingSnapshot(currentRoomId, currentToken).whenComplete { snap, _ ->
            try {
                // 换绑/离房/关闭后迟到的旧自愈响应不写回当前绑定状态
                if (snap != null) {
                    val toDispatch = mutableListOf<PlayerDispatchItem>()
                    synchronized(stateLock) {
                        if (!closed && bindingEpoch == epochAtRequest && boundRoomId != null && boundSeat != null) {
                            val currentCursor = pushSequencer.getCurrentCursor()
                            val curSessionId = boundSessionId
                            val curSyncEpoch = boundSyncEpoch
                            val isStale = (curSessionId != null && snap.sessionId != curSessionId) ||
                                (curSyncEpoch != null && snap.syncEpoch != curSyncEpoch) ||
                                (snap.view.viewerSeat != boundSeat) ||
                                (if (earlyPushOverflowed.get()) snap.cursor < currentCursor else snap.cursor <= currentCursor)
                            if (!isStale) {
                                pushSequencer.adoptFullSnapshot(snap.cursor)
                                earlyPushOverflowed.set(false)
                                boundSessionId = snap.sessionId
                                boundSyncEpoch = snap.syncEpoch
                                val update = ProtocolPlayerUpdate(
                                    snapshot = snap,
                                    fromCursor = snap.cursor,
                                    visibleEvents = emptyList(),
                                    isFullSnapshot = true
                                )
                                toDispatch.add(
                                    PlayerDispatchItem(
                                        epoch = epochAtRequest,
                                        roomId = boundRoomId!!,
                                        sessionId = snap.sessionId,
                                        syncEpoch = snap.syncEpoch,
                                        viewerSeat = snap.view.viewerSeat,
                                        update = update
                                    )
                                )
                            }
                        }
                    }
                    for (item in toDispatch) {
                        dispatchUpdate(item)
                    }
                }
            } finally {
                // 仅当 activePlayerHealToken 仍为本次发起的 healToken 时才释放标志；
                // 换绑或离房已将其清空或交由新代次，旧请求绝不能清除新代次的恢复标志！
                activePlayerHealToken.compareAndSet(healToken, null)
            }
        }
    }

    private fun triggerPublicSnapshotSelfHeal() {
        val epochAtRequest: Long
        val healToken: PublicHealToken
        synchronized(stateLock) {
            if (closed || boundSpectatorId == null) return
            epochAtRequest = bindingEpoch
            val currentToken = activePublicHealToken.get()
            if (currentToken != null && currentToken.epoch == epochAtRequest) {
                // 当前绑定代次已有在途自愈请求，去重避免并发洪泛
                return
            }
            val token = PublicHealToken(epochAtRequest, publicHealSeq.incrementAndGet())
            activePublicHealToken.set(token)
            healToken = token
        }

        publicSnapshot().whenComplete { snap, _ ->
            try {
                // 换绑/离房/关闭后迟到的旧自愈响应不写回当前绑定状态
                if (snap != null) {
                    val toDispatch = mutableListOf<PublicDispatchItem>()
                    synchronized(stateLock) {
                        if (!closed && bindingEpoch == epochAtRequest && boundSpectatorId != null && boundRoomId != null) {
                            val isNewStream = lastPublicSessionId != snap.sessionId || lastPublicSyncEpoch != snap.syncEpoch
                            val isStale = !isNewStream && snap.cursor < lastPublicCursor
                            if (!isStale) {
                                val update = ProtocolPublicUpdate(
                                    snapshot = snap,
                                    fromCursor = snap.cursor,
                                    visibleEvents = emptyList(),
                                    isFullSnapshot = true
                                )
                                toDispatch.add(PublicDispatchItem(epochAtRequest, boundRoomId!!, update))
                                drainPublicReorderUnderLock(toDispatch, epochAtRequest, boundRoomId!!)
                            }
                        }
                    }
                    for (item in toDispatch) {
                        dispatchPublicUpdate(item)
                    }
                }
            } finally {
                // 仅当 activePublicHealToken 仍为本次发起的 healToken 时才释放标志；
                // 换绑或离房已将其清空或交由新代次，旧请求绝不能清除新代次的恢复标志！
                activePublicHealToken.compareAndSet(healToken, null)
            }
        }
    }

    /**
     * 受发起时 bindingEpoch 保护的权威全量基线写回：
     * 新流允许建立新基线；同流仅允许游标前进或采纳当前有效基线，绝不回滚。
     *
     * @return 是否采纳了该权威基线
     */
    private fun adoptAuthoritativePublicSnapshot(snap: ProtocolPublicSnapshot, epochAtRequest: Long): Boolean {
        synchronized(stateLock) {
            if (closed || bindingEpoch != epochAtRequest || boundSpectatorId == null) {
                return false
            }
            val isNewStream = lastPublicSessionId != snap.sessionId || lastPublicSyncEpoch != snap.syncEpoch
            if (!isNewStream && snap.cursor < lastPublicCursor) {
                // 同流旧响应：不回滚，不采纳
                return false
            }
            lastPublicSessionId = snap.sessionId
            lastPublicSyncEpoch = snap.syncEpoch
            lastPublicCursor = maxOf(lastPublicCursor, snap.cursor)
            if (isNewStream) {
                publicReorderBuffer.clear()
            } else {
                publicReorderBuffer.headMap(snap.cursor).clear()
            }
            return true
        }
    }

    private fun isPlayerItemValidUnderLock(item: PlayerDispatchItem): Boolean {
        if (closed || bindingEpoch != item.epoch || boundRoomId != item.roomId || boundSeat == null) {
            return false
        }
        if (boundSeat != item.viewerSeat) {
            return false
        }
        if (boundSessionId != null && item.sessionId != boundSessionId) {
            return false
        }
        if (boundSyncEpoch != null && item.syncEpoch != boundSyncEpoch) {
            return false
        }
        return true
    }

    private fun dispatchUpdate(item: PlayerDispatchItem) {
        val listenersToNotify: List<(ProtocolPlayerUpdate) -> Unit>
        synchronized(stateLock) {
            if (!isPlayerItemValidUnderLock(item)) {
                // 绑定已失效（换绑、离房、关闭或身份不匹配）：丢弃旧绑定的推送，不向新绑定或大厅派发
                return
            }
            if (updateListeners.isEmpty()) {
                if (earlyPushBuffer.size < earlyPushCapacity && !earlyPushOverflowed.get()) {
                    earlyPushBuffer.add(item)
                } else {
                    // 早期缓冲溢出：清空部分回放并标记，注册监听器时改用权威全量自愈，绝不静默丢弃
                    earlyPushBuffer.clear()
                    earlyPushOverflowed.set(true)
                }
                return
            }
            // 如果早期缓冲曾溢出且尚未通过全量快照建立新基线，丢弃普通增量更新，绝不伪造连续更新
            if (earlyPushOverflowed.get() && !item.update.isFullSnapshot) {
                return
            }
            if (item.update.isFullSnapshot) {
                earlyPushOverflowed.set(false)
            }
            listenersToNotify = updateListeners.toList()
        }

        for (listener in listenersToNotify) {
            val valid = synchronized(stateLock) {
                isPlayerItemValidUnderLock(item)
            }
            if (!valid) break
            try {
                listener(item.update)
            } catch (_: Exception) {}
        }
    }

    private fun dispatchPublicUpdate(item: PublicDispatchItem) {
        val listenersToNotify: List<(ProtocolPublicUpdate) -> Unit>
        synchronized(stateLock) {
            if (closed || bindingEpoch != item.epoch || boundRoomId != item.roomId || boundSpectatorId == null) {
                // 绑定已失效（换绑、离房、关闭）：丢弃旧绑定的推送，不向新绑定或大厅派发
                return
            }
            if (publicUpdateListeners.isEmpty()) {
                if (earlyPublicPushBuffer.size < earlyPublicPushCapacity) {
                    earlyPublicPushBuffer.add(item)
                } else {
                    // 早期缓冲溢出：清空部分回放并标记，注册监听器时改用权威全量自愈，绝不静默丢最后一次变化
                    earlyPublicPushBuffer.clear()
                    earlyPublicPushOverflowed.set(true)
                }
                return
            }
            listenersToNotify = publicUpdateListeners.toList()
        }

        for (listener in listenersToNotify) {
            val valid = synchronized(stateLock) {
                !closed && bindingEpoch == item.epoch && boundRoomId == item.roomId && boundSpectatorId != null
            }
            if (!valid) break
            try {
                listener(item.update)
            } catch (_: Exception) {}
        }
    }

    private fun enqueueAndDrainRoomUpdate(item: RoomDispatchItem) {
        synchronized(stateLock) {
            if (closed || bindingEpoch != item.epoch || boundRoomId != item.roomId) {
                return
            }
            if (roomUpdateListeners.isEmpty()) {
                earlyRoomUpdateBuffer.add(item.roomInfo)
                return
            }
        }
        roomDispatchQueue.add(item)
        drainRoomUpdates()
    }

    private fun drainRoomUpdates() {
        if (drainWip.getAndIncrement() != 0) {
            return
        }

        var missed = 1
        while (true) {
            while (true) {
                val currentEpoch: Long
                val currentRoom: String?
                val isClosed: Boolean
                synchronized(stateLock) {
                    isClosed = closed
                    currentEpoch = bindingEpoch
                    currentRoom = boundRoomId
                }
                if (isClosed) {
                    roomDispatchQueue.clear()
                    break
                }

                val nextItem = roomDispatchQueue.poll() ?: break
                if (nextItem.epoch != currentEpoch || nextItem.roomId != currentRoom) {
                    continue
                }

                // 外部回调完全在状态锁外执行，保证有序、单调与代次隔离
                for (listener in roomUpdateListeners) {
                    val abortListener: Boolean = synchronized(stateLock) {
                        closed || bindingEpoch != nextItem.epoch || boundRoomId != nextItem.roomId
                    }
                    if (abortListener) {
                        break
                    }
                    val lastSeen = listenerMaxVersions[listener] ?: 0L
                    if (nextItem.version > lastSeen) {
                        listenerMaxVersions[listener] = nextItem.version
                        try {
                            listener(nextItem.roomInfo)
                        } catch (_: Exception) {}
                    }
                }
            }

            missed = drainWip.addAndGet(-missed)
            if (missed == 0) {
                break
            }
        }
    }

    // ==========================================
    // 通用请求发送辅助
    // ==========================================

    private fun <T> sendRequest(
        request: ApiRequest,
        responseMapper: (ApiResponse, UdpFrame) -> T
    ): CompletableFuture<T> {
        val future = CompletableFuture<T>()
        if (closed) {
            future.completeExceptionally(IOException("UdpClientBinding is closed: $disconnectReason"))
            return future
        }

        val json = ApiMessageCodec.encodeRequest(request)
        val payload = json.toByteArray(Charsets.UTF_8)

        reliableRequestor.send(request.requestId, payload).whenComplete { rspFrame, err ->
            if (err != null) {
                future.completeExceptionally(err)
            } else if (rspFrame != null) {
                try {
                    val rspJson = rspFrame.payload.toString(Charsets.UTF_8)
                    val response = ApiMessageCodec.decodeResponse(rspJson)
                    if (!response.success && response.receipt == null) {
                        val errorMsg = response.error ?: ApiErrors.INTERNAL_ERROR
                        future.completeExceptionally(
                            UdpFrameException(errorMsg, "Server rejected request: $errorMsg")
                        )
                    } else {
                        val result = responseMapper(response, rspFrame)
                        future.complete(result)
                    }
                } catch (e: Exception) {
                    future.completeExceptionally(e)
                }
            }
        }

        return future
    }

    // ==========================================
    // 统一绑定与生命周期状态切换 (必须在 stateLock 内调用)
    // ==========================================

    private fun applyPlayerBindingState(
        newBinding: RoomBinding,
        frameConnectionId: Int,
        isResume: Boolean
    ) {
        if (frameConnectionId != 0 && this.connectionId == 0) {
            this.connectionId = frameConnectionId
            this.terminatingConnectionId = frameConnectionId
        }
        this.boundRoomId = newBinding.roomId
        this.boundSeat = newBinding.seat
        this.resumeToken = newBinding.resumeToken
        this.boundSessionId = newBinding.snapshot.sessionId
        this.boundSyncEpoch = newBinding.snapshot.syncEpoch
        this.bindingEpoch++

        // 清除观战流状态
        this.boundSpectatorId = null
        this.boundSpectatorToken = null
        this.lastPublicSessionId = null
        this.lastPublicSyncEpoch = null
        this.lastPublicCursor = 0L
        this.publicReorderBuffer.clear()
        this.earlyPublicPushBuffer.clear()
        this.earlyPublicPushOverflowed.set(false)
        this.activePublicHealToken.set(null)

        // 清除/重置房间信息与派发队列
        this.lastRoomInfoVersion = 0L
        this.latestRoomInfo = null
        this.earlyRoomUpdateBuffer.clear()
        this.roomDispatchQueue.clear()
        this.listenerMaxVersions.clear()

        // 清除/重置玩家早期缓冲与自愈标记
        this.earlyPushBuffer.clear()
        this.earlyPushOverflowed.set(false)
        this.activePlayerHealToken.set(null)
    }

    private fun applySpectatorBindingState(
        binding: SpectatorBinding,
        frameConnectionId: Int,
        isResume: Boolean
    ) {
        if (frameConnectionId != 0 && this.connectionId == 0) {
            this.connectionId = frameConnectionId
            this.terminatingConnectionId = frameConnectionId
        }
        this.boundRoomId = binding.roomId
        this.boundSeat = null
        this.resumeToken = null
        this.boundSessionId = null
        this.boundSyncEpoch = null
        this.boundSpectatorId = binding.spectatorId
        this.boundSpectatorToken = binding.resumeToken
        this.bindingEpoch++

        // 清除玩家流状态
        this.earlyPushBuffer.clear()
        this.earlyPushOverflowed.set(false)
        this.activePlayerHealToken.set(null)

        // 清除/重置房间信息与派发队列
        this.lastRoomInfoVersion = 0L
        this.latestRoomInfo = null
        this.earlyRoomUpdateBuffer.clear()
        this.roomDispatchQueue.clear()
        this.listenerMaxVersions.clear()

        // 观战流基线与重排状态
        this.lastPublicSessionId = binding.snapshot.sessionId
        this.lastPublicSyncEpoch = binding.snapshot.syncEpoch
        this.lastPublicCursor = binding.snapshot.cursor
        this.publicReorderBuffer.clear()
        this.earlyPublicPushBuffer.clear()
        this.earlyPublicPushOverflowed.set(false)
        this.activePublicHealToken.set(null)
    }

    private fun applyLeaveRoomState() {
        this.connectionId = 0
        this.boundRoomId = null
        this.boundSeat = null
        this.resumeToken = null
        this.boundSessionId = null
        this.boundSyncEpoch = null
        this.boundSpectatorId = null
        this.boundSpectatorToken = null
        this.bindingEpoch++

        this.activePlayerHealToken.set(null)
        this.activePublicHealToken.set(null)

        this.lastRoomInfoVersion = 0L
        this.latestRoomInfo = null
        this.lastPublicSessionId = null
        this.lastPublicSyncEpoch = null
        this.lastPublicCursor = 0L

        this.publicReorderBuffer.clear()
        this.earlyPublicPushOverflowed.set(false)
        this.earlyRoomUpdateBuffer.clear()
        this.roomDispatchQueue.clear()
        this.listenerMaxVersions.clear()
        this.earlyPushBuffer.clear()
        this.earlyPushOverflowed.set(false)
        this.earlyPublicPushBuffer.clear()
    }

    private fun applyCloseState(reason: String): Boolean {
        if (!closed) {
            closed = true
            disconnectReason = reason
            if (connectionId != 0) {
                terminatingConnectionId = connectionId
            }
            boundSessionId = null
            boundSyncEpoch = null
            bindingEpoch++
            this.activePlayerHealToken.set(null)
            this.activePublicHealToken.set(null)
            earlyRoomUpdateBuffer.clear()
            roomDispatchQueue.clear()
            listenerMaxVersions.clear()
            earlyPushBuffer.clear()
            earlyPushOverflowed.set(false)
            earlyPublicPushBuffer.clear()
            earlyPublicPushOverflowed.set(false)
            publicReorderBuffer.clear()
            return true
        }
        return false
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
        return sendRequest(apiReq) { rsp, rspFrame ->
            val newBinding = RoomBinding(
                roomId = rsp.roomId ?: throw IllegalStateException("Missing roomId in CREATE_ROOM response"),
                seat = rsp.seat ?: throw IllegalStateException("Missing seat in CREATE_ROOM response"),
                resumeToken = rsp.resumeToken ?: throw IllegalStateException("Missing resumeToken in CREATE_ROOM response"),
                snapshot = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in CREATE_ROOM response")
            )
            synchronized(stateLock) {
                applyPlayerBindingState(newBinding, rspFrame.connectionId, false)
            }
            replayGuard.reset(0)
            pushSequencer.adoptFullSnapshot(newBinding.snapshot.cursor)
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
        return sendRequest(apiReq) { rsp, rspFrame ->
            val newBinding = RoomBinding(
                roomId = rsp.roomId ?: throw IllegalStateException("Missing roomId in JOIN_ROOM response"),
                seat = rsp.seat ?: throw IllegalStateException("Missing seat in JOIN_ROOM response"),
                resumeToken = rsp.resumeToken ?: throw IllegalStateException("Missing resumeToken in JOIN_ROOM response"),
                snapshot = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in JOIN_ROOM response")
            )
            synchronized(stateLock) {
                applyPlayerBindingState(newBinding, rspFrame.connectionId, false)
            }
            replayGuard.reset(0)
            pushSequencer.adoptFullSnapshot(newBinding.snapshot.cursor)
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
        return sendRequest(apiReq) { rsp, rspFrame ->
            val newBinding = RoomBinding(
                roomId = rsp.roomId ?: throw IllegalStateException("Missing roomId in RESUME_SESSION response"),
                seat = rsp.seat ?: throw IllegalStateException("Missing seat in RESUME_SESSION response"),
                resumeToken = rsp.resumeToken ?: throw IllegalStateException("Missing resumeToken in RESUME_SESSION response"),
                snapshot = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in RESUME_SESSION response")
            )
            synchronized(stateLock) {
                applyPlayerBindingState(newBinding, rspFrame.connectionId, true)
            }
            replayGuard.reset(0)
            pushSequencer.adoptFullSnapshot(newBinding.snapshot.cursor)
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
                replayGuard.reset(0)
                pushSequencer.reset(0L)
            }
            receipt
        }
    }

    override fun getRoomInfo(): CompletableFuture<ProtocolRoomInfo> {
        val currentRoomId: String?
        val currentEpoch: Long
        synchronized(stateLock) {
            currentRoomId = boundRoomId
            currentEpoch = bindingEpoch
        }
        val apiReq = ApiRequest(
            api = ApiTags.GET_ROOM_INFO,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId
        )
        return sendRequest(apiReq) { rsp, _ ->
            val info = rsp.roomInfo ?: throw IllegalStateException("Missing roomInfo in GET_ROOM_INFO response")
            val dispatchItem: RoomDispatchItem? = synchronized(stateLock) {
                if (!closed && bindingEpoch == currentEpoch && boundRoomId != null && boundRoomId == currentRoomId && info.roomId == currentRoomId) {
                    if (info.version > lastRoomInfoVersion) {
                        lastRoomInfoVersion = info.version
                        latestRoomInfo = info
                        RoomDispatchItem(
                            epoch = bindingEpoch,
                            roomId = boundRoomId!!,
                            version = info.version,
                            roomInfo = info
                        )
                    } else null
                } else null
            }
            if (dispatchItem != null) {
                enqueueAndDrainRoomUpdate(dispatchItem)
            }
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
        val earlyItems = mutableListOf<RoomDispatchItem>()
        synchronized(stateLock) {
            roomUpdateListeners.add(listener)
            listenerMaxVersions[listener] = 0L
            val currentEpoch = bindingEpoch
            val currentRoom = boundRoomId
            while (true) {
                val info = earlyRoomUpdateBuffer.poll() ?: break
                if (currentRoom != null && info.roomId == currentRoom) {
                    earlyItems.add(RoomDispatchItem(currentEpoch, currentRoom, info.version, info))
                }
            }
        }
        for (item in earlyItems) {
            roomDispatchQueue.add(item)
        }
        drainRoomUpdates()
        return AutoCloseable {
            roomUpdateListeners.remove(listener)
            listenerMaxVersions.remove(listener)
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
        val (currentRoomId, currentToken, epochAtRequest) = synchronized(stateLock) {
            Triple(boundRoomId, resumeToken, bindingEpoch)
        }
        val apiReq = ApiRequest(
            api = ApiTags.SNAPSHOT,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken
        )
        return sendRequest(apiReq) { rsp, _ ->
            val snap = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in SNAPSHOT response")
            synchronized(stateLock) {
                if (!closed && bindingEpoch == epochAtRequest && boundRoomId != null && boundSeat != null) {
                    if ((boundSessionId == null || snap.sessionId == boundSessionId) &&
                        (boundSyncEpoch == null || snap.syncEpoch == boundSyncEpoch) &&
                        snap.view.viewerSeat == boundSeat
                    ) {
                        pushSequencer.adoptFullSnapshot(snap.cursor)
                    }
                }
            }
            snap
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
        val (currentRoomId, currentToken, epochAtRequest) = synchronized(stateLock) {
            Triple(boundRoomId, resumeToken, bindingEpoch)
        }
        val apiReq = ApiRequest(
            api = ApiTags.RESUME_CURSOR,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken,
            cursor = cursor
        )
        return sendRequest(apiReq) { rsp, _ ->
            val snap = rsp.snapshot ?: throw IllegalStateException("Missing snapshot in RESUME_CURSOR response")
            synchronized(stateLock) {
                if (!closed && bindingEpoch == epochAtRequest && boundRoomId != null && boundSeat != null) {
                    pushSequencer.adoptFullSnapshot(snap.cursor)
                }
            }
            snap
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
    // PushChannel 推送通道实现
    // ==========================================

    override fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable {
        val itemsToDeliver = mutableListOf<PlayerDispatchItem>()
        val needSelfHeal: Boolean
        synchronized(stateLock) {
            updateListeners.add(listener)
            val overflowed = earlyPushOverflowed.get()
            val curEpoch = bindingEpoch
            val curRoom = boundRoomId
            val isPlayer = boundSeat != null && curRoom != null
            if (overflowed) {
                earlyPushBuffer.clear()
                needSelfHeal = isPlayer
            } else if (isPlayer) {
                needSelfHeal = false
                while (true) {
                    val item = earlyPushBuffer.poll() ?: break
                    if (isPlayerItemValidUnderLock(item)) {
                        itemsToDeliver.add(item)
                    }
                }
            } else {
                needSelfHeal = false
                earlyPushBuffer.clear()
            }
        }

        if (needSelfHeal) {
            triggerFullSnapshotSelfHeal()
        } else {
            for (item in itemsToDeliver) {
                val valid = synchronized(stateLock) {
                    isPlayerItemValidUnderLock(item)
                }
                if (!valid) break
                try {
                    listener(item.update)
                } catch (_: Exception) {}
            }
        }
        return AutoCloseable {
            updateListeners.remove(listener)
        }
    }

    // ==========================================
    // SpectatorApi 观战服务实现
    // ==========================================

    override fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding> {
        val apiReq = ApiRequest(
            api = ApiTags.JOIN_SPECTATOR,
            requestId = UUID.randomUUID().toString(),
            roomId = req.roomId,
            spectatorName = req.spectatorName,
            protocolVersion = req.protocolVersion
        )
        return sendRequest(apiReq) { rsp, rspFrame ->
            val binding = rsp.spectatorBinding ?: throw IllegalStateException("Missing spectatorBinding in JOIN_SPECTATOR response")
            synchronized(stateLock) {
                applySpectatorBindingState(binding, rspFrame.connectionId, false)
            }
            replayGuard.reset(0)
            pushSequencer.reset(0L)
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
        return sendRequest(apiReq) { rsp, rspFrame ->
            val binding = rsp.spectatorBinding ?: throw IllegalStateException("Missing spectatorBinding in RESUME_SPECTATOR response")
            synchronized(stateLock) {
                applySpectatorBindingState(binding, rspFrame.connectionId, true)
            }
            replayGuard.reset(0)
            pushSequencer.reset(0L)
            binding
        }
    }

    override fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot> {
        val (currentRoomId, currentToken, epochAtRequest) = synchronized(stateLock) {
            Triple(boundRoomId, boundSpectatorToken, bindingEpoch)
        }
        val apiReq = ApiRequest(
            api = ApiTags.PUBLIC_SNAPSHOT,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken
        )
        return sendRequest(apiReq) { rsp, _ ->
            val snap = rsp.publicSnapshot ?: throw IllegalStateException("Missing publicSnapshot in response")
            // 响应回写必须核对发起时的 bindingEpoch，离房/换绑后的旧响应不得污染新绑定
            adoptAuthoritativePublicSnapshot(snap, epochAtRequest)
            snap
        }
    }

    override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot> {
        val (currentRoomId, currentToken, epochAtRequest) = synchronized(stateLock) {
            Triple(boundRoomId, boundSpectatorToken, bindingEpoch)
        }
        val apiReq = ApiRequest(
            api = ApiTags.RESUME_PUBLIC,
            requestId = UUID.randomUUID().toString(),
            roomId = currentRoomId,
            token = currentToken,
            publicCheckpoint = checkpoint
        )
        return sendRequest(apiReq) { rsp, _ ->
            val snap = rsp.publicSnapshot ?: throw IllegalStateException("Missing publicSnapshot in response")
            // 响应回写必须核对发起时的 bindingEpoch，离房/换绑后的旧响应不得污染新绑定
            adoptAuthoritativePublicSnapshot(snap, epochAtRequest)
            snap
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
        val itemsToDeliver = mutableListOf<PublicDispatchItem>()
        val overflowed: Boolean
        synchronized(stateLock) {
            publicUpdateListeners.add(listener)
            overflowed = earlyPublicPushOverflowed.getAndSet(false)
            val curEpoch = bindingEpoch
            val curRoom = boundRoomId
            val isSpec = boundSpectatorId != null
            if (!overflowed && isSpec && curRoom != null) {
                while (true) {
                    val item = earlyPublicPushBuffer.poll() ?: break
                    if (item.epoch == curEpoch && item.roomId == curRoom) {
                        itemsToDeliver.add(item)
                    }
                }
            } else {
                earlyPublicPushBuffer.clear()
            }
        }

        if (overflowed) {
            // 部分回放无法保证游标连续：改由受绑定保护的权威全量自愈重建基线
            triggerPublicSnapshotSelfHeal()
        } else {
            // 早期缓冲按序回放
            for (item in itemsToDeliver) {
                val valid = synchronized(stateLock) {
                    !closed && bindingEpoch == item.epoch && boundRoomId == item.roomId && boundSpectatorId != null
                }
                if (!valid) break
                try {
                    listener(item.update)
                } catch (_: Exception) {}
            }
        }
        return AutoCloseable {
            publicUpdateListeners.remove(listener)
        }
    }

    override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
        disconnectListeners.add(listener)
        if (closed && disconnectReason != null) {
            try {
                listener(disconnectReason!!)
            } catch (_: Exception) {}
        }
        return AutoCloseable {
            disconnectListeners.remove(listener)
        }
    }

    private fun notifyDisconnect(reason: String) {
        for (listener in disconnectListeners) {
            try {
                listener(reason)
            } catch (_: Exception) {}
        }
    }

    // ==========================================
    // 生命周期管理
    // ==========================================

    fun close(reason: String) {
        val shouldClose = synchronized(stateLock) {
            applyCloseState(reason)
        }
        if (shouldClose) {
            reliableRequestor.close()
            fragmenter.close()
            heartbeatPacer.close()
            onCloseCallback?.invoke()
        }
    }

    override fun close() {
        close(ApiProtocol.LOCAL_CLOSED)
    }
}

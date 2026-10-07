package com.majiang.server.udp

import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.server.api.ApiErrors
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.udp.PushRetransmitWindow
import com.majiang.transport.udp.ReplayGuard
import com.majiang.transport.udp.ResendResponse
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpProtocol
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * 会话绑定角色：玩家席位 (seat 0..3) 或独立观战者 (seat = [UdpClientSession.SPECTATOR_SEAT] 且携带 spectatorId)。
 * 角色决定该会话可用的 API 集合与重传窗口；玩家 token 与观战 token 的索引/恢复路径完全分离。
 */
enum class UdpSessionRole {
    PLAYER,
    SPECTATOR
}

/**
 * UDP 客户端已连接会话状态 (UdpClientSession)
 *
 * 遵循 doc/SERVER_CLIENT_API.md §7 与 TASK-ANTI-T4 交付契约：
 * 1. 唯一会话标识：32 位非零随机数 [connectionId]；
 * 2. 绑定身份：[roomId]、座位 [seat] (0..3)、[token] (断线恢复令牌) 与连接代次 [generation]；
 * 3. 双向防重放：[inboundReplayGuard]（入站防重复/滞后 seq）与 [outboundReplayGuard]（出站单调防回退）；
 * 4. 推送重传与全量自愈：集成 [PushRetransmitWindow]，注入按座位快照供应者 [snapshotSupplier]，
 *    缺口在窗口内按序补发，超出窗口直接生成全量快照自愈；
 * 5. 幂等回执缓存：[idempotentCache] 记录 `requestId -> ApiResponse`，UDP 丢包重传时直接重放已生成回执，绝不二次执行动作；
 * 6. 保活监测：维护最近入站时间戳与超时判定；支持顶号标记 ([isDisplaced]) 与幂等释放。
 *
 * @param connectionId 32 位随机非零会话 ID
 * @param remoteAddress 当前客户端网络端点地址（支持对端端口/IP 变化时自适应更新）
 * @param roomId 绑定的房间号
 * @param seat 绑定的席位索引 (0..3)；观战会话固定为 [UdpClientSession.SPECTATOR_SEAT]
 * @param token 断线恢复令牌（玩家席位令牌或观战恢复令牌，两种索引完全分离）
 * @param generation 连接代次（新连接顶号时自增；观战会话即观战成员代次）
 * @param clock 注入的协议时钟（测试注入虚拟时钟，生产注入系统时钟）
 * @param snapshotSupplier 全量快照供应函数（供重传窗口超出时自愈，仅玩家会话使用）
 * @param maxRetransmitWindowSize 每座位重传窗口容量，默认 256
 * @param spectatorId 观战者标识；非空时会话为独立观战绑定（seat 必须为 SPECTATOR_SEAT）
 */
class UdpClientSession(
    val connectionId: Int,
    @Volatile var remoteAddress: SocketAddress,
    val roomId: String,
    val seat: Int,
    val token: String,
    val generation: Long,
    val clock: UdpClock = UdpClock.SYSTEM,
    val snapshotSupplier: () -> PlayerSnapshot = { error("No snapshot supplier configured for seat $seat") },
    maxRetransmitWindowSize: Int = UdpProtocol.RETRANSMIT_WINDOW_SIZE,
    val spectatorId: String? = null
) : AutoCloseable {

    init {
        require(connectionId != 0) { "connectionId cannot be 0" }
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        if (spectatorId != null) {
            require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
            require(seat == SPECTATOR_SEAT) { "spectator session seat must be $SPECTATOR_SEAT, got $seat" }
        } else {
            require(seat in 0..3) { "seat must be in 0..3: $seat" }
        }
        require(token.isNotBlank()) { "token cannot be blank" }
        require(generation >= 0L) { "generation must be >= 0: $generation" }
    }

    /** 会话绑定角色：玩家或独立观战者 */
    val role: UdpSessionRole = if (spectatorId != null) UdpSessionRole.SPECTATOR else UdpSessionRole.PLAYER
    val isSpectator: Boolean get() = role == UdpSessionRole.SPECTATOR

    val inboundReplayGuard: ReplayGuard = ReplayGuard(expectedConnectionId = connectionId)
    val outboundReplayGuard: ReplayGuard = ReplayGuard(expectedConnectionId = connectionId)

    private val outboundSeqGenerator = AtomicInteger(1)

    val pushWindow: PushRetransmitWindow = PushRetransmitWindow(maxRetransmitWindowSize)

    /** 独立观战公开流重传窗口：与玩家 pushWindow 完全分离，绝不混用游标命名空间 */
    val publicWindow: PushRetransmitWindow = PushRetransmitWindow(maxRetransmitWindowSize)

    data class CachedSessionEntry(
        val response: ApiResponse,
        val api: String,
        val rawPayload: String,
        val request: ApiRequest? = null,
        val timestamp: Long = 0L
    )

    data class InFlightSessionEntry(
        val future: CompletableFuture<ApiResponse>,
        val api: String,
        val rawPayload: String,
        val request: ApiRequest? = null,
        val timestamp: Long = 0L
    )

    private val idempotentCache = ConcurrentHashMap<String, CachedSessionEntry>()
    private val _lastInboundTime = AtomicLong(clock.currentTimeMillis())

    private val _isDisplaced = AtomicBoolean(false)
    private val _isClosed = AtomicBoolean(false)

    val lastInboundTime: Long get() = _lastInboundTime.get()
    val isDisplaced: Boolean get() = _isDisplaced.get()
    val isClosed: Boolean get() = _isClosed.get()

    /**
     * 分配下一个单调递增的出站 seq，并通过出站 ReplayGuard 登记
     */
    fun nextOutboundSeq(): Int {
        val s = outboundSeqGenerator.getAndIncrement()
        outboundReplayGuard.accept(s)
        return s
    }

    /**
     * 收到对端入站数据包，刷新最近活跃保活时间戳
     */
    fun recordInbound(timestamp: Long = clock.currentTimeMillis()) {
        _lastInboundTime.set(timestamp)
    }

    /**
     * 检查当前连接是否已达到入站判死超时（默认 10 秒无任何入站包）
     */
    fun isTimedOut(
        now: Long = clock.currentTimeMillis(),
        timeoutMillis: Long = UdpProtocol.INBOUND_TIMEOUT_MILLIS
    ): Boolean {
        return (now - _lastInboundTime.get()) >= timeoutMillis
    }

    /**
     * 查询指定 requestId 的历史回执（幂等基石）
     */
    fun getCachedEntry(requestId: String): CachedSessionEntry? {
        val entry = idempotentCache[requestId] ?: return null
        if (clock.currentTimeMillis() - entry.timestamp > 60000L) {
            idempotentCache.remove(requestId)
            return null
        }
        return entry
    }
    fun getCachedResponse(requestId: String): ApiResponse? = getCachedEntry(requestId)?.response

    /**
     * 写入幂等缓存（有界淘汰最老条目与超时条目）
     */
    fun cacheResponse(
        requestId: String,
        response: ApiResponse,
        api: String = "",
        rawPayload: String = "",
        request: ApiRequest? = null
    ) {
        val now = clock.currentTimeMillis()
        if (idempotentCache.size >= MAX_IDEMPOTENT_CACHE_ENTRIES) {
            val it = idempotentCache.entries.iterator()
            while (it.hasNext()) {
                val item = it.next()
                if (now - item.value.timestamp > 60000L) {
                    it.remove()
                }
            }
            if (idempotentCache.size >= MAX_IDEMPOTENT_CACHE_ENTRIES) {
                val it2 = idempotentCache.keys.iterator()
                if (it2.hasNext()) {
                    idempotentCache.remove(it2.next())
                }
            }
        }
        idempotentCache[requestId] = CachedSessionEntry(
            response = response,
            api = api,
            rawPayload = rawPayload,
            request = request,
            timestamp = now
        )
    }

    private val inFlightRequests = ConcurrentHashMap<String, InFlightSessionEntry>()
    fun getInFlightEntry(requestId: String): InFlightSessionEntry? = inFlightRequests[requestId]
    fun getInFlight(requestId: String): CompletableFuture<ApiResponse>? = inFlightRequests[requestId]?.future

    fun putInFlightEntry(requestId: String, entry: InFlightSessionEntry): InFlightSessionEntry? {
        val now = clock.currentTimeMillis()
        val it = inFlightRequests.entries.iterator()
        while (it.hasNext()) {
            val item = it.next()
            if (now - item.value.timestamp > 15000L) {
                it.remove()
                item.value.future.completeExceptionally(
                    IllegalStateException("In-flight request timed out")
                )
            }
        }

        val existing = inFlightRequests[requestId]
        if (existing != null) {
            return existing
        }

        if (inFlightRequests.size >= MAX_IN_FLIGHT_ENTRIES) {
            entry.future.complete(
                ApiResponse(requestId = requestId, success = false, error = ApiErrors.SERVER_BUSY)
            )
            return entry
        }

        return inFlightRequests.putIfAbsent(requestId, entry)
    }

    fun putInFlight(
        requestId: String,
        future: CompletableFuture<ApiResponse>,
        api: String = "",
        rawPayload: String = "",
        request: ApiRequest? = null
    ): CompletableFuture<ApiResponse>? {
        val entry = InFlightSessionEntry(future, api, rawPayload, request, clock.currentTimeMillis())
        val existingOrRejected = putInFlightEntry(requestId, entry)
        return existingOrRejected?.future
    }

    fun removeInFlight(requestId: String): CompletableFuture<ApiResponse>? = inFlightRequests.remove(requestId)?.future

    /**
     * 记录一条已派发的推送更新到重传窗口
     */
    fun recordPush(cursor: Long, fromCursor: Long, payload: ByteArray) {
        pushWindow.record(connectionId, cursor, fromCursor, payload)
    }

    /**
     * 记录一条已派发的公开流推送更新到独立观战重传窗口
     */
    fun recordPublicPush(cursor: Long, fromCursor: Long, payload: ByteArray) {
        publicWindow.record(connectionId, cursor, fromCursor, payload)
    }

    /**
     * 处理客户端提交的 RESEND 请求
     */
    fun handleResend(fromCursor: Long): ResendResponse {
        return pushWindow.handleResend(connectionId, fromCursor) {
            val snap = try {
                snapshotSupplier()
            } catch (_: Throwable) {
                null
            }
            if (snap != null) {
                val fullUpdate = ProtocolPlayerUpdate(
                    snapshot = EngineProtocolMapper.toProtocolSnapshot(snap),
                    fromCursor = snap.cursor,
                    visibleEvents = emptyList()
                )
                ApiMessageCodec.encodePush(fullUpdate, isFullSnapshot = true).toByteArray(Charsets.UTF_8)
            } else {
                ByteArray(0)
            }
        }
    }

    /**
     * 标记被新连接顶替（仅设置状态标志，不立即关闭重传窗口与端点）
     */
    fun markDisplaced(): Boolean = _isDisplaced.compareAndSet(false, true)

    override fun close() {
        if (_isClosed.compareAndSet(false, true)) {
            pushWindow.removeConnection(connectionId)
            publicWindow.removeConnection(connectionId)
            val it = inFlightRequests.entries.iterator()
            while (it.hasNext()) {
                val item = it.next()
                it.remove()
                item.value.future.completeExceptionally(
                    IllegalStateException("Session closed")
                )
            }
        }
    }

    override fun toString(): String {
        return "UdpClientSession(connId=$connectionId, roomId=$roomId, seat=$seat, spectatorId=$spectatorId, gen=$generation, displaced=$isDisplaced, closed=$isClosed)"
    }

    companion object {
        const val MAX_IDEMPOTENT_CACHE_ENTRIES: Int = 512
        const val MAX_IN_FLIGHT_ENTRIES: Int = 256

        /** 观战会话的固定 seat 值：观战者不属于四座中的任何一席 */
        const val SPECTATOR_SEAT: Int = -1
    }
}

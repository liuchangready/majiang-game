package com.majiang.server.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.ServerApiException
import com.majiang.server.idempotency.AdmissionResult
import com.majiang.server.idempotency.CompletedReceipt
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.idempotency.InFlightReceipt
import com.majiang.server.idempotency.RequestScopeReceiptStore
import com.majiang.server.room.RoomRegistry
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.common.TransportClock
import com.majiang.transport.tcp.TcpScope
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.locks.ReentrantReadWriteLock

/**
 * 活跃在途请求及其准入元数据
 */
internal data class TrackedInFlight(
    val inFlight: InFlightReceipt,
    val isLobby: Boolean
)

/**
 * 业务回执与跨连接幂等在途管理器 (TcpReceiptManager, D-08 / §9.2 / MR-03)
 *
 * 核心架构职责：
 * 1. 跨连接身份与作用域幂等管理：业务回执与在途请求归属于已验证逻辑身份与 requestScope，
 *    彻底解耦物理 TCP 连接；连接重连或顶号时，能够安全重放已完成回执或关联同一在途 Future；
 * 2. 在途所有权与生命周期解耦：单次物理连接断开不破坏仍在执行的权威业务动作与在途条目；
 * 3. 继承 MR-03 完整契约：FIFO 淘汰治理（查询缓存）、有效期内副作用不淘汰、容量预留前置准入
 *    (SERVER_BUSY)、跨 API requestId 冲突检测 (CONFLICT) 与作用域到期未决对账 (REQUEST_SCOPE_EXPIRED)；
 * 4. 回执新鲜度联动 (MR-06 / R06-T10)：重放涉及房间创建/绑定的回执时，强校验房间是否仍存活，已回收房间返回 ROOM_NOT_FOUND；
 * 5. GET_REQUEST_SCOPE 权威控制查询：每秒 1 次限速防护，同身份并发返回统一作用域；
 * 6. 确定性关闭管理与原子生命周期边界：
 *    - 准入/在途登记与关机形成绝对原子互斥（基于 ReentrantReadWriteLock 门控）；
 *    - 关机时携带原准入的 isLobby 状态彻底释放大厅预留与成员配额，绝不残留泄漏；
 *    - 与关机并发的准入均被立即拒绝或结构化失败并回滚。
 */
class TcpReceiptManager(
    val store: RequestScopeReceiptStore = RequestScopeReceiptStore(),
    val isRoomOpen: (roomId: String) -> Boolean = { true },
    val clock: TransportClock = store.clock
) : AutoCloseable {

    constructor(
        store: RequestScopeReceiptStore,
        registry: RoomRegistry,
        clock: TransportClock = store.clock
    ) : this(
        store = store,
        isRoomOpen = { roomId ->
            val room = registry.getRoom(roomId)
            room != null && !room.closed
        },
        clock = clock
    )

    private val isClosed = AtomicBoolean(false)
    private val lifecycleLock = ReentrantReadWriteLock()

    // 活跃在途请求集合 (跨物理连接维护在途状态所有权，保留原准入 isLobby 属性)
    private val activeInFlightEntries = ConcurrentHashMap<IdempotencyKey, TrackedInFlight>()

    val closed: Boolean get() = isClosed.get()

    /**
     * 颁发大厅作用域凭据 (WELCOME)
     */
    fun issueLobbyScope(
        principal: String,
        validityMillis: Long = store.defaultScopeValidityMillis,
        nowMillis: Long = clock.currentTimeMillis()
    ): TcpScope {
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                throw ServerApiException(ApiProtocol.SERVER_SHUTDOWN, "Server is shutting down")
            }
            return store.issueLobbyScope(principal, validityMillis, nowMillis)
        } finally {
            lifecycleLock.readLock().unlock()
        }
    }

    /** Return the current scope for an authenticated lobby identity after a role leaves a room. */
    fun currentScopeForPrincipal(principal: String, nowMillis: Long = clock.currentTimeMillis()): TcpScope {
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                throw ServerApiException(ApiProtocol.SERVER_SHUTDOWN, "Server is shutting down")
            }
            return store.getOrCreateScopeForPrincipal(principal, nowMillis, recordQueryTime = false).first
        } finally {
            lifecycleLock.readLock().unlock()
        }
    }

    /**
     * 颁发角色作用域凭据 (JOIN_ROOM / RESUME_SESSION / JOIN_SPECTATOR / RESUME_SPECTATOR)
     */
    fun issueRoleScope(
        principal: String,
        validityMillis: Long = store.defaultScopeValidityMillis,
        nowMillis: Long = clock.currentTimeMillis()
    ): TcpScope {
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                throw ServerApiException(ApiProtocol.SERVER_SHUTDOWN, "Server is shutting down")
            }
            return store.issueRoleScope(principal, validityMillis, nowMillis)
        } finally {
            lifecycleLock.readLock().unlock()
        }
    }

    /**
     * GET_REQUEST_SCOPE 权威控制查询处理 (D9 / Design §5 / UC-R07)
     *
     * @return Pair<ApiResponse, TcpScope?>: 业务响应及可选授权的作用域
     */
    fun handleGetRequestScope(
        identity: LogicalIdentityContext,
        requestId: String,
        nowMillis: Long = clock.currentTimeMillis()
    ): Pair<ApiResponse, TcpScope?> {
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                val resp = ApiResponse(requestId = requestId, success = false, error = ApiProtocol.SERVER_SHUTDOWN)
                return resp to null
            }
            val principal = identity.principal
            if (store.isScopeQueryRateLimited(principal, nowMillis)) {
                val resp = ApiResponse(requestId = requestId, success = false, error = ApiErrors.SERVER_BUSY)
                return resp to null
            }
            val (scope, _) = store.getOrCreateScopeForPrincipal(principal, nowMillis)
            val resp = ApiResponse(requestId = requestId, success = true)
            return resp to scope
        } finally {
            lifecycleLock.readLock().unlock()
        }
    }

    /**
     * 请求受理准入裁决与回执关联 (核心幂等网关)
     * 严格处于读锁内，保证准入判定、在途登记与关闭原子互斥，绝不在关闭后遗漏在途登记
     */
    fun acquireAdmission(
        key: IdempotencyKey,
        request: ApiRequest,
        rawPayload: String,
        isSideEffect: Boolean,
        isLobby: Boolean = false,
        nowMillis: Long = clock.currentTimeMillis()
    ): AdmissionResult {
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                return AdmissionResult.ServerBusy
            }

            val rawResult = store.acquireAdmission(
                key = key,
                request = request,
                rawPayload = rawPayload,
                isSideEffect = isSideEffect,
                isLobby = isLobby,
                nowMillis = nowMillis
            )

            if (rawResult is AdmissionResult.Admitted) {
                // 读锁不变量保证：
                // 1. acquireAdmission 全程持有 lifecycleLock.readLock()；
                // 2. close() 执行时必须持有 lifecycleLock.writeLock()；
                // 3. 因此在读锁保护期内，close() 绝对无法进入并写 isClosed = true；
                // 4. 进入此分支说明未关机，原子登记 activeInFlightEntries[key] 后，
                //    后发的 close() 必能在写锁内完整遍历到本条目并以 SERVER_SHUTDOWN 统一清理。
                activeInFlightEntries[key] = TrackedInFlight(rawResult.inFlight, isLobby)
            }

            return when (rawResult) {
                is AdmissionResult.Replay -> {
                    // 回执新鲜度联动 (MR-06 / R06-T10): 校验房间存活状态
                    val originalResp = rawResult.completed.response
                    val targetRoomId = originalResp.roomId ?: originalResp.spectatorBinding?.roomId
                    val responseToSend = if (originalResp.success && targetRoomId != null) {
                        if (!isRoomOpen(targetRoomId)) {
                            ApiResponse(requestId = key.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                        } else {
                            originalResp
                        }
                    } else {
                        originalResp
                    }
                    val effectiveReceipt = if (responseToSend === originalResp) {
                        rawResult.completed
                    } else {
                        rawResult.completed.copy(response = responseToSend)
                    }
                    AdmissionResult.Replay(effectiveReceipt)
                }
                else -> rawResult
            }
        } finally {
            lifecycleLock.readLock().unlock()
        }
    }

    /**
     * 业务执行成功或业务明确拒绝，归档回执并原子完成在途请求
     *
     * 处于 lifecycleLock 读锁内与 close() 排它互斥：
     * 1. 若服务已关机，绝不归档回执，避免关机后残留新副作用；
     * 2. 按 (key, inFlight) 实例身份条件移除 activeInFlightEntries，防止迟到回调误删同 key 新条目；
     * 3. 读锁覆盖 activeInFlightEntries 移除与 store.completeReceipt 全程，杜绝 close() 插在两者之间；
     * 4. 关键：Future 用户回调 (whenComplete 等) 绝不在读锁内执行，以彻底避免回调中同步联动 close() 导致的读锁升级死锁 (R12)。
     */
    fun completeReceipt(
        key: IdempotencyKey,
        inFlight: InFlightReceipt,
        response: ApiResponse,
        grantedScope: TcpScope? = null,
        isLobby: Boolean = false,
        nowMillis: Long = clock.currentTimeMillis()
    ) {
        var shouldCompleteFuture = false
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                return
            }
            var removedTracked: TrackedInFlight? = null
            activeInFlightEntries.compute(key) { _, current ->
                if (current != null && current.inFlight === inFlight) {
                    removedTracked = current
                    null
                } else {
                    current
                }
            }
            if (removedTracked == null) {
                return
            }
            val effectiveIsLobby = isLobby || removedTracked.isLobby || inFlight.isLobby
            val storeRemoved = store.completeReceipt(
                key = key,
                inFlight = inFlight,
                response = response,
                grantedScope = grantedScope,
                isLobby = effectiveIsLobby,
                nowMillis = nowMillis,
                completeFuture = false
            )
            if (storeRemoved) {
                shouldCompleteFuture = true
            }
        } finally {
            lifecycleLock.readLock().unlock()
        }

        // 读锁外触发用户 Future 回调：绝不持有任何读写锁，允许回调同步联动 close() 而无死锁 (R12)
        if (shouldCompleteFuture) {
            inFlight.future.complete(response)
        }
    }

    /**
     * 在途请求异常失败（例如权威超时或内部故障）
     *
     * 处于 lifecycleLock 读锁内与 close() 排它互斥：
     * 1. 若服务已关机，说明已由 close() 统一以 SERVER_SHUTDOWN 失败并释放配额，忽略后发失败；
     * 2. 按 (key, inFlight) 实例身份条件移除 activeInFlightEntries，防止迟到回调误删同 key 新条目；
     * 3. 读锁覆盖 activeInFlightEntries 移除与 store.failInFlight 全程，杜绝 close() 竞态交错；
     * 4. 关键：Future 用户回调绝不在读锁内执行，允许回调同步联动 close() 而无死锁 (R12)。
     */
    fun failInFlight(
        key: IdempotencyKey,
        inFlight: InFlightReceipt,
        throwable: Throwable,
        isLobby: Boolean = false
    ) {
        var shouldCompleteFuture = false
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) {
                return
            }
            var removedTracked: TrackedInFlight? = null
            activeInFlightEntries.compute(key) { _, current ->
                if (current != null && current.inFlight === inFlight) {
                    removedTracked = current
                    null
                } else {
                    current
                }
            }
            if (removedTracked == null) {
                return
            }
            val effectiveIsLobby = isLobby || removedTracked.isLobby || inFlight.isLobby
            val storeRemoved = store.failInFlight(
                key = key,
                inFlight = inFlight,
                throwable = throwable,
                isLobby = effectiveIsLobby,
                completeFuture = false
            )
            if (storeRemoved) {
                shouldCompleteFuture = true
            }
        } finally {
            lifecycleLock.readLock().unlock()
        }

        // 读锁外触发用户 Future 回调
        if (shouldCompleteFuture) {
            inFlight.future.completeExceptionally(throwable)
        }
    }

    /**
     * 查询指定幂等键已完成的回执
     */
    fun findReceipt(key: IdempotencyKey): CompletedReceipt? = store.findReceipt(key)

    /**
     * 查询指定幂等键当前挂起的在途请求
     */
    fun findInFlight(key: IdempotencyKey): InFlightReceipt? {
        return activeInFlightEntries[key]?.inFlight ?: store.findInFlight(key)
    }

    /**
     * 定期清扫已过期且无在途请求的作用域及回执 (UC-R06 / R4.1 / R4.2)
     */
    fun cleanExpiredScopes(nowMillis: Long = clock.currentTimeMillis()) {
        lifecycleLock.readLock().lock()
        try {
            if (isClosed.get()) return
            store.cleanExpiredScopes(nowMillis)
        } finally {
            lifecycleLock.readLock().unlock()
        }
    }

    /**
     * 服务关机处理：持写锁形成原子边界，中断所有挂起在途请求并传递原 isLobby 彻底释放所有预留容量 (UC-C04 / R11)
     * 关键：所有 Future 异常完成均在释放写锁后执行，绝不在写锁内触发用户回调 (R12)。
     */
    override fun close() {
        val shutdownEx = ServerApiException(ApiProtocol.SERVER_SHUTDOWN, "Server is shutting down")
        val entriesToFail = mutableListOf<InFlightReceipt>()
        lifecycleLock.writeLock().lock()
        try {
            if (!isClosed.compareAndSet(false, true)) {
                return
            }
            val it = activeInFlightEntries.entries.iterator()
            while (it.hasNext()) {
                val entry = it.next()
                it.remove()
                try {
                    val storeRemoved = store.failInFlight(
                        key = entry.key,
                        inFlight = entry.value.inFlight,
                        throwable = shutdownEx,
                        isLobby = entry.value.isLobby,
                        completeFuture = false
                    )
                    if (storeRemoved) {
                        entriesToFail.add(entry.value.inFlight)
                    }
                } catch (_: Throwable) {
                }
            }
        } finally {
            lifecycleLock.writeLock().unlock()
        }

        // 写锁外触发用户 Future 异常完成：避免在写锁内触发外部回调导致死锁或重入问题
        for (inFlight in entriesToFail) {
            inFlight.future.completeExceptionally(shutdownEx)
        }
    }
}

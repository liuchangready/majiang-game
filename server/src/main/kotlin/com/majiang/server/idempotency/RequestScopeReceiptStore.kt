package com.majiang.server.idempotency

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ServerApiException
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.common.TransportClock
import com.majiang.transport.tcp.TcpScope
import java.security.SecureRandom
import java.util.Collections
import java.util.LinkedHashMap
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * 跨连接共享业务回执与 RequestScope 存储 (RequestScopeReceiptStore, F12 / Design §5 / UC-R01~R11)
 *
 * 核心职责：
 * 1. 作用域生命周期：颁发 lobbyScope 与 roleScope，初始 10 分钟；校验 scope 合法性、防伪造；
 * 2. GET_REQUEST_SCOPE 权威查询：同身份并发返回同一结果，未过期返回原 scope，过期按需新建，限速 1 次/秒；
 * 3. 幂等与冲突检测：三元组 (logicalPrincipal, requestScope, requestId) 去重；同载荷重放原结果或关联同一在途 Future，
 *    不同载荷/API 返回 CONFLICT (D1)；
 * 4. 预留-消耗容量治理：每成员最多 512 个已受理副作用（在途 <= 64），大厅副作用池 1024 / 查询缓存 256；
 *    副作用容量满在受理前返回 SERVER_BUSY (D2)；有效期内副作用回执不做 FIFO 淘汰；
 * 5. 过期 scope 防重做：到期 scope 原请求先查保留回执（有则重放），无则返回 REQUEST_SCOPE_EXPIRED (D3)，绝不重新执行；
 * 6. 比较删除与闭环：在途条目采用原子比较删除，防止迟到完成删除新条目；完成时精准扣减预留容量；
 * 7. InProcessServer 共享等价入口：单机与联机复用同一套存储与幂等逻辑，避免双轨实现。
 */
class RequestScopeReceiptStore(
    val clock: TransportClock = TransportClock.SYSTEM,
    val maxInFlightPerMember: Int = 64,
    val maxCompletedPerMember: Int = 512,
    val maxLobbySideEffectReceipts: Int = 1024,
    val maxLobbyQueryReceipts: Int = 256,
    val defaultScopeValidityMillis: Long = 10 * 60 * 1000L, // 10 minutes
    val scopeQueryRateLimitMillis: Long = 1000L // 1 req/sec
) {
    private val random = SecureRandom()
    private val stateLock = Any()

    // 作用域映射：scopeString -> ScopeRecord
    private val scopesByString = ConcurrentHashMap<String, ScopeRecord>()

    // 当前活跃作用域：principal -> ScopeRecord
    private val activeScopeByPrincipal = ConcurrentHashMap<String, ScopeRecord>()

    // 上次查询/创建 scope 时间：principal -> timestamp
    private val lastScopeQueryTime = ConcurrentHashMap<String, Long>()

    // 在途请求集合：IdempotencyKey -> InFlightReceipt
    private val inFlightRequests = ConcurrentHashMap<IdempotencyKey, InFlightReceipt>()

    // 成员在途请求计数：principal -> AtomicInteger
    private val memberInFlightCounters = ConcurrentHashMap<String, AtomicInteger>()

    // 成员副作用预留中计数：principal -> AtomicInteger
    private val memberReservedCounters = ConcurrentHashMap<String, AtomicInteger>()

    // 大厅副作用预留计数
    private val lobbySideEffectReserved = AtomicInteger(0)

    // 已完成副作用回执：IdempotencyKey -> CompletedReceipt (LinkedHashMap 保持插入顺序，但有效期内绝不淘汰)
    private val completedSideEffects = LinkedHashMap<IdempotencyKey, CompletedReceipt>()

    // 已完成查询回执缓存（有界 FIFO 淘汰，容量 maxLobbyQueryReceipts）
    private val completedQueries = LinkedHashMap<IdempotencyKey, CompletedReceipt>()

    // 成员已完成副作用计数：principal -> Int
    private val memberCompletedSideEffectCounts = HashMap<String, Int>()

    /**
     * 生成安全随机的 Base64URL/Hex 令牌 (≥ 128 bit 熵)
     */
    fun generateSecureToken(): String {
        val bytes = ByteArray(16) // 128 bit
        random.nextBytes(bytes)
        val sb = StringBuilder(32)
        for (b in bytes) {
            sb.append(String.format("%02x", b))
        }
        return sb.toString()
    }

    /**
     * 颁发大厅作用域凭据 (WELCOME)
     */
    fun issueLobbyScope(
        principal: String,
        validityMillis: Long = defaultScopeValidityMillis,
        nowMillis: Long = clock.currentTimeMillis()
    ): TcpScope = issueScope(principal, validityMillis, nowMillis)

    /**
     * 颁发角色作用域凭据 (JOIN_ROOM / RESUME_SESSION / JOIN_SPECTATOR / RESUME_SPECTATOR)
     */
    fun issueRoleScope(
        principal: String,
        validityMillis: Long = defaultScopeValidityMillis,
        nowMillis: Long = clock.currentTimeMillis()
    ): TcpScope = issueScope(principal, validityMillis, nowMillis)

    /**
     * 内部统一下发作用域
     */
    fun issueScope(
        principal: String,
        validityMillis: Long = defaultScopeValidityMillis,
        nowMillis: Long = clock.currentTimeMillis()
    ): TcpScope {
        require(principal.isNotBlank()) { "principal cannot be blank" }
        require(validityMillis > 0L) { "validityMillis must be > 0" }

        val scopeStr = "scope-" + generateSecureToken()
        val record = ScopeRecord(
            principal = principal,
            scope = scopeStr,
            createdAtMillis = nowMillis,
            expiresAtMillis = nowMillis + validityMillis
        )
        synchronized(stateLock) {
            scopesByString[scopeStr] = record
            activeScopeByPrincipal[principal] = record
        }
        return record.tcpScope
    }

    /**
     * GET_REQUEST_SCOPE 专用入口 (D9 / Design §5)：
     * - 仅凭已完成握手或绑定身份准入；
     * - 同身份并发调用返回同一个结果；
     * - scope 仍有效时返回现有值，过期后按需新建；
     * - 按查询速率限制 (初值 1 次/秒)。
     *
     * @return Pair<TcpScope, Boolean>: first 为作用域，second 为是否为新建
     */
    fun getOrCreateScopeForPrincipal(
        principal: String,
        nowMillis: Long = clock.currentTimeMillis(),
        enforceRateLimit: Boolean = false,
        recordQueryTime: Boolean = true
    ): Pair<TcpScope, Boolean> {
        require(principal.isNotBlank()) { "principal cannot be blank" }

        synchronized(stateLock) {
            val existing = activeScopeByPrincipal[principal]
            if (existing != null && !existing.isExpired(nowMillis)) {
                val lastTime = lastScopeQueryTime[principal]
                if (enforceRateLimit && lastTime != null && scopeQueryRateLimitMillis > 0L && (nowMillis - lastTime) < scopeQueryRateLimitMillis) {
                    throw ServerApiException(ApiErrors.SERVER_BUSY, "Rate limit exceeded for GET_REQUEST_SCOPE")
                }
                if (recordQueryTime) {
                    lastScopeQueryTime[principal] = nowMillis
                }
                return existing.tcpScope to false
            }

            // existing 为空或已过期：必须新建（限速不作用于已过期的重建，UC-R07 / D9）
            val newScope = issueScope(principal, defaultScopeValidityMillis, nowMillis)
            if (recordQueryTime) {
                lastScopeQueryTime[principal] = nowMillis
            }
            return newScope to true
        }
    }

    fun isScopeQueryRateLimited(
        principal: String,
        nowMillis: Long = clock.currentTimeMillis()
    ): Boolean {
        synchronized(stateLock) {
            val existing = activeScopeByPrincipal[principal]
            if (existing != null && !existing.isExpired(nowMillis)) {
                val lastTime = lastScopeQueryTime[principal] ?: return false
                return scopeQueryRateLimitMillis > 0L && (nowMillis - lastTime) < scopeQueryRateLimitMillis
            }
            return false
        }
    }

    /**
     * 校验指定作用域对于指定身份的有效性
     */
    fun validateScope(
        principal: String,
        scope: String,
        nowMillis: Long = clock.currentTimeMillis()
    ): ScopeValidationStatus {
        val record = scopesByString[scope] ?: return ScopeValidationStatus.UNKNOWN
        if (record.principal != principal) {
            return ScopeValidationStatus.MISMATCH
        }
        if (record.isExpired(nowMillis)) {
            return ScopeValidationStatus.EXPIRED
        }
        return ScopeValidationStatus.VALID
    }

    /**
     * 请求受理准入裁决 (核心幂等网关)
     *
     * @param key 业务幂等三元组 (principal, scope, requestId)
     * @param request 业务请求对象
     * @param rawPayload 请求体原始 JSON 串（用于精准载荷比对）
     * @param isSideEffect 是否为副作用接口
     * @param isLobby 是否属于大厅请求
     * @param nowMillis 当前时间戳
     * @return [AdmissionResult]
     */
    fun acquireAdmission(
        key: IdempotencyKey,
        request: ApiRequest,
        rawPayload: String,
        isSideEffect: Boolean,
        isLobby: Boolean = false,
        nowMillis: Long = clock.currentTimeMillis()
    ): AdmissionResult {
        synchronized(stateLock) {
            val scopeRecord = scopesByString[key.scope]
            if (scopeRecord == null || scopeRecord.principal != key.principal) {
                return AdmissionResult.ScopeInvalid
            }

            val isScopeExpired = scopeRecord.isExpired(nowMillis)

            // 1. 检查已完成回执
            val completed = completedSideEffects[key] ?: completedQueries[key]
            if (completed != null) {
                if (isConflict(completed.api, completed.rawPayload, completed.request, request, rawPayload)) {
                    return AdmissionResult.Conflict
                }
                return AdmissionResult.Replay(completed)
            }

            // 2. 检查在途请求
            val inFlight = inFlightRequests[key]
            if (inFlight != null) {
                if (isConflict(inFlight.api, inFlight.rawPayload, inFlight.request, request, rawPayload)) {
                    return AdmissionResult.Conflict
                }
                return AdmissionResult.WaitInFlight(inFlight)
            }

            // 3. 若 Scope 已过期且无任何历史记录，则明确拒绝且绝不重新执行 (UC-R05)
            if (isScopeExpired) {
                return AdmissionResult.ScopeExpired
            }

            // 4. 容量检查（在受理前拒绝，UC-R06 / D2 / R13）
            val memberInFlight = memberInFlightCounters.computeIfAbsent(key.principal) { AtomicInteger(0) }
            val memberReserved = memberReservedCounters.computeIfAbsent(key.principal) { AtomicInteger(0) }
            val memberCompleted = memberCompletedSideEffectCounts.getOrDefault(key.principal, 0)

            // 成员在途上限 <= 64 (普通读取与副作用统一受此约束，R13)
            if (memberInFlight.get() >= maxInFlightPerMember) {
                return AdmissionResult.ServerBusy
            }

            if (isSideEffect) {
                // 成员总副作用记录 + 预留 <= 512
                if (memberCompleted + memberReserved.get() >= maxCompletedPerMember) {
                    return AdmissionResult.ServerBusy
                }
                // 大厅总副作用记录 + 预留 <= 1024
                if (isLobby) {
                    val lobbyCompletedCount = completedSideEffects.size
                    if (lobbyCompletedCount + lobbySideEffectReserved.get() >= maxLobbySideEffectReceipts) {
                        return AdmissionResult.ServerBusy
                    }
                }
            }

            // 5. 准入通过：登记在途并预留容量
            memberInFlight.incrementAndGet()
            if (isSideEffect) {
                memberReserved.incrementAndGet()
                if (isLobby) {
                    lobbySideEffectReserved.incrementAndGet()
                }
            }

            val future = CompletableFuture<ApiResponse>()
            val entry = InFlightReceipt(
                future = future,
                api = request.api,
                rawPayload = rawPayload,
                request = request,
                timestamp = nowMillis,
                isSideEffect = isSideEffect
            )
            inFlightRequests[key] = entry
            return AdmissionResult.Admitted(entry)
        }
    }

    /**
     * 业务执行完成，归档回执并释放/消耗在途与预留容量
     */
    fun completeReceipt(
        key: IdempotencyKey,
        inFlight: InFlightReceipt,
        response: ApiResponse,
        grantedScope: TcpScope? = null,
        isLobby: Boolean = false,
        nowMillis: Long = clock.currentTimeMillis()
    ) {
        val shouldComplete: Boolean
        synchronized(stateLock) {
            // 1. 原子比较删除在途条目 (MR-03 / R12: 仅在成功删除时推进状态)
            val removed = inFlightRequests.remove(key, inFlight)
            if (!removed) {
                return
            }
            shouldComplete = true

            // 2. 扣减成员在途计数
            memberInFlightCounters[key.principal]?.decrementAndGet()

            // 3. 消耗预留容量（仅扣减一次）
            if (inFlight.isSideEffect && inFlight.reservationConsumed.compareAndSet(false, true)) {
                memberReservedCounters[key.principal]?.decrementAndGet()
                if (isLobby) {
                    lobbySideEffectReserved.decrementAndGet()
                }
            }

            // 4. 存入已完成回执
            val receipt = CompletedReceipt(
                response = response,
                api = inFlight.api,
                rawPayload = inFlight.rawPayload,
                request = inFlight.request,
                timestamp = nowMillis,
                grantedScope = grantedScope
            )

            if (inFlight.isSideEffect) {
                completedSideEffects[key] = receipt
                memberCompletedSideEffectCounts[key.principal] =
                    memberCompletedSideEffectCounts.getOrDefault(key.principal, 0) + 1
            } else {
                completedQueries[key] = receipt
                // 查询缓存有界 FIFO 淘汰
                if (completedQueries.size > maxLobbyQueryReceipts) {
                    val it = completedQueries.entries.iterator()
                    if (it.hasNext()) {
                        it.next()
                        it.remove()
                    }
                }
            }
        }

        // 5. 锁外完成在途 Future (R12: 绝不在共享锁内执行外部用户/系统回调)
        if (shouldComplete) {
            inFlight.future.complete(response)
        }
    }

    /**
     * 在途请求异常失败（例如权威超时或内部故障）
     */
    fun failInFlight(
        key: IdempotencyKey,
        inFlight: InFlightReceipt,
        throwable: Throwable,
        isLobby: Boolean = false
    ) {
        val shouldFail: Boolean
        synchronized(stateLock) {
            val removed = inFlightRequests.remove(key, inFlight)
            if (!removed) {
                return
            }
            shouldFail = true

            memberInFlightCounters[key.principal]?.decrementAndGet()

            if (inFlight.isSideEffect && inFlight.reservationConsumed.compareAndSet(false, true)) {
                memberReservedCounters[key.principal]?.decrementAndGet()
                if (isLobby) {
                    lobbySideEffectReserved.decrementAndGet()
                }
            }
        }

        if (shouldFail) {
            inFlight.future.completeExceptionally(throwable)
        }
    }

    /**
     * 查询是否存在指定键的回执或在途请求
     */
    fun findReceipt(key: IdempotencyKey): CompletedReceipt? {
        synchronized(stateLock) {
            return completedSideEffects[key] ?: completedQueries[key]
        }
    }

    /**
     * 查询是否存在指定键的在途条目
     */
    fun findInFlight(key: IdempotencyKey): InFlightReceipt? {
        return inFlightRequests[key]
    }

    /**
     * 清理所有已完全过期的 scope（仅当其所有在途请求均已结束时回收，UC-R06）
     */
    fun cleanExpiredScopes(nowMillis: Long = clock.currentTimeMillis()) {
        synchronized(stateLock) {
            val it = scopesByString.entries.iterator()
            val candidatePrincipals = HashSet<String>()
            while (it.hasNext()) {
                val entry = it.next()
                val record = entry.value
                if (record.isExpired(nowMillis)) {
                    // 检查是否仍有挂起的在途请求
                    val hasInFlight = inFlightRequests.keys.any { it.scope == record.scope }
                    if (!hasInFlight) {
                        it.remove()
                        candidatePrincipals.add(record.principal)
                        // 移除已完成的旧回执，同步递减该成员已完成副作用计数 (R4.1)
                        val seIt = completedSideEffects.entries.iterator()
                        while (seIt.hasNext()) {
                            val seEntry = seIt.next()
                            if (seEntry.key.scope == record.scope) {
                                seIt.remove()
                                val p = seEntry.key.principal
                                val currentCount = memberCompletedSideEffectCounts[p] ?: 0
                                if (currentCount <= 1) {
                                    memberCompletedSideEffectCounts.remove(p)
                                } else {
                                    memberCompletedSideEffectCounts[p] = currentCount - 1
                                }
                            }
                        }
                        completedQueries.keys.removeIf { it.scope == record.scope }
                        // 若活跃 scope 属于它，清理
                        if (activeScopeByPrincipal[record.principal]?.scope == record.scope) {
                            activeScopeByPrincipal.remove(record.principal)
                        }
                    }
                }
            }

            // 当某 principal 已无任何活跃 scope、无在途、无已保留回执时，清理其全部计数与查询时间条目 (R4.2)
            for (p in candidatePrincipals) {
                cleanupPrincipalIfIdle(p, nowMillis)
            }
        }
    }

    private fun cleanupPrincipalIfIdle(principal: String, nowMillis: Long) {
        val hasActiveScope = scopesByString.values.any { it.principal == principal && !it.isExpired(nowMillis) }
        val inFlight = memberInFlightCounters[principal]?.get() ?: 0
        val reserved = memberReservedCounters[principal]?.get() ?: 0
        val completedCount = memberCompletedSideEffectCounts[principal] ?: 0
        val hasQueries = completedQueries.keys.any { it.principal == principal }

        if (!hasActiveScope && inFlight <= 0 && reserved <= 0 && completedCount <= 0 && !hasQueries) {
            memberInFlightCounters.remove(principal)
            memberReservedCounters.remove(principal)
            memberCompletedSideEffectCounts.remove(principal)
            lastScopeQueryTime.remove(principal)
            activeScopeByPrincipal.remove(principal)
        }
    }

    internal fun getCompletedSideEffectCount(principal: String): Int =
        synchronized(stateLock) { memberCompletedSideEffectCounts.getOrDefault(principal, 0) }

    internal fun hasPrincipalState(principal: String): Boolean =
        synchronized(stateLock) {
            activeScopeByPrincipal.containsKey(principal) ||
            memberInFlightCounters.containsKey(principal) ||
            memberReservedCounters.containsKey(principal) ||
            memberCompletedSideEffectCounts.containsKey(principal) ||
            lastScopeQueryTime.containsKey(principal)
        }

    /**
     * 判定两次请求是否构成同键冲突 (ApiErrors.CONFLICT, D1)
     */
    private fun isConflict(
        storedApi: String,
        storedPayload: String,
        storedReq: ApiRequest?,
        incomingReq: ApiRequest,
        incomingPayload: String
    ): Boolean {
        if (storedApi != incomingReq.api) return true
        if (storedPayload.isNotEmpty() && incomingPayload.isNotEmpty() && storedPayload != incomingPayload) {
            return true
        }
        if (storedReq != null && storedReq != incomingReq) {
            return true
        }
        return false
    }

    enum class ScopeValidationStatus {
        VALID,
        EXPIRED,
        UNKNOWN,
        MISMATCH
    }
}

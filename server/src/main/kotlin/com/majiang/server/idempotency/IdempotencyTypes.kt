package com.majiang.server.idempotency

import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.tcp.TcpScope
import java.util.concurrent.CompletableFuture
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/**
 * 业务幂等三元组键 (Design §5: logicalPrincipal, requestScope, requestId)
 *
 * 安全约束：
 * - toString 脱敏输出，严禁在日志中泄露 principal 与 scope 原始敏感令牌。
 */
data class IdempotencyKey(
    val principal: String,
    val scope: String,
    val requestId: String
) {
    init {
        require(principal.isNotBlank()) { "principal cannot be blank" }
        require(scope.isNotBlank()) { "scope cannot be blank" }
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
    }

    override fun toString(): String {
        return "IdempotencyKey(principal=***, scope=***, requestId='$requestId')"
    }
}

/**
 * 服务端颁发的作用域凭据元数据
 */
data class ScopeRecord(
    val principal: String,
    val scope: String,
    val createdAtMillis: Long,
    val expiresAtMillis: Long
) {
    init {
        require(principal.isNotBlank()) { "principal cannot be blank" }
        require(scope.isNotBlank()) { "scope cannot be blank" }
        require(expiresAtMillis >= createdAtMillis) { "expiresAtMillis must be >= createdAtMillis" }
    }

    val tcpScope: TcpScope get() = TcpScope(scope = scope, expiresAtMillis = expiresAtMillis)

    fun isExpired(nowMillis: Long): Boolean = nowMillis >= expiresAtMillis

    override fun toString(): String {
        return "ScopeRecord(principal=***, scope=***, createdAtMillis=$createdAtMillis, expiresAtMillis=$expiresAtMillis)"
    }
}

fun TcpScope.isExpired(nowMillis: Long): Boolean = nowMillis >= expiresAtMillis

/**
 * 已完成的业务回执条目
 */
data class CompletedReceipt(
    val response: ApiResponse,
    val api: String,
    val rawPayload: String,
    val request: ApiRequest?,
    val timestamp: Long,
    val grantedScope: TcpScope? = null
)

/**
 * 在途业务请求登记项
 */
class InFlightReceipt(
    val future: CompletableFuture<ApiResponse>,
    val api: String,
    val rawPayload: String,
    val request: ApiRequest?,
    val timestamp: Long,
    val isSideEffect: Boolean,
    val grantedScope: AtomicReference<TcpScope?> = AtomicReference(null)
) {
    val reservationConsumed: AtomicBoolean = AtomicBoolean(false)
}

/**
 * 请求准入裁决结果
 */
sealed class AdmissionResult {
    /** 准入成功，新在途条目已登记并预留容量 */
    data class Admitted(val inFlight: InFlightReceipt) : AdmissionResult()

    /** 命中历史回执，重放原结果 */
    data class Replay(val completed: CompletedReceipt) : AdmissionResult()

    /** 命中在途请求，关联等待同一 Future */
    data class WaitInFlight(val inFlight: InFlightReceipt) : AdmissionResult()

    /** 同键但载荷或 API 冲突 (ApiErrors.CONFLICT) */
    data object Conflict : AdmissionResult()

    /** requestScope 已过期且无历史记录 (ApiErrors.REQUEST_SCOPE_EXPIRED) */
    data object ScopeExpired : AdmissionResult()

    /** 未知或伪造的 requestScope */
    data object ScopeInvalid : AdmissionResult()

    /** 容量饱和，受理前拒绝 (ApiErrors.SERVER_BUSY) */
    data object ServerBusy : AdmissionResult()
}

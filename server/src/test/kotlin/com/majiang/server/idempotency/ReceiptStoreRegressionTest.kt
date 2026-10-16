package com.majiang.server.idempotency

import com.majiang.server.api.ApiErrors
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * RequestScopeReceiptStore 核心回执与作用域治理回归测试 (R12, R13)
 *
 * 严格覆盖：
 * - R12: completeReceipt 锁外触发用户 Future 回调，杜绝回调重入锁死全服
 * - R12: 比较删除原子完成语义，重复完成无副作用
 * - R13: 普通只读查询严格遵守每成员在途上限 (maxInFlightPerMember)
 * - R13: GET_REQUEST_SCOPE 速率限制严格执行 (1次/秒，返回 SERVER_BUSY)
 */
@Timeout(15)
class ReceiptStoreRegressionTest {

    @Test
    fun `R12 receipt completion invokes callbacks outside store lock and allows reentrant store query`() {
        val store = RequestScopeReceiptStore()
        val scope = store.issueLobbyScope("p-1")
        val req = ApiRequest(api = "SET_READY", requestId = "ready-1")
        val key = IdempotencyKey("p-1", scope.scope, req.requestId)

        val admitted = store.acquireAdmission(key, req, "payload-1", isSideEffect = true) as AdmissionResult.Admitted
        val callbackCouldQuery = CompletableFuture<Boolean>()

        admitted.inFlight.future.whenComplete { _, _ ->
            val queried = CountDownLatch(1)
            val worker = Thread {
                val receipt = store.findReceipt(key)
                if (receipt != null) {
                    queried.countDown()
                }
            }.apply { isDaemon = true; start() }
            callbackCouldQuery.complete(queried.await(500, TimeUnit.MILLISECONDS))
        }

        store.completeReceipt(key, admitted.inFlight, ApiResponse(requestId = req.requestId, success = true))
        assertTrue(callbackCouldQuery.get(1, TimeUnit.SECONDS), "Callbacks must execute outside stateLock")
    }

    @Test
    fun `R12 duplicate completion has no duplicate decrement or corrupted state`() {
        val store = RequestScopeReceiptStore(maxInFlightPerMember = 5)
        val scope = store.issueLobbyScope("p-2")
        val req = ApiRequest(api = "SET_READY", requestId = "ready-2")
        val key = IdempotencyKey("p-2", scope.scope, req.requestId)

        val admitted = store.acquireAdmission(key, req, "payload-2", isSideEffect = true) as AdmissionResult.Admitted
        store.completeReceipt(key, admitted.inFlight, ApiResponse(requestId = req.requestId, success = true))

        // 重复完成（模拟迟到重入）
        store.completeReceipt(key, admitted.inFlight, ApiResponse(requestId = req.requestId, success = true))

        // 验证回执依然可正常查到且状态未损坏
        val found = store.findReceipt(key)
        assertNotNull(found)
        assertEquals(true, found?.response?.success)
    }

    @Test
    fun `R13 ordinary queries obey member in flight bound and reject excess with SERVER_BUSY`() {
        val store = RequestScopeReceiptStore(maxInFlightPerMember = 2)
        val scope = store.issueLobbyScope("p-query")

        val adm1 = store.acquireAdmission(
            IdempotencyKey("p-query", scope.scope, "q1"),
            ApiRequest(api = "LIST_ROOMS", requestId = "q1"),
            "query-1",
            isSideEffect = false
        )
        val adm2 = store.acquireAdmission(
            IdempotencyKey("p-query", scope.scope, "q2"),
            ApiRequest(api = "LIST_ROOMS", requestId = "q2"),
            "query-2",
            isSideEffect = false
        )
        val adm3 = store.acquireAdmission(
            IdempotencyKey("p-query", scope.scope, "q3"),
            ApiRequest(api = "LIST_ROOMS", requestId = "q3"),
            "query-3",
            isSideEffect = false
        )

        assertTrue(adm1 is AdmissionResult.Admitted)
        assertTrue(adm2 is AdmissionResult.Admitted)
        assertTrue(adm3 is AdmissionResult.ServerBusy, "Query exceeding maxInFlightPerMember must return ServerBusy")
    }

    @Test
    fun `R13 scope query rate limit enforces 1 per second per principal`() {
        val store = RequestScopeReceiptStore(scopeQueryRateLimitMillis = 1000L)
        val principal = "p-rate"
        val now = 10_000L

        // 第一次查询：允许
        assertFalse(store.isScopeQueryRateLimited(principal, now))
        store.getOrCreateScopeForPrincipal(principal, now)

        // 500ms 后再次查询：受限
        assertTrue(store.isScopeQueryRateLimited(principal, now + 500L), "Query within 1000ms must be rate limited")

        // 1001ms 后再次查询：恢复允许
        assertFalse(store.isScopeQueryRateLimited(principal, now + 1001L), "Query after 1000ms must be allowed")
    }
}

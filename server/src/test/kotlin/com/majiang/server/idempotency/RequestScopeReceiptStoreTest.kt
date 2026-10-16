package com.majiang.server.idempotency

import com.majiang.server.api.ApiErrors
import com.majiang.server.inprocess.InProcessServer
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.common.TransportClock
import com.majiang.transport.tcp.TcpScope
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNotSame
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * 业务幂等与 RequestScope 存储单元与契约测试 (RequestScopeReceiptStoreTest)
 *
 * 严格覆盖：
 * - UC-R01: 同键同载荷重放
 * - UC-R02: 在途请求去重关联
 * - UC-R03: 同键不同 API/载荷冲突 (ApiErrors.CONFLICT, D1)
 * - UC-R04: 断线恢复与回执重查
 * - UC-R05: Scope 到期处理 (先查回执再报 REQUEST_SCOPE_EXPIRED, D3)
 * - UC-R06: 容量限制受理前拒接 (ApiErrors.SERVER_BUSY, D2)
 * - UC-R07: Scope 准入与 GET_REQUEST_SCOPE 权威查询 (D9)
 * - UC-R08: 身份隔离与令牌防泄露
 * - UC-R09: 有效期内副作用回执不被 FIFO 淘汰
 * - UC-R11: 幂等键与传输解耦
 * - F12: InProcessServer 共享等价入口验证
 */
@Timeout(15)
class RequestScopeReceiptStoreTest {

    private class FakeClock(var current: Long = 1_000_000L) : TransportClock {
        override fun currentTimeMillis(): Long = current
        override fun nanoTime(): Long = current * 1_000_000L
        fun advance(millis: Long) { current += millis }
    }

    @Test
    fun `UC-R01 replay completed receipt with identical payload`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "user-1"
        val scope = store.issueScope(principal).scope
        val key = IdempotencyKey(principal, scope, "req-1")
        val req = ApiRequest(api = "CREATE_ROOM", requestId = "req-1")

        // 第一次请求准入并完成
        val adm1 = store.acquireAdmission(key, req, "{\"api\":\"CREATE_ROOM\"}", isSideEffect = true)
        assertTrue(adm1 is AdmissionResult.Admitted)
        val inFlight = (adm1 as AdmissionResult.Admitted).inFlight

        val originalResp = ApiResponse(requestId = "req-1", success = true, roomId = "R101")
        store.completeReceipt(key, inFlight, originalResp)

        // 第二次提交相同键与载荷
        val adm2 = store.acquireAdmission(key, req, "{\"api\":\"CREATE_ROOM\"}", isSideEffect = true)
        assertTrue(adm2 is AdmissionResult.Replay)
        val replay = (adm2 as AdmissionResult.Replay).completed
        assertEquals("R101", replay.response.roomId)
        assertEquals("CREATE_ROOM", replay.api)
    }

    @Test
    fun `UC-R02 in-flight request coalescing to same future`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "user-1"
        val scope = store.issueScope(principal).scope
        val key = IdempotencyKey(principal, scope, "req-2")
        val req = ApiRequest(api = "START_MATCH", requestId = "req-2")

        val adm1 = store.acquireAdmission(key, req, "{}", isSideEffect = true)
        assertTrue(adm1 is AdmissionResult.Admitted)
        val inFlight1 = (adm1 as AdmissionResult.Admitted).inFlight

        // 并发相同请求
        val adm2 = store.acquireAdmission(key, req, "{}", isSideEffect = true)
        assertTrue(adm2 is AdmissionResult.WaitInFlight)
        val inFlight2 = (adm2 as AdmissionResult.WaitInFlight).inFlight

        val targetResp = ApiResponse(requestId = "req-2", success = true)
        store.completeReceipt(key, inFlight1, targetResp)

        assertEquals(targetResp, inFlight1.future.get(1, TimeUnit.SECONDS))
        assertEquals(targetResp, inFlight2.future.get(1, TimeUnit.SECONDS))
    }

    @Test
    fun `UC-R03 conflict on same key but different payload or api`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "user-1"
        val scope = store.issueScope(principal).scope
        val key = IdempotencyKey(principal, scope, "req-3")

        val reqA = ApiRequest(api = "CREATE_ROOM", requestId = "req-3")
        val adm1 = store.acquireAdmission(key, reqA, "{\"mode\":\"hanchan\"}", isSideEffect = true)
        assertTrue(adm1 is AdmissionResult.Admitted)

        // 同键换载荷
        val reqB = ApiRequest(api = "CREATE_ROOM", requestId = "req-3")
        val admConflictPayload = store.acquireAdmission(key, reqB, "{\"mode\":\"tonpuu\"}", isSideEffect = true)
        assertTrue(admConflictPayload is AdmissionResult.Conflict)

        // 同键换 API
        val reqC = ApiRequest(api = "JOIN_ROOM", requestId = "req-3")
        val admConflictApi = store.acquireAdmission(key, reqC, "{\"mode\":\"hanchan\"}", isSideEffect = true)
        assertTrue(admConflictApi is AdmissionResult.Conflict)
    }

    @Test
    fun `UC-R04 disconnected query recovery after reconnect`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "user-1"
        val scope = store.issueScope(principal).scope
        val key = IdempotencyKey(principal, scope, "req-4")
        val req = ApiRequest(api = "CREATE_ROOM", requestId = "req-4")

        val adm = store.acquireAdmission(key, req, "{}", isSideEffect = true)
        assertTrue(adm is AdmissionResult.Admitted)
        val inFlight = (adm as AdmissionResult.Admitted).inFlight

        // 模拟断线：网络断开并不删除服务端的 inFlight 记录 (Design §5)
        // 权威端在后台继续完成
        val completedResp = ApiResponse(requestId = "req-4", success = true, roomId = "R999")
        store.completeReceipt(key, inFlight, completedResp)

        // 重连后换连接重新查询原 requestId
        val replay = store.findReceipt(key)
        assertNotNull(replay)
        assertEquals("R999", replay!!.response.roomId)
    }

    @Test
    fun `UC-R05 expired scope replaying existing receipt or returning REQUEST_SCOPE_EXPIRED`() {
        val clock = FakeClock(10_000_000L)
        val store = RequestScopeReceiptStore(clock = clock, defaultScopeValidityMillis = 600_000L) // 10 min
        val principal = "user-1"
        val scope = store.issueScope(principal).scope
        val key1 = IdempotencyKey(principal, scope, "req-5-done")
        val req1 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-5-done")

        val adm1 = store.acquireAdmission(key1, req1, "{}", isSideEffect = true)
        val inFlight1 = (adm1 as AdmissionResult.Admitted).inFlight
        store.completeReceipt(key1, inFlight1, ApiResponse(requestId = "req-5-done", success = true))

        // 时间推进 11 分钟，Scope 过期
        clock.advance(11 * 60 * 1000L)

        // 1. 已完成的回执重放：优先返回原结果 (UC-R05)
        val replayAdm = store.acquireAdmission(key1, req1, "{}", isSideEffect = true)
        assertTrue(replayAdm is AdmissionResult.Replay)

        // 2. 无历史记录的新请求在过期 Scope 上发起：明确返回 REQUEST_SCOPE_EXPIRED，绝不重新执行
        val key2 = IdempotencyKey(principal, scope, "req-5-new")
        val req2 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-5-new")
        val expiredAdm = store.acquireAdmission(key2, req2, "{}", isSideEffect = true)
        assertTrue(expiredAdm is AdmissionResult.ScopeExpired)
    }

    @Test
    fun `UC-R06 side-effect capacity bounds return SERVER_BUSY before admission`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(
            clock = clock,
            maxInFlightPerMember = 2,
            maxCompletedPerMember = 3
        )
        val principal = "user-busy"
        val scope = store.issueScope(principal).scope

        // 1. 占满在途容量 2
        val key1 = IdempotencyKey(principal, scope, "req-in-1")
        val key2 = IdempotencyKey(principal, scope, "req-in-2")
        val req1 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-in-1")
        val req2 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-in-2")

        assertTrue(store.acquireAdmission(key1, req1, "{}", isSideEffect = true) is AdmissionResult.Admitted)
        assertTrue(store.acquireAdmission(key2, req2, "{}", isSideEffect = true) is AdmissionResult.Admitted)

        // 第 3 个在途请求在受理前直接返回 SERVER_BUSY (D2)
        val key3 = IdempotencyKey(principal, scope, "req-in-3")
        val req3 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-in-3")
        val busyAdm = store.acquireAdmission(key3, req3, "{}", isSideEffect = true)
        assertTrue(busyAdm is AdmissionResult.ServerBusy)

        // 完成 key1 和 key2
        val in1 = store.findInFlight(key1)!!
        val in2 = store.findInFlight(key2)!!
        store.completeReceipt(key1, in1, ApiResponse(requestId = "req-in-1", success = true))
        store.completeReceipt(key2, in2, ApiResponse(requestId = "req-in-2", success = true))

        // 占满总副作用容量 3
        assertTrue(store.acquireAdmission(key3, req3, "{}", isSideEffect = true) is AdmissionResult.Admitted)
        val in3 = store.findInFlight(key3)!!
        store.completeReceipt(key3, in3, ApiResponse(requestId = "req-in-3", success = true))

        // 第 4 个副作用请求达到 maxCompletedPerMember = 3 上限，直接 SERVER_BUSY
        val key4 = IdempotencyKey(principal, scope, "req-in-4")
        val req4 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-in-4")
        val busyCapAdm = store.acquireAdmission(key4, req4, "{}", isSideEffect = true)
        assertTrue(busyCapAdm is AdmissionResult.ServerBusy)
    }

    @Test
    fun `UC-R07 scope admission unknown scope rejected and GET_REQUEST_SCOPE query concurrent consistency`() {
        val clock = FakeClock(50_000L)
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "user-auth"

        // 1. 未知或伪造的 Scope 遭拒绝
        val fakeKey = IdempotencyKey(principal, "fabricated-scope", "req-fake")
        val fakeReq = ApiRequest(api = "LIST_ROOMS", requestId = "req-fake")
        val fakeAdm = store.acquireAdmission(fakeKey, fakeReq, "{}", isSideEffect = false)
        assertTrue(fakeAdm is AdmissionResult.ScopeInvalid)

        // 2. 并发 GET_REQUEST_SCOPE 返回同一结果 (D9)
        val latch = CountDownLatch(4)
        val threadPool = Executors.newFixedThreadPool(4)
        val results = ConcurrentHashMap.newKeySet<String>()

        for (i in 0 until 4) {
            threadPool.execute {
                val (scope, _) = store.getOrCreateScopeForPrincipal(principal)
                results.add(scope.scope)
                latch.countDown()
            }
        }
        assertTrue(latch.await(3, TimeUnit.SECONDS))
        threadPool.shutdown()

        assertEquals(1, results.size, "Concurrent calls for same principal must return identical scope")
        val issuedScope = results.first()

        // 3. 仍有效时调用返回现有值
        val (cachedScope, isNew1) = store.getOrCreateScopeForPrincipal(principal)
        assertEquals(issuedScope, cachedScope.scope)
        assertFalse(isNew1)

        // 4. 过期后按需新建
        clock.advance(11 * 60 * 1000L)
        val (renewedScope, isNew2) = store.getOrCreateScopeForPrincipal(principal)
        assertNotSame(issuedScope, renewedScope.scope)
        assertTrue(isNew2)
    }

    @Test
    fun `UC-R08 authentication isolation and token masking in logs`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)

        val principalA = "principal-A"
        val principalB = "principal-B"

        val scopeA = store.issueScope(principalA).scope
        val scopeB = store.issueScope(principalB).scope

        val keyA = IdempotencyKey(principalA, scopeA, "req-secret")
        val reqA = ApiRequest(api = "QUERY_ROUND_RESULT", requestId = "req-secret")
        val inFlightA = (store.acquireAdmission(keyA, reqA, "{}", isSideEffect = false) as AdmissionResult.Admitted).inFlight
        store.completeReceipt(keyA, inFlightA, ApiResponse(requestId = "req-secret", success = true, roomId = "SECRET_A"))

        // B 试图以 A 的 requestId 或 Scope 请求历史结果
        val keyB = IdempotencyKey(principalB, scopeA, "req-secret") // B 盗用 A 的 scope
        val admB = store.acquireAdmission(keyB, reqA, "{}", isSideEffect = false)
        assertTrue(admB is AdmissionResult.ScopeInvalid, "Scope mismatched principal must be rejected")

        // 验证 toString 脱敏
        val keyStr = keyA.toString()
        assertFalse(keyStr.contains(principalA), "ToString must not contain plaintext principal")
        assertFalse(keyStr.contains(scopeA), "ToString must not contain plaintext scope")
        assertTrue(keyStr.contains("req-secret"), "RequestId may be visible")
    }

    @Test
    fun `UC-R09 completed side-effects within scope validity are never evicted by query traffic`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(
            clock = clock,
            maxLobbyQueryReceipts = 2 // 查询缓存很小
        )
        val principal = "user-persist"
        val scope = store.issueScope(principal).scope

        // 存入一个副作用回执
        val sideKey = IdempotencyKey(principal, scope, "side-1")
        val sideReq = ApiRequest(api = "CREATE_ROOM", requestId = "side-1")
        val inFlight = (store.acquireAdmission(sideKey, sideReq, "{}", isSideEffect = true) as AdmissionResult.Admitted).inFlight
        store.completeReceipt(sideKey, inFlight, ApiResponse(requestId = "side-1", success = true, roomId = "IMMORTAL"))

        // 灌入大量高频查询
        for (i in 1..20) {
            val qKey = IdempotencyKey(principal, scope, "query-$i")
            val qReq = ApiRequest(api = "LIST_ROOMS", requestId = "query-$i")
            val qIn = (store.acquireAdmission(qKey, qReq, "{}", isSideEffect = false) as AdmissionResult.Admitted).inFlight
            store.completeReceipt(qKey, qIn, ApiResponse(requestId = "query-$i", success = true))
        }

        // 副作用回执依旧完好，未被 FIFO 冲掉 (UC-R09 / MR-03 语义迁移)
        val sideCheck = store.findReceipt(sideKey)
        assertNotNull(sideCheck)
        assertEquals("IMMORTAL", sideCheck!!.response.roomId)
    }

    @Test
    fun `UC-R11 idempotency key decoupled from transport parameters`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "logic-player-1"
        val scope = store.issueScope(principal).scope
        val key = IdempotencyKey(principal, scope, "req-decoupled")
        val req = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-decoupled")

        val inFlight = (store.acquireAdmission(key, req, "{}", isSideEffect = true) as AdmissionResult.Admitted).inFlight
        store.completeReceipt(key, inFlight, ApiResponse(requestId = "req-decoupled", success = true))

        // 无论换 IP、换端口还是换连接 Epoch，只要三元组一致即命中原回执
        val replay = store.acquireAdmission(key, req, "{}", isSideEffect = true)
        assertTrue(replay is AdmissionResult.Replay)
        assertEquals(true, (replay as AdmissionResult.Replay).completed.response.success)
    }

    @Test
    fun `F12 InProcessServer shares equivalent receipt store`() {
        InProcessServer().use { server ->
            assertNotNull(server.receiptStore, "InProcessServer must expose receiptStore")
            val p = "inprocess-user"
            val scope = server.receiptStore.issueScope(p)
            assertTrue(scope.scope.startsWith("scope-"))
            val key = IdempotencyKey(p, scope.scope, "req-inproc")
            val req = ApiRequest(api = "LIST_ROOMS", requestId = "req-inproc")
            val adm = server.receiptStore.acquireAdmission(key, req, "{}", isSideEffect = false)
            assertTrue(adm is AdmissionResult.Admitted)
        }
    }

    @Test
    fun `R4 regression - member completed side-effect count decrements when expired scopes are cleaned`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(
            clock = clock,
            maxCompletedPerMember = 2
        )
        val principal = "user-r4-count"
        val scope = store.issueScope(principal).scope

        // 占满该成员的 2 个副作用容量
        val key1 = IdempotencyKey(principal, scope, "side-1")
        val key2 = IdempotencyKey(principal, scope, "side-2")
        val req1 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "side-1")
        val req2 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "side-2")

        val in1 = (store.acquireAdmission(key1, req1, "{}", isSideEffect = true) as AdmissionResult.Admitted).inFlight
        val in2 = (store.acquireAdmission(key2, req2, "{}", isSideEffect = true) as AdmissionResult.Admitted).inFlight
        store.completeReceipt(key1, in1, ApiResponse(requestId = "side-1", success = true))
        store.completeReceipt(key2, in2, ApiResponse(requestId = "side-2", success = true))

        assertEquals(2, store.getCompletedSideEffectCount(principal))

        // 第 3 个副作用请求达到 maxCompletedPerMember = 2 上限，返回 SERVER_BUSY
        val key3 = IdempotencyKey(principal, scope, "side-3")
        val req3 = ApiRequest(api = "SUBMIT_CHOICE", requestId = "side-3")
        assertTrue(store.acquireAdmission(key3, req3, "{}", isSideEffect = true) is AdmissionResult.ServerBusy)

        // 推进时间至 scope 过期 (10 分钟 TTL + 1 毫秒)
        clock.advance(10 * 60 * 1000L + 1L)

        // 执行过期清理
        store.cleanExpiredScopes()

        // 验证该成员的已完成副作用计数被正确递减归零
        assertEquals(0, store.getCompletedSideEffectCount(principal))

        // 为该 principal 发行新 scope
        val newScope = store.issueScope(principal).scope
        val keyNew = IdempotencyKey(principal, newScope, "side-new")
        val reqNew = ApiRequest(api = "SUBMIT_CHOICE", requestId = "side-new")

        // 验证不再报 SERVER_BUSY，能够正常准入新副作用请求
        val admitted = store.acquireAdmission(keyNew, reqNew, "{}", isSideEffect = true)
        assertTrue(admitted is AdmissionResult.Admitted)
    }

    @Test
    fun `R4 regression - idle principal metadata completely evicted when scope expires and no pending state`() {
        val clock = FakeClock()
        val store = RequestScopeReceiptStore(clock = clock)
        val ephemeralPrincipal = "ephemeral-principal"

        val scope = store.issueScope(ephemeralPrincipal).scope
        val key = IdempotencyKey(ephemeralPrincipal, scope, "req-1")
        val req = ApiRequest(api = "SUBMIT_CHOICE", requestId = "req-1")

        val inFlight = (store.acquireAdmission(key, req, "{}", isSideEffect = true) as AdmissionResult.Admitted).inFlight
        store.completeReceipt(key, inFlight, ApiResponse(requestId = "req-1", success = true))

        // 此时由于持有 activeScope 和 completedSideEffects，存在 principal 状态
        assertTrue(store.hasPrincipalState(ephemeralPrincipal))

        // 推进时间至 scope 过期
        clock.advance(10 * 60 * 1000L + 1L)

        // 执行清理
        store.cleanExpiredScopes()

        // 验证空闲 principal 状态被彻底释放，不产生元数据泄漏
        assertFalse(store.hasPrincipalState(ephemeralPrincipal))
    }

    @Test
    fun `R5 regression - getOrCreateScopeForPrincipal never returns expired scope even within rate limit window`() {
        val clock = FakeClock(10_000L)
        val store = RequestScopeReceiptStore(clock = clock)
        val principal = "user-r5-expiry"

        // 初始获取 scope
        val (scope1, isNew1) = store.getOrCreateScopeForPrincipal(principal)
        assertTrue(isNew1)

        // 推进时间使 scope1 过期
        clock.advance(10 * 60 * 1000L + 1L)
        assertTrue(scope1.isExpired(clock.currentTimeMillis()))

        // 紧接着调用 getOrCreateScopeForPrincipal：即使在限速窗口内，也必须新建而不返回过期 scope
        val (scope2, isNew2) = store.getOrCreateScopeForPrincipal(principal)
        assertTrue(isNew2, "Must issue new scope when existing scope is expired")
        assertNotSame(scope1.scope, scope2.scope)
        assertFalse(scope2.isExpired(clock.currentTimeMillis()))

        // 再次在 100ms 内调用（仍在 1000ms 限速窗口内），由于 scope2 仍有效，返回现有 scope2
        clock.advance(100L)
        val (scope3, isNew3) = store.getOrCreateScopeForPrincipal(principal)
        assertFalse(isNew3)
        assertEquals(scope2.scope, scope3.scope)
    }
}

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
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.common.TransportClock
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpScope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows
import java.nio.channels.SocketChannel
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * TCP 请求路由与业务回执解耦组件行为等价与契约测试 (TcpRequestRouterAndReceiptTest, D-08 / §9.2)
 *
 * 严格覆盖：
 * 1. LogicalIdentityContext 强类型逻辑身份上下文与各角色提取，严禁持有网络传输资源；
 * 2. TcpReceiptManager 独立业务回执管理：准入判定、在途跨连接所有权、跨连接回执重放、跨 API 同 ID 冲突检测；
 * 3. GET_REQUEST_SCOPE 权威控制查询限流 (SERVER_BUSY) 与作用域到期未决对账 (REQUEST_SCOPE_EXPIRED)；
 * 4. 服务关机清理：TcpReceiptManager.close() 原子中断所有挂起的在途请求并以 SERVER_SHUTDOWN 失败；
 * 5. 真实 TCP 跨连接回执重放：Client 1 建房断线，Client 2 携带相同 resumeToken 重连出示相同 requestId，
 *    安全重放原有回执且不重复创建房间；
 * 6. 真实 TCP 跨 API 相同 requestId 冲突拦截 (ApiErrors.CONFLICT)；
 * 7. 真实 TCP 身份与准入边界：
 *    - 观战者非法执行玩家动作拦截 (UNAUTHENTICATED_SESSION)；
 *    - 大厅连接非法执行玩家动作拦截 (UNAUTHENTICATED_SESSION)；
 *    - 非房主席位非法执行房主指令拦截 (UNAUTHENTICATED_SESSION)；
 *    - 伪造或已过期 requestScope 拦截 (REQUEST_SCOPE_EXPIRED)；
 * 8. 真实 TCP 服务关机时在途请求及连接关闭协议通知 (SERVER_SHUTDOWN)。
 */
@Timeout(30)
class TcpRequestRouterAndReceiptTest {

    private class TestClock(private var currentMillis: Long = 1000L) : TransportClock {
        fun advanceMillis(delta: Long) {
            currentMillis += delta
        }

        fun setMillis(millis: Long) {
            currentMillis = millis
        }

        override fun currentTimeMillis(): Long = currentMillis
        override fun nanoTime(): Long = currentMillis * 1_000_000L
    }

    // =========================================================================
    // 1. LogicalIdentityContext 单元测试
    // =========================================================================

    @Test
    fun `LogicalIdentityContext enforces non-blank principal and scope`() {
        val lobbyRole = LobbyRoleState("test-resume-token")
        assertThrows<IllegalArgumentException> {
            LogicalIdentityContext(principal = "", roleState = lobbyRole, requestScope = "scope-1")
        }
        assertThrows<IllegalArgumentException> {
            LogicalIdentityContext(principal = "lobby:test-resume-token", roleState = lobbyRole, requestScope = "")
        }
        assertThrows<IllegalArgumentException> {
            LogicalIdentityContext(principal = "mismatched-principal", roleState = lobbyRole, requestScope = "scope-1")
        }
        assertThrows<IllegalArgumentException> {
            LogicalIdentityContext(principal = "unauthenticated", roleState = UnauthenticatedRoleState, requestScope = "scope-1")
        }
    }

    @Test
    fun `LogicalIdentityContext derives lobby role state accessors correctly`() {
        val lobbyRole = LobbyRoleState("token-abc")
        val ctx = LogicalIdentityContext(
            principal = "lobby:token-abc",
            roleState = lobbyRole,
            requestScope = "scope-lobby-1"
        )

        assertEquals("lobby:token-abc", ctx.principal)
        assertEquals(ConnectionRole.UNBOUND_LOBBY, ctx.role)
        assertTrue(ctx.isLobby)
        assertFalse(ctx.isPlayer)
        assertFalse(ctx.isSpectator)
        assertFalse(ctx.isUnauthenticated)
        assertSame(lobbyRole, ctx.lobbyState)
        assertNull(ctx.playerState)
        assertNull(ctx.spectatorState)
        assertNull(ctx.room)
        assertNull(ctx.seat)
        assertNull(ctx.spectatorId)
        assertNull(ctx.bindingGeneration)
    }

    @Test
    fun `LogicalIdentityContext derives player role state accessors correctly`() {
        val room = SharedRoom(
            roomId = "ROOM_CTX_P",
            hostPlayerName = "HostP",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val playerRole = PlayerState.fromRoom(room, seat = 1, bindingGeneration = 5L)
            val ctx = LogicalIdentityContext(
                principal = "player:${room.roomId}:1",
                roleState = playerRole,
                requestScope = "scope-player-1"
            )

            assertEquals("player:${room.roomId}:1", ctx.principal)
            assertEquals(ConnectionRole.PLAYER, ctx.role)
            assertTrue(ctx.isPlayer)
            assertFalse(ctx.isLobby)
            assertFalse(ctx.isSpectator)
            assertSame(playerRole, ctx.playerState)
            assertSame(room, ctx.room)
            assertEquals(1, ctx.seat)
            assertEquals(5L, ctx.bindingGeneration)
            assertNull(ctx.spectatorState)
            assertNull(ctx.spectatorId)
        } finally {
            room.close()
        }
    }

    @Test
    fun `LogicalIdentityContext derives spectator role state accessors correctly`() {
        val room = SharedRoom(
            roomId = "ROOM_CTX_S",
            hostPlayerName = "HostS",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val spectatorRole = SpectatorState.fromRoom(room, spectatorId = "spec-xyz", bindingGeneration = 3L)
            val ctx = LogicalIdentityContext(
                principal = "spectator:${room.roomId}:spec-xyz",
                roleState = spectatorRole,
                requestScope = "scope-spectator-1"
            )

            assertEquals("spectator:${room.roomId}:spec-xyz", ctx.principal)
            assertEquals(ConnectionRole.SPECTATOR, ctx.role)
            assertTrue(ctx.isSpectator)
            assertFalse(ctx.isPlayer)
            assertFalse(ctx.isLobby)
            assertSame(spectatorRole, ctx.spectatorState)
            assertSame(room, ctx.room)
            assertEquals("spec-xyz", ctx.spectatorId)
            assertEquals(3L, ctx.bindingGeneration)
            assertNull(ctx.playerState)
            assertNull(ctx.seat)
        } finally {
            room.close()
        }
    }

    @Test
    fun `LogicalIdentityContext fromConnection rejects unauthenticated or unhandshaked or closed connection`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 999L, channel = channel, config = serverConfig, globalBudget = budget)
        try {
            // 1. 握手前连接：返回 null
            assertNull(LogicalIdentityContext.fromConnection(conn, "scope-1"))

            // 2. 空白 requestScope：返回 null
            conn.setHandshakedIdentity("lobby:token-test", "token-test", "epoch-1")
            assertNull(LogicalIdentityContext.fromConnection(conn, ""))
            assertNull(LogicalIdentityContext.fromConnection(conn, "   "))

            // 3. 正常已握手：成功构建上下文且字段一致
            val ctx = LogicalIdentityContext.fromConnection(conn, "scope-valid-1")
            assertNotNull(ctx)
            assertEquals("lobby:token-test", ctx!!.principal)
            assertEquals(ConnectionRole.UNBOUND_LOBBY, ctx.role)
            assertTrue(ctx.isLobby)

            // 4. 关闭后：返回 null
            conn.close(TcpCloseReasons.LOCAL_CLOSED, immediate = true)
            assertNull(LogicalIdentityContext.fromConnection(conn, "scope-valid-1"))
        } finally {
            conn.forceCloseChannel()
            channel.close()
        }
    }

    @Test
    fun `controlled atomic interleaving during lobby to player role transition prevents stale principal with new roleState`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 1001L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_INTERLEAVE_LP",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("lobby:tok-lp", "tok-lp", "epoch-lp")
            val playerRole = PlayerState.fromRoom(room, seat = 0)

            // 多轮并发争用：一线程切换角色，另一线程密集采样 fromConnection
            for (i in 0 until 50) {
                // 重置回大厅
                conn.unbindToLobbyRole()

                val started = CountDownLatch(1)
                val stop = AtomicBoolean(false)
                val invalidCombinations = AtomicInteger(0)

                val sampler = Thread {
                    started.countDown()
                    while (!stop.get()) {
                        val ctx = LogicalIdentityContext.fromConnection(conn, "scope-check") ?: continue
                        // 核心断言：绝对不可出现旧 lobby principal + 新 PlayerState 角色！
                        if (ctx.principal.startsWith("lobby:") && ctx.roleState is PlayerState) {
                            invalidCombinations.incrementAndGet()
                        }
                        if (ctx.principal.startsWith("player:") && ctx.roleState is LobbyRoleState) {
                            invalidCombinations.incrementAndGet()
                        }
                        // 严格一致性校验
                        if (ctx.principal != ctx.roleState.logicalPrincipal) {
                            invalidCombinations.incrementAndGet()
                        }
                    }
                }
                sampler.start()
                started.await()

                // 主线程执行原子角色安装
                conn.installPlayerRole(playerRole)
                Thread.sleep(1)
                stop.set(true)
                sampler.join(1000)

                assertEquals(0, invalidCombinations.get(), "Must never observe mismatched principal and roleState during lobby->player transition")
            }
        } finally {
            conn.forceCloseChannel()
            channel.close()
            room.close()
        }
    }

    @Test
    fun `controlled atomic interleaving during player to lobby unbind prevents stale principal with new roleState`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 1002L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_INTERLEAVE_PL",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("lobby:tok-pl", "tok-pl", "epoch-pl")
            val playerRole = PlayerState.fromRoom(room, seat = 0)

            for (i in 0 until 50) {
                // 先置为 PlayerState
                conn.installPlayerRole(playerRole)

                val started = CountDownLatch(1)
                val stop = AtomicBoolean(false)
                val invalidCombinations = AtomicInteger(0)

                val sampler = Thread {
                    started.countDown()
                    while (!stop.get()) {
                        val ctx = LogicalIdentityContext.fromConnection(conn, "scope-check") ?: continue
                        // 核心断言：绝对不可出现旧 player principal + 新 LobbyRoleState 角色！
                        if (ctx.principal.startsWith("player:") && ctx.roleState is LobbyRoleState) {
                            invalidCombinations.incrementAndGet()
                        }
                        if (ctx.principal.startsWith("lobby:") && ctx.roleState is PlayerState) {
                            invalidCombinations.incrementAndGet()
                        }
                        if (ctx.principal != ctx.roleState.logicalPrincipal) {
                            invalidCombinations.incrementAndGet()
                        }
                    }
                }
                sampler.start()
                started.await()

                // 主线程执行原子解绑回退大厅
                conn.unbindToLobbyRole()
                Thread.sleep(1)
                stop.set(true)
                sampler.join(1000)

                assertEquals(0, invalidCombinations.get(), "Must never observe mismatched principal and roleState during player->lobby unbind")
            }
        } finally {
            conn.forceCloseChannel()
            channel.close()
            room.close()
        }
    }

    // =========================================================================
    // 2. TcpReceiptManager 单元与在途生命周期测试
    // =========================================================================

    @Test
    fun `TcpReceiptManager manages in-flight ownership across connections and completes receipt`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-1"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-inflight-01"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        // 1. 第一个连接申请准入 -> Admitted
        val admission1 = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        assertTrue(admission1 is AdmissionResult.Admitted)
        val inFlight = (admission1 as AdmissionResult.Admitted).inFlight
        assertNotNull(manager.findInFlight(key))

        // 2. 第二个连接携带相同身份和 reqId 申请准入 -> WaitInFlight (挂载到同一个在途对象)
        val admission2 = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        assertTrue(admission2 is AdmissionResult.WaitInFlight)
        val inFlight2 = (admission2 as AdmissionResult.WaitInFlight).inFlight
        assertSame(inFlight, inFlight2)

        // 3. 业务完成并回执
        val successResp = ApiResponse(requestId = reqId, success = true, roomId = "ROOM_123")
        manager.completeReceipt(
            key = key,
            inFlight = inFlight,
            response = successResp,
            grantedScope = null,
            isLobby = true,
            nowMillis = 1200L
        )

        // 在途记录已清除，已归档为 CompletedReceipt
        assertNull(manager.findInFlight(key))
        val completed = manager.findReceipt(key)
        assertNotNull(completed)
        assertEquals("ROOM_123", completed!!.response.roomId)

        // 4. 后续重试直接返回 Replay
        val admission3 = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1300L
        )
        assertTrue(admission3 is AdmissionResult.Replay)
        assertEquals("ROOM_123", (admission3 as AdmissionResult.Replay).completed.response.roomId)
    }

    @Test
    fun `TcpReceiptManager detects cross-API duplicate requestId collision as Conflict`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-2"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-conflict-01"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)

        // 先执行 CREATE_ROOM 并完成
        val createReq = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)
        val admission1 = manager.acquireAdmission(
            key = key,
            request = createReq,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        assertTrue(admission1 is AdmissionResult.Admitted)
        val inFlight = (admission1 as AdmissionResult.Admitted).inFlight
        manager.completeReceipt(
            key = key,
            inFlight = inFlight,
            response = ApiResponse(requestId = reqId, success = true, roomId = "ROOM_C1"),
            isLobby = true,
            nowMillis = 1100L
        )

        // 再用相同 reqId 发送 JOIN_ROOM -> 冲突检测触发 Conflict
        val joinReq = ApiRequest(api = ApiTags.JOIN_ROOM, requestId = reqId, roomId = "ROOM_C1")
        val admission2 = manager.acquireAdmission(
            key = key,
            request = joinReq,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1200L
        )
        assertTrue(admission2 is AdmissionResult.Conflict, "Cross-API duplicate requestId must be detected as Conflict")
    }

    @Test
    fun `TcpReceiptManager GET_REQUEST_SCOPE query rate limiting returns SERVER_BUSY`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val identity = LogicalIdentityContext(
            principal = "lobby:rate-limited-user",
            roleState = LobbyRoleState("rate-limited-user"),
            requestScope = "scope-1"
        )

        // 第一次查询：成功返回 grantedScope
        val (resp1, scope1) = manager.handleGetRequestScope(identity, "query-1", nowMillis = 1000L)
        assertTrue(resp1.success)
        assertNotNull(scope1)

        // 立即进行第二次查询 (同一毫秒内)：触发限流返回 SERVER_BUSY
        val (resp2, scope2) = manager.handleGetRequestScope(identity, "query-2", nowMillis = 1000L)
        assertFalse(resp2.success)
        assertEquals(ApiErrors.SERVER_BUSY, resp2.error)
        assertNull(scope2)

        // 超过 1000ms 后再次查询：限流解除
        val (resp3, scope3) = manager.handleGetRequestScope(identity, "query-3", nowMillis = 2001L)
        assertTrue(resp3.success)
        assertNotNull(scope3)
    }

    @Test
    fun `TcpReceiptManager close fails active in-flight entries with SERVER_SHUTDOWN`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:shutdown-user"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-shutdown-in-flight"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        assertTrue(admission is AdmissionResult.Admitted)
        val inFlight = (admission as AdmissionResult.Admitted).inFlight

        // 关机前 future 未完成
        assertFalse(inFlight.future.isDone)

        // 执行 manager.close()
        manager.close()
        assertTrue(manager.closed)

        // inFlight.future 必须被 SERVER_SHUTDOWN 异常完成
        assertTrue(inFlight.future.isDone)
        assertTrue(inFlight.future.isCompletedExceptionally)
        val ex = assertThrows<Exception> { inFlight.future.get(100, TimeUnit.MILLISECONDS) }
        val cause = ex.cause as? ServerApiException
        assertNotNull(cause)
        assertEquals(ApiProtocol.SERVER_SHUTDOWN, cause!!.code)

        // 关机后后续准入直接返回 ServerBusy
        val postAdmission = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1500L
        )
        assertTrue(postAdmission is AdmissionResult.ServerBusy)
    }

    @Test
    fun `store lobby reservation count returns to zero on shutdown with capacity 1 store`() {
        val testClock = TestClock(1000L)
        // 创建可用大厅副作用容量仅为 1 的 store
        val receiptStore = RequestScopeReceiptStore(
            maxLobbySideEffectReceipts = 1,
            clock = testClock
        )
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-cap-1"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId1 = "req-lobby-cap-1"
        val key1 = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId1)
        val req1 = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId1)

        // 1. 申请第一个大厅副作用准入 (isSideEffect=true, isLobby=true) -> 准入成功
        val admission1 = manager.acquireAdmission(
            key = key1,
            request = req1,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        assertTrue(admission1 is AdmissionResult.Admitted)
        val inFlight1 = (admission1 as AdmissionResult.Admitted).inFlight

        // 大厅预留计数增加到 1
        assertEquals(1, receiptStore.getLobbySideEffectReserved())

        // 2. 第二个大厅请求因容量为 1 且已被预留，立即被拒绝为 ServerBusy
        val reqId2 = "req-lobby-cap-2"
        val key2 = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId2)
        val req2 = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId2)
        val admission2 = manager.acquireAdmission(
            key = key2,
            request = req2,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        assertTrue(admission2 is AdmissionResult.ServerBusy, "Second request must be rejected when capacity of 1 is reserved")

        // 3. 执行 manager.close()
        manager.close()
        assertTrue(manager.closed)

        // 4. inFlight1 必须被 SERVER_SHUTDOWN 失败
        assertTrue(inFlight1.future.isCompletedExceptionally)
        val ex = assertThrows<Exception> { inFlight1.future.get(100, TimeUnit.MILLISECONDS) }
        val cause = ex.cause as? ServerApiException
        assertNotNull(cause)
        assertEquals(ApiProtocol.SERVER_SHUTDOWN, cause!!.code)

        // 5. 核心断言：关机后 store 的 lobby 预留计数确实归零 (不泄漏)
        assertEquals(0, receiptStore.getLobbySideEffectReserved(), "Lobby reservation count must be restored to 0 after close")

        // 6. 验证 store 的容量已完全归还：创建新 manager 能够再次使用该 1 容量
        val newManager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)
        val newReqId = "req-lobby-after-restore"
        val newKey = IdempotencyKey(principal = principal, scope = scope.scope, requestId = newReqId)
        val newReq = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = newReqId)
        val newAdmission = newManager.acquireAdmission(
            key = newKey,
            request = newReq,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1100L
        )
        assertTrue(newAdmission is AdmissionResult.Admitted, "Restored capacity of 1 must be available for new admission")
        assertEquals(1, receiptStore.getLobbySideEffectReserved())
        newManager.close()
        assertEquals(0, receiptStore.getLobbySideEffectReserved())
    }

    @Test
    fun `deterministic concurrency test between acquireAdmission and close enforces atomic boundary`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:concurrent-user"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)

        val threadCount = 20
        val startLatch = CountDownLatch(1)
        val doneLatch = CountDownLatch(threadCount)
        val admittedFutures = CopyOnWriteArrayList<CompletableFuture<ApiResponse>>()
        val serverBusyCount = AtomicInteger(0)

        // 启动 20 个并发线程同时尝试 acquireAdmission
        for (i in 0 until threadCount) {
            Thread {
                try {
                    startLatch.await()
                    val reqId = "req-concurrent-$i"
                    val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
                    val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)
                    val result = manager.acquireAdmission(
                        key = key,
                        request = req,
                        rawPayload = "{}",
                        isSideEffect = true,
                        isLobby = true,
                        nowMillis = 1000L
                    )
                    when (result) {
                        is AdmissionResult.Admitted -> admittedFutures.add(result.inFlight.future)
                        is AdmissionResult.ServerBusy -> serverBusyCount.incrementAndGet()
                        else -> {}
                    }
                } finally {
                    doneLatch.countDown()
                }
            }.start()
        }

        // 放行线程并发准入，并在并发过程中触发 manager.close()
        startLatch.countDown()
        manager.close()

        assertTrue(doneLatch.await(5, TimeUnit.SECONDS))
        assertTrue(manager.closed)

        // 核心断言 1：所有成功 Admitted 的 future，无一例外必须已完成且为 SERVER_SHUTDOWN，绝无悬挂未决 future
        for (future in admittedFutures) {
            assertTrue(future.isDone, "Every admitted future must be completed")
            assertTrue(future.isCompletedExceptionally, "Every admitted future must be failed exceptionally on shutdown")
            val ex = assertThrows<Exception> { future.get(50, TimeUnit.MILLISECONDS) }
            val cause = ex.cause as? ServerApiException
            assertNotNull(cause)
            assertEquals(ApiProtocol.SERVER_SHUTDOWN, cause!!.code)
        }

        // 核心断言 2：关闭后所有的预留计数必须回零
        assertEquals(0, receiptStore.getLobbySideEffectReserved(), "All lobby reservations must be 0 after close")

        // 核心断言 3：总数守恒 (admitted + serverBusy == threadCount)
        assertEquals(threadCount, admittedFutures.size + serverBusyCount.get())
    }

    @Test
    fun `controlled interleaving - stale callback cannot remove new in-flight of same key or leak reservations`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-stale-callback"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-stale-01"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        // 1. 请求 1 准入成功
        val admission1 = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1000L)
        assertTrue(admission1 is AdmissionResult.Admitted)
        val inFlight1 = (admission1 as AdmissionResult.Admitted).inFlight
        assertEquals(1, receiptStore.getLobbySideEffectReserved())

        // 2. 请求 1 异常失败（模拟超时或错误），准入容量归还为 0
        manager.failInFlight(key, inFlight1, RuntimeException("Attempt 1 failed"), isLobby = true)
        assertTrue(inFlight1.future.isCompletedExceptionally)
        assertNull(manager.findInFlight(key))
        assertEquals(0, receiptStore.getLobbySideEffectReserved())

        // 3. 客户端以完全相同的 key 重试准入 -> 请求 2 准入成功 (新 InFlightReceipt 实例)
        val admission2 = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1100L)
        assertTrue(admission2 is AdmissionResult.Admitted)
        val inFlight2 = (admission2 as AdmissionResult.Admitted).inFlight
        assertFalse(inFlight1 === inFlight2, "Retry must allocate a fresh InFlightReceipt instance")
        assertSame(inFlight2, manager.findInFlight(key))
        assertEquals(1, receiptStore.getLobbySideEffectReserved())

        // 4. 模拟请求 1 迟到的 completeReceipt 回调执行
        val staleResp = ApiResponse(requestId = reqId, success = true, roomId = "STALE_ROOM")
        manager.completeReceipt(key, inFlight1, staleResp, isLobby = true, nowMillis = 1200L)

        // 关键断言：请求 1 的迟到完成绝不能误删请求 2 的追踪条目，绝不能错误完成请求 2，绝不能扣减请求 2 的预留
        assertSame(inFlight2, manager.findInFlight(key), "inFlight2 must remain tracked despite stale completeReceipt on inFlight1")
        assertFalse(inFlight2.future.isDone, "inFlight2 must NOT be completed by stale callback")
        assertEquals(1, receiptStore.getLobbySideEffectReserved(), "Reservation for inFlight2 must NOT be decremented")
        assertNull(manager.findReceipt(key), "Stale success response must NOT be stored as completed receipt")

        // 5. 模拟请求 1 迟到的 failInFlight 回调执行
        manager.failInFlight(key, inFlight1, RuntimeException("Stale late failure"), isLobby = true)

        // 关键断言：迟到失败同样不能误删请求 2
        assertSame(inFlight2, manager.findInFlight(key), "inFlight2 must remain tracked despite stale failInFlight on inFlight1")
        assertFalse(inFlight2.future.isDone, "inFlight2 must NOT be failed by stale callback")
        assertEquals(1, receiptStore.getLobbySideEffectReserved(), "Reservation for inFlight2 must remain intact")

        // 6. 服务关机：因为 inFlight2 未被误删，关机流程必须精确清理 inFlight2 并以 SERVER_SHUTDOWN 失败
        manager.close()
        assertTrue(manager.closed)

        assertTrue(inFlight2.future.isCompletedExceptionally, "inFlight2 must be cancelled by close()")
        val ex = assertThrows<Exception> { inFlight2.future.get(50, TimeUnit.MILLISECONDS) }
        val cause = ex.cause as? ServerApiException
        assertNotNull(cause)
        assertEquals(ApiProtocol.SERVER_SHUTDOWN, cause!!.code)

        // 关键断言：关机后大厅预留计数严格回零，绝无容量泄漏
        assertEquals(0, receiptStore.getLobbySideEffectReserved(), "All reservations must return to 0 without leaks")
    }

    @Test
    fun `controlled interleaving - close blocks until active completeReceipt releases atomic read lock`() {
        val testClock = TestClock(1000L)
        val insideStoreCompleteLatch = CountDownLatch(1)
        val closeInitiatedLatch = CountDownLatch(1)
        val allowCompleteFinishLatch = CountDownLatch(1)

        val interceptedStore = object : RequestScopeReceiptStore(clock = testClock) {
            override fun completeReceipt(
                key: IdempotencyKey,
                inFlight: InFlightReceipt,
                response: ApiResponse,
                grantedScope: TcpScope?,
                isLobby: Boolean,
                nowMillis: Long,
                completeFuture: Boolean
            ): Boolean {
                insideStoreCompleteLatch.countDown()
                closeInitiatedLatch.await(5, TimeUnit.SECONDS)
                allowCompleteFinishLatch.await(5, TimeUnit.SECONDS)
                return super.completeReceipt(key, inFlight, response, grantedScope, isLobby, nowMillis, completeFuture)
            }
        }

        val manager = TcpReceiptManager(store = interceptedStore, isRoomOpen = { true }, clock = testClock)
        val principal = "lobby:user-latch-complete"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-latch-complete-1"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1000L)
        assertTrue(admission is AdmissionResult.Admitted)
        val inFlight = (admission as AdmissionResult.Admitted).inFlight
        assertEquals(1, interceptedStore.getLobbySideEffectReserved())

        val completeDone = AtomicBoolean(false)
        val closeDone = AtomicBoolean(false)

        val thread1 = Thread {
            manager.completeReceipt(
                key = key,
                inFlight = inFlight,
                response = ApiResponse(requestId = reqId, success = true, roomId = "ROOM_LATCH_1"),
                isLobby = true,
                nowMillis = 1100L
            )
            completeDone.set(true)
        }
        thread1.start()

        assertTrue(insideStoreCompleteLatch.await(5, TimeUnit.SECONDS))
        assertFalse(completeDone.get(), "Thread 1 must still be blocked inside intercepted completeReceipt")

        val thread2 = Thread {
            closeInitiatedLatch.countDown()
            manager.close()
            closeDone.set(true)
        }
        thread2.start()

        // 验证因线程 1 持有 readLock，线程 2 的 close() 被排它 writeLock 阻塞
        Thread.sleep(100)
        assertFalse(closeDone.get(), "manager.close() must be blocked waiting for readLock held by completeReceipt")
        assertFalse(manager.closed, "manager must not be closed yet while completeReceipt is running")

        // 放行线程 1 完成 store.completeReceipt 并释放 readLock
        allowCompleteFinishLatch.countDown()
        thread1.join(2000)
        assertTrue(completeDone.get(), "completeReceipt must complete successfully")

        // 线程 2 获得 writeLock 并完成关机
        thread2.join(2000)
        assertTrue(closeDone.get(), "manager.close() must finish after readLock is released")
        assertTrue(manager.closed)

        // 断言：请求在关机前已正常成功完成，回执已归档，大厅预留计数已消费归零
        assertTrue(inFlight.future.isDone)
        assertFalse(inFlight.future.isCompletedExceptionally)
        val completed = manager.findReceipt(key)
        assertNotNull(completed)
        assertEquals("ROOM_LATCH_1", completed!!.response.roomId)
        assertEquals(0, interceptedStore.getLobbySideEffectReserved())
    }

    @Test
    fun `controlled interleaving - completeReceipt after close is rejected without mutating store`() {
        val testClock = TestClock(1000L)
        val store = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = store, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-post-close-complete"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-post-close-1"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1000L)
        assertTrue(admission is AdmissionResult.Admitted)
        val inFlight = (admission as AdmissionResult.Admitted).inFlight
        assertEquals(1, store.getLobbySideEffectReserved())

        // 关机执行，将所有活跃在途以 SERVER_SHUTDOWN 失败并释放配额
        manager.close()
        assertTrue(manager.closed)
        assertEquals(0, store.getLobbySideEffectReserved())
        assertTrue(inFlight.future.isCompletedExceptionally)

        // 模拟外部或下游迟到的业务成功回调调用 completeReceipt
        val successResp = ApiResponse(requestId = reqId, success = true, roomId = "SHOULD_NOT_BE_SAVED")
        manager.completeReceipt(key, inFlight, successResp, isLobby = true, nowMillis = 1200L)

        // 断言：关机后迟到完成被拒绝，绝不向 store 归档成功回执，预留计数保持为 0
        assertNull(manager.findReceipt(key), "Success receipt must NEVER be archived into store after shutdown")
        assertNull(store.findReceipt(key), "Store must not contain receipt after shutdown")
        assertEquals(0, store.getLobbySideEffectReserved())
    }

    @Test
    fun `controlled interleaving - close blocks until active failInFlight releases atomic read lock`() {
        val testClock = TestClock(1000L)
        val insideStoreFailLatch = CountDownLatch(1)
        val closeInitiatedLatch = CountDownLatch(1)
        val allowFailFinishLatch = CountDownLatch(1)

        val interceptedStore = object : RequestScopeReceiptStore(clock = testClock) {
            override fun failInFlight(
                key: IdempotencyKey,
                inFlight: InFlightReceipt,
                throwable: Throwable,
                isLobby: Boolean,
                completeFuture: Boolean
            ): Boolean {
                insideStoreFailLatch.countDown()
                closeInitiatedLatch.await(5, TimeUnit.SECONDS)
                allowFailFinishLatch.await(5, TimeUnit.SECONDS)
                return super.failInFlight(key, inFlight, throwable, isLobby, completeFuture)
            }
        }

        val manager = TcpReceiptManager(store = interceptedStore, isRoomOpen = { true }, clock = testClock)
        val principal = "lobby:user-latch-fail"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-latch-fail-1"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1000L)
        assertTrue(admission is AdmissionResult.Admitted)
        val inFlight = (admission as AdmissionResult.Admitted).inFlight
        assertEquals(1, interceptedStore.getLobbySideEffectReserved())

        val failDone = AtomicBoolean(false)
        val closeDone = AtomicBoolean(false)
        val customException = RuntimeException("Custom simulated authority timeout")

        val thread1 = Thread {
            manager.failInFlight(key, inFlight, customException, isLobby = true)
            failDone.set(true)
        }
        thread1.start()

        assertTrue(insideStoreFailLatch.await(5, TimeUnit.SECONDS))
        assertFalse(failDone.get())

        val thread2 = Thread {
            closeInitiatedLatch.countDown()
            manager.close()
            closeDone.set(true)
        }
        thread2.start()

        Thread.sleep(100)
        assertFalse(closeDone.get(), "manager.close() must be blocked waiting for readLock held by failInFlight")
        assertFalse(manager.closed)

        allowFailFinishLatch.countDown()
        thread1.join(2000)
        assertTrue(failDone.get())

        thread2.join(2000)
        assertTrue(closeDone.get())
        assertTrue(manager.closed)

        // 断言：inFlight 被业务异常失败，预留归零，无计数下溢
        assertTrue(inFlight.future.isCompletedExceptionally)
        val ex = assertThrows<Exception> { inFlight.future.get(50, TimeUnit.MILLISECONDS) }
        assertEquals(customException, ex.cause)
        assertEquals(0, interceptedStore.getLobbySideEffectReserved())
    }

    @Test
    @Timeout(5)
    fun `whenComplete callback on completeReceipt synchronizing close completes without deadlock`() {
        val testClock = TestClock(1000L)
        val store = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = store, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-whencomplete-complete"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-wc-complete-1"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1000L)
        assertTrue(admission is AdmissionResult.Admitted)
        val inFlight = (admission as AdmissionResult.Admitted).inFlight
        assertEquals(1, store.getLobbySideEffectReserved())

        val callbackInvoked = AtomicBoolean(false)
        val closeFinished = AtomicBoolean(false)

        // 注册同步 whenComplete 回调，在回调内同步调用 manager.close()
        inFlight.future.whenComplete { resp, err ->
            callbackInvoked.set(true)
            // 同步联动关机：若此前读锁未释放，将导致读锁升级死锁！
            manager.close()
            closeFinished.set(true)
        }

        val successResp = ApiResponse(requestId = reqId, success = true, roomId = "ROOM_WC_1")
        // 调用 completeReceipt -> 触发 inFlight.future.complete -> 同步执行 whenComplete -> manager.close()
        manager.completeReceipt(key, inFlight, successResp, isLobby = true, nowMillis = 1100L)

        // 核心断言：必须无死锁快速完成
        assertTrue(callbackInvoked.get(), "whenComplete callback must be invoked")
        assertTrue(closeFinished.get(), "manager.close() inside whenComplete must finish without deadlock")
        assertTrue(manager.closed, "manager must be closed")

        // 验证结果与容量一致性
        assertTrue(inFlight.future.isDone)
        assertFalse(inFlight.future.isCompletedExceptionally)
        assertEquals("ROOM_WC_1", inFlight.future.get().roomId)

        // 回执已由胜者正常归档
        val completed = manager.findReceipt(key)
        assertNotNull(completed)
        assertEquals("ROOM_WC_1", completed!!.response.roomId)

        // 预留容量准确释放回零
        assertEquals(0, store.getLobbySideEffectReserved(), "Lobby reservation must be 0")
    }

    @Test
    @Timeout(5)
    fun `whenComplete callback on failInFlight synchronizing close completes without deadlock`() {
        val testClock = TestClock(1000L)
        val store = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = store, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-whencomplete-fail"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-wc-fail-1"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(key, req, "{}", isSideEffect = true, isLobby = true, nowMillis = 1000L)
        assertTrue(admission is AdmissionResult.Admitted)
        val inFlight = (admission as AdmissionResult.Admitted).inFlight
        assertEquals(1, store.getLobbySideEffectReserved())

        val callbackInvoked = AtomicBoolean(false)
        val closeFinished = AtomicBoolean(false)

        inFlight.future.whenComplete { resp, err ->
            callbackInvoked.set(true)
            manager.close()
            closeFinished.set(true)
        }

        val customError = RuntimeException("Custom simulated failure")
        manager.failInFlight(key, inFlight, customError, isLobby = true)

        // 核心断言：必须无死锁快速完成
        assertTrue(callbackInvoked.get(), "whenComplete callback must be invoked")
        assertTrue(closeFinished.get(), "manager.close() inside whenComplete must finish without deadlock")
        assertTrue(manager.closed, "manager must be closed")

        // 验证结果与容量一致性
        assertTrue(inFlight.future.isDone)
        assertTrue(inFlight.future.isCompletedExceptionally)
        val ex = assertThrows<Exception> { inFlight.future.get() }
        assertEquals(customError, ex.cause)

        // 失败请求不归档成功回执
        assertNull(manager.findReceipt(key))

        // 预留容量准确释放回零，无下溢
        assertEquals(0, store.getLobbySideEffectReserved(), "Lobby reservation must be 0")
    }

    @Test
    fun `TcpReceiptManager replay returns ROOM_NOT_FOUND if target room has been recycled`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        var roomOpen = true
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { roomOpen }, clock = testClock)

        val principal = "lobby:recycle-user"
        val scope = manager.issueLobbyScope(principal, validityMillis = 10_000L, nowMillis = 1000L)
        val reqId = "req-recycle-01"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = reqId)

        val admission = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = true,
            isLobby = true,
            nowMillis = 1000L
        )
        val inFlight = (admission as AdmissionResult.Admitted).inFlight
        manager.completeReceipt(
            key = key,
            inFlight = inFlight,
            response = ApiResponse(requestId = reqId, success = true, roomId = "RECYCLED_ROOM"),
            isLobby = true,
            nowMillis = 1100L
        )

        // 房间存活时重放成功
        val replay1 = manager.acquireAdmission(key = key, request = req, rawPayload = "{}", isSideEffect = true, isLobby = true, nowMillis = 1200L)
        assertTrue(replay1 is AdmissionResult.Replay)
        assertTrue((replay1 as AdmissionResult.Replay).completed.response.success)

        // 房间被回收后重放返回 ROOM_NOT_FOUND
        roomOpen = false
        val replay2 = manager.acquireAdmission(key = key, request = req, rawPayload = "{}", isSideEffect = true, isLobby = true, nowMillis = 1300L)
        assertTrue(replay2 is AdmissionResult.Replay)
        val effectiveResp = (replay2 as AdmissionResult.Replay).completed.response
        assertFalse(effectiveResp.success)
        assertEquals(ApiErrors.ROOM_NOT_FOUND, effectiveResp.error)
    }

    // =========================================================================
    // 3. 真实 TCP 端到端测试 (跨连接回执重放、API冲突、身份边界与关机)
    // =========================================================================

    @Test
    fun `real TCP cross-connection receipt replay on room creation avoids duplicate room`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            val reqId = "req-tcp-cross-replay-1"
            val token: String
            val originalRoomId: String

            // 1. Client 1 握手建房
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome1 = client1.expectWelcome()
                token = welcome1.lobbyResumeToken

                val createEnv = TcpRequestEnvelope(
                    requestScope = welcome1.lobbyScope.scope,
                    requestId = reqId,
                    request = ApiRequest(
                        api = ApiTags.CREATE_ROOM,
                        requestId = reqId,
                        playerName = "HostP1",
                        roomConfig = ProtocolRoomConfig()
                    )
                )
                client1.sendFrame(TcpMessageCodec.toFrame(createEnv))
                val rsp1 = client1.expectResponse()
                assertTrue(rsp1.response.success)
                assertNotNull(rsp1.response.roomId)
                assertEquals(0, rsp1.response.seat)
                originalRoomId = rsp1.response.roomId!!
            }

            assertEquals(1, server.registry.activeRooms().size)

            // 2. Client 2 建立全新物理连接，使用相同 resumeToken 恢复逻辑身份
            TcpTestClient(port = server.boundPort).use { client2 ->
                client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION, lobbyResumeToken = token)))
                val welcome2 = client2.expectWelcome()
                assertEquals(token, welcome2.lobbyResumeToken)

                // 3. 发送完全相同的建房请求 (相同 requestId 与相同 API)
                val replayEnv = TcpRequestEnvelope(
                    requestScope = welcome2.lobbyScope.scope,
                    requestId = reqId,
                    request = ApiRequest(
                        api = ApiTags.CREATE_ROOM,
                        requestId = reqId,
                        playerName = "HostP1",
                        roomConfig = ProtocolRoomConfig()
                    )
                )
                client2.sendFrame(TcpMessageCodec.toFrame(replayEnv))

                // 4. 收到重放的响应：roomId 与席位完全相同，且并未创建第二间房间
                val rsp2 = client2.expectResponse()
                assertTrue(rsp2.response.success)
                assertEquals(originalRoomId, rsp2.response.roomId)
                assertEquals(0, rsp2.response.seat)
            }

            // 服务端依然只有 1 间房，证明幂等回执起效且无重复创建
            assertEquals(1, server.registry.activeRooms().size)
        } finally {
            server.close()
        }
    }

    @Test
    fun `real TCP cross-API duplicate requestId collision triggers CONFLICT error`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = client.expectWelcome()

                // 1. 发送建房请求并获得 roleScope
                val createEnv = TcpRequestEnvelope(
                    requestScope = welcome.lobbyScope.scope,
                    requestId = "create-req",
                    request = ApiRequest(
                        api = ApiTags.CREATE_ROOM,
                        requestId = "create-req",
                        playerName = "P1",
                        roomConfig = ProtocolRoomConfig()
                    )
                )
                client.sendFrame(TcpMessageCodec.toFrame(createEnv))
                val createRsp = client.expectResponse()
                assertTrue(createRsp.response.success)
                val roleScope = createRsp.grantedScope!!.scope

                // 2. 在该席位角色作用域下发送准备就绪请求 SET_READY
                val sharedReqId = "shared-action-req-001"
                val readyEnv = TcpRequestEnvelope(
                    requestScope = roleScope,
                    requestId = sharedReqId,
                    request = ApiRequest(api = ApiTags.SET_READY, requestId = sharedReqId, ready = true)
                )
                client.sendFrame(TcpMessageCodec.toFrame(readyEnv))
                val readyRsp = client.expectResponse()
                assertTrue(readyRsp.response.success)

                // 3. 在同一席位角色作用域下，使用完全相同的 sharedReqId 发送不同 API 请求 (如 START_MATCH)
                val conflictEnv = TcpRequestEnvelope(
                    requestScope = roleScope,
                    requestId = sharedReqId,
                    request = ApiRequest(api = ApiTags.START_MATCH, requestId = sharedReqId)
                )
                client.sendFrame(TcpMessageCodec.toFrame(conflictEnv))

                // 4. 服务端必须识别跨 API 同 requestId 冲突并返回 CONFLICT
                val conflictRsp = client.expectResponse()
                assertFalse(conflictRsp.response.success)
                assertEquals(ApiErrors.CONFLICT, conflictRsp.response.error)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `real TCP identity boundary - spectator cannot invoke player action`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            // 1. Host 客户端建房
            var targetRoomId: String
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = hostClient.expectWelcome()

                val createEnv = TcpRequestEnvelope(
                    requestScope = welcome.lobbyScope.scope,
                    requestId = "host-create-req",
                    request = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "host-create-req", playerName = "Host")
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(createEnv))
                val rsp = hostClient.expectResponse()
                assertTrue(rsp.response.success)
                targetRoomId = rsp.response.roomId!!

                // 2. Spectator 客户端以观战身份加入
                TcpTestClient(port = server.boundPort).use { spectatorClient ->
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val specWelcome = spectatorClient.expectWelcome()

                    val joinSpecEnv = TcpRequestEnvelope(
                        requestScope = specWelcome.lobbyScope.scope,
                        requestId = "spec-join-req",
                        request = ApiRequest(
                            api = ApiTags.JOIN_SPECTATOR,
                            requestId = "spec-join-req",
                            roomId = targetRoomId,
                            playerName = "Observer"
                        )
                    )
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(joinSpecEnv))
                    val joinSpecRsp = spectatorClient.expectResponse()
                    assertTrue(joinSpecRsp.response.success)
                    val spectatorScope = joinSpecRsp.grantedScope!!.scope

                    // 3. 观战者尝试调用玩家专属命令 SET_READY
                    val playerActionEnv = TcpRequestEnvelope(
                        requestScope = spectatorScope,
                        requestId = "spec-illegal-player-action",
                        request = ApiRequest(api = ApiTags.SET_READY, requestId = "spec-illegal-player-action", ready = true)
                    )
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(playerActionEnv))

                    // 4. 必须被 withPlayer 拦截并返回 UNAUTHENTICATED_SESSION
                    val failRsp = spectatorClient.expectResponse()
                    assertFalse(failRsp.response.success)
                    assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, failRsp.response.error)
                }
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `real TCP identity boundary - lobby connection cannot invoke player action`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = client.expectWelcome()
                val lobbyScope = welcome.lobbyScope.scope

                // 未加入任何房间的大厅连接尝试调用玩家命令 SET_READY
                val illegalEnv = TcpRequestEnvelope(
                    requestScope = lobbyScope,
                    requestId = "lobby-illegal-action",
                    request = ApiRequest(api = ApiTags.SET_READY, requestId = "lobby-illegal-action", ready = true)
                )
                client.sendFrame(TcpMessageCodec.toFrame(illegalEnv))

                val rsp = client.expectResponse()
                assertFalse(rsp.response.success)
                assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, rsp.response.error)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `real TCP identity boundary - non-host player cannot invoke host commands`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var targetRoomId: String

            // 1. Host (seat 0) 建房
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = hostClient.expectWelcome()

                val createEnv = TcpRequestEnvelope(
                    requestScope = welcome.lobbyScope.scope,
                    requestId = "host-c-1",
                    request = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "host-c-1", playerName = "Host")
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(createEnv))
                val rsp = hostClient.expectResponse()
                assertTrue(rsp.response.success)
                targetRoomId = rsp.response.roomId!!

                // 2. 玩家 2 加入房间 (seat 1)
                TcpTestClient(port = server.boundPort).use { p2Client ->
                    p2Client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val p2Welcome = p2Client.expectWelcome()

                    val joinEnv = TcpRequestEnvelope(
                        requestScope = p2Welcome.lobbyScope.scope,
                        requestId = "p2-join-1",
                        request = ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "p2-join-1", roomId = targetRoomId, playerName = "P2")
                    )
                    p2Client.sendFrame(TcpMessageCodec.toFrame(joinEnv))
                    val joinRsp = p2Client.expectResponse()
                    assertTrue(joinRsp.response.success)
                    assertEquals(1, joinRsp.response.seat)
                    val p2Scope = joinRsp.grantedScope!!.scope

                    // 3. 玩家 2 尝试执行房主专属命令 START_MATCH
                    val startMatchEnv = TcpRequestEnvelope(
                        requestScope = p2Scope,
                        requestId = "p2-start-illegal",
                        request = ApiRequest(api = ApiTags.START_MATCH, requestId = "p2-start-illegal")
                    )
                    p2Client.sendFrame(TcpMessageCodec.toFrame(startMatchEnv))

                    // 4. 必须被核验 seat == 0 拦截并返回 UNAUTHENTICATED_SESSION
                    val failRsp = p2Client.expectResponse()
                    assertFalse(failRsp.response.success)
                    assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, failRsp.response.error)
                }
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `real TCP identity boundary - forged or unknown scope returns UNAUTHENTICATED_SESSION`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                client.expectWelcome()

                // 使用未向服务端注册过的伪造 scope 发送业务请求
                val unknownEnv = TcpRequestEnvelope(
                    requestScope = "scope-fake-unknown-token-999",
                    requestId = "req-unknown-01",
                    request = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "req-unknown-01")
                )
                client.sendFrame(TcpMessageCodec.toFrame(unknownEnv))

                val rsp = client.expectResponse()
                assertFalse(rsp.response.success)
                assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, rsp.response.error)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `TcpReceiptManager returns ScopeExpired when scope has expired and no receipt exists`() {
        val testClock = TestClock(1000L)
        val receiptStore = RequestScopeReceiptStore(clock = testClock)
        val manager = TcpReceiptManager(store = receiptStore, isRoomOpen = { true }, clock = testClock)

        val principal = "lobby:user-expired"
        val scope = manager.issueLobbyScope(principal, validityMillis = 1000L, nowMillis = 1000L)
        val reqId = "req-expired-check"
        val key = IdempotencyKey(principal = principal, scope = scope.scope, requestId = reqId)
        val req = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = reqId)

        // 时间推进到 2500ms (超过 scope 1000ms 有效期，但尚未执行 cleanExpiredScopes)
        val admission = manager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = "{}",
            isSideEffect = false,
            isLobby = true,
            nowMillis = 2500L
        )
        assertTrue(admission is AdmissionResult.ScopeExpired, "Expired scope without receipt must return ScopeExpired")
    }

    @Test
    fun `real TCP server shutdown closes connections and sends SERVER_SHUTDOWN close frame`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        TcpTestClient(port = server.boundPort).use { client ->
            client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
            val welcome = client.expectWelcome()
            assertNotNull(welcome)

            // 服务端关机
            server.close()

            // 客户端收到 SERVER_SHUTDOWN CLOSE 帧
            val closeMsg = client.expectClose(timeoutMillis = 4000L)
            assertEquals(TcpCloseReasons.SERVER_SHUTDOWN, closeMsg.reason)
        }
    }

    @Test
    fun `real TCP identity boundary - after transitioning to player, old lobby scope cannot authorize player actions`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 1. 握手获取大厅 scope
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = client.expectWelcome()
                val oldLobbyScope = welcome.lobbyScope.scope

                // 2. 建房并占座 0，角色转变为 PlayerState 并颁发新 playerScope
                val createReq = ApiRequest(
                    api = ApiTags.CREATE_ROOM,
                    requestId = "req-create-scope-test",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                client.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(oldLobbyScope, "req-create-scope-test", createReq)))
                val createRsp = client.expectResponse()
                assertTrue(createRsp.response.success)
                val playerScope = createRsp.grantedScope!!.scope
                assertTrue(playerScope.isNotBlank())
                assertNotEquals(oldLobbyScope, playerScope)

                // 3. 角色转变后，客户端尝试使用【旧大厅 scope】调用玩家动作 SET_READY
                val illegalEnv = TcpRequestEnvelope(
                    requestScope = oldLobbyScope,
                    requestId = "req-illegal-old-lobby-scope",
                    request = ApiRequest(api = ApiTags.SET_READY, requestId = "req-illegal-old-lobby-scope", ready = true)
                )
                client.sendFrame(TcpMessageCodec.toFrame(illegalEnv))

                // 4. 核心断言：旧大厅 scope 无法授权新角色动作，返回 UNAUTHENTICATED_SESSION
                val illegalRsp = client.expectResponse()
                assertFalse(illegalRsp.response.success)
                assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, illegalRsp.response.error)

                // 5. 使用新颁发的 playerScope 调用则成功执行
                val validEnv = TcpRequestEnvelope(
                    requestScope = playerScope,
                    requestId = "req-valid-player-scope",
                    request = ApiRequest(api = ApiTags.SET_READY, requestId = "req-valid-player-scope", ready = true)
                )
                client.sendFrame(TcpMessageCodec.toFrame(validEnv))
                val validRsp = client.expectResponse()
                assertTrue(validRsp.response.success)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `real TCP identity boundary - after leaving room and unbinding to lobby, old player scope cannot authorize player actions`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var targetRoomId: String

            // 1. Host 客户端建房
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val hostWelcome = hostClient.expectWelcome()

                val createReq = ApiRequest(
                    api = ApiTags.CREATE_ROOM,
                    requestId = "req-host-create-leave",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(hostWelcome.lobbyScope.scope, "req-host-create-leave", createReq)))
                val createRsp = hostClient.expectResponse()
                assertTrue(createRsp.response.success)
                targetRoomId = createRsp.response.roomId!!

                // 2. Member 客户端加入房间 (获得 seat 1 及 playerScope)
                TcpTestClient(port = server.boundPort).use { memberClient ->
                    memberClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val memberWelcome = memberClient.expectWelcome()

                    val joinReq = ApiRequest(
                        api = ApiTags.JOIN_ROOM,
                        requestId = "req-member-join-leave",
                        roomId = targetRoomId,
                        playerName = "MemberPlayer"
                    )
                    memberClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(memberWelcome.lobbyScope.scope, "req-member-join-leave", joinReq)))
                    val joinRsp = memberClient.expectResponse()
                    assertTrue(joinRsp.response.success)
                    val memberPlayerScope = joinRsp.grantedScope!!.scope

                    // 3. Member 客户端主动 LEAVE_ROOM，触发服务端 unbindToLobbyRole 回退大厅
                    val leaveReq = ApiRequest(
                        api = ApiTags.LEAVE_ROOM,
                        requestId = "req-member-leave-action"
                    )
                    memberClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(memberPlayerScope, "req-member-leave-action", leaveReq)))
                    val leaveRsp = memberClient.expectResponse()
                    assertTrue(leaveRsp.response.success)

                    // 4. 回退大厅后，Member 客户端仍使用【旧玩家 scope】尝试调用 SET_READY
                    val illegalEnv = TcpRequestEnvelope(
                        requestScope = memberPlayerScope,
                        requestId = "req-member-illegal-after-leave",
                        request = ApiRequest(api = ApiTags.SET_READY, requestId = "req-member-illegal-after-leave", ready = true)
                    )
                    memberClient.sendFrame(TcpMessageCodec.toFrame(illegalEnv))

                    // 5. 核心断言：旧玩家 scope 无法授权动作，且当前身份已无玩家权限，返回 UNAUTHENTICATED_SESSION
                    val failRsp = memberClient.expectResponse()
                    assertFalse(failRsp.response.success)
                    assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, failRsp.response.error)
                }
            }
        } finally {
            server.close()
        }
    }
}

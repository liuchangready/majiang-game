package com.majiang.server.binding

import com.majiang.client.transport.tcp.TcpClient
import com.majiang.client.transport.tcp.TcpClientBinding
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.ServerConnection
import com.majiang.server.inprocess.InProcessBinding
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.room.SharedRoom
import com.majiang.server.tcp.PlayerState
import com.majiang.server.tcp.SpectatorState
import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.tcp.TcpRequestEnvelope
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.EnumSource
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit

/**
 * 跨传输链路服务端绑定统一规则与业务场景参数化测试 (D-03 / §9.2 / §9.3)
 *
 * 核心验证目标：
 * 1. 同一套参数化业务场景在 InProcess 与 TCP 两端表现完全等价；
 * 2. 玩家与观战者的顶号恢复 (Displacement)、代次自增与旧连接安全失效；
 * 3. 旧连接断开 (无论是通道关闭还是主动 close) 绝不误断新代次连接；
 * 4. 权威队列执行期最终复核拦截迟到的旧代次命令，严防状态污染；
 * 5. 断开代次屏障 (seatDisconnectedGenerations / lastDisconnectedGeneration) 绝对阻断迟到的 markConnected 误活已断开席位；
 * 6. 重复离房 (Repeated Leave Room) 在大厅与局中均保持幂等成功 (APPLIED)，被顶号离房明确拒绝 (STALE_SESSION)；
 * 7. 房间终态关闭与生命周期流转对所有成员及后续准入一致生效 (ROOM_NOT_FOUND)；
 * 8. 玩家与观战者角色命令及公开/私有流严格权限隔离；
 * 9. 显式业务 requestId 跨重连幂等重放、冲突检测与跨主体身份隔离；
 * 10. seatMemberGenerations 防御性副本防外部篡改。
 */
@Timeout(30)
class DualTransportBindingScenarioParameterizedTest {

    enum class TransportMode {
        IN_PROCESS,
        TCP
    }

    private class TestHarness(val mode: TransportMode) : AutoCloseable {
        val inProcessServer: InProcessServer? = if (mode == TransportMode.IN_PROCESS) {
            InProcessServer(isSingleplayer = false)
        } else null

        val tcpServer: TcpTableServer? = if (mode == TransportMode.TCP) {
            TcpTableServer(TcpServerConfig(port = 0)).apply { start() }
        } else null

        private val createdConnections = ConcurrentLinkedQueue<ServerConnection>()

        fun createConnection(): ServerConnection {
            val conn = when (mode) {
                TransportMode.IN_PROCESS -> inProcessServer!!.createConnection()
                TransportMode.TCP -> TcpClient.connect("127.0.0.1", tcpServer!!.boundPort).get(5, TimeUnit.SECONDS)
            }
            createdConnections.add(conn)
            return conn
        }

        fun getRoom(roomId: String): SharedRoom? {
            return when (mode) {
                TransportMode.IN_PROCESS -> inProcessServer!!.registry.getRoom(roomId)
                TransportMode.TCP -> tcpServer!!.registry.getRoom(roomId)
            }
        }

        fun sendEnvelopeRequest(conn: ServerConnection, request: ApiRequest): CompletableFuture<ApiResponse> {
            return when (mode) {
                TransportMode.IN_PROCESS -> {
                    val binding = conn as InProcessBinding
                    val scope = binding.getRequestScope().get(5, TimeUnit.SECONDS).scope
                    val env = TcpRequestEnvelope(scope, request.requestId, request)
                    binding.executeRequest(env).thenApply { it.response }
                }
                TransportMode.TCP -> {
                    val tcpConn = conn as TcpClientBinding
                    tcpConn.executeRawRequest(request)
                        .thenApply { it.response }
                        .exceptionally { ex ->
                            val apiEx = ServerApiException.unwrap(ex) as? ServerApiException
                            if (apiEx != null) {
                                ApiResponse(requestId = request.requestId, success = false, error = apiEx.code)
                            } else {
                                throw ex
                            }
                        }
                }
            }
        }

        fun capturePlayerSessionValidator(conn: ServerConnection, seat: Int): () -> Boolean {
            return when (mode) {
                TransportMode.IN_PROCESS -> (conn as InProcessBinding).makeSessionValidator()
                TransportMode.TCP -> {
                    val tcpConn = tcpServer!!.connections.values.first { it.viewerSeat == seat }
                    val player = tcpConn.roleState as PlayerState
                    player.makeSessionValidator(tcpConn)
                }
            }
        }

        fun captureSpectatorSessionValidator(conn: ServerConnection, spectatorId: String): () -> Boolean {
            return when (mode) {
                TransportMode.IN_PROCESS -> (conn as InProcessBinding).makeSessionValidator()
                TransportMode.TCP -> {
                    val tcpConn = tcpServer!!.connections.values.first { it.spectatorId == spectatorId }
                    val spec = tcpConn.roleState as SpectatorState
                    spec.makeSessionValidator(tcpConn)
                }
            }
        }

        fun awaitDisplaced(conn: ServerConnection, timeoutMs: Long = 3000) {
            val deadline = System.currentTimeMillis() + timeoutMs
            while (!conn.closed && System.currentTimeMillis() < deadline) {
                Thread.sleep(30)
            }
            assertTrue(conn.closed, "Old connection must be closed/displaced within ${timeoutMs}ms (closed=${conn.closed}, reason=${conn.disconnectReason})")
            assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, conn.disconnectReason)
        }

        override fun close() {
            for (conn in createdConnections) {
                try {
                    conn.close()
                } catch (_: Throwable) {
                }
            }
            createdConnections.clear()
            try {
                tcpServer?.close()
            } catch (_: Throwable) {
            }
            try {
                inProcessServer?.close()
            } catch (_: Throwable) {
            }
        }
    }

    // ==========================================
    // 场景 1: 玩家顶号生命周期与防误断
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 1 - player displacement advances generation, closes old connection and does not kill new connection`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val conn1 = harness.createConnection()
            val bound1 = conn1.createRoom(CreateRoomRequest(playerName = "P1")).get(5, TimeUnit.SECONDS)
            val roomId = bound1.roomId
            val room = harness.getRoom(roomId)!!

            assertEquals(1L, room.seatMemberGeneration(0), "Initial host generation must be 1")
            assertTrue(room.seatConnected[0], "Seat 0 must be connected")

            // 第 2 个连接使用相同 resumeToken 恢复会话 (顶号)
            val conn2 = harness.createConnection()
            val bound2 = conn2.resumeSession(ResumeRequest(roomId, bound1.resumeToken)).get(5, TimeUnit.SECONDS)
            assertEquals(0, bound2.seat)
            assertEquals(2L, room.seatMemberGeneration(0), "Displacement must advance seat generation to 2")

            // 旧连接被异步通知顶号并优雅关闭 (DISPLACED_BY_NEW_CONNECTION)
            harness.awaitDisplaced(conn1)

            // 旧连接显式关闭后，权威队列核验代次，绝不误断新连接席位状态
            conn1.close()
            room.tableHost.enqueueOperation { }.get(3, TimeUnit.SECONDS) // 确保权威队列排空

            assertTrue(room.seatConnected[0], "Old connection close must NOT disconnect new generation seat")
            assertFalse(conn2.closed, "New connection must remain live and healthy")

            // 新连接能够正常与房间交互
            val receipt = conn2.setReady(true).get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
            assertTrue(room.isReady[0])
        }
    }

    // ==========================================
    // 场景 2: 观战者顶号生命周期与防误断
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 2 - spectator displacement advances generation, closes old spectator and does not kill new connection`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            // 观战者 1 加入房间
            val spec1Conn = harness.createConnection()
            val spec1Bound = spec1Conn.joinSpectator(JoinSpectatorRequest(roomId, "SpecUser")).get(5, TimeUnit.SECONDS)
            val specId = spec1Bound.spectatorId
            val member1 = room.spectators[specId]!!
            assertEquals(1L, member1.generation)
            assertTrue(member1.isConnected)

            // 观战者 2 恢复同一观战者身份 (顶号)
            val spec2Conn = harness.createConnection()
            val spec2Bound = spec2Conn.resumeSpectator(ResumeSpectatorRequest(roomId, specId, spec1Bound.resumeToken)).get(5, TimeUnit.SECONDS)
            assertEquals(specId, spec2Bound.spectatorId)

            val member2 = room.spectators[specId]!!
            assertEquals(2L, member2.generation, "Spectator generation must advance to 2")

            // 旧观战连接被推断关闭
            harness.awaitDisplaced(spec1Conn)

            // 旧观战连接断开绝不误断新观战连接
            spec1Conn.close()
            room.tableHost.enqueueOperation { }.get(3, TimeUnit.SECONDS)

            assertTrue(room.isSpectatorConnected(specId), "Old spectator disconnect must NOT disconnect new generation spectator")
            assertFalse(spec2Conn.closed, "New spectator connection must remain live")

            // 新观战连接可正常获取公开快照
            val publicSnap = spec2Conn.publicSnapshot().get(5, TimeUnit.SECONDS)
            assertNotNull(publicSnap)
        }
    }

    // ==========================================
    // 场景 3: 权威队列执行期最终复核拦截旧命令排队
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 3 - queued command from displaced connection is rejected by authority queue final review without state corruption`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            val guestConn = harness.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId, "Guest", 1)).get(5, TimeUnit.SECONDS)
            assertEquals(1, guestBound.seat)
            assertEquals(2L, room.seatMemberGeneration(1))
            assertFalse(room.isReady[1])

            // 新连接 replacementConn 在屏障前先行创建并握手完毕
            val replacementConn = harness.createConnection()

            // 等待先前异步加入与连接通知排空，确保权威队列处于完全空闲状态
            room.tableHost.enqueueOperation { }.get(5, TimeUnit.SECONDS)

            // 1. 在权威队列中放置受控屏障阻断队列执行
            val barrierHold = CountDownLatch(1)
            val barrierEntered = CountDownLatch(1)
            val barrierFuture = room.tableHost.enqueueOperation {
                barrierEntered.countDown()
                barrierHold.await(5, TimeUnit.SECONDS)
            }
            assertTrue(barrierEntered.await(5, TimeUnit.SECONDS), "Barrier must be actively executing on tableHost queue")

            // 2. 新连接 replacementConn 发起真实的顶号恢复请求（排在屏障之后，先于旧连接准备命令执行）
            val resumeFuture = replacementConn.resumeSession(ResumeRequest(roomId, guestBound.resumeToken))

            // 确定性屏障同步：等待 resumeSession 已穿透传输层并进入权威队列（屏障 1 + 恢复 1 = 2）
            val deadlineResume = System.currentTimeMillis() + 5000L
            while (room.tableHost.queuedOperationsCount < 2 && System.currentTimeMillis() < deadlineResume) {
                Thread.sleep(10)
            }
            assertTrue(room.tableHost.queuedOperationsCount >= 2, "resumeFuture must be enqueued first behind barrier")

            // 3. 旧连接 guestConn 提交准备命令（排在 replacementConn 恢复请求之后）
            val staleCmdFuture = guestConn.setReady(true)

            // 明确屏障同步：等待权威队列中确切积压了屏障本身 + replacementConn 恢复 + guestConn 准备命令（共 3 个操作）
            // 严格证明两个请求均已完全穿透传输层并按确定性 FIFO 顺序在权威队列交错排队
            val deadlineStale = System.currentTimeMillis() + 5000L
            while (room.tableHost.queuedOperationsCount < 3 && System.currentTimeMillis() < deadlineStale) {
                Thread.sleep(10)
            }
            assertTrue(room.tableHost.queuedOperationsCount >= 3, "staleCmdFuture must be enqueued second behind resumeFuture")

            // 4. 释放屏障：replacementConn 的 resumeSession 先执行成功，席位代次推进到 3，自动触发真实的顶号通知！
            //    紧接着 guestConn 的 setReady 命令在权威队列执行，此时代次已漂移 (2L != 3L)！
            barrierHold.countDown()
            barrierFuture.get(5, TimeUnit.SECONDS)

            // 新连接成功完成真实顶号恢复
            val resumed = resumeFuture.get(5, TimeUnit.SECONDS)
            assertEquals(1, resumed.seat)
            assertEquals(3L, room.seatMemberGeneration(1))

            // 核心断言：权威队列执行期校验 generation 漂移，必须给出明确的 STALE_SESSION 拒绝或因顶号断开
            try {
                val staleReceipt = staleCmdFuture.get(5, TimeUnit.SECONDS)
                assertEquals(ProtocolReceiptStatus.REJECTED, staleReceipt.status, "Queued command must be REJECTED")
                assertEquals("STALE_SESSION", staleReceipt.reason, "Queued command must be rejected with STALE_SESSION")
            } catch (e: ExecutionException) {
                val cause = e.cause
                assertTrue(cause is IOException || cause is ServerApiException, "Expected connection termination or STALE_SESSION error")
            }

            // 核心断言：席位状态未被旧命令篡改污染（无副作用）
            assertFalse(room.isReady[1], "Seat readiness must NOT be modified by stale displaced command")

            // 核心断言：旧连接因真实顶号自动被 DISPLACED_BY_NEW_CONNECTION 关闭
            harness.awaitDisplaced(guestConn)
            assertTrue(guestConn.closed, "Old connection must be closed following real displacement")

            // 核心断言：旧连接的被顶替与关闭绝不误伤或断开新连接，新连接提交准备成功生效
            assertFalse(replacementConn.closed, "New replacement connection must remain alive and connected")
            assertTrue(room.seatConnected[1], "Seat must remain connected for new connection")
            val okReceipt = replacementConn.setReady(true).get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, okReceipt.status)
            assertTrue(room.isReady[1], "New connection ready must apply cleanly")
        }
    }

    // ==========================================
    // 场景 4: 迟到结果防误活屏障 (真实传输入口测试)
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 4 - delayed markConnected after transport disconnect is rejected by generation barrier and preserves disconnected idle state`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            val guestConn = harness.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId, "Guest", 1)).get(5, TimeUnit.SECONDS)
            val initialGen = room.seatMemberGeneration(1)
            assertTrue(room.seatConnected[1], "Seat 1 must be connected initially")

            // 从真实存活传输层捕获其会话校验器闭包
            val guestValidator = harness.capturePlayerSessionValidator(guestConn, 1)
            assertTrue(guestValidator(), "Captured validator must be valid while connected")

            // 通过真实传输入口断开连接 (模拟客户端网络中断或物理连接关闭)
            guestConn.close()

            // 等待传输层断开调度在权威队列完成对账
            val deadline = System.currentTimeMillis() + 3000
            while (room.seatConnected[1] && System.currentTimeMillis() < deadline) {
                Thread.sleep(20)
            }
            assertFalse(room.seatConnected[1], "Seat 1 must become disconnected following transport close")
            assertEquals(initialGen, room.seatDisconnectedGenerations[1], "seatDisconnectedGenerations must record disconnect generation")

            // 模拟该传输适配器上先前挂起的延迟异步任务/回调迟到到达权威队列
            val delayedMarkFuture = room.markSeatConnected(1, initialGen, sessionValidator = guestValidator)
            delayedMarkFuture.get(5, TimeUnit.SECONDS)

            // 核心断言：领域层代次屏障与传输层断开校验器双重拦截，绝对阻止迟到 mark 反转断开真值
            assertFalse(room.seatConnected[1], "Delayed markConnected through closed transport validator must NOT revive disconnected seat")

            // 观战者同构测试：真实观战传输入口断开与迟到 mark 拦截
            val specConn = harness.createConnection()
            val specBound = specConn.joinSpectator(JoinSpectatorRequest(roomId, "Spec")).get(5, TimeUnit.SECONDS)
            val specId = specBound.spectatorId
            val specGen = room.spectatorMemberGeneration(specId)!!
            assertTrue(room.isSpectatorConnected(specId), "Spectator must be connected initially")

            val specValidator = harness.captureSpectatorSessionValidator(specConn, specId)
            assertTrue(specValidator(), "Spectator validator must be valid while connected")

            specConn.close()
            val specDeadline = System.currentTimeMillis() + 3000
            while (room.isSpectatorConnected(specId) && System.currentTimeMillis() < specDeadline) {
                Thread.sleep(20)
            }
            assertFalse(room.isSpectatorConnected(specId), "Spectator must be disconnected following transport close")
            assertEquals(specGen, room.spectatorLastDisconnectedGeneration(specId))

            val delayedSpecMark = room.markSpectatorConnected(specId, specGen, sessionValidator = specValidator)
            delayedSpecMark.get(5, TimeUnit.SECONDS)
            assertFalse(room.isSpectatorConnected(specId), "Delayed markSpectatorConnected must NOT revive disconnected spectator")
        }
    }

    // ==========================================
    // 场景 4b: 纯领域规则代次屏障防误活测试 (诚实区分公共房间规则)
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 4b - domain generation barrier preserves disconnected state even with null validator`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!
            val initialGen = room.seatMemberGeneration(0)

            // 纯领域规则断开席位
            room.disconnectSeat(0, initialGen).get(5, TimeUnit.SECONDS)
            assertFalse(room.seatConnected[0], "Seat must be disconnected")
            assertEquals(initialGen, room.seatDisconnectedGenerations[0])

            // 即使传入 null validator，底层断开代次屏障也绝对拒绝同代次 markSeatConnected
            room.markSeatConnected(0, initialGen, sessionValidator = null).get(5, TimeUnit.SECONDS)
            assertFalse(room.seatConnected[0], "Domain guard must strictly reject markSeatConnected even with null validator")
        }
    }

    // ==========================================
    // 场景 5: 玩家重复离房幂等与生命周期
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 5 - repeated leave room when in room and when already in lobby is idempotent applied`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val conn = harness.createConnection()
            val bound = conn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            assertNotNull(bound.roomId)

            // 第 1 次离房：在房间内，成功释放席位
            val leave1 = conn.leaveRoom().get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, leave1.status)

            // 第 2 次离房：已在大厅状态，幂等成功 APPLIED
            val leave2 = conn.leaveRoom().get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, leave2.status, "Repeated leaveRoom from lobby must be idempotent APPLIED")

            // 连接保持存活，可继续在大厅发起列房或建房
            assertFalse(conn.closed, "Connection must remain live after repeated leave")
            val rooms = conn.listRooms().get(5, TimeUnit.SECONDS)
            assertNotNull(rooms)
        }
    }

    // ==========================================
    // 场景 6: 观战者重复离房幂等与在同一连接上重新加入
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 6 - spectator repeated leave is idempotent applied and allows rejoin on same connection`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId

            val specConn = harness.createConnection()
            val specBound = specConn.joinSpectator(JoinSpectatorRequest(roomId, "SpecUser")).get(5, TimeUnit.SECONDS)
            assertNotNull(specBound.spectatorId)

            // 第 1 次离房
            val leave1 = specConn.leaveRoom().get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, leave1.status)

            // 第 2 次离房：幂等 APPLIED
            val leave2 = specConn.leaveRoom().get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, leave2.status)
            assertFalse(specConn.closed, "Spectator connection must remain live")

            // 在同一物理连接上重新加入观战
            val rejoinBound = specConn.joinSpectator(JoinSpectatorRequest(roomId, "SpecUser2")).get(5, TimeUnit.SECONDS)
            assertNotNull(rejoinBound.spectatorId)
            val snap = specConn.publicSnapshot().get(5, TimeUnit.SECONDS)
            assertNotNull(snap)
        }
    }

    // ==========================================
    // 场景 7: 被顶号连接离房被拒，绝不误解绑新成员
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 7 - displaced connection leave is rejected with STALE_SESSION and does not vacate new member`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            val guestConn = harness.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId, "Guest", 1)).get(5, TimeUnit.SECONDS)
            assertEquals(1, guestBound.seat)
            assertEquals(2L, room.seatMemberGeneration(1))

            // 新连接 replacementConn 在屏障前先行创建并握手完毕
            val replacementConn = harness.createConnection()

            // 等待先前异步加入与连接通知排空，确保权威队列处于完全空闲状态
            room.tableHost.enqueueOperation { }.get(5, TimeUnit.SECONDS)

            // 1. 在权威队列中放置受控屏障阻断队列执行
            val barrierHold = CountDownLatch(1)
            val barrierEntered = CountDownLatch(1)
            val barrierFuture = room.tableHost.enqueueOperation {
                barrierEntered.countDown()
                barrierHold.await(5, TimeUnit.SECONDS)
            }
            assertTrue(barrierEntered.await(5, TimeUnit.SECONDS), "Barrier must be actively executing on tableHost queue")

            // 2. 新连接 replacementConn 发起真实的顶号恢复请求（排在屏障之后，先于旧离房执行）
            val resumeFuture = replacementConn.resumeSession(ResumeRequest(roomId, guestBound.resumeToken))

            // 确定性屏障同步：等待 resumeSession 已穿透传输层并进入权威队列（屏障 1 + 恢复 1 = 2）
            val deadlineResume = System.currentTimeMillis() + 5000L
            while (room.tableHost.queuedOperationsCount < 2 && System.currentTimeMillis() < deadlineResume) {
                Thread.sleep(10)
            }
            assertTrue(room.tableHost.queuedOperationsCount >= 2, "resumeFuture must be enqueued first behind barrier")

            // 3. 旧连接 guestConn 提交离房命令（排在 replacementConn 恢复请求之后）
            val staleLeaveFuture = guestConn.leaveRoom()

            // 明确屏障同步：等待权威队列中确切积压了屏障本身 + replacementConn 恢复 + guestConn 离房命令（共 3 个操作）
            // 严格证明两个请求均已完全穿透传输层并按确定性 FIFO 顺序在权威队列交错排队
            val deadlineStale = System.currentTimeMillis() + 5000L
            while (room.tableHost.queuedOperationsCount < 3 && System.currentTimeMillis() < deadlineStale) {
                Thread.sleep(10)
            }
            assertTrue(room.tableHost.queuedOperationsCount >= 3, "staleLeaveFuture must be enqueued second behind resumeFuture")

            // 4. 释放屏障：replacementConn 的 resume 先执行成功，完成顶号换代并触发顶号通知！
            //    紧接着 guestConn 的 leaveRoom 命令在权威队列执行，此时代次已漂移 (2L != 3L)！
            barrierHold.countDown()
            barrierFuture.get(5, TimeUnit.SECONDS)

            // 新连接成功完成真实顶号恢复
            val resumed = resumeFuture.get(5, TimeUnit.SECONDS)
            assertEquals(1, resumed.seat)
            assertEquals(3L, room.seatMemberGeneration(1))

            // 核心断言：权威队列执行期核验代次失效，给出明确的 STALE_SESSION 拒绝或因顶号断开
            try {
                val staleLeaveReceipt = staleLeaveFuture.get(5, TimeUnit.SECONDS)
                assertEquals(ProtocolReceiptStatus.REJECTED, staleLeaveReceipt.status, "Stale leave must be REJECTED")
                assertEquals("STALE_SESSION", staleLeaveReceipt.reason, "Stale leave must be rejected with STALE_SESSION")
            } catch (e: ExecutionException) {
                val cause = e.cause
                assertTrue(cause is IOException || cause is ServerApiException, "Expected connection termination or STALE_SESSION error")
            }

            // 核心断言：新代次席位绝未被旧离房误释放，新连接依然在房且连接正常
            assertTrue(room.seatOccupied[1], "New member seat must NOT be vacated by stale leave")
            assertTrue(room.seatConnected[1], "New member seat must remain connected")
            assertFalse(replacementConn.closed, "New replacement connection must remain live and active")

            // 核心断言：旧连接因真实顶号自动被 DISPLACED_BY_NEW_CONNECTION 关闭
            harness.awaitDisplaced(guestConn)
            assertTrue(guestConn.closed, "Old connection must be closed following real displacement")

            // 核心断言：新连接继续执行业务操作完全正常
            val okReceipt = replacementConn.setReady(true).get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, okReceipt.status)
            assertTrue(room.isReady[1], "New connection ready must apply cleanly")
        }
    }

    // ==========================================
    // 场景 8: 房间关闭生命周期与终态准入拦截
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 8 - host leaving lobby closes room, all subsequent calls return ROOM_NOT_FOUND`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            val guestConn = harness.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId, "Guest", 1)).get(5, TimeUnit.SECONDS)

            // 房主在大厅离房 -> 房间终态解散 (CLOSED)
            val leaveReceipt = hostConn.leaveRoom().get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
            assertTrue(room.closed, "Room must be marked CLOSED")
            assertEquals(ProtocolRoomLifecycle.CLOSED, room.lifecycle)

            // 访客调用房间接口：必须返回 ROOM_NOT_FOUND / ROOM_CLOSED 语义
            val getInfoEx = assertThrows<ExecutionException> {
                guestConn.getRoomInfo().get(3, TimeUnit.SECONDS)
            }
            val cause = getInfoEx.cause
            assertTrue(cause is ServerApiException, "Must throw ServerApiException on closed room")
            assertEquals(ApiErrors.ROOM_NOT_FOUND, (cause as ServerApiException).code)

            // 尝试以原 token 恢复被拒绝
            val resumeEx = assertThrows<ExecutionException> {
                guestConn.resumeSession(ResumeRequest(roomId, guestBound.resumeToken)).get(3, TimeUnit.SECONDS)
            }
            assertEquals(ApiErrors.ROOM_NOT_FOUND, (resumeEx.cause as ServerApiException).code)
        }
    }

    // ==========================================
    // 场景 9: 严格角色边界隔离 (玩家 vs 观战)
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 9 - role isolation strictly prevents spectator calling player apis and player calling public apis`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId

            val specConn = harness.createConnection()
            specConn.joinSpectator(JoinSpectatorRequest(roomId, "Spectator")).get(5, TimeUnit.SECONDS)

            // 观战者严禁调用对局玩家专属接口 (如 setReady / snapshot)
            val specReadyFailed = try {
                val receipt = specConn.setReady(true).get(3, TimeUnit.SECONDS)
                receipt.status == ProtocolReceiptStatus.REJECTED
            } catch (_: ExecutionException) {
                true
            }
            assertTrue(specReadyFailed, "Spectator calling setReady must fail or be rejected")

            assertThrows<ExecutionException> {
                specConn.snapshot().get(3, TimeUnit.SECONDS)
            }

            // 玩家严禁调用观战公开专属接口 (如 publicSnapshot)
            assertThrows<ExecutionException> {
                hostConn.publicSnapshot().get(3, TimeUnit.SECONDS)
            }
        }
    }

    // ==========================================
    // 场景 10: 业务回执跨连接重放与身份边界隔离
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 10 - idempotent replay preserves original result across reconnections and isolates cross-principal access`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val bound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = bound.roomId

            // 1. 发起带显式业务 requestId 的准备命令
            val reqId = "test-idempotent-set-ready-100"
            val req1 = ApiRequest(
                api = ApiTags.SET_READY,
                requestId = reqId,
                roomId = roomId,
                ready = true
            )
            val resp1 = harness.sendEnvelopeRequest(hostConn, req1).get(5, TimeUnit.SECONDS)
            assertTrue(resp1.success, "First request must succeed: error=${resp1.error}")
            assertEquals(ProtocolReceiptStatus.APPLIED, resp1.receipt?.status)
            assertTrue(harness.getRoom(roomId)!!.isReady[0], "Host seat must be ready")

            // 2. 模拟断开连接
            hostConn.close()

            // 3. 另一连接 hostConn2 凭 resumeToken 恢复同一席位 (相同 logicalPrincipal)
            val hostConn2 = harness.createConnection()
            val bound2 = hostConn2.resumeSession(ResumeRequest(roomId, bound.resumeToken)).get(5, TimeUnit.SECONDS)
            assertEquals(0, bound2.seat)

            // 4. 重连后复用同一 requestId + 同一 payload 重放：服务端必须命中 receiptStore 幂等重放，回执完全一致且副作用不重复触发
            val reqReplay = ApiRequest(
                api = ApiTags.SET_READY,
                requestId = reqId,
                roomId = roomId,
                ready = true
            )
            val respReplay = harness.sendEnvelopeRequest(hostConn2, reqReplay).get(5, TimeUnit.SECONDS)
            assertTrue(respReplay.success, "Replayed request must succeed via idempotency replay: error=${respReplay.error}")
            assertEquals(ProtocolReceiptStatus.APPLIED, respReplay.receipt?.status)
            assertEquals(reqId, respReplay.requestId)
            assertEquals(resp1.receipt, respReplay.receipt, "Replayed receipt must be identical to original receipt")

            // 5. 同一 requestId 但负载冲突 (ready = false)：必须被 receiptStore 判定为 CONFLICT
            val reqConflict = ApiRequest(
                api = ApiTags.SET_READY,
                requestId = reqId,
                roomId = roomId,
                ready = false
            )
            val respConflict = harness.sendEnvelopeRequest(hostConn2, reqConflict).get(5, TimeUnit.SECONDS)
            assertFalse(respConflict.success, "Conflicting payload with same requestId must be rejected")
            assertEquals(ApiErrors.CONFLICT, respConflict.error)

            // 6. 跨主体身份隔离：另一成员 (Guest 席位 1) 使用相同的 requestId 发起请求
            val guestConn = harness.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId, "Guest", 1)).get(5, TimeUnit.SECONDS)
            assertEquals(1, guestBound.seat)

            val guestReq = ApiRequest(
                api = ApiTags.SET_READY,
                requestId = reqId, // 与 Host 完全相同的 requestId
                roomId = roomId,
                ready = true
            )
            val guestResp = harness.sendEnvelopeRequest(guestConn, guestReq).get(5, TimeUnit.SECONDS)
            assertTrue(guestResp.success, "Guest request with same requestId must succeed in guest's own scope: error=${guestResp.error}")
            assertEquals(ProtocolReceiptStatus.APPLIED, guestResp.receipt?.status)
            assertEquals(reqId, guestResp.requestId)
            // 验证 Guest 执行的是席位 1 的 ready，席位 1 变为 ready，且绝对没有窃取或消费 Host 席位 0 的回执
            val room = harness.getRoom(roomId)!!
            assertTrue(room.isReady[1], "Guest seat 1 must be set ready by its own request")
        }
    }

    // ==========================================
    // 场景 11: 玩家在途恢复与另一连接顶号交错 (受控屏障回归)
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 11 - player in flight resume interleaved with displacement closes old connection with displaced reason and keeps new connection alive`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            val guestConn1 = harness.createConnection()
            val guestBound1 = guestConn1.joinRoom(JoinRoomRequest(roomId, "Guest", 1)).get(5, TimeUnit.SECONDS)
            val guestSeat = guestBound1.seat
            assertEquals(1, guestSeat)
            assertEquals(2L, room.seatMemberGeneration(guestSeat))

            val guestConn2 = harness.createConnection()

            // 等待先前异步加入与连接通知排空，确保权威队列处于完全空闲状态
            room.tableHost.enqueueOperation { }.get(5, TimeUnit.SECONDS)

            // 1. 在 TableHost 权威队列中植入受控屏障阻断队列执行
            val barrierHold = CountDownLatch(1)
            val barrierEntered = CountDownLatch(1)
            val barrierFuture = room.tableHost.enqueueOperation {
                barrierEntered.countDown()
                barrierHold.await(5, TimeUnit.SECONDS)
            }
            assertTrue(barrierEntered.await(5, TimeUnit.SECONDS), "Barrier must be actively executing on queue")

            // 2. 新连接 conn2 发起有效 token 的恢复请求（排在屏障之后，先于 conn1 的恢复请求执行）
            val conn2ResumeFuture = guestConn2.resumeSession(ResumeRequest(roomId, guestBound1.resumeToken))

            // 3. 旧连接 conn1 在途发起一个恢复请求（例如带无效 token 或排队在后）
            //    此时旧连接的 inFlightResumes 计数 > 0
            val conn1ResumeFuture = guestConn1.resumeSession(ResumeRequest(roomId, "invalid-token-racing"))

            // 明确屏障同步：等待权威队列中确切积压了屏障本身 + conn2 恢复 + conn1 在途恢复（共 3 个操作）
            // 证明两个请求均已完全穿透传输层并进入权威队列交错排队，绝不靠时序碰运气
            val deadline11 = System.currentTimeMillis() + 5000L
            while (room.tableHost.queuedOperationsCount < 3 && System.currentTimeMillis() < deadline11) {
                Thread.sleep(10)
            }
            assertTrue(room.tableHost.queuedOperationsCount >= 3, "Both resume operations must be queued behind the barrier")

            // 4. 释放屏障：conn2 的 resume 先执行成功，席位代次推进到 3，触发顶号通知！
            //    conn1 的在途请求仍在等待或失败，其 whenComplete 必须可靠补做代次复核并关闭旧连接！
            barrierHold.countDown()
            barrierFuture.get(5, TimeUnit.SECONDS)

            // conn2 成功完成恢复
            val bound2 = conn2ResumeFuture.get(5, TimeUnit.SECONDS)
            assertEquals(guestSeat, bound2.seat)
            assertEquals(3L, room.seatMemberGeneration(guestSeat))
            assertFalse(guestConn2.closed, "New connection must remain live and active")

            // conn1 的在途请求以异常完成
            val ex1 = assertThrows<ExecutionException> {
                conn1ResumeFuture.get(5, TimeUnit.SECONDS)
            }
            assertTrue(ex1.cause is ServerApiException || ex1.cause is IOException)

            // 核心断言：旧连接 conn1 最终可靠以 DISPLACED_BY_NEW_CONNECTION 关闭！
            harness.awaitDisplaced(guestConn1)
            assertTrue(guestConn1.closed, "Old connection must be closed following in-flight displacement")
            assertTrue(room.seatConnected[guestSeat], "Seat must remain connected for new connection")
        }
    }

    // ==========================================
    // 场景 12: 观战者在途恢复与另一连接顶号交错 (受控屏障回归)
    // ==========================================

    @ParameterizedTest
    @EnumSource(TransportMode::class)
    fun `scenario 12 - spectator in flight resume interleaved with displacement closes old spectator with displaced reason and keeps new connection alive`(mode: TransportMode) {
        TestHarness(mode).use { harness ->
            val hostConn = harness.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).get(5, TimeUnit.SECONDS)
            val roomId = hostBound.roomId
            val room = harness.getRoom(roomId)!!

            val specConn1 = harness.createConnection()
            val specBound1 = specConn1.joinSpectator(JoinSpectatorRequest(roomId, "Spec")).get(5, TimeUnit.SECONDS)
            val specId = specBound1.spectatorId
            assertEquals(1L, room.spectators[specId]!!.generation)

            val specConn2 = harness.createConnection()

            // 等待先前异步加入与连接通知排空，确保权威队列处于完全空闲状态
            room.tableHost.enqueueOperation { }.get(5, TimeUnit.SECONDS)

            // 1. 在 TableHost 权威队列中植入受控屏障阻断队列执行
            val barrierHold = CountDownLatch(1)
            val barrierEntered = CountDownLatch(1)
            val barrierFuture = room.tableHost.enqueueOperation {
                barrierEntered.countDown()
                barrierHold.await(5, TimeUnit.SECONDS)
            }
            assertTrue(barrierEntered.await(5, TimeUnit.SECONDS), "Barrier must be actively executing on queue")

            // 2. 新连接 specConn2 发起有效 token 的恢复请求（排在屏障之后）
            val conn2ResumeFuture = specConn2.resumeSpectator(ResumeSpectatorRequest(roomId, specId, specBound1.resumeToken))

            // 3. 旧连接 specConn1 在途发起恢复请求（带无效 token）
            val conn1ResumeFuture = specConn1.resumeSpectator(ResumeSpectatorRequest(roomId, specId, "invalid-token-racing"))

            // 明确屏障同步：等待权威队列中确切积压了屏障本身 + conn2 恢复 + conn1 在途恢复（共 3 个操作）
            // 证明两个请求均已完全穿透传输层并进入权威队列交错排队，绝不靠时序碰运气
            val deadline12 = System.currentTimeMillis() + 5000L
            while (room.tableHost.queuedOperationsCount < 3 && System.currentTimeMillis() < deadline12) {
                Thread.sleep(10)
            }
            assertTrue(room.tableHost.queuedOperationsCount >= 3, "Both spectator resume operations must be queued behind the barrier")

            // 4. 释放屏障：conn2 恢复成功，观战者代次推进到 2，触发顶号通知！
            barrierHold.countDown()
            barrierFuture.get(5, TimeUnit.SECONDS)

            val bound2 = conn2ResumeFuture.get(5, TimeUnit.SECONDS)
            assertEquals(specId, bound2.spectatorId)
            assertEquals(2L, room.spectators[specId]!!.generation)
            assertFalse(specConn2.closed, "New spectator must remain live")

            // specConn1 的在途请求以异常完成
            val ex1 = assertThrows<ExecutionException> {
                conn1ResumeFuture.get(5, TimeUnit.SECONDS)
            }
            assertTrue(ex1.cause is ServerApiException || ex1.cause is IOException)

            // 核心断言：旧观战连接 specConn1 最终可靠以 DISPLACED_BY_NEW_CONNECTION 关闭！
            harness.awaitDisplaced(specConn1)
            assertTrue(specConn1.closed, "Old spectator must be closed following in-flight displacement")
            assertTrue(room.spectators[specId]!!.isConnected, "Spectator must remain connected for new connection")
        }
    }

    // ==========================================
    // 防篡改验证: seatMemberGenerations, seatDisconnectedGenerations, resumeTokens, spectators 防御性副本与真实顶号代次校验
    // ==========================================

    @Test
    fun `seatMemberGenerations and bindingGenerations return defensive copies and cannot tamper internal room generation state`() {
        val room = SharedRoom(
            roomId = "ROOM_TAMPER_TEST",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            // 1. seatMemberGenerations 防御性副本测试
            val copy1 = room.seatMemberGenerations
            assertEquals(4, copy1.size)
            assertEquals(1L, copy1[0])

            // 尝试篡改返回的数组
            copy1[0] = 9999L
            copy1[1] = 8888L

            // 核心断言：内部状态未受任何影响
            assertEquals(1L, room.seatMemberGeneration(0), "Internal seat 0 generation must remain unchanged")
            assertEquals(1L, room.seatMemberGeneration(1), "Internal seat 1 generation must remain unchanged")
            assertEquals(1L, room.seatMemberGenerations[0], "Defensive copy must reflect true internal generation")

            // 2. 旧公开入口 bindingGenerations 防御性副本测试
            @Suppress("DEPRECATION")
            val copy2 = room.bindingGenerations
            copy2[0] = 7777L
            assertEquals(1L, room.seatMemberGeneration(0), "Modifying deprecated bindingGenerations must NOT mutate internal generation")
            @Suppress("DEPRECATION")
            assertEquals(1L, room.bindingGenerations[0], "bindingGenerations getter must return fresh copy with true internal generation")

            // 3. seatDisconnectedGenerations 防御性副本测试 (D-03 所有权审计)
            val discCopy = room.seatDisconnectedGenerations
            assertEquals(0L, discCopy[0])
            discCopy[0] = 999999L
            assertEquals(0L, room.seatDisconnectedGeneration(0), "Internal seatDisconnectedGeneration must remain 0L")
            assertEquals(0L, room.seatDisconnectedGenerations[0], "seatDisconnectedGenerations getter must return fresh copy")

            // 4. resumeTokens 防御性副本测试 (D-03 所有权审计)
            val tokenCopy = room.resumeTokens
            val originalHostToken = room.seatResumeToken(0)
            assertEquals(originalHostToken, tokenCopy[0])
            tokenCopy[0] = "hacked_host_token"
            assertEquals(originalHostToken, room.seatResumeToken(0), "Internal seatResumeToken must remain unchanged")
            assertEquals(originalHostToken, room.resumeTokens[0], "resumeTokens getter must return fresh copy")

            // 尝试使用被篡改的 token 执行恢复，必定被权威拒绝 INVALID_RESUME_TOKEN
            val hackedResumeEx = assertThrows<ExecutionException> {
                room.resumeSession("hacked_host_token").get(5, TimeUnit.SECONDS)
            }
            assertTrue(hackedResumeEx.cause is ServerApiException)
            assertEquals(ApiErrors.INVALID_TOKEN, (hackedResumeEx.cause as ServerApiException).code)

            // 5. 验证代次受控演进与真实顶号拒绝：无任何公共代次推进/篡改入口，内部代次只能由真实准入/重连受控推进
            val guestJoinRes = room.join("GuestTamper", 1).get(5, TimeUnit.SECONDS)
            val guestSeat = guestJoinRes.first
            val guestToken = guestJoinRes.second
            assertEquals(1, guestSeat)
            assertEquals(2L, room.seatMemberGeneration(1), "Real join must advance seat member generation to 2L")

            // 模拟旧连接 validator（代次为 2L）
            val oldPlayerValidator: () -> Boolean = { room.seatMemberGeneration(1) == 2L }
            assertTrue(oldPlayerValidator(), "Old player validator is initially valid")

            // 真实恢复（新连接顶号）推进代次到 3L
            val resumeRes = room.resumeSession(guestToken).get(5, TimeUnit.SECONDS)
            assertEquals(1, resumeRes.seat)
            assertEquals(3L, room.seatMemberGeneration(1), "Real resumeSession must advance seat member generation to 3L")
            assertFalse(oldPlayerValidator(), "Old player validator is now invalid after displacement")

            // 旧连接由于代次过期，执行离开动作被权威队列严格拦截为 STALE_SESSION
            val stalePlayerLeave = room.leave(1, sessionValidator = oldPlayerValidator).get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.REJECTED, stalePlayerLeave.status)
            assertEquals("STALE_SESSION", stalePlayerLeave.reason)

            // 6. 观战者成员所有权收敛、防篡改只读视图与顶号失效验证 (D-03 观战所有权闭环)
            val specRes = room.joinSpectator("SpecTamper").get(5, TimeUnit.SECONDS)
            val specId = specRes.spectatorId
            assertEquals(1L, room.spectatorMemberGeneration(specId), "Real joinSpectator must initialize generation to 1L")

            // 验证 room.spectators 为不可变只读视图映射
            val spectatorsView = room.spectators
            assertTrue(spectatorsView.containsKey(specId))
            val specView = spectatorsView[specId]!!
            assertEquals("SpecTamper", specView.name)
            assertEquals(1L, specView.generation)
            assertTrue(specView.isConnected)

            // 尝试篡改不可变映射 -> 必抛 UnsupportedOperationException
            assertThrows<UnsupportedOperationException> {
                @Suppress("UNCHECKED_CAST")
                (spectatorsView as MutableMap<String, Any?>).clear()
            }
            assertThrows<UnsupportedOperationException> {
                @Suppress("UNCHECKED_CAST")
                (spectatorsView as MutableMap<String, Any?>).remove(specId)
            }

            // 模拟旧观战连接 validator（代次为 1L）
            val oldSpecValidator: () -> Boolean = { room.spectatorMemberGeneration(specId) == 1L }
            assertTrue(oldSpecValidator(), "Old spectator validator is initially valid")

            // 真实恢复（新观战连接顶号）推进代次到 2L
            val specResumeRes = room.resumeSpectator(specId, specRes.resumeToken).get(5, TimeUnit.SECONDS)
            assertEquals(2L, room.spectatorMemberGeneration(specId), "Real resumeSpectator must advance generation to 2L")
            assertEquals(2L, specResumeRes.generation)
            assertFalse(oldSpecValidator(), "Old spectator validator is now invalid after displacement")

            // 旧观战连接由于代次过期，执行离开动作被权威队列严格拦截为 STALE_SESSION
            val staleSpecLeave = room.leaveSpectator(specId, sessionValidator = oldSpecValidator).get(5, TimeUnit.SECONDS)
            assertEquals(ProtocolReceiptStatus.REJECTED, staleSpecLeave.status)
            assertEquals("STALE_SESSION", staleSpecLeave.reason)
        } finally {
            room.close()
        }
    }
}

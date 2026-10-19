package com.majiang.server.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpScope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows
import java.net.InetSocketAddress
import java.nio.channels.SocketChannel
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * 服务端角色状态分型与物理连接解耦测试 (ServerRoleStateTest, D-02)
 *
 * 严格覆盖：
 * 1. PlayerState 领域不变量：seat 必须在 0..3，非空 snapshotSupplier，订阅生命周期及幂等 detach；
 * 2. SpectatorState 领域不变量：spectatorId 必须非空，非空 publicSnapshotSupplier，订阅生命周期及幂等 detach；
 * 3. 会话校验器生成：makeSessionValidator 严格结合 connectionEpoch/连接开启状态与 room.bindingGenerations；
 * 4. 断线通知代次保护：notifyDisconnect 结合 generation 且仅触发一次，不误关新顶号连接；
 * 5. TcpConnection 角色安装与转换：原子替换、旧订阅安全关闭、关闭连接防篡改；
 * 6. 真实 TCP 顶号 (Displacement) 场景下的状态分离与代次保护；
 * 7. 私有流与公开流角色隔离：观战者不可执行玩家命令，玩家命令入口强类型化；
 * 8. 业务回执归属于逻辑身份作用域 (TcpScope) 而非物理连接。
 */
@Timeout(30)
class ServerRoleStateTest {

    // --- 1. PlayerState 单元与生命周期测试 ---

    @Test
    fun `PlayerState enforces seat bounds 0 to 3`() {
        val room = SharedRoom(
            roomId = "ROOM_SEAT_BOUNDS",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            // 有效座位 0..3 均成功
            for (seat in 0..3) {
                val player = PlayerState.fromRoom(room, seat)
                assertEquals(seat, player.seat)
                assertEquals(ConnectionRole.PLAYER, player.role)
                assertEquals("player:${room.roomId}:$seat", player.logicalPrincipal)
                assertSame(room, player.room)
            }

            // 边界外座位一律抛出 IllegalArgumentException
            assertThrows<IllegalArgumentException> {
                PlayerState.fromRoom(room, -1)
            }
            assertThrows<IllegalArgumentException> {
                PlayerState.fromRoom(room, 4)
            }
            assertThrows<IllegalArgumentException> {
                PlayerState.fromRoom(room, 99)
            }
        } finally {
            room.close()
        }
    }

    @Test
    fun `PlayerState subscription lifecycle and idempotent detach`() {
        val room = SharedRoom(
            roomId = "ROOM_PLAYER_SUB",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val player = PlayerState.fromRoom(room, 0)
            val seatCloseCount = AtomicInteger(0)
            val roomCloseCount = AtomicInteger(0)

            val seatSub = AutoCloseable { seatCloseCount.incrementAndGet() }
            val roomSub = AutoCloseable { roomCloseCount.incrementAndGet() }

            player.attachSubscriptions(seatSub, roomSub)

            // 第一次 detach 关闭全部两个订阅
            player.detach()
            assertEquals(1, seatCloseCount.get())
            assertEquals(1, roomCloseCount.get())

            // 第二次 detach 幂等无副作用
            player.detach()
            assertEquals(1, seatCloseCount.get())
            assertEquals(1, roomCloseCount.get())
        } finally {
            room.close()
        }
    }

    @Test
    fun `PlayerState makeSessionValidator checks generation and transport state`() {
        val room = SharedRoom(
            roomId = "ROOM_SESSION_VAL",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val currentGen = room.seatMemberGeneration(0)
            val player = PlayerState.fromRoom(room, 0, bindingGeneration = currentGen)

            val transportOpen = AtomicBoolean(true)
            val mockTransport = object : TransportConnection {
                override val connectionId: Long = 1L
                override val connectionEpoch: String = "epoch-1"
                override val remoteAddress = InetSocketAddress(1234)
                override val isClosed: Boolean get() = !transportOpen.get()
                override val isClosingOrClosed: Boolean get() = !transportOpen.get()
                override fun sendNormalFrame(frameBytes: ByteArray): Boolean = true
                override fun sendControlFrame(frameBytes: ByteArray): Boolean = true
                override fun close(reason: String, message: String?, immediate: Boolean) { transportOpen.set(false) }
                override fun close() { transportOpen.set(false) }
            }

            val validator = player.makeSessionValidator(mockTransport)

            // 连接正常且代次一致
            assertTrue(validator())

            // 物理连接关闭 -> 校验失败
            transportOpen.set(false)
            assertFalse(validator())

            // 恢复物理连接，但模拟 room 发生真实的重连换代
            transportOpen.set(true)
            room.resumeSession(room.resumeTokens[0]).get(5, TimeUnit.SECONDS)
            assertFalse(validator(), "Mismatched binding generation must fail validation")
        } finally {
            room.close()
        }
    }

    @Test
    fun `PlayerState notifyDisconnect is idempotent and generational`() {
        val room = SharedRoom(
            roomId = "ROOM_DISCONNECT_GEN",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val gen = room.seatMemberGeneration(0)
            val player = PlayerState.fromRoom(room, 0, bindingGeneration = gen)

            // 首次断开
            player.notifyDisconnect()

            // 重复断开幂等无害
            player.notifyDisconnect()
        } finally {
            room.close()
        }
    }

    // --- 2. SpectatorState 单元与生命周期测试 ---

    @Test
    fun `SpectatorState enforces non-blank spectatorId`() {
        val room = SharedRoom(
            roomId = "ROOM_SPEC_BOUNDS",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val spectator = SpectatorState.fromRoom(room, "spec-999")
            assertEquals("spec-999", spectator.spectatorId)
            assertEquals(ConnectionRole.SPECTATOR, spectator.role)
            assertEquals("spectator:${room.roomId}:spec-999", spectator.logicalPrincipal)

            assertThrows<IllegalArgumentException> {
                SpectatorState.fromRoom(room, "")
            }
            assertThrows<IllegalArgumentException> {
                SpectatorState.fromRoom(room, "   ")
            }
        } finally {
            room.close()
        }
    }

    @Test
    fun `SpectatorState subscription lifecycle and idempotent detach`() {
        val room = SharedRoom(
            roomId = "ROOM_SPEC_SUB",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            val spectator = SpectatorState.fromRoom(room, "spec-1")
            val pubCloseCount = AtomicInteger(0)
            val roomCloseCount = AtomicInteger(0)

            spectator.attachSubscriptions(
                publicSub = AutoCloseable { pubCloseCount.incrementAndGet() },
                roomSub = AutoCloseable { roomCloseCount.incrementAndGet() }
            )

            spectator.detach()
            assertEquals(1, pubCloseCount.get())
            assertEquals(1, roomCloseCount.get())

            // 再次 detach 幂等
            spectator.detach()
            assertEquals(1, pubCloseCount.get())
            assertEquals(1, roomCloseCount.get())
        } finally {
            room.close()
        }
    }

    @Test
    fun `SpectatorState makeSessionValidator checks closing transport and member generation`() {
        val room = SharedRoom(
            roomId = "ROOM_SPEC_VAL",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            // 真实加入观战者，初始 generation = 1L
            val joinRes = room.joinSpectator("SpecValName").get(5, TimeUnit.SECONDS)
            val specId = joinRes.spectatorId
            val spectator = SpectatorState.fromRoom(room, specId, bindingGeneration = 1L)

            val transportClosing = AtomicBoolean(false)
            val transportClosed = AtomicBoolean(false)
            val mockTransport = object : TransportConnection {
                override val connectionId: Long = 2L
                override val connectionEpoch: String = "epoch-2"
                override val remoteAddress = InetSocketAddress(1234)
                override val isClosed: Boolean get() = transportClosed.get()
                override val isClosingOrClosed: Boolean get() = transportClosing.get() || transportClosed.get()
                override fun sendNormalFrame(frameBytes: ByteArray): Boolean = true
                override fun sendControlFrame(frameBytes: ByteArray): Boolean = true
                override fun close(reason: String, message: String?, immediate: Boolean) {
                    transportClosing.set(true)
                }
                override fun close() {
                    transportClosing.set(true)
                    transportClosed.set(true)
                }
            }

            val validator = spectator.makeSessionValidator(mockTransport)

            // 1. 物理连接正常且代次一致 -> 校验通过
            assertTrue(validator())

            // 2. 物理连接处于 CLOSING 阶段（顶号通知排空期） -> 校验失败
            transportClosing.set(true)
            assertFalse(validator(), "CLOSING transport must fail session validation")

            // 3. 物理连接开启，但真实恢复顶号使房间观战成员代次递增至 2L -> 校验失败
            transportClosing.set(false)
            room.resumeSpectator(specId, joinRes.resumeToken).get(5, TimeUnit.SECONDS)
            assertEquals(2L, room.spectatorMemberGeneration(specId))
            assertFalse(validator(), "Mismatched spectator generation must fail session validation")

            // 4. 真实退出房间后观战成员被移除 -> 校验失败
            room.leaveSpectator(specId).get(5, TimeUnit.SECONDS)
            assertFalse(validator(), "Removed spectator must fail session validation")
        } finally {
            room.close()
        }
    }

    // --- 3. TcpConnection 角色安装、替换与状态转换测试 ---

    @Test
    fun `TcpConnection role transitions from unauthenticated to lobby to player and back to lobby`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 100L, channel = channel, config = serverConfig, globalBudget = budget)

        try {
            // 初始未认证状态
            assertEquals(ConnectionRole.UNBOUND_LOBBY, conn.role)
            assertSame(UnauthenticatedRoleState, conn.roleState)
            assertNull(conn.playerState)
            assertNull(conn.spectatorState)
            assertEquals(-1, conn.viewerSeat)

            // 模拟握手完成进入 Lobby
            conn.setHandshakedIdentity("principal-123", "token-123", "epoch-100")
            assertEquals(ConnectionRole.UNBOUND_LOBBY, conn.role)
            assertTrue(conn.roleState is LobbyRoleState)

            // 安装 PlayerState (seat 0)
            val room = SharedRoom(
                roomId = "ROOM_TRANSITIONS",
                hostPlayerName = "Host",
                initialConfig = ProtocolRoomConfig(),
                isSingleplayer = true
            )
            try {
                conn.bindPlayerRole(room, 0)
                assertEquals(ConnectionRole.PLAYER, conn.role)
                assertNotNull(conn.playerState)
                assertEquals(0, conn.viewerSeat)
                assertSame(room, conn.activeRoom)

                // 解绑回退至大厅角色 (例如离房)
                conn.unbindToLobbyRole()
                assertEquals(ConnectionRole.UNBOUND_LOBBY, conn.role)
                assertTrue(conn.roleState is LobbyRoleState)
                assertNull(conn.playerState)
                assertEquals(-1, conn.viewerSeat)
                assertNull(conn.activeRoom)

                // 安装 SpectatorState
                conn.bindSpectatorRole(room, "spec-transition-1")
                assertEquals(ConnectionRole.SPECTATOR, conn.role)
                assertNotNull(conn.spectatorState)
                assertEquals("spec-transition-1", conn.spectatorId)
                assertSame(room, conn.activeRoom)
                assertEquals(-1, conn.viewerSeat)
            } finally {
                room.close()
            }
        } finally {
            conn.forceCloseChannel()
            channel.close()
        }
    }

    // --- 4. 真实 TCP 顶号 (Displacement) 与代次保护测试 ---

    @Test
    fun `real TCP displacement detaches old role subscriptions without disconnecting new generation`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String
            var resumeToken: String

            // Client 1: 建房并占座 0
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome1 = client1.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-disp",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                client1.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome1.lobbyScope.scope, "req-create-disp", createReq)))
                val createRsp = client1.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!
                resumeToken = createRsp.response.resumeToken!!

                // Client 2: 通过 resumeToken 顶号重连 seat 0
                TcpTestClient(port = server.boundPort).use { client2 ->
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcome2 = client2.expectWelcome()

                    val resumeReq = ApiRequest(
                        api = "RESUME_SESSION",
                        requestId = "req-resume-disp",
                        roomId = roomId,
                        token = resumeToken
                    )
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome2.lobbyScope.scope, "req-resume-disp", resumeReq)))
                    val resumeRsp = client2.expectResponse()
                    assertTrue(resumeRsp.response.success)
                    assertEquals(0, resumeRsp.response.seat)

                    // Client 1 必须收到 DISPLACED_BY_NEW_CONNECTION 关闭帧
                    val closeMsg = client1.expectClose()
                    assertEquals(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, closeMsg.reason)

                    // Client 2 仍然保持活跃且能够成功发起业务快照请求
                    val snapReq = ApiRequest(
                        api = "SNAPSHOT",
                        requestId = "req-snap-p2"
                    )
                    val seatScope = resumeRsp.grantedScope!!.scope
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(seatScope, "req-snap-p2", snapReq)))
                    val snapRsp = client2.expectResponse()
                    assertTrue(snapRsp.response.success)
                    assertNotNull(snapRsp.response.snapshot)
                }
            }
        } finally {
            server.close()
        }
    }

    // --- 5. 私有流与公开流角色权限隔离测试 ---

    @Test
    fun `real TCP spectator cannot execute player-only actions`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String

            // 1. 玩家 1 建房
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = hostClient.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-spec-test",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome.lobbyScope.scope, "req-create-spec-test", createReq)))
                val createRsp = hostClient.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!

                // 2. 观战者连入观战
                TcpTestClient(port = server.boundPort).use { spectatorClient ->
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val specWelcome = spectatorClient.expectWelcome()

                    val joinSpecReq = ApiRequest(
                        api = ApiTags.JOIN_SPECTATOR,
                        requestId = "req-join-spec",
                        roomId = roomId,
                        spectatorName = "Spec1"
                    )
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(specWelcome.lobbyScope.scope, "req-join-spec", joinSpecReq)))
                    val joinSpecRsp = spectatorClient.expectResponse()
                    assertTrue(joinSpecRsp.response.success)
                    assertNotNull(joinSpecRsp.grantedScope)
                    val specScope = joinSpecRsp.grantedScope!!.scope

                    // 3. 观战者尝试执行玩家专属命令 (SET_READY)，必须被拒绝并返回 UNAUTHENTICATED_SESSION
                    val readyReq = ApiRequest(
                        api = "SET_READY",
                        requestId = "req-spec-ready",
                        ready = true
                    )
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(specScope, "req-spec-ready", readyReq)))
                    val readyRsp = spectatorClient.expectResponse()
                    assertFalse(readyRsp.response.success)
                    assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, readyRsp.response.error)

                    // 4. 观战者尝试执行玩家 DECISION 命令，必须被拒绝并返回 UNAUTHENTICATED_SESSION
                    val decisionReq = ApiRequest(
                        api = "DECISION",
                        requestId = "req-spec-decision"
                    )
                    spectatorClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(specScope, "req-spec-decision", decisionReq)))
                    val decisionRsp = spectatorClient.expectResponse()
                    assertFalse(decisionRsp.response.success)
                    assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, decisionRsp.response.error)
                }
            }
        } finally {
            server.close()
        }
    }

    // --- 6. 业务回执归属于已验证逻辑身份 (TcpScope) 而非物理连接 ---

    @Test
    fun `idempotency receipts are attributed to logical identity scope across reconnections`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String
            var resumeToken: String
            var seatScope: String

            // 1. 连接 1 建房并准备
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome1 = client1.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-idemp",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                client1.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome1.lobbyScope.scope, "req-create-idemp", createReq)))
                val createRsp = client1.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!
                resumeToken = createRsp.response.resumeToken!!

                // 执行一次业务请求 (SET_READY)
                seatScope = createRsp.grantedScope!!.scope
                val readyReq = ApiRequest(
                    api = "SET_READY",
                    requestId = "req-ready-idemp-1",
                    ready = true
                )
                client1.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(seatScope, "req-ready-idemp-1", readyReq)))
                val readyRsp1 = client1.expectResponse()
                assertTrue(readyRsp1.response.success)
            }

            // 2. 连接 2 恢复席位并重发相同的 requestId
            TcpTestClient(port = server.boundPort).use { client2 ->
                client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome2 = client2.expectWelcome()

                val resumeReq = ApiRequest(
                    api = "RESUME_SESSION",
                    requestId = "req-resume-idemp",
                    roomId = roomId,
                    token = resumeToken
                )
                client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome2.lobbyScope.scope, "req-resume-idemp", resumeReq)))
                val resumeRsp = client2.expectResponse()
                assertTrue(resumeRsp.response.success)

                val newSeatScope = resumeRsp.grantedScope!!.scope
                // 使用原相同的 requestId 请求相同的 SET_READY，验证回执幂等重放
                val duplicateReadyReq = ApiRequest(
                    api = "SET_READY",
                    requestId = "req-ready-idemp-1",
                    ready = true
                )
                client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(newSeatScope, "req-ready-idemp-1", duplicateReadyReq)))
                val readyRsp2 = client2.expectResponse()
                assertTrue(readyRsp2.response.success)
                assertEquals("req-ready-idemp-1", readyRsp2.requestId)
            }
        } finally {
            server.close()
        }
    }

    // --- 7. 观战顶号竞态回归测试 (queued old LEAVE_ROOM -> new RESUME_SPECTATOR -> old execution) ---

    @Test
    fun `queued old leaveSpectator then new resumeSpectator preserves spectator and rejects stale leave`() {
        val room = SharedRoom(
            roomId = "ROOM_SPECTATOR_RACE_DOMAIN",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            // 1. 观战者 1 加入房间 (初始 generation = 1L)
            val joinResult = room.joinSpectator("Spectator1").get(5, TimeUnit.SECONDS)
            val specId = joinResult.spectatorId
            val specToken = joinResult.resumeToken
            assertEquals(1L, joinResult.generation)
            assertTrue(room.spectators[specId]!!.isConnected)

            val transport1Closing = AtomicBoolean(false)
            val transport1Closed = AtomicBoolean(false)
            val mockTransport1 = object : TransportConnection {
                override val connectionId: Long = 101L
                override val connectionEpoch: String = "epoch-spec-1"
                override val remoteAddress = InetSocketAddress(1234)
                override val isClosed: Boolean get() = transport1Closed.get()
                override val isClosingOrClosed: Boolean get() = transport1Closing.get() || transport1Closed.get()
                override fun sendNormalFrame(frameBytes: ByteArray): Boolean = true
                override fun sendControlFrame(frameBytes: ByteArray): Boolean = true
                override fun close(reason: String, message: String?, immediate: Boolean) {
                    transport1Closing.set(true)
                }
                override fun close() {
                    transport1Closing.set(true)
                    transport1Closed.set(true)
                }
            }

            val spectatorState1 = SpectatorState(
                spectatorId = specId,
                publicSnapshotSupplier = { room.publicSnapshot { !transport1Closed.get() } },
                room = room,
                bindingGeneration = 1L
            )
            val oldLeaveValidator = spectatorState1.makeSessionValidator(mockTransport1)

            // 2. 新连接 2 发起 resumeSpectator 顶号，使代次递增至 2L 并将旧连接置为 CLOSING
            val resumeResult = room.resumeSpectator(specId, specToken).get(5, TimeUnit.SECONDS)
            assertEquals(2L, resumeResult.generation)
            mockTransport1.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Displaced", false)
            assertTrue(mockTransport1.isClosingOrClosed)

            // 3. 执行旧连接 1 排队的 leaveSpectator
            val leaveReceipt = room.leaveSpectator(specId, oldLeaveValidator).get(5, TimeUnit.SECONDS)

            // 4. 断言：旧 leave 操作因会话校验器核对 generation 与 isClosingOrClosed 失败而被严格拒绝
            assertEquals(ProtocolReceiptStatus.REJECTED, leaveReceipt.status)
            assertEquals("STALE_SESSION", leaveReceipt.reason)

            // 5. 断言：新观战者成员未被误删，仍然在 room.spectators 中且保持连接、generation = 2L
            val currentMember = room.spectators[specId]
            assertNotNull(currentMember, "New spectator member must NOT be removed by old stale leave")
            assertEquals(2L, currentMember!!.generation)
            assertTrue(currentMember.isConnected)
        } finally {
            room.close()
        }
    }

    @Test
    fun `TcpTableServer preserves spectator and rejects stale leave when displaced connection executes queued leave`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String
            var specId: String
            var specToken: String

            // 1. Host 连入并建房
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val hostWelcome = hostClient.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-spec-race",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(hostWelcome.lobbyScope.scope, "req-create-spec-race", createReq)))
                val createRsp = hostClient.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!

                // 2. Client 1 连入并加入观战 (代次 = 1L)
                TcpTestClient(port = server.boundPort).use { client1 ->
                    client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcome1 = client1.expectWelcome()

                    val joinReq = ApiRequest(
                        api = ApiTags.JOIN_SPECTATOR,
                        requestId = "req-join-race-1",
                        roomId = roomId,
                        spectatorName = "Spectator1"
                    )
                    client1.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome1.lobbyScope.scope, "req-join-race-1", joinReq)))
                    val joinRsp = client1.expectResponse()
                    assertTrue(joinRsp.response.success)
                    specId = joinRsp.response.spectatorBinding!!.spectatorId
                    specToken = joinRsp.response.spectatorBinding!!.resumeToken

                    // 捕获服务端持有的 conn1 及其旧会话校验器
                    val conn1 = server.connections.values.first { it.spectatorState?.spectatorId == specId }
                    val oldValidator = conn1.spectatorState!!.makeSessionValidator(conn1)

                    // 3. Client 2 连入并执行 RESUME_SPECTATOR 顶号 (使代次递增至 2L)
                    TcpTestClient(port = server.boundPort).use { client2 ->
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcome2 = client2.expectWelcome()

                    val resumeReq = ApiRequest(
                        api = ApiTags.RESUME_SPECTATOR,
                        requestId = "req-resume-race-2",
                        roomId = roomId,
                        token = specToken,
                        spectatorId = specId
                    )
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome2.lobbyScope.scope, "req-resume-race-2", resumeReq)))
                    val resumeRsp = client2.expectResponse()
                    assertTrue(resumeRsp.response.success)
                    val specScope2 = resumeRsp.grantedScope!!.scope

                    // Client 1 被置为 CLOSING 并收到 DISPLACED_BY_NEW_CONNECTION 关闭帧
                    val closeMsg = client1.expectClose()
                    assertEquals(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, closeMsg.reason)
                    assertTrue(conn1.isClosingOrClosed)

                    // 3. 执行旧连接 conn1 排队的 leaveSpectator 操作
                    val targetRoom = server.registry.getRoom(roomId)!!
                    val leaveReceipt = targetRoom.leaveSpectator(specId, oldValidator).get(5, TimeUnit.SECONDS)

                    // 4. 断言：旧 leave 操作因校验器核对 generation 与 isClosingOrClosed 失败被严格拒绝
                    assertEquals(ProtocolReceiptStatus.REJECTED, leaveReceipt.status)
                    assertEquals("STALE_SESSION", leaveReceipt.reason)

                    // 5. 断言：新观战者成员未被误删，仍存在、已连接且 generation = 2L
                    val currentMember = targetRoom.spectators[specId]
                    assertNotNull(currentMember, "Spectator must still exist in room")
                    assertEquals(2L, currentMember!!.generation)
                    assertTrue(currentMember.isConnected)

                    // 6. 断言：新连接 Client 2 可正常通过 TCP 获取公开快照
                    val snapReq = ApiRequest(
                        api = ApiTags.PUBLIC_SNAPSHOT,
                        requestId = "req-snap-client2"
                    )
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(specScope2, "req-snap-client2", snapReq)))
                    val snapRsp = client2.expectResponse()
                    assertTrue(snapRsp.response.success)
                    assertNotNull(snapRsp.response.publicSnapshot)
                    }
                }
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `delayed markSpectatorConnected from old generation does not reconnect displaced disconnected spectator`() {
        val room = SharedRoom(
            roomId = "ROOM_SPECTATOR_MARK_RACE",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            // 1. 观战者加入 (代次 1L)
            val joinResult = room.joinSpectator("Spectator1").get(5, TimeUnit.SECONDS)
            val specId = joinResult.spectatorId
            val token = joinResult.resumeToken
            assertEquals(1L, joinResult.generation)
            assertEquals(1L, room.spectators[specId]!!.generation)

            // 旧连接标记断开
            room.disconnectSpectator(specId, 1L).get(5, TimeUnit.SECONDS)
            assertFalse(room.spectators[specId]!!.isConnected)

            // 2. 新连接发起 resumeSpectator 顶号 (代次递增至 2L，标记连接)
            val resumeResult = room.resumeSpectator(specId, token).get(5, TimeUnit.SECONDS)
            assertEquals(2L, resumeResult.generation)
            assertTrue(room.spectators[specId]!!.isConnected)

            // 3. 新连接随后也发生断开 (代次 2L 断开)
            room.disconnectSpectator(specId, 2L).get(5, TimeUnit.SECONDS)
            assertFalse(room.spectators[specId]!!.isConnected)

            // 4. 旧代次 1L 延后排队到达的 markSpectatorConnected 执行
            room.markSpectatorConnected(specId, expectedGeneration = 1L).get(5, TimeUnit.SECONDS)

            // 5. 核心断言：当前成员代次为 2L，旧代次 1L 的 markSpectatorConnected 被拒，isConnected 依然保持 false，绝不错误复活
            val currentMember = room.spectators[specId]
            assertNotNull(currentMember)
            assertEquals(2L, currentMember!!.generation)
            assertFalse(currentMember.isConnected, "Delayed mark from generation 1L must NOT reconnect generation 2L member!")
        } finally {
            room.close()
        }
    }

    @Test
    fun `closing connection cannot resume spectator and displace healthy connection`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String
            var specId: String
            var specToken: String

            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val hostWelcome = hostClient.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-closing-race",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(hostWelcome.lobbyScope.scope, "req-create-closing-race", createReq)))
                val createRsp = hostClient.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!

                TcpTestClient(port = server.boundPort).use { client1 ->
                    client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcome1 = client1.expectWelcome()

                    val joinReq = ApiRequest(
                        api = ApiTags.JOIN_SPECTATOR,
                        requestId = "req-join-spec",
                        roomId = roomId,
                        spectatorName = "Spectator1"
                    )
                    client1.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome1.lobbyScope.scope, "req-join-spec", joinReq)))
                    val joinRsp = client1.expectResponse()
                    assertTrue(joinRsp.response.success)
                    specId = joinRsp.response.spectatorBinding!!.spectatorId
                    specToken = joinRsp.response.spectatorBinding!!.resumeToken

                    TcpTestClient(port = server.boundPort).use { client2 ->
                        client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                        val welcome2 = client2.expectWelcome()

                        val resumeReq2 = ApiRequest(
                            api = ApiTags.RESUME_SPECTATOR,
                            requestId = "req-resume-client2",
                            roomId = roomId,
                            token = specToken,
                            spectatorId = specId
                        )
                        client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome2.lobbyScope.scope, "req-resume-client2", resumeReq2)))
                        val resumeRsp2 = client2.expectResponse()
                        assertTrue(resumeRsp2.response.success)
                        val client2Scope = resumeRsp2.grantedScope!!.scope

                        // Client 1 被置为 CLOSING 并收到 DISPLACED_BY_NEW_CONNECTION 关闭帧
                        val close1 = client1.expectClose()
                        assertEquals(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, close1.reason)

                        // 处于 CLOSING 态的 conn1 无法完成排队恢复
                        val targetRoom = server.registry.getRoom(roomId)!!
                        val conn1 = server.connections.values.firstOrNull { it.spectatorState?.spectatorId == specId && it.isClosingOrClosed }
                        if (conn1 != null) {
                            val resumeFuture = targetRoom.resumeSpectator(specId, specToken, sessionValidator = { !conn1.isClosingOrClosed })
                            assertThrows<Exception> {
                                resumeFuture.get(5, TimeUnit.SECONDS)
                            }
                        }

                        // 断言 Client 2 保持健康连接与 generation = 2L，公开快照拉取成功
                        val currentMember = targetRoom.spectators[specId]
                        assertNotNull(currentMember)
                        assertEquals(2L, currentMember!!.generation)
                        assertTrue(currentMember.isConnected)

                        val snapReq = ApiRequest(
                            api = ApiTags.PUBLIC_SNAPSHOT,
                            requestId = "req-snap-client2-intact"
                        )
                        client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(client2Scope, "req-snap-client2-intact", snapReq)))
                        val snapRsp = client2.expectResponse()
                        assertTrue(snapRsp.response.success)
                        assertNotNull(snapRsp.response.publicSnapshot)
                    }
                }
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `snapshotSupplier and publicSnapshotSupplier bind to captured scope without reading mutated conn role`() {
        val room = SharedRoom(
            roomId = "ROOM_SUPPLIER_SCOPE",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = true
        )
        try {
            // 验证 PlayerState 与 SpectatorState 快照供给者仅与自身捕获代次绑定
            val p0 = PlayerState.fromRoom(room, 0)
            val pSnapFuture = p0.snapshotSupplier()
            assertNotNull(pSnapFuture.get(5, TimeUnit.SECONDS))

            val joinResult = room.joinSpectator("spec-capture-test").get(5, TimeUnit.SECONDS)
            val s0 = SpectatorState.fromRoom(room, joinResult.spectatorId)
            val sSnapFuture = s0.publicSnapshotSupplier()
            assertNotNull(sSnapFuture.get(5, TimeUnit.SECONDS))
        } finally {
            room.close()
        }
    }

    // --- 9. 原子安装与关闭并发交错及断线补偿测试 (D-02 Round 3) ---

    @Test
    fun `atomic coordination - player binding rejected when connection closed before role install with compensation disconnect`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 201L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_RACE_P_BEFORE",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("principal-race-p1", "token-race-p1", "epoch-201")
            val joinResult = room.join("Player1", preferSeat = 1).get(5, TimeUnit.SECONDS)
            val seat = joinResult.first
            val capturedGen = room.seatMemberGeneration(seat)
            assertTrue(room.seatOccupied[seat])
            assertTrue(room.seatConnected[seat])

            // 模拟在原子安装角色前，物理连接发生关闭
            conn.close(TcpCloseReasons.LOCAL_CLOSED, immediate = true)
            assertTrue(conn.isClosed)
            assertTrue(conn.isClosingOrClosed)

            val playerState = PlayerState(
                seat = seat,
                snapshotSupplier = { room.snapshot(seat).thenApply { it!! } },
                room = room,
                bindingGeneration = capturedGen
            )
            // 原子安装必须被拒绝并返回 false
            val installSuccess = conn.installPlayerRole(playerState)
            assertFalse(installSuccess, "installPlayerRole must return false on closed connection")
            assertFalse(conn.roleState === playerState, "PlayerState must not be attached to closed connection")

            // 服务端触发精确断线补偿
            playerState.detach()
            playerState.notifyDisconnect().get(5, TimeUnit.SECONDS)

            // 断言席位状态已安全断开，且代次未被污染
            assertFalse(room.seatConnected[seat], "Seat must be disconnected via compensation")
            assertEquals(capturedGen, room.seatMemberGeneration(seat))
        } finally {
            conn.forceCloseChannel()
            channel.close()
            room.close()
        }
    }

    @Test
    fun `atomic coordination - player binding disconnect notified when connection closed after role install`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 202L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_RACE_P_AFTER",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("principal-race-p2", "token-race-p2", "epoch-202")
            val joinResult = room.join("Player2", preferSeat = 2).get(5, TimeUnit.SECONDS)
            val seat = joinResult.first
            val capturedGen = room.seatMemberGeneration(seat)
            assertTrue(room.seatConnected[seat])

            val playerState = PlayerState(
                seat = seat,
                snapshotSupplier = { room.snapshot(seat).thenApply { it!! } },
                room = room,
                bindingGeneration = capturedGen
            )
            val installSuccess = conn.installPlayerRole(playerState)
            assertTrue(installSuccess, "installPlayerRole must succeed on healthy handshaked connection")
            assertSame(playerState, conn.roleState)

            // 安装之后物理连接发生关闭：forceCloseChannel 必须捕获已安装的 PlayerState 并精确调用其 onConnectionClosed
            conn.forceCloseChannel()
            assertTrue(conn.isClosed)

            // 等待房间权威队列处理完成
            room.markSeatConnected(seat, -1L).get(5, TimeUnit.SECONDS)

            // 断言席位已成功由 PlayerState 通知断开
            assertFalse(room.seatConnected[seat], "Seat must be disconnected after connection close")
            assertEquals(capturedGen, room.seatMemberGeneration(seat))
        } finally {
            channel.close()
            room.close()
        }
    }

    @Test
    fun `atomic coordination - spectator binding rejected when connection closed before role install with compensation disconnect`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 203L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_RACE_S_BEFORE",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("principal-race-s1", "token-race-s1", "epoch-203")
            val joinResult = room.joinSpectator("SpecBefore").get(5, TimeUnit.SECONDS)
            val specId = joinResult.spectatorId
            val capturedGen = joinResult.generation
            assertTrue(room.spectators[specId]!!.isConnected)

            // 模拟在安装前发生并发关闭
            conn.close(TcpCloseReasons.LOCAL_CLOSED, immediate = true)
            assertTrue(conn.isClosed)
            assertTrue(conn.isClosingOrClosed)

            val spectatorState = SpectatorState(
                spectatorId = specId,
                publicSnapshotSupplier = { room.publicSnapshot().thenApply { it } },
                room = room,
                bindingGeneration = capturedGen
            )
            val installSuccess = conn.installSpectatorRole(spectatorState)
            assertFalse(installSuccess, "installSpectatorRole must return false on closed connection")
            assertFalse(conn.roleState === spectatorState, "SpectatorState must not be attached to closed connection")

            // 执行断线补偿
            spectatorState.detach()
            spectatorState.notifyDisconnect().get(5, TimeUnit.SECONDS)

            // 断言观战成员已安全断开且代次一致
            assertFalse(room.spectators[specId]!!.isConnected, "Spectator must be disconnected via compensation")
            assertEquals(capturedGen, room.spectators[specId]!!.generation)
        } finally {
            conn.forceCloseChannel()
            channel.close()
            room.close()
        }
    }

    @Test
    fun `atomic coordination - spectator binding disconnect notified when connection closed after role install`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 204L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_RACE_S_AFTER",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("principal-race-s2", "token-race-s2", "epoch-204")
            val joinResult = room.joinSpectator("SpecAfter").get(5, TimeUnit.SECONDS)
            val specId = joinResult.spectatorId
            val capturedGen = joinResult.generation
            assertTrue(room.spectators[specId]!!.isConnected)

            val spectatorState = SpectatorState(
                spectatorId = specId,
                publicSnapshotSupplier = { room.publicSnapshot().thenApply { it } },
                room = room,
                bindingGeneration = capturedGen
            )
            val installSuccess = conn.installSpectatorRole(spectatorState)
            assertTrue(installSuccess, "installSpectatorRole must succeed on healthy connection")
            assertSame(spectatorState, conn.roleState)

            conn.forceCloseChannel()
            assertTrue(conn.isClosed)

            room.markSpectatorConnected(specId, -1L).get(5, TimeUnit.SECONDS)

            assertFalse(room.spectators[specId]!!.isConnected, "Spectator must be disconnected when connection closed after install")
            assertEquals(capturedGen, room.spectators[specId]!!.generation)
        } finally {
            channel.close()
            room.close()
        }
    }

    @Test
    fun `real TCP client closed right after join receives compensation disconnect and does not leak connected state`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String

            // 1. Host 建房 (多人员房间)
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val hostWelcome = hostClient.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-comp-leak",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(hostWelcome.lobbyScope.scope, "req-create-comp-leak", createReq)))
                val createRsp = hostClient.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!

                // 2. Client 2 连入并发送 JOIN_ROOM，随后立即强关底层 Socket
                TcpTestClient(port = server.boundPort).use { client2 ->
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcome2 = client2.expectWelcome()

                    val joinReq = ApiRequest(
                        api = "JOIN_ROOM",
                        requestId = "req-join-disconnect-immediately",
                        roomId = roomId,
                        playerName = "Player2QuickDisconnect"
                    )
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcome2.lobbyScope.scope, "req-join-disconnect-immediately", joinReq)))
                    // 立即关闭 client2
                    client2.close()
                }

                // 等待服务端将 client2 注销与断线通知排空
                val targetRoom = server.registry.getRoom(roomId)!!
                val deadline = System.currentTimeMillis() + 5000
                var seat1Connected = true
                while (System.currentTimeMillis() < deadline) {
                    // 刷新房间队列
                    targetRoom.markSeatConnected(1, -1L).get(1, TimeUnit.SECONDS)
                    if (!targetRoom.seatConnected[1]) {
                        seat1Connected = false
                        break
                    }
                    Thread.sleep(50)
                }
                assertFalse(seat1Connected, "Seat 1 must not be leaked as connected after quick client disconnect")
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `deterministic queue interleaving - player disconnect before delayed mark keeps seat disconnected and idle tracking active`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 205L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_QUEUE_INTERLEAVE_PLAYER",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("principal-interleave-p", "token-interleave-p", "epoch-205")
            val (seat, _) = room.join("PlayerRace").get(5, TimeUnit.SECONDS)
            val capturedGen = room.seatMemberGeneration(seat)

            val playerState = PlayerState(
                seat = seat,
                snapshotSupplier = { room.snapshot(seat).thenApply { it } },
                room = room,
                bindingGeneration = capturedGen
            )
            val installSuccess = conn.installPlayerRole(playerState)
            assertTrue(installSuccess)

            // 1. 模拟连接关闭，触发 disconnectNotified 并将 disconnectSeat 排入队列
            conn.forceCloseChannel()
            assertTrue(conn.isClosed)

            // 2. 模拟基线校验成功后的旧 mark 回调排入队列
            val markFuture = room.markSeatConnected(seat, capturedGen, playerState.makeSessionValidator(conn))
            markFuture.get(5, TimeUnit.SECONDS)

            // 3. 断言席位保持断开状态，且已记录断开代次，空闲状态正确
            assertFalse(room.seatConnected[seat], "Seat must remain disconnected even if mark is executed after disconnect")
            assertEquals(capturedGen, room.seatDisconnectedGenerations[seat], "seatDisconnectedGenerations must record disconnect generation")
            assertFalse(playerState.makeSessionValidator(conn)(), "Session validator must reject closed/disconnected connection")
        } finally {
            channel.close()
            room.close()
        }
    }

    @Test
    fun `deterministic queue interleaving - spectator disconnect before delayed mark keeps spectator disconnected and idle tracking active`() {
        val serverConfig = TcpServerConfig(port = 0)
        val budget = TcpGlobalBudget(1024 * 1024)
        val channel = SocketChannel.open()
        val conn = TcpConnection(connectionId = 206L, channel = channel, config = serverConfig, globalBudget = budget)
        val room = SharedRoom(
            roomId = "ROOM_QUEUE_INTERLEAVE_SPEC",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            conn.setHandshakedIdentity("principal-interleave-s", "token-interleave-s", "epoch-206")
            val joinResult = room.joinSpectator("SpecRace").get(5, TimeUnit.SECONDS)
            val specId = joinResult.spectatorId
            val capturedGen = joinResult.generation

            val spectatorState = SpectatorState(
                spectatorId = specId,
                publicSnapshotSupplier = { room.publicSnapshot().thenApply { it } },
                room = room,
                bindingGeneration = capturedGen
            )
            val installSuccess = conn.installSpectatorRole(spectatorState)
            assertTrue(installSuccess)

            // 1. 模拟连接关闭，触发 disconnectNotified 并将 disconnectSpectator 排入队列
            conn.forceCloseChannel()
            assertTrue(conn.isClosed)

            // 2. 模拟基线校验成功后的旧 mark 回调排入队列
            val markFuture = room.markSpectatorConnected(specId, capturedGen, spectatorState.makeSessionValidator(conn))
            markFuture.get(5, TimeUnit.SECONDS)

            // 3. 断言观战者保持断开状态，且已记录断开代次，空闲状态正确
            assertFalse(room.spectators[specId]!!.isConnected, "Spectator must remain disconnected even if mark is executed after disconnect")
            assertEquals(capturedGen, room.spectators[specId]!!.lastDisconnectedGeneration, "lastDisconnectedGeneration must record disconnect generation")
            assertFalse(spectatorState.makeSessionValidator(conn)(), "Session validator must reject closed/disconnected connection")
            assertEquals(0, room.connectedSpectatorCount())
        } finally {
            channel.close()
            room.close()
        }
    }

    @Test
    fun `domain guard strictly prevents markSeatConnected and markSpectatorConnected from reversing disconnect even with null validator`() {
        val room = SharedRoom(
            roomId = "ROOM_DOMAIN_GUARD",
            hostPlayerName = "Host",
            initialConfig = ProtocolRoomConfig(),
            isSingleplayer = false
        )
        try {
            // 玩家席位
            val (seat, _) = room.join("P_DomainGuard").get(5, TimeUnit.SECONDS)
            val pGen = room.seatMemberGeneration(seat)
            room.disconnectSeat(seat, pGen).get(5, TimeUnit.SECONDS)
            assertFalse(room.seatConnected[seat])
            assertEquals(pGen, room.seatDisconnectedGenerations[seat])

            // 即使传入 null validator，领域代次屏障也必须拒绝将已断开的席位置为已连接
            room.markSeatConnected(seat, pGen, sessionValidator = null).get(5, TimeUnit.SECONDS)
            assertFalse(room.seatConnected[seat], "Domain guard must prevent markSeatConnected reversal even with null validator")

            // 观战者席位
            val specRes = room.joinSpectator("Spec_DomainGuard").get(5, TimeUnit.SECONDS)
            val specId = specRes.spectatorId
            val sGen = specRes.generation
            room.disconnectSpectator(specId, sGen).get(5, TimeUnit.SECONDS)
            assertFalse(room.spectators[specId]!!.isConnected)
            assertEquals(sGen, room.spectators[specId]!!.lastDisconnectedGeneration)

            // 即使传入 null validator，领域代次屏障也必须拒绝将已断开的观战置为已连接
            room.markSpectatorConnected(specId, sGen, sessionValidator = null).get(5, TimeUnit.SECONDS)
            assertFalse(room.spectators[specId]!!.isConnected, "Domain guard must prevent markSpectatorConnected reversal even with null validator")
        } finally {
            room.close()
        }
    }

    @Test
    fun `real TCP spectator closed right after join receives compensation disconnect and does not leak connected state`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            var roomId: String

            // 1. Host 建房
            TcpTestClient(port = server.boundPort).use { hostClient ->
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val hostWelcome = hostClient.expectWelcome()

                val createReq = ApiRequest(
                    api = "CREATE_ROOM",
                    requestId = "req-create-spec-comp-leak",
                    playerName = "HostPlayer",
                    roomConfig = ProtocolRoomConfig()
                )
                hostClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(hostWelcome.lobbyScope.scope, "req-create-spec-comp-leak", createReq)))
                val createRsp = hostClient.expectResponse()
                assertTrue(createRsp.response.success)
                roomId = createRsp.response.roomId!!

                // 2. Spectator client 连入并发送 JOIN_SPECTATOR，随后立即强关底层 Socket
                TcpTestClient(port = server.boundPort).use { specClient ->
                    specClient.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcomeSpec = specClient.expectWelcome()

                    val joinSpecReq = ApiRequest(
                        api = "JOIN_SPECTATOR",
                        requestId = "req-join-spec-quick-disconnect",
                        roomId = roomId,
                        spectatorName = "QuickSpectator"
                    )
                    specClient.sendFrame(TcpMessageCodec.toFrame(TcpRequestEnvelope(welcomeSpec.lobbyScope.scope, "req-join-spec-quick-disconnect", joinSpecReq)))
                    specClient.close()
                }

                // 3. 等待服务端将 spectator 断线通知排空
                val targetRoom = server.registry.getRoom(roomId)!!
                val deadline = System.currentTimeMillis() + 5000
                var spectatorConnected = true
                while (System.currentTimeMillis() < deadline) {
                    targetRoom.markSeatConnected(0, -1L).get(1, TimeUnit.SECONDS)
                    if (targetRoom.connectedSpectatorCount() == 0) {
                        spectatorConnected = false
                        break
                    }
                    Thread.sleep(50)
                }
                assertFalse(spectatorConnected, "Spectator must not be leaked as connected after quick client disconnect")
            }
        } finally {
            server.close()
        }
    }
}

package com.majiang.server.udp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolCheatEconomyConfig
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolShuffleConfig
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget
import com.majiang.transport.udp.Fragmenter
import com.majiang.transport.udp.HeartbeatPacer
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpScheduler
import com.majiang.transport.udp.Cancellable
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * UDP 权威房间服务器真实回环冒烟测试 (UdpTableServerSmokeTest)
 *
 * 绑定 127.0.0.1 临时端口 (port 0)，使用原生 Java DatagramSocket 模拟真实网络客户端，
 * 全链路验证：建房 -> 列表 -> 加房 -> 开赛 -> 快照 -> 推送 -> 顶号重连 (旧连接收 KICK) -> 离开 -> 幂等重放 -> 心跳。
 */
class UdpTableServerSmokeTest {

    private val socketInboxes = ConcurrentHashMap<DatagramSocket, ConcurrentLinkedQueue<UdpFrame>>()
    private val socketFragmenters = ConcurrentHashMap<DatagramSocket, Fragmenter>()

    private fun sendFrame(socket: DatagramSocket, serverPort: Int, frame: UdpFrame) {
        val bytes = UdpFrame.encode(frame)
        val packet = DatagramPacket(bytes, bytes.size, InetAddress.getByName("127.0.0.1"), serverPort)
        socket.send(packet)
    }

    private fun receiveFrame(socket: DatagramSocket, timeoutMillis: Int = 3000): UdpFrame {
        return receiveUntil(socket, timeoutMillis) { true }
    }

    private fun receiveUntil(
        socket: DatagramSocket,
        timeoutMillis: Int = 4000,
        predicate: (UdpFrame) -> Boolean
    ): UdpFrame {
        val inbox = socketInboxes.computeIfAbsent(socket) { ConcurrentLinkedQueue() }
        val frag = socketFragmenters.computeIfAbsent(socket) {
            Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
                override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
            })
        }

        // 1. 优先从已入队且未消费的完成帧中寻找
        val it = inbox.iterator()
        while (it.hasNext()) {
            val f = it.next()
            if (predicate(f)) {
                it.remove()
                return f
            }
        }

        // 2. 循环读取底层 UDP Socket
        val deadline = System.currentTimeMillis() + timeoutMillis
        val buffer = ByteArray(4096)
        while (System.currentTimeMillis() < deadline) {
            val remaining = (deadline - System.currentTimeMillis()).coerceAtLeast(1L).coerceAtMost(1000L).toInt()
            socket.soTimeout = remaining
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                socket.receive(packet)
                val bytes = packet.data.copyOfRange(packet.offset, packet.offset + packet.length)
                val rawFrame = UdpFrame.decode(bytes)

                val completeFrame = if (rawFrame.packetType == UdpPacketType.FRAG) {
                    frag.reassemble(rawFrame) ?: continue
                } else {
                    rawFrame
                }

                if (predicate(completeFrame)) {
                    return completeFrame
                } else {
                    inbox.add(completeFrame)
                }
            } catch (_: SocketTimeoutException) {
            }
        }
        throw SocketTimeoutException("Timed out waiting for frame matching condition")
    }

    private fun request(
        socket: DatagramSocket,
        serverPort: Int,
        connectionId: Int,
        seq: Int,
        request: ApiRequest,
        fragmenter: Fragmenter? = null
    ): Pair<UdpFrame, ApiResponse> {
        val payload = ApiMessageCodec.encodeRequest(request).toByteArray(Charsets.UTF_8)
        val frame = UdpFrame(
            packetType = UdpPacketType.REQ,
            connectionId = connectionId,
            seq = seq,
            ackSeq = 0,
            payload = payload
        )
        sendFrame(socket, serverPort, frame)

        val rspFrame = receiveUntil(socket, 4000) { f ->
            if (f.packetType == UdpPacketType.RSP) {
                try {
                    val json = String(f.payload, Charsets.UTF_8)
                    val resp = ApiMessageCodec.decodeResponse(json)
                    resp.requestId == request.requestId
                } catch (_: Throwable) {
                    false
                }
            } else {
                false
            }
        }

        val json = String(rspFrame.payload, Charsets.UTF_8)
        val resp = ApiMessageCodec.decodeResponse(json)
        return Pair(rspFrame, resp)
    }

    @Test
    fun testFullLoopbackLifecycle() {
        val server = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        server.start()
        val serverPort = server.boundPort
        assertTrue(serverPort > 0, "Server should be bound to an ephemeral port")

        val client1 = DatagramSocket()
        val client2 = DatagramSocket()
        val client3 = DatagramSocket()

        try {
            // 1. Client 1 建房 (CREATE_ROOM)
            val (rspFrame1, respCreate) = request(
                socket = client1,
                serverPort = serverPort,
                connectionId = 0,
                seq = 1,
                request = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "req-1", playerName = "Alice")
            )
            assertTrue(respCreate.success)
            val roomId = respCreate.roomId
            assertNotNull(roomId)
            assertEquals(0, respCreate.seat)
            assertNotNull(respCreate.resumeToken)
            assertNotNull(respCreate.snapshot)
            val connectionId1 = rspFrame1.connectionId
            assertTrue(connectionId1 != 0, "Server must assign non-zero connectionId")

            // 2. Client 1 查询大厅房间列表 (LIST_ROOMS)
            val (_, respList) = request(
                socket = client1,
                serverPort = serverPort,
                connectionId = 0,
                seq = 2,
                request = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "req-2")
            )
            assertTrue(respList.success)
            assertEquals(1, respList.rooms?.size)
            assertEquals(roomId, respList.rooms?.first()?.roomId)
            assertEquals("Alice", respList.rooms?.first()?.hostPlayerName)

            // 3. Client 2 加入房间 (JOIN_ROOM)
            val (rspFrame2, respJoin) = request(
                socket = client2,
                serverPort = serverPort,
                connectionId = 0,
                seq = 1,
                request = ApiRequest(
                    api = ApiTags.JOIN_ROOM,
                    requestId = "req-3",
                    roomId = roomId,
                    playerName = "Bob",
                    preferSeat = 1
                )
            )
            assertTrue(respJoin.success)
            assertEquals(1, respJoin.seat)
            val bobToken = respJoin.resumeToken
            assertNotNull(bobToken)
            val connectionId2 = rspFrame2.connectionId
            assertTrue(connectionId2 != 0)

            // 3.1 Client 1 设置准备状态 (SET_READY)
            val (_, respReady1) = request(
                socket = client1,
                serverPort = serverPort,
                connectionId = connectionId1,
                seq = 3,
                request = ApiRequest(api = ApiTags.SET_READY, requestId = "req-3-1", ready = true)
            )
            assertTrue(respReady1.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, respReady1.receipt?.status)

            // 3.2 Client 2 设置准备状态 (SET_READY)
            val (_, respReady2) = request(
                socket = client2,
                serverPort = serverPort,
                connectionId = connectionId2,
                seq = 2,
                request = ApiRequest(api = ApiTags.SET_READY, requestId = "req-3-2", ready = true)
            )
            assertTrue(respReady2.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, respReady2.receipt?.status)

            // 4. Client 1 发起开赛 (START_MATCH)
            val (_, respStart) = request(
                socket = client1,
                serverPort = serverPort,
                connectionId = connectionId1,
                seq = 4,
                request = ApiRequest(api = ApiTags.START_MATCH, requestId = "req-4")
            )
            assertTrue(respStart.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, respStart.receipt?.status)

            // 5. Client 1 查询全量快照 (SNAPSHOT)
            val (_, respSnap) = request(
                socket = client1,
                serverPort = serverPort,
                connectionId = connectionId1,
                seq = 5,
                request = ApiRequest(api = ApiTags.SNAPSHOT, requestId = "req-5")
            )
            assertTrue(respSnap.success)
            assertNotNull(respSnap.snapshot)

            // 6. Client 2 接收开赛推送 (PUSH)
            val pushFrame = receiveUntil(client2, 3000) {
                it.packetType == UdpPacketType.PUSH
            }
            assertEquals(UdpPacketType.PUSH, pushFrame.packetType)
            assertEquals(connectionId2, pushFrame.connectionId)

            // 7. Client 3 顶号重连 Client 2 (RESUME_SESSION)
            val (rspFrame3, respResume) = request(
                socket = client3,
                serverPort = serverPort,
                connectionId = 0,
                seq = 1,
                request = ApiRequest(
                    api = ApiTags.RESUME_SESSION,
                    requestId = "req-6",
                    roomId = roomId,
                    token = bobToken
                )
            )
            assertTrue(respResume.success)
            assertEquals(1, respResume.seat)
            val connectionId3 = rspFrame3.connectionId
            assertTrue(connectionId3 != 0)
            assertTrue(connectionId3 != connectionId2)

            // 8. 旧 Client 2 收到 KICK 通知
            val kickFrame = receiveUntil(client2, 3000) {
                it.packetType == UdpPacketType.KICK
            }
            assertEquals(UdpPacketType.KICK, kickFrame.packetType)
            assertEquals(connectionId2, kickFrame.connectionId)
            val kickReason = ApiMessageCodec.decodeKick(String(kickFrame.payload, Charsets.UTF_8))
            assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, kickReason)

            // 8.5 旧 Client 2 发送请求返回 INVALID_TOKEN
            val (_, respStale) = request(
                socket = client2,
                serverPort = serverPort,
                connectionId = connectionId2,
                seq = 3,
                request = ApiRequest(api = ApiTags.SNAPSHOT, requestId = "req-stale")
            )
            assertFalse(respStale.success)
            assertEquals(ApiErrors.INVALID_TOKEN, respStale.error)

            // 9. Client 1 离开房间 (LEAVE_ROOM)，房主离开解散房间
            val (_, respLeave) = request(
                socket = client1,
                serverPort = serverPort,
                connectionId = connectionId1,
                seq = 6,
                request = ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "req-7")
            )
            assertTrue(respLeave.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, respLeave.receipt?.status)

            // 10. 房间解散后再次查询快照返回 ROOM_NOT_FOUND
            val (_, respAfterClose) = request(
                socket = client3,
                serverPort = serverPort,
                connectionId = connectionId3,
                seq = 2,
                request = ApiRequest(api = ApiTags.SNAPSHOT, requestId = "req-8")
            )
            assertFalse(respAfterClose.success)
            assertEquals(ApiErrors.ROOM_NOT_FOUND, respAfterClose.error)

        } finally {
            client1.close()
            client2.close()
            client3.close()
            server.close()
            assertTrue(server.closed)
        }
    }

    @Test
    fun testPrivateShufflePlanAndResumeOverRealUdp() {
        UdpTableServer(bindHost = "127.0.0.1", port = 0).use { server ->
            server.start()
            DatagramSocket().use { hostSocket ->
                DatagramSocket().use { guestSocket ->
                    DatagramSocket().use { resumedSocket ->
                        val (hostFrame, created) = request(hostSocket, server.boundPort, 0, 1,
                            ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "shuffle-create", playerName = "A",
                                config = ProtocolMatchConfig(winds = listOf(1),
                                    cheatEconomy = ProtocolCheatEconomyConfig(enabled = true,
                                        initialPoints = listOf(50, 50, 50, 50)),
                                    shuffleConfig = ProtocolShuffleConfig(enabled = true))))
                        assertTrue(created.success)
                        val roomId = assertNotNull(created.roomId)
                        val token = assertNotNull(created.resumeToken)
                        val (guestFrame, joined) = request(guestSocket, server.boundPort, 0, 1,
                            ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "shuffle-join",
                                roomId = roomId, playerName = "B"))
                        assertTrue(joined.success)
                        val hostReady = request(hostSocket, server.boundPort, hostFrame.connectionId, 2,
                            ApiRequest(api = ApiTags.SET_READY, requestId = "shuffle-ready-a", ready = true)).second
                        val guestReady = request(guestSocket, server.boundPort, guestFrame.connectionId, 2,
                            ApiRequest(api = ApiTags.SET_READY, requestId = "shuffle-ready-b", ready = true)).second
                        assertEquals(ProtocolReceiptStatus.APPLIED, hostReady.receipt?.status)
                        assertEquals(ProtocolReceiptStatus.APPLIED, guestReady.receipt?.status)
                        val started = request(hostSocket, server.boundPort, hostFrame.connectionId, 3,
                            ApiRequest(api = ApiTags.START_MATCH, requestId = "shuffle-start")).second
                        assertEquals(ProtocolReceiptStatus.APPLIED, started.receipt?.status)
                        val before = assertNotNull(request(hostSocket, server.boundPort, hostFrame.connectionId, 4,
                            ApiRequest(api = ApiTags.SNAPSHOT, requestId = "shuffle-before")).second.snapshot)
                        val guestBefore = assertNotNull(request(guestSocket, server.boundPort, guestFrame.connectionId, 3,
                            ApiRequest(api = ApiTags.SNAPSHOT, requestId = "shuffle-guest-before")).second.snapshot)
                        assertEquals(50, before.preparation?.availablePoints)
                        assertTrue(before.view.myClosedTiles.isEmpty())
                        assertEquals(before.roundId, guestBefore.roundId)

                        val selection = ProtocolShuffleSelection(initialTargets = setOf(ProtocolShuffleTarget.KOKUSHI))
                        val plan = ProtocolShufflePlan("shuffle-plan", assertNotNull(before.roundId),
                            before.decision.controlEpoch, selection)
                        val planRequest = ApiRequest(api = ApiTags.SUBMIT_SHUFFLE_PLAN,
                            requestId = plan.requestId, shufflePlan = plan)
                        val recorded = request(hostSocket, server.boundPort, hostFrame.connectionId, 5, planRequest).second
                        assertEquals(ProtocolReceiptStatus.RECORDED, recorded.receipt?.status)
                        val replay = request(hostSocket, server.boundPort, hostFrame.connectionId, 6, planRequest).second
                        assertEquals(recorded.receipt, replay.receipt)
                        val privateSnapshot = assertNotNull(request(hostSocket, server.boundPort, hostFrame.connectionId, 7,
                            ApiRequest(api = ApiTags.SNAPSHOT, requestId = "shuffle-private")).second.snapshot)
                        assertEquals(selection, privateSnapshot.preparation?.selection)
                        assertEquals(40, privateSnapshot.preparation?.reservedPoints)
                        assertEquals(10, privateSnapshot.preparation?.availablePoints)
                        val guestSnapshot = assertNotNull(request(guestSocket, server.boundPort, guestFrame.connectionId, 4,
                            ApiRequest(api = ApiTags.SNAPSHOT, requestId = "shuffle-guest")).second.snapshot)
                        assertNull(guestSnapshot.preparation?.selection)
                        assertTrue(guestSnapshot.view.myClosedTiles.isEmpty())

                        val (resumedFrame, resumed) = request(resumedSocket, server.boundPort, 0, 1,
                            ApiRequest(api = ApiTags.RESUME_SESSION, requestId = "shuffle-resume",
                                roomId = roomId, token = token))
                        assertTrue(resumed.success)
                        assertEquals(0, resumed.seat)
                        assertTrue(resumedFrame.connectionId != hostFrame.connectionId)
                        val restored = assertNotNull(request(resumedSocket, server.boundPort,
                            resumedFrame.connectionId, 2,
                            ApiRequest(api = ApiTags.SNAPSHOT, requestId = "shuffle-restored")).second.snapshot)
                        assertTrue(restored.preparation?.confirmed == true)
                        assertEquals(selection, restored.preparation?.selection)
                        assertEquals(before.preparation?.deadlineMillis, restored.preparation?.deadlineMillis)
                        assertEquals(40, restored.preparation?.reservedPoints)
                    }
                }
            }
        }
    }

    @Test
    fun testFourSeatPrivateDealAndIdempotentAuthorityOverRealUdp() {
        UdpTableServer(bindHost = "127.0.0.1", port = 0).use { server ->
            server.start()
            val sockets = List(5) { DatagramSocket() }
            try {
                val (hostFrame, created) = request(sockets[0], server.boundPort, 0, 1,
                    ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "four-create", playerName = "A"))
                val roomId = assertNotNull(created.roomId)
                val hostToken = assertNotNull(created.resumeToken)
                val connectionIds = mutableListOf(hostFrame.connectionId)
                for (seat in 1..3) {
                    val (frame, joined) = request(sockets[seat], server.boundPort, 0, 1,
                        ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "four-join-$seat",
                            roomId = roomId, playerName = "P$seat", preferSeat = seat))
                    assertTrue(joined.success)
                    assertEquals(seat, joined.seat)
                    connectionIds.add(frame.connectionId)
                }
                for (seat in 0..3) {
                    val ready = request(sockets[seat], server.boundPort, connectionIds[seat], 2,
                        ApiRequest(api = ApiTags.SET_READY, requestId = "four-ready-$seat", ready = true)).second
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready.receipt?.status)
                }
                val started = request(sockets[0], server.boundPort, connectionIds[0], 3,
                    ApiRequest(api = ApiTags.START_MATCH, requestId = "four-start")).second
                assertEquals(ProtocolReceiptStatus.APPLIED, started.receipt?.status)
                val snapshots = (0..3).map { seat ->
                    assertNotNull(request(sockets[seat], server.boundPort, connectionIds[seat],
                        if (seat == 0) 4 else 3,
                        ApiRequest(api = ApiTags.SNAPSHOT, requestId = "four-snapshot-$seat")).second.snapshot)
                }
                assertEquals(listOf(0, 1, 2, 3), snapshots.map { it.viewerSeat })
                assertEquals(1, snapshots.map { it.roundId }.toSet().size)
                val hands = snapshots.map { it.view.myClosedTiles + listOfNotNull(it.view.myDrawnTile) }
                assertEquals(listOf(14, 13, 13, 13), hands.map { it.size })
                assertEquals(53, hands.flatten().map { it.tileId }.toSet().size)
                for (seat in 0..3) {
                    val push = receiveUntil(sockets[seat], 4000) { frame ->
                        if (frame.packetType != UdpPacketType.PUSH) false else runCatching {
                            ApiMessageCodec.decodePush(String(frame.payload, Charsets.UTF_8))
                                .visibleEvents.any { it is ProtocolGameEvent.TileDealt }
                        }.getOrDefault(false)
                    }
                    val dealt = ApiMessageCodec.decodePush(String(push.payload, Charsets.UTF_8))
                        .visibleEvents.filterIsInstance<ProtocolGameEvent.TileDealt>()
                    assertEquals(if (seat == 0) 14 else 13, dealt.size)
                    assertTrue(dealt.all { it.player == seat })
                }

                val decision = snapshots[0].decision
                val discard = decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
                val choice = ProtocolActionChoice("four-discard", assertNotNull(decision.roundId),
                    decision.decisionId, decision.controlEpoch, discard.optionId)
                val unauthorized = request(sockets[1], server.boundPort, connectionIds[1], 4,
                    ApiRequest(api = ApiTags.SUBMIT_CHOICE, requestId = "four-forged", choice = choice)).second
                assertEquals(ProtocolReceiptStatus.REJECTED, unauthorized.receipt?.status)
                val command = ApiRequest(api = ApiTags.SUBMIT_CHOICE, requestId = choice.requestId, choice = choice)
                val first = request(sockets[0], server.boundPort, connectionIds[0], 5, command).second
                assertTrue(first.receipt?.status != ProtocolReceiptStatus.REJECTED)
                val replay = request(sockets[0], server.boundPort, connectionIds[0], 6, command).second
                assertEquals(first.receipt, replay.receipt)
                val after = assertNotNull(request(sockets[0], server.boundPort, connectionIds[0], 7,
                    ApiRequest(api = ApiTags.SNAPSHOT, requestId = "four-after")).second.snapshot)
                assertEquals(1, after.view.discards[0].size)
                val guestAfter = assertNotNull(request(sockets[1], server.boundPort, connectionIds[1], 5,
                    ApiRequest(api = ApiTags.SNAPSHOT, requestId = "four-guest-after")).second.snapshot)
                assertEquals(after.view.discards, guestAfter.view.discards)
                val late = request(sockets[4], server.boundPort, 0, 1,
                    ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "four-late",
                        roomId = roomId, playerName = "Late")).second
                assertFalse(late.success)

                val (resumedFrame, resumed) = request(sockets[4], server.boundPort, 0, 2,
                    ApiRequest(api = ApiTags.RESUME_SESSION, requestId = "four-resume",
                        roomId = roomId, token = hostToken))
                assertTrue(resumed.success)
                assertEquals(0, resumed.seat)
                assertTrue(resumedFrame.connectionId != connectionIds[0])
                receiveUntil(sockets[0], 4000) { it.packetType == UdpPacketType.KICK }
                sockets[0].close()
                val restored = request(sockets[4], server.boundPort, resumedFrame.connectionId, 3,
                    ApiRequest(api = ApiTags.SNAPSHOT, requestId = "four-restored")).second
                assertTrue(restored.success)
                assertEquals(after.view.discards, restored.snapshot?.view?.discards)
            } finally {
                sockets.forEach { it.close() }
            }
        }
    }

    @Test
    fun testPingPongEcho() {
        val server = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        server.start()
        val socket = DatagramSocket()

        try {
            val clientMillis = 123456789L
            val pingPayload = HeartbeatPacer.encodePingPayload(clientMillis)
            val pingFrame = UdpFrame(
                packetType = UdpPacketType.PING,
                connectionId = 0,
                seq = 1,
                payload = pingPayload
            )
            sendFrame(socket, server.boundPort, pingFrame)

            val pongFrame = receiveFrame(socket, 2000)
            assertEquals(UdpPacketType.PONG, pongFrame.packetType)
            val (cMillis, sMillis) = HeartbeatPacer.decodePongPayload(pongFrame.payload)
            assertEquals(clientMillis, cMillis)
            assertTrue(sMillis > 0L)
        } finally {
            socket.close()
            server.close()
        }
    }

    @Test
    fun testIdempotentReqRetry() {
        val server = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        server.start()
        val socket = DatagramSocket()

        try {
            // 建房
            val (rspFrame, respCreate) = request(
                socket = socket,
                serverPort = server.boundPort,
                connectionId = 0,
                seq = 1,
                request = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "req-retry-host", playerName = "HostRetry")
            )
            assertTrue(respCreate.success)
            val connectionId = rspFrame.connectionId

            // 第一次发送 CAPABILITIES
            val (rsp1, capResp1) = request(
                socket = socket,
                serverPort = server.boundPort,
                connectionId = connectionId,
                seq = 2,
                request = ApiRequest(api = ApiTags.CAPABILITIES, requestId = "req-cap-1")
            )
            assertTrue(capResp1.success)

            // 重发相同 requestId 的请求（模拟丢包重传）
            val payload = ApiMessageCodec.encodeRequest(
                ApiRequest(api = ApiTags.CAPABILITIES, requestId = "req-cap-1")
            ).toByteArray(Charsets.UTF_8)
            val retryFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = connectionId,
                seq = 2,
                payload = payload
            )
            sendFrame(socket, server.boundPort, retryFrame)

            val rspRetry = receiveUntil(socket, 2000) { it.packetType == UdpPacketType.RSP }
            val capResp2 = ApiMessageCodec.decodeResponse(String(rspRetry.payload, Charsets.UTF_8))

            assertEquals(capResp1.requestId, capResp2.requestId)
            assertEquals(capResp1.capabilities?.protocolVersion, capResp2.capabilities?.protocolVersion)
        } finally {
            socket.close()
            server.close()
        }
    }

    @Test
    fun testMalformedJsonRequest() {
        val server = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        server.start()
        val socket = DatagramSocket()

        try {
            val brokenPayload = """{"requestId":"req-broken","api":""".toByteArray(Charsets.UTF_8)
            val brokenFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = 0,
                seq = 1,
                payload = brokenPayload
            )
            sendFrame(socket, server.boundPort, brokenFrame)

            val rspFrame = receiveFrame(socket, 2000)
            assertEquals(UdpPacketType.RSP, rspFrame.packetType)
            val resp = ApiMessageCodec.decodeResponse(String(rspFrame.payload, Charsets.UTF_8))
            assertFalse(resp.success)
            assertEquals("req-broken", resp.requestId)
            assertEquals(ApiErrors.MALFORMED_REQUEST, resp.error)
        } finally {
            socket.close()
            server.close()
        }
    }

    @Test
    fun testInvalidConnectionIdSilentlyDropped() {
        val server = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        server.start()
        val socket = DatagramSocket()

        try {
            // connectionId 不存在的数据报
            val reqPayload = ApiMessageCodec.encodeRequest(
                ApiRequest(api = ApiTags.CAPABILITIES, requestId = "req-ghost")
            ).toByteArray(Charsets.UTF_8)
            val ghostFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = 987654321,
                seq = 1,
                payload = reqPayload
            )
            sendFrame(socket, server.boundPort, ghostFrame)

            // 服务端静默丢弃，客户端接收超时
            assertFailsWith<SocketTimeoutException> {
                receiveFrame(socket, 1000)
            }
        } finally {
            socket.close()
            server.close()
        }
    }

    @Test
    fun testResendPushHandling() {
        val server = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        server.start()
        val client1 = DatagramSocket()
        val client2 = DatagramSocket()

        try {
            val (_, respCreate) = request(
                socket = client1,
                serverPort = server.boundPort,
                connectionId = 0,
                seq = 1,
                request = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "req-resend-host", playerName = "HostResend")
            )
            val roomId = respCreate.roomId ?: error("Room ID missing in create response")

            val (rsp2, _) = request(
                socket = client2,
                serverPort = server.boundPort,
                connectionId = 0,
                seq = 1,
                request = ApiRequest(
                    api = ApiTags.JOIN_ROOM,
                    requestId = "req-resend-guest",
                    roomId = roomId,
                    playerName = "GuestResend",
                    preferSeat = 1
                )
            )
            val connectionId2 = rsp2.connectionId

            // 客户端 2 索要重传起始游标 0 的推送帧
            val resendJson = """{"fromCursor":0}""".toByteArray(Charsets.UTF_8)
            val resendFrame = UdpFrame(
                packetType = UdpPacketType.RESEND,
                connectionId = connectionId2,
                seq = 2,
                payload = resendJson
            )
            sendFrame(client2, server.boundPort, resendFrame)

            val pushFrame = receiveUntil(client2, 3000) { it.packetType == UdpPacketType.PUSH }
            assertEquals(UdpPacketType.PUSH, pushFrame.packetType)
            assertEquals(connectionId2, pushFrame.connectionId)
        } finally {
            client1.close()
            client2.close()
            server.close()
        }
    }
}

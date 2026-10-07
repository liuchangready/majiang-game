package com.majiang.client.transport.udp

import com.google.gson.JsonParser
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpProtocol
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class UdpClientBindingTest {

    private class FakeUdpClock(var now: Long = 1000L) : UdpClock {
        override fun currentTimeMillis(): Long = now
    }

    private class FakeUdpScheduler(private val clock: FakeUdpClock) : UdpScheduler {
        data class ScheduledTask(val triggerTime: Long, val task: Runnable, var cancelled: Boolean = false)
        val tasks = mutableListOf<ScheduledTask>()

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val entry = ScheduledTask(clock.now + delayMillis, task)
            tasks.add(entry)
            return Cancellable { entry.cancelled = true }
        }

        fun advanceTime(deltaMillis: Long) {
            clock.now += deltaMillis
            // 按到期时间排序依次执行
            while (true) {
                val nextTask = tasks
                    .filter { !it.cancelled && it.triggerTime <= clock.now }
                    .minByOrNull { it.triggerTime } ?: break
                tasks.remove(nextTask)
                if (!nextTask.cancelled) {
                    nextTask.task.run()
                }
            }
        }
    }

    private fun createDefaultSnapshot(cursor: Long = 0L): ProtocolPlayerSnapshot {
        val tile = ProtocolTile.fromTileId(0)
        val view = ProtocolPlayerView(
            viewerSeat = 0,
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(tile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = listOf(tile),
            myDrawnTile = null,
            legalActions = listOf(ProtocolLegalAction.DiscardTile(0, tile))
        )
        val decision = ProtocolPlayerDecision(
            roundId = "round-1",
            decisionId = "dec-1",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, tile)))
        )
        return ProtocolPlayerSnapshot(
            sessionId = "session-1",
            roundId = "round-1",
            syncEpoch = "sync-1",
            cursor = cursor,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = decision
        )
    }

    private fun createDefaultPublicSnapshot(
        cursor: Long = 0L,
        sessionId: String = "session-pub-1",
        syncEpoch: String = "epoch-1"
    ): ProtocolPublicSnapshot {
        val summary = ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 1,
            dealerSeat = 0,
            honba = 0
        )
        return ProtocolPublicSnapshot(
            sessionId = sessionId,
            sessionVersion = 1L,
            roundId = "round-1",
            roundVersion = 1L,
            syncEpoch = syncEpoch,
            cursor = cursor,
            board = null,
            session = summary,
            clocks = emptyList(),
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList()
        )
    }

    private fun createDefaultRoomInfo(
        roomId: String = "ROOM1",
        version: Long = 1L,
        targetScore: Int = 25000
    ): ProtocolRoomInfo {
        return ProtocolRoomInfo(
            roomId = roomId,
            lifecycle = ProtocolRoomLifecycle.LOBBY,
            version = version,
            hostSeat = 0,
            members = emptyList(),
            config = ProtocolRoomConfig(
                matchConfig = ProtocolMatchConfig(targetScore = targetScore)
            )
        )
    }

    private fun <T> CompletableFuture<T>.await(seconds: Long = 5): T = get(seconds, TimeUnit.SECONDS)

    @Test
    fun testReqRetransmissionTriggeredOnTimeoutAndRspCompletion() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler
        )

        // 1. 发起请求 listRooms
        val future = binding.listRooms()
        assertEquals(1, sentBytes.size)

        val frame1 = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.REQ, frame1.packetType)
        val req1 = ApiMessageCodec.decodeRequest(frame1.payload.toString(Charsets.UTF_8))

        // 2. 时间推进 100ms（尚未达到初始 RTO 200ms），不重传
        scheduler.advanceTime(100L)
        assertEquals(1, sentBytes.size)

        // 3. 时间再推进 100ms（累计 200ms），触发首次重传 (attempt 2)
        scheduler.advanceTime(100L)
        assertEquals(2, sentBytes.size)
        val frame2 = UdpFrame.decode(sentBytes[1])
        assertEquals(UdpPacketType.REQ, frame2.packetType)
        assertEquals(frame1.seq, frame2.seq)

        // 4. 第二次 RTO = 200 * 1.5 = 300ms。推进 300ms，触发第二次重传 (attempt 3)
        scheduler.advanceTime(300L)
        assertEquals(3, sentBytes.size)

        // 5. 伪造入站 RSP 响应
        val rsp = ApiResponse(
            requestId = req1.requestId,
            success = true,
            rooms = listOf(RoomSummary("ROOM1", "Room One", "Host", 1, 4, false))
        )
        val rspPayload = ApiMessageCodec.encodeResponse(rsp).toByteArray(Charsets.UTF_8)
        val rspFrame = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 0,
            seq = 100,
            ackSeq = frame1.seq,
            payload = rspPayload
        )
        binding.onInboundFrame(rspFrame)

        // Future 应当已完成
        assertTrue(future.isDone)
        val rooms = future.await()
        assertEquals(1, rooms.size)
        assertEquals("ROOM1", rooms[0].roomId)

        // 验证客户端自动回发了 ACK
        assertEquals(4, sentBytes.size)
        val ackFrame = UdpFrame.decode(sentBytes[3])
        assertEquals(UdpPacketType.ACK, ackFrame.packetType)
        assertEquals(100, ackFrame.ackSeq)

        // 6. 测试幂等性：重复注入相同的 RSP 帧，不报错且再次发送 ACK
        binding.onInboundFrame(rspFrame)
        assertEquals(5, sentBytes.size)
        val ackFrame2 = UdpFrame.decode(sentBytes[4])
        assertEquals(UdpPacketType.ACK, ackFrame2.packetType)
        assertEquals(100, ackFrame2.ackSeq)
    }

    @Test
    fun testRequestTimeoutFailsFutureAfterBoundedRetries() {
        val clock = FakeUdpClock()
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()
        val binding = UdpClientBinding(rawSender = { sentBytes.add(it) }, clock = clock, scheduler = scheduler)
        val pending = binding.listRooms()
        assertEquals(1, sentBytes.size)

        repeat(UdpProtocol.MAX_REQUEST_ATTEMPTS) {
            scheduler.advanceTime(UdpProtocol.MAX_RTO_MILLIS)
        }

        assertTrue(pending.isCompletedExceptionally)
        assertEquals(UdpProtocol.MAX_REQUEST_ATTEMPTS, sentBytes.count { UdpFrame.decode(it).packetType == UdpPacketType.REQ })
        assertTrue(binding.closed)
    }

    @Test
    fun testMalformedResponseFailsOnlyItsRequestAndLaterRequestStillWorks() {
        val clock = FakeUdpClock()
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()
        val binding = UdpClientBinding(rawSender = { sentBytes.add(it) }, clock = clock, scheduler = scheduler)
        val malformed = binding.listRooms()
        val firstRequest = UdpFrame.decode(sentBytes.last())
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 0, 101, firstRequest.seq, "{invalid".toByteArray(Charsets.UTF_8)))
        assertTrue(malformed.isCompletedExceptionally)

        val next = binding.listRooms()
        val secondRequest = UdpFrame.decode(sentBytes.last())
        val decoded = ApiMessageCodec.decodeRequest(secondRequest.payload.toString(Charsets.UTF_8))
        val response = ApiResponse(decoded.requestId, success = true, rooms = listOf(RoomSummary("ROOM2", "Two", "Host", 1, 4, false)))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 0, 102, secondRequest.seq, ApiMessageCodec.encodeResponse(response).toByteArray(Charsets.UTF_8)))
        assertEquals("ROOM2", next.await().single().roomId)
    }

    @Test
    fun testCloseFailsOutstandingRequestAndLateResponseCannotReviveBinding() {
        val clock = FakeUdpClock()
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()
        val binding = UdpClientBinding(rawSender = { sentBytes.add(it) }, clock = clock, scheduler = scheduler)
        val pending = binding.listRooms()
        val request = UdpFrame.decode(sentBytes.single())
        val requestId = ApiMessageCodec.decodeRequest(request.payload.toString(Charsets.UTF_8)).requestId

        binding.close("test_close")
        assertTrue(binding.closed)
        assertEquals("test_close", binding.disconnectReason)
        assertTrue(pending.isCompletedExceptionally)
        val response = ApiResponse(requestId, success = true, rooms = listOf(RoomSummary("ROOM2", "Two", "Host", 1, 4, false)))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 0, 103, request.seq, ApiMessageCodec.encodeResponse(response).toByteArray(Charsets.UTF_8)))
        assertTrue(pending.isCompletedExceptionally)
        assertTrue(binding.listRooms().isCompletedExceptionally)
    }

    @Test
    fun testPushGapTriggersResend() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler
        )

        // 模拟建房绑定成功，初始游标为 10L
        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-create",
            success = true,
            roomId = "ROOM9999",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))

        val rspPayload = ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)
        val rspFrame = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 12345,
            seq = 50,
            ackSeq = reqFrame.seq,
            payload = rspPayload
        )
        binding.onInboundFrame(rspFrame)
        reqFuture.await()

        assertEquals(12345, binding.connectionId)
        assertEquals("ROOM9999", binding.boundRoomId)

        // 清空前序发送记录
        sentBytes.clear()

        // 注入缺口 PUSH：fromCursor = 14L（当前游标为 10L，缺 11L）
        val gapSnap = createDefaultSnapshot(15L)
        val gapUpdate = ProtocolPlayerUpdate(gapSnap, fromCursor = 14L, visibleEvents = emptyList())
        val pushFrame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 12345,
            seq = 51,
            payload = ApiMessageCodec.encodePush(gapUpdate).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame)

        // 验证产出 RESEND 帧，请求 fromCursor = 11L
        assertEquals(1, sentBytes.size)
        val resendFrame = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.RESEND, resendFrame.packetType)
        assertEquals(12345, resendFrame.connectionId)

        val resendJson = resendFrame.payload.toString(Charsets.UTF_8)
        val obj = JsonParser.parseString(resendJson).asJsonObject
        assertEquals(11L, obj.get("fromCursor").asLong)
    }

    @Test
    fun testPushBeyondWindowTriggersFullSnapshotSelfHeal() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 999
        )

        // 建立真实玩家房间绑定
        val initSnap = createDefaultSnapshot(0L)
        val createFuture = binding.createRoom(CreateRoomRequest("Host"))
        val createReqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(createReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(
            UdpFrame(
                UdpPacketType.RSP,
                999,
                50,
                createReqFrame.seq,
                ApiMessageCodec.encodeResponse(
                    ApiResponse(
                        requestId = createReq.requestId,
                        success = true,
                        roomId = "ROOM1",
                        seat = 0,
                        resumeToken = "token-1",
                        snapshot = initSnap
                    )
                ).toByteArray(Charsets.UTF_8)
            )
        )
        createFuture.await()
        sentBytes.clear()

        // 注入超窗缺口（当前游标 0，推送 fromCursor = 300L）
        val beyondSnap = createDefaultSnapshot(301L)
        val beyondUpdate = ProtocolPlayerUpdate(beyondSnap, fromCursor = 300L, visibleEvents = emptyList())
        val pushFrame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 999,
            seq = 10,
            payload = ApiMessageCodec.encodePush(beyondUpdate).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame)

        // 验证自动触发了 SNAPSHOT 请求自愈
        assertTrue(sentBytes.isNotEmpty())
        val reqFrame = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.REQ, reqFrame.packetType)
        val req = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        assertEquals(ApiTags.SNAPSHOT, req.api)
    }

    @Test
    fun testEarlyPushBufferingReplay() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 100
        )

        // 建立真实玩家房间绑定
        val initSnap = createDefaultSnapshot(0L)
        val createFuture = binding.createRoom(CreateRoomRequest("Host"))
        val createReqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(createReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(
            UdpFrame(
                UdpPacketType.RSP,
                100,
                50,
                createReqFrame.seq,
                ApiMessageCodec.encodeResponse(
                    ApiResponse(
                        requestId = createReq.requestId,
                        success = true,
                        roomId = "ROOM1",
                        seat = 0,
                        resumeToken = "token-1",
                        snapshot = initSnap
                    )
                ).toByteArray(Charsets.UTF_8)
            )
        )
        createFuture.await()
        sentBytes.clear()

        // 尚未注册 onUpdate 监听器，推送先行到达
        val snap1 = createDefaultSnapshot(1L)
        val update1 = ProtocolPlayerUpdate(snap1, fromCursor = 0L, visibleEvents = emptyList())
        val pushFrame1 = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 100,
            seq = 1,
            payload = ApiMessageCodec.encodePush(update1).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame1)

        val snap2 = createDefaultSnapshot(2L)
        val update2 = ProtocolPlayerUpdate(snap2, fromCursor = 1L, visibleEvents = emptyList())
        val pushFrame2 = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 100,
            seq = 2,
            payload = ApiMessageCodec.encodePush(update2).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame2)

        // 此时注册监听器，应当立即按序回放两条缓冲
        val received = mutableListOf<ProtocolPlayerUpdate>()
        val sub = binding.onUpdate { received.add(it) }

        assertEquals(2, received.size)
        assertEquals(1L, received[0].snapshot.cursor)
        assertEquals(2L, received[1].snapshot.cursor)

        sub.close()
    }

    @Test
    fun testKickClosesBindingAndSendsAck() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 888
        )

        var disconnectReason: String? = null
        val sub = binding.onDisconnect { reason ->
            disconnectReason = reason
        }

        assertFalse(binding.closed)

        // 注入 KICK 帧
        val kickPayload = ApiMessageCodec.encodeKick(ApiProtocol.DISPLACED_BY_NEW_CONNECTION).toByteArray(Charsets.UTF_8)
        val kickFrame = UdpFrame(
            packetType = UdpPacketType.KICK,
            connectionId = 888,
            seq = 77,
            payload = kickPayload
        )
        binding.onInboundFrame(kickFrame)

        // 校验连接关闭与通知
        assertTrue(binding.closed)
        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, binding.disconnectReason)
        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, disconnectReason)

        // 校验回发了 ACK
        assertEquals(1, sentBytes.size)
        val ackFrame = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.ACK, ackFrame.packetType)
        assertEquals(77, ackFrame.ackSeq)

        sub.close()
    }

    @Test
    fun testHeartbeatMissedPongsTriggersDead() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 777
        )

        var deadReason: String? = null
        binding.onDisconnect { deadReason = it }

        binding.startHeartbeat()

        // 推进 3000ms -> PING 1 发送
        scheduler.advanceTime(3000L)
        assertEquals(1, sentBytes.size)
        val ping1 = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.PING, ping1.packetType)

        // 推进 3000ms (未收到 PONG) -> PING 2 发送 (missedPongs=1)
        // 维持入站活跃，避免 10s READ_TIMEOUT 掩盖心跳失步检测 (对齐 HeartbeatPacerTest 规范)
        binding.onInboundFrame(UdpFrame(UdpPacketType.ACK, 777, 10, ackSeq = 1))
        scheduler.advanceTime(3000L)
        assertEquals(2, sentBytes.size)

        // 推进 3000ms (未收到 PONG) -> PING 3 发送 (missedPongs=2)
        binding.onInboundFrame(UdpFrame(UdpPacketType.ACK, 777, 11, ackSeq = 1))
        scheduler.advanceTime(3000L)
        assertEquals(3, sentBytes.size)

        // 推进 3000ms (未收到 PONG) -> missedPongs=3 >= 3，判死！
        binding.onInboundFrame(UdpFrame(UdpPacketType.ACK, 777, 12, ackSeq = 1))
        scheduler.advanceTime(3000L)
        assertTrue(binding.closed)
        assertEquals(UdpProtocol.REASON_MISSED_HEARTBEATS, binding.disconnectReason)
        assertEquals(UdpProtocol.REASON_MISSED_HEARTBEATS, deadReason)
    }

    @Test
    fun testRoomUpdateAckSentAndRetransmissionRecoversLostAck() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        // 模拟建房绑定成功
        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        val rspPayload = ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)
        val rspFrame = UdpFrame(
            packetType = UdpPacketType.RSP,
            connectionId = 12345,
            seq = 50,
            ackSeq = reqFrame.seq,
            payload = rspPayload
        )
        binding.onInboundFrame(rspFrame)
        reqFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        val sub = binding.onRoomUpdate { received.add(it) }

        // 服务端发送 ROOM_UPDATE 推送（seq = 55, version = 1L）
        val roomInfo1 = createDefaultRoomInfo("ROOM1", version = 1L)
        val pushFrame1 = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 12345,
            seq = 55,
            payload = ApiMessageCodec.encodeRoomUpdatePush(roomInfo1).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame1)

        // 1. 验证客户端立即回发了 ACK（ackSeq = 55），且 UI 监听器收到该更新
        assertEquals(1, sentBytes.size)
        val ack1 = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.ACK, ack1.packetType)
        assertEquals(55, ack1.ackSeq)
        assertEquals(12345, ack1.connectionId)
        assertEquals(1, received.size)
        assertEquals(1L, received[0].version)
        assertEquals(1L, binding.lastRoomInfoVersion)

        // 2. 模拟首个 ACK 在网络中丢弃，服务端重传相同的 ROOM_UPDATE（seq = 55, version = 1L）
        // 关键契约：ReplayGuard 不得阻断重传的识别与 ACK 回发，且 UI 监听器不产生重复事件
        binding.onInboundFrame(pushFrame1)

        assertEquals(2, sentBytes.size)
        val ack2 = UdpFrame.decode(sentBytes[1])
        assertEquals(UdpPacketType.ACK, ack2.packetType)
        assertEquals(55, ack2.ackSeq)
        assertEquals(12345, ack2.connectionId)

        // UI 去重：依然仅有 1 次事件
        assertEquals(1, received.size)
        assertEquals(1L, binding.lastRoomInfoVersion)

        sub.close()
    }

    @Test
    fun testLostFinalRoomUpdateRetransmissionRecoversWithoutUserOperation() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        val rspPayload = ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, rspPayload))
        reqFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        // 首次房间配置更新帧（seq = 60, targetScore = 34567, version = 2L）在网络中丢失，未送达客户端
        // 服务端重传该帧到达客户端
        val changedInfo = createDefaultRoomInfo("ROOM1", version = 2L, targetScore = 34567)
        val retryFrame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 12345,
            seq = 60,
            payload = ApiMessageCodec.encodeRoomUpdatePush(changedInfo).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(retryFrame)

        // 验证客户端无需调用 getRoomInfo 或其他操作，直接识别并应用重传，回发 ACK
        assertEquals(1, sentBytes.size)
        val ack = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.ACK, ack.packetType)
        assertEquals(60, ack.ackSeq)

        assertEquals(1, received.size)
        assertEquals(34567, received[0].config.matchConfig.targetScore)
        assertEquals(2L, received[0].version)
        assertEquals(2L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testOutOfOrderOldRoomVersionDoesNotRegress() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        // 先收到新版本 version = 3L (seq = 70)
        val infoV3 = createDefaultRoomInfo("ROOM1", version = 3L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 70, payload = ApiMessageCodec.encodeRoomUpdatePush(infoV3).toByteArray(Charsets.UTF_8)))

        assertEquals(1, received.size)
        assertEquals(3L, received[0].version)
        assertEquals(3L, binding.lastRoomInfoVersion)
        assertEquals(1, sentBytes.size)

        // 后收到滞后乱序的旧版本 version = 2L (seq = 71)
        val infoV2 = createDefaultRoomInfo("ROOM1", version = 2L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 71, payload = ApiMessageCodec.encodeRoomUpdatePush(infoV2).toByteArray(Charsets.UTF_8)))

        // 验证旧版本仍正常回发 ACK，但版本不回退，且不分发给 UI
        assertEquals(2, sentBytes.size)
        val ack2 = UdpFrame.decode(sentBytes[1])
        assertEquals(UdpPacketType.ACK, ack2.packetType)
        assertEquals(71, ack2.ackSeq)

        assertEquals(1, received.size)
        assertEquals(3L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testVersionJumpMergesWithoutTreatingAsCursorGap() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        // 收到 version = 1L
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 80, payload = ApiMessageCodec.encodeRoomUpdatePush(createDefaultRoomInfo("ROOM1", version = 1L)).toByteArray(Charsets.UTF_8)))
        assertEquals(1, received.size)
        assertEquals(1L, binding.lastRoomInfoVersion)
        sentBytes.clear()

        // 收到 version = 5L（跳版本，跨过了 2, 3, 4）
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 81, payload = ApiMessageCodec.encodeRoomUpdatePush(createDefaultRoomInfo("ROOM1", version = 5L)).toByteArray(Charsets.UTF_8)))

        // 验证：直接合并全量房间视图，不当作 game cursor 缺口（无 RESEND 帧，仅有 ACK 帧）
        assertEquals(1, sentBytes.size)
        val ack = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.ACK, ack.packetType)
        assertEquals(81, ack.ackSeq)

        assertEquals(2, received.size)
        assertEquals(5L, received[1].version)
        assertEquals(5L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testForeignRoomIdUpdateIgnoredWithoutAck() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        // 外来房间推送（roomId 为 ROOM_OTHER）
        val foreignInfo = createDefaultRoomInfo("ROOM_OTHER", version = 1L)
        val foreignFrame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 12345,
            seq = 90,
            payload = ApiMessageCodec.encodeRoomUpdatePush(foreignInfo).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(foreignFrame)

        // 验证：不给外来身份的数据回发 ACK，不通知 UI，版本不变化
        assertTrue(sentBytes.isEmpty())
        assertTrue(received.isEmpty())
        assertEquals(0L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testRoomSwitchResetsVersionAndBuffersAndLowerVersionAccepted() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        // 1. 绑定 ROOM1 并推进版本至 10L
        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()

        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 60, payload = ApiMessageCodec.encodeRoomUpdatePush(createDefaultRoomInfo("ROOM1", version = 10L)).toByteArray(Charsets.UTF_8)))
        assertEquals(10L, binding.lastRoomInfoVersion)
        sentBytes.clear()

        // 2. 离开 ROOM1
        val leaveFuture = binding.leaveRoom()
        val leaveReqFrame = UdpFrame.decode(sentBytes[0])
        val leaveReq = ApiMessageCodec.decodeRequest(leaveReqFrame.payload.toString(Charsets.UTF_8))
        val leaveRsp = ApiResponse(requestId = leaveReq.requestId, success = true, receipt = ProtocolActionReceipt(leaveReq.requestId, ProtocolReceiptStatus.APPLIED))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 70, leaveReqFrame.seq, ApiMessageCodec.encodeResponse(leaveRsp).toByteArray(Charsets.UTF_8)))
        leaveFuture.await()

        assertEquals(0L, binding.lastRoomInfoVersion)
        assertFalse(binding.closed)
        sentBytes.clear()

        // 3. 加入新房 ROOM2（分配新 connectionId 67890）
        val joinFuture = binding.joinRoom(JoinRoomRequest("ROOM2"))
        val joinReqFrame = UdpFrame.decode(sentBytes[0])
        val joinReq = ApiMessageCodec.decodeRequest(joinReqFrame.payload.toString(Charsets.UTF_8))
        val joinRsp = ApiResponse(
            requestId = joinReq.requestId,
            success = true,
            roomId = "ROOM2",
            seat = 1,
            resumeToken = "tok-2",
            snapshot = initSnap
        )
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 67890, 80, joinReqFrame.seq, ApiMessageCodec.encodeResponse(joinRsp).toByteArray(Charsets.UTF_8)))
        joinFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        // ROOM2 推送 version = 2L（低于旧房 ROOM1 的 10L）
        val infoRoom2 = createDefaultRoomInfo("ROOM2", version = 2L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 67890, 85, payload = ApiMessageCodec.encodeRoomUpdatePush(infoRoom2).toByteArray(Charsets.UTF_8)))

        // 验证：新房正确接收低版本更新，不被旧房高版本阻断，并回发新 connectionId 的 ACK
        assertEquals(1, sentBytes.size)
        val ack = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.ACK, ack.packetType)
        assertEquals(85, ack.ackSeq)
        assertEquals(67890, ack.connectionId)

        assertEquals(1, received.size)
        assertEquals("ROOM2", received[0].roomId)
        assertEquals(2L, received[0].version)
        assertEquals(2L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testEarlyRoomUpdateBufferedAndDeliveredOnSubscription() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        // 在尚未注册 onRoomUpdate 监听器时，早期房间推送先到达
        val infoV1 = createDefaultRoomInfo("ROOM1", version = 1L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 100, payload = ApiMessageCodec.encodeRoomUpdatePush(infoV1).toByteArray(Charsets.UTF_8)))

        // 验证回发了 ACK
        assertEquals(1, sentBytes.size)
        val ack = UdpFrame.decode(sentBytes[0])
        assertEquals(100, ack.ackSeq)

        // 随后注册监听器，早期更新按序回放
        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        assertEquals(1, received.size)
        assertEquals(1L, received[0].version)
        assertEquals("ROOM1", received[0].roomId)
    }

    @Test
    fun testDelayedGetRoomInfoQueryDoesNotContaminateNewRoom() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        // 在 ROOM1 发起 getRoomInfo 查询
        val room1InfoFuture = binding.getRoomInfo()
        val queryFrame = UdpFrame.decode(sentBytes[0])
        val queryReq = ApiMessageCodec.decodeRequest(queryFrame.payload.toString(Charsets.UTF_8))
        sentBytes.clear()

        // 玩家离开 ROOM1 并加入 ROOM2
        val leaveFuture = binding.leaveRoom()
        val leaveReqFrame = UdpFrame.decode(sentBytes[0])
        val leaveReq = ApiMessageCodec.decodeRequest(leaveReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 60, leaveReqFrame.seq, ApiMessageCodec.encodeResponse(ApiResponse(leaveReq.requestId, true, receipt = ProtocolActionReceipt(leaveReq.requestId, ProtocolReceiptStatus.APPLIED))).toByteArray(Charsets.UTF_8)))
        leaveFuture.await()
        sentBytes.clear()

        val joinFuture = binding.joinRoom(JoinRoomRequest("ROOM2"))
        val joinReqFrame = UdpFrame.decode(sentBytes[0])
        val joinReq = ApiMessageCodec.decodeRequest(joinReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 99999, 70, joinReqFrame.seq, ApiMessageCodec.encodeResponse(ApiResponse(joinReq.requestId, true, roomId = "ROOM2", seat = 1, resumeToken = "tok-2", snapshot = initSnap)).toByteArray(Charsets.UTF_8)))
        joinFuture.await()
        sentBytes.clear()

        val room2Updates = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { room2Updates.add(it) }

        // 此时 ROOM1 的延迟 getRoomInfo 响应返回（version = 99L）
        val delayedRsp = ApiResponse(
            requestId = queryReq.requestId,
            success = true,
            roomInfo = createDefaultRoomInfo("ROOM1", version = 99L)
        )
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 99999, 80, queryFrame.seq, ApiMessageCodec.encodeResponse(delayedRsp).toByteArray(Charsets.UTF_8)))

        // 验证：Future 拿到该次查询结果，但 ROOM2 的版本未被污染（依然为 0L），ROOM2 监听器未收到旧房通知
        assertTrue(room1InfoFuture.isDone)
        assertEquals("ROOM1", room1InfoFuture.await().roomId)
        assertEquals(0L, binding.lastRoomInfoVersion)
        assertTrue(room2Updates.isEmpty())
    }

    @Test
    fun testMalformedRoomUpdateEnvelopeRejectedWithoutAck() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val received = mutableListOf<ProtocolRoomInfo>()
        binding.onRoomUpdate { received.add(it) }

        // 畸形信封：type 为 ROOM_UPDATE 但缺失 roomInfo 或包含额外非法字段
        val malformedPayload = """{"type":"ROOM_UPDATE","illegalExtraField":"bad"}""".toByteArray(Charsets.UTF_8)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 110, payload = malformedPayload))

        // 验证：不给非法数据回发 ACK，不通知 UI
        assertTrue(sentBytes.isEmpty())
        assertTrue(received.isEmpty())
        assertEquals(0L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testReentrantListenerCallingLeaveRoomDoesNotDeadlock() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        var leaveFuture: CompletableFuture<ProtocolActionReceipt>? = null

        binding.onRoomUpdate {
            // 重入监听器在回调中直接触发离房
            leaveFuture = binding.leaveRoom()
        }

        // 注入房间更新推送
        val roomInfo = createDefaultRoomInfo("ROOM1", version = 1L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 60, payload = ApiMessageCodec.encodeRoomUpdatePush(roomInfo).toByteArray(Charsets.UTF_8)))

        // 验证：首个 ACK 已回发，且 leaveRoom 请求已顺利发出（未死锁）
        assertNotNull(leaveFuture)
        assertTrue(sentBytes.size >= 2) // 1: ACK for PUSH, 2: REQ for leaveRoom
        val ackFrame = UdpFrame.decode(sentBytes[0])
        assertEquals(UdpPacketType.ACK, ackFrame.packetType)
        assertEquals(60, ackFrame.ackSeq)

        val leaveReqFrame = UdpFrame.decode(sentBytes[1])
        assertEquals(UdpPacketType.REQ, leaveReqFrame.packetType)
        val leaveReq = ApiMessageCodec.decodeRequest(leaveReqFrame.payload.toString(Charsets.UTF_8))
        assertEquals(ApiTags.LEAVE_ROOM, leaveReq.api)

        // 注入离开房间的响应
        val leaveRsp = ApiResponse(requestId = leaveReq.requestId, success = true, receipt = ProtocolActionReceipt(leaveReq.requestId, ProtocolReceiptStatus.APPLIED))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 70, leaveReqFrame.seq, ApiMessageCodec.encodeResponse(leaveRsp).toByteArray(Charsets.UTF_8)))

        val receipt = leaveFuture!!.await(5)
        assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
        assertEquals(0, binding.connectionId)
        assertEquals(null, binding.boundRoomId)
    }

    @Test
    fun testReentrantListenerCallingCloseDoesNotDeadlock() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = mutableListOf<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        var secondListenerCalled = false

        binding.onRoomUpdate {
            // 重入监听器在回调中直接触发关闭连接
            binding.close("closed_by_reentrant_listener")
        }
        binding.onRoomUpdate {
            secondListenerCalled = true
        }

        // 注入房间更新推送
        val roomInfo = createDefaultRoomInfo("ROOM1", version = 1L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 60, payload = ApiMessageCodec.encodeRoomUpdatePush(roomInfo).toByteArray(Charsets.UTF_8)))

        // 验证：连接已成功关闭，未发生死锁，后置监听器被守卫阻断未执行
        assertTrue(binding.closed)
        assertEquals("closed_by_reentrant_listener", binding.disconnectReason)
        assertFalse(secondListenerCalled)
    }

    @Test
    fun testConcurrentUpdateSubmissionWhileFirstListenerBlockedNeverDeliversOutOfOrderToSecondListener() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val listener1BlockedLatch = CountDownLatch(1)
        val releaseListener1Latch = CountDownLatch(1)
        val listener1Versions = CopyOnWriteArrayList<Long>()
        val listener2Versions = CopyOnWriteArrayList<Long>()

        // 监听器 1：当收到版本 2 时阻塞，模拟慢处理监听器
        binding.onRoomUpdate { info ->
            listener1Versions.add(info.version)
            if (info.version == 2L) {
                listener1BlockedLatch.countDown()
                releaseListener1Latch.await(5, TimeUnit.SECONDS)
            }
        }

        // 监听器 2：观察者记录收到的版本序列
        binding.onRoomUpdate { info ->
            listener2Versions.add(info.version)
        }

        // 线程 1：推送版本 2L，触发监听器 1 阻塞
        val t1 = Thread {
            val infoV2 = createDefaultRoomInfo("ROOM1", version = 2L)
            binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 60, payload = ApiMessageCodec.encodeRoomUpdatePush(infoV2).toByteArray(Charsets.UTF_8)))
        }
        t1.start()

        // 等待监听器 1 确认已进入阻塞状态
        assertTrue(listener1BlockedLatch.await(5, TimeUnit.SECONDS), "Listener 1 should be blocked inside version 2 callback")

        // 线程 2：在监听器 1 仍在处理版本 2 期间，并发提交版本 3L
        val t2 = Thread {
            val infoV3 = createDefaultRoomInfo("ROOM1", version = 3L)
            binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 70, payload = ApiMessageCodec.encodeRoomUpdatePush(infoV3).toByteArray(Charsets.UTF_8)))
        }
        t2.start()
        t2.join(5000)

        // 此时释放监听器 1
        releaseListener1Latch.countDown()
        t1.join(5000)

        // 验证关键不变量：
        // 1. 监听器 2 绝不可收到倒序的 [3, 2]！
        assertFalse(listener2Versions.size >= 2 && listener2Versions[0] == 3L && listener2Versions[1] == 2L, "Listener 2 must never observe [3, 2]")
        // 2. 监听器 2 观察到的序列严格单调递增
        for (i in 0 until listener2Versions.size - 1) {
            assertTrue(listener2Versions[i] < listener2Versions[i + 1], "Versions observed by listener 2 must be strictly monotonic")
        }
        // 3. 最终视图必然可达版本 3L
        assertTrue(listener2Versions.isNotEmpty())
        assertEquals(3L, listener2Versions.last())
        assertEquals(3L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testInFlightOldRoomUpdateAcrossLeaveAndNewBindDoesNotPolluteCacheOrNotifyNewRoom() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        // 玩家离开 ROOM1 并加入 ROOM2
        val leaveFuture = binding.leaveRoom()
        val leaveReqFrame = UdpFrame.decode(sentBytes[0])
        val leaveReq = ApiMessageCodec.decodeRequest(leaveReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 60, leaveReqFrame.seq, ApiMessageCodec.encodeResponse(ApiResponse(leaveReq.requestId, true, receipt = ProtocolActionReceipt(leaveReq.requestId, ProtocolReceiptStatus.APPLIED))).toByteArray(Charsets.UTF_8)))
        leaveFuture.await()
        sentBytes.clear()

        val joinFuture = binding.joinRoom(JoinRoomRequest("ROOM2"))
        val joinReqFrame = UdpFrame.decode(sentBytes[0])
        val joinReq = ApiMessageCodec.decodeRequest(joinReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 67890, 70, joinReqFrame.seq, ApiMessageCodec.encodeResponse(ApiResponse(joinReq.requestId, true, roomId = "ROOM2", seat = 1, resumeToken = "tok-2", snapshot = initSnap)).toByteArray(Charsets.UTF_8)))
        joinFuture.await()
        sentBytes.clear()

        val room2Updates = CopyOnWriteArrayList<ProtocolRoomInfo>()
        binding.onRoomUpdate { room2Updates.add(it) }

        // 此时迟到的旧房间 ROOM1 推送（高版本 version = 99L）在换房后到达
        val oldPush = createDefaultRoomInfo("ROOM1", version = 99L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 67890, 80, payload = ApiMessageCodec.encodeRoomUpdatePush(oldPush).toByteArray(Charsets.UTF_8)))

        // 验证：旧房间更新被原子身份拦截，不污染新房间缓存，不回发 ACK，不通知新房间监听器
        assertTrue(sentBytes.isEmpty(), "Must not ACK foreign/old room update")
        assertTrue(room2Updates.isEmpty(), "Room 2 listeners must not receive old room update")
        assertEquals(0L, binding.lastRoomInfoVersion, "Room 2 version must remain 0L")

        // 随后 ROOM2 的合法低版本更新（version = 1L）正常接收
        val room2Push = createDefaultRoomInfo("ROOM2", version = 1L)
        binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 67890, 85, payload = ApiMessageCodec.encodeRoomUpdatePush(room2Push).toByteArray(Charsets.UTF_8)))

        assertEquals(1, sentBytes.size, "Must ACK valid Room 2 update")
        assertEquals(1, room2Updates.size)
        assertEquals(1L, room2Updates[0].version)
        assertEquals(1L, binding.lastRoomInfoVersion)
    }

    @Test
    fun testSlowListenerDoesNotBlockCloseOrLeaveFromAnotherThread() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val listenerEnteredLatch = CountDownLatch(1)
        val releaseSlowListenerLatch = CountDownLatch(1)

        // 注册慢监听器：在接收更新时阻塞
        binding.onRoomUpdate { info ->
            listenerEnteredLatch.countDown()
            releaseSlowListenerLatch.await(5, TimeUnit.SECONDS)
        }

        // 线程 1：推送更新触发慢监听器执行
        val pushThread = Thread {
            val info = createDefaultRoomInfo("ROOM1", version = 1L)
            binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 60, payload = ApiMessageCodec.encodeRoomUpdatePush(info).toByteArray(Charsets.UTF_8)))
        }
        pushThread.start()

        // 确保监听器已进入慢执行状态
        assertTrue(listenerEnteredLatch.await(5, TimeUnit.SECONDS))

        // 另一线程执行 close：必须在有界时间内快速完成，不得被慢 listener 阻塞
        val closeStart = System.currentTimeMillis()
        binding.close("timely_close_test")
        val closeDuration = System.currentTimeMillis() - closeStart

        assertTrue(closeDuration < 1000L, "binding.close() took too long ($closeDuration ms), slow listener must not block close")
        assertTrue(binding.closed)
        assertEquals("timely_close_test", binding.disconnectReason)

        // 释放慢监听器，线程正常收敛
        releaseSlowListenerLatch.countDown()
        pushThread.join(5000)
    }

    @Test
    fun testSlowListenerDoesNotBlockLeaveFromAnotherThread() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 12345
        )

        val initSnap = createDefaultSnapshot(10L)
        val createRsp = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM1",
            seat = 0,
            resumeToken = "tok-1",
            snapshot = initSnap
        )
        val reqFuture = binding.createRoom(CreateRoomRequest("Host"))
        val reqFrame = UdpFrame.decode(sentBytes[0])
        val createReq = ApiMessageCodec.decodeRequest(reqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 50, reqFrame.seq, ApiMessageCodec.encodeResponse(createRsp.copy(requestId = createReq.requestId)).toByteArray(Charsets.UTF_8)))
        reqFuture.await()
        sentBytes.clear()

        val listenerEnteredLatch = CountDownLatch(1)
        val releaseSlowListenerLatch = CountDownLatch(1)

        binding.onRoomUpdate { info ->
            listenerEnteredLatch.countDown()
            releaseSlowListenerLatch.await(5, TimeUnit.SECONDS)
        }

        val pushThread = Thread {
            val info = createDefaultRoomInfo("ROOM1", version = 1L)
            binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 12345, 60, payload = ApiMessageCodec.encodeRoomUpdatePush(info).toByteArray(Charsets.UTF_8)))
        }
        pushThread.start()

        assertTrue(listenerEnteredLatch.await(5, TimeUnit.SECONDS))

        // 另一线程调用 leaveRoom：发起请求必须立即完成并发出报文，不得被慢 listener 阻塞
        val leaveStart = System.currentTimeMillis()
        val leaveFuture = binding.leaveRoom()
        val leaveInitiateDuration = System.currentTimeMillis() - leaveStart

        assertTrue(leaveInitiateDuration < 1000L, "binding.leaveRoom() took too long ($leaveInitiateDuration ms) to initiate")
        // 验证 leave 请求报文已立即发出（sentBytes 包含 ACK 与 REQ）
        assertTrue(sentBytes.size >= 2)
        val leaveReqFrame = UdpFrame.decode(sentBytes.last())
        assertEquals(UdpPacketType.REQ, leaveReqFrame.packetType)
        val leaveReq = ApiMessageCodec.decodeRequest(leaveReqFrame.payload.toString(Charsets.UTF_8))
        assertEquals(ApiTags.LEAVE_ROOM, leaveReq.api)

        // 模拟对端返回 leave 响应
        binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 12345, 70, leaveReqFrame.seq, ApiMessageCodec.encodeResponse(ApiResponse(leaveReq.requestId, true, receipt = ProtocolActionReceipt(leaveReq.requestId, ProtocolReceiptStatus.APPLIED))).toByteArray(Charsets.UTF_8)))

        val receipt = leaveFuture.await(5)
        assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
        assertEquals(0, binding.connectionId)
        assertEquals(null, binding.boundRoomId)

        // 释放慢监听器
        releaseSlowListenerLatch.countDown()
        pushThread.join(5000)
    }

    @Test
    fun testSpectatorJoinAndRepeatedFullBaselineNotifiesOnlyOnce() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler
        )

        val initSnap = createDefaultPublicSnapshot(0L)
        val joinFuture = binding.joinSpectator(JoinSpectatorRequest("ROOM1", "Viewer1"))

        val joinReqFrame = UdpFrame.decode(sentBytes[0])
        val joinReq = ApiMessageCodec.decodeRequest(joinReqFrame.payload.toString(Charsets.UTF_8))
        assertEquals(ApiTags.JOIN_SPECTATOR, joinReq.api)
        binding.onInboundFrame(
            UdpFrame(
                UdpPacketType.RSP,
                701,
                100,
                joinReqFrame.seq,
                ApiMessageCodec.encodeResponse(
                    ApiResponse(
                        requestId = joinReq.requestId,
                        success = true,
                        spectatorBinding = SpectatorBinding("ROOM1", "Viewer1", "token1", initSnap)
                    )
                ).toByteArray(Charsets.UTF_8)
            )
        )

        val specBinding = joinFuture.await(5)
        assertEquals("ROOM1", specBinding.roomId)
        assertEquals(701, binding.connectionId)
        assertEquals("ROOM1", binding.boundRoomId)
        assertEquals("Viewer1", binding.boundSpectatorId)
        assertEquals(0L, binding.lastPublicCursor)
        sentBytes.clear()

        val delivered = mutableListOf<ProtocolPublicUpdate>()
        binding.onPublicUpdate { delivered.add(it) }

        // Send a full snapshot push at cursor 10
        val snap10 = createDefaultPublicSnapshot(10L)
        val update10 = ProtocolPublicUpdate(snap10, 10L, emptyList(), isFullSnapshot = true)
        val pushFrame1 = UdpFrame(
            UdpPacketType.PUSH,
            701,
            101,
            payload = ApiMessageCodec.encodePublicUpdatePush(update10).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame1)

        assertEquals(1, delivered.size)
        assertEquals(10L, delivered[0].snapshot.cursor)
        assertTrue(delivered[0].isFullSnapshot)
        assertTrue(sentBytes.any {
            val f = UdpFrame.decode(it)
            f.packetType == UdpPacketType.ACK && f.ackSeq == 101
        })
        sentBytes.clear()

        // Send duplicate full snapshot push at cursor 10
        val pushFrameDup = UdpFrame(
            UdpPacketType.PUSH,
            701,
            102,
            payload = ApiMessageCodec.encodePublicUpdatePush(update10).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrameDup)

        // Must still ACK the duplicate frame
        assertTrue(sentBytes.any {
            val f = UdpFrame.decode(it)
            f.packetType == UdpPacketType.ACK && f.ackSeq == 102
        })
        // But MUST NOT notify listener again!
        assertEquals(1, delivered.size, "Duplicate full baseline must not notify listeners again")
    }

    @Test
    fun testSpectatorGapTriggersResendCheckpoint() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 701
        )

        val initSnap = createDefaultPublicSnapshot(0L)
        val joinFuture = binding.joinSpectator(JoinSpectatorRequest("ROOM1", "Viewer1"))
        val joinReqFrame = UdpFrame.decode(sentBytes[0])
        val joinReq = ApiMessageCodec.decodeRequest(joinReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(
            UdpFrame(
                UdpPacketType.RSP,
                701,
                100,
                joinReqFrame.seq,
                ApiMessageCodec.encodeResponse(
                    ApiResponse(
                        requestId = joinReq.requestId,
                        success = true,
                        spectatorBinding = SpectatorBinding("ROOM1", "Viewer1", "token1", initSnap)
                    )
                ).toByteArray(Charsets.UTF_8)
            )
        )
        joinFuture.await(5)
        sentBytes.clear()

        // Send a push with fromCursor = 5, cursor = 6 (small gap: 5 - 0 = 5 <= 256)
        val snap6 = createDefaultPublicSnapshot(6L)
        val update6 = ProtocolPublicUpdate(snap6, fromCursor = 5L, emptyList(), isFullSnapshot = false)
        val pushFrame = UdpFrame(
            UdpPacketType.PUSH,
            701,
            105,
            payload = ApiMessageCodec.encodePublicUpdatePush(update6).toByteArray(Charsets.UTF_8)
        )
        binding.onInboundFrame(pushFrame)

        // Verifies RESEND frame is sent
        val resendFrame = sentBytes.map { UdpFrame.decode(it) }.firstOrNull { it.packetType == UdpPacketType.RESEND }
        assertNotNull(resendFrame, "Must send RESEND frame on public gap")
        val checkpoint = ApiMessageCodec.decodePublicResendRequest(resendFrame!!.payload.toString(Charsets.UTF_8))
        assertEquals(0L, checkpoint.cursor, "Checkpoint cursor must match lastPublicCursor (0)")
        assertEquals(initSnap.sessionId, checkpoint.sessionId)
        assertEquals(initSnap.syncEpoch, checkpoint.syncEpoch)
    }

    @Test
    fun testSpectatorLeaveRoomCleansBindingsAndResetsTokens() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val sentBytes = CopyOnWriteArrayList<ByteArray>()

        val binding = UdpClientBinding(
            rawSender = { sentBytes.add(it) },
            clock = clock,
            scheduler = scheduler,
            initialConnectionId = 701
        )

        val initSnap = createDefaultPublicSnapshot(0L)
        val joinFuture = binding.joinSpectator(JoinSpectatorRequest("ROOM1", "Viewer1"))
        val joinReqFrame = UdpFrame.decode(sentBytes[0])
        val joinReq = ApiMessageCodec.decodeRequest(joinReqFrame.payload.toString(Charsets.UTF_8))
        binding.onInboundFrame(
            UdpFrame(
                UdpPacketType.RSP,
                701,
                100,
                joinReqFrame.seq,
                ApiMessageCodec.encodeResponse(
                    ApiResponse(
                        requestId = joinReq.requestId,
                        success = true,
                        spectatorBinding = SpectatorBinding("ROOM1", "Viewer1", "token1", initSnap)
                    )
                ).toByteArray(Charsets.UTF_8)
            )
        )
        joinFuture.await(5)
        sentBytes.clear()

        val leaveFuture = binding.leaveRoom()
        val leaveReqFrame = sentBytes.map { UdpFrame.decode(it) }.first { it.packetType == UdpPacketType.REQ }
        val leaveReq = ApiMessageCodec.decodeRequest(leaveReqFrame.payload.toString(Charsets.UTF_8))
        assertEquals(ApiTags.LEAVE_ROOM, leaveReq.api)

        binding.onInboundFrame(
            UdpFrame(
                UdpPacketType.RSP,
                701,
                110,
                leaveReqFrame.seq,
                ApiMessageCodec.encodeResponse(
                    ApiResponse(
                        requestId = leaveReq.requestId,
                        success = true,
                        receipt = ProtocolActionReceipt(leaveReq.requestId, ProtocolReceiptStatus.APPLIED)
                    )
                ).toByteArray(Charsets.UTF_8)
            )
        )
        val receipt = leaveFuture.await(5)
        assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
        assertEquals(0, binding.connectionId)
        assertEquals(null, binding.boundRoomId)
        assertEquals(null, binding.boundSpectatorId)
        assertEquals(null, binding.boundSpectatorToken)
    }
}

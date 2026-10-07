package com.majiang.server.udp

import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.PublicCheckpoint
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.ResendResponse
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.CyclicBarrier
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class UdpRoomTest {

    private val fakeClock = FakeUdpClock(1000L)
    private val monotonicClock = object : MonotonicClock {
        override fun nowMillis(): Long = fakeClock.currentTimeMillis()
    }
    private val address = InetSocketAddress("127.0.0.1", 40000)

    @Test
    fun testHostBindingAndRoomSummary() {
        val room = UdpRoom.standalone(
            roomId = "ROOM1001",
            hostPlayerName = "HostMaster",
            udpClock = fakeClock,
            clock = monotonicClock
        )

        val bindFuture = room.bindHost(
            connectionId = 101,
            remoteAddress = address,
            playerName = "HostMaster"
        )
        val (session, binding) = bindFuture.get()

        assertEquals("ROOM1001", binding.roomId)
        assertEquals(0, binding.seat)
        assertEquals(101, session.connectionId)
        assertNotNull(binding.snapshot)
        assertFalse(room.closed)
        assertFalse(room.isMatchStarted)

        val summary = room.toSummary()
        assertEquals("ROOM1001", summary.roomId)
        assertEquals(1, summary.playerCount)
        assertEquals(4, summary.maxPlayers)
        assertFalse(summary.matchStarted)

        room.close()
        assertTrue(room.closed)
    }

    @Test
    fun testJoinWithPreferSeatAndRollover() {
        val room = UdpRoom.standalone(
            roomId = "ROOM1002",
            hostPlayerName = "Host1",
            udpClock = fakeClock,
            clock = monotonicClock
        )
        room.bindHost(100, address).get()

        // 玩家 1 期望座位 2
        val (sess1, bind1) = room.join(101, address, "Player1", preferSeat = 2).get()
        assertEquals(2, bind1.seat)
        assertEquals(2, sess1.seat)

        // 玩家 2 也期望座位 2 -> 顺延至座位 3
        val (sess2, bind2) = room.join(102, address, "Player2", preferSeat = 2).get()
        assertEquals(3, bind2.seat)
        assertEquals(3, sess2.seat)

        // 玩家 3 再次期望座位 2 -> 顺延至座位 1
        val (sess3, bind3) = room.join(103, address, "Player3", preferSeat = 2).get()
        assertEquals(1, bind3.seat)
        assertEquals(1, sess3.seat)

        assertEquals(4, room.toSummary().playerCount)

        // 满人后第 4 个玩家加入应被拒绝
        val fullEx = assertFailsWith<ExecutionException> {
            room.join(104, address, "Player4", preferSeat = 1).get()
        }
        assertTrue(fullEx.cause?.message?.contains("ROOM_FULL") == true)

        room.close()
    }

    @Test
    fun testStartMatchAuthorizationAndFreeze() {
        val room = UdpRoom.standalone(
            roomId = "ROOM1003",
            hostPlayerName = "HostAdmin",
            udpClock = fakeClock,
            clock = monotonicClock
        )
        val (hostSession, _) = room.bindHost(200, address).get()
        val (guestSession, _) = room.join(201, address, "Guest", preferSeat = 1).get()

        // 普通成员开赛应被拒绝
        val nonHostReceipt = room.startMatch(guestSession).get()
        assertEquals(ProtocolReceiptStatus.REJECTED, nonHostReceipt.status)
        assertEquals("PERMISSION_DENIED", nonHostReceipt.reason)
        assertFalse(room.isMatchStarted)

        // 真人未准备齐时房主开赛应被拒绝 (NOT_READY)
        val unreadyReceipt = room.startMatch(hostSession).get()
        assertEquals(ProtocolReceiptStatus.REJECTED, unreadyReceipt.status)
        assertEquals("NOT_READY", unreadyReceipt.reason)
        assertFalse(room.isMatchStarted)

        // 双方真人均准备就绪
        assertEquals(ProtocolReceiptStatus.APPLIED, room.setReady(guestSession, true).get().status)
        assertEquals(ProtocolReceiptStatus.APPLIED, room.setReady(hostSession, true).get().status)

        // 房主开赛成功，并触发空席位 (seat 2, 3) 自动 AI 托管冻结
        val hostReceipt = room.startMatch(hostSession).get()
        assertEquals(ProtocolReceiptStatus.APPLIED, hostReceipt.status)
        assertTrue(room.isMatchStarted)

        // 开赛后禁止新玩家加入
        val joinAfterStartEx = assertFailsWith<ExecutionException> {
            room.join(202, address, "LatePlayer", preferSeat = 2).get()
        }
        assertTrue(joinAfterStartEx.cause?.message?.contains("ROOM_FULL") == true)

        room.close()
    }

    @Test
    fun testResumeSessionDisplacesOldConnection() {
        val kickLatch = CountDownLatch(1)
        var kickedConnId: Int? = null
        var kickReason: String? = null

        val room = UdpRoom.standalone(
            roomId = "ROOM1004",
            hostPlayerName = "HostRoom",
            udpClock = fakeClock,
            clock = monotonicClock,
            kickSender = { session, reason ->
                kickedConnId = session.connectionId
                kickReason = reason
                kickLatch.countDown()
            }
        )
        room.bindHost(300, address).get()
        val (oldSession, guestBinding) = room.join(301, address, "Bob", preferSeat = 1).get()

        // 使用正确的 resumeToken 顶号重连
        val (newSession, resumeBinding) = room.resume(
            newConnectionId = 302,
            newRemoteAddress = address,
            token = guestBinding.resumeToken
        ).get()

        assertEquals(1, resumeBinding.seat)
        assertEquals(302, newSession.connectionId)
        assertEquals(guestBinding.resumeToken, resumeBinding.resumeToken)

        // 等待异步 KICK 通知与会话关闭完成
        assertTrue(kickLatch.await(3, TimeUnit.SECONDS), "Timed out waiting for displacement kick")
        val deadline = System.currentTimeMillis() + 3000L
        while (!oldSession.isClosed && System.currentTimeMillis() < deadline) {
            Thread.sleep(10L)
        }

        // 验证旧连接已收到 KICK 通知且被标记 displaced，会话关闭，旧代次被作废
        assertEquals(301, kickedConnId)
        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, kickReason)
        assertTrue(oldSession.isDisplaced)
        assertTrue(oldSession.isClosed)
        assertTrue(oldSession.generation < room.getConnectionGeneration(1))

        // 旧连接无法再调用房间命令
        val staleSnapshotEx = assertFailsWith<ExecutionException> {
            room.querySnapshot(oldSession).get()
        }
        assertTrue(staleSnapshotEx.cause?.message?.contains("STALE_SESSION") == true)

        // 新连接能够正常查询快照
        val snapshot = room.querySnapshot(newSession).get()
        assertNotNull(snapshot)

        // 错误 token 重连被拒绝
        val invalidTokenEx = assertFailsWith<ExecutionException> {
            room.resume(303, address, "wrong-token-abc").get()
        }
        assertTrue(invalidTokenEx.cause?.message?.contains(ApiErrors.INVALID_TOKEN) == true)

        room.close()
    }

    @Test
    fun testLeaveRoomHostVsPlayer() {
        val room = UdpRoom.standalone(
            roomId = "ROOM1005",
            hostPlayerName = "HostUser",
            udpClock = fakeClock,
            clock = monotonicClock
        )
        val (hostSession, _) = room.bindHost(400, address).get()
        val (guestSession, _) = room.join(401, address, "GuestUser", preferSeat = 1).get()

        // 普通成员离开：房间不解散，座位移交 AI
        val guestLeaveReceipt = room.leave(guestSession).get()
        assertEquals(ProtocolReceiptStatus.APPLIED, guestLeaveReceipt.status)
        assertFalse(room.closed)
        assertNull(room.getActiveBinding(1))

        // 房主离开：比赛中止，整间房关闭解散
        val hostLeaveReceipt = room.leave(hostSession).get()
        assertEquals(ProtocolReceiptStatus.APPLIED, hostLeaveReceipt.status)
        assertTrue(room.closed)
    }

    @Test
    fun testOnConnectionClosed() {
        val room = UdpRoom.standalone(
            roomId = "ROOM1006",
            hostPlayerName = "HostUser",
            udpClock = fakeClock,
            clock = monotonicClock
        )
        room.bindHost(500, address).get()
        val (guestSession, _) = room.join(501, address, "GuestUser", preferSeat = 1).get()

        assertNotNull(room.getActiveBinding(1))

        // 模拟网络读超时触发的断线清理
        room.onConnectionClosed(guestSession)
        assertNull(room.getActiveBinding(1))

        room.close()
    }

    @Test
    fun testConcurrentOutOfOrderResumePreservesLatestBinding() {
        val kickedList = mutableListOf<Pair<Int, String>>()
        val room = UdpRoom.standalone(
            roomId = "ROOM_CONCURRENT_RESUME",
            hostPlayerName = "HostUser",
            udpClock = fakeClock,
            clock = monotonicClock,
            kickSender = { session, reason ->
                synchronized(kickedList) {
                    kickedList.add(session.connectionId to reason)
                }
            }
        )
        try {
            room.bindHost(600, address).get()
            val (origSession, origBinding) = room.join(601, address, "Guest1", preferSeat = 1).get()
            val token = origBinding.resumeToken

            // 并发发起两次 resume
            // resume1 先进入队列，生成 gen2
            // resume2 后进入队列，生成 gen3
            val f1 = room.resume(602, address, token)
            val f2 = room.resume(603, address, token)

            // 等待两者完成
            val res2 = f2.get()
            assertEquals(603, res2.first.connectionId)

            // 当前活跃绑定必须是最新连接 603
            val active = room.getActiveBinding(1)
            assertNotNull(active)
            assertEquals(603, active.session.connectionId)
            assertFalse(active.session.isClosed)
            assertFalse(active.session.isDisplaced)

            // 601 必须已被 KICK
            assertTrue(origSession.isClosed)
            assertTrue(origSession.isDisplaced)
        } finally {
            room.close()
        }
    }

    @Test
    fun testSlowKickDoesNotBlockOrCorruptNewerResume() {
        val kickStarted = CountDownLatch(1)
        val kickRelease = CountDownLatch(1)
        val room = UdpRoom.standalone(
            roomId = "ROOM_SLOW_KICK",
            hostPlayerName = "HostUser",
            udpClock = fakeClock,
            clock = monotonicClock,
            kickSender = { session, _ ->
                if (session.connectionId == 701) {
                    kickStarted.countDown()
                    kickRelease.await(2, TimeUnit.SECONDS)
                }
            }
        )
        try {
            room.bindHost(700, address).get()
            val (oldSession, binding) = room.join(701, address, "GuestSlow", preferSeat = 1).get()
            val token = binding.resumeToken

            val future = room.resume(702, address, token)
            // kick 执行中，但 resume 绝不应被永久挂起
            kickRelease.countDown()
            val (newSession, _) = future.get(3, TimeUnit.SECONDS)
            assertEquals(702, newSession.connectionId)
            assertEquals(702, room.getActiveBinding(1)?.session?.connectionId)
        } finally {
            kickRelease.countDown()
            room.close()
        }
    }

    @Test
    fun testRoomUpdateRetransmitAndAckClearing() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostUdp")
            val (session, _) = room.bindHost(801, address).get()
            server.registerSession(session)

            val info = room.sharedRoom.getRoomInfoSnapshot()
            server.sendRoomUpdate(session, info)

            // 发送后存在在途记录
            val inFlight = server.getInFlightRoomUpdate(session.connectionId)
            assertNotNull(inFlight)
            val updateSeq = inFlight.seq

            // 收到非匹配 ACK 不得清除在途记录
            val wrongAckFrame = UdpFrame(
                packetType = UdpPacketType.ACK,
                connectionId = session.connectionId,
                seq = 9999,
                ackSeq = updateSeq + 99,
                payload = ByteArray(0)
            )
            server.handleFrame(wrongAckFrame, address)
            assertNotNull(server.getInFlightRoomUpdate(session.connectionId))

            // 收到正确 ackSeq 的 ACK，在途记录被成功清除
            val correctAckFrame = UdpFrame(
                packetType = UdpPacketType.ACK,
                connectionId = session.connectionId,
                seq = 10000,
                ackSeq = updateSeq,
                payload = ByteArray(0)
            )
            server.handleFrame(correctAckFrame, address)
            assertNull(server.getInFlightRoomUpdate(session.connectionId))

            // 较旧版本更新到达不得覆盖较新版本记录
            val olderInfo = info.copy(version = info.version - 1)
            server.sendRoomUpdate(session, olderInfo)
            assertNull(server.getInFlightRoomUpdate(session.connectionId), "Older version room update must be dropped")
        } finally {
            server.close()
        }
    }

    @Test
    fun testConcurrentInterleavedRoomUpdatesDoNotOverwriteNewerVersion() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostConcurrent")
            val (session, _) = room.bindHost(901, address).get()
            server.registerSession(session)
            val baseInfo = room.sharedRoom.getRoomInfoSnapshot()

            // 1. 显式顺序调用下，较旧版本更新无法覆盖较新版本待发记录
            val infoV2 = baseInfo.copy(version = 2L)
            val infoV3 = baseInfo.copy(version = 3L)
            val infoV1 = baseInfo.copy(version = 1L)

            server.sendRoomUpdate(session, infoV2)
            assertEquals(2L, server.getInFlightRoomUpdate(session.connectionId)?.version)
            assertEquals(2L, server.getHighestSentRoomVersion(session.connectionId))

            server.sendRoomUpdate(session, infoV3)
            assertEquals(3L, server.getInFlightRoomUpdate(session.connectionId)?.version)
            assertEquals(3L, server.getHighestSentRoomVersion(session.connectionId))

            // 滞后的 v1 与 v2 被丢弃，在途记录维持 v3
            server.sendRoomUpdate(session, infoV1)
            assertEquals(3L, server.getInFlightRoomUpdate(session.connectionId)?.version)
            server.sendRoomUpdate(session, infoV2)
            assertEquals(3L, server.getInFlightRoomUpdate(session.connectionId)?.version)

            // 2. 多线程高并发乱序提交版本 10..60，最终在途条目必须收敛至最高版本 60L
            val threadCount = 20
            val startLatch = CountDownLatch(1)
            val doneLatch = CountDownLatch(threadCount)
            val versions = (10L..60L).shuffled()

            val executor = java.util.concurrent.Executors.newFixedThreadPool(threadCount)
            for (i in 0 until threadCount) {
                executor.execute {
                    startLatch.await()
                    for (v in versions) {
                        server.sendRoomUpdate(session, baseInfo.copy(version = v))
                    }
                    doneLatch.countDown()
                }
            }

            startLatch.countDown()
            assertTrue(doneLatch.await(5, TimeUnit.SECONDS), "Concurrent room updates timed out")
            executor.shutdown()

            assertEquals(60L, server.getHighestSentRoomVersion(session.connectionId))
            val finalInFlight = server.getInFlightRoomUpdate(session.connectionId)
            assertNotNull(finalInFlight)
            assertEquals(60L, finalInFlight.version)
        } finally {
            server.close()
        }
    }

    @Test
    fun testRoomUpdateCancellationAndNoOrphanRetryOnLeave() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostLeaveRoom")
            val (session, _) = room.bindHost(1001, address).get()
            server.registerSession(session)

            val info = room.sharedRoom.getRoomInfoSnapshot()
            server.sendRoomUpdate(session, info)

            val inFlight = server.getInFlightRoomUpdate(session.connectionId)
            assertNotNull(inFlight)
            assertEquals(1, scheduler.getPendingCount())
            assertFalse(inFlight.future.isDone)

            // 发送 LEAVE_ROOM 请求数据帧
            val leaveReq = ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "leave-1001")
            val leaveFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = session.connectionId,
                seq = 10,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(leaveReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(leaveFrame, address)

            val leaveResponse = waitForResponse(session, leaveReq.requestId)
            assertTrue(leaveResponse.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveResponse.receipt?.status)
            val cleanupDeadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < cleanupDeadline &&
                (!session.isClosed || server.getSession(session.connectionId) != null ||
                    server.getInFlightRoomUpdate(session.connectionId) != null ||
                    server.getHighestSentRoomVersion(session.connectionId) != null ||
                    scheduler.getPendingCount() != 0)
            ) {
                Thread.sleep(10L)
            }

            // 房主成功离房 (APPLIED)：在途房间更新必须立即取消并完成 future，会话关闭并被移除
            assertNull(server.getInFlightRoomUpdate(session.connectionId))
            assertNull(server.getHighestSentRoomVersion(session.connectionId))
            assertTrue(inFlight.future.isDone)
            assertTrue(session.isClosed)
            assertNull(server.getSession(session.connectionId))
            assertEquals(0, scheduler.getPendingCount())

            // 推进时间，绝不触发孤儿重试
            scheduler.advanceTime(10000L)
            assertEquals(0, scheduler.getPendingCount())
        } finally {
            server.close()
        }
    }

    @Test
    fun testRoomUpdateCancellationAndNoOrphanRetryOnKick() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostKickRoom")
            val (session, _) = room.bindHost(1002, address).get()
            server.registerSession(session)

            val info = room.sharedRoom.getRoomInfoSnapshot()
            server.sendRoomUpdate(session, info)

            val inFlight = server.getInFlightRoomUpdate(session.connectionId)
            assertNotNull(inFlight)
            assertEquals(1, scheduler.getPendingCount())
            assertFalse(inFlight.future.isDone)

            // 服务端下发 KICK
            server.sendKick(session, ApiProtocol.DISPLACED_BY_NEW_CONNECTION)

            // 在途房间更新立即取消并完成 future，watermark 被清除
            assertNull(server.getInFlightRoomUpdate(session.connectionId))
            assertNull(server.getHighestSentRoomVersion(session.connectionId))
            assertTrue(inFlight.future.isDone)

            // 推进时间，房间更新定时器已被取消，无孤儿房间更新重发
            scheduler.advanceTime(10000L)
            assertNull(server.getInFlightRoomUpdate(session.connectionId))
        } finally {
            server.close()
        }
    }

    @Test
    fun testRoomUpdateCancellationAndNoOrphanRetryOnTimeout() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostTimeoutRoom")
            val (session, _) = room.bindHost(1003, address).get()
            server.registerSession(session)

            val info = room.sharedRoom.getRoomInfoSnapshot()
            server.sendRoomUpdate(session, info)

            val inFlight = server.getInFlightRoomUpdate(session.connectionId)
            assertNotNull(inFlight)
            assertEquals(1, scheduler.getPendingCount())
            assertFalse(inFlight.future.isDone)

            // 时钟跃迁 11 秒并触发超时检测
            fakeClock.advance(11000L)
            server.checkSessionTimeouts(fakeClock.currentTimeMillis())

            // 超时会话关闭，在途房间更新被取消完成，且不再保留在途记录
            assertTrue(session.isClosed)
            assertNull(server.getSession(session.connectionId))
            assertNull(server.getInFlightRoomUpdate(session.connectionId))
            assertNull(server.getHighestSentRoomVersion(session.connectionId))
            assertTrue(inFlight.future.isDone)

            // 推进时间无孤儿重试
            scheduler.advanceTime(10000L)
            assertEquals(0, scheduler.getPendingCount())
        } finally {
            server.close()
        }
    }

    @Test
    fun testRoomUpdateCancellationAndNoOrphanRetryOnClose() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        val room = server.createRoom("HostCloseRoom")
        val (session, _) = room.bindHost(1004, address).get()
        server.registerSession(session)

        val info = room.sharedRoom.getRoomInfoSnapshot()
        server.sendRoomUpdate(session, info)

        val inFlight = server.getInFlightRoomUpdate(session.connectionId)
        assertNotNull(inFlight)
        assertEquals(1, scheduler.getPendingCount())
        assertFalse(inFlight.future.isDone)

        server.close()

        assertNull(server.getInFlightRoomUpdate(session.connectionId))
        assertNull(server.getHighestSentRoomVersion(session.connectionId))
        assertTrue(inFlight.future.isDone)
        assertTrue(session.isClosed)
        assertEquals(0, scheduler.getPendingCount())

        scheduler.advanceTime(10000L)
        assertEquals(0, scheduler.getPendingCount())
    }

    @Test
    fun testSendRoomUpdateInterleavedWithSessionTeardownDoesNotRecreateEntry() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostBarrierRoom")
            val (session, _) = room.bindHost(1101, address).get()
            server.registerSession(session)
            val info = room.sharedRoom.getRoomInfoSnapshot()

            val barrier = CyclicBarrier(2)
            val threadA = Thread {
                barrier.await()
                for (v in 1L..20L) {
                    server.sendRoomUpdate(session, info.copy(version = v))
                }
            }
            val threadB = Thread {
                barrier.await()
                Thread.sleep(5L)
                fakeClock.advance(11000L)
                server.checkSessionTimeouts(fakeClock.currentTimeMillis())
            }

            threadA.start()
            threadB.start()
            threadA.join(3000L)
            threadB.join(3000L)

            // 终止后绝不重新创建 watermark 或在途条目
            assertTrue(session.isClosed)
            assertNull(server.getInFlightRoomUpdate(session.connectionId))
            assertNull(server.getHighestSentRoomVersion(session.connectionId))
            assertNull(server.getSession(session.connectionId))
            assertEquals(0, scheduler.getPendingCount())
        } finally {
            server.close()
        }
    }

    private fun waitForResponse(
        session: UdpClientSession,
        requestId: String,
        timeoutMs: Long = 3000L
    ): ApiResponse {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            val cached = session.getCachedResponse(requestId)
            if (cached != null) {
                return cached
            }
            Thread.sleep(10L)
        }
        error("Timed out waiting for cached response for requestId=$requestId on connectionId=${session.connectionId}")
    }

    @Test
    fun testLeaveRoomRejectedPreservesActiveBindingAndSession() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(
            clock = fakeClock,
            scheduler = scheduler
        )
        try {
            val room = server.createRoom("HostRejectLeaveRoom")
            val (hostSession, _) = room.bindHost(1201, address).get()
            server.registerSession(hostSession)
            val (guestSession, _) = room.join(1202, address, "GuestBob", preferSeat = 1).get()
            server.registerSession(guestSession)

            // 构造权威 SharedRoom 拒绝离房场景（席位被非正常置空或未就席）：
            // 此时真实 session 向服务端发送 LEAVE_ROOM 将被 SharedRoom 裁决为 REJECTED (NOT_MEMBER)
            room.sharedRoom.seatOccupied[1] = false

            val rejectReq = ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "req-reject-leave")
            val rejectFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = guestSession.connectionId,
                seq = 1,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(rejectReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(rejectFrame, address)

            // 验证异步回执被线程安全观察到，且断言使用 ProtocolReceiptStatus
            val cachedReject = waitForResponse(guestSession, "req-reject-leave")
            assertNotNull(cachedReject)
            assertFalse(cachedReject.success)
            assertEquals(ProtocolReceiptStatus.REJECTED, cachedReject.receipt?.status)
            assertEquals("NOT_MEMBER", cachedReject.error)

            // 权威拒绝不得关闭服务端正常活跃的真人在座会话！
            assertNotNull(server.getSession(guestSession.connectionId))
            assertFalse(guestSession.isClosed)
            assertFalse(guestSession.isDisplaced)

            // 恢复席位真实在座状态，验证真人成员会话仍可正常发送后续业务请求（如 GET_ROOM_INFO）
            room.sharedRoom.seatOccupied[1] = true

            val infoReq = ApiRequest(api = ApiTags.GET_ROOM_INFO, requestId = "req-guest-info")
            val infoFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = guestSession.connectionId,
                seq = 2,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(infoReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(infoFrame, address)
            val cachedInfo = waitForResponse(guestSession, "req-guest-info")
            assertNotNull(cachedInfo)
            assertTrue(cachedInfo.success)
            assertNotNull(cachedInfo.roomInfo)

            // 现在真人发起真正的合法离房
            val validLeaveReq = ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "req-valid-leave")
            val validLeaveFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = guestSession.connectionId,
                seq = 3,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(validLeaveReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(validLeaveFrame, address)

            val cachedApplied = waitForResponse(guestSession, "req-valid-leave")
            assertNotNull(cachedApplied)
            assertTrue(cachedApplied.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, cachedApplied.receipt?.status)

            // 成功离房后，会话方被真正注销与关闭
            assertTrue(guestSession.isClosed)
            assertNull(server.getSession(guestSession.connectionId))
        } finally {
            server.close()
        }
    }

    @Test
    fun testSpectatorJoinResumeAndCheckpointResend() {
        val room = UdpRoom.standalone(
            roomId = "ROOM-SPEC-1",
            hostPlayerName = "HostMaster",
            udpClock = fakeClock,
            clock = monotonicClock
        )
        try {
            room.bindHost(100, address).get()
            room.join(101, address, "P1", preferSeat = 1).get()
            room.join(102, address, "P2", preferSeat = 2).get()
            room.join(103, address, "P3", preferSeat = 3).get()

            // 4 人满房时加入观战
            val (specSession, specBinding) = room.joinSpectator(201, address, "SpecMaster").get()
            assertEquals("ROOM-SPEC-1", specBinding.roomId)
            assertNotNull(specBinding.spectatorId)
            assertNotNull(specBinding.resumeToken)
            assertNotNull(specBinding.snapshot)
            assertEquals(-1, specSession.seat)
            assertEquals(specBinding.spectatorId, specSession.spectatorId)
            assertTrue(room.isSpectatorBindingCurrent(specSession))

            val publishedSnap = room.getPublishedPublicSnapshot()
            assertNotNull(publishedSnap)

            // 严格 PUBLIC_RESEND 校验：合法 checkpoint 使用当前 snapshot.cursor 返回补发记录
            val validCheckpoint = PublicCheckpoint(
                sessionId = publishedSnap.sessionId,
                syncEpoch = publishedSnap.syncEpoch,
                cursor = publishedSnap.cursor
            )
            val resendResp = room.handlePublicResend(specSession, validCheckpoint)
            assertNotNull(resendResp)
            assertTrue(resendResp is ResendResponse.Retransmit || resendResp is ResendResponse.FullSnapshot)

            // 超前游标必须拒绝 (返回 null)
            val futureCheckpoint = PublicCheckpoint(
                sessionId = publishedSnap.sessionId,
                syncEpoch = publishedSnap.syncEpoch,
                cursor = publishedSnap.cursor + 10L
            )
            assertNull(room.handlePublicResend(specSession, futureCheckpoint))

            // 负数游标在构造器边界即被 require 校验拒绝 (抛出 IllegalArgumentException，不能先非法构造导致断言不可达)
            assertFailsWith<IllegalArgumentException> {
                PublicCheckpoint(
                    sessionId = publishedSnap.sessionId,
                    syncEpoch = publishedSnap.syncEpoch,
                    cursor = -1L
                )
            }

            // Long.MAX_VALUE 溢出游标在服务端边界必须拒绝 (返回 null)
            val maxCheckpoint = PublicCheckpoint(
                sessionId = publishedSnap.sessionId,
                syncEpoch = publishedSnap.syncEpoch,
                cursor = Long.MAX_VALUE
            )
            assertNull(room.handlePublicResend(specSession, maxCheckpoint))

            // 错误 syncEpoch 必须拒绝 (返回 null)
            val wrongEpochCheckpoint = PublicCheckpoint(
                sessionId = publishedSnap.sessionId,
                syncEpoch = "WRONG-EPOCH",
                cursor = publishedSnap.cursor
            )
            assertNull(room.handlePublicResend(specSession, wrongEpochCheckpoint))

            // 错误 sessionId 必须拒绝 (返回 null)
            val wrongSessionCheckpoint = PublicCheckpoint(
                sessionId = "WRONG-SESSION",
                syncEpoch = publishedSnap.syncEpoch,
                cursor = publishedSnap.cursor
            )
            assertNull(room.handlePublicResend(specSession, wrongSessionCheckpoint))

            // 凭合法 token 恢复观战
            val (resumedSession, resumedBinding) = room.resumeSpectator(
                newConnectionId = 202,
                newRemoteAddress = address,
                spectatorId = specBinding.spectatorId,
                token = specBinding.resumeToken,
                checkpoint = validCheckpoint
            ).get()
            assertEquals(specBinding.spectatorId, resumedBinding.spectatorId)
            assertTrue(room.isSpectatorBindingCurrent(resumedSession))
            assertFalse(room.isSpectatorBindingCurrent(specSession))

            // 错误 token 恢复观战必须失败
            val badTokenEx = assertFailsWith<ExecutionException> {
                room.resumeSpectator(203, address, specBinding.spectatorId, "BAD_TOKEN").get()
            }
            assertTrue(badTokenEx.cause?.message?.contains("INVALID_TOKEN") == true)

            // 错误 checkpoint 恢复观战必须失败
            val badCheckpointEx = assertFailsWith<ExecutionException> {
                room.resumeSpectator(
                    newConnectionId = 204,
                    newRemoteAddress = address,
                    spectatorId = specBinding.spectatorId,
                    token = resumedBinding.resumeToken,
                    checkpoint = futureCheckpoint
                ).get()
            }
            assertTrue(badCheckpointEx.cause?.message?.contains("INVALID_TOKEN") == true)
        } finally {
            room.close()
        }
    }

    @Test
    fun testUdpTableServerSpectatorEndToEnd() {
        val scheduler = FakeUdpScheduler(fakeClock)
        val server = UdpTableServer(clock = fakeClock, scheduler = scheduler)
        val outboundResponses = ConcurrentLinkedQueue<ApiResponse>()
        server.outboundFrameListener = { frame, _ ->
            if (frame.packetType == UdpPacketType.RSP) {
                try {
                    val resp = ApiMessageCodec.decodeResponse(String(frame.payload, Charsets.UTF_8))
                    outboundResponses.add(resp)
                } catch (_: Throwable) {
                }
            }
        }

        fun waitForOutboundResponse(requestId: String, timeoutMs: Long = 3000L): ApiResponse {
            val deadline = System.currentTimeMillis() + timeoutMs
            while (System.currentTimeMillis() < deadline) {
                val found = outboundResponses.firstOrNull { it.requestId == requestId }
                if (found != null) {
                    return found
                }
                Thread.sleep(10L)
            }
            error("Timed out waiting for outbound response for requestId=$requestId")
        }

        try {
            val room = server.createRoom("HostSpecRoom")
            val (hostSession, _) = room.bindHost(1301, address).get()
            server.registerSession(hostSession)

            // 发送 JOIN_SPECTATOR REQ
            val joinSpecReq = ApiRequest(
                api = ApiTags.JOIN_SPECTATOR,
                requestId = "req-join-spec",
                roomId = room.sharedRoom.roomId,
                spectatorName = "AliceSpec"
            )
            val joinFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = 0,
                seq = 1,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(joinSpecReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(joinFrame, address)

            // 观察真实异步完成后的实际加入响应（不抢在完成前空读 spectators）
            val joinRsp = waitForOutboundResponse("req-join-spec")
            assertTrue(joinRsp.success)
            val specBinding = joinRsp.spectatorBinding
            assertNotNull(specBinding)
            assertEquals(room.sharedRoom.roomId, specBinding.roomId)
            assertNotNull(specBinding.spectatorId)
            assertNotNull(specBinding.resumeToken)
            assertNotNull(specBinding.snapshot)

            val actualSpecSession = room.getSpectatorSession(specBinding.spectatorId)
            assertNotNull(actualSpecSession)
            val specConnId = actualSpecSession.connectionId
            assertEquals(specBinding.spectatorId, actualSpecSession.spectatorId)
            assertTrue(room.isSpectatorBindingCurrent(actualSpecSession))

            val cachedJoin = actualSpecSession.getCachedResponse("req-join-spec")
            assertNotNull(cachedJoin)
            assertTrue(cachedJoin.success)
            assertNotNull(cachedJoin.spectatorBinding)

            // 发送 PUBLIC_SNAPSHOT REQ
            val pubReq = ApiRequest(api = ApiTags.PUBLIC_SNAPSHOT, requestId = "req-pub-snap")
            val pubFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = specConnId,
                seq = 2,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(pubReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(pubFrame, address)

            val pubRsp = waitForOutboundResponse("req-pub-snap")
            assertTrue(pubRsp.success)
            assertNotNull(pubRsp.publicSnapshot)
            assertNull(pubRsp.snapshot)
            assertNull(pubRsp.events)

            val cachedPub = actualSpecSession.getCachedResponse("req-pub-snap")
            assertNotNull(cachedPub)
            assertTrue(cachedPub.success)
            assertNotNull(cachedPub.publicSnapshot)
            assertNull(cachedPub.snapshot)

            // 观战会话调用玩家私有接口 SET_READY 必须被角色门禁拒绝
            val readyReq = ApiRequest(api = ApiTags.SET_READY, requestId = "req-spec-ready", ready = true)
            val readyFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = specConnId,
                seq = 3,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(readyReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(readyFrame, address)

            // 观察实际 RSP，断言 UNAUTHENTICATED_SESSION 拒绝
            val readyRsp = waitForOutboundResponse("req-spec-ready")
            assertFalse(readyRsp.success)
            assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, readyRsp.error)

            // 角色越权/错误请求绝不写入会话成功幂等缓存
            assertNull(actualSpecSession.getCachedResponse("req-spec-ready"))

            // 玩家会话调用观战接口 PUBLIC_SNAPSHOT 必须被角色门禁拒绝
            val playerPubReq = ApiRequest(api = ApiTags.PUBLIC_SNAPSHOT, requestId = "req-player-pub")
            val playerPubFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = hostSession.connectionId,
                seq = 1,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(playerPubReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(playerPubFrame, address)

            val playerPubRsp = waitForOutboundResponse("req-player-pub")
            assertFalse(playerPubRsp.success)
            assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, playerPubRsp.error)

            // 越权请求同样不写入玩家会话成功缓存
            assertNull(hostSession.getCachedResponse("req-player-pub"))

            // 观战会话显式 LEAVE_ROOM
            val leaveReq = ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "req-spec-leave")
            val leaveFrame = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = specConnId,
                seq = 4,
                ackSeq = 0,
                payload = ApiMessageCodec.encodeRequest(leaveReq).toByteArray(Charsets.UTF_8)
            )
            server.handleFrame(leaveFrame, address)

            val leaveRsp = waitForOutboundResponse("req-spec-leave")
            assertTrue(leaveRsp.success)
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveRsp.receipt?.status)

            // 观战离开后会话关闭、解绑并从服务端注销
            assertTrue(actualSpecSession.isClosed)
            assertNull(server.getSession(actualSpecSession.connectionId))
            assertFalse(room.isSpectatorBindingCurrent(actualSpecSession))
        } finally {
            server.close()
        }
    }
}

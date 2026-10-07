package com.majiang.server.inprocess

import com.majiang.match.MatchConfig
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import com.majiang.stacking.ShuffleConfig
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShuffleSelection
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNotSame
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit

/**
 * 进程内单机绑定与服务器测试 (InProcessBindingTest)
 *
 * 验收目标：
 * 1. 端到端流程：createLocalTable -> snapshot -> start -> decision -> submit(弃牌) -> onUpdate(游标推进) -> close；
 * 2. 幂等性：同一 requestId 重复 submit 幂等重放原回执；
 * 3. 释放语义：close 后 closed=true，disconnectReason 稳定，后续交互一律拒绝/异常完成；
 * 4. 单机大厅语义：listRooms/createRoom/joinRoom/resumeSession 返回基于当前快照的 RoomBinding，leaveRoom 返回 LOCAL_TABLE_UNSUPPORTED；
 * 5. 生命周期与 DTO 深拷贝隔离。
 */
@Timeout(15)
class InProcessBindingTest {

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    private fun com.majiang.transport.api.ProtocolPlayerView.closedHandSize(): Int =
        myClosedTiles.size + listOfNotNull(myDrawnTile).size

    @Test
    fun `end to end local table flow and idempotent discard submission`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(
                config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
                humanSeat = 0
            )

            assertFalse(conn.closed)
            assertNull(conn.disconnectReason)

            val cap = conn.capabilities().await()
            assertEquals(ApiProtocol.PROTOCOL_VERSION, cap.protocolVersion)
            assertFalse(cap.remoteTransport)

            val snapBefore = conn.snapshot().await()
            assertEquals(0, snapBefore.viewerSeat)
            assertEquals(0L, snapBefore.cursor)

            val receivedUpdates = CopyOnWriteArrayList<ProtocolPlayerUpdate>()
            val updateSub = conn.onUpdate { receivedUpdates.add(it) }

            val startReceipt = conn.start().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

            val decision = conn.decision().await()
            assertTrue(decision.options.isNotEmpty(), "Decision options must not be empty after start")

            val discardOption = decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
            val choice = ProtocolActionChoice(
                requestId = "req-test-discard-1",
                roundId = decision.roundId ?: "",
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                optionId = discardOption.optionId
            )

            val receipt1 = conn.submit(choice).await()
            assertTrue(
                receipt1.status in setOf(ProtocolReceiptStatus.APPLIED, ProtocolReceiptStatus.RECORDED),
                "Expected successful discard status (APPLIED or RECORDED), got ${receipt1.status}"
            )
            assertNotNull(receipt1.cursor)
            assertTrue(receipt1.cursor!! > 0L)

            // 验证 onUpdate 收到游标前进的 PlayerUpdate（通过有界等待匹配预期游标）
            val expectedCursor = receipt1.cursor!!
            var matchedUpdate: ProtocolPlayerUpdate? = null
            val deadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < deadline) {
                matchedUpdate = receivedUpdates.find { it.snapshot.cursor == expectedCursor }
                if (matchedUpdate != null) break
                Thread.sleep(10)
            }
            assertNotNull(matchedUpdate, "Update with cursor $expectedCursor should be received within timeout")
            assertEquals(expectedCursor, matchedUpdate!!.snapshot.cursor)

            // 幂等验证：重复提交相同 requestId，重放原回执
            val receipt2 = conn.submit(choice).await()
            assertEquals(receipt1, receipt2)

            val snapAfter = conn.snapshot().await()
            assertEquals(receipt1.cursor, snapAfter.cursor)
            assertEquals(1, snapAfter.view.discards[0].size)

            val disconnectReasons = CopyOnWriteArrayList<String>()
            val disconnectSub = conn.onDisconnect { disconnectReasons.add(it) }

            conn.close()
            assertTrue(conn.closed)
            assertEquals(ApiProtocol.LOCAL_CLOSED, conn.disconnectReason)
            assertEquals(listOf(ApiProtocol.LOCAL_CLOSED), disconnectReasons)

            // 再次调用 close 具备幂等性
            conn.close()
            assertEquals(1, disconnectReasons.size)

            // 关闭后注册 onDisconnect 立即同步回调
            val lateReasons = CopyOnWriteArrayList<String>()
            conn.onDisconnect { lateReasons.add(it) }
            assertEquals(listOf(ApiProtocol.LOCAL_CLOSED), lateReasons)

            // 关闭后写操作一律返回 REJECTED(SESSION_CLOSED)
            val rejectedSubmit = conn.submit(choice).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, rejectedSubmit.status)
            assertEquals("SESSION_CLOSED", rejectedSubmit.reason)

            val rejectedStart = conn.start().await()
            assertEquals(ProtocolReceiptStatus.REJECTED, rejectedStart.status)
            assertEquals("SESSION_CLOSED", rejectedStart.reason)

            val rejectedNextRound = conn.nextRound().await()
            assertEquals(ProtocolReceiptStatus.REJECTED, rejectedNextRound.status)
            assertEquals("SESSION_CLOSED", rejectedNextRound.reason)

            val rejectedControl = conn.claimControl().await()
            assertEquals(ProtocolReceiptStatus.REJECTED, rejectedControl.status)
            assertEquals("SESSION_CLOSED", rejectedControl.reason)

            val rejectedLeave = conn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.REJECTED, rejectedLeave.status)
            assertEquals("SESSION_CLOSED", rejectedLeave.reason)

            // 关闭后查询操作一律异常完成
            val snapEx = assertThrows(ExecutionException::class.java) { conn.snapshot().await() }
            assertTrue(snapEx.cause is IllegalStateException)

            val decEx = assertThrows(ExecutionException::class.java) { conn.decision().await() }
            assertTrue(decEx.cause is IllegalStateException)

            val resEx = assertThrows(ExecutionException::class.java) { conn.resume(0L).await() }
            assertTrue(resEx.cause is IllegalStateException)

            val capEx = assertThrows(ExecutionException::class.java) { conn.capabilities().await() }
            assertTrue(capEx.cause is IllegalStateException)

            val createEx = assertThrows(ExecutionException::class.java) { conn.createRoom(CreateRoomRequest()).await() }
            assertTrue(createEx.cause is IllegalStateException)

            val joinEx = assertThrows(ExecutionException::class.java) { conn.joinRoom(JoinRoomRequest(roomId = "DUMMY")).await() }
            assertTrue(joinEx.cause is IllegalStateException)

            val resumeEx = assertThrows(ExecutionException::class.java) { conn.resumeSession(ResumeRequest("DUMMY", "DUMMY")).await() }
            assertTrue(resumeEx.cause is IllegalStateException)

            assertTrue(conn.queryRoundResult("round-dummy").await().isEmpty())
            assertTrue(conn.listRooms().await().isEmpty())
        }
    }

    @Test
    fun `lobby api single player semantics`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(humanSeat = 0)
            val binding = conn as InProcessBinding
            val activeRoom = binding.currentRoom!!

            val rooms = conn.listRooms().await()
            assertEquals(1, rooms.size)
            assertEquals(activeRoom.roomId, rooms[0].roomId)
            assertEquals("${activeRoom.hostPlayerName}的麻将房", rooms[0].roomName)
            assertEquals(4, rooms[0].playerCount) // 1 human + 3 AI

            // 已在房间内时，直接 createRoom 或 joinRoom 均被拒绝 (ALREADY_IN_ROOM)
            val createEx = assertThrows(ExecutionException::class.java) { conn.createRoom(CreateRoomRequest()).await() }
            assertTrue(createEx.cause is IllegalStateException)
            assertTrue(createEx.cause?.message?.contains("ALREADY_IN_ROOM") == true)

            val joinEx = assertThrows(ExecutionException::class.java) { conn.joinRoom(JoinRoomRequest(roomId = activeRoom.roomId)).await() }
            assertTrue(joinEx.cause is IllegalStateException)
            assertTrue(joinEx.cause?.message?.contains("ALREADY_IN_ROOM") == true)

            // 错误/伪造的 resumeToken 恢复会话被拒绝
            val wrongTokenEx = assertThrows(ExecutionException::class.java) {
                conn.resumeSession(ResumeRequest(activeRoom.roomId, "WRONG_TOKEN")).await()
            }
            assertTrue(wrongTokenEx.cause is IllegalStateException)

            // 正确的 resumeToken 恢复会话成功
            val validToken = activeRoom.resumeTokens[0]
            val boundResume = conn.resumeSession(ResumeRequest(activeRoom.roomId, validToken)).await()
            assertEquals(activeRoom.roomId, boundResume.roomId)
            assertEquals(0, boundResume.seat)
            assertEquals(validToken, boundResume.resumeToken)
            assertNotNull(boundResume.snapshot)

            val leaveReceipt = conn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
            assertFalse(conn.closed, "Connection must remain open after leaving room")

            // 显式离开后，原 token 立即失效，再次尝试恢复被拒绝
            val resumeAfterLeaveEx = assertThrows(ExecutionException::class.java) {
                conn.resumeSession(ResumeRequest(activeRoom.roomId, validToken)).await()
            }
            assertTrue(resumeAfterLeaveEx.cause is IllegalStateException)

            // 在同一连接上重新建房
            val boundRecreate = conn.createRoom(CreateRoomRequest(playerName = "Rehost")).await()
            assertNotNull(boundRecreate.roomId)
            assertEquals(0, boundRecreate.seat)
            assertNotNull(boundRecreate.snapshot)

            conn.close()
            val afterCloseLeave = conn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.REJECTED, afterCloseLeave.status)
            assertEquals("SESSION_CLOSED", afterCloseLeave.reason)
        }
    }

    @Test
    fun `server lifecycle manages table replacement and close`() {
        val server = InProcessServer()
        assertFalse(server.closed)
        assertTrue(server.listRooms().isEmpty())

        val conn1 = server.createLocalTable(humanSeat = 0)
        assertFalse(conn1.closed)
        assertEquals(1, server.listRooms().size)
        assertNotNull(server.currentHost)
        assertNotNull(server.currentConnection)

        // 创建新牌桌应干净关闭上一桌
        val conn2 = server.createLocalTable(humanSeat = 1)
        assertTrue(conn1.closed)
        assertEquals(ApiProtocol.LOCAL_CLOSED, conn1.disconnectReason)
        assertFalse(conn2.closed)
        assertEquals(1, (conn2 as InProcessBinding).viewerSeat)

        server.close()
        assertTrue(server.closed)
        assertTrue(conn2.closed)
        assertTrue(server.listRooms().isEmpty())
        assertNull(server.currentHost)
        assertNull(server.currentConnection)

        assertThrows(IllegalStateException::class.java) {
            server.createLocalTable()
        }
    }

    @Test
    fun `returned dtos are defensive deep copies`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable()

            val cap1 = conn.capabilities().await()
            val cap2 = conn.capabilities().await()
            assertNotSame(cap1, cap2)
            assertEquals(cap1, cap2)

            val snap1 = conn.snapshot().await()
            val snap2 = conn.snapshot().await()
            assertNotSame(snap1, snap2)
            assertEquals(snap1.cursor, snap2.cursor)

            val dec1 = conn.decision().await()
            val dec2 = conn.decision().await()
            assertNotSame(dec1, dec2)

            conn.close()
        }
    }

    @Test
    fun `shuffle plan submission during preparation phase`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(
                config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = true)),
                humanSeat = 0
            )

            val startReceipt = conn.start().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

            val snap = conn.snapshot().await()
            assertNotNull(snap.roundId)

            val plan = ProtocolShufflePlan(
                requestId = "plan-test-1",
                roundId = snap.roundId!!,
                controlEpoch = snap.decision.controlEpoch,
                selection = ProtocolShuffleSelection()
            )

            val receipt = conn.submitShufflePlan(plan).await()
            assertEquals(ProtocolReceiptStatus.RECORDED, receipt.status)

            conn.close()
        }
    }

    @Test
    fun `requestControl translates v4 reclaim semantics`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(humanSeat = 0)
            val startReceipt = conn.start().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

            // V4 releaseControl (or requestControl(false)) 代表交出控制托管给 AI
            val delegateReceipt = conn.releaseControl().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, delegateReceipt.status)

            // V4 claimControl (or requestControl(true)) 代表收回人工控制
            val reclaimReceipt = conn.claimControl().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, reclaimReceipt.status)

            conn.close()
        }
    }

    @Test
    fun `unbound connection creates, leaves, and joins rooms on same channel`() {
        InProcessServer().use { server ->
            val conn = server.createConnection()
            assertFalse(conn.closed)

            val bound1 = conn.createRoom(CreateRoomRequest(playerName = "Player1")).await()
            val room1Id = bound1.roomId
            assertEquals(0, bound1.seat)

            val leave1 = conn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leave1.status)
            assertFalse(conn.closed)

            // 创建第二个房间
            val bound2 = conn.createRoom(CreateRoomRequest(playerName = "Player1")).await()
            val room2Id = bound2.roomId
            assertTrue(room1Id != room2Id)

            val leave2 = conn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leave2.status)
            assertFalse(conn.closed)

            conn.close()
            assertTrue(conn.closed)
        }
    }

    @Test
    fun `local host can leave and create another room on the live connection`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(humanSeat = 0)
            assertFalse(conn.closed)

            val leaveReceipt = conn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
            assertFalse(conn.closed, "Connection must remain open after leaving room")

            // 在同一条活跃连接上创建第二个房间
            val boundNew = conn.createRoom(CreateRoomRequest(playerName = "Host2")).await()
            assertNotNull(boundNew.roomId)
            assertEquals(0, boundNew.seat)
            assertNotNull(boundNew.snapshot)

            // 验证继承了服务端的单人策略，开赛直接成功（无需等待其他玩家）
            val startReceipt = conn.start().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

            // 服务端当前活跃牌桌与时钟能够步进
            val ticked = server.tick(presentationBusy = false).await()
            assertNotNull(ticked)

            val decision = conn.decision().await()
            assertTrue(decision.options.isNotEmpty())

            conn.close()
            assertTrue(conn.closed)
        }
    }

    @Test
    fun `foreign room or wrong token resume fails without losing current binding`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val guestConn = server.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId = hostBound.roomId, playerName = "Guest")).await()
            val guestSeat = guestBound.seat
            val guestToken = guestBound.resumeToken

            // 1. 尝试使用不存在的房间号恢复，失败但保留原有 guestConn 绑定
            val wrongRoomEx = assertThrows(ExecutionException::class.java) {
                guestConn.resumeSession(ResumeRequest("NON_EXISTENT", guestToken)).await()
            }
            assertTrue(wrongRoomEx.cause is IllegalStateException)
            assertEquals(guestSeat, (guestConn as InProcessBinding).viewerSeat)
            assertEquals(hostBound.roomId, (guestConn as InProcessBinding).currentRoom?.roomId)

            // 2. 尝试使用错误 token 恢复，失败但保留原有 guestConn 绑定
            val wrongTokenEx = assertThrows(ExecutionException::class.java) {
                guestConn.resumeSession(ResumeRequest(hostBound.roomId, "FAKE_TOKEN")).await()
            }
            assertTrue(wrongTokenEx.cause is IllegalStateException)
            assertEquals(guestSeat, (guestConn as InProcessBinding).viewerSeat)
            assertEquals(hostBound.roomId, (guestConn as InProcessBinding).currentRoom?.roomId)

            hostConn.close()
            guestConn.close()
        }
    }

    @Test
    fun `closing guest transport preserves host room and valid resume token for new connection`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val guestConn1 = server.createConnection()
            val guestBound1 = guestConn1.joinRoom(JoinRoomRequest(roomId = hostBound.roomId, playerName = "Guest")).await()
            val guestSeat = guestBound1.seat
            val guestToken = guestBound1.resumeToken

            // 模拟访客传输层意外关闭（未调用 leaveRoom）
            guestConn1.close()
            assertTrue(guestConn1.closed)

            // 房主房间依然存活，并未被关闭
            val hostRoomInfo = hostConn.getRoomInfo().await()
            assertNotNull(hostRoomInfo)
            assertFalse(hostRoomInfo.lifecycle == ProtocolRoomLifecycle.CLOSED)

            // 新连接接入并恢复原席位
            val guestConn2 = server.createConnection()
            assertFalse(guestConn2.closed)

            val resumedBound = guestConn2.resumeSession(ResumeRequest(hostBound.roomId, guestToken)).await()
            assertEquals(hostBound.roomId, resumedBound.roomId)
            assertEquals(guestSeat, resumedBound.seat)
            assertEquals(guestToken, resumedBound.resumeToken)
            assertNotNull(resumedBound.snapshot)

            hostConn.close()
            guestConn2.close()
        }
    }

    @Test
    fun `last guest endpoint close takes over until resumed player explicitly reclaims control`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val host = server.createConnection()
            val room = host.createRoom(CreateRoomRequest(playerName = "Host")).await()
            val guest = server.createConnection()
            val binding = guest.joinRoom(JoinRoomRequest(roomId = room.roomId, playerName = "Guest")).await()
            val seat = binding.seat
            try {
                assertEquals(ProtocolReceiptStatus.APPLIED, host.setReady(true).await().status)
                assertEquals(ProtocolReceiptStatus.APPLIED, guest.setReady(true).await().status)
                assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                assertEquals("HUMAN", host.snapshot().await().controls.first { it.seat == seat }.role)

                guest.close()
                val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3)
                var takeover = host.snapshot().await().controls.first { it.seat == seat }
                while ((takeover.role != "TAKEOVER" || takeover.connected) && System.nanoTime() < deadline) {
                    Thread.sleep(10)
                    takeover = host.snapshot().await().controls.first { it.seat == seat }
                }
                assertEquals("TAKEOVER", takeover.role)
                assertFalse(takeover.connected)

                server.createConnection().use { resumed ->
                    assertEquals(seat, resumed.resumeSession(ResumeRequest(room.roomId, binding.resumeToken)).await().seat)
                    val beforeClaim = resumed.snapshot().await().controls.first { it.seat == seat }
                    assertEquals("TAKEOVER", beforeClaim.role, "Reconnect alone must not reclaim human control")
                    assertTrue(beforeClaim.connected)
                    assertEquals(ProtocolReceiptStatus.APPLIED, resumed.claimControl().await().status)
                    val human = host.snapshot().await().controls.first { it.seat == seat }
                    assertEquals("HUMAN", human.role)
                    assertTrue(human.connected)
                    assertTrue(human.controlEpoch > takeover.controlEpoch)
                }
            } finally {
                guest.close()
                host.close()
            }
        }
    }

    @Test
    fun `explicit leave before and after start makes old token unrecoverable`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val guestConn = server.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId = hostBound.roomId, playerName = "Guest")).await()
            val guestToken = guestBound.resumeToken

            // 大厅期显式离开
            val leaveReceipt = guestConn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)

            // 尝试使用旧 token 恢复，必然被拒绝
            val resumeEx = assertThrows(ExecutionException::class.java) {
                guestConn.resumeSession(ResumeRequest(hostBound.roomId, guestToken)).await()
            }
            assertTrue(resumeEx.cause is IllegalStateException)

            hostConn.close()
            guestConn.close()
        }
    }

    @Test
    fun `host leave succeeds while guest receives room closed without UI subscription`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val guestConn = server.createConnection()
            guestConn.joinRoom(JoinRoomRequest(roomId = hostBound.roomId, playerName = "Guest")).await()

            val guestDisconnectLatch = CountDownLatch(1)
            val guestDisconnectReasons = CopyOnWriteArrayList<String>()
            guestConn.onDisconnect {
                guestDisconnectReasons.add(it)
                guestDisconnectLatch.countDown()
            }

            // 房主显式离开
            val hostLeaveReceipt = hostConn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, hostLeaveReceipt.status)
            assertFalse(hostConn.closed, "Host connection must remain live after leaveRoom")
            assertNull((hostConn as InProcessBinding).currentRoom, "Host connection must be unbound after leaveRoom")

            // 有界等待真实 onDisconnect 回调（锁外 listener 调度执行）
            assertTrue(guestDisconnectLatch.await(3, TimeUnit.SECONDS), "Guest must receive onDisconnect callback")
            assertTrue(guestConn.closed, "Guest connection must be closed after host left")
            assertEquals(ApiProtocol.ROOM_CLOSED, guestConn.disconnectReason)
            assertTrue(guestDisconnectReasons.contains(ApiProtocol.ROOM_CLOSED))

            // 访客后续动作一律被拒绝
            val rejectedReady = guestConn.setReady(true).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, rejectedReady.status)
            assertEquals("SESSION_CLOSED", rejectedReady.reason)

            // 房主连接依然活跃，可在同一连接上创建新房间
            val hostNewBound = hostConn.createRoom(CreateRoomRequest(playerName = "HostAgain")).await()
            assertNotNull(hostNewBound)
            assertFalse(hostConn.closed)

            hostConn.close()
        }
    }

    @Test
    fun `replacement resume prevents previous connection from changing readiness`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val guestConn1 = server.createConnection()
            val guestBound1 = guestConn1.joinRoom(JoinRoomRequest(roomId = hostBound.roomId, playerName = "Guest")).await()
            val guestToken = guestBound1.resumeToken

            // 访客 1 准备就绪
            val readyReceipt = guestConn1.setReady(true).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, readyReceipt.status)

            // 新连接携带虚假未来游标 999999 恢复席位
            val guestConn2 = server.createConnection()
            val resumedBound2 = guestConn2.resumeSession(ResumeRequest(hostBound.roomId, guestToken, cursor = 999999L)).await()
            assertEquals(guestBound1.seat, resumedBound2.seat)
            // 权威快照必须返回服务端当前真实游标，而非客户端虚构的 999999
            assertEquals(0L, resumedBound2.snapshot.cursor)

            // 被顶替的旧连接尝试取消就绪，必然被拒绝 (STALE_SESSION 或 SESSION_CLOSED)
            val staleReadyReceipt = guestConn1.setReady(false).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, staleReadyReceipt.status)

            // 房主通过真实 API 完成准备（不放宽生产就绪门槛）
            val hostReadyReceipt = hostConn.setReady(true).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, hostReadyReceipt.status)

            // 席位保持就绪，房主开赛成功
            val startReceipt = hostConn.start().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)

            hostConn.close()
            guestConn1.close()
            guestConn2.close()
        }
    }

    @Test
    fun `repeated leave create and start cycles keep the same connection live`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(humanSeat = 0)

            // 循环放大离房/重建竞态窗口：每次都必须在同一连接上完成建房与开局
            repeat(10) { round ->
                assertEquals(ProtocolReceiptStatus.APPLIED, conn.leaveRoom().await().status)
                assertFalse(conn.closed, "Connection must stay live after leave in round $round")
                assertNull((conn as InProcessBinding).currentRoom)

                val bound = conn.createRoom(CreateRoomRequest(playerName = "Host-$round")).await()
                assertEquals(0, bound.seat)

                assertEquals(ProtocolReceiptStatus.APPLIED, conn.start().await().status,
                    "Singleplayer assembly auto-readies the rebound host in round $round")
                assertEquals(14, conn.snapshot().await().view.closedHandSize())
                assertFalse(conn.closed)
            }

            conn.close()
            assertTrue(conn.closed)
        }
    }

    @Test
    fun `suspended retired-room observer does not block leave or rebind`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(humanSeat = 0)
            val binding = conn as InProcessBinding
            val retiredRoom = binding.currentRoom!!

            // 可控屏障：挂起一个独立的房间级慢观察者（不阻塞绑定自身订阅），
            // 覆盖慢观察者隔离下的离房/重建语义；旧绑定迟到回调的确定性覆盖见下一个用例
            val terminalEntered = CountDownLatch(1)
            val releaseTerminal = CountDownLatch(1)
            val retiredSub = retiredRoom.onRoomUpdate { info ->
                if (info.lifecycle == ProtocolRoomLifecycle.CLOSED) {
                    terminalEntered.countDown()
                    check(releaseTerminal.await(8, TimeUnit.SECONDS)) { "Retired-room terminal barrier was never released" }
                }
            }
            val disconnectReasons = CopyOnWriteArrayList<String>()
            val disconnectSub = conn.onDisconnect { disconnectReasons.add(it) }

            try {
                // 房主离房：回执立即 APPLIED，不被挂起的慢观察者阻塞，连接存活且解绑完成
                val leaveReceipt = conn.leaveRoom().await()
                assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
                assertTrue(terminalEntered.await(3, TimeUnit.SECONDS),
                    "Terminal info must reach room-level observers")
                assertFalse(conn.closed, "Connection must stay live after leave receipt")
                assertNull(binding.currentRoom)

                // 屏障仍挂起期间，在同一直连上重建并启动新房
                val rebound = conn.createRoom(CreateRoomRequest(playerName = "Host2")).await()
                assertEquals(0, rebound.seat)
                val newRoomId = binding.currentRoom!!.roomId
                assertTrue(newRoomId != retiredRoom.roomId)
                assertEquals(ProtocolReceiptStatus.APPLIED, conn.start().await().status)

                // 释放慢观察者：连接与新绑定不受任何影响
                releaseTerminal.countDown()

                assertFalse(conn.closed, "Slow retired-room observer must not affect the rebound connection")
                assertEquals(newRoomId, binding.currentRoom?.roomId)
                assertTrue(disconnectReasons.isEmpty(), "No disconnect may be synthesized by stale callbacks")
                assertEquals(14, conn.snapshot().await().view.closedHandSize())
            } finally {
                releaseTerminal.countDown()
                disconnectSub.close()
                retiredSub.close()
            }
        }
    }

    @Test
    fun `stale binding callback after rebind is dropped but real replacement still claims closure`() {
        InProcessServer().use { server ->
            val conn = server.createLocalTable(humanSeat = 0)
            val binding = conn as InProcessBinding

            // 换房前捕获旧绑定身份（与 bindRoom 生产回调使用同一上下文结构）
            val staleCtx = binding.captureCurrentCallbackContext()!!

            assertEquals(ProtocolReceiptStatus.APPLIED, conn.leaveRoom().await().status)
            assertFalse(conn.closed)
            val rebound = conn.createRoom(CreateRoomRequest(playerName = "Host2")).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, conn.start().await().status)

            // 换房后以旧绑定上下文执行迟到回调（真实内部处理入口）：直接丢弃，不误关、不投递
            var delivered = false
            binding.dispatchGatedCallback(staleCtx) { delivered = true }
            assertFalse(delivered, "Stale binding callback must be dropped without delivery")
            binding.handleRoomClosed(staleCtx)
            binding.handleDisplacedByNewConnection(staleCtx)
            assertFalse(conn.closed, "Stale callbacks must not close the rebound connection")
            assertEquals(rebound.roomId, binding.currentRoom?.roomId)

            // 真实替换仍必须认领关闭：先捕获当前上下文，再由新连接真实顶替席位，随后触发顶号裁决。
            // 无论异步回调是否已抢先关闭，最终状态都确定：closed=true 且 DISPLACED_BY_NEW_CONNECTION
            val freshCtx = binding.captureCurrentCallbackContext()!!
            val token = freshCtx.room.resumeTokens[freshCtx.seat]
            val replacement = server.createConnection()
            assertEquals(freshCtx.seat, replacement.resumeSession(ResumeRequest(freshCtx.room.roomId, token)).await().seat)

            binding.handleDisplacedByNewConnection(freshCtx)
            assertTrue(conn.closed, "Real replacement must claim closure deterministically")
            assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, conn.disconnectReason)
            assertFalse(replacement.closed)

            replacement.close()
        }
    }

    @Test
    fun `replacement before explicit leave deterministically invalidates stale connection`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val guestConn = server.createConnection()
            val guestBound = guestConn.joinRoom(JoinRoomRequest(roomId = hostBound.roomId, playerName = "Guest")).await()

            // 新连接先行顶替席位（真实替换必须保留旧连接失效语义）
            val replacement = server.createConnection()
            val resumed = replacement.resumeSession(
                ResumeRequest(hostBound.roomId, guestBound.resumeToken)
            ).await()
            assertEquals(guestBound.seat, resumed.seat)

            // 旧连接随后显式离房：回执被拒绝，且连接被确定性判定为被顶号（不依赖回调时序）
            val staleLeave = guestConn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.REJECTED, staleLeave.status)
            assertTrue(guestConn.closed, "Replaced connection must be invalidated deterministically")
            assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, guestConn.disconnectReason)

            // 新连接不受影响
            assertFalse(replacement.closed)
            assertEquals(ProtocolReceiptStatus.APPLIED, replacement.setReady(true).await().status)

            hostConn.close()
            replacement.close()
            guestConn.close()
        }
    }

    @Test
    fun `nonzero human seat assembles host admin viewer and ai seats consistently`() {
        InProcessServer().use { server ->
            val humanSeat = 2
            val conn = server.createLocalTable(humanSeat = humanSeat)
            val binding = conn as InProcessBinding

            // 连接视角、房间房主位与权威 admin 席位一致
            assertEquals(humanSeat, binding.viewerSeat)
            val room = binding.currentRoom!!
            assertEquals(humanSeat, room.hostSeat)
            assertEquals(humanSeat, room.tableHost.adminSeat)

            // 实际真人仅在 hostSeat，其余席位默认 AI 补位并自动就绪
            val info = conn.getRoomInfo().await()
            assertEquals(humanSeat, info.hostSeat)
            assertEquals(4, info.members.size)
            val human = info.members.first { it.seat == humanSeat }
            assertTrue(human.isHost)
            assertTrue(human.role.equals("HUMAN", ignoreCase = true))
            assertTrue(human.isReady, "Singleplayer assembly auto-readies the human host")
            for (other in info.members.filter { it.seat != humanSeat }) {
                assertFalse(other.isHost)
                assertTrue(other.role.equals("COMPUTER", ignoreCase = true))
                assertTrue(other.isReady)
            }

            // 快照 viewerSeat 与开局操作都以真人座位生效（初始庄家固定 0 号位，非庄家 13 张起手）
            val snap = conn.snapshot().await()
            assertEquals(humanSeat, snap.viewerSeat)
            assertEquals(ProtocolReceiptStatus.APPLIED, conn.start().await().status)
            val active = conn.snapshot().await()
            assertEquals(humanSeat, active.viewerSeat)
            assertEquals(13, active.view.closedHandSize())
            assertFalse(conn.closed)

            conn.close()
            assertTrue(conn.closed)
        }
    }

    @Test
    fun `spectator join and public snapshot and role isolation`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            // 填满另外三席
            val p1 = server.createConnection()
            p1.joinRoom(JoinRoomRequest(hostBound.roomId, playerName = "P1")).await()
            val p2 = server.createConnection()
            p2.joinRoom(JoinRoomRequest(hostBound.roomId, playerName = "P2")).await()
            val p3 = server.createConnection()
            p3.joinRoom(JoinRoomRequest(hostBound.roomId, playerName = "P3")).await()

            // 席位已满，第 5 人尝试作为玩家加入应失败
            val p4 = server.createConnection()
            val fullEx = assertThrows(ExecutionException::class.java) {
                p4.joinRoom(JoinRoomRequest(hostBound.roomId, playerName = "P4")).await()
            }
            assertTrue(fullEx.cause?.message?.contains("ROOM_FULL") == true)

            // 第 5 人作为观战者加入：满房正常加入
            val specConn = server.createConnection()
            val specBound = specConn.joinSpectator(JoinSpectatorRequest(hostBound.roomId, spectatorName = "Spec1")).await()
            assertEquals(hostBound.roomId, specBound.roomId)
            assertNotNull(specBound.spectatorId)
            assertNotNull(specBound.resumeToken)
            assertNotNull(specBound.snapshot)

            val binding = specConn as InProcessBinding
            assertEquals(InProcessBinding.InProcessRole.SPECTATOR, binding.role)
            assertEquals(specBound.spectatorId, binding.spectatorId)
            assertEquals(-1, binding.viewerSeat)

            // 观战可查询公开快照
            val pubSnap = specConn.publicSnapshot().await()
            assertEquals(0L, pubSnap.cursor)

            // 观战不可调用玩家私有/写入接口
            val snapEx = assertThrows(ExecutionException::class.java) {
                specConn.snapshot().await()
            }
            assertTrue(snapEx.cause?.message?.contains("SPECTATOR_READ_ONLY") == true)

            val decEx = assertThrows(ExecutionException::class.java) {
                specConn.decision().await()
            }
            assertTrue(decEx.cause?.message?.contains("SPECTATOR_READ_ONLY") == true)

            val qrEx = assertThrows(ExecutionException::class.java) {
                specConn.queryRoundResult("r1").await()
            }
            assertTrue(qrEx.cause?.message?.contains("SPECTATOR_READ_ONLY") == true)

            val readyReceipt = specConn.setReady(true).await()
            assertEquals(ProtocolReceiptStatus.REJECTED, readyReceipt.status)
            assertEquals("SPECTATOR_READ_ONLY", readyReceipt.reason)

            // 玩家不可调用观战公开快照
            val playerPubEx = assertThrows(ExecutionException::class.java) {
                hostConn.publicSnapshot().await()
            }
            assertTrue(playerPubEx.cause?.message?.contains("ROLE_PLAYER") == true)

            // 观战不可未经离房即加入房间
            val joinEx = assertThrows(ExecutionException::class.java) {
                specConn.joinRoom(JoinRoomRequest(hostBound.roomId, playerName = "SpecAsPlayer")).await()
            }
            assertTrue(joinEx.cause?.message?.contains("ALREADY_IN_ROOM") == true)

            // 观战显式离开
            val leaveReceipt = specConn.leaveRoom().await()
            assertEquals(ProtocolReceiptStatus.APPLIED, leaveReceipt.status)
            assertEquals(InProcessBinding.InProcessRole.UNBOUND, binding.role)
            assertNull(binding.spectatorId)

            hostConn.close()
            p1.close()
            p2.close()
            p3.close()
            p4.close()
            specConn.close()
        }
    }

    @Test
    fun `spectator resume displaces old connection actively without next game actions`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val spec1 = server.createConnection()
            val specBound = spec1.joinSpectator(JoinSpectatorRequest(hostBound.roomId, spectatorName = "Spec1")).await()

            val disconnectedLatch = CountDownLatch(1)
            var disconnectReasonSeen: String? = null
            spec1.onDisconnect { reason ->
                disconnectReasonSeen = reason
                disconnectedLatch.countDown()
            }

            // 新连接凭 token 恢复观战
            val spec2 = server.createConnection()
            val resumedBound = spec2.resumeSpectator(
                ResumeSpectatorRequest(
                    roomId = hostBound.roomId,
                    spectatorId = specBound.spectatorId,
                    resumeToken = specBound.resumeToken,
                    checkpoint = null
                )
            ).await()
            assertEquals(specBound.spectatorId, resumedBound.spectatorId)

            // 旧连接被立即主动终结，绝不依赖下一条游戏动作
            assertTrue(disconnectedLatch.await(3, TimeUnit.SECONDS), "Old spectator connection must be disconnected promptly")
            assertTrue(spec1.closed)
            assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, disconnectReasonSeen)

            // 新连接正常可用
            assertFalse(spec2.closed)
            val pubSnap = spec2.publicSnapshot().await()
            assertNotNull(pubSnap)

            // 旧连接随后 close 不会撤销新连接
            spec1.close()
            assertFalse(spec2.closed)

            hostConn.close()
            spec2.close()
        }
    }

    @Test
    fun `concurrent resume spectator deterministic race rejects superseded request`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val spec1 = server.createConnection()
            val specBound = spec1.joinSpectator(JoinSpectatorRequest(hostBound.roomId, spectatorName = "Spec1")).await()

            val room = (spec1 as InProcessBinding).currentRoom!!

            // 利用 onSpectatorDisplaced 阻塞第一次恢复的完成链
            val firstDisplacedLatch = CountDownLatch(1)
            val unblockFirstLatch = CountDownLatch(1)

            val displaceSub = room.onSpectatorDisplaced { _, gen ->
                if (gen == 2L) {
                    firstDisplacedLatch.countDown()
                    unblockFirstLatch.await(5, TimeUnit.SECONDS)
                }
            }

            val spec2 = server.createConnection()
            val spec3 = server.createConnection()

            // 启动第 1 次恢复 (gen=2)
            val future2 = spec2.resumeSpectator(
                ResumeSpectatorRequest(
                    roomId = hostBound.roomId,
                    spectatorId = specBound.spectatorId,
                    resumeToken = specBound.resumeToken
                )
            )

            // 等待第 1 次恢复触发 onSpectatorDisplaced 被挂起
            assertTrue(firstDisplacedLatch.await(3, TimeUnit.SECONDS))

            // 在第 1 次恢复尚未完成 bindSpectator 时，启动并完成第 2 次恢复 (gen=3)
            val resumed3 = spec3.resumeSpectator(
                ResumeSpectatorRequest(
                    roomId = hostBound.roomId,
                    spectatorId = specBound.spectatorId,
                    resumeToken = specBound.resumeToken
                )
            ).await()
            assertEquals(specBound.spectatorId, resumed3.spectatorId)

            // 释放第 1 次恢复
            unblockFirstLatch.countDown()

            // 第 1 次恢复的完成链必须被拒绝 (DISPLACED_BY_NEW_CONNECTION)，绝不得覆盖第 2 次恢复
            val ex2 = assertThrows(ExecutionException::class.java) {
                future2.await()
            }
            assertTrue(ex2.cause?.message?.contains("DISPLACED_BY_NEW_CONNECTION") == true)

            // 第 2 次恢复仍为当前活跃绑定
            assertFalse(spec3.closed)
            val pubSnap = spec3.publicSnapshot().await()
            assertNotNull(pubSnap)

            displaceSub.close()
            hostConn.close()
            spec1.close()
            spec2.close()
            spec3.close()
        }
    }

    @Test
    fun `stale spectator displaced notification does not displace newer binding`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val hostConn = server.createConnection()
            val hostBound = hostConn.createRoom(CreateRoomRequest(playerName = "Host")).await()

            val spec1 = server.createConnection()
            val specBound = spec1.joinSpectator(JoinSpectatorRequest(hostBound.roomId, spectatorName = "Spec1")).await()

            val spec2 = server.createConnection()
            val resumed2 = spec2.resumeSpectator(
                ResumeSpectatorRequest(
                    roomId = hostBound.roomId,
                    spectatorId = specBound.spectatorId,
                    resumeToken = specBound.resumeToken
                )
            ).await()
            assertEquals(specBound.spectatorId, resumed2.spectatorId)
            assertFalse(spec2.closed)
            assertTrue(spec1.closed)

            var spec2Disconnected = false
            spec2.onDisconnect { spec2Disconnected = true }

            val ctx2 = (spec2 as InProcessBinding).captureCurrentCallbackContext()!!
            assertEquals(2L, ctx2.spectatorGeneration)

            // 验证迟到的旧代次 (gen 1L) 上下文经过 dispatchGatedCallback 会被静默过滤，不触发顶替也不交付
            val staleCtx = InProcessBinding.BindingCallbackContext(
                generation = ctx2.generation - 1,
                room = ctx2.room,
                role = ctx2.role,
                seat = ctx2.seat,
                seatGeneration = ctx2.seatGeneration,
                spectatorId = ctx2.spectatorId,
                spectatorGeneration = 1L
            )
            var delivered = false
            (spec2 as InProcessBinding).dispatchGatedCallback(staleCtx) {
                delivered = true
            }
            assertFalse(delivered)
            assertFalse(spec2.closed)
            assertFalse(spec2Disconnected)

            val snap = spec2.publicSnapshot().await()
            assertNotNull(snap)

            hostConn.close()
            spec1.close()
            spec2.close()
        }
    }
}

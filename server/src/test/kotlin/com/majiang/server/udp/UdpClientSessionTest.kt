package com.majiang.server.udp

import com.majiang.transport.api.ProtocolActionReceipt as ActionReceipt
import com.majiang.access.PlayerSnapshot
import com.majiang.transport.api.ProtocolReceiptStatus as ReceiptStatus
import com.majiang.application.PlayerDecision
import com.majiang.session.PlayerView
import com.majiang.state.GamePhase
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.udp.ResendResponse
import java.net.InetSocketAddress
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class UdpClientSessionTest {

    private val fakeClock = FakeUdpClock(1000L)
    private val address = InetSocketAddress("127.0.0.1", 50000)

    private fun createDummySnapshot(cursor: Long = 10L): PlayerSnapshot {
        return PlayerSnapshot(
            sessionId = "session-1",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = cursor,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = PlayerView.create(
                viewerSeat = 0,
                phase = GamePhase.DEALING,
                roundWind = 1,
                roundNumber = 1,
                honbaSticks = 0,
                riichiSticks = 0,
                dealerSeat = 0,
                currentTurnPlayer = 0,
                wallRemaining = 70,
                doraIndicators = emptyList(),
                scores = listOf(25000, 25000, 25000, 25000),
                riichiStatus = listOf(false, false, false, false),
                discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                handTileCounts = listOf(13, 13, 13, 13),
                myClosedTiles = emptyList(),
                myDrawnTile = null,
                legalActions = emptyList()
            ),
            decision = PlayerDecision(
                roundId = "round-1",
                decisionId = "decision-0",
                controlEpoch = 0L,
                options = emptyList()
            ),
            clocks = emptyList(),
            cheatPoints = listOf(0, 0, 0, 0)
        )
    }

    @Test
    fun testSessionCreationAndValidation() {
        val session = UdpClientSession(
            connectionId = 12345,
            remoteAddress = address,
            roomId = "ROOM1234",
            seat = 0,
            token = "token-abc",
            generation = 1L,
            clock = fakeClock
        )

        assertEquals(12345, session.connectionId)
        assertEquals("ROOM1234", session.roomId)
        assertEquals(0, session.seat)
        assertEquals("token-abc", session.token)
        assertEquals(1L, session.generation)
        assertFalse(session.isClosed)
        assertFalse(session.isDisplaced)

        assertFailsWith<IllegalArgumentException> {
            UdpClientSession(
                connectionId = 0, // connectionId cannot be 0
                remoteAddress = address,
                roomId = "ROOM1234",
                seat = 0,
                token = "token-abc",
                generation = 1L
            )
        }

        assertFailsWith<IllegalArgumentException> {
            UdpClientSession(
                connectionId = 12345,
                remoteAddress = address,
                roomId = "",
                seat = 0,
                token = "token-abc",
                generation = 1L
            )
        }

        assertFailsWith<IllegalArgumentException> {
            UdpClientSession(
                connectionId = 12345,
                remoteAddress = address,
                roomId = "ROOM1234",
                seat = 4, // seat must be in 0..3
                token = "token-abc",
                generation = 1L
            )
        }
    }

    @Test
    fun testSeqMonotonicityAndBidirectionalReplayGuard() {
        val session = UdpClientSession(
            connectionId = 999,
            remoteAddress = address,
            roomId = "ROOM999",
            seat = 1,
            token = "token-xyz",
            generation = 1L,
            clock = fakeClock
        )

        // 出站 seq 单调递增
        val seq1 = session.nextOutboundSeq()
        val seq2 = session.nextOutboundSeq()
        val seq3 = session.nextOutboundSeq()
        assertEquals(1, seq1)
        assertEquals(2, seq2)
        assertEquals(3, seq3)

        // 入站 ReplayGuard
        assertTrue(session.inboundReplayGuard.accept(1))
        assertFalse(session.inboundReplayGuard.accept(1), "Duplicate seq must be rejected")
        assertTrue(session.inboundReplayGuard.accept(4), "Forward seq must be accepted")
        assertTrue(session.inboundReplayGuard.accept(2), "Unseen in-window seq must be accepted")
        assertFalse(session.inboundReplayGuard.accept(2), "Repeated out-of-order seq must be rejected")
        assertFalse(session.inboundReplayGuard.accept(4), "Already seen max seq must be rejected")
        assertTrue(session.inboundReplayGuard.accept(5))
    }

    @Test
    fun testIdempotentCache() {
        val session = UdpClientSession(
            connectionId = 111,
            remoteAddress = address,
            roomId = "ROOM111",
            seat = 2,
            token = "token-111",
            generation = 1L,
            clock = fakeClock
        )

        assertNull(session.getCachedResponse("req-alpha"))

        val response = ApiResponse(
            requestId = "req-alpha",
            success = true,
            receipt = ActionReceipt("req-alpha", ReceiptStatus.APPLIED, cursor = 1L)
        )
        session.cacheResponse("req-alpha", response)

        val cached = session.getCachedResponse("req-alpha")
        assertNotNull(cached)
        assertEquals("req-alpha", cached.requestId)
        assertTrue(cached.success)
        assertEquals(ReceiptStatus.APPLIED, cached.receipt?.status)

        assertNull(session.getCachedResponse("req-beta"))
    }

    @Test
    fun testInboundTimeoutWithFakeClock() {
        val session = UdpClientSession(
            connectionId = 222,
            remoteAddress = address,
            roomId = "ROOM222",
            seat = 3,
            token = "token-222",
            generation = 1L,
            clock = fakeClock
        )

        assertEquals(1000L, session.lastInboundTime)
        assertFalse(session.isTimedOut(now = 1000L))

        fakeClock.advance(5000L)
        assertFalse(session.isTimedOut(fakeClock.currentTimeMillis()))

        fakeClock.advance(5000L) // 累计 10000ms 达到超时阈值
        assertTrue(session.isTimedOut(fakeClock.currentTimeMillis()))

        // 收到入站数据包，重置时间戳
        session.recordInbound(fakeClock.currentTimeMillis())
        assertFalse(session.isTimedOut(fakeClock.currentTimeMillis()))

        fakeClock.advance(10001L)
        assertTrue(session.isTimedOut(fakeClock.currentTimeMillis()))
    }

    @Test
    fun testPushRetransmitWindowAndSnapshotHealing() {
        var supplierCalled = false
        val dummySnap = createDummySnapshot(cursor = 50L)

        val session = UdpClientSession(
            connectionId = 333,
            remoteAddress = address,
            roomId = "ROOM333",
            seat = 0,
            token = "token-333",
            generation = 1L,
            clock = fakeClock,
            snapshotSupplier = {
                supplierCalled = true
                dummySnap
            }
        )

        val payload1 = "update-1".toByteArray(Charsets.UTF_8)
        val payload2 = "update-2".toByteArray(Charsets.UTF_8)

        session.recordPush(cursor = 10L, fromCursor = 9L, payload = payload1)
        session.recordPush(cursor = 11L, fromCursor = 10L, payload = payload2)

        // 窗口内补发
        val resendInWindow = session.handleResend(10L)
        assertTrue(resendInWindow is ResendResponse.Retransmit)
        assertEquals(2, resendInWindow.records.size)
        assertEquals(10L, resendInWindow.records[0].cursor)
        assertEquals(11L, resendInWindow.records[1].cursor)
        assertFalse(supplierCalled)

        // 窗口外索要（早于最老游标 10）
        val resendBeyondWindow = session.handleResend(5L)
        assertTrue(resendBeyondWindow is ResendResponse.FullSnapshot)
        assertTrue(supplierCalled)

        val decodedUpdate = ApiMessageCodec.decodePush(String(resendBeyondWindow.payload, Charsets.UTF_8))
        assertEquals(50L, decodedUpdate.snapshot.cursor)
    }

    @Test
    fun testMarkDisplacedAndClose() {
        val session = UdpClientSession(
            connectionId = 444,
            remoteAddress = address,
            roomId = "ROOM444",
            seat = 1,
            token = "token-444",
            generation = 1L,
            clock = fakeClock
        )

        assertFalse(session.isDisplaced)
        assertTrue(session.markDisplaced())
        assertTrue(session.isDisplaced)
        assertFalse(session.markDisplaced(), "Second displacement should return false")

        assertFalse(session.isClosed)
        session.close()
        assertTrue(session.isClosed)
    }
}

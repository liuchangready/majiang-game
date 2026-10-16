package com.majiang.review

import com.majiang.client.transport.udp.UdpClientBinding
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.ApiErrors
import com.majiang.transport.api.*
import com.majiang.transport.udp.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Independent acceptance through the public binding API. No production-field reflection. */
class PlayerBindingIsolationReviewTest {
    private class Harness : AutoCloseable {
        val sent = CopyOnWriteArrayList<UdpFrame>()
        private var serverSeq = 1000
        var session = "session-A"
        var epoch = "epoch-A"
        val binding = UdpClientBinding(
            rawSender = { sent.add(UdpFrame.decode(it)) },
            clock = object : UdpClock { override fun currentTimeMillis() = 1000L },
            scheduler = object : UdpScheduler {
                override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
            }
        )

        init {
            val created = binding.createRoom(CreateRoomRequest("Reviewer"))
            val request = requests(ApiTags.CREATE_ROOM).last()
            respond(request, ApiResponse(id(request), true, roomId = "ROOM-A", seat = 0,
                resumeToken = "token-A", snapshot = snapshot(0)), 100)
            created.get(2, TimeUnit.SECONDS)
            sent.clear()
        }

        fun id(frame: UdpFrame) = ApiMessageCodec.decodeRequest(frame.payload.toString(Charsets.UTF_8)).requestId
        fun requests(api: String) = sent.filter {
            it.packetType == UdpPacketType.REQ &&
                ApiMessageCodec.decodeRequest(it.payload.toString(Charsets.UTF_8)).api == api
        }

        fun snapshot(cursor: Long): ProtocolPlayerSnapshot {
            val tile = ProtocolTile.fromTileId(0)
            val discard = ProtocolLegalAction.DiscardTile(0, tile)
            val view = ProtocolPlayerView(
                viewerSeat = 0, phase = ProtocolGamePhase.DISCARD, roundWind = 1, roundNumber = 1,
                honbaSticks = 0, riichiSticks = 0, dealerSeat = 0, currentTurnPlayer = 0,
                wallRemaining = 70, doraIndicators = listOf(tile), scores = List(4) { 25000 },
                riichiStatus = List(4) { false }, discards = List(4) { emptyList() },
                melds = List(4) { emptyList() }, handTileCounts = listOf(14, 13, 13, 13),
                myClosedTiles = listOf(tile), myDrawnTile = null, legalActions = listOf(discard)
            )
            return ProtocolPlayerSnapshot(
                sessionId = session, roundId = "round-$session", syncEpoch = epoch,
                cursor = cursor, sessionVersion = 1, roundVersion = 1, view = view,
                decision = ProtocolPlayerDecision("round-$session", "decision-$session", 1,
                    options = listOf(ProtocolActionOption("discard", discard)))
            )
        }

        fun push(cursor: Long, full: Boolean = false) {
            val update = ProtocolPlayerUpdate(snapshot(cursor), fromCursor = cursor - 1,
                visibleEvents = emptyList(), isFullSnapshot = full)
            binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, binding.connectionId, ++serverSeq,
                payload = ApiMessageCodec.encodePush(update).toByteArray(Charsets.UTF_8)))
        }

        fun respond(request: UdpFrame, response: ApiResponse, connectionId: Int = binding.connectionId) {
            binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, connectionId, ++serverSeq, request.seq,
                ApiMessageCodec.encodeResponse(response).toByteArray(Charsets.UTF_8)))
        }

        fun changeRoom() {
            val leave = binding.leaveRoom()
            val leaveRequest = requests(ApiTags.LEAVE_ROOM).last()
            respond(leaveRequest, ApiResponse(id(leaveRequest), true,
                receipt = ProtocolActionReceipt(id(leaveRequest), ProtocolReceiptStatus.APPLIED)))
            leave.get(2, TimeUnit.SECONDS)
            session = "session-B"
            epoch = "epoch-B"
            val join = binding.joinRoom(JoinRoomRequest("ROOM-B"))
            val joinRequest = requests(ApiTags.JOIN_ROOM).last()
            respond(joinRequest, ApiResponse(id(joinRequest), true, roomId = "ROOM-B", seat = 0,
                resumeToken = "token-B", snapshot = snapshot(0)), 200)
            join.get(2, TimeUnit.SECONDS)
        }

        override fun close() { binding.close() }
    }

    @Test fun closeInsideFirstListenerPreventsDeliveryToSecondListener() {
        Harness().use { h ->
            var first = 0
            val second = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { first++; h.binding.close("review-close") }
            h.binding.onUpdate { second.add(it) }
            h.push(1)
            assertEquals(1, first)
            assertTrue(second.isEmpty(), "A listener after a reentrant close must not receive the old item")
        }
    }

    @Test fun rebindInsideFirstListenerPreventsOldItemFromReachingSecondListener() {
        Harness().use { h ->
            var changed = false
            var transitionFailure: Throwable? = null
            val second = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate {
                if (!changed) {
                    changed = true
                    try { h.changeRoom() } catch (failure: Throwable) { transitionFailure = failure }
                }
            }
            h.binding.onUpdate { second.add(it) }
            h.push(1)
            assertNull(transitionFailure, "The reentrant room change itself must complete successfully")
            assertEquals("ROOM-B", h.binding.boundRoomId)
            assertTrue(second.isEmpty(), "ROOM-A item must not be delivered after ROOM-B binding exists")
            h.push(1)
            assertEquals(listOf("session-B"), second.map { it.snapshot.sessionId })
        }
    }

    @Test fun earlyBufferedItemsDoNotSurviveRoomReplacement() {
        Harness().use { h ->
            h.push(1)
            h.push(2)
            h.changeRoom()
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            assertTrue(received.isEmpty())
            h.push(1)
            assertEquals(listOf("session-B"), received.map { it.snapshot.sessionId })
        }
    }

    @Test fun overflowRecoversOneExplicitBaselineInsteadOfReplayingPartialHistory() {
        for (count in listOf(65, 256)) Harness().use { h ->
            repeat(count) { h.push(it.toLong() + 1) }
            val first = mutableListOf<ProtocolPlayerUpdate>()
            val second = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { first.add(it) }
            h.binding.onUpdate { second.add(it) }
            assertTrue(first.isEmpty() && second.isEmpty(), "Overflow must wait for a full baseline, not replay a tail")
            val requests = h.requests(ApiTags.SNAPSHOT)
            assertEquals(1, requests.size, "Listeners must share a single current-binding recovery")
            h.respond(requests.single(), ApiResponse(h.id(requests.single()), true, snapshot = h.snapshot(count.toLong())))
            for (received in listOf(first, second)) {
                assertEquals(1, received.size)
                assertTrue(received.single().isFullSnapshot)
                assertEquals(count.toLong(), received.single().snapshot.cursor)
            }
        }
    }

    @Test fun slowPlayerListenerDoesNotHoldBindingStateLockDuringClose() {
        Harness().use { h ->
            val entered = CountDownLatch(1)
            val release = CountDownLatch(1)
            val pool = Executors.newFixedThreadPool(2)
            try {
                h.binding.onUpdate { entered.countDown(); assertTrue(release.await(5, TimeUnit.SECONDS)) }
                val delivery = CompletableFuture.runAsync({ h.push(1) }, pool)
                assertTrue(entered.await(2, TimeUnit.SECONDS))
                CompletableFuture.runAsync({ h.binding.close("review-slow-listener") }, pool)
                    .get(2, TimeUnit.SECONDS)
                assertTrue(h.binding.closed)
                release.countDown()
                delivery.get(2, TimeUnit.SECONDS)
            } finally {
                release.countDown()
                pool.shutdownNow()
            }
        }
    }

    @Test fun lateSnapshotResponseCannotAdvanceTheReplacementRoomCursor() {
        Harness().use { h ->
            val oldSnapshot = h.snapshot(100)
            h.binding.snapshot()
            val oldRequest = h.requests(ApiTags.SNAPSHOT).single()
            h.changeRoom()
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            h.respond(oldRequest, ApiResponse(h.id(oldRequest), true, snapshot = oldSnapshot), 100)
            h.push(1)
            assertEquals(listOf("session-B"), received.map { it.snapshot.sessionId },
                "Late ROOM-A snapshot must not advance ROOM-B's sequencer to cursor 100")
            assertEquals(listOf(1L), received.map { it.snapshot.cursor })
        }
    }

    @Test fun bufferedDrainStopsImmediatelyWhenItsListenerClosesTheBinding() {
        Harness().use { h ->
            repeat(64) { h.push(it.toLong() + 1) }
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it); h.binding.close("review-buffered-close") }
            assertEquals(1, received.size,
                "Closing during the first buffered callback must discard the remaining 63 items")
            assertEquals(0, h.requests(ApiTags.SNAPSHOT).size,
                "Exactly 64 buffered items must not trigger overflow recovery")
        }
    }

    @Test fun ordinaryUpdatesWaitWhileOverflowRecoveryIsInFlight() {
        Harness().use { h ->
            repeat(65) { h.push(it.toLong() + 1) }
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            val request = h.requests(ApiTags.SNAPSHOT).single()
            h.push(66)
            assertTrue(received.isEmpty(), "No partial continuation may escape before the recovery baseline")
            assertEquals(1, h.requests(ApiTags.SNAPSHOT).size)
            h.respond(request, ApiResponse(h.id(request), true, snapshot = h.snapshot(66)))
            assertEquals(listOf(66L), received.map { it.snapshot.cursor })
            assertTrue(received.single().isFullSnapshot)
            h.push(67)
            assertEquals(listOf(66L, 67L), received.map { it.snapshot.cursor })
        }
    }

    @Test fun lateRecoveryCannotRollBackANewerReliableFullBaseline() {
        Harness().use { h ->
            repeat(65) { h.push(it.toLong() + 1) }
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            val request = h.requests(ApiTags.SNAPSHOT).single()
            h.push(70, full = true)
            assertEquals(listOf(70L), received.map { it.snapshot.cursor })
            assertTrue(received.single().isFullSnapshot)
            h.respond(request, ApiResponse(h.id(request), true, snapshot = h.snapshot(65)))
            h.push(71)
            assertEquals(listOf(70L, 71L), received.map { it.snapshot.cursor },
                "A stale recovery response must not notify or roll back the sequencing baseline")
        }
    }

    @Test fun foreignStreamCannotConsumeTheCurrentBindingsNextCursor() {
        for (changeSession in listOf(true, false)) Harness().use { h ->
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            if (changeSession) h.session = "foreign-session" else h.epoch = "foreign-epoch"
            h.push(1)
            assertTrue(received.isEmpty(), "Same-connection frames still need sessionId and syncEpoch validation")
            h.session = "session-A"
            h.epoch = "epoch-A"
            h.push(1)
            assertEquals(listOf("session-A"), received.map { it.snapshot.sessionId })
            assertEquals(listOf(1L), received.map { it.snapshot.cursor },
                "Rejected foreign data must not consume the valid stream's next cursor")
        }
    }

    @Test fun reliableFullBaselineUnblocksContinuationBeforeHealingResponseArrives() {
        Harness().use { h ->
            repeat(65) { h.push(it.toLong() + 1) }
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            assertEquals(1, h.requests(ApiTags.SNAPSHOT).size)
            h.push(70, full = true)
            h.push(71)
            assertEquals(listOf(70L, 71L), received.map { it.snapshot.cursor },
                "An accepted full baseline resolves overflow even if the older SNAPSHOT request is still pending")
        }
    }

    @Test fun failedOverflowRecoveryCanRetryOnTheNextPushWithoutAnotherListener() {
        Harness().use { h ->
            repeat(65) { h.push(it.toLong() + 1) }
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            val firstRequest = h.requests(ApiTags.SNAPSHOT).single()
            h.respond(firstRequest, ApiResponse(h.id(firstRequest), false, error = ApiErrors.INTERNAL_ERROR))
            h.push(66)
            assertTrue(received.isEmpty(), "The failed baseline must not release a partial continuation")
            val requests = h.requests(ApiTags.SNAPSHOT)
            assertEquals(2, requests.size, "A failed heal cannot permanently stall an existing listener")
            val retry = requests.last()
            h.respond(retry, ApiResponse(h.id(retry), true, snapshot = h.snapshot(66)))
            assertEquals(listOf(66L), received.map { it.snapshot.cursor })
            assertTrue(received.single().isFullSnapshot)
        }
    }

    @Test fun beyondWindowGapPublishesItsRecoveryBaselineBeforeContinuation() {
        Harness().use { h ->
            val received = mutableListOf<ProtocolPlayerUpdate>()
            h.binding.onUpdate { received.add(it) }
            h.push(600)
            val request = h.requests(ApiTags.SNAPSHOT).single()
            assertTrue(received.isEmpty())
            h.respond(request, ApiResponse(h.id(request), true, snapshot = h.snapshot(600)))
            assertEquals(listOf(600L), received.map { it.snapshot.cursor },
                "Gap recovery must publish its adopted full baseline even without early-buffer overflow")
            assertTrue(received.single().isFullSnapshot)
            h.push(601)
            assertEquals(listOf(600L, 601L), received.map { it.snapshot.cursor })
        }
    }
}

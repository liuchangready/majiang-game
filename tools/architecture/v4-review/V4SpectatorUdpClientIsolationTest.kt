package com.majiang.v4lobby

import com.majiang.client.transport.udp.UdpClientBinding
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.*
import com.majiang.transport.udp.*
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Real UDP binding/codec with a controlled datagram peer; no server implementation or socket timing. */
@Timeout(10)
class V4SpectatorUdpClientIsolationTest {
    private fun snapshot(cursor: Long) = ProtocolPublicSnapshot(
        sessionId = "public-session", sessionVersion = cursor, syncEpoch = "public-stream", cursor = cursor,
        session = ProtocolPublicSessionSummary("WAITING_START", List(4) { 25000 }, 1, 0, 0, 0)
    )

    private inner class Peer : AutoCloseable {
        val outbound = mutableListOf<UdpFrame>()
        private val timers = mutableListOf<Runnable>()
        private var serverSeq = 100
        val binding = UdpClientBinding(
            rawSender = { outbound.add(UdpFrame.decode(it)) },
            clock = object : UdpClock { override fun currentTimeMillis() = 1000L },
            scheduler = object : UdpScheduler {
                override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
                    timers.add(task)
                    return Cancellable { timers.remove(task) }
                }
            }
        )
        fun request(api: String): Pair<UdpFrame, ApiRequest> = outbound
            .filter { it.packetType == UdpPacketType.REQ }
            .map { it to ApiMessageCodec.decodeRequest(it.payload.toString(Charsets.UTF_8)) }
            .lastOrNull { it.second.api == api } ?: error("Expected actual outgoing request $api")
        fun respond(frame: UdpFrame, response: ApiResponse) {
            binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 701, serverSeq++, ackSeq = frame.seq,
                payload = ApiMessageCodec.encodeResponse(response).toByteArray(Charsets.UTF_8)))
        }
        fun join() {
            val future = binding.joinSpectator(JoinSpectatorRequest("room"))
            val (frame, request) = request(ApiTags.JOIN_SPECTATOR)
            respond(frame, ApiResponse(requestId = request.requestId, success = true,
                spectatorBinding = SpectatorBinding("room", "viewer", "token", snapshot(0))))
            assertEquals(0L, future.get(2, TimeUnit.SECONDS).snapshot.cursor)
        }
        fun push(cursor: Long, full: Boolean = false) {
            val update = ProtocolPublicUpdate(snapshot(cursor), if (full) cursor else cursor - 1, emptyList(), full)
            binding.onInboundFrame(UdpFrame(UdpPacketType.PUSH, 701, serverSeq++,
                payload = ApiMessageCodec.encodePublicUpdatePush(update).toByteArray(Charsets.UTF_8)))
        }
        fun answerHealingSnapshot(cursor: Long) {
            val (frame, request) = request(ApiTags.PUBLIC_SNAPSHOT)
            respond(frame, ApiResponse(requestId = request.requestId, success = true, publicSnapshot = snapshot(cursor)))
        }
        override fun close() { binding.close() }
    }

    @Test fun gapSelfHealPublishesItsAdoptedBaselineAndThenContinuesNormally() {
        Peer().use { peer ->
            peer.join()
            val updates = mutableListOf<ProtocolPublicUpdate>()
            peer.binding.onPublicUpdate { updates.add(it) }.use {
                peer.push(300)
                assertTrue(updates.isEmpty(), "Do not expose a future cursor across a large gap")
                peer.answerHealingSnapshot(300)
                assertEquals(listOf(300L), updates.map { it.snapshot.cursor },
                    "Adopting the response internally must not suppress the downstream full baseline")
                assertTrue(updates.single().isFullSnapshot)
                peer.push(301)
                assertEquals(listOf(300L, 301L), updates.map { it.snapshot.cursor })
                assertFalse(updates.last().isFullSnapshot)
            }
        }
    }

    @Test fun staleHealingResponseCannotNotifyAfterANewerReliableFullBaseline() {
        Peer().use { peer ->
            peer.join()
            val cursors = mutableListOf<Long>()
            peer.binding.onPublicUpdate { cursors.add(it.snapshot.cursor) }.use {
                peer.push(300)
                peer.request(ApiTags.PUBLIC_SNAPSHOT)
                peer.push(400, full = true)
                peer.answerHealingSnapshot(300)
                peer.push(401)
                assertEquals(listOf(400L, 401L), cursors,
                    "An obsolete healing response must not notify a baseline older than an already delivered update")
            }
        }
    }

    @Test fun closingFromTheFirstCallbackStopsDeliveryToRemainingListeners() {
        Peer().use { peer ->
            peer.join()
            val later = mutableListOf<Long>()
            peer.binding.onPublicUpdate { peer.binding.close() }.use {
                peer.binding.onPublicUpdate { later.add(it.snapshot.cursor) }.use {
                    peer.push(1)
                    assertTrue(later.isEmpty(), "A completed close invalidates callbacks still waiting in this batch")
                }
            }
        }
    }

    @Test fun closingDuringEarlyBufferReplayStopsTheRemainingOldBindingItems() {
        Peer().use { peer ->
            peer.join()
            peer.push(1)
            peer.push(2)
            val delivered = mutableListOf<Long>()
            peer.binding.onPublicUpdate {
                delivered.add(it.snapshot.cursor)
                peer.binding.close()
            }.use {
                assertEquals(listOf(1L), delivered,
                    "Each buffered callback must recheck binding validity after user code can close it")
            }
        }
    }

    @Test fun listenerBufferOverflowHealsEvenWhenTransportAlreadyHasTheLatestCursor() {
        Peer().use { peer ->
            peer.join()
            // The bounded early buffer cannot retain the complete sequence for a late subscriber.
            repeat(300) { peer.push(it.toLong() + 1) }
            val updates = mutableListOf<ProtocolPublicUpdate>()
            peer.binding.onPublicUpdate { updates.add(it) }.use {
                peer.answerHealingSnapshot(300)
                val full = updates.filter { it.isFullSnapshot }
                assertEquals(listOf(300L), full.map { it.snapshot.cursor },
                    "Late subscriber needs a full baseline even if transport cursor is already 300")
                peer.push(301)
                assertEquals(301L, updates.last().snapshot.cursor)
            }
        }
    }
}

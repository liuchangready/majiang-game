package com.majiang.review

import com.majiang.client.api.ConnectionState
import com.majiang.client.session.ReconnectErrorClassifier
import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.transport.udp.UdpClientBinding
import com.majiang.server.api.*
import com.majiang.transport.api.*
import com.majiang.transport.udp.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException

/** Uses the production UDP binding, deterministic timers and encoded RSP frames. */
class Mr02ReconnectReviewTest {
    private class Timer : UdpScheduler {
        data class Entry(val delay: Long, val task: Runnable, var cancelled: Boolean = false)
        val entries = mutableListOf<Entry>()
        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val entry = Entry(delayMillis, task)
            entries.add(entry)
            return Cancellable { entry.cancelled = true }
        }
        fun fire(): Long {
            val entry = entries.first { !it.cancelled }
            entries.remove(entry)
            entry.task.run()
            return entry.delay
        }
    }

    private class Wire : AutoCloseable {
        val sent = mutableListOf<UdpFrame>()
        val binding = UdpClientBinding(
            rawSender = { sent.add(UdpFrame.decode(it)) },
            clock = UdpClock { 1000L },
            scheduler = object : UdpScheduler {
                override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
            }
        )
        private var seq = 100
        fun reply(error: String? = null) {
            val frame = sent.last { it.packetType == UdpPacketType.REQ }
            val request = ApiMessageCodec.decodeRequest(frame.payload.toString(Charsets.UTF_8))
            val response = if (error != null) ApiResponse(request.requestId, false, error = error)
                else ApiResponse(request.requestId, true, roomId = "review-room", seat = 0,
                    resumeToken = "review-token", snapshot = snapshot())
            binding.onInboundFrame(UdpFrame(UdpPacketType.RSP, 7, ++seq, frame.seq,
                ApiMessageCodec.encodeResponse(response).toByteArray(Charsets.UTF_8)))
        }
        fun drop() {
            binding.onInboundFrame(UdpFrame(UdpPacketType.KICK, 7, ++seq,
                payload = ApiMessageCodec.encodeKick("NETWORK_LOST").toByteArray(Charsets.UTF_8)))
        }
        override fun close() = binding.close()
    }

    private fun exercise(error: String, attempts: Int, permanent: Boolean) {
        val initial = Wire()
        val timer = Timer()
        val created = mutableListOf<Wire>()
        val session = ServerConnectionSession(initial.binding, connectionFactory = {
            val wire = Wire()
            created.add(wire)
            CompletableFuture.completedFuture<ServerConnection>(wire.binding)
        }, scheduler = timer)
        try {
            val ready = session.createRoom("Reviewer", null)
            initial.reply()
            ready.join()
            initial.drop()
            repeat(attempts) { attempt ->
                assertEquals(ConnectionState.RECONNECTING, session.connectionState)
                assertEquals(minOf(8000L, 500L * (1L shl attempt)), timer.fire())
                created.last().reply(error)
                assertTrue(created.last().binding.closed, "Failed replacement binding must close")
            }
            assertEquals(attempts, created.size)
            assertEquals(ConnectionState.CLOSED, session.connectionState)
            assertEquals(if (permanent) error else "RECONNECT_ATTEMPTS_EXHAUSTED", session.disconnectReason)
            assertTrue(timer.entries.none { !it.cancelled })
        } finally {
            session.close()
            initial.close()
            created.forEach { it.close() }
        }
    }

    @Test fun udpInvalidTokenTerminatesOnFirstResume() = exercise(ApiErrors.INVALID_TOKEN, 1, true)
    @Test fun udpUnknownBusinessCodeExhaustsExactlyTenAttempts() = exercise("UNKNOWN_CLOSED_ERROR", 10, false)

    @Test fun wrappedIoAndUnknownTextRemainTransientWhileTypedCodeWins() {
        assertFalse(ReconnectErrorClassifier.isPermanent(CompletionException(ExecutionException(IOException("INVALID_TOKEN")))))
        assertFalse(ReconnectErrorClassifier.isPermanent(IllegalStateException("temporary ROOM_CLOSED failure")))
        assertFalse(ReconnectErrorClassifier.isPermanent(ServerApiException(ApiErrors.INTERNAL_ERROR, "INVALID_TOKEN")))
        assertTrue(ReconnectErrorClassifier.isPermanent(ExecutionException(ServerApiException(ApiErrors.INVALID_TOKEN, "temporary"))))
    }

    companion object {
        private fun snapshot(): ProtocolPlayerSnapshot {
            val tile = ProtocolTile.fromTileId(0)
            val discard = ProtocolLegalAction.DiscardTile(0, tile)
            val view = ProtocolPlayerView(
                viewerSeat = 0, phase = ProtocolGamePhase.DISCARD, roundWind = 1, roundNumber = 1,
                honbaSticks = 0, riichiSticks = 0, dealerSeat = 0, currentTurnPlayer = 0,
                wallRemaining = 70, doraIndicators = listOf(tile), scores = List(4) { 25000 },
                riichiStatus = List(4) { false }, discards = List(4) { emptyList() },
                melds = List(4) { emptyList() }, handTileCounts = listOf(14, 13, 13, 13),
                myClosedTiles = listOf(tile), myDrawnTile = null, legalActions = listOf(discard))
            return ProtocolPlayerSnapshot("review-session", "review-round", "review-epoch", 0, 1, 1,
                view = view, decision = ProtocolPlayerDecision("review-round", "review-decision", 1,
                    options = listOf(ProtocolActionOption("discard", discard))))
        }
    }
}

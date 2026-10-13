package com.majiang.server.udp

import com.majiang.server.api.ApiErrors
import com.majiang.transport.api.*
import com.majiang.transport.udp.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/** Codex A2 raw UDP integration: no client implementation or fake SessionApi is involved. */
@Timeout(30)
class BeamSwapUdpPrivacyTest {
    private class WireClient(private val port: Int, clock: UdpClock) : AutoCloseable {
        private val socket = DatagramSocket()
        private val fragmenter = Fragmenter(clock, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
        })
        private var sequence = 1
        private var connectionId = 0
        var fragmentsReceived = 0
            private set

        fun request(request: ApiRequest): ApiResponse {
            val frame = UdpFrame(UdpPacketType.REQ, connectionId, sequence++, 0,
                ApiMessageCodec.encodeRequest(request).toByteArray(Charsets.UTF_8))
            for (part in fragmenter.fragment(frame) { sequence++ }) {
                val bytes = UdpFrame.encode(part)
                socket.send(DatagramPacket(bytes, bytes.size, InetAddress.getByName("127.0.0.1"), port))
            }
            val deadline = System.nanoTime() + 4_000_000_000L
            while (System.nanoTime() < deadline) {
                socket.soTimeout = 300
                val packet = DatagramPacket(ByteArray(4096), 4096)
                try { socket.receive(packet) } catch (_: SocketTimeoutException) { continue }
                val raw = UdpFrame.decode(packet.data.copyOfRange(packet.offset, packet.offset + packet.length))
                val complete = if (raw.packetType == UdpPacketType.FRAG) {
                    fragmentsReceived++
                    fragmenter.reassemble(raw) ?: continue
                } else raw
                if (complete.packetType != UdpPacketType.RSP) continue
                val response = ApiMessageCodec.decodeResponse(String(complete.payload, Charsets.UTF_8))
                if (response.requestId != request.requestId) continue
                if (response.success && complete.connectionId != 0) connectionId = complete.connectionId
                return response
            }
            throw SocketTimeoutException("No response for ${request.api}/${request.requestId}")
        }

        fun snapshot(id: String): ProtocolPlayerSnapshot = requireNotNull(request(ApiRequest(ApiTags.SNAPSHOT, id)).snapshot)
        override fun close() { socket.close(); fragmenter.close() }
    }

    @Test
    fun `raw spectator cannot forge either beam swap operation through UDP dispatch`() {
        val clock = FakeUdpClock()
        UdpTableServer(clock = clock, scheduler = FakeUdpScheduler(clock)).use { server ->
            server.start()
            WireClient(server.boundPort, clock).use { host ->
                WireClient(server.boundPort, clock).use { observer ->
                    val config = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
                        beamSwapConfig = ProtocolBeamSwapConfig(true))
                    val created = host.request(ApiRequest(ApiTags.CREATE_ROOM, "beam-room", config = config))
                    assertTrue(created.success, created.error)
                    assertTrue(observer.request(ApiRequest(ApiTags.JOIN_SPECTATOR, "watch-beam", roomId = created.roomId, spectatorName = "Observer")).success)
                    val activation = ProtocolBeamSwapRequest("forged-beam", "unknown-session", "unknown-round", "unknown-decision", 0)
                    val choice = ProtocolBeamSwapChoiceRequest("forged-beam-choice", "unknown-session", "unknown-round", "unknown-decision", 0,
                        "unknown-offer", ProtocolBeamSwapAction.KEEP_ORIGINAL)
                    for (request in listOf(ApiRequest(ApiTags.SUBMIT_BEAM_SWAP, activation.requestId, beamSwap = activation),
                        ApiRequest(ApiTags.SUBMIT_BEAM_SWAP_CHOICE, choice.requestId, beamSwapChoice = choice))) {
                        val denied = observer.request(request)
                        assertFalse(denied.success)
                        assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, denied.error)
                        assertNull(denied.snapshot)
                        assertNull(denied.receipt)
                    }
                }
            }
        }
    }
}

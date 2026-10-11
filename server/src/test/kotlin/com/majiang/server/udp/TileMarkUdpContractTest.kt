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
class TileMarkUdpContractTest {
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
    fun `real UDP carries twelve marks fragmented snapshots private recovery and exact river position`() {
        val clock = FakeUdpClock()
        UdpTableServer(clock = clock, scheduler = FakeUdpScheduler(clock)).use { server ->
            server.start()
            WireClient(server.boundPort, clock).use { host ->
                WireClient(server.boundPort, clock).use { guest ->
                    WireClient(server.boundPort, clock).use { observer ->
                        val config = ProtocolMatchConfig(
                            cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to 10)),
                            tileMarkConfig = ProtocolTileMarkConfig(true))
                        assertTrue(host.request(ApiRequest(ApiTags.CAPABILITIES, "before-bind-cap")).capabilities!!.tileMarkSupported)
                        val created = host.request(ApiRequest(ApiTags.CREATE_ROOM, "create", playerName = "Host", config = config))
                        assertTrue(created.success, created.error)
                        val roomId = requireNotNull(created.roomId)
                        val token = requireNotNull(created.resumeToken)
                        assertTrue(guest.request(ApiRequest(ApiTags.JOIN_ROOM, "join", roomId = roomId, playerName = "Guest")).success)
                        assertTrue(observer.request(ApiRequest(ApiTags.JOIN_SPECTATOR, "watch", roomId = roomId, spectatorName = "Observer")).success)
                        assertTrue(host.request(ApiRequest(ApiTags.CAPABILITIES, "cap")).capabilities!!.tileMarkSupported)
                        assertEquals(ProtocolReceiptStatus.APPLIED, host.request(ApiRequest(ApiTags.SET_READY, "ready-host", ready = true)).receipt!!.status)
                        assertEquals(ProtocolReceiptStatus.APPLIED, guest.request(ApiRequest(ApiTags.SET_READY, "ready-guest", ready = true)).receipt!!.status)
                        assertEquals(ProtocolReceiptStatus.APPLIED, host.request(ApiRequest(ApiTags.START_MATCH, "start")).receipt!!.status)
                        val initial = host.snapshot("initial")
                        val ids = (initial.view.myClosedTiles + listOfNotNull(initial.view.myDrawnTile)).map { it.tileId }
                        var firstRequest: ApiRequest? = null
                        var firstReceipt: ProtocolActionReceipt? = null
                        for (batch in 0..2) {
                            val snapshot = host.snapshot("before-$batch")
                            val mark = ProtocolTileMarkRequest("mark-$batch", snapshot.sessionId, snapshot.roundId!!,
                                snapshot.decision.decisionId, snapshot.decision.controlEpoch, ids.drop(batch * 4).take(4))
                            val request = ApiRequest(ApiTags.SUBMIT_TILE_MARK, mark.requestId, tileMark = mark)
                            val receipt = host.request(request).receipt!!
                            assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
                            if (batch == 0) { firstRequest = request; firstReceipt = receipt }
                        }
                        val marked = host.snapshot("twelve")
                        assertEquals(12, marked.tileMark!!.activeTileCount)
                        assertEquals(3, marked.tileMark!!.activeGroups.size)
                        assertEquals(10, marked.cheatPoints[0])
                        assertTrue(host.fragmentsReceived > 0, "Snapshots must traverse real UDP fragmentation")
                        assertEquals(firstReceipt, host.request(requireNotNull(firstRequest)).receipt)
                        assertEquals(marked.cursor, host.snapshot("after-replay").cursor)
                        assertTrue(guest.snapshot("guest-private").tileMark!!.activeGroups.isEmpty())

                        val forged = firstRequest!!.tileMark!!.copy(requestId = "observer-mark")
                        val denied = observer.request(ApiRequest(ApiTags.SUBMIT_TILE_MARK, forged.requestId, tileMark = forged))
                        assertFalse(denied.success)
                        assertEquals(ApiErrors.UNAUTHENTICATED_SESSION, denied.error)
                        val publicSnapshot = observer.request(ApiRequest(ApiTags.PUBLIC_SNAPSHOT, "public")).publicSnapshot!!
                        assertFalse(ApiMessageCodec.gson.toJson(publicSnapshot).contains("tileMark"))

                        val discardOption = marked.decision.options.first {
                            val action = it.action
                            action is ProtocolLegalAction.DiscardTile && action.tile.tileId == ids.first()
                        }
                        val choice = ProtocolActionChoice("discard-marked", marked.roundId, marked.decision.decisionId,
                            marked.decision.controlEpoch, discardOption.optionId)
                        assertNotEquals(ProtocolReceiptStatus.REJECTED, host.request(ApiRequest(ApiTags.SUBMIT_CHOICE, choice.requestId, choice = choice)).receipt!!.status)
                        val discarded = host.snapshot("discarded")
                        val tile = discarded.tileMark!!.activeGroups.flatMap { it.tiles }.single { it.tileId == ids.first() }
                        assertEquals(ProtocolRiverPosition(discarded.roundId!!, 0, 0), tile.riverPosition)
                        assertEquals(tile.tileId, discarded.view.discards[0][0].tileId)
                        WireClient(server.boundPort, clock).use { replacement ->
                            val resumed = replacement.request(ApiRequest(ApiTags.RESUME_SESSION, "resume", roomId = roomId, token = token))
                            assertTrue(resumed.success, resumed.error)
                            assertEquals(0, resumed.seat)
                            val restored = replacement.snapshot("restored")
                            assertEquals(discarded.tileMark!!.activeGroups, restored.tileMark!!.activeGroups)
                            assertEquals(10, restored.cheatPoints[0])
                        }
                    }
                }
            }
        }
    }
}

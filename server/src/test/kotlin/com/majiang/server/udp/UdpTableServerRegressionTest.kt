package com.majiang.server.udp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpScheduler
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.Fragmenter
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import kotlin.test.*

@Timeout(15)
class UdpTableServerRegressionTest {

    private class TestPeer(private val port: Int) : AutoCloseable {
        val socket = DatagramSocket()
        private val fragmenter = Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable) = Cancellable { }
        })

        fun send(frame: UdpFrame) {
            val bytes = UdpFrame.encode(frame)
            socket.send(DatagramPacket(bytes, bytes.size, InetAddress.getLoopbackAddress(), port))
        }

        fun request(request: ApiRequest, connectionId: Int = 0, seq: Int = 1): Pair<UdpFrame, ApiResponse> {
            send(
                UdpFrame(
                    packetType = UdpPacketType.REQ,
                    connectionId = connectionId,
                    seq = seq,
                    payload = ApiMessageCodec.encodeRequest(request).toByteArray()
                )
            )
            val frame = receive { f ->
                f.packetType == UdpPacketType.RSP &&
                    ApiMessageCodec.decodeResponse(f.payload.toString(Charsets.UTF_8)).requestId == request.requestId
            }
            return frame to ApiMessageCodec.decodeResponse(frame.payload.toString(Charsets.UTF_8))
        }

        fun receive(timeoutMs: Long = 2000L, predicate: (UdpFrame) -> Boolean): UdpFrame {
            val deadline = System.currentTimeMillis() + timeoutMs
            while (System.currentTimeMillis() < deadline) {
                socket.soTimeout = maxOf(1, (deadline - System.currentTimeMillis()).toInt())
                val packet = DatagramPacket(ByteArray(2048), 2048)
                try {
                    socket.receive(packet)
                    val f = fragmenter.reassemble(UdpFrame.decode(packet.data.copyOf(packet.length))) ?: continue
                    if (predicate(f)) return f
                } catch (_: Exception) {}
            }
            error("No matching packet before timeout")
        }

        override fun close() {
            socket.close()
            fragmenter.close()
        }
    }

    @Test
    fun `same requestId with changed payload returns CONFLICT`() {
        UdpTableServer().use { server ->
            server.start()
            TestPeer(server.boundPort).use { peer ->
                val req1 = ApiRequest(ApiTags.CREATE_ROOM, "req-c1", playerName = "First")
                val rsp1 = peer.request(req1).second
                assertTrue(rsp1.success)

                val req2 = req1.copy(playerName = "SecondModified")
                val rsp2 = peer.request(req2).second
                assertFalse(rsp2.success)
                assertEquals(ApiErrors.CONFLICT, rsp2.error)
            }
        }
    }

    @Test
    fun `reordered distinct requests on active session both receive responses`() {
        UdpTableServer().use { server ->
            server.start()
            TestPeer(server.boundPort).use { peer ->
                val created = peer.request(ApiRequest(ApiTags.CREATE_ROOM, "reorder-create"))
                val connId = created.first.connectionId
                assertTrue(created.second.success)

                // 先发 seq=20，后发 seq=19
                val rsp20 = peer.request(ApiRequest(ApiTags.CAPABILITIES, "req-seq20"), connId, seq = 20).second
                assertTrue(rsp20.success)

                val rsp19 = peer.request(ApiRequest(ApiTags.CAPABILITIES, "req-seq19"), connId, seq = 19).second
                assertTrue(rsp19.success)
            }
        }
    }

    @Test
    fun `session request with same requestId but changed API returns CONFLICT`() {
        UdpTableServer().use { server ->
            server.start()
            TestPeer(server.boundPort).use { peer ->
                val created = peer.request(ApiRequest(ApiTags.CREATE_ROOM, "bound-host"))
                val connId = created.first.connectionId
                assertTrue(created.second.success)

                val rsp1 = peer.request(ApiRequest(ApiTags.CAPABILITIES, "same-id"), connId, seq = 2).second
                assertTrue(rsp1.success)

                val rsp2 = peer.request(ApiRequest(ApiTags.SNAPSHOT, "same-id"), connId, seq = 3).second
                assertFalse(rsp2.success)
                assertEquals(ApiErrors.CONFLICT, rsp2.error)
            }
        }
    }

    @Test
    fun `terminal kick retries until ACK received`() {
        UdpTableServer().use { server ->
            server.start()
            TestPeer(server.boundPort).use { peer ->
                val created = peer.request(ApiRequest(ApiTags.CREATE_ROOM, "kick-test"))
                val session = assertNotNull(server.getSession(created.first.connectionId))
                server.sendKick(session, ApiProtocol.ROOM_CLOSED)

                val kick1 = peer.receive { it.packetType == UdpPacketType.KICK }
                assertEquals(created.first.connectionId, kick1.connectionId)

                // 等待服务端指数退避重试包
                val kickRetry = peer.receive(timeoutMs = 1500L) { it.packetType == UdpPacketType.KICK }
                assertEquals(kick1.connectionId, kickRetry.connectionId)
                assertContentEquals(kick1.payload, kickRetry.payload)

                // 回发 ACK
                peer.send(UdpFrame(UdpPacketType.ACK, kickRetry.connectionId, 10, ackSeq = kickRetry.seq))
            }
        }
    }
}

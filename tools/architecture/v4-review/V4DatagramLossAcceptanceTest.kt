package com.majiang.review

import com.majiang.client.transport.udp.UdpClient
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** Real sockets: lose both the first terminal notification and its first acknowledgement. */
@Timeout(20)
class V4DatagramLossAcceptanceTest {
    private class LossyProxy(serverPort: Int) : AutoCloseable {
        private val socket = DatagramSocket(0, InetAddress.getLoopbackAddress())
        private val serverAddress = InetSocketAddress(InetAddress.getLoopbackAddress(), serverPort)
        @Volatile private var clientAddress: SocketAddress? = null
        private val kickSequences = ConcurrentHashMap.newKeySet<Int>()
        val kicks = AtomicInteger()
        val terminalAcks = AtomicInteger()
        val duplicateAckForwarded = CountDownLatch(1)
        val port: Int get() = socket.localPort
        private val worker = Thread({
            while (!socket.isClosed) {
                try {
                    val packet = DatagramPacket(ByteArray(2048), 2048)
                    socket.receive(packet)
                    val bytes = packet.data.copyOf(packet.length)
                    val frame = UdpFrame.decode(bytes)
                    val fromServer = packet.socketAddress == serverAddress
                    val destination = if (fromServer) clientAddress ?: continue else {
                        clientAddress = packet.socketAddress
                        serverAddress
                    }
                    if (fromServer && frame.packetType == UdpPacketType.KICK) {
                        kickSequences.add(frame.seq)
                        if (kicks.incrementAndGet() == 1) continue
                    }
                    val terminalAck = !fromServer && frame.packetType == UdpPacketType.ACK && frame.ackSeq in kickSequences
                    if (terminalAck && terminalAcks.incrementAndGet() == 1) continue
                    socket.send(DatagramPacket(bytes, bytes.size, destination))
                    if (terminalAck) duplicateAckForwarded.countDown()
                } catch (error: Exception) {
                    if (socket.isClosed) break
                    throw error
                }
            }
        }, "v4-review-lossy-proxy").apply { isDaemon = true; start() }
        override fun close() { socket.close(); worker.join(1000) }
    }

    @Test fun shutdownSurvivesLostKickAndLostTerminalAcknowledgement() {
        val server = UdpTableServer()
        server.start()
        try {
            LossyProxy(server.boundPort).use { proxy ->
                UdpClient.connect("127.0.0.1", proxy.port).get(4, TimeUnit.SECONDS).use { client ->
                    client.createRoom(CreateRoomRequest(protocolVersion = 4)).get(4, TimeUnit.SECONDS)
                    val reason = CompletableFuture<String>()
                    client.onDisconnect { reason.complete(it) }
                    server.close()
                    assertEquals(ApiProtocol.SERVER_SHUTDOWN, reason.get(4, TimeUnit.SECONDS))
                    assertTrue(proxy.duplicateAckForwarded.await(3, TimeUnit.SECONDS),
                        "Closed client must briefly retain its socket to ACK a retransmitted terminal KICK")
                    assertTrue(proxy.kicks.get() >= 3)
                    assertTrue(proxy.terminalAcks.get() >= 2)
                }
            }
        } finally { server.close() }
    }
}

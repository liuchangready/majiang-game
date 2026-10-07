package com.majiang.v4runtime

import com.majiang.client.api.ConnectionState
import com.majiang.client.api.ServerConnectionSession
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiTags
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Real sockets and real heartbeat deadlines; the proxy drops complete traffic in both directions. */
@Timeout(50)
class V4RuntimeUdpAcceptanceTest {
    private class LinkProxy(serverPort: Int) : AutoCloseable {
        private val server = InetSocketAddress(InetAddress.getLoopbackAddress(), serverPort)
        private val socket = DatagramSocket(0, InetAddress.getLoopbackAddress())
        @Volatile var disconnected = false
        @Volatile private var client: SocketAddress? = null
        val controlClaims = AtomicInteger()
        val port get() = socket.localPort
        private val receiver = Thread({
            while (!socket.isClosed) {
                try {
                    val packet = DatagramPacket(ByteArray(2048), 2048)
                    socket.receive(packet)
                    val fromServer = packet.socketAddress == server
                    if (!fromServer) client = packet.socketAddress
                    if (disconnected) continue
                    val destination = if (fromServer) client ?: continue else server
                    val bytes = packet.data.copyOfRange(packet.offset, packet.offset + packet.length)
                    if (!fromServer) {
                        try {
                            val frame = UdpFrame.decode(bytes)
                            if (frame.packetType == UdpPacketType.REQ &&
                                ApiMessageCodec.decodeRequest(frame.payload.toString(Charsets.UTF_8)).api == ApiTags.CLAIM_CONTROL) {
                                controlClaims.incrementAndGet()
                            }
                        } catch (_: Exception) { }
                    }
                    socket.send(DatagramPacket(bytes, bytes.size, destination))
                } catch (_: Exception) {
                    if (socket.isClosed) break
                }
            }
        }, "review-runtime-link-proxy").also { it.isDaemon = true; it.start() }
        override fun close() { socket.close(); receiver.join(1000) }
    }

    @Test fun realUdpCanRecoverTwiceAfterOldTransportResourcesAreReleased() {
        UdpTableServer().use { server ->
            server.start()
            LinkProxy(server.boundPort).use { link ->
                ServerConnectionSession.connectUdp("127.0.0.1", link.port).get(4, TimeUnit.SECONDS).use { runtime ->
                    val initial = runtime.createRoom().get(4, TimeUnit.SECONDS)
                    val restored = AtomicReference<CountDownLatch?>()
                    val reconnects = AtomicInteger()
                    runtime.onStateChange { state ->
                        when (state) {
                            ConnectionState.RECONNECTING -> {
                                reconnects.incrementAndGet()
                                link.disconnected = false
                            }
                            ConnectionState.CONNECTED -> restored.get()?.countDown()
                            else -> Unit
                        }
                    }
                    repeat(2) { cycle ->
                        val ready = CountDownLatch(1)
                        restored.set(ready)
                        link.disconnected = true
                        assertTrue(ready.await(18, TimeUnit.SECONDS), "Recovery ${cycle + 1} never completed")
                        val current = runtime.snapshot().get(4, TimeUnit.SECONDS)
                        assertEquals(initial.sessionId, current.sessionId)
                        assertEquals(initial.viewerSeat, current.viewerSeat)
                        assertEquals(ConnectionState.CONNECTED, runtime.connectionState)
                        assertFalse(runtime.closed)
                        if (cycle == 0) {
                            // Old UdpClient cleanup runs after two seconds. Let that cleanup run before outage #2.
                            val cleanupWindow = CountDownLatch(1)
                            CompletableFuture.delayedExecutor(2300, TimeUnit.MILLISECONDS).execute { cleanupWindow.countDown() }
                            assertTrue(cleanupWindow.await(4, TimeUnit.SECONDS))
                        }
                    }
                    assertEquals(2, reconnects.get())
                    assertEquals(0, link.controlClaims.get(), "Recovery must not automatically claim human control")
                }
            }
        }
    }

    @Test fun closingRuntimeEventuallyReleasesItsUdpReceiver() {
        UdpTableServer().use { server ->
            server.start()
            val existing = Thread.getAllStackTraces().keys.map { it.id }.toSet()
            val runtime = ServerConnectionSession.connectUdp("127.0.0.1", server.boundPort).get(4, TimeUnit.SECONDS)
            val ownedReceivers: List<Thread>
            try {
                runtime.createRoom().get(4, TimeUnit.SECONDS)
                ownedReceivers = Thread.getAllStackTraces().keys.filter {
                    it.name == "udp-client-receiver" && it.id !in existing
                }
                assertTrue(ownedReceivers.isNotEmpty(), "Expected a real transport receiver")
            } finally {
                runtime.close()
            }
            val cleanupWindow = CountDownLatch(1)
            CompletableFuture.delayedExecutor(2600, TimeUnit.MILLISECONDS).execute { cleanupWindow.countDown() }
            assertTrue(cleanupWindow.await(4, TimeUnit.SECONDS))
            assertTrue(ownedReceivers.none { it.isAlive }, "Runtime shutdown cancelled transport cleanup and leaked UDP receiver")
        }
    }
}

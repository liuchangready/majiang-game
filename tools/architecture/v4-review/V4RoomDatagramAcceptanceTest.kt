package com.majiang.v4lobby

import com.majiang.client.transport.udp.UdpClient
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.udp.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Lose the final complete room update, including every fragment when fragmented. */
@Timeout(20)
class V4RoomDatagramAcceptanceTest {
    private class RoomLossProxy(serverPort: Int, private val targetScore: Int) : AutoCloseable {
        private val socket = DatagramSocket(0, InetAddress.getLoopbackAddress())
        private val serverAddress = InetSocketAddress(InetAddress.getLoopbackAddress(), serverPort)
        private val timer = Executors.newSingleThreadScheduledExecutor { r ->
            Thread(r, "review-room-fragment-timer").apply { isDaemon = true }
        }
        private val fragmenter = Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
                val future = timer.schedule(task, delayMillis, TimeUnit.MILLISECONDS)
                return Cancellable { future.cancel(false) }
            }
        })
        private val heldFragments = mutableMapOf<Pair<Int, Int>, MutableList<ByteArray>>()
        private var clientAddress: SocketAddress? = null
        private val didDrop = AtomicBoolean(false)
        val dropped = CountDownLatch(1)
        val failed = CompletableFuture<Throwable>()
        val port: Int get() = socket.localPort
        private val worker = Thread({
            while (!socket.isClosed) {
                try {
                    val packet = DatagramPacket(ByteArray(2048), 2048)
                    socket.receive(packet)
                    val bytes = packet.data.copyOf(packet.length)
                    val fromServer = packet.socketAddress == serverAddress
                    val destination = if (fromServer) clientAddress ?: continue else {
                        clientAddress = packet.socketAddress
                        serverAddress
                    }
                    if (!fromServer) {
                        socket.send(DatagramPacket(bytes, bytes.size, destination))
                        continue
                    }
                    val frame = UdpFrame.decode(bytes)
                    val complete: UdpFrame
                    val datagrams: List<ByteArray>
                    if (frame.packetType == UdpPacketType.FRAG) {
                        val chunk = Fragmenter.decodeSubheader(frame.payload)
                        val key = frame.connectionId to chunk.msgId
                        heldFragments.getOrPut(key) { mutableListOf() }.add(bytes)
                        complete = fragmenter.reassemble(frame, serverAddress) ?: continue
                        datagrams = heldFragments.remove(key)!!
                    } else {
                        complete = frame
                        datagrams = listOf(bytes)
                    }
                    val room = if (complete.packetType == UdpPacketType.PUSH) {
                        runCatching { ApiMessageCodec.decodeRoomUpdatePush(complete.payload.toString(Charsets.UTF_8)) }.getOrNull()
                    } else null
                    if (room?.config?.matchConfig?.targetScore == targetScore && didDrop.compareAndSet(false, true)) {
                        dropped.countDown()
                        continue
                    }
                    for (raw in datagrams) socket.send(DatagramPacket(raw, raw.size, destination))
                } catch (error: Throwable) {
                    if (!socket.isClosed) failed.complete(error)
                    break
                }
            }
        }, "review-room-loss-proxy").apply { isDaemon = true; start() }
        override fun close() {
            socket.close()
            worker.join(1000)
            fragmenter.close()
            timer.shutdownNow()
        }
    }

    private fun <T> CompletableFuture<T>.await(): T = get(4, TimeUnit.SECONDS)

    @Test fun lostFinalRoomConfigurationUpdateRecoversWithoutAnotherUserOperation() {
        UdpTableServer().use { server ->
            server.start()
            RoomLossProxy(server.boundPort, targetScore = 34567).use { proxy ->
                UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                    UdpClient.connect("127.0.0.1", proxy.port).await().use { guest ->
                        val room = host.createRoom(CreateRoomRequest()).await()
                        guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                        val initial = guest.getRoomInfo().await()
                        val changed = initial.config.copy(matchConfig = initial.config.matchConfig.copy(targetScore = 34567))
                        val recovered = CompletableFuture<ProtocolRoomInfo>()
                        guest.onRoomUpdate { if (it.config == changed) recovered.complete(it) }.use {
                            assertEquals(ProtocolReceiptStatus.APPLIED, host.updateConfig(changed).await().status)
                            assertTrue(proxy.dropped.await(3, TimeUnit.SECONDS), "Proxy must actually lose the target room update")
                            // No query, ready toggle, or other user operation is allowed to trigger recovery here.
                            val update = recovered.await()
                            assertEquals(room.roomId, update.roomId)
                            assertEquals(changed, update.config)
                            assertTrue(update.version > initial.version)
                            assertFalse(proxy.failed.isDone, "Packet proxy must remain healthy")
                        }
                    }
                }
            }
        }
    }
}

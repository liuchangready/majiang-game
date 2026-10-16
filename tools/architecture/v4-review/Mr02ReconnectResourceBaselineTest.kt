package com.majiang.v4runtime

import com.majiang.client.api.ConnectionState
import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.transport.udp.ScheduledExecutorUdpScheduler
import com.majiang.client.transport.udp.UdpClient
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.udp.UdpTableServer
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Windows process-level endpoint observations plus actual thread identity/lifecycle checks. */
@Timeout(180)
class Mr02ReconnectResourceBaselineTest {
    private fun endpoints(): Set<String> {
        val pid = ProcessHandle.current().pid().toString()
        val process = ProcessBuilder("netstat.exe", "-ano", "-p", "udp").redirectErrorStream(true).start()
        val text = process.inputStream.bufferedReader().readText()
        assertTrue(process.waitFor(5, TimeUnit.SECONDS), "netstat did not finish")
        assertEquals(0, process.exitValue(), text)
        return text.lineSequence().map { it.trim().split(Regex("\\s+")) }
            .filter { it.size >= 4 && it[0] == "UDP" && it.last() == pid }
            .map { it[1] }.toSet()
    }

    private fun threads() = Thread.getAllStackTraces().keys.filter { it.isAlive }.associateBy { it.id }
    private fun eventually(message: String, condition: () -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(8)
        while (!condition()) {
            assertTrue(System.nanoTime() < deadline, message)
            Thread.sleep(100)
        }
    }

    private class Link(serverPort: Int) : AutoCloseable {
        private val server = InetSocketAddress(InetAddress.getLoopbackAddress(), serverPort)
        private val socket = DatagramSocket(0, InetAddress.getLoopbackAddress())
        @Volatile var drop = false
        @Volatile private var client: SocketAddress? = null
        val failure = AtomicReference<Throwable?>()
        val port get() = socket.localPort
        val receiver = Thread({
            while (!socket.isClosed) {
                try {
                    val packet = DatagramPacket(ByteArray(2048), 2048)
                    socket.receive(packet)
                    val fromServer = packet.socketAddress == server
                    if (!fromServer) client = packet.socketAddress
                    if (drop) continue
                    val destination = if (fromServer) client ?: continue else server
                    socket.send(DatagramPacket(packet.data, packet.length, destination))
                } catch (t: Exception) {
                    if (!socket.isClosed) failure.compareAndSet(null, t)
                }
            }
        }, "review-resource-proxy").apply { isDaemon = true; start() }
        override fun close() { socket.close(); receiver.join(2000) }
    }

    private fun sample(label: String) {
        println("RESOURCE_BASELINE $label pid=${ProcessHandle.current().pid()} udp=${endpoints().sorted()} threads=${threads().size}")
    }

    @Test fun playerAndSpectatorRepeatedOutagesReleaseSocketsAndThreads() {
        assertTrue(System.getProperty("os.name").startsWith("Windows"), "This probe requires Windows netstat")
        // Prime JVM-wide delay infrastructure before measuring owned resource changes.
        CompletableFuture.runAsync({}, CompletableFuture.delayedExecutor(1, TimeUnit.MILLISECONDS)).get(5, TimeUnit.SECONDS)
        UdpTableServer().use { warm ->
            warm.start()
            ServerConnectionSession.connectUdp("127.0.0.1", warm.boundPort).get(5, TimeUnit.SECONDS).use {
                it.createRoom().get(5, TimeUnit.SECONDS)
            }
        }
        Thread.sleep(3500)
        val initialEndpoints = endpoints()
        val initialThreads = threads()
        sample("initial")
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).get(5, TimeUnit.SECONDS).use { host ->
                val room = host.createRoom(CreateRoomRequest()).get(5, TimeUnit.SECONDS)
                for (spectator in listOf(false, true)) {
                    val role = if (spectator) "spectator" else "player"
                    val beforeRoleEndpoints = endpoints()
                    val beforeRoleThreads = threads()
                    Link(server.boundPort).use { link ->
                        val runtime = ServerConnectionSession.connectUdp("127.0.0.1", link.port).get(5, TimeUnit.SECONDS)
                        val scheduler = runtime.scheduler as ScheduledExecutorUdpScheduler
                        try {
                            val sessionId = if (spectator) runtime.joinSpectator(room.roomId).get(5, TimeUnit.SECONDS).sessionId
                                else runtime.createRoom().get(5, TimeUnit.SECONDS).sessionId
                            val restored = AtomicReference<CountDownLatch?>()
                            runtime.onStateChange { state ->
                                if (state == ConnectionState.RECONNECTING) link.drop = false
                                if (state == ConnectionState.CONNECTED) restored.get()?.countDown()
                            }.use {
                                val liveCount = endpoints().size
                                sample("$role-connected")
                                repeat(3) { cycle ->
                                    val oldConnection = runtime.currentConnection
                                    val oldReceivers = threads().values.filter {
                                        it.name == "udp-client-receiver" && it.id !in beforeRoleThreads
                                    }
                                    assertEquals(1, oldReceivers.size, "Exactly one live receiver before outage")
                                    val ready = CountDownLatch(1)
                                    restored.set(ready)
                                    link.drop = true
                                    assertTrue(ready.await(18, TimeUnit.SECONDS), "$role recovery ${cycle + 1} timed out")
                                    assertNotSame(oldConnection, runtime.currentConnection)
                                    assertTrue(oldConnection.closed)
                                    eventually("Old receiver did not exit") { oldReceivers.none { it.isAlive } }
                                    eventually("UDP endpoint count grew after recovery") { endpoints().size == liveCount }
                                    assertSame(scheduler, runtime.scheduler)
                                    assertFalse(scheduler.isShutdown)
                                    val ran = CountDownLatch(1)
                                    scheduler.schedule(0) { ran.countDown() }
                                    assertTrue(ran.await(2, TimeUnit.SECONDS), "Session scheduler must still execute work")
                                    val actualId = if (spectator) runtime.publicSnapshot().get(5, TimeUnit.SECONDS).sessionId
                                        else runtime.snapshot().get(5, TimeUnit.SECONDS).sessionId
                                    assertEquals(sessionId, actualId)
                                    assertNull(link.failure.get())
                                    sample("$role-recovered-${cycle + 1}")
                                }
                            }
                        } finally { runtime.close() }
                        eventually("Session scheduler not shut down") { scheduler.isShutdown }
                    }
                    eventually("Role sockets did not return to exact baseline") { endpoints() == beforeRoleEndpoints }
                    eventually("Role transport threads survived close") {
                        threads().values.none { it.id !in beforeRoleThreads &&
                            (it.name == "udp-client-receiver" || it.name == "udp-scheduler" || it.name == "review-resource-proxy") }
                    }
                    sample("$role-closed")
                }
            }
        }
        eventually("All UDP endpoints must return to initial baseline") { endpoints() == initialEndpoints }
        eventually("New fixture threads remain after all resources close") {
            threads().keys.all { it in initialThreads }
        }
        sample("final")
    }
}

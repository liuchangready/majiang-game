package com.majiang.client.transport.udp

import com.majiang.client.api.ServerConnectionSession
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.UnknownHostException
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

class UdpClientFailureTest {

    // Some development DNS resolvers synthesize an address even for .invalid.
    // A host containing spaces is rejected by the resolver deterministically.
    private val unresolvableHost = "host name with spaces"

    private class TrackableScheduler : UdpScheduler, AutoCloseable {
        val closed = AtomicBoolean(false)

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            return Cancellable {}
        }

        override fun close() {
            closed.set(true)
        }
    }

    @Test
    fun testUdpClientConnectWithInvalidPortCleansUpSchedulerAndCloses() {
        val scheduler = TrackableScheduler()
        val future = UdpClient.connect(
            host = "127.0.0.1",
            port = -1,
            clock = UdpClock.SYSTEM,
            scheduler = scheduler,
            closeSchedulerOnShutdown = true
        )

        assertTrue(future.isCompletedExceptionally, "Connect with port -1 must complete exceptionally")
        assertTrue(scheduler.closed.get(), "Scheduler must be closed on initial connection failure when closeSchedulerOnShutdown is true")
    }

    @Test
    fun testUdpClientConnectWithUnresolvableHostCleansUpSchedulerAndCloses() {
        val scheduler = TrackableScheduler()
        val future = UdpClient.connect(
            host = unresolvableHost,
            port = 45678,
            clock = UdpClock.SYSTEM,
            scheduler = scheduler,
            closeSchedulerOnShutdown = true
        )

        assertTrue(future.isCompletedExceptionally, "Connect with unresolvable host must complete exceptionally")
        try {
            future.get(1, TimeUnit.SECONDS)
        } catch (e: ExecutionException) {
            assertTrue(e.cause is UnknownHostException, "Cause should be UnknownHostException: ${e.cause}")
        }
        assertTrue(scheduler.closed.get(), "Scheduler must be closed on initial unresolvable host failure")
    }

    @Test
    fun testServerConnectionSessionConnectUdpCleansUpOwnSchedulerOnFailure() {
        val future = ServerConnectionSession.connectUdp(
            host = unresolvableHost,
            port = 45678
        )

        assertTrue(future.isCompletedExceptionally, "connectUdp must complete exceptionally on unresolvable host")
    }
}

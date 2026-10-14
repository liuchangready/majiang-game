package com.majiang.client.transport.udp

import com.majiang.client.session.ServerConnectionSession
import com.majiang.server.api.ApiProtocol
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertInstanceOf
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.UnknownHostException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class UdpClientFailureTest {

    // Some development DNS resolvers synthesize an address even for .invalid.
    // A host containing spaces is rejected by the resolver deterministically.
    private val unresolvableHost = "host name with spaces"

    private class TrackableScheduler(
        private val delegate: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor { r ->
            Thread(r, "trackable-scheduler").apply { isDaemon = true }
        }
    ) : UdpScheduler, AutoCloseable {
        val closed = AtomicBoolean(false)
        val closeCount = AtomicInteger(0)
        val taskExecutedLatch = CountDownLatch(1)

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val future = delegate.schedule({
                try {
                    task.run()
                } finally {
                    taskExecutedLatch.countDown()
                }
            }, delayMillis, TimeUnit.MILLISECONDS)
            return Cancellable {
                future.cancel(false)
            }
        }

        override fun close() {
            closeCount.incrementAndGet()
            if (closed.compareAndSet(false, true)) {
                delegate.shutdown()
            }
        }
    }

    private class SwitchableRejectingScheduler : UdpScheduler, AutoCloseable {
        val closed = AtomicBoolean(false)
        val closeCount = AtomicInteger(0)
        val released = CountDownLatch(1)
        val tasks = CopyOnWriteArrayList<Runnable>()
        @Volatile var reject = false

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            if (reject) {
                throw RejectedExecutionException("Simulated rejection")
            }
            tasks.add(task)
            return Cancellable { tasks.remove(task) }
        }

        override fun close() {
            closeCount.incrementAndGet()
            closed.set(true)
            released.countDown()
        }
    }

    @Test
    fun testUdpClientConnectWithInvalidPortCleansUpSchedulerAndCloses() {
        val scheduler = TrackableScheduler()
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = -1,
                clock = UdpClock.SYSTEM,
                scheduler = scheduler,
                closeSchedulerOnShutdown = true
            )

            assertTrue(future.isCompletedExceptionally, "Connect with port -1 must complete exceptionally")
            assertTrue(scheduler.closed.get(), "Scheduler must be closed on initial connection failure when closeSchedulerOnShutdown is true")
            assertEquals(1, scheduler.closeCount.get(), "Scheduler close must be called once")
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testUdpClientConnectWithUnresolvableHostCleansUpSchedulerAndCloses() {
        val scheduler = TrackableScheduler()
        try {
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
            assertEquals(1, scheduler.closeCount.get(), "Scheduler close must be called once")
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testServerConnectionSessionConnectUdpCleansUpOwnSchedulerOnFailure() {
        val future = ServerConnectionSession.connectUdp(
            host = unresolvableHost,
            port = 45678
        )

        assertTrue(future.isCompletedExceptionally, "connectUdp must complete exceptionally on unresolvable host")

        val extScheduler = TrackableScheduler()
        try {
            val injectedFuture = ServerConnectionSession.connectUdp(
                host = unresolvableHost,
                port = 45678,
                scheduler = extScheduler
            )
            assertTrue(injectedFuture.isCompletedExceptionally, "connectUdp with injected scheduler must fail on bad host")
            assertFalse(extScheduler.closed.get(), "Externally supplied scheduler must not be closed on failure by session")
        } finally {
            extScheduler.close()
        }
    }

    @Test
    fun testUdpClientConnectWithInjectedSchedulerDefaultsToCallerOwnedOnFailure() {
        val scheduler = TrackableScheduler()
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = -1,
                clock = UdpClock.SYSTEM,
                scheduler = scheduler
                // closeSchedulerOnShutdown is omitted; defaults to false for externally injected scheduler
            )

            assertTrue(future.isCompletedExceptionally, "Connect with port -1 must complete exceptionally")
            assertFalse(scheduler.closed.get(), "Externally injected scheduler must remain open by default on connection failure")
            assertEquals(0, scheduler.closeCount.get(), "Scheduler must not be closed")
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testUdpClientConnectWithInjectedSchedulerExplicitFalsePreservesSchedulerOnFailure() {
        val scheduler = TrackableScheduler()
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = -1,
                clock = UdpClock.SYSTEM,
                scheduler = scheduler,
                closeSchedulerOnShutdown = false
            )

            assertTrue(future.isCompletedExceptionally, "Connect with port -1 must complete exceptionally")
            assertFalse(scheduler.closed.get(), "Scheduler must remain open when closeSchedulerOnShutdown is false")
            assertEquals(0, scheduler.closeCount.get(), "Scheduler must not be closed")
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testUdpClientConnectWithDefaultSchedulerFailsGracefullyAndReleases() {
        var created: ScheduledExecutorUdpScheduler? = null
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = -1,
                schedulerFactory = { ScheduledExecutorUdpScheduler().also { created = it } }
            )

            assertTrue(future.isCompletedExceptionally, "Connect with default scheduler must complete exceptionally")
            assertNotNull(created, "Default internal scheduler must have been instantiated")
            assertTrue(created!!.isShutdown, "Default internal scheduler must be shut down on failure")
            assertThrows(RejectedExecutionException::class.java) {
                created!!.schedule(10) {}
            }
        } finally {
            created?.close()
        }
    }

    @Test
    fun testUdpClientConnectWithInternalSchedulerExplicitFalseReleasesOnFailure() {
        var created: ScheduledExecutorUdpScheduler? = null
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = -1,
                closeSchedulerOnShutdown = false,
                schedulerFactory = { ScheduledExecutorUdpScheduler().also { created = it } }
            )

            assertTrue(future.isCompletedExceptionally, "Connect with invalid port must fail")
            assertNotNull(created, "Internal scheduler must have been instantiated")
            assertTrue(created!!.isShutdown, "Internal scheduler must be shut down on failure even with closeSchedulerOnShutdown=false")
            assertThrows(RejectedExecutionException::class.java) {
                created!!.schedule(10) {}
            }
        } finally {
            created?.close()
        }
    }

    @Test
    fun testUdpClientConnectWithInjectedSchedulerDefaultsToCallerOwnedOnNormalClose() {
        val scheduler = TrackableScheduler()
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = 45678,
                scheduler = scheduler
            )

            val binding = future.get(1, TimeUnit.SECONDS)
            try {
                binding.close()
                val taskRan = scheduler.taskExecutedLatch.await(3500, TimeUnit.MILLISECONDS)
                assertTrue(taskRan, "Close task scheduled at ~2s must have executed")
                assertFalse(scheduler.closed.get(), "Externally injected scheduler must remain open after close task executes")
                assertEquals(0, scheduler.closeCount.get(), "Externally injected scheduler must not be closed")
            } finally {
                binding.close()
            }
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testUdpClientConnectWithInjectedSchedulerExplicitTrueClosesOnNormalClose() {
        val scheduler = TrackableScheduler()
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = 45678,
                scheduler = scheduler,
                closeSchedulerOnShutdown = true
            )

            val binding = future.get(1, TimeUnit.SECONDS)
            try {
                binding.close()
                val deadline = System.currentTimeMillis() + 3500
                while (!scheduler.closed.get() && System.currentTimeMillis() < deadline) {
                    Thread.sleep(50)
                }
                assertTrue(scheduler.closed.get(), "Scheduler must be closed after 2s close window when closeSchedulerOnShutdown is true")
                assertEquals(1, scheduler.closeCount.get(), "Scheduler must be closed exactly once")
            } finally {
                binding.close()
            }
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testUdpClientConnectWithDefaultSchedulerClosesOnNormalClose() {
        val future = UdpClient.connect(
            host = "127.0.0.1",
            port = 45678
        )

        val binding = future.get(1, TimeUnit.SECONDS)
        try {
            val scheduler = binding.scheduler
            assertInstanceOf(ScheduledExecutorUdpScheduler::class.java, scheduler)
            val internalScheduler = scheduler as ScheduledExecutorUdpScheduler
            assertFalse(internalScheduler.isShutdown, "Scheduler must be active while connection is open")

            binding.close()
            val deadline = System.currentTimeMillis() + 3500
            while (!internalScheduler.isShutdown && System.currentTimeMillis() < deadline) {
                Thread.sleep(50)
            }
            assertTrue(internalScheduler.isShutdown, "Default internal scheduler must be shut down after close window")
            assertThrows(RejectedExecutionException::class.java) {
                scheduler.schedule(10) {}
            }
        } finally {
            binding.close()
        }
    }

    @Test
    fun testUdpClientConnectWithInternalSchedulerExplicitFalseClosesOnNormalClose() {
        val future = UdpClient.connect(
            host = "127.0.0.1",
            port = 45678,
            closeSchedulerOnShutdown = false
        )

        val binding = future.get(1, TimeUnit.SECONDS)
        try {
            val scheduler = binding.scheduler
            assertInstanceOf(ScheduledExecutorUdpScheduler::class.java, scheduler)
            val internalScheduler = scheduler as ScheduledExecutorUdpScheduler
            assertFalse(internalScheduler.isShutdown, "Scheduler must be active while connection is open")

            binding.close()
            val deadline = System.currentTimeMillis() + 3500
            while (!internalScheduler.isShutdown && System.currentTimeMillis() < deadline) {
                Thread.sleep(50)
            }
            assertTrue(internalScheduler.isShutdown, "Internal scheduler must be shut down even if closeSchedulerOnShutdown is false")
            assertThrows(RejectedExecutionException::class.java) {
                scheduler.schedule(10) {}
            }
        } finally {
            binding.close()
        }
    }

    @Test
    fun testUdpClientCloseSocketFallbackWhenSchedulerRejectsTaskAfterStartup() {
        val scheduler = SwitchableRejectingScheduler()
        val future = UdpClient.connect(
            host = "127.0.0.1",
            port = 45678,
            scheduler = scheduler,
            closeSchedulerOnShutdown = true
        )

        val binding = future.get(1, TimeUnit.SECONDS)
        try {
            // Switch to rejecting tasks only after successful connection and heartbeat startup
            scheduler.reject = true
            binding.close()
            val deadline = System.currentTimeMillis() + 3500
            while (!scheduler.closed.get() && System.currentTimeMillis() < deadline) {
                Thread.sleep(50)
            }
            assertTrue(scheduler.closed.get(), "Fallback delayedExecutor must close socket and scheduler when scheduler.schedule rejects on close")
            assertEquals(1, scheduler.closeCount.get(), "Scheduler must be closed once by fallback")
        } finally {
            binding.close()
        }
    }

    @Test
    fun testUdpClientRejectionDuringStartupFailsConnectionAndReleasesOwnedScheduler() {
        val scheduler = SwitchableRejectingScheduler().apply { reject = true }
        val future = UdpClient.connect(
            host = "127.0.0.1",
            port = 45678,
            scheduler = scheduler,
            closeSchedulerOnShutdown = true
        )

        assertTrue(future.isCompletedExceptionally, "Connect must fail when scheduler rejects heartbeat during startup")
        val failure = assertThrows(ExecutionException::class.java) { future.get(1, TimeUnit.SECONDS) }
        assertInstanceOf(RejectedExecutionException::class.java, failure.cause)
        assertTrue(scheduler.closed.get(), "Owned scheduler must be closed on startup failure")
        assertEquals(1, scheduler.closeCount.get(), "Scheduler must be closed once")
    }

    @Test
    fun testUdpClientRejectionDuringStartupPreservesCallerOwnedSchedulerWhenNotTransferred() {
        val scheduler = SwitchableRejectingScheduler().apply { reject = true }
        try {
            val future = UdpClient.connect(
                host = "127.0.0.1",
                port = 45678,
                scheduler = scheduler,
                closeSchedulerOnShutdown = false
            )

            assertTrue(future.isCompletedExceptionally, "Connect must fail when scheduler rejects during startup")
            assertFalse(scheduler.closed.get(), "External scheduler must remain open on startup failure when closeSchedulerOnShutdown is false")
            assertEquals(0, scheduler.closeCount.get(), "Scheduler must not be closed")
        } finally {
            scheduler.close()
        }
    }

    @Test
    fun testUdpClientCloseIsIdempotent() {
        val scheduler = TrackableScheduler()
        val future = UdpClient.connect(
            host = "127.0.0.1",
            port = 45678,
            scheduler = scheduler,
            closeSchedulerOnShutdown = true
        )

        val binding = future.get(1, TimeUnit.SECONDS)
        try {
            binding.close()
            binding.close()
            assertTrue(binding.closed, "Binding must report closed after close()")
            assertEquals(ApiProtocol.LOCAL_CLOSED, binding.disconnectReason)

            val deadline = System.currentTimeMillis() + 3500
            while (!scheduler.closed.get() && System.currentTimeMillis() < deadline) {
                Thread.sleep(50)
            }
            assertTrue(scheduler.closed.get(), "Transferred scheduler must be closed after close window")
            assertEquals(1, scheduler.closeCount.get(), "Scheduler must be closed exactly once even after repeated close()")
        } finally {
            binding.close()
            scheduler.close()
        }
    }
}

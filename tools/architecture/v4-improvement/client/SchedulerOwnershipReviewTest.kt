package com.majiang.review

import com.majiang.client.transport.udp.UdpClient
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.net.DatagramSocket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class SchedulerOwnershipReviewTest {
    @Test fun internallyCreatedSchedulerClosesEvenWithExplicitFalse() {
        DatagramSocket(0).use { peer ->
            val binding = UdpClient.connect("127.0.0.1", peer.localPort,
                closeSchedulerOnShutdown = false).get()
            val scheduler = binding.scheduler
            try {
                binding.close()
                val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
                var rejected = false
                while (System.nanoTime() < deadline && !rejected) {
                    try {
                        scheduler.schedule(0, Runnable {}).cancel()
                        Thread.sleep(20)
                    } catch (_: RejectedExecutionException) {
                        rejected = true
                    }
                }
                assertTrue(rejected, "Internal scheduler must reject new work after closing even with explicit false")
            } finally {
                binding.close()
                (scheduler as? AutoCloseable)?.close()
            }
        }
    }

    private class Scheduler : UdpScheduler, AutoCloseable {
        val closeCount = AtomicInteger()
        val released = CountDownLatch(1)
        val tasks = CopyOnWriteArrayList<Runnable>()
        @Volatile var reject = false
        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            if (reject) throw RejectedExecutionException("review rejection")
            tasks.add(task)
            return Cancellable { tasks.remove(task) }
        }
        override fun close() {
            closeCount.incrementAndGet()
            released.countDown()
        }
    }

    @Test fun sharedSchedulerSurvivesActualCloseTask() {
        DatagramSocket(0).use { peer ->
            val scheduler = Scheduler()
            val binding = UdpClient.connect("127.0.0.1", peer.localPort, scheduler = scheduler).get()
            try {
                binding.close()
                assertEquals(1, scheduler.tasks.size, "Heartbeat must be cancelled, leaving the close task")
                scheduler.tasks.toList().forEach { it.run() }
                assertEquals(0, scheduler.closeCount.get())
            } finally { binding.close(); scheduler.close() }
        }
    }

    @Test fun transferredSchedulerIsClosedExactlyOnceAfterRepeatedClose() {
        DatagramSocket(0).use { peer ->
            val scheduler = Scheduler()
            val binding = UdpClient.connect("127.0.0.1", peer.localPort,
                scheduler = scheduler, closeSchedulerOnShutdown = true).get()
            try {
                binding.close()
                binding.close()
                assertEquals(0, scheduler.closeCount.get(), "Preserve the terminal ACK window")
                val tasks = scheduler.tasks.toList()
                tasks.forEach { it.run(); it.run() }
                assertEquals(1, scheduler.closeCount.get())
            } finally { binding.close() }
        }
    }

    @Test fun rejectionAfterSuccessfulStartupStillReleasesOwnedScheduler() {
        DatagramSocket(0).use { peer ->
            val scheduler = Scheduler()
            val binding = UdpClient.connect("127.0.0.1", peer.localPort,
                scheduler = scheduler, closeSchedulerOnShutdown = true).get()
            try {
                scheduler.reject = true
                binding.close()
                assertTrue(scheduler.released.await(5, TimeUnit.SECONDS), "Fallback must release after close-task rejection")
                assertEquals(1, scheduler.closeCount.get())
            } finally { binding.close() }
        }
    }

    @Test fun rejectionDuringStartupFailsConnectionAndReleasesOwnedScheduler() {
        DatagramSocket(0).use { peer ->
            val scheduler = Scheduler().apply { reject = true }
            val result = UdpClient.connect("127.0.0.1", peer.localPort,
                scheduler = scheduler, closeSchedulerOnShutdown = true)
            val failure = assertThrows(ExecutionException::class.java) { result.get() }
            assertInstanceOf(RejectedExecutionException::class.java, failure.cause)
            assertEquals(1, scheduler.closeCount.get())
        }
    }
}

package com.majiang.review

import com.majiang.transport.udp.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Converts the architecture-review probes into repeatable protocol contracts. */
class FragmentBaselineReviewTest {
    private class Time : UdpClock, UdpScheduler {
        private data class Job(val at: Long, val task: Runnable, var cancelled: Boolean = false)
        private val jobs = mutableListOf<Job>()
        private var now = 1000L
        override fun currentTimeMillis() = now
        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val job = Job(now + delayMillis, task)
            jobs.add(job)
            return Cancellable { job.cancelled = true }
        }
        fun advance(delta: Long) {
            now += delta
            while (true) {
                val job = jobs.filter { !it.cancelled && it.at <= now }.minByOrNull { it.at } ?: break
                jobs.remove(job)
                job.task.run()
            }
        }
    }

    @Test fun fourFragmentsConsumeOneReassemblySlotPerMessageAndExpire() {
        val time = Time()
        Fragmenter(time, time).use { sender ->
            Fragmenter(time, time).use { receiver ->
                val original = UdpFrame(UdpPacketType.REQ, 123, 1, payload = ByteArray(4000) { (it % 251).toByte() })
                var seq = 0
                repeat(3) { attempt ->
                    val packets = sender.fragment(original) { ++seq }
                    assertEquals(4, packets.size)
                    packets.dropLast(1).forEach { assertNull(receiver.reassemble(it)) }
                    assertEquals(attempt + 1, receiver.getInFlightCount(), "Slots count messages, not fragments")
                }
                time.advance(2999)
                assertEquals(3, receiver.getInFlightCount())
                time.advance(1)
                assertEquals(0, receiver.getInFlightCount())
                val results = sender.fragment(original) { ++seq }.reversed().mapNotNull { receiver.reassemble(it) }
                assertEquals(1, results.size)
                assertArrayEquals(original.payload, results.single().payload)
            }
        }
    }

    @Test fun replayWindowAcceptsUnseenOutOfOrderSequenceButRejectsDuplicates() {
        val guard = ReplayGuard(expectedConnectionId = 123)
        assertTrue(guard.accept(3))
        assertTrue(guard.accept(1))
        assertTrue(guard.accept(2))
        assertFalse(guard.accept(1))
        assertFalse(guard.accept(3))
        assertFalse(guard.validateConnectionId(456))
    }

    @Test fun maskingSequenceOverflowDoesNotMakeOldConnectionWindowReusable() {
        val guard = ReplayGuard(initialMaxSeen = Int.MAX_VALUE)
        val wrapped = ((Int.MAX_VALUE.toLong() + 1) and 0x7fffffffL).toInt()
        assertEquals(0, wrapped)
        assertFalse(guard.accept(wrapped))
        assertFalse(guard.accept(1))
        val freshConnection = ReplayGuard(expectedConnectionId = 456)
        assertTrue(freshConnection.accept(1))
    }
}

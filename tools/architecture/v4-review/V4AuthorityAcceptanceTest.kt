package com.majiang.v4authority

import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.udp.UdpRoom
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolReceiptStatus
import java.net.InetSocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** R4 gates. Keep separate from transport acceptance until authority migration is delivered. */
@Timeout(15)
class V4AuthorityAcceptanceTest {
    private fun <T> CompletableFuture<T>.await(): T = get(3, TimeUnit.SECONDS)
    @Test fun slowUpdateObserverDoesNotBlockCallerOrAuthoritativeQueries() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val armed = AtomicBoolean(false)
        val callers = Executors.newFixedThreadPool(2)
        val server = InProcessServer()
        try {
            val connection = server.createLocalTable()
            connection.onUpdate {
                if (armed.compareAndSet(true, false)) {
                    entered.countDown()
                    check(release.await(8, TimeUnit.SECONDS)) { "Test observer was never released" }
                }
            }.use {
                armed.set(true)
                val invocation = callers.submit<CompletableFuture<*>> { connection.releaseControl() }
                assertTrue(entered.await(3, TimeUnit.SECONDS), "Control transition must publish an update")
                // Returning a Future must not require waiting for application/UI callbacks.
                val receipt = invocation.get(2, TimeUnit.SECONDS)
                val queryInvocation = CompletableFuture.supplyAsync({ connection.snapshot() }, callers)
                val snapshot = queryInvocation.get(2, TimeUnit.SECONDS).get(2, TimeUnit.SECONDS)
                assertEquals("TAKEOVER", snapshot.controls.first { control -> control.seat == 0 }.role)
                release.countDown()
                receipt.get(2, TimeUnit.SECONDS)
            }
        } finally {
            release.countDown()
            callers.shutdownNow()
            server.close()
        }
    }

    @Test fun competingChoicesForOneDecisionCommitOnlyOneDiscard() {
        InProcessServer().use { server ->
            val connection = server.createLocalTable()
            assertEquals(ProtocolReceiptStatus.APPLIED, connection.start().await().status)
            val decision = connection.decision().await()
            val option = decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
            val first = ProtocolActionChoice("race-a", decision.roundId, decision.decisionId, decision.controlEpoch, option.optionId)
            val second = first.copy(requestId = "race-b")
            val start = CountDownLatch(1)
            val pool = Executors.newFixedThreadPool(2)
            try {
                val results = listOf(first, second).map { choice ->
                    pool.submit<com.majiang.transport.api.ProtocolActionReceipt> {
                        check(start.await(2, TimeUnit.SECONDS))
                        connection.submit(choice).await()
                    }
                }
                start.countDown()
                val receipts = results.map { it.get(3, TimeUnit.SECONDS) }
                assertEquals(1, receipts.count { it.status != ProtocolReceiptStatus.REJECTED })
                assertEquals(1, connection.snapshot().await().view.discards[0].size)
            } finally {
                start.countDown()
                pool.shutdownNow()
            }
        }
    }

    @Test fun invalidChoiceCannotRefreshDeadlineAndTimeoutCommitsOnce() {
        class Clock(var now: Long = 0L) : MonotonicClock { override fun nowMillis() = now }
        val clock = Clock()
        UdpRoom(com.majiang.server.room.SharedRoom("review-deadline", "Host", clock = clock)).use { room ->
            val (session, _) = room.bindHost(1001, InetSocketAddress("127.0.0.1", 40123)).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, room.setReady(session, true).await().status)
            assertEquals(ProtocolReceiptStatus.APPLIED, room.startMatch(session).await().status)
            val before = room.querySnapshot(session).await()
            val decision = before.decision
            val option = decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
            val choice = ProtocolActionChoice("deadline-choice", decision.roundId, decision.decisionId,
                decision.controlEpoch, option.optionId)
            val deadline = before.clocks.first { it.seat == 0 }.deadline
            clock.now = deadline - 1
            assertEquals(ProtocolReceiptStatus.REJECTED,
                room.submitChoice(session, choice.copy(requestId = "invalid", optionId = "invalid-option")).await().status)
            assertEquals(deadline, room.querySnapshot(session).await().clocks.first { it.seat == 0 }.deadline)
            clock.now = deadline
            assertEquals(ProtocolReceiptStatus.REJECTED, room.submitChoice(session, choice).await().status)
            room.tick()
            assertEquals(1, room.querySnapshot(session).await().view.discards[0].size)
            room.tick()
            assertEquals(1, room.querySnapshot(session).await().view.discards[0].size)
        }
    }
}

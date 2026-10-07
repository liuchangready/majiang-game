package com.majiang.v4authority

import com.majiang.ai.AIStrategy
import com.majiang.control.DisconnectPolicy
import com.majiang.server.host.ServerTableHost
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import java.util.concurrent.CompletableFuture
import java.util.concurrent.AbstractExecutorService
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

@Timeout(15)
class V4ServerHostAcceptanceTest {
    private class Clock(@Volatile var now: Long = 0L) : MonotonicClock { override fun nowMillis() = now }
    private fun <T> CompletableFuture<T>.await(): T = get(3, TimeUnit.SECONDS)
    private class StalledAi : AIStrategy {
        override val name = "review stalled AI"
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            entered.countDown()
            check(release.await(8, TimeUnit.SECONDS))
            return legalActions.firstOrNull { it is GameAction.DiscardTile } ?: legalActions.firstOrNull()
        }
    }

    private class PausedExecutor : AbstractExecutorService() {
        private val tasks = java.util.ArrayDeque<Runnable>()
        private var stopped = false
        override fun execute(task: Runnable) { check(!stopped); tasks.add(task) }
        override fun shutdown() { stopped = true }
        override fun shutdownNow(): MutableList<Runnable> {
            stopped = true
            return tasks.toMutableList().also { tasks.clear() }
        }
        override fun isShutdown() = stopped
        override fun isTerminated() = stopped && tasks.isEmpty()
        override fun awaitTermination(timeout: Long, unit: TimeUnit) = isTerminated
        fun runQueued() { while (tasks.isNotEmpty()) tasks.removeFirst().run() }
    }

    @Test fun stalledAiCannotBlockQueriesOrAuthoritativeTimeout() {
        val clock = Clock()
        val ai = StalledAi()
        val host = ServerTableHost(clock = clock,
            controllers = listOf(SeatController.Computer(ai)) + List(3) { SeatController.RemoteHuman(false) })
        try {
            host.start(0).await()
            assertTrue(ai.entered.await(2, TimeUnit.SECONDS))
            val before = host.snapshot(0).await()
            clock.now = before.clocks.first { it.seat == 0 }.deadline
            host.tick()
            assertEquals(1, host.snapshot(0).await().view.discards[0].size)
        } finally {
            ai.release.countDown()
            host.close()
        }
    }

    @Test fun aiResultComputedBeforeHumanReclaimCannotCommitAfterward() {
        val clock = Clock()
        val ai = StalledAi()
        val executor = Executors.newSingleThreadExecutor()
        val host = ServerTableHost(clock = clock,
            controllers = List(4) { SeatController.RemoteHuman(false) },
            disconnectPolicy = DisconnectPolicy(aiProfileId = "review-stalled"),
            aiProfiles = mapOf("review-stalled" to { ai }), aiExecutor = executor)
        try {
            host.start(0).await()
            val originalDeadline = host.snapshot(0).await().clocks.first { it.seat == 0 }.deadline
            host.releaseControl(0).await()
            assertTrue(ai.entered.await(2, TimeUnit.SECONDS))
            host.claimControl(0).await()
            val regained = host.snapshot(0).await()
            assertEquals(originalDeadline, regained.clocks.first { it.seat == 0 }.deadline)
            ai.release.countDown()
            executor.submit { }.get(2, TimeUnit.SECONDS)
            clock.now = 150
            host.tick()
            assertTrue(host.snapshot(0).await().view.discards[0].isEmpty())
        } finally {
            ai.release.countDown()
            host.close()
            executor.shutdownNow()
        }
    }

    @Test fun slowReceiptConsumerCannotOccupyAuthorityQueue() {
        val host = ServerTableHost(controllers = List(4) { SeatController.RemoteHuman(false) })
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        try {
            // Force the callback to attach before domain start can finish.
            synchronized(host.authority) {
                host.start(0).thenAccept {
                    entered.countDown()
                    check(release.await(8, TimeUnit.SECONDS))
                }
            }
            assertTrue(entered.await(3, TimeUnit.SECONDS))
            assertEquals(14, host.snapshot(0).get(2, TimeUnit.SECONDS).view.myAllClosedTiles.size)
        } finally {
            release.countDown()
            host.close()
        }
    }

    @Test fun pausedAuthorityExecutorDoesNotBlockInvocationAndCloseEndsQueuedFutures() {
        val executor = PausedExecutor()
        val host = ServerTableHost(controllers = List(4) { SeatController.RemoteHuman(false) }, serialExecutor = executor)
        try {
            val originalPhase = host.authority.queryView(0).phase
            val start = host.start(0)
            val query = host.snapshot(0)
            val tick = host.tick()
            assertFalse(start.isDone)
            assertFalse(query.isDone)
            assertFalse(tick.isDone)
            assertEquals(originalPhase, host.authority.queryView(0).phase)
            host.close()
            assertTrue(start.isDone && query.isDone && tick.isDone, "Close must not strand queued futures")
            assertFalse(executor.isShutdown, "Injected executor belongs to its caller")
            executor.runQueued()
            assertEquals(originalPhase, host.authority.queryView(0).phase, "Cancelled queued work cannot mutate after close")
        } finally {
            host.close()
            executor.shutdownNow()
        }
    }

    @Test fun replacedIdentityIsRejectedWhenQueuedOperationsActuallyExecute() {
        val executor = PausedExecutor()
        val host = ServerTableHost(controllers = List(4) { SeatController.RemoteHuman(false) }, serialExecutor = executor)
        var activeIdentity = true
        try {
            val originalPhase = host.authority.queryView(0).phase
            val start = host.start(0) { activeIdentity }
            val query = host.snapshot(0) { activeIdentity }
            val control = host.releaseControl(0) { activeIdentity }
            activeIdentity = false
            executor.runQueued()
            assertEquals("STALE_SESSION", start.await().reason)
            assertEquals("STALE_SESSION", control.await().reason)
            val failure = assertFailsWith<java.util.concurrent.ExecutionException> { query.await() }
            assertEquals("STALE_SESSION", failure.cause?.message)
            assertEquals(originalPhase, host.authority.queryView(0).phase)
        } finally {
            host.close()
            executor.shutdownNow()
        }
    }

    @Test fun shuffleAndActionReceiptNamespacesRemainIndependentAfterLaterCommit() {
        val clock = Clock()
        val host = ServerTableHost(clock = clock,
            config = com.majiang.match.MatchConfig(shuffleConfig = com.majiang.stacking.ShuffleConfig(enabled = true)),
            controllers = List(4) { SeatController.RemoteHuman(false) })
        try {
            host.start(0).await()
            val initial = host.snapshot(0).await()
            val plan = com.majiang.stacking.ShufflePlanRequest("same-request", initial.roundId!!,
                initial.decision.controlEpoch, com.majiang.stacking.ShuffleSelection())
            val planReceipt = host.submitShufflePlan(0, plan).await()
            assertEquals(com.majiang.access.ReceiptStatus.RECORDED, planReceipt.status)
            clock.now = initial.preparation!!.deadlineMillis
            host.tick().await()
            val decision = host.decision(0).await()
            val option = decision.options.first { it.action is GameAction.DiscardTile }
            val action = com.majiang.application.ActionChoice("same-request", decision.roundId,
                decision.decisionId, decision.controlEpoch, option.optionId)
            val actionReceipt = host.submitChoice(0, action).await()
            assertNotEquals(com.majiang.access.ReceiptStatus.REJECTED, actionReceipt.status)
            assertEquals(planReceipt, host.submitShufflePlan(0, plan).await(), "Later action cannot overwrite a shuffle receipt")
            assertEquals(actionReceipt, host.submitChoice(0, action).await())
            assertEquals(1, host.snapshot(0).await().view.discards[0].size)
        } finally {
            host.close()
        }
    }
}

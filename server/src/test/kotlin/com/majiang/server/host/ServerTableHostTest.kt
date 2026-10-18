package com.majiang.server.host

import com.majiang.access.ReceiptStatus
import com.majiang.application.ActionChoice
import com.majiang.server.control.DisconnectPolicy
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GameAction
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import com.majiang.timing.TimeControlConfig
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

@Timeout(15)
class ServerTableHostTest {

    private class ManualClock(var current: Long = 1000L) : MonotonicClock {
        override fun nowMillis(): Long = current
    }

    @Test
    fun `failing seat listener is isolated and unsubscribed observer receives no later updates`() {
        ServerTableHost(config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            controllers = List(4) { SeatController.RemoteHuman(false) }).use { host ->
            val delivered = AtomicInteger()
            val firstTwo = CountDownLatch(2)
            val bad = host.subscribeSeat(0) { throw IllegalStateException("broken observer") }
            val good = host.subscribeSeat(0) {
                delivered.incrementAndGet()
                firstTwo.countDown()
            }
            try {
                assertEquals(ReceiptStatus.APPLIED, host.start(0).get(3, TimeUnit.SECONDS).status)
                assertTrue(firstTwo.await(3, TimeUnit.SECONDS), "Healthy observer missed start update")
                val seen = delivered.get()
                good.close()
                bad.close()
                val barrier = CountDownLatch(2)
                host.subscribeSeat(0) { barrier.countDown() }.use {
                    val decision = host.decision(0).get(3, TimeUnit.SECONDS)
                    val option = decision.options.first { it.action is GameAction.DiscardTile }
                    val choice = ActionChoice("after-unsubscribe", decision.roundId,
                        decision.decisionId, decision.controlEpoch, option.optionId)
                    val receipt = host.submitChoice(0, choice).get(3, TimeUnit.SECONDS)
                    assertTrue(receipt.status != ReceiptStatus.REJECTED)
                    assertTrue(barrier.await(3, TimeUnit.SECONDS), "Committed update did not reach active observer")
                    assertEquals(seen, delivered.get())
                }
            } finally {
                good.close()
                bad.close()
            }
        }
    }

    @Test
    fun `slow update observer does not block caller or authoritative queries on server host`() {
        val callers = Executors.newFixedThreadPool(2)
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val armed = AtomicBoolean(false)

        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            )
        )

        try {
            host.subscribeSeat(0) {
                if (armed.compareAndSet(true, false)) {
                    entered.countDown()
                    check(release.await(8, TimeUnit.SECONDS)) { "Observer was never released" }
                }
            }.use {
                armed.set(true)
                val invocation = callers.submit<CompletableFuture<*>> { host.releaseControl(0) }
                assertTrue(entered.await(3, TimeUnit.SECONDS), "Control transition must publish an update")

                val receiptFuture = invocation.get(2, TimeUnit.SECONDS)
                val queryInvocation = CompletableFuture.supplyAsync({ host.snapshot(0) }, callers)
                val snapshot = queryInvocation.get(2, TimeUnit.SECONDS).get(2, TimeUnit.SECONDS)
                assertEquals("TAKEOVER", snapshot.controls.first { it.seat == 0 }.role)

                release.countDown()
                val receipt = receiptFuture.get(2, TimeUnit.SECONDS) as com.majiang.access.ActionReceipt
                assertEquals(ReceiptStatus.APPLIED, receipt.status)
            }
        } finally {
            release.countDown()
            callers.shutdownNow()
            host.close()
        }
    }

    @Test
    fun `serial queue processes concurrent commands with monotonic cursor updates`() {
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            )
        )

        val receivedCursors = CopyOnWriteArrayList<Long>()
        val sub = host.subscribeSeat(0) { update ->
            receivedCursors.add(update.snapshot.cursor)
        }

        try {
            val startReceipt = host.start(0).get(2, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, startReceipt.status)

            val decision = host.decision(0).get(2, TimeUnit.SECONDS)
            assertTrue(decision.options.isNotEmpty())

            val discardOpt = decision.options.first { it.action is GameAction.DiscardTile }
            val choice = ActionChoice(
                requestId = "serial-req-1",
                roundId = decision.roundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                optionId = discardOpt.optionId
            )

            val submitReceipt = host.submitChoice(0, choice).get(2, TimeUnit.SECONDS)
            assertTrue(
                submitReceipt.status in setOf(ReceiptStatus.APPLIED, ReceiptStatus.RECORDED),
                "Expected successful discard status (APPLIED or RECORDED), got ${submitReceipt.status}"
            )
            assertNotNull(submitReceipt.cursor)
            assertTrue(submitReceipt.cursor!! > 0L)

            // 等待派发完成（有界轮询匹配预期游标）
            val expectedCursor = submitReceipt.cursor!!
            val deadline = System.currentTimeMillis() + 3000L
            while (System.currentTimeMillis() < deadline) {
                if (receivedCursors.contains(expectedCursor)) break
                Thread.sleep(10)
            }
            assertTrue(receivedCursors.contains(expectedCursor), "Cursors $receivedCursors should contain $expectedCursor")
            assertTrue(receivedCursors.size >= 2)
            // 验证游标单调递增
            for (i in 1 until receivedCursors.size) {
                assertTrue(receivedCursors[i] >= receivedCursors[i - 1], "Cursors must be monotonic: $receivedCursors")
            }
        } finally {
            sub.close()
            host.close()
        }
    }

    @Test
    fun `disconnect and control reclaim lifecycle`() {
        val clock = ManualClock(1000L)
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0,
            clock = clock,
            disconnectPolicy = DisconnectPolicy(),
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            )
        )

        try {
            host.start(0).get(2, TimeUnit.SECONDS)

            // 模拟掉线
            host.disconnect(0)
            Thread.sleep(100)
            val snapDisconnected = host.snapshot(0).get(2, TimeUnit.SECONDS)
            assertFalse(snapDisconnected.controls.first { it.seat == 0 }.connected)

            // 模拟重连并收回控制权
            host.reconnect(0)
            Thread.sleep(50)
            val reclaimReceipt = host.claimControl(0).get(2, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, reclaimReceipt.status)

            val snapReclaimed = host.snapshot(0).get(2, TimeUnit.SECONDS)
            assertTrue(snapReclaimed.controls.first { it.seat == 0 }.connected)
            assertEquals("HUMAN", snapReclaimed.controls.first { it.seat == 0 }.role)
        } finally {
            host.close()
        }
    }

    @Test
    fun `connected observer is pushed takeover on disconnect and human control on explicit reclaim`() {
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0,
            clock = ManualClock(1000L),
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.RemoteHuman(false),
                SeatController.Computer(),
                SeatController.Computer()
            )
        )
        try {
            assertEquals(ReceiptStatus.APPLIED, host.start(0).get(3, TimeUnit.SECONDS).status)
            val observerSeat = 1
            val baseline = CountDownLatch(1)
            val takeoverPushed = CountDownLatch(1)
            val reclaimedPushed = CountDownLatch(1)
            val armed = AtomicBoolean(false)
            val updates = CopyOnWriteArrayList<com.majiang.access.PlayerUpdate>()
            host.subscribeSeat(observerSeat) { update ->
                updates.add(update)
                if (!armed.get()) {
                    baseline.countDown()
                } else {
                    val control = update.snapshot.controls.first { it.seat == 0 }
                    if (control.role == "TAKEOVER" && !control.connected) takeoverPushed.countDown()
                    if (control.role == "HUMAN" && control.connected) reclaimedPushed.countDown()
                }
            }.use {
                assertTrue(baseline.await(3, TimeUnit.SECONDS))
                assertTrue(updates.last().snapshot.controls.first { it.seat == observerSeat }.connected)
                val initialCursor = updates.last().snapshot.cursor
                armed.set(true)

                host.disconnect(0)
                assertTrue(takeoverPushed.await(3, TimeUnit.SECONDS),
                    "Existing seat observer missed authoritative disconnect takeover")
                val takeover = updates.first { update ->
                    update.snapshot.controls.first { it.seat == 0 }.let { it.role == "TAKEOVER" && !it.connected }
                }
                assertTrue(takeover.snapshot.cursor > initialCursor)
                val takeoverEpoch = takeover.snapshot.controls.first { it.seat == 0 }.controlEpoch
                assertEquals("TAKEOVER", host.snapshot(observerSeat).get(3, TimeUnit.SECONDS)
                    .controls.first { it.seat == 0 }.role)

                host.reconnect(0)
                assertEquals(ReceiptStatus.APPLIED, host.claimControl(0).get(3, TimeUnit.SECONDS).status)
                assertTrue(reclaimedPushed.await(3, TimeUnit.SECONDS),
                    "Existing seat observer missed authoritative control reclaim")
                val reclaimed = updates.first { update ->
                    update.snapshot.controls.first { it.seat == 0 }.let { it.role == "HUMAN" && it.connected }
                        && update.snapshot.cursor > takeover.snapshot.cursor
                }
                assertTrue(reclaimed.snapshot.cursor > takeover.snapshot.cursor)
                assertTrue(reclaimed.snapshot.controls.first { it.seat == 0 }.controlEpoch > takeoverEpoch)
                assertEquals(host.snapshot(observerSeat).get(3, TimeUnit.SECONDS).controls,
                    reclaimed.snapshot.controls)
            }
        } finally {
            host.close()
        }
    }

    @Test
    fun `injectable serial executor allows paused creation and close cancels pending futures`() {
        val pausedExecutor = Executors.newSingleThreadExecutor()
        val pauseLatch = CountDownLatch(1)
        // 先让 executor 阻塞在 latch 上
        pausedExecutor.submit {
            pauseLatch.await(5, TimeUnit.SECONDS)
        }

        // 构造 ServerTableHost 必须立即返回，不得在构造器内阻塞
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0,
            serialExecutor = pausedExecutor
        )

        // 此时入队的命令处于排队未执行状态
        val pendingFuture = host.start(0)
        assertFalse(pendingFuture.isDone, "Pending future must not be done while executor is paused")

        // 关闭宿主：未决的 future 必须立即完成异常，不可挂起
        host.close()
        assertTrue(pendingFuture.isCompletedExceptionally, "Pending future must complete exceptionally on close")

        pauseLatch.countDown()
        pausedExecutor.shutdownNow()
    }

    @Test
    fun `asynchronous tick does not block caller`() {
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0
        )
        try {
            val tickFuture = host.tick()
            assertNotNull(tickFuture)
            val result = tickFuture.get(2, TimeUnit.SECONDS)
            assertFalse(result) // 未开局无超时，正常返回 false
        } finally {
            host.close()
        }
    }

    @Test
    fun `default mode channels maintain unbounded capacity and zero drops during operations`() {
        val host = ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            adminSeat = 0
        )
        try {
            val seatChannel = host.getSeatChannel(0)
            val publicChannel = host.getPublicDispatchChannel()
            assertNotNull(seatChannel)
            assertNotNull(publicChannel)
            val nonNullSeatChannel = seatChannel!!
            assertEquals(Int.MAX_VALUE, nonNullSeatChannel.capacityItems)
            assertEquals(Long.MAX_VALUE, nonNullSeatChannel.capacityBytes)
            assertEquals(Int.MAX_VALUE, publicChannel.capacityItems)
            assertEquals(Long.MAX_VALUE, publicChannel.capacityBytes)

            val initialDrops = nonNullSeatChannel.overflowCount
            val initialRejects = nonNullSeatChannel.scheduleRejectCount

            // 触发一次启动
            val receipt = host.start(0).get(3, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, receipt.status)

            // 默认模式下通道无界且无丢弃无拒绝
            assertEquals(initialDrops, nonNullSeatChannel.overflowCount)
            assertEquals(initialRejects, nonNullSeatChannel.scheduleRejectCount)
            assertFalse(nonNullSeatChannel.needsRecovery)
        } finally {
            host.close()
        }
    }
}


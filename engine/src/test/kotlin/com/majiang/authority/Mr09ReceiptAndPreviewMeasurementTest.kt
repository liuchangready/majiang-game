package com.majiang.authority

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.match.SessionLifecycle
import com.majiang.session.PlayerPresentationPreviewProvider
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GameAction
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.atomic.AtomicInteger

/**
 * MR-09b storedReceipts 与预览重复计算测量用例
 *
 * 遵循 ANTI-MR09B-TASK 规范：
 * 1. 仅采集测量数据，不包含任何 LRU、配额、缓存或淘汰生产逻辑；
 * 2. 数值指标通过标准前缀 MR09-MEAS println 输出供验收方采集，不作硬断言；
 * 3. 仅对基本合理性进行宽松断言（entries > 0, 状态守恒, spam 线性增长与 close 归零）。
 */
@Timeout(180)
class Mr09ReceiptAndPreviewMeasurementTest {

    private class ManualClock(var current: Long = 1000L) : MonotonicClock {
        override fun nowMillis(): Long = current
    }

    private val controls = listOf(
        SeatControlView(0, true, "HUMAN", 0L),
        SeatControlView(1, true, "COMPUTER", 0L),
        SeatControlView(2, true, "COMPUTER", 0L),
        SeatControlView(3, true, "COMPUTER", 0L)
    )

    private data class DriveResult(
        val roundEndedCount: Int,
        val iterations: Int
    )

    private fun drive(
        adminSeat: Int = 0,
        authority: TableAuthority,
        clock: ManualClock,
        requestIdSeq: AtomicInteger,
        pollsPerDecision: Int
    ): DriveResult {
        var roundEndedCount = 0
        authority.coordinator.eventBus.subscribe<GameEvent.RoundEnded> {
            roundEndedCount++
        }

        authority.start(adminSeat, clock.nowMillis())

        var iterations = 0
        while (authority.sessionLifecycle != SessionLifecycle.FINISHED && iterations < 20000) {
            iterations++
            var actionTaken = false
            for (seat in 0..3) {
                val decision = authority.queryDecision(seat, isAutomated = false)
                if (decision.options.isNotEmpty()) {
                    val drawnTile = authority.coordinator.activeRound?.readState()?.players?.getOrNull(seat)?.drawnTile
                    val chosenOption = decision.options.firstOrNull {
                        it.action is GameAction.DiscardTile && (it.action as GameAction.DiscardTile).tile == drawnTile
                    } ?: decision.options.firstOrNull {
                        it.action is GameAction.Pass
                    } ?: decision.options.firstOrNull {
                        it.action is GameAction.DiscardTile
                    } ?: decision.options.first()

                    val reqId = "mr09-meas-${requestIdSeq.incrementAndGet()}"
                    val choice = ActionChoice(
                        requestId = reqId,
                        roundId = decision.roundId,
                        decisionId = decision.decisionId,
                        controlEpoch = decision.controlEpoch,
                        optionId = chosenOption.optionId
                    )
                    authority.submitChoice(seat, choice, clock.nowMillis())
                    actionTaken = true

                    for (s in 0..3) {
                        repeat(pollsPerDecision) {
                            authority.querySnapshot(s, "epoch-0", 0L, controls)
                        }
                    }
                }
            }

            if (!actionTaken) {
                if (authority.validateNextRoundPreconditions() == null) {
                    val nextRes = authority.nextRound(adminSeat, clock.nowMillis())
                    if (nextRes.receipt.status == ReceiptStatus.APPLIED) {
                        actionTaken = true
                        for (s in 0..3) {
                            repeat(pollsPerDecision) {
                                authority.querySnapshot(s, "epoch-0", 0L, controls)
                            }
                        }
                    }
                }
            }

            if (!actionTaken) {
                break
            }
        }

        return DriveResult(roundEndedCount, iterations)
    }

    @Test
    fun `per match receipt store and single poll preview baseline`() {
        PlayerPresentationPreviewProvider.resetPreviewCallMetrics()
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = false)),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.LocalHuman(),
                SeatController.LocalHuman(),
                SeatController.LocalHuman()
            ),
            adminSeat = 0
        )
        val requestIdSeq = AtomicInteger(0)
        try {
            val driveResult = drive(0, authority, clock, requestIdSeq, pollsPerDecision = 1)
            val receiptMetrics = authority.receiptStoreMetrics()
            val previewMetrics = PlayerPresentationPreviewProvider.previewCallMetrics()

            assertTrue(receiptMetrics.currentEntries > 0, "currentEntries must be > 0")
            assertEquals(
                receiptMetrics.currentEntries,
                receiptMetrics.appliedEntries + receiptMetrics.recordedEntries + receiptMetrics.rejectedEntries,
                "applied + recorded + rejected must equal currentEntries"
            )
            assertTrue(previewMetrics.callCount > 0, "preview callCount must be > 0")

            val rejectedShare = if (receiptMetrics.currentEntries > 0) {
                String.format(java.util.Locale.US, "%.2f%%", receiptMetrics.rejectedEntries.toDouble() / receiptMetrics.currentEntries * 100.0)
            } else "0.00%"
            val bytesPerEntry = if (receiptMetrics.currentEntries > 0) {
                receiptMetrics.approxRetainedBytes / receiptMetrics.currentEntries
            } else 0L

            println("MR09-MEAS match1 entries=${receiptMetrics.currentEntries} rejected=${receiptMetrics.rejectedEntries} rejectedShare=$rejectedShare recorded=${receiptMetrics.recordedEntries} applied=${receiptMetrics.appliedEntries} perSeat=${receiptMetrics.perSeatEntryCounts} approxBytes=${receiptMetrics.approxRetainedBytes} bytesPerEntry=$bytesPerEntry roundEnded=${driveResult.roundEndedCount} lifecycle=${authority.sessionLifecycle} iterations=${driveResult.iterations}")
            println("MR09-MEAS match1 preview calls=${previewMetrics.callCount} distinct=${previewMetrics.distinctDecisions} duplicates=${previewMetrics.duplicateCalls} nullRound=${previewMetrics.nullRoundCount} frozen=${previewMetrics.identityFrozen}")
        } finally {
            authority.close()
        }
    }

    @Test
    fun `reconnect style polling measures preview recompute duplication`() {
        PlayerPresentationPreviewProvider.resetPreviewCallMetrics()
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = false)),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.LocalHuman(),
                SeatController.LocalHuman(),
                SeatController.LocalHuman()
            ),
            adminSeat = 0
        )
        val requestIdSeq = AtomicInteger(0)
        try {
            val driveResult = drive(0, authority, clock, requestIdSeq, pollsPerDecision = 3)
            val receiptMetrics = authority.receiptStoreMetrics()
            val previewMetrics = PlayerPresentationPreviewProvider.previewCallMetrics()

            assertTrue(receiptMetrics.currentEntries > 0, "currentEntries must be > 0")
            assertEquals(
                receiptMetrics.currentEntries,
                receiptMetrics.appliedEntries + receiptMetrics.recordedEntries + receiptMetrics.rejectedEntries,
                "applied + recorded + rejected must equal currentEntries"
            )
            assertTrue(previewMetrics.callCount > 0, "preview callCount must be > 0")
            assertTrue(previewMetrics.duplicateCalls > 0, "duplicateCalls must be > 0 when pollsPerDecision > 1")

            val rejectedShare = if (receiptMetrics.currentEntries > 0) {
                String.format(java.util.Locale.US, "%.2f%%", receiptMetrics.rejectedEntries.toDouble() / receiptMetrics.currentEntries * 100.0)
            } else "0.00%"
            val bytesPerEntry = if (receiptMetrics.currentEntries > 0) {
                receiptMetrics.approxRetainedBytes / receiptMetrics.currentEntries
            } else 0L

            println("MR09-MEAS match2 entries=${receiptMetrics.currentEntries} rejected=${receiptMetrics.rejectedEntries} rejectedShare=$rejectedShare recorded=${receiptMetrics.recordedEntries} applied=${receiptMetrics.appliedEntries} perSeat=${receiptMetrics.perSeatEntryCounts} approxBytes=${receiptMetrics.approxRetainedBytes} bytesPerEntry=$bytesPerEntry roundEnded=${driveResult.roundEndedCount} lifecycle=${authority.sessionLifecycle} iterations=${driveResult.iterations}")
            println("MR09-MEAS match2 preview calls=${previewMetrics.callCount} distinct=${previewMetrics.distinctDecisions} duplicates=${previewMetrics.duplicateCalls} nullRound=${previewMetrics.nullRoundCount} frozen=${previewMetrics.identityFrozen}")
        } finally {
            authority.close()
        }
    }

    @Test
    fun `unique requestId spam documents linear unbounded growth`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            ),
            adminSeat = 0
        )
        val m = 500
        authority.use { auth ->
            val startRes = auth.start(0, clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, startRes.receipt.status)

            val epoch = auth.controlEpochFor(0)
            val currentRound = auth.currentRoundId

            for (i in 1..m) {
                val spamChoice = ActionChoice(
                    requestId = "spam-req-$i",
                    roundId = currentRound,
                    decisionId = "bogus-decision",
                    controlEpoch = epoch,
                    optionId = "bogus-option"
                )
                val res = auth.submitChoice(0, spamChoice, clock.nowMillis())
                assertEquals(ReceiptStatus.REJECTED, res.receipt.status)
            }

            val metrics = auth.receiptStoreMetrics()
            assertTrue(metrics.currentEntries >= m, "currentEntries (${metrics.currentEntries}) must be >= $m")

            val bytesPerEntry = if (metrics.currentEntries > 0) {
                metrics.approxRetainedBytes / metrics.currentEntries
            } else 0L

            println("MR09-MEAS spam entries=${metrics.currentEntries} approxBytes=${metrics.approxRetainedBytes} bytesPerEntry=$bytesPerEntry")
        }

        val postCloseMetrics = authority.receiptStoreMetrics()
        assertEquals(0, postCloseMetrics.currentEntries, "currentEntries must be 0 after close()")
    }
}

package com.majiang.authority

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.application.ActionChoice
import com.majiang.authority.TableAuthority.AutoStepResult
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.state.GameAction
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.timing.TimeControlConfig
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

@Timeout(10)
class TableAuthorityTest {

    private class ManualClock(var current: Long = 1000L) : MonotonicClock {
        override fun nowMillis(): Long = current
    }

    private val controls = listOf(
        SeatControlView(0, true, "HUMAN", 0L),
        SeatControlView(1, true, "COMPUTER", 0L),
        SeatControlView(2, true, "COMPUTER", 0L),
        SeatControlView(3, true, "COMPUTER", 0L)
    )

    @Test
    fun `test synchronous start and discard submission with idempotency`() {
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

        // 非房主启动拒绝
        val nonAdminStart = authority.start(1, clock.nowMillis())
        assertEquals(ReceiptStatus.REJECTED, nonAdminStart.receipt.status)
        assertEquals("PERMISSION_DENIED", nonAdminStart.receipt.reason)

        // 房主启动成功
        val adminStart = authority.start(0, clock.nowMillis())
        assertEquals(ReceiptStatus.APPLIED, adminStart.receipt.status)
        assertTrue(adminStart.stateChanged)

        val snap = authority.querySnapshot(0, "epoch-0", 0L, controls)
        assertNotNull(snap.roundId)
        val decision = authority.queryDecision(0, isAutomated = false)
        assertTrue(decision.options.isNotEmpty(), "Dealer must have options after start")

        val discardOption = decision.options.first { it.action is GameAction.DiscardTile }
        val choice = ActionChoice(
            requestId = "req-discard-1",
            roundId = decision.roundId,
            decisionId = decision.decisionId,
            controlEpoch = decision.controlEpoch,
            optionId = discardOption.optionId
        )

        val result1 = authority.submitChoice(0, choice, clock.nowMillis())
        assertTrue(result1.receipt.status in setOf(ReceiptStatus.APPLIED, ReceiptStatus.RECORDED), "Expected successful discard status")
        assertTrue(result1.stateChanged)
        assertEquals(1, authority.coordinator.activeRound!!.state.players[0].discards.size)

        // 幂等重复提交返回相同回执
        val result2 = authority.submitChoice(0, choice, clock.nowMillis())
        assertEquals(result1.receipt, result2.receipt)
        assertFalse(result2.stateChanged)

        // 冲突载荷拒绝
        val conflicting = choice.copy(optionId = "different-option")
        val result3 = authority.submitChoice(0, conflicting, clock.nowMillis())
        assertEquals(ReceiptStatus.REJECTED, result3.receipt.status)
        assertEquals("REQUEST_ID_CONFLICT", result3.receipt.reason)

        authority.close()
    }

    @Test
    fun `test timeout policy execution when deadline expired`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            timeControl = TimeControlConfig(baseMillis = 3000L, reserveMillis = 0L),
            clock = clock,
            adminSeat = 0
        )
        authority.start(0, clock.nowMillis())
        authority.syncDecisionsAndClocks(clock.nowMillis(), emptySet())

        val timing = authority.decisionClock.timing(0)
        assertNotNull(timing)

        // 未到期时不超时
        clock.current = timing!!.deadlineMillis - 100
        val premature = authority.checkTimeout(clock.nowMillis())
        assertEquals(null, premature)

        // 超过截止时间，自动裁决超时动作
        clock.current = timing.deadlineMillis + 100
        val timeoutResult = authority.checkTimeout(clock.nowMillis())
        assertNotNull(timeoutResult)
        assertTrue(timeoutResult is AutoStepResult.TimeoutResolved)
        assertEquals(0, (timeoutResult as AutoStepResult.TimeoutResolved).seat)

        // 超时后 late submission 必须拒绝
        val decision = authority.queryDecision(0, false)
        val lateChoice = ActionChoice(
            requestId = "late-req-1",
            roundId = timing.key.roundId,
            decisionId = timing.key.decisionId,
            controlEpoch = decision.controlEpoch,
            optionId = "any"
        )
        val lateResult = authority.submitChoice(0, lateChoice, clock.nowMillis())
        assertEquals(ReceiptStatus.REJECTED, lateResult.receipt.status)

        authority.close()
    }

    @Test
    fun `test shuffle preparation phase and physical wall deal`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(
                shuffleConfig = ShuffleConfig(enabled = true, selectionMillis = 10000L)
            ),
            clock = clock,
            adminSeat = 0
        )

        authority.start(0, clock.nowMillis())
        assertTrue(authority.isPreparationActive())

        val prepView = authority.buildPreparationViewForSeat(0)
        assertNotNull(prepView)
        assertEquals(10000L, prepView!!.deadlineMillis - prepView.opensAtMillis)

        // 提交洗牌预设方案
        val epoch = authority.coordinator.epochFor(0)
        val planRequest = ShufflePlanRequest(
            roundId = prepView.roundId,
            controlEpoch = epoch,
            selection = ShuffleSelection(initialTargets = setOf(ShuffleTarget.CHINITSU_MAN)),
            requestId = "prep-req-1"
        )
        val planResult = authority.submitShufflePlan(0, planRequest, clock.nowMillis())
        assertEquals(ReceiptStatus.RECORDED, planResult.receipt.status)

        // 再次提交重复返回原回执
        val planResult2 = authority.submitShufflePlan(0, planRequest, clock.nowMillis())
        assertEquals(planResult.receipt, planResult2.receipt)

        // 时间推进至准备超时，触发原子发牌
        clock.current = prepView.deadlineMillis + 1L
        val autoDeal = authority.checkPreparationStep(clock.nowMillis(), setOf(1, 2, 3), setOf(1, 2, 3))
        assertNotNull(autoDeal)
        assertTrue(autoDeal is AutoStepResult.PreparationDealt)
        assertFalse(authority.isPreparationActive(), "Preparation must conclude after deal")
        TestTableBuilder.assertPhysical(authority.coordinator.activeRound!!.state)

        authority.close()
    }

    @Test
    fun `test pause and resume timing shifts`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            timeControl = TimeControlConfig(baseMillis = 5000L, reserveMillis = 10000L),
            clock = clock,
            adminSeat = 0
        )
        authority.start(0, clock.nowMillis())
        authority.syncDecisionsAndClocks(clock.nowMillis(), emptySet())

        val timingBefore = authority.decisionClock.timing(0)
        assertNotNull(timingBefore)
        val initialDeadline = timingBefore!!.deadlineMillis

        clock.current = 2000L
        assertTrue(authority.pause(clock.nowMillis()))
        assertTrue(authority.isPaused)

        // 暂停期间玩法操作拒绝
        val dec = authority.queryDecision(0, false)
        val choice = ActionChoice("pause-req", dec.roundId, dec.decisionId, dec.controlEpoch, dec.options.first().optionId)
        val pausedSubmit = authority.submitChoice(0, choice, clock.nowMillis())
        assertEquals(ReceiptStatus.REJECTED, pausedSubmit.receipt.status)
        assertEquals("HOST_PAUSED", pausedSubmit.receipt.reason)

        // 暂停 3 秒后恢复
        clock.current = 5000L
        assertTrue(authority.resume(clock.nowMillis()))
        assertFalse(authority.isPaused)

        val timingAfter = authority.decisionClock.timing(0)
        assertNotNull(timingAfter)
        assertEquals(initialDeadline + 3000L, timingAfter!!.deadlineMillis)

        authority.close()
    }

    @Test
    fun `test cheat ledger entry rekeys active decision`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            clock = clock,
            adminSeat = 0
        )
        authority.start(0, clock.nowMillis())
        authority.syncDecisionsAndClocks(clock.nowMillis(), emptySet())

        val decBefore = authority.queryDecision(0, false)
        val timingBefore = authority.decisionClock.timing(0)
        assertNotNull(timingBefore)

        val ledgerResult = authority.applyCheatLedgerEntry("entry-1", 0, 10, "bonus", clock.nowMillis())
        assertEquals(ReceiptStatus.APPLIED, ledgerResult.receipt.status)

        val decAfter = authority.queryDecision(0, false)
        val timingAfter = authority.decisionClock.timing(0)
        assertNotNull(timingAfter)
        assertEquals(decAfter.decisionId, timingAfter!!.key.decisionId)
        assertEquals(timingBefore!!.opensAtMillis, timingAfter.opensAtMillis)
        assertEquals(timingBefore.deadlineMillis, timingAfter.deadlineMillis)

        authority.close()
    }

    @Test
    fun `test update lobby config on default undealt authority and verify query state next start and stable session`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(clock = clock)
        val initialSessionId = authority.coordinator.session.sessionId

        val changedConfig = MatchConfig(
            winds = listOf(1),
            handsPerWind = 2,
            startingScores = listOf(26000, 24000, 25000, 25000),
            targetScore = 28000
        )
        val changedTimeControl = TimeControlConfig(baseMillis = 2000L, reserveMillis = 10000L)

        val success = authority.updateLobbyConfig(changedConfig, changedTimeControl)
        assertTrue(success)

        assertEquals(initialSessionId, authority.coordinator.session.sessionId)
        assertEquals(changedConfig.toRuleConfig(), authority.coordinator.session.ruleConfig)
        assertEquals(changedConfig.toRuleConfig(), authority.coordinator.matchProgression.config)
        assertEquals(changedConfig.startingScores, authority.coordinator.queryView(0).scores)
        assertEquals(10000L, authority.decisionClock.remainingReserve(0))

        val startResult = authority.start(0, clock.nowMillis())
        assertEquals(ReceiptStatus.APPLIED, startResult.receipt.status)
        authority.syncDecisionsAndClocks(clock.nowMillis(), emptySet())

        assertEquals(changedConfig.startingScores, authority.coordinator.queryView(0).scores)
        val timer = authority.querySnapshot(0, "epoch-0", 0L, controls).clocks.first { it.seat == 0 }
        assertEquals(12000L, timer.deadline - timer.opensAt)
        assertEquals(initialSessionId, authority.coordinator.session.sessionId)

        // 对局已开始后更新配置必须拒绝，且状态与截止时间不变
        val deadlineBefore = timer.deadline
        val rejectPostStart = authority.updateLobbyConfig(
            changedConfig.copy(targetScore = 35000),
            TimeControlConfig(baseMillis = 9000L, reserveMillis = 90000L)
        )
        assertFalse(rejectPostStart)
        val timerAfter = authority.querySnapshot(0, "epoch-0", 0L, controls).clocks.first { it.seat == 0 }
        assertEquals(deadlineBefore, timerAfter.deadline)
        assertEquals(changedConfig, authority.config)

        authority.close()
    }

    @Test
    fun `test invalid configuration rejected with exception without partial mutation`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(clock = clock)
        val originalConfig = authority.config
        val originalSessionId = authority.coordinator.session.sessionId
        val originalRuleConfig = authority.coordinator.session.ruleConfig
        val originalScores = authority.coordinator.session.betweenRoundScores
        val originalRoundScores = authority.coordinator.activeRound!!.readState().scores.toList()

        assertThrows<IllegalArgumentException> {
            authority.updateLobbyConfig(originalConfig.copy(startingScores = listOf(1)))
        }

        assertEquals(originalConfig, authority.config)
        assertEquals(originalSessionId, authority.coordinator.session.sessionId)
        assertEquals(originalRuleConfig, authority.coordinator.session.ruleConfig)
        assertEquals(originalScores, authority.coordinator.session.betweenRoundScores)
        assertEquals(originalRoundScores, authority.coordinator.activeRound!!.readState().scores.toList())

        // 验证直接调用 coordinator.updateLobbyConfig 传入非法配置时同样原子拒绝并保持状态
        assertThrows<IllegalArgumentException> {
            authority.coordinator.updateLobbyConfig(originalConfig.copy(startingScores = listOf(1, 2)))
        }
        assertEquals(originalRuleConfig, authority.coordinator.session.ruleConfig)
        assertEquals(originalScores, authority.coordinator.session.betweenRoundScores)
        assertEquals(originalRoundScores, authority.coordinator.activeRound!!.readState().scores.toList())

        authority.close()
    }

    @Test
    fun `test enabling shuffle changes real budget and locks configuration during selection`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(clock = clock)

        val changed = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true))
        assertTrue(authority.updateLobbyConfig(changed))

        val startResult = authority.start(0, clock.nowMillis())
        assertEquals(ReceiptStatus.APPLIED, startResult.receipt.status)
        assertTrue(authority.isPreparationActive())
        assertEquals(com.majiang.state.GamePhase.STACKING, authority.coordinator.activeRound!!.readState().phase)
        assertEquals(listOf(50, 50, 50, 50), authority.querySnapshot(0, "epoch-0", 0L, controls).cheatPoints)

        assertFalse(authority.updateLobbyConfig(MatchConfig()), "Shuffle selection is active, must reject configuration update")
        assertTrue(authority.isPreparationActive())
        assertTrue(authority.config.shuffleConfig.enabled)

        authority.close()
    }
}

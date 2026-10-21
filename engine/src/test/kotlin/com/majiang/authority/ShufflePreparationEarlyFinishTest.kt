package com.majiang.authority

import com.majiang.access.ReceiptStatus
import com.majiang.authority.cheat.ShufflePreparationHandler
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleWallGenerator
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * 准备阶段提前发牌回归（正式 APP 单机“跳过千术仍等满倒计时”）。
 *
 * 覆盖：
 * 1. 四个真人均确认后在 deadline 前立即发牌；
 * 2. 仍有真人未确认时绝不提前，原 deadline 仍是安全兜底；
 * 3. 提前结束仍按原始方案扣费、冻结看破集合、只发一次牌且 136 张守恒；
 * 4. 重复 tick / 迟到请求不得二次扣费或二次发牌。
 */
class ShufflePreparationEarlyFinishTest {

    private class TestClock(var now: Long = 0L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private fun authority(clock: TestClock, points: Int = 100): TableAuthority {
        val costs = mapOf(
            "TILE_MARK" to 10,
            "SLEEVE_BLOOM" to 10,
            "BEAM_SWAP" to 10,
            "SEE_THROUGH" to 10
        )
        return TableAuthority(
            config = MatchConfig(
                winds = listOf(1),
                cheatEconomy = CheatEconomyConfig(
                    enabled = true,
                    initialPoints = List(4) { points },
                    costs = costs
                ),
                shuffleConfig = ShuffleConfig(
                    enabled = true,
                    selectionMillis = 10_000L,
                    seeThroughEnabled = true
                )
            ),
            clock = clock,
            controllers = List(4) { SeatController.LocalHuman() },
            adminSeat = 0
        )
    }

    private fun TableAuthority.plan(
        seat: Int,
        requestId: String,
        selection: ShuffleSelection,
        roundId: String
    ) = ShufflePlanRequest(
        requestId = requestId,
        roundId = roundId,
        controlEpoch = controlEpochFor(seat),
        selection = selection
    )

    @Test
    fun `all four humans confirming before deadline deals immediately with frozen original costs`() {
        val clock = TestClock(1_000L)
        authority(clock).use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status)
            val prep = a.buildPreparationViewForSeat(0)
            assertNotNull(prep)
            val roundId = prep!!.roundId
            assertTrue(a.isPreparationActive())
            assertEquals(11_000L, prep.deadlineMillis)

            val selections = listOf(
                ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN, seeThrough = true),
                ShuffleSelection(target = ShuffleTarget.KOKUSHI),
                ShuffleSelection(seeThrough = true),
                ShuffleSelection()
            )
            selections.forEachIndexed { seat, selection ->
                assertEquals(
                    ReceiptStatus.RECORDED,
                    a.submitShufflePlan(seat, a.plan(seat, "early-$seat", selection, roundId), clock.now).receipt.status
                )
            }

            // 仍在 deadline 之前，不允许依赖 10 秒倒计时。
            clock.now = 1_100L
            val step = a.checkPreparationStep(clock.now, emptySet(), emptySet())
            assertTrue(step is TableAuthority.AutoStepResult.PreparationDealt, "all confirmed seats must deal early")
            assertFalse(a.isPreparationActive())
            assertNull(a.buildPreparationViewForSeat(0))

            val state = a.coordinator.activeRound!!.readState()
            assertEquals(listOf(60, 70, 90, 100), state.cheatPoints.toList())
            assertEquals(1_100L, clock.now, "early deal must not advance the test clock to the deadline")
            TestTableBuilder.assertPhysical(state)

            // 重复 tick 与迟到请求不得二次发牌/二次扣费。
            repeat(5) {
                assertNull(a.checkPreparationStep(clock.now, emptySet(), emptySet()))
            }
            assertEquals(listOf(60, 70, 90, 100), a.coordinator.activeRound!!.readState().cheatPoints.toList())
            TestTableBuilder.assertPhysical(a.coordinator.activeRound!!.readState())

            val late = a.plan(1, "late-after-deal", ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN), roundId)
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(1, late, clock.now).receipt.status)
            assertEquals("WRONG_PHASE", a.submitShufflePlan(1, late, clock.now).receipt.reason)
        }
    }

    @Test
    fun `one unconfirmed human blocks early deal and the original deadline remains the fallback`() {
        val clock = TestClock(2_000L)
        authority(clock).use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status)
            val prep = a.buildPreparationViewForSeat(0)
            assertNotNull(prep)
            val roundId = prep!!.roundId

            for (seat in 0..2) {
                assertEquals(
                    ReceiptStatus.RECORDED,
                    a.submitShufflePlan(
                        seat,
                        a.plan(seat, "partial-$seat", ShuffleSelection(), roundId),
                        clock.now
                    ).receipt.status
                )
            }

            clock.now = prep.deadlineMillis - 1L
            assertNull(
                a.checkPreparationStep(clock.now, emptySet(), emptySet()),
                "three confirmed humans must not confirm the fourth human"
            )
            assertTrue(a.isPreparationActive())
            assertFalse(a.buildPreparationViewForSeat(3)!!.confirmed)

            clock.now = prep.deadlineMillis
            val fallback = a.checkPreparationStep(clock.now, emptySet(), emptySet())
            assertTrue(fallback is TableAuthority.AutoStepResult.PreparationDealt)
            assertFalse(a.isPreparationActive())
            assertEquals(listOf(100, 100, 100, 100), a.coordinator.activeRound!!.readState().cheatPoints.toList())
            TestTableBuilder.assertPhysical(a.coordinator.activeRound!!.readState())
        }
    }

    @Test
    fun `early handler step freezes original plans and effective see-through suppression before deadline`() {
        val config = ShuffleConfig(enabled = true, selectionMillis = 10_000L, seeThroughEnabled = true)
        val handler = ShufflePreparationHandler()
        val startedAt = 5_000L
        handler.start("early-plan-round", startedAt, config)
        val selections = listOf(
            ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN, seeThrough = true),
            ShuffleSelection(target = ShuffleTarget.KOKUSHI),
            ShuffleSelection(target = ShuffleTarget.DAISANGEN),
            ShuffleSelection()
        )
        selections.forEachIndexed { seat, selection ->
            handler.recordPlan(seat, selection, selection.cost())
        }

        val step = handler.step(
            now = startedAt + 100L,
            automatedSeats = emptySet(),
            nativeAiSeats = emptySet(),
            points = List(4) { 100 },
            aiMaySpend = false,
            dealerSeat = 1,
            config = config
        )
        assertTrue(step is ShufflePreparationHandler.Step.Ready)
        val ready = step as ShufflePreparationHandler.Step.Ready
        assertEquals(setOf(0), ready.record.seeThroughSeats)
        assertEquals(selections, ready.plans.map { it.selection }, "original plans must be frozen unchanged")
        assertEquals(40, ready.plans[0].selection.cost())
        assertEquals(30, ready.plans[1].selection.cost())
        assertEquals(30, ready.plans[2].selection.cost())
        assertEquals(ShuffleTarget.CHINITSU_MAN, ready.effectivePlans[0].selection.target)
        assertNull(ready.effectivePlans[1].selection.target, "see-through must suppress other targets")
        assertNull(ready.effectivePlans[2].selection.target)
        assertTrue(ready.effectivePlans.none { it.selection.seeThrough })
        val expected = ShuffleWallGenerator().generate(
            seed = ready.record.seed,
            dealerSeat = 1,
            plans = ready.effectivePlans,
            config = config
        )
        assertEquals(expected, ready.tiles)
        assertEquals(136, ready.tiles!!.size)
    }

    @Test
    fun `last human confirming late still ends early before the original deadline`() {
        val clock = TestClock(3_000L)
        authority(clock).use { a ->
            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status)
            val prep = a.buildPreparationViewForSeat(0)
            assertNotNull(prep)
            val roundId = prep!!.roundId
            for (seat in 0..2) {
                a.submitShufflePlan(seat, a.plan(seat, "late-$seat", ShuffleSelection(), roundId), clock.now)
            }

            clock.now = 5_000L
            assertNull(a.checkPreparationStep(clock.now, emptySet(), emptySet()))
            a.submitShufflePlan(
                3,
                a.plan(3, "closing-human", ShuffleSelection(target = ShuffleTarget.DAISANGEN), roundId),
                clock.now
            )

            clock.now = 5_050L
            val early = a.checkPreparationStep(clock.now, emptySet(), emptySet())
            assertTrue(early is TableAuthority.AutoStepResult.PreparationDealt)
            assertTrue(clock.now < prep.deadlineMillis)
            assertEquals(listOf(100, 100, 100, 70), a.coordinator.activeRound!!.readState().cheatPoints.toList())
        }
    }
}

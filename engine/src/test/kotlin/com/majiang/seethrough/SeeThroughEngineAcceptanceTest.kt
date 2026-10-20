package com.majiang.seethrough

import com.majiang.access.ReceiptStatus
import com.majiang.application.ApplicationCoordinator
import com.majiang.authority.TableAuthority
import com.majiang.authority.cheat.ShufflePreparationHandler
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkGroup
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.cheat.mark.TileMarkState
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.ApprovedSessionStep
import com.majiang.match.MatchConfig
import com.majiang.model.TilePool
import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleWallGenerator
import com.majiang.state.ActionResult
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows

/**
 * 看破（SEE_THROUGH）engine 权威规则验收。
 *
 * 只通过真实准备窗口、Session 变更与发牌接缝验证；不在测试中复制规则实现。
 */
@Timeout(60)
class SeeThroughEngineAcceptanceTest {

    private class ManualClock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private fun economy(points: Int = 100) = CheatEconomyConfig(
        enabled = true,
        initialPoints = List(4) { points },
        costs = mapOf("SEE_THROUGH" to 10, "TILE_MARK" to 10)
    )

    private fun enabledConfig(points: Int = 100, seeThrough: Boolean = true) = MatchConfig(
        cheatEconomy = economy(points),
        shuffleConfig = ShuffleConfig(
            enabled = true,
            selectionMillis = 1_000L,
            seeThroughEnabled = seeThrough
        ),
        tileMarkConfig = TileMarkConfig(true)
    )

    private fun humans() = List(4) { SeatController.RemoteHuman(false) }

    private fun authority(clock: ManualClock, points: Int = 100, seeThrough: Boolean = true) =
        TableAuthority(
            config = enabledConfig(points, seeThrough),
            clock = clock,
            controllers = humans(),
            adminSeat = 0
        )

    private fun planRequest(
        authority: TableAuthority,
        seat: Int,
        requestId: String,
        selection: ShuffleSelection,
        now: Long
    ): ShufflePlanRequest = ShufflePlanRequest(
        requestId = requestId,
        roundId = authority.buildPreparationViewForSeat(0)!!.roundId,
        controlEpoch = authority.controlEpochFor(seat),
        selection = selection
    )

    /**
     * 仅用于测试的历史落汗环境：以 Session 变更载体安装伪造的既有组，
     * 组的 createdRoundId 指向“上一局”，不伪造当前局的发牌历史。
     */
    private fun installMarks(authority: TableAuthority, marks: TileMarkState) {
        val session = authority.coordinator.session
        val roundId = authority.coordinator.activeRound!!.roundId
        val carrier = session.prepare(ApprovedSessionStep.RegisterDealtRound(roundId))
        session.install(carrier.copy(targetTileMarkState = marks))
    }

    private fun marksFor(vararg owners: Int): TileMarkState {
        var state = TileMarkState().registerDealtRound("previous-round")
        for (owner in owners) {
            state = state.addGroup(
                TileMarkGroup(
                    groupId = "group-$owner",
                    ownerSeat = owner,
                    tileIds = (owner * 4 until owner * 4 + 4).toList(),
                    createdRoundId = "previous-round",
                    createdRoundOrdinal = state.dealtRoundOrdinal
                )
            )
        }
        // 上一局必须已结算，当前局才能登记；组仍有未来 3 局有效期，不会被自然到期清除。
        return state.completeRound("previous-round")
    }

    // ------------------------------------------------------------------
    // 费用与配置
    // ------------------------------------------------------------------

    @Test
    fun `cost model charges ten for see-through alone and caps a combined plan at forty`() {
        val solo = ShuffleSelection(seeThrough = true)
        assertEquals(10, solo.cost())
        assertTrue(solo.isActive)

        val targetOnly = ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN)
        assertEquals(30, targetOnly.cost())

        val full = ShuffleSelection(target = ShuffleTarget.KOKUSHI, seeThrough = true)
        assertEquals(40, full.cost())
        assertEquals(ShuffleSelection.MAX_COST, full.cost())
        assertEquals(40, ShuffleSelection.MAX_COST)

        assertTrue(ShuffleSelection().cost() == 0)
        assertFalse(ShuffleSelection().isActive)
        assertFalse(ShuffleSelection().seeThrough)
        assertEquals(full, full.deepCopy())
    }

    @Test
    fun `see-through activation requires shuffle economy and an explicit ten point cost`() {
        // 默认关闭且旧配置不受影响
        assertFalse(ShuffleConfig().seeThroughEnabled)
        MatchConfig().validateSupported()

        for (config in listOf(
            MatchConfig(shuffleConfig = ShuffleConfig(enabled = false, seeThroughEnabled = true), cheatEconomy = economy()),
            MatchConfig(shuffleConfig = ShuffleConfig(enabled = true, seeThroughEnabled = true), cheatEconomy = CheatEconomyConfig()),
            MatchConfig(
                shuffleConfig = ShuffleConfig(enabled = true, seeThroughEnabled = true),
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("SEE_THROUGH" to 9))
            ),
            MatchConfig(
                shuffleConfig = ShuffleConfig(enabled = true, seeThroughEnabled = true),
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("SEE_THROUGH" to 11))
            ),
            MatchConfig(
                shuffleConfig = ShuffleConfig(enabled = true, seeThroughEnabled = true),
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, emptyMap())
            )
        )) {
            assertThrows<IllegalArgumentException> { config.validateSupported() }
        }

        enabledConfig().validateSupported()
    }

    // ------------------------------------------------------------------
    // 截止冻结与原始/有效方案分离
    // ------------------------------------------------------------------

    @Test
    fun `deadline freezes see-through seats and suppresses every opponent unified target`() {
        val config = ShuffleConfig(enabled = true, selectionMillis = 1_000L, seeThroughEnabled = true)
        val handler = ShufflePreparationHandler()
        val startedAt = 5_000L
        handler.start("round-1", startedAt, config)

        // 0 号看破并保留自己的统一目标；1 号统一目标；2 号统一目标；3 号跳过。
        handler.recordPlan(
            0,
            ShuffleSelection(target = ShuffleTarget.CHINITSU_PIN, seeThrough = true),
            40
        )
        handler.recordPlan(
            1,
            ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN),
            30
        )
        handler.recordPlan(
            2,
            ShuffleSelection(target = ShuffleTarget.KOKUSHI),
            30
        )

        val step = handler.step(
            now = startedAt + config.selectionMillis,
            automatedSeats = emptySet(),
            nativeAiSeats = emptySet(),
            points = List(4) { 100 },
            aiMaySpend = false,
            dealerSeat = 2,
            config = config
        ) as ShufflePreparationHandler.Step.Ready

        assertEquals(setOf(0), step.record.seeThroughSeats, "K must be frozen at the deadline")
        assertTrue(step.record.selections[0]!!.seeThrough)

        // 原始方案与费用完全保留
        assertEquals(ShuffleTarget.CHINITSU_PIN, step.plans.first { it.seat == 0 }.selection.target)
        assertEquals(ShuffleTarget.CHINITSU_MAN, step.plans.first { it.seat == 1 }.selection.target)
        assertEquals(30, step.plans.first { it.seat == 1 }.selection.cost())
        assertEquals(40, step.plans.first { it.seat == 0 }.selection.cost())

        // 有效方案：被看破者的整个统一目标移除；看破不进入洗牌算法。
        assertEquals(ShuffleTarget.CHINITSU_PIN, step.effectivePlans.first { it.seat == 0 }.selection.target)
        assertNull(step.effectivePlans.first { it.seat == 1 }.selection.target)
        assertNull(step.effectivePlans.first { it.seat == 2 }.selection.target)
        assertTrue(step.effectivePlans.none { it.selection.seeThrough })

        val expected = ShuffleWallGenerator().generate(
            seed = step.record.seed,
            dealerSeat = 2,
            plans = step.effectivePlans,
            config = config
        )
        assertEquals(expected, step.tiles)
        assertEquals(136, step.tiles!!.size)
        assertEquals(TilePool.buildStandardSet().toSet(), step.tiles!!.toSet())
    }

    @Test
    fun `multiple see-through seats suppress each other symmetrically regardless of submission order`() {
        val config = ShuffleConfig(enabled = true, selectionMillis = 1_000L, seeThroughEnabled = true)

        fun run(reverse: Boolean): ShufflePreparationHandler.Step.Ready {
            val handler = ShufflePreparationHandler()
            val startedAt = 9_000L
            handler.start("round-multi", startedAt, config)
            val plans = listOf(
                0 to ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN, seeThrough = true),
                1 to ShuffleSelection(target = ShuffleTarget.KOKUSHI, seeThrough = true),
                2 to ShuffleSelection(target = ShuffleTarget.CHINITSU_SOU)
            )
            for ((seat, selection) in if (reverse) plans.reversed() else plans) {
                handler.recordPlan(seat, selection, selection.cost())
            }
            return handler.step(
                now = startedAt + config.selectionMillis,
                automatedSeats = emptySet(),
                nativeAiSeats = emptySet(),
                points = List(4) { 100 },
                aiMaySpend = false,
                dealerSeat = 0,
                config = config
            ) as ShufflePreparationHandler.Step.Ready
        }

        val forward = run(reverse = false)
        val backward = run(reverse = true)
        for (step in listOf(forward, backward)) {
            assertEquals(setOf(0, 1), step.record.seeThroughSeats)
            assertNull(step.effectivePlans.first { it.seat == 0 }.selection.target)
            assertNull(step.effectivePlans.first { it.seat == 1 }.selection.target)
            assertNull(step.effectivePlans.first { it.seat == 2 }.selection.target)
            assertEquals(40, step.plans.first { it.seat == 1 }.selection.cost(), "Original cost includes see-through and is unchanged by mutual counter")
            assertEquals(136, step.tiles!!.size)
            // 看破抵消后有效施术人数 n=0：牌山必须与同种子基础洗牌逐张相同。
            assertEquals(
                ShuffleWallGenerator().generate(step.record.seed, 0, emptyList(), config),
                step.tiles
            )
        }
    }

    @Test
    fun `a see-through flag never changes the wall when nobody is countered`() {
        val config = ShuffleConfig(enabled = true, selectionMillis = 1_000L, seeThroughEnabled = true)
        val handler = ShufflePreparationHandler()
        val startedAt = 12_000L
        handler.start("round-solo", startedAt, config)
        val own = ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN, seeThrough = true)
        handler.recordPlan(0, own, own.cost())
        val step = handler.step(
            now = startedAt + config.selectionMillis,
            automatedSeats = emptySet(),
            nativeAiSeats = emptySet(),
            points = List(4) { 100 },
            aiMaySpend = false,
            dealerSeat = 1,
            config = config
        ) as ShufflePreparationHandler.Step.Ready

        assertEquals(setOf(0), step.record.seeThroughSeats)
        // 没有其他看破者，自己的统一目标保留。
        assertEquals(own.target, step.effectivePlans.first { it.seat == 0 }.selection.target)
        assertEquals(
            ShuffleWallGenerator().generate(step.record.seed, 1, step.plans, config),
            step.tiles,
            "Without an opponent see-through, effective plans must match the original plans byte-for-byte"
        )

        // 直接比较 seeThrough 标志对生成器无影响（同种子逐张一致）。
        val basePlans = listOf(
            SeatShufflePlan(0, ShuffleSelection(target = ShuffleTarget.KOKUSHI)),
            SeatShufflePlan(1, ShuffleSelection(target = ShuffleTarget.DAISANGEN))
        )
        val flagged = basePlans.map { it.copy(selection = it.selection.copy(seeThrough = true)) }
        for (seed in 0L..31L) {
            assertEquals(
                ShuffleWallGenerator().generate(seed, 1, basePlans, config),
                ShuffleWallGenerator().generate(seed, 1, flagged, config)
            )
        }
    }

    // ------------------------------------------------------------------
    // 落汗清除与原子发牌
    // ------------------------------------------------------------------

    @Test
    fun `mark clearing keeps only see-through owners and is a single deterministic change`() {
        val state = marksFor(0, 1, 2)
        assertEquals(3, state.groups.size)

        val kept = state.clearMarkGroupsExcept(setOf(0, 2))
        assertEquals(setOf(0, 2), kept.groups.map { it.ownerSeat }.toSet())
        assertEquals(state.version + 1L, kept.version)
        assertEquals(state.dealtRoundIds, kept.dealtRoundIds)

        assertSame(kept, kept.clearMarkGroupsExcept(setOf(0, 1, 2, 3)), "No deletion must return the same instance")
        assertEquals(state.version + 1L, state.clearMarkGroupsExcept(emptySet()).version)
        assertTrue(state.clearMarkGroupsExcept(emptySet()).groups.isEmpty())
        assertThrows<IllegalArgumentException> { state.clearMarkGroupsExcept(setOf(4)) }
    }

    @Test
    fun `authority deal keeps own marks, clears every opponent group and charges original plans`() {
        val clock = ManualClock(1_000L)
        authority(clock).use { a ->
            val roundId = a.coordinator.activeRound!!.roundId
            installMarks(a, marksFor(0, 1, 2, 3))

            assertEquals(ReceiptStatus.APPLIED, a.start(0, clock.now).receipt.status)
            val view = a.buildPreparationViewForSeat(0)!!
            assertTrue(view.seeThroughEnabled)
            assertEquals(10, view.seeThroughCost)
            assertEquals(roundId, view.roundId)

            val seeThroughPlan = planRequest(a, 0, "see-0", ShuffleSelection(seeThrough = true), clock.now)
            val opponentPlan = planRequest(
                a, 1, "opp-1",
                ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN),
                clock.now
            )
            assertEquals(ReceiptStatus.RECORDED, a.submitShufflePlan(0, seeThroughPlan, clock.now).receipt.status)
            assertEquals(ReceiptStatus.RECORDED, a.submitShufflePlan(1, opponentPlan, clock.now).receipt.status)
            assertEquals(10, a.buildPreparationViewForSeat(0)!!.reservedPoints)
            assertEquals(90, a.buildPreparationViewForSeat(0)!!.availablePoints)

            // 相同 requestId 幂等返回原回执；同 requestId 不同载荷拒绝。
            assertEquals(
                a.submitShufflePlan(0, seeThroughPlan, clock.now).receipt,
                a.submitShufflePlan(0, seeThroughPlan, clock.now).receipt
            )
            assertEquals(
                "CONFLICTING_PAYLOAD",
                a.submitShufflePlan(0, seeThroughPlan.copy(selection = ShuffleSelection()), clock.now).receipt.reason
            )

            clock.now = view.deadlineMillis
            val dealt = a.checkPreparationStep(clock.now, emptySet(), emptySet())
            assertTrue(dealt is TableAuthority.AutoStepResult.PreparationDealt)
            assertFalse(a.isPreparationActive())

            val points = a.coordinator.queryCheatPoints()
            assertEquals(90, points[0], "See-through fee is exactly 10")
            assertEquals(70, points[1], "Countered unified target still costs the original 30")
            assertEquals(100, points[2])
            assertEquals(100, points[3])

            val marks = a.coordinator.session.tileMarkState
            assertEquals(setOf(0), marks.groups.map { it.ownerSeat }.toSet(), "Own group survives, all opponent groups die")
            assertEquals(1, marks.groupsForSeat(0).size)
            assertEquals(4, marks.groupsForSeat(0).single().tileIds.size, "The whole own group is retained")

            val state = a.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(state)
            assertEquals(136, TestTableBuilder.actualTiles(state).size)
            assertEquals(136, TestTableBuilder.actualTiles(state).toSet().size)
            assertEquals(TilePool.buildStandardSet().toSet(), TestTableBuilder.actualTiles(state).toSet())
        }
    }

    @Test
    fun `multiple see through owners clear each other and every non see through owner`() {
        val clock = ManualClock(1_500L)
        authority(clock).use { a ->
            installMarks(a, marksFor(0, 1, 2, 3))
            a.start(0, clock.now)
            val deadline = a.buildPreparationViewForSeat(0)!!.deadlineMillis
            val see0 = planRequest(a, 0, "see-0", ShuffleSelection(seeThrough = true), clock.now)
            a.submitShufflePlan(0, see0, clock.now)
            a.submitShufflePlan(
                1,
                planRequest(a, 1, "see-1", ShuffleSelection(seeThrough = true), clock.now),
                clock.now
            )
            a.submitShufflePlan(
                2,
                planRequest(
                    a, 2, "plain-2",
                    ShuffleSelection(target = ShuffleTarget.CHINITSU_SOU),
                    clock.now
                ),
                clock.now
            )

            clock.now = deadline
            assertTrue(a.checkPreparationStep(clock.now, emptySet(), emptySet()) is TableAuthority.AutoStepResult.PreparationDealt)

            // 0 与 1 互相看破：各自的组都被对方清除，非看破者 2/3 的组也全部清除。
            assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
            val points = a.coordinator.queryCheatPoints()
            assertEquals(90, points[0])
            assertEquals(90, points[1])
            assertEquals(70, points[2], "Non see-through plan keeps its original 30 point charge")
            assertEquals(100, points[3])

            // 发牌后重复提交同一 requestId 仍只回放原回执，不再扣费。
            val retry = a.submitShufflePlan(0, see0, clock.now)
            assertEquals(ReceiptStatus.RECORDED, retry.receipt.status)
            assertEquals(90, a.coordinator.queryCheatPoints()[0])
        }
    }

    @Test
    fun `exact ten and forty point balances are accepted and charged once`() {
        val tenClock = ManualClock(1_700L)
        authority(tenClock, points = 10).use { a ->
            a.start(0, tenClock.now)
            val view = a.buildPreparationViewForSeat(0)!!
            a.submitShufflePlan(
                0,
                planRequest(a, 0, "last-ten", ShuffleSelection(seeThrough = true), tenClock.now),
                tenClock.now
            )
            assertEquals(10, a.buildPreparationViewForSeat(0)!!.reservedPoints)
            assertEquals(0, a.buildPreparationViewForSeat(0)!!.availablePoints)
            tenClock.now = view.deadlineMillis
            assertTrue(a.checkPreparationStep(tenClock.now, emptySet(), emptySet()) is TableAuthority.AutoStepResult.PreparationDealt)
            assertEquals(0, a.coordinator.queryCheatPoints()[0])
        }

        val fullClock = ManualClock(1_900L)
        authority(fullClock, points = 100).use { a ->
            a.start(0, fullClock.now)
            val view0 = a.buildPreparationViewForSeat(0)!!
            val full = ShuffleSelection(target = ShuffleTarget.KOKUSHI, seeThrough = true)
            a.submitShufflePlan(
                0,
                planRequest(a, 0, "full-0", ShuffleSelection(seeThrough = true), fullClock.now),
                fullClock.now
            )
            a.submitShufflePlan(
                1,
                planRequest(a, 1, "full-1", full, fullClock.now),
                fullClock.now
            )
            assertEquals(40, a.buildPreparationViewForSeat(1)!!.reservedPoints)
            assertEquals(60, a.buildPreparationViewForSeat(1)!!.availablePoints)
            fullClock.now = view0.deadlineMillis
            assertTrue(a.checkPreparationStep(fullClock.now, emptySet(), emptySet()) is TableAuthority.AutoStepResult.PreparationDealt)
            val points = a.coordinator.queryCheatPoints()
            assertEquals(90, points[0])
            // 被 0 号看破取消整局统一目标，但原始 30+10 费用照扣。
            assertEquals(60, points[1])
        }
    }

    @Test
    fun `opponent marks can be marked again after the deal while own marks persist`() {
        val clock = ManualClock(2_000L)
        authority(clock).use { a ->
            installMarks(a, marksFor(0, 1))
            a.start(0, clock.now)
            val view = a.buildPreparationViewForSeat(0)!!
            a.submitShufflePlan(0, planRequest(a, 0, "see", ShuffleSelection(seeThrough = true), clock.now), clock.now)
            clock.now = view.deadlineMillis
            assertTrue(a.checkPreparationStep(clock.now, emptySet(), emptySet()) is TableAuthority.AutoStepResult.PreparationDealt)
            assertEquals(setOf(0), a.coordinator.session.tileMarkState.groups.map { it.ownerSeat }.toSet())

            a.syncDecisionsAndClocks(clock.now, emptySet())
            val decision = a.queryDecision(0, false)
            val handView = a.coordinator.queryView(0)
            val marked = a.coordinator.session.tileMarkState.groupsForSeat(0).flatMap { it.tileIds }.toSet()
            val freeIds = (handView.myClosedTiles + listOfNotNull(handView.myDrawnTile))
                .map { it.tileId }
                .filter { it !in marked }
                .take(4)
            assertEquals(4, freeIds.size)
            val request = TileMarkRequest(
                requestId = "post-deal-mark",
                sessionId = a.sessionId,
                roundId = a.coordinator.activeRound!!.roundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                tileIds = freeIds
            )
            assertEquals(ReceiptStatus.APPLIED, a.submitTileMark(0, request, clock.now).receipt.status)
            assertEquals(2, a.coordinator.session.tileMarkState.groupsForSeat(0).size)
        }
    }

    @Test
    fun `failed wall or fee validation leaves marks fees and dealt ordinal untouched`() {
        val initial = GameState().apply { cheatPoints.fill(100) }
        val coordinator = ApplicationCoordinator(
            initialState = initial,
            autoPlayAi = false,
            shuffleConfig = ShuffleConfig(enabled = true, seeThroughEnabled = true, selectionMillis = 1_000L),
            cheatEconomyConfig = economy(100)
        )
        val round = coordinator.activeRound!!
        val session = coordinator.session
        val marks = marksFor(0, 1, 2)
        val carrier = session.prepare(ApprovedSessionStep.RegisterDealtRound(round.roundId))
        session.install(carrier.copy(targetTileMarkState = marks))
        val versionBefore = round.version
        val sessionVersionBefore = session.version
        val dealtBefore = session.tileMarkState.dealtRoundIds

        val good = TilePool.buildStandardSet()
        val broken = good.toMutableList().apply { this[135] = this[0] }
        assertTrue(
            coordinator.executeDealWithPreparation(
                round.roundId, broken, mapOf(0 to 10), emptyMap(), setOf(0)
            ) is ActionResult.Rejected
        )
        // 费用超过余额同样零副作用
        assertTrue(
            coordinator.executeDealWithPreparation(
                round.roundId, good, mapOf(0 to 101), emptyMap(), setOf(0)
            ) is ActionResult.Rejected
        )

        assertEquals(versionBefore, round.version)
        assertEquals(sessionVersionBefore, session.version)
        assertEquals(listOf(100, 100, 100, 100), round.readState().cheatPoints.toList())
        assertEquals(dealtBefore, session.tileMarkState.dealtRoundIds)
        assertEquals(setOf(0, 1, 2), session.tileMarkState.groups.map { it.ownerSeat }.toSet())

        // 成功发牌一次性扣费、清标并登记局次。
        assertTrue(
            coordinator.executeDealWithPreparation(
                round.roundId, good, mapOf(0 to 10), mapOf(0 to "shuffle_plan"), setOf(0)
            ) is ActionResult.Accepted
        )
        assertEquals(90, round.readState().cheatPoints[0])
        assertEquals(setOf(0), session.tileMarkState.groups.map { it.ownerSeat }.toSet())
        assertTrue(round.roundId in session.tileMarkState.dealtRoundIds)
        assertTrue(session.tileMarkState.dealtRoundIds.containsAll(dealtBefore))
    }

    @Test
    fun `disabled ability insufficient funds deadline and stale phases are rejected without side effects`() {
        // 能力关闭
        val clock = ManualClock(3_000L)
        authority(clock, seeThrough = false).use { a ->
            a.start(0, clock.now)
            val view = a.buildPreparationViewForSeat(0)!!
            assertFalse(view.seeThroughEnabled)
            val request = planRequest(a, 0, "disabled", ShuffleSelection(seeThrough = true), clock.now)
            val rejected = a.submitShufflePlan(0, request, clock.now)
            assertEquals(ReceiptStatus.REJECTED, rejected.receipt.status)
            assertEquals("SEE_THROUGH_DISABLED", rejected.receipt.reason)
            assertEquals(0, view.reservedPoints)
        }

        // 余额 9 无法支付 10
        val poorClock = ManualClock(4_000L)
        authority(poorClock, points = 9).use { a ->
            a.start(0, poorClock.now)
            val request = planRequest(a, 0, "poor", ShuffleSelection(seeThrough = true), poorClock.now)
            assertEquals("INSUFFICIENT_FUNDS", a.submitShufflePlan(0, request, poorClock.now).receipt.reason)
        }

        // 截止边界、旧局、旧代次
        val edgeClock = ManualClock(5_000L)
        authority(edgeClock).use { a ->
            a.start(0, edgeClock.now)
            val view = a.buildPreparationViewForSeat(0)!!
            val request = planRequest(a, 0, "edge", ShuffleSelection(seeThrough = true), edgeClock.now)
            assertEquals("STALE_EPOCH", a.submitShufflePlan(0, request.copy(controlEpoch = request.controlEpoch + 1), edgeClock.now).receipt.reason)
            assertEquals("WRONG_PHASE", a.submitShufflePlan(0, request.copy(roundId = "other-round"), edgeClock.now).receipt.reason)
            assertEquals("PREPARATION_EXPIRED", a.submitShufflePlan(0, request, view.deadlineMillis).receipt.reason)
        }
    }
}

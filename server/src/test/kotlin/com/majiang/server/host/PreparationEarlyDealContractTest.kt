package com.majiang.server.host

import com.majiang.access.ReceiptStatus
import com.majiang.ai.AiCheatPolicy
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.AbstractExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 正式 APP 单机准备阶段的早开局回归（玩家点“不使用千术”后不得等满 10/15 秒）。
 *
 * 覆盖：
 * 1. 1 真人 + 3 默认电脑：策略禁用/预算 0 的电脑由权威及时零费用确认 skip；
 * 2. 真人 skip 只确认自己，其他未确认真人继续等待；
 * 3. 统一 AI 策略开启且有预算时仍允许电脑施术，不能把 aiMaySpend=false 当作全部 skip；
 * 4. 所有席位确认后在 deadline 前的正常 tick 立即发牌，0 费用或被认可的原方案费用只结算一次。
 */
@Timeout(30)
class PreparationEarlyDealContractTest {

    private class TestClock(var now: Long = 0L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private class DirectExecutorService : AbstractExecutorService() {
        private val closed = AtomicBoolean(false)
        override fun execute(command: Runnable) {
            if (!closed.get()) command.run()
        }

        override fun shutdown() {
            closed.set(true)
        }

        override fun shutdownNow(): MutableList<Runnable> {
            closed.set(true)
            return mutableListOf()
        }

        override fun isShutdown(): Boolean = closed.get()
        override fun isTerminated(): Boolean = closed.get()
        override fun awaitTermination(timeout: Long, unit: TimeUnit): Boolean = true
    }

    private fun <T> java.util.concurrent.CompletableFuture<T>.await(): T =
        get(5, TimeUnit.SECONDS)

    private fun economy(points: Int = 50) = CheatEconomyConfig(
        enabled = true,
        initialPoints = List(4) { points },
        costs = mapOf(
            "TILE_MARK" to 10,
            "SLEEVE_BLOOM" to 10,
            "BEAM_SWAP" to 10,
            "SEE_THROUGH" to 10
        )
    )

    private fun config(policy: AiCheatPolicy = AiCheatPolicy()) = MatchConfig(
        winds = listOf(1),
        cheatEconomy = economy(),
        shuffleConfig = ShuffleConfig(enabled = true, selectionMillis = 10_000L),
        aiCheatPolicy = policy
    )

    private fun skip(host: ServerTableHost, seat: Int, id: String): ReceiptStatus {
        val snapshot = host.snapshot(seat).await()
        val roundId = snapshot.preparation?.roundId ?: error("preparation must be active for seat $seat")
        return host.submitShufflePlan(
            seat,
            ShufflePlanRequest(
                requestId = id,
                roundId = roundId,
                controlEpoch = snapshot.decision.controlEpoch,
                selection = ShuffleSelection()
            )
        ).await().status
    }

    @Test
    fun `one human plus three default computers deals before deadline for zero cost`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = config(),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            ),
            aiExecutor = DirectExecutorService()
        ).use { host ->
            assertEquals(ReceiptStatus.APPLIED, host.start(0).await().status)
            val prep = host.snapshot(0).await().preparation
            assertNotNull(prep)
            assertEquals(10_000L, prep!!.deadlineMillis)

            // 默认电脑没有启用任何付费能力：权威应在 tick 中零费用确认 skip。
            host.tick().await()
            assertEquals(ReceiptStatus.RECORDED, skip(host, 0, "human-skip"))

            // 模拟少量正常 tick，远未到 10 秒 deadline。
            clock.now = 60L
            repeat(3) { host.tick().await() }

            val after = host.snapshot(0).await()
            assertNull(after.preparation, "all seats confirmed must deal before the deadline")
            assertFalse(host.authority.isPreparationActive())
            assertTrue(clock.now < prep.deadlineMillis)
            assertEquals(List(4) { 50 }, after.cheatPoints, "default computers must skip at zero cost")
            assertEquals(14, after.view.myAllClosedTiles.size, "dealer must receive a real 14-tile hand")
            for (seat in 1..3) {
                assertEquals(13, host.snapshot(seat).await().view.myAllClosedTiles.size)
            }

            // 再 tick 不得重发牌或重复扣费。
            repeat(3) { host.tick().await() }
            assertEquals(List(4) { 50 }, host.snapshot(0).await().cheatPoints)
            assertEquals(14, host.snapshot(0).await().view.myAllClosedTiles.size)
        }
    }

    @Test
    fun `human skip never confirms another human and all-human confirmation ends early`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = config(),
            clock = clock,
            controllers = List(4) { SeatController.LocalHuman() },
            aiExecutor = DirectExecutorService()
        ).use { host ->
            assertEquals(ReceiptStatus.APPLIED, host.start(0).await().status)
            assertEquals(ReceiptStatus.RECORDED, skip(host, 0, "human-0"))
            clock.now = 50L
            host.tick().await()
            assertNotNull(host.snapshot(0).await().preparation, "one human skip must not confirm seats 1..3")

            assertEquals(ReceiptStatus.RECORDED, skip(host, 1, "human-1"))
            assertEquals(ReceiptStatus.RECORDED, skip(host, 2, "human-2"))
            clock.now = 80L
            host.tick().await()
            assertNotNull(host.snapshot(0).await().preparation, "seat 3 is still unconfirmed")
            assertFalse(host.snapshot(3).await().preparation!!.confirmed)

            assertEquals(ReceiptStatus.RECORDED, skip(host, 3, "human-3"))
            clock.now = 100L
            host.tick().await()
            assertNull(host.snapshot(0).await().preparation, "all four humans confirmed must end early")
            assertEquals(List(4) { 50 }, host.snapshot(0).await().cheatPoints)
        }
    }

    @Test
    fun `ai policy disabled or zero budget auto-confirms zero-cost skip`() {
        for (policy in listOf(
            AiCheatPolicy(enabled = false),
            AiCheatPolicy(enabled = true, maxSpendPerRound = 0)
        )) {
            val clock = TestClock(0L)
            ServerTableHost(
                config = config(policy),
                clock = clock,
                controllers = listOf(
                    SeatController.LocalHuman(),
                    SeatController.Computer(),
                    SeatController.Computer(),
                    SeatController.Computer()
                ),
                aiExecutor = DirectExecutorService()
            ).use { host ->
                assertEquals(ReceiptStatus.APPLIED, host.start(0).await().status)
                host.tick().await()
                assertEquals(ReceiptStatus.RECORDED, skip(host, 0, "human-skip"))
                clock.now = 70L
                host.tick().await()
                assertNull(host.snapshot(0).await().preparation, "policy=$policy")
                assertEquals(List(4) { 50 }, host.snapshot(0).await().cheatPoints, "policy=$policy")
            }
        }
    }

    @Test
    fun `enabled ai budget still spends before an early human-driven deal`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = config(AiCheatPolicy(enabled = true, maxSpendPerRound = 60)),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            ),
            aiExecutor = DirectExecutorService()
        ).use { host ->
            assertEquals(ReceiptStatus.APPLIED, host.start(0).await().status)
            repeat(6) { host.tick().await() }
            for (seat in 1..3) {
                val prep = host.snapshot(seat).await().preparation
                assertTrue(prep?.confirmed == true, "seat $seat must confirm through the unified AI path")
                assertEquals(30, prep!!.reservedPoints, "enabled budget must allow a paid plan for seat $seat")
            }

            assertEquals(ReceiptStatus.RECORDED, skip(host, 0, "human-skip"))
            clock.now = 120L
            host.tick().await()
            val after = host.snapshot(0).await()
            assertNull(after.preparation)
            assertTrue(clock.now < 10_000L)
            assertEquals(50, after.cheatPoints[0], "human chose skip")
            for (seat in 1..3) {
                assertEquals(20, host.snapshot(seat).await().cheatPoints[seat], "AI paid plan must be charged once")
            }
        }
    }
}

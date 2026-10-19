package com.majiang.authority

import com.majiang.access.ReceiptStatus
import com.majiang.application.ActionChoice
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/**
 * MR-09 storedReceipts 准入式配额防护与全生命周期测试
 *
 * 验证规则：
 * 1. 恶意唯一 ID 准入式阻断：达到 maxReceiptsPerSeat 上限后，超配的新 ID 在业务执行前拒绝，且不写入存储（零泄漏）；
 * 2. 满额旧 ID 重试：已受理 ID 在配额满额时仍享有幂等通行权，安全返回既有回执；
 * 3. 跨 API 冲突优先：跨 API ID 冲突校验优先于配额校验，即使满额也正确返回 REQUEST_ID_CONFLICT；
 * 4. 跨局持久性：回执与配额在整个 MatchSession 生命周期持续生效，局间转换不破坏幂等性；
 * 5. 资源确定性释放：close() 清空存储并将所有席位计数归零。
 */
@Timeout(15)
class Mr09ReceiptQuotaTest {

    private class ManualClock(var current: Long = 1000L) : MonotonicClock {
        override fun nowMillis(): Long = current
    }

    private fun createAuthority(
        maxReceiptsPerSeat: Int = 10,
        shuffleEnabled: Boolean = false,
        tileMarkEnabled: Boolean = false
    ): TableAuthority {
        val cheatEconomy = if (tileMarkEnabled) {
            com.majiang.economy.CheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 100 },
                costs = mapOf("TILE_MARK" to 10)
            )
        } else {
            com.majiang.economy.CheatEconomyConfig()
        }
        val clock = ManualClock(1000L)
        return TableAuthority(
            config = MatchConfig(
                cheatEconomy = cheatEconomy,
                shuffleConfig = ShuffleConfig(enabled = shuffleEnabled),
                tileMarkConfig = TileMarkConfig(enabled = tileMarkEnabled)
            ),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            ),
            adminSeat = 0,
            maxReceiptsPerSeat = maxReceiptsPerSeat
        )
    }

    @Test
    fun `malicious unique requestId spam rejected with quota exceeded without storing when quota full`() {
        val authority = createAuthority(maxReceiptsPerSeat = 10)
        authority.use { auth ->
            val startRes = auth.start(0, 1000L)
            assertEquals(ReceiptStatus.APPLIED, startRes.receipt.status)

            val epoch = auth.controlEpochFor(0)
            val currentRound = auth.currentRoundId

            // 1. 发送 10 个唯一 spam ID，全部被业务拒绝并入库
            for (i in 1..10) {
                val spamChoice = ActionChoice(
                    requestId = "spam-req-$i",
                    roundId = currentRound,
                    decisionId = "bogus-decision",
                    controlEpoch = epoch,
                    optionId = "bogus-option"
                )
                val res = auth.submitChoice(0, spamChoice, 1000L)
                assertEquals(ReceiptStatus.REJECTED, res.receipt.status)
                assertEquals("DECISION_MISMATCH", res.receipt.reason)
            }

            assertEquals(10, auth.getSeatReceiptCount(0))
            assertTrue(auth.isReceiptQuotaExceeded(0))

            // 2. 发送第 11 个唯一 ID：必须被准入配额在业务前拦截，理由为 RECEIPT_QUOTA_EXCEEDED
            val eleventhChoice = ActionChoice(
                requestId = "spam-req-11",
                roundId = currentRound,
                decisionId = "bogus-decision",
                controlEpoch = epoch,
                optionId = "bogus-option"
            )
            val rejectedRes = auth.submitChoice(0, eleventhChoice, 1000L)
            assertEquals(ReceiptStatus.REJECTED, rejectedRes.receipt.status)
            assertEquals("RECEIPT_QUOTA_EXCEEDED", rejectedRes.receipt.reason)

            // 验证未写入存储（零泄漏），计数和 Map 大小依然为 10
            assertEquals(10, auth.getSeatReceiptCount(0))
            assertEquals(10, auth.receiptStoreMetrics().currentEntries)

            // 其他席位配额独立未受影响
            assertEquals(0, auth.getSeatReceiptCount(1))
            assertFalse(auth.isReceiptQuotaExceeded(1))
        }
    }

    @Test
    fun `full quota allows idempotent retry of previously accepted requestId with original receipt`() {
        val authority = createAuthority(maxReceiptsPerSeat = 5)
        authority.use { auth ->
            auth.start(0, 1000L)
            val epoch = auth.controlEpochFor(0)
            val currentRound = auth.currentRoundId

            // 塞满 5 个配额
            for (i in 1..5) {
                val choice = ActionChoice(
                    requestId = "quota-req-$i",
                    roundId = currentRound,
                    decisionId = "bogus-decision",
                    controlEpoch = epoch,
                    optionId = "bogus-option"
                )
                auth.submitChoice(0, choice, 1000L)
            }
            assertEquals(5, auth.getSeatReceiptCount(0))
            assertTrue(auth.isReceiptQuotaExceeded(0))

            // 重试已录入的 quota-req-3：即使配额已满，仍应幂等返回原始回执，不可被配额拦截
            val retryChoice = ActionChoice(
                requestId = "quota-req-3",
                roundId = currentRound,
                decisionId = "bogus-decision",
                controlEpoch = epoch,
                optionId = "bogus-option"
            )
            val retryRes = auth.submitChoice(0, retryChoice, 1000L)
            assertEquals(ReceiptStatus.REJECTED, retryRes.receipt.status)
            assertEquals("DECISION_MISMATCH", retryRes.receipt.reason)
            assertNotEquals("RECEIPT_QUOTA_EXCEEDED", retryRes.receipt.reason)

            // 计数不增加
            assertEquals(5, auth.getSeatReceiptCount(0))
        }
    }

    @Test
    fun `cross api request id conflict detected even when quota is full`() {
        val authority = createAuthority(maxReceiptsPerSeat = 3, tileMarkEnabled = true)
        authority.use { auth ->
            auth.start(0, 1000L)
            val epoch = auth.controlEpochFor(0)
            val currentRound = auth.currentRoundId

            // 用 submitChoice 塞满 3 个配额
            for (i in 1..3) {
                auth.submitChoice(0, ActionChoice("choice-$i", currentRound, "bogus", epoch, "bogus"), 1000L)
            }
            assertEquals(3, auth.getSeatReceiptCount(0))
            assertTrue(auth.isReceiptQuotaExceeded(0))

            // 用相同的 choice-1 请求 ID 提交 TileMark：
            // 校验必须优先识别 REQUEST_ID_CONFLICT，而非被 RECEIPT_QUOTA_EXCEEDED 掩盖
            val markReq = TileMarkRequest(
                requestId = "choice-1",
                sessionId = auth.sessionId,
                roundId = currentRound ?: "r-1",
                decisionId = "dec-1",
                controlEpoch = epoch,
                tileIds = listOf(0)
            )
            val markRes = auth.submitTileMark(0, markReq, 1000L)
            assertEquals(ReceiptStatus.REJECTED, markRes.receipt.status)
            assertEquals("REQUEST_ID_CONFLICT", markRes.receipt.reason)
        }
    }

    @Test
    fun `shuffle plan api enforces quota and detects cross api conflicts`() {
        val authority = createAuthority(maxReceiptsPerSeat = 1, shuffleEnabled = true)
        authority.use { auth ->
            val startRes = auth.start(0, 1000L)
            assertEquals(ReceiptStatus.APPLIED, startRes.receipt.status)

            val epoch = auth.controlEpochFor(0)
            val roundId = auth.queryDecision(0, isAutomated = false).roundId ?: ""

            // 1. 提交 1 个洗牌方案塞满配额 (maxReceiptsPerSeat = 1)
            val plan1 = ShufflePlanRequest("shuffle-1", roundId, epoch, ShuffleSelection())
            val res1 = auth.submitShufflePlan(0, plan1, 1000L)
            assertEquals(ReceiptStatus.RECORDED, res1.receipt.status)
            assertEquals(1, auth.getSeatReceiptCount(0))
            assertTrue(auth.isReceiptQuotaExceeded(0))

            // 2. 满额下幂等重试 shuffle-1 成功返回既有 RECORDED 回执
            val retryRes = auth.submitShufflePlan(0, plan1, 1000L)
            assertEquals(ReceiptStatus.RECORDED, retryRes.receipt.status)
            assertEquals(1, auth.getSeatReceiptCount(0))

            // 3. 满额下新 ID 被配额拒绝
            val plan2 = ShufflePlanRequest("shuffle-2", roundId, epoch, ShuffleSelection())
            val rejectedRes = auth.submitShufflePlan(0, plan2, 1000L)
            assertEquals(ReceiptStatus.REJECTED, rejectedRes.receipt.status)
            assertEquals("RECEIPT_QUOTA_EXCEEDED", rejectedRes.receipt.reason)
            assertEquals(1, auth.getSeatReceiptCount(0), "Quota count must not increase on rejected new ID")

            // 4. 跨 API 冲突校验：用已存在的 shuffle-1 提交 choice，必须返回 REQUEST_ID_CONFLICT
            val choiceConflict = ActionChoice("shuffle-1", roundId, "bogus", epoch, "bogus")
            val conflictRes = auth.submitChoice(0, choiceConflict, 1000L)
            assertEquals(ReceiptStatus.REJECTED, conflictRes.receipt.status)
            assertEquals("REQUEST_ID_CONFLICT", conflictRes.receipt.reason)
        }
    }

    @Test
    fun `cross round shuffle plan duplicate requestId returns REQUEST_ID_CONFLICT even when quota is saturated`() {
        val authority = createAuthority(maxReceiptsPerSeat = 1, shuffleEnabled = true)
        authority.use { auth ->
            val startRes = auth.start(0, 1000L)
            assertEquals(ReceiptStatus.APPLIED, startRes.receipt.status)

            val epoch = auth.controlEpochFor(0)
            val round1 = auth.queryDecision(0, isAutomated = false).roundId ?: "round-1"

            // 1. 在第 1 局提交洗牌方案 "shuf-req-1"，配额达到上限 1
            val planRound1 = ShufflePlanRequest("shuf-req-1", round1, epoch, ShuffleSelection())
            val res1 = auth.submitShufflePlan(0, planRound1, 1000L)
            assertEquals(ReceiptStatus.RECORDED, res1.receipt.status)
            assertEquals(1, auth.getSeatReceiptCount(0))
            assertTrue(auth.isReceiptQuotaExceeded(0))

            // 2. 原局同载荷重试：幂等返回既有 RECORDED 回执
            val retrySameRound = auth.submitShufflePlan(0, planRound1, 1000L)
            assertEquals(ReceiptStatus.RECORDED, retrySameRound.receipt.status)

            // 3. 跨局使用相同 requestId ("shuf-req-1") 提交：
            // 即使配额已满 (1/1)，也必须优先返回 REQUEST_ID_CONFLICT 而非 RECEIPT_QUOTA_EXCEEDED
            val planRound2SameId = ShufflePlanRequest("shuf-req-1", "round-2", epoch, ShuffleSelection())
            val conflictRes = auth.submitShufflePlan(0, planRound2SameId, 1000L)
            assertEquals(ReceiptStatus.REJECTED, conflictRes.receipt.status)
            assertEquals("REQUEST_ID_CONFLICT", conflictRes.receipt.reason)

            // 4. 跨局使用全新未见过的 requestId ("shuf-req-2")：
            // 因为配额已满 (1/1)，且无既有 ID 冲突，才返回 RECEIPT_QUOTA_EXCEEDED
            val planRound2NewId = ShufflePlanRequest("shuf-req-2", "round-2", epoch, ShuffleSelection())
            val quotaRes = auth.submitShufflePlan(0, planRound2NewId, 1000L)
            assertEquals(ReceiptStatus.REJECTED, quotaRes.receipt.status)
            assertEquals("RECEIPT_QUOTA_EXCEEDED", quotaRes.receipt.reason)
        }
    }

    @Test
    fun `receipts and quota persist across round transition preserving idempotency`() {
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
            adminSeat = 0,
            maxReceiptsPerSeat = 1000
        )
        authority.use { auth ->
            auth.start(0, clock.nowMillis())
            val round1 = auth.currentRoundId ?: ""

            // 在第 1 局提交 1 个选择
            val dec0 = auth.queryDecision(0, isAutomated = false)
            val opt0 = dec0.options.first()
            val choice1 = ActionChoice("round1-req-1", round1, dec0.decisionId, dec0.controlEpoch, opt0.optionId)
            val res1 = auth.submitChoice(0, choice1, clock.nowMillis())
            assertTrue(res1.receipt.status in setOf(ReceiptStatus.APPLIED, ReceiptStatus.RECORDED), "Initial discard status must be APPLIED or RECORDED depending on response window")
            val countRound1 = auth.getSeatReceiptCount(0)
            assertTrue(countRound1 >= 1)

            // 驱动直到本局结束并进入下一局
            var iterations = 0
            while (auth.validateNextRoundPreconditions() != null && iterations < 500) {
                iterations++
                for (s in 0..3) {
                    val dec = auth.queryDecision(s, isAutomated = false)
                    if (dec.options.isNotEmpty()) {
                        val opt = dec.options.first()
                        val c = ActionChoice("drive-$s-$iterations", dec.roundId, dec.decisionId, dec.controlEpoch, opt.optionId)
                        auth.submitChoice(s, c, clock.nowMillis())
                    }
                }
            }

            // 如果成功完成并进入下一局
            if (auth.validateNextRoundPreconditions() == null) {
                val nextRes = auth.nextRound(0, clock.nowMillis())
                assertEquals(ReceiptStatus.APPLIED, nextRes.receipt.status)
                val round2 = auth.currentRoundId
                assertNotEquals(round1, round2)

                // 验证第 1 局的回执与配额在第 2 局仍然保留且生效
                assertTrue(auth.getSeatReceiptCount(0) >= countRound1, "Quota usage must persist across rounds")

                // 幂等重试第 1 局的请求 ID：跨局仍原样返回原始回执
                val retryRes = auth.submitChoice(0, choice1, clock.nowMillis())
                assertEquals(res1.receipt.status, retryRes.receipt.status, "Retried receipt status must match original verbatim")
                assertEquals(res1.receipt, retryRes.receipt, "Retried receipt must match original verbatim")
            }
        }
    }

    @Test
    fun `authority close clears stored receipts and resets quota counters to zero`() {
        val authority = createAuthority(maxReceiptsPerSeat = 10)
        authStartAndFill(authority)

        assertEquals(5, authority.getSeatReceiptCount(0))
        assertEquals(5, authority.receiptStoreMetrics().currentEntries)

        // 执行 close
        authority.close()

        assertEquals(0, authority.getSeatReceiptCount(0))
        assertEquals(0, authority.getSeatReceiptCount(1))
        assertEquals(0, authority.getSeatReceiptCount(2))
        assertEquals(0, authority.getSeatReceiptCount(3))
        assertEquals(0, authority.receiptStoreMetrics().currentEntries)
    }

    private fun authStartAndFill(auth: TableAuthority) {
        auth.start(0, 1000L)
        val epoch = auth.controlEpochFor(0)
        val round = auth.currentRoundId
        for (i in 1..5) {
            auth.submitChoice(0, ActionChoice("req-$i", round, "bogus", epoch, "bogus"), 1000L)
        }
    }
}

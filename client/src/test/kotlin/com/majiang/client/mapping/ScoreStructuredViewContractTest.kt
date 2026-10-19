package com.majiang.client.mapping

import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolDoraBreakdown
import com.majiang.transport.api.ProtocolFuBreakdown
import com.majiang.transport.api.ProtocolFuLine
import com.majiang.transport.api.ProtocolPaymentComponent
import com.majiang.transport.api.ProtocolPaymentPlan
import com.majiang.transport.api.ProtocolScoreBreakdown
import com.majiang.transport.api.ProtocolScoreCodes
import com.majiang.transport.api.ProtocolScoreResult
import com.majiang.transport.api.ProtocolYakuEntry
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 v5 protocol → client view 结构化字段映射与安全回退。
 */
class ScoreStructuredViewContractTest {

    private fun breakdown(
        capCode: String? = "MANGAN",
        yakuEntries: List<ProtocolYakuEntry> = listOf(
            ProtocolYakuEntry("PINFU", "平和", 1),
            ProtocolYakuEntry("TANYAO", "断幺九", 1)
        ),
        paymentPlan: ProtocolPaymentPlan? = ProtocolPaymentPlan(
            "NON_DEALER_RON",
            listOf(ProtocolPaymentComponent("DISCARDER", 8000, 1)),
            8000
        )
    ): ProtocolScoreBreakdown = ProtocolScoreBreakdown(
        yakuLines = listOf("平和" to 1, "断幺九" to 1),
        yakumanCount = 0,
        dora = ProtocolDoraBreakdown(
            0, 0, 0, 0, emptyList(), emptyList(), emptyList(), emptyList(), emptyList()
        ),
        fu = ProtocolFuBreakdown(listOf(ProtocolFuLine("底符", 30)), 30, 30, 30, null),
        score = ProtocolScoreResult(
            fu = 30, han = 2, basePoints = 2000, title = "満貫",
            isYakuman = false, yakumanMultiplier = 0, rawBasePoints = 2000,
            capReason = "満貫封頂", formulaText = "30符", rawBasePointsExact = "2000",
            capCode = capCode
        ),
        paymentLines = listOf("荣和：P3 支付 8000"),
        honbaAddition = 300, riichiAddition = 1000, totalPoints = 9300,
        uraEligible = false, isDealer = false, isTsumo = false,
        yakuEntries = yakuEntries,
        paymentPlan = paymentPlan
    )

    @Test
    fun `v5 structured fields map to views and keep legacy values`() {
        val view = ProtocolViewMapper.mapScoreBreakdown(breakdown())

        assertEquals("MANGAN", view.score.capCode)
        assertEquals("満貫封頂", view.score.capReason)
        assertEquals(2000, view.score.basePoints)
        assertEquals(listOf("PINFU", "TANYAO"), view.yakuEntries.map { it.code })
        assertEquals(listOf("平和", "断幺九"), view.yakuEntries.map { it.displayName })
        assertEquals(listOf(1, 1), view.yakuEntries.map { it.han })
        assertEquals("NON_DEALER_RON", view.paymentPlan!!.kind)
        assertEquals(8000, view.paymentPlan!!.amountOf("DISCARDER"))
        assertEquals(1, view.paymentPlan!!.countOf("DISCARDER"))
        assertEquals(8000, view.paymentPlan!!.total)
        // 旧字段逐字保留。
        assertEquals(listOf("平和" to 1, "断幺九" to 1), view.yakuLines)
        assertEquals(listOf("荣和：P3 支付 8000"), view.paymentLines)
        assertEquals(300, view.honbaAddition)
        assertEquals(1000, view.riichiAddition)
        assertEquals(9300, view.totalPoints)
    }

    @Test
    fun `unknown cap and payment codes fall back to legacy text instead of failing`() {
        val view = ProtocolViewMapper.mapScoreBreakdown(
            breakdown(
                capCode = "FUTURE_CAP",
                paymentPlan = ProtocolPaymentPlan(
                    "FUTURE_KIND",
                    listOf(ProtocolPaymentComponent("FUTURE_ROLE", 2000, 1)),
                    2000
                )
            )
        )
        assertNull(view.score.capCode, "unknown cap code must not reach presentation")
        assertEquals("満貫封頂", view.score.capReason)
        assertNull(view.paymentPlan, "unknown payment kind must fall back to paymentLines")
        assertEquals(listOf("荣和：P3 支付 8000"), view.paymentLines)
    }

    @Test
    fun `unknown payer role or inconsistent plan is dropped for legacy fallback`() {
        val unknownRole = ProtocolViewMapper.mapScoreBreakdown(
            breakdown(
                paymentPlan = ProtocolPaymentPlan(
                    "NON_DEALER_RON",
                    listOf(ProtocolPaymentComponent("FUTURE_ROLE", 2000, 1)),
                    2000
                )
            )
        )
        assertNull(unknownRole.paymentPlan)

        val inconsistentTotal = ProtocolViewMapper.mapScoreBreakdown(
            breakdown(
                paymentPlan = ProtocolPaymentPlan(
                    "NON_DEALER_RON",
                    listOf(ProtocolPaymentComponent("DISCARDER", 2000, 1)),
                    1900
                )
            )
        )
        assertNull(inconsistentTotal.paymentPlan)
        assertEquals(listOf("荣和：P3 支付 8000"), inconsistentTotal.paymentLines)
    }

    @Test
    fun `unknown yaku code keeps displayName for presentation fallback`() {
        val view = ProtocolViewMapper.mapScoreBreakdown(
            breakdown(
                yakuEntries = listOf(
                    ProtocolYakuEntry("FUTURE_YAKU", "未来役", 2),
                    ProtocolYakuEntry("TANYAO", "断幺九", 1)
                )
            )
        )
        assertEquals("FUTURE_YAKU", view.yakuEntries[0].code)
        assertEquals("未来役", view.yakuEntries[0].displayName)
        assertEquals(2, view.yakuEntries[0].han)
        assertEquals("平和", view.yakuLines[0].first)
    }

    @Test
    fun `capability scoreDetailVersion survives protocol to view and back`() {
        val protocol = ProtocolAccessCapabilities(
            protocolVersion = 5,
            scoreDetailVersion = ProtocolScoreCodes.CONTRACT_VERSION
        )
        val view = ProtocolViewMapper.mapCapabilities(protocol)
        assertEquals(5, view.protocolVersion)
        assertEquals(1, view.scoreDetailVersion)
        val roundtrip = ProtocolViewMapper.toProtocolCapabilities(view)
        assertEquals(1, roundtrip.scoreDetailVersion)
        assertEquals(5, roundtrip.protocolVersion)
    }

    @Test
    fun `client payment code catalog matches the protocol frozen catalog`() {
        assertEquals(ProtocolScoreCodes.PAYMENT_KINDS, com.majiang.client.api.ScorePaymentCodesView.PAYMENT_KINDS)
        assertEquals(ProtocolScoreCodes.PAYER_ROLES, com.majiang.client.api.ScorePaymentCodesView.PAYER_ROLES)
    }

    @Test
    fun `cross-field inconsistent plan is dropped even when adapter validation is bypassed`() {
        // 非庄荣和套用 DEALER 付款方：与 kind/旧 paymentLines 冲突。
        val wrongRole = breakdown(
            paymentPlan = ProtocolPaymentPlan(
                "NON_DEALER_RON",
                listOf(ProtocolPaymentComponent("DEALER", 2000, 1)),
                2000
            )
        )
        val wrongRoleView = ProtocolViewMapper.mapScoreBreakdown(wrongRole)
        assertNull(wrongRoleView.paymentPlan)
        assertEquals(listOf("荣和：P3 支付 8000"), wrongRoleView.paymentLines)

        // 旧 paymentLines 金额与计划不一致。
        val wrongLines = breakdown().copy(
            paymentLines = listOf("荣和：P3 支付 7999")
        )
        assertNull(ProtocolViewMapper.mapScoreBreakdown(wrongLines).paymentPlan)

        // 总得点不等于基础支付 + 本场 + 立直。
        val wrongTotalPoints = breakdown().copy(totalPoints = 9301)
        assertNull(ProtocolViewMapper.mapScoreBreakdown(wrongTotalPoints).paymentPlan)

        // kind 与 isDealer/isTsumo 形态冲突。
        val wrongForm = breakdown(
            paymentPlan = ProtocolPaymentPlan(
                "DEALER_TSUMO",
                listOf(ProtocolPaymentComponent("NON_DEALER", 4000, 3)),
                12000
            )
        )
        assertNull(ProtocolViewMapper.mapScoreBreakdown(wrongForm).paymentPlan)
    }

    @Test
    fun `view collections are immutable and do not alias protocol lists`() {
        val entries = arrayListOf(ProtocolYakuEntry("PINFU", "平和", 1))
        val components = arrayListOf(ProtocolPaymentComponent("DISCARDER", 8000, 1))
        val proto = breakdown(
            yakuEntries = entries,
            paymentPlan = ProtocolPaymentPlan("NON_DEALER_RON", components, 8000)
        )
        val view = ProtocolViewMapper.mapScoreBreakdown(proto)
        entries.clear()
        components.clear()
        assertEquals(1, view.yakuEntries.size)
        assertEquals(1, view.paymentPlan!!.components.size)
        assertThrows(UnsupportedOperationException::class.java) {
            (view.yakuEntries as MutableList).add(
                com.majiang.client.api.YakuEntryView("X", "x", 0)
            )
        }
        assertTrue(view.yakuEntries.isNotEmpty())
    }
}

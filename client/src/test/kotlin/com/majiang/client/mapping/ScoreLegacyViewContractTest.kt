package com.majiang.client.mapping

import com.majiang.transport.api.ProtocolDoraBreakdown
import com.majiang.transport.api.ProtocolFuBreakdown
import com.majiang.transport.api.ProtocolFuLine
import com.majiang.transport.api.ProtocolScoreBreakdown
import com.majiang.transport.api.ProtocolScoreResult
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 client 侧兼容边界：
 * 当前线路没有稳定码字段时，客户端必须把既有字符串逐字保留，不能自行改写、
 * 归一化或因未知展示名丢弃数据。数值字段也必须原样进入视图模型。
 */
class ScoreLegacyViewContractTest {

    private fun protocolBreakdown(): ProtocolScoreBreakdown = ProtocolScoreBreakdown(
        yakuLines = listOf("立直" to 1, "未登记役种" to 2),
        yakumanCount = 0,
        dora = ProtocolDoraBreakdown(
            indicatorCount = 0,
            uraCount = 0,
            redCount = 0,
            totalHan = 0,
            indicatorTiles = emptyList(),
            uraTiles = emptyList(),
            redTiles = emptyList(),
            indicatorHits = emptyList(),
            uraHits = emptyList()
        ),
        fu = ProtocolFuBreakdown(
            lines = listOf(ProtocolFuLine("底符", 30)),
            subtotal = 30,
            roundedUpTo = 30,
            finalFu = 30,
            specialNote = null
        ),
        score = ProtocolScoreResult(
            fu = 30,
            han = 3,
            basePoints = 960,
            title = "3翻30符",
            isYakuman = false,
            yakumanMultiplier = 0,
            rawBasePoints = 960,
            capReason = null,
            formulaText = "30符 × 2^(2+3) = 960",
            rawBasePointsExact = "960"
        ),
        paymentLines = listOf("荣和：放铳者 支付 3900"),
        honbaAddition = 0,
        riichiAddition = 0,
        totalPoints = 3900,
        uraEligible = true,
        isDealer = false,
        isTsumo = false
    )

    @Test
    fun `legacy strings and numeric fields survive protocol to view mapping verbatim`() {
        val proto = protocolBreakdown()
        val view = ProtocolViewMapper.mapScoreBreakdown(proto)

        assertEquals(proto.yakuLines, view.yakuLines)
        assertEquals(proto.paymentLines, view.paymentLines)
        assertEquals("立直", view.yakuLines[0].first)
        assertEquals(1, view.yakuLines[0].second)
        assertEquals("未登记役种", view.yakuLines[1].first)
        assertEquals(2, view.yakuLines[1].second)

        assertEquals(proto.score.title, view.score.title)
        assertEquals(proto.score.capReason, view.score.capReason)
        assertEquals(proto.score.formulaText, view.score.formulaText)
        assertEquals(proto.score.fu, view.score.fu)
        assertEquals(proto.score.han, view.score.han)
        assertEquals(proto.score.basePoints, view.score.basePoints)
        assertEquals(proto.score.rawBasePoints, view.score.rawBasePoints)
        assertEquals(proto.score.rawBasePointsExact, view.score.rawBasePointsExact)
        assertEquals(proto.score.isYakuman, view.score.isYakuman)
        assertEquals(proto.score.yakumanMultiplier, view.score.yakumanMultiplier)

        assertEquals(proto.honbaAddition, view.honbaAddition)
        assertEquals(proto.riichiAddition, view.riichiAddition)
        assertEquals(proto.totalPoints, view.totalPoints)
        assertEquals(proto.uraEligible, view.uraEligible)
        assertEquals(proto.isDealer, view.isDealer)
        assertEquals(proto.isTsumo, view.isTsumo)

        // 客户端没有结构化字段时，不能凭展示名猜码或改写字串。
        assertFalse(view.yakuLines.any { it.first == "RIICHI" })
        assertTrue(view.yakuEntries.isEmpty())
        assertNull(view.paymentPlan)
        assertNull(view.score.capCode)
    }
}

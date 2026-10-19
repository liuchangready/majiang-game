package com.majiang.server.mapping

import com.google.gson.JsonObject
import com.majiang.model.PlayerHand
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.PayerRole
import com.majiang.rules.PaymentKind
import com.majiang.rules.ScoreCalculator
import com.majiang.rules.ScoreCap
import com.majiang.rules.ScoreTextFormatter
import com.majiang.rules.WinningScoreEvaluator
import com.majiang.rules.Yaku
import com.majiang.rules.YakuEvaluator
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolScoreCodes
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 engine → server → protocol 契约守卫。
 *
 * - engine 稳定码必须与 protocol 冻结码表逐项一致；
 * - 当前 v4 mapper 不得把 engine 新增的结构化字段带进线路；
 * - 既有字符串与数值字段仍逐项相等。
 */
class ScoreCodeContractTest {

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }

    private fun pinfu() = PlayerHand().apply {
        closedTiles.addAll(tiles(Suit.MAN, "23") + tiles(Suit.PIN, "234567") + tiles(Suit.SOU, "23488"))
    }

    private fun breakdown() = WinningScoreEvaluator().explain(
        hand = pinfu(),
        context = YakuEvaluator.WinContext(Tile(Suit.MAN, 1), isTsumo = false, isClosed = true, seatWind = 2, roundWind = 1),
        doraIndicators = emptyList(),
        honba = 2,
        riichiSticks = 1,
        loser = 3
    )!!

    @Test
    fun `engine yaku codes match the frozen protocol catalog`() {
        assertEquals(
            ProtocolScoreCodes.YAKU_CODES,
            Yaku.values().map { it.code }.toSet(),
            "engine Yaku.code and protocol YAKU_CODES must not drift"
        )
        assertEquals(Yaku.values().size, ProtocolScoreCodes.YAKU_CODES.size)
        for (yaku in Yaku.values()) {
            assertTrue(ProtocolScoreCodes.isKnownYakuCode(yaku.code), "missing protocol code for ${yaku.name}")
        }
    }

    @Test
    fun `engine cap payment and payer codes match the frozen protocol catalog`() {
        assertEquals(
            ProtocolScoreCodes.CAP_CODES,
            ScoreCap.values().map { it.code }.toSet() + ScoreTextFormatter.YAKUMAN_CODE
        )
        assertEquals(
            ProtocolScoreCodes.PAYMENT_KINDS,
            PaymentKind.values().map { it.code }.toSet()
        )
        assertEquals(
            ProtocolScoreCodes.PAYER_ROLES,
            PayerRole.values().map { it.code }.toSet()
        )
        assertTrue(ProtocolScoreCodes.isKnownCapCode(ScoreCalculator.INSTANCE.calculate(30, 5).capCode!!))
        assertTrue(ProtocolScoreCodes.isKnownCapCode(ScoreCalculator.INSTANCE.calculate(30, 0, 1).capCode!!))
    }

    @Test
    fun `v4 mapper emits exactly the legacy score field sets and no structured field`() {
        val domain = breakdown()
        val protoScore = EngineProtocolMapper.toProtocolScoreResult(domain.score)
        val scoreJson = ApiMessageCodec.gson.toJsonTree(protoScore).asJsonObject
        val legacyScoreKeys = setOf(
            "fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier",
            "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact"
        )
        assertTrue(
            legacyScoreKeys.containsAll(scoreJson.keySet()),
            "v4 score result must not grow even though engine has capCode: ${scoreJson.keySet()}"
        )
        // Gson 默认省略 null 的 capReason；未封顶 fixture 的其余全部既有字段必须存在。
        assertEquals(legacyScoreKeys - setOf("capReason"), scoreJson.keySet())
        assertFalse(scoreJson.has(ProtocolScoreCodes.FIELD_CAP_CODE))
        assertNull(domain.score.capCode)

        val protoBreakdown = EngineProtocolMapper.toProtocolScoreBreakdown(domain)
        val breakdownJson = ApiMessageCodec.gson.toJsonTree(protoBreakdown).asJsonObject
        assertEquals(
            setOf(
                "yakuLines", "yakumanCount", "dora", "fu", "score", "paymentLines",
                "honbaAddition", "riichiAddition", "totalPoints", "uraEligible", "isDealer", "isTsumo"
            ),
            breakdownJson.keySet(),
            "v4 breakdown must not grow even though engine has yakuEntries/paymentPlan"
        )
        assertFalse(breakdownJson.has(ProtocolScoreCodes.FIELD_YAKU_ENTRIES))
        assertFalse(breakdownJson.has(ProtocolScoreCodes.FIELD_PAYMENT_PLAN))
    }

    @Test
    fun `structured engine fields preserve all legacy mapped values`() {
        val domain = breakdown()
        val proto = EngineProtocolMapper.toProtocolScoreBreakdown(domain)

        assertEquals(domain.yakuLines, proto.yakuLines)
        assertEquals(domain.paymentLines, proto.paymentLines)
        assertEquals(domain.score.title, proto.score.title)
        assertEquals(domain.score.capReason, proto.score.capReason)
        assertEquals(domain.score.formulaText, proto.score.formulaText)
        assertEquals(domain.score.fu, proto.score.fu)
        assertEquals(domain.score.han, proto.score.han)
        assertEquals(domain.score.basePoints, proto.score.basePoints)
        assertEquals(domain.score.rawBasePoints, proto.score.rawBasePoints)
        assertEquals(domain.score.rawBasePointsExact, proto.score.rawBasePointsExact)
        assertEquals(domain.honbaAddition, proto.honbaAddition)
        assertEquals(domain.riichiAddition, proto.riichiAddition)
        assertEquals(domain.totalPoints, proto.totalPoints)
        assertEquals(domain.uraEligible, proto.uraEligible)
        assertEquals(domain.isDealer, proto.isDealer)
        assertEquals(domain.isTsumo, proto.isTsumo)

        // 新增结构化字段只在 engine 内部可用，且必须与旧字符串一致。
        assertEquals(domain.yakuEntries.map { it.displayName to it.han }, domain.yakuLines)
        assertEquals(PaymentKind.NON_DEALER_RON, domain.paymentPlan.kind)
        assertEquals(domain.paymentPlan.total, domain.totalPoints - domain.honbaAddition - domain.riichiAddition)
    }
}

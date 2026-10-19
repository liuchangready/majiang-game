package com.majiang.server.mapping

import com.majiang.access.AccessCapabilities
import com.majiang.client.mapping.ProtocolViewMapper
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
 * D-07 engine → server → protocol 契约守卫（v5 结构化算分线路）。
 *
 * - engine 稳定码必须与 protocol 冻结码表逐项一致；
 * - server mapper 必须在 v5 输出 `capCode` / `yakuEntries` / `paymentPlan`；
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
    fun `every cap and yakuman code survives engine server protocol and client mapping`() {
        val calculator = ScoreCalculator.INSTANCE
        val scores = listOf(
            calculator.calculate(30, 2),   // uncapped
            calculator.calculate(30, 5),   // MANGAN
            calculator.calculate(30, 6),   // HANEMAN
            calculator.calculate(30, 8),   // BAIMAN
            calculator.calculate(30, 11),  // SANBAIMAN
            calculator.calculate(30, 13),  // KAZOE_YAKUMAN
            calculator.calculate(30, 0, 1), // YAKUMAN
            calculator.calculate(25, 0, 2)  // double yakuman
        )
        for (score in scores) {
            val proto = EngineProtocolMapper.toProtocolScoreResult(score)
            val decoded = ApiMessageCodec.gson.fromJson(
                ApiMessageCodec.gson.toJson(proto),
                com.majiang.transport.api.ProtocolScoreResult::class.java
            )
            val view = ProtocolViewMapper.mapScoreResult(decoded)
            assertEquals(score.capCode, view.capCode, "cap code lost for ${score.title}")
            assertEquals(score.capReason, view.capReason)
            assertEquals(score.title, view.title)
            assertEquals(score.basePoints, view.basePoints)
            assertEquals(score.isYakuman, view.isYakuman)
            assertEquals(score.yakumanMultiplier, view.yakumanMultiplier)
        }
    }

    @Test
    fun `server capabilities advertise the frozen structured score detail version`() {
        val protocol = EngineProtocolMapper.toProtocolCapabilities(
            AccessCapabilities(protocolVersion = 5, remoteTransport = true)
        )
        assertEquals(5, protocol.protocolVersion)
        assertEquals(ProtocolScoreCodes.CONTRACT_VERSION, protocol.scoreDetailVersion)
        assertEquals(1, protocol.scoreDetailVersion)
    }

    @Test
    fun `v5 mapper emits exactly the structured score field sets`() {
        val domain = breakdown()
        val protoScore = EngineProtocolMapper.toProtocolScoreResult(domain.score)
        val scoreJson = ApiMessageCodec.gson.toJsonTree(protoScore).asJsonObject
        val v5ScoreKeys = setOf(
            "fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier",
            "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact", "capCode"
        )
        // 未封顶 fixture 的 capCode/capReason 为 null，Gson 省略 null；其余字段必须齐全。
        assertEquals(v5ScoreKeys - setOf("capReason", "capCode"), scoreJson.keySet())
        assertNull(domain.score.capCode)

        val capped = ScoreCalculator.INSTANCE.calculate(30, 5)
        val cappedProto = EngineProtocolMapper.toProtocolScoreResult(capped)
        assertEquals("MANGAN", cappedProto.capCode)

        val protoBreakdown = EngineProtocolMapper.toProtocolScoreBreakdown(domain)
        val breakdownJson = ApiMessageCodec.gson.toJsonTree(protoBreakdown).asJsonObject
        assertEquals(
            setOf(
                "yakuLines", "yakumanCount", "dora", "fu", "score", "paymentLines",
                "honbaAddition", "riichiAddition", "totalPoints", "uraEligible",
                "isDealer", "isTsumo", "yakuEntries", "paymentPlan"
            ),
            breakdownJson.keySet(),
            "v5 breakdown must carry the frozen structured fields"
        )
        assertEquals(
            domain.yakuEntries.map { Triple(it.code, it.displayName, it.han) },
            protoBreakdown.yakuEntries.map { Triple(it.code, it.displayName, it.han) }
        )
        assertEquals(domain.paymentPlan.kind.code, protoBreakdown.paymentPlan!!.kind)
        assertEquals(domain.paymentPlan.total, protoBreakdown.paymentPlan!!.total)
        assertEquals(
            domain.paymentPlan.components.map { Triple(it.payerRole.code, it.amount, it.count) },
            protoBreakdown.paymentPlan!!.components.map { Triple(it.payerRole, it.amount, it.count) }
        )
        assertEquals(
            domain.paymentPlan.components.sumOf { it.amount * it.count },
            protoBreakdown.paymentPlan!!.components.sumOf { it.amount * it.count }
        )
        assertEquals(domain.paymentLines, protoBreakdown.paymentLines)
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

        // 新增结构化字段必须与旧字符串一致。
        assertEquals(domain.yakuEntries.map { it.displayName to it.han }, domain.yakuLines)
        assertEquals(
            domain.yakuEntries.map { Triple(it.code, it.displayName, it.han) },
            proto.yakuEntries.map { Triple(it.code, it.displayName, it.han) }
        )
        assertEquals(PaymentKind.NON_DEALER_RON, domain.paymentPlan.kind)
        assertEquals(domain.paymentPlan.total, domain.totalPoints - domain.honbaAddition - domain.riichiAddition)
        assertEquals(domain.paymentPlan.kind.code, proto.paymentPlan!!.kind)
        assertEquals(domain.paymentPlan.total, proto.paymentPlan!!.total)
        assertEquals(
            domain.paymentPlan.total,
            proto.paymentPlan!!.components.sumOf { it.amount * it.count }
        )
    }
}

package com.majiang.transport.api

import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.majiang.server.api.ApiProtocol
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 线路边界验证（v5 升版后的有效规则）。
 *
 * 历史 v4 fixture 只用于证明“缺省结构化字段可安全回退旧文本”，不再代表受支持的
 * 业务版本。真正的兼容边界是：`ApiProtocol.PROTOCOL_VERSION = 5`、
 * `TcpWire.BUSINESS_VERSION = 2`；旧 v4 HELLO/WELCOME 由双端握手硬拒绝
 * （VERSION_MISMATCH），不会收到含新字段的快照。
 *
 * 同时锁定：未知字段始终严格拒绝；只有稳定码“取值”是向前兼容扩展点。
 */
class ScoreWireBoundaryTest {

    private fun legacyScore() = ProtocolScoreResult(
        fu = 30,
        han = 5,
        basePoints = 2000,
        title = "満貫",
        isYakuman = false,
        yakumanMultiplier = 0,
        rawBasePoints = 3840,
        capReason = "満貫封頂",
        formulaText = "30符 × 2^(2+5) = 3840 → 満貫封頂 2000",
        rawBasePointsExact = "3840"
    )

    private fun legacyBreakdown(): ProtocolScoreBreakdown {
        val tile = ProtocolTile.fromTileId(0)
        val hit = ProtocolDoraIndicatorHit(tile, tile, listOf(tile))
        return ProtocolScoreBreakdown(
            yakuLines = listOf("平和" to 1, "断幺九" to 1),
            yakumanCount = 0,
            dora = ProtocolDoraBreakdown(
                indicatorCount = 1,
                uraCount = 0,
                redCount = 0,
                totalHan = 1,
                indicatorTiles = listOf(tile),
                uraTiles = emptyList(),
                redTiles = emptyList(),
                indicatorHits = listOf(hit),
                uraHits = emptyList()
            ),
            fu = ProtocolFuBreakdown(
                lines = listOf(ProtocolFuLine("底符", 30)),
                subtotal = 30,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = null
            ),
            score = legacyScore(),
            paymentLines = listOf("荣和：P1 支付 8000"),
            honbaAddition = 300,
            riichiAddition = 1000,
            totalPoints = 9300,
            uraEligible = true,
            isDealer = false,
            isTsumo = false
        )
    }

    private fun jsonOf(value: Any): JsonObject = ApiMessageCodec.gson.toJsonTree(value).asJsonObject

    private fun assertUnknownField(type: Class<*>, json: JsonObject, field: String) {
        json.addProperty(field, "x")
        val ex = assertThrows(JsonParseException::class.java) {
            ApiMessageCodec.gson.fromJson(json, type)
        }
        assertTrue(
            ex.message.orEmpty().contains("Unknown field"),
            "expected strict unknown-field rejection for $field, got: ${ex.message}"
        )
    }

    @Test
    fun `historical v4 score payload decodes with structured fields absent and legacy values intact`() {
        val score = jsonOf(legacyScore())
        val v4ScoreKeys = setOf(
            "fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier",
            "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact"
        )
        assertEquals(v4ScoreKeys, score.keySet())
        val decodedScore = ApiMessageCodec.gson.fromJson(score, ProtocolScoreResult::class.java)
        assertNull(decodedScore.capCode)
        assertEquals(2000, decodedScore.basePoints)
        assertEquals("満貫封頂", decodedScore.capReason)

        val breakdown = jsonOf(legacyBreakdown())
        val v4BreakdownKeys = setOf(
            "yakuLines", "yakumanCount", "dora", "fu", "score", "paymentLines",
            "honbaAddition", "riichiAddition", "totalPoints", "uraEligible", "isDealer", "isTsumo"
        )
        breakdown.remove(ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
        breakdown.remove(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
        assertEquals(v4BreakdownKeys, breakdown.keySet())
        val decoded = ApiMessageCodec.gson.fromJson(breakdown, ProtocolScoreBreakdown::class.java)
        assertTrue(decoded.yakuEntries.isEmpty(), "v4 history has no structured yaku entries")
        assertNull(decoded.paymentPlan, "v4 history has no structured payment plan")
        assertEquals(listOf("平和" to 1, "断幺九" to 1), decoded.yakuLines)
        assertEquals(listOf("荣和：P1 支付 8000"), decoded.paymentLines)
        assertEquals(9300, decoded.totalPoints)

        val legacyCaps = JsonObject().apply {
            addProperty("protocolVersion", 4)
            addProperty("remoteTransport", true)
        }
        val caps = ApiMessageCodec.gson.fromJson(legacyCaps, ProtocolAccessCapabilities::class.java)
        assertEquals(4, caps.protocolVersion)
        assertEquals(0, caps.scoreDetailVersion)
    }

    @Test
    fun `v5 payload roundtrips every structured score field in the frozen schema`() {
        val tile = ProtocolTile.fromTileId(0)
        val breakdown = ProtocolScoreBreakdown(
            yakuLines = listOf("平和" to 1, "断幺九" to 1),
            yakumanCount = 0,
            dora = ProtocolDoraBreakdown(
                1, 0, 0, 1, listOf(tile), emptyList(), emptyList(),
                listOf(ProtocolDoraIndicatorHit(tile, tile, listOf(tile))), emptyList()
            ),
            fu = ProtocolFuBreakdown(listOf(ProtocolFuLine("底符", 30)), 30, 30, 30, null),
            score = legacyScore().copy(capCode = ProtocolScoreCodes.CAP_MANGAN),
            paymentLines = listOf("荣和：P1 支付 8000"),
            honbaAddition = 300,
            riichiAddition = 1000,
            totalPoints = 9300,
            uraEligible = true,
            isDealer = false,
            isTsumo = false,
            yakuEntries = listOf(
                ProtocolYakuEntry("PINFU", "平和", 1),
                ProtocolYakuEntry("TANYAO", "断幺九", 1)
            ),
            paymentPlan = ProtocolPaymentPlan(
                kind = "NON_DEALER_RON",
                components = listOf(ProtocolPaymentComponent("DISCARDER", 8000, 1)),
                total = 8000
            )
        )

        val encoded = ApiMessageCodec.gson.toJson(breakdown)
        val decoded = ApiMessageCodec.gson.fromJson(encoded, ProtocolScoreBreakdown::class.java)
        assertEquals(breakdown, decoded)
        assertEquals("MANGAN", decoded.score.capCode)
        assertEquals(listOf("PINFU", "TANYAO"), decoded.yakuEntries.map { it.code })
        assertEquals("NON_DEALER_RON", decoded.paymentPlan!!.kind)
        assertEquals(8000, decoded.paymentPlan!!.total)
        assertTrue(decoded.yakuEntries.isNotEmpty())

        val caps = ProtocolAccessCapabilities(
            protocolVersion = ApiProtocol.PROTOCOL_VERSION,
            scoreDetailVersion = ProtocolScoreCodes.CONTRACT_VERSION
        )
        val capsJson = jsonOf(caps)
        assertTrue(capsJson.has(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION))
        assertEquals(1, capsJson.get(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION).asInt)
    }

    @Test
    fun `unknown fields outside the frozen v5 schema are still rejected`() {
        val score = jsonOf(legacyScore().copy(capCode = "MANGAN"))
        assertEquals(
            setOf(
                "fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier",
                "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact", "capCode"
            ),
            score.keySet()
        )
        assertUnknownField(ProtocolScoreResult::class.java, score.deepCopy(), "futureScoreField")

        val breakdown = jsonOf(
            legacyBreakdown().copy(
                yakuEntries = listOf(ProtocolYakuEntry("PINFU", "平和", 1)),
                paymentLines = listOf("荣和：P1 支付 8000"),
                paymentPlan = ProtocolPaymentPlan(
                    "NON_DEALER_RON",
                    listOf(ProtocolPaymentComponent("DISCARDER", 8000, 1)),
                    8000
                )
            )
        )
        assertUnknownField(ProtocolScoreBreakdown::class.java, breakdown.deepCopy(), "scoreDetail")

        val yakuEntry = JsonObject().apply {
            addProperty("code", "PINFU")
            addProperty("displayName", "平和")
            addProperty("han", 1)
        }
        assertUnknownField(ProtocolYakuEntry::class.java, yakuEntry, "futureYakuField")

        val component = JsonObject().apply {
            addProperty("payerRole", "DISCARDER")
            addProperty("amount", 2000)
            addProperty("count", 1)
        }
        assertUnknownField(ProtocolPaymentComponent::class.java, component, "futureRoleField")

        val plan = JsonObject().apply {
            addProperty("kind", "NON_DEALER_RON")
            add("components", com.google.gson.JsonArray().apply { add(component) })
            addProperty("total", 2000)
        }
        assertUnknownField(ProtocolPaymentPlan::class.java, plan, "futurePlanField")

        val caps = jsonOf(ProtocolAccessCapabilities(protocolVersion = 5, scoreDetailVersion = 1))
        assertUnknownField(ProtocolAccessCapabilities::class.java, caps.deepCopy(), "futureCapability")

        val response = jsonOf(ApiResponse(capabilities = ProtocolAccessCapabilities(protocolVersion = 5)))
        assertUnknownField(ApiResponse::class.java, response.deepCopy(), "scoreDetail")

        val ex = assertThrows(com.majiang.transport.tcp.TcpProtocolException::class.java) {
            TcpMessageCodec.decodeHello(
                """{"wireVersion":1,"businessVersion":3,"futureScoreDetailVersion":1}"""
            )
        }
        val text = (ex.message.orEmpty() + " " + (ex.cause?.message.orEmpty()))
        assertTrue(text.contains("Unknown field"), text)
    }

    @Test
    fun `v6 business version satisfies the structured-score gate while MJ-TCP 1 framing is unchanged`() {
        assertEquals(6, ApiProtocol.PROTOCOL_VERSION)
        assertTrue(
            ApiProtocol.PROTOCOL_VERSION >= ProtocolScoreCodes.STRUCTURED_SCORE_MIN_PROTOCOL_VERSION,
            "structured score fields require protocol version >= 5"
        )
        assertEquals(3, TcpWire.BUSINESS_VERSION)
        assertEquals(1, TcpWire.WIRE_VERSION)
        assertEquals(12, TcpWire.HEADER_SIZE)
        assertEquals("MJTC", TcpWire.MAGIC_ASCII)
        assertEquals(1, ProtocolScoreCodes.CONTRACT_VERSION)
        assertTrue(ProtocolScoreCodes.isKnownYakuCode("RIICHI"))
        assertTrue(ProtocolScoreCodes.isKnownCapCode("MANGAN"))
        assertTrue(ProtocolScoreCodes.isKnownPaymentKind("NON_DEALER_RON"))
        assertTrue(ProtocolScoreCodes.isKnownPayerRole("DISCARDER"))
        assertFalse(ProtocolScoreCodes.isKnownYakuCode("NO_SUCH_YAKU"))
        assertFalse(ProtocolScoreCodes.isKnownCapCode("NO_SUCH_CAP"))
        assertFalse(ProtocolScoreCodes.isKnownPaymentKind("NO_SUCH_KIND"))
        assertFalse(ProtocolScoreCodes.isKnownPayerRole("NO_SUCH_ROLE"))
        assertEquals("UNKNOWN_YAKU", ProtocolScoreCodes.UNKNOWN_YAKU_CODE)
    }
}

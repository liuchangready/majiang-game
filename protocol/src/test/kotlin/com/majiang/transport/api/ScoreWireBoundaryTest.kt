package com.majiang.transport.api

import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.majiang.server.api.ApiProtocol
import com.majiang.transport.tcp.TcpMessageCodec
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 线路阻断证据。
 *
 * 旧端对算分 DTO、capabilities、ApiResponse 与 TcpHello 都执行严格未知字段拒绝；
 * 因此把稳定码/支付结构直接加进现有 MJ-TCP/1 字段会让旧客户端在解析整条快照时
 * 直接失败。本测试锁定这一事实，并断言当前业务协议版本仍低于结构化算分字段的
 * 最低版本，防止在未独立升级协议的情况下悄悄改变 v4 线路。
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
    fun `v4 score result and breakdown reject every proposed structured field`() {
        val score = jsonOf(legacyScore())
        assertEquals(
            setOf(
                "fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier",
                "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact"
            ),
            score.keySet(),
            "v4 ProtocolScoreResult must stay exactly the legacy field set"
        )
        assertUnknownField(ProtocolScoreResult::class.java, score.deepCopy(), ProtocolScoreCodes.FIELD_CAP_CODE)

        val breakdown = jsonOf(legacyBreakdown())
        assertEquals(
            setOf(
                "yakuLines", "yakumanCount", "dora", "fu", "score", "paymentLines",
                "honbaAddition", "riichiAddition", "totalPoints", "uraEligible", "isDealer", "isTsumo"
            ),
            breakdown.keySet(),
            "v4 ProtocolScoreBreakdown must stay exactly the legacy field set"
        )
        assertUnknownField(ProtocolScoreBreakdown::class.java, breakdown.deepCopy(), ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
        assertUnknownField(ProtocolScoreBreakdown::class.java, breakdown.deepCopy(), ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
    }

    @Test
    fun `v4 capabilities response and ApiResponse envelope cannot carry a score capability or payload`() {
        val caps = jsonOf(ProtocolAccessCapabilities(protocolVersion = ApiProtocol.PROTOCOL_VERSION))
        assertUnknownField(
            ProtocolAccessCapabilities::class.java,
            caps.deepCopy(),
            ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION
        )

        val response = jsonOf(ApiResponse(capabilities = ProtocolAccessCapabilities(protocolVersion = ApiProtocol.PROTOCOL_VERSION)))
        assertUnknownField(ApiResponse::class.java, response.deepCopy(), "scoreDetail")
    }

    @Test
    fun `hello handshake has no score capability channel and rejects an invented field`() {
        val ex = assertThrows(com.majiang.transport.tcp.TcpProtocolException::class.java) {
            TcpMessageCodec.decodeHello(
                """{"wireVersion":1,"businessVersion":1,"${ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION}":1}"""
            )
        }
        val text = (ex.message.orEmpty() + " " + (ex.cause?.message.orEmpty()))
        assertTrue(text.contains("Unknown field"), text)
    }

    @Test
    fun `current protocol version is below the structured-score contract gate`() {
        assertTrue(
            ApiProtocol.PROTOCOL_VERSION < ProtocolScoreCodes.STRUCTURED_SCORE_MIN_PROTOCOL_VERSION,
            "structured score fields require a separate protocol version bump"
        )
        assertEquals(1, ProtocolScoreCodes.CONTRACT_VERSION)
        assertTrue(ProtocolScoreCodes.isKnownYakuCode("RIICHI"))
        assertTrue(ProtocolScoreCodes.isKnownCapCode("MANGAN"))
        assertFalse(ProtocolScoreCodes.isKnownYakuCode("NO_SUCH_YAKU"))
        assertFalse(ProtocolScoreCodes.isKnownCapCode("NO_SUCH_CAP"))
        assertEquals("UNKNOWN_YAKU", ProtocolScoreCodes.UNKNOWN_YAKU_CODE)
    }
}

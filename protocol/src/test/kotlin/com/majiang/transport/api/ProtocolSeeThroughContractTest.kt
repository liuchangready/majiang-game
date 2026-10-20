package com.majiang.transport.api

import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.FakeServerConnection
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** v8 统一码牌 + 看破线路契约：严格布尔/单目标、费用、私有准备视图与唯一幂等身份。 */
class ProtocolSeeThroughContractTest {

    private fun selection(seeThrough: Boolean = true) = ProtocolShuffleSelection(
        target = ProtocolShuffleTarget.KOKUSHI,
        seeThrough = seeThrough
    )

    private fun plan(requestId: String = "see-1") = ProtocolShufflePlan(
        requestId = requestId,
        roundId = "round-1",
        controlEpoch = 3,
        selection = selection()
    )

    private fun request() = ApiRequest(
        api = ApiTags.SUBMIT_SHUFFLE_PLAN,
        requestId = "see-1",
        shufflePlan = plan()
    )

    private fun json(request: ApiRequest) =
        JsonParser.parseString(ApiMessageCodec.encodeRequest(request)).asJsonObject

    @Test
    fun `selection cost is ten for see through alone and caps at forty`() {
        assertEquals(10, ProtocolShuffleSelection(seeThrough = true).cost())
        assertTrue(ProtocolShuffleSelection(seeThrough = true).isActive)
        assertEquals(40, selection().cost())
        assertEquals(40, ProtocolShuffleSelection.SEE_THROUGH_COST + ProtocolShuffleSelection.TARGET_COST)
        assertEquals("see-1", plan().copy(selection = selection(false)).requestId)
    }

    @Test
    fun `submit shuffle plan round trips with strict boolean and one envelope identity`() {
        val encoded = json(request())
        assertEquals("see-1", encoded.get("requestId").asString)
        assertEquals("see-1", encoded.getAsJsonObject("shufflePlan").get("requestId").asString)
        assertTrue(encoded.getAsJsonObject("shufflePlan").getAsJsonObject("selection").get("seeThrough").asBoolean)
        assertEquals(request(), ApiMessageCodec.decodeRequest(encoded.toString()))

        for (bad in listOf("null", "\"true\"", "1", "0")) {
            val altered = json(request())
            altered.getAsJsonObject("shufflePlan").getAsJsonObject("selection")
                .add("seeThrough", JsonParser.parseString(bad))
            assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(altered.toString()) }
        }
        val missing = json(request())
        missing.getAsJsonObject("shufflePlan").getAsJsonObject("selection").remove("seeThrough")
        assertFalse(
            ApiMessageCodec.decodeRequest(missing.toString()).shufflePlan!!.selection.seeThrough,
            "Legacy local constructors may omit the field; wire default is false"
        )
        val unknown = json(request())
        unknown.getAsJsonObject("shufflePlan").getAsJsonObject("selection").addProperty("forged", true)
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(unknown.toString()) }

        for (legacyField in listOf("initialTargets", "drawTargets")) {
            val legacy = json(request())
            legacy.getAsJsonObject("shufflePlan").getAsJsonObject("selection")
                .add(legacyField, JsonParser.parseString("[\"KOKUSHI\"]"))
            assertThrows<JsonParseException>(legacyField) {
                ApiMessageCodec.decodeRequest(legacy.toString())
            }
        }
    }

    @Test
    fun `submit shuffle plan rejects a second payload or a second identity`() {
        val mismatched = json(request())
        mismatched.addProperty("requestId", "other")
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(mismatched.toString()) }

        val envelopeRoundMismatch = json(request())
        envelopeRoundMismatch.addProperty("roundId", "other-round")
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(envelopeRoundMismatch.toString()) }

        val mixed = json(request())
        mixed.add(
            "choice",
            JsonParser.parseString(
                """{"requestId":"see-1","roundId":"round-1","decisionId":"d-1","controlEpoch":3,"optionId":"o-1"}"""
            )
        )
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(mixed.toString()) }

        val wrongApi = json(request())
        wrongApi.addProperty("api", ApiTags.SNAPSHOT)
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(wrongApi.toString()) }

        val missingPayload = json(request())
        missingPayload.remove("shufflePlan")
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(missingPayload.toString()) }
    }

    @Test
    fun `shuffle config and capabilities carry see through with legacy defaults off`() {
        val config = ProtocolShuffleConfig(enabled = true, seeThroughEnabled = true)
        val match = ProtocolMatchConfig(
            cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 100 }, mapOf("SEE_THROUGH" to 10)),
            shuffleConfig = config
        )
        assertEquals(config, ApiMessageCodec.decodeRequest(
            ApiMessageCodec.encodeRequest(
                ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "create", config = match)
            )
        ).config!!.shuffleConfig)
        assertFalse(ProtocolShuffleConfig().seeThroughEnabled)
        assertThrows<IllegalArgumentException> {
            ProtocolShuffleConfig(enabled = false, seeThroughEnabled = true)
        }
        assertThrows<IllegalArgumentException> {
            ProtocolMatchConfig(shuffleConfig = config)
        }
        assertThrows<IllegalArgumentException> {
            ProtocolMatchConfig(
                cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 100 }),
                shuffleConfig = config
            )
        }

        val caps = ProtocolAccessCapabilities(
            protocolVersion = ApiProtocol.PROTOCOL_VERSION,
            seeThroughSupported = true
        )
        val wire = ApiMessageCodec.gson.toJsonTree(caps).asJsonObject
        assertTrue(wire.get("seeThroughSupported").asBoolean)
        assertTrue(ApiMessageCodec.gson.fromJson(wire, ProtocolAccessCapabilities::class.java).seeThroughSupported)
        wire.remove("seeThroughSupported")
        assertFalse(ApiMessageCodec.gson.fromJson(wire, ProtocolAccessCapabilities::class.java).seeThroughSupported)
        assertEquals(8, ApiProtocol.PROTOCOL_VERSION)
        assertEquals(1, com.majiang.transport.tcp.TcpWire.WIRE_VERSION)
        assertEquals(8, com.majiang.transport.tcp.TcpWire.BUSINESS_VERSION)
    }

    @Test
    fun `private preparation view round trips and rejects contradictory see through state`() {
        val prep = ProtocolShufflePreparation(
            roundId = "round-1",
            status = ProtocolShufflePreparationStatus.SELECTING,
            opensAtMillis = 1_000L,
            deadlineMillis = 11_000L,
            serverNowMillis = 2_000L,
            confirmed = true,
            selection = selection(),
            reservedPoints = 40,
            availablePoints = 60,
            seeThroughEnabled = true,
            seeThroughCost = 10
        )
        assertThrows<IllegalArgumentException> {
            prep.copy(seeThroughEnabled = false)
        }
        assertThrows<IllegalArgumentException> {
            prep.copy(seeThroughCost = 9)
        }

        val base = FakeServerConnection.createDefaultSnapshot()
        val snapshot = base.copy(preparation = prep)
        val response = ApiMessageCodec.decodeResponse(
            ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot))
        )
        assertEquals(prep, response.snapshot!!.preparation)
        assertEquals(40, response.snapshot!!.preparation!!.selection!!.cost())

        val push = ApiMessageCodec.decodePushEnvelope(
            ApiMessageCodec.encodePush(
                ProtocolPlayerUpdate(snapshot, snapshot.cursor, emptyList()),
                isFullSnapshot = true
            )
        )
        assertEquals(prep, push.update.snapshot.preparation)
    }

    @Test
    fun `private preparation rejects explicit null booleans and leaked owner fields`() {
        val prep = ProtocolShufflePreparation(
            roundId = "round-1",
            status = ProtocolShufflePreparationStatus.SELECTING,
            opensAtMillis = 1_000L,
            deadlineMillis = 11_000L,
            serverNowMillis = 2_000L,
            confirmed = false,
            selection = null,
            reservedPoints = 0,
            availablePoints = 100,
            seeThroughEnabled = true,
            seeThroughCost = 10
        )
        val base = FakeServerConnection.createDefaultSnapshot()
        val wire = JsonParser.parseString(
            ApiMessageCodec.encodeResponse(ApiResponse(snapshot = base.copy(preparation = prep)))
        ).asJsonObject
        val prepJson = wire.getAsJsonObject("snapshot").getAsJsonObject("preparation")

        for (field in listOf("seeThroughEnabled", "seeThroughCost")) {
            val altered = prepJson.deepCopy()
            altered.add(field, JsonParser.parseString("null"))
            assertThrows<JsonParseException> {
                ApiMessageCodec.decodeResponse(
                    wire.deepCopy().apply {
                        getAsJsonObject("snapshot").add("preparation", altered)
                    }.toString()
                )
            }
        }
        for (forged in listOf("ownerSeat", "targetSeat", "opponentSeats", "seeThroughSeats")) {
            val altered = prepJson.deepCopy()
            altered.addProperty(forged, 0)
            assertThrows<JsonParseException> {
                ApiMessageCodec.decodeResponse(
                    wire.deepCopy().apply {
                        getAsJsonObject("snapshot").add("preparation", altered)
                    }.toString()
                )
            }
        }
        for (field in listOf("seeThroughEnabled", "seeThroughCost")) {
            val missing = prepJson.deepCopy()
            missing.remove(field)
            val decoded = ApiMessageCodec.decodeResponse(
                wire.deepCopy().apply {
                    getAsJsonObject("snapshot").add("preparation", missing)
                }.toString()
            )
            assertEquals(field != "seeThroughEnabled", decoded.snapshot!!.preparation!!.seeThroughEnabled)
            assertEquals(10, decoded.snapshot!!.preparation!!.seeThroughCost)
        }
    }

    private fun JsonObject.deepCopy(): JsonObject = JsonParser.parseString(toString()).asJsonObject
}

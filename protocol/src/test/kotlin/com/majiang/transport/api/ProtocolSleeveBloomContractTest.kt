package com.majiang.transport.api

import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.majiang.server.api.FakeServerConnection
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Codex-owned E2 wire contracts; E2 production is intentionally not implemented in E1. */
class ProtocolSleeveBloomContractTest {
    private fun activate() = ApiRequest(api = ApiTags.SUBMIT_SLEEVE_BLOOM, requestId = "activate",
        sleeveBloom = ProtocolSleeveBloomRequest("activate", "session-fake", "round-1", "decision-0", 1))
    private fun choose() = ApiRequest(api = ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE, requestId = "choose",
        sleeveBloomChoice = ProtocolSleeveBloomChoiceRequest("choose", "session-fake", "round-1", "decision-1", 1,
            "offer", ProtocolSleeveBloomKeep.SECOND))
    private fun json(request: ApiRequest) = JsonParser.parseString(ApiMessageCodec.encodeRequest(request)).asJsonObject
    private fun pending(): ProtocolPlayerSnapshot {
        val base = FakeServerConnection.createDefaultSnapshot()
        val offer = ProtocolSleeveBloomOfferView("offer", ProtocolTile(1), ProtocolTile(135),
            ProtocolSleeveBloomDrawKind.NORMAL, 20_000, base.decision.decisionId, base.decision.controlEpoch)
        val view = ProtocolSleeveBloomView(false, "SLEEVE_BLOOM_ALREADY_PENDING", 10,
            ProtocolSleeveBloomDrawKind.NORMAL, base.decision.decisionId, base.decision.controlEpoch, 1, offer)
        return base.copy(view = base.view.copy(phase = ProtocolGamePhase.CHEAT_WINDOW,
            myDrawnTile = ProtocolTile(1)), sleeveBloom = view)
    }

    @Test fun `both operations round trip actual envelopes with distinct known tags`() {
        for (request in listOf(activate(), choose())) {
            assertTrue(request.api in ApiTags.KNOWN)
            assertEquals(request, ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(request)))
        }
    }

    @Test fun `payload required exclusive and bound to envelope identity`() {
        for ((request, field) in listOf(activate() to "sleeveBloom", choose() to "sleeveBloomChoice")) {
            val changes = listOf<(JsonObject) -> Unit>(
                { it.remove(field) }, { it.addProperty("requestId", "other") },
                { it.addProperty("api", ApiTags.SNAPSHOT) },
                { it.add("tileMark", JsonParser.parseString("""{"requestId":"${request.requestId}","sessionId":"session-fake","roundId":"round-1","decisionId":"decision-0","controlEpoch":1,"tileIds":[0,1,2,3]}""")) },
                { it.add("choice", JsonParser.parseString("""{"requestId":"${request.requestId}","roundId":"round-1","decisionId":"decision-0","controlEpoch":1,"optionId":"opt-1"}""")) }
            )
            for (change in changes) assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(json(request).also(change).toString()) }
        }
        val mixed = json(activate())
        mixed.add("sleeveBloomChoice", json(choose()).get("sleeveBloomChoice"))
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(mixed.toString()) }
    }

    @Test fun `strict request fields reject forged authority and malformed control epoch`() {
        for ((request, field) in listOf(activate() to "sleeveBloom", choose() to "sleeveBloomChoice")) {
            for (forged in listOf("seat", "cost", "secondTile", "deadlineMillis")) {
                val altered = json(request)
                altered.getAsJsonObject(field).addProperty(forged, 0)
                assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(altered.toString()) }
            }
            for (epoch in listOf("-1", "1.2", "\"1\"", "9223372036854775808", "null")) {
                val altered = json(request)
                altered.getAsJsonObject(field).add("controlEpoch", JsonParser.parseString(epoch))
                assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(altered.toString()) }
            }
        }
        for (keep in listOf("THIRD", "first", "", "null")) {
            val altered = json(choose())
            altered.getAsJsonObject("sleeveBloomChoice").addProperty("keep", keep)
            assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(altered.toString()) }
        }
    }

    @Test fun `private pending offer survives full snapshot response and push`() {
        val snapshot = pending()
        assertEquals(snapshot, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot))).snapshot)
        val push = ApiMessageCodec.decodePushEnvelope(ApiMessageCodec.encodePush(
            ProtocolPlayerUpdate(snapshot, snapshot.cursor, emptyList()), isFullSnapshot = true))
        assertTrue(push.isFullSnapshot)
        assertEquals(snapshot.sleeveBloom, push.update.snapshot.sleeveBloom)
    }

    @Test fun `pending offer rejects inconsistent phase owner first tile or decision identity`() {
        val original = pending()
        assertThrows<IllegalArgumentException> { original.copy(view = original.view.copy(phase = ProtocolGamePhase.DISCARD)) }
        assertThrows<IllegalArgumentException> { original.copy(view = original.view.copy(currentTurnPlayer = 1)) }
        assertThrows<IllegalArgumentException> { original.copy(view = original.view.copy(myDrawnTile = ProtocolTile(2))) }
        val mutations = listOf<(JsonObject) -> Unit>(
            { it.addProperty("decisionId", "stale") }, { it.addProperty("controlEpoch", 0) },
            { it.addProperty("drawKind", "DEAL_EXTRA") }, { it.addProperty("deadlineMillis", -1) }
        )
        for (change in mutations) {
            val wire = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = original))).asJsonObject
            change(wire.getAsJsonObject("snapshot").getAsJsonObject("sleeveBloom").getAsJsonObject("pendingOffer"))
            assertThrows<JsonParseException> { ApiMessageCodec.decodeResponse(wire.toString()) }
        }
    }

    @Test fun `wire configuration cannot enable bloom without economy and exact declared cost`() {
        for (cost in listOf<Int?>(null, 0, 9, 11)) {
            assertThrows<IllegalArgumentException> {
                ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 },
                    cost?.let { mapOf("SLEEVE_BLOOM" to it) } ?: emptyMap()), sleeveBloomConfig = ProtocolSleeveBloomConfig(true))
            }
        }
        assertThrows<IllegalArgumentException> { ProtocolMatchConfig(sleeveBloomConfig = ProtocolSleeveBloomConfig(true)) }
        val valid = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("SLEEVE_BLOOM" to 10)),
            sleeveBloomConfig = ProtocolSleeveBloomConfig(true))
        val wire = ApiMessageCodec.gson.toJsonTree(valid).asJsonObject
        wire.getAsJsonObject("cheatEconomy").getAsJsonObject("costs").remove("SLEEVE_BLOOM")
        assertThrows<JsonParseException> { ApiMessageCodec.gson.fromJson(wire, ProtocolMatchConfig::class.java) }
    }

    @Test fun `missing old fields mean disabled unsupported and no private offer`() {
        assertFalse(ApiMessageCodec.gson.fromJson("{}", ProtocolSleeveBloomConfig::class.java).enabled)
        val caps = ApiMessageCodec.gson.toJsonTree(ProtocolAccessCapabilities()).asJsonObject
        caps.remove("sleeveBloomSupported")
        assertFalse(ApiMessageCodec.gson.fromJson(caps, ProtocolAccessCapabilities::class.java).sleeveBloomSupported)
        val config = ApiMessageCodec.gson.toJsonTree(ProtocolMatchConfig()).asJsonObject
        config.remove("sleeveBloomConfig")
        assertFalse(ApiMessageCodec.gson.fromJson(config, ProtocolMatchConfig::class.java).sleeveBloomConfig.enabled)
        val wire = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = pending()))).asJsonObject
        wire.getAsJsonObject("snapshot").remove("sleeveBloom")
        assertNull(ApiMessageCodec.decodeResponse(wire.toString()).snapshot!!.sleeveBloom)
    }

    @Test fun `off-turn activation and choice round trip with exclusive payloads`() {
        val offTurn = ApiRequest(
            api = ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN,
            requestId = "off",
            sleeveBloomOffTurn = ProtocolSleeveBloomOffTurnRequest("off", "session-fake", "round-1", "window-0", 1)
        )
        val choose = ApiRequest(
            api = ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE,
            requestId = "pick",
            sleeveBloomOffTurnChoice = ProtocolSleeveBloomOffTurnChoiceRequest(
                "pick", "session-fake", "round-1", 1, "offer-off", ProtocolSleeveBloomKeep.SECOND
            )
        )
        for (request in listOf(offTurn, choose)) {
            assertTrue(request.api in ApiTags.KNOWN)
            assertEquals(request, ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(request)))
        }
        val mixed = json(offTurn)
        mixed.add("sleeveBloomChoice", json(choose()).get("sleeveBloomChoice"))
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(mixed.toString()) }
        val wrongWindow = json(offTurn)
        wrongWindow.getAsJsonObject("sleeveBloomOffTurn").remove("windowId")
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(wrongWindow.toString()) }
        val wrongTag = json(offTurn).also { it.addProperty("api", ApiTags.SUBMIT_SLEEVE_BLOOM) }
        assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(wrongTag.toString()) }
    }

    @Test fun `immediate offer preview flags round trip but off-turn cannot carry them`() {
        val base = FakeServerConnection.createDefaultSnapshot()
        val offer = ProtocolSleeveBloomOfferView(
            "offer-preview", ProtocolTile(1), ProtocolTile(135),
            ProtocolSleeveBloomDrawKind.NORMAL, 20_000,
            base.decision.decisionId, base.decision.controlEpoch,
            firstCanTsumo = true, secondCanTsumo = false, firstCanKan = false, secondCanKan = true
        )
        val view = ProtocolSleeveBloomView(
            canApply = false, disabledReason = "SLEEVE_BLOOM_ALREADY_PENDING", cost = 10,
            drawKind = ProtocolSleeveBloomDrawKind.NORMAL, decisionId = base.decision.decisionId,
            controlEpoch = base.decision.controlEpoch, stateVersion = 1, pendingOffer = offer
        )
        val snapshot = base.copy(
            view = base.view.copy(phase = ProtocolGamePhase.CHEAT_WINDOW, myDrawnTile = ProtocolTile(1)),
            sleeveBloom = view
        )
        val decoded = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot))).snapshot!!
        assertTrue(decoded.sleeveBloom!!.pendingOffer!!.firstCanTsumo)
        assertTrue(decoded.sleeveBloom!!.pendingOffer!!.secondCanKan)
        assertThrows<IllegalArgumentException> {
            offer.copy(mode = ProtocolSleeveBloomOfferMode.OFF_TURN, windowId = "window-0")
        }
    }

    @Test fun `off-turn private snapshot keeps mode window reservation and stays out of public payload`() {
        val base = FakeServerConnection.createDefaultSnapshot()
        val first = ProtocolTile(1)
        val second = ProtocolTile(135)
        val offer = ProtocolSleeveBloomOfferView(
            "offer-off", first, second, ProtocolSleeveBloomDrawKind.NORMAL, 20_000,
            base.decision.decisionId, base.decision.controlEpoch,
            mode = ProtocolSleeveBloomOfferMode.OFF_TURN, windowId = "window-0"
        )
        val view = ProtocolSleeveBloomView(
            canApply = false, disabledReason = "NOT_YOUR_TURN", cost = 10,
            drawKind = null, decisionId = base.decision.decisionId,
            controlEpoch = base.decision.controlEpoch, stateVersion = 3,
            pendingOffer = offer, canApplyOffTurn = false,
            offTurnDisabledReason = "SLEEVE_BLOOM_ALREADY_PENDING",
            offTurnWindowId = "window-0"
        )
        val snapshot = base.copy(
            view = base.view.copy(phase = ProtocolGamePhase.DRAW, currentTurnPlayer = 1, myDrawnTile = null),
            sleeveBloom = view
        )
        val decoded = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot))).snapshot!!
        assertEquals(ProtocolSleeveBloomOfferMode.OFF_TURN, decoded.sleeveBloom!!.pendingOffer!!.mode)
        assertEquals("window-0", decoded.sleeveBloom!!.pendingOffer!!.windowId)

        val reserved = snapshot.copy(sleeveBloom = view.copy(
            pendingOffer = null,
            reservation = ProtocolSleeveBloomReservationView(second, "offer-off", "window-0")
        ))
        val reservedDecoded = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = reserved))).snapshot!!
        assertEquals(second, reservedDecoded.sleeveBloom!!.reservation!!.tile)

        val publicJson = ApiMessageCodec.encodeResponse(ApiResponse(publicSnapshot = FakeServerConnection.createDefaultPublicSnapshot()))
        assertFalse(publicJson.contains("pendingOffer"))
        assertFalse(publicJson.contains("reservation"))
        assertFalse(publicJson.contains("offTurnWindowId"))
        assertFalse(publicJson.contains("secondTile"))
    }
}

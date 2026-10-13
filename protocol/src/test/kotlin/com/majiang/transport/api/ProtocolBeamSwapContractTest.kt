package com.majiang.transport.api

import com.google.gson.JsonNull
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.majiang.server.api.FakeServerConnection
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Independent Codex contracts based on doc/beam-swap/INTERFACES.md. */
class ProtocolBeamSwapContractTest {
    private fun activate() = ApiRequest(api = ApiTags.SUBMIT_BEAM_SWAP, requestId = "activate",
        beamSwap = ProtocolBeamSwapRequest("activate", "session-fake", "round-1", "decision-0", 1))

    private fun choose(slot: ProtocolBeamSwapSlot = ProtocolBeamSwapSlot.WallSlot(32)) =
        ApiRequest(api = ApiTags.SUBMIT_BEAM_SWAP_CHOICE, requestId = "choose",
            beamSwapChoice = ProtocolBeamSwapChoiceRequest("choose", "session-fake", "round-1", "decision-0", 1,
                "offer", ProtocolBeamSwapAction.SWAP, 0, slot))

    private fun keep() = ApiRequest(api = ApiTags.SUBMIT_BEAM_SWAP_CHOICE, requestId = "keep",
        beamSwapChoice = ProtocolBeamSwapChoiceRequest("keep", "session-fake", "round-1", "decision-0", 1,
            "offer", ProtocolBeamSwapAction.KEEP_ORIGINAL))

    private fun json(request: ApiRequest) = JsonParser.parseString(ApiMessageCodec.encodeRequest(request)).asJsonObject
    private fun reject(request: ApiRequest, mutate: (JsonObject) -> Unit) {
        val wire = json(request)
        mutate(wire)
        assertThrows<JsonParseException>(wire.toString()) { ApiMessageCodec.decodeRequest(wire.toString()) }
    }

    private fun pending(): ProtocolPlayerSnapshot {
        val base = FakeServerConnection.createDefaultSnapshot()
        val offer = ProtocolBeamSwapOfferView("offer", ProtocolBeamSwapWindow.WALL,
            listOf(ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.WallSlot(32), false),
                ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.WallSlot(33), true, ProtocolTile(135))),
            listOf(0), 20_000, base.decision.decisionId, base.decision.controlEpoch)
        return base.copy(view = base.view.copy(phase = ProtocolGamePhase.CHEAT_WINDOW,
            doraIndicators = listOf(ProtocolTile(120)), myDrawnTile = ProtocolTile(1)),
            beamSwap = ProtocolBeamSwapView(false, "BEAM_SWAP_ALREADY_PENDING", 10, ProtocolBeamSwapWindow.WALL,
                base.decision.decisionId, base.decision.controlEpoch, 1, offer))
    }

    @Test fun `activation swap and cancellation round trip with exact tagged slots`() {
        for (request in listOf(activate(), keep(), choose(), choose(ProtocolBeamSwapSlot.DeadWallSlot(8)),
            choose(ProtocolBeamSwapSlot.ReplacementSlot(0)))) {
            assertTrue(request.api in ApiTags.KNOWN)
            assertEquals(request, ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(request)))
        }
        val target = json(choose()).getAsJsonObject("beamSwapChoice").getAsJsonObject("targetSlot")
        assertEquals(setOf("kind", "index"), target.keySet())
        assertEquals("WALL", target.get("kind").asString)
        assertEquals(32, target.get("index").asInt)
    }

    @Test fun `each payload is required exclusive and bound to matching envelope request id`() {
        for ((request, field) in listOf(activate() to "beamSwap", choose() to "beamSwapChoice")) {
            reject(request) { it.remove(field) }
            reject(request) { it.add(field, JsonNull.INSTANCE) }
            reject(request) { it.addProperty("requestId", "other") }
            for (other in listOf("choice", "shufflePlan", "tileMark", "sleeveBloom", "sleeveBloomChoice",
                "beamSwap", "beamSwapChoice").filter { it != field }) {
                reject(request) { it.add(other, JsonObject()) }
            }
            for (tag in ApiTags.KNOWN.filter { it != request.api }) {
                reject(request) { it.addProperty("api", tag) }
            }
        }
    }

    @Test fun `keep original omits both selection fields and rejects even explicit null`() {
        val wire = json(keep()).getAsJsonObject("beamSwapChoice")
        assertFalse(wire.has("handTileId"))
        assertFalse(wire.has("targetSlot"))
        for (field in listOf("handTileId", "targetSlot")) {
            reject(keep()) { it.getAsJsonObject("beamSwapChoice").add(field, JsonNull.INSTANCE) }
            reject(keep()) { it.getAsJsonObject("beamSwapChoice").add(field,
                json(choose()).getAsJsonObject("beamSwapChoice").get(field)) }
        }
    }

    @Test fun `swap requires both typed fields and a recognized action`() {
        for (field in listOf("action", "handTileId", "targetSlot")) {
            reject(choose()) { it.getAsJsonObject("beamSwapChoice").remove(field) }
            reject(choose()) { it.getAsJsonObject("beamSwapChoice").add(field, JsonNull.INSTANCE) }
        }
        for (action in listOf("\"swap\"", "\"UNKNOWN\"", "0", "true")) {
            reject(choose()) { it.getAsJsonObject("beamSwapChoice").add("action", JsonParser.parseString(action)) }
        }
        for (id in listOf("-1", "136", "0.5", "\"0\"", "true")) {
            reject(choose()) { it.getAsJsonObject("beamSwapChoice").add("handTileId", JsonParser.parseString(id)) }
        }
    }

    @Test fun `slot kind and index are strict and cannot smuggle a target entity`() {
        for (slot in listOf("{}", "null", "[]", "0", "{\"kind\":\"OTHER\",\"index\":0}",
            "{\"kind\":\"WALL\"}", "{\"index\":0}", "{\"kind\":\"wall\",\"index\":0}",
            "{\"kind\":\"WALL\",\"index\":0,\"tileId\":135}")) {
            reject(choose()) { it.getAsJsonObject("beamSwapChoice").add("targetSlot", JsonParser.parseString(slot)) }
        }
        for (index in listOf("-1", "0.5", "\"0\"", "2147483648", "null", "true")) {
            reject(choose()) { it.getAsJsonObject("beamSwapChoice").getAsJsonObject("targetSlot")
                .add("index", JsonParser.parseString(index)) }
        }
        // Nonnegative but unavailable slots must reach the authority, which owns offer membership.
        assertEquals(choose(ProtocolBeamSwapSlot.WallSlot(999)),
            ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(choose(ProtocolBeamSwapSlot.WallSlot(999)))))
    }

    @Test fun `request adapters reject forged authority fields and malformed identity`() {
        for ((request, field) in listOf(activate() to "beamSwap", choose() to "beamSwapChoice")) {
            for (extra in listOf("seat", "cost", "tileId", "targetTileId", "expectedTileId", "deadlineMillis")) {
                reject(request) { it.getAsJsonObject(field).addProperty(extra, 135) }
            }
            for (epoch in listOf("-1", "1.2", "\"1\"", "9223372036854775808", "null")) {
                reject(request) { it.getAsJsonObject(field).add("controlEpoch", JsonParser.parseString(epoch)) }
            }
            for (identity in listOf("sessionId", "roundId", "decisionId")) {
                reject(request) { it.getAsJsonObject(field).addProperty(identity, " ") }
            }
        }
    }

    @Test fun `blind candidate wire contains only slot and marked while marked candidate reveals its tile`() {
        val snapshot = pending()
        val response = ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot))
        val offer = JsonParser.parseString(response).asJsonObject.getAsJsonObject("snapshot")
            .getAsJsonObject("beamSwap").getAsJsonObject("pendingOffer")
        val blind = offer.getAsJsonArray("candidates")[0].asJsonObject
        assertEquals(setOf("slot", "marked"), blind.keySet())
        assertFalse(blind.get("marked").asBoolean)
        val marked = offer.getAsJsonArray("candidates")[1].asJsonObject
        assertEquals(setOf("slot", "marked", "tile"), marked.keySet())
        assertEquals(135, marked.getAsJsonObject("tile").get("tileId").asInt)
        assertEquals(snapshot, ApiMessageCodec.decodeResponse(response).snapshot)
        val push = ApiMessageCodec.decodePushEnvelope(ApiMessageCodec.encodePush(
            ProtocolPlayerUpdate(snapshot, snapshot.cursor, emptyList()), isFullSnapshot = true))
        assertTrue(push.isFullSnapshot)
        assertEquals(snapshot.beamSwap, push.update.snapshot.beamSwap)
    }

    @Test fun `candidate visibility cannot contradict marking or accept hidden identity fields`() {
        assertThrows<IllegalArgumentException> {
            ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.WallSlot(32), false, ProtocolTile(135))
        }
        assertThrows<IllegalArgumentException> {
            ProtocolBeamSwapCandidateView(ProtocolBeamSwapSlot.WallSlot(32), true)
        }
        for (extra in listOf("tileId", "expectedTileId", "hiddenTileId")) {
            val wire = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = pending()))).asJsonObject
            wire.getAsJsonObject("snapshot").getAsJsonObject("beamSwap").getAsJsonObject("pendingOffer")
                .getAsJsonArray("candidates")[0].asJsonObject.addProperty(extra, 134)
            assertThrows<JsonParseException> { ApiMessageCodec.decodeResponse(wire.toString()) }
        }
    }

    @Test fun `required nested candidate objects reject null and wrong types as parse errors`() {
        val mutations = listOf<Pair<String, (JsonObject) -> Unit>>(
            "null candidate" to { offer -> offer.add("candidates", JsonParser.parseString("[null]")) },
            "numeric candidate" to { offer -> offer.add("candidates", JsonParser.parseString("[1]")) },
            "string candidate" to { offer -> offer.add("candidates", JsonParser.parseString("[\"tile\"]")) },
            "array candidate" to { offer -> offer.add("candidates", JsonParser.parseString("[[]]")) },
            "null slot" to { offer -> offer.getAsJsonArray("candidates")[0].asJsonObject.add("slot", JsonNull.INSTANCE) },
            "array slot" to { offer -> offer.getAsJsonArray("candidates")[0].asJsonObject.add("slot", JsonParser.parseString("[]")) },
            "numeric slot" to { offer -> offer.getAsJsonArray("candidates")[0].asJsonObject.addProperty("slot", 1) },
            "string slot" to { offer -> offer.getAsJsonArray("candidates")[0].asJsonObject.addProperty("slot", "WALL") }
        )
        for ((label, mutate) in mutations) {
            val wire = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = pending()))).asJsonObject
            mutate(wire.getAsJsonObject("snapshot").getAsJsonObject("beamSwap").getAsJsonObject("pendingOffer"))
            assertThrows<JsonParseException>(label) { ApiMessageCodec.decodeResponse(wire.toString()) }
        }
    }

    @Test fun `pending snapshot rejects wrong phase owner absent draw and illegal hand selection`() {
        val original = pending()
        assertThrows<IllegalArgumentException> { original.copy(view = original.view.copy(phase = ProtocolGamePhase.DISCARD)) }
        assertThrows<IllegalArgumentException> { original.copy(view = original.view.copy(currentTurnPlayer = 1)) }
        assertThrows<IllegalArgumentException> { original.copy(view = original.view.copy(myDrawnTile = null)) }
        for (ids in listOf(listOf(1), listOf(99))) {
            assertThrows<IllegalArgumentException> { original.copy(beamSwap = original.beamSwap!!.copy(
                pendingOffer = original.beamSwap!!.pendingOffer!!.copy(selectableHandTileIds = ids))) }
        }
        for (field in listOf("decisionId", "controlEpoch", "deadlineMillis", "window")) {
            val wire = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = original))).asJsonObject
            val offer = wire.getAsJsonObject("snapshot").getAsJsonObject("beamSwap").getAsJsonObject("pendingOffer")
            when (field) {
                "decisionId" -> offer.addProperty(field, "stale")
                "window" -> offer.addProperty(field, "DEAD_WALL")
                else -> offer.addProperty(field, -1)
            }
            assertThrows<JsonParseException> { ApiMessageCodec.decodeResponse(wire.toString()) }
        }
    }

    @Test fun `meld entities never become selectable hand tiles for any kan or call type`() {
        val original = pending()
        for (type in ProtocolMeldType.entries) {
            val tiles = when (type) {
                ProtocolMeldType.CHI -> listOf(ProtocolTile(20), ProtocolTile(24), ProtocolTile(28))
                ProtocolMeldType.PON -> listOf(ProtocolTile(20), ProtocolTile(21), ProtocolTile(22))
                else -> (20..23).map { ProtocolTile(it) }
            }
            val melds = listOf(listOf(ProtocolMeld(type, tiles)), emptyList(), emptyList(), emptyList())
            val valid = original.copy(view = original.view.copy(melds = melds))
            assertEquals(listOf(0), valid.beamSwap!!.pendingOffer!!.selectableHandTileIds)
            assertThrows<IllegalArgumentException> { valid.copy(beamSwap = valid.beamSwap!!.copy(
                pendingOffer = valid.beamSwap!!.pendingOffer!!.copy(selectableHandTileIds = listOf(20)))) }
        }
    }

    @Test fun `offer collection inputs and published snapshots are defensively isolated`() {
        val original = pending()
        val candidates = original.beamSwap!!.pendingOffer!!.candidates.toMutableList()
        val hand = mutableListOf(0)
        val offer = original.beamSwap!!.pendingOffer!!.copy(candidates = candidates, selectableHandTileIds = hand)
        val snapshot = original.copy(beamSwap = original.beamSwap!!.copy(pendingOffer = offer))
        val copied = snapshot.copy()
        candidates.clear()
        hand.clear()
        assertEquals(2, offer.candidates.size)
        assertEquals(listOf(0), offer.selectableHandTileIds)
        assertEquals(snapshot, copied)
        assertThrows<UnsupportedOperationException> { (offer.candidates as MutableList<*>).clear() }
        assertThrows<UnsupportedOperationException> { (offer.selectableHandTileIds as MutableList<*>).clear() }
    }

    @Test fun `feature requires enabled economy and explicit exact ten point cost`() {
        assertThrows<IllegalArgumentException> { ProtocolMatchConfig(beamSwapConfig = ProtocolBeamSwapConfig(true)) }
        for (cost in listOf<Int?>(null, 0, 9, 11)) {
            assertThrows<IllegalArgumentException> { ProtocolMatchConfig(
                cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, cost?.let { mapOf("BEAM_SWAP" to it) } ?: emptyMap()),
                beamSwapConfig = ProtocolBeamSwapConfig(true)) }
        }
        assertThrows<IllegalArgumentException> { ProtocolMatchConfig(
            cheatEconomy = ProtocolCheatEconomyConfig(false, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
            beamSwapConfig = ProtocolBeamSwapConfig(true)) }
        val valid = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
            beamSwapConfig = ProtocolBeamSwapConfig(true))
        val wire = ApiMessageCodec.gson.toJsonTree(valid).asJsonObject
        assertEquals(valid, ApiMessageCodec.gson.fromJson(wire, ProtocolMatchConfig::class.java))
        wire.getAsJsonObject("cheatEconomy").getAsJsonObject("costs").remove("BEAM_SWAP")
        assertThrows<JsonParseException> { ApiMessageCodec.gson.fromJson(wire, ProtocolMatchConfig::class.java) }
    }

    @Test fun `missing feature fields default to disabled unsupported and absent private offer`() {
        assertFalse(ApiMessageCodec.gson.fromJson("{}", ProtocolBeamSwapConfig::class.java).enabled)
        val caps = ApiMessageCodec.gson.toJsonTree(ProtocolAccessCapabilities()).asJsonObject
        caps.remove("beamSwapSupported")
        assertFalse(ApiMessageCodec.gson.fromJson(caps, ProtocolAccessCapabilities::class.java).beamSwapSupported)
        val config = ApiMessageCodec.gson.toJsonTree(ProtocolMatchConfig()).asJsonObject
        config.remove("beamSwapConfig")
        assertFalse(ApiMessageCodec.gson.fromJson(config, ProtocolMatchConfig::class.java).beamSwapConfig.enabled)
        val wire = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = pending()))).asJsonObject
        wire.getAsJsonObject("snapshot").remove("beamSwap")
        assertNull(ApiMessageCodec.decodeResponse(wire.toString()).snapshot!!.beamSwap)
    }
}

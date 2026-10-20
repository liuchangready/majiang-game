package com.majiang.transport.api

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.google.gson.JsonParseException
import com.google.gson.JsonNull
import com.majiang.server.api.FakeServerConnection
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Codex-owned A1 tests for the actual request, response and full-snapshot wire boundaries. */
class ProtocolTileMarkContractTest {
    private fun request() = ProtocolTileMarkRequest("mark-1", "session-fake", "round-1", "decision-0", 1L, listOf(0, 1, 2, 3))
    private fun envelope() = ApiRequest(api = ApiTags.SUBMIT_TILE_MARK, requestId = "mark-1", tileMark = request())
    private fun goodJson() = JsonParser.parseString(ApiMessageCodec.encodeRequest(envelope())).asJsonObject

    private fun snapshot(): ProtocolPlayerSnapshot {
        val base = FakeServerConnection.createDefaultSnapshot()
        val tiles = (20..23).map { id ->
            if (id == 20) ProtocolMarkedTileView(id, ProtocolTile(id), ProtocolTileMarkArea.DISCARD, 2,
                ProtocolRiverPosition("round-1", 2, 6))
            else ProtocolMarkedTileView(id, ProtocolTile(id), ProtocolTileMarkArea.NOT_VISIBLE, null, null)
        }
        val group = ProtocolTileMarkGroupView("group-1", "round-1", 1, 4, 3, false, tiles)
        val mark = ProtocolTileMarkView(false, "INSUFFICIENT_SELECTABLE_TILES", 10, 4, 12,
            emptyList(), base.decision.decisionId, base.decision.controlEpoch, 2L, listOf(group))
        return base.copy(
            view = base.view.copy(discards = List(4) { seat ->
                if (seat == 2) (40..45).map { ProtocolTile(it) } + ProtocolTile(20) else emptyList()
            }),
            tileMark = mark
        )
    }

    @Test
    fun `tile mark request survives the real API envelope codec`() {
        assertTrue(ApiTags.SUBMIT_TILE_MARK in ApiTags.KNOWN)
        assertEquals(envelope(), ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(envelope())))
    }

    @Test
    fun `identity mismatch missing payload and mixed operation payloads are rejected`() {
        val variants = listOf<(JsonObject) -> Unit>(
            { it.addProperty("requestId", "different-id") },
            { it.remove("tileMark") },
            { it.addProperty("api", ApiTags.SNAPSHOT) },
            { it.add("choice", JsonParser.parseString("""{"requestId":"mark-1","roundId":"round-1","decisionId":"decision-0","controlEpoch":1,"optionId":"opt-1"}""")) },
            { it.add("shufflePlan", JsonParser.parseString("""{"requestId":"mark-1","roundId":"round-1","controlEpoch":1,"selection":{"target":null}}""")) }
        )
        variants.forEach { mutate ->
            val json = goodJson().also(mutate)
            assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(json.toString()) }
        }
    }

    @Test
    fun `wire rejects forged authority fields and noninteger identities`() {
        for (field in listOf("ownerSeat", "cost", "expiresAfterRoundOrdinal", "discardIndex")) {
            val json = goodJson()
            json.getAsJsonObject("tileMark").addProperty(field, 0)
            assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(json.toString()) }
        }
        for (value in listOf("1.5", "9223372036854775808", "\"1\"", "-1")) {
            val json = goodJson()
            json.getAsJsonObject("tileMark").add("controlEpoch", JsonParser.parseString(value))
            assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(json.toString()) }
        }
        for (value in listOf("1.5", "2147483648", "\"1\"")) {
            val json = goodJson()
            json.getAsJsonObject("tileMark").add("tileIds", JsonParser.parseString("[0,$value,2,3]"))
            assertThrows<JsonParseException> { ApiMessageCodec.decodeRequest(json.toString()) }
        }
    }

    @Test
    fun `private snapshot and PUSH retain exact river position and all groups`() {
        val snapshot = snapshot()
        val response = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot)))
        assertEquals(snapshot, response.snapshot)
        val decoded = ApiMessageCodec.decodePushEnvelope(ApiMessageCodec.encodePush(
            ProtocolPlayerUpdate(snapshot, snapshot.cursor, emptyList()), isFullSnapshot = true))
        assertEquals(snapshot.tileMark, decoded.update.snapshot.tileMark)
        assertEquals(ProtocolRiverPosition("round-1", 2, 6), decoded.update.snapshot.tileMark!!.activeGroups.single().tiles.first().riverPosition)
        assertTrue(decoded.isFullSnapshot)
    }

    @Test
    fun `snapshot rejects river positions from another round seat index or physical tile`() {
        for (change in listOf<(JsonObject) -> Unit>(
            { it.addProperty("roundId", "previous-round") },
            { it.addProperty("seat", 3) },
            { it.addProperty("discardIndex", -1) },
            { it.addProperty("discardIndex", 7) },
            { it.addProperty("discardIndex", 5) }
        )) {
            val json = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot()))).asJsonObject
            val position = json.getAsJsonObject("snapshot").getAsJsonObject("tileMark")
                .getAsJsonArray("activeGroups")[0].asJsonObject.getAsJsonArray("tiles")[0].asJsonObject.getAsJsonObject("riverPosition")
            change(position)
            assertThrows<JsonParseException> { ApiMessageCodec.decodeResponse(json.toString()) }
        }
    }

    @Test
    fun `unknown area hidden seat and missing discard position are rejected`() {
        for (change in listOf<(JsonObject) -> Unit>(
            { it.addProperty("area", "WALL_SLOT") },
            { it.addProperty("area", "NOT_VISIBLE") },
            { it.add("tile", JsonNull.INSTANCE) },
            { it.remove("riverPosition") }
        )) {
            val json = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot()))).asJsonObject
            val tile = json.getAsJsonObject("snapshot").getAsJsonObject("tileMark")
                .getAsJsonArray("activeGroups")[0].asJsonObject.getAsJsonArray("tiles")[0].asJsonObject
            change(tile)
            assertThrows<JsonParseException> { ApiMessageCodec.decodeResponse(json.toString()) }
        }
    }

    @Test
    fun `discard position cannot belong to a snapshot without a current round`() {
        val source = snapshot()
        assertThrows<IllegalArgumentException> { source.copy(roundId = null) }
        for (explicitNull in listOf(false, true)) {
            val json = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = source))).asJsonObject
            val snap = json.getAsJsonObject("snapshot")
            if (explicitNull) snap.add("roundId", JsonNull.INSTANCE) else snap.remove("roundId")
            assertThrows<JsonParseException> { ApiMessageCodec.decodeResponse(json.toString()) }
        }
    }

    @Test
    fun `malformed group sizes duplicates counts and authoritative fee are rejected`() {
        val variants = listOf<(JsonObject) -> Unit>(
            { it.addProperty("activeTileCount", 0) },
            { it.addProperty("activeTileCount", 16) },
            { it.addProperty("cost", 0) },
            { it.getAsJsonArray("activeGroups").set(0, JsonNull.INSTANCE) },
            { it.getAsJsonArray("activeGroups")[0].asJsonObject.getAsJsonArray("tiles").set(0, JsonNull.INSTANCE) },
            {
                val group = it.getAsJsonArray("activeGroups")[0].asJsonObject
                group.addProperty("createdRoundOrdinal", Int.MAX_VALUE)
                group.addProperty("expiresAfterRoundOrdinal", Int.MIN_VALUE + 2)
            },
            { it.getAsJsonArray("activeGroups")[0].asJsonObject.addProperty("futureRoundsRemaining", 4) },
            { it.getAsJsonArray("activeGroups")[0].asJsonObject.addProperty("isLastEffectiveRound", true) },
            { it.getAsJsonArray("activeGroups")[0].asJsonObject.getAsJsonArray("tiles").remove(3) },
            {
                val tiles = it.getAsJsonArray("activeGroups")[0].asJsonObject.getAsJsonArray("tiles")
                tiles.set(3, tiles[2].deepCopy())
            },
            {
                val groups = it.getAsJsonArray("activeGroups")
                groups.add(groups[0].deepCopy())
                it.addProperty("activeTileCount", 8)
            }
        )
        variants.forEachIndexed { index, mutate ->
            val json = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot()))).asJsonObject
            mutate(json.getAsJsonObject("snapshot").getAsJsonObject("tileMark"))
            assertThrows(JsonParseException::class.java, { ApiMessageCodec.decodeResponse(json.toString()) },
                "Invalid mark variant $index must be rejected: $json")
        }
    }

    @Test
    fun `old wire defaults to disabled unsupported and no marks`() {
        assertFalse(ApiMessageCodec.gson.fromJson("{}", ProtocolTileMarkConfig::class.java).enabled)
        val oldCapabilities = ApiMessageCodec.gson.toJsonTree(ProtocolAccessCapabilities()).asJsonObject
        oldCapabilities.remove("tileMarkSupported")
        assertFalse(ApiMessageCodec.gson.fromJson(oldCapabilities, ProtocolAccessCapabilities::class.java).tileMarkSupported)
        val oldConfig = ApiMessageCodec.gson.toJsonTree(ProtocolMatchConfig()).asJsonObject
        oldConfig.remove("tileMarkConfig")
        assertFalse(ApiMessageCodec.gson.fromJson(oldConfig, ProtocolMatchConfig::class.java).tileMarkConfig.enabled)
        val json = JsonParser.parseString(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot()))).asJsonObject
        json.getAsJsonObject("snapshot").remove("tileMark")
        assertNull(ApiMessageCodec.decodeResponse(json.toString()).snapshot!!.tileMark)
    }

    @Test
    fun `enabled configuration and supported capability survive copy and wire`() {
        val config = ProtocolMatchConfig(
            cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to 10)),
            tileMarkConfig = ProtocolTileMarkConfig(true))
        val req = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "create", playerName = "P", config = config.copy())
        assertTrue(ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(req)).config!!.tileMarkConfig.enabled)
        val capabilities = ProtocolAccessCapabilities(tileMarkSupported = true)
        assertTrue(ApiMessageCodec.gson.fromJson(ApiMessageCodec.gson.toJson(capabilities.copy()), ProtocolAccessCapabilities::class.java).tileMarkSupported)
        for (cost in listOf(0, 9, 11)) {
            assertThrows<Exception> {
                val bad = config.copy(cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to cost)))
                ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(req.copy(config = bad)))
            }
        }
    }

    @Test
    fun `request and decoded snapshot expose immutable defensive collections`() {
        val source = mutableListOf(0, 1, 2, 3)
        val request = ProtocolTileMarkRequest("r", "s", "round", "decision", 0L, source)
        source.clear()
        assertEquals(listOf(0, 1, 2, 3), request.tileIds)
        assertThrows<UnsupportedOperationException> { (request.tileIds as MutableList<*>).clear() }
        val mark = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot()))).snapshot!!.tileMark!!
        assertThrows<UnsupportedOperationException> { (mark.activeGroups as MutableList<*>).clear() }
        assertThrows<UnsupportedOperationException> { (mark.activeGroups.single().tiles as MutableList<*>).clear() }
        assertThrows<UnsupportedOperationException> { (mark.selectableTileIds as MutableList<*>).clear() }
    }
}

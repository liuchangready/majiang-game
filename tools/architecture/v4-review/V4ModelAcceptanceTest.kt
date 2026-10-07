package com.majiang.v4models

import com.majiang.event.CheatTechnique
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.model.Tile
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.inprocess.InProcessServer
import com.majiang.state.GameAction
import com.majiang.stacking.ShuffleConfig
import com.majiang.transport.api.*
import com.google.gson.JsonParseException
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import kotlin.test.*

/** R2 gates: observable value preservation and immutable protocol boundaries. */
class V4ModelAcceptanceTest {
    @Test fun allPhysicalTilesSurviveDomainProtocolRoundTrip() {
        for (id in 0..135) {
            val tile = Tile.fromTileId(id)
            val protocol = EngineProtocolMapper.toProtocolTile(tile)
            assertEquals(id, protocol.tileId)
            assertEquals(tile.typeIndex, protocol.typeIndex)
            assertEquals(tile.suit.name, protocol.suit.name)
            assertEquals(tile.rank, protocol.rank)
            assertEquals(tile.isRedFive, protocol.isRedFive)
            assertEquals(tile, EngineProtocolMapper.toEngineTile(protocol))
        }
    }

    @Test fun protocolTileCannotContradictItsPhysicalIdentity() {
        assertFailsWith<IllegalArgumentException> {
            ProtocolTile(tileId = 0, suit = ProtocolSuit.DRAGON, rank = 3)
        }
    }

    @Test fun concealedKanPreservesAbsentSourceSeat() {
        val tiles = (0..3).map { Tile.fromTileId(it) }
        val meld = com.majiang.model.Meld(com.majiang.model.MeldType.CLOSED_KAN, tiles, -1, tiles.first())
        assertEquals(meld, EngineProtocolMapper.toEngineMeld(EngineProtocolMapper.toProtocolMeld(meld)))
    }

    @Test fun meldPublicTilesAreAnImmutableSnapshotOfInput() {
        val input = mutableListOf(0, 4, 8).map { ProtocolTile.fromTileId(it) }.toMutableList()
        val meld = ProtocolMeld(ProtocolMeldType.CHI, input, 3, input.first())
        input.clear()
        assertEquals(listOf(0, 4, 8), meld.tiles.map { it.tileId })
        assertFails { (meld.tiles as MutableList<ProtocolTile>).clear() }
        assertEquals(3, meld.tiles.size)
    }

    @Test fun matchConfigurationDoesNotRetainMutableCallerLists() {
        val scores = mutableListOf(25000, 25000, 25000, 25000)
        val config = ProtocolMatchConfig(startingScores = scores)
        scores[0] = 1
        assertEquals(25000, config.startingScores[0])
        assertFails { (config.startingScores as MutableList<Int>)[0] = 2 }
    }

    @Test fun publishedDecisionOptionsCannotChangeThroughInputAliasing() {
        val option = ProtocolActionOption("pass", ProtocolLegalAction.Pass(0))
        val input = mutableListOf(option)
        val decision = ProtocolPlayerDecision("round", "decision", 1, input)
        input.clear()
        assertEquals(listOf("pass"), decision.options.map { it.optionId })
        assertFails { (decision.options as MutableList<ProtocolActionOption>).clear() }
    }

    @Test fun actionRoundTripPreservesCheatParameterTypes() {
        val action = GameAction.UseCheatTechnique(0, CheatTechnique.SLEEVE_SWAP, mapOf("card" to 1))
        val restored = EngineProtocolMapper.toEngineAction(EngineProtocolMapper.toProtocolAction(action))
        assertEquals(action, restored, "Numeric parameters must not silently become strings")
    }

    @Test fun completeNondefaultMatchConfigurationSurvivesRoundTrip() {
        val config = MatchConfig(
            handsPerWind = 2, winds = listOf(1), startingScores = listOf(32000, 28000, 24000, 16000),
            allowExtension = false, allowAgariYame = false, bankruptcyEnabled = false,
            bankruptcyThreshold = 100, bankruptcyInclusive = true, targetScore = 40000,
            tieBreakSeats = listOf(3, 1, 0, 2),
            cheatEconomy = CheatEconomyConfig(true, listOf(100, 90, 80, 70), mapOf("SLEEVE_SWAP" to 7)),
            shuffleConfig = ShuffleConfig(true, 6500, 12, 0.6, 0.8, true)
        ).validateSupported()
        assertEquals(config, EngineProtocolMapper.toEngineConfig(EngineProtocolMapper.toProtocolConfig(config)))
    }

    @Test fun incompleteSnapshotCannotPassWireDecoding() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeResponse("""{"requestId":"snapshot","success":true,"snapshot":{}}""")
        }
    }

    @Test fun actionChoiceRequiresControlEpochAtWireBoundary() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeRequest("""{"api":"SUBMIT_CHOICE","requestId":"choice","protocolVersion":4,
                "choice":{"requestId":"choice","roundId":"round","decisionId":"decision","optionId":"option"}}""")
        }
    }

    @Test fun actionChoiceRoundTripPreservesPreRoundNullId() {
        val choice = com.majiang.application.ActionChoice("choice", null, "decision", 1, "option")
        val protocol = EngineProtocolMapper.toProtocolActionChoice(choice)
        assertNull(protocol.roundId)
        assertEquals(choice, EngineProtocolMapper.toEngineActionChoice(protocol))
    }

    @Test fun fractionalControlEpochIsNotSilentlyTruncated() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeRequest("""{"api":"SUBMIT_CHOICE","requestId":"choice","protocolVersion":4,
                "choice":{"requestId":"choice","roundId":"round","decisionId":"decision","optionId":"option","controlEpoch":1.25}}""")
        }
    }

    @Test fun nullablePreRoundChoiceSurvivesWireRoundTrip() {
        val choice = ProtocolActionChoice("choice", null, "decision", 1, "option")
        val request = ApiRequest(api = "SUBMIT_CHOICE", requestId = "choice", choice = choice)
        assertEquals(choice, ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(request)).choice)
    }

    @Test fun snapshotWithEmptyNestedStateIsRejected() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeResponse("""{"requestId":"snapshot","success":true,
                "snapshot":{"sessionId":"session","syncEpoch":"epoch","view":{},"decision":{}}}""")
        }
    }

    @Test fun stringControlEpochIsNotAcceptedAsANumber() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeRequest("""{"api":"SUBMIT_CHOICE","requestId":"choice","protocolVersion":4,
                "choice":{"requestId":"choice","roundId":"round","decisionId":"decision","optionId":"option","controlEpoch":"1"}}""")
        }
    }

    @Test fun decodedSnapshotsExposeImmutableNestedCollections() {
        InProcessServer().use { server ->
            val snapshot = server.createLocalTable().snapshot().get(4, TimeUnit.SECONDS)
            val decoded = assertNotNull(ApiMessageCodec.decodeResponse(
                ApiMessageCodec.encodeResponse(ApiResponse(requestId = "snapshot", snapshot = snapshot))).snapshot)
            val expected = snapshot.view.scores.toList()
            assertFails { (decoded.view.scores as MutableList<Int>)[0] = 0 }
            assertEquals(expected, decoded.view.scores)
            val session = assertNotNull(decoded.session)
            assertFails { (session.scores as MutableList<Int>)[0] = 0 }
            assertEquals(snapshot.session!!.scores, session.scores)
        }
    }

    @Test fun wireTileCannotContradictItsPhysicalIdentity() {
        InProcessServer().use { server ->
            val snapshot = server.createLocalTable().snapshot().get(4, TimeUnit.SECONDS)
            val payload = com.google.gson.JsonParser.parseString(ApiMessageCodec.encodeResponse(
                ApiResponse(requestId = "snapshot", snapshot = snapshot))).asJsonObject
            payload.getAsJsonObject("snapshot").getAsJsonObject("view").add("myDrawnTile",
                com.google.gson.JsonParser.parseString("""{"tileId":0,"suit":"DRAGON","rank":3}"""))
            assertFailsWith<JsonParseException> { ApiMessageCodec.decodeResponse(payload.toString()) }
        }
    }

    @Test fun requestDecoderRejectsUnknownNestedFields() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeRequest("""{"api":"SUBMIT_CHOICE","requestId":"choice","protocolVersion":4,
                "choice":{"requestId":"choice","roundId":"round","decisionId":"decision","optionId":"option","controlEpoch":1,"takeover":true}}""")
        }
    }

    @Test fun typedIntegerParameterRejectsFractionAndOverflow() {
        for (value in listOf("1.25", "2147483648", "\"1\"")) {
            assertFailsWith<JsonParseException>("Invalid INT value $value must be rejected") {
                ApiMessageCodec.gson.fromJson("""{"type":"INT","value":$value}""", ProtocolParamValue::class.java)
            }
        }
    }

    @Test fun decodedUpdateEventsCannotBeModifiedByAConsumer() {
        InProcessServer().use { server ->
            val snapshot = server.createLocalTable().snapshot().get(4, TimeUnit.SECONDS)
            val update = ProtocolPlayerUpdate(snapshot, snapshot.cursor, listOf(ProtocolGameEvent.DealingComplete(0)))
            val decoded = ApiMessageCodec.decodePush(ApiMessageCodec.encodePush(update, true))
            assertFails { (decoded.visibleEvents as MutableList<ProtocolGameEvent>).clear() }
            assertEquals(1, decoded.visibleEvents.size)
        }
    }

    @Test fun publishedClientSnapshotCollectionsAreImmutable() {
        InProcessServer().use { server ->
            val snapshot = server.createLocalTable().snapshot().get(4, TimeUnit.SECONDS)
            val view = com.majiang.client.api.ProtocolViewMapper.mapSnapshot(snapshot)
            assertFails { (view.decision.options as MutableList<*>).clear() }
            assertFails { (view.clocks as MutableList<*>).clear() }
            assertFails { (view.view.discards as MutableList<*>).clear() }
            assertEquals(4, view.view.discards.size)
        }
    }

    @Test fun missingRequiredSnapshotFieldsAreNotInvented() {
        InProcessServer().use { server ->
            val snapshot = server.createLocalTable().snapshot().get(4, TimeUnit.SECONDS)
            val encoded = ApiMessageCodec.encodeResponse(ApiResponse(requestId = "snapshot", snapshot = snapshot))
            for ((container, field) in listOf("snapshot" to "cursor", "view" to "roundWind", "view" to "discards")) {
                val root = com.google.gson.JsonParser.parseString(encoded).asJsonObject
                val snap = root.getAsJsonObject("snapshot")
                (if (container == "snapshot") snap else snap.getAsJsonObject(container)).remove(field)
                assertFailsWith<JsonParseException>("Missing required $container.$field must be rejected") {
                    ApiMessageCodec.decodeResponse(root.toString())
                }
            }
        }
    }

    @Test fun fractionalScoreIsNotSilentlyTruncated() {
        InProcessServer().use { server ->
            val snapshot = server.createLocalTable().snapshot().get(4, TimeUnit.SECONDS)
            val root = com.google.gson.JsonParser.parseString(ApiMessageCodec.encodeResponse(
                ApiResponse(requestId = "snapshot", snapshot = snapshot))).asJsonObject
            root.getAsJsonObject("snapshot").getAsJsonObject("view").getAsJsonArray("scores")
                .set(0, com.google.gson.JsonPrimitive(25000.5))
            assertFailsWith<JsonParseException> { ApiMessageCodec.decodeResponse(root.toString()) }
        }
    }

    @Test fun clientConfigurationCopiesCallerCollections() {
        val scores = mutableListOf(25000, 25000, 25000, 25000)
        val config = com.majiang.client.api.MatchConfig(startingScores = scores)
        scores[0] = 0
        assertEquals(25000, config.startingScores[0])
        assertFails { (config.startingScores as MutableList<Int>)[0] = 0 }
    }

    @Test fun legalActionsRequireExplicitPlayerIdentity() {
        for (tag in listOf("Pass", "DrawTile", "DeclareTsumo", "DeclareRon")) {
            assertFailsWith<JsonParseException>("$tag must not invent player 0") {
                ApiMessageCodec.gson.fromJson("""{"actionType":"$tag"}""", ProtocolLegalAction::class.java)
            }
        }
    }

    @Test fun nestedClockRejectsFractionalDeadline() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"seat":0,"opensAt":0,"deadline":1.5,"reserve":20000,"serverNow":0}""",
                ProtocolClock::class.java)
        }
    }

    @Test fun nestedControlRejectsUnsupportedRole() {
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"seat":0,"connected":true,"role":"INVALID","controlEpoch":0}""",
                ProtocolSeatControl::class.java)
        }
    }

    @Test fun scalarScoreRejectsUnknownFields() {
        val obj = ApiMessageCodec.gson.toJsonTree(ProtocolScoreResult(30, 1, 240, "1 han", false, 0)).asJsonObject
        obj.addProperty("unexpected", 1)
        assertFailsWith<JsonParseException> { ApiMessageCodec.gson.fromJson(obj, ProtocolScoreResult::class.java) }
    }

    @Test fun responseEnvelopeRejectsMalformedIdentityValue() {
        assertFailsWith<JsonParseException> { ApiMessageCodec.decodeResponse("""{"requestId":{},"success":true}""") }
    }

    @Test fun unidentifiedErrorResponseRemainsDecodable() {
        val response = ApiResponse(success = false, error = "MALFORMED_REQUEST")
        val decoded = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(response))
        assertNull(decoded.requestId)
        assertFalse(decoded.success)
        assertEquals("MALFORMED_REQUEST", decoded.error)
    }
}

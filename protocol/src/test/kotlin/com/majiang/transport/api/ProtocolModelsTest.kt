package com.majiang.transport.api

import com.google.gson.JsonParseException
import com.majiang.server.api.RoomSummary
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class ProtocolModelsTest {

    @Test
    fun `protocol tile validates physical identity`() {
        for (id in 0..135) {
            val tile = ProtocolTile.fromTileId(id)
            assertEquals(id, tile.tileId)
            assertEquals(id / 4, tile.typeIndex)
            assertEquals(tile.rank, tile.index)
        }
        // Contradictory identity must fail
        assertThrows<IllegalArgumentException> {
            ProtocolTile(tileId = 0, suit = ProtocolSuit.DRAGON, rank = 3)
        }
    }

    @Test
    fun `meld public tiles are immutable defensive snapshot`() {
        val input = mutableListOf(0, 4, 8).map { ProtocolTile.fromTileId(it) }.toMutableList()
        val meld = ProtocolMeld(ProtocolMeldType.CHI, input, 3, input.first())
        input.clear()
        assertEquals(listOf(0, 4, 8), meld.tiles.map { it.tileId })
        assertThrows<UnsupportedOperationException> {
            (meld.tiles as MutableList<ProtocolTile>).clear()
        }
        assertEquals(3, meld.tiles.size)
    }

    @Test
    fun `match configuration does not retain mutable caller lists`() {
        val scores = mutableListOf(25000, 25000, 25000, 25000)
        val config = ProtocolMatchConfig(startingScores = scores)
        scores[0] = 1
        assertEquals(25000, config.startingScores[0])
        assertThrows<UnsupportedOperationException> {
            (config.startingScores as MutableList<Int>)[0] = 2
        }
    }

    @Test
    fun `published decision options cannot change through input aliasing`() {
        val option = ProtocolActionOption("pass", ProtocolLegalAction.Pass(0))
        val input = mutableListOf(option)
        val decision = ProtocolPlayerDecision("round", "decision", 1, input)
        input.clear()
        assertEquals(listOf("pass"), decision.options.map { it.optionId })
        assertThrows<UnsupportedOperationException> {
            (decision.options as MutableList<ProtocolActionOption>).clear()
        }
    }

    @Test
    fun `protocol param value preserves types in json codec`() {
        val action = ProtocolLegalAction.UseCheatTechnique(
            player = 0,
            technique = ProtocolCheatTechnique.SLEEVE_SWAP,
            params = mapOf("card" to ProtocolParamValue.IntValue(1), "name" to ProtocolParamValue.StringValue("test"))
        )
        val json = ApiMessageCodec.gson.toJson(action)
        val restored = ApiMessageCodec.gson.fromJson(json, ProtocolLegalAction::class.java) as ProtocolLegalAction.UseCheatTechnique
        assertEquals(ProtocolParamValue.IntValue(1), restored.params["card"])
        assertEquals(1, (restored.params["card"] as ProtocolParamValue.IntValue).value)
        assertEquals("test", (restored.params["name"] as ProtocolParamValue.StringValue).value)
    }

    @Test
    fun `wire decoding rejects incomplete snapshot`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeResponse("""{"requestId":"snapshot","success":true,"snapshot":{}}""")
        }
    }

    @Test
    fun `wire decoding requires controlEpoch for action choice`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest("""{"api":"SUBMIT_CHOICE","requestId":"choice","protocolVersion":4,
                "choice":{"requestId":"choice","roundId":"round","decisionId":"decision","optionId":"option"}}""")
        }
    }

    @Test
    fun `wire decoding rejects fractional control epoch`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest("""{"api":"SUBMIT_CHOICE","requestId":"choice","protocolVersion":4,
                "choice":{"requestId":"choice","roundId":"round","decisionId":"decision","optionId":"option","controlEpoch":1.25}}""")
        }
    }

    @Test
    fun `decoded snapshots expose immutable nested collections`() {
        val view = ProtocolPlayerView(
            viewerSeat = 0,
            phase = ProtocolGamePhase.DRAW,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(ProtocolTile.fromTileId(0)),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = listOf(ProtocolTile.fromTileId(4)),
            myDrawnTile = null,
            legalActions = emptyList()
        )
        val decision = ProtocolPlayerDecision(
            roundId = "round",
            decisionId = "decision",
            controlEpoch = 1L,
            options = emptyList()
        )
        val snap = ProtocolPlayerSnapshot(
            sessionId = "session",
            roundId = "round",
            syncEpoch = "epoch",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = decision
        )
        val encoded = ApiMessageCodec.encodeResponse(ApiResponse(requestId = "test", snapshot = snap))
        val decoded = ApiMessageCodec.decodeResponse(encoded).snapshot!!
        assertThrows<UnsupportedOperationException> {
            (decoded.view.scores as MutableList<Int>)[0] = 0
        }
        assertEquals(listOf(25000, 25000, 25000, 25000), decoded.view.scores)
    }

    @Test
    fun `legal actions require explicit player identity`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"actionType":"Pass"}""", ProtocolLegalAction::class.java)
        }
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"actionType":"DrawTile"}""", ProtocolLegalAction::class.java)
        }
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"actionType":"DeclareTsumo"}""", ProtocolLegalAction::class.java)
        }
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"actionType":"DeclareRon"}""", ProtocolLegalAction::class.java)
        }
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"actionType":"Pass","player":4}""", ProtocolLegalAction::class.java)
        }
        val pass = ApiMessageCodec.gson.fromJson("""{"actionType":"Pass","player":0}""", ProtocolLegalAction::class.java)
        assertEquals(ProtocolLegalAction.Pass(0), pass)
    }

    @Test
    fun `response envelope preserves nullable identity and rejects invalid supplied type`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeResponse("""{"requestId":{},"success":true}""")
        }
        assertEquals(null, ApiMessageCodec.decodeResponse("""{"success":false,"error":"MALFORMED_REQUEST"}""").requestId)
        assertEquals(null, ApiMessageCodec.decodeResponse("""{"requestId":null,"success":false,"error":"MALFORMED_REQUEST"}""").requestId)
        val resp = ApiMessageCodec.decodeResponse("""{"requestId":"req-1","success":true}""")
        assertEquals("req-1", resp.requestId)
        assertTrue(resp.success)
    }

    @Test
    fun `nested control rejects unsupported role`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"seat":0,"connected":true,"role":"INVALID","controlEpoch":0}""", ProtocolSeatControl::class.java)
        }
        val ctrl = ApiMessageCodec.gson.fromJson("""{"seat":0,"connected":true,"role":"HUMAN","controlEpoch":0}""", ProtocolSeatControl::class.java)
        assertEquals("HUMAN", ctrl.role)
        assertEquals(0, ctrl.seat)
    }

    @Test
    fun `scalar score rejects unknown fields`() {
        val obj = ApiMessageCodec.gson.toJsonTree(ProtocolScoreResult(30, 1, 240, "1 han", false, 0)).asJsonObject
        obj.addProperty("unexpected", 1)
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(obj, ProtocolScoreResult::class.java)
        }
    }

    @Test
    fun `clock rejects fractional deadline and negative reserve`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"seat":0,"opensAt":0,"deadline":1.5,"reserve":20000,"serverNow":0}""", ProtocolClock::class.java)
        }
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"seat":0,"opensAt":0,"deadline":1000,"reserve":-1,"serverNow":0}""", ProtocolClock::class.java)
        }
        val clock = ApiMessageCodec.gson.fromJson("""{"seat":0,"opensAt":0,"deadline":1000,"reserve":20000,"serverNow":0}""", ProtocolClock::class.java)
        assertEquals(1000L, clock.deadline)
    }

    @Test
    fun `fu breakdown requires mandatory fields`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"subtotal":30}""", ProtocolFuBreakdown::class.java)
        }
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"lines":[],"subtotal":30,"roundedUpTo":30}""", ProtocolFuBreakdown::class.java)
        }
    }

    @Test
    fun `game event requires turn number for turn-based events`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"eventType":"TileDealt","player":0,"tile":{"tileId":0}}""", ProtocolGameEvent::class.java)
        }
        val started = ApiMessageCodec.gson.fromJson("""{"eventType":"StackingStarted"}""", ProtocolGameEvent::class.java)
        assertEquals(0, started.turnNumber)
    }

    @Test
    fun `room summary validates player count range`() {
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson("""{"roomId":"r1","roomName":"Room","hostPlayerName":"Host","playerCount":0,"maxPlayers":4,"matchStarted":false}""", RoomSummary::class.java)
        }
        val summary = ApiMessageCodec.gson.fromJson("""{"roomId":"r1","roomName":"Room","hostPlayerName":"Host","playerCount":1,"maxPlayers":4,"matchStarted":false}""", RoomSummary::class.java)
        assertEquals("r1", summary.roomId)
        assertEquals(1, summary.playerCount)
    }
}

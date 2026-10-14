package com.majiang.transport.api

import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.FakeServerConnection
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class ProtocolPublicModelsCodecTest {

    private val redFive = ProtocolTile.fromTileId(19) // 5 万 copy 3 (赤五)
    private val indicator = ProtocolTile.fromTileId(12) // 4 万 copy 0
    private val finalScores = listOf(-500, 35500, 32500, 32500)

    private fun json(text: String): JsonObject = JsonParser.parseString(text).asJsonObject

    private fun createBreakdown(): ProtocolScoreBreakdown {
        val hit = ProtocolDoraIndicatorHit(indicator, redFive, listOf(redFive))
        val dora = ProtocolDoraBreakdown(
            indicatorCount = 1,
            uraCount = 1,
            redCount = 1,
            totalHan = 3,
            indicatorTiles = listOf(indicator),
            uraTiles = listOf(indicator),
            redTiles = listOf(redFive),
            indicatorHits = listOf(hit),
            uraHits = listOf(hit)
        )
        val fu = ProtocolFuBreakdown(
            lines = listOf(ProtocolFuLine("Closed ron", 30)),
            subtotal = 30,
            roundedUpTo = 30,
            finalFu = 30,
            specialNote = "review-note"
        )
        val score = ProtocolScoreResult(
            fu = 30,
            han = 5,
            basePoints = 2000,
            title = "Mangan",
            isYakuman = false,
            yakumanMultiplier = 0,
            rawBasePoints = 3840,
            capReason = "Mangan",
            formulaText = "30 * 2^(2+5)",
            rawBasePointsExact = "3840"
        )
        return ProtocolScoreBreakdown(
            yakuLines = listOf("Riichi" to 1, "Tanyao" to 1),
            yakumanCount = 0,
            dora = dora,
            fu = fu,
            score = score,
            paymentLines = listOf("Seat 0 pays 8000"),
            honbaAddition = 300,
            riichiAddition = 1000,
            totalPoints = 9300,
            uraEligible = true,
            isDealer = false,
            isTsumo = false
        )
    }

    private fun createPublicEvents(): List<ProtocolGameEvent> = listOf(
        ProtocolGameEvent.RonWin(8, 1, 0, redFive, listOf("Riichi", "Tanyao"), 5, 30, 9300, createBreakdown()),
        ProtocolGameEvent.DoraRevealed(8, indicator, true),
        ProtocolGameEvent.ScoreSettled(8, mapOf(0 to -9300, 1 to 9300), finalScores),
        ProtocolGameEvent.RoundEnded(8, 2, 3),
        ProtocolGameEvent.MatchEnded(8, finalScores, 1)
    )

    private fun createPublicSnapshot(cursor: Long = 9L, boardPresent: Boolean = true): ProtocolPublicSnapshot {
        val outcomes = listOf(
            ProtocolRoundOutcome.DealerWin(0),
            ProtocolRoundOutcome.NonDealerWin(2),
            ProtocolRoundOutcome.Ron(1, 0),
            ProtocolRoundOutcome.Tsumo(3),
            ProtocolRoundOutcome.ExhaustiveDraw(linkedSetOf(0, 2)),
            ProtocolRoundOutcome.AbortiveDraw("FOUR_WINDS")
        )
        val records = outcomes.mapIndexed { index, outcome ->
            ProtocolPublicScoreRecord(
                roundId = "round-$index",
                resultId = "result-$index",
                finalScores = finalScores,
                deltas = listOf(-9300, 9300, 0, 0),
                remainingRiichiSticks = 2,
                outcome = outcome
            )
        }
        val board = if (boardPresent) {
            ProtocolPublicBoardView(
                phase = ProtocolGamePhase.ROUND_END,
                roundWind = 2,
                roundNumber = 3,
                honbaSticks = 1,
                riichiSticks = 2,
                dealerSeat = 3,
                currentTurnPlayer = 1,
                wallRemaining = 20,
                doraIndicators = listOf(indicator),
                scores = finalScores,
                riichiStatus = listOf(false, true, false, false),
                discards = List(4) { listOf(redFive) },
                melds = listOf(
                    listOf(ProtocolMeld(ProtocolMeldType.PON, listOf(ProtocolTile.fromTileId(16), ProtocolTile.fromTileId(17), redFive), 1, redFive)),
                    emptyList(),
                    emptyList(),
                    emptyList()
                ),
                handTileCounts = listOf(10, 13, 13, 13),
                lastDiscard = redFive,
                lastDiscardPlayer = 0
            )
        } else null
        return ProtocolPublicSnapshot(
            sessionId = "public-session",
            sessionVersion = 21L,
            roundId = if (boardPresent) "round-6" else null,
            roundVersion = if (boardPresent) 3L else null,
            syncEpoch = "public-epoch",
            cursor = cursor,
            board = board,
            session = ProtocolPublicSessionSummary(
                lifecycle = "FINISHED",
                scores = finalScores,
                roundWind = 2,
                roundNumber = 3,
                dealerSeat = 3,
                honba = 1,
                roundRecords = records,
                finalResult = ProtocolPublicMatchResult(finalScores, listOf(1, 2, 3, 0), "NORMAL_COMPLETION", 1)
            ),
            clocks = listOf(ProtocolClock(1, 100L, 3100L, 2000L, 150L)),
            paused = false,
            settlementRoundId = "round-6",
            settlementEvents = createPublicEvents()
        )
    }

    private fun assertImmutableList(list: List<*>) {
        assertTrue(list.isNotEmpty(), "Use a nonempty list for mutation probe")
        val saved = list.toList()
        val failure = runCatching { (list as MutableList<*>).clear() }.exceptionOrNull()
        assertTrue(failure is UnsupportedOperationException || failure is ClassCastException, "List must reject mutation: $failure")
        assertEquals(saved, list)
    }

    private fun assertImmutableSet(set: Set<*>) {
        assertTrue(set.isNotEmpty(), "Use a nonempty set for mutation probe")
        val saved = set.toSet()
        val failure = runCatching { (set as MutableSet<*>).clear() }.exceptionOrNull()
        assertTrue(failure is UnsupportedOperationException || failure is ClassCastException, "Set must reject mutation: $failure")
        assertEquals(saved, set)
    }

    @Test
    fun `public snapshot roundtrip preserves all history outcomes physical tiles and full scoring`() {
        for (input in listOf(createPublicSnapshot(), createPublicSnapshot(boardPresent = false))) {
            val encoded = ApiMessageCodec.encodePublicSnapshot(input)
            val output = ApiMessageCodec.decodePublicSnapshot(encoded)

            assertEquals(input, output)
            assertEquals(input.hashCode(), output.hashCode())
            assertEquals(input, output.copy())
            assertEquals(finalScores, output.session.scores, "Valid negative scores must survive bankruptcy results")
            assertEquals(input.session.roundRecords.map { it.outcome }, output.session.roundRecords.map { it.outcome })
            assertEquals(List(6) { 2 }, output.session.roundRecords.map { it.remainingRiichiSticks })
            assertEquals(1, output.session.finalResult!!.remainingRiichiSticksTransferredTo)

            val ron = output.settlementEvents.filterIsInstance<ProtocolGameEvent.RonWin>().single()
            assertEquals(redFive.tileId, ron.winTile.tileId)
            assertTrue(ron.winTile.isRedFive)
            assertEquals(createBreakdown(), ron.breakdown)
            assertEquals("3840", ron.breakdown!!.score.rawBasePointsExact)
        }

        val lobby = createPublicSnapshot(boardPresent = false).copy(
            settlementRoundId = null,
            settlementEvents = emptyList(),
            session = createPublicSnapshot().session.copy(
                lifecycle = "WAITING_START",
                roundRecords = emptyList(),
                finalResult = null
            )
        )
        val restoredLobby = ApiMessageCodec.decodePublicSnapshot(ApiMessageCodec.encodePublicSnapshot(lobby))
        assertNull(restoredLobby.board)
        assertEquals("WAITING_START", restoredLobby.session.lifecycle)
        assertTrue(restoredLobby.settlementEvents.isEmpty())
    }

    @Test
    fun `public source collections copies and decoded nested getters remain immutable`() {
        val scores = finalScores.toMutableList()
        val tiles = mutableListOf(ProtocolTile.fromTileId(16), ProtocolTile.fromTileId(17), redFive)
        val river = mutableListOf(redFive)
        val allRivers = MutableList<List<ProtocolTile>>(4) { river }
        val melds = mutableListOf(ProtocolMeld(ProtocolMeldType.PON, tiles, 1, redFive))
        val outcomeSeats = linkedSetOf(0, 2)
        val records = mutableListOf(
            ProtocolPublicScoreRecord(
                "r", "result", scores,
                mutableListOf(-9300, 9300, 0, 0), 2, ProtocolRoundOutcome.ExhaustiveDraw(outcomeSeats)
            )
        )
        val board = createPublicSnapshot().board!!.copy(
            scores = scores,
            discards = allRivers,
            melds = listOf(melds, emptyList(), emptyList(), emptyList())
        )
        val session = createPublicSnapshot().session.copy(scores = scores, roundRecords = records)
        val events = createPublicEvents().toMutableList()
        val clocks = createPublicSnapshot().clocks.toMutableList()
        val constructed = createPublicSnapshot().copy(board = board, session = session, settlementEvents = events, clocks = clocks)
        val wireBefore = ApiMessageCodec.encodePublicSnapshot(constructed)

        scores[0] = 999
        tiles.clear()
        river.clear()
        allRivers.clear()
        melds.clear()
        outcomeSeats.clear()
        records.clear()
        events.clear()
        clocks.clear()

        assertEquals(json(wireBefore), json(ApiMessageCodec.encodePublicSnapshot(constructed)), "Mutating source collections must not alter PUBLIC values")

        for (safe in listOf(constructed, constructed.copy(), ApiMessageCodec.decodePublicSnapshot(wireBefore))) {
            assertEquals(constructed, safe)
            assertEquals(constructed.hashCode(), safe.hashCode())
            assertImmutableList(safe.clocks)
            assertImmutableList(safe.settlementEvents)
            assertNotNull(safe.board)
            val b = safe.board!!
            assertImmutableList(b.scores)
            assertImmutableList(b.handTileCounts)
            assertImmutableList(b.riichiStatus)
            assertImmutableList(b.discards)
            assertImmutableList(b.discards[0])
            assertImmutableList(b.melds)
            assertImmutableList(b.melds[0])
            assertImmutableList(b.melds[0][0].tiles)
            assertImmutableList(safe.session.roundRecords)
            assertImmutableList(safe.session.roundRecords[0].finalScores)
            assertImmutableList(safe.session.roundRecords[0].deltas)

            val seats = (safe.session.roundRecords[0].outcome as ProtocolRoundOutcome.ExhaustiveDraw).tenpaiSeats
            assertImmutableSet(seats)
            assertEquals(setOf(0, 2), seats)

            val detail = (safe.settlementEvents[0] as ProtocolGameEvent.RonWin).breakdown!!
            assertImmutableList(detail.yakuLines)
            assertImmutableList(detail.paymentLines)
            assertImmutableList(detail.fu.lines)
            assertImmutableList(detail.dora.indicatorHits)
            assertImmutableList(detail.dora.indicatorHits[0].tiles)
            assertImmutableList(detail.dora.uraHits)
            assertImmutableList(detail.dora.uraHits[0].tiles)
        }
    }

    @Test
    fun `spectator requests use typed identity and complete recovery checkpoint without leaking token`() {
        val secret = "spectator-private-resume-secret"
        val checkpointJson = """{"sessionId":"public-session","syncEpoch":"public-epoch","cursor":7}"""
        val inputs = listOf(
            """{"api":"JOIN_SPECTATOR","requestId":"wire-join","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"roomId":"room-1","spectatorName":"Observer"}""",
            """{"api":"RESUME_SPECTATOR","requestId":"wire-resume","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"roomId":"room-1","spectatorId":"spec-1","token":"$secret","publicCheckpoint":$checkpointJson}""",
            """{"api":"PUBLIC_SNAPSHOT","requestId":"wire-snap","protocolVersion":${ApiProtocol.PROTOCOL_VERSION}}""",
            """{"api":"RESUME_PUBLIC","requestId":"wire-res-pub","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"publicCheckpoint":$checkpointJson}""",
            """{"api":"QUERY_PUBLIC_ROUND_RESULT","requestId":"wire-qry","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"roundId":"round-1"}"""
        )

        for (input in inputs) {
            val decoded = ApiMessageCodec.decodeRequest(input)
            val again = ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(decoded))
            assertEquals(decoded, again, "Spectator request must survive request codec roundtrip")
            assertFalse(decoded.toString().contains(secret), "Request diagnostics must redact resume token")
            assertEquals(json(input).get("api").asString, decoded.api)
        }

        val resumed = ApiMessageCodec.decodeRequest(inputs[1])
        val encoded = json(ApiMessageCodec.encodeRequest(resumed))
        assertEquals(secret, encoded.get("token").asString, "Wire token must survive; only diagnostics redact it")
        assertEquals(json(checkpointJson), encoded.getAsJsonObject("publicCheckpoint"))
        assertEquals("spec-1", encoded.get("spectatorId").asString)

        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest(json(inputs[0]).apply { addProperty("role", "SPECTATOR") }.toString())
        }
    }

    @Test
    fun `spectator requests reject malformed identity and mixed player authority fields`() {
        val checkpointJson = """{"sessionId":"public-session","syncEpoch":"public-epoch","cursor":7}"""
        val join = json("""{"api":"JOIN_SPECTATOR","requestId":"wire-join","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"roomId":"room-1","spectatorName":"Observer"}""")
        val resume = json("""{"api":"RESUME_SPECTATOR","requestId":"wire-resume","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"roomId":"room-1","spectatorId":"spec-1","token":"private-token","publicCheckpoint":$checkpointJson}""")
        val resumePublic = json("""{"api":"RESUME_PUBLIC","requestId":"wire-res-pub","protocolVersion":${ApiProtocol.PROTOCOL_VERSION},"publicCheckpoint":$checkpointJson}""")

        val bad = mutableListOf<JsonObject>()
        bad += join.deepCopy().apply { addProperty("spectatorName", 7) }
        bad += join.deepCopy().apply { addProperty("spectatorName", " ") }
        bad += join.deepCopy().apply { addProperty("roomId", "") }
        bad += join.deepCopy().apply { addProperty("protocolVersion", 0) }
        bad += resume.deepCopy().apply { addProperty("spectatorId", 7) }
        bad += resume.deepCopy().apply { addProperty("spectatorId", "") }
        bad += resume.deepCopy().apply { addProperty("token", true) }
        bad += resume.deepCopy().apply { addProperty("token", "") }
        bad += resumePublic.deepCopy().apply { remove("publicCheckpoint"); addProperty("cursor", 7) }
        bad += resumePublic.deepCopy().apply { getAsJsonObject("publicCheckpoint").remove("syncEpoch") }
        bad += resumePublic.deepCopy().apply { getAsJsonObject("publicCheckpoint").addProperty("cursor", -1) }

        for (base in listOf(join, resume, resumePublic)) {
            bad += base.deepCopy().apply { addProperty("preferSeat", 0) }
            bad += base.deepCopy().apply {
                add(
                    "choice", JsonParser.parseString(
                        """{"requestId":"forged","roundId":"round-6","decisionId":"private-decision","controlEpoch":1,"optionId":"private-option"}"""
                    )
                )
            }
        }

        for (invalid in bad) {
            assertThrows<JsonParseException>("Malformed or mixed spectator request must be rejected: $invalid") {
                ApiMessageCodec.decodeRequest(invalid.toString())
            }
        }
    }

    @Test
    fun `spectator binding roundtrip redacts token and public responses reject mixed player payload`() {
        val secret = "binding-private-resume-token"
        val binding = SpectatorBinding("room-review", "spectator-review", secret, createPublicSnapshot())
        val response = ApiResponse(requestId = "wire-review", spectatorBinding = binding)
        val resumed = ResumeSpectatorRequest(
            binding.roomId, binding.spectatorId, secret,
            PublicCheckpoint(binding.snapshot.sessionId, binding.snapshot.syncEpoch, binding.snapshot.cursor)
        )

        assertFalse(binding.toString().contains(secret))
        assertFalse(resumed.toString().contains(secret))
        assertFalse(response.toString().contains(secret))

        val restored = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(response))
        assertEquals(binding, restored.spectatorBinding)
        assertEquals(secret, restored.spectatorBinding!!.resumeToken)

        val publicOnly = ApiResponse(publicSnapshot = createPublicSnapshot())
        assertEquals(publicOnly, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(publicOnly)))

        val legacy = ApiResponse(
            seat = 0,
            resumeToken = "legacy-player-resume-token",
            capabilities = ProtocolAccessCapabilities(protocolVersion = ApiProtocol.PROTOCOL_VERSION),
            events = listOf(ProtocolGameEvent.TileDrawn(1, 0, redFive, 20))
        )
        assertEquals(
            legacy, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(legacy)),
            "Existing legal PLAYER responses must remain valid without PUBLIC fields"
        )

        val playerPayload = json(ApiMessageCodec.encodeResponse(legacy))
        for (key in listOf("seat", "resumeToken", "capabilities", "events")) {
            for (base in listOf(response, publicOnly, ApiResponse(publicEvents = createPublicEvents()))) {
                val mixed = json(ApiMessageCodec.encodeResponse(base)).apply { add(key, playerPayload.get(key).deepCopy()) }
                assertThrows<JsonParseException>("PUBLIC and PLAYER $key response fields cannot mix") {
                    ApiMessageCodec.decodeResponse(mixed.toString())
                }
            }
        }

        val room = ProtocolRoomInfo("room-review", ProtocolRoomLifecycle.LOBBY, version = 2L)
        val roomResponse = ApiResponse(
            roomInfo = room,
            receipt = ProtocolActionReceipt("room-review", ProtocolReceiptStatus.APPLIED)
        )
        assertEquals(
            roomResponse, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(roomResponse)),
            "Public room information and receipts are not private player payload"
        )
    }

    @Test
    fun `public events whitelist applies to snapshot update and standalone result response`() {
        val good = createPublicSnapshot()
        val goodUpdate = ProtocolPublicUpdate(good, 8L, createPublicEvents(), false)
        val privateEvents = listOf(
            ProtocolGameEvent.TileDealt(1, 0, redFive),
            ProtocolGameEvent.TileDrawn(1, 0, redFive, 20),
            ProtocolGameEvent.RinshanDraw(1, 0, redFive),
            ProtocolGameEvent.CheatPointsChanged(1, 0, -5, 15)
        )

        for (privateEvent in privateEvents) {
            val eventJson = json(ApiMessageCodec.encodeResponse(ApiResponse(events = listOf(privateEvent))))
                .getAsJsonArray("events")
            val badSnapshot = json(ApiMessageCodec.encodePublicSnapshot(good)).apply { add("settlementEvents", eventJson.deepCopy()) }
            val badUpdate = json(ApiMessageCodec.encodePublicUpdatePush(goodUpdate)).apply {
                getAsJsonObject("update").add("visibleEvents", eventJson.deepCopy())
            }
            val badResult = JsonObject().apply { addProperty("success", true); add("publicEvents", eventJson.deepCopy()) }

            assertThrows<JsonParseException>("Private ${privateEvent.eventType} in public snapshot must be rejected") {
                ApiMessageCodec.decodePublicSnapshot(badSnapshot.toString())
            }
            assertThrows<JsonParseException>("Private ${privateEvent.eventType} in public update must be rejected") {
                ApiMessageCodec.decodePublicUpdatePush(badUpdate.toString())
            }
            assertThrows<JsonParseException>("Private ${privateEvent.eventType} in standalone result must be rejected") {
                ApiMessageCodec.decodeResponse(badResult.toString())
            }
            assertThrows<IllegalArgumentException> { good.copy(settlementEvents = listOf(privateEvent)) }
            assertThrows<IllegalArgumentException> { goodUpdate.copy(visibleEvents = listOf(privateEvent)) }
        }

        val response = ApiResponse(publicEvents = createPublicEvents())
        val restored = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(response))
        assertEquals(createPublicEvents(), restored.publicEvents, "Whitelisting must preserve real public wins and full scoring")

        val unknown = json(ApiMessageCodec.encodeResponse(response))
        unknown.getAsJsonArray("publicEvents")[0].asJsonObject.addProperty("eventType", "FuturePrivateEvent")
        assertThrows<JsonParseException>("Unknown event cannot be presumed public") {
            ApiMessageCodec.decodeResponse(unknown.toString())
        }
    }

    @Test
    fun `standalone public result response defensively copies and protects its event list`() {
        val source = createPublicEvents().toMutableList()
        val response = ApiResponse(publicEvents = source)
        val before = ApiMessageCodec.encodeResponse(response)
        source.clear()
        assertEquals(json(before), json(ApiMessageCodec.encodeResponse(response)), "ApiResponse.publicEvents must copy its input")
        for (result in listOf(response, response.copy(), ApiMessageCodec.decodeResponse(before))) {
            assertNotNull(result.publicEvents)
            assertImmutableList(result.publicEvents!!)
            assertEquals(createPublicEvents(), result.publicEvents)
        }
    }

    @Test
    fun `public snapshot rejects unknown fields and private nested supersets at every boundary`() {
        val valid = json(ApiMessageCodec.encodePublicSnapshot(createPublicSnapshot()))
        val targets: List<Pair<String, (JsonObject) -> JsonObject>> = listOf(
            "root" to { it },
            "board" to { it.getAsJsonObject("board") },
            "session" to { it.getAsJsonObject("session") },
            "record" to { it.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject },
            "outcome" to { it.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject.getAsJsonObject("outcome") },
            "result" to { it.getAsJsonObject("session").getAsJsonObject("finalResult") },
            "breakdown" to { it.getAsJsonArray("settlementEvents")[0].asJsonObject.getAsJsonObject("breakdown") }
        )
        for ((name, target) in targets) {
            val invalid = valid.deepCopy()
            target(invalid).addProperty("privateReviewMarker", "must-not-be-ignored")
            assertThrows<JsonParseException>("Unknown field inside $name must be rejected") {
                ApiMessageCodec.decodePublicSnapshot(invalid.toString())
            }
        }
        for (key in listOf(
            "myClosedTiles", "myDrawnTile", "sleeveTiles", "wall", "uraDoraIndicators",
            "decision", "options", "presentationPreview", "controls", "cheatPoints", "preparedSelection"
        )) {
            val invalid = valid.deepCopy()
            invalid.getAsJsonObject("board").add(key, JsonParser.parseString("[]"))
            assertThrows<JsonParseException>("Player/private field $key must not be ignored in PUBLIC board") {
                ApiMessageCodec.decodePublicSnapshot(invalid.toString())
            }
        }
        for (key in listOf("finalCheatPoints", "cheatDeltas", "cheatEntries")) {
            val invalid = valid.deepCopy()
            invalid.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject
                .add(key, JsonParser.parseString("[1,2,3,4]"))
            assertThrows<JsonParseException>("Private history field $key must be rejected") {
                ApiMessageCodec.decodePublicSnapshot(invalid.toString())
            }
        }
    }

    @Test
    fun `public snapshot strictly validates metadata enums seat ranks counts and physical tiles`() {
        val valid = json(ApiMessageCodec.encodePublicSnapshot(createPublicSnapshot()))
        val corruptions: List<(JsonObject) -> Unit> = listOf(
            { it.remove("sessionId") },
            { it.addProperty("sessionId", 1) },
            { it.addProperty("syncEpoch", " ") },
            { it.addProperty("cursor", -1) },
            { it.addProperty("cursor", "9") },
            { it.addProperty("sessionVersion", -1) },
            { it.addProperty("roundVersion", 1.5) },
            { it.addProperty("paused", "false") },
            { it.getAsJsonObject("board").addProperty("phase", "INVALID") },
            { it.getAsJsonObject("board").addProperty("roundWind", 0) },
            { it.getAsJsonObject("board").addProperty("roundNumber", -1) },
            { it.getAsJsonObject("board").addProperty("dealerSeat", 4) },
            { it.getAsJsonObject("board").add("handTileCounts", JsonParser.parseString("[13,13,-1,13]")) },
            { it.getAsJsonObject("board").add("scores", JsonParser.parseString("[1,2,3]")) },
            { it.getAsJsonObject("board").getAsJsonArray("doraIndicators")[0].asJsonObject.addProperty("rank", 9) },
            { it.getAsJsonObject("session").addProperty("lifecycle", "PLAYING") },
            { it.getAsJsonObject("session").getAsJsonObject("finalResult").addProperty("reason", "GUESSED_FINISH") },
            { it.getAsJsonObject("session").getAsJsonObject("finalResult").add("rankedSeats", JsonParser.parseString("[0,0,1,2]")) },
            { it.getAsJsonObject("session").getAsJsonObject("finalResult").addProperty("remainingRiichiSticksTransferredTo", 4) },
            { it.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject.getAsJsonObject("outcome").addProperty("winner", 4) }
        )
        for ((index, corrupt) in corruptions.withIndex()) {
            val invalid = valid.deepCopy()
            corrupt(invalid)
            assertThrows<JsonParseException>("Malformed public snapshot variant $index must be rejected: $invalid") {
                ApiMessageCodec.decodePublicSnapshot(invalid.toString())
            }
        }
    }

    @Test
    fun `public update enforces continuous or full baseline semantics and explicit boolean on wire`() {
        val regular = ProtocolPublicUpdate(createPublicSnapshot(9L), 8L, createPublicEvents(), false)
        val full = ProtocolPublicUpdate(createPublicSnapshot(Long.MAX_VALUE), Long.MAX_VALUE, createPublicEvents(), true)

        for (input in listOf(regular, full)) {
            val encoded = json(ApiMessageCodec.encodePublicUpdatePush(input))
            assertEquals("PUBLIC_UPDATE", encoded.get("type").asString)
            assertTrue(encoded.getAsJsonObject("update").get("isFullSnapshot").asJsonPrimitive.isBoolean)
            assertEquals(input, ApiMessageCodec.decodePublicUpdatePush(encoded.toString()))
            assertEquals(input, input.copy())
        }

        val valid = json(ApiMessageCodec.encodePublicUpdatePush(regular))
        val mutations: List<(JsonObject) -> Unit> = listOf(
            { it.remove("isFullSnapshot") },
            { it.addProperty("isFullSnapshot", "false") },
            { it.addProperty("isFullSnapshot", 0) },
            { it.addProperty("fromCursor", -1) },
            { it.addProperty("fromCursor", 7) },
            { it.addProperty("fromCursor", 9) },
            { it.addProperty("isFullSnapshot", true) },
            { it.addProperty("unknown", true) }
        )
        for (mutate in mutations) {
            val invalid = valid.deepCopy()
            mutate(invalid.getAsJsonObject("update"))
            assertThrows<JsonParseException>("Invalid public update must be rejected: $invalid") {
                ApiMessageCodec.decodePublicUpdatePush(invalid.toString())
            }
        }

        for (badType in listOf("ROOM_UPDATE", "PLAYER_UPDATE", "UNKNOWN")) {
            assertThrows<JsonParseException>("Wrong public PUSH type $badType must be rejected") {
                ApiMessageCodec.decodePublicUpdatePush(valid.deepCopy().apply { addProperty("type", badType) }.toString())
            }
        }

        assertThrows<IllegalArgumentException> { ProtocolPublicUpdate(createPublicSnapshot(9), 7, isFullSnapshot = false) }
        assertThrows<IllegalArgumentException> { ProtocolPublicUpdate(createPublicSnapshot(9), 8, isFullSnapshot = true) }
        assertThrows<IllegalArgumentException> { ProtocolPublicUpdate(createPublicSnapshot(0), Long.MAX_VALUE, isFullSnapshot = false) }
    }

    @Test
    fun `public resend preserves complete checkpoint including long boundaries and rejects malformed payloads`() {
        fun checkpointJson(cursor: String = "7") =
            """{"sessionId":"public-session","syncEpoch":"public-epoch","cursor":$cursor}"""

        fun resendJson(cp: String = checkpointJson()) =
            """{"type":"PUBLIC_RESEND","checkpoint":$cp}"""

        for (cursor in listOf(0L, 7L, Long.MAX_VALUE)) {
            val cp = PublicCheckpoint("public-session", "public-epoch", cursor)
            val encoded = ApiMessageCodec.encodePublicResendRequest(cp)
            val decoded = ApiMessageCodec.decodePublicResendRequest(encoded)
            assertEquals(cp, decoded)
            assertEquals(cursor, decoded.cursor)
            assertEquals("public-session", decoded.sessionId)
            assertEquals("public-epoch", decoded.syncEpoch)

            val fromWire = ApiMessageCodec.decodePublicResendRequest(resendJson(checkpointJson(cursor.toString())))
            assertEquals(cp, fromWire)
        }

        val invalidEnvelopes = listOf(
            """{"fromCursor":7}""",
            """{"type":"PUBLIC_RESEND","cursor":7}""",
            """{"type":"RESEND","checkpoint":${checkpointJson()}}""",
            """{"type":7,"checkpoint":${checkpointJson()}}""",
            """{"checkpoint":${checkpointJson()}}""",
            """{"type":"PUBLIC_RESEND"}""",
            """{"type":"PUBLIC_RESEND","checkpoint":null}""",
            """{"type":"PUBLIC_RESEND","checkpoint":${checkpointJson()},"unexpected":true}""",
            "[]",
            "null",
            "not-json"
        )
        for (invalid in invalidEnvelopes) {
            assertThrows<JsonParseException>("Invalid PUBLIC_RESEND envelope must be rejected: $invalid") {
                ApiMessageCodec.decodePublicResendRequest(invalid)
            }
        }

        val invalidCheckpoints = mutableListOf<String>()
        for (key in listOf("sessionId", "syncEpoch", "cursor")) {
            invalidCheckpoints += json(checkpointJson()).apply { remove(key) }.toString()
        }
        for (key in listOf("sessionId", "syncEpoch")) {
            for (bad in listOf("null", "true", "23", "\"\"", "\"   \"", "[]", "{}")) {
                invalidCheckpoints += json(checkpointJson()).apply { add(key, JsonParser.parseString(bad)) }.toString()
            }
        }
        for (bad in listOf("-1", "\"7\"", "1.5", "1e1", "9223372036854775808", "true", "null", "[]", "{}")) {
            invalidCheckpoints += checkpointJson(bad)
        }
        invalidCheckpoints += json(checkpointJson()).apply { addProperty("seat", 0) }.toString()

        for (invalidCp in invalidCheckpoints) {
            assertThrows<JsonParseException>("Invalid PUBLIC checkpoint must be rejected: $invalidCp") {
                ApiMessageCodec.decodePublicResendRequest(resendJson(invalidCp))
            }
        }
    }

    @Test
    fun `push classification separates public room and legacy player and rejects invalid type envelopes`() {
        val action = ProtocolLegalAction.DiscardTile(0, redFive)
        val playerView = ProtocolPlayerView(
            viewerSeat = 0, phase = ProtocolGamePhase.DISCARD, roundWind = 1, roundNumber = 0,
            honbaSticks = 0, riichiSticks = 0, dealerSeat = 0, currentTurnPlayer = 0, wallRemaining = 70,
            doraIndicators = listOf(indicator), scores = List(4) { 25000 }, riichiStatus = List(4) { false },
            discards = List(4) { emptyList() }, melds = List(4) { emptyList() },
            handTileCounts = List(4) { 13 }, myClosedTiles = listOf(redFive), myDrawnTile = null,
            legalActions = listOf(action)
        )
        val playerSnapshot = ProtocolPlayerSnapshot(
            sessionId = "player-session", roundId = "player-round", syncEpoch = "player-epoch", cursor = 4L,
            sessionVersion = 1L, roundVersion = 1L, view = playerView,
            decision = ProtocolPlayerDecision(
                "player-round", "player-decision", 2L,
                listOf(ProtocolActionOption("discard-red-five", action))
            )
        )
        val playerUpdate = ProtocolPlayerUpdate(playerSnapshot, 4L, isFullSnapshot = true)
        val playerWire = ApiMessageCodec.encodePush(playerUpdate)
        assertFalse(json(playerWire).has("type"), "Keep existing V4 untyped PLAYER encoding as regression baseline")
        assertEquals(playerUpdate, ApiMessageCodec.decodePush(playerWire))

        val roomInfo = ProtocolRoomInfo("room-review", ProtocolRoomLifecycle.LOBBY, version = 2L)
        val roomWire = ApiMessageCodec.encodeRoomUpdatePush(roomInfo)
        assertEquals(roomInfo, ApiMessageCodec.decodeRoomUpdatePush(roomWire))

        val publicUpdate = ProtocolPublicUpdate(createPublicSnapshot(), 9L, createPublicEvents(), true)
        val publicWire = ApiMessageCodec.encodePublicUpdatePush(publicUpdate)
        assertEquals(publicUpdate, ApiMessageCodec.decodePublicUpdatePush(publicWire))

        assertEquals(PushType.PLAYER, ApiMessageCodec.classifyPush(playerWire))
        assertEquals(PushType.ROOM, ApiMessageCodec.classifyPush(roomWire))
        assertEquals(PushType.PUBLIC, ApiMessageCodec.classifyPush(publicWire))
        assertFalse(ApiMessageCodec.isRoomUpdateEnvelope(playerWire))
        assertTrue(ApiMessageCodec.isRoomUpdateEnvelope(roomWire))
        assertFalse(ApiMessageCodec.isRoomUpdateEnvelope(publicWire), "PUBLIC must not enter ROOM decoder route")

        for (invalidType in listOf("\"UNKNOWN_UPDATE\"", "\"public_update\"", "3", "true", "null", "[]", "{}")) {
            val malformed = json(publicWire).apply { add("type", JsonParser.parseString(invalidType)) }
            assertThrows<JsonParseException>("Invalid PUSH type cannot fall back to PLAYER or ROOM: $malformed") {
                ApiMessageCodec.classifyPush(malformed.toString())
            }
        }
        for (malformed in listOf("[]", "null", "23", "\"ROOM_UPDATE\"", "{\"type\":\"PUBLIC_UPDATE\"", "{}")) {
            assertThrows<JsonParseException>("PUSH classifier must reject malformed or non-object envelopes: $malformed") {
                ApiMessageCodec.classifyPush(malformed)
            }
        }
    }
}

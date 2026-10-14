package com.majiang.v4lobby

import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.*
import org.junit.jupiter.api.Test
import java.lang.reflect.InvocationTargetException
import kotlin.test.*

/** Protocol-only independent probes. No engine/client/server implementation or module-test fixture. */
class V4PublicWireAcceptanceTest {
    private fun json(text: String): JsonObject = JsonParser.parseString(text).asJsonObject

    /** New frozen codec entry points are found without importing not-yet-landed public DTO classes. */
    private fun codec(methodName: String, argument: Any): Any {
        val method = ApiMessageCodec::class.java.methods.singleOrNull {
            it.name == methodName && it.parameterCount == 1 && it.parameterTypes[0].isInstance(argument)
        }
        assertNotNull(method, "The real ApiMessageCodec must implement $methodName(${argument.javaClass.simpleName})")
        return try {
            assertNotNull(method.invoke(ApiMessageCodec, argument), "$methodName returned null")
        } catch (failure: InvocationTargetException) {
            throw (failure.targetException ?: failure)
        }
    }

    private fun field(value: Any, name: String): Any? {
        val getter = value.javaClass.methods.singleOrNull {
            it.name == "get${name.replaceFirstChar { c -> c.uppercaseChar() }}" && it.parameterCount == 0
        }
        assertNotNull(getter, "Missing public field ${value.javaClass.simpleName}.$name")
        return getter.invoke(value)
    }

    private fun checkpointJson(cursor: String = "7") =
        """{"sessionId":"public-session","syncEpoch":"public-epoch","cursor":$cursor}"""

    private fun resend(checkpoint: String = checkpointJson()) =
        """{"type":"PUBLIC_RESEND","checkpoint":$checkpoint}"""

    private fun rejectJson(label: String, block: () -> Unit) {
        assertFailsWith<JsonParseException>(label, block)
    }

    @Test fun publicResendPreservesTheCompleteCheckpointIncludingLongBoundaries() {
        for (cursor in listOf(0L, 7L, Long.MAX_VALUE)) {
            val input = resend(checkpointJson(cursor.toString()))
            val checkpoint = codec("decodePublicResendRequest", input)
            assertEquals("public-session", field(checkpoint, "sessionId"))
            assertEquals("public-epoch", field(checkpoint, "syncEpoch"))
            assertEquals(cursor, field(checkpoint, "cursor"))
            val encoded = codec("encodePublicResendRequest", checkpoint) as String
            assertEquals(json(input), json(encoded), "PUBLIC_RESEND must retain all three checkpoint fields")
            val decodedAgain = codec("decodePublicResendRequest", encoded)
            assertEquals(checkpoint, decodedAgain)
            assertEquals(checkpoint.hashCode(), decodedAgain.hashCode())
        }
    }

    @Test fun publicResendRejectsBareCursorsUnknownFieldsAndNonExactCheckpointValues() {
        val invalidEnvelopes = listOf(
            """{"fromCursor":7}""", """{"type":"PUBLIC_RESEND","cursor":7}""",
            """{"type":"RESEND","checkpoint":${checkpointJson()}}""",
            """{"type":7,"checkpoint":${checkpointJson()}}""",
            """{"checkpoint":${checkpointJson()}}""",
            """{"type":"PUBLIC_RESEND"}""",
            """{"type":"PUBLIC_RESEND","checkpoint":null}""",
            """{"type":"PUBLIC_RESEND","checkpoint":${checkpointJson()},"unexpected":true}""",
            "[]", "null", "not-json")
        for (invalid in invalidEnvelopes) {
            rejectJson("Invalid PUBLIC_RESEND envelope: $invalid") { codec("decodePublicResendRequest", invalid) }
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
        for (invalid in invalidCheckpoints) {
            rejectJson("Invalid PUBLIC checkpoint: $invalid") { codec("decodePublicResendRequest", resend(invalid)) }
        }
    }

    private fun request(api: String, extra: String = "") =
        """{"api":"$api","requestId":"wire-review","protocolVersion":${ApiProtocol.PROTOCOL_VERSION}$extra}"""

    @Test fun spectatorRequestsUseTypedIdentityAndCompleteRecoveryCheckpointWithoutLeakingTokenToLogs() {
        val secret = "spectator-private-resume-secret"
        val inputs = listOf(
            request("JOIN_SPECTATOR", """, "roomId":"room-review", "spectatorName":"Observer""""),
            request("RESUME_SPECTATOR", """, "roomId":"room-review", "spectatorId":"spectator-review", "token":"$secret", "publicCheckpoint":${checkpointJson()}"""),
            request("PUBLIC_SNAPSHOT"),
            request("RESUME_PUBLIC", """, "publicCheckpoint":${checkpointJson()}"""),
            request("QUERY_PUBLIC_ROUND_RESULT", """, "roundId":"round-review""""))
        for (input in inputs) {
            val decoded = ApiMessageCodec.decodeRequest(input)
            val again = ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(decoded))
            assertEquals(decoded, again, "Spectator request must survive the actual request codec")
            assertFalse(decoded.toString().contains(secret), "Request diagnostics must redact resume token")
            assertEquals(json(input).get("api").asString, decoded.api)
        }
        val resumed = ApiMessageCodec.decodeRequest(inputs[1])
        val encoded = json(ApiMessageCodec.encodeRequest(resumed))
        assertEquals(secret, encoded.get("token").asString, "Wire token must survive; only diagnostics redact it")
        assertEquals(json(checkpointJson()), encoded.getAsJsonObject("publicCheckpoint"))
        assertEquals("spectator-review", encoded.get("spectatorId").asString)
        rejectJson("Clients cannot self-assert a spectator role") {
            ApiMessageCodec.decodeRequest(json(inputs[0]).apply { addProperty("role", "SPECTATOR") }.toString())
        }
    }

    @Test fun spectatorRequestsRejectMalformedIdentityAndMixedPlayerAuthorityFields() {
        val join = json(request("JOIN_SPECTATOR", """, "roomId":"room-review", "spectatorName":"Observer""""))
        val resume = json(request("RESUME_SPECTATOR", """, "roomId":"room-review", "spectatorId":"spectator-review", "token":"private-token", "publicCheckpoint":${checkpointJson()}"""))
        val resumePublic = json(request("RESUME_PUBLIC", """, "publicCheckpoint":${checkpointJson()}"""))
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
            bad += base.deepCopy().apply { add("choice", JsonParser.parseString(
                """{"requestId":"forged","roundId":"round-6","decisionId":"private-decision","controlEpoch":1,"optionId":"private-option"}""")) }
        }
        for (invalid in bad) rejectJson("Malformed or mixed spectator request: $invalid") {
            ApiMessageCodec.decodeRequest(invalid.toString())
        }
    }

    private val redFive = ProtocolTile.fromTileId(19)
    private val indicator = ProtocolTile.fromTileId(12)
    private val finalScores = listOf(-500, 35500, 32500, 32500)

    private fun breakdown() = ProtocolScoreBreakdown(
        yakuLines = listOf("Riichi" to 1, "Tanyao" to 1), yakumanCount = 0,
        dora = ProtocolDoraBreakdown(1, 1, 1, 3, listOf(redFive), listOf(redFive), listOf(redFive),
            listOf(ProtocolDoraIndicatorHit(indicator, redFive, listOf(redFive))),
            listOf(ProtocolDoraIndicatorHit(indicator, redFive, listOf(redFive)))),
        fu = ProtocolFuBreakdown(listOf(ProtocolFuLine("Closed ron", 30)), 30, 30, 30, "review-note"),
        score = ProtocolScoreResult(30, 5, 2000, "Mangan", false, 0,
            rawBasePoints = 3840, capReason = "Mangan", formulaText = "30 * 2^(2+5)", rawBasePointsExact = "3840"),
        paymentLines = listOf("Seat 0 pays 8000"), honbaAddition = 300, riichiAddition = 1000,
        totalPoints = 9300, uraEligible = true, isDealer = false, isTsumo = false)

    private fun publicEvents(): List<ProtocolGameEvent> = listOf(
        ProtocolGameEvent.RonWin(8, 1, 0, redFive, listOf("Riichi", "Tanyao"), 5, 30, 9300, breakdown()),
        ProtocolGameEvent.DoraRevealed(8, indicator, true),
        ProtocolGameEvent.ScoreSettled(8, mapOf(0 to -9300, 1 to 9300), finalScores),
        ProtocolGameEvent.RoundEnded(8, 2, 3),
        ProtocolGameEvent.MatchEnded(8, finalScores, 1))

    private fun snapshot(cursor: Long = 9L, boardPresent: Boolean = true): ProtocolPublicSnapshot {
        val outcomes = listOf(
            ProtocolRoundOutcome.DealerWin(0), ProtocolRoundOutcome.NonDealerWin(2),
            ProtocolRoundOutcome.Ron(1, 0), ProtocolRoundOutcome.Tsumo(3),
            ProtocolRoundOutcome.ExhaustiveDraw(linkedSetOf(0, 2)), ProtocolRoundOutcome.AbortiveDraw("FOUR_WINDS"))
        val records = outcomes.mapIndexed { index, outcome ->
            ProtocolPublicScoreRecord("round-$index", "result-$index", finalScores,
                listOf(-9300, 9300, 0, 0), 2, outcome)
        }
        val board = if (boardPresent) ProtocolPublicBoardView(
            ProtocolGamePhase.ROUND_END, 2, 3, 1, 2, 3, 1, 20,
            listOf(indicator), finalScores, listOf(false, true, false, false),
            List(4) { listOf(redFive) },
            listOf(listOf(ProtocolMeld(ProtocolMeldType.PON,
                listOf(ProtocolTile.fromTileId(16), ProtocolTile.fromTileId(17), redFive), 1, redFive)),
                emptyList(), emptyList(), emptyList()),
            listOf(10, 13, 13, 13), redFive, 0) else null
        return ProtocolPublicSnapshot(
            sessionId = "public-session", sessionVersion = 21L,
            roundId = if (boardPresent) "round-6" else null, roundVersion = if (boardPresent) 3L else null,
            syncEpoch = "public-epoch", cursor = cursor, board = board,
            session = ProtocolPublicSessionSummary("FINISHED", finalScores, 2, 3, 3, 1, records,
                ProtocolPublicMatchResult(finalScores, listOf(1, 2, 3, 0), "NORMAL_COMPLETION", 1)),
            clocks = listOf(ProtocolClock(1, 100L, 3100L, 2000L, 150L)), paused = false,
            settlementRoundId = "round-6", settlementEvents = publicEvents())
    }

    private fun encodeSnapshot(value: ProtocolPublicSnapshot) = codec("encodePublicSnapshot", value) as String
    private fun decodeSnapshot(text: String) = codec("decodePublicSnapshot", text) as ProtocolPublicSnapshot
    private fun encodeUpdate(value: ProtocolPublicUpdate) = codec("encodePublicUpdatePush", value) as String
    private fun decodeUpdate(text: String) = codec("decodePublicUpdatePush", text) as ProtocolPublicUpdate

    @Test fun publicSnapshotRoundTripPreservesAllHistoryOutcomesPhysicalTilesAndFullScoring() {
        for (input in listOf(snapshot(), snapshot(boardPresent = false))) {
            val output = decodeSnapshot(encodeSnapshot(input))
            assertEquals(input, output)
            assertEquals(input.hashCode(), output.hashCode())
            assertEquals(input, output.copy())
            assertEquals(finalScores, output.session.scores, "Valid negative scores must survive bankruptcy results")
            assertEquals(input.session.roundRecords.map { it.outcome }, output.session.roundRecords.map { it.outcome })
            assertEquals(List(6) { 2 }, output.session.roundRecords.map { it.remainingRiichiSticks })
            assertEquals(1, output.session.finalResult!!.remainingRiichiSticksTransferredTo)
            val ron = output.settlementEvents.filterIsInstance<ProtocolGameEvent.RonWin>().single()
            assertEquals(redFive.tileId, ron.winTile.tileId)
            assertEquals(breakdown(), ron.breakdown)
            assertEquals("3840", ron.breakdown!!.score.rawBasePointsExact)
        }
        val lobby = snapshot(boardPresent = false).copy(settlementRoundId = null, settlementEvents = emptyList(),
            session = snapshot().session.copy(lifecycle = "WAITING_START", roundRecords = emptyList(), finalResult = null))
        val restoredLobby = decodeSnapshot(encodeSnapshot(lobby))
        assertNull(restoredLobby.board)
        assertEquals("WAITING_START", restoredLobby.session.lifecycle)
        assertTrue(restoredLobby.settlementEvents.isEmpty())
    }

    @Test fun publicUpdateEnforcesContinuousOrFullBaselineSemanticsAndExplicitBooleanOnWire() {
        val regular = ProtocolPublicUpdate(snapshot(9L), 8L, publicEvents(), false)
        val full = ProtocolPublicUpdate(snapshot(Long.MAX_VALUE), Long.MAX_VALUE, publicEvents(), true)
        for (input in listOf(regular, full)) {
            val encoded = json(encodeUpdate(input))
            assertEquals("PUBLIC_UPDATE", encoded.get("type").asString)
            assertTrue(encoded.getAsJsonObject("update").get("isFullSnapshot").asJsonPrimitive.isBoolean)
            assertEquals(input, decodeUpdate(encoded.toString()))
            assertEquals(input, input.copy())
        }
        val valid = json(encodeUpdate(regular))
        for (mutate in listOf<(JsonObject) -> Unit>(
            { it.remove("isFullSnapshot") }, { it.addProperty("isFullSnapshot", "false") },
            { it.addProperty("isFullSnapshot", 0) }, { it.addProperty("fromCursor", -1) },
            { it.addProperty("fromCursor", 7) }, { it.addProperty("fromCursor", 9) },
            { it.addProperty("isFullSnapshot", true) }, { it.addProperty("unknown", true) })) {
            val invalid = valid.deepCopy()
            mutate(invalid.getAsJsonObject("update"))
            rejectJson("Invalid public update: $invalid") { decodeUpdate(invalid.toString()) }
        }
        for (badType in listOf("ROOM_UPDATE", "PLAYER_UPDATE", "UNKNOWN")) {
            rejectJson("Wrong public PUSH type $badType") {
                decodeUpdate(valid.deepCopy().apply { addProperty("type", badType) }.toString())
            }
        }
        assertFailsWith<IllegalArgumentException> { ProtocolPublicUpdate(snapshot(9), 7, isFullSnapshot = false) }
        assertFailsWith<IllegalArgumentException> { ProtocolPublicUpdate(snapshot(9), 8, isFullSnapshot = true) }
        assertFailsWith<IllegalArgumentException> { ProtocolPublicUpdate(snapshot(0), Long.MAX_VALUE, isFullSnapshot = false) }
    }

    @Test fun publicSnapshotRejectsUnknownFieldsAndPrivateNestedSupersetsAtEveryBoundary() {
        val valid = json(encodeSnapshot(snapshot()))
        val targets: List<Pair<String, (JsonObject) -> JsonObject>> = listOf(
            "root" to { it }, "board" to { it.getAsJsonObject("board") },
            "session" to { it.getAsJsonObject("session") },
            "record" to { it.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject },
            "outcome" to { it.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject.getAsJsonObject("outcome") },
            "result" to { it.getAsJsonObject("session").getAsJsonObject("finalResult") },
            "breakdown" to { it.getAsJsonArray("settlementEvents")[0].asJsonObject.getAsJsonObject("breakdown") })
        for ((name, target) in targets) {
            val invalid = valid.deepCopy()
            target(invalid).addProperty("privateReviewMarker", "must-not-be-ignored")
            rejectJson("Unknown field inside $name") { decodeSnapshot(invalid.toString()) }
        }
        for (key in listOf("myClosedTiles", "myDrawnTile", "sleeveTiles", "wall", "uraDoraIndicators",
            "decision", "options", "presentationPreview", "controls", "cheatPoints", "preparedSelection")) {
            val invalid = valid.deepCopy()
            invalid.getAsJsonObject("board").add(key, JsonParser.parseString("[]"))
            rejectJson("Player/private field $key must not be ignored in PUBLIC board") { decodeSnapshot(invalid.toString()) }
        }
        for (key in listOf("finalCheatPoints", "cheatDeltas", "cheatEntries")) {
            val invalid = valid.deepCopy()
            invalid.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject
                .add(key, JsonParser.parseString("[1,2,3,4]"))
            rejectJson("Private history field $key") { decodeSnapshot(invalid.toString()) }
        }
    }

    @Test fun publicSnapshotStrictlyValidatesMetadataEnumsSeatRanksCountsAndPhysicalTiles() {
        val valid = json(encodeSnapshot(snapshot()))
        val corruptions: List<(JsonObject) -> Unit> = listOf(
            { it.remove("sessionId") }, { it.addProperty("sessionId", 1) }, { it.addProperty("syncEpoch", " ") },
            { it.addProperty("cursor", -1) }, { it.addProperty("cursor", "9") },
            { it.addProperty("sessionVersion", -1) }, { it.addProperty("roundVersion", 1.5) },
            { it.addProperty("paused", "false") }, { it.getAsJsonObject("board").addProperty("phase", "INVALID") },
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
            { it.getAsJsonObject("session").getAsJsonArray("roundRecords")[0].asJsonObject.getAsJsonObject("outcome").addProperty("winner", 4) })
        for ((index, corrupt) in corruptions.withIndex()) {
            val invalid = valid.deepCopy()
            corrupt(invalid)
            rejectJson("Malformed public snapshot variant $index: $invalid") { decodeSnapshot(invalid.toString()) }
        }
    }

    private fun privateEvents(): List<ProtocolGameEvent> = listOf(
        ProtocolGameEvent.TileDealt(1, 0, redFive), ProtocolGameEvent.TileDrawn(1, 0, redFive, 20),
        ProtocolGameEvent.RinshanDraw(1, 0, redFive),
        ProtocolGameEvent.CheatPointsChanged(1, 0, -5, 15))

    @Test fun publicEventsWhitelistAppliesToSnapshotUpdateAndStandaloneResultResponse() {
        val good = snapshot()
        val goodUpdate = ProtocolPublicUpdate(good, 8L, publicEvents(), false)
        for (privateEvent in privateEvents()) {
            val eventJson = json(ApiMessageCodec.encodeResponse(ApiResponse(events = listOf(privateEvent))))
                .getAsJsonArray("events")
            val badSnapshot = json(encodeSnapshot(good)).apply { add("settlementEvents", eventJson.deepCopy()) }
            val badUpdate = json(encodeUpdate(goodUpdate)).apply {
                getAsJsonObject("update").add("visibleEvents", eventJson.deepCopy())
            }
            val badResult = JsonObject().apply { addProperty("success", true); add("publicEvents", eventJson.deepCopy()) }
            rejectJson("Private ${privateEvent.eventType} in public snapshot") { decodeSnapshot(badSnapshot.toString()) }
            rejectJson("Private ${privateEvent.eventType} in public update") { decodeUpdate(badUpdate.toString()) }
            rejectJson("Private ${privateEvent.eventType} in standalone result") { ApiMessageCodec.decodeResponse(badResult.toString()) }
            assertFailsWith<IllegalArgumentException> { good.copy(settlementEvents = listOf(privateEvent)) }
            assertFailsWith<IllegalArgumentException> { goodUpdate.copy(visibleEvents = listOf(privateEvent)) }
        }
        val response = ApiResponse(publicEvents = publicEvents())
        val restored = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(response))
        assertEquals(publicEvents(), restored.publicEvents, "Whitelisting must preserve real public wins and full scoring")
        val unknown = json(ApiMessageCodec.encodeResponse(response))
        unknown.getAsJsonArray("publicEvents")[0].asJsonObject.addProperty("eventType", "FuturePrivateEvent")
        rejectJson("Unknown event cannot be presumed public") { ApiMessageCodec.decodeResponse(unknown.toString()) }
    }

    @Test fun spectatorBindingRoundTripRedactsTokenAndPublicResponsesRejectMixedPlayerPayload() {
        val secret = "binding-private-resume-token"
        val binding = SpectatorBinding("room-review", "spectator-review", secret, snapshot())
        val response = ApiResponse(requestId = "wire-review", spectatorBinding = binding)
        val resumed = ResumeSpectatorRequest(binding.roomId, binding.spectatorId, secret,
            PublicCheckpoint(binding.snapshot.sessionId, binding.snapshot.syncEpoch, binding.snapshot.cursor))
        assertFalse(binding.toString().contains(secret))
        assertFalse(resumed.toString().contains(secret))
        assertFalse(response.toString().contains(secret))
        val restored = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(response))
        assertEquals(binding, restored.spectatorBinding)
        assertEquals(secret, restored.spectatorBinding!!.resumeToken)
        val publicOnly = ApiResponse(publicSnapshot = snapshot())
        assertEquals(publicOnly, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(publicOnly)))
        val legacy = ApiResponse(seat = 0, resumeToken = "legacy-player-resume-token",
            capabilities = ProtocolAccessCapabilities(protocolVersion = ApiProtocol.PROTOCOL_VERSION),
            events = listOf(ProtocolGameEvent.TileDrawn(1, 0, redFive, 20)))
        assertEquals(legacy, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(legacy)),
            "Existing legal PLAYER responses must remain valid without PUBLIC fields")
        val playerPayload = json(ApiMessageCodec.encodeResponse(legacy))
        for (key in listOf("seat", "resumeToken", "capabilities", "events")) {
            for (base in listOf(response, publicOnly, ApiResponse(publicEvents = publicEvents()))) {
                val mixed = json(ApiMessageCodec.encodeResponse(base)).apply { add(key, playerPayload.get(key).deepCopy()) }
                rejectJson("PUBLIC and PLAYER $key response fields cannot mix") { ApiMessageCodec.decodeResponse(mixed.toString()) }
            }
        }
        val room = ProtocolRoomInfo("room-review", ProtocolRoomLifecycle.LOBBY, version = 2L)
        val roomResponse = ApiResponse(roomInfo = room,
            receipt = ProtocolActionReceipt("room-review", ProtocolReceiptStatus.APPLIED))
        assertEquals(roomResponse, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(roomResponse)),
            "Public room information and receipts are not private player payload")
    }

    private fun immutableList(list: List<*>) {
        assertTrue(list.isNotEmpty(), "Use a nonempty list for an effective mutation probe")
        val saved = list.toList()
        val failure = runCatching { (list as MutableList<*>).clear() }.exceptionOrNull()
        assertTrue(failure is UnsupportedOperationException || failure is ClassCastException)
        assertEquals(saved, list)
    }

    @Test fun publicSourceCollectionsCopiesAndDecodedNestedGettersRemainImmutable() {
        val scores = finalScores.toMutableList()
        val tiles = mutableListOf(ProtocolTile.fromTileId(16), ProtocolTile.fromTileId(17), redFive)
        val river = mutableListOf(redFive)
        val allRivers = MutableList<List<ProtocolTile>>(4) { river }
        val melds = mutableListOf(ProtocolMeld(ProtocolMeldType.PON, tiles, 1, redFive))
        val outcomeSeats = linkedSetOf(0, 2)
        val records = mutableListOf(ProtocolPublicScoreRecord("r", "result", scores,
            mutableListOf(-9300, 9300, 0, 0), 2, ProtocolRoundOutcome.ExhaustiveDraw(outcomeSeats)))
        val board = snapshot().board!!.copy(scores = scores, discards = allRivers,
            melds = listOf(melds, emptyList(), emptyList(), emptyList()))
        val session = snapshot().session.copy(scores = scores, roundRecords = records)
        val events = publicEvents().toMutableList()
        val clocks = snapshot().clocks.toMutableList()
        val constructed = snapshot().copy(board = board, session = session, settlementEvents = events, clocks = clocks)
        val wireBefore = encodeSnapshot(constructed)
        scores[0] = 999; tiles.clear(); river.clear(); allRivers.clear(); melds.clear(); outcomeSeats.clear()
        records.clear(); events.clear(); clocks.clear()
        assertEquals(json(wireBefore), json(encodeSnapshot(constructed)), "Mutating source collections must not alter PUBLIC values")
        for (safe in listOf(constructed, constructed.copy(), decodeSnapshot(wireBefore))) {
            assertEquals(constructed, safe)
            assertEquals(constructed.hashCode(), safe.hashCode())
            immutableList(safe.clocks); immutableList(safe.settlementEvents)
            val b = assertNotNull(safe.board)
            immutableList(b.scores); immutableList(b.handTileCounts); immutableList(b.riichiStatus)
            immutableList(b.discards); immutableList(b.discards[0]); immutableList(b.melds)
            immutableList(b.melds[0]); immutableList(b.melds[0][0].tiles)
            immutableList(safe.session.roundRecords); immutableList(safe.session.roundRecords[0].finalScores)
            immutableList(safe.session.roundRecords[0].deltas)
            val seats = (safe.session.roundRecords[0].outcome as ProtocolRoundOutcome.ExhaustiveDraw).tenpaiSeats
            val failure = runCatching { (seats as MutableSet<Int>).clear() }.exceptionOrNull()
            assertTrue(failure is UnsupportedOperationException || failure is ClassCastException)
            assertEquals(setOf(0, 2), seats)
            val detail = (safe.settlementEvents[0] as ProtocolGameEvent.RonWin).breakdown!!
            immutableList(detail.yakuLines); immutableList(detail.paymentLines); immutableList(detail.fu.lines)
            immutableList(detail.dora.indicatorHits); immutableList(detail.dora.indicatorHits[0].tiles)
            immutableList(detail.dora.uraHits); immutableList(detail.dora.uraHits[0].tiles)
        }
    }

    @Test fun standalonePublicResultResponseDefensivelyCopiesAndProtectsItsEventList() {
        val source = publicEvents().toMutableList()
        val response = ApiResponse(publicEvents = source)
        val before = ApiMessageCodec.encodeResponse(response)
        source.clear()
        assertEquals(json(before), json(ApiMessageCodec.encodeResponse(response)), "ApiResponse.publicEvents must copy its input")
        for (result in listOf(response, response.copy(), ApiMessageCodec.decodeResponse(before))) {
            immutableList(assertNotNull(result.publicEvents))
            assertEquals(publicEvents(), result.publicEvents)
        }
    }

    @Test fun pushClassificationSeparatesPublicRoomAndLegacyPlayerAndRejectsInvalidTypeEnvelopes() {
        val action = ProtocolLegalAction.DiscardTile(0, redFive)
        val playerView = ProtocolPlayerView(
            viewerSeat = 0, phase = ProtocolGamePhase.DISCARD, roundWind = 1, roundNumber = 0,
            honbaSticks = 0, riichiSticks = 0, dealerSeat = 0, currentTurnPlayer = 0, wallRemaining = 70,
            doraIndicators = listOf(indicator), scores = List(4) { 25000 }, riichiStatus = List(4) { false },
            discards = List(4) { emptyList() }, melds = List(4) { emptyList() },
            handTileCounts = List(4) { 13 }, myClosedTiles = listOf(redFive), myDrawnTile = null,
            legalActions = listOf(action))
        val playerSnapshot = ProtocolPlayerSnapshot(
            sessionId = "player-session", roundId = "player-round", syncEpoch = "player-epoch", cursor = 4L,
            sessionVersion = 1L, roundVersion = 1L, view = playerView,
            decision = ProtocolPlayerDecision("player-round", "player-decision", 2L,
                listOf(ProtocolActionOption("discard-red-five", action))))
        val playerUpdate = ProtocolPlayerUpdate(playerSnapshot, 4L, isFullSnapshot = true)
        val playerWire = ApiMessageCodec.encodePush(playerUpdate)
        assertFalse(json(playerWire).has("type"), "Keep the existing V4 untyped PLAYER encoding as the regression baseline")
        assertEquals(playerUpdate, ApiMessageCodec.decodePush(playerWire), "The PLAYER baseline must be genuinely decodable")

        val roomInfo = ProtocolRoomInfo("room-review", ProtocolRoomLifecycle.LOBBY, version = 2L)
        val roomWire = ApiMessageCodec.encodeRoomUpdatePush(roomInfo)
        assertEquals(roomInfo, ApiMessageCodec.decodeRoomUpdatePush(roomWire))
        val publicUpdate = ProtocolPublicUpdate(snapshot(), 9L, publicEvents(), true)
        val publicWire = encodeUpdate(publicUpdate)
        assertEquals(publicUpdate, decodeUpdate(publicWire))

        assertEquals(PushType.PLAYER, codec("classifyPush", playerWire))
        assertEquals(PushType.ROOM, codec("classifyPush", roomWire))
        assertEquals(PushType.PUBLIC, codec("classifyPush", publicWire))
        assertFalse(ApiMessageCodec.isRoomUpdateEnvelope(playerWire))
        assertTrue(ApiMessageCodec.isRoomUpdateEnvelope(roomWire))
        assertFalse(ApiMessageCodec.isRoomUpdateEnvelope(publicWire), "PUBLIC must not enter the ROOM decoder route")

        for (invalidType in listOf("\"UNKNOWN_UPDATE\"", "\"public_update\"", "3", "true", "null", "[]", "{}")) {
            val malformed = json(publicWire).apply { add("type", JsonParser.parseString(invalidType)) }
            rejectJson("Invalid PUSH type cannot fall back to PLAYER or ROOM: $malformed") {
                codec("classifyPush", malformed.toString())
            }
        }
        for (malformed in listOf("[]", "null", "23", "\"ROOM_UPDATE\"", "{\"type\":\"PUBLIC_UPDATE\"", "{}")) {
            rejectJson("PUSH classifier must reject malformed or non-object envelopes: $malformed") {
                codec("classifyPush", malformed)
            }
        }
    }
}

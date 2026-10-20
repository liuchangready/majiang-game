package com.majiang.transport.api

import com.google.gson.JsonParseException
import com.majiang.server.api.FakeServerConnection
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ApiMessageCodecTest {

    @Test
    fun `valid request encodes and decodes roundtrip`() {
        val req = ApiRequest(
            api = ApiTags.CREATE_ROOM,
            requestId = "req-test-1",
            playerName = "PlayerA"
        )
        val json = ApiMessageCodec.encodeRequest(req)
        val decoded = ApiMessageCodec.decodeRequest(json)

        assertEquals(ApiTags.CREATE_ROOM, decoded.api)
        assertEquals("req-test-1", decoded.requestId)
        assertEquals("PlayerA", decoded.playerName)
    }

    @Test
    fun `missing api field throws JsonParseException`() {
        val json = """{"requestId":"req-test-2"}"""
        val ex = assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest(json)
        }
        assertTrue(ex.message?.contains("api") == true)
    }

    @Test
    fun `missing requestId field throws JsonParseException`() {
        val json = """{"api":"create_room"}"""
        val ex = assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest(json)
        }
        assertTrue(ex.message?.contains("requestId") == true)
    }

    @Test
    fun `syntactically valid unknown api tag is preserved for dispatcher`() {
        val json = """{"api":"totally_unknown_action","requestId":"req-test-3","protocolVersion":4}"""
        val decoded = ApiMessageCodec.decodeRequest(json)
        assertEquals("totally_unknown_action", decoded.api)
        assertEquals("req-test-3", decoded.requestId)
    }

    @Test
    fun `invalid protocol version throws JsonParseException`() {
        val json = """{"api":"create_room","requestId":"req-test-4","protocolVersion":0}"""
        val ex = assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest(json)
        }
        assertTrue(ex.message?.contains("protocolVersion") == true)
    }

    @Test
    fun `invalid nested config throws JsonParseException at boundary`() {
        val json = """{"api":"CREATE_ROOM","requestId":"req-cfg","config":{"playerCount":3,"handsPerWind":4,"winds":[1,2],"startingScores":[25000,25000,25000],"tieBreakSeats":[0,1,2]}}"""
        val ex = assertThrows<JsonParseException> {
            ApiMessageCodec.decodeRequest(json)
        }
        assertTrue(ex.message?.contains("playerCount") == true || ex.message?.contains("config") == true)
    }

    @Test
    fun `push wire marker decoded correctly`() {
        val snapshot = FakeServerConnection.createDefaultSnapshot(cursor = 10)
        val jsonWithMarker = ApiMessageCodec.encodePush(ProtocolPlayerUpdate(snapshot, 10, emptyList()), isFullSnapshot = true)
        val decoded = ApiMessageCodec.decodePushEnvelope(jsonWithMarker)
        assertEquals(10L, decoded.update.snapshot.cursor)
        assertTrue(decoded.isFullSnapshot)
    }

    @Test
    fun `spectator request tags encode and decode roundtrip`() {
        val joinReq = ApiRequest(
            api = ApiTags.JOIN_SPECTATOR,
            requestId = "req-spec-1",
            roomId = "ROOM1234",
            spectatorName = "Observer1"
        )
        val joinJson = ApiMessageCodec.encodeRequest(joinReq)
        val decodedJoin = ApiMessageCodec.decodeRequest(joinJson)
        assertEquals(ApiTags.JOIN_SPECTATOR, decodedJoin.api)
        assertEquals("req-spec-1", decodedJoin.requestId)
        assertEquals("ROOM1234", decodedJoin.roomId)
        assertEquals("Observer1", decodedJoin.spectatorName)

        val cp = com.majiang.server.api.PublicCheckpoint("session-1", "epoch-1", 7L)
        val resumeReq = ApiRequest(
            api = ApiTags.RESUME_SPECTATOR,
            requestId = "req-spec-2",
            roomId = "ROOM1234",
            spectatorId = "spec-1",
            token = "secret-token",
            publicCheckpoint = cp
        )
        val resumeJson = ApiMessageCodec.encodeRequest(resumeReq)
        val decodedResume = ApiMessageCodec.decodeRequest(resumeJson)
        assertEquals(ApiTags.RESUME_SPECTATOR, decodedResume.api)
        assertEquals("spec-1", decodedResume.spectatorId)
        assertEquals("secret-token", decodedResume.token)
        assertEquals(cp, decodedResume.publicCheckpoint)

        val pubSnapReq = ApiRequest(
            api = ApiTags.PUBLIC_SNAPSHOT,
            requestId = "req-spec-3"
        )
        val pubSnapJson = ApiMessageCodec.encodeRequest(pubSnapReq)
        val decodedPubSnap = ApiMessageCodec.decodeRequest(pubSnapJson)
        assertEquals(ApiTags.PUBLIC_SNAPSHOT, decodedPubSnap.api)

        val resPubReq = ApiRequest(
            api = ApiTags.RESUME_PUBLIC,
            requestId = "req-spec-4",
            publicCheckpoint = cp
        )
        val resPubJson = ApiMessageCodec.encodeRequest(resPubReq)
        val decodedResPub = ApiMessageCodec.decodeRequest(resPubJson)
        assertEquals(ApiTags.RESUME_PUBLIC, decodedResPub.api)
        assertEquals(cp, decodedResPub.publicCheckpoint)

        val queryReq = ApiRequest(
            api = ApiTags.QUERY_PUBLIC_ROUND_RESULT,
            requestId = "req-spec-5",
            roundId = "round-1"
        )
        val queryJson = ApiMessageCodec.encodeRequest(queryReq)
        val decodedQuery = ApiMessageCodec.decodeRequest(queryJson)
        assertEquals(ApiTags.QUERY_PUBLIC_ROUND_RESULT, decodedQuery.api)
        assertEquals("round-1", decodedQuery.roundId)
    }

    @Test
    fun `nondefault shuffle configuration and plan retain typed values across wire`() {
        val config = ProtocolMatchConfig(shuffleConfig = ProtocolShuffleConfig(
            enabled = true, plannedDraws = 18, interference = 2.0))
        val create = ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "shuffle-config",
            playerName = "creator", config = config)
        assertEquals(config.shuffleConfig,
            ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(create)).config?.shuffleConfig)

        val plan = ProtocolShufflePlan("shuffle-plan", "round", 7L,
            ProtocolShuffleSelection(target = ProtocolShuffleTarget.KOKUSHI))
        val submit = ApiRequest(api = ApiTags.SUBMIT_SHUFFLE_PLAN, requestId = plan.requestId,
            shufflePlan = plan)
        assertEquals(plan, ApiMessageCodec.decodeRequest(ApiMessageCodec.encodeRequest(submit)).shufflePlan)
    }

    @Test
    fun `shuffle wire rejects forged fields targets and fractional or overflowing epochs`() {
        val plan = ProtocolShufflePlan("shuffle-plan", "round", 7L,
            ProtocolShuffleSelection(target = ProtocolShuffleTarget.KOKUSHI))
        val good = ApiMessageCodec.encodeRequest(ApiRequest(api = ApiTags.SUBMIT_SHUFFLE_PLAN,
            requestId = plan.requestId, shufflePlan = plan))
        val malformed = listOf(
            good.replace("\"controlEpoch\":7", "\"controlEpoch\":7.5"),
            good.replace("\"controlEpoch\":7", "\"controlEpoch\":9223372036854775808"),
            good.replace("\"controlEpoch\":7", "\"controlEpoch\":\"7\""),
            good.replace("\"KOKUSHI\"", "7"),
            good.replace("\"KOKUSHI\"", "\"INVALID\""),
            good.replace("\"target\":", "\"cost\":0,\"target\":"),
            good.replace("\"controlEpoch\":7", "\"seat\":1,\"controlEpoch\":7")
        )
        for (json in malformed) {
            assertTrue(json != good)
            assertThrows<Exception> { ApiMessageCodec.decodeRequest(json) }
        }
    }

    @Test
    fun `snapshot wire retains private preparation and rejects unknown target`() {
        val selection = ProtocolShuffleSelection(target = ProtocolShuffleTarget.KOKUSHI)
        val snapshot = FakeServerConnection.createDefaultSnapshot().copy(
            preparation = ProtocolShufflePreparation(
                roundId = "round-1", status = ProtocolShufflePreparationStatus.SELECTING,
                opensAtMillis = 0L, deadlineMillis = 10_000L, serverNowMillis = 1_000L,
                confirmed = true, selection = selection, reservedPoints = 40, availablePoints = 10))
        val encoded = ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot))
        val decoded = ApiMessageCodec.decodeResponse(encoded).snapshot
        assertEquals(snapshot.preparation, decoded?.preparation)
        assertEquals(40, decoded?.preparation?.reservedPoints)
        val other = FakeServerConnection.createDefaultSnapshot(seat = 1)
        assertFalse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = other)).contains("KOKUSHI"))
        assertThrows<Exception> {
            ApiMessageCodec.decodeResponse(encoded.replace("KOKUSHI", "UNKNOWN_TARGET"))
        }
        val withoutPreparation = ApiMessageCodec.encodeResponse(ApiResponse(snapshot = snapshot.copy(preparation = null)))
        assertNull(ApiMessageCodec.decodeResponse(withoutPreparation).snapshot?.preparation)
    }
}

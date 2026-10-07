package com.majiang.transport.api

import com.google.gson.JsonParseException
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ProtocolRoomModelsCodecTest {

    @Test
    fun testRoomInfoCodecRoundTrip() {
        val member1 = ProtocolRoomMember(
            seat = 0,
            playerName = "Alice",
            isHost = true,
            isReady = true,
            role = "HUMAN"
        )
        val member2 = ProtocolRoomMember(
            seat = 1,
            playerName = "Bob",
            isHost = false,
            isReady = false,
            role = "COMPUTER"
        )
        val config = ProtocolRoomConfig(
            matchConfig = ProtocolMatchConfig(winds = listOf(1, 2)),
            timeControl = ProtocolTimeControlConfig(roundReserveSeconds = 15L, turnThinkingSeconds = 5L)
        )
        val info = ProtocolRoomInfo(
            roomId = "ROOM9999",
            lifecycle = ProtocolRoomLifecycle.LOBBY,
            version = 42L,
            hostSeat = 0,
            members = listOf(member1, member2),
            config = config
        )

        val json = ApiMessageCodec.encodeRoomInfo(info)
        val decoded = ApiMessageCodec.decodeRoomInfo(json)

        assertEquals(info.roomId, decoded.roomId)
        assertEquals(info.lifecycle, decoded.lifecycle)
        assertEquals(info.version, decoded.version)
        assertEquals(info.hostSeat, decoded.hostSeat)
        assertEquals(info.members.size, decoded.members.size)
        assertEquals(info.members[0], decoded.members[0])
        assertEquals(info.members[1], decoded.members[1])
        assertEquals(info.config, decoded.config)
        assertEquals(info, decoded)
    }

    @Test
    fun testRoomUpdatePushEnvelopeCodec() {
        val info = ProtocolRoomInfo(
            roomId = "ROOM1234",
            lifecycle = ProtocolRoomLifecycle.PREPARING,
            version = 7L,
            hostSeat = 0,
            members = listOf(
                ProtocolRoomMember(seat = 0, playerName = "Host", isHost = true, isReady = true, role = "HUMAN")
            ),
            config = ProtocolRoomConfig()
        )

        val json = ApiMessageCodec.encodeRoomUpdatePush(info)
        assertTrue(ApiMessageCodec.isRoomUpdatePush(json))

        val decoded = ApiMessageCodec.decodeRoomUpdatePush(json)
        assertEquals(info, decoded)
    }

    @Test
    fun testUnknownFieldRejection() {
        val jsonWithUnknown = """{"roomId":"R1","lifecycle":"LOBBY","version":1,"hostSeat":0,"members":[],"config":{},"unknownField":"bad"}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.decodeRoomInfo(jsonWithUnknown)
        }

        val memberWithUnknown = """{"seat":0,"playerName":"P","isHost":true,"isReady":false,"role":"HUMAN","hacker":true}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(memberWithUnknown, ProtocolRoomMember::class.java)
        }

        val timeWithUnknown = """{"roundReserveSeconds":20,"turnThinkingSeconds":3,"extra":123}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(timeWithUnknown, ProtocolTimeControlConfig::class.java)
        }
    }

    @Test
    fun testTimeControlValidation() {
        // Negative roundReserveSeconds
        val negTimeJson = """{"roundReserveSeconds":-1,"turnThinkingSeconds":3}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(negTimeJson, ProtocolTimeControlConfig::class.java)
        }

        // Overflow range check
        val overflowTimeJson = """{"roundReserveSeconds":${Long.MAX_VALUE},"turnThinkingSeconds":3}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(overflowTimeJson, ProtocolTimeControlConfig::class.java)
        }
    }

    @Test
    fun testMemberValidation() {
        // Invalid seat
        val invalidSeatJson = """{"seat":5,"playerName":"P","isHost":true,"isReady":false,"role":"HUMAN"}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(invalidSeatJson, ProtocolRoomMember::class.java)
        }

        // Blank name
        val blankNameJson = """{"seat":0,"playerName":"","isHost":true,"isReady":false,"role":"HUMAN"}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(blankNameJson, ProtocolRoomMember::class.java)
        }

        // Invalid role
        val invalidRoleJson = """{"seat":0,"playerName":"P","isHost":true,"isReady":false,"role":"SPECTATOR"}"""
        assertFailsWith<JsonParseException> {
            ApiMessageCodec.gson.fromJson(invalidRoleJson, ProtocolRoomMember::class.java)
        }
    }
}

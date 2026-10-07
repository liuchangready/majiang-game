package com.majiang.v4models

import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class V4RoomProtocolAcceptanceTest {
    @Test fun roomUpdateDecoderAcceptsOnlyItsExplicitTypedEnvelope() {
        val info = ProtocolRoomInfo("review-room", ProtocolRoomLifecycle.LOBBY, version = 1L)
        val valid = ApiMessageCodec.encodeRoomUpdatePush(info)
        assertEquals(info, ApiMessageCodec.decodeRoomUpdatePush(valid))
        val wrongType = JsonParser.parseString(valid).asJsonObject.apply { addProperty("type", "PLAYER_UPDATE") }
        val extraField = JsonParser.parseString(valid).asJsonObject.apply { addProperty("unexpected", true) }
        val missingType = JsonParser.parseString(valid).asJsonObject.apply { remove("type") }
        for (invalid in listOf(wrongType.toString(), extraField.toString(), missingType.toString(),
            ApiMessageCodec.encodeRoomInfo(info))) {
            assertFailsWith<JsonParseException>("Room update envelope must reject: $invalid") {
                ApiMessageCodec.decodeRoomUpdatePush(invalid)
            }
        }
    }
}

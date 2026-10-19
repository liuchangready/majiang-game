package com.majiang.transport.api

import com.google.gson.JsonDeserializationContext
import com.google.gson.JsonDeserializer
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonSerializationContext
import com.google.gson.JsonSerializer
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import java.lang.reflect.Type
import java.util.Collections

/**
 * RoomSummary JsonDeserializer
 */
internal class RoomSummaryAdapter : JsonDeserializer<RoomSummary> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): RoomSummary {
        if (!json.isJsonObject) throw JsonParseException("RoomSummary must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "roomName", "hostPlayerName", "playerCount", "maxPlayers", "matchStarted", "configSummary"), "RoomSummary")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("Missing roomId in RoomSummary"), "RoomSummary.roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank in RoomSummary")
        val roomName = validateStrictString(obj.get("roomName") ?: throw JsonParseException("Missing roomName in RoomSummary"), "RoomSummary.roomName")
        if (roomName.isBlank()) throw JsonParseException("roomName cannot be blank in RoomSummary")
        val hostName = validateStrictString(obj.get("hostPlayerName") ?: throw JsonParseException("Missing hostPlayerName in RoomSummary"), "RoomSummary.hostPlayerName")
        if (hostName.isBlank()) throw JsonParseException("hostPlayerName cannot be blank in RoomSummary")
        val maxPlayers = if (obj.has("maxPlayers") && !obj.get("maxPlayers").isJsonNull) {
            val mp = validateStrictInt(obj.get("maxPlayers"), "RoomSummary.maxPlayers")
            if (mp < 1) throw JsonParseException("maxPlayers must be >= 1: $mp")
            mp
        } else 4
        val playerCount = validateStrictInt(obj.get("playerCount") ?: throw JsonParseException("Missing playerCount in RoomSummary"), "RoomSummary.playerCount")
        if (playerCount !in 1..maxPlayers) {
            throw JsonParseException("playerCount must be in 1..$maxPlayers: $playerCount")
        }
        val matchStarted = validateStrictBoolean(obj.get("matchStarted") ?: throw JsonParseException("Missing matchStarted in RoomSummary"), "RoomSummary.matchStarted")
        val configSummary = if (obj.has("configSummary") && !obj.get("configSummary").isJsonNull) {
            validateStrictString(obj.get("configSummary"), "RoomSummary.configSummary")
        } else ""
        return RoomSummary(roomId, roomName, hostName, playerCount, maxPlayers, matchStarted, configSummary)
    }
}

/**
 * RoomBinding JsonDeserializer
 */
internal class RoomBindingAdapter : JsonDeserializer<RoomBinding> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): RoomBinding {
        if (!json.isJsonObject) throw JsonParseException("RoomBinding must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "seat", "resumeToken", "snapshot"), "RoomBinding")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("Missing roomId in RoomBinding"), "RoomBinding.roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank in RoomBinding")
        val seat = validateStrictInt(obj.get("seat") ?: throw JsonParseException("Missing seat in RoomBinding"), "RoomBinding.seat")
        if (seat !in 0..3) throw JsonParseException("seat must be in 0..3: $seat")
        val resumeToken = validateStrictString(obj.get("resumeToken") ?: throw JsonParseException("Missing resumeToken in RoomBinding"), "RoomBinding.resumeToken")
        if (resumeToken.isBlank()) throw JsonParseException("resumeToken cannot be blank in RoomBinding")
        val snapshotElem = obj.get("snapshot") ?: throw JsonParseException("Missing snapshot in RoomBinding")
        val snapshot = context.deserialize<ProtocolPlayerSnapshot>(snapshotElem, ProtocolPlayerSnapshot::class.java)
        return RoomBinding(roomId, seat, resumeToken, snapshot)
    }
}

/**
 * CreateRoomRequest JsonDeserializer
 */
internal class CreateRoomRequestAdapter : JsonDeserializer<CreateRoomRequest> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): CreateRoomRequest {
        if (!json.isJsonObject) throw JsonParseException("CreateRoomRequest must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("playerName", "config", "protocolVersion"), "CreateRoomRequest")
        val playerName = obj.get("playerName")?.takeIf { !it.isJsonNull }?.let {
            val name = validateStrictString(it, "CreateRoomRequest.playerName")
            if (name.isBlank()) throw JsonParseException("playerName cannot be blank in CreateRoomRequest")
            name
        }
        val config = if (obj.has("config") && !obj.get("config").isJsonNull) {
            context.deserialize<ProtocolMatchConfig>(obj.get("config"), ProtocolMatchConfig::class.java)
        } else null
        val protocolVersion = if (obj.has("protocolVersion") && !obj.get("protocolVersion").isJsonNull) {
            val pv = validateStrictInt(obj.get("protocolVersion"), "CreateRoomRequest.protocolVersion")
            if (pv < 1) throw JsonParseException("protocolVersion must be >= 1: $pv")
            pv
        } else ApiProtocol.PROTOCOL_VERSION
        return CreateRoomRequest(playerName, config, protocolVersion)
    }
}

/**
 * JoinRoomRequest JsonDeserializer
 */
internal class JoinRoomRequestAdapter : JsonDeserializer<JoinRoomRequest> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): JoinRoomRequest {
        if (!json.isJsonObject) throw JsonParseException("JoinRoomRequest must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "playerName", "preferSeat"), "JoinRoomRequest")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("Missing roomId in JoinRoomRequest"), "JoinRoomRequest.roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank in JoinRoomRequest")
        val playerName = obj.get("playerName")?.takeIf { !it.isJsonNull }?.let {
            val name = validateStrictString(it, "JoinRoomRequest.playerName")
            if (name.isBlank()) throw JsonParseException("playerName cannot be blank in JoinRoomRequest")
            name
        }
        val preferSeat = if (obj.has("preferSeat") && !obj.get("preferSeat").isJsonNull) {
            val seat = validateStrictInt(obj.get("preferSeat"), "JoinRoomRequest.preferSeat")
            if (seat !in 0..3) throw JsonParseException("preferSeat must be in 0..3: $seat")
            seat
        } else null
        return JoinRoomRequest(roomId, playerName, preferSeat)
    }
}

/**
 * ResumeRequest JsonDeserializer
 */
internal class ResumeRequestAdapter : JsonDeserializer<ResumeRequest> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ResumeRequest {
        if (!json.isJsonObject) throw JsonParseException("ResumeRequest must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "token", "cursor"), "ResumeRequest")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("Missing roomId in ResumeRequest"), "ResumeRequest.roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank in ResumeRequest")
        val token = validateStrictString(obj.get("token") ?: throw JsonParseException("Missing token in ResumeRequest"), "ResumeRequest.token")
        if (token.isBlank()) throw JsonParseException("token cannot be blank in ResumeRequest")
        val cursor = if (obj.has("cursor") && !obj.get("cursor").isJsonNull) {
            val c = validateStrictLong(obj.get("cursor"), "ResumeRequest.cursor")
            if (c < 0L) throw JsonParseException("cursor must be >= 0: $c")
            c
        } else null
        return ResumeRequest(roomId, token, cursor)
    }
}

/**
 * ProtocolRoomMember TypeAdapter
 */
internal class ProtocolRoomMemberAdapter : JsonSerializer<ProtocolRoomMember>, JsonDeserializer<ProtocolRoomMember> {
    override fun serialize(src: ProtocolRoomMember, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("seat", src.seat)
        obj.addProperty("playerName", src.playerName)
        obj.addProperty("isHost", src.isHost)
        obj.addProperty("isReady", src.isReady)
        obj.addProperty("role", src.role)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolRoomMember {
        if (!json.isJsonObject) throw JsonParseException("ProtocolRoomMember must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("seat", "playerName", "isHost", "isReady", "role"), "ProtocolRoomMember")
        val seat = validateStrictInt(obj.get("seat") ?: throw JsonParseException("ProtocolRoomMember missing seat"), "seat")
        val playerName = validateStrictString(obj.get("playerName") ?: throw JsonParseException("ProtocolRoomMember missing playerName"), "playerName")
        val isHost = validateStrictBoolean(obj.get("isHost") ?: throw JsonParseException("ProtocolRoomMember missing isHost"), "isHost")
        val isReady = validateStrictBoolean(obj.get("isReady") ?: throw JsonParseException("ProtocolRoomMember missing isReady"), "isReady")
        val role = validateStrictString(obj.get("role") ?: throw JsonParseException("ProtocolRoomMember missing role"), "role")
        try {
            return ProtocolRoomMember(seat, playerName, isHost, isReady, role)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolRoomMember: ${e.message}", e)
        }
    }
}

/**
 * ProtocolTimeControlConfig TypeAdapter
 */
internal class ProtocolTimeControlConfigAdapter : JsonSerializer<ProtocolTimeControlConfig>, JsonDeserializer<ProtocolTimeControlConfig> {
    override fun serialize(src: ProtocolTimeControlConfig, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roundReserveSeconds", src.roundReserveSeconds)
        obj.addProperty("turnThinkingSeconds", src.turnThinkingSeconds)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolTimeControlConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolTimeControlConfig must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roundReserveSeconds", "turnThinkingSeconds"), "ProtocolTimeControlConfig")
        val roundReserveSeconds = if (obj.has("roundReserveSeconds") && !obj.get("roundReserveSeconds").isJsonNull) {
            validateStrictLong(obj.get("roundReserveSeconds"), "roundReserveSeconds")
        } else 20L
        val turnThinkingSeconds = if (obj.has("turnThinkingSeconds") && !obj.get("turnThinkingSeconds").isJsonNull) {
            validateStrictLong(obj.get("turnThinkingSeconds"), "turnThinkingSeconds")
        } else 3L
        try {
            return ProtocolTimeControlConfig(roundReserveSeconds, turnThinkingSeconds)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolTimeControlConfig: ${e.message}", e)
        }
    }
}

/**
 * ProtocolRoomConfig TypeAdapter
 */
internal class ProtocolRoomConfigAdapter : JsonSerializer<ProtocolRoomConfig>, JsonDeserializer<ProtocolRoomConfig> {
    override fun serialize(src: ProtocolRoomConfig, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.add("matchConfig", context.serialize(src.matchConfig, ProtocolMatchConfig::class.java))
        obj.add("timeControl", context.serialize(src.timeControl, ProtocolTimeControlConfig::class.java))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolRoomConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolRoomConfig must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("matchConfig", "timeControl"), "ProtocolRoomConfig")
        val matchConfig = if (obj.has("matchConfig") && !obj.get("matchConfig").isJsonNull) {
            context.deserialize<ProtocolMatchConfig>(obj.get("matchConfig"), ProtocolMatchConfig::class.java)
        } else ProtocolMatchConfig()
        val timeControl = if (obj.has("timeControl") && !obj.get("timeControl").isJsonNull) {
            context.deserialize<ProtocolTimeControlConfig>(obj.get("timeControl"), ProtocolTimeControlConfig::class.java)
        } else ProtocolTimeControlConfig()
        return ProtocolRoomConfig(matchConfig, timeControl)
    }
}

/**
 * ProtocolRoomInfo TypeAdapter
 */
internal class ProtocolRoomInfoAdapter : JsonSerializer<ProtocolRoomInfo>, JsonDeserializer<ProtocolRoomInfo> {
    override fun serialize(src: ProtocolRoomInfo, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roomId", src.roomId)
        obj.addProperty("lifecycle", src.lifecycle.name)
        obj.addProperty("version", src.version)
        obj.addProperty("hostSeat", src.hostSeat)
        val arr = com.google.gson.JsonArray()
        for (m in src.members) {
            arr.add(context.serialize(m, ProtocolRoomMember::class.java))
        }
        obj.add("members", arr)
        obj.add("config", context.serialize(src.config, ProtocolRoomConfig::class.java))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolRoomInfo {
        if (!json.isJsonObject) throw JsonParseException("ProtocolRoomInfo must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "lifecycle", "version", "hostSeat", "members", "config"), "ProtocolRoomInfo")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("ProtocolRoomInfo missing roomId"), "roomId")
        val lifecycleStr = validateStrictString(obj.get("lifecycle") ?: throw JsonParseException("ProtocolRoomInfo missing lifecycle"), "lifecycle")
        val lifecycle = try {
            ProtocolRoomLifecycle.valueOf(lifecycleStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid lifecycle in ProtocolRoomInfo: $lifecycleStr", e)
        }
        val version = if (obj.has("version") && !obj.get("version").isJsonNull) {
            validateStrictLong(obj.get("version"), "version")
        } else 1L
        val hostSeat = if (obj.has("hostSeat") && !obj.get("hostSeat").isJsonNull) {
            validateStrictInt(obj.get("hostSeat"), "hostSeat")
        } else 0
        val members = if (obj.has("members") && !obj.get("members").isJsonNull) {
            val arr = obj.getAsJsonArray("members")
            val list = ArrayList<ProtocolRoomMember>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolRoomMember::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()
        val config = if (obj.has("config") && !obj.get("config").isJsonNull) {
            context.deserialize<ProtocolRoomConfig>(obj.get("config"), ProtocolRoomConfig::class.java)
        } else ProtocolRoomConfig()
        try {
            return ProtocolRoomInfo(roomId, lifecycle, version, hostSeat, members, config)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolRoomInfo: ${e.message}", e)
        }
    }
}

package com.majiang.transport.api

import com.google.gson.Gson
import com.google.gson.GsonBuilder
import com.google.gson.JsonDeserializationContext
import com.google.gson.JsonDeserializer
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.google.gson.JsonSerializationContext
import com.google.gson.JsonSerializer
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.SpectatorBinding
import com.google.gson.reflect.TypeToken
import java.lang.reflect.Type
import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * V4 传输层 API 消息标签 (ApiTags)
 */
object ApiTags {
    /** 大厅期接口标签 */
    const val CREATE_ROOM = "CREATE_ROOM"
    const val JOIN_ROOM = "JOIN_ROOM"
    const val LIST_ROOMS = "LIST_ROOMS"
    const val RESUME_SESSION = "RESUME_SESSION"
    const val LEAVE_ROOM = "LEAVE_ROOM"
    const val GET_ROOM_INFO = "GET_ROOM_INFO"
    const val SET_READY = "SET_READY"
    const val UPDATE_CONFIG = "UPDATE_CONFIG"

    /** 对局期接口标签 */
    const val CAPABILITIES = "CAPABILITIES"
    const val SNAPSHOT = "SNAPSHOT"
    const val DECISION = "DECISION"
    const val SUBMIT_CHOICE = "SUBMIT_CHOICE"
    const val SUBMIT_SHUFFLE_PLAN = "SUBMIT_SHUFFLE_PLAN"
    const val SUBMIT_TILE_MARK = "SUBMIT_TILE_MARK"
    const val SUBMIT_SLEEVE_BLOOM = "SUBMIT_SLEEVE_BLOOM"
    const val SUBMIT_SLEEVE_BLOOM_CHOICE = "SUBMIT_SLEEVE_BLOOM_CHOICE"
    const val SUBMIT_BEAM_SWAP = "SUBMIT_BEAM_SWAP"
    const val SUBMIT_BEAM_SWAP_CHOICE = "SUBMIT_BEAM_SWAP_CHOICE"
    const val START_MATCH = "START_MATCH"
    const val NEXT_ROUND = "NEXT_ROUND"
    const val RESUME_CURSOR = "RESUME_CURSOR"
    const val CLAIM_CONTROL = "CLAIM_CONTROL"
    const val RELEASE_CONTROL = "RELEASE_CONTROL"
    const val QUERY_ROUND_RESULT = "QUERY_ROUND_RESULT"

    /** 观战与公开流接口标签 */
    const val JOIN_SPECTATOR = "JOIN_SPECTATOR"
    const val RESUME_SPECTATOR = "RESUME_SPECTATOR"
    const val PUBLIC_SNAPSHOT = "PUBLIC_SNAPSHOT"
    const val RESUME_PUBLIC = "RESUME_PUBLIC"
    const val QUERY_PUBLIC_ROUND_RESULT = "QUERY_PUBLIC_ROUND_RESULT"

    /** 全部已知标签（读取侧白名单） */
    val KNOWN: Set<String> = setOf(
        CREATE_ROOM, JOIN_ROOM, LIST_ROOMS, RESUME_SESSION, LEAVE_ROOM,
        GET_ROOM_INFO, SET_READY, UPDATE_CONFIG,
        CAPABILITIES, SNAPSHOT, DECISION, SUBMIT_CHOICE, SUBMIT_SHUFFLE_PLAN, SUBMIT_TILE_MARK,
        SUBMIT_SLEEVE_BLOOM, SUBMIT_SLEEVE_BLOOM_CHOICE,
        SUBMIT_BEAM_SWAP, SUBMIT_BEAM_SWAP_CHOICE,
        START_MATCH, NEXT_ROUND, RESUME_CURSOR, CLAIM_CONTROL, RELEASE_CONTROL,
        QUERY_ROUND_RESULT,
        JOIN_SPECTATOR, RESUME_SPECTATOR, PUBLIC_SNAPSHOT, RESUME_PUBLIC, QUERY_PUBLIC_ROUND_RESULT
    )
}

/**
 * V4 统一请求信封（UDP REQ payload）
 */
data class ApiRequest(
    val api: String,
    val requestId: String,
    val roomId: String? = null,
    val token: String? = null,
    val playerName: String? = null,
    val preferSeat: Int? = null,
    val config: ProtocolMatchConfig? = null,
    val choice: ProtocolActionChoice? = null,
    val shufflePlan: ProtocolShufflePlan? = null,
    val cursor: Long? = null,
    val roundId: String? = null,
    val protocolVersion: Int = ApiProtocol.PROTOCOL_VERSION,
    val ready: Boolean? = null,
    val roomConfig: ProtocolRoomConfig? = null,
    val spectatorId: String? = null,
    val spectatorName: String? = null,
    val publicCheckpoint: PublicCheckpoint? = null,
    val tileMark: ProtocolTileMarkRequest? = null,
    val sleeveBloom: ProtocolSleeveBloomRequest? = null,
    val sleeveBloomChoice: ProtocolSleeveBloomChoiceRequest? = null,
    val beamSwap: ProtocolBeamSwapRequest? = null,
    val beamSwapChoice: ProtocolBeamSwapChoiceRequest? = null
) {
    init {
        require(api.isNotBlank()) { "api cannot be blank" }
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(protocolVersion >= 1) { "protocolVersion must be >= 1: $protocolVersion" }
        if (spectatorId != null) {
            require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
        }
        if (spectatorName != null) {
            require(spectatorName.isNotBlank()) { "spectatorName cannot be blank" }
        }
    }

    override fun toString(): String {
        return "ApiRequest(api=$api, requestId=$requestId, roomId=$roomId, token=***, spectatorId=$spectatorId, protocolVersion=$protocolVersion)"
    }
}

/**
 * V4 统一响应信封（UDP RSP payload）
 */
class ApiResponse @JvmOverloads constructor(
    val requestId: String? = null,
    val success: Boolean = true,
    val error: String? = null,
    val roomId: String? = null,
    val seat: Int? = null,
    val resumeToken: String? = null,
    val rooms: List<RoomSummary>? = null,
    val capabilities: ProtocolAccessCapabilities? = null,
    val snapshot: ProtocolPlayerSnapshot? = null,
    val decision: ProtocolPlayerDecision? = null,
    val receipt: ProtocolActionReceipt? = null,
    val events: List<ProtocolGameEvent>? = null,
    val roomInfo: ProtocolRoomInfo? = null,
    val spectatorBinding: SpectatorBinding? = null,
    val publicSnapshot: ProtocolPublicSnapshot? = null,
    publicEvents: List<ProtocolGameEvent>? = null
) {
    val publicEvents: List<ProtocolGameEvent>? = publicEvents?.let {
        val list = ArrayList<ProtocolGameEvent>(it.size)
        for (ev in it) {
            validatePublicGameEvent(ev)
            list.add(ev)
        }
        Collections.unmodifiableList(list)
    }

    init {
        val hasPublic = spectatorBinding != null || publicSnapshot != null || this.publicEvents != null
        val hasPlayer = seat != null || resumeToken != null || snapshot != null || decision != null || capabilities != null || events != null
        require(!(hasPublic && hasPlayer)) { "PUBLIC and PLAYER response fields cannot mix" }
    }

    operator fun component1(): String? = requestId
    operator fun component2(): Boolean = success
    operator fun component3(): String? = error
    operator fun component4(): String? = roomId
    operator fun component5(): Int? = seat
    operator fun component6(): String? = resumeToken
    operator fun component7(): List<RoomSummary>? = rooms
    operator fun component8(): ProtocolAccessCapabilities? = capabilities
    operator fun component9(): ProtocolPlayerSnapshot? = snapshot
    operator fun component10(): ProtocolPlayerDecision? = decision
    operator fun component11(): ProtocolActionReceipt? = receipt
    operator fun component12(): List<ProtocolGameEvent>? = events
    operator fun component13(): ProtocolRoomInfo? = roomInfo
    operator fun component14(): SpectatorBinding? = spectatorBinding
    operator fun component15(): ProtocolPublicSnapshot? = publicSnapshot
    operator fun component16(): List<ProtocolGameEvent>? = this.publicEvents

    fun copy(
        requestId: String? = this.requestId,
        success: Boolean = this.success,
        error: String? = this.error,
        roomId: String? = this.roomId,
        seat: Int? = this.seat,
        resumeToken: String? = this.resumeToken,
        rooms: List<RoomSummary>? = this.rooms,
        capabilities: ProtocolAccessCapabilities? = this.capabilities,
        snapshot: ProtocolPlayerSnapshot? = this.snapshot,
        decision: ProtocolPlayerDecision? = this.decision,
        receipt: ProtocolActionReceipt? = this.receipt,
        events: List<ProtocolGameEvent>? = this.events,
        roomInfo: ProtocolRoomInfo? = this.roomInfo,
        spectatorBinding: SpectatorBinding? = this.spectatorBinding,
        publicSnapshot: ProtocolPublicSnapshot? = this.publicSnapshot,
        publicEvents: List<ProtocolGameEvent>? = this.publicEvents
    ): ApiResponse = ApiResponse(
        requestId, success, error, roomId, seat, resumeToken, rooms, capabilities,
        snapshot, decision, receipt, events, roomInfo, spectatorBinding, publicSnapshot, publicEvents
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ApiResponse) return false
        return requestId == other.requestId &&
            success == other.success &&
            error == other.error &&
            roomId == other.roomId &&
            seat == other.seat &&
            resumeToken == other.resumeToken &&
            rooms == other.rooms &&
            capabilities == other.capabilities &&
            snapshot == other.snapshot &&
            decision == other.decision &&
            receipt == other.receipt &&
            events == other.events &&
            roomInfo == other.roomInfo &&
            spectatorBinding == other.spectatorBinding &&
            publicSnapshot == other.publicSnapshot &&
            this.publicEvents == other.publicEvents
    }

    override fun hashCode(): Int {
        var result = (requestId?.hashCode() ?: 0)
        result = 31 * result + success.hashCode()
        result = 31 * result + (error?.hashCode() ?: 0)
        result = 31 * result + (roomId?.hashCode() ?: 0)
        result = 31 * result + (seat ?: 0)
        result = 31 * result + (resumeToken?.hashCode() ?: 0)
        result = 31 * result + (rooms?.hashCode() ?: 0)
        result = 31 * result + (capabilities?.hashCode() ?: 0)
        result = 31 * result + (snapshot?.hashCode() ?: 0)
        result = 31 * result + (decision?.hashCode() ?: 0)
        result = 31 * result + (receipt?.hashCode() ?: 0)
        result = 31 * result + (events?.hashCode() ?: 0)
        result = 31 * result + (roomInfo?.hashCode() ?: 0)
        result = 31 * result + (spectatorBinding?.hashCode() ?: 0)
        result = 31 * result + (publicSnapshot?.hashCode() ?: 0)
        result = 31 * result + (this.publicEvents?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String {
        return "ApiResponse(requestId=$requestId, success=$success, error=$error, roomId=$roomId, seat=$seat, resumeToken=***, spectatorBinding=$spectatorBinding)"
    }
}

/**
 * 解码后的推送更新与全量标记
 */
data class DecodedPush(
    val update: ProtocolPlayerUpdate,
    val isFullSnapshot: Boolean
)

fun checkUnknownFields(obj: JsonObject, allowedKeys: Set<String>, typeName: String) {
    val unknown = obj.keySet().filter { it !in allowedKeys }
    if (unknown.isNotEmpty()) {
        throw JsonParseException("Unknown field(s) in $typeName: $unknown")
    }
}

fun validateStrictInt(elem: JsonElement, fieldName: String): Int {
    if (!elem.isJsonPrimitive || !elem.asJsonPrimitive.isNumber) {
        throw JsonParseException("$fieldName must be a numeric integer: $elem")
    }
    val raw = elem.asJsonPrimitive.asString
    if (raw.contains(".") || raw.contains("e", ignoreCase = true)) {
        throw JsonParseException("$fieldName cannot be fractional: $raw")
    }
    return try {
        raw.toInt()
    } catch (e: Exception) {
        throw JsonParseException("$fieldName integer overflow/invalid: $raw", e)
    }
}

fun validateStrictLong(elem: JsonElement, fieldName: String): Long {
    if (!elem.isJsonPrimitive || !elem.asJsonPrimitive.isNumber) {
        throw JsonParseException("$fieldName must be a numeric integer: $elem")
    }
    val raw = elem.asJsonPrimitive.asString
    if (raw.contains(".") || raw.contains("e", ignoreCase = true)) {
        throw JsonParseException("$fieldName cannot be fractional: $raw")
    }
    return try {
        raw.toLong()
    } catch (e: Exception) {
        throw JsonParseException("$fieldName long overflow/invalid: $raw", e)
    }
}

fun validateStrictDouble(elem: JsonElement, fieldName: String): Double {
    if (!elem.isJsonPrimitive || !elem.asJsonPrimitive.isNumber) {
        throw JsonParseException("$fieldName must be a numeric double: $elem")
    }
    val d = try {
        elem.asDouble
    } catch (e: Exception) {
        throw JsonParseException("$fieldName double invalid: $elem", e)
    }
    if (d.isNaN() || d.isInfinite()) {
        throw JsonParseException("$fieldName cannot be NaN or Infinite: $d")
    }
    return d
}

fun validateStrictBoolean(elem: JsonElement, fieldName: String): Boolean {
    if (!elem.isJsonPrimitive || !elem.asJsonPrimitive.isBoolean) {
        throw JsonParseException("$fieldName must be a boolean: $elem")
    }
    return elem.asBoolean
}

fun validateStrictString(elem: JsonElement, fieldName: String): String {
    if (!elem.isJsonPrimitive || !elem.asJsonPrimitive.isString) {
        throw JsonParseException("$fieldName must be a string: $elem")
    }
    return elem.asString
}

fun validateStrictOptionalString(elem: JsonElement?, fieldName: String): String? {
    if (elem == null || elem.isJsonNull) return null
    return validateStrictString(elem, fieldName)
}

fun validateStrictIntList(elem: JsonElement, fieldName: String): List<Int> {
    if (!elem.isJsonArray) {
        throw JsonParseException("$fieldName must be a JSON array: $elem")
    }
    val arr = elem.asJsonArray
    val list = ArrayList<Int>(arr.size())
    for (i in 0 until arr.size()) {
        list.add(validateStrictInt(arr.get(i), "$fieldName[$i]"))
    }
    return Collections.unmodifiableList(list)
}

fun validateStrictBooleanList(elem: JsonElement, fieldName: String): List<Boolean> {
    if (!elem.isJsonArray) {
        throw JsonParseException("$fieldName must be a JSON array: $elem")
    }
    val arr = elem.asJsonArray
    val list = ArrayList<Boolean>(arr.size())
    for (i in 0 until arr.size()) {
        list.add(validateStrictBoolean(arr.get(i), "$fieldName[$i]"))
    }
    return Collections.unmodifiableList(list)
}

fun validateStrictStringList(elem: JsonElement, fieldName: String): List<String> {
    if (!elem.isJsonArray) {
        throw JsonParseException("$fieldName must be a JSON array: $elem")
    }
    val arr = elem.asJsonArray
    val list = ArrayList<String>(arr.size())
    for (i in 0 until arr.size()) {
        list.add(validateStrictString(arr.get(i), "$fieldName[$i]"))
    }
    return Collections.unmodifiableList(list)
}

/**
 * ProtocolTile TypeAdapter (强制 136 张物理牌自洽与赤五校验，杜绝 Gson 反射绕过构造校验)
 */
private class ProtocolTileAdapter : JsonSerializer<ProtocolTile>, JsonDeserializer<ProtocolTile> {
    override fun serialize(src: ProtocolTile, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("tileId", src.tileId)
        obj.addProperty("suit", src.suit.name)
        obj.addProperty("rank", src.rank)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolTile {
        if (!json.isJsonObject) {
            throw JsonParseException("ProtocolTile must be a JSON object: $json")
        }
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("tileId", "suit", "rank"), "ProtocolTile")
        val tileIdElem = obj.get("tileId") ?: throw JsonParseException("ProtocolTile missing tileId")
        val tileId = validateStrictInt(tileIdElem, "ProtocolTile.tileId")
        if (tileId !in 0..135) {
            throw JsonParseException("ProtocolTile tileId out of bounds (0..135): $tileId")
        }
        val expectedSuit = ProtocolTile.derivedSuit(tileId)
        val expectedRank = ProtocolTile.derivedRank(tileId)
        if (obj.has("suit") && !obj.get("suit").isJsonNull) {
            val suitStr = validateStrictString(obj.get("suit"), "ProtocolTile.suit")
            val suit = try {
                ProtocolSuit.valueOf(suitStr)
            } catch (e: Exception) {
                throw JsonParseException("Invalid suit in ProtocolTile: $suitStr", e)
            }
            if (suit != expectedSuit) {
                throw JsonParseException("Contradictory suit for tileId $tileId: expected $expectedSuit, got $suit")
            }
        }
        if (obj.has("rank") && !obj.get("rank").isJsonNull) {
            val rank = validateStrictInt(obj.get("rank"), "ProtocolTile.rank")
            if (rank != expectedRank) {
                throw JsonParseException("Contradictory rank for tileId $tileId: expected $expectedRank, got $rank")
            }
        }
        return ProtocolTile(tileId, expectedSuit, expectedRank)
    }
}

/**
 * ProtocolParamValue 多态 TypeAdapter
 * 严格结构化类型反序列化，禁止降级回退或宽松类型隐式转换。
 */
private class ProtocolParamValueAdapter : JsonSerializer<ProtocolParamValue>, JsonDeserializer<ProtocolParamValue> {
    override fun serialize(src: ProtocolParamValue, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        when (src) {
            is ProtocolParamValue.IntValue -> {
                obj.addProperty("type", "INT")
                obj.addProperty("value", src.value)
            }
            is ProtocolParamValue.LongValue -> {
                obj.addProperty("type", "LONG")
                obj.addProperty("value", src.value)
            }
            is ProtocolParamValue.DoubleValue -> {
                obj.addProperty("type", "DOUBLE")
                obj.addProperty("value", src.value)
            }
            is ProtocolParamValue.StringValue -> {
                obj.addProperty("type", "STRING")
                obj.addProperty("value", src.value)
            }
            is ProtocolParamValue.BooleanValue -> {
                obj.addProperty("type", "BOOLEAN")
                obj.addProperty("value", src.value)
            }
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolParamValue {
        if (!json.isJsonObject) {
            throw JsonParseException("ProtocolParamValue must be a JSON object with type and value: $json")
        }
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("type", "value"), "ProtocolParamValue")
        val typeElem = obj.get("type") ?: throw JsonParseException("Missing type in ProtocolParamValue")
        val type = validateStrictString(typeElem, "ProtocolParamValue.type")
        val valElem = obj.get("value") ?: throw JsonParseException("Missing value in ProtocolParamValue")
        return when (type) {
            "INT" -> ProtocolParamValue.IntValue(validateStrictInt(valElem, "ProtocolParamValue.value"))
            "LONG" -> ProtocolParamValue.LongValue(validateStrictLong(valElem, "ProtocolParamValue.value"))
            "DOUBLE" -> ProtocolParamValue.DoubleValue(validateStrictDouble(valElem, "ProtocolParamValue.value"))
            "STRING" -> ProtocolParamValue.StringValue(validateStrictString(valElem, "ProtocolParamValue.value"))
            "BOOLEAN" -> ProtocolParamValue.BooleanValue(validateStrictBoolean(valElem, "ProtocolParamValue.value"))
            else -> throw JsonParseException("Unknown ProtocolParamValue type: $type")
        }
    }
}

/**
 * ProtocolLegalAction 多态 TypeAdapter
 */
private class ProtocolLegalActionAdapter : JsonSerializer<ProtocolLegalAction>, JsonDeserializer<ProtocolLegalAction> {
    override fun serialize(src: ProtocolLegalAction, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val elem = context.serialize(src, src.javaClass).asJsonObject
        elem.addProperty("actionType", src.actionType)
        return elem
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolLegalAction {
        if (!json.isJsonObject) throw JsonParseException("ProtocolLegalAction must be a JSON object")
        val obj = json.asJsonObject
        val actionTypeElem = obj.get("actionType")
            ?: throw JsonParseException("Missing actionType in ProtocolLegalAction")
        val actionType = validateStrictString(actionTypeElem, "actionType")
        return when (actionType) {
            "StartStacking" -> {
                checkUnknownFields(obj, setOf("actionType", "stackingPlans"), "StartStacking")
                val stackingPlans: List<String> = if (obj.has("stackingPlans") && !obj.get("stackingPlans").isJsonNull) {
                    val arr = obj.getAsJsonArray("stackingPlans")
                    val list = ArrayList<String>(arr.size())
                    for (elem in arr) list.add(validateStrictString(elem, "stackingPlans"))
                    Collections.unmodifiableList(list)
                } else emptyList()
                ProtocolLegalAction.StartStacking(stackingPlans)
            }
            "DealComplete" -> {
                checkUnknownFields(obj, setOf("actionType"), "DealComplete")
                ProtocolLegalAction.DealComplete
            }
            "DrawTile" -> {
                checkUnknownFields(obj, setOf("actionType", "player"), "DrawTile")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DrawTile")
                val p = validateStrictInt(pElem, "DrawTile.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                ProtocolLegalAction.DrawTile(p)
            }
            "DiscardTile" -> {
                checkUnknownFields(obj, setOf("actionType", "player", "tile"), "DiscardTile")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DiscardTile")
                val p = validateStrictInt(pElem, "DiscardTile.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                val tileElem = obj.get("tile") ?: throw JsonParseException("Missing tile in DiscardTile")
                ProtocolLegalAction.DiscardTile(p, context.deserialize(tileElem, ProtocolTile::class.java))
            }
            "DeclareChi" -> {
                checkUnknownFields(obj, setOf("actionType", "player", "meld"), "DeclareChi")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareChi")
                val p = validateStrictInt(pElem, "DeclareChi.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                val meldElem = obj.get("meld") ?: throw JsonParseException("Missing meld in DeclareChi")
                ProtocolLegalAction.DeclareChi(p, context.deserialize(meldElem, ProtocolMeld::class.java))
            }
            "DeclarePon" -> {
                checkUnknownFields(obj, setOf("actionType", "player", "tiles"), "DeclarePon")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclarePon")
                val p = validateStrictInt(pElem, "DeclarePon.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                val tilesElem = obj.get("tiles") ?: throw JsonParseException("Missing tiles in DeclarePon")
                if (!tilesElem.isJsonArray) throw JsonParseException("tiles must be an array")
                val arr = tilesElem.asJsonArray
                val list = ArrayList<ProtocolTile>(arr.size())
                for (elem in arr) list.add(context.deserialize(elem, ProtocolTile::class.java))
                ProtocolLegalAction.DeclarePon(p, Collections.unmodifiableList(list))
            }
            "DeclareKan" -> {
                checkUnknownFields(obj, setOf("actionType", "player", "tiles", "kanType"), "DeclareKan")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareKan")
                val p = validateStrictInt(pElem, "DeclareKan.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                val tilesElem = obj.get("tiles") ?: throw JsonParseException("Missing tiles in DeclareKan")
                if (!tilesElem.isJsonArray) throw JsonParseException("tiles must be an array")
                val arr = tilesElem.asJsonArray
                val list = ArrayList<ProtocolTile>(arr.size())
                for (elem in arr) list.add(context.deserialize(elem, ProtocolTile::class.java))
                val kanTypeElem = obj.get("kanType") ?: throw JsonParseException("Missing kanType in DeclareKan")
                val kanTypeStr = validateStrictString(kanTypeElem, "DeclareKan.kanType")
                val kanType = try {
                    ProtocolMeldType.valueOf(kanTypeStr)
                } catch (e: Exception) {
                    throw JsonParseException("Invalid kanType in DeclareKan: $kanTypeStr", e)
                }
                ProtocolLegalAction.DeclareKan(p, Collections.unmodifiableList(list), kanType)
            }
            "DeclareRiichi" -> {
                checkUnknownFields(obj, setOf("actionType", "player", "discardTile"), "DeclareRiichi")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareRiichi")
                val p = validateStrictInt(pElem, "DeclareRiichi.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                val tileElem = obj.get("discardTile") ?: throw JsonParseException("Missing discardTile in DeclareRiichi")
                ProtocolLegalAction.DeclareRiichi(p, context.deserialize(tileElem, ProtocolTile::class.java))
            }
            "DeclareTsumo" -> {
                checkUnknownFields(obj, setOf("actionType", "player"), "DeclareTsumo")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareTsumo")
                val p = validateStrictInt(pElem, "DeclareTsumo.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                ProtocolLegalAction.DeclareTsumo(p)
            }
            "DeclareRon" -> {
                checkUnknownFields(obj, setOf("actionType", "player"), "DeclareRon")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareRon")
                val p = validateStrictInt(pElem, "DeclareRon.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                ProtocolLegalAction.DeclareRon(p)
            }
            "UseCheatTechnique" -> {
                checkUnknownFields(obj, setOf("actionType", "player", "technique", "params"), "UseCheatTechnique")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in UseCheatTechnique")
                val p = validateStrictInt(pElem, "UseCheatTechnique.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                val techElem = obj.get("technique") ?: throw JsonParseException("Missing technique in UseCheatTechnique")
                val techStr = validateStrictString(techElem, "UseCheatTechnique.technique")
                val technique = try {
                    ProtocolCheatTechnique.valueOf(techStr)
                } catch (e: Exception) {
                    throw JsonParseException("Invalid technique in UseCheatTechnique: $techStr", e)
                }
                val params: Map<String, ProtocolParamValue> = if (obj.has("params") && !obj.get("params").isJsonNull) {
                    val pElem = obj.get("params")
                    if (!pElem.isJsonObject) throw JsonParseException("params must be an object")
                    val pMap = LinkedHashMap<String, ProtocolParamValue>()
                    for ((k, v) in pElem.asJsonObject.entrySet()) {
                        pMap[k] = context.deserialize(v, ProtocolParamValue::class.java)
                    }
                    Collections.unmodifiableMap(pMap)
                } else emptyMap()
                ProtocolLegalAction.UseCheatTechnique(p, technique, params)
            }
            "Pass" -> {
                checkUnknownFields(obj, setOf("actionType", "player"), "Pass")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in Pass")
                val p = validateStrictInt(pElem, "Pass.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                ProtocolLegalAction.Pass(p)
            }
            "DeclareAbortiveDraw" -> {
                checkUnknownFields(obj, setOf("actionType", "player"), "DeclareAbortiveDraw")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareAbortiveDraw")
                val p = validateStrictInt(pElem, "DeclareAbortiveDraw.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                ProtocolLegalAction.DeclareAbortiveDraw(p)
            }
            "DeclareAgariYame" -> {
                checkUnknownFields(obj, setOf("actionType", "player"), "DeclareAgariYame")
                val pElem = obj.get("player") ?: throw JsonParseException("Missing player in DeclareAgariYame")
                val p = validateStrictInt(pElem, "DeclareAgariYame.player")
                if (p !in 0..3) throw JsonParseException("player must be in 0..3: $p")
                ProtocolLegalAction.DeclareAgariYame(p)
            }
            else -> throw JsonParseException("Unknown actionType: $actionType")
        }
    }
}

/**
 * ProtocolGameEvent 多态 TypeAdapter
 */
private class ProtocolGameEventAdapter : JsonSerializer<ProtocolGameEvent>, JsonDeserializer<ProtocolGameEvent> {
    override fun serialize(src: ProtocolGameEvent, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val elem = context.serialize(src, src.javaClass).asJsonObject
        elem.addProperty("eventType", src.eventType)
        return elem
    }

    private fun requireTurnNumber(obj: JsonObject, eventType: String): Int {
        val elem = obj.get("turnNumber") ?: throw JsonParseException("Missing turnNumber in $eventType")
        return validateStrictInt(elem, "$eventType.turnNumber")
    }

    private fun validateSeat(seat: Int, fieldName: String): Int {
        if (seat !in 0..3) throw JsonParseException("$fieldName must be in 0..3: $seat")
        return seat
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolGameEvent {
        if (!json.isJsonObject) throw JsonParseException("ProtocolGameEvent must be a JSON object")
        val obj = json.asJsonObject
        val eventTypeElem = obj.get("eventType")
            ?: throw JsonParseException("Missing eventType in ProtocolGameEvent")
        val eventType = validateStrictString(eventTypeElem, "eventType")
        return when (eventType) {
            "StackingStarted" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber"), "StackingStarted")
                val turn = obj.get("turnNumber")?.let { validateStrictInt(it, "StackingStarted.turnNumber") } ?: 0
                ProtocolGameEvent.StackingStarted(turn)
            }
            "WallBuilt" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "wallSize"), "WallBuilt")
                val turn = obj.get("turnNumber")?.let { validateStrictInt(it, "WallBuilt.turnNumber") } ?: 0
                val wallSize = obj.get("wallSize")?.let { validateStrictInt(it, "wallSize") } ?: 136
                ProtocolGameEvent.WallBuilt(turn, wallSize)
            }
            "TileDealt" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "tile"), "TileDealt")
                val turn = requireTurnNumber(obj, "TileDealt")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in TileDealt"), "TileDealt.player"), "TileDealt.player")
                val tileElem = obj.get("tile") ?: throw JsonParseException("Missing tile in TileDealt")
                ProtocolGameEvent.TileDealt(turn, player, context.deserialize(tileElem, ProtocolTile::class.java))
            }
            "DealingComplete" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber"), "DealingComplete")
                val turn = obj.get("turnNumber")?.let { validateStrictInt(it, "DealingComplete.turnNumber") } ?: 0
                ProtocolGameEvent.DealingComplete(turn)
            }
            "TileDrawn" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "tile", "wallRemaining"), "TileDrawn")
                val turn = requireTurnNumber(obj, "TileDrawn")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in TileDrawn"), "TileDrawn.player"), "TileDrawn.player")
                val tileElem = obj.get("tile") ?: throw JsonParseException("Missing tile in TileDrawn")
                val wallRemaining = validateStrictInt(obj.get("wallRemaining") ?: throw JsonParseException("Missing wallRemaining in TileDrawn"), "TileDrawn.wallRemaining")
                ProtocolGameEvent.TileDrawn(turn, player, context.deserialize(tileElem, ProtocolTile::class.java), wallRemaining)
            }
            "TileDiscarded" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "tile", "isRiichiDiscard", "isTsumogiri"), "TileDiscarded")
                val turn = requireTurnNumber(obj, "TileDiscarded")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in TileDiscarded"), "TileDiscarded.player"), "TileDiscarded.player")
                val tileElem = obj.get("tile") ?: throw JsonParseException("Missing tile in TileDiscarded")
                val isRiichiDiscard = if (obj.has("isRiichiDiscard") && !obj.get("isRiichiDiscard").isJsonNull) {
                    validateStrictBoolean(obj.get("isRiichiDiscard"), "isRiichiDiscard")
                } else false
                val isTsumogiri = if (obj.has("isTsumogiri") && !obj.get("isTsumogiri").isJsonNull) {
                    validateStrictBoolean(obj.get("isTsumogiri"), "isTsumogiri")
                } else false
                ProtocolGameEvent.TileDiscarded(turn, player, context.deserialize(tileElem, ProtocolTile::class.java), isRiichiDiscard, isTsumogiri)
            }
            "ChiDeclared" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "meld", "fromPlayer"), "ChiDeclared")
                val turn = requireTurnNumber(obj, "ChiDeclared")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in ChiDeclared"), "ChiDeclared.player"), "ChiDeclared.player")
                val meldElem = obj.get("meld") ?: throw JsonParseException("Missing meld in ChiDeclared")
                val fromPlayer = validateSeat(validateStrictInt(obj.get("fromPlayer") ?: throw JsonParseException("Missing fromPlayer in ChiDeclared"), "ChiDeclared.fromPlayer"), "ChiDeclared.fromPlayer")
                ProtocolGameEvent.ChiDeclared(turn, player, context.deserialize(meldElem, ProtocolMeld::class.java), fromPlayer)
            }
            "PonDeclared" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "meld", "fromPlayer"), "PonDeclared")
                val turn = requireTurnNumber(obj, "PonDeclared")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in PonDeclared"), "PonDeclared.player"), "PonDeclared.player")
                val meldElem = obj.get("meld") ?: throw JsonParseException("Missing meld in PonDeclared")
                val fromPlayer = validateSeat(validateStrictInt(obj.get("fromPlayer") ?: throw JsonParseException("Missing fromPlayer in PonDeclared"), "PonDeclared.fromPlayer"), "PonDeclared.fromPlayer")
                ProtocolGameEvent.PonDeclared(turn, player, context.deserialize(meldElem, ProtocolMeld::class.java), fromPlayer)
            }
            "KanDeclared" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "meld", "kanType"), "KanDeclared")
                val turn = requireTurnNumber(obj, "KanDeclared")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in KanDeclared"), "KanDeclared.player"), "KanDeclared.player")
                val meldElem = obj.get("meld") ?: throw JsonParseException("Missing meld in KanDeclared")
                val kanTypeElem = obj.get("kanType") ?: throw JsonParseException("Missing kanType in KanDeclared")
                ProtocolGameEvent.KanDeclared(turn, player, context.deserialize(meldElem, ProtocolMeld::class.java), context.deserialize(kanTypeElem, ProtocolMeldType::class.java))
            }
            "RinshanDraw" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "tile"), "RinshanDraw")
                val turn = requireTurnNumber(obj, "RinshanDraw")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in RinshanDraw"), "RinshanDraw.player"), "RinshanDraw.player")
                val tileElem = obj.get("tile") ?: throw JsonParseException("Missing tile in RinshanDraw")
                ProtocolGameEvent.RinshanDraw(turn, player, context.deserialize(tileElem, ProtocolTile::class.java))
            }
            "RiichiDeclared" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "discardTile", "isDoubleRiichi"), "RiichiDeclared")
                val turn = requireTurnNumber(obj, "RiichiDeclared")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in RiichiDeclared"), "RiichiDeclared.player"), "RiichiDeclared.player")
                val tileElem = obj.get("discardTile") ?: throw JsonParseException("Missing discardTile in RiichiDeclared")
                val isDoubleRiichi = if (obj.has("isDoubleRiichi") && !obj.get("isDoubleRiichi").isJsonNull) {
                    validateStrictBoolean(obj.get("isDoubleRiichi"), "isDoubleRiichi")
                } else false
                ProtocolGameEvent.RiichiDeclared(turn, player, context.deserialize(tileElem, ProtocolTile::class.java), isDoubleRiichi)
            }
            "TsumoWin" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "winTile", "yakuList", "han", "fu", "score", "breakdown"), "TsumoWin")
                val turn = requireTurnNumber(obj, "TsumoWin")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in TsumoWin"), "TsumoWin.player"), "TsumoWin.player")
                val winTile = context.deserialize<ProtocolTile>(obj.get("winTile") ?: throw JsonParseException("Missing winTile in TsumoWin"), ProtocolTile::class.java)
                val yakuListElem = obj.get("yakuList") ?: throw JsonParseException("Missing yakuList in TsumoWin")
                val yakuList = validateStrictStringList(yakuListElem, "TsumoWin.yakuList")
                val han = validateStrictInt(obj.get("han") ?: throw JsonParseException("Missing han in TsumoWin"), "TsumoWin.han")
                val fu = validateStrictInt(obj.get("fu") ?: throw JsonParseException("Missing fu in TsumoWin"), "TsumoWin.fu")
                val score = validateStrictInt(obj.get("score") ?: throw JsonParseException("Missing score in TsumoWin"), "TsumoWin.score")
                val breakdown: ProtocolScoreBreakdown? = if (obj.has("breakdown") && !obj.get("breakdown").isJsonNull) {
                    context.deserialize(obj.get("breakdown"), ProtocolScoreBreakdown::class.java)
                } else null
                ProtocolGameEvent.TsumoWin(turn, player, winTile, yakuList, han, fu, score, breakdown)
            }
            "RonWin" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "winner", "loser", "winTile", "yakuList", "han", "fu", "score", "breakdown"), "RonWin")
                val turn = requireTurnNumber(obj, "RonWin")
                val winner = validateSeat(validateStrictInt(obj.get("winner") ?: throw JsonParseException("Missing winner in RonWin"), "RonWin.winner"), "RonWin.winner")
                val loser = validateSeat(validateStrictInt(obj.get("loser") ?: throw JsonParseException("Missing loser in RonWin"), "RonWin.loser"), "RonWin.loser")
                val winTile = context.deserialize<ProtocolTile>(obj.get("winTile") ?: throw JsonParseException("Missing winTile in RonWin"), ProtocolTile::class.java)
                val yakuListElem = obj.get("yakuList") ?: throw JsonParseException("Missing yakuList in RonWin")
                val yakuList = validateStrictStringList(yakuListElem, "RonWin.yakuList")
                val han = validateStrictInt(obj.get("han") ?: throw JsonParseException("Missing han in RonWin"), "RonWin.han")
                val fu = validateStrictInt(obj.get("fu") ?: throw JsonParseException("Missing fu in RonWin"), "RonWin.fu")
                val score = validateStrictInt(obj.get("score") ?: throw JsonParseException("Missing score in RonWin"), "RonWin.score")
                val breakdown: ProtocolScoreBreakdown? = if (obj.has("breakdown") && !obj.get("breakdown").isJsonNull) {
                    context.deserialize(obj.get("breakdown"), ProtocolScoreBreakdown::class.java)
                } else null
                ProtocolGameEvent.RonWin(turn, winner, loser, winTile, yakuList, han, fu, score, breakdown)
            }
            "DoraRevealed" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "indicator", "isUraDora"), "DoraRevealed")
                val turn = requireTurnNumber(obj, "DoraRevealed")
                val indicator = context.deserialize<ProtocolTile>(obj.get("indicator") ?: throw JsonParseException("Missing indicator in DoraRevealed"), ProtocolTile::class.java)
                val isUraDora = if (obj.has("isUraDora") && !obj.get("isUraDora").isJsonNull) {
                    validateStrictBoolean(obj.get("isUraDora"), "isUraDora")
                } else false
                ProtocolGameEvent.DoraRevealed(turn, indicator, isUraDora)
            }
            "CheatAttempted" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "technique", "success"), "CheatAttempted")
                val turn = requireTurnNumber(obj, "CheatAttempted")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in CheatAttempted"), "CheatAttempted.player"), "CheatAttempted.player")
                val technique = context.deserialize<ProtocolCheatTechnique>(obj.get("technique") ?: throw JsonParseException("Missing technique in CheatAttempted"), ProtocolCheatTechnique::class.java)
                val success = validateStrictBoolean(obj.get("success") ?: throw JsonParseException("Missing success in CheatAttempted"), "CheatAttempted.success")
                ProtocolGameEvent.CheatAttempted(turn, player, technique, success)
            }
            "SuspicionChanged" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "target", "observer", "oldValue", "newValue"), "SuspicionChanged")
                val turn = requireTurnNumber(obj, "SuspicionChanged")
                val target = validateSeat(validateStrictInt(obj.get("target") ?: throw JsonParseException("Missing target in SuspicionChanged"), "SuspicionChanged.target"), "SuspicionChanged.target")
                val observer = validateSeat(validateStrictInt(obj.get("observer") ?: throw JsonParseException("Missing observer in SuspicionChanged"), "SuspicionChanged.observer"), "SuspicionChanged.observer")
                val oldValue = validateStrictDouble(obj.get("oldValue") ?: throw JsonParseException("Missing oldValue in SuspicionChanged"), "SuspicionChanged.oldValue").toFloat()
                val newValue = validateStrictDouble(obj.get("newValue") ?: throw JsonParseException("Missing newValue in SuspicionChanged"), "SuspicionChanged.newValue").toFloat()
                ProtocolGameEvent.SuspicionChanged(turn, target, observer, oldValue, newValue)
            }
            "BustedEvent" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "player", "caughtBy", "technique"), "BustedEvent")
                val turn = requireTurnNumber(obj, "BustedEvent")
                val player = validateSeat(validateStrictInt(obj.get("player") ?: throw JsonParseException("Missing player in BustedEvent"), "BustedEvent.player"), "BustedEvent.player")
                val caughtBy = validateSeat(validateStrictInt(obj.get("caughtBy") ?: throw JsonParseException("Missing caughtBy in BustedEvent"), "BustedEvent.caughtBy"), "BustedEvent.caughtBy")
                val technique = context.deserialize<ProtocolCheatTechnique>(obj.get("technique") ?: throw JsonParseException("Missing technique in BustedEvent"), ProtocolCheatTechnique::class.java)
                ProtocolGameEvent.BustedEvent(turn, player, caughtBy, technique)
            }
            "GazeShifted" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "observer", "newTarget", "oldTarget"), "GazeShifted")
                val turn = requireTurnNumber(obj, "GazeShifted")
                val observer = validateSeat(validateStrictInt(obj.get("observer") ?: throw JsonParseException("Missing observer in GazeShifted"), "GazeShifted.observer"), "GazeShifted.observer")
                val newTarget = if (obj.has("newTarget") && !obj.get("newTarget").isJsonNull) {
                    validateSeat(validateStrictInt(obj.get("newTarget"), "newTarget"), "newTarget")
                } else null
                val oldTarget = if (obj.has("oldTarget") && !obj.get("oldTarget").isJsonNull) {
                    validateSeat(validateStrictInt(obj.get("oldTarget"), "oldTarget"), "oldTarget")
                } else null
                ProtocolGameEvent.GazeShifted(turn, observer, newTarget, oldTarget)
            }
            "CheatPointsChanged" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "seat", "amount", "newBalance"), "CheatPointsChanged")
                val turn = requireTurnNumber(obj, "CheatPointsChanged")
                val seat = validateSeat(validateStrictInt(obj.get("seat") ?: throw JsonParseException("Missing seat in CheatPointsChanged"), "CheatPointsChanged.seat"), "CheatPointsChanged.seat")
                val amount = validateStrictInt(obj.get("amount") ?: throw JsonParseException("Missing amount in CheatPointsChanged"), "CheatPointsChanged.amount")
                val newBalance = validateStrictInt(obj.get("newBalance") ?: throw JsonParseException("Missing newBalance in CheatPointsChanged"), "CheatPointsChanged.newBalance")
                ProtocolGameEvent.CheatPointsChanged(turn, seat, amount, newBalance)
            }
            "DrawGame" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "reason", "tenpaiPlayers"), "DrawGame")
                val turn = requireTurnNumber(obj, "DrawGame")
                val reason = context.deserialize<ProtocolDrawReason>(obj.get("reason") ?: throw JsonParseException("Missing reason in DrawGame"), ProtocolDrawReason::class.java)
                val tenpaiPlayersElem = obj.get("tenpaiPlayers") ?: throw JsonParseException("Missing tenpaiPlayers in DrawGame")
                val tenpaiPlayers = validateStrictIntList(tenpaiPlayersElem, "DrawGame.tenpaiPlayers")
                for (p in tenpaiPlayers) validateSeat(p, "DrawGame.tenpaiPlayers seat")
                ProtocolGameEvent.DrawGame(turn, reason, tenpaiPlayers)
            }
            "ScoreSettled" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "payments", "newScores"), "ScoreSettled")
                val turn = requireTurnNumber(obj, "ScoreSettled")
                val pElem = obj.get("payments") ?: throw JsonParseException("Missing payments in ScoreSettled")
                if (!pElem.isJsonObject) throw JsonParseException("payments must be a JSON object in ScoreSettled")
                val pObj = pElem.asJsonObject
                val map = LinkedHashMap<Int, Int>()
                for ((k, v) in pObj.entrySet()) {
                    val key = try {
                        if (k.contains(".") || k.contains("e", ignoreCase = true)) throw IllegalArgumentException()
                        k.toInt()
                    } catch (e: Exception) {
                        throw JsonParseException("Payment seat key must be an integer: $k", e)
                    }
                    validateSeat(key, "payments seat")
                    val value = validateStrictInt(v, "payments.$k")
                    map[key] = value
                }
                val newScoresElem = obj.get("newScores") ?: throw JsonParseException("Missing newScores in ScoreSettled")
                val newScores = validateStrictIntList(newScoresElem, "ScoreSettled.newScores")
                ProtocolGameEvent.ScoreSettled(turn, Collections.unmodifiableMap(map), newScores)
            }
            "RoundEnded" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "roundWind", "roundNumber"), "RoundEnded")
                val turn = requireTurnNumber(obj, "RoundEnded")
                val roundWind = validateStrictInt(obj.get("roundWind") ?: throw JsonParseException("Missing roundWind in RoundEnded"), "RoundEnded.roundWind")
                if (roundWind !in 1..4) throw JsonParseException("roundWind must be in 1..4: $roundWind")
                val roundNumber = validateStrictInt(obj.get("roundNumber") ?: throw JsonParseException("Missing roundNumber in RoundEnded"), "RoundEnded.roundNumber")
                ProtocolGameEvent.RoundEnded(turn, roundWind, roundNumber)
            }
            "MatchEnded" -> {
                checkUnknownFields(obj, setOf("eventType", "turnNumber", "finalScores", "winner"), "MatchEnded")
                val turn = requireTurnNumber(obj, "MatchEnded")
                val finalScoresElem = obj.get("finalScores") ?: throw JsonParseException("Missing finalScores in MatchEnded")
                val finalScores = validateStrictIntList(finalScoresElem, "MatchEnded.finalScores")
                val winner = validateSeat(validateStrictInt(obj.get("winner") ?: throw JsonParseException("Missing winner in MatchEnded"), "MatchEnded.winner"), "MatchEnded.winner")
                ProtocolGameEvent.MatchEnded(turn, finalScores, winner)
            }
            else -> throw JsonParseException("Unknown eventType: $eventType")
        }
    }
}

/**
 * ProtocolMeld JsonDeserializer (确保集合不可变)
 */
private class ProtocolMeldAdapter : JsonDeserializer<ProtocolMeld> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolMeld {
        if (!json.isJsonObject) throw JsonParseException("ProtocolMeld must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("type", "tiles", "fromPlayer", "calledTile"), "ProtocolMeld")
        val typeElem = obj.get("type") ?: throw JsonParseException("Missing type in ProtocolMeld")
        val type = context.deserialize<ProtocolMeldType>(typeElem, ProtocolMeldType::class.java)
        val tilesElem = obj.get("tiles") ?: throw JsonParseException("Missing tiles in ProtocolMeld")
        if (!tilesElem.isJsonArray) throw JsonParseException("tiles must be an array")
        val arr = tilesElem.asJsonArray
        val tList = ArrayList<ProtocolTile>(arr.size())
        for (elem in arr) tList.add(context.deserialize(elem, ProtocolTile::class.java))
        val tiles = Collections.unmodifiableList(tList)
        val fromPlayerElem = obj.get("fromPlayer") ?: throw JsonParseException("Missing fromPlayer in ProtocolMeld")
        val fromPlayer = validateStrictInt(fromPlayerElem, "fromPlayer")
        val calledTile: ProtocolTile? = if (obj.has("calledTile") && !obj.get("calledTile").isJsonNull) {
            context.deserialize(obj.get("calledTile"), ProtocolTile::class.java)
        } else null
        return ProtocolMeld(type, tiles, fromPlayer, calledTile)
    }
}

/**
 * ProtocolCheatEconomyConfig JsonDeserializer
 */
private class ProtocolCheatEconomyConfigAdapter : JsonDeserializer<ProtocolCheatEconomyConfig> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolCheatEconomyConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolCheatEconomyConfig must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("enabled", "initialPoints", "costs"), "ProtocolCheatEconomyConfig")
        val enabled = if (obj.has("enabled") && !obj.get("enabled").isJsonNull) {
            validateStrictBoolean(obj.get("enabled"), "enabled")
        } else false
        val initialPoints: List<Int> = if (obj.has("initialPoints") && !obj.get("initialPoints").isJsonNull) {
            validateStrictIntList(obj.get("initialPoints"), "initialPoints")
        } else listOf(0, 0, 0, 0)
        val costs: Map<String, Int> = if (obj.has("costs") && !obj.get("costs").isJsonNull) {
            val cElem = obj.get("costs")
            if (!cElem.isJsonObject) throw JsonParseException("costs must be an object")
            val map = LinkedHashMap<String, Int>()
            for ((k, v) in cElem.asJsonObject.entrySet()) {
                map[k] = validateStrictInt(v, "costs.$k")
            }
            Collections.unmodifiableMap(map)
        } else emptyMap()
        return ProtocolCheatEconomyConfig(enabled, initialPoints, costs)
    }
}

/**
 * ProtocolShuffleConfig JsonDeserializer
 */
private class ProtocolShuffleConfigAdapter : JsonDeserializer<ProtocolShuffleConfig> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolShuffleConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolShuffleConfig must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("enabled", "selectionMillis", "plannedDraws", "strength", "interference", "aiMaySpend"), "ProtocolShuffleConfig")
        val enabled = if (obj.has("enabled") && !obj.get("enabled").isJsonNull) {
            validateStrictBoolean(obj.get("enabled"), "enabled")
        } else false
        val selectionMillis = if (obj.has("selectionMillis") && !obj.get("selectionMillis").isJsonNull) {
            val v = validateStrictLong(obj.get("selectionMillis"), "selectionMillis")
            if (v <= 0L) throw JsonParseException("selectionMillis must be positive: $v")
            v
        } else 10000L
        val plannedDraws = if (obj.has("plannedDraws") && !obj.get("plannedDraws").isJsonNull) {
            val v = validateStrictInt(obj.get("plannedDraws"), "plannedDraws")
            if (v !in 0..18) throw JsonParseException("plannedDraws must be in 0..18: $v")
            v
        } else 8
        val strength = if (obj.has("strength") && !obj.get("strength").isJsonNull) {
            val v = validateStrictDouble(obj.get("strength"), "strength")
            if (v !in 0.0..1.0) throw JsonParseException("strength must be in 0.0..1.0: $v")
            v
        } else 0.75
        val interference = if (obj.has("interference") && !obj.get("interference").isJsonNull) {
            val v = validateStrictDouble(obj.get("interference"), "interference")
            if (v < 0.0) throw JsonParseException("interference must be >= 0.0: $v")
            v
        } else 0.65
        val aiMaySpend = if (obj.has("aiMaySpend") && !obj.get("aiMaySpend").isJsonNull) {
            validateStrictBoolean(obj.get("aiMaySpend"), "aiMaySpend")
        } else false
        return ProtocolShuffleConfig(enabled, selectionMillis, plannedDraws, strength, interference, aiMaySpend)
    }
}

/**
 * ProtocolMatchConfig JsonDeserializer (确保 13 维配置集合不可变)
 */
private class ProtocolMatchConfigAdapter : JsonDeserializer<ProtocolMatchConfig> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolMatchConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolMatchConfig must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "playerCount", "handsPerWind", "winds", "startingScores", "allowExtension",
                "allowAgariYame", "bankruptcyEnabled", "bankruptcyThreshold", "bankruptcyInclusive",
                "targetScore", "tieBreakSeats", "cheatEconomy", "shuffleConfig", "tileMarkConfig",
                "sleeveBloomConfig", "beamSwapConfig"
            ),
            "ProtocolMatchConfig"
        )
        val playerCount = if (obj.has("playerCount") && !obj.get("playerCount").isJsonNull) {
            validateStrictInt(obj.get("playerCount"), "playerCount")
        } else 4
        val handsPerWind = if (obj.has("handsPerWind") && !obj.get("handsPerWind").isJsonNull) {
            validateStrictInt(obj.get("handsPerWind"), "handsPerWind")
        } else 4
        val winds: List<Int> = if (obj.has("winds") && !obj.get("winds").isJsonNull) {
            validateStrictIntList(obj.get("winds"), "winds")
        } else listOf(1, 2)
        val startingScores: List<Int> = if (obj.has("startingScores") && !obj.get("startingScores").isJsonNull) {
            validateStrictIntList(obj.get("startingScores"), "startingScores")
        } else listOf(25000, 25000, 25000, 25000)
        val allowExtension = if (obj.has("allowExtension") && !obj.get("allowExtension").isJsonNull) {
            validateStrictBoolean(obj.get("allowExtension"), "allowExtension")
        } else true
        val allowAgariYame = if (obj.has("allowAgariYame") && !obj.get("allowAgariYame").isJsonNull) {
            validateStrictBoolean(obj.get("allowAgariYame"), "allowAgariYame")
        } else true
        val bankruptcyEnabled = if (obj.has("bankruptcyEnabled") && !obj.get("bankruptcyEnabled").isJsonNull) {
            validateStrictBoolean(obj.get("bankruptcyEnabled"), "bankruptcyEnabled")
        } else true
        val bankruptcyThreshold = if (obj.has("bankruptcyThreshold") && !obj.get("bankruptcyThreshold").isJsonNull) {
            validateStrictInt(obj.get("bankruptcyThreshold"), "bankruptcyThreshold")
        } else 0
        val bankruptcyInclusive = if (obj.has("bankruptcyInclusive") && !obj.get("bankruptcyInclusive").isJsonNull) {
            validateStrictBoolean(obj.get("bankruptcyInclusive"), "bankruptcyInclusive")
        } else false
        val targetScore = if (obj.has("targetScore") && !obj.get("targetScore").isJsonNull) {
            validateStrictInt(obj.get("targetScore"), "targetScore")
        } else 30000
        val tieBreakSeats: List<Int> = if (obj.has("tieBreakSeats") && !obj.get("tieBreakSeats").isJsonNull) {
            validateStrictIntList(obj.get("tieBreakSeats"), "tieBreakSeats")
        } else listOf(0, 1, 2, 3)
        val cheatEconomy: ProtocolCheatEconomyConfig = if (obj.has("cheatEconomy") && !obj.get("cheatEconomy").isJsonNull) {
            context.deserialize(obj.get("cheatEconomy"), ProtocolCheatEconomyConfig::class.java)
        } else ProtocolCheatEconomyConfig()
        val shuffleConfig: ProtocolShuffleConfig = if (obj.has("shuffleConfig") && !obj.get("shuffleConfig").isJsonNull) {
            context.deserialize(obj.get("shuffleConfig"), ProtocolShuffleConfig::class.java)
        } else ProtocolShuffleConfig()
        val tileMarkConfig: ProtocolTileMarkConfig = if (obj.has("tileMarkConfig") && !obj.get("tileMarkConfig").isJsonNull) {
            context.deserialize(obj.get("tileMarkConfig"), ProtocolTileMarkConfig::class.java)
        } else ProtocolTileMarkConfig()
        val sleeveBloomConfig: ProtocolSleeveBloomConfig = if (obj.has("sleeveBloomConfig") && !obj.get("sleeveBloomConfig").isJsonNull) {
            context.deserialize(obj.get("sleeveBloomConfig"), ProtocolSleeveBloomConfig::class.java)
        } else ProtocolSleeveBloomConfig()
        val beamSwapConfig: ProtocolBeamSwapConfig = if (obj.has("beamSwapConfig") && !obj.get("beamSwapConfig").isJsonNull) {
            context.deserialize(obj.get("beamSwapConfig"), ProtocolBeamSwapConfig::class.java)
        } else ProtocolBeamSwapConfig()

        return try {
            ProtocolMatchConfig(
                playerCount = playerCount,
                handsPerWind = handsPerWind,
                winds = winds,
                startingScores = startingScores,
                allowExtension = allowExtension,
                allowAgariYame = allowAgariYame,
                bankruptcyEnabled = bankruptcyEnabled,
                bankruptcyThreshold = bankruptcyThreshold,
                bankruptcyInclusive = bankruptcyInclusive,
                targetScore = targetScore,
                tieBreakSeats = tieBreakSeats,
                cheatEconomy = cheatEconomy,
                shuffleConfig = shuffleConfig,
                tileMarkConfig = tileMarkConfig,
                sleeveBloomConfig = sleeveBloomConfig,
                beamSwapConfig = beamSwapConfig
            )
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * ProtocolShuffleSelection JsonDeserializer (确保集合不可变)
 */
private class ProtocolShuffleSelectionAdapter : JsonDeserializer<ProtocolShuffleSelection> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolShuffleSelection {
        if (!json.isJsonObject) throw JsonParseException("ProtocolShuffleSelection must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("initialTargets", "drawTargets"), "ProtocolShuffleSelection")
        val initialTargets: Set<ProtocolShuffleTarget> = if (obj.has("initialTargets") && !obj.get("initialTargets").isJsonNull) {
            val arr = obj.getAsJsonArray("initialTargets")
            val set = LinkedHashSet<ProtocolShuffleTarget>()
            for (elem in arr) {
                set.add(ProtocolShuffleTarget.valueOf(validateStrictString(elem, "initialTargets")))
            }
            Collections.unmodifiableSet(set)
        } else emptySet()
        val drawTargets: Set<ProtocolShuffleTarget> = if (obj.has("drawTargets") && !obj.get("drawTargets").isJsonNull) {
            val arr = obj.getAsJsonArray("drawTargets")
            val set = LinkedHashSet<ProtocolShuffleTarget>()
            for (elem in arr) {
                set.add(ProtocolShuffleTarget.valueOf(validateStrictString(elem, "drawTargets")))
            }
            Collections.unmodifiableSet(set)
        } else emptySet()
        return ProtocolShuffleSelection(initialTargets, drawTargets)
    }
}

/**
 * ProtocolShufflePlan JsonDeserializer
 */
private class ProtocolShufflePlanAdapter : JsonDeserializer<ProtocolShufflePlan> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolShufflePlan {
        if (!json.isJsonObject) throw JsonParseException("ProtocolShufflePlan must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("requestId", "roundId", "controlEpoch", "selection"), "ProtocolShufflePlan")
        val reqId = obj.get("requestId")?.takeIf { !it.isJsonNull && it.isJsonPrimitive }?.let { validateStrictString(it, "requestId") }
            ?: throw JsonParseException("Missing requestId in ProtocolShufflePlan")
        if (reqId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val roundId = obj.get("roundId")?.takeIf { !it.isJsonNull && it.isJsonPrimitive }?.let { validateStrictString(it, "roundId") }
            ?: throw JsonParseException("Missing roundId in ProtocolShufflePlan")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val epochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolShufflePlan")
        val epoch = validateStrictLong(epochElem, "ProtocolShufflePlan.controlEpoch")
        if (epoch < 0L) throw JsonParseException("controlEpoch must be >= 0: $epoch")
        val selectionElem = obj.get("selection") ?: throw JsonParseException("Missing selection in ProtocolShufflePlan")
        val selection: ProtocolShuffleSelection = context.deserialize(selectionElem, ProtocolShuffleSelection::class.java)
        return ProtocolShufflePlan(reqId, roundId, epoch, selection)
    }
}

/**
 * ProtocolActionChoice JsonDeserializer
 */
private class ProtocolActionChoiceAdapter : JsonDeserializer<ProtocolActionChoice> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolActionChoice {
        if (!json.isJsonObject) throw JsonParseException("ProtocolActionChoice must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("requestId", "roundId", "decisionId", "controlEpoch", "optionId"), "ProtocolActionChoice")
        val reqId = obj.get("requestId")?.takeIf { !it.isJsonNull && it.isJsonPrimitive }?.let { validateStrictString(it, "requestId") }
            ?: throw JsonParseException("Missing requestId in ProtocolActionChoice")
        if (reqId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val roundId = obj.get("roundId")?.takeIf { !it.isJsonNull && it.isJsonPrimitive }?.let { validateStrictString(it, "roundId") }
        if (roundId != null && roundId.isBlank()) throw JsonParseException("roundId cannot be blank when present")
        val decId = obj.get("decisionId")?.takeIf { !it.isJsonNull && it.isJsonPrimitive }?.let { validateStrictString(it, "decisionId") }
            ?: throw JsonParseException("Missing decisionId in ProtocolActionChoice")
        if (decId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val epochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolActionChoice")
        val epoch = validateStrictLong(epochElem, "ProtocolActionChoice.controlEpoch")
        if (epoch < 0L) throw JsonParseException("controlEpoch must be >= 0: $epoch")
        val optId = obj.get("optionId")?.takeIf { !it.isJsonNull && it.isJsonPrimitive }?.let { validateStrictString(it, "optionId") }
            ?: throw JsonParseException("Missing optionId in ProtocolActionChoice")
        if (optId.isBlank()) throw JsonParseException("optionId cannot be blank")
        return ProtocolActionChoice(reqId, roundId, decId, epoch, optId)
    }
}

/**
 * ProtocolFuBreakdown JsonDeserializer (确保 lines 不可变，且必须字段不隐式赋默认值)
 */
private class ProtocolFuBreakdownAdapter : JsonDeserializer<ProtocolFuBreakdown> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolFuBreakdown {
        if (!json.isJsonObject) throw JsonParseException("ProtocolFuBreakdown must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("lines", "subtotal", "roundedUpTo", "finalFu", "specialNote"), "ProtocolFuBreakdown")
        val linesElem = obj.get("lines") ?: throw JsonParseException("Missing lines in ProtocolFuBreakdown")
        if (!linesElem.isJsonArray) throw JsonParseException("lines must be a JSON array in ProtocolFuBreakdown")
        val arr = linesElem.asJsonArray
        val list = ArrayList<ProtocolFuLine>(arr.size())
        for (elem in arr) list.add(context.deserialize(elem, ProtocolFuLine::class.java))
        val lines = Collections.unmodifiableList(list)
        val subtotal = validateStrictInt(obj.get("subtotal") ?: throw JsonParseException("Missing subtotal in ProtocolFuBreakdown"), "subtotal")
        val roundedUpTo = validateStrictInt(obj.get("roundedUpTo") ?: throw JsonParseException("Missing roundedUpTo in ProtocolFuBreakdown"), "roundedUpTo")
        val finalFu = validateStrictInt(obj.get("finalFu") ?: throw JsonParseException("Missing finalFu in ProtocolFuBreakdown"), "finalFu")
        val specialNote = obj.get("specialNote")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "specialNote") }
        return ProtocolFuBreakdown(lines, subtotal, roundedUpTo, finalFu, specialNote)
    }
}

/**
 * ProtocolDoraIndicatorHit JsonDeserializer (确保 tiles 不可变且必须字段校验)
 */
private class ProtocolDoraIndicatorHitAdapter : JsonDeserializer<ProtocolDoraIndicatorHit> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolDoraIndicatorHit {
        if (!json.isJsonObject) throw JsonParseException("ProtocolDoraIndicatorHit must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("indicator", "target", "tiles"), "ProtocolDoraIndicatorHit")
        val indicator = context.deserialize<ProtocolTile>(obj.get("indicator") ?: throw JsonParseException("Missing indicator in ProtocolDoraIndicatorHit"), ProtocolTile::class.java)
        val target = context.deserialize<ProtocolTile>(obj.get("target") ?: throw JsonParseException("Missing target in ProtocolDoraIndicatorHit"), ProtocolTile::class.java)
        val tilesElem = obj.get("tiles") ?: throw JsonParseException("Missing tiles in ProtocolDoraIndicatorHit")
        if (!tilesElem.isJsonArray) throw JsonParseException("tiles must be an array in ProtocolDoraIndicatorHit")
        val arr = tilesElem.asJsonArray
        val list = ArrayList<ProtocolTile>(arr.size())
        for (elem in arr) list.add(context.deserialize(elem, ProtocolTile::class.java))
        val tiles = Collections.unmodifiableList(list)
        return ProtocolDoraIndicatorHit(indicator, target, tiles)
    }
}

/**
 * ProtocolDoraBreakdown JsonDeserializer (确保所有列表不可变且必须字段校验)
 */
private class ProtocolDoraBreakdownAdapter : JsonDeserializer<ProtocolDoraBreakdown> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolDoraBreakdown {
        if (!json.isJsonObject) throw JsonParseException("ProtocolDoraBreakdown must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("indicatorCount", "uraCount", "redCount", "totalHan", "indicatorTiles", "uraTiles", "redTiles", "indicatorHits", "uraHits"), "ProtocolDoraBreakdown")
        val indicatorCount = validateStrictInt(obj.get("indicatorCount") ?: throw JsonParseException("Missing indicatorCount in ProtocolDoraBreakdown"), "indicatorCount")
        val uraCount = validateStrictInt(obj.get("uraCount") ?: throw JsonParseException("Missing uraCount in ProtocolDoraBreakdown"), "uraCount")
        val redCount = validateStrictInt(obj.get("redCount") ?: throw JsonParseException("Missing redCount in ProtocolDoraBreakdown"), "redCount")
        val totalHan = validateStrictInt(obj.get("totalHan") ?: throw JsonParseException("Missing totalHan in ProtocolDoraBreakdown"), "totalHan")

        val indTilesElem = obj.get("indicatorTiles") ?: throw JsonParseException("Missing indicatorTiles in ProtocolDoraBreakdown")
        if (!indTilesElem.isJsonArray) throw JsonParseException("indicatorTiles must be an array in ProtocolDoraBreakdown")
        val indArr = indTilesElem.asJsonArray
        val indList = ArrayList<ProtocolTile>(indArr.size())
        for (elem in indArr) indList.add(context.deserialize(elem, ProtocolTile::class.java))
        val indicatorTiles = Collections.unmodifiableList(indList)

        val uraTilesElem = obj.get("uraTiles") ?: throw JsonParseException("Missing uraTiles in ProtocolDoraBreakdown")
        if (!uraTilesElem.isJsonArray) throw JsonParseException("uraTiles must be an array in ProtocolDoraBreakdown")
        val uraArr = uraTilesElem.asJsonArray
        val uraList = ArrayList<ProtocolTile>(uraArr.size())
        for (elem in uraArr) uraList.add(context.deserialize(elem, ProtocolTile::class.java))
        val uraTiles = Collections.unmodifiableList(uraList)

        val redTilesElem = obj.get("redTiles") ?: throw JsonParseException("Missing redTiles in ProtocolDoraBreakdown")
        if (!redTilesElem.isJsonArray) throw JsonParseException("redTiles must be an array in ProtocolDoraBreakdown")
        val redArr = redTilesElem.asJsonArray
        val redList = ArrayList<ProtocolTile>(redArr.size())
        for (elem in redArr) redList.add(context.deserialize(elem, ProtocolTile::class.java))
        val redTiles = Collections.unmodifiableList(redList)

        val indHitsElem = obj.get("indicatorHits") ?: throw JsonParseException("Missing indicatorHits in ProtocolDoraBreakdown")
        if (!indHitsElem.isJsonArray) throw JsonParseException("indicatorHits must be an array in ProtocolDoraBreakdown")
        val indHitsArr = indHitsElem.asJsonArray
        val indHitsList = ArrayList<ProtocolDoraIndicatorHit>(indHitsArr.size())
        for (elem in indHitsArr) indHitsList.add(context.deserialize(elem, ProtocolDoraIndicatorHit::class.java))
        val indicatorHits = Collections.unmodifiableList(indHitsList)

        val uraHitsElem = obj.get("uraHits") ?: throw JsonParseException("Missing uraHits in ProtocolDoraBreakdown")
        if (!uraHitsElem.isJsonArray) throw JsonParseException("uraHits must be an array in ProtocolDoraBreakdown")
        val uraHitsArr = uraHitsElem.asJsonArray
        val uraHitsList = ArrayList<ProtocolDoraIndicatorHit>(uraHitsArr.size())
        for (elem in uraHitsArr) uraHitsList.add(context.deserialize(elem, ProtocolDoraIndicatorHit::class.java))
        val uraHits = Collections.unmodifiableList(uraHitsList)

        return ProtocolDoraBreakdown(indicatorCount, uraCount, redCount, totalHan, indicatorTiles, uraTiles, redTiles, indicatorHits, uraHits)
    }
}

/**
 * ProtocolScoreBreakdown JsonDeserializer (确保 yakuLines 与 paymentLines 不可变，yakuLines 严格解析 exact Int)
 */
private class ProtocolScoreBreakdownAdapter : JsonDeserializer<ProtocolScoreBreakdown> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolScoreBreakdown {
        if (!json.isJsonObject) throw JsonParseException("ProtocolScoreBreakdown must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("yakuLines", "yakumanCount", "dora", "fu", "score", "paymentLines", "honbaAddition", "riichiAddition", "totalPoints", "uraEligible", "isDealer", "isTsumo"), "ProtocolScoreBreakdown")
        val yakuLinesElem = obj.get("yakuLines") ?: throw JsonParseException("Missing yakuLines in ProtocolScoreBreakdown")
        if (!yakuLinesElem.isJsonArray) throw JsonParseException("yakuLines must be an array in ProtocolScoreBreakdown")
        val arr = yakuLinesElem.asJsonArray
        val list = ArrayList<Pair<String, Int>>(arr.size())
        for (elem in arr) {
            if (!elem.isJsonObject) throw JsonParseException("yakuLine must be an object with first and second: $elem")
            val pObj = elem.asJsonObject
            checkUnknownFields(pObj, setOf("first", "second"), "yakuLine")
            val name = validateStrictString(pObj.get("first") ?: throw JsonParseException("Missing first in yakuLine"), "yakuLine.first")
            val han = validateStrictInt(pObj.get("second") ?: throw JsonParseException("Missing second in yakuLine"), "yakuLine.second")
            list.add(Pair(name, han))
        }
        val yakuLines = Collections.unmodifiableList(list)

        val yakumanCount = validateStrictInt(obj.get("yakumanCount") ?: throw JsonParseException("Missing yakumanCount in ProtocolScoreBreakdown"), "yakumanCount")
        val doraElem = obj.get("dora") ?: throw JsonParseException("Missing dora in ProtocolScoreBreakdown")
        val dora = context.deserialize<ProtocolDoraBreakdown>(doraElem, ProtocolDoraBreakdown::class.java)
        val fuElem = obj.get("fu") ?: throw JsonParseException("Missing fu in ProtocolScoreBreakdown")
        val fu = context.deserialize<ProtocolFuBreakdown>(fuElem, ProtocolFuBreakdown::class.java)
        val scoreElem = obj.get("score") ?: throw JsonParseException("Missing score in ProtocolScoreBreakdown")
        val score = context.deserialize<ProtocolScoreResult>(scoreElem, ProtocolScoreResult::class.java)

        val paymentLinesElem = obj.get("paymentLines") ?: throw JsonParseException("Missing paymentLines in ProtocolScoreBreakdown")
        val paymentLines = validateStrictStringList(paymentLinesElem, "paymentLines")

        val honbaAddition = validateStrictInt(obj.get("honbaAddition") ?: throw JsonParseException("Missing honbaAddition in ProtocolScoreBreakdown"), "honbaAddition")
        val riichiAddition = validateStrictInt(obj.get("riichiAddition") ?: throw JsonParseException("Missing riichiAddition in ProtocolScoreBreakdown"), "riichiAddition")
        val totalPoints = validateStrictInt(obj.get("totalPoints") ?: throw JsonParseException("Missing totalPoints in ProtocolScoreBreakdown"), "totalPoints")
        val uraEligible = validateStrictBoolean(obj.get("uraEligible") ?: throw JsonParseException("Missing uraEligible in ProtocolScoreBreakdown"), "uraEligible")
        val isDealer = validateStrictBoolean(obj.get("isDealer") ?: throw JsonParseException("Missing isDealer in ProtocolScoreBreakdown"), "isDealer")
        val isTsumo = validateStrictBoolean(obj.get("isTsumo") ?: throw JsonParseException("Missing isTsumo in ProtocolScoreBreakdown"), "isTsumo")
        return ProtocolScoreBreakdown(yakuLines, yakumanCount, dora, fu, score, paymentLines, honbaAddition, riichiAddition, totalPoints, uraEligible, isDealer, isTsumo)
    }
}

/**
 * ProtocolPublicRoundRecord JsonDeserializer (确保所有结算分数列表不可变且必须字段校验)
 */
private class ProtocolPublicRoundRecordAdapter : JsonDeserializer<ProtocolPublicRoundRecord> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicRoundRecord {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicRoundRecord must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roundId", "resultId", "finalScores", "deltas", "finalCheatPoints", "cheatDeltas"), "ProtocolPublicRoundRecord")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolPublicRoundRecord"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank in ProtocolPublicRoundRecord")
        val resultId = validateStrictString(obj.get("resultId") ?: throw JsonParseException("Missing resultId in ProtocolPublicRoundRecord"), "resultId")
        if (resultId.isBlank()) throw JsonParseException("resultId cannot be blank in ProtocolPublicRoundRecord")
        val finalScores = validateStrictIntList(obj.get("finalScores") ?: throw JsonParseException("Missing finalScores in ProtocolPublicRoundRecord"), "finalScores")
        val deltas = validateStrictIntList(obj.get("deltas") ?: throw JsonParseException("Missing deltas in ProtocolPublicRoundRecord"), "deltas")
        val finalCheatPoints = validateStrictIntList(obj.get("finalCheatPoints") ?: throw JsonParseException("Missing finalCheatPoints in ProtocolPublicRoundRecord"), "finalCheatPoints")
        val cheatDeltas = validateStrictIntList(obj.get("cheatDeltas") ?: throw JsonParseException("Missing cheatDeltas in ProtocolPublicRoundRecord"), "cheatDeltas")
        return ProtocolPublicRoundRecord(roundId, resultId, finalScores, deltas, finalCheatPoints, cheatDeltas)
    }
}

/**
 * ProtocolPublicSessionResult JsonDeserializer (确保所有名次和终局分数列表不可变且必须字段校验)
 */
private class ProtocolPublicSessionResultAdapter : JsonDeserializer<ProtocolPublicSessionResult> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicSessionResult {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicSessionResult must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("finalScores", "rankedSeats", "reason", "finalCheatPoints"), "ProtocolPublicSessionResult")
        val finalScores = validateStrictIntList(obj.get("finalScores") ?: throw JsonParseException("Missing finalScores in ProtocolPublicSessionResult"), "finalScores")
        val rankedSeats = validateStrictIntList(obj.get("rankedSeats") ?: throw JsonParseException("Missing rankedSeats in ProtocolPublicSessionResult"), "rankedSeats")
        val reason = validateStrictString(obj.get("reason") ?: throw JsonParseException("Missing reason in ProtocolPublicSessionResult"), "reason")
        if (reason.isBlank()) throw JsonParseException("reason cannot be blank in ProtocolPublicSessionResult")
        val finalCheatPoints: List<Int> = if (obj.has("finalCheatPoints") && !obj.get("finalCheatPoints").isJsonNull) {
            validateStrictIntList(obj.get("finalCheatPoints"), "finalCheatPoints")
        } else emptyList()
        return ProtocolPublicSessionResult(finalScores, rankedSeats, reason, finalCheatPoints)
    }
}

/**
 * ProtocolSessionView JsonDeserializer (确保 scores 与 roundRecords 不可变且必须字段校验)
 */
private class ProtocolSessionViewAdapter : JsonDeserializer<ProtocolSessionView> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSessionView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSessionView must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("lifecycle", "scores", "roundWind", "roundNumber", "dealerSeat", "honba", "roundRecords", "finalResult"), "ProtocolSessionView")
        val lifecycle = validateStrictString(obj.get("lifecycle") ?: throw JsonParseException("Missing lifecycle in ProtocolSessionView"), "lifecycle")
        if (lifecycle.isBlank()) throw JsonParseException("lifecycle cannot be blank in ProtocolSessionView")
        val scores = validateStrictIntList(obj.get("scores") ?: throw JsonParseException("Missing scores in ProtocolSessionView"), "scores")
        if (scores.size != 4) throw JsonParseException("scores must have size 4 in ProtocolSessionView: ${scores.size}")
        val roundWind = validateStrictInt(obj.get("roundWind") ?: throw JsonParseException("Missing roundWind in ProtocolSessionView"), "roundWind")
        if (roundWind !in 1..4) throw JsonParseException("roundWind must be in 1..4: $roundWind")
        val roundNumber = validateStrictInt(obj.get("roundNumber") ?: throw JsonParseException("Missing roundNumber in ProtocolSessionView"), "roundNumber")
        val dealerSeat = validateStrictInt(obj.get("dealerSeat") ?: throw JsonParseException("Missing dealerSeat in ProtocolSessionView"), "dealerSeat")
        if (dealerSeat !in 0..3) throw JsonParseException("dealerSeat must be in 0..3: $dealerSeat")
        val honba = validateStrictInt(obj.get("honba") ?: throw JsonParseException("Missing honba in ProtocolSessionView"), "honba")
        if (honba < 0) throw JsonParseException("honba must be >= 0: $honba")
        val roundRecords: List<ProtocolPublicRoundRecord> = if (obj.has("roundRecords") && !obj.get("roundRecords").isJsonNull) {
            val arr = obj.getAsJsonArray("roundRecords")
            val list = ArrayList<ProtocolPublicRoundRecord>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolPublicRoundRecord::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()
        val finalResult: ProtocolPublicSessionResult? = if (obj.has("finalResult") && !obj.get("finalResult").isJsonNull) {
            context.deserialize(obj.get("finalResult"), ProtocolPublicSessionResult::class.java)
        } else null
        return ProtocolSessionView(lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult)
    }
}

/**
 * ProtocolPlayerDecision JsonDeserializer (确保 options 不可变且必须字段校验)
 */
private class ProtocolPlayerDecisionAdapter : JsonDeserializer<ProtocolPlayerDecision> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPlayerDecision {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPlayerDecision must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roundId", "decisionId", "controlEpoch", "options"), "ProtocolPlayerDecision")
        val roundId = obj.get("roundId")?.takeIf { !it.isJsonNull }?.let {
            val rid = validateStrictString(it, "roundId")
            if (rid.isBlank()) throw JsonParseException("roundId cannot be blank when present in ProtocolPlayerDecision")
            rid
        }
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolPlayerDecision"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank in ProtocolPlayerDecision")
        val epochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolPlayerDecision")
        val controlEpoch = validateStrictLong(epochElem, "ProtocolPlayerDecision.controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be >= 0: $controlEpoch")
        val options: List<ProtocolActionOption> = if (obj.has("options") && !obj.get("options").isJsonNull) {
            val arr = obj.getAsJsonArray("options")
            val list = ArrayList<ProtocolActionOption>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolActionOption::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()
        return ProtocolPlayerDecision(roundId, decisionId, controlEpoch, options)
    }
}

/**
 * ProtocolClock JsonDeserializer
 */
private class ProtocolClockAdapter : JsonDeserializer<ProtocolClock> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolClock {
        if (!json.isJsonObject) throw JsonParseException("ProtocolClock must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("seat", "opensAt", "deadline", "reserve", "serverNow"), "ProtocolClock")
        val seatElem = obj.get("seat") ?: throw JsonParseException("Missing seat in ProtocolClock")
        val seat = validateStrictInt(seatElem, "ProtocolClock.seat")
        if (seat !in 0..3) throw JsonParseException("seat must be in 0..3: $seat")
        val opensAtElem = obj.get("opensAt") ?: throw JsonParseException("Missing opensAt in ProtocolClock")
        val opensAt = validateStrictLong(opensAtElem, "ProtocolClock.opensAt")
        val deadlineElem = obj.get("deadline") ?: throw JsonParseException("Missing deadline in ProtocolClock")
        val deadline = validateStrictLong(deadlineElem, "ProtocolClock.deadline")
        if (deadline < opensAt) throw JsonParseException("deadline must be >= opensAt: deadline=$deadline, opensAt=$opensAt")
        val reserveElem = obj.get("reserve") ?: throw JsonParseException("Missing reserve in ProtocolClock")
        val reserve = validateStrictLong(reserveElem, "ProtocolClock.reserve")
        if (reserve < 0L) throw JsonParseException("reserve must be >= 0: $reserve")
        val serverNowElem = obj.get("serverNow") ?: throw JsonParseException("Missing serverNow in ProtocolClock")
        val serverNow = validateStrictLong(serverNowElem, "ProtocolClock.serverNow")
        return ProtocolClock(seat, opensAt, deadline, reserve, serverNow)
    }
}

/**
 * ProtocolSeatControl JsonDeserializer
 */
private class ProtocolSeatControlAdapter : JsonDeserializer<ProtocolSeatControl> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSeatControl {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSeatControl must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("seat", "connected", "role", "controlEpoch"), "ProtocolSeatControl")
        val seatElem = obj.get("seat") ?: throw JsonParseException("Missing seat in ProtocolSeatControl")
        val seat = validateStrictInt(seatElem, "ProtocolSeatControl.seat")
        if (seat !in 0..3) throw JsonParseException("seat must be in 0..3: $seat")
        val connElem = obj.get("connected") ?: throw JsonParseException("Missing connected in ProtocolSeatControl")
        val connected = validateStrictBoolean(connElem, "ProtocolSeatControl.connected")
        val roleElem = obj.get("role") ?: throw JsonParseException("Missing role in ProtocolSeatControl")
        val role = validateStrictString(roleElem, "ProtocolSeatControl.role")
        if (role !in ProtocolSeatControl.VALID_ROLES) {
            throw JsonParseException("role must be one of ${ProtocolSeatControl.VALID_ROLES}: $role")
        }
        val epochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolSeatControl")
        val controlEpoch = validateStrictLong(epochElem, "ProtocolSeatControl.controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be >= 0: $controlEpoch")
        return ProtocolSeatControl(seat, connected, role, controlEpoch)
    }
}

/**
 * ProtocolScoreResult JsonDeserializer
 */
private class ProtocolScoreResultAdapter : JsonDeserializer<ProtocolScoreResult> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolScoreResult {
        if (!json.isJsonObject) throw JsonParseException("ProtocolScoreResult must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier", "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact"),
            "ProtocolScoreResult"
        )
        val fu = validateStrictInt(obj.get("fu") ?: throw JsonParseException("Missing fu in ProtocolScoreResult"), "ProtocolScoreResult.fu")
        val han = validateStrictInt(obj.get("han") ?: throw JsonParseException("Missing han in ProtocolScoreResult"), "ProtocolScoreResult.han")
        val basePoints = validateStrictInt(obj.get("basePoints") ?: throw JsonParseException("Missing basePoints in ProtocolScoreResult"), "ProtocolScoreResult.basePoints")
        val title = validateStrictString(obj.get("title") ?: throw JsonParseException("Missing title in ProtocolScoreResult"), "ProtocolScoreResult.title")
        val isYakuman = validateStrictBoolean(obj.get("isYakuman") ?: throw JsonParseException("Missing isYakuman in ProtocolScoreResult"), "ProtocolScoreResult.isYakuman")
        val yakumanMultiplier = validateStrictInt(obj.get("yakumanMultiplier") ?: throw JsonParseException("Missing yakumanMultiplier in ProtocolScoreResult"), "ProtocolScoreResult.yakumanMultiplier")
        val rawBasePoints = if (obj.has("rawBasePoints") && !obj.get("rawBasePoints").isJsonNull) {
            validateStrictInt(obj.get("rawBasePoints"), "ProtocolScoreResult.rawBasePoints")
        } else basePoints
        val capReason = obj.get("capReason")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "ProtocolScoreResult.capReason") }
        val formulaText = if (obj.has("formulaText") && !obj.get("formulaText").isJsonNull) {
            validateStrictString(obj.get("formulaText"), "ProtocolScoreResult.formulaText")
        } else ""
        val rawBasePointsExact = if (obj.has("rawBasePointsExact") && !obj.get("rawBasePointsExact").isJsonNull) {
            validateStrictString(obj.get("rawBasePointsExact"), "ProtocolScoreResult.rawBasePointsExact")
        } else rawBasePoints.toString()
        return ProtocolScoreResult(fu, han, basePoints, title, isYakuman, yakumanMultiplier, rawBasePoints, capReason, formulaText, rawBasePointsExact)
    }
}

/**
 * ProtocolFuLine JsonDeserializer
 */
private class ProtocolFuLineAdapter : JsonDeserializer<ProtocolFuLine> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolFuLine {
        if (!json.isJsonObject) throw JsonParseException("ProtocolFuLine must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("description", "fu"), "ProtocolFuLine")
        val description = validateStrictString(obj.get("description") ?: throw JsonParseException("Missing description in ProtocolFuLine"), "ProtocolFuLine.description")
        val fu = validateStrictInt(obj.get("fu") ?: throw JsonParseException("Missing fu in ProtocolFuLine"), "ProtocolFuLine.fu")
        return ProtocolFuLine(description, fu)
    }
}

/**
 * ProtocolFuritenStatus JsonDeserializer
 */
private class ProtocolFuritenStatusAdapter : JsonDeserializer<ProtocolFuritenStatus> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolFuritenStatus {
        if (!json.isJsonObject) throw JsonParseException("ProtocolFuritenStatus must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("discard", "temporary", "riichi"), "ProtocolFuritenStatus")
        val discard = if (obj.has("discard") && !obj.get("discard").isJsonNull) {
            validateStrictBoolean(obj.get("discard"), "ProtocolFuritenStatus.discard")
        } else false
        val temporary = if (obj.has("temporary") && !obj.get("temporary").isJsonNull) {
            validateStrictBoolean(obj.get("temporary"), "ProtocolFuritenStatus.temporary")
        } else false
        val riichi = if (obj.has("riichi") && !obj.get("riichi").isJsonNull) {
            validateStrictBoolean(obj.get("riichi"), "ProtocolFuritenStatus.riichi")
        } else false
        return ProtocolFuritenStatus(discard, temporary, riichi)
    }
}

/**
 * ProtocolActionReceipt JsonDeserializer
 */
private class ProtocolActionReceiptAdapter : JsonDeserializer<ProtocolActionReceipt> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolActionReceipt {
        if (!json.isJsonObject) throw JsonParseException("ProtocolActionReceipt must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("requestId", "status", "reason", "cursor"), "ProtocolActionReceipt")
        val reqId = validateStrictString(obj.get("requestId") ?: throw JsonParseException("Missing requestId in ProtocolActionReceipt"), "ProtocolActionReceipt.requestId")
        if (reqId.isBlank()) throw JsonParseException("requestId cannot be blank in ProtocolActionReceipt")
        val statusStr = validateStrictString(obj.get("status") ?: throw JsonParseException("Missing status in ProtocolActionReceipt"), "ProtocolActionReceipt.status")
        val status = try {
            ProtocolReceiptStatus.valueOf(statusStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid status in ProtocolActionReceipt: $statusStr", e)
        }
        val reason = obj.get("reason")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "ProtocolActionReceipt.reason") }
        val cursor = if (obj.has("cursor") && !obj.get("cursor").isJsonNull) {
            val c = validateStrictLong(obj.get("cursor"), "ProtocolActionReceipt.cursor")
            if (c < 0L) throw JsonParseException("cursor must be >= 0: $c")
            c
        } else null
        return ProtocolActionReceipt(reqId, status, reason, cursor)
    }
}

/**
 * ProtocolAccessCapabilities JsonDeserializer
 */
private class ProtocolAccessCapabilitiesAdapter : JsonDeserializer<ProtocolAccessCapabilities> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolAccessCapabilities {
        if (!json.isJsonObject) throw JsonParseException("ProtocolAccessCapabilities must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("protocolVersion", "supportedPlayerCounts", "remoteTransport", "persistence", "resumption", "tileMarkSupported", "sleeveBloomSupported", "beamSwapSupported"), "ProtocolAccessCapabilities")
        val pv = if (obj.has("protocolVersion") && !obj.get("protocolVersion").isJsonNull) {
            val v = validateStrictInt(obj.get("protocolVersion"), "ProtocolAccessCapabilities.protocolVersion")
            if (v < 1) throw JsonParseException("protocolVersion must be >= 1: $v")
            v
        } else 4
        val counts: Set<Int> = if (obj.has("supportedPlayerCounts") && !obj.get("supportedPlayerCounts").isJsonNull) {
            val list = validateStrictIntList(obj.get("supportedPlayerCounts"), "ProtocolAccessCapabilities.supportedPlayerCounts")
            if (list.isEmpty()) throw JsonParseException("supportedPlayerCounts cannot be empty")
            Collections.unmodifiableSet(LinkedHashSet(list))
        } else setOf(4)
        val remote = if (obj.has("remoteTransport") && !obj.get("remoteTransport").isJsonNull) {
            validateStrictBoolean(obj.get("remoteTransport"), "ProtocolAccessCapabilities.remoteTransport")
        } else false
        val persist = if (obj.has("persistence") && !obj.get("persistence").isJsonNull) {
            validateStrictBoolean(obj.get("persistence"), "ProtocolAccessCapabilities.persistence")
        } else false
        val resume = if (obj.has("resumption") && !obj.get("resumption").isJsonNull) {
            validateStrictBoolean(obj.get("resumption"), "ProtocolAccessCapabilities.resumption")
        } else true
        val tileMarkSupported = if (obj.has("tileMarkSupported") && !obj.get("tileMarkSupported").isJsonNull) {
            validateStrictBoolean(obj.get("tileMarkSupported"), "ProtocolAccessCapabilities.tileMarkSupported")
        } else false
        val sleeveBloomSupported = if (obj.has("sleeveBloomSupported") && !obj.get("sleeveBloomSupported").isJsonNull) {
            validateStrictBoolean(obj.get("sleeveBloomSupported"), "ProtocolAccessCapabilities.sleeveBloomSupported")
        } else false
        val beamSwapSupported = if (obj.has("beamSwapSupported") && !obj.get("beamSwapSupported").isJsonNull) {
            validateStrictBoolean(obj.get("beamSwapSupported"), "ProtocolAccessCapabilities.beamSwapSupported")
        } else false
        return ProtocolAccessCapabilities(pv, counts, remote, persist, resume, tileMarkSupported, sleeveBloomSupported, beamSwapSupported)
    }
}

/**
 * ProtocolActionOption JsonDeserializer
 */
private class ProtocolActionOptionAdapter : JsonDeserializer<ProtocolActionOption> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolActionOption {
        if (!json.isJsonObject) throw JsonParseException("ProtocolActionOption must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("optionId", "action"), "ProtocolActionOption")
        val optionId = validateStrictString(obj.get("optionId") ?: throw JsonParseException("Missing optionId in ProtocolActionOption"), "ProtocolActionOption.optionId")
        if (optionId.isBlank()) throw JsonParseException("optionId cannot be blank in ProtocolActionOption")
        val actionElem = obj.get("action") ?: throw JsonParseException("Missing action in ProtocolActionOption")
        val action = context.deserialize<ProtocolLegalAction>(actionElem, ProtocolLegalAction::class.java)
        return ProtocolActionOption(optionId, action)
    }
}

/**
 * ProtocolShufflePreparation JsonDeserializer
 */
private class ProtocolShufflePreparationAdapter : JsonDeserializer<ProtocolShufflePreparation> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolShufflePreparation {
        if (!json.isJsonObject) throw JsonParseException("ProtocolShufflePreparation must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("roundId", "status", "opensAtMillis", "deadlineMillis", "serverNowMillis", "confirmed", "selection", "reservedPoints", "availablePoints"),
            "ProtocolShufflePreparation"
        )
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolShufflePreparation"), "ProtocolShufflePreparation.roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank in ProtocolShufflePreparation")
        val statusStr = validateStrictString(obj.get("status") ?: throw JsonParseException("Missing status in ProtocolShufflePreparation"), "ProtocolShufflePreparation.status")
        val status = try {
            ProtocolShufflePreparationStatus.valueOf(statusStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid status in ProtocolShufflePreparation: $statusStr", e)
        }
        val opensAt = validateStrictLong(obj.get("opensAtMillis") ?: throw JsonParseException("Missing opensAtMillis in ProtocolShufflePreparation"), "ProtocolShufflePreparation.opensAtMillis")
        val deadline = validateStrictLong(obj.get("deadlineMillis") ?: throw JsonParseException("Missing deadlineMillis in ProtocolShufflePreparation"), "ProtocolShufflePreparation.deadlineMillis")
        if (deadline < opensAt) throw JsonParseException("deadline must be >= opensAt in ProtocolShufflePreparation")
        val serverNow = validateStrictLong(obj.get("serverNowMillis") ?: throw JsonParseException("Missing serverNowMillis in ProtocolShufflePreparation"), "ProtocolShufflePreparation.serverNowMillis")
        val confirmed = validateStrictBoolean(obj.get("confirmed") ?: throw JsonParseException("Missing confirmed in ProtocolShufflePreparation"), "ProtocolShufflePreparation.confirmed")
        val selection = if (obj.has("selection") && !obj.get("selection").isJsonNull) {
            context.deserialize<ProtocolShuffleSelection>(obj.get("selection"), ProtocolShuffleSelection::class.java)
        } else null
        val reserved = validateStrictInt(obj.get("reservedPoints") ?: throw JsonParseException("Missing reservedPoints in ProtocolShufflePreparation"), "ProtocolShufflePreparation.reservedPoints")
        val available = validateStrictInt(obj.get("availablePoints") ?: throw JsonParseException("Missing availablePoints in ProtocolShufflePreparation"), "ProtocolShufflePreparation.availablePoints")
        return ProtocolShufflePreparation(roundId, status, opensAt, deadline, serverNow, confirmed, selection, reserved, available)
    }
}

/**
 * RoomSummary JsonDeserializer
 */
private class RoomSummaryAdapter : JsonDeserializer<RoomSummary> {
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
private class RoomBindingAdapter : JsonDeserializer<RoomBinding> {
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
private class CreateRoomRequestAdapter : JsonDeserializer<CreateRoomRequest> {
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
private class JoinRoomRequestAdapter : JsonDeserializer<JoinRoomRequest> {
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
private class ResumeRequestAdapter : JsonDeserializer<ResumeRequest> {
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
 * ApiRequest JsonDeserializer
 */
private class ApiRequestAdapter : JsonDeserializer<ApiRequest> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ApiRequest {
        if (!json.isJsonObject) throw JsonParseException("ApiRequest must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("api", "requestId", "roomId", "token", "playerName", "preferSeat", "config", "choice", "shufflePlan", "cursor", "roundId", "protocolVersion", "ready", "roomConfig", "spectatorId", "spectatorName", "publicCheckpoint", "tileMark", "sleeveBloom", "sleeveBloomChoice", "beamSwap", "beamSwapChoice"),
            "ApiRequest"
        )
        val api = validateStrictString(obj.get("api") ?: throw JsonParseException("missing or blank api"), "api")
        if (api.isBlank()) throw JsonParseException("missing or blank api")
        val reqIdElem = obj.get("requestId") ?: throw JsonParseException("missing or blank requestId")
        val requestId = validateStrictString(reqIdElem, "requestId")
        if (requestId.isBlank()) throw JsonParseException("missing or blank requestId")

        val isSpectatorApi = api in setOf(
            ApiTags.JOIN_SPECTATOR,
            ApiTags.RESUME_SPECTATOR,
            ApiTags.PUBLIC_SNAPSHOT,
            ApiTags.RESUME_PUBLIC,
            ApiTags.QUERY_PUBLIC_ROUND_RESULT
        )
        if (isSpectatorApi) {
            if (obj.has("preferSeat")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'preferSeat'")
            }
            if (obj.has("choice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'choice'")
            }
            if (obj.has("shufflePlan")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'shufflePlan'")
            }
            if (obj.has("tileMark")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'tileMark'")
            }
            if (obj.has("sleeveBloom")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloom'")
            }
            if (obj.has("sleeveBloomChoice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloomChoice'")
            }
            if (obj.has("beamSwap")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'beamSwap'")
            }
            if (obj.has("beamSwapChoice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'beamSwapChoice'")
            }
            when (api) {
                ApiTags.JOIN_SPECTATOR -> {
                    val rElem = obj.get("roomId") ?: throw JsonParseException("roomId required for JOIN_SPECTATOR")
                    val r = validateStrictString(rElem, "roomId")
                    if (r.isBlank()) throw JsonParseException("roomId cannot be blank for JOIN_SPECTATOR")
                    if (obj.has("spectatorName") && !obj.get("spectatorName").isJsonNull) {
                        val sn = validateStrictString(obj.get("spectatorName"), "spectatorName")
                        if (sn.isBlank()) throw JsonParseException("spectatorName cannot be blank")
                    }
                }
                ApiTags.RESUME_SPECTATOR -> {
                    if (obj.has("cursor")) {
                        throw JsonParseException("cursor cannot be present in RESUME_SPECTATOR; use publicCheckpoint")
                    }
                    val rElem = obj.get("roomId") ?: throw JsonParseException("roomId required for RESUME_SPECTATOR")
                    val r = validateStrictString(rElem, "roomId")
                    if (r.isBlank()) throw JsonParseException("roomId cannot be blank for RESUME_SPECTATOR")

                    val sElem = obj.get("spectatorId") ?: throw JsonParseException("spectatorId required for RESUME_SPECTATOR")
                    val s = validateStrictString(sElem, "spectatorId")
                    if (s.isBlank()) throw JsonParseException("spectatorId cannot be blank for RESUME_SPECTATOR")

                    val tElem = obj.get("token") ?: throw JsonParseException("token required for RESUME_SPECTATOR")
                    val t = validateStrictString(tElem, "token")
                    if (t.isBlank()) throw JsonParseException("token cannot be blank for RESUME_SPECTATOR")

                    if (obj.has("publicCheckpoint") && !obj.get("publicCheckpoint").isJsonNull) {
                        val cpElem = obj.get("publicCheckpoint")
                        if (!cpElem.isJsonObject) throw JsonParseException("publicCheckpoint must be a JSON object")
                    }
                }
                ApiTags.RESUME_PUBLIC -> {
                    if (obj.has("cursor")) {
                        throw JsonParseException("cursor cannot be present in RESUME_PUBLIC; use publicCheckpoint")
                    }
                    val cpElem = obj.get("publicCheckpoint") ?: throw JsonParseException("publicCheckpoint required for RESUME_PUBLIC")
                    if (!cpElem.isJsonObject) throw JsonParseException("publicCheckpoint must be a JSON object")
                }
                ApiTags.QUERY_PUBLIC_ROUND_RESULT -> {
                    val rElem = obj.get("roundId") ?: throw JsonParseException("roundId required for QUERY_PUBLIC_ROUND_RESULT")
                    val r = validateStrictString(rElem, "roundId")
                    if (r.isBlank()) throw JsonParseException("roundId cannot be blank for QUERY_PUBLIC_ROUND_RESULT")
                }
            }
        }

        val roomId = obj.get("roomId")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "roomId") }
        val token = obj.get("token")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "token") }
        val playerName = obj.get("playerName")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "playerName") }
        val preferSeat = if (obj.has("preferSeat") && !obj.get("preferSeat").isJsonNull) {
            val seat = validateStrictInt(obj.get("preferSeat"), "preferSeat")
            if (seat !in 0..3) throw JsonParseException("preferSeat out of range: $seat")
            seat
        } else null
        val config = if (obj.has("config") && !obj.get("config").isJsonNull) {
            context.deserialize<ProtocolMatchConfig>(obj.get("config"), ProtocolMatchConfig::class.java)
        } else null
        val choice = if (obj.has("choice") && !obj.get("choice").isJsonNull) {
            context.deserialize<ProtocolActionChoice>(obj.get("choice"), ProtocolActionChoice::class.java)
        } else null
        val shufflePlan = if (obj.has("shufflePlan") && !obj.get("shufflePlan").isJsonNull) {
            context.deserialize<ProtocolShufflePlan>(obj.get("shufflePlan"), ProtocolShufflePlan::class.java)
        } else null
        val cursor = if (obj.has("cursor") && !obj.get("cursor").isJsonNull) {
            val c = validateStrictLong(obj.get("cursor"), "cursor")
            if (c < 0L) throw JsonParseException("cursor must be non-negative: $c")
            c
        } else null
        val roundId = obj.get("roundId")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "roundId") }
        val protocolVersion = if (obj.has("protocolVersion") && !obj.get("protocolVersion").isJsonNull) {
            val pv = validateStrictInt(obj.get("protocolVersion"), "protocolVersion")
            if (pv < 1) throw JsonParseException("protocolVersion must be >= 1: $pv")
            pv
        } else ApiProtocol.PROTOCOL_VERSION
        val ready = if (obj.has("ready") && !obj.get("ready").isJsonNull) {
            validateStrictBoolean(obj.get("ready"), "ready")
        } else null
        val roomConfig = if (obj.has("roomConfig") && !obj.get("roomConfig").isJsonNull) {
            context.deserialize<ProtocolRoomConfig>(obj.get("roomConfig"), ProtocolRoomConfig::class.java)
        } else null
        val spectatorId = obj.get("spectatorId")?.takeIf { !it.isJsonNull }?.let {
            val sId = validateStrictString(it, "spectatorId")
            if (sId.isBlank()) throw JsonParseException("spectatorId cannot be blank in ApiRequest")
            sId
        }
        val spectatorName = obj.get("spectatorName")?.takeIf { !it.isJsonNull }?.let {
            val sName = validateStrictString(it, "spectatorName")
            if (sName.isBlank()) throw JsonParseException("spectatorName cannot be blank in ApiRequest")
            sName
        }
        val publicCheckpoint = if (obj.has("publicCheckpoint") && !obj.get("publicCheckpoint").isJsonNull) {
            context.deserialize<PublicCheckpoint>(obj.get("publicCheckpoint"), PublicCheckpoint::class.java)
        } else null
        val tileMark = if (obj.has("tileMark") && !obj.get("tileMark").isJsonNull) {
            context.deserialize<ProtocolTileMarkRequest>(obj.get("tileMark"), ProtocolTileMarkRequest::class.java)
        } else null
        val sleeveBloom = if (obj.has("sleeveBloom") && !obj.get("sleeveBloom").isJsonNull) {
            context.deserialize<ProtocolSleeveBloomRequest>(obj.get("sleeveBloom"), ProtocolSleeveBloomRequest::class.java)
        } else null
        val sleeveBloomChoice = if (obj.has("sleeveBloomChoice") && !obj.get("sleeveBloomChoice").isJsonNull) {
            context.deserialize<ProtocolSleeveBloomChoiceRequest>(obj.get("sleeveBloomChoice"), ProtocolSleeveBloomChoiceRequest::class.java)
        } else null
        val beamSwap = if (obj.has("beamSwap") && !obj.get("beamSwap").isJsonNull) {
            context.deserialize<ProtocolBeamSwapRequest>(obj.get("beamSwap"), ProtocolBeamSwapRequest::class.java)
        } else null
        val beamSwapChoice = if (obj.has("beamSwapChoice") && !obj.get("beamSwapChoice").isJsonNull) {
            context.deserialize<ProtocolBeamSwapChoiceRequest>(obj.get("beamSwapChoice"), ProtocolBeamSwapChoiceRequest::class.java)
        } else null

        if (api == ApiTags.SUBMIT_TILE_MARK) {
            if (tileMark == null) {
                throw JsonParseException("Missing tileMark payload in SUBMIT_TILE_MARK request")
            }
            if (tileMark.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and tileMark (${tileMark.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_TILE_MARK cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_TILE_MARK cannot contain shufflePlan payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_TILE_MARK cannot contain sleeveBloom payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_TILE_MARK cannot contain sleeveBloomChoice payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_TILE_MARK cannot contain beamSwap payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_TILE_MARK cannot contain beamSwapChoice payload")
            }
        } else {
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("tileMark payload is only allowed for SUBMIT_TILE_MARK")
            }
        }

        if (api == ApiTags.SUBMIT_SLEEVE_BLOOM) {
            if (sleeveBloom == null) {
                throw JsonParseException("Missing sleeveBloom payload in SUBMIT_SLEEVE_BLOOM request")
            }
            if (sleeveBloom.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and sleeveBloom (${sleeveBloom.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM cannot contain shufflePlan payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM cannot contain tileMark payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM cannot contain sleeveBloomChoice payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM cannot contain beamSwap payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM cannot contain beamSwapChoice payload")
            }
        } else {
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("sleeveBloom payload is only allowed for SUBMIT_SLEEVE_BLOOM")
            }
        }

        if (api == ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE) {
            if (sleeveBloomChoice == null) {
                throw JsonParseException("Missing sleeveBloomChoice payload in SUBMIT_SLEEVE_BLOOM_CHOICE request")
            }
            if (sleeveBloomChoice.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and sleeveBloomChoice (${sleeveBloomChoice.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_CHOICE cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_CHOICE cannot contain shufflePlan payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_CHOICE cannot contain tileMark payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_CHOICE cannot contain sleeveBloom payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_CHOICE cannot contain beamSwap payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_CHOICE cannot contain beamSwapChoice payload")
            }
        } else {
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("sleeveBloomChoice payload is only allowed for SUBMIT_SLEEVE_BLOOM_CHOICE")
            }
        }

        if (api == ApiTags.SUBMIT_BEAM_SWAP) {
            if (beamSwap == null) {
                throw JsonParseException("Missing beamSwap payload in SUBMIT_BEAM_SWAP request")
            }
            if (beamSwap.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and beamSwap (${beamSwap.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP cannot contain shufflePlan payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP cannot contain tileMark payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP cannot contain sleeveBloom payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP cannot contain sleeveBloomChoice payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP cannot contain beamSwapChoice payload")
            }
        } else {
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("beamSwap payload is only allowed for SUBMIT_BEAM_SWAP")
            }
        }

        if (api == ApiTags.SUBMIT_BEAM_SWAP_CHOICE) {
            if (beamSwapChoice == null) {
                throw JsonParseException("Missing beamSwapChoice payload in SUBMIT_BEAM_SWAP_CHOICE request")
            }
            if (beamSwapChoice.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and beamSwapChoice (${beamSwapChoice.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP_CHOICE cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP_CHOICE cannot contain shufflePlan payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP_CHOICE cannot contain tileMark payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP_CHOICE cannot contain sleeveBloom payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP_CHOICE cannot contain sleeveBloomChoice payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_BEAM_SWAP_CHOICE cannot contain beamSwap payload")
            }
        } else {
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("beamSwapChoice payload is only allowed for SUBMIT_BEAM_SWAP_CHOICE")
            }
        }

        return ApiRequest(api, requestId, roomId, token, playerName, preferSeat, config, choice, shufflePlan, cursor, roundId, protocolVersion, ready, roomConfig, spectatorId, spectatorName, publicCheckpoint, tileMark, sleeveBloom, sleeveBloomChoice, beamSwap, beamSwapChoice)
    }
}

/**
 * ProtocolPlayerView JsonDeserializer (确保嵌套集合全部不可变，严禁无默认值构造参数被隐式填默认值)
 */
private class ProtocolPlayerViewAdapter : JsonDeserializer<ProtocolPlayerView> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPlayerView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPlayerView must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "viewerSeat", "phase", "roundWind", "roundNumber", "honbaSticks", "riichiSticks",
                "dealerSeat", "currentTurnPlayer", "wallRemaining", "doraIndicators", "scores",
                "riichiStatus", "discards", "melds", "handTileCounts", "myClosedTiles",
                "myDrawnTile", "legalActions", "lastDiscard", "lastDiscardPlayer",
                "mySleeveTiles", "myFuriten", "myDiscardHistory"
            ),
            "ProtocolPlayerView"
        )
        val viewerSeatElem = obj.get("viewerSeat") ?: throw JsonParseException("Missing viewerSeat in ProtocolPlayerView")
        val viewerSeat = validateStrictInt(viewerSeatElem, "viewerSeat")
        if (viewerSeat !in 0..3) throw JsonParseException("viewerSeat must be 0..3: $viewerSeat")

        val phaseElem = obj.get("phase") ?: throw JsonParseException("Missing phase in ProtocolPlayerView")
        val phaseStr = validateStrictString(phaseElem, "phase")
        val phase = try {
            ProtocolGamePhase.valueOf(phaseStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid phase in ProtocolPlayerView: $phaseStr", e)
        }

        val roundWindElem = obj.get("roundWind") ?: throw JsonParseException("Missing roundWind in ProtocolPlayerView")
        val roundWind = validateStrictInt(roundWindElem, "roundWind")

        val roundNumberElem = obj.get("roundNumber") ?: throw JsonParseException("Missing roundNumber in ProtocolPlayerView")
        val roundNumber = validateStrictInt(roundNumberElem, "roundNumber")

        val honbaSticksElem = obj.get("honbaSticks") ?: throw JsonParseException("Missing honbaSticks in ProtocolPlayerView")
        val honbaSticks = validateStrictInt(honbaSticksElem, "honbaSticks")

        val riichiSticksElem = obj.get("riichiSticks") ?: throw JsonParseException("Missing riichiSticks in ProtocolPlayerView")
        val riichiSticks = validateStrictInt(riichiSticksElem, "riichiSticks")

        val dealerSeatElem = obj.get("dealerSeat") ?: throw JsonParseException("Missing dealerSeat in ProtocolPlayerView")
        val dealerSeat = validateStrictInt(dealerSeatElem, "dealerSeat")
        if (dealerSeat !in 0..3) throw JsonParseException("dealerSeat must be 0..3: $dealerSeat")

        val currentTurnPlayerElem = obj.get("currentTurnPlayer") ?: throw JsonParseException("Missing currentTurnPlayer in ProtocolPlayerView")
        val currentTurnPlayer = validateStrictInt(currentTurnPlayerElem, "currentTurnPlayer")
        if (currentTurnPlayer !in 0..3) throw JsonParseException("currentTurnPlayer must be 0..3: $currentTurnPlayer")

        val wallRemainingElem = obj.get("wallRemaining") ?: throw JsonParseException("Missing wallRemaining in ProtocolPlayerView")
        val wallRemaining = validateStrictInt(wallRemainingElem, "wallRemaining")

        val doraIndicatorsElem = obj.get("doraIndicators") ?: throw JsonParseException("Missing doraIndicators in ProtocolPlayerView")
        if (!doraIndicatorsElem.isJsonArray) throw JsonParseException("doraIndicators must be an array")
        val dArr = doraIndicatorsElem.asJsonArray
        val dList = ArrayList<ProtocolTile>(dArr.size())
        for (elem in dArr) dList.add(context.deserialize(elem, ProtocolTile::class.java))
        val doraIndicators = Collections.unmodifiableList(dList)

        val scoresElem = obj.get("scores") ?: throw JsonParseException("Missing scores in ProtocolPlayerView")
        val scores = validateStrictIntList(scoresElem, "scores")
        if (scores.size != 4) throw JsonParseException("scores must have size 4")

        val riichiStatusElem = obj.get("riichiStatus") ?: throw JsonParseException("Missing riichiStatus in ProtocolPlayerView")
        val riichiStatus = validateStrictBooleanList(riichiStatusElem, "riichiStatus")
        if (riichiStatus.size != 4) throw JsonParseException("riichiStatus must have size 4")

        val discardsElem = obj.get("discards") ?: throw JsonParseException("Missing discards in ProtocolPlayerView")
        if (!discardsElem.isJsonArray) throw JsonParseException("discards must be an array")
        val discardsArr = discardsElem.asJsonArray
        if (discardsArr.size() != 4) throw JsonParseException("discards must have size 4: ${discardsArr.size()}")
        val discardsList = ArrayList<List<ProtocolTile>>(4)
        for (seat in 0..3) {
            val seatElem = discardsArr.get(seat)
            if (!seatElem.isJsonArray) throw JsonParseException("discards[$seat] must be an array")
            val arr = seatElem.asJsonArray
            val tList = ArrayList<ProtocolTile>(arr.size())
            for (elem in arr) tList.add(context.deserialize(elem, ProtocolTile::class.java))
            discardsList.add(Collections.unmodifiableList(tList))
        }
        val discards = Collections.unmodifiableList(discardsList)

        val meldsElem = obj.get("melds") ?: throw JsonParseException("Missing melds in ProtocolPlayerView")
        if (!meldsElem.isJsonArray) throw JsonParseException("melds must be an array")
        val meldsArr = meldsElem.asJsonArray
        if (meldsArr.size() != 4) throw JsonParseException("melds must have size 4: ${meldsArr.size()}")
        val meldsList = ArrayList<List<ProtocolMeld>>(4)
        for (seat in 0..3) {
            val seatElem = meldsArr.get(seat)
            if (!seatElem.isJsonArray) throw JsonParseException("melds[$seat] must be an array")
            val arr = seatElem.asJsonArray
            val mList = ArrayList<ProtocolMeld>(arr.size())
            for (elem in arr) mList.add(context.deserialize(elem, ProtocolMeld::class.java))
            meldsList.add(Collections.unmodifiableList(mList))
        }
        val melds = Collections.unmodifiableList(meldsList)

        val handTileCountsElem = obj.get("handTileCounts") ?: throw JsonParseException("Missing handTileCounts in ProtocolPlayerView")
        val handTileCounts = validateStrictIntList(handTileCountsElem, "handTileCounts")
        if (handTileCounts.size != 4) throw JsonParseException("handTileCounts must have size 4")

        val myClosedTilesElem = obj.get("myClosedTiles") ?: throw JsonParseException("Missing myClosedTiles in ProtocolPlayerView")
        if (!myClosedTilesElem.isJsonArray) throw JsonParseException("myClosedTiles must be an array")
        val cArr = myClosedTilesElem.asJsonArray
        val cList = ArrayList<ProtocolTile>(cArr.size())
        for (elem in cArr) cList.add(context.deserialize(elem, ProtocolTile::class.java))
        val myClosedTiles = Collections.unmodifiableList(cList)

        val myDrawnTile: ProtocolTile? = if (obj.has("myDrawnTile") && !obj.get("myDrawnTile").isJsonNull) {
            context.deserialize(obj.get("myDrawnTile"), ProtocolTile::class.java)
        } else null

        val legalActionsElem = obj.get("legalActions") ?: throw JsonParseException("Missing legalActions in ProtocolPlayerView")
        if (!legalActionsElem.isJsonArray) throw JsonParseException("legalActions must be an array")
        val lArr = legalActionsElem.asJsonArray
        val lList = ArrayList<ProtocolLegalAction>(lArr.size())
        for (elem in lArr) lList.add(context.deserialize(elem, ProtocolLegalAction::class.java))
        val legalActions = Collections.unmodifiableList(lList)

        val lastDiscard: ProtocolTile? = if (obj.has("lastDiscard") && !obj.get("lastDiscard").isJsonNull) {
            context.deserialize(obj.get("lastDiscard"), ProtocolTile::class.java)
        } else null

        val lastDiscardPlayer: Int? = if (obj.has("lastDiscardPlayer") && !obj.get("lastDiscardPlayer").isJsonNull) {
            validateStrictInt(obj.get("lastDiscardPlayer"), "lastDiscardPlayer")
        } else null

        val mySleeveTiles: List<ProtocolTile> = if (obj.has("mySleeveTiles") && !obj.get("mySleeveTiles").isJsonNull) {
            val arr = obj.getAsJsonArray("mySleeveTiles")
            val list = ArrayList<ProtocolTile>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolTile::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        val myFuriten: ProtocolFuritenStatus = if (obj.has("myFuriten") && !obj.get("myFuriten").isJsonNull) {
            context.deserialize(obj.get("myFuriten"), ProtocolFuritenStatus::class.java)
        } else ProtocolFuritenStatus()

        val myDiscardHistory: List<ProtocolTile> = if (obj.has("myDiscardHistory") && !obj.get("myDiscardHistory").isJsonNull) {
            val arr = obj.getAsJsonArray("myDiscardHistory")
            val list = ArrayList<ProtocolTile>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolTile::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        return ProtocolPlayerView(
            viewerSeat = viewerSeat,
            phase = phase,
            roundWind = roundWind,
            roundNumber = roundNumber,
            honbaSticks = honbaSticks,
            riichiSticks = riichiSticks,
            dealerSeat = dealerSeat,
            currentTurnPlayer = currentTurnPlayer,
            wallRemaining = wallRemaining,
            doraIndicators = doraIndicators,
            scores = scores,
            riichiStatus = riichiStatus,
            discards = discards,
            melds = melds,
            handTileCounts = handTileCounts,
            myClosedTiles = myClosedTiles,
            myDrawnTile = myDrawnTile,
            legalActions = legalActions,
            lastDiscard = lastDiscard,
            lastDiscardPlayer = lastDiscardPlayer,
            mySleeveTiles = mySleeveTiles,
            myFuriten = myFuriten,
            myDiscardHistory = myDiscardHistory
        )
    }
}

/**
 * ProtocolRiichiPreviewOption TypeAdapter
 */
private class ProtocolRiichiPreviewOptionAdapter : JsonSerializer<ProtocolRiichiPreviewOption>, JsonDeserializer<ProtocolRiichiPreviewOption> {
    override fun serialize(src: ProtocolRiichiPreviewOption, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("optionId", src.optionId)
        obj.add("action", context.serialize(src.action, ProtocolLegalAction::class.java))
        val waitsArr = com.google.gson.JsonArray()
        for (t in src.waits) waitsArr.add(context.serialize(t, ProtocolTile::class.java))
        obj.add("waits", waitsArr)
        obj.add("furiten", context.serialize(src.furiten, ProtocolFuritenStatus::class.java))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolRiichiPreviewOption {
        if (!json.isJsonObject) throw JsonParseException("ProtocolRiichiPreviewOption must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("optionId", "action", "waits", "furiten"), "ProtocolRiichiPreviewOption")
        val optionId = validateStrictString(obj.get("optionId") ?: throw JsonParseException("Missing optionId"), "optionId")
        val actionElem = obj.get("action") ?: throw JsonParseException("Missing action")
        val action = context.deserialize<ProtocolLegalAction>(actionElem, ProtocolLegalAction::class.java)
        if (action !is ProtocolLegalAction.DeclareRiichi) {
            throw JsonParseException("ProtocolRiichiPreviewOption action must be DeclareRiichi, got ${action::class.java.simpleName}")
        }
        val waitsElem = obj.get("waits") ?: throw JsonParseException("Missing waits")
        if (!waitsElem.isJsonArray) throw JsonParseException("waits must be an array")
        val wArr = waitsElem.asJsonArray
        val wList = ArrayList<ProtocolTile>(wArr.size())
        for (elem in wArr) wList.add(context.deserialize(elem, ProtocolTile::class.java))
        val furiten = if (obj.has("furiten") && !obj.get("furiten").isJsonNull) {
            context.deserialize<ProtocolFuritenStatus>(obj.get("furiten"), ProtocolFuritenStatus::class.java)
        } else ProtocolFuritenStatus()
        return try {
            ProtocolRiichiPreviewOption(optionId, action, Collections.unmodifiableList(wList), furiten)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolRiichiPreviewOption: ${e.message}", e)
        }
    }
}

/**
 * ProtocolMeldPreviewOption TypeAdapter
 */
private class ProtocolMeldPreviewOptionAdapter : JsonSerializer<ProtocolMeldPreviewOption>, JsonDeserializer<ProtocolMeldPreviewOption> {
    override fun serialize(src: ProtocolMeldPreviewOption, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("optionId", src.optionId)
        obj.add("action", context.serialize(src.action, ProtocolLegalAction::class.java))
        obj.addProperty("type", src.type.name)
        val tilesArr = com.google.gson.JsonArray()
        for (t in src.tiles) tilesArr.add(context.serialize(t, ProtocolTile::class.java))
        obj.add("tiles", tilesArr)
        val handArr = com.google.gson.JsonArray()
        for (t in src.handTiles) handArr.add(context.serialize(t, ProtocolTile::class.java))
        obj.add("handTiles", handArr)
        if (src.calledTile != null) obj.add("calledTile", context.serialize(src.calledTile, ProtocolTile::class.java))
        if (src.addedTile != null) obj.add("addedTile", context.serialize(src.addedTile, ProtocolTile::class.java))
        if (src.fromPlayer != null) obj.addProperty("fromPlayer", src.fromPlayer)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolMeldPreviewOption {
        if (!json.isJsonObject) throw JsonParseException("ProtocolMeldPreviewOption must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("optionId", "action", "type", "tiles", "handTiles", "calledTile", "addedTile", "fromPlayer"), "ProtocolMeldPreviewOption")
        val optionId = validateStrictString(obj.get("optionId") ?: throw JsonParseException("Missing optionId"), "optionId")
        val actionElem = obj.get("action") ?: throw JsonParseException("Missing action")
        val action = context.deserialize<ProtocolLegalAction>(actionElem, ProtocolLegalAction::class.java)
        val typeStr = validateStrictString(obj.get("type") ?: throw JsonParseException("Missing type"), "type")
        val type = try {
            ProtocolMeldType.valueOf(typeStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid ProtocolMeldType: $typeStr", e)
        }
        val tilesElem = obj.get("tiles") ?: throw JsonParseException("Missing tiles")
        if (!tilesElem.isJsonArray) throw JsonParseException("tiles must be an array")
        val tArr = tilesElem.asJsonArray
        val tList = ArrayList<ProtocolTile>(tArr.size())
        for (elem in tArr) tList.add(context.deserialize(elem, ProtocolTile::class.java))

        val handElem = obj.get("handTiles") ?: throw JsonParseException("Missing handTiles")
        if (!handElem.isJsonArray) throw JsonParseException("handTiles must be an array")
        val hArr = handElem.asJsonArray
        val hList = ArrayList<ProtocolTile>(hArr.size())
        for (elem in hArr) hList.add(context.deserialize(elem, ProtocolTile::class.java))

        val calledTile = if (obj.has("calledTile") && !obj.get("calledTile").isJsonNull) {
            context.deserialize<ProtocolTile>(obj.get("calledTile"), ProtocolTile::class.java)
        } else null

        val addedTile = if (obj.has("addedTile") && !obj.get("addedTile").isJsonNull) {
            context.deserialize<ProtocolTile>(obj.get("addedTile"), ProtocolTile::class.java)
        } else null

        val fromPlayer = if (obj.has("fromPlayer") && !obj.get("fromPlayer").isJsonNull) {
            validateStrictInt(obj.get("fromPlayer"), "fromPlayer")
        } else null

        return try {
            ProtocolMeldPreviewOption(
                optionId = optionId,
                action = action,
                type = type,
                tiles = Collections.unmodifiableList(tList),
                handTiles = Collections.unmodifiableList(hList),
                calledTile = calledTile,
                addedTile = addedTile,
                fromPlayer = fromPlayer
            )
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolMeldPreviewOption: ${e.message}", e)
        }
    }
}

/**
 * ProtocolPlayerPresentationPreview TypeAdapter
 */
private class ProtocolPlayerPresentationPreviewAdapter : JsonSerializer<ProtocolPlayerPresentationPreview>, JsonDeserializer<ProtocolPlayerPresentationPreview> {
    override fun serialize(src: ProtocolPlayerPresentationPreview, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        val waitsArr = com.google.gson.JsonArray()
        for (t in src.waits) waitsArr.add(context.serialize(t, ProtocolTile::class.java))
        obj.add("waits", waitsArr)
        val riichiArr = com.google.gson.JsonArray()
        for (r in src.riichiOptions) riichiArr.add(context.serialize(r, ProtocolRiichiPreviewOption::class.java))
        obj.add("riichiOptions", riichiArr)
        val meldArr = com.google.gson.JsonArray()
        for (m in src.meldOptions) meldArr.add(context.serialize(m, ProtocolMeldPreviewOption::class.java))
        obj.add("meldOptions", meldArr)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPlayerPresentationPreview {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPlayerPresentationPreview must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roundId", "decisionId", "controlEpoch", "waits", "riichiOptions", "meldOptions"), "ProtocolPlayerPresentationPreview")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolPlayerPresentationPreview"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank in ProtocolPlayerPresentationPreview")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolPlayerPresentationPreview"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank in ProtocolPlayerPresentationPreview")
        val controlEpoch = validateStrictLong(obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolPlayerPresentationPreview"), "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")

        val waits: List<ProtocolTile> = if (obj.has("waits") && !obj.get("waits").isJsonNull) {
            val arr = obj.getAsJsonArray("waits")
            val list = ArrayList<ProtocolTile>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolTile::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        val riichiOptions: List<ProtocolRiichiPreviewOption> = if (obj.has("riichiOptions") && !obj.get("riichiOptions").isJsonNull) {
            val arr = obj.getAsJsonArray("riichiOptions")
            val list = ArrayList<ProtocolRiichiPreviewOption>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolRiichiPreviewOption::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        val meldOptions: List<ProtocolMeldPreviewOption> = if (obj.has("meldOptions") && !obj.get("meldOptions").isJsonNull) {
            val arr = obj.getAsJsonArray("meldOptions")
            val list = ArrayList<ProtocolMeldPreviewOption>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolMeldPreviewOption::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        return ProtocolPlayerPresentationPreview(
            roundId = roundId,
            decisionId = decisionId,
            controlEpoch = controlEpoch,
            waits = waits,
            riichiOptions = riichiOptions,
            meldOptions = meldOptions
        )
    }
}

/**
 * ProtocolPlayerSnapshot JsonDeserializer (确保快照及其嵌套对象全部不可变且校验完整性)
 */
private class ProtocolPlayerSnapshotAdapter : JsonDeserializer<ProtocolPlayerSnapshot> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPlayerSnapshot {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPlayerSnapshot must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "sessionId", "roundId", "syncEpoch", "cursor", "sessionVersion", "roundVersion",
                "view", "decision", "clocks", "cheatPoints", "session", "controls",
                "paused", "settlementRoundId", "settlementEvents", "preparation", "presentationPreview",
                "tileMark", "sleeveBloom", "beamSwap"
            ),
            "ProtocolPlayerSnapshot"
        )
        val sessionIdElem = obj.get("sessionId") ?: throw JsonParseException("Missing sessionId in ProtocolPlayerSnapshot")
        val sessionId = validateStrictString(sessionIdElem, "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")

        val roundId = obj.get("roundId")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "roundId") }

        val syncEpochElem = obj.get("syncEpoch") ?: throw JsonParseException("Missing syncEpoch in ProtocolPlayerSnapshot")
        val syncEpoch = validateStrictString(syncEpochElem, "syncEpoch")
        if (syncEpoch.isBlank()) throw JsonParseException("syncEpoch cannot be blank")

        val cursorElem = obj.get("cursor") ?: throw JsonParseException("Missing cursor in ProtocolPlayerSnapshot")
        val cursor = validateStrictLong(cursorElem, "cursor")
        if (cursor < 0L) throw JsonParseException("cursor must be non-negative: $cursor")

        val sessionVersionElem = obj.get("sessionVersion") ?: throw JsonParseException("Missing sessionVersion in ProtocolPlayerSnapshot")
        val sessionVersion = validateStrictLong(sessionVersionElem, "sessionVersion")
        if (sessionVersion < 0L) throw JsonParseException("sessionVersion must be non-negative: $sessionVersion")

        val roundVersion = if (obj.has("roundVersion") && !obj.get("roundVersion").isJsonNull) {
            val rv = validateStrictLong(obj.get("roundVersion"), "roundVersion")
            if (rv < 0L) throw JsonParseException("roundVersion must be non-negative: $rv")
            rv
        } else null

        val viewElem = obj.get("view") ?: throw JsonParseException("Missing view in ProtocolPlayerSnapshot")
        val decisionElem = obj.get("decision") ?: throw JsonParseException("Missing decision in ProtocolPlayerSnapshot")
        val view: ProtocolPlayerView = context.deserialize(viewElem, ProtocolPlayerView::class.java)
        val decision: ProtocolPlayerDecision = context.deserialize(decisionElem, ProtocolPlayerDecision::class.java)

        val clocks: List<ProtocolClock> = if (obj.has("clocks") && !obj.get("clocks").isJsonNull) {
            val arr = obj.getAsJsonArray("clocks")
            val list = ArrayList<ProtocolClock>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolClock::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        val cheatPoints: List<Int> = if (obj.has("cheatPoints") && !obj.get("cheatPoints").isJsonNull) {
            validateStrictIntList(obj.get("cheatPoints"), "cheatPoints")
        } else emptyList()

        val session: ProtocolSessionView? = if (obj.has("session") && !obj.get("session").isJsonNull) {
            context.deserialize(obj.get("session"), ProtocolSessionView::class.java)
        } else null

        val controls: List<ProtocolSeatControl> = if (obj.has("controls") && !obj.get("controls").isJsonNull) {
            val arr = obj.getAsJsonArray("controls")
            val list = ArrayList<ProtocolSeatControl>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolSeatControl::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        val paused = if (obj.has("paused") && !obj.get("paused").isJsonNull) {
            validateStrictBoolean(obj.get("paused"), "paused")
        } else false

        val settlementRoundId = obj.get("settlementRoundId")?.takeIf { !it.isJsonNull }?.let { validateStrictString(it, "settlementRoundId") }

        val settlementEvents: List<ProtocolGameEvent> = if (obj.has("settlementEvents") && !obj.get("settlementEvents").isJsonNull) {
            val arr = obj.getAsJsonArray("settlementEvents")
            val list = ArrayList<ProtocolGameEvent>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolGameEvent::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()

        val preparation: ProtocolShufflePreparation? = if (obj.has("preparation") && !obj.get("preparation").isJsonNull) {
            context.deserialize(obj.get("preparation"), ProtocolShufflePreparation::class.java)
        } else null

        val presentationPreview: ProtocolPlayerPresentationPreview? = if (obj.has("presentationPreview") && !obj.get("presentationPreview").isJsonNull) {
            context.deserialize(obj.get("presentationPreview"), ProtocolPlayerPresentationPreview::class.java)
        } else null

        if (presentationPreview != null) {
            if (roundId == null) {
                throw JsonParseException("presentationPreview cannot be present when snapshot roundId is null")
            }
            if (presentationPreview.roundId != roundId) {
                throw JsonParseException("presentationPreview.roundId (${presentationPreview.roundId}) does not match snapshot roundId ($roundId)")
            }
            if (decision.roundId != roundId) {
                throw JsonParseException("decision.roundId (${decision.roundId}) does not match roundId ($roundId) when presentationPreview is present")
            }
            if (presentationPreview.decisionId != decision.decisionId) {
                throw JsonParseException("presentationPreview.decisionId (${presentationPreview.decisionId}) does not match decisionId (${decision.decisionId})")
            }
            if (presentationPreview.controlEpoch != decision.controlEpoch) {
                throw JsonParseException("presentationPreview.controlEpoch (${presentationPreview.controlEpoch}) does not match decision.controlEpoch (${decision.controlEpoch})")
            }
            val decisionOptionsById = decision.options.associateBy { it.optionId }
            for (riichiOpt in presentationPreview.riichiOptions) {
                val matched = decisionOptionsById[riichiOpt.optionId]
                    ?: throw JsonParseException("presentationPreview riichi optionId '${riichiOpt.optionId}' not found in decision.options")
                if (matched.action != riichiOpt.action) {
                    throw JsonParseException("presentationPreview riichi option '${riichiOpt.optionId}' action does not match decision action")
                }
            }
            for (meldOpt in presentationPreview.meldOptions) {
                val matched = decisionOptionsById[meldOpt.optionId]
                    ?: throw JsonParseException("presentationPreview meld optionId '${meldOpt.optionId}' not found in decision.options")
                if (matched.action != meldOpt.action) {
                    throw JsonParseException("presentationPreview meld option '${meldOpt.optionId}' action does not match decision action")
                }
            }
        }

        val tileMark: ProtocolTileMarkView? = if (obj.has("tileMark") && !obj.get("tileMark").isJsonNull) {
            context.deserialize(obj.get("tileMark"), ProtocolTileMarkView::class.java)
        } else null

        if (tileMark != null) {
            for (group in tileMark.activeGroups) {
                for (tile in group.tiles) {
                    if (tile.area == ProtocolTileMarkArea.DISCARD) {
                        val pos = tile.riverPosition
                            ?: throw JsonParseException("Missing riverPosition for DISCARD marked tile")
                        if (roundId == null || pos.roundId != roundId) {
                            throw JsonParseException("RiverPosition roundId (${pos.roundId}) does not match snapshot roundId ($roundId)")
                        }
                        if (pos.seat != tile.seat) {
                            throw JsonParseException("RiverPosition seat (${pos.seat}) does not match marked tile seat (${tile.seat})")
                        }
                        val discards = view.discards.getOrNull(pos.seat)
                            ?: throw JsonParseException("Seat ${pos.seat} discards not found in view")
                        if (pos.discardIndex !in discards.indices) {
                            throw JsonParseException("RiverPosition discardIndex (${pos.discardIndex}) out of range (0 until ${discards.size})")
                        }
                        if (discards[pos.discardIndex].tileId != tile.tileId) {
                            throw JsonParseException("RiverPosition tile at seat ${pos.seat} index ${pos.discardIndex} is ${discards[pos.discardIndex].tileId}, expected ${tile.tileId}")
                        }
                    }
                }
            }
        }

        val sleeveBloom: ProtocolSleeveBloomView? = if (obj.has("sleeveBloom") && !obj.get("sleeveBloom").isJsonNull) {
            context.deserialize(obj.get("sleeveBloom"), ProtocolSleeveBloomView::class.java)
        } else null

        val beamSwap: ProtocolBeamSwapView? = if (obj.has("beamSwap") && !obj.get("beamSwap").isJsonNull) {
            context.deserialize(obj.get("beamSwap"), ProtocolBeamSwapView::class.java)
        } else null

        return try {
            ProtocolPlayerSnapshot(
                sessionId = sessionId,
                roundId = roundId,
                syncEpoch = syncEpoch,
                cursor = cursor,
                sessionVersion = sessionVersion,
                roundVersion = roundVersion,
                view = view,
                decision = decision,
                clocks = clocks,
                cheatPoints = cheatPoints,
                session = session,
                controls = controls,
                paused = paused,
                settlementRoundId = settlementRoundId,
                settlementEvents = settlementEvents,
                preparation = preparation,
                presentationPreview = presentationPreview,
                tileMark = tileMark,
                sleeveBloom = sleeveBloom,
                beamSwap = beamSwap
            )
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * ProtocolPlayerUpdate JsonDeserializer (确保增量更新包及其事件列表不可变)
 */
private class ProtocolPlayerUpdateAdapter : JsonDeserializer<ProtocolPlayerUpdate> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPlayerUpdate {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPlayerUpdate must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("snapshot", "fromCursor", "visibleEvents", "full", "isFullSnapshot"), "ProtocolPlayerUpdate")
        val snapshotElem = obj.get("snapshot") ?: throw JsonParseException("Missing snapshot in ProtocolPlayerUpdate")
        val snapshot: ProtocolPlayerSnapshot = context.deserialize(snapshotElem, ProtocolPlayerSnapshot::class.java)
        val fromCursorElem = obj.get("fromCursor") ?: throw JsonParseException("Missing fromCursor in ProtocolPlayerUpdate")
        val fromCursor = validateStrictLong(fromCursorElem, "fromCursor")
        if (fromCursor < 0L) throw JsonParseException("fromCursor must be non-negative: $fromCursor")
        val visibleEvents: List<ProtocolGameEvent> = if (obj.has("visibleEvents") && !obj.get("visibleEvents").isJsonNull) {
            val arr = obj.getAsJsonArray("visibleEvents")
            val list = ArrayList<ProtocolGameEvent>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolGameEvent::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()
        return ProtocolPlayerUpdate(snapshot, fromCursor, visibleEvents)
    }
}

/**
 * ProtocolRoundOutcome TypeAdapter (支持 6 种独立公开单局胜负类型)
 */
private class ProtocolRoundOutcomeAdapter : JsonSerializer<ProtocolRoundOutcome>, JsonDeserializer<ProtocolRoundOutcome> {
    override fun serialize(src: ProtocolRoundOutcome, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("outcomeType", src.outcomeType)
        when (src) {
            is ProtocolRoundOutcome.DealerWin -> {
                obj.addProperty("winner", src.winner)
            }
            is ProtocolRoundOutcome.NonDealerWin -> {
                obj.addProperty("winner", src.winner)
            }
            is ProtocolRoundOutcome.Ron -> {
                obj.addProperty("winner", src.winner)
                obj.addProperty("loser", src.loser)
            }
            is ProtocolRoundOutcome.Tsumo -> {
                obj.addProperty("winner", src.winner)
            }
            is ProtocolRoundOutcome.ExhaustiveDraw -> {
                val arr = com.google.gson.JsonArray()
                for (s in src.tenpaiSeats) arr.add(s)
                obj.add("tenpaiSeats", arr)
            }
            is ProtocolRoundOutcome.AbortiveDraw -> {
                obj.addProperty("reason", src.reason)
            }
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolRoundOutcome {
        if (!json.isJsonObject) throw JsonParseException("ProtocolRoundOutcome must be a JSON object: $json")
        val obj = json.asJsonObject
        val typeElem = obj.get("outcomeType") ?: throw JsonParseException("Missing outcomeType in ProtocolRoundOutcome")
        val outcomeType = validateStrictString(typeElem, "ProtocolRoundOutcome.outcomeType")
        return when (outcomeType) {
            "DealerWin" -> {
                checkUnknownFields(obj, setOf("outcomeType", "winner"), "ProtocolRoundOutcome.DealerWin")
                val w = validateStrictInt(obj.get("winner") ?: throw JsonParseException("Missing winner in DealerWin"), "winner")
                if (w !in 0..3) throw JsonParseException("winner must be in 0..3: $w")
                ProtocolRoundOutcome.DealerWin(w)
            }
            "NonDealerWin" -> {
                checkUnknownFields(obj, setOf("outcomeType", "winner"), "ProtocolRoundOutcome.NonDealerWin")
                val w = validateStrictInt(obj.get("winner") ?: throw JsonParseException("Missing winner in NonDealerWin"), "winner")
                if (w !in 0..3) throw JsonParseException("winner must be in 0..3: $w")
                ProtocolRoundOutcome.NonDealerWin(w)
            }
            "Ron" -> {
                checkUnknownFields(obj, setOf("outcomeType", "winner", "loser"), "ProtocolRoundOutcome.Ron")
                val w = validateStrictInt(obj.get("winner") ?: throw JsonParseException("Missing winner in Ron"), "winner")
                if (w !in 0..3) throw JsonParseException("winner must be in 0..3: $w")
                val l = validateStrictInt(obj.get("loser") ?: throw JsonParseException("Missing loser in Ron"), "loser")
                if (l !in 0..3) throw JsonParseException("loser must be in 0..3: $l")
                ProtocolRoundOutcome.Ron(w, l)
            }
            "Tsumo" -> {
                checkUnknownFields(obj, setOf("outcomeType", "winner"), "ProtocolRoundOutcome.Tsumo")
                val w = validateStrictInt(obj.get("winner") ?: throw JsonParseException("Missing winner in Tsumo"), "winner")
                if (w !in 0..3) throw JsonParseException("winner must be in 0..3: $w")
                ProtocolRoundOutcome.Tsumo(w)
            }
            "ExhaustiveDraw" -> {
                checkUnknownFields(obj, setOf("outcomeType", "tenpaiSeats"), "ProtocolRoundOutcome.ExhaustiveDraw")
                val seats: Set<Int> = if (obj.has("tenpaiSeats") && !obj.get("tenpaiSeats").isJsonNull) {
                    val arr = obj.getAsJsonArray("tenpaiSeats")
                    val set = LinkedHashSet<Int>(arr.size())
                    for (elem in arr) {
                        val s = validateStrictInt(elem, "tenpaiSeat")
                        if (s !in 0..3) throw JsonParseException("tenpaiSeat must be in 0..3: $s")
                        set.add(s)
                    }
                    Collections.unmodifiableSet(set)
                } else emptySet()
                ProtocolRoundOutcome.ExhaustiveDraw(seats)
            }
            "AbortiveDraw" -> {
                checkUnknownFields(obj, setOf("outcomeType", "reason"), "ProtocolRoundOutcome.AbortiveDraw")
                val reason = if (obj.has("reason") && !obj.get("reason").isJsonNull) {
                    validateStrictString(obj.get("reason"), "reason")
                } else ""
                ProtocolRoundOutcome.AbortiveDraw(reason)
            }
            else -> throw JsonParseException("Unknown ProtocolRoundOutcome outcomeType: $outcomeType")
        }
    }
}

/**
 * ProtocolPublicScoreRecord TypeAdapter
 */
private class ProtocolPublicScoreRecordAdapter : JsonSerializer<ProtocolPublicScoreRecord>, JsonDeserializer<ProtocolPublicScoreRecord> {
    override fun serialize(src: ProtocolPublicScoreRecord, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("resultId", src.resultId)
        val fArr = com.google.gson.JsonArray()
        for (s in src.finalScores) fArr.add(s)
        obj.add("finalScores", fArr)
        val dArr = com.google.gson.JsonArray()
        for (d in src.deltas) dArr.add(d)
        obj.add("deltas", dArr)
        obj.addProperty("remainingRiichiSticks", src.remainingRiichiSticks)
        if (src.outcome != null) {
            obj.add("outcome", context.serialize(src.outcome, ProtocolRoundOutcome::class.java))
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicScoreRecord {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicScoreRecord must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("roundId", "resultId", "finalScores", "deltas", "remainingRiichiSticks", "outcome"),
            "ProtocolPublicScoreRecord"
        )
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("ProtocolPublicScoreRecord missing roundId"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val resultId = validateStrictString(obj.get("resultId") ?: throw JsonParseException("ProtocolPublicScoreRecord missing resultId"), "resultId")
        if (resultId.isBlank()) throw JsonParseException("resultId cannot be blank")
        val finalScoresElem = obj.get("finalScores") ?: throw JsonParseException("ProtocolPublicScoreRecord missing finalScores")
        val finalScores = validateStrictIntList(finalScoresElem, "finalScores")
        if (finalScores.size != 4) throw JsonParseException("finalScores must have size 4: ${finalScores.size}")
        val deltasElem = obj.get("deltas") ?: throw JsonParseException("ProtocolPublicScoreRecord missing deltas")
        val deltas = validateStrictIntList(deltasElem, "deltas")
        if (deltas.size != 4) throw JsonParseException("deltas must have size 4: ${deltas.size}")
        val rrsElem = obj.get("remainingRiichiSticks") ?: throw JsonParseException("ProtocolPublicScoreRecord missing remainingRiichiSticks")
        val remainingRiichiSticks = validateStrictInt(rrsElem, "remainingRiichiSticks")
        if (remainingRiichiSticks < 0) throw JsonParseException("remainingRiichiSticks must be >= 0: $remainingRiichiSticks")
        val outcome: ProtocolRoundOutcome? = if (obj.has("outcome") && !obj.get("outcome").isJsonNull) {
            context.deserialize(obj.get("outcome"), ProtocolRoundOutcome::class.java)
        } else null
        return ProtocolPublicScoreRecord(roundId, resultId, finalScores, deltas, remainingRiichiSticks, outcome)
    }
}

/**
 * ProtocolPublicMatchResult TypeAdapter
 */
private class ProtocolPublicMatchResultAdapter : JsonSerializer<ProtocolPublicMatchResult>, JsonDeserializer<ProtocolPublicMatchResult> {
    override fun serialize(src: ProtocolPublicMatchResult, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        val fArr = com.google.gson.JsonArray()
        for (s in src.finalScores) fArr.add(s)
        obj.add("finalScores", fArr)
        val rArr = com.google.gson.JsonArray()
        for (r in src.rankedSeats) rArr.add(r)
        obj.add("rankedSeats", rArr)
        obj.addProperty("reason", src.reason)
        if (src.remainingRiichiSticksTransferredTo != null) {
            obj.addProperty("remainingRiichiSticksTransferredTo", src.remainingRiichiSticksTransferredTo)
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicMatchResult {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicMatchResult must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("finalScores", "rankedSeats", "reason", "remainingRiichiSticksTransferredTo"),
            "ProtocolPublicMatchResult"
        )
        val finalScoresElem = obj.get("finalScores") ?: throw JsonParseException("ProtocolPublicMatchResult missing finalScores")
        val finalScores = validateStrictIntList(finalScoresElem, "finalScores")
        if (finalScores.size != 4) throw JsonParseException("finalScores must have size 4: ${finalScores.size}")
        val rankedSeatsElem = obj.get("rankedSeats") ?: throw JsonParseException("ProtocolPublicMatchResult missing rankedSeats")
        val rankedSeats = validateStrictIntList(rankedSeatsElem, "rankedSeats")
        if (rankedSeats.size != 4) throw JsonParseException("rankedSeats must have size 4: ${rankedSeats.size}")
        if (rankedSeats.toSet() != setOf(0, 1, 2, 3)) throw JsonParseException("rankedSeats must be a permutation of 0..3: $rankedSeats")
        val reasonElem = obj.get("reason") ?: throw JsonParseException("ProtocolPublicMatchResult missing reason")
        val reason = validateStrictString(reasonElem, "reason")
        if (reason !in KNOWN_PUBLIC_FINISH_REASONS) throw JsonParseException("Unknown match finish reason: $reason")
        val trans = if (obj.has("remainingRiichiSticksTransferredTo") && !obj.get("remainingRiichiSticksTransferredTo").isJsonNull) {
            val s = validateStrictInt(obj.get("remainingRiichiSticksTransferredTo"), "remainingRiichiSticksTransferredTo")
            if (s !in 0..3) throw JsonParseException("remainingRiichiSticksTransferredTo must be in 0..3: $s")
            s
        } else null
        return ProtocolPublicMatchResult(finalScores, rankedSeats, reason, trans)
    }
}

/**
 * ProtocolPublicSessionSummary TypeAdapter
 */
private class ProtocolPublicSessionSummaryAdapter : JsonSerializer<ProtocolPublicSessionSummary>, JsonDeserializer<ProtocolPublicSessionSummary> {
    override fun serialize(src: ProtocolPublicSessionSummary, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("lifecycle", src.lifecycle)
        val sArr = com.google.gson.JsonArray()
        for (s in src.scores) sArr.add(s)
        obj.add("scores", sArr)
        obj.addProperty("roundWind", src.roundWind)
        obj.addProperty("roundNumber", src.roundNumber)
        obj.addProperty("dealerSeat", src.dealerSeat)
        obj.addProperty("honba", src.honba)
        val rArr = com.google.gson.JsonArray()
        for (r in src.roundRecords) rArr.add(context.serialize(r, ProtocolPublicScoreRecord::class.java))
        obj.add("roundRecords", rArr)
        if (src.finalResult != null) {
            obj.add("finalResult", context.serialize(src.finalResult, ProtocolPublicMatchResult::class.java))
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicSessionSummary {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicSessionSummary must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("lifecycle", "scores", "roundWind", "roundNumber", "dealerSeat", "honba", "roundRecords", "finalResult"),
            "ProtocolPublicSessionSummary"
        )
        val lifecycleElem = obj.get("lifecycle") ?: throw JsonParseException("ProtocolPublicSessionSummary missing lifecycle")
        val lifecycle = validateStrictString(lifecycleElem, "lifecycle")
        if (lifecycle !in KNOWN_PUBLIC_LIFECYCLES) throw JsonParseException("Unknown session lifecycle: $lifecycle")
        val scoresElem = obj.get("scores") ?: throw JsonParseException("ProtocolPublicSessionSummary missing scores")
        val scores = validateStrictIntList(scoresElem, "scores")
        if (scores.size != 4) throw JsonParseException("scores must have size 4: ${scores.size}")
        val roundWind = validateStrictInt(obj.get("roundWind") ?: throw JsonParseException("ProtocolPublicSessionSummary missing roundWind"), "roundWind")
        if (roundWind !in 1..4) throw JsonParseException("roundWind must be in 1..4: $roundWind")
        val roundNumber = validateStrictInt(obj.get("roundNumber") ?: throw JsonParseException("ProtocolPublicSessionSummary missing roundNumber"), "roundNumber")
        if (roundNumber < 0) throw JsonParseException("roundNumber must be >= 0: $roundNumber")
        val dealerSeat = validateStrictInt(obj.get("dealerSeat") ?: throw JsonParseException("ProtocolPublicSessionSummary missing dealerSeat"), "dealerSeat")
        if (dealerSeat !in 0..3) throw JsonParseException("dealerSeat must be in 0..3: $dealerSeat")
        val honba = validateStrictInt(obj.get("honba") ?: throw JsonParseException("ProtocolPublicSessionSummary missing honba"), "honba")
        if (honba < 0) throw JsonParseException("honba must be >= 0: $honba")
        val roundRecords: List<ProtocolPublicScoreRecord> = if (obj.has("roundRecords") && !obj.get("roundRecords").isJsonNull) {
            val arr = obj.getAsJsonArray("roundRecords")
            val list = ArrayList<ProtocolPublicScoreRecord>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolPublicScoreRecord::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()
        val finalResult: ProtocolPublicMatchResult? = if (obj.has("finalResult") && !obj.get("finalResult").isJsonNull) {
            context.deserialize(obj.get("finalResult"), ProtocolPublicMatchResult::class.java)
        } else null
        return ProtocolPublicSessionSummary(lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult)
    }
}

/**
 * ProtocolPublicBoardView TypeAdapter
 */
private class ProtocolPublicBoardViewAdapter : JsonSerializer<ProtocolPublicBoardView>, JsonDeserializer<ProtocolPublicBoardView> {
    override fun serialize(src: ProtocolPublicBoardView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("phase", src.phase.name)
        obj.addProperty("roundWind", src.roundWind)
        obj.addProperty("roundNumber", src.roundNumber)
        obj.addProperty("honbaSticks", src.honbaSticks)
        obj.addProperty("riichiSticks", src.riichiSticks)
        obj.addProperty("dealerSeat", src.dealerSeat)
        obj.addProperty("currentTurnPlayer", src.currentTurnPlayer)
        obj.addProperty("wallRemaining", src.wallRemaining)
        val dArr = com.google.gson.JsonArray()
        for (d in src.doraIndicators) dArr.add(context.serialize(d, ProtocolTile::class.java))
        obj.add("doraIndicators", dArr)
        val sArr = com.google.gson.JsonArray()
        for (s in src.scores) sArr.add(s)
        obj.add("scores", sArr)
        val rArr = com.google.gson.JsonArray()
        for (r in src.riichiStatus) rArr.add(r)
        obj.add("riichiStatus", rArr)
        val discArr = com.google.gson.JsonArray()
        for (pDisc in src.discards) {
            val inner = com.google.gson.JsonArray()
            for (t in pDisc) inner.add(context.serialize(t, ProtocolTile::class.java))
            discArr.add(inner)
        }
        obj.add("discards", discArr)
        val meldArr = com.google.gson.JsonArray()
        for (pMelds in src.melds) {
            val inner = com.google.gson.JsonArray()
            for (m in pMelds) inner.add(context.serialize(m, ProtocolMeld::class.java))
            meldArr.add(inner)
        }
        obj.add("melds", meldArr)
        val hArr = com.google.gson.JsonArray()
        for (h in src.handTileCounts) hArr.add(h)
        obj.add("handTileCounts", hArr)
        if (src.lastDiscard != null) {
            obj.add("lastDiscard", context.serialize(src.lastDiscard, ProtocolTile::class.java))
        }
        if (src.lastDiscardPlayer != null) {
            obj.addProperty("lastDiscardPlayer", src.lastDiscardPlayer)
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicBoardView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicBoardView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "phase", "roundWind", "roundNumber", "honbaSticks", "riichiSticks",
                "dealerSeat", "currentTurnPlayer", "wallRemaining", "doraIndicators",
                "scores", "riichiStatus", "discards", "melds", "handTileCounts",
                "lastDiscard", "lastDiscardPlayer"
            ),
            "ProtocolPublicBoardView"
        )
        val phaseStr = validateStrictString(obj.get("phase") ?: throw JsonParseException("ProtocolPublicBoardView missing phase"), "phase")
        val phase = try {
            ProtocolGamePhase.valueOf(phaseStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid phase in ProtocolPublicBoardView: $phaseStr", e)
        }
        val roundWind = validateStrictInt(obj.get("roundWind") ?: throw JsonParseException("ProtocolPublicBoardView missing roundWind"), "roundWind")
        if (roundWind !in 1..4) throw JsonParseException("roundWind must be in 1..4: $roundWind")
        val roundNumber = validateStrictInt(obj.get("roundNumber") ?: throw JsonParseException("ProtocolPublicBoardView missing roundNumber"), "roundNumber")
        if (roundNumber < 0) throw JsonParseException("roundNumber must be >= 0: $roundNumber")
        val honbaSticks = validateStrictInt(obj.get("honbaSticks") ?: throw JsonParseException("ProtocolPublicBoardView missing honbaSticks"), "honbaSticks")
        if (honbaSticks < 0) throw JsonParseException("honbaSticks must be >= 0: $honbaSticks")
        val riichiSticks = validateStrictInt(obj.get("riichiSticks") ?: throw JsonParseException("ProtocolPublicBoardView missing riichiSticks"), "riichiSticks")
        if (riichiSticks < 0) throw JsonParseException("riichiSticks must be >= 0: $riichiSticks")
        val dealerSeat = validateStrictInt(obj.get("dealerSeat") ?: throw JsonParseException("ProtocolPublicBoardView missing dealerSeat"), "dealerSeat")
        if (dealerSeat !in 0..3) throw JsonParseException("dealerSeat must be in 0..3: $dealerSeat")
        val currentTurnPlayer = validateStrictInt(obj.get("currentTurnPlayer") ?: throw JsonParseException("ProtocolPublicBoardView missing currentTurnPlayer"), "currentTurnPlayer")
        if (currentTurnPlayer !in 0..3) throw JsonParseException("currentTurnPlayer must be in 0..3: $currentTurnPlayer")
        val wallRemaining = validateStrictInt(obj.get("wallRemaining") ?: throw JsonParseException("ProtocolPublicBoardView missing wallRemaining"), "wallRemaining")
        if (wallRemaining < 0) throw JsonParseException("wallRemaining must be >= 0: $wallRemaining")
        val doraIndicatorsElem = obj.get("doraIndicators") ?: throw JsonParseException("ProtocolPublicBoardView missing doraIndicators")
        if (!doraIndicatorsElem.isJsonArray) throw JsonParseException("doraIndicators must be an array")
        val dArr = doraIndicatorsElem.asJsonArray
        val dList = ArrayList<ProtocolTile>(dArr.size())
        for (elem in dArr) dList.add(context.deserialize(elem, ProtocolTile::class.java))
        val doraIndicators = Collections.unmodifiableList(dList)
        val scoresElem = obj.get("scores") ?: throw JsonParseException("ProtocolPublicBoardView missing scores")
        val scores = validateStrictIntList(scoresElem, "scores")
        if (scores.size != 4) throw JsonParseException("scores must have size 4: ${scores.size}")
        val riichiElem = obj.get("riichiStatus") ?: throw JsonParseException("ProtocolPublicBoardView missing riichiStatus")
        val riichiStatus = validateStrictBooleanList(riichiElem, "riichiStatus")
        if (riichiStatus.size != 4) throw JsonParseException("riichiStatus must have size 4: ${riichiStatus.size}")
        val discardsElem = obj.get("discards") ?: throw JsonParseException("ProtocolPublicBoardView missing discards")
        if (!discardsElem.isJsonArray) throw JsonParseException("discards must be an array")
        val discArr = discardsElem.asJsonArray
        if (discArr.size() != 4) throw JsonParseException("discards must have size 4: ${discArr.size()}")
        val discardsList = ArrayList<List<ProtocolTile>>(4)
        for (i in 0 until 4) {
            val sub = discArr.get(i)
            if (!sub.isJsonArray) throw JsonParseException("discards[$i] must be an array")
            val subArr = sub.asJsonArray
            val subList = ArrayList<ProtocolTile>(subArr.size())
            for (elem in subArr) subList.add(context.deserialize(elem, ProtocolTile::class.java))
            discardsList.add(Collections.unmodifiableList(subList))
        }
        val discards = Collections.unmodifiableList(discardsList)
        val meldsElem = obj.get("melds") ?: throw JsonParseException("ProtocolPublicBoardView missing melds")
        if (!meldsElem.isJsonArray) throw JsonParseException("melds must be an array")
        val meldArr = meldsElem.asJsonArray
        if (meldArr.size() != 4) throw JsonParseException("melds must have size 4: ${meldArr.size()}")
        val meldsList = ArrayList<List<ProtocolMeld>>(4)
        for (i in 0 until 4) {
            val sub = meldArr.get(i)
            if (!sub.isJsonArray) throw JsonParseException("melds[$i] must be an array")
            val subArr = sub.asJsonArray
            val subList = ArrayList<ProtocolMeld>(subArr.size())
            for (elem in subArr) subList.add(context.deserialize(elem, ProtocolMeld::class.java))
            meldsList.add(Collections.unmodifiableList(subList))
        }
        val melds = Collections.unmodifiableList(meldsList)
        val hElem = obj.get("handTileCounts") ?: throw JsonParseException("ProtocolPublicBoardView missing handTileCounts")
        val handTileCounts = validateStrictIntList(hElem, "handTileCounts")
        if (handTileCounts.size != 4) throw JsonParseException("handTileCounts must have size 4: ${handTileCounts.size}")
        for (c in handTileCounts) {
            if (c < 0) throw JsonParseException("handTileCounts element cannot be negative: $c")
        }
        val lastDiscard: ProtocolTile? = if (obj.has("lastDiscard") && !obj.get("lastDiscard").isJsonNull) {
            context.deserialize(obj.get("lastDiscard"), ProtocolTile::class.java)
        } else null
        val lastDiscardPlayer = if (obj.has("lastDiscardPlayer") && !obj.get("lastDiscardPlayer").isJsonNull) {
            val p = validateStrictInt(obj.get("lastDiscardPlayer"), "lastDiscardPlayer")
            if (p !in 0..3) throw JsonParseException("lastDiscardPlayer must be in 0..3: $p")
            p
        } else null
        return ProtocolPublicBoardView(
            phase, roundWind, roundNumber, honbaSticks, riichiSticks, dealerSeat,
            currentTurnPlayer, wallRemaining, doraIndicators, scores, riichiStatus,
            discards, melds, handTileCounts, lastDiscard, lastDiscardPlayer
        )
    }
}

/**
 * ProtocolPublicSnapshot TypeAdapter
 */
private class ProtocolPublicSnapshotAdapter : JsonSerializer<ProtocolPublicSnapshot>, JsonDeserializer<ProtocolPublicSnapshot> {
    override fun serialize(src: ProtocolPublicSnapshot, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("sessionVersion", src.sessionVersion)
        if (src.roundId != null) obj.addProperty("roundId", src.roundId)
        if (src.roundVersion != null) obj.addProperty("roundVersion", src.roundVersion)
        obj.addProperty("syncEpoch", src.syncEpoch)
        obj.addProperty("cursor", src.cursor)
        if (src.board != null) {
            obj.add("board", context.serialize(src.board, ProtocolPublicBoardView::class.java))
        }
        obj.add("session", context.serialize(src.session, ProtocolPublicSessionSummary::class.java))
        val cArr = com.google.gson.JsonArray()
        for (c in src.clocks) cArr.add(context.serialize(c, ProtocolClock::class.java))
        obj.add("clocks", cArr)
        obj.addProperty("paused", src.paused)
        if (src.settlementRoundId != null) obj.addProperty("settlementRoundId", src.settlementRoundId)
        val sArr = com.google.gson.JsonArray()
        for (ev in src.settlementEvents) sArr.add(context.serialize(ev, ProtocolGameEvent::class.java))
        obj.add("settlementEvents", sArr)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicSnapshot {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicSnapshot must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "sessionId", "sessionVersion", "roundId", "roundVersion", "syncEpoch",
                "cursor", "board", "session", "clocks", "paused", "settlementRoundId", "settlementEvents"
            ),
            "ProtocolPublicSnapshot"
        )
        val sIdElem = obj.get("sessionId") ?: throw JsonParseException("ProtocolPublicSnapshot missing sessionId")
        val sessionId = validateStrictString(sIdElem, "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val sessionVersion = validateStrictLong(obj.get("sessionVersion") ?: throw JsonParseException("ProtocolPublicSnapshot missing sessionVersion"), "sessionVersion")
        if (sessionVersion < 0L) throw JsonParseException("sessionVersion must be >= 0: $sessionVersion")
        val roundId = if (obj.has("roundId") && !obj.get("roundId").isJsonNull) {
            val rId = validateStrictString(obj.get("roundId"), "roundId")
            if (rId.isBlank()) throw JsonParseException("roundId cannot be blank when present")
            rId
        } else null
        val roundVersion = if (obj.has("roundVersion") && !obj.get("roundVersion").isJsonNull) {
            val rv = validateStrictLong(obj.get("roundVersion"), "roundVersion")
            if (rv < 0L) throw JsonParseException("roundVersion must be >= 0: $rv")
            rv
        } else null
        val epochElem = obj.get("syncEpoch") ?: throw JsonParseException("ProtocolPublicSnapshot missing syncEpoch")
        val syncEpoch = validateStrictString(epochElem, "syncEpoch")
        if (syncEpoch.isBlank()) throw JsonParseException("syncEpoch cannot be blank")
        val cursorElem = obj.get("cursor") ?: throw JsonParseException("ProtocolPublicSnapshot missing cursor")
        val cursor = validateStrictLong(cursorElem, "cursor")
        if (cursor < 0L) throw JsonParseException("cursor must be >= 0: $cursor")
        val board: ProtocolPublicBoardView? = if (obj.has("board") && !obj.get("board").isJsonNull) {
            context.deserialize(obj.get("board"), ProtocolPublicBoardView::class.java)
        } else null
        val sessionElem = obj.get("session") ?: throw JsonParseException("ProtocolPublicSnapshot missing session")
        val session: ProtocolPublicSessionSummary = context.deserialize(sessionElem, ProtocolPublicSessionSummary::class.java)
        val clocks: List<ProtocolClock> = if (obj.has("clocks") && !obj.get("clocks").isJsonNull) {
            val arr = obj.getAsJsonArray("clocks")
            val list = ArrayList<ProtocolClock>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolClock::class.java))
            Collections.unmodifiableList(list)
        } else emptyList()
        val paused = if (obj.has("paused") && !obj.get("paused").isJsonNull) {
            validateStrictBoolean(obj.get("paused"), "paused")
        } else false
        val settlementRoundId = if (obj.has("settlementRoundId") && !obj.get("settlementRoundId").isJsonNull) {
            val sId = validateStrictString(obj.get("settlementRoundId"), "settlementRoundId")
            if (sId.isBlank()) throw JsonParseException("settlementRoundId cannot be blank when present")
            sId
        } else null
        val settlementEvents: List<ProtocolGameEvent> = if (obj.has("settlementEvents") && !obj.get("settlementEvents").isJsonNull) {
            val arr = obj.getAsJsonArray("settlementEvents")
            val list = ArrayList<ProtocolGameEvent>(arr.size())
            for (elem in arr) {
                val ev: ProtocolGameEvent = context.deserialize(elem, ProtocolGameEvent::class.java)
                try {
                    validatePublicGameEvent(ev)
                } catch (e: IllegalArgumentException) {
                    throw JsonParseException(e.message, e)
                }
                list.add(ev)
            }
            Collections.unmodifiableList(list)
        } else emptyList()
        return ProtocolPublicSnapshot(
            sessionId, sessionVersion, roundId, roundVersion, syncEpoch, cursor,
            board, session, clocks, paused, settlementRoundId, settlementEvents
        )
    }
}

/**
 * ProtocolPublicUpdate TypeAdapter
 */
private class ProtocolPublicUpdateAdapter : JsonSerializer<ProtocolPublicUpdate>, JsonDeserializer<ProtocolPublicUpdate> {
    override fun serialize(src: ProtocolPublicUpdate, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.add("snapshot", context.serialize(src.snapshot, ProtocolPublicSnapshot::class.java))
        obj.addProperty("fromCursor", src.fromCursor)
        val arr = com.google.gson.JsonArray()
        for (ev in src.visibleEvents) arr.add(context.serialize(ev, ProtocolGameEvent::class.java))
        obj.add("visibleEvents", arr)
        obj.addProperty("isFullSnapshot", src.isFullSnapshot)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolPublicUpdate {
        if (!json.isJsonObject) throw JsonParseException("ProtocolPublicUpdate must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("snapshot", "fromCursor", "visibleEvents", "isFullSnapshot"), "ProtocolPublicUpdate")
        val snapshotElem = obj.get("snapshot") ?: throw JsonParseException("ProtocolPublicUpdate missing snapshot")
        val snapshot: ProtocolPublicSnapshot = context.deserialize(snapshotElem, ProtocolPublicSnapshot::class.java)
        val fromCursorElem = obj.get("fromCursor") ?: throw JsonParseException("ProtocolPublicUpdate missing fromCursor")
        val fromCursor = validateStrictLong(fromCursorElem, "fromCursor")
        if (fromCursor < 0L) throw JsonParseException("fromCursor must be >= 0: $fromCursor")
        val isFullElem = obj.get("isFullSnapshot") ?: throw JsonParseException("ProtocolPublicUpdate missing isFullSnapshot")
        val isFullSnapshot = validateStrictBoolean(isFullElem, "isFullSnapshot")
        val visibleEvents: List<ProtocolGameEvent> = if (obj.has("visibleEvents") && !obj.get("visibleEvents").isJsonNull) {
            val arr = obj.getAsJsonArray("visibleEvents")
            val list = ArrayList<ProtocolGameEvent>(arr.size())
            for (elem in arr) {
                val ev: ProtocolGameEvent = context.deserialize(elem, ProtocolGameEvent::class.java)
                try {
                    validatePublicGameEvent(ev)
                } catch (e: IllegalArgumentException) {
                    throw JsonParseException(e.message, e)
                }
                list.add(ev)
            }
            Collections.unmodifiableList(list)
        } else emptyList()
        if (isFullSnapshot) {
            if (fromCursor != snapshot.cursor) {
                throw JsonParseException("For full snapshot baseline, fromCursor ($fromCursor) must equal snapshot.cursor (${snapshot.cursor})")
            }
        } else {
            if (snapshot.cursor != fromCursor + 1L) {
                throw JsonParseException("For regular update, snapshot.cursor (${snapshot.cursor}) must equal fromCursor + 1 (${fromCursor + 1L})")
            }
        }
        return ProtocolPublicUpdate(snapshot, fromCursor, visibleEvents, isFullSnapshot)
    }
}

/**
 * PublicCheckpoint TypeAdapter
 */
private class PublicCheckpointAdapter : JsonSerializer<PublicCheckpoint>, JsonDeserializer<PublicCheckpoint> {
    override fun serialize(src: PublicCheckpoint, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("syncEpoch", src.syncEpoch)
        obj.addProperty("cursor", src.cursor)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): PublicCheckpoint {
        if (!json.isJsonObject) throw JsonParseException("PublicCheckpoint must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("sessionId", "syncEpoch", "cursor"), "PublicCheckpoint")
        val sIdElem = obj.get("sessionId") ?: throw JsonParseException("PublicCheckpoint missing sessionId")
        val sessionId = validateStrictString(sIdElem, "PublicCheckpoint.sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val epochElem = obj.get("syncEpoch") ?: throw JsonParseException("PublicCheckpoint missing syncEpoch")
        val syncEpoch = validateStrictString(epochElem, "PublicCheckpoint.syncEpoch")
        if (syncEpoch.isBlank()) throw JsonParseException("syncEpoch cannot be blank")
        val cursorElem = obj.get("cursor") ?: throw JsonParseException("PublicCheckpoint missing cursor")
        val cursor = validateStrictLong(cursorElem, "PublicCheckpoint.cursor")
        if (cursor < 0L) throw JsonParseException("cursor must be >= 0: $cursor")
        try {
            return PublicCheckpoint(sessionId, syncEpoch, cursor)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * JoinSpectatorRequest TypeAdapter
 */
private class JoinSpectatorRequestAdapter : JsonSerializer<JoinSpectatorRequest>, JsonDeserializer<JoinSpectatorRequest> {
    override fun serialize(src: JoinSpectatorRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roomId", src.roomId)
        if (src.spectatorName != null) obj.addProperty("spectatorName", src.spectatorName)
        obj.addProperty("protocolVersion", src.protocolVersion)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): JoinSpectatorRequest {
        if (!json.isJsonObject) throw JsonParseException("JoinSpectatorRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "spectatorName", "protocolVersion"), "JoinSpectatorRequest")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("JoinSpectatorRequest missing roomId"), "roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank")
        val spectatorName = validateStrictOptionalString(obj.get("spectatorName"), "spectatorName")
        val protocolVersion = if (obj.has("protocolVersion") && !obj.get("protocolVersion").isJsonNull) {
            validateStrictInt(obj.get("protocolVersion"), "protocolVersion")
        } else ApiProtocol.PROTOCOL_VERSION
        if (protocolVersion < 1) throw JsonParseException("protocolVersion must be >= 1: $protocolVersion")
        try {
            return JoinSpectatorRequest(roomId, spectatorName, protocolVersion)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * ResumeSpectatorRequest TypeAdapter
 */
private class ResumeSpectatorRequestAdapter : JsonSerializer<ResumeSpectatorRequest>, JsonDeserializer<ResumeSpectatorRequest> {
    override fun serialize(src: ResumeSpectatorRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roomId", src.roomId)
        obj.addProperty("spectatorId", src.spectatorId)
        obj.addProperty("resumeToken", src.resumeToken)
        if (src.checkpoint != null) {
            obj.add("checkpoint", context.serialize(src.checkpoint, PublicCheckpoint::class.java))
        }
        obj.addProperty("protocolVersion", src.protocolVersion)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ResumeSpectatorRequest {
        if (!json.isJsonObject) throw JsonParseException("ResumeSpectatorRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "spectatorId", "resumeToken", "token", "checkpoint", "publicCheckpoint", "protocolVersion"), "ResumeSpectatorRequest")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("ResumeSpectatorRequest missing roomId"), "roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank")
        val spectatorId = validateStrictString(obj.get("spectatorId") ?: throw JsonParseException("ResumeSpectatorRequest missing spectatorId"), "spectatorId")
        if (spectatorId.isBlank()) throw JsonParseException("spectatorId cannot be blank")
        val tokenElem = obj.get("resumeToken") ?: obj.get("token") ?: throw JsonParseException("ResumeSpectatorRequest missing resumeToken")
        val resumeToken = validateStrictString(tokenElem, "resumeToken")
        if (resumeToken.isBlank()) throw JsonParseException("resumeToken cannot be blank")
        val cpElem = obj.get("checkpoint") ?: obj.get("publicCheckpoint")
        val checkpoint: PublicCheckpoint? = if (cpElem != null && !cpElem.isJsonNull) {
            context.deserialize(cpElem, PublicCheckpoint::class.java)
        } else null
        val protocolVersion = if (obj.has("protocolVersion") && !obj.get("protocolVersion").isJsonNull) {
            validateStrictInt(obj.get("protocolVersion"), "protocolVersion")
        } else ApiProtocol.PROTOCOL_VERSION
        if (protocolVersion < 1) throw JsonParseException("protocolVersion must be >= 1: $protocolVersion")
        try {
            return ResumeSpectatorRequest(roomId, spectatorId, resumeToken, checkpoint, protocolVersion)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * SpectatorBinding TypeAdapter
 */
private class SpectatorBindingAdapter : JsonSerializer<SpectatorBinding>, JsonDeserializer<SpectatorBinding> {
    override fun serialize(src: SpectatorBinding, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roomId", src.roomId)
        obj.addProperty("spectatorId", src.spectatorId)
        obj.addProperty("resumeToken", src.resumeToken)
        obj.add("snapshot", context.serialize(src.snapshot, ProtocolPublicSnapshot::class.java))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): SpectatorBinding {
        if (!json.isJsonObject) throw JsonParseException("SpectatorBinding must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roomId", "spectatorId", "resumeToken", "snapshot"), "SpectatorBinding")
        val roomId = validateStrictString(obj.get("roomId") ?: throw JsonParseException("SpectatorBinding missing roomId"), "roomId")
        if (roomId.isBlank()) throw JsonParseException("roomId cannot be blank")
        val spectatorId = validateStrictString(obj.get("spectatorId") ?: throw JsonParseException("SpectatorBinding missing spectatorId"), "spectatorId")
        if (spectatorId.isBlank()) throw JsonParseException("spectatorId cannot be blank")
        val resumeToken = validateStrictString(obj.get("resumeToken") ?: throw JsonParseException("SpectatorBinding missing resumeToken"), "resumeToken")
        if (resumeToken.isBlank()) throw JsonParseException("resumeToken cannot be blank")
        val snapElem = obj.get("snapshot") ?: throw JsonParseException("SpectatorBinding missing snapshot")
        val snapshot: ProtocolPublicSnapshot = context.deserialize(snapElem, ProtocolPublicSnapshot::class.java)
        try {
            return SpectatorBinding(roomId, spectatorId, resumeToken, snapshot)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * ApiResponse JsonDeserializer (确保集合与嵌套 DTO 完整性与不可变性，严格拒绝 PUBLIC 与 PLAYER 字段混合)
 */
private class ApiResponseAdapter : JsonDeserializer<ApiResponse> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ApiResponse {
        if (!json.isJsonObject) throw JsonParseException("ApiResponse must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "requestId", "success", "error", "roomId", "seat", "resumeToken", "rooms",
                "capabilities", "snapshot", "decision", "receipt", "events", "roomInfo",
                "spectatorBinding", "publicSnapshot", "publicEvents"
            ),
            "ApiResponse"
        )
        val requestId = validateStrictOptionalString(obj.get("requestId"), "requestId")
        val success = if (obj.has("success") && !obj.get("success").isJsonNull) {
            validateStrictBoolean(obj.get("success"), "success")
        } else true
        val error = validateStrictOptionalString(obj.get("error"), "error")
        val roomId = validateStrictOptionalString(obj.get("roomId"), "roomId")
        val seat = if (obj.has("seat") && !obj.get("seat").isJsonNull) {
            val s = validateStrictInt(obj.get("seat"), "seat")
            if (s !in 0..3) throw JsonParseException("seat must be 0..3: $s")
            s
        } else null
        val resumeToken = validateStrictOptionalString(obj.get("resumeToken"), "resumeToken")
        val rooms: List<RoomSummary>? = if (obj.has("rooms") && !obj.get("rooms").isJsonNull) {
            val arr = obj.getAsJsonArray("rooms")
            val list = ArrayList<RoomSummary>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, RoomSummary::class.java))
            Collections.unmodifiableList(list)
        } else null
        val capabilities: ProtocolAccessCapabilities? = if (obj.has("capabilities") && !obj.get("capabilities").isJsonNull) {
            context.deserialize(obj.get("capabilities"), ProtocolAccessCapabilities::class.java)
        } else null
        val snapshot: ProtocolPlayerSnapshot? = if (obj.has("snapshot") && !obj.get("snapshot").isJsonNull) {
            context.deserialize(obj.get("snapshot"), ProtocolPlayerSnapshot::class.java)
        } else null
        val decision: ProtocolPlayerDecision? = if (obj.has("decision") && !obj.get("decision").isJsonNull) {
            context.deserialize(obj.get("decision"), ProtocolPlayerDecision::class.java)
        } else null
        val receipt: ProtocolActionReceipt? = if (obj.has("receipt") && !obj.get("receipt").isJsonNull) {
            context.deserialize(obj.get("receipt"), ProtocolActionReceipt::class.java)
        } else null
        val events: List<ProtocolGameEvent>? = if (obj.has("events") && !obj.get("events").isJsonNull) {
            val arr = obj.getAsJsonArray("events")
            val list = ArrayList<ProtocolGameEvent>(arr.size())
            for (elem in arr) list.add(context.deserialize(elem, ProtocolGameEvent::class.java))
            Collections.unmodifiableList(list)
        } else null
        val roomInfo: ProtocolRoomInfo? = if (obj.has("roomInfo") && !obj.get("roomInfo").isJsonNull) {
            context.deserialize(obj.get("roomInfo"), ProtocolRoomInfo::class.java)
        } else null
        val spectatorBinding: SpectatorBinding? = if (obj.has("spectatorBinding") && !obj.get("spectatorBinding").isJsonNull) {
            context.deserialize(obj.get("spectatorBinding"), SpectatorBinding::class.java)
        } else null
        val publicSnapshot: ProtocolPublicSnapshot? = if (obj.has("publicSnapshot") && !obj.get("publicSnapshot").isJsonNull) {
            context.deserialize(obj.get("publicSnapshot"), ProtocolPublicSnapshot::class.java)
        } else null
        val publicEvents: List<ProtocolGameEvent>? = if (obj.has("publicEvents") && !obj.get("publicEvents").isJsonNull) {
            val arr = obj.getAsJsonArray("publicEvents")
            val list = ArrayList<ProtocolGameEvent>(arr.size())
            for (elem in arr) {
                val ev: ProtocolGameEvent = context.deserialize(elem, ProtocolGameEvent::class.java)
                try {
                    validatePublicGameEvent(ev)
                } catch (e: IllegalArgumentException) {
                    throw JsonParseException(e.message, e)
                }
                list.add(ev)
            }
            Collections.unmodifiableList(list)
        } else null

        val hasPublic = spectatorBinding != null || publicSnapshot != null || publicEvents != null
        val hasPlayer = seat != null || resumeToken != null || snapshot != null || decision != null || capabilities != null || events != null
        if (hasPublic && hasPlayer) {
            throw JsonParseException("PUBLIC and PLAYER response fields cannot mix")
        }

        try {
            return ApiResponse(
                requestId = requestId,
                success = success,
                error = error,
                roomId = roomId,
                seat = seat,
                resumeToken = resumeToken,
                rooms = rooms,
                capabilities = capabilities,
                snapshot = snapshot,
                decision = decision,
                receipt = receipt,
                events = events,
                roomInfo = roomInfo,
                spectatorBinding = spectatorBinding,
                publicSnapshot = publicSnapshot,
                publicEvents = publicEvents
            )
        } catch (e: IllegalArgumentException) {
            throw JsonParseException(e.message, e)
        }
    }
}

/**
 * ProtocolRoomMember TypeAdapter
 */
private class ProtocolRoomMemberAdapter : JsonSerializer<ProtocolRoomMember>, JsonDeserializer<ProtocolRoomMember> {
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
private class ProtocolTimeControlConfigAdapter : JsonSerializer<ProtocolTimeControlConfig>, JsonDeserializer<ProtocolTimeControlConfig> {
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
private class ProtocolRoomConfigAdapter : JsonSerializer<ProtocolRoomConfig>, JsonDeserializer<ProtocolRoomConfig> {
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
private class ProtocolRoomInfoAdapter : JsonSerializer<ProtocolRoomInfo>, JsonDeserializer<ProtocolRoomInfo> {
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

/**
 * ProtocolTileMarkConfig TypeAdapter
 */
private class ProtocolTileMarkConfigAdapter : JsonSerializer<ProtocolTileMarkConfig>, JsonDeserializer<ProtocolTileMarkConfig> {
    override fun serialize(src: ProtocolTileMarkConfig, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("enabled", src.enabled)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolTileMarkConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolTileMarkConfig must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("enabled"), "ProtocolTileMarkConfig")
        val enabled = if (obj.has("enabled") && !obj.get("enabled").isJsonNull) {
            validateStrictBoolean(obj.get("enabled"), "ProtocolTileMarkConfig.enabled")
        } else false
        return ProtocolTileMarkConfig(enabled)
    }
}

/**
 * ProtocolTileMarkRequest TypeAdapter
 */
private class ProtocolTileMarkRequestAdapter : JsonSerializer<ProtocolTileMarkRequest>, JsonDeserializer<ProtocolTileMarkRequest> {
    override fun serialize(src: ProtocolTileMarkRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestId", src.requestId)
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        val arr = com.google.gson.JsonArray()
        for (id in src.tileIds) arr.add(id)
        obj.add("tileIds", arr)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolTileMarkRequest {
        if (!json.isJsonObject) throw JsonParseException("ProtocolTileMarkRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("requestId", "sessionId", "roundId", "decisionId", "controlEpoch", "tileIds"),
            "ProtocolTileMarkRequest"
        )
        val requestId = validateStrictString(obj.get("requestId") ?: throw JsonParseException("Missing requestId in ProtocolTileMarkRequest"), "requestId")
        if (requestId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val sessionId = validateStrictString(obj.get("sessionId") ?: throw JsonParseException("Missing sessionId in ProtocolTileMarkRequest"), "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolTileMarkRequest"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolTileMarkRequest"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpoch = validateStrictLong(obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolTileMarkRequest"), "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        val tileIdsElem = obj.get("tileIds") ?: throw JsonParseException("Missing tileIds in ProtocolTileMarkRequest")
        val tileIds = validateStrictIntList(tileIdsElem, "tileIds")
        for (id in tileIds) {
            if (id !in 0..135) throw JsonParseException("tileId out of range 0..135: $id")
        }
        return try {
            ProtocolTileMarkRequest(requestId, sessionId, roundId, decisionId, controlEpoch, tileIds)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolTileMarkRequest: ${e.message}", e)
        }
    }
}

/**
 * ProtocolRiverPosition TypeAdapter
 */
private class ProtocolRiverPositionAdapter : JsonSerializer<ProtocolRiverPosition>, JsonDeserializer<ProtocolRiverPosition> {
    override fun serialize(src: ProtocolRiverPosition, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("seat", src.seat)
        obj.addProperty("discardIndex", src.discardIndex)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolRiverPosition {
        if (!json.isJsonObject) throw JsonParseException("ProtocolRiverPosition must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("roundId", "seat", "discardIndex"), "ProtocolRiverPosition")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolRiverPosition"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val seat = validateStrictInt(obj.get("seat") ?: throw JsonParseException("Missing seat in ProtocolRiverPosition"), "seat")
        if (seat !in 0..3) throw JsonParseException("seat must be in 0..3: $seat")
        val discardIndex = validateStrictInt(obj.get("discardIndex") ?: throw JsonParseException("Missing discardIndex in ProtocolRiverPosition"), "discardIndex")
        if (discardIndex < 0) throw JsonParseException("discardIndex must be non-negative: $discardIndex")
        return ProtocolRiverPosition(roundId, seat, discardIndex)
    }
}

/**
 * ProtocolMarkedTileView TypeAdapter
 */
private class ProtocolMarkedTileViewAdapter : JsonSerializer<ProtocolMarkedTileView>, JsonDeserializer<ProtocolMarkedTileView> {
    override fun serialize(src: ProtocolMarkedTileView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("tileId", src.tileId)
        obj.add("tile", context.serialize(src.tile, ProtocolTile::class.java))
        obj.addProperty("area", src.area.name)
        if (src.seat != null) obj.addProperty("seat", src.seat)
        if (src.riverPosition != null) obj.add("riverPosition", context.serialize(src.riverPosition, ProtocolRiverPosition::class.java))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolMarkedTileView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolMarkedTileView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("tileId", "tile", "area", "seat", "riverPosition"), "ProtocolMarkedTileView")
        val tileId = validateStrictInt(obj.get("tileId") ?: throw JsonParseException("Missing tileId in ProtocolMarkedTileView"), "tileId")
        if (tileId !in 0..135) throw JsonParseException("tileId must be in 0..135: $tileId")
        val tileElem = obj.get("tile") ?: throw JsonParseException("Missing tile in ProtocolMarkedTileView")
        val tile: ProtocolTile = context.deserialize(tileElem, ProtocolTile::class.java)
        if (tile.tileId != tileId) throw JsonParseException("tile.tileId (${tile.tileId}) does not match tileId ($tileId)")
        val areaElem = obj.get("area") ?: throw JsonParseException("Missing area in ProtocolMarkedTileView")
        val areaStr = validateStrictString(areaElem, "area")
        val area = try {
            ProtocolTileMarkArea.valueOf(areaStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid area in ProtocolMarkedTileView: $areaStr", e)
        }
        val seat = if (obj.has("seat") && !obj.get("seat").isJsonNull) {
            val s = validateStrictInt(obj.get("seat"), "seat")
            if (s !in 0..3) throw JsonParseException("seat must be in 0..3: $s")
            s
        } else null
        val riverPosition = if (obj.has("riverPosition") && !obj.get("riverPosition").isJsonNull) {
            context.deserialize<ProtocolRiverPosition>(obj.get("riverPosition"), ProtocolRiverPosition::class.java)
        } else null

        if (area == ProtocolTileMarkArea.DISCARD) {
            if (seat == null || seat !in 0..3) throw JsonParseException("seat must be non-null and in 0..3 for DISCARD, got: $seat")
            if (riverPosition == null) throw JsonParseException("riverPosition must be non-null for DISCARD")
            if (riverPosition.seat != seat) throw JsonParseException("riverPosition.seat (${riverPosition.seat}) must match seat ($seat)")
        } else {
            if (riverPosition != null) throw JsonParseException("riverPosition must be null when area is not DISCARD, was: $riverPosition")
        }
        if (area == ProtocolTileMarkArea.OWN_HAND || area == ProtocolTileMarkArea.OPPONENT_HAND || area == ProtocolTileMarkArea.EXPOSED_MELD) {
            if (seat == null || seat !in 0..3) throw JsonParseException("seat must be non-null and in 0..3 for $area, got: $seat")
        } else if (area == ProtocolTileMarkArea.EXPOSED_INDICATOR || area == ProtocolTileMarkArea.NOT_VISIBLE) {
            if (seat != null) throw JsonParseException("seat must be null for $area, got: $seat")
        }

        return try {
            ProtocolMarkedTileView(tileId, tile, area, seat, riverPosition)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolMarkedTileView: ${e.message}", e)
        }
    }
}

/**
 * ProtocolTileMarkGroupView TypeAdapter
 */
private class ProtocolTileMarkGroupViewAdapter : JsonSerializer<ProtocolTileMarkGroupView>, JsonDeserializer<ProtocolTileMarkGroupView> {
    override fun serialize(src: ProtocolTileMarkGroupView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("groupId", src.groupId)
        obj.addProperty("createdRoundId", src.createdRoundId)
        obj.addProperty("createdRoundOrdinal", src.createdRoundOrdinal)
        obj.addProperty("expiresAfterRoundOrdinal", src.expiresAfterRoundOrdinal)
        obj.addProperty("futureRoundsRemaining", src.futureRoundsRemaining)
        obj.addProperty("isLastEffectiveRound", src.isLastEffectiveRound)
        val arr = com.google.gson.JsonArray()
        for (t in src.tiles) arr.add(context.serialize(t, ProtocolMarkedTileView::class.java))
        obj.add("tiles", arr)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolTileMarkGroupView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolTileMarkGroupView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("groupId", "createdRoundId", "createdRoundOrdinal", "expiresAfterRoundOrdinal", "futureRoundsRemaining", "isLastEffectiveRound", "tiles"),
            "ProtocolTileMarkGroupView"
        )
        val groupId = validateStrictString(obj.get("groupId") ?: throw JsonParseException("Missing groupId in ProtocolTileMarkGroupView"), "groupId")
        if (groupId.isBlank()) throw JsonParseException("groupId cannot be blank")
        val createdRoundId = validateStrictString(obj.get("createdRoundId") ?: throw JsonParseException("Missing createdRoundId in ProtocolTileMarkGroupView"), "createdRoundId")
        if (createdRoundId.isBlank()) throw JsonParseException("createdRoundId cannot be blank")
        val createdRoundOrdinal = validateStrictInt(obj.get("createdRoundOrdinal") ?: throw JsonParseException("Missing createdRoundOrdinal in ProtocolTileMarkGroupView"), "createdRoundOrdinal")
        if (createdRoundOrdinal < 1 || createdRoundOrdinal > Int.MAX_VALUE - 3) {
            throw JsonParseException("createdRoundOrdinal out of valid range 1..${Int.MAX_VALUE - 3}: $createdRoundOrdinal")
        }
        val expiresAfterRoundOrdinal = validateStrictInt(obj.get("expiresAfterRoundOrdinal") ?: throw JsonParseException("Missing expiresAfterRoundOrdinal in ProtocolTileMarkGroupView"), "expiresAfterRoundOrdinal")
        if (expiresAfterRoundOrdinal.toLong() != createdRoundOrdinal.toLong() + 3L) {
            throw JsonParseException("expiresAfterRoundOrdinal ($expiresAfterRoundOrdinal) must equal createdRoundOrdinal ($createdRoundOrdinal) + 3")
        }
        val futureRoundsRemaining = validateStrictInt(obj.get("futureRoundsRemaining") ?: throw JsonParseException("Missing futureRoundsRemaining in ProtocolTileMarkGroupView"), "futureRoundsRemaining")
        if (futureRoundsRemaining !in 0..3) {
            throw JsonParseException("futureRoundsRemaining must be in 0..3: $futureRoundsRemaining")
        }
        val isLastEffectiveRound = validateStrictBoolean(obj.get("isLastEffectiveRound") ?: throw JsonParseException("Missing isLastEffectiveRound in ProtocolTileMarkGroupView"), "isLastEffectiveRound")
        if (isLastEffectiveRound != (futureRoundsRemaining == 0)) {
            throw JsonParseException("isLastEffectiveRound ($isLastEffectiveRound) must be true if and only if futureRoundsRemaining == 0 ($futureRoundsRemaining)")
        }
        val tilesElem = obj.get("tiles") ?: throw JsonParseException("Missing tiles in ProtocolTileMarkGroupView")
        if (!tilesElem.isJsonArray) throw JsonParseException("tiles must be a JSON array")
        val tilesArr = tilesElem.asJsonArray
        if (tilesArr.size() != 4) throw JsonParseException("Group tiles size must be exactly 4, got: ${tilesArr.size()}")
        val list = ArrayList<ProtocolMarkedTileView>(4)
        for (elem in tilesArr) {
            if (elem == null || elem.isJsonNull || !elem.isJsonObject) {
                throw JsonParseException("tiles element must be a non-null JSON object")
            }
            list.add(context.deserialize(elem, ProtocolMarkedTileView::class.java))
        }
        if (list.map { it.tileId }.distinct().size != 4) {
            throw JsonParseException("tiles within group must have 4 distinct tileIds")
        }
        return ProtocolTileMarkGroupView(groupId, createdRoundId, createdRoundOrdinal, expiresAfterRoundOrdinal, futureRoundsRemaining, isLastEffectiveRound, Collections.unmodifiableList(list))
    }
}

/**
 * ProtocolTileMarkView TypeAdapter
 */
private class ProtocolTileMarkViewAdapter : JsonSerializer<ProtocolTileMarkView>, JsonDeserializer<ProtocolTileMarkView> {
    override fun serialize(src: ProtocolTileMarkView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("canApply", src.canApply)
        if (src.disabledReason != null) obj.addProperty("disabledReason", src.disabledReason)
        obj.addProperty("cost", src.cost)
        obj.addProperty("activeTileCount", src.activeTileCount)
        obj.addProperty("maxActiveTiles", src.maxActiveTiles)
        val selArr = com.google.gson.JsonArray()
        for (id in src.selectableTileIds) selArr.add(id)
        obj.add("selectableTileIds", selArr)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        obj.addProperty("stateVersion", src.stateVersion)
        val grpArr = com.google.gson.JsonArray()
        for (g in src.activeGroups) grpArr.add(context.serialize(g, ProtocolTileMarkGroupView::class.java))
        obj.add("activeGroups", grpArr)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolTileMarkView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolTileMarkView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("canApply", "disabledReason", "cost", "activeTileCount", "maxActiveTiles", "selectableTileIds", "decisionId", "controlEpoch", "stateVersion", "activeGroups"),
            "ProtocolTileMarkView"
        )
        val canApply = validateStrictBoolean(obj.get("canApply") ?: throw JsonParseException("Missing canApply in ProtocolTileMarkView"), "canApply")
        val disabledReason = validateStrictOptionalString(obj.get("disabledReason"), "disabledReason")
        val cost = validateStrictInt(obj.get("cost") ?: throw JsonParseException("Missing cost in ProtocolTileMarkView"), "cost")
        if (cost != 10) throw JsonParseException("cost must be 10, got: $cost")
        val activeTileCount = validateStrictInt(obj.get("activeTileCount") ?: throw JsonParseException("Missing activeTileCount in ProtocolTileMarkView"), "activeTileCount")
        if (activeTileCount !in 0..12) throw JsonParseException("activeTileCount must be in 0..12, got: $activeTileCount")
        val maxActiveTiles = if (obj.has("maxActiveTiles") && !obj.get("maxActiveTiles").isJsonNull) {
            validateStrictInt(obj.get("maxActiveTiles"), "maxActiveTiles")
        } else 12
        if (maxActiveTiles != 12) throw JsonParseException("maxActiveTiles must be 12, got: $maxActiveTiles")
        val selectableTileIds = if (obj.has("selectableTileIds") && !obj.get("selectableTileIds").isJsonNull) {
            validateStrictIntList(obj.get("selectableTileIds"), "selectableTileIds")
        } else emptyList()
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolTileMarkView"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpoch = validateStrictLong(obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolTileMarkView"), "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        val stateVersion = validateStrictLong(obj.get("stateVersion") ?: throw JsonParseException("Missing stateVersion in ProtocolTileMarkView"), "stateVersion")
        if (stateVersion < 0L) throw JsonParseException("stateVersion must be non-negative: $stateVersion")
        val activeGroupsElem = obj.get("activeGroups") ?: throw JsonParseException("Missing activeGroups in ProtocolTileMarkView")
        if (!activeGroupsElem.isJsonArray) throw JsonParseException("activeGroups must be a JSON array")
        val grpArr = activeGroupsElem.asJsonArray
        if (grpArr.size() > 3) throw JsonParseException("activeGroups size cannot exceed 3, got: ${grpArr.size()}")
        val groupsList = ArrayList<ProtocolTileMarkGroupView>(grpArr.size())
        for (elem in grpArr) {
            if (elem == null || elem.isJsonNull || !elem.isJsonObject) {
                throw JsonParseException("activeGroups element must be a non-null JSON object")
            }
            groupsList.add(context.deserialize(elem, ProtocolTileMarkGroupView::class.java))
        }
        val totalTiles = groupsList.sumOf { it.tiles.size }
        if (totalTiles != activeTileCount) {
            throw JsonParseException("activeTileCount ($activeTileCount) must equal total tiles in activeGroups ($totalTiles)")
        }
        val allTileIds = groupsList.flatMap { g -> g.tiles.map { it.tileId } }
        if (allTileIds.distinct().size != allTileIds.size) {
            throw JsonParseException("duplicate tileId across activeGroups: $allTileIds")
        }
        val allGroupIds = groupsList.map { it.groupId }
        if (allGroupIds.distinct().size != allGroupIds.size) {
            throw JsonParseException("duplicate groupId across activeGroups: $allGroupIds")
        }
        return try {
            ProtocolTileMarkView(canApply, disabledReason, cost, activeTileCount, maxActiveTiles, selectableTileIds, decisionId, controlEpoch, stateVersion, groupsList)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolTileMarkView: ${e.message}", e)
        }
    }
}

/**
 * ProtocolSleeveBloomConfig TypeAdapter
 */
private class ProtocolSleeveBloomConfigAdapter : JsonSerializer<ProtocolSleeveBloomConfig>, JsonDeserializer<ProtocolSleeveBloomConfig> {
    override fun serialize(src: ProtocolSleeveBloomConfig, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("enabled", src.enabled)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSleeveBloomConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSleeveBloomConfig must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("enabled"), "ProtocolSleeveBloomConfig")
        val enabled = if (obj.has("enabled") && !obj.get("enabled").isJsonNull) {
            validateStrictBoolean(obj.get("enabled"), "ProtocolSleeveBloomConfig.enabled")
        } else false
        return ProtocolSleeveBloomConfig(enabled)
    }
}

/**
 * ProtocolSleeveBloomRequest TypeAdapter
 */
private class ProtocolSleeveBloomRequestAdapter : JsonSerializer<ProtocolSleeveBloomRequest>, JsonDeserializer<ProtocolSleeveBloomRequest> {
    override fun serialize(src: ProtocolSleeveBloomRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestId", src.requestId)
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSleeveBloomRequest {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSleeveBloomRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("requestId", "sessionId", "roundId", "decisionId", "controlEpoch"),
            "ProtocolSleeveBloomRequest"
        )
        val requestId = validateStrictString(obj.get("requestId") ?: throw JsonParseException("Missing requestId in ProtocolSleeveBloomRequest"), "requestId")
        if (requestId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val sessionId = validateStrictString(obj.get("sessionId") ?: throw JsonParseException("Missing sessionId in ProtocolSleeveBloomRequest"), "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolSleeveBloomRequest"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolSleeveBloomRequest"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolSleeveBloomRequest")
        ApiMessageCodec.validateControlEpochElement(controlEpochElem, "controlEpoch")
        val controlEpoch = validateStrictLong(controlEpochElem, "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        return try {
            ProtocolSleeveBloomRequest(requestId, sessionId, roundId, decisionId, controlEpoch)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolSleeveBloomRequest: ${e.message}", e)
        }
    }
}

/**
 * ProtocolSleeveBloomChoiceRequest TypeAdapter
 */
private class ProtocolSleeveBloomChoiceRequestAdapter : JsonSerializer<ProtocolSleeveBloomChoiceRequest>, JsonDeserializer<ProtocolSleeveBloomChoiceRequest> {
    override fun serialize(src: ProtocolSleeveBloomChoiceRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestId", src.requestId)
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        obj.addProperty("offerId", src.offerId)
        obj.addProperty("keep", src.keep.name)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSleeveBloomChoiceRequest {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSleeveBloomChoiceRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("requestId", "sessionId", "roundId", "decisionId", "controlEpoch", "offerId", "keep"),
            "ProtocolSleeveBloomChoiceRequest"
        )
        val requestId = validateStrictString(obj.get("requestId") ?: throw JsonParseException("Missing requestId in ProtocolSleeveBloomChoiceRequest"), "requestId")
        if (requestId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val sessionId = validateStrictString(obj.get("sessionId") ?: throw JsonParseException("Missing sessionId in ProtocolSleeveBloomChoiceRequest"), "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolSleeveBloomChoiceRequest"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolSleeveBloomChoiceRequest"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolSleeveBloomChoiceRequest")
        ApiMessageCodec.validateControlEpochElement(controlEpochElem, "controlEpoch")
        val controlEpoch = validateStrictLong(controlEpochElem, "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        val offerId = validateStrictString(obj.get("offerId") ?: throw JsonParseException("Missing offerId in ProtocolSleeveBloomChoiceRequest"), "offerId")
        if (offerId.isBlank()) throw JsonParseException("offerId cannot be blank")
        val keepElem = obj.get("keep") ?: throw JsonParseException("Missing keep in ProtocolSleeveBloomChoiceRequest")
        val keepStr = validateStrictString(keepElem, "keep")
        val keep = try {
            ProtocolSleeveBloomKeep.valueOf(keepStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid keep in ProtocolSleeveBloomChoiceRequest: $keepStr", e)
        }
        return try {
            ProtocolSleeveBloomChoiceRequest(requestId, sessionId, roundId, decisionId, controlEpoch, offerId, keep)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolSleeveBloomChoiceRequest: ${e.message}", e)
        }
    }
}

/**
 * ProtocolSleeveBloomOfferView TypeAdapter
 */
private class ProtocolSleeveBloomOfferViewAdapter : JsonSerializer<ProtocolSleeveBloomOfferView>, JsonDeserializer<ProtocolSleeveBloomOfferView> {
    override fun serialize(src: ProtocolSleeveBloomOfferView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("offerId", src.offerId)
        obj.add("firstTile", context.serialize(src.firstTile, ProtocolTile::class.java))
        obj.add("secondTile", context.serialize(src.secondTile, ProtocolTile::class.java))
        obj.addProperty("drawKind", src.drawKind.name)
        obj.addProperty("deadlineMillis", src.deadlineMillis)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSleeveBloomOfferView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSleeveBloomOfferView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("offerId", "firstTile", "secondTile", "drawKind", "deadlineMillis", "decisionId", "controlEpoch"),
            "ProtocolSleeveBloomOfferView"
        )
        val offerId = validateStrictString(obj.get("offerId") ?: throw JsonParseException("Missing offerId in ProtocolSleeveBloomOfferView"), "offerId")
        if (offerId.isBlank()) throw JsonParseException("offerId cannot be blank")
        val firstTileElem = obj.get("firstTile") ?: throw JsonParseException("Missing firstTile in ProtocolSleeveBloomOfferView")
        val firstTile: ProtocolTile = context.deserialize(firstTileElem, ProtocolTile::class.java)
        val secondTileElem = obj.get("secondTile") ?: throw JsonParseException("Missing secondTile in ProtocolSleeveBloomOfferView")
        val secondTile: ProtocolTile = context.deserialize(secondTileElem, ProtocolTile::class.java)
        val drawKindElem = obj.get("drawKind") ?: throw JsonParseException("Missing drawKind in ProtocolSleeveBloomOfferView")
        val drawKindStr = validateStrictString(drawKindElem, "drawKind")
        val drawKind = try {
            ProtocolSleeveBloomDrawKind.valueOf(drawKindStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid drawKind in ProtocolSleeveBloomOfferView: $drawKindStr", e)
        }
        val deadlineMillis = validateStrictLong(obj.get("deadlineMillis") ?: throw JsonParseException("Missing deadlineMillis in ProtocolSleeveBloomOfferView"), "deadlineMillis")
        if (deadlineMillis < 0L) throw JsonParseException("deadlineMillis must be non-negative: $deadlineMillis")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolSleeveBloomOfferView"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpoch = validateStrictLong(obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolSleeveBloomOfferView"), "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        return try {
            ProtocolSleeveBloomOfferView(offerId, firstTile, secondTile, drawKind, deadlineMillis, decisionId, controlEpoch)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolSleeveBloomOfferView: ${e.message}", e)
        }
    }
}

/**
 * ProtocolSleeveBloomView TypeAdapter
 */
private class ProtocolSleeveBloomViewAdapter : JsonSerializer<ProtocolSleeveBloomView>, JsonDeserializer<ProtocolSleeveBloomView> {
    override fun serialize(src: ProtocolSleeveBloomView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("canApply", src.canApply)
        if (src.disabledReason != null) obj.addProperty("disabledReason", src.disabledReason)
        obj.addProperty("cost", src.cost)
        if (src.drawKind != null) obj.addProperty("drawKind", src.drawKind.name)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        obj.addProperty("stateVersion", src.stateVersion)
        if (src.pendingOffer != null) {
            obj.add("pendingOffer", context.serialize(src.pendingOffer, ProtocolSleeveBloomOfferView::class.java))
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolSleeveBloomView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolSleeveBloomView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("canApply", "disabledReason", "cost", "drawKind", "decisionId", "controlEpoch", "stateVersion", "pendingOffer"),
            "ProtocolSleeveBloomView"
        )
        val canApply = validateStrictBoolean(obj.get("canApply") ?: throw JsonParseException("Missing canApply in ProtocolSleeveBloomView"), "canApply")
        val disabledReason = validateStrictOptionalString(obj.get("disabledReason"), "disabledReason")
        val cost = validateStrictInt(obj.get("cost") ?: throw JsonParseException("Missing cost in ProtocolSleeveBloomView"), "cost")
        if (cost != 10) throw JsonParseException("cost must be 10, got: $cost")
        val drawKind = if (obj.has("drawKind") && !obj.get("drawKind").isJsonNull) {
            val drawKindStr = validateStrictString(obj.get("drawKind"), "drawKind")
            try {
                ProtocolSleeveBloomDrawKind.valueOf(drawKindStr)
            } catch (e: Exception) {
                throw JsonParseException("Invalid drawKind in ProtocolSleeveBloomView: $drawKindStr", e)
            }
        } else null
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolSleeveBloomView"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpoch = validateStrictLong(obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolSleeveBloomView"), "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        val stateVersion = validateStrictLong(obj.get("stateVersion") ?: throw JsonParseException("Missing stateVersion in ProtocolSleeveBloomView"), "stateVersion")
        if (stateVersion < 0L) throw JsonParseException("stateVersion must be non-negative: $stateVersion")
        val pendingOffer: ProtocolSleeveBloomOfferView? = if (obj.has("pendingOffer") && !obj.get("pendingOffer").isJsonNull) {
            context.deserialize(obj.get("pendingOffer"), ProtocolSleeveBloomOfferView::class.java)
        } else null
        return try {
            ProtocolSleeveBloomView(canApply, disabledReason, cost, drawKind, decisionId, controlEpoch, stateVersion, pendingOffer)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolSleeveBloomView: ${e.message}", e)
        }
    }
}

/**
 * ProtocolBeamSwapSlot TypeAdapter
 */
private class ProtocolBeamSwapSlotAdapter : JsonSerializer<ProtocolBeamSwapSlot>, JsonDeserializer<ProtocolBeamSwapSlot> {
    override fun serialize(src: ProtocolBeamSwapSlot, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        val kind = when (src) {
            is ProtocolBeamSwapSlot.WallSlot -> "WALL"
            is ProtocolBeamSwapSlot.DeadWallSlot -> "DEAD_WALL"
            is ProtocolBeamSwapSlot.ReplacementSlot -> "REPLACEMENT"
        }
        obj.addProperty("kind", kind)
        obj.addProperty("index", src.index)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapSlot {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapSlot must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("kind", "index"), "ProtocolBeamSwapSlot")
        val kindElem = obj.get("kind") ?: throw JsonParseException("Missing kind in ProtocolBeamSwapSlot")
        val kindStr = validateStrictString(kindElem, "ProtocolBeamSwapSlot.kind")
        val indexElem = obj.get("index") ?: throw JsonParseException("Missing index in ProtocolBeamSwapSlot")
        val index = validateStrictInt(indexElem, "ProtocolBeamSwapSlot.index")
        if (index < 0) throw JsonParseException("index must be non-negative: $index")
        return when (kindStr) {
            "WALL" -> ProtocolBeamSwapSlot.WallSlot(index)
            "DEAD_WALL" -> ProtocolBeamSwapSlot.DeadWallSlot(index)
            "REPLACEMENT" -> ProtocolBeamSwapSlot.ReplacementSlot(index)
            else -> throw JsonParseException("Invalid kind in ProtocolBeamSwapSlot: $kindStr")
        }
    }
}

/**
 * ProtocolBeamSwapConfig TypeAdapter
 */
private class ProtocolBeamSwapConfigAdapter : JsonSerializer<ProtocolBeamSwapConfig>, JsonDeserializer<ProtocolBeamSwapConfig> {
    override fun serialize(src: ProtocolBeamSwapConfig, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("enabled", src.enabled)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapConfig must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("enabled"), "ProtocolBeamSwapConfig")
        val enabled = if (obj.has("enabled") && !obj.get("enabled").isJsonNull) {
            validateStrictBoolean(obj.get("enabled"), "ProtocolBeamSwapConfig.enabled")
        } else false
        return ProtocolBeamSwapConfig(enabled)
    }
}

/**
 * ProtocolBeamSwapRequest TypeAdapter
 */
private class ProtocolBeamSwapRequestAdapter : JsonSerializer<ProtocolBeamSwapRequest>, JsonDeserializer<ProtocolBeamSwapRequest> {
    override fun serialize(src: ProtocolBeamSwapRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestId", src.requestId)
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapRequest {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("requestId", "sessionId", "roundId", "decisionId", "controlEpoch"),
            "ProtocolBeamSwapRequest"
        )
        val requestId = validateStrictString(obj.get("requestId") ?: throw JsonParseException("Missing requestId in ProtocolBeamSwapRequest"), "requestId")
        if (requestId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val sessionId = validateStrictString(obj.get("sessionId") ?: throw JsonParseException("Missing sessionId in ProtocolBeamSwapRequest"), "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolBeamSwapRequest"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolBeamSwapRequest"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolBeamSwapRequest")
        ApiMessageCodec.validateControlEpochElement(controlEpochElem, "controlEpoch")
        val controlEpoch = validateStrictLong(controlEpochElem, "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        return try {
            ProtocolBeamSwapRequest(requestId, sessionId, roundId, decisionId, controlEpoch)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolBeamSwapRequest: ${e.message}", e)
        }
    }
}

/**
 * ProtocolBeamSwapChoiceRequest TypeAdapter
 */
private class ProtocolBeamSwapChoiceRequestAdapter : JsonSerializer<ProtocolBeamSwapChoiceRequest>, JsonDeserializer<ProtocolBeamSwapChoiceRequest> {
    override fun serialize(src: ProtocolBeamSwapChoiceRequest, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestId", src.requestId)
        obj.addProperty("sessionId", src.sessionId)
        obj.addProperty("roundId", src.roundId)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        obj.addProperty("offerId", src.offerId)
        obj.addProperty("action", src.action.name)
        if (src.action == ProtocolBeamSwapAction.SWAP) {
            src.handTileId?.let { obj.addProperty("handTileId", it) }
            src.targetSlot?.let { obj.add("targetSlot", context.serialize(it, ProtocolBeamSwapSlot::class.java)) }
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapChoiceRequest {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapChoiceRequest must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("requestId", "sessionId", "roundId", "decisionId", "controlEpoch", "offerId", "action", "handTileId", "targetSlot"),
            "ProtocolBeamSwapChoiceRequest"
        )
        val requestId = validateStrictString(obj.get("requestId") ?: throw JsonParseException("Missing requestId in ProtocolBeamSwapChoiceRequest"), "requestId")
        if (requestId.isBlank()) throw JsonParseException("requestId cannot be blank")
        val sessionId = validateStrictString(obj.get("sessionId") ?: throw JsonParseException("Missing sessionId in ProtocolBeamSwapChoiceRequest"), "sessionId")
        if (sessionId.isBlank()) throw JsonParseException("sessionId cannot be blank")
        val roundId = validateStrictString(obj.get("roundId") ?: throw JsonParseException("Missing roundId in ProtocolBeamSwapChoiceRequest"), "roundId")
        if (roundId.isBlank()) throw JsonParseException("roundId cannot be blank")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolBeamSwapChoiceRequest"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolBeamSwapChoiceRequest")
        ApiMessageCodec.validateControlEpochElement(controlEpochElem, "controlEpoch")
        val controlEpoch = validateStrictLong(controlEpochElem, "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        val offerId = validateStrictString(obj.get("offerId") ?: throw JsonParseException("Missing offerId in ProtocolBeamSwapChoiceRequest"), "offerId")
        if (offerId.isBlank()) throw JsonParseException("offerId cannot be blank")
        val actionElem = obj.get("action") ?: throw JsonParseException("Missing action in ProtocolBeamSwapChoiceRequest")
        val actionStr = validateStrictString(actionElem, "action")
        val action = try {
            ProtocolBeamSwapAction.valueOf(actionStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid action in ProtocolBeamSwapChoiceRequest: $actionStr", e)
        }
        val handTileId: Int?
        val targetSlot: ProtocolBeamSwapSlot?
        when (action) {
            ProtocolBeamSwapAction.KEEP_ORIGINAL -> {
                if (obj.has("handTileId")) {
                    throw JsonParseException("handTileId must be omitted when action is KEEP_ORIGINAL")
                }
                if (obj.has("targetSlot")) {
                    throw JsonParseException("targetSlot must be omitted when action is KEEP_ORIGINAL")
                }
                handTileId = null
                targetSlot = null
            }
            ProtocolBeamSwapAction.SWAP -> {
                val handElem = obj.get("handTileId") ?: throw JsonParseException("Missing handTileId in ProtocolBeamSwapChoiceRequest for SWAP")
                handTileId = validateStrictInt(handElem, "handTileId")
                if (handTileId !in 0..135) {
                    throw JsonParseException("handTileId must be in 0..135: $handTileId")
                }
                val slotElem = obj.get("targetSlot") ?: throw JsonParseException("Missing targetSlot in ProtocolBeamSwapChoiceRequest for SWAP")
                if (slotElem.isJsonNull || !slotElem.isJsonObject) {
                    throw JsonParseException("targetSlot must be a non-null JSON object")
                }
                targetSlot = context.deserialize(slotElem, ProtocolBeamSwapSlot::class.java)
            }
        }
        return try {
            ProtocolBeamSwapChoiceRequest(requestId, sessionId, roundId, decisionId, controlEpoch, offerId, action, handTileId, targetSlot)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolBeamSwapChoiceRequest: ${e.message}", e)
        }
    }
}

/**
 * ProtocolBeamSwapCandidateView TypeAdapter
 */
private class ProtocolBeamSwapCandidateViewAdapter : JsonSerializer<ProtocolBeamSwapCandidateView>, JsonDeserializer<ProtocolBeamSwapCandidateView> {
    override fun serialize(src: ProtocolBeamSwapCandidateView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.add("slot", context.serialize(src.slot, ProtocolBeamSwapSlot::class.java))
        obj.addProperty("marked", src.marked)
        if (src.marked && src.tile != null) {
            obj.add("tile", context.serialize(src.tile, ProtocolTile::class.java))
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapCandidateView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapCandidateView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("slot", "marked", "tile"), "ProtocolBeamSwapCandidateView")
        val slotElem = obj.get("slot") ?: throw JsonParseException("Missing slot in ProtocolBeamSwapCandidateView")
        if (slotElem.isJsonNull || !slotElem.isJsonObject) {
            throw JsonParseException("slot must be a non-null JSON object")
        }
        val slot: ProtocolBeamSwapSlot = context.deserialize(slotElem, ProtocolBeamSwapSlot::class.java)
            ?: throw JsonParseException("slot cannot deserialize to null")
        val markedElem = obj.get("marked") ?: throw JsonParseException("Missing marked in ProtocolBeamSwapCandidateView")
        val marked = validateStrictBoolean(markedElem, "ProtocolBeamSwapCandidateView.marked")
        val tile: ProtocolTile? = if (obj.has("tile") && !obj.get("tile").isJsonNull) {
            val tileElem = obj.get("tile")
            if (!tileElem.isJsonObject) throw JsonParseException("tile must be a non-null JSON object")
            context.deserialize(tileElem, ProtocolTile::class.java)
        } else null
        if (!marked && obj.has("tile")) {
            throw JsonParseException("Unmarked candidate cannot contain tile field")
        }
        if (marked && tile == null) {
            throw JsonParseException("Marked candidate must contain non-null tile")
        }
        return try {
            ProtocolBeamSwapCandidateView(slot, marked, tile)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolBeamSwapCandidateView: ${e.message}", e)
        }
    }
}

/**
 * ProtocolBeamSwapOfferView TypeAdapter
 */
private class ProtocolBeamSwapOfferViewAdapter : JsonSerializer<ProtocolBeamSwapOfferView>, JsonDeserializer<ProtocolBeamSwapOfferView> {
    override fun serialize(src: ProtocolBeamSwapOfferView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("offerId", src.offerId)
        obj.addProperty("window", src.window.name)
        val candArr = com.google.gson.JsonArray()
        for (c in src.candidates) candArr.add(context.serialize(c, ProtocolBeamSwapCandidateView::class.java))
        obj.add("candidates", candArr)
        val handArr = com.google.gson.JsonArray()
        for (h in src.selectableHandTileIds) handArr.add(h)
        obj.add("selectableHandTileIds", handArr)
        obj.addProperty("deadlineMillis", src.deadlineMillis)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapOfferView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapOfferView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("offerId", "window", "candidates", "selectableHandTileIds", "deadlineMillis", "decisionId", "controlEpoch"),
            "ProtocolBeamSwapOfferView"
        )
        val offerId = validateStrictString(obj.get("offerId") ?: throw JsonParseException("Missing offerId in ProtocolBeamSwapOfferView"), "offerId")
        if (offerId.isBlank()) throw JsonParseException("offerId cannot be blank")
        val windowElem = obj.get("window") ?: throw JsonParseException("Missing window in ProtocolBeamSwapOfferView")
        val windowStr = validateStrictString(windowElem, "window")
        val window = try {
            ProtocolBeamSwapWindow.valueOf(windowStr)
        } catch (e: Exception) {
            throw JsonParseException("Invalid window in ProtocolBeamSwapOfferView: $windowStr", e)
        }
        val candElem = obj.get("candidates") ?: throw JsonParseException("Missing candidates in ProtocolBeamSwapOfferView")
        if (!candElem.isJsonArray) throw JsonParseException("candidates must be a JSON array")
        val candArr = candElem.asJsonArray
        val candidates = ArrayList<ProtocolBeamSwapCandidateView>(candArr.size())
        for (elem in candArr) {
            if (elem == null || elem.isJsonNull || !elem.isJsonObject) {
                throw JsonParseException("Candidate element must be a non-null JSON object: $elem")
            }
            val candidate: ProtocolBeamSwapCandidateView = context.deserialize(elem, ProtocolBeamSwapCandidateView::class.java)
                ?: throw JsonParseException("Candidate element cannot deserialize to null")
            candidates.add(candidate)
        }
        val handElem = obj.get("selectableHandTileIds") ?: throw JsonParseException("Missing selectableHandTileIds in ProtocolBeamSwapOfferView")
        val selectableHandTileIds = validateStrictIntList(handElem, "selectableHandTileIds")
        for (h in selectableHandTileIds) {
            if (h !in 0..135) throw JsonParseException("selectableHandTileIds element out of range 0..135: $h")
        }
        val deadlineMillisElem = obj.get("deadlineMillis") ?: throw JsonParseException("Missing deadlineMillis in ProtocolBeamSwapOfferView")
        val deadlineMillis = validateStrictLong(deadlineMillisElem, "deadlineMillis")
        if (deadlineMillis < 0L) throw JsonParseException("deadlineMillis must be non-negative: $deadlineMillis")
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolBeamSwapOfferView"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpochElem = obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolBeamSwapOfferView")
        ApiMessageCodec.validateControlEpochElement(controlEpochElem, "controlEpoch")
        val controlEpoch = validateStrictLong(controlEpochElem, "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        return try {
            ProtocolBeamSwapOfferView(offerId, window, candidates, selectableHandTileIds, deadlineMillis, decisionId, controlEpoch)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolBeamSwapOfferView: ${e.message}", e)
        }
    }
}

/**
 * ProtocolBeamSwapView TypeAdapter
 */
private class ProtocolBeamSwapViewAdapter : JsonSerializer<ProtocolBeamSwapView>, JsonDeserializer<ProtocolBeamSwapView> {
    override fun serialize(src: ProtocolBeamSwapView, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("canApply", src.canApply)
        if (src.disabledReason != null) obj.addProperty("disabledReason", src.disabledReason)
        obj.addProperty("cost", src.cost)
        if (src.window != null) obj.addProperty("window", src.window.name)
        obj.addProperty("decisionId", src.decisionId)
        obj.addProperty("controlEpoch", src.controlEpoch)
        obj.addProperty("stateVersion", src.stateVersion)
        if (src.pendingOffer != null) {
            obj.add("pendingOffer", context.serialize(src.pendingOffer, ProtocolBeamSwapOfferView::class.java))
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolBeamSwapView {
        if (!json.isJsonObject) throw JsonParseException("ProtocolBeamSwapView must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("canApply", "disabledReason", "cost", "window", "decisionId", "controlEpoch", "stateVersion", "pendingOffer"),
            "ProtocolBeamSwapView"
        )
        val canApply = validateStrictBoolean(obj.get("canApply") ?: throw JsonParseException("Missing canApply in ProtocolBeamSwapView"), "canApply")
        val disabledReason = validateStrictOptionalString(obj.get("disabledReason"), "disabledReason")
        val cost = validateStrictInt(obj.get("cost") ?: throw JsonParseException("Missing cost in ProtocolBeamSwapView"), "cost")
        if (cost != 10) throw JsonParseException("cost must be 10, got: $cost")
        val window = if (obj.has("window") && !obj.get("window").isJsonNull) {
            val windowStr = validateStrictString(obj.get("window"), "window")
            try {
                ProtocolBeamSwapWindow.valueOf(windowStr)
            } catch (e: Exception) {
                throw JsonParseException("Invalid window in ProtocolBeamSwapView: $windowStr", e)
            }
        } else null
        val decisionId = validateStrictString(obj.get("decisionId") ?: throw JsonParseException("Missing decisionId in ProtocolBeamSwapView"), "decisionId")
        if (decisionId.isBlank()) throw JsonParseException("decisionId cannot be blank")
        val controlEpoch = validateStrictLong(obj.get("controlEpoch") ?: throw JsonParseException("Missing controlEpoch in ProtocolBeamSwapView"), "controlEpoch")
        if (controlEpoch < 0L) throw JsonParseException("controlEpoch must be non-negative: $controlEpoch")
        val stateVersion = validateStrictLong(obj.get("stateVersion") ?: throw JsonParseException("Missing stateVersion in ProtocolBeamSwapView"), "stateVersion")
        if (stateVersion < 0L) throw JsonParseException("stateVersion must be non-negative: $stateVersion")
        val pendingOffer: ProtocolBeamSwapOfferView? = if (obj.has("pendingOffer") && !obj.get("pendingOffer").isJsonNull) {
            context.deserialize(obj.get("pendingOffer"), ProtocolBeamSwapOfferView::class.java)
        } else null
        return try {
            ProtocolBeamSwapView(canApply, disabledReason, cost, window, decisionId, controlEpoch, stateVersion, pendingOffer)
        } catch (e: IllegalArgumentException) {
            throw JsonParseException("Invalid ProtocolBeamSwapView: ${e.message}", e)
        }
    }
}

/**
 * V4 消息编解码器 (ApiMessageCodec)
 *
 * 采用独立 Gson 实例，不 import engine，对多态类型进行显式标签编解码和严格输入边界校验。
 */
object ApiMessageCodec {
    val gson: Gson = GsonBuilder()
        .registerTypeAdapter(ProtocolTile::class.java, ProtocolTileAdapter())
        .registerTypeAdapter(ProtocolParamValue::class.java, ProtocolParamValueAdapter())
        .registerTypeAdapter(ProtocolLegalAction::class.java, ProtocolLegalActionAdapter())
        .registerTypeAdapter(ProtocolGameEvent::class.java, ProtocolGameEventAdapter())
        .registerTypeAdapter(ProtocolMeld::class.java, ProtocolMeldAdapter())
        .registerTypeAdapter(ProtocolCheatEconomyConfig::class.java, ProtocolCheatEconomyConfigAdapter())
        .registerTypeAdapter(ProtocolShuffleSelection::class.java, ProtocolShuffleSelectionAdapter())
        .registerTypeAdapter(ProtocolShufflePlan::class.java, ProtocolShufflePlanAdapter())
        .registerTypeAdapter(ProtocolMatchConfig::class.java, ProtocolMatchConfigAdapter())
        .registerTypeAdapter(ProtocolFuBreakdown::class.java, ProtocolFuBreakdownAdapter())
        .registerTypeAdapter(ProtocolDoraIndicatorHit::class.java, ProtocolDoraIndicatorHitAdapter())
        .registerTypeAdapter(ProtocolDoraBreakdown::class.java, ProtocolDoraBreakdownAdapter())
        .registerTypeAdapter(ProtocolScoreBreakdown::class.java, ProtocolScoreBreakdownAdapter())
        .registerTypeAdapter(ProtocolPublicRoundRecord::class.java, ProtocolPublicRoundRecordAdapter())
        .registerTypeAdapter(ProtocolPublicSessionResult::class.java, ProtocolPublicSessionResultAdapter())
        .registerTypeAdapter(ProtocolSessionView::class.java, ProtocolSessionViewAdapter())
        .registerTypeAdapter(ProtocolActionChoice::class.java, ProtocolActionChoiceAdapter())
        .registerTypeAdapter(ProtocolPlayerDecision::class.java, ProtocolPlayerDecisionAdapter())
        .registerTypeAdapter(ProtocolPlayerView::class.java, ProtocolPlayerViewAdapter())
        .registerTypeAdapter(ProtocolRiichiPreviewOption::class.java, ProtocolRiichiPreviewOptionAdapter())
        .registerTypeAdapter(ProtocolMeldPreviewOption::class.java, ProtocolMeldPreviewOptionAdapter())
        .registerTypeAdapter(ProtocolPlayerPresentationPreview::class.java, ProtocolPlayerPresentationPreviewAdapter())
        .registerTypeAdapter(ProtocolPlayerSnapshot::class.java, ProtocolPlayerSnapshotAdapter())
        .registerTypeAdapter(ProtocolPlayerUpdate::class.java, ProtocolPlayerUpdateAdapter())
        .registerTypeAdapter(ProtocolClock::class.java, ProtocolClockAdapter())
        .registerTypeAdapter(ProtocolSeatControl::class.java, ProtocolSeatControlAdapter())
        .registerTypeAdapter(ProtocolScoreResult::class.java, ProtocolScoreResultAdapter())
        .registerTypeAdapter(ProtocolFuLine::class.java, ProtocolFuLineAdapter())
        .registerTypeAdapter(ProtocolFuritenStatus::class.java, ProtocolFuritenStatusAdapter())
        .registerTypeAdapter(ProtocolActionReceipt::class.java, ProtocolActionReceiptAdapter())
        .registerTypeAdapter(ProtocolAccessCapabilities::class.java, ProtocolAccessCapabilitiesAdapter())
        .registerTypeAdapter(ProtocolActionOption::class.java, ProtocolActionOptionAdapter())
        .registerTypeAdapter(ProtocolShufflePreparation::class.java, ProtocolShufflePreparationAdapter())
        .registerTypeAdapter(ProtocolRoomMember::class.java, ProtocolRoomMemberAdapter())
        .registerTypeAdapter(ProtocolTimeControlConfig::class.java, ProtocolTimeControlConfigAdapter())
        .registerTypeAdapter(ProtocolRoomConfig::class.java, ProtocolRoomConfigAdapter())
        .registerTypeAdapter(ProtocolRoomInfo::class.java, ProtocolRoomInfoAdapter())
        .registerTypeAdapter(RoomSummary::class.java, RoomSummaryAdapter())
        .registerTypeAdapter(RoomBinding::class.java, RoomBindingAdapter())
        .registerTypeAdapter(CreateRoomRequest::class.java, CreateRoomRequestAdapter())
        .registerTypeAdapter(JoinRoomRequest::class.java, JoinRoomRequestAdapter())
        .registerTypeAdapter(ResumeRequest::class.java, ResumeRequestAdapter())
        .registerTypeAdapter(ApiRequest::class.java, ApiRequestAdapter())
        .registerTypeAdapter(ApiResponse::class.java, ApiResponseAdapter())
        .registerTypeAdapter(ProtocolRoundOutcome::class.java, ProtocolRoundOutcomeAdapter())
        .registerTypeAdapter(ProtocolPublicScoreRecord::class.java, ProtocolPublicScoreRecordAdapter())
        .registerTypeAdapter(ProtocolPublicMatchResult::class.java, ProtocolPublicMatchResultAdapter())
        .registerTypeAdapter(ProtocolPublicSessionSummary::class.java, ProtocolPublicSessionSummaryAdapter())
        .registerTypeAdapter(ProtocolPublicBoardView::class.java, ProtocolPublicBoardViewAdapter())
        .registerTypeAdapter(ProtocolPublicSnapshot::class.java, ProtocolPublicSnapshotAdapter())
        .registerTypeAdapter(ProtocolPublicUpdate::class.java, ProtocolPublicUpdateAdapter())
        .registerTypeAdapter(PublicCheckpoint::class.java, PublicCheckpointAdapter())
        .registerTypeAdapter(JoinSpectatorRequest::class.java, JoinSpectatorRequestAdapter())
        .registerTypeAdapter(ResumeSpectatorRequest::class.java, ResumeSpectatorRequestAdapter())
        .registerTypeAdapter(SpectatorBinding::class.java, SpectatorBindingAdapter())
        .registerTypeAdapter(ProtocolTileMarkConfig::class.java, ProtocolTileMarkConfigAdapter())
        .registerTypeAdapter(ProtocolTileMarkRequest::class.java, ProtocolTileMarkRequestAdapter())
        .registerTypeAdapter(ProtocolRiverPosition::class.java, ProtocolRiverPositionAdapter())
        .registerTypeAdapter(ProtocolMarkedTileView::class.java, ProtocolMarkedTileViewAdapter())
        .registerTypeAdapter(ProtocolTileMarkGroupView::class.java, ProtocolTileMarkGroupViewAdapter())
        .registerTypeAdapter(ProtocolTileMarkView::class.java, ProtocolTileMarkViewAdapter())
        .registerTypeAdapter(ProtocolSleeveBloomConfig::class.java, ProtocolSleeveBloomConfigAdapter())
        .registerTypeAdapter(ProtocolSleeveBloomRequest::class.java, ProtocolSleeveBloomRequestAdapter())
        .registerTypeAdapter(ProtocolSleeveBloomChoiceRequest::class.java, ProtocolSleeveBloomChoiceRequestAdapter())
        .registerTypeAdapter(ProtocolSleeveBloomOfferView::class.java, ProtocolSleeveBloomOfferViewAdapter())
        .registerTypeAdapter(ProtocolSleeveBloomView::class.java, ProtocolSleeveBloomViewAdapter())
        .registerTypeAdapter(ProtocolBeamSwapSlot::class.java, ProtocolBeamSwapSlotAdapter())
        .registerTypeHierarchyAdapter(ProtocolBeamSwapSlot::class.java, ProtocolBeamSwapSlotAdapter())
        .registerTypeAdapter(ProtocolBeamSwapConfig::class.java, ProtocolBeamSwapConfigAdapter())
        .registerTypeAdapter(ProtocolBeamSwapRequest::class.java, ProtocolBeamSwapRequestAdapter())
        .registerTypeAdapter(ProtocolBeamSwapChoiceRequest::class.java, ProtocolBeamSwapChoiceRequestAdapter())
        .registerTypeAdapter(ProtocolBeamSwapCandidateView::class.java, ProtocolBeamSwapCandidateViewAdapter())
        .registerTypeAdapter(ProtocolBeamSwapOfferView::class.java, ProtocolBeamSwapOfferViewAdapter())
        .registerTypeAdapter(ProtocolBeamSwapView::class.java, ProtocolBeamSwapViewAdapter())
        .create()

    fun validateControlEpochElement(epochElem: JsonElement, context: String) {
        if (!epochElem.isJsonPrimitive || !epochElem.asJsonPrimitive.isNumber) {
            throw JsonParseException("$context: controlEpoch must be a primitive number")
        }
        val prim = epochElem.asJsonPrimitive
        val raw = prim.asString
        if (raw.contains(".") || raw.contains("e", ignoreCase = true)) {
            throw JsonParseException("$context: fractional controlEpoch is not permitted: $raw")
        }
        val value = try {
            raw.toLong()
        } catch (e: Exception) {
            throw JsonParseException("$context: invalid controlEpoch integer: $raw", e)
        }
        if (value < 0L) {
            throw JsonParseException("$context: controlEpoch must be non-negative: $value")
        }
    }

    private fun validateSnapshotObject(snapObj: JsonObject) {
        val sessionId = snapObj.get("sessionId")?.takeIf { it.isJsonPrimitive }?.asString
        val syncEpoch = snapObj.get("syncEpoch")?.takeIf { it.isJsonPrimitive }?.asString
        if (sessionId.isNullOrBlank()) {
            throw JsonParseException("Incomplete snapshot: missing or blank sessionId")
        }
        if (syncEpoch.isNullOrBlank()) {
            throw JsonParseException("Incomplete snapshot: missing or blank syncEpoch")
        }
        val viewElem = snapObj.get("view")
        if (viewElem == null || !viewElem.isJsonObject) {
            throw JsonParseException("Incomplete snapshot: missing view object")
        }
        val viewObj = viewElem.asJsonObject
        if (viewObj.entrySet().isEmpty() || !viewObj.has("viewerSeat") || !viewObj.has("phase")) {
            throw JsonParseException("Incomplete snapshot: view object has missing or empty nested state")
        }
        val decisionElem = snapObj.get("decision")
        if (decisionElem == null || !decisionElem.isJsonObject) {
            throw JsonParseException("Incomplete snapshot: missing decision object")
        }
        val decisionObj = decisionElem.asJsonObject
        if (decisionObj.entrySet().isEmpty() || !decisionObj.has("decisionId")) {
            throw JsonParseException("Incomplete snapshot: decision object has missing or empty nested state")
        }
    }

    fun validatePublicSnapshotObject(snapObj: JsonObject) {
        val sIdElem = snapObj.get("sessionId")
        if (sIdElem == null || !sIdElem.isJsonPrimitive || !sIdElem.asJsonPrimitive.isString || sIdElem.asString.isBlank()) {
            throw JsonParseException("Incomplete public snapshot: missing or blank sessionId")
        }
        val epochElem = snapObj.get("syncEpoch")
        if (epochElem == null || !epochElem.isJsonPrimitive || !epochElem.asJsonPrimitive.isString || epochElem.asString.isBlank()) {
            throw JsonParseException("Incomplete public snapshot: missing or blank syncEpoch")
        }
        val sessionElem = snapObj.get("session")
        if (sessionElem == null || !sessionElem.isJsonObject) {
            throw JsonParseException("Incomplete public snapshot: missing session object")
        }
        val sessionObj = sessionElem.asJsonObject
        if (sessionObj.entrySet().isEmpty() || !sessionObj.has("lifecycle")) {
            throw JsonParseException("Incomplete public snapshot: session object has missing or empty nested state")
        }
    }

    fun encodeRequest(request: ApiRequest): String = gson.toJson(request)

    fun decodeRequest(json: String): ApiRequest {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("ApiRequest payload must be a JSON object")
        }
        val rootObj = elem.asJsonObject

        if (rootObj.has("role")) {
            throw JsonParseException("Clients cannot self-assert a spectator role")
        }

        val apiElem = rootObj.get("api")
        if (apiElem == null || !apiElem.isJsonPrimitive || !apiElem.asJsonPrimitive.isString || apiElem.asString.isBlank()) {
            throw JsonParseException("missing or blank api")
        }
        val requestIdElem = rootObj.get("requestId")
        if (requestIdElem == null || !requestIdElem.isJsonPrimitive || !requestIdElem.asJsonPrimitive.isString || requestIdElem.asString.isBlank()) {
            throw JsonParseException("missing or blank requestId")
        }

        val apiStr = apiElem.asString
        val isSpectatorApi = apiStr in setOf(
            ApiTags.JOIN_SPECTATOR,
            ApiTags.RESUME_SPECTATOR,
            ApiTags.PUBLIC_SNAPSHOT,
            ApiTags.RESUME_PUBLIC,
            ApiTags.QUERY_PUBLIC_ROUND_RESULT
        )
        if (isSpectatorApi) {
            if (rootObj.has("preferSeat")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'preferSeat'")
            }
            if (rootObj.has("choice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'choice'")
            }
            if (rootObj.has("shufflePlan")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'shufflePlan'")
            }
            if (rootObj.has("tileMark")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'tileMark'")
            }
            if (rootObj.has("sleeveBloom")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloom'")
            }
            if (rootObj.has("sleeveBloomChoice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloomChoice'")
            }
            when (apiStr) {
                ApiTags.JOIN_SPECTATOR -> {
                    val rElem = rootObj.get("roomId") ?: throw JsonParseException("roomId required for JOIN_SPECTATOR")
                    val r = validateStrictString(rElem, "roomId")
                    if (r.isBlank()) throw JsonParseException("roomId cannot be blank for JOIN_SPECTATOR")
                    if (rootObj.has("spectatorName") && !rootObj.get("spectatorName").isJsonNull) {
                        val sn = validateStrictString(rootObj.get("spectatorName"), "spectatorName")
                        if (sn.isBlank()) throw JsonParseException("spectatorName cannot be blank")
                    }
                }
                ApiTags.RESUME_SPECTATOR -> {
                    if (rootObj.has("cursor")) {
                        throw JsonParseException("cursor cannot be present in RESUME_SPECTATOR; use publicCheckpoint")
                    }
                    val rElem = rootObj.get("roomId") ?: throw JsonParseException("roomId required for RESUME_SPECTATOR")
                    val r = validateStrictString(rElem, "roomId")
                    if (r.isBlank()) throw JsonParseException("roomId cannot be blank for RESUME_SPECTATOR")

                    val sElem = rootObj.get("spectatorId") ?: throw JsonParseException("spectatorId required for RESUME_SPECTATOR")
                    val s = validateStrictString(sElem, "spectatorId")
                    if (s.isBlank()) throw JsonParseException("spectatorId cannot be blank for RESUME_SPECTATOR")

                    val tElem = rootObj.get("token") ?: throw JsonParseException("token required for RESUME_SPECTATOR")
                    val t = validateStrictString(tElem, "token")
                    if (t.isBlank()) throw JsonParseException("token cannot be blank for RESUME_SPECTATOR")

                    if (rootObj.has("publicCheckpoint") && !rootObj.get("publicCheckpoint").isJsonNull) {
                        val cpElem = rootObj.get("publicCheckpoint")
                        if (!cpElem.isJsonObject) throw JsonParseException("publicCheckpoint must be a JSON object")
                    }
                }
                ApiTags.RESUME_PUBLIC -> {
                    if (rootObj.has("cursor")) {
                        throw JsonParseException("cursor cannot be present in RESUME_PUBLIC; use publicCheckpoint")
                    }
                    val cpElem = rootObj.get("publicCheckpoint") ?: throw JsonParseException("publicCheckpoint required for RESUME_PUBLIC")
                    if (!cpElem.isJsonObject) throw JsonParseException("publicCheckpoint must be a JSON object")
                }
                ApiTags.QUERY_PUBLIC_ROUND_RESULT -> {
                    val rElem = rootObj.get("roundId") ?: throw JsonParseException("roundId required for QUERY_PUBLIC_ROUND_RESULT")
                    val r = validateStrictString(rElem, "roundId")
                    if (r.isBlank()) throw JsonParseException("roundId cannot be blank for QUERY_PUBLIC_ROUND_RESULT")
                }
            }
        }
        if (rootObj.has("protocolVersion")) {
            val pvElem = rootObj.get("protocolVersion")
            if (!pvElem.isJsonPrimitive || !pvElem.asJsonPrimitive.isNumber) {
                throw JsonParseException("protocolVersion must be a number")
            }
            val raw = pvElem.asJsonPrimitive.asString
            if (raw.contains(".") || raw.contains("e", ignoreCase = true)) {
                throw JsonParseException("protocolVersion must be an integer: $raw")
            }
            val pv = try {
                raw.toInt()
            } catch (e: Exception) {
                throw JsonParseException("invalid protocolVersion: $raw", e)
            }
            if (pv < 1) {
                throw JsonParseException("protocolVersion must be >= 1: $pv")
            }
        }
        if (rootObj.has("preferSeat")) {
            val sElem = rootObj.get("preferSeat")
            if (!sElem.isJsonNull) {
                if (!sElem.isJsonPrimitive || !sElem.asJsonPrimitive.isNumber) {
                    throw JsonParseException("preferSeat must be a number")
                }
                val raw = sElem.asJsonPrimitive.asString
                if (raw.contains(".") || raw.contains("e", ignoreCase = true)) {
                    throw JsonParseException("preferSeat must be an integer: $raw")
                }
            }
        }
        if (rootObj.has("cursor")) {
            val cElem = rootObj.get("cursor")
            if (!cElem.isJsonNull) {
                if (!cElem.isJsonPrimitive || !cElem.asJsonPrimitive.isNumber) {
                    throw JsonParseException("cursor must be a number")
                }
                val raw = cElem.asJsonPrimitive.asString
                if (raw.contains(".") || raw.contains("e", ignoreCase = true)) {
                    throw JsonParseException("cursor must be an integer: $raw")
                }
            }
        }

        // 严格线路上校验 choice 边界
        if (rootObj.has("choice")) {
            val choiceElem = rootObj.get("choice")
            if (choiceElem.isJsonObject) {
                val choiceObj = choiceElem.asJsonObject
                if (!choiceObj.has("controlEpoch")) {
                    throw JsonParseException("choice requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(choiceObj.get("controlEpoch"), "choice")
                if (choiceObj.has("roundId") && !choiceObj.get("roundId").isJsonNull) {
                    val roundIdElem = choiceObj.get("roundId")
                    if (!roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                        throw JsonParseException("roundId cannot be blank when present in choice")
                    }
                }
            } else if (!choiceElem.isJsonNull) {
                throw JsonParseException("choice must be a JSON object")
            }
        }

        // 严格线路上校验 shufflePlan 边界
        if (rootObj.has("shufflePlan")) {
            val planElem = rootObj.get("shufflePlan")
            if (planElem.isJsonObject) {
                val planObj = planElem.asJsonObject
                if (!planObj.has("controlEpoch")) {
                    throw JsonParseException("shufflePlan requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(planObj.get("controlEpoch"), "shufflePlan")
                val roundIdElem = planObj.get("roundId")
                if (roundIdElem == null || !roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                    throw JsonParseException("shufflePlan requires roundId at wire boundary")
                }
            } else if (!planElem.isJsonNull) {
                throw JsonParseException("shufflePlan must be a JSON object")
            }
        }

        // 严格线路上校验 tileMark 边界
        if (rootObj.has("tileMark")) {
            val tmElem = rootObj.get("tileMark")
            if (tmElem.isJsonObject) {
                val tmObj = tmElem.asJsonObject
                if (!tmObj.has("controlEpoch")) {
                    throw JsonParseException("tileMark requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(tmObj.get("controlEpoch"), "tileMark")
                val roundIdElem = tmObj.get("roundId")
                if (roundIdElem == null || !roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                    throw JsonParseException("tileMark requires roundId at wire boundary")
                }
            } else if (!tmElem.isJsonNull) {
                throw JsonParseException("tileMark must be a JSON object")
            }
        }
        // 严格线路上校验 sleeveBloom 边界
        if (rootObj.has("sleeveBloom")) {
            val elem = rootObj.get("sleeveBloom")
            if (elem.isJsonObject) {
                val obj = elem.asJsonObject
                if (!obj.has("controlEpoch")) {
                    throw JsonParseException("sleeveBloom requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(obj.get("controlEpoch"), "sleeveBloom")
                val roundIdElem = obj.get("roundId")
                if (roundIdElem == null || !roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                    throw JsonParseException("sleeveBloom requires roundId at wire boundary")
                }
            } else if (!elem.isJsonNull) {
                throw JsonParseException("sleeveBloom must be a JSON object")
            }
        }

        // 严格线路上校验 sleeveBloomChoice 边界
        if (rootObj.has("sleeveBloomChoice")) {
            val elem = rootObj.get("sleeveBloomChoice")
            if (elem.isJsonObject) {
                val obj = elem.asJsonObject
                if (!obj.has("controlEpoch")) {
                    throw JsonParseException("sleeveBloomChoice requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(obj.get("controlEpoch"), "sleeveBloomChoice")
                val roundIdElem = obj.get("roundId")
                if (roundIdElem == null || !roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                    throw JsonParseException("sleeveBloomChoice requires roundId at wire boundary")
                }
            } else if (!elem.isJsonNull) {
                throw JsonParseException("sleeveBloomChoice must be a JSON object")
            }
        }

        val req = try {
            gson.fromJson(rootObj, ApiRequest::class.java)
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("JSON mapping error: ${e.message}", e)
        } ?: throw JsonParseException("empty ApiRequest payload")

        req.config?.let { cfg ->
            if (cfg.playerCount != 4) {
                throw JsonParseException("Unsupported player count in config: ${cfg.playerCount}")
            }
            if (cfg.handsPerWind !in 1..4) {
                throw JsonParseException("Invalid handsPerWind in config: ${cfg.handsPerWind}")
            }
            if (cfg.winds.isEmpty()) {
                throw JsonParseException("Empty winds in config")
            }
            if (cfg.startingScores.size != cfg.playerCount) {
                throw JsonParseException("startingScores size does not match playerCount")
            }
            if (cfg.tieBreakSeats.size != cfg.playerCount) {
                throw JsonParseException("tieBreakSeats size does not match playerCount")
            }
        }
        req.choice?.let { ch ->
            if (ch.requestId.isBlank() || ch.decisionId.isBlank() || ch.optionId.isBlank()) {
                throw JsonParseException("Missing required fields in ActionChoice")
            }
            if (ch.roundId != null && ch.roundId.isBlank()) {
                throw JsonParseException("roundId cannot be blank when present in ActionChoice")
            }
            if (ch.controlEpoch < 0L) {
                throw JsonParseException("controlEpoch must be >= 0 in ActionChoice")
            }
        }
        req.shufflePlan?.let { sp ->
            if (sp.requestId.isBlank() || sp.roundId.isBlank()) {
                throw JsonParseException("Missing required fields in ShufflePlanRequest")
            }
            if (sp.controlEpoch < 0L) {
                throw JsonParseException("controlEpoch must be >= 0 in ShufflePlan")
            }
        }
        req.preferSeat?.let { seat ->
            if (seat !in 0..3) {
                throw JsonParseException("preferSeat out of range: $seat")
            }
        }
        req.cursor?.let { c ->
            if (c < 0L) {
                throw JsonParseException("cursor must be non-negative: $c")
            }
        }

        return req
    }

    fun encodeResponse(response: ApiResponse): String = gson.toJson(response)

    fun decodeResponse(json: String): ApiResponse {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("ApiResponse payload must be a JSON object")
        }
        val obj = elem.asJsonObject

        // 校验 snapshot 存在时的完整性，防御 Gson Unsafe 实例化产生空壳对象
        if (obj.has("snapshot")) {
            val snapElem = obj.get("snapshot")
            if (snapElem.isJsonNull) {
                // null ok
            } else if (snapElem.isJsonObject) {
                validateSnapshotObject(snapElem.asJsonObject)
            } else {
                throw JsonParseException("snapshot must be a JSON object")
            }
        }

        if (obj.has("publicSnapshot")) {
            val pubSnapElem = obj.get("publicSnapshot")
            if (pubSnapElem.isJsonNull) {
                // null ok
            } else if (pubSnapElem.isJsonObject) {
                validatePublicSnapshotObject(pubSnapElem.asJsonObject)
            } else {
                throw JsonParseException("publicSnapshot must be a JSON object")
            }
        }

        if (obj.has("spectatorBinding")) {
            val bindElem = obj.get("spectatorBinding")
            if (bindElem.isJsonNull) {
                // null ok
            } else if (bindElem.isJsonObject) {
                val bindObj = bindElem.asJsonObject
                if (bindObj.has("snapshot") && bindObj.get("snapshot").isJsonObject) {
                    validatePublicSnapshotObject(bindObj.getAsJsonObject("snapshot"))
                }
            } else {
                throw JsonParseException("spectatorBinding must be a JSON object")
            }
        }

        val resp = try {
            gson.fromJson(obj, ApiResponse::class.java)
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("JSON mapping error for ApiResponse: ${e.message}", e)
        } ?: throw JsonParseException("empty ApiResponse payload")

        return resp
    }

    fun encodePush(
        update: ProtocolPlayerUpdate,
        isFullSnapshot: Boolean = update.isFullSnapshot || (update.fromCursor >= update.snapshot.cursor)
    ): String {
        val tree = gson.toJsonTree(update).asJsonObject
        if (isFullSnapshot) {
            tree.addProperty("full", true)
        }
        return gson.toJson(tree)
    }

    fun decodePush(json: String): ProtocolPlayerUpdate = decodePushEnvelope(json).update

    fun decodePushEnvelope(json: String): DecodedPush {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("PlayerUpdate payload must be a JSON object")
        }
        val obj = elem.asJsonObject

        if (obj.has("snapshot")) {
            val snapElem = obj.get("snapshot")
            if (snapElem.isJsonObject) {
                validateSnapshotObject(snapElem.asJsonObject)
            } else if (!snapElem.isJsonNull) {
                throw JsonParseException("snapshot must be a JSON object")
            }
        }

        val hasExplicitMarker = when {
            obj.has("full") -> {
                val prop = obj.get("full")
                if (!prop.isJsonPrimitive || !prop.asJsonPrimitive.isBoolean) {
                    throw JsonParseException("Field 'full' must be a boolean")
                }
                prop.asBoolean
            }
            obj.has("isFullSnapshot") -> {
                val prop = obj.get("isFullSnapshot")
                if (!prop.isJsonPrimitive || !prop.asJsonPrimitive.isBoolean) {
                    throw JsonParseException("Field 'isFullSnapshot' must be a boolean")
                }
                prop.asBoolean
            }
            else -> false
        }

        val copy = obj.deepCopy()
        copy.remove("full")
        copy.remove("isFullSnapshot")
        val update = try {
            gson.fromJson(copy, ProtocolPlayerUpdate::class.java)
        } catch (e: Exception) {
            throw JsonParseException("JSON mapping error for ProtocolPlayerUpdate: ${e.message}", e)
        } ?: throw JsonParseException("empty ProtocolPlayerUpdate payload")

        val effectiveUpdate = if (update.isFullSnapshot != hasExplicitMarker) {
            update.copy(isFullSnapshot = hasExplicitMarker)
        } else {
            update
        }

        return DecodedPush(effectiveUpdate, hasExplicitMarker)
    }

    /** KICK 载荷 */
    fun encodeKick(reason: String): String = gson.toJson(mapOf("reason" to reason))

    fun decodeKick(json: String): String {
        val obj = JsonParser.parseString(json).asJsonObject
        return obj.get("reason")?.asString
            ?: throw JsonParseException("KICK payload missing reason")
    }

    fun encodeSnapshot(snapshot: ProtocolPlayerSnapshot): String = gson.toJson(snapshot)

    fun decodeSnapshot(json: String): ProtocolPlayerSnapshot {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("Snapshot payload must be a JSON object")
        }
        validateSnapshotObject(elem.asJsonObject)
        return gson.fromJson(elem, ProtocolPlayerSnapshot::class.java)
            ?: throw JsonParseException("empty snapshot payload")
    }

    fun encodeRoomInfo(roomInfo: ProtocolRoomInfo): String = gson.toJson(roomInfo)

    fun decodeRoomInfo(json: String): ProtocolRoomInfo {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("ProtocolRoomInfo payload must be a JSON object")
        }
        return gson.fromJson(elem, ProtocolRoomInfo::class.java)
            ?: throw JsonParseException("empty ProtocolRoomInfo payload")
    }

    /**
     * 编码显式定型房间更新推送信封 (ROOM_UPDATE PUSH envelope)
     */
    fun encodeRoomUpdatePush(roomInfo: ProtocolRoomInfo): String {
        val obj = JsonObject()
        obj.addProperty("type", "ROOM_UPDATE")
        obj.add("roomInfo", gson.toJsonTree(roomInfo))
        return gson.toJson(obj)
    }

    /**
     * 判断给定 JSON 是否为显式定型推送信封（存在 type 字段的 PUSH 载荷）。
     *
     * 仅当存在 type 字段且其值为 "ROOM_UPDATE" 时返回 true。
     * 对局推送不含 type 字段，PUBLIC 推送 type 必须为 "PUBLIC_UPDATE"。
     */
    fun isRoomUpdateEnvelope(json: String): Boolean {
        return try {
            val elem = JsonParser.parseString(json)
            if (!elem.isJsonObject) return false
            val obj = elem.asJsonObject
            val typeElem = obj.get("type") ?: return false
            typeElem.isJsonPrimitive && typeElem.asJsonPrimitive.isString && typeElem.asString == "ROOM_UPDATE"
        } catch (_: Exception) {
            false
        }
    }

    /**
     * 判断给定 JSON 是否为合法的 ROOM_UPDATE 推送（仅接受显式 typed envelope）
     */
    fun isRoomUpdatePush(json: String): Boolean = isRoomUpdateEnvelope(json)

    /**
     * 传输层 PUSH 信封分类器：区分 PLAYER / ROOM / PUBLIC
     *
     * 严格契约：
     * - 非 JSON 对象或语法错误抛出 JsonParseException
     * - 存在 type 字段时必须为非 null 字符串，且必须为已知类型（ROOM_UPDATE / PUBLIC_UPDATE）
     * - 明确为 null 的 type、未知 type、非字符串或格式错误抛出 JsonParseException
     * - 无 type 字段时判定为 PLAYER，且必须验证实际玩家信封结构（拒绝空对象 {} 与缺少合法 snapshot 的载荷）
     */
    fun classifyPush(json: String): PushType {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("PUSH payload must be a JSON object")
        }
        val obj = elem.asJsonObject
        if (obj.has("type")) {
            val typeElem = obj.get("type")
            if (typeElem == null || typeElem.isJsonNull || !typeElem.isJsonPrimitive || !typeElem.asJsonPrimitive.isString) {
                throw JsonParseException("PUSH 'type' must be a non-null string")
            }
            return when (val typeStr = typeElem.asString) {
                "ROOM_UPDATE" -> PushType.ROOM
                "PUBLIC_UPDATE" -> PushType.PUBLIC
                else -> throw JsonParseException("Unknown PUSH type: $typeStr")
            }
        }
        val snapElem = obj.get("snapshot")
            ?: throw JsonParseException("Player push envelope missing snapshot")
        if (!snapElem.isJsonObject) {
            throw JsonParseException("Player push envelope snapshot must be a JSON object")
        }
        validateSnapshotObject(snapElem.asJsonObject)
        decodePushEnvelope(json)
        return PushType.PLAYER
    }

    /**
     * 解码房间更新推送信封（严格显式定型：拒绝缺失/错误 type、额外信封字段与裸旧 JSON）
     */
    fun decodeRoomUpdatePush(json: String): ProtocolRoomInfo {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("RoomUpdate push payload must be a JSON object")
        }
        val obj = elem.asJsonObject
        checkUnknownFields(obj, setOf("type", "roomInfo"), "RoomUpdateEnvelope")
        val typeElem = obj.get("type") ?: throw JsonParseException("RoomUpdateEnvelope missing type")
        val type = validateStrictString(typeElem, "RoomUpdateEnvelope.type")
        if (type != "ROOM_UPDATE") {
            throw JsonParseException("RoomUpdateEnvelope.type must be ROOM_UPDATE: $type")
        }
        val roomInfoElem = obj.get("roomInfo") ?: throw JsonParseException("RoomUpdateEnvelope missing roomInfo")
        if (!roomInfoElem.isJsonObject) {
            throw JsonParseException("RoomUpdateEnvelope.roomInfo must be a JSON object")
        }
        return gson.fromJson(roomInfoElem, ProtocolRoomInfo::class.java)
            ?: throw JsonParseException("empty roomInfo in envelope")
    }

    /**
     * 编码公开流快照 (ProtocolPublicSnapshot)
     */
    fun encodePublicSnapshot(snapshot: ProtocolPublicSnapshot): String {
        return gson.toJson(snapshot)
    }

    /**
     * 解码公开流快照 (ProtocolPublicSnapshot)
     */
    fun decodePublicSnapshot(json: String): ProtocolPublicSnapshot {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("ProtocolPublicSnapshot payload must be a JSON object")
        }
        val obj = elem.asJsonObject
        validatePublicSnapshotObject(obj)
        return try {
            gson.fromJson(obj, ProtocolPublicSnapshot::class.java)
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("JSON mapping error for ProtocolPublicSnapshot: ${e.message}", e)
        } ?: throw JsonParseException("empty ProtocolPublicSnapshot payload")
    }

    /**
     * 编码公开流推送 (PUBLIC_UPDATE PUSH envelope)
     */
    fun encodePublicUpdatePush(update: ProtocolPublicUpdate): String {
        val obj = JsonObject()
        obj.addProperty("type", "PUBLIC_UPDATE")
        obj.add("update", gson.toJsonTree(update))
        return gson.toJson(obj)
    }

    /**
     * 解码公开流推送 (PUBLIC_UPDATE PUSH envelope)
     */
    fun decodePublicUpdatePush(json: String): ProtocolPublicUpdate {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("PUBLIC_UPDATE push payload must be a JSON object")
        }
        val obj = elem.asJsonObject
        checkUnknownFields(obj, setOf("type", "update"), "PublicUpdateEnvelope")
        val typeElem = obj.get("type") ?: throw JsonParseException("PublicUpdateEnvelope missing type")
        val type = validateStrictString(typeElem, "PublicUpdateEnvelope.type")
        if (type != "PUBLIC_UPDATE") {
            throw JsonParseException("PublicUpdateEnvelope.type must be PUBLIC_UPDATE: $type")
        }
        val updateElem = obj.get("update") ?: throw JsonParseException("PublicUpdateEnvelope missing update")
        if (!updateElem.isJsonObject) {
            throw JsonParseException("PublicUpdateEnvelope.update must be a JSON object")
        }
        return try {
            gson.fromJson(updateElem, ProtocolPublicUpdate::class.java)
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("JSON mapping error for ProtocolPublicUpdate: ${e.message}", e)
        } ?: throw JsonParseException("empty update in envelope")
    }

    /**
     * 编码公开流重传请求载荷 (PUBLIC_RESEND envelope)
     */
    fun encodePublicResendRequest(checkpoint: PublicCheckpoint): String {
        val obj = JsonObject()
        obj.addProperty("type", "PUBLIC_RESEND")
        obj.add("checkpoint", gson.toJsonTree(checkpoint))
        return gson.toJson(obj)
    }

    /**
     * 解码公开流重传请求载荷 (PUBLIC_RESEND envelope)
     */
    fun decodePublicResendRequest(json: String): PublicCheckpoint {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw JsonParseException("JSON syntax error: ${e.message}", e)
        }
        if (!elem.isJsonObject) {
            throw JsonParseException("PUBLIC_RESEND envelope must be a JSON object")
        }
        val obj = elem.asJsonObject
        checkUnknownFields(obj, setOf("type", "checkpoint"), "PublicResendEnvelope")
        val typeElem = obj.get("type") ?: throw JsonParseException("PublicResendEnvelope missing type")
        val type = validateStrictString(typeElem, "PublicResendEnvelope.type")
        if (type != "PUBLIC_RESEND") {
            throw JsonParseException("PublicResendEnvelope.type must be PUBLIC_RESEND: $type")
        }
        val cpElem = obj.get("checkpoint") ?: throw JsonParseException("PublicResendEnvelope missing checkpoint")
        if (!cpElem.isJsonObject) {
            throw JsonParseException("PublicResendEnvelope.checkpoint must be a JSON object")
        }
        return try {
            gson.fromJson(cpElem, PublicCheckpoint::class.java)
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("JSON mapping error for PublicCheckpoint: ${e.message}", e)
        } ?: throw JsonParseException("empty checkpoint in envelope")
    }
}

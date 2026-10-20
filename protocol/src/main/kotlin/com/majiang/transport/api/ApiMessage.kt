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
    const val SUBMIT_SLEEVE_BLOOM_OFF_TURN = "SUBMIT_SLEEVE_BLOOM_OFF_TURN"
    const val SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE = "SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE"
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
        SUBMIT_SLEEVE_BLOOM_OFF_TURN, SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE,
        SUBMIT_BEAM_SWAP, SUBMIT_BEAM_SWAP_CHOICE,
        START_MATCH, NEXT_ROUND, RESUME_CURSOR, CLAIM_CONTROL, RELEASE_CONTROL,
        QUERY_ROUND_RESULT,
        JOIN_SPECTATOR, RESUME_SPECTATOR, PUBLIC_SNAPSHOT, RESUME_PUBLIC, QUERY_PUBLIC_ROUND_RESULT
    )
}

/**
 * V4 统一请求信封（TCP / 传输 REQ payload）
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
    val sleeveBloomOffTurn: ProtocolSleeveBloomOffTurnRequest? = null,
    val sleeveBloomOffTurnChoice: ProtocolSleeveBloomOffTurnChoiceRequest? = null,
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
 * V4 统一响应信封（TCP / 传输 RSP payload）
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
        return ProtocolTile.fromTileId(tileId)
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
 * ProtocolShuffleConfig JsonDeserializer
 */
private class ProtocolShuffleConfigAdapter : JsonDeserializer<ProtocolShuffleConfig> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolShuffleConfig {
        if (!json.isJsonObject) throw JsonParseException("ProtocolShuffleConfig must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("enabled", "selectionMillis", "plannedDraws", "strength", "interference", "aiMaySpend", "seeThroughEnabled"), "ProtocolShuffleConfig")
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
        val seeThroughEnabled = if (obj.has("seeThroughEnabled")) {
            val elem = obj.get("seeThroughEnabled")
            if (elem.isJsonNull) throw JsonParseException("seeThroughEnabled must be a JSON boolean, explicit null is not allowed")
            validateStrictBoolean(elem, "seeThroughEnabled")
        } else false
        return ProtocolShuffleConfig(enabled, selectionMillis, plannedDraws, strength, interference, aiMaySpend, seeThroughEnabled)
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
        checkUnknownFields(obj, setOf("initialTargets", "drawTargets", "seeThrough"), "ProtocolShuffleSelection")
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
        val seeThrough = if (obj.has("seeThrough")) {
            val elem = obj.get("seeThrough")
            if (elem.isJsonNull) throw JsonParseException("seeThrough must be a JSON boolean, explicit null is not allowed")
            validateStrictBoolean(elem, "seeThrough")
        } else false
        return ProtocolShuffleSelection(initialTargets, drawTargets, seeThrough)
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
        checkUnknownFields(
            obj,
            setOf(
                "yakuLines", "yakumanCount", "dora", "fu", "score", "paymentLines",
                "honbaAddition", "riichiAddition", "totalPoints", "uraEligible", "isDealer", "isTsumo",
                ProtocolScoreCodes.FIELD_YAKU_ENTRIES, ProtocolScoreCodes.FIELD_PAYMENT_PLAN
            ),
            "ProtocolScoreBreakdown"
        )
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

        // v5 结构化役种行：类型/上限严格；未知 code 允许。与旧 yakuLines 不一致时
        // 丢弃结构化列表并回退旧文本，而不是拒绝整条快照。
        val decodedYakuEntries: List<ProtocolYakuEntry> =
            if (obj.has(ProtocolScoreCodes.FIELD_YAKU_ENTRIES) &&
                !obj.get(ProtocolScoreCodes.FIELD_YAKU_ENTRIES).isJsonNull
            ) {
                val entriesElem = obj.get(ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
                if (!entriesElem.isJsonArray) {
                    throw JsonParseException("ProtocolScoreBreakdown.yakuEntries must be an array")
                }
                val entriesArr = entriesElem.asJsonArray
                if (entriesArr.size() > ProtocolScoreCodes.MAX_YAKU_ENTRIES) {
                    throw JsonParseException(
                        "ProtocolScoreBreakdown.yakuEntries too many: ${entriesArr.size()}"
                    )
                }
                val entries = ArrayList<ProtocolYakuEntry>(entriesArr.size())
                for (i in 0 until entriesArr.size()) {
                    entries.add(
                        context.deserialize<ProtocolYakuEntry>(
                            entriesArr.get(i),
                            ProtocolYakuEntry::class.java
                        )
                    )
                }
                Collections.unmodifiableList(entries)
            } else {
                emptyList()
            }
        val yakuEntries = if (
            decodedYakuEntries.size == yakuLines.size &&
            decodedYakuEntries.indices.all { i ->
                decodedYakuEntries[i].displayName == yakuLines[i].first &&
                    decodedYakuEntries[i].han == yakuLines[i].second
            }
        ) {
            decodedYakuEntries
        } else {
            emptyList()
        }

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

        // v5 基础支付计划：类型/上限严格。已知 kind 必须与旧权威字段
        // （isDealer/isTsumo、角色/人数、basePoints 取整、totalPoints、paymentLines）
        // 全部一致，否则只降级 paymentPlan，客户端回退 paymentLines，不拒绝整条快照。
        // 未知 kind 保留原值供向前兼容审计；客户端映射会将其安全回退。
        val decodedPaymentPlan: ProtocolPaymentPlan? =
            if (obj.has(ProtocolScoreCodes.FIELD_PAYMENT_PLAN) &&
                !obj.get(ProtocolScoreCodes.FIELD_PAYMENT_PLAN).isJsonNull
            ) {
                context.deserialize<ProtocolPaymentPlan>(
                    obj.get(ProtocolScoreCodes.FIELD_PAYMENT_PLAN),
                    ProtocolPaymentPlan::class.java
                )
            } else {
                null
            }
        val paymentPlan = decodedPaymentPlan?.takeIf { plan ->
            if (!plan.isStructurallyConsistent()) {
                false
            } else if (!ProtocolScoreCodes.isKnownPaymentKind(plan.kind)) {
                // 未知可扩展码不视为损坏；保留供客户端显式回退。
                true
            } else {
                ProtocolPaymentPlanValidator.isConsistent(
                    plan = plan,
                    score = score,
                    paymentLines = paymentLines,
                    honbaAddition = honbaAddition,
                    riichiAddition = riichiAddition,
                    totalPoints = totalPoints,
                    isDealer = isDealer,
                    isTsumo = isTsumo
                )
            }
        }

        return ProtocolScoreBreakdown(
            yakuLines, yakumanCount, dora, fu, score, paymentLines,
            honbaAddition, riichiAddition, totalPoints, uraEligible, isDealer, isTsumo,
            yakuEntries, paymentPlan
        )
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
            setOf("api", "requestId", "roomId", "token", "playerName", "preferSeat", "config", "choice", "shufflePlan", "cursor", "roundId", "protocolVersion", "ready", "roomConfig", "spectatorId", "spectatorName", "publicCheckpoint", "tileMark", "sleeveBloom", "sleeveBloomChoice", "sleeveBloomOffTurn", "sleeveBloomOffTurnChoice", "beamSwap", "beamSwapChoice"),
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
            if (obj.has("sleeveBloomOffTurn")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloomOffTurn'")
            }
            if (obj.has("sleeveBloomOffTurnChoice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloomOffTurnChoice'")
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
        val sleeveBloomOffTurn = if (obj.has("sleeveBloomOffTurn") && !obj.get("sleeveBloomOffTurn").isJsonNull) {
            context.deserialize<ProtocolSleeveBloomOffTurnRequest>(obj.get("sleeveBloomOffTurn"), ProtocolSleeveBloomOffTurnRequest::class.java)
        } else null
        val sleeveBloomOffTurnChoice = if (obj.has("sleeveBloomOffTurnChoice") && !obj.get("sleeveBloomOffTurnChoice").isJsonNull) {
            context.deserialize<ProtocolSleeveBloomOffTurnChoiceRequest>(obj.get("sleeveBloomOffTurnChoice"), ProtocolSleeveBloomOffTurnChoiceRequest::class.java)
        } else null
        val beamSwap = if (obj.has("beamSwap") && !obj.get("beamSwap").isJsonNull) {
            context.deserialize<ProtocolBeamSwapRequest>(obj.get("beamSwap"), ProtocolBeamSwapRequest::class.java)
        } else null
        val beamSwapChoice = if (obj.has("beamSwapChoice") && !obj.get("beamSwapChoice").isJsonNull) {
            context.deserialize<ProtocolBeamSwapChoiceRequest>(obj.get("beamSwapChoice"), ProtocolBeamSwapChoiceRequest::class.java)
        } else null

        if (api == ApiTags.SUBMIT_SHUFFLE_PLAN) {
            if (shufflePlan == null) {
                throw JsonParseException("Missing shufflePlan payload in SUBMIT_SHUFFLE_PLAN request")
            }
            // 唯一幂等身份：信封 requestId 必须与专有载荷一致；可选信封 roundId 也不得另指一局。
            if (shufflePlan.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and shufflePlan (${shufflePlan.requestId})")
            }
            if (roundId != null && roundId != shufflePlan.roundId) {
                throw JsonParseException("roundId mismatch between envelope ($roundId) and shufflePlan (${shufflePlan.roundId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain choice payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain tileMark payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain sleeveBloom payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain sleeveBloomChoice payload")
            }
            if (sleeveBloomOffTurn != null || obj.has("sleeveBloomOffTurn")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain sleeveBloomOffTurn payload")
            }
            if (sleeveBloomOffTurnChoice != null || obj.has("sleeveBloomOffTurnChoice")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain sleeveBloomOffTurnChoice payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain beamSwap payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_SHUFFLE_PLAN cannot contain beamSwapChoice payload")
            }
        } else {
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("shufflePlan payload is only allowed for SUBMIT_SHUFFLE_PLAN")
            }
        }

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

        if (api == ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN) {
            if (sleeveBloomOffTurn == null) {
                throw JsonParseException("Missing sleeveBloomOffTurn payload in SUBMIT_SLEEVE_BLOOM_OFF_TURN request")
            }
            if (sleeveBloomOffTurn.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and sleeveBloomOffTurn (${sleeveBloomOffTurn.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain shufflePlan payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain tileMark payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain sleeveBloom payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain sleeveBloomChoice payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain beamSwap payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN cannot contain beamSwapChoice payload")
            }
        }

        if (api == ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE) {
            if (sleeveBloomOffTurnChoice == null) {
                throw JsonParseException("Missing sleeveBloomOffTurnChoice payload in SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE request")
            }
            if (sleeveBloomOffTurnChoice.requestId != requestId) {
                throw JsonParseException("requestId mismatch between envelope ($requestId) and sleeveBloomOffTurnChoice (${sleeveBloomOffTurnChoice.requestId})")
            }
            if (choice != null || obj.has("choice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain choice payload")
            }
            if (shufflePlan != null || obj.has("shufflePlan")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain shufflePlan payload")
            }
            if (tileMark != null || obj.has("tileMark")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain tileMark payload")
            }
            if (sleeveBloom != null || obj.has("sleeveBloom")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain sleeveBloom payload")
            }
            if (sleeveBloomChoice != null || obj.has("sleeveBloomChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain sleeveBloomChoice payload")
            }
            if (beamSwap != null || obj.has("beamSwap")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain beamSwap payload")
            }
            if (beamSwapChoice != null || obj.has("beamSwapChoice")) {
                throw JsonParseException("SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE cannot contain beamSwapChoice payload")
            }
        }

        if (api != ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN && (sleeveBloomOffTurn != null || obj.has("sleeveBloomOffTurn"))) {
            throw JsonParseException("sleeveBloomOffTurn payload is only allowed for SUBMIT_SLEEVE_BLOOM_OFF_TURN")
        }
        if (api != ApiTags.SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE && (sleeveBloomOffTurnChoice != null || obj.has("sleeveBloomOffTurnChoice"))) {
            throw JsonParseException("sleeveBloomOffTurnChoice payload is only allowed for SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE")
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

        return ApiRequest(api, requestId, roomId, token, playerName, preferSeat, config, choice, shufflePlan, cursor, roundId, protocolVersion, ready, roomConfig, spectatorId, spectatorName, publicCheckpoint, tileMark, sleeveBloom, sleeveBloomChoice, sleeveBloomOffTurn, sleeveBloomOffTurnChoice, beamSwap, beamSwapChoice)
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
 * V4 消息编解码器 (ApiMessageCodec)
 *
 * 采用独立 Gson 实例，不 import engine，对多态类型进行显式标签编解码和严格输入边界校验。
 */
object ApiMessageCodec {
    val gson: Gson = GsonBuilder()
        .registerTypeAdapter(ProtocolTile::class.java, ProtocolTileAdapter())
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
        .registerTypeAdapter(ProtocolYakuEntry::class.java, ProtocolYakuEntryAdapter())
        .registerTypeAdapter(ProtocolPaymentComponent::class.java, ProtocolPaymentComponentAdapter())
        .registerTypeAdapter(ProtocolPaymentPlan::class.java, ProtocolPaymentPlanAdapter())
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
        .registerTypeAdapter(ProtocolSleeveBloomOffTurnRequest::class.java, ProtocolSleeveBloomOffTurnRequestAdapter())
        .registerTypeAdapter(ProtocolSleeveBloomOffTurnChoiceRequest::class.java, ProtocolSleeveBloomOffTurnChoiceRequestAdapter())
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
            if (rootObj.has("sleeveBloomOffTurn")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloomOffTurn'")
            }
            if (rootObj.has("sleeveBloomOffTurnChoice")) {
                throw JsonParseException("Spectator request cannot contain player authority field 'sleeveBloomOffTurnChoice'")
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

        // 严格线路上校验 sleeveBloomOffTurn 边界
        if (rootObj.has("sleeveBloomOffTurn")) {
            val elem = rootObj.get("sleeveBloomOffTurn")
            if (elem.isJsonObject) {
                val obj = elem.asJsonObject
                if (!obj.has("controlEpoch")) {
                    throw JsonParseException("sleeveBloomOffTurn requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(obj.get("controlEpoch"), "sleeveBloomOffTurn")
                val roundIdElem = obj.get("roundId")
                if (roundIdElem == null || !roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                    throw JsonParseException("sleeveBloomOffTurn requires roundId at wire boundary")
                }
                val windowElem = obj.get("windowId")
                if (windowElem == null || !windowElem.isJsonPrimitive || windowElem.asString.isBlank()) {
                    throw JsonParseException("sleeveBloomOffTurn requires windowId at wire boundary")
                }
            } else if (!elem.isJsonNull) {
                throw JsonParseException("sleeveBloomOffTurn must be a JSON object")
            }
        }

        // 严格线路上校验 sleeveBloomOffTurnChoice 边界
        if (rootObj.has("sleeveBloomOffTurnChoice")) {
            val elem = rootObj.get("sleeveBloomOffTurnChoice")
            if (elem.isJsonObject) {
                val obj = elem.asJsonObject
                if (!obj.has("controlEpoch")) {
                    throw JsonParseException("sleeveBloomOffTurnChoice requires controlEpoch at wire boundary")
                }
                validateControlEpochElement(obj.get("controlEpoch"), "sleeveBloomOffTurnChoice")
                val roundIdElem = obj.get("roundId")
                if (roundIdElem == null || !roundIdElem.isJsonPrimitive || roundIdElem.asString.isBlank()) {
                    throw JsonParseException("sleeveBloomOffTurnChoice requires roundId at wire boundary")
                }
                val offerElem = obj.get("offerId")
                if (offerElem == null || !offerElem.isJsonPrimitive || offerElem.asString.isBlank()) {
                    throw JsonParseException("sleeveBloomOffTurnChoice requires offerId at wire boundary")
                }
            } else if (!elem.isJsonNull) {
                throw JsonParseException("sleeveBloomOffTurnChoice must be a JSON object")
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

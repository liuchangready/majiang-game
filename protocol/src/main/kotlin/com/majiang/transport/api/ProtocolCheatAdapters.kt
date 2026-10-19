package com.majiang.transport.api

import com.google.gson.JsonDeserializationContext
import com.google.gson.JsonDeserializer
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonSerializationContext
import com.google.gson.JsonSerializer
import java.lang.reflect.Type
import java.util.Collections
import java.util.LinkedHashMap

/**
 * ProtocolCheatEconomyConfig JsonDeserializer
 */
internal class ProtocolCheatEconomyConfigAdapter : JsonDeserializer<ProtocolCheatEconomyConfig> {
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
 * ProtocolTileMarkConfig TypeAdapter
 */
internal class ProtocolTileMarkConfigAdapter : JsonSerializer<ProtocolTileMarkConfig>, JsonDeserializer<ProtocolTileMarkConfig> {
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
internal class ProtocolTileMarkRequestAdapter : JsonSerializer<ProtocolTileMarkRequest>, JsonDeserializer<ProtocolTileMarkRequest> {
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
internal class ProtocolRiverPositionAdapter : JsonSerializer<ProtocolRiverPosition>, JsonDeserializer<ProtocolRiverPosition> {
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
internal class ProtocolMarkedTileViewAdapter : JsonSerializer<ProtocolMarkedTileView>, JsonDeserializer<ProtocolMarkedTileView> {
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
internal class ProtocolTileMarkGroupViewAdapter : JsonSerializer<ProtocolTileMarkGroupView>, JsonDeserializer<ProtocolTileMarkGroupView> {
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
internal class ProtocolTileMarkViewAdapter : JsonSerializer<ProtocolTileMarkView>, JsonDeserializer<ProtocolTileMarkView> {
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
internal class ProtocolSleeveBloomConfigAdapter : JsonSerializer<ProtocolSleeveBloomConfig>, JsonDeserializer<ProtocolSleeveBloomConfig> {
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
internal class ProtocolSleeveBloomRequestAdapter : JsonSerializer<ProtocolSleeveBloomRequest>, JsonDeserializer<ProtocolSleeveBloomRequest> {
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
internal class ProtocolSleeveBloomChoiceRequestAdapter : JsonSerializer<ProtocolSleeveBloomChoiceRequest>, JsonDeserializer<ProtocolSleeveBloomChoiceRequest> {
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
internal class ProtocolSleeveBloomOfferViewAdapter : JsonSerializer<ProtocolSleeveBloomOfferView>, JsonDeserializer<ProtocolSleeveBloomOfferView> {
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
internal class ProtocolSleeveBloomViewAdapter : JsonSerializer<ProtocolSleeveBloomView>, JsonDeserializer<ProtocolSleeveBloomView> {
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
internal class ProtocolBeamSwapSlotAdapter : JsonSerializer<ProtocolBeamSwapSlot>, JsonDeserializer<ProtocolBeamSwapSlot> {
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
internal class ProtocolBeamSwapConfigAdapter : JsonSerializer<ProtocolBeamSwapConfig>, JsonDeserializer<ProtocolBeamSwapConfig> {
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
internal class ProtocolBeamSwapRequestAdapter : JsonSerializer<ProtocolBeamSwapRequest>, JsonDeserializer<ProtocolBeamSwapRequest> {
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
internal class ProtocolBeamSwapChoiceRequestAdapter : JsonSerializer<ProtocolBeamSwapChoiceRequest>, JsonDeserializer<ProtocolBeamSwapChoiceRequest> {
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
internal class ProtocolBeamSwapCandidateViewAdapter : JsonSerializer<ProtocolBeamSwapCandidateView>, JsonDeserializer<ProtocolBeamSwapCandidateView> {
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
internal class ProtocolBeamSwapOfferViewAdapter : JsonSerializer<ProtocolBeamSwapOfferView>, JsonDeserializer<ProtocolBeamSwapOfferView> {
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
internal class ProtocolBeamSwapViewAdapter : JsonSerializer<ProtocolBeamSwapView>, JsonDeserializer<ProtocolBeamSwapView> {
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

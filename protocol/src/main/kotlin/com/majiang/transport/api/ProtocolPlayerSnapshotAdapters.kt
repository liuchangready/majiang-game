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
import java.util.LinkedHashSet

/**
 * ProtocolPlayerDecision JsonDeserializer (确保 options 不可变且必须字段校验)
 */
internal class ProtocolPlayerDecisionAdapter : JsonDeserializer<ProtocolPlayerDecision> {
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
internal class ProtocolClockAdapter : JsonDeserializer<ProtocolClock> {
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
internal class ProtocolSeatControlAdapter : JsonDeserializer<ProtocolSeatControl> {
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
internal class ProtocolScoreResultAdapter : JsonDeserializer<ProtocolScoreResult> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolScoreResult {
        if (!json.isJsonObject) throw JsonParseException("ProtocolScoreResult must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "fu", "han", "basePoints", "title", "isYakuman", "yakumanMultiplier",
                "rawBasePoints", "capReason", "formulaText", "rawBasePointsExact",
                ProtocolScoreCodes.FIELD_CAP_CODE
            ),
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
        val capCode = if (obj.has(ProtocolScoreCodes.FIELD_CAP_CODE) && !obj.get(ProtocolScoreCodes.FIELD_CAP_CODE).isJsonNull) {
            val code = validateStrictString(
                obj.get(ProtocolScoreCodes.FIELD_CAP_CODE),
                "ProtocolScoreResult.capCode"
            )
            if (code.isBlank()) throw JsonParseException("ProtocolScoreResult.capCode cannot be blank")
            if (code.length > ProtocolScoreCodes.MAX_CODE_LENGTH) {
                throw JsonParseException("ProtocolScoreResult.capCode too long: ${code.length}")
            }
            code
        } else null
        return ProtocolScoreResult(
            fu, han, basePoints, title, isYakuman, yakumanMultiplier,
            rawBasePoints, capReason, formulaText, rawBasePointsExact, capCode
        )
    }
}

/**
 * ProtocolFuLine JsonDeserializer
 */
internal class ProtocolFuLineAdapter : JsonDeserializer<ProtocolFuLine> {
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
internal class ProtocolFuritenStatusAdapter : JsonDeserializer<ProtocolFuritenStatus> {
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
internal class ProtocolActionReceiptAdapter : JsonDeserializer<ProtocolActionReceipt> {
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
internal class ProtocolAccessCapabilitiesAdapter : JsonDeserializer<ProtocolAccessCapabilities> {
    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): ProtocolAccessCapabilities {
        if (!json.isJsonObject) throw JsonParseException("ProtocolAccessCapabilities must be a JSON object")
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf(
                "protocolVersion", "supportedPlayerCounts", "remoteTransport", "persistence",
                "resumption", "tileMarkSupported", "sleeveBloomSupported", "beamSwapSupported",
                ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION
            ),
            "ProtocolAccessCapabilities"
        )
        val pv = if (obj.has("protocolVersion") && !obj.get("protocolVersion").isJsonNull) {
            val v = validateStrictInt(obj.get("protocolVersion"), "ProtocolAccessCapabilities.protocolVersion")
            if (v < 1) throw JsonParseException("protocolVersion must be >= 1: $v")
            v
        } else 5
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
        val scoreDetailVersion = if (obj.has(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION) &&
            !obj.get(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION).isJsonNull
        ) {
            val v = validateStrictInt(
                obj.get(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION),
                "ProtocolAccessCapabilities.scoreDetailVersion"
            )
            if (v !in 0..ProtocolScoreCodes.MAX_SCORE_DETAIL_VERSION) {
                throw JsonParseException("scoreDetailVersion out of range: $v")
            }
            v
        } else 0
        return ProtocolAccessCapabilities(
            pv, counts, remote, persist, resume,
            tileMarkSupported, sleeveBloomSupported, beamSwapSupported, scoreDetailVersion
        )
    }
}

/**
 * ProtocolActionOption JsonDeserializer
 */
internal class ProtocolActionOptionAdapter : JsonDeserializer<ProtocolActionOption> {
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
internal class ProtocolShufflePreparationAdapter : JsonDeserializer<ProtocolShufflePreparation> {
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
 * ProtocolPlayerView TypeAdapter
 */
internal class ProtocolPlayerViewAdapter : JsonDeserializer<ProtocolPlayerView> {
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
internal class ProtocolRiichiPreviewOptionAdapter : JsonSerializer<ProtocolRiichiPreviewOption>, JsonDeserializer<ProtocolRiichiPreviewOption> {
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
internal class ProtocolMeldPreviewOptionAdapter : JsonSerializer<ProtocolMeldPreviewOption>, JsonDeserializer<ProtocolMeldPreviewOption> {
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
internal class ProtocolPlayerPresentationPreviewAdapter : JsonSerializer<ProtocolPlayerPresentationPreview>, JsonDeserializer<ProtocolPlayerPresentationPreview> {
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
internal class ProtocolPlayerSnapshotAdapter : JsonDeserializer<ProtocolPlayerSnapshot> {
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
internal class ProtocolPlayerUpdateAdapter : JsonDeserializer<ProtocolPlayerUpdate> {
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

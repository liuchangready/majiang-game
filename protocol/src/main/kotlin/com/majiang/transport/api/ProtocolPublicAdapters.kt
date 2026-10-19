package com.majiang.transport.api

import com.google.gson.JsonDeserializationContext
import com.google.gson.JsonDeserializer
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonSerializationContext
import com.google.gson.JsonSerializer
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import java.lang.reflect.Type
import java.util.Collections
import java.util.LinkedHashSet

/**
 * ProtocolPublicRoundRecord JsonDeserializer (确保单局历史结算记录不可变且必须字段校验)
 */
internal class ProtocolPublicRoundRecordAdapter : JsonDeserializer<ProtocolPublicRoundRecord> {
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
internal class ProtocolPublicSessionResultAdapter : JsonDeserializer<ProtocolPublicSessionResult> {
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
internal class ProtocolSessionViewAdapter : JsonDeserializer<ProtocolSessionView> {
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
 * ProtocolRoundOutcome TypeAdapter (支持 6 种独立公开单局胜负类型)
 */
internal class ProtocolRoundOutcomeAdapter : JsonSerializer<ProtocolRoundOutcome>, JsonDeserializer<ProtocolRoundOutcome> {
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
internal class ProtocolPublicScoreRecordAdapter : JsonSerializer<ProtocolPublicScoreRecord>, JsonDeserializer<ProtocolPublicScoreRecord> {
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
internal class ProtocolPublicMatchResultAdapter : JsonSerializer<ProtocolPublicMatchResult>, JsonDeserializer<ProtocolPublicMatchResult> {
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
internal class ProtocolPublicSessionSummaryAdapter : JsonSerializer<ProtocolPublicSessionSummary>, JsonDeserializer<ProtocolPublicSessionSummary> {
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
internal class ProtocolPublicBoardViewAdapter : JsonSerializer<ProtocolPublicBoardView>, JsonDeserializer<ProtocolPublicBoardView> {
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
internal class ProtocolPublicSnapshotAdapter : JsonSerializer<ProtocolPublicSnapshot>, JsonDeserializer<ProtocolPublicSnapshot> {
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
internal class ProtocolPublicUpdateAdapter : JsonSerializer<ProtocolPublicUpdate>, JsonDeserializer<ProtocolPublicUpdate> {
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
internal class PublicCheckpointAdapter : JsonSerializer<PublicCheckpoint>, JsonDeserializer<PublicCheckpoint> {
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
internal class JoinSpectatorRequestAdapter : JsonSerializer<JoinSpectatorRequest>, JsonDeserializer<JoinSpectatorRequest> {
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
internal class ResumeSpectatorRequestAdapter : JsonSerializer<ResumeSpectatorRequest>, JsonDeserializer<ResumeSpectatorRequest> {
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
internal class SpectatorBindingAdapter : JsonSerializer<SpectatorBinding>, JsonDeserializer<SpectatorBinding> {
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

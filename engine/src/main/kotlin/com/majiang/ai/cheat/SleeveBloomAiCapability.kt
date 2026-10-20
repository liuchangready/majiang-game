package com.majiang.ai.cheat

import com.majiang.ai.AiCandidate
import com.majiang.ai.AiCandidateLimits
import com.majiang.ai.AiDecisionStamp
import com.majiang.ai.AiEffect
import com.majiang.ai.AiPhase
import com.majiang.ai.AiRisk
import com.majiang.ai.AiSeatSnapshot
import com.majiang.ai.AiVisibleParams
import com.majiang.authority.TableAuthority
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomKeep
import com.majiang.cheat.bloom.SleeveBloomOfferMode
import com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomOffTurnRequest
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.model.Tile

/**
 * 袖里藏花 AI 适配器。
 *
 * 支持两条路径：
 * - 本人摸牌后的即时激活与二选一；
 * - 本人弃牌后他家回合的预约激活与独立截止二选一。
 *
 * 激活与后续二选一是两个独立决策；激活 `APPLIED` 后由通用调度器在新快照上再次调度。
 * offer 出现后始终提供“保留第一张”的安全兜底，禁止直接摸切跳过 offer。
 */
class SleeveBloomAiCapability(
    private val authority: TableAuthority
) : CheatAiCapability {

    override val id: String = AiCapabilityIds.SLEEVE_BLOOM
    override val version: Int = 1

    override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
        id = id,
        version = version,
        phases = setOf(AiPhase.ORDINARY_ACTION, AiPhase.OFFER_CHOICE),
        supportsSubmit = true,
        maxCandidates = 4
    )

    override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> {
        val bloom = view.cheatView.sleeveBloom ?: return emptyList()
        return when (view.phase) {
            AiPhase.ORDINARY_ACTION -> activationCandidates(view, bloom, limits)
            AiPhase.OFFER_CHOICE -> choiceCandidates(view, bloom)
            else -> emptyList()
        }
    }

    private fun activationCandidates(
        view: AiSeatSnapshot,
        bloom: com.majiang.cheat.bloom.SleeveBloomView,
        limits: AiCandidateLimits
    ): List<AiCandidate> {
        val result = ArrayList<AiCandidate>(2)
        if (bloom.canApply && limits.canSpend(bloom.cost)) {
            result.add(
                AiCandidate(
                    optionId = "$id:activate",
                    capabilityId = id,
                    phase = AiPhase.ORDINARY_ACTION,
                    cost = bloom.cost,
                    risk = AiRisk(exposure = 0.2, suspicion = 0.15),
                    estimatedEffect = AiEffect(handValue = 9, note = "swap current draw with next wall"),
                    terminalForWindow = true,
                    parameters = AiVisibleParams.of("mode" to MODE_IMMEDIATE)
                )
            )
        }
        if (bloom.canApplyOffTurn && bloom.offTurnWindowId != null && limits.canSpend(bloom.cost)) {
            result.add(
                AiCandidate(
                    optionId = "$id:offturn",
                    capabilityId = id,
                    phase = AiPhase.ORDINARY_ACTION,
                    cost = bloom.cost,
                    risk = AiRisk(exposure = 0.25, suspicion = 0.2),
                    estimatedEffect = AiEffect(handValue = 7, note = "reserve an off-turn wall candidate"),
                    terminalForWindow = true,
                    parameters = AiVisibleParams.of(
                        "mode" to MODE_OFF_TURN,
                        "windowId" to bloom.offTurnWindowId
                    )
                )
            )
        }
        return result
    }

    private fun choiceCandidates(
        view: AiSeatSnapshot,
        bloom: com.majiang.cheat.bloom.SleeveBloomView
    ): List<AiCandidate> {
        val offer = bloom.pendingOffer ?: return emptyList()
        val hand = view.view.myClosedTiles
        val first = choiceCandidate(
            offer = offer,
            keep = SleeveBloomKeep.FIRST,
            tile = offer.firstTile,
            canTsumo = offer.firstCanTsumo,
            canKan = offer.firstCanKan,
            hand = hand,
            safeDefault = true
        )
        val second = choiceCandidate(
            offer = offer,
            keep = SleeveBloomKeep.SECOND,
            tile = offer.secondTile,
            canTsumo = offer.secondCanTsumo,
            canKan = offer.secondCanKan,
            hand = hand,
            safeDefault = false
        )
        return listOf(first, second)
    }

    private fun choiceCandidate(
        offer: com.majiang.cheat.bloom.SleeveBloomOfferView,
        keep: SleeveBloomKeep,
        tile: Tile,
        canTsumo: Boolean,
        canKan: Boolean,
        hand: List<Tile>,
        safeDefault: Boolean
    ): AiCandidate {
        var effect = 4 + AiCheatHeuristics.relativeValue(tile, hand) * 2
        if (canTsumo) effect += 100
        if (canKan) effect += 8
        val params = if (offer.mode == SleeveBloomOfferMode.OFF_TURN) {
            AiVisibleParams.of(
                "mode" to MODE_OFF_TURN,
                "offerId" to offer.offerId,
                "keep" to keep.name,
                "windowId" to (offer.windowId ?: "")
            )
        } else {
            AiVisibleParams.of(
                "mode" to MODE_IMMEDIATE,
                "offerId" to offer.offerId,
                "keep" to keep.name
            )
        }
        return AiCandidate(
            optionId = "$id:keep-${keep.name.lowercase()}",
            capabilityId = id,
            phase = AiPhase.OFFER_CHOICE,
            cost = 0,
            risk = AiRisk(),
            estimatedEffect = AiEffect(handValue = effect, note = "keep ${keep.name.lowercase()} bloom tile"),
            terminalForWindow = true,
            parameters = params,
            isSafeDefault = safeDefault
        )
    }

    override fun submit(
        seat: Int,
        selected: AiCandidate,
        stamp: AiDecisionStamp,
        now: Long
    ): TableAuthority.MutationResult {
        require(selected.capabilityId == id) { "capability mismatch: ${selected.capabilityId}" }
        val roundId = requireNotNull(stamp.roundId) { "sleeve bloom requires roundId" }
        val requestId = AiRequestId.of(id, stamp, selected.optionId)
        val mode = selected.parameters.string("mode")
        return when (selected.phase) {
            AiPhase.ORDINARY_ACTION -> when (mode) {
                MODE_IMMEDIATE -> {
                    val decisionId = requireNotNull(stamp.decisionId) { "sleeve bloom activation requires decisionId" }
                    authority.submitSleeveBloom(
                        seat,
                        SleeveBloomRequest(requestId, stamp.sessionId, roundId, decisionId, stamp.controlEpoch),
                        now,
                        allowAutomated = true
                    )
                }
                MODE_OFF_TURN -> {
                    val windowId = requireNotNull(selected.parameters.string("windowId")) {
                        "off-turn sleeve bloom activation requires windowId"
                    }
                    authority.submitSleeveBloomOffTurn(
                        seat,
                        SleeveBloomOffTurnRequest(requestId, stamp.sessionId, roundId, windowId, stamp.controlEpoch),
                        now,
                        allowAutomated = true
                    )
                }
                else -> error("unknown sleeve bloom activation mode: $mode")
            }
            AiPhase.OFFER_CHOICE -> {
                val offerId = requireNotNull(selected.parameters.string("offerId")) {
                    "sleeve bloom choice requires offerId"
                }
                val keep = parseKeep(selected.parameters.string("keep"))
                when (mode) {
                    MODE_IMMEDIATE -> {
                        val decisionId = requireNotNull(stamp.decisionId) {
                            "sleeve bloom immediate choice requires decisionId"
                        }
                        authority.submitSleeveBloomChoice(
                            seat,
                            SleeveBloomChoiceRequest(
                                requestId,
                                stamp.sessionId,
                                roundId,
                                decisionId,
                                stamp.controlEpoch,
                                offerId,
                                keep
                            ),
                            now,
                            allowAutomated = true
                        )
                    }
                    MODE_OFF_TURN -> authority.submitSleeveBloomOffTurnChoice(
                        seat,
                        SleeveBloomOffTurnChoiceRequest(
                            requestId,
                            stamp.sessionId,
                            roundId,
                            stamp.controlEpoch,
                            offerId,
                            keep
                        ),
                        now,
                        allowAutomated = true
                    )
                    else -> error("unknown sleeve bloom choice mode: $mode")
                }
            }
            else -> error("unsupported sleeve bloom phase: ${selected.phase}")
        }
    }

    private fun parseKeep(raw: String?): SleeveBloomKeep =
        SleeveBloomKeep.entries.firstOrNull { it.name == raw }
            ?: error("invalid sleeve bloom keep: $raw")

    companion object {
        const val MODE_IMMEDIATE = "immediate"
        const val MODE_OFF_TURN = "offturn"
    }
}

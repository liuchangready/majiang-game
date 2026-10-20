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
import com.majiang.cheat.beam.BeamSwapAction
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.beam.BeamSwapSlot
import com.majiang.model.Tile

/**
 * 偷梁换柱 AI 适配器。
 *
 * 激活与后续选择是两个独立决策。选择阶段只能使用 [com.majiang.cheat.beam.BeamSwapOfferView]
 * 暴露的槽位与“本人落汗命中的牌面”；未标记槽位的真实牌面不可见。
 * 无法确定收益时始终提供“保留原状”的安全兜底。
 */
class BeamSwapAiCapability(
    private val authority: TableAuthority
) : CheatAiCapability {

    override val id: String = AiCapabilityIds.BEAM_SWAP
    override val version: Int = 1

    override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
        id = id,
        version = version,
        phases = setOf(AiPhase.ORDINARY_ACTION, AiPhase.OFFER_CHOICE),
        supportsSubmit = true,
        maxCandidates = 4
    )

    override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> {
        val beam = view.cheatView.beamSwap ?: return emptyList()
        return when (view.phase) {
            AiPhase.ORDINARY_ACTION -> {
                if (!beam.canApply || !limits.canSpend(beam.cost)) {
                    emptyList()
                } else {
                    listOf(
                        AiCandidate(
                            optionId = "$id:activate",
                            capabilityId = id,
                            phase = AiPhase.ORDINARY_ACTION,
                            cost = beam.cost,
                            risk = AiRisk(exposure = 0.25, suspicion = 0.2),
                            estimatedEffect = AiEffect(handValue = 9, note = "exchange hand tile with wall"),
                            terminalForWindow = true,
                            parameters = AiVisibleParams.of("mode" to "activate")
                        )
                    )
                }
            }
            AiPhase.OFFER_CHOICE -> choiceCandidates(view, beam)
            else -> emptyList()
        }
    }

    private fun choiceCandidates(
        view: AiSeatSnapshot,
        beam: com.majiang.cheat.beam.BeamSwapView
    ): List<AiCandidate> {
        val offer = beam.pendingOffer ?: return emptyList()
        val handById = LinkedHashMap<Int, Tile>()
        for (tile in view.view.myClosedTiles) handById[tile.tileId] = tile
        val selectable = offer.selectableHandTileIds.mapNotNull { handById[it] }
        val hand = view.view.myClosedTiles

        val result = ArrayList<AiCandidate>(4)
        result.add(
            AiCandidate(
                optionId = "$id:keep",
                capabilityId = id,
                phase = AiPhase.OFFER_CHOICE,
                cost = 0,
                risk = AiRisk(),
                estimatedEffect = AiEffect(note = "keep original tile"),
                terminalForWindow = true,
                parameters = AiVisibleParams.of("mode" to "keep", "offerId" to offer.offerId),
                isSafeDefault = true
            )
        )

        if (selectable.isEmpty()) return result
        val source = AiCheatHeuristics.leastValuableHandTiles(selectable, 1).first()
        val knownTargets = offer.candidates
            .mapNotNull { candidate -> candidate.tile?.let { it to candidate.slot } }
            .sortedWith(
                compareByDescending<Pair<Tile, BeamSwapSlot>> {
                    AiCheatHeuristics.relativeValue(it.first, hand)
                }.thenBy { it.second.toString() }
            )
        for ((target, slot) in knownTargets.take(describe().maxCandidates - 1)) {
            val delta = AiCheatHeuristics.relativeValue(target, hand) -
                AiCheatHeuristics.relativeValue(source, hand)
            result.add(
                AiCandidate(
                    optionId = "$id:swap-${slotKey(slot)}-${source.tileId}",
                    capabilityId = id,
                    phase = AiPhase.OFFER_CHOICE,
                    cost = 0,
                    risk = AiRisk(exposure = 0.3, suspicion = 0.25),
                    estimatedEffect = AiEffect(
                        handValue = 6 + delta * 2,
                        note = "swap marked candidate"
                    ),
                    terminalForWindow = true,
                    parameters = AiVisibleParams.of(
                        "mode" to "swap",
                        "offerId" to offer.offerId,
                        "handTileId" to source.tileId.toString(),
                        "slotType" to slotType(slot),
                        "slotIndex" to slotIndex(slot).toString(),
                        "targetTileId" to target.tileId.toString()
                    )
                )
            )
        }
        return result
    }

    override fun submit(
        seat: Int,
        selected: AiCandidate,
        stamp: AiDecisionStamp,
        now: Long
    ): TableAuthority.MutationResult {
        require(selected.capabilityId == id) { "capability mismatch: ${selected.capabilityId}" }
        val roundId = requireNotNull(stamp.roundId) { "beam swap requires roundId" }
        val requestId = AiRequestId.of(id, stamp, selected.optionId)
        return when (selected.phase) {
            AiPhase.ORDINARY_ACTION -> {
                val decisionId = requireNotNull(stamp.decisionId) { "beam swap activation requires decisionId" }
                authority.submitBeamSwap(
                    seat,
                    BeamSwapRequest(requestId, stamp.sessionId, roundId, decisionId, stamp.controlEpoch),
                    now,
                    allowAutomated = true
                )
            }
            AiPhase.OFFER_CHOICE -> {
                val offerId = requireNotNull(selected.parameters.string("offerId")) {
                    "beam swap choice requires offerId"
                }
                when (selected.parameters.string("mode")) {
                    "keep" -> {
                        val decisionId = requireNotNull(stamp.decisionId) { "beam swap choice requires decisionId" }
                        authority.submitBeamSwapChoice(
                            seat,
                            BeamSwapChoiceRequest(
                                requestId,
                                stamp.sessionId,
                                roundId,
                                decisionId,
                                stamp.controlEpoch,
                                offerId,
                                BeamSwapAction.KEEP_ORIGINAL
                            ),
                            now,
                            allowAutomated = true
                        )
                    }
                    "swap" -> {
                        val decisionId = requireNotNull(stamp.decisionId) { "beam swap choice requires decisionId" }
                        val handTileId = requireNotNull(selected.parameters.int("handTileId")) {
                            "beam swap requires handTileId"
                        }
                        val slot = parseSlot(
                            requireNotNull(selected.parameters.string("slotType")) { "beam swap requires slotType" },
                            requireNotNull(selected.parameters.int("slotIndex")) { "beam swap requires slotIndex" }
                        )
                        authority.submitBeamSwapChoice(
                            seat,
                            BeamSwapChoiceRequest(
                                requestId,
                                stamp.sessionId,
                                roundId,
                                decisionId,
                                stamp.controlEpoch,
                                offerId,
                                BeamSwapAction.SWAP,
                                handTileId,
                                slot
                            ),
                            now,
                            allowAutomated = true
                        )
                    }
                    else -> error("unknown beam swap mode")
                }
            }
            else -> error("unsupported beam swap phase: ${selected.phase}")
        }
    }

    private fun slotType(slot: BeamSwapSlot): String = when (slot) {
        is BeamSwapSlot.WallSlot -> "wall"
        is BeamSwapSlot.DeadWallSlot -> "deadWall"
        is BeamSwapSlot.ReplacementSlot -> "replacement"
    }

    private fun slotIndex(slot: BeamSwapSlot): Int = when (slot) {
        is BeamSwapSlot.WallSlot -> slot.index
        is BeamSwapSlot.DeadWallSlot -> slot.index
        is BeamSwapSlot.ReplacementSlot -> slot.index
    }

    private fun slotKey(slot: BeamSwapSlot): String = "${slotType(slot)}-${slotIndex(slot)}"

    private fun parseSlot(type: String, index: Int): BeamSwapSlot = when (type) {
        "wall" -> BeamSwapSlot.WallSlot(index)
        "deadWall" -> BeamSwapSlot.DeadWallSlot(index)
        "replacement" -> BeamSwapSlot.ReplacementSlot(index)
        else -> error("unknown beam swap slot type: $type")
    }
}

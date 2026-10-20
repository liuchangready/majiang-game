package com.majiang.authority.cheat

import com.majiang.application.PlayerDecision
import com.majiang.cheat.beam.BeamSwapAction
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.cheat.beam.BeamSwapOffer
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.beam.BeamSwapSlot
import com.majiang.cheat.beam.BeamSwapWindow
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.model.Tile
import com.majiang.state.GamePhase
import com.majiang.state.SeatController
import com.majiang.timing.DecisionKey
import com.majiang.timing.DecisionTiming

/**
 * 偷梁换柱请求专用预检处理器（D-09）。
 *
 * 负责激活资格、候选充足性以及二选一时整份 offer 基线的漂移检查。
 * 不持有协调器、牌局状态、回执表或计时器；所有输入均为调用方派生的只读事实。
 */
internal class BeamSwapSubmissionHandler {

    data class NormalizedActivationPayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long
    )

    data class NormalizedChoicePayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long,
        val offerId: String,
        val action: BeamSwapAction,
        val handTileId: Int?,
        val targetSlot: BeamSwapSlot?
    )

    data class ActivationInput(
        val seat: Int,
        val request: BeamSwapRequest,
        val featureEnabled: Boolean,
        val economyEnabled: Boolean,
        val configuredCost: Int?,
        val closed: Boolean,
        val sessionClosed: Boolean,
        val paused: Boolean,
        val preparationActive: Boolean,
        val roundId: String?,
        val roundEnded: Boolean,
        val decision: PlayerDecision,
        val controller: SeatController,
        val timing: DecisionTiming?,
        val now: Long,
        val currentPlayer: Int?,
        val phase: GamePhase?,
        val responseWindowActive: Boolean,
        val drawnTileId: Int?,
        val drawnTileInHand: Boolean,
        val eligibleHandTileCount: Int,
        val lastDrawKind: SleeveBloomDrawKind,
        val riichiDeclared: Boolean,
        val sleeveBloomOfferActive: Boolean,
        val beamSwapOfferActive: Boolean,
        val sleeveBloomUsedOnTurn: Int,
        val beamSwapUsedOnTurn: Int,
        val turnNumber: Int,
        val wallRemaining: Int,
        val wallPointer: Int,
        val wallEndExclusive: Int,
        val deadWall: List<Tile>,
        val deadWallReplacements: List<Tile>,
        val deadWallTileIdsInDeadWall: Set<Int>,
        val doraIndicatorTileIds: Set<Int>,
        val rinshanPointer: Int,
        val cheatPoints: Int,
        val cost: Int,
        val allowAutomated: Boolean = false
    )

    data class ChoiceInput(
        val seat: Int,
        val request: BeamSwapChoiceRequest,
        val featureEnabled: Boolean,
        val economyEnabled: Boolean,
        val configuredCost: Int?,
        val closed: Boolean,
        val sessionClosed: Boolean,
        val paused: Boolean,
        val preparationActive: Boolean,
        val roundId: String?,
        val roundEnded: Boolean,
        val decision: PlayerDecision,
        val controller: SeatController,
        val timing: DecisionTiming?,
        val now: Long,
        val currentPlayer: Int?,
        val phase: GamePhase?,
        val responseWindowActive: Boolean,
        val offer: BeamSwapOffer?,
        val turnNumber: Int,
        val drawnTileId: Int?,
        val drawnTileInHand: Boolean,
        val lastDrawKind: SleeveBloomDrawKind,
        val currentEligibleHandTileIds: Set<Int>,
        val wall: List<Tile>,
        val wallPointer: Int,
        val wallEndExclusive: Int,
        val wallTileIdsInWall: Set<Int>,
        val deadWall: List<Tile>,
        val deadWallReplacements: List<Tile>,
        val deadWallTileIdsInDeadWall: Set<Int>,
        val doraIndicatorTileIds: Set<Int>,
        val rinshanPointer: Int,
        val allowAutomated: Boolean = false
    )

    fun normalizeActivation(request: BeamSwapRequest): NormalizedActivationPayload =
        NormalizedActivationPayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch
        )

    fun normalizeChoice(request: BeamSwapChoiceRequest): NormalizedChoicePayload =
        NormalizedChoicePayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch,
            offerId = request.offerId,
            action = request.action,
            handTileId = request.handTileId,
            targetSlot = request.targetSlot
        )

    fun validateActivation(input: ActivationInput): CheatSubmissionCheck {
        val gate = checkGate(
            featureEnabled = input.featureEnabled,
            economyEnabled = input.economyEnabled,
            configuredCost = input.configuredCost,
            closed = input.closed,
            sessionClosed = input.sessionClosed,
            paused = input.paused,
            preparationActive = input.preparationActive,
            roundId = input.roundId,
            roundEnded = input.roundEnded,
            requestRoundId = input.request.roundId,
            decision = input.decision,
            controller = input.controller,
            allowAutomated = input.allowAutomated,
            timing = input.timing,
            now = input.now,
            seat = input.seat,
            requestDecisionId = input.request.decisionId,
            requestControlEpoch = input.request.controlEpoch
        )
        if (gate != null) return gate

        if (input.currentPlayer != input.seat) return reject("NOT_YOUR_TURN")
        if (input.phase != GamePhase.CHEAT_WINDOW || input.responseWindowActive) return reject("WRONG_PHASE")

        val drawnTileId = input.drawnTileId
        if (drawnTileId == null || !input.drawnTileInHand) return reject("BEAM_SWAP_NO_DRAWN_TILE")
        if (input.eligibleHandTileCount == 0) return reject("BEAM_SWAP_NO_HAND_TILES")
        if (input.riichiDeclared) return reject("BEAM_SWAP_RIICHI_LOCKED")
        if (input.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            return reject("BEAM_SWAP_DEALER_EXTRA_EXCLUDED")
        }
        if (input.beamSwapOfferActive || input.sleeveBloomOfferActive) {
            return reject("BEAM_SWAP_ALREADY_PENDING")
        }
        if (input.beamSwapUsedOnTurn == input.turnNumber || input.sleeveBloomUsedOnTurn == input.turnNumber) {
            return reject("BEAM_SWAP_ALREADY_USED_THIS_DRAW")
        }

        when (input.lastDrawKind) {
            SleeveBloomDrawKind.NORMAL -> {
                if (input.wallRemaining < 1 || input.wallPointer >= input.wallEndExclusive) {
                    return reject("BEAM_SWAP_NO_CANDIDATES")
                }
            }
            SleeveBloomDrawKind.RINSHAN -> {
                var count = 0
                for (i in input.rinshanPointer..3) {
                    val tile = input.deadWall.getOrNull(i) ?: continue
                    if (tile.tileId in input.deadWallTileIdsInDeadWall) count++
                }
                for (i in 4..13) {
                    val tile = input.deadWall.getOrNull(i) ?: continue
                    if (tile.tileId in input.deadWallTileIdsInDeadWall &&
                        tile.tileId !in input.doraIndicatorTileIds
                    ) {
                        count++
                    }
                }
                for (tile in input.deadWallReplacements) {
                    if (tile.tileId in input.deadWallTileIdsInDeadWall) count++
                }
                if (count == 0) return reject("BEAM_SWAP_NO_CANDIDATES")
            }
            SleeveBloomDrawKind.DEAL_EXTRA -> return reject("ACTION_NOT_AVAILABLE")
        }

        if (input.cheatPoints < input.cost) return reject("INSUFFICIENT_FUNDS")
        return CheatSubmissionCheck.Allowed
    }

    fun validateChoice(input: ChoiceInput): CheatSubmissionCheck {
        val gate = checkGate(
            featureEnabled = input.featureEnabled,
            economyEnabled = input.economyEnabled,
            configuredCost = input.configuredCost,
            closed = input.closed,
            sessionClosed = input.sessionClosed,
            paused = input.paused,
            preparationActive = input.preparationActive,
            roundId = input.roundId,
            roundEnded = input.roundEnded,
            requestRoundId = input.request.roundId,
            decision = input.decision,
            controller = input.controller,
            allowAutomated = input.allowAutomated,
            timing = input.timing,
            now = input.now,
            seat = input.seat,
            requestDecisionId = input.request.decisionId,
            requestControlEpoch = input.request.controlEpoch
        )
        if (gate != null) return gate

        val offer = input.offer
        if (offer == null || offer.seat != input.seat) return reject("BEAM_SWAP_NO_PENDING_OFFER")
        if (offer.offerId != input.request.offerId) return reject("BEAM_SWAP_OFFER_MISMATCH")

        when (input.request.action) {
            BeamSwapAction.SWAP -> {
                val handTileId = input.request.handTileId
                val targetSlot = input.request.targetSlot
                if (handTileId == null ||
                    targetSlot == null ||
                    handTileId !in offer.eligibleHandTileIds ||
                    offer.candidates.none { it.slot == targetSlot }
                ) {
                    return reject("BEAM_SWAP_INVALID_CHOICE")
                }
            }
            BeamSwapAction.KEEP_ORIGINAL -> {
                if (input.request.handTileId != null || input.request.targetSlot != null) {
                    return reject("BEAM_SWAP_INVALID_CHOICE")
                }
            }
        }

        val isPhaseConsistent = input.currentPlayer == input.seat &&
                input.phase == GamePhase.CHEAT_WINDOW &&
                !input.responseWindowActive
        val isDrawKindConsistent = when (offer.window) {
            BeamSwapWindow.WALL -> input.lastDrawKind == SleeveBloomDrawKind.NORMAL
            BeamSwapWindow.DEAD_WALL -> input.lastDrawKind == SleeveBloomDrawKind.RINSHAN
        }
        val handConsistent = input.turnNumber == offer.createdTurnNumber &&
                input.drawnTileId != null &&
                input.drawnTileId == offer.drawnTileId &&
                input.drawnTileInHand &&
                input.currentEligibleHandTileIds == offer.eligibleHandTileIds.toSet()

        val candidatesConsistent = offer.candidates.all { candidate ->
            when (val slot = candidate.slot) {
                is BeamSwapSlot.WallSlot -> {
                    input.wallPointer <= slot.index &&
                            slot.index < input.wallEndExclusive &&
                            input.wall.getOrNull(slot.index)?.tileId == candidate.tileId &&
                            candidate.tileId in input.wallTileIdsInWall
                }
                is BeamSwapSlot.DeadWallSlot -> {
                    val tile = input.deadWall.getOrNull(slot.index)
                    tile != null &&
                            tile.tileId == candidate.tileId &&
                            tile.tileId in input.deadWallTileIdsInDeadWall &&
                            tile.tileId !in input.doraIndicatorTileIds &&
                            (slot.index in input.rinshanPointer..3 || slot.index in 4..13)
                }
                is BeamSwapSlot.ReplacementSlot -> {
                    val tile = input.deadWallReplacements.getOrNull(slot.index)
                    tile != null &&
                            tile.tileId == candidate.tileId &&
                            tile.tileId in input.deadWallTileIdsInDeadWall
                }
            }
        }

        return if (isPhaseConsistent && isDrawKindConsistent && handConsistent && candidatesConsistent) {
            CheatSubmissionCheck.Allowed
        } else {
            CheatSubmissionCheck.StaleOffer("BEAM_SWAP_STALE_OFFER")
        }
    }

    private fun checkGate(
        featureEnabled: Boolean,
        economyEnabled: Boolean,
        configuredCost: Int?,
        closed: Boolean,
        sessionClosed: Boolean,
        paused: Boolean,
        preparationActive: Boolean,
        roundId: String?,
        roundEnded: Boolean,
        requestRoundId: String,
        decision: PlayerDecision,
        controller: SeatController,
        allowAutomated: Boolean,
        timing: DecisionTiming?,
        now: Long,
        seat: Int,
        requestDecisionId: String,
        requestControlEpoch: Long
    ): CheatSubmissionCheck? {
        if (!featureEnabled || !economyEnabled || configuredCost != 10) return reject("BEAM_SWAP_DISABLED")
        if (closed || sessionClosed) return reject("SESSION_CLOSED")
        if (paused) return reject("HOST_PAUSED")
        if (preparationActive) return reject("WRONG_PHASE")

        if (roundId == null || roundEnded || requestRoundId != roundId) return reject("DECISION_MISMATCH")
        if (requestControlEpoch != decision.controlEpoch) return reject("STALE_CONTROL_EPOCH")
        if (requestDecisionId != decision.decisionId) return reject("DECISION_MISMATCH")

        if (!controller.isHumanOrAllowedAutomated(allowAutomated)) {
            return reject("ACTION_NOT_AVAILABLE")
        }

        if (timing == null || timing.key != DecisionKey(roundId, seat, requestDecisionId)) {
            return reject("NO_ACTIVE_DECISION")
        }
        if (now >= timing.deadlineMillis) return reject("DEADLINE_EXPIRED")
        return null
    }

    private fun SeatController.isHumanOrAllowedAutomated(allowAutomated: Boolean): Boolean =
        this is SeatController.LocalHuman ||
            this is SeatController.RemoteHuman ||
            (allowAutomated && (this is SeatController.Computer || this is SeatController.Takeover))

    private fun reject(reason: String): CheatSubmissionCheck.Rejected =
        CheatSubmissionCheck.Rejected(reason)
}

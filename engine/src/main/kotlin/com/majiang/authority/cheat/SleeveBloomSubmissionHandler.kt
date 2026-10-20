package com.majiang.authority.cheat

import com.majiang.application.PlayerDecision
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.state.GamePhase
import com.majiang.state.SeatController
import com.majiang.timing.DecisionKey
import com.majiang.timing.DecisionTiming

/**
 * 袖里藏花请求专用预检处理器（D-09）。
 *
 * 激活与二选一共享同一套作用域/代次/截止时间规则，但对候选的物理一致性检查不同。
 * 处理器保持无状态：不持有协调器、牌局、回执表或计时器。
 */
internal class SleeveBloomSubmissionHandler {

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
        val keep: com.majiang.cheat.bloom.SleeveBloomKeep
    )

    data class NormalizedOffTurnActivationPayload(
        val sessionId: String,
        val roundId: String,
        val windowId: String,
        val controlEpoch: Long
    )

    data class NormalizedOffTurnChoicePayload(
        val sessionId: String,
        val roundId: String,
        val controlEpoch: Long,
        val offerId: String,
        val keep: com.majiang.cheat.bloom.SleeveBloomKeep
    )

    data class ActivationInput(
        val seat: Int,
        val request: SleeveBloomRequest,
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
        val lastDrawKind: SleeveBloomDrawKind,
        val riichiDeclared: Boolean,
        val sleeveBloomOfferActive: Boolean,
        val offTurnOfferActive: Boolean,
        val beamSwapOfferActive: Boolean,
        val sleeveBloomUsedOnTurn: Int,
        val beamSwapUsedOnTurn: Int,
        val turnNumber: Int,
        val wallRemaining: Int,
        val wallPointer: Int,
        val wallEndExclusive: Int,
        val nextWallTileId: Int?,
        val nextWallTileInWall: Boolean,
        val rinshanPointer: Int,
        val totalKanCount: Int,
        val deadWallPreviousTileId: Int?,
        val nextDeadWallTileId: Int?,
        val nextDeadWallInDeadWall: Boolean,
        val cheatPoints: Int,
        val cost: Int
    )

    data class ChoiceInput(
        val seat: Int,
        val request: SleeveBloomChoiceRequest,
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
        val turnNumber: Int,
        val drawnTileId: Int?,
        val drawnTileInHand: Boolean,
        val offerSeat: Int?,
        val offerId: String?,
        val offerMode: com.majiang.cheat.bloom.SleeveBloomOfferMode?,
        val offerCreatedTurnNumber: Int?,
        val offerFirstTileId: Int?,
        val offerSecondTileId: Int?,
        val offerDrawKind: SleeveBloomDrawKind?,
        val offerSecondWallIndex: Int?,
        val nextAvailableWallIndex: Int?,
        val wallPointer: Int,
        val wallEndExclusive: Int,
        val wallTileAtOfferIndexInWall: Boolean,
        val rinshanPointer: Int,
        val totalKanCount: Int,
        val deadWallPreviousTileId: Int?,
        val deadWallSecondTileId: Int?,
        val deadWallSecondInDeadWall: Boolean
    )

    data class OffTurnActivationInput(
        val seat: Int,
        val request: com.majiang.cheat.bloom.SleeveBloomOffTurnRequest,
        val featureEnabled: Boolean,
        val economyEnabled: Boolean,
        val configuredCost: Int?,
        val closed: Boolean,
        val sessionClosed: Boolean,
        val paused: Boolean,
        val preparationActive: Boolean,
        val roundId: String?,
        val roundEnded: Boolean,
        val controller: SeatController,
        val controlEpoch: Long,
        val currentPlayer: Int?,
        val phase: GamePhase?,
        val windowMatches: Boolean,
        val immediateOfferActive: Boolean,
        val offTurnOfferActive: Boolean,
        val reservationActive: Boolean,
        val beamSwapOfferActive: Boolean,
        val availableWallCount: Int,
        val cheatPoints: Int,
        val cost: Int
    )

    data class OffTurnChoiceInput(
        val seat: Int,
        val request: com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest,
        val featureEnabled: Boolean,
        val economyEnabled: Boolean,
        val configuredCost: Int?,
        val closed: Boolean,
        val sessionClosed: Boolean,
        val paused: Boolean,
        val preparationActive: Boolean,
        val roundId: String?,
        val roundEnded: Boolean,
        val controller: SeatController,
        val controlEpoch: Long,
        val now: Long,
        val offerSeat: Int?,
        val offerId: String?,
        val offerDeadlineMillis: Long?,
        val offerFirstInWall: Boolean,
        val offerSecondInWall: Boolean
    )

    fun normalizeActivation(request: SleeveBloomRequest): NormalizedActivationPayload =
        NormalizedActivationPayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch
        )

    fun normalizeChoice(request: SleeveBloomChoiceRequest): NormalizedChoicePayload =
        NormalizedChoicePayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch,
            offerId = request.offerId,
            keep = request.keep
        )

    fun normalizeOffTurnActivation(
        request: com.majiang.cheat.bloom.SleeveBloomOffTurnRequest
    ): NormalizedOffTurnActivationPayload = NormalizedOffTurnActivationPayload(
        sessionId = request.sessionId,
        roundId = request.roundId,
        windowId = request.windowId,
        controlEpoch = request.controlEpoch
    )

    fun normalizeOffTurnChoice(
        request: com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest
    ): NormalizedOffTurnChoicePayload = NormalizedOffTurnChoicePayload(
        sessionId = request.sessionId,
        roundId = request.roundId,
        controlEpoch = request.controlEpoch,
        offerId = request.offerId,
        keep = request.keep
    )

    fun validateActivation(input: ActivationInput): CheatSubmissionCheck {
        val gate = checkGate(
            featureEnabled = input.featureEnabled,
            economyEnabled = input.economyEnabled,
            configuredCost = input.configuredCost,
            disabledReason = "SLEEVE_BLOOM_DISABLED",
            closed = input.closed,
            sessionClosed = input.sessionClosed,
            paused = input.paused,
            preparationActive = input.preparationActive,
            roundId = input.roundId,
            roundEnded = input.roundEnded,
            requestRoundId = input.request.roundId,
            decision = input.decision,
            controller = input.controller,
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
        if (drawnTileId == null || !input.drawnTileInHand) return reject("SLEEVE_BLOOM_NO_DRAWN_TILE")
        if (input.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            return reject("SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED")
        }
        if (input.sleeveBloomOfferActive) return reject("SLEEVE_BLOOM_ALREADY_PENDING")
        if (input.offTurnOfferActive) return reject("SLEEVE_BLOOM_ALREADY_PENDING")
        if (input.beamSwapOfferActive) return reject("BEAM_SWAP_ALREADY_PENDING")
        if (input.sleeveBloomUsedOnTurn == input.turnNumber || input.beamSwapUsedOnTurn == input.turnNumber) {
            return reject("SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW")
        }

        when (input.lastDrawKind) {
            SleeveBloomDrawKind.NORMAL -> {
                // 第二候选是“下一个可用槽位”，可能位于 wallPointer 之前的解锁洞；
                // 不能再用连续区间/指针判断候选是否存在。
                if (input.nextWallTileId == null || !input.nextWallTileInWall) {
                    return reject("SLEEVE_BLOOM_WALL_EMPTY")
                }
            }
            SleeveBloomDrawKind.RINSHAN -> {
                val rinshanValid = input.rinshanPointer in 1..3 &&
                        input.totalKanCount < 4 &&
                        input.deadWallPreviousTileId == drawnTileId &&
                        input.nextDeadWallTileId != null &&
                        input.nextDeadWallInDeadWall
                if (!rinshanValid) return reject("SLEEVE_BLOOM_WALL_EMPTY")
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
            disabledReason = "SLEEVE_BLOOM_DISABLED",
            closed = input.closed,
            sessionClosed = input.sessionClosed,
            paused = input.paused,
            preparationActive = input.preparationActive,
            roundId = input.roundId,
            roundEnded = input.roundEnded,
            requestRoundId = input.request.roundId,
            decision = input.decision,
            controller = input.controller,
            timing = input.timing,
            now = input.now,
            seat = input.seat,
            requestDecisionId = input.request.decisionId,
            requestControlEpoch = input.request.controlEpoch
        )
        if (gate != null) return gate

        if (input.currentPlayer != input.seat) return reject("NOT_YOUR_TURN")
        if (input.phase != GamePhase.CHEAT_WINDOW || input.responseWindowActive) return reject("WRONG_PHASE")

        val offerSeat = input.offerSeat
        if (offerSeat == null || offerSeat != input.seat) {
            return reject("SLEEVE_BLOOM_NO_PENDING_OFFER")
        }
        if (input.offerMode != com.majiang.cheat.bloom.SleeveBloomOfferMode.IMMEDIATE) {
            return reject("SLEEVE_BLOOM_NO_PENDING_OFFER")
        }
        if (input.offerId != input.request.offerId) {
            return reject("SLEEVE_BLOOM_OFFER_MISMATCH")
        }

        val isConsistent = input.turnNumber == input.offerCreatedTurnNumber &&
                input.drawnTileId == input.offerFirstTileId &&
                input.drawnTileInHand &&
                when (input.offerDrawKind) {
                    SleeveBloomDrawKind.NORMAL -> {
                        val index = input.offerSecondWallIndex
                        index != null &&
                                index == input.nextAvailableWallIndex &&
                                index in 0 until input.wallEndExclusive &&
                                input.wallTileAtOfferIndexInWall
                    }
                    SleeveBloomDrawKind.RINSHAN -> {
                        input.rinshanPointer in 1..3 &&
                                input.totalKanCount < 4 &&
                                input.deadWallPreviousTileId == input.offerFirstTileId &&
                                input.deadWallSecondTileId == input.offerSecondTileId &&
                                input.deadWallSecondInDeadWall
                    }
                    SleeveBloomDrawKind.DEAL_EXTRA, null -> false
                }

        return if (isConsistent) {
            CheatSubmissionCheck.Allowed
        } else {
            CheatSubmissionCheck.StaleOffer("SLEEVE_BLOOM_STALE_OFFER")
        }
    }

    fun validateOffTurnActivation(input: OffTurnActivationInput): CheatSubmissionCheck {
        if (!input.featureEnabled || !input.economyEnabled || input.configuredCost != 10) {
            return reject("SLEEVE_BLOOM_DISABLED")
        }
        if (input.closed || input.sessionClosed) return reject("SESSION_CLOSED")
        if (input.paused) return reject("HOST_PAUSED")
        if (input.preparationActive) return reject("WRONG_PHASE")
        if (input.roundId == null || input.roundEnded || input.request.roundId != input.roundId) {
            return reject("DECISION_MISMATCH")
        }
        if (input.request.controlEpoch != input.controlEpoch) return reject("STALE_CONTROL_EPOCH")
        if (input.controller !is SeatController.LocalHuman && input.controller !is SeatController.RemoteHuman) {
            return reject("ACTION_NOT_AVAILABLE")
        }
        if (!input.windowMatches) return reject("SLEEVE_BLOOM_NO_WINDOW")
        if (input.currentPlayer == input.seat) return reject("NOT_YOUR_TURN")
        if (input.phase !in setOf(
                GamePhase.DRAW,
                GamePhase.CHEAT_WINDOW,
                GamePhase.DISCARD,
                GamePhase.RESPONSE_WINDOW,
                GamePhase.CHANKAN_WINDOW
            )
        ) {
            return reject("WRONG_PHASE")
        }
        if (input.immediateOfferActive || input.offTurnOfferActive) return reject("SLEEVE_BLOOM_ALREADY_PENDING")
        if (input.reservationActive) return reject("SLEEVE_BLOOM_RESERVED_PENDING")
        if (input.beamSwapOfferActive) return reject("BEAM_SWAP_ALREADY_PENDING")
        if (input.availableWallCount < 2) return reject("SLEEVE_BLOOM_WALL_EMPTY")
        if (input.cheatPoints < input.cost) return reject("INSUFFICIENT_FUNDS")
        return CheatSubmissionCheck.Allowed
    }

    fun validateOffTurnChoice(input: OffTurnChoiceInput): CheatSubmissionCheck {
        if (!input.featureEnabled || !input.economyEnabled || input.configuredCost != 10) {
            return reject("SLEEVE_BLOOM_DISABLED")
        }
        if (input.closed || input.sessionClosed) return reject("SESSION_CLOSED")
        if (input.paused) return reject("HOST_PAUSED")
        if (input.preparationActive) return reject("WRONG_PHASE")
        if (input.roundId == null || input.roundEnded || input.request.roundId != input.roundId) {
            return reject("DECISION_MISMATCH")
        }
        if (input.request.controlEpoch != input.controlEpoch) return reject("STALE_CONTROL_EPOCH")
        if (input.controller !is SeatController.LocalHuman && input.controller !is SeatController.RemoteHuman) {
            return reject("ACTION_NOT_AVAILABLE")
        }
        if (input.offerSeat == null || input.offerSeat != input.seat) {
            return reject("SLEEVE_BLOOM_NO_PENDING_OFFER")
        }
        if (input.offerId != input.request.offerId) return reject("SLEEVE_BLOOM_OFFER_MISMATCH")
        val deadline = input.offerDeadlineMillis ?: return reject("SLEEVE_BLOOM_NO_PENDING_OFFER")
        if (input.now >= deadline) return reject("DEADLINE_EXPIRED")
        if (!input.offerFirstInWall || !input.offerSecondInWall) return reject("SLEEVE_BLOOM_STALE_OFFER")
        return CheatSubmissionCheck.Allowed
    }

    private fun checkGate(
        featureEnabled: Boolean,
        economyEnabled: Boolean,
        configuredCost: Int?,
        disabledReason: String,
        closed: Boolean,
        sessionClosed: Boolean,
        paused: Boolean,
        preparationActive: Boolean,
        roundId: String?,
        roundEnded: Boolean,
        requestRoundId: String,
        decision: PlayerDecision,
        controller: SeatController,
        timing: DecisionTiming?,
        now: Long,
        seat: Int,
        requestDecisionId: String,
        requestControlEpoch: Long
    ): CheatSubmissionCheck? {
        if (!featureEnabled || !economyEnabled || configuredCost != 10) return reject(disabledReason)
        if (closed || sessionClosed) return reject("SESSION_CLOSED")
        if (paused) return reject("HOST_PAUSED")
        if (preparationActive) return reject("WRONG_PHASE")

        if (roundId == null || roundEnded || requestRoundId != roundId) return reject("DECISION_MISMATCH")
        if (requestControlEpoch != decision.controlEpoch) return reject("STALE_CONTROL_EPOCH")
        if (requestDecisionId != decision.decisionId) return reject("DECISION_MISMATCH")

        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            return reject("ACTION_NOT_AVAILABLE")
        }

        if (timing == null || timing.key != DecisionKey(roundId, seat, requestDecisionId)) {
            return reject("NO_ACTIVE_DECISION")
        }
        if (now >= timing.deadlineMillis) return reject("DEADLINE_EXPIRED")
        return null
    }

    private fun reject(reason: String): CheatSubmissionCheck.Rejected =
        CheatSubmissionCheck.Rejected(reason)
}

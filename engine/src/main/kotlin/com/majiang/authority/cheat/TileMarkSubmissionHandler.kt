package com.majiang.authority.cheat

import com.majiang.application.PlayerDecision
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.state.GamePhase
import com.majiang.state.SeatController
import com.majiang.timing.DecisionKey
import com.majiang.timing.DecisionTiming

/**
 * 落汗请求专用预检处理器（D-09）。
 *
 * 只承担请求归一化与资格/候选检查；回执查重、配额、权威锁、计时 rekey 和事务提交
 * 仍由 [com.majiang.authority.TableAuthority] 的原有同步入口协调。处理器不持有
 * 协调器、牌局状态或回执表，只读取调用方传入的不可变事实。
 */
internal class TileMarkSubmissionHandler {

    data class NormalizedPayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long,
        val normalizedTileIds: List<Int>
    )

    data class Input(
        val seat: Int,
        val request: TileMarkRequest,
        val currentSessionId: String,
        val roundId: String?,
        val phase: GamePhase?,
        val responseWindowActive: Boolean,
        val preparationActive: Boolean,
        val currentPlayer: Int?,
        val decision: PlayerDecision,
        val controller: SeatController,
        val timing: DecisionTiming?,
        val now: Long,
        val riichiDeclared: Boolean,
        val sleeveBloomOfferSeat: Int?,
        val beamSwapOfferSeat: Int?,
        val handTileIds: Set<Int>,
        val markedTileIds: Set<Int>,
        val existingGroupCount: Int,
        val cheatPoints: Int,
        val cost: Int
    )

    fun normalize(request: TileMarkRequest): NormalizedPayload = NormalizedPayload(
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        normalizedTileIds = request.tileIds.sorted()
    )

    fun validate(input: Input): CheatSubmissionCheck {
        if (input.preparationActive) return reject("WRONG_PHASE")

        val roundId = input.roundId
        if (roundId == null ||
            input.phase == null ||
            input.phase == GamePhase.ROUND_END ||
            input.request.roundId != roundId ||
            input.request.sessionId != input.currentSessionId
        ) {
            return reject("DECISION_MISMATCH")
        }

        if (input.request.controlEpoch != input.decision.controlEpoch) return reject("STALE_CONTROL_EPOCH")
        if (input.request.decisionId != input.decision.decisionId) return reject("DECISION_MISMATCH")

        val controller = input.controller
        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            return reject("ACTION_NOT_AVAILABLE")
        }

        val timing = input.timing
        if (timing == null || timing.key != DecisionKey(roundId, input.seat, input.request.decisionId)) {
            return reject("NO_ACTIVE_DECISION")
        }
        if (input.now >= timing.deadlineMillis) return reject("DEADLINE_EXPIRED")

        if (input.currentPlayer != input.seat) return reject("NOT_YOUR_TURN")
        if (input.sleeveBloomOfferSeat == input.seat) return reject("SLEEVE_BLOOM_CHOICE_PENDING")
        if (input.beamSwapOfferSeat == input.seat) return reject("BEAM_SWAP_CHOICE_PENDING")

        if (input.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD) || input.responseWindowActive) {
            return reject("WRONG_PHASE")
        }
        if (input.riichiDeclared) return reject("TILE_MARK_RIICHI_LOCKED")

        val tileIds = input.request.tileIds
        if (tileIds.size != 4 || tileIds.distinct().size != 4 || tileIds.any { it !in 0..135 }) {
            return reject("TILE_MARK_INVALID_SELECTION")
        }
        if (!tileIds.all { it in input.handTileIds }) {
            return reject("TILE_MARK_TILE_NOT_OWNED")
        }
        if (tileIds.any { it in input.markedTileIds }) {
            return reject("TILE_MARK_ALREADY_MARKED")
        }
        if (input.existingGroupCount >= 3 || input.markedTileIds.size + 4 > 12) {
            return reject("TILE_MARK_CAPACITY_EXCEEDED")
        }
        if (input.cheatPoints < input.cost) {
            return reject("INSUFFICIENT_FUNDS")
        }
        return CheatSubmissionCheck.Allowed
    }

    private fun reject(reason: String): CheatSubmissionCheck = CheatSubmissionCheck.Rejected(reason)
}

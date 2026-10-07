package com.majiang.application

import com.majiang.match.MatchSession
import com.majiang.round.RoundChange
import com.majiang.round.RoundStateMachine
import com.majiang.state.GamePhase

/**
 * 单局结算结果验证器
 *
 * 外部审计组件，负责校验 RoundResult 的完整性、物理守恒律、初始账本匹配与版本对应关系。
 * MatchSession 自身不包含验证逻辑，完全依赖该验证器进行前置准入审核。
 */
object RoundResultValidator {

    /**
     * 验证单局终局结算结果与整场 Session 账本的匹配性、守恒律和版本一致性
     */
    fun validate(
        session: MatchSession,
        round: RoundStateMachine,
        change: RoundChange,
        consumedResultIds: Set<String>
    ) {
        val result = change.result ?: throw IllegalArgumentException("RoundChange must contain a RoundResult to validate")

        // 1. 同源与活跃局校验
        check(session.activeRound != null && session.activeRound === round && session.activeRound?.roundId == round.roundId) {
            "Round ${round.roundId} is not active in session (activeRound=${session.activeRound?.roundId})"
        }

        // 2. 结果标识与单局标识校验
        check(result.resultId.isNotBlank()) { "RoundResult resultId cannot be blank" }
        check(!consumedResultIds.contains(result.resultId)) { "RoundResult ${result.resultId} has already been consumed" }
        check(result.roundId == round.roundId) { "RoundResult roundId ${result.roundId} does not match active roundId ${round.roundId}" }
        check(change.roundId == round.roundId) { "RoundChange roundId ${change.roundId} does not match active roundId ${round.roundId}" }
        check(change.sourceVersion == round.version) { "RoundChange sourceVersion ${change.sourceVersion} does not match round version ${round.version}" }

        // 3. 候选终态与响应窗口关闭检查
        check(change.state.phase == GamePhase.ROUND_END) {
            "Candidate state phase must be ROUND_END when result is present, got ${change.state.phase}"
        }
        check(change.responseWindow == null) {
            "ResponseWindow must be null at ROUND_END"
        }

        // 4. 候选状态与结果的一致性校验
        check(change.state.scores.toList() == result.finalScores) {
            "Candidate state scores ${change.state.scores.toList()} does not match result finalScores ${result.finalScores}"
        }
        check(change.state.riichiSticks == result.remainingRiichiSticks) {
            "Candidate state riichiSticks ${change.state.riichiSticks} does not match result remainingRiichiSticks ${result.remainingRiichiSticks}"
        }

        // 5. 初始账本匹配（必须与本局开始时 Session 设立的账本一致）
        check(result.startingScores.size == 4) { "startingScores must have 4 elements, got ${result.startingScores.size}" }
        check(result.finalScores.size == 4) { "finalScores must have 4 elements, got ${result.finalScores.size}" }
        check(result.totalDeltas.size == 4) { "totalDeltas must have 4 elements, got ${result.totalDeltas.size}" }
        check(result.startingScores == session.betweenRoundScores) {
            "RoundResult startingScores ${result.startingScores} does not match session betweenRoundScores ${session.betweenRoundScores}"
        }
        check(result.startingRiichiSticks == session.betweenRoundRiichiSticks) {
            "RoundResult startingRiichiSticks ${result.startingRiichiSticks} does not match session betweenRoundRiichiSticks ${session.betweenRoundRiichiSticks}"
        }

        // 6. 结算差额封闭：finalScores = startingScores + totalDeltas
        for (i in 0 until 4) {
            val expectedFinal = result.startingScores[i] + result.totalDeltas[i]
            check(result.finalScores[i] == expectedFinal) {
                "Seat $i finalScore ${result.finalScores[i]} != startingScore ${result.startingScores[i]} + delta ${result.totalDeltas[i]}"
            }
        }

        // 7. 点数与供托守恒律 (Conservation)
        check(result.remainingRiichiSticks >= 0) { "remainingRiichiSticks cannot be negative: ${result.remainingRiichiSticks}" }
        val startingTotalPoints = result.startingScores.sum() + result.startingRiichiSticks * 1000
        val finalTotalPoints = result.finalScores.sum() + result.remainingRiichiSticks * 1000
        check(startingTotalPoints == finalTotalPoints) {
            "Point conservation violated: startingTotal $startingTotalPoints != finalTotal $finalTotalPoints"
        }

        // 8. 出千资源账本校验 (Cheat points verification: 基线匹配、候选余额一致、差额封闭、非负；不设总量守恒)
        check(result.startingCheatPoints.size == 4) {
            "startingCheatPoints must have 4 elements, got ${result.startingCheatPoints.size}"
        }
        check(result.finalCheatPoints.size == 4) {
            "finalCheatPoints must have 4 elements, got ${result.finalCheatPoints.size}"
        }
        check(result.cheatPointDeltas.size == 4) {
            "cheatPointDeltas must have 4 elements, got ${result.cheatPointDeltas.size}"
        }
        check(result.startingCheatPoints == session.betweenRoundCheatPoints) {
            "RoundResult startingCheatPoints ${result.startingCheatPoints} does not match session betweenRoundCheatPoints ${session.betweenRoundCheatPoints}"
        }
        check(change.state.cheatPoints.toList() == result.finalCheatPoints) {
            "Candidate state cheatPoints ${change.state.cheatPoints.toList()} does not match result finalCheatPoints ${result.finalCheatPoints}"
        }
        for (i in 0 until 4) {
            val expectedFinal = result.startingCheatPoints[i] + result.cheatPointDeltas[i]
            check(result.finalCheatPoints[i] == expectedFinal) {
                "Seat $i finalCheatPoints ${result.finalCheatPoints[i]} != startingCheatPoints ${result.startingCheatPoints[i]} + delta ${result.cheatPointDeltas[i]}"
            }
        }
        check(result.finalCheatPoints.all { it >= 0 }) {
            "Cheat points must be non-negative: ${result.finalCheatPoints}"
        }
    }
}

package com.majiang.application

import com.majiang.match.MatchSession
import com.majiang.match.ProgressionPlan
import com.majiang.match.SessionLifecycle
import com.majiang.round.RoundResult
import com.majiang.round.RoundStateMachine
import com.majiang.state.GamePhase

/**
 * 整场 Session 指令验证器
 *
 * 外部审计组件，校验 Session 指令的前置生命周期阶段、操作合法性与版本一致性。
 */
object SessionCommandValidator {

    fun validateStartSession(session: MatchSession, firstRound: RoundStateMachine) {
        check(session.lifecycle in setOf(SessionLifecycle.CREATED, SessionLifecycle.WAITING_START)) {
            "Cannot start session in lifecycle ${session.lifecycle}"
        }
        check(session.activeRound == null) {
            "Cannot start session when active round is already set"
        }
        check(firstRound.version == 0L) {
            "First round must be at version 0, got ${firstRound.version}"
        }
        check(firstRound.state.phase != GamePhase.MATCH_END) {
            "First round cannot be in MATCH_END phase, got ${firstRound.state.phase}"
        }
    }

    fun validateAcceptRoundResult(
        session: MatchSession,
        round: RoundStateMachine,
        result: RoundResult,
        progressionPlan: ProgressionPlan
    ) {
        check(session.lifecycle == SessionLifecycle.ROUND_ACTIVE) {
            "Cannot accept round result when session is in lifecycle ${session.lifecycle}"
        }
        check(session.activeRound != null && session.activeRound === round && session.activeRound?.roundId == round.roundId) {
            "Active round ${session.activeRound?.roundId} does not match roundId ${round.roundId}"
        }
        check(result.roundId == round.roundId) {
            "Result roundId ${result.roundId} does not match active roundId ${round.roundId}"
        }
    }

    fun validateChooseDealerEnd(session: MatchSession, dealerSeat: Int) {
        check(session.lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE) {
            "Cannot choose dealer end when session is in lifecycle ${session.lifecycle}"
        }
        check(dealerSeat == session.currentDealer) {
            "Seat $dealerSeat is not current dealer (${session.currentDealer})"
        }
        check(session.canDealerDeclareAgariYame) {
            "Dealer is not eligible to declare Agari-Yame"
        }
        check(session.pendingProgressionPlan != null) {
            "Pending progression plan is missing"
        }
    }

    fun validateStartNextRound(session: MatchSession, activeRound: RoundStateMachine?) {
        check(session.lifecycle in setOf(SessionLifecycle.WAITING_READY, SessionLifecycle.WAITING_DEALER_CHOICE)) {
            "Cannot start next round in lifecycle ${session.lifecycle}"
        }
        check(activeRound == null || activeRound.state.phase == GamePhase.ROUND_END || activeRound.state.phase == GamePhase.STACKING) {
            "Cannot start next round while active round is in phase ${activeRound?.state?.phase}"
        }
    }
}

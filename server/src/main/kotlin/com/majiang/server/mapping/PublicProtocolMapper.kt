package com.majiang.server.mapping

import com.majiang.match.RoundOutcome
import com.majiang.projection.PublicBoardView
import com.majiang.projection.PublicMatchResult
import com.majiang.projection.PublicScoreRecord
import com.majiang.projection.PublicSessionSummary
import com.majiang.projection.PublicTableProjection
import com.majiang.transport.api.ProtocolPublicBoardView
import com.majiang.transport.api.ProtocolPublicMatchResult
import com.majiang.transport.api.ProtocolPublicScoreRecord
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolRoundOutcome
import java.util.Collections

/**
 * 公开领域投影与传输协议 DTO 映射器 (PublicProtocolMapper)
 *
 * 负责将纯领域 PUBLIC 模型转换为独立协议模型，完全隔离领域层与网络传输层。
 */
object PublicProtocolMapper {

    fun toProtocolRoundOutcome(outcome: RoundOutcome?): ProtocolRoundOutcome? = when (outcome) {
        null -> null
        is RoundOutcome.DealerWin -> ProtocolRoundOutcome.DealerWin(outcome.winner)
        is RoundOutcome.NonDealerWin -> ProtocolRoundOutcome.NonDealerWin(outcome.winner)
        is RoundOutcome.Ron -> ProtocolRoundOutcome.Ron(outcome.winner, outcome.loser)
        is RoundOutcome.Tsumo -> ProtocolRoundOutcome.Tsumo(outcome.winner)
        is RoundOutcome.ExhaustiveDraw -> ProtocolRoundOutcome.ExhaustiveDraw(outcome.tenpaiSeats)
        is RoundOutcome.AbortiveDraw -> ProtocolRoundOutcome.AbortiveDraw(outcome.reason)
    }

    fun toProtocolPublicMatchResult(res: PublicMatchResult?): ProtocolPublicMatchResult? {
        if (res == null) return null
        return ProtocolPublicMatchResult(
            finalScores = res.finalScores,
            rankedSeats = res.rankedSeats,
            reason = res.reason,
            remainingRiichiSticksTransferredTo = res.remainingRiichiSticksTransferredTo
        )
    }

    fun toProtocolPublicScoreRecord(rec: PublicScoreRecord): ProtocolPublicScoreRecord =
        ProtocolPublicScoreRecord(
            roundId = rec.roundId,
            resultId = rec.resultId,
            finalScores = rec.finalScores,
            deltas = rec.deltas,
            remainingRiichiSticks = rec.remainingRiichiSticks,
            outcome = toProtocolRoundOutcome(rec.outcome)
        )

    fun toProtocolPublicBoardView(board: PublicBoardView): ProtocolPublicBoardView =
        ProtocolPublicBoardView(
            phase = EngineProtocolMapper.toProtocolPhase(board.phase),
            roundWind = board.roundWind,
            roundNumber = board.roundNumber,
            honbaSticks = board.honbaSticks,
            riichiSticks = board.riichiSticks,
            dealerSeat = board.dealerSeat,
            currentTurnPlayer = board.currentTurnPlayer,
            wallRemaining = board.wallRemaining,
            doraIndicators = EngineProtocolMapper.toProtocolTiles(board.doraIndicators),
            scores = board.scores,
            riichiStatus = board.riichiStatus,
            discards = board.discards.map { EngineProtocolMapper.toProtocolTiles(it) },
            melds = board.melds.map { playerMelds -> playerMelds.map { EngineProtocolMapper.toProtocolMeld(it) } },
            handTileCounts = board.handTileCounts,
            lastDiscard = board.lastDiscard?.let { EngineProtocolMapper.toProtocolTile(it) },
            lastDiscardPlayer = board.lastDiscardPlayer
        )

    fun toProtocolPublicSessionSummary(summary: PublicSessionSummary): ProtocolPublicSessionSummary =
        ProtocolPublicSessionSummary(
            lifecycle = summary.lifecycle,
            scores = summary.scores,
            roundWind = summary.roundWind,
            roundNumber = summary.roundNumber,
            dealerSeat = summary.dealerSeat,
            honba = summary.honba,
            roundRecords = summary.roundRecords.map { toProtocolPublicScoreRecord(it) },
            finalResult = toProtocolPublicMatchResult(summary.finalResult)
        )

    fun toProtocolPublicSnapshot(
        proj: PublicTableProjection,
        syncEpoch: String,
        cursor: Long
    ): ProtocolPublicSnapshot = ProtocolPublicSnapshot(
        sessionId = proj.sessionId,
        sessionVersion = proj.sessionVersion,
        roundId = proj.roundId,
        roundVersion = proj.roundVersion,
        syncEpoch = syncEpoch,
        cursor = cursor,
        board = proj.board?.let { toProtocolPublicBoardView(it) },
        session = toProtocolPublicSessionSummary(proj.session),
        clocks = Collections.unmodifiableList(proj.clocks.map { EngineProtocolMapper.toProtocolClock(it) }),
        paused = proj.paused,
        settlementRoundId = proj.settlementRoundId,
        settlementEvents = EngineProtocolMapper.toProtocolEvents(proj.settlementEvents)
    )
}

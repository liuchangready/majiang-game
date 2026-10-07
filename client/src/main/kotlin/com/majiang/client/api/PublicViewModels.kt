package com.majiang.client.api

import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * 客户端公开流检查点 (PublicCheckpoint)
 *
 * client.api 自有不可变值类型，用于独立观战流 (PUBLIC) 的状态恢复与对账基线确认。
 * 强制要求绑定完整 sessionId 与 syncEpoch 代次，裸游标禁止跨流或跨代次混用。
 * 禁止 typealias 协议类型。
 *
 * @property sessionId 对局会话标识（不可为空白）
 * @property syncEpoch 公开流代次（不可为空白）
 * @property cursor 客户端当前持有的单调递增游标（>= 0）
 */
data class PublicCheckpoint(
    val sessionId: String,
    val syncEpoch: String,
    val cursor: Long
) {
    init {
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(syncEpoch.isNotBlank()) { "syncEpoch cannot be blank" }
        require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
    }
}

/**
 * 客户端独立公开单局结果 (PublicRoundOutcomeView)
 *
 * 纯客户端视图模型，不泄露出千信息。
 */
sealed class PublicRoundOutcomeView(val outcomeType: String) {

    data class DealerWin(val winner: Int) : PublicRoundOutcomeView("DealerWin") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
        }
    }

    data class NonDealerWin(val winner: Int) : PublicRoundOutcomeView("NonDealerWin") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
        }
    }

    data class Ron(val winner: Int, val loser: Int) : PublicRoundOutcomeView("Ron") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
            require(loser in 0..3) { "loser must be in 0..3: $loser" }
        }
    }

    data class Tsumo(val winner: Int) : PublicRoundOutcomeView("Tsumo") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
        }
    }

    class ExhaustiveDraw(tenpaiSeats: Set<Int> = emptySet()) : PublicRoundOutcomeView("ExhaustiveDraw") {
        val tenpaiSeats: Set<Int> = Collections.unmodifiableSet(LinkedHashSet(tenpaiSeats))

        init {
            for (s in this.tenpaiSeats) {
                require(s in 0..3) { "tenpaiSeat must be in 0..3: $s" }
            }
        }

        operator fun component1(): Set<Int> = tenpaiSeats

        fun copy(tenpaiSeats: Set<Int> = this.tenpaiSeats): ExhaustiveDraw = ExhaustiveDraw(tenpaiSeats)

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is ExhaustiveDraw) return false
            return tenpaiSeats == other.tenpaiSeats
        }

        override fun hashCode(): Int = 31 * outcomeType.hashCode() + tenpaiSeats.hashCode()

        override fun toString(): String = "ExhaustiveDraw(tenpaiSeats=$tenpaiSeats)"
    }

    data class AbortiveDraw(val reason: String = "") : PublicRoundOutcomeView("AbortiveDraw")
}

/**
 * 客户端公开单局历史记分摘要 (PublicScoreRecordView)
 *
 * 保持公开分值、净胜差与局次结果，绝不泄露出千点数或私有流水。
 */
class PublicScoreRecordView @JvmOverloads constructor(
    val roundId: String,
    val resultId: String,
    finalScores: List<Int>,
    deltas: List<Int>,
    val remainingRiichiSticks: Int = 0,
    val outcome: PublicRoundOutcomeView? = null
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val deltas: List<Int> = Collections.unmodifiableList(ArrayList(deltas))

    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(resultId.isNotBlank()) { "resultId cannot be blank" }
        require(this.finalScores.size == 4) { "finalScores must have size 4: ${this.finalScores.size}" }
        require(this.deltas.size == 4) { "deltas must have size 4: ${this.deltas.size}" }
        require(remainingRiichiSticks >= 0) { "remainingRiichiSticks must be >= 0: $remainingRiichiSticks" }
    }

    operator fun component1(): String = roundId
    operator fun component2(): String = resultId
    operator fun component3(): List<Int> = finalScores
    operator fun component4(): List<Int> = deltas
    operator fun component5(): Int = remainingRiichiSticks
    operator fun component6(): PublicRoundOutcomeView? = outcome

    fun copy(
        roundId: String = this.roundId,
        resultId: String = this.resultId,
        finalScores: List<Int> = this.finalScores,
        deltas: List<Int> = this.deltas,
        remainingRiichiSticks: Int = this.remainingRiichiSticks,
        outcome: PublicRoundOutcomeView? = this.outcome
    ): PublicScoreRecordView = PublicScoreRecordView(roundId, resultId, finalScores, deltas, remainingRiichiSticks, outcome)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicScoreRecordView) return false
        return roundId == other.roundId &&
            resultId == other.resultId &&
            finalScores == other.finalScores &&
            deltas == other.deltas &&
            remainingRiichiSticks == other.remainingRiichiSticks &&
            outcome == other.outcome
    }

    override fun hashCode(): Int {
        var result = roundId.hashCode()
        result = 31 * result + resultId.hashCode()
        result = 31 * result + finalScores.hashCode()
        result = 31 * result + deltas.hashCode()
        result = 31 * result + remainingRiichiSticks.hashCode()
        result = 31 * result + (outcome?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "PublicScoreRecordView(roundId=$roundId, resultId=$resultId, finalScores=$finalScores, deltas=$deltas, remainingRiichiSticks=$remainingRiichiSticks, outcome=$outcome)"
}

val KNOWN_PUBLIC_LIFECYCLE_VIEWS: Set<String> = Collections.unmodifiableSet(linkedSetOf(
    "CREATED", "WAITING_START", "ROUND_ACTIVE", "ROUND_RESULT",
    "WAITING_DEALER_CHOICE", "WAITING_READY", "FINISHED", "PAUSED", "CLOSED"
))

val KNOWN_PUBLIC_FINISH_REASON_VIEWS: Set<String> = Collections.unmodifiableSet(linkedSetOf(
    "BANKRUPTCY", "NORMAL_COMPLETION", "WEST_COMPLETION", "AGARI_YAME"
))

/**
 * 客户端公开整场终局结果 (PublicMatchResultView)
 */
class PublicMatchResultView @JvmOverloads constructor(
    finalScores: List<Int>,
    rankedSeats: List<Int>,
    val reason: String,
    val remainingRiichiSticksTransferredTo: Int? = null
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val rankedSeats: List<Int> = Collections.unmodifiableList(ArrayList(rankedSeats))

    init {
        require(reason.isNotBlank()) { "reason cannot be blank" }
        require(reason in KNOWN_PUBLIC_FINISH_REASON_VIEWS) { "Unknown match finish reason: $reason" }
        require(this.finalScores.size == 4) { "finalScores must have size 4: ${this.finalScores.size}" }
        require(this.rankedSeats.size == 4) { "rankedSeats must have size 4: ${this.rankedSeats.size}" }
        require(this.rankedSeats.toSet() == setOf(0, 1, 2, 3)) { "rankedSeats must be a permutation of 0..3: $rankedSeats" }
        if (remainingRiichiSticksTransferredTo != null) {
            require(remainingRiichiSticksTransferredTo in 0..3) {
                "remainingRiichiSticksTransferredTo must be in 0..3: $remainingRiichiSticksTransferredTo"
            }
        }
    }

    operator fun component1(): List<Int> = finalScores
    operator fun component2(): List<Int> = rankedSeats
    operator fun component3(): String = reason
    operator fun component4(): Int? = remainingRiichiSticksTransferredTo

    fun copy(
        finalScores: List<Int> = this.finalScores,
        rankedSeats: List<Int> = this.rankedSeats,
        reason: String = this.reason,
        remainingRiichiSticksTransferredTo: Int? = this.remainingRiichiSticksTransferredTo
    ): PublicMatchResultView = PublicMatchResultView(finalScores, rankedSeats, reason, remainingRiichiSticksTransferredTo)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicMatchResultView) return false
        return finalScores == other.finalScores &&
            rankedSeats == other.rankedSeats &&
            reason == other.reason &&
            remainingRiichiSticksTransferredTo == other.remainingRiichiSticksTransferredTo
    }

    override fun hashCode(): Int {
        var result = finalScores.hashCode()
        result = 31 * result + rankedSeats.hashCode()
        result = 31 * result + reason.hashCode()
        result = 31 * result + (remainingRiichiSticksTransferredTo?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "PublicMatchResultView(finalScores=$finalScores, rankedSeats=$rankedSeats, reason=$reason, remainingRiichiSticksTransferredTo=$remainingRiichiSticksTransferredTo)"
}

/**
 * 客户端公开整场赛程概况 (PublicSessionSummaryView)
 */
class PublicSessionSummaryView @JvmOverloads constructor(
    val lifecycle: String,
    scores: List<Int>,
    val roundWind: Int,
    val roundNumber: Int,
    val dealerSeat: Int,
    val honba: Int,
    roundRecords: List<PublicScoreRecordView> = emptyList(),
    val finalResult: PublicMatchResultView? = null
) {
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val roundRecords: List<PublicScoreRecordView> = Collections.unmodifiableList(ArrayList(roundRecords))

    init {
        require(lifecycle.isNotBlank()) { "lifecycle cannot be blank" }
        require(lifecycle in KNOWN_PUBLIC_LIFECYCLE_VIEWS) { "Unknown session lifecycle: $lifecycle" }
        require(this.scores.size == 4) { "scores must have size 4: ${this.scores.size}" }
        require(roundWind in 1..4) { "roundWind must be in 1..4: $roundWind" }
        require(roundNumber >= 0) { "roundNumber must be >= 0: $roundNumber" }
        require(dealerSeat in 0..3) { "dealerSeat must be in 0..3: $dealerSeat" }
        require(honba >= 0) { "honba must be >= 0: $honba" }
    }

    operator fun component1(): String = lifecycle
    operator fun component2(): List<Int> = scores
    operator fun component3(): Int = roundWind
    operator fun component4(): Int = roundNumber
    operator fun component5(): Int = dealerSeat
    operator fun component6(): Int = honba
    operator fun component7(): List<PublicScoreRecordView> = roundRecords
    operator fun component8(): PublicMatchResultView? = finalResult

    fun copy(
        lifecycle: String = this.lifecycle,
        scores: List<Int> = this.scores,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        dealerSeat: Int = this.dealerSeat,
        honba: Int = this.honba,
        roundRecords: List<PublicScoreRecordView> = this.roundRecords,
        finalResult: PublicMatchResultView? = this.finalResult
    ): PublicSessionSummaryView = PublicSessionSummaryView(lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicSessionSummaryView) return false
        return lifecycle == other.lifecycle &&
            scores == other.scores &&
            roundWind == other.roundWind &&
            roundNumber == other.roundNumber &&
            dealerSeat == other.dealerSeat &&
            honba == other.honba &&
            roundRecords == other.roundRecords &&
            finalResult == other.finalResult
    }

    override fun hashCode(): Int {
        var result = lifecycle.hashCode()
        result = 31 * result + scores.hashCode()
        result = 31 * result + roundWind
        result = 31 * result + roundNumber
        result = 31 * result + dealerSeat
        result = 31 * result + honba
        result = 31 * result + roundRecords.hashCode()
        result = 31 * result + (finalResult?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "PublicSessionSummaryView(lifecycle=$lifecycle, scores=$scores, roundWind=$roundWind, roundNumber=$roundNumber, dealerSeat=$dealerSeat, honba=$honba, roundRecords=$roundRecords, finalResult=$finalResult)"
}

/**
 * 客户端牌桌纯公开视图 (PublicBoardView)
 *
 * 遵循严格信息边界：仅暴露四家暗牌张数 (handTileCounts)，绝不暴露手牌/摸牌实体、袖牌、未公开里宝、玩家决策。
 */
class PublicBoardView @JvmOverloads constructor(
    val phase: GamePhaseView,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val wallRemaining: Int,
    doraIndicators: List<TileView>,
    scores: List<Int>,
    riichiStatus: List<Boolean>,
    discards: List<List<TileView>>,
    melds: List<List<MeldView>>,
    handTileCounts: List<Int>,
    val lastDiscard: TileView? = null,
    val lastDiscardPlayer: Int? = null
) {
    val doraIndicators: List<TileView> = Collections.unmodifiableList(ArrayList(doraIndicators))
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val riichiStatus: List<Boolean> = Collections.unmodifiableList(ArrayList(riichiStatus))
    val discards: List<List<TileView>> = Collections.unmodifiableList(
        discards.map { Collections.unmodifiableList(ArrayList(it)) }
    )
    val melds: List<List<MeldView>> = Collections.unmodifiableList(
        melds.map { playerMelds ->
            Collections.unmodifiableList(ArrayList(playerMelds))
        }
    )
    val handTileCounts: List<Int> = Collections.unmodifiableList(ArrayList(handTileCounts))

    init {
        require(roundWind in 1..4) { "roundWind must be in 1..4: $roundWind" }
        require(roundNumber >= 0) { "roundNumber must be >= 0: $roundNumber" }
        require(dealerSeat in 0..3) { "dealerSeat must be in 0..3: $dealerSeat" }
        require(currentTurnPlayer in 0..3) { "currentTurnPlayer must be in 0..3: $currentTurnPlayer" }
        require(this.scores.size == 4) { "scores must have size 4: ${this.scores.size}" }
        require(this.riichiStatus.size == 4) { "riichiStatus must have size 4: ${this.riichiStatus.size}" }
        require(this.discards.size == 4) { "discards must have size 4: ${this.discards.size}" }
        require(this.melds.size == 4) { "melds must have size 4: ${this.melds.size}" }
        require(this.handTileCounts.size == 4) { "handTileCounts must have size 4: ${this.handTileCounts.size}" }
        require(this.handTileCounts.all { it >= 0 }) { "handTileCounts cannot be negative: $handTileCounts" }
        require(honbaSticks >= 0) { "honbaSticks must be >= 0: $honbaSticks" }
        require(riichiSticks >= 0) { "riichiSticks must be >= 0: $riichiSticks" }
        require(wallRemaining >= 0) { "wallRemaining must be >= 0: $wallRemaining" }
        if (lastDiscardPlayer != null) {
            require(lastDiscardPlayer in 0..3) { "lastDiscardPlayer must be in 0..3: $lastDiscardPlayer" }
        }
    }

    operator fun component1(): GamePhaseView = phase
    operator fun component2(): Int = roundWind
    operator fun component3(): Int = roundNumber
    operator fun component4(): Int = honbaSticks
    operator fun component5(): Int = riichiSticks
    operator fun component6(): Int = dealerSeat
    operator fun component7(): Int = currentTurnPlayer
    operator fun component8(): Int = wallRemaining
    operator fun component9(): List<TileView> = doraIndicators
    operator fun component10(): List<Int> = scores
    operator fun component11(): List<Boolean> = riichiStatus
    operator fun component12(): List<List<TileView>> = discards
    operator fun component13(): List<List<MeldView>> = melds
    operator fun component14(): List<Int> = handTileCounts
    operator fun component15(): TileView? = lastDiscard
    operator fun component16(): Int? = lastDiscardPlayer

    fun copy(
        phase: GamePhaseView = this.phase,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        honbaSticks: Int = this.honbaSticks,
        riichiSticks: Int = this.riichiSticks,
        dealerSeat: Int = this.dealerSeat,
        currentTurnPlayer: Int = this.currentTurnPlayer,
        wallRemaining: Int = this.wallRemaining,
        doraIndicators: List<TileView> = this.doraIndicators,
        scores: List<Int> = this.scores,
        riichiStatus: List<Boolean> = this.riichiStatus,
        discards: List<List<TileView>> = this.discards,
        melds: List<List<MeldView>> = this.melds,
        handTileCounts: List<Int> = this.handTileCounts,
        lastDiscard: TileView? = this.lastDiscard,
        lastDiscardPlayer: Int? = this.lastDiscardPlayer
    ): PublicBoardView = PublicBoardView(
        phase, roundWind, roundNumber, honbaSticks, riichiSticks, dealerSeat,
        currentTurnPlayer, wallRemaining, doraIndicators, scores, riichiStatus,
        discards, melds, handTileCounts, lastDiscard, lastDiscardPlayer
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicBoardView) return false
        return phase == other.phase &&
            roundWind == other.roundWind &&
            roundNumber == other.roundNumber &&
            honbaSticks == other.honbaSticks &&
            riichiSticks == other.riichiSticks &&
            dealerSeat == other.dealerSeat &&
            currentTurnPlayer == other.currentTurnPlayer &&
            wallRemaining == other.wallRemaining &&
            doraIndicators == other.doraIndicators &&
            scores == other.scores &&
            riichiStatus == other.riichiStatus &&
            discards == other.discards &&
            melds == other.melds &&
            handTileCounts == other.handTileCounts &&
            lastDiscard == other.lastDiscard &&
            lastDiscardPlayer == other.lastDiscardPlayer
    }

    override fun hashCode(): Int {
        var result = phase.hashCode()
        result = 31 * result + roundWind
        result = 31 * result + roundNumber
        result = 31 * result + honbaSticks
        result = 31 * result + riichiSticks
        result = 31 * result + dealerSeat
        result = 31 * result + currentTurnPlayer
        result = 31 * result + wallRemaining
        result = 31 * result + doraIndicators.hashCode()
        result = 31 * result + scores.hashCode()
        result = 31 * result + riichiStatus.hashCode()
        result = 31 * result + discards.hashCode()
        result = 31 * result + melds.hashCode()
        result = 31 * result + handTileCounts.hashCode()
        result = 31 * result + (lastDiscard?.hashCode() ?: 0)
        result = 31 * result + (lastDiscardPlayer?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "PublicBoardView(phase=$phase, roundWind=$roundWind, roundNumber=$roundNumber, honbaSticks=$honbaSticks, riichiSticks=$riichiSticks, dealerSeat=$dealerSeat, currentTurnPlayer=$currentTurnPlayer, wallRemaining=$wallRemaining, doraIndicators=$doraIndicators, scores=$scores, riichiStatus=$riichiStatus, discards=$discards, melds=$melds, handTileCounts=$handTileCounts, lastDiscard=$lastDiscard, lastDiscardPlayer=$lastDiscardPlayer)"
}

/**
 * 校验指定游戏事件是否符合公共/观战上下文白名单。
 *
 * 显式白名单穷尽放行已接受的公开事件，严格拒绝私密/千术/摸发牌/流水事件出现在公开数据包中。
 */
fun validatePublicGameEventView(event: GameEventView): GameEventView {
    return when (event) {
        is GameEventView.StackingStarted,
        is GameEventView.WallBuilt,
        is GameEventView.DealingComplete,
        is GameEventView.TileDiscarded,
        is GameEventView.ChiDeclared,
        is GameEventView.PonDeclared,
        is GameEventView.KanDeclared,
        is GameEventView.RiichiDeclared,
        is GameEventView.TsumoWin,
        is GameEventView.RonWin,
        is GameEventView.DoraRevealed,
        is GameEventView.BustedEvent,
        is GameEventView.DrawGame,
        is GameEventView.ScoreSettled,
        is GameEventView.RoundEnded,
        is GameEventView.MatchEnded -> event

        is GameEventView.TileDealt,
        is GameEventView.TileDrawn,
        is GameEventView.RinshanDraw,
        is GameEventView.CheatAttempted,
        is GameEventView.SuspicionChanged,
        is GameEventView.GazeShifted,
        is GameEventView.CheatPointsChanged -> {
            throw IllegalArgumentException("Private event '${event::class.simpleName}' is forbidden in PUBLIC context")
        }
    }
}

/**
 * 客户端公开快照视图模型 (PublicSnapshotView)
 *
 * 面向独立观战流与全量基线同步，绝不包含特定玩家席位、手牌、决策或千术凭据。
 */
class PublicSnapshotView @JvmOverloads constructor(
    val sessionId: String,
    val sessionVersion: Long,
    val roundId: String? = null,
    val roundVersion: Long? = null,
    val syncEpoch: String,
    val cursor: Long,
    val board: PublicBoardView? = null,
    val session: PublicSessionSummaryView,
    clocks: List<ClockInfo> = emptyList(),
    val paused: Boolean = false,
    val settlementRoundId: String? = null,
    settlementEvents: List<GameEventView> = emptyList()
) {
    val clocks: List<ClockInfo> = Collections.unmodifiableList(ArrayList(clocks))
    val settlementEvents: List<GameEventView> = Collections.unmodifiableList(ArrayList(settlementEvents))

    init {
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(syncEpoch.isNotBlank()) { "syncEpoch cannot be blank" }
        require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
        require(sessionVersion >= 0L) { "sessionVersion must be >= 0: $sessionVersion" }
        if (roundId != null) {
            require(roundId.isNotBlank()) { "roundId cannot be blank when present" }
        }
        if (roundVersion != null) {
            require(roundVersion >= 0L) { "roundVersion must be >= 0: $roundVersion" }
        }
        if (settlementRoundId != null) {
            require(settlementRoundId.isNotBlank()) { "settlementRoundId cannot be blank" }
        }
        for (ev in this.settlementEvents) {
            validatePublicGameEventView(ev)
        }
    }

    operator fun component1(): String = sessionId
    operator fun component2(): Long = sessionVersion
    operator fun component3(): String? = roundId
    operator fun component4(): Long? = roundVersion
    operator fun component5(): String = syncEpoch
    operator fun component6(): Long = cursor
    operator fun component7(): PublicBoardView? = board
    operator fun component8(): PublicSessionSummaryView = session
    operator fun component9(): List<ClockInfo> = clocks
    operator fun component10(): Boolean = paused
    operator fun component11(): String? = settlementRoundId
    operator fun component12(): List<GameEventView> = settlementEvents

    fun copy(
        sessionId: String = this.sessionId,
        sessionVersion: Long = this.sessionVersion,
        roundId: String? = this.roundId,
        roundVersion: Long? = this.roundVersion,
        syncEpoch: String = this.syncEpoch,
        cursor: Long = this.cursor,
        board: PublicBoardView? = this.board,
        session: PublicSessionSummaryView = this.session,
        clocks: List<ClockInfo> = this.clocks,
        paused: Boolean = this.paused,
        settlementRoundId: String? = this.settlementRoundId,
        settlementEvents: List<GameEventView> = this.settlementEvents
    ): PublicSnapshotView = PublicSnapshotView(
        sessionId, sessionVersion, roundId, roundVersion, syncEpoch, cursor,
        board, session, clocks, paused, settlementRoundId, settlementEvents
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicSnapshotView) return false
        return sessionId == other.sessionId &&
            sessionVersion == other.sessionVersion &&
            roundId == other.roundId &&
            roundVersion == other.roundVersion &&
            syncEpoch == other.syncEpoch &&
            cursor == other.cursor &&
            board == other.board &&
            session == other.session &&
            clocks == other.clocks &&
            paused == other.paused &&
            settlementRoundId == other.settlementRoundId &&
            settlementEvents == other.settlementEvents
    }

    override fun hashCode(): Int {
        var result = sessionId.hashCode()
        result = 31 * result + sessionVersion.hashCode()
        result = 31 * result + (roundId?.hashCode() ?: 0)
        result = 31 * result + (roundVersion?.hashCode() ?: 0)
        result = 31 * result + syncEpoch.hashCode()
        result = 31 * result + cursor.hashCode()
        result = 31 * result + (board?.hashCode() ?: 0)
        result = 31 * result + session.hashCode()
        result = 31 * result + clocks.hashCode()
        result = 31 * result + paused.hashCode()
        result = 31 * result + (settlementRoundId?.hashCode() ?: 0)
        result = 31 * result + settlementEvents.hashCode()
        return result
    }

    override fun toString(): String =
        "PublicSnapshotView(sessionId=$sessionId, sessionVersion=$sessionVersion, roundId=$roundId, syncEpoch=$syncEpoch, cursor=$cursor)"
}

/**
 * 客户端公开流增量推送视图模型 (PublicUpdateView)
 *
 * 严格契约：
 * 1. 普通连续更新：snapshot.cursor == fromCursor + 1 且 isFullSnapshot == false
 * 2. 全量基线重置：fromCursor == snapshot.cursor 且 isFullSnapshot == true
 * 3. fromCursor 必须 >= 0
 * 4. visibleEvents 严格受公开白名单限制，集合不可修改
 */
class PublicUpdateView(
    val snapshot: PublicSnapshotView,
    val fromCursor: Long,
    visibleEvents: List<GameEventView> = emptyList(),
    val isFullSnapshot: Boolean = false
) {
    val visibleEvents: List<GameEventView> = Collections.unmodifiableList(ArrayList(visibleEvents))

    init {
        require(fromCursor >= 0L) { "fromCursor must be >= 0: $fromCursor" }
        if (isFullSnapshot) {
            require(fromCursor == snapshot.cursor) {
                "For full snapshot baseline, fromCursor ($fromCursor) must equal snapshot.cursor (${snapshot.cursor})"
            }
        } else {
            require(snapshot.cursor == fromCursor + 1L) {
                "For regular update, snapshot.cursor (${snapshot.cursor}) must equal fromCursor + 1 (${fromCursor + 1L})"
            }
        }
        for (ev in this.visibleEvents) {
            validatePublicGameEventView(ev)
        }
    }

    operator fun component1(): PublicSnapshotView = snapshot
    operator fun component2(): Long = fromCursor
    operator fun component3(): List<GameEventView> = visibleEvents
    operator fun component4(): Boolean = isFullSnapshot

    fun copy(
        snapshot: PublicSnapshotView = this.snapshot,
        fromCursor: Long = this.fromCursor,
        visibleEvents: List<GameEventView> = this.visibleEvents,
        isFullSnapshot: Boolean = this.isFullSnapshot
    ): PublicUpdateView = PublicUpdateView(snapshot, fromCursor, visibleEvents, isFullSnapshot)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicUpdateView) return false
        return snapshot == other.snapshot &&
            fromCursor == other.fromCursor &&
            visibleEvents == other.visibleEvents &&
            isFullSnapshot == other.isFullSnapshot
    }

    override fun hashCode(): Int {
        var result = snapshot.hashCode()
        result = 31 * result + fromCursor.hashCode()
        result = 31 * result + visibleEvents.hashCode()
        result = 31 * result + isFullSnapshot.hashCode()
        return result
    }

    override fun toString(): String =
        "PublicUpdateView(snapshot=$snapshot, fromCursor=$fromCursor, visibleEvents=$visibleEvents, isFullSnapshot=$isFullSnapshot)"
}

package com.majiang.transport.api

import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * 传输层独立协议单局结果枚举/抽象类型 (ProtocolRoundOutcome)
 *
 * 镜像领域 RoundOutcome，不泄露出千信息，纯独立协议 DTO。
 */
sealed class ProtocolRoundOutcome(val outcomeType: String) {

    data class DealerWin(val winner: Int) : ProtocolRoundOutcome("DealerWin") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
        }
    }

    data class NonDealerWin(val winner: Int) : ProtocolRoundOutcome("NonDealerWin") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
        }
    }

    data class Ron(val winner: Int, val loser: Int) : ProtocolRoundOutcome("Ron") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
            require(loser in 0..3) { "loser must be in 0..3: $loser" }
        }
    }

    data class Tsumo(val winner: Int) : ProtocolRoundOutcome("Tsumo") {
        init {
            require(winner in 0..3) { "winner must be in 0..3: $winner" }
        }
    }

    class ExhaustiveDraw(tenpaiSeats: Set<Int> = emptySet()) : ProtocolRoundOutcome("ExhaustiveDraw") {
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

    data class AbortiveDraw(val reason: String = "") : ProtocolRoundOutcome("AbortiveDraw")
}

/**
 * 传输层独立协议公开单局历史记分摘要 (ProtocolPublicScoreRecord)
 *
 * 保持公开分值、净胜差与局次结果，绝不泄露出千点数或私有流水。
 */
class ProtocolPublicScoreRecord @JvmOverloads constructor(
    val roundId: String,
    val resultId: String,
    finalScores: List<Int>,
    deltas: List<Int>,
    val remainingRiichiSticks: Int = 0,
    val outcome: ProtocolRoundOutcome? = null
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
    operator fun component6(): ProtocolRoundOutcome? = outcome

    fun copy(
        roundId: String = this.roundId,
        resultId: String = this.resultId,
        finalScores: List<Int> = this.finalScores,
        deltas: List<Int> = this.deltas,
        remainingRiichiSticks: Int = this.remainingRiichiSticks,
        outcome: ProtocolRoundOutcome? = this.outcome
    ): ProtocolPublicScoreRecord = ProtocolPublicScoreRecord(roundId, resultId, finalScores, deltas, remainingRiichiSticks, outcome)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicScoreRecord) return false
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
        "ProtocolPublicScoreRecord(roundId=$roundId, resultId=$resultId, finalScores=$finalScores, deltas=$deltas, remainingRiichiSticks=$remainingRiichiSticks, outcome=$outcome)"
}

val KNOWN_PUBLIC_LIFECYCLES: Set<String> = Collections.unmodifiableSet(linkedSetOf(
    "CREATED", "WAITING_START", "ROUND_ACTIVE", "ROUND_RESULT",
    "WAITING_DEALER_CHOICE", "WAITING_READY", "FINISHED", "PAUSED", "CLOSED"
))

val KNOWN_PUBLIC_FINISH_REASONS: Set<String> = Collections.unmodifiableSet(linkedSetOf(
    "BANKRUPTCY", "NORMAL_COMPLETION", "WEST_COMPLETION", "AGARI_YAME"
))

/**
 * 传输层独立协议公开整场终局结果 (ProtocolPublicMatchResult)
 */
class ProtocolPublicMatchResult @JvmOverloads constructor(
    finalScores: List<Int>,
    rankedSeats: List<Int>,
    val reason: String,
    val remainingRiichiSticksTransferredTo: Int? = null
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val rankedSeats: List<Int> = Collections.unmodifiableList(ArrayList(rankedSeats))

    init {
        require(reason.isNotBlank()) { "reason cannot be blank" }
        require(reason in KNOWN_PUBLIC_FINISH_REASONS) { "Unknown match finish reason: $reason" }
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
    ): ProtocolPublicMatchResult = ProtocolPublicMatchResult(finalScores, rankedSeats, reason, remainingRiichiSticksTransferredTo)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicMatchResult) return false
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
        "ProtocolPublicMatchResult(finalScores=$finalScores, rankedSeats=$rankedSeats, reason=$reason, remainingRiichiSticksTransferredTo=$remainingRiichiSticksTransferredTo)"
}

/**
 * 传输层独立协议公开整场赛程概况 (ProtocolPublicSessionSummary)
 */
class ProtocolPublicSessionSummary @JvmOverloads constructor(
    val lifecycle: String,
    scores: List<Int>,
    val roundWind: Int,
    val roundNumber: Int,
    val dealerSeat: Int,
    val honba: Int,
    roundRecords: List<ProtocolPublicScoreRecord> = emptyList(),
    val finalResult: ProtocolPublicMatchResult? = null
) {
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val roundRecords: List<ProtocolPublicScoreRecord> = Collections.unmodifiableList(ArrayList(roundRecords))

    init {
        require(lifecycle.isNotBlank()) { "lifecycle cannot be blank" }
        require(lifecycle in KNOWN_PUBLIC_LIFECYCLES) { "Unknown session lifecycle: $lifecycle" }
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
    operator fun component7(): List<ProtocolPublicScoreRecord> = roundRecords
    operator fun component8(): ProtocolPublicMatchResult? = finalResult

    fun copy(
        lifecycle: String = this.lifecycle,
        scores: List<Int> = this.scores,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        dealerSeat: Int = this.dealerSeat,
        honba: Int = this.honba,
        roundRecords: List<ProtocolPublicScoreRecord> = this.roundRecords,
        finalResult: ProtocolPublicMatchResult? = this.finalResult
    ): ProtocolPublicSessionSummary = ProtocolPublicSessionSummary(lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicSessionSummary) return false
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
        "ProtocolPublicSessionSummary(lifecycle=$lifecycle, scores=$scores, roundWind=$roundWind, roundNumber=$roundNumber, dealerSeat=$dealerSeat, honba=$honba, roundRecords=$roundRecords, finalResult=$finalResult)"
}

/**
 * 传输层独立协议牌桌纯公开视图 (ProtocolPublicBoardView)
 *
 * 遵循严格信息边界：仅暴露四家暗牌张数 (handTileCounts)，绝不暴露手牌/摸牌实体、袖牌、未公开里宝、玩家决策。
 */
class ProtocolPublicBoardView @JvmOverloads constructor(
    val phase: ProtocolGamePhase,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val wallRemaining: Int,
    doraIndicators: List<ProtocolTile>,
    scores: List<Int>,
    riichiStatus: List<Boolean>,
    discards: List<List<ProtocolTile>>,
    melds: List<List<ProtocolMeld>>,
    handTileCounts: List<Int>,
    val lastDiscard: ProtocolTile? = null,
    val lastDiscardPlayer: Int? = null
) {
    val doraIndicators: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(doraIndicators))
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val riichiStatus: List<Boolean> = Collections.unmodifiableList(ArrayList(riichiStatus))
    val discards: List<List<ProtocolTile>> = Collections.unmodifiableList(
        discards.map { Collections.unmodifiableList(ArrayList(it)) }
    )
    val melds: List<List<ProtocolMeld>> = Collections.unmodifiableList(
        melds.map { playerMelds ->
            Collections.unmodifiableList(
                playerMelds.map { meld ->
                    meld.copy(tiles = Collections.unmodifiableList(ArrayList(meld.tiles)))
                }
            )
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

    operator fun component1(): ProtocolGamePhase = phase
    operator fun component2(): Int = roundWind
    operator fun component3(): Int = roundNumber
    operator fun component4(): Int = honbaSticks
    operator fun component5(): Int = riichiSticks
    operator fun component6(): Int = dealerSeat
    operator fun component7(): Int = currentTurnPlayer
    operator fun component8(): Int = wallRemaining
    operator fun component9(): List<ProtocolTile> = doraIndicators
    operator fun component10(): List<Int> = scores
    operator fun component11(): List<Boolean> = riichiStatus
    operator fun component12(): List<List<ProtocolTile>> = discards
    operator fun component13(): List<List<ProtocolMeld>> = melds
    operator fun component14(): List<Int> = handTileCounts
    operator fun component15(): ProtocolTile? = lastDiscard
    operator fun component16(): Int? = lastDiscardPlayer

    fun copy(
        phase: ProtocolGamePhase = this.phase,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        honbaSticks: Int = this.honbaSticks,
        riichiSticks: Int = this.riichiSticks,
        dealerSeat: Int = this.dealerSeat,
        currentTurnPlayer: Int = this.currentTurnPlayer,
        wallRemaining: Int = this.wallRemaining,
        doraIndicators: List<ProtocolTile> = this.doraIndicators,
        scores: List<Int> = this.scores,
        riichiStatus: List<Boolean> = this.riichiStatus,
        discards: List<List<ProtocolTile>> = this.discards,
        melds: List<List<ProtocolMeld>> = this.melds,
        handTileCounts: List<Int> = this.handTileCounts,
        lastDiscard: ProtocolTile? = this.lastDiscard,
        lastDiscardPlayer: Int? = this.lastDiscardPlayer
    ): ProtocolPublicBoardView = ProtocolPublicBoardView(
        phase, roundWind, roundNumber, honbaSticks, riichiSticks, dealerSeat,
        currentTurnPlayer, wallRemaining, doraIndicators, scores, riichiStatus,
        discards, melds, handTileCounts, lastDiscard, lastDiscardPlayer
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicBoardView) return false
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
        "ProtocolPublicBoardView(phase=$phase, roundWind=$roundWind, roundNumber=$roundNumber, honbaSticks=$honbaSticks, riichiSticks=$riichiSticks, dealerSeat=$dealerSeat, currentTurnPlayer=$currentTurnPlayer, wallRemaining=$wallRemaining, doraIndicators=$doraIndicators, scores=$scores, riichiStatus=$riichiStatus, discards=$discards, melds=$melds, handTileCounts=$handTileCounts, lastDiscard=$lastDiscard, lastDiscardPlayer=$lastDiscardPlayer)"
}

/**
 * 校验指定游戏事件是否符合公共/观战上下文白名单。
 *
 * 显式白名单穷尽放行已接受的公开事件，严格拒绝私密/千术/摸发牌/流水事件出现在公开数据包中。
 */
fun validatePublicGameEvent(event: ProtocolGameEvent): ProtocolGameEvent {
    return when (event) {
        is ProtocolGameEvent.StackingStarted,
        is ProtocolGameEvent.WallBuilt,
        is ProtocolGameEvent.DealingComplete,
        is ProtocolGameEvent.TileDiscarded,
        is ProtocolGameEvent.ChiDeclared,
        is ProtocolGameEvent.PonDeclared,
        is ProtocolGameEvent.KanDeclared,
        is ProtocolGameEvent.RiichiDeclared,
        is ProtocolGameEvent.TsumoWin,
        is ProtocolGameEvent.RonWin,
        is ProtocolGameEvent.DoraRevealed,
        is ProtocolGameEvent.DrawGame,
        is ProtocolGameEvent.ScoreSettled,
        is ProtocolGameEvent.RoundEnded,
        is ProtocolGameEvent.MatchEnded -> event

        is ProtocolGameEvent.TileDealt,
        is ProtocolGameEvent.TileDrawn,
        is ProtocolGameEvent.RinshanDraw,
        is ProtocolGameEvent.CheatPointsChanged -> {
            throw IllegalArgumentException("Private event '${event.eventType}' is forbidden in PUBLIC context")
        }
    }
}

/**
 * 传输层独立协议公开快照 DTO (ProtocolPublicSnapshot)
 *
 * 面向独立观战流与全量基线同步，绝不包含特定玩家席位、手牌、决策或千术凭据。
 */
class ProtocolPublicSnapshot @JvmOverloads constructor(
    val sessionId: String,
    val sessionVersion: Long,
    val roundId: String? = null,
    val roundVersion: Long? = null,
    val syncEpoch: String,
    val cursor: Long,
    val board: ProtocolPublicBoardView? = null,
    val session: ProtocolPublicSessionSummary,
    clocks: List<ProtocolClock> = emptyList(),
    val paused: Boolean = false,
    val settlementRoundId: String? = null,
    settlementEvents: List<ProtocolGameEvent> = emptyList()
) {
    val clocks: List<ProtocolClock> = Collections.unmodifiableList(ArrayList(clocks))
    val settlementEvents: List<ProtocolGameEvent> = Collections.unmodifiableList(ArrayList(settlementEvents))

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
            validatePublicGameEvent(ev)
        }
    }

    operator fun component1(): String = sessionId
    operator fun component2(): Long = sessionVersion
    operator fun component3(): String? = roundId
    operator fun component4(): Long? = roundVersion
    operator fun component5(): String = syncEpoch
    operator fun component6(): Long = cursor
    operator fun component7(): ProtocolPublicBoardView? = board
    operator fun component8(): ProtocolPublicSessionSummary = session
    operator fun component9(): List<ProtocolClock> = clocks
    operator fun component10(): Boolean = paused
    operator fun component11(): String? = settlementRoundId
    operator fun component12(): List<ProtocolGameEvent> = settlementEvents

    fun copy(
        sessionId: String = this.sessionId,
        sessionVersion: Long = this.sessionVersion,
        roundId: String? = this.roundId,
        roundVersion: Long? = this.roundVersion,
        syncEpoch: String = this.syncEpoch,
        cursor: Long = this.cursor,
        board: ProtocolPublicBoardView? = this.board,
        session: ProtocolPublicSessionSummary = this.session,
        clocks: List<ProtocolClock> = this.clocks,
        paused: Boolean = this.paused,
        settlementRoundId: String? = this.settlementRoundId,
        settlementEvents: List<ProtocolGameEvent> = this.settlementEvents
    ): ProtocolPublicSnapshot = ProtocolPublicSnapshot(
        sessionId, sessionVersion, roundId, roundVersion, syncEpoch, cursor,
        board, session, clocks, paused, settlementRoundId, settlementEvents
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicSnapshot) return false
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
        "ProtocolPublicSnapshot(sessionId=$sessionId, sessionVersion=$sessionVersion, roundId=$roundId, syncEpoch=$syncEpoch, cursor=$cursor)"
}

/**
 * 传输层独立协议公开流增量推送 DTO (ProtocolPublicUpdate)
 *
 * 严格契约：
 * 1. 普通连续更新：snapshot.cursor == fromCursor + 1 且 isFullSnapshot == false
 * 2. 全量基线重置：fromCursor == snapshot.cursor 且 isFullSnapshot == true
 * 3. fromCursor 必须 >= 0
 * 4. visibleEvents 严格受公开白名单限制，集合不可修改
 */
class ProtocolPublicUpdate(
    val snapshot: ProtocolPublicSnapshot,
    val fromCursor: Long,
    visibleEvents: List<ProtocolGameEvent> = emptyList(),
    val isFullSnapshot: Boolean = false
) {
    val visibleEvents: List<ProtocolGameEvent> = Collections.unmodifiableList(ArrayList(visibleEvents))

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
            validatePublicGameEvent(ev)
        }
    }

    operator fun component1(): ProtocolPublicSnapshot = snapshot
    operator fun component2(): Long = fromCursor
    operator fun component3(): List<ProtocolGameEvent> = visibleEvents
    operator fun component4(): Boolean = isFullSnapshot

    fun copy(
        snapshot: ProtocolPublicSnapshot = this.snapshot,
        fromCursor: Long = this.fromCursor,
        visibleEvents: List<ProtocolGameEvent> = this.visibleEvents,
        isFullSnapshot: Boolean = this.isFullSnapshot
    ): ProtocolPublicUpdate = ProtocolPublicUpdate(snapshot, fromCursor, visibleEvents, isFullSnapshot)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicUpdate) return false
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
        "ProtocolPublicUpdate(snapshot=$snapshot, fromCursor=$fromCursor, visibleEvents=$visibleEvents, isFullSnapshot=$isFullSnapshot)"
}

/**
 * 传输层 PUSH 信封分类器目标类型
 */
enum class PushType {
    PLAYER,
    ROOM,
    PUBLIC
}

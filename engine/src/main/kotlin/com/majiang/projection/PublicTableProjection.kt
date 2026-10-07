package com.majiang.projection

import com.majiang.access.ClockView
import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.event.snapshotScoreBreakdown
import com.majiang.match.RoundOutcome
import com.majiang.model.Meld
import com.majiang.model.Tile
import com.majiang.rules.ScoreBreakdown
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import java.util.ArrayList
import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet
import java.util.NoSuchElementException

/**
 * 递归深度脱敏并防御性复制 [RoundOutcome] 嵌套集合。
 *
 * 针对 [RoundOutcome.ExhaustiveDraw] 的 tenpaiSeats 建立不可修改防御性拷贝，
 * 防止来自可变集合的引用污染。
 */
fun sanitizeOutcome(outcome: RoundOutcome?): RoundOutcome? = when (outcome) {
    is RoundOutcome.ExhaustiveDraw -> {
        outcome.copy(
            tenpaiSeats = Collections.unmodifiableSet(LinkedHashSet(outcome.tenpaiSeats))
        )
    }
    else -> outcome
}

/**
 * 比较两个游戏事件的值语义。
 *
 * 解决领域层 [GameEvent.ScoreSettled.newScores] 与 [GameEvent.MatchEnded.finalScores]
 * 作为 [IntArray] 原生数组在数据类默认 equals 中仅做引用比较的问题。
 */
internal fun eventValueEquals(a: GameEvent, b: GameEvent): Boolean {
    if (a === b) return true
    if (a::class != b::class) return false
    return when (a) {
        is GameEvent.ScoreSettled -> {
            val bSettled = b as GameEvent.ScoreSettled
            a.turnNumber == bSettled.turnNumber &&
                    a.payments == bSettled.payments &&
                    a.newScores.contentEquals(bSettled.newScores)
        }
        is GameEvent.MatchEnded -> {
            val bEnded = b as GameEvent.MatchEnded
            a.turnNumber == bEnded.turnNumber &&
                    a.winner == bEnded.winner &&
                    a.finalScores.contentEquals(bEnded.finalScores)
        }
        else -> a == b
    }
}

internal fun eventValueHashCode(event: GameEvent): Int {
    return when (event) {
        is GameEvent.ScoreSettled -> 31 * (31 * event.turnNumber + event.payments.hashCode()) + event.newScores.contentHashCode()
        is GameEvent.MatchEnded -> 31 * (31 * event.turnNumber + event.finalScores.contentHashCode()) + event.winner
        else -> event.hashCode()
    }
}

/**
 * 公开单局历史记分摘要 (PublicScoreRecord)
 *
 * 严格保留局次结果、公开分值与净胜差，杜绝泄露出千点数余额或出千流水明细。
 * 集合在构造时建立不可变防御性副本，对外只读不可篡改。
 */
class PublicScoreRecord @JvmOverloads constructor(
    val roundId: String,
    val resultId: String,
    finalScores: List<Int>,
    deltas: List<Int>,
    val remainingRiichiSticks: Int = 0,
    outcome: RoundOutcome? = null
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val deltas: List<Int> = Collections.unmodifiableList(ArrayList(deltas))
    val outcome: RoundOutcome? = sanitizeOutcome(outcome)

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
    operator fun component6(): RoundOutcome? = outcome

    fun copy(
        roundId: String = this.roundId,
        resultId: String = this.resultId,
        finalScores: List<Int> = this.finalScores,
        deltas: List<Int> = this.deltas,
        remainingRiichiSticks: Int = this.remainingRiichiSticks,
        outcome: RoundOutcome? = this.outcome
    ): PublicScoreRecord = PublicScoreRecord(roundId, resultId, finalScores, deltas, remainingRiichiSticks, outcome)

    fun deepCopy(): PublicScoreRecord = PublicScoreRecord(roundId, resultId, finalScores, deltas, remainingRiichiSticks, outcome)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicScoreRecord) return false
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
        "PublicScoreRecord(roundId=$roundId, resultId=$resultId, finalScores=$finalScores, deltas=$deltas, remainingRiichiSticks=$remainingRiichiSticks, outcome=$outcome)"

    companion object {
        fun create(
            roundId: String,
            resultId: String,
            finalScores: List<Int>,
            deltas: List<Int>,
            remainingRiichiSticks: Int = 0,
            outcome: RoundOutcome? = null
        ): PublicScoreRecord = PublicScoreRecord(roundId, resultId, finalScores, deltas, remainingRiichiSticks, outcome)
    }
}

/**
 * 公开整场终局结果 (PublicMatchResult)
 *
 * 仅包含最终各家得分、排名次序与终局原因，不泄露整场出千余额与出千总结。
 */
class PublicMatchResult @JvmOverloads constructor(
    finalScores: List<Int>,
    rankedSeats: List<Int>,
    val reason: String,
    val remainingRiichiSticksTransferredTo: Int? = null
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val rankedSeats: List<Int> = Collections.unmodifiableList(ArrayList(rankedSeats))

    init {
        require(reason.isNotBlank()) { "reason cannot be blank" }
        require(this.finalScores.size == 4) { "finalScores must have size 4: ${this.finalScores.size}" }
        require(this.rankedSeats.size == 4) { "rankedSeats must have size 4: ${this.rankedSeats.size}" }
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
    ): PublicMatchResult = PublicMatchResult(finalScores, rankedSeats, reason, remainingRiichiSticksTransferredTo)

    fun deepCopy(): PublicMatchResult = PublicMatchResult(finalScores, rankedSeats, reason, remainingRiichiSticksTransferredTo)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicMatchResult) return false
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
        "PublicMatchResult(finalScores=$finalScores, rankedSeats=$rankedSeats, reason=$reason, remainingRiichiSticksTransferredTo=$remainingRiichiSticksTransferredTo)"

    companion object {
        fun create(
            finalScores: List<Int>,
            rankedSeats: List<Int>,
            reason: String,
            remainingRiichiSticksTransferredTo: Int? = null
        ): PublicMatchResult = PublicMatchResult(finalScores, rankedSeats, reason, remainingRiichiSticksTransferredTo)
    }
}

/**
 * 公开整场赛程概况 (PublicSessionSummary)
 *
 * 汇集生命周期、公开比分、风向局次、公开单局历史与终局结果。
 */
class PublicSessionSummary @JvmOverloads constructor(
    val lifecycle: String,
    scores: List<Int>,
    val roundWind: Int,
    val roundNumber: Int,
    val dealerSeat: Int,
    val honba: Int,
    roundRecords: List<PublicScoreRecord> = emptyList(),
    val finalResult: PublicMatchResult? = null
) {
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val roundRecords: List<PublicScoreRecord> = Collections.unmodifiableList(
        ArrayList(roundRecords.map { it.deepCopy() })
    )

    init {
        require(lifecycle.isNotBlank()) { "lifecycle cannot be blank" }
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
    operator fun component7(): List<PublicScoreRecord> = roundRecords
    operator fun component8(): PublicMatchResult? = finalResult

    fun copy(
        lifecycle: String = this.lifecycle,
        scores: List<Int> = this.scores,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        dealerSeat: Int = this.dealerSeat,
        honba: Int = this.honba,
        roundRecords: List<PublicScoreRecord> = this.roundRecords,
        finalResult: PublicMatchResult? = this.finalResult
    ): PublicSessionSummary = PublicSessionSummary(lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult)

    fun deepCopy(): PublicSessionSummary = PublicSessionSummary(
        lifecycle = lifecycle,
        scores = scores,
        roundWind = roundWind,
        roundNumber = roundNumber,
        dealerSeat = dealerSeat,
        honba = honba,
        roundRecords = roundRecords.map { it.deepCopy() },
        finalResult = finalResult?.deepCopy()
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicSessionSummary) return false
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
        "PublicSessionSummary(lifecycle=$lifecycle, scores=$scores, roundWind=$roundWind, roundNumber=$roundNumber, dealerSeat=$dealerSeat, honba=$honba, roundRecords=$roundRecords, finalResult=$finalResult)"

    companion object {
        fun create(
            lifecycle: String,
            scores: List<Int>,
            roundWind: Int,
            roundNumber: Int,
            dealerSeat: Int,
            honba: Int,
            roundRecords: List<PublicScoreRecord> = emptyList(),
            finalResult: PublicMatchResult? = null
        ): PublicSessionSummary = PublicSessionSummary(lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult)
    }
}

/**
 * 牌桌纯公开视图 (PublicBoardView)
 *
 * 遵循严格信息边界：
 * 1. 仅暴露四家暗牌张数（handTileCounts），绝不包含任何暗牌实体或摸牌实体；
 * 2. 绝不泄露袖牌、牌山内部顺序、未揭示里宝牌、玩家决策、候选选项或出千点数；
 * 3. 牌河、副露、Dora 指示牌、点数、立直状态、场况完全公开；
 * 4. 所有内部集合均深拷贝并包装为不可修改集合。
 */
class PublicBoardView @JvmOverloads constructor(
    val phase: GamePhase,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val wallRemaining: Int,
    doraIndicators: List<Tile>,
    scores: List<Int>,
    riichiStatus: List<Boolean>,
    discards: List<List<Tile>>,
    melds: List<List<Meld>>,
    handTileCounts: List<Int>,
    val lastDiscard: Tile? = null,
    val lastDiscardPlayer: Int? = null
) {
    val doraIndicators: List<Tile> = Collections.unmodifiableList(ArrayList(doraIndicators))
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val riichiStatus: List<Boolean> = Collections.unmodifiableList(ArrayList(riichiStatus))
    val discards: List<List<Tile>> = Collections.unmodifiableList(
        discards.map { Collections.unmodifiableList(ArrayList(it)) }
    )
    val melds: List<List<Meld>> = Collections.unmodifiableList(
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
        require(dealerSeat in 0..3) { "dealerSeat must be in 0..3: $dealerSeat" }
        require(currentTurnPlayer in 0..3) { "currentTurnPlayer must be in 0..3: $currentTurnPlayer" }
        require(this.scores.size == 4) { "scores must have size 4: ${this.scores.size}" }
        require(this.riichiStatus.size == 4) { "riichiStatus must have size 4: ${this.riichiStatus.size}" }
        require(this.discards.size == 4) { "discards must have size 4: ${this.discards.size}" }
        require(this.melds.size == 4) { "melds must have size 4: ${this.melds.size}" }
        require(this.handTileCounts.size == 4) { "handTileCounts must have size 4: ${this.handTileCounts.size}" }
        require(honbaSticks >= 0) { "honbaSticks must be >= 0: $honbaSticks" }
        require(riichiSticks >= 0) { "riichiSticks must be >= 0: $riichiSticks" }
        require(wallRemaining >= 0) { "wallRemaining must be >= 0: $wallRemaining" }
        if (lastDiscardPlayer != null) {
            require(lastDiscardPlayer in 0..3) { "lastDiscardPlayer must be in 0..3: $lastDiscardPlayer" }
        }
    }

    operator fun component1(): GamePhase = phase
    operator fun component2(): Int = roundWind
    operator fun component3(): Int = roundNumber
    operator fun component4(): Int = honbaSticks
    operator fun component5(): Int = riichiSticks
    operator fun component6(): Int = dealerSeat
    operator fun component7(): Int = currentTurnPlayer
    operator fun component8(): Int = wallRemaining
    operator fun component9(): List<Tile> = doraIndicators
    operator fun component10(): List<Int> = scores
    operator fun component11(): List<Boolean> = riichiStatus
    operator fun component12(): List<List<Tile>> = discards
    operator fun component13(): List<List<Meld>> = melds
    operator fun component14(): List<Int> = handTileCounts
    operator fun component15(): Tile? = lastDiscard
    operator fun component16(): Int? = lastDiscardPlayer

    fun copy(
        phase: GamePhase = this.phase,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        honbaSticks: Int = this.honbaSticks,
        riichiSticks: Int = this.riichiSticks,
        dealerSeat: Int = this.dealerSeat,
        currentTurnPlayer: Int = this.currentTurnPlayer,
        wallRemaining: Int = this.wallRemaining,
        doraIndicators: List<Tile> = this.doraIndicators,
        scores: List<Int> = this.scores,
        riichiStatus: List<Boolean> = this.riichiStatus,
        discards: List<List<Tile>> = this.discards,
        melds: List<List<Meld>> = this.melds,
        handTileCounts: List<Int> = this.handTileCounts,
        lastDiscard: Tile? = this.lastDiscard,
        lastDiscardPlayer: Int? = this.lastDiscardPlayer
    ): PublicBoardView = PublicBoardView(
        phase, roundWind, roundNumber, honbaSticks, riichiSticks, dealerSeat,
        currentTurnPlayer, wallRemaining, doraIndicators, scores, riichiStatus,
        discards, melds, handTileCounts, lastDiscard, lastDiscardPlayer
    )

    fun deepCopy(): PublicBoardView = PublicBoardView(
        phase = phase,
        roundWind = roundWind,
        roundNumber = roundNumber,
        honbaSticks = honbaSticks,
        riichiSticks = riichiSticks,
        dealerSeat = dealerSeat,
        currentTurnPlayer = currentTurnPlayer,
        wallRemaining = wallRemaining,
        doraIndicators = doraIndicators,
        scores = scores,
        riichiStatus = riichiStatus,
        discards = discards,
        melds = melds,
        handTileCounts = handTileCounts,
        lastDiscard = lastDiscard,
        lastDiscardPlayer = lastDiscardPlayer
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

    companion object {
        fun fromGameState(state: GameState): PublicBoardView {
            val handCounts = state.players.map { player ->
                player.closedTiles.size + (if (player.drawnTile != null) 1 else 0)
            }
            return PublicBoardView(
                phase = state.phase,
                roundWind = state.roundWind,
                roundNumber = state.roundNumber,
                honbaSticks = state.honbaSticks,
                riichiSticks = state.riichiSticks,
                dealerSeat = state.dealerIndex,
                currentTurnPlayer = state.currentPlayer,
                wallRemaining = state.wallRemaining,
                doraIndicators = state.doraIndicators,
                scores = state.scores.toList(),
                riichiStatus = state.riichiDeclared.toList(),
                discards = state.players.map { it.discards },
                melds = state.players.map { it.melds },
                handTileCounts = handCounts,
                lastDiscard = state.lastDiscard,
                lastDiscardPlayer = if (state.lastDiscardPlayer in 0..3) state.lastDiscardPlayer else null
            )
        }
    }
}

/**
 * 独立观战/公开领域投影 (PublicTableProjection)
 *
 * 聚合整场会话公开摘要、可选活动牌桌公开视图、时钟视图与结算事件。
 * 纯领域实体，不绑定特定玩家视点，不包含网络游标或代次。
 */
class PublicTableProjection @JvmOverloads constructor(
    val sessionId: String,
    val sessionVersion: Long,
    val roundId: String?,
    val roundVersion: Long?,
    val board: PublicBoardView?,
    val session: PublicSessionSummary,
    clocks: List<ClockView> = emptyList(),
    val paused: Boolean = false,
    val settlementRoundId: String? = null,
    settlementEvents: List<GameEvent> = emptyList()
) {
    val clocks: List<ClockView> = Collections.unmodifiableList(ArrayList(clocks))
    val settlementEvents: List<GameEvent> = if (settlementEvents is CopyOnReadEventList) {
        settlementEvents
    } else {
        CopyOnReadEventList(settlementEvents, revealSettlementUra = true)
    }

    init {
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(sessionVersion >= 0L) { "sessionVersion must be >= 0: $sessionVersion" }
        if (roundVersion != null) {
            require(roundVersion >= 0L) { "roundVersion must be >= 0: $roundVersion" }
        }
        if (settlementRoundId != null) {
            require(settlementRoundId.isNotBlank()) { "settlementRoundId cannot be blank" }
        }
    }

    operator fun component1(): String = sessionId
    operator fun component2(): Long = sessionVersion
    operator fun component3(): String? = roundId
    operator fun component4(): Long? = roundVersion
    operator fun component5(): PublicBoardView? = board
    operator fun component6(): PublicSessionSummary = session
    operator fun component7(): List<ClockView> = clocks
    operator fun component8(): Boolean = paused
    operator fun component9(): String? = settlementRoundId
    operator fun component10(): List<GameEvent> = settlementEvents

    fun copy(
        sessionId: String = this.sessionId,
        sessionVersion: Long = this.sessionVersion,
        roundId: String? = this.roundId,
        roundVersion: Long? = this.roundVersion,
        board: PublicBoardView? = this.board,
        session: PublicSessionSummary = this.session,
        clocks: List<ClockView> = this.clocks,
        paused: Boolean = this.paused,
        settlementRoundId: String? = this.settlementRoundId,
        settlementEvents: List<GameEvent> = this.settlementEvents
    ): PublicTableProjection = PublicTableProjection(
        sessionId, sessionVersion, roundId, roundVersion, board, session,
        clocks, paused, settlementRoundId, settlementEvents
    )

    fun deepCopy(): PublicTableProjection = PublicTableProjection(
        sessionId = sessionId,
        sessionVersion = sessionVersion,
        roundId = roundId,
        roundVersion = roundVersion,
        board = board?.deepCopy(),
        session = session.deepCopy(),
        clocks = clocks,
        paused = paused,
        settlementRoundId = settlementRoundId,
        settlementEvents = settlementEvents
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublicTableProjection) return false
        return sessionId == other.sessionId &&
                sessionVersion == other.sessionVersion &&
                roundId == other.roundId &&
                roundVersion == other.roundVersion &&
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
        result = 31 * result + (board?.hashCode() ?: 0)
        result = 31 * result + session.hashCode()
        result = 31 * result + clocks.hashCode()
        result = 31 * result + paused.hashCode()
        result = 31 * result + (settlementRoundId?.hashCode() ?: 0)
        result = 31 * result + settlementEvents.hashCode()
        return result
    }

    override fun toString(): String =
        "PublicTableProjection(sessionId=$sessionId, sessionVersion=$sessionVersion, roundId=$roundId, roundVersion=$roundVersion, board=$board, session=$session, clocks=$clocks, paused=$paused, settlementRoundId=$settlementRoundId, settlementEvents=$settlementEvents)"

    companion object {
        fun create(
            sessionId: String,
            sessionVersion: Long,
            roundId: String?,
            roundVersion: Long?,
            board: PublicBoardView?,
            session: PublicSessionSummary,
            clocks: List<ClockView> = emptyList(),
            paused: Boolean = false,
            settlementRoundId: String? = null,
            settlementEvents: List<GameEvent> = emptyList()
        ): PublicTableProjection = PublicTableProjection(
            sessionId, sessionVersion, roundId, roundVersion, board, session,
            clocks, paused, settlementRoundId, settlementEvents
        )
    }
}

/**
 * 不可修改且具备读取时防御性深快照语义的公开事件列表。
 *
 * 解决 ScoreSettled.newScores / MatchEnded.finalScores 等内部原生数组
 * 在通过 getter 读取并就地修改后，污染下一次读取的问题。
 * 同时覆盖数组数据类的元素级值等价比较与 contains/indexOf 语义。
 */
class CopyOnReadEventList(
    underlying: List<GameEvent>,
    private val revealSettlementUra: Boolean
) : List<GameEvent> {

    private val storage: List<GameEvent> = Collections.unmodifiableList(
        ArrayList(underlying.map { deepCopyEvent(it, revealSettlementUra) })
    )

    override val size: Int get() = storage.size

    override fun isEmpty(): Boolean = storage.isEmpty()

    override fun contains(element: GameEvent): Boolean =
        storage.any { eventValueEquals(it, element) }

    override fun containsAll(elements: Collection<GameEvent>): Boolean =
        elements.all { contains(it) }

    override fun get(index: Int): GameEvent {
        return deepCopyEvent(storage[index], revealSettlementUra)
    }

    override fun indexOf(element: GameEvent): Int =
        storage.indexOfFirst { eventValueEquals(it, element) }

    override fun lastIndexOf(element: GameEvent): Int =
        storage.indexOfLast { eventValueEquals(it, element) }

    override fun iterator(): Iterator<GameEvent> = object : Iterator<GameEvent> {
        private var cursor = 0
        override fun hasNext(): Boolean = cursor < storage.size
        override fun next(): GameEvent {
            if (cursor >= storage.size) throw NoSuchElementException()
            return deepCopyEvent(storage[cursor++], revealSettlementUra)
        }
    }

    override fun listIterator(): ListIterator<GameEvent> = listIterator(0)

    override fun listIterator(index: Int): ListIterator<GameEvent> {
        if (index < 0 || index > storage.size) {
            throw IndexOutOfBoundsException("Index: $index, Size: ${storage.size}")
        }
        return object : ListIterator<GameEvent> {
            private var cursor = index
            override fun hasNext(): Boolean = cursor < storage.size
            override fun next(): GameEvent {
                if (cursor >= storage.size) throw NoSuchElementException()
                return deepCopyEvent(storage[cursor++], revealSettlementUra)
            }
            override fun hasPrevious(): Boolean = cursor > 0
            override fun previous(): GameEvent {
                if (cursor <= 0) throw NoSuchElementException()
                return deepCopyEvent(storage[--cursor], revealSettlementUra)
            }
            override fun nextIndex(): Int = cursor
            override fun previousIndex(): Int = cursor - 1
        }
    }

    override fun subList(fromIndex: Int, toIndex: Int): List<GameEvent> {
        return CopyOnReadEventList(storage.subList(fromIndex, toIndex), revealSettlementUra)
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other is CopyOnReadEventList) {
            if (size != other.size) return false
            for (i in indices) {
                if (!eventValueEquals(storage[i], other.storage[i])) return false
            }
            return true
        }
        if (other !is List<*>) return false
        if (size != other.size) return false
        for (i in indices) {
            val otherElem = other[i]
            val thisElem = get(i)
            if (thisElem != otherElem) return false
        }
        return true
    }

    override fun hashCode(): Int {
        var hashCode = 1
        for (i in indices) {
            hashCode = 31 * hashCode + eventValueHashCode(storage[i])
        }
        return hashCode
    }

    override fun toString(): String {
        return storage.mapIndexed { i, _ -> get(i) }.toString()
    }
}

internal fun sanitizeBreakdown(
    breakdown: ScoreBreakdown?,
    revealSettlementUra: Boolean
): ScoreBreakdown? {
    if (breakdown == null) return null
    val snap = snapshotScoreBreakdown(breakdown) ?: return null
    return if (!revealSettlementUra) {
        snap.copy(
            dora = snap.dora.copy(
                uraTiles = Collections.unmodifiableList(emptyList()),
                uraHits = Collections.unmodifiableList(emptyList())
            )
        )
    } else {
        snap
    }
}

internal fun deepCopyEvent(event: GameEvent, revealSettlementUra: Boolean): GameEvent {
    val snap = snapshotGameEvent(event)
    return when (snap) {
        is GameEvent.ScoreSettled -> snap.copy(
            payments = Collections.unmodifiableMap(LinkedHashMap(snap.payments)),
            newScores = snap.newScores.clone()
        )
        is GameEvent.MatchEnded -> snap.copy(
            finalScores = snap.finalScores.clone()
        )
        is GameEvent.TsumoWin -> snap.copy(
            yakuList = Collections.unmodifiableList(ArrayList(snap.yakuList)),
            breakdown = sanitizeBreakdown(snap.breakdown, revealSettlementUra)
        )
        is GameEvent.RonWin -> snap.copy(
            yakuList = Collections.unmodifiableList(ArrayList(snap.yakuList)),
            breakdown = sanitizeBreakdown(snap.breakdown, revealSettlementUra)
        )
        is GameEvent.DrawGame -> snap.copy(
            tenpaiPlayers = Collections.unmodifiableList(ArrayList(snap.tenpaiPlayers))
        )
        is GameEvent.ChiDeclared -> snap.copy(
            meld = snap.meld.copy(tiles = Collections.unmodifiableList(ArrayList(snap.meld.tiles)))
        )
        is GameEvent.PonDeclared -> snap.copy(
            meld = snap.meld.copy(tiles = Collections.unmodifiableList(ArrayList(snap.meld.tiles)))
        )
        is GameEvent.KanDeclared -> snap.copy(
            meld = snap.meld.copy(tiles = Collections.unmodifiableList(ArrayList(snap.meld.tiles)))
        )
        else -> snap
    }
}

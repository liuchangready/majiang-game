package com.majiang.match

import com.majiang.economy.CheatLedgerEntry
import com.majiang.economy.CheatLedgerSummary
import com.majiang.economy.CheatSeatStatistics
import com.majiang.event.GameEvent
import com.majiang.round.RoundResult
import com.majiang.round.RoundStateMachine
import java.util.Collections
import java.util.UUID

/**
 * 整场比赛生命周期阶段
 */
enum class SessionLifecycle {
    CREATED,
    WAITING_START,
    ROUND_ACTIVE,
    ROUND_RESULT,
    WAITING_DEALER_CHOICE,
    WAITING_READY,
    FINISHED,
    PAUSED,
    CLOSED
}

/**
 * 单局历史记录简报
 */
data class RoundRecord @JvmOverloads constructor(
    val roundId: String,
    val resultId: String,
    val outcome: RoundOutcome,
    val finalScores: List<Int>,
    val deltas: List<Int>,
    val remainingRiichiSticks: Int,
    val finalCheatPoints: List<Int> = List(4) { 0 },
    val cheatDeltas: List<Int> = List(4) { 0 },
    val cheatEntries: List<CheatLedgerEntry> = emptyList()
)

/**
 * 整场终局结果
 */
data class SessionResult @JvmOverloads constructor(
    val sessionId: String,
    val finalScores: List<Int>,
    val rankings: List<RankingEntry>,
    val reason: MatchFinishReason,
    val remainingRiichiSticksTransferredTo: Int? = null,
    val finalCheatPoints: List<Int> = List(4) { 0 },
    val cheatSummary: CheatLedgerSummary? = null
)

/**
 * 推进方案类型
 */
enum class ProgressionPlanKind {
    PREPARE_NEXT_ROUND,
    WAIT_DEALER_CHOICE,
    FINISH_SESSION
}

/**
 * 由外部协调器调用 MatchProgression 产出的整场推进方案
 */
data class ProgressionPlan(
    val kind: ProgressionPlanKind,
    val nextRound: ProgressionResult.NextRound? = null,
    val matchFinished: ProgressionResult.MatchFinished? = null,
    val canDealerDeclareAgariYame: Boolean = false
)

/**
 * 经过外部验证并批准的整场变更指令（只供可信协调器使用）
 */
sealed interface ApprovedSessionStep {
    data class StartSession(val firstRound: RoundStateMachine) : ApprovedSessionStep
    data class AcceptRoundResult(val result: RoundResult, val progressionPlan: ProgressionPlan) : ApprovedSessionStep
    data class ChooseDealerEnd(val agariYame: Boolean, val progressionPlan: ProgressionPlan) : ApprovedSessionStep
    data class StartNextRound(val nextRound: RoundStateMachine) : ApprovedSessionStep
    object Pause : ApprovedSessionStep
    object Resume : ApprovedSessionStep
    object Close : ApprovedSessionStep
}

/**
 * 隔离候选与已安装数据的整场状态变更描述
 */
data class SessionChange @JvmOverloads constructor(
    val sessionId: String,
    val sourceVersion: Long,
    val targetLifecycle: SessionLifecycle,
    val targetDealer: Int,
    val targetRoundWind: Int,
    val targetRoundNumber: Int,
    val targetHonba: Int,
    val targetScores: List<Int>,
    val targetRiichiSticks: Int,
    val targetActiveRound: RoundStateMachine?,
    val newRoundRecord: RoundRecord? = null,
    val targetPendingProgression: ProgressionPlan? = null,
    val targetCanDealerDeclareAgariYame: Boolean = false,
    val targetResult: SessionResult? = null,
    val events: List<GameEvent> = emptyList(),
    val targetCheatPoints: List<Int> = emptyList()
)

/**
 * 整场只读快照
 */
data class SessionSnapshot @JvmOverloads constructor(
    val sessionId: String,
    val version: Long,
    val lifecycle: SessionLifecycle,
    val ruleConfig: MatchRuleConfig,
    val currentDealer: Int,
    val currentRoundWind: Int,
    val currentRoundNumber: Int,
    val currentHonba: Int,
    val betweenRoundScores: List<Int>,
    val betweenRoundRiichiSticks: Int,
    val activeRoundId: String?,
    val roundRecords: List<RoundRecord>,
    val canDealerDeclareAgariYame: Boolean,
    val result: SessionResult?,
    val betweenRoundCheatPoints: List<Int> = List(4) { 0 }
)

/**
 * 整场比赛 Session (DESIGN v2)
 *
 * 核心职责：
 * 1. 独立保存整场状态：局间账本、进度、活动局引用、单局历史、整场完成；
 * 2. 纯领域容器：不包含麻将规则验证、算分算法、守恒审计、AI 策略或网络依赖；
 * 3. 内部 prepare / install 严格通过 ApprovedSessionStep 与 SessionChange 执行变更；
 * 4. 每局终态生成结果并且只交接一次，结算和供托不重复计入；
 * 5. 整场结束时旧 Round 终态保持 ROUND_END，不被修改为 MATCH_END。
 * 6. 所有结果、record 与 pending plan 等嵌套集合严格进行防御复制。
 */
class MatchSession @JvmOverloads constructor(
    val sessionId: String = UUID.randomUUID().toString(),
    ruleConfig: MatchRuleConfig = MatchRuleConfig(),
    initialScores: List<Int> = listOf(25000, 25000, 25000, 25000),
    initialDealer: Int = 0,
    initialRoundWind: Int = 1,
    initialRoundNumber: Int = 0,
    initialHonba: Int = 0,
    initialRiichiSticks: Int = 0,
    initialCheatPoints: List<Int> = List(4) { 0 }
) {
    var version: Long = 0L
        private set

    var lifecycle: SessionLifecycle = SessionLifecycle.CREATED
        private set

    var ruleConfig: MatchRuleConfig = ruleConfig
        private set

    var currentDealer: Int = initialDealer
        private set

    var currentRoundWind: Int = initialRoundWind
        private set

    var currentRoundNumber: Int = initialRoundNumber
        private set

    var currentHonba: Int = initialHonba
        private set

    private var _initialScores: List<Int> = Collections.unmodifiableList(initialScores.toList())
    var initialScores: List<Int>
        get() = _initialScores
        internal set(value) {
            _initialScores = Collections.unmodifiableList(value.toList())
        }

    private var _betweenRoundScores: List<Int> = Collections.unmodifiableList(initialScores.toList())
    var betweenRoundScores: List<Int>
        get() = _betweenRoundScores
        internal set(value) {
            _betweenRoundScores = Collections.unmodifiableList(value.toList())
        }

    var betweenRoundRiichiSticks: Int = initialRiichiSticks
        private set

    private var _initialCheatPoints: List<Int> = Collections.unmodifiableList(initialCheatPoints.toList())
    var initialCheatPoints: List<Int>
        get() = _initialCheatPoints
        internal set(value) {
            _initialCheatPoints = Collections.unmodifiableList(value.toList())
        }

    private var _betweenRoundCheatPoints: List<Int> = Collections.unmodifiableList(initialCheatPoints.toList())
    var betweenRoundCheatPoints: List<Int>
        get() = _betweenRoundCheatPoints
        internal set(value) {
            _betweenRoundCheatPoints = Collections.unmodifiableList(value.toList())
        }

    var activeRound: RoundStateMachine? = null
        private set

    private val _roundRecords: MutableList<RoundRecord> = mutableListOf()
    val roundRecords: List<RoundRecord>
        get() = Collections.unmodifiableList(_roundRecords.map { r ->
            r.copy(
                finalScores = Collections.unmodifiableList(r.finalScores.toList()),
                deltas = Collections.unmodifiableList(r.deltas.toList()),
                finalCheatPoints = Collections.unmodifiableList(r.finalCheatPoints.toList()),
                cheatDeltas = Collections.unmodifiableList(r.cheatDeltas.toList()),
                cheatEntries = Collections.unmodifiableList(r.cheatEntries.map { it.copy() })
            )
        })

    private var _pendingProgressionPlan: ProgressionPlan? = null
    var pendingProgressionPlan: ProgressionPlan?
        get() = _pendingProgressionPlan?.let { plan ->
            plan.copy(
                nextRound = plan.nextRound?.copy(),
                matchFinished = plan.matchFinished?.copy(
                    finalScores = Collections.unmodifiableList(plan.matchFinished.finalScores.toList()),
                    rankings = Collections.unmodifiableList(plan.matchFinished.rankings.map { it.copy() })
                )
            )
        }
        private set(value) {
            _pendingProgressionPlan = value
        }

    var canDealerDeclareAgariYame: Boolean = false
        private set

    private var _result: SessionResult? = null
    var result: SessionResult?
        get() = _result?.let { res ->
            res.copy(
                finalScores = Collections.unmodifiableList(res.finalScores.toList()),
                rankings = Collections.unmodifiableList(res.rankings.map { it.copy() }),
                finalCheatPoints = Collections.unmodifiableList(res.finalCheatPoints.toList()),
                cheatSummary = res.cheatSummary
            )
        }
        private set(value) {
            _result = value
        }

    /**
     * 读取整场当前不可变快照
     */
    fun readState(): SessionSnapshot = SessionSnapshot(
        sessionId = sessionId,
        version = version,
        lifecycle = lifecycle,
        ruleConfig = ruleConfig.copy(),
        currentDealer = currentDealer,
        currentRoundWind = currentRoundWind,
        currentRoundNumber = currentRoundNumber,
        currentHonba = currentHonba,
        betweenRoundScores = Collections.unmodifiableList(betweenRoundScores.toList()),
        betweenRoundRiichiSticks = betweenRoundRiichiSticks,
        activeRoundId = activeRound?.roundId,
        roundRecords = roundRecords,
        canDealerDeclareAgariYame = canDealerDeclareAgariYame,
        result = result,
        betweenRoundCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
    )

    /**
     * 针对初始局的外部同步（仅允许在初始版本使用，且严格只同步初始账本与坐席信息）
     */
    internal fun syncInitialState(state: com.majiang.state.GameState) {
        if (version <= 1L && lifecycle == SessionLifecycle.ROUND_ACTIVE && _roundRecords.isEmpty()) {
            currentDealer = state.dealerIndex
            currentRoundWind = state.roundWind
            currentRoundNumber = state.roundNumber
            currentHonba = state.honbaSticks
            _betweenRoundScores = Collections.unmodifiableList(state.scores.toList())
            _initialScores = Collections.unmodifiableList(state.scores.toList())
            betweenRoundRiichiSticks = state.riichiSticks
            _betweenRoundCheatPoints = Collections.unmodifiableList(state.cheatPoints.toList())
            _initialCheatPoints = Collections.unmodifiableList(state.cheatPoints.toList())
        }
    }

    /**
     * 大厅准备阶段单局未发牌时的原子配置更新
     */
    internal fun updateLobbyConfig(config: com.majiang.match.MatchConfig) {
        check(version <= 1L && _roundRecords.isEmpty()) {
            "Cannot update lobby config on settled or progressed session"
        }
        this.ruleConfig = config.toRuleConfig()
        this.currentRoundWind = config.winds.firstOrNull() ?: 1
        this.currentRoundNumber = 0
        this.currentDealer = 0
        this.currentHonba = 0
        this.betweenRoundRiichiSticks = 0
        this._initialScores = Collections.unmodifiableList(config.startingScores.toList())
        this._betweenRoundScores = Collections.unmodifiableList(config.startingScores.toList())
        val cheatPts = if (config.cheatEconomy.enabled) {
            config.cheatEconomy.initialPoints.toList()
        } else {
            List(4) { 0 }
        }
        this._initialCheatPoints = Collections.unmodifiableList(cheatPts)
        this._betweenRoundCheatPoints = Collections.unmodifiableList(cheatPts)
    }

    /**
     * 根据已批准指令准备候选变更，不修改本对象权威状态、不执行副作用
     */
    internal fun prepare(step: ApprovedSessionStep): SessionChange {
        return when (step) {
            is ApprovedSessionStep.StartSession -> {
                SessionChange(
                    sessionId = sessionId,
                    sourceVersion = version,
                    targetLifecycle = SessionLifecycle.ROUND_ACTIVE,
                    targetDealer = currentDealer,
                    targetRoundWind = currentRoundWind,
                    targetRoundNumber = currentRoundNumber,
                    targetHonba = currentHonba,
                    targetScores = Collections.unmodifiableList(betweenRoundScores.toList()),
                    targetRiichiSticks = betweenRoundRiichiSticks,
                    targetActiveRound = step.firstRound,
                    targetPendingProgression = null,
                    targetCanDealerDeclareAgariYame = false,
                    targetResult = null,
                    events = emptyList(),
                    targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                )
            }

            is ApprovedSessionStep.AcceptRoundResult -> {
                val roundRes = step.result
                val record = RoundRecord(
                    roundId = roundRes.roundId,
                    resultId = roundRes.resultId,
                    outcome = roundRes.outcome,
                    finalScores = Collections.unmodifiableList(roundRes.finalScores.toList()),
                    deltas = Collections.unmodifiableList(roundRes.totalDeltas.toList()),
                    remainingRiichiSticks = roundRes.remainingRiichiSticks,
                    finalCheatPoints = Collections.unmodifiableList(roundRes.finalCheatPoints.toList()),
                    cheatDeltas = Collections.unmodifiableList(roundRes.cheatPointDeltas.toList()),
                    cheatEntries = Collections.unmodifiableList(roundRes.cheatEntries.map { it.copy() })
                )

                when (step.progressionPlan.kind) {
                    ProgressionPlanKind.FINISH_SESSION -> {
                        val finished = step.progressionPlan.matchFinished
                        val finalScores = finished?.finalScores ?: roundRes.finalScores
                        val allCheatEntries = _roundRecords.flatMap { it.cheatEntries } + roundRes.cheatEntries
                        val finals = roundRes.finalCheatPoints
                        val startBaselines = _initialCheatPoints
                        val summary = CheatLedgerSummary(
                            startingBalances = Collections.unmodifiableList(startBaselines.toList()),
                            finalBalances = Collections.unmodifiableList(finals.toList()),
                            totalDeltas = Collections.unmodifiableList((0 until 4).map { finals[it] - startBaselines[it] }),
                            entries = Collections.unmodifiableList(allCheatEntries.map { it.copy() }),
                            seatStatistics = Collections.unmodifiableList((0 until 4).map { s ->
                                val gained = allCheatEntries.filter { it.seat == s && it.amount > 0 }.sumOf { it.amount }
                                val spent = allCheatEntries.filter { it.seat == s && it.amount < 0 }.sumOf { -it.amount }
                                CheatSeatStatistics(
                                    seat = s,
                                    startingPoints = startBaselines.getOrElse(s) { 0 },
                                    finalPoints = finals.getOrElse(s) { 0 },
                                    totalGained = gained,
                                    totalSpent = spent,
                                    netDelta = finals.getOrElse(s) { 0 } - startBaselines.getOrElse(s) { 0 }
                                )
                            })
                        )
                        val sessionRes = SessionResult(
                            sessionId = sessionId,
                            finalScores = Collections.unmodifiableList(finalScores.toList()),
                            rankings = Collections.unmodifiableList(finished?.rankings?.map { it.copy() } ?: emptyList()),
                            reason = finished?.reason ?: MatchFinishReason.NORMAL_COMPLETION,
                            remainingRiichiSticksTransferredTo = finished?.remainingRiichiSticksTransferredTo,
                            finalCheatPoints = Collections.unmodifiableList(finals.toList()),
                            cheatSummary = summary
                        )
                        val winnerSeat = sessionRes.rankings.firstOrNull()?.seat ?: 0
                        val matchEndEvent = GameEvent.MatchEnded(
                            turnNumber = 0,
                            finalScores = finalScores.toIntArray(),
                            winner = winnerSeat
                        )

                        SessionChange(
                            sessionId = sessionId,
                            sourceVersion = version,
                            targetLifecycle = SessionLifecycle.FINISHED,
                            targetDealer = currentDealer,
                            targetRoundWind = currentRoundWind,
                            targetRoundNumber = currentRoundNumber,
                            targetHonba = currentHonba,
                            targetScores = Collections.unmodifiableList(finalScores.toList()),
                            targetRiichiSticks = 0,
                            targetActiveRound = null,
                            newRoundRecord = record,
                            targetPendingProgression = null,
                            targetCanDealerDeclareAgariYame = false,
                            targetResult = sessionRes,
                            events = listOf(matchEndEvent),
                            targetCheatPoints = Collections.unmodifiableList(roundRes.finalCheatPoints.toList())
                        )
                    }

                    ProgressionPlanKind.WAIT_DEALER_CHOICE -> {
                        SessionChange(
                            sessionId = sessionId,
                            sourceVersion = version,
                            targetLifecycle = SessionLifecycle.WAITING_DEALER_CHOICE,
                            targetDealer = currentDealer,
                            targetRoundWind = currentRoundWind,
                            targetRoundNumber = currentRoundNumber,
                            targetHonba = currentHonba,
                            targetScores = Collections.unmodifiableList(roundRes.finalScores.toList()),
                            targetRiichiSticks = roundRes.remainingRiichiSticks,
                            targetActiveRound = null,
                            newRoundRecord = record,
                            targetPendingProgression = step.progressionPlan.copy(),
                            targetCanDealerDeclareAgariYame = true,
                            targetResult = null,
                            events = emptyList(),
                            targetCheatPoints = Collections.unmodifiableList(roundRes.finalCheatPoints.toList())
                        )
                    }

                    ProgressionPlanKind.PREPARE_NEXT_ROUND -> {
                        val next = step.progressionPlan.nextRound
                        SessionChange(
                            sessionId = sessionId,
                            sourceVersion = version,
                            targetLifecycle = SessionLifecycle.WAITING_READY,
                            targetDealer = next?.dealer ?: currentDealer,
                            targetRoundWind = next?.roundWind ?: currentRoundWind,
                            targetRoundNumber = next?.roundNumber ?: currentRoundNumber,
                            targetHonba = next?.honba ?: currentHonba,
                            targetScores = Collections.unmodifiableList(roundRes.finalScores.toList()),
                            targetRiichiSticks = roundRes.remainingRiichiSticks,
                            targetActiveRound = null,
                            newRoundRecord = record,
                            targetPendingProgression = step.progressionPlan.copy(),
                            targetCanDealerDeclareAgariYame = false,
                            targetResult = null,
                            events = emptyList(),
                            targetCheatPoints = Collections.unmodifiableList(roundRes.finalCheatPoints.toList())
                        )
                    }
                }
            }

            is ApprovedSessionStep.ChooseDealerEnd -> {
                if (step.agariYame) {
                    val finished = step.progressionPlan.matchFinished
                    val finalScores = finished?.finalScores ?: betweenRoundScores
                    val allCheatEntries = _roundRecords.flatMap { it.cheatEntries }
                    val finals = betweenRoundCheatPoints
                    val startBaselines = _initialCheatPoints
                    val summary = CheatLedgerSummary(
                        startingBalances = Collections.unmodifiableList(startBaselines.toList()),
                        finalBalances = Collections.unmodifiableList(finals.toList()),
                        totalDeltas = Collections.unmodifiableList((0 until 4).map { finals[it] - startBaselines[it] }),
                        entries = Collections.unmodifiableList(allCheatEntries.map { it.copy() }),
                        seatStatistics = Collections.unmodifiableList((0 until 4).map { s ->
                            val gained = allCheatEntries.filter { it.seat == s && it.amount > 0 }.sumOf { it.amount }
                            val spent = allCheatEntries.filter { it.seat == s && it.amount < 0 }.sumOf { -it.amount }
                            CheatSeatStatistics(
                                seat = s,
                                startingPoints = startBaselines.getOrElse(s) { 0 },
                                finalPoints = finals.getOrElse(s) { 0 },
                                totalGained = gained,
                                totalSpent = spent,
                                netDelta = finals.getOrElse(s) { 0 } - startBaselines.getOrElse(s) { 0 }
                            )
                        })
                    )
                    val sessionRes = SessionResult(
                        sessionId = sessionId,
                        finalScores = Collections.unmodifiableList(finalScores.toList()),
                        rankings = Collections.unmodifiableList(finished?.rankings?.map { it.copy() } ?: emptyList()),
                        reason = MatchFinishReason.AGARI_YAME,
                        remainingRiichiSticksTransferredTo = finished?.remainingRiichiSticksTransferredTo,
                        finalCheatPoints = Collections.unmodifiableList(finals.toList()),
                        cheatSummary = summary
                    )
                    val winnerSeat = sessionRes.rankings.firstOrNull()?.seat ?: currentDealer
                    val matchEndEvent = GameEvent.MatchEnded(
                        turnNumber = 0,
                        finalScores = finalScores.toIntArray(),
                        winner = winnerSeat
                    )

                    SessionChange(
                        sessionId = sessionId,
                        sourceVersion = version,
                        targetLifecycle = SessionLifecycle.FINISHED,
                        targetDealer = currentDealer,
                        targetRoundWind = currentRoundWind,
                        targetRoundNumber = currentRoundNumber,
                        targetHonba = currentHonba,
                        targetScores = Collections.unmodifiableList(finalScores.toList()),
                        targetRiichiSticks = 0,
                        targetActiveRound = null,
                        targetPendingProgression = null,
                        targetCanDealerDeclareAgariYame = false,
                        targetResult = sessionRes,
                        events = listOf(matchEndEvent),
                        targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                    )
                } else {
                    val next = step.progressionPlan.nextRound
                    SessionChange(
                        sessionId = sessionId,
                        sourceVersion = version,
                        targetLifecycle = SessionLifecycle.WAITING_READY,
                        targetDealer = next?.dealer ?: currentDealer,
                        targetRoundWind = next?.roundWind ?: currentRoundWind,
                        targetRoundNumber = next?.roundNumber ?: currentRoundNumber,
                        targetHonba = next?.honba ?: currentHonba,
                        targetScores = Collections.unmodifiableList(betweenRoundScores.toList()),
                        targetRiichiSticks = betweenRoundRiichiSticks,
                        targetActiveRound = null,
                        targetPendingProgression = step.progressionPlan.copy(),
                        targetCanDealerDeclareAgariYame = false,
                        targetResult = null,
                        events = emptyList(),
                        targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                    )
                }
            }

            is ApprovedSessionStep.StartNextRound -> {
                val next = _pendingProgressionPlan?.nextRound
                val targetDealer = if (lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE && next != null) next.dealer else currentDealer
                val targetRoundWind = if (lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE && next != null) next.roundWind else currentRoundWind
                val targetRoundNumber = if (lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE && next != null) next.roundNumber else currentRoundNumber
                val targetHonba = if (lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE && next != null) next.honba else currentHonba

                SessionChange(
                    sessionId = sessionId,
                    sourceVersion = version,
                    targetLifecycle = SessionLifecycle.ROUND_ACTIVE,
                    targetDealer = targetDealer,
                    targetRoundWind = targetRoundWind,
                    targetRoundNumber = targetRoundNumber,
                    targetHonba = targetHonba,
                    targetScores = Collections.unmodifiableList(betweenRoundScores.toList()),
                    targetRiichiSticks = betweenRoundRiichiSticks,
                    targetActiveRound = step.nextRound,
                    targetPendingProgression = null,
                    targetCanDealerDeclareAgariYame = false,
                    targetResult = null,
                    events = emptyList(),
                    targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                )
            }

            is ApprovedSessionStep.Pause -> {
                SessionChange(
                    sessionId = sessionId,
                    sourceVersion = version,
                    targetLifecycle = SessionLifecycle.PAUSED,
                    targetDealer = currentDealer,
                    targetRoundWind = currentRoundWind,
                    targetRoundNumber = currentRoundNumber,
                    targetHonba = currentHonba,
                    targetScores = Collections.unmodifiableList(betweenRoundScores.toList()),
                    targetRiichiSticks = betweenRoundRiichiSticks,
                    targetActiveRound = activeRound,
                    targetPendingProgression = _pendingProgressionPlan?.copy(),
                    targetCanDealerDeclareAgariYame = canDealerDeclareAgariYame,
                    targetResult = result,
                    events = emptyList(),
                    targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                )
            }

            is ApprovedSessionStep.Resume -> {
                val restoredLifecycle = if (activeRound != null) SessionLifecycle.ROUND_ACTIVE else SessionLifecycle.WAITING_READY
                SessionChange(
                    sessionId = sessionId,
                    sourceVersion = version,
                    targetLifecycle = restoredLifecycle,
                    targetDealer = currentDealer,
                    targetRoundWind = currentRoundWind,
                    targetRoundNumber = currentRoundNumber,
                    targetHonba = currentHonba,
                    targetScores = Collections.unmodifiableList(betweenRoundScores.toList()),
                    targetRiichiSticks = betweenRoundRiichiSticks,
                    targetActiveRound = activeRound,
                    targetPendingProgression = _pendingProgressionPlan?.copy(),
                    targetCanDealerDeclareAgariYame = canDealerDeclareAgariYame,
                    targetResult = result,
                    events = emptyList(),
                    targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                )
            }

            is ApprovedSessionStep.Close -> {
                SessionChange(
                    sessionId = sessionId,
                    sourceVersion = version,
                    targetLifecycle = SessionLifecycle.CLOSED,
                    targetDealer = currentDealer,
                    targetRoundWind = currentRoundWind,
                    targetRoundNumber = currentRoundNumber,
                    targetHonba = currentHonba,
                    targetScores = Collections.unmodifiableList(betweenRoundScores.toList()),
                    targetRiichiSticks = betweenRoundRiichiSticks,
                    targetActiveRound = null,
                    targetPendingProgression = null,
                    targetCanDealerDeclareAgariYame = false,
                    targetResult = result,
                    events = emptyList(),
                    targetCheatPoints = Collections.unmodifiableList(betweenRoundCheatPoints.toList())
                )
            }
        }
    }

    /**
     * 仅供可信协调器在审计后安装已批准变更
     */
    internal fun install(change: SessionChange) {
        if (change.sourceVersion != this.version) {
            throw IllegalStateException("SessionChange sourceVersion ${change.sourceVersion} does not match current session version $version")
        }
        this.version++
        this.lifecycle = change.targetLifecycle
        this.currentDealer = change.targetDealer
        this.currentRoundWind = change.targetRoundWind
        this.currentRoundNumber = change.targetRoundNumber
        this.currentHonba = change.targetHonba
        this._betweenRoundScores = Collections.unmodifiableList(change.targetScores.toList())
        this.betweenRoundRiichiSticks = change.targetRiichiSticks
        if (change.targetCheatPoints.isNotEmpty()) {
            this._betweenRoundCheatPoints = Collections.unmodifiableList(change.targetCheatPoints.toList())
        }
        this.activeRound = change.targetActiveRound
        change.newRoundRecord?.let { rec ->
            this._roundRecords.add(rec.copy(
                finalScores = Collections.unmodifiableList(rec.finalScores.toList()),
                deltas = Collections.unmodifiableList(rec.deltas.toList()),
                finalCheatPoints = Collections.unmodifiableList(rec.finalCheatPoints.toList()),
                cheatDeltas = Collections.unmodifiableList(rec.cheatDeltas.toList()),
                cheatEntries = Collections.unmodifiableList(rec.cheatEntries.map { it.copy() })
            ))
        }
        this._pendingProgressionPlan = change.targetPendingProgression?.copy(
            nextRound = change.targetPendingProgression.nextRound?.copy(),
            matchFinished = change.targetPendingProgression.matchFinished?.copy(
                finalScores = Collections.unmodifiableList(change.targetPendingProgression.matchFinished.finalScores.toList()),
                rankings = Collections.unmodifiableList(change.targetPendingProgression.matchFinished.rankings.map { it.copy() })
            )
        )
        this.canDealerDeclareAgariYame = change.targetCanDealerDeclareAgariYame
        this._result = change.targetResult?.copy(
            finalScores = Collections.unmodifiableList(change.targetResult.finalScores.toList()),
            rankings = Collections.unmodifiableList(change.targetResult.rankings.map { it.copy() }),
            finalCheatPoints = Collections.unmodifiableList(change.targetResult.finalCheatPoints.toList()),
            cheatSummary = change.targetResult.cheatSummary
        )
    }
}

package com.majiang.authority

import com.majiang.access.ActionReceipt
import com.majiang.access.ClockView
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PublicRoundRecord
import com.majiang.access.PublicSessionResult
import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.access.SessionView
import com.majiang.access.deepCopy
import com.majiang.ai.AiCandidateLimits
import com.majiang.ai.AiCheatPolicy
import com.majiang.ai.AiCheatView
import com.majiang.ai.AiDecisionStamp
import com.majiang.ai.AiPhase
import com.majiang.ai.AiSeatSnapshot
import com.majiang.ai.cheat.AiCapabilityIds
import com.majiang.application.ActionChoice
import com.majiang.application.ApplicationCoordinator
import com.majiang.application.PlayerDecision
import com.majiang.application.deepCopy
import com.majiang.authority.cheat.BeamSwapSubmissionHandler
import com.majiang.authority.cheat.CheatSubmissionCheck
import com.majiang.authority.cheat.ShufflePreparationHandler
import com.majiang.authority.cheat.SleeveBloomSubmissionHandler
import com.majiang.authority.cheat.TileMarkSubmissionHandler
import com.majiang.authority.cheat.rejectionReason
import com.majiang.cheat.beam.*
import com.majiang.cheat.bloom.*
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkCost
import com.majiang.cheat.mark.TileMarkProjectionFactory
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.cheat.mark.TileMarkView
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.match.MatchProgression
import com.majiang.match.SessionLifecycle
import com.majiang.projection.PublicProjectionFactory
import com.majiang.projection.PublicTableProjection
import com.majiang.session.PlayerView
import com.majiang.session.SessionUpdate
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShufflePreparationView
import com.majiang.state.ActionResult
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.timing.DecisionClock
import com.majiang.timing.DecisionKey
import com.majiang.timing.DecisionTiming
import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock
import com.majiang.timing.TimeControlConfig
import com.majiang.timing.TimeoutPolicy
import java.util.Collections
import java.util.LinkedHashSet
import java.util.concurrent.ConcurrentHashMap

/**
 * 领域纯同步权威入口 (TableAuthority)
 *
 * 遵循 V4 架构契约与 TASK-ANTI-R4A 规范：
 * 1. 作为纯领域同步门面，直接聚合 [ApplicationCoordinator]、[DecisionClock] 与洗牌千术规则组件；
 * 2. 接收来自上层授信的 seat、controlEpoch 与 now 权威时间戳；
 * 3. 拥有纯走子、准备阶段 (STACKING) 牌山生成物理守恒、倒计时与超时兜底计算；
 * 4. 严禁感知 Socket、网络代次、断线恢复令牌 (resumeToken)、连接端点、订阅流或后台线程池；
 * 5. 严禁委托向旧版 TableHost。
 */
class TableAuthority @JvmOverloads constructor(
    config: MatchConfig = MatchConfig(),
    timeControl: TimeControlConfig = TimeControlConfig(),
    val clock: MonotonicClock = SystemMonotonicClock,
    controllers: List<SeatController> = listOf(
        SeatController.LocalHuman(),
        SeatController.Computer(),
        SeatController.Computer(),
        SeatController.Computer()
    ),
    initialState: GameState? = null,
    val adminSeat: Int = 0,
    initialCoordinator: ApplicationCoordinator? = null,
    val maxReceiptsPerSeat: Int = 8192
) : AutoCloseable {

    // 配置与控制者列表防御性复制
    var config: MatchConfig = sanitizeConfig(config)
        private set

    var timeControl: TimeControlConfig = timeControl
        private set

    private val isInitialStateAlreadyDealt = initialState != null &&
        (initialState.phase != GamePhase.STACKING || initialState.wall.isNotEmpty() || initialState.players.any { it.closedTiles.isNotEmpty() })

    private var hasStarted: Boolean = isInitialStateAlreadyDealt

    private val _controllers = controllers.toMutableList()
    val controllers: List<SeatController> get() = Collections.unmodifiableList(_controllers)

    init {
        this.config.validateSupported()
        require(adminSeat in 0..3) { "adminSeat must be in 0..3, got $adminSeat" }
        require(this._controllers.size == 4) { "controllers list must contain exactly 4 items, got ${this._controllers.size}" }
        require(maxReceiptsPerSeat > 0) { "maxReceiptsPerSeat must be > 0, got $maxReceiptsPerSeat" }
    }

    // 内部应用协调器（默认关闭同步 AI 与自动摸切，启用延迟自动动作）
    internal val coordinator: ApplicationCoordinator = initialCoordinator ?: run {
        val baseState = if (initialState != null) {
            initialState.deepCopy()
        } else {
            val s = GameState()
            for (i in 0 until 4) {
                s.scores[i] = this.config.startingScores[i]
            }
            s.roundWind = this.config.winds.firstOrNull() ?: 1
            s.dealerIndex = 0
            s.currentPlayer = 0
            s.phase = GamePhase.STACKING
            if (this.config.cheatEconomy.enabled) {
                for (i in 0 until 4) {
                    s.cheatPoints[i] = this.config.cheatEconomy.initialPoints.getOrElse(i) { 0 }
                }
            }
            s
        }
        val coord = ApplicationCoordinator(
            initialState = baseState,
            matchProgression = MatchProgression(this.config.toRuleConfig()),
            autoPlayAi = false,
            autoRiichiDiscard = false,
            deferAutomaticActions = true,
            shuffleConfig = this.config.shuffleConfig,
            cheatEconomyConfig = this.config.cheatEconomy,
            sleeveBloomConfig = this.config.sleeveBloomConfig,
            beamSwapConfig = this.config.beamSwapConfig
        )
        if (!coord.hasControllerConfiguration) {
            coord.initializeMatch(this._controllers)
        }
        coord
    }

    // 纯 JVM 权威决策时钟
    var decisionClock: DecisionClock = DecisionClock(config = timeControl, clock = clock)
        private set

    @Volatile
    private var isClosed: Boolean = false

    // 幂等请求与回执存储
    private data class ReceiptKey(val seat: Int, val requestId: String)
    private enum class SubmissionKind {
        CHOICE,
        TILE_MARK,
        SLEEVE_BLOOM,
        SLEEVE_BLOOM_CHOICE,
        SLEEVE_BLOOM_OFF_TURN,
        SLEEVE_BLOOM_OFF_TURN_CHOICE,
        BEAM_SWAP,
        BEAM_SWAP_CHOICE
    }
    private data class StoredSubmission(
        val kind: SubmissionKind,
        val receipt: ActionReceipt,
        val choice: ActionChoice? = null,
        val tileMarkPayload: TileMarkSubmissionHandler.NormalizedPayload? = null,
        val sleeveBloomPayload: SleeveBloomSubmissionHandler.NormalizedActivationPayload? = null,
        val sleeveBloomChoicePayload: SleeveBloomSubmissionHandler.NormalizedChoicePayload? = null,
        val sleeveBloomOffTurnPayload: SleeveBloomSubmissionHandler.NormalizedOffTurnActivationPayload? = null,
        val sleeveBloomOffTurnChoicePayload: SleeveBloomSubmissionHandler.NormalizedOffTurnChoicePayload? = null,
        val beamSwapPayload: BeamSwapSubmissionHandler.NormalizedActivationPayload? = null,
        val beamSwapChoicePayload: BeamSwapSubmissionHandler.NormalizedChoicePayload? = null
    )
    private val perSeatReceiptCounts = IntArray(4)

    private val storedReceipts = object : ConcurrentHashMap<ReceiptKey, StoredSubmission>() {
        override fun put(key: ReceiptKey, value: StoredSubmission): StoredSubmission? {
            val prev = super.put(key, value)
            if (prev == null && key.seat in 0..3) {
                perSeatReceiptCounts[key.seat]++
            }
            return prev
        }

        override fun clear() {
            super.clear()
            for (i in 0..3) perSeatReceiptCounts[i] = 0
        }
    }

    /** MR-09b 回执库只读测量指标（近似估算，仅用于量级对比） */
    data class ReceiptStoreMetrics(
        val currentEntries: Int,
        val rejectedEntries: Int,
        val recordedEntries: Int,
        val appliedEntries: Int,
        val perSeatEntryCounts: List<Int>,
        val approxRetainedBytes: Long
    )

    /**
     * MR-09b 测量入口：只读遍历 storedReceipts，统计条目构成与近似保留字节。
     * 估算口径（粗粒度，不作绝对内存承诺）：对象头 16B；int/long/引用按
     * 4/8/8B；字符串 24B + 2×length；枚举/可空字段按引用计；载荷对象按
     * 其字段同法累加，不计共享驻留与 JVM 压缩。线程安全：ConcurrentHashMap
     * 弱一致遍历，仅用于权威线程外的诊断读取。
     */
    internal fun receiptStoreMetrics(): ReceiptStoreMetrics {
        var rejected = 0
        var recorded = 0
        var applied = 0
        val perSeat = IntArray(4)
        var bytes = 0L
        for ((key, value) in storedReceipts) {
            perSeat[key.seat]++
            when (value.receipt.status) {
                ReceiptStatus.REJECTED -> rejected++
                ReceiptStatus.RECORDED -> recorded++
                ReceiptStatus.APPLIED -> applied++
            }
            bytes += approxReceiptEntryBytes(key, value)
        }
        return ReceiptStoreMetrics(
            currentEntries = storedReceipts.size,
            rejectedEntries = rejected,
            recordedEntries = recorded,
            appliedEntries = applied,
            perSeatEntryCounts = perSeat.toList(),
            approxRetainedBytes = bytes
        )
    }

    private fun approxReceiptEntryBytes(key: ReceiptKey, value: StoredSubmission): Long {
        var bytes = 16L + 4L + 8L                       // ReceiptKey 头+seat+引用
        bytes += 24L + 2L * key.requestId.length        // requestId 字符串
        bytes += 16L + 8L * 3                           // StoredSubmission 头+固定引用
        val receipt = value.receipt
        bytes += 16L + 8L + 4L + 8L + 8L                // ActionReceipt 头+四字段
        bytes += 24L + 2L * receipt.requestId.length
        receipt.reason?.let { bytes += 24L + 2L * it.length }
        value.choice?.let { bytes += 16L + 8L * 3 + 24L + 2L * it.requestId.length }
        value.tileMarkPayload?.let { bytes += 16L + 8L * 3 + 24L + 2L * it.sessionId.length }
        value.sleeveBloomPayload?.let { bytes += 16L + 8L * 3 }
        value.sleeveBloomChoicePayload?.let { bytes += 16L + 8L * 5 + 24L + 2L * it.offerId.length }
        value.sleeveBloomOffTurnPayload?.let { bytes += 16L + 8L * 3 + 24L + 2L * it.windowId.length }
        value.sleeveBloomOffTurnChoicePayload?.let { bytes += 16L + 8L * 4 + 24L + 2L * it.offerId.length }
        value.beamSwapPayload?.let { bytes += 16L + 8L * 3 }
        value.beamSwapChoicePayload?.let { bytes += 16L + 8L * 5 + 24L + 2L * it.offerId.length }
        return bytes
    }

    // 历史单局结算事件记录 (roundId -> visibleEvents)
    private val roundResultEvents = ConcurrentHashMap<String, List<GameEvent>>()
    private var lastActiveRoundId: String? = null
    private var pausedAuthorityTimeMillis: Long? = null
    private var lastCompletedRoundId: String? = null
    private var lastCompletedSettlementEvents: List<GameEvent> = emptyList()

    // D-09：局内千术与码牌的 internal 专用处理器。处理器不持有权威入口/协调器/牌局状态。
    private val tileMarkHandler = TileMarkSubmissionHandler()
    private val sleeveBloomHandler = SleeveBloomSubmissionHandler()
    private val beamSwapHandler = BeamSwapSubmissionHandler()
    private val shufflePreparationHandler = ShufflePreparationHandler()

    private data class ShuffleReceiptKey(val seat: Int, val roundId: String, val requestId: String)
    private data class StoredShufflePlan(val request: ShufflePlanRequest, val receipt: ActionReceipt)
    private val perSeatShufflePlanCounts = IntArray(4)

    private val storedShufflePlans = object : ConcurrentHashMap<ShuffleReceiptKey, StoredShufflePlan>() {
        override fun put(key: ShuffleReceiptKey, value: StoredShufflePlan): StoredShufflePlan? {
            val prev = super.put(key, value)
            if (prev == null && key.seat in 0..3) {
                perSeatShufflePlanCounts[key.seat]++
            }
            return prev
        }

        override fun clear() {
            super.clear()
            for (i in 0..3) perSeatShufflePlanCounts[i] = 0
        }
    }

    /**
     * 获取指定席位当前已接纳的回执总数（含 storedReceipts 与 storedShufflePlans）
     */
    internal fun getSeatReceiptCount(seat: Int): Int {
        if (seat !in 0..3) return 0
        return perSeatReceiptCounts[seat] + perSeatShufflePlanCounts[seat]
    }

    internal fun isReceiptQuotaExceeded(seat: Int): Boolean {
        if (seat !in 0..3) return false
        return getSeatReceiptCount(seat) >= maxReceiptsPerSeat
    }

    // 权威只读门面：收拢对外暴露，不对外暴露协调器及可变Session
    val sessionId: String get() = coordinator.session.sessionId
    val sessionLifecycle: SessionLifecycle get() = coordinator.session.lifecycle
    val currentDealer: Int get() = coordinator.session.currentDealer
    fun controlEpochFor(seat: Int): Long = coordinator.epochFor(seat)

    val currentRoundId: String? get() = coordinator.activeRound?.roundId
    val currentRoundVersion: Long? get() = coordinator.activeRound?.version
    val isPaused: Boolean get() = decisionClock.isPausedState
    val hasActiveRoundNotEnded: Boolean
        get() {
            val round = coordinator.activeRound
            return round != null && round.state.phase != GamePhase.ROUND_END && !isPreparationActive()
        }

    @Synchronized
    fun initializeInitialClocks(): Boolean {
        val round = coordinator.activeRound
        if (round != null && round.state.phase != GamePhase.ROUND_END && !isPreparationActive()) {
            decisionClock.beginRound(round.roundId)
            lastActiveRoundId = round.roundId
            syncDecisionsAndClocks(clock.nowMillis(), emptySet())
            return true
        }
        return false
    }

    init {
        val round = this.coordinator.activeRound
        if (round != null && round.state.phase != GamePhase.ROUND_END) {
            if (this.config.shuffleConfig.enabled && !isInitialStateAlreadyDealt) {
                // 等待 startSession 开启洗牌准备阶段
            } else {
                decisionClock.beginRound(round.roundId)
                lastActiveRoundId = round.roundId
                syncDecisionsAndClocks(clock.nowMillis(), emptySet())
            }
        }
    }

    // ========================================
    // 变更与执行结果包装
    // ========================================

    data class MutationResult(
        val receipt: ActionReceipt,
        val events: List<GameEvent>,
        val stateChanged: Boolean
    )

    sealed class AutoStepResult {
        data class PreparationDealt(val events: List<GameEvent>) : AutoStepResult()
        data class TimeoutResolved(val seat: Int, val action: GameAction, val events: List<GameEvent>) : AutoStepResult()
        data class DealerChoiceTaken(val dealer: Int, val action: GameAction, val events: List<GameEvent>) : AutoStepResult()
        data class NextRoundStarted(val events: List<GameEvent>) : AutoStepResult()
        data class RiichiAutoDiscarded(val seat: Int, val action: GameAction, val events: List<GameEvent>) : AutoStepResult()
        data class SleeveBloomOffTurnExpired(val seats: List<Int>, val events: List<GameEvent>) : AutoStepResult()

        val allEvents: List<GameEvent> get() = when (this) {
            is PreparationDealt -> events
            is TimeoutResolved -> events
            is DealerChoiceTaken -> events
            is NextRoundStarted -> events
            is RiichiAutoDiscarded -> events
            is SleeveBloomOffTurnExpired -> events
        }
    }

    data class AiDecisionRequest(
        val seat: Int,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long,
        val view: PlayerView,
        val legalActions: List<GameAction>
    )

    // ========================================
    // 准备阶段判定与视图
    // ========================================

    fun isPreparationActive(): Boolean {
        val activeRound = coordinator.activeRound
        return shufflePreparationHandler.isActive(
            closed = isClosed,
            sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
            activeRoundId = activeRound?.roundId,
            phase = activeRound?.state?.phase,
            wallEmpty = activeRound?.state?.wall?.isEmpty() == true
        )
    }

    fun preparationDeadline(): Long = shufflePreparationHandler.deadlineMillis()

    fun buildPreparationViewForSeat(seat: Int): ShufflePreparationView? {
        if (!isPreparationActive()) return null
        val roundId = shufflePreparationHandler.currentRoundId() ?: return null
        val round = coordinator.activeRound
        val record = if (round != null && round.roundId == roundId) round.preparationRecord else null

        val now = if (decisionClock.isPausedState && pausedAuthorityTimeMillis != null) {
            pausedAuthorityTimeMillis!!
        } else {
            clock.nowMillis()
        }
        val currentPoints = if (config.cheatEconomy.enabled) {
            if (round != null) {
                val state = round.readState()
                if (state.cheatPoints.size > seat) {
                    state.cheatPoints[seat]
                } else {
                    coordinator.session.betweenRoundCheatPoints.getOrElse(seat) { 0 }
                }
            } else {
                coordinator.session.betweenRoundCheatPoints.getOrElse(seat) { 0 }
            }
        } else {
            0
        }
        return shufflePreparationHandler.buildView(seat, record, now, currentPoints)
    }

    private fun startPreparation(roundId: String, now: Long) {
        val record = shufflePreparationHandler.start(roundId, now, config.shuffleConfig)
        coordinator.activeRound?.let { round ->
            if (round.roundId == roundId) {
                round.updatePreparationRecord(record)
            }
        }
    }

    // ========================================
    // 纯同步玩法操作
    // ========================================

    @Synchronized
    fun start(seat: Int, now: Long): MutationResult {
        if (decisionClock.isPausedState) {
            return MutationResult(ActionReceipt("start", ReceiptStatus.REJECTED, reason = "HOST_PAUSED"), emptyList(), false)
        }
        if (seat != adminSeat) {
            return MutationResult(ActionReceipt("start", ReceiptStatus.REJECTED, reason = "PERMISSION_DENIED"), emptyList(), false)
        }
        if (coordinator.session.lifecycle == SessionLifecycle.FINISHED) {
            return MutationResult(ActionReceipt("start", ReceiptStatus.REJECTED, reason = "MATCH_FINISHED"), emptyList(), false)
        }

        val round = coordinator.activeRound
        val isAlreadyDealt = round != null && (round.readState().phase != GamePhase.STACKING || round.readState().wall.isNotEmpty())
        if (isAlreadyDealt) {
            hasStarted = true
            return MutationResult(ActionReceipt("start", ReceiptStatus.APPLIED), emptyList(), false)
        }

        if (config.shuffleConfig.enabled && !isInitialStateAlreadyDealt) {
            if (round != null) {
                if (isPreparationActive()) {
                    return MutationResult(ActionReceipt("start", ReceiptStatus.APPLIED), emptyList(), false)
                }
                hasStarted = true
                startPreparation(round.roundId, now)
                return MutationResult(ActionReceipt("start", ReceiptStatus.APPLIED), emptyList(), true)
            }
        }

        val result = coordinator.start()
        if (result is ActionResult.Accepted) {
            hasStarted = true
            if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
                val newRound = coordinator.activeRound
                if (newRound != null) {
                    if (config.shuffleConfig.enabled && !isInitialStateAlreadyDealt) {
                        startPreparation(newRound.roundId, now)
                    } else {
                        decisionClock.beginRound(newRound.roundId)
                        lastActiveRoundId = newRound.roundId
                    }
                }
                recordSettlementEvents(result.events)
            }
            return MutationResult(ActionReceipt("start", ReceiptStatus.APPLIED), result.events, true)
        } else {
            val reason = if (result is ActionResult.Rejected) result.reason.name else "START_FAILED"
            return MutationResult(ActionReceipt("start", ReceiptStatus.REJECTED, reason = reason), emptyList(), false)
        }
    }

    fun validateNextRoundPreconditions(): ActionReceipt? {
        if (decisionClock.isPausedState) {
            return ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "HOST_PAUSED")
        }
        if (coordinator.session.lifecycle == SessionLifecycle.FINISHED) {
            return ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "MATCH_FINISHED")
        }
        if (coordinator.session.lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE) {
            return ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "WAITING_DEALER_CHOICE")
        }
        if (coordinator.session.lifecycle == SessionLifecycle.CREATED || coordinator.session.lifecycle == SessionLifecycle.WAITING_START) {
            return ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "SESSION_NOT_STARTED")
        }
        if (isPreparationActive()) {
            return ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "PREPARATION_ACTIVE")
        }
        if (coordinator.activeRound != null && coordinator.activeRound?.state?.phase != GamePhase.ROUND_END) {
            return ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = "ROUND_ACTIVE")
        }
        return null
    }

    @Synchronized
    fun nextRound(seat: Int, now: Long): MutationResult {
        val preError = validateNextRoundPreconditions()
        if (preError != null) {
            return MutationResult(preError, emptyList(), false)
        }

        val result = coordinator.nextRound()
        if (result is ActionResult.Accepted) {
            hasStarted = true
            if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
                val newRound = coordinator.activeRound
                if (newRound != null) {
                    if (config.shuffleConfig.enabled) {
                        startPreparation(newRound.roundId, now)
                    } else {
                        decisionClock.beginRound(newRound.roundId)
                        lastActiveRoundId = newRound.roundId
                    }
                }
                recordSettlementEvents(result.events)
            }
            return MutationResult(ActionReceipt("nextRound", ReceiptStatus.APPLIED), result.events, true)
        } else {
            val reason = if (result is ActionResult.Rejected) result.reason.name else "NEXT_ROUND_FAILED"
            return MutationResult(ActionReceipt("nextRound", ReceiptStatus.REJECTED, reason = reason), emptyList(), false)
        }
    }

    @Synchronized
    fun submitChoice(seat: Int, choice: ActionChoice, now: Long): MutationResult {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            return MutationResult(ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED"), emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            return MutationResult(ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED"), emptyList(), false)
        }
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        // 1. 幂等与重试校验
        val receiptKey = ReceiptKey(seat, choice.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.CHOICE && stored.choice == choice) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == choice.requestId }) {
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isPreparationActive()) {
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, rejected, choice = choice)
            return MutationResult(rejected, emptyList(), false)
        }

        // 2. 当前有效决策与代次校验
        val currentDecision = coordinator.queryDecision(seat)
        if (choice.controlEpoch != currentDecision.controlEpoch) {
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "STALE_CONTROL_EPOCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, rejected, choice = choice)
            return MutationResult(rejected, emptyList(), false)
        }

        if (currentDecision.decisionId != choice.decisionId || choice.roundId != currentDecision.roundId) {
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, rejected, choice = choice)
            return MutationResult(rejected, emptyList(), false)
        }

        val selectedOption = currentDecision.options.firstOrNull { it.optionId == choice.optionId }
        if (selectedOption == null) {
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "OPTION_NOT_FOUND")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, rejected, choice = choice)
            return MutationResult(rejected, emptyList(), false)
        }

        // 3. 权威截止时间校验
        val timing = decisionClock.timing(seat)
        val roundId = choice.roundId
        if (roundId != null) {
            val decisionKey = DecisionKey(roundId, seat, choice.decisionId)
            if (timing != null && timing.key == decisionKey) {
                if (now >= timing.deadlineMillis) {
                    val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")
                    storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, rejected, choice = choice)
                    return MutationResult(rejected, emptyList(), false)
                }
            }
        }

        // 4. 提交执行动作
        val actionResult = coordinator.submitChoice(seat, choice)
        if (actionResult !is ActionResult.Accepted) {
            val reasonStr = when (actionResult) {
                is ActionResult.Rejected -> actionResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(choice.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, rejected, choice = choice)
            return MutationResult(rejected, emptyList(), false)
        }

        // 5. 成功执行后结算计时与状态判断
        val round = coordinator.activeRound
        val window = round?.responseWindow
        val isWindowWaiting = window != null && !window.isComplete
        val status = if (isWindowWaiting) ReceiptStatus.RECORDED else ReceiptStatus.APPLIED
        val receipt = ActionReceipt(choice.requestId, status)

        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            if (roundId != null && timing != null && timing.key == DecisionKey(roundId, seat, choice.decisionId)) {
                decisionClock.complete(timing.key, now)
            }

            if (round != null && window == null) {
                cancelUnsubmittedTimings(round.roundId, now)
            }

            recordSettlementEvents(actionResult.events)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.CHOICE, receipt, choice = choice)
        }

        return MutationResult(receipt, actionResult.events, true)
    }

    @Synchronized
    fun submitShufflePlan(seat: Int, request: ShufflePlanRequest, now: Long): MutationResult {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED"), emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED"), emptyList(), false)
        }
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }

        val receiptKey = ShuffleReceiptKey(seat, request.roundId, request.requestId)
        val stored = storedShufflePlans[receiptKey]
        if (stored != null) {
            if (stored.request.selection == request.selection && stored.request.controlEpoch == request.controlEpoch) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "CONFLICTING_PAYLOAD"), emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT"), emptyList(), false)
        }
        if (storedReceipts.containsKey(ReceiptKey(seat, request.requestId))) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT"), emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED"), emptyList(), false)
        }

        val currentEpoch = coordinator.epochFor(seat)
        val currentPoints = coordinator.activeRound?.state?.cheatPoints?.get(seat) ?: 0
        when (val check = shufflePreparationHandler.validatePlan(
            seat = seat,
            request = request,
            currentEpoch = currentEpoch,
            now = now,
            currentPoints = currentPoints,
            active = isPreparationActive(),
            activeRoundId = coordinator.activeRound?.roundId
        )) {
            is ShufflePreparationHandler.PlanCheck.Rejected ->
                return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = check.reason), emptyList(), false)
            is ShufflePreparationHandler.PlanCheck.Accepted -> {
                val record = shufflePreparationHandler.recordPlan(seat, check.selection, check.cost)
                coordinator.activeRound?.let { round ->
                    if (round.roundId == record.roundId) {
                        round.updatePreparationRecord(record)
                    }
                }
            }
        }

        val receipt = ActionReceipt(request.requestId, ReceiptStatus.RECORDED)
        storedShufflePlans[receiptKey] = StoredShufflePlan(request.deepCopy(), receipt)
        return MutationResult(receipt, emptyList(), true)
    }

    @Synchronized
    fun submitAiChoice(
        seat: Int,
        roundId: String,
        decisionId: String,
        controlEpoch: Long,
        action: GameAction,
        now: Long
    ): MutationResult {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            return MutationResult(ActionReceipt(decisionId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED"), emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            return MutationResult(ActionReceipt(decisionId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED"), emptyList(), false)
        }
        val currTiming = decisionClock.timing(seat)
        val currDecision = coordinator.queryDecision(seat)

        val matchesRound = roundId == (currDecision.roundId ?: "session")
        val matchesDecision = decisionId == currDecision.decisionId
        val matchesEpoch = controlEpoch == currDecision.controlEpoch
        val matchesTiming = currTiming != null && currTiming.key == DecisionKey(roundId, seat, decisionId)
        val notExpired = currTiming != null && now < currTiming.deadlineMillis
        val validAction = currDecision.options.any { it.action == action }

        if (!matchesRound || !matchesDecision || !matchesEpoch || !matchesTiming || !notExpired || !validAction) {
            return MutationResult(ActionReceipt(decisionId, ReceiptStatus.REJECTED, reason = "STALE_AI_SUBMISSION"), emptyList(), false)
        }

        val actionResult = coordinator.handleAction(action)
        if (actionResult is ActionResult.Accepted) {
            if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
                decisionClock.complete(currTiming.key, now)
                val round = coordinator.activeRound
                val win = round?.responseWindow
                if (round != null && win == null) {
                    cancelUnsubmittedTimings(round.roundId, now)
                }
                recordSettlementEvents(actionResult.events)
            }
            return MutationResult(ActionReceipt(decisionId, ReceiptStatus.APPLIED), actionResult.events, true)
        }
        return MutationResult(ActionReceipt(decisionId, ReceiptStatus.REJECTED, reason = "ACTION_REJECTED"), emptyList(), false)
    }

    @Synchronized
    fun applyCheatLedgerEntry(entryId: String, seat: Int, amount: Int, reason: String, now: Long): MutationResult {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            return MutationResult(ActionReceipt(entryId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED"), emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            return MutationResult(ActionReceipt(entryId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED"), emptyList(), false)
        }
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(entryId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (isPreparationActive() && amount < 0) {
            val currentPoints = coordinator.queryCheatPoints(seat)
            val reserved = shufflePreparationHandler.reservedFor(seat)
            if (currentPoints + amount < reserved) {
                return MutationResult(ActionReceipt(entryId, ReceiptStatus.REJECTED, reason = "INSUFFICIENT_FUNDS"), emptyList(), false)
            }
        }

        val timingsBefore = (0 until 4).mapNotNull { s -> decisionClock.timing(s) }
        val result = coordinator.applyCheatLedgerEntry(entryId, seat, amount, reason)
        if (result is ActionResult.Accepted) {
            if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
                for (timing in timingsBefore) {
                    val newDecision = coordinator.queryDecision(timing.key.seat)
                    val newRoundId = newDecision.roundId
                    if (newRoundId != null && newRoundId == timing.key.roundId && newDecision.options.isNotEmpty()) {
                        val newKey = DecisionKey(newRoundId, timing.key.seat, newDecision.decisionId)
                        if (newKey != timing.key) {
                            decisionClock.rekey(timing.key, newKey)
                        }
                    }
                }
                recordSettlementEvents(result.events)
            }
            return MutationResult(ActionReceipt(entryId, ReceiptStatus.APPLIED), result.events, true)
        } else {
            val reasonStr = if (result is ActionResult.Rejected) result.reason.name else "REJECTED"
            return MutationResult(ActionReceipt(entryId, ReceiptStatus.REJECTED, reason = reasonStr), emptyList(), false)
        }
    }

    @Synchronized
    fun submitTileMark(
        seat: Int,
        request: TileMarkRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (!config.tileMarkConfig.enabled) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "TILE_MARK_DISABLED"), emptyList(), false)
        }
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED"), emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED"), emptyList(), false)
        }
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = tileMarkHandler.normalize(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.TILE_MARK && stored.tileMarkPayload == currentPayload) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED")
            return MutationResult(rejected, emptyList(), false)
        }

        val round = coordinator.activeRound
        val currentDecision = coordinator.queryDecision(seat)
        val controller = coordinator.controllerFor(seat)
        val timing = decisionClock.timing(seat)
        val candidateState = round?.readState()
        val seatGroups = coordinator.session.tileMarkState.groupsForSeat(seat)
        val seatMarkedIds = seatGroups.flatMap { it.tileIds }.toSet()
        val cost = TileMarkCost.of(config.cheatEconomy)
        val handTileIds = candidateState?.players?.getOrNull(seat)?.let { player ->
            (player.closedTiles + listOfNotNull(player.drawnTile)).map { it.tileId }.toSet()
        } ?: emptySet()

        val check = tileMarkHandler.validate(
            TileMarkSubmissionHandler.Input(
                seat = seat,
                request = request,
                currentSessionId = coordinator.session.sessionId,
                roundId = round?.roundId,
                phase = candidateState?.phase,
                responseWindowActive = round?.responseWindow != null,
                preparationActive = isPreparationActive(),
                currentPlayer = candidateState?.currentPlayer,
                decision = currentDecision,
                controller = controller,
                timing = timing,
                now = now,
                riichiDeclared = candidateState?.riichiDeclared?.getOrNull(seat) ?: false,
                sleeveBloomOfferSeat = candidateState?.sleeveBloomOffer?.seat,
                sleeveBloomOffTurnOfferSeat = candidateState?.sleeveBloomOffTurnOffer(seat)?.seat,
                beamSwapOfferSeat = candidateState?.beamSwapOffer?.seat,
                handTileIds = handTileIds,
                markedTileIds = seatMarkedIds,
                existingGroupCount = seatGroups.size,
                cheatPoints = candidateState?.cheatPoints?.getOrNull(seat) ?: 0,
                cost = cost,
                allowAutomated = allowAutomated
            )
        )
        val rejection = check.rejectionReason
        if (rejection != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = rejection)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 6. 执行原子扣费与安装标记
        val coordResult = coordinator.submitTileMark(seat, request)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 7. 计时 rekey 延续（保留截止时间与储备）
        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            val newDecision = coordinator.queryDecision(seat)
            val activeRoundId = checkNotNull(round).roundId
            val timingKey = checkNotNull(timing).key
            val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
            decisionClock.rekey(timingKey, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, receipt, tileMarkPayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交袖里藏花激活请求
     */
    @Synchronized
    fun submitSleeveBloom(
        seat: Int,
        request: SleeveBloomRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = sleeveBloomHandler.normalizeActivation(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.SLEEVE_BLOOM && stored.sleeveBloomPayload == currentPayload) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED")
            return MutationResult(rejected, emptyList(), false)
        }

        val round = coordinator.activeRound
        val currentDecision = coordinator.queryDecision(seat)
        val controller = coordinator.controllerFor(seat)
        val timing = decisionClock.timing(seat)
        val candidateState = round?.readState()
        val drawnTile = candidateState?.players?.getOrNull(seat)?.drawnTile
        val rinshanPointer = candidateState?.rinshanPointer ?: 0
        val nextTile = candidateState?.nextAvailableWallTile()
        val previousDeadWallTile = candidateState?.deadWall?.getOrNull(rinshanPointer - 1)
        val nextDeadWallTile = candidateState?.deadWall?.getOrNull(rinshanPointer)
        val cost = config.cheatEconomy.costs["SLEEVE_BLOOM"] ?: 10
        val check = sleeveBloomHandler.validateActivation(
            SleeveBloomSubmissionHandler.ActivationInput(
                seat = seat,
                request = request,
                featureEnabled = config.sleeveBloomConfig.enabled,
                economyEnabled = config.cheatEconomy.enabled,
                configuredCost = config.cheatEconomy.costs["SLEEVE_BLOOM"],
                closed = isClosed,
                sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
                paused = decisionClock.isPausedState,
                preparationActive = isPreparationActive(),
                roundId = round?.roundId,
                roundEnded = candidateState?.phase == GamePhase.ROUND_END,
                decision = currentDecision,
                controller = controller,
                timing = timing,
                now = now,
                currentPlayer = candidateState?.currentPlayer,
                phase = candidateState?.phase,
                responseWindowActive = round?.responseWindow != null,
                drawnTileId = drawnTile?.tileId,
                drawnTileInHand = drawnTile != null &&
                        candidateState.tilePool.getLocation(drawnTile) == com.majiang.model.TileLocation.hand(seat),
                lastDrawKind = candidateState?.lastDrawKind ?: SleeveBloomDrawKind.DEAL_EXTRA,
                riichiDeclared = candidateState?.riichiDeclared?.getOrNull(seat) ?: false,
                sleeveBloomOfferActive = candidateState?.sleeveBloomOffer != null,
                offTurnOfferActive = candidateState?.sleeveBloomOffTurnOffer(seat) != null,
                beamSwapOfferActive = candidateState?.beamSwapOffer != null,
                sleeveBloomUsedOnTurn = candidateState?.sleeveBloomUsedOnTurn ?: -1,
                beamSwapUsedOnTurn = candidateState?.beamSwapUsedOnTurn ?: -1,
                turnNumber = candidateState?.turnNumber ?: -1,
                wallRemaining = candidateState?.wallRemaining ?: 0,
                wallPointer = candidateState?.wallPointer ?: 0,
                wallEndExclusive = candidateState?.wallEndExclusive ?: 0,
                nextWallTileId = nextTile?.tileId,
                nextWallTileInWall = nextTile != null &&
                        candidateState.tilePool.getLocation(nextTile) == com.majiang.model.TileLocation.WALL,
                rinshanPointer = rinshanPointer,                totalKanCount = candidateState?.totalKanCount ?: 0,
                deadWallPreviousTileId = previousDeadWallTile?.tileId,
                nextDeadWallTileId = nextDeadWallTile?.tileId,
                nextDeadWallInDeadWall = nextDeadWallTile != null &&
                        candidateState.tilePool.getLocation(nextDeadWallTile) == com.majiang.model.TileLocation.DEAD_WALL,
                cheatPoints = candidateState?.cheatPoints?.getOrNull(seat) ?: 0,
                cost = cost,
                allowAutomated = allowAutomated
            )
        )
        val rejection = check.rejectionReason
        if (rejection != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = rejection)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 6. 执行原子扣费与安装 offer
        val coordResult = coordinator.submitSleeveBloom(seat, request, now, allowAutomated)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 7. 计时 rekey 延续（保留截止时间与储备）
        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            val newDecision = coordinator.queryDecision(seat)
            val activeRoundId = checkNotNull(round).roundId
            val timingKey = checkNotNull(timing).key
            val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
            decisionClock.rekey(timingKey, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, receipt, sleeveBloomPayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交袖里藏花二选一决策请求
     */
    @Synchronized
    fun submitSleeveBloomChoice(
        seat: Int,
        request: SleeveBloomChoiceRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = sleeveBloomHandler.normalizeChoice(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.SLEEVE_BLOOM_CHOICE && stored.sleeveBloomChoicePayload == currentPayload) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED")
            return MutationResult(rejected, emptyList(), false)
        }

        val round = coordinator.activeRound
        val currentDecision = coordinator.queryDecision(seat)
        val controller = coordinator.controllerFor(seat)
        val timing = decisionClock.timing(seat)
        val candidateState = round?.readState()
        val offer = candidateState?.sleeveBloomOffer
        val drawnTile = candidateState?.players?.getOrNull(seat)?.drawnTile
        val rinshanPointer = candidateState?.rinshanPointer ?: 0
        val offerSecondWallIndex = offer?.let { candidateState?.wallTileIndex(it.secondTile) }
            ?.takeIf { it >= 0 }
        val offerSecondTile = offerSecondWallIndex?.let { candidateState?.wall?.getOrNull(it) }
        val previousDeadWallTile = candidateState?.deadWall?.getOrNull(rinshanPointer - 1)
        val secondDeadWallTile = candidateState?.deadWall?.getOrNull(rinshanPointer)
        val check = sleeveBloomHandler.validateChoice(
            SleeveBloomSubmissionHandler.ChoiceInput(
                seat = seat,
                request = request,
                featureEnabled = config.sleeveBloomConfig.enabled,
                economyEnabled = config.cheatEconomy.enabled,
                configuredCost = config.cheatEconomy.costs["SLEEVE_BLOOM"],
                closed = isClosed,
                sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
                paused = decisionClock.isPausedState,
                preparationActive = isPreparationActive(),
                roundId = round?.roundId,
                roundEnded = candidateState?.phase == GamePhase.ROUND_END,
                decision = currentDecision,
                controller = controller,
                timing = timing,
                now = now,
                currentPlayer = candidateState?.currentPlayer,
                phase = candidateState?.phase,
                responseWindowActive = round?.responseWindow != null,
                turnNumber = candidateState?.turnNumber ?: -1,
                drawnTileId = drawnTile?.tileId,
                drawnTileInHand = drawnTile != null &&
                        candidateState.tilePool.getLocation(drawnTile) == com.majiang.model.TileLocation.hand(seat),
                offerSeat = offer?.seat,
                offerId = offer?.offerId,
                offerMode = offer?.mode,
                offerCreatedTurnNumber = offer?.createdTurnNumber,
                offerFirstTileId = offer?.firstTile?.tileId,
                offerSecondTileId = offer?.secondTile?.tileId,
                offerDrawKind = offer?.drawKind,
                offerSecondWallIndex = offerSecondWallIndex,
                nextAvailableWallIndex = candidateState?.nextAvailableWallIndex(),
                wallPointer = candidateState?.wallPointer ?: 0,
                wallEndExclusive = candidateState?.wallEndExclusive ?: 0,
                wallTileAtOfferIndexInWall = offerSecondTile != null &&
                        candidateState?.tilePool?.getLocation(offerSecondTile) == com.majiang.model.TileLocation.WALL,
                rinshanPointer = rinshanPointer,
                totalKanCount = candidateState?.totalKanCount ?: 0,
                deadWallPreviousTileId = previousDeadWallTile?.tileId,
                deadWallSecondTileId = secondDeadWallTile?.tileId,
                deadWallSecondInDeadWall = secondDeadWallTile != null &&
                        candidateState.tilePool.getLocation(secondDeadWallTile) == com.majiang.model.TileLocation.DEAD_WALL,
                allowAutomated = allowAutomated
            )
        )
        when (check) {
            is CheatSubmissionCheck.Rejected -> {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = check.reason)
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
            is CheatSubmissionCheck.StaleOffer -> {
                coordinator.cleanStaleSleeveBloomOffer(seat)
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = check.reason)
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
                val newDecision = coordinator.queryDecision(seat)
                val activeRoundId = checkNotNull(round).roundId
                val timingKey = checkNotNull(timing).key
                val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
                decisionClock.rekey(timingKey, newKey)
                return MutationResult(rejected, emptyList(), true)
            }
            CheatSubmissionCheck.Allowed -> Unit
        }

        // 6. 执行选择决策
        val coordResult = coordinator.submitSleeveBloomChoice(seat, request, allowAutomated)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 7. 计时 rekey 延续（保留截止时间与储备）
        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            val newDecision = coordinator.queryDecision(seat)
            val activeRoundId = checkNotNull(round).roundId
            val timingKey = checkNotNull(timing).key
            val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
            decisionClock.rekey(timingKey, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, receipt, sleeveBloomChoicePayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交他家回合预定袖里藏花激活请求。
     *
     * 该请求不依赖任何即时 decisionId，以本人弃牌后签发的 windowId 与控制代次授权。
     */
    @Synchronized
    fun submitSleeveBloomOffTurn(
        seat: Int,
        request: com.majiang.cheat.bloom.SleeveBloomOffTurnRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }
        val currentPayload = sleeveBloomHandler.normalizeOffTurnActivation(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.SLEEVE_BLOOM_OFF_TURN &&
                stored.sleeveBloomOffTurnPayload == currentPayload
            ) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            return MutationResult(
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT"),
                emptyList(),
                false
            )
        }
        if (isReceiptQuotaExceeded(seat)) {
            return MutationResult(
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED"),
                emptyList(),
                false
            )
        }

        val round = coordinator.activeRound
        val controller = coordinator.controllerFor(seat)
        val candidateState = round?.readState()
        val cost = config.cheatEconomy.costs["SLEEVE_BLOOM"] ?: 10
        val check = sleeveBloomHandler.validateOffTurnActivation(
            SleeveBloomSubmissionHandler.OffTurnActivationInput(
                seat = seat,
                request = request,
                featureEnabled = config.sleeveBloomConfig.enabled,
                economyEnabled = config.cheatEconomy.enabled,
                configuredCost = config.cheatEconomy.costs["SLEEVE_BLOOM"],
                closed = isClosed,
                sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
                paused = decisionClock.isPausedState,
                preparationActive = isPreparationActive(),
                roundId = round?.roundId,
                roundEnded = candidateState?.phase == GamePhase.ROUND_END,
                controller = controller,
                controlEpoch = coordinator.epochFor(seat),
                currentPlayer = candidateState?.currentPlayer,
                phase = candidateState?.phase,
                windowMatches = candidateState?.sleeveBloomWindowId(seat) == request.windowId,
                immediateOfferActive = candidateState?.sleeveBloomOffer != null,
                offTurnOfferActive = candidateState?.sleeveBloomOffTurnOffer(seat) != null,
                reservationActive = candidateState?.sleeveBloomReservation(seat) != null,
                beamSwapOfferActive = candidateState?.beamSwapOffer != null,
                availableWallCount = candidateState?.availableWallTileCount() ?: 0,
                cheatPoints = candidateState?.cheatPoints?.getOrNull(seat) ?: 0,
                cost = cost,
                allowAutomated = allowAutomated
            )
        )
        val rejection = check.rejectionReason
        if (rejection != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = rejection)
            storedReceipts[receiptKey] = StoredSubmission(
                SubmissionKind.SLEEVE_BLOOM_OFF_TURN,
                rejected,
                sleeveBloomOffTurnPayload = currentPayload
            )
            return MutationResult(rejected, emptyList(), false)
        }

        val coordResult = coordinator.submitSleeveBloomOffTurn(seat, request, now, allowAutomated)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(
                SubmissionKind.SLEEVE_BLOOM_OFF_TURN,
                rejected,
                sleeveBloomOffTurnPayload = currentPayload
            )
            return MutationResult(rejected, emptyList(), false)
        }

        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        storedReceipts[receiptKey] = StoredSubmission(
            SubmissionKind.SLEEVE_BLOOM_OFF_TURN,
            receipt,
            sleeveBloomOffTurnPayload = currentPayload
        )
        recordSettlementEvents(coordResult.events)
        rekeyActiveTimingsPreservingDeadline()
        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交他家回合预定二选一（FIRST / SECOND）。
     */
    @Synchronized
    fun submitSleeveBloomOffTurnChoice(
        seat: Int,
        request: com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }
        val currentPayload = sleeveBloomHandler.normalizeOffTurnChoice(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.SLEEVE_BLOOM_OFF_TURN_CHOICE &&
                stored.sleeveBloomOffTurnChoicePayload == currentPayload
            ) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            return MutationResult(
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT"),
                emptyList(),
                false
            )
        }
        if (isReceiptQuotaExceeded(seat)) {
            return MutationResult(
                ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED"),
                emptyList(),
                false
            )
        }

        val round = coordinator.activeRound
        val controller = coordinator.controllerFor(seat)
        val candidateState = round?.readState()
        val offer = candidateState?.sleeveBloomOffTurnOffer(seat)
        val firstInWall = offer != null &&
            candidateState.tilePool.getLocation(offer.firstTile) == com.majiang.model.TileLocation.WALL
        val secondInWall = offer != null &&
            candidateState.tilePool.getLocation(offer.secondTile) == com.majiang.model.TileLocation.WALL
        val cost = config.cheatEconomy.costs["SLEEVE_BLOOM"] ?: 10
        val check = sleeveBloomHandler.validateOffTurnChoice(
            SleeveBloomSubmissionHandler.OffTurnChoiceInput(
                seat = seat,
                request = request,
                featureEnabled = config.sleeveBloomConfig.enabled,
                economyEnabled = config.cheatEconomy.enabled,
                configuredCost = config.cheatEconomy.costs["SLEEVE_BLOOM"],
                closed = isClosed,
                sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
                paused = decisionClock.isPausedState,
                preparationActive = isPreparationActive(),
                roundId = round?.roundId,
                roundEnded = candidateState?.phase == GamePhase.ROUND_END,
                controller = controller,
                controlEpoch = coordinator.epochFor(seat),
                now = now,
                offerSeat = offer?.seat,
                offerId = offer?.offerId,
                offerDeadlineMillis = offer?.deadlineMillis,
                offerFirstInWall = firstInWall,
                offerSecondInWall = secondInWall,
                allowAutomated = allowAutomated
            )
        )
        when (val result = check) {
            is CheatSubmissionCheck.Rejected -> {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = result.reason)
                storedReceipts[receiptKey] = StoredSubmission(
                    SubmissionKind.SLEEVE_BLOOM_OFF_TURN_CHOICE,
                    rejected,
                    sleeveBloomOffTurnChoicePayload = currentPayload
                )
                if (result.reason == "DEADLINE_EXPIRED") {
                    // 迟到选择不生效；权威按 FIRST 结算已付款 offer，保留选择结果而非退款。
                    coordinator.resolveExpiredSleeveBloomOffTurnOffers(now)
                    rekeyActiveTimingsPreservingDeadline()
                    return MutationResult(rejected, emptyList(), true)
                }
                return MutationResult(rejected, emptyList(), false)
            }
            is CheatSubmissionCheck.StaleOffer -> {
                coordinator.cleanStaleSleeveBloomOffTurnOffer(seat)
                rekeyActiveTimingsPreservingDeadline()
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = result.reason)
                storedReceipts[receiptKey] = StoredSubmission(
                    SubmissionKind.SLEEVE_BLOOM_OFF_TURN_CHOICE,
                    rejected,
                    sleeveBloomOffTurnChoicePayload = currentPayload
                )
                return MutationResult(rejected, emptyList(), true)
            }
            CheatSubmissionCheck.Allowed -> Unit
        }

        val coordResult = coordinator.submitSleeveBloomOffTurnChoice(seat, request, now, allowAutomated)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(
                SubmissionKind.SLEEVE_BLOOM_OFF_TURN_CHOICE,
                rejected,
                sleeveBloomOffTurnChoicePayload = currentPayload
            )
            return MutationResult(rejected, emptyList(), false)
        }

        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        storedReceipts[receiptKey] = StoredSubmission(
            SubmissionKind.SLEEVE_BLOOM_OFF_TURN_CHOICE,
            receipt,
            sleeveBloomOffTurnChoicePayload = currentPayload
        )
        rekeyActiveTimingsPreservingDeadline()
        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交偷梁换柱激活请求
     */
    @Synchronized
    fun submitBeamSwap(
        seat: Int,
        request: BeamSwapRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = beamSwapHandler.normalizeActivation(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.BEAM_SWAP && stored.beamSwapPayload == currentPayload) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED")
            return MutationResult(rejected, emptyList(), false)
        }

        val round = coordinator.activeRound
        val currentDecision = coordinator.queryDecision(seat)
        val controller = coordinator.controllerFor(seat)
        val timing = decisionClock.timing(seat)
        val candidateState = round?.readState()
        val drawnTile = candidateState?.players?.getOrNull(seat)?.drawnTile
        val deadWall = candidateState?.deadWall ?: emptyList()
        val deadWallReplacements = candidateState?.deadWallReplacements ?: emptyList()
        val deadWallTileIdsInDeadWall = (deadWall + deadWallReplacements)
            .filter { candidateState?.tilePool?.getLocation(it) == com.majiang.model.TileLocation.DEAD_WALL }
            .map { it.tileId }
            .toSet()
        val doraIndicatorTileIds = candidateState?.doraIndicators?.map { it.tileId }?.toSet() ?: emptySet()
        val eligibleHandTileCount = if (candidateState == null || drawnTile == null) {
            0
        } else {
            candidateState.players[seat].closedTiles.count {
                it != drawnTile && candidateState.tilePool.getLocation(it) == com.majiang.model.TileLocation.hand(seat)
            }
        }
        val cost = config.cheatEconomy.costs["BEAM_SWAP"] ?: 10
        val check = beamSwapHandler.validateActivation(
            BeamSwapSubmissionHandler.ActivationInput(
                seat = seat,
                request = request,
                featureEnabled = config.beamSwapConfig.enabled,
                economyEnabled = config.cheatEconomy.enabled,
                configuredCost = config.cheatEconomy.costs["BEAM_SWAP"],
                closed = isClosed,
                sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
                paused = decisionClock.isPausedState,
                preparationActive = isPreparationActive(),
                roundId = round?.roundId,
                roundEnded = candidateState?.phase == GamePhase.ROUND_END,
                decision = currentDecision,
                controller = controller,
                timing = timing,
                now = now,
                currentPlayer = candidateState?.currentPlayer,
                phase = candidateState?.phase,
                responseWindowActive = round?.responseWindow != null,
                drawnTileId = drawnTile?.tileId,
                drawnTileInHand = drawnTile != null &&
                        candidateState.tilePool.getLocation(drawnTile) == com.majiang.model.TileLocation.hand(seat),
                eligibleHandTileCount = eligibleHandTileCount,
                lastDrawKind = candidateState?.lastDrawKind ?: SleeveBloomDrawKind.DEAL_EXTRA,
                riichiDeclared = candidateState?.riichiDeclared?.getOrNull(seat) ?: false,
                sleeveBloomOfferActive = candidateState?.sleeveBloomOffer != null,
                beamSwapOfferActive = candidateState?.beamSwapOffer != null,
                sleeveBloomUsedOnTurn = candidateState?.sleeveBloomUsedOnTurn ?: -1,
                beamSwapUsedOnTurn = candidateState?.beamSwapUsedOnTurn ?: -1,
                turnNumber = candidateState?.turnNumber ?: -1,
                wallRemaining = candidateState?.wallRemaining ?: 0,
                wallPointer = candidateState?.wallPointer ?: 0,
                wallEndExclusive = candidateState?.wallEndExclusive ?: 0,
                deadWall = deadWall,
                deadWallReplacements = deadWallReplacements,
                deadWallTileIdsInDeadWall = deadWallTileIdsInDeadWall,
                doraIndicatorTileIds = doraIndicatorTileIds,
                rinshanPointer = candidateState?.rinshanPointer ?: 0,
                cheatPoints = candidateState?.cheatPoints?.getOrNull(seat) ?: 0,
                cost = cost,
                allowAutomated = allowAutomated
            )
        )
        val rejection = check.rejectionReason
        if (rejection != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = rejection)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 6. 执行原子扣费与安装 offer
        val coordResult = coordinator.submitBeamSwap(seat, request, now, allowAutomated)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 7. 计时 rekey 延续（保留截止时间与储备）
        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            val newDecision = coordinator.queryDecision(seat)
            val activeRoundId = checkNotNull(round).roundId
            val timingKey = checkNotNull(timing).key
            val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
            decisionClock.rekey(timingKey, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, receipt, beamSwapPayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交偷梁换柱选择决策请求
     */
    @Synchronized
    fun submitBeamSwapChoice(
        seat: Int,
        request: BeamSwapChoiceRequest,
        now: Long,
        allowAutomated: Boolean = false
    ): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = beamSwapHandler.normalizeChoice(request)
        val receiptKey = ReceiptKey(seat, request.requestId)
        val stored = storedReceipts[receiptKey]
        if (stored != null) {
            if (stored.kind == SubmissionKind.BEAM_SWAP_CHOICE && stored.beamSwapChoicePayload == currentPayload) {
                return MutationResult(stored.receipt, emptyList(), false)
            } else {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
                return MutationResult(rejected, emptyList(), false)
            }
        }
        if (storedShufflePlans.keys.any { it.seat == seat && it.requestId == request.requestId }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "REQUEST_ID_CONFLICT")
            return MutationResult(rejected, emptyList(), false)
        }

        if (isReceiptQuotaExceeded(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "RECEIPT_QUOTA_EXCEEDED")
            return MutationResult(rejected, emptyList(), false)
        }

        val round = coordinator.activeRound
        val currentDecision = coordinator.queryDecision(seat)
        val controller = coordinator.controllerFor(seat)
        val timing = decisionClock.timing(seat)
        val candidateState = round?.readState()
        val offer = candidateState?.beamSwapOffer
        val drawnTile = candidateState?.players?.getOrNull(seat)?.drawnTile
        val deadWall = candidateState?.deadWall ?: emptyList()
        val deadWallReplacements = candidateState?.deadWallReplacements ?: emptyList()
        val deadWallTileIdsInDeadWall = (deadWall + deadWallReplacements)
            .filter { candidateState?.tilePool?.getLocation(it) == com.majiang.model.TileLocation.DEAD_WALL }
            .map { it.tileId }
            .toSet()
        val wall = candidateState?.wall ?: emptyList()
        val wallTileIdsInWall = wall
            .filter { candidateState?.tilePool?.getLocation(it) == com.majiang.model.TileLocation.WALL }
            .map { it.tileId }
            .toSet()
        val currentEligibleHandTileIds = if (candidateState == null || drawnTile == null) {
            emptySet()
        } else {
            candidateState.players[seat].closedTiles.filter {
                it != drawnTile && candidateState.tilePool.getLocation(it) == com.majiang.model.TileLocation.hand(seat)
            }.map { it.tileId }.toSet()
        }
        val check = beamSwapHandler.validateChoice(
            BeamSwapSubmissionHandler.ChoiceInput(
                seat = seat,
                request = request,
                featureEnabled = config.beamSwapConfig.enabled,
                economyEnabled = config.cheatEconomy.enabled,
                configuredCost = config.cheatEconomy.costs["BEAM_SWAP"],
                closed = isClosed,
                sessionClosed = coordinator.session.lifecycle == SessionLifecycle.CLOSED,
                paused = decisionClock.isPausedState,
                preparationActive = isPreparationActive(),
                roundId = round?.roundId,
                roundEnded = candidateState?.phase == GamePhase.ROUND_END,
                decision = currentDecision,
                controller = controller,
                timing = timing,
                now = now,
                currentPlayer = candidateState?.currentPlayer,
                phase = candidateState?.phase,
                responseWindowActive = round?.responseWindow != null,
                offer = offer,
                turnNumber = candidateState?.turnNumber ?: -1,
                drawnTileId = drawnTile?.tileId,
                drawnTileInHand = drawnTile != null &&
                        candidateState.tilePool.getLocation(drawnTile) == com.majiang.model.TileLocation.hand(seat),
                lastDrawKind = candidateState?.lastDrawKind ?: SleeveBloomDrawKind.DEAL_EXTRA,
                currentEligibleHandTileIds = currentEligibleHandTileIds,
                wall = wall,
                wallPointer = candidateState?.wallPointer ?: 0,
                wallEndExclusive = candidateState?.wallEndExclusive ?: 0,
                wallTileIdsInWall = wallTileIdsInWall,
                deadWall = deadWall,
                deadWallReplacements = deadWallReplacements,
                deadWallTileIdsInDeadWall = deadWallTileIdsInDeadWall,
                doraIndicatorTileIds = candidateState?.doraIndicators?.map { it.tileId }?.toSet() ?: emptySet(),
                rinshanPointer = candidateState?.rinshanPointer ?: 0,
                allowAutomated = allowAutomated
            )
        )
        when (check) {
            is CheatSubmissionCheck.Rejected -> {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = check.reason)
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
            is CheatSubmissionCheck.StaleOffer -> {
                coordinator.cleanStaleBeamSwapOffer(seat)
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = check.reason)
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
                val newDecision = coordinator.queryDecision(seat)
                val activeRoundId = checkNotNull(round).roundId
                val timingKey = checkNotNull(timing).key
                val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
                decisionClock.rekey(timingKey, newKey)
                return MutationResult(rejected, emptyList(), true)
            }
            CheatSubmissionCheck.Allowed -> Unit
        }

        // 6. 执行选择决策
        val coordResult = coordinator.submitBeamSwapChoice(seat, request, allowAutomated)
        if (coordResult !is ActionResult.Accepted) {
            val reasonStr = when (coordResult) {
                is ActionResult.Rejected -> coordResult.reason.name
                else -> "REJECTED"
            }
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = reasonStr)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 7. 计时 rekey 延续（保留截止时间与储备）
        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            val newDecision = coordinator.queryDecision(seat)
            val activeRoundId = checkNotNull(round).roundId
            val timingKey = checkNotNull(timing).key
            val newKey = DecisionKey(activeRoundId, seat, newDecision.decisionId)
            decisionClock.rekey(timingKey, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, receipt, beamSwapChoicePayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    // ========================================
    // 纯计算 Tick 自动推进
    // ========================================

    @Synchronized
    fun checkPreparationStep(now: Long, automatedSeats: Set<Int>, nativeAiSeats: Set<Int>): AutoStepResult? {
        if (!isPreparationActive()) return null
        val round = coordinator.activeRound ?: return null
        if (round.state.phase != GamePhase.STACKING) return null

        when (val step = shufflePreparationHandler.step(
            now = now,
            automatedSeats = automatedSeats,
            nativeAiSeats = nativeAiSeats,
            points = round.state.cheatPoints.toList(),
            // 统一 AI 千术策略开启时由能力适配器先提交；旧配置才走兼容的自动确认。
            aiMaySpend = config.shuffleConfig.aiMaySpend && !config.aiCheatPolicy.enabled,
            dealerSeat = round.state.dealerIndex,
            config = config.shuffleConfig
        )) {
            ShufflePreparationHandler.Step.Waiting -> return null
            is ShufflePreparationHandler.Step.RecordUpdated -> {
                round.updatePreparationRecord(step.record)
                return null
            }
            is ShufflePreparationHandler.Step.Ready -> {
                round.updatePreparationRecord(step.record)
                val tiles = step.tiles
                if (tiles != null && tiles.size == 136) {
                    // 费用严格来自原始方案（被看破者仍支付原统一目标 30 点），看破席位集合来自截止时冻结的 K。
                    val costs = step.plans.associate { it.seat to it.selection.cost() }
                    val reasons = step.plans.associate { it.seat to "shuffle_plan" }
                    val dealResult = coordinator.executeDealWithPreparation(
                        roundId = step.roundId,
                        wallTiles = tiles,
                        cheatCosts = costs,
                        costReasons = reasons,
                        seeThroughSeats = step.record.seeThroughSeats
                    )
                    if (dealResult is ActionResult.Accepted) {
                        shufflePreparationHandler.completeDeal()
                        decisionClock.beginRound(step.roundId)
                        lastActiveRoundId = step.roundId
                        recordSettlementEvents(dealResult.events)
                        return AutoStepResult.PreparationDealt(dealResult.events)
                    }
                }
                return null
            }
        }
    }

    /**
     * 他家回合 offer/预留只改变牌山私有状态，不得因 round.version 变化重置当下玩家的决策截止。
     * 将仍活跃的计时键重键到当前决策身份，完整保留 opensAt/deadline/储备。
     */
    private fun rekeyActiveTimingsPreservingDeadline() {
        val roundId = coordinator.activeRound?.roundId ?: return
        for (seat in 0..3) {
            val timing = decisionClock.timing(seat) ?: continue
            if (timing.key.roundId != roundId) continue
            val decision = coordinator.queryDecision(seat)
            if (decision.roundId != roundId || decision.decisionId == timing.key.decisionId) continue
            decisionClock.rekey(timing.key, DecisionKey(roundId, seat, decision.decisionId))
        }
    }

    @Synchronized
    fun checkTimeout(now: Long): AutoStepResult? {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) return null
        if (!decisionClock.isPausedState) {
            // 他家回合预定的独立 10 秒选择截止先于任何即时决策超时结算。
            val expiredOffers = coordinator.resolveExpiredSleeveBloomOffTurnOffers(now)
            if (expiredOffers.isNotEmpty()) {
                rekeyActiveTimingsPreservingDeadline()
                return AutoStepResult.SleeveBloomOffTurnExpired(expiredOffers, emptyList())
            }
        }
        val expiredTimings = decisionClock.expired(now)
        if (expiredTimings.isNotEmpty()) {
            val timing = expiredTimings.first()
            val seat = timing.key.seat

            var effectiveTimingKey = timing.key
            if (coordinator.activeRound?.state?.sleeveBloomOffer?.seat == seat) {
                coordinator.resolveSleeveBloomKeepFirst(seat)
                val newDecision = coordinator.queryDecision(seat)
                val newKey = DecisionKey(timing.key.roundId, seat, newDecision.decisionId)
                decisionClock.rekey(timing.key, newKey)
                effectiveTimingKey = newKey
            }
            if (coordinator.activeRound?.state?.beamSwapOffer?.seat == seat) {
                coordinator.resolveBeamSwapKeepOriginal(seat)
                val newDecision = coordinator.queryDecision(seat)
                val newKey = DecisionKey(timing.key.roundId, seat, newDecision.decisionId)
                decisionClock.rekey(effectiveTimingKey, newKey)
                effectiveTimingKey = newKey
            }

            val decision = coordinator.queryDecision(seat)

            if (decision.decisionId == effectiveTimingKey.decisionId && decision.options.isNotEmpty()) {
                val options = decision.options.map { it.action }
                val view = coordinator.queryView(seat)
                val defaultAction = TimeoutPolicy.choose(view, options)

                if (defaultAction != null) {
                    val actionResult = coordinator.handleAction(defaultAction)
                    if (actionResult is ActionResult.Accepted) {
                        decisionClock.expire(effectiveTimingKey, timing.generation, now)
                        val activeRound = coordinator.activeRound
                        val win = activeRound?.responseWindow
                        if (activeRound != null && win == null) {
                            cancelUnsubmittedTimings(timing.key.roundId, now)
                        }
                        recordSettlementEvents(actionResult.events)
                        return AutoStepResult.TimeoutResolved(seat, defaultAction, actionResult.events)
                    }
                }
            }
        }
        return null
    }

    @Synchronized
    fun checkWaitingDealerChoice(now: Long, automatedDealer: Boolean): AutoStepResult? {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) return null
        if (coordinator.session.lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE && automatedDealer) {
            val dealer = coordinator.session.currentDealer
            val decision = coordinator.queryDecision(dealer)
            if (decision.options.isNotEmpty()) {
                val chosenAction = decision.options.firstOrNull { it.optionId == "opt-continue" }?.action
                    ?: decision.options.first().action

                val actionResult = coordinator.handleAction(chosenAction)
                if (actionResult is ActionResult.Accepted) {
                    val newRound = coordinator.activeRound
                    if (newRound != null && newRound.roundId != lastActiveRoundId) {
                        if (config.shuffleConfig.enabled) {
                            startPreparation(newRound.roundId, now)
                        } else {
                            decisionClock.beginRound(newRound.roundId)
                            lastActiveRoundId = newRound.roundId
                        }
                    }
                    recordSettlementEvents(actionResult.events)
                    return AutoStepResult.DealerChoiceTaken(dealer, chosenAction, actionResult.events)
                }
            }
        }
        return null
    }

    @Synchronized
    fun checkAutoNextRound(now: Long, allReady: Boolean): AutoStepResult? {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) return null
        val roundAtEnd = coordinator.activeRound?.state?.phase == GamePhase.ROUND_END
        val sessionAtEnd = coordinator.session.lifecycle in setOf(SessionLifecycle.ROUND_RESULT, SessionLifecycle.WAITING_READY)
        if ((roundAtEnd || sessionAtEnd) &&
            coordinator.session.lifecycle != SessionLifecycle.WAITING_DEALER_CHOICE &&
            coordinator.session.lifecycle != SessionLifecycle.FINISHED
        ) {
            if (allReady) {
                val result = coordinator.nextRound()
                if (result is ActionResult.Accepted) {
                    val newRound = coordinator.activeRound
                    if (newRound != null) {
                        if (config.shuffleConfig.enabled) {
                            startPreparation(newRound.roundId, now)
                        } else {
                            decisionClock.beginRound(newRound.roundId)
                            lastActiveRoundId = newRound.roundId
                        }
                    }
                    recordSettlementEvents(result.events)
                    return AutoStepResult.NextRoundStarted(result.events)
                }
            }
        }
        return null
    }

    @Synchronized
    fun checkRiichiAutoDiscard(now: Long, humanAutoRiichiSeats: Set<Int>): AutoStepResult? {        val round = coordinator.activeRound
        if (round != null && round.state.phase in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD)) {
            val currPlayer = round.state.currentPlayer
            if (currPlayer in humanAutoRiichiSeats &&
                round.state.riichiDeclared[currPlayer] &&
                round.state.players[currPlayer].drawnTile != null
            ) {
                if (round.state.sleeveBloomOffer?.seat == currPlayer) {
                    // 立直玩家已付费进入二选一：不得自动保留 FIRST 并抢在其选择之前摸切。
                    // 由该座 DecisionClock 截止后的 checkTimeout 先结算 FIRST 再执行默认动作。
                    return null
                }
                if (round.state.beamSwapOffer?.seat == currPlayer) {
                    coordinator.resolveBeamSwapKeepOriginal(currPlayer)
                    val oldTiming = decisionClock.timing(currPlayer)
                    if (oldTiming != null) {
                        val newDecision = coordinator.queryDecision(currPlayer)
                        val newKey = DecisionKey(round.roundId, currPlayer, newDecision.decisionId)
                        decisionClock.rekey(oldTiming.key, newKey)
                    }
                }
                val actions = coordinator.legalActions(currPlayer)
                val hasTsumo = actions.any { it is GameAction.DeclareTsumo }
                val hasKan = actions.any { it is GameAction.DeclareKan }
                if (!hasTsumo && !hasKan && !hasSleeveBloomActivation(round.state, currPlayer)) {
                    val drawnTile = round.state.players[currPlayer].drawnTile!!
                    val discardAction = GameAction.DiscardTile(currPlayer, drawnTile)
                    val timing = decisionClock.timing(currPlayer)
                    val actionResult = coordinator.handleAction(discardAction)
                    if (actionResult is ActionResult.Accepted) {
                        if (timing != null) {
                            decisionClock.complete(timing.key, now)
                        }
                        recordSettlementEvents(actionResult.events)
                        return AutoStepResult.RiichiAutoDiscarded(currPlayer, discardAction, actionResult.events)
                    }
                }
            }
        }
        return null
    }

    /**
     * 立直自动摸切前的技能让位判定：存在可付费即时施术的候选时，权威时钟未到不得抢先摸切。
     */
    private fun hasSleeveBloomActivation(state: GameState, seat: Int): Boolean {
        if (!config.sleeveBloomConfig.enabled || !config.cheatEconomy.enabled) return false
        val cost = config.cheatEconomy.costs["SLEEVE_BLOOM"] ?: return false
        if (cost != 10) return false
        if (state.cheatPoints[seat] < cost) return false
        if (state.currentPlayer != seat || state.phase != GamePhase.CHEAT_WINDOW) return false
        if (state.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) return false
        if (state.sleeveBloomOffer != null ||
            state.sleeveBloomOffTurnOffer(seat) != null ||
            state.beamSwapOffer != null
        ) {
            return false
        }
        if (state.sleeveBloomUsedOnTurn == state.turnNumber || state.beamSwapUsedOnTurn == state.turnNumber) {
            return false
        }
        val hand = state.players[seat]
        val drawn = hand.drawnTile ?: return false
        if (state.tilePool.getLocation(drawn) != com.majiang.model.TileLocation.hand(seat)) return false
        return when (state.lastDrawKind) {
            SleeveBloomDrawKind.NORMAL -> state.nextAvailableWallTile()
                ?.let { state.tilePool.getLocation(it) == com.majiang.model.TileLocation.WALL } == true
            SleeveBloomDrawKind.RINSHAN ->
                state.rinshanPointer in 1..3 &&
                    state.totalKanCount < 4 &&
                    state.deadWall.getOrNull(state.rinshanPointer - 1) == drawn &&
                    state.deadWall.getOrNull(state.rinshanPointer)
                        ?.let { state.tilePool.getLocation(it) == com.majiang.model.TileLocation.DEAD_WALL } == true
            SleeveBloomDrawKind.DEAL_EXTRA -> false
        }
    }

    @Synchronized
    fun syncDecisionsAndClocks(now: Long, automatedSeats: Set<Int>): List<AiDecisionRequest> {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) return emptyList()
        val round = coordinator.activeRound
        if (round != null && round.state.phase == GamePhase.ROUND_END) {
            val res = round.result
            if (res != null) {
                roundResultEvents[res.roundId] = res.events
            }
        }

        if (coordinator.session.lifecycle == SessionLifecycle.FINISHED || isPreparationActive()) {
            for (s in 0..3) {
                val t = decisionClock.timing(s)
                if (t != null) {
                    decisionClock.cancel(t.key, now)
                }
            }
            return emptyList()
        }

        val activeRoundId = round?.roundId
        val aiRequests = mutableListOf<AiDecisionRequest>()

        for (s in 0..3) {
            val decision = coordinator.queryDecision(s)
            val roundId = decision.roundId

            if (roundId != null && roundId == activeRoundId && decision.options.isNotEmpty()) {
                val key = DecisionKey(roundId, s, decision.decisionId)
                val existing = decisionClock.timing(s)
                if (existing == null) {
                    decisionClock.open(key, now)
                } else if (existing.key != key) {
                    decisionClock.cancel(existing.key, now)
                    decisionClock.open(key, now)
                }

                if (s in automatedSeats &&
                    round?.state?.sleeveBloomOffer?.seat != s &&
                    round?.state?.sleeveBloomOffTurnOffer(s) == null &&
                    round?.state?.beamSwapOffer?.seat != s
                ) {
                    val epoch = decision.controlEpoch
                    val view = coordinator.queryView(s).deepCopy()
                    val legals = decision.options.map { it.action.deepCopy() }
                    aiRequests.add(AiDecisionRequest(s, roundId, decision.decisionId, epoch, view, legals))
                }
            } else {
                val existing = decisionClock.timing(s)
                if (existing != null) {
                    decisionClock.cancel(existing.key, now)
                }
            }
        }
        return aiRequests
    }

    fun setSeatController(seat: Int, controller: SeatController) {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        _controllers[seat] = controller
        coordinator.setSeatController(seat, controller)
    }

    fun controllerFor(seat: Int): SeatController {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        return coordinator.controllerFor(seat)
    }

    fun cancelUnsubmittedTimings(roundId: String, now: Long) {
        for (s in 0..3) {
            val timing = decisionClock.timing(s)
            if (timing != null && timing.key.roundId == roundId) {
                decisionClock.cancel(timing.key, now)
            }
        }
    }

    // ========================================
    // 时钟暂停与恢复
    // ========================================

    @Synchronized
    fun pause(now: Long): Boolean {
        val wasPaused = decisionClock.isPausedState
        decisionClock.pause(now)
        if (!wasPaused) {
            pausedAuthorityTimeMillis = now
            return true
        }
        return false
    }

    @Synchronized
    fun resume(now: Long): Boolean {
        val wasPaused = decisionClock.isPausedState
        decisionClock.resume(now)
        if (wasPaused) {
            val pausedAt = pausedAuthorityTimeMillis
            val pauseDuration = if (pausedAt != null) maxOf(0L, now - pausedAt) else 0L
            if (pausedAt != null && isPreparationActive()) {
                shufflePreparationHandler.shiftForPause(pauseDuration)
            }
            if (pauseDuration > 0L) {
                coordinator.shiftSleeveBloomOffTurnDeadlines(pauseDuration)
                rekeyActiveTimingsPreservingDeadline()
            }
            pausedAuthorityTimeMillis = null
            return true
        }
        return false
    }

    // ========================================
    // 查询接口
    // ========================================

    fun queryView(seat: Int): PlayerView = coordinator.queryView(seat)

    fun queryDecision(seat: Int, isAutomated: Boolean): PlayerDecision {
        if (isPreparationActive()) {
            val epoch = coordinator.epochFor(seat)
            val prepRoundId = shufflePreparationHandler.currentRoundId()
            return PlayerDecision(
                roundId = prepRoundId,
                decisionId = "preparation-$prepRoundId-$seat",
                controlEpoch = epoch,
                options = emptyList()
            )
        }
        val raw = coordinator.queryDecision(seat)
        return if (isAutomated) {
            PlayerDecision(
                roundId = raw.roundId,
                decisionId = raw.decisionId,
                controlEpoch = raw.controlEpoch,
                options = emptyList()
            )
        } else {
            PlayerDecision(
                roundId = raw.roundId,
                decisionId = raw.decisionId,
                controlEpoch = raw.controlEpoch,
                options = raw.options.map { it.deepCopy() }
            )
        }
    }

    fun queryRoundResultEvents(seat: Int, roundId: String): List<GameEvent> {
        if (seat !in 0..3) return emptyList()
        val rawEvents = roundResultEvents[roundId]
            ?: if (coordinator.activeRound?.roundId == roundId) {
                coordinator.activeRound?.result?.events ?: emptyList()
            } else {
                emptyList()
            }
        return SessionUpdate.filterVisibleEvents(rawEvents, seat)
    }

    private fun isUraRevealed(events: List<GameEvent>): Boolean =
        events.any { it is GameEvent.TsumoWin || it is GameEvent.RonWin }

    fun queryPublicProjection(): PublicTableProjection {
        val session = coordinator.session.readState()
        val activeRound = coordinator.activeRound
        val state = activeRound?.readState()
        val roundId = activeRound?.roundId
        val roundVersion = activeRound?.version
        val clocks = buildClockViews()
        val paused = decisionClock.isPausedState

        val isTerminalSettlement = (activeRound == null || activeRound.state.phase == GamePhase.ROUND_END) &&
            coordinator.session.lifecycle in setOf(
                SessionLifecycle.ROUND_RESULT,
                SessionLifecycle.WAITING_DEALER_CHOICE,
                SessionLifecycle.WAITING_READY,
                SessionLifecycle.FINISHED
            )

        val completedId = if (isTerminalSettlement) {
            lastCompletedRoundId ?: activeRound?.roundId ?: coordinator.session.roundRecords.lastOrNull()?.roundId
        } else {
            null
        }

        val rawSettlementEvents = if (isTerminalSettlement && completedId != null) {
            if (lastCompletedSettlementEvents.isNotEmpty()) {
                lastCompletedSettlementEvents
            } else {
                roundResultEvents[completedId] ?: emptyList()
            }
        } else {
            emptyList()
        }

        val revealUra = isUraRevealed(rawSettlementEvents)

        return PublicProjectionFactory.project(
            session = session,
            state = state,
            roundId = if (isTerminalSettlement && roundId == null) completedId else roundId,
            roundVersion = roundVersion,
            clocks = clocks,
            paused = paused,
            settlementRoundId = completedId,
            settlementEvents = rawSettlementEvents,
            revealSettlementUra = revealUra
        )
    }

    fun queryPublicRoundResultEvents(roundId: String): List<GameEvent> {
        val rawEvents = roundResultEvents[roundId]
            ?: if (coordinator.activeRound?.roundId == roundId) {
                coordinator.activeRound?.result?.events ?: emptyList()
            } else {
                emptyList()
            }
        val revealUra = isUraRevealed(rawEvents)
        return PublicProjectionFactory.publicEvents(rawEvents, revealSettlementUra = revealUra)
    }

    fun filterPublicEvents(events: List<GameEvent>): List<GameEvent> {
        val revealUra = isUraRevealed(events)
        return PublicProjectionFactory.publicEvents(events, revealSettlementUra = revealUra)
    }

    fun buildClockViews(now: Long = if (decisionClock.isPausedState && pausedAuthorityTimeMillis != null) pausedAuthorityTimeMillis!! else clock.nowMillis()): List<ClockView> {
        val list = mutableListOf<ClockView>()
        for (s in 0..3) {
            val timing = decisionClock.timing(s)
            val reserve = decisionClock.remainingReserve(s)
            if (timing != null) {
                list.add(
                    ClockView(
                        seat = s,
                        opensAt = timing.opensAtMillis,
                        deadline = timing.deadlineMillis,
                        reserve = reserve,
                        serverNow = now
                    )
                )
            } else {
                list.add(
                    ClockView(
                        seat = s,
                        opensAt = 0L,
                        deadline = 0L,
                        reserve = reserve,
                        serverNow = now
                    )
                )
            }
        }
        return Collections.unmodifiableList(list)
    }

    fun buildSessionView(): SessionView {
        val session = coordinator.session
        val activeRound = coordinator.activeRound
        val scores = if (activeRound != null && activeRound.state.phase != GamePhase.ROUND_END) {
            activeRound.readState().scores.toList()
        } else {
            session.betweenRoundScores.toList()
        }

        val records = session.roundRecords.map { r ->
            PublicRoundRecord(
                roundId = r.roundId,
                resultId = r.resultId,
                finalScores = r.finalScores.toList(),
                deltas = r.deltas.toList(),
                finalCheatPoints = r.finalCheatPoints.toList(),
                cheatDeltas = r.cheatDeltas.toList()
            )
        }

        val finalRes = session.result?.let { res ->
            PublicSessionResult(
                finalScores = res.finalScores.toList(),
                rankedSeats = res.rankings.map { it.seat },
                reason = res.reason.name,
                finalCheatPoints = res.finalCheatPoints.toList()
            )
        }

        return SessionView(
            lifecycle = session.lifecycle.name,
            scores = scores,
            roundWind = session.currentRoundWind,
            roundNumber = session.currentRoundNumber,
            dealerSeat = session.currentDealer,
            honba = session.currentHonba,
            roundRecords = records,
            finalResult = finalRes
        )
    }

    /**
     * AI 投影专用控制视图：权威层不持有连接状态，仅以领域控制者类型与代次描述。
     */
    private fun buildAiControlViews(): List<SeatControlView> {
        val list = ArrayList<SeatControlView>(4)
        for (s in 0..3) {
            val role = when (coordinator.controllerFor(s)) {
                is SeatController.LocalHuman, is SeatController.RemoteHuman -> "HUMAN"
                is SeatController.Computer -> "COMPUTER"
                is SeatController.Takeover -> "TAKEOVER"
            }
            list.add(
                SeatControlView(
                    seat = s,
                    connected = true,
                    role = role,
                    controlEpoch = coordinator.epochFor(s)
                )
            )
        }
        return Collections.unmodifiableList(list)
    }

    /**
     * 通用 AI 调度器使用的“是否仍存在本座位待决 offer”轻量检查。
     *
     * 只读当前领域状态，不生成投影、不暴露他家信息；仅在权威串行队列内调用。
     */
    fun hasPendingAiOffer(seat: Int): Boolean {
        if (seat !in 0..3) return false
        val state = coordinator.activeRound?.state ?: return false
        return state.sleeveBloomOffer?.seat == seat ||
            state.sleeveBloomOffTurnOffer(seat) != null ||
            state.beamSwapOffer?.seat == seat
    }

    /**
     * 轻量检查本座位是否存在“本人弃牌后签发的他家回合袖里藏花预约窗口”。
     *
     * 只做门控；真正的可施术判定仍由 [SleeveBloomProjectionFactory] 与权威提交校验负责。
     */
    fun hasOffTurnSleeveBloomOpportunity(seat: Int): Boolean {
        if (seat !in 0..3) return false
        if (!config.sleeveBloomConfig.enabled || !config.cheatEconomy.enabled) return false
        if (decisionClock.isPausedState) return false
        val state = coordinator.activeRound?.state ?: return false
        if (state.phase !in setOf(
                GamePhase.DRAW,
                GamePhase.CHEAT_WINDOW,
                GamePhase.DISCARD,
                GamePhase.RESPONSE_WINDOW,
                GamePhase.CHANKAN_WINDOW
            )
        ) {
            return false
        }
        if (state.currentPlayer == seat) return false
        if (state.sleeveBloomOffer != null ||
            state.sleeveBloomOffTurnOffer(seat) != null ||
            state.beamSwapOffer != null
        ) {
            return false
        }
        if (state.sleeveBloomReservation(seat) != null) return false
        if (state.sleeveBloomWindowId(seat) == null) return false
        if (state.availableWallTileCount() < 2) return false
        val cost = config.cheatEconomy.costs["SLEEVE_BLOOM"] ?: 10
        if ((state.cheatPoints.getOrNull(seat) ?: 0) < cost) return false
        return true
    }

    /**
     * 按座位一次性构建 AI 专用不可变投影。
     *
     * 与 [querySnapshot] 的关键区别：
     * 1. 使用本座位真实合法决策（自动化座位不清空 options）；
     * 2. 私有千术投影以本座位作为行动者生成（仅内部 AI 使用，绝不回传客户端）；
     * 3. 不携带 GameState、牌山顺序或他家暗牌；
     * 4. 同时冻结 stamp 与预算，供异步线程只读。
     *
     * @param strategyPolicy 策略级可选约束，与房间策略取交集
     * @param spentThisRound 本局该座位已由 AI 花费的点数（预算扣减）
     * @param registeredCapabilityIds 组合根已显式注册的能力 ID；新增能力由调用方传入即可进入限权集合
     */
    fun buildAiSeatSnapshot(
        seat: Int,
        now: Long,
        strategyPolicy: AiCheatPolicy? = null,
        spentThisRound: Int = 0,
        registeredCapabilityIds: Set<String> = BUILT_IN_AI_CAPABILITY_ID_SET
    ): AiSeatSnapshot {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        val controller = coordinator.controllerFor(seat)
        val takeover = controller is SeatController.Takeover
        val automated = takeover || controller is SeatController.Computer

        val rawDecision = coordinator.queryDecision(seat)
        val view = coordinator.queryView(seat).deepCopy()
        val controls = buildAiControlViews()
        val projectionControls = if (automated) {
            controls.map { control ->
                if (control.seat == seat) control.copy(role = "HUMAN", connected = true) else control
            }
        } else {
            controls
        }

        val round = coordinator.activeRound
        val timing = decisionClock.timing(seat)
        val paused = decisionClock.isPausedState
        val effectiveNow = if (paused && pausedAuthorityTimeMillis != null) {
            pausedAuthorityTimeMillis!!
        } else {
            now
        }
        val preparation = buildPreparationViewForSeat(seat)

        val cheatPoints = if (config.cheatEconomy.enabled) {
            coordinator.queryCheatPoints(seat)
        } else {
            0
        }

        val tileMarkView = if (config.tileMarkConfig.enabled) {
            TileMarkProjectionFactory.create(
                observerSeat = seat,
                session = coordinator.session,
                activeRound = round,
                view = view,
                decision = rawDecision,
                controls = projectionControls,
                nowMillis = effectiveNow,
                timing = timing,
                isPaused = paused,
                config = config.tileMarkConfig,
                economyConfig = config.cheatEconomy
            )
        } else {
            null
        }

        val sleeveBloomView = if (config.sleeveBloomConfig.enabled) {
            SleeveBloomProjectionFactory.create(
                observerSeat = seat,
                activeRound = round,
                view = view,
                decision = rawDecision,
                controls = projectionControls,
                timing = timing,
                nowMillis = effectiveNow,
                isPaused = paused,
                config = config.sleeveBloomConfig,
                economyConfig = config.cheatEconomy
            )
        } else {
            null
        }

        val beamSwapView = if (config.beamSwapConfig.enabled) {
            BeamSwapProjectionFactory.create(
                observerSeat = seat,
                activeRound = round,
                view = view,
                decision = rawDecision,
                controls = projectionControls,
                timing = timing,
                nowMillis = effectiveNow,
                isPaused = paused,
                config = config.beamSwapConfig,
                economyConfig = config.cheatEconomy,
                session = coordinator.session
            )
        } else {
            null
        }

        val preparationActive = preparation != null && isPreparationActive() && !preparation.confirmed
        val bloomOffer = sleeveBloomView?.pendingOffer
        val beamOffer = beamSwapView?.pendingOffer
        val phase = when {
            preparationActive -> AiPhase.SHUFFLE_PREPARATION
            bloomOffer != null || beamOffer != null -> AiPhase.OFFER_CHOICE
            rawDecision.options.isNotEmpty() -> AiPhase.ORDINARY_ACTION
            sleeveBloomView?.canApplyOffTurn == true && sleeveBloomView.offTurnWindowId != null ->
                AiPhase.ORDINARY_ACTION
            else -> AiPhase.IDLE
        }
        val roundId = if (preparationActive) preparation!!.roundId else round?.roundId
        val decisionId = when (phase) {
            AiPhase.SHUFFLE_PREPARATION -> "preparation-$roundId-$seat"
            else -> rawDecision.decisionId
        }
        val deadline = when (phase) {
            AiPhase.SHUFFLE_PREPARATION -> preparation!!.deadlineMillis
            AiPhase.OFFER_CHOICE -> bloomOffer?.deadlineMillis ?: beamOffer?.deadlineMillis ?: timing?.deadlineMillis
            AiPhase.ORDINARY_ACTION -> timing?.deadlineMillis
            AiPhase.IDLE -> null
        }
        val stateVersion = round?.version ?: 0L
        val stamp = AiDecisionStamp(
            seat = seat,
            sessionId = coordinator.session.sessionId,
            roundId = roundId,
            decisionId = decisionId,
            controlEpoch = rawDecision.controlEpoch,
            phase = phase,
            stateVersion = stateVersion,
            offerId = bloomOffer?.offerId ?: beamOffer?.offerId,
            windowId = bloomOffer?.windowId ?: sleeveBloomView?.offTurnWindowId,
            deadlineMillis = deadline
        )

        val policy = config.aiCheatPolicy.restrictWith(strategyPolicy)
        val enabledCapabilities = LinkedHashSet<String>()
        val seatMaySpend = automated && (!takeover || policy.takeoverMaySpend)
        val legacyShuffle = automated && !takeover && config.shuffleConfig.aiMaySpend &&
            strategyPolicy?.enabled != false &&
            registeredCapabilityIds.contains(AiCapabilityIds.SHUFFLE_PREPARATION) &&
            policy.isCapabilityToggled(AiCapabilityIds.SHUFFLE_PREPARATION)
        val effectiveBudget = when {
            policy.enabled && seatMaySpend -> policy.maxSpendPerRound
            legacyShuffle -> 60
            else -> 0
        }
        val remainingBudget = maxOf(0, effectiveBudget - spentThisRound)
        if (remainingBudget > 0) {
            if (policy.enabled && seatMaySpend) {
                for (capabilityId in registeredCapabilityIds) {
                    if (capabilityId == AiCapabilityIds.MAHJONG) continue
                    if (policy.isCapabilityToggled(capabilityId)) enabledCapabilities.add(capabilityId)
                }
            }
            if (legacyShuffle) enabledCapabilities.add(AiCapabilityIds.SHUFFLE_PREPARATION)
        }
        if (phase == AiPhase.OFFER_CHOICE) {
            if (bloomOffer != null) enabledCapabilities.add(AiCapabilityIds.SLEEVE_BLOOM)
            if (beamOffer != null) enabledCapabilities.add(AiCapabilityIds.BEAM_SWAP)
        }

        val limits = AiCandidateLimits(
            enabledCapabilityIds = enabledCapabilities,
            maxSpend = remainingBudget,
            remainingPoints = cheatPoints,
            reservePoints = if (legacyShuffle && !policy.enabled) 0 else policy.reservePoints,
            candidateLimit = policy.candidateLimit,
            riskTolerance = policy.riskTolerance
        )

        return AiSeatSnapshot(
            stamp = stamp,
            view = view,
            decision = rawDecision,
            cheatView = AiCheatView(
                cheatPoints = cheatPoints,
                preparation = preparation,
                tileMark = tileMarkView,
                sleeveBloom = sleeveBloomView,
                beamSwap = beamSwapView
            ),
            limits = limits,
            policy = policy,
            takeover = takeover
        )
    }

    fun querySnapshot(
        seat: Int,
        syncEpoch: String,
        cursor: Long,
        controls: List<SeatControlView>
    ): PlayerSnapshot {
        val isAutomated = controls.getOrNull(seat)?.role != "HUMAN"
        val view = coordinator.queryView(seat)
        val decision = queryDecision(seat, isAutomated)
        val clocks = buildClockViews()
        val prepView = buildPreparationViewForSeat(seat)

        val cheatPoints = if (config.cheatEconomy.enabled) {
            val activeRound = coordinator.activeRound
            if (activeRound != null) {
                activeRound.readState().cheatPoints.toList()
            } else {
                coordinator.session.betweenRoundCheatPoints
            }
        } else {
            emptyList()
        }

        val sessionView = buildSessionView()
        val paused = decisionClock.isPausedState

        val activeRound = coordinator.activeRound
        val isTerminalSettlement = (activeRound == null || activeRound.state.phase == GamePhase.ROUND_END) &&
            coordinator.session.lifecycle in setOf(
                SessionLifecycle.ROUND_RESULT,
                SessionLifecycle.WAITING_DEALER_CHOICE,
                SessionLifecycle.WAITING_READY,
                SessionLifecycle.FINISHED
            )

        val completedId = if (isTerminalSettlement) {
            lastCompletedRoundId ?: activeRound?.roundId ?: coordinator.session.roundRecords.lastOrNull()?.roundId
        } else {
            null
        }

        val rawSettlementEvents = if (isTerminalSettlement && completedId != null) {
            if (lastCompletedSettlementEvents.isNotEmpty()) {
                lastCompletedSettlementEvents
            } else {
                roundResultEvents[completedId] ?: emptyList()
            }
        } else {
            emptyList()
        }

        val settlementEvents = if (isTerminalSettlement && completedId != null) {
            SessionUpdate.filterVisibleEvents(rawSettlementEvents.filter { isSettlementEvent(it) }, seat)
        } else {
            emptyList()
        }

        val snapshotRoundId = coordinator.activeRound?.roundId ?: (if (isTerminalSettlement) completedId else null)
        val presentationPreview = if (snapshotRoundId != null && snapshotRoundId == decision.roundId) {
            com.majiang.session.PlayerPresentationPreviewProvider.createPreview(view, decision)
        } else {
            null
        }

        val tileMarkView = if (config.tileMarkConfig.enabled) {
            TileMarkProjectionFactory.create(
                observerSeat = seat,
                session = coordinator.session,
                activeRound = coordinator.activeRound,
                view = view,
                decision = decision,
                controls = controls,
                nowMillis = if (decisionClock.isPausedState && pausedAuthorityTimeMillis != null) pausedAuthorityTimeMillis!! else clock.nowMillis(),
                timing = decisionClock.timing(seat),
                isPaused = paused,
                config = config.tileMarkConfig,
                economyConfig = config.cheatEconomy
            )
        } else {
            null
        }

        val sleeveBloomView = if (config.sleeveBloomConfig.enabled) {
            SleeveBloomProjectionFactory.create(
                observerSeat = seat,
                activeRound = coordinator.activeRound,
                view = view,
                decision = decision,
                controls = controls,
                timing = decisionClock.timing(seat),
                nowMillis = if (decisionClock.isPausedState && pausedAuthorityTimeMillis != null) pausedAuthorityTimeMillis!! else clock.nowMillis(),
                isPaused = paused,
                config = config.sleeveBloomConfig,
                economyConfig = config.cheatEconomy
            )
        } else {
            null
        }

        val beamSwapView = if (config.beamSwapConfig.enabled) {
            BeamSwapProjectionFactory.create(
                observerSeat = seat,
                activeRound = coordinator.activeRound,
                view = view,
                decision = decision,
                controls = controls,
                timing = decisionClock.timing(seat),
                nowMillis = if (decisionClock.isPausedState && pausedAuthorityTimeMillis != null) pausedAuthorityTimeMillis!! else clock.nowMillis(),
                isPaused = paused,
                config = config.beamSwapConfig,
                economyConfig = config.cheatEconomy,
                session = coordinator.session
            )
        } else {
            null
        }

        return PlayerSnapshot(
            sessionId = coordinator.session.sessionId,
            roundId = snapshotRoundId,
            syncEpoch = syncEpoch,
            cursor = cursor,
            sessionVersion = coordinator.session.version,
            roundVersion = coordinator.activeRound?.version,
            view = view,
            decision = decision,
            clocks = clocks,
            cheatPoints = cheatPoints,
            session = sessionView,
            controls = controls,
            paused = paused,
            settlementRoundId = completedId,
            settlementEvents = settlementEvents,
            preparation = prepView,
            presentationPreview = presentationPreview,
            tileMark = tileMarkView,
            sleeveBloom = sleeveBloomView,
            beamSwap = beamSwapView
        )
    }

    private fun recordSettlementEvents(events: List<GameEvent>) {
        val prevRoundId = lastActiveRoundId
        val currentRound = coordinator.activeRound
        val currentRoundId = currentRound?.roundId

        if (currentRound != null && currentRound.state.phase != GamePhase.ROUND_END && currentRoundId != lastCompletedRoundId) {
            lastCompletedRoundId = null
            lastCompletedSettlementEvents = emptyList()
        }

        if (prevRoundId != null && (currentRoundId == null || currentRoundId != prevRoundId)) {
            if (!roundResultEvents.containsKey(prevRoundId)) {
                roundResultEvents[prevRoundId] = events.filter { isSettlementEvent(it) }
            }
        }
        if (currentRound != null && currentRound.state.phase == GamePhase.ROUND_END) {
            val res = currentRound.result
            val completedId = res?.roundId ?: currentRound.roundId
            lastCompletedRoundId = completedId
            val filteredEvents = if (res != null) {
                res.events.filter { isSettlementEvent(it) }
            } else {
                events.filter { isSettlementEvent(it) }
            }
            lastCompletedSettlementEvents = filteredEvents
            roundResultEvents[completedId] = filteredEvents
        }
        lastActiveRoundId = currentRoundId
    }

    private fun isSettlementEvent(event: GameEvent): Boolean = when (event) {
        is GameEvent.TsumoWin,
        is GameEvent.RonWin,
        is GameEvent.DrawGame,
        is GameEvent.ScoreSettled,
        is GameEvent.RoundEnded,
        is GameEvent.MatchEnded -> true
        is GameEvent.DoraRevealed -> event.isUraDora
        is GameEvent.CheatPointsChanged -> true
        else -> false
    }

    @Synchronized
    override fun close() {
        isClosed = true
        coordinator.close()
        decisionClock.reset()
        pausedAuthorityTimeMillis = null
        lastCompletedRoundId = null
        lastCompletedSettlementEvents = emptyList()
        shufflePreparationHandler.reset()
        storedShufflePlans.clear()
        storedReceipts.clear()
        roundResultEvents.clear()
    }

    @Synchronized
    fun updateLobbyConfig(newConfig: MatchConfig, newTimeControl: TimeControlConfig = this.timeControl): Boolean {
        if (hasStarted) {
            return false
        }
        if (isPreparationActive() || shufflePreparationHandler.currentRoundId() != null) {
            return false
        }
        if (coordinator.session.roundRecords.isNotEmpty()) {
            return false
        }
        if (coordinator.session.lifecycle in setOf(
                SessionLifecycle.ROUND_RESULT,
                SessionLifecycle.WAITING_DEALER_CHOICE,
                SessionLifecycle.WAITING_READY,
                SessionLifecycle.FINISHED,
                SessionLifecycle.PAUSED,
                SessionLifecycle.CLOSED
            )
        ) {
            return false
        }
        val round = coordinator.activeRound ?: return false
        val s = round.readState()
        if (s.phase != GamePhase.STACKING || s.wall.isNotEmpty() || s.players.any { it.closedTiles.isNotEmpty() }) {
            return false
        }

        newConfig.validateSupported()
        TimeControlConfig(newTimeControl.baseMillis, newTimeControl.reserveMillis, newTimeControl.reserveScope)

        val sanitized = sanitizeConfig(newConfig)
        val coordSuccess = coordinator.updateLobbyConfig(sanitized)
        if (!coordSuccess) {
            return false
        }

        this.config = sanitized
        this.timeControl = newTimeControl
        val freshClock = DecisionClock(config = newTimeControl, clock = clock)
        if (!sanitized.shuffleConfig.enabled) {
            val active = coordinator.activeRound
            if (active != null && active.state.phase != GamePhase.ROUND_END) {
                freshClock.beginRound(active.roundId)
                lastActiveRoundId = active.roundId
            }
        }
        this.decisionClock = freshClock
        return true
    }

    companion object {
        private val BUILT_IN_AI_CAPABILITY_IDS: List<String> = listOf(
            AiCapabilityIds.SHUFFLE_PREPARATION,
            AiCapabilityIds.TILE_MARK,
            AiCapabilityIds.SLEEVE_BLOOM,
            AiCapabilityIds.BEAM_SWAP
        )
        private val BUILT_IN_AI_CAPABILITY_ID_SET: Set<String> = BUILT_IN_AI_CAPABILITY_IDS.toSet()

        fun sanitizeConfig(config: MatchConfig): MatchConfig {
            val effectiveCheatEconomy = if (config.shuffleConfig.enabled && !config.cheatEconomy.enabled) {
                val presetPoints = if (config.winds == listOf(1)) 50 else 100
                CheatEconomyConfig(
                    enabled = true,
                    initialPoints = List(4) { presetPoints },
                    costs = mapOf("shuffle_target" to 30, "target" to 30)
                )
            } else {
                config.cheatEconomy
            }
            return config.copy(
                startingScores = config.startingScores.toList(),
                winds = config.winds.toList(),
                tieBreakSeats = config.tieBreakSeats.toList(),
                cheatEconomy = effectiveCheatEconomy.copy(
                    initialPoints = effectiveCheatEconomy.initialPoints.toList(),
                    costs = HashMap(effectiveCheatEconomy.costs)
                ),
                shuffleConfig = config.shuffleConfig.copy(),
                tileMarkConfig = config.tileMarkConfig.copy(),
                sleeveBloomConfig = config.sleeveBloomConfig.copy(),
                beamSwapConfig = config.beamSwapConfig.copy(),
                aiCheatPolicy = config.aiCheatPolicy.copy(
                    capabilityToggles = HashMap(config.aiCheatPolicy.capabilityToggles)
                )
            )
        }
    }
}

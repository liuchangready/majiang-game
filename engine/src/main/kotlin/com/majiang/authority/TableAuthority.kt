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
import com.majiang.application.ActionChoice
import com.majiang.application.ApplicationCoordinator
import com.majiang.application.PlayerDecision
import com.majiang.application.deepCopy
import com.majiang.cheat.beam.*
import com.majiang.cheat.bloom.*
import com.majiang.cheat.mark.TileMarkConfig
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
import com.majiang.round.RoundPreparationRecord
import com.majiang.session.PlayerView
import com.majiang.session.SessionUpdate
import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShufflePreparationStatus
import com.majiang.stacking.ShufflePreparationView
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleWallGenerator
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
import java.security.SecureRandom
import java.util.Collections
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
    private enum class SubmissionKind { CHOICE, TILE_MARK, SLEEVE_BLOOM, SLEEVE_BLOOM_CHOICE, BEAM_SWAP, BEAM_SWAP_CHOICE }
    private data class NormalizedTileMarkPayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long,
        val normalizedTileIds: List<Int>
    )
    private data class NormalizedSleeveBloomPayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long
    )
    private data class NormalizedSleeveBloomChoicePayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long,
        val offerId: String,
        val keep: SleeveBloomKeep
    )
    private data class NormalizedBeamSwapPayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long
    )
    private data class NormalizedBeamSwapChoicePayload(
        val sessionId: String,
        val roundId: String,
        val decisionId: String,
        val controlEpoch: Long,
        val offerId: String,
        val action: com.majiang.cheat.beam.BeamSwapAction,
        val handTileId: Int?,
        val targetSlot: com.majiang.cheat.beam.BeamSwapSlot?
    )
    private data class StoredSubmission(
        val kind: SubmissionKind,
        val receipt: ActionReceipt,
        val choice: ActionChoice? = null,
        val tileMarkPayload: NormalizedTileMarkPayload? = null,
        val sleeveBloomPayload: NormalizedSleeveBloomPayload? = null,
        val sleeveBloomChoicePayload: NormalizedSleeveBloomChoicePayload? = null,
        val beamSwapPayload: NormalizedBeamSwapPayload? = null,
        val beamSwapChoicePayload: NormalizedBeamSwapChoicePayload? = null
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

    // 洗牌千术准备阶段状态 (Task ANTI-SHUFFLE-B)
    private var preparationRoundId: String? = null
    private var prepStatus: ShufflePreparationStatus = ShufflePreparationStatus.SELECTING
    private var prepOpensAtMillis: Long = 0L
    private var prepDeadlineMillis: Long = 0L
    private var prepRoundSeed: Long = 0L
    private val prepConfirmed = BooleanArray(4) { false }
    private val prepSelections = arrayOfNulls<ShuffleSelection>(4)
    private val prepReserved = IntArray(4) { 0 }

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

    private val secureRandom = SecureRandom()

    // 权威只读门面：收拢对外暴露，不对外暴露协调器及可变Session
    val sessionId: String get() = coordinator.session.sessionId
    val sessionLifecycle: SessionLifecycle get() = coordinator.session.lifecycle
    val currentDealer: Int get() = coordinator.session.currentDealer
    fun controlEpochFor(seat: Int): Long = coordinator.epochFor(seat)

    val currentRoundId: String? get() = coordinator.activeRound?.roundId
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

        val allEvents: List<GameEvent> get() = when (this) {
            is PreparationDealt -> events
            is TimeoutResolved -> events
            is DealerChoiceTaken -> events
            is NextRoundStarted -> events
            is RiichiAutoDiscarded -> events
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
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) return false
        val prepId = preparationRoundId ?: return false
        val activeRound = coordinator.activeRound ?: return false
        return activeRound.roundId == prepId &&
            activeRound.state.phase == GamePhase.STACKING &&
            activeRound.state.wall.isEmpty()
    }

    fun preparationDeadline(): Long = prepDeadlineMillis

    fun buildPreparationViewForSeat(seat: Int): ShufflePreparationView? {
        if (!isPreparationActive()) return null
        val roundId = preparationRoundId ?: return null
        val round = coordinator.activeRound
        val record = if (round != null && round.roundId == roundId) round.preparationRecord else null

        val confirmed = record?.confirmed?.getOrNull(seat) ?: prepConfirmed[seat]
        val selection = record?.selections?.getOrNull(seat) ?: prepSelections[seat]
        val reserved = record?.reserved?.getOrNull(seat) ?: prepReserved[seat]

        val now = if (decisionClock.isPausedState && pausedAuthorityTimeMillis != null) {
            pausedAuthorityTimeMillis!!
        } else {
            clock.nowMillis()
        }
        val currentPoints = if (config.cheatEconomy.enabled) {
            if (round != null && round.readState().cheatPoints.size > seat) {
                round.readState().cheatPoints[seat]
            } else {
                coordinator.session.betweenRoundCheatPoints.getOrElse(seat) { 0 }
            }
        } else {
            0
        }
        val available = maxOf(0, currentPoints - reserved)
        return ShufflePreparationView(
            roundId = roundId,
            status = prepStatus,
            opensAtMillis = prepOpensAtMillis,
            deadlineMillis = prepDeadlineMillis,
            serverNowMillis = now,
            confirmed = confirmed,
            selection = selection?.deepCopy(),
            reservedPoints = reserved,
            availablePoints = available
        )
    }

    private fun startPreparation(roundId: String, now: Long) {
        preparationRoundId = roundId
        prepStatus = ShufflePreparationStatus.SELECTING
        prepOpensAtMillis = now
        prepDeadlineMillis = prepOpensAtMillis + config.shuffleConfig.selectionMillis
        prepRoundSeed = secureRandom.nextLong()
        prepConfirmed.fill(false)
        prepSelections.fill(null)
        prepReserved.fill(0)
        coordinator.activeRound?.let { round ->
            if (round.roundId == roundId) {
                round.updatePreparationRecord(
                    RoundPreparationRecord(
                        roundId = roundId,
                        seed = prepRoundSeed,
                        selections = prepSelections.map { it?.deepCopy() },
                        confirmed = prepConfirmed.toList(),
                        reserved = prepReserved.toList()
                    )
                )
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
        if (request.controlEpoch != currentEpoch) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_EPOCH"), emptyList(), false)
        }

        if (!isPreparationActive() || request.roundId != preparationRoundId || prepStatus != ShufflePreparationStatus.SELECTING) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE"), emptyList(), false)
        }

        if (now >= prepDeadlineMillis) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "PREPARATION_EXPIRED"), emptyList(), false)
        }

        if (prepConfirmed[seat]) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ALREADY_CONFIRMED"), emptyList(), false)
        }

        val safeSelection = request.selection.deepCopy()
        val cost = safeSelection.cost()
        if (cost !in 0..60) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_COST"), emptyList(), false)
        }

        val currentPoints = coordinator.activeRound?.state?.cheatPoints?.get(seat) ?: 0
        if (currentPoints < cost) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INSUFFICIENT_FUNDS"), emptyList(), false)
        }

        prepConfirmed[seat] = true
        prepSelections[seat] = safeSelection
        prepReserved[seat] = cost
        coordinator.activeRound?.let { round ->
            if (round.roundId == preparationRoundId) {
                round.updatePreparationRecord(
                    RoundPreparationRecord(
                        roundId = round.roundId,
                        seed = prepRoundSeed,
                        selections = prepSelections.map { it?.deepCopy() },
                        confirmed = prepConfirmed.toList(),
                        reserved = prepReserved.toList()
                    )
                )
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
            val reserved = prepReserved[seat]
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
    fun submitTileMark(seat: Int, request: TileMarkRequest, now: Long): MutationResult {
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
        val currentPayload = NormalizedTileMarkPayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch,
            normalizedTileIds = request.tileIds.sorted()
        )
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

        if (isPreparationActive()) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 2. 当前有效局与代次/决策标识校验
        val round = coordinator.activeRound
        if (round == null || round.state.phase == GamePhase.ROUND_END || request.roundId != round.roundId || request.sessionId != coordinator.session.sessionId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val currentDecision = coordinator.queryDecision(seat)
        if (request.controlEpoch != currentDecision.controlEpoch) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_CONTROL_EPOCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (request.decisionId != currentDecision.decisionId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val controller = coordinator.controllerFor(seat)
        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 3. 权威截止时间校验
        val timing = decisionClock.timing(seat)
        val decisionKey = DecisionKey(round.roundId, seat, request.decisionId)
        if (timing == null || timing.key != decisionKey) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NO_ACTIVE_DECISION")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (now >= timing.deadlineMillis) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 4. 阶段与回合校验
        val candidateState = round.readState()
        if (candidateState.currentPlayer != seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NOT_YOUR_TURN")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.sleeveBloomOffer != null && candidateState.sleeveBloomOffer?.seat == seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_CHOICE_PENDING")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.beamSwapOffer != null && candidateState.beamSwapOffer?.seat == seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_CHOICE_PENDING")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD) || round.responseWindow != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.riichiDeclared[seat]) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "TILE_MARK_RIICHI_LOCKED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 5. 牌张选择与归属校验
        if (request.tileIds.size != 4 || request.tileIds.distinct().size != 4 || request.tileIds.any { it !in 0..135 }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "TILE_MARK_INVALID_SELECTION")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        val playerHand = candidateState.players[seat].closedTiles + listOfNotNull(candidateState.players[seat].drawnTile)
        val playerTileIds = playerHand.map { it.tileId }.toSet()
        if (!request.tileIds.all { it in playerTileIds }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "TILE_MARK_TILE_NOT_OWNED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        val seatGroups = coordinator.session.tileMarkState.groupsForSeat(seat)
        val seatMarkedIds = seatGroups.flatMap { it.tileIds }.toSet()
        if (request.tileIds.any { it in seatMarkedIds }) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "TILE_MARK_ALREADY_MARKED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (seatGroups.size >= 3 || seatMarkedIds.size + 4 > 12) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "TILE_MARK_CAPACITY_EXCEEDED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 6. 费用校验
        val cost = config.cheatEconomy.costs["TILE_MARK"] ?: 10
        if (candidateState.cheatPoints[seat] < cost) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INSUFFICIENT_FUNDS")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, rejected, tileMarkPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 7. 执行原子扣费与安装标记
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

        // 8. 计时 rekey 延续（保留截止时间与储备）
        val receipt = ActionReceipt(request.requestId, ReceiptStatus.APPLIED)
        if (!isClosed && coordinator.session.lifecycle != SessionLifecycle.CLOSED) {
            val newDecision = coordinator.queryDecision(seat)
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.TILE_MARK, receipt, tileMarkPayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交袖里藏花激活请求
     */
    @Synchronized
    fun submitSleeveBloom(seat: Int, request: SleeveBloomRequest, now: Long): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = NormalizedSleeveBloomPayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch
        )
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

        // 首次请求门禁校验
        if (!config.sleeveBloomConfig.enabled || !config.cheatEconomy.enabled || config.cheatEconomy.costs["SLEEVE_BLOOM"] != 10) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_DISABLED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        if (isPreparationActive()) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 2. 当前有效局与代次/决策标识校验
        val round = coordinator.activeRound
        if (round == null || round.state.phase == GamePhase.ROUND_END || request.roundId != round.roundId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val currentDecision = coordinator.queryDecision(seat)
        if (request.controlEpoch != currentDecision.controlEpoch) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_CONTROL_EPOCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (request.decisionId != currentDecision.decisionId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val controller = coordinator.controllerFor(seat)
        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 3. 权威截止时间校验
        val timing = decisionClock.timing(seat)
        val decisionKey = DecisionKey(round.roundId, seat, request.decisionId)
        if (timing == null || timing.key != decisionKey) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NO_ACTIVE_DECISION")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (now >= timing.deadlineMillis) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 4. 阶段与回合校验
        val candidateState = round.readState()
        if (candidateState.currentPlayer != seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NOT_YOUR_TURN")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.phase != GamePhase.CHEAT_WINDOW || round.responseWindow != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        val drawnTile = candidateState.players[seat].drawnTile
        if (drawnTile == null || candidateState.tilePool.getLocation(drawnTile) != com.majiang.model.TileLocation.hand(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_NO_DRAWN_TILE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.riichiDeclared[seat]) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_RIICHI_LOCKED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.sleeveBloomOffer != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_ALREADY_PENDING")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.beamSwapOffer != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_ALREADY_PENDING")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.sleeveBloomUsedOnTurn == candidateState.turnNumber || candidateState.beamSwapUsedOnTurn == candidateState.turnNumber) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.lastDrawKind == SleeveBloomDrawKind.NORMAL) {
            val nextTile = candidateState.wall.getOrNull(candidateState.wallPointer)
            if (candidateState.wallRemaining < 1 ||
                candidateState.wallPointer >= candidateState.wallEndExclusive ||
                nextTile == null ||
                candidateState.tilePool.getLocation(nextTile) != com.majiang.model.TileLocation.WALL
            ) {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_WALL_EMPTY")
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
        } else if (candidateState.lastDrawKind == SleeveBloomDrawKind.RINSHAN) {
            val rinshanValid = candidateState.rinshanPointer in 1..3 &&
                candidateState.totalKanCount < 4 &&
                candidateState.deadWall.getOrNull(candidateState.rinshanPointer - 1) == drawnTile &&
                candidateState.deadWall.getOrNull(candidateState.rinshanPointer)?.let {
                    candidateState.tilePool.getLocation(it) == com.majiang.model.TileLocation.DEAD_WALL
                } == true
            if (!rinshanValid) {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_WALL_EMPTY")
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
        } else {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 5. 费用校验
        val cost = config.cheatEconomy.costs["SLEEVE_BLOOM"] ?: 10
        if (candidateState.cheatPoints[seat] < cost) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INSUFFICIENT_FUNDS")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, rejected, sleeveBloomPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 6. 执行原子扣费与安装 offer
        val coordResult = coordinator.submitSleeveBloom(seat, request, now)
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
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM, receipt, sleeveBloomPayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交袖里藏花二选一决策请求
     */
    @Synchronized
    fun submitSleeveBloomChoice(seat: Int, request: SleeveBloomChoiceRequest, now: Long): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = NormalizedSleeveBloomChoicePayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch,
            offerId = request.offerId,
            keep = request.keep
        )
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

        // 首次请求门禁校验
        if (!config.sleeveBloomConfig.enabled || !config.cheatEconomy.enabled || config.cheatEconomy.costs["SLEEVE_BLOOM"] != 10) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_DISABLED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        if (isPreparationActive()) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 2. 当前有效局与代次/决策标识校验
        val round = coordinator.activeRound
        if (round == null || round.state.phase == GamePhase.ROUND_END || request.roundId != round.roundId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val currentDecision = coordinator.queryDecision(seat)
        if (request.controlEpoch != currentDecision.controlEpoch) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_CONTROL_EPOCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (request.decisionId != currentDecision.decisionId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val controller = coordinator.controllerFor(seat)
        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 3. 权威截止时间校验
        val timing = decisionClock.timing(seat)
        val decisionKey = DecisionKey(round.roundId, seat, request.decisionId)
        if (timing == null || timing.key != decisionKey) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NO_ACTIVE_DECISION")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (now >= timing.deadlineMillis) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 4. 阶段与回合校验
        val candidateState = round.readState()
        if (candidateState.currentPlayer != seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NOT_YOUR_TURN")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.phase != GamePhase.CHEAT_WINDOW || round.responseWindow != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 5. 挂起 offer 匹配与局面一致性
        val offer = candidateState.sleeveBloomOffer
        if (offer == null || offer.seat != seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_NO_PENDING_OFFER")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (offer.offerId != request.offerId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_OFFER_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val isConsistent = candidateState.turnNumber == offer.createdTurnNumber &&
                candidateState.players[seat].drawnTile == offer.firstTile &&
                candidateState.tilePool.getLocation(offer.firstTile) == com.majiang.model.TileLocation.hand(seat) &&
                when (offer.drawKind) {
                    SleeveBloomDrawKind.NORMAL -> {
                        candidateState.wallPointer < candidateState.wallEndExclusive &&
                                candidateState.wall.getOrNull(candidateState.wallPointer) == offer.secondTile &&
                                candidateState.tilePool.getLocation(offer.secondTile) == com.majiang.model.TileLocation.WALL
                    }
                    SleeveBloomDrawKind.RINSHAN -> {
                        candidateState.rinshanPointer in 1..3 &&
                                candidateState.totalKanCount < 4 &&
                                candidateState.deadWall.getOrNull(candidateState.rinshanPointer - 1) == offer.firstTile &&
                                candidateState.deadWall.getOrNull(candidateState.rinshanPointer) == offer.secondTile &&
                                candidateState.tilePool.getLocation(offer.secondTile) == com.majiang.model.TileLocation.DEAD_WALL
                    }
                    SleeveBloomDrawKind.DEAL_EXTRA -> false
                }

        if (!isConsistent) {
            coordinator.cleanStaleSleeveBloomOffer(seat)
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SLEEVE_BLOOM_STALE_OFFER")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, rejected, sleeveBloomChoicePayload = currentPayload)
            val newDecision = coordinator.queryDecision(seat)
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
            return MutationResult(rejected, emptyList(), true)
        }

        // 6. 执行选择决策
        val coordResult = coordinator.submitSleeveBloomChoice(seat, request)
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
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.SLEEVE_BLOOM_CHOICE, receipt, sleeveBloomChoicePayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交偷梁换柱激活请求
     */
    @Synchronized
    fun submitBeamSwap(seat: Int, request: BeamSwapRequest, now: Long): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = NormalizedBeamSwapPayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch
        )
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

        // 首次请求门禁校验
        if (!config.beamSwapConfig.enabled || !config.cheatEconomy.enabled || config.cheatEconomy.costs["BEAM_SWAP"] != 10) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_DISABLED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        if (isPreparationActive()) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 2. 当前有效局与代次/决策标识校验
        val round = coordinator.activeRound
        if (round == null || round.state.phase == GamePhase.ROUND_END || request.roundId != round.roundId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val currentDecision = coordinator.queryDecision(seat)
        if (request.controlEpoch != currentDecision.controlEpoch) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_CONTROL_EPOCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (request.decisionId != currentDecision.decisionId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val controller = coordinator.controllerFor(seat)
        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 3. 权威截止时间校验
        val timing = decisionClock.timing(seat)
        val decisionKey = DecisionKey(round.roundId, seat, request.decisionId)
        if (timing == null || timing.key != decisionKey) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NO_ACTIVE_DECISION")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (now >= timing.deadlineMillis) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 4. 阶段与回合校验
        val candidateState = round.readState()
        if (candidateState.currentPlayer != seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NOT_YOUR_TURN")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.phase != GamePhase.CHEAT_WINDOW || round.responseWindow != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        val drawnTile = candidateState.players[seat].drawnTile
        if (drawnTile == null || candidateState.tilePool.getLocation(drawnTile) != com.majiang.model.TileLocation.hand(seat)) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_NO_DRAWN_TILE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        val eligibleHand = candidateState.players[seat].closedTiles.filter {
            it != drawnTile && candidateState.tilePool.getLocation(it) == com.majiang.model.TileLocation.hand(seat)
        }
        if (eligibleHand.isEmpty()) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_NO_HAND_TILES")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.riichiDeclared[seat]) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_RIICHI_LOCKED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_DEALER_EXTRA_EXCLUDED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.beamSwapOffer != null || candidateState.sleeveBloomOffer != null) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_ALREADY_PENDING")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (candidateState.beamSwapUsedOnTurn == candidateState.turnNumber || candidateState.sleeveBloomUsedOnTurn == candidateState.turnNumber) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_ALREADY_USED_THIS_DRAW")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 候选充足性检查
        if (candidateState.lastDrawKind == SleeveBloomDrawKind.NORMAL) {
            if (candidateState.wallRemaining < 1 || candidateState.wallPointer >= candidateState.wallEndExclusive) {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_NO_CANDIDATES")
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
        } else if (candidateState.lastDrawKind == SleeveBloomDrawKind.RINSHAN) {
            var deadWallCandidateCount = 0
            for (i in candidateState.rinshanPointer..3) {
                val t = candidateState.deadWall.getOrNull(i)
                if (t != null && candidateState.tilePool.getLocation(t) == com.majiang.model.TileLocation.DEAD_WALL) deadWallCandidateCount++
            }
            for (i in 4..13) {
                val t = candidateState.deadWall.getOrNull(i)
                if (t != null && candidateState.tilePool.getLocation(t) == com.majiang.model.TileLocation.DEAD_WALL && !candidateState.doraIndicators.contains(t)) deadWallCandidateCount++
            }
            for (i in 0 until candidateState.deadWallReplacements.size) {
                val t = candidateState.deadWallReplacements.getOrNull(i)
                if (t != null && candidateState.tilePool.getLocation(t) == com.majiang.model.TileLocation.DEAD_WALL) deadWallCandidateCount++
            }
            if (deadWallCandidateCount == 0) {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_NO_CANDIDATES")
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
        } else {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 5. 费用校验
        val cost = config.cheatEconomy.costs["BEAM_SWAP"] ?: 10
        if (candidateState.cheatPoints[seat] < cost) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INSUFFICIENT_FUNDS")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, rejected, beamSwapPayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 6. 执行原子扣费与安装 offer
        val coordResult = coordinator.submitBeamSwap(seat, request, now)
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
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP, receipt, beamSwapPayload = currentPayload)
            recordSettlementEvents(coordResult.events)
        }

        return MutationResult(receipt, coordResult.events, true)
    }

    /**
     * 提交偷梁换柱选择决策请求
     */
    @Synchronized
    fun submitBeamSwapChoice(seat: Int, request: BeamSwapChoiceRequest, now: Long): MutationResult {
        if (seat !in 0..3) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "INVALID_SEAT"), emptyList(), false)
        }
        if (request.sessionId != coordinator.session.sessionId) {
            return MutationResult(ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"), emptyList(), false)
        }

        // 1. 幂等与冲突校验
        val currentPayload = NormalizedBeamSwapChoicePayload(
            sessionId = request.sessionId,
            roundId = request.roundId,
            decisionId = request.decisionId,
            controlEpoch = request.controlEpoch,
            offerId = request.offerId,
            action = request.action,
            handTileId = request.handTileId,
            targetSlot = request.targetSlot
        )
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

        // 首次请求门禁校验
        if (!config.beamSwapConfig.enabled || !config.cheatEconomy.enabled || config.cheatEconomy.costs["BEAM_SWAP"] != 10) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_DISABLED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (decisionClock.isPausedState) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "HOST_PAUSED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        if (isPreparationActive()) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "WRONG_PHASE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 2. 当前有效局与代次/决策标识校验
        val round = coordinator.activeRound
        if (round == null || round.state.phase == GamePhase.ROUND_END || request.roundId != round.roundId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val currentDecision = coordinator.queryDecision(seat)
        if (request.controlEpoch != currentDecision.controlEpoch) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "STALE_CONTROL_EPOCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (request.decisionId != currentDecision.decisionId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val controller = coordinator.controllerFor(seat)
        if (controller !is SeatController.LocalHuman && controller !is SeatController.RemoteHuman) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        // 3. 权威截止时间校验
        val timing = decisionClock.timing(seat)
        val decisionKey = DecisionKey(round.roundId, seat, request.decisionId)
        if (timing == null || timing.key != decisionKey) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "NO_ACTIVE_DECISION")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (now >= timing.deadlineMillis) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "DEADLINE_EXPIRED")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        val candidateState = round.readState()

        // 4. 挂起 offer 匹配与选择格式校验
        val offer = candidateState.beamSwapOffer
        if (offer == null || offer.seat != seat) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_NO_PENDING_OFFER")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }
        if (offer.offerId != request.offerId) {
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_OFFER_MISMATCH")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            return MutationResult(rejected, emptyList(), false)
        }

        if (request.action == BeamSwapAction.SWAP) {
            val handTileId = request.handTileId
            val targetSlot = request.targetSlot
            if (handTileId == null || targetSlot == null || handTileId !in offer.eligibleHandTileIds || offer.candidates.none { it.slot == targetSlot }) {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_INVALID_CHOICE")
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
        } else if (request.action == BeamSwapAction.KEEP_ORIGINAL) {
            if (request.handTileId != null || request.targetSlot != null) {
                val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_INVALID_CHOICE")
                storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
                return MutationResult(rejected, emptyList(), false)
            }
        }

        // 5. 局面一致性（漂移校验）
        val isPhaseConsistent = candidateState.currentPlayer == seat &&
            candidateState.phase == GamePhase.CHEAT_WINDOW &&
            round.responseWindow == null
        val isDrawKindConsistent = when (offer.window) {
            BeamSwapWindow.WALL -> candidateState.lastDrawKind == SleeveBloomDrawKind.NORMAL
            BeamSwapWindow.DEAD_WALL -> candidateState.lastDrawKind == SleeveBloomDrawKind.RINSHAN
        }

        val drawnTile = candidateState.players[seat].drawnTile
        val currentEligibleHandIds = candidateState.players[seat].closedTiles.filter {
            it != drawnTile && candidateState.tilePool.getLocation(it) == com.majiang.model.TileLocation.hand(seat)
        }.map { it.tileId }.toSet()
        val handConsistent = candidateState.turnNumber == offer.createdTurnNumber &&
            drawnTile != null && drawnTile.tileId == offer.drawnTileId &&
            candidateState.tilePool.getLocation(drawnTile) == com.majiang.model.TileLocation.hand(seat) &&
            currentEligibleHandIds == offer.eligibleHandTileIds.toSet()

        val candidatesConsistent = offer.candidates.all { cand ->
            when (val slot = cand.slot) {
                is BeamSwapSlot.WallSlot -> {
                    candidateState.wallPointer <= slot.index && slot.index < candidateState.wallEndExclusive &&
                        candidateState.wall.getOrNull(slot.index)?.tileId == cand.tileId &&
                        candidateState.wall.getOrNull(slot.index)?.let { candidateState.tilePool.getLocation(it) == com.majiang.model.TileLocation.WALL } == true
                }
                is BeamSwapSlot.DeadWallSlot -> {
                    val tile = candidateState.deadWall.getOrNull(slot.index)
                    tile != null && tile.tileId == cand.tileId &&
                        candidateState.tilePool.getLocation(tile) == com.majiang.model.TileLocation.DEAD_WALL &&
                        !candidateState.doraIndicators.contains(tile) &&
                        (slot.index in candidateState.rinshanPointer..3 || slot.index in 4..13)
                }
                is BeamSwapSlot.ReplacementSlot -> {
                    val tile = candidateState.deadWallReplacements.getOrNull(slot.index)
                    tile != null && tile.tileId == cand.tileId &&
                        candidateState.tilePool.getLocation(tile) == com.majiang.model.TileLocation.DEAD_WALL
                }
            }
        }

        if (!isPhaseConsistent || !isDrawKindConsistent || !handConsistent || !candidatesConsistent) {
            coordinator.cleanStaleBeamSwapOffer(seat)
            val rejected = ActionReceipt(request.requestId, ReceiptStatus.REJECTED, reason = "BEAM_SWAP_STALE_OFFER")
            storedReceipts[receiptKey] = StoredSubmission(SubmissionKind.BEAM_SWAP_CHOICE, rejected, beamSwapChoicePayload = currentPayload)
            val newDecision = coordinator.queryDecision(seat)
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
            return MutationResult(rejected, emptyList(), true)
        }

        // 6. 执行选择决策
        val coordResult = coordinator.submitBeamSwapChoice(seat, request)
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
            val newKey = DecisionKey(round.roundId, seat, newDecision.decisionId)
            decisionClock.rekey(timing.key, newKey)
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

        var aiUpdated = false
        for (s in 0 until 4) {
            val isNative = s in nativeAiSeats
            if (s in automatedSeats && !prepConfirmed[s]) {
                if (isNative && config.shuffleConfig.aiMaySpend) {
                    val currentPoints = round.state.cheatPoints.getOrElse(s) { 0 }
                    val selection = when {
                        currentPoints >= 60 -> ShuffleSelection(
                            initialTargets = setOf(ShuffleTarget.CHINITSU_MAN),
                            drawTargets = setOf(ShuffleTarget.DAISANGEN)
                        )
                        currentPoints >= 40 -> ShuffleSelection(
                            initialTargets = setOf(ShuffleTarget.CHINITSU_MAN)
                        )
                        currentPoints >= 20 -> ShuffleSelection(
                            drawTargets = setOf(ShuffleTarget.DAISANGEN)
                        )
                        else -> ShuffleSelection()
                    }
                    prepConfirmed[s] = true
                    prepSelections[s] = selection
                    prepReserved[s] = selection.cost()
                    aiUpdated = true
                } else {
                    prepConfirmed[s] = true
                    prepSelections[s] = ShuffleSelection()
                    prepReserved[s] = 0
                    aiUpdated = true
                }
            }
        }
        if (aiUpdated) {
            round.updatePreparationRecord(
                RoundPreparationRecord(
                    roundId = round.roundId,
                    seed = prepRoundSeed,
                    selections = prepSelections.map { it?.deepCopy() },
                    confirmed = prepConfirmed.toList(),
                    reserved = prepReserved.toList()
                )
            )
        }

        if (now >= prepDeadlineMillis) {
            prepStatus = ShufflePreparationStatus.GENERATING
            var timeoutUpdated = false
            for (s in 0 until 4) {
                if (!prepConfirmed[s]) {
                    prepConfirmed[s] = true
                    prepSelections[s] = ShuffleSelection()
                    prepReserved[s] = 0
                    timeoutUpdated = true
                }
            }
            if (timeoutUpdated) {
                round.updatePreparationRecord(
                    RoundPreparationRecord(
                        roundId = round.roundId,
                        seed = prepRoundSeed,
                        selections = prepSelections.map { it?.deepCopy() },
                        confirmed = prepConfirmed.toList(),
                        reserved = prepReserved.toList()
                    )
                )
            }

            val record = round.preparationRecord
            val plans = (0 until 4).map {
                val sel = record?.selections?.getOrNull(it) ?: prepSelections[it] ?: ShuffleSelection()
                SeatShufflePlan(it, sel)
            }
            val seedToUse = record?.seed ?: prepRoundSeed
            val generator = ShuffleWallGenerator()
            val tiles = try {
                generator.generate(
                    seed = seedToUse,
                    dealerSeat = round.state.dealerIndex,
                    plans = plans,
                    config = config.shuffleConfig
                )
            } catch (t: Throwable) {
                null
            }

            if (tiles != null && tiles.size == 136) {
                val costs = (0 until 4).associateWith { plans[it].selection.cost() }
                val reasons = (0 until 4).associateWith { "shuffle_plan" }
                val dealResult = coordinator.executeDealWithPreparation(
                    roundId = round.roundId,
                    wallTiles = tiles,
                    cheatCosts = costs,
                    costReasons = reasons
                )
                if (dealResult is ActionResult.Accepted) {
                    preparationRoundId = null
                    prepStatus = ShufflePreparationStatus.SELECTING
                    decisionClock.beginRound(round.roundId)
                    lastActiveRoundId = round.roundId
                    recordSettlementEvents(dealResult.events)
                    return AutoStepResult.PreparationDealt(dealResult.events)
                }
            }
            prepStatus = ShufflePreparationStatus.GENERATING
        }
        return null
    }

    @Synchronized
    fun checkTimeout(now: Long): AutoStepResult? {
        if (isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED) return null
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
    fun checkRiichiAutoDiscard(now: Long, humanAutoRiichiSeats: Set<Int>): AutoStepResult? {
        val round = coordinator.activeRound
        if (round != null && round.state.phase in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD)) {
            val currPlayer = round.state.currentPlayer
            if (currPlayer in humanAutoRiichiSeats &&
                round.state.riichiDeclared[currPlayer] &&
                round.state.players[currPlayer].drawnTile != null
            ) {
                if (round.state.sleeveBloomOffer?.seat == currPlayer) {
                    coordinator.resolveSleeveBloomKeepFirst(currPlayer)
                    val oldTiming = decisionClock.timing(currPlayer)
                    if (oldTiming != null) {
                        val newDecision = coordinator.queryDecision(currPlayer)
                        val newKey = DecisionKey(round.roundId, currPlayer, newDecision.decisionId)
                        decisionClock.rekey(oldTiming.key, newKey)
                    }
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
                if (!hasTsumo && !hasKan) {
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

                if (s in automatedSeats && round?.state?.sleeveBloomOffer?.seat != s && round?.state?.beamSwapOffer?.seat != s) {
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
            if (pausedAt != null && isPreparationActive()) {
                val pauseDuration = maxOf(0L, now - pausedAt)
                prepOpensAtMillis += pauseDuration
                prepDeadlineMillis += pauseDuration
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
            return PlayerDecision(
                roundId = preparationRoundId,
                decisionId = "preparation-$preparationRoundId-$seat",
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
        preparationRoundId = null
        prepStatus = ShufflePreparationStatus.SELECTING
        prepOpensAtMillis = 0L
        prepDeadlineMillis = 0L
        prepRoundSeed = 0L
        prepConfirmed.fill(false)
        prepSelections.fill(null)
        prepReserved.fill(0)
        storedShufflePlans.clear()
        storedReceipts.clear()
        roundResultEvents.clear()
    }

    @Synchronized
    fun updateLobbyConfig(newConfig: MatchConfig, newTimeControl: TimeControlConfig = this.timeControl): Boolean {
        if (hasStarted) {
            return false
        }
        if (isPreparationActive() || preparationRoundId != null) {
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
        fun sanitizeConfig(config: MatchConfig): MatchConfig {
            val effectiveCheatEconomy = if (config.shuffleConfig.enabled && !config.cheatEconomy.enabled) {
                val presetPoints = if (config.winds == listOf(1)) 50 else 100
                CheatEconomyConfig(
                    enabled = true,
                    initialPoints = List(4) { presetPoints },
                    costs = mapOf("shuffle_initial" to 40, "shuffle_draw" to 20, "initial" to 40, "draw" to 20)
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
                beamSwapConfig = config.beamSwapConfig.copy()
            )
        }
    }
}

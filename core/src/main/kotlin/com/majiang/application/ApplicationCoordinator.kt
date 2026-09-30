package com.majiang.application

import com.majiang.ai.AIPlayer
import com.majiang.ai.BasicAIStrategy
import com.majiang.event.GameEvent
import com.majiang.event.GameEventBus
import com.majiang.event.snapshotGameEvent
import com.majiang.match.*
import com.majiang.round.*
import com.majiang.rules.*
import com.majiang.session.PlayerView
import com.majiang.settlement.*
import com.majiang.state.*
import java.util.Collections
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.withLock
import kotlin.random.Random

/**
 * 应用层协调器 (ApplicationCoordinator) - V2 生产级单桌串行与外部协调中心
 *
 * 核心职责：
 * 1. 单桌串行保证：统一可重入读写锁 + 原子守卫，并发与回调重入命令一律拒绝并保证零副作用，杜绝死锁与半提交；
 * 2. 独立外部验证与审计：外部驱动 StateInvariants、SessionCommandValidator 与 RoundResultValidator；
 * 3. 单局结果交接：消费 RoundResult，严格核对守恒与账本，驱动 MatchProgression 且仅交接一次；
 * 4. 实例隔离与消除别名：每局发牌（包括连庄与初始局）使用独立 RoundStateMachine 与深拷贝 GameState，旧局彻底冻结；
 * 5. 共享事件广播隔离：完整顶层提交后降级读锁发布 snapshotGameEvent，空事件亦广播状态变更，监听器异常隔离；
 * 6. 控制者与代次管理：委托 ControllerRegistry，支持 0~4 真人/AI 调度与立直自动摸切安全兜底；
 * 7. 决策时效与请求幂等：委托 DecisionRegistry 与 RequestRegistry，内部线程安全同步，按 requestId 去重并做深拷贝隔离；
 * 8. 只读兼容镜像实时同步：为旧兼容适配器提供提交后立即同步与非执行期间显式修改同步机制。
 */
class ApplicationCoordinator(
    initialState: GameState = GameState(),
    val eventBus: GameEventBus = GameEventBus(),
    val autoPlayAi: Boolean = true,
    val autoRiichiDiscard: Boolean = true,
    val random: Random = Random.Default,
    val matchProgression: MatchProgression = MatchProgression(),
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
    val settlementService: SettlementService = SettlementService.INSTANCE,
    val deferAutomaticActions: Boolean = false
) {
    val roundCommandProcessor: RoundCommandProcessor = RoundCommandProcessor(
        ruleEngine = ruleEngine,
        winningScoreEvaluator = winningScoreEvaluator,
        settlementService = settlementService,
        random = random
    )

    val session: MatchSession = MatchSession(
        ruleConfig = matchProgression.config,
        initialScores = initialState.scores.toList(),
        initialDealer = initialState.dealerIndex,
        initialRoundWind = initialState.roundWind,
        initialRoundNumber = initialState.roundNumber,
        initialHonba = initialState.honbaSticks,
        initialRiichiSticks = initialState.riichiSticks
    )

    val activeRound: RoundStateMachine?
        get() = session.activeRound

    // 记录上一局结束时的只读快照，用于局间与终局 PlayerView 展现，防止牌桌手牌/副露消失
    var lastEndedRoundSnapshot: GameState? = null
        private set

    // 独立外部注册与验证组件
    private val controllerRegistry = ControllerRegistry()
    private val decisionRegistry = DecisionRegistry()
    private val requestRegistry = RequestRegistry()

    val hasControllerConfiguration: Boolean
        get() = controllerRegistry.hasExplicitConfiguration

    val humanPlayerIndex: Int
        get() = controllerRegistry.humanPlayerIndex

    // 兼容层镜像目标（仅 GameStateMachine 注册，新路径默认为 null）
    private var legacyMirror: GameState? = null

    // 并发与串行控制边界：统一可重入读写锁保证读写强一致
    private val rwLock = ReentrantReadWriteLock()
    private val isProcessingAction = AtomicBoolean(false)
    private val currentExecutingThread = AtomicReference<Thread?>(null)

    private val subscribers = CopyOnWriteArrayList<(List<GameEvent>) -> Unit>()
    private val consumedResults = mutableSetOf<String>()

    init {
        // 初始局准备：使用 initialState 的深拷贝创建全新 Round 实例，消除状态别名
        val firstRoundState = initialState.deepCopy()
        val firstRound = RoundStateMachine(firstRoundState)
        SessionCommandValidator.validateStartSession(session, firstRound)
        val prep = session.prepare(ApprovedSessionStep.StartSession(firstRound))
        session.install(prep)
    }

    /**
     * 注册兼容层旧 GameStateMachine 的 state 镜像目标
     */
    internal fun attachLegacyMirror(mirror: GameState) {
        rwLock.writeLock().withLock {
            this.legacyMirror = mirror
            syncFromLegacyMirror()
            syncToLegacyMirror()
        }
    }

    /**
     * 提交后立即同步最新权威状态到兼容镜像
     */
    internal fun syncToLegacyMirror() {
        val mirror = legacyMirror ?: return
        val round = activeRound
        if (round != null) {
            round.state.copyInto(mirror)
        } else {
            val snapshot = lastEndedRoundSnapshot
            if (snapshot != null) {
                snapshot.copyInto(mirror)
            }
        }
        if (session.lifecycle == SessionLifecycle.FINISHED) {
            mirror.phase = GamePhase.MATCH_END
            for (i in 0 until 4) mirror.scores[i] = session.betweenRoundScores[i]
            mirror.riichiSticks = 0
        }
    }

    /**
     * 将兼容镜像在非执行期间的显式修改同步到当前活动的权威 Round（保持起始积分基线不重置）
     */
    internal fun syncFromLegacyMirror() {
        val mirror = legacyMirror ?: return
        val round = activeRound ?: return
        if (round.state.phase == GamePhase.ROUND_END) return
        if (session.lifecycle == SessionLifecycle.FINISHED) return

        if (round.version == 0L) {
            mirror.copyInto(round.state)
            round.startingScores = round.state.scores.toList()
            round.startingRiichiSticks = round.state.riichiSticks
            session.syncInitialState(round.state)
        } else {
            mirror.copyInto(round.state)
        }
    }

    /**
     * 在读锁查询前尝试同步（非阻塞，执行中或持有锁回调中安全跳过，防死锁）
     */
    internal fun syncLegacyStateBeforeQuery() {
        if (legacyMirror == null) return
        if (isProcessingAction.get()) return
        if (currentExecutingThread.get() == Thread.currentThread()) return
        if (rwLock.writeLock().tryLock()) {
            try {
                syncFromLegacyMirror()
            } finally {
                rwLock.writeLock().unlock()
            }
        }
    }

    // ========================================
    // 控制者配置与查询
    // ========================================

    fun controllerFor(seat: Int): SeatController {
        rwLock.readLock().withLock {
            return controllerRegistry.getController(seat)
        }
    }

    fun initializeMatch(controllers: List<SeatController>) {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            throw IllegalStateException("ApplicationCoordinator is busy executing an action")
        }
        if (!rwLock.writeLock().tryLock()) {
            throw IllegalStateException("ApplicationCoordinator is busy executing an action")
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            throw IllegalStateException("ApplicationCoordinator is busy executing an action")
        }
        currentExecutingThread.set(Thread.currentThread())

        try {
            syncFromLegacyMirror()
            controllerRegistry.initialize(controllers)
            decisionRegistry.invalidateAll()
            syncToLegacyMirror()
        } finally {
            currentExecutingThread.set(null)
            isProcessingAction.set(false)
            rwLock.writeLock().unlock()
        }
    }

    fun initializeMatch(humanIndex: Int = 0, aiStrategies: Map<Int, AIPlayer> = emptyMap()) {
        if (humanIndex !in -1..3) {
            throw IllegalArgumentException("humanIndex must be in -1..3, got $humanIndex")
        }
        for ((seat, ai) in aiStrategies) {
            if (seat !in 0..3) {
                throw IllegalArgumentException("Seat in aiStrategies must be in 0..3, got $seat")
            }
            if (ai.playerIndex != seat) {
                throw IllegalArgumentException("AIPlayer playerIndex (${ai.playerIndex}) does not match key ($seat)")
            }
            if (humanIndex != -1 && seat == humanIndex) {
                throw IllegalArgumentException("Seat $seat is designated as human player, cannot specify AI strategy")
            }
        }

        val converted = (0 until 4).map { seat ->
            if (seat == humanIndex) {
                SeatController.LocalHuman()
            } else {
                val strategy = aiStrategies[seat]?.strategy ?: BasicAIStrategy()
                SeatController.Computer(strategy)
            }
        }
        initializeMatch(converted)
    }

    fun setSeatController(seat: Int, controller: SeatController): ActionResult {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!rwLock.writeLock().tryLock()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        currentExecutingThread.set(Thread.currentThread())

        val allEvents = mutableListOf<GameEvent>()
        var acceptedResultToReturn: ActionResult? = null
        try {
            if (session.lifecycle == SessionLifecycle.FINISHED) {
                return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
            }
            if (seat !in 0..3) {
                return ActionResult.Rejected(RejectionReason.INVALID_PLAYER)
            }

            syncFromLegacyMirror()
            controllerRegistry.setController(seat, controller)
            decisionRegistry.invalidate(seat)

            if (!deferAutomaticActions) {
                runAutomaticActionLoop(allEvents)
            }
            syncToLegacyMirror()
            val acceptedResult = ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            acceptedResultToReturn = acceptedResult
            return acceptedResult
        } finally {
            currentExecutingThread.set(null)
            if (acceptedResultToReturn != null) {
                rwLock.readLock().lock()
                rwLock.writeLock().unlock()
                try {
                    broadcastEvents(allEvents)
                } finally {
                    isProcessingAction.set(false)
                    rwLock.readLock().unlock()
                }
            } else {
                isProcessingAction.set(false)
                rwLock.writeLock().unlock()
            }
        }
    }

    // ========================================
    // 视图与规则查询（加一致性读锁）
    // ========================================

    fun queryView(seat: Int): PlayerView {
        syncLegacyStateBeforeQuery()
        rwLock.readLock().withLock {
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            val round = activeRound
            val phase = if (session.lifecycle == SessionLifecycle.FINISHED) GamePhase.MATCH_END else GamePhase.ROUND_END
            val legals = legalActions(seat)

            if (round == null) {
                val snapshot = lastEndedRoundSnapshot
                if (snapshot != null) {
                    val baseView = PlayerView.fromGameState(snapshot, seat, legals)
                    return baseView.copy(
                        phase = phase,
                        scores = session.betweenRoundScores,
                        riichiSticks = if (session.lifecycle == SessionLifecycle.FINISHED) 0 else session.betweenRoundRiichiSticks,
                        legalActions = legals
                    )
                }
                return PlayerView.create(
                    viewerSeat = seat,
                    phase = phase,
                    roundWind = session.currentRoundWind,
                    roundNumber = session.currentRoundNumber,
                    honbaSticks = session.currentHonba,
                    riichiSticks = if (session.lifecycle == SessionLifecycle.FINISHED) 0 else session.betweenRoundRiichiSticks,
                    dealerSeat = session.currentDealer,
                    currentTurnPlayer = session.currentDealer,
                    wallRemaining = 0,
                    doraIndicators = emptyList(),
                    scores = session.betweenRoundScores,
                    riichiStatus = listOf(false, false, false, false),
                    discards = List(4) { emptyList() },
                    melds = List(4) { emptyList() },
                    handTileCounts = List(4) { 0 },
                    myClosedTiles = emptyList(),
                    myDrawnTile = null,
                    legalActions = legals
                )
            }

            val stateCopy = round.readState()
            val baseView = PlayerView.fromGameState(stateCopy, seat, legals)
            return if (session.lifecycle == SessionLifecycle.FINISHED) {
                baseView.copy(
                    phase = GamePhase.MATCH_END,
                    scores = session.betweenRoundScores,
                    riichiSticks = 0,
                    legalActions = legals
                )
            } else {
                baseView
            }
        }
    }

    fun legalActions(seat: Int): List<GameAction> {
        syncLegacyStateBeforeQuery()
        rwLock.readLock().withLock {
            if (seat !in 0..3) return emptyList()
            if (session.lifecycle == SessionLifecycle.FINISHED) return emptyList()

            val actions = mutableListOf<GameAction>()

            if (session.canDealerDeclareAgariYame && seat == session.currentDealer) {
                actions.add(GameAction.DeclareAgariYame(seat))
            }

            val round = activeRound
            if (round != null && round.state.phase != GamePhase.ROUND_END) {
                val roundLegals = roundCommandProcessor.legalActions(round, seat)
                actions.addAll(roundLegals)
            }

            return actions.map { it.deepCopy() }
        }
    }

    // ========================================
    // 决策与选择 (PlayerDecision / ActionChoice)
    // ========================================

    fun queryDecision(seat: Int): PlayerDecision {
        syncLegacyStateBeforeQuery()
        rwLock.readLock().withLock {
            require(seat in 0..3) { "seat must be in 0..3, got $seat" }
            val epoch = controllerRegistry.getEpoch(seat)

            if (session.lifecycle == SessionLifecycle.FINISHED) {
                return PlayerDecision(
                    roundId = null,
                    decisionId = "finished-$seat",
                    controlEpoch = epoch,
                    options = emptyList()
                )
            }

            if (session.lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE && seat == session.currentDealer) {
                val decisionKey = "session-dealer-choice-${session.sessionId}-${session.version}-$seat"
                return decisionRegistry.getOrRegister(seat, null, decisionKey, epoch) {
                    listOf(
                        ActionOption("opt-agari-yame", GameAction.DeclareAgariYame(seat).deepCopy()),
                        ActionOption("opt-continue", GameAction.StartStacking().deepCopy())
                    )
                }
            }

            val round = activeRound
            if (round == null || round.state.phase == GamePhase.ROUND_END) {
                return PlayerDecision(
                    roundId = round?.roundId,
                    decisionId = "between-rounds-$seat",
                    controlEpoch = epoch,
                    options = emptyList()
                )
            }

            val window = round.responseWindow
            if (window != null && window.eligibleSeats.contains(seat) && !window.hasResponded(seat)) {
                val decisionKey = "window-${round.roundId}-${window.id}-$seat"
                return decisionRegistry.getOrRegister(seat, round.roundId, decisionKey, epoch) {
                    val legal = window.eligibleActions[seat] ?: emptyList()
                    legal.mapIndexed { index, act ->
                        ActionOption("opt-$index", act.deepCopy())
                    }
                }
            }

            if (round.state.phase in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD, GamePhase.DRAW)) {
                if (round.state.currentPlayer == seat) {
                    val decisionKey = "turn-${round.roundId}-${round.version}-$seat"
                    return decisionRegistry.getOrRegister(seat, round.roundId, decisionKey, epoch) {
                        val legal = legalActions(seat)
                        legal.mapIndexed { index, act ->
                            ActionOption("opt-$index", act.deepCopy())
                        }
                    }
                }
            }

            return PlayerDecision(
                roundId = round.roundId,
                decisionId = "no-action-$seat",
                controlEpoch = epoch,
                options = emptyList()
            )
        }
    }

    fun submitChoice(seat: Int, choice: ActionChoice): ActionResult {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!rwLock.writeLock().tryLock()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        currentExecutingThread.set(Thread.currentThread())

        val allEvents = mutableListOf<GameEvent>()
        var acceptedResultToReturn: ActionResult? = null
        try {
            if (seat !in 0..3) {
                return ActionResult.Rejected(RejectionReason.INVALID_PLAYER)
            }

            // 1. 请求幂等与回放校验（按座位去重，相同返回原回执，不同载荷冲突拒绝）
            val receipt = requestRegistry.getReceipt(seat, choice.requestId)
            if (receipt != null) {
                return if (receipt.choicePayload == choice) {
                    receipt.result
                } else {
                    ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                }
            }

            // 2. 控制代次时效校验
            if (choice.controlEpoch != controllerRegistry.getEpoch(seat)) {
                return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
            }

            // 3. 控制者身份必须为真人
            if (!controllerRegistry.isHuman(seat)) {
                return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
            }

            // 4. 决策缓存与严格 roundId 匹配
            val registeredDecision = decisionRegistry.get(seat)
            if (registeredDecision == null || registeredDecision.decisionId != choice.decisionId) {
                return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
            }

            // 严格作用域匹配：不能提交 roundId=null 伪装局内选项，必须与注册决策完全一致
            if (choice.roundId != registeredDecision.roundId) {
                return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
            }

            val selectedOption = registeredDecision.options.firstOrNull { it.optionId == choice.optionId }
                ?: return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)

            // 5. 同一锁内重新核对当前资格有效性
            if (registeredDecision.roundId != null) {
                val round = activeRound
                if (round == null || round.roundId != registeredDecision.roundId) {
                    return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
                }
                if (round.state.phase == GamePhase.ROUND_END) {
                    return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
                }
                if (registeredDecision.decisionId.startsWith("window-")) {
                    val window = round.responseWindow
                    if (window == null || "window-${round.roundId}-${window.id}-$seat" != registeredDecision.decisionId) {
                        return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                    }
                    if (!window.eligibleSeats.contains(seat) || window.hasResponded(seat)) {
                        return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                    }
                } else if (registeredDecision.decisionId.startsWith("turn-")) {
                    if (round.state.currentPlayer != seat || round.state.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD, GamePhase.DRAW)) {
                        return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                    }
                    val expectedTurnKey = "turn-${round.roundId}-${round.version}-$seat"
                    if (registeredDecision.decisionId != expectedTurnKey) {
                        return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                    }
                }
            } else {
                if (session.lifecycle != SessionLifecycle.WAITING_DEALER_CHOICE || seat != session.currentDealer) {
                    return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                }
                val expectedSessionKey = "session-dealer-choice-${session.sessionId}-${session.version}-$seat"
                if (registeredDecision.decisionId != expectedSessionKey) {
                    return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
                }
            }

            syncFromLegacyMirror()

            // 注意：非法请求绝不清除合法决策缓存！
            val actionToExecute = selectedOption.action.deepCopy()
            val execResult = executeActionInternal(actionToExecute, allEvents)
            if (execResult !is ActionResult.Accepted) {
                val rejectedResult = execResult
                requestRegistry.registerReceipt(seat, choice, rejectedResult)
                return rejectedResult
            }

            // 仅在执行成功后消费当前座位的决策，保留同窗口其他座位的合法决策
            decisionRegistry.consume(seat)

            if (!deferAutomaticActions) {
                runAutomaticActionLoop(allEvents)
            }
            val acceptedResult = ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            requestRegistry.registerReceipt(seat, choice, acceptedResult)
            acceptedResultToReturn = acceptedResult
            return acceptedResult
        } finally {
            currentExecutingThread.set(null)
            if (acceptedResultToReturn != null) {
                // 提交完全成功后，降级为读锁发布事件，允许并发查询已提交状态
                rwLock.readLock().lock()
                rwLock.writeLock().unlock()
                try {
                    broadcastEvents(allEvents)
                } finally {
                    isProcessingAction.set(false)
                    rwLock.readLock().unlock()
                }
            } else {
                isProcessingAction.set(false)
                rwLock.writeLock().unlock()
            }
        }
    }

    // ========================================
    // 动作执行入口
    // ========================================

    fun submitPlayerAction(seat: Int, action: GameAction): ActionResult {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!rwLock.writeLock().tryLock()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        currentExecutingThread.set(Thread.currentThread())

        val allEvents = mutableListOf<GameEvent>()
        var acceptedResultToReturn: ActionResult? = null
        try {
            if (session.lifecycle == SessionLifecycle.FINISHED) {
                return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
            }
            if (seat !in 0..3) {
                return ActionResult.Rejected(RejectionReason.INVALID_PLAYER)
            }
            if (action is GameAction.StartStacking || action is GameAction.DealComplete) {
                return ActionResult.Rejected(RejectionReason.UNSUPPORTED_ACTION)
            }
            val actionSeat = getActionPlayer(action)
            if (actionSeat != seat) {
                return ActionResult.Rejected(RejectionReason.INVALID_PLAYER)
            }
            if (!controllerRegistry.isHuman(seat)) {
                return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
            }

            syncFromLegacyMirror()

            val execResult = executeActionInternal(action.deepCopy(), allEvents)
            if (execResult !is ActionResult.Accepted) {
                return execResult
            }

            decisionRegistry.consume(seat)
            if (!deferAutomaticActions) {
                runAutomaticActionLoop(allEvents)
            }
            val acceptedResult = ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            acceptedResultToReturn = acceptedResult
            return acceptedResult
        } finally {
            currentExecutingThread.set(null)
            if (acceptedResultToReturn != null) {
                rwLock.readLock().lock()
                rwLock.writeLock().unlock()
                try {
                    broadcastEvents(allEvents)
                } finally {
                    isProcessingAction.set(false)
                    rwLock.readLock().unlock()
                }
            } else {
                isProcessingAction.set(false)
                rwLock.writeLock().unlock()
            }
        }
    }

    fun handleAction(action: GameAction): ActionResult {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!rwLock.writeLock().tryLock()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        currentExecutingThread.set(Thread.currentThread())

        val allEvents = mutableListOf<GameEvent>()
        var acceptedResultToReturn: ActionResult? = null
        try {
            syncFromLegacyMirror()

            val player = getActionPlayer(action)
            val execResult = executeActionInternal(action.deepCopy(), allEvents)
            if (execResult !is ActionResult.Accepted) {
                return execResult
            }

            if (player in 0..3) {
                decisionRegistry.consume(player)
            }
            if (!deferAutomaticActions) {
                runAutomaticActionLoop(allEvents)
            }
            val acceptedResult = ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            acceptedResultToReturn = acceptedResult
            return acceptedResult
        } finally {
            currentExecutingThread.set(null)
            if (acceptedResultToReturn != null) {
                rwLock.readLock().lock()
                rwLock.writeLock().unlock()
                try {
                    broadcastEvents(allEvents)
                } finally {
                    isProcessingAction.set(false)
                    rwLock.readLock().unlock()
                }
            } else {
                isProcessingAction.set(false)
                rwLock.writeLock().unlock()
            }
        }
    }

    fun start(): ActionResult {
        return handleAction(GameAction.StartStacking())
    }

    fun nextRound(): ActionResult {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!rwLock.writeLock().tryLock()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        currentExecutingThread.set(Thread.currentThread())

        val allEvents = mutableListOf<GameEvent>()
        var acceptedResultToReturn: ActionResult? = null
        try {
            if (session.lifecycle == SessionLifecycle.FINISHED) {
                return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
            }
            if (activeRound != null && activeRound?.state?.phase != GamePhase.ROUND_END) {
                return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
            }

            syncFromLegacyMirror()

            val execResult = executeNextRoundInternal(allEvents)
            if (execResult !is ActionResult.Accepted) {
                return execResult
            }

            decisionRegistry.invalidateAll()
            if (!deferAutomaticActions) {
                runAutomaticActionLoop(allEvents)
            }
            val acceptedResult = ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            acceptedResultToReturn = acceptedResult
            return acceptedResult
        } finally {
            currentExecutingThread.set(null)
            if (acceptedResultToReturn != null) {
                rwLock.readLock().lock()
                rwLock.writeLock().unlock()
                try {
                    broadcastEvents(allEvents)
                } finally {
                    isProcessingAction.set(false)
                    rwLock.readLock().unlock()
                }
            } else {
                isProcessingAction.set(false)
                rwLock.writeLock().unlock()
            }
        }
    }

    fun advanceAutomaticActions(): ActionResult {
        if (currentExecutingThread.get() == Thread.currentThread()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!rwLock.writeLock().tryLock()) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        if (!isProcessingAction.compareAndSet(false, true)) {
            rwLock.writeLock().unlock()
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }
        currentExecutingThread.set(Thread.currentThread())

        val allEvents = mutableListOf<GameEvent>()
        var acceptedResultToReturn: ActionResult? = null
        try {
            if (session.lifecycle == SessionLifecycle.FINISHED) {
                return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
            }
            syncFromLegacyMirror()
            if (deferAutomaticActions) {
                stepAutomaticAction(allEvents)
            } else {
                runAutomaticActionLoop(allEvents)
            }
            syncToLegacyMirror()
            val acceptedResult = ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            acceptedResultToReturn = acceptedResult
            return acceptedResult
        } finally {
            currentExecutingThread.set(null)
            if (acceptedResultToReturn != null) {
                rwLock.readLock().lock()
                rwLock.writeLock().unlock()
                try {
                    broadcastEvents(allEvents)
                } finally {
                    isProcessingAction.set(false)
                    rwLock.readLock().unlock()
                }
            } else {
                isProcessingAction.set(false)
                rwLock.writeLock().unlock()
            }
        }
    }

    // ========================================
    // 共享广播与订阅（发布隔离）
    // ========================================

    fun subscribe(listener: (List<GameEvent>) -> Unit): AutoCloseable {
        subscribers.add(listener)
        return AutoCloseable {
            subscribers.remove(listener)
        }
    }

    private fun broadcastEvents(events: List<GameEvent>) {
        val list = if (events.isEmpty()) emptyList() else Collections.unmodifiableList(events.map { snapshotGameEvent(it) })
        for (listener in subscribers) {
            try {
                listener(list)
            } catch (t: Throwable) {
                // 监听器异常隔离，不影响其他端点或协调器
            }
        }
    }

    // ========================================
    // 内部动作执行与单局/整场交接
    // ========================================

    private fun executeActionInternal(action: GameAction, allEvents: MutableList<GameEvent>): ActionResult {
        if (session.lifecycle == SessionLifecycle.FINISHED) {
            return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
        }

        // 1. Session 范围动作：DeclareAgariYame
        if (action is GameAction.DeclareAgariYame) {
            if (activeRound != null && activeRound?.state?.phase != GamePhase.ROUND_END) {
                return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
            }
            if (action.player != session.currentDealer || !session.canDealerDeclareAgariYame) {
                return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
            }
            SessionCommandValidator.validateChooseDealerEnd(session, action.player)

            val progResult = matchProgression.nextRound(ProgressionInput(
                currentDealer = session.currentDealer,
                currentRoundWind = session.currentRoundWind,
                currentRoundNumber = session.currentRoundNumber,
                currentHonba = session.currentHonba,
                scores = session.betweenRoundScores,
                outcome = RoundOutcome.DealerWin(session.currentDealer),
                remainingRiichiSticks = session.betweenRoundRiichiSticks,
                dealerDeclaredAgariYame = true
            ))

            if (progResult is ProgressionResult.MatchFinished) {
                val plan = ProgressionPlan(
                    kind = ProgressionPlanKind.FINISH_SESSION,
                    matchFinished = progResult
                )
                val sessionStep = ApprovedSessionStep.ChooseDealerEnd(agariYame = true, progressionPlan = plan)
                val sessionChange = session.prepare(sessionStep)
                session.install(sessionChange)

                decisionRegistry.invalidateAll()
                syncToLegacyMirror()

                for (e in sessionChange.events) {
                    val snap = snapshotGameEvent(e)
                    allEvents.add(snap)
                    eventBus.publish(snap)
                }
                return ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
            }
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }

        // DealComplete 始终不作为玩家可调用动作
        if (action is GameAction.DealComplete) {
            return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        }

        // 2. 外部兼容调用 StartStacking
        if (action is GameAction.StartStacking) {
            if (activeRound == null || activeRound?.state?.phase == GamePhase.ROUND_END) {
                return executeNextRoundInternal(allEvents)
            }
            if (activeRound?.state?.phase != GamePhase.STACKING) {
                return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
            }
            if (action.stackingPlans.isNotEmpty()) {
                return ActionResult.Rejected(RejectionReason.UNSUPPORTED_ACTION)
            }
        }

        // 3. 局内动作执行
        val round = activeRound ?: return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
        if (round.state.phase == GamePhase.ROUND_END) {
            return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
        }

        val prep = roundCommandProcessor.prepare(round, action)
        if (prep is RoundPreparation.Rejected) {
            return ActionResult.Rejected(prep.reason)
        }
        val change = (prep as RoundPreparation.Ready).change

        // 外部审计：牌张物理守恒与状态不变量
        StateInvariants.assertValid(change.state)

        // 检查本局是否结束并准备整场结果交接
        var sessionChange: SessionChange? = null
        val roundRes = change.result
        if (roundRes != null) {
            RoundResultValidator.validate(session, round, change, consumedResults)

            val progResult = matchProgression.nextRound(ProgressionInput(
                currentDealer = change.state.dealerIndex,
                currentRoundWind = change.state.roundWind,
                currentRoundNumber = change.state.roundNumber,
                currentHonba = change.state.honbaSticks,
                scores = roundRes.finalScores,
                outcome = roundRes.outcome,
                remainingRiichiSticks = roundRes.remainingRiichiSticks,
                dealerDeclaredAgariYame = false
            ))

            val progressionPlan = when (progResult) {
                is ProgressionResult.MatchFinished -> ProgressionPlan(
                    kind = ProgressionPlanKind.FINISH_SESSION,
                    matchFinished = progResult
                )
                is ProgressionResult.NextRound -> {
                    if (progResult.canDealerDeclareAgariYame) {
                        ProgressionPlan(
                            kind = ProgressionPlanKind.WAIT_DEALER_CHOICE,
                            nextRound = progResult,
                            canDealerDeclareAgariYame = true
                        )
                    } else {
                        ProgressionPlan(
                            kind = ProgressionPlanKind.PREPARE_NEXT_ROUND,
                            nextRound = progResult
                        )
                    }
                }
            }

            SessionCommandValidator.validateAcceptRoundResult(session, round, roundRes, progressionPlan)
            val sessionStep = ApprovedSessionStep.AcceptRoundResult(roundRes, progressionPlan)
            sessionChange = session.prepare(sessionStep)
        }

        // 原子安装本局变更及整场交接
        round.install(change)
        if (sessionChange != null && roundRes != null) {
            session.install(sessionChange)
            consumedResults.add(roundRes.resultId)
            lastEndedRoundSnapshot = change.state.deepCopy()
            decisionRegistry.invalidateAll()
        }
        syncToLegacyMirror()

        // 收集已提交事件并发布
        for (e in change.events) {
            val snap = snapshotGameEvent(e)
            allEvents.add(snap)
            eventBus.publish(snap)
        }
        if (sessionChange != null) {
            for (e in sessionChange.events) {
                val snap = snapshotGameEvent(e)
                allEvents.add(snap)
                eventBus.publish(snap)
            }
        }

        return ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
    }

    private fun executeNextRoundInternal(allEvents: MutableList<GameEvent>): ActionResult {
        if (session.lifecycle == SessionLifecycle.FINISHED) {
            return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
        }
        if (activeRound != null && activeRound?.state?.phase != GamePhase.ROUND_END && activeRound?.state?.phase != GamePhase.STACKING) {
            return ActionResult.Rejected(RejectionReason.WRONG_PHASE)
        }

        SessionCommandValidator.validateStartNextRound(session, activeRound)

        // 1. 确定新局初始参数（若处于 WAITING_DEALER_CHOICE，相当于庄家选择继续对局，从 pendingProgressionPlan 取）
        val nextDealer: Int
        val nextRoundWind: Int
        val nextRoundNumber: Int
        val nextHonba: Int
        if (session.lifecycle == SessionLifecycle.WAITING_DEALER_CHOICE) {
            val planNext = session.pendingProgressionPlan?.nextRound
            nextDealer = planNext?.dealer ?: session.currentDealer
            nextRoundWind = planNext?.roundWind ?: session.currentRoundWind
            nextRoundNumber = planNext?.roundNumber ?: session.currentRoundNumber
            nextHonba = planNext?.honba ?: session.currentHonba
        } else {
            nextDealer = session.currentDealer
            nextRoundWind = session.currentRoundWind
            nextRoundNumber = session.currentRoundNumber
            nextHonba = session.currentHonba
        }

        // 2. 创建新局状态与 RoundStateMachine
        val newRoundState = GameState()
        newRoundState.roundWind = nextRoundWind
        newRoundState.roundNumber = nextRoundNumber
        newRoundState.dealerIndex = nextDealer
        newRoundState.honbaSticks = nextHonba
        newRoundState.riichiSticks = session.betweenRoundRiichiSticks
        for (i in 0 until 4) newRoundState.scores[i] = session.betweenRoundScores[i]
        newRoundState.currentPlayer = nextDealer
        newRoundState.phase = GamePhase.STACKING

        val nextRound = RoundStateMachine(newRoundState)

        // 3. 准备 Session 变更（绝不先写 Session）
        val sessionStep = ApprovedSessionStep.StartNextRound(nextRound)
        val sessionChange = session.prepare(sessionStep)

        // 4. 准备单局发牌候选
        val prep = roundCommandProcessor.prepare(nextRound, GameAction.StartStacking())
        if (prep is RoundPreparation.Rejected) {
            return ActionResult.Rejected(prep.reason)
        }
        val roundChange = (prep as RoundPreparation.Ready).change

        // 5. 外部审计：牌张物理守恒与状态不变量
        StateInvariants.assertValid(roundChange.state)

        // 6. 所有校验审计通过后，原子安装 Session 与 Round
        session.install(sessionChange)
        nextRound.install(roundChange)
        syncToLegacyMirror()

        // 7. 收集并记录事件
        for (e in sessionChange.events) {
            val snap = snapshotGameEvent(e)
            allEvents.add(snap)
            eventBus.publish(snap)
        }
        for (e in roundChange.events) {
            val snap = snapshotGameEvent(e)
            allEvents.add(snap)
            eventBus.publish(snap)
        }

        decisionRegistry.invalidateAll()
        return ActionResult.Accepted(allEvents.map { snapshotGameEvent(it) })
    }

    // ========================================
    // 顺序自动动作调度循环 (Unified Sequential Loop)
    // ========================================

    private fun runAutomaticActionLoop(allEvents: MutableList<GameEvent>) {
        while (stepAutomaticAction(allEvents)) {
            // 持续单步推进直到无法自动执行
        }
    }

    private fun stepAutomaticAction(allEvents: MutableList<GameEvent>): Boolean {
        if (session.lifecycle == SessionLifecycle.FINISHED) return false
        val round = activeRound ?: return false
        if (round.state.phase == GamePhase.ROUND_END) return false

        // 1. 响应窗口：选择尚未响应的 AI/托管座位，用策略（或 Pass 兜底）应答
        val window = round.responseWindow
        if (window != null) {
            if (!autoPlayAi) return false

            val nextAiSeat = window.eligibleSeats.firstOrNull { seat ->
                !window.hasResponded(seat) && controllerRegistry.isAutomated(seat)
            }

            if (nextAiSeat != null) {
                val aiAction = decideAiResponseAction(nextAiSeat, window)
                if (aiAction != null) {
                    val result = executeActionInternal(aiAction, allEvents)
                    return result is ActionResult.Accepted
                } else {
                    return false
                }
            } else {
                // 所有有资格的 AI 座位均已响应，等待真人输入
                return false
            }
        }

        // 2. 自家阶段（CHEAT_WINDOW 或 DISCARD）
        if (round.state.phase in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD)) {
            val currPlayer = round.state.currentPlayer
            val controller = controllerFor(currPlayer)

            if (controllerRegistry.isHuman(currPlayer)) {
                val seatPref = when (controller) {
                    is SeatController.LocalHuman -> controller.autoRiichiDiscard
                    is SeatController.RemoteHuman -> controller.autoRiichiDiscard
                    else -> false
                }
                if (autoRiichiDiscard && seatPref && round.state.riichiDeclared[currPlayer] && round.state.players[currPlayer].drawnTile != null) {
                    val actions = legalActions(currPlayer)
                    val hasTsumo = actions.any { it is GameAction.DeclareTsumo }
                    val hasKan = actions.any { it is GameAction.DeclareKan }

                    if (hasTsumo || hasKan) {
                        // 存在合法自摸或暗杠，暂停等待真人选择
                        return false
                    }

                    val drawnTile = round.state.players[currPlayer].drawnTile!!
                    val discardAction = GameAction.DiscardTile(currPlayer, drawnTile)
                    val result = executeActionInternal(discardAction, allEvents)
                    if (result is ActionResult.Accepted) {
                        decisionRegistry.consume(currPlayer)
                        return true
                    }
                    return false
                } else {
                    // 等待真人出牌
                    return false
                }
            } else if (controllerRegistry.isAutomated(currPlayer)) {
                if (!autoPlayAi) return false
                if (!controllerRegistry.hasExplicitConfiguration) {
                    // 未显式配置时保留无头 fixture 语义
                    return false
                }

                val aiAction = decideAiTurnAction(currPlayer, controller)
                if (aiAction != null) {
                    val result = executeActionInternal(aiAction, allEvents)
                    return result is ActionResult.Accepted
                } else {
                    return false
                }
            } else {
                return false
            }
        } else {
            return false
        }
    }

    private fun decideAiResponseAction(seat: Int, window: ResponseWindow): GameAction? {
        if (!window.eligibleSeats.contains(seat) || window.hasResponded(seat)) return null

        val controller = controllerFor(seat)
        val strategy = when (controller) {
            is SeatController.Computer -> controller.strategy
            is SeatController.Takeover -> controller.strategy
            else -> null
        }

        val eligible = window.eligibleActions[seat] ?: emptyList()
        if (eligible.isEmpty()) return null

        var chosen: GameAction? = null
        if (strategy != null) {
            try {
                val view = queryView(seat)
                chosen = strategy.decideAction(view, view.legalActions)
            } catch (e: Exception) {
                chosen = null
            }
        }

        if (chosen != null && isValidAiResponseAction(seat, chosen, window)) {
            return chosen
        }

        // 合法响应窗口使用 Pass 兜底
        val pass = GameAction.Pass(seat)
        if (isValidAiResponseAction(seat, pass, window)) {
            return pass
        }
        return null
    }

    private fun isValidAiResponseAction(seat: Int, action: GameAction, window: ResponseWindow): Boolean {
        if (getActionPlayer(action) != seat) return false
        return window.findMatchingEligibleAction(seat, action) != null
    }

    private fun decideAiTurnAction(player: Int, controller: SeatController): GameAction? {
        val round = activeRound ?: return null
        if (round.state.currentPlayer != player) return null
        if (round.state.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD)) return null

        val strategy = when (controller) {
            is SeatController.Computer -> controller.strategy
            is SeatController.Takeover -> controller.strategy
            else -> null
        }

        val legal = legalActions(player)
        if (legal.isEmpty()) return null

        var chosen: GameAction? = null
        if (strategy != null) {
            try {
                val view = queryView(player)
                chosen = strategy.decideAction(view, view.legalActions)
            } catch (e: Exception) {
                chosen = null
            }
        }

        if (chosen != null && isValidAiTurnAction(player, chosen)) {
            return chosen
        }

        // 兜底方案：自家回合优先摸切，否则首个合法弃牌
        val drawnTile = round.state.players[player].drawnTile
        if (drawnTile != null) {
            val tsumogiri = GameAction.DiscardTile(player, drawnTile)
            if (isValidAiTurnAction(player, tsumogiri)) {
                return tsumogiri
            }
        }

        val firstDiscard = legal.firstOrNull { it is GameAction.DiscardTile && isValidAiTurnAction(player, it) }
        if (firstDiscard != null) {
            return firstDiscard
        }
        return null
    }

    private fun isValidAiTurnAction(player: Int, action: GameAction): Boolean {
        if (getActionPlayer(action) != player) return false
        val round = activeRound ?: return false
        val prep = roundCommandProcessor.prepare(round, action)
        return prep is RoundPreparation.Ready
    }

    private fun getActionPlayer(action: GameAction): Int = when (action) {
        is GameAction.DiscardTile -> action.player
        is GameAction.DrawTile -> action.player
        is GameAction.DeclareChi -> action.player
        is GameAction.DeclarePon -> action.player
        is GameAction.DeclareKan -> action.player
        is GameAction.DeclareRiichi -> action.player
        is GameAction.DeclareTsumo -> action.player
        is GameAction.DeclareRon -> action.player
        is GameAction.Pass -> action.player
        is GameAction.DeclareAbortiveDraw -> action.player
        is GameAction.DeclareAgariYame -> action.player
        is GameAction.UseCheatTechnique -> action.player
        else -> -1
    }
}

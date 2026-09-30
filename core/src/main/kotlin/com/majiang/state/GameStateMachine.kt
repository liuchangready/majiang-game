package com.majiang.state

import com.majiang.ai.AIPlayer
import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEventBus
import com.majiang.match.MatchProgression
import com.majiang.rules.RiichiRuleEngine
import com.majiang.rules.WinningScoreEvaluator
import com.majiang.settlement.SettlementService
import kotlin.random.Random

/**
 * 核心游戏状态机兼容适配器 (Architecture V2)
 *
 * 将调用全面转发给 [ApplicationCoordinator]，自身作为轻量代理，不保留原 1400 行麻将规则与算分算法。
 * 历史实现留存在 build/round-session-split/GameStateMachine.before.kt。
 *
 * 显式同步职责：
 * 1. 注册自身 [state] 为协调器的实时只读兼容镜像目标；
 * 2. 协调器在每次已提交 Round / Session 步骤后、事件发布前与 AI 策略读取前实时同步至 [state]；
 * 3. 在非执行期间，支持旧单测 fixture 显式修改 [state] 后无缝同步回当前活动的权威 Round；
 * 4. 严格隔离：绝不重新让镜像等于 Round.state，同步镜像不改变 Round 终态，命令入口统一由协调器非阻塞保护。
 */
class GameStateMachine(
    val state: GameState,
    val eventBus: GameEventBus,
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
    val settlementService: SettlementService = SettlementService.INSTANCE,
    val matchProgression: MatchProgression = MatchProgression(),
    val autoPlayAi: Boolean = true,
    val random: Random = Random.Default,
    val autoRiichiDiscard: Boolean = true
) {
    val coordinator: ApplicationCoordinator = ApplicationCoordinator(
        initialState = state,
        eventBus = eventBus,
        autoPlayAi = autoPlayAi,
        autoRiichiDiscard = autoRiichiDiscard,
        random = random,
        matchProgression = matchProgression,
        ruleEngine = ruleEngine,
        winningScoreEvaluator = winningScoreEvaluator,
        settlementService = settlementService
    )

    init {
        coordinator.attachLegacyMirror(state)
    }

    /** 是否已显式调用任一 initializeMatch 或成功 setSeatController。只读代理。 */
    val hasControllerConfiguration: Boolean
        get() = coordinator.hasControllerConfiguration

    /** 旧 humanPlayerIndex 保留只读兼容查询（恰一名 LocalHuman/RemoteHuman 时为其座位，否则 -1） */
    val humanPlayerIndex: Int
        get() = coordinator.humanPlayerIndex

    /** 当前活跃的响应窗口（若处于 RESPONSE_WINDOW 或 CHANKAN_WINDOW） */
    val currentResponseWindow: ResponseWindow?
        get() = coordinator.activeRound?.responseWindow

    // ========================================
    // 控制者查询与配置
    // ========================================

    fun controllerFor(seat: Int): SeatController = coordinator.controllerFor(seat)

    fun initializeMatch(controllers: List<SeatController>) {
        coordinator.initializeMatch(controllers)
    }

    fun initializeMatch(humanIndex: Int = 0, aiStrategies: Map<Int, AIPlayer> = emptyMap()) {
        coordinator.initializeMatch(humanIndex, aiStrategies)
    }

    fun legalActions(player: Int): List<GameAction> {
        coordinator.syncLegacyStateBeforeQuery()
        return coordinator.legalActions(player)
    }

    // ========================================
    // 公开命令入口
    // ========================================

    fun submitPlayerAction(seat: Int, action: GameAction): ActionResult {
        return coordinator.submitPlayerAction(seat, action)
    }

    fun setSeatController(seat: Int, controller: SeatController): ActionResult {
        return coordinator.setSeatController(seat, controller)
    }

    fun advanceAutomaticActions(): ActionResult {
        return coordinator.advanceAutomaticActions()
    }

    fun handleAction(action: GameAction): ActionResult {
        return coordinator.handleAction(action)
    }

    fun startNewRound(): ActionResult {
        return coordinator.start()
    }

    fun nextRound(): ActionResult {
        return coordinator.nextRound()
    }
}

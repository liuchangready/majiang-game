package com.majiang.round

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.stacking.ShuffleSelection
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.ResponseWindow
import java.util.UUID

/**
 * 洗牌千术准备阶段单局不可变记录 (RoundPreparationRecord)
 *
 * 归属于 RoundStateMachine 的防御记录，跟踪各席位方案、确认状态与资金预留。
 *
 * @property seeThroughSeats 准备截止时冻结的本局看破席位集合（K），结算后不可更改
 */
data class RoundPreparationRecord(
    val roundId: String,
    val seed: Long,
    val selections: List<ShuffleSelection?> = List(4) { null },
    val confirmed: List<Boolean> = List(4) { false },
    val reserved: List<Int> = List(4) { 0 },
    val seeThroughSeats: Set<Int> = emptySet()
) {
    fun deepCopy(): RoundPreparationRecord = copy(
        selections = selections.map { it?.deepCopy() },
        confirmed = confirmed.toList(),
        reserved = reserved.toList(),
        seeThroughSeats = java.util.Collections.unmodifiableSet(java.util.LinkedHashSet(seeThroughSeats))
    )
}

/**
 * 单局状态机 (RoundStateMachine)
 *
 * 遵循 DESIGN.md 4.1 与分类开发设计契约及 R2 边界规范：
 * - 只保存并应用本局状态转移，终态固定为 ROUND_END；
 * - 每次发牌使用新实例、新 roundId；
 * - 不包含权限、合法动作生成、守恒审计、AI 或 MatchProgression 判断；
 * - 不持有 planner 或 rule 服务，规则计算留在外部独立服务；
 * - 内部 prepare/install 只对已批准变更工作，隔离候选计算与权威状态；
 * - 拒绝或外部审计失败时权威状态不留任何痕迹；
 * - readState() 深复制；responseWindow、result 与 accumulatedEvents 均提供严格防御复制。
 */
class RoundStateMachine(
    initialState: GameState,
    val roundId: String = UUID.randomUUID().toString()
) {
    /** 初始局可以保留传入引用以兼容旧 fixture；客户端永不可访问 */
    internal val state: GameState = initialState

    /** 状态机版本号，从 0 开始，每次 install 成功自增 1 */
    var version: Long = 0L
        private set

    /** 本局起始积分记录（用于结算 delta 计算） */
    internal var startingScores: List<Int> = initialState.scores.toList()

    /** 本局起始场上立直棒数 */
    internal var startingRiichiSticks: Int = initialState.riichiSticks

    /** 本局起始出千点数记录（冻结基线，用于结算 delta 计算） */
    internal var startingCheatPoints: List<Int> = initialState.cheatPoints.toList()

    /** 冻结的起始出千点数不可变列表 */
    val frozenStartingCheatPoints: List<Int>
        get() = java.util.Collections.unmodifiableList(startingCheatPoints.toList())

    /** 当前活跃的响应窗口（若处于 RESPONSE_WINDOW 或 CHANKAN_WINDOW） */
    private var _responseWindow: ResponseWindow? = null

    /** 活跃响应窗口（防御复制） */
    val responseWindow: ResponseWindow?
        get() = _responseWindow?.deepCopy()

    /** 本局终局结算结果（防御复制） */
    private var _result: RoundResult? = null

    /** 终局结果（防御复制） */
    val result: RoundResult?
        get() = _result?.deepCopy()

    /** 本局洗牌千术准备阶段记录（防御复制） */
    private var _preparationRecord: RoundPreparationRecord? = null

    /** 洗牌千术准备阶段记录（防御复制） */
    val preparationRecord: RoundPreparationRecord?
        get() = _preparationRecord?.deepCopy()

    /**
     * 更新本局洗牌千术准备阶段记录（严格校验归属局号并深拷贝隔离）
     */
    fun updatePreparationRecord(record: RoundPreparationRecord) {
        check(record.roundId == roundId) {
            "Cannot set preparation record for round ${record.roundId} into round $roundId"
        }
        this._preparationRecord = record.deepCopy()
    }

    /** 本局自开始以来已提交的全部事件历史 */
    private val _accumulatedEvents: MutableList<GameEvent> = mutableListOf()

    /** 内部只读查看已提交的事件历史（防御复制） */
    internal val accumulatedEvents: List<GameEvent>
        get() = _accumulatedEvents.map { snapshotGameEvent(it) }

    /**
     * 读取当前权威状态的完整深拷贝
     */
    fun readState(): GameState = state.deepCopy()

    /**
     * 大厅准备阶段单局未发牌时的原子配置更新
     */
    internal fun updateLobbyConfig(config: com.majiang.match.MatchConfig) {
        check(version == 0L && state.phase == GamePhase.STACKING && state.wall.isEmpty()) {
            "Cannot update lobby config on active or progressed round"
        }
        for (i in 0 until 4) {
            state.scores[i] = config.startingScores[i]
            state.cheatPoints[i] = if (config.cheatEconomy.enabled) {
                config.cheatEconomy.initialPoints.getOrElse(i) { 0 }
            } else {
                0
            }
        }
        state.roundWind = config.winds.firstOrNull() ?: 1
        state.roundNumber = 0
        state.dealerIndex = 0
        state.currentPlayer = 0
        state.honbaSticks = 0
        state.riichiSticks = 0

        startingScores = java.util.Collections.unmodifiableList(config.startingScores.toList())
        startingRiichiSticks = 0
        val cheatPts = if (config.cheatEconomy.enabled) {
            config.cheatEconomy.initialPoints.toList()
        } else {
            List(4) { 0 }
        }
        startingCheatPoints = java.util.Collections.unmodifiableList(cheatPts)
    }

    /**
     * 准备执行已批准的单步操作，组装隔离的候选变更
     *
     * 保证纯函数式/事务性：调用 prepare 绝不修改当前 state、version、responseWindow 或 result。
     * 若本局已结束或操作不合法，抛出异常。
     */
    internal fun prepare(step: ApprovedRoundStep): RoundChange {
        if (state.phase == GamePhase.ROUND_END || _result != null) {
            throw IllegalStateException("Round $roundId is already finished")
        }
        if (step.action is GameAction.DeclareAgariYame) {
            throw IllegalArgumentException("DeclareAgariYame is not supported in RoundStateMachine")
        }
        if (step.action is GameAction.StartStacking && (state.phase != GamePhase.STACKING || state.wall.isNotEmpty())) {
            throw IllegalStateException("StartStacking is only allowed on an un-dealt round instance")
        }

        return RoundChange(
            roundId = roundId,
            sourceVersion = version,
            state = step.state,
            responseWindow = step.responseWindow,
            result = step.result,
            events = step.events
        )
    }

    /**
     * 外部验证和审计通过后调用，原子应用已准备的变更
     */
    internal fun install(change: RoundChange) {
        check(change.roundId == roundId) {
            "Cannot install change for round ${change.roundId} into round $roundId"
        }
        check(change.sourceVersion == version) {
            "Version mismatch: expected $version, got ${change.sourceVersion}"
        }

        // 若为开局发牌动作，冻结起始积分、起始立直棒与起始出千点数
        if (state.phase == GamePhase.STACKING && change.state.phase != GamePhase.STACKING) {
            startingScores = change.state.scores.toList()
            startingRiichiSticks = change.state.riichiSticks
            if (_accumulatedEvents.none { it is GameEvent.CheatPointsChanged } && change.events.none { it is GameEvent.CheatPointsChanged }) {
                startingCheatPoints = change.state.cheatPoints.toList()
            }
        }

        change.state.copyInto(this.state)
        this._responseWindow = change.responseWindow?.deepCopy()
        val changeResult = change.result
        if (changeResult != null) {
            this._result = changeResult.deepCopy()
        }
        this._accumulatedEvents.addAll(change.events.map { snapshotGameEvent(it) })
        this.version++
    }
}

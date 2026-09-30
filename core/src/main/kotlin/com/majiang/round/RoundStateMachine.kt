package com.majiang.round

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.ResponseWindow
import java.util.UUID

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

        // 若为开局发牌动作，冻结起始积分与起始立直棒
        if (version == 0L && state.phase == GamePhase.STACKING && change.state.phase != GamePhase.STACKING) {
            startingScores = change.state.scores.toList()
            startingRiichiSticks = change.state.riichiSticks
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

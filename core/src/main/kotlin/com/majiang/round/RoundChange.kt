package com.majiang.round

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.state.GameAction
import com.majiang.state.GameState
import com.majiang.state.RejectionReason
import com.majiang.state.ResponseWindow

/**
 * 经外部规则服务检验并通过的单步操作描述与计算效果
 *
 * 遵循 DESIGN.md 4.1 与 R2 审查修复：
 * - internal 类型与 internal 构造器，仅供 com.majiang.round 内部规划服务生成；
 * - 外部调用者无法随意创建审批指令。
 */
internal data class ApprovedRoundStep internal constructor(
    val action: GameAction,
    val state: GameState,
    val responseWindow: ResponseWindow?,
    val result: RoundResult?,
    val events: List<GameEvent>
)

/**
 * 单局状态变更候选。
 *
 * 遵循 DESIGN.md 4.1 与 R2 审查修复：
 * - 内部构造，只读暴露属性；
 * - 隔离候选计算数据与权威已安装数据；
 * - 对外暴露的所有可变嵌套对象均提供防御复制快照读取，读到的对象无法篡改权威或已安装状态；
 * - 外部验证与审计通过后调用 RoundStateMachine.install 原子提交。
 */
class RoundChange internal constructor(
    val roundId: String,
    val sourceVersion: Long,
    state: GameState,
    responseWindow: ResponseWindow?,
    result: RoundResult?,
    events: List<GameEvent>
) {
    /** 候选状态（外部审计 StateInvariants.assertValid 检验对象） */
    val state: GameState = state

    private val _responseWindow: ResponseWindow? = responseWindow?.deepCopy()

    /** 候选响应窗口（防御复制） */
    val responseWindow: ResponseWindow?
        get() = _responseWindow?.deepCopy()

    private val _result: RoundResult? = result?.deepCopy()

    /** 候选终局结果（防御复制） */
    val result: RoundResult?
        get() = _result?.deepCopy()

    private val _events: List<GameEvent> = events.map { snapshotGameEvent(it) }

    /** 候选步骤产生的事件列表（防御复制） */
    val events: List<GameEvent>
        get() = _events.map { snapshotGameEvent(it) }
}

/**
 * 外部规划与校验结果
 */
sealed interface RoundPreparation {
    data class Ready(val change: RoundChange) : RoundPreparation
    data class Rejected(val reason: RejectionReason) : RoundPreparation
}

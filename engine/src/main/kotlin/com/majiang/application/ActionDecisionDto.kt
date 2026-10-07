package com.majiang.application

import com.majiang.state.GameAction

/**
 * 玩家当前可做的决策（局内合法动作或局间操作）
 *
 * @property roundId 决策所属的单局标识；若为整场/局间决策（如尾庄和止）则为 null
 * @property decisionId 当前决策的唯一版本标识（窗口变化、局变化或控制代次变化会更新）
 * @property controlEpoch 该座位当前的控制代次
 * @property options 该决策下所有候选选项（action 均已深拷贝）
 */
data class PlayerDecision(
    val roundId: String?,
    val decisionId: String,
    val controlEpoch: Long,
    val options: List<ActionOption>
)

/**
 * 候选选项
 *
 * @property optionId 选项唯一标识（如 opt-0）
 * @property action 动作深拷贝
 */
data class ActionOption(
    val optionId: String,
    val action: GameAction
)

/**
 * 玩家或端点提交的选择
 *
 * @property requestId 请求去重标识（按座位作用域去重，幂等回放）
 * @property roundId 目标局标识
 * @property decisionId 依据的决策标识
 * @property controlEpoch 提交时的控制代次
 * @property optionId 所选选项标识
 */
data class ActionChoice(
    val requestId: String,
    val roundId: String?,
    val decisionId: String,
    val controlEpoch: Long,
    val optionId: String
)

fun PlayerDecision.deepCopy(): PlayerDecision = PlayerDecision(
    roundId = this.roundId,
    decisionId = this.decisionId,
    controlEpoch = this.controlEpoch,
    options = this.options.map { it.deepCopy() }
)

fun ActionOption.deepCopy(): ActionOption = ActionOption(
    optionId = this.optionId,
    action = this.action.deepCopy()
)


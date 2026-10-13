package com.majiang.state

import com.majiang.event.GameEvent

/**
 * 动作执行结果契约 (DESIGN.md 4.1)
 *
 * 统一领域层动作处理返回值：
 * - Accepted: 动作校验通过并已原子提交，携带该动作所产生的不可变领域事件列表。
 * - Rejected: 动作因规则/阶段/所有权不满足被拒绝，不修改任何状态，不发布事件，给出具体拒绝原因。
 */
sealed interface ActionResult {
    data class Accepted(val events: List<GameEvent> = emptyList()) : ActionResult
    data class Rejected(val reason: RejectionReason) : ActionResult
}

enum class RejectionReason {
    INVALID_PLAYER,
    WRONG_PHASE,
    NOT_YOUR_TURN,
    TILE_NOT_OWNED,
    INVALID_COMBINATION,
    ACTION_NOT_AVAILABLE,
    ALREADY_RESPONDED,
    MATCH_FINISHED,
    UNSUPPORTED_ACTION,
    SLEEVE_BLOOM_CHOICE_PENDING,
    BEAM_SWAP_CHOICE_PENDING
}

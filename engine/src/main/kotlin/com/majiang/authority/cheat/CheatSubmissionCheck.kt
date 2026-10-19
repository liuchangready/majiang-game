package com.majiang.authority.cheat

/**
 * D-09 千术请求预检结果。
 *
 * 专用处理器只做归一化、候选/资格与局面一致性判断，不写回执、不扣费、不改牌。
 * [StaleOffer] 表示发现挂起 offer 已与实际牌局漂移，由 TableAuthority 在原事务边界内清理。
 */
internal sealed interface CheatSubmissionCheck {
    data object Allowed : CheatSubmissionCheck

    data class Rejected(val reason: String) : CheatSubmissionCheck

    data class StaleOffer(val reason: String) : CheatSubmissionCheck
}

/**
 * 对只接受“允许/普通拒绝”的激活类入口统一失败关闭；StaleOffer 也只当作拒绝，
 * 绝不被误读为允许执行。
 */
internal val CheatSubmissionCheck.rejectionReason: String?
    get() = when (this) {
        CheatSubmissionCheck.Allowed -> null
        is CheatSubmissionCheck.Rejected -> reason
        is CheatSubmissionCheck.StaleOffer -> reason
    }

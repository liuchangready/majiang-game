package com.majiang.presentation.game

/**
 * 权威倒计时追踪器 (AuthorityCountdownTracker)。
 *
 * 核心职责：
 * 1. 封装权威时钟 (serverNow, deadline) 的本地单调时间采样与流逝推算；
 * 2. 采样保持：同一快照或同一决策周期内重复刷新不重置采样基准，防止本地时钟预算被无限刷新延长；
 * 3. 暂停感知：对局暂停时返回 0 或不计流逝，恢复时按最新权威补偿样本重新基准采样；
 * 4. 到期保护：剩余时间 <= 0 时阻止操作确认与输入；
 * 5. 纯表现层工具，只依赖标准库，不修改权威状态或发送越权请求。
 */
class AuthorityCountdownTracker(
    val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
) {
    /** 本地接收到权威时钟样本时的单调时间戳 (毫秒)；null 表示当前无有效采样 */
    var sampledLocalMillis: Long? = null
        private set

    /** 采样时刻的权威服务器时间 (serverNow) */
    var sampledServerNow: Long = 0L
        private set

    /** 采样时刻的权威截止时间 (deadlineMillis 或 deadline) */
    var cachedDeadline: Long = 0L
        private set

    /** 是否持有活跃的权威采样 */
    val hasActiveSample: Boolean
        get() = sampledLocalMillis != null && cachedDeadline > 0L

    /**
     * 剩余决策毫秒数。
     * 若对局处于暂停状态、未采样或已超时，严格返回 0L。
     */
    fun remainingMillis(isPaused: Boolean = false, nowMillis: Long = monotonicClock()): Long {
        if (isPaused) return 0L
        val sample = sampledLocalMillis ?: return 0L
        val elapsed = maxOf(0L, nowMillis - sample)
        val estimatedServerNow = sampledServerNow + elapsed
        return maxOf(0L, cachedDeadline - estimatedServerNow)
    }

    /**
     * 校验当前预估时间是否在截止期限前 (estimatedServerNow < cachedDeadline)。
     * 若对局处于暂停状态或未采样，返回 false。
     */
    fun isDeadlineValid(isPaused: Boolean = false, nowMillis: Long = monotonicClock()): Boolean {
        if (isPaused) return false
        val sample = sampledLocalMillis ?: return false
        val elapsed = maxOf(0L, nowMillis - sample)
        val estimatedServerNow = sampledServerNow + elapsed
        return estimatedServerNow < cachedDeadline
    }

    /**
     * 更新或维持权威时钟采样。
     *
     * 采样规则：
     * - 当 deadline <= 0 或处于暂停状态时，清空当前采样；
     * - 当已有采样且属于同一快照/代次 (isSameIdentity == true)，维持原有本地基准采样，不更新 sampledLocalMillis；
     * - 当全新代次或采样为空时，记录当前 nowMillis、serverNow 与 deadline。
     */
    fun updateSample(
        serverNow: Long,
        deadline: Long,
        isSameIdentity: Boolean,
        isPaused: Boolean = false,
        nowMillis: Long = monotonicClock()
    ) {
        if (deadline > 0L && !isPaused) {
            if (!isSameIdentity || sampledLocalMillis == null) {
                sampledLocalMillis = nowMillis
                sampledServerNow = serverNow
                cachedDeadline = deadline
            }
        } else {
            clear()
        }
    }

    /**
     * 清空当前采样状态。
     */
    fun clear() {
        sampledLocalMillis = null
        sampledServerNow = 0L
        cachedDeadline = 0L
    }
}

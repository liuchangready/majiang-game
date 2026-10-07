package com.majiang.application

/**
 * 玩家当前可执行决策注册表
 *
 * 维护每个座位的活跃决策缓存。
 * - get / register 均做防御性深拷贝，避免外部篡改候选选项及内部动作。
 * - consume(seat) 仅在动作成功应用后安全清除本座位的决策，同窗口其他座位的决策不受影响。
 * - invalid 请求绝不清除合法决策缓存。
 * - 内部同步监视器锁保证多读线程并发调用 queryDecision 时的线程安全，杜绝读锁升级死锁。
 */
class DecisionRegistry {
    private val lock = Any()
    private val cached = arrayOfNulls<PlayerDecision>(4)

    fun get(seat: Int): PlayerDecision? = synchronized(lock) {
        if (seat !in 0..3) return null
        cached[seat]?.deepCopy()
    }

    fun register(seat: Int, decision: PlayerDecision): PlayerDecision = synchronized(lock) {
        if (seat !in 0..3) return decision.deepCopy()
        val copy = decision.deepCopy()
        cached[seat] = copy
        return copy.deepCopy()
    }

    fun getOrRegister(
        seat: Int,
        roundId: String?,
        decisionId: String,
        controlEpoch: Long,
        computeOptions: () -> List<ActionOption>
    ): PlayerDecision = synchronized(lock) {
        if (seat !in 0..3) {
            return PlayerDecision(roundId, decisionId, controlEpoch, emptyList())
        }
        val cur = cached[seat]
        if (cur != null && cur.roundId == roundId && cur.decisionId == decisionId && cur.controlEpoch == controlEpoch) {
            return cur.deepCopy()
        }
        val options = computeOptions()
        val decision = PlayerDecision(roundId, decisionId, controlEpoch, options)
        cached[seat] = decision.deepCopy()
        return decision.deepCopy()
    }

    fun consume(seat: Int) = synchronized(lock) {
        if (seat in 0..3) {
            cached[seat] = null
        }
    }

    fun invalidate(seat: Int) = synchronized(lock) {
        if (seat in 0..3) {
            cached[seat] = null
        }
    }

    fun invalidateAll() = synchronized(lock) {
        for (i in 0 until 4) {
            cached[i] = null
        }
    }
}

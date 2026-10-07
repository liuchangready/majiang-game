package com.majiang.transport.udp

/**
 * 防重放与连接序号校验器 (ReplayGuard)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.4-4 防重放与完整性。
 * - 每方向 seq 单调：`accept(seq)` 在 `seq <= maxSeen` 时返回 false（丢弃已见重放包与滞后旧包）；
 * - 乱序向前跳跃（如 seq 1 后直接收到 seq 3）允许接受，同时更新 maxSeen 阻断后续重复；
 * - connectionId 不匹配的数据报由上层静默丢弃（防放大反射攻击），本组件提供静态与实例校验函数。
 */
class ReplayGuard(
    private val expectedConnectionId: Int? = null,
    initialMaxSeen: Int = 0,
    private val windowSize: Int = 256
) {
    init {
        require(windowSize > 0) { "windowSize must be positive: $windowSize" }
    }

    @Volatile
    private var maxSeen: Int = initialMaxSeen
    @Volatile
    private var baseline: Int = initialMaxSeen
    private val seenSeq = IntArray(windowSize) { -1 }

    init {
        if (initialMaxSeen > 0) {
            seenSeq[initialMaxSeen % windowSize] = initialMaxSeen
        }
    }

    /**
     * 校验入站数据报的序号是否合法（基于有界滑动窗口防重放）。
     *
     * @param seq 数据报中的发送序号
     * @return 当为新序或窗口内未曾见过的乱序报文时返回 true 并记录；当为已见重复报文或窗口外老包时返回 false
     */
    @Synchronized
    fun accept(seq: Int): Boolean {
        if (seq <= 0) return false
        if (seq <= baseline) return false
        if (seq > maxSeen) {
            val diff = seq - maxSeen
            if (diff >= windowSize) {
                seenSeq.fill(-1)
            } else {
                for (s in (maxSeen + 1) until seq) {
                    seenSeq[s % windowSize] = -1
                }
            }
            maxSeen = seq
            seenSeq[seq % windowSize] = seq
            return true
        }

        // seq <= maxSeen && seq > baseline
        if (maxSeen - seq >= windowSize) {
            return false // 超出窗口左边界，视为陈旧重放
        }
        val idx = seq % windowSize
        if (seenSeq[idx] == seq) {
            return false // 已接收过，阻断重复重放
        }
        seenSeq[idx] = seq
        return true
    }

    /**
     * 校验数据报携带的 connectionId 是否与本端绑定预期相符。
     *
     * @param connectionId 数据报中的会话 ID
     * @return 若预期未设定（如握手期）或两者完全一致返回 true，否则返回 false（上层静默丢弃）
     */
    fun validateConnectionId(connectionId: Int): Boolean {
        return expectedConnectionId == null || expectedConnectionId == connectionId
    }

    /**
     * 获取当前记录的已见最大 seq
     */
    fun getMaxSeen(): Int = maxSeen

    /**
     * 重置防重放窗口状态（如新连接建立时）
     */
    @Synchronized
    fun reset(newMaxSeen: Int = 0) {
        baseline = newMaxSeen
        maxSeen = newMaxSeen
        seenSeq.fill(-1)
        if (newMaxSeen > 0) {
            seenSeq[newMaxSeen % windowSize] = newMaxSeen
        }
    }

    companion object {
        /**
         * 校验数据报 connectionId 是否与期望的会话 ID 严格一致
         */
        fun isConnectionValid(frame: UdpFrame, expectedConnectionId: Int): Boolean {
            return frame.connectionId == expectedConnectionId
        }

        /**
         * 校验两个 connectionId 是否匹配
         */
        fun matchConnectionId(actual: Int, expected: Int): Boolean {
            return actual == expected
        }
    }
}

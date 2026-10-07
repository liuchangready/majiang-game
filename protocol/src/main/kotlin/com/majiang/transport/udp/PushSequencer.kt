package com.majiang.transport.udp

/**
 * 推送更新处理结果
 */
sealed class PushOfferResult {
    /** 连续按序应用，当前游标推进至 [cursor] */
    data class Applied(val cursor: Long) : PushOfferResult()

    /** 陈旧或重复包 (cursor <= currentCursor)，已被安全丢弃 */
    data class Stale(val cursor: Long, val currentCursor: Long) : PushOfferResult()

    /** 窗口内缺口检测：需向服务端发送 RESEND(fromCursor = [missingFromCursor]) */
    data class GapDetected(val missingFromCursor: Long, val updateCursor: Long) : PushOfferResult()

    /** 缺口超出重传窗口 (>= windowSize)：请求方直接采用全量快照自愈 */
    data class BeyondWindowGap(val currentCursor: Long, val updateCursor: Long) : PushOfferResult()
}

/**
 * 客户端推送游标顺序与自愈器 (PushSequencer)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.4-3 推送流（游标序 + 自愈）。
 * - 接收端按 cursor 单调推进；
 * - 当收到包的 `cursor <= 本端游标` 时直接丢弃（抗乱序与重复）；
 * - 当 `fromCursor > 本端游标 + 1` 判定存在未收到缺口：
 *   - 若缺口在重传窗口内（< 256），产出 `RESEND(fromCursor = 本端游标 + 1)` 动作；
 *   - 若缺口超出重传窗口（>= 256），触发全量快照回调直接跳进；
 * - 当 `fromCursor <= 本端游标 + 1` 且 `cursor > 本端游标`，正常按序推进并应用。
 */
class PushSequencer(
    initialCursor: Long = 0L,
    private val windowSize: Int = UdpProtocol.RETRANSMIT_WINDOW_SIZE,
    private val onResendNeeded: ((missingFromCursor: Long) -> Unit)? = null,
    private val onFullSnapshotNeeded: ((currentCursor: Long, updateCursor: Long) -> Unit)? = null,
    private val onApplied: ((cursor: Long) -> Unit)? = null
) {
    @Volatile
    private var currentCursor: Long = initialCursor

    /**
     * 获取当前已确认推进的本端游标
     */
    fun getCurrentCursor(): Long = currentCursor

    /**
     * 接收并处理一条推送更新。
     *
     * @param cursor 该更新到达的权威游标
     * @param fromCursor 该更新所基于的前置权威游标
     * @param isFullSnapshot 是否为全量快照包（全量快照无需校验前置连续性，可直接自愈采用）
     * @return 处理结果状态 [PushOfferResult]
     */
    @Synchronized
    fun offer(cursor: Long, fromCursor: Long, isFullSnapshot: Boolean = false): PushOfferResult {
        // 1. 陈旧包或已应用过的重复包，直接丢弃（全量快照若 cursor <= currentCursor 亦属陈旧，严禁回滚）
        if (cursor <= currentCursor) {
            return PushOfferResult.Stale(cursor = cursor, currentCursor = currentCursor)
        }

        // 2. 若明确为全量快照且游标推进，则直接跃迁至最新权威游标完成自愈
        if (isFullSnapshot) {
            currentCursor = cursor
            onApplied?.invoke(currentCursor)
            return PushOfferResult.Applied(currentCursor)
        }

        // 3. 缺口判定：fromCursor > 本端游标 + 1
        if (fromCursor > currentCursor + 1) {
            val missingFromCursor = currentCursor + 1
            val gap = fromCursor - missingFromCursor
            return if (gap >= windowSize) {
                // 缺口过大，超出服务端 256 环形重传窗口，通知采用全量快照
                onFullSnapshotNeeded?.invoke(currentCursor, cursor)
                PushOfferResult.BeyondWindowGap(currentCursor = currentCursor, updateCursor = cursor)
            } else {
                // 窗口内缺口，发起补发请求
                onResendNeeded?.invoke(missingFromCursor)
                PushOfferResult.GapDetected(missingFromCursor = missingFromCursor, updateCursor = cursor)
            }
        }

        // 4. 连续包：fromCursor <= currentCursor + 1 且 cursor > currentCursor
        currentCursor = cursor
        onApplied?.invoke(cursor)
        return PushOfferResult.Applied(cursor)
    }

    /**
     * 显式以全量快照推进游标（如重连恢复或全量刷新），保持在同一流内单调递增
     */
    @Synchronized
    fun adoptFullSnapshot(snapshotCursor: Long) {
        if (snapshotCursor > currentCursor) {
            currentCursor = snapshotCursor
            onApplied?.invoke(snapshotCursor)
        }
    }

    /**
     * 重置游标（如换局、新绑定或新会话代次，允许建立新的更低基线）
     */
    @Synchronized
    fun reset(newCursor: Long = 0L) {
        currentCursor = newCursor
    }
}

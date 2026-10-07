package com.majiang.access

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import java.util.Collections

/**
 * 玩家订阅增量更新数据包 (PlayerUpdate)
 *
 * 携带最新快照 [snapshot]、前置游标 [fromCursor] 以及本批次已提交的可见领域事件 [visibleEvents]。
 * 游标按座位独立连续，私密事件脱敏绝不造成可见游标缺口。
 *
 * @property snapshot 最新可见状态完整快照
 * @property fromCursor 前置游标
 * @property visibleEvents 自前置游标以来产生的脱敏可见事件
 */
data class PlayerUpdate(
    val snapshot: PlayerSnapshot,
    val fromCursor: Long,
    val visibleEvents: List<GameEvent> = emptyList()
) {
    init {
        require(fromCursor >= 0L) { "fromCursor must be >= 0: $fromCursor" }
        require(snapshot.cursor >= fromCursor) {
            "snapshot.cursor (${snapshot.cursor}) cannot be less than fromCursor ($fromCursor)"
        }
    }

    fun deepCopy(): PlayerUpdate = PlayerUpdate(
        snapshot = snapshot.deepCopy(),
        fromCursor = fromCursor,
        visibleEvents = Collections.unmodifiableList(visibleEvents.map { snapshotGameEvent(it) })
    )
}

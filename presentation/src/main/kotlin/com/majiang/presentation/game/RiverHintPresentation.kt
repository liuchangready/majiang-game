package com.majiang.presentation.game

import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.GameSnapshotView
import java.util.ArrayList
import java.util.Collections

/**
 * 牌河提示数据模型 (RiverHintView)。
 *
 * @property wallRemaining 牌山剩余可摸总数（null 表示未发牌或缺少快照；合法 0 保留 0）
 * @property hands 按本局门风东、南、西、北顺序排列的四方手牌行展示
 */
class RiverHintView(val wallRemaining: Int?, hands: List<OpponentHandRow>) {
    val hands: List<OpponentHandRow> = Collections.unmodifiableList(ArrayList(hands))

    operator fun component1(): Int? = wallRemaining
    operator fun component2(): List<OpponentHandRow> = hands

    override fun equals(other: Any?): Boolean =
        other is RiverHintView && wallRemaining == other.wallRemaining && hands == other.hands

    override fun hashCode(): Int = 31 * (wallRemaining ?: 0) + hands.hashCode()
    override fun toString(): String = "RiverHintView(wallRemaining=$wallRemaining, hands=$hands)"
}

/**
 * 牌河提示纯视图派生组件 (RiverHintPresentation)。
 *
 * 核心契约：
 * 1. 仅依赖 client.api 视图模型；
 * 2. build 按本局门风东南西北返回四行手牌展示；
 * 3. 本人所在行仅允许 OWN_HAND 标记翻面，普通手牌为牌背；
 * 4. 对手行仅允许该席位 OPPONENT_HAND 标记翻面；
 * 5. NOT_VISIBLE、DISCARD、副露等不作为暗手牌翻面；
 * 6. null 快照返回 wallRemaining=null, hands=emptyList；
 * 7. STACKING 未发牌阶段 wallRemaining=null，hands 四行 slots 为空；
 * 8. 其余阶段原样读取快照 wallRemaining，合法 0 保留 0。
 */
object RiverHintPresentation {

    fun build(snapshot: GameSnapshotView?): RiverHintView {
        if (snapshot == null) return RiverHintView(null, Collections.emptyList())
        val phase = snapshot.view.phase
        val isStacking = (phase == GamePhaseView.STACKING)
        val wallRemaining = if (isStacking) null else snapshot.view.wallRemaining
        val dealer = snapshot.view.dealerSeat
        val viewer = snapshot.viewerSeat
        val handCounts = snapshot.view.handTileCounts

        val hands = ArrayList<OpponentHandRow>(4)
        for (windOffset in 0..3) {
            val seat = (dealer + windOffset) % 4
            val count = if (isStacking) 0 else (handCounts.getOrNull(seat) ?: 0)
            val slots = OpponentHandPresentation.buildSlotsForSeat(
                snapshot = snapshot,
                seat = seat,
                count = count,
                isViewer = (seat == viewer),
                isStacking = isStacking
            )
            hands.add(OpponentHandRow(seat, slots))
        }
        return RiverHintView(wallRemaining, Collections.unmodifiableList(hands))
    }
}

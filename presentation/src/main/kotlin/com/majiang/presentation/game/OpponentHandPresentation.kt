package com.majiang.presentation.game

import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.TileMarkArea
import com.majiang.client.api.TileView
import java.util.ArrayList
import java.util.Collections

/**
 * 手牌槽位展示模型 (HandDisplaySlot)。
 *
 * @property tile 本人授权有效落汗牌面；null 表示普通不透明牌背（牌背无身份）。
 */
data class HandDisplaySlot(val tile: TileView?)

/**
 * 对手或指定席位暗手牌单行展示视图 (OpponentHandRow)。
 *
 * @property seat 目标席位 (0..3)
 * @property slots 暗手牌槽位列表（防御性复制不可变列表）
 */
class OpponentHandRow(val seat: Int, slots: List<HandDisplaySlot>) {
    val slots: List<HandDisplaySlot> = Collections.unmodifiableList(ArrayList(slots))

    operator fun component1(): Int = seat
    operator fun component2(): List<HandDisplaySlot> = slots

    override fun equals(other: Any?): Boolean =
        other is OpponentHandRow && seat == other.seat && slots == other.slots

    override fun hashCode(): Int = 31 * seat + slots.hashCode()
    override fun toString(): String = "OpponentHandRow(seat=$seat, slots=$slots)"
}

/**
 * 对手暗手牌纯视图派生组件 (OpponentHandPresentation)。
 *
 * 核心契约：
 * 1. 仅依赖 client.api 视图模型；
 * 2. build 返回按 viewerSeat 后的下家、对家、上家顺序的三行；
 * 3. 每行 slots 数量严格等于 handTileCounts[seat]，不产生多牌或未知牌面；
 * 4. 仅该家 OPPONENT_HAND 的有效标记可翻面，按 (typeIndex, tileId) 排列在一端；
 * 5. 重复/超量授权信息安全降级到牌背；
 * 6. null 快照返回空列表；STACKING 阶段返回空槽行。
 */
object OpponentHandPresentation {

    fun build(snapshot: GameSnapshotView?): List<OpponentHandRow> {
        if (snapshot == null) return Collections.emptyList()
        val phase = snapshot.view.phase
        val viewer = snapshot.viewerSeat
        val handCounts = snapshot.view.handTileCounts

        val rows = ArrayList<OpponentHandRow>(3)
        for (i in 1..3) {
            val seat = (viewer + i) % 4
            val count = if (phase == GamePhaseView.STACKING) 0 else (handCounts.getOrNull(seat) ?: 0)
            val slots = buildSlotsForSeat(
                snapshot = snapshot,
                seat = seat,
                count = count,
                isViewer = false,
                isStacking = (phase == GamePhaseView.STACKING)
            )
            rows.add(OpponentHandRow(seat, slots))
        }
        return Collections.unmodifiableList(rows)
    }

    /**
     * 针对纯计数公开数据构造对手暗手牌行（供观战等无私有授权快照的场景使用）。
     * 对 seat=(0+i)%4 (即 seat 1..3) 构造，STACKING 阶段 0 张，其余均为 HandDisplaySlot(null)。
     */
    fun rowsFromCounts(handTileCounts: List<Int>, phase: GamePhaseView): List<OpponentHandRow> {
        val rows = ArrayList<OpponentHandRow>(3)
        for (i in 1..3) {
            val seat = i
            val count = if (phase == GamePhaseView.STACKING) 0 else (handTileCounts.getOrNull(seat) ?: 0)
            val slots = if (count <= 0) {
                Collections.emptyList()
            } else {
                Collections.unmodifiableList(List(count) { HandDisplaySlot(null) })
            }
            rows.add(OpponentHandRow(seat, slots))
        }
        return Collections.unmodifiableList(rows)
    }

    internal fun buildSlotsForSeat(
        snapshot: GameSnapshotView,
        seat: Int,
        count: Int,
        isViewer: Boolean,
        isStacking: Boolean
    ): List<HandDisplaySlot> {
        if (count <= 0 || isStacking) return Collections.emptyList()

        val activeGroups = snapshot.tileMark?.activeGroups ?: emptyList()
        val matchingMarks = ArrayList<TileView>()
        val seenTileIds = mutableSetOf<Int>()
        var duplicateFound = false

        for (group in activeGroups) {
            for (marked in group.tiles) {
                val matches = if (isViewer) {
                    marked.area == TileMarkArea.OWN_HAND && marked.seat == seat
                } else {
                    marked.area == TileMarkArea.OPPONENT_HAND && marked.seat == seat && seat != snapshot.viewerSeat
                }
                if (matches) {
                    if (!seenTileIds.add(marked.tileId)) {
                        duplicateFound = true
                    }
                    matchingMarks.add(marked.tile)
                }
            }
        }

        // 重复或超量授权信息安全降级为牌背，不产生多牌或未知牌面
        if (duplicateFound || matchingMarks.size > count) {
            return Collections.unmodifiableList(List(count) { HandDisplaySlot(null) })
        }

        // 已知牌按 typeIndex、tileId 升序排列在一端
        matchingMarks.sortWith(compareBy({ it.typeIndex }, { it.tileId }))

        val slots = ArrayList<HandDisplaySlot>(count)
        for (tile in matchingMarks) {
            slots.add(HandDisplaySlot(tile))
        }
        while (slots.size < count) {
            slots.add(HandDisplaySlot(null))
        }
        return Collections.unmodifiableList(slots)
    }
}

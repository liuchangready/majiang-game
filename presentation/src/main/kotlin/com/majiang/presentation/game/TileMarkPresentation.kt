package com.majiang.presentation.game

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.MarkedTileView
import com.majiang.client.api.TileMarkArea
import com.majiang.client.api.TileMarkGroupView

/**
 * 落汗千术表现层辅助工具 (TileMarkPresentation)。
 *
 * 核心契约：
 * 1. 纯客户端辅助组件，仅依赖 client.api；
 * 2. [riverMarks] 返回当前局与快照牌河精确一致的 DISCARD 标记；
 * 3. [opponentMarks] 仅返回对手座位的 OPPONENT_HAND 授权标记牌；
 * 4. [locationText] 与 [durationText] 提供统一的人类可读中文描述；
 * 5. 不一致快照或缺失数据一律安全降级，不自作主张修正领域身份。
 */
object TileMarkPresentation {

    /**
     * 提取当前快照中与实际牌河位置一致的 DISCARD 标记。
     */
    fun riverMarks(snapshot: GameSnapshotView?): List<MarkedTileView> {
        val mark = snapshot?.tileMark ?: return emptyList()
        val currentRoundId = snapshot.roundId ?: return emptyList()
        val discards = snapshot.view.discards
        val result = mutableListOf<MarkedTileView>()

        for (group in mark.activeGroups) {
            for (marked in group.tiles) {
                if (marked.area == TileMarkArea.DISCARD) {
                    val pos = marked.riverPosition
                    val seat = marked.seat
                    if (pos != null && seat != null && pos.seat == seat && pos.roundId == currentRoundId) {
                        val seatDiscards = discards.getOrNull(seat)
                        if (seatDiscards != null && pos.discardIndex in seatDiscards.indices) {
                            if (seatDiscards[pos.discardIndex].tileId == marked.tileId) {
                                result.add(marked)
                            }
                        }
                    }
                }
            }
        }
        return result
    }

    /**
     * 获取指定对手座位的已授权识别牌列表（仅 OPPONENT_HAND 且属于该席位）。
     */
    fun opponentMarks(snapshot: GameSnapshotView?, seat: Int): List<MarkedTileView> {
        val mark = snapshot?.tileMark ?: return emptyList()
        if (seat == snapshot.viewerSeat || seat !in 0..3) return emptyList()
        val result = mutableListOf<MarkedTileView>()

        for (group in mark.activeGroups) {
            for (marked in group.tiles) {
                if (marked.area == TileMarkArea.OPPONENT_HAND && marked.seat == seat) {
                    result.add(marked)
                }
            }
        }
        return result
    }

    /**
     * 相对位置中文描述（以 viewerSeat 为基准）。
     */
    fun locationText(tile: MarkedTileView, viewerSeat: Int): String = when (tile.area) {
        TileMarkArea.DISCARD -> {
            val seat = tile.seat ?: 0
            val relative = listOf("自己", "下家", "对家", "上家")[(seat - viewerSeat + 4) % 4]
            val index1Based = (tile.riverPosition?.discardIndex ?: 0) + 1
            "${relative}牌河第 $index1Based 张"
        }
        TileMarkArea.NOT_VISIBLE -> "当前不可见"
        TileMarkArea.OWN_HAND -> "自己手中"
        TileMarkArea.OPPONENT_HAND -> {
            val seat = tile.seat ?: 0
            val relative = listOf("自己", "下家", "对家", "上家")[(seat - viewerSeat + 4) % 4]
            "${relative}手中"
        }
        TileMarkArea.EXPOSED_MELD -> {
            val seat = tile.seat ?: 0
            val relative = listOf("自己", "下家", "对家", "上家")[(seat - viewerSeat + 4) % 4]
            "${relative}副露"
        }
        TileMarkArea.EXPOSED_INDICATOR -> "宝牌指示牌"
    }

    /**
     * 独立期限中文描述。
     */
    fun durationText(group: TileMarkGroupView): String =
        if (group.isLastEffectiveRound || group.futureRoundsRemaining == 0) {
            "本局最后有效"
        } else {
            "另余 ${group.futureRoundsRemaining} 局"
        }
}

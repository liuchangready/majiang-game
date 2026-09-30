package com.majiang.render

import com.majiang.model.Suit
import com.majiang.model.Tile

/**
 * 整理后的手牌表现层视图数据。
 *
 * @property tiles 包含暗牌及摸牌（若有）的展示牌面列表（已排序）
 * @property drawnTileIndex 摸牌在 [tiles] 中的索引（若无摸牌则为 -1）
 */
data class ArrangedHand(
    val tiles: List<Tile>,
    val drawnTileIndex: Int = -1
)

/**
 * 麻将手牌表现层理牌器（纯函数无副作用）。
 *
 * 排序规则（严格定义）：
 * 1. 花色组总序：万(MAN) → 索(SOU) → 筒(PIN) → 风牌(WIND) → 三元牌(DRAGON)
 * 2. 数牌(万/索/筒)内部：数字从小到大（1→9），同种多张相邻按 copyIndex 升序
 * 3. 风牌内部：東(1) → 南(2) → 西(3) → 北(4)（rank 升序）
 * 4. 三元牌内部：中(3) → 發(2) → 白(1)（rank 降序，模型定义 1=白 2=發 3=中）
 * 5. 同种牌（相同 suit 与 rank）：按物理副本 copyIndex (0..3) 作次级排序，保证顺序稳定
 */
object TileArranger {

    /** 花色组优先级映射：万(0) → 索(1) → 筒(2) → 风(3) → 三元(4) */
    private fun suitPriority(suit: Suit): Int = when (suit) {
        Suit.MAN -> 0
        Suit.SOU -> 1
        Suit.PIN -> 2
        Suit.WIND -> 3
        Suit.DRAGON -> 4
    }

    /**
     * 单张牌标准比较器。
     */
    val TILE_COMPARATOR: Comparator<Tile> = Comparator { a, b ->
        val suitComp = suitPriority(a.suit).compareTo(suitPriority(b.suit))
        if (suitComp != 0) return@Comparator suitComp

        val rankComp = when (a.suit) {
            Suit.DRAGON -> b.rank.compareTo(a.rank) // 降序：中(3) → 發(2) → 白(1)
            else -> a.rank.compareTo(b.rank)       // 升序：1..9, 東(1)..北(4)
        }
        if (rankComp != 0) return@Comparator rankComp

        a.copyIndex.compareTo(b.copyIndex)
    }

    /**
     * 对牌列表按规则排序。
     */
    fun sortTiles(tiles: List<Tile>): List<Tile> = tiles.sortedWith(TILE_COMPARATOR)

    private data class TaggedTile(val tile: Tile, val isDrawn: Boolean)

    /**
     * 针对手牌展示的无状态排序纯函数。
     *
     * 将暗牌与刚摸的牌合并排序，同时精确标记出摸牌在排序后列表中的新索引位置。
     *
     * @param closedTiles 玩家手中的暗牌列表
     * @param drawnTile 刚刚摸到的牌（若无则为 null）
     * @return 包含排序后列表以及摸牌索引的 [ArrangedHand]
     */
    fun sortForDisplay(closedTiles: List<Tile>, drawnTile: Tile? = null): ArrangedHand {
        if (closedTiles.isEmpty() && drawnTile == null) {
            return ArrangedHand(emptyList(), -1)
        }

        val taggedList = ArrayList<TaggedTile>(closedTiles.size + (if (drawnTile != null) 1 else 0))
        for (tile in closedTiles) {
            taggedList.add(TaggedTile(tile, isDrawn = false))
        }
        if (drawnTile != null) {
            taggedList.add(TaggedTile(drawnTile, isDrawn = true))
        }

        // 使用稳定排序，比较器基于 tile 属性
        val sorted = taggedList.sortedWith(Comparator { a, b ->
            TILE_COMPARATOR.compare(a.tile, b.tile)
        })

        val drawnIndex = sorted.indexOfFirst { it.isDrawn }
        return ArrangedHand(sorted.map { it.tile }, drawnIndex)
    }
}

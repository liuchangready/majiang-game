package com.majiang.rules

import com.majiang.model.PlayerHand
import com.majiang.model.Tile

/**
 * 振听状态模型
 *
 * @property discard 舍牌振听：听牌待牌中存在本人历史舍牌（含被他人鸣走的牌）
 * @property temporary 同巡/临时振听：放过他人打出的完成形，持续至本人下次实际摸牌（普通摸牌或岭上摸牌）
 * @property riichi 立直振听：立直宣言后放过和牌机会，持续至本局结束
 */
data class FuritenStatus(
    val discard: Boolean = false,
    val temporary: Boolean = false,
    val riichi: Boolean = false
) {
    val isFuriten: Boolean get() = discard || temporary || riichi
}

/**
 * 振听规则与待牌判定服务 — 纯函数式、无状态副作用
 */
object FuritenRules {

    /**
     * 计算手牌的理论待牌集合（牌种 typeIndex 集合，0..33）
     *
     * 规则要求：
     * - 使用 HandDecomposer 拆解；
     * - 排除剩余闭手与本人副露占满 4 张后的第 5 张牌候选；
     * - 不按公开河或牌山过滤空听（即使场上已见 4 张，只要闭手+副露未占满 4 张依然算待牌）。
     *
     * @param hand 玩家手牌（提供副露信息）
     * @param closedTiles 参与待牌判定的门前暗牌（默认为 hand.closedTiles，通常为 13 张等效闭手）
     * @return 所有和了所需的牌种 typeIndex 集合
     */
    fun waits(hand: PlayerHand, closedTiles: List<Tile> = hand.closedTiles): Set<Int> {
        if (hand.melds.size !in 0..4 || closedTiles.size + 3 * hand.melds.size != 13) {
            return emptySet()
        }

        val closedCounts = IntArray(34)
        for (tile in closedTiles) {
            closedCounts[tile.typeIndex]++
        }
        val meldCounts = IntArray(34)
        for (meld in hand.melds) {
            for (tile in meld.tiles) {
                meldCounts[tile.typeIndex]++
            }
        }

        val rawWaits = HandDecomposer.INSTANCE.calculateWaits(closedTiles)
        return rawWaits.filter { waitType ->
            closedCounts[waitType] + meldCounts[waitType] < 4
        }.toSet()
    }

    /**
     * 计算当前手牌的综合振听状态
     *
     * 规则要求：
     * - 舍牌振听：当前全部理论待牌与本人完整 discardHistory 求交（包含已被别人吃碰杠走的历史舍牌；若 discardHistory 为空则兼容回退 discards）。手牌改听且无交集时解除。
     * - 临时振听与立直振听：继承传入的状态标记。
     *
     * @param hand 玩家手牌（提供舍牌历史、副露等）
     * @param closedTiles 门前暗牌（默认为 hand.closedTiles）
     * @param temporary 同巡/临时振听标记
     * @param riichi 立直振听标记
     * @return 振听状态快照
     */
    fun status(
        hand: PlayerHand,
        closedTiles: List<Tile> = hand.closedTiles,
        temporary: Boolean = false,
        riichi: Boolean = false
    ): FuritenStatus {
        val myWaits = waits(hand, closedTiles)
        val history = if (hand.discardHistory.isNotEmpty()) hand.discardHistory else hand.discards
        val isDiscardFuriten = myWaits.isNotEmpty() && history.any { it.typeIndex in myWaits }

        return FuritenStatus(
            discard = isDiscardFuriten,
            temporary = temporary,
            riichi = riichi
        )
    }

    /**
     * 检查给定的目标牌是否能使手牌完成（数学完成形）
     *
     * 规则要求：
     * - 只检查有效 13 张等效手牌 + 候选是否完成；
     * - 不检查役种，不检查振听。
     *
     * @param hand 玩家手牌（13 张等效手牌）
     * @param tile 候选牌
     * @return 若能构成完成形则返回 true
     */
    fun completesHand(hand: PlayerHand, tile: Tile): Boolean {
        return waits(hand).contains(tile.typeIndex)
    }
}

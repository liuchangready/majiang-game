package com.majiang.ai.cheat

import com.majiang.model.Tile
import kotlin.math.abs

/**
 * AI 千术候选的可见启发式。
 *
 * 所有输入都来自本座位授权投影；这里只做排序与效果估计，不改变任何规则或物理结果。
 */
internal object AiCheatHeuristics {

    /** 单张牌在自身手牌中的保留价值（越高越值得保留）。 */
    fun tileValue(tile: Tile, hand: List<Tile>): Int {
        var value = 0
        val same = hand.count { it.sameType(tile) }
        value += when {
            same >= 3 -> 18
            same == 2 -> 10
            else -> 0
        }
        if (tile.isNumberTile) {
            val near = hand.count {
                it !== tile && it.suit == tile.suit && abs(it.rank - tile.rank) == 1
            }
            val far = hand.count {
                it !== tile && it.suit == tile.suit && abs(it.rank - tile.rank) == 2
            }
            value += near * 5 + far * 2
            if (tile.rank in setOf(1, 9)) value -= 2
        } else {
            value += if (tile.suit == com.majiang.model.Suit.DRAGON) 4 else 1
        }
        return value
    }

    /** 选择价值最低的若干手牌（用于偷梁换柱的换出牌）。 */
    fun leastValuableHandTiles(hand: List<Tile>, limit: Int): List<Tile> =
        hand.sortedWith(compareBy<Tile> { tileValue(it, hand) }.thenBy { it.tileId }).take(limit)

    /** 选择价值最高的若干手牌（用于落汗目标）。 */
    fun mostValuableHandTiles(hand: List<Tile>, limit: Int): List<Tile> =
        hand.sortedWith(compareByDescending<Tile> { tileValue(it, hand) }.thenBy { it.tileId }).take(limit)

    /** 在手牌/候选牌上下文中比较两张候选牌的相对价值。 */
    fun relativeValue(candidate: Tile, hand: List<Tile>): Int = tileValue(candidate, hand)
}

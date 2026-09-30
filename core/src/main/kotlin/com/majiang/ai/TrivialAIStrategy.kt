package com.majiang.ai

import com.majiang.session.PlayerView
import com.majiang.state.GameAction

/**
 * 最简 AI 策略（摸切 AI）
 *
 * 行为特征：
 * - 打牌：永远摸切（优先打出 view.myDrawnTile；无 drawnTile 时返回手牌中的最后一张合法牌）
 * - 副露：永不吃、碰、杠
 * - 立直：永不立直
 * - 和了：能荣和就荣和，能自摸就自摸，保证对局顺利终局
 * - 流局：不主动声明九种九牌流局
 */
class TrivialAIStrategy : AIStrategy {

    override val name: String = "摸切AI"

    override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
        if (legalActions.isEmpty()) return null

        // 1. 和了（荣和 / 自摸）
        val ron = legalActions.firstOrNull { it is GameAction.DeclareRon }
        if (ron != null) return ron

        val tsumo = legalActions.firstOrNull { it is GameAction.DeclareTsumo }
        if (tsumo != null) return tsumo

        // 2. 摸切打牌
        val discardActions = legalActions.filterIsInstance<GameAction.DiscardTile>()
        if (discardActions.isNotEmpty()) {
            val drawn = view.myDrawnTile
            if (drawn != null) {
                val tsumogiri = discardActions.firstOrNull { it.tile == drawn }
                if (tsumogiri != null) return tsumogiri
            }
            // 无 drawnTile 时匹配闭手最后一张或取最后一个合法打牌动作
            val lastClosed = view.myClosedTiles.lastOrNull()
            if (lastClosed != null) {
                val lastMatch = discardActions.firstOrNull { it.tile == lastClosed }
                if (lastMatch != null) return lastMatch
            }
            return discardActions.last()
        }

        // 3. 响应窗口跳过
        val pass = legalActions.firstOrNull { it is GameAction.Pass }
        if (pass != null) return pass

        // 4. 兜底
        return legalActions.first()
    }
}

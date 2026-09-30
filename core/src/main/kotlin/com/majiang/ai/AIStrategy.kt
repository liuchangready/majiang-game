package com.majiang.ai

import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.HandDecomposer
import com.majiang.session.PlayerView
import com.majiang.state.GameAction

/**
 * AI 策略接口
 *
 * 定义 AI 玩家的决策行为。
 * 策略仅接收当前 AI 座位的只读 PlayerView 与合法动作列表，禁止直接访问或暴露完整 GameState。
 */
interface AIStrategy {
    /** 策略名称 */
    val name: String

    /**
     * 根据当前座位视图与该座位合法动作集合，做出决策并返回选中的一个合法动作。
     *
     * @param view 当前 AI 座位的只读深拷贝视图
     * @param legalActions 当前 AI 座位的合法动作列表
     * @return 选中的一个合法动作，或在没有可选动作时返回 null
     */
    fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction?
}

/**
 * 基础 AI 策略 — 简单贪心算法
 *
 * 决策逻辑：
 * - 和了：始终和（自摸/荣和）
 * - 流局：有九种九牌流局机会则流局
 * - 立直：听牌且点数足够即立直，选择待牌数最多的打法
 * - 副露：碰役牌（三元牌、自风、场风）；暗杠一般无害倾向于杠；不主动吃
 * - 响应窗口：若不鸣牌/不和则 Pass
 * - 打牌：优先打字牌和幺九孤张，保留搭子和面子
 */
class BasicAIStrategy : AIStrategy {

    override val name = "基础AI"

    private val decomposer = HandDecomposer.INSTANCE

    override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
        if (legalActions.isEmpty()) return null

        // 1. 和了优先（荣和 / 自摸）
        val ronAction = legalActions.firstOrNull { it is GameAction.DeclareRon }
        if (ronAction != null) return ronAction

        val tsumoAction = legalActions.firstOrNull { it is GameAction.DeclareTsumo }
        if (tsumoAction != null) return tsumoAction

        // 2. 九种九牌流局
        val abortiveAction = legalActions.firstOrNull { it is GameAction.DeclareAbortiveDraw }
        if (abortiveAction != null) return abortiveAction

        // 3. 立直宣言判定
        val riichiActions = legalActions.filterIsInstance<GameAction.DeclareRiichi>()
        if (riichiActions.isNotEmpty() && view.scores[view.viewerSeat] >= 1000) {
            // 选择立直打出后待牌数最多的动作
            val bestRiichi = riichiActions.maxByOrNull { action ->
                val remaining = view.myAllClosedTiles.toMutableList()
                remaining.remove(action.discardTile)
                decomposer.calculateWaits(remaining).size
            }
            if (bestRiichi != null) return bestRiichi
        }

        // 4. 杠判定（优先暗杠）
        val kanActions = legalActions.filterIsInstance<GameAction.DeclareKan>()
        if (kanActions.isNotEmpty()) {
            val closedKan = kanActions.firstOrNull { it.kanType == MeldType.CLOSED_KAN }
            if (closedKan != null) return closedKan
        }

        // 5. 碰役牌判定
        val ponActions = legalActions.filterIsInstance<GameAction.DeclarePon>()
        if (ponActions.isNotEmpty()) {
            val lastDiscard = view.lastDiscard
            if (lastDiscard != null && isYakuhai(lastDiscard, view)) {
                return ponActions.first()
            }
        }

        // 6. 响应窗口中的 Pass
        val passAction = legalActions.firstOrNull { it is GameAction.Pass }
        val hasMeldOrWinChoice = legalActions.any {
            it is GameAction.DeclareChi || it is GameAction.DeclarePon ||
                    it is GameAction.DeclareKan || it is GameAction.DeclareRon
        }
        if (passAction != null && hasMeldOrWinChoice) {
            return passAction
        }

        // 7. 出牌决策
        val discardActions = legalActions.filterIsInstance<GameAction.DiscardTile>()
        if (discardActions.isNotEmpty()) {
            // 如果已立直，且摸到的牌在合法出牌列表中，则摸切
            if (view.riichiStatus[view.viewerSeat] && view.myDrawnTile != null) {
                val tsumogiri = discardActions.firstOrNull { it.tile == view.myDrawnTile }
                if (tsumogiri != null) return tsumogiri
            }

            // 评估每张可打出牌的"不需要度"，打出最不需要的
            val allClosed = view.myAllClosedTiles
            val mySeatWind = view.mySeatWind
            val roundWind = view.roundWind

            val bestDiscard = discardActions.maxByOrNull { action ->
                tileDisposability(action.tile, allClosed, mySeatWind, roundWind)
            }
            if (bestDiscard != null) return bestDiscard
        }

        // 默认兜底：若有 Pass 则 Pass，否则取第一个动作
        return passAction ?: legalActions.first()
    }

    private fun isYakuhai(tile: Tile, view: PlayerView): Boolean {
        if (tile.suit == Suit.DRAGON) return true
        if (tile.suit == Suit.WIND) {
            val seatWind = view.mySeatWind
            if (tile.rank == seatWind || tile.rank == view.roundWind) return true
        }
        return false
    }

    /**
     * 评估一张牌的"可丢弃度"（值越高越应该打出）
     */
    private fun tileDisposability(
        tile: Tile,
        allClosed: List<Tile>,
        seatWind: Int,
        roundWind: Int
    ): Int {
        var score = 0
        val sameTypeCount = allClosed.count { it.sameType(tile) }

        // 孤张字牌优先打出
        if (tile.isHonor && sameTypeCount == 1) {
            val isYakuhai = tile.suit == Suit.DRAGON ||
                    (tile.suit == Suit.WIND && (tile.rank == seatWind || tile.rank == roundWind))
            score += if (isYakuhai) 5 else 15
        }

        // 幺九孤张
        if (tile.isTerminal && sameTypeCount == 1) score += 10

        // 对子/刻子不打
        if (sameTypeCount >= 2) score -= 20
        if (sameTypeCount >= 3) score -= 40

        // 有搭子的牌不打
        if (tile.isNumberTile) {
            val hasNeighbor = allClosed.any {
                it.suit == tile.suit && it != tile &&
                        kotlin.math.abs(it.rank - tile.rank) <= 2
            }
            if (hasNeighbor) score -= 10
        }

        // 边张牌（1, 2, 8, 9）效率较低
        if (tile.isNumberTile && tile.rank in setOf(1, 2, 8, 9)) score += 3

        return score
    }
}

/**
 * 高级 AI 策略 — 基于向听数和期望值（预留接口）
 */
class AdvancedAIStrategy(
    private val delegate: AIStrategy = BasicAIStrategy()
) : AIStrategy {
    override val name = "高级AI"
    override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? =
        delegate.decideAction(view, legalActions)
}

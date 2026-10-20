package com.majiang.ai

import com.majiang.model.Meld
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import kotlin.math.min

/**
 * 三类简单目标感知 AI 策略（仅用于可复现实验，非产品最优打法）。
 *
 * 共同契约（DESIGN.md §5）：
 * - 输入仅 [PlayerView] 与 [legalActions]，只读本人可见暗牌、公开副露/牌河/指示牌及合法动作；
 * - 决策绝不读取 GameState、完整牌山、对手暗牌或未来摸牌；
 * - 合法自摸/荣和优先接受；否则按目标评分选合法动作；无目标可行候选时回退到 [BasicAIStrategy] 合法动作（先滤除本目标禁止的动作）；
 * - 任何返回动作都属于 [legalActions]；无动作返回 null；确定性（无 RNG）。
 */
object TargetAwareAi {

    /** 万/索/筒三色清一色 */
    val KOKUSHI_INDICES: Set<Int> = setOf(0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33)
    val DAISANGEN_INDICES: Set<Int> = setOf(31, 32, 33)
    val DAISUUSHII_INDICES: Set<Int> = setOf(27, 28, 29, 30)

    private fun removeTile(hand: List<Tile>, tile: Tile): List<Tile> {
        val out = ArrayList<Tile>(hand)
        val idx = out.indexOfFirst { it.tileId == tile.tileId }
        if (idx >= 0) out.removeAt(idx)
        return out
    }

    private fun ownMeldTiles(view: PlayerView): List<Tile> =
        view.melds.getOrNull(view.viewerSeat)?.flatMap { it.tiles } ?: emptyList()

    /** 本人控制的全部牌（暗牌含摸牌 + 本人公开副露） */
    private fun baseTiles(view: PlayerView): List<Tile> = view.myAllClosedTiles + ownMeldTiles(view)

    /**
     * 计算吃/碰后的可见牌集合：从暗手移除实际参与副露的牌，再加入完整副露。
     * 仅用于目标评分，不修改任何权威状态。
     */
    private fun afterChi(base: List<Tile>, meld: Meld): List<Tile> {
        val remaining = ArrayList(base)
        for (tile in meld.tiles) {
            if (tile.tileId == meld.calledTile.tileId) continue
            val index = remaining.indexOfFirst { it.tileId == tile.tileId }
            if (index >= 0) remaining.removeAt(index)
        }
        return remaining + meld.tiles
    }

    private fun afterPon(base: List<Tile>, pon: GameAction.DeclarePon, called: Tile): List<Tile> {
        val remaining = ArrayList(base)
        for (tile in pon.tiles) {
            val index = remaining.indexOfFirst { it.tileId == tile.tileId }
            if (index >= 0) remaining.removeAt(index)
        }
        return remaining + pon.tiles + called
    }

    // ---- 评分 ----

    private fun suitScore(tiles: List<Tile>, suit: Suit): Int {
        val ranks = IntArray(10)
        var targetCount = 0
        for (t in tiles) {
            if (t.suit == suit) {
                ranks[t.rank]++
                targetCount++
            }
        }
        var mentsu = 0
        for (r in 1..9) if (ranks[r] >= 3) mentsu++
        for (r in 1..7) if (ranks[r] >= 1 && ranks[r + 1] >= 1 && ranks[r + 2] >= 1) mentsu++
        var taatsu = 0
        for (r in 1..8) if (ranks[r] >= 1 && ranks[r + 1] >= 1) taatsu++
        for (r in 1..7) if (ranks[r] >= 1 && ranks[r + 2] >= 1) taatsu++
        return 100 * targetCount + 12 * mentsu + 4 * taatsu
    }

    private fun numberMentsuCount(tiles: List<Tile>): Int {
        val man = IntArray(10); val sou = IntArray(10); val pin = IntArray(10)
        for (t in tiles) {
            when (t.suit) {
                Suit.MAN -> man[t.rank]++
                Suit.SOU -> sou[t.rank]++
                Suit.PIN -> pin[t.rank]++
                else -> {}
            }
        }
        var mentsu = 0
        for (ranks in arrayOf(man, sou, pin)) {
            for (r in 1..9) if (ranks[r] >= 3) mentsu++
            for (r in 1..7) if (ranks[r] >= 1 && ranks[r + 1] >= 1 && ranks[r + 2] >= 1) mentsu++
        }
        return mentsu
    }

    private fun honorScore(tiles: List<Tile>, requiredHonors: Set<Int>): Int {
        val counts = IntArray(34)
        for (t in tiles) counts[t.typeIndex]++
        var completeKinds = 0
        var sumMin3 = 0
        for (h in requiredHonors) {
            val c = counts[h]
            if (c >= 3) completeKinds++
            sumMin3 += min(c, 3)
        }
        return 100 * completeKinds + 15 * sumMin3 + 3 * numberMentsuCount(tiles)
    }

    private fun kokushiScore(tiles: List<Tile>): Int {
        val counts = IntArray(34)
        for (t in tiles) counts[t.typeIndex]++
        var distinct = 0
        var totalExtra = 0
        var hasPair = false
        for (i in KOKUSHI_INDICES) {
            val c = counts[i]
            if (c >= 1) distinct++
            if (c >= 2) hasPair = true
            if (c > 1) totalExtra += (c - 1)
        }
        val extraCopies = maxOf(0, totalExtra - 1)
        return 100 * distinct + (if (hasPair) 15 else 0) - 20 * extraCopies
    }

    private fun winOrNull(legalActions: List<GameAction>): GameAction? {
        legalActions.firstOrNull { it is GameAction.DeclareRon }?.let { return it }
        legalActions.firstOrNull { it is GameAction.DeclareTsumo }?.let { return it }
        return null
    }

    /** 已立直且刚摸牌时遵守合法摸切。 */
    private fun riichiTsumogiri(view: PlayerView, legalActions: List<GameAction>): GameAction? {
        if (!view.riichiStatus.getOrElse(view.viewerSeat) { false }) return null
        val drawn = view.myDrawnTile ?: return null
        return legalActions.firstOrNull { it is GameAction.DiscardTile && it.tile.tileId == drawn.tileId }
    }

    /**
     * 万/索/筒清一色：优先保留目标花色，从合法打牌中先打非目标花色；仅吃/碰目标花色。
     */
    class SuitGoalAi(val targetSuit: Suit) : AIStrategy {
        override val name: String = "清一色AI(${targetSuit.name})"

        private fun score(tiles: List<Tile>): Int = suitScore(tiles, targetSuit)

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            if (legalActions.isEmpty()) return null
            winOrNull(legalActions)?.let { return it }
            riichiTsumogiri(view, legalActions)?.let { return it }

            val base = baseTiles(view)
            val before = score(base)

            // 仅碰目标花色，且鸣牌后目标形状严格改善。
            val called = view.lastDiscard
            val pons = legalActions.filterIsInstance<GameAction.DeclarePon>()
                .filter { called != null && called.suit == targetSuit }
                .filter { called != null && score(afterPon(base, it, called)) > before }
            if (pons.isNotEmpty()) {
                return pons.maxWithOrNull(
                    compareBy<GameAction.DeclarePon> { called?.let { c -> score(afterPon(base, it, c)) } ?: before }
                        .thenBy { it.tiles.firstOrNull()?.typeIndex ?: 0 }
                        .thenBy { it.tiles.firstOrNull()?.copyIndex ?: 0 }
                )
            }

            // 仅吃目标花色，且鸣牌后目标形状严格改善。
            val chis = legalActions.filterIsInstance<GameAction.DeclareChi>()
                .filter { it.meld.tiles.all { t -> t.suit == targetSuit } }
                .filter { score(afterChi(base, it.meld)) > before }
            if (chis.isNotEmpty()) {
                return chis.maxWithOrNull(
                    compareBy<GameAction.DeclareChi> { score(afterChi(base, it.meld)) }
                        .thenBy { it.meld.calledTile.typeIndex }
                        .thenBy { it.meld.calledTile.copyIndex }
                )
            }

            val discards = legalActions.filterIsInstance<GameAction.DiscardTile>()
            if (discards.isNotEmpty()) {
                return discards.maxWithOrNull(
                    compareBy<GameAction.DiscardTile> { score(removeTile(base, it.tile)) }
                        .thenBy { it.tile.typeIndex }
                        .thenBy { it.tile.copyIndex }
                )
            }

            return fallback(view, legalActions) { action ->
                when (action) {
                    is GameAction.DeclareChi ->
                        action.meld.tiles.any { it.suit != targetSuit } ||
                            score(afterChi(base, action.meld)) <= before
                    is GameAction.DeclarePon ->
                        called == null || called.suit != targetSuit ||
                            score(afterPon(base, action, called)) <= before
                    is GameAction.DeclareKan -> true
                    else -> false
                }
            }
        }
    }

    /**
     * 大三元/大四喜：优先保留并碰齐缺口目标字牌，剩余空间用普通面子与雀头效率。
     *
     * @param requiredHonors 目标字牌 typeIndex 集合（27..33 内）
     */
    class HonorTripletGoalAi(val requiredHonors: Set<Int>) : AIStrategy {
        override val name: String = "字牌刻子AI($requiredHonors)"

        init {
            require(requiredHonors.all { it in 27..33 }) { "requiredHonors must be in 27..33, got $requiredHonors" }
            require(requiredHonors.isNotEmpty()) { "requiredHonors cannot be empty" }
        }

        private fun score(tiles: List<Tile>): Int = honorScore(tiles, requiredHonors)

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            if (legalActions.isEmpty()) return null
            winOrNull(legalActions)?.let { return it }
            riichiTsumogiri(view, legalActions)?.let { return it }

            val base = baseTiles(view)

            // 仅碰缺口目标字牌
            val pons = legalActions.filterIsInstance<GameAction.DeclarePon>()
                .filter { view.lastDiscard?.typeIndex in requiredHonors }
                .filter {
                    val called = view.lastDiscard ?: return@filter false
                    val count = base.count { it.typeIndex == called.typeIndex }
                    count < 3
                }
            if (pons.isNotEmpty()) {
                return pons.maxWithOrNull(compareBy<GameAction.DeclarePon> {
                        score(base + listOfNotNull(view.lastDiscard))
                    }
                    .thenBy { it.tiles.firstOrNull()?.typeIndex ?: 0 }
                    .thenBy { it.tiles.firstOrNull()?.copyIndex ?: 0 })
            }

            val discards = legalActions.filterIsInstance<GameAction.DiscardTile>()
            if (discards.isNotEmpty()) {
                return discards.maxWithOrNull(
                    compareBy<GameAction.DiscardTile> { score(removeTile(base, it.tile)) }
                        .thenBy { it.tile.typeIndex }
                        .thenBy { it.tile.copyIndex }
                )
            }

            return fallback(view, legalActions) { action ->
                when (action) {
                    is GameAction.DeclareChi -> true
                    is GameAction.DeclarePon -> view.lastDiscard?.typeIndex !in requiredHonors
                    is GameAction.DeclareKan -> true
                    else -> false
                }
            }
        }
    }

    /**
     * 国士无双：优先收齐 13 种幺九再留一对；先打非幺九；跳过九种九牌流局；禁止吃碰杠。
     */
    object KokushiGoalAi : AIStrategy {
        override val name: String = "国士AI"

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            if (legalActions.isEmpty()) return null
            winOrNull(legalActions)?.let { return it }
            riichiTsumogiri(view, legalActions)?.let { return it }

            val discards = legalActions.filterIsInstance<GameAction.DiscardTile>()
            if (discards.isNotEmpty()) {
                return discards.maxWithOrNull(
                    compareBy<GameAction.DiscardTile> { kokushiScore(removeTile(view.myAllClosedTiles, it.tile)) }
                        .thenBy { it.tile.typeIndex }
                        .thenBy { it.tile.copyIndex }
                )
            }

            return fallback(view, legalActions) { action ->
                when (action) {
                    is GameAction.DeclareAbortiveDraw -> true
                    is GameAction.DeclareChi -> true
                    is GameAction.DeclarePon -> true
                    is GameAction.DeclareKan -> true
                    else -> false
                }
            }
        }
    }

    private fun fallback(
        view: PlayerView,
        legalActions: List<GameAction>,
        forbidden: (GameAction) -> Boolean
    ): GameAction? {
        val base = BasicAIStrategy().decideAction(view, legalActions)
        if (base != null && !forbidden(base)) return base
        return legalActions.firstOrNull { !forbidden(it) }
    }
}

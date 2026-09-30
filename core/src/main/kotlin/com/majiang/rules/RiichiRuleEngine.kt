package com.majiang.rules

import com.majiang.model.*
import com.majiang.state.GamePhase
import com.majiang.state.GameState

/**
 * 日麻规则引擎 — 纯函数式判定，不修改任何状态
 *
 * 职责：
 * - 判定当前手牌可执行的合法动作集合
 * - 听牌检测与待牌计算
 * - 吃/碰/杠合法性判定
 * - 立直条件判定
 * - 振听检查
 * - 流局判定
 */
class RiichiRuleEngine {

    private val decomposer = HandDecomposer.INSTANCE
    private val defaultYakuEvaluator = YakuEvaluator()

    /** 合法动作 */
    sealed class LegalAction {
        data class Discard(val tile: Tile) : LegalAction()
        data class Chi(val meld: Meld) : LegalAction()
        data class Pon(val tiles: List<Tile>, val fromPlayer: Int) : LegalAction()
        data class Kan(val tiles: List<Tile>, val kanType: MeldType) : LegalAction()
        data class Riichi(val discardTile: Tile) : LegalAction()
        object Tsumo : LegalAction()
        object Ron : LegalAction()
        data class AbortiveDraw(val reason: AbortiveDrawType) : LegalAction()
        object Pass : LegalAction()
    }

    /** 流局类型 */
    enum class AbortiveDrawType {
        EXHAUSTIVE,       // 荒牌（牌山摸完）
        FOUR_WINDS,       // 四风连打
        FOUR_KANS,        // 四杠散了
        FOUR_RIICHI,      // 四家立直
        NINE_TERMINALS    // 九种九牌
    }

    // ========================
    // 和了判定
    // ========================

    /**
     * 判定手牌是否和了（完成形）
     * @param closedTiles 门前手牌（含最后摸到/荣和的牌，共14张减去副露×3）
     */
    fun isWinningHand(closedTiles: List<Tile>): Boolean {
        return decomposer.canWin(closedTiles)
    }

    /**
     * 判定手牌是否听牌，返回所有待牌的 typeIndex
     */
    fun getTenpaiWaits(closedTiles: List<Tile>): Set<Int> {
        return decomposer.calculateWaits(closedTiles)
    }

    /**
     * 判定手牌是否听牌
     */
    fun isTenpai(closedTiles: List<Tile>): Boolean {
        return getTenpaiWaits(closedTiles).isNotEmpty()
    }

    /**
     * 构造权威一致的 WinContext (INTEGRATION.md item 6)
     */
    fun buildWinContext(
        state: GameState,
        player: Int,
        winTile: Tile,
        isTsumo: Boolean,
        isChankan: Boolean = false
    ): YakuEvaluator.WinContext {
        val hand = state.players[player]
        val meldOccurred = state.players.any { it.melds.isNotEmpty() } || state.totalKanCount > 0
        val isFirstUninterrupted = state.isFirstUninterruptedGo && !meldOccurred && hand.discards.isEmpty()

        return YakuEvaluator.WinContext(
            winTile = winTile,
            isTsumo = isTsumo,
            isClosed = hand.isClosed,
            seatWind = state.seatWind(player),
            roundWind = state.roundWind,
            isRiichi = state.riichiDeclared[player],
            isDoubleRiichi = state.doubleRiichiDeclared[player],
            isIppatsu = state.ippatsuActive[player],
            isTenhou = isTsumo && player == state.dealerIndex && isFirstUninterrupted && state.turnNumber <= 1,
            isChiihou = isTsumo && player != state.dealerIndex && isFirstUninterrupted,
            isRinshan = isTsumo && (state.phase == GamePhase.KAN_DRAW || state.isRinshanDraw),
            isHaitei = isTsumo && !state.isRinshanDraw && state.wallRemaining == 0,
            isHoutei = !isTsumo && state.wallRemaining == 0,
            isChankan = !isTsumo && (isChankan || state.phase == GamePhase.CHANKAN_WINDOW)
        )
    }

    /**
     * 判定手牌是否至少有 1 翻（或役满）。
     * 用于荣和 / 自摸拦截，禁止无役和了。
     */
    fun hasAtLeastOneYaku(
        hand: PlayerHand,
        winTile: Tile,
        isTsumo: Boolean,
        state: GameState,
        player: Int,
        evaluator: YakuEvaluator = defaultYakuEvaluator
    ): Boolean {
        val evalHand = if (isTsumo) {
            if (hand.drawnTile == null && hand.closedTiles.size % 3 == 1) {
                val temp = PlayerHand()
                temp.closedTiles.addAll(hand.closedTiles)
                temp.drawnTile = winTile
                temp.melds.addAll(hand.melds)
                temp
            } else {
                hand
            }
        } else {
            val temp = PlayerHand()
            temp.closedTiles.addAll(hand.closedTiles)
            temp.drawnTile = winTile
            temp.melds.addAll(hand.melds)
            temp
        }

        val context = buildWinContext(state, player, winTile, isTsumo)
        val result = evaluator.evaluate(evalHand, context)
        return result.isYakuman || result.totalHan > 0
    }

    // ========================
    // 吃碰杠判定
    // ========================

    /**
     * 判定是否可吃（仅上家的牌，且必须是数牌）
     * @return 所有可能的吃法（可能有多种组合）
     */
    fun canChi(hand: PlayerHand, discardTile: Tile, fromPlayer: Int, currentPlayer: Int): List<Meld> {
        // 只能吃上家
        if ((fromPlayer + 1) % 4 != currentPlayer) return emptyList()
        // 字牌不能吃
        if (!discardTile.isNumberTile) return emptyList()

        val results = mutableListOf<Meld>()
        val closed = hand.closedTiles
        val rank = discardTile.rank
        val suit = discardTile.suit

        // 三种吃法：
        // 1. discardTile 在最左边：discard, +1, +2
        if (rank <= 7) {
            val t1 = closed.find { it.suit == suit && it.rank == rank + 1 }
            val t2 = closed.find { it.suit == suit && it.rank == rank + 2 && it != t1 }
            if (t1 != null && t2 != null) {
                results.add(Meld(
                    type = MeldType.CHI,
                    tiles = listOf(discardTile, t1, t2).sortedBy { it.rank },
                    fromPlayer = fromPlayer,
                    calledTile = discardTile
                ))
            }
        }

        // 2. discardTile 在中间：-1, discard, +1
        if (rank >= 2 && rank <= 8) {
            val t1 = closed.find { it.suit == suit && it.rank == rank - 1 }
            val t2 = closed.find { it.suit == suit && it.rank == rank + 1 && it != t1 }
            if (t1 != null && t2 != null) {
                results.add(Meld(
                    type = MeldType.CHI,
                    tiles = listOf(t1, discardTile, t2).sortedBy { it.rank },
                    fromPlayer = fromPlayer,
                    calledTile = discardTile
                ))
            }
        }

        // 3. discardTile 在最右边：-2, -1, discard
        if (rank >= 3) {
            val t1 = closed.find { it.suit == suit && it.rank == rank - 2 }
            val t2 = closed.find { it.suit == suit && it.rank == rank - 1 && it != t1 }
            if (t1 != null && t2 != null) {
                results.add(Meld(
                    type = MeldType.CHI,
                    tiles = listOf(t1, t2, discardTile).sortedBy { it.rank },
                    fromPlayer = fromPlayer,
                    calledTile = discardTile
                ))
            }
        }

        return results
    }

    /**
     * 判定是否可碰
     */
    fun canPon(hand: PlayerHand, discardTile: Tile): Boolean {
        return hand.closedTiles.count { it.sameType(discardTile) } >= 2
    }

    /**
     * 获取碰牌所需的手牌
     */
    fun getPonTiles(hand: PlayerHand, discardTile: Tile): List<Tile>? {
        val matching = hand.closedTiles.filter { it.sameType(discardTile) }
        return if (matching.size >= 2) matching.take(2) else null
    }

    /**
     * 判定所有可能的杠操作
     */
    data class KanOption(
        val tiles: List<Tile>,
        val kanType: MeldType
    )

    fun getKanOptions(hand: PlayerHand, drawnTile: Tile?): List<KanOption> {
        val options = mutableListOf<KanOption>()
        val allClosed = hand.closedTiles.toMutableList()
        if (drawnTile != null) allClosed.add(drawnTile)

        // 暗杠：手中 4 张同种牌
        val grouped = allClosed.groupBy { Pair(it.suit, it.rank) }
        for ((_, tiles) in grouped) {
            if (tiles.size == 4) {
                options.add(KanOption(tiles, MeldType.CLOSED_KAN))
            }
        }

        // 加杠：已碰的面子 + 手中第 4 张
        for (meld in hand.melds) {
            if (meld.type == MeldType.PON) {
                val matchInHand = allClosed.find { it.sameType(meld.tiles.first()) }
                if (matchInHand != null) {
                    options.add(KanOption(meld.tiles + matchInHand, MeldType.ADDED_KAN))
                }
            }
        }

        return options
    }

    /**
     * 判定是否可大明杠（别人打出的牌 + 手中 3 张）
     */
    fun canOpenKan(hand: PlayerHand, discardTile: Tile): Boolean {
        return hand.closedTiles.count { it.sameType(discardTile) } >= 3
    }

    // ========================
    // 立直判定
    // ========================

    /**
     * 判定是否可立直
     * @return 可立直时打出的牌列表（打出后仍为听牌状态的所有精确物理牌）
     */
    fun canRiichi(hand: PlayerHand, state: GameState, player: Int): List<Tile> {
        if (player !in 0..3) return emptyList()

        // 条件 1：门前清
        if (!hand.isClosed) return emptyList()

        // 条件 2：未立直
        if (state.riichiDeclared[player]) return emptyList()

        // 条件 3：点数 >= 1000（需要支付 1000 点立直棒）
        if (state.scores[player] < 1000) return emptyList()

        // 条件 4：牌山剩余 >= 4 张（需要至少能再摸一圈）
        if (state.wallRemaining < 4) return emptyList()

        // 条件 5：打出某张牌后能听牌
        val allClosed = hand.allClosedTiles
        if (allClosed.isEmpty()) return emptyList()

        // 统计自己已有副露中的各牌种张数（排除第 5 张牌候选）
        val meldCounts = IntArray(34)
        for (meld in hand.melds) {
            for (tile in meld.tiles) {
                meldCounts[tile.typeIndex]++
            }
        }

        // 按牌种缓存待牌判定结果，避免同种牌重复运算
        val typeTenpaiCache = mutableMapOf<Int, Boolean>()
        val riichiDiscards = mutableListOf<Tile>()

        for (tile in allClosed) {
            val canRiichiForType = typeTenpaiCache.getOrPut(tile.typeIndex) {
                val remaining = allClosed.toMutableList()
                val removeIndex = remaining.indexOfFirst { it.typeIndex == tile.typeIndex }
                if (removeIndex != -1) {
                    remaining.removeAt(removeIndex)
                }

                val closedCounts = IntArray(34)
                for (t in remaining) {
                    closedCounts[t.typeIndex]++
                }

                // 用 HandDecomposer 求待牌，排除剩余暗牌 + 自身副露已占满 4 张的第 5 张候选
                // （不因牌河已见而拒绝空听）
                val rawWaits = decomposer.calculateWaits(remaining)
                rawWaits.any { waitType ->
                    closedCounts[waitType] + meldCounts[waitType] < 4
                }
            }

            if (canRiichiForType) {
                riichiDiscards.add(tile)
            }
        }

        return riichiDiscards
    }


    // ========================
    // 振听判定
    // ========================

    /**
     * 检查振听状态
     *
     * 三种振听（DESIGN.md 4.2 & INTEGRATION.md item 6）：
     * 1. 舍张振听：历史弃牌（含被叫走的牌）中有待牌
     * 2. 同巡振听：本巡内放弃过荣和
     * 3. 立直振听：立直后放弃过任何荣和机会
     */
    fun isFuriten(
        hand: PlayerHand,
        closedTiles: List<Tile>,
        isSameTurnFuriten: Boolean = false,
        isRiichiFuriten: Boolean = false
    ): Boolean {
        return FuritenRules.status(
            hand = hand,
            closedTiles = closedTiles,
            temporary = isSameTurnFuriten,
            riichi = isRiichiFuriten
        ).isFuriten
    }

    // ========================
    // 流局判定
    // ========================

    /**
     * 检查是否需要流局
     */
    fun checkAbortiveDraw(state: GameState): AbortiveDrawType? {
        // 荒牌：牌山摸完
        if (state.wallRemaining <= 0) return AbortiveDrawType.EXHAUSTIVE

        // 四风连打：第一巡所有玩家打出同一种风牌
        if (checkFourWinds(state)) return AbortiveDrawType.FOUR_WINDS

        // 四杠散了：场上总共宣告了 4 次杠（且不是同一人）
        if (checkFourKans(state)) return AbortiveDrawType.FOUR_KANS

        // 四家立直
        if (state.riichiDeclared.count { it } == 4) return AbortiveDrawType.FOUR_RIICHI

        return null
    }

    /**
     * 检查九种九牌（玩家可选择宣告流局）
     * 条件：第一巡、门前、手牌中有 9 种以上的幺九牌
     */
    fun canDeclareNineTerminals(hand: PlayerHand, state: GameState, player: Int): Boolean {
        if (!state.isFirstUninterruptedGo) return false
        if (!hand.isClosed) return false
        if (hand.discards.isNotEmpty()) return false

        val allTiles = hand.allClosedTiles
        val terminalHonorTypes = allTiles.filter { it.isTerminalOrHonor }
            .map { it.typeIndex }
            .toSet()

        return terminalHonorTypes.size >= 9
    }

    private fun checkFourWinds(state: GameState): Boolean {
        if (!state.isFirstUninterruptedGo) return false

        // 每位玩家必须恰好打了一张牌
        val allDiscards = state.players.map { it.discards }
        if (allDiscards.any { it.size != 1 }) return false

        // 四张牌都是同一种风牌
        val firstTile = allDiscards[0][0]
        if (!firstTile.isHonor || firstTile.suit != Suit.WIND) return false
        return allDiscards.all { it[0].sameType(firstTile) }
    }

    private fun checkFourKans(state: GameState): Boolean {
        if (state.totalKanCount < 4) return false
        // 同一人的 4 杠不算四杠散了（那是四杠子役满）
        return state.playerKanCounts.count { it > 0 } >= 2
    }

    // ========================
    // 响应窗口优先级
    // ========================

    /**
     * 在响应窗口中，确定优先级最高的动作
     * 优先级：荣和 > 杠 > 碰 > 吃
     */
    enum class ResponsePriority(val level: Int) {
        RON(4),
        KAN(3),
        PON(2),
        CHI(1),
        PASS(0)
    }

    companion object {
        val INSTANCE = RiichiRuleEngine()
    }
}


package com.majiang.state

import com.majiang.model.MeldType
import com.majiang.model.Tile
import com.majiang.rules.RiichiKanValidator
import com.majiang.rules.RiichiRuleEngine
import com.majiang.rules.WinningScoreEvaluator

/**
 * 统一合法动作生成器 (DESIGN.md 4.1)
 *
 * 保证校验与展示使用完全相同的规则源：
 * - 规则包含阶段、座位、当前玩家、精确 copyIndex 所有权、和牌形与有役、三项振听、立直条件、牌山容量与副露组合
 * - 严禁返回非法动作或为他家生成动作
 * - 响应窗口下仅返回属于该座位的待决响应动作
 */
object LegalActionProvider {

    /**
     * 为指定座位的玩家生成当前全部合法的操作动作列表
     */
    fun legalActions(
        state: GameState,
        player: Int,
        responseWindow: ResponseWindow? = null,
        ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
        winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
        canDealerDeclareAgariYame: Boolean = false
    ): List<GameAction> {
        if (player !in 0..3) return emptyList()

        return when (state.phase) {
            GamePhase.MATCH_END -> emptyList()

            GamePhase.ROUND_END -> {
                val actions = mutableListOf<GameAction>()
                if (player == state.dealerIndex && canDealerDeclareAgariYame) {
                    actions.add(GameAction.DeclareAgariYame(player))
                }
                actions
            }

            GamePhase.STACKING, GamePhase.DEALING -> emptyList()

            GamePhase.RESPONSE_WINDOW, GamePhase.CHANKAN_WINDOW -> {
                val window = responseWindow ?: return emptyList()
                if (window.fromPlayer == player || window.hasResponded(player)) {
                    return emptyList()
                }
                window.eligibleActions[player] ?: emptyList()
            }

            GamePhase.DRAW -> {
                if (player != state.currentPlayer) return emptyList()
                val hand = state.players[player]
                if (hand.drawnTile == null && state.availableWallTileCountFor(player) > 0) {
                    listOf(GameAction.DrawTile(player))
                } else {
                    emptyList()
                }
            }

            GamePhase.CHEAT_WINDOW, GamePhase.DISCARD -> {
                if (player != state.currentPlayer) return emptyList()
                val hand = state.players[player]
                val actions = mutableListOf<GameAction>()

                val allClosed = hand.allClosedTiles
                val currentDrawn = hand.drawnTile

                // 1. 自摸判定（必须有真实摸牌且完成形与有合法役种）
                if (currentDrawn != null) {
                    val winTile = currentDrawn
                    val context = ruleEngine.buildWinContext(state, player, winTile, isTsumo = true)
                    val candidate = winningScoreEvaluator.evaluate(
                        hand = hand,
                        context = context,
                        doraIndicators = state.doraIndicators,
                        uraDoraIndicators = state.uraDoraIndicators,
                        isDealer = (player == state.dealerIndex)
                    )
                    if (candidate != null) {
                        actions.add(GameAction.DeclareTsumo(player))
                    }
                }

                // 2. 九种九牌流局判定
                if (ruleEngine.canDeclareNineTerminals(hand, state, player)) {
                    actions.add(GameAction.DeclareAbortiveDraw(player))
                }

                // 3. 杠判定（暗杠与加杠）
                // 规范要求（WRC 2022 §8.9.1 & INTEGRATION.md item 5 & Review Item 5）：
                // - 仅在活牌山有可用替补牌且岭上槽位未耗尽且总杠次数未达4次
                //   (availableWallTileCount > 0 && rinshanPointer < 4 && totalKanCount < 4) 时提供杠
                // - 未立直玩家全量开放（暗杠与加杠）
                // - 立直后仅允许暗杠（CLOSED_KAN），且需通过 RiichiKanValidator 严格校验（摸牌参与且待牌/结构不变）
                if (state.availableWallTileCount() > 0 && state.rinshanPointer < 4 && state.totalKanCount < 4) {
                    if (!state.riichiDeclared[player]) {
                        val kanOptions = ruleEngine.getKanOptions(hand, currentDrawn)
                        for (opt in kanOptions) {
                            actions.add(GameAction.DeclareKan(player, opt.tiles, opt.kanType))
                        }
                    } else if (currentDrawn != null && hand.isClosed) {
                        val matchingClosed = hand.closedTiles.filter { it.sameType(currentDrawn) }
                        if (matchingClosed.size == 3) {
                            val kanTiles = matchingClosed + currentDrawn
                            if (RiichiKanValidator.canDeclare(hand, kanTiles)) {
                                actions.add(GameAction.DeclareKan(player, kanTiles, MeldType.CLOSED_KAN))
                            }
                        }
                    }
                }

                // 4. 立直宣言判定（门前清、未立直、点数>=1000、牌山>=4张、打出某张牌后听牌）
                if (!state.riichiDeclared[player] && hand.isClosed && state.scores[player] >= 1000 && state.availableWallTileCount() >= 4) {
                    val riichiDiscards = ruleEngine.canRiichi(hand, state, player)
                    for (tile in riichiDiscards) {
                        actions.add(GameAction.DeclareRiichi(player, tile))
                    }
                }

                // 5. 出牌判定
                if (state.riichiDeclared[player]) {
                    // 已立直玩家固定只能摸切刚摸到的牌
                    if (currentDrawn != null) {
                        actions.add(GameAction.DiscardTile(player, currentDrawn))
                    }
                } else {
                    // 未立直玩家：可打出手牌中的任意牌
                    val allTiles = buildList {
                        addAll(hand.closedTiles)
                        if (currentDrawn != null) add(currentDrawn)
                    }
                    val seen = mutableSetOf<Tile>()
                    for (tile in allTiles) {
                        if (seen.add(tile)) {
                            actions.add(GameAction.DiscardTile(player, tile))
                        }
                    }
                }

                actions
            }

            GamePhase.KAN_DRAW -> emptyList()
        }
    }

    /**
     * 构建响应窗口中各座位的候选合法响应动作集合
     */
    fun buildEligibleResponseActions(
        state: GameState,
        discarder: Int,
        targetTile: Tile,
        isChankan: Boolean,
        ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
        winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE
    ): Map<Int, List<GameAction>> {
        val result = mutableMapOf<Int, List<GameAction>>()

        for (seat in 0 until 4) {
            if (seat == discarder) continue
            val hand = state.players[seat]
            val actions = mutableListOf<GameAction>()

            // 1. 荣和判定（检查听牌形、役种存在、无舍张/同巡/立直振听）
            val isFuriten = ruleEngine.isFuriten(
                hand = hand,
                closedTiles = hand.closedTiles,
                isSameTurnFuriten = state.sameTurnFuriten[seat],
                isRiichiFuriten = state.riichiFuriten[seat]
            )

            if (!isFuriten) {
                val context = ruleEngine.buildWinContext(
                    state = state,
                    player = seat,
                    winTile = targetTile,
                    isTsumo = false,
                    isChankan = isChankan
                )
                val canWin = winningScoreEvaluator.canWinWithYaku(
                    hand = hand,
                    context = context,
                    isDealer = (seat == state.dealerIndex)
                )
                if (canWin) {
                    actions.add(GameAction.DeclareRon(seat))
                }
            }

            // 抢杠响应窗口下禁止吃、碰、大明杠
            if (!isChankan && !state.riichiDeclared[seat]) {
                // 2. 碰判定
                if (ruleEngine.canPon(hand, targetTile)) {
                    val matching = hand.closedTiles.filter { it.sameType(targetTile) }
                    if (matching.size >= 2) {
                        actions.add(GameAction.DeclarePon(seat, matching.take(2)))
                    }
                }

                // 3. 大明杠判定（活牌山有可用替补牌、岭上槽位未耗尽且总杠次数未达4次）
                if (state.availableWallTileCount() > 0 && state.rinshanPointer < 4 && state.totalKanCount < 4 && ruleEngine.canOpenKan(hand, targetTile)) {
                    val matching = hand.closedTiles.filter { it.sameType(targetTile) }
                    if (matching.size >= 3) {
                        actions.add(GameAction.DeclareKan(seat, matching.take(3), MeldType.OPEN_KAN))
                    }
                }

                // 4. 吃判定（仅限上家打牌，即 seat == (discarder + 1) % 4）
                if (seat == (discarder + 1) % 4) {
                    val chiMelds = ruleEngine.canChi(hand, targetTile, discarder, seat)
                    for (meld in chiMelds) {
                        actions.add(GameAction.DeclareChi(seat, meld))
                    }
                }
            }

            // 若该座位有任何鸣牌或荣和机会，必定提供 Pass 选项
            if (actions.isNotEmpty()) {
                actions.add(GameAction.Pass(seat))
                result[seat] = actions
            }
        }

        return result
    }
}

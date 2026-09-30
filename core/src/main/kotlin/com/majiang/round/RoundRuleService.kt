package com.majiang.round

import com.majiang.rules.RiichiRuleEngine
import com.majiang.rules.WinningScoreEvaluator
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.LegalActionProvider
import com.majiang.state.RejectionReason
import com.majiang.state.ResponseWindow

/**
 * 单局独立规则服务
 *
 * 职责：
 * - 纯函数式动作前置校验 (validatePreconditions) 与合法候选生成 (legalActions)；
 * - 规则与算分复用现有规则引擎实现；
 * - 不包含权限、AI、控制者、身份或整场推进判定；
 * - 绝不修改任何外部状态。
 */
class RoundRuleService(
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE
) {
    /**
     * 为指定座位的玩家生成当前全部合法的规则候选操作动作
     *
     * 规则要求：
     * - 已结束的 Round 返回 emptyList；
     * - 剔除 DeclareAgariYame（和止属于 Session 范围）；
     * - 响应窗口下仅返回该座位的待决响应动作。
     */
    fun legalActions(round: RoundStateMachine, seat: Int): List<GameAction> {
        val state = round.state
        if (round.result != null || state.phase == GamePhase.ROUND_END || state.phase == GamePhase.MATCH_END) {
            return emptyList()
        }
        return LegalActionProvider.legalActions(
            state = state,
            player = seat,
            responseWindow = round.responseWindow,
            ruleEngine = ruleEngine,
            winningScoreEvaluator = winningScoreEvaluator,
            canDealerDeclareAgariYame = false
        )
    }

    /**
     * 动作前置预校验（无副作用）
     *
     * 校验规则：
     * - 已结束对局拒绝 WRONG_PHASE；
     * - 拒绝 DeclareAgariYame (UNSUPPORTED_ACTION)；
     * - StartStacking 仅允许尚未发牌的新实例；
     * - 响应窗口动作严格校验资格与候选匹配；
     * - 回合动作严格校验当前行动玩家与牌张所有权。
     */
    fun validatePreconditions(round: RoundStateMachine, action: GameAction): RejectionReason? {
        val state = round.state
        if (round.result != null || state.phase == GamePhase.ROUND_END || state.phase == GamePhase.MATCH_END) {
            return RejectionReason.WRONG_PHASE
        }

        when (action) {
            is GameAction.StartStacking -> {
                if (state.phase != GamePhase.STACKING || state.wall.isNotEmpty()) {
                    return RejectionReason.WRONG_PHASE
                }
                if (action.stackingPlans.isNotEmpty()) {
                    return RejectionReason.UNSUPPORTED_ACTION
                }
                return null
            }

            is GameAction.DealComplete -> {
                return RejectionReason.ACTION_NOT_AVAILABLE
            }

            is GameAction.DeclareAgariYame -> {
                return RejectionReason.UNSUPPORTED_ACTION
            }

            is GameAction.UseCheatTechnique -> {
                return RejectionReason.UNSUPPORTED_ACTION
            }

            else -> {
                val player = ResponseWindow.getActionPlayer(action)
                if (player !in 0..3) return RejectionReason.INVALID_PLAYER

                // 处于响应窗口或抢杠窗口时的校验
                if (state.phase in setOf(GamePhase.RESPONSE_WINDOW, GamePhase.CHANKAN_WINDOW)) {
                    val window = round.responseWindow ?: return RejectionReason.WRONG_PHASE
                    if (player == window.fromPlayer) return RejectionReason.NOT_YOUR_TURN
                    if (window.hasResponded(player)) return RejectionReason.ALREADY_RESPONDED
                    if (!window.eligibleSeats.contains(player)) return RejectionReason.ACTION_NOT_AVAILABLE

                    if (window.findMatchingEligibleAction(player, action) == null) {
                        return RejectionReason.ACTION_NOT_AVAILABLE
                    }
                    return null
                }

                // 非响应窗口下提交响应类动作均拒绝 WRONG_PHASE
                if (action is GameAction.Pass || action is GameAction.DeclareRon || action is GameAction.DeclarePon || action is GameAction.DeclareChi) {
                    return RejectionReason.WRONG_PHASE
                }

                // 回合动作必须属于 currentPlayer
                if (player != state.currentPlayer) return RejectionReason.NOT_YOUR_TURN

                if (action is GameAction.DrawTile) {
                    if (state.phase != GamePhase.DRAW) return RejectionReason.WRONG_PHASE
                    if (state.players[player].drawnTile != null) return RejectionReason.ACTION_NOT_AVAILABLE
                    return null
                }

                if (action is GameAction.DiscardTile) {
                    if (state.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD)) {
                        return RejectionReason.WRONG_PHASE
                    }
                    val hand = state.players[player]
                    if (!hand.closedTiles.contains(action.tile) && hand.drawnTile != action.tile) {
                        return RejectionReason.TILE_NOT_OWNED
                    }
                    if (state.riichiDeclared[player] && hand.drawnTile != null && action.tile != hand.drawnTile) {
                        return RejectionReason.ACTION_NOT_AVAILABLE
                    }
                    return null
                }

                if (state.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD)) {
                    return RejectionReason.WRONG_PHASE
                }

                val allowed = legalActions(round, player)
                val matches = when (action) {
                    is GameAction.DeclareTsumo -> allowed.any { it is GameAction.DeclareTsumo }
                    is GameAction.DeclareAbortiveDraw -> allowed.any { it is GameAction.DeclareAbortiveDraw }
                    is GameAction.DeclareRiichi -> allowed.any {
                        it is GameAction.DeclareRiichi && it.discardTile == action.discardTile
                    }
                    is GameAction.DeclareKan -> allowed.any { allowedKan ->
                        allowedKan is GameAction.DeclareKan &&
                                allowedKan.kanType == action.kanType &&
                                allowedKan.tiles.size == action.tiles.size &&
                                allowedKan.tiles.containsAll(action.tiles) &&
                                action.tiles.containsAll(allowedKan.tiles)
                    }
                    else -> false
                }

                if (!matches) {
                    return when {
                        action is GameAction.DeclareRiichi && !state.players[player].closedTiles.contains(action.discardTile) && state.players[player].drawnTile != action.discardTile ->
                            RejectionReason.TILE_NOT_OWNED
                        else -> RejectionReason.ACTION_NOT_AVAILABLE
                    }
                }
                return null
            }
        }
    }
}

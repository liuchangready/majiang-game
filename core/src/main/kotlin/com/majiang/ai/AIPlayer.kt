package com.majiang.ai

import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import com.majiang.state.GameState

/**
 * AI 玩家控制器（领域侧适配器）
 *
 * 封装 AI 决策逻辑，连接领域状态与 AIStrategy。
 * AIStrategy 仅能接收脱敏后的只读 PlayerView 与合法动作列表，严禁直接访问完整 GameState。
 */
class AIPlayer(
    val playerIndex: Int,
    val strategy: AIStrategy = BasicAIStrategy()
) {
    val strategyName: String get() = strategy.name

    /**
     * 基于 PlayerView 做出决策并返回选中的合法动作
     */
    fun decideAction(view: PlayerView, legalActions: List<GameAction> = view.legalActions): GameAction? {
        require(view.viewerSeat == playerIndex) {
            "PlayerView seat (${view.viewerSeat}) does not match AIPlayer seat ($playerIndex)"
        }
        return strategy.decideAction(view, legalActions)
    }

    /**
     * 领域侧适配入口：从 GameState 投影生成只读 PlayerView 后转交 strategy 决策
     */
    fun decideAction(state: GameState, legalActions: List<GameAction>): GameAction? {
        val view = PlayerView.fromGameState(state, playerIndex, legalActions)
        return strategy.decideAction(view, legalActions)
    }
}

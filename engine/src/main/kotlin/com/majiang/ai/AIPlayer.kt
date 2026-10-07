package com.majiang.ai

/**
 * AI 玩家控制器（领域侧适配器）
 *
 * 作为座位与 AI 策略的具名绑定，供 initializeMatch 等 API 引用。
 * 决策时由 ApplicationCoordinator 直接调用 strategy.decideAction(view, legalActions)，
 * AIStrategy 仅能接收脱敏后的只读 PlayerView 与合法动作列表，严禁直接访问完整 GameState。
 */
class AIPlayer(
    val playerIndex: Int,
    val strategy: AIStrategy = BasicAIStrategy()
)

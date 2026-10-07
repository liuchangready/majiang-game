package com.majiang.round

import com.majiang.rules.RiichiRuleEngine
import com.majiang.rules.WinningScoreEvaluator
import com.majiang.settlement.SettlementService
import com.majiang.state.GameAction
import kotlin.random.Random

/**
 * 单局外部命令处理服务 (DESIGN.md 4.1)
 *
 * 职责：
 * - 动作验证、候选生成、准备本局变更；
 * - 纯单局外部规划与校验服务；
 * - 不包含 AI、控制者、身份、审计和事件总线；
 * - 拒绝 DeclareAgariYame，以及对已结束 Round 的所有操作（包括 StartStacking）；
 * - StartStacking 只允许尚未发牌的新实例；
 * - 隔离候选状态与权威状态，无任何副作用。
 */
class RoundCommandProcessor(
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
    val settlementService: SettlementService = SettlementService.INSTANCE,
    val random: Random = Random.Default
) {
    val ruleService: RoundRuleService = RoundRuleService(ruleEngine, winningScoreEvaluator)
    val planner: RoundTransitionPlanner = RoundTransitionPlanner(ruleEngine, winningScoreEvaluator, settlementService, random)

    /**
     * 生成指定座位当前全部合法的规则候选操作动作
     */
    fun legalActions(round: RoundStateMachine, seat: Int): List<GameAction> {
        return ruleService.legalActions(round, seat)
    }

    /**
     * 校验并准备执行单局动作
     *
     * - 若动作不合法或处于非法阶段，返回 Rejected(reason)，保证零副作用；
     * - 若动作合法，在候选深拷贝上完成该动作及全部机械后续，返回 Ready(change)；
     * - 权威 RoundStateMachine 在外部通过审计并显式调用 install 前完全不发生变更。
     */
    fun prepare(round: RoundStateMachine, action: GameAction): RoundPreparation {
        val rejectionReason = ruleService.validatePreconditions(round, action)
        if (rejectionReason != null) {
            return RoundPreparation.Rejected(rejectionReason)
        }

        val step = planner.plan(round, action)
        val change = round.prepare(step)
        return RoundPreparation.Ready(change)
    }
}

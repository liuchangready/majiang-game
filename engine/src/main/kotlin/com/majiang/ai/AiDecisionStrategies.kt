package com.majiang.ai

import com.majiang.ai.cheat.AiCapabilityIds
import com.majiang.ai.cheat.CheatAiRegistry

/**
 * 普通动作候选工厂。
 *
 * 普通动作与千术共享 [AiCandidate] 契约；普通动作的 `optionId` 直接复用权威决策中的
 * 选项 ID，提交时重新定位到新鲜决策选项，绝不直接执行异步线程持有的动作对象。
 */
object AiCandidateFactory {

    const val MAHJONG_CAPABILITY_ID: String = AiCapabilityIds.MAHJONG

    fun build(snapshot: AiSeatSnapshot, registry: CheatAiRegistry): List<AiCandidate> {
        val result = ArrayList<AiCandidate>()
        if (snapshot.phase == AiPhase.ORDINARY_ACTION) {
            result.addAll(ordinary(snapshot))
        }
        result.addAll(registry.candidates(snapshot, snapshot.limits))

        val seen = LinkedHashSet<String>()
        val unique = ArrayList<AiCandidate>(result.size)
        for (candidate in result) {
            if (seen.add(candidate.optionId)) unique.add(candidate)
        }
        return unique
    }

    fun ordinary(snapshot: AiSeatSnapshot): List<AiCandidate> {
        if (snapshot.phase != AiPhase.ORDINARY_ACTION) return emptyList()
        val result = ArrayList<AiCandidate>(snapshot.decision.options.size)
        for (option in snapshot.decision.options) {
            result.add(
                AiCandidate(
                    optionId = option.optionId,
                    capabilityId = MAHJONG_CAPABILITY_ID,
                    phase = AiPhase.ORDINARY_ACTION,
                    cost = 0,
                    risk = AiRisk(),
                    estimatedEffect = effectOf(option.action),
                    terminalForWindow = terminalOf(option.action),
                    parameters = AiVisibleParams.EMPTY
                )
            )
        }
        return result
    }

    private fun effectOf(action: com.majiang.state.GameAction): AiEffect = when (action) {
        is com.majiang.state.GameAction.DeclareTsumo,
        is com.majiang.state.GameAction.DeclareRon -> AiEffect(handValue = 1000, note = "win")
        is com.majiang.state.GameAction.DeclareRiichi -> AiEffect(handValue = 13, note = "riichi")
        is com.majiang.state.GameAction.DeclareKan -> AiEffect(handValue = 6, note = "kan")
        is com.majiang.state.GameAction.DeclarePon,
        is com.majiang.state.GameAction.DeclareChi -> AiEffect(handValue = 3, note = "meld")
        is com.majiang.state.GameAction.DeclareAbortiveDraw -> AiEffect(handValue = 100, note = "abortive draw")
        else -> AiEffect()
    }

    private fun terminalOf(action: com.majiang.state.GameAction): Boolean = when (action) {
        is com.majiang.state.GameAction.Pass -> false
        is com.majiang.state.GameAction.DeclareKan -> false
        else -> true
    }
}

/**
 * 固定随机种子的确定性微扰。
 *
 * 只影响候选排名，不改变规则、合法性或物理结果；同输入同输出，便于复现测试。
 */
object AiCandidateScorer {
    private const val JITTER_SEED = 0x5EEDL

    fun score(candidate: AiCandidate, limits: AiCandidateLimits): Double {
        val effect = candidate.estimatedEffect
        val costPenalty = candidate.cost * 0.45
        val riskPenalty = candidate.risk.penalty * 10.0 * (1.0 - limits.riskTolerance)
        val jitter = (((candidate.optionId.hashCode().toLong() * 31L + JITTER_SEED) % 7L).toInt() - 3) * 0.25
        return effect.handValue + effect.shantenImprovement * 8.0 + effect.retainedValue -
            costPenalty - riskPenalty + jitter
    }
}

/**
 * 旧 [AIStrategy] 兼容包装器。
 *
 * 普通动作仍由旧策略决定；未显式允许千术时，offer 阶段直接选择安全兜底，
 * 洗牌准备阶段选择跳过，绝不改变旧策略的既有行为。
 */
open class LegacyAiStrategyAdapter @JvmOverloads constructor(
    private val delegate: AIStrategy,
    private val allowCheats: Boolean = false,
    override val cheatPolicy: AiCheatPolicy? = null
) : AiDecisionStrategy {

    override fun choose(context: AiDecisionContext): String? =
        chooseWithStrategy(context, delegate, allowCheats)
}

/**
 * 默认基础 AI 的统一决策策略。
 *
 * 普通动作委托 [BasicAIStrategy]；千术候选按收益、点数成本与暴露风险统一评分。
 * 只有千术评分严格高于普通动作且为正时才覆盖普通动作，保护既有和牌/立直优先级。
 */
class BasicAiDecisionStrategy @JvmOverloads constructor(
    private val delegate: AIStrategy = BasicAIStrategy(),
    override val cheatPolicy: AiCheatPolicy? = null
) : AiDecisionStrategy {

    override fun choose(context: AiDecisionContext): String? =
        chooseWithStrategy(context, delegate, allowCheats = true)
}

private fun chooseWithStrategy(
    context: AiDecisionContext,
    delegate: AIStrategy,
    allowCheats: Boolean
): String? {
    val phase = context.stamp.phase
    val cheatCandidates = context.candidates.filter { it.capabilityId != AiCandidateFactory.MAHJONG_CAPABILITY_ID }

    if (phase == AiPhase.OFFER_CHOICE) {
        if (cheatCandidates.isEmpty()) return context.safeDefault()?.optionId
        if (!allowCheats) return context.safeDefault()?.optionId ?: cheatCandidates.first().optionId
        return cheatCandidates.maxByOrNull { AiCandidateScorer.score(it, context.limits) }?.optionId
    }

    if (phase == AiPhase.SHUFFLE_PREPARATION) {
        if (!allowCheats) return context.safeDefault()?.optionId ?: cheatCandidates.firstOrNull()?.optionId
        val safe = context.safeDefault()
        val scored = cheatCandidates.filter { !it.isSafeDefault }
            .maxByOrNull { AiCandidateScorer.score(it, context.limits) }
        return if (scored != null && AiCandidateScorer.score(scored, context.limits) > 0.0) {
            scored.optionId
        } else {
            safe?.optionId ?: scored?.optionId
        }
    }

    val ordinaryCandidates = context.candidates.filter { it.capabilityId == AiCandidateFactory.MAHJONG_CAPABILITY_ID }
    val ordinaryChoice = chooseOrdinary(context, ordinaryCandidates, delegate)
    if (!allowCheats || cheatCandidates.isEmpty()) return ordinaryChoice

    val bestCheat = cheatCandidates.maxByOrNull { AiCandidateScorer.score(it, context.limits) } ?: return ordinaryChoice
    val cheatScore = AiCandidateScorer.score(bestCheat, context.limits)
    val ordinaryCandidate = ordinaryChoice?.let { id -> ordinaryCandidates.firstOrNull { it.optionId == id } }
    val ordinaryScore = ordinaryCandidate?.let { AiCandidateScorer.score(it, context.limits) } ?: 0.0
    return if (cheatScore > ordinaryScore && cheatScore > 0.0) bestCheat.optionId else ordinaryChoice
}

/**
 * 旧策略普通动作选择入口（与候选隔离）。
 *
 * 该函数单独拆出，确保 [AIStrategy] 始终只接收 [PlayerView] 与合法动作列表。
 */
private fun chooseOrdinary(
    context: AiDecisionContext,
    ordinaryCandidates: List<AiCandidate>,
    delegate: AIStrategy
): String? {
    if (ordinaryCandidates.isEmpty()) return null
    val actions = context.decision.options.map { it.action }
    if (actions.isEmpty()) return null
    val chosen = delegate.decideAction(context.view, actions) ?: return null
    val option = context.decision.options.firstOrNull { it.action == chosen } ?: return null
    return ordinaryCandidates.firstOrNull { it.optionId == option.optionId }?.optionId
}

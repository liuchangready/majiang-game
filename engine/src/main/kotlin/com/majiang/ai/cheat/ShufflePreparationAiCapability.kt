package com.majiang.ai.cheat

import com.majiang.ai.AiCandidate
import com.majiang.ai.AiCandidateLimits
import com.majiang.ai.AiDecisionStamp
import com.majiang.ai.AiEffect
import com.majiang.ai.AiPhase
import com.majiang.ai.AiRisk
import com.majiang.ai.AiSeatSnapshot
import com.majiang.ai.AiVisibleParams
import com.majiang.authority.TableAuthority
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget

/**
 * 洗牌准备阶段 AI 适配器（统一码牌千术）。
 *
 * 候选复用 [ShuffleSelection.cost] 与 [TableAuthority.submitShufflePlan] 的预留/扣费语义；
 * 不直接生成牌山，也不读取任何隐藏牌信息。AI 自动方案只选一个目标（默认万子清一色），
 * 遵守预算与看破权限。截止时间前未确认仍由权威层默认跳过。
 */
class ShufflePreparationAiCapability(
    private val authority: TableAuthority
) : CheatAiCapability {

    override val id: String = AiCapabilityIds.SHUFFLE_PREPARATION
    override val version: Int = 2

    override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
        id = id,
        version = version,
        phases = setOf(AiPhase.SHUFFLE_PREPARATION),
        supportsSubmit = true,
        maxCandidates = 6
    )

    override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> {
        if (view.phase != AiPhase.SHUFFLE_PREPARATION) return emptyList()
        val preparation = view.cheatView.preparation ?: return emptyList()
        if (preparation.confirmed) return emptyList()

        val result = ArrayList<AiCandidate>(4)
        result.add(
            AiCandidate(
                optionId = "$id:skip",
                capabilityId = id,
                phase = AiPhase.SHUFFLE_PREPARATION,
                cost = 0,
                risk = AiRisk(),
                estimatedEffect = AiEffect(note = "skip preparation"),
                terminalForWindow = true,
                parameters = AiVisibleParams.EMPTY,
                isSafeDefault = true
            )
        )

        val seeThroughEnabled = preparation.seeThroughEnabled
        if (seeThroughEnabled) {
            val seeOnly = ShuffleSelection(seeThrough = true)
            if (limits.canSpend(seeOnly.cost())) {
                result.add(planCandidate("see-through", seeOnly, 8, "counter opponent shuffle shaping"))
            }
        }

        val target = ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN)
        if (limits.canSpend(target.cost())) {
            result.add(planCandidate("target", target, 22, "unified shaping for initial hand and draws"))
        }

        if (seeThroughEnabled) {
            val combined = target.copy(seeThrough = true)
            if (limits.canSpend(combined.cost())) {
                result.add(planCandidate("combined", combined, 30, "own shaping plus see-through"))
            }
        }
        return result
    }

    private fun planCandidate(
        optionSuffix: String,
        selection: ShuffleSelection,
        effect: Int,
        note: String
    ): AiCandidate = AiCandidate(
        optionId = "$id:$optionSuffix",
        capabilityId = id,
        phase = AiPhase.SHUFFLE_PREPARATION,
        cost = selection.cost(),
        risk = AiRisk(exposure = 0.05, suspicion = 0.05),
        estimatedEffect = AiEffect(handValue = effect, note = note),
        terminalForWindow = true,
        parameters = AiVisibleParams.of(
            "target" to (selection.target?.name ?: ""),
            "seeThrough" to selection.seeThrough.toString()
        )
    )

    override fun submit(
        seat: Int,
        selected: AiCandidate,
        stamp: AiDecisionStamp,
        now: Long
    ): TableAuthority.MutationResult {
        require(selected.capabilityId == id) { "capability mismatch: ${selected.capabilityId}" }
        require(selected.phase == AiPhase.SHUFFLE_PREPARATION) { "phase mismatch: ${selected.phase}" }
        val roundId = requireNotNull(stamp.roundId) { "shuffle preparation requires roundId" }
        val selection = ShuffleSelection(
            target = parseTarget(selected.parameters.string("target")),
            seeThrough = selected.parameters.string("seeThrough")?.toBooleanStrictOrNull() == true
        )
        val request = ShufflePlanRequest(
            requestId = AiRequestId.of(id, stamp, selected.optionId),
            roundId = roundId,
            controlEpoch = stamp.controlEpoch,
            selection = selection
        )
        return authority.submitShufflePlan(seat, request, now)
    }

    private fun parseTarget(raw: String?): ShuffleTarget? {
        if (raw.isNullOrBlank()) return null
        val name = raw.trim()
        return ShuffleTarget.entries.firstOrNull { it.name == name }
            ?: error("unknown shuffle target: $name")
    }
}

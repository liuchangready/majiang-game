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
import java.util.LinkedHashSet

/**
 * 洗牌准备阶段 AI 适配器。
 *
 * 候选复用 [ShuffleSelection.cost] 与 [TableAuthority.submitShufflePlan] 的预留/扣费语义；
 * 不直接生成牌山，也不读取任何隐藏牌信息。截止时间前未确认仍由权威层默认跳过。
 */
class ShufflePreparationAiCapability(
    private val authority: TableAuthority
) : CheatAiCapability {

    override val id: String = AiCapabilityIds.SHUFFLE_PREPARATION
    override val version: Int = 1

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

        val initial = ShuffleSelection(initialTargets = setOf(ShuffleTarget.CHINITSU_MAN))
        if (limits.canSpend(initial.cost())) {
            result.add(planCandidate("initial", initial, 22, "initial hand shaping"))
        }
        val draw = ShuffleSelection(drawTargets = setOf(ShuffleTarget.DAISANGEN))
        if (limits.canSpend(draw.cost())) {
            result.add(planCandidate("draw", draw, 10, "future draw shaping"))
        }
        val both = ShuffleSelection(
            initialTargets = setOf(ShuffleTarget.CHINITSU_MAN),
            drawTargets = setOf(ShuffleTarget.DAISANGEN)
        )
        if (limits.canSpend(both.cost())) {
            result.add(planCandidate("both", both, 40, "initial and draw shaping"))
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
            "initialTargets" to names(selection.initialTargets),
            "drawTargets" to names(selection.drawTargets)
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
            initialTargets = parseTargets(selected.parameters.string("initialTargets")),
            drawTargets = parseTargets(selected.parameters.string("drawTargets"))
        )
        val request = ShufflePlanRequest(
            requestId = AiRequestId.of(id, stamp, selected.optionId),
            roundId = roundId,
            controlEpoch = stamp.controlEpoch,
            selection = selection
        )
        return authority.submitShufflePlan(seat, request, now)
    }

    private fun names(targets: Set<ShuffleTarget>): String =
        targets.joinToString(",") { it.name }

    private fun parseTargets(raw: String?): Set<ShuffleTarget> {
        if (raw.isNullOrBlank()) return emptySet()
        val result = LinkedHashSet<ShuffleTarget>()
        for (part in raw.split(',')) {
            val name = part.trim()
            if (name.isEmpty()) continue
            val target = ShuffleTarget.entries.firstOrNull { it.name == name } ?: continue
            result.add(target)
        }
        return result
    }
}

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
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.model.Tile
import java.util.LinkedHashMap

/**
 * 落汗 AI 适配器。
 *
 * 仅使用本座位 [com.majiang.cheat.mark.TileMarkView.selectableTileIds] 与自身手牌；
 * 候选只表达“标记哪四张”，费用、上限、立直锁与透视边界全部由
 * [TableAuthority.submitTileMark] 再次校验。
 */
class TileMarkAiCapability(
    private val authority: TableAuthority
) : CheatAiCapability {

    override val id: String = AiCapabilityIds.TILE_MARK
    override val version: Int = 1

    override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
        id = id,
        version = version,
        phases = setOf(AiPhase.ORDINARY_ACTION),
        supportsSubmit = true,
        maxCandidates = 3
    )

    override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> {
        if (view.phase != AiPhase.ORDINARY_ACTION) return emptyList()
        val markView = view.cheatView.tileMark ?: return emptyList()
        if (!markView.canApply) return emptyList()
        // 费用必须来自本座位授权投影，禁止在适配器内硬编码；权威扣费/审计/预算均以该值为准。
        val cost = markView.cost
        if (!limits.canSpend(cost)) return emptyList()
        val selectable = markView.selectableTileIds
        if (selectable.size < 4) return emptyList()

        val byId = LinkedHashMap<Int, Tile>()
        for (tile in view.view.myClosedTiles) byId[tile.tileId] = tile
        view.view.myDrawnTile?.let { byId[it.tileId] = it }
        val hand = byId.values.toList()
        val selectableTiles = selectable.mapNotNull { byId[it] }
        if (selectableTiles.size < 4) return emptyList()

        val high = AiCheatHeuristics.mostValuableHandTiles(selectableTiles, 4).map { it.tileId }
        val low = AiCheatHeuristics.leastValuableHandTiles(selectableTiles, 4).map { it.tileId }
        val result = ArrayList<AiCandidate>(2)
        result.add(candidate("high", high, 8, hand, cost))
        if (low.toSet() != high.toSet()) {
            result.add(candidate("low", low, 5, hand, cost))
        }
        return result
    }

    private fun candidate(
        optionSuffix: String,
        tileIds: List<Int>,
        effect: Int,
        hand: List<Tile>,
        cost: Int
    ): AiCandidate = AiCandidate(
        optionId = "$id:$optionSuffix",
        capabilityId = id,
        phase = AiPhase.ORDINARY_ACTION,
        cost = cost,
        risk = AiRisk(exposure = 0.15, suspicion = 0.1),
        estimatedEffect = AiEffect(handValue = effect, note = "mark ${tileIds.size} tiles"),
        terminalForWindow = false,
        parameters = AiVisibleParams.of(
            "tileIds" to tileIds.joinToString(","),
            "handSize" to hand.size.toString()
        )
    )

    override fun submit(
        seat: Int,
        selected: AiCandidate,
        stamp: AiDecisionStamp,
        now: Long
    ): TableAuthority.MutationResult {
        require(selected.capabilityId == id) { "capability mismatch: ${selected.capabilityId}" }
        require(selected.phase == AiPhase.ORDINARY_ACTION) { "phase mismatch: ${selected.phase}" }
        val roundId = requireNotNull(stamp.roundId) { "tile mark requires roundId" }
        val decisionId = requireNotNull(stamp.decisionId) { "tile mark requires decisionId" }
        val tileIds = selected.parameters.intList("tileIds")
        require(tileIds.size == 4) { "tile mark candidate must select exactly 4 tiles" }
        val request = TileMarkRequest(
            requestId = AiRequestId.of(id, stamp, selected.optionId),
            sessionId = stamp.sessionId,
            roundId = roundId,
            decisionId = decisionId,
            controlEpoch = stamp.controlEpoch,
            tileIds = tileIds
        )
        return authority.submitTileMark(seat, request, now, allowAutomated = true)
    }
}

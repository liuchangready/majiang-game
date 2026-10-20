package com.majiang.ai.cheat

import com.majiang.ai.AiCandidate
import com.majiang.ai.AiCandidateLimits
import com.majiang.ai.AiDecisionStamp
import com.majiang.ai.AiPhase
import com.majiang.ai.AiSeatSnapshot
import com.majiang.authority.TableAuthority
import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * 千术能力描述。
 *
 * 注册表启动时校验 ID、版本、阶段、候选上限与提交入口，防止能力适配器与通用调度器契约漂移。
 */
data class AiCapabilityDescriptor(
    val id: String,
    val version: Int,
    val phases: Set<AiPhase>,
    val supportsSubmit: Boolean,
    val maxCandidates: Int
) {
    init {
        require(id.isNotBlank()) { "descriptor id cannot be blank" }
        require(version > 0) { "descriptor version must be positive, got: $version" }
        require(phases.isNotEmpty()) { "descriptor phases cannot be empty" }
        require(phases.none { it == AiPhase.IDLE }) { "descriptor must not advertise IDLE" }
        require(maxCandidates in 1..64) { "descriptor maxCandidates must be in 1..64, got: $maxCandidates" }
    }
}

/**
 * 千术 AI 适配器。
 *
 * `candidates()` 只能读取按座位冻结的 [AiSeatSnapshot]；`submit()` 仅在权威串行队列内
 * 由通用调度器调用，并必须复用既有 `TableAuthority.submit*` 入口。
 */
interface CheatAiCapability {
    val id: String
    val version: Int

    fun describe(): AiCapabilityDescriptor

    fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate>

    fun submit(seat: Int, selected: AiCandidate, stamp: AiDecisionStamp, now: Long): TableAuthority.MutationResult
}

/**
 * 内置与测试能力使用的稳定 ID。
 */
object AiCapabilityIds {
    const val MAHJONG = "mahjong"
    const val SHUFFLE_PREPARATION = "shuffle_preparation"
    const val TILE_MARK = "tile_mark"
    const val SLEEVE_BLOOM = "sleeve_bloom"
    const val BEAM_SWAP = "beam_swap"
}

/**
 * 确定性幂等请求 ID：由完整 [AiDecisionStamp] 与 optionId 派生。
 *
 * 同一 stamp + 同一候选在重试时复用同一 requestId，权威回执库可安全去重；
 * 任一决策身份字段（session/round/decision/epoch/phase/stateVersion/offer/window/deadline）
 * 或能力/选项变化都会产生不同 requestId，避免同一座位同一局的第二个 off-turn 窗口
 * 或同 decisionId 的 stateVersion 变化被旧回执误判为冲突。
 */
object AiRequestId {
    fun of(capabilityId: String, stamp: AiDecisionStamp, optionId: String): String {
        val raw = buildString {
            append("ai-")
            appendField(stamp.seat.toString())
            appendField(stamp.sessionId)
            appendField(stamp.roundId)
            appendField(stamp.decisionId)
            appendField(stamp.controlEpoch.toString())
            appendField(stamp.phase.name)
            appendField(stamp.stateVersion.toString())
            appendField(stamp.offerId)
            appendField(stamp.windowId)
            appendField(stamp.deadlineMillis?.toString())
            appendField(capabilityId)
            appendField(optionId)
        }
        val sanitized = raw.replace(Regex("[^A-Za-z0-9._:-]"), "_")
        val digest = java.security.MessageDigest.getInstance("SHA-256")
            .digest(raw.toByteArray(Charsets.UTF_8))
        val suffix = digest.take(8).joinToString("") { "%02x".format(it.toInt() and 0xff) }
        return "${sanitized.take(150)}-$suffix"
    }

    private fun StringBuilder.appendField(value: String?) {
        if (value == null) {
            append("~|")
        } else {
            append(value.length).append(':').append(value).append('|')
        }
    }
}

/**
 * 已注册千术能力的只读注册表。
 *
 * 通用 AI 调度器只遍历本注册表生成候选，禁止在调度器或策略内写 `when (千术类型)`。
 */
class CheatAiRegistry(capabilities: List<CheatAiCapability>) {
    private val byId: Map<String, CheatAiCapability>
    private val descriptors: Map<String, AiCapabilityDescriptor>

    val capabilities: List<CheatAiCapability>

    init {
        val ordered = Collections.unmodifiableList(ArrayList(capabilities))
        val index = LinkedHashMap<String, CheatAiCapability>()
        val descriptorIndex = LinkedHashMap<String, AiCapabilityDescriptor>()
        for (capability in ordered) {
            val id = capability.id
            require(id.isNotBlank()) { "capability id cannot be blank" }
            require(id != AiCapabilityIds.MAHJONG) {
                "capability id '${AiCapabilityIds.MAHJONG}' is reserved for ordinary actions"
            }
            require(index.put(id, capability) == null) { "duplicate capability id: $id" }
            val descriptor = capability.describe()
            require(descriptor.id == id) {
                "capability id '$id' does not match descriptor id '${descriptor.id}'"
            }
            require(descriptor.version == capability.version) {
                "capability '$id' version ${capability.version} does not match descriptor version ${descriptor.version}"
            }
            require(descriptor.supportsSubmit) { "capability '$id' must expose an authoritative submit entry" }
            descriptorIndex[id] = descriptor
        }
        this.capabilities = ordered
        this.byId = Collections.unmodifiableMap(index)
        this.descriptors = Collections.unmodifiableMap(descriptorIndex)
    }

    fun descriptor(capabilityId: String): AiCapabilityDescriptor? = descriptors[capabilityId]

    fun find(capabilityId: String): CheatAiCapability? = byId[capabilityId]

    fun require(capabilityId: String): CheatAiCapability =
        byId[capabilityId] ?: throw IllegalArgumentException("Unknown AI cheat capability: $capabilityId")

    val ids: List<String> get() = capabilities.map { it.id }

    /**
     * 在给定快照与预算下生成已校验的千术候选。
     *
     * offer 选择阶段（费用已在激活时支付）不受当前花费总开关影响，始终生成安全候选，
     * 以便 offer 出现后可以继续决策并安全兜底。
     */
    fun candidates(snapshot: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> {
        val phase = snapshot.phase
        if (phase == AiPhase.IDLE) return emptyList()
        val result = ArrayList<AiCandidate>()
        for (capability in capabilities) {
            val descriptor = descriptors[capability.id] ?: continue
            if (phase !in descriptor.phases) continue
            if (phase != AiPhase.OFFER_CHOICE && !limits.allows(capability.id)) continue
            val raw = try {
                capability.candidates(snapshot, limits)
            } catch (_: Throwable) {
                emptyList()
            }
            val filtered = ArrayList<AiCandidate>()
            val seen = LinkedHashSet<String>()
            val max = minOf(limits.candidateLimit, descriptor.maxCandidates)
            for (candidate in raw) {
                if (filtered.size >= max) break
                if (candidate.capabilityId != capability.id) continue
                if (candidate.phase != phase) continue
                if (candidate.cost < 0) continue
                if (phase == AiPhase.OFFER_CHOICE && candidate.cost != 0) continue
                if (phase != AiPhase.OFFER_CHOICE && !limits.canSpend(candidate.cost)) continue
                if (!seen.add(candidate.optionId)) continue
                filtered.add(candidate)
            }
            result.addAll(filtered)
        }
        return Collections.unmodifiableList(result)
    }

    companion object {
        /** 默认四能力显式装配；新增千术在组合根追加一个适配器即可进入候选集。 */
        fun builtIn(authority: TableAuthority): CheatAiRegistry = CheatAiRegistry(
            listOf(
                ShufflePreparationAiCapability(authority),
                TileMarkAiCapability(authority),
                SleeveBloomAiCapability(authority),
                BeamSwapAiCapability(authority)
            )
        )
    }
}

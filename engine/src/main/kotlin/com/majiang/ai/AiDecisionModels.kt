package com.majiang.ai

import com.majiang.access.deepCopy
import com.majiang.application.PlayerDecision
import com.majiang.application.deepCopy
import com.majiang.cheat.beam.BeamSwapView
import com.majiang.cheat.bloom.SleeveBloomView
import com.majiang.cheat.mark.TileMarkView
import com.majiang.session.PlayerView
import com.majiang.stacking.ShufflePreparationView
import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * AI 决策窗口阶段。
 *
 * 阶段是 stamp 的一部分：同一座位、同一 decisionId 在不同阶段（例如普通行动窗口
 * 与千术 offer 选择窗口）互不通用，避免异步结果跨窗口错误生效。
 */
enum class AiPhase {
    IDLE,
    SHUFFLE_PREPARATION,
    ORDINARY_ACTION,
    OFFER_CHOICE
}

/**
 * 候选的风险估计（仅启发式，不参与权威判定）。
 *
 * @property exposure 行动被其他玩家察觉的风险 [0, 1]
 * @property suspicion 行动增加怀疑度的风险 [0, 1]
 */
data class AiRisk @JvmOverloads constructor(
    val exposure: Double = 0.0,
    val suspicion: Double = 0.0
) {
    init {
        require(!exposure.isNaN() && exposure in 0.0..1.0) { "exposure must be in 0.0..1.0, got: $exposure" }
        require(!suspicion.isNaN() && suspicion in 0.0..1.0) { "suspicion must be in 0.0..1.0, got: $suspicion" }
    }

    /** 统一风险罚分，供策略排序使用。 */
    val penalty: Double get() = exposure * 0.5 + suspicion * 0.5
}

/**
 * 候选的可见效果估计。
 *
 * 只允许使用本座位可见信息推导，不承诺真实结果。
 */
data class AiEffect @JvmOverloads constructor(
    val handValue: Int = 0,
    val shantenImprovement: Int = 0,
    val retainedValue: Int = 0,
    val note: String = ""
) {
    init {
        require(note.length <= 120) { "effect note must be <= 120 chars" }
    }
}

/**
 * 不含隐藏牌与可变领域对象的候选参数。
 *
 * 参数以字符串键值对承载，能力适配器自行解析；容量与内容均只来自本座位授权投影。
 */
class AiVisibleParams private constructor(raw: Map<String, String>) {
    val values: Map<String, String> = Collections.unmodifiableMap(LinkedHashMap(raw))

    fun string(key: String): String? = values[key]

    fun int(key: String): Int? = values[key]?.toIntOrNull()

    fun intList(key: String): List<Int> = values[key]
        ?.split(',')
        ?.mapNotNull { it.trim().toIntOrNull() }
        ?: emptyList()

    fun stringSet(key: String): Set<String> = values[key]
        ?.split(',')
        ?.map { it.trim() }
        ?.filter { it.isNotEmpty() }
        ?.let { LinkedHashSet(it) }
        ?: emptySet()

    companion object {
        val EMPTY: AiVisibleParams = AiVisibleParams(emptyMap())

        fun of(vararg entries: Pair<String, String>): AiVisibleParams {
            val map = LinkedHashMap<String, String>()
            for ((key, value) in entries) {
                require(key.isNotBlank()) { "parameter key cannot be blank" }
                map[key] = value
            }
            return AiVisibleParams(map)
        }

        fun from(map: Map<String, String>): AiVisibleParams = AiVisibleParams(map)
    }

    override fun equals(other: Any?): Boolean = other is AiVisibleParams && values == other.values

    override fun hashCode(): Int = values.hashCode()

    override fun toString(): String = "AiVisibleParams($values)"
}

/**
 * 统一 AI 候选。
 *
 * 普通麻将动作与千术使用同一候选契约。`optionId` 在单次决策内唯一，提交时会在
 * 新鲜权威投影上重新定位；策略只返回 `optionId`，不得直接构造领域请求。
 *
 * @property optionId 当前决策内唯一标识
 * @property capabilityId `"mahjong"` 或稳定的千术能力 ID
 * @property phase 生成该候选的决策窗口阶段
 * @property cost 预估消耗千术点（普通动作为 0）
 * @property risk 暴露与怀疑风险估计
 * @property estimatedEffect 仅基于可见信息的效果估计
 * @property terminalForWindow 执行后是否结束当前决策窗口
 * @property parameters 可见参数，不含隐藏牌或可变领域对象
 * @property isSafeDefault 当前窗口的安全兜底候选（不使用/保留原状/跳过）
 */
data class AiCandidate(
    val optionId: String,
    val capabilityId: String,
    val phase: AiPhase,
    val cost: Int,
    val risk: AiRisk,
    val estimatedEffect: AiEffect,
    val terminalForWindow: Boolean,
    val parameters: AiVisibleParams = AiVisibleParams.EMPTY,
    val isSafeDefault: Boolean = false
) {
    init {
        require(optionId.isNotBlank()) { "optionId cannot be blank" }
        require(capabilityId.isNotBlank()) { "capabilityId cannot be blank" }
        require(cost >= 0) { "cost must be non-negative, got: $cost" }
    }
}

/**
 * 决策身份 stamp。
 *
 * 异步计算开始时冻结，结果回到权威串行队列时逐项与当前 stamp 比较。任何一项不一致
 * （跨局、决策已推进、控制权收回、阶段变化、状态版本变化、offer/window 更替、deadline
 * 变化）都使旧结果失效。
 */
data class AiDecisionStamp @JvmOverloads constructor(
    val seat: Int,
    val sessionId: String,
    val roundId: String?,
    val decisionId: String?,
    val controlEpoch: Long,
    val phase: AiPhase,
    val stateVersion: Long,
    val offerId: String?,
    val windowId: String?,
    val deadlineMillis: Long?
) {
    init {
        require(seat in 0..3) { "seat must be in 0..3, got: $seat" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(stateVersion >= 0L) { "stateVersion must be non-negative: $stateVersion" }
        if (deadlineMillis != null) {
            require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        }
    }
}

/**
 * 单次 AI 决策的预算与筛选上限（由权威投影一次计算并冻结）。
 */
class AiCandidateLimits(
    enabledCapabilityIds: Set<String>,
    val maxSpend: Int,
    val remainingPoints: Int,
    val reservePoints: Int,
    val candidateLimit: Int,
    val riskTolerance: Double
) {
    val enabledCapabilityIds: Set<String> = Collections.unmodifiableSet(LinkedHashSet(enabledCapabilityIds))

    init {
        require(maxSpend >= 0) { "maxSpend must be non-negative, got: $maxSpend" }
        require(remainingPoints >= 0) { "remainingPoints must be non-negative, got: $remainingPoints" }
        require(reservePoints >= 0) { "reservePoints must be non-negative, got: $reservePoints" }
        require(candidateLimit in 1..64) { "candidateLimit must be in 1..64, got: $candidateLimit" }
        require(!riskTolerance.isNaN() && riskTolerance in 0.0..1.0) {
            "riskTolerance must be in 0.0..1.0, got: $riskTolerance"
        }
    }

    /** 本决策可实际花费的上限。 */
    val spendablePoints: Int get() = maxOf(0, minOf(maxSpend, remainingPoints - reservePoints))

    fun allows(capabilityId: String): Boolean = capabilityId in enabledCapabilityIds

    fun canSpend(cost: Int): Boolean = cost >= 0 && cost <= spendablePoints
}

/**
 * 本座位授权的千术私有投影集合。
 *
 * 仅包含投影工厂为本座位生成的数据；绝不携带 GameState、牌山顺序或他家暗牌。
 */
class AiCheatView @JvmOverloads constructor(
    val cheatPoints: Int = 0,
    val preparation: ShufflePreparationView? = null,
    val tileMark: TileMarkView? = null,
    val sleeveBloom: SleeveBloomView? = null,
    val beamSwap: BeamSwapView? = null
) {
    init {
        require(cheatPoints >= 0) { "cheatPoints must be non-negative, got: $cheatPoints" }
    }

    fun deepCopy(): AiCheatView = AiCheatView(
        cheatPoints = cheatPoints,
        preparation = preparation?.deepCopy(),
        tileMark = tileMark?.deepCopy(),
        sleeveBloom = sleeveBloom?.deepCopy(),
        beamSwap = beamSwap?.deepCopy()
    )
}

/**
 * 权威层一次性按座位生成的 AI 决策快照。
 *
 * 该对象在进入异步线程前深拷贝冻结；能力适配器只能读取此快照。
 */
class AiSeatSnapshot(
    val stamp: AiDecisionStamp,
    val view: PlayerView,
    val decision: PlayerDecision,
    val cheatView: AiCheatView,
    val limits: AiCandidateLimits,
    val policy: AiCheatPolicy,
    val takeover: Boolean
) {
    val seat: Int get() = stamp.seat
    val phase: AiPhase get() = stamp.phase

    fun deepCopy(): AiSeatSnapshot = AiSeatSnapshot(
        stamp = stamp,
        view = view.deepCopy(),
        decision = decision.deepCopy(),
        cheatView = cheatView.deepCopy(),
        limits = limits,
        policy = policy,
        takeover = takeover
    )
}

/**
 * 策略看到的完整决策上下文。
 */
class AiDecisionContext(
    val snapshot: AiSeatSnapshot,
    candidates: List<AiCandidate>
) {
    val view: PlayerView get() = snapshot.view
    val decision: PlayerDecision get() = snapshot.decision
    val privateCheatView: AiCheatView get() = snapshot.cheatView
    val limits: AiCandidateLimits get() = snapshot.limits
    val stamp: AiDecisionStamp get() = snapshot.stamp
    val roundId: String? get() = snapshot.stamp.roundId
    val decisionId: String? get() = snapshot.stamp.decisionId
    val controlEpoch: Long get() = snapshot.stamp.controlEpoch
    val deadlineMillis: Long? get() = snapshot.stamp.deadlineMillis

    val candidates: List<AiCandidate> = Collections.unmodifiableList(ArrayList(candidates))

    init {
        val ids = this.candidates.map { it.optionId }
        require(ids.size == ids.toSet().size) { "candidate optionId must be unique within a decision" }
    }

    fun candidate(optionId: String): AiCandidate? = candidates.firstOrNull { it.optionId == optionId }

    fun safeDefault(): AiCandidate? = candidates.firstOrNull { it.isSafeDefault }
}

/**
 * 统一 AI 决策策略入口。
 *
 * 旧 [AIStrategy] 通过兼容包装器接入；新策略实现本接口即可同时看到普通动作与千术候选。
 * 返回 `optionId`，返回 null 表示交由调度器执行安全兜底。
 */
interface AiDecisionStrategy {
    /** 策略级可选约束；与房间策略取交集（null 表示不额外限制）。 */
    val cheatPolicy: AiCheatPolicy? get() = null

    fun choose(context: AiDecisionContext): String?
}

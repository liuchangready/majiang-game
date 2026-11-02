package com.majiang.authority.cheat

import com.majiang.model.Tile
import com.majiang.round.RoundPreparationRecord
import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShufflePreparationStatus
import com.majiang.stacking.ShufflePreparationView
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleWallGenerator
import com.majiang.state.GamePhase
import java.security.SecureRandom

/**
 * 码牌/洗牌准备阶段专用处理器（D-09）。
 *
 * 码牌属于准备阶段，不套用局内三大绝技的 Handler 生命周期。本处理器拥有准备窗口的
 * 选择、确认与预留状态，但不创建线程或计时源：超时、取消和 AI 自动处理仍由
 * [com.majiang.authority.TableAuthority] 在既有 tick/权威流程中调用。
 */
internal class ShufflePreparationHandler {

    sealed interface PlanCheck {
        data class Accepted(val cost: Int, val selection: ShuffleSelection) : PlanCheck
        data class Rejected(val reason: String) : PlanCheck
    }

    sealed interface Step {
        data object Waiting : Step
        data class RecordUpdated(val record: RoundPreparationRecord) : Step
        data class Ready(
            val roundId: String,
            val record: RoundPreparationRecord,
            /** 截止时冻结的原始方案（费用必须按它计算，绝不能被看破改写）。 */
            val plans: List<SeatShufflePlan>,
            /** 仅供牌山算法使用的有效方案副本：被看破者的统一目标 target 已被移除（整效抵消）。 */
            val effectivePlans: List<SeatShufflePlan>,
            val tiles: List<Tile>?
        ) : Step
    }

    private var preparationRoundId: String? = null
    private var status: ShufflePreparationStatus = ShufflePreparationStatus.SELECTING
    private var opensAtMillis: Long = 0L
    private var deadlineMillis: Long = 0L
    private var roundSeed: Long = 0L
    private var seeThroughEnabled: Boolean = false
    private var frozenSeeThroughSeats: Set<Int> = emptySet()
    private val confirmed = BooleanArray(4) { false }
    private val selections = arrayOfNulls<ShuffleSelection>(4)
    private val reserved = IntArray(4) { 0 }
    private val secureRandom = SecureRandom()

    fun currentRoundId(): String? = preparationRoundId

    fun currentStatus(): ShufflePreparationStatus = status

    fun deadlineMillis(): Long = deadlineMillis

    fun reservedFor(seat: Int): Int = if (seat in 0..3) reserved[seat] else 0

    fun start(roundId: String, now: Long, config: ShuffleConfig): RoundPreparationRecord {
        preparationRoundId = roundId
        status = ShufflePreparationStatus.SELECTING
        opensAtMillis = now
        deadlineMillis = opensAtMillis + config.selectionMillis
        roundSeed = secureRandom.nextLong()
        seeThroughEnabled = config.seeThroughEnabled
        frozenSeeThroughSeats = emptySet()
        confirmed.fill(false)
        selections.fill(null)
        reserved.fill(0)
        return record(roundId)
    }

    fun isActive(
        closed: Boolean,
        sessionClosed: Boolean,
        activeRoundId: String?,
        phase: GamePhase?,
        wallEmpty: Boolean
    ): Boolean {
        if (closed || sessionClosed) return false
        val prepId = preparationRoundId ?: return false
        return activeRoundId == prepId && phase == GamePhase.STACKING && wallEmpty
    }

    fun buildView(
        seat: Int,
        record: RoundPreparationRecord?,
        now: Long,
        currentPoints: Int
    ): ShufflePreparationView {
        val roundId = requireNotNull(preparationRoundId) { "Preparation is not active" }
        val confirmedValue = record?.confirmed?.getOrNull(seat) ?: confirmed[seat]
        val selection = record?.selections?.getOrNull(seat) ?: selections[seat]
        val reservedValue = record?.reserved?.getOrNull(seat) ?: reserved[seat]
        val available = maxOf(0, currentPoints - reservedValue)
        return ShufflePreparationView(
            roundId = roundId,
            status = status,
            opensAtMillis = opensAtMillis,
            deadlineMillis = deadlineMillis,
            serverNowMillis = now,
            confirmed = confirmedValue,
            selection = selection?.deepCopy(),
            reservedPoints = reservedValue,
            availablePoints = available,
            seeThroughEnabled = seeThroughEnabled,
            seeThroughCost = ShuffleSelection.SEE_THROUGH_COST
        )
    }

    fun validatePlan(
        seat: Int,
        request: ShufflePlanRequest,
        currentEpoch: Long,
        now: Long,
        currentPoints: Int,
        active: Boolean,
        activeRoundId: String?
    ): PlanCheck {
        if (request.controlEpoch != currentEpoch) return PlanCheck.Rejected("STALE_EPOCH")
        if (!active ||
            request.roundId != preparationRoundId ||
            activeRoundId != preparationRoundId ||
            status != ShufflePreparationStatus.SELECTING
        ) {
            return PlanCheck.Rejected("WRONG_PHASE")
        }
        if (now >= deadlineMillis) return PlanCheck.Rejected("PREPARATION_EXPIRED")
        if (confirmed[seat]) return PlanCheck.Rejected("ALREADY_CONFIRMED")

        val safeSelection = request.selection.deepCopy()
        if (safeSelection.seeThrough && !seeThroughEnabled) {
            return PlanCheck.Rejected("SEE_THROUGH_DISABLED")
        }
        val cost = safeSelection.cost()
        if (cost !in 0..ShuffleSelection.MAX_COST) return PlanCheck.Rejected("INVALID_COST")
        if (currentPoints < cost) return PlanCheck.Rejected("INSUFFICIENT_FUNDS")
        return PlanCheck.Accepted(cost, safeSelection)
    }

    fun recordPlan(seat: Int, selection: ShuffleSelection, cost: Int): RoundPreparationRecord {
        confirmed[seat] = true
        selections[seat] = selection
        reserved[seat] = cost
        return record(requireNotNull(preparationRoundId) { "Preparation is not active" })
    }

    /**
     * 推进准备阶段。
     *
     * 新 AI 策略路径由 [com.majiang.ai.cheat.ShufflePreparationAiCapability] 在截止前提交，
     * 策略提交成功即已确认。为保留旧 `ShuffleConfig.aiMaySpend` 的实际效果，当房间未开启
     * 统一 AI 千术策略时，原生电脑仍按旧点数档位自动确认；该兼容路径不会作用于托管座位。
     * 截止时仍未确认的座位（包括 AI 与真人）按既有规则默认跳过。
     *
     * 早结束仅要求“四个座位都已确认”，不会把某个真人的跳过扩散到其他真人；
     * 所有已确认方案与看破集合在进入 GENERATING 的同一刻冻结，费用仍按原始方案计算。
     */
    fun step(
        now: Long,
        automatedSeats: Set<Int>,
        nativeAiSeats: Set<Int>,
        points: List<Int>,
        aiMaySpend: Boolean,
        dealerSeat: Int,
        config: ShuffleConfig
    ): Step {
        val prepId = preparationRoundId ?: return Step.Waiting
        var aiUpdated = false
        if (aiMaySpend) {
            for (seat in 0 until 4) {
                if (seat !in automatedSeats || seat !in nativeAiSeats || confirmed[seat]) continue
                val currentPoints = points.getOrElse(seat) { 0 }
                val selection = when {
                    seeThroughEnabled && currentPoints >= 40 ->
                        ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN, seeThrough = true)
                    currentPoints >= 30 ->
                        ShuffleSelection(target = ShuffleTarget.CHINITSU_MAN)
                    else -> ShuffleSelection()
                }
                confirmed[seat] = true
                selections[seat] = selection
                reserved[seat] = selection.cost()
                aiUpdated = true
            }
        }

        if (now < deadlineMillis) {
            // 所有席位都已明确确认（真人提交或权威零费用 skip）时，没有理由继续空等倒计时：
            // 立即冻结方案并进入发牌。仍有任一真人未确认时保持等待，原 deadline 仍是安全兜底。
            if (!confirmed.all { it }) {
                return if (aiUpdated) Step.RecordUpdated(record(prepId)) else Step.Waiting
            }
        }

        status = ShufflePreparationStatus.GENERATING
        for (seat in 0 until 4) {
            if (!confirmed[seat]) {
                confirmed[seat] = true
                selections[seat] = ShuffleSelection()
                reserved[seat] = 0
            }
        }

        // 准备截止时冻结看破席位集合 K：之后请求顺序、AI 完成顺序或断线都不再影响结算。
        frozenSeeThroughSeats = if (seeThroughEnabled) {
            (0 until 4).filter { seat -> selections[seat]?.seeThrough == true }.toSet()
        } else {
            emptySet()
        }

        val record = record(prepId)
        val plans = (0 until 4).map { seat ->
            SeatShufflePlan(seat, record.selections.getOrNull(seat) ?: ShuffleSelection())
        }

        // 原始方案与有效方案严格分离：看破只影响牌山算法副本（整效抵消 target→null），
        // 费用仍由 [plans]（原始选择）计算；有效施术人数 n 由 effectivePlans 中 target 非空的席位计数。
        val effectivePlans = plans.map { plan ->
            val suppressed = frozenSeeThroughSeats.any { it != plan.seat }
            val effective = if (suppressed) {
                plan.selection.deepCopy().copy(target = null, seeThrough = false)
            } else {
                plan.selection.deepCopy().copy(seeThrough = false)
            }
            SeatShufflePlan(plan.seat, effective)
        }

        val tiles = try {
            ShuffleWallGenerator().generate(
                seed = record.seed,
                dealerSeat = dealerSeat,
                plans = effectivePlans,
                config = config
            ).takeIf { it.size == 136 }
        } catch (t: Throwable) {
            null
        }
        return Step.Ready(prepId, record, plans, effectivePlans, tiles)
    }

    fun shiftForPause(pauseDuration: Long) {
        if (preparationRoundId == null) return
        opensAtMillis += pauseDuration
        deadlineMillis += pauseDuration
    }

    fun completeDeal() {
        preparationRoundId = null
        status = ShufflePreparationStatus.SELECTING
    }

    fun reset() {
        preparationRoundId = null
        status = ShufflePreparationStatus.SELECTING
        opensAtMillis = 0L
        deadlineMillis = 0L
        roundSeed = 0L
        seeThroughEnabled = false
        frozenSeeThroughSeats = emptySet()
        confirmed.fill(false)
        selections.fill(null)
        reserved.fill(0)
    }

    private fun record(roundId: String): RoundPreparationRecord = RoundPreparationRecord(
        roundId = roundId,
        seed = roundSeed,
        selections = selections.map { it?.deepCopy() },
        confirmed = confirmed.toList(),
        reserved = reserved.toList(),
        seeThroughSeats = frozenSeeThroughSeats
    )
}

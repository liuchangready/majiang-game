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
            val plans: List<SeatShufflePlan>,
            val tiles: List<Tile>?
        ) : Step
    }

    private var preparationRoundId: String? = null
    private var status: ShufflePreparationStatus = ShufflePreparationStatus.SELECTING
    private var opensAtMillis: Long = 0L
    private var deadlineMillis: Long = 0L
    private var roundSeed: Long = 0L
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
            availablePoints = available
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
        val cost = safeSelection.cost()
        if (cost !in 0..60) return PlanCheck.Rejected("INVALID_COST")
        if (currentPoints < cost) return PlanCheck.Rejected("INSUFFICIENT_FUNDS")
        return PlanCheck.Accepted(cost, safeSelection)
    }

    fun recordPlan(seat: Int, selection: ShuffleSelection, cost: Int): RoundPreparationRecord {
        confirmed[seat] = true
        selections[seat] = selection
        reserved[seat] = cost
        return record(requireNotNull(preparationRoundId) { "Preparation is not active" })
    }

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
        for (seat in 0 until 4) {
            val isNative = seat in nativeAiSeats
            if (seat in automatedSeats && !confirmed[seat]) {
                if (isNative && aiMaySpend) {
                    val currentPoints = points.getOrElse(seat) { 0 }
                    val selection = when {
                        currentPoints >= 60 -> ShuffleSelection(
                            initialTargets = setOf(ShuffleTarget.CHINITSU_MAN),
                            drawTargets = setOf(ShuffleTarget.DAISANGEN)
                        )
                        currentPoints >= 40 -> ShuffleSelection(
                            initialTargets = setOf(ShuffleTarget.CHINITSU_MAN)
                        )
                        currentPoints >= 20 -> ShuffleSelection(
                            drawTargets = setOf(ShuffleTarget.DAISANGEN)
                        )
                        else -> ShuffleSelection()
                    }
                    confirmed[seat] = true
                    selections[seat] = selection
                    reserved[seat] = selection.cost()
                } else {
                    confirmed[seat] = true
                    selections[seat] = ShuffleSelection()
                    reserved[seat] = 0
                }
                aiUpdated = true
            }
        }

        if (now < deadlineMillis) {
            return if (aiUpdated) Step.RecordUpdated(record(prepId)) else Step.Waiting
        }

        status = ShufflePreparationStatus.GENERATING
        for (seat in 0 until 4) {
            if (!confirmed[seat]) {
                confirmed[seat] = true
                selections[seat] = ShuffleSelection()
                reserved[seat] = 0
            }
        }

        val record = record(prepId)
        val plans = (0 until 4).map { seat ->
            SeatShufflePlan(seat, record.selections.getOrNull(seat) ?: ShuffleSelection())
        }
        val tiles = try {
            ShuffleWallGenerator().generate(
                seed = record.seed,
                dealerSeat = dealerSeat,
                plans = plans,
                config = config
            ).takeIf { it.size == 136 }
        } catch (t: Throwable) {
            null
        }
        return Step.Ready(prepId, record, plans, tiles)
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
        confirmed.fill(false)
        selections.fill(null)
        reserved.fill(0)
    }

    private fun record(roundId: String): RoundPreparationRecord = RoundPreparationRecord(
        roundId = roundId,
        seed = roundSeed,
        selections = selections.map { it?.deepCopy() },
        confirmed = confirmed.toList(),
        reserved = reserved.toList()
    )
}

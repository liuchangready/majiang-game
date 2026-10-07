package com.majiang.presentation.game

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.ShufflePlan
import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleSelection
import com.majiang.client.api.ShuffleTarget
import java.util.UUID

/**
 * 洗牌千术准备阶段纯表现层状态模型 (V4 presentation.game)。
 *
 * 遵循契约与纯客户端模型规范：
 * 1. 严格使用 client.api 视图模型 (ShufflePreparationView, ShuffleSelection, ShufflePlan, ShuffleTarget)；
 * 2. 独立于图形引擎与权威宿主，支持无头单元测试；
 * 3. 维护草稿选择（偷天换日 initialTargets，瞒天过海 drawTargets）；
 * 4. 实时计算费用（偷天换日 40，瞒天过海 20，共选 60，均不选 0）；
 * 5. 区分草稿预测扣费与权威预留：权威确认后 availablePoints 已扣除预留，不重复二次扣减；
 * 6. 校验预算超支（允许编辑超支草稿，但禁用确认并提供原因）；
 * 7. 单局生命周期与跨局重置：新局完全重置草稿与本地/权威确认状态；同局快照保留草稿；
 * 8. 重连恢复权威已确认选择；
 * 9. 暂停、非 SELECTING 状态、超时截止严格阻断编辑与提交；
 * 10. 默认单调时钟 System.nanoTime()/1_000_000，支持显式系统时间参数供无头确定性测试。
 */
class ShufflePreparationModel(
    private val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
) {
    /** 当前局标识 */
    var roundId: String? = null
        private set

    /** 当前控制代次 */
    var controlEpoch: Long = 0L
        private set

    /** 当前准备状态 (SELECTING / GENERATING)，未进入准备阶段为 null */
    var status: ShufflePreparationStatusView? = null
        private set

    /** 权威服务器是否已确认千术方案 */
    var isAuthorityConfirmed: Boolean = false
        private set

    /** 本地回执已记录但尚未体现在权威快照中的确认状态 */
    var isLocallyConfirmed: Boolean = false
        private set

    /** 本地异步提交请求是否在途 */
    var isSubmitting: Boolean = false
        private set

    /** 当前在途请求标识 */
    var submittingRequestId: String? = null
        private set

    /** 可用出千点数（权威服务器视角，若权威已确认则已扣除预留费用） */
    var availablePoints: Int = 0
        private set

    /** 权威已确认或本地成功记录的方案（若有） */
    var confirmedSelection: ShuffleSelection? = null
        private set

    /** 偷天换日草稿目标集合（影响起手配牌，非空消耗 40 点） */
    val initialTargets: MutableSet<ShuffleTarget> = LinkedHashSet()

    /** 瞒天过海草稿目标集合（影响前 8 次摸牌顺位，非空消耗 20 点） */
    val drawTargets: MutableSet<ShuffleTarget> = LinkedHashSet()

    /** 错误或拒绝提示信息 */
    var errorMessage: String? = null

    /** 权威时间样本与本地接收时间（用于本地单调流逝时间推算倒计时） */
    var opensAtMillis: Long = 0L
        private set
    var deadlineMillis: Long = 0L
        private set
    var serverNowMillis: Long = 0L
        private set
    var localReceiveTimeMillis: Long = 0L
        private set
    var isPaused: Boolean = false
        private set

    /** 偷天换日消耗点数（非空 40 点，空 0 点） */
    val initialCost: Int get() = if (initialTargets.isNotEmpty()) 40 else 0

    /** 瞒天过海消耗点数（非空 20 点，空 0 点） */
    val drawCost: Int get() = if (drawTargets.isNotEmpty()) 20 else 0

    /** 总消耗点数 (0, 20, 40, 60) */
    val totalCost: Int get() = initialCost + drawCost

    /**
     * 剩余点数预览：
     * - 权威已确认时：availablePoints 已经由权威扣除预留点数，直接返回 availablePoints；
     * - 本地已确认但快照在途时：从当前快照的 availablePoints 中扣除已确认方案的费用，防止瞬态二次扣减；
     * - 草稿编辑期（未确认）：从当前 availablePoints 中扣除草稿预计费用 totalCost。
     */
    val remainingPointsPreview: Int
        get() = when {
            isAuthorityConfirmed -> availablePoints
            isLocallyConfirmed -> availablePoints - (confirmedSelection?.cost() ?: 0)
            else -> availablePoints - totalCost
        }

    /**
     * 点数是否可承受：
     * - 已确认（权威或本地）时：方案已锁定，必然处于可承受状态，不误报点数不足；
     * - 未确认时：草稿总消耗 <= 当前可用点数。
     */
    val isAffordable: Boolean
        get() = when {
            isAuthorityConfirmed || isLocallyConfirmed -> true
            else -> totalCost <= availablePoints
        }

    /** 是否已锁定（权威确认、本地确认、正在提交或正在生成牌山，禁止再修改或再次提交） */
    val isLocked: Boolean
        get() = isAuthorityConfirmed || isLocallyConfirmed || isSubmitting || status == ShufflePreparationStatusView.GENERATING

    /** 是否正处于准备中（未结束） */
    val isActive: Boolean
        get() = status != null

    /** 倒计时是否已到期（serverNow 已达到或超过 deadline，或本地插值推算已归零） */
    val isTimeExpired: Boolean
        get() = deadlineMillis > 0L && (serverNowMillis >= deadlineMillis || remainingSeconds() <= 0L)

    /** 是否可以编辑草稿（处于 SELECTING 状态、未锁定、未暂停、未到期） */
    val canEditDraft: Boolean
        get() = isActive &&
                status == ShufflePreparationStatusView.SELECTING &&
                !isPaused &&
                !isLocked &&
                !isTimeExpired

    /** 是否可以确认提交 */
    val canConfirm: Boolean
        get() = !isLocked &&
                status == ShufflePreparationStatusView.SELECTING &&
                !isPaused &&
                !isTimeExpired &&
                isAffordable

    /** 是否可以跳过（不使用千术） */
    val canSkip: Boolean
        get() = !isLocked &&
                status == ShufflePreparationStatusView.SELECTING &&
                !isPaused &&
                !isTimeExpired

    /** 确认按钮不可用时的禁用原因提示 */
    val confirmDisabledReason: String?
        get() = when {
            status == null -> "未开放选择"
            isPaused -> "对局已暂停"
            isAuthorityConfirmed || isLocallyConfirmed -> "已确认"
            isSubmitting -> "提交中..."
            status == ShufflePreparationStatusView.GENERATING -> "洗牌生成中..."
            status != ShufflePreparationStatusView.SELECTING -> "未开放选择"
            isTimeExpired -> "准备时间已到"
            !isAffordable -> "点数不足"
            else -> null
        }

    /** 是否展示混合倾向提示：“多选为混合倾向，不保证同时成牌” */
    val hasMixedTargetsNotice: Boolean
        get() = initialTargets.size > 1 || drawTargets.size > 1

    /** 是否展示摸牌顺位提示：“吃碰杠会改变预期摸牌” */
    val hasDrawNotice: Boolean
        get() = drawTargets.isNotEmpty()

    /** 切换偷天换日目标 */
    fun toggleInitialTarget(target: ShuffleTarget): Boolean {
        if (!canEditDraft) return false
        if (initialTargets.contains(target)) {
            initialTargets.remove(target)
        } else {
            initialTargets.add(target)
        }
        errorMessage = null
        return true
    }

    /** 切换瞒天过海目标 */
    fun toggleDrawTarget(target: ShuffleTarget): Boolean {
        if (!canEditDraft) return false
        if (drawTargets.contains(target)) {
            drawTargets.remove(target)
        } else {
            drawTargets.add(target)
        }
        errorMessage = null
        return true
    }

    /** 清空草稿目标 */
    fun clearDraft() {
        if (!canEditDraft) return
        initialTargets.clear()
        drawTargets.clear()
        errorMessage = null
    }

    /** 当前草稿方案 */
    fun draftSelection(): ShuffleSelection = ShuffleSelection(
        initialTargets = LinkedHashSet(initialTargets),
        drawTargets = LinkedHashSet(drawTargets)
    )

    /** 标记提交中 */
    fun markSubmitting(requestId: String? = null) {
        isSubmitting = true
        submittingRequestId = requestId
        errorMessage = null
    }

    /** 提交被拒绝或异常时重置提交态 */
    fun onSubmissionRejected(reason: String?) {
        onSubmissionRejected(null, reason)
    }

    /** 提交被拒绝或异常时重置提交态（带 requestId 校验） */
    fun onSubmissionRejected(requestId: String?, reason: String?) {
        if (submittingRequestId != null && requestId != null && submittingRequestId != requestId) return
        isSubmitting = false
        submittingRequestId = null
        errorMessage = reason
    }

    /** 提交成功记录时标记 */
    fun onSubmissionAccepted() {
        onSubmissionAccepted(null, draftSelection())
    }

    /** 提交成功记录时标记（传入实际提交方案，跳过时清除草稿） */
    fun onSubmissionAccepted(requestId: String?, selection: ShuffleSelection) {
        if (submittingRequestId != null && requestId != null && submittingRequestId != requestId) return
        isSubmitting = false
        submittingRequestId = null
        isLocallyConfirmed = true
        confirmedSelection = selection
        initialTargets.clear()
        initialTargets.addAll(selection.initialTargets)
        drawTargets.clear()
        drawTargets.addAll(selection.drawTargets)
        errorMessage = null
    }

    /** 构造出千提交请求 */
    fun buildPlanRequest(requestId: String = UUID.randomUUID().toString()): ShufflePlan? {
        val currentRound = roundId ?: return null
        if (!canConfirm) return null
        return ShufflePlan(
            requestId = requestId,
            roundId = currentRound,
            controlEpoch = controlEpoch,
            selection = draftSelection()
        )
    }

    /** 构造不使用千术（跳过）请求 */
    fun buildSkipRequest(requestId: String = UUID.randomUUID().toString()): ShufflePlan? {
        val currentRound = roundId ?: return null
        if (!canSkip) return null
        return ShufflePlan(
            requestId = requestId,
            roundId = currentRound,
            controlEpoch = controlEpoch,
            selection = ShuffleSelection(emptySet(), emptySet())
        )
    }

    /** 计算剩余秒数（权威 serverNow + 本地单调流逝时间，暂停冻结） */
    fun remainingSeconds(nowSystemMillis: Long = monotonicClock()): Long {
        if (status == ShufflePreparationStatusView.GENERATING) return 0L
        if (deadlineMillis <= 0L) return 0L
        val elapsed = if (isPaused) 0L else maxOf(0L, nowSystemMillis - localReceiveTimeMillis)
        val estServerNow = serverNowMillis + elapsed
        val remainingMillis = maxOf(0L, deadlineMillis - estServerNow)
        return if (remainingMillis > 0L) (remainingMillis + 999L) / 1000L else 0L
    }

    /**
     * 根据权威视图更新模型。
     */
    fun update(
        prep: ShufflePreparationView?,
        paused: Boolean = false,
        epoch: Long = 0L,
        nowSystemMillis: Long = monotonicClock()
    ) {
        if (prep == null) {
            status = null
            isAuthorityConfirmed = false
            isLocallyConfirmed = false
            confirmedSelection = null
            isSubmitting = false
            submittingRequestId = null
            errorMessage = null
            initialTargets.clear()
            drawTargets.clear()
            return
        }

        val isNewRound = (roundId != prep.roundId)
        if (isNewRound) {
            roundId = prep.roundId
            initialTargets.clear()
            drawTargets.clear()
            isSubmitting = false
            submittingRequestId = null
            isAuthorityConfirmed = false
            isLocallyConfirmed = false
            confirmedSelection = null
            errorMessage = null
        }

        controlEpoch = epoch
        status = prep.status
        availablePoints = prep.availablePoints
        opensAtMillis = prep.opensAtMillis

        val isAuthoritySampleChanged = isNewRound ||
                prep.serverNowMillis != serverNowMillis ||
                prep.deadlineMillis != deadlineMillis ||
                paused != isPaused

        if (isAuthoritySampleChanged) {
            deadlineMillis = prep.deadlineMillis
            serverNowMillis = prep.serverNowMillis
            localReceiveTimeMillis = nowSystemMillis
            isPaused = paused
        }

        if (prep.confirmed) {
            isAuthorityConfirmed = true
            isLocallyConfirmed = false
            isSubmitting = false
            submittingRequestId = null
            val selected = prep.selection
            if (selected != null) {
                val initialMapped = selected.initialTargets.mapNotNull {
                    try { ShuffleTarget.valueOf(it.name) } catch (_: Throwable) { null }
                }.toSet()
                val drawMapped = selected.drawTargets.mapNotNull {
                    try { ShuffleTarget.valueOf(it.name) } catch (_: Throwable) { null }
                }.toSet()
                val clientSelection = ShuffleSelection(initialMapped, drawMapped)
                confirmedSelection = clientSelection
                initialTargets.clear()
                initialTargets.addAll(initialMapped)
                drawTargets.clear()
                drawTargets.addAll(drawMapped)
            }
        } else {
            isAuthorityConfirmed = false
        }
    }

    /** 便捷快照更新 */
    fun update(snapshot: GameSnapshotView, nowSystemMillis: Long = monotonicClock()) {
        update(
            prep = snapshot.preparation,
            paused = snapshot.paused,
            epoch = snapshot.decision.controlEpoch,
            nowSystemMillis = nowSystemMillis
        )
    }
}

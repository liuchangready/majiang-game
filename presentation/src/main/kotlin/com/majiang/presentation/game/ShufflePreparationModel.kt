package com.majiang.presentation.game

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.ShufflePlan
import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleSelection
import com.majiang.client.api.ShuffleTarget
import java.util.UUID

/**
 * 洗牌千术准备阶段纯表现层状态模型 (V4 presentation.game) —— 统一码牌千术。
 *
 * 遵循契约与纯客户端模型规范：
 * 1. 严格使用 client.api 视图模型 (ShufflePreparationView, ShuffleSelection, ShufflePlan, ShuffleTarget)；
 * 2. 独立于图形引擎与权威宿主，支持无头单元测试；
 * 3. 默认只显示右侧紧凑「本局准备」栏，**不自动展开六种目标**；
 * 4. 只有玩家主动点「偷天换日 · 30点」才进入 [PreparationPage.DRAWER]，
 *    目标必须用户手选（不默认清一色），只编辑不提交；
 * 5. 维护草稿选择（唯一目标 target 与看破 seeThrough），
 *    实时计算费用（统一目标 30，看破 10，合计最高 40，均不选 0）；
 * 6. 区分草稿预测扣费与权威预留：权威确认后 availablePoints 已扣除预留，不重复二次扣减；
 * 7. 校验预算超支（允许编辑超支草稿，但禁用提交并提供原因）；
 * 8. 单局生命周期与跨局重置：新局完全重置草稿与本地/权威确认状态；同局快照保留草稿；
 * 9. 重连恢复权威已确认选择；未确认时回到默认准备栏，不从旧局缓存拼接选择；
 * 10. 暂停、非 SELECTING 状态、超时截止严格阻断编辑与提交；
 * 11. 提交失败必须恢复「发起提交的原页面」与合法草稿；
 * 12. 迟到回执（请求标识不匹配 / 新局已复位）不得污染当前局；
 * 13. 默认单调时钟 System.nanoTime()/1_000_000，支持显式系统时间参数供无头确定性测试。
 */
class ShufflePreparationModel(
    private val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
) {

    /** 准备阶段当前页面。 */
    enum class PreparationPage {
        /** 右侧紧凑准备栏（默认）。 */
        PANEL,

        /** 目标抽屉（六选一）。 */
        DRAWER
    }

    /** 准备栏主要动作类型。 */
    enum class PreparationPrimaryAction {
        /** 默认零费用开局。 */
        START_ZERO_COST,

        /** 仅看破（10 点）。 */
        CONFIRM_SEE_THROUGH,

        /** 偷天换日目标（30 点，或叠加看破 40 点）。 */
        CONFIRM_TARGET
    }

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

    /** 发起在途请求时所在的页面（失败时恢复原页面）。 */
    var submittingOriginPage: PreparationPage = PreparationPage.PANEL
        private set

    /** 当前页面（准备栏 / 目标抽屉）。 */
    var page: PreparationPage = PreparationPage.PANEL
        private set

    /** 可用出千点数（权威服务器视角，若权威已确认则已扣除预留费用） */
    var availablePoints: Int = 0
        private set

    /** 权威已确认或本地成功记录的方案（若有） */
    var confirmedSelection: ShuffleSelection? = null
        private set

    /** 统一码牌目标草稿（null 表示不做牌；单选） */
    var target: ShuffleTarget? = null
        private set

    /** 当前房间是否允许看破；禁用房间不显示可提交入口 */
    var seeThroughEnabled: Boolean = false
        private set

    /** 看破固定价格（权威准备视图下发，正常恒为 10） */
    var seeThroughPrice: Int = 10
        private set

    /** 草稿是否勾选看破 */
    var isSeeThroughSelected: Boolean = false
        private set

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

    /** 统一目标消耗点数（非空 30 点，空 0 点） */
    val targetCost: Int get() = if (target != null) ShuffleSelection.TARGET_COST else 0

    /** 看破消耗点数（勾选 10 点，未勾选 0 点） */
    val seeThroughCost: Int get() = if (isSeeThroughSelected) seeThroughPrice else 0

    /** 总消耗点数 (0..40) */
    val totalCost: Int get() = targetCost + seeThroughCost

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

    /** 本人方案是否已确认（本地回执或权威快照任一成立）。 */
    val isPlanConfirmed: Boolean
        get() = isAuthorityConfirmed || isLocallyConfirmed

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

    /** 是否可以确认提交（提交带目标或看破的方案） */
    val canConfirm: Boolean
        get() = !isLocked &&
                status == ShufflePreparationStatusView.SELECTING &&
                !isPaused &&
                !isTimeExpired &&
                isAffordable

    /** 是否可以跳过（不使用千术，零费用） */
    val canSkip: Boolean
        get() = !isLocked &&
                status == ShufflePreparationStatusView.SELECTING &&
                !isPaused &&
                !isTimeExpired

    /** 抽屉内「确认施术」是否可用：必须已经手选目标。 */
    val canConfirmTarget: Boolean
        get() = canConfirm && target != null

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

    /** 抽屉确认按钮不可用时的禁用原因提示（没有目标时必须先选牌型）。 */
    val targetConfirmDisabledReason: String?
        get() = when {
            confirmDisabledReason != null -> confirmDisabledReason
            target == null -> "先选择一个目标牌型"
            else -> null
        }

    /** 是否展示摸牌顺位提示：“吃碰杠/提前终局会改变预期摸牌”（已选统一目标即作用于起手+摸牌） */
    val hasDrawNotice: Boolean
        get() = target != null

    /** 是否展示看破说明（仅启用房间） */
    val hasSeeThroughNotice: Boolean
        get() = seeThroughEnabled

    /** 准备栏主要动作类型。 */
    val primaryAction: PreparationPrimaryAction
        get() = when {
            target != null -> PreparationPrimaryAction.CONFIRM_TARGET
            isSeeThroughSelected -> PreparationPrimaryAction.CONFIRM_SEE_THROUGH
            else -> PreparationPrimaryAction.START_ZERO_COST
        }

    /** 准备栏主要动作文案（费用随草稿动态变化）。 */
    val primaryActionLabel: String
        get() = when (primaryAction) {
            PreparationPrimaryAction.START_ZERO_COST -> "开始本局 · 0点"
            PreparationPrimaryAction.CONFIRM_SEE_THROUGH -> "确认看破 · ${seeThroughCost}点"
            PreparationPrimaryAction.CONFIRM_TARGET -> "确认施术 · ${totalCost}点"
        }

    /** 准备栏主要动作当前是否可提交。 */
    val canSubmitPrimary: Boolean
        get() = when (primaryAction) {
            PreparationPrimaryAction.START_ZERO_COST -> canSkip
            else -> canConfirm
        }

    /** 抽屉确认按钮文案。 */
    val drawerConfirmLabel: String
        get() = "确认施术 · ${totalCost}点"

    /**
     * 自己已确认后的准确阶段反馈。
     *
     * 本地受理不代表发牌完成：确认后必须继续显示“等待其他玩家完成准备”或“正在发牌”，
     * 由权威 preparation 结束快照解除遮罩，不能本地关闭并假设已经发牌。
     */
    val confirmedFeedbackText: String?
        get() = when {
            status == ShufflePreparationStatusView.GENERATING -> "正在发牌…"
            isSubmitting -> "正在确认方案…"
            isAuthorityConfirmed || isLocallyConfirmed -> "等待其他玩家完成准备"
            else -> null
        }

    /** 确认回执标题（只读本人方案）。 */
    val confirmedReceiptTitle: String
        get() = "方案已确认"

    /** 选择/取消唯一目标（单选；再次点击已选目标即取消） */
    fun selectTarget(newTarget: ShuffleTarget): Boolean {
        if (!canEditDraft) return false
        target = if (target == newTarget) null else newTarget
        errorMessage = null
        return true
    }

    /** 切换看破草稿（禁用房间或不可编辑时拒绝） */
    fun toggleSeeThrough(): Boolean {
        if (!canEditDraft || !seeThroughEnabled) return false
        isSeeThroughSelected = !isSeeThroughSelected
        errorMessage = null
        return true
    }

    /** 清空草稿目标与看破勾选 */
    fun clearDraft() {
        if (!canEditDraft) return
        target = null
        isSeeThroughSelected = false
        errorMessage = null
    }

    /** 清空全部草稿（目标 + 看破），用于「直接开局 · 0点」。 */
    fun clearAllDraft() {
        target = null
        isSeeThroughSelected = false
        errorMessage = null
    }

    /**
     * 当前草稿快照。
     *
     * 供「直接开局 · 0点」这类“先清空再提交”的动作在提交失败时回滚草稿。
     */
    fun captureDraft(): ShuffleSelection = ShuffleSelection(target, isSeeThroughSelected)

    /**
     * 恢复草稿（提交失败回滚用）。
     *
     * 只有在既未确认、也未在途提交时才允许恢复：
     * 已确认说明权威已接受方案，绝不能被回滚覆盖；在途提交期间也不得改写草稿。
     *
     * @param roundId 发起该草稿的局标识：**跨局迟到回执不得把旧局草稿写进新局**。
     */
    fun restoreDraft(snapshot: ShuffleSelection, roundId: String): Boolean {
        if (isAuthorityConfirmed || isLocallyConfirmed || isSubmitting) return false
        if (this.roundId != roundId) return false
        target = snapshot.target
        isSeeThroughSelected = snapshot.seeThrough
        return true
    }

    /**
     * 判断某个异步回执是否仍然属于本局、且没有被别的请求取代。
     *
     * 用于丢弃**跨局迟到回执**：旧局的成功/失败回执到达时，新局可能已经开始，
     * 此时任何面板副作用（锁存回执、收回抽屉、回滚草稿、显示错误）都会污染新局。
     *
     * 判定条件：
     * 1. 必须属于当前局（[roundId] 一致）；
     * 2. 当前若还有在途请求，则必须就是这一个（防止同局内被后续请求取代）。
     */
    fun isCurrentSubmissionFor(requestId: String, roundId: String): Boolean {
        if (this.roundId != roundId) return false
        val inFlight = submittingRequestId
        if (inFlight != null && inFlight != requestId) return false
        return true
    }

    /**
     * 展开目标抽屉：只切换页面，不扣点、不提交、不改变目标草稿。
     *
     * 抽屉打开后仍不预选任何牌型（默认不是清一色）。
     */
    fun openTargetDrawer(): Boolean {
        if (!canEditDraft) return false
        page = PreparationPage.DRAWER
        errorMessage = null
        return true
    }

    /**
     * 收起目标抽屉：清空目标草稿、保留看破勾选、不提交任何请求。
     *
     * 重新打开时仍然没有默认目标。
     */
    fun closeTargetDrawer(): Boolean {
        if (page != PreparationPage.DRAWER) return false
        page = PreparationPage.PANEL
        target = null
        errorMessage = null
        return true
    }

    /** 当前草稿方案（仅看破也是合法非空方案） */
    fun draftSelection(): ShuffleSelection = ShuffleSelection(
        target = target,
        seeThrough = isSeeThroughSelected
    )

    /** 标记提交中（记录发起页面，失败时恢复原页面） */
    fun markSubmitting(requestId: String? = null, originPage: PreparationPage = page) {
        isSubmitting = true
        submittingRequestId = requestId
        submittingOriginPage = originPage
        errorMessage = null
    }

    /** 提交被拒绝或异常时重置提交态 */
    fun onSubmissionRejected(reason: String?) {
        onSubmissionRejected(null, reason)
    }

    /**
     * 提交被拒绝或异常时重置提交态（带 requestId 校验）。
     *
     * 迟到回执保护：请求标识与在途请求不一致，或已经没有在途请求（例如新局已复位）时，
     * 一律忽略，不得污染当前局的错误提示与页面。
     */
    fun onSubmissionRejected(requestId: String?, reason: String?) {
        if (!isCurrentSubmission(requestId)) return
        isSubmitting = false
        submittingRequestId = null
        // 失败恢复发起提交的原页面与合法草稿（抽屉提交失败不能丢回初始页）。
        page = submittingOriginPage
        errorMessage = reason
    }

    /** 提交成功记录时标记 */
    fun onSubmissionAccepted() {
        onSubmissionAccepted(null, draftSelection())
    }

    /** 提交成功记录时标记（传入实际提交方案，跳过时清除草稿） */
    fun onSubmissionAccepted(requestId: String?, selection: ShuffleSelection) {
        if (!isCurrentSubmission(requestId)) return
        isSubmitting = false
        submittingRequestId = null
        isLocallyConfirmed = true
        confirmedSelection = selection
        target = selection.target
        isSeeThroughSelected = selection.seeThrough
        // 成功后抽屉收起为只读回执（DESIGN §5）。
        page = PreparationPage.PANEL
        errorMessage = null
    }

    private fun isCurrentSubmission(requestId: String?): Boolean {
        if (requestId != null) return submittingRequestId == requestId
        return submittingRequestId != null
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
            selection = ShuffleSelection()
        )
    }

    /**
     * 构造准备栏主要动作对应的请求。
     *
     * - 默认零费用：target=null, seeThrough=false；
     * - 仅看破：target=null, seeThrough=true；
     * - 已选目标：提交当前完整草稿。
     */
    fun buildPrimaryRequest(requestId: String = UUID.randomUUID().toString()): ShufflePlan? =
        when (primaryAction) {
            PreparationPrimaryAction.START_ZERO_COST -> buildSkipRequest(requestId)
            else -> buildPlanRequest(requestId)
        }

    /**
     * 构造抽屉确认请求：必须已经手选目标，否则返回 null（没目标禁确认）。
     */
    fun buildDrawerConfirmRequest(requestId: String = UUID.randomUUID().toString()): ShufflePlan? {
        if (!canConfirmTarget) return null
        return buildPlanRequest(requestId)
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
     * 重连 / 全量基线重建：回到默认准备栏，清空本地草稿，不从旧局缓存拼接选择。
     * 权威已确认状态由随后的权威快照覆盖。
     */
    fun onReconnect() {
        page = PreparationPage.PANEL
        target = null
        isSeeThroughSelected = false
        isSubmitting = false
        submittingRequestId = null
        errorMessage = null
    }

    /**
     * 确认成功后强制收回抽屉页面：只剩只读回执，不再保留任何抽屉交互。
     *
     * 迟到的权威快照可能已经把本地确认标记清掉，因此这里同时把页面收回准备栏，
     * 保证「方案已确认」出现时抽屉一定已经收起。
     */
    fun collapseToReceiptPage() {
        if (page != PreparationPage.PANEL) page = PreparationPage.PANEL
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
            target = null
            isSeeThroughSelected = false
            seeThroughEnabled = false
            page = PreparationPage.PANEL
            return
        }

        val isNewRound = (roundId != prep.roundId)
        if (isNewRound) {
            roundId = prep.roundId
            target = null
            isSeeThroughSelected = false
            isSubmitting = false
            submittingRequestId = null
            isAuthorityConfirmed = false
            isLocallyConfirmed = false
            confirmedSelection = null
            errorMessage = null
            page = PreparationPage.PANEL
        }

        controlEpoch = epoch
        status = prep.status
        availablePoints = prep.availablePoints
        opensAtMillis = prep.opensAtMillis
        seeThroughEnabled = prep.seeThroughEnabled
        seeThroughPrice = prep.seeThroughCost
        if (!seeThroughEnabled) {
            // 禁用房间的旧草稿不得携带看破；重连到禁用房间同样清除。
            isSeeThroughSelected = false
        }

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
                val mapped = selected.target?.let {
                    try { ShuffleTarget.valueOf(it.name) } catch (_: Throwable) { null }
                }
                confirmedSelection = ShuffleSelection(mapped, selected.seeThrough)
                target = mapped
                isSeeThroughSelected = selected.seeThrough
            }
            // 权威已确认 = 锁定回执，绝不回到可编辑页面。
            page = PreparationPage.PANEL
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

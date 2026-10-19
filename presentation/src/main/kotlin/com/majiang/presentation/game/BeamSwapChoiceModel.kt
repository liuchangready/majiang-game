package com.majiang.presentation.game

import com.majiang.client.api.BeamSwapAction
import com.majiang.client.api.BeamSwapChoiceRequest
import com.majiang.client.api.BeamSwapRequest
import com.majiang.client.api.BeamSwapSlot
import com.majiang.client.api.GameSnapshotView

/**
 * 纯偷梁换柱二选一决策模型 (BeamSwapChoiceModel)。
 *
 * 核心契约：
 * 1. 纯表现层模型，供无头契约测试与真实面板共用，只依赖 client.api；
 * 2. 权威快照 pendingOffer 驱动开启选择模式 (isChoosing)，不依赖乐观激活；
 * 3. 构造请求严格只读，不修改快照或状态；
 * 4. 选择 SWAP 必须同时选中原有暗手牌与目标槽位 (无序)，KEEP_ORIGINAL 无需预选；
 * 5. 身份、局、代次、决策、候选牌内容或可选手牌任一变化清空本地双选，但有效 offer 继续保持面板开启；
 * 6. 暂停、断线、托管使选择面板即时隐藏并清空双选，恢复后按最新权威快照恢复未选中状态；
 * 7. 倒计时归零后阻止确认与选择输入，但面板保持 isChoosing 持续占有模态，直到权威快照清除 offer。
 * 8. 委托 AuthorityCountdownTracker 治理权威时钟采样与本地流逝推算，委托 DecisionIdentityGuard 追踪快照代次与决策身份。
 */
class BeamSwapChoiceModel(
    val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
) {
    /** 权威倒计时追踪器委托 */
    val countdownTracker = AuthorityCountdownTracker(monotonicClock)

    /** 决策身份守卫委托 */
    val identityGuard = DecisionIdentityGuard()

    /** 是否处于偷梁换柱模态展示状态 */
    var isChoosing: Boolean = false
        private set

    /** 当前选中的原有暗手牌实体 ID */
    var selectedHandTileId: Int? = null
        private set

    /** 当前选中的目标牌山/王牌槽位 */
    var selectedTargetSlot: BeamSwapSlot? = null
        private set

    // 当前绑定的快照与连接环境状态
    private var currentSnapshot: GameSnapshotView? = null
    private var isPending: Boolean = false
    private var isConnected: Boolean = true

    // 本模型专属候选特征追踪
    private var lastSelectableHandTileIds: List<Int>? = null
    private var lastCandidatesSlots: List<BeamSwapSlot>? = null
    private var lastCandidatesMarks: List<Boolean>? = null
    private var lastCandidatesTileIds: List<Int?>? = null
    private var lastWasPaused: Boolean = false
    private var lastConnected: Boolean = true

    /** 剩余决策时间（毫秒，权威采样推估；暂停时返回 0） */
    val remainingMillis: Long
        get() {
            val snap = currentSnapshot ?: return 0L
            return countdownTracker.remainingMillis(isPaused = snap.paused)
        }

    /** 是否满足入口激活条件 */
    val canActivate: Boolean
        get() {
            val snap = currentSnapshot ?: return false
            val beam = snap.beamSwap ?: return false
            if (isChoosing || isPending || !isConnected || snap.paused) return false
            val viewerSeat = snap.viewerSeat
            val role = snap.controls.find { it.seat == viewerSeat }?.role
            if (role != null && role != "HUMAN") return false
            val points = snap.cheatPoints.getOrNull(viewerSeat) ?: 0
            if (points < beam.cost) return false
            if (remainingMillis <= 0L) return false
            return beam.canApply && beam.pendingOffer == null
        }

    /** 是否满足互换确认条件 */
    val canConfirm: Boolean
        get() {
            if (!isChoosing || isPending || !isConnected) return false
            val snap = currentSnapshot ?: return false
            if (snap.paused) return false
            val viewerSeat = snap.viewerSeat
            val role = snap.controls.find { it.seat == viewerSeat }?.role
            if (role != null && role != "HUMAN") return false
            if (remainingMillis <= 0L) return false
            return selectedHandTileId != null && selectedTargetSlot != null
        }

    /**
     * 更新快照与连接环境。
     */
    fun update(
        snapshot: GameSnapshotView?,
        connected: Boolean = true,
        isPending: Boolean = false,
        nowMillis: Long = monotonicClock()
    ) {
        this.isPending = isPending
        this.isConnected = connected

        if (snapshot == null) {
            cancel()
            currentSnapshot = null
            countdownTracker.clear()
            identityGuard.reset()
            lastConnected = connected
            return
        }

        val wasConnected = lastConnected
        lastConnected = connected

        val beam = snapshot.beamSwap
        val offer = beam?.pendingOffer

        // 检查身份代次、决策、候选牌或可选手牌是否变更
        val candidateChanged = offer?.offerId != identityGuard.lastExtraToken ||
            offer?.selectableHandTileIds != lastSelectableHandTileIds ||
            offer?.candidates?.map { it.slot } != lastCandidatesSlots ||
            offer?.candidates?.map { it.marked } != lastCandidatesMarks ||
            offer?.candidates?.map { it.tile?.tileId } != lastCandidatesTileIds

        val identityChanged = identityGuard.hasIdentity && (
            identityGuard.hasDecisionChanged(snapshot) || candidateChanged
        )

        val viewerSeat = snapshot.viewerSeat
        val role = snapshot.controls.find { it.seat == viewerSeat }?.role
        val isHuman = role == null || role == "HUMAN"

        // 暂停、断线、托管或 offer 清除时隐藏并重置选中；重连时清空选中
        val shouldChoose = offer != null && isHuman && !snapshot.paused && connected
        if (shouldChoose) {
            isChoosing = true
            if (identityChanged || !wasConnected) {
                selectedHandTileId = null
                selectedTargetSlot = null
            }
        } else {
            isChoosing = false
            selectedHandTileId = null
            selectedTargetSlot = null
        }

        val wasPaused = lastWasPaused
        val isPaused = snapshot.paused
        lastWasPaused = isPaused

        // 权威时钟采样：未暂停且未从暂停恢复时维持采样
        val isSameSnapshotIdentity = identityGuard.isSameSnapshot(snapshot, extraToken = offer?.offerId) &&
            !wasPaused && !isPaused

        val viewerClock = snapshot.clocks.find { it.seat == viewerSeat }
        val targetDeadline = offer?.deadlineMillis ?: viewerClock?.deadline ?: 0L
        if (viewerClock != null && targetDeadline > 0L && !isPaused) {
            countdownTracker.updateSample(
                serverNow = viewerClock.serverNow,
                deadline = targetDeadline,
                isSameIdentity = isSameSnapshotIdentity,
                isPaused = isPaused,
                nowMillis = nowMillis
            )
        } else {
            countdownTracker.clear()
        }

        this.currentSnapshot = snapshot
        this.identityGuard.record(snapshot, extraToken = offer?.offerId)
        this.lastSelectableHandTileIds = offer?.selectableHandTileIds
        this.lastCandidatesSlots = offer?.candidates?.map { it.slot }
        this.lastCandidatesMarks = offer?.candidates?.map { it.marked }
        this.lastCandidatesTileIds = offer?.candidates?.map { it.tile?.tileId }
    }

    /**
     * 选中原有暗手牌。
     */
    fun selectHand(tileId: Int): Boolean {
        if (!isChoosing) return false
        if (remainingMillis <= 0L) return false
        if (isPending || !isConnected) return false
        val snap = currentSnapshot ?: return false
        if (snap.paused) return false
        val offer = snap.beamSwap?.pendingOffer ?: return false
        if (tileId !in offer.selectableHandTileIds) return false
        selectedHandTileId = tileId
        return true
    }

    /**
     * 选中目标槽位。
     */
    fun selectTarget(slot: BeamSwapSlot): Boolean {
        if (!isChoosing) return false
        if (remainingMillis <= 0L) return false
        if (isPending || !isConnected) return false
        val snap = currentSnapshot ?: return false
        if (snap.paused) return false
        val offer = snap.beamSwap?.pendingOffer ?: return false
        if (offer.candidates.none { it.slot == slot }) return false
        selectedTargetSlot = slot
        return true
    }

    /**
     * 构造激活请求（只读）。
     */
    fun buildActivateRequest(requestId: String): BeamSwapRequest? {
        if (!canActivate) return null
        val snap = currentSnapshot ?: return null
        val beam = snap.beamSwap ?: return null

        return BeamSwapRequest(
            requestId = requestId,
            sessionId = snap.sessionId,
            roundId = snap.roundId ?: "",
            decisionId = beam.decisionId,
            controlEpoch = beam.controlEpoch
        )
    }

    /**
     * 构造选择请求（只读）。
     */
    fun buildChoiceRequest(requestId: String, action: BeamSwapAction): BeamSwapChoiceRequest? {
        if (!isChoosing) return null
        if (isPending || !isConnected) return null
        val snap = currentSnapshot ?: return null
        if (snap.paused) return null
        val role = snap.controls.find { it.seat == snap.viewerSeat }?.role
        if (role != null && role != "HUMAN") return null
        if (remainingMillis <= 0L) return null
        val beam = snap.beamSwap ?: return null
        val offer = beam.pendingOffer ?: return null

        return when (action) {
            BeamSwapAction.SWAP -> {
                val hand = selectedHandTileId ?: return null
                val target = selectedTargetSlot ?: return null
                BeamSwapChoiceRequest(
                    requestId = requestId,
                    sessionId = snap.sessionId,
                    roundId = snap.roundId ?: "",
                    decisionId = snap.decision.decisionId,
                    controlEpoch = snap.decision.controlEpoch,
                    offerId = offer.offerId,
                    action = BeamSwapAction.SWAP,
                    handTileId = hand,
                    targetSlot = target
                )
            }
            BeamSwapAction.KEEP_ORIGINAL -> {
                BeamSwapChoiceRequest(
                    requestId = requestId,
                    sessionId = snap.sessionId,
                    roundId = snap.roundId ?: "",
                    decisionId = snap.decision.decisionId,
                    controlEpoch = snap.decision.controlEpoch,
                    offerId = offer.offerId,
                    action = BeamSwapAction.KEEP_ORIGINAL,
                    handTileId = null,
                    targetSlot = null
                )
            }
        }
    }

    /**
     * 取消或清理本地选择状态。
     */
    fun cancel() {
        isChoosing = false
        selectedHandTileId = null
        selectedTargetSlot = null
        countdownTracker.clear()
    }
}

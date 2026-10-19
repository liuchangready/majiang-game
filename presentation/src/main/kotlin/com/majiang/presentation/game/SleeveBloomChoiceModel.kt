package com.majiang.presentation.game

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.SleeveBloomChoiceRequest
import com.majiang.client.api.SleeveBloomKeep
import com.majiang.client.api.SleeveBloomRequest

/**
 * 纯袖里藏花二选一决策模型 (SleeveBloomChoiceModel)。
 *
 * 核心契约：
 * 1. 纯表现层模型，供无头契约测试与真实面板共用，只依赖 client.api；
 * 2. 权威快照 pendingOffer 驱动开启选择模式 (isChoosing)，不依赖乐观激活；
 * 3. 构造请求严格只读，不修改快照或状态；
 * 4. 选择 FIRST（取消/保留第一张）无需预选，选择 SECOND 确认必须先有 selected；
 * 5. 身份、决策、代次或候选变化清空本地选中，但有效 offer 继续保持面板开启；
 * 6. 暂停、断线、托管使二选一即时隐藏并清空选中，恢复后按最新权威快照恢复未选中状态；
 * 7. 倒计时归零后阻止确认与选择输入，但面板保持 isChoosing 持续占有模态，直到权威快照清除 offer。
 * 8. 委托 AuthorityCountdownTracker 治理权威时钟采样与本地流逝推算，委托 DecisionIdentityGuard 追踪快照代次与决策身份。
 */
class SleeveBloomChoiceModel(
    val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
) {
    /** 权威倒计时追踪器委托 */
    val countdownTracker = AuthorityCountdownTracker(monotonicClock)

    /** 决策身份守卫委托 */
    val identityGuard = DecisionIdentityGuard()

    /** 是否处于二选一模态展示状态 */
    var isChoosing: Boolean = false
        private set

    /** 当前本地选中的候选目标 (FIRST / SECOND) */
    var selected: SleeveBloomKeep? = null
        private set

    // 当前绑定的快照与连接环境状态
    private var currentSnapshot: GameSnapshotView? = null
    private var isPending: Boolean = false
    private var isConnected: Boolean = true

    // 本模型专属候选牌特征追踪
    private var lastFirstTileId: Int? = null
    private var lastSecondTileId: Int? = null

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
            val bloom = snap.sleeveBloom ?: return false
            if (isChoosing || isPending || !isConnected || snap.paused) return false
            val viewerSeat = snap.viewerSeat
            val role = snap.controls.find { it.seat == viewerSeat }?.role
            if (role != null && role != "HUMAN") return false
            val points = snap.cheatPoints.getOrNull(viewerSeat) ?: 0
            if (points < bloom.cost) return false
            return bloom.canApply && bloom.pendingOffer == null
        }

    /** 是否满足提交确认条件（保留所选牌卡） */
    val canConfirm: Boolean
        get() {
            if (!isChoosing || selected == null || isPending || !isConnected) return false
            val snap = currentSnapshot ?: return false
            if (snap.paused) return false
            val viewerSeat = snap.viewerSeat
            val role = snap.controls.find { it.seat == viewerSeat }?.role
            if (role != null && role != "HUMAN") return false
            if (remainingMillis <= 0L) return false
            return true
        }

    /**
     * 更新快照与连接环境。
     */
    fun update(
        snapshot: GameSnapshotView?,
        pending: Boolean = false,
        connected: Boolean = true,
        nowMillis: Long = monotonicClock()
    ) {
        this.isPending = pending
        this.isConnected = connected

        if (snapshot == null || !connected) {
            cancel()
            currentSnapshot = null
            countdownTracker.clear()
            return
        }

        val bloom = snapshot.sleeveBloom
        val offer = bloom?.pendingOffer

        // 检查身份代次与候选牌实体是否变更
        val candidateChanged = offer?.offerId != identityGuard.lastExtraToken ||
            offer?.firstTile?.tileId != lastFirstTileId ||
            offer?.secondTile?.tileId != lastSecondTileId

        val identityChanged = identityGuard.hasIdentity && (
            identityGuard.hasDecisionChanged(snapshot) || candidateChanged
        )

        if (identityChanged) {
            selected = null
        }

        val viewerSeat = snapshot.viewerSeat
        val role = snapshot.controls.find { it.seat == viewerSeat }?.role
        val isHuman = role == null || role == "HUMAN"

        // 暂停、断线、托管或 offer 清除时隐藏并重置选中
        val shouldChoose = offer != null && isHuman && !snapshot.paused && connected
        if (shouldChoose) {
            isChoosing = true
        } else {
            isChoosing = false
            selected = null
        }

        // 权威时钟采样：仅在全新快照代次或采样为空时初始化采样，重复刷新同一快照不重置采样
        val isSameSnapshotIdentity = identityGuard.isSameSnapshot(snapshot, extraToken = offer?.offerId)

        val viewerClock = snapshot.clocks.find { it.seat == viewerSeat }
        if (viewerClock != null && offer != null && !snapshot.paused) {
            countdownTracker.updateSample(
                serverNow = viewerClock.serverNow,
                deadline = offer.deadlineMillis,
                isSameIdentity = isSameSnapshotIdentity,
                isPaused = snapshot.paused,
                nowMillis = nowMillis
            )
        } else {
            countdownTracker.clear()
        }

        this.currentSnapshot = snapshot
        this.identityGuard.record(snapshot, extraToken = offer?.offerId)
        this.lastFirstTileId = offer?.firstTile?.tileId
        this.lastSecondTileId = offer?.secondTile?.tileId
    }

    /**
     * 选中保留目标 (FIRST 或 SECOND)。
     */
    fun select(keep: SleeveBloomKeep): Boolean {
        if (!isChoosing) return false
        if (remainingMillis <= 0L) return false
        if (isPending || !isConnected) return false
        val snap = currentSnapshot ?: return false
        if (snap.paused) return false
        selected = keep
        return true
    }

    /**
     * 构造激活请求（只读，若不可激活则返回 null）。
     */
    fun buildActivateRequest(requestId: String): SleeveBloomRequest? {
        if (!canActivate) return null
        val snap = currentSnapshot ?: return null
        val bloom = snap.sleeveBloom ?: return null

        return SleeveBloomRequest(
            requestId = requestId,
            sessionId = snap.sessionId,
            roundId = snap.roundId ?: "",
            decisionId = bloom.decisionId,
            controlEpoch = bloom.controlEpoch
        )
    }

    /**
     * 构造二选一请求（只读，若不可提交则返回 null）。
     */
    fun buildChoiceRequest(requestId: String, keep: SleeveBloomKeep): SleeveBloomChoiceRequest? {
        if (!isChoosing) return null
        if (isPending || !isConnected) return null
        val snap = currentSnapshot ?: return null
        if (snap.paused) return null
        val role = snap.controls.find { it.seat == snap.viewerSeat }?.role
        if (role != null && role != "HUMAN") return null
        if (remainingMillis <= 0L) return null
        val bloom = snap.sleeveBloom ?: return null
        val offer = bloom.pendingOffer ?: return null

        // 保留第二张必须先有选中 SECOND；取消/保留第一张不要求预先选中
        if (keep == SleeveBloomKeep.SECOND && selected != SleeveBloomKeep.SECOND) return null

        return SleeveBloomChoiceRequest(
            requestId = requestId,
            sessionId = snap.sessionId,
            roundId = snap.roundId ?: "",
            decisionId = snap.decision.decisionId,
            controlEpoch = snap.decision.controlEpoch,
            offerId = offer.offerId,
            keep = keep
        )
    }

    /**
     * 取消或清理本地选择状态。
     */
    fun cancel() {
        isChoosing = false
        selected = null
        countdownTracker.clear()
    }
}

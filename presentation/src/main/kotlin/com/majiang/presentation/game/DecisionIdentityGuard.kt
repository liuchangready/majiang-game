package com.majiang.presentation.game

import com.majiang.client.api.GameSnapshotView

/**
 * 权威决策身份守卫 (DecisionIdentityGuard)。
 *
 * 核心契约：
 * 1. 追踪快照会话、代次与决策身份 (sessionId, syncEpoch, roundId, decisionId, controlEpoch, cursor)；
 * 2. 识别决策身份变更：当会话、局、决策 ID 或控制代次任一发生变化时，判断为身份变更；
 * 3. 识别完全一致的快照身份：供倒计时采样等逻辑判断是否为同一快照的重复帧刷新，避免重置流逝采样；
 * 4. 纯表现层模型委托组件，只依赖 client.api，不含任何 engine/server/protocol 依赖。
 */
class DecisionIdentityGuard {
    /** 上次记录的会话标识 */
    var lastSessionId: String? = null
        private set

    /** 上次记录的同步代次 */
    var lastSyncEpoch: String? = null
        private set

    /** 上次记录的单局标识 */
    var lastRoundId: String? = null
        private set

    /** 上次记录的决策标识 */
    var lastDecisionId: String? = null
        private set

    /** 上次记录的控制代次 */
    var lastControlEpoch: Long? = null
        private set

    /** 上次记录的游标位置 */
    var lastCursor: Long? = null
        private set

    /** 上次记录的专属特征标记 (如 offerId) */
    var lastExtraToken: Any? = null
        private set

    /** 是否已建立有效身份基准 */
    val hasIdentity: Boolean
        get() = lastSessionId != null

    /**
     * 判断核心决策身份 (sessionId, syncEpoch, roundId, decisionId, controlEpoch) 是否发生变更。
     * 若此前尚未记录身份（hasIdentity == false），返回 false（首次初始化由调用方决定行为）。
     */
    fun hasDecisionChanged(snapshot: GameSnapshotView): Boolean {
        if (!hasIdentity) return false
        return snapshot.sessionId != lastSessionId ||
            snapshot.syncEpoch != lastSyncEpoch ||
            snapshot.roundId != lastRoundId ||
            snapshot.decision.decisionId != lastDecisionId ||
            snapshot.decision.controlEpoch != lastControlEpoch
    }

    /**
     * 判断核心决策身份是否发生变更（字段重载）。
     */
    fun hasDecisionChanged(
        sessionId: String,
        syncEpoch: String,
        roundId: String?,
        decisionId: String?,
        controlEpoch: Long?
    ): Boolean {
        if (!hasIdentity) return false
        return sessionId != lastSessionId ||
            syncEpoch != lastSyncEpoch ||
            roundId != lastRoundId ||
            decisionId != lastDecisionId ||
            controlEpoch != lastControlEpoch
    }

    /**
     * 判断当前快照是否与此前记录处于完全相同的快照身份（包含 cursor 与可选 extraToken）。
     */
    fun isSameSnapshot(
        snapshot: GameSnapshotView,
        extraToken: Any? = null
    ): Boolean {
        return lastSessionId == snapshot.sessionId &&
            lastSyncEpoch == snapshot.syncEpoch &&
            lastRoundId == snapshot.roundId &&
            lastDecisionId == snapshot.decision.decisionId &&
            lastControlEpoch == snapshot.decision.controlEpoch &&
            lastCursor == snapshot.cursor &&
            lastExtraToken == extraToken
    }

    /**
     * 记录最新快照身份与专属特征。
     */
    fun record(
        snapshot: GameSnapshotView,
        extraToken: Any? = null
    ) {
        lastSessionId = snapshot.sessionId
        lastSyncEpoch = snapshot.syncEpoch
        lastRoundId = snapshot.roundId
        lastDecisionId = snapshot.decision.decisionId
        lastControlEpoch = snapshot.decision.controlEpoch
        lastCursor = snapshot.cursor
        lastExtraToken = extraToken
    }

    /**
     * 纯字段记录重载。
     */
    fun record(
        sessionId: String?,
        syncEpoch: String?,
        roundId: String?,
        decisionId: String?,
        controlEpoch: Long?,
        cursor: Long? = null,
        extraToken: Any? = null
    ) {
        lastSessionId = sessionId
        lastSyncEpoch = syncEpoch
        lastRoundId = roundId
        lastDecisionId = decisionId
        lastControlEpoch = controlEpoch
        lastCursor = cursor
        lastExtraToken = extraToken
    }

    /**
     * 重置所有身份字段为 null。
     */
    fun reset() {
        lastSessionId = null
        lastSyncEpoch = null
        lastRoundId = null
        lastDecisionId = null
        lastControlEpoch = null
        lastCursor = null
        lastExtraToken = null
    }
}

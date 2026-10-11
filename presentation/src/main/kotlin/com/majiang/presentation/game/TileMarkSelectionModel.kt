package com.majiang.presentation.game

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.TileMarkRequest

/**
 * 纯落汗选牌模型 (TileMarkSelectionModel)。
 *
 * 核心契约：
 * 1. 纯表现层模型，供无头契约测试与真实面板共用；
 * 2. 选牌按实体 tileId 保存，手牌排序不改变已选择牌；
 * 3. 恰好 4 张实体牌可确认，拒绝第 5 张，拒绝非当前可选候选；
 * 4. 身份变化（sessionId/syncEpoch/roundId/decisionId/controlEpoch 或候选集合变化）清空选择；
 * 5. 暂停、断线、托管、deadline 到期、pending 使确认与请求构造失效；
 * 6. 使用权威时钟 (serverNow, deadline) 与本地接收单调采样推导流逝时间，同一快照重复刷新不重置采样；
 * 7. 纯只读构造请求，不修改快照或权威状态。
 */
class TileMarkSelectionModel(
    val monotonicClock: () -> Long = { System.nanoTime() / 1_000_000L }
) {
    private val _selectedTileIds = LinkedHashSet<Int>()

    /** 只读已选实体牌 ID 集合 */
    val selectedTileIds: Set<Int>
        get() = _selectedTileIds

    /** 是否处于选牌模式 */
    var isSelecting: Boolean = false
        private set

    // 当前绑定的快照与连接环境状态
    private var currentSnapshot: GameSnapshotView? = null
    private var isPending: Boolean = false
    private var isConnected: Boolean = true

    // 时钟权威采样
    private var sampledLocalMillis: Long? = null
    private var sampledServerNow: Long = 0L
    private var cachedDeadline: Long = 0L

    // 快照代次与候选集合特征
    private var lastSessionId: String? = null
    private var lastSyncEpoch: String? = null
    private var lastRoundId: String? = null
    private var lastDecisionId: String? = null
    private var lastControlEpoch: Long? = null
    private var lastSelectableIds: Set<Int>? = null
    private var lastCursor: Long? = null

    /** 是否满足提交确认条件 */
    val canConfirm: Boolean
        get() = evaluateCanConfirm(monotonicClock())

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
            sampledLocalMillis = null
            lastSelectableIds = null
            return
        }

        if (pending) {
            cancel()
        }

        val mark = snapshot.tileMark
        if (mark == null) {
            cancel()
            currentSnapshot = snapshot
            sampledLocalMillis = null
            lastSelectableIds = null
            return
        }

        val newSelectableSet = mark.selectableTileIds.toSet()

        // 检查身份代次与候选集合是否发生变化
        val identityChanged = lastSessionId != null && (
            snapshot.sessionId != lastSessionId ||
            snapshot.syncEpoch != lastSyncEpoch ||
            snapshot.roundId != lastRoundId ||
            snapshot.decision.decisionId != lastDecisionId ||
            snapshot.decision.controlEpoch != lastControlEpoch ||
            mark.decisionId != lastDecisionId ||
            mark.controlEpoch != lastControlEpoch ||
            (lastSelectableIds != null && newSelectableSet != lastSelectableIds)
        )

        if (identityChanged) {
            cancel()
        }

        val viewerSeat = snapshot.viewerSeat
        val points = snapshot.cheatPoints.getOrNull(viewerSeat) ?: 0
        val controllerRole = snapshot.controls.find { it.seat == viewerSeat }?.role
        val viewerClock = snapshot.clocks.find { it.seat == viewerSeat }

        // 任何使选牌草稿作废的条件发生时立即清空草稿
        val isSnapshotInvalidForMark = snapshot.paused ||
            !mark.canApply ||
            points < mark.cost ||
            (controllerRole != null && controllerRole != "HUMAN") ||
            viewerClock == null

        if (isSnapshotInvalidForMark) {
            cancel()
        }

        // 时钟采样：仅在全新快照或代次变化时初始化采样，重复刷新同一快照不重置采样
        val isSameSnapshotIdentity = (
            lastSessionId == snapshot.sessionId &&
            lastSyncEpoch == snapshot.syncEpoch &&
            lastRoundId == snapshot.roundId &&
            lastDecisionId == snapshot.decision.decisionId &&
            lastControlEpoch == snapshot.decision.controlEpoch &&
            lastCursor == snapshot.cursor
        )

        if (viewerClock != null) {
            if (!isSameSnapshotIdentity || sampledLocalMillis == null) {
                sampledLocalMillis = nowMillis
                sampledServerNow = viewerClock.serverNow
                cachedDeadline = viewerClock.deadline
            }
        } else {
            sampledLocalMillis = null
            cachedDeadline = 0L
        }

        this.currentSnapshot = snapshot
        this.lastSessionId = snapshot.sessionId
        this.lastSyncEpoch = snapshot.syncEpoch
        this.lastRoundId = snapshot.roundId
        this.lastDecisionId = snapshot.decision.decisionId
        this.lastControlEpoch = snapshot.decision.controlEpoch
        this.lastSelectableIds = newSelectableSet
        this.lastCursor = snapshot.cursor

        // 若当前选中的牌不再属于可选候选，也应取消选择
        if (_selectedTileIds.any { it !in newSelectableSet }) {
            cancel()
        }
    }

    /**
     * 进入选牌模式。
     */
    fun begin(): Boolean {
        val now = monotonicClock()
        val snap = currentSnapshot ?: return false
        val mark = snap.tileMark ?: return false
        if (!mark.canApply || !isConnected || isPending || snap.paused) return false
        if (!isDeadlineValid(now)) return false

        isSelecting = true
        return true
    }

    /**
     * 切换选择指定实体牌。
     */
    fun toggle(tileId: Int): Boolean {
        if (!isSelecting) return false
        val now = monotonicClock()
        if (!isDeadlineValid(now)) return false

        val snap = currentSnapshot ?: return false
        val mark = snap.tileMark ?: return false
        if (tileId !in mark.selectableTileIds) return false

        if (_selectedTileIds.contains(tileId)) {
            _selectedTileIds.remove(tileId)
            return true
        }

        if (_selectedTileIds.size >= 4) {
            return false // 拒绝第 5 张
        }

        _selectedTileIds.add(tileId)
        return true
    }

    /**
     * 取消选牌。
     */
    fun cancel() {
        isSelecting = false
        _selectedTileIds.clear()
    }

    /**
     * 构造请求（若不可确认则返回 null）。
     */
    fun buildRequest(requestId: String): TileMarkRequest? {
        val now = monotonicClock()
        if (!evaluateCanConfirm(now)) return null
        val snap = currentSnapshot ?: return null
        val mark = snap.tileMark ?: return null

        return TileMarkRequest(
            requestId = requestId,
            sessionId = snap.sessionId,
            roundId = snap.roundId ?: "",
            decisionId = mark.decisionId,
            controlEpoch = mark.controlEpoch,
            tileIds = _selectedTileIds.toList()
        )
    }

    private fun isDeadlineValid(now: Long): Boolean {
        val sample = sampledLocalMillis ?: return false
        val elapsed = maxOf(0L, now - sample)
        val estimatedServerNow = sampledServerNow + elapsed
        return estimatedServerNow < cachedDeadline
    }

    private fun evaluateCanConfirm(now: Long): Boolean {
        if (!isSelecting) return false
        if (_selectedTileIds.size != 4) return false
        val snap = currentSnapshot ?: return false
        if (snap.paused || isPending || !isConnected) return false
        val mark = snap.tileMark ?: return false
        if (!mark.canApply) return false

        val viewerSeat = snap.viewerSeat
        val points = snap.cheatPoints.getOrNull(viewerSeat) ?: 0
        if (points < mark.cost) return false

        val controllerRole = snap.controls.find { it.seat == viewerSeat }?.role
        if (controllerRole != null && controllerRole != "HUMAN") return false

        if (!isDeadlineValid(now)) return false

        val selectable = mark.selectableTileIds.toSet()
        if (_selectedTileIds.any { it !in selectable }) return false

        return true
    }
}

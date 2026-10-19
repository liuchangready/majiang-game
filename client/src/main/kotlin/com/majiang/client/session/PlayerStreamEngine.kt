package com.majiang.client.session

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.GameUpdateView
import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import java.util.LinkedHashSet
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.ConcurrentSkipListMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 玩家流更新处理效果
 */
internal sealed class PlayerUpdateEffect {
    object None : PlayerUpdateEffect()
    object BufferedEarly : PlayerUpdateEffect()
    object OverflowEarly : PlayerUpdateEffect()
    data class Dispatch(
        val updates: List<GameUpdateView>,
        val shouldRecover: Boolean,
        val recoverCursor: Long
    ) : PlayerUpdateEffect()
}

/**
 * 玩家流恢复响应处理效果
 */
internal sealed class PlayerResumeEffect {
    object None : PlayerResumeEffect()
    data class Dispatch(
        val updateToDispatch: GameUpdateView?,
        val subsequentUpdates: List<GameUpdateView>,
        val shouldTriggerNextRecovery: Boolean,
        val nextCursor: Long
    ) : PlayerResumeEffect()
}

/**
 * 客户端玩家流同步引擎 (PlayerStreamEngine)
 *
 * 满足 V4 改进计划 D-01 (§9.2)：从 [ServerConnectionSession] 独立提取玩家私有流同步组件。
 *
 * 状态所有权：
 * 1. 独占私有流互斥锁 ([syncLock])；
 * 2. 真实拥有当前有效私有快照 ([cachedSnapshot])；
 * 3. 真实拥有乱序重排缓冲 ([reorderBuffer]) 与最大缓冲有界限制 ([maxBufferSize])；
 * 4. 真实拥有断线与缺口自愈标记 ([isResuming])；
 * 5. 真实拥有握手早期推送缓冲 ([earlyUpdateBuffer]) 与溢出标记 ([earlyUpdateOverflowed])；
 * 6. 真实拥有已应用游标去重集合 ([appliedBatchCursors])；
 * 7. 真实拥有观察者集合 ([updateListeners]) 与监听器异常隔离。
 *
 * 协同机制：
 * - 纯领域逻辑，不持有父级会话引用，不引入万能 Context；
 * - 入站更新与恢复响应返回待派发效果，由调用协调层在锁外执行 UI 派发与网络请求；
 * - 终结与复位时清理内部缓冲与状态。
 */
internal class PlayerStreamEngine(
    val maxBufferSize: Int = 64,
    val maxAppliedBatchCursors: Int = 256
) {
    val syncLock = Any()

    private var _cachedSnapshot: GameSnapshotView? = null
    val cachedSnapshot: GameSnapshotView? get() = synchronized(syncLock) { _cachedSnapshot }

    private var _isBindingInProgress: Boolean = false
    val bindingInProgress: Boolean get() = synchronized(syncLock) { _isBindingInProgress }

    private val reorderBuffer = ConcurrentSkipListMap<Long, GameUpdateView>()
    val isResuming = AtomicBoolean(false)

    private val earlyUpdateBuffer = ConcurrentLinkedQueue<ProtocolPlayerUpdate>()
    val earlyUpdateOverflowed = AtomicBoolean(false)

    private val appliedBatchCursors = LinkedHashSet<Long>()
    private val updateListeners = CopyOnWriteArrayList<(GameUpdateView) -> Unit>()
    private val isClosed = AtomicBoolean(false)

    val closed: Boolean get() = isClosed.get()
    val reorderBufferSize: Int get() = reorderBuffer.size
    val earlyBufferSize: Int get() = earlyUpdateBuffer.size

    /**
     * 注册玩家对局更新观察者
     */
    fun onUpdate(listener: (GameUpdateView) -> Unit): AutoCloseable {
        updateListeners.add(listener)
        return AutoCloseable {
            updateListeners.remove(listener)
        }
    }

internal data class PlayerInstallResult(
    val pendingUpdates: List<GameUpdateView>,
    val hadOverflow: Boolean
)

    /**
     * 在锁内安装初始快照并排水早期缓冲
     */
    fun installInitialSnapshotUnderLock(
        initialSnapshot: GameSnapshotView
    ): PlayerInstallResult {
        _isBindingInProgress = false
        _cachedSnapshot = initialSnapshot
        reorderBuffer.clear()
        appliedBatchCursors.clear()
        isResuming.set(false)

        val pendingUpdatesToDispatch = mutableListOf<GameUpdateView>()
        val hadOverflow = earlyUpdateOverflowed.getAndSet(false)
        if (hadOverflow) {
            earlyUpdateBuffer.clear()
        } else {
            val earlyList = mutableListOf<ProtocolPlayerUpdate>()
            while (true) {
                val u = earlyUpdateBuffer.poll() ?: break
                earlyList.add(u)
            }
            earlyList.sortBy { it.fromCursor }

            for (protoUpdate in earlyList) {
                val updateView = try {
                    ProtocolViewMapper.mapUpdate(protoUpdate)
                } catch (_: Throwable) {
                    continue
                }
                val updateSnap = updateView.snapshot
                val c = _cachedSnapshot ?: break
                if (updateSnap.sessionId != c.sessionId || updateSnap.view.viewerSeat != c.view.viewerSeat) continue
                if (updateSnap.syncEpoch != c.syncEpoch) continue

                if (updateView.isFullSnapshot) {
                    if (updateSnap.cursor >= c.cursor) {
                        _cachedSnapshot = updateSnap
                        reorderBuffer.headMap(updateSnap.cursor).clear()
                        pendingUpdatesToDispatch.add(updateView)
                    }
                    continue
                }

                if (updateSnap.cursor <= c.cursor) continue
                if (updateView.fromCursor != c.cursor) {
                    if (updateView.fromCursor > c.cursor && reorderBuffer.size < maxBufferSize) {
                        reorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                    }
                } else {
                    applyUpdateUnderLock(updateView, pendingUpdatesToDispatch)
                    drainReorderBufferUnderLock(pendingUpdatesToDispatch)
                }
            }
        }
        return PlayerInstallResult(pendingUpdatesToDispatch, hadOverflow)
    }

    val reorderBufferNotEmpty: Boolean get() = synchronized(syncLock) { reorderBuffer.isNotEmpty() }

    /**
     * 在锁内采纳权威查询快照并对齐重排缓冲
     */
    fun adoptQuerySnapshotUnderLock(protoSnap: ProtocolPlayerSnapshot): GameSnapshotView {
        val mapped = ProtocolViewMapper.mapSnapshot(protoSnap)
        val cur = _cachedSnapshot
        return if (cur == null || cur.sessionId != mapped.sessionId ||
            mapped.syncEpoch != cur.syncEpoch || mapped.cursor >= cur.cursor
        ) {
            _cachedSnapshot = mapped
            reorderBuffer.headMap(mapped.cursor).clear()
            mapped
        } else {
            cur
        }
    }

    /**
     * 在锁内开始绑定过程 (标记绑定进行中并复位旧状态)
     */
    fun beginBindingUnderLock(
        clearEarlyBuffer: Boolean = true,
        clearSnapshot: Boolean = true
    ) {
        resetSyncStateUnderLock(clearEarlyBuffer, clearSnapshot)
        _isBindingInProgress = true
    }

    /**
     * 在锁内取消绑定标记
     */
    fun cancelBindingUnderLock() {
        _isBindingInProgress = false
    }

    /**
     * 在锁内复位同步状态
     */
    fun resetSyncStateUnderLock(
        clearEarlyBuffer: Boolean = true,
        clearSnapshot: Boolean = true
    ) {
        _isBindingInProgress = false
        if (clearSnapshot) {
            _cachedSnapshot = null
        }
        reorderBuffer.clear()
        appliedBatchCursors.clear()
        isResuming.set(false)
        if (clearEarlyBuffer) {
            earlyUpdateBuffer.clear()
            earlyUpdateOverflowed.set(false)
        }
    }

    /**
     * 处理入站玩家更新
     */
    fun handleInboundUpdate(
        protoUpdate: ProtocolPlayerUpdate,
        epoch: Long,
        roomEpoch: Long,
        isSessionValid: () -> Boolean,
        isMatchingBinding: (viewerSeat: Int, sessionId: String) -> Boolean
    ): PlayerUpdateEffect {
        val updatesToDispatch = mutableListOf<GameUpdateView>()
        var shouldRecover = false
        var recoverCursor = 0L

        synchronized(syncLock) {
            if (isClosed.get() || !isSessionValid()) return PlayerUpdateEffect.None

            // 绑定建立前（加入/恢复握手期），新连接推送进入早期缓冲
            if (_isBindingInProgress) {
                if (earlyUpdateBuffer.size < maxBufferSize && !earlyUpdateOverflowed.get()) {
                    earlyUpdateBuffer.add(protoUpdate)
                    return PlayerUpdateEffect.BufferedEarly
                } else {
                    earlyUpdateBuffer.clear()
                    earlyUpdateOverflowed.set(true)
                    return PlayerUpdateEffect.OverflowEarly
                }
            }

            val current = _cachedSnapshot ?: return PlayerUpdateEffect.None

            val updateView = try {
                ProtocolViewMapper.mapUpdate(protoUpdate)
            } catch (_: Throwable) {
                return PlayerUpdateEffect.None
            }
            val updateSnap = updateView.snapshot

            // 1. 验证会话 ID 与观察者席位一致性
            if (updateSnap.sessionId != current.sessionId ||
                updateSnap.view.viewerSeat != current.view.viewerSeat ||
                !isMatchingBinding(updateSnap.view.viewerSeat, updateSnap.sessionId)
            ) {
                return PlayerUpdateEffect.None
            }

            // 2. 验证 syncEpoch 代次
            if (updateSnap.syncEpoch != current.syncEpoch) {
                return PlayerUpdateEffect.None
            }

            // 3. 检查是否具备显式全量标记
            if (updateView.isFullSnapshot) {
                if (updateSnap.cursor >= current.cursor) {
                    _cachedSnapshot = updateSnap
                    reorderBuffer.headMap(updateSnap.cursor).clear()
                    isResuming.set(false)
                    updatesToDispatch.add(updateView)
                }
            } else if (updateSnap.cursor <= current.cursor) {
                // 游标已落后或重复，安全丢弃
                return PlayerUpdateEffect.None
            } else if (updateView.fromCursor > current.cursor) {
                // 发现 GAP：暂存乱序缓冲，拦截未来决策
                if (reorderBuffer.size < maxBufferSize) {
                    reorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                }
                if (isResuming.compareAndSet(false, true)) {
                    shouldRecover = true
                    recoverCursor = current.cursor
                }
            } else {
                // 连续到达或重叠向前推进
                applyUpdateUnderLock(updateView, updatesToDispatch)
                drainReorderBufferUnderLock(updatesToDispatch)

                // 检查排水后是否仍有后续缺口需要继续触发恢复
                val curAfterDrain = _cachedSnapshot?.cursor ?: 0L
                if (reorderBuffer.isNotEmpty() && reorderBuffer.firstKey() > curAfterDrain) {
                    if (isResuming.compareAndSet(false, true)) {
                        shouldRecover = true
                        recoverCursor = curAfterDrain
                    }
                }
            }
        }

        if (updatesToDispatch.isEmpty() && !shouldRecover) {
            return PlayerUpdateEffect.None
        }

        return PlayerUpdateEffect.Dispatch(
            updates = updatesToDispatch,
            shouldRecover = shouldRecover,
            recoverCursor = recoverCursor
        )
    }

    /**
     * 处理恢复响应 (resume)
     */
    fun handleResumeResponse(
        protoSnapshot: ProtocolPlayerSnapshot?,
        err: Throwable?,
        epoch: Long,
        roomEpoch: Long,
        isSessionValid: () -> Boolean,
        isMatchingBinding: (viewerSeat: Int, sessionId: String) -> Boolean
    ): PlayerResumeEffect {
        if (isClosed.get() || !isSessionValid()) {
            isResuming.set(false)
            return PlayerResumeEffect.None
        }
        if (err != null || protoSnapshot == null) {
            // 恢复失败释放标记，后续收包可继续重试，杜绝挂死
            isResuming.set(false)
            return PlayerResumeEffect.None
        }

        var updateToDispatch: GameUpdateView? = null
        val subsequentUpdates = mutableListOf<GameUpdateView>()
        var shouldTriggerNextRecovery = false
        var nextCursor = 0L

        synchronized(syncLock) {
            isResuming.set(false)
            if (isClosed.get() || !isSessionValid()) {
                return PlayerResumeEffect.None
            }
            val current = _cachedSnapshot
            if (current != null &&
                protoSnapshot.sessionId == current.sessionId &&
                protoSnapshot.syncEpoch == current.syncEpoch &&
                protoSnapshot.view.viewerSeat == current.view.viewerSeat &&
                protoSnapshot.cursor >= current.cursor &&
                isMatchingBinding(protoSnapshot.view.viewerSeat, protoSnapshot.sessionId)
            ) {
                val initialCursor = current.cursor
                val restored = try {
                    ProtocolViewMapper.mapSnapshot(protoSnapshot)
                } catch (_: Throwable) {
                    return PlayerResumeEffect.None
                }

                val snapshotAdvanced = restored.cursor > initialCursor
                if (snapshotAdvanced) {
                    _cachedSnapshot = restored
                    reorderBuffer.headMap(restored.cursor).clear()
                    updateToDispatch = GameUpdateView(
                        snapshot = restored,
                        fromCursor = restored.cursor,
                        visibleEvents = emptyList(),
                        isFullSnapshot = true
                    )
                }

                drainReorderBufferUnderLock(subsequentUpdates)

                val curAfterDrain = _cachedSnapshot?.cursor ?: initialCursor
                val madeProgress = curAfterDrain > initialCursor

                if (madeProgress && reorderBuffer.isNotEmpty() && reorderBuffer.firstKey() > curAfterDrain) {
                    if (isResuming.compareAndSet(false, true)) {
                        shouldTriggerNextRecovery = true
                        nextCursor = curAfterDrain
                    }
                }
            }
        }

        if (updateToDispatch == null && subsequentUpdates.isEmpty() && !shouldTriggerNextRecovery) {
            return PlayerResumeEffect.None
        }

        return PlayerResumeEffect.Dispatch(
            updateToDispatch = updateToDispatch,
            subsequentUpdates = subsequentUpdates,
            shouldTriggerNextRecovery = shouldTriggerNextRecovery,
            nextCursor = nextCursor
        )
    }

    /**
     * 在锁外派发玩家更新至观察者并隔离异常
     */
    fun notifyUpdateListeners(
        update: GameUpdateView,
        isSessionValid: () -> Boolean
    ) {
        for (listener in updateListeners) {
            if (isClosed.get() || !isSessionValid()) break
            try {
                listener(update)
            } catch (_: Throwable) {
                // 隔离监听器异常
            }
        }
    }

    private fun applyUpdateUnderLock(
        update: GameUpdateView,
        outList: MutableList<GameUpdateView>
    ) {
        val cursor = update.snapshot.cursor
        if (!appliedBatchCursors.add(cursor)) {
            return
        }
        if (appliedBatchCursors.size > maxAppliedBatchCursors) {
            val it = appliedBatchCursors.iterator()
            if (it.hasNext()) {
                it.next()
                it.remove()
            }
        }
        _cachedSnapshot = update.snapshot
        outList.add(update)
    }

    private fun drainReorderBufferUnderLock(outList: MutableList<GameUpdateView>) {
        while (true) {
            val cur = _cachedSnapshot ?: break
            val next = reorderBuffer.remove(cur.cursor) ?: break
            applyUpdateUnderLock(next, outList)
        }
    }

    /**
     * 关闭并清理
     */
    fun close() {
        isClosed.set(true)
        updateListeners.clear()
        synchronized(syncLock) {
            _cachedSnapshot = null
            reorderBuffer.clear()
            appliedBatchCursors.clear()
            isResuming.set(false)
            earlyUpdateBuffer.clear()
            earlyUpdateOverflowed.set(false)
        }
    }
}

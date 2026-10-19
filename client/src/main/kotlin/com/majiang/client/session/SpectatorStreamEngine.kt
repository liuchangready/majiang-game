package com.majiang.client.session

import com.majiang.client.api.PublicCheckpoint
import com.majiang.client.api.PublicSnapshotView
import com.majiang.client.api.PublicUpdateView
import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.ConcurrentSkipListMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * 观战恢复识别 Token
 */
internal data class PublicRecoveryToken(
    val connectionEpoch: Long,
    val roomEpoch: Long,
    val seq: Long
)

/**
 * 观战流更新处理效果
 */
internal sealed class SpectatorUpdateEffect {
    object None : SpectatorUpdateEffect()
    object BufferedEarly : SpectatorUpdateEffect()
    object OverflowEarly : SpectatorUpdateEffect()
    data class Dispatch(
        val updates: List<PublicUpdateView>,
        val shouldRecover: Boolean,
        val checkpoint: PublicCheckpoint?,
        val token: PublicRecoveryToken?,
        val boundRoomId: String
    ) : SpectatorUpdateEffect()
}

/**
 * 观战流恢复响应处理效果
 */
internal sealed class SpectatorResumeEffect {
    object None : SpectatorResumeEffect()
    data class Dispatch(
        val updateToDispatch: PublicUpdateView?,
        val subsequentUpdates: List<PublicUpdateView>,
        val shouldTriggerNextRecovery: Boolean,
        val nextCheckpoint: PublicCheckpoint?,
        val nextToken: PublicRecoveryToken?
    ) : SpectatorResumeEffect()
}

/**
 * 客户端观战流同步引擎 (SpectatorStreamEngine)
 *
 * 满足 V4 改进计划 D-01 (§9.2)：从 [ServerConnectionSession] 独立提取观战公开流同步组件。
 *
 * 状态所有权：
 * 1. 独占公开流互斥锁 ([publicSyncLock])；
 * 2. 真实拥有当前有效公开快照 ([cachedPublicSnapshot])；
 * 3. 真实拥有公开乱序重排缓冲 ([publicReorderBuffer])；
 * 4. 真实拥有带代次识别的观战恢复令牌 ([activePublicRecoveryToken]) 与序号分配 ([publicRecoverySeq])；
 * 5. 真实拥有观战握手早期推送缓冲 ([earlyPublicUpdateBuffer]) 与溢出标记 ([earlyPublicUpdateOverflowed])；
 * 6. 真实拥有观战观察者集合 ([publicUpdateListeners]) 与监听器异常隔离。
 *
 * 协同机制：
 * - 纯领域逻辑，不持有父级会话引用，不引入万能 Context；
 * - 入站公开更新与恢复响应返回待派发效果，由调用协调层在锁外执行 UI 派发与网络请求；
 * - 保证与玩家流锁顺序遵循 `syncLock -> publicSyncLock`。
 */
internal class SpectatorStreamEngine(
    val maxBufferSize: Int = 64
) {
    val publicSyncLock = Any()

    private var _cachedPublicSnapshot: PublicSnapshotView? = null
    val cachedPublicSnapshot: PublicSnapshotView? get() = synchronized(publicSyncLock) { _cachedPublicSnapshot }

    private var _isSpectatorBindingInProgress: Boolean = false
    val spectatorBindingInProgress: Boolean get() = synchronized(publicSyncLock) { _isSpectatorBindingInProgress }

    private val publicReorderBuffer = ConcurrentSkipListMap<Long, PublicUpdateView>()
    val activePublicRecoveryToken = AtomicReference<PublicRecoveryToken?>(null)
    private val publicRecoverySeq = AtomicLong(0L)

    private val earlyPublicUpdateBuffer = ConcurrentLinkedQueue<ProtocolPublicUpdate>()
    val earlyPublicUpdateOverflowed = AtomicBoolean(false)

    private val publicUpdateListeners = CopyOnWriteArrayList<(PublicUpdateView) -> Unit>()
    private val isClosed = AtomicBoolean(false)

    val closed: Boolean get() = isClosed.get()
    val reorderBufferSize: Int get() = publicReorderBuffer.size
    val earlyBufferSize: Int get() = earlyPublicUpdateBuffer.size

    /**
     * 注册观战公开更新观察者
     */
    fun onPublicUpdate(listener: (PublicUpdateView) -> Unit): AutoCloseable {
        publicUpdateListeners.add(listener)
        return AutoCloseable {
            publicUpdateListeners.remove(listener)
        }
    }

internal data class SpectatorInstallResult(
    val pendingUpdates: List<PublicUpdateView>,
    val hadOverflow: Boolean
)

    /**
     * 在锁内安装初始快照并排水早期缓冲
     */
    fun installInitialSnapshotUnderLock(
        initialSnapshot: PublicSnapshotView
    ): SpectatorInstallResult {
        _isSpectatorBindingInProgress = false
        _cachedPublicSnapshot = initialSnapshot
        publicReorderBuffer.clear()
        activePublicRecoveryToken.set(null)

        val pendingUpdatesToDispatch = mutableListOf<PublicUpdateView>()
        val hadOverflow = earlyPublicUpdateOverflowed.getAndSet(false)
        val earlyList = mutableListOf<ProtocolPublicUpdate>()
        while (true) {
            val u = earlyPublicUpdateBuffer.poll() ?: break
            earlyList.add(u)
        }

        for (protoUpdate in earlyList) {
            val updateView = try {
                ProtocolViewMapper.mapPublicUpdate(protoUpdate)
            } catch (_: Throwable) {
                continue
            }
            val updateSnap = updateView.snapshot
            val c = _cachedPublicSnapshot ?: break
            if (updateSnap.sessionId != c.sessionId || updateSnap.syncEpoch != c.syncEpoch) continue

            if (updateView.isFullSnapshot) {
                if (updateSnap.cursor > c.cursor) {
                    _cachedPublicSnapshot = updateSnap
                    publicReorderBuffer.headMap(updateSnap.cursor).clear()
                    pendingUpdatesToDispatch.add(updateView)
                    drainPublicReorderBufferUnderLock(pendingUpdatesToDispatch)
                } else {
                    publicReorderBuffer.headMap(updateSnap.cursor).clear()
                }
            } else if (updateSnap.cursor <= c.cursor) {
                continue
            } else if (updateView.fromCursor != c.cursor) {
                if (updateView.fromCursor > c.cursor && publicReorderBuffer.size < maxBufferSize) {
                    publicReorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                }
            } else {
                applyPublicUpdateUnderLock(updateView, pendingUpdatesToDispatch)
                drainPublicReorderBufferUnderLock(pendingUpdatesToDispatch)
            }
        }
        return SpectatorInstallResult(pendingUpdatesToDispatch, hadOverflow)
    }

    fun allocateRecoveryToken(connectionEpoch: Long, roomEpoch: Long): PublicRecoveryToken =
        PublicRecoveryToken(connectionEpoch, roomEpoch, publicRecoverySeq.incrementAndGet())

    /**
     * 在锁内采纳权威公开快照并对齐重排缓冲
     */
    fun adoptAuthoritativePublicSnapshotUnderLock(
        view: PublicSnapshotView,
        epoch: Long,
        roomEpoch: Long,
        roomId: String,
        boundRoomIdProvider: () -> String?,
        isSessionValid: () -> Boolean
    ): Boolean {
        synchronized(publicSyncLock) {
            if (isClosed.get() || !isSessionValid()) return false
            val curRoom = boundRoomIdProvider()
            if (curRoom == null || curRoom != roomId) return false

            val cur = _cachedPublicSnapshot
            return if (cur == null || cur.sessionId != view.sessionId || cur.syncEpoch != view.syncEpoch) {
                _cachedPublicSnapshot = view
                publicReorderBuffer.clear()
                true
            } else if (view.cursor > cur.cursor) {
                _cachedPublicSnapshot = view
                publicReorderBuffer.headMap(view.cursor).clear()
                true
            } else if (view.cursor == cur.cursor) {
                true
            } else {
                false
            }
        }
    }

    /**
     * 在锁内开始观战绑定过程 (标记绑定进行中并复位旧状态)
     */
    fun beginBindingUnderLock(
        clearEarlyBuffer: Boolean = true,
        clearSnapshot: Boolean = true
    ) {
        resetSyncStateUnderLock(clearEarlyBuffer, clearSnapshot)
        _isSpectatorBindingInProgress = true
    }

    /**
     * 在锁内取消观战绑定标记
     */
    fun cancelBindingUnderLock() {
        _isSpectatorBindingInProgress = false
    }

    /**
     * 在锁内复位观战同步状态
     */
    fun resetSyncStateUnderLock(
        clearEarlyBuffer: Boolean = true,
        clearSnapshot: Boolean = true
    ) {
        _isSpectatorBindingInProgress = false
        if (clearSnapshot) {
            _cachedPublicSnapshot = null
        }
        publicReorderBuffer.clear()
        activePublicRecoveryToken.set(null)
        if (clearEarlyBuffer) {
            earlyPublicUpdateBuffer.clear()
            earlyPublicUpdateOverflowed.set(false)
        }
    }

    /**
     * 处理入站公开更新
     */
    fun handleInboundPublicUpdate(
        protoUpdate: ProtocolPublicUpdate,
        epoch: Long,
        roomEpoch: Long,
        boundRoomIdProvider: () -> String?,
        isSessionValid: () -> Boolean
    ): SpectatorUpdateEffect {
        val updatesToDispatch = mutableListOf<PublicUpdateView>()
        var shouldRecover = false
        var checkpointToRecover: PublicCheckpoint? = null
        var recoveryToken: PublicRecoveryToken? = null
        var boundRoomId: String? = null

        synchronized(publicSyncLock) {
            if (isClosed.get() || !isSessionValid()) return SpectatorUpdateEffect.None

            // 绑定建立前（观战加入/恢复握手期），新连接推送进入早期缓冲
            if (_isSpectatorBindingInProgress) {
                if (earlyPublicUpdateBuffer.size < maxBufferSize) {
                    earlyPublicUpdateBuffer.add(protoUpdate)
                    return SpectatorUpdateEffect.BufferedEarly
                } else {
                    earlyPublicUpdateOverflowed.set(true)
                    return SpectatorUpdateEffect.OverflowEarly
                }
            }

            val current = _cachedPublicSnapshot ?: return SpectatorUpdateEffect.None
            val curRoom = boundRoomIdProvider() ?: return SpectatorUpdateEffect.None
            boundRoomId = curRoom

            val updateView = try {
                ProtocolViewMapper.mapPublicUpdate(protoUpdate)
            } catch (_: Throwable) {
                return SpectatorUpdateEffect.None
            }
            val updateSnap = updateView.snapshot

            // 1. 验证会话 ID 与 syncEpoch 代次
            if (updateSnap.sessionId != current.sessionId || updateSnap.syncEpoch != current.syncEpoch) {
                return SpectatorUpdateEffect.None
            }

            // 2. 检查是否具备显式全量标记
            if (updateView.isFullSnapshot) {
                if (updateSnap.cursor > current.cursor) {
                    _cachedPublicSnapshot = updateSnap
                    publicReorderBuffer.headMap(updateSnap.cursor).clear()
                    activePublicRecoveryToken.set(null)
                    updatesToDispatch.add(updateView)
                    drainPublicReorderBufferUnderLock(updatesToDispatch)
                } else {
                    publicReorderBuffer.headMap(updateSnap.cursor).clear()
                }
            } else if (updateSnap.cursor <= current.cursor) {
                // 游标已落后或重复，安全丢弃
                return SpectatorUpdateEffect.None
            } else if (updateView.fromCursor != current.cursor) {
                // 严格公开顺序：普通更新的 fromCursor 必须严格等于当前基线 cursor
                if (updateView.fromCursor > current.cursor) {
                    if (publicReorderBuffer.size < maxBufferSize) {
                        publicReorderBuffer.putIfAbsent(updateView.fromCursor, updateView)
                    }
                    val curToken = activePublicRecoveryToken.get()
                    if (curToken == null || curToken.connectionEpoch != epoch || curToken.roomEpoch != roomEpoch) {
                        val token = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
                        activePublicRecoveryToken.set(token)
                        recoveryToken = token
                        shouldRecover = true
                        checkpointToRecover = PublicCheckpoint(current.sessionId, current.syncEpoch, current.cursor)
                    }
                }
            } else {
                // updateView.fromCursor == current.cursor 严格连续到达
                applyPublicUpdateUnderLock(updateView, updatesToDispatch)
                drainPublicReorderBufferUnderLock(updatesToDispatch)

                val curAfterDrain = _cachedPublicSnapshot?.cursor ?: 0L
                if (publicReorderBuffer.isNotEmpty() && publicReorderBuffer.firstKey() > curAfterDrain) {
                    val curToken = activePublicRecoveryToken.get()
                    if (curToken == null || curToken.connectionEpoch != epoch || curToken.roomEpoch != roomEpoch) {
                        val token = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
                        activePublicRecoveryToken.set(token)
                        recoveryToken = token
                        shouldRecover = true
                        checkpointToRecover = PublicCheckpoint(current.sessionId, current.syncEpoch, curAfterDrain)
                    }
                }
            }
        }

        if (updatesToDispatch.isEmpty() && !shouldRecover) {
            return SpectatorUpdateEffect.None
        }

        return SpectatorUpdateEffect.Dispatch(
            updates = updatesToDispatch,
            shouldRecover = shouldRecover,
            checkpoint = checkpointToRecover,
            token = recoveryToken,
            boundRoomId = boundRoomId ?: ""
        )
    }

    /**
     * 处理观战恢复响应 (resumePublic)
     */
    fun handleResumePublicResponse(
        protoSnapshot: ProtocolPublicSnapshot?,
        err: Throwable?,
        token: PublicRecoveryToken,
        roomId: String,
        boundRoomIdProvider: () -> String?,
        isSessionValid: () -> Boolean
    ): SpectatorResumeEffect {
        val epoch = token.connectionEpoch
        val roomEpoch = token.roomEpoch
        if (isClosed.get() || !isSessionValid()) {
            return SpectatorResumeEffect.None
        }
        if (err != null || protoSnapshot == null) {
            activePublicRecoveryToken.compareAndSet(token, null)
            return SpectatorResumeEffect.None
        }

        var updateToDispatch: PublicUpdateView? = null
        val subsequentUpdates = mutableListOf<PublicUpdateView>()
        var shouldTriggerNextRecovery = false
        var nextCheckpoint: PublicCheckpoint? = null
        var nextToken: PublicRecoveryToken? = null

        synchronized(publicSyncLock) {
            if (isClosed.get() || !isSessionValid()) {
                return SpectatorResumeEffect.None
            }
            val curRoom = boundRoomIdProvider()
            if (curRoom == null || curRoom != roomId) {
                return SpectatorResumeEffect.None
            }
            if (!activePublicRecoveryToken.compareAndSet(token, null)) {
                return SpectatorResumeEffect.None
            }
            val current = _cachedPublicSnapshot
            if (current != null &&
                protoSnapshot.sessionId == current.sessionId &&
                protoSnapshot.syncEpoch == current.syncEpoch &&
                protoSnapshot.cursor >= current.cursor
            ) {
                val initialCursor = current.cursor
                val restored = try {
                    ProtocolViewMapper.mapPublicSnapshot(protoSnapshot)
                } catch (_: Throwable) {
                    return SpectatorResumeEffect.None
                }

                val snapshotAdvanced = restored.cursor > initialCursor
                if (snapshotAdvanced) {
                    _cachedPublicSnapshot = restored
                    publicReorderBuffer.headMap(restored.cursor).clear()
                    updateToDispatch = PublicUpdateView(
                        snapshot = restored,
                        fromCursor = restored.cursor,
                        visibleEvents = emptyList(),
                        isFullSnapshot = true
                    )
                }

                drainPublicReorderBufferUnderLock(subsequentUpdates)

                val curAfterDrain = _cachedPublicSnapshot?.cursor ?: initialCursor
                val madeProgress = curAfterDrain > initialCursor

                if (madeProgress && publicReorderBuffer.isNotEmpty() && publicReorderBuffer.firstKey() > curAfterDrain) {
                    val nToken = PublicRecoveryToken(epoch, roomEpoch, publicRecoverySeq.incrementAndGet())
                    activePublicRecoveryToken.set(nToken)
                    nextToken = nToken
                    shouldTriggerNextRecovery = true
                    nextCheckpoint = PublicCheckpoint(current.sessionId, current.syncEpoch, curAfterDrain)
                }
            }
        }

        if (updateToDispatch == null && subsequentUpdates.isEmpty() && !shouldTriggerNextRecovery) {
            return SpectatorResumeEffect.None
        }

        return SpectatorResumeEffect.Dispatch(
            updateToDispatch = updateToDispatch,
            subsequentUpdates = subsequentUpdates,
            shouldTriggerNextRecovery = shouldTriggerNextRecovery,
            nextCheckpoint = nextCheckpoint,
            nextToken = nextToken
        )
    }

    /**
     * 在锁外派发公开更新至观察者并隔离异常
     */
    fun notifyPublicUpdateListeners(
        update: PublicUpdateView,
        isSessionValid: () -> Boolean
    ) {
        for (listener in publicUpdateListeners) {
            if (isClosed.get() || !isSessionValid()) break
            try {
                listener(update)
            } catch (_: Throwable) {
                // 隔离监听器异常
            }
        }
    }

    private fun applyPublicUpdateUnderLock(
        update: PublicUpdateView,
        outList: MutableList<PublicUpdateView>
    ) {
        val current = _cachedPublicSnapshot ?: return
        if (update.fromCursor != current.cursor) return
        if (update.snapshot.cursor <= current.cursor) return

        _cachedPublicSnapshot = update.snapshot
        outList.add(update)
    }

    private fun drainPublicReorderBufferUnderLock(outList: MutableList<PublicUpdateView>) {
        while (true) {
            val current = _cachedPublicSnapshot ?: break
            val nextUpdate = publicReorderBuffer[current.cursor] ?: break
            publicReorderBuffer.remove(current.cursor)
            applyPublicUpdateUnderLock(nextUpdate, outList)
        }
    }

    /**
     * 关闭并清理
     */
    fun close() {
        isClosed.set(true)
        publicUpdateListeners.clear()
        synchronized(publicSyncLock) {
            _cachedPublicSnapshot = null
            publicReorderBuffer.clear()
            activePublicRecoveryToken.set(null)
            earlyPublicUpdateBuffer.clear()
            earlyPublicUpdateOverflowed.set(false)
        }
    }
}

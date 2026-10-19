package com.majiang.client.session

import com.majiang.client.api.RoomInfo
import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.transport.api.ProtocolRoomInfo
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * 房间观察者状态与分发锁
 */
internal class RoomObserver(val listener: (RoomInfo) -> Unit) {
    @Volatile
    var active: Boolean = true
    val listenerLock = Any()
    var lastDeliveredRoomEpoch: Long = -1L
    var lastDeliveredVersion: Long = -1L
}

/**
 * 房间入站更新处理结果
 */
internal sealed class RoomUpdateEffect {
    object None : RoomUpdateEffect()
    data class Notify(
        val roomInfo: RoomInfo,
        val connectionEpoch: Long,
        val roomEpoch: Long,
        val observers: List<RoomObserver>
    ) : RoomUpdateEffect()
}

/**
 * 客户端房间同步状态与生命周期引擎 (RoomSyncEngine)
 *
 * 满足 V4 改进计划 D-01 (§9.2) 第一步：从 [ServerConnectionSession] 独立提取房间同步组件。
 *
 * 状态所有权：
 * 1. 真实拥有房间信息缓存 ([cachedRoomInfo]) 与最新版本号 ([cachedRoomVersion])；
 * 2. 真实拥有房间代次与轮转状态 ([roomEpochRef] / [currentRoomEpoch])；
 * 3. 真实拥有观察者列表 ([roomObservers]) 与观察者递送状态 ([RoomObserver])；
 * 4. 真实拥有房间作用域未决 Future 追踪 ([outstandingRoomFutures])；
 * 5. 内部独占同步锁 ([roomLock])，杜绝跨流反向死锁，并保证版本单调前进。
 *
 * 协同机制：
 * - 纯领域逻辑，不持有父级会话引用，不引入万能 Context；
 * - 入站更新经内部校验写回缓存后，返回待派发效果 [RoomUpdateEffect.Notify]，由调用协调层在锁外执行 UI 派发；
 * - 终结、重置与代次演进时返回受影响的 Future 清单，由调用协调层统一在锁外以代次异常完成。
 */
internal class RoomSyncEngine {

    val roomLock = Any()
    val roomEpochRef = AtomicLong(0L)
    val currentRoomEpoch: Long get() = roomEpochRef.get()

    private var cachedRoomInfo: RoomInfo? = null
    private var cachedRoomVersion: Long? = null

    val cachedInfo: RoomInfo? get() = synchronized(roomLock) { cachedRoomInfo }
    val cachedVersion: Long? get() = synchronized(roomLock) { cachedRoomVersion }

    private val roomObservers = CopyOnWriteArrayList<RoomObserver>()
    private val outstandingRoomFutures = ConcurrentHashMap<CompletableFuture<*>, Long>()
    private val isClosed = AtomicBoolean(false)

    val closed: Boolean get() = isClosed.get()

    /**
     * 注册房间状态变更观察者
     */
    fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable {
        val observer = RoomObserver(listener)
        roomObservers.add(observer)
        return AutoCloseable {
            observer.active = false
            roomObservers.remove(observer)
        }
    }

    /**
     * 追踪绑定到特定房间代次的未决 Future
     */
    fun <T> trackRoomFuture(future: CompletableFuture<T>, roomEpoch: Long = currentRoomEpoch): CompletableFuture<T> {
        outstandingRoomFutures[future] = roomEpoch
        future.whenComplete { _, _ ->
            outstandingRoomFutures.remove(future)
        }
        return future
    }

    /**
     * 取消追踪指定的 Future
     */
    fun untrackRoomFuture(future: CompletableFuture<*>) {
        outstandingRoomFutures.remove(future)
    }

    /**
     * 递增并推进房间代次
     */
    fun advanceRoomEpoch(): Long {
        return roomEpochRef.incrementAndGet()
    }

    /**
     * 清理当前房间信息与版本号缓存
     */
    fun resetRoomInfo() {
        synchronized(roomLock) {
            cachedRoomInfo = null
            cachedRoomVersion = null
        }
    }

    /**
     * 搜集并清理小于目标房间代次的所有未决 Future
     */
    fun pruneFuturesOlderThan(targetRoomEpoch: Long): List<CompletableFuture<*>> {
        val toFail = mutableListOf<CompletableFuture<*>>()
        val it = outstandingRoomFutures.entries.iterator()
        while (it.hasNext()) {
            val entry = it.next()
            if (entry.value < targetRoomEpoch) {
                it.remove()
                toFail.add(entry.key)
            }
        }
        return toFail
    }

    /**
     * 搜集并清理小于等于目标房间代次的所有未决 Future（用于离房退出）
     */
    fun pruneFuturesLessOrEqual(targetRoomEpoch: Long): List<CompletableFuture<*>> {
        val toFail = mutableListOf<CompletableFuture<*>>()
        val it = outstandingRoomFutures.entries.iterator()
        while (it.hasNext()) {
            val entry = it.next()
            if (entry.value <= targetRoomEpoch) {
                it.remove()
                toFail.add(entry.key)
            }
        }
        return toFail
    }

    /**
     * 离房归档处理：递增代次、清空房间缓存并提取需要异常完成的房间 Future
     */
    fun retireRoom(): List<CompletableFuture<*>> {
        val retiredEpoch = advanceRoomEpoch()
        resetRoomInfo()
        return pruneFuturesLessOrEqual(retiredEpoch)
    }

    /**
     * 终结引擎：失效所有观察者、清空房间缓存并提取全部遗留房间 Future
     */
    fun close(): List<CompletableFuture<*>> {
        isClosed.set(true)
        for (obs in roomObservers) {
            obs.active = false
        }
        roomObservers.clear()
        resetRoomInfo()
        val allFutures = outstandingRoomFutures.keys.toList()
        outstandingRoomFutures.clear()
        return allFutures
    }

    /**
     * 处理入站房间更新报文，执行版本校验并写回缓存。
     *
     * @return 若产生有效前向更新，返回包含待派发观察者任务的 [RoomUpdateEffect.Notify]，否则返回 [RoomUpdateEffect.None]。
     */
    /**
     * 处理入站房间更新报文，执行版本校验并在锁内二次校验当前绑定房间，防止换绑竞态污染。
     *
     * @return 若产生有效前向更新，返回包含待派发观察者任务的 [RoomUpdateEffect.Notify]，否则返回 [RoomUpdateEffect.None]。
     */
    fun handleInboundRoomUpdate(
        protoRoomInfo: ProtocolRoomInfo,
        connectionEpoch: Long,
        currentBoundRoomId: () -> String?,
        isSessionValid: () -> Boolean
    ): RoomUpdateEffect {
        if (isClosed.get() || !isSessionValid()) {
            return RoomUpdateEffect.None
        }
        val preliminaryRoom = currentBoundRoomId()
        if (preliminaryRoom == null || protoRoomInfo.roomId != preliminaryRoom) {
            return RoomUpdateEffect.None
        }
        val mapped = try {
            ProtocolViewMapper.mapRoomInfo(protoRoomInfo)
        } catch (_: Throwable) {
            return RoomUpdateEffect.None
        }
        val roomEpoch = currentRoomEpoch
        val shouldNotify = synchronized(roomLock) {
            if (isClosed.get() || roomEpoch != currentRoomEpoch || !isSessionValid()) {
                false
            } else {
                val curBoundRoom = currentBoundRoomId()
                if (curBoundRoom == null || curBoundRoom != mapped.roomId) {
                    false
                } else {
                    val curVer = cachedRoomVersion
                    if (curVer == null || mapped.version > curVer) {
                        cachedRoomVersion = mapped.version
                        cachedRoomInfo = mapped
                        true
                    } else {
                        false
                    }
                }
            }
        }
        if (!shouldNotify) {
            return RoomUpdateEffect.None
        }
        return RoomUpdateEffect.Notify(
            roomInfo = mapped,
            connectionEpoch = connectionEpoch,
            roomEpoch = roomEpoch,
            observers = roomObservers.toList()
        )
    }

    fun handleInboundRoomUpdate(
        protoRoomInfo: ProtocolRoomInfo,
        connectionEpoch: Long,
        expectedRoomId: String?,
        isSessionValid: () -> Boolean
    ): RoomUpdateEffect = handleInboundRoomUpdate(
        protoRoomInfo = protoRoomInfo,
        connectionEpoch = connectionEpoch,
        currentBoundRoomId = { expectedRoomId },
        isSessionValid = isSessionValid
    )

    /**
     * 执行单个观察者递送，具备单调版本检查、代次校验与异常隔离。由调用方在锁外通过派发器异步执行。
     */
    fun notifyObserver(
        observer: RoomObserver,
        info: RoomInfo,
        connectionEpoch: Long,
        roomEpoch: Long,
        isSessionValidAndMatchingRoom: () -> Boolean
    ) {
        if (!observer.active || !roomObservers.contains(observer)) return
        if (isClosed.get() || !isSessionValidAndMatchingRoom()) return

        synchronized(observer.listenerLock) {
            if (!observer.active || !roomObservers.contains(observer)) return
            if (isClosed.get() || !isSessionValidAndMatchingRoom()) return

            val isStale = synchronized(roomLock) {
                if (isClosed.get() || roomEpoch != currentRoomEpoch || !isSessionValidAndMatchingRoom()) {
                    true
                } else {
                    val curVer = cachedRoomVersion
                    curVer != null && info.version < curVer
                }
            }
            if (isStale) return

            if (observer.lastDeliveredRoomEpoch == roomEpoch && info.version <= observer.lastDeliveredVersion) {
                return
            }

            observer.lastDeliveredRoomEpoch = roomEpoch
            observer.lastDeliveredVersion = info.version

            try {
                observer.listener(info)
            } catch (_: Throwable) {
                // 隔离监听器异常
            }
        }
    }

    /**
     * 采纳权威查询返回的房间信息并在锁内二次校验当前绑定房间，防止换绑后迟到的旧房间查询污染缓存。
     */
    fun adoptAuthoritativeRoomInfo(
        protoInfo: ProtocolRoomInfo,
        currentBoundRoomId: () -> String?,
        requestRoomEpoch: Long,
        isSessionValid: () -> Boolean
    ): RoomInfo? {
        if (isClosed.get() || !isSessionValid()) return null
        val preliminaryRoom = currentBoundRoomId()
        if (preliminaryRoom == null || protoInfo.roomId != preliminaryRoom) return null
        val mapped = try {
            ProtocolViewMapper.mapRoomInfo(protoInfo)
        } catch (_: Throwable) {
            return null
        }
        return synchronized(roomLock) {
            if (isClosed.get() || requestRoomEpoch != currentRoomEpoch || !isSessionValid()) {
                null
            } else {
                val curBoundRoom = currentBoundRoomId()
                if (curBoundRoom == null || curBoundRoom != mapped.roomId) {
                    null
                } else {
                    val cur = cachedRoomInfo
                    if (cur != null && cur.roomId == mapped.roomId && cur.version >= mapped.version) {
                        cur
                    } else {
                        cachedRoomInfo = mapped
                        cachedRoomVersion = mapped.version
                        mapped
                    }
                }
            }
        }
    }

    fun adoptAuthoritativeRoomInfo(
        protoInfo: ProtocolRoomInfo,
        expectedRoomId: String?,
        requestRoomEpoch: Long,
        isSessionValid: () -> Boolean
    ): RoomInfo? = adoptAuthoritativeRoomInfo(
        protoInfo = protoInfo,
        currentBoundRoomId = { expectedRoomId },
        requestRoomEpoch = requestRoomEpoch,
        isSessionValid = isSessionValid
    )
}

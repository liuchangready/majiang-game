package com.majiang.server.room

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerApiException
import com.majiang.state.GameState
import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock
import com.majiang.transport.api.ProtocolRoomConfig
import java.util.ArrayList
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 传输中立的房间注册表与生命周期协调器 (RoomRegistry)
 *
 * 遵循 V4 架构与 TASK-ANTI-R4B1ROOM 规范：
 * 1. 集中管理权威共享房间 [SharedRoom] 的准入、创建、查询、解散与清理；
 * 2. 严格原子与有界准入控制：基于内部短临界区锁防御并发超限与停服后写入；
 * 3. 锁与业务完全解耦：持锁期间仅执行轻量级元数据检查与集合更新，严禁在持锁下执行房间构造、监听器或等待 Future；
 * 4. 替换防泄漏：registerRoom 若覆盖旧房间，在锁外确定性释放被替换房间资源；
 * 5. 确定性关闭：原子标记关闭并清空索引，在锁外批量关闭所有存活房间。
 */
class RoomRegistry(
    val clock: MonotonicClock = SystemMonotonicClock,
    val maxRooms: Int = 1024
) : AutoCloseable {

    private val isClosed = AtomicBoolean(false)
    val closed: Boolean get() = isClosed.get()

    private val rooms = ConcurrentHashMap<String, SharedRoom>()
    private val lock = Any()

    fun createRoom(
        hostPlayerName: String = "Host",
        initialConfig: ProtocolRoomConfig = ProtocolRoomConfig(),
        isSingleplayer: Boolean = false,
        fixedRoomId: String? = null,
        serialExecutor: ExecutorService? = null,
        hostSeat: Int = 0,
        initialState: GameState? = null
    ): SharedRoom {
        var roomIdToUse: String
        synchronized(lock) {
            if (isClosed.get()) {
                throw ServerApiException(ApiErrors.INTERNAL_ERROR, "RoomRegistry is closed")
            }
            rooms.entries.removeIf { it.value.closed }
            if (rooms.size >= maxRooms) {
                throw ServerApiException(ApiErrors.SERVER_FULL, "SERVER_FULL: room limit ($maxRooms) reached")
            }
            if (fixedRoomId != null) {
                val existing = rooms[fixedRoomId]
                if (existing != null && !existing.closed) {
                    throw ServerApiException(ApiErrors.CONFLICT, "Room already exists: $fixedRoomId")
                }
                roomIdToUse = fixedRoomId
            } else {
                do {
                    roomIdToUse = newRoomId()
                } while (rooms.containsKey(roomIdToUse))
            }
        }

        // 构造 SharedRoom 在锁外进行
        val room = SharedRoom(
            roomId = roomIdToUse,
            hostPlayerName = hostPlayerName,
            initialConfig = initialConfig,
            isSingleplayer = isSingleplayer,
            clock = clock,
            serialExecutor = serialExecutor,
            hostSeat = hostSeat,
            initialState = initialState
        )

        var created = false
        try {
            synchronized(lock) {
                if (isClosed.get()) {
                    throw ServerApiException(ApiErrors.INTERNAL_ERROR, "RoomRegistry is closed")
                }
                rooms.entries.removeIf { it.value.closed }
                if (rooms.size >= maxRooms) {
                    throw ServerApiException(ApiErrors.SERVER_FULL, "SERVER_FULL: room limit ($maxRooms) reached")
                }
                val existing = rooms.putIfAbsent(roomIdToUse, room)
                if (existing == null || existing.closed) {
                    if (existing != null && existing.closed) {
                        rooms[roomIdToUse] = room
                    }
                    created = true
                } else if (fixedRoomId != null) {
                    throw ServerApiException(ApiErrors.CONFLICT, "Room already exists: $fixedRoomId")
                }
            }
        } finally {
            if (!created) {
                try {
                    room.close()
                } catch (_: Throwable) {
                }
            }
        }

        if (!created) {
            return createRoom(hostPlayerName, initialConfig, isSingleplayer, fixedRoomId, serialExecutor, hostSeat, initialState)
        }

        return room
    }

    fun registerRoom(room: SharedRoom) {
        var displaced: SharedRoom? = null
        synchronized(lock) {
            if (isClosed.get()) throw ServerApiException(ApiErrors.INTERNAL_ERROR, "RoomRegistry is closed")
            rooms.entries.removeIf { it.value.closed }
            if (rooms.size >= maxRooms && !rooms.containsKey(room.roomId)) {
                throw ServerApiException(ApiErrors.SERVER_FULL, "SERVER_FULL: room limit ($maxRooms) reached")
            }
            displaced = rooms.put(room.roomId, room)
        }
        if (displaced != null && displaced !== room && !displaced!!.closed) {
            try {
                displaced!!.close()
            } catch (_: Throwable) {
            }
        }
    }

    fun getRoom(roomId: String): SharedRoom? {
        val r = rooms[roomId] ?: return null
        if (r.closed) {
            rooms.remove(roomId, r)
            return null
        }
        return r
    }

    fun removeRoom(roomId: String): SharedRoom? = rooms.remove(roomId)

    fun listSummaries(): List<RoomSummary> {
        rooms.entries.removeIf { it.value.closed }
        return rooms.values.filter { !it.closed }.map { it.toSummary() }
    }

    fun count(): Int {
        rooms.entries.removeIf { it.value.closed }
        return rooms.size
    }

    private fun newRoomId(): String =
        UUID.randomUUID().toString().replace("-", "").substring(0, 8).uppercase()

    override fun close() {
        val roomsToClose: List<SharedRoom>
        synchronized(lock) {
            if (!isClosed.compareAndSet(false, true)) return
            roomsToClose = ArrayList(rooms.values)
            rooms.clear()
        }
        for (room in roomsToClose) {
            try {
                room.close()
            } catch (_: Throwable) {
            }
        }
    }
}

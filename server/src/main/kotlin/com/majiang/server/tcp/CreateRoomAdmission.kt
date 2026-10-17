package com.majiang.server.tcp

import com.majiang.server.room.RoomCapacityPolicy
import com.majiang.server.room.RoomRegistry
import com.majiang.transport.api.ProtocolRoomLifecycle
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.util.ArrayDeque
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 未绑定建房准入判定与原子容量预留器 (CreateRoomAdmission)
 *
 * 遵循 MR-06 规范，在分配房间与每桌权威执行器前原子化检查并预留配额：
 * 1. 全局容量预算：注册表存活房间数 + 在途预留数 < registry.maxRooms；
 * 2. 单源 IP 未开局配额：该 IP 名下未开局 (LOBBY) 房间数 + 在途预留数 < maxUnstartedRoomsPerIp；
 * 3. 逻辑主体配额：该 principal 名下未开局 (LOBBY) 房间数 + 在途预留数 < maxUnstartedRoomsPerPrincipal；
 * 4. 全局建房速率：滑动窗口限流 (globalCreateRoomRatePerSecond)。
 *
 * 共享 NAT 语义（写进 KDoc）：
 * 同 IP 多身份各自算 principal 名额，IP 名额共享；两者取更严者生效（即任一达限即拒）。
 * TCP 握手不构成用户鉴权，不能据此取消全局预算。
 */
class CreateRoomAdmission(
    private val registry: RoomRegistry,
    private val capacityPolicy: RoomCapacityPolicy = RoomCapacityPolicy(),
    private val clockMillis: () -> Long = { System.currentTimeMillis() }
) {

    private val lock = Any()

    // 在途预留计数与按 IP / Principal 细分
    private var inFlightGlobal: Int = 0
    private val inFlightPerIp = mutableMapOf<String, Int>()
    private val inFlightPerPrincipal = mutableMapOf<String, Int>()

    // 正式记账记录: roomId -> RoomAccount
    data class RoomAccount(
        val roomId: String,
        val principal: String,
        val ip: String
    )

    private val committedRooms = mutableMapOf<String, RoomAccount>()

    // 全局速率滑动窗口：记录过去 1000ms 内的准入时间戳
    private val rateWindowTimestamps = ArrayDeque<Long>()

    /**
     * 建房准入预留句柄
     */
    interface Reservation {
        /**
         * 建房成功后提交转为正式记账（仅首次有效）
         */
        fun commit(roomId: String): Boolean

        /**
         * 失败或取消时释放在途预留（仅首次有效）
         */
        fun release(): Boolean
    }

    /**
     * 准入判定结果
     */
    sealed class Outcome {
        data class Accepted(val reservation: Reservation) : Outcome()
        object RateLimited : Outcome()
        object QuotaExceeded : Outcome()
    }

    /**
     * 单锁内原子完成全部检查与在途预留
     *
     * @param principal 请求方逻辑身份（如 "lobby:token"）
     * @param remoteIp 请求方来源 IP 地址
     * @return [Outcome] 包含成功预留或明确拒绝原因
     */
    fun tryReserveOutcome(principal: String, remoteIp: String): Outcome {
        val now = clockMillis()
        synchronized(lock) {
            // 惰性清理过期或离开 LOBBY 的房间记账
            pruneStaleRoomsLocked()

            // 1. 全局速率滑动窗口检查
            while (rateWindowTimestamps.isNotEmpty() && rateWindowTimestamps.peekFirst() <= now - 1000L) {
                rateWindowTimestamps.pollFirst()
            }
            if (rateWindowTimestamps.size >= capacityPolicy.globalCreateRoomRatePerSecond) {
                return Outcome.RateLimited
            }

            // 2. 全局容量检查
            val currentRooms = registry.count()
            if (currentRooms + inFlightGlobal >= registry.maxRooms) {
                return Outcome.QuotaExceeded
            }

            // 3. 单 IP 未开局房间配额检查
            val ipUnstarted = countUnstartedForIpLocked(remoteIp)
            val ipInFlight = inFlightPerIp[remoteIp] ?: 0
            if (ipUnstarted + ipInFlight >= capacityPolicy.maxUnstartedRoomsPerIp) {
                return Outcome.QuotaExceeded
            }

            // 4. 逻辑主体未开局房间配额检查
            val principalUnstarted = countUnstartedForPrincipalLocked(principal)
            val principalInFlight = inFlightPerPrincipal[principal] ?: 0
            if (principalUnstarted + principalInFlight >= capacityPolicy.maxUnstartedRoomsPerPrincipal) {
                return Outcome.QuotaExceeded
            }

            // 全部通过：原子入队速率窗口并递增在途预留
            rateWindowTimestamps.addLast(now)
            inFlightGlobal++
            inFlightPerIp[remoteIp] = ipInFlight + 1
            inFlightPerPrincipal[principal] = principalInFlight + 1

            val reservationConsumed = AtomicBoolean(false)
            val reservation = object : Reservation {
                override fun commit(roomId: String): Boolean {
                    if (!reservationConsumed.compareAndSet(false, true)) {
                        return false
                    }
                    synchronized(this@CreateRoomAdmission.lock) {
                        decrementInFlightLocked(remoteIp, principal)
                        committedRooms[roomId] = RoomAccount(
                            roomId = roomId,
                            principal = principal,
                            ip = remoteIp
                        )
                    }
                    return true
                }

                override fun release(): Boolean {
                    if (!reservationConsumed.compareAndSet(false, true)) {
                        return false
                    }
                    synchronized(this@CreateRoomAdmission.lock) {
                        decrementInFlightLocked(remoteIp, principal)
                    }
                    return true
                }
            }

            return Outcome.Accepted(reservation)
        }
    }

    /**
     * 兼容接口：成功返回 Reservation，失败返回 null
     */
    fun tryReserve(principal: String, remoteIp: String): Reservation? {
        return when (val outcome = tryReserveOutcome(principal, remoteIp)) {
            is Outcome.Accepted -> outcome.reservation
            else -> null
        }
    }

    private fun decrementInFlightLocked(ip: String, principal: String) {
        inFlightGlobal = (inFlightGlobal - 1).coerceAtLeast(0)
        val curIp = inFlightPerIp[ip] ?: 0
        if (curIp <= 1) {
            inFlightPerIp.remove(ip)
        } else {
            inFlightPerIp[ip] = curIp - 1
        }
        val curPrin = inFlightPerPrincipal[principal] ?: 0
        if (curPrin <= 1) {
            inFlightPerPrincipal.remove(principal)
        } else {
            inFlightPerPrincipal[principal] = curPrin - 1
        }
    }

    private fun countUnstartedForIpLocked(ip: String): Int {
        var count = 0
        for (acc in committedRooms.values) {
            if (acc.ip == ip) {
                val room = registry.getRoom(acc.roomId)
                if (room != null && !room.closed && room.lifecycle == ProtocolRoomLifecycle.LOBBY) {
                    count++
                }
            }
        }
        return count
    }

    private fun countUnstartedForPrincipalLocked(principal: String): Int {
        var count = 0
        for (acc in committedRooms.values) {
            if (acc.principal == principal) {
                val room = registry.getRoom(acc.roomId)
                if (room != null && !room.closed && room.lifecycle == ProtocolRoomLifecycle.LOBBY) {
                    count++
                }
            }
        }
        return count
    }

    private fun pruneStaleRoomsLocked() {
        committedRooms.entries.removeIf { (roomId, _) ->
            val room = registry.getRoom(roomId)
            room == null || room.closed || room.lifecycle != ProtocolRoomLifecycle.LOBBY
        }
    }

    /**
     * 对账式清账：由巡检通道同步调用，以 registry.activeRooms() 为准
     */
    fun reconcile(reg: RoomRegistry = registry) {
        synchronized(lock) {
            committedRooms.entries.removeIf { (roomId, _) ->
                val room = reg.getRoom(roomId)
                room == null || room.closed || room.lifecycle != ProtocolRoomLifecycle.LOBBY
            }
        }
    }

    /**
     * 显式注销房间记账
     */
    fun onRoomClosed(roomId: String) {
        synchronized(lock) {
            committedRooms.remove(roomId)
        }
    }

    /**
     * 查询指定房间的创建者身份
     */
    fun getCreatorPrincipal(roomId: String): String? {
        synchronized(lock) {
            return committedRooms[roomId]?.principal
        }
    }

    // 可观测性指标查询
    val activeReservationCount: Int get() = synchronized(lock) { inFlightGlobal }
    val committedRoomCount: Int get() = synchronized(lock) { committedRooms.size }

    fun unstartedRoomsForIp(ip: String): Int = synchronized(lock) { countUnstartedForIpLocked(ip) }
    fun unstartedRoomsForPrincipal(principal: String): Int = synchronized(lock) { countUnstartedForPrincipalLocked(principal) }

    companion object {
        /**
         * 从 SocketAddress 中提取远端 IP 字符串
         */
        fun extractRemoteIp(address: SocketAddress?): String {
            if (address is InetSocketAddress) {
                val addr = address.address
                if (addr != null) {
                    val host = addr.hostAddress
                    if (!host.isNullOrBlank()) {
                        return host
                    }
                }
                val hostString = address.hostString
                if (!hostString.isNullOrBlank()) {
                    return hostString
                }
            }
            return "unknown"
        }
    }
}

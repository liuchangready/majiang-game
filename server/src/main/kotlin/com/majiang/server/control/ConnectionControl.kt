package com.majiang.server.control

import com.majiang.timing.MonotonicClock
import com.majiang.timing.SystemMonotonicClock

/**
 * 控制者角色 (V4 服务端连接控制)
 */
enum class ControllerRole {
    /** 真人玩家 */
    HUMAN,

    /** 原生电脑 AI 玩家 */
    COMPUTER,

    /** 掉线或临时托管状态下的 AI 接管 */
    TAKEOVER
}

/**
 * 网络连接状态
 */
enum class ConnectionStatus {
    CONNECTED,
    DISCONNECTED
}

/**
 * 座位连接与控制状态 DTO
 *
 * @property seat 座位号
 * @property status 连接状态（已连接/已断线）
 * @property role 当前控制者角色
 * @property controlEpoch 控制权代次（每次角色变更时自增）
 * @property disconnectedAtMillis 断线单调毫秒时间戳（若已连接则为 null）
 * @property graceDeadlineMillis 宽限期截止单调毫秒时间戳（若无宽限或已到期则为 null）
 * @property aiProfileId 托管或 AI 采用的配置标识
 */
data class SeatConnectionState(
    val seat: Int,
    val status: ConnectionStatus = ConnectionStatus.CONNECTED,
    val role: ControllerRole = ControllerRole.HUMAN,
    val controlEpoch: Long = 0L,
    val disconnectedAtMillis: Long? = null,
    val graceDeadlineMillis: Long? = null,
    val aiProfileId: String? = null
) {
    val isConnected: Boolean get() = status == ConnectionStatus.CONNECTED
    val isDisconnected: Boolean get() = status == ConnectionStatus.DISCONNECTED
    val isHuman: Boolean get() = role == ControllerRole.HUMAN
    val isTakenOver: Boolean get() = role == ControllerRole.TAKEOVER
    val isComputer: Boolean get() = role == ControllerRole.COMPUTER
    val isAutomated: Boolean get() = role != ControllerRole.HUMAN
}

/**
 * 座位连接与接管控制器 (ConnectionControl)
 *
 * 负责追踪各座位的网络连接状态、掉线检测、宽限期计时、AI 自动接管与重新接管。
 * 纯状态逻辑，保留稳定的座位身份（seatId）与自增控制代次（controlEpoch），不自行修改底层麻将领域状态。
 *
 * 核心契约：
 * 1. [disconnect]: 记录断线，根据 [DisconnectPolicy] 决定立即接管、开启宽限倒计时或仅走超时策略；
 * 2. [checkGraceExpiration] / [pollPendingTakeovers]: 宽限期到达后将控制者原子切换为 TAKEOVER 并自增代次；
 * 3. [reconnect]: 恢复连接状态，清除断线与宽限时间；
 * 4. [reclaimControl]: 真人重连后收回控制权，角色还原为 HUMAN 并自增代次。
 */
class ConnectionControl(
    val policy: DisconnectPolicy = DisconnectPolicy(),
    val clock: MonotonicClock = SystemMonotonicClock,
    val seatCount: Int = 4,
    initialRoles: Map<Int, ControllerRole> = emptyMap()
) {
    init {
        require(seatCount in 1..4) { "seatCount must be between 1 and 4, got $seatCount" }
    }

    private val seatStates = Array(seatCount) { seat ->
        SeatConnectionState(
            seat = seat,
            status = ConnectionStatus.CONNECTED,
            role = initialRoles[seat] ?: ControllerRole.HUMAN,
            controlEpoch = 0L
        )
    }

    private fun checkSeat(seat: Int) {
        require(seat in 0 until seatCount) { "Seat must be in 0 until $seatCount, got $seat" }
    }

    /**
     * 查询指定座位的当前连接与控制状态
     */
    @Synchronized
    fun seatState(seat: Int): SeatConnectionState {
        checkSeat(seat)
        return seatStates[seat]
    }

    /** 别名查询方法 */
    @Synchronized
    fun state(seat: Int): SeatConnectionState = seatState(seat)

    @Synchronized
    fun getSeatState(seat: Int): SeatConnectionState = seatState(seat)

    /** 查询指定座位的当前控制权代次 */
    @Synchronized
    fun getEpoch(seat: Int): Long = seatState(seat).controlEpoch

    @Synchronized
    fun controlEpoch(seat: Int): Long = seatState(seat).controlEpoch

    /** 查询指定座位是否处于已连接状态 */
    @Synchronized
    fun isConnected(seat: Int): Boolean = seatState(seat).isConnected

    /** 查询指定座位的当前控制者角色 */
    @Synchronized
    fun getRole(seat: Int): ControllerRole = seatState(seat).role

    @Synchronized
    fun controllerRole(seat: Int): ControllerRole = seatState(seat).role

    /** 查询指定座位是否为真人控制 */
    @Synchronized
    fun isHuman(seat: Int): Boolean = seatState(seat).isHuman

    /** 查询指定座位是否处于临时接管状态 */
    @Synchronized
    fun isTakenOver(seat: Int): Boolean = seatState(seat).isTakenOver

    /** 查询指定座位是否为原生电脑 AI */
    @Synchronized
    fun isComputer(seat: Int): Boolean = seatState(seat).isComputer

    /**
     * 处理座位断线
     *
     * - 若该座位已处于断线状态，幂等返回，不重置断线时刻与宽限期；
     * - 针对 [DisconnectMode.IMMEDIATE_AI]：立即切换为 [ControllerRole.TAKEOVER]，自增 controlEpoch；
     * - 针对 [DisconnectMode.GRACE_THEN_AI]：记录断线时刻并计算 graceDeadlineMillis，保持原控制者角色与代次；
     * - 针对 [DisconnectMode.TIMEOUT_ONLY]：记录断线，不设置宽限截止，保持原角色。
     */
    @Synchronized
    fun disconnect(seat: Int, atMillis: Long = clock.nowMillis()): SeatConnectionState {
        checkSeat(seat)
        val current = seatStates[seat]
        if (current.isDisconnected) {
            // 已断线，防重复抖动
            return current
        }

        val updated = when (policy.mode) {
            DisconnectMode.IMMEDIATE_AI -> {
                current.copy(
                    status = ConnectionStatus.DISCONNECTED,
                    role = ControllerRole.TAKEOVER,
                    controlEpoch = current.controlEpoch + 1L,
                    disconnectedAtMillis = atMillis,
                    graceDeadlineMillis = null,
                    aiProfileId = policy.aiProfileId
                )
            }

            DisconnectMode.GRACE_THEN_AI -> {
                if (current.role == ControllerRole.HUMAN) {
                    current.copy(
                        status = ConnectionStatus.DISCONNECTED,
                        disconnectedAtMillis = atMillis,
                        graceDeadlineMillis = Math.addExact(atMillis, policy.graceMillis),
                        aiProfileId = policy.aiProfileId
                    )
                } else {
                    current.copy(
                        status = ConnectionStatus.DISCONNECTED,
                        disconnectedAtMillis = atMillis,
                        graceDeadlineMillis = null,
                        aiProfileId = current.aiProfileId ?: policy.aiProfileId
                    )
                }
            }

            DisconnectMode.TIMEOUT_ONLY -> {
                current.copy(
                    status = ConnectionStatus.DISCONNECTED,
                    disconnectedAtMillis = atMillis,
                    graceDeadlineMillis = null,
                    aiProfileId = null
                )
            }
        }

        seatStates[seat] = updated
        return updated
    }

    /** 别名方法 */
    @Synchronized
    fun onDisconnected(seat: Int, atMillis: Long = clock.nowMillis()): SeatConnectionState =
        disconnect(seat, atMillis)

    /**
     * 检查并触发指定座位的宽限期到期接管
     *
     * @param expectedEpoch 可选的预期控制代次，若提供且与当前代次不匹配则视为过时代次任务直接返回 false 无副作用
     * @return 若宽限期到期并触发了 AI 接管则返回 true；否则返回 false
     */
    @Synchronized
    fun checkGraceExpiration(
        seat: Int,
        nowMillis: Long = clock.nowMillis(),
        expectedEpoch: Long? = null
    ): Boolean {
        checkSeat(seat)
        val current = seatStates[seat]
        if (expectedEpoch != null && current.controlEpoch != expectedEpoch) {
            return false
        }
        if (current.isDisconnected && current.role == ControllerRole.HUMAN && current.graceDeadlineMillis != null) {
            if (nowMillis >= current.graceDeadlineMillis) {
                seatStates[seat] = current.copy(
                    role = ControllerRole.TAKEOVER,
                    controlEpoch = current.controlEpoch + 1L,
                    graceDeadlineMillis = null
                )
                return true
            }
        }
        return false
    }

    /** 别名方法 */
    @Synchronized
    fun expireGrace(
        seat: Int,
        nowMillis: Long = clock.nowMillis(),
        expectedEpoch: Long? = null
    ): Boolean = checkGraceExpiration(seat, nowMillis, expectedEpoch)

    /**
     * 判断指定座位是否已到期需要接管（纯只读判断）
     */
    @Synchronized
    fun isTakeoverNeeded(
        seat: Int,
        nowMillis: Long = clock.nowMillis(),
        expectedEpoch: Long? = null
    ): Boolean {
        checkSeat(seat)
        val current = seatStates[seat]
        if (expectedEpoch != null && current.controlEpoch != expectedEpoch) return false
        return current.isDisconnected &&
                current.role == ControllerRole.HUMAN &&
                current.graceDeadlineMillis != null &&
                nowMillis >= current.graceDeadlineMillis
    }

    /**
     * 轮询检查所有处于宽限期的座位，对到期座位执行接管
     *
     * @return 触发接管的座位列表
     */
    @Synchronized
    fun pollPendingTakeovers(nowMillis: Long = clock.nowMillis()): List<Int> {
        val takenOverSeats = mutableListOf<Int>()
        for (seat in 0 until seatCount) {
            if (checkGraceExpiration(seat, nowMillis)) {
                takenOverSeats.add(seat)
            }
        }
        return takenOverSeats
    }

    /**
     * 处理座位重新连接
     *
     * - 状态置为 [ConnectionStatus.CONNECTED]，清空断线与宽限期记录；
     * - 若 [reclaim] 为 true 且当前处于接管状态，同步收回控制权（角色还原为 HUMAN，自增代次）；
     * - 若 [reclaim] 为 false，保留当前角色，后续由客户端显式调用 [reclaimControl] 收回。
     */
    @Synchronized
    fun reconnect(
        seat: Int,
        atMillis: Long = clock.nowMillis(),
        reclaim: Boolean = false
    ): SeatConnectionState {
        checkSeat(seat)
        val current = seatStates[seat]

        val shouldReclaimRole = reclaim && current.role != ControllerRole.HUMAN
        val nextRole = if (shouldReclaimRole) ControllerRole.HUMAN else current.role
        val nextEpoch = if (shouldReclaimRole) current.controlEpoch + 1L else current.controlEpoch

        if (current.isConnected && !shouldReclaimRole) {
            return current
        }

        val updated = current.copy(
            status = ConnectionStatus.CONNECTED,
            role = nextRole,
            controlEpoch = nextEpoch,
            disconnectedAtMillis = null,
            graceDeadlineMillis = null,
            aiProfileId = if (shouldReclaimRole) null else current.aiProfileId
        )
        seatStates[seat] = updated
        return updated
    }

    /** 别名方法 */
    @Synchronized
    fun onReconnected(
        seat: Int,
        atMillis: Long = clock.nowMillis(),
        reclaim: Boolean = false
    ): SeatConnectionState = reconnect(seat, atMillis, reclaim)

    /**
     * 真人收回控制权 (Reclaim Control)
     *
     * 将座位的控制者还原为 [ControllerRole.HUMAN]，自增 controlEpoch 废止先前接管 AI 的未决决策凭据。
     */
    @Synchronized
    fun reclaimControl(seat: Int): SeatConnectionState {
        checkSeat(seat)
        val current = seatStates[seat]
        if (current.role == ControllerRole.HUMAN) {
            return current
        }

        val updated = current.copy(
            role = ControllerRole.HUMAN,
            controlEpoch = current.controlEpoch + 1L,
            aiProfileId = null
        )
        seatStates[seat] = updated
        return updated
    }

    /** 别名方法 */
    @Synchronized
    fun reclaim(seat: Int): SeatConnectionState = reclaimControl(seat)

    /**
     * 请求切换控制权（接管或收回）
     */
    @Synchronized
    fun requestControl(seat: Int, takeover: Boolean): SeatConnectionState {
        checkSeat(seat)
        val current = seatStates[seat]
        return if (takeover) {
            if (current.role == ControllerRole.TAKEOVER) {
                current
            } else {
                val updated = current.copy(
                    role = ControllerRole.TAKEOVER,
                    controlEpoch = current.controlEpoch + 1L,
                    aiProfileId = policy.aiProfileId
                )
                seatStates[seat] = updated
                updated
            }
        } else {
            reclaimControl(seat)
        }
    }

    /**
     * 显式设置座位角色
     */
    @Synchronized
    fun setRole(seat: Int, newRole: ControllerRole, aiProfileId: String? = null): SeatConnectionState {
        checkSeat(seat)
        val current = seatStates[seat]
        if (current.role == newRole) {
            return current
        }
        val updated = current.copy(
            role = newRole,
            controlEpoch = current.controlEpoch + 1L,
            aiProfileId = aiProfileId
        )
        seatStates[seat] = updated
        return updated
    }

    /**
     * 获取所有座位的连接状态快照
     */
    @Synchronized
    fun allSeatStates(): List<SeatConnectionState> = seatStates.toList()
}

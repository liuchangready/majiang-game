package com.majiang.access

/**
 * 席位连接与控制视图 (SeatControlView)
 *
 * @property seat 座位号 (0..3)
 * @property connected 网络连接状态
 * @property role 控制者角色，严格为 "HUMAN", "COMPUTER", "TAKEOVER" 之一
 * @property controlEpoch 权威 Coordinator 控制代次，与决策令牌一致
 */
data class SeatControlView(
    val seat: Int,
    val connected: Boolean,
    val role: String,
    val controlEpoch: Long
) {
    init {
        require(seat in 0..3) { "seat must be in 0..3, got $seat" }
        require(role in VALID_ROLES) { "role must be one of $VALID_ROLES, got '$role'" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0, got $controlEpoch" }
    }

    fun deepCopy(): SeatControlView = copy()

    companion object {
        val VALID_ROLES: Set<String> = setOf("HUMAN", "COMPUTER", "TAKEOVER")
    }
}

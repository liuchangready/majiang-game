package com.majiang.server.api

/**
 * 加入房间请求 DTO
 *
 * @property roomId 目标房间号（不可为空白）
 * @property playerName 玩家显示名，缺省为 null（服务端缺省为 "Player"）
 * @property preferSeat 期望座位 (0..3)，被占用时由服务端分配；null 表示由服务端自动分配
 */
data class JoinRoomRequest(
    val roomId: String,
    val playerName: String? = null,
    val preferSeat: Int? = null
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        if (playerName != null) {
            require(playerName.isNotBlank()) { "playerName cannot be blank" }
        }
        if (preferSeat != null) {
            require(preferSeat in 0..3) { "preferSeat must be in 0..3: $preferSeat" }
        }
    }
}

package com.majiang.server.api

/**
 * 房间摘要信息 DTO (供大厅列表查询)
 *
 * @property roomId 房间唯一标识（不可为空白）
 * @property roomName 房间显示名称（不可为空白）
 * @property hostPlayerName 房主玩家显示名（不可为空白）
 * @property playerCount 当前房间内已占用的玩家数 (1..maxPlayers)
 * @property maxPlayers 最大玩家数（默认为 4，必须 >= 1）
 * @property matchStarted 比赛是否已经开始
 * @property configSummary 赛制规则配置的人读摘要文本
 */
data class RoomSummary(
    val roomId: String,
    val roomName: String,
    val hostPlayerName: String,
    val playerCount: Int,
    val maxPlayers: Int = 4,
    val matchStarted: Boolean,
    val configSummary: String = ""
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(roomName.isNotBlank()) { "roomName cannot be blank" }
        require(hostPlayerName.isNotBlank()) { "hostPlayerName cannot be blank" }
        require(maxPlayers >= 1) { "maxPlayers must be >= 1: $maxPlayers" }
        require(playerCount in 1..maxPlayers) {
            "playerCount must be in 1..$maxPlayers: $playerCount"
        }
    }
}

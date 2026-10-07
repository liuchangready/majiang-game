package com.majiang.server.api

import com.majiang.transport.api.ProtocolPublicSnapshot

/**
 * 房间观战绑定成功结果 DTO (SpectatorBinding)
 *
 * 观战加入或断线恢复成功后下发，确立客户端与特定房间的观战绑定关系，
 * 并携带初始公开基线快照。
 *
 * 严格契约：绝不包含玩家席位 (seat)、玩家私有快照或决策选项。
 *
 * @property roomId 8 位房间号（不可为空白）
 * @property spectatorId 观战者标识（不可为空白）
 * @property resumeToken 观战者断线恢复令牌（不可为空白，toString 中必须脱敏）
 * @property snapshot 绑定后的初始公开快照基线（非空）
 */
data class SpectatorBinding(
    val roomId: String,
    val spectatorId: String,
    val resumeToken: String,
    val snapshot: ProtocolPublicSnapshot
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
        require(resumeToken.isNotBlank()) { "resumeToken cannot be blank" }
    }

    override fun toString(): String {
        return "SpectatorBinding(roomId=$roomId, spectatorId=$spectatorId, resumeToken=***, snapshot=$snapshot)"
    }
}

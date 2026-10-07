package com.majiang.server.api

/**
 * 观战恢复 / 断线重连请求 DTO (ResumeSpectatorRequest)
 *
 * @property roomId 目标房间号（不可为空白）
 * @property spectatorId 观战者标识（不可为空白）
 * @property resumeToken 观战者恢复令牌（不可为空白，toString 中必须脱敏）
 * @property checkpoint 客户端当前持有的公开流检查点基线（可选）
 * @property protocolVersion 客户端协议版本
 */
data class ResumeSpectatorRequest(
    val roomId: String,
    val spectatorId: String,
    val resumeToken: String,
    val checkpoint: PublicCheckpoint? = null,
    val protocolVersion: Int = ApiProtocol.PROTOCOL_VERSION
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(spectatorId.isNotBlank()) { "spectatorId cannot be blank" }
        require(resumeToken.isNotBlank()) { "resumeToken cannot be blank" }
        require(protocolVersion >= 1) { "protocolVersion must be >= 1: $protocolVersion" }
    }

    override fun toString(): String {
        return "ResumeSpectatorRequest(roomId=$roomId, spectatorId=$spectatorId, resumeToken=***, checkpoint=$checkpoint, protocolVersion=$protocolVersion)"
    }
}

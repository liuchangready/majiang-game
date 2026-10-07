package com.majiang.server.api

/**
 * 加入观战请求 DTO (JoinSpectatorRequest)
 *
 * @property roomId 目标房间号（不可为空白）
 * @property spectatorName 观战者显示名，缺省为 null（服务端缺省为 "Spectator"）
 * @property protocolVersion 客户端协议版本，服务端只接受当前支持的版本
 */
data class JoinSpectatorRequest(
    val roomId: String,
    val spectatorName: String? = null,
    val protocolVersion: Int = ApiProtocol.PROTOCOL_VERSION
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(protocolVersion >= 1) { "protocolVersion must be >= 1: $protocolVersion" }
        if (spectatorName != null) {
            require(spectatorName.isNotBlank()) { "spectatorName cannot be blank" }
        }
    }
}

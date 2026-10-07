package com.majiang.server.api

import com.majiang.transport.api.ProtocolMatchConfig

/**
 * 创建房间请求 DTO
 *
 * @property playerName 房主玩家显示名，缺省为 null（服务端缺省为 "Host"）
 * @property config 比赛规则配置，缺省为 null（服务端缺省为默认半庄战）
 * @property protocolVersion 客户端协议版本，服务端只接受当前支持的版本
 */
data class CreateRoomRequest(
    val playerName: String? = null,
    val config: ProtocolMatchConfig? = null,
    val protocolVersion: Int = ApiProtocol.PROTOCOL_VERSION
) {
    init {
        require(protocolVersion >= 1) { "protocolVersion must be >= 1: $protocolVersion" }
        if (playerName != null) {
            require(playerName.isNotBlank()) { "playerName cannot be blank" }
        }
    }
}

package com.majiang.server.api

import com.majiang.transport.api.ProtocolPlayerSnapshot

/**
 * 房间席位绑定成功结果 DTO
 *
 * 建房、加入房间或断线重连成功后下发，确立客户端与特定房间席位的绑定关系，
 * 并携带初始视图快照（等价于立刻收到一次初始 PUSH_UPDATE）。
 *
 * @property roomId 8 位房间号（不可为空白）
 * @property seat 绑定的座位索引 (0..3)
 * @property resumeToken 断线恢复令牌（客户端安全持久化，日志与 toString 中必须脱敏）
 * @property snapshot 绑定后的初始完整快照视图（非空）
 */
data class RoomBinding(
    val roomId: String,
    val seat: Int,
    val resumeToken: String,
    val snapshot: ProtocolPlayerSnapshot
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(resumeToken.isNotBlank()) { "resumeToken cannot be blank" }
    }

    override fun toString(): String {
        return "RoomBinding(roomId=$roomId, seat=$seat, resumeToken=***, snapshot=$snapshot)"
    }
}

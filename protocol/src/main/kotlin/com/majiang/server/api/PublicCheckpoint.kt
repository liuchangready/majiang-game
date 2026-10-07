package com.majiang.server.api

/**
 * 传输层与协议层公开流检查点 (PublicCheckpoint)
 *
 * 用于独立观战流 (PUBLIC) 的状态恢复与对账基线确认。
 * 强制要求绑定完整 sessionId 与 syncEpoch 代次，裸游标禁止跨流或跨代次混用。
 *
 * @property sessionId 对局会话标识（不可为空白）
 * @property syncEpoch 公开流代次（不可为空白）
 * @property cursor 客户端当前持有的单调递增游标（>= 0）
 */
data class PublicCheckpoint(
    val sessionId: String,
    val syncEpoch: String,
    val cursor: Long
) {
    init {
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(syncEpoch.isNotBlank()) { "syncEpoch cannot be blank" }
        require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
    }
}

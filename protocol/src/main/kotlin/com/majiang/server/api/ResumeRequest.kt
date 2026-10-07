package com.majiang.server.api

/**
 * 断线重连 / 会话恢复请求 DTO
 *
 * @property roomId 原房间号（不可为空白）
 * @property token 原断线恢复令牌 resumeToken（不可为空白，toString 中必须脱敏）
 * @property cursor 客户端最后持有的同步游标；null 或 0 表示请求全量快照
 */
data class ResumeRequest(
    val roomId: String,
    val token: String,
    val cursor: Long? = null
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(token.isNotBlank()) { "token cannot be blank" }
        if (cursor != null) {
            require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
        }
    }

    override fun toString(): String {
        return "ResumeRequest(roomId=$roomId, token=***, cursor=$cursor)"
    }
}

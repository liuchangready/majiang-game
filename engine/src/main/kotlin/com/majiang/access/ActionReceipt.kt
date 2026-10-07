package com.majiang.access

/**
 * 玩家操作回执 (ActionReceipt)
 *
 * @property requestId 请求去重唯一标识
 * @property status 当前回执状态 (REJECTED, RECORDED, APPLIED)
 * @property reason 若被拒绝或记录，附带的稳定原因说明
 * @property cursor 该操作提交生效后对应的同步游标；若未提交生效则为 null
 */
data class ActionReceipt(
    val requestId: String,
    val status: ReceiptStatus,
    val reason: String? = null,
    val cursor: Long? = null
) {
    init {
        if (cursor != null) {
            require(cursor >= 0L) { "cursor must be >= 0, got $cursor" }
        }
    }
}

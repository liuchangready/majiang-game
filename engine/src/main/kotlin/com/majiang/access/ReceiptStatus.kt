package com.majiang.access

/**
 * 操作回执状态
 *
 * - REJECTED: 未记录该操作，不改变牌局状态，包含明确原因码
 * - RECORDED: 响应选择已记录，尚待其他座位响应或优先级裁决
 * - APPLIED: 对应状态变化已经提交并生效
 */
enum class ReceiptStatus {
    REJECTED,
    RECORDED,
    APPLIED
}

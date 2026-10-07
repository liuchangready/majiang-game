package com.majiang.transport.udp

/**
 * UDP 自封装数据报包类型枚举 (UdpPacketType)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.3 包类型全表。
 * 低 6 位（0x01 ~ 0x3F）标识具体类型，高 2 位为 flags 掩码保留。
 */
enum class UdpPacketType(val typeId: Int, val description: String) {
    /** 0x01: 客户端发起请求 (C->S)，重传直至 RSP */
    REQ(0x01, "Request"),

    /** 0x02: 服务端响应 (S->C)，重传直至 ACK */
    RSP(0x02, "Response"),

    /** 0x03: 服务端推送更新 (S->C)，游标序自愈 */
    PUSH(0x03, "PushUpdate"),

    /** 0x04: 纯确认包 (双向)，无载荷 */
    ACK(0x04, "Acknowledge"),

    /** 0x05: 客户端请求重传推送缺口 (C->S) */
    RESEND(0x05, "ResendGap"),

    /** 0x06: 客户端心跳请求 (C->S) */
    PING(0x06, "Ping"),

    /** 0x07: 服务端心跳响应 (S->C) */
    PONG(0x07, "Pong"),

    /** 0x08: 分片承载包 (双向)，重组后按原类型处理 */
    FRAG(0x08, "Fragment"),

    /** 0x09: 连接终结通知 (S->C)，顶号/房关/停服 */
    KICK(0x09, "Kick");

    companion object {
        private val BY_ID = entries.associateBy { it.typeId }

        /**
         * 按 6-bit 类型 ID 查找对应包类型，未定义时返回 null
         */
        fun fromTypeId(typeId: Int): UdpPacketType? = BY_ID[typeId]
    }
}

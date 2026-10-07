package com.majiang.transport.udp

/**
 * UDP 自封装二进制协议常量与参数规范 (UdpProtocol)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7 全部（数据报格式、包类型、可靠性、参数表）。
 * 所有协议常量集中定义于此，禁止在业务与测试逻辑中散写硬编码字面量。
 */
object UdpProtocol {
    // -------------------------------------------------------------
    // §7.2 数据报与头部结构常量
    // -------------------------------------------------------------

    /** 协议魔数: 0x4D47 ("MG") */
    const val MAGIC: Short = 0x4D47.toShort()

    /** 协议版本号: 4 (V4 基线) */
    const val VERSION: Int = 4

    /** 22 字节二进制固定头部长度 */
    const val HEADER_SIZE: Int = 22

    /** 单个 UDP 数据报总长上限 (1200 字节，避免 IP 分片) */
    const val MAX_DATAGRAM_SIZE: Int = 1200

    /** 单个数据报最大载荷长度 (1200 - 22 = 1178 字节) */
    const val MAX_PAYLOAD_SIZE: Int = MAX_DATAGRAM_SIZE - HEADER_SIZE

    /** packetType 字节高 2 位 flags: bit7 = zlib 压缩 */
    const val FLAG_ZLIB: Int = 0x80

    /** packetType 字节高 2 位 flags: bit6 = 加密预留位 */
    const val FLAG_ENCRYPTED: Int = 0x40

    /** flags 掩码 (高 2 位: 0xC0) */
    const val FLAGS_MASK: Int = 0xC0

    /** packetType 掩码 (低 6 位: 0x3F) */
    const val PACKET_TYPE_MASK: Int = 0x3F

    // -------------------------------------------------------------
    // §7.4-2 分片常量
    // -------------------------------------------------------------

    /** payload > 1090 字节触发分片 (1200 - 22 头 - 9 FRAG 子头 - 余量) */
    const val FRAGMENT_THRESHOLD_BYTES: Int = 1090

    /** 分片重组超时 (3 秒未齐整条丢弃) */
    const val FRAGMENT_TIMEOUT_MILLIS: Long = 3000L

    /** 分片重组并发缓冲上限 (同时最多 8 条在途消息) */
    const val MAX_CONCURRENT_FRAGMENTS: Int = 8

    // -------------------------------------------------------------
    // §7.4-1, §7.6 可靠请求-响应 (RTO) 参数
    // -------------------------------------------------------------

    /** 初始重传超时 (200 毫秒) */
    const val INITIAL_RTO_MILLIS: Long = 200L

    /** RTO 退避乘数 (x1.5) */
    const val RTO_BACKOFF_FACTOR: Double = 1.5

    /** 最大重传超时上限 (3 秒) */
    const val MAX_RTO_MILLIS: Long = 3000L

    /** 累计无应答最大尝试次数 (5 次无应答判死) */
    const val MAX_REQUEST_ATTEMPTS: Int = 5

    /** 单请求超时截止上限 (10 秒，与 Future 超时对齐) */
    const val REQUEST_TIMEOUT_MILLIS: Long = 10000L

    // -------------------------------------------------------------
    // §7.4-3, §7.6 推送游标与重传窗口参数
    // -------------------------------------------------------------

    /** 服务端每座位保留最近已发推送包上限 (256 个) */
    const val RETRANSMIT_WINDOW_SIZE: Int = 256

    // -------------------------------------------------------------
    // §7.6 心跳与判死参数
    // -------------------------------------------------------------

    /** PING 发送间隔 (3 秒) */
    const val HEARTBEAT_PING_INTERVAL_MILLIS: Long = 3000L

    /** PONG 期望响应时间 (1 秒) */
    const val HEARTBEAT_PONG_EXPECTED_MILLIS: Long = 1000L

    /** 连续丢失 PONG 判死阈值 (3 次) */
    const val HEARTBEAT_MAX_MISSED_PONGS: Int = 3

    /** 双端入站判死超时 (10 秒无任何入站包) */
    const val INBOUND_TIMEOUT_MILLIS: Long = 10000L

    /** RTT 估计 EWMA 平滑权重 alpha (7/8 = 0.875) */
    const val RTT_EWMA_ALPHA: Double = 0.875

    // -------------------------------------------------------------
    // 稳定原因码 (供上层判恶意包、异常分类及连接状态流转)
    // -------------------------------------------------------------

    const val REASON_DATAGRAM_TOO_LARGE: String = "DATAGRAM_TOO_LARGE"
    const val REASON_DATAGRAM_TOO_SHORT: String = "DATAGRAM_TOO_SHORT"
    const val REASON_INVALID_MAGIC: String = "INVALID_MAGIC"
    const val REASON_INVALID_VERSION: String = "INVALID_PROTOCOL_VERSION"
    const val REASON_UNKNOWN_PACKET_TYPE: String = "UNKNOWN_PACKET_TYPE"
    const val REASON_PAYLOAD_LENGTH_MISMATCH: String = "PAYLOAD_LENGTH_MISMATCH"
    const val REASON_CRC_MISMATCH: String = "CRC_MISMATCH"

    const val REASON_DUPLICATE_REQUEST_ID: String = "DUPLICATE_REQUEST_ID"
    const val REASON_REQUEST_TIMEOUT: String = "REQUEST_TIMEOUT"
    const val REASON_READ_TIMEOUT: String = "READ_TIMEOUT"
    const val REASON_MISSED_HEARTBEATS: String = "MISSED_HEARTBEATS"

    const val KICK_DISPLACED_BY_NEW_CONNECTION: String = "DISPLACED_BY_NEW_CONNECTION"
    const val KICK_ROOM_CLOSED: String = "ROOM_CLOSED"
    const val KICK_SERVER_SHUTDOWN: String = "SERVER_SHUTDOWN"
}

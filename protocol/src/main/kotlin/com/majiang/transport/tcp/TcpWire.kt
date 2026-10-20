package com.majiang.transport.tcp

/**
 * TCP 线路协议常量与基础规格 (TcpWire, MJ-TCP/1 契约)
 *
 * 对应设计文档：doc/v4-重构/TCP-MIGRATION-DESIGN.md §3 与冻结决策 D1-D13。
 * 所有多字节整数采用大端序 (Big-Endian)。
 *
 * 帧头部格式 (固定 12 字节):
 * - 偏移 0..3 (4 字节): 魔数 ASCII "MJTC" (0x4D4A5443)
 * - 偏移 4 (1 字节): wireVersion = 1
 * - 偏移 5 (1 字节): type (1..10)
 * - 偏移 6..7 (2 字节): flags (v1 必须为 0)
 * - 偏移 8..11 (4 字节): payloadLength (无符号 32 位整数，1..1,048,576 字节)
 * - 偏移 12..12+N-1 (N 字节): payload (严格 UTF-8 JSON)
 *
 * 所有帧级常量必须集中于此对象，严禁业务与测试散写硬编码字面量。
 */
object TcpWire {
    /** 线路协议魔数字符串: "MJTC" */
    const val MAGIC_ASCII: String = "MJTC"

    /** 线路协议魔数整数表示 (Big-Endian 0x4D4A5443) */
    const val MAGIC_INT: Int = 0x4D4A5443

    /** 线路协议魔数字节数组 */
    val MAGIC_BYTES: ByteArray = byteArrayOf(
        0x4D.toByte(), // 'M'
        0x4A.toByte(), // 'J'
        0x54.toByte(), // 'T'
        0x43.toByte()  // 'C'
    )

    /** 传输线路版本 (MJ-TCP/1) */
    const val WIRE_VERSION: Int = 1

    /**
     * 业务契约版本。
     *
     * v2 起携带 D-07 结构化算分字段（capCode / yakuEntries / paymentPlan /
     * scoreDetailVersion）。v3 起携带袖里藏花他家回合 OFF_TURN_OFFER/RESERVED
     * 私有快照与 windowId 请求语义。双端对 HELLO/WELCOME 做硬相等校验，
     * 旧业务版本明确以 VERSION_MISMATCH 拒绝；不提供同进程旧/新版本混发或热迁移。
     * 传输帧格式仍为 MJ-TCP/1（[WIRE_VERSION] = 1，12 字节帧头），不因业务升版改变。
     */
    const val BUSINESS_VERSION: Int = 3

    // === 帧类型常量 (1..10) ===

    /** 客户端到服务端：握手发起帧 */
    const val FRAME_TYPE_HELLO: Int = 1

    /** 服务端到客户端：握手就绪应答帧 */
    const val FRAME_TYPE_WELCOME: Int = 2

    /** 客户端到服务端：业务请求信封帧 */
    const val FRAME_TYPE_REQ: Int = 3

    /** 服务端到客户端：业务响应信封帧 */
    const val FRAME_TYPE_RSP: Int = 4

    /** 服务端到客户端：私有玩家状态推送 */
    const val FRAME_TYPE_PLAYER_UPDATE: Int = 5

    /** 服务端到客户端：公开观战流推送 */
    const val FRAME_TYPE_PUBLIC_UPDATE: Int = 6

    /** 服务端到客户端：房间元数据更新推送 */
    const val FRAME_TYPE_ROOM_UPDATE: Int = 7

    /** 双向：心跳探测帧 (带 16 hex nonce) */
    const val FRAME_TYPE_PING: Int = 8

    /** 双向：心跳应答帧 (回显 nonce) */
    const val FRAME_TYPE_PONG: Int = 9

    /** 服务端到客户端（或尽力而为双向）：关闭通知帧 */
    const val FRAME_TYPE_CLOSE: Int = 10

    /** 合法帧类型闭区间 (1..10) */
    val FRAME_TYPES: IntRange = 1..10

    /** 头部固定字节大小 (12 字节) */
    const val HEADER_SIZE: Int = 12

    /** 单帧载荷最小字节数 (1 字节，禁止空载荷) */
    const val MIN_PAYLOAD_BYTES: Int = 1

    /** 单帧载荷最大字节数 (1,048,576 字节 = 1 MiB) */
    const val MAX_PAYLOAD_BYTES: Int = 1_048_576

    /** 单连接解码暂存上限 (64 KiB) */
    const val DECODE_SCRATCH_LIMIT: Int = 64 * 1024

    /** Wire v1 默认 flags 必须为 0 */
    const val FLAGS_DEFAULT: Short = 0

    /**
     * 判断指定帧类型是否在合法区间内
     */
    fun isValidFrameType(type: Int): Boolean = type in FRAME_TYPES

    /**
     * 获取帧类型的可读名称（用于日志与调试）
     */
    fun frameTypeName(type: Int): String = when (type) {
        FRAME_TYPE_HELLO -> "HELLO"
        FRAME_TYPE_WELCOME -> "WELCOME"
        FRAME_TYPE_REQ -> "REQ"
        FRAME_TYPE_RSP -> "RSP"
        FRAME_TYPE_PLAYER_UPDATE -> "PLAYER_UPDATE"
        FRAME_TYPE_PUBLIC_UPDATE -> "PUBLIC_UPDATE"
        FRAME_TYPE_ROOM_UPDATE -> "ROOM_UPDATE"
        FRAME_TYPE_PING -> "PING"
        FRAME_TYPE_PONG -> "PONG"
        FRAME_TYPE_CLOSE -> "CLOSE"
        else -> "UNKNOWN($type)"
    }
}

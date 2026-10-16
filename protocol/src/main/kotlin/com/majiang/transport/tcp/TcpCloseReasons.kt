package com.majiang.transport.tcp

import com.majiang.server.api.ApiProtocol
import java.util.Collections

/**
 * TCP 连接关闭原因稳定定义 (D4 冻结契约)
 *
 * 集中管理所有 TCP 稳定关闭原因码，禁止业务与测试散写硬编码字面量。
 *
 * 分类规则（D4，对齐 MR-02 ReconnectErrorClassifier 与设计 §6）：
 * - 永久原因（PERMANENT，客户端缺陷或已被顶号/房间关闭/服务端停服/本地主动关闭，不可自动重试，需升级或通知用户）：
 *   VERSION_MISMATCH, PROTOCOL_VIOLATION, DISPLACED_BY_NEW_CONNECTION, ROOM_CLOSED, SERVER_SHUTDOWN, LOCAL_CLOSED
 * - 临时原因（TRANSIENT，网络波动或服务端短暂过载，有界指数退避后可重试）：
 *   HANDSHAKE_TIMEOUT, HEARTBEAT_TIMEOUT, SERVER_OVERLOADED, SLOW_CONSUMER
 */
object TcpCloseReasons {
    // === 复用 ApiProtocol 稳定码 ===

    /** 服务端正常关机停服 */
    const val SERVER_SHUTDOWN: String = ApiProtocol.SERVER_SHUTDOWN

    /** 房间已解散或关闭 */
    const val ROOM_CLOSED: String = ApiProtocol.ROOM_CLOSED

    /** 因新连接顶号重连，旧连接被断开 */
    const val DISPLACED_BY_NEW_CONNECTION: String = ApiProtocol.DISPLACED_BY_NEW_CONNECTION

    /** 本地连接主动关闭 */
    const val LOCAL_CLOSED: String = ApiProtocol.LOCAL_CLOSED

    /** 心跳丢失 / 读超时断开 */
    const val HEARTBEAT_TIMEOUT: String = ApiProtocol.HEARTBEAT_TIMEOUT

    // === TCP 新增稳定码 (D4) ===

    /** 传输线路版本或业务版本不匹配（永久错误） */
    const val VERSION_MISMATCH: String = "VERSION_MISMATCH"

    /** 线路协议违规（魔数错误、flags 非法、超限长度、坏 UTF-8/JSON、外层 ID 不一致等）（永久错误） */
    const val PROTOCOL_VIOLATION: String = "PROTOCOL_VIOLATION"

    /** 建连后握手超时（5 秒未完成握手）（临时错误） */
    const val HANDSHAKE_TIMEOUT: String = "HANDSHAKE_TIMEOUT"

    /** 服务端过载（未握手桶超限 64 条或总连接超限）（临时错误） */
    const val SERVER_OVERLOADED: String = "SERVER_OVERLOADED"

    /** 慢客户端（发送队列超限 256 帧或 4 MiB）（临时错误） */
    const val SLOW_CONSUMER: String = "SLOW_CONSUMER"

    /**
     * 永久关闭原因集合（客户端缺陷/认证失效/停服/主动关闭，禁止自动重连）
     */
    val PERMANENT: Set<String> = Collections.unmodifiableSet(
        linkedSetOf(
            VERSION_MISMATCH,
            PROTOCOL_VIOLATION,
            DISPLACED_BY_NEW_CONNECTION,
            ROOM_CLOSED,
            SERVER_SHUTDOWN,
            LOCAL_CLOSED
        )
    )

    /**
     * 临时关闭原因集合（网络波动/服务端过载，可按退避策略重试）
     */
    val TRANSIENT: Set<String> = Collections.unmodifiableSet(
        linkedSetOf(
            HANDSHAKE_TIMEOUT,
            HEARTBEAT_TIMEOUT,
            SERVER_OVERLOADED,
            SLOW_CONSUMER
        )
    )

    /**
     * 全部已知关闭原因全集
     */
    val ALL: Set<String> = Collections.unmodifiableSet(
        linkedSetOf(
            SERVER_SHUTDOWN,
            ROOM_CLOSED,
            DISPLACED_BY_NEW_CONNECTION,
            LOCAL_CLOSED,
            HEARTBEAT_TIMEOUT,
            VERSION_MISMATCH,
            PROTOCOL_VIOLATION,
            HANDSHAKE_TIMEOUT,
            SERVER_OVERLOADED,
            SLOW_CONSUMER
        )
    )

    /**
     * 判断是否为永久错误
     */
    fun isPermanent(reason: String): Boolean = reason in PERMANENT

    /**
     * 判断是否为临时可重试错误
     */
    fun isTransient(reason: String): Boolean = reason in TRANSIENT
}

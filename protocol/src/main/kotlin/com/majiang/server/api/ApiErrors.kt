package com.majiang.server.api

import java.util.Collections

/**
 * 传输层与协议层权威错误码定义
 *
 * 对齐 SERVER_CLIENT_API.md §5 表格。
 *
 * 注意：业务性拒绝（如决策过期、非法动作、余额不足等）不是错误码，
 * 而是通过 ActionReceipt(status = REJECTED, reason = "...") 表达。
 */
object ApiErrors {
    /** 协议版本缺失或与服务端当前版本不符 */
    const val INVALID_PROTOCOL_VERSION = "INVALID_PROTOCOL_VERSION"

    /** 帧解析失败、未知请求类型或参数校验失败 */
    const val MALFORMED_REQUEST = "MALFORMED_REQUEST"

    /** 当前连接已绑定席位，禁止重复创建、加入或恢复房间 */
    const val ALREADY_BOUND = "ALREADY_BOUND"

    /** 目标房间不存在或已关闭 */
    const val ROOM_NOT_FOUND = "ROOM_NOT_FOUND"

    /** 房间席位已满 */
    const val ROOM_FULL = "ROOM_FULL"

    /** 服务端房间总数已达上限 */
    const val SERVER_FULL = "SERVER_FULL"

    /** 缺少必填字段：roomId */
    const val MISSING_ROOM_ID = "MISSING_ROOM_ID"

    /** 缺少必填字段：roomId 或 token */
    const val MISSING_ROOM_OR_TOKEN = "MISSING_ROOM_OR_TOKEN"

    /** 缺少必填字段：action choice 动作载荷 */
    const val MISSING_ACTION_CHOICE = "MISSING_ACTION_CHOICE"

    /** 缺少必填字段：shuffle plan 洗牌方案载荷 */
    const val MISSING_SHUFFLE_PLAN = "MISSING_SHUFFLE_PLAN"

    /** 连接未绑定席位即发送会话期对局命令 */
    const val UNAUTHENTICATED_SESSION = "UNAUTHENTICATED_SESSION"

    /** resumeToken 与当前席位绑定不匹配或已失效 */
    const val INVALID_TOKEN = "INVALID_TOKEN"

    /** 未知的请求类型 */
    const val UNKNOWN_REQUEST_TYPE = "UNKNOWN_REQUEST_TYPE"

    /** 请求标识相同但载荷或接口冲突 */
    const val CONFLICT = "CONFLICT"

    /** 服务端繁忙或在途请求达到上限 */
    const val SERVER_BUSY = "SERVER_BUSY"

    /** 服务端内部未知异常兜底 */
    const val INTERNAL_ERROR = "INTERNAL_ERROR"

    /** 请求作用域已过期 (D3 冻结契约) */
    const val REQUEST_SCOPE_EXPIRED = "REQUEST_SCOPE_EXPIRED"

    /** 全部已知标准错误码集合 */
    val ALL: Set<String> = Collections.unmodifiableSet(
        linkedSetOf(
            INVALID_PROTOCOL_VERSION,
            MALFORMED_REQUEST,
            ALREADY_BOUND,
            ROOM_NOT_FOUND,
            ROOM_FULL,
            SERVER_FULL,
            MISSING_ROOM_ID,
            MISSING_ROOM_OR_TOKEN,
            MISSING_ACTION_CHOICE,
            MISSING_SHUFFLE_PLAN,
            UNAUTHENTICATED_SESSION,
            INVALID_TOKEN,
            UNKNOWN_REQUEST_TYPE,
            CONFLICT,
            SERVER_BUSY,
            INTERNAL_ERROR,
            REQUEST_SCOPE_EXPIRED
        )
    )
}

/**
 * 协议版本与连接终结原因常量
 */
object ApiProtocol {
    /**
     * 权威业务协议版本号（v8：统一码牌千术，将洗牌方案收敛为单一 target + seeThrough）。
     *
     * v7 及更旧版本不再被双端接受：旧 initialTargets/drawTargets 字段已移除，HELLO/WELCOME
     * 硬相等校验按 VERSION_MISMATCH 拒绝，不做热迁移。
     * MJ-TCP/1 12 字节帧格式与 `TcpWire.WIRE_VERSION = 1` 保持不变。
     */
    const val PROTOCOL_VERSION = 8

    // === 连接终结 / KICK 原因码 ===

    /** 因新连接使用 resumeToken 顶号重连，旧连接被踢下线 */
    const val DISPLACED_BY_NEW_CONNECTION = "DISPLACED_BY_NEW_CONNECTION"

    /** 顶号下线同义常量（对齐简报声明） */
    const val DISCONNECTED_BY_NEW_CONNECTION = "DISCONNECTED_BY_NEW_CONNECTION"

    /** 房间已解散或关闭 */
    const val ROOM_CLOSED = "ROOM_CLOSED"

    /** 服务端正常关机停服 */
    const val SERVER_SHUTDOWN = "SERVER_SHUTDOWN"

    /** 网络读超时 / 心跳丢失断线 */
    const val READ_TIMEOUT = "READ_TIMEOUT"

    /** 心跳超时 */
    const val HEARTBEAT_TIMEOUT = "HEARTBEAT_TIMEOUT"

    /** 本地连接主动关闭 */
    const val LOCAL_CLOSED = "LOCAL_CLOSED"
}

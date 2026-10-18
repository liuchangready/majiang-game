package com.majiang.server.tcp

import com.majiang.server.room.RoomCapacityPolicy
import com.majiang.server.room.RoomRetentionPolicy
import com.majiang.transport.common.TransportClock
import com.majiang.transport.common.TransportScheduler

/**
 * TCP 权威服务器运行配置 (TcpServerConfig)
 *
 * 所有初值严格对齐 Design §4、Review D1–D13 及 T0 实测冻结值 (CLAUDE-TCP-T0-MEASUREMENT.md)；
 * 所有参数可注入，默认值即设计初值。
 */
data class TcpServerConfig(
    /** 监听主机绑定地址 */
    val host: String = "127.0.0.1",

    /** 监听端口 (0 表示使用系统分配的临时端口) */
    val port: Int = 0,

    /** 全局网络预算上限 (默认 256 MiB) */
    val globalBudgetBytes: Long = 256L * 1024 * 1024,

    /** 最大并发连接数 (默认 512) */
    val maxConnections: Int = 512,

    /** 未握手连接桶上限 (默认 64) */
    val maxUnhandshakedConnections: Int = 64,

    /** 握手超时时间 (默认 5 秒) */
    val handshakeTimeoutMillis: Long = 5000L,

    /** 客户端心跳上报间隔 (默认 3 秒) */
    val heartbeatIntervalMillis: Long = 3000L,

    /** 入站判死超时 (默认 10 秒无完整合法帧) */
    val idleDeadMillis: Long = 10000L,

    /** 不完整帧首字节起算硬截止时间 (默认 10 秒) */
    val incompleteFrameTimeoutMillis: Long = 10000L,

    /** 停滞写入超时 (默认连续 10 秒零写出进展) */
    val stalledWriteTimeoutMillis: Long = 10000L,

    /** 终态通知收尾最大排出时间 (默认 1 秒) */
    val terminalFlushTimeoutMillis: Long = 1000L,

    /** 单连接业务发送队列最大帧数 (默认 256) */
    val maxOutboundQueueFrames: Int = 256,

    /** 单连接业务发送队列最大字节数 (默认 4 MiB) */
    val maxOutboundQueueBytes: Long = 4L * 1024 * 1024,

    /** 控制帧优先级队列最大帧数 (默认 8) */
    val maxControlQueueFrames: Int = 8,

    /** 控制帧优先级队列最大字节数 (默认 8 KiB) */
    val maxControlQueueBytes: Long = 8L * 1024,

    /** 单连接接收串行邮箱最大帧数 (默认 64) */
    val maxMailboxFrames: Int = 64,

    /** 单连接接收串行邮箱最大字节数 (默认 4 MiB) */
    val maxMailboxBytes: Long = 4L * 1024 * 1024,

    /** 单连接单轮 selector 最大读取/写出字节数 (默认 64 KiB，UC-L05 公平调度) */
    val maxBytesPerRotation: Int = 64 * 1024,

    /** 单连接单轮 selector 最大读取/写出帧数 (默认 32，UC-L05 公平调度) */
    val maxFramesPerRotation: Int = 32,

    /** 单成员最大在途请求数 (默认 64) */
    val maxInFlightRequestsPerMember: Int = 64,

    /** 单成员已受理副作用记录容量 (默认 512) */
    val maxCompletedReceiptsPerMember: Int = 512,

    /** 大厅总副作用回执容量 (默认 1024) */
    val maxLobbySideEffectReceipts: Int = 1024,

    /** 大厅查询回执缓存容量 (默认 256) */
    val maxLobbyQueryReceipts: Int = 256,

    /** 作用域默认有效期 (默认 10 分钟) */
    val scopeValidityMillis: Long = 10 * 60 * 1000L,

    /** GET_REQUEST_SCOPE 限速间隔 (默认 1 秒/次) */
    val scopeQueryRateLimitMillis: Long = 1000L,

    /** 业务解码与分发工作线程池大小 (默认 4) */
    val workerThreadPoolSize: Int = 4,

    /** 权威房间周期推进 tick 间隔 (默认 50ms) */
    val tickIntervalMillis: Long = 50L,

    /** 注入的时钟源 */
    val clock: TransportClock = TransportClock.SYSTEM,

    /** 注入的定时调度器（可空） */
    val scheduler: TransportScheduler? = null,

    /** 房间留存与回收策略（拟定默认值，可通过服务端配置调整） */
    val retentionPolicy: RoomRetentionPolicy = RoomRetentionPolicy(),

    /** 房间与观战容量策略（拟定默认值，可通过服务端配置调整） */
    val capacityPolicy: RoomCapacityPolicy = RoomCapacityPolicy(),

    /** 共享派发线程池固定线程数（拟定默认值，可通过服务端配置调整） */
    val dispatchPoolThreads: Int = 8,

    /** 共享派发线程池有界阻塞队列容量（拟定默认值，可通过服务端配置调整） */
    val dispatchPoolQueueCapacity: Int = 1024,

    /** 共享 Future 完成线程池固定线程数（拟定默认值，可通过服务端配置调整） */
    val completionPoolThreads: Int = 4,

    /** 共享 Future 完成线程池有界阻塞队列容量（拟定默认值，可通过服务端配置调整） */
    val completionPoolQueueCapacity: Int = 1024,

    /** 每席位串行派发通道队列条目容量（拟定默认值，可通过服务端配置调整） */
    val seatChannelCapacityItems: Int = 64,

    /** 每席位串行派发通道队列估算字节容量（拟定默认值，可通过服务端配置调整） */
    val seatChannelCapacityBytes: Long = 1024L * 1024L,

    /** 公开流串行派发通道队列条目容量（拟定默认值，可通过服务端配置调整） */
    val publicChannelCapacityItems: Int = 64,

    /** 公开流串行派发通道队列估算字节容量（拟定默认值，可通过服务端配置调整） */
    val publicChannelCapacityBytes: Long = 1024L * 1024L,

    /** 串行通道单轮排空最大条目数（拟定默认值，可通过服务端配置调整） */
    val channelDrainBatchSize: Int = 16,

    /** 共享线程池组关闭最大等待截止时间（毫秒，拟定默认值，可通过服务端配置调整） */
    val sharedPoolsCloseDeadlineMillis: Long = 1000L
) {
    /** 房间存活巡检周期（毫秒，拟定默认值，可通过服务端配置调整） */
    val patrolIntervalMillis: Long get() = retentionPolicy.patrolIntervalMillis

    init {
        require(globalBudgetBytes > 0L) { "globalBudgetBytes must be > 0" }
        require(maxConnections > 0) { "maxConnections must be > 0" }
        require(maxUnhandshakedConnections > 0) { "maxUnhandshakedConnections must be > 0" }
        require(handshakeTimeoutMillis > 0L) { "handshakeTimeoutMillis must be > 0" }
        require(idleDeadMillis > 0L) { "idleDeadMillis must be > 0" }
        require(maxOutboundQueueFrames > 0) { "maxOutboundQueueFrames must be > 0" }
        require(maxOutboundQueueBytes > 0L) { "maxOutboundQueueBytes must be > 0" }
        require(maxMailboxFrames > 0) { "maxMailboxFrames must be > 0" }
        require(maxMailboxBytes > 0L) { "maxMailboxBytes must be > 0" }
        require(workerThreadPoolSize > 0) { "workerThreadPoolSize must be > 0" }
        require(dispatchPoolThreads > 0) { "dispatchPoolThreads must be > 0" }
        require(dispatchPoolQueueCapacity > 0) { "dispatchPoolQueueCapacity must be > 0" }
        require(completionPoolThreads > 0) { "completionPoolThreads must be > 0" }
        require(completionPoolQueueCapacity > 0) { "completionPoolQueueCapacity must be > 0" }
        require(seatChannelCapacityItems > 0) { "seatChannelCapacityItems must be > 0" }
        require(seatChannelCapacityBytes > 0L) { "seatChannelCapacityBytes must be > 0" }
        require(publicChannelCapacityItems > 0) { "publicChannelCapacityItems must be > 0" }
        require(publicChannelCapacityBytes > 0L) { "publicChannelCapacityBytes must be > 0" }
        require(channelDrainBatchSize > 0) { "channelDrainBatchSize must be > 0" }
        require(sharedPoolsCloseDeadlineMillis > 0L) { "sharedPoolsCloseDeadlineMillis must be > 0" }
    }
}

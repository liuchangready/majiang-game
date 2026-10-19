package com.majiang.server.room

import com.majiang.transport.api.ProtocolRoomLifecycle

/**
 * 根据房间生命周期与有效连接历史选取适用的空闲回收 TTL（毫秒）。
 * 纯函数，支持直接单测。
 */
fun ttlFor(
    lifecycle: ProtocolRoomLifecycle,
    everConnectedHuman: Boolean,
    creatorActive: Boolean,
    policy: RoomRetentionPolicy
): Long {
    return when (lifecycle) {
        ProtocolRoomLifecycle.LOBBY -> {
            if (!everConnectedHuman && !creatorActive) {
                policy.unengagedRoomTtlMillis
            } else {
                policy.lobbyIdleTtlMillis
            }
        }
        ProtocolRoomLifecycle.PREPARING,
        ProtocolRoomLifecycle.PLAYING,
        ProtocolRoomLifecycle.SETTLED -> policy.inProgressIdleTtlMillis
        ProtocolRoomLifecycle.FINISHED -> policy.finishedIdleTtlMillis
        ProtocolRoomLifecycle.CLOSED -> 0L
    }
}

/**
 * 权威房间留存与回收策略配置 (RoomRetentionPolicy)
 *
 * 遵循 MR-06 规范，所有时长参数均为单调时长（毫秒）：
 * 所有新增默认值均为"拟定默认值，可通过服务端配置调整"。
 */
data class RoomRetentionPolicy(
    /**
     * 全员人类连接断开后的大厅空闲回收 TTL（毫秒）。
     * 拟定默认值：5 分钟 (300,000ms)，可通过服务端配置调整。
     */
    val lobbyIdleTtlMillis: Long = 5 * 60 * 1000L,

    /**
     * 对局进行中（PREPARING / PLAYING / SETTLED）无人连接空闲回收 TTL（毫秒）。
     * 拟定默认值：30 分钟 (1,800,000ms)，可通过服务端配置调整。
     */
    val inProgressIdleTtlMillis: Long = 30 * 60 * 1000L,

    /**
     * 终局结算（FINISHED）无人连接空闲回收 TTL（毫秒）。
     * 拟定默认值：10 分钟 (600,000ms)，可通过服务端配置调整。
     */
    val finishedIdleTtlMillis: Long = 10 * 60 * 1000L,

    /**
     * "未形成有效参与"（从未有真人/观战连接，且当前空闲）的短期限回收 TTL（毫秒）。
     * 拟定默认值：60 秒 (60,000ms)，可通过服务端配置调整。
     */
    val unengagedRoomTtlMillis: Long = 60 * 1000L,

    /**
     * 房间存活巡检周期（毫秒）。
     * 拟定默认值：5 秒 (5,000ms)，可通过服务端配置调整。
     */
    val patrolIntervalMillis: Long = 5 * 1000L
) {
    init {
        require(lobbyIdleTtlMillis > 0L) { "lobbyIdleTtlMillis must be > 0" }
        require(inProgressIdleTtlMillis > 0L) { "inProgressIdleTtlMillis must be > 0" }
        require(finishedIdleTtlMillis > 0L) { "finishedIdleTtlMillis must be > 0" }
        require(unengagedRoomTtlMillis > 0L) { "unengagedRoomTtlMillis must be > 0" }
        require(patrolIntervalMillis > 0L) { "patrolIntervalMillis must be > 0" }
    }
}

/**
 * 权威房间与观战容量配额策略配置 (RoomCapacityPolicy)
 *
 * 遵循 MR-06 规范，治理观战人数上限、单 IP/身份未开局房间配额与全局建房速率：
 * 所有新增默认值均为"拟定默认值，可通过服务端配置调整"。
 */
data class RoomCapacityPolicy(
    /**
     * 单房间允许的最大观战人数上限（占位口径，含断线保留恢复身份成员）。
     * 拟定默认值：32，可通过服务端配置调整。
     */
    val maxSpectators: Int = 32,

    /**
     * 单源 IP 允许名下未开局（LOBBY 态）房间的最大配额。
     * 拟定默认值：3，可通过服务端配置调整。
     */
    val maxUnstartedRoomsPerIp: Int = 3,

    /**
     * 单大厅逻辑身份 (lobby:token) 允许名下未开局（LOBBY 态）房间的最大配额。
     * 拟定默认值：3，可通过服务端配置调整。
     */
    val maxUnstartedRoomsPerPrincipal: Int = 3,

    /**
     * 全局每秒建房速率预算（滑动窗口上限）。
     * 拟定默认值：10 次/秒，可通过服务端配置调整。
     */
    val globalCreateRoomRatePerSecond: Int = 10
) {
    init {
        require(maxSpectators > 0) { "maxSpectators must be > 0" }
        require(maxUnstartedRoomsPerIp > 0) { "maxUnstartedRoomsPerIp must be > 0" }
        require(maxUnstartedRoomsPerPrincipal > 0) { "maxUnstartedRoomsPerPrincipal must be > 0" }
        require(globalCreateRoomRatePerSecond > 0) { "globalCreateRoomRatePerSecond must be > 0" }
    }
}

package com.majiang.transport.api

import java.util.Collections

/**
 * 传输层独立协议房间生命周期枚举 (ProtocolRoomLifecycle)
 */
enum class ProtocolRoomLifecycle {
    LOBBY,
    PREPARING,
    PLAYING,
    SETTLED,
    FINISHED,
    CLOSED
}

/**
 * 传输层独立协议房间成员信息 (ProtocolRoomMember)
 */
data class ProtocolRoomMember(
    val seat: Int,
    val playerName: String,
    val isHost: Boolean,
    val isReady: Boolean,
    val role: String
) {
    init {
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(playerName.isNotBlank()) { "playerName cannot be blank" }
        require(role in ProtocolSeatControl.VALID_ROLES) { "role must be one of ${ProtocolSeatControl.VALID_ROLES}: $role" }
    }
}

/**
 * 传输层独立协议思考与时钟配置 (ProtocolTimeControlConfig)
 */
data class ProtocolTimeControlConfig(
    val roundReserveSeconds: Long = 20L,
    val turnThinkingSeconds: Long = 3L
) {
    init {
        require(roundReserveSeconds in 0L..(Long.MAX_VALUE / 1000L)) {
            "roundReserveSeconds out of valid range (0..${Long.MAX_VALUE / 1000L}): $roundReserveSeconds"
        }
        require(turnThinkingSeconds in 0L..(Long.MAX_VALUE / 1000L)) {
            "turnThinkingSeconds out of valid range (0..${Long.MAX_VALUE / 1000L}): $turnThinkingSeconds"
        }
    }
}

/**
 * 传输层独立协议房间配置 (ProtocolRoomConfig)
 */
data class ProtocolRoomConfig(
    val matchConfig: ProtocolMatchConfig = ProtocolMatchConfig(),
    val timeControl: ProtocolTimeControlConfig = ProtocolTimeControlConfig()
)

/**
 * 传输层独立协议完整房间信息 (ProtocolRoomInfo)
 */
class ProtocolRoomInfo(
    val roomId: String,
    val lifecycle: ProtocolRoomLifecycle,
    val version: Long = 1L,
    val hostSeat: Int = 0,
    members: List<ProtocolRoomMember> = emptyList(),
    val config: ProtocolRoomConfig = ProtocolRoomConfig()
) {
    val members: List<ProtocolRoomMember> = Collections.unmodifiableList(ArrayList(members))

    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(version >= 0L) { "version must be >= 0: $version" }
        require(hostSeat in 0..3) { "hostSeat must be in 0..3: $hostSeat" }
    }

    operator fun component1(): String = roomId
    operator fun component2(): ProtocolRoomLifecycle = lifecycle
    operator fun component3(): Long = version
    operator fun component4(): Int = hostSeat
    operator fun component5(): List<ProtocolRoomMember> = members
    operator fun component6(): ProtocolRoomConfig = config

    fun copy(
        roomId: String = this.roomId,
        lifecycle: ProtocolRoomLifecycle = this.lifecycle,
        version: Long = this.version,
        hostSeat: Int = this.hostSeat,
        members: List<ProtocolRoomMember> = this.members,
        config: ProtocolRoomConfig = this.config
    ): ProtocolRoomInfo = ProtocolRoomInfo(roomId, lifecycle, version, hostSeat, members, config)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolRoomInfo) return false
        return roomId == other.roomId && lifecycle == other.lifecycle &&
            version == other.version && hostSeat == other.hostSeat &&
            members == other.members && config == other.config
    }

    override fun hashCode(): Int {
        var r = roomId.hashCode()
        r = 31 * r + lifecycle.hashCode()
        r = 31 * r + version.hashCode()
        r = 31 * r + hostSeat.hashCode()
        r = 31 * r + members.hashCode()
        r = 31 * r + config.hashCode()
        return r
    }

    override fun toString(): String =
        "ProtocolRoomInfo(roomId=$roomId, lifecycle=$lifecycle, version=$version, hostSeat=$hostSeat, members=$members, config=$config)"
}

package com.majiang.cheat.mark

import java.util.ArrayList
import java.util.Collections

/**
 * 落汗千术配置
 *
 * @property enabled 是否启用落汗功能（默认关闭）
 */
data class TileMarkConfig(
    val enabled: Boolean = false
)

/**
 * 提交落汗千术请求
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 * @property tileIds 选择标记的实体牌标识列表（防御性复制且只读）
 */
class TileMarkRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    tileIds: List<Int>
) {
    val tileIds: List<Int> = Collections.unmodifiableList(ArrayList(tileIds))

    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): String = decisionId
    operator fun component5(): Long = controlEpoch
    operator fun component6(): List<Int> = tileIds

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        tileIds: List<Int> = this.tileIds
    ): TileMarkRequest = TileMarkRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        tileIds = tileIds
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TileMarkRequest) return false
        return requestId == other.requestId &&
                sessionId == other.sessionId &&
                roundId == other.roundId &&
                decisionId == other.decisionId &&
                controlEpoch == other.controlEpoch &&
                tileIds == other.tileIds
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + sessionId.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + tileIds.hashCode()
        return result
    }

    override fun toString(): String {
        return "TileMarkRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, tileIds=$tileIds)"
    }
}

/**
 * 已生效的落汗标记组
 *
 * @property groupId 标记组唯一标识
 * @property ownerSeat 所属玩家席位 (0..3)
 * @property tileIds 标记的4张实体牌标识 (0..135, 防御性复制且只读)
 * @property createdRoundId 创建局标识
 * @property createdRoundOrdinal 创建时的已发牌局次计数
 * @property expiresAfterRoundOrdinal 标记到期的局次计数 (固定为 createdRoundOrdinal + 3)
 */
class TileMarkGroup @JvmOverloads constructor(
    val groupId: String,
    val ownerSeat: Int,
    tileIds: List<Int>,
    val createdRoundId: String,
    val createdRoundOrdinal: Int,
    val expiresAfterRoundOrdinal: Int = createdRoundOrdinal + 3
) {
    val tileIds: List<Int> = Collections.unmodifiableList(ArrayList(tileIds))

    init {
        require(groupId.isNotBlank()) { "groupId cannot be blank" }
        require(ownerSeat in 0..3) { "ownerSeat must be in 0..3: $ownerSeat" }
        require(createdRoundId.isNotBlank()) { "createdRoundId cannot be blank" }
        require(createdRoundOrdinal in 1..(Int.MAX_VALUE - 3)) {
            "createdRoundOrdinal must be in 1..${Int.MAX_VALUE - 3}, got: $createdRoundOrdinal"
        }
        require(this.tileIds.size == 4) { "tileIds size must be exactly 4, got: ${this.tileIds.size}" }
        require(this.tileIds.distinct().size == 4) { "tileIds must contain 4 distinct elements: ${this.tileIds}" }
        require(this.tileIds.all { it in 0..135 }) { "tileIds must all be in 0..135: ${this.tileIds}" }
        require(expiresAfterRoundOrdinal == createdRoundOrdinal + 3) {
            "expiresAfterRoundOrdinal ($expiresAfterRoundOrdinal) must equal createdRoundOrdinal ($createdRoundOrdinal) + 3"
        }
    }

    operator fun component1(): String = groupId
    operator fun component2(): Int = ownerSeat
    operator fun component3(): List<Int> = tileIds
    operator fun component4(): String = createdRoundId
    operator fun component5(): Int = createdRoundOrdinal
    operator fun component6(): Int = expiresAfterRoundOrdinal

    fun copy(
        groupId: String = this.groupId,
        ownerSeat: Int = this.ownerSeat,
        tileIds: List<Int> = this.tileIds,
        createdRoundId: String = this.createdRoundId,
        createdRoundOrdinal: Int = this.createdRoundOrdinal,
        expiresAfterRoundOrdinal: Int = this.expiresAfterRoundOrdinal
    ): TileMarkGroup = TileMarkGroup(
        groupId = groupId,
        ownerSeat = ownerSeat,
        tileIds = tileIds,
        createdRoundId = createdRoundId,
        createdRoundOrdinal = createdRoundOrdinal,
        expiresAfterRoundOrdinal = expiresAfterRoundOrdinal
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TileMarkGroup) return false
        return groupId == other.groupId &&
                ownerSeat == other.ownerSeat &&
                tileIds == other.tileIds &&
                createdRoundId == other.createdRoundId &&
                createdRoundOrdinal == other.createdRoundOrdinal &&
                expiresAfterRoundOrdinal == other.expiresAfterRoundOrdinal
    }

    override fun hashCode(): Int {
        var result = groupId.hashCode()
        result = 31 * result + ownerSeat.hashCode()
        result = 31 * result + tileIds.hashCode()
        result = 31 * result + createdRoundId.hashCode()
        result = 31 * result + createdRoundOrdinal.hashCode()
        result = 31 * result + expiresAfterRoundOrdinal.hashCode()
        return result
    }

    override fun toString(): String {
        return "TileMarkGroup(groupId=$groupId, ownerSeat=$ownerSeat, tileIds=$tileIds, createdRoundId=$createdRoundId, createdRoundOrdinal=$createdRoundOrdinal, expiresAfterRoundOrdinal=$expiresAfterRoundOrdinal)"
    }
}

/**
 * 标记牌在牌河中的精准位置
 *
 * @property roundId 局标识
 * @property seat 牌河所属席位 (0..3)
 * @property discardIndex 在该席位当前牌河列表中的索引 (>= 0)
 */
data class RiverPosition(
    val roundId: String,
    val seat: Int,
    val discardIndex: Int
) {
    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(seat in 0..3) { "seat must be in 0..3, got: $seat" }
        require(discardIndex >= 0) { "discardIndex must be non-negative: $discardIndex" }
    }
}

package com.majiang.client.api

import java.util.ArrayList
import java.util.Collections

/**
 * 客户端独立落汗千术配置模型 (TileMarkConfig)
 *
 * @property enabled 是否启用落汗功能（默认关闭）
 */
data class TileMarkConfig(
    val enabled: Boolean = false
)

/**
 * 客户端独立落汗千术请求模型 (TileMarkRequest)
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
        require(this.tileIds.all { it in 0..135 }) { "tileIds must all be in 0..135: ${this.tileIds}" }
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

    fun deepCopy(): TileMarkRequest = copy(tileIds = ArrayList(tileIds))

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

    override fun toString(): String =
        "TileMarkRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, tileIds=$tileIds)"
}

/**
 * 客户端独立标记牌在牌河中的精准位置模型 (RiverPositionView)
 *
 * @property roundId 局标识
 * @property seat 牌河所属席位 (0..3)
 * @property discardIndex 在该席位当前牌河列表中的索引 (>= 0)
 */
data class RiverPositionView(
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

/**
 * 客户端独立标记牌当前所处区域枚举 (TileMarkArea)
 */
enum class TileMarkArea {
    OWN_HAND,
    OPPONENT_HAND,
    DISCARD,
    EXPOSED_MELD,
    EXPOSED_INDICATOR,
    NOT_VISIBLE
}

/**
 * 客户端独立单张标记牌私有视图模型 (MarkedTileView)
 *
 * @property tileId 实体牌全局标识 (0..135)
 * @property tile 强类型牌面值对象
 * @property area 观察者可见的受限区域
 * @property seat 所在座位 (0..3)；仅当区域为 OWN_HAND, OPPONENT_HAND, DISCARD, EXPOSED_MELD 时非空
 * @property riverPosition 牌河精准位置；仅当区域为 DISCARD 时非空
 */
data class MarkedTileView @JvmOverloads constructor(
    val tileId: Int,
    val tile: TileView,
    val area: TileMarkArea,
    val seat: Int? = null,
    val riverPosition: RiverPositionView? = null
) {
    init {
        require(tileId in 0..135) { "tileId must be in 0..135, got: $tileId" }
        require(tile.tileId == tileId) { "tile.tileId (${tile.tileId}) must match tileId ($tileId)" }
        if (area == TileMarkArea.DISCARD) {
            require(seat != null && seat in 0..3) { "seat must be non-null and in 0..3 for DISCARD, got: $seat" }
            require(riverPosition != null) { "riverPosition must be non-null for DISCARD" }
            require(riverPosition.seat == seat) { "riverPosition.seat (${riverPosition.seat}) must match seat ($seat)" }
        } else {
            require(riverPosition == null) { "riverPosition must be null when area is not DISCARD, was: $riverPosition" }
        }
        if (area == TileMarkArea.OWN_HAND || area == TileMarkArea.OPPONENT_HAND || area == TileMarkArea.EXPOSED_MELD) {
            require(seat != null && seat in 0..3) { "seat must be non-null and in 0..3 for $area, got: $seat" }
        } else if (area == TileMarkArea.EXPOSED_INDICATOR || area == TileMarkArea.NOT_VISIBLE) {
            require(seat == null) { "seat must be null for $area, got: $seat" }
        }
    }
}

/**
 * 客户端独立单组落汗标记观察者视图模型 (TileMarkGroupView)
 *
 * @property groupId 标记组标识
 * @property createdRoundId 创建局标识
 * @property createdRoundOrdinal 创建局已发牌序号
 * @property expiresAfterRoundOrdinal 最后一局有效局序号
 * @property futureRoundsRemaining 当前局之后还可覆盖的局数
 * @property isLastEffectiveRound 当前局是否为该组最后一局有效局
 * @property tiles 4张标记牌的可见情况（不可变且只读）
 */
class TileMarkGroupView @JvmOverloads constructor(
    val groupId: String,
    val createdRoundId: String,
    val createdRoundOrdinal: Int,
    val expiresAfterRoundOrdinal: Int,
    val futureRoundsRemaining: Int,
    val isLastEffectiveRound: Boolean,
    tiles: List<MarkedTileView>
) {
    val tiles: List<MarkedTileView> = Collections.unmodifiableList(ArrayList(tiles))

    init {
        require(groupId.isNotBlank()) { "groupId cannot be blank" }
        require(createdRoundId.isNotBlank()) { "createdRoundId cannot be blank" }
        require(createdRoundOrdinal in 1..(Int.MAX_VALUE - 3)) {
            "createdRoundOrdinal must be in 1..${Int.MAX_VALUE - 3}, got: $createdRoundOrdinal"
        }
        require(expiresAfterRoundOrdinal.toLong() == createdRoundOrdinal.toLong() + 3L) {
            "expiresAfterRoundOrdinal ($expiresAfterRoundOrdinal) must equal createdRoundOrdinal ($createdRoundOrdinal) + 3"
        }
        require(futureRoundsRemaining in 0..3) { "futureRoundsRemaining must be in 0..3, got: $futureRoundsRemaining" }
        require(isLastEffectiveRound == (futureRoundsRemaining == 0)) {
            "isLastEffectiveRound ($isLastEffectiveRound) must be true if and only if futureRoundsRemaining == 0 ($futureRoundsRemaining)"
        }
        require(this.tiles.size == 4) { "tiles size must be exactly 4, got: ${this.tiles.size}" }
        require(this.tiles.map { it.tileId }.distinct().size == 4) {
            "tiles must have 4 distinct tileIds: ${this.tiles.map { it.tileId }}"
        }
    }

    operator fun component1(): String = groupId
    operator fun component2(): String = createdRoundId
    operator fun component3(): Int = createdRoundOrdinal
    operator fun component4(): Int = expiresAfterRoundOrdinal
    operator fun component5(): Int = futureRoundsRemaining
    operator fun component6(): Boolean = isLastEffectiveRound
    operator fun component7(): List<MarkedTileView> = tiles

    fun copy(
        groupId: String = this.groupId,
        createdRoundId: String = this.createdRoundId,
        createdRoundOrdinal: Int = this.createdRoundOrdinal,
        expiresAfterRoundOrdinal: Int = this.expiresAfterRoundOrdinal,
        futureRoundsRemaining: Int = this.futureRoundsRemaining,
        isLastEffectiveRound: Boolean = this.isLastEffectiveRound,
        tiles: List<MarkedTileView> = this.tiles
    ): TileMarkGroupView = TileMarkGroupView(
        groupId = groupId,
        createdRoundId = createdRoundId,
        createdRoundOrdinal = createdRoundOrdinal,
        expiresAfterRoundOrdinal = expiresAfterRoundOrdinal,
        futureRoundsRemaining = futureRoundsRemaining,
        isLastEffectiveRound = isLastEffectiveRound,
        tiles = tiles
    )

    fun deepCopy(): TileMarkGroupView = copy(tiles = tiles.map { it.copy() })

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TileMarkGroupView) return false
        return groupId == other.groupId &&
            createdRoundId == other.createdRoundId &&
            createdRoundOrdinal == other.createdRoundOrdinal &&
            expiresAfterRoundOrdinal == other.expiresAfterRoundOrdinal &&
            futureRoundsRemaining == other.futureRoundsRemaining &&
            isLastEffectiveRound == other.isLastEffectiveRound &&
            tiles == other.tiles
    }

    override fun hashCode(): Int {
        var result = groupId.hashCode()
        result = 31 * result + createdRoundId.hashCode()
        result = 31 * result + createdRoundOrdinal.hashCode()
        result = 31 * result + expiresAfterRoundOrdinal.hashCode()
        result = 31 * result + futureRoundsRemaining.hashCode()
        result = 31 * result + isLastEffectiveRound.hashCode()
        result = 31 * result + tiles.hashCode()
        return result
    }

    override fun toString(): String =
        "TileMarkGroupView(groupId=$groupId, createdRoundId=$createdRoundId, createdRoundOrdinal=$createdRoundOrdinal, expiresAfterRoundOrdinal=$expiresAfterRoundOrdinal, futureRoundsRemaining=$futureRoundsRemaining, isLastEffectiveRound=$isLastEffectiveRound, tiles=$tiles)"
}

/**
 * 客户端独立观察者私有落汗千术完整视图模型 (TileMarkView)
 *
 * @property canApply 当前是否允许施术
 * @property disabledReason 禁用原因；允许时为 null
 * @property cost 施术费用（固定 10）
 * @property activeTileCount 当前有效标记总数 (0..12)
 * @property maxActiveTiles 最大有效标记总数（固定 12）
 * @property selectableTileIds 当前可选的自身手牌实体列表（防御性复制且只读）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 状态版本
 * @property activeGroups 当前生效的标记组列表（0..3组，防御性复制且只读）
 */
class TileMarkView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val activeTileCount: Int,
    val maxActiveTiles: Int = 12,
    selectableTileIds: List<Int> = emptyList(),
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    activeGroups: List<TileMarkGroupView> = emptyList()
) {
    val selectableTileIds: List<Int> = Collections.unmodifiableList(ArrayList(selectableTileIds))
    val activeGroups: List<TileMarkGroupView> = Collections.unmodifiableList(ArrayList(activeGroups))

    init {
        require(cost == 10) { "cost must be 10, got: $cost" }
        require(maxActiveTiles == 12) { "maxActiveTiles must be 12, got: $maxActiveTiles" }
        require(activeTileCount in 0..12) { "activeTileCount must be in 0..12, got: $activeTileCount" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(stateVersion >= 0L) { "stateVersion must be non-negative: $stateVersion" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(this.selectableTileIds.all { it in 0..135 }) { "selectableTileIds must all be in 0..135: ${this.selectableTileIds}" }
        require(this.activeGroups.size in 0..3) { "activeGroups size must be in 0..3, got: ${this.activeGroups.size}" }
        val totalTiles = this.activeGroups.sumOf { it.tiles.size }
        require(totalTiles == activeTileCount) {
            "activeTileCount ($activeTileCount) must equal total marked tiles across activeGroups ($totalTiles)"
        }
        val allTileIds = this.activeGroups.flatMap { g -> g.tiles.map { it.tileId } }
        require(allTileIds.distinct().size == allTileIds.size) {
            "duplicate tileId across activeGroups: $allTileIds"
        }
        val allGroupIds = this.activeGroups.map { it.groupId }
        require(allGroupIds.distinct().size == allGroupIds.size) {
            "duplicate groupId across activeGroups: $allGroupIds"
        }
    }

    operator fun component1(): Boolean = canApply
    operator fun component2(): String? = disabledReason
    operator fun component3(): Int = cost
    operator fun component4(): Int = activeTileCount
    operator fun component5(): Int = maxActiveTiles
    operator fun component6(): List<Int> = selectableTileIds
    operator fun component7(): String = decisionId
    operator fun component8(): Long = controlEpoch
    operator fun component9(): Long = stateVersion
    operator fun component10(): List<TileMarkGroupView> = activeGroups

    fun copy(
        canApply: Boolean = this.canApply,
        disabledReason: String? = this.disabledReason,
        cost: Int = this.cost,
        activeTileCount: Int = this.activeTileCount,
        maxActiveTiles: Int = this.maxActiveTiles,
        selectableTileIds: List<Int> = this.selectableTileIds,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        stateVersion: Long = this.stateVersion,
        activeGroups: List<TileMarkGroupView> = this.activeGroups
    ): TileMarkView = TileMarkView(
        canApply = canApply,
        disabledReason = disabledReason,
        cost = cost,
        activeTileCount = activeTileCount,
        maxActiveTiles = maxActiveTiles,
        selectableTileIds = selectableTileIds,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        stateVersion = stateVersion,
        activeGroups = activeGroups
    )

    fun deepCopy(): TileMarkView = copy(
        selectableTileIds = ArrayList(selectableTileIds),
        activeGroups = activeGroups.map { it.deepCopy() }
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TileMarkView) return false
        return canApply == other.canApply &&
            disabledReason == other.disabledReason &&
            cost == other.cost &&
            activeTileCount == other.activeTileCount &&
            maxActiveTiles == other.maxActiveTiles &&
            selectableTileIds == other.selectableTileIds &&
            decisionId == other.decisionId &&
            controlEpoch == other.controlEpoch &&
            stateVersion == other.stateVersion &&
            activeGroups == other.activeGroups
    }

    override fun hashCode(): Int {
        var result = canApply.hashCode()
        result = 31 * result + (disabledReason?.hashCode() ?: 0)
        result = 31 * result + cost.hashCode()
        result = 31 * result + activeTileCount.hashCode()
        result = 31 * result + maxActiveTiles.hashCode()
        result = 31 * result + selectableTileIds.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + stateVersion.hashCode()
        result = 31 * result + activeGroups.hashCode()
        return result
    }

    override fun toString(): String =
        "TileMarkView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, activeTileCount=$activeTileCount, maxActiveTiles=$maxActiveTiles, selectableTileIds=$selectableTileIds, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, activeGroups=$activeGroups)"
}

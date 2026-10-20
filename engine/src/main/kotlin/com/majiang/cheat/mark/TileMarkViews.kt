package com.majiang.cheat.mark

import com.majiang.model.Tile
import java.util.ArrayList
import java.util.Collections

/**
 * 标记牌当前所处区域投影
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
 * 单张标记牌对观察者的私有视图
 *
 * @property tileId 实体牌全局标识 (0..135)
 * @property tile 强类型牌面值对象
 * @property area 观察者可见的受限区域
 * @property seat 所在座位 (0..3)；仅当区域为 OWN_HAND, OPPONENT_HAND, DISCARD, EXPOSED_MELD 时非空
 * @property riverPosition 牌河精准位置；仅当区域为 DISCARD 时非空
 */
data class MarkedTileView @JvmOverloads constructor(
    val tileId: Int,
    val tile: Tile,
    val area: TileMarkArea,
    val seat: Int? = null,
    val riverPosition: RiverPosition? = null
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
 * 单组落汗标记的观察者视图
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
        require(createdRoundOrdinal >= 1) { "createdRoundOrdinal must be >= 1, got: $createdRoundOrdinal" }
        require(expiresAfterRoundOrdinal == createdRoundOrdinal + 3) {
            "expiresAfterRoundOrdinal ($expiresAfterRoundOrdinal) must equal createdRoundOrdinal ($createdRoundOrdinal) + 3"
        }
        require(futureRoundsRemaining >= 0) { "futureRoundsRemaining must be non-negative, got: $futureRoundsRemaining" }
        require(this.tiles.size == 4) { "tiles size must be exactly 4, got: ${this.tiles.size}" }
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

    override fun toString(): String {
        return "TileMarkGroupView(groupId=$groupId, createdRoundId=$createdRoundId, createdRoundOrdinal=$createdRoundOrdinal, expiresAfterRoundOrdinal=$expiresAfterRoundOrdinal, futureRoundsRemaining=$futureRoundsRemaining, isLastEffectiveRound=$isLastEffectiveRound, tiles=$tiles)"
    }
}

/**
 * 观察者私有落汗千术完整视图
 *
 * @property canApply 当前是否允许施术
 * @property disabledReason 禁用原因；允许时为 null
 * @property cost 施术费用（由 cheatEconomy.costs["TILE_MARK"] 配置，默认 10，必须为正）
 * @property activeTileCount 当前有效标记总数 (0..12)
 * @property maxActiveTiles 最大有效标记总数（固定 12）
 * @property selectableTileIds 当前可选的自身手牌实体列表（排除已标记，仅在可施术时提供）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 状态版本
 * @property activeGroups 当前生效的标记组列表（0..3组）
 */
class TileMarkView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val activeTileCount: Int,
    val maxActiveTiles: Int = 12,
    selectableTileIds: List<Int>,
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    activeGroups: List<TileMarkGroupView>
) {
    val selectableTileIds: List<Int> = Collections.unmodifiableList(ArrayList(selectableTileIds))
    val activeGroups: List<TileMarkGroupView> = Collections.unmodifiableList(ArrayList(activeGroups))

    init {
        require(cost > 0) { "cost must be positive, got: $cost" }
        require(maxActiveTiles == 12) { "maxActiveTiles must be 12, got: $maxActiveTiles" }
        require(activeTileCount in 0..12) { "activeTileCount must be in 0..12, got: $activeTileCount" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(stateVersion >= 0L) { "stateVersion must be non-negative: $stateVersion" }
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

    override fun toString(): String {
        return "TileMarkView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, activeTileCount=$activeTileCount, maxActiveTiles=$maxActiveTiles, selectableTileIds=$selectableTileIds, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, activeGroups=$activeGroups)"
    }
}

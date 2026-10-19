package com.majiang.presentation.common.render

import com.majiang.client.api.ClockInfo
import com.majiang.client.api.MeldView
import com.majiang.client.api.TileView
import com.majiang.presentation.game.OpponentHandRow
import java.util.ArrayList
import java.util.Collections
import java.util.HashSet

/**
 * 弃牌河落汗标记键值 (RiverMarkedTile)。
 *
 * @property seat 席位 (0..3)
 * @property discardIndex 弃牌槽索引 (0..)
 * @property tileId 牌全局唯一物理编号
 */
data class RiverMarkedTile(
    val seat: Int,
    val discardIndex: Int,
    val tileId: Int
)

/**
 * 牌桌公开桌面渲染模型 (TableBoardRenderModel)。
 *
 * 核心安全契约与设计规范：
 * 1. 纯公开数据快照，全部字段不可变并做防御性复制；
 * 2. 严禁引用或持有任何会话、私有视图或快照对象；
 * 3. 绝不包含除公开数据以外的玩家暗手牌、操作候选或千术草稿数据；
 * 4. 玩家模式与观战模式共用此统一数据结构。
 */
class TableBoardRenderModel(
    val viewerSeat: Int,
    val isSpectator: Boolean,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val wallRemaining: Int,
    doraIndicators: List<TileView>,
    markedDoraIndicatorIds: Set<Int>,
    scores: List<Int>,
    riichiStatus: List<Boolean>,
    val currentTurnPlayer: Int,
    discards: List<List<TileView>>,
    riverMarkedTileKeys: Set<RiverMarkedTile>,
    melds: List<List<MeldView>>,
    markedMeldTileIds: Set<Int>,
    opponentHandRows: List<OpponentHandRow>,
    val viewerHandBackCount: Int,
    clocks: List<ClockInfo>
) {
    val doraIndicators: List<TileView> = Collections.unmodifiableList(ArrayList(doraIndicators))
    val markedDoraIndicatorIds: Set<Int> = Collections.unmodifiableSet(HashSet(markedDoraIndicatorIds))
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val riichiStatus: List<Boolean> = Collections.unmodifiableList(ArrayList(riichiStatus))
    val discards: List<List<TileView>> = Collections.unmodifiableList(
        discards.map { Collections.unmodifiableList(ArrayList(it)) }
    )
    val riverMarkedTileKeys: Set<RiverMarkedTile> = Collections.unmodifiableSet(HashSet(riverMarkedTileKeys))
    val melds: List<List<MeldView>> = Collections.unmodifiableList(
        melds.map { Collections.unmodifiableList(ArrayList(it)) }
    )
    val markedMeldTileIds: Set<Int> = Collections.unmodifiableSet(HashSet(markedMeldTileIds))
    val opponentHandRows: List<OpponentHandRow> = Collections.unmodifiableList(ArrayList(opponentHandRows))
    val clocks: List<ClockInfo> = Collections.unmodifiableList(ArrayList(clocks))

    init {
        require(viewerSeat in 0..3) { "viewerSeat must be in 0..3: $viewerSeat" }
        require(this.scores.size == 4) { "scores must have size 4: ${this.scores.size}" }
        require(this.riichiStatus.size == 4) { "riichiStatus must have size 4: ${this.riichiStatus.size}" }
        require(this.discards.size == 4) { "discards must have size 4: ${this.discards.size}" }
        require(this.melds.size == 4) { "melds must have size 4: ${this.melds.size}" }
        require(viewerHandBackCount >= 0) { "viewerHandBackCount must be >= 0: $viewerHandBackCount" }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TableBoardRenderModel) return false

        if (viewerSeat != other.viewerSeat) return false
        if (isSpectator != other.isSpectator) return false
        if (roundWind != other.roundWind) return false
        if (roundNumber != other.roundNumber) return false
        if (honbaSticks != other.honbaSticks) return false
        if (riichiSticks != other.riichiSticks) return false
        if (wallRemaining != other.wallRemaining) return false
        if (doraIndicators != other.doraIndicators) return false
        if (markedDoraIndicatorIds != other.markedDoraIndicatorIds) return false
        if (scores != other.scores) return false
        if (riichiStatus != other.riichiStatus) return false
        if (currentTurnPlayer != other.currentTurnPlayer) return false
        if (discards != other.discards) return false
        if (riverMarkedTileKeys != other.riverMarkedTileKeys) return false
        if (melds != other.melds) return false
        if (markedMeldTileIds != other.markedMeldTileIds) return false
        if (opponentHandRows != other.opponentHandRows) return false
        if (viewerHandBackCount != other.viewerHandBackCount) return false
        if (clocks != other.clocks) return false

        return true
    }

    override fun hashCode(): Int {
        var result = viewerSeat
        result = 31 * result + isSpectator.hashCode()
        result = 31 * result + roundWind
        result = 31 * result + roundNumber
        result = 31 * result + honbaSticks
        result = 31 * result + riichiSticks
        result = 31 * result + wallRemaining
        result = 31 * result + doraIndicators.hashCode()
        result = 31 * result + markedDoraIndicatorIds.hashCode()
        result = 31 * result + scores.hashCode()
        result = 31 * result + riichiStatus.hashCode()
        result = 31 * result + currentTurnPlayer
        result = 31 * result + discards.hashCode()
        result = 31 * result + riverMarkedTileKeys.hashCode()
        result = 31 * result + melds.hashCode()
        result = 31 * result + markedMeldTileIds.hashCode()
        result = 31 * result + opponentHandRows.hashCode()
        result = 31 * result + viewerHandBackCount
        result = 31 * result + clocks.hashCode()
        return result
    }

    override fun toString(): String {
        return "TableBoardRenderModel(viewerSeat=$viewerSeat, isSpectator=$isSpectator, roundWind=$roundWind, roundNumber=$roundNumber, scores=$scores, viewerHandBackCount=$viewerHandBackCount)"
    }

    companion object
}

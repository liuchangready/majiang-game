package com.majiang.presentation.common.render

import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.PublicSnapshotView
import com.majiang.client.api.TileMarkArea
import com.majiang.presentation.game.OpponentHandPresentation
import com.majiang.presentation.game.TileMarkPresentation

/**
 * 快照到公开桌面渲染模型的映射器 (TableBoardRenderMapper)。
 *
 * 核心契约：
 * 1. 负责从私有玩家快照 [GameSnapshotView] 或纯公开快照 [PublicSnapshotView] 构造不可变 [TableBoardRenderModel]；
 * 2. 严禁泄漏任何私有手牌明面（仅公开或经落汗授权的标记牌参与翻面与半透明）；
 * 3. 观战快照恒置 viewerSeat=0、isSpectator=true、落汗标记集全空、底牌背数取 handTileCounts[0]；
 * 4. 独立于 TableBoardRenderModel 与 TableBoardRenderer，防止渲染数据结构与具体快照类型产生不必要的直接耦合。
 */
object TableBoardRenderMapper {

    fun fromPlayerSnapshot(snapshot: GameSnapshotView): TableBoardRenderModel {
        val viewer = snapshot.viewerSeat
        val view = snapshot.view

        val exposedIndicators = snapshot.tileMark?.activeGroups?.flatMap { it.tiles }
            ?.filter { it.area == TileMarkArea.EXPOSED_INDICATOR } ?: emptyList()
        val indicatorIds = exposedIndicators.map { it.tileId }.toSet()

        val riverMarks = TileMarkPresentation.riverMarks(snapshot)
        val riverMarkedKeys = riverMarks.mapNotNull { mark ->
            val seat = mark.seat
            val discardIndex = mark.riverPosition?.discardIndex
            if (seat != null && discardIndex != null) {
                RiverMarkedTile(seat, discardIndex, mark.tileId)
            } else {
                null
            }
        }.toSet()

        val exposedMelds = snapshot.tileMark?.activeGroups?.flatMap { it.tiles }
            ?.filter { it.area == TileMarkArea.EXPOSED_MELD } ?: emptyList()
        val meldIds = exposedMelds.map { it.tileId }.toSet()

        val opponentRows = OpponentHandPresentation.build(snapshot)

        return TableBoardRenderModel(
            viewerSeat = viewer,
            isSpectator = false,
            roundWind = view.roundWind,
            roundNumber = view.roundNumber,
            honbaSticks = view.honbaSticks,
            riichiSticks = view.riichiSticks,
            wallRemaining = view.wallRemaining,
            doraIndicators = view.doraIndicators,
            markedDoraIndicatorIds = indicatorIds,
            scores = view.scores,
            riichiStatus = view.riichiStatus,
            currentTurnPlayer = view.currentTurnPlayer,
            discards = view.discards,
            riverMarkedTileKeys = riverMarkedKeys,
            melds = view.melds,
            markedMeldTileIds = meldIds,
            opponentHandRows = opponentRows,
            viewerHandBackCount = 0,
            clocks = snapshot.clocks
        )
    }

    fun fromPublicSnapshot(snapshot: PublicSnapshotView): TableBoardRenderModel {
        val board = requireNotNull(snapshot.board) { "PublicSnapshotView.board cannot be null" }

        val opponentRows = OpponentHandPresentation.rowsFromCounts(board.handTileCounts, board.phase)
        val viewerBackCount = board.handTileCounts.getOrElse(0) { 0 }

        return TableBoardRenderModel(
            viewerSeat = 0,
            isSpectator = true,
            roundWind = board.roundWind,
            roundNumber = board.roundNumber,
            honbaSticks = board.honbaSticks,
            riichiSticks = board.riichiSticks,
            wallRemaining = board.wallRemaining,
            doraIndicators = board.doraIndicators,
            markedDoraIndicatorIds = emptySet(),
            scores = board.scores,
            riichiStatus = board.riichiStatus,
            currentTurnPlayer = board.currentTurnPlayer,
            discards = board.discards,
            riverMarkedTileKeys = emptySet(),
            melds = board.melds,
            markedMeldTileIds = emptySet(),
            opponentHandRows = opponentRows,
            viewerHandBackCount = viewerBackCount,
            clocks = snapshot.clocks
        )
    }
}

fun TableBoardRenderModel.Companion.fromPlayerSnapshot(snapshot: GameSnapshotView): TableBoardRenderModel =
    TableBoardRenderMapper.fromPlayerSnapshot(snapshot)

fun TableBoardRenderModel.Companion.fromPublicSnapshot(snapshot: PublicSnapshotView): TableBoardRenderModel =
    TableBoardRenderMapper.fromPublicSnapshot(snapshot)

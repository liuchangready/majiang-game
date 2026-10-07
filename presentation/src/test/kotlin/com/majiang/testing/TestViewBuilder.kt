package com.majiang.testing

import com.majiang.client.api.*

/**
 * 表现层测试专用的纯客户端模型工厂 (TestViewBuilder)。
 * 严禁依赖 engine、protocol 或 server。
 */
object TestViewBuilder {

    fun tile(suit: SuitView, rank: Int, copyIndex: Int = 0): TileView {
        val typeIndex = when (suit) {
            SuitView.MAN -> rank - 1
            SuitView.SOU -> rank - 1 + 9
            SuitView.PIN -> rank - 1 + 18
            SuitView.WIND -> rank - 1 + 27
            SuitView.DRAGON -> rank - 1 + 31
        }
        return TileView.fromTypeIndex(typeIndex, copyIndex)
    }

    fun tile(id: Int): TileView = TileView.fromTileId(id)

    fun meld(
        type: MeldTypeView,
        tiles: List<TileView>,
        fromPlayer: Int = 0,
        calledTile: TileView = tiles.first()
    ): MeldView = MeldView(type, tiles, fromPlayer, calledTile)

    fun playerView(
        viewerSeat: Int = 2,
        phase: GamePhaseView = GamePhaseView.DISCARD,
        roundWind: Int = 1,
        roundNumber: Int = 1,
        honbaSticks: Int = 0,
        riichiSticks: Int = 0,
        dealerSeat: Int = 0,
        currentTurnPlayer: Int = viewerSeat,
        wallRemaining: Int = 70,
        doraIndicators: List<TileView> = listOf(tile(SuitView.PIN, 1)),
        scores: List<Int> = listOf(25000, 25000, 25000, 25000),
        riichiStatus: List<Boolean> = listOf(false, false, false, false),
        discards: List<List<TileView>> = List(4) { emptyList() },
        melds: List<List<MeldView>> = List(4) { emptyList() },
        handTileCounts: List<Int> = listOf(13, 13, 14, 13),
        myClosedTiles: List<TileView> = (1..6).map { tile(SuitView.MAN, it) } +
            (7..9).map { tile(SuitView.PIN, it) } + (2..5).map { tile(SuitView.SOU, it) },
        myDrawnTile: TileView? = tile(SuitView.SOU, 6),
        legalActions: List<LegalActionView> = emptyList(),
        lastDiscard: TileView? = null,
        lastDiscardPlayer: Int? = null,
        mySleeveTiles: List<TileView> = emptyList(),
        myFuriten: FuritenStatusView = FuritenStatusView(),
        myDiscardHistory: List<TileView> = emptyList()
    ): PlayerView = PlayerView(
        viewerSeat = viewerSeat,
        phase = phase,
        roundWind = roundWind,
        roundNumber = roundNumber,
        honbaSticks = honbaSticks,
        riichiSticks = riichiSticks,
        dealerSeat = dealerSeat,
        currentTurnPlayer = currentTurnPlayer,
        wallRemaining = wallRemaining,
        doraIndicators = doraIndicators,
        scores = scores,
        riichiStatus = riichiStatus,
        discards = discards,
        melds = melds,
        handTileCounts = handTileCounts,
        myClosedTiles = myClosedTiles,
        myDrawnTile = myDrawnTile,
        legalActions = legalActions,
        lastDiscard = lastDiscard,
        lastDiscardPlayer = lastDiscardPlayer,
        mySleeveTiles = mySleeveTiles,
        myFuriten = myFuriten,
        myDiscardHistory = myDiscardHistory
    )

    fun publicBoardView(
        phase: GamePhaseView = GamePhaseView.DISCARD,
        roundWind: Int = 1,
        roundNumber: Int = 1,
        honbaSticks: Int = 0,
        riichiSticks: Int = 0,
        dealerSeat: Int = 0,
        currentTurnPlayer: Int = 0,
        wallRemaining: Int = 70,
        doraIndicators: List<TileView> = listOf(tile(SuitView.PIN, 1)),
        scores: List<Int> = listOf(25000, 25000, 25000, 25000),
        riichiStatus: List<Boolean> = listOf(false, false, false, false),
        discards: List<List<TileView>> = List(4) { emptyList() },
        melds: List<List<MeldView>> = List(4) { emptyList() },
        handTileCounts: List<Int> = listOf(13, 13, 13, 13),
        lastDiscard: TileView? = null,
        lastDiscardPlayer: Int? = null
    ): PublicBoardView = PublicBoardView(
        phase = phase,
        roundWind = roundWind,
        roundNumber = roundNumber,
        honbaSticks = honbaSticks,
        riichiSticks = riichiSticks,
        dealerSeat = dealerSeat,
        currentTurnPlayer = currentTurnPlayer,
        wallRemaining = wallRemaining,
        doraIndicators = doraIndicators,
        scores = scores,
        riichiStatus = riichiStatus,
        discards = discards,
        melds = melds,
        handTileCounts = handTileCounts,
        lastDiscard = lastDiscard,
        lastDiscardPlayer = lastDiscardPlayer
    )

    fun publicSnapshotView(
        sessionId: String = "fake-public-session",
        roundId: String? = "round-1-1-0",
        syncEpoch: String = "epoch-1",
        cursor: Long = 1L,
        sessionVersion: Long = 1L,
        roundVersion: Long? = 1L,
        board: PublicBoardView = publicBoardView(),
        clocks: List<ClockInfo> = emptyList(),
        session: PublicSessionSummaryView = PublicSessionSummaryView(
            lifecycle = "ROUND_ACTIVE",
            scores = board.scores,
            roundWind = board.roundWind,
            roundNumber = board.roundNumber,
            dealerSeat = board.dealerSeat,
            honba = board.honbaSticks
        ),
        paused: Boolean = false,
        settlementRoundId: String? = null,
        settlementEvents: List<GameEventView> = emptyList()
    ): PublicSnapshotView = PublicSnapshotView(
        sessionId = sessionId,
        roundId = roundId,
        syncEpoch = syncEpoch,
        cursor = cursor,
        sessionVersion = sessionVersion,
        roundVersion = roundVersion,
        board = board,
        clocks = clocks,
        session = session,
        paused = paused,
        settlementRoundId = settlementRoundId,
        settlementEvents = settlementEvents
    )
}

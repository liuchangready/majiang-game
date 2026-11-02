package com.majiang.presentation.common.render

import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.PlayerView
import com.majiang.client.api.PublicBoardView

/**
 * 持续牌桌 HUD 的最小公开字段投影 (TableHudView)。
 *
 * 玩家与观战共用同一套头像/名牌/罗盘绘制，但不把 `PlayerView` 的私有手牌、
 * 袖牌、千术字段带进观战屏。这里只保留公开风局、点数、立直、庄家、当前巡与时钟
 * 所需的座位信息。
 */
data class TableHudView(
    val viewerSeat: Int,
    val phase: GamePhaseView,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val scores: List<Int>,
    val riichiStatus: List<Boolean>
) {
    fun seatWind(seat: Int): Int = ((seat - dealerSeat + 4) % 4) + 1

    companion object {
        fun fromPlayerView(view: PlayerView): TableHudView = TableHudView(
            viewerSeat = view.viewerSeat,
            phase = view.phase,
            roundWind = view.roundWind,
            roundNumber = view.roundNumber,
            honbaSticks = view.honbaSticks,
            riichiSticks = view.riichiSticks,
            dealerSeat = view.dealerSeat,
            currentTurnPlayer = view.currentTurnPlayer,
            scores = view.scores,
            riichiStatus = view.riichiStatus
        )

        fun fromPublicBoard(board: PublicBoardView): TableHudView = TableHudView(
            viewerSeat = 0,
            phase = board.phase,
            roundWind = board.roundWind,
            roundNumber = board.roundNumber,
            honbaSticks = board.honbaSticks,
            riichiSticks = board.riichiSticks,
            dealerSeat = board.dealerSeat,
            currentTurnPlayer = board.currentTurnPlayer,
            scores = board.scores,
            riichiStatus = board.riichiStatus
        )
    }
}

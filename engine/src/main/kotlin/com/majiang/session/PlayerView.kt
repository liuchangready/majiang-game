package com.majiang.session

import com.majiang.model.Meld
import com.majiang.model.Tile
import com.majiang.rules.FuritenRules
import com.majiang.rules.FuritenStatus
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import java.util.Collections

/**
 * 玩家座位的深拷贝只读视图。
 *
 * 保证三层架构的信息隔离与安全性：
 * 1. 仅观察者自己的暗牌（closedTiles 与 drawnTile）完全可见；
 * 2. 对手手牌只提供暗牌张数（handTileCounts），绝不泄露对手暗牌具体内容；
 * 3. 牌桌公开信息（各家牌河、公开副露、Dora 指示牌、各家分数、场况元数据、阶段）完全公开；
 * 4. 绝不泄露 GameState/PlayerHand/TilePool 内部引用、牌山顺序、未公开里宝或其他座位的合法动作；
 * 5. 所有集合对外均为只读且不可篡改（深拷贝并包装为不可修改集合），防止外部意外修改或由于未来状态更新引起视图漂移。
 */
data class PlayerView(
    val viewerSeat: Int,
    val phase: GamePhase,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val wallRemaining: Int,
    val doraIndicators: List<Tile>,
    val scores: List<Int>,
    val riichiStatus: List<Boolean>,
    val discards: List<List<Tile>>,
    val melds: List<List<Meld>>,
    val handTileCounts: List<Int>,
    val myClosedTiles: List<Tile>,
    val myDrawnTile: Tile?,
    val legalActions: List<GameAction>,
    val lastDiscard: Tile? = null,
    val lastDiscardPlayer: Int? = null,
    val mySleeveTiles: List<Tile> = emptyList(),
    val myFuriten: FuritenStatus = FuritenStatus(),
    val myDiscardHistory: List<Tile> = emptyList()
) {
    init {
        require(viewerSeat in 0..3) { "viewerSeat must be 0..3, got $viewerSeat" }
        require(scores.size == 4) { "scores must have size 4" }
        require(riichiStatus.size == 4) { "riichiStatus must have size 4" }
        require(discards.size == 4) { "discards must have size 4" }
        require(melds.size == 4) { "melds must have size 4" }
        require(handTileCounts.size == 4) { "handTileCounts must have size 4" }
    }

    /** 是否轮到当前观察者行动 */
    val isMyTurn: Boolean get() = currentTurnPlayer == viewerSeat

    /** 获取指定座位的自风 (1=東, 2=南, 3=西, 4=北) */
    fun seatWind(seat: Int): Int = ((seat - dealerSeat + 4) % 4) + 1

    /** 观察者自己的门风 */
    val mySeatWind: Int get() = seatWind(viewerSeat)

    /** 观察者自己的所有暗牌（包含刚摸到的牌） */
    val myAllClosedTiles: List<Tile>
        get() = Collections.unmodifiableList(if (myDrawnTile != null) myClosedTiles + myDrawnTile else myClosedTiles)

    companion object {
        /**
         * 严格构建深拷贝且集合被包装为不可修改的 PlayerView
         */
        fun create(
            viewerSeat: Int,
            phase: GamePhase,
            roundWind: Int,
            roundNumber: Int,
            honbaSticks: Int,
            riichiSticks: Int,
            dealerSeat: Int,
            currentTurnPlayer: Int,
            wallRemaining: Int,
            doraIndicators: List<Tile>,
            scores: List<Int>,
            riichiStatus: List<Boolean>,
            discards: List<List<Tile>>,
            melds: List<List<Meld>>,
            handTileCounts: List<Int>,
            myClosedTiles: List<Tile>,
            myDrawnTile: Tile?,
            legalActions: List<GameAction>,
            lastDiscard: Tile? = null,
            lastDiscardPlayer: Int? = null,
            mySleeveTiles: List<Tile> = emptyList(),
            myFuriten: FuritenStatus = FuritenStatus(),
            myDiscardHistory: List<Tile> = emptyList()
        ): PlayerView {
            return PlayerView(
                viewerSeat = viewerSeat,
                phase = phase,
                roundWind = roundWind,
                roundNumber = roundNumber,
                honbaSticks = honbaSticks,
                riichiSticks = riichiSticks,
                dealerSeat = dealerSeat,
                currentTurnPlayer = currentTurnPlayer,
                wallRemaining = wallRemaining,
                doraIndicators = Collections.unmodifiableList(doraIndicators.toList()),
                scores = Collections.unmodifiableList(scores.toList()),
                riichiStatus = Collections.unmodifiableList(riichiStatus.toList()),
                discards = Collections.unmodifiableList(discards.map { Collections.unmodifiableList(it.toList()) }),
                melds = Collections.unmodifiableList(
                    melds.map { playerMelds ->
                        Collections.unmodifiableList(
                            playerMelds.map { meld ->
                                meld.copy(tiles = Collections.unmodifiableList(meld.tiles.toList()))
                            }
                        )
                    }
                ),
                handTileCounts = Collections.unmodifiableList(handTileCounts.toList()),
                myClosedTiles = Collections.unmodifiableList(myClosedTiles.toList()),
                myDrawnTile = myDrawnTile,
                legalActions = Collections.unmodifiableList(
                    legalActions.map { action ->
                        when (action) {
                            is GameAction.DeclareChi -> action.copy(
                                meld = action.meld.copy(tiles = Collections.unmodifiableList(action.meld.tiles.toList()))
                            )
                            is GameAction.DeclarePon -> action.copy(
                                tiles = Collections.unmodifiableList(action.tiles.toList())
                            )
                            is GameAction.DeclareKan -> action.copy(
                                tiles = Collections.unmodifiableList(action.tiles.toList())
                            )
                            is GameAction.StartStacking -> action.copy(
                                stackingPlans = Collections.unmodifiableList(action.stackingPlans.toList())
                            )
                            is GameAction.UseCheatTechnique -> action.copy(
                                params = Collections.unmodifiableMap(action.params.toMap())
                            )
                            else -> action
                        }
                    }
                ),
                lastDiscard = lastDiscard,
                lastDiscardPlayer = lastDiscardPlayer,
                mySleeveTiles = Collections.unmodifiableList(mySleeveTiles.toList()),
                myFuriten = myFuriten,
                myDiscardHistory = Collections.unmodifiableList(myDiscardHistory.toList())
            )
        }

        /**
         * 从领域 GameState 投影生成特定座位的 PlayerView。
         * 严格过滤他家暗牌、牌山顺序、未公开里 Dora 等私密状态。
         */
        fun fromGameState(
            state: GameState,
            viewerSeat: Int,
            legalActions: List<GameAction>,
            myFuriten: FuritenStatus = if (viewerSeat in state.players.indices) {
                FuritenRules.status(
                    hand = state.players[viewerSeat],
                    closedTiles = state.players[viewerSeat].closedTiles,
                    temporary = if (viewerSeat in state.sameTurnFuriten.indices) state.sameTurnFuriten[viewerSeat] else false,
                    riichi = if (viewerSeat in state.riichiFuriten.indices) state.riichiFuriten[viewerSeat] else false
                )
            } else FuritenStatus(),
            myDiscardHistory: List<Tile> = if (viewerSeat in state.players.indices) {
                val h = state.players[viewerSeat]
                (if (h.discardHistory.isNotEmpty()) h.discardHistory else h.discards).toList()
            } else emptyList()
        ): PlayerView {
            require(viewerSeat in 0..3) { "viewerSeat must be 0..3, got $viewerSeat" }
            val myHand = state.players[viewerSeat]

            // 他家只给暗牌数量（暗牌 + 刚摸的牌）
            val handCounts = state.players.map { player ->
                player.closedTiles.size + (if (player.drawnTile != null) 1 else 0)
            }

            return create(
                viewerSeat = viewerSeat,
                phase = state.phase,
                roundWind = state.roundWind,
                roundNumber = state.roundNumber,
                honbaSticks = state.honbaSticks,
                riichiSticks = state.riichiSticks,
                dealerSeat = state.dealerIndex,
                currentTurnPlayer = state.currentPlayer,
                wallRemaining = state.wallRemaining,
                doraIndicators = state.doraIndicators.toList(),
                scores = state.scores.toList(),
                riichiStatus = state.riichiDeclared.toList(),
                discards = state.players.map { it.discards.toList() },
                melds = state.players.map { it.melds.toList() },
                handTileCounts = handCounts,
                myClosedTiles = myHand.closedTiles.toList(),
                myDrawnTile = myHand.drawnTile,
                legalActions = legalActions,
                lastDiscard = state.lastDiscard,
                lastDiscardPlayer = if (state.lastDiscardPlayer in 0..3) state.lastDiscardPlayer else null,
                mySleeveTiles = if (viewerSeat in state.sleeveTiles.indices) state.sleeveTiles[viewerSeat].toList() else emptyList(),
                myFuriten = myFuriten,
                myDiscardHistory = myDiscardHistory
            )
        }
    }
}

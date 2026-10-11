package com.majiang.cheat.mark

import com.majiang.access.SeatControlView
import com.majiang.application.PlayerDecision
import com.majiang.economy.CheatEconomyConfig
import com.majiang.model.Tile
import com.majiang.round.RoundStateMachine
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.timing.DecisionTiming
import java.util.ArrayList

/**
 * 落汗千术私有投影工厂
 */
object TileMarkProjectionFactory {
    fun create(
        observerSeat: Int,
        session: com.majiang.match.MatchSession,
        activeRound: RoundStateMachine?,
        view: PlayerView,
        decision: PlayerDecision,
        controls: List<SeatControlView>,
        nowMillis: Long,
        timing: DecisionTiming?,
        isPaused: Boolean,
        config: TileMarkConfig,
        economyConfig: CheatEconomyConfig
    ): TileMarkView? {
        if (!config.enabled || observerSeat !in 0..3) {
            return null
        }

        val myGroups = session.tileMarkState.groupsForSeat(observerSeat)
        val activeTileCount = myGroups.sumOf { it.tileIds.size }
        val maxActiveTiles = 12
        val cost = economyConfig.costs["TILE_MARK"] ?: 10
        val currentOrdinal = session.tileMarkState.dealtRoundOrdinal
        val stateVersion = session.tileMarkState.version

        // 构造当前座位的各个生效组视图
        val groupViews = ArrayList<TileMarkGroupView>(myGroups.size)
        for (group in myGroups) {
            val futureRoundsRemaining = maxOf(0, group.expiresAfterRoundOrdinal - currentOrdinal)
            val isLastEffectiveRound = (group.expiresAfterRoundOrdinal == currentOrdinal)
            val tileViews = ArrayList<MarkedTileView>(4)

            val roundState = activeRound?.readState()
            val isRoundDealt = activeRound != null && roundState != null &&
                (roundState.phase != GamePhase.STACKING || roundState.wall.isNotEmpty() || roundState.players.any { it.closedTiles.isNotEmpty() })

            for (tileId in group.tileIds) {
                val tile = Tile.fromTileId(tileId)
                if (!isRoundDealt || roundState == null) {
                    tileViews.add(
                        MarkedTileView(
                            tileId = tileId,
                            tile = tile,
                            area = TileMarkArea.NOT_VISIBLE,
                            seat = null,
                            riverPosition = null
                        )
                    )
                } else {
                    var area: TileMarkArea = TileMarkArea.NOT_VISIBLE
                    var tileSeat: Int? = null
                    var riverPos: RiverPosition? = null

                    // 1. 检查牌河 (discards)
                    for (discardSeat in 0..3) {
                        val discards = view.discards.getOrNull(discardSeat) ?: emptyList()
                        val idx = discards.indexOfFirst { it.tileId == tileId }
                        if (idx != -1) {
                            area = TileMarkArea.DISCARD
                            tileSeat = discardSeat
                            riverPos = RiverPosition(
                                roundId = activeRound.roundId,
                                seat = discardSeat,
                                discardIndex = idx
                            )
                            break
                        }
                    }

                    // 2. 检查副露 (melds)
                    if (area == TileMarkArea.NOT_VISIBLE) {
                        for (meldSeat in 0..3) {
                            val melds = roundState.players[meldSeat].melds
                            val inOpenMeld = melds.any { it.isOpen && it.tiles.any { t -> t.tileId == tileId } }
                            if (inOpenMeld) {
                                area = TileMarkArea.EXPOSED_MELD
                                tileSeat = meldSeat
                                riverPos = null
                                break
                            }
                            val inClosedMeld = melds.any { !it.isOpen && it.tiles.any { t -> t.tileId == tileId } }
                            if (inClosedMeld && meldSeat == observerSeat) {
                                area = TileMarkArea.EXPOSED_MELD
                                tileSeat = meldSeat
                                riverPos = null
                                break
                            }
                        }
                    }

                    // 3. 检查手牌 (hands)
                    if (area == TileMarkArea.NOT_VISIBLE) {
                        val myPlayer = roundState.players[observerSeat]
                        val inMyHand = myPlayer.closedTiles.any { it.tileId == tileId } || myPlayer.drawnTile?.tileId == tileId
                        if (inMyHand) {
                            area = TileMarkArea.OWN_HAND
                            tileSeat = observerSeat
                            riverPos = null
                        } else {
                            for (oppSeat in 0..3) {
                                if (oppSeat != observerSeat) {
                                    val oppPlayer = roundState.players[oppSeat]
                                    val inOppHand = oppPlayer.closedTiles.any { it.tileId == tileId } || oppPlayer.drawnTile?.tileId == tileId
                                    if (inOppHand) {
                                        area = TileMarkArea.OPPONENT_HAND
                                        tileSeat = oppSeat
                                        riverPos = null
                                        break
                                    }
                                }
                            }
                        }
                    }

                    // 4. 检查宝牌指示牌 (doraIndicators)
                    if (area == TileMarkArea.NOT_VISIBLE) {
                        if (roundState.doraIndicators.any { it.tileId == tileId }) {
                            area = TileMarkArea.EXPOSED_INDICATOR
                            tileSeat = null
                            riverPos = null
                        }
                    }

                    tileViews.add(
                        MarkedTileView(
                            tileId = tileId,
                            tile = tile,
                            area = area,
                            seat = tileSeat,
                            riverPosition = riverPos
                        )
                    )
                }
            }

            groupViews.add(
                TileMarkGroupView(
                    groupId = group.groupId,
                    createdRoundId = group.createdRoundId,
                    createdRoundOrdinal = group.createdRoundOrdinal,
                    expiresAfterRoundOrdinal = group.expiresAfterRoundOrdinal,
                    futureRoundsRemaining = futureRoundsRemaining,
                    isLastEffectiveRound = isLastEffectiveRound,
                    tiles = tileViews
                )
            )
        }

        // 判定 canApply、disabledReason、selectableTileIds
        var canApply = true
        var disabledReason: String? = null

        val roundState = activeRound?.readState()
        val isHuman = controls.getOrNull(observerSeat)?.role == "HUMAN"

        if (isPaused) {
            canApply = false
            disabledReason = "HOST_PAUSED"
        } else if (!isHuman) {
            canApply = false
            disabledReason = "NOT_HUMAN_CONTROLLED"
        } else if (activeRound == null || roundState == null) {
            canApply = false
            disabledReason = "NO_ACTIVE_ROUND"
        } else if (roundState.currentPlayer != observerSeat) {
            canApply = false
            disabledReason = "NOT_YOUR_TURN"
        } else if (roundState.phase !in setOf(GamePhase.CHEAT_WINDOW, GamePhase.DISCARD) || activeRound.responseWindow != null) {
            canApply = false
            disabledReason = "WRONG_PHASE"
        } else if (roundState.riichiDeclared[observerSeat]) {
            canApply = false
            disabledReason = "TILE_MARK_RIICHI_LOCKED"
        } else if (decision.options.none { it.action is GameAction.DiscardTile }) {
            canApply = false
            disabledReason = "WRONG_PHASE"
        } else if (timing == null || nowMillis >= timing.deadlineMillis) {
            canApply = false
            disabledReason = "DEADLINE_EXPIRED"
        } else if (activeTileCount + 4 > maxActiveTiles || myGroups.size >= 3) {
            canApply = false
            disabledReason = "TILE_MARK_CAPACITY_EXCEEDED"
        } else if (roundState.cheatPoints[observerSeat] < cost) {
            canApply = false
            disabledReason = "INSUFFICIENT_FUNDS"
        } else {
            val myHandTiles = view.myClosedTiles + listOfNotNull(view.myDrawnTile)
            val alreadyMarkedIds = myGroups.flatMap { it.tileIds }.toSet()
            val available = myHandTiles.filter { it.tileId !in alreadyMarkedIds }
            if (available.size < 4) {
                canApply = false
                disabledReason = "INSUFFICIENT_SELECTABLE_TILES"
            }
        }

        val selectableTileIds = if (canApply) {
            val myHandTiles = view.myClosedTiles + listOfNotNull(view.myDrawnTile)
            val alreadyMarkedIds = myGroups.flatMap { it.tileIds }.toSet()
            myHandTiles.map { it.tileId }.filter { it !in alreadyMarkedIds }
        } else {
            emptyList()
        }

        return TileMarkView(
            canApply = canApply,
            disabledReason = disabledReason,
            cost = cost,
            activeTileCount = activeTileCount,
            maxActiveTiles = maxActiveTiles,
            selectableTileIds = selectableTileIds,
            decisionId = decision.decisionId,
            controlEpoch = decision.controlEpoch,
            stateVersion = stateVersion,
            activeGroups = groupViews
        )
    }
}

package com.majiang.round

import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.match.RoundOutcome
import com.majiang.model.*
import com.majiang.rules.FuritenRules
import com.majiang.rules.RiichiRuleEngine
import com.majiang.rules.WinningScoreEvaluator
import com.majiang.settlement.SettlementInput
import com.majiang.settlement.SettlementService
import com.majiang.state.*
import java.util.Collections
import java.util.LinkedHashSet
import java.util.UUID
import kotlin.random.Random

/**
 * 单局状态转移规划器
 *
 * 职责：
 * - 纯单局状态转移与机械后续计算；
 * - 在候选深拷贝上试探执行已批准的动作；
 * - 覆盖动作的所有机械后续（出牌自动建立响应窗口，无响应自动摸下一张或流局，杠牌翻指示牌与摸岭上等）；
 * - 不包含任何 AI 决策、外部事件总线发布或跨局推进 (MatchProgression)；
 * - 纯函数式，绝不修改传入的权威状态机实例。
 */
class RoundTransitionPlanner(
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
    val settlementService: SettlementService = SettlementService.INSTANCE,
    val random: Random = Random.Default
) {
    /**
     * 为单局状态机规划并计算已批准步骤的候选变更
     */
    internal fun plan(round: RoundStateMachine, action: GameAction): ApprovedRoundStep {
        val candidate = round.readState()
        val stepEvents = mutableListOf<GameEvent>()
        var newWindow: ResponseWindow? = null
        var roundResult: RoundResult? = null

        if (candidate.phase in setOf(GamePhase.RESPONSE_WINDOW, GamePhase.CHANKAN_WINDOW)) {
            val window = round.responseWindow?.deepCopy()
                ?: throw IllegalStateException("Current response window is null in phase ${candidate.phase}")
            val player = ResponseWindow.getActionPlayer(action)
            val recorded = window.recordResponse(player, action)
            if (!recorded) {
                throw IllegalStateException("Failed to record response for player $player: $action")
            }

            // 记录同巡或立直振听（选择任意非Ron响应即放弃合法Ron）
            if (action !is GameAction.DeclareRon && window.eligibleActions[player]?.any { it is GameAction.DeclareRon } == true) {
                if (candidate.riichiDeclared[player]) {
                    candidate.riichiFuriten[player] = true
                } else {
                    candidate.sameTurnFuriten[player] = true
                }
            }

            if (!window.isComplete) {
                // 窗口尚未收集齐所有资格座位，保持开放
                newWindow = window
                roundResult = null
            } else {
                // 窗口收集完毕，关闭并裁决
                newWindow = null
                val winner = ResponseResolver.INSTANCE.resolveWinner(window.fromPlayer, window.responses)
                if (winner != null) {
                    when (val action = winner.action) {
                        is GameAction.DeclareRon -> {
                            roundResult = executeRonCommit(round, candidate, winner.seat, window.fromPlayer, window.targetTile, window.isChankan, stepEvents)
                        }
                        is GameAction.DeclarePon -> {
                            executePonCommit(candidate, winner.seat, window.targetTile, window.fromPlayer, action.tiles, stepEvents)
                            roundResult = null
                        }
                        is GameAction.DeclareChi -> {
                            executeChiCommit(candidate, winner.seat, window.targetTile, window.fromPlayer, action.meld, stepEvents)
                            roundResult = null
                        }
                        is GameAction.DeclareKan -> {
                            executeOpenKanCommit(candidate, winner.seat, window.targetTile, window.fromPlayer, action.tiles, stepEvents)
                            roundResult = null
                        }
                        else -> {
                            if (window.isChankan) {
                                commitAddedKanAndRinshan(candidate, window.fromPlayer, window.targetTile, stepEvents)
                                roundResult = null
                            } else {
                                roundResult = advanceToNextDraw(round, candidate, stepEvents)
                            }
                        }
                    }
                } else {
                    // 全员 Pass
                    if (window.isChankan) {
                        commitAddedKanAndRinshan(candidate, window.fromPlayer, window.targetTile, stepEvents)
                        roundResult = null
                    } else {
                        roundResult = advanceToNextDraw(round, candidate, stepEvents)
                    }
                }
            }
        } else {
            when (action) {
                is GameAction.StartStacking -> {
                    candidate.resetForNewRound()
                    candidate.phase = GamePhase.STACKING
                    stepEvents.add(GameEvent.StackingStarted(candidate.turnNumber))

                    val allTiles = TilePool.buildStandardSet().toMutableList()
                    allTiles.shuffle(random)

                    val mainWall = allTiles.subList(0, 122).toMutableList()
                    val deadWallTiles = allTiles.subList(122, 136).toMutableList()

                    candidate.wall.clear()
                    candidate.wall.addAll(mainWall)
                    candidate.deadWall.clear()
                    candidate.deadWall.addAll(deadWallTiles)
                    candidate.deadWallReplacements.clear()
                    candidate.wallPointer = 0
                    candidate.wallEndExclusive = mainWall.size
                    candidate.rinshanPointer = 0

                    for (tile in mainWall) candidate.tilePool.transfer(tile, Zone.UNASSIGNED, Zone.WALL)
                    for (tile in deadWallTiles) candidate.tilePool.transfer(tile, Zone.UNASSIGNED, Zone.DEAD_WALL)

                    // 翻开首张 Dora 指示牌 (deadWall[4])
                    val doraIndicator = candidate.deadWall[4]
                    candidate.doraIndicators.add(doraIndicator)
                    stepEvents.add(GameEvent.DoraRevealed(candidate.turnNumber, doraIndicator, false))
                    stepEvents.add(GameEvent.WallBuilt(candidate.turnNumber, candidate.wall.size))

                    // 发牌流程
                    candidate.phase = GamePhase.DEALING
                    val order = (0 until 4).map { (candidate.dealerIndex + it) % 4 }
                    for (r in 0 until 3) {
                        for (p in order) {
                            for (i in 0 until 4) {
                                val dealt = TileMovement.deal(candidate, p)
                                stepEvents.add(GameEvent.TileDealt(candidate.turnNumber, p, dealt))
                            }
                        }
                    }
                    for (p in order) {
                        val dealt = TileMovement.deal(candidate, p)
                        stepEvents.add(GameEvent.TileDealt(candidate.turnNumber, p, dealt))
                    }

                    // 庄家第 14 张牌放入 drawnTile
                    val extraTile = TileMovement.dealExtra(candidate, candidate.dealerIndex)
                    stepEvents.add(GameEvent.TileDealt(candidate.turnNumber, candidate.dealerIndex, extraTile))

                    stepEvents.add(GameEvent.DealingComplete(candidate.turnNumber))
                    candidate.currentPlayer = candidate.dealerIndex
                    candidate.phase = GamePhase.CHEAT_WINDOW
                    newWindow = null
                    roundResult = null
                }

                is GameAction.DrawTile -> {
                    val tile = TileMovement.draw(candidate, action.player)
                    candidate.turnNumber++
                    candidate.sameTurnFuriten[action.player] = false
                    candidate.isRinshanDraw = false
                    stepEvents.add(GameEvent.TileDrawn(candidate.turnNumber, action.player, tile, candidate.wallRemaining))
                    candidate.phase = GamePhase.CHEAT_WINDOW
                    newWindow = null
                    roundResult = null
                }

                is GameAction.DiscardTile -> {
                    val isTsumogiri = candidate.players[action.player].drawnTile == action.tile
                    val discarded = TileMovement.discard(candidate, action.player, action.tile)
                    candidate.ippatsuActive[action.player] = false
                    candidate.players[action.player].ippatsu = false
                    candidate.isRinshanDraw = false

                    val isRiichiDiscard = candidate.riichiDeclared[action.player] &&
                            candidate.riichiTurn[action.player] == candidate.turnNumber

                    stepEvents.add(GameEvent.TileDiscarded(
                        candidate.turnNumber, action.player, discarded,
                        isRiichiDiscard = isRiichiDiscard,
                        isTsumogiri = isTsumogiri
                    ))
                    candidate.phase = GamePhase.RESPONSE_WINDOW
                    val openResult = openResponseWindow(round, candidate, isChankan = false, pendingKan = null, stepEvents)
                    newWindow = openResult.window
                    roundResult = openResult.result
                }

                is GameAction.DeclareRiichi -> {
                    candidate.riichiDeclared[action.player] = true
                    candidate.riichiTurn[action.player] = candidate.turnNumber
                    candidate.ippatsuActive[action.player] = true
                    candidate.players[action.player].riichiDeclared = true
                    candidate.players[action.player].riichiTurn = candidate.turnNumber
                    candidate.players[action.player].ippatsu = true
                    candidate.scores[action.player] -= 1000
                    candidate.riichiSticks++
                    candidate.isRinshanDraw = false

                    val isDoubleRiichi = candidate.isFirstUninterruptedGo && candidate.players[action.player].discards.isEmpty()
                    candidate.doubleRiichiDeclared[action.player] = isDoubleRiichi

                    stepEvents.add(GameEvent.RiichiDeclared(candidate.turnNumber, action.player, action.discardTile, isDoubleRiichi))

                    val isTsumogiri = candidate.players[action.player].drawnTile == action.discardTile
                    val discarded = TileMovement.discard(candidate, action.player, action.discardTile)
                    candidate.ippatsuActive[action.player] = true

                    stepEvents.add(GameEvent.TileDiscarded(
                        candidate.turnNumber, action.player, discarded,
                        isRiichiDiscard = true,
                        isTsumogiri = isTsumogiri
                    ))
                    candidate.phase = GamePhase.RESPONSE_WINDOW
                    val openResult = openResponseWindow(round, candidate, isChankan = false, pendingKan = null, stepEvents)
                    newWindow = openResult.window
                    roundResult = openResult.result
                }

                is GameAction.DeclareKan -> {
                    when (action.kanType) {
                        MeldType.CLOSED_KAN -> {
                            val meld = TileMovement.closedKan(candidate, action.player, action.tiles)
                            candidate.totalKanCount++
                            candidate.playerKanCounts[action.player]++
                            for (i in 0 until 4) {
                                candidate.ippatsuActive[i] = false
                                candidate.players[i].ippatsu = false
                            }
                            candidate.isFirstUninterruptedGo = false
                            revealNewDoraOnCandidate(candidate, stepEvents)
                            stepEvents.add(GameEvent.KanDeclared(candidate.turnNumber, action.player, meld, MeldType.CLOSED_KAN))

                            candidate.phase = GamePhase.KAN_DRAW
                            val rinshanTile = TileMovement.drawRinshan(candidate, action.player)
                            candidate.isRinshanDraw = true
                            candidate.turnNumber++
                            candidate.sameTurnFuriten[action.player] = false
                            stepEvents.add(GameEvent.RinshanDraw(candidate.turnNumber, action.player, rinshanTile))
                            candidate.phase = GamePhase.CHEAT_WINDOW
                            newWindow = null
                            roundResult = null
                        }

                        MeldType.ADDED_KAN -> {
                            candidate.phase = GamePhase.CHANKAN_WINDOW
                            val openResult = openResponseWindow(round, candidate, isChankan = true, pendingKan = action, stepEvents)
                            newWindow = openResult.window
                            roundResult = openResult.result
                        }

                        else -> throw IllegalArgumentException("Unsupported kanType in DeclareKan: ${action.kanType}")
                    }
                }

                is GameAction.DeclareTsumo -> {
                    roundResult = executeTsumoCommit(round, candidate, action.player, stepEvents)
                    newWindow = null
                }

                is GameAction.DeclareAbortiveDraw -> {
                    roundResult = executeNineTerminalsDraw(round, candidate, stepEvents)
                    newWindow = null
                }

                else -> throw IllegalArgumentException("Unsupported action in round: $action")
            }
        }

        return ApprovedRoundStep(
            action = action,
            state = candidate,
            responseWindow = newWindow,
            result = roundResult,
            events = stepEvents
        )
    }

    private data class OpenWindowResult(
        val window: ResponseWindow?,
        val result: RoundResult?
    )

    private fun openResponseWindow(
        round: RoundStateMachine,
        candidate: GameState,
        isChankan: Boolean,
        pendingKan: GameAction.DeclareKan? = null,
        events: MutableList<GameEvent>
    ): OpenWindowResult {
        val fromPlayer = if (isChankan) pendingKan!!.player else candidate.lastDiscardPlayer
        val targetTile = if (isChankan) {
            val hand = candidate.players[fromPlayer]
            pendingKan!!.tiles.first { tile -> hand.drawnTile == tile || hand.closedTiles.contains(tile) }
        } else {
            candidate.lastDiscard!!
        }

        val eligible = LegalActionProvider.buildEligibleResponseActions(
            state = candidate,
            discarder = fromPlayer,
            targetTile = targetTile,
            isChankan = isChankan,
            ruleEngine = ruleEngine,
            winningScoreEvaluator = winningScoreEvaluator
        )

        // 记录无役完成形等未获得合法荣和机会的数学完成形振听
        for (seat in 0 until 4) {
            if (seat == fromPlayer) continue
            val hasLegalRon = eligible[seat]?.any { it is GameAction.DeclareRon } == true
            if (!hasLegalRon && FuritenRules.completesHand(candidate.players[seat], targetTile)) {
                if (candidate.riichiDeclared[seat]) {
                    candidate.riichiFuriten[seat] = true
                } else {
                    candidate.sameTurnFuriten[seat] = true
                }
            }
        }

        if (eligible.isEmpty()) {
            if (isChankan) {
                commitAddedKanAndRinshan(candidate, fromPlayer, targetTile, events)
                return OpenWindowResult(window = null, result = null)
            } else {
                val nextDrawResult = advanceToNextDraw(round, candidate, events)
                return OpenWindowResult(window = null, result = nextDrawResult)
            }
        }

        val window = ResponseWindow(
            id = candidate.turnNumber,
            fromPlayer = fromPlayer,
            targetTile = targetTile,
            eligibleActions = eligible,
            isChankan = isChankan,
            pendingKanAction = pendingKan
        )
        candidate.phase = if (isChankan) GamePhase.CHANKAN_WINDOW else GamePhase.RESPONSE_WINDOW
        return OpenWindowResult(window = window, result = null)
    }

    private fun advanceToNextDraw(
        round: RoundStateMachine,
        candidate: GameState,
        events: MutableList<GameEvent>
    ): RoundResult? {
        val abortive = ruleEngine.checkAbortiveDraw(candidate)
        if (abortive != null) {
            return handleDrawGame(round, candidate, abortive, events)
        }

        if (candidate.wallRemaining <= 0) {
            return handleDrawGame(round, candidate, RiichiRuleEngine.AbortiveDrawType.EXHAUSTIVE, events)
        }

        if (candidate.players.all { it.discards.isNotEmpty() }) {
            candidate.isFirstUninterruptedGo = false
        }

        val nextPlayer = candidate.nextPlayer(candidate.lastDiscardPlayer)
        candidate.currentPlayer = nextPlayer
        candidate.phase = GamePhase.DRAW
        val tile = TileMovement.draw(candidate, nextPlayer)
        candidate.turnNumber++
        candidate.sameTurnFuriten[nextPlayer] = false
        candidate.isRinshanDraw = false
        events.add(GameEvent.TileDrawn(candidate.turnNumber, nextPlayer, tile, candidate.wallRemaining))
        candidate.phase = GamePhase.CHEAT_WINDOW
        return null
    }

    private fun handleDrawGame(
        round: RoundStateMachine,
        candidate: GameState,
        reason: RiichiRuleEngine.AbortiveDrawType,
        events: MutableList<GameEvent>
    ): RoundResult {
        if (reason == RiichiRuleEngine.AbortiveDrawType.EXHAUSTIVE) {
            val tenpaiSeats = (0 until 4).filter { ruleEngine.isTenpai(candidate.players[it].allClosedTiles) }.toSet()
            val input = SettlementInput(candidate.scores.toList(), candidate.dealerIndex, candidate.honbaSticks, candidate.riichiSticks)
            val settlement = settlementService.exhaustiveDraw(input, tenpaiSeats)

            for (i in 0 until 4) candidate.scores[i] = settlement.newScores[i]
            candidate.riichiSticks = settlement.remainingRiichiSticks

            val payments = (0 until 4).associateWith { settlement.deltas[it] }
            events.add(GameEvent.DrawGame(candidate.turnNumber, DrawReason.EXHAUSTIVE, tenpaiSeats.toList()))
            events.add(GameEvent.ScoreSettled(candidate.turnNumber, payments, settlement.newScores.toIntArray()))
            candidate.phase = GamePhase.ROUND_END
            events.add(GameEvent.RoundEnded(candidate.turnNumber, candidate.roundWind, candidate.roundNumber))

            val outcome = RoundOutcome.ExhaustiveDraw(Collections.unmodifiableSet(LinkedHashSet(tenpaiSeats)))
            return buildRoundResult(round, candidate, outcome, events)
        } else {
            val drawReason = when (reason) {
                RiichiRuleEngine.AbortiveDrawType.FOUR_WINDS -> DrawReason.FOUR_WINDS
                RiichiRuleEngine.AbortiveDrawType.FOUR_KANS -> DrawReason.FOUR_KANS
                RiichiRuleEngine.AbortiveDrawType.FOUR_RIICHI -> DrawReason.FOUR_RIICHI
                RiichiRuleEngine.AbortiveDrawType.NINE_TERMINALS -> DrawReason.NINE_TERMINALS
                else -> DrawReason.EXHAUSTIVE
            }

            val input = SettlementInput(candidate.scores.toList(), candidate.dealerIndex, candidate.honbaSticks, candidate.riichiSticks)
            val settlement = settlementService.abortiveDraw(input)
            val payments = (0 until 4).associateWith { 0 }

            events.add(GameEvent.DrawGame(candidate.turnNumber, drawReason, emptyList()))
            events.add(GameEvent.ScoreSettled(candidate.turnNumber, payments, settlement.newScores.toIntArray()))
            candidate.phase = GamePhase.ROUND_END
            events.add(GameEvent.RoundEnded(candidate.turnNumber, candidate.roundWind, candidate.roundNumber))

            val outcome = RoundOutcome.AbortiveDraw(drawReason.name)
            return buildRoundResult(round, candidate, outcome, events)
        }
    }

    private fun executeRonCommit(
        round: RoundStateMachine,
        candidate: GameState,
        winner: Int,
        loser: Int,
        winTile: Tile,
        isChankan: Boolean,
        events: MutableList<GameEvent>
    ): RoundResult {
        if (candidate.riichiDeclared[winner]) {
            candidate.uraDoraIndicators.clear()
            for (k in 0 until candidate.doraIndicators.size) {
                val uraIndex = 4 + k * 2 + 1
                if (uraIndex < candidate.deadWall.size) {
                    val ura = candidate.deadWall[uraIndex]
                    candidate.uraDoraIndicators.add(ura)
                    events.add(GameEvent.DoraRevealed(candidate.turnNumber, ura, isUraDora = true))
                }
            }
        } else {
            candidate.uraDoraIndicators.clear()
        }

        val context = ruleEngine.buildWinContext(candidate, winner, winTile, isTsumo = false, isChankan = isChankan)
        val evaluation = winningScoreEvaluator.evaluate(
            hand = candidate.players[winner],
            context = context,
            doraIndicators = candidate.doraIndicators,
            uraDoraIndicators = candidate.uraDoraIndicators,
            isDealer = (winner == candidate.dealerIndex)
        ) ?: throw IllegalStateException("Winning evaluation failed on ron commit")

        val input = SettlementInput(candidate.scores.toList(), candidate.dealerIndex, candidate.honbaSticks, candidate.riichiSticks)
        val settlement = settlementService.ron(input, winner, loser, evaluation.scoreResult)

        for (i in 0 until 4) candidate.scores[i] = settlement.newScores[i]
        candidate.riichiSticks = settlement.remainingRiichiSticks

        val yakuNames = evaluation.yakuResult.yakuList.map { "${it.first.displayName} ${it.second}翻" }.toMutableList()
        val ura = if (candidate.riichiDeclared[winner]) candidate.uraDoraIndicators else emptyList()
        val evalHand = PlayerHand().apply {
            closedTiles.addAll(candidate.players[winner].closedTiles)
            drawnTile = winTile
            melds.addAll(candidate.players[winner].melds)
        }
        val breakdown = winningScoreEvaluator.breakdownDora(evalHand, candidate.doraIndicators, ura)
        if (!evaluation.scoreResult.isYakuman) {
            if (breakdown.indicatorCount > 0) {
                yakuNames.add("DORA +${breakdown.indicatorCount}")
            }
            if (breakdown.uraCount > 0) {
                yakuNames.add("URA DORA +${breakdown.uraCount}")
            }
            if (breakdown.redCount > 0) {
                yakuNames.add("RED DORA +${breakdown.redCount}")
            }
        }
        val payments = (0 until 4).associateWith { settlement.deltas[it] }

        val scoreBreakdown = winningScoreEvaluator.explain(
            hand = candidate.players[winner],
            context = context,
            doraIndicators = candidate.doraIndicators,
            uraDoraIndicators = candidate.uraDoraIndicators,
            isDealer = (winner == candidate.dealerIndex),
            honba = input.honba,
            riichiSticks = input.riichiSticks,
            loser = loser
        )

        events.add(GameEvent.RonWin(candidate.turnNumber, winner, loser, winTile, yakuNames, evaluation.scoreResult.han, evaluation.fu, evaluation.totalPoints, scoreBreakdown))
        events.add(GameEvent.ScoreSettled(candidate.turnNumber, payments, settlement.newScores.toIntArray()))
        candidate.phase = GamePhase.ROUND_END
        events.add(GameEvent.RoundEnded(candidate.turnNumber, candidate.roundWind, candidate.roundNumber))

        val outcome = if (winner == candidate.dealerIndex) RoundOutcome.DealerWin(winner) else RoundOutcome.NonDealerWin(winner)
        return buildRoundResult(round, candidate, outcome, events)
    }

    private fun executeTsumoCommit(
        round: RoundStateMachine,
        candidate: GameState,
        player: Int,
        events: MutableList<GameEvent>
    ): RoundResult {
        val hand = candidate.players[player]
        val winTile = hand.drawnTile ?: throw IllegalStateException("Player $player has no drawnTile for tsumo")

        if (candidate.riichiDeclared[player]) {
            candidate.uraDoraIndicators.clear()
            for (k in 0 until candidate.doraIndicators.size) {
                val uraIndex = 4 + k * 2 + 1
                if (uraIndex < candidate.deadWall.size) {
                    val ura = candidate.deadWall[uraIndex]
                    candidate.uraDoraIndicators.add(ura)
                    events.add(GameEvent.DoraRevealed(candidate.turnNumber, ura, isUraDora = true))
                }
            }
        } else {
            candidate.uraDoraIndicators.clear()
        }

        val context = ruleEngine.buildWinContext(candidate, player, winTile, isTsumo = true)
        val evaluation = winningScoreEvaluator.evaluate(
            hand = hand,
            context = context,
            doraIndicators = candidate.doraIndicators,
            uraDoraIndicators = candidate.uraDoraIndicators,
            isDealer = (player == candidate.dealerIndex)
        ) ?: throw IllegalStateException("Winning evaluation failed on tsumo commit")

        val input = SettlementInput(candidate.scores.toList(), candidate.dealerIndex, candidate.honbaSticks, candidate.riichiSticks)
        val settlement = settlementService.tsumo(input, player, evaluation.scoreResult)

        for (i in 0 until 4) candidate.scores[i] = settlement.newScores[i]
        candidate.riichiSticks = settlement.remainingRiichiSticks

        val yakuNames = evaluation.yakuResult.yakuList.map { "${it.first.displayName} ${it.second}翻" }.toMutableList()
        val ura = if (hand.riichiDeclared) candidate.uraDoraIndicators else emptyList()
        val breakdown = winningScoreEvaluator.breakdownDora(hand, candidate.doraIndicators, ura)
        if (!evaluation.scoreResult.isYakuman) {
            if (breakdown.indicatorCount > 0) {
                yakuNames.add("DORA +${breakdown.indicatorCount}")
            }
            if (breakdown.uraCount > 0) {
                yakuNames.add("URA DORA +${breakdown.uraCount}")
            }
            if (breakdown.redCount > 0) {
                yakuNames.add("RED DORA +${breakdown.redCount}")
            }
        }
        val payments = (0 until 4).associateWith { settlement.deltas[it] }

        val scoreBreakdown = winningScoreEvaluator.explain(
            hand = hand,
            context = context,
            doraIndicators = candidate.doraIndicators,
            uraDoraIndicators = candidate.uraDoraIndicators,
            isDealer = (player == candidate.dealerIndex),
            honba = input.honba,
            riichiSticks = input.riichiSticks,
            loser = null
        )

        events.add(GameEvent.TsumoWin(candidate.turnNumber, player, winTile, yakuNames, evaluation.scoreResult.han, evaluation.fu, evaluation.totalPoints, scoreBreakdown))
        events.add(GameEvent.ScoreSettled(candidate.turnNumber, payments, settlement.newScores.toIntArray()))
        candidate.phase = GamePhase.ROUND_END
        events.add(GameEvent.RoundEnded(candidate.turnNumber, candidate.roundWind, candidate.roundNumber))

        val outcome = if (player == candidate.dealerIndex) RoundOutcome.DealerWin(player) else RoundOutcome.NonDealerWin(player)
        return buildRoundResult(round, candidate, outcome, events)
    }

    private fun executeNineTerminalsDraw(
        round: RoundStateMachine,
        candidate: GameState,
        events: MutableList<GameEvent>
    ): RoundResult {
        val input = SettlementInput(candidate.scores.toList(), candidate.dealerIndex, candidate.honbaSticks, candidate.riichiSticks)
        val settlement = settlementService.abortiveDraw(input)
        val payments = (0 until 4).associateWith { 0 }

        events.add(GameEvent.DrawGame(candidate.turnNumber, DrawReason.NINE_TERMINALS, emptyList()))
        events.add(GameEvent.ScoreSettled(candidate.turnNumber, payments, settlement.newScores.toIntArray()))
        candidate.phase = GamePhase.ROUND_END
        events.add(GameEvent.RoundEnded(candidate.turnNumber, candidate.roundWind, candidate.roundNumber))

        val outcome = RoundOutcome.AbortiveDraw("NINE_TERMINALS")
        return buildRoundResult(round, candidate, outcome, events)
    }

    private fun executePonCommit(
        candidate: GameState,
        player: Int,
        calledTile: Tile,
        fromPlayer: Int,
        tiles: List<Tile>,
        events: MutableList<GameEvent>
    ) {
        val meld = TileMovement.pon(candidate, player, calledTile, fromPlayer, tiles)
        for (i in 0 until 4) {
            candidate.ippatsuActive[i] = false
            candidate.players[i].ippatsu = false
        }
        candidate.isFirstUninterruptedGo = false
        candidate.currentPlayer = player
        candidate.phase = GamePhase.DISCARD
        events.add(GameEvent.PonDeclared(candidate.turnNumber, player, meld, fromPlayer))
    }

    private fun executeChiCommit(
        candidate: GameState,
        player: Int,
        calledTile: Tile,
        fromPlayer: Int,
        meld: Meld,
        events: MutableList<GameEvent>
    ) {
        val handTiles = meld.tiles.filter { it != calledTile }
        val created = TileMovement.chi(candidate, player, calledTile, fromPlayer, handTiles)
        for (i in 0 until 4) {
            candidate.ippatsuActive[i] = false
            candidate.players[i].ippatsu = false
        }
        candidate.isFirstUninterruptedGo = false
        candidate.currentPlayer = player
        candidate.phase = GamePhase.DISCARD
        events.add(GameEvent.ChiDeclared(candidate.turnNumber, player, created, fromPlayer))
    }

    private fun executeOpenKanCommit(
        candidate: GameState,
        player: Int,
        calledTile: Tile,
        fromPlayer: Int,
        tiles: List<Tile>,
        events: MutableList<GameEvent>
    ) {
        val handTiles = tiles.filter { it != calledTile }
        val meld = TileMovement.openKan(candidate, player, calledTile, fromPlayer, handTiles)
        candidate.totalKanCount++
        candidate.playerKanCounts[player]++
        for (i in 0 until 4) {
            candidate.ippatsuActive[i] = false
            candidate.players[i].ippatsu = false
        }
        candidate.isFirstUninterruptedGo = false
        revealNewDoraOnCandidate(candidate, events)
        events.add(GameEvent.KanDeclared(candidate.turnNumber, player, meld, MeldType.OPEN_KAN))

        candidate.currentPlayer = player
        candidate.phase = GamePhase.KAN_DRAW
        val rinshan = TileMovement.drawRinshan(candidate, player)
        candidate.isRinshanDraw = true
        candidate.turnNumber++
        candidate.sameTurnFuriten[player] = false
        events.add(GameEvent.RinshanDraw(candidate.turnNumber, player, rinshan))
        candidate.phase = GamePhase.CHEAT_WINDOW
    }

    private fun commitAddedKanAndRinshan(
        candidate: GameState,
        player: Int,
        addedTile: Tile,
        events: MutableList<GameEvent>
    ) {
        val meld = TileMovement.addedKan(candidate, player, addedTile)
        candidate.totalKanCount++
        candidate.playerKanCounts[player]++
        for (i in 0 until 4) {
            candidate.ippatsuActive[i] = false
            candidate.players[i].ippatsu = false
        }
        candidate.isFirstUninterruptedGo = false
        revealNewDoraOnCandidate(candidate, events)
        events.add(GameEvent.KanDeclared(candidate.turnNumber, player, meld, MeldType.ADDED_KAN))

        candidate.phase = GamePhase.KAN_DRAW
        val rinshan = TileMovement.drawRinshan(candidate, player)
        candidate.isRinshanDraw = true
        candidate.turnNumber++
        candidate.sameTurnFuriten[player] = false
        events.add(GameEvent.RinshanDraw(candidate.turnNumber, player, rinshan))
        candidate.phase = GamePhase.CHEAT_WINDOW
    }

    private fun revealNewDoraOnCandidate(candidate: GameState, events: MutableList<GameEvent>) {
        val nextDoraIndex = 4 + candidate.doraIndicators.size * 2
        if (nextDoraIndex < candidate.deadWall.size) {
            val indicator = candidate.deadWall[nextDoraIndex]
            candidate.doraIndicators.add(indicator)
            events.add(GameEvent.DoraRevealed(candidate.turnNumber, indicator, false))
        }
    }

    private fun buildRoundResult(
        round: RoundStateMachine,
        candidate: GameState,
        outcome: RoundOutcome,
        stepEvents: List<GameEvent>
    ): RoundResult {
        val startingScores = round.startingScores.toList()
        val finalScores = candidate.scores.toList()
        val totalDeltas = (0 until 4).map { finalScores[it] - startingScores[it] }
        val startingRiichiSticks = round.startingRiichiSticks
        val remainingRiichiSticks = candidate.riichiSticks
        val allEvents = (round.accumulatedEvents + stepEvents).map { snapshotGameEvent(it) }
        val isolatedOutcome = when (outcome) {
            is RoundOutcome.ExhaustiveDraw -> RoundOutcome.ExhaustiveDraw(
                Collections.unmodifiableSet(LinkedHashSet(outcome.tenpaiSeats))
            )
            else -> outcome
        }
        val startingCheat = round.startingCheatPoints.toList()
        val finalCheat = candidate.cheatPoints.toList()
        val cheatDeltas = (0 until 4).map { finalCheat[it] - startingCheat[it] }
        val cheatEntries = candidate.cheatEntries.map { it.copy() }
        return RoundResult(
            resultId = UUID.randomUUID().toString(),
            roundId = round.roundId,
            outcome = isolatedOutcome,
            startingScores = startingScores,
            finalScores = finalScores,
            totalDeltas = totalDeltas,
            startingRiichiSticks = startingRiichiSticks,
            remainingRiichiSticks = remainingRiichiSticks,
            events = allEvents,
            startingCheatPoints = startingCheat,
            finalCheatPoints = finalCheat,
            cheatPointDeltas = cheatDeltas,
            cheatEntries = cheatEntries
        )
    }
}

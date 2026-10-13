package com.majiang.cheat.beam

import com.majiang.access.SeatControlView
import com.majiang.application.PlayerDecision
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchSession
import com.majiang.model.Tile
import com.majiang.model.TileLocation
import com.majiang.round.RoundStateMachine
import com.majiang.session.PlayerView
import com.majiang.state.GamePhase
import com.majiang.timing.DecisionTiming

/**
 * 偷梁换柱私有投影工厂
 */
object BeamSwapProjectionFactory {
    fun create(
        observerSeat: Int,
        activeRound: RoundStateMachine?,
        view: PlayerView,
        decision: PlayerDecision,
        controls: List<SeatControlView>,
        timing: DecisionTiming?,
        nowMillis: Long,
        isPaused: Boolean = false,
        config: BeamSwapConfig,
        economyConfig: CheatEconomyConfig,
        session: MatchSession? = null
    ): BeamSwapView? {
        if (!config.enabled || observerSeat !in 0..3) {
            return null
        }

        val cost = economyConfig.costs["BEAM_SWAP"] ?: 10
        val stateVersion = activeRound?.version ?: 0L

        if (activeRound == null || activeRound.state.phase == GamePhase.ROUND_END) {
            return BeamSwapView(
                canApply = false,
                disabledReason = "NO_ACTIVE_ROUND",
                cost = cost,
                window = null,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                stateVersion = stateVersion,
                pendingOffer = null
            )
        }

        val state = activeRound.readState()
        val offer = state.beamSwapOffer

        // 落汗标记获取（仅观察者本人的有效标记）
        val myMarkedTileIds = if (session != null) {
            session.tileMarkState.groupsForSeat(observerSeat).flatMap { it.tileIds }.toSet()
        } else {
            emptySet()
        }

        val pendingOffer = if (offer != null && offer.seat == observerSeat) {
            val candidateViews = offer.candidates.map { c ->
                val isMarked = c.tileId in myMarkedTileIds
                BeamSwapCandidateView(
                    slot = c.slot,
                    marked = isMarked,
                    tile = if (isMarked) Tile.fromTileId(c.tileId) else null
                )
            }
            BeamSwapOfferView(
                offerId = offer.offerId,
                window = offer.window,
                candidates = candidateViews,
                selectableHandTileIds = offer.eligibleHandTileIds,
                deadlineMillis = timing?.deadlineMillis ?: 0L,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch
            )
        } else {
            null
        }

        val window = if (state.players[observerSeat].drawnTile != null && state.currentPlayer == observerSeat) {
            when (state.lastDrawKind) {
                SleeveBloomDrawKind.NORMAL -> BeamSwapWindow.WALL
                SleeveBloomDrawKind.RINSHAN -> BeamSwapWindow.DEAD_WALL
                else -> null
            }
        } else {
            null
        }

        val ctrl = controls.find { it.seat == observerSeat }
        val isHuman = ctrl != null && ctrl.role == "HUMAN" && ctrl.connected

        val drawn = state.players[observerSeat].drawnTile
        val drawnInHand = drawn != null && state.tilePool.getLocation(drawn) == TileLocation.hand(observerSeat)

        val eligibleHandTiles = if (state.currentPlayer == observerSeat) {
            state.players[observerSeat].closedTiles.filter {
                it != drawn && state.tilePool.getLocation(it) == TileLocation.hand(observerSeat)
            }
        } else {
            emptyList()
        }

        val normalCandidatesExist = state.wallRemaining >= 1 && state.wallPointer < state.wallEndExclusive
        val deadWallCandidatesExist = (if (state.rinshanPointer in 0..4) (4 - state.rinshanPointer) else 0) +
                (4..13).count { idx ->
                    val t = state.deadWall.getOrNull(idx)
                    t != null && !state.doraIndicators.contains(t)
                } +
                state.deadWallReplacements.size > 0

        val canApply: Boolean
        val disabledReason: String?

        if (isPaused) {
            canApply = false
            disabledReason = "HOST_PAUSED"
        } else if (pendingOffer != null || state.beamSwapOffer != null || state.sleeveBloomOffer != null) {
            canApply = false
            disabledReason = if (pendingOffer != null || state.beamSwapOffer != null) {
                "BEAM_SWAP_ALREADY_PENDING"
            } else {
                "SLEEVE_BLOOM_ALREADY_PENDING"
            }
        } else if (!isHuman) {
            canApply = false
            disabledReason = "ACTION_NOT_AVAILABLE"
        } else if (state.currentPlayer != observerSeat) {
            canApply = false
            disabledReason = "NOT_YOUR_TURN"
        } else if (state.phase != GamePhase.CHEAT_WINDOW || activeRound.responseWindow != null) {
            canApply = false
            disabledReason = "WRONG_PHASE"
        } else if (drawn == null || !drawnInHand) {
            canApply = false
            disabledReason = "BEAM_SWAP_NO_DRAWN_TILE"
        } else if (eligibleHandTiles.isEmpty()) {
            canApply = false
            disabledReason = "BEAM_SWAP_NO_HAND_TILES"
        } else if (state.riichiDeclared[observerSeat]) {
            canApply = false
            disabledReason = "BEAM_SWAP_RIICHI_LOCKED"
        } else if (state.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            canApply = false
            disabledReason = "BEAM_SWAP_DEALER_EXTRA_EXCLUDED"
        } else if (state.beamSwapUsedOnTurn == state.turnNumber || state.sleeveBloomUsedOnTurn == state.turnNumber) {
            canApply = false
            disabledReason = "BEAM_SWAP_ALREADY_USED_THIS_DRAW"
        } else if (state.lastDrawKind == SleeveBloomDrawKind.NORMAL && !normalCandidatesExist) {
            canApply = false
            disabledReason = "BEAM_SWAP_NO_CANDIDATES"
        } else if (state.lastDrawKind == SleeveBloomDrawKind.RINSHAN && !deadWallCandidatesExist) {
            canApply = false
            disabledReason = "BEAM_SWAP_NO_CANDIDATES"
        } else if (state.cheatPoints[observerSeat] < cost) {
            canApply = false
            disabledReason = "INSUFFICIENT_FUNDS"
        } else if (timing == null) {
            canApply = false
            disabledReason = "NO_ACTIVE_DECISION"
        } else if (nowMillis >= timing.deadlineMillis) {
            canApply = false
            disabledReason = "DEADLINE_EXPIRED"
        } else {
            canApply = true
            disabledReason = null
        }

        return BeamSwapView(
            canApply = canApply,
            disabledReason = disabledReason,
            cost = cost,
            window = window,
            decisionId = decision.decisionId,
            controlEpoch = decision.controlEpoch,
            stateVersion = stateVersion,
            pendingOffer = pendingOffer
        )
    }
}

package com.majiang.architecture

import com.majiang.application.*
import com.majiang.event.GameEvent
import com.majiang.match.*
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SessionBoundaryAcceptanceTest {
    private fun hand() = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
        (2..4).map { Tile(suit, it) }
    } + (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)

    private fun winningState() = TestTableBuilder.create(hands = mapOf(0 to hand()), drawn = 0 to Tile(Suit.MAN, 5), dealer = 0)

    private fun coordinator(state: GameState): ApplicationCoordinator = ApplicationCoordinator(
        initialState = state, autoPlayAi = false, autoRiichiDiscard = false
    ).also { it.initializeMatch(List(4) { SeatController.RemoteHuman(false) }) }

    private fun accepted(result: ActionResult) {
        assertTrue(result is ActionResult.Accepted, "$result")
    }

    @Test fun `dealer repeat creates a new machine and leaves the old round immutable`() {
        val c = coordinator(winningState())
        val old = c.activeRound!!
        accepted(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)))
        assertEquals(GamePhase.ROUND_END, old.readState().phase)
        val oldView = PlayerView.fromGameState(old.readState(), 0, emptyList())
        val result = old.result!!
        val boundaryScores = c.queryView(0).scores
        assertEquals(14, c.queryView(0).myAllClosedTiles.size, "Settlement keeps the ended round's visible hand")
        assertEquals(0, c.queryView(0).honbaSticks, "Settlement still describes the ended round")
        accepted(c.nextRound())
        val next = c.activeRound!!
        assertNotSame(old, next)
        assertNotEquals(old.roundId, next.roundId)
        assertEquals(0, c.queryView(0).dealerSeat)
        assertEquals(1, c.queryView(0).honbaSticks)
        assertEquals(boundaryScores, c.queryView(0).scores)
        assertEquals(result.finalScores, c.queryView(0).scores)
        assertEquals(oldView, PlayerView.fromGameState(old.readState(), 0, emptyList()))
        assertEquals(result.resultId, old.result!!.resultId)
        assertEquals(result.finalScores, old.result!!.finalScores)
        assertEquals(result.events.map { it.toString() }, old.result!!.events.map { it.toString() })
    }

    @Test fun `repeated end action and next round cannot settle or create a second active round`() {
        val c = coordinator(winningState())
        val events = mutableListOf<GameEvent>()
        c.subscribe { events.addAll(it) }
        accepted(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)))
        val scores = c.queryView(0).scores
        assertTrue(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)) is ActionResult.Rejected)
        assertEquals(scores, c.queryView(0).scores)
        assertEquals(1, events.count { it is GameEvent.ScoreSettled })
        accepted(c.nextRound())
        val active = c.activeRound
        assertTrue(c.nextRound() is ActionResult.Rejected)
        assertSame(active, c.activeRound)
        assertEquals(scores, c.queryView(0).scores)
    }

    @Test fun `riichi deposits transfer exactly once across round and session ledgers`() {
        val s = winningState()
        s.riichiDeclared[0] = true
        s.players[0].riichiDeclared = true
        s.scores[0] = 24000
        s.riichiSticks = 1
        val c = coordinator(s)
        val old = c.activeRound!!
        accepted(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)))
        val result = old.result!!
        assertEquals(24000, result.startingScores[0])
        assertEquals(1, result.startingRiichiSticks)
        assertEquals(100000, result.finalScores.sum() + result.remainingRiichiSticks * 1000)
        assertEquals(0, result.remainingRiichiSticks)
        assertEquals(result.finalScores, c.queryView(0).scores)
        accepted(c.nextRound())
        assertEquals(result.finalScores, c.queryView(0).scores)
        assertEquals(0, c.queryView(0).riichiSticks)
    }

    @Test fun `dealer end choice finishes session without changing completed round`() {
        val s = winningState()
        s.roundWind = 2
        s.roundNumber = 3
        s.scores[0] = 40000
        s.scores[1] = 20000
        s.scores[2] = 20000
        s.scores[3] = 20000
        val c = coordinator(s)
        val old = c.activeRound!!
        accepted(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)))
        val roundScores = old.readState().scores.toList()
        val roundVersion = old.version
        assertTrue(c.legalActions(0).any { it is GameAction.DeclareAgariYame })
        val decision = c.queryDecision(0)
        assertNull(decision.roundId, "Dealer end belongs to Session")
        val choice = decision.options.first { it.action is GameAction.DeclareAgariYame }
        accepted(c.submitChoice(0, ActionChoice("dealer-end", decision.roundId, decision.decisionId, decision.controlEpoch, choice.optionId)))
        assertEquals(GamePhase.MATCH_END, c.queryView(0).phase)
        assertEquals(14, c.queryView(0).myAllClosedTiles.size, "Final settlement keeps the winning hand visible")
        assertEquals(GamePhase.ROUND_END, old.readState().phase)
        assertEquals(roundVersion, old.version)
        assertEquals(roundScores, old.readState().scores.toList())
        assertTrue(c.nextRound() is ActionResult.Rejected)
    }

    @Test fun `result deltas include a riichi deposit paid during this same round`() {
        val s = TestTableBuilder.create(hands = mapOf(0 to hand()), drawn = 0 to Tile(Suit.WIND, 4), dealer = 0,
            wallPrefix = listOf(Tile(Suit.DRAGON, 1), Tile(Suit.DRAGON, 2), Tile(Suit.DRAGON, 3), Tile(Suit.MAN, 5)))
        val c = coordinator(s)
        val round = c.activeRound!!
        fun passResponses() {
            repeat(4) {
                val window = c.activeRound?.responseWindow ?: return
                val seat = window.eligibleSeats.firstOrNull { !window.hasResponded(it) } ?: return
                accepted(c.submitPlayerAction(seat, GameAction.Pass(seat)))
            }
            assertNull(c.activeRound?.responseWindow)
        }
        accepted(c.submitPlayerAction(0, GameAction.DeclareRiichi(0, Tile(Suit.WIND, 4))))
        assertEquals(24000, c.queryView(0).scores[0])
        assertEquals(1, c.queryView(0).riichiSticks)
        passResponses()
        for (seat in 1..3) {
            assertEquals(seat, c.queryView(0).currentTurnPlayer)
            val draw = c.queryView(seat).myDrawnTile!!
            accepted(c.submitPlayerAction(seat, GameAction.DiscardTile(seat, draw)))
            passResponses()
        }
        assertTrue(c.legalActions(0).any { it is GameAction.DeclareTsumo })
        accepted(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)))
        val result = round.result!!
        assertEquals(List(4) { 25000 }, result.startingScores)
        assertEquals(result.finalScores.map { it - 25000 }, result.totalDeltas)
        assertEquals(100000, result.finalScores.sum())
        accepted(c.nextRound())
        assertEquals(result.finalScores, c.queryView(0).scores)
    }
}

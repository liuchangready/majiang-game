package com.majiang.architecture

import com.majiang.event.GameEvent
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.round.*
import com.majiang.session.PlayerView
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.random.Random

class RoundBoundaryAcceptanceTest {
    private fun winningHand() = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
        (2..4).map { Tile(suit, it) }
    } + (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)

    private fun winningRound(): RoundStateMachine = RoundStateMachine(
        TestTableBuilder.create(hands = mapOf(0 to winningHand()), drawn = 0 to Tile(Suit.MAN, 5), dealer = 0)
    )

    private fun ready(prepared: RoundPreparation): RoundChange {
        assertTrue(prepared is RoundPreparation.Ready, "$prepared")
        return (prepared as RoundPreparation.Ready).change
    }

    private fun fingerprint(round: RoundStateMachine): String {
        val s = round.readState()
        return listOf((0..3).map { PlayerView.fromGameState(s, it, emptyList()) },
            s.wall, s.deadWall, s.wallPointer, s.rinshanPointer,
            round.responseWindow, round.version, round.result).toString()
    }

    @Test fun `preparing a full deal changes neither authority nor version until installation`() {
        val round = RoundStateMachine(GameState())
        val processor = RoundCommandProcessor(random = Random(41))
        val before = fingerprint(round)
        val change = ready(processor.prepare(round, GameAction.StartStacking()))
        assertEquals(before, fingerprint(round))
        assertEquals(14, change.state.players[0].allClosedTiles.size)
        StateInvariants.assertValid(change.state)
        round.install(change)
        assertEquals(14, round.readState().players[0].allClosedTiles.size)
        assertEquals(1L, round.version)
        assertNull(round.result)
    }

    @Test fun `discarded candidate and failed audit leave an untouched round`() {
        val round = winningRound()
        val processor = RoundCommandProcessor()
        val before = fingerprint(round)
        val change = ready(processor.prepare(round, GameAction.DeclareTsumo(0)))
        assertEquals(GamePhase.ROUND_END, change.state.phase)
        // A broken external candidate must fail audit before anyone installs it.
        val broken = change.state.deepCopy()
        broken.players[0].closedTiles.removeAt(0)
        assertThrows(IllegalStateException::class.java) { StateInvariants.assertValid(broken) }
        assertEquals(before, fingerprint(round))
    }

    @Test fun `single round ends without progressing match and can never restart`() {
        val round = winningRound()
        val processor = RoundCommandProcessor()
        val change = ready(processor.prepare(round, GameAction.DeclareTsumo(0)))
        StateInvariants.assertValid(change.state)
        round.install(change)
        val result = round.result!!
        assertEquals(GamePhase.ROUND_END, round.readState().phase)
        assertEquals(round.roundId, result.roundId)
        assertEquals(result.finalScores, result.startingScores.zip(result.totalDeltas) { start, delta -> start + delta })
        assertTrue(result.events.any { it is GameEvent.TsumoWin })
        assertTrue(result.events.none { it is GameEvent.MatchEnded })
        val ended = fingerprint(round)
        for (action in listOf(GameAction.StartStacking(), GameAction.DeclareAgariYame(0), GameAction.DeclareTsumo(0))) {
            assertTrue(processor.prepare(round, action) is RoundPreparation.Rejected, "$action")
            assertEquals(ended, fingerprint(round))
        }
    }

    @Test fun `read snapshots cannot modify the authoritative hand ledger or wall`() {
        val round = winningRound()
        val before = fingerprint(round)
        val exposed = round.readState()
        exposed.scores[0] = -999
        exposed.players[0].closedTiles.clear()
        exposed.wall.clear()
        assertEquals(before, fingerprint(round))
    }

    @Test fun `settlement event arrays are detached on install and on every result read`() {
        val round = winningRound()
        val change = ready(RoundCommandProcessor().prepare(round, GameAction.DeclareTsumo(0)))
        StateInvariants.assertValid(change.state)
        round.install(change)
        val original = round.result!!.events.filterIsInstance<GameEvent.ScoreSettled>().single().newScores.toList()
        change.events.filterIsInstance<GameEvent.ScoreSettled>().single().newScores[0] = -123
        assertEquals(original, round.result!!.events.filterIsInstance<GameEvent.ScoreSettled>().single().newScores.toList())
        round.result!!.events.filterIsInstance<GameEvent.ScoreSettled>().single().newScores[0] = -456
        assertEquals(original, round.result!!.events.filterIsInstance<GameEvent.ScoreSettled>().single().newScores.toList())
        assertEquals(original, round.readState().scores.toList())
    }

    @Test fun `invalid player action has no candidate or authority side effects`() {
        val round = winningRound()
        val before = fingerprint(round)
        assertTrue(RoundCommandProcessor().prepare(round, GameAction.DiscardTile(1, Tile(Suit.WIND, 4))) is RoundPreparation.Rejected)
        assertEquals(before, fingerprint(round))
    }

    @Test fun `response preparation keeps original window and furiten unchanged`() {
        fun hand(copy: Int) = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
            (2..4).map { Tile(suit, it, copy) }
        } + (6..8).map { Tile(Suit.SOU, it, copy) } + Tile(Suit.MAN, 5, copy)
        val called = Tile(Suit.MAN, 5, 3)
        val round = RoundStateMachine(TestTableBuilder.create(hands = mapOf(0 to hand(0), 3 to hand(1)), drawn = 2 to called))
        val processor = RoundCommandProcessor()
        val discard = ready(processor.prepare(round, GameAction.DiscardTile(2, called)))
        StateInvariants.assertValid(discard.state)
        round.install(discard)
        assertNotNull(round.responseWindow)
        val before = fingerprint(round)
        val passing = ready(processor.prepare(round, GameAction.Pass(3)))
        assertEquals(before, fingerprint(round))
        assertTrue(passing.state.sameTurnFuriten[3])
        assertFalse(round.readState().sameTurnFuriten[3])
        round.install(passing)
        assertTrue(round.responseWindow!!.hasResponded(3))
        assertFalse(round.responseWindow!!.hasResponded(0))
        // Even a mutable compatibility ResponseWindow returned to callers is detached.
        round.responseWindow!!.responses.clear()
        assertTrue(round.responseWindow!!.hasResponded(3))
    }
}

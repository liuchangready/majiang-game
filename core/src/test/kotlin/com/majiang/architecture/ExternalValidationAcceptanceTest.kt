package com.majiang.architecture

import com.majiang.application.ApplicationCoordinator
import com.majiang.application.RoundResultValidator
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.round.*
import com.majiang.state.GameAction
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ExternalValidationAcceptanceTest {
    private fun fixture(): Pair<ApplicationCoordinator, RoundChange> {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
            (2..4).map { Tile(suit, it) }
        } + (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val c = ApplicationCoordinator(initialState = TestTableBuilder.create(
            hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5), dealer = 0), autoPlayAi = false)
        val change = (RoundCommandProcessor().prepare(c.activeRound!!, GameAction.DeclareTsumo(0)) as RoundPreparation.Ready).change
        return c to change
    }

    @Test fun `external result validator rejects already consumed results without installing anything`() {
        val (c, change) = fixture()
        val round = c.activeRound!!
        assertDoesNotThrow { RoundResultValidator.validate(c.session, round, change, emptySet()) }
        assertThrows(IllegalStateException::class.java) {
            RoundResultValidator.validate(c.session, round, change, setOf(change.result!!.resultId))
        }
        assertEquals(0L, round.version)
        assertNull(round.result)
    }

    @Test fun `external result validator rejects a balanced result that disagrees with candidate ledger`() {
        val (c, change) = fixture()
        val incorrect = change.state.deepCopy()
        incorrect.scores[0] += 100
        incorrect.scores[1] -= 100
        val forged = RoundChange(change.roundId, change.sourceVersion, incorrect,
            change.responseWindow, change.result, change.events)
        assertThrows(IllegalStateException::class.java) {
            RoundResultValidator.validate(c.session, c.activeRound!!, forged, emptySet())
        }
        assertEquals(List(4) { 25000 }, c.queryView(0).scores)
    }

    @Test fun `external result validator rejects a terminal result attached to a nonterminal state`() {
        val (c, change) = fixture()
        val nonterminal = change.state.deepCopy()
        nonterminal.phase = com.majiang.state.GamePhase.DISCARD
        val forged = RoundChange(change.roundId, change.sourceVersion, nonterminal,
            change.responseWindow, change.result, change.events)
        assertThrows(IllegalStateException::class.java) {
            RoundResultValidator.validate(c.session, c.activeRound!!, forged, emptySet())
        }
    }
}

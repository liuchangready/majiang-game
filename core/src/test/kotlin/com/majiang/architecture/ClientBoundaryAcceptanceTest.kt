package com.majiang.architecture

import com.majiang.application.*
import com.majiang.client.LocalPlayerEndpoint
import com.majiang.event.GameEvent
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.SessionUpdate
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.random.Random

class ClientBoundaryAcceptanceTest {
    private fun coordinator(state: GameState = GameState()) = ApplicationCoordinator(
        initialState = state, autoPlayAi = false, autoRiichiDiscard = false, random = Random(51)
    ).also { it.initializeMatch(List(4) { SeatController.RemoteHuman(false) }) }

    private fun accepted(result: ActionResult) {
        assertTrue(result is ActionResult.Accepted, "$result")
    }

    private fun choice(c: ApplicationCoordinator, seat: Int, request: String,
                       predicate: (GameAction) -> Boolean = { it is GameAction.DiscardTile }): ActionChoice {
        val d = c.queryDecision(seat)
        val o = d.options.first { predicate(it.action) }
        return ActionChoice(request, d.roundId, d.decisionId, d.controlEpoch, o.optionId)
    }

    @Test fun `two clients share one authority and both receive committed updates`() {
        val c = coordinator()
        val a = LocalPlayerEndpoint(0, c)
        val b = LocalPlayerEndpoint(1, c)
        val updatesA = mutableListOf<SessionUpdate>()
        val updatesB = mutableListOf<SessionUpdate>()
        a.subscribe { updatesA.add(it) }
        b.subscribe { updatesB.add(it) }
        accepted(a.start())
        assertEquals(a.view().scores, b.view().scores)
        assertEquals(14, a.view().myAllClosedTiles.size)
        assertEquals(13, b.view().myAllClosedTiles.size)
        updatesA.clear()
        updatesB.clear()
        accepted(a.submitChoice(choice(c, 0, "discard-1")))
        assertEquals(1, updatesA.size)
        assertEquals(1, updatesB.size)
        assertEquals(a.view().discards, b.view().discards)
        assertEquals(0, updatesA.single().view.viewerSeat)
        assertEquals(1, updatesB.single().view.viewerSeat)
    }

    @Test fun `closing one client and failing listeners cannot close or block another client`() {
        val c = coordinator()
        val a = LocalPlayerEndpoint(0, c)
        val b = LocalPlayerEndpoint(1, c)
        val updates = mutableListOf<SessionUpdate>()
        a.subscribe { error("broken UI observer") }
        b.subscribe { updates.add(it) }
        a.close()
        accepted(b.start())
        assertEquals(13, b.view().myAllClosedTiles.size)
        assertTrue(updates.isNotEmpty())
        assertTrue(a.start() is ActionResult.Rejected)
        assertTrue(a.legalActions().isEmpty())
        assertNotNull(c.activeRound)
    }

    @Test fun `same choice request is idempotent and conflicting reuse is rejected`() {
        val c = coordinator()
        accepted(c.start())
        var broadcasts = 0
        c.subscribe { broadcasts++ }
        val command = choice(c, 0, "same-request")
        accepted(c.submitChoice(0, command))
        val view = c.queryView(0)
        val version = c.activeRound!!.version
        accepted(c.submitChoice(0, command))
        assertEquals(view, c.queryView(0))
        assertEquals(version, c.activeRound!!.version)
        assertEquals(1, broadcasts)
        assertTrue(c.submitChoice(0, command.copy(optionId = "forged-option")) is ActionResult.Rejected)
        assertEquals(view, c.queryView(0))
        assertEquals(1, broadcasts)
    }

    @Test fun `bound endpoint cannot submit another seat decision or trusted system action`() {
        val c = coordinator()
        accepted(c.start())
        val b = LocalPlayerEndpoint(1, c)
        val before = b.view()
        assertTrue(b.submitChoice(choice(c, 0, "wrong-seat")) is ActionResult.Rejected)
        assertTrue(b.submit(GameAction.StartStacking()) is ActionResult.Rejected)
        assertEquals(before, b.view())
    }

    @Test fun `old controller decision is invalid after control transfer even to another human`() {
        val c = coordinator()
        accepted(c.start())
        val command = choice(c, 0, "old-controller")
        accepted(c.setSeatController(0, SeatController.LocalHuman(false)))
        val current = c.queryDecision(0)
        assertNotEquals(command.controlEpoch, current.controlEpoch)
        val before = c.queryView(0)
        assertTrue(c.submitChoice(0, command) is ActionResult.Rejected)
        assertEquals(before, c.queryView(0))
        accepted(c.submitChoice(0, choice(c, 0, "current-controller")))
    }

    @Test fun `round decision cannot be submitted with a forged session scope`() {
        val c = coordinator()
        accepted(c.start())
        val command = choice(c, 0, "wrong-scope")
        val before = c.queryView(0)
        assertTrue(c.submitChoice(0, command.copy(roundId = null)) is ActionResult.Rejected)
        assertEquals(before, c.queryView(0))
        accepted(c.submitChoice(0, command.copy(requestId = "real-scope")))
    }

    @Test fun `client cannot corrupt registered options through returned collections`() {
        val c = coordinator()
        accepted(c.start())
        val decision = c.queryDecision(0)
        val command = choice(c, 0, "unmodified-authority")
        val originalOptions = decision.options.toList()
        try {
            (decision.options as MutableList<ActionOption>).clear()
        } catch (_: UnsupportedOperationException) {
            // Immutable and detached mutable snapshots are both acceptable at this boundary.
        }
        assertEquals(originalOptions, c.queryDecision(0).options)
        accepted(c.submitChoice(0, command))
    }

    @Test fun `mutating returned settlement cannot poison a replayed receipt`() {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
            (2..4).map { Tile(suit, it) }
        } + (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val c = coordinator(TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5), dealer = 0))
        val command = choice(c, 0, "tsumo-replay") { it is GameAction.DeclareTsumo }
        val result = c.submitChoice(0, command)
        accepted(result)
        val event = (result as ActionResult.Accepted).events.filterIsInstance<GameEvent.ScoreSettled>().single()
        val scores = event.newScores.toList()
        event.newScores[0] = -999
        val replay = c.submitChoice(0, command)
        accepted(replay)
        assertEquals(scores, (replay as ActionResult.Accepted).events.filterIsInstance<GameEvent.ScoreSettled>().single().newScores.toList())
        assertEquals(scores, c.queryView(0).scores)
    }

    @Test fun `another seat response does not invalidate outstanding legal decision`() {
        fun hand(copy: Int) = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
            (2..4).map { Tile(suit, it, copy) }
        } + (6..8).map { Tile(Suit.SOU, it, copy) } + Tile(Suit.MAN, 5, copy)
        val called = Tile(Suit.MAN, 5, 3)
        val c = coordinator(TestTableBuilder.create(hands = mapOf(0 to hand(0), 3 to hand(1)), drawn = 2 to called))
        accepted(c.submitPlayerAction(2, GameAction.DiscardTile(2, called)))
        val ron = choice(c, 0, "ron-old-snapshot") { it is GameAction.DeclareRon }
        val pass = choice(c, 3, "pass") { it is GameAction.Pass }
        val endpoint = LocalPlayerEndpoint(3, c)
        val updates = mutableListOf<SessionUpdate>()
        endpoint.subscribe { updates.add(it) }
        updates.clear()
        accepted(c.submitChoice(3, pass))
        assertEquals(1, updates.size, "A recorded response updates decision UI even with no tile event")
        assertTrue(updates.single().view.legalActions.isEmpty())
        accepted(c.submitChoice(0, ron))
        val before = c.queryView(0)
        assertTrue(c.submitChoice(0, ron.copy(requestId = "repeated-seat-response")) is ActionResult.Rejected)
        assertEquals(before, c.queryView(0))
    }

    @Test fun `old round choice cannot operate on a new dealer repeat round`() {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
            (2..4).map { Tile(suit, it) }
        } + (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val c = coordinator(TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5), dealer = 0))
        val oldDiscard = choice(c, 0, "delayed-discard")
        accepted(c.submitPlayerAction(0, GameAction.DeclareTsumo(0)))
        accepted(c.nextRound())
        assertNotEquals(oldDiscard.roundId, c.queryDecision(0).roundId)
        val before = c.queryView(0)
        assertTrue(c.submitChoice(0, oldDiscard) is ActionResult.Rejected)
        assertEquals(before, c.queryView(0))
    }
}

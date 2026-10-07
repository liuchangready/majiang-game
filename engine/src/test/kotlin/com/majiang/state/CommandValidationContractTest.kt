package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.CheatTechnique
import com.majiang.event.GameEventBus
import com.majiang.model.TilePool
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class CommandValidationContractTest {
    private fun withFreshTable(check: (GameState, GameEventBus, ApplicationCoordinator) -> Unit) {
        val state = GameState()
        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = true)
        engine.initializeMatch(0)
        engine.handleAction(GameAction.StartStacking())
        val liveState = engine.activeRound!!.readState()
        check(liveState, bus, engine)
    }

    private fun rejectWithoutMutation(s: GameState, bus: GameEventBus, engine: ApplicationCoordinator, action: GameAction) {
        val hands = s.players.map { it.allClosedTiles.toList() }
        val rivers = s.players.map { it.discards.toList() }
        val melds = s.players.map { it.melds.toList() }
        val wall = s.wall.toList()
        val pointer = s.wallPointer
        val phase = s.phase
        val player = s.currentPlayer
        val scores = s.scores.toList()
        val deposits = s.riichiSticks
        val declared = s.riichiDeclared.toList()
        val history = bus.getHistory()
        val result = assertDoesNotThrow<ActionResult> { engine.handleAction(action) }
        assertTrue(result is ActionResult.Rejected, "Expected rejection of $action, got $result")
        val post = engine.activeRound!!.readState()
        assertEquals(hands, post.players.map { it.allClosedTiles.toList() })
        assertEquals(rivers, post.players.map { it.discards.toList() })
        assertEquals(melds, post.players.map { it.melds.toList() })
        assertEquals(wall, post.wall)
        assertEquals(pointer, post.wallPointer)
        assertEquals(phase, post.phase)
        assertEquals(player, post.currentPlayer)
        assertEquals(scores, post.scores.toList())
        assertEquals(deposits, post.riichiSticks)
        assertEquals(declared, post.riichiDeclared.toList())
        assertEquals(history, bus.getHistory(), "Rejected commands cannot publish domain events")
    }

    @Test
    fun `all nonacting seats cannot discard even their own physical tile`() = withFreshTable { s, bus, fsm ->
        for (seat in 1..3) rejectWithoutMutation(s, bus, fsm,
            GameAction.DiscardTile(seat, s.players[seat].closedTiles.first()))
    }

    @Test
    fun `negative and oversized seat indices return rejection rather than crash`() = withFreshTable { s, bus, fsm ->
        val tile = s.players[0].closedTiles.first()
        for (seat in listOf(-1, 4, Int.MIN_VALUE, Int.MAX_VALUE)) {
            rejectWithoutMutation(s, bus, fsm, GameAction.DrawTile(seat))
            rejectWithoutMutation(s, bus, fsm, GameAction.DiscardTile(seat, tile))
            rejectWithoutMutation(s, bus, fsm, GameAction.Pass(seat))
            rejectWithoutMutation(s, bus, fsm, GameAction.DeclareTsumo(seat))
            rejectWithoutMutation(s, bus, fsm, GameAction.DeclareRiichi(seat, tile))
        }
    }

    @Test
    fun `cannot discard another player's physical copy`() = withFreshTable { s, bus, fsm ->
        rejectWithoutMutation(s, bus, fsm, GameAction.DiscardTile(0, s.players[1].closedTiles.first()))
    }

    @Test
    fun `invalid riichi tile never deducts deposit first`() = withFreshTable { s, bus, fsm ->
        rejectWithoutMutation(s, bus, fsm, GameAction.DeclareRiichi(0, s.wall[s.wallPointer]))
    }

    @Test
    fun `out of turn win and abortive draw cannot end the round`() = withFreshTable { s, bus, fsm ->
        rejectWithoutMutation(s, bus, fsm, GameAction.DeclareTsumo(1))
        rejectWithoutMutation(s, bus, fsm, GameAction.DeclareRon(1))
        rejectWithoutMutation(s, bus, fsm, GameAction.DeclareAbortiveDraw(1))
    }

    @Test
    fun `unsupported cheat is explicitly rejected instead of silent success`() = withFreshTable { s, bus, fsm ->
        // Complete cheat wiring is deliberately excluded from this delivery.
        rejectWithoutMutation(s, bus, fsm, GameAction.UseCheatTechnique(0, CheatTechnique.SLEEVE_SWAP))
    }

    @Test
    fun `legal discard actions identify exactly the acting player's physical hand`() = withFreshTable { s, bus, fsm ->
        val before = bus.getHistory()
        val legal = fsm.legalActions(0).filterIsInstance<GameAction.DiscardTile>()
        assertEquals(s.players[0].allClosedTiles.toSet(), legal.map { it.tile }.toSet())
        assertTrue(legal.all { it.player == 0 })
        for (seat in 1..3) assertTrue(fsm.legalActions(seat).isEmpty())
        assertEquals(before, bus.getHistory(), "Querying legal actions is read-only")
        assertEquals(136, TilePool.buildStandardSet().size)
    }
}

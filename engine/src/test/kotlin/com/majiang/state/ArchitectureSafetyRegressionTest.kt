package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEventBus
import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Codex-owned acceptance regressions. Expected RED before architecture V2 integration. */
class ArchitectureSafetyRegressionTest {
    private data class Table(val state: GameState, val bus: GameEventBus, val engine: ApplicationCoordinator)

    private fun started(): Table {
        val state = GameState()
        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = true)
        engine.initializeMatch(0)
        engine.handleAction(GameAction.StartStacking())
        return Table(engine.activeRound!!.readState(), bus, engine)
    }

    // Independent physical enumeration for the pre-V2 representation (no kan in these fixtures).
    private fun physicalTiles(s: GameState): List<Tile> = buildList {
        addAll(s.wall.subList(s.wallPointer, s.wallEndExclusive))
        addAll(s.deadWall.drop(s.rinshanPointer))
        addAll(s.deadWallReplacements)
        addAll(s.sleeveTiles.flatten())
        s.players.forEach { p ->
            addAll(p.closedTiles)
            p.drawnTile?.let { add(it) }
            addAll(p.discards)
            p.melds.forEach { addAll(it.tiles) }
        }
    }

    private fun fingerprint(t: Table): String = with(t.engine.activeRound?.readState() ?: t.engine.lastEndedRoundSnapshot!!) {
        listOf(
            phase, currentPlayer, wallPointer, rinshanPointer, turnNumber,
            wall.toList(), deadWall.toList(), sleeveTiles.toList(),
            players.map { listOf(it.closedTiles.toList(), it.drawnTile, it.melds.toList(),
                it.discards.toList(), it.riichiDeclared, it.riichiTurn, it.ippatsu) },
            scores.toList(), riichiSticks, honbaSticks, riichiDeclared.toList(),
            riichiTurn.toList(), ippatsuActive.toList(), lastDiscard, lastDiscardPlayer,
            dealerIndex, roundWind, roundNumber, isFirstUninterruptedGo,
            totalKanCount, playerKanCounts.toList(), doraIndicators.toList(), uraDoraIndicators.toList(),
            Zone.values().flatMap { z -> (-1..3).map { p -> tilePool.getByZone(z, p) } },
            t.bus.getHistory().toList()
        ).toString()
    }

    @Test
    fun `baseline normal deal contains all 136 unique physical tiles`() {
        val t = started()
        assertEquals(TilePool.buildStandardSet().toSet(), physicalTiles(t.state).toSet())
        assertEquals(136, physicalTiles(t.state).size)
    }

    @Test
    fun `audit detects a tile removed from the actual hand`() {
        val t = started()
        t.state.players[0].closedTiles.removeAt(0)
        assertEquals(135, physicalTiles(t.state).size)
        assertThrows(RuntimeException::class.java) { t.state.tilePool.assertConservation() }
    }

    @Test
    fun `audit detects a tile duplicated across actual hands`() {
        val t = started()
        t.state.players[1].closedTiles.add(t.state.players[0].closedTiles.first())
        assertEquals(137, physicalTiles(t.state).size)
        assertThrows(RuntimeException::class.java) { t.state.tilePool.assertConservation() }
    }

    @Test
    fun `pool records the actual owner of every dealt tile`() {
        val t = started()
        for (p in 0..3) {
            assertEquals(t.state.players[p].allClosedTiles.toSet(),
                t.state.tilePool.getByZone(Zone.HAND, p).toSet(), "owner=$p")
        }
    }

    @Test
    fun `duplicate draw cannot overwrite dealer drawn tile`() {
        val t = started()
        val before = fingerprint(t)
        assertDoesNotThrow { t.engine.handleAction(GameAction.DrawTile(0)) }
        assertEquals(before, fingerprint(t), "Rejected draw must not change state or emit events")
        assertEquals(136, physicalTiles(t.state).size)
    }

    @Test
    fun `pass outside a response window cannot advance play`() {
        val t = started()
        val before = fingerprint(t)
        assertDoesNotThrow { t.engine.handleAction(GameAction.Pass(0)) }
        assertEquals(before, fingerprint(t))
    }

    @Test
    fun `starting a new round during active play must be rejected`() {
        val t = started()
        val before = fingerprint(t)
        assertDoesNotThrow { t.engine.handleAction(GameAction.StartStacking()) }
        assertEquals(before, fingerprint(t))
    }

    @Test
    fun `discarding a wall tile is rejected before any mutation`() {
        val t = started()
        val tile = t.state.wall[t.state.wallPointer]
        val before = fingerprint(t)
        assertDoesNotThrow { t.engine.handleAction(GameAction.DiscardTile(0, tile)) }
        assertEquals(before, fingerprint(t))
    }

    /** Complete, deterministic, physically valid table; no initializeAll(HAND) shortcut. */
    private fun winningTable(): Table {
        val state = GameState()
        val bus = GameEventBus()
        val remaining = TilePool.buildStandardSet().toMutableList()
        fun take(suit: Suit, rank: Int): Tile = remaining.first { it.suit == suit && it.rank == rank }
            .also { remaining.remove(it) }
        val winning = listOf(
            take(Suit.MAN, 2), take(Suit.MAN, 3), take(Suit.MAN, 4),
            take(Suit.MAN, 3), take(Suit.MAN, 4), take(Suit.MAN, 5),
            take(Suit.PIN, 4), take(Suit.PIN, 5), take(Suit.PIN, 6),
            take(Suit.SOU, 6), take(Suit.SOU, 7), take(Suit.SOU, 8),
            take(Suit.PIN, 5), take(Suit.PIN, 5)
        )
        state.players[1].closedTiles.addAll(winning.dropLast(1))
        state.players[1].drawnTile = winning.last()
        winning.forEach { state.tilePool.transfer(it, Zone.UNASSIGNED, Zone.HAND, 1) }
        for (p in listOf(0, 2, 3)) repeat(13) {
            val tile = remaining.removeAt(0)
            state.players[p].closedTiles.add(tile)
            state.tilePool.transfer(tile, Zone.UNASSIGNED, Zone.HAND, p)
        }
        repeat(14) {
            val tile = remaining.removeAt(0)
            state.deadWall.add(tile)
            state.tilePool.transfer(tile, Zone.UNASSIGNED, Zone.DEAD_WALL)
        }
        state.wall.addAll(remaining)
        state.wallEndExclusive = state.wall.size
        remaining.forEach { state.tilePool.transfer(it, Zone.UNASSIGNED, Zone.WALL) }
        state.phase = GamePhase.DISCARD
        state.currentPlayer = 1
        state.turnNumber = 10
        state.isFirstUninterruptedGo = false
        state.honbaSticks = 2
        state.riichiSticks = 3
        assertEquals(136, physicalTiles(state).size)
        assertEquals(136, physicalTiles(state).toSet().size)
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = true)
        return Table(state, bus, engine)
    }

    @Test
    fun `settlement conserves player balances plus table deposits`() {
        val t = winningTable()
        val before = t.state.scores.sum() + t.state.riichiSticks * 1000
        t.engine.handleAction(GameAction.DeclareTsumo(1))
        val post = t.engine.activeRound?.readState() ?: t.engine.lastEndedRoundSnapshot!!
        assertEquals(GamePhase.ROUND_END, post.phase)
        assertEquals(before, post.scores.sum() + post.riichiSticks * 1000)
    }

    @Test
    fun `winning settlement consumes table deposits exactly once`() {
        val t = winningTable()
        t.engine.handleAction(GameAction.DeclareTsumo(1))
        val post = t.engine.activeRound?.readState() ?: t.engine.lastEndedRoundSnapshot!!
        assertEquals(GamePhase.ROUND_END, post.phase)
        assertEquals(0, post.riichiSticks)
    }

    @Test
    fun `repeated win after round end cannot pay again`() {
        val t = winningTable()
        t.engine.handleAction(GameAction.DeclareTsumo(1))
        val before = fingerprint(t)
        assertDoesNotThrow { t.engine.handleAction(GameAction.DeclareTsumo(1)) }
        assertEquals(before, fingerprint(t))
    }
}

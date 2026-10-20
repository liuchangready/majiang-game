package com.majiang.testing

import com.majiang.model.*
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.assertEquals

/** Test-owned complete physical table constructor; never used by production. */
object TestTableBuilder {
    fun create(
        hands: Map<Int, List<Tile>> = emptyMap(),
        drawn: Pair<Int, Tile>? = null,
        melds: Map<Int, List<Meld>> = emptyMap(),
        discards: Map<Int, List<Tile>> = emptyMap(),
        sleeves: Map<Int, List<Tile>> = emptyMap(),
        wallPrefix: List<Tile> = emptyList(),
        deadWallPrefix: List<Tile> = emptyList(),
        phase: GamePhase = GamePhase.DISCARD,
        currentPlayer: Int = drawn?.first ?: 0,
        dealer: Int = 0
    ): GameState {
        val s = GameState()
        val remaining = TilePool.buildStandardSet().toMutableList()
        fun reserve(tile: Tile) {
            require(remaining.remove(tile)) { "Duplicate or illegal fixture tile: $tile" }
        }
        hands.values.flatten().forEach(::reserve)
        drawn?.second?.let(::reserve)
        melds.values.flatten().flatMap { it.tiles }.forEach(::reserve)
        discards.values.flatten().forEach(::reserve)
        sleeves.values.flatten().forEach(::reserve)
        wallPrefix.forEach(::reserve)
        deadWallPrefix.forEach(::reserve)
        s.phase = phase
        s.currentPlayer = currentPlayer
        s.dealerIndex = dealer
        s.turnNumber = 12
        s.isFirstUninterruptedGo = false
        for (seat in 0..3) {
            val hand = s.players[seat]
            hand.closedTiles.addAll(hands[seat].orEmpty())
            hand.melds.addAll(melds[seat].orEmpty())
            hand.discards.addAll(discards[seat].orEmpty())
            hand.discardHistory.addAll(discards[seat].orEmpty())
            s.sleeveTiles[seat].addAll(sleeves[seat].orEmpty())
            if (drawn?.first == seat) hand.drawnTile = drawn.second
            val wants14 = seat == currentPlayer && phase in setOf(GamePhase.DISCARD, GamePhase.CHEAT_WINDOW)
            val target = (if (wants14) 14 else 13) - hand.melds.size * 3 -
                s.sleeveTiles[seat].size - (if (hand.drawnTile == null) 0 else 1)
            require(hand.closedTiles.size <= target) { "Too many fixture tiles for seat $seat" }
            while (hand.closedTiles.size < target) hand.closedTiles.add(remaining.removeAt(0))
            hand.allClosedTiles.forEach { s.tilePool.transfer(it, Zone.UNASSIGNED, Zone.HAND, seat) }
            hand.melds.flatMap { it.tiles }.forEach { s.tilePool.transfer(it, Zone.UNASSIGNED, Zone.MELD, seat) }
            hand.discards.forEach { s.tilePool.transfer(it, Zone.UNASSIGNED, Zone.DISCARD, seat) }
            s.sleeveTiles[seat].forEach { s.tilePool.transfer(it, Zone.UNASSIGNED, Zone.SLEEVE, seat) }
        }
        require(deadWallPrefix.size <= 14)
        s.deadWall.addAll(deadWallPrefix)
        deadWallPrefix.forEach { s.tilePool.transfer(it, Zone.UNASSIGNED, Zone.DEAD_WALL) }
        repeat(14 - deadWallPrefix.size) {
            val tile = remaining.removeAt(0)
            s.deadWall.add(tile)
            s.tilePool.transfer(tile, Zone.UNASSIGNED, Zone.DEAD_WALL)
        }
        s.wall.addAll(wallPrefix)
        s.wall.addAll(remaining)
        s.wallEndExclusive = s.wall.size
        s.wall.forEach { s.tilePool.transfer(it, Zone.UNASSIGNED, Zone.WALL) }
        val last = discards.entries.singleOrNull { it.value.isNotEmpty() }
        if (last != null) {
            s.lastDiscard = last.value.last()
            s.lastDiscardPlayer = last.key
        }
        assertPhysical(s)
        StateInvariants.assertValid(s)
        return s
    }

    /** Independent of TilePool and StateInvariants: catches a shared production audit mistake. */
    fun actualTiles(s: GameState): List<Tile> = buildList {
        // 活牌山可能包含 wallPointer 之后被跳过、解锁后重新可摸的“洞”。
        // 以 TilePool 的 WALL 归属为准，避免把已摸走的旧槽位重复计入。
        addAll(s.wall.filter { s.tilePool.getLocation(it) == TileLocation.WALL })
        addAll(s.deadWall.drop(s.rinshanPointer))
        addAll(s.deadWallReplacements)
        addAll(s.sleeveTiles.flatten())
        for (hand in s.players) {
            addAll(hand.allClosedTiles)
            addAll(hand.discards)
            addAll(hand.melds.flatMap { it.tiles })
        }
    }

    fun assertPhysical(s: GameState) {
        val actual = actualTiles(s)
        assertEquals(136, actual.size, "Actual container count")
        assertEquals(TilePool.buildStandardSet().toSet(), actual.toSet(), "Exact physical identity set")
        for (seat in 0..3) {
            assertEquals(s.players[seat].allClosedTiles.toSet(), s.tilePool.getByZone(Zone.HAND, seat).toSet())
            assertEquals(s.players[seat].melds.flatMap { it.tiles }.toSet(), s.tilePool.getByZone(Zone.MELD, seat).toSet())
            assertEquals(s.players[seat].discards.toSet(), s.tilePool.getByZone(Zone.DISCARD, seat).toSet())
            assertEquals(s.sleeveTiles[seat].toSet(), s.tilePool.getByZone(Zone.SLEEVE, seat).toSet())
        }
    }
}

package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.*
import com.majiang.model.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiFlowContractTest {
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c -> val r = c.digitToInt(); Tile(suit, r, copies.getOrDefault(r, 0).also { copies[r] = it + 1 }) }
    }
    private fun waiting() = tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") +
        tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")
    private val fourth = Tile(Suit.DRAGON, 1, 3)
    private val miss = Tile(Suit.WIND, 4)
    private val win = Tile(Suit.SOU, 4)
    private fun markRiichi(s: GameState, seat: Int) {
        s.riichiDeclared[seat] = true; s.players[seat].riichiDeclared = true
        s.scores[seat] -= 1000; s.riichiSticks++
    }
    private data class Fixture(val e: ApplicationCoordinator, val bus: GameEventBus) {
        val s: GameState get() = e.activeRound?.readState() ?: e.lastEndedRoundSnapshot!!
    }
    private fun fixture(draw: Tile, seat: Int = 0, riichi: Boolean = true,
        closed: List<Tile> = waiting(), rinshan: List<Tile> = emptyList(), auto: Boolean = true,
        configure: (GameState) -> Unit = {}): Fixture {
        val s = TestTableBuilder.create(hands = mapOf(seat to closed), wallPrefix = listOf(draw),
            deadWallPrefix = rinshan, phase = GamePhase.DRAW, currentPlayer = seat)
        if (riichi) markRiichi(s, seat)
        configure(s)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false, autoRiichiDiscard = auto)
        e.initializeMatch(seat)
        return Fixture(e, bus)
    }
    private fun submit(f: Fixture, action: GameAction): List<GameEvent> {
        val result = f.e.handleAction(action)
        assertTrue(result is ActionResult.Accepted, "$action -> $result")
        TestTableBuilder.assertPhysical(f.s)
        StateInvariants.assertValid(f.s)
        assertEquals(100000, f.s.scores.sum() + f.s.riichiSticks * 1000)
        return (result as ActionResult.Accepted).events
    }
    private fun draw(f: Fixture, seat: Int = 0) = submit(f, GameAction.DrawTile(seat))

    @Test fun `ordinary riichi draw is automatically discarded once in original command events`() {
        val f = fixture(miss)
        val events = draw(f)
        val discarded = events.filterIsInstance<GameEvent.TileDiscarded>().single { it.player == 0 }
        assertEquals(miss, discarded.tile)
        assertTrue(discarded.isTsumogiri)
        assertFalse(discarded.isRiichiDiscard)
        assertEquals(waiting(), f.s.players[0].closedTiles)
        assertNull(f.s.players[0].drawnTile)
        assertEquals(1, f.s.players[0].discardHistory.count { it == miss })
        assertEquals(events, f.bus.getHistory())
    }

    @Test fun `winning riichi draw pauses and explicit tsumo settles exactly once`() {
        val f = fixture(win)
        val events = draw(f)
        assertEquals(win, f.s.players[0].drawnTile)
        assertTrue(GameAction.DeclareTsumo(0) in f.e.legalActions(0))
        assertTrue(events.none { it is GameEvent.TileDiscarded || it is GameEvent.TsumoWin })
        submit(f, GameAction.DeclareTsumo(0))
        assertEquals(GamePhase.ROUND_END, f.s.phase)
        assertEquals(1, f.bus.getHistory().filterIsInstance<GameEvent.TsumoWin>().size)
        assertTrue(f.e.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Rejected)
    }

    @Test fun `human can explicitly decline tsumo by discarding the drawn tile`() {
        val f = fixture(win)
        draw(f)
        submit(f, GameAction.DiscardTile(0, win))
        assertTrue(f.bus.getHistory().none { it is GameEvent.TsumoWin })
        assertTrue(win in f.s.players[0].discardHistory)
    }

    @Test fun `non riichi player retains manual turn and opt out supports stepwise simulations`() {
        for (f in listOf(fixture(miss, riichi = false), fixture(miss, auto = false))) {
            val events = draw(f)
            assertEquals(miss, f.s.players[0].drawnTile)
            assertTrue(events.none { it is GameEvent.TileDiscarded })
        }
    }

    @Test fun `automatic riichi discard follows the actual human seat`() {
        val f = fixture(miss, seat = 2)
        val events = draw(f, 2)
        assertEquals(miss, events.filterIsInstance<GameEvent.TileDiscarded>().single { it.player == 2 }.tile)
        assertEquals(waiting(), f.s.players[2].closedTiles)
    }

    @Test fun `legal kan pauses and can be declined by manual tsumogiri`() {
        val f = fixture(fourth)
        val events = draw(f)
        val kan = f.e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        assertEquals(MeldType.CLOSED_KAN, kan.kanType)
        assertEquals((0..3).map { Tile(Suit.DRAGON, 1, it) }.toSet(), kan.tiles.toSet())
        assertEquals(fourth, f.s.players[0].drawnTile)
        assertTrue(events.none { it is GameEvent.TileDiscarded || it is GameEvent.KanDeclared })
        submit(f, GameAction.DiscardTile(0, fourth))
        assertTrue(f.s.players[0].melds.isEmpty())
        assertTrue(fourth in f.s.players[0].discardHistory)
    }

    @Test fun `kan followed by nonwinning rinshan automatically discards the replacement tile`() {
        val f = fixture(fourth, rinshan = listOf(miss)) { it.ippatsuActive.fill(true) }
        draw(f)
        val kan = f.e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        val events = submit(f, kan)
        assertEquals(miss, events.filterIsInstance<GameEvent.RinshanDraw>().single().tile)
        assertEquals(miss, events.filterIsInstance<GameEvent.TileDiscarded>().single { it.player == 0 }.tile)
        assertEquals(1, f.s.rinshanPointer)
        assertEquals(MeldType.CLOSED_KAN, f.s.players[0].melds.single().type)
        assertTrue(f.s.riichiDeclared[0])
        assertTrue(f.s.ippatsuActive.none { it })
        assertEquals(1, events.filterIsInstance<GameEvent.DoraRevealed>().size)
    }

    @Test fun `winning rinshan pauses with legal tsumo and retained rinshan scoring context`() {
        val f = fixture(fourth, rinshan = listOf(win))
        draw(f)
        val kan = f.e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        val events = submit(f, kan)
        assertEquals(win, f.s.players[0].drawnTile)
        assertTrue(f.s.isRinshanDraw)
        assertTrue(events.none { it is GameEvent.TileDiscarded })
        assertTrue(GameAction.DeclareTsumo(0) in f.e.legalActions(0))
        val result = submit(f, GameAction.DeclareTsumo(0))
        assertTrue(result.filterIsInstance<GameEvent.TsumoWin>().single().yakuList.any { it.contains("岭上") })
    }

    @Test fun `same wait but changed decomposition cannot pause automatic discard for an illegal kan`() {
        val closed = tiles(Suit.MAN, "666777888") + tiles(Suit.SOU, "56") + tiles(Suit.PIN, "11")
        val drawn = Tile(Suit.MAN, 6, 3)
        val f = fixture(drawn, closed = closed)
        val events = draw(f)
        assertEquals(drawn, events.filterIsInstance<GameEvent.TileDiscarded>().single { it.player == 0 }.tile)
        assertTrue(f.s.players[0].melds.isEmpty())
    }

    @Test fun `forged post riichi kan and hand discard are rejected with no side effects`() {
        val f = fixture(fourth)
        draw(f)
        val kan = f.e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        val before = f.bus.getHistory().toList()
        val actions = listOf(kan.copy(kanType = MeldType.ADDED_KAN), kan.copy(kanType = MeldType.OPEN_KAN),
            kan.copy(tiles = listOf(kan.tiles[0], kan.tiles[0], kan.tiles[2], kan.tiles[3])),
            GameAction.DiscardTile(0, f.s.players[0].closedTiles.first()))
        for (bad in actions) assertTrue(f.e.handleAction(bad) is ActionResult.Rejected)
        assertEquals(before, f.bus.getHistory())
        assertEquals(fourth, f.s.players[0].drawnTile)
        assertEquals(0, f.s.rinshanPointer)
        assertTrue(kan in f.e.legalActions(0))
        TestTableBuilder.assertPhysical(f.s)
    }

    @Test fun `automatic tsumogiri still offers another player's ron`() {
        val opponent = tiles(Suit.MAN, "123789").map { it.copy(copyIndex = 1) } + tiles(Suit.PIN, "123") + tiles(Suit.DRAGON, "222") + tiles(Suit.WIND, "4")
        val drawn = Tile(Suit.WIND, 4, 1)
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting(), 1 to opponent),
            wallPrefix = listOf(drawn), phase = GamePhase.DRAW)
        markRiichi(s, 0)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false)
        val f = Fixture(e, bus)
        draw(f)
        assertEquals(GamePhase.RESPONSE_WINDOW, f.s.phase)
        assertTrue(GameAction.DeclareRon(1) in f.e.legalActions(1))
        submit(f, GameAction.DeclareRon(1))
        for (seat in listOf(2, 3)) if (GameAction.Pass(seat) in f.e.legalActions(seat)) submit(f, GameAction.Pass(seat))
        assertEquals(GamePhase.ROUND_END, f.s.phase)
        assertEquals(0, f.bus.getHistory().filterIsInstance<GameEvent.RonWin>().single().loser)
    }

    @Test fun `automatic rotation into human turn also triggers riichi tsumogiri`() {
        val previous = Tile(Suit.DRAGON, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), drawn = 3 to previous, wallPrefix = listOf(miss))
        markRiichi(s, 0)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false)
        val f = Fixture(e, bus)
        submit(f, GameAction.DiscardTile(3, previous))
        repeat(4) { for (seat in 0..3) if (GameAction.Pass(seat) in f.e.legalActions(seat)) submit(f, GameAction.Pass(seat)) }
        assertEquals(1, f.s.players[0].discardHistory.count { it == miss })
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.TileDiscarded>().count { it.player == 0 && it.tile == miss })
    }

    @Test fun `a second legal kan from rinshan pauses again and can lead to rinshan tsumo`() {
        val closed = tiles(Suit.DRAGON, "111222") + tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")
        val second = Tile(Suit.DRAGON, 2, 3)
        val f = fixture(fourth, closed = closed, rinshan = listOf(second, win))
        draw(f)
        val firstKan = f.e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        submit(f, firstKan)
        assertEquals(second, f.s.players[0].drawnTile)
        val secondKan = f.e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        assertTrue(second in secondKan.tiles)
        submit(f, secondKan)
        assertEquals(2, f.s.rinshanPointer)
        assertEquals(2, f.s.players[0].melds.size)
        assertTrue(GameAction.DeclareTsumo(0) in f.e.legalActions(0))
        assertTrue(f.s.players[0].discards.isEmpty())
        submit(f, GameAction.DeclareTsumo(0))
    }

    @Test fun `empty live wall cannot offer kan and last nonwinner draw ends with exhaustive draw`() {
        val f = fixture(fourth) { s ->
            // Keep exactly one drawable tile, physically moving the rest into an opponent river.
            for (tile in s.wall.drop(1)) {
                s.tilePool.transfer(tile, Zone.WALL, Zone.DISCARD, 1)
                s.players[1].discards.add(tile)
                s.players[1].discardHistory.add(tile)
            }
            s.wallEndExclusive = 1
        }
        val events = draw(f)
        assertTrue(f.e.legalActions(0).none { it is GameAction.DeclareKan })
        assertEquals(fourth, events.filterIsInstance<GameEvent.TileDiscarded>().single { it.player == 0 }.tile)
        for (seat in 1..3) if (GameAction.Pass(seat) in f.e.legalActions(seat)) submit(f, GameAction.Pass(seat))
        assertEquals(GamePhase.ROUND_END, f.s.phase)
        assertEquals(DrawReason.EXHAUSTIVE, f.bus.getHistory().filterIsInstance<GameEvent.DrawGame>().single().reason)
    }

    @Test fun `exhausted rinshan capacity suppresses otherwise legal riichi kan`() {
        val f = fixture(fourth, auto = false) { s ->
            // Construct a physically valid used-up replacement wall without fabricating extra tiles.
            repeat(4) {
                val consumed = s.deadWall[s.rinshanPointer++]
                s.tilePool.transfer(consumed, Zone.DEAD_WALL, Zone.DISCARD, 1)
                s.players[1].discards.add(consumed)
                s.players[1].discardHistory.add(consumed)
                val replacement = s.wall[--s.wallEndExclusive]
                s.tilePool.transfer(replacement, Zone.WALL, Zone.DEAD_WALL)
                s.deadWallReplacements.add(replacement)
            }
            TestTableBuilder.assertPhysical(s)
        }
        draw(f)
        assertTrue(f.e.legalActions(0).none { it is GameAction.DeclareKan })
        val history = f.bus.getHistory().toList()
        assertTrue(f.e.handleAction(GameAction.DeclareKan(0, (0..3).map { Tile(Suit.DRAGON, 1, it) }, MeldType.CLOSED_KAN)) is ActionResult.Rejected)
        assertEquals(history, f.bus.getHistory())
    }

    @Test fun `automatic discard remains inside reentry guard`() {
        val f = fixture(miss)
        var nested: ActionResult? = null
        f.bus.subscribe<GameEvent.TileDiscarded> { if (it.player == 0) nested = f.e.handleAction(GameAction.DrawTile(1)) }
        draw(f)
        assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), nested)
        assertEquals(1, f.s.players[0].discardHistory.size)
    }

    @Test fun `default automation passes through all three AI turns then pauses at human tsumo`() {
        val hands = (1..3).associateWith { copy ->
            listOf(1, 5, 9).map { Tile(Suit.MAN, it, copy) } +
                listOf(1, 3, 9).map { Tile(Suit.SOU, it, copy) } +
                listOf(1, 5, 9).map { Tile(Suit.PIN, it, copy) } +
                (1..3).map { Tile(Suit.WIND, it, copy) } + Tile(Suit.DRAGON, 3, copy)
        } + (0 to waiting())
        val s = TestTableBuilder.create(hands = hands, phase = GamePhase.DRAW,
            wallPrefix = listOf(miss, Tile(Suit.MAN, 2, 1), Tile(Suit.SOU, 8), Tile(Suit.PIN, 2), win))
        markRiichi(s, 0)
        val bus = GameEventBus()
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = true)
        e.initializeMatch(0, (1..3).associateWith { com.majiang.ai.AIPlayer(it, com.majiang.ai.TrivialAIStrategy()) })
        val f = Fixture(e, bus)
        val events = draw(f)
        assertEquals(0, f.s.currentPlayer)
        assertEquals(win, f.s.players[0].drawnTile)
        assertTrue(GameAction.DeclareTsumo(0) in e.legalActions(0))
        assertEquals(listOf(0, 1, 2, 3), events.filterIsInstance<GameEvent.TileDiscarded>().map { it.player })
        assertEquals(5, events.filterIsInstance<GameEvent.TileDrawn>().size)
        assertTrue(events.none { it is GameEvent.TsumoWin })
    }

    @Test fun `missing AI controller does not turn another seat into the human player`() {
        val s = TestTableBuilder.create(hands = mapOf(2 to waiting()), wallPrefix = listOf(miss),
            phase = GamePhase.DRAW, currentPlayer = 2)
        markRiichi(s, 2)
        val bus = GameEventBus()
        // Without initializeMatch, the declared human seat is still 0. Manual simulations own all other seats.
        val e = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false)
        val f = Fixture(e, bus)
        assertEquals(0, f.e.humanPlayerIndex)
        val events = draw(f, 2)
        assertEquals(miss, f.s.players[2].drawnTile)
        assertTrue(events.none { it is GameEvent.TileDiscarded })
    }
}

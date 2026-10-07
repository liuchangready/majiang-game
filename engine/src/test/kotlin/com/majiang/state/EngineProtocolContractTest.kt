package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.*
import com.majiang.model.*
import com.majiang.rules.RiichiRuleEngine
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.random.Random

class EngineProtocolContractTest {
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }
    private data class Fixture(val bus: GameEventBus, val engine: ApplicationCoordinator) {
        val state: GameState get() = engine.activeRound?.readState() ?: engine.lastEndedRoundSnapshot!!
    }
    private fun addedKanFixture(customize: (GameState) -> Unit = {}): Fixture {
        val ponTiles = (0..2).map { Tile(Suit.MAN, 5, it) }
        val waiting = tiles(Suit.MAN, "46") + tiles(Suit.PIN, "123789") + tiles(Suit.SOU, "12366")
        val state = TestTableBuilder.create(hands = mapOf(1 to waiting), drawn = 0 to Tile(Suit.MAN, 5, 3),
            melds = mapOf(0 to listOf(Meld(MeldType.PON, ponTiles, 3, ponTiles[0]))))
        customize(state)
        val bus = GameEventBus()
        return Fixture(bus, ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false))
    }
    private fun submit(f: Fixture, action: GameAction) {
        assertTrue(f.engine.handleAction(action) is ActionResult.Accepted, "Rejected $action")
        TestTableBuilder.assertPhysical(f.state)
        StateInvariants.assertValid(f.state)
    }

    @Test
    fun `ron responses wait for all seats and settle only the nearest winner once`() {
        fun waiting(copy: Int) = listOf(
            Tile(Suit.MAN, 2, copy), Tile(Suit.MAN, 3, copy), Tile(Suit.MAN, 4, copy),
            Tile(Suit.PIN, 2, copy), Tile(Suit.PIN, 3, copy), Tile(Suit.PIN, 4, copy),
            Tile(Suit.SOU, 2, copy), Tile(Suit.SOU, 3, copy), Tile(Suit.SOU, 4, copy),
            Tile(Suit.SOU, 6, copy), Tile(Suit.SOU, 7, copy), Tile(Suit.SOU, 8, copy), Tile(Suit.MAN, 5, copy)
        )
        val tile = Tile(Suit.MAN, 5, 3)
        val state = TestTableBuilder.create(hands = mapOf(3 to waiting(0), 0 to waiting(1)), drawn = 2 to tile)
        val bus = GameEventBus()
        val f = Fixture(bus, ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false))
        submit(f, GameAction.DiscardTile(2, tile))
        assertTrue(GameAction.DeclareRon(0) in f.engine.legalActions(0))
        assertTrue(GameAction.DeclareRon(3) in f.engine.legalActions(3))
        submit(f, GameAction.DeclareRon(0))
        assertEquals(GamePhase.RESPONSE_WINDOW, f.state.phase)
        assertEquals(listOf(25000, 25000, 25000, 25000), f.state.scores.toList())
        assertTrue(bus.getHistory().none { it is GameEvent.RonWin || it is GameEvent.ScoreSettled })
        assertTrue(f.engine.handleAction(GameAction.DeclareRon(0)) is ActionResult.Rejected)
        submit(f, GameAction.DeclareRon(3))
        if (GameAction.Pass(1) in f.engine.legalActions(1)) submit(f, GameAction.Pass(1))
        assertEquals(3, bus.getHistory().filterIsInstance<GameEvent.RonWin>().single().winner)
        assertEquals(1, bus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().size)
        assertEquals(GamePhase.ROUND_END, f.state.phase)
    }

    @Test
    fun `pon never grants tsumo before another tile has been drawn`() {
        val called = Tile(Suit.DRAGON, 1, 2)
        val waiting = listOf(Tile(Suit.DRAGON, 1), Tile(Suit.DRAGON, 1, 1)) +
            tiles(Suit.MAN, "234") + tiles(Suit.PIN, "234") + tiles(Suit.SOU, "23455")
        val state = TestTableBuilder.create(hands = mapOf(0 to waiting), drawn = 3 to called)
        val bus = GameEventBus()
        val f = Fixture(bus, ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false))
        submit(f, GameAction.DiscardTile(3, called))
        val pon = f.engine.legalActions(0).filterIsInstance<GameAction.DeclarePon>().first()
        submit(f, pon)
        for (seat in 1..2) if (GameAction.Pass(seat) in f.engine.legalActions(seat)) submit(f, GameAction.Pass(seat))
        assertEquals(0, f.state.currentPlayer)
        assertNull(f.state.players[0].drawnTile)
        assertTrue(f.state.sameTurnFuriten[0], "Choosing pon instead of an available ron also declines ron")
        assertFalse(GameAction.DeclareTsumo(0) in f.engine.legalActions(0))
        assertTrue(f.engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Rejected)
        assertTrue(bus.getHistory().none { it is GameEvent.TsumoWin })
    }

    @Test
    fun `chi rejects a forged meld type without recording a response`() {
        val called = Tile(Suit.MAN, 1)
        val state = TestTableBuilder.create(hands = mapOf(1 to listOf(Tile(Suit.MAN, 2), Tile(Suit.MAN, 3))), drawn = 0 to called)
        val bus = GameEventBus()
        val f = Fixture(bus, ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false))
        submit(f, GameAction.DiscardTile(0, called))
        val legal = f.engine.legalActions(1).filterIsInstance<GameAction.DeclareChi>().first()
        val historySize = bus.getHistory().size
        val forged = legal.copy(meld = legal.meld.copy(type = MeldType.PON))
        assertTrue(f.engine.handleAction(forged) is ActionResult.Rejected)
        assertEquals(historySize, bus.getHistory().size)
        assertTrue(legal in f.engine.legalActions(1))
        TestTableBuilder.assertPhysical(f.state)
    }

    @Test
    fun `added kan opens chankan before changing pon or drawing rinshan`() {
        val f = addedKanFixture()
        val beforePointer = f.state.rinshanPointer
        val kan = f.engine.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single { it.kanType == MeldType.ADDED_KAN }
        submit(f, kan)
        assertEquals(GamePhase.CHANKAN_WINDOW, f.state.phase)
        assertEquals(MeldType.PON, f.state.players[0].melds.single().type)
        assertEquals(beforePointer, f.state.rinshanPointer)
        assertFalse(f.bus.getHistory().any { it is GameEvent.KanDeclared || it is GameEvent.RinshanDraw })
        assertTrue(GameAction.DeclareRon(1) in f.engine.legalActions(1))
        submit(f, GameAction.DeclareRon(1))
        for (seat in listOf(2, 3)) if (GameAction.Pass(seat) in f.engine.legalActions(seat)) submit(f, GameAction.Pass(seat))
        assertEquals(GamePhase.ROUND_END, f.state.phase)
        assertEquals(1, f.bus.getHistory().filterIsInstance<GameEvent.RonWin>().size)
        assertEquals(beforePointer, f.state.rinshanPointer)
        assertEquals(MeldType.PON, f.state.players[0].melds.single().type)
        assertEquals(100000, f.state.scores.sum() + f.state.riichiSticks * 1000)
    }

    @Test
    fun `passing chankan commits added kan exactly once and marks temporary furiten`() {
        val f = addedKanFixture()
        val kan = f.engine.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single { it.kanType == MeldType.ADDED_KAN }
        submit(f, kan)
        for (seat in 1..3) if (GameAction.Pass(seat) in f.engine.legalActions(seat)) submit(f, GameAction.Pass(seat))
        assertTrue(f.state.phase in setOf(GamePhase.DISCARD, GamePhase.CHEAT_WINDOW))
        assertEquals(MeldType.ADDED_KAN, f.state.players[0].melds.single().type)
        assertEquals(1, f.state.rinshanPointer)
        assertTrue(f.state.sameTurnFuriten[1])
        assertEquals(1, f.bus.getHistory().filterIsInstance<GameEvent.KanDeclared>().size)
        assertEquals(1, f.bus.getHistory().filterIsInstance<GameEvent.RinshanDraw>().size)
        assertTrue(f.engine.handleAction(kan) is ActionResult.Rejected)
        assertEquals(1, f.state.rinshanPointer)
    }

    @Test
    fun `claimed discard still causes furiten despite empty river`() {
        val f = addedKanFixture { s ->
            s.players[1].discardHistory.add(Tile(Suit.MAN, 5, 0))
        }
        assertTrue(f.state.players[1].discards.isEmpty())
        assertTrue(RiichiRuleEngine.INSTANCE.isFuriten(f.state.players[1], f.state.players[1].closedTiles))
        val kan = f.engine.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single { it.kanType == MeldType.ADDED_KAN }
        submit(f, kan)
        assertFalse(GameAction.DeclareRon(1) in f.engine.legalActions(1))
    }

    @Test
    fun `forged added kan is rejected without opening a response window`() {
        for (tiles in listOf(emptyList(), listOf(Tile(Suit.WIND, 4, 3)), listOf(Tile(Suit.MAN, 5, 3), Tile(Suit.MAN, 5, 3)))) {
            val f = addedKanFixture()
            val before = f.state.players.map { it.allClosedTiles.toList() }
            assertDoesNotThrow {
                assertTrue(f.engine.handleAction(GameAction.DeclareKan(0, tiles, MeldType.ADDED_KAN)) is ActionResult.Rejected)
            }
            assertEquals(GamePhase.DISCARD, f.state.phase)
            assertEquals(before, f.state.players.map { it.allClosedTiles.toList() })
            assertTrue(f.bus.getHistory().isEmpty())
            assertNull(f.engine.activeRound?.responseWindow)
            TestTableBuilder.assertPhysical(f.state)
        }
    }

    @Test
    fun `passing ron in riichi causes permanent furiten surviving the next draw`() {
        val f = addedKanFixture { s ->
            s.riichiDeclared[1] = true
            s.players[1].riichiDeclared = true
            s.scores[1] -= 1000
            s.riichiSticks = 1
        }
        val kan = f.engine.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single { it.kanType == MeldType.ADDED_KAN }
        submit(f, kan)
        assertTrue(GameAction.DeclareRon(1) in f.engine.legalActions(1))
        for (seat in 1..3) if (GameAction.Pass(seat) in f.engine.legalActions(seat)) submit(f, GameAction.Pass(seat))
        assertTrue(f.state.riichiFuriten[1])
        var steps = 0
        while (!(f.state.currentPlayer == 1 && f.state.players[1].drawnTile != null)) {
            assertTrue(steps++ < 15)
            val choices = (0..3).flatMap { f.engine.legalActions(it) }
            val action = choices.filterIsInstance<GameAction.Pass>().firstOrNull()
                ?: choices.filterIsInstance<GameAction.DiscardTile>().firstOrNull()
            assertNotNull(action)
            submit(f, action!!)
        }
        assertTrue(f.state.riichiFuriten[1])
    }

    @Test
    fun `riichi pays once and restricts the next draw to tsumogiri`() {
        val waiting = tiles(Suit.MAN, "123456") + tiles(Suit.PIN, "789") + tiles(Suit.SOU, "2345")
        val discard = Tile(Suit.WIND, 4)
        val state = TestTableBuilder.create(hands = mapOf(0 to waiting), drawn = 0 to discard)
        val bus = GameEventBus()
        val f = Fixture(bus, ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false, autoRiichiDiscard = false))
        val action = GameAction.DeclareRiichi(0, discard)
        assertTrue(action in f.engine.legalActions(0))
        submit(f, action)
        assertEquals(24000, f.state.scores[0])
        assertEquals(1, f.state.riichiSticks)
        assertTrue(f.engine.handleAction(action) is ActionResult.Rejected)
        assertEquals(24000, f.state.scores[0])
        var steps = 0
        while (!(f.state.currentPlayer == 0 && f.state.phase in setOf(GamePhase.DISCARD, GamePhase.CHEAT_WINDOW))) {
            assertTrue(steps++ < 20, "Did not return to riichi player")
            val choices = (0..3).flatMap { f.engine.legalActions(it) }
            val next = choices.filterIsInstance<GameAction.Pass>().firstOrNull()
                ?: choices.filterIsInstance<GameAction.DiscardTile>().firstOrNull()
            assertNotNull(next)
            submit(f, next!!)
        }
        assertEquals(listOf(GameAction.DiscardTile(0, f.state.players[0].drawnTile!!)), f.engine.legalActions(0).filterIsInstance<GameAction.DiscardTile>())
        assertTrue(f.engine.legalActions(0).none { it is GameAction.DeclareRiichi || it is GameAction.DeclareKan })
        assertEquals(100000, f.state.scores.sum() + f.state.riichiSticks * 1000)
    }

    @Test
    fun `committed state is visible to observers and reentrant commands are rejected`() {
        val state = GameState()
        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false, random = Random(91))
        engine.initializeMatch(0)
        var nested: ActionResult? = null
        var received = 0
        bus.subscribe<GameEvent.DealingComplete>(priority = 0) {
            val live = engine.activeRound!!.readState()
            TestTableBuilder.assertPhysical(live)
            StateInvariants.assertValid(live)
            nested = engine.handleAction(engine.legalActions(0).filterIsInstance<GameAction.DiscardTile>().first())
            error("deliberately faulty listener")
        }
        bus.subscribe<GameEvent.DealingComplete>(priority = 1) { received++ }
        assertTrue(engine.handleAction(GameAction.StartStacking()) is ActionResult.Accepted)
        assertTrue(nested is ActionResult.Rejected)
        assertEquals(1, received)
        val next = (0..3).flatMap { engine.legalActions(it) }.first()
        assertTrue(engine.handleAction(next) is ActionResult.Accepted)
        TestTableBuilder.assertPhysical(engine.activeRound!!.readState())
    }
}

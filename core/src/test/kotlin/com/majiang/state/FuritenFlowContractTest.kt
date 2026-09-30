package com.majiang.state

import com.majiang.event.*
import com.majiang.model.*
import com.majiang.rules.FuritenRules
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class FuritenFlowContractTest {
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { val r = it.digitToInt(); Tile(suit, r, copies.getOrDefault(r, 0).also { n -> copies[r] = n + 1 }) }
    }
    private fun waiting() = tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") + tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")
    private fun engine(s: GameState) = GameStateMachine(s, GameEventBus(), autoPlayAi = false, autoRiichiDiscard = false)
    private fun riichi(s: GameState, seat: Int) {
        s.riichiDeclared[seat] = true; s.players[seat].riichiDeclared = true
        s.scores[seat] -= 1000; s.riichiSticks++
    }
    private fun submit(e: GameStateMachine, action: GameAction): ActionResult.Accepted {
        val result = e.handleAction(action)
        assertTrue(result is ActionResult.Accepted, "$action -> $result")
        TestTableBuilder.assertPhysical(e.state)
        StateInvariants.assertValid(e.state)
        assertEquals(100000, e.state.scores.sum() + e.state.riichiSticks * 1000)
        return result as ActionResult.Accepted
    }

    @Test fun `discard temporary and riichi furiten each reject every ron wait with no command side effects`() {
        for (reason in 0..2) for (rank in listOf(4, 7)) {
            val trigger = Tile(Suit.SOU, rank, 1)
            val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), drawn = 3 to trigger,
                discards = if (reason == 0) mapOf(0 to listOf(Tile(Suit.SOU, 4))) else emptyMap())
            if (reason == 1) s.sameTurnFuriten[0] = true
            if (reason == 2) { riichi(s, 0); s.riichiFuriten[0] = true }
            val e = engine(s)
            submit(e, GameAction.DiscardTile(3, trigger))
            assertTrue(e.legalActions(0).none { it is GameAction.DeclareRon })
            val history = e.eventBus.getHistory().toList()
            val before = s.players.map { it.allClosedTiles.toList() }
            assertTrue(e.handleAction(GameAction.DeclareRon(0)) is ActionResult.Rejected)
            assertEquals(history, e.eventBus.getHistory())
            assertEquals(before, s.players.map { it.allClosedTiles.toList() })
            assertTrue(history.none { it is GameEvent.RonWin })
            TestTableBuilder.assertPhysical(s)
        }
    }

    @Test fun `each furiten cause still permits explicit self draw and exactly one settlement`() {
        for (reason in 0..2) {
            val win = Tile(Suit.SOU, 7)
            val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), drawn = 0 to win,
                discards = if (reason == 0) mapOf(0 to listOf(Tile(Suit.SOU, 4))) else emptyMap())
            if (reason == 1) s.sameTurnFuriten[0] = true
            if (reason == 2) { riichi(s, 0); s.riichiFuriten[0] = true }
            val e = engine(s)
            assertTrue(GameAction.DeclareTsumo(0) in e.legalActions(0))
            submit(e, GameAction.DeclareTsumo(0))
            assertEquals(1, e.eventBus.getHistory().filterIsInstance<GameEvent.TsumoWin>().size)
            assertEquals(1, e.eventBus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().size)
            assertTrue(e.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Rejected)
        }
    }

    @Test fun `own draw clears only temporary marker and preserves riichi marker and other seats`() {
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), wallPrefix = listOf(Tile(Suit.WIND, 4)), phase = GamePhase.DRAW)
        riichi(s, 0)
        s.sameTurnFuriten[0] = true; s.sameTurnFuriten[1] = true; s.riichiFuriten[0] = true
        submit(engine(s), GameAction.DrawTile(0))
        assertFalse(s.sameTurnFuriten[0])
        assertTrue(s.sameTurnFuriten[1])
        assertTrue(s.riichiFuriten[0])
    }

    @Test fun `riichi furiten winning draw pauses automatic tsumogiri for explicit tsumo`() {
        val win = Tile(Suit.SOU, 7)
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), wallPrefix = listOf(win), phase = GamePhase.DRAW)
        riichi(s, 0); s.riichiFuriten[0] = true
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        val result = submit(e, GameAction.DrawTile(0))
        assertEquals(win, s.players[0].drawnTile)
        assertTrue(GameAction.DeclareTsumo(0) in e.legalActions(0))
        assertTrue(result.events.none { it is GameEvent.TileDiscarded || it is GameEvent.TsumoWin })
        submit(e, GameAction.DeclareTsumo(0))
    }

    @Test fun `available ron is never marked missed before the player responds`() {
        val tile = Tile(Suit.SOU, 7)
        val s = TestTableBuilder.create(hands = mapOf(2 to waiting()), drawn = 0 to tile)
        val e = engine(s)
        submit(e, GameAction.DiscardTile(0, tile))
        assertTrue(GameAction.DeclareRon(2) in e.legalActions(2))
        assertFalse(s.sameTurnFuriten[2])
        submit(e, GameAction.Pass(2))
        assertTrue(s.sameTurnFuriten[2])
    }

    @Test fun `a no yaku completion automatically marks temporary furiten without requiring a pass`() {
        // 123m 123p 23s NN + open 789m: 4s has no yaku, but 1s has mixed triple sequence.
        val closed = tiles(Suit.MAN, "123") + tiles(Suit.PIN, "123") + tiles(Suit.SOU, "23") + tiles(Suit.WIND, "44")
        val chi = tiles(Suit.MAN, "789")
        val missed = Tile(Suit.SOU, 4)
        val s = TestTableBuilder.create(hands = mapOf(2 to closed), drawn = 0 to missed,
            melds = mapOf(2 to listOf(Meld(MeldType.CHI, chi, 1, chi.first()))))
        assertTrue(FuritenRules.completesHand(s.players[2], missed))
        assertTrue(LegalActionProvider.buildEligibleResponseActions(s, 0, missed, false)[2].orEmpty().none { it is GameAction.DeclareRon })
        assertTrue(LegalActionProvider.buildEligibleResponseActions(s, 0, Tile(Suit.SOU, 1), false)[2].orEmpty().any { it is GameAction.DeclareRon })
        val e = engine(s)
        submit(e, GameAction.DiscardTile(0, missed))
        assertTrue(s.sameTurnFuriten[2], "No-yaku structural completion was silently passed")
        assertTrue(e.legalActions(2).isEmpty(), "Do not open a pass-only human prompt to track missed no-yaku completion")
        assertTrue(LegalActionProvider.buildEligibleResponseActions(s, 1, Tile(Suit.SOU, 1), false)[2].orEmpty().none { it is GameAction.DeclareRon })
    }

    @Test fun `new round clears both transient furiten flags`() {
        val s = GameState()
        s.sameTurnFuriten.fill(true); s.riichiFuriten.fill(true)
        val e = engine(s)
        submit(e, GameAction.StartStacking())
        assertTrue(s.sameTurnFuriten.none { it })
        assertTrue(s.riichiFuriten.none { it })
    }

    @Test fun `rinshan clears temporary furiten while riichi furiten still permits winning replacement draw`() {
        val fourth = Tile(Suit.DRAGON, 1, 3)
        val win = Tile(Suit.SOU, 7)
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), drawn = 0 to fourth, deadWallPrefix = listOf(win))
        riichi(s, 0); s.riichiFuriten[0] = true; s.sameTurnFuriten[0] = true
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        val kan = e.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        submit(e, kan)
        assertFalse(s.sameTurnFuriten[0])
        assertTrue(s.riichiFuriten[0])
        assertTrue(s.isRinshanDraw)
        assertEquals(win, s.players[0].drawnTile)
        assertTrue(GameAction.DeclareTsumo(0) in e.legalActions(0))
        submit(e, GameAction.DeclareTsumo(0))
    }

    @Test fun `automatic AI seats also record no yaku completions as missed wins`() {
        val closed = tiles(Suit.MAN, "123") + tiles(Suit.PIN, "123") + tiles(Suit.SOU, "23") + tiles(Suit.WIND, "44")
        val chi = tiles(Suit.MAN, "789")
        val missed = Tile(Suit.SOU, 4)
        val s = TestTableBuilder.create(hands = mapOf(2 to closed), drawn = 0 to missed,
            melds = mapOf(2 to listOf(Meld(MeldType.CHI, chi, 1, chi.first()))))
        val e = GameStateMachine(s, GameEventBus())
        e.initializeMatch(1)
        submit(e, GameAction.DiscardTile(0, missed))
        assertTrue(s.sameTurnFuriten[2])
        assertEquals(1, s.currentPlayer)
    }

    @Test fun `claimed historical discard still blocks chankan on the other waiting tile`() {
        val s = TestTableBuilder.create(hands = mapOf(0 to waiting()), drawn = 3 to Tile(Suit.SOU, 7))
        s.players[0].discardHistory.add(Tile(Suit.SOU, 4))
        assertTrue(s.players[0].discards.isEmpty())
        assertTrue(FuritenRules.status(s.players[0]).discard)
        val responses = LegalActionProvider.buildEligibleResponseActions(s, 3, Tile(Suit.SOU, 7), true)
        assertTrue(responses[0].orEmpty().none { it is GameAction.DeclareRon })
    }
}

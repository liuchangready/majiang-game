package com.majiang.state

import com.majiang.event.*
import com.majiang.model.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SettlementDetailPaymentAcceptanceTest {
    private fun hand() = listOf(
        Tile(Suit.MAN,2),Tile(Suit.MAN,3),Tile(Suit.MAN,4),
        Tile(Suit.PIN,2),Tile(Suit.PIN,3),Tile(Suit.PIN,4),
        Tile(Suit.SOU,2),Tile(Suit.SOU,3),Tile(Suit.SOU,4),
        Tile(Suit.SOU,6),Tile(Suit.SOU,7),Tile(Suit.SOU,8),Tile(Suit.MAN,5,3))

    @Test fun `dealer tsumo explanation equals each payment including honba and deposited sticks`() {
        val indicator=Tile(Suit.MAN,4,1)
        val s=TestTableBuilder.create(hands=mapOf(0 to hand()),drawn=0 to Tile(Suit.MAN,5),dealer=0,deadWallPrefix=listOf(indicator))
        s.doraIndicators.add(indicator); s.honbaSticks=2; s.riichiSticks=2
        s.scores[0]=24000; s.scores[1]=24000
        val bus=GameEventBus(); val engine=GameStateMachine(s,bus,autoPlayAi=false,autoRiichiDiscard=false)
        assertTrue(engine.handleAction(GameAction.DeclareTsumo(0)) is ActionResult.Accepted)
        val b=bus.getHistory().filterIsInstance<GameEvent.TsumoWin>().single().breakdown!!
        val paid=bus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().single()
        assertEquals(7,b.score.han) // tsumo1 + tanyao1 + sanshoku2 + dora2 + red1
        assertEquals(20600,b.totalPoints) // 6000x3 + 200x3 + 2000
        assertEquals(mapOf(0 to 20600,1 to -6200,2 to -6200,3 to -6200),paid.payments)
        assertEquals(b.totalPoints,paid.payments[0]); assertEquals(100000,paid.newScores.sum()+s.riichiSticks*1000)
        assertTrue(b.isDealer); assertTrue(b.isTsumo)
        assertEquals(listOf(2),b.dora.indicatorHits.map{it.tiles.size})
    }

    @Test fun `dealer ron explanation equals only discarder payment plus riichi pot`() {
        val indicator=Tile(Suit.MAN,4,1); val tile=Tile(Suit.MAN,5)
        val s=TestTableBuilder.create(hands=mapOf(0 to hand()),drawn=1 to tile,currentPlayer=1,phase=GamePhase.DISCARD,dealer=0,deadWallPrefix=listOf(indicator))
        s.doraIndicators.add(indicator); s.honbaSticks=2; s.riichiSticks=2
        s.scores[0]=24000; s.scores[1]=24000
        val bus=GameEventBus(); val engine=GameStateMachine(s,bus,autoPlayAi=false,autoRiichiDiscard=false)
        assertTrue(engine.handleAction(GameAction.DiscardTile(1,tile)) is ActionResult.Accepted)
        for(seat in listOf(2,3)) if(GameAction.Pass(seat) in engine.legalActions(seat)) engine.handleAction(GameAction.Pass(seat))
        assertTrue(engine.handleAction(GameAction.DeclareRon(0)) is ActionResult.Accepted)
        val b=bus.getHistory().filterIsInstance<GameEvent.RonWin>().single().breakdown!!
        val paid=bus.getHistory().filterIsInstance<GameEvent.ScoreSettled>().single()
        assertEquals(6,b.score.han); assertEquals(20600,b.totalPoints)
        assertEquals(20600,paid.payments[0]); assertEquals(-18600,paid.payments[1])
        assertEquals(0,paid.payments[2]?:0); assertEquals(0,paid.payments[3]?:0)
        assertEquals(100000,paid.newScores.sum()+s.riichiSticks*1000)
        assertTrue(b.isDealer); assertFalse(b.isTsumo)
    }
}

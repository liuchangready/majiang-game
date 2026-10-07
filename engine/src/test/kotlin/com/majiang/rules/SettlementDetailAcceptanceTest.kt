package com.majiang.rules

import com.majiang.event.*
import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.math.BigInteger

/** Codex-owned acceptance: independent expected arithmetic and adversarial event consumers. */
class SettlementDetailAcceptanceTest {
    private val evaluator = WinningScoreEvaluator()
    private fun ts(suit: Suit, ranks: String): List<Tile> {
        val seen = mutableMapOf<Int, Int>()
        return ranks.map { it.digitToInt() }.map { r -> Tile(suit,r,seen.getOrDefault(r,0)).also { seen[r]=it.copyIndex+1 } }
    }
    private fun pinfu() = PlayerHand().apply {
        closedTiles.addAll(ts(Suit.MAN,"23") + ts(Suit.PIN,"234567") + ts(Suit.SOU,"23488"))
    }
    private fun context(tsumo: Boolean = false, riichi: Boolean = false) =
        YakuEvaluator.WinContext(Tile(Suit.MAN,1), tsumo,true,2,1,isRiichi=riichi)

    @Test fun `ordinary ron lists exactly pinfu and derives 30 fu 1 han 1000 points`() {
        val b = evaluator.explain(pinfu(),context(),emptyList(),honba=2,riichiSticks=1,loser=3)!!
        assertEquals(listOf("平和" to 1),b.yakuLines)
        assertEquals(30,b.fu.finalFu); assertEquals(30,b.fu.subtotal)
        assertEquals(1,b.score.han); assertEquals(240,b.score.basePoints)
        assertEquals("240",b.score.rawBasePointsExact)
        assertEquals(1000,b.score.nonDealerRonPayment())
        assertEquals(2600,b.totalPoints)
        assertFalse(b.isDealer); assertFalse(b.isTsumo); assertFalse(b.uraEligible)
    }

    @Test fun `pinfu tsumo preserves 20 fu exception and rounds each payment separately`() {
        val b=evaluator.explain(pinfu(),context(tsumo=true),emptyList())!!
        assertEquals(setOf("平和" to 1,"门前清自摸和" to 1),b.yakuLines.toSet())
        assertEquals(20,b.fu.finalFu); assertEquals(320,b.score.basePoints)
        assertEquals(700 to 400,b.score.nonDealerTsumoPayment())
        assertEquals(1500,b.totalPoints); assertTrue(b.isTsumo)
        val dealer=evaluator.explain(pinfu(),context(tsumo=true),emptyList(),isDealer=true)!!
        assertEquals(2100,dealer.totalPoints); assertTrue(dealer.isDealer)
    }

    @Test fun `all limit tiers and high han retain exact positive formula`() {
        for ((fu,han,base) in listOf(Triple(30,4,1920),Triple(40,4,2000),Triple(70,3,2000),
            Triple(30,5,2000),Triple(30,6,3000),Triple(30,8,4000),Triple(30,11,6000),Triple(30,13,8000),
            Triple(30,25,8000),Triple(30,30,8000),Triple(30,64,8000),Triple(30,80,8000))) {
            val s=ScoreCalculator().calculate(fu,han)
            val exact=BigInteger.valueOf(fu.toLong()).shiftLeft(han+2).toString()
            assertEquals(exact,s.rawBasePointsExact,"$fu fu $han han")
            assertTrue(s.formulaText.contains(exact),s.formulaText)
            assertEquals(base,s.basePoints)
            assertTrue(s.rawBasePoints>0,"Legacy value must not wrap negative/zero")
        }
    }

    @Test fun `each indicator retains mapping zero hits duplicates and red stacking`() {
        val red=Tile(Suit.MAN,5,3); val normal=Tile(Suit.MAN,5)
        val hand=PlayerHand().apply { closedTiles.addAll(listOf(red,normal)) }
        val d=evaluator.breakdownDora(hand,listOf(Tile(Suit.MAN,4),Tile(Suit.MAN,4,1),Tile(Suit.PIN,9)),listOf(Tile(Suit.MAN,4,2)))
        assertEquals(listOf(2,2,0),d.indicatorHits.map{it.tiles.size})
        assertEquals(listOf(4,4,18),d.indicatorHits.map{it.target.typeIndex})
        assertEquals(listOf(red,normal),d.indicatorHits[0].tiles)
        assertEquals(listOf(red,normal),d.uraHits.single().tiles)
        assertEquals(4,d.indicatorCount); assertEquals(2,d.uraCount); assertEquals(1,d.redCount)
        assertEquals(7,d.totalHan)
    }

    @Test fun `honor and nine indicator wraps and kan counts all four physical tiles`() {
        val hand=PlayerHand().apply {
            closedTiles.addAll(listOf(Tile(Suit.MAN,1),Tile(Suit.WIND,1),Tile(Suit.DRAGON,1)))
            melds.add(Meld(MeldType.CLOSED_KAN,ts(Suit.PIN,"5555"),-1,Tile(Suit.PIN,5)))
        }
        val d=evaluator.breakdownDora(hand,listOf(Tile(Suit.MAN,9),Tile(Suit.WIND,4),Tile(Suit.DRAGON,3),Tile(Suit.PIN,4)))
        assertEquals(listOf(1,1,1,4),d.indicatorHits.map{it.tiles.size})
        assertEquals(7,d.indicatorCount); assertEquals(1,d.redCount); assertEquals(8,d.totalHan)
        assertEquals((0..3).toList(),d.indicatorHits.last().tiles.map{it.copyIndex})
    }

    @Test fun `ron winning tile counted once and ura metadata only disclosed after riichi`() {
        val omote=listOf(Tile(Suit.MAN,9)); val ura=listOf(Tile(Suit.SOU,7))
        val no=evaluator.explain(pinfu(),context(),omote,ura)!!
        assertEquals(1,no.dora.indicatorCount); assertEquals(listOf(Tile(Suit.MAN,1)),no.dora.indicatorHits.single().tiles)
        assertFalse(no.uraEligible); assertTrue(no.dora.uraHits.isEmpty()); assertTrue(no.dora.uraTiles.isEmpty())
        val yes=evaluator.explain(pinfu(),context(riichi=true),omote,ura)!!
        assertTrue(yes.uraEligible); assertEquals(2,yes.dora.uraHits.single().tiles.size)
        assertEquals(5,yes.score.han); assertEquals(8000,yes.totalPoints)
        val alreadyComplete=pinfu().apply { drawnTile=Tile(Suit.MAN,1) }
        assertEquals(yes,evaluator.explain(alreadyComplete,context(riichi=true),omote,ura))
    }

    @Test fun `dora cannot authorize a yaku-less ron`() {
        val hand=PlayerHand().apply { closedTiles.addAll(ts(Suit.MAN,"12")+ts(Suit.PIN,"234678")+ts(Suit.SOU,"34577")) }
        val c=context().copy(winTile=Tile(Suit.MAN,3)) // edge wait; no other yaku
        assertNull(evaluator.explain(hand,c,listOf(Tile(Suit.SOU,6),Tile(Suit.SOU,6,1))))
    }

    @Test fun `ambiguous seven pairs hand displays only the best compatible interpretation`() {
        val hand=PlayerHand().apply { closedTiles.addAll(ts(Suit.MAN,"1122334455667")) }
        val b=evaluator.explain(hand,context().copy(winTile=Tile(Suit.MAN,7,1)),emptyList())!!
        // Pair 11, sequences 234 x2 and 567 x2: ron on 7 is a two-sided wait.
        assertEquals(setOf("平和" to 1,"两杯口" to 3,"清一色" to 6),b.yakuLines.toSet())
        assertEquals(10,b.score.han); assertEquals(16000,b.totalPoints)
        assertFalse(b.yakuLines.any{it.first=="七对子" || it.first=="一杯口"})
    }

    @Test fun `seven pairs retains exactly 25 fu rather than normal ten fu rounding`() {
        val hand=PlayerHand().apply { closedTiles.addAll(ts(Suit.MAN,"1199")+ts(Suit.PIN,"2255")+ts(Suit.SOU,"3388")+Tile(Suit.WIND,1)) }
        val b=evaluator.explain(hand,context().copy(winTile=Tile(Suit.WIND,1,1)),emptyList())!!
        assertEquals(listOf("七对子" to 2),b.yakuLines)
        assertEquals(25,b.fu.finalFu); assertEquals(400,b.score.basePoints); assertEquals(1600,b.totalPoints)
    }

    @Test fun `yakuman lists all compatible yakuman but bonus hits do not change payment`() {
        val hand=PlayerHand().apply { closedTiles.addAll(ts(Suit.DRAGON,"11122233")+ts(Suit.WIND,"11122")) }
        val c=context(tsumo=true).copy(winTile=Tile(Suit.DRAGON,3,2),isRiichi=true)
        val b=evaluator.explain(hand,c,listOf(Tile(Suit.DRAGON,2)),listOf(Tile(Suit.WIND,4)))!!
        assertEquals(setOf("四暗刻","大三元","字一色"),b.yakuLines.map{it.first}.toSet())
        assertEquals(3,b.yakumanCount); assertEquals(0,b.score.han)
        assertEquals(6,b.dora.totalHan); assertEquals(96000,b.totalPoints)
    }

    @Test fun `bus isolates every nested settlement list from producer and listeners`() {
        val tiles=mutableListOf(Tile(Suit.MAN,1)); val hits=mutableListOf(DoraIndicatorHit(Tile(Suit.MAN,9),Tile(Suit.MAN,1),tiles))
        val yaku=mutableListOf("平和" to 1); val fu=mutableListOf(FuLine("底符",30)); val pay=mutableListOf("P3 支付 1000")
        val b=evaluator.explain(pinfu(),context(),emptyList())!!.copy(yakuLines=yaku,
            dora=DoraBreakdown(1,1,1,indicatorTiles=tiles,uraTiles=tiles,redTiles=tiles,indicatorHits=hits,uraHits=hits),
            fu=FuBreakdown(fu,30,30,30),paymentLines=pay)
        val event=GameEvent.RonWin(1,0,3,Tile(Suit.MAN,1),listOf("平和"),1,30,1000,b)
        val bus=GameEventBus(); var second:ScoreBreakdown?=null
        bus.subscribe<GameEvent.RonWin>(-1){ (it.breakdown!!.dora.indicatorHits[0].tiles as MutableList<*>).clear() }
        bus.subscribe<GameEvent.RonWin>{second=it.breakdown}
        bus.publish(event); tiles.clear(); hits.clear(); yaku.clear(); fu.clear(); pay.clear()
        val saved=(bus.getHistory().single() as GameEvent.RonWin).breakdown!!
        assertEquals(1,saved.dora.indicatorHits.single().tiles.size)
        assertEquals(1,second!!.dora.indicatorHits.single().tiles.size)
        val lists=listOf(saved.yakuLines,saved.dora.indicatorTiles,saved.dora.uraTiles,saved.dora.redTiles,
            saved.dora.indicatorHits,saved.dora.uraHits,saved.dora.uraHits.single().tiles,saved.fu.lines,saved.paymentLines)
        for (list in lists) { assertEquals(1,list.size); assertThrows(UnsupportedOperationException::class.java){(list as MutableList<*>).clear()} }
        val tsumo=GameEvent.TsumoWin(1,0,Tile(Suit.MAN,1),listOf("平和"),1,30,1000,b.copy(yakuLines=mutableListOf("平和" to 1)))
        val copy=(tsumo.snapshot() as GameEvent.TsumoWin).breakdown!!
        assertThrows(UnsupportedOperationException::class.java){(copy.yakuLines as MutableList<*>).clear()}
    }
}

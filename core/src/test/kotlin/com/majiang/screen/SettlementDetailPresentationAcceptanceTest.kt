package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.rules.*
import com.majiang.state.GameState
import com.majiang.state.GamePhase
import com.majiang.session.PlayerView
import com.majiang.testing.FakeGameSession
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SettlementDetailPresentationAcceptanceTest : GdxHeadlessTestRunner() {
    private fun fixture() = ScoreBreakdown(
        yakuLines=listOf("立直" to 1,"一发" to 1,"门前清自摸和" to 1,"平和" to 1,"断幺九" to 1,"一杯口" to 1,"清一色" to 6),
        yakumanCount=0,
        dora=DoraBreakdown(10,10,3,
            indicatorTiles=List(10){Tile(Suit.MAN,5,it%2)},uraTiles=List(10){Tile(Suit.MAN,5,it%2)},
            redTiles=listOf(Tile(Suit.MAN,5,3),Tile(Suit.PIN,5,3),Tile(Suit.SOU,5,3)),
            indicatorHits=List(5){ DoraIndicatorHit(Tile(Suit.MAN,4,it%4),Tile(Suit.MAN,5),listOf(Tile(Suit.MAN,5),Tile(Suit.MAN,5,1))) },
            uraHits=List(5){ DoraIndicatorHit(Tile(Suit.PIN,4,it%4),Tile(Suit.PIN,5),listOf(Tile(Suit.PIN,5),Tile(Suit.PIN,5,1))) }),
        fu=FuBreakdown(listOf(FuLine("平和自摸",20)),20,20,20,"平和自摸20符"),
        score=ScoreCalculator().calculate(20,35),paymentLines=listOf("自摸：庄家付16000，闲家各付8000"),
        honbaAddition=600,riichiAddition=2000,totalPoints=34600,uraEligible=true,isDealer=false,isTsumo=true)

    @Test fun `every yaku and every indicator remains reachable exactly once through pages`() {
        val b=fixture()
        for(section in SettlementSection.entries){
            val rows=SettlementDetailPresenter.rows(b,section)
            val pages=(0 until SettlementDetailPresenter.pageCount(b,section)).map{ SettlementDetailPresenter.page(b,section,it) }
            assertEquals(rows,pages.flatten()); assertTrue(pages.all{it.size<=6 && it.isNotEmpty()})
        }
        val roles=SettlementDetailPresenter.rows(b,SettlementSection.YAKU)
        for((name,_) in b.yakuLines) assertEquals(1,roles.count{it.text.contains(name)},name)
        val dora=SettlementDetailPresenter.rows(b,SettlementSection.DORA)
        for (hit in b.dora.indicatorHits + b.dora.uraHits) {
            assertTrue(dora.any { it.tiles == listOf(hit.indicator,hit.target)+hit.tiles },"Mapping must include actual indicator, target and hits")
        }
        assertTrue(SettlementDetailPresenter.pageCount(b,SettlementSection.DORA)>=2)
    }

    @Test fun `zero categories and ineligible ura remain explicit without leaking tiles`() {
        val b=fixture().copy(dora=DoraBreakdown(0,0,0),uraEligible=false)
        val rows=SettlementDetailPresenter.rows(b,SettlementSection.DORA)
        val text=rows.joinToString("\n"){it.text}
        assertTrue(text.contains("表") || text.contains("宝牌")); assertTrue(text.contains("赤"))
        assertTrue(text.contains("未立直") || text.contains("不适用")); assertTrue(text.contains("0"))
        assertTrue(rows.all{it.tiles.isEmpty()})
    }

    @Test fun `yakuman explicitly excludes bonus han and ordinary fu formula`() {
        val b=fixture().copy(yakuLines=listOf("大三元" to 0,"字一色" to 0),yakumanCount=2,
            score=ScoreCalculator().calculate(30,0,2),totalPoints=66600)
        val dora=SettlementDetailPresenter.rows(b,SettlementSection.DORA).joinToString("\n"){it.text}
        val calc=SettlementDetailPresenter.rows(b,SettlementSection.CALC).joinToString("\n"){it.text}
        assertTrue(dora.contains("不计"),dora)
        assertTrue(calc.contains("8000") && calc.contains("2"),calc)
        assertFalse(calc.contains("2^("),calc)
    }

    @Test fun `each fu contribution is individually readable and multiple honba are multiplied per payer`() {
        val b=fixture().copy(yakuLines=listOf("两立直" to 2,"门前清自摸和" to 1,"役牌 白" to 1),dora=DoraBreakdown(0,0,0),
            fu=FuBreakdown(listOf(FuLine("副底",20),FuLine("幺九暗刻",8),FuLine("中张暗刻",4),FuLine("役牌雀头",2),FuLine("嵌张",2),FuLine("自摸",2)),38,40,40),score=ScoreCalculator().calculate(40,4),totalPoints=10600)
        val rows=SettlementDetailPresenter.rows(b,SettlementSection.CALC)
        for(line in b.fu.lines) assertTrue(rows.any{it.text.contains(line.description) && it.text.contains(line.fu.toString()) && it.text.length<55},line.description)
        assertEquals(b.fu.lines.size,b.fu.lines.map { line -> rows.indexOfFirst{it.text.contains(line.description)} }.toSet().size,"Fu items must occupy individual rows")
        val text=rows.joinToString("\n"){it.text}
        assertTrue(rows.any{it.text.contains("本场") && Regex("(?<![0-9])200(?![0-9])").containsMatchIn(it.text)},"Two honba must show 200 per payer: $text")
        assertTrue(text.contains("2560") && text.contains("2000"),text)
    }

    @Test fun `tab and page clicks only navigate and reset for new settlement and next round`() {
        val state=GameState().apply { phase=GamePhase.ROUND_END }
        val view=PlayerView.fromGameState(state,2,emptyList())
        val session=FakeGameSession(view); val screen=GameScreen(MahjongGame(),session)
        screen.startGame()
        fun emit()=session.update(view,listOf(GameEvent.TsumoWin(1,2,Tile(Suit.MAN,1),listOf("立直"),35,20,32000,fixture())))
        fun click(x:Int,y:Int){screen.touchDown(x,y,0,0)}
        emit(); assertTrue(screen.isSettlementShown)
        assertEquals(SettlementSection.YAKU,screen.settlementSection)
        click(410,530); assertEquals(SettlementSection.DORA,screen.settlementSection)
        val visited=mutableListOf<SettlementDetailRow>()
        repeat(screen.settlementPageCount){visited.addAll(screen.visibleSettlementRows()); click(980,210)}
        assertEquals(SettlementDetailPresenter.rows(fixture(),SettlementSection.DORA),visited)
        assertEquals(screen.settlementPageCount-1,screen.settlementPage)
        click(410,530); assertEquals(0,screen.settlementPage)
        click(240,210); assertEquals(0,screen.settlementPage)
        click(590,530); assertEquals(SettlementSection.CALC,screen.settlementSection)
        click(980,210); emit(); assertEquals(SettlementSection.YAKU,screen.settlementSection); assertEquals(0,screen.settlementPage)
        assertTrue(session.submitted.isEmpty())
        click(640,165); assertFalse(screen.isSettlementShown)
    }

    @Test fun `calculation shows unrounded and rounded payments for all four winner roles`() {
        for(dealer in listOf(false,true)) for(tsumo in listOf(false,true)) {
            val score=ScoreCalculator().calculate(if(tsumo)20 else 30,if(tsumo)2 else 1)
            val b=fixture().copy(yakuLines=if(tsumo)listOf("门前清自摸和" to 1,"平和" to 1)else listOf("平和" to 1),
                dora=DoraBreakdown(0,0,0),fu=FuBreakdown(emptyList(),score.fu,score.fu,score.fu),score=score,
                isDealer=dealer,isTsumo=tsumo,honbaAddition=0,riichiAddition=0,totalPoints=score.totalPoints(dealer,tsumo),paymentLines=emptyList())
            val text=SettlementDetailPresenter.rows(b,SettlementSection.CALC).joinToString("\n"){it.text}
            val expected=when { tsumo && dealer -> listOf(640,700); tsumo -> listOf(640,700,320,400); dealer -> listOf(1440,1500); else -> listOf(960,1000) }
            for(number in expected) assertTrue(Regex("(?<![0-9])$number(?![0-9])").containsMatchIn(text),"dealer=$dealer tsumo=$tsumo missing $number: $text")
        }
    }

    @Test fun `open ron minimum is explained separately from rounding twenty fu`() {
        val b=fixture().copy(yakuLines=listOf("断幺九" to 1),dora=DoraBreakdown(0,0,0),fu=FuBreakdown(listOf(FuLine("副底",20)),20,20,30,"荣和 30符保底"),
            score=ScoreCalculator().calculate(30,1),isTsumo=false,totalPoints=1000,honbaAddition=0,riichiAddition=0,paymentLines=emptyList())
        val rows=SettlementDetailPresenter.rows(b,SettlementSection.CALC)
        assertTrue(rows.any{it.text.contains("保底") && it.text.contains("30")})
        assertFalse(rows.any{it.text.contains("取整至十位") && it.text.endsWith("30 符")},"20 rounded up to ten is 20, the 30 fu minimum is a separate rule")
    }

    @Test fun `legacy win events keep all fourteen entries reachable without invented breakdown`() {
        val state=GameState().apply { phase=GamePhase.ROUND_END }
        val view=PlayerView.fromGameState(state,2,emptyList())
        val session=FakeGameSession(view); val screen=GameScreen(MahjongGame(),session)
        screen.startGame()
        val entries=List(14){"旧记录役种${it+1} 1番"}
        val event=GameEvent.TsumoWin(1,2,Tile(Suit.MAN,1),entries,14,30,32000)
        session.update(view,listOf(event))
        assertEquals(3,screen.settlementPageCount); assertNull(screen.currentSettlementData!!.breakdown)
        val seen=mutableListOf<String>()
        repeat(3){ seen.addAll(screen.visibleSettlementRows().map{it.text}); screen.touchDown(850,275,0,0) }
        for(entry in entries) assertEquals(1,seen.count{it.contains(entry)},entry)
        assertEquals(2,screen.settlementPage)
        screen.touchDown(380,275,0,0); assertEquals(1,screen.settlementPage)
        session.update(view,listOf(event)); assertEquals(0,screen.settlementPage)
        assertTrue(session.submitted.isEmpty())
        val boundary="测".repeat(49)
        val wrapped=screen.wrapLegacyText(boundary,580f,null)
        assertEquals(boundary,wrapped.joinToString(""))
        assertTrue(wrapped.all{it.length*12<=580})
    }
}

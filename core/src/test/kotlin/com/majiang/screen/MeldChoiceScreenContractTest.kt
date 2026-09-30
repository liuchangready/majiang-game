package com.majiang.screen

import com.badlogic.gdx.Input
import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.session.*
import com.majiang.state.*
import com.majiang.testing.*
import com.majiang.testing.MeldChoiceFixtures as F
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MeldChoiceScreenContractTest : GdxHeadlessTestRunner() {
    private fun screen(f:F.Fixture)=GameScreen(MahjongGame(),LocalGameSession(f.seat,f.engine))
    private fun click(s:GameScreen,x:Int,y:Int)=assertTrue(s.touchDown(x,y,0,0))
    private fun card(s:GameScreen,i:Int)=click(s,270+255*i,350)
    private fun confirm(s:GameScreen)=click(s,720,160)

    @Test fun `each of three chi combinations can be independently previewed then committed`() {
        for(i in 0..2) {
            val f=F.chi(); val s=screen(f)
            val before=f.state.players[2].closedTiles.toList()
            val target=f.engine.legalActions(2).filterIsInstance<GameAction.DeclareChi>()[i]
            click(s,500,160); assertTrue(s.meldSelectionModel.isSelecting)
            confirm(s); assertEquals(before,f.state.players[2].closedTiles)
            assertTrue(f.state.players[2].melds.isEmpty())
            card(s,i); assertEquals(before,f.state.players[2].closedTiles)
            confirm(s); f.passOthers()
            assertEquals(target.meld,f.state.players[2].melds.single())
            assertFalse(s.meldSelectionModel.isSelecting)
            assertEquals(11,f.state.players[2].closedTiles.size)
            TestTableBuilder.assertPhysical(f.state); StateInvariants.assertValid(f.state)
            s.dispose()
        }
    }
    @Test fun `cancel and back preserve ron and do not record missed win`() {
        for(mode in 0..2) {
            val f=F.ponRon(); val s=screen(f)
            assertTrue(s.canHumanRon()); click(s,640,160)
            assertFalse(f.state.sameTurnFuriten[2]); assertTrue(f.state.players[2].melds.isEmpty())
            when(mode) { 0->click(s,1100,160); 1->assertTrue(s.keyDown(Input.Keys.ESCAPE)); else->assertTrue(s.keyDown(Input.Keys.BACK)) }
            assertFalse(s.meldSelectionModel.isSelecting); assertTrue(s.canHumanRon())
            assertFalse(f.state.sameTurnFuriten[2]); assertEquals(GamePhase.RESPONSE_WINDOW,f.state.phase)
            click(s,640,160); click(s,550,160); f.passOthers()
            assertEquals(GamePhase.ROUND_END,f.state.phase)
            assertTrue(f.state.players[2].melds.isEmpty()); TestTableBuilder.assertPhysical(f.state)
            s.dispose()
        }
    }
    @Test fun `confirmed pon consumes two owned tiles and still records declining an available ron`() {
        val f=F.ponRon(); val s=screen(f)
        val action=f.engine.legalActions(2).filterIsInstance<GameAction.DeclarePon>().single()
        val called=f.state.lastDiscard!!
        click(s,640,160); assertNotNull(s.meldSelectionModel.confirmationAction())
        assertTrue(f.state.players[2].melds.isEmpty()); confirm(s); f.passOthers()
        assertEquals((action.tiles+called).toSet(),f.state.players[2].melds.single().tiles.toSet())
        assertTrue(f.state.sameTurnFuriten[2]); assertFalse(s.canHumanTsumo())
        TestTableBuilder.assertPhysical(f.state); s.dispose()
    }
    @Test fun `open closed and added kan commit chosen four tiles and draw exactly one rinshan`() {
        for(type in listOf(MeldType.OPEN_KAN,MeldType.CLOSED_KAN,MeldType.ADDED_KAN)) {
            val f=if(type==MeldType.OPEN_KAN) F.openKan() else F.ownKans()
            val s=screen(f); val before=f.state.rinshanPointer
            click(s,350,160)
            val i=s.meldSelectionModel.options.indexOfFirst { it.type==type }
            assertTrue(i>=0); val expected=s.meldSelectionModel.options[i].tiles.toSet()
            card(s,i); assertEquals(before,f.state.rinshanPointer)
            confirm(s); f.passOthers()
            val meld=f.state.players[2].melds.single { it.type==type }
            assertEquals(expected,meld.tiles.toSet()); assertEquals(before+1,f.state.rinshanPointer)
            assertNotNull(f.state.players[2].drawnTile)
            TestTableBuilder.assertPhysical(f.state); StateInvariants.assertValid(f.state); s.dispose()
        }
    }
    @Test fun `hover sorting and hand clicks cannot replace selected action or accidentally discard`() {
        val session=FakeGameSession(F.chi().view()); val s=GameScreen(MahjongGame(),session)
        click(s,500,160); card(s,2); val expected=s.meldSelectionModel.selectedAction
        s.mouseMoved(270,350); click(s,1140,75); click(s,245,80)
        assertTrue(session.submitted.isEmpty()); assertEquals(expected,s.meldSelectionModel.selectedAction)
        confirm(s); assertEquals(listOf(expected),session.submitted)
        s.touchDown(720,160,0,0); assertEquals(1,session.submitted.size); s.dispose()
    }
    @Test fun `explicit modal pass is the only preview action that submits pass`() {
        val session=FakeGameSession(F.ponRon().view()); val s=GameScreen(MahjongGame(),session)
        click(s,640,160); click(s,1100,160); assertTrue(session.submitted.isEmpty())
        click(s,640,160); click(s,900,160)
        assertEquals(listOf(GameAction.Pass(2)),session.submitted)
        assertFalse(s.meldSelectionModel.isSelecting); s.dispose()
    }
    @Test fun `rejected and silently stale confirmations exit without committing another candidate`() {
        for(stale in listOf(false,true)) {
            val base=FakeGameSession(F.chi().view()); var latest=base.view(); var attempts=0
            val session=object:GameSession by base {
                override fun view()=latest
                override fun legalActions()=latest.legalActions
                override fun submit(action:GameAction):ActionResult { attempts++; return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE) }
            }
            val s=GameScreen(MahjongGame(),session); click(s,500,160); card(s,1)
            if(stale) latest=latest.copy(legalActions=listOf(GameAction.Pass(2)))
            confirm(s); assertEquals(if(stale) 0 else 1,attempts)
            assertFalse(s.meldSelectionModel.isSelecting); assertTrue(base.submitted.isEmpty()); s.dispose()
        }
    }
    @Test fun `opening kan choices retains winning self draw shortcut`() {
        val h=F.tiles(Suit.MAN,"111123")+F.tiles(Suit.PIN,"234")+F.tiles(Suit.SOU,"345")+F.tiles(Suit.WIND,"1")
        val state=TestTableBuilder.create(hands=mapOf(2 to h),drawn=2 to Tile(Suit.WIND,1,1))
        val engine=GameStateMachine(state,GameEventBus(),autoPlayAi=false)
        engine.initializeMatch(2)
        val s=GameScreen(MahjongGame(),LocalGameSession(2,engine))
        assertTrue(s.canHumanKan()); assertTrue(s.canHumanTsumo())
        click(s,350,160); click(s,550,160)
        assertEquals(GamePhase.ROUND_END,state.phase); assertEquals(0,state.rinshanPointer)
        TestTableBuilder.assertPhysical(state); s.dispose()
    }
}

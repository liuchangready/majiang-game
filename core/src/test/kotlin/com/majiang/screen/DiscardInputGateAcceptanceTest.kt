package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.render.DiscardAnimation
import com.majiang.session.*
import com.majiang.state.*
import com.majiang.testing.FakeGameSession
import com.majiang.testing.MeldChoiceFixtures
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Codex-owned regression: the authoritative next turn can arrive before presentation catches up. */
class DiscardInputGateAcceptanceTest : GdxHeadlessTestRunner() {
    private fun view(): PlayerView {
        val s=GameState().apply { phase=GamePhase.DISCARD; currentPlayer=2 }
        s.players[2].closedTiles.addAll((1..9).map{Tile(Suit.SOU,it)}+(1..4).map{Tile(Suit.PIN,it)})
        s.players[2].drawnTile=Tile(Suit.PIN,5)
        return PlayerView.fromGameState(s,2,s.players[2].allClosedTiles.map{GameAction.DiscardTile(2,it)})
    }
    private fun addDiscards(v:PlayerView,count:Int=1):PlayerView = v.copy(discards=v.discards.mapIndexed{seat,old->
        if(seat<count) old+Tile(Suit.MAN,seat+1) else old
    })
    private fun clickTile(s:GameScreen,t:Tile?=null) {
        val r=s.refreshHandTileRects().first{t==null || it.tile==t}.rect
        s.touchDown((r.x+r.width/2).toInt(),80,0,0)
    }
    private fun scene(v:PlayerView=view()):Pair<GameScreen,FakeGameSession> {
        val f=FakeGameSession(v); val s=GameScreen(MahjongGame(),f);s.startGame();return s to f
    }

    @Test fun `synchronous next-turn snapshot blocks a second click before any render`() {
        val v=view();val fake=FakeGameSession(v)
        val session=object:GameSession by fake {
            override fun submit(action:GameAction):ActionResult {
                val result=fake.submit(action)
                if(result is ActionResult.Accepted) fake.update(addDiscards(fake.view()))
                return result
            }
        }
        val s=GameScreen(MahjongGame(),session);s.startGame()
        clickTile(s);assertEquals(1,fake.submitted.size)
        // No render/checkDiscardsDiff call between two input events.
        clickTile(s);clickTile(s)
        assertEquals(1,fake.submitted.size,"The next legal turn must not bypass pending playback")
        assertTrue(s.discardQueue.isNotEmpty() || s.currentDiscardAnim!=null)
    }

    @Test fun `all three queued discards must finish before hand and sort unlock`() {
        val (s,f)=scene(); f.update(addDiscards(f.view(),3))
        s.checkDiscardsDiff()
        repeat(3){
            clickTile(s);s.touchDown(1140,75,0,0)
            assertTrue(f.submitted.isEmpty(),"No early discard at animation $it")
            assertFalse(s.isAutoSort,"Sort must not operate hand during playback")
            s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f)
        }
        assertNull(s.currentDiscardAnim);assertTrue(s.discardQueue.isEmpty())
        assertTrue(f.submitted.isEmpty(),"Blocked clicks are discarded, never replayed")
        clickTile(s);assertEquals(1,f.submitted.size)
        s.touchDown(1140,75,0,0);assertTrue(s.isAutoSort)
    }

    @Test fun `response controls wait for animation then ron remains selectable`() {
        val v=view().copy(phase=GamePhase.RESPONSE_WINDOW,currentTurnPlayer=1,
            legalActions=listOf(GameAction.DeclareRon(2),GameAction.Pass(2)))
        val (s,f)=scene(v);f.update(addDiscards(v))
        assertFalse(s.canHumanRon());assertFalse(s.canHumanPass())
        s.touchDown(750,160,0,0);s.touchDown(900,160,0,0);clickTile(s)
        assertTrue(f.submitted.isEmpty())
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f)
        assertTrue(s.canHumanRon());assertTrue(s.canHumanPass())
        s.touchDown(750,160,0,0)
        assertEquals(listOf(GameAction.DeclareRon(2)),f.submitted)
    }

    @Test fun `my turn alone cannot submit a discard without a legal discard action`() {
        val fake=FakeGameSession(view().copy(legalActions=listOf(GameAction.DeclareTsumo(2))))
        val attempted=mutableListOf<GameAction>()
        val session=object:GameSession by fake {
            override fun submit(action:GameAction):ActionResult { attempted.add(action);return fake.submit(action) }
        }
        val s=GameScreen(MahjongGame(),session);s.startGame();clickTile(s)
        assertTrue(attempted.isEmpty(),"Do not even submit unauthorized hand clicks")
        s.touchDown(900,160,0,0);assertEquals(listOf(GameAction.DeclareTsumo(2)),attempted)
    }

    @Test fun `an already open meld confirmation cannot bypass the playback gate`() {
        val v=MeldChoiceFixtures.ponRon().view()
        val (s,f)=scene(v);s.touchDown(620,160,0,0)
        assertTrue(s.meldSelectionModel.isSelecting)
        val intended=s.meldSelectionModel.confirmationAction()!!
        val t=v.discards[1].last()
        s.discardQueue.add(GameScreen.QueuedDiscard(1,t,v.discards[1].lastIndex,s.getDiscardStart(1),s.getDiscardSlotPosition(1,v.discards[1].lastIndex)))
        s.touchDown(720,160,0,0)
        assertTrue(f.submitted.isEmpty())
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f)
        s.touchDown(720,160,0,0)
        assertEquals(listOf(intended),f.submitted)
    }

    @Test fun `all turn buttons report unavailable during playback without deleting legal actions`() {
        val v=view();val tile=v.myAllClosedTiles.first()
        val actions=listOf(GameAction.DeclareTsumo(2),GameAction.DeclareRiichi(2,tile),GameAction.DeclareAbortiveDraw(2),
            GameAction.DeclarePon(2,listOf(tile,tile.copy(copyIndex=1))),
            GameAction.DeclareChi(2,Meld(MeldType.CHI,listOf(tile,Tile(Suit.SOU,2),Tile(Suit.SOU,3)),1,tile)),
            GameAction.DeclareKan(2,(0..3).map{Tile(Suit.DRAGON,1,it)},MeldType.CLOSED_KAN))
        val (s,f)=scene(v.copy(legalActions=actions));f.update(addDiscards(f.view()))
        assertFalse(s.canHumanTsumo());assertFalse(s.canHumanRiichi());assertFalse(s.canHumanAbortiveDraw())
        assertFalse(s.canHumanPon());assertFalse(s.canHumanChi());assertFalse(s.canHumanKan())
        assertEquals(actions,f.legalActions(),"UI lock must not mutate domain eligibility")
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f)
        assertTrue(s.canHumanTsumo());assertTrue(s.canHumanRiichi());assertTrue(s.canHumanAbortiveDraw())
        assertTrue(s.canHumanPon());assertTrue(s.canHumanChi());assertTrue(s.canHumanKan())
    }

    @Test fun `claimed queued tile is removed and cannot keep input locked`() {
        val (s,f)=scene();val before=f.view();f.update(addDiscards(before,2));s.checkDiscardsDiff()
        s.updateDiscardAnimation(0.1f);assertEquals(1,s.discardQueue.size)
        f.update(f.view().copy(discards=f.view().discards.mapIndexed{i,tiles->if(i==1)emptyList() else tiles}))
        s.checkDiscardsDiff();assertTrue(s.discardQueue.isEmpty(),"Claimed unplayed animation must be pruned")
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        assertNull(s.currentDiscardAnim);clickTile(s);assertEquals(1,f.submitted.size)
    }

    @Test fun `new round and settlement clear playback rather than stranding controls`() {
        val (s,f)=scene();val base=f.view();f.update(addDiscards(base,3));s.checkDiscardsDiff();s.updateDiscardAnimation(0.1f)
        f.update(base,listOf(GameEvent.StackingStarted()))
        assertTrue(s.discardQueue.isEmpty());assertNull(s.currentDiscardAnim)
        clickTile(s);assertEquals(1,f.submitted.size)
        f.update(addDiscards(base),listOf(GameEvent.TsumoWin(1,2,Tile(Suit.PIN,5),listOf("自摸"),1,30,1500)))
        assertTrue(s.isSettlementShown);assertTrue(s.discardQueue.isEmpty());assertNull(s.currentDiscardAnim)
        s.touchDown(640,165,0,0);assertFalse(s.isSettlementShown)
    }

    @Test fun `new playback immediately clears a previously highlighted hand tile`() {
        val (s,f)=scene();val slot=s.refreshHandTileRects().first()
        val field=GameScreen::class.java.getDeclaredField("hoveredTile").apply{isAccessible=true}
        s.mouseMoved((slot.rect.x+20).toInt(),80)
        assertEquals(slot.tile,field.get(s))
        f.update(addDiscards(f.view()))
        assertNull(field.get(s),"No misleading hover between update and next render")
        s.mouseMoved((slot.rect.x+20).toInt(),80);assertNull(field.get(s))
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f)
        s.mouseMoved((slot.rect.x+20).toInt(),80);assertEquals(slot.tile,field.get(s))
    }

    @Test fun `selected riichi confirmation is blocked until pending playback completes`() {
        val state=GameState().apply {phase=GamePhase.DISCARD;currentPlayer=2}
        val east=Tile(Suit.WIND,1,1);val river=Tile(Suit.MAN,9,3)
        state.players[2].closedTiles.addAll((1..6).map{Tile(Suit.MAN,it)}+(7..9).map{Tile(Suit.PIN,it)}+(1..3).map{Tile(Suit.SOU,it)}+Tile(Suit.WIND,1))
        state.players[2].drawnTile=east;state.players[0].discards.add(river)
        val action=GameAction.DeclareRiichi(2,east)
        val (s,f)=scene(PlayerView.fromGameState(state,2,listOf(action)))
        s.touchDown(720,160,0,0);assertTrue(s.riichiSelectionModel.isSelecting)
        clickTile(s,east);assertEquals(action,s.riichiSelectionModel.confirmationAction())
        s.discardQueue.add(GameScreen.QueuedDiscard(0,river,0,s.getDiscardStart(0),s.getDiscardSlotPosition(0,0)))
        s.touchDown(720,160,0,0);assertTrue(f.submitted.isEmpty())
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION+0.01f)
        s.touchDown(720,160,0,0);assertEquals(listOf(action),f.submitted)
    }
}

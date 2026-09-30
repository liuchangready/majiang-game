package com.majiang.render

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.model.*
import com.majiang.screen.GameScreen
import com.majiang.session.*
import com.majiang.state.*
import com.majiang.testing.*
import com.majiang.testing.MeldChoiceFixtures as F
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MeldDisplayContractTest : GdxHeadlessTestRunner() {
    private fun meld(type:MeldType):Meld {
        val tiles=when(type) {
            MeldType.CHI->F.tiles(Suit.MAN,"234")
            MeldType.PON->F.tiles(Suit.PIN,"777")
            MeldType.OPEN_KAN->F.tiles(Suit.SOU,"8888")
            MeldType.CLOSED_KAN->F.tiles(Suit.DRAGON,"1111")
            MeldType.ADDED_KAN->F.tiles(Suit.WIND,"2222")
        }
        return Meld(type,tiles,if(type==MeldType.CLOSED_KAN)-1 else 1,tiles.first())
    }
    private fun view(melds:List<Meld>,seat:Int=2):PlayerView {
        val s=TestTableBuilder.create(melds=mapOf(seat to melds),drawn=seat to Tile(Suit.WIND,4,3))
        return PlayerView.fromGameState(s,seat,emptyList())
    }
    @Test fun `every meld type displays each exact physical tile once`() {
        for(type in MeldType.values()) {
            val source=meld(type);val v=view(listOf(source));val g=MeldDisplayLayout.layout(v).single()
            assertEquals(type,g.type);assertEquals(source.tiles,g.tiles.map { it.tile })
            assertEquals(if(type in listOf(MeldType.CHI,MeldType.PON))3 else 4,g.tiles.size)
            assertEquals(source.tiles.size,g.tiles.map { it.tile }.toSet().size)
            if(type==MeldType.CLOSED_KAN) { assertNull(g.fromPlayer);assertTrue(g.tiles.none { it.called||it.added }) }
            else { assertEquals(1,g.fromPlayer);assertEquals(source.calledTile,g.tiles.single { it.called }.tile) }
        }
    }
    @Test fun `closed kan hides outer tiles and added kan marks only appended fourth tile`() {
        val closed=MeldDisplayLayout.layout(view(listOf(meld(MeldType.CLOSED_KAN)))).single()
        assertEquals(listOf(true,false,false,true),closed.tiles.map { it.faceDown })
        val added=MeldDisplayLayout.layout(view(listOf(meld(MeldType.ADDED_KAN)))).single()
        assertTrue(added.tiles.none { it.faceDown });assertEquals(1,added.tiles.count { it.added })
        assertEquals(Tile(Suit.WIND,2,3),added.tiles.single { it.added }.tile)
        assertFalse(added.tiles.single { it.added }.called)
    }
    @Test fun `observer layout ignores other seats and never mutates input`() {
        val own=meld(MeldType.CHI);val other=meld(MeldType.PON)
        val v=view(listOf(own)).copy(melds=listOf(listOf(other),emptyList(),listOf(own),emptyList()))
        val before=v.copy();val groups=MeldDisplayLayout.layout(v)
        assertEquals(listOf(MeldType.CHI),groups.map { it.type });assertEquals(before,v)
        assertEquals(listOf(MeldType.PON),MeldDisplayLayout.layout(v.copy(viewerSeat=0)).map { it.type })
        assertTrue(MeldDisplayLayout.layout(v.copy(viewerSeat=3)).isEmpty())
    }
    @Test fun `layout snapshots and nested tile lists are immutable`() {
        val source=meld(MeldType.PON);val tiles=source.tiles.toMutableList()
        val base=view(listOf(source));val list=mutableListOf(source.copy(tiles=tiles))
        val groups=MeldDisplayLayout.layout(base.copy(melds=listOf(emptyList(),emptyList(),list,emptyList())))
        tiles.clear();list.clear();assertEquals(3,groups.single().tiles.size)
        assertThrows(UnsupportedOperationException::class.java){(groups as MutableList).clear()}
        assertThrows(UnsupportedOperationException::class.java){(groups.single().tiles as MutableList).clear()}
    }
    @Test fun `one to four kans fit beside actual hand slots with sorting on and off`() {
        val kans=listOf(Suit.MAN,Suit.PIN,Suit.SOU,Suit.DRAGON).map { suit->
            val tiles=(0..3).map { Tile(suit,1,it) };Meld(MeldType.CLOSED_KAN,tiles,-1,tiles.first()) }
        for(n in 1..4) {
            val session=FakeGameSession(view(kans.take(n)));val screen=GameScreen(MahjongGame(),session)
            repeat(2) {
                val hand=screen.refreshHandTileRects();val groups=screen.displayedMelds()
                assertEquals(n,groups.size);val all=groups.flatMap { it.tiles }
                assertTrue(all.all { it.x>=220 && it.x+it.width<=1048.01f && it.y>=25 && it.y+it.height<145 })
                assertTrue(hand.all { h->all.none { t-> h.rect.overlaps(com.badlogic.gdx.math.Rectangle(t.x,t.y,t.width,t.height)) } })
                for(a in all.indices) for(b in a+1 until all.size) {
                    val x=all[a];val y=all[b]
                    assertFalse(com.badlogic.gdx.math.Rectangle(x.x,x.y,x.width,x.height).overlaps(com.badlogic.gdx.math.Rectangle(y.x,y.y,y.width,y.height)))
                }
                assertEquals(1048f,all.maxOf { it.x+it.width },0.01f)
                val before=groups;screen.touchDown(1140,75,0,0);assertEquals(before,screen.displayedMelds())
            }
            screen.dispose()
        }
    }
    @Test fun `renderer uses real tile skin for faces and backs and labels called and added tiles`() {
        val groups=MeldDisplayLayout.layout(view(listOf(meld(MeldType.CHI),meld(MeldType.PON),meld(MeldType.CLOSED_KAN),meld(MeldType.ADDED_KAN))))
        val faces=mutableListOf<Tile>();var backs=0
        val skin=object:TileSkin {
            override val id=SkinId.CLASSIC;override val displayName="recording"
            override fun appendTileDraw(buf:TileDrawBuffer,tile:Tile,x:Float,y:Float,width:Float,height:Float,isHover:Boolean,isDrawn:Boolean){faces.add(tile)}
            override fun appendTileBackDraw(buf:TileDrawBuffer,x:Float,y:Float,width:Float,height:Float){backs++}
        }
        val buffer=TileDrawBuffer();MeldDisplayRenderer.append(buffer,skin,groups)
        assertEquals(2,backs);assertEquals(groups.flatMap { it.tiles }.filterNot { it.faceDown }.map { it.tile },faces)
        assertTrue(buffer.textDraws.any { "CALLED" in it.text });assertTrue(buffer.textDraws.any { "ADD" in it.text })
        assertTrue(buffer.textDraws.any { "CLOSED" in it.text });assertTrue(buffer.textDraws.any { "FROM" in it.text })
    }
    @Test fun `completed meld tiles are not clickable discards and updates clear stale groups`() {
        val v=view(listOf(meld(MeldType.PON)))
        val session=FakeGameSession(v.copy(legalActions=v.myAllClosedTiles.map { GameAction.DiscardTile(2,it) }))
        val screen=GameScreen(MahjongGame(),session)
        for(t in screen.displayedMelds().flatMap { it.tiles })screen.touchDown((t.x+t.width/2).toInt(),(t.y+t.height/2).toInt(),0,0)
        assertTrue(session.submitted.isEmpty())
        session.update(v.copy(melds=List(4){emptyList()}))
        assertTrue(screen.displayedMelds().isEmpty());screen.dispose()
    }
    @Test fun `real confirmed chi pon and all kan types immediately appear in screen view`() {
        for(type in MeldType.values()) {
            val f=when(type){MeldType.CHI->F.chi();MeldType.PON->F.ponRon();MeldType.OPEN_KAN->F.openKan();else->F.ownKans()}
            f.passOthers()
            val session=LocalGameSession(2,f.engine);val screen=GameScreen(MahjongGame(),session)
            val x=when(type){MeldType.CHI->500;MeldType.PON->640;else->350}
            assertTrue(screen.touchDown(x,160,0,0))
            val index=screen.meldSelectionModel.options.indexOfFirst { it.type==type }
            assertTrue(index>=0);assertTrue(screen.touchDown(270+255*index,350,0,0))
            assertTrue(screen.touchDown(720,160,0,0))
            // Added-kan response windows can need other seats to explicitly pass in this non-auto fixture.
            f.passOthers()
            val actual=screen.displayedMelds()
            if(f.state.phase==GamePhase.CHANKAN_WINDOW) fail<Unit>("Unresolved kan response")
            val expected=f.state.players[2].melds
            assertEquals(expected.map { it.type },actual.map { it.type })
            assertEquals(expected.flatMap { it.tiles },actual.flatMap { g->g.tiles.map { it.tile } })
            assertTrue(actual.any { it.type==type });TestTableBuilder.assertPhysical(f.state)
            screen.dispose()
        }
    }
}

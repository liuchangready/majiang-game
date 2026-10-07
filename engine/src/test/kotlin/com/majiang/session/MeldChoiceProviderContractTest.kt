package com.majiang.session

import com.majiang.model.*
import com.majiang.state.*
import com.majiang.testing.MeldChoiceFixtures as F
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MeldChoiceProviderContractTest {
    @Test fun `all three chi choices show complete sequences and exact two owned tiles`() {
        val v=F.chi().view()
        val options=MeldChoiceProvider.options(v,MeldChoiceKind.CHI)
        assertEquals(setOf(listOf(3,4,5),listOf(4,5,6),listOf(5,6,7)),options.map { it.tiles.map { t->t.rank }.sorted() }.toSet())
        for(o in options) {
            assertEquals(MeldType.CHI,o.type); assertEquals(3,o.tiles.size); assertEquals(2,o.handTiles.size)
            assertEquals(v.lastDiscard,o.calledTile); assertEquals(1,o.fromPlayer)
            assertTrue(v.myAllClosedTiles.containsAll(o.handTiles)); assertFalse(o.calledTile in o.handTiles)
            assertTrue(o.action in v.legalActions)
        }
    }
    @Test fun `pon and open kan include the physical called tile exactly once`() {
        val v=F.openKan().view()
        for((kind,count) in listOf(MeldChoiceKind.PON to 3,MeldChoiceKind.KAN to 4)) {
            val o=MeldChoiceProvider.options(v,kind).single()
            assertEquals(count,o.tiles.size); assertEquals(count-1,o.handTiles.size)
            assertEquals(1,o.tiles.count { it==v.lastDiscard }); assertEquals(v.lastDiscard,o.calledTile)
            assertEquals(1,o.fromPlayer); assertNull(o.addedTile)
        }
    }
    @Test fun `closed and added kan remain distinct and added kan highlights only the fourth tile`() {
        val v=F.ownKans().view()
        val options=MeldChoiceProvider.options(v,MeldChoiceKind.KAN)
        assertEquals(setOf(MeldType.CLOSED_KAN,MeldType.ADDED_KAN),options.map { it.type }.toSet())
        val closed=options.single { it.type==MeldType.CLOSED_KAN }
        assertEquals(4,closed.handTiles.size); assertNull(closed.calledTile); assertNull(closed.fromPlayer)
        val added=options.single { it.type==MeldType.ADDED_KAN }
        assertEquals(4,added.tiles.size); assertEquals(4,added.tiles.toSet().size)
        assertEquals(listOf(v.myDrawnTile),added.handTiles); assertEquals(v.myDrawnTile,added.addedTile)
        assertEquals(v.melds[2].single().calledTile,added.calledTile); assertEquals(0,added.fromPlayer)
        assertEquals(v.melds[2].single().tiles.toSet()+v.myDrawnTile,added.tiles.toSet())
    }
    @Test fun `missing response target and other seat actions cannot become choices`() {
        val v=F.openKan().view()
        assertTrue(MeldChoiceProvider.options(v.copy(lastDiscard=null),MeldChoiceKind.PON).isEmpty())
        assertTrue(MeldChoiceProvider.options(v.copy(lastDiscardPlayer=2),MeldChoiceKind.KAN).isEmpty())
        val other=(v.legalActions.filterIsInstance<GameAction.DeclarePon>().single()).copy(player=0)
        assertTrue(MeldChoiceProvider.options(v.copy(legalActions=listOf(other)),MeldChoiceKind.PON).isEmpty())
        assertTrue(MeldChoiceProvider.options(v.copy(legalActions=emptyList()),MeldChoiceKind.PON).isEmpty())
    }
    @Test fun `options are detached immutable snapshots including nested action lists`() {
        val v=F.chi().view()
        val original=v.legalActions.filterIsInstance<GameAction.DeclareChi>().first()
        val source=original.meld.tiles.toMutableList()
        val action=original.copy(meld=original.meld.copy(tiles=source))
        val options=MeldChoiceProvider.options(v.copy(legalActions=listOf(action,action)),MeldChoiceKind.CHI)
        assertEquals(1,options.size)
        source.clear()
        val o=options.single()
        assertEquals(3,o.tiles.size); assertEquals(3,(o.action as GameAction.DeclareChi).meld.tiles.size)
        assertThrows(UnsupportedOperationException::class.java) { (options as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (o.tiles as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (o.handTiles as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (o.action.meld.tiles as MutableList).clear() }
    }
    @Test fun `unowned physical copies and incomplete added kan cannot fabricate candidates`() {
        val v=F.openKan().view()
        val bad=GameAction.DeclarePon(2,listOf(Tile(Suit.DRAGON,1),Tile(Suit.DRAGON,1,3)))
        assertTrue(MeldChoiceProvider.options(v.copy(legalActions=listOf(bad)),MeldChoiceKind.PON).isEmpty())
        val added=F.ownKans().view()
        val noMeld=added.copy(melds=List(4){emptyList()})
        assertFalse(MeldChoiceProvider.options(noMeld,MeldChoiceKind.KAN).any { it.type==MeldType.ADDED_KAN })
    }
}

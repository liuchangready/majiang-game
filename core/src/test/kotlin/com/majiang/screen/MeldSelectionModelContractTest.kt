package com.majiang.screen

import com.majiang.model.*
import com.majiang.session.*
import com.majiang.state.GameAction
import com.majiang.testing.MeldChoiceFixtures as F
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MeldSelectionModelContractTest {
    @Test fun `multiple choices require selection and confirm returns exact selected action`() {
        val m=MeldSelectionModel(); val v=F.chi().view(); m.update(v)
        assertFalse(m.isSelecting); assertNull(m.confirmationAction())
        assertTrue(m.begin(MeldChoiceKind.CHI)); assertNull(m.selectedAction)
        assertNull(m.confirmationAction()); val last=m.options.last().action
        assertTrue(m.select(last)); assertEquals(last,m.confirmationAction())
        assertFalse(m.select(GameAction.Pass(2))); assertEquals(last,m.confirmationAction())
        assertTrue(m.begin(MeldChoiceKind.CHI)); assertEquals(last,m.confirmationAction())
    }
    @Test fun `single choice preselects but cancellation and unavailable categories never confirm`() {
        val m=MeldSelectionModel(); m.update(F.ponRon().view())
        assertFalse(m.begin(MeldChoiceKind.KAN))
        assertTrue(m.begin(MeldChoiceKind.PON)); assertNotNull(m.confirmationAction())
        m.cancel(); assertFalse(m.isSelecting); assertNull(m.selectedAction); assertNull(m.confirmationAction())
    }
    @Test fun `equal snapshot preserves selection and actual update invalidates it`() {
        val m=MeldSelectionModel(); val v=F.chi().view(); m.update(v); m.begin(MeldChoiceKind.CHI)
        val action=m.options.last().action; m.select(action); m.update(v.copy())
        assertEquals(action,m.confirmationAction())
        m.update(v.copy(wallRemaining=v.wallRemaining-1))
        assertFalse(m.isSelecting); assertNull(m.confirmationAction())
    }
    @Test fun `pages expose all admitted physical choices and never confirm an off page choice`() {
        val v=F.chi().view()
        val owned=F.tiles(Suit.MAN,"4466")
        val called=Tile(Suit.MAN,5)
        val actions=owned.filter { it.rank==4 }.flatMap { a -> owned.filter { it.rank==6 }.map { b ->
            GameAction.DeclareChi(2,Meld(MeldType.CHI,listOf(a,called,b),1,called)) } }
        val m=MeldSelectionModel(); m.update(v.copy(myClosedTiles=owned,legalActions=actions)); m.begin(MeldChoiceKind.CHI)
        assertEquals(2,m.pageCount); assertEquals(3,m.visibleOptions.size)
        assertFalse(m.select(actions.last())); assertTrue(m.select(actions.first()))
        assertFalse(m.setPage(-1)); assertEquals(actions.first(),m.confirmationAction())
        assertTrue(m.setPage(1)); assertNull(m.selectedAction); assertEquals(listOf(actions.last()),m.visibleOptions.map { it.action })
        assertTrue(m.select(actions.last())); assertEquals(actions.last(),m.confirmationAction())
        assertFalse(m.setPage(2)); assertEquals(1,m.pageIndex)
    }
}

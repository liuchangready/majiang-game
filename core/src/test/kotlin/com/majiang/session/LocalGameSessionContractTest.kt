package com.majiang.session

import com.majiang.event.*
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.random.Random

class LocalGameSessionContractTest {
    private data class Fixture(val state: GameState, val session: LocalGameSession)
    private fun fixture(): Fixture {
        val state = GameState()
        val bus = GameEventBus()
        val engine = GameStateMachine(state, bus, autoPlayAi = false, random = Random(32))
        engine.initializeMatch(2)
        return Fixture(state, LocalGameSession(viewerSeat = 2, engine = engine, eventBus = bus))
    }
    private fun assertPrivateEventsBelongToViewer(events: List<GameEvent>) {
        for (event in events) when (event) {
            is GameEvent.TileDealt -> assertEquals(2, event.player)
            is GameEvent.TileDrawn -> assertEquals(2, event.player)
            is GameEvent.RinshanDraw -> assertEquals(2, event.player)
            else -> Unit
        }
    }

    @Test
    fun `both command return and subscription redact other seats' private tiles`() {
        val f = fixture()
        val updates = mutableListOf<SessionUpdate>()
        f.session.subscribe { updates.add(it) }
        val result = f.session.start()
        assertTrue(result is ActionResult.Accepted)
        assertPrivateEventsBelongToViewer((result as ActionResult.Accepted).events)
        assertTrue(updates.isNotEmpty())
        assertPrivateEventsBelongToViewer(updates.flatMap { it.visibleEvents })
        assertEquals(f.state.players[2].allClosedTiles, f.session.view().myAllClosedTiles)
    }

    @Test
    fun `session cannot submit another seat's otherwise legal discard`() {
        val f = fixture()
        f.session.start()
        val before = f.state.players[0].allClosedTiles.toList()
        val result = f.session.submit(GameAction.DiscardTile(0, f.state.players[0].drawnTile!!))
        assertTrue(result is ActionResult.Rejected)
        assertEquals(before, f.state.players[0].allClosedTiles)
    }

    @Test
    fun `system actions are not accepted through player submit`() {
        val f = fixture()
        f.session.start()
        assertTrue(f.session.submit(GameAction.StartStacking()) is ActionResult.Rejected)
        assertTrue(f.session.submit(GameAction.DealComplete) is ActionResult.Rejected)
    }

    @Test
    fun `next round cannot interrupt an active round`() {
        val f = fixture()
        f.session.start()
        val before = f.session.view()
        assertTrue(f.session.nextRound() is ActionResult.Rejected)
        assertEquals(before, f.session.view())
    }

    @Test
    fun `unsubscribed observer receives no updates`() {
        val f = fixture()
        val updates = mutableListOf<SessionUpdate>()
        val subscription = f.session.subscribe { updates.add(it) }
        subscription.close()
        updates.clear()
        f.session.start()
        assertTrue(updates.isEmpty())
    }

    @Test
    fun `initial observer exception does not escape or prevent other observers`() {
        val f = fixture()
        assertDoesNotThrow { f.session.subscribe { error("bad initial observer") } }
        val updates = mutableListOf<SessionUpdate>()
        f.session.subscribe { updates.add(it) }
        updates.clear()
        assertTrue(f.session.start() is ActionResult.Accepted)
        assertTrue(updates.isNotEmpty())
    }

    @Test
    fun `closed session rejects commands and never starts its engine`() {
        val f = fixture()
        f.session.close()
        assertTrue(f.session.start() is ActionResult.Rejected)
        assertTrue(f.session.nextRound() is ActionResult.Rejected)
        assertTrue(f.session.submit(GameAction.Pass(2)) is ActionResult.Rejected)
        assertTrue(f.state.wall.isEmpty())
    }
}

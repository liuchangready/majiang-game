package com.majiang.session

import com.majiang.ai.*
import com.majiang.event.*
import com.majiang.model.*
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SeatControllerSessionAcceptanceTest {
    private fun humans(): MutableList<SeatController> = mutableListOf(
        SeatController.LocalHuman(false), SeatController.RemoteHuman(false),
        SeatController.RemoteHuman(false), SeatController.RemoteHuman(false))

    @Test fun `start preserves a previously configured multi human engine`() {
        val s = GameState()
        val e = GameStateMachine(s, GameEventBus())
        val config = humans()
        e.initializeMatch(config)
        val session = LocalGameSession(0, e)
        assertTrue(session.start() is ActionResult.Accepted)
        assertEquals(config, (0..3).map(e::controllerFor))
        assertTrue(s.players.all { it.discards.isEmpty() })
        assertTrue(session.legalActions().any { it is GameAction.DiscardTile })
    }

    @Test fun `legacy explicit AI strategy survives local session startup`() {
        val e = GameStateMachine(GameState(), GameEventBus())
        val strategy = TrivialAIStrategy()
        e.initializeMatch(0, mapOf(1 to AIPlayer(1, strategy)))
        val session = LocalGameSession(0, e)
        assertTrue(session.start() is ActionResult.Accepted)
        assertSame(strategy, (e.controllerFor(1) as SeatController.Computer).strategy)
    }

    @Test fun `constructor controller list is detached and overrides default setup`() {
        val s = GameState()
        val e = GameStateMachine(s, GameEventBus())
        val config = humans()
        val session = LocalGameSession(viewerSeat = 2, engine = e, controllers = config)
        config[1] = SeatController.Computer()
        assertTrue(session.start() is ActionResult.Accepted)
        assertEquals(SeatController.RemoteHuman(false), e.controllerFor(1))
        assertEquals(SeatController.RemoteHuman(false), e.controllerFor(2))
        assertEquals(0, s.currentPlayer)
        assertTrue(s.players.all { it.discards.isEmpty() })
    }

    @Test fun `takeover hides actionable controls and prevents a stale UI command`() {
        val s = TestTableBuilder.create(drawn = 1 to Tile(Suit.WIND, 4))
        val e = GameStateMachine(s, GameEventBus(), autoPlayAi = false)
        e.initializeMatch(humans())
        val session = LocalGameSession(1, e)
        val action = session.legalActions().filterIsInstance<GameAction.DiscardTile>().first()
        val tiles = session.view().myAllClosedTiles
        assertTrue(e.setSeatController(1, SeatController.Takeover()) is ActionResult.Accepted)
        assertTrue(session.legalActions().isEmpty())
        assertTrue(session.view().legalActions.isEmpty())
        assertEquals(tiles, session.view().myAllClosedTiles)
        assertEquals(ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE), session.submit(action))
        assertTrue(e.setSeatController(1, SeatController.RemoteHuman(false)) is ActionResult.Accepted)
        assertTrue(action in session.legalActions())
        assertTrue(session.submit(action) is ActionResult.Accepted)
        TestTableBuilder.assertPhysical(s)
    }

    @Test fun `default session still creates one local player at the requested seat`() {
        val e = GameStateMachine(GameState(), GameEventBus(), autoPlayAi = false)
        val session = LocalGameSession(2, e)
        assertTrue(session.start() is ActionResult.Accepted)
        assertTrue(e.controllerFor(2) is SeatController.LocalHuman)
        assertTrue(listOf(0, 1, 3).all { e.controllerFor(it) is SeatController.Computer })
    }

    @Test fun `seat configuration made before start is not reset to AI`() {
        val e = GameStateMachine(GameState(), GameEventBus())
        assertTrue(e.setSeatController(1, SeatController.RemoteHuman(false)) is ActionResult.Accepted)
        assertTrue(e.hasControllerConfiguration)
        val session = LocalGameSession(0, e)
        assertTrue(session.start() is ActionResult.Accepted)
        assertEquals(SeatController.RemoteHuman(false), e.controllerFor(1))
    }
}

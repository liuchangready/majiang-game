package com.majiang.architecture

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.application.*
import com.majiang.client.EndpointCapabilities
import com.majiang.client.PlayerEndpoint
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.screen.GameScreen
import com.majiang.session.PlayerView
import com.majiang.state.*
import com.majiang.testing.FakeGameSession
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ClientSelectionAcceptanceTest : GdxHeadlessTestRunner() {
    @Test fun `UI never replaces a selected physical tile with another copy of the same type`() {
        val selected = GameAction.DiscardTile(0, Tile(Suit.PIN, 5, 0))
        val different = GameAction.DiscardTile(0, Tile(Suit.PIN, 5, 1))
        val state = GameState().also { it.phase = GamePhase.DISCARD }
        val legacy = FakeGameSession(PlayerView.fromGameState(state, 0, listOf(selected)))
        val choices = mutableListOf<ActionChoice>()
        val endpoint = object : PlayerEndpoint by legacy {
            override val capabilities = EndpointCapabilities(decisions = true)
            override fun queryDecision() = PlayerDecision("r", "d", 1, listOf(ActionOption("other-copy", different)))
            override fun submitChoice(choice: ActionChoice): ActionResult {
                choices.add(choice)
                return ActionResult.Accepted()
            }
        }
        val screen = GameScreen(MahjongGame(), endpoint = endpoint)
        assertTrue(screen.submitAction(selected) is ActionResult.Rejected)
        assertTrue(choices.isEmpty())
        assertTrue(legacy.submitted.isEmpty())
    }

    @Test fun `choice transport exception does not trigger a second legacy submission`() {
        val selected = GameAction.DiscardTile(0, Tile(Suit.PIN, 5))
        val state = GameState().also { it.phase = GamePhase.DISCARD }
        val legacy = FakeGameSession(PlayerView.fromGameState(state, 0, listOf(selected)))
        var choices = 0
        val endpoint = object : PlayerEndpoint by legacy {
            override val capabilities = EndpointCapabilities(decisions = true)
            override fun queryDecision() = PlayerDecision("r", "d", 1, listOf(ActionOption("tile", selected)))
            override fun submitChoice(choice: ActionChoice): ActionResult {
                choices++
                throw IllegalStateException("Simulated response loss after remote acceptance")
            }
        }
        val screen = GameScreen(MahjongGame(), endpoint = endpoint)
        runCatching { screen.submitAction(selected) }
        assertEquals(1, choices)
        assertTrue(legacy.submitted.isEmpty(), "Never retry a choice by submitting an unversioned raw action")
    }
}

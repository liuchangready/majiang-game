package com.majiang.state

import com.majiang.ai.*
import com.majiang.event.*
import com.majiang.session.PlayerView
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.random.Random

class MixedAiInvariantContractTest {
    @Test
    fun `mixed strategies preserve physical and monetary invariants at every command`() {
        for (seed in listOf(7, 42, 2026)) {
            val state = GameState()
            val bus = GameEventBus()
            val fsm = GameStateMachine(state, bus, autoPlayAi = false, random = Random(seed))
            fsm.initializeMatch(-1)
            val strategies = listOf(BasicAIStrategy(), TrivialAIStrategy(), BasicAIStrategy(), TrivialAIStrategy())
            repeat(2) { round ->
                assertTrue(fsm.handleAction(GameAction.StartStacking()) is ActionResult.Accepted)
                var steps = 0
                val trace = mutableListOf<GameAction>()
                while (state.phase !in setOf(GamePhase.ROUND_END, GamePhase.MATCH_END)) {
                    assertTrue(steps++ < 600, "seed=$seed round=$round did not terminate; trace=$trace")
                    val seats = (0..3).map { (it + seed) % 4 }
                    val actor = seats.firstOrNull { fsm.legalActions(it).isNotEmpty() }
                    assertNotNull(actor, "seed=$seed phase=${state.phase} has no legal continuation; trace=$trace")
                    val seat = actor!!
                    val actions = fsm.legalActions(seat)
                    val view = PlayerView.fromGameState(state, seat, actions)
                    val chosen = strategies[seat].decideAction(view, actions)
                    assertNotNull(chosen, "seed=$seed seat=$seat strategy returned no action")
                    assertTrue(chosen in actions)
                    trace.add(chosen!!)
                    assertTrue(fsm.handleAction(chosen) is ActionResult.Accepted, "seed=$seed trace=$trace")
                    TestTableBuilder.assertPhysical(state)
                    StateInvariants.assertValid(state)
                    assertEquals(100000, state.scores.sum() + state.riichiSticks * 1000, "seed=$seed trace=$trace")
                }
            }
        }
    }
}

package com.majiang.state

import com.majiang.ai.AIStrategy
import com.majiang.presentation.game.GameScreen
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Architectural acceptance: consumers must not receive write-capable domain objects. */
class ArchitectureBoundaryContractTest {
    private val forbidden = listOf(
        "com.majiang.state.GameState",
        "com.majiang.model.PlayerHand", "com.majiang.model.TilePool",
        "com.majiang.application.ApplicationCoordinator",
        "com.majiang.rules.RiichiRuleEngine",
        "com.majiang.host.TableHost"
    )

    @Test
    fun `AI strategy public signatures do not accept mutable authority state`() {
        val leaks = AIStrategy::class.java.methods.filter { method ->
            val signature = method.toGenericString()
            forbidden.any { signature.contains(it) }
        }
        assertTrue(leaks.isEmpty(), "AI must decide from its own view and legal actions: $leaks")
    }

    @Test
    fun `GameScreen does not publicly expose mutable state or engine`() {
        val leaks = GameScreen::class.java.methods.filter { method ->
            val signature = method.toGenericString()
            forbidden.any { signature.contains(it) }
        }
        assertTrue(leaks.isEmpty(), "Screen must not expose domain write authority: $leaks")
    }

    @Test
    fun `GameScreen does not privately construct a parallel rule authority`() {
        val leaks = GameScreen::class.java.declaredFields.filter {
            forbidden.any { type -> it.type.name == type }
        }
        assertTrue(leaks.isEmpty(), "Action availability must come from injected client.api GameSession: $leaks")
    }
}

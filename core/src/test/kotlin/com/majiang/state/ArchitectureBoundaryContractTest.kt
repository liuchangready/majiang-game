package com.majiang.state

import com.majiang.ai.AIStrategy
import com.majiang.screen.GameScreen
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Architectural acceptance: consumers must not receive write-capable domain objects. */
class ArchitectureBoundaryContractTest {
    private val forbidden = listOf(
        "com.majiang.state.GameState", "com.majiang.state.GameStateMachine",
        "com.majiang.model.PlayerHand", "com.majiang.model.TilePool"
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
            forbidden.any { method.genericReturnType.typeName.contains(it) }
        }
        assertTrue(leaks.isEmpty(), "Screen must not expose domain write authority: $leaks")
    }

    @Test
    fun `GameScreen does not privately construct a parallel rule authority`() {
        val leaks = GameScreen::class.java.declaredFields.filter {
            it.type.name == "com.majiang.rules.RiichiRuleEngine"
        }
        assertTrue(leaks.isEmpty(), "Action availability must come from the injected GameSession")
    }
}

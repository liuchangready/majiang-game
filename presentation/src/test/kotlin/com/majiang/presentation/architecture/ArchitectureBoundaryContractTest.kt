package com.majiang.presentation.architecture

import com.majiang.ai.AIStrategy
import com.majiang.presentation.game.GameScreen
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.File

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

    @Test
    fun `presentation source files strictly respect architectural layer boundaries`() {
        val candidates = listOf(
            File("src/main/kotlin/com/majiang/presentation"),
            File("presentation/src/main/kotlin/com/majiang/presentation")
        )
        val scanDir = candidates.firstOrNull { it.isDirectory }
        assertNotNull(scanDir, "Scan root directory src/main/kotlin/com/majiang/presentation must exist")

        val ktFiles = scanDir!!.walkTopDown().filter { it.isFile && it.extension == "kt" }.toList()
        assertTrue(ktFiles.isNotEmpty(), "Scan root directory must contain at least one .kt file")

        val enginePackages = listOf(
            "model", "state", "round", "rules", "cheat", "settlement", "timing",
            "stacking", "access", "ai", "application", "authority", "control",
            "economy", "event", "host", "match", "projection", "session"
        )
        val enginePrefixes = enginePackages.map { "com.majiang.$it." }
        val otherForbiddenPrefixes = listOf(
            "com.majiang.server.",
            "com.majiang.transport.",
            "com.majiang.network."
        )

        val importRegex = Regex("""(?m)^\s*import\s+(?:static\s+)?([^\s;]+)""")
        val violations = mutableListOf<String>()

        for (file in ktFiles) {
            val content = file.readText()
            for (match in importRegex.findAll(content)) {
                val imp = match.groupValues[1]
                val isForbiddenClient = imp.startsWith("com.majiang.client.") && !imp.startsWith("com.majiang.client.api.")
                val isForbiddenOther = otherForbiddenPrefixes.any { imp.startsWith(it) }
                val isForbiddenEngine = enginePrefixes.any { imp.startsWith(it) }

                if (isForbiddenClient || isForbiddenOther || isForbiddenEngine) {
                    violations.add("${file.name}: forbidden import $imp")
                }
            }
        }

        assertTrue(violations.isEmpty(), "Found forbidden architectural imports:\n" + violations.joinToString("\n"))
    }
}

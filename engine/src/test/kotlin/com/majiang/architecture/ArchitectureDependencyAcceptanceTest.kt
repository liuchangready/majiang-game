package com.majiang.architecture

import java.nio.file.Files
import java.nio.file.Path
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** These boundaries are part of the requested architecture, independently of gameplay outputs. */
class ArchitectureDependencyAcceptanceTest {
    private fun source(relative: String): String {
        val path = Path.of("src/main/kotlin", relative)
        return Files.readString(path)
    }

    @Test fun `round authority does not depend on match policy AI validation auditing or presentation`() {
        val text = source("com/majiang/round/RoundStateMachine.kt")
        for (forbidden in listOf("import com.majiang.ai.", "import com.majiang.application.",
            "import com.majiang.session.PlayerView", "import com.majiang.state.LegalActionProvider",
            "import com.majiang.state.StateInvariants", "import com.majiang.match.MatchProgression",
            "import com.majiang.event.GameEventBus")) {
            assertFalse(text.contains(forbidden), "Round must not depend on $forbidden")
        }
        assertFalse(Regex("fun\\s+(nextRound|initializeMatch|submitPlayerAction|legalActions)\\s*\\(").containsMatchIn(text))
    }

    @Test fun `session domain does not calculate rules validate requests or drive AI`() {
        val text = source("com/majiang/match/MatchSession.kt")
        for (forbidden in listOf("import com.majiang.ai.", "import com.majiang.rules.",
            "import com.majiang.state.LegalActionProvider", "import com.majiang.state.StateInvariants",
            "import com.majiang.event.GameEventBus", "import com.majiang.application.")) {
            assertFalse(text.contains(forbidden), "Session must not depend on $forbidden")
        }
        assertFalse(Regex("MatchProgression\\s*\\(").containsMatchIn(text), "Progression policy belongs outside Session")
    }
}

package com.majiang.mr09scan

import java.nio.file.Files
import java.nio.file.Path
import org.junit.jupiter.api.Test
import kotlin.test.assertTrue

class Mr09RiichiWriteFunnelScanTest {
    private val root = Path.of(System.getProperty("user.dir")).toAbsolutePath().let {
        if (Files.exists(it.resolve("settings.gradle"))) it else it.parent
    }

    private fun sourceFiles(relative: String): List<Path> = Files.walk(root.resolve(relative)).use { paths ->
        paths.filter { Files.isRegularFile(it) && it.toString().endsWith(".kt") }.toList()
    }

    private val exemptFileNames = setOf(
        "GameState.kt",
        "PlayerHand.kt",
        "WinningScoreEvaluator.kt"
    )

    private val forbiddenPatterns = listOf(
        Regex("ippatsuActive\\s*\\[[^\\]]*\\]\\s*=(?!=)"),
        Regex("riichiDeclared\\s*\\[[^\\]]*\\]\\s*=(?!=)"),
        Regex("riichiTurn\\s*\\[[^\\]]*\\]\\s*=(?!=)"),
        Regex("doubleRiichiDeclared\\s*\\[[^\\]]*\\]\\s*=(?!=)"),
        Regex("\\.ippatsu\\s*=(?!=)"),
        Regex("\\.riichiDeclared\\s*=(?!=)"),
        Regex("\\.riichiTurn\\s*=(?!=)")
    )

    @Test
    fun engineSourceHasNoDirectWritesToRiichiOrIppatsuOutsideExemptions() {
        val allEngineFiles = sourceFiles("engine/src/main")
        assertTrue(allEngineFiles.isNotEmpty(), "Engine source files must not be empty")

        val filesToScan = allEngineFiles.filter { path ->
            path.fileName.toString() !in exemptFileNames
        }
        assertTrue(filesToScan.isNotEmpty(), "Scanned files count must be > 0")

        val violations = mutableListOf<String>()

        for (file in filesToScan) {
            val content = Files.readString(file)
            for (pattern in forbiddenPatterns) {
                if (pattern.containsMatchIn(content)) {
                    val lines = Files.readAllLines(file)
                    for ((lineNum, line) in lines.withIndex()) {
                        if (pattern.containsMatchIn(line)) {
                            violations.add("${file.fileName}:${lineNum + 1}: ${line.trim()} (matched ${pattern.pattern})")
                        }
                    }
                }
            }
        }

        assertTrue(violations.isEmpty(), "Found direct writes to riichi/ippatsu fields outside exempt files:\n" + violations.joinToString("\n"))
    }
}

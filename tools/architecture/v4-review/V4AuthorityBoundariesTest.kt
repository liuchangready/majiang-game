package com.majiang.v4authority

import java.nio.file.Files
import java.nio.file.Path
import org.junit.jupiter.api.Test
import kotlin.test.assertTrue

class V4AuthorityBoundariesTest {
    private val root = Path.of(System.getProperty("user.dir")).toAbsolutePath().let {
        if (Files.exists(it.resolve("settings.gradle"))) it else it.parent
    }

    private fun sourceFiles(relative: String): List<Path> = Files.walk(root.resolve(relative)).use { paths ->
        paths.filter { Files.isRegularFile(it) && it.toString().endsWith(".kt") }.toList()
    }

    @Test fun domainAuthorityDoesNotOwnConnectionsOrExecutionThreads() {
        val files = sourceFiles("engine/src/main").filter {
            Regex("\\bclass\\s+TableAuthority\\b").containsMatchIn(Files.readString(it))
        }
        assertTrue(files.isNotEmpty(), "Missing pure domain TableAuthority")
        val forbidden = Regex("(?m)^\\s*import\\s+(?:com\\.majiang\\.(?:host\\.TableHost|access\\.(?:SessionAccess|local\\.)|server\\.|client\\.|transport\\.)|java\\.net\\.|java\\.util\\.concurrent\\.(?:Executors|ExecutorService|ThreadPoolExecutor))")
        val violations = files.filter { forbidden.containsMatchIn(Files.readString(it)) }
        assertTrue(violations.isEmpty(), "Domain authority owns transport/host/thread responsibilities: $violations")
    }

    @Test fun bothV4BindingsHaveStoppedImportingLegacyDomainHostAndAccess() {
        val files = sourceFiles("server/src/main/kotlin/com/majiang/server/inprocess") +
            sourceFiles("server/src/main/kotlin/com/majiang/server/udp")
        val forbidden = Regex("(?m)^\\s*import\\s+com\\.majiang\\.(?:host\\.TableHost|access\\.SessionAccess|access\\.local\\.LocalSession)\\b")
        val violations = files.filter { forbidden.containsMatchIn(Files.readString(it)) }
        assertTrue(violations.isEmpty(), "V4 bindings still execute the V3 domain host: $violations")
    }
}

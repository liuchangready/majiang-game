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
        // T5 删除 UDP 绑定目录后仅剩两种绑定；扫描现存目录，目录整体消失不再是豁免理由
        val bindingDirs = listOf(
            "server/src/main/kotlin/com/majiang/server/inprocess",
            "server/src/main/kotlin/com/majiang/server/tcp"
        )
        val files = bindingDirs.flatMap { dir ->
            if (Files.exists(root.resolve(dir))) sourceFiles(dir)
            else throw AssertionError("Expected binding directory is missing: $dir")
        }
        assertTrue(files.isNotEmpty(), "No binding sources found under $bindingDirs")
        val forbidden = Regex("(?m)^\\s*import\\s+com\\.majiang\\.(?:host\\.TableHost|access\\.SessionAccess|access\\.local\\.LocalSession)\\b")
        val violations = files.filter { forbidden.containsMatchIn(Files.readString(it)) }
        assertTrue(violations.isEmpty(), "V4 bindings still execute the V3 domain host: $violations")
    }
}

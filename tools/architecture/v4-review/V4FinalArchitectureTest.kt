package com.majiang.v4final

import java.nio.file.Files
import java.nio.file.Path
import java.lang.reflect.GenericArrayType
import java.lang.reflect.Modifier
import java.lang.reflect.ParameterizedType
import java.lang.reflect.Type
import java.lang.reflect.TypeVariable
import java.lang.reflect.WildcardType
import org.junit.jupiter.api.Test
import kotlin.test.assertTrue

/** Final gates are intentionally separate from the incremental R1 regression suite. */
class V4FinalArchitectureTest {
    private val root: Path = Path.of(System.getProperty("user.dir")).toAbsolutePath().let {
        if (Files.exists(it.resolve("settings.gradle"))) it else it.parent
    }
    private val modules = listOf("engine", "protocol", "client", "server", "presentation")
    private data class Source(val module: String, val path: Path, val text: String, val pkg: String)
    private fun sources(): List<Source> = modules.flatMap { module ->
        val dir = root.resolve("$module/src/main")
        if (!Files.exists(dir)) emptyList() else Files.walk(dir).use { paths ->
            paths.filter { Files.isRegularFile(it) && it.toString().let { n -> n.endsWith(".kt") || n.endsWith(".java") } }
                .map { file ->
                    val text = Files.readString(file)
                    Source(module, file, text,
                        Regex("(?m)^\\s*package\\s+([\\w.]+)").find(text)?.groupValues?.get(1) ?: "")
                }.toList()
        }
    }

    @Test fun productionModuleDependenciesMatchV4() {
        val forbidden = mapOf(
            "engine" to setOf("protocol", "client", "server", "presentation"),
            "protocol" to setOf("engine", "client", "server", "presentation"),
            "client" to setOf("engine", "server", "presentation"),
            "server" to setOf("client", "presentation"),
            "presentation" to setOf("engine")
        )
        val violations = mutableListOf<String>()
        for ((module, rejected) in forbidden) {
            val gradle = Files.readString(root.resolve("$module/build.gradle"))
            val dependencies = Regex("(?m)^\\s*(?:api|implementation|compileOnly|runtimeOnly)\\s+project\\s*\\(?\\s*['\"]:([^'\"]+)['\"]")
                .findAll(gradle).map { it.groupValues[1] }
            dependencies.filter { it in rejected }.forEach { violations.add("$module -> $it") }
        }
        assertTrue(violations.isEmpty(), "Forbidden production dependencies (test dependencies are excluded): $violations")
    }

    @Test fun actualSourcePackagesEnforceLayerBoundaries() {
        val all = sources()
        val owners = all.filter { it.pkg.isNotEmpty() }.groupBy { it.pkg }.mapValues { (_, files) -> files.map { it.module }.toSet() }
        val packages = owners.keys.sortedByDescending { it.length }
        val rejected = mapOf(
            "engine" to setOf("client", "protocol", "server", "presentation"),
            "protocol" to setOf("engine", "client", "server", "presentation"),
            "client" to setOf("engine", "server", "presentation"),
            "server" to setOf("client", "presentation"),
            "presentation" to setOf("engine")
        )
        val violations = mutableListOf<String>()
        for (source in all) {
            val imports = Regex("(?m)^\\s*import\\s+(?:static\\s+)?([\\w.*]+)").findAll(source.text)
            for (match in imports) {
                val name = match.groupValues[1]
                val target = packages.firstOrNull { name.startsWith("$it.") } ?: continue
                if (source.module !in owners.getValue(target) && owners.getValue(target).any { it in rejected[source.module].orEmpty() }) {
                    violations.add("${root.relativize(source.path)} imports $name")
                }
                if (source.pkg in setOf("com.majiang.presentation.menu", "com.majiang.presentation.game", "com.majiang.presentation.common") ||
                    listOf("menu", "game", "common").any { source.pkg.startsWith("com.majiang.presentation.$it.") }) {
                    if (name.startsWith("com.majiang.") && !name.startsWith("com.majiang.client.api.") &&
                        !("presentation" in owners.getValue(target))) {
                        violations.add("UI boundary: ${root.relativize(source.path)} imports $name")
                    }
                }
            }
        }
        assertTrue(violations.isEmpty(), violations.joinToString("\n"))
    }

    @Test fun fullyQualifiedReferencesAndUiImplementationsRespectSourceOwners() {
        val all = sources()
        val declaredType = Regex("\\b(?:class|interface|object|typealias|enum\\s+class)\\s+([A-Z][A-Za-z0-9_]*)")
        val owners = all.flatMap { source ->
            declaredType.findAll(source.text).map { match ->
                "${source.pkg}.${match.groupValues[1]}" to source
            }.toList()
        }.groupBy({ it.first }, { it.second }).mapValues { (_, files) -> files.map { it.module }.toSet() }
        val names = owners.keys.sortedByDescending { it.length }
        val forbidden = mapOf(
            "engine" to setOf("protocol", "client", "server", "presentation"),
            "protocol" to setOf("engine", "client", "server", "presentation"),
            "client" to setOf("engine", "server", "presentation"),
            "server" to setOf("client", "presentation"),
            "presentation" to setOf("engine")
        )
        val violations = mutableListOf<String>()
        for (source in all) {
            val withoutComments = source.text.replace(Regex("(?s)/\\*.*?\\*/"), "")
                .replace(Regex("(?m)//[^\\n]*"), "")
            val uiPackage = listOf("menu", "game", "common").any {
                source.pkg == "com.majiang.presentation.$it" || source.pkg.startsWith("com.majiang.presentation.$it.")
            }
            for (match in Regex("\\bcom\\.majiang(?:\\.[A-Za-z_][A-Za-z0-9_]*)+").findAll(withoutComments)) {
                val reference = match.value
                val declared = names.firstOrNull { reference == it || reference.startsWith("$it.") } ?: continue
                val targetModules = owners.getValue(declared)
                if (targetModules.size > 1) {
                    violations.add("Ambiguous declared type $declared in $targetModules")
                } else if (targetModules.single() in forbidden[source.module].orEmpty()) {
                    violations.add("${root.relativize(source.path)} references $declared in ${targetModules.single()}")
                }
                if (uiPackage) {
                    val target = all.firstOrNull { it.pkg == declared.substringBeforeLast('.') &&
                        Regex("\\b(?:class|interface|object|typealias)\\s+${Regex.escape(declared.substringAfterLast('.'))}\\b")
                            .containsMatchIn(it.text) }
                    if (target != null && target.module == "client" && target.path.fileName.toString() in
                        setOf("ServerConnectionSession.kt", "ProtocolViewMapper.kt", "EngineDtoMapper.kt")) {
                        violations.add("UI implementation reference: ${root.relativize(source.path)} -> $declared")
                    }
                    if (declared == "com.majiang.MahjongGame") {
                        violations.add("UI composition-root reference: ${root.relativize(source.path)}")
                    }
                }
            }
        }
        assertTrue(violations.isEmpty(), violations.distinct().joinToString("\n"))
    }

    @Test fun oldRuntimeEntryTypesAreRemoved() {
        val staleTypes = setOf("V3GameClientAdapter", "NetworkSessionAccess", "RemoteSession", "MallSession",
            "ClientTransport", "ClientConnectionHandler", "LocalTableRuntime", "LocalPlayerEndpoint", "PlayerEndpoint",
            "SessionAccess", "MahjongGameAccess", "LocalGameSession")
        val violations = sources().filter { source ->
            Regex("\\b(?:class|interface|object|typealias)\\s+(${staleTypes.joinToString("|")})\\b").containsMatchIn(source.text)
        }.map { root.relativize(it.path).toString() }
        assertTrue(violations.isEmpty(), "Obsolete V3 runtime still defined: $violations")
    }

    @Test fun presentationPackagesAndDomainAuthorityExist() {
        val all = sources()
        val missing = listOf("menu", "game", "common").filter { segment ->
            all.none { it.module == "presentation" && (it.pkg == "com.majiang.presentation.$segment" || it.pkg.startsWith("com.majiang.presentation.$segment.")) }
        }
        assertTrue(missing.isEmpty(), "Missing V4 presentation packages: $missing")
        assertTrue(all.any { it.module == "engine" && Regex("\\b(?:class|interface)\\s+TableAuthority\\b").containsMatchIn(it.text) },
            "Domain TableAuthority entry is missing")
        val engineTransport = all.filter { it.module == "engine" &&
            Regex("(?m)^\\s*import\\s+(?:java\\.net\\.|com\\.badlogic\\.|com\\.google\\.gson\\.)").containsMatchIn(it.text) }
        assertTrue(engineTransport.isEmpty(), "Domain imports transport/graphics/codec: ${engineTransport.map { it.path }}")
        val engineHost = all.filter { it.module == "engine" &&
            Regex("\\bclass\\s+(?:TableHost|LocalSession)\\b").containsMatchIn(it.text) }
        assertTrue(engineHost.isEmpty(), "Connection host remains in domain: ${engineHost.map { it.path }}")
    }

    @Test fun connectionLifecycleImplementationsBelongToServer() {
        val all = sources()
        val connectionTypes = setOf("ConnectionControl", "DisconnectPolicy", "DisconnectMode",
            "ConnectionStatus", "SeatConnectionState", "ControllerRole")
        for (name in connectionTypes) {
            val declaration = Regex("\\b(?:class|interface|object|typealias)\\s+${Regex.escape(name)}\\b")
            val owners = all.filter { declaration.containsMatchIn(it.text) }
            assertTrue(owners.size == 1 && owners.single().module == "server" &&
                owners.single().pkg == "com.majiang.server.control",
                "$name must have exactly one server implementation, without legacy aliases: ${owners.map { it.path }}")
        }
        val legacyReferences = all.filter {
            Regex("\\bcom\\.majiang\\.control(?:\\.|\\b)").containsMatchIn(it.text)
        }
        assertTrue(legacyReferences.isEmpty(), "Old engine connection-control references: ${legacyReferences.map { it.path }}")
    }

    @Test fun facadePublicTypesDoNotLeakDomainOrProtocolObjects() {
        val seen = mutableSetOf<Type>()
        val leaks = mutableListOf<String>()
        fun inspect(type: Type) {
            if (!seen.add(type)) return
            when (type) {
                is ParameterizedType -> {
                    val raw = type.rawType
                    if (raw is Class<*> && raw.name.startsWith("com.majiang.")) inspect(raw)
                    type.actualTypeArguments.forEach { inspect(it) }
                }
                is GenericArrayType -> inspect(type.genericComponentType)
                is WildcardType -> (if (type.lowerBounds.isNotEmpty()) type.lowerBounds else type.upperBounds).forEach { inspect(it) }
                is TypeVariable<*> -> type.bounds.forEach { inspect(it) }
                is Class<*> -> {
                    if (type.isArray) { inspect(type.componentType); return }
                    if (type == Any::class.java || type == Map::class.java || type == Collection::class.java || type == List::class.java) {
                        leaks.add("Untyped payload: ${type.name}")
                        return
                    }
                    if (type.name.startsWith("com.majiang.") && !type.name.startsWith("com.majiang.client.api.")) {
                        leaks.add(type.name)
                    } else if (type.name.startsWith("com.majiang.client.api.")) {
                        type.genericSuperclass?.takeIf { it != Any::class.java }?.let { inspect(it) }
                        type.genericInterfaces.forEach { inspect(it) }
                        type.declaredFields.filter { Modifier.isPublic(it.modifiers) && !it.isSynthetic }.forEach { field ->
                            inspect(field.genericType)
                        }
                        type.declaredMethods.filter { Modifier.isPublic(it.modifiers) && !it.isSynthetic &&
                            !(it.name == "equals" && it.parameterTypes.contentEquals(arrayOf(Any::class.java))) }.forEach { method ->
                            inspect(method.genericReturnType)
                            method.genericParameterTypes.forEach { inspect(it) }
                        }
                        type.constructors.filterNot { it.isSynthetic }.forEach { constructor -> constructor.genericParameterTypes.forEach { inspect(it) } }
                        type.declaredClasses.filterNot { it.isSynthetic || it.simpleName == "Companion" }.forEach { inspect(it) }
                    }
                }
            }
        }
        listOf("com.majiang.client.api.LobbySession", "com.majiang.client.api.GameSession",
            "com.majiang.client.api.SpectatorSession").forEach {
            inspect(Class.forName(it, false, javaClass.classLoader))
        }
        assertTrue(leaks.isEmpty(), "Facade recursively exposes non-client types: $leaks")
    }
}

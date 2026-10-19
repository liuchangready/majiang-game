package com.majiang.authority

import java.lang.reflect.GenericArrayType
import java.lang.reflect.Modifier
import java.lang.reflect.ParameterizedType
import java.lang.reflect.Type
import java.lang.reflect.TypeVariable
import java.lang.reflect.WildcardType
import java.nio.file.Files
import java.nio.file.Path
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * D-09 结构边界：专用处理器必须 internal、无父对象/回执表状态，唯一 requestId 命名空间
 * 仍由 TableAuthority 持有；ApplicationCoordinator 只在既有执行位置调用最小账本能力。
 */
class D09AuthorityDelegationStructureTest {

    private val authorityDir = Path.of("src/main/kotlin/com/majiang/authority")
    private val handlerNames = listOf(
        "TileMarkSubmissionHandler",
        "SleeveBloomSubmissionHandler",
        "BeamSwapSubmissionHandler",
        "ShufflePreparationHandler"
    )
    private val forbiddenParentTypes = setOf(
        "com.majiang.authority.TableAuthority",
        "com.majiang.application.ApplicationCoordinator",
        "com.majiang.state.GameState",
        "com.majiang.match.MatchSession",
        "com.majiang.round.RoundStateMachine"
    )

    private fun text(relative: String): String = Files.readString(Path.of("src/main/kotlin", relative))

    private fun nestedTypes(root: Class<*>): List<Class<*>> {
        val result = mutableListOf<Class<*>>()
        fun walk(type: Class<*>) {
            for (nested in type.declaredClasses) {
                if (nested.isSynthetic) continue
                result.add(nested)
                walk(nested)
            }
        }
        walk(root)
        return result
    }

    /** 递归展开字段泛型，防止 List<MatchSession> / Map<String, RoundStateMachine> 绕过简单类型名检查。 */
    private fun typeNames(type: Type): List<String> = when (type) {
        is Class<*> -> listOf(type.name) + type.typeParameters.flatMap { it.bounds.flatMap(::typeNames) }
        is ParameterizedType -> listOf((type.rawType as Class<*>).name) +
                type.actualTypeArguments.flatMap(::typeNames)
        is GenericArrayType -> typeNames(type.genericComponentType)
        is WildcardType -> (type.lowerBounds + type.upperBounds).flatMap(::typeNames)
        is TypeVariable<*> -> type.bounds.flatMap(::typeNames)
        else -> emptyList()
    }

    @Test
    fun `dedicated handlers and every nested input contain no parent aggregate`() {
        val forbiddenReceiptNames = setOf("storedReceipts", "storedShufflePlans", "perSeatReceiptCounts")
        for (name in handlerNames) {
            val file = authorityDir.resolve("cheat/$name.kt")
            assertTrue(Files.exists(file), "Missing internal handler $name")
            val source = Files.readString(file)
            assertTrue(
                Regex("\\binternal\\s+class\\s+$name\\b").containsMatchIn(source),
                "$name must be internal"
            )
            assertFalse(
                source.contains("storedReceipts") || source.contains("storedShufflePlans"),
                "$name must not own a separate receipt namespace"
            )

            val root = Class.forName("com.majiang.authority.cheat.$name")
            for (type in listOf(root) + nestedTypes(root)) {
                for (field in type.declaredFields) {
                    if (field.isSynthetic || Modifier.isStatic(field.modifiers)) continue
                    val names = typeNames(field.genericType)
                    val parent = names.firstOrNull { it in forbiddenParentTypes }
                    assertNull(
                        parent,
                        "${type.name}.${field.name} must not carry parent aggregate $parent"
                    )
                    assertFalse(
                        field.name in forbiddenReceiptNames,
                        "${type.name}.${field.name} must not hold a receipt map"
                    )
                }
            }
        }

        val cheatDir = authorityDir.resolve("cheat")
        val sources = Files.list(cheatDir).use { paths ->
            paths.filter { Files.isRegularFile(it) && it.toString().endsWith(".kt") }.toList()
        }
        for (source in sources) {
            val code = Files.readString(source)
                .replace(Regex("(?s)/\\*.*?\\*/"), "")
                .replace(Regex("(?m)//[^\\n]*"), "")
            for (forbidden in forbiddenParentTypes) {
                assertFalse(
                    code.contains(forbidden),
                    "${source.fileName} must not reference parent aggregate $forbidden in executable code"
                )
            }
        }
        assertFalse(
            Files.exists(cheatDir.resolve("PrivateCheatPreviewAssembler.kt")),
            "Preview assembly must stay in TableAuthority rather than reintroducing a parent-carrying wrapper"
        )
    }

    @Test
    fun `table authority remains the single receipt and preparation transaction owner`() {
        val authority = text("com/majiang/authority/TableAuthority.kt")
        assertTrue(Regex("private\\s+val\\s+storedReceipts\\b").containsMatchIn(authority))
        assertTrue(Regex("private\\s+val\\s+storedShufflePlans\\b").containsMatchIn(authority))
        assertTrue(authority.contains("tileMarkHandler.validate("))
        assertTrue(authority.contains("sleeveBloomHandler.validateActivation("))
        assertTrue(authority.contains("sleeveBloomHandler.validateChoice("))
        assertTrue(authority.contains("beamSwapHandler.validateActivation("))
        assertTrue(authority.contains("beamSwapHandler.validateChoice("))
        assertTrue(authority.contains("shufflePreparationHandler.validatePlan("))
        assertTrue(authority.contains("shufflePreparationHandler.step("))
        assertFalse(authority.contains("prepConfirmed"), "Preparation state must live in the dedicated handler")
        assertFalse(authority.contains("NormalizedTileMarkPayload"))
        assertFalse(authority.contains("PrivateCheatPreviewAssembler"), "Preview wrapper must not return")
        assertTrue(authority.contains("TileMarkProjectionFactory.create("))
        assertTrue(authority.contains("SleeveBloomProjectionFactory.create("))
        assertTrue(authority.contains("BeamSwapProjectionFactory.create("))
    }

    @Test
    fun `coordinator delegates fee and effect construction at existing execution sites`() {
        val coordinator = text("com/majiang/application/ApplicationCoordinator.kt")
        assertTrue(coordinator.contains("import com.majiang.application.cheat.CheatLedgerCharge"))
        assertTrue(coordinator.contains("import com.majiang.application.cheat.CheatEffectFactory"))
        assertTrue(
            Regex("CheatLedgerCharge\\.apply\\(").findAll(coordinator).count() >= 4,
            "Every fee path must use the same typed ledger capability"
        )
        assertTrue(
            Regex("CheatEffectFactory\\.(tileMarkGroup|sleeveBloomOffer|beamSwapOffer)\\(")
                .findAll(coordinator).count() >= 3,
            "Effect construction must be delegated without moving transaction ownership"
        )
        assertFalse(
            coordinator.contains("storedReceipts"),
            "ApplicationCoordinator must not adopt the authority receipt namespace"
        )
    }
}

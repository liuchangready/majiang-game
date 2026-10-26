package com.majiang.v3

import com.majiang.model.Tile
import com.majiang.model.TilePool
import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleTier
import com.majiang.stacking.ShuffleWallGenerator
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.ByteArrayOutputStream
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.security.MessageDigest
import java.util.Locale

/**
 * P0.1 反向牌墙伴随证据导出器（严格 test-only）。
 *
 * 本任务只补齐已接受 shuffle 报告列出的 `reversedEqualsThree` 缺口：旧
 * `tests/v5-golden/shuffle-v1.json` 只有布尔，没有实际 reversed wall。这里真实调用
 * **未修改的** V4 [ShuffleWallGenerator.generate]，对同一 seed/dealer/threePlans/config
 * 分别以正序与逆序 plans 执行，并导出实际 `baseWall` / `threeCasterWall` / `reversedWall`。
 *
 * 边界：
 * - 不实现随机插桩、RNG、trace 猜测；不声称 C# 三人 Shape 逐张对齐。
 * - 不修改旧 fixture、生产 Kotlin/C#、catalog/manifest、统一 verifier、solution/project。
 * - 默认运行会真实执行并断言法律，但不写盘；仅
 *   `V5_SHUFFLE_REVERSED_GOLDEN_EXPORT=1`（或系统属性
 *   `v5.shuffle.reversed.export=true`）才写新伴随文件。
 * - 文件中的墙、布尔、count、source SHA 全部由真实运行/真实字节计算，不手填。
 * - `sha256Raw` 与 `sha256LfNormalized` 是两个明确不同的域；本工作树为 CRLF，
 *   因此 raw 与 LF-normalized 值不同，导出物同时记录两者与字节数。
 *
 * 运行（在冻结 base `03084f08af6e2ddefa2ebc6e094de5dac2474fd4` 之上）：
 * `V5_SHUFFLE_REVERSED_GOLDEN_EXPORT=1 ./gradlew :engine:test --tests "com.majiang.v3.V5ShuffleReversedGoldenExporterTest" --console=plain`
 */
class V5ShuffleReversedGoldenExporterTest {

    @Test
    fun exportReversedWallCompanion() {
        val export = System.getenv("V5_SHUFFLE_REVERSED_GOLDEN_EXPORT") == "1" ||
            System.getProperty("v5.shuffle.reversed.export") == "true"

        val generator = ShuffleWallGenerator()
        val cfg = ShuffleConfig(enabled = true)
        val threePlans = listOf(
            SeatShufflePlan(0, ShuffleSelection(ShuffleTarget.KOKUSHI)),
            SeatShufflePlan(1, ShuffleSelection(ShuffleTarget.DAISANGEN)),
            SeatShufflePlan(2, ShuffleSelection(ShuffleTarget.CHINITSU_MAN))
        )
        val reversedPlans = threePlans.reversed()
        val standardSet = TilePool.buildStandardSet().toSet()

        val cases = mutableListOf<RJ>()
        var threeCasterChangedSeeds = 0
        var reversedEqualsThreeSeeds = 0
        var deadWallSegmentEqualSeeds = 0

        for (seed in 0L..31L) {
            val dealer = (seed % 4L).toInt()

            // 三次真实 V4 执行：基础、正序三人、逆序三人。
            val base = generator.generate(seed, dealer, emptyList(), cfg)
            val three = generator.generate(seed, dealer, threePlans, cfg)
            val reversed = generator.generate(seed, dealer, reversedPlans, cfg)

            // 重复运行逐字段确定性（同一输入两次必须完全相同）。
            assertEquals(base, generator.generate(seed, dealer, emptyList(), cfg), "seed=$seed base determinism")
            assertEquals(three, generator.generate(seed, dealer, threePlans, cfg), "seed=$seed three determinism")
            assertEquals(reversed, generator.generate(seed, dealer, reversedPlans, cfg), "seed=$seed reversed determinism")

            // 真实法律断言：136 唯一、标准牌集、3 赤五、死山 122..135 隔离。
            for ((label, wall) in listOf("base" to base, "three" to three, "reversed" to reversed)) {
                assertEquals(136, wall.size, "seed=$seed $label wall size")
                assertEquals(136, wall.toSet().size, "seed=$seed $label wall uniqueness")
                assertEquals(standardSet, wall.toSet(), "seed=$seed $label must conserve the 136-tile standard set")
                assertEquals(3, wall.count { it.isRedFive }, "seed=$seed $label red five count")
                assertEquals(base.drop(122), wall.drop(122), "seed=$seed $label dead wall must equal base dead wall")
            }

            // 旧导出器在写盘前断言的真实 V4 关系，本伴随文件重新从实际墙计算。
            val reversedEqualsThree = three == reversed
            val threeCasterChanged = base != three
            val deadWallSegmentEqual = base.drop(122) == three.drop(122)

            assertTrue(deadWallSegmentEqual, "seed=$seed dead wall segment must be equal")
            assertTrue(reversedEqualsThree, "seed=$seed reversed plans must produce the same actual wall as forward plans")
            assertTrue(threeCasterChanged, "seed=$seed n=3 must change at least one slot in the accepted sample")

            if (threeCasterChanged) threeCasterChangedSeeds++
            if (reversedEqualsThree) reversedEqualsThreeSeeds++
            if (deadWallSegmentEqual) deadWallSegmentEqualSeeds++

            cases.add(
                robj(
                    "seed" to rj(seed),
                    "dealerSeat" to rj(dealer),
                    "plans" to plansJson(threePlans),
                    "reversedPlans" to plansJson(reversedPlans),
                    "baseWall" to tilesJson(base),
                    "threeCasterWall" to tilesJson(three),
                    "reversedWall" to tilesJson(reversed),
                    "redFiveCount" to rj(three.count { it.isRedFive }),
                    "deadWallSegmentEqual" to rj(deadWallSegmentEqual),
                    "threeCasterChanged" to rj(threeCasterChanged),
                    "reversedEqualsThree" to rj(reversedEqualsThree)
                )
            )
        }

        assertEquals(32, cases.size, "exporter loop must cover seeds 0..31 exactly")
        assertEquals(32, threeCasterChangedSeeds, "accepted V4 sample has all 32 three-caster walls changed")
        assertEquals(32, reversedEqualsThreeSeeds, "accepted V4 sample has all 32 reversed walls equal to three")
        assertEquals(32, deadWallSegmentEqualSeeds, "accepted V4 sample isolates dead wall for all 32 seeds")

        if (!export) {
            println(
                "V5ShuffleReversedGoldenExporterTest: verified 32 real V4 forward/reversed pairs; " +
                    "set V5_SHUFFLE_REVERSED_GOLDEN_EXPORT=1 to write the companion fixture."
            )
            return
        }

        val root = repoRoot()
        val baseCommit = gitHead(root)
        assertEquals(
            FROZEN_V4_BASE,
            baseCommit,
            "export must run on the frozen base $FROZEN_V4_BASE; do not silently change provenance"
        )

        // 旧 fixture 仍必须是已接受的同一份历史样本：绑定它的真实 schema/commit/32 seeds/布尔，
        // 但**不重写**它。逐字段墙绑定由 C# ShuffleReversedGoldenTests 在运行时完成。
        val oldFixturePath = root.resolve("tests/v5-golden/shuffle-v1.json")
        assertTrue(Files.exists(oldFixturePath), "old fixture must exist: $oldFixturePath")
        val oldRaw = Files.readAllBytes(oldFixturePath)
        val oldText = String(oldRaw, StandardCharsets.UTF_8)
        assertEquals("v5-shuffle-golden-v1", extractStringField(oldText, "schema"), "old fixture schema")
        val oldV4Commit = extractStringField(oldText, "v4Commit")
        assertEquals(40, oldV4Commit.length, "old fixture v4Commit must be 40 hex")
        assertTrue(oldV4Commit.all { it in "0123456789abcdefABCDEF" }, "old fixture v4Commit must be hex")
        val oldSeeds = Regex("\"seed\"\\s*:\\s*(\\d+)")
            .findAll(oldText)
            .map { it.groupValues[1].toInt() }
            .toList()
        assertEquals(32, oldSeeds.size, "old fixture must still contain exactly 32 cases")
        assertEquals((0..31).toList(), oldSeeds.sorted(), "old fixture seeds must still be exactly 0..31")
        val oldReversedTrue = Regex("\"reversedEqualsThree\"\\s*:\\s*true").findAll(oldText).count()
        assertEquals(32, oldReversedTrue, "old fixture must still carry 32 true reversedEqualsThree booleans")

        val sourceFiles = listOf(
            SourceFile("v4_production_wall_generator", "engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt"),
            SourceFile("v4_production_shuffle_slots", "engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt"),
            SourceFile("v4_production_shuffle_config", "engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt"),
            SourceFile("v4_production_shuffle_targets", "engine/src/main/kotlin/com/majiang/stacking/ShuffleTargets.kt"),
            SourceFile("v4_production_shuffle_target", "engine/src/main/kotlin/com/majiang/stacking/ShuffleTarget.kt"),
            SourceFile("v4_production_shuffle_selection", "engine/src/main/kotlin/com/majiang/stacking/ShuffleSelection.kt"),
            SourceFile("v4_production_seat_shuffle_plan", "engine/src/main/kotlin/com/majiang/stacking/SeatShufflePlan.kt"),
            SourceFile("v4_production_tile_pool", "engine/src/main/kotlin/com/majiang/model/TilePool.kt"),
            SourceFile("v4_production_tile", "engine/src/main/kotlin/com/majiang/model/Tile.kt"),
            SourceFile("v4_test_only_exporter", "engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt")
        )
        val sourceEntries = sourceFiles.map { sourceJson(root, it) }

        val oldLf = lfNormalized(oldRaw)
        val rootJson = robj(
            "schema" to rj(SCHEMA),
            "generator" to rj(EXPORTER_PATH),
            "generatorVersion" to rj(1),
            "v4BaseCommit" to rj(baseCommit),
            "exportWorktreeHead" to rj(baseCommit),
            "rulesetVersion" to rj(1),
            "generatedByCommand" to rj(
                "V5_SHUFFLE_REVERSED_GOLDEN_EXPORT=1 ./gradlew :engine:test " +
                    "--tests \"com.majiang.v3.V5ShuffleReversedGoldenExporterTest\" --console=plain"
            ),
            "extractionStatus" to rj("exported"),
            "exportedAtNote" to rj(
                "generated from actual V4 ShuffleWallGenerator.generate calls; forward and reversed plan order " +
                    "are separate executions; no expected wall, boolean or hash is hand-written"
            ),
            "v4ProductionSourceNote" to rj(
                "engine/src/main/kotlin/com/majiang/stacking and engine/src/main/kotlin/com/majiang/model are the " +
                    "V4 production sources used; v4BaseCommit is the frozen tree at export time and the source " +
                    "hashes below bind the exact working-tree bytes"
            ),
            "relationNote" to rj(
                "threeCasterWall and reversedWall are separate actual V4 generate executions with plans and " +
                    "plans.reversed(); reversedEqualsThree is recomputed by comparing the two exported walls, " +
                    "not copied from shuffle-v1.json"
            ),
            "scopeNote" to rj(
                "This companion supplies the missing actual reversed wall only. It is not a C# Shape " +
                    "three-caster output diff, does not provide V4 ordered random events, and does not clear " +
                    "BLOCKED_RANDOM_INPUT for threeCasterShapeBinding."
            ),
            "generationBaselineNote" to rj(
                "Exported in worktree wt/v5-shuffle-reversed-golden on top of frozen base $FROZEN_V4_BASE before " +
                    "the delivery commit. The delivery commit adds only test-only exporter/binding/probe/report " +
                    "files; the production source hashes above are unchanged from that base."
            ),
            "sourceHashAlgorithm" to rj("SHA-256"),
            "sourceHashNote" to rj(
                "sha256Raw is the exact working-tree byte digest at export time; sha256LfNormalized is the " +
                    "LF-normalized digest that converts CRLF and lone CR to LF and is stable across checkouts. " +
                    "They are different hash domains and may differ when a file uses CRLF; the pre-existing Kotlin " +
                    "sources here use CRLF while the newly added exporter uses LF."
            ),
            "sourceTree" to rarr(sourceEntries),
            "bindsOldFixture" to robj(
                "path" to rj("tests/v5-golden/shuffle-v1.json"),
                "schema" to rj("v5-shuffle-golden-v1"),
                "v4Commit" to rj(oldV4Commit),
                "caseCount" to rj(oldSeeds.size),
                "sha256Raw" to rj(sha256(oldRaw)),
                "sha256LfNormalized" to rj(sha256(oldLf)),
                "bytesRaw" to rj(oldRaw.size.toLong()),
                "bytesLfNormalized" to rj(oldLf.size.toLong()),
                "lineEnding" to rj(detectLineEnding(oldRaw))
            ),
            "config" to configJson(cfg),
            "generationResult" to robj(
                "cases" to rj(cases.size),
                "reversedEqualsThreeSeeds" to rj(reversedEqualsThreeSeeds),
                "threeCasterChangedSeeds" to rj(threeCasterChangedSeeds),
                "deadWallSegmentEqualSeeds" to rj(deadWallSegmentEqualSeeds),
                "exporterTestResult" to rj("PASSED")
            ),
            "cases" to rarr(cases)
        )

        val path = root.resolve("tests/v5-golden/shuffle-reversed-v1.json")
        Files.createDirectories(path.parent)
        Files.write(path, rootJson.render().toByteArray(StandardCharsets.UTF_8))
        assertTrue(Files.size(path) > 50_000L, "companion fixture looks unexpectedly small: ${Files.size(path)} bytes")
        println("wrote $path (${Files.size(path)} bytes, ${cases.size} cases)")
    }

    private data class SourceFile(val role: String, val relativePath: String)

    private fun sourceJson(root: Path, source: SourceFile): RJ {
        val path = root.resolve(source.relativePath)
        assertTrue(Files.exists(path), "source file must exist: ${source.relativePath}")
        val raw = Files.readAllBytes(path)
        val lf = lfNormalized(raw)
        val rawHash = sha256(raw)
        val lfHash = sha256(lf)
        return robj(
            "role" to rj(source.role),
            "path" to rj(source.relativePath),
            "sha256Raw" to rj(rawHash),
            "sha256LfNormalized" to rj(lfHash),
            "bytesRaw" to rj(raw.size.toLong()),
            "bytesLfNormalized" to rj(lf.size.toLong()),
            "lineEnding" to rj(detectLineEnding(raw)),
            "rawEqualsLfNormalized" to rj(rawHash == lfHash)
        )
    }

    private fun plansJson(plans: List<SeatShufflePlan>): RJ = rarr(
        plans.map { plan ->
            robj(
                "seat" to rj(plan.seat),
                "target" to rj(plan.selection.target!!.name)
            )
        }
    )

    private fun tilesJson(tiles: List<Tile>): RJ = RJ.Array(tiles.map { rj(it.tileId) }, compact = true)

    private fun tiersJson(tiers: Map<Int, ShuffleTier>): RJ = robj(
        *tiers.map { (n, tier) ->
            n.toString() to robj(
                "strength" to d(tier.strength),
                "initialBudget" to rj(tier.initialBudget),
                "drawBudget" to rj(tier.drawBudget)
            )
        }.toTypedArray()
    )

    private fun configJson(cfg: ShuffleConfig): RJ {
        val targets = ShuffleTarget.entries.toList()
        return robj(
            "enabled" to rj(cfg.enabled),
            "plannedDraws" to rj(cfg.plannedDraws),
            "strength" to d(cfg.strength),
            "interference" to d(cfg.interference),
            "diversityInterference" to d(cfg.diversityInterference),
            "diversityBudgetPenalty" to d(cfg.diversityBudgetPenalty),
            "tiers" to tiersJson(cfg.tiers),
            "mixedTiers" to tiersJson(cfg.mixedTiers),
            "targetTiers" to robj(
                *targets.map { target ->
                    target.name to tiersJson(cfg.targetTiers[target] ?: emptyMap())
                }.toTypedArray()
            )
        )
    }

    private fun d(value: Double): RJ = RJ.DNum(value)

    private fun repoRoot(): Path {
        var path: Path? = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        while (path != null) {
            if (Files.exists(path.resolve("AGENTS.md")) && Files.exists(path.resolve("dotnet"))) {
                return path
            }
            path = path.parent
        }
        error("cannot locate repository root from ${System.getProperty("user.dir")}")
    }

    private fun gitHead(root: Path): String {
        val process = ProcessBuilder("git", "-C", root.toString(), "rev-parse", "HEAD")
            .redirectErrorStream(true)
            .start()
        val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
        require(process.waitFor() == 0 && text.length == 40) { "git rev-parse HEAD failed: $text" }
        return text
    }

    private fun extractStringField(json: String, field: String): String {
        val match = Regex("\"${Regex.escape(field)}\"\\s*:\\s*\"([^\"]*)\"").find(json)
        requireNotNull(match) { "missing string field $field in old fixture" }
        return match.groupValues[1]
    }

    private fun sha256(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") {
            String.format(Locale.ROOT, "%02x", it)
        }

    /** CRLF 与孤立 CR 都规范化为 LF；不改变其他字节。 */
    private fun lfNormalized(bytes: ByteArray): ByteArray {
        val out = ByteArrayOutputStream(bytes.size)
        var i = 0
        while (i < bytes.size) {
            val value = bytes[i].toInt() and 0xff
            if (value == 13) {
                out.write(10)
                if (i + 1 < bytes.size && (bytes[i + 1].toInt() and 0xff) == 10) {
                    i++
                }
            } else {
                out.write(value)
            }
            i++
        }
        return out.toByteArray()
    }

    private fun detectLineEnding(bytes: ByteArray): String {
        var crlf = 0
        var lf = 0
        var loneCr = 0
        var i = 0
        while (i < bytes.size) {
            val value = bytes[i].toInt() and 0xff
            if (value == 13) {
                if (i + 1 < bytes.size && (bytes[i + 1].toInt() and 0xff) == 10) {
                    crlf++
                    i++
                } else {
                    loneCr++
                }
            } else if (value == 10) {
                lf++
            }
            i++
        }
        return when {
            crlf > 0 && lf == 0 && loneCr == 0 -> "CRLF"
            crlf == 0 && loneCr == 0 -> "LF"
            else -> "MIXED"
        }
    }

    private companion object {
        const val SCHEMA = "v5-shuffle-reversed-golden-v1"
        const val EXPORTER_PATH =
            "engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt"
        const val FROZEN_V4_BASE = "03084f08af6e2ddefa2ebc6e094de5dac2474fd4"
    }
}

// -------------------------------------------------------------------------
// Local JSON rendering helpers (test-only; no domain algorithm is duplicated).
// -------------------------------------------------------------------------

private sealed class RJ {
    data class Str(val value: String) : RJ()
    data class Num(val value: Long) : RJ()
    data class DNum(val value: Double) : RJ()
    data class Bool(val value: Boolean) : RJ()
    object Null : RJ()
    data class Array(val items: List<RJ>, val compact: Boolean = false) : RJ()
    data class Object(val fields: List<Pair<String, RJ>>) : RJ()

    fun render(): String {
        val sb = StringBuilder()
        renderTo(sb, 0, compact = false)
        return sb.toString()
    }

    private fun renderTo(sb: StringBuilder, indent: Int, compact: Boolean) {
        when (this) {
            is Str -> sb.append(quote(value))
            is Num -> sb.append(value)
            is DNum -> sb.append(renderDouble(value))
            is Bool -> sb.append(if (value) "true" else "false")
            Null -> sb.append("null")
            is Array -> {
                sb.append('[')
                if (items.isNotEmpty()) {
                    val childCompact = compact || this.compact
                    items.forEachIndexed { index, item ->
                        if (index > 0) sb.append(',')
                        if (!childCompact) {
                            sb.append('\n').append(" ".repeat(indent + 2))
                        }
                        item.renderTo(sb, indent + 2, childCompact)
                    }
                    if (!childCompact) {
                        sb.append('\n').append(" ".repeat(indent))
                    }
                }
                sb.append(']')
            }
            is Object -> {
                sb.append('{')
                if (fields.isNotEmpty()) {
                    fields.forEachIndexed { index, field ->
                        if (index > 0) sb.append(',')
                        if (!compact) {
                            sb.append('\n').append(" ".repeat(indent + 2))
                        }
                        sb.append(quote(field.first)).append(':')
                        if (!compact) sb.append(' ')
                        field.second.renderTo(sb, indent + 2, compact)
                    }
                    if (!compact) {
                        sb.append('\n').append(" ".repeat(indent))
                    }
                }
                sb.append('}')
            }
        }
    }

    /** Kotlin/Java Double.toString 产生合法 JSON 数值字面量（含 E 计数法），Locale 无关。 */
    private fun renderDouble(value: Double): String {
        require(!value.isNaN() && !value.isInfinite()) { "golden JSON must not contain non-finite doubles" }
        return value.toString()
    }

    private fun quote(value: String): String {
        val sb = StringBuilder("\"")
        for (c in value) {
            when (c) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> if (c.code < 0x20) {
                    sb.append("\\u").append(String.format(Locale.ROOT, "%04x", c.code))
                } else {
                    sb.append(c)
                }
            }
        }
        sb.append('"')
        return sb.toString()
    }
}

private fun rj(value: String): RJ = RJ.Str(value)
private fun rj(value: Int): RJ = RJ.Num(value.toLong())
private fun rj(value: Long): RJ = RJ.Num(value)
private fun rj(value: Boolean): RJ = RJ.Bool(value)
private fun robj(vararg fields: Pair<String, RJ>): RJ = RJ.Object(fields.toList())
private fun rarr(items: List<RJ>): RJ = RJ.Array(items)

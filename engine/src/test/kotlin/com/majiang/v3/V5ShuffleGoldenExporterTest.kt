package com.majiang.v3

import com.majiang.model.Tile
import com.majiang.model.TilePool
import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleSlots
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleTargets
import com.majiang.stacking.ShuffleTier
import com.majiang.stacking.ShuffleWallGenerator
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.Locale

/**
 * V4 只读 golden 导出器（码牌/牌山域）。
 *
 * 仅当 `V5_SHUFFLE_GOLDEN_EXPORT=1`（或系统属性 v5.shuffle.export=true）时执行：
 * 真实运行 V4 [ShuffleWallGenerator] 后，先在测试内断言 V4 验收法律
 * （136 张守恒、赤五恒 3、死山 122..135 隔离、排序无关、四人恒等、
 * 无目标/禁用/零额度恒等），全部通过后才写 `tests/v5-golden/shuffle-v1.json`。
 * 文件中的一切数值均来自真实 V4 运行；严禁手填预期哈希。
 *
 * 与 scoring-v1 的差异（开放决策 D-09）：V4 牌山由 JVM `Random(seed)` 派生，
 * 不是跨运行时可重放规范。因此本文件记录的墙是“V4 行为样本”，
 * `rngGapNote` 明确声明 C# 侧不得做逐字节墙复现断言，只验证
 * 结构法律 + 纯数学（槽位/需求向量/重叠度）与来源可追溯性。
 */
class V5ShuffleGoldenExporterTest {

    @Test
    fun exportShuffleGoldenSamples() {
        if (System.getenv("V5_SHUFFLE_GOLDEN_EXPORT") != "1" && System.getProperty("v5.shuffle.export") != "true") {
            return
        }

        val generator = ShuffleWallGenerator()
        val cfg = ShuffleConfig(enabled = true)
        val zeroBudgetCfg = cfg.withTier(1, ShuffleTier(1.0, initialBudget = 0, drawBudget = 0))

        val threePlans = listOf(
            SeatShufflePlan(0, ShuffleSelection(ShuffleTarget.KOKUSHI)),
            SeatShufflePlan(1, ShuffleSelection(ShuffleTarget.DAISANGEN)),
            SeatShufflePlan(2, ShuffleSelection(ShuffleTarget.CHINITSU_MAN))
        )
        val fourPlans = threePlans + SeatShufflePlan(3, ShuffleSelection(ShuffleTarget.DAISUUSHII))
        val noTargetPlans = (0..3).map { SeatShufflePlan(it, ShuffleSelection(null)) }
        val singlePlans = listOf(SeatShufflePlan(0, ShuffleSelection(ShuffleTarget.CHINITSU_MAN)))

        val standardSet = TilePool.buildStandardSet().toSet()
        val cases = mutableListOf<J>()
        var threeChangedCount = 0

        for (seed in 0L..31L) {
            val dealer = (seed % 4L).toInt()
            val base = generator.generate(seed, dealer, emptyList(), cfg)
            val three = generator.generate(seed, dealer, threePlans, cfg)
            val reversed = generator.generate(seed, dealer, threePlans.reversed(), cfg)
            val four = generator.generate(seed, dealer, fourPlans, cfg)
            val noTarget = generator.generate(seed, dealer, noTargetPlans, cfg)
            val zeroBudget = generator.generate(seed, dealer, singlePlans, zeroBudgetCfg)

            // --- 法律断言（写盘前全部复核，任一失败即中止导出） ---
            for (wall in listOf(base, three, four, noTarget, zeroBudget)) {
                assertEquals(136, wall.size, "seed=$seed wall size")
                assertEquals(standardSet, wall.toSet(), "seed=$seed must conserve the 136-tile standard set")
                assertEquals(3, wall.count { it.isRedFive }, "seed=$seed red five count")
            }
            assertEquals(base, noTarget, "seed=$seed no-target identity")
            assertEquals(base, zeroBudget, "seed=$seed zero-budget identity")
            assertEquals(base, four, "seed=$seed four-caster identity")
            assertEquals(three, reversed, "seed=$seed order independence")
            assertEquals(base.drop(122), three.drop(122), "seed=$seed dead wall segment equality")
            if (base != three) threeChangedCount++

            cases.add(obj(
                "seed" to j(seed),
                "dealerSeat" to j(dealer),
                "plans" to arr(threePlans.map { p ->
                    obj(
                        "seat" to j(p.seat),
                        "target" to (p.selection.target?.name?.let { j(it) } ?: J.Null)
                    )
                }),
                "baseWall" to tilesJson(base),
                "threeCasterWall" to tilesJson(three),
                "fourCasterWall" to tilesJson(four),
                "noTargetWall" to tilesJson(noTarget),
                "zeroBudgetWall" to tilesJson(zeroBudget),
                "redFiveCount" to j(three.count { it.isRedFive }),
                "deadWallSegmentEqual" to j(base.drop(122) == three.drop(122)),
                "threeCasterChanged" to j(base != three),
                "fourCasterIdentity" to j(base == four),
                "reversedEqualsThree" to j(three == reversed)
            ))
        }

        assertTrue(threeChangedCount > 0, "n=3 must still perform exchanges in the exported sample")

        // 槽位矩阵（真实 V4 ShuffleSlots 输出；V4 验收法律的数值事实）
        val slotMatrix = mutableListOf<J>()
        for (dealer in 0..3) {
            for (seat in 0..3) {
                val initial = ShuffleSlots.initial(dealer, seat)
                val draws = ShuffleSlots.draws(dealer, seat, 18)
                assertEquals(if (seat == dealer) 14 else 13, initial.size)
                assertEquals(if (seat == dealer) 52 else 48 + (seat - dealer + 4) % 4, initial.last())
                assertTrue(draws.all { it in 53..121 }, "draw slots must stay in 53..121")
                slotMatrix.add(obj(
                    "dealerSeat" to j(dealer),
                    "seat" to j(seat),
                    "initialSlots" to intsJson(initial),
                    "drawSlots" to intsJson(draws)
                ))
            }
        }

        // 需求向量与重叠度矩阵（真实 ShuffleTargets 输出）
        val targets = ShuffleTarget.entries.toList()
        assertEquals(6, targets.size)
        val demandVectors = targets.associateWith { ShuffleTargets.demand(it) }
        for ((_, demand) in demandVectors) {
            assertEquals(1.0, demand.sum(), 1e-8)
        }
        val overlap = targets.associateWith { a -> targets.associateWith { b -> ShuffleTargets.overlap(a, b) } }
        assertEquals(2 / kotlin.math.sqrt(117.0), overlap.getValue(ShuffleTarget.CHINITSU_MAN).getValue(ShuffleTarget.KOKUSHI), 1e-8)
        assertEquals(3 / kotlin.math.sqrt(39.0), overlap.getValue(ShuffleTarget.DAISANGEN).getValue(ShuffleTarget.KOKUSHI), 1e-8)
        assertEquals(4 / kotlin.math.sqrt(52.0), overlap.getValue(ShuffleTarget.DAISUUSHII).getValue(ShuffleTarget.KOKUSHI), 1e-8)

        // 配置快照（真实 ShuffleConfig 默认值）
        val configSnapshot = obj(
            "enabled" to j(cfg.enabled),
            "plannedDraws" to j(cfg.plannedDraws),
            "strength" to d(cfg.strength),
            "interference" to d(cfg.interference),
            "diversityInterference" to d(cfg.diversityInterference),
            "diversityBudgetPenalty" to d(cfg.diversityBudgetPenalty),
            "tiers" to tiersJson(cfg.tiers),
            "mixedTiers" to tiersJson(cfg.mixedTiers),
            "targetTiers" to obj(
                *targets.map { target ->
                    target.name to tiersJson(cfg.targetTiers[target] ?: emptyMap())
                }.toTypedArray()
            )
        )

        val root = obj(
            "schema" to j("v5-shuffle-golden-v1"),
            "generator" to j("engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt"),
            "generatorVersion" to j(1),
            "v4Commit" to j(v4Commit()),
            "rulesetVersion" to j(1),
            "generatedByCommand" to j(
                "V5_SHUFFLE_GOLDEN_EXPORT=1 ./gradlew :engine:test --tests \\\"com.majiang.v3.V5ShuffleGoldenExporterTest\\\" --console=plain"
            ),
            "extractionStatus" to j("exported"),
            "exportedAtNote" to j("generated from actual V4 ShuffleWallGenerator/ShuffleSlots/ShuffleTargets calls; no expected value is hand-written"),
            "v4RulesSourceNote" to j(
                "engine/src/main/kotlin/com/majiang/stacking is the V4 production source; the v4Commit above is the exact tree used"
            ),
            "rngGapNote" to j(
                "OPEN DECISION D-09: V4 walls derive from JVM kotlin.random.Random(seed) which is not a cross-runtime " +
                    "replay specification. These walls are behavior samples for law verification and provenance only; " +
                    "V5 replay records explicit random inputs (base wall order + consumed doubles) instead of seeds. " +
                    "C# golden tests must verify structural laws and pure math against this file, never wall bytes."
            ),
            "generationResult" to obj(
                "cases" to j(cases.size),
                "slotMatrixEntries" to j(slotMatrix.size),
                "threeCasterChangedSeeds" to j(threeChangedCount),
                "exporterTestResult" to j("PASSED")
            ),
            "slotMatrix" to arr(slotMatrix),
            "demandVectors" to obj(
                *targets.map { target ->
                    target.name to J.Array(demandVectors.getValue(target).map { d(it) }, compact = true)
                }.toTypedArray()
            ),
            "overlapMatrix" to obj(
                *targets.map { a ->
                    a.name to obj(
                        *targets.map { b -> b.name to d(overlap.getValue(a).getValue(b)) }.toTypedArray()
                    )
                }.toTypedArray()
            ),
            "configSnapshot" to configSnapshot,
            "cases" to arr(cases)
        )

        val path = repoRoot().resolve("tests/v5-golden/shuffle-v1.json")
        Files.createDirectories(path.parent)
        Files.write(path, root.render().toByteArray(StandardCharsets.UTF_8))
        assertTrue(Files.size(path) > 50_000L, "golden file looks unexpectedly small: ${Files.size(path)} bytes")
        assertTrue(Files.exists(path), "golden file was not written: $path")
    }

    private fun tilesJson(tiles: List<Tile>): J = J.Array(tiles.map { j(it.tileId) }, compact = true)

    private fun intsJson(values: List<Int>): J = J.Array(values.map { j(it) }, compact = true)

    private fun tiersJson(tiers: Map<Int, ShuffleTier>): J = obj(
        *tiers.map { (n, tier) ->
            n.toString() to obj(
                "strength" to d(tier.strength),
                "initialBudget" to j(tier.initialBudget),
                "drawBudget" to j(tier.drawBudget)
            )
        }.toTypedArray()
    )

    private fun d(value: Double): J = J.DNum(value)

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

    private fun v4Commit(): String {
        return try {
            val process = ProcessBuilder("git", "-C", repoRoot().toString(), "rev-parse", "HEAD")
                .redirectErrorStream(true)
                .start()
            val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
            if (process.waitFor() == 0 && text.isNotEmpty()) text else "unknown"
        } catch (e: Exception) {
            "unknown"
        }
    }
}

// -------------------------------------------------------------------------
// Small JSON helpers (local copy of the scoring exporter pattern; adds DNum)
// -------------------------------------------------------------------------

private sealed class J {
    data class Str(val value: String) : J()
    data class Num(val value: Long) : J()
    data class DNum(val value: Double) : J()
    data class Bool(val value: Boolean) : J()
    object Null : J()
    data class Array(val items: List<J>, val compact: Boolean = false) : J()
    data class Object(val fields: List<Pair<String, J>>) : J()

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

private fun j(value: String): J = J.Str(value)
private fun j(value: Int): J = J.Num(value.toLong())
private fun j(value: Long): J = J.Num(value)
private fun j(value: Boolean): J = J.Bool(value)
private fun obj(vararg fields: Pair<String, J>): J = J.Object(fields.toList())
private fun arr(items: List<J>): J = J.Array(items)

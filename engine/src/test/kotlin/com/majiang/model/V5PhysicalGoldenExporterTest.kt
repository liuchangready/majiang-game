package com.majiang.model

import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleSlots
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleWallGenerator
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.security.MessageDigest
import java.util.Locale

/**
 * P0.1-a/P0.1-b/P1.1 物理牌 V4 -> C# 黄金导出器（只读 V4 生产代码）。
 *
 * 仅当 `V5_PHYSICAL_GOLDEN_EXPORT=1`（或系统属性 v5.physical.export=true）时执行。
 * 覆盖清单中的且仅有六项：G-001a、G-001b、G-001c、G-002、G-003a、G-003b。
 * 所有 expected 值都来自本测试内真实调用 V4 Tile/TilePool/TileLocation/
 * StateInvariants/ShuffleWallGenerator/TestTableBuilder 的结果；没有手填预期哈希，
 * 没有读源码推断行为，也没有把 C# 实现当作 oracle。
 *
 * 本导出器不是 P1.1 的重新开发，不改 V4 规则、不改其他黄金文件、不触及
 * Domain/csproj/CI/actor/Wire。G-003b 的反射仅存在于本测试内，用于删除
 * standalone TilePool 的一个 private tileMap 键；不改生产代码。
 */
class V5PhysicalGoldenExporterTest {

    @Test
    fun exportPhysicalGoldenSamples() {
        if (System.getenv("V5_PHYSICAL_GOLDEN_EXPORT") != "1" &&
            System.getProperty("v5.physical.export") != "true"
        ) {
            return
        }

        val root = repoRoot()
        val standardSet = TilePool.buildStandardSet()
        val commit = git("rev-parse", "HEAD")
        val status = git("status", "--porcelain=v1", "--untracked-files=all")
        val taskPaths = setOf(
            "engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt",
            "tests/v5-golden/physical-v1.json",
            "dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs",
            "doc/v5-重构/V5-P11-PHYSICAL-GOLDEN.md"
        )
        val trackedModified = git("diff", "--name-only", "HEAD", "--")
            .lineSequence().map { it.trim() }.filter { it.isNotEmpty() }.toList()
        val trackedProductionModified = trackedModified.filterNot { it in taskPaths }

        // ---------------------------------------------------------------
        // G-001a: standard 136 identities, 34 types x 4 copies, red fives.
        // ---------------------------------------------------------------
        assertEquals(136, standardSet.size)
        assertEquals(136, standardSet.toSet().size)
        assertEquals((0..135).toList(), standardSet.map { it.tileId })
        val redFives = standardSet.filter { it.isRedFive }
        assertEquals(3, redFives.size)
        assertEquals(
            setOf(Tile(Suit.MAN, 5, 3), Tile(Suit.PIN, 5, 3), Tile(Suit.SOU, 5, 3)),
            redFives.toSet()
        )
        assertEquals(133, standardSet.count { !it.isRedFive })
        for (tile in standardSet) {
            assertEquals(tile.typeIndex * 4 + tile.copyIndex, tile.tileId, "$tile tileId formula")
            assertEquals(tile.tileId / 4, tile.typeIndex, "$tile typeIndex")
            assertEquals(tile.tileId % 4, tile.copyIndex, "$tile copyIndex")
        }

        val g001a = sample(
            id = "G-001a",
            manifestStatusBefore = "pending",
            sourceFile = "engine/src/main/kotlin/com/majiang/model/Tile.kt; engine/src/main/kotlin/com/majiang/model/TilePool.kt",
            testMethod = "exportPhysicalGoldenSamples / G-001a",
            note = "TilePool.buildStandardSet() real V4 call; no random, clock or session.",
            input = notApplicableInput("TilePool.buildStandardSet(); no random, no clock"),
            expected = obj(
                "size" to j(standardSet.size),
                "distinctIdentities" to j(standardSet.toSet().size),
                "redFiveCount" to j(redFives.size),
                "nonRedFiveCount" to j(standardSet.count { !it.isRedFive }),
                "standardSetOrderIsTileIdOrder" to j(standardSet.map { it.tileId } == (0..135).toList()),
                "redFiveTileIds" to compactInts(redFives.map { it.tileId }),
                "tiles" to arr(standardSet.map { tileJson(it) })
            )
        )

        // ---------------------------------------------------------------
        // G-001b: 34-dimensional boundary indices and 34 groups x 4.
        // ---------------------------------------------------------------
        val boundaries = listOf(0, 8, 9, 17, 18, 26, 27, 30, 31, 33)
        val boundarySamples = boundaries.map { typeIndex ->
            val tile = Tile.fromTypeIndex(typeIndex, 0)
            assertEquals(typeIndex, tile.typeIndex)
            assertEquals(0, tile.copyIndex)
            tileJson(tile)
        }
        val grouped = standardSet.groupBy { it.typeIndex }
        assertEquals(34, grouped.size)
        assertEquals(136, grouped.values.sumOf { it.size })
        val groupJson = (0..33).map { typeIndex ->
            val four = (0..3).map { copy -> Tile.fromTypeIndex(typeIndex, copy) }
            assertEquals(4, four.size)
            assertEquals(4, four.map { it.tileId }.toSet().size)
            for (tile in four) {
                assertEquals(typeIndex, tile.typeIndex)
                assertEquals(typeIndex * 4 + tile.copyIndex, tile.tileId)
            }
            obj(
                "typeIndex" to j(typeIndex),
                "count" to j(four.size),
                "tileIds" to compactInts(four.map { it.tileId }),
                "suit" to j(four[0].suit.name),
                "rank" to j(four[0].rank)
            )
        }
        for (typeIndex in 0..33) {
            assertEquals(4, grouped.getValue(typeIndex).size, "typeIndex=$typeIndex group size")
        }

        val g001b = sample(
            id = "G-001b",
            manifestStatusBefore = "pending",
            sourceFile = "engine/src/main/kotlin/com/majiang/model/Tile.kt; engine/src/main/kotlin/com/majiang/model/TilePool.kt",
            testMethod = "exportPhysicalGoldenSamples / G-001b",
            note = "Tile.fromTypeIndex/typeIndex and TilePool.buildStandardSet real V4 calls.",
            input = notApplicableInput("Tile.fromTypeIndex(boundary, 0) and 136 standard identities; no random, no clock"),
            expected = obj(
                "boundaryTypeIndexes" to compactInts(boundaries),
                "boundarySamples" to arr(boundarySamples),
                "groups" to arr(groupJson),
                "distinctTypeIndexes" to j(grouped.size),
                "totalTiles" to j(standardSet.size),
                "allGroupsFour" to j(grouped.values.all { it.size == 4 }),
                "tileIdFormula" to j("typeIndex * 4 + copyIndex"),
                "suitOrder" to j("MAN 0..8, SOU 9..17, PIN 18..26, WIND 27..30, DRAGON 31..33")
            )
        )

        // ---------------------------------------------------------------
        // G-001c: fromTileId 0..135 roundtrip and out-of-range rejection.
        // ---------------------------------------------------------------
        val validRoundTrips = (0..135).map { id ->
            val tile = Tile.fromTileId(id)
            assertEquals(id, tile.tileId)
            assertEquals(id / 4, tile.typeIndex)
            assertEquals(id % 4, tile.copyIndex)
            obj(
                "tileId" to j(id),
                "typeIndex" to j(tile.typeIndex),
                "copyIndex" to j(tile.copyIndex),
                "suit" to j(tile.suit.name),
                "rank" to j(tile.rank)
            )
        }
        val invalidInputs = listOf(-1, 136, Int.MIN_VALUE, Int.MAX_VALUE)
        val invalidJson = invalidInputs.map { input ->
            val ex = assertThrows<IllegalArgumentException> { Tile.fromTileId(input) }
            assertEquals("java.lang.IllegalArgumentException", ex.javaClass.name)
            exceptionJson(ex, "invalid-tile-id", extra = arrayOf("input" to j(input)))
        }

        val g001c = sample(
            id = "G-001c",
            manifestStatusBefore = "pending-v4-replay",
            sourceFile = "engine/src/main/kotlin/com/majiang/model/Tile.kt:112-120",
            testMethod = "exportPhysicalGoldenSamples / G-001c (new probe)",
            note = "fromTileId(0), fromTileId(135), every 0..135 roundtrip, and out-of-range inputs.",
            input = notApplicableInput("Tile.fromTileId(input) for 0..135 and invalid inputs; no random, no clock"),
            expected = obj(
                "validCount" to j(validRoundTrips.size),
                "validRoundTrips" to arr(validRoundTrips),
                "allRoundTripEqual" to j(validRoundTrips.size == 136),
                "boundaries" to arr(listOf(tileJson(Tile.fromTileId(0)), tileJson(Tile.fromTileId(135)))),
                "invalid" to arr(invalidJson),
                "csharpExceptionMapping" to obj(
                    "v4ExceptionType" to j("java.lang.IllegalArgumentException"),
                    "csharpExceptionType" to j("System.ArgumentOutOfRangeException"),
                    "semanticCategory" to j("invalid-tile-id")
                )
            )
        )

        // ---------------------------------------------------------------
        // G-002: TileLocation legal/illegal zone-owner matrix.
        // ---------------------------------------------------------------
        val locationCases = listOf(
            LocationCase("loc-wall-default", Zone.WALL, -1, true, "seatless-factory"),
            LocationCase("loc-wall-owner-0", Zone.WALL, 0, false, "structural-owner-forbidden"),
            LocationCase("loc-wall-owner-3", Zone.WALL, 3, false, "structural-owner-forbidden"),
            LocationCase("loc-deadwall-default", Zone.DEAD_WALL, -1, true, "seatless-factory"),
            LocationCase("loc-deadwall-owner-0", Zone.DEAD_WALL, 0, false, "structural-owner-forbidden"),
            LocationCase("loc-unassigned-default", Zone.UNASSIGNED, -1, true, "seatless-factory"),
            LocationCase("loc-unassigned-owner-0", Zone.UNASSIGNED, 0, false, "structural-owner-forbidden"),
            LocationCase("loc-hand-0", Zone.HAND, 0, true, "seat-factory"),
            LocationCase("loc-hand-3", Zone.HAND, 3, true, "seat-factory"),
            LocationCase("loc-hand-minus1", Zone.HAND, -1, false, "invalid-seat"),
            LocationCase("loc-hand-4", Zone.HAND, 4, false, "invalid-seat"),
            LocationCase("loc-discard-0", Zone.DISCARD, 0, true, "seat-factory"),
            LocationCase("loc-discard-3", Zone.DISCARD, 3, true, "seat-factory"),
            LocationCase("loc-discard-minus1", Zone.DISCARD, -1, false, "invalid-seat"),
            LocationCase("loc-discard-4", Zone.DISCARD, 4, false, "invalid-seat"),
            LocationCase("loc-meld-0", Zone.MELD, 0, true, "seat-factory"),
            LocationCase("loc-meld-3", Zone.MELD, 3, true, "seat-factory"),
            LocationCase("loc-meld-minus1", Zone.MELD, -1, false, "invalid-seat"),
            LocationCase("loc-sleeve-1", Zone.SLEEVE, 1, true, "seat-factory"),
            LocationCase("loc-sleeve-4", Zone.SLEEVE, 4, false, "invalid-seat")
        )
        val locationJsonCases = locationCases.map { case ->
            val attempt = runCatching { TileLocation(case.zone, case.player) }
            assertEquals(case.accepted, attempt.isSuccess, "${case.id} accepted=${case.accepted}")
            if (case.accepted) {
                val location = attempt.getOrThrow()
                assertEquals(case.zone, location.zone)
                assertEquals(case.player, location.player)
                obj(
                    "caseId" to j(case.id),
                    "zone" to j(case.zone.name),
                    "player" to j(case.player),
                    "outcome" to j("accepted"),
                    "constructed" to j(true),
                    "zoneOut" to j(location.zone.name),
                    "playerOut" to j(location.player),
                    "csharpMapping" to j(case.csharpMapping)
                )
            } else {
                val ex = attempt.exceptionOrNull()
                assertTrue(ex is IllegalArgumentException, "${case.id} expected IllegalArgumentException")
                ex as IllegalArgumentException
                assertEquals("java.lang.IllegalArgumentException", ex.javaClass.name)
                obj(
                    "caseId" to j(case.id),
                    "zone" to j(case.zone.name),
                    "player" to j(case.player),
                    "outcome" to j("rejected"),
                    "constructed" to j(false),
                    "exceptionType" to j(ex.javaClass.name),
                    "message" to j(ex.message ?: ""),
                    "semanticCategory" to j(
                        if (case.zone in setOf(Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED)) {
                            "invalid-zone-owner"
                        } else {
                            "invalid-player-owner"
                        }
                    ),
                    "csharpMapping" to j(case.csharpMapping)
                )
            }
        }
        assertEquals(
            locationCases.count { it.accepted },
            locationJsonCases.count { it.first("outcome") == j("accepted") },
            "accepted TileLocation case count"
        )

        val g002 = sample(
            id = "G-002",
            manifestStatusBefore = "pending-v4-replay",
            sourceFile = "engine/src/main/kotlin/com/majiang/model/TileLocation.kt:14-22",
            testMethod = "exportPhysicalGoldenSamples / G-002 (new probe)",
            note = "Direct TileLocation(zone, player) construction; pure value object, failed construction yields no state.",
            input = notApplicableInput("TileLocation(zone, player) matrix; no random, no clock"),
            expected = obj(
                "cases" to arr(locationJsonCases),
                "acceptedCount" to j(locationCases.count { it.accepted }),
                "rejectedCount" to j(locationCases.count { !it.accepted }),
                "csharpStructuralNote" to j(
                    "C# TilePosition has no zone+seat constructor: seatless zones cannot carry a seat, " +
                        "and seat-required zones reject -1/4 at construction. Owner rejection is therefore " +
                        "structurally enforced for WALL/DEAD_WALL/UNASSIGNED rather than exception-identical."
                )
            )
        )

        // ---------------------------------------------------------------
        // G-003a: 32 fixed seeds, full wall + 136 positions, real V4 output.
        // ---------------------------------------------------------------
        val config = ShuffleConfig(enabled = true)
        val plans = listOf(
            SeatShufflePlan(0, ShuffleSelection(ShuffleTarget.KOKUSHI)),
            SeatShufflePlan(1, ShuffleSelection(ShuffleTarget.DAISANGEN)),
            SeatShufflePlan(2, ShuffleSelection(ShuffleTarget.CHINITSU_MAN))
        )
        val generator = ShuffleWallGenerator()
        val standardIds = standardSet.map { it.tileId }.toSet()
        val shuffleCases = (0L..31L).map { seed ->
            val dealer = (seed % 4L).toInt()
            val wall = generator.generate(seed, dealer, plans, config)
            val base = generator.generate(seed, dealer, emptyList(), config)
            val reversed = generator.generate(seed, dealer, plans.reversed(), config)

            assertEquals(136, wall.size, "seed=$seed wall size")
            assertEquals(136, wall.map { it.tileId }.toSet().size, "seed=$seed wall distinct")
            assertEquals(standardIds, wall.map { it.tileId }.toSet(), "seed=$seed standard set")
            assertEquals(3, wall.count { it.isRedFive }, "seed=$seed red five count")
            assertEquals(wall, reversed, "seed=$seed order independence")
            assertEquals(
                base.drop(ShuffleSlots.DEAD_WALL_START),
                wall.drop(ShuffleSlots.DEAD_WALL_START),
                "seed=$seed dead wall isolation"
            )

            // Real V4 TilePool is used to read back the exact zone/player for every
            // wall slot. The slot index is the accepted 0..121 / 122..135 layout.
            val pool = TilePool()
            wall.forEachIndexed { index, tile ->
                pool.setLocation(
                    tile,
                    if (index < ShuffleSlots.DEAD_WALL_START) TileLocation.WALL else TileLocation.DEAD_WALL
                )
            }
            pool.assertConservation()
            val positions = wall.mapIndexed { index, tile ->
                val location = pool.getLocation(tile)
                    ?: error("V4 TilePool lost tile $tile after setLocation at slot $index")
                obj(
                    "tileId" to j(tile.tileId),
                    "zone" to j(location.zone.name),
                    "seat" to j(location.player),
                    "slot" to j(index)
                )
            }

            obj(
                "seed" to j(seed),
                "dealerSeat" to j(dealer),
                "wall" to compactInts(wall.map { it.tileId }),
                "baseWall" to compactInts(base.map { it.tileId }),
                "redFiveTileIds" to compactInts(wall.filter { it.isRedFive }.map { it.tileId }),
                "redFiveCount" to j(wall.count { it.isRedFive }),
                "conserved" to j(true),
                "positions" to arr(positions),
                "deadWallSegmentEqual" to j(
                    base.drop(ShuffleSlots.DEAD_WALL_START) == wall.drop(ShuffleSlots.DEAD_WALL_START)
                ),
                "reversedEquals" to j(wall == reversed),
                "wallCanonicalSha256" to j(wallCanonicalDigest(seed, wall))
            )
        }
        assertTrue(shuffleCases.isNotEmpty())

        val g003a = sample(
            id = "G-003a",
            manifestStatusBefore = "pending",
            sourceFile = "engine/src/main/kotlin/com/majiang/model/TilePool.kt:197-205; engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt",
            testMethod = "exportPhysicalGoldenSamples / G-003a",
            note = "Accepted ShuffleAlgorithmAcceptanceTest fixture: seeds 0..31, dealer=seed%4, three plans, ShuffleConfig(enabled=true).",
            input = obj(
                "fixture" to j("ShuffleWallGenerator.generate(seed, seed%4, threePlans, ShuffleConfig(enabled=true))"),
                "seed" to j("0..31 fixed; the actual seed is recorded in each case"),
                "dealerFormula" to j("dealerSeat = seed % 4"),
                "plans" to arr(
                    plans.map { plan ->
                        obj(
                            "seat" to j(plan.seat),
                            "target" to j(plan.selection.target?.name ?: "null")
                        )
                    }
                ),
                "config" to j("ShuffleConfig(enabled=true)"),
                "randomInputs" to j(
                    "Fixed V4 seeds 0..31 are the explicit random inputs; each case records the complete " +
                        "136-tile wall and 136 TilePool positions, not just the seed."
                ),
                "clockMillis" to j("not-applicable"),
                "sessionId" to j("not-applicable"),
                "roundId" to j("not-applicable"),
                "requestId" to j("not-applicable")
            ),
            expected = obj(
                "seedCount" to j(shuffleCases.size),
                "cases" to arr(shuffleCases),
                "allConserved" to j(shuffleCases.all { it.first("conserved") == j(true) }),
                "allRedFiveThree" to j(shuffleCases.all { it.first("redFiveCount") == j(3) }),
                "allOrderIndependent" to j(shuffleCases.all { it.first("reversedEquals") == j(true) }),
                "allDeadWallIsolated" to j(shuffleCases.all { it.first("deadWallSegmentEqual") == j(true) }),
                "positionsRecorded" to j(shuffleCases.size * 136)
            )
        )

        // ---------------------------------------------------------------
        // G-003b: standalone missing-key audit + bound ledger mismatch.
        // Duplicate-key MutableMap branch is not constructible; record the
        // actual overwrite observation instead of pretending to reach it.
        // ---------------------------------------------------------------
        val deletedTile = Tile(Suit.MAN, 1, 0)
        val standalone = TilePool()
        val standaloneBeforeLocation = standalone.getLocation(deletedTile)
        assertEquals(TileLocation.UNASSIGNED, standaloneBeforeLocation)
        assertEquals(136, standalone.size)

        val tileMapField = TilePool::class.java.getDeclaredField("tileMap")
        tileMapField.isAccessible = true
        @Suppress("UNCHECKED_CAST")
        val rawTileMap = tileMapField.get(standalone) as MutableMap<Tile, TileLocation>
        assertEquals(136, rawTileMap.size)
        val removed = rawTileMap.remove(deletedTile)
        assertTrue(removed != null)
        assertEquals(135, rawTileMap.size)

        val probeAFirst = runCatching { standalone.assertConservation() }
        val probeAFirstEx = probeAFirst.exceptionOrNull()
        assertTrue(probeAFirstEx is IllegalArgumentException, "standalone audit must reject 135 keys")
        probeAFirstEx as IllegalArgumentException
        assertEquals("java.lang.IllegalArgumentException", probeAFirstEx.javaClass.name)
        assertTrue((probeAFirstEx.message ?: "").contains("exactly 136"))
        assertTrue((probeAFirstEx.message ?: "").contains("135"))

        val probeAAfterPoolSize = standalone.size
        val probeAAfterMapSize = rawTileMap.size
        val probeAAfterLocation = standalone.getLocation(deletedTile)
        val missingAfterProbeA = standardSet.filter { standalone.getLocation(it) == null }.map { it.tileId }
        assertNull(probeAAfterLocation)
        assertEquals(1, missingAfterProbeA.size)

        val probeASecond = runCatching { standalone.assertConservation() }
        val probeASecondEx = probeASecond.exceptionOrNull()
        assertTrue(probeASecondEx is IllegalArgumentException)
        probeASecondEx as IllegalArgumentException
        assertEquals(probeAFirstEx.message, probeASecondEx.message)

        // Probe B: legal bound state, then desynchronise one ledger entry only.
        val boundState = TestTableBuilder.create()
        val targetTile = boundState.players[0].closedTiles.first()
        val boundBeforeLocation = boundState.tilePool.getLocation(targetTile)
            ?: error("bound fixture has no pool location for $targetTile")
        val actualBefore = TestTableBuilder.actualTiles(boundState).map { it.tileId }
        val boundBeforeAudit = runCatching { boundState.tilePool.assertConservation() }
        assertTrue(boundBeforeAudit.isSuccess, "fixture must start with a valid bound audit")
        assertEquals(136, actualBefore.size)
        assertEquals(136, actualBefore.toSet().size)
        assertEquals(136, boundState.tilePool.size)

        boundState.tilePool.setLocation(targetTile, TileLocation.WALL)
        val probeBFirst = runCatching { boundState.tilePool.assertConservation() }
        val probeBFirstEx = probeBFirst.exceptionOrNull()
        assertTrue(probeBFirstEx is IllegalStateException, "bound audit must reject a desynchronised ledger")
        probeBFirstEx as IllegalStateException
        assertEquals("java.lang.IllegalStateException", probeBFirstEx.javaClass.name)
        assertTrue((probeBFirstEx.message ?: "").contains("TilePool audit failed"))

        val boundAfterLocation = boundState.tilePool.getLocation(targetTile)
        val actualAfter = TestTableBuilder.actualTiles(boundState).map { it.tileId }
        val boundAfterPoolSize = boundState.tilePool.size
        val closedTilesStillContains = boundState.players[0].closedTiles.contains(targetTile)
        assertEquals(TileLocation.WALL, boundAfterLocation)
        assertEquals(136, actualAfter.size)
        assertEquals(actualBefore.toSet(), actualAfter.toSet())
        assertEquals(136, boundAfterPoolSize)
        assertTrue(closedTilesStillContains)

        val probeBSecond = runCatching { boundState.tilePool.assertConservation() }
        val probeBSecondEx = probeBSecond.exceptionOrNull()
        assertTrue(probeBSecondEx is IllegalStateException)
        probeBSecondEx as IllegalStateException
        assertEquals(probeBFirstEx.message, probeBSecondEx.message)

        // Duplicate-key observation: MutableMap<Tile, TileLocation> cannot hold two
        // entries with equal keys; the second put overwrites the first.
        val duplicateMap = mutableMapOf<Tile, TileLocation>()
        duplicateMap[deletedTile] = TileLocation.WALL
        val duplicateFirstValue = duplicateMap[deletedTile]
        duplicateMap[deletedTile] = TileLocation.hand(0)
        val duplicateFinalValue = duplicateMap[deletedTile]
        assertEquals(1, duplicateMap.size)
        assertTrue(duplicateFirstValue != duplicateFinalValue)
        assertEquals(TileLocation.hand(0), duplicateFinalValue)

        val g003b = sample(
            id = "G-003b",
            manifestStatusBefore = "pending-v4-replay",
            sourceFile = "engine/src/main/kotlin/com/majiang/model/TilePool.kt:28,48-51,128-145; engine/src/main/kotlin/com/majiang/state/StateInvariants.kt:306-316",
            testMethod = "exportPhysicalGoldenSamples / G-003b (new probe; test-only reflection)",
            note = "Probe A removes one private tileMap key from a standalone pool; Probe B desynchronises one bound pool ledger entry. Neither audit repairs state.",
            input = notApplicableInput("TilePool + TestTableBuilder fixtures; no random, no clock"),
            expected = obj(
                "probeA" to obj(
                    "fixture" to j("TilePool(gameState=null); test-only java.lang.reflect.Field tileMap remove(Tile(MAN,1,0))"),
                    "target" to tileJson(deletedTile),
                    "before" to obj(
                        "poolSize" to j(136),
                        "mapSize" to j(136),
                        "location" to locationJson(TileLocation.UNASSIGNED)
                    ),
                    "mutation" to obj(
                        "via" to j("private field tileMap via java.lang.reflect.Field#setAccessible(true)"),
                        "removed" to j(removed != null),
                        "mapSizeBefore" to j(136),
                        "mapSizeAfter" to j(135)
                    ),
                    "firstAudit" to exceptionJson(probeAFirstEx, "pool-size"),
                    "after" to obj(
                        "poolSize" to j(probeAAfterPoolSize),
                        "mapSize" to j(probeAAfterMapSize),
                        "location" to J.Null,
                        "missingTileIds" to compactInts(missingAfterProbeA)
                    ),
                    "secondAudit" to exceptionJson(probeASecondEx, "pool-size"),
                    "autoRepairObserved" to j(false)
                ),
                "probeB" to obj(
                    "fixture" to j("TestTableBuilder.create() legal state; tilePool.setLocation(players[0].closedTiles[0], TileLocation.WALL)"),
                    "target" to tileJson(targetTile),
                    "before" to obj(
                        "auditOutcome" to j("accepted"),
                        "poolSize" to j(136),
                        "location" to locationJson(boundBeforeLocation),
                        "actualTiles" to compactInts(actualBefore),
                        "actualTilesCount" to j(actualBefore.size),
                        "actualTilesSha256" to j(sha256Text(actualBefore.joinToString(",")))
                    ),
                    "firstAudit" to exceptionJson(probeBFirstEx, "pool-ledger-mismatch"),
                    "after" to obj(
                        "poolSize" to j(boundAfterPoolSize),
                        "poolZone" to j(boundAfterLocation!!.zone.name),
                        "poolPlayer" to j(boundAfterLocation.player),
                        "closedTilesStillContains" to j(closedTilesStillContains),
                        "actualTiles" to compactInts(actualAfter),
                        "actualTilesCount" to j(actualAfter.size),
                        "actualTilesSha256" to j(sha256Text(actualAfter.joinToString(",")))
                    ),
                    "secondAudit" to exceptionJson(probeBSecondEx, "pool-ledger-mismatch"),
                    "autoRepairObserved" to j(false),
                    "csharpMapping" to obj(
                        "status" to j("structural-immutable-map-no-separate-ledger"),
                        "note" to j(
                            "C# TilePositionMap owns the only placement ledger; there is no mutable pool map " +
                                "to desynchronise. Invariant is enforced at construction: duplicate tile, " +
                                "duplicate position and <136 placements throw; AsPlacements is read-only."
                        )
                    )
                ),
                "duplicateKeyStaticLimitation" to obj(
                    "status" to j("NOT_CONSTRUCTIBLE"),
                    "observedMapSize" to j(duplicateMap.size),
                    "firstValue" to locationJson(duplicateFirstValue!!),
                    "finalValue" to locationJson(duplicateFinalValue!!),
                    "auditBranchReachability" to j("unreachable"),
                    "reason" to j(
                        "MutableMap<Tile, TileLocation> keys by Tile equality; putting an equal key overwrites " +
                            "instead of creating a second entry, so assertStandaloneConservation's duplicate-key " +
                            "check cannot be reached through this container. Duplicate physical wall is covered " +
                            "by G-010, not fabricated here."
                    )
                )
            )
        )

        // ---------------------------------------------------------------
        // Provenance and file.
        // ---------------------------------------------------------------
        val sourceFiles = listOf(
            "engine/src/main/kotlin/com/majiang/model/Tile.kt",
            "engine/src/main/kotlin/com/majiang/model/TileLocation.kt",
            "engine/src/main/kotlin/com/majiang/model/TilePool.kt",
            "engine/src/main/kotlin/com/majiang/state/StateInvariants.kt",
            "engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt",
            "engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt",
            "engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt",
            "engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt"
        )
        val sourceHashes = sourceFiles.map { relative ->
            val path = root.resolve(relative)
            assertTrue(Files.exists(path), "source file missing: $relative")
            obj("file" to j(relative), "sha256" to j(sha256File(path)))
        }

        val exporterPath = root.resolve("engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt")
        assertTrue(Files.exists(exporterPath))

        val rootJson = obj(
            "schema" to j("v5-physical-golden-v1"),
            "generator" to j("engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt"),
            "generatorVersion" to j(1),
            "rulesetVersion" to j(1),
            "v4Commit" to j(commit),
            "v4ProductionTreeClean" to j(trackedProductionModified.isEmpty()),
            "worktreeDirty" to j(status.isNotEmpty()),
            "gitStatusPorcelain" to J.Array(
                status.lineSequence().filter { it.isNotBlank() }.map { j(it) }.toList(),
                compact = true
            ),
            "trackedModifiedFiles" to compactStrings(trackedModified),
            "trackedProductionModifiedFiles" to compactStrings(trackedProductionModified),
            "taskPaths" to compactStrings(taskPaths.toList()),
            "exporterSourceSha256" to j(sha256File(exporterPath)),
            "sourceHashAlgorithm" to j(
                "SHA-256 over UTF-8 text with CRLF normalized to LF; line endings do not affect source hashes"
            ),
            "v4SourceHashes" to arr(sourceHashes),
            "generatedByCommand" to j(GENERATION_COMMAND),
            "extractionStatus" to j("exported"),
            "clockUsed" to j(false),
            "randomInputsNote" to j(
                "No implicit random or clock input is used. G-003a uses the accepted fixed seeds 0..31 and " +
                    "records each complete wall; all other samples are pure value probes with no random input."
            ),
            "v4RulesSourceNote" to j(
                "This file is a generation-time snapshot. v4Commit is git HEAD when the exporter ran; " +
                    "trackedModifiedFiles is empty, so V4 production sources equal that commit. Only the four " +
                    "authorized task files are untracked/new."
            ),
            "hashCrosswalk" to obj(
                "v4CanonicalStateHash" to j("NOT_AVAILABLE"),
                "v4Reason" to j(
                    "V4 engine exposes no cross-language canonical state hash; StateInvariants compares " +
                        "object fields and throws. No V4 internal digest is claimed to equal any C# hash."
                ),
                "csharpHash" to j(
                    "Mahjong.Domain.TileStateHash encoding v3, 64-bit FNV-1a over the canonical field stream. " +
                        "It is a V5/C# definition, not a V4 digest."
                ),
                "wallCanonicalDigestAlgorithm" to j(
                    "SHA-256 over ASCII 'G-003a|<seed>|' followed by the 136 tileIds in slot order joined by ','"
                ),
                "csharpHashVerification" to j(
                    "C# golden tests verify the C# hash is stable across rebuilding the same map; no C# hash is " +
                        "stored in this V4-generated file because that would be circular."
                )
            ),
            "scope" to obj(
                "manifestEntriesTargeted" to j(6),
                "manifestEntriesExportedByThisRun" to j(6),
                "manifestEntriesNotFullAudit" to j(67),
                "fullManifestAudited" to j(false),
                "targetIds" to compactStrings(listOf("G-001a", "G-001b", "G-001c", "G-002", "G-003a", "G-003b")),
                "note" to j(
                    "Only the six P0.1-a/P0.1-b/P1.1 physical entries are covered. The other 67 manifest " +
                        "entries were not re-audited; no full 73-case or G1 claim is made. Existing " +
                        "scoring/round/cheat/shuffle golden files were not regenerated."
                )
            ),
            "generationResult" to obj(
                "samplesExported" to j(6),
                "failures" to j(0),
                "remainingApplicable" to j(0),
                "totalWallsRecorded" to j(shuffleCases.size * 2),
                "totalPositionsRecorded" to j(shuffleCases.size * 136),
                "exporterTestResult" to j("PASSED")
            ),
            "samples" to arr(listOf(g001a, g001b, g001c, g002, g003a, g003b))
        )

        val path = root.resolve("tests/v5-golden/physical-v1.json")
        Files.createDirectories(path.parent)
        val rendered = rootJson.render()
        Files.write(path, rendered.toByteArray(StandardCharsets.UTF_8))
        assertTrue(Files.exists(path))
        assertTrue(Files.size(path) > 100_000L, "golden file looks unexpectedly small: ${Files.size(path)} bytes")
    }

    private data class LocationCase(
        val id: String,
        val zone: Zone,
        val player: Int,
        val accepted: Boolean,
        val csharpMapping: String
    )

    private fun sample(
        id: String,
        manifestStatusBefore: String,
        sourceFile: String,
        testMethod: String,
        note: String,
        input: J,
        expected: J
    ): J = obj(
        "sampleId" to j(id),
        "manifestStatusBefore" to j(manifestStatusBefore),
        "extractionStatus" to j("exported"),
        "source" to obj(
            "file" to j(sourceFile),
            "testClass" to j("com.majiang.model.V5PhysicalGoldenExporterTest"),
            "testMethod" to j(testMethod),
            "note" to j(note)
        ),
        "command" to j(GENERATION_COMMAND),
        "input" to input,
        "expected" to expected
    )

    private fun notApplicableInput(fixture: String): J = obj(
        "fixture" to j(fixture),
        "seed" to j("not-applicable"),
        "clockMillis" to j("not-applicable"),
        "sessionId" to j("not-applicable"),
        "roundId" to j("not-applicable"),
        "decisionId" to j("not-applicable"),
        "controlEpoch" to j("not-applicable"),
        "requestId" to j("not-applicable")
    )

    private fun tileJson(tile: Tile): J = obj(
        "tileId" to j(tile.tileId),
        "suit" to j(tile.suit.name),
        "rank" to j(tile.rank),
        "copyIndex" to j(tile.copyIndex),
        "typeIndex" to j(tile.typeIndex),
        "isRedFive" to j(tile.isRedFive),
        "isNumberTile" to j(tile.isNumberTile),
        "isHonor" to j(tile.isHonor),
        "isTerminal" to j(tile.isTerminal),
        "isTerminalOrHonor" to j(tile.isTerminalOrHonor),
        "isSimple" to j(tile.isSimple)
    )

    private fun locationJson(location: TileLocation): J = obj(
        "zone" to j(location.zone.name),
        "player" to j(location.player)
    )

    private fun exceptionJson(
        exception: Throwable,
        semanticCategory: String,
        extra: Array<out Pair<String, J>> = emptyArray()
    ): J = obj(
        "outcome" to j("rejected"),
        "exceptionType" to j(exception.javaClass.name),
        "message" to j(exception.message ?: ""),
        "semanticCategory" to j(semanticCategory),
        *extra
    )

    private fun wallCanonicalDigest(seed: Long, wall: List<Tile>): String =
        sha256Text("G-003a|$seed|" + wall.joinToString(",") { it.tileId.toString() })

    private fun compactInts(values: List<Int>): J =
        J.Array(values.map { J.Num(it.toLong()) }, compact = true)

    private fun compactStrings(values: List<String>): J =
        J.Array(values.map { J.Str(it) }, compact = true)

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

    private fun git(vararg args: String): String {
        val process = ProcessBuilder(listOf("git", "-C", repoRoot().toString()) + args.toList())
            .redirectErrorStream(true)
            .start()
        val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
        val exit = process.waitFor()
        check(exit == 0) { "git ${args.joinToString(" ")} failed with $exit: $text" }
        return text
    }

    private fun sha256File(path: Path): String {
        // Source hashes are line-ending independent: normalize CRLF to LF before hashing.
        val text = String(Files.readAllBytes(path), StandardCharsets.UTF_8)
        return sha256Text(text.replace("\r\n", "\n"))
    }

    private fun sha256Text(text: String): String =
        sha256(text.toByteArray(StandardCharsets.UTF_8))

    private fun sha256(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
        return digest.joinToString("") { String.format(Locale.ROOT, "%02x", it) }
    }

    private fun J.first(name: String): J? {
        if (this !is J.Object) return null
        return fields.firstOrNull { it.first == name }?.second
    }

    companion object {
        private const val GENERATION_COMMAND =
            "\$env:V5_PHYSICAL_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test " +
                "--tests \"com.majiang.model.V5PhysicalGoldenExporterTest\" --console=plain"
    }
}

// -------------------------------------------------------------------------
// Small deterministic JSON writer (same pattern as the accepted exporters).
// -------------------------------------------------------------------------

private sealed class J {
    data class Str(val value: String) : J()
    data class Num(val value: Long) : J()
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

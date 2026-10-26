package com.majiang.model

import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.StateInvariants
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
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
 * P0.1 movement-specific V4 -> V5 golden exporter (G-004..G-007).
 *
 * Default run (no export variable) builds and validates every case in memory through the
 * real V4 public entry points and deliberately does not touch the committed fixture.
 * Export is explicit:
 *
 *   $env:V5_MOVEMENT_GOLDEN_EXPORT='1'
 *   .\gradlew.bat :engine:test --tests "com.majiang.model.V5TileMovementGoldenExporterTest" --rerun-tasks --console=plain
 *
 * Every expected value below is the actual result of a real V4 call. No expected hash is
 * hand-written. The exporter only writes `tests/v5-golden/movement-v1.json`; it does not
 * modify the five accepted fixtures, the manifest/catalog, C# production code or V4
 * production code.
 */
class V5TileMovementGoldenExporterTest {

    @Test
    fun exportMovementGoldenSamplesOrValidateInert() {
        val root = repoRoot()
        val standardSet = TilePool.buildStandardSet()
        val cases = buildCases(standardSet)

        validateCases(cases, standardSet)

        val enabled = System.getenv("V5_MOVEMENT_GOLDEN_EXPORT") == "1" ||
            System.getProperty("v5.movement.export") == "true"
        if (!enabled) {
            // Inert mode still proves the V4 cases execute and their captured states are
            // structurally closed. It never writes the committed fixture.
            return
        }

        val commit = git("rev-parse", "HEAD")
        val taskPaths = setOf(
            "engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt",
            "tests/v5-golden/movement-v1.json",
            "dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs",
            "tools/v5-golden/probe-movement-binding.ps1",
            "doc/v5-重构/V5-P01-MOVEMENT-GOLDEN-G004-G007.md"
        )
        val trackedModified = git("diff", "--name-only", "HEAD", "--")
            .lineSequence().map { it.trim() }.filter { it.isNotEmpty() }.toList()
        val productionModified = trackedModified.filterNot { it in taskPaths }
        val exporterPath = root.resolve("engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt")

        val sources = listOf(
            "engine/src/main/kotlin/com/majiang/model/Tile.kt",
            "engine/src/main/kotlin/com/majiang/model/TileLocation.kt",
            "engine/src/main/kotlin/com/majiang/model/TilePool.kt",
            "engine/src/main/kotlin/com/majiang/model/TileMovement.kt",
            "engine/src/main/kotlin/com/majiang/model/Meld.kt",
            "engine/src/main/kotlin/com/majiang/model/PlayerHand.kt",
            "engine/src/main/kotlin/com/majiang/state/GameState.kt",
            "engine/src/main/kotlin/com/majiang/state/StateInvariants.kt",
            "engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt",
            "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt"
        )
        val sourceHashes = sources.map { sourceHash(root.resolve(it), it) }

        val rootJson = mobj(
            "schema" to mj("v5-movement-golden-v1"),
            "generatorVersion" to mj(1),
            "rulesetVersion" to mj(1),
            "generator" to mj("engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt"),
            "generatedByCommand" to mj(GENERATION_COMMAND),
            "extractionStatus" to mj("exported"),
            "v4Commit" to mj(commit),
            "v4ProductionTreeClean" to mj(productionModified.isEmpty()),
            "trackedModifiedFiles" to compactStrings(trackedModified),
            "trackedProductionModifiedFiles" to compactStrings(productionModified),
            "taskPaths" to compactStrings(taskPaths.toList().sorted()),
            "targetIds" to compactStrings(listOf("G-004", "G-005", "G-006", "G-007")),
            "caseCount" to mj(cases.size),
            "clockUsed" to mj(false),
            "implicitRandomUsed" to mj(false),
            "inputModel" to mj(
                "V4 TestTableBuilder fixture + real TileMovement calls. " +
                    "The only preparatory action is a real TileMovement.draw for the closed-kan and " +
                    "added-kan cases so the drawn tile has a production source (WALL), not a preset fixture value."
            ),
            "mapping" to mobj(
                "v4ToCsharpPosition" to mj(
                    "V4 wall index -> C# Wall(index); V4 deadWall index -> C# DeadWall(index); " +
                        "V4 deadWallReplacements index -> C# DeadWallReplacement(index); " +
                        "V4 hand (allClosedTiles sorted by tileId) -> C# Hand(seat, dense sorted slot); " +
                        "V4 meld list index -> C# Meld(seat, meldIndex*4 + offset of tileId-sorted tiles); " +
                        "V4 discards list index -> C# Discard(seat, listIndex); " +
                        "V4 sleeve list index -> C# Sleeve(seat, listIndex)."
                ),
                "v4ConsumedDeadWall" to mj(
                    "V4 keeps the consumed rinshan tile in the deadWall list while rinshanPointer advances; " +
                        "the canonical placement uses the TilePool location (HAND/DISCARD), never DeadWall, " +
                        "for indices below rinshanPointer. C# requires those dead-wall slots to be empty."
                ),
                "v4StaleLastDiscard" to mj(
                    "V4 leaves lastDiscard/lastDiscardPlayer pointing at a tile that a call already moved out " +
                        "of the river, and TileMovement.draw/drawRinshan do not clear a still-valid lastDiscard. " +
                        "C# TilePositionMap clears LastDiscard on a successful draw/call and sets it on discard. " +
                        "Canonical lastDiscard therefore follows the C# evidence; the raw V4 value is kept in " +
                        "v4RawLastDiscard as SOURCE_ONLY."
                ),
                "meldMetadata" to mj(
                    "C# TilePositionMap stores only physical meld slots, not MeldType/fromPlayer/calledTile. " +
                        "The fixture exports that metadata as SOURCE_ONLY; the C# test compares physical groups only."
                ),
                "kanCount" to mj(
                    "TileMovement does not update GameState.totalKanCount; the application layer does. " +
                        "C# TilePositionMap increments KanCount on kan transitions. It is exported as " +
                        "v4TotalKanCount (SOURCE_ONLY) and is not part of the physical diff."
                ),
                "handSlots" to mj(
                    "V4 hand is a sorted list, not a slot-addressed array. Hand slots are therefore canonicalized " +
                        "to dense 0..n-1 in tileId order on both sides; wall/discard/meld/dead-wall slots remain exact."
                ),
                "lastRinshanEvidence" to mj(
                    "C# LastRinshanDraw is a V5 representation derived from the successful drawRinshan action; " +
                        "V4 has no such field."
                )
            ),
            "fieldClassification" to mobj(
                "INPUT" to compactStrings(
                    listOf(
                        "initialState.placements", "initialState.pointers", "initialState.hands",
                        "initialState.rivers", "initialState.melds", "initialState.deadWallBySlot",
                        "initialState.replacements", "initialState.doraIndicators", "initialState.lastDiscard",
                        "steps[].action"
                    )
                ),
                "OUTPUT" to compactStrings(
                    listOf(
                        "steps[].outcome", "steps[].resultTileId", "steps[].after.placements",
                        "steps[].after.pointers", "steps[].after.lastDiscard", "steps[].after.drawnTile",
                        "steps[].after.rinshanDraw", "steps[].after.hands", "steps[].after.melds",
                        "steps[].after.rivers", "steps[].after.deadWallBySlot",
                        "steps[].after.replacements", "steps[].after.doraIndicators"
                    )
                ),
                "STRUCTURE" to compactStrings(
                    listOf(
                        "tileCount=136", "unique tile ids 0..135", "unique positions",
                        "effective dead wall = 14", "wallPointer<=wallEndExclusive",
                        "zone seat/index legality", "conservation before/after every step"
                    )
                ),
                "META" to compactStrings(
                    listOf(
                        "schema", "generatorVersion", "rulesetVersion", "v4Commit",
                        "sourceHashes", "exporterSourceSha256LfNormalized", "generatedByCommand"
                    )
                ),
                "SOURCE_ONLY" to compactStrings(
                    listOf(
                        "steps[].before.v4TotalKanCount", "steps[].after.v4TotalKanCount",
                        "steps[].before.discardHistory", "steps[].after.discardHistory",
                        "steps[].before.melds[].type/fromPlayer/calledTile",
                        "steps[].after.melds[].type/fromPlayer/calledTile",
                        "steps[].before.v4RawLastDiscard", "steps[].after.v4RawLastDiscard",
                        "initialState.wallSlots", "initialState.deadWallList"
                    )
                ),
                "UNREPRESENTABLE" to compactStrings(
                    listOf(
                        "TilePositionMap has no discardHistory",
                        "TilePositionMap has no meld type/fromPlayer/calledTile",
                        "TilePositionMap has no V4 totalKanCount",
                        "V4 TilePool and C# positions are different ledgers; C# is immutable single-ledger"
                    )
                )
            ),
            "sourceHashes" to marr(sourceHashes),
            "exporterSource" to sourceHash(exporterPath, "engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt"),
            "scope" to mobj(
                "manifestEntriesTargeted" to mj(4),
                "manifestEntriesExportedByThisRun" to mj(4),
                "fullManifestAudited" to mj(false),
                "note" to mj(
                    "Only G-004..G-007 are covered. The frozen catalogue/manifest and the five accepted " +
                        "fixtures are not changed. This is not a full 73-case audit and not G1/G5 acceptance."
                )
            ),
            "generationResult" to mobj(
                "casesExported" to mj(cases.size),
                "stepsRecorded" to mj(cases.sumOf { it.steps.size }),
                "failures" to mj(0),
                "exporterTestResult" to mj("PASSED")
            ),
            "cases" to marr(cases.map { caseJson(it, standardSet) })
        )

        val path = root.resolve("tests/v5-golden/movement-v1.json")
        Files.createDirectories(path.parent)
        val rendered = rootJson.render()
        Files.write(path, rendered.toByteArray(StandardCharsets.UTF_8))
        assertTrue(Files.exists(path))
        assertTrue(Files.size(path) > 100_000L, "movement fixture looks unexpectedly small: ${Files.size(path)} bytes")
    }

    // ---------------------------------------------------------------------
    // Case construction through real V4 public entry points.
    // ---------------------------------------------------------------------

    private fun buildCases(standardSet: List<Tile>): List<MovementCase> {
        assertEquals(136, standardSet.size)
        val cases = mutableListOf<MovementCase>()

        // G-004: normal draw then discard, two steps in one case.
        run {
            val state = TestTableBuilder.create(phase = GamePhase.DRAW)
            val recorder = Recorder(
                caseId = "MV-G004-DRAW-DISCARD",
                gRows = listOf("G-004"),
                title = "normal draw then discard preserves exact identity and river history",
                fixture = "TestTableBuilder.create(phase=DRAW); seat0; top=wall[wallPointer]",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:74-118 (drawNormal/draw)",
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:125-156 (discard)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:14-25"
                ),
                notes = listOf("draw and discard are recorded as two steps in the same case")
            )
            recorder.start(state)
            val top = state.wall[state.wallPointer]
            val drawn = TileMovement.draw(state, 0)
            assertEquals(top, drawn)
            recorder.success(
                action = action("DRAW", seat = 0),
                resultTile = drawn,
                state = state,
                drawnSource = "WALL",
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.DrawFromWall(0)"),
                clearLastDiscard = true
            )
            state.phase = GamePhase.DISCARD
            val discarded = TileMovement.discard(state, 0, top)
            assertEquals(top, discarded)
            recorder.success(
                action = action("DISCARD", seat = 0, tileId = top.tileId),
                resultTile = discarded,
                state = state,
                drawnSource = null,
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.Discard(0, T${top.tileId})")
            )
            cases += recorder.finish()
        }

        // G-005: pon.
        run {
            val copies = (0..3).map { Tile(Suit.PIN, 5, it) }
            val state = TestTableBuilder.create(
                hands = mapOf(0 to copies.take(2)),
                discards = mapOf(2 to listOf(copies[3])),
                phase = GamePhase.RESPONSE_WINDOW,
                currentPlayer = 2
            )
            val recorder = Recorder(
                caseId = "MV-G005-PON",
                gRows = listOf("G-005"),
                title = "pon transfers all three physical copies and clears the source river",
                fixture = "Pin5 c0/c1 in seat0 hand; Pin5 c3 in seat2 river; phase=RESPONSE_WINDOW, currentPlayer=2",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:231-290 (pon)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:27-38"
                ),
                notes = listOf("lastDiscard is valid before the call; V4 leaves it stale afterwards, canonical C# evidence is null")
            )
            recorder.start(state)
            val meld = TileMovement.pon(state, 0, copies[3], 2, copies.take(2))
            assertEquals(MeldType.PON, meld.type)
            recorder.success(
                action = action(
                    "PON", seat = 0, calledTileId = copies[3].tileId, fromSeat = 2,
                    handTileIds = copies.take(2).map { it.tileId }
                ),
                resultTile = null,
                state = state,
                drawnSource = null,
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.Pon(0, 2, T${copies[3].tileId}, T${copies[0].tileId}, T${copies[1].tileId})"),
                clearLastDiscard = true,
                extra = listOf("returnedMeldType" to mj("PON"))
            )
            cases += recorder.finish()
        }

        // G-005: chi.
        run {
            val handTiles = listOf(Tile(Suit.MAN, 2, 2), Tile(Suit.MAN, 3, 2))
            val called = Tile(Suit.MAN, 4, 2)
            val state = TestTableBuilder.create(
                hands = mapOf(0 to handTiles),
                discards = mapOf(3 to listOf(called)),
                phase = GamePhase.RESPONSE_WINDOW,
                currentPlayer = 3
            )
            val recorder = Recorder(
                caseId = "MV-G005-CHI",
                gRows = listOf("G-005"),
                title = "chi transfers both hand tiles and the called discard",
                fixture = "Man2 c2/Man3 c2 in seat0 hand; Man4 c2 in seat3 river; phase=RESPONSE_WINDOW, currentPlayer=3",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:158-229 (chi)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:40-50"
                ),
                notes = listOf("kamicha constraint and sequence shape are both exercised by the real call")
            )
            recorder.start(state)
            val meld = TileMovement.chi(state, 0, called, 3, handTiles)
            assertEquals(MeldType.CHI, meld.type)
            recorder.success(
                action = action(
                    "CHI", seat = 0, calledTileId = called.tileId, fromSeat = 3,
                    handTileIds = handTiles.map { it.tileId }
                ),
                resultTile = null,
                state = state,
                drawnSource = null,
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.Chi(0, 3, T${called.tileId}, T${handTiles[0].tileId}, T${handTiles[1].tileId})"),
                clearLastDiscard = true,
                extra = listOf("returnedMeldType" to mj("CHI"))
            )
            cases += recorder.finish()
        }

        // G-006: closed kan + rinshan. A real draw gives the unrelated drawn tile a
        // production source instead of a preset fixture value.
        run {
            val four = (0..3).map { Tile(Suit.SOU, 8, it) }
            val state = TestTableBuilder.create(hands = mapOf(0 to four), phase = GamePhase.DRAW)
            state.doraIndicators.add(state.deadWall[4])
            val recorder = Recorder(
                caseId = "MV-G006-CLOSED-KAN-RINSHAN",
                gRows = listOf("G-006"),
                title = "closed kan preserves an unrelated drawn tile and replenishes the dead wall",
                fixture = "Sou8 c0..c3 in seat0 hand; phase=DRAW; a real draw(0) obtains the unrelated drawn tile",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:353-409 (closedKan)",
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:460-513 (drawRinshan)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:52-70"
                ),
                notes = listOf("the preparatory draw is a real TileMovement.draw, so drawnTile.source is WALL; closedKan merges it into closedTiles and clears drawnTile")
            )
            recorder.start(state)
            val unrelated = TileMovement.draw(state, 0)
            recorder.success(
                action = action("DRAW", seat = 0),
                resultTile = unrelated,
                state = state,
                drawnSource = "WALL",
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.DrawFromWall(0)"),
                clearLastDiscard = true
            )
            val meld = TileMovement.closedKan(state, 0, four)
            assertEquals(MeldType.CLOSED_KAN, meld.type)
            assertTrue(unrelated in state.players[0].closedTiles)
            assertNull(state.players[0].drawnTile)
            recorder.success(
                action = action("CLOSED_KAN", seat = 0, tileIds = four.map { it.tileId }),
                resultTile = null,
                state = state,
                drawnSource = null,
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.ClosedKan(0, T${four[0].tileId}, T${four[1].tileId}, T${four[2].tileId}, T${four[3].tileId})"),
                extra = listOf("returnedMeldType" to mj("CLOSED_KAN"), "preservedDrawnTileId" to mj(unrelated.tileId))
            )
            val rinshanSlot = state.rinshanPointer
            val rinshanTile = TileMovement.drawRinshan(state, 0)
            recorder.success(
                action = action("DRAW_RINSHAN", seat = 0),
                resultTile = rinshanTile,
                state = state,
                drawnSource = "RINSHAN",
                rinshan = RinshanEvidence(rinshanTile.tileId, 0, rinshanSlot),
                csharpMethods = listOf("TilePositionMap.DrawReplacement(0)"),
                clearLastDiscard = true,
                extra = listOf(
                    "rinshanSlot" to mj(rinshanSlot),
                    "replacementTileId" to mj(state.deadWallReplacements.last().tileId),
                    "effectiveDeadWall" to mj(state.deadWall.size - state.rinshanPointer + state.deadWallReplacements.size)
                )
            )
            cases += recorder.finish()
        }

        // G-006: added kan + rinshan. wallPrefix forces the real draw to be the fourth copy.
        run {
            val copies = (0..3).map { Tile(Suit.PIN, 6, it) }
            val pon = Meld(MeldType.PON, copies.take(3), 3, copies[0])
            val state = TestTableBuilder.create(
                melds = mapOf(0 to listOf(pon)),
                wallPrefix = listOf(copies[3]),
                phase = GamePhase.DRAW
            )
            state.doraIndicators.add(state.deadWall[4])
            val recorder = Recorder(
                caseId = "MV-G006-ADDED-KAN-RINSHAN",
                gRows = listOf("G-006"),
                title = "added kan consumes the just drawn fourth copy exactly once and replenishes the dead wall",
                fixture = "Pin6 c0..c2 in a seat0 PON meld; wallPrefix=[Pin6 c3]; phase=DRAW; real draw(0) obtains c3",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:411-458 (addedKan)",
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:460-513 (drawRinshan)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:72-85"
                ),
                notes = listOf("the drawn fourth copy is consumed from drawnTile, not from closedTiles; drawRinshan follows")
            )
            recorder.start(state)
            val fourth = TileMovement.draw(state, 0)
            assertEquals(copies[3], fourth)
            recorder.success(
                action = action("DRAW", seat = 0),
                resultTile = fourth,
                state = state,
                drawnSource = "WALL",
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.DrawFromWall(0)"),
                clearLastDiscard = true
            )
            val meld = TileMovement.addedKan(state, 0, fourth)
            assertEquals(MeldType.ADDED_KAN, meld.type)
            assertNull(state.players[0].drawnTile)
            recorder.success(
                action = action("ADDED_KAN", seat = 0, tileId = fourth.tileId),
                resultTile = null,
                state = state,
                drawnSource = null,
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.AddedKan(0, T${fourth.tileId})"),
                extra = listOf("returnedMeldType" to mj("ADDED_KAN"))
            )
            val rinshanSlot = state.rinshanPointer
            val rinshanTile = TileMovement.drawRinshan(state, 0)
            recorder.success(
                action = action("DRAW_RINSHAN", seat = 0),
                resultTile = rinshanTile,
                state = state,
                drawnSource = "RINSHAN",
                rinshan = RinshanEvidence(rinshanTile.tileId, 0, rinshanSlot),
                csharpMethods = listOf("TilePositionMap.DrawReplacement(0)"),
                clearLastDiscard = true,
                extra = listOf(
                    "rinshanSlot" to mj(rinshanSlot),
                    "replacementTileId" to mj(state.deadWallReplacements.last().tileId),
                    "effectiveDeadWall" to mj(state.deadWall.size - state.rinshanPointer + state.deadWallReplacements.size)
                )
            )
            cases += recorder.finish()
        }

        // G-006: open kan + rinshan.
        run {
            val copies = (0..3).map { Tile(Suit.SOU, 7, it) }
            val state = TestTableBuilder.create(
                hands = mapOf(0 to copies.take(3)),
                discards = mapOf(2 to listOf(copies[3])),
                phase = GamePhase.RESPONSE_WINDOW,
                currentPlayer = 2
            )
            state.doraIndicators.add(state.deadWall[4])
            val recorder = Recorder(
                caseId = "MV-G006-OPEN-KAN-RINSHAN",
                gRows = listOf("G-006"),
                title = "open kan removes the discard, transfers all four tiles and replenishes the dead wall",
                fixture = "Sou7 c0..c2 in seat0 hand; Sou7 c3 in seat2 river; phase=RESPONSE_WINDOW, currentPlayer=2",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:292-351 (openKan)",
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:460-513 (drawRinshan)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:87-99"
                ),
                notes = listOf("the called discard leaves the river; all four copies enter MELD")
            )
            recorder.start(state)
            val meld = TileMovement.openKan(state, 0, copies[3], 2, copies.take(3))
            assertEquals(MeldType.OPEN_KAN, meld.type)
            recorder.success(
                action = action(
                    "OPEN_KAN", seat = 0, calledTileId = copies[3].tileId, fromSeat = 2,
                    handTileIds = copies.take(3).map { it.tileId }
                ),
                resultTile = null,
                state = state,
                drawnSource = null,
                rinshan = null,
                csharpMethods = listOf("TilePositionMap.OpenKan(0, 2, T${copies[3].tileId}, T${copies[0].tileId}, T${copies[1].tileId}, T${copies[2].tileId})"),
                clearLastDiscard = true,
                extra = listOf("returnedMeldType" to mj("OPEN_KAN"))
            )
            val rinshanSlot = state.rinshanPointer
            val rinshanTile = TileMovement.drawRinshan(state, 0)
            recorder.success(
                action = action("DRAW_RINSHAN", seat = 0),
                resultTile = rinshanTile,
                state = state,
                drawnSource = "RINSHAN",
                rinshan = RinshanEvidence(rinshanTile.tileId, 0, rinshanSlot),
                csharpMethods = listOf("TilePositionMap.DrawReplacement(0)"),
                clearLastDiscard = true,
                extra = listOf(
                    "rinshanSlot" to mj(rinshanSlot),
                    "replacementTileId" to mj(state.deadWallReplacements.last().tileId),
                    "effectiveDeadWall" to mj(state.deadWall.size - state.rinshanPointer + state.deadWallReplacements.size)
                )
            )
            cases += recorder.finish()
        }

        // G-007: duplicate physical copies in a pon must fail before mutation.
        run {
            val a = Tile(Suit.MAN, 5, 0)
            val discard = Tile(Suit.MAN, 5, 3)
            val state = TestTableBuilder.create(
                hands = mapOf(0 to listOf(a)),
                discards = mapOf(2 to listOf(discard)),
                phase = GamePhase.RESPONSE_WINDOW,
                currentPlayer = 2
            )
            val recorder = Recorder(
                caseId = "MV-G007-DUPLICATE-PON-REJECTION",
                gRows = listOf("G-007"),
                title = "duplicate physical copies in a pon fail before mutation",
                fixture = "Man5 c0 in seat0 hand; Man5 c3 in seat2 river; pon hand input [c0, c0]",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:252-253 (distinctCopies)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:112-122"
                ),
                notes = listOf("V4 throws IllegalArgumentException; C# returns typed InvalidMeldShape and leaves the map untouched")
            )
            recorder.start(state)
            val ex = assertThrows<IllegalArgumentException> {
                TileMovement.pon(state, 0, discard, 2, listOf(a, a))
            }
            recorder.rejection(
                action = action(
                    "PON", seat = 0, calledTileId = discard.tileId, fromSeat = 2,
                    handTileIds = listOf(a.tileId, a.tileId)
                ),
                exception = ex,
                state = state,
                csharpErrorCodes = listOf("InvalidMeldShape")
            )
            cases += recorder.finish()
        }

        // G-007: fifth rinshan draw cannot consume a dora indicator.
        run {
            val state = TestTableBuilder.create(phase = GamePhase.DRAW)
            state.doraIndicators.add(state.deadWall[4])
            val recorder = Recorder(
                caseId = "MV-G007-FIFTH-RINSHAN-REJECTION",
                gRows = listOf("G-007"),
                title = "fifth rinshan draw cannot consume a dead-wall/dora tile",
                fixture = "TestTableBuilder.create(phase=DRAW); four complete drawRinshan+discard steps; fifth drawRinshan",
                sourceAnchors = listOf(
                    "engine/src/main/kotlin/com/majiang/model/TileMovement.kt:469-513 (drawRinshan pointer guard)",
                    "engine/src/test/kotlin/com/majiang/model/TileMovementContractTest.kt:124-135"
                ),
                notes = listOf("the four successful steps move replacement tiles into deadWallReplacements; the fifth is rejected with the 136-placement state unchanged")
            )
            recorder.start(state)
            repeat(4) { index ->
                val slot = state.rinshanPointer
                val tile = TileMovement.drawRinshan(state, 0)
                recorder.success(
                    action = action("DRAW_RINSHAN", seat = 0),
                    resultTile = tile,
                    state = state,
                    drawnSource = "RINSHAN",
                    rinshan = RinshanEvidence(tile.tileId, 0, slot),
                    csharpMethods = listOf("TilePositionMap.DrawReplacement(0)"),
                    clearLastDiscard = true,
                    extra = listOf(
                        "iteration" to mj(index + 1),
                        "rinshanSlot" to mj(slot),
                        "replacementTileId" to mj(state.deadWallReplacements.last().tileId)
                    )
                )
                val discarded = TileMovement.discard(state, 0, tile)
                recorder.success(
                    action = action("DISCARD", seat = 0, tileId = tile.tileId),
                    resultTile = discarded,
                    state = state,
                    drawnSource = null,
                    rinshan = null,
                    csharpMethods = listOf("TilePositionMap.Discard(0, T${tile.tileId})"),
                    extra = listOf("iteration" to mj(index + 1))
                )
            }
            val before = TestTableBuilder.actualTiles(state).map { it.tileId }
            val ex = assertThrows<IllegalArgumentException> {
                TileMovement.drawRinshan(state, 0)
            }
            val after = TestTableBuilder.actualTiles(state).map { it.tileId }
            assertEquals(before, after)
            recorder.rejection(
                action = action("DRAW_RINSHAN", seat = 0),
                exception = ex,
                state = state,
                csharpErrorCodes = listOf("DeadWallExhausted")
            )
            cases += recorder.finish()
        }

        return cases
    }

    // ---------------------------------------------------------------------
    // Recorder and canonical state.
    // ---------------------------------------------------------------------

    private inner class Recorder(
        private val caseId: String,
        private val gRows: List<String>,
        private val title: String,
        private val fixture: String,
        private val sourceAnchors: List<String>,
        private val notes: List<String>
    ) {
        private lateinit var initial: MovementState
        private var previous: MovementState? = null
        private val steps = mutableListOf<MovementStep>()

        fun start(state: GameState) {
            initial = snapshot(state, standardSet(), drawnSource = null, rinshan = null)
            previous = initial
        }

        fun success(
            action: MovementAction,
            resultTile: Tile?,
            state: GameState,
            drawnSource: String?,
            rinshan: RinshanEvidence?,
            csharpMethods: List<String>,
            clearLastDiscard: Boolean = false,
            extra: List<Pair<String, MJ>> = emptyList()
        ) {
            val before = previous ?: error("recorder not started")
            // StateInvariants enforces phase-sensitive shape rules. These phase/current
            // player assignments mirror the accepted TileMovementContractTest setup;
            // they are test harness state, not exported physical content.
            when (action.type) {
                "DRAW", "DRAW_RINSHAN" -> state.phase = GamePhase.DISCARD
                "PON", "CHI" -> {
                    state.phase = GamePhase.DISCARD
                    state.currentPlayer = action.seat
                }
                "DISCARD" -> state.phase = GamePhase.RESPONSE_WINDOW
                "CLOSED_KAN", "ADDED_KAN", "OPEN_KAN" -> {
                    state.phase = GamePhase.KAN_DRAW
                    state.currentPlayer = action.seat
                }
            }
            TestTableBuilder.assertPhysical(state)
            StateInvariants.assertValid(state)
            val after = snapshot(state, standardSet(), drawnSource, rinshan, clearLastDiscard)
            steps += MovementStep(
                ordinal = steps.size + 1,
                action = action,
                outcome = "success",
                resultTileId = resultTile?.tileId,
                exception = null,
                before = before,
                after = after,
                csharpMethods = csharpMethods,
                csharpErrorCodes = emptyList(),
                extra = extra
            )
            previous = after
        }

        fun rejection(
            action: MovementAction,
            exception: Throwable,
            state: GameState,
            csharpErrorCodes: List<String>
        ) {
            val before = previous ?: error("recorder not started")
            // A rejection must not change any container or the canonical state.
            TestTableBuilder.assertPhysical(state)
            StateInvariants.assertValid(state)
            val after = snapshot(state, standardSet(), drawnSource = null, rinshan = null)
            assertEquals(before, after, "$caseId/${steps.size}: rejection changed canonical state")
            assertEquals(
                before.placements,
                snapshot(state, standardSet(), drawnSource = null, rinshan = null).placements,
                "$caseId/${steps.size}: rejection changed placements"
            )
            steps += MovementStep(
                ordinal = steps.size + 1,
                action = action,
                outcome = "rejected",
                resultTileId = null,
                exception = ExceptionEvidence(
                    type = exception.javaClass.name,
                    message = exception.message ?: "",
                    semanticCategory = if (action.type == "DRAW_RINSHAN") "rinshan-exhausted" else "duplicate-physical-copy"
                ),
                before = before,
                after = after,
                csharpMethods = emptyList(),
                csharpErrorCodes = csharpErrorCodes,
                extra = emptyList(),
                unchanged = true
            )
        }

        fun finish(): MovementCase {
            previous ?: error("recorder not started")
            return MovementCase(
                caseId = caseId,
                gRows = gRows,
                title = title,
                fixture = fixture,
                sourceAnchors = sourceAnchors,
                notes = notes,
                initial = initial,
                steps = steps.toList()
            )
        }

    }

    private data class MovementCase(
        val caseId: String,
        val gRows: List<String>,
        val title: String,
        val fixture: String,
        val sourceAnchors: List<String>,
        val notes: List<String>,
        val initial: MovementState,
        val steps: List<MovementStep>
    )

    private data class MovementStep(
        val ordinal: Int,
        val action: MovementAction,
        val outcome: String,
        val resultTileId: Int?,
        val exception: ExceptionEvidence?,
        val before: MovementState,
        val after: MovementState,
        val csharpMethods: List<String>,
        val csharpErrorCodes: List<String>,
        val extra: List<Pair<String, MJ>>,
        val unchanged: Boolean = false
    )

    private data class MovementAction(
        val type: String,
        val seat: Int,
        val tileId: Int? = null,
        val fromSeat: Int? = null,
        val handTileIds: List<Int> = emptyList(),
        val tileIds: List<Int> = emptyList(),
        val calledTileId: Int? = null
    )

    private data class ExceptionEvidence(
        val type: String,
        val message: String,
        val semanticCategory: String
    )

    private data class Placement(
        val tileId: Int,
        val zone: String,
        val seat: Int,
        val index: Int
    )

    private data class LastDiscard(
        val tileId: Int,
        val seat: Int,
        val slot: Int
    )

    private data class RawLastDiscard(
        val tileId: Int,
        val seat: Int,
        val presentInRiver: Boolean,
        val slot: Int
    )

    private data class DrawnTile(
        val tileId: Int,
        val seat: Int,
        val source: String
    )

    private data class RinshanEvidence(
        val tileId: Int,
        val seat: Int,
        val slot: Int
    )

    private data class MeldInfo(
        val seat: Int,
        val groupIndex: Int,
        val type: String,
        val tiles: List<Int>,
        val fromPlayer: Int,
        val calledTileId: Int
    )

    private data class MovementState(
        val placements: List<Placement>,
        val wallPointer: Int,
        val wallEndExclusive: Int,
        val rinshanPointer: Int,
        val lastDiscard: LastDiscard?,
        val drawnTile: DrawnTile?,
        val rinshanDraw: RinshanEvidence?,
        val doraIndicators: List<Int>,
        val deadWallBySlot: List<Int?>,
        val deadWallList: List<Int>,
        val replacements: List<Int>,
        val wallSlots: List<Int?>,
        val hands: List<List<Int>>,
        val melds: List<MeldInfo>,
        val rivers: List<List<Int>>,
        val discardHistory: List<List<Int>>,
        val v4TotalKanCount: Int,
        val v4RawLastDiscard: RawLastDiscard?
    )

    private fun snapshot(
        state: GameState,
        standardSet: List<Tile>,
        drawnSource: String?,
        rinshan: RinshanEvidence?,
        clearLastDiscard: Boolean = false
    ): MovementState {
        val positions = arrayOfNulls<Placement>(136)
        val pool = state.tilePool

        for (tile in standardSet) {
            val location = pool.getLocation(tile)
                ?: error("tile ${tile.tileId} has no TilePool location")
            val id = tile.tileId
            when (location.zone) {
                Zone.WALL -> {
                    val index = state.wall.indexOf(tile)
                    require(index in 0 until state.wallEndExclusive) {
                        "tile $id in WALL at wall index $index outside [0,${state.wallEndExclusive})"
                    }
                    positions[id] = Placement(id, "WALL", -1, index)
                }
                Zone.DEAD_WALL -> {
                    val replacementIndex = state.deadWallReplacements.indexOf(tile)
                    if (replacementIndex >= 0) {
                        positions[id] = Placement(id, "DEAD_WALL_REPLACEMENT", -1, replacementIndex)
                    } else {
                        val index = state.deadWall.indexOf(tile)
                        require(index >= state.rinshanPointer) {
                            "tile $id in DEAD_WALL at consumed slot $index (rinshanPointer=${state.rinshanPointer})"
                        }
                        positions[id] = Placement(id, "DEAD_WALL", -1, index)
                    }
                }
                Zone.HAND -> {
                    val seat = location.player
                    val sorted = state.players[seat].allClosedTiles.sortedBy { it.tileId }
                    val index = sorted.indexOf(tile)
                    require(index >= 0) { "tile $id missing from hand $seat" }
                    positions[id] = Placement(id, "HAND", seat, index)
                }
                Zone.MELD -> {
                    val seat = location.player
                    var found = false
                    for ((meldIndex, meld) in state.players[seat].melds.withIndex()) {
                        val sorted = meld.tiles.sortedBy { it.tileId }
                        val offset = sorted.indexOf(tile)
                        if (offset >= 0) {
                            positions[id] = Placement(id, "MELD", seat, meldIndex * 4 + offset)
                            found = true
                            break
                        }
                    }
                    require(found) { "tile $id missing from melds of seat $seat" }
                }
                Zone.DISCARD -> {
                    val seat = location.player
                    val index = state.players[seat].discards.indexOf(tile)
                    require(index >= 0) { "tile $id missing from discards of seat $seat" }
                    positions[id] = Placement(id, "DISCARD", seat, index)
                }
                Zone.SLEEVE -> {
                    val seat = location.player
                    val index = state.sleeveTiles[seat].indexOf(tile)
                    require(index >= 0) { "tile $id missing from sleeve of seat $seat" }
                    positions[id] = Placement(id, "SLEEVE", seat, index)
                }
                Zone.UNASSIGNED -> error("tile $id unexpectedly UNASSIGNED")
            }
        }

        val placements = positions.map { it ?: error("placement missing") }
        require(placements.size == 136)
        require(placements.map { it.tileId }.toSet().size == 136)
        require(placements.map { Triple(it.zone, it.seat, it.index) }.toSet().size == 136)

        val effectiveDeadWall = placements.count { it.zone == "DEAD_WALL" } +
            placements.count { it.zone == "DEAD_WALL_REPLACEMENT" }
        assertEquals(14, effectiveDeadWall, "effective dead wall")
        assertEquals(14, state.deadWall.size, "V4 deadWall list size")

        val rawLast = state.lastDiscard?.let { tile ->
            val seat = state.lastDiscardPlayer
            val slot = state.players[seat].discards.indexOf(tile)
            RawLastDiscard(tile.tileId, seat, slot >= 0, slot)
        }
        val canonicalLast = if (clearLastDiscard) {
            null
        } else {
            rawLast?.takeIf { it.presentInRiver && it.slot == state.players[it.seat].discards.lastIndex }
        }

        val drawn = state.players.mapIndexedNotNull { seat, hand ->
            hand.drawnTile?.let { DrawnTile(it.tileId, seat, drawnSource ?: error("drawn tile without a known source")) }
        }.singleOrNull()

        val melds = state.players.flatMapIndexed { seat, hand ->
            hand.melds.mapIndexed { meldIndex, meld ->
                MeldInfo(
                    seat = seat,
                    groupIndex = meldIndex,
                    type = meld.type.name,
                    tiles = meld.tiles.map { it.tileId }.sorted(),
                    fromPlayer = meld.fromPlayer,
                    calledTileId = meld.calledTile.tileId
                )
            }
        }

        return MovementState(
            placements = placements.sortedBy { it.tileId },
            wallPointer = state.wallPointer,
            wallEndExclusive = state.wallEndExclusive,
            rinshanPointer = state.rinshanPointer,
            lastDiscard = canonicalLast?.let { LastDiscard(it.tileId, it.seat, it.slot) },
            drawnTile = drawn,
            rinshanDraw = rinshan,
            doraIndicators = state.doraIndicators.map { it.tileId },
            deadWallBySlot = (0..13).map { index ->
                if (index < state.rinshanPointer) null else state.deadWall.getOrNull(index)?.tileId
            },
            deadWallList = state.deadWall.map { it.tileId },
            replacements = state.deadWallReplacements.map { it.tileId },
            wallSlots = (0 until state.wallEndExclusive).map { index ->
                state.wall.getOrNull(index)?.takeIf { pool.getLocation(it) == TileLocation.WALL }?.tileId
            },
            hands = state.players.map { hand -> hand.allClosedTiles.sortedBy { it.tileId }.map { it.tileId } },
            melds = melds,
            rivers = state.players.map { hand -> hand.discards.map { it.tileId } },
            discardHistory = state.players.map { hand -> hand.discardHistory.map { it.tileId } },
            v4TotalKanCount = state.totalKanCount,
            v4RawLastDiscard = rawLast
        )
    }

    private fun validateCases(cases: List<MovementCase>, standardSet: List<Tile>) {
        assertEquals(8, cases.size)
        assertEquals(8, cases.map { it.caseId }.toSet().size)
        assertEquals(setOf("G-004", "G-005", "G-006", "G-007"), cases.flatMap { it.gRows }.toSet())
        val standardIds = standardSet.map { it.tileId }.toSet()
        for (case in cases) {
            assertTrue(case.steps.isNotEmpty(), "${case.caseId} has no steps")
            validateState(case.caseId, "initial", case.initial, standardIds)
            var previous = case.initial
            for (step in case.steps) {
                assertEquals(previous, step.before, "${case.caseId}/${step.ordinal} before != previous after")
                validateState(case.caseId, step.ordinal.toString(), step.after, standardIds)
                if (step.outcome == "rejected") {
                    assertEquals(step.before, step.after, "${case.caseId}/${step.ordinal} rejection mutated state")
                    assertTrue(step.unchanged)
                } else {
                    assertNotNull(step.after)
                }
                previous = step.after
            }
        }
        assertEquals(2, cases.flatMap { it.steps }.count { it.outcome == "rejected" })
    }

    private fun validateState(caseId: String, label: String, state: MovementState, standardIds: Set<Int>) {
        assertEquals(136, state.placements.size, "$caseId/$label placement count")
        assertEquals(standardIds, state.placements.map { it.tileId }.toSet(), "$caseId/$label exact identities")
        assertEquals(136, state.placements.map { it.tileId }.toSet().size, "$caseId/$label unique ids")
        assertEquals(
            state.placements.size,
            state.placements.map { Triple(it.zone, it.seat, it.index) }.toSet().size,
            "$caseId/$label unique positions"
        )
        assertEquals(14, state.deadWallBySlot.size, "$caseId/$label dead wall slots")
        assertEquals(14, state.deadWallList.size, "$caseId/$label V4 dead wall list")
        assertEquals(
            14,
            state.placements.count { it.zone == "DEAD_WALL" } +
                state.placements.count { it.zone == "DEAD_WALL_REPLACEMENT" },
            "$caseId/$label effective dead wall"
        )
        assertTrue(state.wallPointer in 0..state.wallEndExclusive, "$caseId/$label wall pointer")
        assertEquals(state.replacements.size, state.rinshanPointer, "$caseId/$label replacement count")
        for (p in state.placements) {
            when (p.zone) {
                "WALL" -> {
                    assertTrue(p.seat == -1 && p.index in 0 until state.wallEndExclusive)
                }
                "DEAD_WALL" -> assertTrue(p.seat == -1 && p.index in state.rinshanPointer..13)
                "DEAD_WALL_REPLACEMENT" -> assertTrue(p.seat == -1 && p.index in state.replacements.indices)
                "HAND" -> assertTrue(p.seat in 0..3 && p.index in 0..13)
                "MELD" -> assertTrue(p.seat in 0..3 && p.index in 0..15)
                "DISCARD" -> assertTrue(p.seat in 0..3 && p.index in 0..135)
                "SLEEVE" -> assertTrue(p.seat in 0..3 && p.index in 0..13)
                else -> error("unknown canonical zone ${p.zone}")
            }
        }
    }

    private fun standardSet(): List<Tile> = TilePool.buildStandardSet()

    // ---------------------------------------------------------------------
    // JSON rendering.
    // ---------------------------------------------------------------------

    private fun caseJson(case: MovementCase, standardSet: List<Tile>): MJ = mobj(
        "caseId" to mj(case.caseId),
        "gRows" to compactStrings(case.gRows),
        "title" to mj(case.title),
        "fixture" to mj(case.fixture),
        "sourceAnchors" to compactStrings(case.sourceAnchors),
        "notes" to compactStrings(case.notes),
        "initialState" to stateJson(case.initial, standardSet),
        "steps" to marr(case.steps.map { stepJson(case.caseId, it) })
    )

    private fun stepJson(caseId: String, step: MovementStep): MJ = mobj(
        "ordinal" to mj(step.ordinal),
        "action" to actionJson(step.action),
        "outcome" to mj(step.outcome),
        "resultTileId" to (step.resultTileId?.let { mj(it) } ?: MJ.Null),
        "exception" to (step.exception?.let {
            mobj(
                "type" to mj(it.type),
                "message" to mj(it.message),
                "semanticCategory" to mj(it.semanticCategory)
            )
        } ?: MJ.Null),
        "unchangedOnRejection" to mj(step.unchanged),
        "csharpMethods" to compactStrings(step.csharpMethods),
        "csharpErrorCodes" to compactStrings(step.csharpErrorCodes),
        "extra" to mobj(*step.extra.toTypedArray()),
        "before" to stateJson(step.before, standardSet()),
        "after" to stateJson(step.after, standardSet())
    )

    private fun actionJson(action: MovementAction): MJ {
        val fields = mutableListOf<Pair<String, MJ>>(
            "type" to mj(action.type),
            "seat" to mj(action.seat)
        )
        action.tileId?.let { fields += "tileId" to mj(it) }
        action.fromSeat?.let { fields += "fromSeat" to mj(it) }
        action.calledTileId?.let { fields += "calledTileId" to mj(it) }
        if (action.handTileIds.isNotEmpty()) fields += "handTileIds" to compactInts(action.handTileIds)
        if (action.tileIds.isNotEmpty()) fields += "tileIds" to compactInts(action.tileIds)
        return mobj(*fields.toTypedArray())
    }

    private fun stateJson(state: MovementState, standardSet: List<Tile>): MJ = mobj(
        "tileCount" to mj(state.placements.size),
        "standardSetMatches" to mj(state.placements.map { it.tileId }.toSet() == standardSet.map { it.tileId }.toSet()),
        "zoneCounts" to mobj(
            "WALL" to mj(state.placements.count { it.zone == "WALL" }),
            "DEAD_WALL" to mj(state.placements.count { it.zone == "DEAD_WALL" }),
            "DEAD_WALL_REPLACEMENT" to mj(state.placements.count { it.zone == "DEAD_WALL_REPLACEMENT" }),
            "HAND" to mj(state.placements.count { it.zone == "HAND" }),
            "DISCARD" to mj(state.placements.count { it.zone == "DISCARD" }),
            "MELD" to mj(state.placements.count { it.zone == "MELD" }),
            "SLEEVE" to mj(state.placements.count { it.zone == "SLEEVE" })
        ),
        "pointers" to mobj(
            "wallPointer" to mj(state.wallPointer),
            "wallEndExclusive" to mj(state.wallEndExclusive),
            "rinshanPointer" to mj(state.rinshanPointer)
        ),
        "lastDiscard" to (state.lastDiscard?.let {
            mobj("tileId" to mj(it.tileId), "seat" to mj(it.seat), "slot" to mj(it.slot))
        } ?: MJ.Null),
        "drawnTile" to (state.drawnTile?.let {
            mobj("tileId" to mj(it.tileId), "seat" to mj(it.seat), "source" to mj(it.source))
        } ?: MJ.Null),
        "rinshanDraw" to (state.rinshanDraw?.let {
            mobj("tileId" to mj(it.tileId), "seat" to mj(it.seat), "slot" to mj(it.slot))
        } ?: MJ.Null),
        "doraIndicators" to compactInts(state.doraIndicators),
        "deadWallBySlot" to marr(state.deadWallBySlot.map { it?.let { id -> mj(id) } ?: MJ.Null }),
        "deadWallList" to compactInts(state.deadWallList),
        "replacements" to compactInts(state.replacements),
        "wallSlots" to marr(state.wallSlots.map { it?.let { id -> mj(id) } ?: MJ.Null }),
        "hands" to marr(state.hands.mapIndexed { seat, tiles ->
            mobj(
                "seat" to mj(seat),
                "count" to mj(tiles.size),
                "tiles" to compactInts(tiles)
            )
        }),
        "melds" to marr(state.melds.map {
            mobj(
                "seat" to mj(it.seat),
                "groupIndex" to mj(it.groupIndex),
                "type" to mj(it.type),
                "tiles" to compactInts(it.tiles),
                "fromPlayer" to mj(it.fromPlayer),
                "calledTileId" to mj(it.calledTileId)
            )
        }),
        "rivers" to marr(state.rivers.mapIndexed { seat, tiles ->
            mobj("seat" to mj(seat), "tiles" to compactInts(tiles))
        }),
        "discardHistory" to marr(state.discardHistory.mapIndexed { seat, tiles ->
            mobj("seat" to mj(seat), "tiles" to compactInts(tiles))
        }),
        "v4TotalKanCount" to mj(state.v4TotalKanCount),
        "v4RawLastDiscard" to (state.v4RawLastDiscard?.let {
            mobj(
                "tileId" to mj(it.tileId),
                "seat" to mj(it.seat),
                "presentInRiver" to mj(it.presentInRiver),
                "slot" to mj(it.slot)
            )
        } ?: MJ.Null),
        "placements" to marr(state.placements.map {
            mobj(
                "tileId" to mj(it.tileId),
                "zone" to mj(it.zone),
                "seat" to mj(it.seat),
                "index" to mj(it.index)
            )
        })
    )

    private fun sourceHash(path: Path, relative: String): MJ {
        assertTrue(Files.exists(path), "source file missing: $relative")
        val raw = Files.readAllBytes(path)
        val text = String(raw, StandardCharsets.UTF_8)
        val lf = text.replace("\r\n", "\n")
        val crlfCount = countOccurrences(text, "\r\n")
        val lfOnlyCount = countOccurrences(text, "\n") - crlfCount
        val kind = when {
            crlfCount > 0 && lfOnlyCount == 0 -> "CRLF"
            crlfCount == 0 && lfOnlyCount > 0 -> "LF"
            crlfCount == 0 && lfOnlyCount == 0 -> "NONE"
            else -> "MIXED"
        }
        return mobj(
            "file" to mj(relative),
            "bytesRaw" to mj(raw.size),
            "sha256RawBytes" to mj(sha256(raw)),
            "sha256LfNormalized" to mj(sha256(lf.toByteArray(StandardCharsets.UTF_8))),
            "crlfCount" to mj(crlfCount),
            "lfOnlyCount" to mj(lfOnlyCount),
            "lineEndings" to mj(kind),
            "algorithm" to mj("SHA-256; sha256RawBytes over raw file bytes; sha256LfNormalized over UTF-8 text with CRLF replaced by LF")
        )
    }

    private fun countOccurrences(text: String, needle: String): Int {
        var count = 0
        var index = 0
        while (true) {
            index = text.indexOf(needle, index)
            if (index < 0) return count
            count++
            index += needle.length
        }
    }

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

    private fun sha256(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes)
            .joinToString("") { String.format(Locale.ROOT, "%02x", it) }

    private fun action(
        type: String,
        seat: Int,
        tileId: Int? = null,
        fromSeat: Int? = null,
        handTileIds: List<Int> = emptyList(),
        tileIds: List<Int> = emptyList(),
        calledTileId: Int? = null
    ): MovementAction = MovementAction(type, seat, tileId, fromSeat, handTileIds, tileIds, calledTileId)

    companion object {
        private const val GENERATION_COMMAND =
            "\$env:V5_MOVEMENT_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test " +
                "--tests \"com.majiang.model.V5TileMovementGoldenExporterTest\" --rerun-tasks --console=plain"
    }
}

// -------------------------------------------------------------------------
// Small deterministic JSON writer (file-local; same shape as accepted exporters).
// -------------------------------------------------------------------------

private sealed class MJ {
    data class Str(val value: String) : MJ()
    data class Num(val value: Long) : MJ()
    data class Bool(val value: Boolean) : MJ()
    object Null : MJ()
    data class Array(val items: List<MJ>, val compact: Boolean = false) : MJ()
    data class Object(val fields: List<Pair<String, MJ>>) : MJ()

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

private fun mj(value: String): MJ = MJ.Str(value)
private fun mj(value: Int): MJ = MJ.Num(value.toLong())
private fun mj(value: Long): MJ = MJ.Num(value)
private fun mj(value: Boolean): MJ = MJ.Bool(value)
private fun mobj(vararg fields: Pair<String, MJ>): MJ = MJ.Object(fields.toList())
private fun marr(items: List<MJ>): MJ = MJ.Array(items)
private fun compactInts(values: List<Int>): MJ = MJ.Array(values.map { MJ.Num(it.toLong()) }, compact = true)
private fun compactStrings(values: List<String>): MJ = MJ.Array(values.map { MJ.Str(it) }, compact = true)

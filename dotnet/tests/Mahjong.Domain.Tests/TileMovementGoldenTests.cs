using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// P0.1 movement differential: reads the V4-generated
    /// <c>tests/v5-golden/movement-v1.json</c> and replays G-004..G-007 through the
    /// existing production <see cref="TilePositionMap"/> methods.
    ///
    /// The fixture is the only source of expected values. This test does not contain a
    /// V4 rules oracle and does not compare an unequal hash: every comparable physical
    /// field, pointer and evidence value is diffed after a documented canonicalization.
    ///
    /// Deliberate mapping boundaries (also written into the fixture and the delivery
    /// report):
    /// - V4 hand is a sorted list; C# hand slots are allocation details. Both sides are
    ///   canonicalized to dense 0..n-1 in tileId order; wall/discard/meld/dead-wall slots
    ///   remain exact.
    /// - V4 meld list index maps to C# Meld(seat, index*4 + tileId-sorted offset).
    /// - V4 deadWallReplacements index maps to C# DeadWallReplacement(index).
    /// - C# LastDiscard evidence is cleared by a successful draw/call; V4 TileMovement
    ///   leaves a stale value. The fixture keeps the raw V4 value as SOURCE_ONLY and
    ///   canonical lastDiscard follows the C# evidence.
    /// - C# TilePositionMap has no discardHistory or meld type/fromPlayer/calledTile.
    /// - C# KanCount increments on kan; V4 TileMovement does not update totalKanCount.
    /// </summary>
    public sealed class TileMovementGoldenTests
    {
        private const string Schema = "v5-movement-golden-v1";

        private static readonly Lazy<JsonDocument> Golden = new Lazy<JsonDocument>(LoadGolden);

        private static readonly string[] ExpectedCaseIds =
        {
            "MV-G004-DRAW-DISCARD",
            "MV-G005-PON",
            "MV-G005-CHI",
            "MV-G006-CLOSED-KAN-RINSHAN",
            "MV-G006-ADDED-KAN-RINSHAN",
            "MV-G006-OPEN-KAN-RINSHAN",
            "MV-G007-DUPLICATE-PON-REJECTION",
            "MV-G007-FIFTH-RINSHAN-REJECTION"
        };

        private static readonly string[] RequiredSourceFiles =
        {
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
        };

        private static readonly string[] ExpectedTaskPaths =
        {
            "doc/v5-重构/V5-P01-MOVEMENT-GOLDEN-G004-G007.md",
            "dotnet/tests/Mahjong.Domain.Tests/TileMovementGoldenTests.cs",
            "engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt",
            "tests/v5-golden/movement-v1.json",
            "tools/v5-golden/probe-movement-binding.ps1"
        };

        private static readonly Dictionary<string, string[]> ExpectedGRows =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "MV-G004-DRAW-DISCARD", new[] { "G-004" } },
                { "MV-G005-PON", new[] { "G-005" } },
                { "MV-G005-CHI", new[] { "G-005" } },
                { "MV-G006-CLOSED-KAN-RINSHAN", new[] { "G-006" } },
                { "MV-G006-ADDED-KAN-RINSHAN", new[] { "G-006" } },
                { "MV-G006-OPEN-KAN-RINSHAN", new[] { "G-006" } },
                { "MV-G007-DUPLICATE-PON-REJECTION", new[] { "G-007" } },
                { "MV-G007-FIFTH-RINSHAN-REJECTION", new[] { "G-007" } }
            };

        [Fact]
        public void GoldenFileCarriesRealV4ProvenanceAndEightCases()
        {
            JsonElement root = Golden.Value.RootElement;
            Assert.Equal(Schema, Str(root, "schema"));
            Assert.Equal(1, Int(root, "generatorVersion"));
            Assert.Equal(1, Int(root, "rulesetVersion"));
            Assert.Equal("exported", Str(root, "extractionStatus"));
            Assert.Equal(8, Int(root, "caseCount"));
            Assert.False(Bool(root, "clockUsed"));
            Assert.False(Bool(root, "implicitRandomUsed"));
            Assert.Equal(
                new[] { "G-004", "G-005", "G-006", "G-007" },
                Strings(root.GetProperty("targetIds")));

            string commit = Str(root, "v4Commit");
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(Uri.IsHexDigit), "v4Commit must be a 40-hex git commit");
            Assert.True(Bool(root, "v4ProductionTreeClean"));
            Assert.Empty(Strings(root.GetProperty("trackedModifiedFiles")));
            Assert.Empty(Strings(root.GetProperty("trackedProductionModifiedFiles")));

            string[] tasks = Strings(root.GetProperty("taskPaths"));
            Assert.Equal(
                ExpectedTaskPaths.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
                tasks.OrderBy(t => t, StringComparer.Ordinal).ToArray());

            string command = Str(root, "generatedByCommand");
            Assert.Contains("V5_MOVEMENT_GOLDEN_EXPORT", command);
            Assert.Contains("gradlew", command);
            Assert.Contains(":engine:test", command);
            Assert.Contains("--tests", command);
            Assert.Contains("V5TileMovementGoldenExporterTest", command);
            Assert.Contains("--rerun-tasks", command);

            JsonElement generation = root.GetProperty("generationResult");
            Assert.Equal(8, Int(generation, "casesExported"));
            Assert.Equal(22, Int(generation, "stepsRecorded"));
            Assert.Equal(0, Int(generation, "failures"));
            Assert.Equal("PASSED", Str(generation, "exporterTestResult"));

            JsonElement scope = root.GetProperty("scope");
            Assert.Equal(4, Int(scope, "manifestEntriesTargeted"));
            Assert.Equal(4, Int(scope, "manifestEntriesExportedByThisRun"));
            Assert.False(Bool(scope, "fullManifestAudited"));
            Assert.Contains("not a full 73-case audit", Str(scope, "note"));

            string repositoryRoot = RepositoryRoot();
            JsonElement sources = root.GetProperty("sourceHashes");
            Assert.Equal(RequiredSourceFiles.Length, sources.GetArrayLength());
            var seenSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement source in sources.EnumerateArray())
            {
                string relative = Str(source, "file");
                Assert.Contains(relative, RequiredSourceFiles);
                Assert.True(seenSources.Add(relative), "duplicate source hash entry " + relative);
                AssertSourceHash(repositoryRoot, source);
            }

            foreach (string required in RequiredSourceFiles)
            {
                Assert.True(seenSources.Contains(required), "missing required source hash " + required);
            }

            JsonElement exporterSource = root.GetProperty("exporterSource");
            Assert.Equal(
                "engine/src/test/kotlin/com/majiang/model/V5TileMovementGoldenExporterTest.kt",
                Str(exporterSource, "file"));
            AssertSourceHash(repositoryRoot, exporterSource);

            JsonElement cases = root.GetProperty("cases");
            Assert.Equal(ExpectedCaseIds.Length, cases.GetArrayLength());
            var ids = new List<string>();
            foreach (JsonElement sample in cases.EnumerateArray())
            {
                string caseId = Str(sample, "caseId");
                ids.Add(caseId);
                Assert.True(ExpectedGRows.ContainsKey(caseId), "unknown case id " + caseId);
                Assert.Equal(ExpectedGRows[caseId], Strings(sample.GetProperty("gRows")));
                Assert.True(sample.GetProperty("steps").GetArrayLength() > 0, caseId + " has no steps");
                JsonElement initial = sample.GetProperty("initialState");
                Assert.Equal(136, Int(initial, "tileCount"));
                Assert.True(Bool(initial, "standardSetMatches"));
                Assert.Equal(136, initial.GetProperty("placements").GetArrayLength());
            }

            Assert.Equal(ExpectedCaseIds, ids);

            JsonElement classification = root.GetProperty("fieldClassification");
            foreach (string category in new[] { "INPUT", "OUTPUT", "STRUCTURE", "META", "SOURCE_ONLY", "UNREPRESENTABLE" })
            {
                Assert.True(classification.TryGetProperty(category, out JsonElement items), "missing classification " + category);
                Assert.True(items.GetArrayLength() > 0, "empty classification " + category);
            }
        }

        [Fact]
        public void G004DrawAndDiscardReplayExactly()
        {
            ReplayCase("MV-G004-DRAW-DISCARD", expectedSteps: 2, expectedRejections: 0);
        }

        [Fact]
        public void G005PonReplayTransfersThreeTiles()
        {
            ReplayCase("MV-G005-PON", expectedSteps: 1, expectedRejections: 0);
        }

        [Fact]
        public void G005ChiReplayTransfersThreeTiles()
        {
            ReplayCase("MV-G005-CHI", expectedSteps: 1, expectedRejections: 0);
        }

        [Fact]
        public void G006ClosedKanAndRinshanReplay()
        {
            ReplayCase("MV-G006-CLOSED-KAN-RINSHAN", expectedSteps: 3, expectedRejections: 0);
        }

        [Fact]
        public void G006AddedKanAndRinshanReplay()
        {
            ReplayCase("MV-G006-ADDED-KAN-RINSHAN", expectedSteps: 3, expectedRejections: 0);
        }

        [Fact]
        public void G006OpenKanAndRinshanReplay()
        {
            ReplayCase("MV-G006-OPEN-KAN-RINSHAN", expectedSteps: 2, expectedRejections: 0);
        }

        [Fact]
        public void G007DuplicatePonRejectsWithoutMutation()
        {
            ReplayCase("MV-G007-DUPLICATE-PON-REJECTION", expectedSteps: 1, expectedRejections: 1);
        }

        [Fact]
        public void G007FifthRinshanRejectsWithoutMutation()
        {
            ReplayCase("MV-G007-FIFTH-RINSHAN-REJECTION", expectedSteps: 9, expectedRejections: 1);
        }

        [Fact]
        public void MappingCoverageRecordsSourceOnlyAndUnrepresentableFields()
        {
            JsonElement root = Golden.Value.RootElement;
            string[] sourceOnly = Strings(root.GetProperty("fieldClassification").GetProperty("SOURCE_ONLY"));
            Assert.Contains("steps[].after.discardHistory", sourceOnly);
            Assert.Contains("steps[].after.melds[].type/fromPlayer/calledTile", sourceOnly);
            Assert.Contains("steps[].after.v4TotalKanCount", sourceOnly);
            Assert.Contains("steps[].after.v4RawLastDiscard", sourceOnly);

            string[] unrepresentable = Strings(root.GetProperty("fieldClassification").GetProperty("UNREPRESENTABLE"));
            Assert.Contains(unrepresentable, item => item.Contains("discardHistory", StringComparison.Ordinal));
            Assert.Contains(unrepresentable, item => item.Contains("meld type", StringComparison.Ordinal));
            Assert.Contains(unrepresentable, item => item.Contains("totalKanCount", StringComparison.Ordinal));

            // Structural proof that these are not hidden TilePositionMap fields.
            Assert.Null(typeof(TilePositionMap).GetProperty("DiscardHistory"));
            Assert.Null(typeof(TilePositionMap).GetProperty("MeldType"));
            Assert.Null(typeof(TilePositionMap).GetProperty("TotalKanCount"));
            Assert.NotNull(typeof(TilePositionMap).GetProperty("KanCount"));

            // V4 stale last-discard -> canonical null, raw value still exported.
            JsonElement pon = Sample("MV-G005-CHI");
            JsonElement ponAfter = pon.GetProperty("steps")[0].GetProperty("after");
            Assert.Equal(JsonValueKind.Null, ponAfter.GetProperty("lastDiscard").ValueKind);
            JsonElement raw = ponAfter.GetProperty("v4RawLastDiscard");
            Assert.False(Bool(raw, "presentInRiver"));
            Assert.NotEqual(JsonValueKind.Null, raw.ValueKind);

            // C# KanCount increments on a kan while TileMovement leaves totalKanCount at 0.
            JsonElement closed = Sample("MV-G006-CLOSED-KAN-RINSHAN");
            TilePositionMap map = BuildInitial(closed.GetProperty("initialState"));
            TransitionResult draw = map.DrawFromWall(0);
            Assert.True(draw.IsSuccess);
            map = draw.RequireState();
            int[] four = Ints(closed.GetProperty("steps")[1].GetProperty("action").GetProperty("tileIds"));
            TransitionResult kan = map.ClosedKan(
                seat: 0,
                tileA: new TileId(four[0]),
                tileB: new TileId(four[1]),
                tileC: new TileId(four[2]),
                tileD: new TileId(four[3]));
            Assert.True(kan.IsSuccess, kan.Error?.ToString());
            Assert.Equal(1, kan.RequireState().KanCount);
            Assert.Equal(0, Int(closed.GetProperty("steps")[1].GetProperty("after"), "v4TotalKanCount"));
        }

        // -----------------------------------------------------------------
        // Replay and field-level diff.
        // -----------------------------------------------------------------

        private static void ReplayCase(string caseId, int expectedSteps, int expectedRejections)
        {
            JsonElement sample = Sample(caseId);
            TilePositionMap map = BuildInitial(sample.GetProperty("initialState"));
            AssertState(sample.GetProperty("initialState"), map, caseId + " initial");

            JsonElement steps = sample.GetProperty("steps");
            Assert.Equal(expectedSteps, steps.GetArrayLength());
            int successes = 0;
            int rejections = 0;

            foreach (JsonElement step in steps.EnumerateArray())
            {
                int ordinal = Int(step, "ordinal");
                string label = caseId + "/step" + ordinal.ToString(CultureInfo.InvariantCulture);
                AssertState(step.GetProperty("before"), map, label + " before");

                TransitionResult result = RunAction(map, step.GetProperty("action"));
                string outcome = Str(step, "outcome");
                if (outcome == "success")
                {
                    Assert.True(result.IsSuccess, label + " expected success but got " + result.Error);
                    Assert.NotNull(result.State);
                    map = result.State!;
                    JsonElement expectedResult = step.GetProperty("resultTileId");
                    if (expectedResult.ValueKind == JsonValueKind.Null)
                    {
                        Assert.False(
                            result.ResultTile.HasValue,
                            label + " expected null result, but the production call returned " +
                            (result.ResultTile.HasValue ? result.ResultTile.Value.ToString() : "<null>"));
                    }
                    else
                    {
                        Assert.True(result.ResultTile.HasValue, label + " missing result tile");
                        Assert.Equal(expectedResult.GetInt32(), result.ResultTile!.Value.Value);
                    }

                    successes++;
                }
                else
                {
                    Assert.Equal("rejected", outcome);
                    Assert.False(result.IsSuccess, label + " expected rejection");
                    Assert.Null(result.State);
                    Assert.NotNull(result.Error);
                    Assert.False(result.ResultTile.HasValue, label + " rejection must not return a tile");
                    Assert.Equal(JsonValueKind.Null, step.GetProperty("resultTileId").ValueKind);
                    string[] codes = Strings(step.GetProperty("csharpErrorCodes"));
                    Assert.Contains(result.Error!.Code.ToString(), codes);
                    Assert.True(Bool(step, "unchangedOnRejection"));
                    rejections++;
                }

                AssertState(step.GetProperty("after"), map, label + " after");
            }

            Assert.Equal(expectedSteps - expectedRejections, successes);
            Assert.Equal(expectedRejections, rejections);
            Assert.Equal(successes, map.Revision);
        }

        private static TilePositionMap BuildInitial(JsonElement state)
        {
            var placements = new List<TilePlacement>();
            foreach (JsonElement placement in state.GetProperty("placements").EnumerateArray())
            {
                placements.Add(new TilePlacement(
                    new TileId(Int(placement, "tileId")),
                    ParsePosition(placement)));
            }

            JsonElement pointers = state.GetProperty("pointers");
            int[] dora = Ints(state.GetProperty("doraIndicators"));
            return TilePositionMap.FromPlacements(
                placements,
                wallPointer: Int(pointers, "wallPointer"),
                wallEndExclusive: Int(pointers, "wallEndExclusive"),
                rinshanPointer: Int(pointers, "rinshanPointer"),
                lastDiscard: ParseLastDiscard(state.GetProperty("lastDiscard")),
                revision: 0,
                rinshanDraw: ParseRinshan(state.GetProperty("rinshanDraw")),
                doraIndicators: dora.Select(id => new TileId(id)).ToArray(),
                kanCount: 0,
                drawnTile: ParseDrawn(state.GetProperty("drawnTile")));
        }

        private static TransitionResult RunAction(TilePositionMap map, JsonElement action)
        {
            string type = Str(action, "type");
            int seat = Int(action, "seat");
            switch (type)
            {
                case "DRAW":
                    return map.DrawFromWall(seat);
                case "DISCARD":
                    return map.Discard(seat, new TileId(Int(action, "tileId")));
                case "PON":
                {
                    int[] hand = Ints(action.GetProperty("handTileIds"));
                    Assert.Equal(2, hand.Length);
                    return map.Pon(
                        seat,
                        Int(action, "fromSeat"),
                        new TileId(Int(action, "calledTileId")),
                        new TileId(hand[0]),
                        new TileId(hand[1]));
                }
                case "CHI":
                {
                    int[] hand = Ints(action.GetProperty("handTileIds"));
                    Assert.Equal(2, hand.Length);
                    return map.Chi(
                        seat,
                        Int(action, "fromSeat"),
                        new TileId(Int(action, "calledTileId")),
                        new TileId(hand[0]),
                        new TileId(hand[1]));
                }
                case "CLOSED_KAN":
                {
                    int[] tiles = Ints(action.GetProperty("tileIds"));
                    Assert.Equal(4, tiles.Length);
                    return map.ClosedKan(
                        seat,
                        new TileId(tiles[0]),
                        new TileId(tiles[1]),
                        new TileId(tiles[2]),
                        new TileId(tiles[3]));
                }
                case "ADDED_KAN":
                    return map.AddedKan(seat, new TileId(Int(action, "tileId")));
                case "OPEN_KAN":
                {
                    int[] hand = Ints(action.GetProperty("handTileIds"));
                    Assert.Equal(3, hand.Length);
                    return map.OpenKan(
                        seat,
                        Int(action, "fromSeat"),
                        new TileId(Int(action, "calledTileId")),
                        new TileId(hand[0]),
                        new TileId(hand[1]),
                        new TileId(hand[2]));
                }
                case "DRAW_RINSHAN":
                    return map.DrawReplacement(seat);
                default:
                    throw new InvalidOperationException("unknown movement action " + type);
            }
        }

        private static void AssertState(JsonElement expected, TilePositionMap map, string label)
        {
            Assert.Equal(136, Int(expected, "tileCount"));
            Assert.True(Bool(expected, "standardSetMatches"), label + " standard set");
            CanonicalState expectedState = ParseExpectedState(expected, label);
            CanonicalState actual = Canonicalize(map);

            for (int tileId = 0; tileId < 136; tileId++)
            {
                Eq(expectedState.Positions[tileId], actual.Positions[tileId], label + " T" + tileId + " canonical position");
            }

            Eq(expectedState.WallPointer, actual.WallPointer, label + " wallPointer");
            Eq(expectedState.WallEndExclusive, actual.WallEndExclusive, label + " wallEndExclusive");
            Eq(expectedState.RinshanPointer, actual.RinshanPointer, label + " rinshanPointer");
            AssertEvidence(expected.GetProperty("lastDiscard"), actual.LastDiscard, label + " lastDiscard");
            AssertDrawn(expected.GetProperty("drawnTile"), actual.DrawnTile, label + " drawnTile");
            AssertRinshan(expected.GetProperty("rinshanDraw"), actual.RinshanDraw, label + " rinshanDraw");
            SeqEq(expectedState.DoraIndicators, actual.DoraIndicators, label + " doraIndicators");

            Assert.Equal(4, expectedState.Hands.Count);
            for (int seat = 0; seat < 4; seat++)
            {
                SeqEq(expectedState.Hands[seat], actual.Hands[seat], label + " hand seat " + seat);
            }

            Eq(expectedState.Melds.Count, actual.Melds.Count, label + " meld group count");
            var actualMelds = actual.Melds.ToDictionary(m => (m.Seat, m.GroupIndex));
            foreach (MeldGroup meld in expectedState.Melds)
            {
                Assert.True(actualMelds.TryGetValue((meld.Seat, meld.GroupIndex), out MeldGroup? actualMeld),
                    label + " missing meld group seat" + meld.Seat + "/" + meld.GroupIndex);
                Assert.NotNull(actualMeld);
                SeqEq(meld.Tiles, actualMeld!.Tiles, label + " meld tiles seat" + meld.Seat + "/" + meld.GroupIndex);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                SeqEq(expectedState.Rivers[seat], actual.Rivers[seat], label + " river seat " + seat);
            }

            SeqEq(expectedState.Replacements, actual.Replacements, label + " replacements");
            Eq(14, expectedState.DeadWallSlots.Count, label + " expected dead-wall slots");
            Eq(14, actual.DeadWallSlots.Count, label + " actual dead-wall slots");

            for (int slot = 0; slot < 14; slot++)
            {
                Eq(expectedState.DeadWallSlots[slot], actual.DeadWallSlots[slot], label + " dead-wall slot " + slot);
            }

            JsonElement zoneCounts = expected.GetProperty("zoneCounts");
            Eq(Int(zoneCounts, "WALL"), actual.Positions.Count(p => p.Zone == TileZone.Wall), label + " WALL count");
            Eq(Int(zoneCounts, "DEAD_WALL"), actual.Positions.Count(p => p.Zone == TileZone.DeadWall), label + " DEAD_WALL count");
            Eq(
                Int(zoneCounts, "DEAD_WALL_REPLACEMENT"),
                actual.Positions.Count(p => p.Zone == TileZone.DeadWallReplacement),
                label + " DEAD_WALL_REPLACEMENT count");
            Eq(Int(zoneCounts, "HAND"), actual.Positions.Count(p => p.Zone == TileZone.Hand), label + " HAND count");
            Eq(Int(zoneCounts, "DISCARD"), actual.Positions.Count(p => p.Zone == TileZone.Discard), label + " DISCARD count");
            Eq(Int(zoneCounts, "MELD"), actual.Positions.Count(p => p.Zone == TileZone.Meld), label + " MELD count");
            Eq(Int(zoneCounts, "SLEEVE"), actual.Positions.Count(p => p.Zone == TileZone.Sleeve), label + " SLEEVE count");
            Eq(14, actual.Positions.Count(p => p.Zone == TileZone.DeadWall) +
                  actual.Positions.Count(p => p.Zone == TileZone.DeadWallReplacement), label + " effective dead wall");
        }

        /// <summary>
        /// Validates the fixture's redundant STRUCTURE expressions before comparing anything to
        /// the C# map: 136 unique tile ids, unique legal raw positions, hand slot permutations
        /// allowed, meld group identity exact with only in-group order free, and hands/melds/
        /// rivers/dead-wall/replacements/dora all agreeing with placements.
        /// </summary>
        private static CanonicalState ParseExpectedState(JsonElement expected, string label)
        {
            var state = new CanonicalState();
            JsonElement pointers = expected.GetProperty("pointers");
            state.WallPointer = Int(pointers, "wallPointer");
            state.WallEndExclusive = Int(pointers, "wallEndExclusive");
            state.RinshanPointer = Int(pointers, "rinshanPointer");
            Assert.InRange(state.WallPointer, 0, 122);
            Assert.InRange(state.WallEndExclusive, 0, 122);
            Assert.InRange(state.RinshanPointer, 0, 4);
            Assert.True(state.WallPointer <= state.WallEndExclusive, label + " wallPointer > wallEndExclusive");

            state.LastDiscard = ParseLastDiscard(expected.GetProperty("lastDiscard"));
            state.DrawnTile = ParseDrawn(expected.GetProperty("drawnTile"));
            state.RinshanDraw = ParseRinshan(expected.GetProperty("rinshanDraw"));
            state.DoraIndicators = Ints(expected.GetProperty("doraIndicators"));
            int[] replacementIds = Ints(expected.GetProperty("replacements"));
            Assert.InRange(replacementIds.Length, 0, 4);
            Assert.Equal(state.RinshanPointer, replacementIds.Length);
            state.Replacements.AddRange(replacementIds);

            var raw = new List<(int TileId, TilePosition Position)>();
            var tileIds = new HashSet<int>();
            var rawPositions = new HashSet<TilePosition>();
            foreach (JsonElement placement in expected.GetProperty("placements").EnumerateArray())
            {
                int tileId = Int(placement, "tileId");
                Assert.InRange(tileId, 0, 135);
                Assert.True(tileIds.Add(tileId), label + " duplicate tile id " + tileId);
                TilePosition position = ParsePosition(placement);
                Assert.True(position.IsAssigned, label + " T" + tileId + " must be assigned");
                Assert.True(rawPositions.Add(position), label + " duplicate raw position " + position);
                raw.Add((tileId, position));
            }

            Assert.Equal(136, raw.Count);
            Assert.Equal(136, tileIds.Count);
            Assert.Equal(136, rawPositions.Count);

            var handBySeat = new List<int>[4];
            var riverBySeat = new List<(int Slot, int TileId)>[4];
            var meldGroups = new Dictionary<(int Seat, int Group), List<(int Offset, int TileId)>>();
            var deadWallSlots = new int?[14];
            for (int seat = 0; seat < 4; seat++)
            {
                handBySeat[seat] = new List<int>();
                riverBySeat[seat] = new List<(int Slot, int TileId)>();
            }

            foreach ((int tileId, TilePosition position) in raw)
            {
                switch (position.Zone)
                {
                    case TileZone.Wall:
                        Eq(-1, position.Seat, label + " T" + tileId + " WALL seat");
                        Assert.InRange(position.Index, 0, 121);
                        Assert.True(position.Index < state.WallEndExclusive, label + " T" + tileId + " WALL index");
                        break;
                    case TileZone.DeadWall:
                        Eq(-1, position.Seat, label + " T" + tileId + " DEAD_WALL seat");
                        Assert.InRange(position.Index, 0, 13);
                        Assert.True(position.Index >= state.RinshanPointer, label + " T" + tileId + " consumed DEAD_WALL slot");
                        Assert.Null(deadWallSlots[position.Index]);
                        deadWallSlots[position.Index] = tileId;
                        break;
                    case TileZone.DeadWallReplacement:
                        Eq(-1, position.Seat, label + " T" + tileId + " REPLACEMENT seat");
                        Assert.InRange(position.Index, 0, 3);
                        Assert.True(position.Index < replacementIds.Length, label + " T" + tileId + " replacement slot");
                        Assert.Equal(replacementIds[position.Index], tileId);
                        break;
                    case TileZone.Hand:
                        Assert.InRange(position.Seat, 0, 3);
                        Assert.InRange(position.Index, 0, 13);
                        handBySeat[position.Seat].Add(tileId);
                        break;
                    case TileZone.Discard:
                        Assert.InRange(position.Seat, 0, 3);
                        Assert.InRange(position.Index, 0, 135);
                        riverBySeat[position.Seat].Add((position.Index, tileId));
                        break;
                    case TileZone.Meld:
                        Assert.InRange(position.Seat, 0, 3);
                        Assert.InRange(position.Index, 0, 15);
                        var key = (position.Seat, position.Index / 4);
                        if (!meldGroups.TryGetValue(key, out List<(int Offset, int TileId)>? groupTiles))
                        {
                            groupTiles = new List<(int Offset, int TileId)>();
                            meldGroups.Add(key, groupTiles);
                        }

                        groupTiles.Add((position.Index % 4, tileId));
                        break;
                    case TileZone.Sleeve:
                        Assert.InRange(position.Seat, 0, 3);
                        Assert.InRange(position.Index, 0, 13);
                        break;
                    default:
                        Assert.Fail(label + " T" + tileId + " has unsupported zone " + position.Zone);
                        break;
                }
            }

            state.DeadWallSlots.AddRange(deadWallSlots);
            int effectiveDeadWall = deadWallSlots.Count(v => v.HasValue) + replacementIds.Length;
            Eq(14, effectiveDeadWall, label + " effective dead wall");

            JsonElement expectedDeadWall = expected.GetProperty("deadWallBySlot");
            Assert.Equal(14, expectedDeadWall.GetArrayLength());
            for (int slot = 0; slot < 14; slot++)
            {
                if (expectedDeadWall[slot].ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(deadWallSlots[slot]);
                }
                else
                {
                    Assert.Equal(expectedDeadWall[slot].GetInt32(), deadWallSlots[slot]);
                }
            }

            JsonElement expectedReplacements = expected.GetProperty("replacements");
            Assert.Equal(replacementIds, Ints(expectedReplacements));
            JsonElement hands = expected.GetProperty("hands");
            Assert.Equal(4, hands.GetArrayLength());
            for (int seat = 0; seat < 4; seat++)
            {
                handBySeat[seat].Sort();
                JsonElement hand = hands[seat];
                Eq(seat, Int(hand, "seat"), label + " hand seat");
                int[] handTiles = Ints(hand.GetProperty("tiles"));
                Eq(Int(hand, "count"), handTiles.Length, label + " hand count seat " + seat);
                SeqEq(handTiles, handBySeat[seat], label + " hands/placements seat " + seat);
                state.Hands.Add(handBySeat[seat]);
            }

            JsonElement melds = expected.GetProperty("melds");
            var fixtureMelds = new Dictionary<(int Seat, int Group), int[]>();
            foreach (JsonElement meld in melds.EnumerateArray())
            {
                int seat = Int(meld, "seat");
                int group = Int(meld, "groupIndex");
                int[] tiles = Ints(meld.GetProperty("tiles"));
                Assert.True(fixtureMelds.TryAdd((seat, group), tiles), label + " duplicate meld group " + seat + "/" + group);
            }

            Eq(fixtureMelds.Count, meldGroups.Count, label + " melds/placements group count");
            foreach (KeyValuePair<(int Seat, int Group), List<(int Offset, int TileId)>> pair in meldGroups)
            {
                List<(int Offset, int TileId)> groupTiles = pair.Value;
                groupTiles.Sort((left, right) => left.Offset.CompareTo(right.Offset));
                int[] offsets = groupTiles.Select(t => t.Offset).ToArray();
                SeqEq(Enumerable.Range(0, groupTiles.Count).ToArray(), offsets,
                    label + " meld group seat" + pair.Key.Seat + "/" + pair.Key.Group + " raw offsets");
                Assert.True(groupTiles.Count == 3 || groupTiles.Count == 4,
                    label + " meld group must have 3 or 4 tiles");
                int[] tiles = groupTiles.Select(t => t.TileId).OrderBy(t => t).ToArray();
                Assert.True(fixtureMelds.TryGetValue(pair.Key, out int[]? fixtureTiles),
                    label + " melds/placements missing group " + pair.Key.Seat + "/" + pair.Key.Group);
                SeqEq(fixtureTiles!, tiles, label + " melds/placements seat" + pair.Key.Seat + "/" + pair.Key.Group);
                state.Melds.Add(new MeldGroup(pair.Key.Seat, pair.Key.Group, tiles.ToList()));
            }

            for (int seat = 0; seat < 4; seat++)
            {
                riverBySeat[seat].Sort((left, right) => left.Slot.CompareTo(right.Slot));
                int[] slots = riverBySeat[seat].Select(r => r.Slot).ToArray();
                SeqEq(Enumerable.Range(0, riverBySeat[seat].Count).ToArray(), slots,
                    label + " river slots must be dense from 0 for seat " + seat);
                int[] riverTiles = riverBySeat[seat].Select(r => r.TileId).ToArray();
                JsonElement river = expected.GetProperty("rivers")[seat];
                Eq(seat, Int(river, "seat"), label + " river seat");
                SeqEq(Ints(river.GetProperty("tiles")), riverTiles, label + " rivers/placements seat " + seat);
                state.Rivers.Add(riverTiles.ToList());
            }

            Assert.Equal(4, expected.GetProperty("rivers").GetArrayLength());
            Assert.Equal(state.RinshanPointer, replacementIds.Length);
            if (state.RinshanDraw is RinshanDrawEvidence rinshan)
            {
                Eq(state.RinshanPointer - 1, rinshan.DeadWallIndex, label + " rinshan slot");
                Assert.Null(deadWallSlots[rinshan.DeadWallIndex]);
                Assert.True(handBySeat[rinshan.Seat].Contains(rinshan.Tile.Value),
                    label + " rinshan tile must be in hand");
            }

            if (state.DrawnTile is DrawnTileEvidence drawn)
            {
                Assert.True(handBySeat[drawn.Seat].Contains(drawn.Tile.Value), label + " drawn tile must be in hand");
                if (drawn.Source == DrawSource.Rinshan)
                {
                    Assert.True(state.RinshanDraw.HasValue, label + " rinshan drawn source requires rinshan evidence");
                    Eq(drawn.Tile, state.RinshanDraw!.Value.Tile, label + " drawn/rinshan tile");
                    Eq(drawn.Seat, state.RinshanDraw!.Value.Seat, label + " drawn/rinshan seat");
                }
            }
            else
            {
                Assert.Null(state.RinshanDraw);
            }

            if (state.LastDiscard is LastDiscardEvidence last)
            {
                Assert.InRange(last.Seat, 0, 3);
                Assert.Equal(last.Seat, last.Position.Seat);
                Assert.InRange(last.Position.Index, 0, 135);
                Eq(state.Rivers[last.Seat].Count - 1, last.Position.Index,
                    label + " lastDiscard must be the latest river slot");
                Assert.Equal(state.Rivers[last.Seat].Last(), last.Tile.Value);
            }

            var doraSeen = new HashSet<int>();
            foreach (int dora in state.DoraIndicators)
            {
                Assert.True(doraSeen.Add(dora), label + " duplicate dora indicator " + dora);
                Assert.InRange(dora, 0, 135);
                Assert.True(deadWallSlots.Contains(dora), label + " dora indicator must be in DEAD_WALL");
                int doraSlot = Array.IndexOf(deadWallSlots, dora);
                Assert.True(doraSlot >= 4, label + " dora indicator must be in an unrevealed slot >=4");
            }

            JsonElement zoneCounts = expected.GetProperty("zoneCounts");
            Eq(Int(zoneCounts, "WALL"), raw.Count(p => p.Position.Zone == TileZone.Wall), label + " WALL count");
            Eq(Int(zoneCounts, "DEAD_WALL"), raw.Count(p => p.Position.Zone == TileZone.DeadWall), label + " DEAD_WALL count");
            Eq(Int(zoneCounts, "DEAD_WALL_REPLACEMENT"), raw.Count(p => p.Position.Zone == TileZone.DeadWallReplacement), label + " REPLACEMENT count");
            Eq(Int(zoneCounts, "HAND"), raw.Count(p => p.Position.Zone == TileZone.Hand), label + " HAND count");
            Eq(Int(zoneCounts, "DISCARD"), raw.Count(p => p.Position.Zone == TileZone.Discard), label + " DISCARD count");
            Eq(Int(zoneCounts, "MELD"), raw.Count(p => p.Position.Zone == TileZone.Meld), label + " MELD count");
            Eq(Int(zoneCounts, "SLEEVE"), raw.Count(p => p.Position.Zone == TileZone.Sleeve), label + " SLEEVE count");

            for (int tileId = 0; tileId < 136; tileId++)
            {
                state.Positions[tileId] = raw.Single(p => p.TileId == tileId).Position;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int rank = 0; rank < state.Hands[seat].Count; rank++)
                {
                    state.Positions[state.Hands[seat][rank]] = TilePosition.Hand(seat, rank);
                }
            }

            foreach (MeldGroup meld in state.Melds)
            {
                for (int rank = 0; rank < meld.Tiles.Count; rank++)
                {
                    state.Positions[meld.Tiles[rank]] = TilePosition.Meld(meld.Seat, meld.GroupIndex * 4 + rank);
                }
            }

            Eq(136, state.Positions.Distinct().Count(), label + " canonical unique positions");
            return state;
        }

        private static void AssertEvidence(JsonElement expected, LastDiscardEvidence? actual, string label)
        {
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
                return;
            }

            Assert.True(actual.HasValue, label + " expected evidence");
            LastDiscardEvidence value = actual!.Value;
            Eq(Int(expected, "tileId"), value.Tile.Value, label + " tile");
            Eq(Int(expected, "seat"), value.Seat, label + " seat");
            Eq(Int(expected, "slot"), value.Position.Index, label + " slot");
        }

        private static void AssertDrawn(JsonElement expected, DrawnTileEvidence? actual, string label)
        {
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
                return;
            }

            Assert.True(actual.HasValue, label + " expected drawn tile");
            DrawnTileEvidence value = actual!.Value;
            Eq(Int(expected, "tileId"), value.Tile.Value, label + " tile");
            Eq(Int(expected, "seat"), value.Seat, label + " seat");
            Eq(ParseDrawSource(Str(expected, "source")), value.Source, label + " source");
        }

        private static void AssertRinshan(JsonElement expected, RinshanDrawEvidence? actual, string label)
        {
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
                return;
            }

            Assert.True(actual.HasValue, label + " expected rinshan evidence");
            RinshanDrawEvidence value = actual!.Value;
            Eq(Int(expected, "tileId"), value.Tile.Value, label + " tile");
            Eq(Int(expected, "seat"), value.Seat, label + " seat");
            Eq(Int(expected, "slot"), value.DeadWallIndex, label + " slot");
        }

        private static CanonicalState Canonicalize(TilePositionMap map)
        {
            var state = new CanonicalState();
            for (int id = 0; id < 136; id++)
            {
                state.Positions[id] = map[id];
            }

            state.WallPointer = map.WallPointer;
            state.WallEndExclusive = map.WallEndExclusive;
            state.RinshanPointer = map.RinshanPointer;
            state.LastDiscard = map.LastDiscard;
            state.DrawnTile = map.DrawnTile;
            state.RinshanDraw = map.LastRinshanDraw;
            state.DoraIndicators = map.DoraIndicators.Select(t => t.Value).ToArray();

            for (int seat = 0; seat < 4; seat++)
            {
                var hand = new List<int>();
                var river = new List<(int Slot, int TileId)>();
                for (int id = 0; id < 136; id++)
                {
                    TilePosition position = map[id];
                    if (position.Zone == TileZone.Hand && position.Seat == seat)
                    {
                        hand.Add(id);
                    }
                    else if (position.Zone == TileZone.Discard && position.Seat == seat)
                    {
                        river.Add((position.Index, id));
                    }
                }

                hand.Sort();
                state.Hands.Add(hand);
                state.Rivers.Add(river.OrderBy(item => item.Slot).Select(item => item.TileId).ToList());
            }

            for (int slot = 0; slot < 14; slot++)
            {
                if (map.TryGetTileAt(TilePosition.DeadWall(slot), out TileId deadWallTile))
                {
                    state.DeadWallSlots.Add(deadWallTile.Value);
                }
                else
                {
                    state.DeadWallSlots.Add(null);
                }
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int baseSlot = 0; baseSlot < TilePositionMap.MeldGroupCount * TilePositionMap.MeldGroupSize; baseSlot += TilePositionMap.MeldGroupSize)
                {
                    var tiles = new List<int>();
                    for (int i = 0; i < TilePositionMap.MeldGroupSize; i++)
                    {
                        if (map.TryGetTileAt(TilePosition.Meld(seat, baseSlot + i), out TileId tile))
                        {
                            tiles.Add(tile.Value);
                        }
                    }

                    if (tiles.Count > 0)
                    {
                        tiles.Sort();
                        state.Melds.Add(new MeldGroup(seat, baseSlot / TilePositionMap.MeldGroupSize, tiles));
                    }
                }
            }

            for (int slot = 0; slot < 4; slot++)
            {
                if (map.TryGetTileAt(TilePosition.DeadWallReplacement(slot), out TileId replacement))
                {
                    state.Replacements.Add(replacement.Value);
                }
            }

            // Canonical positions for the slot-addressed zones: hand slots are allocation
            // details, and meld group-internal order is free; wall/discard/dead-wall/
            // replacement/sleeve slots remain exact.
            for (int seat = 0; seat < 4; seat++)
            {
                for (int rank = 0; rank < state.Hands[seat].Count; rank++)
                {
                    state.Positions[state.Hands[seat][rank]] = TilePosition.Hand(seat, rank);
                }
            }

            foreach (MeldGroup meld in state.Melds)
            {
                for (int rank = 0; rank < meld.Tiles.Count; rank++)
                {
                    state.Positions[meld.Tiles[rank]] = TilePosition.Meld(meld.Seat, meld.GroupIndex * 4 + rank);
                }
            }

            return state;
        }

        // -----------------------------------------------------------------
        // Fixture parsing helpers.
        // -----------------------------------------------------------------

        private static JsonDocument LoadGolden()
        {
            string path = Path.Combine(RepositoryRoot(), "tests", "v5-golden", "movement-v1.json");
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream);
        }

        private static JsonElement Sample(string caseId)
        {
            foreach (JsonElement sample in Golden.Value.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (Str(sample, "caseId") == caseId)
                {
                    return sample;
                }
            }

            throw new InvalidOperationException("movement golden case not found: " + caseId);
        }

        private static TilePosition ParsePosition(JsonElement element)
        {
            TileZone zone = ParseZone(Str(element, "zone"));
            int seat = Int(element, "seat");
            int index = Int(element, "index");
            switch (zone)
            {
                case TileZone.Unassigned:
                    return TilePosition.Unassigned;
                case TileZone.Wall:
                    return TilePosition.Wall(index);
                case TileZone.DeadWall:
                    return TilePosition.DeadWall(index);
                case TileZone.DeadWallReplacement:
                    return TilePosition.DeadWallReplacement(index);
                case TileZone.Hand:
                    return TilePosition.Hand(seat, index);
                case TileZone.Discard:
                    return TilePosition.Discard(seat, index);
                case TileZone.Meld:
                    return TilePosition.Meld(seat, index);
                case TileZone.Sleeve:
                    return TilePosition.Sleeve(seat, index);
                default:
                    throw new InvalidOperationException("unknown zone " + zone);
            }
        }

        private static LastDiscardEvidence? ParseLastDiscard(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return new LastDiscardEvidence(
                new TileId(Int(element, "tileId")),
                TilePosition.Discard(Int(element, "seat"), Int(element, "slot")));
        }

        private static DrawnTileEvidence? ParseDrawn(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return new DrawnTileEvidence(
                new TileId(Int(element, "tileId")),
                Int(element, "seat"),
                ParseDrawSource(Str(element, "source")));
        }

        private static RinshanDrawEvidence? ParseRinshan(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return new RinshanDrawEvidence(
                new TileId(Int(element, "tileId")),
                Int(element, "seat"),
                Int(element, "slot"));
        }

        private static TileZone ParseZone(string name)
        {
            switch (name)
            {
                case "UNASSIGNED": return TileZone.Unassigned;
                case "WALL": return TileZone.Wall;
                case "DEAD_WALL": return TileZone.DeadWall;
                case "DEAD_WALL_REPLACEMENT": return TileZone.DeadWallReplacement;
                case "HAND": return TileZone.Hand;
                case "DISCARD": return TileZone.Discard;
                case "MELD": return TileZone.Meld;
                case "SLEEVE": return TileZone.Sleeve;
                default: throw new InvalidOperationException("unknown golden zone " + name);
            }
        }

        private static DrawSource ParseDrawSource(string name)
        {
            switch (name)
            {
                case "WALL": return DrawSource.Wall;
                case "RINSHAN": return DrawSource.Rinshan;
                case "DEAL_EXTRA": return DrawSource.DealExtra;
                default: throw new InvalidOperationException("unknown draw source " + name);
            }
        }

        private static void AssertSourceHash(string repositoryRoot, JsonElement source)
        {
            string relative = Str(source, "file");
            string path = Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source file missing: " + relative);

            byte[] raw = File.ReadAllBytes(path);
            string text = Encoding.UTF8.GetString(raw);
            string lf = text.Replace("\r\n", "\n");
            int crlf = Count(text, "\r\n");
            int lfOnly = Count(text, "\n") - crlf;
            string recordedKind = Str(source, "lineEndings");
            string actualKind = ClassifyLineEndings(crlf, lfOnly);
            string actualRawHash = Sha256(raw);
            string recordedRawHash = Str(source, "sha256RawBytes");

            // Content authority is always the LF-normalized hash. Raw bytes are
            // additionally verified when the checkout has the recorded line-ending
            // profile. If Git's autocrlf/checkout changed only line endings, the raw
            // hash must differ and the test says so explicitly; it never treats the
            // LF hash as a raw-byte hash.
            Eq(
                Str(source, "sha256LfNormalized"),
                Sha256(Encoding.UTF8.GetBytes(lf)),
                relative + " LF-normalized SHA-256");

            if (string.Equals(recordedKind, actualKind, StringComparison.Ordinal))
            {
                Eq(Int(source, "bytesRaw"), raw.Length, relative + " raw bytes");
                Eq(recordedRawHash, actualRawHash, relative + " raw SHA-256");
                Eq(Int(source, "crlfCount"), crlf, relative + " CRLF count");
                Eq(Int(source, "lfOnlyCount"), lfOnly, relative + " LF-only count");
            }
            else
            {
                Assert.True(
                    !string.Equals(recordedRawHash, actualRawHash, StringComparison.Ordinal),
                    relative + " changed line-ending profile must change raw bytes; recorded " +
                    recordedKind + ", actual " + actualKind);
                Assert.True(
                    Int(source, "bytesRaw") != raw.Length,
                    relative + " changed line-ending profile must change raw byte count");
                Assert.True(
                    actualKind == "CRLF" || actualKind == "LF" || actualKind == "MIXED" || actualKind == "NONE",
                    relative + " unknown actual line-ending profile");
            }
        }

        private static int Count(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                index = text.IndexOf(needle, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                index += needle.Length;
            }
        }

        private static string ClassifyLineEndings(int crlf, int lfOnly)
        {
            if (crlf > 0 && lfOnly == 0) return "CRLF";
            if (crlf == 0 && lfOnly > 0) return "LF";
            if (crlf == 0 && lfOnly == 0) return "NONE";
            return "MIXED";
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "dotnet")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }

        private static string Str(JsonElement element, string name)
        {
            return element.GetProperty(name).GetString() ??
                   throw new InvalidOperationException("expected non-null string " + name);
        }

        private static int Int(JsonElement element, string name)
        {
            return element.GetProperty(name).GetInt32();
        }

        private static bool Bool(JsonElement element, string name)
        {
            return element.GetProperty(name).GetBoolean();
        }

        private static int[] Ints(JsonElement array)
        {
            return array.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        }

        private static string[] Strings(JsonElement array)
        {
            return array.EnumerateArray().Select(v => v.GetString()!).ToArray();
        }

        private static void Eq<T>(T expected, T actual, string label)
        {
            Assert.True(
                EqualityComparer<T>.Default.Equals(expected, actual),
                label + ": expected " + expected + ", actual " + actual);
        }

        private static void SeqEq<T>(IEnumerable<T> expected, IEnumerable<T> actual, string label)
        {
            T[] left = expected.ToArray();
            T[] right = actual.ToArray();
            Assert.True(
                left.SequenceEqual(right),
                label + ": expected [" + string.Join(",", left) + "], actual [" + string.Join(",", right) + "]");
        }

        private sealed class CanonicalState
        {
            public TilePosition[] Positions { get; } = new TilePosition[136];

            public int WallPointer { get; set; }

            public int WallEndExclusive { get; set; }

            public int RinshanPointer { get; set; }

            public LastDiscardEvidence? LastDiscard { get; set; }

            public DrawnTileEvidence? DrawnTile { get; set; }

            public RinshanDrawEvidence? RinshanDraw { get; set; }

            public int[] DoraIndicators { get; set; } = Array.Empty<int>();

            public List<List<int>> Hands { get; } = new List<List<int>>();

            public List<MeldGroup> Melds { get; } = new List<MeldGroup>();

            public List<List<int>> Rivers { get; } = new List<List<int>>();

            public List<int?> DeadWallSlots { get; } = new List<int?>();

            public List<int> Replacements { get; } = new List<int>();
        }

        private sealed class MeldGroup
        {
            public MeldGroup(int seat, int groupIndex, List<int> tiles)
            {
                Seat = seat;
                GroupIndex = groupIndex;
                Tiles = tiles;
            }

            public int Seat { get; }

            public int GroupIndex { get; }

            public List<int> Tiles { get; }
        }
    }
}

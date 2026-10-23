using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// P1.1 物理牌差分：真实读取 V4 导出的 <c>tests/v5-golden/physical-v1.json</c>，
    /// 逐项对照 C# Domain 的公开 API。覆盖且仅覆盖清单六项
    /// G-001a/G-001b/G-001c/G-002/G-003a/G-003b。
    ///
    /// 不把 C# 实现当 oracle：所有 expected 都来自 V4 真实调用。G-003a 的 32 张完整
    /// 牌墙作为显式随机输入交给 C# <see cref="RecordedRandomDraw"/>，本测试断言结构法律
    /// 与 136 位置，不声称 C# 复现 V4 的 JVM seed RNG。
    ///
    /// G-003b 的 V4 mutable TilePool 与 C# immutable <see cref="TilePositionMap"/> 结构
    /// 不同：V4 反射删除/账本失配探针在 C# 侧没有对应的可变容器，故 mapping 明确记为
    /// structural；C# 侧验证构造期 duplicate/missing/duplicate-position 拒绝与只读
    /// AsPlacements。没有把不可复现的 mutable 损坏注入写成 PASS。
    /// </summary>
    public sealed class PhysicalGoldenTests
    {
        private static readonly Lazy<JsonDocument> Golden = new Lazy<JsonDocument>(LoadGolden);

        [Fact]
        public void GoldenFileCarriesRealV4ProvenanceAndOnlySixSamples()
        {
            JsonElement root = Golden.Value.RootElement;
            Assert.Equal("v5-physical-golden-v1", Str(root, "schema"));
            Assert.Equal(1, root.GetProperty("generatorVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("rulesetVersion").GetInt32());
            Assert.Equal("exported", Str(root, "extractionStatus"));
            Assert.False(root.GetProperty("clockUsed").GetBoolean());

            string commit = Str(root, "v4Commit");
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(Uri.IsHexDigit), "v4Commit must be a 40-hex git commit");
            Assert.True(root.GetProperty("v4ProductionTreeClean").GetBoolean());
            Assert.Equal(0, root.GetProperty("trackedProductionModifiedFiles").GetArrayLength());

            string exporterSha = Str(root, "exporterSourceSha256");
            Assert.Equal(64, exporterSha.Length);
            Assert.Contains("CRLF normalized to LF", Str(root, "sourceHashAlgorithm"));
            string repositoryRoot = RepositoryRoot();
            string exporterPath = Path.Combine(
                repositoryRoot, "engine", "src", "test", "kotlin", "com", "majiang", "model",
                "V5PhysicalGoldenExporterTest.kt");
            Assert.Equal(exporterSha, Sha256SourceFile(exporterPath));

            foreach (JsonElement source in root.GetProperty("v4SourceHashes").EnumerateArray())
            {
                string relative = Str(source, "file").Replace('/', Path.DirectorySeparatorChar);
                Assert.Equal(Str(source, "sha256"), Sha256SourceFile(Path.Combine(repositoryRoot, relative)));
            }

            JsonElement generation = root.GetProperty("generationResult");
            Assert.Equal(6, generation.GetProperty("samplesExported").GetInt32());
            Assert.Equal(0, generation.GetProperty("failures").GetInt32());
            Assert.Equal(0, generation.GetProperty("remainingApplicable").GetInt32());
            Assert.Equal("PASSED", Str(generation, "exporterTestResult"));
            Assert.Equal(64, generation.GetProperty("totalWallsRecorded").GetInt32());
            Assert.Equal(4352, generation.GetProperty("totalPositionsRecorded").GetInt32());

            JsonElement scope = root.GetProperty("scope");
            Assert.Equal(6, scope.GetProperty("manifestEntriesTargeted").GetInt32());
            Assert.Equal(6, scope.GetProperty("manifestEntriesExportedByThisRun").GetInt32());
            Assert.Equal(67, scope.GetProperty("manifestEntriesNotFullAudit").GetInt32());
            Assert.False(scope.GetProperty("fullManifestAudited").GetBoolean());
            Assert.Contains("no full 73-case", Str(scope, "note"));

            string[] expectedIds = { "G-001a", "G-001b", "G-001c", "G-002", "G-003a", "G-003b" };
            JsonElement samples = root.GetProperty("samples");
            Assert.Equal(expectedIds.Length, samples.GetArrayLength());
            var actualIds = new List<string>();
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                actualIds.Add(Str(sample, "sampleId"));
                Assert.Equal("exported", Str(sample, "extractionStatus"));
                Assert.Equal("not-applicable", Str(sample.GetProperty("input"), "clockMillis"));
                Assert.Equal("not-applicable", Str(sample.GetProperty("input"), "sessionId"));
                Assert.Contains("V5PhysicalGoldenExporterTest", Str(sample, "command"));
            }

            Assert.Equal(expectedIds, actualIds);

            JsonElement hash = root.GetProperty("hashCrosswalk");
            Assert.Equal("NOT_AVAILABLE", Str(hash, "v4CanonicalStateHash"));
            Assert.Contains("not a V4 digest", Str(hash, "csharpHash"));
            Assert.Contains("SHA-256", Str(hash, "wallCanonicalDigestAlgorithm"));
        }

        [Fact]
        public void G001aStandardSetMatchesDomainTileIdentityAndRedFiveRules()
        {
            JsonElement expected = Sample("G-001a").GetProperty("expected");
            Assert.Equal(136, expected.GetProperty("size").GetInt32());
            Assert.Equal(136, expected.GetProperty("distinctIdentities").GetInt32());
            Assert.Equal(3, expected.GetProperty("redFiveCount").GetInt32());
            Assert.Equal(133, expected.GetProperty("nonRedFiveCount").GetInt32());
            Assert.True(expected.GetProperty("standardSetOrderIsTileIdOrder").GetBoolean());

            JsonElement tiles = expected.GetProperty("tiles");
            Assert.Equal(136, tiles.GetArrayLength());
            var redFiveIds = new List<int>();
            for (int i = 0; i < tiles.GetArrayLength(); i++)
            {
                JsonElement json = tiles[i];
                int id = Int(json, "tileId");
                Assert.Equal(i, id);

                TileId tile = new TileId(id);
                Assert.Equal(id, tile.Value);
                Assert.Equal(Int(json, "typeIndex"), tile.TypeIndex);
                Assert.Equal(Int(json, "copyIndex"), tile.CopyIndex);
                Assert.Equal(Suit(Str(json, "suit")), tile.Suit);
                Assert.Equal(Int(json, "rank"), tile.Rank);
                Assert.Equal(Bool(json, "isRedFive"), tile.IsRedFive);
                Assert.Equal(Bool(json, "isNumberTile"), tile.IsNumberTile);
                Assert.Equal(Bool(json, "isHonor"), tile.IsHonor);
                Assert.Equal(Bool(json, "isTerminal"), tile.IsTerminal);
                Assert.Equal(Bool(json, "isTerminalOrHonor"), tile.IsTerminalOrHonor);
                Assert.Equal(Bool(json, "isSimple"), tile.IsSimple);
                Assert.Equal(tile, TileId.All[i]);

                if (tile.IsRedFive)
                {
                    redFiveIds.Add(id);
                }
            }

            Assert.Equal(new[] { 19, 55, 91 }, redFiveIds.OrderBy(v => v).ToArray());
            Assert.Equal(redFiveIds, Ints(expected.GetProperty("redFiveTileIds")));
            Assert.Equal(
                redFiveIds.OrderBy(v => v),
                TileId.All.Where(t => t.IsRedFive).Select(t => t.Value).OrderBy(v => v));
        }

        [Fact]
        public void G001bTypeIndexBoundariesAnd34GroupsMatchDomain()
        {
            JsonElement expected = Sample("G-001b").GetProperty("expected");
            Assert.Equal(34, expected.GetProperty("distinctTypeIndexes").GetInt32());
            Assert.Equal(136, expected.GetProperty("totalTiles").GetInt32());
            Assert.True(expected.GetProperty("allGroupsFour").GetBoolean());
            Assert.Equal("typeIndex * 4 + copyIndex", Str(expected, "tileIdFormula"));

            int[] boundaryIndexes = Ints(expected.GetProperty("boundaryTypeIndexes"));
            Assert.Equal(new[] { 0, 8, 9, 17, 18, 26, 27, 30, 31, 33 }, boundaryIndexes);
            JsonElement boundaries = expected.GetProperty("boundarySamples");
            Assert.Equal(boundaryIndexes.Length, boundaries.GetArrayLength());
            for (int i = 0; i < boundaries.GetArrayLength(); i++)
            {
                JsonElement json = boundaries[i];
                int typeIndex = Int(json, "typeIndex");
                Assert.Equal(boundaryIndexes[i], typeIndex);
                TileId tile = TileId.FromTypeIndex(typeIndex, Int(json, "copyIndex"));
                Assert.Equal(Int(json, "tileId"), tile.Value);
                Assert.Equal(typeIndex, tile.TypeIndex);
                Assert.Equal(Suit(Str(json, "suit")), tile.Suit);
                Assert.Equal(Int(json, "rank"), tile.Rank);
            }

            JsonElement groups = expected.GetProperty("groups");
            Assert.Equal(34, groups.GetArrayLength());
            var seenTypes = new HashSet<int>();
            foreach (JsonElement group in groups.EnumerateArray())
            {
                int typeIndex = Int(group, "typeIndex");
                Assert.True(seenTypes.Add(typeIndex), "duplicate typeIndex " + typeIndex);
                int[] tileIds = Ints(group.GetProperty("tileIds"));
                Assert.Equal(4, tileIds.Length);
                Assert.Equal(Int(group, "count"), tileIds.Length);
                Assert.Equal(
                    TileId.All.Where(t => t.TypeIndex == typeIndex).Select(t => t.Value).ToArray(),
                    tileIds);

                TileId first = new TileId(tileIds[0]);
                Assert.Equal(typeIndex, first.TypeIndex);
                Assert.Equal(Suit(Str(group, "suit")), first.Suit);
                Assert.Equal(Int(group, "rank"), first.Rank);
            }

            Assert.Equal(34, seenTypes.Count);
        }

        [Fact]
        public void G001cTileIdRoundTripAndIllegalInputsMatch()
        {
            JsonElement expected = Sample("G-001c").GetProperty("expected");
            Assert.Equal(136, expected.GetProperty("validCount").GetInt32());
            Assert.True(expected.GetProperty("allRoundTripEqual").GetBoolean());

            JsonElement valid = expected.GetProperty("validRoundTrips");
            Assert.Equal(136, valid.GetArrayLength());
            for (int i = 0; i < valid.GetArrayLength(); i++)
            {
                JsonElement json = valid[i];
                int id = Int(json, "tileId");
                Assert.Equal(i, id);

                TileId tile = new TileId(id);
                Assert.Equal(Int(json, "typeIndex"), tile.TypeIndex);
                Assert.Equal(Int(json, "copyIndex"), tile.CopyIndex);
                Assert.Equal(Suit(Str(json, "suit")), tile.Suit);
                Assert.Equal(Int(json, "rank"), tile.Rank);

                TileId fromType = TileId.FromTypeIndex(tile.TypeIndex, tile.CopyIndex);
                Assert.Equal(tile, fromType);
                Assert.Equal(id, fromType.Value);
                Assert.True(TileId.TryFromValue(id, out TileId parsed));
                Assert.Equal(tile, parsed);
            }

            JsonElement boundaries = expected.GetProperty("boundaries");
            Assert.Equal(2, boundaries.GetArrayLength());
            Assert.Equal(0, Int(boundaries[0], "tileId"));
            Assert.Equal(135, Int(boundaries[1], "tileId"));
            Assert.Equal(TileId.FromTypeIndex(33, 3), new TileId(135));

            JsonElement invalid = expected.GetProperty("invalid");
            Assert.Equal(4, invalid.GetArrayLength());
            foreach (JsonElement json in invalid.EnumerateArray())
            {
                int input = Int(json, "input");
                Assert.Equal("java.lang.IllegalArgumentException", Str(json, "exceptionType"));
                Assert.Equal("invalid-tile-id", Str(json, "semanticCategory"));

                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TileId(input));
                Assert.Contains("TileId must be in 0..135", ex.Message);
                Assert.False(TileId.TryFromValue(input, out _));
            }
        }

        [Fact]
        public void G002ZoneOwnerSemanticsMapToImmutablePositions()
        {
            JsonElement expected = Sample("G-002").GetProperty("expected");
            JsonElement cases = expected.GetProperty("cases");
            int accepted = 0;
            int rejected = 0;

            foreach (JsonElement json in cases.EnumerateArray())
            {
                string caseId = Str(json, "caseId");
                string zoneName = Str(json, "zone");
                TileZone zone = Zone(zoneName);
                int player = Int(json, "player");
                string outcome = Str(json, "outcome");
                string csharpMapping = Str(json, "csharpMapping");

                if (outcome == "accepted")
                {
                    accepted++;
                    Assert.True(Bool(json, "constructed"), caseId);
                    Assert.Equal(zoneName, Str(json, "zoneOut"));
                    Assert.Equal(player, Int(json, "playerOut"));

                    if (TileZoneRules.RequiresSeat(zone))
                    {
                        TilePosition position = SeatPosition(zone, player);
                        Assert.Equal(zone, position.Zone);
                        Assert.Equal(player, position.Seat);
                        Assert.Equal(0, position.Index);
                    }
                    else
                    {
                        TilePosition position = SeatlessPosition(zone);
                        Assert.Equal(zone, position.Zone);
                        Assert.Equal(-1, position.Seat);
                    }
                }
                else
                {
                    rejected++;
                    Assert.Equal("rejected", outcome);
                    Assert.False(Bool(json, "constructed"), caseId);
                    Assert.Equal("java.lang.IllegalArgumentException", Str(json, "exceptionType"));
                    Assert.False(string.IsNullOrEmpty(Str(json, "message")));

                    if (csharpMapping == "structural-owner-forbidden")
                    {
                        // C# exposes no zone+seat constructor for seatless zones, so the V4
                        // invalid-owner rejection is structurally enforced rather than
                        // exception-identical. Assert the structural invariant, not a fake throw.
                        Assert.False(TileZoneRules.RequiresSeat(zone));
                        Assert.Equal("invalid-zone-owner", Str(json, "semanticCategory"));
                        Assert.Equal(-1, SeatlessPosition(zone).Seat);
                    }
                    else
                    {
                        Assert.Equal("invalid-seat", csharpMapping);
                        Assert.Equal("invalid-player-owner", Str(json, "semanticCategory"));
                        Assert.Throws<ArgumentOutOfRangeException>(() => SeatPosition(zone, player));
                    }
                }
            }

            Assert.Equal(10, accepted);
            Assert.Equal(10, rejected);
            Assert.Equal(accepted, expected.GetProperty("acceptedCount").GetInt32());
            Assert.Equal(rejected, expected.GetProperty("rejectedCount").GetInt32());
            Assert.Contains("structurally enforced", Str(expected, "csharpStructuralNote"));
        }

        [Fact]
        public void G003aExportedWallsDriveDomainMapAndConservationWithoutRngClaim()
        {
            JsonElement expected = Sample("G-003a").GetProperty("expected");
            Assert.Equal(32, expected.GetProperty("seedCount").GetInt32());
            Assert.True(expected.GetProperty("allConserved").GetBoolean());
            Assert.True(expected.GetProperty("allRedFiveThree").GetBoolean());
            Assert.True(expected.GetProperty("allOrderIndependent").GetBoolean());
            Assert.True(expected.GetProperty("allDeadWallIsolated").GetBoolean());
            Assert.Equal(4352, expected.GetProperty("positionsRecorded").GetInt32());

            string rngNote = Str(Sample("G-003a").GetProperty("input"), "randomInputs");
            Assert.Contains("complete", rngNote);
            Assert.Contains("136-tile wall", rngNote);

            JsonElement cases = expected.GetProperty("cases");
            Assert.Equal(32, cases.GetArrayLength());
            int totalPositions = 0;
            foreach (JsonElement json in cases.EnumerateArray())
            {
                int seed = Int(json, "seed");
                int dealer = Int(json, "dealerSeat");
                Assert.Equal(seed % 4, dealer);

                int[] wall = Ints(json.GetProperty("wall"));
                int[] baseWall = Ints(json.GetProperty("baseWall"));
                Assert.Equal(136, wall.Length);
                Assert.Equal(136, baseWall.Length);
                Assert.Equal(136, wall.Distinct().Count());
                Assert.Equal(Enumerable.Range(0, 136), wall.OrderBy(v => v));

                int[] redFiveIds = Ints(json.GetProperty("redFiveTileIds"));
                Assert.Equal(3, redFiveIds.Length);
                Assert.Equal(redFiveIds, wall.Where(v => new TileId(v).IsRedFive).ToArray());
                Assert.Equal(3, Int(json, "redFiveCount"));
                Assert.True(Bool(json, "conserved"));

                var recorded = RecordedRandomDraw.Create(
                    "G-003a-seed-" + seed.ToString(CultureInfo.InvariantCulture),
                    wall.Select(v => new TileId(v)).ToList());
                TilePositionMap map = TilePositionMap.CreateWall(recorded);
                ConservationReport report = TilePositionMap.Validate(map);
                Assert.True(report.IsValid, "seed=" + seed + " " + report);
                Assert.Equal(136, report.AssignedCount);
                Assert.Equal(0, report.UnassignedCount);
                Assert.Equal(14, report.DeadWallCount);
                Assert.Equal(0, report.ReplacementCount);
                Assert.Equal(16, TileStateHash.Format(map.StateHash).Length);

                JsonElement positions = json.GetProperty("positions");
                Assert.Equal(136, positions.GetArrayLength());
                var byTile = new Dictionary<int, (string Zone, int Seat, int Slot)>();
                foreach (JsonElement position in positions.EnumerateArray())
                {
                    int tileId = Int(position, "tileId");
                    Assert.True(byTile.TryAdd(tileId, (Str(position, "zone"), Int(position, "seat"), Int(position, "slot"))));
                }

                Assert.Equal(136, byTile.Count);
                for (int tileId = 0; tileId < 136; tileId++)
                {
                    TilePosition actual = map.PositionOf(new TileId(tileId));
                    (string zone, int seat, int slot) = byTile[tileId];
                    Assert.Equal(zone == "WALL" ? TileZone.Wall : TileZone.DeadWall, actual.Zone);
                    Assert.Equal(seat, actual.Seat);
                    // V4 records the absolute wall slot 0..135; C# DeadWall slots are 0..13.
                    Assert.Equal(zone == "WALL" ? slot : slot - 122, actual.Index);
                }

                Assert.Equal(Str(json, "wallCanonicalSha256"), Sha256Text(WallDigestText(seed, wall)));
                TilePositionMap rebuilt = TilePositionMap.CreateWall(RecordedRandomDraw.Create(
                    "G-003a-rebuild-" + seed.ToString(CultureInfo.InvariantCulture),
                    wall.Select(v => new TileId(v)).ToList()));
                Assert.Equal(map.StateHash, rebuilt.StateHash);
                for (int tileId = 0; tileId < 136; tileId++)
                {
                    Assert.Equal(map.PositionOf(new TileId(tileId)), rebuilt.PositionOf(new TileId(tileId)));
                }

                Assert.True(Bool(json, "deadWallSegmentEqual"));
                Assert.True(Bool(json, "reversedEquals"));
                for (int slot = 122; slot < 136; slot++)
                {
                    Assert.Equal(baseWall[slot], wall[slot]);
                }

                totalPositions += positions.GetArrayLength();
            }

            Assert.Equal(4352, totalPositions);
        }

        [Fact]
        public void G003bStandaloneDeletionProbeIsExportedAndCsharpRejectsMissingPlacements()
        {
            JsonElement probe = Sample("G-003b").GetProperty("expected").GetProperty("probeA");
            Assert.Equal("rejected", Str(probe.GetProperty("firstAudit"), "outcome"));
            Assert.Equal("java.lang.IllegalArgumentException", Str(probe.GetProperty("firstAudit"), "exceptionType"));
            Assert.Equal("pool-size", Str(probe.GetProperty("firstAudit"), "semanticCategory"));
            Assert.Contains("exactly 136", Str(probe.GetProperty("firstAudit"), "message"));
            Assert.Contains("135", Str(probe.GetProperty("firstAudit"), "message"));
            Assert.Equal(135, Int(probe.GetProperty("mutation"), "mapSizeAfter"));
            Assert.Equal(135, Int(probe.GetProperty("after"), "poolSize"));
            Assert.Equal(135, Int(probe.GetProperty("after"), "mapSize"));
            Assert.Equal(JsonValueKind.Null, probe.GetProperty("after").GetProperty("location").ValueKind);
            Assert.Equal(new[] { 0 }, Ints(probe.GetProperty("after").GetProperty("missingTileIds")));
            Assert.False(Bool(probe, "autoRepairObserved"));
            Assert.Equal("rejected", Str(probe.GetProperty("secondAudit"), "outcome"));
            Assert.Equal(
                Str(probe.GetProperty("firstAudit"), "message"),
                Str(probe.GetProperty("secondAudit"), "message"));

            var missing = new List<TilePlacement>();
            for (int i = 0; i < 135; i++)
            {
                missing.Add(new TilePlacement(new TileId(i), SlotPosition(i)));
            }

            ArgumentException missingEx = Assert.Throws<ArgumentException>(
                () => TilePositionMap.FromPlacements(missing, 0, 122, 0));
            Assert.Contains("exactly 136", missingEx.Message);

            var duplicateTile = new List<TilePlacement>();
            for (int i = 0; i < 136; i++)
            {
                duplicateTile.Add(new TilePlacement(new TileId(i), SlotPosition(i)));
            }

            duplicateTile[135] = new TilePlacement(new TileId(0), SlotPosition(135));
            ArgumentException duplicateTileEx = Assert.Throws<ArgumentException>(
                () => TilePositionMap.FromPlacements(duplicateTile, 0, 122, 0));
            Assert.Contains("Duplicate physical tile", duplicateTileEx.Message);

            var duplicatePosition = new List<TilePlacement>();
            for (int i = 0; i < 136; i++)
            {
                duplicatePosition.Add(new TilePlacement(new TileId(i), SlotPosition(i)));
            }

            duplicatePosition[1] = new TilePlacement(new TileId(1), TilePosition.Wall(0));
            ArgumentException duplicatePositionEx = Assert.Throws<ArgumentException>(
                () => TilePositionMap.FromPlacements(duplicatePosition, 0, 122, 0));
            Assert.Contains("occupied by both", duplicatePositionEx.Message);

            TilePositionMap map = TilePositionMap.CreateWall(RecordedRandomDraw.Standard("g003b-readonly"));
            IList<TilePlacement> view = (IList<TilePlacement>)map.AsPlacements();
            ulong before = map.StateHash;
            Assert.Throws<NotSupportedException>(
                () => view.Add(new TilePlacement(new TileId(0), TilePosition.Wall(0))));
            Assert.Equal(before, map.StateHash);
        }

        [Fact]
        public void G003bBoundLedgerMismatchProbeIsExportedAndCsharpMapIsSingleLedger()
        {
            JsonElement probe = Sample("G-003b").GetProperty("expected").GetProperty("probeB");
            Assert.Equal("accepted", Str(probe.GetProperty("before"), "auditOutcome"));
            Assert.Equal("rejected", Str(probe.GetProperty("firstAudit"), "outcome"));
            Assert.Equal("java.lang.IllegalStateException", Str(probe.GetProperty("firstAudit"), "exceptionType"));
            Assert.Equal("pool-ledger-mismatch", Str(probe.GetProperty("firstAudit"), "semanticCategory"));
            Assert.Contains("TilePool audit failed", Str(probe.GetProperty("firstAudit"), "message"));
            Assert.Equal("WALL", Str(probe.GetProperty("after"), "poolZone"));
            Assert.Equal(-1, Int(probe.GetProperty("after"), "poolPlayer"));
            Assert.True(Bool(probe.GetProperty("after"), "closedTilesStillContains"));
            Assert.Equal(136, Int(probe.GetProperty("after"), "poolSize"));
            Assert.Equal(136, Int(probe.GetProperty("after"), "actualTilesCount"));
            Assert.Equal(
                Str(probe.GetProperty("before"), "actualTilesSha256"),
                Str(probe.GetProperty("after"), "actualTilesSha256"));
            Assert.False(Bool(probe, "autoRepairObserved"));
            Assert.Equal("rejected", Str(probe.GetProperty("secondAudit"), "outcome"));
            Assert.Equal(
                Str(probe.GetProperty("firstAudit"), "message"),
                Str(probe.GetProperty("secondAudit"), "message"));

            JsonElement mapping = probe.GetProperty("csharpMapping");
            Assert.Equal("structural-immutable-map-no-separate-ledger", Str(mapping, "status"));
            Assert.Contains("read-only", Str(mapping, "note"));

            TilePositionMap map = TilePositionMap.CreateWall(RecordedRandomDraw.Standard("g003b-single-ledger"));
            Assert.True(TilePositionMap.Validate(map).IsValid);
            TilePositionMap rebuilt = TilePositionMap.FromPlacements(
                map.AsPlacements(),
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer);
            Assert.Equal(map.StateHash, rebuilt.StateHash);
            for (int tileId = 0; tileId < 136; tileId++)
            {
                Assert.Equal(map.PositionOf(new TileId(tileId)), rebuilt.PositionOf(new TileId(tileId)));
            }

            Assert.Throws<NotSupportedException>(
                () => ((IList<TilePlacement>)map.AsPlacements()).Add(
                    new TilePlacement(new TileId(0), TilePosition.Wall(0))));
        }

        [Fact]
        public void G003bDuplicateKeyBranchIsDocumentedAsNotConstructible()
        {
            JsonElement limitation = Sample("G-003b").GetProperty("expected").GetProperty("duplicateKeyStaticLimitation");
            Assert.Equal("NOT_CONSTRUCTIBLE", Str(limitation, "status"));
            Assert.Equal(1, Int(limitation, "observedMapSize"));
            Assert.Equal("unreachable", Str(limitation, "auditBranchReachability"));
            Assert.Contains("overwrites", Str(limitation, "reason"));
            Assert.Contains("G-010", Str(limitation, "reason"));
            Assert.Equal("WALL", Str(limitation.GetProperty("firstValue"), "zone"));
            Assert.Equal("HAND", Str(limitation.GetProperty("finalValue"), "zone"));
        }

        private static JsonElement Sample(string sampleId)
        {
            foreach (JsonElement sample in Golden.Value.RootElement.GetProperty("samples").EnumerateArray())
            {
                if (Str(sample, "sampleId") == sampleId)
                {
                    return sample;
                }
            }

            throw new InvalidOperationException("golden sample missing: " + sampleId);
        }

        private static JsonDocument LoadGolden()
        {
            return JsonDocument.Parse(File.ReadAllText(FindGoldenPath()));
        }

        private static string FindGoldenPath([CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(overrideRoot!, "tests", "v5-golden", "physical-v1.json");
                if (File.Exists(overridden))
                {
                    return overridden;
                }
            }

            foreach (string start in new[]
                     {
                         AppContext.BaseDirectory,
                         Directory.GetCurrentDirectory(),
                         Path.GetDirectoryName(sourceFile) ?? string.Empty
                     })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(directory.FullName, "tests", "v5-golden", "physical-v1.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException(
                "tests/v5-golden/physical-v1.json was not found from " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() +
                "; set MAHJONG_REPO_ROOT if the test binaries live outside the repository.");
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(FindGoldenPath())!);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "dotnet")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("repository root not found from golden path");
        }

        private static TileSuit Suit(string name)
        {
            switch (name)
            {
                case "MAN": return TileSuit.Man;
                case "PIN": return TileSuit.Pin;
                case "SOU": return TileSuit.Sou;
                case "WIND": return TileSuit.Wind;
                case "DRAGON": return TileSuit.Dragon;
                default: throw new InvalidOperationException("unknown V4 suit " + name);
            }
        }

        private static TileZone Zone(string name)
        {
            switch (name)
            {
                case "WALL": return TileZone.Wall;
                case "DEAD_WALL": return TileZone.DeadWall;
                case "UNASSIGNED": return TileZone.Unassigned;
                case "HAND": return TileZone.Hand;
                case "DISCARD": return TileZone.Discard;
                case "MELD": return TileZone.Meld;
                case "SLEEVE": return TileZone.Sleeve;
                default: throw new InvalidOperationException("unknown V4 zone " + name);
            }
        }

        private static TilePosition SeatPosition(TileZone zone, int seat)
        {
            switch (zone)
            {
                case TileZone.Hand: return TilePosition.Hand(seat, 0);
                case TileZone.Discard: return TilePosition.Discard(seat, 0);
                case TileZone.Meld: return TilePosition.Meld(seat, 0);
                case TileZone.Sleeve: return TilePosition.Sleeve(seat, 0);
                default: throw new InvalidOperationException("zone does not require a seat: " + zone);
            }
        }

        private static TilePosition SeatlessPosition(TileZone zone)
        {
            switch (zone)
            {
                case TileZone.Wall: return TilePosition.Wall(0);
                case TileZone.DeadWall: return TilePosition.DeadWall(0);
                case TileZone.Unassigned: return TilePosition.Unassigned;
                default: throw new InvalidOperationException("zone requires a seat: " + zone);
            }
        }

        /// <summary>V4 absolute wall slot 0..135 -> C# Wall 0..121 / DeadWall 0..13.</summary>
        private static TilePosition SlotPosition(int slot)
        {
            if (slot < 0 || slot > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "V4 wall slot must be 0..135.");
            }

            return slot < 122 ? TilePosition.Wall(slot) : TilePosition.DeadWall(slot - 122);
        }

        private static string WallDigestText(int seed, int[] wall)
        {
            return "G-003a|" + seed.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", wall);
        }

        private static string Sha256SourceFile(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            return Sha256Bytes(Encoding.UTF8.GetBytes(text));
        }

        private static string Sha256Text(string text)
        {
            return Sha256Bytes(Encoding.UTF8.GetBytes(text));
        }

        private static string Sha256Bytes(byte[] bytes)
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
    }
}

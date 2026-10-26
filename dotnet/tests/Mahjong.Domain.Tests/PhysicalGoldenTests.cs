using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// 字段消费口径（详见交付报告）：
    /// - IN/OUT：fixture 值直接驱动真实入口，并与真实返回值逐字段比对。
    /// - INTERNAL：仅用于构造/聚合校验，跨语言不可比或不可由导出字段重算。
    /// - META：provenance/摘要/说明文字，只做结构与来源锚点断言，不冒充行为等价。
    /// - BLOCKED：V4 mutable TilePool 与 C# immutable TilePositionMap 的结构差异；
    ///   不发明可变 API，只验证 C# 可构造的不可变不变量。
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
            Assert.Equal(0, root.GetProperty("trackedModifiedFiles").GetArrayLength());
            Assert.Equal(0, root.GetProperty("trackedProductionModifiedFiles").GetArrayLength());

            Assert.Equal(
                "engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt",
                Str(root, "generator"));
            Assert.Contains("V5_PHYSICAL_GOLDEN_EXPORT", Str(root, "generatedByCommand"));
            Assert.Contains("V5PhysicalGoldenExporterTest", Str(root, "generatedByCommand"));
            Assert.Contains("No implicit random or clock input", Str(root, "randomInputsNote"));
            Assert.Contains("seeds 0..31", Str(root, "randomInputsNote"));
            Assert.Contains("generation-time snapshot", Str(root, "v4RulesSourceNote"));
            Assert.Contains("trackedModifiedFiles is empty", Str(root, "v4RulesSourceNote"));

            JsonElement taskPaths = root.GetProperty("taskPaths");
            Assert.Equal(4, taskPaths.GetArrayLength());
            string[] tasks = taskPaths.EnumerateArray().Select(v => v.GetString()!).ToArray();
            Assert.Contains("engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt", tasks);
            Assert.Contains("tests/v5-golden/physical-v1.json", tasks);
            Assert.Contains("dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs", tasks);
            Assert.Contains(tasks, t => t.EndsWith("V5-P11-PHYSICAL-GOLDEN.md", StringComparison.Ordinal));

            // worktreeDirty is a generation-time provenance snapshot: it must agree with
            // the recorded porcelain list, not with the current worktree.
            JsonElement status = root.GetProperty("gitStatusPorcelain");
            Assert.Equal(status.GetArrayLength() > 0, root.GetProperty("worktreeDirty").GetBoolean());
            foreach (JsonElement entry in status.EnumerateArray())
            {
                Assert.StartsWith("?? ", entry.GetString());
            }

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
            Assert.Equal(
                new[] { "G-001a", "G-001b", "G-001c", "G-002", "G-003a", "G-003b" },
                scope.GetProperty("targetIds").EnumerateArray().Select(v => v.GetString()!).ToArray());

            string[] expectedIds = { "G-001a", "G-001b", "G-001c", "G-002", "G-003a", "G-003b" };
            JsonElement samples = root.GetProperty("samples");
            Assert.Equal(expectedIds.Length, samples.GetArrayLength());
            var actualIds = new List<string>();
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                string sampleId = Str(sample, "sampleId");
                actualIds.Add(sampleId);
                Assert.Equal("exported", Str(sample, "extractionStatus"));
                Assert.Equal("not-applicable", Str(sample.GetProperty("input"), "clockMillis"));
                Assert.Equal("not-applicable", Str(sample.GetProperty("input"), "sessionId"));
                Assert.Contains("V5PhysicalGoldenExporterTest", Str(sample, "command"));
                Assert.False(string.IsNullOrWhiteSpace(Str(sample.GetProperty("source"), "file")));
                Assert.False(string.IsNullOrWhiteSpace(Str(sample.GetProperty("source"), "testMethod")));
                AssertSampleMetadata(sample, sampleId);
            }

            Assert.Equal(expectedIds, actualIds);

            // hashCrosswalk is META, not behavior equivalence: V4 has no cross-language
            // canonical state hash. The C# hash is a V5-only FNV-1a definition and the
            // fixture deliberately stores no C# hash value (that would be circular).
            JsonElement hash = root.GetProperty("hashCrosswalk");
            AssertExactKeys(
                hash,
                "v4CanonicalStateHash",
                "v4Reason",
                "csharpHash",
                "wallCanonicalDigestAlgorithm",
                "csharpHashVerification");
            Assert.Equal("NOT_AVAILABLE", Str(hash, "v4CanonicalStateHash"));
            string v4Reason = Str(hash, "v4Reason");
            Assert.Contains("no cross-language canonical state hash", v4Reason);
            Assert.Contains("StateInvariants", v4Reason);
            Assert.Contains("No V4 internal digest", v4Reason);
            Assert.Contains(
                "engine/src/main/kotlin/com/majiang/state/StateInvariants.kt",
                root.GetProperty("v4SourceHashes").EnumerateArray().Select(v => Str(v, "file")).ToArray());

            string csharpHash = Str(hash, "csharpHash");
            Assert.Contains("TileStateHash", csharpHash);
            Assert.Contains("encoding v3", csharpHash);
            Assert.Contains("FNV-1a", csharpHash);
            Assert.Contains("not a V4 digest", csharpHash);
            Assert.Equal(3, TileStateHash.EncodingVersion);

            string verification = Str(hash, "csharpHashVerification");
            Assert.Contains("no C# hash is stored", verification);
            Assert.Contains("circular", verification);

            // The META claim "no C# hash is stored" is checked structurally: no value in
            // hashCrosswalk is a bare 16-hex C# state hash.
            foreach (JsonProperty property in hash.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    Assert.False(
                        Regex.IsMatch(property.Value.GetString()!, "^[0-9a-f]{16}$"),
                        "hashCrosswalk unexpectedly embeds a 16-hex C# hash in " + property.Name);
                }
            }

            // Behavior anchor for csharpHashVerification: rebuilding the same physical
            // map is stable; this is a V5/C# property, not a V4 digest claim.
            TilePositionMap first = TilePositionMap.CreateWall(RecordedRandomDraw.Standard("hash-crosswalk-stability"));
            TilePositionMap second = TilePositionMap.CreateWall(RecordedRandomDraw.Standard("hash-crosswalk-stability"));
            Assert.Equal(first.StateHash, second.StateHash);
            string formatted = TileStateHash.Format(first.StateHash);
            Assert.Equal(16, formatted.Length);
            Assert.True(formatted.All(Uri.IsHexDigit));
            Assert.Equal(formatted.ToLowerInvariant(), formatted);

            string wallAlgorithm = Str(hash, "wallCanonicalDigestAlgorithm");
            Assert.Contains("SHA-256", wallAlgorithm);
            Assert.Contains("G-003a|", wallAlgorithm);
            Assert.Contains("136 tileIds", wallAlgorithm);
            Assert.Contains("joined by ','", wallAlgorithm);
            JsonElement digestCase = Sample("G-003a").GetProperty("expected").GetProperty("cases")[0];
            Assert.Equal(
                Str(digestCase, "wallCanonicalSha256"),
                Sha256Text(WallDigestText(Int(digestCase, "seed"), Ints(digestCase.GetProperty("wall")))));
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
            Assert.Equal(Int(expected, "size"), tiles.GetArrayLength());
            var redFiveIds = new List<int>();
            var distinctIds = new HashSet<int>();
            for (int i = 0; i < tiles.GetArrayLength(); i++)
            {
                JsonElement json = tiles[i];
                int id = Int(json, "tileId");
                Assert.Equal(i, id);
                Assert.True(distinctIds.Add(id), "duplicate tileId " + id);

                TileId tile = new TileId(id);
                AssertTileSemantics(json, tile);
                Assert.Equal(tile, TileId.All[i]);
                Assert.Equal(id, TileId.All[i].Value);

                if (tile.IsRedFive)
                {
                    redFiveIds.Add(id);
                }
            }

            Assert.Equal(Int(expected, "size"), distinctIds.Count);
            Assert.Equal(Int(expected, "distinctIdentities"), distinctIds.Count);
            Assert.Equal(Int(expected, "redFiveCount"), redFiveIds.Count);
            Assert.Equal(Int(expected, "nonRedFiveCount"), tiles.GetArrayLength() - redFiveIds.Count);
            Assert.Equal(
                Bool(expected, "standardSetOrderIsTileIdOrder"),
                tiles.EnumerateArray().Select(v => Int(v, "tileId")).SequenceEqual(Enumerable.Range(0, 136)));
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
                int copyIndex = Int(json, "copyIndex");
                Assert.Equal(boundaryIndexes[i], typeIndex);
                Assert.Equal(0, copyIndex);

                // Same input through the real entry point: FromTypeIndex(typeIndex, copyIndex).
                // All eleven exported tile-semantic fields are compared, not only tileId.
                TileId tile = TileId.FromTypeIndex(typeIndex, copyIndex);
                AssertTileSemantics(json, tile);
                Assert.Equal(typeIndex * 4 + copyIndex, tile.Value);
            }

            JsonElement groups = expected.GetProperty("groups");
            Assert.Equal(34, groups.GetArrayLength());
            var seenTypes = new HashSet<int>();
            var groupSuitByType = new Dictionary<int, string>();
            foreach (JsonElement group in groups.EnumerateArray())
            {
                int typeIndex = Int(group, "typeIndex");
                Assert.InRange(typeIndex, 0, 33);
                Assert.True(seenTypes.Add(typeIndex), "duplicate typeIndex " + typeIndex);
                int[] tileIds = Ints(group.GetProperty("tileIds"));
                Assert.Equal(4, tileIds.Length);
                Assert.Equal(Int(group, "count"), tileIds.Length);
                Assert.Equal(
                    TileId.All.Where(t => t.TypeIndex == typeIndex).Select(t => t.Value).ToArray(),
                    tileIds);

                TileId first = new TileId(tileIds[0]);
                Assert.Equal(typeIndex, first.TypeIndex);
                Assert.Equal(0, first.CopyIndex);
                Assert.Equal(Suit(Str(group, "suit")), first.Suit);
                Assert.Equal(Int(group, "rank"), first.Rank);
                groupSuitByType.Add(typeIndex, Str(group, "suit"));

                // The exported formula is behavior, not a label: it must hold for each of
                // the four physical copies.
                for (int copy = 0; copy < 4; copy++)
                {
                    TileId tile = new TileId(tileIds[copy]);
                    Assert.Equal(typeIndex, tile.TypeIndex);
                    Assert.Equal(copy, tile.CopyIndex);
                    Assert.Equal(typeIndex * 4 + copy, tile.Value);
                    Assert.Equal(first.Suit, tile.Suit);
                    Assert.Equal(first.Rank, tile.Rank);
                }
            }

            Assert.Equal(34, seenTypes.Count);

            // suitOrder is a V4 META string, but it is fully checkable against the real
            // C# suit mapping and the exported 34 groups: contiguous 0..33 coverage.
            AssertSuitOrder(Str(expected, "suitOrder"), groupSuitByType);
        }

        [Fact]
        public void G001cTileIdRoundTripAndIllegalInputsMatch()
        {
            JsonElement expected = Sample("G-001c").GetProperty("expected");
            Assert.Equal(136, expected.GetProperty("validCount").GetInt32());
            Assert.True(expected.GetProperty("allRoundTripEqual").GetBoolean());

            JsonElement valid = expected.GetProperty("validRoundTrips");
            Assert.Equal(136, valid.GetArrayLength());
            Assert.Equal(Int(expected, "validCount"), valid.GetArrayLength());
            bool allEqual = true;
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
                allEqual &= tile == fromType && tile == parsed;
            }

            Assert.Equal(Bool(expected, "allRoundTripEqual"), allEqual);

            // G-001c boundaries: every exported semantic field is derived twice from the
            // real entry points new TileId(id) and FromTypeIndex(typeIndex, copyIndex),
            // and TryFromValue(id) must reproduce the same physical identity.
            JsonElement boundaries = expected.GetProperty("boundaries");
            Assert.Equal(2, boundaries.GetArrayLength());
            Assert.Equal(0, Int(boundaries[0], "tileId"));
            Assert.Equal(135, Int(boundaries[1], "tileId"));
            for (int i = 0; i < boundaries.GetArrayLength(); i++)
            {
                JsonElement json = boundaries[i];
                int id = Int(json, "tileId");
                TileId tile = new TileId(id);
                AssertTileSemantics(json, tile);
                Assert.Equal(id, tile.Value);

                TileId fromType = TileId.FromTypeIndex(Int(json, "typeIndex"), Int(json, "copyIndex"));
                AssertTileSemantics(json, fromType);
                Assert.Equal(id, fromType.Value);

                Assert.True(TileId.TryFromValue(id, out TileId parsed));
                AssertTileSemantics(json, parsed);
                Assert.Equal(tile, parsed);
            }

            Assert.Equal(TileId.FromTypeIndex(33, 3), new TileId(135));

            JsonElement invalid = expected.GetProperty("invalid");
            Assert.Equal(4, invalid.GetArrayLength());
            Assert.Equal(
                new[] { -1, 136, int.MinValue, int.MaxValue },
                invalid.EnumerateArray().Select(v => Int(v, "input")).ToArray());
            var actualExceptionTypes = new HashSet<string>();
            var actualCategories = new HashSet<string>();
            Type? actualCsharpType = null;
            foreach (JsonElement json in invalid.EnumerateArray())
            {
                ArgumentOutOfRangeException ex = AssertInvalidTileId(json);
                actualExceptionTypes.Add(ex.GetType().FullName!);
                actualCategories.Add(Str(json, "semanticCategory"));
                actualCsharpType = ex.GetType();
            }

            Assert.Single(actualExceptionTypes);
            Assert.Single(actualCategories);
            Assert.Equal("System.ArgumentOutOfRangeException", actualExceptionTypes.Single());

            // csharpExceptionMapping must be derived from the real thrown exception type
            // (FullName) and the real semantic category, with the V4 type coming from the
            // exported invalid entries. It must not be a format-only string assertion.
            JsonElement mapping = expected.GetProperty("csharpExceptionMapping");
            AssertExactKeys(mapping, "v4ExceptionType", "csharpExceptionType", "semanticCategory");
            Assert.Equal(Str(invalid[0], "exceptionType"), Str(mapping, "v4ExceptionType"));
            Assert.Equal(actualExceptionTypes.Single(), Str(mapping, "csharpExceptionType"));
            Assert.Equal(actualCategories.Single(), Str(mapping, "semanticCategory"));
            Assert.Equal("java.lang.IllegalArgumentException", Str(mapping, "v4ExceptionType"));
            Assert.Equal("invalid-tile-id", Str(mapping, "semanticCategory"));
            Assert.NotNull(actualCsharpType);
            Assert.Equal(Str(mapping, "csharpExceptionType"), actualCsharpType!.FullName);
            Assert.NotNull(Type.GetType(Str(mapping, "csharpExceptionType"), throwOnError: false));

            // Source anchor: Tile.kt:112-120 really contains the V4 require that throws
            // IllegalArgumentException; the fixture's v4SourceHashes pins that file.
            AssertG001cExceptionMappingSource(Sample("G-001c"));
        }

        [Fact]
        public void G002ZoneOwnerSemanticsMapToImmutablePositions()
        {
            JsonElement expected = Sample("G-002").GetProperty("expected");
            JsonElement cases = expected.GetProperty("cases");
            string[] expectedCaseIds =
            {
                "loc-wall-default",
                "loc-wall-owner-0",
                "loc-wall-owner-3",
                "loc-deadwall-default",
                "loc-deadwall-owner-0",
                "loc-unassigned-default",
                "loc-unassigned-owner-0",
                "loc-hand-0",
                "loc-hand-3",
                "loc-hand-minus1",
                "loc-hand-4",
                "loc-discard-0",
                "loc-discard-3",
                "loc-discard-minus1",
                "loc-discard-4",
                "loc-meld-0",
                "loc-meld-3",
                "loc-meld-minus1",
                "loc-sleeve-1",
                "loc-sleeve-4"
            };
            Assert.Equal(
                expectedCaseIds.OrderBy(v => v, StringComparer.Ordinal),
                cases.EnumerateArray().Select(v => Str(v, "caseId")).OrderBy(v => v, StringComparer.Ordinal));
            int accepted = 0;
            int rejected = 0;
            int invalidZoneOwner = 0;
            int invalidPlayerOwner = 0;
            int seatFactoryAccepted = 0;
            int seatlessFactoryAccepted = 0;

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

                    // The mapping is a real branch selector, not a label: a seatless
                    // factory must be used exactly when TileZoneRules.RequiresSeat is
                    // false, and vice versa. Unknown values and a legal enum that names
                    // the wrong branch are both rejected here.
                    bool requiresSeat = TileZoneRules.RequiresSeat(zone);
                    string expectedMapping = requiresSeat ? "seat-factory" : "seatless-factory";
                    Assert.Equal(expectedMapping, csharpMapping);
                    Assert.Equal(requiresSeat, player >= 0);

                    if (requiresSeat)
                    {
                        seatFactoryAccepted++;
                        TilePosition position = SeatPosition(zone, player);
                        Assert.Equal(zone, position.Zone);
                        Assert.Equal(player, position.Seat);
                        Assert.Equal(0, position.Index);
                    }
                    else
                    {
                        seatlessFactoryAccepted++;
                        TilePosition position = SeatlessPosition(zone);
                        Assert.Equal(zone, position.Zone);
                        Assert.Equal(-1, position.Seat);
                        Assert.Equal(zone == TileZone.Unassigned ? -1 : 0, position.Index);
                    }
                }
                else
                {
                    rejected++;
                    Assert.Equal("rejected", outcome);
                    Assert.False(Bool(json, "constructed"), caseId);
                    Assert.Equal("java.lang.IllegalArgumentException", Str(json, "exceptionType"));
                    Assert.True(
                        csharpMapping == "structural-owner-forbidden" || csharpMapping == "invalid-seat",
                        caseId + " has unknown rejected csharpMapping " + csharpMapping);
                    AssertV4ZoneOwnerMessage(json, zoneName, player, csharpMapping);

                    if (csharpMapping == "structural-owner-forbidden")
                    {
                        invalidZoneOwner++;
                        // C# exposes no zone+seat constructor for seatless zones, so the V4
                        // invalid-owner rejection is structurally enforced rather than
                        // exception-identical. Assert the structural invariant, not a fake throw.
                        Assert.False(TileZoneRules.RequiresSeat(zone));
                        Assert.Equal("invalid-zone-owner", Str(json, "semanticCategory"));
                        Assert.Equal(-1, SeatlessPosition(zone).Seat);
                        Assert.Empty(typeof(TilePosition).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
                    }
                    else
                    {
                        invalidPlayerOwner++;
                        Assert.Equal("invalid-seat", csharpMapping);
                        Assert.Equal("invalid-player-owner", Str(json, "semanticCategory"));
                        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                            () => SeatPosition(zone, player));
                        Assert.Equal("seat", ex.ParamName);
                        Assert.Equal(player, Assert.IsType<int>(ex.ActualValue));
                        Assert.Contains(zone.ToString(), ex.Message);
                    }
                }
            }

            Assert.Equal(10, accepted);
            Assert.Equal(10, rejected);
            Assert.Equal(4, invalidZoneOwner);
            Assert.Equal(6, invalidPlayerOwner);
            Assert.Equal(3, seatlessFactoryAccepted);
            Assert.Equal(7, seatFactoryAccepted);
            Assert.Equal(accepted, expected.GetProperty("acceptedCount").GetInt32());
            Assert.Equal(rejected, expected.GetProperty("rejectedCount").GetInt32());
            string structuralNote = Str(expected, "csharpStructuralNote");
            Assert.Contains("has no zone+seat constructor", structuralNote);
            Assert.Contains("structurally enforced", structuralNote);
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
            Assert.Equal(Int(expected, "seedCount"), cases.GetArrayLength());
            int totalPositions = 0;
            var seenSeeds = new HashSet<int>();
            bool allConserved = true;
            bool allRedFiveThree = true;
            bool allOrderIndependent = true;
            bool allDeadWallIsolated = true;
            foreach (JsonElement json in cases.EnumerateArray())
            {
                int seed = Int(json, "seed");
                int dealer = Int(json, "dealerSeat");
                Assert.InRange(seed, 0, 31);
                Assert.True(seenSeeds.Add(seed), "duplicate seed " + seed);
                Assert.Equal(seed % 4, dealer);

                int[] wall = Ints(json.GetProperty("wall"));
                int[] baseWall = Ints(json.GetProperty("baseWall"));
                Assert.Equal(136, wall.Length);
                Assert.Equal(136, baseWall.Length);
                Assert.Equal(136, wall.Distinct().Count());
                Assert.Equal(136, baseWall.Distinct().Count());
                Assert.Equal(Enumerable.Range(0, 136), wall.OrderBy(v => v));
                Assert.Equal(Enumerable.Range(0, 136), baseWall.OrderBy(v => v));

                int[] redFiveIds = Ints(json.GetProperty("redFiveTileIds"));
                Assert.Equal(3, redFiveIds.Length);
                Assert.Equal(redFiveIds, wall.Where(v => new TileId(v).IsRedFive).ToArray());
                Assert.Equal(3, Int(json, "redFiveCount"));
                Assert.True(Bool(json, "conserved"));
                allConserved &= Bool(json, "conserved");
                allRedFiveThree &= Int(json, "redFiveCount") == 3 && redFiveIds.Length == 3;

                // deadWallSegmentEqual is recomputable from the exported baseWall/wall.
                bool deadWallEqual = wall.Skip(122).SequenceEqual(baseWall.Skip(122));
                Assert.Equal(Bool(json, "deadWallSegmentEqual"), deadWallEqual);
                allDeadWallIsolated &= deadWallEqual;

                // reversedEquals is V4 plan-order metadata: the reversed-plan wall is not
                // exported, so C# cannot recompute it. Assert the exported boolean itself
                // and the aggregate, and do not claim to replay V4's plan reversal.
                Assert.True(Bool(json, "reversedEquals"));
                allOrderIndependent &= Bool(json, "reversedEquals");

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
                var seenPositionTiles = new HashSet<int>();
                for (int slot = 0; slot < positions.GetArrayLength(); slot++)
                {
                    JsonElement position = positions[slot];
                    int tileId = Int(position, "tileId");
                    Assert.Equal(wall[slot], tileId);
                    Assert.Equal(slot, Int(position, "slot"));
                    Assert.True(seenPositionTiles.Add(tileId), "duplicate position tileId " + tileId);
                    Assert.Equal(-1, Int(position, "seat"));
                    string zone = Str(position, "zone");
                    Assert.Equal(slot < 122 ? "WALL" : "DEAD_WALL", zone);
                    Assert.True(byTile.TryAdd(tileId, (zone, Int(position, "seat"), Int(position, "slot"))));
                }

                Assert.Equal(136, byTile.Count);
                Assert.Equal(136, seenPositionTiles.Count);
                for (int tileId = 0; tileId < 136; tileId++)
                {
                    TilePosition actual = map.PositionOf(new TileId(tileId));
                    (string zone, int seat, int slot) = byTile[tileId];
                    Assert.Equal(zone == "WALL" ? TileZone.Wall : TileZone.DeadWall, actual.Zone);
                    Assert.Equal(seat, actual.Seat);
                    // V4 records the absolute wall slot 0..135; C# DeadWall slots are 0..13.
                    Assert.Equal(zone == "WALL" ? slot : slot - 122, actual.Index);
                }

                string digest = Str(json, "wallCanonicalSha256");
                Assert.Matches("^[0-9a-f]{64}$", digest);
                Assert.Equal(digest, Sha256Text(WallDigestText(seed, wall)));
                TilePositionMap rebuilt = TilePositionMap.CreateWall(RecordedRandomDraw.Create(
                    "G-003a-rebuild-" + seed.ToString(CultureInfo.InvariantCulture),
                    wall.Select(v => new TileId(v)).ToList()));
                Assert.Equal(map.StateHash, rebuilt.StateHash);
                for (int tileId = 0; tileId < 136; tileId++)
                {
                    Assert.Equal(map.PositionOf(new TileId(tileId)), rebuilt.PositionOf(new TileId(tileId)));
                }

                for (int slot = 122; slot < 136; slot++)
                {
                    Assert.Equal(baseWall[slot], wall[slot]);
                }

                totalPositions += positions.GetArrayLength();
            }

            Assert.Equal(32, seenSeeds.Count);
            Assert.Equal(Bool(expected, "allConserved"), allConserved);
            Assert.Equal(Bool(expected, "allRedFiveThree"), allRedFiveThree);
            Assert.Equal(Bool(expected, "allOrderIndependent"), allOrderIndependent);
            Assert.Equal(Bool(expected, "allDeadWallIsolated"), allDeadWallIsolated);
            Assert.Equal(Int(expected, "positionsRecorded"), totalPositions);
            Assert.Equal(4352, totalPositions);
        }

        [Fact]
        public void G003bStandaloneDeletionProbeIsExportedAndCsharpRejectsMissingPlacements()
        {
            JsonElement probe = Sample("G-003b").GetProperty("expected").GetProperty("probeA");
            Assert.Contains("TilePool", Str(probe, "fixture"));
            Assert.Contains("tileMap", Str(probe, "fixture"));
            AssertTileSemantics(probe.GetProperty("target"), new TileId(0));

            JsonElement before = probe.GetProperty("before");
            Assert.Equal(136, Int(before, "poolSize"));
            Assert.Equal(136, Int(before, "mapSize"));
            Assert.Equal("UNASSIGNED", Str(before.GetProperty("location"), "zone"));
            Assert.Equal(-1, Int(before.GetProperty("location"), "player"));

            JsonElement mutation = probe.GetProperty("mutation");
            Assert.Contains("setAccessible", Str(mutation, "via"));
            Assert.True(Bool(mutation, "removed"));
            Assert.Equal(136, Int(mutation, "mapSizeBefore"));
            Assert.Equal(135, Int(mutation, "mapSizeAfter"));

            AssertG003bAudit(
                probe.GetProperty("firstAudit"),
                "rejected",
                "java.lang.IllegalArgumentException",
                "pool-size",
                "Pool must contain exactly 136 tiles, but has 135");

            JsonElement after = probe.GetProperty("after");
            Assert.Equal(135, Int(after, "poolSize"));
            Assert.Equal(135, Int(after, "mapSize"));
            Assert.Equal(Int(mutation, "mapSizeAfter"), Int(after, "mapSize"));
            Assert.Equal(JsonValueKind.Null, after.GetProperty("location").ValueKind);
            Assert.Equal(new[] { 0 }, Ints(after.GetProperty("missingTileIds")));
            Assert.Equal(
                new[] { Int(probe.GetProperty("target"), "tileId") },
                Ints(after.GetProperty("missingTileIds")));
            Assert.False(Bool(probe, "autoRepairObserved"));
            AssertG003bAudit(
                probe.GetProperty("secondAudit"),
                "rejected",
                "java.lang.IllegalArgumentException",
                "pool-size",
                "Pool must contain exactly 136 tiles, but has 135");
            AssertG003bAuditEqual(probe.GetProperty("firstAudit"), probe.GetProperty("secondAudit"));

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
            ulong previous = map.StateHash;
            Assert.Throws<NotSupportedException>(
                () => view.Add(new TilePlacement(new TileId(0), TilePosition.Wall(0))));
            Assert.Equal(previous, map.StateHash);
        }

        [Fact]
        public void G003bBoundLedgerMismatchProbeIsExportedAndCsharpMapIsSingleLedger()
        {
            JsonElement probe = Sample("G-003b").GetProperty("expected").GetProperty("probeB");
            Assert.Contains("TestTableBuilder.create()", Str(probe, "fixture"));
            Assert.Contains("setLocation", Str(probe, "fixture"));
            AssertTileSemantics(probe.GetProperty("target"), new TileId(0));

            JsonElement before = probe.GetProperty("before");
            Assert.Equal("accepted", Str(before, "auditOutcome"));
            Assert.Equal(136, Int(before, "poolSize"));
            Assert.Equal("HAND", Str(before.GetProperty("location"), "zone"));
            Assert.Equal(0, Int(before.GetProperty("location"), "player"));
            int[] actualBefore = AssertActualTiles(before);

            AssertG003bAudit(
                probe.GetProperty("firstAudit"),
                "rejected",
                "java.lang.IllegalStateException",
                "pool-ledger-mismatch",
                "TilePool audit failed");
            string firstMessage = Str(probe.GetProperty("firstAudit"), "message");
            Assert.Contains("Tile(suit=MAN, rank=1, copyIndex=0)", firstMessage);
            Assert.Contains("actual container has TileLocation(zone=HAND, player=0)", firstMessage);
            Assert.Contains("recorded TileLocation(zone=WALL, player=-1)", firstMessage);

            JsonElement after = probe.GetProperty("after");
            Assert.Equal("WALL", Str(after, "poolZone"));
            Assert.Equal(-1, Int(after, "poolPlayer"));
            Assert.True(Bool(after, "closedTilesStillContains"));
            Assert.Equal(136, Int(after, "poolSize"));
            int[] actualAfter = AssertActualTiles(after);
            Assert.Equal(actualBefore, actualAfter);
            Assert.Equal(Str(before, "actualTilesSha256"), Str(after, "actualTilesSha256"));
            Assert.False(Bool(probe, "autoRepairObserved"));
            AssertG003bAudit(
                probe.GetProperty("secondAudit"),
                "rejected",
                "java.lang.IllegalStateException",
                "pool-ledger-mismatch",
                "TilePool audit failed");
            AssertG003bAuditEqual(probe.GetProperty("firstAudit"), probe.GetProperty("secondAudit"));

            JsonElement mapping = probe.GetProperty("csharpMapping");
            AssertExactKeys(mapping, "status", "note");
            Assert.Equal("structural-immutable-map-no-separate-ledger", Str(mapping, "status"));
            Assert.Contains("read-only", Str(mapping, "note"));
            Assert.Contains("construction", Str(mapping, "note"));

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
            Assert.Equal(-1, Int(limitation.GetProperty("firstValue"), "player"));
            Assert.Equal("HAND", Str(limitation.GetProperty("finalValue"), "zone"));
            Assert.Equal(0, Int(limitation.GetProperty("finalValue"), "player"));
            Assert.NotEqual(
                (Str(limitation.GetProperty("firstValue"), "zone"), Int(limitation.GetProperty("firstValue"), "player")),
                (Str(limitation.GetProperty("finalValue"), "zone"), Int(limitation.GetProperty("finalValue"), "player")));
        }

        private static void AssertSampleMetadata(JsonElement sample, string sampleId)
        {
            string expectedStatus = (sampleId == "G-001c" || sampleId == "G-002" || sampleId == "G-003b")
                ? "pending-v4-replay"
                : "pending";
            Assert.Equal(expectedStatus, Str(sample, "manifestStatusBefore"));

            JsonElement source = sample.GetProperty("source");
            Assert.False(string.IsNullOrWhiteSpace(Str(source, "file")));
            Assert.Equal("com.majiang.model.V5PhysicalGoldenExporterTest", Str(source, "testClass"));
            Assert.Contains(sampleId, Str(source, "testMethod"));
            Assert.False(string.IsNullOrWhiteSpace(Str(source, "note")));

            JsonElement input = sample.GetProperty("input");
            string fixture = Str(input, "fixture");
            Assert.False(string.IsNullOrWhiteSpace(fixture));
            switch (sampleId)
            {
                case "G-001a":
                    Assert.Contains("buildStandardSet", fixture);
                    break;
                case "G-001b":
                    Assert.Contains("fromTypeIndex", fixture);
                    break;
                case "G-001c":
                    Assert.Contains("fromTileId(input)", fixture);
                    break;
                case "G-002":
                    Assert.Contains("TileLocation(zone, player)", fixture);
                    break;
                case "G-003a":
                    Assert.Contains("ShuffleWallGenerator.generate", fixture);
                    break;
                case "G-003b":
                    Assert.Contains("TilePool", fixture);
                    break;
                default:
                    throw new InvalidOperationException("unknown sampleId metadata: " + sampleId);
            }

            foreach (JsonProperty property in input.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "fixture":
                    case "seed":
                    case "dealerFormula":
                    case "plans":
                    case "config":
                    case "randomInputs":
                        break;
                    default:
                        Assert.Equal("not-applicable", property.Value.GetString());
                        break;
                }
            }

            if (sampleId == "G-003a")
            {
                Assert.Contains("0..31 fixed", Str(input, "seed"));
                Assert.Equal("dealerSeat = seed % 4", Str(input, "dealerFormula"));
                Assert.Equal("ShuffleConfig(enabled=true)", Str(input, "config"));
                Assert.Contains("complete", Str(input, "randomInputs"));
                Assert.Contains("136-tile wall", Str(input, "randomInputs"));
                JsonElement plans = input.GetProperty("plans");
                Assert.Equal(3, plans.GetArrayLength());
                Assert.Equal(
                    new[] { 0, 1, 2 },
                    plans.EnumerateArray().Select(v => Int(v, "seat")).ToArray());
                Assert.Equal(
                    new[] { "KOKUSHI", "DAISANGEN", "CHINITSU_MAN" },
                    plans.EnumerateArray().Select(v => Str(v, "target")).ToArray());
            }
            else
            {
                Assert.Equal("not-applicable", Str(input, "seed"));
            }
        }

        private static void AssertTileSemantics(JsonElement json, TileId tile)
        {
            Assert.Equal(Int(json, "tileId"), tile.Value);
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
        }

        private static ArgumentOutOfRangeException AssertInvalidTileId(JsonElement json)
        {
            int input = Int(json, "input");
            Assert.Equal("rejected", Str(json, "outcome"));
            Assert.Equal("java.lang.IllegalArgumentException", Str(json, "exceptionType"));
            Assert.Equal("invalid-tile-id", Str(json, "semanticCategory"));
            Assert.Equal(
                "tileId must be 0..135, got " + input.ToString(CultureInfo.InvariantCulture),
                Str(json, "message"));

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TileId(input));
            Assert.Equal("value", ex.ParamName);
            Assert.Equal(input, Assert.IsType<int>(ex.ActualValue));
            Assert.Contains("0..135", ex.Message);
            Assert.Equal("System.ArgumentOutOfRangeException", ex.GetType().FullName);
            Assert.False(TileId.TryFromValue(input, out _));
            Assert.False(TileId.TryFromTypeIndex(input / 4, input % 4, out _));
            return ex;
        }

        private static void AssertSuitOrder(string suitOrder, IReadOnlyDictionary<int, string> groupSuitByType)
        {
            string[] segments = suitOrder.Split(',', StringSplitOptions.TrimEntries);
            Assert.Equal(5, segments.Length);
            string[] expectedSuits = { "MAN", "SOU", "PIN", "WIND", "DRAGON" };
            int nextTypeIndex = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                Match match = Regex.Match(segments[i], @"^(MAN|SOU|PIN|WIND|DRAGON) (\d+)\.\.(\d+)$");
                Assert.True(match.Success, "bad suitOrder segment: " + segments[i]);
                Assert.Equal(expectedSuits[i], match.Groups[1].Value);
                int first = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                int last = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                Assert.Equal(nextTypeIndex, first);
                Assert.True(last >= first, "descending suitOrder segment: " + segments[i]);
                for (int typeIndex = first; typeIndex <= last; typeIndex++)
                {
                    Assert.True(groupSuitByType.ContainsKey(typeIndex), "suitOrder covers missing typeIndex " + typeIndex);
                    Assert.Equal(match.Groups[1].Value, groupSuitByType[typeIndex]);
                    TileId tile = TileId.FromTypeIndex(typeIndex, 0);
                    Assert.Equal(Suit(match.Groups[1].Value), tile.Suit);
                }

                nextTypeIndex = last + 1;
            }

            Assert.Equal(34, nextTypeIndex);
        }

        private static void AssertV4ZoneOwnerMessage(JsonElement json, string zoneName, int player, string csharpMapping)
        {
            string message = Str(json, "message");
            if (csharpMapping == "structural-owner-forbidden")
            {
                Assert.Equal(
                    "Zone " + zoneName + " must not have player owner, but got player=" +
                    player.ToString(CultureInfo.InvariantCulture),
                    message);
            }
            else
            {
                Assert.Equal("invalid-seat", csharpMapping);
                Assert.Equal(
                    "Zone " + zoneName + " must belong to player 0..3, but got player=" +
                    player.ToString(CultureInfo.InvariantCulture),
                    message);
            }
        }

        private static void AssertG001cExceptionMappingSource(JsonElement sample)
        {
            JsonElement source = sample.GetProperty("source");
            Assert.Equal("com.majiang.model.V5PhysicalGoldenExporterTest", Str(source, "testClass"));
            Assert.Contains("G-001c", Str(source, "testMethod"));
            Assert.Contains("out-of-range", Str(source, "note"));

            Match match = Regex.Match(Str(source, "file"), @"^(?<path>[^:]+):(?<start>\d+)-(?<end>\d+)$");
            Assert.True(match.Success, "G-001c source.file must be path:start-end");
            string path = Path.Combine(
                RepositoryRoot(),
                match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "G-001c source anchor missing: " + path);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            int start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
            int end = int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture);
            Assert.InRange(start, 1, lines.Length);
            Assert.InRange(end, start, lines.Length);
            string segment = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
            Assert.Contains("fun fromTileId", segment);
            Assert.Contains("require(tileId in 0..135)", segment);
            Assert.Contains("tileId must be 0..135, got $tileId", segment);
            Assert.Contains("fromTypeIndex(tileId / 4, tileId % 4)", segment);
        }

        private static void AssertG003bAudit(
            JsonElement audit,
            string outcome,
            string exceptionType,
            string semanticCategory,
            string messageFragment)
        {
            Assert.Equal(outcome, Str(audit, "outcome"));
            Assert.Equal(exceptionType, Str(audit, "exceptionType"));
            Assert.Equal(semanticCategory, Str(audit, "semanticCategory"));
            Assert.Contains(messageFragment, Str(audit, "message"));
        }

        private static void AssertG003bAuditEqual(JsonElement first, JsonElement second)
        {
            Assert.Equal(Str(first, "outcome"), Str(second, "outcome"));
            Assert.Equal(Str(first, "exceptionType"), Str(second, "exceptionType"));
            Assert.Equal(Str(first, "semanticCategory"), Str(second, "semanticCategory"));
            Assert.Equal(Str(first, "message"), Str(second, "message"));
        }

        private static int[] AssertActualTiles(JsonElement container)
        {
            int[] actualTiles = Ints(container.GetProperty("actualTiles"));
            Assert.Equal(136, actualTiles.Length);
            Assert.Equal(136, actualTiles.Distinct().Count());
            Assert.Equal(Enumerable.Range(0, 136), actualTiles.OrderBy(v => v));
            Assert.Equal(Int(container, "actualTilesCount"), actualTiles.Length);
            Assert.Equal(
                Sha256Text(string.Join(",", actualTiles.Select(v => v.ToString(CultureInfo.InvariantCulture)))),
                Str(container, "actualTilesSha256"));
            Assert.Matches("^[0-9a-f]{64}$", Str(container, "actualTilesSha256"));
            return actualTiles;
        }

        private static void AssertExactKeys(JsonElement element, params string[] expectedNames)
        {
            string[] actual = element.EnumerateObject()
                .Select(p => p.Name)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();
            string[] expected = expectedNames.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
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

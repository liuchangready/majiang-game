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
using Mahjong.Domain.Stacking;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// P0.1 companion binding for the V4-exported actual reversed wall.
    ///
    /// This class reads the new <c>tests/v5-golden/shuffle-reversed-v1.json</c> together
    /// with the unchanged historical <c>tests/v5-golden/shuffle-v1.json</c> and proves:
    /// - the companion's 32 exact seeds/dealers/plans/base walls/three-caster walls are
    ///   the same real V4 samples as the old fixture; any mismatch fails instead of
    ///   refreshing the old fixture;
    /// - <c>reversedWall</c> is a real conserved 136-tile V4 wall from a separate
    ///   <c>generate(seed, dealer, threePlans.reversed(), config)</c> execution;
    /// - <c>reversedEqualsThree</c> is recomputed in C# from the two exported walls and
    ///   must equal both the companion boolean and the old fixture boolean;
    /// - source provenance hashes are recomputed from the real source bytes, with the
    ///   raw and LF-normalized domains kept distinct.
    ///
    /// It is <b>not</b> a C# Shape three-caster output diff. The companion only supplies
    /// the previously missing actual reversed wall; <c>threeCasterShapeBinding</c> still
    /// lacks real V4 ordered random events and remains BLOCKED_RANDOM_INPUT. The old
    /// SOURCE_ONLY conclusion is not rewritten.
    /// </summary>
    public sealed class ShuffleReversedGoldenTests
    {
        private const string CompanionRelativePath = "tests/v5-golden/shuffle-reversed-v1.json";
        private const string OldRelativePath = "tests/v5-golden/shuffle-v1.json";
        private const string ExporterPath =
            "engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt";
        private const string V4StackingPath = "engine/src/main/kotlin/com/majiang/stacking";

        // The caller-declared frozen PR19 accepted base. This is a provenance equality,
        // not an ancestry re-verification.
        private const string FrozenV4Base = "03084f08af6e2ddefa2ebc6e094de5dac2474fd4";
        private const string CompanionSchema = "v5-shuffle-reversed-golden-v1";
        private const string OldSchema = "v5-shuffle-golden-v1";

        // Historical fixture integrity pins from the previously accepted shuffle-v1
        // artifact. These are source-fixture digests, not generated wall or output
        // oracles: they prove the old sample was not refreshed while the new companion
        // was added. The LF digest is checkout-stable; the raw digest is reconstructed
        // from the recorded CRLF profile.
        private const string AcceptedOldFixtureLfSha256 =
            "eb1fcce7a11330721aff5042c73e7140e229a815b83f8202de3c17312788b4b4";

        private const string AcceptedOldFixtureRawCrlfSha256 =
            "386061b42691ccf36381e2d87dc4787367958c8e9e96a93ba69d1e3fe5dac282";

        private static readonly Lazy<JsonDocument> Companion =
            new Lazy<JsonDocument>(() => JsonDocument.Parse(File.ReadAllText(FindFixturePath(CompanionRelativePath))));

        private static readonly Lazy<JsonDocument> Old =
            new Lazy<JsonDocument>(() => JsonDocument.Parse(File.ReadAllText(FindFixturePath(OldRelativePath))));

        private static readonly string[] TopLevelFields =
        {
            "schema",
            "generator",
            "generatorVersion",
            "v4BaseCommit",
            "exportWorktreeHead",
            "rulesetVersion",
            "generatedByCommand",
            "extractionStatus",
            "exportedAtNote",
            "v4ProductionSourceNote",
            "relationNote",
            "scopeNote",
            "generationBaselineNote",
            "sourceHashAlgorithm",
            "sourceHashNote",
            "sourceTree",
            "bindsOldFixture",
            "config",
            "generationResult",
            "cases"
        };

        private static readonly string[] CaseFields =
        {
            "seed",
            "dealerSeat",
            "plans",
            "reversedPlans",
            "baseWall",
            "threeCasterWall",
            "reversedWall",
            "redFiveCount",
            "deadWallSegmentEqual",
            "threeCasterChanged",
            "reversedEqualsThree"
        };

        private static readonly string[] SourceFields =
        {
            "role",
            "path",
            "sha256Raw",
            "sha256LfNormalized",
            "bytesRaw",
            "bytesLfNormalized",
            "lineEnding",
            "rawEqualsLfNormalized"
        };

        private static readonly string[] BindsOldFixtureFields =
        {
            "path",
            "schema",
            "v4Commit",
            "caseCount",
            "sha256Raw",
            "sha256LfNormalized",
            "bytesRaw",
            "bytesLfNormalized",
            "lineEnding"
        };

        private static readonly string[] GenerationResultFields =
        {
            "cases",
            "reversedEqualsThreeSeeds",
            "threeCasterChangedSeeds",
            "deadWallSegmentEqualSeeds",
            "exporterTestResult"
        };

        private static readonly string[] ConfigFields =
        {
            "enabled",
            "plannedDraws",
            "strength",
            "interference",
            "diversityInterference",
            "diversityBudgetPenalty",
            "tiers",
            "mixedTiers",
            "targetTiers"
        };

        private static readonly IReadOnlyDictionary<string, string> RequiredSources =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                {
                    "v4_production_wall_generator",
                    "engine/src/main/kotlin/com/majiang/stacking/ShuffleWallGenerator.kt"
                },
                {
                    "v4_production_shuffle_slots",
                    "engine/src/main/kotlin/com/majiang/stacking/ShuffleSlots.kt"
                },
                {
                    "v4_production_shuffle_config",
                    "engine/src/main/kotlin/com/majiang/stacking/ShuffleConfig.kt"
                },
                {
                    "v4_production_shuffle_targets",
                    "engine/src/main/kotlin/com/majiang/stacking/ShuffleTargets.kt"
                },
                {
                    "v4_production_shuffle_target",
                    "engine/src/main/kotlin/com/majiang/stacking/ShuffleTarget.kt"
                },
                {
                    "v4_production_shuffle_selection",
                    "engine/src/main/kotlin/com/majiang/stacking/ShuffleSelection.kt"
                },
                {
                    "v4_production_seat_shuffle_plan",
                    "engine/src/main/kotlin/com/majiang/stacking/SeatShufflePlan.kt"
                },
                {
                    "v4_production_tile_pool",
                    "engine/src/main/kotlin/com/majiang/model/TilePool.kt"
                },
                {
                    "v4_production_tile",
                    "engine/src/main/kotlin/com/majiang/model/Tile.kt"
                },
                {
                    "v4_test_only_exporter",
                    "engine/src/test/kotlin/com/majiang/v3/V5ShuffleReversedGoldenExporterTest.kt"
                }
            };

        // ------------------------------------------------------------------
        // Provenance, source hashes and old-fixture binding
        // ------------------------------------------------------------------

        [Fact]
        public void CompanionSchemaSourceTreeAndOldFixtureProvenanceAreReal()
        {
            JsonElement root = Companion.Value.RootElement;
            AssertExactProperties(root, TopLevelFields, "root");
            Assert.Equal(CompanionSchema, Str(root, "schema"));
            Assert.Equal(ExporterPath, Str(root, "generator"));
            Assert.Equal(1, Int(root, "generatorVersion"));
            Assert.Equal(1, Int(root, "rulesetVersion"));
            Assert.Equal("exported", Str(root, "extractionStatus"));
            Assert.Equal(FrozenV4Base, Str(root, "v4BaseCommit"));
            Assert.Equal(FrozenV4Base, Str(root, "exportWorktreeHead"));
            Assert.Contains("V5ShuffleReversedGoldenExporterTest", Str(root, "generatedByCommand"));
            Assert.Contains("actual V4 ShuffleWallGenerator.generate", Str(root, "exportedAtNote"));
            Assert.Contains("not copied from shuffle-v1.json", Str(root, "relationNote"));
            Assert.Contains("BLOCKED_RANDOM_INPUT", Str(root, "scopeNote"));
            Assert.Contains("not a C# Shape", Str(root, "scopeNote"));
            Assert.Contains("separate actual V4 generate executions", Str(root, "relationNote"));
            Assert.Equal("SHA-256", Str(root, "sourceHashAlgorithm"));
            Assert.Contains("sha256LfNormalized", Str(root, "sourceHashNote"));
            Assert.Contains("sha256Raw", Str(root, "sourceHashNote"));
            Assert.Contains(FrozenV4Base, Str(root, "generationBaselineNote"));

            string repositoryRoot = RequireRepositoryRoot();
            JsonElement sources = Arr(root, "sourceTree", RequiredSources.Count);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement source in sources.EnumerateArray())
            {
                AssertExactProperties(source, SourceFields, "sourceTree[]");
                string role = Str(source, "role");
                string path = Str(source, "path");
                Assert.True(
                    RequiredSources.TryGetValue(role, out string? expectedPath),
                    "unexpected source role, never fall back to a valid entry: " + role);
                Assert.Equal(expectedPath, path);
                Assert.True(roles.Add(role), "duplicate source role: " + role);
                Assert.True(paths.Add(path), "duplicate source path: " + path);
                AssertHash(Str(source, "sha256Raw"), "sourceTree[].sha256Raw");
                AssertHash(Str(source, "sha256LfNormalized"), "sourceTree[].sha256LfNormalized");
                VerifySourceFile(repositoryRoot, source);
            }

            Assert.Equal(RequiredSources.Count, roles.Count);
            foreach (string role in RequiredSources.Keys)
            {
                Assert.True(roles.Contains(role), "missing required source role: " + role);
            }

            JsonElement binds = Obj(root, "bindsOldFixture");
            AssertExactProperties(binds, BindsOldFixtureFields, "bindsOldFixture");
            Assert.Equal(OldRelativePath, Str(binds, "path"));
            Assert.Equal(OldSchema, Str(binds, "schema"));
            Assert.Equal(32, Int(binds, "caseCount"));
            string oldCommit = Str(binds, "v4Commit");
            AssertHashLike40(oldCommit, "bindsOldFixture.v4Commit");

            JsonElement oldRoot = Old.Value.RootElement;
            Assert.Equal(OldSchema, Str(oldRoot, "schema"));
            Assert.Equal(32, Arr(oldRoot, "cases", 32).GetArrayLength());
            Assert.Equal(oldCommit, Str(oldRoot, "v4Commit"));

            string oldPath = FindFixturePath(OldRelativePath);
            byte[] oldRaw = File.ReadAllBytes(oldPath);
            byte[] oldLf = NormalizeLf(oldRaw);
            Assert.Equal(Sha256(oldLf), Str(binds, "sha256LfNormalized"));
            Assert.Equal((long)oldLf.Length, Long(binds, "bytesLfNormalized"));
            string oldEnding = Str(binds, "lineEnding");
            byte[] reconstructedOldRaw = oldEnding == "CRLF" ? ToCrlf(oldLf) : oldLf;
            Assert.Equal(Sha256(reconstructedOldRaw), Str(binds, "sha256Raw"));
            Assert.Equal((long)reconstructedOldRaw.Length, Long(binds, "bytesRaw"));
            Assert.Equal(AcceptedOldFixtureLfSha256, Str(binds, "sha256LfNormalized"));
            Assert.Equal(AcceptedOldFixtureLfSha256, Sha256(oldLf));
            Assert.Equal(AcceptedOldFixtureRawCrlfSha256, Str(binds, "sha256Raw"));
            Assert.Equal(AcceptedOldFixtureRawCrlfSha256, Sha256(reconstructedOldRaw));
            if (DetectLineEnding(oldRaw) == oldEnding)
            {
                Assert.Equal(Sha256(oldRaw), Str(binds, "sha256Raw"));
            }

            JsonElement generation = Obj(root, "generationResult");
            AssertExactProperties(generation, GenerationResultFields, "generationResult");
            Assert.Equal(32, Int(generation, "cases"));
            Assert.Equal("PASSED", Str(generation, "exporterTestResult"));
        }

        // ------------------------------------------------------------------
        // Exact old-fixture input/output binding
        // ------------------------------------------------------------------

        [Fact]
        public void CompanionBindsEveryOldCaseSeedDealerPlansBaseAndThreeCasterWall()
        {
            var oldBySeed = OldCases().ToDictionary(item => item.Seed, item => item);
            Assert.Equal(32, oldBySeed.Count);
            Assert.Equal(Enumerable.Range(0, 32), oldBySeed.Keys.OrderBy(seed => seed));

            var newCases = Cases().ToList();
            Assert.Equal(32, newCases.Count);
            var seeds = new HashSet<int>();
            foreach (FixtureCase fixture in newCases)
            {
                Assert.InRange(fixture.Seed, 0, 31);
                Assert.True(seeds.Add(fixture.Seed), "duplicate companion seed: " + fixture.Seed);
                Assert.Equal(fixture.Seed % 4, fixture.DealerSeat);
                Assert.True(
                    oldBySeed.TryGetValue(fixture.Seed, out OldCase? old),
                    "old fixture has no sample for seed " + fixture.Seed);

                Assert.Equal(old!.DealerSeat, fixture.DealerSeat);
                AssertPlanContract(fixture.Plans, "companion plans seed=" + fixture.Seed);
                AssertPlanContract(old.Plans, "old plans seed=" + old.Seed);
                Assert.Equal(
                    old.Plans.Select(plan => (plan.Seat, plan.Target)),
                    fixture.Plans.Select(plan => (plan.Seat, plan.Target)));

                // The exact reversed input is also recorded and must be the exact reverse.
                Assert.Equal(
                    fixture.Plans.Select(plan => (plan.Seat, plan.Target)).Reverse(),
                    fixture.ReversedPlans.Select(plan => (plan.Seat, plan.Target)));
                Assert.Equal(3, fixture.ReversedPlans.Count);
                Assert.Equal(
                    new[] { 2, 1, 0 },
                    fixture.ReversedPlans.Select(plan => plan.Seat).ToArray());

                // The new companion must bind the unchanged old sample, not a refreshed one.
                Assert.Equal(old.BaseWall, fixture.BaseWall);
                Assert.Equal(old.ThreeCasterWall, fixture.ThreeCasterWall);
                Assert.True(
                    old.ReversedEqualsThree,
                    "old fixture source boolean must still be true for seed " + old.Seed);
            }

            Assert.Equal(32, seeds.Count);
            Assert.Equal(Enumerable.Range(0, 32), seeds.OrderBy(seed => seed));
        }

        // ------------------------------------------------------------------
        // Real reversed wall, conservation and recomputed relation
        // ------------------------------------------------------------------

        [Fact]
        public void ReversedWallIsConservedAndRecomputesReversedEqualsThreeFromBothFixtures()
        {
            var oldBySeed = OldCases().ToDictionary(item => item.Seed, item => item);
            int reversedTrue = 0;
            int changedTrue = 0;
            int deadWallTrue = 0;

            foreach (FixtureCase fixture in Cases())
            {
                AssertWall(fixture.BaseWall, "baseWall seed=" + fixture.Seed);
                AssertWall(fixture.ThreeCasterWall, "threeCasterWall seed=" + fixture.Seed);
                AssertWall(fixture.ReversedWall, "reversedWall seed=" + fixture.Seed);
                Assert.Equal(3, fixture.RedFiveCount);

                bool deadWallSegmentEqual = fixture.BaseWall
                    .Skip(ShuffleSlots.DeadWallStart)
                    .SequenceEqual(fixture.ThreeCasterWall.Skip(ShuffleSlots.DeadWallStart));
                Assert.Equal(fixture.DeadWallSegmentEqual, deadWallSegmentEqual);
                Assert.True(
                    fixture.BaseWall.Skip(ShuffleSlots.DeadWallStart)
                        .SequenceEqual(fixture.ReversedWall.Skip(ShuffleSlots.DeadWallStart)),
                    "reversedWall must not disturb dead wall 122..135 for seed " + fixture.Seed);

                bool threeCasterChanged = !fixture.BaseWall.SequenceEqual(fixture.ThreeCasterWall);
                Assert.Equal(fixture.ThreeCasterChanged, threeCasterChanged);
                Assert.True(threeCasterChanged, "accepted n=3 sample must change slots for seed " + fixture.Seed);

                // This is the actual SOURCE_ONLY gap being closed: recompute from the two
                // real V4 walls, not from the old boolean and not from a C# Shape call.
                bool reversedEqualsThree = fixture.ThreeCasterWall.SequenceEqual(fixture.ReversedWall);
                Assert.Equal(fixture.ReversedEqualsThree, reversedEqualsThree);
                Assert.True(reversedEqualsThree, "V4 reversed-plan wall must equal forward wall for seed " + fixture.Seed);
                Assert.False(
                    fixture.BaseWall.SequenceEqual(fixture.ReversedWall),
                    "reversedWall must be a real shaped V4 wall, not a copy of baseWall, seed " + fixture.Seed);

                Assert.True(
                    oldBySeed.TryGetValue(fixture.Seed, out OldCase? old),
                    "old fixture missing seed " + fixture.Seed);
                Assert.Equal(old!.ReversedEqualsThree, reversedEqualsThree);
                Assert.Equal(old.ThreeCasterWall, fixture.ReversedWall);

                if (reversedEqualsThree)
                {
                    reversedTrue++;
                }

                if (threeCasterChanged)
                {
                    changedTrue++;
                }

                if (deadWallSegmentEqual)
                {
                    deadWallTrue++;
                }
            }

            Assert.Equal(32, reversedTrue);
            Assert.Equal(32, changedTrue);
            Assert.Equal(32, deadWallTrue);
            JsonElement generation = Obj(Companion.Value.RootElement, "generationResult");
            Assert.Equal(reversedTrue, Int(generation, "reversedEqualsThreeSeeds"));
            Assert.Equal(changedTrue, Int(generation, "threeCasterChangedSeeds"));
            Assert.Equal(deadWallTrue, Int(generation, "deadWallSegmentEqualSeeds"));
        }

        // ------------------------------------------------------------------
        // Exact config and old-fixture immutability
        // ------------------------------------------------------------------

        [Fact]
        public void CompanionConfigAndCountersMatchTheAcceptedV4InputContract()
        {
            JsonElement root = Companion.Value.RootElement;
            JsonElement config = Obj(root, "config");
            AssertExactProperties(config, ConfigFields, "config");

            JsonElement oldConfig = Obj(Old.Value.RootElement, "configSnapshot");
            AssertJsonEqual(config, oldConfig, "config");

            ShuffleConfig defaults = new ShuffleConfig(enabled: true);
            Assert.True(Bool(config, "enabled"));
            Assert.Equal(defaults.PlannedDraws, Int(config, "plannedDraws"));
            Assert.Equal(defaults.Strength, Double(config, "strength"), 12);
            Assert.Equal(defaults.Interference, Double(config, "interference"), 12);
            Assert.Equal(defaults.DiversityInterference, Double(config, "diversityInterference"), 12);
            Assert.Equal(defaults.DiversityBudgetPenalty, Double(config, "diversityBudgetPenalty"), 12);
            AssertTierMap(Obj(config, "tiers"), defaults.Tiers, "config.tiers");
            AssertTierMap(Obj(config, "mixedTiers"), defaults.MixedTiers, "config.mixedTiers");

            JsonElement targetTiers = Obj(config, "targetTiers");
            Assert.Equal(
                defaults.TargetTiers.Keys.OrderBy(target => (int)target).ToList(),
                targetTiers.EnumerateObject()
                    .Select(property => ParseTarget(property.Name))
                    .OrderBy(target => (int)target)
                    .ToList());
            foreach (KeyValuePair<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> entry in defaults.TargetTiers)
            {
                AssertTierMap(
                    Obj(targetTiers, ExportTargetName(entry.Key)),
                    entry.Value,
                    "config.targetTiers." + entry.Key);
            }

            // The old fixture is still the accepted V4 sample: exact 0..31 and every
            // exported reversedEqualsThree is true. This class never rewrites it.
            var oldCases = OldCases().ToList();
            Assert.Equal(32, oldCases.Count);
            Assert.Equal(Enumerable.Range(0, 32), oldCases.Select(item => item.Seed).OrderBy(seed => seed));
            Assert.All(oldCases, item => Assert.True(item.ReversedEqualsThree));

            JsonElement generation = Obj(root, "generationResult");
            Assert.Equal(32, Int(generation, "cases"));
            Assert.Equal("PASSED", Str(generation, "exporterTestResult"));
        }

        // ------------------------------------------------------------------
        // Strict parsing: missing, wrong-typed and unknown values throw/fail.
        // ------------------------------------------------------------------

        private static IEnumerable<FixtureCase> Cases()
        {
            JsonElement cases = Arr(Companion.Value.RootElement, "cases", 32);
            foreach (JsonElement entry in cases.EnumerateArray())
            {
                AssertExactProperties(entry, CaseFields, "cases[]");
                yield return new FixtureCase
                {
                    Seed = Int(entry, "seed"),
                    DealerSeat = Int(entry, "dealerSeat"),
                    Plans = ParsePlans(entry, "plans"),
                    ReversedPlans = ParsePlans(entry, "reversedPlans"),
                    BaseWall = IntArray(entry, "baseWall", TileId.Count),
                    ThreeCasterWall = IntArray(entry, "threeCasterWall", TileId.Count),
                    ReversedWall = IntArray(entry, "reversedWall", TileId.Count),
                    RedFiveCount = Int(entry, "redFiveCount"),
                    DeadWallSegmentEqual = Bool(entry, "deadWallSegmentEqual"),
                    ThreeCasterChanged = Bool(entry, "threeCasterChanged"),
                    ReversedEqualsThree = Bool(entry, "reversedEqualsThree")
                };
            }
        }

        private static IEnumerable<OldCase> OldCases()
        {
            JsonElement cases = Arr(Old.Value.RootElement, "cases", 32);
            foreach (JsonElement entry in cases.EnumerateArray())
            {
                yield return new OldCase
                {
                    Seed = Int(entry, "seed"),
                    DealerSeat = Int(entry, "dealerSeat"),
                    Plans = ParsePlans(entry, "plans"),
                    BaseWall = IntArray(entry, "baseWall", TileId.Count),
                    ThreeCasterWall = IntArray(entry, "threeCasterWall", TileId.Count),
                    ReversedEqualsThree = Bool(entry, "reversedEqualsThree")
                };
            }
        }

        private static IReadOnlyList<Plan> ParsePlans(JsonElement parent, string name)
        {
            JsonElement plans = Arr(parent, name, 3);
            var result = new List<Plan>(3);
            foreach (JsonElement plan in plans.EnumerateArray())
            {
                int seat = Int(plan, "seat");
                ShuffleTarget target = ParseTarget(Str(plan, "target"));
                result.Add(new Plan(seat, target));
            }

            return result;
        }

        private static void AssertPlanContract(IReadOnlyList<Plan> plans, string context)
        {
            Assert.Equal(3, plans.Count);
            Assert.Equal(new[] { 0, 1, 2 }, plans.Select(plan => plan.Seat).ToArray());
            Assert.Equal(
                new[] { ShuffleTarget.Kokushi, ShuffleTarget.Daisangen, ShuffleTarget.ChinitsuMan },
                plans.Select(plan => plan.Target).ToArray());
        }

        private static string ExportTargetName(ShuffleTarget target)
        {
            switch (target)
            {
                case ShuffleTarget.Kokushi:
                    return "KOKUSHI";
                case ShuffleTarget.ChinitsuMan:
                    return "CHINITSU_MAN";
                case ShuffleTarget.Daisangen:
                    return "DAISANGEN";
                case ShuffleTarget.Daisuushii:
                    return "DAISUUSHII";
                case ShuffleTarget.ChinitsuSou:
                    return "CHINITSU_SOU";
                case ShuffleTarget.ChinitsuPin:
                    return "CHINITSU_PIN";
                default:
                    throw new InvalidOperationException("unknown shuffle target enum: " + target);
            }
        }

        private static ShuffleTarget ParseTarget(string name)
        {
            switch (name)
            {
                case "KOKUSHI":
                    return ShuffleTarget.Kokushi;
                case "CHINITSU_MAN":
                    return ShuffleTarget.ChinitsuMan;
                case "DAISANGEN":
                    return ShuffleTarget.Daisangen;
                case "DAISUUSHII":
                    return ShuffleTarget.Daisuushii;
                case "CHINITSU_SOU":
                    return ShuffleTarget.ChinitsuSou;
                case "CHINITSU_PIN":
                    return ShuffleTarget.ChinitsuPin;
                default:
                    throw new InvalidOperationException("unknown shuffle target, never fall back: " + name);
            }
        }

        private static void AssertWall(int[] wall, string context)
        {
            Assert.Equal(TileId.Count, wall.Length);
            Assert.Equal(TileId.Count, wall.Distinct().Count());
            Assert.Equal(Enumerable.Range(0, TileId.Count), wall.OrderBy(value => value));
            foreach (int value in wall)
            {
                _ = new TileId(value);
            }

            Assert.Equal(3, wall.Count(value => new TileId(value).IsRedFive));
        }

        // ------------------------------------------------------------------
        // Source-byte provenance. Raw and LF-normalized are distinct domains.
        // ------------------------------------------------------------------

        private static void VerifySourceFile(string repositoryRoot, JsonElement source)
        {
            string relativePath = Str(source, "path");
            string fullPath = Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), "source file must exist: " + relativePath);

            byte[] raw = File.ReadAllBytes(fullPath);
            byte[] lf = NormalizeLf(raw);
            string rawHash = Sha256(raw);
            string lfHash = Sha256(lf);
            Assert.Equal(lfHash, Str(source, "sha256LfNormalized"));
            Assert.Equal((long)lf.Length, Long(source, "bytesLfNormalized"));

            string ending = Str(source, "lineEnding");
            Assert.Contains(ending, new[] { "LF", "CRLF" });
            byte[] reconstructedRaw = ending == "CRLF" ? ToCrlf(lf) : lf;
            Assert.Equal(Sha256(reconstructedRaw), Str(source, "sha256Raw"));
            Assert.Equal((long)reconstructedRaw.Length, Long(source, "bytesRaw"));

            // rawEqualsLfNormalized records the export-time relation between the two
            // digests, not the checkout's current line ending. Compare the recorded
            // digests so a clean LF checkout of a CRLF-exported file remains valid.
            bool recordedRawEqualsLf = string.Equals(
                Str(source, "sha256Raw"),
                Str(source, "sha256LfNormalized"),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(recordedRawEqualsLf, Bool(source, "rawEqualsLfNormalized"));

            // When the checkout line ending matches the recorded profile, the actual raw
            // bytes must also match. On a different checkout the stable LF digest above
            // still binds the content.
            if (DetectLineEnding(raw) == ending)
            {
                Assert.Equal(rawHash, Str(source, "sha256Raw"));
            }
        }

        private static string Sha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static byte[] NormalizeLf(byte[] bytes)
        {
            var output = new List<byte>(bytes.Length);
            int index = 0;
            while (index < bytes.Length)
            {
                if (bytes[index] == 13)
                {
                    output.Add(10);
                    if (index + 1 < bytes.Length && bytes[index + 1] == 10)
                    {
                        index++;
                    }
                }
                else
                {
                    output.Add(bytes[index]);
                }

                index++;
            }

            return output.ToArray();
        }

        private static byte[] ToCrlf(byte[] lfBytes)
        {
            var output = new List<byte>(lfBytes.Length + 1024);
            foreach (byte value in lfBytes)
            {
                if (value == 10)
                {
                    output.Add(13);
                }

                output.Add(value);
            }

            return output.ToArray();
        }

        private static string DetectLineEnding(byte[] bytes)
        {
            int crlf = 0;
            int lf = 0;
            int loneCr = 0;
            int index = 0;
            while (index < bytes.Length)
            {
                if (bytes[index] == 13)
                {
                    if (index + 1 < bytes.Length && bytes[index + 1] == 10)
                    {
                        crlf++;
                        index++;
                    }
                    else
                    {
                        loneCr++;
                    }
                }
                else if (bytes[index] == 10)
                {
                    lf++;
                }

                index++;
            }

            if (crlf > 0 && lf == 0 && loneCr == 0)
            {
                return "CRLF";
            }

            if (crlf == 0 && loneCr == 0)
            {
                return "LF";
            }

            return "MIXED";
        }

        // ------------------------------------------------------------------
        // JSON helpers: missing and wrong-typed values never become defaults.
        // ------------------------------------------------------------------

        private static void AssertExactProperties(JsonElement element, string[] expected, string context)
        {
            string[] actual = element.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] sortedExpected = expected
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(sortedExpected, actual);
        }

        private static void AssertHash(string value, string context)
        {
            Assert.Equal(64, value.Length);
            Assert.True(value.All(Uri.IsHexDigit), context + " must be 64 hex characters");
        }

        private static void AssertHashLike40(string value, string context)
        {
            Assert.Equal(40, value.Length);
            Assert.True(value.All(Uri.IsHexDigit), context + " must be 40 hex characters");
        }

        private static void AssertTierMap(
            JsonElement element,
            IReadOnlyDictionary<int, ShuffleTier> expected,
            string context)
        {
            int[] actualKeys = element.EnumerateObject()
                .Select(property => int.Parse(property.Name, CultureInfo.InvariantCulture))
                .OrderBy(key => key)
                .ToArray();
            Assert.Equal(expected.Keys.OrderBy(key => key).ToArray(), actualKeys);
            foreach (KeyValuePair<int, ShuffleTier> entry in expected)
            {
                JsonElement tier = element.GetProperty(entry.Key.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(entry.Value.Strength, Double(tier, "strength"), 12);
                Assert.Equal(entry.Value.InitialBudget, Int(tier, "initialBudget"));
                Assert.Equal(entry.Value.DrawBudget, Int(tier, "drawBudget"));
            }
        }

        private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path)
        {
            Assert.Equal(expected.ValueKind, actual.ValueKind);
            switch (expected.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var expectedProperties = expected.EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .ToList();
                    var actualProperties = actual.EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .ToList();
                    Assert.Equal(
                        expectedProperties.Select(property => property.Name),
                        actualProperties.Select(property => property.Name));
                    for (int index = 0; index < expectedProperties.Count; index++)
                    {
                        AssertJsonEqual(
                            expectedProperties[index].Value,
                            actualProperties[index].Value,
                            path + "." + expectedProperties[index].Name);
                    }

                    break;
                }

                case JsonValueKind.Array:
                    Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                    for (int index = 0; index < expected.GetArrayLength(); index++)
                    {
                        AssertJsonEqual(expected[index], actual[index], path + "[" + index + "]");
                    }

                    break;
                case JsonValueKind.Number:
                    Assert.Equal(expected.GetDouble(), actual.GetDouble(), 12);
                    break;
                case JsonValueKind.String:
                    Assert.Equal(expected.GetString(), actual.GetString());
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    throw new InvalidOperationException("unsupported JSON kind at " + path + ": " + expected.ValueKind);
            }
        }

        private static JsonElement Obj(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(name + " must be a JSON object, got " + value.ValueKind + ".");
            }

            return value;
        }

        private static JsonElement Arr(JsonElement parent, string name, int expectedCount)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(name + " must be a JSON array, got " + value.ValueKind + ".");
            }

            if (value.GetArrayLength() != expectedCount)
            {
                throw new InvalidOperationException(
                    name + " must contain " + expectedCount.ToString(CultureInfo.InvariantCulture) +
                    " entries, got " + value.GetArrayLength().ToString(CultureInfo.InvariantCulture) + ".");
            }

            return value;
        }

        private static string Str(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(name + " must be a JSON string, got " + value.ValueKind + ".");
            }

            return value.GetString()!;
        }

        private static bool Bool(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            {
                throw new InvalidOperationException(name + " must be a JSON boolean, got " + value.ValueKind + ".");
            }

            return value.GetBoolean();
        }

        private static int Int(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            {
                throw new InvalidOperationException(name + " must be a JSON int32, got " + value.ValueKind + ".");
            }

            return result;
        }

        private static long Long(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
            {
                throw new InvalidOperationException(name + " must be a JSON int64, got " + value.ValueKind + ".");
            }

            return result;
        }

        private static double Double(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result))
            {
                throw new InvalidOperationException(name + " must be a JSON number, got " + value.ValueKind + ".");
            }

            return result;
        }

        private static int[] IntArray(JsonElement parent, string name, int expectedLength)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(name + " must be a JSON array, got " + value.ValueKind + ".");
            }

            if (value.GetArrayLength() != expectedLength)
            {
                throw new InvalidOperationException(
                    name + " must contain " + expectedLength.ToString(CultureInfo.InvariantCulture) +
                    " entries, got " + value.GetArrayLength().ToString(CultureInfo.InvariantCulture) + ".");
            }

            var result = new int[expectedLength];
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int parsed))
                {
                    throw new InvalidOperationException(
                        name + "[" + index.ToString(CultureInfo.InvariantCulture) + "] must be a JSON int32.");
                }

                result[index] = parsed;
                index++;
            }

            return result;
        }

        private static string RequireRepositoryRoot()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, ExporterPath)) &&
                        Directory.Exists(Path.Combine(directory.FullName, V4StackingPath)))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            throw new InvalidOperationException(
                "cannot locate repository root from " + AppContext.BaseDirectory +
                "; source hashes and provenance require the real repository tree");
        }

        private static string FindFixturePath(string relativeName, [CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(
                    overrideRoot!,
                    relativeName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(overridden))
                {
                    return overridden;
                }

                throw new FileNotFoundException(
                    "MAHJONG_REPO_ROOT is set but does not contain " + relativeName +
                    ": " + overrideRoot);
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
                    string candidate = Path.Combine(
                        directory.FullName,
                        relativeName.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException(
                relativeName + " not found under " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() +
                "; set MAHJONG_REPO_ROOT if the test binaries live outside the repository.");
        }

        private readonly struct Plan
        {
            internal Plan(int seat, ShuffleTarget target)
            {
                Seat = seat;
                Target = target;
            }

            internal int Seat { get; }

            internal ShuffleTarget Target { get; }
        }

        private sealed class FixtureCase
        {
            internal int Seed { get; set; }

            internal int DealerSeat { get; set; }

            internal IReadOnlyList<Plan> Plans { get; set; } = Array.Empty<Plan>();

            internal IReadOnlyList<Plan> ReversedPlans { get; set; } = Array.Empty<Plan>();

            internal int[] BaseWall { get; set; } = Array.Empty<int>();

            internal int[] ThreeCasterWall { get; set; } = Array.Empty<int>();

            internal int[] ReversedWall { get; set; } = Array.Empty<int>();

            internal int RedFiveCount { get; set; }

            internal bool DeadWallSegmentEqual { get; set; }

            internal bool ThreeCasterChanged { get; set; }

            internal bool ReversedEqualsThree { get; set; }
        }

        private sealed class OldCase
        {
            internal int Seed { get; set; }

            internal int DealerSeat { get; set; }

            internal IReadOnlyList<Plan> Plans { get; set; } = Array.Empty<Plan>();

            internal int[] BaseWall { get; set; } = Array.Empty<int>();

            internal int[] ThreeCasterWall { get; set; } = Array.Empty<int>();

            internal bool ReversedEqualsThree { get; set; }
        }
    }
}

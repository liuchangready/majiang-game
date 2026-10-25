using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Stacking;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// N3 field-level binding for the V4-exported <c>tests/v5-golden/shuffle-v1.json</c>.
    ///
    /// Unlike <see cref="ShuffleGoldenTests"/>, this class does not treat the exported
    /// three-caster wall as a C# output. Each field is classified by what can actually be
    /// proven:
    /// - <b>PURE_MATH</b>: slotMatrix, demandVectors, overlapMatrix and configSnapshot are
    ///   compared value-for-value with the C# public helpers that own the same definitions.
    /// - <b>SHAPE_OUTPUT</b>: the no-target (n=0), four-caster (n=4) and zero-budget
    ///   identity paths are fed the same real base wall and a legal trace into the real
    ///   <see cref="ShuffleWallSynthesizer.Shape(string, IReadOnlyList{TileId}, IReadOnlyList{double}, int, IReadOnlyList{SeatShufflePlan}, ShuffleConfig)"/>
    ///   entry point; the shaped wall is compared tile-for-tile with the exported wall.
    ///   The zero-budget path is identity, but V4 still shuffles the seat/slot arrays
    ///   before the zero budget is observed, so its legal trace is a vacuous all-zero
    ///   sequence of the exact shuffle consumption, not an empty sequence. The empty
    ///   sequence is asserted to fail with TraceExhausted.
    /// - <b>INTERNAL_EXPORTED_WALL_RELATION</b>: deadWallSegmentEqual, threeCasterChanged
    ///   and fourCasterIdentity are recomputed from the exported arrays. The dead-wall
    ///   flag is additionally mapped through C# <see cref="TilePositionMap"/>:
    ///   exported slots 122..135 are C# DeadWall(0..13), and the flag means those 14
    ///   physical tiles are identical in baseWall and threeCasterWall. This is not a
    ///   Shape output for the three-caster case.
    /// - <b>SOURCE_ONLY</b>: reversedEqualsThree is the exporter's own V4 assertion. The
    ///   fixture does not export the reversed wall, so C# cannot recompute it; only the
    ///   exported boolean is consumed and the gap is recorded.
    /// - <b>BLOCKED_RANDOM_INPUT</b>: threeCasterWall cannot be fed to Shape because the
    ///   fixture does not export the recorded doubles consumed by V4's JVM Random(seed).
    ///   No seed simulation, guessed trace or reverse-engineered trace is performed here.
    /// </summary>
    public sealed class ShuffleGoldenBindingTests
    {
        private static readonly Lazy<JsonDocument> Golden = new Lazy<JsonDocument>(LoadGolden);

        private const string ExporterPath = "engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt";
        private const string V4RulesSourcePath = "engine/src/main/kotlin/com/majiang/stacking";

        private static readonly string[] TopLevelFields =
        {
            "schema",
            "generator",
            "generatorVersion",
            "v4Commit",
            "rulesetVersion",
            "generatedByCommand",
            "extractionStatus",
            "exportedAtNote",
            "v4RulesSourceNote",
            "rngGapNote",
            "generationResult",
            "slotMatrix",
            "demandVectors",
            "overlapMatrix",
            "configSnapshot",
            "cases"
        };

        private static readonly string[] CaseFields =
        {
            "seed",
            "dealerSeat",
            "plans",
            "baseWall",
            "threeCasterWall",
            "fourCasterWall",
            "noTargetWall",
            "zeroBudgetWall",
            "redFiveCount",
            "deadWallSegmentEqual",
            "threeCasterChanged",
            "fourCasterIdentity",
            "reversedEqualsThree"
        };

        private static readonly string[] GenerationResultFields =
        {
            "cases",
            "slotMatrixEntries",
            "threeCasterChangedSeeds",
            "exporterTestResult"
        };

        private static readonly string[] ConfigSnapshotFields =
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

        // ------------------------------------------------------------------
        // Structural inventory and provenance
        // ------------------------------------------------------------------

        [Fact]
        public void GoldenFileShapeInventoryIsCompleteAndProvenanceIsReal()
        {
            JsonElement root = Golden.Value.RootElement;
            AssertExactProperties(root, TopLevelFields, "root");
            Assert.Equal("v5-shuffle-golden-v1", Str(root, "schema"));
            Assert.Equal(1, Int(root, "generatorVersion"));
            Assert.Equal(1, Int(root, "rulesetVersion"));
            Assert.Equal("exported", Str(root, "extractionStatus"));
            Assert.Contains("V5ShuffleGoldenExporterTest", Str(root, "generatedByCommand"));
            Assert.Contains("actual V4 ShuffleWallGenerator", Str(root, "exportedAtNote"));

            string commit = Str(root, "v4Commit");
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(Uri.IsHexDigit), "v4Commit must be a real 40-hex git commit");

            string generator = Str(root, "generator");
            Assert.Equal(ExporterPath, generator);
            string? repositoryRoot = FindRepositoryRoot();
            if (repositoryRoot != null)
            {
                Assert.True(
                    File.Exists(Path.Combine(repositoryRoot, generator)),
                    "generator must name the real V4 exporter source: " + generator);
            }

            string v4RulesSource = Str(root, "v4RulesSourceNote");
            Assert.Contains(V4RulesSourcePath, v4RulesSource);
            Assert.Contains("V4 production source", v4RulesSource);
            Assert.Contains("v4Commit above is the exact tree used", v4RulesSource);
            if (repositoryRoot != null)
            {
                Assert.True(
                    Directory.Exists(Path.Combine(repositoryRoot, V4RulesSourcePath)),
                    "v4RulesSourceNote must name the real V4 production source tree: " + V4RulesSourcePath);
            }

            string rngGap = Str(root, "rngGapNote");
            Assert.Contains("D-09", rngGap);
            Assert.Contains("behavior samples", rngGap);
            Assert.Contains("never wall bytes", rngGap);

            JsonElement generation = Obj(root, "generationResult");
            AssertExactProperties(generation, GenerationResultFields, "generationResult");
            Assert.Equal(32, Int(generation, "cases"));
            Assert.Equal(16, Int(generation, "slotMatrixEntries"));
            Assert.Equal("PASSED", Str(generation, "exporterTestResult"));

            JsonElement cases = Arr(root, "cases", 32);
            int changed = 0;
            var seeds = new HashSet<int>();
            foreach (JsonElement entry in cases.EnumerateArray())
            {
                AssertExactProperties(entry, CaseFields, "cases[]");
                int seed = Int(entry, "seed");
                Assert.True(seeds.Add(seed), "duplicate seed sample: " + seed);
                Assert.Equal(seed % 4, Int(entry, "dealerSeat"));
                Assert.Equal(3, Int(entry, "redFiveCount"));
                if (Bool(entry, "threeCasterChanged"))
                {
                    changed++;
                }
            }

            // The V4 exporter loop is exactly seed 0..31. Length alone is not
            // completeness: 32 copies of seed 0, one missing seed or seed 32 must fail.
            // Set equality is order-independent, so reordering cases remains legal.
            Assert.Equal(32, seeds.Count);
            Assert.Equal(Enumerable.Range(0, 32), seeds.OrderBy(seed => seed));
            Assert.Equal(changed, Int(generation, "threeCasterChangedSeeds"));
        }

        // ------------------------------------------------------------------
        // PURE_MATH: slot matrix, demand vectors, overlap matrix, config snapshot
        // ------------------------------------------------------------------

        [Fact]
        public void SlotMatrixDemandVectorsOverlapAndConfigSnapshotMatchCSharp()
        {
            JsonElement root = Golden.Value.RootElement;

            JsonElement matrix = Arr(root, "slotMatrix", 16);
            var seen = new HashSet<(int Dealer, int Seat)>();
            foreach (JsonElement entry in matrix.EnumerateArray())
            {
                int dealer = Int(entry, "dealerSeat");
                int seat = Int(entry, "seat");
                Assert.InRange(dealer, 0, 3);
                Assert.InRange(seat, 0, 3);
                Assert.True(seen.Add((dealer, seat)), "duplicate slotMatrix dealer/seat entry");

                Assert.Equal(
                    ShuffleSlots.Initial(dealer, seat).ToList(),
                    IntArray(entry, "initialSlots").ToList());
                Assert.Equal(
                    ShuffleSlots.Draws(dealer, seat, 18).ToList(),
                    IntArray(entry, "drawSlots").ToList());
            }

            Assert.Equal(16, seen.Count);

            var targets = new Dictionary<string, ShuffleTarget>(StringComparer.Ordinal)
            {
                { "KOKUSHI", ShuffleTarget.Kokushi },
                { "CHINITSU_MAN", ShuffleTarget.ChinitsuMan },
                { "DAISANGEN", ShuffleTarget.Daisangen },
                { "DAISUUSHII", ShuffleTarget.Daisuushii },
                { "CHINITSU_SOU", ShuffleTarget.ChinitsuSou },
                { "CHINITSU_PIN", ShuffleTarget.ChinitsuPin }
            };

            JsonElement demandVectors = Obj(root, "demandVectors");
            Assert.Equal(targets.Count, demandVectors.EnumerateObject().Count());
            foreach (KeyValuePair<string, ShuffleTarget> entry in targets)
            {
                double[] expected = ShuffleTargets.Demand(entry.Value);
                double[] actual = DoubleArray(demandVectors.GetProperty(entry.Key), entry.Key, 34);
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i], actual[i], 12);
                }
            }

            JsonElement overlap = Obj(root, "overlapMatrix");
            Assert.Equal(targets.Count, overlap.EnumerateObject().Count());
            foreach (KeyValuePair<string, ShuffleTarget> a in targets)
            {
                JsonElement row = Obj(overlap, a.Key);
                Assert.Equal(targets.Count, row.EnumerateObject().Count());
                foreach (KeyValuePair<string, ShuffleTarget> b in targets)
                {
                    double expected = ShuffleTargets.Overlap(a.Value, b.Value);
                    double actual = Double(row, b.Key);
                    Assert.Equal(expected, actual, 12);
                    Assert.Equal(actual, Double(Obj(overlap, b.Key), a.Key), 12);
                }
            }

            ShuffleConfig config = new ShuffleConfig(enabled: true);
            JsonElement snapshot = Obj(root, "configSnapshot");
            AssertExactProperties(snapshot, ConfigSnapshotFields, "configSnapshot");
            Assert.True(Bool(snapshot, "enabled"));
            Assert.Equal(config.PlannedDraws, Int(snapshot, "plannedDraws"));
            Assert.Equal(config.Strength, Double(snapshot, "strength"), 12);
            Assert.Equal(config.Interference, Double(snapshot, "interference"), 12);
            Assert.Equal(config.DiversityInterference, Double(snapshot, "diversityInterference"), 12);
            Assert.Equal(config.DiversityBudgetPenalty, Double(snapshot, "diversityBudgetPenalty"), 12);

            AssertTierMap(Obj(snapshot, "tiers"), config.Tiers, "configSnapshot.tiers");
            AssertTierMap(Obj(snapshot, "mixedTiers"), config.MixedTiers, "configSnapshot.mixedTiers");

            JsonElement targetTiers = Obj(snapshot, "targetTiers");
            Assert.Equal(
                config.TargetTiers.Keys.OrderBy(target => (int)target).ToList(),
                targetTiers.EnumerateObject()
                    .Select(property => ParseTarget(property.Name))
                    .OrderBy(target => (int)target)
                    .ToList());
            foreach (KeyValuePair<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> entry in config.TargetTiers)
            {
                AssertTierMap(
                    Obj(targetTiers, ExportTargetName(entry.Key)),
                    entry.Value,
                    "configSnapshot.targetTiers." + entry.Key);
            }
        }

        // ------------------------------------------------------------------
        // SHAPE_OUTPUT: n=0 / n=4 / zero-budget identity paths
        // ------------------------------------------------------------------

        [Fact]
        public void ThirtyTwoCasesDriveRealShapeOnNoTargetFourCasterAndZeroBudgetIdentities()
        {
            foreach (FixtureCase fixture in Cases())
            {
                TileId[] baseTiles = ToTiles(fixture.BaseWall);
                List<SeatShufflePlan> noTargetPlans = Enumerable.Range(0, 4)
                    .Select(seat => new SeatShufflePlan(seat, new ShuffleSelection()))
                    .ToList();

                ShuffleSynthesisResult noTarget = ShuffleWallSynthesizer.Shape(
                    RecordId(fixture, "no-target"),
                    baseTiles,
                    Array.Empty<double>(),
                    fixture.DealerSeat,
                    noTargetPlans,
                    new ShuffleConfig(enabled: true));
                AssertSuccess(noTarget, "noTarget seed=" + fixture.Seed);
                Assert.Equal(0, noTarget.ActivePlanCount);
                Assert.Equal(0, noTarget.ConsumedRandomCount);
                int[] noTargetWall = Values(noTarget.Wall!);
                AssertWall(noTargetWall, "noTarget Shape seed=" + fixture.Seed);
                Assert.Equal(fixture.NoTargetWall, noTargetWall);
                Assert.Equal(fixture.BaseWall, noTargetWall);

                var fourPlans = fixture.Plans
                    .Select(plan => new SeatShufflePlan(plan.Seat, new ShuffleSelection(plan.Selection.Target)))
                    .ToList();
                fourPlans.Add(new SeatShufflePlan(3, new ShuffleSelection(ShuffleTarget.Daisuushii)));

                ShuffleSynthesisResult four = ShuffleWallSynthesizer.Shape(
                    RecordId(fixture, "four-caster"),
                    baseTiles,
                    Array.Empty<double>(),
                    fixture.DealerSeat,
                    fourPlans,
                    new ShuffleConfig(enabled: true));
                AssertSuccess(four, "fourCaster seed=" + fixture.Seed);
                Assert.Equal(4, four.ActivePlanCount);
                Assert.Equal(0, four.ConsumedRandomCount);
                int[] fourWall = Values(four.Wall!);
                AssertWall(fourWall, "fourCaster Shape seed=" + fixture.Seed);
                Assert.Equal(fixture.FourCasterWall, fourWall);
                Assert.Equal(fixture.BaseWall, fourWall);

                // V4's zero-budget path is identity, but it still shuffles the active
                // seat list and the initial/draw slot arrays before observing budget 0.
                // A legal explicit trace is therefore the vacuous zero sequence, not empty.
                int vacuousCount =
                    ShuffleSlots.Initial(fixture.DealerSeat, 0).Count - 1 +
                    ShuffleSlots.Draws(fixture.DealerSeat, 0, 18).Count - 1;
                Assert.True(vacuousCount > 0, "zero-budget shuffle must consume recorded values");

                var zeroPlans = new List<SeatShufflePlan>
                {
                    new SeatShufflePlan(0, new ShuffleSelection(ShuffleTarget.ChinitsuMan))
                };
                ShuffleConfig zeroConfig = new ShuffleConfig(enabled: true)
                    .WithTier(1, new ShuffleTier(1.0, 0, 0));

                ShuffleSynthesisResult zeroBudget = ShuffleWallSynthesizer.Shape(
                    RecordId(fixture, "zero-budget"),
                    baseTiles,
                    new double[vacuousCount],
                    fixture.DealerSeat,
                    zeroPlans,
                    zeroConfig);
                AssertSuccess(zeroBudget, "zeroBudget seed=" + fixture.Seed);
                Assert.Equal(1, zeroBudget.ActivePlanCount);
                Assert.Equal(vacuousCount, zeroBudget.ConsumedRandomCount);
                int[] zeroBudgetWall = Values(zeroBudget.Wall!);
                AssertWall(zeroBudgetWall, "zeroBudget Shape seed=" + fixture.Seed);
                Assert.Equal(fixture.ZeroBudgetWall, zeroBudgetWall);
                Assert.Equal(fixture.BaseWall, zeroBudgetWall);
            }
        }

        [Fact]
        public void ZeroBudgetIdentityRejectsEmptyTraceWithTraceExhausted()
        {
            FixtureCase fixture = Cases().First();
            TileId[] baseTiles = ToTiles(fixture.BaseWall);
            var zeroPlans = new List<SeatShufflePlan>
            {
                new SeatShufflePlan(0, new ShuffleSelection(ShuffleTarget.ChinitsuMan))
            };
            ShuffleConfig zeroConfig = new ShuffleConfig(enabled: true)
                .WithTier(1, new ShuffleTier(1.0, 0, 0));

            ShuffleSynthesisResult empty = ShuffleWallSynthesizer.Shape(
                RecordId(fixture, "zero-budget-empty"),
                baseTiles,
                Array.Empty<double>(),
                fixture.DealerSeat,
                zeroPlans,
                zeroConfig);

            Assert.False(empty.IsSuccess);
            Assert.Equal(ShuffleSynthesisErrorCode.TraceExhausted, empty.Error!.Code);
            Assert.Null(empty.Wall);
            Assert.Equal(0, empty.ConsumedRandomCount);
            Assert.Contains("recorded doubles exhausted", empty.Error.Message);
        }

        // ------------------------------------------------------------------
        // INTERNAL_EXPORTED_WALL_RELATION: dead-wall flag through TilePositionMap
        // ------------------------------------------------------------------

        [Fact]
        public void DeadWallSegmentBooleanMatchesCSharpTilePositionMapForAllCases()
        {
            foreach (FixtureCase fixture in Cases())
            {
                TileId[] baseTiles = ToTiles(fixture.BaseWall);
                TileId[] threeTiles = ToTiles(fixture.ThreeCasterWall);
                TilePositionMap baseMap = TilePositionMap.CreateWall(
                    RecordedRandomDraw.Create(RecordId(fixture, "base-map"), baseTiles));
                TilePositionMap threeMap = TilePositionMap.CreateWall(
                    RecordedRandomDraw.Create(RecordId(fixture, "three-map"), threeTiles));

                bool segmentEqual = true;
                for (int i = 0; i <= ShuffleSlots.DeadWallEnd - ShuffleSlots.DeadWallStart; i++)
                {
                    TileId baseTile = baseMap.TileAt(TilePosition.DeadWall(i));
                    TileId threeTile = threeMap.TileAt(TilePosition.DeadWall(i));
                    Assert.Equal(fixture.BaseWall[ShuffleSlots.DeadWallStart + i], baseTile.Value);
                    Assert.Equal(fixture.ThreeCasterWall[ShuffleSlots.DeadWallStart + i], threeTile.Value);
                    segmentEqual &= baseTile == threeTile;
                }

                // The exported flag is exactly "the two exported walls have the same
                // physical tiles in slots 122..135", mapped through C# DeadWall(0..13).
                Assert.Equal(fixture.DeadWallSegmentEqual, segmentEqual);
                Assert.Equal(
                    fixture.DeadWallSegmentEqual,
                    fixture.BaseWall.Skip(ShuffleSlots.DeadWallStart)
                        .SequenceEqual(fixture.ThreeCasterWall.Skip(ShuffleSlots.DeadWallStart)));

                // The dealt map must keep the same dead-wall identity and reveal the
                // dora indicator at C# DeadWall(4), i.e. exported wall slot 126.
                TilePositionMap dealt = TilePositionMap.CreateDealt(
                    RecordedRandomDraw.Create(RecordId(fixture, "three-dealt"), threeTiles),
                    fixture.DealerSeat);
                Assert.Equal(fixture.ThreeCasterWall[126], dealt.DoraIndicators[0].Value);
                Assert.Equal(
                    fixture.ThreeCasterWall[126],
                    dealt.TileAt(TilePosition.DeadWall(4)).Value);
            }
        }

        // ------------------------------------------------------------------
        // INTERNAL / SOURCE_ONLY relation booleans and red-five identity
        // ------------------------------------------------------------------

        [Fact]
        public void ExportedRelationBooleansAreConsumedAtTheirDeclaredStrength()
        {
            foreach (FixtureCase fixture in Cases())
            {
                Assert.Equal(
                    fixture.ThreeCasterChanged,
                    !fixture.BaseWall.SequenceEqual(fixture.ThreeCasterWall));
                Assert.Equal(
                    fixture.FourCasterIdentity,
                    fixture.BaseWall.SequenceEqual(fixture.FourCasterWall));
                Assert.Equal(
                    fixture.DeadWallSegmentEqual,
                    fixture.BaseWall.Skip(ShuffleSlots.DeadWallStart)
                        .SequenceEqual(fixture.ThreeCasterWall.Skip(ShuffleSlots.DeadWallStart)));

                // SOURCE_ONLY: V4 asserted "three == reversed" before writing the file.
                // The reversed wall itself is not exported, so this is not a C# binding.
                Assert.True(
                    fixture.ReversedEqualsThree,
                    "source-only exporter invariant reversedEqualsThree must be true");

                Assert.Equal(new[] { 0, 1, 2 }, fixture.Plans.Select(plan => plan.Seat).OrderBy(seat => seat).ToArray());
                Assert.Equal(fixture.RedFiveCount, fixture.ThreeCasterWall.Count(value => new TileId(value).IsRedFive));
                Assert.Equal(fixture.RedFiveCount, fixture.BaseWall.Count(value => new TileId(value).IsRedFive));
                Assert.Equal(fixture.RedFiveCount, fixture.FourCasterWall.Count(value => new TileId(value).IsRedFive));
                Assert.Equal(fixture.RedFiveCount, fixture.NoTargetWall.Count(value => new TileId(value).IsRedFive));
                Assert.Equal(fixture.RedFiveCount, fixture.ZeroBudgetWall.Count(value => new TileId(value).IsRedFive));
            }
        }

        [Fact]
        public void AllThirtyTwoCasesExposeConservedWallsAndSourceRedFiveCount()
        {
            foreach (FixtureCase fixture in Cases())
            {
                AssertWall(fixture.BaseWall, "baseWall seed=" + fixture.Seed);
                AssertWall(fixture.ThreeCasterWall, "threeCasterWall seed=" + fixture.Seed);
                AssertWall(fixture.FourCasterWall, "fourCasterWall seed=" + fixture.Seed);
                AssertWall(fixture.NoTargetWall, "noTargetWall seed=" + fixture.Seed);
                AssertWall(fixture.ZeroBudgetWall, "zeroBudgetWall seed=" + fixture.Seed);

                Assert.Equal(fixture.RedFiveCount, fixture.BaseWall.Count(value => new TileId(value).IsRedFive));
            }
        }

        // ------------------------------------------------------------------
        // Fixture parsing helpers. Missing, wrong-typed and unknown values throw:
        // they never fall back to a valid default.
        // ------------------------------------------------------------------

        private static IEnumerable<FixtureCase> Cases()
        {
            JsonElement cases = Arr(Golden.Value.RootElement, "cases", 32);
            foreach (JsonElement entry in cases.EnumerateArray())
            {
                yield return ParseCase(entry);
            }
        }

        private static FixtureCase ParseCase(JsonElement entry)
        {
            var parsed = new FixtureCase
            {
                Seed = Int(entry, "seed"),
                DealerSeat = Int(entry, "dealerSeat"),
                BaseWall = IntArray(entry, "baseWall", 136),
                ThreeCasterWall = IntArray(entry, "threeCasterWall", 136),
                FourCasterWall = IntArray(entry, "fourCasterWall", 136),
                NoTargetWall = IntArray(entry, "noTargetWall", 136),
                ZeroBudgetWall = IntArray(entry, "zeroBudgetWall", 136),
                RedFiveCount = Int(entry, "redFiveCount"),
                DeadWallSegmentEqual = Bool(entry, "deadWallSegmentEqual"),
                ThreeCasterChanged = Bool(entry, "threeCasterChanged"),
                FourCasterIdentity = Bool(entry, "fourCasterIdentity"),
                ReversedEqualsThree = Bool(entry, "reversedEqualsThree")
            };

            JsonElement plans = Arr(entry, "plans", 3);
            var parsedPlans = new List<SeatShufflePlan>(3);
            foreach (JsonElement plan in plans.EnumerateArray())
            {
                int seat = Int(plan, "seat");
                ShuffleTarget target = ParseTarget(Str(plan, "target"));
                parsedPlans.Add(new SeatShufflePlan(seat, new ShuffleSelection(target)));
            }

            parsed.Plans = parsedPlans;
            return parsed;
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
                    throw new InvalidOperationException("unknown shuffle target: " + name);
            }
        }

        private static TileId[] ToTiles(IReadOnlyList<int> values)
        {
            var tiles = new TileId[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                tiles[i] = new TileId(values[i]);
            }

            return tiles;
        }

        private static int[] Values(RecordedRandomDraw wall)
        {
            var values = new int[wall.WallOrder.Count];
            for (int i = 0; i < wall.WallOrder.Count; i++)
            {
                values[i] = wall.WallOrder[i].Value;
            }

            return values;
        }

        private static string RecordId(FixtureCase fixture, string path)
        {
            return "shuffle-golden-" + fixture.Seed.ToString(CultureInfo.InvariantCulture) + "-" + path;
        }

        private static void AssertSuccess(ShuffleSynthesisResult result, string context)
        {
            string detail = result.Error == null ? "no error" : result.Error.ToString();
            Assert.True(result.IsSuccess, context + " must shape successfully: " + detail);
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
        }

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

        private static double Double(JsonElement parent, string name)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result))
            {
                throw new InvalidOperationException(name + " must be a JSON double, got " + value.ValueKind + ".");
            }

            return result;
        }

        private static int[] IntArray(JsonElement parent, string name, int? expectedLength = null)
        {
            JsonElement value = parent.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(name + " must be a JSON array, got " + value.ValueKind + ".");
            }

            if (expectedLength.HasValue && value.GetArrayLength() != expectedLength.Value)
            {
                throw new InvalidOperationException(
                    name + " must contain " + expectedLength.Value.ToString(CultureInfo.InvariantCulture) +
                    " entries, got " + value.GetArrayLength().ToString(CultureInfo.InvariantCulture) + ".");
            }

            var result = new int[value.GetArrayLength()];
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

        private static double[] DoubleArray(JsonElement value, string name, int expectedLength)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != expectedLength)
            {
                throw new InvalidOperationException(name + " must be a JSON array of length " + expectedLength + ".");
            }

            var result = new double[expectedLength];
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double parsed))
                {
                    throw new InvalidOperationException(
                        name + "[" + index.ToString(CultureInfo.InvariantCulture) + "] must be a JSON number.");
                }

                result[index] = parsed;
                index++;
            }

            return result;
        }

        private static JsonDocument LoadGolden()
        {
            return JsonDocument.Parse(File.ReadAllText(FindGoldenPath()));
        }

        private static string? FindRepositoryRoot()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, ExporterPath)) &&
                        Directory.Exists(Path.Combine(directory.FullName, V4RulesSourcePath)))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            return null;
        }

        private static string FindGoldenPath([CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(overrideRoot!, "tests", "v5-golden", "shuffle-v1.json");
                if (File.Exists(overridden))
                {
                    return overridden;
                }

                throw new FileNotFoundException(
                    "MAHJONG_REPO_ROOT is set but does not contain tests/v5-golden/shuffle-v1.json: " + overrideRoot);
            }

            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(sourceFile) ?? string.Empty })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(directory.FullName, "tests", "v5-golden", "shuffle-v1.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException(
                "shuffle-v1.json not found under " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() + "; set MAHJONG_REPO_ROOT if the test binaries live outside the repository.");
        }

        private sealed class FixtureCase
        {
            internal int Seed { get; set; }

            internal int DealerSeat { get; set; }

            internal IReadOnlyList<SeatShufflePlan> Plans { get; set; } = Array.Empty<SeatShufflePlan>();

            internal int[] BaseWall { get; set; } = Array.Empty<int>();

            internal int[] ThreeCasterWall { get; set; } = Array.Empty<int>();

            internal int[] FourCasterWall { get; set; } = Array.Empty<int>();

            internal int[] NoTargetWall { get; set; } = Array.Empty<int>();

            internal int[] ZeroBudgetWall { get; set; } = Array.Empty<int>();

            internal int RedFiveCount { get; set; }

            internal bool DeadWallSegmentEqual { get; set; }

            internal bool ThreeCasterChanged { get; set; }

            internal bool FourCasterIdentity { get; set; }

            internal bool ReversedEqualsThree { get; set; }
        }
    }
}

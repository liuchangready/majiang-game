using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Ai;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// N3-B6 companion-fixture consumer. Reads tests/v5-golden/ai-discard-flags-v1.json,
    /// which is generated only by the V4 test-only exporter
    /// (engine/src/test/kotlin/com/majiang/state/V5AiDiscardFlagsGoldenExporterTest.kt)
    /// from real V4 domain play: every river entry and claimed discard in the fixture was
    /// extracted from the published GameEvent stream, the public PlayerView projection and
    /// PlayerHand.discardHistory — never hand-written.
    ///
    /// This test consumes the fixture values through the official public API only
    /// (AiVisibleDiscard / AiVisibleMeld / AiObservation constructors and properties) and
    /// asserts the flag SEMANTICS, not fixture echoes:
    ///   - active-river entries must carry IsClaimed == false (V4 removes claimed tiles),
    ///   - claimed discards exist outside the river with IsClaimed == true and must never
    ///     appear in any river, including the riichi discard that was claimed (both flags true),
    ///   - exactly the riichi declaration discard carries IsRiichi == true on the river.
    /// The optional MAHJONG_AI_DISCARD_FLAGS_GOLDEN override points the same compiled tests
    /// at an isolated TEMP copy for mutation probes; a set-but-missing path is a hard failure.
    /// No Mahjong.Ai production type is modified.
    /// </summary>
    public class AiDiscardFlagsGoldenTests
    {
        private const string OverrideVariable = "MAHJONG_AI_DISCARD_FLAGS_GOLDEN";
        private const string FixtureName = "ai-discard-flags-v1.json";

        private static JsonDocument LoadFixture()
        {
            string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            string path;
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                path = Path.GetFullPath(overridden!);
                Assert.True(File.Exists(path), OverrideVariable + " points to a missing fixture: " + path);
            }
            else
            {
                path = Path.Combine(AppContext.BaseDirectory, FixtureName);
            }

            Assert.True(File.Exists(path), "Missing golden fixture: " + path);
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        private static JsonElement CaseById(JsonElement root, string caseId)
        {
            foreach (JsonElement testCase in root.GetProperty("cases").EnumerateArray())
            {
                if (Text(testCase, "caseId") == caseId)
                {
                    return testCase;
                }
            }

            throw new Xunit.Sdk.XunitException("fixture is missing case " + caseId);
        }

        private static List<JsonElement> River(JsonElement testCase) =>
            testCase.GetProperty("river").EnumerateArray().ToList();

        private static List<JsonElement> Claimed(JsonElement testCase) =>
            testCase.GetProperty("claimedDiscards").EnumerateArray().ToList();

        private static List<AiVisibleDiscard> BuildRiver(JsonElement testCase)
        {
            // Consumption chain: fixture values enter C# only through the public constructor.
            return River(testCase)
                .Select(entry => new AiVisibleDiscard(
                    (int)Int64(entry, "seat"),
                    new TileId((int)Int64(entry, "tileId")),
                    Bool(entry, "isRiichi"),
                    Bool(entry, "isClaimed")))
                .ToList();
        }

        private static List<AiVisibleMeld> BuildMelds(JsonElement testCase)
        {
            return testCase.GetProperty("publicMelds").EnumerateArray()
                .Select(meld => new AiVisibleMeld(
                    (int)Int64(meld, "seat"),
                    meld.GetProperty("tileIds").EnumerateArray()
                        .Select(id => new TileId((int)id.GetInt64()))
                        .ToList()))
                .ToList();
        }

        private static AiObservation BuildObservation(
            JsonElement testCase,
            IReadOnlyList<AiVisibleDiscard> river,
            IReadOnlyList<AiVisibleMeld> melds)
        {
            int viewer = (int)Int64(testCase, "viewerSeat");
            return new AiObservation(
                sessionId: "ai-discard-flags-v1",
                decisionKey: new DecisionKey(
                    "ai-discard-flags-v1", viewer, "discard-flags-" + Text(testCase, "caseId")),
                controlEpoch: 1,
                stateVersion: 1,
                decisionDeadlineMillis: 1000,
                phase: RoundPhase.Discard,
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                riichiSticks: (int)Int64(testCase, "riichiSticks"),
                remainingWallCount: 55,
                myHandTileIds: Enumerable.Range(100, 13).Select(id => new TileId(id)).ToList(),
                myDrawnTileId: null,
                myMelds: new List<AiVisibleMeld>(),
                visibleMelds: melds,
                visibleDiscards: river,
                visibleDoraIndicators: new List<TileId>(),
                scores: testCase.GetProperty("postCaseScores").EnumerateArray()
                    .Select(x => (long)x.GetInt32()).ToList(),
                legalActions: new List<LegalActionOption> { new LegalActionOption(LegalActionKind.Discard, viewer) });
        }

        [Fact]
        public void FixtureHasProvenanceAndTheThreeDerivedCases()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            Assert.Equal("ai-discard-flags-v1", Text(root, "fixture"));
            Assert.Equal(1L, Int64(root, "version"));

            JsonElement provenance = root.GetProperty("provenance");
            Assert.Equal(
                "engine/src/test/kotlin/com/majiang/state/V5AiDiscardFlagsGoldenExporterTest.kt",
                Text(provenance, "generator"));
            Assert.Equal(0L, Int64(provenance, "nativeExitCode"));
            Assert.Equal(40, Text(provenance, "generatedAtHead").Length);
            Assert.Equal(64, Text(provenance, "renderSha256").Length);
            Assert.Contains(
                Text(provenance, "trackedDirtyClass"),
                new[] { "clean", "test-only", "mixed" });

            List<string> hashedPaths = provenance.GetProperty("sourceHashes").EnumerateArray()
                .Select(entry => Text(entry, "path"))
                .ToList();
            Assert.Contains("engine/src/main/kotlin/com/majiang/session/PlayerView.kt", hashedPaths);
            Assert.Contains("dotnet/src/Mahjong.Ai/AiObservation.cs", hashedPaths);
            Assert.Contains("dotnet/src/Mahjong.TableRuntime/TableActor.cs", hashedPaths);
            Assert.Equal(64, Text(hashedEntry(provenance, "engine/src/main/kotlin/com/majiang/event/GameEvent.kt"), "sha256").Length);

            List<string> caseIds = root.GetProperty("cases").EnumerateArray()
                .Select(testCase => Text(testCase, "caseId"))
                .ToList();
            Assert.Equal(new[] { "riichi-river", "claimed-discard", "riichi-discard-claimed" }, caseIds);
        }

        private static JsonElement hashedEntry(JsonElement provenance, string path)
        {
            foreach (JsonElement entry in provenance.GetProperty("sourceHashes").EnumerateArray())
            {
                if (Text(entry, "path") == path)
                {
                    return entry;
                }
            }

            throw new Xunit.Sdk.XunitException("sourceHashes is missing " + path);
        }

        /// <summary>
        /// The flag contract itself. These are semantic assertions with a causal direction
        /// (river false / claimed true / exactly one riichi discard), so flipping either flag
        /// in a TEMP fixture copy must fail here with a non-empty enumeration.
        /// </summary>
        [Fact]
        public void RiverAndClaimedFlagValuesFollowTheV4DiscardSemantics()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            JsonElement riichiRiver = CaseById(root, "riichi-river");
            List<JsonElement> river = River(riichiRiver);
            Assert.True(river.Count >= 5, "riichi-river must export a non-empty go-around river");

            List<AiVisibleDiscard> instances = BuildRiver(riichiRiver);
            Assert.Equal(river.Count, instances.Count);
            Assert.True(instances.All(discard => !discard.IsClaimed), "no active-river entry may be claimed");
            Assert.Equal(1, instances.Count(discard => discard.IsRiichi));
            AiVisibleDiscard riichiDiscard = instances.Single(discard => discard.IsRiichi);
            Assert.Equal(0, riichiDiscard.Seat);
            Assert.Equal(0, (int)Int64(River(riichiRiver)[instances.IndexOf(riichiDiscard)], "riverIndex"));

            List<bool> riichiStatus = riichiRiver.GetProperty("riichiStatusBySeat").EnumerateArray()
                .Select(x => x.GetBoolean()).ToList();
            Assert.Equal(new[] { true, false, false, false }, riichiStatus);
            Assert.Empty(Claimed(riichiRiver));

            JsonElement claimedCase = CaseById(root, "claimed-discard");
            List<JsonElement> claimed = Claimed(claimedCase);
            Assert.Single(claimed);
            AiVisibleDiscard claimedInstance = new AiVisibleDiscard(
                (int)Int64(claimed[0], "seat"),
                new TileId((int)Int64(claimed[0], "tileId")),
                Bool(claimed[0], "isRiichi"),
                Bool(claimed[0], "isClaimed"));
            Assert.True(claimedInstance.IsClaimed, "a ponned discard must consume as isClaimed=true");
            Assert.False(claimedInstance.IsRiichi, "the ponned discard in this case is not a riichi discard");
            Assert.True(Bool(claimed[0], "absentFromActiveRiver"));
            Assert.True(Bool(claimed[0], "presentInDiscardHistory"));
            Assert.DoesNotContain(
                (int)Int64(claimed[0], "tileId"),
                River(claimedCase).Select(entry => (int)Int64(entry, "tileId")));
            Assert.True(River(claimedCase).Count >= 1, "claimed-discard must still export a non-empty river");
            Assert.True(
                River(claimedCase).All(entry => !Bool(entry, "isClaimed")),
                "every active-river entry must be isClaimed=false");

            JsonElement bothCase = CaseById(root, "riichi-discard-claimed");
            List<JsonElement> bothClaimed = Claimed(bothCase);
            Assert.Single(bothClaimed);
            Assert.True(Bool(bothClaimed[0], "isClaimed"), "the ponned riichi discard must be isClaimed=true");
            Assert.True(Bool(bothClaimed[0], "isRiichi"), "the ponned riichi discard must be isRiichi=true");
            Assert.DoesNotContain(
                (int)Int64(bothClaimed[0], "tileId"),
                River(bothCase).Select(entry => (int)Int64(entry, "tileId")));
        }

        [Fact]
        public void AiObservationConsumesRiverFlagsThroughThePublicProjectionSurface()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            foreach (JsonElement testCase in root.GetProperty("cases").EnumerateArray())
            {
                List<AiVisibleDiscard> river = BuildRiver(testCase);
                List<AiVisibleMeld> melds = BuildMelds(testCase);
                AiObservation observation = BuildObservation(testCase, river, melds);

                // The observation must expose exactly the fixture's per-discard flag values.
                List<JsonElement> riverEntries = River(testCase);
                Assert.Equal(riverEntries.Count, observation.VisibleDiscards.Count);
                for (int i = 0; i < riverEntries.Count; i++)
                {
                    Assert.Equal((int)Int64(riverEntries[i], "seat"), observation.VisibleDiscards[i].Seat);
                    Assert.Equal((int)Int64(riverEntries[i], "tileId"), observation.VisibleDiscards[i].Tile.Value);
                    Assert.Equal(Bool(riverEntries[i], "isRiichi"), observation.VisibleDiscards[i].IsRiichi);
                    Assert.Equal(Bool(riverEntries[i], "isClaimed"), observation.VisibleDiscards[i].IsClaimed);
                }

                List<JsonElement> meldEntries = testCase.GetProperty("publicMelds").EnumerateArray().ToList();
                Assert.Equal(meldEntries.Count, observation.VisibleMelds.Count);

                // A river riichi discard may only belong to a seat whose riichi status is true.
                foreach (AiVisibleDiscard discard in observation.VisibleDiscards)
                {
                    if (discard.IsRiichi)
                    {
                        Assert.True(
                            testCase.GetProperty("riichiStatusBySeat")[discard.Seat].GetBoolean(),
                            "a riichi discard must belong to a seat that declared riichi");
                    }
                }
            }
        }

        [Fact]
        public void ClaimedDiscardsStayOutsideEveryObservationRiver()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            foreach (string caseId in new[] { "claimed-discard", "riichi-discard-claimed" })
            {
                JsonElement testCase = CaseById(root, caseId);
                AiObservation observation = BuildObservation(testCase, BuildRiver(testCase), BuildMelds(testCase));

                foreach (JsonElement claimed in Claimed(testCase))
                {
                    int claimedTileId = (int)Int64(claimed, "tileId");
                    Assert.DoesNotContain(
                        observation.VisibleDiscards,
                        discard => discard.Tile.Value == claimedTileId);
                    // Standalone consumption through the official public constructor.
                    AiVisibleDiscard standalone = new AiVisibleDiscard(
                        (int)Int64(claimed, "seat"),
                        new TileId(claimedTileId),
                        Bool(claimed, "isRiichi"),
                        isClaimed: true);
                    Assert.True(standalone.IsClaimed);
                    Assert.Equal(Bool(claimed, "isRiichi"), standalone.IsRiichi);
                }
            }

            // The claimed riichi discard carries BOTH flags true — the strongest combination.
            JsonElement bothCase = CaseById(root, "riichi-discard-claimed");
            JsonElement bothClaimed = Claimed(bothCase).Single();
            Assert.True(Bool(bothClaimed, "isRiichi") && Bool(bothClaimed, "isClaimed"));
            Assert.Equal(
                new[] { true, false, false, false },
                bothCase.GetProperty("riichiStatusBySeat").EnumerateArray().Select(x => x.GetBoolean()).ToArray());
            Assert.DoesNotContain(
                River(bothCase),
                entry => (int)Int64(entry, "seat") == 0);
        }

        [Fact]
        public void ObservationDefensivelyCopiesDiscardsAndStaysReadOnly()
        {
            using JsonDocument document = LoadFixture();
            JsonElement testCase = CaseById(document.RootElement, "riichi-river");

            List<AiVisibleDiscard> source = BuildRiver(testCase);
            int count = source.Count;
            AiObservation observation = BuildObservation(testCase, source, BuildMelds(testCase));

            source.Clear();
            source.Add(new AiVisibleDiscard(0, new TileId(134), isRiichi: true, isClaimed: true));
            Assert.Equal(count, observation.VisibleDiscards.Count);
            Assert.DoesNotContain(observation.VisibleDiscards, discard => discard.Tile.Value == 134);
            Assert.DoesNotContain(observation.VisibleDiscards, discard => discard.IsRiichi && discard.IsClaimed);

            Assert.Throws<NotSupportedException>(
                () => ((ICollection<AiVisibleDiscard>)observation.VisibleDiscards)
                    .Add(new AiVisibleDiscard(0, new TileId(135))));
        }

        [Fact]
        public void FixtureAndPublicSurfaceExposeOnlyPublicDiscardInformation()
        {
            using JsonDocument document = LoadFixture();
            JsonElement root = document.RootElement;

            var forbiddenPropertyNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "wall", "wallOrder", "wallTiles", "wallPrefix", "deadWall", "seed",
                "sleeve", "sleeveTiles", "closedTiles", "handTileIds", "drawnTileId",
                "uraDora", "hiddenTiles", "tilePositionMap"
            };
            var offenders = new List<string>();
            CollectPropertyNames(root, string.Empty, offenders);
            Assert.DoesNotContain(offenders, name => forbiddenPropertyNames.Contains(name));

            // The public C# surface must not offer a path to hidden information either.
            string[] discardProperties = typeof(AiVisibleDiscard).GetProperties()
                .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "IsClaimed", "IsRiichi", "Seat", "Tile" }, discardProperties);

            List<string> observationProperties = typeof(AiObservation).GetProperties()
                .Select(property => property.Name).ToList();
            Assert.DoesNotContain(
                observationProperties,
                name => name.Contains("Sleeve") || name.Contains("Seed") || name.Contains("Ura") ||
                        name.Contains("DeadWall") || name.Contains("ClosedTiles"));
            Assert.All(
                observationProperties.Where(name => name.Contains("Wall")),
                name => Assert.Equal("RemainingWallCount", name));
        }

        private static void CollectPropertyNames(JsonElement element, string prefix, List<string> names)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        names.Add(property.Name);
                        CollectPropertyNames(property.Value, prefix + "." + property.Name, names);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        CollectPropertyNames(item, prefix, names);
                    }

                    break;
            }
        }

        private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

        private static long Int64(JsonElement element, string name) => element.GetProperty(name).GetInt64();

        private static bool Bool(JsonElement element, string name) => element.GetProperty(name).GetBoolean();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// P0.1-a/P0.1-b visibility binding for G-057a..G-057d.
    ///
    /// The fixture <c>tests/v5-golden/visibility-v1.json</c> is generated only by the V4
    /// exporter <c>V5VisibilityGoldenExporterTest</c>; every expected value in it is an
    /// actual V4 call result. This test rebuilds a real V5 136-entity round from the
    /// exported placement input and calls the existing production projections:
    /// <see cref="TableVisibilityProjection.ProjectPlayerSnapshot"/>,
    /// <see cref="TableVisibilityProjection.ProjectPublicSnapshot"/>,
    /// <see cref="TableVisibilityProjection.ProjectOwnerPrivate"/> and
    /// <see cref="TableEventProjection.ProjectMany"/>. It never feeds a V4 object into a
    /// V5 API and never copies a V5 expected value back out of the fixture.
    ///
    /// Known semantic gaps (legal actions, ScoreBreakdown, closed-kan identities, session
    /// summary shape, sleeve representation) are listed in the fixture's
    /// <c>knownDivergences</c> block and are NOT counted as aligned fields.
    /// </summary>
    public sealed class VisibilityGoldenTests
    {
        private const string Schema = "v5-visibility-golden-v1";
        private const string BaselineCommit = "1432d775526de921f2cde3aba283236fc3b8e8c1";

        private static readonly Lazy<JsonDocument> Golden = new Lazy<JsonDocument>(LoadGolden);

        private static readonly string[] ExpectedCaseIds =
        {
            "VIS-G057A-SNAPSHOT-COPY",
            "VIS-G057B-EVENT-VISIBILITY",
            "VIS-G057C-PUBLIC-PROJECTION",
            "VIS-G057D-SEAT-VIEW-ISOLATION"
        };

        private static readonly Dictionary<string, string[]> ExpectedGRows =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "VIS-G057A-SNAPSHOT-COPY", new[] { "G-057a" } },
                { "VIS-G057B-EVENT-VISIBILITY", new[] { "G-057b" } },
                { "VIS-G057C-PUBLIC-PROJECTION", new[] { "G-057c" } },
                { "VIS-G057D-SEAT-VIEW-ISOLATION", new[] { "G-057d" } }
            };

        private static readonly string[] ExpectedTaskPaths =
        {
            "doc/v5-重构/V5-P01-VISIBILITY-GOLDEN-G057.md",
            "dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs",
            "engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt",
            "tests/v5-golden/visibility-v1.json",
            "tools/v5-golden/probe-visibility-binding.ps1"
        };

        private static readonly string[] ExpectedDivergenceIds =
        {
            "VG-D01", "VG-D02", "VG-D03", "VG-D04", "VG-D05", "VG-D06", "VG-D07", "VG-D08"
        };

        private static readonly string[] RequiredFieldClasses =
        {
            "INPUT", "OUTPUT", "STRUCTURE", "META", "SOURCE_ONLY", "UNREPRESENTABLE"
        };

        private static readonly string[] RequiredSourceFiles =
        {
            "engine/src/main/kotlin/com/majiang/session/PlayerView.kt",
            "engine/src/main/kotlin/com/majiang/session/SessionUpdate.kt",
            "engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt",
            "engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt",
            "engine/src/main/kotlin/com/majiang/event/GameEvent.kt",
            "engine/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt",
            "engine/src/main/kotlin/com/majiang/state/GameState.kt",
            "engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt",
            "engine/src/test/kotlin/com/majiang/session/SessionSnapshotContractTest.kt",
            "engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt",
            "dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs",
            "dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs",
            "dotnet/src/Mahjong.TableRuntime/TableVisibilityContexts.cs",
            "dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs",
            "dotnet/src/Mahjong.Domain/TilePositionMap.cs",
            "dotnet/src/Mahjong.Domain/RoundStateMachine.cs",
            "dotnet/src/Mahjong.Domain/RoundTypes.cs"
        };

        private static readonly string[] SupportedGenerationCommands =
        {
            "$env:V5_VISIBILITY_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test --tests \"com.majiang.projection.V5VisibilityGoldenExporterTest\" --rerun-tasks --console=plain",
            "V5_VISIBILITY_GOLDEN_EXPORT=1 ./gradlew :engine:test --tests \"com.majiang.projection.V5VisibilityGoldenExporterTest\" --rerun-tasks --console=plain"
        };

        [Fact]
        public void GoldenMetaProvenanceAndCaseIdentityAreReal()
        {
            JsonElement root = Golden.Value.RootElement;
            Assert.Equal(Schema, Str(root, "schema"));
            Assert.Equal(1, Int(root, "generatorVersion"));
            Assert.Equal(1, Int(root, "rulesetVersion"));
            Assert.Equal("exported", Str(root, "extractionStatus"));
            Assert.Equal(4, Int(root, "caseCount"));
            Assert.False(Bool(root, "clockUsed"));
            Assert.False(Bool(root, "implicitRandomUsed"));
            Assert.Equal(BaselineCommit, Str(root, "v5BaselineCommit"));

            string commit = Str(root, "v4SourceCommit");
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(Uri.IsHexDigit), "v4SourceCommit must be 40 hex characters");

            Assert.True(Bool(root, "v4ProductionTreeClean"));
            Assert.Empty(Strings(root.GetProperty("trackedProductionModifiedFiles")));
            foreach (string modified in Strings(root.GetProperty("trackedModifiedFiles")))
            {
                Assert.Contains(modified, ExpectedTaskPaths);
            }

            Assert.Equal(
                ExpectedTaskPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                Strings(root.GetProperty("taskPaths")).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            Assert.Equal(
                new[] { "G-057a", "G-057b", "G-057c", "G-057d" },
                Strings(root.GetProperty("targetIds")));

            string command = Str(root, "generatedByCommand").Trim();
            Assert.True(
                SupportedGenerationCommands.Contains(command, StringComparer.Ordinal),
                "generatedByCommand is not the supported explicit export form: " + command);

            JsonElement generation = root.GetProperty("generationResult");
            Assert.Equal(4, Int(generation, "casesExported"));
            Assert.Equal(4, Int(generation, "stateCases"));
            Assert.Equal(0, Int(generation, "failures"));
            Assert.Equal("PASSED", Str(generation, "exporterTestResult"));
            Assert.True(Bool(generation, "secondRenderIdentical"));

            JsonElement scope = root.GetProperty("scope");
            Assert.Equal(4, Int(scope, "manifestEntriesTargeted"));
            Assert.Equal(4, Int(scope, "manifestEntriesExportedByThisRun"));
            Assert.False(Bool(scope, "fullManifestAudited"));
            Assert.Contains("not a full 73-case audit", Str(scope, "note"));

            JsonElement cases = root.GetProperty("cases");
            Assert.Equal(4, cases.GetArrayLength());
            string[] ids = cases.EnumerateArray().Select(value => Str(value, "id")).ToArray();
            Assert.Equal(ExpectedCaseIds, ids);
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
            foreach (JsonElement item in cases.EnumerateArray())
            {
                string id = Str(item, "id");
                Assert.Equal(ExpectedGRows[id], Strings(item.GetProperty("gRows")));
                Assert.NotEmpty(Strings(item.GetProperty("sourceAnchors")));
                Assert.False(Bool(item.GetProperty("input"), "clockUsed"));
                Assert.False(Bool(item.GetProperty("input"), "implicitRandomUsed"));
            }

            foreach (string category in RequiredFieldClasses)
            {
                Assert.True(
                    root.GetProperty("fieldClassification").TryGetProperty(category, out JsonElement values),
                    "missing field classification " + category);
                Assert.NotEmpty(Strings(values));
            }

            JsonElement divergences = root.GetProperty("knownDivergences");
            string[] divergenceIds = divergences.EnumerateArray()
                .Select(value => Str(value, "id")).ToArray();
            Assert.Equal(ExpectedDivergenceIds, divergenceIds);
            foreach (JsonElement divergence in divergences.EnumerateArray())
            {
                Assert.NotEmpty(Str(divergence, "severity"));
                Assert.NotEmpty(Str(divergence, "location"));
                Assert.NotEmpty(Str(divergence, "owner"));
                Assert.NotEmpty(Str(divergence, "status"));
            }

            Assert.Contains(
                divergences.EnumerateArray(),
                value => Str(value, "id") == "VG-D01" && Str(value, "status").StartsWith("BLOCKED", StringComparison.Ordinal));
            Assert.Contains(
                divergences.EnumerateArray(),
                value => Str(value, "id") == "VG-D06" && Str(value, "status").StartsWith("BLOCKED", StringComparison.Ordinal));
            Assert.Contains(
                divergences.EnumerateArray(),
                value => Str(value, "id") == "VG-D03" && Str(value, "status").Contains("UNREPRESENTABLE", StringComparison.Ordinal));

            VerifyRenderHash(root);
            VerifySourceHashes(root);
        }

        [Fact]
        public void G057aSnapshotDeepCopyMatchesV4()
        {
            JsonElement item = Case("VIS-G057A-SNAPSHOT-COPY");
            JsonElement stateJson = item.GetProperty("input").GetProperty("state");
            Scenario scenario = BuildScenario(stateJson);
            CheatState cheat = V32Fixtures.Cheat();
            TableProjectionContext context = scenario.Context(new TableProjectionPolicy());
            PlayerSnapshotView view = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, context);

            AssertSeatView(item.GetProperty("expected").GetProperty("snapshot"), view);

            // The fixture's expected snapshot was captured after the V4 source state and the
            // producer-owned meld/legal-action lists were mutated. The V4 view and the V5 view
            // must agree on that captured value.
            List<uint> before = view.Private.ClosedTiles.ToList();
            scenario.Placements.Clear();
            scenario.Scores[0] = 1;
            scenario.Dora[0] = 135;
            PlayerSnapshotView replayed = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, scenario.Context(new TableProjectionPolicy()));
            Assert.Equal(before, replayed.Private.ClosedTiles);
            Assert.Equal(25000L, replayed.Public.Board!.Scores[0].Score);

            JsonElement expectedSnapshot = item.GetProperty("expected").GetProperty("snapshot");
            Assert.Equal(
                Ints(expectedSnapshot.GetProperty("scores")).Select(value => (long)value),
                replayed.Public.Board!.Scores.Select(value => value.Score));
        }

        [Fact]
        public void G057bEventVisibilityMatchesV4()
        {
            JsonElement item = Case("VIS-G057B-EVENT-VISIBILITY");
            JsonElement input = item.GetProperty("input");
            List<TableEventProjectionInput> unauthorized = BuildEventInputs(input.GetProperty("events"));
            List<TableEventProjectionInput> authorized = BuildEventInputs(input.GetProperty("authorizedEvents"));

            JsonElement visibility = item.GetProperty("expected").GetProperty("visibility");
            for (int seat = 0; seat < 4; seat++)
            {
                TableProjectionAudience audience = TableProjectionAudience.ForSeat(seat);
                IReadOnlyList<TableProjectedEvent> actualUnauthorized = TableEventProjection.ProjectMany(
                    unauthorized, audience, new TableProjectionPolicy(false, false, false));
                AssertEventList(
                    visibility.GetProperty("unauthorized").GetProperty(seat.ToString(CultureInfo.InvariantCulture)),
                    actualUnauthorized);

                IReadOnlyList<TableProjectedEvent> actualAuthorized = TableEventProjection.ProjectMany(
                    authorized, audience, new TableProjectionPolicy(false, true, true));
                AssertEventList(
                    visibility.GetProperty("settlementAuthorized").GetProperty(seat.ToString(CultureInfo.InvariantCulture)),
                    actualAuthorized);
            }

            // V5-only stricter policy (VG-D01): CheatPointsChanged is private by type.
            uint[] mutableTiles = Array.Empty<uint>();
            var cheatPoints = new TableEventProjectionInput(
                ProtocolEventType.CheatPointsChanged,
                new EventSeq(1),
                new StateVersion(1),
                0,
                0,
                mutableTiles,
                cheatAmount: -10,
                cheatBalance: 990);
            Assert.Empty(cheatPoints.TileIds);
            Assert.NotNull(TableEventProjection.Project(
                cheatPoints, TableProjectionAudience.ForSeat(0), new TableProjectionPolicy()));
            for (int seat = 1; seat < 4; seat++)
            {
                Assert.Null(TableEventProjection.Project(
                    cheatPoints, TableProjectionAudience.ForSeat(seat), new TableProjectionPolicy()));
            }

            Assert.Null(TableEventProjection.Project(
                cheatPoints, TableProjectionAudience.Spectator, new TableProjectionPolicy()));
        }

        [Fact]
        public void G057cPublicProjectionBoardAndWhitelistMatchV4()
        {
            JsonElement item = Case("VIS-G057C-PUBLIC-PROJECTION");
            JsonElement input = item.GetProperty("input");
            Scenario scenario = BuildScenario(input.GetProperty("state"));
            List<TableEventProjectionInput> events = BuildEventInputs(input.GetProperty("events"));
            JsonElement expected = item.GetProperty("expected");

            var unauthorizedPolicy = new TableProjectionPolicy(false, true, false);
            var sessionProjection = new TableSessionProjection(
                false,
                string.Empty,
                Array.Empty<long>(),
                Array.Empty<int>(),
                "round-vis-g057c");
            TableProjectionContext unauthorizedContext = scenario.Context(
                unauthorizedPolicy, events, sessionProjection);
            PublicSnapshotView unauthorized = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, unauthorizedContext);
            AssertBoard(expected.GetProperty("board"), unauthorized.View.Board!);
            Assert.False(unauthorized.View.Session.Finished);
            Assert.Equal("round-vis-g057c", unauthorized.View.SettlementRoundId);

            IReadOnlyList<TableProjectedEvent> unauthorizedEvents = TableEventProjection.ProjectMany(
                events, TableProjectionAudience.Spectator, unauthorizedPolicy);
            AssertEventList(
                expected.GetProperty("settlement").GetProperty("unauthorized"),
                unauthorizedEvents);
            Assert.DoesNotContain(unauthorizedEvents, value => value.IsUraDora);

            var authorizedPolicy = new TableProjectionPolicy(false, true, true);
            TableProjectionContext authorizedContext = scenario.Context(
                authorizedPolicy, events, sessionProjection);
            PublicSnapshotView authorized = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, authorizedContext);
            AssertBoard(expected.GetProperty("board"), authorized.View.Board!);
            IReadOnlyList<TableProjectedEvent> authorizedEvents = TableEventProjection.ProjectMany(
                events, TableProjectionAudience.Spectator, authorizedPolicy);
            AssertEventList(
                expected.GetProperty("settlement").GetProperty("authorized"),
                authorizedEvents);
            Assert.Contains(authorizedEvents, value => value.IsUraDora);

            // The metadata-only settlement records must agree on type/visibility with the
            // typed spectator projection. Their payload is intentionally empty in P3.2a.
            bool sawUraRecord = false;
            foreach (ProtocolEventRecord record in authorized.View.SettlementEvents)
            {
                Assert.True(
                    record.Visibility == ProtocolEventVisibility.Public ||
                    record.Visibility == ProtocolEventVisibility.SettlementPublic,
                    "settlement record has an unexpected visibility " + record.Visibility);
                Assert.Empty(record.CopyWirePayload());
                if (record.Type == ProtocolEventType.DoraRevealed)
                {
                    sawUraRecord = true;
                    Assert.Equal(ProtocolEventVisibility.SettlementPublic, record.Visibility);
                }
            }

            Assert.True(sawUraRecord, "authorized settlement batch must carry the ura DoraRevealed record");

            // V4-only ScoreBreakdown is recorded but cannot be compared to a V5 field.
            JsonElement breakdownUnauthorized = expected.GetProperty("tsumoBreakdown").GetProperty("unauthorized");
            JsonElement breakdownAuthorized = expected.GetProperty("tsumoBreakdown").GetProperty("authorized");
            Assert.Empty(Ints(breakdownUnauthorized.GetProperty("uraTiles")));
            Assert.Single(Ints(breakdownAuthorized.GetProperty("uraTiles")));
            Assert.Equal(0, Int(breakdownUnauthorized, "uraHits"));
            Assert.Equal(1, Int(breakdownAuthorized, "uraHits"));
        }

        [Fact]
        public void G057cPublicProjectionIsDefensivelyCopiedAndReadOnly()
        {
            JsonElement item = Case("VIS-G057C-PUBLIC-PROJECTION");
            JsonElement input = item.GetProperty("input");
            Scenario scenario = BuildScenario(input.GetProperty("state"));
            List<TableEventProjectionInput> events = BuildEventInputs(input.GetProperty("events"));
            var policy = new TableProjectionPolicy(false, true, false);
            PublicSnapshotView snapshot = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, scenario.Context(policy, events));

            long[] scoresBefore = snapshot.View.Board!.Scores.Select(value => value.Score).ToArray();
            uint[] doraBefore = snapshot.View.Board!.DoraIndicators.ToArray();
            scenario.Placements.Clear();
            scenario.Scores[0] = 7;
            scenario.Dora[0] = 135;
            Assert.Equal(scoresBefore, snapshot.View.Board!.Scores.Select(value => value.Score));
            Assert.Equal(doraBefore, snapshot.View.Board!.DoraIndicators);

            Assert.Throws<NotSupportedException>(
                () => ((IList<uint>)snapshot.View.Board!.DoraIndicators).Add(0));
            Assert.Throws<NotSupportedException>(
                () => ((IList<MeldView>)snapshot.View.Board!.Players[1].Melds).Add(
                    new MeldView(ProtocolMeldType.Chi, new uint[] { 0, 1, 2 }, null, 0)));
            Assert.Throws<NotSupportedException>(
                () => ((IList<ClockView>)snapshot.View.Clocks).Add(new ClockView(0, 0, 0, 0)));

            // A projected event owns its tile list; mutating the producer array cannot change it.
            uint[] producerTiles = { 53 };
            var producer = new TableEventProjectionInput(
                ProtocolEventType.TileDiscarded,
                new EventSeq(1),
                new StateVersion(1),
                0,
                0,
                producerTiles);
            producerTiles[0] = 1;
            Assert.Equal(53u, producer.TileIds[0]);
            TableProjectedEvent projected = TableEventProjection.Project(
                producer, TableProjectionAudience.Spectator, new TableProjectionPolicy())!;
            Assert.Throws<NotSupportedException>(() => ((IList<uint>)projected.TileIds).Add(2));
        }

        [Fact]
        public void G057dSeatViewsAndOwnerExtensionMatchV4()
        {
            JsonElement item = Case("VIS-G057D-SEAT-VIEW-ISOLATION");
            Scenario scenario = BuildScenario(item.GetProperty("input").GetProperty("state"));
            CheatState cheat = BuildCheat(item.GetProperty("input").GetProperty("state").GetProperty("privateCheat"));
            TableProjectionContext context = scenario.Context(new TableProjectionPolicy());
            JsonElement seatViews = item.GetProperty("expected").GetProperty("seatViews");

            for (int seat = 0; seat <= 2; seat++)
            {
                PlayerSnapshotView view = TableVisibilityProjection.ProjectPlayerSnapshot(
                    scenario.Round, cheat, seat, context);
                AssertSeatView(seatViews.GetProperty(seat.ToString(CultureInfo.InvariantCulture)), view);
            }

            JsonElement seat1Expected = seatViews.GetProperty("1");
            TableOwnerPrivateView owner1 = TableVisibilityProjection.ProjectOwnerPrivate(
                scenario.Round, cheat, 1, context);
            Assert.Equal(Ints(seat1Expected.GetProperty("mySleeveTiles")).Select(value => (uint)value), owner1.SleeveTiles);
            Assert.Equal(990, owner1.Cheat.Balance);
            Assert.Single(owner1.Cheat.Ledger);
            Assert.Equal("vis-g057d-cheat-seat1", owner1.Cheat.Ledger[0].EntryId);
            Assert.Single(owner1.Melds);
            Assert.Equal(ProtocolMeldType.Chi, owner1.Melds[0].Type);
            Assert.Equal(new uint[] { 72, 76, 80 }, owner1.Melds[0].TileIds);

            TableOwnerPrivateView owner0 = TableVisibilityProjection.ProjectOwnerPrivate(
                scenario.Round, cheat, 0, context);
            Assert.Empty(owner0.SleeveTiles);
            Assert.Equal(1000, owner0.Cheat.Balance);
            Assert.Empty(owner0.Cheat.Ledger);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, context);
            Assert.Null(seat0.Private.Decision);
            Assert.NotNull(seat0.Private.Cheat);
            Assert.Equal(1000L, seat0.Private.Cheat!.CheatPoints);
            Assert.Empty(seat0.Private.Cheat!.Ledger);
            Assert.Empty(seat0.Private.SleeveTiles);
        }

        [Fact]
        public void G057dNeighbourAndSpectatorNeverSeeHiddenIdentities()
        {
            JsonElement item = Case("VIS-G057D-SEAT-VIEW-ISOLATION");
            Scenario scenario = BuildScenario(item.GetProperty("input").GetProperty("state"));
            CheatState cheat = BuildCheat(item.GetProperty("input").GetProperty("state").GetProperty("privateCheat"));
            TableProjectionContext context = scenario.Context(new TableProjectionPolicy());
            JsonElement expected = item.GetProperty("expected");
            JsonElement hidden = expected.GetProperty("hiddenTileIds");

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, context);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 1, context);
            PublicSnapshotView spectator = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, context);

            AssertBoard(expected.GetProperty("spectatorBoard"), spectator.View.Board!);

            HashSet<int> seat0Ids = PlayerTileIds(seat0);
            HashSet<int> seat1Ids = PlayerTileIds(seat1);
            HashSet<int> spectatorIds = BoardTileIds(spectator.View.Board!);
            TableOwnerPrivateView owner1 = TableVisibilityProjection.ProjectOwnerPrivate(
                scenario.Round, cheat, 1, context);

            foreach (int tile in Ints(hidden.GetProperty("opponentClosedFromSeat0")))
            {
                Assert.DoesNotContain(tile, seat0Ids);
                Assert.DoesNotContain(tile, spectatorIds);
                Assert.True(
                    seat1Ids.Contains(tile) || owner1.SleeveTiles.Contains((uint)tile),
                    "seat1's own view/owner extension must contain hidden tile " + tile);
            }

            foreach (int tile in Ints(hidden.GetProperty("wallOrder")))
            {
                Assert.DoesNotContain(tile, seat0Ids);
                Assert.DoesNotContain(tile, seat1Ids);
                Assert.DoesNotContain(tile, spectatorIds);
            }

            foreach (int tile in Ints(hidden.GetProperty("uraDoraIndicators")))
            {
                Assert.DoesNotContain(tile, seat0Ids);
                Assert.DoesNotContain(tile, spectatorIds);
                Assert.DoesNotContain(tile, spectator.View.Board!.DoraIndicators.Select(value => (int)value));
            }

            foreach (int tile in Ints(hidden.GetProperty("unrevealedDeadWall")))
            {
                Assert.DoesNotContain(tile, seat0Ids);
                Assert.DoesNotContain(tile, spectatorIds);
            }

            TableOwnerPrivateView owner1ForSleeve = owner1;
            foreach (int tile in Ints(expected.GetProperty("seatViews").GetProperty("1").GetProperty("mySleeveTiles")))
            {
                Assert.Contains((uint)tile, owner1ForSleeve.SleeveTiles);
                Assert.DoesNotContain(tile, seat0Ids);
            }

            foreach (string entryId in Strings(hidden.GetProperty("privateCheatEntryIds")))
            {
                Assert.DoesNotContain(
                    seat0.Private.Cheat!.Ledger,
                    value => string.Equals(value.EntryId, entryId, StringComparison.Ordinal));
                Assert.Contains(
                    owner1.Cheat.Ledger,
                    value => string.Equals(value.EntryId, entryId, StringComparison.Ordinal));
            }

            foreach (int balance in Ints(hidden.GetProperty("privateCheatBalances")))
            {
                Assert.NotEqual(balance, (int)seat0.Private.Cheat!.CheatPoints);
            }
        }

        [Fact]
        public void V5ProjectionCollectionsAreReadOnlyAndCopied()
        {
            JsonElement item = Case("VIS-G057D-SEAT-VIEW-ISOLATION");
            JsonElement stateJson = item.GetProperty("input").GetProperty("state");
            Scenario scenario = BuildScenario(stateJson);
            CheatState cheat = V32Fixtures.Cheat();
            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, scenario.Context(new TableProjectionPolicy()));

            Assert.Throws<NotSupportedException>(
                () => ((IList<uint>)seat0.Private.ClosedTiles).Add(0));
            Assert.Throws<NotSupportedException>(
                () => ((IList<DiscardView>)seat0.Public.Board!.Players[0].Discards).Add(
                    new DiscardView(0, 0, false, false)));
            Assert.Throws<NotSupportedException>(
                () => ((IList<MeldView>)seat0.Public.Board!.Players[1].Melds).Add(
                    new MeldView(ProtocolMeldType.Chi, new uint[] { 0, 1, 2 }, null, 0)));

            List<uint> closedBefore = seat0.Private.ClosedTiles.ToList();
            scenario.Placements.Clear();
            scenario.Scores[0] = 1;
            PlayerSnapshotView replayed = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, scenario.Context(new TableProjectionPolicy()));
            Assert.Equal(closedBefore, replayed.Private.ClosedTiles);
            Assert.Equal(25000L, replayed.Public.Board!.Scores[0].Score);

            // A closed kan is public as an event but its identities are owner-only.
            var closedKan = new TableEventProjectionInput(
                ProtocolEventType.KanDeclared,
                new EventSeq(1),
                new StateVersion(1),
                0,
                2,
                new uint[] { 10, 11, 12, 13 },
                isClosedKan: true);
            TableProjectedEvent? spectatorKan = TableEventProjection.Project(
                closedKan, TableProjectionAudience.Spectator, new TableProjectionPolicy());
            TableProjectedEvent? ownerKan = TableEventProjection.Project(
                closedKan, TableProjectionAudience.ForSeat(2), new TableProjectionPolicy());
            Assert.NotNull(spectatorKan);
            Assert.Empty(spectatorKan!.TileIds);
            Assert.NotNull(ownerKan);
            Assert.Equal(new uint[] { 10, 11, 12, 13 }, ownerKan!.TileIds);
        }

        // ------------------------------------------------------------------
        // Production projection invocation / comparison
        // ------------------------------------------------------------------

        private static void AssertSeatView(JsonElement expected, PlayerSnapshotView actual)
        {
            PublicBoardView board = actual.Public.Board!;
            Assert.Equal(Int(expected, "viewerSeat"), actual.Seat);
            AssertBoardCommon(expected, board);
            Assert.Equal(Int(expected, "viewerSeat"), board.Players[actual.Seat].Seat);

            Assert.Equal(
                Ints(expected.GetProperty("myClosedTiles")).Select(value => (uint)value),
                actual.Private.ClosedTiles);
            Assert.Equal(
                NullableInt(expected.GetProperty("myDrawnTile")),
                actual.Private.DrawnTileId.HasValue ? (int?)actual.Private.DrawnTileId.Value : null);
            Assert.Equal(
                Ints(expected.GetProperty("myAllClosedTiles")).Select(value => (uint)value),
                actual.Private.DrawnTileId.HasValue
                    ? actual.Private.ClosedTiles.Concat(new[] { actual.Private.DrawnTileId.Value })
                    : actual.Private.ClosedTiles);
            Assert.Equal(
                Ints(expected.GetProperty("myDiscardHistory")).Select(value => (uint)value),
                actual.Private.DiscardHistory);

            JsonElement furiten = expected.GetProperty("myFuriten");
            bool isFuriten = Bool(furiten, "discard") || Bool(furiten, "temporary") || Bool(furiten, "riichi");
            Assert.Equal(isFuriten, actual.Private.Furiten);

            int? derivedLastDiscardSeat = DeriveLastDiscardSeat(board);
            Assert.Equal(NullableInt(expected.GetProperty("lastDiscardPlayer")), derivedLastDiscardSeat);

            if (expected.GetProperty("legalActions").GetArrayLength() == 0)
            {
                Assert.Null(actual.Private.Decision);
            }
        }

        private static void AssertBoard(JsonElement expected, PublicBoardView actual)
        {
            AssertBoardCommon(expected, actual);
            Assert.Equal(NullableInt(expected.GetProperty("lastDiscardPlayer")), DeriveLastDiscardSeat(actual));
        }

        private static void AssertBoardCommon(JsonElement expected, PublicBoardView actual)
        {
            Assert.Equal(expected.GetProperty("phase").GetString(), PhaseName(actual.Phase));
            Assert.Equal(Int(expected, "roundWind"), (int)actual.RoundWind);
            Assert.Equal(Int(expected, "roundNumber"), (int)actual.RoundNumber);
            Assert.Equal(Int(expected, "honbaSticks"), (int)actual.Honba);
            Assert.Equal(Int(expected, "riichiSticks"), (int)actual.RiichiSticks);
            Assert.Equal(Int(expected, "dealerSeat"), actual.DealerSeat);
            Assert.Equal(Int(expected, "currentTurnPlayer"), actual.CurrentSeat);
            Assert.Equal(Int(expected, "wallRemaining"), (int)actual.WallRemaining);
            Assert.Equal(
                Ints(expected.GetProperty("doraIndicators")).Select(value => (uint)value),
                actual.DoraIndicators);
            Assert.Equal(
                Ints(expected.GetProperty("scores")).Select(value => (long)value),
                actual.Scores.Select(value => value.Score));
            Assert.Equal(
                BooleanArray(expected.GetProperty("riichiStatus")),
                actual.Players.Select(value => value.Riichi));
            Assert.Equal(
                Ints(expected.GetProperty("handTileCounts")).Select(value => (uint)value),
                actual.Players.Select(value => value.HandCount));

            JsonElement expectedDiscards = expected.GetProperty("discards");
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(
                    Ints(expectedDiscards[seat]).Select(value => (uint)value),
                    actual.Players[seat].Discards.Select(value => value.TileId));
            }

            JsonElement expectedMelds = expected.GetProperty("melds");
            for (int seat = 0; seat < 4; seat++)
            {
                JsonElement melds = expectedMelds[seat];
                Assert.Equal(melds.GetArrayLength(), actual.Players[seat].Melds.Count);
                for (int index = 0; index < melds.GetArrayLength(); index++)
                {
                    JsonElement meld = melds[index];
                    Assert.Equal(
                        Str(meld, "type"),
                        actual.Players[seat].Melds[index].Type.ToString().ToUpperInvariant());
                    Assert.Equal(
                        Ints(meld.GetProperty("tileIds")).Select(value => (uint)value),
                        actual.Players[seat].Melds[index].TileIds);
                }
            }

            Assert.Equal(
                NullableInt(expected.GetProperty("lastDiscard")),
                actual.LastDiscard == null ? (int?)null : (int)actual.LastDiscard.TileId);
        }

        private static int? DeriveLastDiscardSeat(PublicBoardView board)
        {
            if (board.LastDiscard == null)
            {
                return null;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int index = 0; index < board.Players[seat].Discards.Count; index++)
                {
                    if (board.Players[seat].Discards[index].TileId == board.LastDiscard.TileId)
                    {
                        return seat;
                    }
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Scenario construction
        // ------------------------------------------------------------------

        private sealed class Scenario
        {
            public Scenario(
                List<TilePlacement> placements,
                uint[] dora,
                long[] scores,
                TilePositionMap map,
                RoundStateMachine round,
                IReadOnlyList<TableSeatPublicFacts> publicFacts,
                IReadOnlyList<TableSeatPrivateFacts> privateFacts)
            {
                Placements = placements;
                Dora = dora;
                Scores = scores;
                Map = map;
                Round = round;
                PublicFacts = publicFacts;
                PrivateFacts = privateFacts;
            }

            public List<TilePlacement> Placements { get; }

            public uint[] Dora { get; }

            public long[] Scores { get; }

            public TilePositionMap Map { get; }

            public RoundStateMachine Round { get; }

            public IReadOnlyList<TableSeatPublicFacts> PublicFacts { get; }

            public IReadOnlyList<TableSeatPrivateFacts> PrivateFacts { get; }

            public TableProjectionContext Context(
                TableProjectionPolicy policy,
                IReadOnlyList<TableEventProjectionInput>? events = null,
                TableSessionProjection? session = null)
            {
                ulong lastSeq = 0;
                ulong eventVersion = 0;
                if (events != null && events.Count > 0)
                {
                    lastSeq = events[events.Count - 1].EventSeq.Value;
                    for (int index = 0; index < events.Count; index++)
                    {
                        eventVersion = Math.Max(eventVersion, events[index].CommandVersion.Value);
                    }
                }

                ulong version = Math.Max(unchecked((ulong)Round.Version), eventVersion);
                var identity = new TableProjectionIdentity(
                    new SessionId("session-visibility-golden"),
                    new RoundId(Round.RoundId),
                    new StateVersion(version),
                    new EventSeq(lastSeq),
                    new StreamCursor(new StreamVersion(0), new ProjectionSeq(0)),
                    new SessionVersion(1));
                return new TableProjectionContext(
                    identity,
                    policy,
                    Array.Empty<ClockView>(),
                    PublicFacts,
                    PrivateFacts,
                    null,
                    events,
                    session);
            }
        }

        private static Scenario BuildScenario(JsonElement state)
        {
            var placements = new List<TilePlacement>();
            int wallCount = 0;
            foreach (JsonElement placement in state.GetProperty("placements").EnumerateArray())
            {
                int tileId = placement[0].GetInt32();
                string zone = placement[1].GetString()!;
                int seat = placement[2].GetInt32();
                int index = placement[3].GetInt32();
                if (string.Equals(zone, "WALL", StringComparison.Ordinal))
                {
                    wallCount++;
                }

                placements.Add(new TilePlacement(new TileId(tileId), Position(zone, seat, index)));
            }

            uint[] dora = Ints(state.GetProperty("doraIndicators")).Select(value => (uint)value).ToArray();
            LastDiscardEvidence? lastDiscard = null;
            JsonElement last = state.GetProperty("lastDiscard");
            if (last.ValueKind != JsonValueKind.Null)
            {
                lastDiscard = new LastDiscardEvidence(
                    new TileId(Int(last, "tileId")),
                    TilePosition.Discard(Int(last, "seat"), Int(last, "index")));
            }

            DrawnTileEvidence? drawn = null;
            int dealerSeat = Int(state, "dealerSeat");
            foreach (JsonElement hand in state.GetProperty("hands").EnumerateArray())
            {
                JsonElement drawnTile = hand.GetProperty("drawnTileId");
                if (drawnTile.ValueKind != JsonValueKind.Null)
                {
                    int seat = Int(hand, "seat");
                    if (drawn.HasValue)
                    {
                        throw new InvalidOperationException("fixture expects at most one tracked drawn tile");
                    }

                    drawn = new DrawnTileEvidence(
                        new TileId(drawnTile.GetInt32()),
                        seat,
                        seat == dealerSeat ? DrawSource.DealExtra : DrawSource.Wall);
                }
            }

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                0,
                wallCount,
                0,
                lastDiscard,
                0,
                null,
                dora.Select(value => new TileId((int)value)).ToArray(),
                0,
                drawn);

            long[] scores = Ints(state.GetProperty("scores")).Select(value => (long)value).ToArray();
            var spec = new RoundSpec(
                Str(state, "roundId"),
                dealerSeat,
                Int(state, "roundWind"),
                Int(state, "roundNumber"),
                Int(state, "honbaSticks"),
                scores,
                Int(state, "riichiSticks"));
            RoundStateMachine round = RoundStateMachine.CreateDealt(spec, map, ParsePhase(Str(state, "phase")));

            var publicFacts = new List<TableSeatPublicFacts>(4);
            var privateFacts = new List<TableSeatPrivateFacts>(4);
            bool[] riichi = BooleanArray(state.GetProperty("riichiDeclared"));
            JsonElement meldsBySeat = state.GetProperty("melds");
            JsonElement hands = state.GetProperty("hands");
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = map.TilesInZone(TileZone.Discard, seat);
                var discards = new List<DiscardView>(river.Count);
                for (int index = 0; index < river.Count; index++)
                {
                    discards.Add(new DiscardView(
                        unchecked((uint)river[index].Value),
                        unchecked((uint)index),
                        false,
                        false));
                }

                var melds = new List<TableMeldFact>();
                foreach (JsonElement meld in meldsBySeat[seat].EnumerateArray())
                {
                    melds.Add(new TableMeldFact(
                        ParseMeldType(Str(meld, "type")),
                        Ints(meld.GetProperty("tileIds")).Select(value => (uint)value).ToArray(),
                        NullableUInt(meld.GetProperty("calledTileId")),
                        Int(meld, "fromSeat")));
                }

                publicFacts.Add(new TableSeatPublicFacts(seat, riichi[seat], melds, discards));

                JsonElement hand = hands[seat];
                privateFacts.Add(new TableSeatPrivateFacts(
                    seat,
                    Bool(hand, "furiten"),
                    false,
                    Ints(hand.GetProperty("discardHistory")).Select(value => (uint)value).ToArray()));
            }

            return new Scenario(placements, dora, scores, map, round, publicFacts, privateFacts);
        }

        private static CheatState BuildCheat(JsonElement privateCheat)
        {
            JsonElement points = privateCheat.GetProperty("points");
            int initial = points[0].GetInt32();
            CheatState state = V32Fixtures.Cheat(initial);
            foreach (JsonElement entry in privateCheat.GetProperty("entries").EnumerateArray())
            {
                state = V32Fixtures.WithLedger(
                    state,
                    new CheatLedgerEntry(
                        Str(entry, "entryId"),
                        Int(entry, "seat"),
                        Int(entry, "amount"),
                        Int(entry, "balanceAfter"),
                        Str(entry, "reason")));
            }

            return state;
        }

        private static List<TableEventProjectionInput> BuildEventInputs(JsonElement events)
        {
            var result = new List<TableEventProjectionInput>();
            uint ordinal = 0;
            foreach (JsonElement value in events.EnumerateArray())
            {
                ProtocolEventType type = Enum.Parse<ProtocolEventType>(Str(value, "kind"), false);
                uint[] tiles = Ints(value.GetProperty("tileIds")).Select(tile => (uint)tile).ToArray();
                bool ura = value.TryGetProperty("isUraDora", out JsonElement uraValue) && uraValue.GetBoolean();
                long? cheatAmount = value.TryGetProperty("amount", out JsonElement amountValue)
                    ? amountValue.GetInt32()
                    : (long?)null;
                long? cheatBalance = value.TryGetProperty("newBalance", out JsonElement balanceValue)
                    ? balanceValue.GetInt32()
                    : (long?)null;
                result.Add(new TableEventProjectionInput(
                    type,
                    new EventSeq((ulong)result.Count + 1),
                    new StateVersion(1),
                    ordinal++,
                    Int(value, "seat"),
                    tiles,
                    isUraDora: ura,
                    cheatAmount: cheatAmount,
                    cheatBalance: cheatBalance));
            }

            return result;
        }

        private static void AssertEventList(JsonElement expected, IReadOnlyList<TableProjectedEvent> actual)
        {
            JsonElement.ArrayEnumerator enumerator = expected.EnumerateArray();
            int index = 0;
            while (enumerator.MoveNext())
            {
                JsonElement value = enumerator.Current;
                Assert.True(index < actual.Count, "actual event list is shorter than expected at " + index);
                TableProjectedEvent projected = actual[index];
                Assert.Equal(Str(value, "kind"), projected.Type.ToString());
                Assert.Equal(Int(value, "seat"), projected.Seat);
                Assert.Equal(
                    Ints(value.GetProperty("tileIds")).Select(tile => (uint)tile),
                    projected.TileIds);
                if (value.TryGetProperty("isUraDora", out JsonElement ura))
                {
                    Assert.Equal(ura.GetBoolean(), projected.IsUraDora);
                }

                index++;
            }

            Assert.Equal(index, actual.Count);
        }

        // ------------------------------------------------------------------
        // Negative privacy proofs use actual projected values, never reflection
        // ------------------------------------------------------------------

        private static HashSet<int> PlayerTileIds(PlayerSnapshotView snapshot)
        {
            var result = BoardTileIds(snapshot.Public.Board!);
            foreach (uint tile in snapshot.Private.ClosedTiles)
            {
                result.Add((int)tile);
            }

            if (snapshot.Private.DrawnTileId.HasValue)
            {
                result.Add((int)snapshot.Private.DrawnTileId.Value);
            }

            foreach (uint tile in snapshot.Private.SleeveTiles)
            {
                result.Add((int)tile);
            }

            foreach (MeldView meld in snapshot.Private.OwnerMelds)
            {
                foreach (uint tile in meld.TileIds)
                {
                    result.Add((int)tile);
                }
            }

            return result;
        }

        private static HashSet<int> BoardTileIds(PublicBoardView board)
        {
            var result = new HashSet<int>();
            foreach (uint tile in board.DoraIndicators)
            {
                result.Add((int)tile);
            }

            foreach (PlayerPublicView player in board.Players)
            {
                foreach (MeldView meld in player.Melds)
                {
                    foreach (uint tile in meld.TileIds)
                    {
                        result.Add((int)tile);
                    }
                }

                foreach (DiscardView discard in player.Discards)
                {
                    result.Add((int)discard.TileId);
                }
            }

            if (board.LastDiscard != null)
            {
                result.Add((int)board.LastDiscard.TileId);
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Provenance verification
        // ------------------------------------------------------------------

        private static void VerifyRenderHash(JsonElement root)
        {
            string path = Path.Combine(RepositoryRoot(), "tests/v5-golden/visibility-v1.json");
            string text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            Assert.DoesNotContain("\r", text);
            string normalized = Regex.Replace(
                text,
                "\"renderSha256LfNormalized\":\\s*\"[0-9a-f]{64}\"",
                "\"renderSha256LfNormalized\": null");
            Assert.Equal(Str(root, "renderSha256LfNormalized"), Sha256(Encoding.UTF8.GetBytes(normalized)));
            Assert.EndsWith("\n", text);
        }

        private static void VerifySourceHashes(JsonElement root)
        {
            JsonElement sources = root.GetProperty("sourceHashes");
            Assert.Equal(RequiredSourceFiles.Length, sources.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement source in sources.EnumerateArray())
            {
                string relative = Str(source, "file");
                Assert.Contains(relative, RequiredSourceFiles);
                Assert.True(seen.Add(relative), "duplicate source hash " + relative);
                AssertSourceHash(source);
            }

            foreach (string required in RequiredSourceFiles)
            {
                Assert.Contains(required, seen);
            }

            AssertSourceHash(root.GetProperty("exporterSource"));
        }

        private static void AssertSourceHash(JsonElement source)
        {
            string relative = Str(source, "file");
            string path = Path.Combine(
                RepositoryRoot(),
                relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source file missing: " + relative);
            byte[] raw = File.ReadAllBytes(path);
            string text = Encoding.UTF8.GetString(raw);
            string lf = text.Replace("\r\n", "\n");
            int crlf = Count(text, "\r\n");
            int lfOnly = Count(text, "\n") - crlf;
            string actualKind = ClassifyLineEndings(crlf, lfOnly);
            Assert.Equal(Str(source, "sha256LfNormalized"), Sha256(Encoding.UTF8.GetBytes(lf)));
            if (string.Equals(Str(source, "lineEndings"), actualKind, StringComparison.Ordinal))
            {
                Assert.Equal(Int(source, "bytesRaw"), raw.Length);
                Assert.Equal(Str(source, "sha256RawBytes"), Sha256(raw));
                Assert.Equal(Int(source, "crlfCount"), crlf);
                Assert.Equal(Int(source, "lfOnlyCount"), lfOnly);
            }
            else
            {
                // A checkout with a different autocrlf profile must change raw bytes but not
                // the LF-normalized content authority.
                Assert.NotEqual(Str(source, "sha256RawBytes"), Sha256(raw));
                Assert.NotEqual(Int(source, "bytesRaw"), raw.Length);
                Assert.Contains(actualKind, new[] { "CRLF", "LF", "MIXED", "NONE" });
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static JsonElement Case(string id)
        {
            foreach (JsonElement item in Golden.Value.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (string.Equals(Str(item, "id"), id, StringComparison.Ordinal))
                {
                    return item;
                }
            }

            throw new InvalidOperationException("fixture case not found: " + id);
        }

        private static TilePosition Position(string zone, int seat, int index)
        {
            switch (zone)
            {
                case "WALL": return TilePosition.Wall(index);
                case "DEAD_WALL": return TilePosition.DeadWall(index);
                case "DEAD_WALL_REPLACEMENT": return TilePosition.DeadWallReplacement(index);
                case "HAND": return TilePosition.Hand(seat, index);
                case "DISCARD": return TilePosition.Discard(seat, index);
                case "MELD": return TilePosition.Meld(seat, index);
                case "SLEEVE": return TilePosition.Sleeve(seat, index);
                default: throw new InvalidOperationException("unknown fixture zone " + zone);
            }
        }

        private static RoundPhase ParsePhase(string phase)
        {
            switch (phase)
            {
                case "STACKING": return RoundPhase.Stacking;
                case "DEALING": return RoundPhase.Dealing;
                case "DRAW": return RoundPhase.Draw;
                case "CHEAT_WINDOW": return RoundPhase.CheatWindow;
                case "DISCARD": return RoundPhase.Discard;
                case "RESPONSE_WINDOW": return RoundPhase.ResponseWindow;
                case "CHANKAN_WINDOW": return RoundPhase.ChankanWindow;
                case "KAN_DRAW": return RoundPhase.KanDraw;
                case "ROUND_END": return RoundPhase.RoundEnd;
                default: throw new InvalidOperationException("unknown fixture phase " + phase);
            }
        }

        private static string PhaseName(ProtocolGamePhase phase)
        {
            switch (phase)
            {
                case ProtocolGamePhase.Stacking: return "STACKING";
                case ProtocolGamePhase.Dealing: return "DEALING";
                case ProtocolGamePhase.Draw: return "DRAW";
                case ProtocolGamePhase.CheatWindow: return "CHEAT_WINDOW";
                case ProtocolGamePhase.Discard: return "DISCARD";
                case ProtocolGamePhase.ResponseWindow: return "RESPONSE_WINDOW";
                case ProtocolGamePhase.ChankanWindow: return "CHANKAN_WINDOW";
                case ProtocolGamePhase.KanDraw: return "KAN_DRAW";
                case ProtocolGamePhase.RoundEnd: return "ROUND_END";
                case ProtocolGamePhase.MatchEnd: return "MATCH_END";
                default: throw new InvalidOperationException("unknown V5 phase " + phase);
            }
        }

        private static ProtocolMeldType ParseMeldType(string type)
        {
            switch (type)
            {
                case "CHI": return ProtocolMeldType.Chi;
                case "PON": return ProtocolMeldType.Pon;
                case "OPEN_KAN": return ProtocolMeldType.OpenKan;
                case "CLOSED_KAN": return ProtocolMeldType.ClosedKan;
                case "ADDED_KAN": return ProtocolMeldType.AddedKan;
                default: throw new InvalidOperationException("unknown fixture meld type " + type);
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

        private static JsonDocument LoadGolden()
        {
            string path = Path.Combine(RepositoryRoot(), "tests/v5-golden/visibility-v1.json");
            return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
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

        private static int? NullableInt(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Null ? (int?)null : element.GetInt32();
        }

        private static uint? NullableUInt(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Null ? (uint?)null : (uint)element.GetInt32();
        }

        private static int[] Ints(JsonElement array)
        {
            return array.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        }

        private static bool[] BooleanArray(JsonElement array)
        {
            return array.EnumerateArray().Select(value => value.GetBoolean()).ToArray();
        }

        private static string[] Strings(JsonElement array)
        {
            return array.EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
    }
}

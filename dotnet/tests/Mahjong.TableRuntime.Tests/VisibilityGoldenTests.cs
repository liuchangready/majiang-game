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
    /// The fixture is produced only by the V4 exporter. This test rebuilds a real V5
    /// 136-entity round from the exported placement input and drives the complete existing
    /// production path: typed projections, TableProjectionContractMapper.MapEvent /
    /// MapPublicSnapshot / MapPlayerSnapshot, including owner sleeve/melds and score
    /// breakdown payloads. No V4 object is passed into a V5 API.
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
            "dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs",
            "dotnet/src/Mahjong.Contracts/ProtocolSnapshot.cs",
            "dotnet/src/Mahjong.Contracts/ProtocolProjectionPayload.cs",
            "dotnet/src/Mahjong.Domain/TilePositionMap.cs",
            "dotnet/src/Mahjong.Domain/RoundStateMachine.cs",
            "dotnet/src/Mahjong.Domain/RoundTypes.cs",
            "dotnet/tests/Mahjong.TableRuntime.Tests/TableProjectionContractMapperTests.cs"
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
                value => Str(value, "id") == "VG-D06" &&
                         (Str(value, "status").Contains("MAPPED", StringComparison.Ordinal) ||
                          Str(value, "status").Contains("AUTHORIZATION", StringComparison.Ordinal)));
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
            CheatState cheat = BuildCheat(stateJson.GetProperty("privateCheat"));
            TableProjectionContext context = scenario.Context(new TableProjectionPolicy());
            PlayerSnapshotView view = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, context);

            AssertSeatView(item.GetProperty("expected").GetProperty("snapshot"), view);

            TableOwnerPrivateView owner0 = TableVisibilityProjection.ProjectOwnerPrivate(
                scenario.Round, cheat, 0, context);
            PlayerSnapshotView mapped = TableProjectionContractMapper.MapPlayerSnapshot(
                view, owner0, new TableProjectionTimeContext(0));
            Assert.Equal(view.Private.ClosedTiles, mapped.Private.ClosedTiles);
            Assert.Equal(view.Private.DrawnTileId, mapped.Private.DrawnTileId);
            Assert.Empty(mapped.Private.SleeveTiles);

            // Real authoritative transition, not a mutation of constructor material: the new
            // round must differ and the old V4-golden snapshot must not drift.
            List<uint> oldClosed = view.Private.ClosedTiles.ToList();
            int oldDiscards = view.Public.Board!.Players[0].Discards.Count;
            RoundStateMachine advanced = V32Fixtures.ApplyDiscard(scenario.Round, 0, out _);
            var melds = new Dictionary<int, IReadOnlyList<TableMeldFact>>
            {
                [1] = scenario.PublicFacts[1].Melds
            };
            PlayerSnapshotView advancedView = TableVisibilityProjection.ProjectPlayerSnapshot(
                advanced, cheat, 0, V32Fixtures.Context(advanced, melds: melds));
            Assert.True(
                advancedView.Public.Board!.Players[0].Discards.Count > oldDiscards,
                "the real discard transition must be visible to the new projection");
            Assert.NotEqual(oldClosed, advancedView.Private.ClosedTiles);
            Assert.Equal(oldClosed, view.Private.ClosedTiles);
            Assert.Equal(oldDiscards, view.Public.Board!.Players[0].Discards.Count);

            // Nested meld facts are copied at construction; this is a construction-layer proof,
            // not a claim about the immutable snapshot itself.
            uint[] meldTiles = { 0, 4, 8 };
            var fact = new TableMeldFact(ProtocolMeldType.Chi, meldTiles, 0, 0);
            meldTiles[0] = 9;
            Assert.Equal(new uint[] { 0, 4, 8 }, fact.TileIds);
        }

        [Fact]
        public void G057bEventVisibilityAndMappedPayloadsMatchV4()
        {
            JsonElement item = Case("VIS-G057B-EVENT-VISIBILITY");
            JsonElement input = item.GetProperty("input");
            List<FixtureEvent> unauthorized = BuildFixtureEvents(input.GetProperty("events"));
            List<FixtureEvent> authorized = BuildFixtureEvents(input.GetProperty("authorizedEvents"));

            JsonElement visibility = item.GetProperty("expected").GetProperty("visibility");
            for (int seat = 0; seat < 4; seat++)
            {
                TableProjectionAudience audience = TableProjectionAudience.ForSeat(seat);
                AssertScenarioEvents(
                    visibility.GetProperty("unauthorized").GetProperty(seat.ToString(CultureInfo.InvariantCulture)),
                    unauthorized,
                    audience,
                    new TableProjectionPolicy(false, false, false));
                AssertScenarioEvents(
                    visibility.GetProperty("settlementAuthorized").GetProperty(seat.ToString(CultureInfo.InvariantCulture)),
                    authorized,
                    audience,
                    new TableProjectionPolicy(false, true, true));
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
            TableProjectedEvent ownerCheat = TableEventProjection.Project(
                cheatPoints, TableProjectionAudience.ForSeat(0), new TableProjectionPolicy())!;
            var mappedCheat = TableProjectionContractMapper.MapEvent(
                ownerCheat, new TableProjectionEventFacts(turnNumber: 0));
            Assert.Equal(-10L, mappedCheat.ProjectionPayload!.CheatAmount);
            for (int seat = 1; seat < 4; seat++)
            {
                Assert.Null(TableEventProjection.Project(
                    cheatPoints, TableProjectionAudience.ForSeat(seat), new TableProjectionPolicy()));
            }

            Assert.Null(TableEventProjection.Project(
                cheatPoints, TableProjectionAudience.Spectator, new TableProjectionPolicy()));

            // Mapping rejects a missing required fact; it never invents a default.
            TableProjectedEvent discard = TableEventProjection.Project(
                unauthorized.First(value => Str(value.Descriptor, "kind") == "TileDiscarded").Input,
                TableProjectionAudience.ForSeat(2),
                new TableProjectionPolicy())!;
            Assert.Throws<TableProjectionException>(() =>
                TableProjectionContractMapper.MapEvent(discard, null));
        }

        [Fact]
        public void G057cPublicProjectionAndMappedPayloadsMatchV4()
        {
            JsonElement item = Case("VIS-G057C-PUBLIC-PROJECTION");
            JsonElement input = item.GetProperty("input");
            Scenario scenario = BuildScenario(input.GetProperty("state"));
            List<FixtureEvent> fixtures = BuildFixtureEvents(input.GetProperty("events"));
            List<TableEventProjectionInput> events = fixtures.Select(value => value.Input).ToList();
            JsonElement expected = item.GetProperty("expected");
            ClockView clock = ParseClock(input.GetProperty("clock"));
            ClockView expectedClock = ParseClock(expected.GetProperty("clock"));
            var clocks = new List<ClockView> { clock };

            var unauthorizedPolicy = new TableProjectionPolicy(false, true, false);
            var sessionProjection = new TableSessionProjection(
                false,
                string.Empty,
                Array.Empty<long>(),
                Array.Empty<int>(),
                "round-vis-g057c");
            TableProjectionContext unauthorizedContext = scenario.Context(
                unauthorizedPolicy, events, sessionProjection, clocks);
            PublicSnapshotView unauthorized = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, unauthorizedContext);
            AssertBoard(expected.GetProperty("board"), unauthorized.View.Board!);
            AssertClock(expectedClock, unauthorized.View.Clocks);
            Assert.False(unauthorized.View.Session.Finished);
            Assert.Equal("round-vis-g057c", unauthorized.View.SettlementRoundId);

            IReadOnlyList<TableProjectedEvent> unauthorizedEvents = TableEventProjection.ProjectMany(
                events, TableProjectionAudience.Spectator, unauthorizedPolicy);
            AssertEventList(
                expected.GetProperty("settlement").GetProperty("unauthorized"),
                unauthorizedEvents);
            Assert.DoesNotContain(unauthorizedEvents, value => value.IsUraDora);

            List<FixtureEvent> unauthorizedVisible = VisibleFixtures(
                fixtures, TableProjectionAudience.Spectator, unauthorizedPolicy);
            List<ProtocolEventRecord> unauthorizedTyped = MapAll(
                unauthorizedEvents, unauthorizedVisible, includeBreakdown: false, uraAuthorized: false);
            foreach (ProtocolEventRecord record in unauthorizedTyped)
            {
                if (record.Type == ProtocolEventType.TsumoWin)
                {
                    Assert.Null(record.ProjectionPayload!.Breakdown);
                    Assert.Equal(4000L, record.ProjectionPayload.Score);
                    Assert.Equal(3u, record.ProjectionPayload.Han);
                    Assert.Equal(30u, record.ProjectionPayload.Fu);
                }
            }

            // Raw unauthorised breakdown identities are a caller-contract rejection, not a
            // silent strip inside the mapper.
            FixtureEvent tsumo = fixtures.Single(value => Str(value.Descriptor, "kind") == "TsumoWin");
            Assert.Throws<ArgumentException>(() => Facts(tsumo, uraAuthorized: false, includeBreakdown: true));

            PublicSnapshotView mappedUnauthorized = TableProjectionContractMapper.MapPublicSnapshot(
                unauthorized,
                SettlementTyped(unauthorizedEvents, unauthorizedVisible, includeBreakdown: false, uraAuthorized: false));
            AssertBoard(expected.GetProperty("board"), mappedUnauthorized.View.Board!);
            Assert.All(
                mappedUnauthorized.View.SettlementEvents,
                record => Assert.NotNull(record.ProjectionPayload));

            var authorizedPolicy = new TableProjectionPolicy(false, true, true);
            TableProjectionContext authorizedContext = scenario.Context(
                authorizedPolicy, events, sessionProjection, clocks);
            PublicSnapshotView authorized = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, authorizedContext);
            AssertBoard(expected.GetProperty("board"), authorized.View.Board!);
            IReadOnlyList<TableProjectedEvent> authorizedEvents = TableEventProjection.ProjectMany(
                events, TableProjectionAudience.Spectator, authorizedPolicy);
            AssertEventList(
                expected.GetProperty("settlement").GetProperty("authorized"),
                authorizedEvents);
            Assert.Contains(authorizedEvents, value => value.IsUraDora);

            List<FixtureEvent> authorizedVisible = VisibleFixtures(
                fixtures, TableProjectionAudience.Spectator, authorizedPolicy);
            List<ProtocolEventRecord> authorizedTyped = MapAll(
                authorizedEvents, authorizedVisible, includeBreakdown: true, uraAuthorized: true);
            ProtocolEventRecord authorizedTsumo = authorizedTyped.Single(
                value => value.Type == ProtocolEventType.TsumoWin);
            JsonElement breakdown = expected.GetProperty("tsumoBreakdown").GetProperty("authorized");
            Assert.NotNull(authorizedTsumo.ProjectionPayload!.Breakdown);
            ScoreBreakdownView payload = authorizedTsumo.ProjectionPayload.Breakdown!;
            Assert.Equal(Ints(breakdown.GetProperty("uraTiles")).Select(value => (uint)value), payload.UraTiles);
            Assert.Equal(
                Ints(breakdown.GetProperty("uraHitTiles")).Select(value => (uint)value),
                payload.UraHits);
            Assert.Equal((uint)Int(breakdown, "totalPoints"), checked((uint)payload.TotalPoints));
            Assert.Equal((uint)Int(breakdown, "doraCount"), payload.DoraCount);
            Assert.Equal((uint)Int(breakdown, "uraCount"), payload.UraCount);
            Assert.True(payload.UraAuthorized);
            Assert.Equal(4000L, authorizedTsumo.ProjectionPayload.Score);

            PublicSnapshotView mappedAuthorized = TableProjectionContractMapper.MapPublicSnapshot(
                authorized,
                SettlementTyped(authorizedEvents, authorizedVisible, includeBreakdown: true, uraAuthorized: true));
            AssertBoard(expected.GetProperty("board"), mappedAuthorized.View.Board!);
            Assert.Contains(
                mappedAuthorized.View.SettlementEvents,
                record => record.Type == ProtocolEventType.DoraRevealed &&
                          record.ProjectionPayload!.UraAuthorized);
        }

        [Fact]
        public void G057cPublicProjectionIsDefensivelyCopiedAndClockSensitive()
        {
            JsonElement item = Case("VIS-G057C-PUBLIC-PROJECTION");
            JsonElement input = item.GetProperty("input");
            Scenario scenario = BuildScenario(input.GetProperty("state"));
            List<FixtureEvent> fixtures = BuildFixtureEvents(input.GetProperty("events"));
            JsonElement expected = item.GetProperty("expected");
            var policy = new TableProjectionPolicy(false, true, false);
            ClockView clock = ParseClock(input.GetProperty("clock"));
            ClockView expectedClock = ParseClock(expected.GetProperty("clock"));
            var mutableClocks = new List<ClockView> { clock };
            PublicSnapshotView snapshot = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, scenario.Context(policy, fixtures.Select(value => value.Input).ToList(), clocks: mutableClocks));
            AssertClock(expectedClock, snapshot.View.Clocks);

            long[] scoresBefore = snapshot.View.Board!.Scores.Select(value => value.Score).ToArray();
            uint[] doraBefore = snapshot.View.Board!.DoraIndicators.ToArray();
            scenario.Scores[0] = 7;
            scenario.Dora[0] = 135;
            mutableClocks.Clear();
            Assert.Equal(scoresBefore, snapshot.View.Board!.Scores.Select(value => value.Score));
            Assert.Equal(doraBefore, snapshot.View.Board!.DoraIndicators);
            AssertClock(expectedClock, snapshot.View.Clocks);
            Assert.Equal(
                Int(expected.GetProperty("copyProbe"), "clocksAfterMutation"),
                snapshot.View.Clocks.Count);

            // A genuinely different explicit clock input produces a different new snapshot,
            // while the old snapshot retains the old value.
            var newClock = new ClockView(expectedClock.ServerTimeMs, expectedClock.DeadlineRemainingMs + 100, expectedClock.ReserveRemainingMs, expectedClock.DecisionSeat);
            PublicSnapshotView newer = TableVisibilityProjection.ProjectPublicSnapshot(
                scenario.Round, scenario.Context(policy, fixtures.Select(value => value.Input).ToList(), clocks: new[] { newClock }));
            Assert.NotEqual(expectedClock.DeadlineRemainingMs, newer.View.Clocks[0].DeadlineRemainingMs);
            AssertClock(expectedClock, snapshot.View.Clocks);

            Assert.Throws<NotSupportedException>(
                () => ((IList<uint>)snapshot.View.Board!.DoraIndicators).Add(0));
            Assert.Throws<NotSupportedException>(
                () => ((IList<MeldView>)snapshot.View.Board!.Players[1].Melds).Add(
                    new MeldView(ProtocolMeldType.Chi, new uint[] { 0, 1, 2 }, null, 0)));
            Assert.Throws<NotSupportedException>(
                () => ((IList<ClockView>)snapshot.View.Clocks).Add(new ClockView(0, 0, 0, 0)));

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
        public void G057dSeatViewsOwnerMappedPathAndCheatFactsMatchV4()
        {
            JsonElement item = Case("VIS-G057D-SEAT-VIEW-ISOLATION");
            JsonElement input = item.GetProperty("input");
            Scenario scenario = BuildScenario(input.GetProperty("state"));
            CheatState cheat = BuildCheat(input.GetProperty("state").GetProperty("privateCheat"));
            TableProjectionContext context = scenario.Context(new TableProjectionPolicy());
            JsonElement seatViews = item.GetProperty("expected").GetProperty("seatViews");
            var time = new TableProjectionTimeContext(0);

            for (int seat = 0; seat <= 2; seat++)
            {
                PlayerSnapshotView view = TableVisibilityProjection.ProjectPlayerSnapshot(
                    scenario.Round, cheat, seat, context);
                AssertSeatView(seatViews.GetProperty(seat.ToString(CultureInfo.InvariantCulture)), view);
                TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(
                    scenario.Round, cheat, seat, context);
                AssertCheat(input.GetProperty("state").GetProperty("privateCheat"), owner.Cheat, seat);
            }

            JsonElement seat1Expected = seatViews.GetProperty("1");
            TableOwnerPrivateView owner1 = TableVisibilityProjection.ProjectOwnerPrivate(
                scenario.Round, cheat, 1, context);
            Assert.Equal(Ints(seat1Expected.GetProperty("mySleeveTiles")).Select(value => (uint)value), owner1.SleeveTiles);
            Assert.Single(owner1.Melds);
            Assert.Equal(ProtocolMeldType.Chi, owner1.Melds[0].Type);
            Assert.Equal(new uint[] { 72, 76, 80 }, owner1.Melds[0].TileIds);
            Assert.Equal(0, owner1.Melds[0].FromSeat);
            Assert.Equal(72u, owner1.Melds[0].CalledTileId);

            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 1, context);
            PlayerSnapshotView mappedSeat1 = TableProjectionContractMapper.MapPlayerSnapshot(
                seat1, owner1, time);
            Assert.Equal(owner1.SleeveTiles, mappedSeat1.Private.SleeveTiles);
            Assert.Single(mappedSeat1.Private.OwnerMelds);
            Assert.Equal(72u, mappedSeat1.Private.OwnerMelds[0].CalledTileId);
            Assert.Equal(0, mappedSeat1.Private.OwnerMelds[0].FromSeat);
            Assert.Equal(990L, mappedSeat1.Private.Cheat!.CheatPoints);
            Assert.Single(mappedSeat1.Private.Cheat!.Ledger);
            Assert.Equal("vis-g057d-cheat-seat1", mappedSeat1.Private.Cheat!.Ledger[0].EntryId);
            Assert.Equal(-10L, mappedSeat1.Private.Cheat!.Ledger[0].Amount);
            Assert.Equal(990L, mappedSeat1.Private.Cheat!.Ledger[0].BalanceAfter);
            Assert.Equal("CHEAT_COST", mappedSeat1.Private.Cheat!.Ledger[0].ReasonCode);

            TableOwnerPrivateView owner0 = TableVisibilityProjection.ProjectOwnerPrivate(
                scenario.Round, cheat, 0, context);
            Assert.Empty(owner0.SleeveTiles);
            Assert.Empty(owner0.Cheat.Ledger);
            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, context);
            PlayerSnapshotView mappedSeat0 = TableProjectionContractMapper.MapPlayerSnapshot(
                seat0, owner0, time);
            Assert.Empty(mappedSeat0.Private.SleeveTiles);
            Assert.Null(mappedSeat0.Private.Decision);
            Assert.Equal(1000L, mappedSeat0.Private.Cheat!.CheatPoints);
            Assert.Empty(mappedSeat0.Private.Cheat!.Ledger);
        }

        [Fact]
        public void G057dNeighbourAndSpectatorNeverSeeHiddenIdentities()
        {
            JsonElement item = Case("VIS-G057D-SEAT-VIEW-ISOLATION");
            JsonElement input = item.GetProperty("input");
            Scenario scenario = BuildScenario(input.GetProperty("state"));
            CheatState cheat = BuildCheat(input.GetProperty("state").GetProperty("privateCheat"));
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
            PlayerSnapshotView mappedSeat0 = TableProjectionContractMapper.MapPlayerSnapshot(
                seat0,
                TableVisibilityProjection.ProjectOwnerPrivate(scenario.Round, cheat, 0, context),
                new TableProjectionTimeContext(0));

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

            foreach (int tile in Ints(expected.GetProperty("seatViews").GetProperty("1").GetProperty("mySleeveTiles")))
            {
                Assert.Contains((uint)tile, owner1.SleeveTiles);
                Assert.DoesNotContain(tile, seat0Ids);
                Assert.DoesNotContain(tile, PlayerTileIds(mappedSeat0));
            }

            foreach (string entryId in Strings(hidden.GetProperty("privateCheatEntryIds")))
            {
                Assert.DoesNotContain(
                    mappedSeat0.Private.Cheat!.Ledger,
                    value => string.Equals(value.EntryId, entryId, StringComparison.Ordinal));
                Assert.Contains(
                    owner1.Cheat.Ledger,
                    value => string.Equals(value.EntryId, entryId, StringComparison.Ordinal));
            }

            foreach (int balance in Ints(hidden.GetProperty("privateCheatBalances")))
            {
                Assert.NotEqual(balance, (int)mappedSeat0.Private.Cheat!.CheatPoints);
            }
        }

        [Fact]
        public void V5ProjectionCollectionsContextAndEventInputsAreReadOnlyOrCopied()
        {
            JsonElement item = Case("VIS-G057D-SEAT-VIEW-ISOLATION");
            JsonElement stateJson = item.GetProperty("input").GetProperty("state");
            Scenario scenario = BuildScenario(stateJson);
            CheatState cheat = V32Fixtures.Cheat();
            var clocks = new List<ClockView> { new ClockView(10, 20, 30, 0) };
            TableProjectionContext context = scenario.Context(new TableProjectionPolicy(), clocks: clocks);
            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                scenario.Round, cheat, 0, context);
            clocks.Clear();
            Assert.Single(context.Clocks);

            Assert.Throws<NotSupportedException>(
                () => ((IList<uint>)seat0.Private.ClosedTiles).Add(0));
            Assert.Throws<NotSupportedException>(
                () => ((IList<DiscardView>)seat0.Public.Board!.Players[0].Discards).Add(
                    new DiscardView(0, 0, false, false)));
            Assert.Throws<NotSupportedException>(
                () => ((IList<MeldView>)seat0.Public.Board!.Players[1].Melds).Add(
                    new MeldView(ProtocolMeldType.Chi, new uint[] { 0, 1, 2 }, null, 0)));

            uint[] meldTiles = { 10, 14, 18 };
            var meldFact = new TableMeldFact(ProtocolMeldType.Chi, meldTiles, 10, 0);
            meldTiles[0] = 22;
            Assert.Equal(10u, meldFact.TileIds[0]);
            var discards = new List<DiscardView> { new DiscardView(53, 0, false, false) };
            var publicFacts = new TableSeatPublicFacts(0, false, new[] { meldFact }, discards);
            discards.Clear();
            Assert.Single(publicFacts.Discards);

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
            ProtocolEventRecord mappedOwnerKan = TableProjectionContractMapper.MapEvent(
                ownerKan,
                new TableProjectionEventFacts(
                    turnNumber: 1,
                    meld: new MeldView(ProtocolMeldType.ClosedKan, new uint[] { 10, 11, 12, 13 }, null, 2)));
            Assert.Equal(4, mappedOwnerKan.ProjectionPayload!.Meld!.TileIds.Count);
            Assert.Throws<TableProjectionException>(() =>
                TableProjectionContractMapper.MapEvent(
                    spectatorKan,
                    new TableProjectionEventFacts(
                        turnNumber: 1,
                        meld: new MeldView(ProtocolMeldType.ClosedKan, new uint[] { 10, 11, 12, 13 }, null, 2))));
        }

        // ------------------------------------------------------------------
        // Fixture event model / facts
        // ------------------------------------------------------------------

        private sealed class FixtureEvent
        {
            public FixtureEvent(JsonElement descriptor, TableEventProjectionInput input)
            {
                Descriptor = descriptor;
                Input = input;
            }

            public JsonElement Descriptor { get; }

            public TableEventProjectionInput Input { get; }
        }

        private static List<FixtureEvent> BuildFixtureEvents(JsonElement events)
        {
            var result = new List<FixtureEvent>();
            int lastTurn = -1;
            uint ordinal = 0;
            foreach (JsonElement value in events.EnumerateArray())
            {
                int turn = Int(value, "turnNumber");
                Assert.True(turn >= lastTurn, "fixture event turns must be non-decreasing");
                lastTurn = turn;
                ProtocolEventType type = Enum.Parse<ProtocolEventType>(Str(value, "kind"), false);
                uint[] tiles = Ints(value.GetProperty("tileIds")).Select(tile => (uint)tile).ToArray();
                bool ura = value.TryGetProperty("isUraDora", out JsonElement uraValue) && uraValue.GetBoolean();
                long? cheatAmount = value.TryGetProperty("amount", out JsonElement amountValue)
                    ? amountValue.GetInt32()
                    : (long?)null;
                long? cheatBalance = value.TryGetProperty("newBalance", out JsonElement balanceValue)
                    ? balanceValue.GetInt32()
                    : (long?)null;
                var input = new TableEventProjectionInput(
                    type,
                    new EventSeq((ulong)result.Count + 1),
                    new StateVersion(1),
                    ordinal++,
                    Int(value, "seat"),
                    tiles,
                    isUraDora: ura,
                    cheatAmount: cheatAmount,
                    cheatBalance: cheatBalance);
                result.Add(new FixtureEvent(value, input));
            }

            return result;
        }

        private static List<FixtureEvent> VisibleFixtures(
            List<FixtureEvent> fixtures,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            return fixtures
                .Where(value => TableEventProjection.Project(value.Input, audience, policy) != null)
                .ToList();
        }

        private static TableProjectionEventFacts Facts(
            FixtureEvent fixture,
            bool uraAuthorized,
            bool includeBreakdown)
        {
            JsonElement value = fixture.Descriptor;
            ProtocolEventType type = fixture.Input.Type;
            int? fromSeat = value.TryGetProperty("fromSeat", out JsonElement fromSeatValue)
                ? fromSeatValue.GetInt32()
                : (int?)null;
            uint? calledTile = value.TryGetProperty("calledTileId", out JsonElement calledValue)
                ? (uint)calledValue.GetInt32()
                : (uint?)null;
            uint? wallRemaining = value.TryGetProperty("wallRemaining", out JsonElement wallValue)
                ? (uint)wallValue.GetInt32()
                : (uint?)null;
            bool? riichiDiscard = value.TryGetProperty("riichiDiscard", out JsonElement riichiValue)
                ? riichiValue.GetBoolean()
                : (bool?)null;
            bool? tsumogiri = value.TryGetProperty("tsumogiri", out JsonElement tsumogiriValue)
                ? tsumogiriValue.GetBoolean()
                : (bool?)null;
            uint? roundWind = value.TryGetProperty("roundWind", out JsonElement windValue)
                ? (uint)windValue.GetInt32()
                : (uint?)null;
            uint? roundNumber = value.TryGetProperty("roundNumber", out JsonElement numberValue)
                ? (uint)numberValue.GetInt32()
                : (uint?)null;
            int? loserSeat = value.TryGetProperty("loserSeat", out JsonElement loserValue)
                ? loserValue.GetInt32()
                : (int?)null;

            MeldView? meld = null;
            if (type == ProtocolEventType.ChiDeclared ||
                type == ProtocolEventType.PonDeclared ||
                type == ProtocolEventType.KanDeclared)
            {
                ProtocolMeldType meldType = type == ProtocolEventType.ChiDeclared
                    ? ProtocolMeldType.Chi
                    : type == ProtocolEventType.PonDeclared
                        ? ProtocolMeldType.Pon
                        : ParseMeldType(Str(value, "kanType"));
                meld = new MeldView(
                    meldType,
                    fixture.Input.TileIds,
                    calledTile,
                    fromSeat ?? ProtocolConstants.NoSeat);
            }

            IReadOnlyList<YakuView>? yaku = null;
            if (value.TryGetProperty("yaku", out JsonElement yakuValue))
            {
                var list = new List<YakuView>();
                foreach (JsonElement item in yakuValue.EnumerateArray())
                {
                    list.Add(new YakuView(Str(item, "code"), (uint)Int(item, "han")));
                }

                yaku = list;
            }

            ScoreBreakdownView? breakdown = null;
            if (includeBreakdown && value.TryGetProperty("breakdown", out JsonElement breakdownValue))
            {
                var yakuList = new List<YakuView>();
                foreach (JsonElement item in breakdownValue.GetProperty("yaku").EnumerateArray())
                {
                    yakuList.Add(new YakuView(Str(item, "code"), (uint)Int(item, "han")));
                }

                breakdown = new ScoreBreakdownView(
                    (uint)Int(breakdownValue, "han"),
                    (uint)Int(breakdownValue, "fu"),
                    Int(breakdownValue, "totalPoints"),
                    yakuList,
                    (uint)Int(breakdownValue, "doraCount"),
                    (uint)Int(breakdownValue, "uraCount"),
                    Ints(breakdownValue.GetProperty("uraTiles")).Select(tile => (uint)tile).ToArray(),
                    Ints(breakdownValue.GetProperty("uraHitTiles")).Select(tile => (uint)tile).ToArray(),
                    uraAuthorized);
            }

            return new TableProjectionEventFacts(
                turnNumber: (uint)Int(value, "turnNumber"),
                wallRemaining: wallRemaining,
                meld: meld,
                fromSeat: fromSeat,
                riichiDiscard: riichiDiscard,
                tsumogiri: tsumogiri,
                loserSeat: loserSeat,
                yaku: yaku,
                han: value.TryGetProperty("han", out JsonElement hanValue) ? (uint)hanValue.GetInt32() : (uint?)null,
                fu: value.TryGetProperty("fu", out JsonElement fuValue) ? (uint)fuValue.GetInt32() : (uint?)null,
                score: value.TryGetProperty("score", out JsonElement scoreValue) ? scoreValue.GetInt32() : (long?)null,
                breakdown: breakdown,
                roundWind: roundWind,
                roundNumber: roundNumber);
        }

        private static List<ProtocolEventRecord> MapAll(
            IReadOnlyList<TableProjectedEvent> projected,
            List<FixtureEvent> fixtures,
            bool includeBreakdown,
            bool uraAuthorized)
        {
            Assert.Equal(projected.Count, fixtures.Count);
            var result = new List<ProtocolEventRecord>(projected.Count);
            for (int index = 0; index < projected.Count; index++)
            {
                result.Add(TableProjectionContractMapper.MapEvent(
                    projected[index],
                    Facts(fixtures[index], uraAuthorized, includeBreakdown)));
            }

            return result;
        }

        private static List<ProtocolEventRecord> SettlementTyped(
            IReadOnlyList<TableProjectedEvent> projected,
            List<FixtureEvent> fixtures,
            bool includeBreakdown,
            bool uraAuthorized)
        {
            var result = new List<ProtocolEventRecord>();
            for (int index = 0; index < projected.Count; index++)
            {
                TableProjectedEvent value = projected[index];
                bool settlement = value.Type == ProtocolEventType.TsumoWin ||
                                  value.Type == ProtocolEventType.RonWin ||
                                  value.Type == ProtocolEventType.DrawGame ||
                                  value.Type == ProtocolEventType.ScoreSettled ||
                                  value.Type == ProtocolEventType.RoundEnded ||
                                  value.Type == ProtocolEventType.MatchEnded ||
                                  (value.Type == ProtocolEventType.DoraRevealed && value.IsUraDora);
                if (!settlement)
                {
                    continue;
                }

                result.Add(TableProjectionContractMapper.MapEvent(
                    value,
                    Facts(fixtures[index], uraAuthorized, includeBreakdown)));
            }

            return result;
        }

        private static void AssertScenarioEvents(
            JsonElement expected,
            List<FixtureEvent> fixtures,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            List<TableEventProjectionInput> inputs = fixtures.Select(value => value.Input).ToList();
            IReadOnlyList<TableProjectedEvent> projected = TableEventProjection.ProjectMany(inputs, audience, policy);
            AssertEventList(expected, projected);
            List<FixtureEvent> visible = VisibleFixtures(fixtures, audience, policy);
            Assert.Equal(projected.Count, visible.Count);
            for (int index = 0; index < projected.Count; index++)
            {
                JsonElement descriptor = expected[index];
                ProtocolEventRecord record = TableProjectionContractMapper.MapEvent(
                    projected[index],
                    Facts(visible[index], projected[index].IsUraDora, includeBreakdown: true));
                ProjectedEventPayload payload = record.ProjectionPayload!;
                Assert.Equal((uint)Int(descriptor, "turnNumber"), payload.TurnNumber);
                Assert.Equal(Int(descriptor, "seat"), payload.Seat);
                Assert.Equal(
                    Ints(descriptor.GetProperty("tileIds")).Select(tile => (uint)tile),
                    payload.TileIds);
                if (descriptor.TryGetProperty("fromSeat", out JsonElement fromSeat))
                {
                    Assert.Equal(fromSeat.GetInt32(), payload.FromSeat);
                }

                if (descriptor.TryGetProperty("calledTileId", out JsonElement called))
                {
                    Assert.NotNull(payload.Meld);
                    Assert.Equal((uint)called.GetInt32(), payload.Meld!.CalledTileId);
                }

                if (descriptor.TryGetProperty("riichiDiscard", out JsonElement riichi))
                {
                    Assert.Equal(riichi.GetBoolean(), payload.RiichiDiscard);
                    Assert.Equal(Bool(descriptor, "tsumogiri"), payload.Tsumogiri);
                }

                if (descriptor.TryGetProperty("wallRemaining", out JsonElement wall))
                {
                    Assert.Equal((uint)wall.GetInt32(), payload.WallRemaining);
                }

                if (descriptor.TryGetProperty("roundWind", out JsonElement wind))
                {
                    Assert.Equal((uint)wind.GetInt32(), payload.RoundWind);
                    Assert.Equal((uint)Int(descriptor, "roundNumber"), payload.RoundNumber);
                }
            }
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
            Assert.Equal(NullableInt(expected.GetProperty("lastDiscardPlayer")), DeriveLastDiscardSeat(board));

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
                    MeldView actualMeld = actual.Players[seat].Melds[index];
                    Assert.Equal(Str(meld, "type"), actualMeld.Type.ToString().ToUpperInvariant());
                    Assert.Equal(
                        Ints(meld.GetProperty("tileIds")).Select(value => (uint)value),
                        actualMeld.TileIds);
                    Assert.Equal(Int(meld, "fromSeat"), actualMeld.FromSeat);
                    Assert.Equal(NullableUInt(meld.GetProperty("calledTileId")), actualMeld.CalledTileId);
                }
            }

            Assert.Equal(
                NullableInt(expected.GetProperty("lastDiscard")),
                actual.LastDiscard == null ? (int?)null : (int)actual.LastDiscard.TileId);
        }

        private static void AssertCheat(JsonElement privateCheat, TableOwnerCheatView actual, int seat)
        {
            JsonElement points = privateCheat.GetProperty("points");
            Assert.Equal(points[seat].GetInt32(), actual.Balance);
            List<JsonElement> entries = privateCheat.GetProperty("entries").EnumerateArray()
                .Where(value => Int(value, "seat") == seat)
                .ToList();
            Assert.Equal(entries.Count, actual.Ledger.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                Assert.Equal(Str(entries[index], "entryId"), actual.Ledger[index].EntryId);
                Assert.Equal(Int(entries[index], "amount"), actual.Ledger[index].Amount);
                Assert.Equal(Int(entries[index], "balanceAfter"), actual.Ledger[index].BalanceAfter);
                Assert.Equal(Str(entries[index], "reason"), actual.Ledger[index].ReasonCode);
            }
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
        // Scenario construction and input consistency
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
                TableSessionProjection? session = null,
                IReadOnlyList<ClockView>? clocks = null)
            {
                return ContextFor(Round, policy, events, session, clocks);
            }

            public TableProjectionContext ContextFor(
                RoundStateMachine round,
                TableProjectionPolicy policy,
                IReadOnlyList<TableEventProjectionInput>? events = null,
                TableSessionProjection? session = null,
                IReadOnlyList<ClockView>? clocks = null)
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

                ulong version = Math.Max(unchecked((ulong)round.Version), eventVersion);
                var identity = new TableProjectionIdentity(
                    new SessionId("session-visibility-golden"),
                    new RoundId(round.RoundId),
                    new StateVersion(version),
                    new EventSeq(lastSeq),
                    new StreamCursor(new StreamVersion(0), new ProjectionSeq(0)),
                    new SessionVersion(1));
                return new TableProjectionContext(
                    identity,
                    policy,
                    clocks ?? Array.Empty<ClockView>(),
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

            Assert.Equal(Int(state, "wallRemaining"), wallCount);
            Assert.Equal(Int(state, "dealerSeat"), Int(state, "currentPlayer"));
            Assert.Equal(136, placements.Select(value => value.Tile.Value).Distinct().Count());

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
                    Assert.False(drawn.HasValue, "fixture expects at most one tracked drawn tile");
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

            AssertPlacementConsistency(state, placements);

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
            Assert.Equal(Int(state, "currentPlayer"), round.CurrentSeat);

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

        private static void AssertPlacementConsistency(JsonElement state, List<TilePlacement> placements)
        {
            JsonElement hands = state.GetProperty("hands");
            JsonElement discards = state.GetProperty("discards");
            JsonElement melds = state.GetProperty("melds");
            for (int seat = 0; seat < 4; seat++)
            {
                List<int> handTiles = placements
                    .Where(value => value.Position.Zone == TileZone.Hand && value.Position.Seat == seat)
                    .OrderBy(value => value.Position.Index)
                    .Select(value => value.Tile.Value)
                    .ToList();
                JsonElement hand = hands[seat];
                int[] closed = Ints(hand.GetProperty("closedTileIds"));
                int? drawn = NullableInt(hand.GetProperty("drawnTileId"));
                Assert.Equal(closed.Length + (drawn.HasValue ? 1 : 0), handTiles.Count);
                Assert.Equal(closed, handTiles.Take(closed.Length));
                if (drawn.HasValue)
                {
                    Assert.Equal(drawn.Value, handTiles[closed.Length]);
                }

                int[] river = placements
                    .Where(value => value.Position.Zone == TileZone.Discard && value.Position.Seat == seat)
                    .OrderBy(value => value.Position.Index)
                    .Select(value => value.Tile.Value)
                    .ToArray();
                Assert.Equal(Ints(discards[seat]), river);

                int[] sleeves = placements
                    .Where(value => value.Position.Zone == TileZone.Sleeve && value.Position.Seat == seat)
                    .OrderBy(value => value.Position.Index)
                    .Select(value => value.Tile.Value)
                    .ToArray();
                Assert.Equal(Ints(hand.GetProperty("sleeveTileIds")), sleeves);

                int meldIndex = 0;
                foreach (JsonElement meld in melds[seat].EnumerateArray())
                {
                    int[] expected = Ints(meld.GetProperty("tileIds"));
                    for (int offset = 0; offset < expected.Length; offset++)
                    {
                        int slot = meldIndex * 4 + offset;
                        TilePlacement placed = placements.Single(
                            value => value.Position.Zone == TileZone.Meld &&
                                     value.Position.Seat == seat &&
                                     value.Position.Index == slot);
                        Assert.Equal(expected[offset], placed.Tile.Value);
                    }

                    meldIndex++;
                }
            }
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

            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(points[seat].GetInt32(), state.ViewFor(seat).Balance);
            }

            return state;
        }

        private static ClockView ParseClock(JsonElement clock)
        {
            long serverNow = Int(clock, "serverNow");
            long deadline = Int(clock, "deadline");
            return new ClockView(
                serverNow,
                deadline - serverNow,
                Int(clock, "reserve"),
                Int(clock, "seat"));
        }

        private static void AssertClock(ClockView expected, IReadOnlyList<ClockView> actual)
        {
            Assert.Single(actual);
            Assert.Equal(expected.ServerTimeMs, actual[0].ServerTimeMs);
            Assert.Equal(expected.DeadlineRemainingMs, actual[0].DeadlineRemainingMs);
            Assert.Equal(expected.ReserveRemainingMs, actual[0].ReserveRemainingMs);
            Assert.Equal(expected.DecisionSeat, actual[0].DecisionSeat);
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

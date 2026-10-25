using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;
using Xunit.Sdk;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// N3 table cheat admission golden consumer binding.
    ///
    /// Source of truth: the V4 exporter
    /// <c>engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt:359-427</c> and the
    /// exported <c>tests/v5-golden/cheat-v1.json</c> tileMarkHandler section. The fixture only
    /// records the V4 handler verdict (outcome/reason) for each case; it contains no tileMark
    /// goldenInputs. Each input below is rebuilt from the exporter source branch that produces
    /// the verdict, not invented, and is then executed through the already accepted
    /// <see cref="TableActor.SubmitCheatAsync"/> -&gt; ValidateAuthorityIdentity -&gt;
    /// RoundCheatTransition.Prepare -&gt; real Domain path.
    ///
    /// The fixture path can be overridden with V5_TABLE_CHEAT_GOLDEN_PATH so the independent
    /// mutation probe can point the same compiled tests at single-field TEMP copies.
    ///
    /// Only one test-only reflection WRITE exists: <see cref="ClearClockTimings"/>. It injects
    /// the V4 "decision exists but timing is absent" state because the accepted actor API keeps
    /// _pending and _decisionClock atomic. That state is a fault injection, not a business-reachable
    /// state, and the test asserts the exact "not open in the clock" branch.
    /// </summary>
    public sealed class N3TableCheatGoldenAdmissionTests
    {
        private const string RoundId = "r-golden";
        private const string DecisionId = "d-1";
        private const int InitialPoints = 40;
        private const int TileMarkCost = 10;
        private const long Now = 1000L;
        private const long Deadline = 10000L;
        private const string FixtureSchema = "v5-cheat-golden-v1";
        private const string FixtureGenerator = "engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt";

        private static readonly string OpenPendingTiming =
            "0:" + DecisionId + ":" + Deadline + ";1:-:-;2:-:-;3:-:-";

        private static readonly string[] KnownTileMarkCaseIds =
        {
            "tm-allowed",
            "tm-not-your-turn",
            "tm-wrong-phase-preparation",
            "tm-wrong-phase-draw",
            "tm-wrong-phase-round-end",
            "tm-response-window",
            "tm-riichi-locked",
            "tm-invalid-selection-three",
            "tm-invalid-selection-duplicate",
            "tm-tile-not-owned",
            "tm-already-marked",
            "tm-capacity-groups",
            "tm-capacity-tiles",
            "tm-insufficient-funds",
            "tm-sleeve-pending",
            "tm-offturn-sleeve-pending",
            "tm-beam-pending",
            "tm-no-active-decision",
            "tm-deadline-expired",
            "tm-decision-mismatch"
        };

        /// <summary>
        /// The V4 verdict column read from the exporter and the C# typed reason the same branch
        /// must produce. The mapping is deliberately explicit: V4 groups "round absent",
        /// "round ended" and "decision identity" under DECISION_MISMATCH, while the C# actor
        /// keeps them as separate typed branches.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, OracleRow> Oracle =
            new Dictionary<string, OracleRow>(StringComparer.Ordinal)
            {
                ["tm-allowed"] = new OracleRow("Allowed", null),
                ["tm-not-your-turn"] = new OracleRow("Rejected", "NOT_YOUR_TURN"),
                ["tm-wrong-phase-preparation"] = new OracleRow("Rejected", "WRONG_PHASE"),
                ["tm-wrong-phase-draw"] = new OracleRow("Rejected", "WRONG_PHASE"),
                // V4 :410 checks phase == ROUND_END together with the absent-round guard and returns
                // DECISION_MISMATCH. C# keeps a bound round and rejects a finished round in
                // RoundCheatTransition.Guard, so the typed equivalent is ROUND_FINISHED.
                ["tm-wrong-phase-round-end"] = new OracleRow("Rejected", "DECISION_MISMATCH"),
                ["tm-response-window"] = new OracleRow("Rejected", "WRONG_PHASE"),
                ["tm-riichi-locked"] = new OracleRow("Rejected", "TILE_MARK_RIICHI_LOCKED"),
                ["tm-invalid-selection-three"] = new OracleRow("Rejected", "TILE_MARK_INVALID_SELECTION"),
                ["tm-invalid-selection-duplicate"] = new OracleRow("Rejected", "TILE_MARK_INVALID_SELECTION"),
                ["tm-tile-not-owned"] = new OracleRow("Rejected", "TILE_MARK_TILE_NOT_OWNED"),
                ["tm-already-marked"] = new OracleRow("Rejected", "TILE_MARK_ALREADY_MARKED"),
                ["tm-capacity-groups"] = new OracleRow("Rejected", "TILE_MARK_CAPACITY_EXCEEDED"),
                ["tm-capacity-tiles"] = new OracleRow("Rejected", "TILE_MARK_CAPACITY_EXCEEDED"),
                ["tm-insufficient-funds"] = new OracleRow("Rejected", "INSUFFICIENT_FUNDS"),
                ["tm-sleeve-pending"] = new OracleRow("Rejected", "SLEEVE_BLOOM_CHOICE_PENDING"),
                ["tm-offturn-sleeve-pending"] = new OracleRow("Rejected", "SLEEVE_BLOOM_CHOICE_PENDING"),
                ["tm-beam-pending"] = new OracleRow("Rejected", "BEAM_SWAP_CHOICE_PENDING"),
                // V4 :423 passes a decision but timing = null, so :82-84 returns NO_ACTIVE_DECISION.
                // C# has a pending decision whose clock timing is missing: DecisionMismatch at the
                // "not open in the clock" branch (TableActor.cs:1955-1961).
                ["tm-no-active-decision"] = new OracleRow("Rejected", "NO_ACTIVE_DECISION"),
                // V4 :424 sets now == deadline, so :85 returns DEADLINE_EXPIRED.
                ["tm-deadline-expired"] = new OracleRow("Rejected", "DEADLINE_EXPIRED"),
                // V4 :425 passes roundId = null (an absent active round) while the request keeps its
                // original identity, so :64-71 returns DECISION_MISMATCH. C# has a bound round and
                // rejects a command for another round in ValidateAuthorityIdentity as RoundMismatch.
                ["tm-decision-mismatch"] = new OracleRow("Rejected", "DECISION_MISMATCH")
            };

        [Fact]
        public async Task golden_tm_allowed_commits_once_and_replays_without_a_second_charge()
        {
            GoldenCase golden = LoadBoundCase("tm-allowed");
            Assert.Equal("Allowed", golden.Outcome);
            Assert.Null(golden.Reason);

            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            Assert.Equal(InitialPoints, GetCheat(harness.Actor)!.Ledger.BalanceOf(0));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: false))).Status);

            ObservedState before = await CaptureAsync(harness);
            Assert.Equal(0UL, before.StateVersion);
            Assert.Equal(InitialPoints, before.Balance);
            Assert.Equal(0, before.MarkGroupCount);
            Assert.Equal(OpenPendingTiming, before.PendingTiming);

            TableCheatCommandEnvelope envelope = Cheat(
                harness, "tm-allowed", RoundId, 0, new DecisionId(DecisionId));
            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Equal(1UL, result.CommandVersion!.Value.Value);
            Assert.Equal(3, result.Events.Count);
            Assert.Equal(RoundEventType.CheatApplied, result.Events[0].Type);
            Assert.False(result.Events[0].IsCheatEvent);
            TableEventRecord points = Assert.Single(
                result.Events, e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged);
            Assert.Equal(-TileMarkCost, points.Cheat!.Event.Amount);
            Assert.Equal(InitialPoints - TileMarkCost, points.Cheat.Event.NewBalance);
            TableEventRecord mark = Assert.Single(
                result.Events, e => e.Cheat?.Event.Kind == CheatEventKind.TileMarkApplied);
            Assert.Equal("g-golden", mark.Cheat!.Event.GroupId);
            Assert.Equal(0, mark.Seat);

            ObservedState after = await CaptureAsync(harness);
            Assert.Equal(before.StateVersion + 1UL, after.StateVersion);
            Assert.Equal(before.LastEventSeq + (ulong)result.Events.Count, after.LastEventSeq);
            Assert.Equal(before.RoundEventCount + 1, after.RoundEventCount);
            Assert.Equal(1, after.CommitCount);
            Assert.Equal(1, after.PublicationCount);
            Assert.Equal(InitialPoints - TileMarkCost, after.Balance);
            Assert.Equal(1, after.LedgerEntryCount);
            Assert.Equal(1, after.MarkGroupCount);
            Assert.Equal(4, after.MarkedTileCount);
            Assert.Equal(before.Placements, after.Placements);
            // The table command version above is the authoritative one-step version. The private
            // cheat aggregate advances its own internal counter per sub-transition, so assert it
            // moved forward rather than pretending it equals the command version.
            Assert.True(after.CheatVersion > before.CheatVersion);
            Assert.NotEqual(before.CheatFingerprint, after.CheatFingerprint);
            AssertPhysicalConservation(harness.Actor);

            // The exact original envelope must return the original receipt object, with no second
            // version, charge, mark, event or durable commit.
            TableOperationResult replay = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);
            Assert.Same(result, replay);
            ObservedState afterReplay = await CaptureAsync(harness);
            AssertUnchanged(after, afterReplay);
            Assert.Equal(1, afterReplay.CommitCount);
            Assert.Equal(InitialPoints - TileMarkCost, afterReplay.Balance);

            // Control-generation guard (not a V4 golden case): the same pending decision and a new
            // request id under control epoch 1 must not reuse the accepted control generation.
            TableCheatCommandEnvelope staleControl = Cheat(
                harness, "tm-allowed-stale-control", RoundId, 1, new DecisionId(DecisionId), control: 1UL);
            TableOperationResult stale = await harness.Actor.SubmitCheatAsync(
                staleControl, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, stale.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, stale.ReasonCode);
            Assert.Empty(stale.Events);
            Assert.Null(stale.CommandVersion);
            ObservedState afterStale = await CaptureAsync(harness);
            AssertUnchanged(afterReplay, afterStale);
            Assert.Equal(InitialPoints - TileMarkCost, afterStale.Balance);
            Assert.Equal(1, afterStale.CommitCount);
            AssertPhysicalConservation(harness.Actor);
        }

        [Fact]
        public async Task golden_tm_wrong_phase_round_end_rejects_with_the_terminated_round_reason()
        {
            GoldenCase golden = LoadBoundCase("tm-wrong-phase-round-end");
            Assert.Equal("Rejected", golden.Outcome);
            Assert.Equal("DECISION_MISMATCH", golden.Reason);

            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: false))).Status);

            long[] finalScores = { 26000, 24000, 25000, 25000 };
            var settlement = new RoundSettlement(
                "tm-round-end-result",
                RoundOutcome.Tsumo(0),
                finalScores,
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero)),
                1234L);
            TableOperationResult tsumo = await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Tsumo(round.RoundId, 0, settlement),
                new RequestId("tm-round-end-tsumo"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                new DecisionId(DecisionId)));
            Assert.Equal(TableOperationStatus.Accepted, tsumo.Status);

            ObservedState before = await CaptureAsync(harness);
            Assert.True(before.IsFinished);
            Assert.Equal(RoundPhase.RoundEnd, before.Phase);
            Assert.Equal(1UL, before.StateVersion);
            Assert.Equal(1, before.CommitCount);
            // The terminal transition itself consumed the open decision; no pending key survives.
            Assert.Equal("0:-:-;1:-:-;2:-:-;3:-:-", before.PendingTiming);
            Assert.Equal(136, before.UniqueTileCount);

            // V4 still shows a decision object at ROUND_END, but C# clears _pending on the terminal
            // transition. Supplying a decision id here would select the other (no-pending) branch,
            // so the representable equivalent carries the original round/seat/request identity and
            // no decision id, letting the terminal guard be the rejecting branch.
            TableCheatCommandEnvelope envelope = Cheat(
                harness, "tm-round-end", RoundId, 1, decision: null);
            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal("ROUND_FINISHED", result.ReasonCode);
            Assert.Contains("already finished", result.Detail, StringComparison.Ordinal);
            Assert.Empty(result.Events);
            Assert.Null(result.CommandVersion);

            ObservedState after = await CaptureAsync(harness);
            AssertUnchanged(before, after);
            Assert.True(after.IsFinished);
            Assert.Equal(1UL, after.StateVersion);
            Assert.Equal(1, after.CommitCount);
            Assert.Equal(1, after.PublicationCount);
            Assert.Equal(InitialPoints, after.Balance);
            Assert.Equal(0, after.MarkGroupCount);
            Assert.Equal(136, after.UniqueTileCount);

            // A finished round cannot be revived by opening a decision.
            TableOperationResult reopen = await harness.Actor.OpenDecisionAsync(
                Pending(harness, discard: true), TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, reopen.Status);
            Assert.Equal("ROUND_FINISHED", reopen.ReasonCode);
            ObservedState afterReopen = await CaptureAsync(harness);
            AssertUnchanged(after, afterReopen);
            AssertPhysicalConservation(harness.Actor);
        }

        [Fact]
        public async Task golden_tm_no_active_decision_rejects_when_pending_exists_without_clock_timing()
        {
            GoldenCase golden = LoadBoundCase("tm-no-active-decision");
            Assert.Equal("Rejected", golden.Outcome);
            Assert.Equal("NO_ACTIVE_DECISION", golden.Reason);

            await using ActorHarness harness = CreateHarness();
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: false))).Status);

            ObservedState beforeInjection = await CaptureAsync(harness);
            Assert.Equal(OpenPendingTiming, beforeInjection.PendingTiming);

            // Fault injection only: the accepted OpenDecisionAsync installs _pending and the clock
            // timing atomically, so "pending but no timing" is not business reachable. Clearing the
            // clock timings reproduces the V4 Input(timing = null, decision = decision(seat)) state
            // without touching the pending decision, ledger, round or cheat aggregate.
            ClearClockTimings(harness.Actor);
            ObservedState before = await CaptureAsync(harness);
            Assert.Equal("0:" + DecisionId + ":-;1:-:-;2:-:-;3:-:-", before.PendingTiming);
            AssertUnchangedExceptPending(beforeInjection, before);

            // The request definitely carries the active decision id, so this cannot be the
            // "request without a decision id" or "no pending decision" branch.
            TableCheatCommandEnvelope envelope = Cheat(
                harness, "tm-no-active-decision", RoundId, 0, new DecisionId(DecisionId));
            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, result.ReasonCode);
            Assert.Contains("not open in the clock", result.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("does not carry", result.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("not open for this seat", result.Detail, StringComparison.Ordinal);
            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Empty(result.Events);
            Assert.Null(result.CommandVersion);

            ObservedState after = await CaptureAsync(harness);
            AssertUnchanged(before, after);
            Assert.Equal(0UL, after.StateVersion);
            Assert.Equal(0, after.CommitCount);
            Assert.Equal(0, after.PublicationCount);
            Assert.Equal(InitialPoints, after.Balance);
            Assert.Equal(0, after.MarkGroupCount);
            Assert.Equal(0, after.MarkedTileCount);
            Assert.Equal(136, after.UniqueTileCount);
            AssertPhysicalConservation(harness.Actor);
        }

        [Fact]
        public async Task golden_tm_deadline_expired_rejects_at_the_exact_deadline_before_any_tick()
        {
            GoldenCase golden = LoadBoundCase("tm-deadline-expired");
            Assert.Equal("Rejected", golden.Outcome);
            Assert.Equal("DEADLINE_EXPIRED", golden.Reason);

            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: true))).Status);
            ObservedState before = await CaptureAsync(harness);
            Assert.Equal(OpenPendingTiming, before.PendingTiming);

            // Exactly at the deadline. Admission is processed before any tick, so the identity
            // check sees the still-open timing and rejects with zero side effects.
            harness.Clock.Now = Deadline;
            TableCheatCommandEnvelope envelope = Cheat(
                harness, "tm-deadline-expired", RoundId, 0, new DecisionId(DecisionId));
            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, result.ReasonCode);
            Assert.Contains("authoritative deadline", result.Detail, StringComparison.Ordinal);
            Assert.Empty(result.Events);
            Assert.Null(result.CommandVersion);

            ObservedState after = await CaptureAsync(harness);
            AssertUnchanged(before, after);
            Assert.Equal(0UL, after.StateVersion);
            Assert.Equal(0, after.CommitCount);
            Assert.Equal(0, after.PublicationCount);
            Assert.Equal(InitialPoints, after.Balance);
            Assert.Equal(0, after.MarkGroupCount);
            Assert.Equal(136, after.UniqueTileCount);
            // The rejection does not silently expire the decision; that is the tick's job.
            Assert.Equal(OpenPendingTiming, after.PendingTiming);
            AssertPhysicalConservation(harness.Actor);

            // Alternative ordering: a due authoritative tick first applies the real timeout, so the
            // table is legitimately not unchanged and the later cheat hits the no-pending branch.
            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TableOperationStatus.Accepted, tick.Status);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);
            ObservedState afterTick = await CaptureAsync(harness);
            Assert.Equal(1UL, afterTick.StateVersion);
            Assert.Equal(1, afterTick.CommitCount);
            Assert.Equal(1, afterTick.PublicationCount);
            Assert.NotEqual(before.Placements, afterTick.Placements);
            Assert.Equal("0:-:-;1:-:-;2:-:-;3:-:-", afterTick.PendingTiming);
            Assert.Equal(136, afterTick.UniqueTileCount);

            TableCheatCommandEnvelope afterTickEnvelope = Cheat(
                harness, "tm-deadline-after-tick", RoundId, 1, new DecisionId(DecisionId));
            TableOperationResult afterTickResult = await harness.Actor.SubmitCheatAsync(
                afterTickEnvelope, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, afterTickResult.Status);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, afterTickResult.ReasonCode);
            Assert.Contains("not open for this seat", afterTickResult.Detail, StringComparison.Ordinal);
            Assert.Empty(afterTickResult.Events);
            ObservedState afterLate = await CaptureAsync(harness);
            AssertUnchanged(afterTick, afterLate);
            AssertPhysicalConservation(harness.Actor);
        }

        [Fact]
        public async Task golden_tm_decision_mismatch_rejects_when_the_command_round_is_not_active()
        {
            GoldenCase golden = LoadBoundCase("tm-decision-mismatch");
            Assert.Equal("Rejected", golden.Outcome);
            Assert.Equal("DECISION_MISMATCH", golden.Reason);

            await using ActorHarness harness = CreateHarness();
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: false))).Status);
            ObservedState before = await CaptureAsync(harness);
            Assert.Equal(OpenPendingTiming, before.PendingTiming);

            // V4's roundIdGiven = null means the authority has no active round while the request
            // still names one. The C# actor always has a bound round, so the representable
            // equivalent is a command naming a round that is not the active round. The pending
            // decision is present and matches, so this is not the decision-id mismatch branch.
            TableCheatCommandEnvelope envelope = Cheat(
                harness, "tm-decision-mismatch", "r-stale-golden", 0, new DecisionId(DecisionId));
            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RoundMismatch, result.ReasonCode);
            Assert.Contains("does not match", result.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("decision", result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.Events);
            Assert.Null(result.CommandVersion);

            ObservedState after = await CaptureAsync(harness);
            AssertUnchanged(before, after);
            Assert.Equal(0UL, after.StateVersion);
            Assert.Equal(0, after.CommitCount);
            Assert.Equal(0, after.PublicationCount);
            Assert.Equal(InitialPoints, after.Balance);
            Assert.Equal(0, after.MarkGroupCount);
            Assert.Equal(OpenPendingTiming, after.PendingTiming);
            Assert.Equal(136, after.UniqueTileCount);
            AssertPhysicalConservation(harness.Actor);
        }

        [Fact]
        public void golden_tile_mark_handler_cases_are_strictly_typed_and_complete()
        {
            GoldenFixture fixture = LoadFixture();
            Assert.Equal(20, fixture.Cases.Count);
            Assert.Equal(KnownTileMarkCaseIds, fixture.Cases.Select(c => c.CaseId).ToArray());
            foreach (string id in Oracle.Keys)
            {
                Assert.True(fixture.CasesById.ContainsKey(id), "missing bound tileMark case " + id);
            }

            foreach (GoldenCase item in fixture.Cases)
            {
                if (!Oracle.ContainsKey(item.CaseId))
                {
                    throw new XunitException(
                        "tileMarkHandler exposes a case that the V4 exporter does not define: " + item.CaseId);
                }

                OracleRow row = Oracle[item.CaseId];
                Assert.Equal(row.V4Outcome, item.Outcome);
                Assert.Equal(row.V4Reason, item.Reason);
            }
        }

        private static GoldenCase LoadBoundCase(string caseId)
        {
            GoldenFixture fixture = LoadFixture();
            if (!fixture.CasesById.ContainsKey(caseId))
            {
                throw new XunitException("tileMarkHandler is missing the bound case " + caseId);
            }

            GoldenCase item = fixture.CasesById[caseId];
            OracleRow row = Oracle[caseId];
            // The fixture is the V4 oracle column; a changed outcome/reason must fail here rather
            // than being absorbed by a self-consistent reason map.
            Assert.Equal(row.V4Outcome, item.Outcome);
            Assert.Equal(row.V4Reason, item.Reason);
            return item;
        }

        private static GoldenFixture LoadFixture()
        {
            string path = ResolveFixturePath();
            byte[] bytes = File.ReadAllBytes(path);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            }
            catch (JsonException exception)
            {
                throw new XunitException("cheat-v1.json is not valid JSON: " + exception.Message);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                RequireKind(root, JsonValueKind.Object, "root");
                AssertExactProperties(
                    root,
                    "root",
                    "schema",
                    "generator",
                    "generatorVersion",
                    "v4Commit",
                    "rulesetVersion",
                    "generatedByCommand",
                    "extractionStatus",
                    "exportedAtNote",
                    "v4RulesSourceNote",
                    "slotConventionNote",
                    "ledger",
                    "tileMarkState",
                    "tileMarkHandler",
                    "sleeveBloomHandler",
                    "beamSwapHandler",
                    "tileMovement");

                string schema = RequireString(root, "schema", "root");
                if (!string.Equals(schema, FixtureSchema, StringComparison.Ordinal))
                {
                    throw new XunitException(
                        "cheat-v1.json schema is '" + schema + "', expected '" + FixtureSchema + "'.");
                }

                string generator = RequireString(root, "generator", "root");
                if (!string.Equals(generator, FixtureGenerator, StringComparison.Ordinal))
                {
                    throw new XunitException(
                        "cheat-v1.json generator is '" + generator + "', expected '" + FixtureGenerator + "'.");
                }

                int generatorVersion = RequireInt32(root, "generatorVersion", "root");
                if (generatorVersion != 1)
                {
                    throw new XunitException("cheat-v1.json generatorVersion must be 1, actual " + generatorVersion + ".");
                }

                string status = RequireString(root, "extractionStatus", "root");
                if (!string.Equals(status, "exported", StringComparison.Ordinal))
                {
                    throw new XunitException("cheat-v1.json extractionStatus must be 'exported', actual '" + status + "'.");
                }

                if (!root.TryGetProperty("tileMarkHandler", out JsonElement handler))
                {
                    throw new XunitException("cheat-v1.json is missing tileMarkHandler.");
                }

                RequireKind(handler, JsonValueKind.Array, "tileMarkHandler");
                var cases = new List<GoldenCase>();
                var byId = new Dictionary<string, GoldenCase>(StringComparer.Ordinal);
                int index = 0;
                foreach (JsonElement element in handler.EnumerateArray())
                {
                    string context = "tileMarkHandler[" + index + "]";
                    RequireKind(element, JsonValueKind.Object, context);
                    AssertExactProperties(element, context, "caseId", "note", "outcome", "reason");
                    string caseId = RequireString(element, "caseId", context);
                    string note = RequireString(element, "note", context);
                    string outcome = RequireString(element, "outcome", context);
                    if (!string.Equals(outcome, "Allowed", StringComparison.Ordinal) &&
                        !string.Equals(outcome, "Rejected", StringComparison.Ordinal))
                    {
                        throw new XunitException(
                            context + ".outcome must be Allowed or Rejected, actual '" + outcome + "'.");
                    }

                    if (!element.TryGetProperty("reason", out JsonElement reasonElement))
                    {
                        throw new XunitException(context + " is missing 'reason'.");
                    }

                    string? reason;
                    if (reasonElement.ValueKind == JsonValueKind.Null)
                    {
                        reason = null;
                    }
                    else if (reasonElement.ValueKind == JsonValueKind.String)
                    {
                        reason = reasonElement.GetString();
                        if (string.IsNullOrEmpty(reason))
                        {
                            throw new XunitException(context + ".reason must be non-empty when it is a string.");
                        }
                    }
                    else
                    {
                        throw new XunitException(
                            context + ".reason must be a string or null, actual " + reasonElement.ValueKind + ".");
                    }

                    if (string.Equals(outcome, "Allowed", StringComparison.Ordinal) && reason != null)
                    {
                        throw new XunitException(context + " is Allowed but carries reason '" + reason + "'.");
                    }

                    if (string.Equals(outcome, "Rejected", StringComparison.Ordinal) && reason == null)
                    {
                        throw new XunitException(context + " is Rejected but has a null reason.");
                    }

                    if (Array.IndexOf(KnownTileMarkCaseIds, caseId) < 0)
                    {
                        throw new XunitException(
                            context + " has a caseId that the V4 exporter does not define: '" + caseId + "'.");
                    }

                    var item = new GoldenCase(caseId, note, outcome, reason);
                    if (byId.ContainsKey(caseId))
                    {
                        throw new XunitException("tileMarkHandler has a duplicate caseId '" + caseId + "'.");
                    }

                    byId.Add(caseId, item);
                    cases.Add(item);
                    index++;
                }

                if (cases.Count == 0)
                {
                    throw new XunitException("tileMarkHandler must contain the 20 V4 cases, actual 0.");
                }

                foreach (string required in KnownTileMarkCaseIds)
                {
                    if (!byId.ContainsKey(required))
                    {
                        throw new XunitException("tileMarkHandler is missing V4 case '" + required + "'.");
                    }
                }

                return new GoldenFixture(cases, byId);
            }
        }

        private static string ResolveFixturePath()
        {
            string? overridden = Environment.GetEnvironmentVariable("V5_TABLE_CHEAT_GOLDEN_PATH");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                string full = Path.GetFullPath(overridden);
                if (!File.Exists(full))
                {
                    throw new XunitException(
                        "V5_TABLE_CHEAT_GOLDEN_PATH points to a missing fixture: " + full);
                }

                return full;
            }

            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(
                    directory.FullName, "tests", "v5-golden", "cheat-v1.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new XunitException(
                "Could not locate tests/v5-golden/cheat-v1.json above " + AppContext.BaseDirectory +
                " and V5_TABLE_CHEAT_GOLDEN_PATH is not set.");
        }

        private static void RequireKind(JsonElement element, JsonValueKind expected, string context)
        {
            if (element.ValueKind != expected)
            {
                throw new XunitException(
                    context + " must be " + expected + ", actual " + element.ValueKind + ".");
            }
        }

        private static void AssertExactProperties(JsonElement element, string context, params string[] required)
        {
            var seen = new List<string>();
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (seen.Contains(property.Name, StringComparer.Ordinal))
                {
                    throw new XunitException(context + " has a duplicate property '" + property.Name + "'.");
                }

                seen.Add(property.Name);
            }

            foreach (string name in required)
            {
                if (!seen.Contains(name, StringComparer.Ordinal))
                {
                    throw new XunitException(context + " is missing property '" + name + "'.");
                }
            }

            foreach (string name in seen)
            {
                if (Array.IndexOf(required, name) < 0)
                {
                    throw new XunitException(context + " has an unknown property '" + name + "'.");
                }
            }
        }

        private static string RequireString(JsonElement element, string name, string context)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                throw new XunitException(context + " is missing property '" + name + "'.");
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new XunitException(
                    context + "." + name + " must be a string, actual " + value.ValueKind + ".");
            }

            return value.GetString()!;
        }

        private static int RequireInt32(JsonElement element, string name, string context)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                throw new XunitException(context + " is missing property '" + name + "'.");
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            {
                throw new XunitException(
                    context + "." + name + " must be a 32-bit integer, actual " + value.ValueKind + ".");
            }

            return number;
        }

        private static ActorHarness CreateHarness()
        {
            var clock = new ManualTableClock { Now = Now };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt(RoundId);
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [CheatEconomyConfig.TileMarkCostKey] = TileMarkCost,
                [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                [CheatEconomyConfig.BeamSwapCostKey] = 10
            };
            var economy = new CheatEconomyConfig(true, InitialPoints, costs);
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                CheatState.Initialize(economy), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(Deadline - Now, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = economy,
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        private static PendingDecision Pending(ActorHarness harness, bool discard)
        {
            RoundStateMachine round = harness.InitialRound;
            int seat = round.CurrentSeat;
            var candidates = new List<LegalActionOption>();
            if (discard)
            {
                TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, tile));
            }
            else
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Tsumo, seat, null));
            }

            return new PendingDecision(
                new DecisionKey(round.RoundId, seat, DecisionId),
                0L,
                candidates,
                round.Tiles.DrawnTile?.Tile);
        }

        private static TableCheatCommandEnvelope Cheat(
            ActorHarness harness,
            string requestId,
            string roundId,
            int expectedVersion,
            DecisionId? decision,
            ulong control = 0UL)
        {
            TileId[] tiles = HandTiles(GetRound(harness.Actor));
            byte[] payload = Encoding.UTF8.GetBytes(
                "TM|" + requestId + "|" + roundId + "|" + expectedVersion + "|" +
                (decision.HasValue ? decision.Value.Value : "-") + "|" +
                string.Join(",", tiles.Select(t => t.Value)));
            return new TableCheatCommandEnvelope(
                new RoundCheatCommand(
                    roundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    requestId,
                    RoundCheatPayload.TileMark("g-golden", tiles)),
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1UL),
                new ControlEpoch(control),
                decision,
                0L,
                new RequestScopeId("scope-golden"),
                "CHEAT",
                TablePayloadSha256.Compute(payload));
        }

        private static TileId[] HandTiles(RoundStateMachine round)
        {
            // The exact V4 entity ids belong to the V4 deal. The source input class is "four owned,
            // unmarked closed-hand tiles", so take four distinct tiles from the live C# hand.
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            if (tiles.Length != 4)
            {
                throw new XunitException("The live round hand does not expose four tiles for seat 0.");
            }

            return tiles;
        }

        private static async Task<ObservedState> CaptureAsync(ActorHarness harness)
        {
            TableActor actor = harness.Actor;
            TableSnapshot snapshot = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            TableTimingSnapshot timing = await actor.GetTimingSnapshotAsync(
                TestContext.Current.CancellationToken);
            RoundStateMachine round = GetRound(actor);
            CheatState cheat = GetCheat(actor)!;
            IReadOnlyList<TilePlacement> placements = round.Tiles.AsPlacements();
            return new ObservedState
            {
                StateVersion = snapshot.StateVersion.Value,
                LastEventSeq = snapshot.LastEventSeq.Value,
                StateFingerprint = snapshot.StateFingerprint,
                IsFinished = snapshot.IsFinished,
                Phase = snapshot.Phase,
                CurrentSeat = snapshot.CurrentSeat,
                Scores = string.Join(",", snapshot.Scores),
                Placements = Placements(round),
                UniqueTileCount = placements.Select(p => p.Tile.Value).Distinct().Count(),
                RoundEventCount = round.Events.Count,
                RoundDomainVersion = round.Version,
                CheatVersion = cheat.Version,
                CheatFingerprint = cheat.Fingerprint,
                Balance = cheat.Ledger.BalanceOf(0),
                LedgerEntryCount = cheat.Ledger.Entries.Count,
                MarkGroupCount = cheat.TileMarks.Groups.Count,
                MarkedTileCount = cheat.TileMarks.Groups.Sum(g => g.TileIds.Count),
                MarkedTiles = string.Join(",", cheat.TileMarks.Groups
                    .SelectMany(g => g.TileIds)
                    .Select(t => t.Value)
                    .OrderBy(v => v)),
                PendingTiming = string.Join(";", timing.Seats.Select(seat =>
                    seat.Seat + ":" +
                    (seat.PendingKey.HasValue ? seat.PendingKey.Value.DecisionId : "-") + ":" +
                    (seat.Timing != null ? seat.Timing.DeadlineMillis.ToString() : "-"))),
                CommitCount = harness.CommitPort.Requests.Count,
                PublicationCount = harness.Sink.Publications.Count
            };
        }

        private static string Placements(RoundStateMachine round)
        {
            return string.Join(";", round.Tiles.AsPlacements()
                .OrderBy(p => p.Tile.Value)
                .Select(p => p.Tile.Value + ":" + p.Position.Zone + ":" +
                             p.Position.Seat + ":" + p.Position.Index));
        }

        private static void AssertUnchanged(ObservedState before, ObservedState after)
        {
            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.LastEventSeq, after.LastEventSeq);
            Assert.Equal(before.StateFingerprint, after.StateFingerprint);
            Assert.Equal(before.IsFinished, after.IsFinished);
            Assert.Equal(before.Phase, after.Phase);
            Assert.Equal(before.CurrentSeat, after.CurrentSeat);
            Assert.Equal(before.Scores, after.Scores);
            Assert.Equal(before.Placements, after.Placements);
            Assert.Equal(before.RoundEventCount, after.RoundEventCount);
            Assert.Equal(before.RoundDomainVersion, after.RoundDomainVersion);
            Assert.Equal(before.CheatVersion, after.CheatVersion);
            Assert.Equal(before.CheatFingerprint, after.CheatFingerprint);
            Assert.Equal(before.Balance, after.Balance);
            Assert.Equal(before.LedgerEntryCount, after.LedgerEntryCount);
            Assert.Equal(before.MarkGroupCount, after.MarkGroupCount);
            Assert.Equal(before.MarkedTileCount, after.MarkedTileCount);
            Assert.Equal(before.MarkedTiles, after.MarkedTiles);
            Assert.Equal(before.PendingTiming, after.PendingTiming);
            Assert.Equal(before.CommitCount, after.CommitCount);
            Assert.Equal(before.PublicationCount, after.PublicationCount);
        }

        private static void AssertUnchangedExceptPending(ObservedState before, ObservedState after)
        {
            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.LastEventSeq, after.LastEventSeq);
            Assert.Equal(before.StateFingerprint, after.StateFingerprint);
            Assert.Equal(before.IsFinished, after.IsFinished);
            Assert.Equal(before.Phase, after.Phase);
            Assert.Equal(before.CurrentSeat, after.CurrentSeat);
            Assert.Equal(before.Scores, after.Scores);
            Assert.Equal(before.Placements, after.Placements);
            Assert.Equal(before.RoundEventCount, after.RoundEventCount);
            Assert.Equal(before.RoundDomainVersion, after.RoundDomainVersion);
            Assert.Equal(before.CheatVersion, after.CheatVersion);
            Assert.Equal(before.CheatFingerprint, after.CheatFingerprint);
            Assert.Equal(before.Balance, after.Balance);
            Assert.Equal(before.LedgerEntryCount, after.LedgerEntryCount);
            Assert.Equal(before.MarkGroupCount, after.MarkGroupCount);
            Assert.Equal(before.MarkedTileCount, after.MarkedTileCount);
            Assert.Equal(before.MarkedTiles, after.MarkedTiles);
            Assert.Equal(before.CommitCount, after.CommitCount);
            Assert.Equal(before.PublicationCount, after.PublicationCount);
        }

        private static void AssertPhysicalConservation(TableActor actor)
        {
            RoundStateMachine round = GetRound(actor);
            IReadOnlyList<TilePlacement> placements = round.Tiles.AsPlacements();
            Assert.Equal(TileId.Count, placements.Count);
            Assert.Equal(
                TileId.Count,
                placements.Select(p => p.Tile.Value).Distinct().Count());
            Assert.All(placements, p => Assert.InRange(p.Tile.Value, 0, TileId.Count - 1));
        }

        /// <summary>
        /// Test-only fault injection: clears the Domain clock timings while preserving the actor's
        /// pending decisions. The accepted public OpenDecisionAsync installs both atomically, so
        /// this state is not reachable through the business API and is never claimed as such.
        /// </summary>
        private static void ClearClockTimings(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField(
                "_decisionClock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var clock = (DecisionClock)field.GetValue(actor)!;
            clock.ClearTimings();
        }

        private static RoundStateMachine GetRound(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField(
                "_round", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (RoundStateMachine)field.GetValue(actor)!;
        }

        private static CheatState? GetCheat(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField(
                "_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (CheatState?)field.GetValue(actor);
        }

        private sealed class GoldenCase
        {
            public GoldenCase(string caseId, string note, string outcome, string? reason)
            {
                CaseId = caseId;
                Note = note;
                Outcome = outcome;
                Reason = reason;
            }

            public string CaseId { get; }

            public string Note { get; }

            public string Outcome { get; }

            public string? Reason { get; }
        }

        private sealed class GoldenFixture
        {
            public GoldenFixture(
                IReadOnlyList<GoldenCase> cases,
                IReadOnlyDictionary<string, GoldenCase> byId)
            {
                Cases = cases;
                CasesById = byId;
            }

            public IReadOnlyList<GoldenCase> Cases { get; }

            public IReadOnlyDictionary<string, GoldenCase> CasesById { get; }
        }

        private sealed class OracleRow
        {
            public OracleRow(string v4Outcome, string? v4Reason)
            {
                V4Outcome = v4Outcome;
                V4Reason = v4Reason;
            }

            public string V4Outcome { get; }

            public string? V4Reason { get; }
        }

        private sealed class ObservedState
        {
            public ulong StateVersion;
            public ulong LastEventSeq;
            public ulong StateFingerprint;
            public bool IsFinished;
            public RoundPhase Phase;
            public int CurrentSeat;
            public string Scores = string.Empty;
            public string Placements = string.Empty;
            public int RoundEventCount;
            public long RoundDomainVersion;
            public long CheatVersion;
            public ulong CheatFingerprint;
            public int Balance;
            public int LedgerEntryCount;
            public int MarkGroupCount;
            public int MarkedTileCount;
            public string MarkedTiles = string.Empty;
            public string PendingTiming = string.Empty;
            public int CommitCount;
            public int PublicationCount;
            public int UniqueTileCount;
        }
    }
}

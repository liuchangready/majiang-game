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
    /// Binding readiness is deliberately 4/5, not 5/5. <c>tm-decision-mismatch</c> is the V4
    /// "authority has no active round" input; the accepted actor always owns a concrete
    /// <c>_round</c> and there is no safe null-round port. It is declared BLOCKED by
    /// <see cref="BindingCases"/> and is never counted as bound. The wrong-round guard is kept
    /// only as <c>supplementary_stale_round_identity_is_rejected_with_round_mismatch</c>.
    ///
    /// Two bound cases need test-only reflection WRITES:
    /// <see cref="ClearClockTimings"/> removes the clock timing while keeping the pending decision
    /// (<c>tm-no-active-decision</c>), and <see cref="InjectPendingAndTiming"/> re-installs the
    /// original pending decision + timing after a real terminal Tsumo
    /// (<c>tm-wrong-phase-round-end</c>, preserving the original decision id). Both states are
    /// fault injections, not business-reachable states, and the tests assert the exact branches.
    ///
    /// The fixture path can be overridden with V5_TABLE_CHEAT_GOLDEN_PATH so the independent
    /// mutation probe can point the same compiled tests at single-field TEMP copies.
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

        /// <summary>
        /// Machine-readable readiness ledger consumed by probe-table-cheat-golden.ps1. It declares
        /// the real eligibility, not just green method names: four bound source-input cases (two of
        /// them fault-injection reproductions) and one explicitly blocked source case.
        /// </summary>
        private const string ManifestDisplayName =
            "N3-BINDING-MANIFEST|denominator=5|bound=4|blocked=1|" +
            "boundCases=tm-allowed,tm-wrong-phase-round-end,tm-no-active-decision,tm-deadline-expired|" +
            "blockedCases=tm-decision-mismatch|" +
            "faultInjection=tm-wrong-phase-round-end,tm-no-active-decision|" +
            "reason=NO_ACTIVE_ROUND_NOT_REPRESENTABLE";

        private static readonly string OpenPendingTiming =
            "0:" + DecisionId + ":" + Deadline + ";1:-:-;2:-:-;3:-:-";

        private static readonly BindingCase[] BindingCases =
        {
            new BindingCase(
                "tm-allowed",
                BindingStatus.Bound,
                "golden_tm_allowed_commits_once_and_replays_without_a_second_charge",
                "source input expressed with a real RemoteHuman seat; host sessionId and " +
                "RemoteHuman(autoRiichiDiscard=false) have no actor cheat-envelope field but are " +
                "not read by the tile-mark verdict",
                "sessionId,autoRiichiDiscard"),
            new BindingCase(
                "tm-wrong-phase-round-end",
                BindingStatus.BoundFaultInjection,
                "golden_tm_wrong_phase_round_end_rejects_with_the_terminated_round_reason",
                "ROUND_END with the original pending decision/timing and decision id re-injected " +
                "after a real terminal Tsumo via test-only reflection; not business reachable",
                "sessionId,autoRiichiDiscard"),
            new BindingCase(
                "tm-no-active-decision",
                BindingStatus.BoundFaultInjection,
                "golden_tm_no_active_decision_rejects_when_pending_exists_without_clock_timing",
                "pending decision kept while the clock timing is cleared via test-only reflection; " +
                "not business reachable because OpenDecisionAsync installs both atomically",
                "sessionId,autoRiichiDiscard"),
            new BindingCase(
                "tm-deadline-expired",
                BindingStatus.Bound,
                "golden_tm_deadline_expired_rejects_at_the_exact_deadline_before_any_tick",
                "source input expressed with a real RemoteHuman seat and now == authoritative deadline",
                "sessionId,autoRiichiDiscard"),
            new BindingCase(
                "tm-decision-mismatch",
                BindingStatus.Blocked,
                null,
                "V4 roundIdGiven=null means the authority has no active round; the accepted actor " +
                "always owns a concrete _round and has no safe null-round port. The stale-round " +
                "supplementary guard is not this source case.",
                "sessionId,autoRiichiDiscard,noActiveRoundState")
        };

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
                // original identity, so :64-71 returns DECISION_MISMATCH. This source input is
                // BLOCKED for the accepted actor: it always owns a concrete _round and there is no
                // safe null-round port. The fixture value is still validated for structure, and a
                // separate supplementary test covers the wrong-round guard without claiming this
                // source case.
                ["tm-decision-mismatch"] = new OracleRow("Rejected", "DECISION_MISMATCH")
            };

        [Fact]
        public async Task golden_tm_allowed_commits_once_and_replays_without_a_second_charge()
        {
            GoldenCase golden = LoadBoundCase("tm-allowed");
            Assert.Equal("Allowed", golden.Outcome);
            Assert.Null(golden.Reason);

            await using ActorHarness harness = CreateHarness();
            await AssertRemoteHumanControllers(harness);
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
            await AssertRemoteHumanControllers(harness);

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
            await AssertRemoteHumanControllers(harness);
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

            // The terminal transition itself consumed the open decision. To reproduce the V4
            // source input exactly (ROUND_END and the original decision still present), re-install
            // the same pending decision and its clock timing. This is a test-only fault injection:
            // a finished round with a pending decision is not business reachable.
            InjectPendingAndTiming(harness.Actor, Pending(harness, discard: false));

            ObservedState before = await CaptureAsync(harness);
            Assert.True(before.IsFinished);
            Assert.Equal(RoundPhase.RoundEnd, before.Phase);
            Assert.Equal(1UL, before.StateVersion);
            Assert.Equal(1, before.CommitCount);
            Assert.Equal(OpenPendingTiming, before.PendingTiming);
            Assert.Equal(136, before.UniqueTileCount);
            await AssertRemoteHumanControllers(harness);

            // The source request keeps its original decision identity; the terminal guard, not the
            // no-pending branch, must reject it.
            TableCheatCommandEnvelope envelope = Cheat(
                harness, "tm-round-end", RoundId, 1, new DecisionId(DecisionId));
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
            await AssertRemoteHumanControllers(harness);
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
            await AssertRemoteHumanControllers(harness);

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
            await AssertRemoteHumanControllers(harness);
            RoundStateMachine round = harness.InitialRound;
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: true))).Status);
            ObservedState before = await CaptureAsync(harness);
            Assert.Equal(OpenPendingTiming, before.PendingTiming);

            // Exactly at the deadline. Admission is processed before any tick, so the identity
            // check sees the still-open timing and rejects with zero side effects.
            harness.Clock.Now = Deadline;
            await AssertRemoteHumanControllers(harness);
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
        public async Task supplementary_stale_round_identity_is_rejected_with_round_mismatch()
        {
            // SUPPLEMENTARY C# COVERAGE ONLY. This is NOT the V4 tm-decision-mismatch source case:
            // that case's authority has no active round (roundIdGiven=null), which the accepted
            // actor cannot represent. This test keeps the wrong-round identity guard honest without
            // counting toward binding readiness.
            await using ActorHarness harness = CreateHarness();
            await AssertRemoteHumanControllers(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(Pending(harness, discard: false))).Status);
            ObservedState before = await CaptureAsync(harness);
            Assert.Equal(OpenPendingTiming, before.PendingTiming);
            await AssertRemoteHumanControllers(harness);

            TableCheatCommandEnvelope envelope = Cheat(
                harness, "supplementary-stale-round", "r-stale-golden", 0, new DecisionId(DecisionId));
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

        [Fact(DisplayName = ManifestDisplayName)]
        public void binding_readiness_manifest_is_explicit()
        {
            Assert.Equal(5, BindingCases.Length);
            BindingCase[] bound = BindingCases
                .Where(c => c.Status != BindingStatus.Blocked)
                .ToArray();
            BindingCase[] blocked = BindingCases
                .Where(c => c.Status == BindingStatus.Blocked)
                .ToArray();

            Assert.Equal(4, bound.Length);
            BindingCase blockedCase = Assert.Single(blocked);
            Assert.Equal("tm-decision-mismatch", blockedCase.CaseId);
            Assert.False(string.IsNullOrWhiteSpace(blockedCase.Reason));
            Assert.True(string.IsNullOrWhiteSpace(blockedCase.BindingMethod));
            Assert.All(bound, c => Assert.False(string.IsNullOrWhiteSpace(c.BindingMethod)));
            Assert.All(bound, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
            Assert.Equal(2, bound.Count(c => c.Status == BindingStatus.BoundFaultInjection));
            Assert.Equal(2, bound.Count(c => c.Status == BindingStatus.Bound));
            Assert.Contains("denominator=5", ManifestDisplayName, StringComparison.Ordinal);
            Assert.Contains("bound=4", ManifestDisplayName, StringComparison.Ordinal);
            Assert.Contains("blocked=1", ManifestDisplayName, StringComparison.Ordinal);
            Assert.Contains(
                "blockedCases=tm-decision-mismatch",
                ManifestDisplayName,
                StringComparison.Ordinal);
        }

        [Fact]
        public void golden_tile_mark_handler_cases_are_strictly_typed_and_complete()
        {
            GoldenFixture fixture = LoadFixture();
            Assert.Equal(20, fixture.Cases.Count);
            Assert.Equal(KnownTileMarkCaseIds, fixture.Cases.Select(c => c.CaseId).ToArray());
            foreach (string id in Oracle.Keys)
            {
                Assert.True(fixture.CasesById.ContainsKey(id), "missing tileMark case " + id);
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
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = economy,
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        /// <summary>
        /// The V4 source uses <c>SeatController.RemoteHuman(false)</c>. The accepted actor expresses
        /// the controller KIND through <see cref="TableActorOptions.InitialControllers"/>; read the
        /// real control snapshot and assert the kind/connection/control epoch before execution.
        /// The <c>autoRiichiDiscard=false</c> flag is not part of the C# control view and is not
        /// read by the tile-mark admission branch; it is recorded as a host gap, not invented.
        /// </summary>
        private static async Task AssertRemoteHumanControllers(ActorHarness harness)
        {
            TableControlSnapshot control = await harness.Actor.GetControlSnapshotAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(4, control.Seats.Count);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(seat, control.Seats[seat].Seat);
                Assert.Equal(SeatControllerKind.RemoteHuman, control.Seats[seat].Kind);
                Assert.Equal(ConnectionStatus.Connected, control.Seats[seat].Status);
                Assert.Equal(0L, control.Seats[seat].ControlEpoch);
            }
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
            GetDecisionClock(actor).ClearTimings();
        }

        /// <summary>
        /// Test-only fault injection: re-installs the original pending decision and its clock timing
        /// after a real terminal Tsumo consumed them. This preserves the V4 ROUND_END source input
        /// (finished round + pending decision + original decision id) while remaining an abnormal,
        /// non-business-reachable state. Production terminal cleanup is not modified.
        /// </summary>
        private static void InjectPendingAndTiming(TableActor actor, PendingDecision decision)
        {
            FieldInfo field = typeof(TableActor).GetField(
                "_pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Dictionary<int, PendingDecision>)field.GetValue(actor)!;
            pending[decision.Key.Seat] = decision;

            DecisionClock clock = GetDecisionClock(actor);
            clock.ClearTimings();
            clock.Open(decision.Key, Now);
        }

        private static DecisionClock GetDecisionClock(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField(
                "_decisionClock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (DecisionClock)field.GetValue(actor)!;
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

        private enum BindingStatus
        {
            Bound = 0,
            BoundFaultInjection = 1,
            Blocked = 2
        }

        private sealed class BindingCase
        {
            public BindingCase(
                string caseId,
                BindingStatus status,
                string? bindingMethod,
                string reason,
                string hostGaps)
            {
                CaseId = caseId;
                Status = status;
                BindingMethod = bindingMethod;
                Reason = reason;
                HostGaps = hostGaps;
            }

            public string CaseId { get; }

            public BindingStatus Status { get; }

            public string? BindingMethod { get; }

            public string Reason { get; }

            public string HostGaps { get; }
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

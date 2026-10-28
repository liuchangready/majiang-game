using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// N-2 host wiring tests. They drive the real actor mailbox and the frozen calculator; no
    /// test re-implements scoring or inserts a settlement produced by the test.
    /// </summary>
    public sealed class N2HostSettlementTests
    {
        [Fact]
        public async Task TsumoIsCalculatedFromTheCurrentRoundAndCommittedThroughTheActor()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);
            TableSettlementRequest request = harness.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                round.Version,
                "n2-tsumo");

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.TsumoWin);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.RoundEnded);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(snapshot.IsFinished);
            Assert.Equal(100000L, snapshot.Scores.Sum());
            Assert.NotEqual(25000L, snapshot.Scores[0]);
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public async Task RonUsesTheOpenResponseWindowAndRejectsAClaimOutsideIt()
        {
            RoundStateMachine round = SettlementFixture.RonOpening();
            await using var harness = N2Harness.Create(round);
            var discard = new TableCommandEnvelope(
                RoundCommand.Discard(round.RoundId, 0, SettlementFixture.RonDiscardTile(), new[] { 1 }),
                new RequestId("n2-ron-discard"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0));
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(discard, TestContext.Current.CancellationToken)).Status);

            TableSettlementRequest wrongSeat = harness.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 2),
                1,
                "n2-ron-wrong-seat");
            TableOperationResult rejected = await harness.Actor.SubmitSettlementAsync(
                wrongSeat, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.WinnerNotEligible), rejected.ReasonCode);

            TableSettlementRequest valid = harness.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 1),
                1,
                "n2-ron");
            TableOperationResult accepted = await harness.Actor.SubmitSettlementAsync(
                valid, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Contains(accepted.Events, record => record.Type == RoundEventType.RonWin);
            Assert.Contains(accepted.Events, record => record.Type == RoundEventType.RoundEnded);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public async Task ExhaustiveDrawIsCalculatedWithFourSeatsAndKeepsScoreConservation()
        {
            RoundStateMachine round = SettlementFixture.ExhaustiveDraw();
            await using var harness = N2Harness.Create(round);

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.ExhaustiveDraw, 0),
                    round.Version,
                    "n2-exhaustive"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.ExhaustiveDraw);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(snapshot.IsFinished);
            Assert.Equal(100000L, snapshot.Scores.Sum());
            Assert.Equal(new long[] { 26000L, 26000L, 26000L, 22000L }, snapshot.Scores.ToArray());
        }

        [Fact]
        public async Task AbortiveDrawIsCalculatedFromTheAuthoritativeProjectionAndKeepsScores()
        {
            RoundStateMachine round = SettlementFixture.FourWindsAbort();
            await using var harness = N2Harness.Create(round);

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(
                        round.RoundId,
                        TableSettlementAction.AbortiveDraw,
                        0,
                        SettlementAbortiveDrawReason.FourWinds),
                    round.Version,
                    "n2-abortive"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.AbortiveDraw);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 25000L, 25000L, 25000L, 25000L }, snapshot.Scores.ToArray());
        }

        [Fact]
        public async Task RawSettlementEnvelopeIsRefusedWhenTheN2AuthorityIsConfigured()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);
            var forged = new RoundSettlement(
                "forged",
                RoundOutcome.Tsumo(0),
                new long[] { 99999L, 1L, 0L, 0L },
                0,
                new PersistedUtcTime(DateTimeOffset.UnixEpoch),
                0L);
            var envelope = new TableCommandEnvelope(
                RoundCommand.Tsumo(round.RoundId, 0, forged),
                new RequestId("raw-forged"),
                new StateVersion((ulong)round.Version),
                new OwnerEpoch(1),
                new ControlEpoch(0));

            TableOperationResult result = await harness.Actor.SubmitAsync(
                envelope, TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableSettlementReasonCode.RawEntryRejected, result.ReasonCode);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.False(snapshot.IsFinished);
            Assert.Equal(new long[] { 25000L, 25000L, 25000L, 25000L }, snapshot.Scores.ToArray());
        }

        [Fact]
        public async Task StaleOrForeignSourceFactsAreRejectedWithoutStateMutation()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);

            await AssertRejectedWithoutMutation(
                harness,
                source => new TableSettlementSourceRef(source.RoundId, source.Version + 1L, source.Fingerprint),
                TableSettlementReasonCode.SourceVersionStale,
                "stale-version");

            await AssertRejectedWithoutMutation(
                harness,
                source => new TableSettlementSourceRef(source.RoundId, source.Version, source.Fingerprint ^ 1UL),
                TableSettlementReasonCode.SourceFingerprintMismatch,
                "foreign-fingerprint");
        }

        [Fact]
        public async Task MissingSeatHostFactsFailClosedAndAreNotTreatedAsFalse()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(
                round,
                new DelegateSettlementFactsPort((source, _) => new TableSettlementHostFacts(
                    source,
                    new[]
                    {
                        new TableSettlementSeatHostFacts(0),
                        new TableSettlementSeatHostFacts(1)
                    })));

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    round.Version,
                    "missing-seats"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableSettlementReasonCode.HostFactsInvalid, result.ReasonCode);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.False(snapshot.IsFinished);
        }

        [Fact]
        public async Task OldRoundVersionOwnerControlAndDecisionAreRejectedBeforeCalculation()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);

            TableOperationResult wrongRound = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent("another-round", TableSettlementAction.Tsumo, 0),
                    0,
                    "old-round"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, wrongRound.Status);
            Assert.Equal(TableRuntimeReasonCode.RoundMismatch, wrongRound.ReasonCode);

            TableOperationResult oldVersion = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    7,
                    "old-version"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, oldVersion.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, oldVersion.ReasonCode);

            TableOperationResult staleOwner = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "stale-owner",
                    ownerEpoch: 2),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, staleOwner.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, staleOwner.ReasonCode);

            TableOperationResult staleControl = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "stale-control",
                    controlEpoch: 3),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, staleControl.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, staleControl.ReasonCode);

            TableOperationResult missingDecision = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "missing-decision",
                    decisionId: new DecisionId("never-opened")),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, missingDecision.Status);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, missingDecision.ReasonCode);

            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.False(snapshot.IsFinished);
            Assert.Equal(new long[] { 25000L, 25000L, 25000L, 25000L }, snapshot.Scores.ToArray());
        }

        [Fact]
        public async Task ForgedHostFactsAreRejectedByTheFrozenCalculator()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(
                round,
                new DelegateSettlementFactsPort((source, _) => new TableSettlementHostFacts(
                    source,
                    Enumerable.Range(0, 4).Select(seat => new TableSettlementSeatHostFacts(
                        seat,
                        isIppatsu: seat == 0)))));

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "forged-ippatsu"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(
                TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.RiichiStateInvalid),
                result.ReasonCode);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.False(snapshot.IsFinished);
        }

        [Fact]
        public async Task EmptyOwnDiscardHistoryIsProvedEmptyAndRonStillWorks()
        {
            RoundStateMachine round = SettlementFixture.RonOpening();
            await using var harness = N2Harness.Create(round);
            var discard = new TableCommandEnvelope(
                RoundCommand.Discard(round.RoundId, 0, SettlementFixture.RonDiscardTile(), new[] { 1 }),
                new RequestId("empty-history-discard"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0));
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(discard, TestContext.Current.CancellationToken)).Status);

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 1),
                    1,
                    "empty-history-ron"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.RonWin);
        }

        [Fact]
        public async Task TerminalRoundRejectsASecondSettlementWithoutChangingScores()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);
            TableOperationResult first = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "terminal-first"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            TableSnapshot afterFirst = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);

            TableOperationResult second = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    1,
                    "terminal-second"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, second.Status);
            Assert.Equal(
                TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.RoundFinished),
                second.ReasonCode);
            TableSnapshot afterSecond = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(afterFirst.StateVersion, afterSecond.StateVersion);
            Assert.Equal(afterFirst.Scores.ToArray(), afterSecond.Scores.ToArray());
        }

        [Fact]
        public async Task CommitFailureDoesNotConsumeTheDecisionClockOrState()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);
            var key = new DecisionKey(round.RoundId, 0, "n2-decision");
            var pending = new PendingDecision(
                key,
                0L,
                new[] { new LegalActionOption(LegalActionKind.Tsumo, 0) },
                round.Tiles.DrawnTile?.Tile);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(pending, TestContext.Current.CancellationToken)).Status);

            TableTimingSnapshot timingBefore =
                await harness.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);
            TableSnapshot snapshotBefore =
                await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);

            harness.CommitPort.FailNext();
            TableOperationResult failed = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "commit-failure",
                    decisionId: new DecisionId("n2-decision")),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            TableTimingSnapshot timingAfter =
                await harness.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);
            TableSnapshot snapshotAfter =
                await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(snapshotBefore.StateVersion, snapshotAfter.StateVersion);
            Assert.Equal(snapshotBefore.Phase, snapshotAfter.Phase);
            Assert.Equal(snapshotBefore.Scores.ToArray(), snapshotAfter.Scores.ToArray());
            Assert.NotNull(timingAfter.Seats[0].PendingKey);
            Assert.Equal(timingBefore.Seats[0].Timing!.DeadlineMillis, timingAfter.Seats[0].Timing!.DeadlineMillis);

            // The same expected version and decision must still be usable: the failed commit did
            // not consume the clock, the pending decision, or the round version.
            TableOperationResult retried = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "commit-retry",
                    decisionId: new DecisionId("n2-decision")),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, retried.Status);
        }

        [Fact]
        public async Task DurableSettlementReplaysIdenticallyAndConflictsOnDifferentPayload()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(round);
            TableSettlementRequest request = harness.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                0,
                "durable-settlement-1",
                "SETTLEMENT_TSUMO",
                "payload-one");

            TableOperationResult first = await harness.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);
            Assert.True(
                first.Status == TableOperationStatus.Accepted,
                first.Status + " " + first.ReasonCode + " " + first.Detail);
            Assert.Equal(1, harness.Store.OutboxCount);

            TableOperationResult replay = await harness.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(first.Receipt!.EvaluatedStateVersion, replay.Receipt!.EvaluatedStateVersion);
            Assert.Equal(1, harness.Store.OutboxCount);

            TableSettlementRequest conflict = harness.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                0,
                "durable-settlement-1",
                "SETTLEMENT_TSUMO",
                "different-payload");
            TableOperationResult conflicted = await harness.Actor.SubmitSettlementAsync(
                conflict, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Conflict, conflicted.Status);
            Assert.Equal(1, harness.Store.OutboxCount);

            TableCheckpoint? checkpoint = await harness.Store.LoadCheckpointAsync(
                N2DurableHarness.Room, TestContext.Current.CancellationToken);
            Assert.NotNull(checkpoint);
            RoundStateMachine terminal = RoundRecovery.Restore(
                RoundRecoveryCodec.Decode(checkpoint!.StatePayload));
            Assert.True(terminal.IsFinished);
            Assert.Equal(136, terminal.Tiles.AsPlacements().Count);
            Assert.True(TilePositionMap.Validate(terminal.Tiles).IsValid);
            Assert.Equal(100000L, terminal.Result!.FinalScores.Sum());
        }

        [Fact]
        public async Task RecoveryDoesNotRepeatTheSettlementPaymentOrOutbox()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using N2DurableHarness first = await N2DurableHarness.CreateAsync(round);
            TableSettlementRequest request = first.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                0,
                "recovery-settlement",
                "SETTLEMENT_TSUMO",
                "recovery-payload");
            TableOperationResult accepted = await first.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);
            Assert.True(
                accepted.Status == TableOperationStatus.Accepted,
                accepted.Status + " " + accepted.ReasonCode + " " + accepted.Detail);
            Assert.Equal(1, first.Store.OutboxCount);

            await using N2DurableHarness recovered = first.Attach();
            TableRecoveryResult recovery = await recovered.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            Assert.True(recovery.Report!.Terminal);
            Assert.Equal(1, first.Store.OutboxCount);

            TableOperationResult replay = await recovered.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1, first.Store.OutboxCount);
            TableSnapshot snapshot = await recovered.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public async Task TrustedFuritenFactRejectsRonAndFailingPortFailsClosed()
        {
            RoundStateMachine round = SettlementFixture.RonOpening();
            await using var harness = N2Harness.Create(round);
            var discard = new TableCommandEnvelope(
                RoundCommand.Discard(round.RoundId, 0, SettlementFixture.RonDiscardTile(), new[] { 1 }),
                new RequestId("furiten-discard"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0));
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(discard, TestContext.Current.CancellationToken)).Status);

            harness.SetFactsPort(new DelegateSettlementFactsPort((source, _) => new TableSettlementHostFacts(
                source,
                Enumerable.Range(0, 4).Select(seat => new TableSettlementSeatHostFacts(
                    seat,
                    isTemporaryFuriten: seat == 1)))));
            TableOperationResult furiten = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 1),
                    1,
                    "furiten-ron"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, furiten.Status);
            Assert.Equal(
                TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.FuritenRon),
                furiten.ReasonCode);

            harness.SetFactsPort(new ThrowingSettlementFactsPort());
            TableOperationResult failed = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 1),
                    1,
                    "failing-port"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, failed.Status);
            Assert.Equal(TableSettlementReasonCode.FactsUnavailable, failed.ReasonCode);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.False(snapshot.IsFinished);
        }

        [Fact]
        public async Task TypedTsumoBindsWinnerSeatToAuthorityAndRejectsForeignSeat()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = N2Harness.Create(round);
            var key = new DecisionKey(round.RoundId, 1, "foreign-decision");
            var pending = new PendingDecision(
                key,
                0L,
                new[] { new LegalActionOption(LegalActionKind.Tsumo, 1) },
                round.Tiles.DrawnTile?.Tile);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(pending, TestContext.Current.CancellationToken)).Status);

            TableOperationResult spoof = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 1),
                    0,
                    "spoof-winner",
                    decisionId: new DecisionId("foreign-decision")),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, spoof.Status);
            Assert.Equal(
                TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.WinnerNotCurrent),
                spoof.ReasonCode);
            TableSnapshot afterSpoof = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.False(afterSpoof.IsFinished);
            Assert.Equal(new long[] { 25000L, 25000L, 25000L, 25000L }, afterSpoof.Scores.ToArray());

            TableOperationResult drawSpoof = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.ExhaustiveDraw, 1),
                    0,
                    "spoof-draw-seat"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, drawSpoof.Status);
            Assert.Equal(TableSettlementReasonCode.SeatMismatch, drawSpoof.ReasonCode);

            TableOperationResult legal = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    0,
                    "real-winner"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, legal.Status);
            TableSnapshot afterLegal = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100000L, afterLegal.Scores.Sum());
        }

        [Fact]
        public async Task PassAndOtherSeatDiscardsDoNotCancelDoubleRiichiAndScoresMatchCalculator()
        {
            RoundStateMachine round = SettlementFixture.DoubleRiichiRonWindow();
            var intent = new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 1);
            var ippatsuPort = new DelegateSettlementFactsPort((source, _) => new TableSettlementHostFacts(
                source,
                Enumerable.Range(0, 4).Select(seat => new TableSettlementSeatHostFacts(
                    seat,
                    isIppatsu: seat == 1))));
            RoundSettlementFacts facts = BuildFacts(round, intent, ippatsuPort, "double-riichi-facts");
            RoundSettlementSeatFacts seat1 = facts.FindSeat(1)!;
            Assert.True(seat1.IsDoubleRiichi);
            Assert.True(seat1.IsIppatsu);
            Assert.False(seat1.IsIppatsuInterruptedByCallOrKan);
            Assert.True(seat1.IsFirstTurnUninterrupted);

            RoundSettlementCalculation calculation = RoundSettlementCalculator.CalculateRon(round, 1, facts);
            Assert.Equal("DOUBLE_RIICHI", calculation.Breakdown!.Yaku[0].Code);
            Assert.Equal("IPPATSU", calculation.Breakdown!.Yaku[1].Code);
            Assert.True(calculation.Breakdown!.TotalYakuHan >= 3);

            await using var harness = N2Harness.Create(round, ippatsuPort);
            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(intent, 0, "double-riichi-ron"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(calculation.Settlement.FinalScores.ToArray(), snapshot.Scores.ToArray());
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public async Task PassPreservesFirstTurnAndChiihouYakuman()
        {
            RoundStateMachine round = SettlementFixture.ChiihouAfterPass();
            var intent = new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 1);
            RoundSettlementFacts facts = BuildFacts(round, intent, new DefaultSettlementFactsPort(), "chiihou-facts");
            Assert.True(facts.FindSeat(1)!.IsFirstTurnUninterrupted);
            Assert.False(facts.FindSeat(1)!.IsIppatsuInterruptedByCallOrKan);
            RoundSettlementCalculation calculation = RoundSettlementCalculator.CalculateTsumo(round, facts);
            Assert.Contains(calculation.Breakdown!.Yaku, y => y.Code == "CHIIHOU");

            await using var harness = N2Harness.Create(round);
            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(intent, 0, "chiihou-pass"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 9000L, 57000L, 17000L, 17000L }, snapshot.Scores.ToArray());
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public async Task TypedTsumoSettlesSleeveOfferAtomicallyAndRollsBackOnCommitFailure()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(1000), round.RoundId);
            var clock = new ManualTableClock { Now = 1000L };
            var commit = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commit);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(1),
                CheatEconomy = V32Fixtures.Economy(1000),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(true),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options,
                round,
                commit,
                clock,
                sink,
                null,
                null,
                null,
                null,
                new DefaultSettlementFactsPort());

            var offer = new TableCheatCommandEnvelope(
                new RoundCheatCommand(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloom,
                    "offer-1",
                    RoundCheatPayload.SleeveBloom()),
                new RequestId("offer-1"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                new RequestScopeId("offer-scope"),
                "CHEAT",
                TablePayloadSha256.Compute(new byte[] { 1 }));
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await actor.SubmitCheatAsync(offer, TestContext.Current.CancellationToken)).Status);
            Assert.NotNull(GetCheat(actor).SleeveBloom.ImmediateOffer);

            commit.FailNext();
            TableOperationResult failed = await actor.SubmitSettlementAsync(
                new TableSettlementRequest(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    new RequestId("offer-tsumo-failed"),
                    new StateVersion(1),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.NotNull(GetCheat(actor).SleeveBloom.ImmediateOffer);
            TableSnapshot afterFailure = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1UL, afterFailure.StateVersion.Value);
            Assert.Equal(new long[] { 25000L, 25000L, 25000L, 25000L }, afterFailure.Scores.ToArray());

            TableOperationResult settled = await actor.SubmitSettlementAsync(
                new TableSettlementRequest(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    new RequestId("offer-tsumo"),
                    new StateVersion(1),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, settled.Status);
            Assert.Contains(settled.Events, record => record.Type == RoundEventType.TsumoWin);
            Assert.Null(GetCheat(actor).SleeveBloom.ImmediateOffer);
            TableSnapshot afterSettlement = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100000L, afterSettlement.Scores.Sum());
        }

        [Fact]
        public async Task TypedTsumoSettlesBeamSwapOfferAtomically()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(1000), round.RoundId);
            var clock = new ManualTableClock { Now = 1000L };
            var commit = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commit);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(1),
                CheatEconomy = V32Fixtures.Economy(1000),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(true),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options,
                round,
                commit,
                clock,
                sink,
                null,
                null,
                null,
                null,
                new DefaultSettlementFactsPort());

            var offer = new TableCheatCommandEnvelope(
                new RoundCheatCommand(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitBeamSwap,
                    "beam-offer",
                    RoundCheatPayload.BeamSwap()),
                new RequestId("beam-offer"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                new RequestScopeId("beam-scope"),
                "CHEAT",
                TablePayloadSha256.Compute(new byte[] { 2 }));
            TableOperationResult activated = await actor.SubmitCheatAsync(
                offer, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            Assert.NotNull(GetCheat(actor).BeamSwap.Offer);

            TableOperationResult settled = await actor.SubmitSettlementAsync(
                new TableSettlementRequest(
                    new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                    new RequestId("beam-tsumo"),
                    new StateVersion(1),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, settled.Status);
            Assert.Contains(settled.Events, record => record.Type == RoundEventType.TsumoWin);
            Assert.Null(GetCheat(actor).BeamSwap.Offer);
            TableSnapshot snapshot = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public async Task DurableSleeveOfferSettlementReplaysAndRecoversWithoutRepeatingCheatOrPayment()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(1000), round.RoundId);
            await using N2DurableHarness harness = await N2DurableHarness.CreateAsync(
                round,
                configure: options =>
                {
                    options.CheatEconomy = V32Fixtures.Economy(1000);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig(true);
                    options.InitialCheatState = cheat;
                });

            var offer = new TableCheatCommandEnvelope(
                new RoundCheatCommand(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloom,
                    "durable-offer",
                    RoundCheatPayload.SleeveBloom()),
                new RequestId("durable-offer"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                harness.Scope.ScopeId,
                "CHEAT",
                TablePayloadSha256.Compute(new byte[] { 3 }));
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitCheatAsync(offer, TestContext.Current.CancellationToken)).Status);
            Assert.NotNull(GetCheat(harness.Actor).SleeveBloom.ImmediateOffer);

            TableSettlementRequest request = harness.Request(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                1,
                "durable-offer-tsumo",
                "SETTLEMENT_TSUMO",
                "durable-offer-payload");
            TableOperationResult first = await harness.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);
            Assert.True(
                first.Status == TableOperationStatus.Accepted,
                first.Status + " " + first.ReasonCode + " " + first.Detail);
            Assert.Equal(1, harness.Store.OutboxCount);
            Assert.Null(GetCheat(harness.Actor).SleeveBloom.ImmediateOffer);

            TableOperationResult replay = await harness.Actor.SubmitSettlementAsync(
                request, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1, harness.Store.OutboxCount);
            Assert.Null(GetCheat(harness.Actor).SleeveBloom.ImmediateOffer);

            await using N2DurableHarness recovered = harness.Attach();
            TableRecoveryResult recovery = await recovered.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            Assert.True(recovery.Report!.Terminal);
            Assert.Equal(1, harness.Store.OutboxCount);
            Assert.Null(GetCheat(recovered.Actor).SleeveBloom.ImmediateOffer);
            TableSnapshot snapshot = await recovered.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100000L, snapshot.Scores.Sum());
        }

        [Fact]
        public void RealChiStillInterruptsFirstTurnFactsUnlikePass()
        {
            RoundStateMachine round = SettlementFixture.ChiAfterDiscard();
            var intent = new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 1);
            RoundSettlementFacts facts = BuildFacts(
                round, intent, new DefaultSettlementFactsPort(), "chi-facts");
            Assert.False(facts.FindSeat(1)!.IsFirstTurnUninterrupted);
            Assert.False(facts.FindSeat(1)!.IsChiihou);
            Assert.Contains(
                round.Events,
                value => value.Type == RoundEventType.ResponseClaimed && value.Detail == "CHI");
        }

        [Fact]
        public void RealPonAfterRiichiInterruptsIppatsuAndRemovesTheYaku()
        {
            RoundStateMachine round = SettlementFixture.RiichiPonThenRonWindow();
            var intent = new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, 1);
            RoundSettlementFacts facts = BuildFacts(
                round, intent, new DefaultSettlementFactsPort(), "pon-ippatsu-facts");
            RoundSettlementSeatFacts seat1 = facts.FindSeat(1)!;
            Assert.True(seat1.IsDoubleRiichi);
            Assert.False(seat1.IsIppatsu);
            Assert.True(seat1.IsIppatsuInterruptedByCallOrKan);
            Assert.False(seat1.IsFirstTurnUninterrupted);
            Assert.Contains(
                round.Events,
                value => value.Type == RoundEventType.PonDeclared && value.Seat == 0);

            RoundSettlementCalculation calculation = RoundSettlementCalculator.CalculateRon(round, 1, facts);
            Assert.Equal("DOUBLE_RIICHI", calculation.Breakdown!.Yaku[0].Code);
            Assert.DoesNotContain(calculation.Breakdown.Yaku, y => y.Code == "IPPATSU");
            Assert.Equal(4, calculation.Breakdown.TotalYakuHan);
            Assert.Equal(40, calculation.Breakdown.Fu.FinalFu);
            Assert.Equal(9000L, calculation.Breakdown.TotalPoints);
            Assert.Equal(
                new[] { "DOUBLE_RIICHI", "TANYAO", "IIPEIKO" },
                calculation.Breakdown.Yaku.Select(y => y.Code).ToArray());
        }

        private static CheatState GetCheat(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (CheatState)field.GetValue(actor)!;
        }

        private static RoundSettlementFacts BuildFacts(
            RoundStateMachine round,
            TableSettlementIntent intent,
            ITableSettlementFactsPort port,
            string resultId)
        {
            var source = new TableSettlementSourceRef(round.RoundId, round.Version, round.SourceFingerprint);
            TableSettlementHostFacts host = port.Resolve(source, intent);
            return TableSettlementFactBuilder.Build(round, intent, host, resultId, 1000L, 1000L);
        }

        private static async Task AssertRejectedWithoutMutation(
            N2Harness harness,
            Func<TableSettlementSourceRef, TableSettlementSourceRef> rebind,
            string expectedReason,
            string requestId)
        {
            var port = new DelegateSettlementFactsPort((source, _) =>
            {
                TableSettlementSourceRef foreign = rebind(source);
                return new TableSettlementHostFacts(
                    foreign,
                    Enumerable.Range(0, 4).Select(seat => new TableSettlementSeatHostFacts(seat)));
            });
            harness.SetFactsPort(port);
            TableSnapshot before = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(
                    new TableSettlementIntent(harness.InitialRound.RoundId, TableSettlementAction.Tsumo, 0),
                    harness.InitialRound.Version,
                    requestId),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(expectedReason, result.ReasonCode);
            TableSnapshot after = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.Phase, after.Phase);
            Assert.Equal(before.Scores.ToArray(), after.Scores.ToArray());
        }
    }

    internal sealed class DelegateSettlementFactsPort : ITableSettlementFactsPort
    {
        private readonly Func<TableSettlementSourceRef, TableSettlementIntent, TableSettlementHostFacts> _factory;

        public DelegateSettlementFactsPort(
            Func<TableSettlementSourceRef, TableSettlementIntent, TableSettlementHostFacts> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent)
        {
            return _factory(source, intent);
        }
    }

    internal sealed class N2Harness : IAsyncDisposable
    {
        private N2Harness(
            TableActor actor,
            ManualTableClock clock,
            RecordingCommitPort commitPort,
            RoundStateMachine initialRound,
            MutableSettlementFactsPort port)
        {
            Actor = actor;
            Clock = clock;
            CommitPort = commitPort;
            InitialRound = initialRound;
            Port = port;
        }

        public TableActor Actor { get; }

        public ManualTableClock Clock { get; }

        public RecordingCommitPort CommitPort { get; }

        public RoundStateMachine InitialRound { get; }

        public MutableSettlementFactsPort Port { get; }

        public void SetFactsPort(ITableSettlementFactsPort port)
        {
            Port.Inner = port ?? throw new ArgumentNullException(nameof(port));
        }

        public static N2Harness Create(
            RoundStateMachine round,
            ITableSettlementFactsPort? port = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(1)
            };
            ITableSettlementFactsPort actualPort = port ?? new DefaultSettlementFactsPort();
            var mutablePort = new MutableSettlementFactsPort(actualPort);
            var actor = new TableActor(
                options,
                round,
                commitPort,
                clock,
                sink,
                null,
                null,
                null,
                null,
                mutablePort);
            return new N2Harness(actor, clock, commitPort, round, mutablePort);
        }

        public TableSettlementRequest Request(
            TableSettlementIntent intent,
            long expectedVersion,
            string requestId,
            DecisionId? decisionId = null,
            ulong controlEpoch = 0UL,
            ulong ownerEpoch = 1UL)
        {
            return new TableSettlementRequest(
                intent,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(ownerEpoch),
                new ControlEpoch(controlEpoch),
                decisionId);
        }

        public ValueTask DisposeAsync()
        {
            return Actor.DisposeAsync();
        }
    }

    internal sealed class MutableSettlementFactsPort : ITableSettlementFactsPort
    {
        public MutableSettlementFactsPort(ITableSettlementFactsPort inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public ITableSettlementFactsPort Inner { get; set; }

        public TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent)
        {
            return Inner.Resolve(source, intent);
        }
    }

    internal sealed class DefaultSettlementFactsPort : ITableSettlementFactsPort
    {
        public TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent)
        {
            return new TableSettlementHostFacts(
                source,
                Enumerable.Range(0, 4).Select(seat => new TableSettlementSeatHostFacts(seat)));
        }
    }

    internal sealed class ThrowingSettlementFactsPort : ITableSettlementFactsPort
    {
        public TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent)
        {
            throw new InvalidOperationException("injected fact-source failure");
        }
    }

    internal sealed class N2DurableHarness : IAsyncDisposable
    {
        public const string Room = "room-1";
        public const string Owner = "owner-n2";
        public const string Boot = "boot-n2";
        public const string Account = "acct-n2";

        private N2DurableHarness(
            TableActor actor,
            InMemoryTableStore store,
            InMemoryRequestScopeAuthority scopes,
            TableScopeRecord scope,
            ManualTableClock clock,
            RoundStateMachine initialRound,
            ITableSettlementFactsPort port,
            ITableProjectionContextSource projectionSource,
            Action<TableActorOptions>? configure)
        {
            Actor = actor;
            Store = store;
            Scopes = scopes;
            Scope = scope;
            Clock = clock;
            InitialRound = initialRound;
            Port = port;
            ProjectionSource = projectionSource;
            Configure = configure;
        }

        public TableActor Actor { get; }

        public InMemoryTableStore Store { get; }

        public InMemoryRequestScopeAuthority Scopes { get; }

        public TableScopeRecord Scope { get; }

        public ManualTableClock Clock { get; }

        public RoundStateMachine InitialRound { get; }

        public ITableSettlementFactsPort Port { get; }

        public ITableProjectionContextSource ProjectionSource { get; }

        public Action<TableActorOptions>? Configure { get; }

        public static async Task<N2DurableHarness> CreateAsync(
            RoundStateMachine round,
            ITableSettlementFactsPort? port = null,
            Action<TableActorOptions>? configure = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var scopes = new InMemoryRequestScopeAuthority();
            TableScopeRecord scope = scopes.Issue(
                Account,
                TableScopeRole.Player,
                Room,
                (ulong)DurableHarness.UtcBase,
                (ulong)DurableHarness.UtcBase + 3_600_000UL);
            var store = new InMemoryTableStore(clock, scopes);
            var ledger = DurableHarness.NewLedger();
            TableCheckpoint checkpoint = DurableHarness.InitialCheckpoint(round, ledger, boot: Boot);
            await store.CreateRoomAsync(checkpoint, CancellationToken.None);
            TableLeaseAcquireResult lease = await store.AcquireAsync(
                Room, Owner, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.True(lease.Acquired, lease.Detail);

            var projectionSource = DurableHarness.TerminalFactsSource();
            ITableSettlementFactsPort actualPort = port ?? new DefaultSettlementFactsPort();
            var commit = new RecordingCommitPort();
            var actor = new TableActor(
                Options(configure),
                round,
                commit,
                clock,
                new RecordingPublicationSink(commit),
                null,
                projectionSource,
                null,
                Durable(store, scopes, ledger),
                actualPort);
            return new N2DurableHarness(
                actor, store, scopes, scope, clock, round, actualPort, projectionSource, configure);
        }

        public N2DurableHarness Attach()
        {
            var ledger = DurableHarness.NewLedger();
            var commit = new RecordingCommitPort();
            var actor = new TableActor(
                Options(Configure),
                InitialRound,
                commit,
                Clock,
                new RecordingPublicationSink(commit),
                null,
                ProjectionSource,
                null,
                Durable(Store, Scopes, ledger),
                Port);
            return new N2DurableHarness(
                actor, Store, Scopes, Scope, Clock, InitialRound, Port, ProjectionSource, Configure);
        }

        public TableSettlementRequest Request(
            TableSettlementIntent intent,
            long expectedVersion,
            string requestId,
            string api,
            string payloadText)
        {
            byte[] payload = Encoding.UTF8.GetBytes(payloadText);
            return new TableSettlementRequest(
                intent,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                Scope.ScopeId,
                api,
                TablePayloadSha256.Compute(payload));
        }

        public ValueTask DisposeAsync()
        {
            return Actor.DisposeAsync();
        }

        private static TableActorOptions Options(Action<TableActorOptions>? configure)
        {
            var options = new TableActorOptions
            {
                RoomId = Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(1)
            };
            configure?.Invoke(options);
            return options;
        }

        private static TableDurableContext Durable(
            InMemoryTableStore store,
            InMemoryRequestScopeAuthority scopes,
            CheatLedger ledger)
        {
            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                scopes,
                Owner,
                TimeSpan.FromMinutes(10),
                Boot,
                1,
                Room);
            durable.CheatLedgerProvider = () => ledger;
            durable.CheatStateProvider = () => new CheatState(
                ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            durable.RecoveryStateSink = new RecordingRecoveryStateSink();
            return durable;
        }
    }

    internal static class SettlementFixture
    {
        public static RoundStateMachine Tsumo()
        {
            var plan = new SettlementPlan(RoundPhase.CheatWindow)
            {
                WallTiles = 122,
                FillRemainingIntoWall = true,
                DrawnSeat = 0,
                DrawnTile = Tile(22, 1),
                DrawnSource = DrawSource.Wall
            };
            plan.AddDora(39);
            plan.AddHand(
                0,
                Tile(1, 0), Tile(2, 0), Tile(3, 0),
                Tile(2, 1), Tile(3, 1), Tile(4, 1),
                Tile(5, 0), Tile(6, 0), Tile(7, 0),
                Tile(19, 0), Tile(20, 0), Tile(21, 0),
                Tile(22, 0), Tile(22, 1));
            return plan.Build(Spec("round-tsumo"));
        }

        public static RoundStateMachine RonOpening()
        {
            var plan = new SettlementPlan(RoundPhase.CheatWindow)
            {
                WallTiles = 122,
                FillRemainingIntoWall = true,
                DrawnSeat = 0,
                DrawnTile = Tile(22, 1),
                DrawnSource = DrawSource.Wall
            };
            plan.AddDora(39);
            plan.AddHand(
                0,
                Tile(0, 0), Tile(0, 1), Tile(0, 2),
                Tile(8, 0), Tile(8, 1), Tile(8, 2),
                Tile(9, 0), Tile(9, 1), Tile(9, 2),
                Tile(18, 0), Tile(18, 1), Tile(18, 2),
                Tile(22, 0), Tile(22, 1));
            plan.AddHand(
                1,
                Tile(1, 0), Tile(2, 0), Tile(3, 0),
                Tile(1, 1), Tile(2, 1), Tile(3, 1),
                Tile(5, 0), Tile(6, 0), Tile(7, 0),
                Tile(19, 0), Tile(20, 0), Tile(21, 0),
                Tile(22, 2));
            return plan.Build(Spec("round-ron"));
        }

        public static TileId RonDiscardTile()
        {
            return Tile(22, 1);
        }

        public static RoundStateMachine ExhaustiveDraw()
        {
            var plan = new SettlementPlan(RoundPhase.Draw)
            {
                WallTiles = 0
            };
            plan.AddDora(39);
            plan.AddHand(
                0,
                Tile(1, 0), Tile(2, 0), Tile(3, 0),
                Tile(2, 1), Tile(3, 1), Tile(4, 1),
                Tile(5, 0), Tile(6, 0), Tile(7, 0),
                Tile(19, 0), Tile(20, 0), Tile(21, 0),
                Tile(22, 0));
            plan.AddHand(
                1,
                Tile(10, 0), Tile(11, 0), Tile(12, 0),
                Tile(11, 1), Tile(12, 1), Tile(13, 1),
                Tile(14, 0), Tile(15, 0), Tile(16, 0),
                Tile(1, 1), Tile(2, 2), Tile(3, 2),
                Tile(4, 2));
            plan.AddHand(
                2,
                Tile(19, 1), Tile(20, 1), Tile(21, 1),
                Tile(20, 2), Tile(21, 2), Tile(22, 2),
                Tile(23, 0), Tile(24, 0), Tile(25, 0),
                Tile(10, 1), Tile(11, 2), Tile(12, 2),
                Tile(13, 2));
            plan.AddHand(
                3,
                Tile(0, 0), Tile(3, 3), Tile(6, 2),
                Tile(18, 0), Tile(21, 3), Tile(24, 3),
                Tile(9, 0), Tile(12, 3), Tile(15, 3),
                Tile(27, 0), Tile(28, 0), Tile(29, 0),
                Tile(30, 0));
            return plan.Build(Spec("round-exhaustive"));
        }

        public static RoundStateMachine FourWindsAbort()
        {
            var plan = new SettlementPlan(RoundPhase.Draw)
            {
                WallTiles = 122,
                FillRemainingIntoWall = true
            };
            plan.AddDora(39);
            plan.AddHand(
                0,
                Tile(1, 0), Tile(2, 0), Tile(3, 0),
                Tile(1, 1), Tile(2, 1), Tile(3, 1),
                Tile(5, 0), Tile(6, 0), Tile(7, 0),
                Tile(19, 0), Tile(20, 0), Tile(21, 0),
                Tile(22, 0));
            plan.AddDiscard(0, 27, 0);
            plan.AddDiscard(1, 27, 1);
            plan.AddDiscard(2, 27, 2);
            plan.AddDiscard(3, 27, 3);
            return plan.Build(Spec("round-abort"));
        }

        public static RoundStateMachine ChiihouAfterPass()
        {
            RoundStateMachine opening = RonOpening();
            var plan = new SettlementPlan(RoundPhase.CheatWindow)
            {
                FillRemainingIntoWall = true,
                DrawnSeat = 0,
                DrawnTile = Tile(22, 1),
                DrawnSource = DrawSource.Wall
            };
            plan.AddDora(39);
            plan.AddWallTop(Tile(22, 3).Value);
            plan.AddHand(0, opening.Tiles.TilesInZone(TileZone.Hand, 0).ToArray());
            plan.AddHand(1, opening.Tiles.TilesInZone(TileZone.Hand, 1).ToArray());
            RoundStateMachine chiOpening = plan.Build(opening.Spec);
            RoundStateMachine window = Step(
                chiOpening,
                RoundCommand.Discard(chiOpening.RoundId, 0, Tile(22, 1), new[] { 2 }));
            return Step(window, RoundCommand.Pass(window.RoundId, 2));
        }

        public static RoundStateMachine DoubleRiichiRonWindow()
        {
            RoundStateMachine opening = RonOpening();
            var plan = new SettlementPlan(RoundPhase.CheatWindow)
            {
                FillRemainingIntoWall = true,
                DrawnSeat = 0,
                DrawnTile = Tile(22, 1),
                DrawnSource = DrawSource.Wall
            };
            plan.AddDora(39);
            // First wall tile is seat 1's riichi discard; second is seat 2's 5p discard
            // that seat 1 can Ron. Both are real drawn/discarded physical tiles.
            plan.AddWallTop(Tile(0, 3).Value, Tile(22, 3).Value);
            plan.AddHand(0, opening.Tiles.TilesInZone(TileZone.Hand, 0).ToArray());
            plan.AddHand(1, opening.Tiles.TilesInZone(TileZone.Hand, 1).ToArray());
            RoundStateMachine first = plan.Build(opening.Spec);
            RoundStateMachine afterDealerDiscard = Step(
                first,
                RoundCommand.Discard(first.RoundId, 0, Tile(22, 1)));
            RoundStateMachine afterRiichi = Step(
                afterDealerDiscard,
                RoundCommand.Riichi(afterDealerDiscard.RoundId, 1, Tile(0, 3), new[] { 2 }));
            RoundStateMachine afterPass = Step(
                afterRiichi,
                RoundCommand.Pass(afterRiichi.RoundId, 2));
            return Step(
                afterPass,
                RoundCommand.Discard(afterPass.RoundId, 2, Tile(22, 3), new[] { 1 }));
        }

        /// <summary>
        /// Seat 1 declares riichi, passes on the first Ron chance, then seat 3 discards a tile
        /// seat 0 really Pons; seat 0's next discard opens a fresh Ron window for seat 1. This
        /// proves the host fact builder sees a real call as an ippatsu interruption.
        /// </summary>
        public static RoundStateMachine RiichiPonThenRonWindow()
        {
            RoundStateMachine opening = RonOpening();
            var plan = new SettlementPlan(RoundPhase.CheatWindow)
            {
                FillRemainingIntoWall = true,
                DrawnSeat = 0,
                DrawnTile = Tile(22, 1),
                DrawnSource = DrawSource.Wall
            };
            plan.AddDora(39);
            plan.AddWallTop(Tile(0, 3).Value, Tile(22, 3).Value, Tile(8, 3).Value);
            plan.AddHand(0, opening.Tiles.TilesInZone(TileZone.Hand, 0).ToArray());
            plan.AddHand(1, opening.Tiles.TilesInZone(TileZone.Hand, 1).ToArray());
            RoundStateMachine first = plan.Build(opening.Spec);
            RoundStateMachine afterDealerDiscard = Step(
                first,
                RoundCommand.Discard(first.RoundId, 0, Tile(22, 1)));
            RoundStateMachine afterRiichi = Step(
                afterDealerDiscard,
                RoundCommand.Riichi(afterDealerDiscard.RoundId, 1, Tile(0, 3), new[] { 2 }));
            RoundStateMachine afterPass = Step(
                afterRiichi,
                RoundCommand.Pass(afterRiichi.RoundId, 2));
            RoundStateMachine ronWindow = Step(
                afterPass,
                RoundCommand.Discard(afterPass.RoundId, 2, Tile(22, 3), new[] { 1 }));
            RoundStateMachine afterFirstPass = Step(
                ronWindow,
                RoundCommand.Pass(ronWindow.RoundId, 1));
            RoundStateMachine ponWindow = Step(
                afterFirstPass,
                RoundCommand.Discard(afterFirstPass.RoundId, 3, Tile(8, 3), new[] { 0 }));
            RoundStateMachine afterPon = Step(
                ponWindow,
                RoundCommand.Pon(ponWindow.RoundId, 0, Tile(8, 3), Tile(8, 0), Tile(8, 1)));
            return Step(
                afterPon,
                RoundCommand.Discard(afterPon.RoundId, 0, Tile(22, 0), new[] { 1 }));
        }

        public static RoundStateMachine ChiAfterDiscard()
        {
            RoundStateMachine opening = RonOpening();
            RoundStateMachine window = Step(
                opening,
                RoundCommand.Discard(opening.RoundId, 0, Tile(0, 0), new[] { 1 }));
            return Step(
                window,
                RoundCommand.Chi(window.RoundId, 1, Tile(0, 0), Tile(1, 0), Tile(2, 0)));
        }

        private static RoundStateMachine Step(RoundStateMachine round, RoundCommand command)
        {
            RoundTransition transition = round.Execute(command);
            if (!transition.IsAccepted || transition.State == null)
            {
                throw new InvalidOperationException(
                    "Fixture command rejected: " + (transition.Error == null ? "unknown" : transition.Error.ToString()));
            }

            return transition.State;
        }

        private static RoundSpec Spec(string roundId)
        {
            return new RoundSpec(
                roundId,
                0,
                1,
                0,
                0,
                new long[] { 25000L, 25000L, 25000L, 25000L },
                0);
        }

        private static TileId Tile(int typeIndex, int copyIndex)
        {
            return TileId.FromTypeIndex(typeIndex, copyIndex);
        }
    }

    /// <summary>Small deterministic placement builder for the host tests; mirrors production physics.</summary>
    internal sealed class SettlementPlan
    {
        private readonly List<TilePlacement> _placements = new List<TilePlacement>();
        private readonly HashSet<int> _usedTiles = new HashSet<int>();
        private readonly HashSet<int> _usedPositions = new HashSet<int>();
        private readonly int[] _nextDiscard = new int[4];
        private readonly List<TileId>[] _hands =
        {
            new List<TileId>(), new List<TileId>(), new List<TileId>(), new List<TileId>()
        };
        private readonly List<TileId> _dora = new List<TileId>();
        private readonly List<TileId> _wallTop = new List<TileId>();
        private readonly List<(int Seat, TileId Tile)> _discards = new List<(int Seat, TileId Tile)>();

        public SettlementPlan(RoundPhase phase)
        {
            Phase = phase;
        }

        public RoundPhase Phase { get; }

        public int WallTiles { get; set; }

        public bool FillRemainingIntoWall { get; set; }

        public TileId? DrawnTile { get; set; }

        public int DrawnSeat { get; set; }

        public DrawSource DrawnSource { get; set; } = DrawSource.Wall;

        public void AddHand(int seat, params TileId[] tiles)
        {
            _hands[seat].AddRange(tiles);
        }

        public void AddDora(params int[] tileValues)
        {
            foreach (int value in tileValues)
            {
                _dora.Add(new TileId(value));
            }
        }

        public void AddWallTop(params int[] tileValues)
        {
            foreach (int value in tileValues)
            {
                _wallTop.Add(new TileId(value));
            }
        }

        public void AddDiscard(int seat, int typeIndex, int copyIndex)
        {
            _discards.Add((seat, TileId.FromTypeIndex(typeIndex, copyIndex)));
        }

        public RoundStateMachine Build(RoundSpec spec)
        {
            return RoundStateMachine.CreateDealt(spec, BuildMap(), Phase);
        }

        private TilePositionMap BuildMap()
        {
            if (WallTiles < 0 || WallTiles > 122)
            {
                throw new InvalidOperationException("WallTiles must be 0..122.");
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int i = 0; i < _hands[seat].Count; i++)
                {
                    Place(_hands[seat][i], TilePosition.Hand(seat, i));
                }
            }

            foreach ((int seat, TileId tile) in _discards)
            {
                Place(tile, TilePosition.Discard(seat, _nextDiscard[seat]++));
            }

            for (int i = 0; i < _dora.Count; i++)
            {
                Place(_dora[i], TilePosition.DeadWall(4 + (2 * i)));
            }

            for (int i = 0; i < _wallTop.Count; i++)
            {
                Place(_wallTop[i], TilePosition.Wall(i));
            }

            for (int slot = 0; slot < 14; slot++)
            {
                TilePosition position = TilePosition.DeadWall(slot);
                if (!_usedPositions.Contains(PositionKey(position)))
                {
                    Place(NextUnused(), position);
                }
            }

            if (FillRemainingIntoWall)
            {
                int remaining = TileId.Count - _usedTiles.Count;
                WallTiles = Math.Min(122, _wallTop.Count + remaining);
            }

            for (int i = _wallTop.Count; i < WallTiles; i++)
            {
                Place(NextUnused(), TilePosition.Wall(i));
            }

            while (true)
            {
                int value = NextUnusedValue();
                if (value < 0)
                {
                    break;
                }

                int seat = value % 4;
                Place(new TileId(value), TilePosition.Discard(seat, _nextDiscard[seat]++));
            }

            DrawnTileEvidence? drawn = DrawnTile.HasValue
                ? new DrawnTileEvidence(DrawnTile.Value, DrawnSeat, DrawnSource)
                : (DrawnTileEvidence?)null;
            return TilePositionMap.FromPlacements(
                _placements,
                0,
                WallTiles,
                0,
                null,
                0,
                null,
                _dora.ToArray(),
                0,
                drawn);
        }

        private void Place(TileId tile, TilePosition position)
        {
            if (!_usedTiles.Add(tile.Value))
            {
                throw new InvalidOperationException("Physical tile " + tile + " placed twice.");
            }

            int key = PositionKey(position);
            if (!_usedPositions.Add(key))
            {
                throw new InvalidOperationException("Position " + position + " placed twice.");
            }

            _placements.Add(new TilePlacement(tile, position));
        }

        private TileId NextUnused()
        {
            int value = NextUnusedValue();
            if (value < 0)
            {
                throw new InvalidOperationException("No unused physical tile remains.");
            }

            return new TileId(value);
        }

        private int NextUnusedValue()
        {
            for (int value = 0; value < TileId.Count; value++)
            {
                if (!_usedTiles.Contains(value))
                {
                    return value;
                }
            }

            return -1;
        }

        private static int PositionKey(TilePosition position)
        {
            return (((int)position.Zone * 4 + (position.Seat + 1)) * 256) + position.Index;
        }
    }
}

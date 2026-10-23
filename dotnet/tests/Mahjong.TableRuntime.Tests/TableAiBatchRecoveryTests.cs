using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableAiBatchRecoveryTests
    {
        [Fact]
        public async Task OldRawBatchSubmissionAfterRecoveryIsRejectedWithoutCommit()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            AiDecisionSubmission oldSubmission = decision.Ai.ToDomainSubmission(1000L);
            await RecoverCurrentDecisionAsync(harness, decision);

            harness.Clock.Now = 1500L;
            TableTickResult result = await harness.TickAsync(
                new TableTickRequest(new[] { oldSubmission }));

            Assert.DoesNotContain(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiAccepted &&
                         entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Contains(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiRejected &&
                         entry.RejectionCode == TickRejectionCode.StaleRecoveryGeneration);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task OldAiDecisionBatchAfterRecoveryIsRejectedWithoutCommit()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            AiDecision oldDecision = decision.Ai;
            await RecoverCurrentDecisionAsync(harness, decision);

            harness.Clock.Now = 1500L;
            TableTickResult result = await harness.TickAsync(
                TableTickRequest.WithAiDecisions(new[] { oldDecision }));

            Assert.DoesNotContain(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiAccepted);
            Assert.Contains(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiRejected &&
                         entry.RejectionCode == TickRejectionCode.StaleRecoveryGeneration);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task FreshObservationBatchAfterRecoveryIsAccepted()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            await RecoverCurrentDecisionAsync(harness, decision);
            AiDecision fresh = await CaptureFreshDecisionAsync(harness, decision.Chosen);

            harness.Clock.Now = 1500L;
            TableTickResult result = await harness.TickAsync(
                TableTickRequest.WithAiDecisions(new[] { fresh }));

            Assert.Equal(TickEntryKind.AiAccepted, result.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, result.Entries[0].Transition!.Status);
            Assert.Equal(1UL, result.FinalStateVersion.Value);
        }

        [Fact]
        public async Task MixedBatchRejectsOldRawAndAcceptsFreshObservation()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            AiDecisionSubmission oldRaw = decision.Ai.ToDomainSubmission(1000L);
            await RecoverCurrentDecisionAsync(harness, decision);
            AiDecision fresh = await CaptureFreshDecisionAsync(harness, decision.Chosen);

            harness.Clock.Now = 1500L;
            TableTickResult result = await harness.TickAsync(new TableTickRequest(
                new[] { oldRaw },
                new[] { fresh }));

            Assert.Equal(1, result.Entries.Count(entry => entry.Kind == TickEntryKind.AiAccepted));
            Assert.Contains(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiRejected &&
                         entry.RejectionCode == TickRejectionCode.StaleRecoveryGeneration);
            Assert.Equal(1UL, result.FinalStateVersion.Value);
        }

        [Fact]
        public async Task FreshBatchCommitFailureDoesNotConsumeAndCanRetry()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            await RecoverCurrentDecisionAsync(harness, decision);
            AiDecision fresh = await CaptureFreshDecisionAsync(harness, decision.Chosen);

            harness.Clock.Now = 1500L;
            harness.CommitPort.FailNext();
            TableTickResult failed = await harness.TickAsync(
                TableTickRequest.WithAiDecisions(new[] { fresh }));
            Assert.Equal(TickEntryKind.AiAccepted, failed.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Failed, failed.Entries[0].Transition!.Status);
            Assert.Equal(0UL, failed.FinalStateVersion.Value);

            TableTickResult retried = await harness.TickAsync(
                TableTickRequest.WithAiDecisions(new[] { fresh }));
            Assert.Equal(TableOperationStatus.Accepted, retried.Entries[0].Transition!.Status);
            Assert.Equal(1UL, retried.FinalStateVersion.Value);
        }

        [Fact]
        public async Task TimeoutStillWinsWhenRawBatchArrivesAtTheRecoveredDeadline()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            AiDecisionSubmission oldRaw = decision.Ai.ToDomainSubmission(1000L);
            await RecoverCurrentDecisionAsync(harness, decision);

            harness.Clock.Now = 2000L;
            TableTickResult result = await harness.TickAsync(
                new TableTickRequest(new[] { oldRaw }));

            Assert.Contains(result.Entries, entry => entry.Kind == TickEntryKind.Timeout);
            TableTickEntry timeout = result.Entries.First(entry => entry.Kind == TickEntryKind.Timeout);
            Assert.Equal(TableOperationStatus.Accepted, timeout.Transition!.Status);
            Assert.DoesNotContain(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiAccepted &&
                         entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Equal(1UL, result.FinalStateVersion.Value);
        }

        [Fact]
        public async Task ConcurrentObservationsForOneDecisionOnlyAllowOneAcceptedResult()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            await RecoverCurrentDecisionAsync(harness, decision);
            AiDecision first = await CaptureFreshDecisionAsync(harness, decision.Chosen);
            AiDecision second = await CaptureFreshDecisionAsync(harness, decision.Chosen);

            harness.Clock.Now = 1500L;
            TableTickResult result = await harness.TickAsync(
                TableTickRequest.WithAiDecisions(new[] { first, second }));

            Assert.Equal(1, result.Entries.Count(
                entry => entry.Kind == TickEntryKind.AiAccepted &&
                         entry.Transition?.Status == TableOperationStatus.Accepted));
            Assert.Contains(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiRejected &&
                         entry.RejectionCode == TickRejectionCode.StaleDecision);
            Assert.Equal(1UL, result.FinalStateVersion.Value);
        }

        [Fact]
        public async Task ObservationFenceDoesNotRetainHistoricalSnapshots()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            await RecoverCurrentDecisionAsync(harness, decision);

            bool alive = await CaptureAndCollectAsync(harness);
            Assert.False(alive, "the recovery observation fence must not strongly retain full observations");
        }

        private static async Task RecoverCurrentDecisionAsync(
            ActorHarness harness,
            DiscardDecisionFixture decision)
        {
            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(Enumerable.Range(0, 4)
                .Select(seat => P31Fixtures.Connected(SeatControllerKind.Computer, 0L, seat))
                .ToArray());
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("batch recovery fence")).Status);
            var recovered = new TableRecoveredDecision(
                decision.Pending.Key,
                0L,
                decision.Pending.Candidates,
                decision.Pending.DrawnTile,
                1000L,
                0L,
                0L);
            TableOperationResult completed = await harness.CompleteRecoveryAsync(
                new TableRecoveryBaseline(
                    harness.InitialRound,
                    snapshot.StateVersion,
                    snapshot.LastEventSeq,
                    snapshot.OwnerEpoch,
                    new TableControlRecoveryBaseline(control, 0L),
                    new TableRecoveryTiming(new[] { recovered })));
            Assert.True(completed.Status == TableOperationStatus.Configured, completed.ReasonCode + ": " + completed.Detail);
        }

        private static async Task<AiDecision> CaptureFreshDecisionAsync(
            ActorHarness harness,
            LegalActionOption action)
        {
            TableAiObservationResult captured = await harness.CaptureAiObservationAsync(0);
            Assert.True(captured.IsCaptured);
            var intent = new CommandIntent(captured.Observation!, action, "test-ai", 1);
            return new AiDecision(captured.Observation!, intent, 0.5d);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<bool> CaptureAndCollectAsync(ActorHarness harness)
        {
            TableAiObservationResult? captured = await harness.CaptureAiObservationAsync(0);
            Assert.True(captured.IsCaptured);
            await harness.GetSnapshotAsync();
            var weak = new WeakReference(captured.Observation!);
            captured = null;
            await Task.Delay(50, TestContext.Current.CancellationToken);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return weak.IsAlive;
        }
    }
}

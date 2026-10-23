using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableActorTests
    {
        [Fact]
        public async Task SequentialCommandsAdvanceExactlyOnceAndShareCommandVersionAcrossEvents()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 5);

            var results = new List<TableOperationResult>();
            for (int i = 0; i < chain.Count; i++)
            {
                results.Add(await harness.SubmitAsync(chain[i]));
            }

            for (int i = 0; i < results.Count; i++)
            {
                TableOperationResult result = results[i];
                Assert.Equal(TableOperationStatus.Accepted, result.Status);
                Assert.True(result.Admitted);
                Assert.NotNull(result.Receipt);
                Assert.Equal((ulong)(i + 1), result.CommandVersion!.Value.Value);
                Assert.True(result.Events.Count >= 2, "a discard must emit at least discard + next draw");
                Assert.Equal(result.Events.Count, result.Events.Select(e => e.Ordinal).Distinct().Count());

                for (int ordinal = 0; ordinal < result.Events.Count; ordinal++)
                {
                    TableEventRecord record = result.Events[ordinal];
                    Assert.Equal(ordinal, record.Ordinal);
                    Assert.Equal(result.CommandVersion.Value, record.CommandVersion);
                }

                if (i > 0)
                {
                    Assert.Equal(
                        results[i - 1].LastEventSeq!.Value.Value + 1,
                        result.Events[0].Seq.Value);
                }
            }

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(5UL, snapshot.StateVersion.Value);
            Assert.Equal(TableActorLifecycle.Serving, snapshot.Lifecycle);
            Assert.Equal(5, harness.CommitPort.CommittedRequests.Count);
            Assert.Equal(5, harness.Sink.Publications.Count);
            Assert.False(harness.Sink.PublishedBeforeCommit);
            Assert.All(
                harness.Sink.Publications,
                publication => Assert.Equal(TablePublicationKind.Applied, publication.Kind));
        }

        [Fact]
        public async Task DomainRejectionAdvancesNoVersionAndPublishesNothing()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundStateMachine round = harness.InitialRound;
            int wrongSeat = (round.CurrentSeat + 1) % 4;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, wrongSeat)[0];
            TableCommandEnvelope command = P31Fixtures.Command(
                RoundCommand.Discard(round.RoundId, wrongSeat, tile),
                0,
                "reject-1");

            TableOperationResult result = await harness.SubmitAsync(command);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.True(result.Admitted);
            Assert.Equal("NOT_YOUR_TURN", result.ReasonCode);
            Assert.Equal(0UL, result.EvaluatedStateVersion.Value);
            Assert.Null(result.CommandVersion);
            Assert.Null(result.Receipt);
            Assert.Empty(result.Events);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Empty(harness.Sink.Publications);

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(0UL, snapshot.StateVersion.Value);
            Assert.Equal(0UL, snapshot.LastEventSeq.Value);
        }

        [Fact]
        public async Task ConcurrentSubmissionsProduceExactlyOneCommittedVersion()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundCommand command = P31Fixtures.DiscardCurrent(harness.InitialRound);
            TableCommandEnvelope[] envelopes = Enumerable.Range(0, 8)
                .Select(index => P31Fixtures.Command(command, 0, "cas-" + index))
                .ToArray();

            using var start = new ManualResetEventSlim(false);
            Task<TableOperationResult>[] tasks = envelopes
                .Select(envelope => Task.Run(async () =>
                {
                    start.Wait();
                    return await harness.SubmitAsync(envelope);
                }))
                .ToArray();
            start.Set();

            TableOperationResult[] results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(result => result.Status == TableOperationStatus.Accepted));
            Assert.Equal(
                7,
                results.Count(result =>
                    result.Status == TableOperationStatus.Rejected &&
                    result.ReasonCode == TableRuntimeReasonCode.StaleStateVersion));
            Assert.Single(harness.CommitPort.Requests);
            Assert.Equal(1UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
            Assert.Single(harness.Sink.Publications);
        }

        [Fact]
        public async Task FullBusinessMailboxRejectsWithoutAdmissionAndWithoutDroppingAdmittedWork()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(mailboxCapacity: 1);
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 3);

            harness.CommitPort.BlockNext();
            Task<TableOperationResult> first = harness.SubmitAsync(chain[0]).AsTask();
            await harness.CommitPort.Entered;

            Task<TableOperationResult> second = harness.SubmitAsync(chain[1]).AsTask();
            TableOperationResult third = await harness.SubmitAsync(chain[2]);

            Assert.Equal(TableOperationStatus.MailboxFull, third.Status);
            Assert.False(third.Admitted);
            Assert.Equal(TableRuntimeReasonCode.MailboxFull, third.ReasonCode);

            harness.CommitPort.Release();
            TableOperationResult firstResult = await first;
            TableOperationResult secondResult = await second;

            Assert.Equal(TableOperationStatus.Accepted, firstResult.Status);
            Assert.Equal(TableOperationStatus.Accepted, secondResult.Status);
            Assert.Equal(1UL, firstResult.CommandVersion!.Value.Value);
            Assert.Equal(2UL, secondResult.CommandVersion!.Value.Value);
            Assert.Equal(2, harness.CommitPort.Requests.Count);
            Assert.Equal(2UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task CloseRefusesNewWorkButDrainsEveryPreviouslyAdmittedCommand()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(mailboxCapacity: 1);
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 3);

            harness.CommitPort.BlockNext();
            Task<TableOperationResult> first = harness.SubmitAsync(chain[0]).AsTask();
            await harness.CommitPort.Entered;
            Task<TableOperationResult> second = harness.SubmitAsync(chain[1]).AsTask();

            Task close = harness.CloseAsync().AsTask();
            TableOperationResult refused = await harness.SubmitAsync(chain[2]);
            Assert.Equal(TableOperationStatus.Closed, refused.Status);
            Assert.False(refused.Admitted);
            Assert.Equal(TableRuntimeReasonCode.ActorClosed, refused.ReasonCode);

            harness.CommitPort.Release();
            TableOperationResult firstResult = await first;
            TableOperationResult secondResult = await second;
            await close;

            Assert.Equal(TableOperationStatus.Accepted, firstResult.Status);
            Assert.Equal(TableOperationStatus.Accepted, secondResult.Status);
            Assert.Equal(2UL, secondResult.CommandVersion!.Value.Value);
            Assert.Equal(2, harness.CommitPort.Requests.Count);
            Assert.Equal(2, harness.Sink.Publications.Count);
        }

        [Fact]
        public async Task DeadlineBoundaryTimeoutWinsOverSameTickAi()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);

            TableOperationResult opened = await harness.OpenDecisionAsync(decision.Pending);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            harness.Clock.Now = 2000L;
            var request = new TableTickRequest(
                new[] { decision.Ai.ToDomainSubmission(2000L) });
            TableTickResult tick = await harness.TickAsync(request);

            Assert.Equal(TableOperationStatus.Accepted, tick.Status);
            Assert.Equal(2, tick.Entries.Count);
            Assert.Equal(TickEntryKind.Timeout, tick.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.None, tick.Entries[0].RejectionCode);
            Assert.NotNull(tick.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            Assert.Equal(TickEntryKind.AiRejected, tick.Entries[1].Kind);
            Assert.Equal(TickRejectionCode.DeadlineExpired, tick.Entries[1].RejectionCode);
            Assert.Null(tick.Entries[1].Transition);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);

            TableEventRecord discard = tick.Entries[0].Transition!.Events
                .Single(record => record.Type == RoundEventType.TileDiscarded);
            Assert.Equal(decision.Fallback.Tile!.Value, discard.Tile);
            Assert.Single(harness.CommitPort.CommittedRequests);
        }

        [Fact]
        public async Task AiBeforeDeadlineIsAcceptedAndItsTileIsApplied()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 1500L;
            TableTickResult tick = await harness.CompleteAiAsync(decision.Ai);

            Assert.Equal(TableOperationStatus.Accepted, tick.Status);
            Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, tick.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.None, tick.Entries[0].RejectionCode);
            Assert.NotNull(tick.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);

            TableEventRecord discard = tick.Entries[0].Transition!.Events
                .Single(record => record.Type == RoundEventType.TileDiscarded);
            Assert.Equal(decision.Chosen.Tile!.Value, discard.Tile);
            Assert.Single(harness.CommitPort.CommittedRequests);
        }

        [Fact]
        public async Task LateAiCompletionAtDeadlineLosesToTimeout()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 2000L;
            TableTickResult result = await harness.CompleteAiAsync(decision.Ai);

            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(TickEntryKind.Timeout, result.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, result.Entries[0].Transition!.Status);
            Assert.Equal(TickEntryKind.AiRejected, result.Entries[1].Kind);
            Assert.Equal(TickRejectionCode.DeadlineExpired, result.Entries[1].RejectionCode);
            Assert.Equal(1UL, result.FinalStateVersion.Value);

            TableEventRecord discard = result.Entries[0].Transition!.Events
                .Single(record => record.Type == RoundEventType.TileDiscarded);
            Assert.Equal(decision.Fallback.Tile!.Value, discard.Tile);
        }

        [Fact]
        public async Task StaleControlEpochAiIsRejectedWithoutVersionChange()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture valid = P31Fixtures.BuildDiscardDecision(harness);
            DiscardDecisionFixture stale = P31Fixtures.BuildDiscardDecision(harness, controlEpoch: 1UL);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(valid.Pending)).Status);

            harness.Clock.Now = 1500L;
            TableTickResult result = await harness.CompleteAiAsync(stale.Ai);

            Assert.Single(result.Entries);
            Assert.Equal(TickEntryKind.AiRejected, result.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.StaleEpoch, result.Entries[0].RejectionCode);
            Assert.Equal(0UL, result.FinalStateVersion.Value);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Empty(harness.Sink.Publications);
        }

        [Fact]
        public async Task StaleRoundAndDecisionAiAreRejectedAndDoNotConsumeTheDecision()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture valid = P31Fixtures.BuildDiscardDecision(harness);
            DiscardDecisionFixture staleRound = P31Fixtures.BuildDiscardDecision(harness, roundId: "other-round");
            DiscardDecisionFixture staleDecision = P31Fixtures.BuildDiscardDecision(harness, decisionId: "old-decision");
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(valid.Pending)).Status);

            harness.Clock.Now = 1500L;
            TableTickResult roundResult = await harness.CompleteAiAsync(staleRound.Ai);
            Assert.Equal(TickRejectionCode.StaleRound, roundResult.Entries[0].RejectionCode);
            Assert.Equal(0UL, roundResult.FinalStateVersion.Value);

            TableTickResult decisionResult = await harness.CompleteAiAsync(staleDecision.Ai);
            Assert.Equal(TickRejectionCode.StaleDecision, decisionResult.Entries[0].RejectionCode);
            Assert.Equal(0UL, decisionResult.FinalStateVersion.Value);

            TableTickResult accepted = await harness.CompleteAiAsync(valid.Ai);
            Assert.Equal(TickEntryKind.AiAccepted, accepted.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Entries[0].Transition!.Status);
            Assert.Equal(1UL, accepted.FinalStateVersion.Value);
        }

        [Fact]
        public async Task LeaseLossIsOrderedAfterInFlightCommitAndRejectsQueuedWork()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 3);

            harness.CommitPort.BlockNext();
            Task<TableOperationResult> first = harness.SubmitAsync(chain[0]).AsTask();
            await harness.CommitPort.Entered;

            Task<TableOperationResult> leaseLost = harness
                .ReportLeaseLostAsync(new OwnerEpoch(1), "test lease loss")
                .AsTask();
            Task<TableOperationResult> second = harness.SubmitAsync(chain[1]).AsTask();

            harness.CommitPort.Release();
            TableOperationResult firstResult = await first;
            TableOperationResult leaseResult = await leaseLost;
            TableOperationResult secondResult = await second;

            Assert.Equal(TableOperationStatus.Accepted, firstResult.Status);
            Assert.Equal(1UL, firstResult.CommandVersion!.Value.Value);
            Assert.Equal(TableOperationStatus.Configured, leaseResult.Status);
            Assert.Equal(TableOperationStatus.Rejected, secondResult.Status);
            Assert.Equal(TableRuntimeReasonCode.LeaseLost, secondResult.ReasonCode);
            Assert.Equal(1UL, secondResult.EvaluatedStateVersion.Value);
            Assert.Single(harness.CommitPort.Requests);

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(TableActorLifecycle.LeaseLost, snapshot.Lifecycle);
            Assert.Equal(1UL, snapshot.StateVersion.Value);
        }

        [Fact]
        public async Task CommitFailureInstallsNothingAndRetryCanStillCommit()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2);

            harness.CommitPort.FailNext();
            TableOperationResult failed = await harness.SubmitAsync(chain[0]);

            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Equal(TableRuntimeReasonCode.CommitFailed, failed.ReasonCode);
            Assert.Equal(0UL, failed.EvaluatedStateVersion.Value);
            Assert.Null(failed.CommandVersion);
            Assert.Empty(failed.Events);
            Assert.Empty(harness.Sink.Publications);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);

            TableOperationResult retried = await harness.SubmitAsync(chain[0]);
            Assert.Equal(TableOperationStatus.Accepted, retried.Status);
            Assert.Equal(1UL, retried.CommandVersion!.Value.Value);
            Assert.Single(harness.Sink.Publications);
            Assert.Equal(2, harness.CommitPort.Requests.Count);
        }

        [Fact]
        public async Task CommitCancellationInstallsNothing()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            TableCommandEnvelope command = P31Fixtures.BuildDiscardChain(harness.InitialRound, 1)[0];

            harness.CommitPort.CancelNext();
            TableOperationResult result = await harness.SubmitAsync(command);

            Assert.Equal(TableOperationStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.CommitCancelled, result.ReasonCode);
            Assert.Equal(0UL, result.EvaluatedStateVersion.Value);
            Assert.Empty(harness.Sink.Publications);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task SlowCommitSuccessSerializesFollowingMessagesAndInstallsInOrder()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2);

            harness.CommitPort.BlockNext();
            Task<TableOperationResult> first = harness.SubmitAsync(chain[0]).AsTask();
            await harness.CommitPort.Entered;
            Task<TableOperationResult> second = harness.SubmitAsync(chain[1]).AsTask();

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted, "a second same-table message must not run while commit is in flight");
            Assert.Equal(1, harness.CommitPort.MaxConcurrentCommits);

            harness.CommitPort.Release();
            TableOperationResult firstResult = await first;
            TableOperationResult secondResult = await second;

            Assert.Equal(1UL, firstResult.CommandVersion!.Value.Value);
            Assert.Equal(2UL, secondResult.CommandVersion!.Value.Value);
            Assert.Equal(2, harness.CommitPort.Requests.Count);
            Assert.Equal(0UL, harness.CommitPort.Requests[0].ExpectedStateVersion.Value);
            Assert.Equal(1UL, harness.CommitPort.Requests[1].ExpectedStateVersion.Value);
            Assert.Equal(1, harness.CommitPort.MaxConcurrentCommits);
        }

        [Fact]
        public async Task StaleOwnerEpochCommandIsRejectedWithoutVersionOrCommit()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            TableCommandEnvelope command = P31Fixtures.Command(
                P31Fixtures.DiscardCurrent(harness.InitialRound),
                0,
                "stale-owner",
                ownerEpoch: 2UL);

            TableOperationResult result = await harness.SubmitAsync(command);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, result.ReasonCode);
            Assert.Equal(0UL, result.EvaluatedStateVersion.Value);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Empty(harness.Sink.Publications);
        }

        [Fact]
        public async Task RecoveryControlWaitsBehindInFlightCommitAndThenRefusesFollowingWork()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 2);

            harness.CommitPort.BlockNext();
            Task<TableOperationResult> first = harness.SubmitAsync(chain[0]).AsTask();
            await harness.CommitPort.Entered;
            Task<TableOperationResult> begin = harness.BeginRecoveryAsync("after in-flight commit").AsTask();
            Task<TableOperationResult> afterBegin = harness.SubmitAsync(chain[1]).AsTask();

            harness.CommitPort.Release();
            TableOperationResult firstResult = await first;
            TableOperationResult beginResult = await begin;
            TableOperationResult afterBeginResult = await afterBegin;

            Assert.Equal(TableOperationStatus.Accepted, firstResult.Status);
            Assert.Equal(1UL, firstResult.CommandVersion!.Value.Value);
            Assert.Equal(TableOperationStatus.Configured, beginResult.Status);
            Assert.Equal(TableOperationStatus.Rejected, afterBeginResult.Status);
            Assert.Equal(TableRuntimeReasonCode.Recovering, afterBeginResult.ReasonCode);
            Assert.Equal(1UL, afterBeginResult.EvaluatedStateVersion.Value);
            Assert.Single(harness.CommitPort.Requests);

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(TableActorLifecycle.Recovering, snapshot.Lifecycle);
            Assert.Equal(1UL, snapshot.StateVersion.Value);
        }

        [Fact]
        public async Task RecoveryIsSerializedRefusesCommandsAndInstallsBaseline()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            TableCommandEnvelope beforeRecovery = P31Fixtures.BuildDiscardChain(harness.InitialRound, 1)[0];

            TableOperationResult began = await harness.BeginRecoveryAsync("test recovery");
            Assert.Equal(TableOperationStatus.Configured, began.Status);

            TableOperationResult refused = await harness.SubmitAsync(beforeRecovery);
            Assert.Equal(TableOperationStatus.Rejected, refused.Status);
            Assert.Equal(TableRuntimeReasonCode.Recovering, refused.ReasonCode);
            Assert.Equal(0UL, refused.EvaluatedStateVersion.Value);
            Assert.Empty(harness.CommitPort.Requests);

            RoundStateMachine recoveredRound = P31Fixtures.Dealt("round-2");
            var baseline = new TableRecoveryBaseline(
                recoveredRound,
                new StateVersion(10UL),
                new EventSeq(50UL),
                new OwnerEpoch(1));
            harness.CommitPort.SetDurableBaseline(new StateVersion(10UL), new EventSeq(50UL));

            TableOperationResult completed = await harness.CompleteRecoveryAsync(baseline);
            Assert.Equal(TableOperationStatus.Configured, completed.Status);

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(TableActorLifecycle.Serving, snapshot.Lifecycle);
            Assert.Equal(10UL, snapshot.StateVersion.Value);
            Assert.Equal(50UL, snapshot.LastEventSeq.Value);
            Assert.Equal("round-2", snapshot.RoundId);

            TableCommandEnvelope afterRecovery = P31Fixtures.Command(
                P31Fixtures.DiscardCurrent(recoveredRound),
                10,
                "after-recovery");
            TableOperationResult accepted = await harness.SubmitAsync(afterRecovery);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(11UL, accepted.CommandVersion!.Value.Value);
            Assert.Equal(51UL, accepted.Events[0].Seq.Value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedAiCommitDoesNotConsumeDecision(bool cancel)
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 1500L;
            if (cancel)
            {
                harness.CommitPort.CancelNext();
            }
            else
            {
                harness.CommitPort.FailNext();
            }

            TableTickResult first = await harness.CompleteAiAsync(decision.Ai);
            Assert.Single(first.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, first.Entries[0].Kind);
            Assert.NotNull(first.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Failed, first.Entries[0].Transition!.Status);
            Assert.Equal(0UL, first.FinalStateVersion.Value);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
            Assert.Empty(harness.Sink.Publications);

            TableTickResult retry = await harness.CompleteAiAsync(decision.Ai);
            Assert.Single(retry.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, retry.Entries[0].Kind);
            Assert.NotNull(retry.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Accepted, retry.Entries[0].Transition!.Status);
            Assert.Equal(1UL, retry.FinalStateVersion.Value);
        }

        [Fact]
        public async Task FailedTimeoutCommitMustRemainDue()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 2000L;
            harness.CommitPort.FailNext();
            TableTickResult first = await harness.TickAsync();
            Assert.Single(first.Entries);
            Assert.Equal(TickEntryKind.Timeout, first.Entries[0].Kind);
            Assert.NotNull(first.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Failed, first.Entries[0].Transition!.Status);
            Assert.Equal(0UL, first.FinalStateVersion.Value);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);

            TableTickResult retry = await harness.TickAsync();
            Assert.Single(retry.Entries);
            Assert.Equal(TickEntryKind.Timeout, retry.Entries[0].Kind);
            Assert.NotNull(retry.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Accepted, retry.Entries[0].Transition!.Status);
            Assert.Equal(1UL, retry.FinalStateVersion.Value);
        }

        [Fact]
        public async Task FailedAiCommitKeepsOriginalDeadlineAndReserve()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                decisionClockConfig: new DecisionClockConfig(1000L, 1000L, ReserveScope.Round));
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            // d1 opens at 1000, deadline 3000, reserve 1000.
            harness.Clock.Now = 2500L;
            harness.CommitPort.FailNext();
            TableTickResult failed = await harness.CompleteAiAsync(decision.Ai);
            Assert.Equal(TableOperationStatus.Failed, failed.Entries[0].Transition!.Status);
            Assert.Equal(0UL, failed.FinalStateVersion.Value);

            // If the failed commit had settled the reserve or re-opened the decision, the
            // original 3000 deadline would have moved and this tick would accept the AI.
            harness.Clock.Now = 3000L;
            TableTickResult boundary = await harness.TickAsync(
                new TableTickRequest(new[] { decision.Ai.ToDomainSubmission(3000L) }));
            Assert.Equal(2, boundary.Entries.Count);
            Assert.Equal(TickEntryKind.Timeout, boundary.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, boundary.Entries[0].Transition!.Status);
            Assert.Equal(TickEntryKind.AiRejected, boundary.Entries[1].Kind);
            Assert.Equal(TickRejectionCode.DeadlineExpired, boundary.Entries[1].RejectionCode);
            Assert.Equal(1UL, boundary.FinalStateVersion.Value);
        }

        [Fact]
        public async Task TickKeepsCommittedPrefixAndDoesNotConsumeFailedOrLaterSeats()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundStateMachine round = harness.InitialRound;
            int discarder = round.CurrentSeat;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, discarder)[0];
            TableCommandEnvelope openingDiscard = P31Fixtures.Command(
                RoundCommand.Discard(
                    round.RoundId,
                    discarder,
                    tile,
                    new[] { 1, 2, 3 }),
                0,
                "open-response-window");
            TableOperationResult openedWindow = await harness.SubmitAsync(openingDiscard);
            Assert.Equal(TableOperationStatus.Accepted, openedWindow.Status);
            Assert.Equal(1UL, openedWindow.CommandVersion!.Value.Value);
            Assert.Equal(RoundPhase.ResponseWindow, (await harness.GetSnapshotAsync()).Phase);

            for (int seat = 1; seat <= 3; seat++)
            {
                var pending = new PendingDecision(
                    new DecisionKey(round.RoundId, seat, "pass-" + seat),
                    0L,
                    new[] { new LegalActionOption(LegalActionKind.Pass, seat) });
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.OpenDecisionAsync(pending)).Status);
            }

            harness.Clock.Now = 2000L;
            harness.CommitPort.SucceedNext();
            harness.CommitPort.FailNext();

            TableTickResult first = await harness.TickAsync();
            Assert.Equal(3, first.Entries.Count);
            Assert.Equal(TickEntryKind.Timeout, first.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, first.Entries[0].Transition!.Status);
            Assert.Equal(2UL, first.Entries[0].Transition!.CommandVersion!.Value.Value);
            Assert.Equal(TableOperationStatus.Failed, first.Entries[1].Transition!.Status);
            Assert.Equal(TableOperationStatus.Failed, first.Entries[2].Transition!.Status);
            Assert.Equal(2UL, first.FinalStateVersion.Value);

            TableTickResult retry = await harness.TickAsync();
            Assert.Equal(2, retry.Entries.Count);
            Assert.All(retry.Entries, entry => Assert.Equal(TickEntryKind.Timeout, entry.Kind));
            Assert.All(
                retry.Entries,
                entry => Assert.Equal(TableOperationStatus.Accepted, entry.Transition!.Status));
            Assert.Equal(4UL, retry.FinalStateVersion.Value);
        }

        [Fact]
        public async Task DomainRejectedTimeoutDoesNotConsumeDecision()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            var pending = new PendingDecision(
                new DecisionKey(harness.InitialRound.RoundId, harness.InitialRound.CurrentSeat, "pass-only"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Pass, harness.InitialRound.CurrentSeat) });
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(pending)).Status);

            harness.Clock.Now = 2000L;
            TableTickResult first = await harness.TickAsync();
            Assert.Single(first.Entries);
            Assert.Equal(TickEntryKind.Timeout, first.Entries[0].Kind);
            Assert.NotNull(first.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Rejected, first.Entries[0].Transition!.Status);
            Assert.Equal("NO_RESPONSE_WINDOW", first.Entries[0].Transition!.ReasonCode);
            Assert.Equal(0UL, first.FinalStateVersion.Value);
            Assert.Empty(harness.CommitPort.Requests);

            TableTickResult second = await harness.TickAsync();
            Assert.Single(second.Entries);
            Assert.Equal(TickEntryKind.Timeout, second.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Rejected, second.Entries[0].Transition!.Status);
            Assert.Equal("NO_RESPONSE_WINDOW", second.Entries[0].Transition!.ReasonCode);
            Assert.Equal(0UL, second.FinalStateVersion.Value);
        }

        [Fact]
        public async Task MissingDefaultActionDoesNotConsumeDecision()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            var pending = new PendingDecision(
                new DecisionKey(harness.InitialRound.RoundId, harness.InitialRound.CurrentSeat, "no-default"),
                0L,
                Array.Empty<LegalActionOption>());
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(pending)).Status);

            harness.Clock.Now = 2000L;
            TableTickResult first = await harness.TickAsync();
            Assert.Single(first.Entries);
            Assert.Equal(TickEntryKind.Timeout, first.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.NoDefaultAction, first.Entries[0].RejectionCode);
            Assert.Equal(TableOperationStatus.Rejected, first.Entries[0].Transition!.Status);
            Assert.Equal(TableRuntimeReasonCode.NoDefaultAction, first.Entries[0].Transition!.ReasonCode);
            Assert.Equal(0UL, first.FinalStateVersion.Value);

            TableTickResult second = await harness.TickAsync();
            Assert.Single(second.Entries);
            Assert.Equal(TickEntryKind.Timeout, second.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.NoDefaultAction, second.Entries[0].RejectionCode);
            Assert.Equal(0UL, second.FinalStateVersion.Value);
        }

        [Fact]
        public async Task UnsupportedActionDoesNotConsumeDecision()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            SingleActionDecisionFixture decision = P31Fixtures.BuildSingleActionDecision(
                harness,
                LegalActionKind.Tsumo);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 1500L;
            TableTickResult first = await harness.CompleteAiAsync(decision.Ai);
            Assert.Single(first.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, first.Entries[0].Kind);
            Assert.NotNull(first.Entries[0].Transition);
            Assert.Equal(TableOperationStatus.Rejected, first.Entries[0].Transition!.Status);
            Assert.Equal(TableRuntimeReasonCode.UnsupportedAction, first.Entries[0].Transition!.ReasonCode);
            Assert.Equal(0UL, first.FinalStateVersion.Value);

            TableTickResult second = await harness.CompleteAiAsync(decision.Ai);
            Assert.Single(second.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, second.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Rejected, second.Entries[0].Transition!.Status);
            Assert.Equal(TableRuntimeReasonCode.UnsupportedAction, second.Entries[0].Transition!.ReasonCode);
            Assert.Equal(0UL, second.FinalStateVersion.Value);
        }
    }
}

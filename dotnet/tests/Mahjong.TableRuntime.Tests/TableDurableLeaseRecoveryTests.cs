using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableDurableLeaseRecoveryTests
    {
        [Fact]
        public async Task ScopeRoleRoomAndCrossAccountQueryAreNotClientClaims()
        {
            await using DurableHarness spectator = await DurableHarness.CreateAsync(
                role: TableScopeRole.Spectator);
            TableOperationResult spectatorResult = await spectator.SubmitAsync(
                spectator.Envelope(P31Fixtures.DiscardCurrent(spectator.InitialRound), 0, "spectator"));
            Assert.Equal(TableOperationStatus.Rejected, spectatorResult.Status);
            Assert.Equal(TableRuntimeReasonCode.ScopeRoleMismatch, spectatorResult.ReasonCode);
            Assert.Equal(0, spectator.Store.ReceiptCount);
            Assert.Equal(0, spectator.Store.EventCount);

            await using DurableHarness wrongRoom = await DurableHarness.CreateAsync(scopeRoom: "other-room");
            TableOperationResult roomResult = await wrongRoom.SubmitAsync(
                wrongRoom.Envelope(P31Fixtures.DiscardCurrent(wrongRoom.InitialRound), 0, "room"));
            Assert.Equal(TableOperationStatus.Rejected, roomResult.Status);
            Assert.Equal(TableRuntimeReasonCode.ScopeRoomMismatch, roomResult.ReasonCode);
            Assert.Equal(0, wrongRoom.Store.ReceiptCount);

            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableScopeRecord secondAccount = harness.Scopes.Issue(
                "acct-2",
                TableScopeRole.Player,
                DurableHarness.Room,
                (ulong)DurableHarness.UtcBase,
                (ulong)DurableHarness.UtcBase + 3_600_000UL);
            TableOperationResult accepted = await harness.SubmitAsync(
                harness.Envelope(
                    P31Fixtures.DiscardCurrent(harness.InitialRound),
                    0,
                    "r-a2",
                    scope: secondAccount));
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);

            ReceiptLookupResult other = await harness.Actor.QueryReceiptAsync(
                DurableHarness.Account,
                secondAccount.ScopeId,
                new RequestId("r-a2"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ReceiptLookupStatus.NotFound, other.Status);
            Assert.Null(other.Receipt);

            ReceiptLookupResult owner = await harness.Actor.QueryReceiptAsync(
                "acct-2",
                secondAccount.ScopeId,
                new RequestId("r-a2"),
                TestContext.Current.CancellationToken);
            Assert.True(owner.IsDefinitive);
        }

        [Fact]
        public async Task RevokedOrExpiredScopeRejectsNewRequestsButReplaysCommittedReceipt()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableCommandEnvelope acceptedCommand = harness.Envelope(
                P31Fixtures.DiscardCurrent(harness.InitialRound),
                0,
                "r-before-revoke");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(acceptedCommand)).Status);
            int receipts = harness.Store.ReceiptCount;
            int events = harness.Store.EventCount;

            harness.Scopes.Revoke(harness.Scope.ScopeId, (ulong)harness.Clock.UtcNowMs);

            TableOperationResult replay = await harness.SubmitAsync(acceptedCommand);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            ReceiptLookupResult lookup = await harness.Actor.QueryReceiptAsync(
                DurableHarness.Account,
                harness.Scope.ScopeId,
                acceptedCommand.RequestId,
                TestContext.Current.CancellationToken);
            Assert.True(lookup.IsDefinitive);

            TableOperationResult afterRevoke = await harness.SubmitAsync(
                harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 1, "r-after-revoke"));
            Assert.Equal(TableOperationStatus.Rejected, afterRevoke.Status);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, afterRevoke.ReasonCode);
            Assert.Equal(receipts, harness.Store.ReceiptCount);
            Assert.Equal(events, harness.Store.EventCount);

            harness.Clock.Now += 4_000_000L;
            TableOperationResult afterExpiry = await harness.SubmitAsync(
                harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 1, "r-after-expiry"));
            Assert.Equal(TableOperationStatus.Rejected, afterExpiry.Status);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, afterExpiry.ReasonCode);
            Assert.Equal(receipts, harness.Store.ReceiptCount);
        }

        [Fact]
        public async Task RevocationAtCommitBoundaryRollsBackEverything()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableCommandEnvelope command = harness.Envelope(
                P31Fixtures.DiscardCurrent(harness.InitialRound),
                0,
                "r-race");
            harness.Store.BeforeScopeCheckForTest = () =>
                harness.Scopes.Revoke(harness.Scope.ScopeId, (ulong)harness.Clock.UtcNowMs);

            TableOperationResult result = await harness.SubmitAsync(command);
            harness.Store.BeforeScopeCheckForTest = null;

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, result.ReasonCode);
            Assert.Equal(0, harness.Store.ReceiptCount);
            Assert.Equal(0, harness.Store.EventCount);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0UL, snapshot.StateVersion.Value);
            Assert.Empty(harness.Sink.Publications);
        }

        [Fact]
        public async Task OldOwnerIsStoppedAndNewLeaseEpochRecoversBeforeServing()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync();
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(first.InitialRound, 3)
                .Select((value, index) => first.Envelope(value.Command, index, "chain-" + index))
                .ToList();
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(chain[0])).Status);

            await using DurableHarness recovered = DurableHarness.Attach(first, DurableHarness.Owner, 1UL);
            TableRecoveryResult result = await recovered.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.Equal(2UL, result.Baseline!.OwnerEpoch.Value);
            Assert.Equal(1UL, result.Baseline.StateVersion.Value);

            TableOperationResult staleOwner = await first.SubmitAsync(
                first.Envelope(chain[1].Command, 1, "stale-owner", ownerEpoch: 1UL));
            Assert.Equal(TableOperationStatus.Failed, staleOwner.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, staleOwner.ReasonCode);
            TableSnapshot firstSnapshot = await first.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableActorLifecycle.LeaseLost, firstSnapshot.Lifecycle);
            Assert.Single(first.Sink.Publications, value => value.Kind == TablePublicationKind.LeaseLost);

            TableOperationResult accepted = await recovered.SubmitAsync(
                recovered.Envelope(chain[1].Command, 1, "new-owner", ownerEpoch: 2UL, recoveryGeneration: 1L));
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(2UL, accepted.CommandVersion!.Value.Value);
        }

        [Fact]
        public async Task SameEpochWrongStateIsRejectedWithoutEventOrPublication()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await first.SubmitAsync(first.Envelope(P31Fixtures.DiscardCurrent(first.InitialRound), 0, "first"))).Status);
            int events = first.Store.EventCount;

            await using DurableHarness stale = DurableHarness.Attach(first, DurableHarness.Owner, 1UL);
            TableOperationResult result = await stale.SubmitAsync(
                stale.Envelope(P31Fixtures.DiscardCurrent(stale.InitialRound), 0, "stale-state"));
            Assert.Equal(TableOperationStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, result.ReasonCode);
            Assert.Equal(events, first.Store.EventCount);
            Assert.DoesNotContain(stale.Sink.Publications, value => value.Kind == TablePublicationKind.Applied);
            TableSnapshot snapshot = await stale.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableActorLifecycle.Faulted, snapshot.Lifecycle);
        }

        [Fact]
        public async Task RejectedReceiptCannotBypassOwnerFence()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await first.SubmitAsync(first.Envelope(P31Fixtures.DiscardCurrent(first.InitialRound), 0, "fence-ok"))).Status);
            int receipts = first.Store.ReceiptCount;
            await first.Store.AcquireAsync(
                DurableHarness.Room,
                DurableHarness.Owner,
                TimeSpan.FromMinutes(10),
                TestContext.Current.CancellationToken);

            RoundStateMachine round = first.InitialRound;
            int wrongSeat = (round.CurrentSeat + 1) % 4;
            TileId wrongTile = round.Tiles.TilesInZone(TileZone.Hand, wrongSeat)[0];
            TableOperationResult rejected = await first.SubmitAsync(
                first.Envelope(
                    RoundCommand.Discard(round.RoundId, wrongSeat, wrongTile),
                    1,
                    "fence-rejected",
                    ownerEpoch: 1UL));
            Assert.Equal(TableOperationStatus.Failed, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, rejected.ReasonCode);
            Assert.Equal(receipts, first.Store.ReceiptCount);
            Assert.Equal(2, first.Store.EventCount);
        }

        [Fact]
        public async Task TwoActorsSameKeyCommitOnceAndSecondSeesExactReceipt()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync();
            TableCommandEnvelope command = first.Envelope(
                P31Fixtures.DiscardCurrent(first.InitialRound),
                0,
                "shared-key");
            await using DurableHarness second = DurableHarness.Attach(first, "owner-2", 1UL);

            TableOperationResult a = await first.SubmitAsync(command);
            TableOperationResult b = await second.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Accepted, a.Status);
            Assert.Equal(TableOperationStatus.Replayed, b.Status);
            Assert.Equal(a.CommandVersion, b.CommandVersion);
            Assert.Equal(1, first.Store.ReceiptCount);
            Assert.Equal(2, first.Store.EventCount);
            IReadOnlyList<TableEventRecord> events = await first.Store.LoadEventsAfterAsync(
                DurableHarness.Room,
                new EventSeq(0),
                TestContext.Current.CancellationToken);
            Assert.Single(events.Select(value => value.CommandVersion).Distinct());
        }

        [Fact]
        public async Task EveryPreApplyFaultPointRollsBackAndDoesNotConsumeDecision()
        {
            foreach (TableStoreFaultPoint point in Enum.GetValues(typeof(TableStoreFaultPoint)))
            {
                await using DurableHarness harness = await DurableHarness.CreateAsync();
                RoundStateMachine round = harness.InitialRound;
                int seat = round.CurrentSeat;
                IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
                var pending = new PendingDecision(
                    new DecisionKey(round.RoundId, seat, "d1"),
                    0L,
                    new[]
                    {
                        new LegalActionOption(LegalActionKind.Discard, seat, hand[0]),
                        new LegalActionOption(LegalActionKind.Discard, seat, hand[1])
                    },
                    null);
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.Actor.OpenDecisionAsync(pending, TestContext.Current.CancellationToken)).Status);

                TableCommandEnvelope command = harness.Envelope(
                    RoundCommand.Discard(round.RoundId, seat, hand[0]),
                    0,
                    "fault-" + point,
                    decisionId: "d1");
                harness.Store.ArmedFaultPoint = point;
                TableOperationResult failed = await harness.SubmitAsync(command);

                Assert.Equal(TableOperationStatus.Failed, failed.Status);
                Assert.Contains(
                    failed.ReasonCode,
                    new[] { TableRuntimeReasonCode.CommitFailed, TableRuntimeReasonCode.CommitOutcomeUnknown });
                Assert.DoesNotContain(harness.Sink.Publications, value => value.Kind == TablePublicationKind.Applied);
                Assert.Empty(harness.Legacy.CommittedRequests);
                Assert.Empty(harness.Legacy.Requests);

                if (point == TableStoreFaultPoint.AfterApplyBeforeReturn)
                {
                    Assert.True(harness.Store.EventCount > 0);
                    ReceiptLookupResult lookup = await harness.Actor.QueryReceiptAsync(
                        DurableHarness.Account,
                        harness.Scope.ScopeId,
                        command.RequestId,
                        TestContext.Current.CancellationToken);
                    Assert.True(lookup.IsDefinitive);
                    continue;
                }

                Assert.Equal(0, harness.Store.EventCount);
                Assert.Equal(0, harness.Store.ReceiptCount);
                harness.Store.ArmedFaultPoint = null;
                TableOperationResult retried = await harness.SubmitAsync(command);
                Assert.Equal(TableOperationStatus.Accepted, retried.Status);
                TableTimingSnapshot timing = await harness.Actor.GetTimingSnapshotAsync(
                    TestContext.Current.CancellationToken);
                Assert.Null(timing.Seats[seat].PendingKey);
            }
        }

        [Fact]
        public async Task LeaseRenewalFailureStopsServingWithoutRetry()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            harness.Clock.Now += 700_000L;

            TableLeaseRenewResult renew = await harness.Actor.RenewLeaseAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(TableLeaseRenewStatus.Expired, renew.Status);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableActorLifecycle.LeaseLost, snapshot.Lifecycle);
            Assert.Single(harness.Sink.Publications, value => value.Kind == TablePublicationKind.LeaseLost);

            TableOperationResult command = await harness.SubmitAsync(
                harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "after-expiry"));
            Assert.Equal(TableOperationStatus.Rejected, command.Status);
            Assert.Equal(TableRuntimeReasonCode.LeaseLost, command.ReasonCode);
            Assert.Equal(0, harness.Store.EventCount);
        }

        [Fact]
        public async Task ConcurrentSameKeyAcrossTwoActorsCommitsOnce()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync();
            await using DurableHarness second = DurableHarness.Attach(first, DurableHarness.Owner, 1UL);
            TableCommandEnvelope command = first.Envelope(
                P31Fixtures.DiscardCurrent(first.InitialRound),
                0,
                "concurrent");
            Task<TableOperationResult> left = first.SubmitAsync(command).AsTask();
            Task<TableOperationResult> right = second.SubmitAsync(command).AsTask();
            TableOperationResult[] results = await Task.WhenAll(left, right);

            Assert.Single(results, value => value.Status == TableOperationStatus.Accepted);
            Assert.Single(results, value => value.Status == TableOperationStatus.Replayed);
            Assert.Equal(1, first.Store.ReceiptCount);
            Assert.Equal(2, first.Store.EventCount);
        }

        [Fact]
        public async Task SaveCheckpointRejectsExpiredLeaseAndLeavesCheckpointUnchanged()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            TableCheckpoint before = (await h.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            h.Clock.Now += 700_000L;

            TableOperationResult saved = await h.Actor.SaveCheckpointAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Failed, saved.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, saved.ReasonCode);
            TableCheckpoint after = (await h.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            Assert.Equal(before.TimingVersion, after.TimingVersion);
            Assert.Equal(before.ControlVersion, after.ControlVersion);
        }

        [Fact]
        public async Task SameEpochOldControlCannotOverwriteCheckpoint()
        {
            await using DurableHarness old = await DurableHarness.CreateAsync();
            await using DurableHarness current = DurableHarness.Attach(old, DurableHarness.Owner, 1UL);
            TableControlResult changed = await current.Actor.RequestTakeoverAsync(
                1,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                "control fence",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableControlStatus.Changed, changed.Status);

            TableOperationResult saved = await old.Actor.SaveCheckpointAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Failed, saved.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleControlVersion, saved.ReasonCode);
            TableCheckpoint checkpoint = (await old.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            Assert.Equal(1L, checkpoint.ControlVersion);
            TableSnapshot snapshot = await old.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableActorLifecycle.Faulted, snapshot.Lifecycle);
        }

        [Fact]
        public async Task ConcurrentOldAndNewOwnerCheckpointSavesLeaveNewOwnerState()
        {
            await using DurableHarness old = await DurableHarness.CreateAsync();
            await using DurableHarness current = DurableHarness.Attach(old, DurableHarness.Owner, 1UL);
            Assert.True((await current.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken)).Recovered);

            Task<TableOperationResult> oldSave = old.Actor.SaveCheckpointAsync(TestContext.Current.CancellationToken).AsTask();
            Task<TableOperationResult> currentSave = current.Actor.SaveCheckpointAsync(TestContext.Current.CancellationToken).AsTask();
            TableOperationResult[] results = await Task.WhenAll(oldSave, currentSave);

            Assert.Equal(TableOperationStatus.Failed, results[0].Status);
            Assert.Equal(TableOperationStatus.Configured, results[1].Status);
            TableCheckpoint checkpoint = (await old.Store.LoadCheckpointAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            Assert.Equal(0UL, checkpoint.StateVersion.Value);
            Assert.Equal(0L, checkpoint.ControlVersion);
        }

        [Fact]
        public async Task RevokeAfterScopeReadCannotInterleaveBeforeCommitApply()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            TableCommandEnvelope command = h.Envelope(
                P31Fixtures.DiscardCurrent(h.InitialRound),
                0,
                "scope-race");
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            h.Store.AfterScopeCheckForTest = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            };

            Task<TableOperationResult> submit = Task.Run(async () =>
                await h.SubmitAsync(command).ConfigureAwait(false),
                TestContext.Current.CancellationToken);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            var revokeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task revoke = Task.Run(() =>
            {
                revokeStarted.TrySetResult(true);
                h.Scopes.Revoke(h.Scope.ScopeId, (ulong)h.Clock.UtcNowMs);
            }, TestContext.Current.CancellationToken);
            await revokeStarted.Task;
            Assert.False(revoke.IsCompleted);

            release.Set();
            TableOperationResult result = await submit;
            await revoke;
            h.Store.AfterScopeCheckForTest = null;

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.True(h.Scopes.ReadScopeForCommit(h.Scope.ScopeId)!.IsRevoked);
            TableOperationResult next = await h.SubmitAsync(
                h.Envelope(P31Fixtures.DiscardCurrent(h.InitialRound), 1, "scope-race-next"));
            Assert.Equal(TableOperationStatus.Rejected, next.Status);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, next.ReasonCode);
        }

        [Fact]
        public async Task TerminalRecoveryWireExposesTypedSettlementFromPersistedFacts()
        {
            var sink = new RecordingRecoveryStateSink();
            var source = new RecoveredProjectionSource(sink, DurableHarness.TerminalFactsSource());
            await using DurableHarness first = await DurableHarness.CreateAsync(
                projectionSource: source,
                streamOptions: new TableStreamOptions(),
                recoverySink: sink);
            RoundStateMachine round = first.InitialRound;
            var settlement = new RoundSettlement(
                "terminal-wire",
                RoundOutcome.Tsumo(0),
                new long[] { 26000, 24000, 25000, 25000 },
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            TableCommandEnvelope command = first.Envelope(
                RoundCommand.Tsumo(round.RoundId, 0, settlement),
                0,
                "terminal-wire",
                api: "TSUMO");
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(command)).Status);

            await using DurableHarness recovered = DurableHarness.Attach(
                first,
                DurableHarness.Owner,
                1UL,
                sink);
            TableRecoveryResult result = await recovered.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.True(result.Report!.Terminal);
            Assert.True(result.Report.OutboxPayloadMatches);

            var playerSink = new RecordingStreamSink();
            TableStreamControlResult player = await recovered.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("terminal-player"),
                        TableStreamAudience.ForSeat(0),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    new OwnerEpoch(2),
                    1L,
                    playerSink,
                    new RoundId(recovered.InitialRound.RoundId),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.True(
                player.Status == TableStreamControlStatus.Subscribed ||
                player.Status == TableStreamControlStatus.BaselineQueued,
                player.ReasonCode + ": " + player.Detail);
            Assert.Single(await playerSink.WaitForCountAsync(1));
            PlayerSnapshotView playerView = playerSink.Frames[0].Player!;
            Assert.NotEmpty(playerView.Public.SettlementEvents);
            Assert.Contains(
                playerView.Public.SettlementEvents,
                value => value.Type == ProtocolEventType.TsumoWin);
            Assert.NotNull(playerView.Private.Cheat);

            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult spectator = await recovered.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("terminal-spectator"),
                        TableStreamAudience.Spectator,
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    new OwnerEpoch(2),
                    1L,
                    spectatorSink,
                    new RoundId(recovered.InitialRound.RoundId)),
                TestContext.Current.CancellationToken);
            Assert.True(
                spectator.Status == TableStreamControlStatus.Subscribed ||
                spectator.Status == TableStreamControlStatus.BaselineQueued,
                spectator.ReasonCode + ": " + spectator.Detail);
            Assert.Single(await spectatorSink.WaitForCountAsync(1));
            Assert.NotEmpty(spectatorSink.Frames[0].Public!.View.SettlementEvents);
        }

        [Fact]
        public async Task ControlEpochAndDecisionTimingAreRecoveredConsistently()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableControlResult takeover = await harness.Actor.RequestTakeoverAsync(
                1,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                "recovery test",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableControlStatus.Changed, takeover.Status);
            Assert.Equal(1L, takeover.ControlVersion);

            RoundStateMachine round = harness.InitialRound;
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, 1);
            var pending = new PendingDecision(
                new DecisionKey(round.RoundId, 1, "d-recovery"),
                1L,
                new[]
                {
                    new LegalActionOption(LegalActionKind.Discard, 1, hand[0]),
                    new LegalActionOption(LegalActionKind.Discard, 1, hand[1])
                },
                null);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(pending, TestContext.Current.CancellationToken)).Status);

            TableCheckpoint checkpoint = await harness.SaveCheckpointAsync();
            TableTimingCheckpoint persistedTiming = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
            Assert.Single(persistedTiming.Decisions);
            long originalBase = persistedTiming.Decisions[0].RemainingBaseMillis;

            harness.Clock.Now += 2000L;
            var service = new TableRecoveryService(harness.Store, harness.Store, harness.Store, harness.Clock);
            TableRecoveryResult result = await service.RecoverAsync(
                DurableHarness.Room,
                new TableRecoveryOptions(
                    DurableHarness.Owner,
                    "boot-2",
                    1,
                    TimeSpan.FromMinutes(10),
                    1000L,
                    DurableHarness.Room),
                TestContext.Current.CancellationToken);

            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.Equal(1, result.Report!.ControlVersion);
            Assert.Equal(1, result.Report.DecisionsRestored);
            Assert.Equal(1L, result.Baseline!.Control!.ControlVersion);
            Assert.Equal(1L, result.Baseline.Control.State.Seats[1].ControlEpoch);
            Assert.Equal(SeatControllerKind.Takeover, result.Baseline.Control.State.Seats[1].Kind);
            TableRecoveredDecision restored = Assert.Single(result.Baseline.Timing.Decisions);
            Assert.Equal(originalBase + 1000L, restored.RemainingBaseMillis);
            Assert.Equal(2UL, result.Baseline.OwnerEpoch.Value);
        }

        [Fact]
        public async Task TerminalCommitWritesOneOutboxAndReplayKeepsItUnique()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync(
                projectionSource: DurableHarness.TerminalFactsSource());
            RoundStateMachine round = harness.InitialRound;
            var settlement = new RoundSettlement(
                "result-1",
                RoundOutcome.Tsumo(0),
                new long[] { 26000, 24000, 25000, 25000 },
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            TableCommandEnvelope command = harness.Envelope(
                RoundCommand.Tsumo(round.RoundId, 0, settlement),
                0,
                "terminal",
                api: "TSUMO");

            TableOperationResult first = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            TableSettlementOutboxRecord? outbox = await harness.Store.TryGetAsync(
                DurableHarness.Room,
                1,
                TestContext.Current.CancellationToken);
            Assert.NotNull(outbox);
            Assert.Equal(1, harness.Store.OutboxCount);

            TableOperationResult replay = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1, harness.Store.OutboxCount);

            var service = new TableRecoveryService(harness.Store, harness.Store, harness.Store, harness.Clock);
            TableRecoveryResult recovery = await service.RecoverAsync(
                DurableHarness.Room,
                new TableRecoveryOptions(
                    DurableHarness.Owner,
                    "boot-2",
                    1,
                    TimeSpan.FromMinutes(10),
                    0L,
                    DurableHarness.Room),
                TestContext.Current.CancellationToken);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            Assert.True(recovery.Report!.Terminal);
            Assert.True(recovery.Report.OutboxPresent);
            Assert.True(recovery.Report.OutboxPayloadMatches);
        }

        [Fact]
        public async Task RecoveryInstallsStrictWireBaselineAndRejectsOldGenerationAndEpoch()
        {
            var recoverySink = new RecordingRecoveryStateSink();
            var recoveredSource = new RecoveredProjectionSource(recoverySink);
            CheatLedger appliedLedger = DurableHarness.NewLedger()
                .Apply("mark-fee", 0, -5, "TILE_MARK").Ledger!;
            await using DurableHarness first = await DurableHarness.CreateAsync(
                ledger: appliedLedger,
                projectionSource: recoveredSource,
                streamOptions: new TableStreamOptions(),
                recoverySink: recoverySink,
                configure: options => options.SnapshotEventWindow = 1);
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(first.InitialRound, 3)
                .Select((value, index) => first.Envelope(value.Command, index, "wire-" + index))
                .ToList();
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(chain[0])).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(chain[1])).Status);

            await using DurableHarness recovered = DurableHarness.Attach(
                first,
                DurableHarness.Owner,
                1UL,
                recoverySink);
            TableRecoveryResult result = await recovered.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.NotNull(recoverySink.Installed);
            Assert.NotNull(recoverySink.Installed!.Cheat);
            Assert.Equal(2UL, recoverySink.Installed.StateVersion.Value);
            Assert.Equal(4, recoverySink.Installed.CommittedHistory.Count);
            Assert.Equal(result.Report!.TimingVersion, recoverySink.Installed.TimingVersion);

            var oldOwnerSink = new RecordingStreamSink();
            TableStreamControlResult oldOwner = await recovered.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("s-old"),
                        TableStreamAudience.ForSeat(0),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    new OwnerEpoch(1),
                    1L,
                    oldOwnerSink,
                    new RoundId(recovered.InitialRound.RoundId),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableStreamControlStatus.Rejected, oldOwner.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, oldOwner.ReasonCode);

            var oldGenerationSink = new RecordingStreamSink();
            TableStreamControlResult oldGeneration = await recovered.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("s-gen0"),
                        TableStreamAudience.Spectator,
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    new OwnerEpoch(2),
                    0L,
                    oldGenerationSink,
                    new RoundId(recovered.InitialRound.RoundId)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableStreamControlStatus.Rejected, oldGeneration.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, oldGeneration.ReasonCode);

            var playerSink = new RecordingStreamSink();
            TableStreamControlResult player = await recovered.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("s-player"),
                        TableStreamAudience.ForSeat(0),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    new OwnerEpoch(2),
                    1L,
                    playerSink,
                    new RoundId(recovered.InitialRound.RoundId),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.True(
                player.Status == TableStreamControlStatus.Subscribed ||
                player.Status == TableStreamControlStatus.BaselineQueued,
                player.ReasonCode + ": " + player.Detail);
            Assert.Single(await playerSink.WaitForCountAsync(1));
            Assert.True(playerSink.Frames[0].IsFullSnapshot);
            Assert.Equal(2UL, playerSink.Frames[0].StreamVersion.Value);
            Assert.NotNull(playerSink.Frames[0].Player);
            Assert.True(recoveredSource.UsedRecovered);
            PlayerSnapshotView playerView = playerSink.Frames[0].Player!;
            Assert.Equal(2UL, playerView.StateVersion.Value);
            Assert.NotNull(playerView.Private.Cheat);
            Assert.Contains(
                playerView.Private.Cheat!.Ledger,
                entry => entry.EntryId == "mark-fee" && entry.Amount == -5);
            Assert.Equal(appliedLedger.BalanceOf(0), playerView.Private.Cheat.CheatPoints);
            Assert.True(playerView.Private.DiscardHistory.Count >= 1);
            Assert.NotEmpty(playerView.Private.ClosedTiles);

            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult spectator = await recovered.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("s-spectator"),
                        TableStreamAudience.Spectator,
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    new OwnerEpoch(2),
                    1L,
                    spectatorSink,
                    new RoundId(recovered.InitialRound.RoundId)),
                TestContext.Current.CancellationToken);
            Assert.True(
                spectator.Status == TableStreamControlStatus.Subscribed ||
                spectator.Status == TableStreamControlStatus.BaselineQueued,
                spectator.ReasonCode + ": " + spectator.Detail);
            Assert.Single(await spectatorSink.WaitForCountAsync(1));
            Assert.True(spectatorSink.Frames[0].IsFullSnapshot);
            Assert.NotNull(spectatorSink.Frames[0].Public);
            PublicSnapshotView publicView = spectatorSink.Frames[0].Public!;
            Assert.Equal(2UL, publicView.StateVersion.Value);
            Assert.NotNull(publicView.View.Board);
            Assert.Equal(2, publicView.View.Board!.Players.Sum(value => value.Discards.Count));
            Assert.Empty(publicView.View.SettlementEvents);
        }
    }

    internal sealed class RecoveredProjectionSource : ITableProjectionContextSource
    {
        private readonly TestProjectionSource _live;

        public RecoveredProjectionSource(
            RecordingRecoveryStateSink sink,
            TestProjectionSource? live = null)
        {
            Sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _live = live ?? new TestProjectionSource();
            if (_live.EventFactsFactory == null)
            {
                _live.EventFactsFactory = request => WireEventFacts.Build(request);
            }
        }

        public RecordingRecoveryStateSink Sink { get; }

        public int Calls { get; private set; }

        public bool UsedRecovered { get; private set; }

        public TableProjectionContextData Capture(TableProjectionContextRequest request)
        {
            Calls++;
            TableProjectionContextData? persisted = Sink.Installed?.ProjectionFacts;
            if (persisted != null)
            {
                if (!string.Equals(persisted.RoundId, request.Round.RoundId, StringComparison.Ordinal) ||
                    persisted.StateVersion != request.StateVersion)
                {
                    throw new InvalidOperationException(
                        "Recovered projection facts do not match the requested round/state.");
                }

                if (Sink.Installed!.Cheat == null)
                {
                    throw new InvalidOperationException("Recovered cheat aggregate is missing.");
                }

                UsedRecovered = true;
                return new TableProjectionContextData(
                    persisted.RoundId,
                    persisted.StateVersion,
                    persisted.SessionVersion,
                    Sink.Installed.Cheat,
                    persisted.PublicFacts,
                    persisted.PrivateFacts,
                    persisted.Session,
                    persisted.Policy,
                    persisted.EventFacts,
                    persisted.ConcealedMeldFacts);
            }

            return _live.Capture(request);
        }
    }
}

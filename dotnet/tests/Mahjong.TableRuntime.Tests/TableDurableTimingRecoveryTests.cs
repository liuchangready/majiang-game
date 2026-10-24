using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableDurableTimingRecoveryTests
    {
        private static PendingDecision Decision(DurableHarness h, int seat, string id)
        {
            IReadOnlyList<TileId> hand = h.InitialRound.Tiles.TilesInZone(TileZone.Hand, seat);
            return new PendingDecision(
                new DecisionKey(h.InitialRound.RoundId, seat, id),
                0L,
                hand.Select(tile => new LegalActionOption(LegalActionKind.Discard, seat, tile)).ToArray(),
                null);
        }

        private static async Task<TableRecoveryResult> RecoverAsync(
            DurableHarness h,
            string boot,
            long grace)
        {
            var service = new TableRecoveryService(h.Store, h.Store, h.Store, h.Clock);
            return await service.RecoverAsync(
                DurableHarness.Room,
                new TableRecoveryOptions(
                    DurableHarness.Owner,
                    boot,
                    1,
                    TimeSpan.FromMinutes(10),
                    grace,
                    DurableHarness.Room),
                TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SameBootRecoveryChargesActiveElapsedAndDoesNotGrantGrace()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "same-boot"), TestContext.Current.CancellationToken)).Status);
            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            long remaining = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload).Decisions[0].RemainingBaseMillis;
            h.Clock.Now += 300L;

            TableRecoveryResult recovered = await RecoverAsync(h, DurableHarness.Boot, 500L);

            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(remaining - 300L, Assert.Single(recovered.Baseline!.Timing.Decisions).RemainingBaseMillis);
        }

        [Fact]
        public async Task LongCrossBootDowntimeIsExcludedButGraceIsAppliedOnce()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "long-boot"), TestContext.Current.CancellationToken)).Status);
            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            long remaining = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload).Decisions[0].RemainingBaseMillis;
            h.Clock.Now += 600_000L;

            TableRecoveryResult recovered = await RecoverAsync(h, "boot-2", 500L);

            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(remaining + 500L, Assert.Single(recovered.Baseline!.Timing.Decisions).RemainingBaseMillis);
            Assert.Equal(600_000L, recovered.Report!.DowntimeMillis);
        }

        [Fact]
        public async Task UtcRollbackAcrossBootFailsClosed()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "rollback"), TestContext.Current.CancellationToken)).Status);
            await h.SaveCheckpointAsync();
            h.Clock.Now -= 5000L;

            TableRecoveryResult recovered = await RecoverAsync(h, "boot-2", 0L);

            Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
            Assert.Equal(TableRuntimeReasonCode.ClockRegression, recovered.ReasonCode);
        }

        [Fact]
        public async Task TwoConsecutiveRecoveriesDoNotGrantGraceTwice()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "twice"), TestContext.Current.CancellationToken)).Status);
            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            long remaining = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload).Decisions[0].RemainingBaseMillis;
            h.Clock.Now += 60_000L;

            TableRecoveryResult first = await RecoverAsync(h, "boot-2", 500L);
            Assert.True(first.Recovered, first.ReasonCode + ": " + first.Detail);
            Assert.Equal(remaining + 500L, Assert.Single(first.Baseline!.Timing.Decisions).RemainingBaseMillis);

            h.Clock.Now += 100L;
            TableRecoveryResult second = await RecoverAsync(h, "boot-2", 500L);
            Assert.True(second.Recovered, second.ReasonCode + ": " + second.Detail);
            Assert.Equal(remaining + 400L, Assert.Single(second.Baseline!.Timing.Decisions).RemainingBaseMillis);
        }

        [Fact]
        public async Task DecisionOpenedAfterCheckpointIsRecoveredWithoutLoss()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "before-checkpoint"), TestContext.Current.CancellationToken)).Status);
            await h.SaveCheckpointAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 1, "after-checkpoint"), TestContext.Current.CancellationToken)).Status);

            TableRecoveryResult recovered = await RecoverAsync(h, "boot-2", 0L);

            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(2, recovered.Baseline!.Timing.Decisions.Count);
            Assert.Contains(recovered.Baseline.Timing.Decisions, value => value.Key.Seat == 0);
            Assert.Contains(recovered.Baseline.Timing.Decisions, value => value.Key.Seat == 1);
        }

        [Fact]
        public async Task MultipleSeatsAndInternalTimeoutKeepReservesConsistent()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync(configure: options =>
                options.DecisionClockConfig = new DecisionClockConfig(100L, 1000L, ReserveScope.Round));
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "seat0"), TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 1, "seat1"), TestContext.Current.CancellationToken)).Status);
            await h.SaveCheckpointAsync();

            h.Clock.Now += 350L;
            IReadOnlyList<TileId> hand = h.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0);
            Assert.Equal(TableOperationStatus.Accepted, (await h.SubmitAsync(h.Envelope(
                RoundCommand.Discard(h.InitialRound.RoundId, 0, hand[0]),
                0,
                "seat0-command",
                decisionId: "seat0"))).Status);

            h.Clock.Now += 500L;
            TableTickResult tick = await h.Actor.TickAsync(
                TableTickRequest.Empty,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, tick.Status);

            TableTimingSnapshot live = await h.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);
            TableRecoveryResult recovered = await RecoverAsync(h, "boot-2", 0L);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(
                    live.Seats[seat].RemainingReserveMillis,
                    recovered.Baseline!.Timing.SeatReservesMillis![seat]);
            }
        }

        [Fact]
        public async Task FailedCommitDoesNotAdvanceTimingJournalAndRetrySucceeds()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "fault-timing"), TestContext.Current.CancellationToken)).Status);
            long before = (await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.TimingVersion;
            IReadOnlyList<TileId> hand = h.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0);
            TableCommandEnvelope command = h.Envelope(
                RoundCommand.Discard(h.InitialRound.RoundId, 0, hand[0]),
                0,
                "fault-timing",
                decisionId: "fault-timing");
            h.Store.ArmedFaultPoint = TableStoreFaultPoint.AfterStateCheck;

            TableOperationResult failed = await h.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Equal(before, (await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.TimingVersion);
            Assert.Contains(
                (await h.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken)).Seats,
                value => value.PendingKey != null);

            h.Store.ArmedFaultPoint = null;
            Assert.Equal(TableOperationStatus.Accepted, (await h.SubmitAsync(command)).Status);
            Assert.True((await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.TimingVersion > before);
        }

        [Fact]
        public async Task LostResponseTimingIsRecoveredAndReplayDoesNotReconsume()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync(configure: options =>
                options.DecisionClockConfig = new DecisionClockConfig(100L, 1000L, ReserveScope.Round));
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h, 0, "lost-timing"), TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TileId> hand = h.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0);
            TableCommandEnvelope command = h.Envelope(
                RoundCommand.Discard(h.InitialRound.RoundId, 0, hand[0]),
                0,
                "lost-timing",
                decisionId: "lost-timing");
            h.Clock.Now += 500L;
            h.Store.ArmedFaultPoint = TableStoreFaultPoint.AfterApplyBeforeReturn;

            TableOperationResult lost = await h.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Failed, lost.Status);
            long committedTimingVersion = (await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.TimingVersion;
            Assert.Equal(600L, (await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.Timing.SeatReservesMillis[0]);

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(600L, recovered.Baseline!.Timing.SeatReservesMillis![0]);

            TableOperationResult replay = await h.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(
                committedTimingVersion + 1L,
                (await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.TimingVersion);
            Assert.Equal(600L, (await h.Store.LoadTimingAsync(DurableHarness.Room, TestContext.Current.CancellationToken))!.Timing.SeatReservesMillis[0]);
        }

        [Fact]
        public async Task MissingRecoverySinkFailsClosedInsteadOfServing()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            h.Durable.RecoveryStateSink = null;
            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryStateSinkRequired, recovered.ReasonCode);
            TableSnapshot snapshot = await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableActorLifecycle.Faulted, snapshot.Lifecycle);
        }

        [Fact]
        public async Task RecoveryInstallsTypedCheatLedgerAndLatestCommittedFacts()
        {
            CheatLedger initial = DurableHarness.NewLedger();
            CheatLedgerApplyResult applied = initial.Apply("fee-1", 0, -5, "TILE_MARK");
            Assert.True(applied.Success, applied.Error.ToString());
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness first = await DurableHarness.CreateAsync(
                ledger: applied.Ledger!,
                recoverySink: sink);
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(
                first.Envelope(P31Fixtures.DiscardCurrent(first.InitialRound), 0, "facts-1"))).Status);
            await first.SaveCheckpointAsync();

            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(first.InitialRound, 2);
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(
                first.Envelope(chain[1].Command, 1, "facts-2"))).Status);

            await using DurableHarness recovered = DurableHarness.Attach(
                first,
                DurableHarness.Owner,
                1UL,
                sink);
            TableRecoveryResult result = await recovered.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.NotNull(sink.Installed);
            Assert.NotNull(sink.Installed!.Cheat);
            Assert.Equal(1, sink.Installed.Cheat!.Ledger.Version);
            Assert.Equal(-5, sink.Installed.Cheat.Ledger.Entries[0].Amount);
            Assert.NotNull(sink.Installed.ProjectionFacts);
            Assert.Equal(2UL, sink.Installed.ProjectionFacts!.StateVersion.Value);
            Assert.Equal(4, sink.Installed.ProjectionFacts.PublicFacts.Count);
            Assert.True(result.Report!.CheatStateRestored);
            Assert.True(result.Report.CommittedHistoryCount >= 4);
        }

        [Fact]
        public async Task RecoveredCrossBootDecisionCanCommitBeforeGraceDeadline()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync(configure: options =>
                options.DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round));
            Assert.Equal(TableOperationStatus.Configured,
                (await first.Actor.OpenDecisionAsync(Decision(first, 0, "cross-command"), TestContext.Current.CancellationToken)).Status);
            await first.SaveCheckpointAsync();
            first.Clock.Now += 60_000L;
            await using DurableHarness recovered = DurableHarness.Attach(
                first,
                DurableHarness.Owner,
                1UL,
                new RecordingRecoveryStateSink(),
                "boot-2");
            Assert.True((await recovered.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken)).Recovered);
            IReadOnlyList<TileId> hand = first.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0);
            TableCommandEnvelope command = recovered.Envelope(
                RoundCommand.Discard(first.InitialRound.RoundId, 0, hand[0]),
                0,
                "cross-command",
                decisionId: "cross-command",
                ownerEpoch: 2UL,
                recoveryGeneration: 1L);
            TableOperationResult result = await recovered.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Equal(1UL, result.CommandVersion!.Value.Value);
        }

        [Fact]
        public async Task RecoveredCrossBootDecisionTimesOutAtGraceDeadlineWithoutSecondGrant()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync(configure: options =>
                options.DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round));
            Assert.Equal(TableOperationStatus.Configured,
                (await first.Actor.OpenDecisionAsync(Decision(first, 0, "cross-timeout"), TestContext.Current.CancellationToken)).Status);
            await first.SaveCheckpointAsync();
            first.Clock.Now += 60_000L;
            await using DurableHarness recovered = DurableHarness.Attach(
                first,
                DurableHarness.Owner,
                1UL,
                new RecordingRecoveryStateSink(),
                "boot-2");
            Assert.True((await recovered.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken)).Recovered);
            TableTimingSnapshot before = await recovered.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);
            long deadline = before.Seats[0].Timing!.DeadlineMillis;
            recovered.Clock.Now = deadline + 1L;
            TableTickResult tick = await recovered.Actor.TickAsync(
                TableTickRequest.Empty,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, tick.Status);
            Assert.Contains(tick.Entries, entry => entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Equal(1UL, (await recovered.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        [Fact]
        public async Task OwnerTransferDuringHostInstallFailsClosed()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            h.Durable.RecoveryStateSink = new ActionRecoverySink(() =>
                h.Store.AcquireAsync(
                    DurableHarness.Room,
                    DurableHarness.Owner,
                    TimeSpan.FromMinutes(10),
                    TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult());

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
            Assert.DoesNotContain(h.Sink.Publications, value => value.Kind == TablePublicationKind.Recovered);
        }

        [Fact]
        public async Task TimingChangeDuringHostInstallFailsClosed()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            h.Durable.RecoveryStateSink = new ActionRecoverySink(() =>
            {
                TableTimingJournalRecord current = h.Store.LoadTimingAsync(
                    DurableHarness.Room,
                    TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()!;
                var appended = h.Store.AppendTimingAsync(
                    new TableTimingAppendRequest(
                        DurableHarness.Room,
                        DurableHarness.Owner,
                        new OwnerEpoch(2),
                        current.StateVersion,
                        current.ControlVersion,
                        current.TimingVersion,
                        current.Timing),
                    TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
                Assert.True(appended.IsCommitted, appended.ReasonCode + ": " + appended.Detail);
            });

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
            Assert.DoesNotContain(h.Sink.Publications, value => value.Kind == TablePublicationKind.Recovered);
        }

        [Fact]
        public async Task HostInstallExceptionFailsClosedBeforePublishingRecovered()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            h.Durable.RecoveryStateSink = new ActionRecoverySink(() =>
                throw new InvalidOperationException("host install failed"));

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
            Assert.DoesNotContain(h.Sink.Publications, value => value.Kind == TablePublicationKind.Recovered);
        }

        [Fact]
        public async Task FullHistoryIsInstalledEvenWhenSnapshotWindowIsZero()
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness first = await DurableHarness.CreateAsync(
                configure: options => options.SnapshotEventWindow = 0,
                recoverySink: sink);
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(first.InitialRound, 3)
                .Select((value, index) => first.Envelope(value.Command, index, "history-" + index))
                .ToList();
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(chain[0])).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(chain[1])).Status);
            await first.SaveCheckpointAsync();
            Assert.Equal(TableOperationStatus.Accepted, (await first.SubmitAsync(chain[2])).Status);
            int totalEvents = first.Store.EventCount;

            await using DurableHarness recovered = DurableHarness.Attach(
                first,
                DurableHarness.Owner,
                1UL,
                sink);
            TableRecoveryResult result = await recovered.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.NotNull(sink.Installed);
            Assert.Equal(totalEvents, sink.Installed!.CommittedHistory.Count);
            Assert.True(result.Report!.CommittedHistoryCount >= totalEvents);
        }
    }

    internal sealed class ActionRecoverySink : ITableRecoveryStateSink
    {
        private readonly Action _install;

        public ActionRecoverySink(Action install)
        {
            _install = install ?? throw new ArgumentNullException(nameof(install));
        }

        public void Install(TableRecoveryAuxState state) => _install();
    }
}

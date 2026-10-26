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
    /// <summary>
    /// D-1-01 regression: the N-2 settlement entry must fail closed when the composition root
    /// did not supply an <see cref="ITableSettlementFactsPort"/>. The tests use the real actor,
    /// the real winning <see cref="RoundStateMachine"/> fixture and, for the durable case, the
    /// real in-memory store. They never inject the rejection reason or mirror the actor branch.
    /// </summary>
    public sealed class SettlementAuthorityUnavailableTests
    {
        [Fact]
        public async Task MissingFactsPortRejectsAValidTsumoWithoutAdvancingAnyState()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = SettlementAuthorityHarness.Create(round, factsPort: null);
            DecisionKey key = await harness.OpenTsumoDecisionAsync(round);

            TableSnapshot before = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            TableTimingSnapshot timingBefore =
                await harness.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);
            TilePlacement[] placementsBefore = round.Tiles.AsPlacements().ToArray();
            long[] roundScoresBefore = round.Scores.ToArray();
            ulong physicalHashBefore = round.Tiles.StateHash;
            long roundVersionBefore = round.Version;
            RoundPhase phaseBefore = round.Phase;

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(round.RoundId, before.StateVersion, "authority-missing-tsumo", key),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Equal(TableSettlementReasonCode.AuthorityUnavailable, result.ReasonCode);
            Assert.Contains("No N-2 settlement host fact port", result.Detail);
            Assert.Equal(before.StateVersion, result.EvaluatedStateVersion);
            Assert.Empty(result.Events);
            Assert.Null(result.Receipt);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Empty(harness.Sink.Publications);

            TableSnapshot after = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            TableTimingSnapshot timingAfter =
                await harness.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);

            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.LastEventSeq, after.LastEventSeq);
            Assert.Equal(before.Phase, after.Phase);
            Assert.Equal(before.CurrentSeat, after.CurrentSeat);
            Assert.False(after.IsFinished);
            Assert.Equal(before.Scores.ToArray(), after.Scores.ToArray());
            Assert.Empty(after.RecentEvents);

            Assert.Equal(roundVersionBefore, round.Version);
            Assert.Equal(phaseBefore, round.Phase);
            Assert.False(round.IsFinished);
            Assert.Equal(roundScoresBefore, round.Scores.ToArray());
            Assert.Equal(physicalHashBefore, round.Tiles.StateHash);
            Assert.True(placementsBefore.SequenceEqual(round.Tiles.AsPlacements()));
            Assert.Equal(TilePositionMap.TotalTileCount, placementsBefore.Length);

            Assert.Equal(key, timingAfter.Seats[0].PendingKey!.Value);
            Assert.NotNull(timingAfter.Seats[0].Timing);
            Assert.Equal(
                timingBefore.Seats[0].Timing!.DeadlineMillis,
                timingAfter.Seats[0].Timing!.DeadlineMillis);
            Assert.Equal(timingBefore.StateVersion, timingAfter.StateVersion);
        }

        [Fact]
        public async Task TheSameValidTsumoIsAcceptedWhenFactsPortIsConfigured()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using var harness = SettlementAuthorityHarness.Create(round, new DefaultSettlementFactsPort());
            DecisionKey key = await harness.OpenTsumoDecisionAsync(round);
            TableSnapshot before = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);

            TableOperationResult result = await harness.Actor.SubmitSettlementAsync(
                harness.Request(round.RoundId, before.StateVersion, "authority-present-tsumo", key),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.True(result.IsAccepted);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.TsumoWin);
            Assert.Contains(result.Events, record => record.Type == RoundEventType.RoundEnded);
            Assert.NotNull(result.Receipt);

            TableSnapshot after = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(after.IsFinished);
            Assert.NotEqual(before.StateVersion, after.StateVersion);
            Assert.Equal(TableActorLifecycle.Serving, after.Lifecycle);
        }

        [Fact]
        public async Task DurableRejectionLeavesNoEventsReceiptsOrSettlementOutboxAndStaysRepeatable()
        {
            RoundStateMachine round = SettlementFixture.Tsumo();
            await using DurableMissingFactsPortHarness harness =
                await DurableMissingFactsPortHarness.CreateAsync(round);

            TableSnapshot snapshotBefore =
                await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            TableStateRow? stateBefore = await harness.Store.LoadStateAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            TableCheckpoint? checkpointBefore = await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            IReadOnlyList<TableEventRecord> eventsBefore = await harness.Store.LoadEventsAfterAsync(
                DurableHarness.Room, new EventSeq(0), TestContext.Current.CancellationToken);
            IReadOnlyList<TableSettlementOutboxRecord> outboxBefore =
                await harness.Store.ListUnpublishedAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(stateBefore);
            Assert.NotNull(checkpointBefore);
            Assert.NotNull(checkpointBefore!.CheatPayloadSha256);
            Assert.Empty(eventsBefore);
            Assert.Empty(outboxBefore);

            string firstRequestId = "authority-durable-missing-1";
            TableOperationResult first = await harness.Actor.SubmitSettlementAsync(
                harness.Request(round.RoundId, snapshotBefore.StateVersion, firstRequestId),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, first.Status);
            Assert.Equal(TableSettlementReasonCode.AuthorityUnavailable, first.ReasonCode);
            Assert.Empty(first.Events);
            Assert.Null(first.Receipt);

            TableStateRow? stateAfterFirst = await harness.Store.LoadStateAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            TableCheckpoint? checkpointAfterFirst = await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            IReadOnlyList<TableEventRecord> eventsAfterFirst = await harness.Store.LoadEventsAfterAsync(
                DurableHarness.Room, new EventSeq(0), TestContext.Current.CancellationToken);
            IReadOnlyList<TableSettlementOutboxRecord> outboxAfterFirst =
                await harness.Store.ListUnpublishedAsync(TestContext.Current.CancellationToken);

            AssertStateUnchanged(stateBefore, stateAfterFirst);
            AssertCheckpointUnchanged(checkpointBefore, checkpointAfterFirst);
            Assert.Empty(eventsAfterFirst);
            Assert.Empty(outboxAfterFirst);
            await AssertDurableRejectionReceiptAsync(harness, snapshotBefore, firstRequestId);

            string secondRequestId = "authority-durable-missing-2";
            TableOperationResult second = await harness.Actor.SubmitSettlementAsync(
                harness.Request(round.RoundId, snapshotBefore.StateVersion, secondRequestId),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, second.Status);
            Assert.Equal(TableSettlementReasonCode.AuthorityUnavailable, second.ReasonCode);
            Assert.Empty(second.Events);
            Assert.Null(second.Receipt);

            TableStateRow? stateAfterSecond = await harness.Store.LoadStateAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            TableCheckpoint? checkpointAfterSecond = await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken);
            AssertStateUnchanged(stateBefore, stateAfterSecond);
            AssertCheckpointUnchanged(checkpointBefore, checkpointAfterSecond);
            Assert.Empty(await harness.Store.LoadEventsAfterAsync(
                DurableHarness.Room, new EventSeq(0), TestContext.Current.CancellationToken));
            Assert.Empty(await harness.Store.ListUnpublishedAsync(TestContext.Current.CancellationToken));
            await AssertDurableRejectionReceiptAsync(harness, snapshotBefore, secondRequestId);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Empty(harness.Sink.Publications);

            TableSnapshot snapshotAfter =
                await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(snapshotBefore.StateVersion, snapshotAfter.StateVersion);
            Assert.Equal(snapshotBefore.LastEventSeq, snapshotAfter.LastEventSeq);
            Assert.False(snapshotAfter.IsFinished);
            Assert.Equal(snapshotBefore.Scores.ToArray(), snapshotAfter.Scores.ToArray());
        }

        private static void AssertStateUnchanged(TableStateRow? before, TableStateRow? after)
        {
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before!.StateVersion, after!.StateVersion);
            Assert.Equal(before.LastEventSeq, after.LastEventSeq);
            Assert.Equal(before.StateFingerprint, after.StateFingerprint);
            Assert.Equal(before.StateHash, after.StateHash);
            Assert.Equal(before.CheatVersion, after.CheatVersion);
            Assert.Equal(before.CheatFingerprint, after.CheatFingerprint);
            Assert.Equal(before.CheatBound, after.CheatBound);
        }

        private static void AssertCheckpointUnchanged(TableCheckpoint? before, TableCheckpoint? after)
        {
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before!.StateVersion, after!.StateVersion);
            Assert.Equal(before.LastEventSeq, after.LastEventSeq);
            Assert.Equal(before.StateFingerprint, after.StateFingerprint);
            Assert.Equal(before.CheatPayloadSha256, after.CheatPayloadSha256);
        }

        private static async Task AssertDurableRejectionReceiptAsync(
            DurableMissingFactsPortHarness harness,
            TableSnapshot snapshotBefore,
            string requestId)
        {
            TableDurableReceipt? stored = await harness.Store.TryGetAsync(
                DurableHarness.Account,
                harness.Scope.ScopeId,
                new RequestId(requestId),
                TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            Assert.False(stored!.IsAccepted);
            Assert.Equal(ReceiptStatus.Rejected, stored.Receipt.Status);
            Assert.Equal(TableSettlementReasonCode.AuthorityUnavailable, stored.Receipt.ReasonCode);
            Assert.Null(stored.Receipt.CommandVersion);
            Assert.Null(stored.Receipt.EventSeq);
            Assert.Equal(snapshotBefore.StateVersion, stored.Receipt.EvaluatedStateVersion);
        }
    }

    internal sealed class SettlementAuthorityHarness : IAsyncDisposable
    {
        private SettlementAuthorityHarness(
            TableActor actor,
            ManualTableClock clock,
            RecordingCommitPort commitPort,
            RecordingPublicationSink sink,
            RoundStateMachine round)
        {
            Actor = actor;
            Clock = clock;
            CommitPort = commitPort;
            Sink = sink;
            Round = round;
        }

        public TableActor Actor { get; }

        public ManualTableClock Clock { get; }

        public RecordingCommitPort CommitPort { get; }

        public RecordingPublicationSink Sink { get; }

        public RoundStateMachine Round { get; }

        public static SettlementAuthorityHarness Create(
            RoundStateMachine round,
            ITableSettlementFactsPort? factsPort)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            var actor = new TableActor(
                Options(),
                round,
                commitPort,
                clock,
                sink,
                null,
                null,
                null,
                null,
                factsPort);
            return new SettlementAuthorityHarness(actor, clock, commitPort, sink, round);
        }

        public async Task<DecisionKey> OpenTsumoDecisionAsync(RoundStateMachine round)
        {
            var key = new DecisionKey(round.RoundId, 0, "authority-decision-" + round.RoundId);
            var pending = new PendingDecision(
                key,
                0L,
                new[] { new LegalActionOption(LegalActionKind.Tsumo, 0) },
                round.Tiles.DrawnTile?.Tile);
            TableOperationResult opened = await Actor.OpenDecisionAsync(
                pending, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);
            return key;
        }

        public TableSettlementRequest Request(
            string roundId,
            StateVersion expectedVersion,
            string requestId,
            DecisionKey key)
        {
            return new TableSettlementRequest(
                new TableSettlementIntent(roundId, TableSettlementAction.Tsumo, 0),
                new RequestId(requestId),
                expectedVersion,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                new DecisionId(key.DecisionId));
        }

        public ValueTask DisposeAsync()
        {
            return Actor.DisposeAsync();
        }

        private static TableActorOptions Options()
        {
            return new TableActorOptions
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
        }
    }

    internal sealed class DurableMissingFactsPortHarness : IAsyncDisposable
    {
        private DurableMissingFactsPortHarness(
            TableActor actor,
            InMemoryTableStore store,
            TableScopeRecord scope,
            RecordingCommitPort commitPort,
            RecordingPublicationSink sink,
            RoundStateMachine round)
        {
            Actor = actor;
            Store = store;
            Scope = scope;
            CommitPort = commitPort;
            Sink = sink;
            Round = round;
        }

        public TableActor Actor { get; }

        public InMemoryTableStore Store { get; }

        public TableScopeRecord Scope { get; }

        public RecordingCommitPort CommitPort { get; }

        public RecordingPublicationSink Sink { get; }

        public RoundStateMachine Round { get; }

        public static async Task<DurableMissingFactsPortHarness> CreateAsync(RoundStateMachine round)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var scopes = new InMemoryRequestScopeAuthority();
            TableScopeRecord scope = scopes.Issue(
                DurableHarness.Account,
                TableScopeRole.Player,
                DurableHarness.Room,
                (ulong)DurableHarness.UtcBase,
                (ulong)DurableHarness.UtcBase + 3_600_000UL);
            var store = new InMemoryTableStore(clock, scopes);
            CheatLedger ledger = DurableHarness.NewLedger();
            TableCheckpoint checkpoint = DurableHarness.InitialCheckpoint(
                round, ledger, boot: DurableHarness.Boot);
            await store.CreateRoomAsync(checkpoint, CancellationToken.None);
            TableLeaseAcquireResult lease = await store.AcquireAsync(
                DurableHarness.Room,
                DurableHarness.Owner,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.True(lease.Acquired, lease.Detail);

            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                scopes,
                DurableHarness.Owner,
                TimeSpan.FromMinutes(10),
                DurableHarness.Boot,
                1,
                DurableHarness.Room);
            durable.CheatLedgerProvider = () => ledger;
            durable.CheatStateProvider = () => new CheatState(
                ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            durable.RecoveryStateSink = new RecordingRecoveryStateSink();

            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            var actor = new TableActor(
                Options(),
                round,
                commitPort,
                clock,
                sink,
                null,
                DurableHarness.TerminalFactsSource(),
                null,
                durable,
                settlementFactsPort: null);
            return new DurableMissingFactsPortHarness(actor, store, scope, commitPort, sink, round);
        }

        public TableSettlementRequest Request(
            string roundId,
            StateVersion expectedVersion,
            string requestId)
        {
            byte[] payload = Encoding.UTF8.GetBytes("settlement-payload:" + requestId);
            return new TableSettlementRequest(
                new TableSettlementIntent(roundId, TableSettlementAction.Tsumo, 0),
                new RequestId(requestId),
                expectedVersion,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                Scope.ScopeId,
                "SETTLEMENT_TSUMO",
                TablePayloadSha256.Compute(payload));
        }

        public ValueTask DisposeAsync()
        {
            return Actor.DisposeAsync();
        }

        private static TableActorOptions Options()
        {
            return new TableActorOptions
            {
                RoomId = DurableHarness.Room,
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
        }
    }
}

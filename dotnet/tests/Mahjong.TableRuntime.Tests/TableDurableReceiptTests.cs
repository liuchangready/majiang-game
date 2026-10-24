using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableReceiptLeaseRecoveryTests
    {
        [Fact]
        public async Task AcceptedReceiptReplayReturnsOriginalWithoutNewVersionOrEvent()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            TableCommandEnvelope command = harness.Envelope(
                P31Fixtures.DiscardCurrent(round),
                0,
                "r-accepted");

            TableOperationResult first = await harness.SubmitAsync(command);
            Assert.True(
                first.Status == TableOperationStatus.Accepted,
                first.ReasonCode + ": " + first.Detail);
            Assert.NotNull(first.DurableReceipt);
            int eventsAfterFirst = harness.Store.EventCount;
            int receiptsAfterFirst = harness.Store.ReceiptCount;
            int publicationsAfterFirst = harness.Sink.Publications.Count;

            TableOperationResult replay = await harness.SubmitAsync(command);

            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.True(replay.Replayed);
            Assert.NotNull(replay.DurableReceipt);
            Assert.Equal(first.CommandVersion, replay.CommandVersion);
            Assert.Equal(first.Receipt!.EvaluatedStateVersion, replay.Receipt!.EvaluatedStateVersion);
            Assert.Equal(first.Receipt.LastEventSeq, replay.Receipt.LastEventSeq);
            Assert.Empty(replay.Events);
            Assert.Equal(eventsAfterFirst, harness.Store.EventCount);
            Assert.Equal(receiptsAfterFirst, harness.Store.ReceiptCount);
            Assert.Equal(publicationsAfterFirst, harness.Sink.Publications.Count);

            ReceiptLookupResult lookup = await harness.Actor.QueryReceiptAsync(
                DurableHarness.Account,
                harness.Scope.ScopeId,
                command.RequestId,
                TestContext.Current.CancellationToken);
            Assert.True(lookup.IsDefinitive);
            Assert.Equal(first.CommandVersion, lookup.Receipt!.CommandVersion);
            Assert.True(lookup.Receipt.Replayed);
        }

        [Fact]
        public async Task RejectedReceiptReplaysOriginalResultAfterStateAndClockChange()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            int wrongSeat = (round.CurrentSeat + 1) % 4;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, wrongSeat)[0];
            TableCommandEnvelope rejected = harness.Envelope(
                RoundCommand.Discard(round.RoundId, wrongSeat, tile),
                0,
                "r-rejected");

            TableOperationResult first = await harness.SubmitAsync(rejected);
            Assert.Equal(TableOperationStatus.Rejected, first.Status);
            Assert.NotNull(first.DurableReceipt);
            Assert.Equal(0UL, first.EvaluatedStateVersion.Value);
            Assert.Null(first.CommandVersion);

            harness.Clock.Now += 5000L;
            TableOperationResult accepted = await harness.SubmitAsync(
                harness.Envelope(P31Fixtures.DiscardCurrent(round), 0, "r-after-reject"));
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(1UL, accepted.CommandVersion!.Value.Value);

            TableOperationResult replay = await harness.SubmitAsync(rejected);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(ReceiptStatus.Rejected, replay.DurableReceipt!.Receipt.Status);
            Assert.Equal(0UL, replay.EvaluatedStateVersion.Value);
            Assert.Equal("NOT_YOUR_TURN", replay.ReasonCode);
            Assert.Null(replay.CommandVersion);
            var events = await harness.Store.LoadEventsAfterAsync(
                DurableHarness.Room,
                new EventSeq(0),
                TestContext.Current.CancellationToken);
            Assert.Single(events, e => e.CommandVersion.Value == 1UL && e.Ordinal == 0);
            Assert.Equal(2, events.Count(e => e.CommandVersion.Value == 1UL));
        }

        [Fact]
        public async Task RecoveryReplaysAfterNonZeroCheckpointAndKeepsRejectedReceipt()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4)
                .Select((value, index) => harness.Envelope(value.Command, index, "v" + (index + 1)))
                .ToList();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(chain[0])).Status);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(chain[1])).Status);

            TableCheckpoint checkpoint = await harness.SaveCheckpointAsync();
            Assert.Equal(2UL, checkpoint.StateVersion.Value);

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(chain[2])).Status);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(chain[3])).Status);

            int wrongSeat = (round.CurrentSeat + 1) % 4;
            TileId wrongTile = round.Tiles.TilesInZone(TileZone.Hand, wrongSeat)[0];
            TableOperationResult rejected = await harness.SubmitAsync(
                harness.Envelope(
                    RoundCommand.Discard(round.RoundId, wrongSeat, wrongTile),
                    4,
                    "v-rejected"));
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);

            harness.Clock.Now += 5000L;
            var service = new TableRecoveryService(harness.Store, harness.Store, harness.Store, harness.Clock);
            var options = new TableRecoveryOptions(
                DurableHarness.Owner,
                "boot-2",
                1,
                TimeSpan.FromMinutes(10),
                1000L,
                DurableHarness.Room);
            TableRecoveryResult result = await service.RecoverAsync(
                DurableHarness.Room,
                options,
                TestContext.Current.CancellationToken);

            Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
            Assert.NotNull(result.Baseline);
            Assert.NotNull(result.Report);
            Assert.Equal(2UL, result.Report!.CheckpointVersion.Value);
            Assert.Equal(2, result.Report.ReplayedCommands);
            Assert.Equal(4, result.Report.ReplayedEvents);
            Assert.Equal(4UL, result.Report.FinalStateVersion.Value);
            Assert.Equal(5000L, result.Report.DowntimeMillis);
            Assert.Equal(1000L, result.Report.GraceMillis);
            Assert.Equal("boot-1", result.Report.PreviousBootId);
            Assert.Equal("boot-2", result.Report.CurrentBootId);
            Assert.Equal(0, result.Report.ControlVersion);
            Assert.Equal(0, result.Report.DecisionsRestored);
            Assert.NotNull(result.Report.CheatLedgerVersion);
            Assert.Equal(0, result.Report.CheatLedgerVersion!.Value);

            TableStateRow state = (await harness.Store.LoadStateAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            Assert.Equal(state.StateFingerprint, result.Baseline!.Round.SourceFingerprint);
            Assert.Equal(state.StateVersion, result.Baseline.StateVersion);
            Assert.Equal(state.LastEventSeq, result.Baseline.LastEventSeq);
            Assert.Equal(136, result.Baseline.Round.Tiles.AsPlacements().Count);
            Assert.Equal(136, result.Baseline.Round.Tiles.CountAt(TileZone.Wall) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.Hand) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.Discard) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.Meld) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.DeadWall) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.DeadWallReplacement) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.Sleeve) +
                              result.Baseline.Round.Tiles.CountAt(TileZone.Unassigned));

            IReadOnlyList<TableRecoveryAuditRecord> audits = await harness.Store.LoadRecoveryAuditsAsync(
                DurableHarness.Room,
                TestContext.Current.CancellationToken);
            Assert.Single(audits);
            Assert.Equal(TableRecoveryAuditKind.RecoveryAdjusted, audits[0].Kind);

            ReceiptLookupResult rejectedLookup = await harness.Actor.QueryReceiptAsync(
                DurableHarness.Account,
                harness.Scope.ScopeId,
                new RequestId("v-rejected"),
                TestContext.Current.CancellationToken);
            Assert.True(rejectedLookup.IsDefinitive);
            Assert.Equal(ReceiptStatus.Rejected, rejectedLookup.Receipt!.Status);
        }

        [Fact]
        public async Task AcceptedCommandHasNonEmptyEventsAndOneContiguousCommandVersion()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableOperationResult result = await harness.SubmitAsync(
                harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "multi"));
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.True(result.Events.Count >= 2);
            Assert.Equal(1UL, result.CommandVersion!.Value.Value);
            for (int i = 0; i < result.Events.Count; i++)
            {
                Assert.Equal(i, result.Events[i].Ordinal);
                Assert.Equal(result.CommandVersion.Value, result.Events[i].CommandVersion);
                Assert.NotNull(result.Events[i].Payload);
                if (i > 0)
                {
                    Assert.Equal(result.Events[i - 1].Seq.Value + 1, result.Events[i].Seq.Value);
                }
            }

            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1UL, snapshot.StateVersion.Value);
        }

        [Fact]
        public async Task LostResponseIsQueryableAndExplicitRecoveryReplaysSameRequest()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            TableCommandEnvelope command = harness.Envelope(
                P31Fixtures.DiscardCurrent(harness.InitialRound),
                0,
                "lost-response");
            harness.Store.ArmedFaultPoint = TableStoreFaultPoint.AfterApplyBeforeReturn;

            TableOperationResult lost = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Failed, lost.Status);
            Assert.Equal(TableRuntimeReasonCode.CommitOutcomeUnknown, lost.ReasonCode);
            Assert.DoesNotContain(harness.Sink.Publications, value => value.Kind == TablePublicationKind.Applied);
            Assert.Equal(2, harness.Store.EventCount);

            ReceiptLookupResult lookup = await harness.Actor.QueryReceiptAsync(
                DurableHarness.Account,
                harness.Scope.ScopeId,
                command.RequestId,
                TestContext.Current.CancellationToken);
            Assert.True(lookup.IsDefinitive);
            Assert.Equal(1UL, lookup.Receipt!.CommandVersion!.Value.Value);

            TableRecoveryResult recovery = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            Assert.Equal(1UL, recovery.Baseline!.StateVersion.Value);

            TableOperationResult replay = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1UL, replay.CommandVersion!.Value.Value);
            Assert.Equal(2, harness.Store.EventCount);
        }

        [Fact]
        public async Task TerminalOutboxFaultRollsBackAndRetryWritesOnlyOneResult()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            var settlement = new RoundSettlement(
                "result-fault",
                RoundOutcome.Tsumo(0),
                new long[] { 26000, 24000, 25000, 25000 },
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            TableCommandEnvelope command = harness.Envelope(
                RoundCommand.Tsumo(round.RoundId, 0, settlement),
                0,
                "terminal-fault",
                api: "TSUMO");
            harness.Store.ArmedFaultPoint = TableStoreFaultPoint.AfterOutboxStage;
            TableOperationResult failed = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Equal(0, harness.Store.OutboxCount);
            Assert.Equal(0, harness.Store.EventCount);
            Assert.Equal(0, harness.Store.ReceiptCount);

            harness.Store.ArmedFaultPoint = null;
            TableOperationResult retried = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Accepted, retried.Status);
            Assert.Equal(1, harness.Store.OutboxCount);
            Assert.Equal(1, harness.Store.ReceiptCount);
        }

        [Fact]
        public async Task RecoveryIsBlockedWhileAnotherLiveOwnerHoldsLease()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            var service = new TableRecoveryService(harness.Store, harness.Store, harness.Store, harness.Clock);
            TableRecoveryResult result = await service.RecoverAsync(
                DurableHarness.Room,
                new TableRecoveryOptions(
                    "another-owner",
                    "boot-2",
                    1,
                    TimeSpan.FromMinutes(10),
                    0L,
                    DurableHarness.Room),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Blocked, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryLeaseHeld, result.ReasonCode);
        }

        [Fact]
        public async Task InternalTickCommitsDurablyWithoutClientReceipt()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            int seat = round.CurrentSeat;
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            var pending = new PendingDecision(
                new DecisionKey(round.RoundId, seat, "tick-1"),
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
            harness.Clock.Now += 2000L;

            TableTickResult tick = await harness.Actor.TickAsync(
                TableTickRequest.Empty,
                TestContext.Current.CancellationToken);

            Assert.True(
                tick.Status == TableOperationStatus.Accepted,
                tick.ReasonCode + ": " + tick.Detail);
            Assert.Contains(tick.Entries, entry => entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.True(harness.Store.EventCount > 0);
            Assert.Equal(0, harness.Store.ReceiptCount);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1UL, snapshot.StateVersion.Value);
        }

        [Fact]
        public async Task InternalTimeoutIsJournalledAndReplayedByRecovery()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(P31Fixtures.DiscardCurrent(round), 0, "pre-tick"))).Status);

            RoundStateMachine afterDiscard = round.Execute(P31Fixtures.DiscardCurrent(round)).State!;
            int seat = afterDiscard.CurrentSeat;
            IReadOnlyList<TileId> hand = afterDiscard.Tiles.TilesInZone(TileZone.Hand, seat);
            var pending = new PendingDecision(
                new DecisionKey(afterDiscard.RoundId, seat, "tick-replay"),
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
            TableCheckpoint checkpoint = await harness.SaveCheckpointAsync();
            Assert.Equal(1UL, checkpoint.StateVersion.Value);

            harness.Clock.Now += 2000L;
            TableTickResult tick = await harness.Actor.TickAsync(
                TableTickRequest.Empty,
                TestContext.Current.CancellationToken);
            Assert.True(tick.Status == TableOperationStatus.Accepted, tick.ReasonCode + ": " + tick.Detail);
            Assert.Equal(2UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);

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
            Assert.Equal(1, recovery.Report!.ReplayedCommands);
            Assert.True(recovery.Report.ReplayedEvents >= 2);
            Assert.Equal(2UL, recovery.Report.FinalStateVersion.Value);
        }

        [Fact]
        public async Task CheckpointPersistsHostCommittedFactsAndRealCheatLedger()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            var applied = harness.Ledger.Apply("e1", 0, -5, "TEST");
            Assert.True(applied.Success, applied.Error.ToString());
            CheatLedger appliedLedger = applied.Ledger!;
            harness.Durable.CheatLedgerProvider = () => appliedLedger;
            harness.Durable.CheatStateProvider = () => new CheatState(
                appliedLedger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            byte[] facts = Encoding.UTF8.GetBytes("P3.2-committed-facts-v1");
            harness.Durable.CommittedFactsProvider = () => facts;

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.SubmitAsync(harness.Envelope(P31Fixtures.DiscardCurrent(harness.InitialRound), 0, "facts"))).Status);
            TableCheckpoint checkpoint = await harness.SaveCheckpointAsync();
            TableCommittedFactsEnvelope factsEnvelope = TableCommittedFactsCodec.Decode(
                checkpoint.CommittedFactsPayload!,
                harness.InitialRound.RoundId,
                new StateVersion(1));
            Assert.Equal(facts, factsEnvelope.Payload);
            Assert.NotNull(checkpoint.CommittedFactsPayloadSha256);
            Assert.NotNull(checkpoint.CheatPayload);
            TableCheatLedgerCheckpoint decodedCheat = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload!);
            Assert.Single(decodedCheat.Entries);

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
            Assert.Equal(1, recovery.Report!.CheatLedgerVersion);
            Assert.Equal(1, appliedLedger.Version);
            Assert.Equal(-5, appliedLedger.Entries[0].Amount);
        }

        [Fact]
        public async Task SameKeyDifferentPayloadOrApiIsConflictWithNoSideEffect()
        {
            await using DurableHarness harness = await DurableHarness.CreateAsync();
            RoundStateMachine round = harness.InitialRound;
            TableCommandEnvelope command = harness.Envelope(
                P31Fixtures.DiscardCurrent(round),
                0,
                "r-conflict");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(command)).Status);
            int events = harness.Store.EventCount;
            int receipts = harness.Store.ReceiptCount;

            TableCommandEnvelope differentPayload = harness.Envelope(
                P31Fixtures.DiscardCurrent(round),
                0,
                "r-conflict",
                payloadText: "different");
            TableOperationResult payloadConflict = await harness.SubmitAsync(differentPayload);
            Assert.Equal(TableOperationStatus.Conflict, payloadConflict.Status);
            Assert.Equal(ProtocolErrorCode.RequestIdConflict, payloadConflict.ReasonCode);

            TableCommandEnvelope differentApi = harness.Envelope(
                P31Fixtures.DiscardCurrent(round),
                0,
                "r-conflict",
                api: "DRAW");
            TableOperationResult apiConflict = await harness.SubmitAsync(differentApi);
            Assert.Equal(TableOperationStatus.Conflict, apiConflict.Status);

            Assert.Equal(events, harness.Store.EventCount);
            Assert.Equal(receipts, harness.Store.ReceiptCount);
        }
    }
}

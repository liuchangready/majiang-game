using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    [Collection(PostgresTableCollection.Name)]
    public sealed class PostgresR1RegressionTests
    {
        [Fact]
        public async Task RealLostCommitAcknowledgement_MustReturnOutcomeUnknownAndKeepOneReceipt()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "r1-lost-ack");
            var builder = new NpgsqlConnectionStringBuilder(PostgresTableTestEnvironment.RuntimeConnectionString);
            await using var proxy = new CommitAckDropProxy(builder.Port);
            builder.Port = proxy.Port;
            builder.SslMode = SslMode.Disable;
            builder.Pooling = false;
            await using var proxiedStore = new PostgresTableStore(
                new PostgresTableOptions(builder.ConnectionString, PostgresTableTestEnvironment.PayloadKey));

            TableDurableCommitResult result = await proxiedStore.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            await proxy.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            TableDurableReceipt? receipt = await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId("r1-lost-ack"),
                TestContext.Current.CancellationToken);
            Assert.True(proxy.CommitSeen && proxy.AckWithheld);
            Assert.NotNull(receipt);
            Assert.Equal(TableCommitDisposition.OutcomeUnknown, result.Disposition);
            Assert.Equal(1UL, (await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken))!.StateVersion.Value);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
            Assert.Equal((long)candidate.EventCount, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));

            TableDurableCommitResult replay = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Replayed, replay.Disposition);
        }

        [Fact]
        public async Task EventTileAndDetail_AreNotReadableAsPlaintextShadowColumns()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "r1-private-event");
            Assert.True((await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            long shadowColumns = await PostgresTableTestEnvironment.CountAsync(
                @"SELECT count(*) FROM information_schema.columns
                   WHERE table_schema = 'table_store' AND table_name = 'table_event'
                     AND column_name IN ('tile', 'detail')",
                TestContext.Current.CancellationToken);
            Assert.Equal(0L, shadowColumns);

            IReadOnlyList<TableEventRecord> events = await fixture.Store.LoadEventsAfterAsync(
                P52Fixture.Room,
                new EventSeq(0),
                TestContext.Current.CancellationToken);
            Assert.Contains(events, value => value.Type == RoundEventType.TileDrawn && value.Tile.HasValue);
        }

        [Fact]
        public async Task LeaseExpiringDuringEventWrite_MustRollBackBeforeCommit()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "r1-lease-event");
            await InstallDelayTriggerAsync("table_event", "p52_r1_event_delay");
            await SetShortLeaseAsync();
            TableDurableCommitResult result;
            try
            {
                result = await fixture.Store.CommitAsync(
                    candidate.Request,
                    TestContext.Current.CancellationToken);
            }
            finally
            {
                await DropDelayTriggerAsync("table_event", "p52_r1_event_delay");
            }

            Assert.Equal(TableCommitDisposition.StaleOwner, result.Disposition);
            await AssertNoRowsAsync();
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_lease WHERE lease_expires_at < updated_at",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task LeaseExpiringDuringCheckpointTimingAndControlWrites_MustRollBack()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TableTimingCheckpoint timing2 = P52Fixture.Timing(
                fixture.Round.RoundId,
                P52Fixture.Boot,
                now,
                fixture.Clock.Now);
            TableCheckpoint checkpoint = TableCheckpointFactory.Create(
                P52Fixture.Room,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                P52Fixture.Control(),
                timing2,
                1,
                P52Fixture.Boot,
                now,
                cheat: null,
                committedFactsPayload: null,
                timingVersion: 2L,
                capturedMonotonicMillis: fixture.Clock.Now,
                committedHistory: null);
            var fence = new TableCheckpointFence(
                P52Fixture.Room,
                P52Fixture.Owner,
                fixture.Lease.OwnerEpoch,
                new StateVersion(0),
                0L,
                1L);

            await InstallDelayTriggerAsync("table_checkpoint", "p52_r1_checkpoint_delay");
            await SetShortLeaseAsync();
            try
            {
                TableDurableCommitResult checkpointResult = await fixture.Store.SaveCheckpointAsync(
                    checkpoint,
                    fence,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableCommitDisposition.StaleOwner, checkpointResult.Disposition);
            }
            finally
            {
                await DropDelayTriggerAsync("table_checkpoint", "p52_r1_checkpoint_delay");
            }

            await SetShortLeaseAsync();
            await InstallDelayTriggerAsync("table_timing_journal", "p52_r1_timing_delay");
            try
            {
                TableDurableCommitResult timingResult = await fixture.Store.AppendTimingAsync(
                    new TableTimingAppendRequest(
                        P52Fixture.Room,
                        P52Fixture.Owner,
                        fixture.Lease.OwnerEpoch,
                        new StateVersion(0),
                        0L,
                        1L,
                        timing2),
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableCommitDisposition.StaleOwner, timingResult.Disposition);
            }
            finally
            {
                await DropDelayTriggerAsync("table_timing_journal", "p52_r1_timing_delay");
            }

            var controlChange = new TableControlEvent(
                1L,
                fixture.Round.RoundId,
                0,
                TableControlChangeKind.Disconnect,
                null,
                null,
                SeatControllerKind.RemoteHuman,
                SeatControllerKind.RemoteHuman,
                ConnectionStatus.Connected,
                ConnectionStatus.Disconnected,
                null,
                fixture.Clock.Now,
                checked((long)now),
                "lease control fence");
            TableCheckpoint controlCheckpoint = TableCheckpointFactory.Create(
                P52Fixture.Room,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                P52Fixture.Control(1L),
                timing2,
                1,
                P52Fixture.Boot,
                now,
                cheat: null,
                committedFactsPayload: null,
                timingVersion: 2L,
                capturedMonotonicMillis: fixture.Clock.Now,
                committedHistory: null);
            var controlRequest = new TableControlCommitRequest(
                P52Fixture.Room,
                fixture.Lease.OwnerEpoch,
                0L,
                1L,
                new StateVersion(0),
                fixture.Round.RoundId,
                controlChange,
                P52Fixture.Owner);
            await SetShortLeaseAsync();
            await InstallDelayTriggerAsync("table_control_journal", "p52_r1_control_delay");
            try
            {
                TableDurableCommitResult controlResult = await fixture.Store.CommitControlAsync(
                    controlRequest,
                    controlCheckpoint,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableCommitDisposition.StaleOwner, controlResult.Disposition);
            }
            finally
            {
                await DropDelayTriggerAsync("table_control_journal", "p52_r1_control_delay");
            }

            TableStateRow state = (await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken))!;
            Assert.Equal(0UL, state.StateVersion.Value);
            Assert.Equal(0L, state.ControlVersion);
            Assert.Equal(1L, state.TimingVersion);
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_control_journal",
                TestContext.Current.CancellationToken));
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ReceiptOptionalFields_ArePreservedForAppliedRecordedAndRejected()
        {
            await AssertReceiptRoundTripAsync(
                committedAtMs: null,
                canonicalDigest: null,
                status: ReceiptStatus.Applied,
                requestId: "r1-receipt-null");
            await AssertReceiptRoundTripAsync(
                committedAtMs: 1777777777777UL,
                canonicalDigest: CanonicalPayloadDigest.Compute(Encoding.UTF8.GetBytes("canonical-p52")),
                status: ReceiptStatus.Applied,
                requestId: "r1-receipt-nonnull");
            await AssertReceiptRoundTripAsync(
                committedAtMs: 1777777777778UL,
                canonicalDigest: CanonicalPayloadDigest.Compute(Encoding.UTF8.GetBytes("canonical-recorded")),
                status: ReceiptStatus.Recorded,
                requestId: "r1-receipt-recorded");

            PayloadDigest rejectedDigest = CanonicalPayloadDigest.Compute(Encoding.UTF8.GetBytes("canonical-rejected"));
            await AssertRejectedReceiptRoundTripAsync(
                null,
                null,
                "r1-receipt-rejected-null-null");
            await AssertRejectedReceiptRoundTripAsync(
                null,
                rejectedDigest,
                "r1-receipt-rejected-null-digest");
            await AssertRejectedReceiptRoundTripAsync(
                1777777777779UL,
                null,
                "r1-receipt-rejected-time-null");
            await AssertRejectedReceiptRoundTripAsync(
                1777777777779UL,
                rejectedDigest,
                "r1-receipt-rejected-time-digest");
        }

        [Fact]
        public async Task TerminalRoundOutbox_IsGeneratedFromRealDomainResultAndRecovered()
        {
            RoundStateMachine preTerminal = P52TerminalFixture.ExhaustiveDrawReady();
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TableCheckpoint initial = P52Fixture.InitialCheckpointFor(preTerminal, now, P52Fixture.Boot);
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                initialCheckpoint: initial,
                initialRound: preTerminal);
            RoundCommand command = P52TerminalFixture.ExhaustiveDrawCommand(preTerminal);
            RoundTransition terminal = preTerminal.Execute(command);
            Assert.True(terminal.IsAccepted && terminal.State?.Result != null);
            byte[] outboxPayload = TableSettlementPayloadCodec.Encode(P52Fixture.Room, terminal.State!.Result!);
            DirectCommitCandidate candidate = fixture.BuildCommit(
                preTerminal,
                new StateVersion(0),
                new EventSeq(0),
                "r1-terminal-outbox",
                terminal: true,
                outboxMatchId: "r1-terminal-match",
                outboxResultVersion: 1,
                outboxPayloadBytes: outboxPayload,
                command: command);
            TableDurableCommitResult committed = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.True(committed.IsCommitted, committed.Detail);

            TableRecoveryResult recovery = await RecoverAsync(
                fixture,
                "r1-terminal-match",
                requireCheatLedger: false,
                requireCommittedFacts: false);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            Assert.True(recovery.Report!.Terminal);
            Assert.True(recovery.Report.OutboxPresent);
            Assert.True(recovery.Report.OutboxPayloadMatches);
            Assert.Equal(1UL, recovery.Report.FinalStateVersion.Value);
        }

        [Fact]
        public async Task NonEmptyCheatLedgerAndFacts_WithCheckpointAndTail_RecoverStrictly()
        {
            CheatState cheat = P52TerminalFixture.NonEmptyCheatState();
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate first = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "r1-ledger-first",
                nextCheatVersion: cheat.Version,
                nextCheatFingerprint: cheat.Fingerprint);
            Assert.True((await fixture.Store.CommitAsync(
                first.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            byte[] facts1 = P52TerminalFixture.BuildFacts(
                first.NextRound,
                new StateVersion(1),
                cheat,
                first.Request.Events);
            ulong checkpointUtc = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TableTimingCheckpoint checkpointTiming = P52Fixture.Timing(
                first.NextRound.RoundId,
                P52Fixture.Boot,
                checkpointUtc,
                fixture.Clock.Now);
            TableCheckpoint checkpoint = P52TerminalFixture.BuildCheckpoint(
                P52Fixture.Room,
                first.NextRound,
                new StateVersion(1),
                first.NextLastSeq,
                cheat,
                first.Request.Events,
                new[] { new TableCommandBinding(1, first.Request.Events.Count, first.NextRound.SourceFingerprint) },
                checkpointTiming,
                3L,
                checkpointUtc,
                P52Fixture.Boot);
            TableDurableCommitResult saved = await fixture.Store.SaveCheckpointAsync(
                checkpoint,
                new TableCheckpointFence(
                    P52Fixture.Room,
                    P52Fixture.Owner,
                    fixture.Lease.OwnerEpoch,
                    new StateVersion(1),
                    0L,
                    2L),
                TestContext.Current.CancellationToken);
            Assert.True(saved.IsCommitted, saved.Detail);

            int drawTurns = 0;
            for (int i = 0; i < first.NextRound.Events.Count; i++)
            {
                RoundEventType type = first.NextRound.Events[i].Type;
                if (type == RoundEventType.TileDrawn || type == RoundEventType.RinshanDrawn)
                {
                    drawTurns++;
                }
            }

            CheatState postWindowCheat = cheat.WithSleeveBloom(
                cheat.SleeveBloom.WithOpenedWindow(first.NextRound.CurrentSeat, drawTurns, out _));
            DirectCommitCandidate tailDraft = fixture.BuildCommit(
                first.NextRound,
                new StateVersion(1),
                first.NextLastSeq,
                "r1-ledger-tail",
                timingVersion: 4L,
                nextCheatVersion: postWindowCheat.Version,
                nextCheatFingerprint: postWindowCheat.Fingerprint);
            var history = new List<TableEventRecord>(first.Request.Events);
            history.AddRange(tailDraft.Request.Events);
            byte[] facts2 = P52TerminalFixture.BuildFacts(
                tailDraft.NextRound,
                new StateVersion(2),
                postWindowCheat,
                history);
            DirectCommitCandidate tail = fixture.BuildCommit(
                first.NextRound,
                new StateVersion(1),
                first.NextLastSeq,
                "r1-ledger-tail",
                timingVersion: 4L,
                committedFactsPayload: facts2,
                nextCheatVersion: postWindowCheat.Version,
                nextCheatFingerprint: postWindowCheat.Fingerprint);
            Assert.True((await fixture.Store.CommitAsync(
                tail.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            TableStateRow strictState = (await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken))!;
            TableCheckpoint strictCheckpoint = (await fixture.Store.LoadCheckpointAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken))!;
            TableCheatLedgerCheckpoint strictCheat = TableCheatLedgerCheckpointCodec.Decode(
                strictCheckpoint.CheatPayload!);
            Assert.Equal(cheat.Version, strictCheat.TotalVersion);
            Assert.Equal(cheat.Fingerprint, strictCheat.Fingerprint);
            Assert.Equal(postWindowCheat.Version, strictState.CheatVersion);
            Assert.Equal(postWindowCheat.Fingerprint, strictState.CheatFingerprint);

            TableRecoveryResult recovery = await RecoverAsync(
                fixture,
                null,
                requireCheatLedger: true,
                requireCommittedFacts: true);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            Assert.True(recovery.Report!.CheatStateRestored);
            Assert.Equal<int?>(checked((int)cheat.Ledger.Version), recovery.Report.CheatLedgerVersion);
            Assert.Equal(2UL, recovery.Report.FinalStateVersion.Value);
            Assert.Equal(1, recovery.Report.ReplayedCommands);
        }

        [Fact]
        public async Task CorruptCheatLedgerOrFacts_FailsStrictRecovery()
        {
            CheatState cheat = P52TerminalFixture.NonEmptyCheatState();
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TableCheckpoint checkpoint = P52TerminalFixture.BuildCheckpoint(
                P52Fixture.Room,
                P52Fixture.Dealt("round-p52-ledger-corrupt"),
                new StateVersion(0),
                new EventSeq(0),
                cheat,
                null,
                null,
                P52Fixture.Timing("round-p52-ledger-corrupt", P52Fixture.Boot, now),
                1L,
                now,
                P52Fixture.Boot);
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                initialCheckpoint: checkpoint,
                initialRound: P52Fixture.Dealt("round-p52-ledger-corrupt"));

            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_checkpoint SET cheat_blob = decode('00', 'hex') WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));
            TableRecoveryResult cheatResult = await RecoverAsync(
                fixture,
                null,
                requireCheatLedger: true,
                requireCommittedFacts: true);
            Assert.Equal(TableRecoveryStatus.Failed, cheatResult.Status);

            await using P52Fixture factsFixture = await P52Fixture.CreateAsync();
            TableCheckpoint factsCheckpoint = P52TerminalFixture.BuildCheckpoint(
                P52Fixture.Room,
                factsFixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                cheat,
                null,
                null,
                P52Fixture.Timing(factsFixture.Round.RoundId, P52Fixture.Boot, now),
                1L,
                now,
                P52Fixture.Boot);
            await using P52Fixture factsFixture2 = await P52Fixture.CreateAsync(
                initialCheckpoint: factsCheckpoint,
                initialRound: factsFixture.Round);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_committed_facts SET payload_blob = decode('00', 'hex') WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));
            TableRecoveryResult factsResult = await RecoverAsync(
                factsFixture2,
                null,
                requireCheatLedger: true,
                requireCommittedFacts: true);
            Assert.Equal(TableRecoveryStatus.Failed, factsResult.Status);
        }

        private static async Task AssertReceiptRoundTripAsync(
            ulong? committedAtMs,
            PayloadDigest? canonicalDigest,
            ReceiptStatus status,
            string requestId)
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                requestId,
                committedAtMs: committedAtMs,
                canonicalDigest: canonicalDigest,
                receiptStatus: status);
            Assert.True((await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            TableDurableReceipt loaded = (await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId(requestId),
                TestContext.Current.CancellationToken))!;
            AssertSameDurableReceipt(candidate.Request.Receipt!, loaded);
            TableDurableCommitResult replay = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Replayed, replay.Disposition);
            AssertSameDurableReceipt(candidate.Request.Receipt!, replay.Receipt!);
        }

        private static async Task AssertRejectedReceiptRoundTripAsync(
            ulong? committedAtMs,
            PayloadDigest? canonicalDigest,
            string requestId)
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            var original = new Receipt(
                ReceiptStatus.Rejected,
                "P52_REVIEW_REJECT",
                new StateVersion(0),
                null,
                null,
                committedAtMs,
                canonicalDigest);
            TableDurableCommitRequest request = fixture.BuildRejectedCommit(
                new StateVersion(0),
                requestId,
                original);
            Assert.True((await fixture.Store.CommitAsync(
                request,
                TestContext.Current.CancellationToken)).IsCommitted);

            TableDurableReceipt loaded = (await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId(requestId),
                TestContext.Current.CancellationToken))!;
            AssertSameDurableReceipt(request.Receipt!, loaded);
            Assert.Equal(committedAtMs, loaded.Receipt.CommittedAtMs);

            TableDurableCommitResult replay = await fixture.Store.CommitAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Replayed, replay.Disposition);
            AssertSameDurableReceipt(request.Receipt!, replay.Receipt!);
            Assert.Equal(committedAtMs, replay.Receipt!.Receipt.CommittedAtMs);

            TableStateRow state = (await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken))!;
            Assert.Equal(0UL, state.StateVersion.Value);
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
        }

        private static void AssertSameDurableReceipt(TableDurableReceipt expected, TableDurableReceipt actual)
        {
            Assert.Equal(expected.AccountId, actual.AccountId);
            Assert.Equal(expected.ScopeId, actual.ScopeId);
            Assert.Equal(expected.RequestId, actual.RequestId);
            Assert.Equal(expected.Api, actual.Api);
            Assert.Equal(expected.PayloadSha256, actual.PayloadSha256);
            Assert.Equal(expected.Detail, actual.Detail);
            Assert.Equal(expected.CreatedAtUtcMs, actual.CreatedAtUtcMs);
            Assert.Equal(expected.ExpiresAtUtcMs, actual.ExpiresAtUtcMs);
            AssertSameReceipt(expected.Receipt, actual.Receipt);
        }

        private static void AssertSameReceipt(Receipt expected, Receipt actual)
        {
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.ReasonCode, actual.ReasonCode);
            Assert.Equal(expected.EvaluatedStateVersion, actual.EvaluatedStateVersion);
            Assert.Equal(expected.CommandVersion, actual.CommandVersion);
            Assert.Equal(expected.EventSeq, actual.EventSeq);
            Assert.Equal(expected.CommittedAtMs, actual.CommittedAtMs);
            Assert.Equal(expected.CanonicalPayloadDigest, actual.CanonicalPayloadDigest);
        }

        private static ValueTask<TableRecoveryResult> RecoverAsync(
            P52Fixture fixture,
            string? matchId,
            bool requireCheatLedger,
            bool requireCommittedFacts)
        {
            var service = new TableRecoveryService(
                fixture.Store,
                fixture.Store,
                fixture.Store,
                fixture.Clock);
            return service.RecoverAsync(
                P52Fixture.Room,
                new TableRecoveryOptions(
                    P52Fixture.Owner,
                    "r1-recovery-boot",
                    1,
                    TimeSpan.FromMinutes(10),
                    0L,
                    matchId,
                    requireCheatLedger,
                    requireCommittedFacts),
                TestContext.Current.CancellationToken);
        }

        private static async Task SetShortLeaseAsync()
        {
            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_lease SET lease_expires_at = clock_timestamp() + interval '1 second' WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));
        }

        private static async Task InstallDelayTriggerAsync(string table, string function)
        {
            await PostgresTableTestEnvironment.ExecuteAsync($@"
CREATE OR REPLACE FUNCTION table_store.{function}() RETURNS trigger LANGUAGE plpgsql AS
$$ BEGIN PERFORM pg_sleep(2); RETURN NEW; END $$;
DROP TRIGGER IF EXISTS p52_delay ON table_store.{table};
CREATE TRIGGER p52_delay BEFORE INSERT ON table_store.{table}
FOR EACH ROW EXECUTE FUNCTION table_store.{function}();",
                TestContext.Current.CancellationToken);
        }

        private static async Task DropDelayTriggerAsync(string table, string function)
        {
            await PostgresTableTestEnvironment.ExecuteAsync($@"
DROP TRIGGER IF EXISTS p52_delay ON table_store.{table};
DROP FUNCTION IF EXISTS table_store.{function}();",
                TestContext.Current.CancellationToken);
        }

        private static async Task AssertNoRowsAsync()
        {
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT state_version FROM table_store.table_state",
                TestContext.Current.CancellationToken));
        }
    }

    internal sealed class CommitAckDropProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        private readonly int _backendPort;
        private TcpClient? _client;
        private TcpClient? _backend;
        private int _commit;
        private bool _ackWithheld;

        internal bool CommitSeen => Volatile.Read(ref _commit) == 1;

        internal bool AckWithheld => _ackWithheld;

        internal int Port { get; }

        internal Task Done { get; }

        internal CommitAckDropProxy(int backendPort)
        {
            _backendPort = backendPort;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Done = RunAsync();
        }

        private async Task RunAsync()
        {
            _client = await _listener.AcceptTcpClientAsync(_timeout.Token);
            _backend = new TcpClient();
            await _backend.ConnectAsync(IPAddress.Loopback, _backendPort, _timeout.Token);
            NetworkStream front = _client.GetStream();
            NetworkStream back = _backend.GetStream();
            Task upload = Task.Run(async () =>
            {
                byte[] bytes = new byte[65536];
                var pending = new List<byte>();
                bool startup = true;
                try
                {
                    while (true)
                    {
                        int read = await front.ReadAsync(bytes, _timeout.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        pending.AddRange(bytes.Take(read));
                        while (pending.Count >= (startup ? 4 : 5))
                        {
                            byte[] packet = pending.ToArray();
                            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
                                packet.AsSpan(startup ? 0 : 1, 4)) + (startup ? 0 : 1);
                            if (length < 4 || length > 2_000_000)
                            {
                                throw new InvalidOperationException("Bad frontend frame.");
                            }

                            if (packet.Length < length)
                            {
                                break;
                            }

                            if (!startup && (packet[0] == (byte)'Q' || packet[0] == (byte)'P'))
                            {
                                int offset = 5;
                                if (packet[0] == (byte)'P')
                                {
                                    while (packet[offset++] != 0)
                                    {
                                    }
                                }

                                int end = offset;
                                while (end < length && packet[end] != 0)
                                {
                                    end++;
                                }

                                string query = Encoding.UTF8.GetString(packet, offset, end - offset);
                                if (query.Trim().TrimEnd(';').Equals("COMMIT", StringComparison.OrdinalIgnoreCase))
                                {
                                    Interlocked.Exchange(ref _commit, 1);
                                }
                            }

                            pending.RemoveRange(0, length);
                            startup = false;
                        }

                        await back.WriteAsync(bytes.AsMemory(0, read), _timeout.Token);
                    }
                }
                catch (Exception) when (CommitSeen)
                {
                }
            });

            byte[] buffer = new byte[65536];
            try
            {
                while (true)
                {
                    int read = await back.ReadAsync(buffer, _timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    if (CommitSeen)
                    {
                        _ackWithheld = true;
                        for (int i = 0; i < 100; i++)
                        {
                            long count = await PostgresTableTestEnvironment.CountAsync(
                                "SELECT count(*) FROM table_store.request_receipt WHERE request_id = 'r1-lost-ack'",
                                TestContext.Current.CancellationToken);
                            if (count == 1)
                            {
                                break;
                            }

                            await Task.Delay(20, _timeout.Token);
                        }

                        _client.Dispose();
                        _backend.Dispose();
                        break;
                    }

                    await front.WriteAsync(buffer.AsMemory(0, read), _timeout.Token);
                }
            }
            finally
            {
                _client.Dispose();
                _backend.Dispose();
            }

            await upload;
        }

        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            _listener.Stop();
            _client?.Dispose();
            _backend?.Dispose();
            try
            {
                await Done;
            }
            catch
            {
            }

            _timeout.Dispose();
        }
    }
}

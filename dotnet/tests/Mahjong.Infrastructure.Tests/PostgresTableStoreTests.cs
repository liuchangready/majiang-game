using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    [Collection(PostgresTableCollection.Name)]
    public sealed class PostgresTableStoreTests
    {
        [Fact]
        public async Task ActorCommit_RoundTripsRealEventsReceiptAndColdRecovery()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            var sink = new RecordingPublicationSink();
            await using TableActor actor = fixture.CreateActor(sink);
            TableCommandEnvelope envelope = fixture.DiscardEnvelope("req-actor-1");

            TableOperationResult result = await actor.SubmitAsync(
                envelope,
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.True(result.Events.Count > 0);
            Assert.Equal(new StateVersion(1), result.CommandVersion);
            Assert.Single(sink.Publications);

            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.NotNull(state);
            Assert.Equal(new StateVersion(1), state!.StateVersion);
            Assert.Equal(new EventSeq((ulong)result.Events.Count), state.LastEventSeq);
            Assert.Equal(result.CommandVersion!.Value.Value, state.StateVersion.Value);

            TableDurableReceipt? receipt = await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId("req-actor-1"),
                TestContext.Current.CancellationToken);
            Assert.NotNull(receipt);
            Assert.Equal(ReceiptStatus.Applied, receipt!.Receipt.Status);
            Assert.Equal(result.LastEventSeq, receipt.Receipt.EventSeq);

            ReceiptLookupResult lookup = await actor.QueryReceiptAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId("req-actor-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ReceiptLookupStatus.Found, lookup.Status);

            IReadOnlyList<TableEventRecord> events = await fixture.Store.LoadEventsAfterAsync(
                P52Fixture.Room,
                new EventSeq(0),
                TestContext.Current.CancellationToken);
            Assert.Equal(result.Events.Count, events.Count);
            Assert.Equal(Enumerable.Range(0, events.Count), events.Select(e => e.Ordinal));

            IReadOnlyList<TableCommandJournalRecord> commands = await fixture.Store.LoadCommandsAfterAsync(
                P52Fixture.Room,
                new StateVersion(0),
                TestContext.Current.CancellationToken);
            Assert.Single(commands);

            var recovery = new TableRecoveryService(
                fixture.Store,
                fixture.Store,
                fixture.Store,
                fixture.Clock);
            TableRecoveryResult recovered = await recovery.RecoverAsync(
                P52Fixture.Room,
                new TableRecoveryOptions(
                    P52Fixture.Owner,
                    "p52-recovery-boot",
                    1,
                    TimeSpan.FromMinutes(10),
                    0L,
                    null,
                    requireCheatLedger: false,
                    requireCommittedFacts: false),
                TestContext.Current.CancellationToken);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(new StateVersion(1), recovered.Report!.FinalStateVersion);
            Assert.Equal(1, recovered.Report.ReplayedCommands);
            Assert.Equal(events.Count, recovered.Report.ReplayedEvents);
            Assert.Equal(state.StateHash, recovered.Report.FinalStateHash);
            Assert.Equal(state.StateFingerprint, recovered.Report.FinalFingerprint);
            Assert.Equal(new OwnerEpoch(2), recovered.Report.CurrentOwnerEpoch);

            IReadOnlyList<TableRecoveryAuditRecord> audits = await fixture.Store.LoadRecoveryAuditsAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.NotEmpty(audits);
        }

        [Fact]
        public async Task ConcurrentDuplicateSubmission_CommitsOnceAndReplaysTheOther()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            await using TableActor first = fixture.CreateActor();
            await using TableActor second = fixture.CreateActor();
            TableCommandEnvelope envelope = fixture.DiscardEnvelope("req-concurrent-duplicate");

            TableOperationResult[] results = await Task.WhenAll(
                first.SubmitAsync(envelope, TestContext.Current.CancellationToken).AsTask(),
                second.SubmitAsync(envelope, TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(1, results.Count(r => r.Status == TableOperationStatus.Accepted));
            Assert.Equal(1, results.Count(r => r.Status == TableOperationStatus.Replayed));

            long stateVersion = await PostgresTableTestEnvironment.CountAsync(
                "SELECT state_version FROM table_store.table_state WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));
            Assert.Equal(1L, stateVersion);

            long receipts = await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt WHERE request_id = 'req-concurrent-duplicate'",
                TestContext.Current.CancellationToken);
            Assert.Equal(1L, receipts);

            long events = await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));
            Assert.Equal((long)results.Single(r => r.Status == TableOperationStatus.Accepted).Events.Count, events);
        }

        [Fact]
        public async Task SameRequestDifferentPayload_IsConflictWithoutNewSideEffects()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            await using TableActor actor = fixture.CreateActor();
            TableOperationResult accepted = await actor.SubmitAsync(
                fixture.DiscardEnvelope("req-conflict", payload: "original-business-bytes"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);

            long beforeEvents = await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken);
            TableOperationResult conflict = await actor.SubmitAsync(
                fixture.DiscardEnvelope("req-conflict", payload: "different-business-bytes", expected: new StateVersion(1)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Conflict, conflict.Status);

            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), state!.StateVersion);
            Assert.Equal(beforeEvents, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task BusinessRejection_WritesOnlyReceiptAndVersionDoesNotMove()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            await using TableActor actor = fixture.CreateActor();
            RoundCommand invalid = InvalidDiscard(fixture.Round);
            TableOperationResult rejected = await actor.SubmitAsync(
                fixture.DiscardEnvelope("req-rejected", command: invalid),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.NotEqual(string.Empty, rejected.ReasonCode);

            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(0), state!.StateVersion);
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));

            TableDurableReceipt? receipt = await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId("req-rejected"),
                TestContext.Current.CancellationToken);
            Assert.NotNull(receipt);
            Assert.Equal(ReceiptStatus.Rejected, receipt!.Receipt.Status);
            Assert.Null(receipt.Receipt.CommandVersion);
            Assert.Null(receipt.Receipt.EventSeq);
            Assert.Equal(0UL, receipt.Receipt.EvaluatedStateVersion.Value);

            TableOperationResult replay = await actor.SubmitAsync(
                fixture.DiscardEnvelope("req-rejected", command: invalid),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ExpiredScope_WithoutReceipt_HasZeroSideEffects()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                scopeIssuedUtcMs: now - 5_000L,
                scopeExpiresUtcMs: now - 1_000L);
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-expired-scope");

            TableDurableCommitResult result = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.ScopeRejected, result.Disposition);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, result.ReasonCode);
            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task RevokedScope_ExistingReceiptStillReadable_NewCommandRejected()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-before-revoke");
            Assert.True((await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken)).IsCommitted);
            Assert.True(await fixture.Store.RevokeScopeAsync(
                fixture.Scope.ScopeId,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TestContext.Current.CancellationToken));

            TableDurableReceipt? existing = await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId("req-before-revoke"),
                TestContext.Current.CancellationToken);
            Assert.NotNull(existing);

            TableDurableCommitResult replay = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Replayed, replay.Disposition);
            Assert.Equal(existing!.Receipt.CommandVersion, replay.Receipt!.Receipt.CommandVersion);

            DirectCommitCandidate after = fixture.BuildCommit(
                candidate.NextRound,
                new StateVersion(1),
                candidate.NextLastSeq,
                "req-after-revoke");
            TableDurableCommitResult rejected = await fixture.Store.CommitAsync(
                after.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.ScopeRejected, rejected.Disposition);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT state_version FROM table_store.table_state",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task RevocationAtCommitBoundary_BlocksThenRejectsWithoutSideEffects()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-revoke-race");

            await using var blocker = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await blocker.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlTransaction hold = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT scope_id FROM table_store.request_scope WHERE scope_id = @scope FOR UPDATE",
                blocker,
                hold))
            {
                lockCommand.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                Assert.Equal(fixture.Scope.ScopeId.Value, await lockCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            Task<TableDurableCommitResult> commit = fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken).AsTask();
            await WaitForRowLockWaitAsync();
            Assert.False(commit.IsCompleted);

            await using (var revoke = new NpgsqlCommand(
                "UPDATE table_store.request_scope SET revoked_at_utc_ms = @now WHERE scope_id = @scope",
                blocker,
                hold))
            {
                revoke.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                revoke.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                Assert.Equal(1, await revoke.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            await hold.CommitAsync(TestContext.Current.CancellationToken);
            TableDurableCommitResult result = await commit;
            Assert.Equal(TableCommitDisposition.ScopeRejected, result.Disposition);
            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task ExpiryAtCommitBoundary_WaitsForLockThenUsesDatabaseClockAndRejects()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                scopeExpiresUtcMs: now + 700L);
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-expiry-race");

            await using var blocker = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await blocker.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlTransaction hold = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT scope_id FROM table_store.request_scope WHERE scope_id = @scope FOR UPDATE",
                blocker,
                hold))
            {
                lockCommand.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                Assert.Equal(fixture.Scope.ScopeId.Value, await lockCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            Task<TableDurableCommitResult> commit = fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken).AsTask();
            await WaitForRowLockWaitAsync();
            Assert.False(commit.IsCompleted);

            long waitUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000L;
            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < waitUntil)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            await hold.CommitAsync(TestContext.Current.CancellationToken);
            TableDurableCommitResult result = await commit;
            Assert.Equal(TableCommitDisposition.ScopeRejected, result.Disposition);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, result.ReasonCode);
            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task LeaseDualInstanceAndOldOwnerFencing()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            TableLeaseAcquireResult held = await fixture.Store.AcquireAsync(
                P52Fixture.Room,
                "p52-owner-2",
                TimeSpan.FromMinutes(10),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableLeaseAcquireStatus.Held, held.Status);

            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_lease SET lease_expires_at = clock_timestamp() - interval '1 second' WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableLeaseAcquireResult acquired = await fixture.Store.AcquireAsync(
                P52Fixture.Room,
                "p52-owner-2",
                TimeSpan.FromMinutes(10),
                TestContext.Current.CancellationToken);
            Assert.True(acquired.Acquired, acquired.Detail);
            Assert.Equal(new OwnerEpoch(2), acquired.Lease!.OwnerEpoch);

            DirectCommitCandidate old = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-old-owner");
            TableDurableCommitResult result = await fixture.Store.CommitAsync(
                old.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.StaleOwner, result.Disposition);

            TableLeaseRenewResult renew = await fixture.Store.RenewAsync(
                P52Fixture.Room,
                P52Fixture.Owner,
                fixture.Lease.OwnerEpoch,
                TimeSpan.FromMinutes(10),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableLeaseRenewStatus.StaleOwner, renew.Status);
            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task StaleStateFenceRejectsWithoutReceiptOrEvent()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate first = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-first");
            Assert.True((await fixture.Store.CommitAsync(
                first.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            DirectCommitCandidate stale = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-stale-state");
            TableDurableCommitResult result = await fixture.Store.CommitAsync(
                stale.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.StaleState, result.Disposition);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
            Assert.Equal((long)first.EventCount, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task StoreFailureBeforeCommit_DoesNotPublishThroughActor()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            var sink = new RecordingPublicationSink();
            await using TableActor actor = fixture.CreateActor(sink);
            await PostgresTableTestEnvironment.ExecuteAsync(
                "UPDATE table_store.table_lease SET lease_expires_at = clock_timestamp() - interval '1 second' WHERE room_id = @room",
                TestContext.Current.CancellationToken,
                ("room", P52Fixture.Room));

            TableOperationResult result = await actor.SubmitAsync(
                fixture.DiscardEnvelope("req-expired-lease", expected: new StateVersion(0)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, result.ReasonCode);
            Assert.DoesNotContain(sink.Publications, p => p.Kind == TablePublicationKind.Applied);
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
            Assert.Equal(0L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task TerminalOutbox_IsAtomicAndConsumerIsIdempotent()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-outbox",
                terminal: true,
                outboxMatchId: "match-outbox",
                outboxResultVersion: 3,
                outboxPayload: "settlement-final-payload");
            TableDurableCommitResult committed = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.True(committed.IsCommitted, committed.Detail);

            TableSettlementOutboxRecord? record = await fixture.Store.TryGetAsync(
                "match-outbox",
                3,
                TestContext.Current.CancellationToken);
            Assert.NotNull(record);
            Assert.Null(record!.PublishedAtUtcMs);

            IReadOnlyList<TableSettlementOutboxRecord> unpublished = await fixture.Store.ListUnpublishedAsync(
                TestContext.Current.CancellationToken);
            Assert.Single(unpublished);

            ulong firstPublished = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.True(await fixture.Store.MarkPublishedAsync(
                "match-outbox",
                3,
                firstPublished,
                TestContext.Current.CancellationToken));
            TableSettlementOutboxRecord? published = await fixture.Store.TryGetAsync(
                "match-outbox",
                3,
                TestContext.Current.CancellationToken);
            Assert.Equal(firstPublished, published!.PublishedAtUtcMs);

            Assert.True(await fixture.Store.MarkPublishedAsync(
                "match-outbox",
                3,
                firstPublished + 10_000UL,
                TestContext.Current.CancellationToken));
            TableSettlementOutboxRecord? replayed = await fixture.Store.TryGetAsync(
                "match-outbox",
                3,
                TestContext.Current.CancellationToken);
            Assert.Equal(firstPublished, replayed!.PublishedAtUtcMs);
            Assert.Empty(await fixture.Store.ListUnpublishedAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task OutboxPayloadConflict_RollsBackEventReceiptAndState()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate first = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-outbox-first",
                terminal: true,
                outboxMatchId: "match-outbox-conflict",
                outboxResultVersion: 1,
                outboxPayload: "payload-one");
            Assert.True((await fixture.Store.CommitAsync(
                first.Request,
                TestContext.Current.CancellationToken)).IsCommitted);

            DirectCommitCandidate conflicting = fixture.BuildCommit(
                first.NextRound,
                new StateVersion(1),
                first.NextLastSeq,
                "req-outbox-second",
                terminal: true,
                outboxMatchId: "match-outbox-conflict",
                outboxResultVersion: 1,
                outboxPayload: "payload-two",
                timingVersion: 3L);
            TableDurableCommitResult result = await fixture.Store.CommitAsync(
                conflicting.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Failed, result.Disposition);
            Assert.Equal(TableRuntimeReasonCode.OutboxConflict, result.ReasonCode);

            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), state!.StateVersion);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.settlement_outbox",
                TestContext.Current.CancellationToken));
            Assert.Equal((long)first.EventCount, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
        }

        private static RoundCommand InvalidDiscard(RoundStateMachine round)
        {
            int other = (round.CurrentSeat + 1) % 4;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, other)[0];
            return RoundCommand.Discard(round.RoundId, round.CurrentSeat, tile);
        }

        private static async Task AssertNoSideEffectsAsync()
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

        private static async Task WaitForRowLockWaitAsync()
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                long waiting = await PostgresTableTestEnvironment.CountAsync(
                    @"SELECT count(*) FROM pg_stat_activity
                       WHERE application_name = 'mahjong-table-p52'
                         AND wait_event_type = 'Lock'
                         AND query LIKE '%request_scope%'",
                    TestContext.Current.CancellationToken);
                if (waiting > 0)
                {
                    return;
                }

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            throw new InvalidOperationException("The committing transaction never blocked on the scope row lock.");
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.TableRuntime;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    [Collection(PostgresTableCollection.Name)]
    public sealed class PostgresFaultTests
    {
        [Fact]
        public async Task DatabaseSlow_CommandTimeoutFailsWithoutSideEffects()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync(commandTimeoutSeconds: 1);
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-slow-db");

            await using var blocker = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await blocker.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlTransaction hold = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@room, 0))",
                blocker,
                hold))
            {
                lockCommand.Parameters.AddWithValue("room", P52Fixture.Room);
                await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            Task<TableDurableCommitResult> commit = fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken).AsTask();
            TableDurableCommitResult result = await commit.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Failed, result.Disposition);
            await hold.RollbackAsync(TestContext.Current.CancellationToken);

            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task DatabaseDisconnect_DuringLockWaitFailsWithoutSideEffects()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-disconnect");

            await using var blocker = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await blocker.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlTransaction hold = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@room, 0))",
                blocker,
                hold))
            {
                lockCommand.Parameters.AddWithValue("room", P52Fixture.Room);
                await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            Task<TableDurableCommitResult> commit = fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken).AsTask();
            await WaitForAdvisoryLockWaitAsync();
            long terminated = await PostgresTableTestEnvironment.CountAsync(
                @"SELECT count(*) FROM (
                    SELECT pg_terminate_backend(pid)
                      FROM pg_stat_activity
                     WHERE application_name = 'mahjong-table-p52'
                       AND wait_event_type = 'Lock'
                       AND query LIKE '%pg_advisory_xact_lock%'
                       AND pid <> pg_backend_pid()
                  ) terminated",
                TestContext.Current.CancellationToken);
            Assert.True(terminated > 0, "Expected the committing backend to be waiting on the room lock.");
            TableDurableCommitResult result = await commit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Failed, result.Disposition);
            await hold.RollbackAsync(TestContext.Current.CancellationToken);

            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task LostCommitResponse_IsOutcomeUnknownAndReceiptReconcilesExactlyOnce()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-lost-response");
            fixture.Store.AfterCommitBeforeReturnForTest = () =>
                throw new InvalidOperationException("simulated lost COMMIT response");
            TableDurableCommitResult unknown = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            fixture.Store.AfterCommitBeforeReturnForTest = null;

            Assert.Equal(TableCommitDisposition.OutcomeUnknown, unknown.Disposition);
            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), state!.StateVersion);
            Assert.Equal((long)candidate.EventCount, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
            TableDurableReceipt? first = await fixture.Store.TryGetAsync(
                P52Fixture.Account,
                fixture.Scope.ScopeId,
                new RequestId("req-lost-response"),
                TestContext.Current.CancellationToken);
            Assert.NotNull(first);

            TableDurableCommitResult replay = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.Replayed, replay.Disposition);
            Assert.Equal(first!.Receipt.CommandVersion, replay.Receipt!.Receipt.CommandVersion);
            Assert.Equal(new StateVersion(1), (await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken))!.StateVersion);
            Assert.Equal(1L, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt",
                TestContext.Current.CancellationToken));
        }

        private static async Task WaitForAdvisoryLockWaitAsync()
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                long waiting = await PostgresTableTestEnvironment.CountAsync(
                    @"SELECT count(*) FROM pg_stat_activity
                       WHERE application_name = 'mahjong-table-p52'
                         AND wait_event_type = 'Lock'
                         AND query LIKE '%pg_advisory_xact_lock%'",
                    TestContext.Current.CancellationToken);
                if (waiting > 0)
                {
                    return;
                }

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            throw new InvalidOperationException("The commit never blocked on the room advisory lock.");
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
    }
}

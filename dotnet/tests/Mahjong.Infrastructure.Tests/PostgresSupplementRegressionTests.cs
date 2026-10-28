using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.TableRuntime;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    /// <summary>
    /// Deterministic regressions for the PostgreSQL supplement failures:
    /// a slow fixture preparation must not consume the scope lifetime, and the
    /// expiry/revocation row-lock boundaries must keep using the database clock
    /// with zero side effects.
    /// </summary>
    [Collection(PostgresTableCollection.Name)]
    public sealed class PostgresSupplementRegressionTests
    {
        [Fact]
        public async Task DelayedScopePreparation_EstablishesScopeAfterPreparationAndCommits()
        {
            TimeSpan preparation = TimeSpan.FromSeconds(2);
            TimeSpan afterIssue = TimeSpan.FromMilliseconds(1_100);
            var elapsed = Stopwatch.StartNew();
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                scopePreparationDelay: preparation,
                afterScopeIssueDelay: afterIssue);
            elapsed.Stop();

            Assert.True(
                fixture.ScopePreparationElapsed >= preparation,
                "The fixture did not actually delay preparation: preparationMs=" +
                ((long)fixture.ScopePreparationElapsed.TotalMilliseconds));
            Assert.True(
                elapsed.Elapsed >= preparation + afterIssue,
                "The post-issue scheduling delay was not exercised: elapsedMs=" +
                ((long)elapsed.Elapsed.TotalMilliseconds));

            // Causal order, not a speed bound: scope issue must happen after the
            // preparation completion instant. A mutant that captures the issue
            // time before preparation necessarily violates this assertion.
            Assert.True(
                fixture.Scope.IssuedAtUtcMs >= fixture.ScopePreparationCompletedAtUtcMs,
                "The scope was issued before preparation completed: issued=" +
                fixture.Scope.IssuedAtUtcMs + "; prepared=" +
                fixture.ScopePreparationCompletedAtUtcMs);

            // The post-issue delay makes the scope older than one second. With a
            // valid default lifetime the commit must still be accepted; this is
            // deliberately not an upper-bound machine-speed assertion.
            long wallNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.True(
                wallNow - checked((long)fixture.Scope.IssuedAtUtcMs) >= 1_000L,
                "The post-issue delay did not actually elapse after scope issue.");
            Assert.True(
                fixture.Scope.ExpiresAtUtcMs > fixture.Scope.IssuedAtUtcMs,
                "The default fixture scope is not valid.");

            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-delayed-preparation");
            TableDurableCommitResult result = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.True(
                result.IsCommitted,
                "A scope issued after a slow preparation was rejected: disposition=" +
                result.Disposition + "; reason=" + result.ReasonCode + "; detail=" + result.Detail);

            TableStateRow? state = await fixture.Store.LoadStateAsync(
                P52Fixture.Room,
                TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), state!.StateVersion);
            Assert.Equal((long)candidate.EventCount, await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event",
                TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ExpiryAtCommitBoundary_AfterDelayedPreparation_RejectsWithoutSideEffects()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                scopePreparationDelay: TimeSpan.FromMilliseconds(900));
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-delayed-expiry");

            TableDurableCommitResult result =
                await P52ScopeBoundary.CommitAfterExpiryCrossesTransactionStartAsync(
                    fixture,
                    candidate,
                    TestContext.Current.CancellationToken);
            Assert.Equal(TableCommitDisposition.ScopeRejected, result.Disposition);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, result.ReasonCode);
            await AssertNoSideEffectsAsync();
        }

        [Fact]
        public async Task RevocationAtCommitBoundary_AfterDelayedPreparation_RejectsWithoutSideEffects()
        {
            await using P52Fixture fixture = await P52Fixture.CreateAsync(
                scopePreparationDelay: TimeSpan.FromMilliseconds(900));
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                "req-delayed-revoke");

            TableDurableCommitResult result = await CommitWhileScopeLockedAsync(
                fixture,
                candidate,
                @"UPDATE table_store.request_scope
                     SET revoked_at_utc_ms = (EXTRACT(EPOCH FROM clock_timestamp()) * 1000)::bigint
                   WHERE scope_id = @scope");
            Assert.Equal(TableCommitDisposition.ScopeRejected, result.Disposition);
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, result.ReasonCode);
            await AssertNoSideEffectsAsync();
        }

        private static async Task<TableDurableCommitResult> CommitWhileScopeLockedAsync(
            P52Fixture fixture,
            DirectCommitCandidate candidate,
            string mutationSql)
        {
            await using var blocker = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await blocker.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlTransaction hold = await blocker.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT scope_id FROM table_store.request_scope WHERE scope_id = @scope FOR UPDATE",
                blocker,
                hold))
            {
                lockCommand.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                Assert.Equal(
                    fixture.Scope.ScopeId.Value,
                    await lockCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            Task<TableDurableCommitResult> commit = fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken).AsTask();
            await WaitForRowLockWaitAsync();
            Assert.False(commit.IsCompleted);

            await using (var mutation = new NpgsqlCommand(mutationSql, blocker, hold))
            {
                mutation.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                Assert.Equal(
                    1,
                    await mutation.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            Assert.False(commit.IsCompleted);
            await hold.CommitAsync(TestContext.Current.CancellationToken);
            return await commit;
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

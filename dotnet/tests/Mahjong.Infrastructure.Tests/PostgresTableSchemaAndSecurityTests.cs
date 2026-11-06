using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    [Collection(PostgresTableCollection.Name)]
    public sealed class PostgresTableSchemaAndSecurityTests
    {
        [Fact]
        public async Task MigrationTwice_CreatesRequiredTablesConstraintsIndexesAndLeastPrivilegeRole()
        {
            await PostgresTableTestEnvironment.EnsureSchemaAsync(TestContext.Current.CancellationToken);
            await PostgresTableTestEnvironment.EnsureSchemaAsync(TestContext.Current.CancellationToken);

            string[] tables = await PostgresTableTestEnvironment.QueryStringsAsync(
                @"SELECT table_name
                    FROM information_schema.tables
                   WHERE table_schema = 'table_store'
                   ORDER BY table_name",
                TestContext.Current.CancellationToken);
            string[] required =
            {
                "request_receipt", "request_scope", "schema_migration", "settlement_outbox",
                "table_checkpoint", "table_command_journal", "table_committed_facts",
                "table_control_journal", "table_event", "table_lease", "table_recovery_audit",
                "table_state", "table_timing_journal"
            };
            Assert.Equal(required, tables);

            long checkpointColumns = await PostgresTableTestEnvironment.CountAsync(
                @"SELECT count(*) FROM information_schema.columns
                   WHERE table_schema = 'table_store' AND table_name = 'table_checkpoint'",
                TestContext.Current.CancellationToken);
            Assert.True(checkpointColumns >= 20, "Checkpoint table lacks required payload/fence columns.");

            long uniqueOrdinal = await PostgresTableTestEnvironment.CountAsync(
                @"SELECT count(*) FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                   WHERE n.nspname = 'table_store' AND t.relname = 'table_event'
                     AND c.contype = 'u'",
                TestContext.Current.CancellationToken);
            Assert.True(uniqueOrdinal >= 1, "The command_version/ordinal uniqueness constraint is missing.");

            long indexes = await PostgresTableTestEnvironment.CountAsync(
                @"SELECT count(*) FROM pg_indexes WHERE schemaname = 'table_store'",
                TestContext.Current.CancellationToken);
            Assert.True(indexes >= 8, "Expected durable lookup/expiry indexes were not created.");

            bool superuser = Convert.ToBoolean(await PostgresTableTestEnvironment.ScalarAsync(
                "SELECT rolsuper FROM pg_roles WHERE rolname = 'mahjong_table_runtime'",
                TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(superuser);

            bool canCreate = Convert.ToBoolean(await PostgresTableTestEnvironment.ScalarAsync(
                "SELECT has_schema_privilege('mahjong_table_runtime', 'table_store', 'CREATE')",
                TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(canCreate);

            bool canSelect = Convert.ToBoolean(await PostgresTableTestEnvironment.ScalarAsync(
                "SELECT has_table_privilege('mahjong_table_runtime', 'table_store.table_event', 'SELECT')",
                TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(canSelect);

            bool canTruncate = Convert.ToBoolean(await PostgresTableTestEnvironment.ScalarAsync(
                "SELECT has_table_privilege('mahjong_table_runtime', 'table_store.table_event', 'TRUNCATE')",
                TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(canTruncate);

            await using var runtime = new NpgsqlConnection(PostgresTableTestEnvironment.RuntimeConnectionString);
            await runtime.OpenAsync(TestContext.Current.CancellationToken);
            await using var ddl = new NpgsqlCommand(
                "CREATE TABLE table_store.p52_must_not_exist (x integer)",
                runtime);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
                () => ddl.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal("42501", denied.SqlState);
        }

        [Fact]
        public async Task PayloadKeyIsRequiredAndDatabaseBytesDoNotContainPlaintext()
        {
            string? previous = Environment.GetEnvironmentVariable(PostgresTableOptions.PayloadKeyEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(PostgresTableOptions.PayloadKeyEnvironmentVariable, null);
                Assert.Throws<PostgresTableConfigurationException>(() =>
                    new PostgresTableOptions(PostgresTableTestEnvironment.RuntimeConnectionString, null!));
                Assert.Throws<PostgresTableConfigurationException>(() =>
                    new PostgresTableOptions(PostgresTableTestEnvironment.RuntimeConnectionString, "not-base64"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(PostgresTableOptions.PayloadKeyEnvironmentVariable, previous);
            }

            const string marker = "P52-PLAINTEXT-MARKER-9e6f51";
            await using P52Fixture fixture = await P52Fixture.CreateAsync();
            DirectCommitCandidate candidate = fixture.BuildCommit(
                fixture.Round,
                new Mahjong.Contracts.StateVersion(0),
                new Mahjong.Contracts.EventSeq(0),
                "req-encrypted",
                terminal: true,
                outboxMatchId: "match-encrypted",
                outboxResultVersion: 7,
                outboxPayload: marker,
                committedFacts: marker);
            Mahjong.TableRuntime.TableDurableCommitResult committed = await fixture.Store.CommitAsync(
                candidate.Request,
                TestContext.Current.CancellationToken);
            Assert.True(committed.IsCommitted, committed.Detail);

            byte[] markerBytes = System.Text.Encoding.UTF8.GetBytes(marker);
            long factsPlain = await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_committed_facts WHERE position(@marker in payload_blob) > 0",
                TestContext.Current.CancellationToken,
                ("marker", markerBytes));
            long outboxPlain = await PostgresTableTestEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.settlement_outbox WHERE position(@marker in payload_blob) > 0",
                TestContext.Current.CancellationToken,
                ("marker", markerBytes));
            Assert.Equal(0L, factsPlain);
            Assert.Equal(0L, outboxPlain);

            Mahjong.TableRuntime.TableCommittedFactsSnapshot? facts =
                await fixture.Store.LoadCommittedFactsAsync(P52Fixture.Room, TestContext.Current.CancellationToken);
            Assert.NotNull(facts);
            Assert.Equal(marker, System.Text.Encoding.UTF8.GetString(facts!.Payload));

            Mahjong.TableRuntime.TableSettlementOutboxRecord? outbox =
                await fixture.Store.TryGetAsync("match-encrypted", 7, TestContext.Current.CancellationToken);
            Assert.NotNull(outbox);
            Assert.Equal(marker, System.Text.Encoding.UTF8.GetString(outbox!.Payload));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Infrastructure;
using Npgsql;
using Xunit;

namespace Mahjong.TableService.DurableHost.Tests
{
    [CollectionDefinition(DurableHostCollection.Name, DisableParallelization = true)]
    public sealed class DurableHostCollection
    {
        public const string Name = "durable-table-host-p54b";
    }

    /// <summary>
    /// Strict real-PostgreSQL environment for the P5.4b-A durable host tests. A missing
    /// database, key or empty probe database FAILS the test run — it never becomes a skip
    /// and never a 0-test green. Only a loopback server on a non-default port with a
    /// mahjong-prefixed database is accepted, exactly like the accepted P5.2 guard.
    /// </summary>
    internal static class DurableTableHostEnvironment
    {
        internal const string RuntimeVariable = "MAHJONG_P54B_TEST_PG";

        internal const string AdminVariable = "MAHJONG_P54B_TEST_PG_ADMIN";

        internal const string EmptyVariable = "MAHJONG_P54B_TEST_PG_EMPTY";

        internal const string KeyVariable = "MAHJONG_P54B_TEST_PG_KEY";

        internal const string EvidenceVariable = "MAHJONG_P54B_TEST_PG_EVIDENCE";

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static bool _schemaReady;

        internal static string RuntimeConnectionString => Require(RuntimeVariable, "mahjong_p54b");

        internal static string AdminConnectionString => Require(AdminVariable, "mahjong_p54b");

        internal static string EmptyDatabaseConnectionString => Require(EmptyVariable, "mahjong_p54b");

        internal static string PayloadKey
        {
            get
            {
                string? value = Environment.GetEnvironmentVariable(KeyVariable);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException(
                        "BLOCKED: " + KeyVariable + " is required for the real PostgreSQL P5.4b-A durable host tests.");
                }

                return value;
            }
        }

        internal static async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_schemaReady)
                {
                    return;
                }

                // Two identical deployment runs prove the migration is idempotent.
                await PostgresTableMigrator.ApplyAsync(AdminConnectionString, cancellationToken).ConfigureAwait(false);
                await PostgresTableMigrator.ApplyAsync(AdminConnectionString, cancellationToken).ConfigureAwait(false);
                Assert.True(
                    await PostgresTableMigrator.IsReadyAsync(AdminConnectionString, cancellationToken)
                        .ConfigureAwait(false),
                    "The table_store schema is not ready after two migrations.");
                _schemaReady = true;
            }
            finally
            {
                Gate.Release();
            }
        }

        internal static async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
TRUNCATE table_store.table_recovery_audit,
         table_store.settlement_outbox,
         table_store.table_committed_facts,
         table_store.table_control_journal,
         table_store.table_command_journal,
         table_store.table_timing_journal,
         table_store.table_checkpoint,
         table_store.table_event,
         table_store.request_receipt,
         table_store.request_scope,
         table_store.table_lease,
         table_store.table_state
CASCADE";
            await ExecuteAsync(sql).ConfigureAwait(false);
        }

        internal static async Task<long> CountAsync(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            object? value = await ScalarAsync(sql, parameters).ConfigureAwait(false);
            return value == null || value is DBNull
                ? 0L
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        internal static async Task<object?> ScalarAsync(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteScalarAsync().ConfigureAwait(false);
        }

        internal static async Task<int> ExecuteAsync(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        internal static void AppendEvidence(string name, string content)
        {
            string? root = Environment.GetEnvironmentVariable(EvidenceVariable);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(root);
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, name), content);
        }

        private static string Require(string variable, string databasePrefix)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "BLOCKED: " + variable + " is required for the real PostgreSQL P5.4b-A durable host tests. " +
                    "This assembly deliberately has no skipped/missing-PG fake-green path.");
            }

            Validate(value, variable, databasePrefix);
            return value;
        }

        private static void Validate(string connectionString, string variable, string databasePrefix)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            string host = builder.Host ?? string.Empty;
            string[] hosts = host.Split(',', StringSplitOptions.RemoveEmptyEntries);
            bool loopback = hosts.Length > 0 && Array.TrueForAll(hosts, h =>
                string.Equals(h, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h, "::1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase));
            if (!loopback)
            {
                throw new InvalidOperationException(
                    "Refusing " + variable + ": only a loopback PostgreSQL test database is allowed.");
            }

            if (builder.Port == 5432 || builder.Port <= 0)
            {
                throw new InvalidOperationException(
                    "Refusing " + variable + ": default/unspecified PostgreSQL port is not an isolated test port.");
            }

            if (string.IsNullOrWhiteSpace(builder.Database) ||
                !builder.Database.StartsWith(databasePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing " + variable + ": database name must start with '" + databasePrefix + "'.");
            }
        }
    }
}

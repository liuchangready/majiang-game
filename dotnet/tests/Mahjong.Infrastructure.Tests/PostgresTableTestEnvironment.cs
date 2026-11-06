using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Mahjong.Infrastructure.Tests
{
    [CollectionDefinition(PostgresTableCollection.Name, DisableParallelization = true)]
    public sealed class PostgresTableCollection
    {
        public const string Name = "postgres-table-p52";
    }

    /// <summary>
    /// Strict real-PostgreSQL test environment. A missing database or key fails the
    /// test run; it never becomes a skip or a 0-test success.
    /// </summary>
    internal static class PostgresTableTestEnvironment
    {
        internal const string RuntimeVariable = "MAHJONG_P52_TEST_POSTGRES";
        internal const string AdminVariable = "MAHJONG_P52_TEST_POSTGRES_ADMIN";
        internal const string KeyVariable = "MAHJONG_TABLE_PG_PAYLOAD_KEY";
        internal const string EvidenceVariable = "MAHJONG_P52_PG_EVIDENCE";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static bool _schemaReady;

        internal static string RuntimeConnectionString => Require(RuntimeVariable, databasePrefix: "mahjong_p52_test");

        internal static string AdminConnectionString => Require(AdminVariable, databasePrefix: "mahjong_p52_test");

        internal static string PayloadKey
        {
            get
            {
                string? value = Environment.GetEnvironmentVariable(KeyVariable);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException(
                        "BLOCKED: " + KeyVariable + " is required for the real PostgreSQL P5.2 tests.");
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
                Assert.True(await PostgresTableMigrator.IsReadyAsync(AdminConnectionString, cancellationToken)
                    .ConfigureAwait(false), "The P5.2 schema is not ready after two migrations.");
                _schemaReady = true;
            }
            finally
            {
                Gate.Release();
            }
        }

        internal static PostgresTableOptions NewOptions(int commandTimeoutSeconds = 15)
        {
            return new PostgresTableOptions(RuntimeConnectionString, PayloadKey, commandTimeoutSeconds);
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
            await ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<long> CountAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            object? value = await ScalarAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
            return value == null || value is DBNull
                ? 0L
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        internal static async Task<object?> ScalarAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<int> ExecuteAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<string[]> QueryStringsAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }

            return rows.ToArray();
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
                    "BLOCKED: " + variable + " is required for the real PostgreSQL P5.2 tests. " +
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
            bool loopback = hosts.Length > 0 && hosts.All(h =>
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

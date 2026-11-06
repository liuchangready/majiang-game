using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Mahjong.Infrastructure
{
    /// <summary>
    /// Applies the idempotent P5.2 table schema. Migration is an explicit deployment
    /// step; tests and the verification wrapper apply it twice to prove repeatability.
    /// The runtime role is never granted DDL.
    /// </summary>
    public static class PostgresTableMigrator
    {
        public const int SchemaVersion = 1;

        public static async Task ApplyAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
            }

            string sql = ReadMigration();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandTimeout = 120
            };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public static async Task<bool> IsReadyAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT to_regclass('table_store.table_state') IS NOT NULL
   AND to_regclass('table_store.table_event') IS NOT NULL
   AND to_regclass('table_store.request_scope') IS NOT NULL
   AND to_regclass('table_store.request_receipt') IS NOT NULL
   AND to_regclass('table_store.table_lease') IS NOT NULL
   AND to_regclass('table_store.table_checkpoint') IS NOT NULL
   AND to_regclass('table_store.settlement_outbox') IS NOT NULL
   AND EXISTS (SELECT 1 FROM table_store.schema_migration WHERE version = 1)";
            await using var command = new NpgsqlCommand(sql, connection);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is bool value && value;
        }

        public static async Task<string> ServerVersionAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT current_setting('server_version')", connection);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string ReadMigration()
        {
            Assembly assembly = typeof(PostgresTableMigrator).Assembly;
            const string suffix = "Migrations.001_table_store.sql";
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    using Stream? stream = assembly.GetManifestResourceStream(name);
                    if (stream == null)
                    {
                        break;
                    }

                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }

            throw new InvalidOperationException("The embedded table-store migration is missing.");
        }
    }
}

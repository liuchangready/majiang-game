using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Npgsql;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    [CollectionDefinition(PostgresLobbyCollection.Name, DisableParallelization = true)]
    public sealed class PostgresLobbyCollection
    {
        public const string Name = "postgres-lobby";
    }

    /// <summary>
    /// Real PostgreSQL helper for the P5.3 lobby store. It refuses anything that
    /// is not an explicitly configured loopback test database: production hosts,
    /// the default PostgreSQL port and database names outside the
    /// "mahjong_lobby_test" prefix are rejected before a connection is opened.
    /// The password (if the connection string carries one) is never logged or
    /// written to the evidence file.
    /// </summary>
    internal static class PostgresLobbyTestDb
    {
        internal const string ConnectionVariable = "MAHJONG_LOBBY_TEST_POSTGRES";
        internal const string EvidenceVariable = "MAHJONG_LOBBY_PG_EVIDENCE";
        internal const string HandoffVariable = "MAHJONG_LOBBY_PG_HANDOFF";
        internal const string RestartPhaseVariable = "MAHJONG_LOBBY_PG_RESTART_PHASE";
        internal const string RootVariable = "MAHJONG_LOBBY_PG_ROOT";
        internal const string DataDirectoryVariable = "MAHJONG_LOBBY_PG_DATA";
        internal const string ExpectedOwner = "mahjong-p53-pg";

        internal static readonly DateTimeOffset FixedNow =
            new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static bool _schemaReady;
        private static string? _connectionString;
        private static string? _serverIdentity;

        internal static string ServerIdentity => _serverIdentity ?? "unknown";

        internal static LobbyOptions CreateOptions(bool autoMigrate)
        {
            var options = new LobbyOptions
            {
                StorageProvider = "Postgres",
                PostgresConnectionString = RequireConnectionString(),
                AutoMigrate = autoMigrate,
                TicketTtlSeconds = 60,
                TicketMaxTtlSeconds = 300,
                ReservationTtlSeconds = 15,
                QueueTtlSeconds = 120,
                SessionTtlHours = 24,
                GuestAccountRetentionDays = 7,
                ReceiptRetentionDays = 7,
                MatchingEnabled = true
            };
            options.Validate("Development");
            return options;
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

                string connectionString = RequireConnectionString();
                await ValidateIsolatedServerAsync(connectionString, cancellationToken).ConfigureAwait(false);
                LobbyOptions options = CreateOptions(autoMigrate: true);
                await using var store = new PostgresLobbyStore(connectionString, options);
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _schemaReady = true;
            }
            finally
            {
                Gate.Release();
            }
        }

        internal static async Task<LobbyFixture> NewIsolatedFixtureAsync(
            IRoomProjectionReader? projections = null,
            CancellationToken cancellationToken = default)
        {
            await ResetAsync(cancellationToken).ConfigureAwait(false);
            return await NewFixtureAsync(projections, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<LobbyFixture> NewFixtureAsync(
            IRoomProjectionReader? projections = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            string connectionString = RequireConnectionString();
            LobbyOptions options = CreateOptions(autoMigrate: false);
            var store = new PostgresLobbyStore(connectionString, options);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var clock = new TestClock(FixedNow);
            return LobbyFixture.CreateWithStore(store, options, clock, projections);
        }

        internal static async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
TRUNCATE lobby.table_ticket_consumption, lobby.table_ticket, lobby.match_ticket,
         lobby.assignment, lobby.room, lobby.session, lobby.request_receipt, lobby.account
CASCADE";
            await ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<long> CountAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            object? result = await ExecuteScalarAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
            return result == null || result is DBNull
                ? 0
                : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        internal static async Task<string?> ScalarStringAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            object? result = await ExecuteScalarAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
            return result == null || result is DBNull
                ? null
                : Convert.ToString(result, CultureInfo.InvariantCulture);
        }

        internal static async Task<int> ExecuteAsync(
            string sql,
            CancellationToken cancellationToken = default,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(RequireConnectionString());
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
            await using var connection = new NpgsqlConnection(RequireConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var values = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }

            return values.ToArray();
        }

        internal static string RequireConnectionString()
        {
            if (_connectionString != null)
            {
                return _connectionString;
            }

            string? value = Environment.GetEnvironmentVariable(ConnectionVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                Assert.Skip(
                    "NOT_RUN: no PostgreSQL test database. Set " + ConnectionVariable +
                    " to an isolated loopback database named mahjong_lobby_test*.");
            }

            _connectionString = value!;
            return _connectionString;
        }

        private static async Task ValidateIsolatedServerAsync(
            string connectionString,
            CancellationToken cancellationToken)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            string host = builder.Host ?? string.Empty;
            string[] hosts = host.Split(',', StringSplitOptions.RemoveEmptyEntries);
            bool loopback = hosts.Length > 0 && hosts.All(IsLoopbackHost);
            if (!loopback)
            {
                throw new InvalidOperationException(
                    "Refusing to run the lobby PostgreSQL tests against a non-loopback host. " +
                    "Only 127.0.0.1, ::1 or localhost is accepted.");
            }

            if (builder.Port == 5432 || builder.Port <= 0)
            {
                throw new InvalidOperationException(
                    "Refusing to run the lobby PostgreSQL tests on port 5432/default. " +
                    "Use a dedicated loopback test port such as 55432.");
            }

            string database = builder.Database ?? string.Empty;
            if (!database.StartsWith("mahjong_lobby_test", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to run the lobby PostgreSQL tests against database '" + database + "'. " +
                    "The database name must start with 'mahjong_lobby_test'.");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT version(),
       current_database(),
       coalesce(host(inet_server_addr()), ''),
       coalesce(inet_server_port(), 0),
       pg_postmaster_start_time()::text,
       current_setting('data_directory'),
       CASE WHEN to_regclass('lobby_verify.meta') IS NULL THEN ''
            ELSE coalesce((SELECT owner FROM lobby_verify.meta ORDER BY owner LIMIT 1), '') END,
       CASE WHEN to_regclass('lobby_verify.meta') IS NULL THEN ''
            ELSE coalesce((SELECT root FROM lobby_verify.meta ORDER BY owner LIMIT 1), '') END,
       CASE WHEN to_regclass('lobby_verify.meta') IS NULL THEN 0
            ELSE (SELECT count(*) FROM lobby_verify.meta) END";
            await using var command = new NpgsqlCommand(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The PostgreSQL test server did not answer the identity query.");
            }

            string version = reader.GetString(0);
            string actualDatabase = reader.GetString(1);
            string address = reader.GetString(2);
            int port = reader.GetInt32(3);
            string startTime = reader.GetString(4);
            string dataDirectory = reader.GetString(5);
            string markerOwner = reader.GetString(6);
            string markerRoot = reader.GetString(7);
            long markerRowCount = reader.GetInt64(8);

            if (!IsLoopbackHost(address) || port != builder.Port)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL server is not the configured loopback test instance (address=" +
                    address + ", port=" + port.ToString(CultureInfo.InvariantCulture) + ").");
            }

            if (!string.Equals(actualDatabase, database, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The PostgreSQL server database identity does not match the request.");
            }

            // The destructive tests truncate lobby tables. Require the marker
            // written by scripts/pg-lobby-verify/start-postgres.ps1 so a user
            // database that merely shares the port/name convention is refused.
            if (!string.Equals(markerOwner, ExpectedOwner, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing to write/truncate database '" + actualDatabase +
                    "': it does not carry the disposable lobby_verify.meta owner marker '" +
                    ExpectedOwner + "'. Use scripts/pg-lobby-verify/start-postgres.ps1.");
            }
            if (markerRowCount != 1)
            {
                throw new InvalidOperationException(
                    "Refusing to write/truncate database '" + actualDatabase +
                    "': lobby_verify.meta must contain exactly one row (found " + markerRowCount + ").");
            }

            string expectedDataDirectory = NormalizePath(dataDirectory);
            string? expectedRoot = Environment.GetEnvironmentVariable(RootVariable);
            string? expectedData = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(expectedRoot))
            {
                string normalizedRoot = NormalizePath(expectedRoot!);
                if (!string.Equals(normalizedRoot, NormalizePath(markerRoot), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Refusing to write/truncate database '" + actualDatabase +
                        "': its ownership marker root does not match " + RootVariable + ".");
                }

                string dataFromRoot = NormalizePath(Path.Combine(expectedRoot!, "data"));
                if (!string.Equals(dataFromRoot, expectedDataDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Refusing to write/truncate database '" + actualDatabase +
                        "': server data_directory does not match the disposable root's data directory.");
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedData) &&
                !string.Equals(
                    NormalizePath(expectedData!),
                    expectedDataDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to write/truncate database '" + actualDatabase +
                    "': server data_directory does not match " + DataDirectoryVariable + ".");
            }

            if (_serverIdentity == null)
            {
                _serverIdentity =
                    "version=" + version + "; database=" + actualDatabase +
                    "; address=" + address + ":" + port.ToString(CultureInfo.InvariantCulture) +
                    "; postmasterStartedAt=" + startTime + "; dataDirectory=" + dataDirectory +
                    "; owner=" + markerOwner + "; root=" + markerRoot;
                PostgresEvidence.Record(
                    "server.identity",
                    ("version", version),
                    ("database", actualDatabase),
                    ("address", address),
                    ("port", port),
                    ("postmasterStartedAt", startTime),
                    ("dataDirectory", dataDirectory),
                    ("owner", markerOwner),
                    ("root", markerRoot));
            }
        }

        private static bool IsLoopbackHost(string host)
        {
            return string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
                   string.Equals(host, "::1", StringComparison.Ordinal) ||
                   string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static async Task<object?> ExecuteScalarAsync(
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(RequireConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static class PostgresEvidence
    {
        private static readonly object Sync = new object();

        internal static void Record(string @event, params (string Key, object? Value)[] fields)
        {
            string? path = Environment.GetEnvironmentVariable(PostgresLobbyTestDb.EvidenceVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["event"] = @event
            };
            foreach ((string key, object? value) in fields)
            {
                payload[key] = value;
            }

            string line = LobbyJson.Serialize(payload);
            lock (Sync)
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }

    internal sealed class MutableTestProjectionReader : IRoomProjectionReader
    {
        private readonly Dictionary<string, PublicRoomProjection> _items =
            new Dictionary<string, PublicRoomProjection>(StringComparer.Ordinal);

        internal void Set(
            string roomId,
            string lifecycle,
            DateTimeOffset updatedAt,
            int seats = 0,
            int spectators = 0,
            uint rulesetVersion = 1)
        {
            _items[roomId] = new PublicRoomProjection(
                roomId,
                lifecycle,
                seats,
                spectators,
                1,
                rulesetVersion,
                updatedAt);
        }

        public Task<PublicRoomProjectionBatch> ReadAsync(
            IReadOnlyList<string> roomIds,
            CancellationToken cancellationToken)
        {
            var items = roomIds
                .Where(_items.ContainsKey)
                .Select(id => _items[id])
                .ToList();
            return Task.FromResult(new PublicRoomProjectionBatch(items));
        }
    }

    internal static class LobbyTestAsync
    {
        internal static async Task<(LobbyResponse? Response, LobbyException? Error)> CaptureAsync(
            Func<Task<LobbyResponse>> action)
        {
            try
            {
                return (await action().ConfigureAwait(false), null);
            }
            catch (LobbyException ex)
            {
                return (null, ex);
            }
        }

        internal static async Task<LobbyAccount> RequireAccountAsync(
            LobbyFixture fixture,
            string accountId)
        {
            LobbyAccount? account = await fixture.Application.Store
                .FindAccountAsync(accountId, CancellationToken.None)
                .ConfigureAwait(false);
            if (account == null)
            {
                throw new InvalidOperationException("The persisted account was not found: " + accountId);
            }

            return account;
        }
    }
}

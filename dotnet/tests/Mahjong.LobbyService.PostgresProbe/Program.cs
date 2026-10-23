using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Mahjong.LobbyService.Tests;
using Npgsql;

namespace Mahjong.LobbyService.PostgresProbe
{
    /// <summary>
    /// Cross-process restart probe for the P5.3 lobby PostgreSQL store. This is
    /// deliberately a console program, not an xUnit test: the ordinary lobby
    /// test assembly must contain no phase-only skips. The runner invokes:
    ///   write -> immediate PostgreSQL restart -> read
    /// and each phase exits non-zero on any failed assertion.
    /// </summary>
    internal static class Program
    {
        private const string HandoffMarker = "p53-pg-restart-v1";
        private const string ExpectedOwner = "mahjong-p53-pg";
        private const string ConnectionVariable = "MAHJONG_LOBBY_TEST_POSTGRES";
        private const string EvidenceVariable = "MAHJONG_LOBBY_PG_EVIDENCE";
        private const string HandoffVariable = "MAHJONG_LOBBY_PG_HANDOFF";
        private const string RootVariable = "MAHJONG_LOBBY_PG_ROOT";
        private const string DataDirectoryVariable = "MAHJONG_LOBBY_PG_DATA";

        private static readonly DateTimeOffset FixedNow =
            new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        public static async Task<int> Main(string[] args)
        {
            string phase = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
            try
            {
                switch (phase)
                {
                    case "write":
                        await RunWriteAsync(default);
                        return 0;
                    case "read":
                        await RunReadAsync(default);
                        return 0;
                    default:
                        Console.Error.WriteLine(
                            "usage: Mahjong.LobbyService.PostgresProbe write|read " +
                            "(configure MAHJONG_LOBBY_TEST_POSTGRES, MAHJONG_LOBBY_PG_ROOT, " +
                            "MAHJONG_LOBBY_PG_DATA, MAHJONG_LOBBY_PG_HANDOFF).");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PROBE_RESULT: FAIL phase=" + phase + " " + ex);
                return 1;
            }
        }

        private static async Task RunWriteAsync(CancellationToken cancellationToken)
        {
            string connectionString = RequireEnvironment(ConnectionVariable);
            string root = RequireEnvironment(RootVariable);
            string dataDirectory = RequireEnvironment(DataDirectoryVariable);
            string handoffPath = RequireEnvironment(HandoffVariable);
            string? evidencePath = Environment.GetEnvironmentVariable(EvidenceVariable);

            DbIdentity identity = await ValidateDisposableDatabaseAsync(
                connectionString,
                root,
                dataDirectory,
                cancellationToken).ConfigureAwait(false);

            // A destructive TRUNCATE is allowed only after the read-only
            // data_directory + marker checks above have passed.
            await ResetAsync(connectionString, cancellationToken).ConfigureAwait(false);

            LobbyOptions options = CreateOptions(connectionString);
            var clock = new TestClock(FixedNow);
            var store = new PostgresLobbyStore(connectionString, options);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using LobbyFixture fixture = LobbyFixture.CreateWithStore(store, options, clock);

            GuestSessionView host = await fixture.CreateGuestAsync("pg-restart-host").ConfigureAwait(false);
            CreateRoomView room = await fixture.CreateRoomAsync(host, "PG restart room").ConfigureAwait(false);
            GuestSessionView guest = await fixture.CreateGuestAsync("pg-restart-guest").ConfigureAwait(false);
            LobbyAccount account = await RequireAccountAsync(fixture, guest.AccountId, cancellationToken)
                .ConfigureAwait(false);

            string joinKey = NewRequestId("req_restart_join_");
            LobbyResponse joinResponse = await fixture.Application.JoinRoomAsync(
                account,
                room.Room.RoomId,
                new JoinRoomCommand(1),
                joinKey,
                cancellationToken).ConfigureAwait(false);
            EnsureStatus(joinResponse, 201, "join");
            JoinRoomView joinView = LobbyJson.Deserialize<JoinRoomView>(joinResponse.Json);

            string issueKey = NewRequestId("req_restart_issue_");
            LobbyResponse issueResponse = await fixture.Application.IssueTableTicketAsync(
                account,
                new IssueTableTicketCommand(joinView.Assignment.AssignmentId),
                issueKey,
                cancellationToken).ConfigureAwait(false);
            EnsureStatus(issueResponse, 201, "issue");
            TableTicketView ticket = LobbyJson.Deserialize<TableTicketView>(issueResponse.Json);

            TicketConsumptionResult consumed = await fixture
                .ConsumeAsync(ticket, account.AccountId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(consumed.AssignmentId, joinView.Assignment.AssignmentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The consumed ticket did not match the assignment.");
            }

            long consumptionRows = await CountAsync(
                connectionString,
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                cancellationToken,
                ("jti", ticket.Jti)).ConfigureAwait(false);
            if (consumptionRows != 1)
            {
                throw new InvalidOperationException(
                    "Expected exactly one ticket-consumption row, found " +
                    consumptionRows.ToString(CultureInfo.InvariantCulture) + ".");
            }

            var handoff = new RestartHandoff
            {
                Marker = HandoffMarker,
                AccountId = account.AccountId,
                RoomId = room.Room.RoomId,
                AssignmentId = joinView.Assignment.AssignmentId,
                JoinRequestId = joinKey,
                JoinSeat = joinView.Assignment.SeatId ?? 1,
                JoinBody = joinResponse.Json,
                IssueRequestId = issueKey,
                IssueBody = issueResponse.Json,
                Jti = ticket.Jti,
                ConsumptionRows = consumptionRows,
                TicketRows = 1,
                WriterProcessId = Environment.ProcessId,
                PostmasterStartedAt = identity.StartedAt,
                DatabaseVersion = identity.Version
            };

            string json = LobbyJson.Serialize(handoff);
            string tempPath = handoffPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, handoffPath, overwrite: true);

            RecordEvidence(
                evidencePath,
                "restart.write",
                ("writerProcessId", handoff.WriterProcessId),
                ("postmasterStartedAt", handoff.PostmasterStartedAt),
                ("accountId", handoff.AccountId),
                ("assignmentId", handoff.AssignmentId),
                ("jti", handoff.Jti),
                ("consumptionRows", consumptionRows),
                ("dataDirectory", identity.DataDirectory));

            Console.WriteLine(
                "PROBE_RESULT: PASS phase=write writer=" +
                handoff.WriterProcessId.ToString(CultureInfo.InvariantCulture) +
                " consumptionRows=" + consumptionRows.ToString(CultureInfo.InvariantCulture) +
                " postmaster=" + handoff.PostmasterStartedAt);
        }

        private static async Task RunReadAsync(CancellationToken cancellationToken)
        {
            string connectionString = RequireEnvironment(ConnectionVariable);
            string root = RequireEnvironment(RootVariable);
            string dataDirectory = RequireEnvironment(DataDirectoryVariable);
            string handoffPath = RequireEnvironment(HandoffVariable);
            string? evidencePath = Environment.GetEnvironmentVariable(EvidenceVariable);

            if (!File.Exists(handoffPath))
            {
                throw new InvalidOperationException(
                    "The restart handoff file is missing; run the write phase first: " + handoffPath);
            }

            RestartHandoff handoff = LobbyJson.Deserialize<RestartHandoff>(File.ReadAllText(handoffPath));
            if (!string.Equals(handoff.Marker, HandoffMarker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The restart handoff marker is invalid.");
            }

            DbIdentity identity = await ValidateDisposableDatabaseAsync(
                connectionString,
                root,
                dataDirectory,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(handoff.PostmasterStartedAt, identity.StartedAt, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The postmaster start time did not change; PostgreSQL was not actually restarted.");
            }
            if (handoff.WriterProcessId == Environment.ProcessId)
            {
                throw new InvalidOperationException("The read phase is not running in a new OS process.");
            }

            LobbyOptions options = CreateOptions(connectionString);
            var clock = new TestClock(FixedNow);
            var store = new PostgresLobbyStore(connectionString, options);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using LobbyFixture fixture = LobbyFixture.CreateWithStore(store, options, clock);

            LobbyAccount account = await RequireAccountAsync(fixture, handoff.AccountId, cancellationToken)
                .ConfigureAwait(false);

            LobbyResponse joinReplay = await fixture.Application.JoinRoomAsync(
                account,
                handoff.RoomId,
                new JoinRoomCommand(handoff.JoinSeat),
                handoff.JoinRequestId,
                cancellationToken).ConfigureAwait(false);
            EnsureStatus(joinReplay, 201, "join replay");
            if (!joinReplay.Replayed || !string.Equals(joinReplay.Json, handoff.JoinBody, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The persisted join receipt did not replay identically.");
            }

            LobbyResponse issueReplay = await fixture.Application.IssueTableTicketAsync(
                account,
                new IssueTableTicketCommand(handoff.AssignmentId),
                handoff.IssueRequestId,
                cancellationToken).ConfigureAwait(false);
            EnsureStatus(issueReplay, 201, "issue replay");
            if (!issueReplay.Replayed || !string.Equals(issueReplay.Json, handoff.IssueBody, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The persisted issue receipt did not replay identically.");
            }
            TableTicketView replayedTicket = LobbyJson.Deserialize<TableTicketView>(issueReplay.Json);
            if (!string.Equals(replayedTicket.Jti, handoff.Jti, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The replayed ticket jti changed.");
            }

            string? assignmentState = await ScalarStringAsync(
                connectionString,
                @"
SELECT status || '|' ||
       CASE WHEN table_confirmed_at IS NULL THEN 'unconfirmed' ELSE 'table-confirmed' END || '|' ||
       CASE WHEN expires_at IS NULL THEN 'no-expiry' ELSE 'expiry-set' END
FROM lobby.assignment WHERE assignment_id = @assignment",
                cancellationToken,
                ("assignment", handoff.AssignmentId)).ConfigureAwait(false);
            if (!string.Equals(assignmentState, "CONFIRMED|table-confirmed|no-expiry", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected persisted assignment state: " + assignmentState);
            }

            long consumptionRows = await CountAsync(
                connectionString,
                "SELECT count(*) FROM lobby.table_ticket_consumption WHERE jti = @jti",
                cancellationToken,
                ("jti", handoff.Jti)).ConfigureAwait(false);
            long consumedTicketRows = await CountAsync(
                connectionString,
                "SELECT count(*) FROM lobby.table_ticket WHERE jti = @jti AND consumed_at IS NOT NULL",
                cancellationToken,
                ("jti", handoff.Jti)).ConfigureAwait(false);
            long ticketRows = await CountAsync(
                connectionString,
                "SELECT count(*) FROM lobby.table_ticket WHERE assignment_id = @assignment",
                cancellationToken,
                ("assignment", handoff.AssignmentId)).ConfigureAwait(false);
            if (consumptionRows != 1 || consumedTicketRows != 1 || ticketRows != 1)
            {
                throw new InvalidOperationException(
                    "Persisted ticket/consumption cardinality is not 1/1/1: " +
                    consumptionRows.ToString(CultureInfo.InvariantCulture) + "/" +
                    consumedTicketRows.ToString(CultureInfo.InvariantCulture) + "/" +
                    ticketRows.ToString(CultureInfo.InvariantCulture) + ".");
            }

            RecordEvidence(
                evidencePath,
                "restart.read",
                ("readerProcessId", Environment.ProcessId),
                ("writerProcessId", handoff.WriterProcessId),
                ("postmasterBeforeRestart", handoff.PostmasterStartedAt),
                ("postmasterAfterRestart", identity.StartedAt),
                ("joinReplayed", joinReplay.Replayed),
                ("issueReplayed", issueReplay.Replayed),
                ("consumptionRows", consumptionRows),
                ("assignmentState", assignmentState),
                ("dataDirectory", identity.DataDirectory));

            Console.WriteLine(
                "PROBE_RESULT: PASS phase=read reader=" +
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " writer=" + handoff.WriterProcessId.ToString(CultureInfo.InvariantCulture) +
                " consumptionRows=" + consumptionRows.ToString(CultureInfo.InvariantCulture) +
                " postmaster=" + identity.StartedAt);
        }

        private static async Task<DbIdentity> ValidateDisposableDatabaseAsync(
            string connectionString,
            string expectedRoot,
            string expectedDataDirectory,
            CancellationToken cancellationToken)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (builder.Port == 5432 || builder.Port <= 0)
            {
                throw new InvalidOperationException(
                    "Refusing a default/unsafe PostgreSQL port; use a dedicated loopback test port.");
            }
            string database = builder.Database ?? string.Empty;
            if (!database.StartsWith("mahjong_lobby_test", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing database '" + database + "': name must start with mahjong_lobby_test.");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT current_setting('data_directory'),
       pg_postmaster_start_time()::text,
       coalesce(host(inet_server_addr()), ''),
       coalesce(inet_server_port(), 0),
       current_database(),
       version(),
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
                throw new InvalidOperationException("The PostgreSQL identity query returned no row.");
            }

            string actualDataDirectory = reader.GetString(0);
            string startedAt = reader.GetString(1);
            string address = reader.GetString(2);
            int port = reader.GetInt32(3);
            string actualDatabase = reader.GetString(4);
            string version = reader.GetString(5);
            string markerOwner = reader.GetString(6);
            string markerRoot = reader.GetString(7);
            long markerRows = reader.GetInt64(8);

            if (!IPAddress.TryParse(address, out IPAddress? parsedAddress) ||
                parsedAddress == null ||
                !IPAddress.IsLoopback(parsedAddress))
            {
                throw new InvalidOperationException("Refusing a non-loopback PostgreSQL server: " + address);
            }
            if (port != builder.Port)
            {
                throw new InvalidOperationException("The PostgreSQL server port does not match the connection string.");
            }
            if (!string.Equals(actualDatabase, database, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The PostgreSQL database identity does not match the connection string.");
            }

            string normalizedRoot = NormalizePath(expectedRoot);
            string normalizedData = NormalizePath(expectedDataDirectory);
            string expectedFromRoot = NormalizePath(Path.Combine(expectedRoot, "data"));
            if (!string.Equals(NormalizePath(actualDataDirectory), normalizedData, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(normalizedData, expectedFromRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing: server data_directory is not the configured disposable data directory.");
            }
            if (!string.Equals(markerOwner, ExpectedOwner, StringComparison.Ordinal) || markerRows != 1)
            {
                throw new InvalidOperationException(
                    "Refusing: lobby_verify.meta owner/row-count does not identify this disposable database.");
            }
            if (!string.Equals(NormalizePath(markerRoot), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing: lobby_verify.meta root does not match MAHJONG_LOBBY_PG_ROOT.");
            }

            return new DbIdentity(actualDataDirectory, startedAt, version);
        }

        private static async Task ResetAsync(string connectionString, CancellationToken cancellationToken)
        {
            const string sql = @"
TRUNCATE lobby.table_ticket_consumption, lobby.table_ticket, lobby.match_ticket,
         lobby.assignment, lobby.room, lobby.session, lobby.request_receipt, lobby.account
CASCADE";
            await ExecuteAsync(connectionString, sql, cancellationToken).ConfigureAwait(false);
        }

        private static LobbyOptions CreateOptions(string connectionString)
        {
            var options = new LobbyOptions
            {
                StorageProvider = "Postgres",
                PostgresConnectionString = connectionString,
                AutoMigrate = false,
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

        private static async Task<LobbyAccount> RequireAccountAsync(
            LobbyFixture fixture,
            string accountId,
            CancellationToken cancellationToken)
        {
            LobbyAccount? account = await fixture.Application.Store
                .FindAccountAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            return account ?? throw new InvalidOperationException("The persisted account was not found: " + accountId);
        }

        private static void EnsureStatus(LobbyResponse response, int expected, string operation)
        {
            if (response.StatusCode != expected)
            {
                throw new InvalidOperationException(
                    operation + " returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture) +
                    ": " + response.Json);
            }
        }

        private static string RequireEnvironment(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Required environment variable is missing: " + name);
            }
            return value!;
        }

        private static string NewRequestId(string prefix)
        {
            return prefix + Guid.NewGuid().ToString("N");
        }

        private static string NormalizePath(string path)
        {
            string native = path.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(native).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static async Task<long> CountAsync(
            string connectionString,
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object? Value)[] parameters)
        {
            object? value = await ScalarAsync(connectionString, sql, cancellationToken, parameters)
                .ConfigureAwait(false);
            return value == null || value is DBNull
                ? 0
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static async Task<string?> ScalarStringAsync(
            string connectionString,
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object? Value)[] parameters)
        {
            object? value = await ScalarAsync(connectionString, sql, cancellationToken, parameters)
                .ConfigureAwait(false);
            return value == null || value is DBNull
                ? null
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static async Task<object?> ScalarAsync(
            string connectionString,
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object? Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task ExecuteAsync(
            string connectionString,
            string sql,
            CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void RecordEvidence(
            string? path,
            string @event,
            params (string Key, object? Value)[] fields)
        {
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

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.AppendAllText(path, LobbyJson.Serialize(payload) + Environment.NewLine);
        }

        private sealed record DbIdentity(string DataDirectory, string StartedAt, string Version);

        private sealed class RestartHandoff
        {
            public string Marker { get; set; } = string.Empty;

            public string AccountId { get; set; } = string.Empty;

            public string RoomId { get; set; } = string.Empty;

            public string AssignmentId { get; set; } = string.Empty;

            public string JoinRequestId { get; set; } = string.Empty;

            public int JoinSeat { get; set; }

            public string JoinBody { get; set; } = string.Empty;

            public string IssueRequestId { get; set; } = string.Empty;

            public string IssueBody { get; set; } = string.Empty;

            public string Jti { get; set; } = string.Empty;

            public long ConsumptionRows { get; set; }

            public long TicketRows { get; set; }

            public int WriterProcessId { get; set; }

            public string PostmasterStartedAt { get; set; } = string.Empty;

            public string DatabaseVersion { get; set; } = string.Empty;
        }
    }
}

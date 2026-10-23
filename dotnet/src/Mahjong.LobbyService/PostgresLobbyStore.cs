using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// PostgreSQL implementation of the lobby store. Every lobby command that
    /// has a side effect runs in one transaction together with its idempotency
    /// receipt; assignment/ticket consumption uses row locks plus unique
    /// constraints. This class is not exercised by the in-memory tests.
    /// </summary>
    public sealed class PostgresLobbyStore : ILobbyStore
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly LobbyOptions _options;

        public PostgresLobbyStore(string connectionString, LobbyOptions options)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
            }

            _options = options;
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }

        public string ProviderName => "PostgreSQL (lobby schema)";

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_options.AutoMigrate)
            {
                string migration = ReadMigration();
                await using (NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                                 .ConfigureAwait(false))
                {
                    await using (var command = new NpgsqlCommand(migration, connection))
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!await CheckReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new LobbyOptionsException(
                    "The lobby schema is missing. Apply Migrations/001_lobby.sql or set Lobby:AutoMigrate=true in a controlled environment.");
            }
        }

        public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT to_regclass('lobby.room') IS NOT NULL AND to_regclass('lobby.account') IS NOT NULL",
                connection);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is bool value && value;
        }

        public async Task<LobbySessionInfo?> FindSessionByTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            const string sql = @"
SELECT s.token_hash, s.account_id, s.created_at, s.expires_at, s.revoked_at,
       a.account_id, a.is_guest, a.display_name, a.created_at, a.expires_at
FROM lobby.session s
JOIN lobby.account a ON a.account_id = s.account_id
WHERE s.token_hash = @token_hash";
            await using var command = new NpgsqlCommand(sql, connection);
            Add(command, "token_hash", tokenHash, NpgsqlDbType.Text);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var session = new LobbySession(
                reader.GetString(0),
                reader.GetString(1),
                ReadTimestamp(reader, 2),
                ReadTimestamp(reader, 3),
                ReadNullableTimestamp(reader, 4));
            var account = new LobbyAccount(
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetString(7),
                ReadTimestamp(reader, 8),
                ReadTimestamp(reader, 9));
            return new LobbySessionInfo(session, account);
        }

        public async Task<LobbyAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            const string sql = @"
SELECT account_id, is_guest, display_name, created_at, expires_at
FROM lobby.account WHERE account_id = @account_id";
            await using var command = new NpgsqlCommand(sql, connection);
            Add(command, "account_id", accountId, NpgsqlDbType.Text);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new LobbyAccount(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetString(2),
                ReadTimestamp(reader, 3),
                ReadTimestamp(reader, 4));
        }

        public async Task<LobbyRoomPage> ListRoomsAsync(
            string? cursor,
            int limit,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            bool hasCursor = LobbyCursor.TryDecode(cursor, out LobbyCursor parsed);
            if (!string.IsNullOrEmpty(cursor) && !hasCursor)
            {
                throw new LobbyException(400, "INVALID_CURSOR", "The room catalog cursor is invalid.");
            }

            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            const string sql = @"
SELECT r.room_id, r.title, r.visibility, r.state, r.allow_spectators, r.max_players,
       r.ruleset_version, r.host_account_id, r.created_at, r.updated_at, r.closed_at,
       (SELECT count(*)::int FROM lobby.assignment a
         WHERE a.room_id = r.room_id AND a.role = 'PLAYER'
           AND a.status IN ('RESERVED', 'CONFIRMED')
           AND (a.expires_at IS NULL OR a.expires_at > @now)) AS seats_taken,
       (SELECT count(*)::int FROM lobby.assignment a
         WHERE a.room_id = r.room_id AND a.role = 'SPECTATOR'
           AND a.status IN ('RESERVED', 'CONFIRMED')
           AND (a.expires_at IS NULL OR a.expires_at > @now)) AS spectator_count
FROM lobby.room r
WHERE r.visibility = 'PUBLIC' AND r.state = 'OPEN'
  AND (@has_cursor = false OR r.created_at < @c_created OR (r.created_at = @c_created AND r.room_id > @c_room))
ORDER BY r.created_at DESC, r.room_id ASC
LIMIT @limit";
            await using var command = new NpgsqlCommand(sql, connection);
            Add(command, "now", now, NpgsqlDbType.TimestampTz);
            Add(command, "has_cursor", hasCursor, NpgsqlDbType.Boolean);
            Add(command, "c_created", hasCursor ? parsed.CreatedAt : (object?)DBNull.Value, NpgsqlDbType.TimestampTz);
            Add(command, "c_room", hasCursor ? parsed.RoomId : (object?)DBNull.Value, NpgsqlDbType.Text);
            Add(command, "limit", limit + 1, NpgsqlDbType.Integer);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var items = new List<LobbyRoomSnapshot>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LobbyRoom room = ReadRoom(reader, 0);
                items.Add(new LobbyRoomSnapshot(room, reader.GetInt32(11), reader.GetInt32(12)));
            }

            bool hasMore = items.Count > limit;
            if (hasMore)
            {
                items.RemoveAt(items.Count - 1);
            }

            string? next = hasMore && items.Count > 0
                ? LobbyCursor.Encode(items[items.Count - 1].Room.CreatedAt, items[items.Count - 1].Room.RoomId)
                : null;
            return new LobbyRoomPage(items, next);
        }

        public async Task<LobbyRoomSnapshot?> FindRoomSnapshotAsync(
            string roomId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            const string sql = @"
SELECT r.room_id, r.title, r.visibility, r.state, r.allow_spectators, r.max_players,
       r.ruleset_version, r.host_account_id, r.created_at, r.updated_at, r.closed_at,
       (SELECT count(*)::int FROM lobby.assignment a
         WHERE a.room_id = r.room_id AND a.role = 'PLAYER'
           AND a.status IN ('RESERVED', 'CONFIRMED')
           AND (a.expires_at IS NULL OR a.expires_at > @now)) AS seats_taken,
       (SELECT count(*)::int FROM lobby.assignment a
         WHERE a.room_id = r.room_id AND a.role = 'SPECTATOR'
           AND a.status IN ('RESERVED', 'CONFIRMED')
           AND (a.expires_at IS NULL OR a.expires_at > @now)) AS spectator_count
FROM lobby.room r
WHERE r.room_id = @room_id";
            await using var command = new NpgsqlCommand(sql, connection);
            Add(command, "room_id", roomId, NpgsqlDbType.Text);
            Add(command, "now", now, NpgsqlDbType.TimestampTz);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new LobbyRoomSnapshot(ReadRoom(reader, 0), reader.GetInt32(11), reader.GetInt32(12));
        }

        public async Task<LobbyAssignment?> FindAssignmentAsync(
            string assignmentId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            const string sql = @"
SELECT assignment_id, room_id, account_id, role, seat_id, status, created_at, updated_at,
       expires_at, confirmed_at, table_confirmed_at, match_ticket_id
FROM lobby.assignment WHERE assignment_id = @assignment_id";
            await using var command = new NpgsqlCommand(sql, connection);
            Add(command, "assignment_id", assignmentId, NpgsqlDbType.Text);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadAssignment(reader, 0);
        }

        public async Task<LobbyMatchTicket?> FindMatchTicketAsync(
            string matchTicketId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            const string sql = @"
SELECT match_ticket_id, account_id, mode, status, ruleset_version, region, created_at, expires_at,
       reserved_at, confirmed_at, cancelled_at, assignment_id, room_id
FROM lobby.match_ticket WHERE match_ticket_id = @id";
            await using var command = new NpgsqlCommand(sql, connection);
            Add(command, "id", matchTicketId, NpgsqlDbType.Text);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadMatchTicket(reader, 0);
        }

        public async Task<LobbyResponse> ExecuteIdempotentAsync(
            LobbyIdempotencyKey key,
            string payloadDigest,
            DateTimeOffset now,
            Func<ILobbyTransaction, CancellationToken, Task<LobbyResponse>> operation,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await AcquireReceiptLockAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
                ReceiptRow? existing = await ReadReceiptAsync(connection, transaction, key, cancellationToken)
                    .ConfigureAwait(false);
                if (existing != null)
                {
                    if (!string.Equals(existing.PayloadDigest, payloadDigest, StringComparison.Ordinal))
                    {
                        throw new LobbyException(409, "REQUEST_ID_CONFLICT", "The idempotency key was already used with a different payload.");
                    }

                    if (!string.Equals(existing.Status, "COMPLETED", StringComparison.Ordinal))
                    {
                        throw new LobbyException(409, "REQUEST_IN_PROGRESS", "The original request is still in progress.");
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new LobbyResponse(existing.HttpStatus, existing.ResultBody, Replayed: true);
                }

                await InsertReceiptAsync(connection, transaction, key, payloadDigest, now, cancellationToken)
                    .ConfigureAwait(false);
                LobbyResponse response = await operation(
                    new PostgresLobbyTransaction(connection, transaction),
                    cancellationToken).ConfigureAwait(false);
                await CompleteReceiptAsync(connection, transaction, key, response, now, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch (PostgresException ex)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw Translate(ex);
            }
            catch
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(
            DateTimeOffset now,
            Func<ILobbyTransaction, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                T result = await operation(
                    new PostgresLobbyTransaction(connection, transaction),
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (PostgresException ex)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw Translate(ex);
            }
            catch
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }

        private static async Task AcquireReceiptLockAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            LobbyIdempotencyKey key,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
                connection,
                transaction);
            Add(command, "key", key.Principal + "\n" + key.RequestId, NpgsqlDbType.Text);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<ReceiptRow?> ReadReceiptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            LobbyIdempotencyKey key,
            CancellationToken cancellationToken)
        {
            const string sql = @"
SELECT payload_digest, status, http_status, result_body
FROM lobby.request_receipt WHERE principal = @principal AND request_id = @request_id";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            Add(command, "principal", key.Principal, NpgsqlDbType.Text);
            Add(command, "request_id", key.RequestId, NpgsqlDbType.Text);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new ReceiptRow(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
        }

        private Task InsertReceiptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            LobbyIdempotencyKey key,
            string payloadDigest,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            const string sql = @"
INSERT INTO lobby.request_receipt
    (principal, request_id, payload_digest, status, http_status, result_body, created_at, completed_at, expires_at)
VALUES (@principal, @request_id, @digest, 'IN_PROGRESS', 0, '', @now, NULL, @expires)";
            return ExecuteNonQueryAsync(connection, transaction, sql, cancellationToken,
                ("principal", key.Principal, NpgsqlDbType.Text),
                ("request_id", key.RequestId, NpgsqlDbType.Text),
                ("digest", payloadDigest, NpgsqlDbType.Text),
                ("now", now, NpgsqlDbType.TimestampTz),
                ("expires", now.AddDays(_options.ReceiptRetentionDays), NpgsqlDbType.TimestampTz));
        }

        private static Task CompleteReceiptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            LobbyIdempotencyKey key,
            LobbyResponse response,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            const string sql = @"
UPDATE lobby.request_receipt
SET status = 'COMPLETED', http_status = @http_status, result_body = @body, completed_at = @now
WHERE principal = @principal AND request_id = @request_id";
            return ExecuteNonQueryAsync(connection, transaction, sql, cancellationToken,
                ("http_status", response.StatusCode, NpgsqlDbType.Integer),
                ("body", response.Json, NpgsqlDbType.Text),
                ("now", now, NpgsqlDbType.TimestampTz),
                ("principal", key.Principal, NpgsqlDbType.Text),
                ("request_id", key.RequestId, NpgsqlDbType.Text));
        }

        private static async Task ExecuteNonQueryAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object? Value, NpgsqlDbType Type)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach ((string name, object? value, NpgsqlDbType type) in parameters)
            {
                Add(command, name, value, type);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task SafeRollbackAsync(NpgsqlTransaction transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The original failure is more useful than a rollback failure.
            }
        }

        private static LobbyException Translate(PostgresException ex)
        {
            if (string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                return ex.ConstraintName switch
                {
                    "uq_assignment_active_seat" => new LobbyException(409, "SEAT_TAKEN", "The seat is already reserved."),
                    "uq_assignment_active_account" => new LobbyException(409, "ALREADY_ASSIGNED", "This account already has an active assignment."),
                    "uq_match_active_account" => new LobbyException(409, "ALREADY_QUEUED", "This account already has an active match ticket."),
                    "uq_table_ticket_active_assignment" => new LobbyException(409, "TABLE_TICKET_ALREADY_ACTIVE", "The assignment already has an active ticket."),
                    "pk_table_ticket_consumption" => new LobbyException(409, "TICKET_ALREADY_CONSUMED", "The table ticket was already consumed."),
                    _ => new LobbyException(409, "CONFLICT", "The operation conflicts with existing lobby state.")
                };
            }

            if (string.Equals(ex.SqlState, "23503", StringComparison.Ordinal))
            {
                return new LobbyException(409, "REFERENCE_CONFLICT", "A referenced lobby row no longer exists.");
            }

            return new LobbyException(503, "STORAGE_UNAVAILABLE", "The lobby database rejected the transaction.");
        }

        private static string ReadMigration()
        {
            Assembly assembly = typeof(PostgresLobbyStore).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(
                "Mahjong.LobbyService.Migrations.001_lobby.sql");
            if (stream == null)
            {
                throw new InvalidOperationException("Embedded lobby migration was not found.");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType type)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type)
            {
                Value = value ?? DBNull.Value
            });
        }

        private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
        }

        private static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);
        }

        private static LobbyRoom ReadRoom(NpgsqlDataReader reader, int offset)
        {
            return new LobbyRoom(
                reader.GetString(offset),
                reader.GetString(offset + 1),
                string.Equals(reader.GetString(offset + 2), "PUBLIC", StringComparison.Ordinal)
                    ? LobbyRoomVisibility.Public
                    : LobbyRoomVisibility.Private,
                string.Equals(reader.GetString(offset + 3), "OPEN", StringComparison.Ordinal)
                    ? LobbyRoomState.Open
                    : LobbyRoomState.Closed,
                reader.GetBoolean(offset + 4),
                reader.GetInt32(offset + 5),
                (uint)reader.GetInt64(offset + 6),
                reader.GetString(offset + 7),
                ReadTimestamp(reader, offset + 8),
                ReadTimestamp(reader, offset + 9),
                ReadNullableTimestamp(reader, offset + 10));
        }

        private static LobbyAssignment ReadAssignment(NpgsqlDataReader reader, int offset)
        {
            return new LobbyAssignment(
                reader.GetString(offset),
                reader.GetString(offset + 1),
                reader.GetString(offset + 2),
                string.Equals(reader.GetString(offset + 3), "PLAYER", StringComparison.Ordinal)
                    ? LobbyRole.Player
                    : LobbyRole.Spectator,
                reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
                reader.GetString(offset + 5) switch
                {
                    "RESERVED" => LobbyAssignmentStatus.Reserved,
                    "CONFIRMED" => LobbyAssignmentStatus.Confirmed,
                    "CANCELLED" => LobbyAssignmentStatus.Cancelled,
                    "EXPIRED" => LobbyAssignmentStatus.Expired,
                    _ => LobbyAssignmentStatus.Unspecified
                },
                ReadTimestamp(reader, offset + 6),
                ReadTimestamp(reader, offset + 7),
                ReadNullableTimestamp(reader, offset + 8),
                ReadNullableTimestamp(reader, offset + 9),
                ReadNullableTimestamp(reader, offset + 10),
                reader.IsDBNull(offset + 11) ? null : reader.GetString(offset + 11));
        }

        private static LobbyMatchTicket ReadMatchTicket(NpgsqlDataReader reader, int offset)
        {
            return new LobbyMatchTicket(
                reader.GetString(offset),
                reader.GetString(offset + 1),
                string.Equals(reader.GetString(offset + 2), "CASUAL", StringComparison.Ordinal)
                    ? LobbyMatchMode.Casual
                    : LobbyMatchMode.Ranked,
                reader.GetString(offset + 3) switch
                {
                    "QUEUED" => LobbyMatchStatus.Queued,
                    "RESERVED" => LobbyMatchStatus.Reserved,
                    "CONFIRMED" => LobbyMatchStatus.Confirmed,
                    "CANCELLED" => LobbyMatchStatus.Cancelled,
                    "EXPIRED" => LobbyMatchStatus.Expired,
                    _ => LobbyMatchStatus.Unspecified
                },
                (uint)reader.GetInt64(offset + 4),
                reader.GetString(offset + 5),
                ReadTimestamp(reader, offset + 6),
                ReadTimestamp(reader, offset + 7),
                ReadNullableTimestamp(reader, offset + 8),
                ReadNullableTimestamp(reader, offset + 9),
                ReadNullableTimestamp(reader, offset + 10),
                reader.IsDBNull(offset + 11) ? null : reader.GetString(offset + 11),
                reader.IsDBNull(offset + 12) ? null : reader.GetString(offset + 12));
        }

        private sealed record ReceiptRow(string PayloadDigest, string Status, int HttpStatus, string ResultBody);

        private sealed class PostgresLobbyTransaction : ILobbyTransaction
        {
            private readonly NpgsqlConnection _connection;
            private readonly NpgsqlTransaction _transaction;

            public PostgresLobbyTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
            {
                _connection = connection;
                _transaction = transaction;
            }

            public async Task LockAccountAsync(string accountId, CancellationToken cancellationToken)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
                    _connection,
                    _transaction);
                Add(command, "key", "lobby-account:" + accountId, NpgsqlDbType.Text);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            public async Task<LobbyAccount?> FindAccountByBootstrapAsync(
                string deviceDigest,
                string bootstrapHash,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT account_id, is_guest, display_name, created_at, expires_at
FROM lobby.account
WHERE device_digest = @device_digest AND bootstrap_hash = @bootstrap_hash";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "device_digest", deviceDigest, NpgsqlDbType.Text);
                Add(command, "bootstrap_hash", bootstrapHash, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new LobbyAccount(
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetString(2),
                    ReadTimestamp(reader, 3),
                    ReadTimestamp(reader, 4));
            }

            public async Task<LobbyAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT account_id, is_guest, display_name, created_at, expires_at
FROM lobby.account WHERE account_id = @account_id";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "account_id", accountId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new LobbyAccount(
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetString(2),
                    ReadTimestamp(reader, 3),
                    ReadTimestamp(reader, 4));
            }

            public async Task InsertAccountAsync(
                LobbyAccount account,
                string deviceDigest,
                string bootstrapHash,
                CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.account
    (account_id, is_guest, display_name, device_digest, bootstrap_hash, created_at, expires_at)
VALUES (@account_id, @is_guest, @display_name, @device_digest, @bootstrap_hash, @created_at, @expires_at)
ON CONFLICT (device_digest, bootstrap_hash) DO NOTHING";
                await ExecuteAsync(sql, cancellationToken,
                    ("account_id", account.AccountId, NpgsqlDbType.Text),
                    ("is_guest", account.IsGuest, NpgsqlDbType.Boolean),
                    ("display_name", account.DisplayName, NpgsqlDbType.Text),
                    ("device_digest", deviceDigest, NpgsqlDbType.Text),
                    ("bootstrap_hash", bootstrapHash, NpgsqlDbType.Text),
                    ("created_at", account.CreatedAt, NpgsqlDbType.TimestampTz),
                    ("expires_at", account.ExpiresAt, NpgsqlDbType.TimestampTz)).ConfigureAwait(false);
            }

            public async Task InsertSessionAsync(LobbySession session, CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.session (token_hash, account_id, created_at, expires_at, revoked_at)
VALUES (@token_hash, @account_id, @created_at, @expires_at, @revoked_at)";
                await ExecuteAsync(sql, cancellationToken,
                    ("token_hash", session.TokenHash, NpgsqlDbType.Text),
                    ("account_id", session.AccountId, NpgsqlDbType.Text),
                    ("created_at", session.CreatedAt, NpgsqlDbType.TimestampTz),
                    ("expires_at", session.ExpiresAt, NpgsqlDbType.TimestampTz),
                    ("revoked_at", session.RevokedAt, NpgsqlDbType.TimestampTz)).ConfigureAwait(false);
            }

            public async Task<LobbyRoom?> FindRoomForUpdateAsync(
                string roomId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT room_id, title, visibility, state, allow_spectators, max_players, ruleset_version,
       host_account_id, created_at, updated_at, closed_at
FROM lobby.room WHERE room_id = @room_id FOR UPDATE";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "room_id", roomId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadRoom(reader, 0);
            }

            public async Task InsertRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.room
    (room_id, title, visibility, state, allow_spectators, max_players, ruleset_version,
     host_account_id, created_at, updated_at, closed_at)
VALUES (@room_id, @title, @visibility, @state, @allow_spectators, @max_players, @ruleset_version,
        @host_account_id, @created_at, @updated_at, @closed_at)";
                await ExecuteAsync(sql, cancellationToken,
                    ("room_id", room.RoomId, NpgsqlDbType.Text),
                    ("title", room.Title, NpgsqlDbType.Text),
                    ("visibility", room.Visibility == LobbyRoomVisibility.Public ? "PUBLIC" : "PRIVATE", NpgsqlDbType.Text),
                    ("state", room.State == LobbyRoomState.Open ? "OPEN" : "CLOSED", NpgsqlDbType.Text),
                    ("allow_spectators", room.AllowSpectators, NpgsqlDbType.Boolean),
                    ("max_players", room.MaxPlayers, NpgsqlDbType.Integer),
                    ("ruleset_version", (long)room.RulesetVersion, NpgsqlDbType.Bigint),
                    ("host_account_id", room.HostAccountId, NpgsqlDbType.Text),
                    ("created_at", room.CreatedAt, NpgsqlDbType.TimestampTz),
                    ("updated_at", room.UpdatedAt, NpgsqlDbType.TimestampTz),
                    ("closed_at", room.ClosedAt, NpgsqlDbType.TimestampTz)).ConfigureAwait(false);
            }

            public async Task UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
            {
                const string sql = @"
UPDATE lobby.room
SET state = @state, updated_at = @updated_at, closed_at = @closed_at
WHERE room_id = @room_id";
                await ExecuteAsync(sql, cancellationToken,
                    ("state", room.State == LobbyRoomState.Open ? "OPEN" : "CLOSED", NpgsqlDbType.Text),
                    ("updated_at", room.UpdatedAt, NpgsqlDbType.TimestampTz),
                    ("closed_at", room.ClosedAt, NpgsqlDbType.TimestampTz),
                    ("room_id", room.RoomId, NpgsqlDbType.Text)).ConfigureAwait(false);
            }

            public async Task<int> CancelActiveAssignmentsForRoomAsync(
                string roomId,
                DateTimeOffset now,
                CancellationToken cancellationToken)
            {
                const string sql = @"
UPDATE lobby.assignment
SET status = 'CANCELLED', updated_at = @now, expires_at = NULL
WHERE room_id = @room_id AND status IN ('RESERVED', 'CONFIRMED')";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "now", now, NpgsqlDbType.TimestampTz);
                Add(command, "room_id", roomId, NpgsqlDbType.Text);
                int cancelled = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                // A terminal room must also release match reservations whose
                // assignments were just cancelled; otherwise the global match
                // placement would still block joining another room.
                const string cancelMatches = @"
UPDATE lobby.match_ticket
SET status = 'CANCELLED', cancelled_at = @now
WHERE status = 'RESERVED'
  AND (
      room_id = @room_id OR
      assignment_id IN (
          SELECT assignment_id FROM lobby.assignment WHERE room_id = @room_id
      )
  )";
                await using var matchCommand = new NpgsqlCommand(cancelMatches, _connection, _transaction);
                Add(matchCommand, "now", now, NpgsqlDbType.TimestampTz);
                Add(matchCommand, "room_id", roomId, NpgsqlDbType.Text);
                await matchCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return cancelled;
            }

            public async Task<LobbyAssignment?> FindAssignmentForUpdateAsync(
                string assignmentId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT assignment_id, room_id, account_id, role, seat_id, status, created_at, updated_at,
       expires_at, confirmed_at, table_confirmed_at, match_ticket_id
FROM lobby.assignment WHERE assignment_id = @assignment_id FOR UPDATE";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "assignment_id", assignmentId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadAssignment(reader, 0);
            }

            public async Task<LobbyAssignment?> FindAnyActiveAssignmentForAccountAsync(
                string accountId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT assignment_id, room_id, account_id, role, seat_id, status, created_at, updated_at,
       expires_at, confirmed_at, table_confirmed_at, match_ticket_id
FROM lobby.assignment
WHERE account_id = @account_id AND status IN ('RESERVED', 'CONFIRMED')
ORDER BY updated_at DESC LIMIT 1";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "account_id", accountId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadAssignment(reader, 0);
            }

            public async Task<IReadOnlyList<LobbyAssignment>> ListActiveAssignmentsForAccountAsync(
                string accountId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT assignment_id, room_id, account_id, role, seat_id, status, created_at, updated_at,
       expires_at, confirmed_at, table_confirmed_at, match_ticket_id
FROM lobby.assignment
WHERE account_id = @account_id AND status IN ('RESERVED', 'CONFIRMED')
ORDER BY created_at";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "account_id", accountId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                var items = new List<LobbyAssignment>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(ReadAssignment(reader, 0));
                }

                return items;
            }

            public async Task<IReadOnlyList<LobbyAssignment>> ListAssignmentsAsync(
                string roomId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT assignment_id, room_id, account_id, role, seat_id, status, created_at, updated_at,
       expires_at, confirmed_at, table_confirmed_at, match_ticket_id
FROM lobby.assignment WHERE room_id = @room_id
ORDER BY seat_id NULLS LAST, created_at";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "room_id", roomId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                var items = new List<LobbyAssignment>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(ReadAssignment(reader, 0));
                }

                return items;
            }

            public async Task<int> CountActiveAssignmentsAsync(
                string roomId,
                LobbyRole role,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT count(*)::int FROM lobby.assignment
WHERE room_id = @room_id AND role = @role AND status IN ('RESERVED', 'CONFIRMED')";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "room_id", roomId, NpgsqlDbType.Text);
                Add(command, "role", role == LobbyRole.Player ? "PLAYER" : "SPECTATOR", NpgsqlDbType.Text);
                object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return value is int count ? count : 0;
            }

            public async Task InsertAssignmentAsync(
                LobbyAssignment assignment,
                CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.assignment
    (assignment_id, room_id, account_id, role, seat_id, status, created_at, updated_at,
     expires_at, confirmed_at, table_confirmed_at, match_ticket_id)
VALUES (@assignment_id, @room_id, @account_id, @role, @seat_id, @status, @created_at, @updated_at,
        @expires_at, @confirmed_at, @table_confirmed_at, @match_ticket_id)";
                await ExecuteAsync(sql, cancellationToken,
                    ("assignment_id", assignment.AssignmentId, NpgsqlDbType.Text),
                    ("room_id", assignment.RoomId, NpgsqlDbType.Text),
                    ("account_id", assignment.AccountId, NpgsqlDbType.Text),
                    ("role", assignment.Role == LobbyRole.Player ? "PLAYER" : "SPECTATOR", NpgsqlDbType.Text),
                    ("seat_id", assignment.SeatId, NpgsqlDbType.Integer),
                    ("status", StatusText(assignment.Status), NpgsqlDbType.Text),
                    ("created_at", assignment.CreatedAt, NpgsqlDbType.TimestampTz),
                    ("updated_at", assignment.UpdatedAt, NpgsqlDbType.TimestampTz),
                    ("expires_at", assignment.ExpiresAt, NpgsqlDbType.TimestampTz),
                    ("confirmed_at", assignment.ConfirmedAt, NpgsqlDbType.TimestampTz),
                    ("table_confirmed_at", assignment.TableConfirmedAt, NpgsqlDbType.TimestampTz),
                    ("match_ticket_id", assignment.MatchTicketId, NpgsqlDbType.Text)).ConfigureAwait(false);
            }

            public async Task UpdateAssignmentAsync(
                LobbyAssignment assignment,
                CancellationToken cancellationToken)
            {
                const string sql = @"
UPDATE lobby.assignment SET
    role = @role, seat_id = @seat_id, status = @status, updated_at = @updated_at,
    expires_at = @expires_at, confirmed_at = @confirmed_at,
    table_confirmed_at = @table_confirmed_at, match_ticket_id = @match_ticket_id
WHERE assignment_id = @assignment_id";
                await ExecuteAsync(sql, cancellationToken,
                    ("role", assignment.Role == LobbyRole.Player ? "PLAYER" : "SPECTATOR", NpgsqlDbType.Text),
                    ("seat_id", assignment.SeatId, NpgsqlDbType.Integer),
                    ("status", StatusText(assignment.Status), NpgsqlDbType.Text),
                    ("updated_at", assignment.UpdatedAt, NpgsqlDbType.TimestampTz),
                    ("expires_at", assignment.ExpiresAt, NpgsqlDbType.TimestampTz),
                    ("confirmed_at", assignment.ConfirmedAt, NpgsqlDbType.TimestampTz),
                    ("table_confirmed_at", assignment.TableConfirmedAt, NpgsqlDbType.TimestampTz),
                    ("match_ticket_id", assignment.MatchTicketId, NpgsqlDbType.Text),
                    ("assignment_id", assignment.AssignmentId, NpgsqlDbType.Text)).ConfigureAwait(false);
            }

            public async Task<LobbyMatchTicket?> FindMatchTicketAsync(
                string matchTicketId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT match_ticket_id, account_id, mode, status, ruleset_version, region, created_at, expires_at,
       reserved_at, confirmed_at, cancelled_at, assignment_id, room_id
FROM lobby.match_ticket WHERE match_ticket_id = @id";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "id", matchTicketId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadMatchTicket(reader, 0);
            }

            public async Task<LobbyMatchTicket?> FindMatchTicketForUpdateAsync(
                string matchTicketId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT match_ticket_id, account_id, mode, status, ruleset_version, region, created_at, expires_at,
       reserved_at, confirmed_at, cancelled_at, assignment_id, room_id
FROM lobby.match_ticket WHERE match_ticket_id = @id FOR UPDATE";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "id", matchTicketId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadMatchTicket(reader, 0);
            }

            public async Task<LobbyMatchTicket?> FindActiveMatchTicketForAccountAsync(
                string accountId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT match_ticket_id, account_id, mode, status, ruleset_version, region, created_at, expires_at,
       reserved_at, confirmed_at, cancelled_at, assignment_id, room_id
FROM lobby.match_ticket
WHERE account_id = @account_id AND status IN ('QUEUED', 'RESERVED')
ORDER BY created_at LIMIT 1";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "account_id", accountId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadMatchTicket(reader, 0);
            }

            public async Task<IReadOnlyList<LobbyMatchTicket>> ListQueuedMatchTicketsForUpdateAsync(
                LobbyMatchMode mode,
                uint rulesetVersion,
                string? region,
                int limit,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT match_ticket_id, account_id, mode, status, ruleset_version, region, created_at, expires_at,
       reserved_at, confirmed_at, cancelled_at, assignment_id, room_id
FROM lobby.match_ticket
WHERE status = 'QUEUED' AND mode = @mode AND ruleset_version = @ruleset
  AND (@region IS NULL OR region = @region)
ORDER BY created_at, match_ticket_id
LIMIT @limit
FOR UPDATE SKIP LOCKED";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "mode", mode == LobbyMatchMode.Casual ? "CASUAL" : "RANKED", NpgsqlDbType.Text);
                Add(command, "ruleset", (long)rulesetVersion, NpgsqlDbType.Bigint);
                Add(command, "region", region, NpgsqlDbType.Text);
                Add(command, "limit", limit, NpgsqlDbType.Integer);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                var items = new List<LobbyMatchTicket>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(ReadMatchTicket(reader, 0));
                }

                return items;
            }

            public async Task InsertMatchTicketAsync(
                LobbyMatchTicket ticket,
                CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.match_ticket
    (match_ticket_id, account_id, mode, status, ruleset_version, region, created_at, expires_at,
     reserved_at, confirmed_at, cancelled_at, assignment_id, room_id)
VALUES (@id, @account_id, @mode, @status, @ruleset, @region, @created_at, @expires_at,
        @reserved_at, @confirmed_at, @cancelled_at, @assignment_id, @room_id)";
                await ExecuteAsync(sql, cancellationToken,
                    ("id", ticket.MatchTicketId, NpgsqlDbType.Text),
                    ("account_id", ticket.AccountId, NpgsqlDbType.Text),
                    ("mode", ticket.Mode == LobbyMatchMode.Casual ? "CASUAL" : "RANKED", NpgsqlDbType.Text),
                    ("status", MatchStatusText(ticket.Status), NpgsqlDbType.Text),
                    ("ruleset", (long)ticket.RulesetVersion, NpgsqlDbType.Bigint),
                    ("region", ticket.Region, NpgsqlDbType.Text),
                    ("created_at", ticket.CreatedAt, NpgsqlDbType.TimestampTz),
                    ("expires_at", ticket.ExpiresAt, NpgsqlDbType.TimestampTz),
                    ("reserved_at", ticket.ReservedAt, NpgsqlDbType.TimestampTz),
                    ("confirmed_at", ticket.ConfirmedAt, NpgsqlDbType.TimestampTz),
                    ("cancelled_at", ticket.CancelledAt, NpgsqlDbType.TimestampTz),
                    ("assignment_id", ticket.AssignmentId, NpgsqlDbType.Text),
                    ("room_id", ticket.RoomId, NpgsqlDbType.Text)).ConfigureAwait(false);
            }

            public async Task UpdateMatchTicketAsync(
                LobbyMatchTicket ticket,
                CancellationToken cancellationToken)
            {
                const string sql = @"
UPDATE lobby.match_ticket SET
    status = @status, expires_at = @expires_at, reserved_at = @reserved_at,
    confirmed_at = @confirmed_at, cancelled_at = @cancelled_at,
    assignment_id = @assignment_id, room_id = @room_id
WHERE match_ticket_id = @id";
                await ExecuteAsync(sql, cancellationToken,
                    ("status", MatchStatusText(ticket.Status), NpgsqlDbType.Text),
                    ("expires_at", ticket.ExpiresAt, NpgsqlDbType.TimestampTz),
                    ("reserved_at", ticket.ReservedAt, NpgsqlDbType.TimestampTz),
                    ("confirmed_at", ticket.ConfirmedAt, NpgsqlDbType.TimestampTz),
                    ("cancelled_at", ticket.CancelledAt, NpgsqlDbType.TimestampTz),
                    ("assignment_id", ticket.AssignmentId, NpgsqlDbType.Text),
                    ("room_id", ticket.RoomId, NpgsqlDbType.Text),
                    ("id", ticket.MatchTicketId, NpgsqlDbType.Text)).ConfigureAwait(false);
            }

            public async Task<LobbyTableTicketRecord?> FindTableTicketForUpdateAsync(
                string jti,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT jti, assignment_id, account_id, room_id, role, seat_id, issued_at, expires_at, kid,
       consumed_at, superseded_at
FROM lobby.table_ticket WHERE jti = @jti FOR UPDATE";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "jti", jti, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadTableTicket(reader, 0);
            }

            public async Task<LobbyTableTicketRecord?> FindActiveTableTicketForAssignmentAsync(
                string assignmentId,
                CancellationToken cancellationToken)
            {
                const string sql = @"
SELECT jti, assignment_id, account_id, room_id, role, seat_id, issued_at, expires_at, kid,
       consumed_at, superseded_at
FROM lobby.table_ticket
WHERE assignment_id = @assignment_id AND consumed_at IS NULL AND superseded_at IS NULL
ORDER BY issued_at DESC LIMIT 1
FOR UPDATE";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "assignment_id", assignmentId, NpgsqlDbType.Text);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadTableTicket(reader, 0);
            }

            public async Task InsertTableTicketAsync(
                LobbyTableTicketRecord ticket,
                CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.table_ticket
    (jti, assignment_id, account_id, room_id, role, seat_id, issued_at, expires_at, kid,
     consumed_at, superseded_at)
VALUES (@jti, @assignment_id, @account_id, @room_id, @role, @seat_id, @issued_at, @expires_at, @kid,
        @consumed_at, @superseded_at)";
                await ExecuteAsync(sql, cancellationToken,
                    ("jti", ticket.Jti, NpgsqlDbType.Text),
                    ("assignment_id", ticket.AssignmentId, NpgsqlDbType.Text),
                    ("account_id", ticket.AccountId, NpgsqlDbType.Text),
                    ("room_id", ticket.RoomId, NpgsqlDbType.Text),
                    ("role", ticket.Role == LobbyRole.Player ? "PLAYER" : "SPECTATOR", NpgsqlDbType.Text),
                    ("seat_id", ticket.SeatId, NpgsqlDbType.Integer),
                    ("issued_at", ticket.IssuedAt, NpgsqlDbType.TimestampTz),
                    ("expires_at", ticket.ExpiresAt, NpgsqlDbType.TimestampTz),
                    ("kid", ticket.Kid, NpgsqlDbType.Text),
                    ("consumed_at", ticket.ConsumedAt, NpgsqlDbType.TimestampTz),
                    ("superseded_at", ticket.SupersededAt, NpgsqlDbType.TimestampTz)).ConfigureAwait(false);
            }

            public async Task UpdateTableTicketAsync(
                LobbyTableTicketRecord ticket,
                CancellationToken cancellationToken)
            {
                const string sql = @"
UPDATE lobby.table_ticket
SET consumed_at = @consumed_at, superseded_at = @superseded_at
WHERE jti = @jti";
                await ExecuteAsync(sql, cancellationToken,
                    ("consumed_at", ticket.ConsumedAt, NpgsqlDbType.TimestampTz),
                    ("superseded_at", ticket.SupersededAt, NpgsqlDbType.TimestampTz),
                    ("jti", ticket.Jti, NpgsqlDbType.Text)).ConfigureAwait(false);
            }

            public async Task InsertTicketConsumptionAsync(
                LobbyTicketConsumption consumption,
                CancellationToken cancellationToken)
            {
                const string sql = @"
INSERT INTO lobby.table_ticket_consumption
    (jti, assignment_id, account_id, room_id, seat_id, consumed_at)
VALUES (@jti, @assignment_id, @account_id, @room_id, @seat_id, @consumed_at)";
                await ExecuteAsync(sql, cancellationToken,
                    ("jti", consumption.Jti, NpgsqlDbType.Text),
                    ("assignment_id", consumption.AssignmentId, NpgsqlDbType.Text),
                    ("account_id", consumption.AccountId, NpgsqlDbType.Text),
                    ("room_id", consumption.RoomId, NpgsqlDbType.Text),
                    ("seat_id", consumption.SeatId, NpgsqlDbType.Integer),
                    ("consumed_at", consumption.ConsumedAt, NpgsqlDbType.TimestampTz)).ConfigureAwait(false);
            }

            public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
            {
                const string sql = @"
WITH expired_assignments AS (
    UPDATE lobby.assignment
    SET status = 'EXPIRED', updated_at = @now, expires_at = NULL
    WHERE status = 'RESERVED' AND expires_at IS NOT NULL AND expires_at <= @now
    RETURNING 1
), expired_matches AS (
    UPDATE lobby.match_ticket
    SET status = 'EXPIRED'
    WHERE status IN ('QUEUED', 'RESERVED') AND expires_at <= @now
    RETURNING 1
)
SELECT (SELECT count(*) FROM expired_assignments) + (SELECT count(*) FROM expired_matches)";
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                Add(command, "now", now, NpgsqlDbType.TimestampTz);
                object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return value is long count ? (int)count : 0;
            }

            private async Task ExecuteAsync(
                string sql,
                CancellationToken cancellationToken,
                params (string Name, object? Value, NpgsqlDbType Type)[] parameters)
            {
                await using var command = new NpgsqlCommand(sql, _connection, _transaction);
                foreach ((string name, object? value, NpgsqlDbType type) in parameters)
                {
                    Add(command, name, value, type);
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            private static LobbyAssignment ReadAssignment(NpgsqlDataReader reader, int offset)
            {
                return new LobbyAssignment(
                    reader.GetString(offset),
                    reader.GetString(offset + 1),
                    reader.GetString(offset + 2),
                    string.Equals(reader.GetString(offset + 3), "PLAYER", StringComparison.Ordinal)
                        ? LobbyRole.Player
                        : LobbyRole.Spectator,
                    reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
                    reader.GetString(offset + 5) switch
                    {
                        "RESERVED" => LobbyAssignmentStatus.Reserved,
                        "CONFIRMED" => LobbyAssignmentStatus.Confirmed,
                        "CANCELLED" => LobbyAssignmentStatus.Cancelled,
                        "EXPIRED" => LobbyAssignmentStatus.Expired,
                        _ => LobbyAssignmentStatus.Unspecified
                    },
                    ReadTimestamp(reader, offset + 6),
                    ReadTimestamp(reader, offset + 7),
                    ReadNullableTimestamp(reader, offset + 8),
                    ReadNullableTimestamp(reader, offset + 9),
                    ReadNullableTimestamp(reader, offset + 10),
                    reader.IsDBNull(offset + 11) ? null : reader.GetString(offset + 11));
            }

            private static LobbyTableTicketRecord ReadTableTicket(NpgsqlDataReader reader, int offset)
            {
                return new LobbyTableTicketRecord(
                    reader.GetString(offset),
                    reader.GetString(offset + 1),
                    reader.GetString(offset + 2),
                    reader.GetString(offset + 3),
                    string.Equals(reader.GetString(offset + 4), "PLAYER", StringComparison.Ordinal)
                        ? LobbyRole.Player
                        : LobbyRole.Spectator,
                    reader.IsDBNull(offset + 5) ? null : reader.GetInt32(offset + 5),
                    ReadTimestamp(reader, offset + 6),
                    ReadTimestamp(reader, offset + 7),
                    reader.GetString(offset + 8),
                    ReadNullableTimestamp(reader, offset + 9),
                    ReadNullableTimestamp(reader, offset + 10));
            }

            private static string StatusText(LobbyAssignmentStatus status)
            {
                return status switch
                {
                    LobbyAssignmentStatus.Reserved => "RESERVED",
                    LobbyAssignmentStatus.Confirmed => "CONFIRMED",
                    LobbyAssignmentStatus.Cancelled => "CANCELLED",
                    LobbyAssignmentStatus.Expired => "EXPIRED",
                    _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Assignment status is not persistable.")
                };
            }

            private static string MatchStatusText(LobbyMatchStatus status)
            {
                return status switch
                {
                    LobbyMatchStatus.Queued => "QUEUED",
                    LobbyMatchStatus.Reserved => "RESERVED",
                    LobbyMatchStatus.Confirmed => "CONFIRMED",
                    LobbyMatchStatus.Cancelled => "CANCELLED",
                    LobbyMatchStatus.Expired => "EXPIRED",
                    _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Match status is not persistable.")
                };
            }
        }
    }
}

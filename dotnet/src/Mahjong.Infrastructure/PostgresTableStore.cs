using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.TableRuntime;
using Npgsql;
using NpgsqlTypes;

namespace Mahjong.Infrastructure
{
    /// <summary>
    /// Real PostgreSQL implementation of the frozen P3.4 durable ports. All logical
    /// faces share one database and one transaction implementation:
    /// lease fence -> state CAS -> scope row lock -> events -> command journal ->
    /// receipt -> terminal outbox -> optional checkpoint. A commit is never
    /// reported as applied before PostgreSQL commits; publication is the caller's
    /// post-return step.
    /// </summary>
    public sealed partial class PostgresTableStore :
        ITableStore,
        IReceiptStore,
        ITableLease,
        ISettlementOutbox,
        IRequestScopeAuthority,
        ITableTransactionalScopeReader,
        IAsyncDisposable
    {
        private const string RoomLockSql =
            "SELECT pg_advisory_xact_lock(hashtextextended(@room_id, 0))";

        private readonly NpgsqlDataSource _dataSource;
        private readonly PostgresTableOptions _options;
        private readonly PostgresPayloadProtector _protector;
        private bool _disposed;

        /// <summary>
        /// Test-only seam used to prove that a committed transaction whose response is
        /// lost is reported as OUTCOME_UNKNOWN, never as a clean failure. It is
        /// internal and never wired by production composition.
        /// </summary>
        internal Action? AfterCommitBeforeReturnForTest { get; set; }

        public PostgresTableStore(PostgresTableOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _protector = new PostgresPayloadProtector(options.CopyPayloadKey());
            var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            builder.ConnectionStringBuilder.MaxPoolSize = options.MaxPoolSize;
            builder.ConnectionStringBuilder.ApplicationName = "mahjong-table-p52";
            _dataSource = builder.Build();
        }

        public string ProviderName => "PostgreSQL (table_store schema)";

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _protector.Dispose();
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PostgresTableStore));
            }
        }

        private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
        {
            return new NpgsqlCommand(sql, connection, transaction)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
        }

        private static void Add(
            NpgsqlCommand command,
            string name,
            object? value,
            NpgsqlDbType type)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        }

        private static void AddText(NpgsqlCommand command, string name, string? value) =>
            Add(command, name, value, NpgsqlDbType.Text);

        private static void AddBigint(NpgsqlCommand command, string name, long value) =>
            Add(command, name, value, NpgsqlDbType.Bigint);

        private static void AddNullableBigint(NpgsqlCommand command, string name, long? value) =>
            Add(command, name, value, NpgsqlDbType.Bigint);

        private static void AddBytea(NpgsqlCommand command, string name, byte[]? value) =>
            Add(command, name, value, NpgsqlDbType.Bytea);

        private static void AddBool(NpgsqlCommand command, string name, bool value) =>
            Add(command, name, value, NpgsqlDbType.Boolean);

        private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset value) =>
            Add(command, name, value, NpgsqlDbType.TimestampTz);

        private static void AddNumeric(NpgsqlCommand command, string name, decimal value) =>
            Add(command, name, value, NpgsqlDbType.Numeric);

        private static TablePayloadSha256 Hash(byte[] value) => new TablePayloadSha256(value);

        private static TablePayloadSha256 ReadHash(NpgsqlDataReader reader, int ordinal)
        {
            byte[] bytes = reader.GetFieldValue<byte[]>(ordinal);
            if (bytes.Length != TablePayloadSha256.SizeBytes)
            {
                throw new InvalidDataException("Stored SHA-256 digest has an invalid length.");
            }

            return new TablePayloadSha256(bytes);
        }

        private static ulong ReadULong(NpgsqlDataReader reader, int ordinal)
        {
            decimal value = reader.GetFieldValue<decimal>(ordinal);
            if (value < 0M || value > ulong.MaxValue)
            {
                throw new InvalidDataException("Stored unsigned 64-bit value is out of range.");
            }

            return (ulong)value;
        }

        private static decimal ToNumeric(ulong value) => (decimal)value;

        private static string StatusText(ReceiptStatus status)
        {
            switch (status)
            {
                case ReceiptStatus.Applied:
                    return "APPLIED";
                case ReceiptStatus.Recorded:
                    return "RECORDED";
                case ReceiptStatus.Rejected:
                    return "REJECTED";
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Only definitive receipts are stored.");
            }
        }

        private static ReceiptStatus ParseStatus(string value, long evaluated, long? command, long? seq, string? reason)
        {
            switch (value)
            {
                case "APPLIED":
                    return ReceiptStatus.Applied;
                case "RECORDED":
                    return ReceiptStatus.Recorded;
                case "REJECTED":
                    return ReceiptStatus.Rejected;
                default:
                    throw new InvalidDataException("Stored receipt status is invalid.");
            }
        }

        private static Receipt RestoreReceipt(
            string value,
            string? reason,
            StateVersion evaluated,
            long? command,
            long? seq,
            ulong? committedAtMs)
        {
            StateVersion? commandVersion = command.HasValue ? new StateVersion((ulong)command.Value) : (StateVersion?)null;
            EventSeq? eventSeq = seq.HasValue ? new EventSeq((ulong)seq.Value) : (EventSeq?)null;
            switch (value)
            {
                case "APPLIED":
                    return Receipt.Applied(evaluated, commandVersion!.Value, eventSeq!.Value, reason, committedAtMs);
                case "RECORDED":
                    return Receipt.Recorded(evaluated, commandVersion!.Value, eventSeq!.Value, reason, committedAtMs);
                case "REJECTED":
                    return Receipt.Rejected(evaluated, reason ?? string.Empty);
                default:
                    throw new InvalidDataException("Stored receipt status is invalid.");
            }
        }

        private static TableScopeRole ParseRole(string value)
        {
            switch (value)
            {
                case "PLAYER":
                    return TableScopeRole.Player;
                case "SPECTATOR":
                    return TableScopeRole.Spectator;
                case "LOBBY":
                    return TableScopeRole.Lobby;
                default:
                    throw new InvalidDataException("Stored scope role is invalid.");
            }
        }

        private static string RoleText(TableScopeRole role)
        {
            switch (role)
            {
                case TableScopeRole.Player:
                    return "PLAYER";
                case TableScopeRole.Spectator:
                    return "SPECTATOR";
                case TableScopeRole.Lobby:
                    return "LOBBY";
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown scope role.");
            }
        }

        private static DateTimeOffset FromMs(long milliseconds)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new InvalidDataException("Stored UTC millisecond timestamp is out of range.");
            }
        }

        private static long FromTimestamp(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

        private static long ReadTimestampMs(NpgsqlDataReader reader, int ordinal)
        {
            DateTimeOffset value = reader.GetFieldValue<DateTimeOffset>(ordinal);
            return value.ToUnixTimeMilliseconds();
        }

        private static long? ReadNullableTimestampMs(NpgsqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (long?)null : ReadTimestampMs(reader, ordinal);
        }

        private static string SafeDatabaseDetail(Exception exception)
        {
            if (exception is PostgresException postgres)
            {
                return "PostgreSQL error " + postgres.SqlState + ".";
            }

            if (exception is NpgsqlException)
            {
                return "PostgreSQL operation failed.";
            }

            if (exception is TimeoutException)
            {
                return "PostgreSQL operation timed out.";
            }

            return exception.GetType().Name + ".";
        }

        private static string Aad(string purpose, string key) => "mahjong-table-store|1|" + purpose + "|" + key;

        private sealed class ScopeRow
        {
            internal ScopeRow(
                string scopeId,
                string accountId,
                TableScopeRole role,
                string? roomId,
                ulong issuedAt,
                ulong expiresAt,
                ulong? revokedAt)
            {
                ScopeId = scopeId;
                AccountId = accountId;
                Role = role;
                RoomId = roomId;
                IssuedAt = issuedAt;
                ExpiresAt = expiresAt;
                RevokedAt = revokedAt;
            }

            internal string ScopeId { get; }
            internal string AccountId { get; }
            internal TableScopeRole Role { get; }
            internal string? RoomId { get; }
            internal ulong IssuedAt { get; }
            internal ulong ExpiresAt { get; }
            internal ulong? RevokedAt { get; }

            internal TableScopeRecord ToRecord() => new TableScopeRecord(
                new RequestScopeId(ScopeId),
                AccountId,
                Role,
                RoomId,
                IssuedAt,
                ExpiresAt,
                RevokedAt);
        }
    }
}

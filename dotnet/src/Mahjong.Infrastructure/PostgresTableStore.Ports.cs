using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.TableRuntime;
using Npgsql;
using NpgsqlTypes;

namespace Mahjong.Infrastructure
{
    public sealed partial class PostgresTableStore
    {
        // ---------------------------------------------------------------------
        // IRequestScopeAuthority / host scope issuance helpers.
        // ---------------------------------------------------------------------

        public async ValueTask<TableScopeLookup> LookupAsync(
            RequestScopeId scopeId,
            CancellationToken cancellationToken)
        {
            if (scopeId.IsEmpty)
            {
                return TableScopeLookup.Missing();
            }

            ScopeRow? row = await ReadScopeAsync(scopeId, forShare: false, null, cancellationToken)
                .ConfigureAwait(false);
            return row == null
                ? TableScopeLookup.Missing()
                : TableScopeLookup.FromScope(row.ToRecord());
        }

        public async Task<TableScopeRecord> IssueScopeAsync(
            TableScopeRecord scope,
            CancellationToken cancellationToken = default)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
INSERT INTO table_store.request_scope
    (scope_id, account_id, role, room_id, issued_at_utc_ms, expires_at_utc_ms, revoked_at_utc_ms)
VALUES (@scope_id, @account_id, @role, @room_id, @issued_at, @expires_at, @revoked_at)
ON CONFLICT (scope_id) DO NOTHING";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "scope_id", scope.ScopeId.Value);
            AddText(command, "account_id", scope.AccountId);
            AddText(command, "role", RoleText(scope.Role));
            AddText(command, "room_id", scope.RoomId);
            Add(command, "issued_at", checked((long)scope.IssuedAtUtcMs), NpgsqlDbType.Bigint);
            Add(command, "expires_at", checked((long)scope.ExpiresAtUtcMs), NpgsqlDbType.Bigint);
            AddNullableBigint(command, "revoked_at", scope.RevokedAtUtcMs.HasValue
                ? checked((long)scope.RevokedAtUtcMs.Value)
                : (long?)null);
            int inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted != 1)
            {
                throw new InvalidOperationException("Scope id already exists.");
            }

            return scope;
        }

        public async Task<bool> RevokeScopeAsync(
            RequestScopeId scopeId,
            ulong revokedAtUtcMs,
            CancellationToken cancellationToken = default)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
UPDATE table_store.request_scope
   SET revoked_at_utc_ms = COALESCE(revoked_at_utc_ms, @revoked_at)
 WHERE scope_id = @scope_id";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "scope_id", scopeId.Value);
            Add(command, "revoked_at", checked((long)revokedAtUtcMs), NpgsqlDbType.Bigint);
            int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return rows == 1;
        }

        public async Task<TableScopeRecord?> ReadScopeAsync(
            RequestScopeId scopeId,
            CancellationToken cancellationToken = default)
        {
            ScopeRow? row = await ReadScopeAsync(scopeId, forShare: false, null, cancellationToken)
                .ConfigureAwait(false);
            return row?.ToRecord();
        }

        private async ValueTask<ScopeRow?> ReadScopeAsync(
            RequestScopeId scopeId,
            bool forShare,
            NpgsqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            NpgsqlConnection? owned = null;
            NpgsqlConnection connection;
            if (transaction == null)
            {
                owned = await OpenAsync(cancellationToken).ConfigureAwait(false);
                connection = owned;
            }
            else
            {
                connection = transaction.Connection!;
            }

            try
            {
                string sql = @"
SELECT scope_id, account_id, role, room_id, issued_at_utc_ms, expires_at_utc_ms, revoked_at_utc_ms
  FROM table_store.request_scope
 WHERE scope_id = @scope_id" + (forShare ? " FOR SHARE" : string.Empty);
                await using NpgsqlCommand command = Command(sql, connection, transaction);
                AddText(command, "scope_id", scopeId.Value);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new ScopeRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    ParseRole(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    checked((ulong)reader.GetInt64(4)),
                    checked((ulong)reader.GetInt64(5)),
                    reader.IsDBNull(6) ? (ulong?)null : checked((ulong)reader.GetInt64(6)));
            }
            finally
            {
                if (owned != null)
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Standalone transactional scope read used by contract tests. The commit path
        /// itself re-reads the scope row FOR SHARE inside its own transaction; this
        /// method is not the production linearization point.
        /// </summary>
        public TableScopeReadLease BeginScopeForCommit(RequestScopeId scopeId)
        {
            ThrowIfDisposed();
            NpgsqlConnection connection = _dataSource.OpenConnection();
            NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                using NpgsqlCommand command = Command(
                    @"SELECT scope_id, account_id, role, room_id, issued_at_utc_ms, expires_at_utc_ms, revoked_at_utc_ms
                        FROM table_store.request_scope
                       WHERE scope_id = @scope_id
                       FOR SHARE",
                    connection,
                    transaction);
                AddText(command, "scope_id", scopeId.Value);
                using NpgsqlDataReader reader = command.ExecuteReader();
                ScopeRow? row = null;
                if (reader.Read())
                {
                    row = new ScopeRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        ParseRole(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        checked((ulong)reader.GetInt64(4)),
                        checked((ulong)reader.GetInt64(5)),
                        reader.IsDBNull(6) ? (ulong?)null : checked((ulong)reader.GetInt64(6)));
                }

                reader.Dispose();
                return new TableScopeReadLease(row?.ToRecord(), () =>
                {
                    try
                    {
                        transaction.Commit();
                    }
                    finally
                    {
                        transaction.Dispose();
                        connection.Dispose();
                    }
                });
            }
            catch
            {
                transaction.Dispose();
                connection.Dispose();
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // ITableLease
        // ---------------------------------------------------------------------

        public async ValueTask<TableLeaseAcquireResult> AcquireAsync(
            string roomId,
            string ownerInstance,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
            }

            long ttlMs = checked((long)ttl.TotalMilliseconds);
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string upsert = @"
INSERT INTO table_store.table_lease
    (room_id, owner_instance, owner_epoch, lease_expires_at, updated_at)
VALUES (@room_id, @owner_instance, 1,
        clock_timestamp() + (@ttl_ms * interval '1 millisecond'), clock_timestamp())
ON CONFLICT (room_id) DO UPDATE
   SET owner_instance = EXCLUDED.owner_instance,
       owner_epoch = table_store.table_lease.owner_epoch + 1,
       lease_expires_at = EXCLUDED.lease_expires_at,
       updated_at = clock_timestamp()
 WHERE table_store.table_lease.lease_expires_at <= clock_timestamp()
    OR table_store.table_lease.owner_instance = EXCLUDED.owner_instance
RETURNING owner_instance, owner_epoch,
          (extract(epoch from lease_expires_at) * 1000)::bigint,
          (extract(epoch from updated_at) * 1000)::bigint";
            await using (NpgsqlCommand command = Command(upsert, connection))
            {
                AddText(command, "room_id", roomId);
                AddText(command, "owner_instance", ownerInstance);
                AddBigint(command, "ttl_ms", ttlMs);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var lease = new TableLeaseSnapshot(
                        roomId,
                        reader.GetString(0),
                        new OwnerEpoch(checked((ulong)reader.GetInt64(1))),
                        checked((ulong)reader.GetInt64(2)),
                        checked((ulong)reader.GetInt64(3)));
                    return TableLeaseAcquireResult.Success(lease);
                }
            }

            TableLeaseSnapshot? existing = await GetAsync(roomId, cancellationToken).ConfigureAwait(false);
            if (existing == null)
            {
                return TableLeaseAcquireResult.Failed("The table lease row does not exist for this room.");
            }

            return existing.IsExpiredAt((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                ? TableLeaseAcquireResult.Failed("The lease changed concurrently; retry the acquire.")
                : TableLeaseAcquireResult.Held(existing, "Another live owner holds the lease.");
        }

        public async ValueTask<TableLeaseRenewResult> RenewAsync(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedEpoch,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
            }

            long ttlMs = checked((long)ttl.TotalMilliseconds);
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
UPDATE table_store.table_lease
   SET lease_expires_at = clock_timestamp() + (@ttl_ms * interval '1 millisecond'),
       updated_at = clock_timestamp()
 WHERE room_id = @room_id
   AND owner_instance = @owner_instance
   AND owner_epoch = @owner_epoch
   AND lease_expires_at > clock_timestamp()
RETURNING owner_instance, owner_epoch,
          (extract(epoch from lease_expires_at) * 1000)::bigint,
          (extract(epoch from updated_at) * 1000)::bigint";
            await using (NpgsqlCommand command = Command(sql, connection))
            {
                AddText(command, "room_id", roomId);
                AddText(command, "owner_instance", ownerInstance);
                Add(command, "owner_epoch", checked((long)expectedEpoch.Value), NpgsqlDbType.Bigint);
                AddBigint(command, "ttl_ms", ttlMs);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return TableLeaseRenewResult.Success(new TableLeaseSnapshot(
                        roomId,
                        reader.GetString(0),
                        new OwnerEpoch(checked((ulong)reader.GetInt64(1))),
                        checked((ulong)reader.GetInt64(2)),
                        checked((ulong)reader.GetInt64(3))));
                }
            }

            TableLeaseSnapshot? existing = await GetAsync(roomId, cancellationToken).ConfigureAwait(false);
            if (existing != null &&
                string.Equals(existing.OwnerInstance, ownerInstance, StringComparison.Ordinal) &&
                existing.OwnerEpoch == expectedEpoch)
            {
                return TableLeaseRenewResult.Expired("The lease expired before renewal.");
            }

            return TableLeaseRenewResult.Stale("The lease is owned by another instance/epoch.");
        }

        public async ValueTask<TableLeaseSnapshot?> GetAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT owner_instance, owner_epoch,
       (extract(epoch from lease_expires_at) * 1000)::bigint,
       (extract(epoch from updated_at) * 1000)::bigint
  FROM table_store.table_lease
 WHERE room_id = @room_id";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", roomId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new TableLeaseSnapshot(
                roomId,
                reader.GetString(0),
                new OwnerEpoch(checked((ulong)reader.GetInt64(1))),
                checked((ulong)reader.GetInt64(2)),
                checked((ulong)reader.GetInt64(3)));
        }

        // ---------------------------------------------------------------------
        // IReceiptStore
        // ---------------------------------------------------------------------

        public async ValueTask<TableDurableReceipt?> TryGetAsync(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT room_id, api, payload_sha256, status, reason_code,
       evaluated_state_version, command_version, event_seq,
       (extract(epoch from created_at) * 1000)::bigint,
       (extract(epoch from expires_at) * 1000)::bigint,
       response_blob
  FROM table_store.request_receipt
 WHERE account_id = @account_id AND scope_id = @scope_id AND request_id = @request_id";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "account_id", accountId);
            AddText(command, "scope_id", scopeId.Value);
            AddText(command, "request_id", requestId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            string api = reader.GetString(1);
            var digest = ReadHash(reader, 2);
            string status = reader.GetString(3);
            string? reason = reader.IsDBNull(4) ? null : reader.GetString(4);
            var evaluated = new StateVersion(checked((ulong)reader.GetInt64(5)));
            long? commandVersion = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            long? eventSeq = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7);
            ulong createdAt = checked((ulong)reader.GetInt64(8));
            ulong expiresAt = checked((ulong)reader.GetInt64(9));
            byte[] response = reader.GetFieldValue<byte[]>(10);
            string detail = System.Text.Encoding.UTF8.GetString(
                _protector.Open(response, Aad("receipt", accountId + "|" + scopeId.Value + "|" + requestId.Value)));
            Receipt receipt = RestoreReceipt(status, reason, evaluated, commandVersion, eventSeq, createdAt);
            return new TableDurableReceipt(
                accountId,
                scopeId,
                requestId,
                api,
                digest,
                receipt,
                detail,
                createdAt,
                expiresAt);
        }

        // ---------------------------------------------------------------------
        // ISettlementOutbox
        // ---------------------------------------------------------------------

        public async ValueTask<TableSettlementOutboxRecord?> TryGetAsync(
            string matchId,
            int resultVersion,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT payload_blob, payload_sha256,
       (extract(epoch from created_at) * 1000)::bigint,
       CASE WHEN published_at IS NULL THEN NULL
            ELSE (extract(epoch from published_at) * 1000)::bigint END
  FROM table_store.settlement_outbox
 WHERE match_id = @match_id AND result_version = @result_version";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "match_id", matchId);
            Add(command, "result_version", resultVersion, NpgsqlDbType.Integer);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            byte[] payload = _protector.Open(
                reader.GetFieldValue<byte[]>(0),
                Aad("outbox", matchId + "|" + resultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var digest = ReadHash(reader, 1);
            ulong createdAt = checked((ulong)reader.GetInt64(2));
            ulong? publishedAt = reader.IsDBNull(3) ? (ulong?)null : checked((ulong)reader.GetInt64(3));
            return new TableSettlementOutboxRecord(matchId, resultVersion, payload, digest, createdAt, publishedAt);
        }

        public async ValueTask<IReadOnlyList<TableSettlementOutboxRecord>> ListUnpublishedAsync(
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT match_id, result_version, payload_blob, payload_sha256,
       (extract(epoch from created_at) * 1000)::bigint
  FROM table_store.settlement_outbox
 WHERE published_at IS NULL
 ORDER BY created_at, match_id, result_version";
            await using NpgsqlCommand command = Command(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = new List<TableSettlementOutboxRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string matchId = reader.GetString(0);
                int resultVersion = reader.GetInt32(1);
                byte[] payload = _protector.Open(
                    reader.GetFieldValue<byte[]>(2),
                    Aad("outbox", matchId + "|" + resultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                rows.Add(new TableSettlementOutboxRecord(
                    matchId,
                    resultVersion,
                    payload,
                    ReadHash(reader, 3),
                    checked((ulong)reader.GetInt64(4)),
                    null));
            }

            return rows;
        }

        public async ValueTask<bool> MarkPublishedAsync(
            string matchId,
            int resultVersion,
            ulong publishedAtUtcMs,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
UPDATE table_store.settlement_outbox
   SET published_at = COALESCE(published_at, @published_at)
 WHERE match_id = @match_id AND result_version = @result_version
RETURNING match_id";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "match_id", matchId);
            Add(command, "result_version", resultVersion, NpgsqlDbType.Integer);
            AddTimestamp(command, "published_at", FromMs(checked((long)publishedAtUtcMs)));
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null && result != DBNull.Value;
        }

        // ---------------------------------------------------------------------
        // Recovery audit
        // ---------------------------------------------------------------------

        public async ValueTask AppendRecoveryAuditAsync(
            TableRecoveryAuditRecord audit,
            CancellationToken cancellationToken)
        {
            if (audit == null)
            {
                throw new ArgumentNullException(nameof(audit));
            }

            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
INSERT INTO table_store.table_recovery_audit
    (room_id, kind, recorded_at_utc_ms, previous_boot_id, current_boot_id,
     downtime_millis, grace_millis, previous_owner_instance, previous_owner_epoch,
     current_owner_epoch, detail)
VALUES (@room_id, @kind, @recorded_at, @previous_boot, @current_boot,
        @downtime, @grace, @previous_owner, @previous_epoch, @current_epoch, @detail)";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", audit.RoomId);
            Add(command, "kind", (int)audit.Kind, NpgsqlDbType.Integer);
            Add(command, "recorded_at", checked((long)audit.RecordedAtUtcMs), NpgsqlDbType.Bigint);
            AddText(command, "previous_boot", audit.PreviousBootId);
            AddText(command, "current_boot", audit.CurrentBootId);
            AddBigint(command, "downtime", audit.DowntimeMillis);
            AddBigint(command, "grace", audit.GraceMillis);
            AddText(command, "previous_owner", audit.PreviousOwnerInstance);
            Add(command, "previous_epoch", checked((long)audit.PreviousOwnerEpoch.Value), NpgsqlDbType.Bigint);
            Add(command, "current_epoch", checked((long)audit.CurrentOwnerEpoch.Value), NpgsqlDbType.Bigint);
            AddText(command, "detail", audit.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IReadOnlyList<TableRecoveryAuditRecord>> LoadRecoveryAuditsAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT room_id, kind, recorded_at_utc_ms, previous_boot_id, current_boot_id,
       downtime_millis, grace_millis, previous_owner_instance, previous_owner_epoch,
       current_owner_epoch, detail
  FROM table_store.table_recovery_audit
 WHERE room_id = @room_id
 ORDER BY audit_id";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", roomId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = new List<TableRecoveryAuditRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new TableRecoveryAuditRecord(
                    reader.GetString(0),
                    (TableRecoveryAuditKind)reader.GetInt32(1),
                    checked((ulong)reader.GetInt64(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    new OwnerEpoch(checked((ulong)reader.GetInt64(8))),
                    new OwnerEpoch(checked((ulong)reader.GetInt64(9))),
                    reader.GetString(10)));
            }

            return rows;
        }
    }
}

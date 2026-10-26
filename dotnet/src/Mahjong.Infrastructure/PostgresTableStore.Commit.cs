using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Npgsql;
using NpgsqlTypes;

namespace Mahjong.Infrastructure
{
    public sealed partial class PostgresTableStore
    {
        public async ValueTask<TableDurableCommitResult> CommitAsync(
            TableDurableCommitRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Checkpoint != null &&
                !string.Equals(request.Checkpoint.RoomId, request.RoomId, StringComparison.Ordinal))
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Commit checkpoint room does not match the fenced request room.");
            }

            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            bool commitIssued = false;
            bool committed = false;
            try
            {
                await AcquireRoomLockAsync(connection, transaction, request.RoomId, cancellationToken)
                    .ConfigureAwait(false);

                if (!await RoomExistsAsync(connection, transaction, request.RoomId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointMissing,
                        "Room is not initialized.");
                }

                // 1) Receipt lookup precedes scope, lease and state. A committed receipt is
                // returned even after the scope is revoked or the lease moved on.
                if (request.Receipt != null)
                {
                    TableDurableReceipt? existing = await LoadReceiptForUpdateAsync(
                        connection,
                        transaction,
                        request.Receipt.AccountId,
                        request.Receipt.ScopeId,
                        request.Receipt.RequestId,
                        cancellationToken).ConfigureAwait(false);
                    if (existing != null)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return existing.Matches(request.Receipt.Api, request.Receipt.PayloadSha256)
                            ? TableDurableCommitResult.Replayed(existing)
                            : TableDurableCommitResult.Conflict(
                                "The same request id was used with a different API/payload.");
                    }
                }

                // 2) Scope recheck and row lock inside the transaction.
                if (!request.InternalCommand)
                {
                    TableDurableReceipt receipt = request.Receipt!;
                    ScopeRow? scope = await ReadScopeAsync(
                        receipt.ScopeId,
                        forShare: true,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    long scopeNow = await DbNowMsAsync(connection, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    TableDurableCommitResult? scopeFailure = ValidateScope(
                        scope,
                        request,
                        receipt,
                        scopeNow);
                    if (scopeFailure != null)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return scopeFailure;
                    }
                }

                // 3) Lease fence. The row update is itself conditional on a live expiry.
                TableLeaseSnapshot? lease = await LoadLeaseAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false);
                long now = await DbNowMsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (lease == null ||
                    !string.Equals(lease.OwnerInstance, request.OwnerInstance, StringComparison.Ordinal) ||
                    lease.OwnerEpoch != request.ExpectedOwnerEpoch ||
                    now < 0 || (ulong)now >= lease.ExpiresAtUtcMs)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleOwner(
                        "The table lease is not owned by this instance/epoch or has expired.");
                }

                const string leaseUpdate = @"
UPDATE table_store.table_lease
   SET updated_at = clock_timestamp()
 WHERE room_id = @room_id
   AND owner_instance = @owner_instance
   AND owner_epoch = @owner_epoch
   AND lease_expires_at > clock_timestamp()";
                await using (NpgsqlCommand command = Command(leaseUpdate, connection, transaction))
                {
                    AddText(command, "room_id", request.RoomId);
                    AddText(command, "owner_instance", request.OwnerInstance);
                    Add(command, "owner_epoch", checked((long)request.ExpectedOwnerEpoch.Value), NpgsqlDbType.Bigint);
                    int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (rows != 1)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.StaleOwner(
                            "The table lease expired before the transactional fence.");
                    }
                }

                // 4) State CAS is independent of the lease fence.
                TableStateRow? state = await LoadStateAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false);
                if (state == null || state.StateVersion != request.ExpectedStateVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleState("The expected state version is not current.");
                }

                bool rejected = request.Receipt != null &&
                                request.Receipt.Receipt.Status == ReceiptStatus.Rejected &&
                                !request.RejectedWithState;
                bool accepted = request.InternalCommand ||
                                request.RejectedWithState ||
                                (request.Receipt != null && request.Receipt.IsAccepted);

                if (!accepted && !rejected)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.InternalError,
                        "A commit batch must be accepted, rejected or internal.");
                }

                List<TableEventRecord>? newEvents = null;
                TableCommandJournalRecord? journal = null;
                TableCheckpoint? checkpoint = null;
                long nextTimingVersion = state.TimingVersion;
                if (accepted)
                {
                    if (request.Timing == null || request.TimingVersion != state.TimingVersion + 1L)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.StaleStateVersion,
                            "An accepted command requires the next timing snapshot/version.");
                    }

                    TableDurableCommitResult? timingFailure = ValidateAcceptedTiming(request);
                    if (timingFailure != null)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return timingFailure;
                    }

                    TableDurableCommitResult? eventsFailure = ValidateAndCopyEvents(
                        request,
                        state.LastEventSeq,
                        out newEvents);
                    if (eventsFailure != null)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return eventsFailure;
                    }

                    journal = request.CommandJournal;
                    if (journal == null)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.InternalError,
                            "Accepted batch has no command journal.");
                    }

                    if (journal.CommandVersion != request.CommandVersion ||
                        journal.EvaluatedStateVersion != request.ExpectedStateVersion)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CommitFailed,
                            "The command journal does not match the accepted batch.");
                    }

                    byte[] nextStatePayload = request.NextStatePayload!;
                    TablePayloadSha256 nextHash = TablePayloadSha256.Compute(nextStatePayload);
                    if (request.NextStateFingerprint == 0UL)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "An accepted command requires a nonzero state fingerprint.");
                    }

                    if ((request.NextCheatVersion == 0L) != (request.NextCheatFingerprint == 0UL))
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Cheat version and fingerprint must be supplied together.");
                    }

                    if (request.CommittedFactsPayload != null)
                    {
                        if (!request.CommittedFactsPayloadSha256.HasValue ||
                            TablePayloadSha256.Compute(request.CommittedFactsPayload) !=
                            request.CommittedFactsPayloadSha256.Value)
                        {
                            await SafeRollbackAsync(transaction).ConfigureAwait(false);
                            return TableDurableCommitResult.Failed(
                                TableRuntimeReasonCode.CheckpointInvalid,
                                "Committed facts payload does not match its digest.");
                        }
                    }

                    checkpoint = request.Checkpoint;
                    if (checkpoint != null)
                    {
                        if (checkpoint.StateVersion != request.CommandVersion ||
                            checkpoint.LastEventSeq != newEvents![newEvents.Count - 1].Seq ||
                            checkpoint.StateFingerprint != request.NextStateFingerprint ||
                            checkpoint.StatePayloadSha256 != nextHash ||
                            checkpoint.ControlVersion != state.ControlVersion ||
                            checkpoint.TimingVersion != request.TimingVersion ||
                            checkpoint.RulesetVersion != state.RulesetVersion)
                        {
                            await SafeRollbackAsync(transaction).ConfigureAwait(false);
                            return TableDurableCommitResult.Failed(
                                TableRuntimeReasonCode.CheckpointInvalid,
                                "The checkpoint does not match the accepted state/control/timing/ruleset.");
                        }

                        if (!TableCheckpointValidator.TryValidate(checkpoint, out string reason, out string detail))
                        {
                            await SafeRollbackAsync(transaction).ConfigureAwait(false);
                            return TableDurableCommitResult.Failed(reason, detail);
                        }

                        TableCheckpoint? previous = await LoadCheckpointAsync(
                            connection,
                            transaction,
                            request.RoomId,
                            cancellationToken).ConfigureAwait(false);
                        TableDurableCommitResult? continuity = ValidateCheckpointContinuity(previous, checkpoint);
                        if (continuity != null)
                        {
                            await SafeRollbackAsync(transaction).ConfigureAwait(false);
                            return continuity;
                        }

                        byte[] encodedTiming = TableTimingCheckpointCodec.Encode(request.Timing);
                        if (!encodedTiming.AsSpan().SequenceEqual(checkpoint.TimingPayload))
                        {
                            await SafeRollbackAsync(transaction).ConfigureAwait(false);
                            return TableDurableCommitResult.Failed(
                                TableRuntimeReasonCode.CheckpointInvalid,
                                "Accepted timing journal and checkpoint timing payload disagree.");
                        }
                    }

                    nextTimingVersion = request.TimingVersion;
                }

                // 5a/5b are staged in one transaction. REJECTED writes only a receipt.
                if (accepted)
                {
                    for (int i = 0; i < newEvents!.Count; i++)
                    {
                        await InsertEventAsync(
                            connection,
                            transaction,
                            request.RoomId,
                            newEvents[i],
                            cancellationToken).ConfigureAwait(false);
                    }

                    await InsertCommandAsync(connection, transaction, request.RoomId, journal!, cancellationToken)
                        .ConfigureAwait(false);
                    await InsertTimingJournalAsync(
                        connection,
                        transaction,
                        request.RoomId,
                        nextTimingVersion,
                        request.CommandVersion,
                        state.ControlVersion,
                        request.Timing!.BootId,
                        request.Timing.CapturedAtUtcMs,
                        request.Timing.CapturedMonotonicMillis,
                        TableTimingCheckpointCodec.Encode(request.Timing),
                        TablePayloadSha256.Compute(TableTimingCheckpointCodec.Encode(request.Timing)),
                        cancellationToken).ConfigureAwait(false);

                    if (checkpoint != null)
                    {
                        await UpsertCheckpointAsync(connection, transaction, checkpoint, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (request.CommittedFactsPayload != null)
                    {
                        await UpsertCommittedFactsAsync(
                            connection,
                            transaction,
                            request.RoomId,
                            request.CommittedFactsPayload,
                            request.CommittedFactsPayloadSha256!.Value,
                            cancellationToken).ConfigureAwait(false);
                    }

                    byte[] nextState = request.NextStatePayload!;
                    TablePayloadSha256 stateHash = TablePayloadSha256.Compute(nextState);
                    const string stateUpdate = @"
UPDATE table_store.table_state
   SET state_version = @state_version,
       last_event_seq = @last_event_seq,
       state_fingerprint = @state_fingerprint,
       state_hash = @state_hash,
       control_version = @control_version,
       timing_version = @timing_version,
       cheat_version = @cheat_version,
       cheat_fingerprint = @cheat_fingerprint,
       cheat_bound = @cheat_bound,
       updated_at = clock_timestamp()
 WHERE room_id = @room_id
   AND state_version = @expected_state_version
   AND control_version = @control_version";
                    await using NpgsqlCommand command = Command(stateUpdate, connection, transaction);
                    AddText(command, "room_id", request.RoomId);
                    AddBigint(command, "state_version", checked((long)request.CommandVersion.Value));
                    AddBigint(command, "last_event_seq", checked((long)newEvents[newEvents.Count - 1].Seq.Value));
                    AddNumeric(command, "state_fingerprint", ToNumeric(request.NextStateFingerprint));
                    AddBytea(command, "state_hash", stateHash.ToArray());
                    AddBigint(command, "control_version", state.ControlVersion);
                    AddBigint(command, "timing_version", nextTimingVersion);
                    AddBigint(command, "cheat_version", request.NextCheatVersion);
                    AddNumeric(command, "cheat_fingerprint", ToNumeric(request.NextCheatFingerprint));
                    AddBool(command, "cheat_bound", request.NextCheatVersion != 0L);
                    AddBigint(command, "expected_state_version", checked((long)request.ExpectedStateVersion.Value));
                    int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (rows != 1)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.StaleState("The state CAS failed inside the transaction.");
                    }
                }

                if (request.Receipt != null)
                {
                    await InsertReceiptAsync(connection, transaction, request, state, newEvents, cancellationToken)
                        .ConfigureAwait(false);
                }

                for (int i = 0; i < request.Outbox.Count; i++)
                {
                    await InsertOutboxAsync(
                        connection,
                        transaction,
                        request.Outbox[i],
                        cancellationToken).ConfigureAwait(false);
                }

                // 5c) Commit-boundary scope recheck using the database clock. This is the
                // linearization point for revocation/expiry.
                if (!request.InternalCommand)
                {
                    TableDurableReceipt receipt = request.Receipt!;
                    const string scopeBoundary = @"
SELECT 1
  FROM table_store.request_scope
 WHERE scope_id = @scope_id
   AND revoked_at_utc_ms IS NULL
   AND expires_at_utc_ms > (extract(epoch from clock_timestamp()) * 1000)::bigint
 FOR SHARE";
                    await using NpgsqlCommand command = Command(scopeBoundary, connection, transaction);
                    AddText(command, "scope_id", receipt.ScopeId.Value);
                    object? present = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (present == null || present == DBNull.Value)
                    {
                        await SafeRollbackAsync(transaction).ConfigureAwait(false);
                        return TableDurableCommitResult.ScopeRejected(
                            ProtocolErrorCode.RequestScopeExpired,
                            "The request scope expired or was revoked at the commit boundary.");
                    }
                }

                bool liveLease = await RefreshLeaseFenceAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    request.OwnerInstance,
                    request.ExpectedOwnerEpoch,
                    cancellationToken).ConfigureAwait(false);
                if (!liveLease)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleOwner(
                        "The table lease expired before the final commit fence.");
                }

                commitIssued = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                AfterCommitBeforeReturnForTest?.Invoke();
                return TableDurableCommitResult.Committed(request.Receipt);
            }
            catch (OutboxConflictException exception)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.OutboxConflict,
                    exception.Message);
            }
            catch (OperationCanceledException)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The commit was interrupted after it may have reached PostgreSQL.")
                    : TableDurableCommitResult.Cancelled("The commit was cancelled before PostgreSQL commit.");
            }
            catch (Exception exception)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The COMMIT may have reached PostgreSQL but its acknowledgement was lost.")
                    : TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        SafeDatabaseDetail(exception));
            }
        }

        private static TableDurableCommitResult? ValidateScope(
            ScopeRow? scope,
            TableDurableCommitRequest request,
            TableDurableReceipt receipt,
            long nowUtcMs)
        {
            if (scope == null)
            {
                return TableDurableCommitResult.ScopeRejected(
                    ProtocolErrorCode.Unauthenticated,
                    "The request scope does not exist.");
            }

            if (!string.Equals(scope.AccountId, receipt.AccountId, StringComparison.Ordinal))
            {
                return TableDurableCommitResult.ScopeRejected(
                    TableRuntimeReasonCode.ScopeAccountMismatch,
                    "The request scope belongs to another account.");
            }

            if (scope.Role != TableScopeRole.Player)
            {
                return TableDurableCommitResult.ScopeRejected(
                    TableRuntimeReasonCode.ScopeRoleMismatch,
                    "Only a player scope can submit a table command.");
            }

            if (!string.Equals(scope.RoomId, request.RoomId, StringComparison.Ordinal))
            {
                return TableDurableCommitResult.ScopeRejected(
                    TableRuntimeReasonCode.ScopeRoomMismatch,
                    "The request scope is bound to another room.");
            }

            if (scope.RevokedAt.HasValue)
            {
                return TableDurableCommitResult.ScopeRejected(
                    ProtocolErrorCode.RequestScopeExpired,
                    "The request scope was revoked.");
            }

            if (nowUtcMs < 0 || (ulong)nowUtcMs >= scope.ExpiresAt)
            {
                return TableDurableCommitResult.ScopeRejected(
                    ProtocolErrorCode.RequestScopeExpired,
                    "The request scope expired.");
            }

            return null;
        }

        private static TableDurableCommitResult? ValidateAndCopyEvents(
            TableDurableCommitRequest request,
            EventSeq expectedLastSeq,
            out List<TableEventRecord>? copy)
        {
            copy = null;
            if (request.Events == null || request.Events.Count == 0)
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CommitFailed,
                    "An accepted command requires at least one event.");
            }

            var values = new List<TableEventRecord>(request.Events.Count);
            ulong nextSeq = expectedLastSeq.Value + 1UL;
            for (int i = 0; i < request.Events.Count; i++)
            {
                TableEventRecord record = request.Events[i];
                if (record.Ordinal != i)
                {
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        "Event ordinals are not contiguous.");
                }

                if (record.CommandVersion != request.CommandVersion)
                {
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        "Event command version does not match the batch.");
                }

                if (record.Seq.Value != nextSeq)
                {
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        "Event sequence is not contiguous.");
                }

                values.Add(record);
                nextSeq++;
            }

            copy = values;
            return null;
        }

        private static TableDurableCommitResult? ValidateAcceptedTiming(TableDurableCommitRequest request)
        {
            try
            {
                RoundRecoveryState state = RoundRecoveryCodec.Decode(request.NextStatePayload!);
                string roundId = request.Timing!.RoundId;
                bool terminalReset = state.Result != null;
                if ((roundId.Length == 0 && !terminalReset) ||
                    (roundId.Length > 0 &&
                     !string.Equals(roundId, state.Spec.RoundId, StringComparison.Ordinal)))
                {
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Accepted timing does not belong to the committed next round.");
                }

                return null;
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is IOException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Accepted timing/state is invalid: " + exception.Message);
            }
        }

        private async ValueTask<bool> RoomExistsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = Command(
                "SELECT 1 FROM table_store.table_state WHERE room_id = @room_id",
                connection,
                transaction);
            AddText(command, "room_id", roomId);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null && result != DBNull.Value;
        }

        private async ValueTask<TableDurableReceipt?> LoadReceiptForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            CancellationToken cancellationToken)
        {
            const string sql = @"
SELECT api, payload_sha256, status, reason_code, evaluated_state_version,
       command_version, event_seq, committed_at_utc_ms, canonical_payload_digest,
       (extract(epoch from created_at) * 1000)::bigint,
       (extract(epoch from expires_at) * 1000)::bigint,
       response_blob
  FROM table_store.request_receipt
 WHERE account_id = @account_id AND scope_id = @scope_id AND request_id = @request_id
 FOR UPDATE";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "account_id", accountId);
            AddText(command, "scope_id", scopeId.Value);
            AddText(command, "request_id", requestId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            string api = reader.GetString(0);
            var digest = ReadHash(reader, 1);
            string status = reader.GetString(2);
            string? reason = reader.IsDBNull(3) ? null : reader.GetString(3);
            var evaluated = new StateVersion(checked((ulong)reader.GetInt64(4)));
            long? commandVersion = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5);
            long? seq = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            ulong? committedAt = reader.IsDBNull(7) ? (ulong?)null : checked((ulong)reader.GetInt64(7));
            PayloadDigest? canonical = ReadOptionalPayloadDigest(reader, 8);
            ulong createdAt = checked((ulong)reader.GetInt64(9));
            ulong expiresAt = checked((ulong)reader.GetInt64(10));
            byte[] response = reader.GetFieldValue<byte[]>(11);
            string detail = System.Text.Encoding.UTF8.GetString(
                _protector.Open(response, Aad("receipt", accountId + "|" + scopeId.Value + "|" + requestId.Value)));
            return new TableDurableReceipt(
                accountId,
                scopeId,
                requestId,
                api,
                digest,
                RestoreReceipt(status, reason, evaluated, commandVersion, seq, committedAt, canonical),
                detail,
                createdAt,
                expiresAt);
        }

        private async Task InsertEventAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            TableEventRecord record,
            CancellationToken cancellationToken)
        {
            byte[] payload = TableCommittedHistoryCodec.Encode(new[] { record });
            byte[] blob = _protector.Seal(
                payload,
                Aad("event", roomId + "|" + record.Seq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            const string sql = @"
INSERT INTO table_store.table_event
    (room_id, seq, command_version, event_ordinal, event_type, event_schema, seat,
     event_blob, event_blob_sha256)
VALUES
    (@room_id, @seq, @command_version, @event_ordinal, @event_type, @event_schema, @seat,
     @blob, @hash)";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            AddBigint(command, "seq", checked((long)record.Seq.Value));
            AddBigint(command, "command_version", checked((long)record.CommandVersion.Value));
            Add(command, "event_ordinal", record.Ordinal, NpgsqlDbType.Integer);
            Add(command, "event_type", (int)record.Type, NpgsqlDbType.Integer);
            Add(command, "event_schema", record.EventSchema, NpgsqlDbType.Integer);
            Add(command, "seat", record.Seat, NpgsqlDbType.Integer);
            AddBytea(command, "blob", blob);
            AddBytea(command, "hash", TablePayloadSha256.Compute(payload).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task InsertCommandAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            TableCommandJournalRecord journal,
            CancellationToken cancellationToken)
        {
            byte[] payload = journal.CommandPayload;
            if (TablePayloadSha256.Compute(payload) != journal.CommandPayloadSha256)
            {
                throw new InvalidDataException("Command journal payload bytes do not match the encoded command digest.");
            }

            byte[] blob = _protector.Seal(
                payload,
                Aad("command", roomId + "|" + journal.CommandVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            const string sql = @"
INSERT INTO table_store.table_command_journal
    (room_id, command_version, evaluated_state_version, request_id, account_id, scope_id, api,
     payload_sha256, command_payload_blob, command_payload_sha256, schema_version)
VALUES
    (@room_id, @command_version, @evaluated_state_version, @request_id, @account_id, @scope_id, @api,
     @payload_sha256, @blob, @blob_sha256, @schema_version)";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            AddBigint(command, "command_version", checked((long)journal.CommandVersion.Value));
            AddBigint(command, "evaluated_state_version", checked((long)journal.EvaluatedStateVersion.Value));
            AddText(command, "request_id", journal.RequestId.Value);
            AddText(command, "account_id", journal.AccountId);
            AddText(command, "scope_id", journal.ScopeId.Value);
            AddText(command, "api", journal.Api);
            AddBytea(command, "payload_sha256", journal.PayloadSha256.ToArray());
            AddBytea(command, "blob", blob);
            AddBytea(command, "blob_sha256", TablePayloadSha256.Compute(payload).ToArray());
            Add(command, "schema_version", journal.SchemaVersion, NpgsqlDbType.Integer);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task InsertReceiptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            TableDurableCommitRequest request,
            TableStateRow state,
            List<TableEventRecord>? events,
            CancellationToken cancellationToken)
        {
            TableDurableReceipt receipt = request.Receipt!;
            // A forced-state P1.4 rejection still commits private events/state, but its
            // business receipt remains REJECTED and therefore carries no command/event
            // identity fields (the schema enforces that same rule).
            bool acceptedReceipt = receipt.IsAccepted;
            long? commandVersion = acceptedReceipt ? checked((long)request.CommandVersion.Value) : (long?)null;
            long? eventSeq = acceptedReceipt ? checked((long)events![events.Count - 1].Seq.Value) : (long?)null;
            string detail = receipt.Detail ?? string.Empty;
            byte[] response = _protector.Seal(
                System.Text.Encoding.UTF8.GetBytes(detail),
                Aad("receipt", receipt.AccountId + "|" + receipt.ScopeId.Value + "|" + receipt.RequestId.Value));
            const string sql = @"
INSERT INTO table_store.request_receipt
    (account_id, scope_id, request_id, room_id, api, payload_sha256, status, reason_code,
     evaluated_state_version, command_version, event_seq, committed_at_utc_ms,
     canonical_payload_digest, response_blob, created_at, expires_at)
VALUES
    (@account_id, @scope_id, @request_id, @room_id, @api, @payload_sha256, @status, @reason_code,
     @evaluated_state_version, @command_version, @event_seq, @committed_at,
     @canonical_digest, @response_blob, @created_at, @expires_at)";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "account_id", receipt.AccountId);
            AddText(command, "scope_id", receipt.ScopeId.Value);
            AddText(command, "request_id", receipt.RequestId.Value);
            AddText(command, "room_id", request.RoomId);
            AddText(command, "api", receipt.Api);
            AddBytea(command, "payload_sha256", receipt.PayloadSha256.ToArray());
            AddText(command, "status", StatusText(receipt.Receipt.Status));
            AddText(command, "reason_code", string.IsNullOrEmpty(receipt.Receipt.ReasonCode)
                ? null
                : receipt.Receipt.ReasonCode);
            AddBigint(command, "evaluated_state_version", checked((long)receipt.Receipt.EvaluatedStateVersion.Value));
            AddNullableBigint(command, "command_version", commandVersion);
            AddNullableBigint(command, "event_seq", eventSeq);
            AddNullableBigint(
                command,
                "committed_at",
                receipt.Receipt.CommittedAtMs.HasValue
                    ? checked((long)receipt.Receipt.CommittedAtMs.Value)
                    : (long?)null);
            AddBytea(
                command,
                "canonical_digest",
                receipt.Receipt.CanonicalPayloadDigest.HasValue
                    ? receipt.Receipt.CanonicalPayloadDigest.Value.AsSpan().ToArray()
                    : null);
            AddBytea(command, "response_blob", response);
            AddTimestamp(command, "created_at", FromMs(checked((long)receipt.CreatedAtUtcMs)));
            AddTimestamp(command, "expires_at", FromMs(checked((long)receipt.ExpiresAtUtcMs)));
            int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows != 1)
            {
                throw new InvalidOperationException("Receipt insert did not write exactly one row.");
            }

            _ = state;
        }

        private async Task InsertOutboxAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            TableSettlementOutboxRecord record,
            CancellationToken cancellationToken)
        {
            const string existingSql = @"
SELECT payload_sha256
  FROM table_store.settlement_outbox
 WHERE match_id = @match_id AND result_version = @result_version
 FOR UPDATE";
            await using (NpgsqlCommand existingCommand = Command(existingSql, connection, transaction))
            {
                AddText(existingCommand, "match_id", record.MatchId);
                Add(existingCommand, "result_version", record.ResultVersion, NpgsqlDbType.Integer);
                await using NpgsqlDataReader reader = await existingCommand.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (ReadHash(reader, 0) != record.PayloadSha256)
                    {
                        throw new OutboxConflictException(
                            "A different settlement payload already exists for this match/result version.");
                    }

                    return;
                }
            }

            const string insertSql = @"
INSERT INTO table_store.settlement_outbox
    (match_id, result_version, payload_blob, payload_sha256, created_at)
VALUES
    (@match_id, @result_version, @payload, @hash, @created_at)";
            await using NpgsqlCommand command = Command(insertSql, connection, transaction);
            AddText(command, "match_id", record.MatchId);
            Add(command, "result_version", record.ResultVersion, NpgsqlDbType.Integer);
            AddBytea(command, "payload", _protector.Seal(
                record.Payload,
                Aad("outbox", record.MatchId + "|" + record.ResultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            AddBytea(command, "hash", record.PayloadSha256.ToArray());
            AddTimestamp(command, "created_at", FromMs(checked((long)record.CreatedAtUtcMs)));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed class OutboxConflictException : Exception
        {
            internal OutboxConflictException(string message)
                : base(message)
            {
            }
        }
    }
}

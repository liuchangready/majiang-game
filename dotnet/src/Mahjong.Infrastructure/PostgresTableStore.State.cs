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
        private const string CheckpointColumns = @"
room_id, state_version, last_event_seq, state_fingerprint, ruleset_version,
schema_version, boot_id, created_at_utc_ms, captured_monotonic_ms,
control_version, timing_version,
state_blob, state_sha256, control_blob, control_sha256, timing_blob, timing_sha256,
cheat_blob, cheat_sha256, facts_blob, facts_sha256, history_blob, history_sha256,
bindings_blob, bindings_sha256";

        public async ValueTask CreateRoomAsync(
            TableCheckpoint initialCheckpoint,
            CancellationToken cancellationToken)
        {
            if (initialCheckpoint == null)
            {
                throw new ArgumentNullException(nameof(initialCheckpoint));
            }

            if (!TableCheckpointValidator.TryValidate(
                    initialCheckpoint,
                    out string reason,
                    out string detail))
            {
                throw new InvalidDataException(reason + ": " + detail);
            }

            if (!initialCheckpoint.TimingPayloadSha256.Equals(
                    TablePayloadSha256.Compute(initialCheckpoint.TimingPayload)))
            {
                throw new InvalidDataException("The initial checkpoint timing payload hash is invalid.");
            }

            TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(initialCheckpoint.TimingPayload);
            long initialTimingVersion = initialCheckpoint.TimingVersion == 0 ? 1L : initialCheckpoint.TimingVersion;
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await AcquireRoomLockAsync(connection, transaction, initialCheckpoint.RoomId, cancellationToken)
                    .ConfigureAwait(false);

                const string stateSql = @"
INSERT INTO table_store.table_state
    (room_id, state_version, last_event_seq, state_fingerprint, state_hash, ruleset_version,
     control_version, timing_version, cheat_version, cheat_fingerprint, cheat_bound, updated_at)
VALUES (@room_id, @state_version, @last_event_seq, @state_fingerprint, @state_hash, @ruleset,
        @control_version, @timing_version, 0, 0, false, clock_timestamp())
ON CONFLICT (room_id) DO NOTHING";
                await using (NpgsqlCommand command = Command(stateSql, connection, transaction))
                {
                    AddText(command, "room_id", initialCheckpoint.RoomId);
                    AddBigint(command, "state_version", checked((long)initialCheckpoint.StateVersion.Value));
                    AddBigint(command, "last_event_seq", checked((long)initialCheckpoint.LastEventSeq.Value));
                    AddNumeric(command, "state_fingerprint", ToNumeric(initialCheckpoint.StateFingerprint));
                    AddBytea(command, "state_hash", initialCheckpoint.StatePayloadSha256.ToArray());
                    Add(command, "ruleset", initialCheckpoint.RulesetVersion, NpgsqlDbType.Integer);
                    AddBigint(command, "control_version", initialCheckpoint.ControlVersion);
                    AddBigint(command, "timing_version", initialTimingVersion);
                    int inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (inserted != 1)
                    {
                        throw new InvalidOperationException("Room already exists.");
                    }
                }

                await UpsertCheckpointAsync(connection, transaction, initialCheckpoint, cancellationToken)
                    .ConfigureAwait(false);
                await InsertTimingJournalAsync(
                    connection,
                    transaction,
                    initialCheckpoint.RoomId,
                    initialTimingVersion,
                    initialCheckpoint.StateVersion,
                    initialCheckpoint.ControlVersion,
                    initialCheckpoint.BootId,
                    initialCheckpoint.CreatedAtUtcMs,
                    initialCheckpoint.CapturedMonotonicMillis,
                    initialCheckpoint.TimingPayload,
                    initialCheckpoint.TimingPayloadSha256,
                    cancellationToken).ConfigureAwait(false);
                if (initialCheckpoint.CommittedFactsPayload != null)
                {
                    await UpsertCommittedFactsAsync(
                        connection,
                        transaction,
                        initialCheckpoint.RoomId,
                        initialCheckpoint.CommittedFactsPayload,
                        initialCheckpoint.CommittedFactsPayloadSha256!.Value,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask<TableStateRow?> LoadStateAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await LoadStateAsync(connection, null, roomId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask<TableCheckpoint?> LoadCheckpointAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await LoadCheckpointAsync(connection, null, roomId, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<TableTimingJournalRecord?> LoadTimingAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT room_id, timing_version, state_version, control_version, boot_id,
       captured_at_utc_ms, captured_monotonic_ms, timing_blob, timing_sha256
  FROM table_store.table_timing_journal
 WHERE room_id = @room_id
 ORDER BY timing_version DESC
 LIMIT 1";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", roomId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            long version = reader.GetInt64(1);
            string boot = reader.GetString(4);
            byte[] blob = reader.GetFieldValue<byte[]>(7);
            var hash = ReadHash(reader, 8);
            byte[] payload = _protector.Open(
                blob,
                Aad("timing", roomId + "|" + version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (TablePayloadSha256.Compute(payload) != hash)
            {
                throw new InvalidDataException("Timing journal payload hash does not match.");
            }

            TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(payload);
            if (!string.Equals(timing.BootId, boot, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Timing journal boot id does not match its payload.");
            }

            return new TableTimingJournalRecord(
                roomId,
                version,
                new StateVersion(checked((ulong)reader.GetInt64(2))),
                reader.GetInt64(3),
                boot,
                checked((ulong)reader.GetInt64(5)),
                reader.GetInt64(6),
                timing);
        }

        public async ValueTask<TableCommittedFactsSnapshot?> LoadCommittedFactsAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql = @"
SELECT payload_blob, payload_sha256
  FROM table_store.table_committed_facts
 WHERE room_id = @room_id";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", roomId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            byte[] payload = _protector.Open(
                reader.GetFieldValue<byte[]>(0),
                Aad("facts", roomId));
            return new TableCommittedFactsSnapshot(payload, ReadHash(reader, 1));
        }

        public async ValueTask<IReadOnlyList<TableEventRecord>> LoadEventsAfterAsync(
            string roomId,
            EventSeq afterSeq,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            TableStateRow? state = await LoadStateAsync(connection, null, roomId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false);
            if (state == null)
            {
                throw new InvalidDataException("The room state row is missing.");
            }

            const string sql = @"
SELECT seq, command_version, event_ordinal, event_type, event_schema, seat,
       event_blob, event_blob_sha256
  FROM table_store.table_event
 WHERE room_id = @room_id AND seq > @after_seq
 ORDER BY seq";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", roomId);
            AddBigint(command, "after_seq", checked((long)afterSeq.Value));
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var records = new List<TableEventRecord>();
            ulong expected = afterSeq.Value + 1UL;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ulong seq = checked((ulong)reader.GetInt64(0));
                if (seq != expected)
                {
                    throw new InvalidDataException(
                        "Event gap: expected seq " + expected + ", found " + seq + ".");
                }

                byte[] blob = reader.GetFieldValue<byte[]>(6);
                byte[] payload = _protector.Open(
                    blob,
                    Aad("event", roomId + "|" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (TablePayloadSha256.Compute(payload) != ReadHash(reader, 7))
                {
                    throw new InvalidDataException("Event payload hash does not match for seq " + seq + ".");
                }

                IReadOnlyList<TableEventRecord> decoded = TableCommittedHistoryCodec.Decode(payload);
                if (decoded.Count != 1)
                {
                    throw new InvalidDataException("Stored event payload must contain exactly one event.");
                }

                TableEventRecord record = decoded[0];
                if (record.Seq.Value != seq ||
                    record.CommandVersion.Value != checked((ulong)reader.GetInt64(1)) ||
                    record.Ordinal != reader.GetInt32(2) ||
                    (int)record.Type != reader.GetInt32(3) ||
                    record.EventSchema != reader.GetInt32(4) ||
                    record.Seat != reader.GetInt32(5))
                {
                    throw new InvalidDataException("Event row metadata does not match its payload at seq " + seq + ".");
                }

                records.Add(record);
                expected++;
            }

            if (expected != state.LastEventSeq.Value + 1UL)
            {
                throw new InvalidDataException(
                    "Event log ended at " + (expected - 1UL) + " but table_state requires " + state.LastEventSeq.Value + ".");
            }

            return records;
        }

        public async ValueTask<IReadOnlyList<TableCommandJournalRecord>> LoadCommandsAfterAsync(
            string roomId,
            StateVersion afterVersion,
            CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            TableStateRow? state = await LoadStateAsync(connection, null, roomId, forUpdate: false, cancellationToken)
                .ConfigureAwait(false);
            if (state == null)
            {
                throw new InvalidDataException("The room state row is missing.");
            }

            const string sql = @"
SELECT command_version, evaluated_state_version, request_id, account_id, scope_id, api,
       payload_sha256, command_payload_blob, command_payload_sha256, schema_version
  FROM table_store.table_command_journal
 WHERE room_id = @room_id AND command_version > @after_version
 ORDER BY command_version";
            await using NpgsqlCommand command = Command(sql, connection);
            AddText(command, "room_id", roomId);
            AddBigint(command, "after_version", checked((long)afterVersion.Value));
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var records = new List<TableCommandJournalRecord>();
            ulong expected = afterVersion.Value + 1UL;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ulong version = checked((ulong)reader.GetInt64(0));
                if (version != expected)
                {
                    throw new InvalidDataException(
                        "Command journal gap: expected version " + expected + ", found " + version + ".");
                }

                byte[] blob = reader.GetFieldValue<byte[]>(7);
                byte[] payload = _protector.Open(
                    blob,
                    Aad("command", roomId + "|" + version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (TablePayloadSha256.Compute(payload) != ReadHash(reader, 8))
                {
                    throw new InvalidDataException("Command payload hash does not match for version " + version + ".");
                }

                var record = new TableCommandJournalRecord(
                    new StateVersion(version),
                    new StateVersion(checked((ulong)reader.GetInt64(1))),
                    new RequestId(reader.GetString(2)),
                    reader.GetString(3),
                    new RequestScopeId(reader.GetString(4)),
                    reader.GetString(5),
                    ReadHash(reader, 6),
                    payload,
                    reader.GetInt32(9));
                if (record.CommandPayloadSha256 != TablePayloadSha256.Compute(payload))
                {
                    throw new InvalidDataException("Command journal encoded payload digest does not match its bytes.");
                }

                records.Add(record);
                expected++;
            }

            if (expected != state.StateVersion.Value + 1UL)
            {
                throw new InvalidDataException(
                    "Command journal ended at " + (expected - 1UL) +
                    " but table_state requires " + state.StateVersion.Value + ".");
            }

            return records;
        }

        public async ValueTask<TableDurableCommitResult> SaveCheckpointAsync(
            TableCheckpoint checkpoint,
            TableCheckpointFence fence,
            CancellationToken cancellationToken)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            if (fence == null)
            {
                throw new ArgumentNullException(nameof(fence));
            }

            if (!string.Equals(checkpoint.RoomId, fence.RoomId, StringComparison.Ordinal))
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Fence room does not match checkpoint.");
            }

            await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            bool commitIssued = false;
            bool committed = false;
            try
            {
                await AcquireRoomLockAsync(connection, transaction, fence.RoomId, cancellationToken)
                    .ConfigureAwait(false);
                TableDurableCommitResult? fenceFailure = await ValidateFenceAsync(
                    connection,
                    transaction,
                    fence.RoomId,
                    fence.OwnerInstance,
                    fence.ExpectedOwnerEpoch,
                    fence.ExpectedStateVersion,
                    fence.ExpectedControlVersion,
                    cancellationToken).ConfigureAwait(false);
                if (fenceFailure != null)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return fenceFailure;
                }

                TableStateRow state = (await LoadStateAsync(
                    connection,
                    transaction,
                    fence.RoomId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false))!;
                if (state.TimingVersion != fence.ExpectedTimingVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.StaleStateVersion,
                        "Expected timing version is stale.");
                }

                if (checkpoint.StateVersion != state.StateVersion ||
                    checkpoint.LastEventSeq != state.LastEventSeq ||
                    checkpoint.StateFingerprint != state.StateFingerprint ||
                    checkpoint.StatePayloadSha256 != state.StateHash ||
                    checkpoint.ControlVersion != state.ControlVersion ||
                    checkpoint.RulesetVersion != state.RulesetVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "A checkpoint does not match the current committed state/control/ruleset.");
                }

                if (checkpoint.TimingVersion != state.TimingVersion + 1L)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "A checkpoint must advance timing version by exactly one.");
                }

                if (!TableCheckpointValidator.TryValidate(checkpoint, out string reason, out string detail))
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(reason, detail);
                }

                TableCheckpoint? previous = await LoadCheckpointAsync(connection, transaction, fence.RoomId, cancellationToken)
                    .ConfigureAwait(false);
                TableDurableCommitResult? carry = ValidateCheckpointContinuity(previous, checkpoint);
                if (carry != null)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return carry;
                }

                if (TablePayloadSha256.Compute(checkpoint.TimingPayload) != checkpoint.TimingPayloadSha256)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Checkpoint timing payload/boot is inconsistent.");
                }

                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                await UpsertCheckpointAsync(connection, transaction, checkpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (checkpoint.CommittedFactsPayload != null)
                {
                    await UpsertCommittedFactsAsync(
                        connection,
                        transaction,
                        checkpoint.RoomId,
                        checkpoint.CommittedFactsPayload,
                        checkpoint.CommittedFactsPayloadSha256!.Value,
                        cancellationToken).ConfigureAwait(false);
                }

                await InsertTimingJournalAsync(
                    connection,
                    transaction,
                    checkpoint.RoomId,
                    checkpoint.TimingVersion,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    checkpoint.BootId,
                    checkpoint.CreatedAtUtcMs,
                    checkpoint.CapturedMonotonicMillis,
                    checkpoint.TimingPayload,
                    checkpoint.TimingPayloadSha256,
                    cancellationToken).ConfigureAwait(false);
                await UpdateStateTimingAsync(
                    connection,
                    transaction,
                    checkpoint.RoomId,
                    checkpoint.TimingVersion,
                    cancellationToken).ConfigureAwait(false);
                bool liveLease = await RefreshLeaseFenceAsync(
                    connection,
                    transaction,
                    checkpoint.RoomId,
                    fence.OwnerInstance,
                    fence.ExpectedOwnerEpoch,
                    cancellationToken).ConfigureAwait(false);
                if (!liveLease)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleOwner(
                        "The table lease expired before the checkpoint commit fence.");
                }

                _ = timing;
                commitIssued = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                return TableDurableCommitResult.Committed(null);
            }
            catch (OperationCanceledException)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The checkpoint COMMIT may have reached PostgreSQL without an acknowledgement.")
                    : TableDurableCommitResult.Cancelled("Checkpoint save was cancelled before commit.");
            }
            catch (Exception exception)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The checkpoint COMMIT may have reached PostgreSQL without an acknowledgement.")
                    : TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        SafeDatabaseDetail(exception));
            }
        }

        public async ValueTask<TableDurableCommitResult> AppendTimingAsync(
            TableTimingAppendRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
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
                TableDurableCommitResult? fenceFailure = await ValidateFenceAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    request.OwnerInstance,
                    request.ExpectedOwnerEpoch,
                    request.ExpectedStateVersion,
                    request.ExpectedControlVersion,
                    cancellationToken).ConfigureAwait(false);
                if (fenceFailure != null)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return fenceFailure;
                }

                TableStateRow state = (await LoadStateAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false))!;
                if (state.TimingVersion != request.ExpectedTimingVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.StaleStateVersion,
                        "Expected timing version is stale.");
                }

                string boot = request.Timing.BootId;
                if (string.IsNullOrWhiteSpace(boot))
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "Timing append requires a boot id.");
                }

                long nextVersion = checked(state.TimingVersion + 1L);
                byte[] payload = TableTimingCheckpointCodec.Encode(request.Timing);
                TablePayloadSha256 hash = TablePayloadSha256.Compute(payload);
                await InsertTimingJournalAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    nextVersion,
                    state.StateVersion,
                    state.ControlVersion,
                    boot,
                    request.Timing.CapturedAtUtcMs,
                    request.Timing.CapturedMonotonicMillis,
                    payload,
                    hash,
                    cancellationToken).ConfigureAwait(false);
                await UpdateStateTimingAsync(connection, transaction, request.RoomId, nextVersion, cancellationToken)
                    .ConfigureAwait(false);
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
                        "The table lease expired before the timing commit fence.");
                }

                commitIssued = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                return TableDurableCommitResult.Committed(null);
            }
            catch (OperationCanceledException)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The timing COMMIT may have reached PostgreSQL without an acknowledgement.")
                    : TableDurableCommitResult.Cancelled("Timing append was cancelled before commit.");
            }
            catch (Exception exception)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The timing COMMIT may have reached PostgreSQL without an acknowledgement.")
                    : TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        SafeDatabaseDetail(exception));
            }
        }

        public async ValueTask<TableDurableCommitResult> CommitControlAsync(
            TableControlCommitRequest request,
            TableCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            if (!string.Equals(checkpoint.RoomId, request.RoomId, StringComparison.Ordinal))
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Control checkpoint room does not match the fenced request room.");
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
                TableStateRow? state = await LoadStateAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false);
                if (state == null)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointMissing,
                        "Room is not initialized.");
                }

                TableLeaseSnapshot? lease = await LoadLeaseAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    forUpdate: true,
                    cancellationToken).ConfigureAwait(false);
                long now = await DbNowMsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (lease == null ||
                    !string.Equals(lease.OwnerInstance, request.OwnerInstance, StringComparison.Ordinal) ||
                    lease.OwnerEpoch != request.OwnerEpoch ||
                    now < 0 || (ulong)now >= lease.ExpiresAtUtcMs)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleOwner(
                        "The control change does not own a live lease.");
                }

                if (state.StateVersion != request.StateVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleState(
                        "The control change references a stale state version.");
                }

                if (state.ControlVersion != request.ExpectedControlVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.StaleControlVersion,
                        "The expected control version is not current.");
                }

                if (checkpoint.StateVersion != state.StateVersion ||
                    checkpoint.LastEventSeq != state.LastEventSeq ||
                    checkpoint.StateFingerprint != state.StateFingerprint ||
                    checkpoint.StatePayloadSha256 != state.StateHash ||
                    checkpoint.ControlVersion != request.ControlVersion ||
                    checkpoint.TimingVersion != state.TimingVersion + 1L ||
                    checkpoint.RulesetVersion != state.RulesetVersion)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "The control checkpoint does not match table_state/timing/ruleset.");
                }

                if (!TableCheckpointValidator.TryValidate(checkpoint, out string reason, out string detail))
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.Failed(reason, detail);
                }

                TableCheckpoint? previous = await LoadCheckpointAsync(connection, transaction, request.RoomId, cancellationToken)
                    .ConfigureAwait(false);
                TableDurableCommitResult? carry = ValidateCheckpointContinuity(previous, checkpoint);
                if (carry != null)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return carry;
                }

                await InsertControlJournalAsync(
                    connection,
                    transaction,
                    request,
                    cancellationToken).ConfigureAwait(false);
                await UpsertCheckpointAsync(connection, transaction, checkpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (checkpoint.CommittedFactsPayload != null)
                {
                    await UpsertCommittedFactsAsync(
                        connection,
                        transaction,
                        checkpoint.RoomId,
                        checkpoint.CommittedFactsPayload,
                        checkpoint.CommittedFactsPayloadSha256!.Value,
                        cancellationToken).ConfigureAwait(false);
                }

                await InsertTimingJournalAsync(
                    connection,
                    transaction,
                    checkpoint.RoomId,
                    checkpoint.TimingVersion,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    checkpoint.BootId,
                    checkpoint.CreatedAtUtcMs,
                    checkpoint.CapturedMonotonicMillis,
                    checkpoint.TimingPayload,
                    checkpoint.TimingPayloadSha256,
                    cancellationToken).ConfigureAwait(false);
                const string update = @"
UPDATE table_store.table_state
   SET control_version = @control_version,
       timing_version = @timing_version,
       updated_at = clock_timestamp()
 WHERE room_id = @room_id";
                await using (NpgsqlCommand command = Command(update, connection, transaction))
                {
                    AddText(command, "room_id", request.RoomId);
                    AddBigint(command, "control_version", request.ControlVersion);
                    AddBigint(command, "timing_version", checkpoint.TimingVersion);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                bool liveLease = await RefreshLeaseFenceAsync(
                    connection,
                    transaction,
                    request.RoomId,
                    request.OwnerInstance,
                    request.OwnerEpoch,
                    cancellationToken).ConfigureAwait(false);
                if (!liveLease)
                {
                    await SafeRollbackAsync(transaction).ConfigureAwait(false);
                    return TableDurableCommitResult.StaleOwner(
                        "The table lease expired before the control commit fence.");
                }

                commitIssued = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                return TableDurableCommitResult.Committed(null);
            }
            catch (OperationCanceledException)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The control COMMIT may have reached PostgreSQL without an acknowledgement.")
                    : TableDurableCommitResult.Cancelled("Control commit was cancelled before commit.");
            }
            catch (Exception exception)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return committed || commitIssued
                    ? TableDurableCommitResult.OutcomeUnknown(
                        "The control COMMIT may have reached PostgreSQL without an acknowledgement.")
                    : TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CommitFailed,
                        SafeDatabaseDetail(exception));
            }
        }

        // ---------------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------------

        private async Task AcquireRoomLockAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = Command(RoomLockSql, connection, transaction);
            AddText(command, "room_id", roomId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<TableDurableCommitResult?> ValidateFenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedOwnerEpoch,
            StateVersion expectedStateVersion,
            long expectedControlVersion,
            CancellationToken cancellationToken)
        {
            TableLeaseSnapshot? lease = await LoadLeaseAsync(
                connection,
                transaction,
                roomId,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
            long now = await DbNowMsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (lease == null ||
                !string.Equals(lease.OwnerInstance, ownerInstance, StringComparison.Ordinal) ||
                lease.OwnerEpoch != expectedOwnerEpoch ||
                now < 0 || (ulong)now >= lease.ExpiresAtUtcMs)
            {
                return TableDurableCommitResult.StaleOwner(
                    "The lease is not owned by this instance/epoch or has expired.");
            }

            TableStateRow? state = await LoadStateAsync(
                connection,
                transaction,
                roomId,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
            if (state == null || state.StateVersion != expectedStateVersion)
            {
                return TableDurableCommitResult.StaleState("The expected state version is not current.");
            }

            if (state.ControlVersion != expectedControlVersion)
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.StaleControlVersion,
                    "The expected control version is not current.");
            }

            return null;
        }

        private static async ValueTask SafeRollbackAsync(NpgsqlTransaction transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The original failure is the authoritative outcome.
            }
        }

        private static async ValueTask<long> DbNowMsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new NpgsqlCommand(
                "SELECT (extract(epoch from clock_timestamp()) * 1000)::bigint",
                connection,
                transaction);
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private async ValueTask<TableStateRow?> LoadStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string roomId,
            bool forUpdate,
            CancellationToken cancellationToken)
        {
            string sql = @"
SELECT room_id, state_version, last_event_seq, state_fingerprint, state_hash, ruleset_version,
       control_version, timing_version, cheat_version, cheat_fingerprint, cheat_bound,
       (extract(epoch from updated_at) * 1000)::bigint
  FROM table_store.table_state
 WHERE room_id = @room_id" + (forUpdate ? " FOR UPDATE" : string.Empty);
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new TableStateRow(
                reader.GetString(0),
                new StateVersion(checked((ulong)reader.GetInt64(1))),
                new EventSeq(checked((ulong)reader.GetInt64(2))),
                ReadULong(reader, 3),
                ReadHash(reader, 4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                checked((ulong)reader.GetInt64(11)),
                reader.GetInt64(7),
                reader.GetInt64(8),
                ReadULong(reader, 9),
                reader.GetBoolean(10));
        }

        private async ValueTask<TableLeaseSnapshot?> LoadLeaseAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string roomId,
            bool forUpdate,
            CancellationToken cancellationToken)
        {
            string sql = @"
SELECT owner_instance, owner_epoch,
       (extract(epoch from lease_expires_at) * 1000)::bigint,
       (extract(epoch from updated_at) * 1000)::bigint
  FROM table_store.table_lease
 WHERE room_id = @room_id" + (forUpdate ? " FOR UPDATE" : string.Empty);
            await using NpgsqlCommand command = Command(sql, connection, transaction);
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

        private async ValueTask<TableCheckpoint?> LoadCheckpointAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string roomId,
            CancellationToken cancellationToken)
        {
            string sql = "SELECT " + CheckpointColumns + @"
  FROM table_store.table_checkpoint
 WHERE room_id = @room_id
 ORDER BY state_version DESC
 LIMIT 1";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            string storedRoom = reader.GetString(0);
            ulong version = checked((ulong)reader.GetInt64(1));
            ulong lastSeq = checked((ulong)reader.GetInt64(2));
            ulong fingerprint = ReadULong(reader, 3);
            int ruleset = reader.GetInt32(4);
            int schema = reader.GetInt32(5);
            string boot = reader.GetString(6);
            ulong created = checked((ulong)reader.GetInt64(7));
            long monotonic = reader.GetInt64(8);
            long controlVersion = reader.GetInt64(9);
            long timingVersion = reader.GetInt64(10);
            string aadPrefix = storedRoom + "|" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] state = OpenCheckpointPart(reader, 11, 12, "checkpoint-state", aadPrefix);
            TablePayloadSha256 stateHash = ReadHash(reader, 12);
            byte[] control = OpenCheckpointPart(reader, 13, 14, "checkpoint-control", aadPrefix);
            TablePayloadSha256 controlHash = ReadHash(reader, 14);
            byte[] timing = OpenCheckpointPart(reader, 15, 16, "checkpoint-timing", aadPrefix);
            TablePayloadSha256 timingHash = ReadHash(reader, 16);
            byte[]? cheat = OpenOptionalCheckpointPart(reader, 17, 18, "checkpoint-cheat", aadPrefix);
            TablePayloadSha256? cheatHash = reader.IsDBNull(18) ? (TablePayloadSha256?)null : ReadHash(reader, 18);
            byte[]? facts = OpenOptionalCheckpointPart(reader, 19, 20, "checkpoint-facts", aadPrefix);
            TablePayloadSha256? factsHash = reader.IsDBNull(20) ? (TablePayloadSha256?)null : ReadHash(reader, 20);
            byte[]? history = OpenOptionalCheckpointPart(reader, 21, 22, "checkpoint-history", aadPrefix);
            TablePayloadSha256? historyHash = reader.IsDBNull(22) ? (TablePayloadSha256?)null : ReadHash(reader, 22);
            byte[]? bindings = OpenOptionalCheckpointPart(reader, 23, 24, "checkpoint-bindings", aadPrefix);
            TablePayloadSha256? bindingsHash = reader.IsDBNull(24) ? (TablePayloadSha256?)null : ReadHash(reader, 24);

            return new TableCheckpoint(
                storedRoom,
                new StateVersion(version),
                new EventSeq(lastSeq),
                fingerprint,
                ruleset,
                state,
                stateHash,
                controlVersion,
                control,
                controlHash,
                timing,
                timingHash,
                cheat,
                cheatHash,
                facts,
                factsHash,
                created,
                boot,
                schema,
                timingVersion,
                monotonic,
                history,
                historyHash,
                bindings,
                bindingsHash);
        }

        private byte[] OpenCheckpointPart(
            NpgsqlDataReader reader,
            int blobOrdinal,
            int hashOrdinal,
            string purpose,
            string aadKey)
        {
            byte[] blob = reader.GetFieldValue<byte[]>(blobOrdinal);
            byte[] payload = _protector.Open(blob, Aad(purpose, aadKey));
            if (TablePayloadSha256.Compute(payload) != ReadHash(reader, hashOrdinal))
            {
                throw new InvalidDataException("Checkpoint payload hash does not match for " + purpose + ".");
            }

            return payload;
        }

        private byte[]? OpenOptionalCheckpointPart(
            NpgsqlDataReader reader,
            int blobOrdinal,
            int hashOrdinal,
            string purpose,
            string aadKey)
        {
            if (reader.IsDBNull(blobOrdinal))
            {
                return null;
            }

            return OpenCheckpointPart(reader, blobOrdinal, hashOrdinal, purpose, aadKey);
        }

        private async Task UpsertCheckpointAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            TableCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            string aadKey = checkpoint.RoomId + "|" +
                checkpoint.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] state = _protector.Seal(checkpoint.StatePayload, Aad("checkpoint-state", aadKey));
            byte[] control = _protector.Seal(checkpoint.ControlPayload, Aad("checkpoint-control", aadKey));
            byte[] timing = _protector.Seal(checkpoint.TimingPayload, Aad("checkpoint-timing", aadKey));
            byte[]? cheat = checkpoint.CheatPayload == null
                ? null
                : _protector.Seal(checkpoint.CheatPayload, Aad("checkpoint-cheat", aadKey));
            byte[]? facts = checkpoint.CommittedFactsPayload == null
                ? null
                : _protector.Seal(checkpoint.CommittedFactsPayload, Aad("checkpoint-facts", aadKey));
            byte[]? history = checkpoint.CommittedHistoryPayload == null
                ? null
                : _protector.Seal(checkpoint.CommittedHistoryPayload, Aad("checkpoint-history", aadKey));
            byte[]? bindings = checkpoint.CommittedCommandBindingsPayload == null
                ? null
                : _protector.Seal(checkpoint.CommittedCommandBindingsPayload, Aad("checkpoint-bindings", aadKey));

            const string sql = @"
INSERT INTO table_store.table_checkpoint
    (room_id, state_version, last_event_seq, state_fingerprint, ruleset_version, schema_version,
     boot_id, created_at_utc_ms, captured_monotonic_ms, control_version, timing_version,
     state_blob, state_sha256, control_blob, control_sha256, timing_blob, timing_sha256,
     cheat_blob, cheat_sha256, facts_blob, facts_sha256, history_blob, history_sha256,
     bindings_blob, bindings_sha256)
VALUES
    (@room_id, @state_version, @last_event_seq, @state_fingerprint, @ruleset, @schema,
     @boot_id, @created_at, @captured_monotonic, @control_version, @timing_version,
     @state_blob, @state_sha256, @control_blob, @control_sha256, @timing_blob, @timing_sha256,
     @cheat_blob, @cheat_sha256, @facts_blob, @facts_sha256, @history_blob, @history_sha256,
     @bindings_blob, @bindings_sha256)
ON CONFLICT (room_id, state_version) DO UPDATE
SET last_event_seq = EXCLUDED.last_event_seq,
    state_fingerprint = EXCLUDED.state_fingerprint,
    ruleset_version = EXCLUDED.ruleset_version,
    schema_version = EXCLUDED.schema_version,
    boot_id = EXCLUDED.boot_id,
    created_at_utc_ms = EXCLUDED.created_at_utc_ms,
    captured_monotonic_ms = EXCLUDED.captured_monotonic_ms,
    control_version = EXCLUDED.control_version,
    timing_version = EXCLUDED.timing_version,
    state_blob = EXCLUDED.state_blob,
    state_sha256 = EXCLUDED.state_sha256,
    control_blob = EXCLUDED.control_blob,
    control_sha256 = EXCLUDED.control_sha256,
    timing_blob = EXCLUDED.timing_blob,
    timing_sha256 = EXCLUDED.timing_sha256,
    cheat_blob = EXCLUDED.cheat_blob,
    cheat_sha256 = EXCLUDED.cheat_sha256,
    facts_blob = EXCLUDED.facts_blob,
    facts_sha256 = EXCLUDED.facts_sha256,
    history_blob = EXCLUDED.history_blob,
    history_sha256 = EXCLUDED.history_sha256,
    bindings_blob = EXCLUDED.bindings_blob,
    bindings_sha256 = EXCLUDED.bindings_sha256,
    persisted_at = clock_timestamp()";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", checkpoint.RoomId);
            AddBigint(command, "state_version", checked((long)checkpoint.StateVersion.Value));
            AddBigint(command, "last_event_seq", checked((long)checkpoint.LastEventSeq.Value));
            AddNumeric(command, "state_fingerprint", ToNumeric(checkpoint.StateFingerprint));
            Add(command, "ruleset", checkpoint.RulesetVersion, NpgsqlDbType.Integer);
            Add(command, "schema", checkpoint.SchemaVersion, NpgsqlDbType.Integer);
            AddText(command, "boot_id", checkpoint.BootId);
            Add(command, "created_at", checked((long)checkpoint.CreatedAtUtcMs), NpgsqlDbType.Bigint);
            AddBigint(command, "captured_monotonic", checkpoint.CapturedMonotonicMillis);
            AddBigint(command, "control_version", checkpoint.ControlVersion);
            AddBigint(command, "timing_version", checkpoint.TimingVersion);
            AddBytea(command, "state_blob", state);
            AddBytea(command, "state_sha256", checkpoint.StatePayloadSha256.ToArray());
            AddBytea(command, "control_blob", control);
            AddBytea(command, "control_sha256", checkpoint.ControlPayloadSha256.ToArray());
            AddBytea(command, "timing_blob", timing);
            AddBytea(command, "timing_sha256", checkpoint.TimingPayloadSha256.ToArray());
            AddBytea(command, "cheat_blob", cheat);
            AddBytea(command, "cheat_sha256", checkpoint.CheatPayloadSha256?.ToArray());
            AddBytea(command, "facts_blob", facts);
            AddBytea(command, "facts_sha256", checkpoint.CommittedFactsPayloadSha256?.ToArray());
            AddBytea(command, "history_blob", history);
            AddBytea(command, "history_sha256", checkpoint.CommittedHistoryPayloadSha256?.ToArray());
            AddBytea(command, "bindings_blob", bindings);
            AddBytea(command, "bindings_sha256", checkpoint.CommittedCommandBindingsPayloadSha256?.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task InsertTimingJournalAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            long timingVersion,
            StateVersion stateVersion,
            long controlVersion,
            string bootId,
            ulong capturedAtUtcMs,
            long capturedMonotonicMillis,
            byte[] timingPayload,
            TablePayloadSha256 timingHash,
            CancellationToken cancellationToken)
        {
            byte[] blob = _protector.Seal(
                timingPayload,
                Aad("timing", roomId + "|" + timingVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            const string sql = @"
INSERT INTO table_store.table_timing_journal
    (room_id, timing_version, state_version, control_version, boot_id,
     captured_at_utc_ms, captured_monotonic_ms, timing_blob, timing_sha256)
VALUES
    (@room_id, @timing_version, @state_version, @control_version, @boot_id,
     @captured_at, @captured_monotonic, @timing_blob, @timing_sha256)
ON CONFLICT (room_id, timing_version) DO NOTHING";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            AddBigint(command, "timing_version", timingVersion);
            AddBigint(command, "state_version", checked((long)stateVersion.Value));
            AddBigint(command, "control_version", controlVersion);
            AddText(command, "boot_id", bootId);
            Add(command, "captured_at", checked((long)capturedAtUtcMs), NpgsqlDbType.Bigint);
            AddBigint(command, "captured_monotonic", capturedMonotonicMillis);
            AddBytea(command, "timing_blob", blob);
            AddBytea(command, "timing_sha256", timingHash.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task UpsertCommittedFactsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            byte[] payload,
            TablePayloadSha256 hash,
            CancellationToken cancellationToken)
        {
            if (TablePayloadSha256.Compute(payload) != hash)
            {
                throw new InvalidDataException("Committed facts payload does not match its digest.");
            }

            const string sql = @"
INSERT INTO table_store.table_committed_facts (room_id, payload_blob, payload_sha256, updated_at)
VALUES (@room_id, @payload, @hash, clock_timestamp())
ON CONFLICT (room_id) DO UPDATE
SET payload_blob = EXCLUDED.payload_blob,
    payload_sha256 = EXCLUDED.payload_sha256,
    updated_at = clock_timestamp()";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            AddBytea(command, "payload", _protector.Seal(payload, Aad("facts", roomId)));
            AddBytea(command, "hash", hash.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task UpdateStateTimingAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            long timingVersion,
            CancellationToken cancellationToken)
        {
            const string sql = @"
UPDATE table_store.table_state
   SET timing_version = @timing_version,
       updated_at = clock_timestamp()
 WHERE room_id = @room_id";
            await using NpgsqlCommand command = new NpgsqlCommand(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            AddBigint(command, "timing_version", timingVersion);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> RefreshLeaseFenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string roomId,
            string ownerInstance,
            OwnerEpoch ownerEpoch,
            CancellationToken cancellationToken)
        {
            const string sql = @"
UPDATE table_store.table_lease
   SET updated_at = clock_timestamp()
 WHERE room_id = @room_id
   AND owner_instance = @owner_instance
   AND owner_epoch = @owner_epoch
   AND lease_expires_at > clock_timestamp()
RETURNING 1";
            await using NpgsqlCommand command = new NpgsqlCommand(sql, connection, transaction);
            AddText(command, "room_id", roomId);
            AddText(command, "owner_instance", ownerInstance);
            Add(command, "owner_epoch", checked((long)ownerEpoch.Value), NpgsqlDbType.Bigint);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null && result != DBNull.Value;
        }

        private async Task InsertControlJournalAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            TableControlCommitRequest request,
            CancellationToken cancellationToken)
        {
            TableControlEvent change = request.Change;
            const string sql = @"
INSERT INTO table_store.table_control_journal
    (room_id, control_version, state_version, round_id, seat, kind,
     previous_epoch, new_epoch, previous_kind, new_kind, previous_status, new_status,
     grace_deadline_millis, at_monotonic_millis, at_utc_millis, reason,
     disconnected_at_millis, ai_profile_id, policy_mode, policy_grace_millis,
     policy_ai_profile_id, owner_instance)
VALUES
    (@room_id, @control_version, @state_version, @round_id, @seat, @kind,
     @previous_epoch, @new_epoch, @previous_kind, @new_kind, @previous_status, @new_status,
     @grace_deadline, @at_monotonic, @at_utc, @reason,
     @disconnected_at, @ai_profile, @policy_mode, @policy_grace,
     @policy_ai_profile, @owner_instance)";
            await using NpgsqlCommand command = Command(sql, connection, transaction);
            AddText(command, "room_id", request.RoomId);
            AddBigint(command, "control_version", request.ControlVersion);
            AddBigint(command, "state_version", checked((long)request.StateVersion.Value));
            AddText(command, "round_id", request.RoundId);
            Add(command, "seat", change.Seat, NpgsqlDbType.Integer);
            Add(command, "kind", (int)change.Kind, NpgsqlDbType.Integer);
            AddNullableBigint(command, "previous_epoch", change.PreviousEpoch);
            AddNullableBigint(command, "new_epoch", change.NewEpoch);
            AddNullableBigint(command, "previous_kind", change.PreviousKind.HasValue ? (long?)(int)change.PreviousKind.Value : null);
            AddNullableBigint(command, "new_kind", change.NewKind.HasValue ? (long?)(int)change.NewKind.Value : null);
            AddNullableBigint(command, "previous_status", change.PreviousStatus.HasValue ? (long?)(int)change.PreviousStatus.Value : null);
            AddNullableBigint(command, "new_status", change.NewStatus.HasValue ? (long?)(int)change.NewStatus.Value : null);
            AddNullableBigint(command, "grace_deadline", change.GraceDeadlineMillis);
            AddBigint(command, "at_monotonic", change.AtMonotonicMillis);
            AddBigint(command, "at_utc", change.AtUtcMillis);
            AddText(command, "reason", change.Reason);
            AddNullableBigint(command, "disconnected_at", change.DisconnectedAtMillis);
            AddText(command, "ai_profile", change.AiProfileId);
            AddNullableBigint(command, "policy_mode", change.PolicyMode.HasValue ? (long?)(int)change.PolicyMode.Value : null);
            AddNullableBigint(command, "policy_grace", change.PolicyGraceMillis);
            AddText(command, "policy_ai_profile", change.PolicyAiProfileId);
            AddText(command, "owner_instance", request.OwnerInstance);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static TableDurableCommitResult? ValidateCheckpointContinuity(
            TableCheckpoint? previous,
            TableCheckpoint next)
        {
            if (previous == null)
            {
                return null;
            }

            try
            {
                if (previous.CheatPayload != null && next.CheatPayload == null)
                {
                    return TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "A checkpoint cannot erase the existing required cheat ledger.");
                }

                if (previous.ControlVersion == next.ControlVersion &&
                    !previous.ControlPayload.AsSpan().SequenceEqual(next.ControlPayload))
                {
                    TableControlCheckpoint before = TableControlCheckpointCodec.Decode(previous.ControlPayload);
                    TableControlCheckpoint after = TableControlCheckpointCodec.Decode(next.ControlPayload);
                    if (!IsValidRecoveredControlRebase(
                            before.Snapshot,
                            after.Snapshot,
                            previous.BootId,
                            next.BootId,
                            next.CapturedMonotonicMillis))
                    {
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "A same-version checkpoint cannot rewrite the authoritative control snapshot.");
                    }
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is IOException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "Control snapshot is invalid: " + exception.Message);
            }

            return null;
        }

        private static bool IsValidRecoveredControlRebase(
            ControlRegistrySnapshot before,
            ControlRegistrySnapshot after,
            string beforeBootId,
            string afterBootId,
            long nowMonotonic)
        {
            if (before.Policy.Mode != after.Policy.Mode ||
                before.Policy.GraceMillis != after.Policy.GraceMillis ||
                !string.Equals(before.Policy.AiProfileId, after.Policy.AiProfileId, StringComparison.Ordinal) ||
                before.PreferredHumanKinds.Count != after.PreferredHumanKinds.Count)
            {
                return false;
            }

            for (int i = 0; i < before.PreferredHumanKinds.Count; i++)
            {
                if (before.PreferredHumanKinds[i] != after.PreferredHumanKinds[i])
                {
                    return false;
                }
            }

            bool sameBoot = string.Equals(beforeBootId, afterBootId, StringComparison.Ordinal);
            for (int seat = 0; seat < 4; seat++)
            {
                SeatControlState left = before.Seats[seat];
                SeatControlState right = after.Seats[seat];
                if (left.Seat != right.Seat ||
                    left.Status != right.Status ||
                    left.Kind != right.Kind ||
                    left.ControlEpoch != right.ControlEpoch ||
                    !string.Equals(left.AiProfileId, right.AiProfileId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (left.Status == ConnectionStatus.Connected)
                {
                    if (right.DisconnectedAtMillis.HasValue || right.GraceDeadlineMillis.HasValue)
                    {
                        return false;
                    }

                    continue;
                }

                if (left.DisconnectedAtMillis.HasValue != right.DisconnectedAtMillis.HasValue ||
                    left.GraceDeadlineMillis.HasValue != right.GraceDeadlineMillis.HasValue)
                {
                    return false;
                }

                if (!left.DisconnectedAtMillis.HasValue)
                {
                    continue;
                }

                long oldDisconnected = left.DisconnectedAtMillis.Value;
                long newDisconnected = right.DisconnectedAtMillis!.Value;
                if (newDisconnected < 0 || newDisconnected > nowMonotonic ||
                    (sameBoot && newDisconnected < oldDisconnected))
                {
                    return false;
                }

                if (left.GraceDeadlineMillis.HasValue)
                {
                    if (!sameBoot)
                    {
                        return false;
                    }

                    long oldDeadline = left.GraceDeadlineMillis.Value;
                    long newDeadline = right.GraceDeadlineMillis!.Value;
                    if (oldDeadline > nowMonotonic)
                    {
                        if (newDeadline != oldDeadline)
                        {
                            return false;
                        }
                    }
                    else if (newDeadline > nowMonotonic)
                    {
                        return false;
                    }

                    long oldDuration = oldDeadline - oldDisconnected;
                    long newDuration = newDeadline - newDisconnected;
                    if (oldDuration < 0 || newDuration < 0 || newDuration > oldDuration)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}

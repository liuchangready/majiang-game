using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Host-issued scope registry. It is intentionally a separate object from the table actor:
    /// the actor only ever learns account/role/room from this trusted record. The same object
    /// implements <see cref="ITableTransactionalScopeReader"/> so the in-memory store can
    /// re-read the scope under the commit lock, mirroring the PostgreSQL row lock.
    /// </summary>
    public sealed class InMemoryRequestScopeAuthority : IRequestScopeAuthority, ITableTransactionalScopeReader
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, TableScopeRecord> _scopes = new Dictionary<string, TableScopeRecord>(StringComparer.Ordinal);

        public TableScopeRecord Issue(
            string accountId,
            TableScopeRole role,
            string? roomId,
            ulong issuedAtUtcMs,
            ulong expiresAtUtcMs,
            string? scopeId = null)
        {
            string id = scopeId ?? Guid.NewGuid().ToString("N");
            var record = new TableScopeRecord(
                new RequestScopeId(id),
                accountId,
                role,
                roomId,
                issuedAtUtcMs,
                expiresAtUtcMs);
            lock (_gate)
            {
                if (_scopes.ContainsKey(id))
                {
                    throw new InvalidOperationException("Scope id already exists.");
                }

                _scopes.Add(id, record);
            }

            return record;
        }

        public bool Revoke(RequestScopeId scopeId, ulong revokedAtUtcMs)
        {
            lock (_gate)
            {
                if (!_scopes.TryGetValue(scopeId.Value, out TableScopeRecord? existing))
                {
                    return false;
                }

                _scopes[scopeId.Value] = new TableScopeRecord(
                    existing.ScopeId,
                    existing.AccountId,
                    existing.Role,
                    existing.RoomId,
                    existing.IssuedAtUtcMs,
                    existing.ExpiresAtUtcMs,
                    revokedAtUtcMs);
                return true;
            }
        }

        public TableScopeReadLease BeginScopeForCommit(RequestScopeId scopeId)
        {
            Monitor.Enter(_gate);
            try
            {
                _scopes.TryGetValue(scopeId.Value, out TableScopeRecord? scope);
                return new TableScopeReadLease(scope, () => Monitor.Exit(_gate));
            }
            catch
            {
                Monitor.Exit(_gate);
                throw;
            }
        }

        public TableScopeRecord? ReadScopeForCommit(RequestScopeId scopeId)
        {
            lock (_gate)
            {
                return _scopes.TryGetValue(scopeId.Value, out TableScopeRecord? scope) ? scope : null;
            }
        }

        public ValueTask<TableScopeLookup> LookupAsync(RequestScopeId scopeId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TableScopeRecord? scope;
            lock (_gate)
            {
                _scopes.TryGetValue(scopeId.Value, out scope);
            }

            return new ValueTask<TableScopeLookup>(
                scope == null ? TableScopeLookup.Missing() : TableScopeLookup.FromScope(scope));
        }
    }

    public enum TableStoreFaultPoint : byte
    {
        AfterReceiptLookup = 0,
        AfterScopeCheck = 1,
        AfterLeaseCheck = 2,
        AfterStateCheck = 3,
        AfterEventStage = 4,
        AfterReceiptStage = 5,
        AfterOutboxStage = 6,
        BeforeApply = 7,
        AfterApplyBeforeReturn = 8
    }

    public sealed class TableStoreInjectedFaultException : Exception
    {
        public TableStoreInjectedFaultException(TableStoreFaultPoint point)
            : base("Injected table-store fault at " + point + ".")
        {
            Point = point;
        }

        public TableStoreFaultPoint Point { get; }
    }

    /// <summary>
    /// In-memory implementation of all four logical durable ports with PostgreSQL-shaped
    /// transaction rules:
    /// - one lock is the transaction; staged writes are applied only at the end;
    /// - receipt lookup precedes scope validation, then lease, then state;
    /// - accepted batches must carry contiguous events and a command journal;
    /// - REJECTED writes only a receipt (and still passes the owner/state fences);
    /// - one terminal outbox key can never produce a second result;
    /// - a checkpoint is independently encoded data, never a live actor object.
    ///
    /// It is a contract fake for P3.4, not a PostgreSQL implementation. P5.2 must preserve the
    /// same ordering and constraints in one database transaction.
    /// </summary>
    public sealed class InMemoryTableStore :
        ITableStore,
        IReceiptStore,
        ITableLease,
        ISettlementOutbox
    {
        private readonly object _gate = new object();
        private readonly ITableClock _clock;
        private readonly InMemoryRequestScopeAuthority _scopes;
        private readonly HashSet<string> _rooms = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, LeaseRow> _leases = new Dictionary<string, LeaseRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, StateRow> _states = new Dictionary<string, StateRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TableEventRecord>> _events =
            new Dictionary<string, List<TableEventRecord>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TableCommandJournalRecord>> _commands =
            new Dictionary<string, List<TableCommandJournalRecord>>(StringComparer.Ordinal);
        private readonly Dictionary<string, CheckpointRow> _checkpoints = new Dictionary<string, CheckpointRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, TableTimingJournalRecord> _timings =
            new Dictionary<string, TableTimingJournalRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, TableCommittedFactsSnapshot> _facts =
            new Dictionary<string, TableCommittedFactsSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TableRecoveryAuditRecord>> _audits =
            new Dictionary<string, List<TableRecoveryAuditRecord>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ReceiptRow> _receipts = new Dictionary<string, ReceiptRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, OutboxRow> _outbox = new Dictionary<string, OutboxRow>(StringComparer.Ordinal);

        public InMemoryTableStore(ITableClock clock, InMemoryRequestScopeAuthority scopes)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        }

        /// <summary>Test hook: the next matching fault is consumed and never silently retried.</summary>
        public TableStoreFaultPoint? ArmedFaultPoint { get; set; }

        /// <summary>
        /// Test hook for the commit-boundary revocation/expiry race. It runs after the receipt
        /// lookup and immediately before the transactional scope read, so a test can commit a
        /// revocation at the documented linearization point without sleeping.
        /// </summary>
        public Action? BeforeScopeCheckForTest { get; set; }

        /// <summary>Test hook invoked while the scope read lease is held and before commit apply.</summary>
        public Action? AfterScopeCheckForTest { get; set; }

        /// <summary>Test hook: replaces the latest checkpoint without store-side validation.</summary>
        public void ReplaceCheckpointForTest(string roomId, TableCheckpoint checkpoint)
        {
            lock (_gate)
            {
                _checkpoints[roomId] = new CheckpointRow(checkpoint);
            }
        }

        public void RemoveEventForTest(string roomId, ulong seq)
        {
            lock (_gate)
            {
                if (_events.TryGetValue(roomId, out List<TableEventRecord>? events))
                {
                    events.RemoveAll(value => value.Seq.Value == seq);
                }
            }
        }

        public void SetEventOrdinalForTest(string roomId, ulong seq, int ordinal)
        {
            lock (_gate)
            {
                if (!_events.TryGetValue(roomId, out List<TableEventRecord>? events))
                {
                    return;
                }

                for (int i = 0; i < events.Count; i++)
                {
                    TableEventRecord value = events[i];
                    if (value.Seq.Value == seq)
                    {
                        events[i] = new TableEventRecord(
                            value.Seq,
                            value.CommandVersion,
                            ordinal,
                            value.Type,
                            value.Seat,
                            value.Tile,
                            value.Detail,
                            value.EventSchema,
                            value.Payload);
                        return;
                    }
                }
            }
        }

        public void RemoveCommandForTest(string roomId, ulong version)
        {
            lock (_gate)
            {
                if (_commands.TryGetValue(roomId, out List<TableCommandJournalRecord>? commands))
                {
                    commands.RemoveAll(value => value.CommandVersion.Value == version);
                }
            }
        }

        public void SetStateHashForTest(string roomId, TablePayloadSha256 hash)
        {
            lock (_gate)
            {
                if (_states.TryGetValue(roomId, out StateRow? state))
                {
                    state.StateHash = hash;
                }
            }
        }

        public void SetStateFingerprintForTest(string roomId, ulong fingerprint)
        {
            lock (_gate)
            {
                if (_states.TryGetValue(roomId, out StateRow? state))
                {
                    state.StateFingerprint = fingerprint;
                }
            }
        }

        /// <summary>Negative-control hook: tamper with the committed complete-cheat binding.</summary>
        public void SetCheatBindingForTest(string roomId, long version, ulong fingerprint)
        {
            lock (_gate)
            {
                if (_states.TryGetValue(roomId, out StateRow? state))
                {
                    state.CheatVersion = version;
                    state.CheatFingerprint = fingerprint;
                }
            }
        }

        public void SetCommittedFactsForTest(string roomId, byte[] payload, TablePayloadSha256 sha256)
        {
            lock (_gate)
            {
                _facts[roomId] = new TableCommittedFactsSnapshot(payload, sha256);
            }
        }

        public int CommitCount { get; private set; }
        public int ReceiptCount
        {
            get
            {
                lock (_gate)
                {
                    return _receipts.Count;
                }
            }
        }

        public int EventCount
        {
            get
            {
                lock (_gate)
                {
                    int total = 0;
                    foreach (List<TableEventRecord> events in _events.Values)
                    {
                        total += events.Count;
                    }

                    return total;
                }
            }
        }

        public int OutboxCount
        {
            get
            {
                lock (_gate)
                {
                    return _outbox.Count;
                }
            }
        }

        public TableCheckpoint? LastCheckpoint
        {
            get
            {
                lock (_gate)
                {
                    TableCheckpoint? last = null;
                    foreach (CheckpointRow row in _checkpoints.Values)
                    {
                        if (last == null || row.Checkpoint.StateVersion.CompareTo(last.StateVersion) > 0)
                        {
                            last = row.Checkpoint;
                        }
                    }

                    return last;
                }
            }
        }

        public ValueTask CreateRoomAsync(TableCheckpoint initialCheckpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (initialCheckpoint == null)
            {
                throw new ArgumentNullException(nameof(initialCheckpoint));
            }

            lock (_gate)
            {
                if (!_rooms.Add(initialCheckpoint.RoomId))
                {
                    throw new InvalidOperationException("Room already exists.");
                }

                long initialTimingVersion = initialCheckpoint.TimingVersion == 0 ? 1L : initialCheckpoint.TimingVersion;
                _states.Add(
                    initialCheckpoint.RoomId,
                    new StateRow(
                        initialCheckpoint.StateVersion,
                        initialCheckpoint.LastEventSeq,
                        initialCheckpoint.StateFingerprint,
                        initialCheckpoint.StatePayloadSha256,
                        initialCheckpoint.RulesetVersion,
                        initialCheckpoint.ControlVersion,
                        (ulong)_clock.UtcNowMs,
                        initialTimingVersion));
                _events.Add(initialCheckpoint.RoomId, new List<TableEventRecord>());
                _commands.Add(initialCheckpoint.RoomId, new List<TableCommandJournalRecord>());
                _audits.Add(initialCheckpoint.RoomId, new List<TableRecoveryAuditRecord>());
                _checkpoints[initialCheckpoint.RoomId] = new CheckpointRow(initialCheckpoint);
                _timings[initialCheckpoint.RoomId] = new TableTimingJournalRecord(
                    initialCheckpoint.RoomId,
                    initialTimingVersion,
                    initialCheckpoint.StateVersion,
                    initialCheckpoint.ControlVersion,
                    initialCheckpoint.BootId,
                    initialCheckpoint.CreatedAtUtcMs,
                    initialCheckpoint.CapturedMonotonicMillis,
                    TableTimingCheckpointCodec.Decode(initialCheckpoint.TimingPayload));
                if (initialCheckpoint.CommittedFactsPayload != null)
                {
                    _facts[initialCheckpoint.RoomId] = new TableCommittedFactsSnapshot(
                        initialCheckpoint.CommittedFactsPayload,
                        initialCheckpoint.CommittedFactsPayloadSha256!.Value);
                }
            }

            return default;
        }

        public ValueTask<TableStateRow?> LoadStateAsync(string roomId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_states.TryGetValue(roomId, out StateRow? row))
                {
                    return new ValueTask<TableStateRow?>((TableStateRow?)null);
                }

                return new ValueTask<TableStateRow?>(row.ToPublic(roomId));
            }
        }

        public ValueTask<TableCheckpoint?> LoadCheckpointAsync(string roomId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return new ValueTask<TableCheckpoint?>(
                    _checkpoints.TryGetValue(roomId, out CheckpointRow? row) ? row.Checkpoint : null);
            }
        }

        public ValueTask<TableTimingJournalRecord?> LoadTimingAsync(string roomId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return new ValueTask<TableTimingJournalRecord?>(
                    _timings.TryGetValue(roomId, out TableTimingJournalRecord? row) ? row : null);
            }
        }

        public ValueTask<TableCommittedFactsSnapshot?> LoadCommittedFactsAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return new ValueTask<TableCommittedFactsSnapshot?>(
                    _facts.TryGetValue(roomId, out TableCommittedFactsSnapshot? facts) ? facts : null);
            }
        }

        private TableDurableCommitResult? ValidateCheatCarryover(
            string roomId,
            TableCheckpoint checkpoint)
        {
            if (_checkpoints.TryGetValue(roomId, out CheckpointRow? existing) &&
                existing.Checkpoint.CheatPayload != null &&
                checkpoint.CheatPayload == null)
            {
                return TableDurableCommitResult.Failed(
                    TableRuntimeReasonCode.CheckpointInvalid,
                    "A checkpoint cannot erase the existing required cheat ledger.");
            }

            return null;
        }

        private TableDurableCommitResult? ValidateControlSnapshotUnchanged(
            string roomId,
            TableCheckpoint checkpoint)
        {
            if (!_checkpoints.TryGetValue(roomId, out CheckpointRow? existing) ||
                existing.Checkpoint.ControlVersion != checkpoint.ControlVersion)
            {
                return null;
            }

            if (checkpoint.ControlPayload.AsSpan().SequenceEqual(
                    existing.Checkpoint.ControlPayload))
            {
                return null;
            }

            try
            {
                TableControlCheckpoint before = TableControlCheckpointCodec.Decode(
                    existing.Checkpoint.ControlPayload);
                TableControlCheckpoint after = TableControlCheckpointCodec.Decode(
                    checkpoint.ControlPayload);
                if (IsValidRecoveredControlRebase(
                        before.Snapshot,
                        after.Snapshot,
                        existing.Checkpoint.BootId,
                        checkpoint.BootId,
                        _clock.MonotonicMillis))
                {
                    return null;
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

            return TableDurableCommitResult.Failed(
                TableRuntimeReasonCode.CheckpointInvalid,
                "A same-version checkpoint cannot rewrite the authoritative control snapshot.");
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
                    if (right.DisconnectedAtMillis.HasValue ||
                        right.GraceDeadlineMillis.HasValue)
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
                        // Grace was still live at save time: the absolute deadline cannot be
                        // moved (neither shortened nor extended) by a same-version checkpoint.
                        if (newDeadline != oldDeadline)
                        {
                            return false;
                        }
                    }
                    else if (newDeadline > nowMonotonic)
                    {
                        // Grace was already expired at save time; it may stay pinned to its
                        // recovery-time now, but can never regain a future deadline.
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

        private static TableDurableCommitResult? ValidateAcceptedTiming(
            TableDurableCommitRequest request,
            TableCheckpoint? checkpoint)
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

                if (checkpoint != null)
                {
                    byte[] encoded = TableTimingCheckpointCodec.Encode(request.Timing);
                    if (!encoded.AsSpan().SequenceEqual(checkpoint.TimingPayload))
                    {
                        return TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Accepted timing journal and checkpoint timing payload disagree.");
                    }
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

        public ValueTask<TableDurableCommitResult> SaveCheckpointAsync(
            TableCheckpoint checkpoint,
            TableCheckpointFence fence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            if (fence == null)
            {
                throw new ArgumentNullException(nameof(fence));
            }

            lock (_gate)
            {
                if (!string.Equals(checkpoint.RoomId, fence.RoomId, StringComparison.Ordinal))
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(TableRuntimeReasonCode.CheckpointInvalid, "Fence room does not match checkpoint."));
                }

                TableDurableCommitResult? fenceFailure = ValidateFence(
                    fence.RoomId,
                    fence.OwnerInstance,
                    fence.ExpectedOwnerEpoch,
                    fence.ExpectedStateVersion,
                    fence.ExpectedControlVersion,
                    cancellationToken);
                if (fenceFailure != null)
                {
                    return new ValueTask<TableDurableCommitResult>(fenceFailure);
                }

                StateRow state = _states[fence.RoomId];
                if (state.TimingVersion != fence.ExpectedTimingVersion)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(TableRuntimeReasonCode.StaleStateVersion, "Expected timing version is stale."));
                }

                if (checkpoint.StateVersion != state.StateVersion ||
                    checkpoint.LastEventSeq != state.LastEventSeq ||
                    checkpoint.StateFingerprint != state.StateFingerprint ||
                    checkpoint.StatePayloadSha256 != state.StateHash ||
                    checkpoint.ControlVersion != state.ControlVersion ||
                    checkpoint.RulesetVersion != state.RulesetVersion)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "A checkpoint does not match the current committed state/control/ruleset."));
                }

                if (checkpoint.TimingVersion != state.TimingVersion + 1)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "A checkpoint must advance timing version by exactly one."));
                }

                if (!TableCheckpointValidator.TryValidate(
                        checkpoint,
                        out string validationReason,
                        out string validationDetail))
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(validationReason, validationDetail));
                }

                TableDurableCommitResult? cheatCarryover = ValidateCheatCarryover(checkpoint.RoomId, checkpoint);
                if (cheatCarryover != null)
                {
                    return new ValueTask<TableDurableCommitResult>(cheatCarryover);
                }

                TableDurableCommitResult? controlSnapshot =
                    ValidateControlSnapshotUnchanged(checkpoint.RoomId, checkpoint);
                if (controlSnapshot != null)
                {
                    return new ValueTask<TableDurableCommitResult>(controlSnapshot);
                }

                if (TablePayloadSha256.Compute(checkpoint.TimingPayload) != checkpoint.TimingPayloadSha256)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "Checkpoint timing payload/boot is inconsistent."));
                }

                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                _checkpoints[checkpoint.RoomId] = new CheckpointRow(checkpoint);
                if (checkpoint.CommittedFactsPayload != null)
                {
                    _facts[checkpoint.RoomId] = new TableCommittedFactsSnapshot(
                        checkpoint.CommittedFactsPayload,
                        checkpoint.CommittedFactsPayloadSha256!.Value);
                }
                _timings[checkpoint.RoomId] = new TableTimingJournalRecord(
                    checkpoint.RoomId,
                    checkpoint.TimingVersion,
                    checkpoint.StateVersion,
                    checkpoint.ControlVersion,
                    checkpoint.BootId,
                    checkpoint.CreatedAtUtcMs,
                    checkpoint.CapturedMonotonicMillis,
                    timing);
                state.TimingVersion = checkpoint.TimingVersion;
                _leases[checkpoint.RoomId].UpdatedAtUtcMs = (ulong)_clock.UtcNowMs;
                CommitCount++;
                return new ValueTask<TableDurableCommitResult>(TableDurableCommitResult.Committed(null));
            }
        }

        public ValueTask<TableDurableCommitResult> AppendTimingAsync(
            TableTimingAppendRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (_gate)
            {
                TableDurableCommitResult? fenceFailure = ValidateFence(
                    request.RoomId,
                    request.OwnerInstance,
                    request.ExpectedOwnerEpoch,
                    request.ExpectedStateVersion,
                    request.ExpectedControlVersion,
                    cancellationToken);
                if (fenceFailure != null)
                {
                    return new ValueTask<TableDurableCommitResult>(fenceFailure);
                }

                StateRow state = _states[request.RoomId];
                if (state.TimingVersion != request.ExpectedTimingVersion)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(TableRuntimeReasonCode.StaleStateVersion, "Expected timing version is stale."));
                }

                long nextVersion = checked(state.TimingVersion + 1L);
                string boot = request.Timing.BootId;
                if (string.IsNullOrWhiteSpace(boot))
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.RecoveryTimingInvalid,
                            "Timing append requires a boot id."));
                }
                _timings[request.RoomId] = new TableTimingJournalRecord(
                    request.RoomId,
                    nextVersion,
                    state.StateVersion,
                    state.ControlVersion,
                    boot,
                    request.Timing.CapturedAtUtcMs,
                    request.Timing.CapturedMonotonicMillis,
                    request.Timing);
                state.TimingVersion = nextVersion;
                _leases[request.RoomId].UpdatedAtUtcMs = (ulong)_clock.UtcNowMs;
                CommitCount++;
                return new ValueTask<TableDurableCommitResult>(TableDurableCommitResult.Committed(null));
            }
        }

        private string _durableBootId(string roomId)
        {
            return _timings.TryGetValue(roomId, out TableTimingJournalRecord? existing)
                ? existing.BootId
                : _checkpoints.TryGetValue(roomId, out CheckpointRow? checkpoint)
                    ? checkpoint.Checkpoint.BootId
                    : "unknown";
        }

        private TableDurableCommitResult? ValidateFence(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedOwnerEpoch,
            StateVersion expectedStateVersion,
            long expectedControlVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong now = (ulong)_clock.UtcNowMs;
            if (!_leases.TryGetValue(roomId, out LeaseRow? lease) ||
                !string.Equals(lease.OwnerInstance, ownerInstance, StringComparison.Ordinal) ||
                lease.OwnerEpoch != expectedOwnerEpoch ||
                now >= lease.ExpiresAtUtcMs)
            {
                return TableDurableCommitResult.StaleOwner("The lease is not owned by this instance/epoch or has expired.");
            }

            if (!_states.TryGetValue(roomId, out StateRow? state) ||
                state.StateVersion != expectedStateVersion)
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

        public ValueTask<TableDurableCommitResult> CommitControlAsync(
            TableControlCommitRequest request,
            TableCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                return new ValueTask<TableDurableCommitResult>(
                    TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Control checkpoint room does not match the fenced request room."));
            }

            lock (_gate)
            {
                if (!_rooms.Contains(request.RoomId))
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(TableRuntimeReasonCode.CheckpointMissing, "Room is not initialized."));
                }

                ulong now = (ulong)_clock.UtcNowMs;
                if (!_leases.TryGetValue(request.RoomId, out LeaseRow? lease) ||
                    !string.Equals(lease.OwnerInstance, request.OwnerInstance, StringComparison.Ordinal) ||
                    lease.OwnerEpoch != request.OwnerEpoch ||
                    now >= lease.ExpiresAtUtcMs)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.StaleOwner("The control change does not own a live lease."));
                }

                if (!_states.TryGetValue(request.RoomId, out StateRow? state) ||
                    state.StateVersion != request.StateVersion)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.StaleState("The control change references a stale state version."));
                }

                if (state.ControlVersion != request.ExpectedControlVersion)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.StaleControlVersion,
                            "The expected control version is not current."));
                }

                if (checkpoint.StateVersion != state.StateVersion ||
                    checkpoint.LastEventSeq != state.LastEventSeq ||
                    checkpoint.StateFingerprint != state.StateFingerprint ||
                    checkpoint.StatePayloadSha256 != state.StateHash ||
                    checkpoint.ControlVersion != request.ControlVersion ||
                    checkpoint.TimingVersion != state.TimingVersion + 1L ||
                    checkpoint.RulesetVersion != state.RulesetVersion)
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(
                            TableRuntimeReasonCode.CheckpointInvalid,
                            "The control checkpoint does not match table_state/timing/ruleset."));
                }

                if (!TableCheckpointValidator.TryValidate(
                        checkpoint,
                        out string validationReason,
                        out string validationDetail))
                {
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(validationReason, validationDetail));
                }

                TableDurableCommitResult? cheatCarryover = ValidateCheatCarryover(request.RoomId, checkpoint);
                if (cheatCarryover != null)
                {
                    return new ValueTask<TableDurableCommitResult>(cheatCarryover);
                }

                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                state.ControlVersion = request.ControlVersion;
                state.TimingVersion = checkpoint.TimingVersion;
                state.UpdatedAtUtcMs = now;
                _timings[request.RoomId] = new TableTimingJournalRecord(
                    request.RoomId,
                    checkpoint.TimingVersion,
                    state.StateVersion,
                    state.ControlVersion,
                    checkpoint.BootId,
                    checkpoint.CreatedAtUtcMs,
                    checkpoint.CapturedMonotonicMillis,
                    timing);
                _checkpoints[request.RoomId] = new CheckpointRow(checkpoint);
                if (checkpoint.CommittedFactsPayload != null)
                {
                    _facts[request.RoomId] = new TableCommittedFactsSnapshot(
                        checkpoint.CommittedFactsPayload,
                        checkpoint.CommittedFactsPayloadSha256!.Value);
                }
                lease.UpdatedAtUtcMs = now;
                CommitCount++;
                return new ValueTask<TableDurableCommitResult>(TableDurableCommitResult.Committed(null));
            }
        }

        public ValueTask<IReadOnlyList<TableEventRecord>> LoadEventsAfterAsync(
            string roomId,
            EventSeq afterSeq,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_events.TryGetValue(roomId, out List<TableEventRecord>? events))
                {
                    return new ValueTask<IReadOnlyList<TableEventRecord>>(Array.Empty<TableEventRecord>());
                }

                var copy = new List<TableEventRecord>();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Seq.Value > afterSeq.Value)
                    {
                        copy.Add(events[i]);
                    }
                }

                return new ValueTask<IReadOnlyList<TableEventRecord>>(new ReadOnlyCollection<TableEventRecord>(copy));
            }
        }

        public ValueTask<IReadOnlyList<TableCommandJournalRecord>> LoadCommandsAfterAsync(
            string roomId,
            StateVersion afterVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_commands.TryGetValue(roomId, out List<TableCommandJournalRecord>? commands))
                {
                    return new ValueTask<IReadOnlyList<TableCommandJournalRecord>>(
                        Array.Empty<TableCommandJournalRecord>());
                }

                var copy = new List<TableCommandJournalRecord>();
                for (int i = 0; i < commands.Count; i++)
                {
                    if (commands[i].CommandVersion.CompareTo(afterVersion) > 0)
                    {
                        copy.Add(commands[i]);
                    }
                }

                return new ValueTask<IReadOnlyList<TableCommandJournalRecord>>(
                    new ReadOnlyCollection<TableCommandJournalRecord>(copy));
            }
        }

        public ValueTask<TableDurableCommitResult> CommitAsync(
            TableDurableCommitRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Checkpoint != null &&
                !string.Equals(request.Checkpoint.RoomId, request.RoomId, StringComparison.Ordinal))
            {
                return new ValueTask<TableDurableCommitResult>(
                    TableDurableCommitResult.Failed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        "Commit checkpoint room does not match the fenced request room."));
            }

            lock (_gate)
            {
                bool applied = false;
                TableScopeReadLease? scopeLease = null;
                try
                {
                    if (!_rooms.Contains(request.RoomId))
                    {
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.Failed(TableRuntimeReasonCode.CheckpointMissing, "Room is not initialized."));
                    }

                    // 1) Receipt lookup precedes scope, lease and state. A committed receipt is
                    // returned even after the scope is revoked or the lease moved on.
                    if (request.Receipt != null)
                    {
                        string key = ReceiptKey(request.Receipt.AccountId, request.Receipt.ScopeId, request.Receipt.RequestId);
                        if (_receipts.TryGetValue(key, out ReceiptRow? existing))
                        {
                            if (existing.Receipt.Matches(request.Receipt.Api, request.Receipt.PayloadSha256))
                            {
                                return new ValueTask<TableDurableCommitResult>(
                                    TableDurableCommitResult.Replayed(existing.Receipt));
                            }

                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.Conflict("The same request id was used with a different API/payload."));
                        }
                    }

                    ThrowIf(TableStoreFaultPoint.AfterReceiptLookup);

                    ulong now = (ulong)_clock.UtcNowMs;

                    // 2) Scope recheck inside the transaction.
                    if (!request.InternalCommand)
                    {
                        TableDurableReceipt receipt = request.Receipt!;
                        BeforeScopeCheckForTest?.Invoke();
                        scopeLease = _scopes.BeginScopeForCommit(receipt.ScopeId);
                        TableScopeRecord? scope = scopeLease.Scope;
                        // The wait for the transactional scope read may have consumed time;
                        // expiry must be evaluated at the linearization point, not with the
                        // pre-wait sample.
                        ulong scopeNow = (ulong)_clock.UtcNowMs;
                        if (scope == null)
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.ScopeRejected(
                                    ProtocolErrorCode.Unauthenticated,
                                    "The request scope does not exist."));
                        }

                        if (!string.Equals(scope.AccountId, receipt.AccountId, StringComparison.Ordinal))
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.ScopeRejected(
                                    TableRuntimeReasonCode.ScopeAccountMismatch,
                                    "The request scope belongs to another account."));
                        }

                        if (scope.Role == TableScopeRole.Lobby || scope.Role == TableScopeRole.Spectator)
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.ScopeRejected(
                                    TableRuntimeReasonCode.ScopeRoleMismatch,
                                    "Only a player scope can submit a table command."));
                        }

                        if (!scope.MatchesRoom(request.RoomId))
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.ScopeRejected(
                                    TableRuntimeReasonCode.ScopeRoomMismatch,
                                    "The request scope is bound to another room."));
                        }

                        if (scope.IsRevoked)
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.ScopeRejected(
                                    ProtocolErrorCode.RequestScopeExpired,
                                    "The request scope was revoked."));
                        }

                        if (scope.IsExpiredAt(scopeNow))
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.ScopeRejected(
                                    ProtocolErrorCode.RequestScopeExpired,
                                    "The request scope expired."));
                        }
                    }

                    ThrowIf(TableStoreFaultPoint.AfterScopeCheck);
                    AfterScopeCheckForTest?.Invoke();
                    now = (ulong)_clock.UtcNowMs;

                    // 3) Owner/lease fence. This is the lease's linearization point; a wait
                    // after the scope read must not authorize against the pre-wait clock.
                    if (!_leases.TryGetValue(request.RoomId, out LeaseRow? lease) ||
                        !string.Equals(lease.OwnerInstance, request.OwnerInstance, StringComparison.Ordinal) ||
                        lease.OwnerEpoch != request.ExpectedOwnerEpoch)
                    {
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.StaleOwner("The table lease is not owned by this instance/epoch."));
                    }

                    if (now >= lease.ExpiresAtUtcMs)
                    {
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.StaleOwner(
                                "The table lease expired before the transactional fence."));
                    }

                    ThrowIf(TableStoreFaultPoint.AfterLeaseCheck);

                    // 4) State CAS is independent of the lease fence.
                    if (!_states.TryGetValue(request.RoomId, out StateRow? state) ||
                        state.StateVersion != request.ExpectedStateVersion)
                    {
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.StaleState("The expected state version is not current."));
                    }

                    ThrowIf(TableStoreFaultPoint.AfterStateCheck);

                    bool rejected = request.Receipt != null && request.Receipt.Receipt.Status == ReceiptStatus.Rejected;
                    bool accepted = request.Receipt != null && request.Receipt.IsAccepted;
                    if (request.InternalCommand)
                    {
                        accepted = true;
                    }

                    // N-1b forced-state rejection: the business reply stays REJECTED, but the
                    // P1.4 forced FIRST/clear state, its private events and journal commit in
                    // the same atomic transaction as an accepted command.
                    if (request.RejectedWithState)
                    {
                        accepted = true;
                    }

                    List<TableEventRecord>? newEvents = null;
                    TableCommandJournalRecord? journal = null;
                    StateRow? nextState = null;
                    TableCheckpoint? checkpoint = null;
                    if (accepted)
                    {
                        if (request.Timing == null || request.TimingVersion != state.TimingVersion + 1L)
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.Failed(
                                    TableRuntimeReasonCode.StaleStateVersion,
                                    "An accepted command requires the next timing snapshot/version."));
                        }

                        TableDurableCommitResult? timingFailure =
                            ValidateAcceptedTiming(request, request.Checkpoint);
                        if (timingFailure != null)
                        {
                            return new ValueTask<TableDurableCommitResult>(timingFailure);
                        }

                        newEvents = ValidateAndCopyEvents(request, state.LastEventSeq);
                        journal = request.CommandJournal ??
                            throw new InvalidOperationException("Accepted batch has no command journal.");
                        TablePayloadSha256 nextHash = TablePayloadSha256.Compute(request.NextStatePayload!);
                        nextState = new StateRow(
                            request.CommandVersion,
                            newEvents[newEvents.Count - 1].Seq,
                            request.NextStateFingerprint,
                            nextHash,
                            state.RulesetVersion,
                            state.ControlVersion,
                            now,
                            request.TimingVersion,
                            request.NextCheatVersion,
                            request.NextCheatFingerprint);
                        checkpoint = request.Checkpoint;
                        if (checkpoint != null)
                        {
                            if (checkpoint.StateVersion != request.CommandVersion ||
                                checkpoint.LastEventSeq != nextState.LastEventSeq ||
                                checkpoint.StateFingerprint != request.NextStateFingerprint ||
                                checkpoint.StatePayloadSha256 != nextHash ||
                                checkpoint.ControlVersion != state.ControlVersion ||
                                checkpoint.TimingVersion != request.TimingVersion ||
                                checkpoint.RulesetVersion != state.RulesetVersion)
                            {
                                return new ValueTask<TableDurableCommitResult>(
                                    TableDurableCommitResult.Failed(
                                        TableRuntimeReasonCode.CheckpointInvalid,
                                        "The checkpoint does not match the accepted state/control/timing/ruleset."));
                            }

                            if (!TableCheckpointValidator.TryValidate(
                                    checkpoint,
                                    out string validationReason,
                                    out string validationDetail))
                            {
                                return new ValueTask<TableDurableCommitResult>(
                                    TableDurableCommitResult.Failed(validationReason, validationDetail));
                            }

                            TableDurableCommitResult? cheatCarryover =
                                ValidateCheatCarryover(request.RoomId, checkpoint);
                            if (cheatCarryover != null)
                            {
                                return new ValueTask<TableDurableCommitResult>(cheatCarryover);
                            }

                            TableDurableCommitResult? controlSnapshot =
                                ValidateControlSnapshotUnchanged(request.RoomId, checkpoint);
                            if (controlSnapshot != null)
                            {
                                return new ValueTask<TableDurableCommitResult>(controlSnapshot);
                            }
                        }
                    }
                    else if (!rejected)
                    {
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.Failed(
                                TableRuntimeReasonCode.InternalError,
                                "A commit batch must be accepted, rejected or internal."));
                    }

                    ThrowIf(TableStoreFaultPoint.AfterEventStage);

                    string? receiptKey = null;
                    if (request.Receipt != null)
                    {
                        receiptKey = ReceiptKey(
                            request.Receipt.AccountId,
                            request.Receipt.ScopeId,
                            request.Receipt.RequestId);
                    }

                    ThrowIf(TableStoreFaultPoint.AfterReceiptStage);

                    var outboxRows = new List<KeyValuePair<string, OutboxRow>>();
                    for (int i = 0; i < request.Outbox.Count; i++)
                    {
                        TableSettlementOutboxRecord value = request.Outbox[i];
                        string key = OutboxKey(value.MatchId, value.ResultVersion);
                        if (_outbox.TryGetValue(key, out OutboxRow? existing) ||
                            ContainsKey(outboxRows, key))
                        {
                            TableSettlementOutboxRecord prior = existing != null
                                ? existing.Record
                                : FindOutbox(outboxRows, key, value);
                            if (prior.PayloadSha256 != value.PayloadSha256)
                            {
                                return new ValueTask<TableDurableCommitResult>(
                                    TableDurableCommitResult.Failed(
                                        TableRuntimeReasonCode.OutboxConflict,
                                        "A different settlement payload already exists for this match/result version."));
                            }

                            continue;
                        }

                        outboxRows.Add(new KeyValuePair<string, OutboxRow>(key, new OutboxRow(value)));
                    }

                    ThrowIf(TableStoreFaultPoint.AfterOutboxStage);
                    if (accepted && request.CommittedFactsPayload != null)
                    {
                        if (!request.CommittedFactsPayloadSha256.HasValue ||
                            TablePayloadSha256.Compute(request.CommittedFactsPayload) !=
                            request.CommittedFactsPayloadSha256.Value)
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.Failed(
                                    TableRuntimeReasonCode.CheckpointInvalid,
                                    "Committed facts payload does not match its digest."));
                        }
                    }

                    ThrowIf(TableStoreFaultPoint.BeforeApply);

                    // Apply only after every validation and stage has succeeded.
                    if (accepted)
                    {
                        _events[request.RoomId].AddRange(newEvents!);
                        _commands[request.RoomId].Add(journal!);
                        _states[request.RoomId] = nextState!;
                        _timings[request.RoomId] = new TableTimingJournalRecord(
                            request.RoomId,
                            request.TimingVersion,
                            nextState!.StateVersion,
                            nextState.ControlVersion,
                            request.Timing!.BootId,
                            request.Timing.CapturedAtUtcMs,
                            request.Timing.CapturedMonotonicMillis,
                            request.Timing);
                        if (checkpoint != null)
                        {
                            _checkpoints[request.RoomId] = new CheckpointRow(checkpoint);
                        }

                        if (request.CommittedFactsPayload != null)
                        {
                            _facts[request.RoomId] = new TableCommittedFactsSnapshot(
                                request.CommittedFactsPayload,
                                request.CommittedFactsPayloadSha256!.Value);
                        }
                    }

                    if (receiptKey != null)
                    {
                        _receipts.Add(receiptKey, new ReceiptRow(request.Receipt!));
                    }

                    for (int i = 0; i < outboxRows.Count; i++)
                    {
                        _outbox[outboxRows[i].Key] = outboxRows[i].Value;
                    }

                    // Lease heartbeat: only updated_at moves; owner/epoch are unchanged.
                    lease.UpdatedAtUtcMs = now;
                    applied = true;
                    CommitCount++;

                    if (ArmedFaultPoint == TableStoreFaultPoint.AfterApplyBeforeReturn)
                    {
                        ArmedFaultPoint = null;
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.OutcomeUnknown(
                                "The commit was applied, but the response was lost."));
                    }

                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Committed(request.Receipt));
                }
                catch (TableStoreInjectedFaultException exception)
                {
                    ArmedFaultPoint = null;
                    if (exception.Point == TableStoreFaultPoint.AfterApplyBeforeReturn)
                    {
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.OutcomeUnknown(exception.Message));
                    }

                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(TableRuntimeReasonCode.CommitFailed, exception.Message));
                }
                catch (Exception exception) when (!(exception is OperationCanceledException))
                {
                    if (applied)
                    {
                        // Never report a plain failure after mutations were applied; the caller
                        // must reconcile via the receipt/recovery path.
                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.OutcomeUnknown(exception.GetType().Name + ": " + exception.Message));
                    }

                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.Failed(TableRuntimeReasonCode.CommitFailed, exception.Message));
                }
                finally
                {
                    scopeLease?.Dispose();
                }
            }
        }

        public ValueTask<TableDurableReceipt?> TryGetAsync(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return new ValueTask<TableDurableReceipt?>(
                    _receipts.TryGetValue(ReceiptKey(accountId, scopeId, requestId), out ReceiptRow? row)
                        ? row.Receipt
                        : null);
            }
        }

        public ValueTask<TableLeaseAcquireResult> AcquireAsync(
            string roomId,
            string ownerInstance,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
            }

            lock (_gate)
            {
                ulong now = (ulong)_clock.UtcNowMs;
                ulong expires = now + (ulong)ttl.TotalMilliseconds;
                if (_leases.TryGetValue(roomId, out LeaseRow? existing))
                {
                    bool same = string.Equals(existing.OwnerInstance, ownerInstance, StringComparison.Ordinal);
                    if (!same && now < existing.ExpiresAtUtcMs)
                    {
                        return new ValueTask<TableLeaseAcquireResult>(
                            TableLeaseAcquireResult.Held(existing.ToPublic(roomId), "Another live owner holds the lease."));
                    }

                    var next = new LeaseRow(
                        ownerInstance,
                        new OwnerEpoch(existing.OwnerEpoch.Value + 1),
                        expires,
                        now);
                    _leases[roomId] = next;
                    return new ValueTask<TableLeaseAcquireResult>(
                        TableLeaseAcquireResult.Success(next.ToPublic(roomId)));
                }

                var created = new LeaseRow(ownerInstance, new OwnerEpoch(1), expires, now);
                _leases[roomId] = created;
                return new ValueTask<TableLeaseAcquireResult>(
                    TableLeaseAcquireResult.Success(created.ToPublic(roomId)));
            }
        }

        public ValueTask<TableLeaseRenewResult> RenewAsync(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedEpoch,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
            }

            lock (_gate)
            {
                ulong now = (ulong)_clock.UtcNowMs;
                if (!_leases.TryGetValue(roomId, out LeaseRow? lease) ||
                    !string.Equals(lease.OwnerInstance, ownerInstance, StringComparison.Ordinal) ||
                    lease.OwnerEpoch != expectedEpoch)
                {
                    return new ValueTask<TableLeaseRenewResult>(
                        TableLeaseRenewResult.Stale("The lease is owned by another instance/epoch."));
                }

                if (now >= lease.ExpiresAtUtcMs)
                {
                    return new ValueTask<TableLeaseRenewResult>(
                        TableLeaseRenewResult.Expired("The lease expired before renewal."));
                }

                lease.ExpiresAtUtcMs = now + (ulong)ttl.TotalMilliseconds;
                lease.UpdatedAtUtcMs = now;
                return new ValueTask<TableLeaseRenewResult>(
                    TableLeaseRenewResult.Success(lease.ToPublic(roomId)));
            }
        }

        public ValueTask<TableLeaseSnapshot?> GetAsync(string roomId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return new ValueTask<TableLeaseSnapshot?>(
                    _leases.TryGetValue(roomId, out LeaseRow? lease) ? lease.ToPublic(roomId) : null);
            }
        }

        public ValueTask<TableSettlementOutboxRecord?> TryGetAsync(
            string matchId,
            int resultVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return new ValueTask<TableSettlementOutboxRecord?>(
                    _outbox.TryGetValue(OutboxKey(matchId, resultVersion), out OutboxRow? row)
                        ? row.Record
                        : null);
            }
        }

        public ValueTask<IReadOnlyList<TableSettlementOutboxRecord>> ListUnpublishedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var copy = new List<TableSettlementOutboxRecord>();
                foreach (OutboxRow row in _outbox.Values)
                {
                    if (!row.Record.PublishedAtUtcMs.HasValue)
                    {
                        copy.Add(row.Record);
                    }
                }

                return new ValueTask<IReadOnlyList<TableSettlementOutboxRecord>>(
                    new ReadOnlyCollection<TableSettlementOutboxRecord>(copy));
            }
        }

        public ValueTask<bool> MarkPublishedAsync(
            string matchId,
            int resultVersion,
            ulong publishedAtUtcMs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                string key = OutboxKey(matchId, resultVersion);
                if (!_outbox.TryGetValue(key, out OutboxRow? row))
                {
                    return new ValueTask<bool>(false);
                }

                if (!row.Record.PublishedAtUtcMs.HasValue)
                {
                    _outbox[key] = new OutboxRow(row.Record.MarkPublished(publishedAtUtcMs));
                }

                return new ValueTask<bool>(true);
            }
        }

        public ValueTask AppendRecoveryAuditAsync(TableRecoveryAuditRecord audit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (audit == null)
            {
                throw new ArgumentNullException(nameof(audit));
            }

            lock (_gate)
            {
                if (!_audits.TryGetValue(audit.RoomId, out List<TableRecoveryAuditRecord>? list))
                {
                    list = new List<TableRecoveryAuditRecord>();
                    _audits.Add(audit.RoomId, list);
                }

                list.Add(audit);
            }

            return default;
        }

        public ValueTask<IReadOnlyList<TableRecoveryAuditRecord>> LoadRecoveryAuditsAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_audits.TryGetValue(roomId, out List<TableRecoveryAuditRecord>? list))
                {
                    return new ValueTask<IReadOnlyList<TableRecoveryAuditRecord>>(Array.Empty<TableRecoveryAuditRecord>());
                }

                return new ValueTask<IReadOnlyList<TableRecoveryAuditRecord>>(
                    new ReadOnlyCollection<TableRecoveryAuditRecord>(new List<TableRecoveryAuditRecord>(list)));
            }
        }

        private void ThrowIf(TableStoreFaultPoint point)
        {
            if (ArmedFaultPoint == point)
            {
                throw new TableStoreInjectedFaultException(point);
            }
        }

        private static List<TableEventRecord> ValidateAndCopyEvents(
            TableDurableCommitRequest request,
            EventSeq expectedLastSeq)
        {
            var copy = new List<TableEventRecord>(request.Events.Count);
            ulong nextSeq = expectedLastSeq.Value + 1;
            for (int i = 0; i < request.Events.Count; i++)
            {
                TableEventRecord record = request.Events[i];
                if (record.Ordinal != i)
                {
                    throw new InvalidOperationException("Event ordinals are not contiguous.");
                }

                if (record.CommandVersion != request.CommandVersion)
                {
                    throw new InvalidOperationException("Event command version does not match the batch.");
                }

                if (record.Seq.Value != nextSeq)
                {
                    throw new InvalidOperationException("Event sequence is not contiguous.");
                }

                copy.Add(record);
                nextSeq++;
            }

            return copy;
        }

        private static bool ContainsKey(List<KeyValuePair<string, OutboxRow>> rows, string key)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Key, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static TableSettlementOutboxRecord FindOutbox(
            List<KeyValuePair<string, OutboxRow>> rows,
            string key,
            TableSettlementOutboxRecord fallback)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Key, key, StringComparison.Ordinal))
                {
                    return rows[i].Value.Record;
                }
            }

            return fallback;
        }

        private static string ReceiptKey(string accountId, RequestScopeId scopeId, RequestId requestId) =>
            accountId + "\u0000" + scopeId.Value + "\u0000" + requestId.Value;

        private static string OutboxKey(string matchId, int resultVersion) =>
            matchId + "\u0000" + resultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private sealed class LeaseRow
        {
            public LeaseRow(string ownerInstance, OwnerEpoch ownerEpoch, ulong expiresAtUtcMs, ulong updatedAtUtcMs)
            {
                OwnerInstance = ownerInstance;
                OwnerEpoch = ownerEpoch;
                ExpiresAtUtcMs = expiresAtUtcMs;
                UpdatedAtUtcMs = updatedAtUtcMs;
            }

            public string OwnerInstance { get; }

            public OwnerEpoch OwnerEpoch { get; }

            public ulong ExpiresAtUtcMs { get; set; }

            public ulong UpdatedAtUtcMs { get; set; }

            public TableLeaseSnapshot ToPublic(string roomId) =>
                new TableLeaseSnapshot(roomId, OwnerInstance, OwnerEpoch, ExpiresAtUtcMs, UpdatedAtUtcMs);
        }

        private sealed class StateRow
        {
            public StateRow(
                StateVersion stateVersion,
                EventSeq lastEventSeq,
                ulong stateFingerprint,
                TablePayloadSha256 stateHash,
                int rulesetVersion,
                long controlVersion,
                ulong updatedAtUtcMs,
                long timingVersion = 0L,
                long cheatVersion = 0L,
                ulong cheatFingerprint = 0UL)
            {
                StateVersion = stateVersion;
                LastEventSeq = lastEventSeq;
                StateFingerprint = stateFingerprint;
                StateHash = stateHash;
                RulesetVersion = rulesetVersion;
                ControlVersion = controlVersion;
                UpdatedAtUtcMs = updatedAtUtcMs;
                TimingVersion = timingVersion;
                CheatVersion = cheatVersion;
                CheatFingerprint = cheatFingerprint;
            }

            public StateVersion StateVersion { get; }

            public EventSeq LastEventSeq { get; }

            public ulong StateFingerprint { get; set; }

            public TablePayloadSha256 StateHash { get; set; }

            public int RulesetVersion { get; }

            public long ControlVersion { get; set; }

            public long TimingVersion { get; set; }

            public long CheatVersion { get; set; }

            public ulong CheatFingerprint { get; set; }

            public ulong UpdatedAtUtcMs { get; set; }

            public TableStateRow ToPublic(string roomId) =>
                new TableStateRow(
                    roomId,
                    StateVersion,
                    LastEventSeq,
                    StateFingerprint,
                    StateHash,
                    RulesetVersion,
                    ControlVersion,
                    UpdatedAtUtcMs,
                    TimingVersion,
                    CheatVersion,
                    CheatFingerprint);
        }

        private sealed class CheckpointRow
        {
            public CheckpointRow(TableCheckpoint checkpoint)
            {
                Checkpoint = checkpoint;
            }

            public TableCheckpoint Checkpoint { get; }
        }

        private sealed class ReceiptRow
        {
            public ReceiptRow(TableDurableReceipt receipt)
            {
                Receipt = receipt;
            }

            public TableDurableReceipt Receipt { get; }
        }

        private sealed class OutboxRow
        {
            public OutboxRow(TableSettlementOutboxRecord record)
            {
                Record = record;
            }

            public TableSettlementOutboxRecord Record { get; }
        }
    }
}

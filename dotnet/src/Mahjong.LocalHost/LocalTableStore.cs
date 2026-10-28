using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// File-backed local table store. It composes the accepted in-memory durable contract
    /// implementation (so every fence/receipt/checkpoint rule is the accepted one) and adds a
    /// single-file Checkpoint+Tail envelope written through an atomic replace. The file lock is
    /// the local lease ownership proof; a second process cannot open the same room.
    /// </summary>
    internal sealed class LocalTableStore : ITableStore, IReceiptStore, ITableLease, ISettlementOutbox, IDisposable
    {
        private const string SaveSuffix = ".save";
        private const string BackupSuffix = ".save.prev";
        private const string TempSuffix = ".save.tmp.";
        private const string LockSuffix = ".lock";

        private readonly object _gate = new object();
        private readonly LocalTableHostOptions _options;
        private readonly ITableClock _clock;
        private readonly ReplayClockProxy _replayClock;
        private readonly string _saveDirectory;
        private readonly string _primaryPath;
        private readonly string _backupPath;
        private readonly string _lockPath;
        private readonly ILocalFileSystem _files;

        private InMemoryTableStore? _inner;
        private InMemoryRequestScopeAuthority? _scopes;
        private LocalSave? _save;
        private (string AccountId, RequestScopeId ScopeId, RequestId RequestId, string Digest)? _pendingReceiptDigest;
        private (string AccountId, RequestScopeId ScopeId, RequestId RequestId, string Digest, string Kind)? _pendingControlResult;
        private Stream? _lockStream;
        private bool _leaseAcquired;
        private string _leaseOwner = string.Empty;
        private OwnerEpoch _leaseEpoch;
        private ulong _leaseExpiresUtcMs;
        private bool _backupNeedsRepair;
        private bool _faulted;
        private bool _disposed;

        private LocalTableStore(
            LocalTableHostOptions options,
            ITableClock clock,
            ILocalFileSystem files,
            string saveDirectory)
        {
            _options = options;
            _clock = clock;
            _replayClock = new ReplayClockProxy(clock);
            _files = files;
            _saveDirectory = saveDirectory;
            _primaryPath = Path.Combine(saveDirectory, options.RoomId + SaveSuffix);
            _backupPath = Path.Combine(saveDirectory, options.RoomId + BackupSuffix);
            _lockPath = Path.Combine(saveDirectory, options.RoomId + LockSuffix);
        }

        public bool Exists => _save != null;

        public bool BackupNeedsRepair
        {
            get
            {
                lock (_gate)
                {
                    return _backupNeedsRepair;
                }
            }
        }

        public OwnerEpoch LeaseEpoch
        {
            get
            {
                lock (_gate)
                {
                    return _leaseEpoch;
                }
            }
        }

        public string SaveDirectory => _saveDirectory;

        /// <summary>The same authority the accepted store validates during a commit.</summary>
        internal InMemoryRequestScopeAuthority Scopes =>
            _scopes ?? throw new InvalidOperationException("The local store is not initialized.");

        internal TableScopeRecord IssueScope(
            string accountId,
            TableScopeRole role,
            string roomId,
            ulong issuedAtUtcMs,
            ulong expiresAtUtcMs,
            string scopeId)
        {
            lock (_gate)
            {
                EnsureInner();
                return _scopes!.Issue(accountId, role, roomId, issuedAtUtcMs, expiresAtUtcMs, scopeId);
            }
        }

        public static async ValueTask<LocalTableStore> OpenAsync(
            LocalTableHostOptions options,
            ITableClock clock,
            ILocalFileSystem? files,
            CancellationToken cancellationToken)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            string directory = LocalTableHostPaths.ResolveSaveDirectory(options.SaveDirectory);
            ILocalFileSystem actualFiles = files ?? new SystemLocalFileSystem();
            var store = new LocalTableStore(options, clock, actualFiles, directory);
            await store.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
            return store;
        }

        private async ValueTask OpenCoreAsync(CancellationToken cancellationToken)
        {
            _files.CreateDirectory(_saveDirectory);

            try
            {
                _lockStream = _files.OpenExclusive(_lockPath);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.LeaseConflict,
                    "The local table is already open by another host: " + exception.Message,
                    exception);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalSave? loaded = LoadEnvelopeWithRecovery();
                if (loaded == null)
                {
                    return;
                }

                if (loaded.RulesetVersion != _options.RulesetVersion)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.CorruptState,
                        "The local save ruleset " + loaded.RulesetVersion.ToString(CultureInfo.InvariantCulture) +
                        " does not match the requested ruleset " + _options.RulesetVersion.ToString(CultureInfo.InvariantCulture) + ".");
                }

                if (!string.Equals(loaded.RoomId, _options.RoomId, StringComparison.Ordinal))
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.CorruptState,
                        "The local save room id does not match the requested room.");
                }

                _save = loaded;
                await HydrateInnerStoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleaseLock();
                throw;
            }
        }

        private LocalSave? LoadEnvelopeWithRecovery()
        {
            LocalSave? primary = TryLoad(_primaryPath, out string? primaryError);
            if (primary != null)
            {
                return primary;
            }

            LocalSave? backup = TryLoad(_backupPath, out string? backupError);
            if (primaryError == null && backupError == null)
            {
                return null;
            }

            if (backup == null)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.CorruptState,
                    "The local save and its previous copy are both unreadable. primary=" + primaryError +
                    " backup=" + backupError);
            }

            // The primary is missing/corrupt but the previous copy is valid: publish it through
            // same-volume renames, never an in-place partial copy.
            byte[] bytes = LocalSaveCodec.EncodeEnvelope(backup);
            string tmp = NewTempPath();
            WriteTemporary(tmp, bytes);
            string corrupt = _primaryPath + ".corrupt." + Guid.NewGuid().ToString("N");
            bool quarantined = false;
            try
            {
                if (_files.FileExists(_primaryPath))
                {
                    _files.Move(_primaryPath, corrupt);
                    quarantined = true;
                }

                _files.Move(tmp, _primaryPath);
                if (quarantined)
                {
                    _files.Delete(corrupt);
                }
            }
            catch (Exception exception)
            {
                TryDelete(tmp);
                if (quarantined && !_files.FileExists(_primaryPath))
                {
                    try
                    {
                        _files.Move(corrupt, _primaryPath);
                    }
                    catch
                    {
                        // The verified backup is still present; a later open can retry.
                    }
                }

                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The previous local save is valid but could not be published as primary: " + exception.Message,
                    exception);
            }

            return backup;
        }

        private LocalSave? TryLoad(string path, out string? error)
        {
            error = null;
            if (!_files.FileExists(path))
            {
                return null;
            }

            try
            {
                byte[] bytes = _files.ReadAllBytes(path);
                return LocalSaveCodec.DecodeEnvelope(bytes);
            }
            catch (InvalidDataException exception) when (
                exception.Message.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // A readable but newer envelope is not corruption. Falling back to an older
                // previous copy would silently roll the authoritative state backwards.
                throw new LocalTableException(
                    LocalTableErrorKind.CorruptState,
                    "The local save format/version is not supported by this host: " + exception.Message,
                    exception);
            }
            catch (IOException exception)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The local save is temporarily unreadable; it is not treated as corruption: " + exception.Message,
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The local save cannot be read due to a transient permission failure: " + exception.Message,
                    exception);
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is ArgumentException)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        private async ValueTask HydrateInnerStoreAsync(CancellationToken cancellationToken)
        {
            LocalSave save = _save ?? throw new InvalidOperationException("No save is loaded.");
            TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
            _scopes = new InMemoryRequestScopeAuthority();
            _inner = new InMemoryTableStore(_replayClock, _scopes);
            await _inner.CreateRoomAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            if (save.CurrentState.CheatBound)
            {
                _inner.SetCheatBindingForTest(save.RoomId, save.CurrentState.CheatVersion, save.CurrentState.CheatFingerprint);
            }

            TableLeaseAcquireResult acquired = await _inner.AcquireAsync(
                save.RoomId,
                _options.OwnerInstanceId,
                TimeSpan.FromDays(36500),
                cancellationToken).ConfigureAwait(false);
            if (!acquired.Acquired || acquired.Lease == null)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.LeaseConflict,
                    "The local lease could not be acquired during hydration: " + acquired.Detail);
            }

            _leaseAcquired = true;
            _leaseOwner = _options.OwnerInstanceId;
            _leaseEpoch = acquired.Lease.OwnerEpoch;
            _leaseExpiresUtcMs = acquired.Lease.ExpiresAtUtcMs;

            var replayedScopes = new List<RequestScopeId>();
            if (_scopes != null)
            {
                foreach (TableDurableReceipt receipt in save.Receipts.Values)
                {
                    if (_scopes.ReadScopeForCommit(receipt.ScopeId) == null)
                    {
                        _scopes.Issue(
                            receipt.AccountId,
                            TableScopeRole.Player,
                            save.RoomId,
                            receipt.CreatedAtUtcMs,
                            receipt.ExpiresAtUtcMs,
                            receipt.ScopeId.Value);
                        replayedScopes.Add(receipt.ScopeId);
                    }
                }
            }

            for (int i = 0; i < save.Operations.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (save.Operations[i].ExpectedStateVersion < checkpoint.StateVersion.Value)
                {
                    continue;
                }
                TableDurableCommitResult result = await ReplayOperationAsync(save.Operations[i], replayedScopes, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsCommitted && !result.IsReplayed)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.CorruptState,
                        "Replay operation " + i.ToString(CultureInfo.InvariantCulture) + " (" +
                        save.Operations[i].Kind + ") was rejected: " + result.ReasonCode + " " + result.Detail);
                }
            }

            TableStateRow? innerState = await _inner.LoadStateAsync(save.RoomId, cancellationToken).ConfigureAwait(false);
            if (innerState == null ||
                innerState.StateVersion != save.CurrentState.StateVersion ||
                innerState.LastEventSeq != save.CurrentState.LastEventSeq ||
                innerState.ControlVersion != save.CurrentState.ControlVersion ||
                innerState.TimingVersion != save.CurrentState.TimingVersion)
            {
                var operationParts = new List<string>(save.Operations.Count);
                for (int i = 0; i < save.Operations.Count; i++)
                {
                    LocalDurableOperation operation = save.Operations[i];
                    operationParts.Add(
                        operation.Kind + "@" +
                        operation.ExpectedStateVersion.ToString(CultureInfo.InvariantCulture) +
                        "/t" + operation.ExpectedTimingVersion.ToString(CultureInfo.InvariantCulture));
                }

                string operations = string.Join(",", operationParts);
                throw new LocalTableException(
                    LocalTableErrorKind.CorruptState,
                    "Replayed store state does not match the persisted state row. " +
                    "inner=" + DescribeState(innerState) +
                    " saved=" + DescribeState(save.CurrentState) +
                    " checkpoint(v=" + checkpoint.StateVersion.Value.ToString(CultureInfo.InvariantCulture) +
                    ",seq=" + checkpoint.LastEventSeq.Value.ToString(CultureInfo.InvariantCulture) +
                    ",ctrl=" + checkpoint.ControlVersion.ToString(CultureInfo.InvariantCulture) +
                    ",timing=" + checkpoint.TimingVersion.ToString(CultureInfo.InvariantCulture) + ")" +
                    " ops=" + save.Operations.Count.ToString(CultureInfo.InvariantCulture) +
                    "[" + operations + "]");
            }
            _replayClock.SetOverrideUtc(null);
            ulong finishNow = (ulong)Math.Max(0L, _clock.UtcNowMs);
            if (_scopes != null)
            {
                foreach (RequestScopeId replayedScopeId in replayedScopes)
                {
                    TableScopeRecord? rec = _scopes.ReadScopeForCommit(replayedScopeId);
                    ulong revokeAt = rec != null && rec.IssuedAtUtcMs > finishNow ? rec.IssuedAtUtcMs : finishNow;
                    _scopes.Revoke(replayedScopeId, revokeAt);
                }
            }
            TableLeaseRenewResult renewed = await _inner.RenewAsync(
                save.RoomId,
                _options.OwnerInstanceId,
                _leaseEpoch,
                _options.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            if (renewed.Renewed && renewed.Lease != null)
            {
                _leaseExpiresUtcMs = renewed.Lease.ExpiresAtUtcMs;
            }
        }

        private static string DescribeState(TableStateRow? state)
        {
            return state == null
                ? "<null>"
                : "(v=" + state.StateVersion.Value.ToString(CultureInfo.InvariantCulture) +
                  ",seq=" + state.LastEventSeq.Value.ToString(CultureInfo.InvariantCulture) +
                  ",ctrl=" + state.ControlVersion.ToString(CultureInfo.InvariantCulture) +
                  ",timing=" + state.TimingVersion.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static string DescribeState(LocalSaveState state)
        {
            return "(v=" + state.StateVersion.Value.ToString(CultureInfo.InvariantCulture) +
                   ",seq=" + state.LastEventSeq.Value.ToString(CultureInfo.InvariantCulture) +
                   ",ctrl=" + state.ControlVersion.ToString(CultureInfo.InvariantCulture) +
                   ",timing=" + state.TimingVersion.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private async ValueTask<TableDurableCommitResult> ReplayOperationAsync(
            LocalDurableOperation operation,
            List<RequestScopeId> replayedScopes,
            CancellationToken cancellationToken)
        {
            InMemoryTableStore inner = _inner!;
            string room = _options.RoomId;
            string owner = _options.OwnerInstanceId;
            OwnerEpoch epoch = _leaseEpoch;
            switch (operation.Kind)
            {
                case LocalOperationKind.Commit:
                {
                    if (!operation.InternalCommand && operation.Receipt != null && _scopes != null)
                    {
                        if (_scopes.ReadScopeForCommit(operation.Receipt.ScopeId) == null)
                        {
                            _scopes.Issue(
                                operation.Receipt.AccountId,
                                TableScopeRole.Player,
                                room,
                                operation.Receipt.CreatedAtUtcMs,
                                operation.Receipt.ExpiresAtUtcMs,
                                operation.Receipt.ScopeId.Value);
                            replayedScopes.Add(operation.Receipt.ScopeId);
                        }
                    }

                    TableCheckpoint? checkpoint = operation.CheckpointBlob == null
                        ? null
                        : LocalSaveCodec.DecodeCheckpoint(operation.CheckpointBlob);
                    TableTimingCheckpoint? timing = operation.TimingPayload == null
                        ? null
                        : TableTimingCheckpointCodec.Decode(operation.TimingPayload);
                    var request = new TableDurableCommitRequest(
                        room,
                        owner,
                        epoch,
                        new StateVersion(operation.ExpectedStateVersion),
                        operation.Receipt,
                        operation.InternalCommand,
                        operation.Events,
                        operation.Journal,
                        operation.Outbox,
                        checkpoint,
                        operation.NextStatePayload,
                        operation.NextStateFingerprint,
                        operation.Terminal,
                        timing,
                        operation.TimingVersion,
                        operation.CommittedFactsPayload,
                        operation.CommittedFactsPayloadSha256,
                        operation.NextCheatVersion,
                        operation.NextCheatFingerprint);

                    if (operation.Receipt != null && operation.Receipt.CreatedAtUtcMs > 0)
                    {
                        _replayClock.SetOverrideUtc((long)operation.Receipt.CreatedAtUtcMs);
                    }

                    try
                    {
                        return await inner.CommitAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _replayClock.SetOverrideUtc(null);
                    }
                }

                case LocalOperationKind.SaveCheckpoint:
                {
                    TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(operation.CheckpointBlob!);
                    var fence = new TableCheckpointFence(
                        room,
                        owner,
                        epoch,
                        new StateVersion(operation.ExpectedStateVersion),
                        operation.ExpectedControlVersion,
                        operation.ExpectedTimingVersion);
                    return await inner.SaveCheckpointAsync(checkpoint, fence, cancellationToken).ConfigureAwait(false);
                }

                case LocalOperationKind.CommitControl:
                {
                    TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(operation.CheckpointBlob!);
                    var request = new TableControlCommitRequest(
                        room,
                        epoch,
                        operation.ExpectedControlVersion,
                        operation.ExpectedControlVersion + 1,
                        new StateVersion(operation.ExpectedStateVersion),
                        operation.RoundId ?? string.Empty,
                        operation.ControlChange!,
                        owner);
                    return await inner.CommitControlAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);
                }

                case LocalOperationKind.AppendTiming:
                {
                    TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(operation.TimingPayload!);
                    var request = new TableTimingAppendRequest(
                        room,
                        owner,
                        epoch,
                        new StateVersion(operation.ExpectedStateVersion),
                        operation.ExpectedControlVersion,
                        operation.ExpectedTimingVersion,
                        timing);
                    return await inner.AppendTimingAsync(request, cancellationToken).ConfigureAwait(false);
                }

                case LocalOperationKind.AppendAudit:
                    return TableDurableCommitResult.Committed(null);

                default:
                    return TableDurableCommitResult.Failed("LOCAL_BAD_OP", "Unknown local operation.");
            }
        }

        public ValueTask CreateRoomAsync(TableCheckpoint initialCheckpoint, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (_save != null)
                {
                    throw new LocalTableException(LocalTableErrorKind.CorruptState, "The local room already exists.");
                }

                if (initialCheckpoint == null)
                {
                    throw new ArgumentNullException(nameof(initialCheckpoint));
                }

                EnsureInner();
                _inner!.CreateRoomAsync(initialCheckpoint, cancellationToken).AsTask().GetAwaiter().GetResult();
                EnsureLeaseAcquired();

                var save = new LocalSave
                {
                    FileFormatVersion = 1,
                    RoomId = _options.RoomId,
                    Origin = LocalSaveOrigin.Local,
                    RulesetVersion = _options.RulesetVersion
                };
                ApplyCheckpointToSave(save, initialCheckpoint, initialCheckpoint.TimingVersion);
                save.CurrentState.ControlEpoch = _leaseEpoch.Value;
                save.CurrentState.StateVersion = initialCheckpoint.StateVersion;
                save.CurrentState.LastEventSeq = initialCheckpoint.LastEventSeq;
                save.CurrentState.StateFingerprint = initialCheckpoint.StateFingerprint;
                save.CurrentState.StateHash = initialCheckpoint.StatePayloadSha256;
                save.CurrentState.CheatVersion = 0L;
                save.CurrentState.CheatFingerprint = 0UL;
                save.CurrentState.CheatBound = false;
                PersistOrThrow(save, cancellationToken);
                _save = save;
                return default;
            }
        }

        public ValueTask<TableStateRow?> LoadStateAsync(string roomId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                if (_save == null)
                {
                    return new ValueTask<TableStateRow?>((TableStateRow?)null);
                }

                ulong now = NowUtc();
                return new ValueTask<TableStateRow?>(
                    _save.CurrentState.ToStateRow(_save.RoomId, _save.RulesetVersion, now));
            }
        }

        public ValueTask<TableCheckpoint?> LoadCheckpointAsync(string roomId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                if (_save == null)
                {
                    return new ValueTask<TableCheckpoint?>((TableCheckpoint?)null);
                }

                return new ValueTask<TableCheckpoint?>(LocalSaveCodec.DecodeCheckpoint(_save.BaseCheckpointBlob));
            }
        }

        public ValueTask<TableTimingJournalRecord?> LoadTimingAsync(string roomId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                if (_save == null)
                {
                    return new ValueTask<TableTimingJournalRecord?>((TableTimingJournalRecord?)null);
                }

                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(_save.LatestTimingPayload);
                var record = new TableTimingJournalRecord(
                    _save.RoomId,
                    _save.CurrentState.TimingVersion,
                    _save.CurrentState.StateVersion,
                    _save.CurrentState.ControlVersion,
                    timing.BootId,
                    timing.CapturedAtUtcMs,
                    timing.CapturedMonotonicMillis,
                    timing);
                return new ValueTask<TableTimingJournalRecord?>(record);
            }
        }

        public ValueTask<TableCommittedFactsSnapshot?> LoadCommittedFactsAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                if (_save?.LatestCommittedFactsPayload == null)
                {
                    return new ValueTask<TableCommittedFactsSnapshot?>((TableCommittedFactsSnapshot?)null);
                }

                return new ValueTask<TableCommittedFactsSnapshot?>(
                    new TableCommittedFactsSnapshot(
                        _save.LatestCommittedFactsPayload,
                        _save.LatestCommittedFactsPayloadSha256!.Value));
            }
        }

        public ValueTask<TableDurableCommitResult> AppendTimingAsync(
            TableTimingAppendRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                EnsureRoom(request.RoomId);
                TableDurableCommitResult result = _inner!
                    .AppendTimingAsync(request, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (!result.IsCommitted)
                {
                    return new ValueTask<TableDurableCommitResult>(result);
                }

                LocalSave candidate = _save!.DeepCloneForCandidate();
                byte[] timingBytes = TableTimingCheckpointCodec.Encode(request.Timing);
                candidate.LatestTimingPayload = timingBytes;
                candidate.LatestTimingPayloadSha256 = TablePayloadSha256.Compute(timingBytes);
                candidate.CurrentState.TimingVersion = checked(_save.CurrentState.TimingVersion + 1L);
                var operation = new LocalDurableOperation
                {
                    Kind = LocalOperationKind.AppendTiming,
                    OwnerInstance = request.OwnerInstance,
                    ExpectedOwnerEpoch = request.ExpectedOwnerEpoch.Value,
                    ExpectedStateVersion = request.ExpectedStateVersion.Value,
                    ExpectedControlVersion = request.ExpectedControlVersion,
                    ExpectedTimingVersion = request.ExpectedTimingVersion,
                    TimingPayload = timingBytes
                };
                candidate.Operations.Add(operation);
                LocalWriteResult write = Persist(candidate, cancellationToken);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _pendingReceiptDigest = null;
                }

                return CompleteWrite(write, candidate, result);
            }
        }

        public ValueTask<TableDurableCommitResult> SaveCheckpointAsync(
            TableCheckpoint checkpoint,
            TableCheckpointFence fence,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (checkpoint == null)
                {
                    throw new ArgumentNullException(nameof(checkpoint));
                }

                if (fence == null)
                {
                    throw new ArgumentNullException(nameof(fence));
                }

                EnsureRoom(checkpoint.RoomId);
                TableDurableCommitResult result = _inner!
                    .SaveCheckpointAsync(checkpoint, fence, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (!result.IsCommitted)
                {
                    return new ValueTask<TableDurableCommitResult>(result);
                }

                LocalSave candidate = _save!.DeepCloneForCandidate();
                ApplyCheckpointToSave(candidate, checkpoint, checkpoint.TimingVersion);
                candidate.Operations.Clear();
                candidate.CommandJournals.Clear();
                candidate.EventJournals.Clear();
                LocalWriteResult write = Persist(candidate, cancellationToken);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _pendingReceiptDigest = null;
                }

                return CompleteWrite(write, candidate, result);
            }
        }

        public ValueTask<TableDurableCommitResult> CommitControlAsync(
            TableControlCommitRequest request,
            TableCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                if (checkpoint == null)
                {
                    throw new ArgumentNullException(nameof(checkpoint));
                }

                EnsureRoom(request.RoomId);
                TableDurableCommitResult result = _inner!
                    .CommitControlAsync(request, checkpoint, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (!result.IsCommitted)
                {
                    return new ValueTask<TableDurableCommitResult>(result);
                }

                LocalSave candidate = _save!.DeepCloneForCandidate();
                ApplyCheckpointToSave(candidate, checkpoint, checkpoint.TimingVersion);
                candidate.CurrentState.ControlVersion = checkpoint.ControlVersion;
                candidate.Operations.Clear();
                candidate.CommandJournals.Clear();
                candidate.EventJournals.Clear();
                (string AccountId, RequestScopeId ScopeId, RequestId RequestId, string Digest, string Kind)? pendingControl =
                    _pendingControlResult;
                if (pendingControl.HasValue)
                {
                    candidate.ControlResults[ReceiptKey(
                        pendingControl.Value.AccountId,
                        pendingControl.Value.ScopeId,
                        pendingControl.Value.RequestId)] = new LocalControlRecord(
                        candidate.CurrentState.StateVersion.Value,
                        pendingControl.Value.Digest,
                        pendingControl.Value.Kind);
                }

                LocalWriteResult write = Persist(candidate, cancellationToken);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _pendingReceiptDigest = null;
                    _pendingControlResult = null;
                }

                return CompleteWrite(write, candidate, result);
            }
        }

        public ValueTask<IReadOnlyList<TableEventRecord>> LoadEventsAfterAsync(
            string roomId,
            EventSeq afterSeq,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                var copy = new List<TableEventRecord>();
                if (_save != null)
                {
                    TableCheckpoint cp = LocalSaveCodec.DecodeCheckpoint(_save.BaseCheckpointBlob);
                    if (cp.CommittedHistoryPayload != null)
                    {
                        IReadOnlyList<TableEventRecord> history = TableCommittedHistoryCodec.Decode(cp.CommittedHistoryPayload);
                        for (int i = 0; i < history.Count; i++)
                        {
                            if (history[i].Seq.Value > afterSeq.Value)
                            {
                                copy.Add(history[i]);
                            }
                        }
                    }

                    for (int i = 0; i < _save.EventJournals.Count; i++)
                    {
                        if (_save.EventJournals[i].Seq.Value > afterSeq.Value &&
                            (cp.CommittedHistoryPayload == null || _save.EventJournals[i].Seq.Value > cp.LastEventSeq.Value))
                        {
                            copy.Add(_save.EventJournals[i]);
                        }
                    }
                }

                return new ValueTask<IReadOnlyList<TableEventRecord>>(copy);
            }
        }

        public ValueTask<IReadOnlyList<TableCommandJournalRecord>> LoadCommandsAfterAsync(
            string roomId,
            StateVersion afterVersion,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                var copy = new List<TableCommandJournalRecord>();
                if (_save != null)
                {
                    for (int i = 0; i < _save.CommandJournals.Count; i++)
                    {
                        if (_save.CommandJournals[i].CommandVersion.CompareTo(afterVersion) > 0)
                        {
                            copy.Add(_save.CommandJournals[i]);
                        }
                    }
                }

                return new ValueTask<IReadOnlyList<TableCommandJournalRecord>>(copy);
            }
        }

        public ValueTask<TableDurableCommitResult> CommitAsync(
            TableDurableCommitRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                EnsureRoom(request.RoomId);

                if (request.Receipt != null && _save != null)
                {
                    string receiptKey = ReceiptKey(request.Receipt.AccountId, request.Receipt.ScopeId, request.Receipt.RequestId);
                    if (_save.Receipts.TryGetValue(receiptKey, out TableDurableReceipt? existingReceipt))
                    {
                        if (existingReceipt.Matches(request.Receipt.Api, request.Receipt.PayloadSha256))
                        {
                            return new ValueTask<TableDurableCommitResult>(
                                TableDurableCommitResult.Replayed(existingReceipt));
                        }

                        return new ValueTask<TableDurableCommitResult>(
                            TableDurableCommitResult.Conflict("The same request id was used with a different API/payload."));
                    }
                }

                TableDurableCommitResult result = _inner!
                    .CommitAsync(request, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (result.IsReplayed || !result.IsCommitted)
                {
                    return new ValueTask<TableDurableCommitResult>(result);
                }

                LocalSave candidate = _save!.DeepCloneForCandidate();
                ApplyCommitToSave(candidate, request);
                ApplyCommittedFuritenToSave(candidate, request);
                if (request.Checkpoint != null)
                {
                    candidate.Operations.Clear();
                }

                (string AccountId, RequestScopeId ScopeId, RequestId RequestId, string Digest)? pendingDigest =
                    _pendingReceiptDigest;
                if (pendingDigest.HasValue)
                {
                    candidate.ReceiptDigests[ReceiptKey(
                        pendingDigest.Value.AccountId,
                        pendingDigest.Value.ScopeId,
                        pendingDigest.Value.RequestId)] = pendingDigest.Value.Digest;
                }
                var operation = new LocalDurableOperation
                {
                    Kind = LocalOperationKind.Commit,
                    OwnerInstance = request.OwnerInstance,
                    ExpectedOwnerEpoch = request.ExpectedOwnerEpoch.Value,
                    ExpectedStateVersion = request.ExpectedStateVersion.Value,
                    Receipt = request.Receipt,
                    InternalCommand = request.InternalCommand,
                    Events = new List<TableEventRecord>(request.Events),
                    Journal = request.CommandJournal,
                    Outbox = new List<TableSettlementOutboxRecord>(request.Outbox),
                    CheckpointBlob = request.Checkpoint == null ? null : LocalSaveCodec.EncodeCheckpoint(request.Checkpoint),
                    NextStatePayload = request.NextStatePayload,
                    NextStateFingerprint = request.NextStateFingerprint,
                    Terminal = request.Terminal,
                    TimingPayload = request.Timing == null ? null : TableTimingCheckpointCodec.Encode(request.Timing),
                    TimingVersion = request.TimingVersion,
                    CommittedFactsPayload = request.CommittedFactsPayload,
                    CommittedFactsPayloadSha256 = request.CommittedFactsPayloadSha256,
                    NextCheatVersion = request.NextCheatVersion,
                    NextCheatFingerprint = request.NextCheatFingerprint
                };
                if (request.Checkpoint == null)
                {
                    candidate.Operations.Add(operation);
                }

                LocalWriteResult write = Persist(candidate, cancellationToken);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _pendingReceiptDigest = null;
                }

                return CompleteWrite(write, candidate, result);
            }
        }

        public ValueTask AppendRecoveryAuditAsync(TableRecoveryAuditRecord audit, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (audit == null)
                {
                    throw new ArgumentNullException(nameof(audit));
                }

                EnsureRoom(audit.RoomId);
                _inner!.AppendRecoveryAuditAsync(audit, cancellationToken).AsTask().GetAwaiter().GetResult();
                LocalSave candidate = _save!.DeepCloneForCandidate();
                candidate.RecoveryAudits.Add(audit);
                candidate.Operations.Add(new LocalDurableOperation
                {
                    Kind = LocalOperationKind.AppendAudit,
                    OwnerInstance = _options.OwnerInstanceId,
                    ExpectedOwnerEpoch = _leaseEpoch.Value,
                    ExpectedStateVersion = _save.CurrentState.StateVersion.Value,
                    ExpectedControlVersion = _save.CurrentState.ControlVersion,
                    ExpectedTimingVersion = _save.CurrentState.TimingVersion,
                    Audit = audit
                });
                LocalWriteResult write = Persist(candidate, cancellationToken);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _save = candidate;
                }
                else if (write.Verdict == LocalWriteVerdict.OutcomeUnknown)
                {
                    _faulted = true;
                    throw new LocalTableException(LocalTableErrorKind.Unavailable, write.Detail);
                }
                else
                {
                    throw new LocalTableException(LocalTableErrorKind.Unavailable, write.Detail);
                }

                return default;
            }
        }

        public ValueTask<IReadOnlyList<TableRecoveryAuditRecord>> LoadRecoveryAuditsAsync(
            string roomId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                EnsureRoom(roomId);
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<IReadOnlyList<TableRecoveryAuditRecord>>(
                    _save == null ? Array.Empty<TableRecoveryAuditRecord>() : new List<TableRecoveryAuditRecord>(_save.RecoveryAudits));
            }
        }

        public ValueTask<TableDurableReceipt?> TryGetAsync(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = ReceiptKey(accountId, scopeId, requestId);
                if (!_faulted && _save != null)
                {
                    if (_save.Receipts.TryGetValue(key, out TableDurableReceipt? receipt))
                    {
                        return new ValueTask<TableDurableReceipt?>((TableDurableReceipt?)receipt);
                    }

                    return new ValueTask<TableDurableReceipt?>((TableDurableReceipt?)null);
                }

                if (_faulted)
                {
                    // Never answer an unknown commit from the stale in-memory envelope. Read the
                    // physically committed primary (or, only when it is genuinely corrupt, the
                    // previous copy) and report an unknown result when that evidence is missing.
                    LocalSave? disk = null;
                    if (_files.FileExists(_primaryPath))
                    {
                        disk = TryLoad(_primaryPath, out string? primaryError);
                        if (disk == null)
                        {
                            disk = _files.FileExists(_backupPath)
                                ? TryLoad(_backupPath, out _)
                                : null;
                            if (disk == null)
                            {
                                throw new LocalTableException(
                                    LocalTableErrorKind.Unavailable,
                                    "The committed primary is unreadable and the previous copy cannot prove the receipt: " +
                                    primaryError);
                            }
                        }
                    }
                    else if (_files.FileExists(_backupPath))
                    {
                        disk = TryLoad(_backupPath, out _);
                        if (disk == null)
                        {
                            throw new LocalTableException(
                                LocalTableErrorKind.Unavailable,
                                "The committed primary is missing and the previous copy is unreadable.");
                        }
                    }

                    if (disk == null)
                    {
                        return new ValueTask<TableDurableReceipt?>((TableDurableReceipt?)null);
                    }

                    if (disk.Receipts.TryGetValue(key, out TableDurableReceipt? diskReceipt))
                    {
                        return new ValueTask<TableDurableReceipt?>((TableDurableReceipt?)diskReceipt);
                    }

                    throw new LocalTableException(
                        LocalTableErrorKind.Unavailable,
                        "The disk evidence does not contain the queried receipt; the outcome remains unknown.");
                }

                return new ValueTask<TableDurableReceipt?>((TableDurableReceipt?)null);
            }
        }

        internal void PrepareReceiptDigest(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            string digest)
        {
            lock (_gate)
            {
                _pendingReceiptDigest = string.IsNullOrEmpty(digest)
                    ? null
                    : (accountId, scopeId, requestId, digest);
            }
        }

        internal bool[] LoadTemporaryFuriten()
        {
            lock (_gate)
            {
                return _save == null ? new bool[4] : (bool[])_save.TemporaryFuriten.Clone();
            }
        }

        internal bool[] LoadRiichiFuriten()
        {
            lock (_gate)
            {
                return _save == null ? new bool[4] : (bool[])_save.RiichiFuriten.Clone();
            }
        }

        internal LocalControlRecord? GetControlResult(string accountId, RequestScopeId scopeId, RequestId requestId)
        {
            lock (_gate)
            {
                string key = ReceiptKey(accountId, scopeId, requestId);
                return _save != null && _save.ControlResults.TryGetValue(key, out LocalControlRecord? record)
                    ? record
                    : null;
            }
        }

        internal void PrepareControlResult(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            string digest,
            string kind)
        {
            lock (_gate)
            {
                _pendingControlResult = string.IsNullOrEmpty(digest)
                    ? null
                    : (accountId, scopeId, requestId, digest, kind);
            }
        }

        internal void CommitPreparedControlResultOnly()
        {
            lock (_gate)
            {
                if (_save == null || !_pendingControlResult.HasValue)
                {
                    return;
                }

                (string AccountId, RequestScopeId ScopeId, RequestId RequestId, string Digest, string Kind) pending =
                    _pendingControlResult.Value;
                LocalSave candidate = _save.DeepCloneForCandidate();
                candidate.ControlResults[ReceiptKey(pending.AccountId, pending.ScopeId, pending.RequestId)] =
                    new LocalControlRecord(
                        candidate.CurrentState.StateVersion.Value,
                        pending.Digest,
                        pending.Kind);
                LocalWriteResult write = Persist(candidate, CancellationToken.None);
                if (write.Verdict != LocalWriteVerdict.Committed)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.Unavailable,
                        "The unchanged control result could not be committed.");
                }

                _save = candidate;
                _pendingControlResult = null;
            }
        }

        internal void ClearPreparedControlResult()
        {
            lock (_gate)
            {
                _pendingControlResult = null;
            }
        }

        internal void RecordControlResult(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            ulong evaluatedStateVersion,
            string digest,
            string kind)
        {
            lock (_gate)
            {
                if (_save == null)
                {
                    return;
                }

                string key = ReceiptKey(accountId, scopeId, requestId);
                LocalSave candidate = _save.DeepCloneForCandidate();
                candidate.ControlResults[key] = new LocalControlRecord(evaluatedStateVersion, digest, kind);
                LocalWriteResult write = Persist(candidate, CancellationToken.None);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _save = candidate;
                }
            }
        }

        internal string? GetReceiptDigest(string accountId, RequestScopeId scopeId, RequestId requestId)
        {
            lock (_gate)
            {
                string key = ReceiptKey(accountId, scopeId, requestId);
                return _save != null && _save.ReceiptDigests.TryGetValue(key, out string? digest)
                    ? digest
                    : null;
            }
        }

        internal void RecordReceiptDigest(string accountId, RequestScopeId scopeId, RequestId requestId, string digest)
        {
            lock (_gate)
            {
                if (_save == null || string.IsNullOrEmpty(digest))
                {
                    return;
                }

                string key = ReceiptKey(accountId, scopeId, requestId);
                if (_save.ReceiptDigests.TryGetValue(key, out string? existing) &&
                    string.Equals(existing, digest, StringComparison.Ordinal))
                {
                    return;
                }

                LocalSave candidate = _save.DeepCloneForCandidate();
                candidate.ReceiptDigests[key] = digest;
                try
                {
                    LocalWriteResult write = Persist(candidate, CancellationToken.None);
                    if (write.Verdict == LocalWriteVerdict.Committed)
                    {
                        _save = candidate;
                    }
                }
                catch
                {
                    // The receipt itself is already durable; digest metadata is best effort.
                }
            }
        }

        public ValueTask<TableLeaseAcquireResult> AcquireAsync(
            string roomId,
            string ownerInstance,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRoom(roomId);
                if (_leaseAcquired)
                {
                    if (!string.Equals(_leaseOwner, ownerInstance, StringComparison.Ordinal))
                    {
                        return new ValueTask<TableLeaseAcquireResult>(
                            TableLeaseAcquireResult.Held(LeaseSnapshot(), "The local file lease is owned by " + _leaseOwner + "."));
                    }

                    if (NowUtc() >= _leaseExpiresUtcMs)
                    {
                        return new ValueTask<TableLeaseAcquireResult>(
                            TableLeaseAcquireResult.Failed("The local lease expired."));
                    }

                    return new ValueTask<TableLeaseAcquireResult>(TableLeaseAcquireResult.Success(LeaseSnapshot()));
                }

                EnsureInner();
                TableLeaseAcquireResult acquired = _inner!
                    .AcquireAsync(roomId, ownerInstance, ttl, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (acquired.Acquired && acquired.Lease != null)
                {
                    _leaseAcquired = true;
                    _leaseOwner = ownerInstance;
                    _leaseEpoch = acquired.Lease.OwnerEpoch;
                    _leaseExpiresUtcMs = acquired.Lease.ExpiresAtUtcMs;
                }

                return new ValueTask<TableLeaseAcquireResult>(acquired);
            }
        }

        public ValueTask<TableLeaseRenewResult> RenewAsync(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedEpoch,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRoom(roomId);
                if (!_leaseAcquired ||
                    !string.Equals(_leaseOwner, ownerInstance, StringComparison.Ordinal) ||
                    _leaseEpoch != expectedEpoch)
                {
                    return new ValueTask<TableLeaseRenewResult>(
                        TableLeaseRenewResult.Stale("The local lease owner/epoch is stale."));
                }

                TableLeaseRenewResult renewed = _inner!
                    .RenewAsync(roomId, ownerInstance, expectedEpoch, ttl, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (renewed.Renewed && renewed.Lease != null)
                {
                    _leaseExpiresUtcMs = renewed.Lease.ExpiresAtUtcMs;
                }

                return new ValueTask<TableLeaseRenewResult>(renewed);
            }
        }

        public ValueTask<TableLeaseSnapshot?> GetAsync(string roomId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRoom(roomId);
                return new ValueTask<TableLeaseSnapshot?>(
                    _leaseAcquired ? LeaseSnapshot() : (TableLeaseSnapshot?)null);
            }
        }

        public ValueTask<TableSettlementOutboxRecord?> TryGetAsync(
            string matchId,
            int resultVersion,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_save != null)
                {
                    _save.Outbox.TryGetValue(OutboxKey(matchId, resultVersion), out TableSettlementOutboxRecord? record);
                    return new ValueTask<TableSettlementOutboxRecord?>((TableSettlementOutboxRecord?)record);
                }

                return new ValueTask<TableSettlementOutboxRecord?>((TableSettlementOutboxRecord?)null);
            }
        }

        public ValueTask<IReadOnlyList<TableSettlementOutboxRecord>> ListUnpublishedAsync(
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var list = new List<TableSettlementOutboxRecord>();
                if (_save != null)
                {
                    foreach (TableSettlementOutboxRecord record in _save.Outbox.Values)
                    {
                        if (!record.PublishedAtUtcMs.HasValue)
                        {
                            list.Add(record);
                        }
                    }
                }

                return new ValueTask<IReadOnlyList<TableSettlementOutboxRecord>>(list);
            }
        }

        public ValueTask<bool> MarkPublishedAsync(
            string matchId,
            int resultVersion,
            ulong publishedAtUtcMs,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                string key = OutboxKey(matchId, resultVersion);
                if (_save == null || !_save.Outbox.TryGetValue(key, out TableSettlementOutboxRecord? existing))
                {
                    return new ValueTask<bool>(false);
                }

                LocalSave candidate = _save.DeepCloneForCandidate();
                candidate.Outbox[key] = existing.MarkPublished(publishedAtUtcMs);
                LocalWriteResult write = Persist(candidate, cancellationToken);
                if (write.Verdict == LocalWriteVerdict.Committed)
                {
                    _save = candidate;
                    return new ValueTask<bool>(true);
                }

                if (write.Verdict == LocalWriteVerdict.OutcomeUnknown)
                {
                    _faulted = true;
                }

                throw new LocalTableException(LocalTableErrorKind.Unavailable, write.Detail);
            }
        }

        private void ApplyCommitToSave(LocalSave save, TableDurableCommitRequest request)
        {
            // A rejected receipt commit has no events and no next state. Its CommandVersion is
            // still defined as expected+1 by the contract, but it must not advance the persisted
            // state row; doing so made cold hydration compare a rejected receipt against an
            // advanced save row.
            if (request.Events.Count > 0)
            {
                save.CurrentState.StateVersion = request.CommandVersion;
                save.CurrentState.LastEventSeq = request.Events[request.Events.Count - 1].Seq;
                save.CurrentState.StateFingerprint = request.NextStateFingerprint;
                save.CurrentState.StateHash = TablePayloadSha256.Compute(request.NextStatePayload!);
                save.EventJournals.AddRange(request.Events);
            }

            // A rejected receipt commit carries no timing snapshot and timingVersion 0. It must
            // not regress the store's timing version, because AppendTiming operations committed
            // after it are replayed independently during hydration.
            if (request.Timing != null)
            {
                if (request.TimingVersion < 1)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.CorruptState,
                        "A commit with a timing snapshot must carry a positive timing version.");
                }

                save.CurrentState.TimingVersion = request.TimingVersion;
                byte[] timing = TableTimingCheckpointCodec.Encode(request.Timing);
                save.LatestTimingPayload = timing;
                save.LatestTimingPayloadSha256 = TablePayloadSha256.Compute(timing);
            }

            if (request.CommittedFactsPayload != null)
            {
                save.LatestCommittedFactsPayload = request.CommittedFactsPayload;
                save.LatestCommittedFactsPayloadSha256 = request.CommittedFactsPayloadSha256;
            }

            if (request.NextCheatVersion != 0L)
            {
                save.CurrentState.CheatVersion = request.NextCheatVersion;
                save.CurrentState.CheatFingerprint = request.NextCheatFingerprint;
                save.CurrentState.CheatBound = true;
            }

            if (request.CommandJournal != null)
            {
                save.CommandJournals.Add(request.CommandJournal);
            }

            if (request.Receipt != null)
            {
                save.Receipts[ReceiptKey(request.Receipt.AccountId, request.Receipt.ScopeId, request.Receipt.RequestId)] =
                    request.Receipt;
            }

            for (int i = 0; i < request.Outbox.Count; i++)
            {
                TableSettlementOutboxRecord record = request.Outbox[i];
                save.Outbox[OutboxKey(record.MatchId, record.ResultVersion)] = record;
                save.TerminalResultId = record.MatchId;
                save.TerminalResultVersion = record.ResultVersion;
            }

            if (request.Checkpoint != null)
            {
                ApplyCheckpointToSave(save, request.Checkpoint, request.TimingVersion);
                save.Operations.Clear();
                save.CommandJournals.Clear();
                save.EventJournals.Clear();
            }

            if (request.Terminal)
            {
                save.TerminalState = LocalSaveTerminalState.RoundFinished;
            }
        }

        /// <summary>
        /// Mirrors the actor-derived furiten bits into the local save from the same committed
        /// projection-facts payload that carries the post-command private facts. The write is part
        /// of the same candidate save as state/timing/receipt, so a failed commit cannot leave a
        /// furiten side effect behind and a cold open sees exactly the committed bits.
        /// </summary>
        private static void ApplyCommittedFuritenToSave(LocalSave save, TableDurableCommitRequest request)
        {
            if (request.CommittedFactsPayload == null || request.CommandJournal == null)
            {
                return;
            }

            TableProjectionFactsCodec.TableProjectionFactsIdentity identity =
                TableProjectionFactsCodec.ReadIdentity(request.CommittedFactsPayload);
            if (identity.StateVersion != request.CommandJournal.CommandVersion)
            {
                // A synthetic/legacy commit may carry the previous checkpoint's facts payload.
                // They are not this command's facts and must never be applied to it.
                return;
            }

            TableProjectionContextData facts = TableProjectionFactsCodec.Decode(
                request.CommittedFactsPayload,
                identity.RoundId,
                identity.StateVersion);
            for (int i = 0; i < facts.PrivateFacts.Count; i++)
            {
                TableSeatPrivateFacts fact = facts.PrivateFacts[i];
                if (fact.Seat < 0 || fact.Seat > 3)
                {
                    continue;
                }

                save.TemporaryFuriten[fact.Seat] = fact.TemporaryFuriten;
                save.RiichiFuriten[fact.Seat] = fact.RiichiFuriten;
            }
        }

        private static void ApplyCheckpointToSave(LocalSave save, TableCheckpoint checkpoint, long timingVersion)
        {
            byte[] blob = LocalSaveCodec.EncodeCheckpoint(checkpoint);
            save.BaseCheckpointBlob = blob;
            save.CheckpointSha256 = TablePayloadSha256.Compute(blob);
            save.CurrentState.StateVersion = checkpoint.StateVersion;
            save.CurrentState.LastEventSeq = checkpoint.LastEventSeq;
            save.CurrentState.StateFingerprint = checkpoint.StateFingerprint;
            save.CurrentState.StateHash = checkpoint.StatePayloadSha256;
            save.CurrentState.ControlVersion = checkpoint.ControlVersion;
            save.CurrentState.TimingVersion = timingVersion;
            save.CurrentControlSnapshotPayload = checkpoint.ControlPayload;
            save.CurrentControlSnapshotSha256 = checkpoint.ControlPayloadSha256;
            save.LatestTimingPayload = checkpoint.TimingPayload;
            save.LatestTimingPayloadSha256 = checkpoint.TimingPayloadSha256;
            if (checkpoint.CommittedFactsPayload != null)
            {
                save.LatestCommittedFactsPayload = checkpoint.CommittedFactsPayload;
                save.LatestCommittedFactsPayloadSha256 = checkpoint.CommittedFactsPayloadSha256;
            }
            if (checkpoint.CheatPayload != null)
            {
                TableCheatLedgerCheckpoint cpCheat = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload);
                var restoredCheat = cpCheat.RestoreCheatState();
                save.CurrentState.CheatVersion = restoredCheat.Version;
                save.CurrentState.CheatFingerprint = restoredCheat.Fingerprint;
                save.CurrentState.CheatBound = true;
            }
        }

        private void EnsureInner()
        {
            if (_inner != null)
            {
                return;
            }

            _scopes = new InMemoryRequestScopeAuthority();
            _inner = new InMemoryTableStore(_replayClock, _scopes);
        }

        private void EnsureLeaseAcquired()
        {
            if (_leaseAcquired)
            {
                return;
            }

            TableLeaseAcquireResult acquired = _inner!
                .AcquireAsync(_options.RoomId, _options.OwnerInstanceId, _options.LeaseDuration, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (!acquired.Acquired || acquired.Lease == null)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.LeaseConflict,
                    "The local lease could not be acquired: " + acquired.Detail);
            }

            _leaseAcquired = true;
            _leaseOwner = _options.OwnerInstanceId;
            _leaseEpoch = acquired.Lease.OwnerEpoch;
            _leaseExpiresUtcMs = acquired.Lease.ExpiresAtUtcMs;
        }

        private LocalWriteResult Persist(LocalSave candidate, CancellationToken cancellationToken)
        {
            byte[] bytes;
            try
            {
                bytes = LocalSaveCodec.EncodeEnvelope(candidate);
            }
            catch (Exception exception)
            {
                return LocalWriteResult.Aborted("Local save serialization failed: " + exception.Message);
            }

            string tmp = NewTempPath();
            try
            {
                WriteTemporary(tmp, bytes);
            }
            catch (Exception exception)
            {
                TryDelete(tmp);
                return LocalWriteResult.Aborted("Local temporary write failed: " + exception.Message);
            }

            // Cancellation is honoured up to the linearization point only. After the atomic
            // move/replace succeeds the facts are committed and installation must continue.
            if (cancellationToken.IsCancellationRequested)
            {
                TryDelete(tmp);
                return LocalWriteResult.Aborted("Local save was cancelled before its commit point.");
            }

            bool primaryExists = _files.FileExists(_primaryPath);
            if (!primaryExists)
            {
                try
                {
                    _files.Move(tmp, _primaryPath);
                }
                catch (Exception exception)
                {
                    bool committed = ProbeCandidateBytes(bytes);
                    if (committed)
                    {
                        return LocalWriteResult.Committed(false, "Initial move reported an error but the committed file is present.");
                    }

                    TryDelete(tmp);
                    if (_files.FileExists(_primaryPath))
                    {
                        _faulted = true;
                        return LocalWriteResult.Unknown("Initial create failure left an unreadable primary: " + exception.Message);
                    }

                    return LocalWriteResult.Aborted("Initial move failed before commit: " + exception.Message);
                }

                bool repair = false;
                try
                {
                    _files.Copy(_primaryPath, _backupPath, true);
                }
                catch (Exception)
                {
                    repair = true;
                }

                return LocalWriteResult.Committed(repair, repair
                    ? "Primary committed; initial backup copy failed and needs repair."
                    : string.Empty);
            }

            try
            {
                _files.Replace(tmp, _primaryPath, _backupPath);
                return LocalWriteResult.Committed(false, string.Empty);
            }
            catch (Exception exception)
            {
                if (ProbeCandidateBytes(bytes))
                {
                    return LocalWriteResult.Committed(false, "Replace reported an error but the committed file is present.");
                }

                TryDelete(tmp);
                if (ProbeAnyReadable())
                {
                    return LocalWriteResult.Aborted("Atomic replace failed before commit: " + exception.Message);
                }

                _faulted = true;
                return LocalWriteResult.Unknown("Atomic replace left the primary unreadable: " + exception.Message);
            }
        }

        private void PersistOrThrow(LocalSave candidate, CancellationToken cancellationToken)
        {
            LocalWriteResult write = Persist(candidate, cancellationToken);
            if (write.Verdict == LocalWriteVerdict.Committed)
            {
                _backupNeedsRepair |= write.BackupNeedsRepair;
                _save = candidate;
                return;
            }

            if (write.Verdict == LocalWriteVerdict.OutcomeUnknown)
            {
                _faulted = true;
                throw new LocalTableException(LocalTableErrorKind.Unavailable, write.Detail);
            }

            throw new LocalTableException(LocalTableErrorKind.Unavailable, write.Detail);
        }

        private ValueTask<TableDurableCommitResult> CompleteWrite(
            LocalWriteResult write,
            LocalSave candidate,
            TableDurableCommitResult innerResult)
        {
            switch (write.Verdict)
            {
                case LocalWriteVerdict.Committed:
                    _backupNeedsRepair |= write.BackupNeedsRepair;
                    _save = candidate;
                    return new ValueTask<TableDurableCommitResult>(innerResult);
                default:
                    // The accepted in-memory store already advanced before the file commit was
                    // attempted. A failed file commit therefore cannot be reported as a clean
                    // ABORTED result: the actor is fenced and the outcome is reconciled by a
                    // cold restart from the last physically committed envelope.
                    _faulted = true;
                    return new ValueTask<TableDurableCommitResult>(
                        TableDurableCommitResult.OutcomeUnknown(write.Detail));
            }
        }

        private bool ProbeCandidateBytes(byte[] bytes)
        {
            try
            {
                if (!_files.FileExists(_primaryPath))
                {
                    return false;
                }

                byte[] primary = _files.ReadAllBytes(_primaryPath);
                if (primary.Length != bytes.Length)
                {
                    return false;
                }

                for (int i = 0; i < bytes.Length; i++)
                {
                    if (primary[i] != bytes[i])
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ProbeAnyReadable()
        {
            try
            {
                return TryLoad(_primaryPath, out _) != null;
            }
            catch (LocalTableException)
            {
                return false;
            }
        }

        private void WriteTemporary(string path, byte[] bytes)
        {
            using (Stream stream = _files.CreateNew(path))
            {
                stream.Write(bytes, 0, bytes.Length);
                _files.FlushToDisk(stream);
            }

            byte[] verify = _files.ReadAllBytes(path);
            if (verify.Length != bytes.Length)
            {
                throw new IOException("Temporary local save length changed after flush.");
            }

            if (TablePayloadSha256.Compute(verify).ToArray().AsSpan().SequenceEqual(
                    TablePayloadSha256.Compute(bytes).ToArray()) == false)
            {
                throw new IOException("Temporary local save digest changed after flush.");
            }
        }

        private string NewTempPath() =>
            Path.Combine(
                _saveDirectory,
                _options.RoomId + TempSuffix + Guid.NewGuid().ToString("N"));

        private void TryDelete(string path)
        {
            try
            {
                _files.Delete(path);
            }
            catch
            {
                // A leftover temp file is never authority and is ignored/overwritten by name.
            }
        }

        private TableLeaseSnapshot LeaseSnapshot()
        {
            return new TableLeaseSnapshot(
                _options.RoomId,
                _leaseOwner,
                _leaseEpoch,
                _leaseExpiresUtcMs,
                NowUtc());
        }

        private ulong NowUtc()
        {
            long value = _clock.UtcNowMs;
            return value < 0 ? 0UL : (ulong)value;
        }

        private void EnsureRoom(string roomId)
        {
            if (!string.Equals(roomId, _options.RoomId, StringComparison.Ordinal))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "Room '" + roomId + "' does not belong to this local host.");
            }
        }

        private void ThrowIfUnavailable()
        {
            if (_disposed)
            {
                throw new LocalTableException(LocalTableErrorKind.ChannelClosed, "The local table store is disposed.");
            }

            if (_faulted)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The local table store is fenced after an unknown commit outcome.");
            }
        }

        private static string ReceiptKey(string accountId, RequestScopeId scopeId, RequestId requestId) =>
            accountId + "\u001f" + scopeId.Value + "\u001f" + requestId.Value;

        private static string OutboxKey(string matchId, int resultVersion) =>
            matchId + "\u001f" + resultVersion.ToString(CultureInfo.InvariantCulture);

        private void ReleaseLock()
        {
            if (_lockStream != null)
            {
                try
                {
                    _lockStream.Dispose();
                }
                catch
                {
                    // The OS handle is gone even if Dispose reports a flush error.
                }

                _lockStream = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ReleaseLock();
            }
        }

        private sealed class ReplayClockProxy : ITableClock
        {
            private readonly ITableClock _underlying;
            private long? _overrideUtcNowMs;

            public ReplayClockProxy(ITableClock underlying)
            {
                _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
            }

            public void SetOverrideUtc(long? utcNowMs)
            {
                _overrideUtcNowMs = utcNowMs;
            }

            public long MonotonicMillis => _underlying.MonotonicMillis;

            public long UtcNowMs => _overrideUtcNowMs ?? _underlying.UtcNowMs;
        }
    }

    /// <summary>Path rules for the local save root (no Unity API and no traversal).</summary>
    public static class LocalTableHostPaths
    {
        public static string ResolveSaveDirectory(string saveDirectory)
        {
            if (string.IsNullOrWhiteSpace(saveDirectory))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "SaveDirectory must be a non-empty local path.");
            }

            string full;
            try
            {
                full = Path.GetFullPath(saveDirectory);
            }
            catch (Exception exception)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "SaveDirectory is not a valid local path: " + exception.Message,
                    exception);
            }

            string[] segments = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "..")
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.InvalidOptions,
                        "SaveDirectory must not contain a parent traversal segment.");
                }
            }

            return full;
        }
    }
}

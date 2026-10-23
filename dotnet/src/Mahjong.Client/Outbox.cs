using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

[assembly: InternalsVisibleTo("Mahjong.Client.Tests")]
[assembly: InternalsVisibleTo("Mahjong.Client.OutboxWorker")]

namespace Mahjong.Client
{
    /// <summary>
    /// Durable side-effect record. The canonical business bytes are copied at
    /// creation time and are never rebuilt from Protobuf or a view model.
    /// </summary>
    public sealed class OutboxEntry
    {
        private readonly byte[] _canonicalPayload;

        public OutboxEntry(ProtocolCommand command, RequestScope scope, SessionId sessionId, RoomId roomId)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (!command.ScopeId.HasValue || command.ScopeId.Value.IsEmpty)
            {
                throw new ArgumentException("Only scoped, side-effecting commands belong in the outbox.", nameof(command));
            }

            if (command.ScopeId.Value != scope.ScopeId)
            {
                throw new ArgumentException("The command scope id must match the server-issued scope.", nameof(command));
            }

            if (command.Payload.Length == 0)
            {
                throw new ArgumentException("Outbox commands need a non-empty canonical payload.", nameof(command));
            }

            Key = new IdempotencyKey(scope.AccountId, scope.ScopeId, command.RequestId);
            Kind = command.Kind;
            ScopeId = command.ScopeId.Value;
            RequestId = command.RequestId;
            PayloadSchemaVersion = command.PayloadSchemaVersion;
            ExpectedStateVersion = command.ExpectedStateVersion;
            RoundId = command.RoundId;
            DecisionId = command.DecisionId;
            ControlEpoch = command.ControlEpoch;
            OwnerEpoch = command.OwnerEpoch;
            Digest = command.Payload.Digest;
            _canonicalPayload = command.Payload.CopyBytes();
            OwnerScope = scope;
            SessionId = sessionId;
            RoomId = roomId;
        }

        public IdempotencyKey Key { get; }

        public CommandKind Kind { get; }

        public RequestScopeId ScopeId { get; }

        public RequestId RequestId { get; }

        public ushort PayloadSchemaVersion { get; }

        public StateVersion ExpectedStateVersion { get; }

        public RoundId? RoundId { get; }

        public DecisionId? DecisionId { get; }

        public ControlEpoch? ControlEpoch { get; }

        public OwnerEpoch? OwnerEpoch { get; }

        public PayloadDigest Digest { get; }

        public string DigestSha256 => Digest.ToHex();

        public int PayloadLength => _canonicalPayload.Length;

        public SessionId SessionId { get; }

        public RoomId RoomId { get; }

        /// <summary>The exact server-issued scope that keyed the original request.</summary>
        public RequestScope OwnerScope { get; }

        public ReadOnlyMemory<byte> CanonicalPayload => _canonicalPayload;

        public byte[] CopyCanonicalPayload()
        {
            byte[] copy = new byte[_canonicalPayload.Length];
            Array.Copy(_canonicalPayload, copy, copy.Length);
            return copy;
        }

        /// <summary>Rebuilds the command from retained bytes, preserving id and digest.</summary>
        public ProtocolCommand ToProtocolCommand()
        {
            BusinessPayload payload = BusinessPayload.Retain(_canonicalPayload);
            if (payload.Digest != Digest)
            {
                throw new InvalidOperationException("Stored canonical payload no longer matches its digest.");
            }

            return new ProtocolCommand(
                Kind,
                payload,
                RequestId,
                ScopeId,
                ExpectedStateVersion,
                RoundId,
                DecisionId,
                ControlEpoch,
                OwnerEpoch,
                PayloadSchemaVersion);
        }

        /// <summary>True when a second submission is the exact same logical request.</summary>
        public bool Matches(ProtocolCommand command)
        {
            if (command == null)
            {
                return false;
            }

            return Kind == command.Kind &&
                   RequestId == command.RequestId &&
                   ScopeId == command.ScopeId &&
                   ExpectedStateVersion == command.ExpectedStateVersion &&
                   RoundId == command.RoundId &&
                   DecisionId == command.DecisionId &&
                   ControlEpoch == command.ControlEpoch &&
                   OwnerEpoch == command.OwnerEpoch &&
                   PayloadSchemaVersion == command.PayloadSchemaVersion &&
                   Digest == command.Payload.Digest;
        }

        internal bool MatchesStored(OutboxEntry other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other == null ||
                !Key.Equals(other.Key) ||
                Kind != other.Kind ||
                ExpectedStateVersion != other.ExpectedStateVersion ||
                PayloadSchemaVersion != other.PayloadSchemaVersion ||
                Digest != other.Digest ||
                _canonicalPayload.Length != other._canonicalPayload.Length)
            {
                return false;
            }

            return new ReadOnlySpan<byte>(_canonicalPayload).SequenceEqual(new ReadOnlySpan<byte>(other._canonicalPayload));
        }
    }

    public enum OutboxDisposition
    {
        Unspecified = 0,
        Applied = 1,
        Recorded = 2,
        Rejected = 3,
        Conflict = 4
    }

    /// <summary>
    /// Definitive local disposition of an outbox entry. Only the store's
    /// resolution removes it from the unresolved set; an UNKNOWN lookup never
    /// does.
    /// </summary>
    public sealed class OutboxResolution
    {
        public OutboxResolution(
            OutboxDisposition disposition,
            ReceiptStatus receiptStatus,
            string reasonCode,
            StateVersion evaluatedStateVersion,
            StateVersion? commandVersion,
            EventSeq? eventSeq,
            ulong? committedAtMs,
            string canonicalPayloadSha256,
            bool fromReceiptLookup)
        {
            if (disposition == OutboxDisposition.Unspecified)
            {
                throw new ArgumentException("Outbox resolution must be explicit.", nameof(disposition));
            }

            Disposition = disposition;
            ReceiptStatus = receiptStatus;
            ReasonCode = reasonCode ?? string.Empty;
            EvaluatedStateVersion = evaluatedStateVersion;
            CommandVersion = commandVersion;
            EventSeq = eventSeq;
            CommittedAtMs = committedAtMs;
            CanonicalPayloadSha256 = canonicalPayloadSha256 ?? string.Empty;
            FromReceiptLookup = fromReceiptLookup;
        }

        public OutboxDisposition Disposition { get; }

        public ReceiptStatus ReceiptStatus { get; }

        public string ReasonCode { get; }

        public StateVersion EvaluatedStateVersion { get; }

        public StateVersion? CommandVersion { get; }

        public EventSeq? EventSeq { get; }

        public ulong? CommittedAtMs { get; }

        public string CanonicalPayloadSha256 { get; }

        public bool FromReceiptLookup { get; }

        public bool Succeeded =>
            Disposition == OutboxDisposition.Applied || Disposition == OutboxDisposition.Recorded;

        public static OutboxResolution FromReceipt(ReceiptStatus status, string? reasonCode, StateVersion evaluated, StateVersion? commandVersion, EventSeq? eventSeq, ulong? committedAtMs, string sha256, bool fromLookup)
        {
            switch (status)
            {
                case ReceiptStatus.Applied:
                case ReceiptStatus.Recorded:
                    if (!commandVersion.HasValue || !eventSeq.HasValue)
                    {
                        throw new ArgumentException("An accepted receipt requires command_version and event_seq.", nameof(commandVersion));
                    }

                    if (commandVersion.Value.Value != evaluated.Value + 1)
                    {
                        throw new ArgumentException("command_version must equal evaluated_state_version + 1.", nameof(commandVersion));
                    }

                    if (eventSeq.Value.Value == 0)
                    {
                        throw new ArgumentException("An accepted receipt requires a positive event_seq.", nameof(eventSeq));
                    }

                    return new OutboxResolution(
                        status == ReceiptStatus.Applied ? OutboxDisposition.Applied : OutboxDisposition.Recorded,
                        status,
                        reasonCode ?? string.Empty,
                        evaluated,
                        commandVersion,
                        eventSeq,
                        committedAtMs,
                        sha256,
                        fromLookup);
                case ReceiptStatus.Rejected:
                    if (string.IsNullOrEmpty(reasonCode))
                    {
                        throw new ArgumentException("A rejected receipt requires a reason code.", nameof(reasonCode));
                    }

                    return new OutboxResolution(
                        OutboxDisposition.Rejected,
                        status,
                        reasonCode!,
                        evaluated,
                        null,
                        null,
                        committedAtMs,
                        sha256,
                        fromLookup);
                default:
                    throw new ArgumentException("UNKNOWN is not a definitive outbox resolution.", nameof(status));
            }
        }

        public static OutboxResolution Conflict(string reasonCode, string sha256)
        {
            return new OutboxResolution(
                OutboxDisposition.Conflict,
                ReceiptStatus.Unknown,
                reasonCode,
                new StateVersion(0),
                null,
                null,
                null,
                sha256,
                false);
        }
    }

    /// <summary>
    /// Persistence port for the client outbox. A production implementation must
    /// fsync/replace atomically before <see cref="SaveAsync"/> completes; that
    /// is what makes send-after-persist safe across a process crash.
    /// </summary>
    public interface IOutboxStore
    {
        Task SaveAsync(OutboxEntry entry, CancellationToken cancellationToken);

        Task<IReadOnlyList<OutboxEntry>> LoadUnresolvedAsync(CancellationToken cancellationToken);

        Task ResolveAsync(IdempotencyKey key, OutboxResolution resolution, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Test/local implementation. It preserves entries for the lifetime of the
    /// store object (so session restart can be simulated) but is not durable
    /// across a process restart. A file or platform-store adapter is still
    /// required before remote use.
    /// </summary>
    public sealed class InMemoryOutboxStore : IOutboxStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<IdempotencyKey, OutboxEntry> _entries = new Dictionary<IdempotencyKey, OutboxEntry>();
        private readonly Dictionary<IdempotencyKey, OutboxResolution> _resolutions = new Dictionary<IdempotencyKey, OutboxResolution>();

        public Task SaveAsync(OutboxEntry entry, CancellationToken cancellationToken)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_resolutions.ContainsKey(entry.Key))
                {
                    throw new InvalidOperationException("Outbox entry " + entry.Key + " is already resolved.");
                }

                if (_entries.TryGetValue(entry.Key, out OutboxEntry? existing))
                {
                    if (!existing.MatchesStored(entry))
                    {
                        throw new InvalidOperationException("Outbox key " + entry.Key + " already has different bytes or metadata.");
                    }

                    return Task.CompletedTask;
                }

                _entries.Add(entry.Key, entry);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> LoadUnresolvedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var result = new List<OutboxEntry>();
                foreach (KeyValuePair<IdempotencyKey, OutboxEntry> pair in _entries)
                {
                    if (!_resolutions.ContainsKey(pair.Key))
                    {
                        result.Add(pair.Value);
                    }
                }

                return Task.FromResult<IReadOnlyList<OutboxEntry>>(result);
            }
        }

        public Task ResolveAsync(IdempotencyKey key, OutboxResolution resolution, CancellationToken cancellationToken)
        {
            if (resolution == null)
            {
                throw new ArgumentNullException(nameof(resolution));
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_entries.ContainsKey(key))
                {
                    throw new InvalidOperationException("Cannot resolve unknown outbox key " + key + ".");
                }

                if (_resolutions.TryGetValue(key, out OutboxResolution? existing))
                {
                    if (existing.Disposition != resolution.Disposition ||
                        existing.ReceiptStatus != resolution.ReceiptStatus ||
                        !string.Equals(existing.ReasonCode, resolution.ReasonCode, StringComparison.Ordinal) ||
                        existing.EvaluatedStateVersion != resolution.EvaluatedStateVersion ||
                        existing.CommandVersion != resolution.CommandVersion ||
                        existing.EventSeq != resolution.EventSeq ||
                        existing.CommittedAtMs != resolution.CommittedAtMs ||
                        !string.Equals(existing.CanonicalPayloadSha256, resolution.CanonicalPayloadSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Outbox key " + key + " already has a different resolution.");
                    }

                    return Task.CompletedTask;
                }

                _resolutions.Add(key, resolution);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Bounds and file name for <see cref="FileOutboxStore"/>. The store copies
    /// these values at construction time; mutating this object later does not
    /// reconfigure an existing store.
    /// </summary>
    public sealed class FileOutboxOptions
    {
        /// <summary>Maximum number of unresolved commands kept in one store.</summary>
        public int MaxEntries { get; set; } = 256;

        /// <summary>
        /// Maximum number of resolved-key tombstones. They are intentionally
        /// retained so a confirmed request id can never be resurrected.
        /// </summary>
        public int MaxResolutions { get; set; } = 4096;

        /// <summary>Per-command canonical payload cap. Never above the wire cap.</summary>
        public int MaxPayloadBytes { get; set; } = CanonicalPayloadDigest.MaxPayloadBytes;

        /// <summary>Whole snapshot cap, including header, metadata and payloads.</summary>
        public int MaxStoreBytes { get; set; } = 32 * 1024 * 1024;

        /// <summary>How long a writer waits for the cross-process lock file.</summary>
        public int LockTimeoutMilliseconds { get; set; } = 10_000;

        /// <summary>A file name relative to the configured directory, not a path.</summary>
        public string FileName { get; set; } = "outbox.dat";

        internal FileOutboxOptions ValidatedCopy()
        {
            if (MaxEntries < 1 || MaxEntries > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxEntries), MaxEntries, "MaxEntries must be in 1..4096.");
            }

            if (MaxResolutions < 1 || MaxResolutions > 65536)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxResolutions), MaxResolutions, "MaxResolutions must be in 1..65536.");
            }

            if (MaxPayloadBytes < 1 || MaxPayloadBytes > CanonicalPayloadDigest.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxPayloadBytes),
                    MaxPayloadBytes,
                    "MaxPayloadBytes must be in 1.." + CanonicalPayloadDigest.MaxPayloadBytes + ".");
            }

            if (MaxStoreBytes < 1024 || MaxStoreBytes > 256 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxStoreBytes),
                    MaxStoreBytes,
                    "MaxStoreBytes must be in 1024..268435456.");
            }

            if (LockTimeoutMilliseconds < 1 || LockTimeoutMilliseconds > 120_000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(LockTimeoutMilliseconds),
                    LockTimeoutMilliseconds,
                    "LockTimeoutMilliseconds must be in 1..120000.");
            }

            if (string.IsNullOrEmpty(FileName) ||
                !string.Equals(Path.GetFileName(FileName), FileName, StringComparison.Ordinal) ||
                string.Equals(FileName, ".", StringComparison.Ordinal) ||
                string.Equals(FileName, "..", StringComparison.Ordinal))
            {
                throw new ArgumentException("FileName must be a plain file name without a directory.", nameof(FileName));
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
            {
                if (FileName.IndexOf(invalid[i]) >= 0)
                {
                    throw new ArgumentException("FileName contains an invalid character.", nameof(FileName));
                }
            }

            if (FileName.IndexOf('*') >= 0 || FileName.IndexOf('?') >= 0 ||
                FileName.IndexOf('/') >= 0 || FileName.IndexOf('\\') >= 0)
            {
                throw new ArgumentException("FileName must not contain wildcard or path separator characters.", nameof(FileName));
            }

            return new FileOutboxOptions
            {
                MaxEntries = MaxEntries,
                MaxResolutions = MaxResolutions,
                MaxPayloadBytes = MaxPayloadBytes,
                MaxStoreBytes = MaxStoreBytes,
                LockTimeoutMilliseconds = LockTimeoutMilliseconds,
                FileName = FileName
            };
        }
    }

    /// <summary>
    /// The committed store exists but cannot be decoded safely. It is never
    /// interpreted as an empty store: silently starting over could send a
    /// command whose previous outcome is still unknown.
    /// </summary>
    public sealed class OutboxStoreCorruptException : IOException
    {
        public OutboxStoreCorruptException(string storePath, string reason, Exception? innerException = null)
            : base("Outbox store '" + storePath + "' is corrupt: " + reason, innerException)
        {
            StorePath = storePath;
            Reason = reason;
        }

        public string StorePath { get; }

        public string Reason { get; }
    }

    /// <summary>An explicit, non-destructive failure when a configured bound is reached.</summary>
    public sealed class OutboxStoreFullException : IOException
    {
        public OutboxStoreFullException(string storePath, string reason)
            : base("Outbox store '" + storePath + "' is full: " + reason)
        {
            StorePath = storePath;
            Reason = reason;
        }

        public string StorePath { get; }

        public string Reason { get; }
    }

    internal enum FileOutboxWritePhase
    {
        BeforeTempWrite = 1,
        AfterTempWrite = 2,
        AfterTempFlush = 3,
        BeforeReplace = 4,
        AfterReplace = 5
    }

    /// <summary>
    /// Internal fault-injection seam used by the test-only worker process to
    /// stop a commit at a deterministic phase. It is not a production hook and
    /// does not alter the IOutboxStore contract.
    /// </summary>
    internal static class FileOutboxFaultInjection
    {
        internal static Action<FileOutboxWritePhase, string>? Observer { get; set; }

        internal static void Notify(FileOutboxWritePhase phase, string path)
        {
            Observer?.Invoke(phase, path);
        }
    }

    /// <summary>
    /// Durable client outbox backed by one versioned snapshot file.
    ///
    /// Format: 8-byte magic "MJOUTBX1", u32 format version, u32 flags,
    /// u32 body length, u32 reserved, SHA-256(body), then the body. All
    /// integers are little-endian. The body contains the unresolved entries
    /// (full identity, scope, version metadata, exact canonical business
    /// bytes and digest) followed by resolved-key tombstones.
    ///
    /// Durability: every operation takes an in-process gate and a cross-process
    /// lock file, re-reads the committed snapshot, writes a new snapshot to a
    /// sibling temp file, flushes that file with Flush(true), then replaces the
    /// committed file with File.Replace. SaveAsync therefore does not complete
    /// until the record is on disk, which is what makes persist-before-send
    /// meaningful.
    ///
    /// Corruption: a missing committed file is an empty store; an existing but
    /// invalid/truncated/unknown-version committed file throws
    /// <see cref="OutboxStoreCorruptException"/>. Orphan *.tmp-* files are
    /// uncommitted writes and are removed; a crash during the first save can
    /// therefore leave no committed state, but it never masquerades as a valid
    /// record.
    ///
    /// Privacy: this store has no ticket parameter and never receives the
    /// transport/session ticket. It does persist the caller's exact canonical
    /// command bytes (required by P5.4a recovery); callers must keep the
    /// directory in app-private storage and must not put a bearer ticket in a
    /// command payload. The file is not encrypted. Directory ACLs, Android/iOS
    /// app sandboxes, OS-level file protection and secure key storage remain
    /// platform responsibilities; see the P5.4a report for the tested boundary.
    /// </summary>
    public sealed class FileOutboxStore : IOutboxStore
    {
        private const int HeaderBytes = 56;
        private const uint FormatVersion = 1;
        private const int MaxStringBytes = 1024;
        private const int MaxReasonBytes = 512;
        private const byte EntryRoundFlag = 1;
        private const byte EntryDecisionFlag = 2;
        private const byte EntryControlFlag = 4;
        private const byte EntryOwnerFlag = 8;
        private const byte ResolutionCommandVersionFlag = 1;
        private const byte ResolutionEventSeqFlag = 2;
        private const byte ResolutionCommittedAtFlag = 4;

        private static readonly byte[] MagicBytes = { (byte)'M', (byte)'J', (byte)'O', (byte)'U', (byte)'T', (byte)'B', (byte)'X', (byte)'1' };
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        private readonly string _directory;
        private readonly string _fileName;
        private readonly string _storePath;
        private readonly string _lockPath;
        private readonly FileOutboxOptions _options;
        private readonly SemaphoreSlim _pathGate;

        public FileOutboxStore(string directoryPath)
            : this(directoryPath, null)
        {
        }

        public FileOutboxStore(string directoryPath, FileOutboxOptions? options)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("An outbox directory is required.", nameof(directoryPath));
            }

            _directory = Path.GetFullPath(directoryPath);
            Directory.CreateDirectory(_directory);
            _options = (options ?? new FileOutboxOptions()).ValidatedCopy();
            _fileName = _options.FileName;
            _storePath = Path.Combine(_directory, _fileName);
            _lockPath = _storePath + ".lock";
            _pathGate = PathGates.GetOrAdd(_storePath, _ => new SemaphoreSlim(1, 1));
        }

        public string DirectoryPath => _directory;

        public string StorePath => _storePath;

        public async Task SaveAsync(OutboxEntry entry, CancellationToken cancellationToken)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (FileStream processLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
                {
                    OutboxSnapshot snapshot = ReadSnapshot(cancellationToken);
                    if (snapshot.Resolutions.ContainsKey(entry.Key))
                    {
                        throw new InvalidOperationException("Outbox entry " + entry.Key + " is already resolved.");
                    }

                    OutboxEntry? existing = snapshot.FindEntry(entry.Key);
                    if (existing != null)
                    {
                        if (!EntriesEqual(existing, entry))
                        {
                            throw new InvalidOperationException("Outbox key " + entry.Key + " already has different bytes or metadata.");
                        }

                        return;
                    }

                    ValidateEntry(entry);
                    if (snapshot.Entries.Count >= _options.MaxEntries)
                    {
                        throw new OutboxStoreFullException(
                            _storePath,
                            "the unresolved-entry bound of " + _options.MaxEntries + " was reached.");
                    }

                    snapshot.Entries.Add(entry);
                    WriteSnapshot(snapshot, cancellationToken);
                }
            }
            finally
            {
                _pathGate.Release();
            }
        }

        public async Task<IReadOnlyList<OutboxEntry>> LoadUnresolvedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (FileStream processLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
                {
                    OutboxSnapshot snapshot = ReadSnapshot(cancellationToken);
                    var result = new List<OutboxEntry>(snapshot.Entries.Count);
                    result.AddRange(snapshot.Entries);
                    result.Sort((left, right) => CompareKeys(left.Key, right.Key));
                    return result;
                }
            }
            finally
            {
                _pathGate.Release();
            }
        }

        public async Task ResolveAsync(IdempotencyKey key, OutboxResolution resolution, CancellationToken cancellationToken)
        {
            if (resolution == null)
            {
                throw new ArgumentNullException(nameof(resolution));
            }

            ValidateResolution(resolution);
            cancellationToken.ThrowIfCancellationRequested();
            await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (FileStream processLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
                {
                    OutboxSnapshot snapshot = ReadSnapshot(cancellationToken);
                    OutboxEntry? entry = snapshot.FindEntry(key);
                    OutboxResolution? existingResolution = snapshot.FindResolution(key);

                    if (existingResolution != null)
                    {
                        if (!ResolutionsEqual(existingResolution, resolution))
                        {
                            throw new InvalidOperationException("Outbox key " + key + " already has a different resolution.");
                        }

                        if (entry != null)
                        {
                            snapshot.Entries.Remove(entry);
                            WriteSnapshot(snapshot, cancellationToken);
                        }

                        return;
                    }

                    if (entry == null)
                    {
                        throw new InvalidOperationException("Cannot resolve unknown outbox key " + key + ".");
                    }

                    if (snapshot.Resolutions.Count >= _options.MaxResolutions)
                    {
                        throw new OutboxStoreFullException(
                            _storePath,
                            "the resolved-key bound of " + _options.MaxResolutions + " was reached.");
                    }

                    snapshot.Entries.Remove(entry);
                    snapshot.Resolutions.Add(key, resolution);
                    WriteSnapshot(snapshot, cancellationToken);
                }
            }
            finally
            {
                _pathGate.Release();
            }
        }

        private void ValidateEntry(OutboxEntry entry)
        {
            if (entry.PayloadLength <= 0 || entry.PayloadLength > _options.MaxPayloadBytes)
            {
                throw new ArgumentException("Outbox payload length is outside the configured bound.", nameof(entry));
            }

            if (entry.PayloadSchemaVersion != CanonicalPayloadDigest.FormatVersion)
            {
                throw new ArgumentException("Only canonical payload schema 1 can be persisted.", nameof(entry));
            }

            if (entry.Digest.IsEmpty || entry.Digest.AsSpan().Length != PayloadDigest.SizeBytes)
            {
                throw new ArgumentException("Outbox entry is missing its canonical digest.", nameof(entry));
            }

            if (entry.OwnerScope == null)
            {
                throw new ArgumentException("Outbox entry is missing its owner scope.", nameof(entry));
            }

            if (ContainsResumeToken(entry))
            {
                throw new NotSupportedException(
                    "The plaintext file outbox refuses to persist a resume-token-bearing command. " +
                    "Use a platform secure store for token-bearing recovery commands; the transport ticket is never stored.");
            }
        }

        private static bool ContainsResumeToken(OutboxEntry entry)
        {
            if (entry.Kind != CommandKind.ResumeSession && entry.Kind != CommandKind.ResumeSpectator)
            {
                return false;
            }

            CanonicalPayloadDocument document = CanonicalPayloadParser.Parse(entry.CanonicalPayload.Span);
            return document.HasField(CommandPayloadFields.ResumeToken);
        }

        private static void ValidateResolution(OutboxResolution resolution)
        {
            if (resolution.Disposition == OutboxDisposition.Unspecified)
            {
                throw new ArgumentException("Outbox resolution must be definitive.", nameof(resolution));
            }

            if (!IsHexSha256(resolution.CanonicalPayloadSha256))
            {
                throw new ArgumentException("Outbox resolution digest must be empty or a 64-char SHA-256 hex string.", nameof(resolution));
            }
        }

        private static bool IsHexSha256(string value)
        {
            if (value.Length == 0)
            {
                return true;
            }

            if (value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') ||
                           (c >= 'a' && c <= 'f') ||
                           (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 16, FileOptions.None);
                }
                catch (IOException ex)
                {
                    if (elapsed.ElapsedMilliseconds >= _options.LockTimeoutMilliseconds)
                    {
                        throw new IOException(
                            "Timed out acquiring the outbox lock at '" + _lockPath + "' after " +
                            _options.LockTimeoutMilliseconds + "ms.",
                            ex);
                    }

                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private OutboxSnapshot ReadSnapshot(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupOrphanTempFiles();

            if (Directory.Exists(_storePath))
            {
                throw new OutboxStoreCorruptException(_storePath, "the committed path is a directory, not a store file.");
            }

            if (!File.Exists(_storePath))
            {
                return new OutboxSnapshot();
            }

            var info = new FileInfo(_storePath);
            if (info.Length <= 0)
            {
                throw new OutboxStoreCorruptException(_storePath, "the committed file is empty.");
            }

            if (info.Length > _options.MaxStoreBytes)
            {
                throw new OutboxStoreCorruptException(
                    _storePath,
                    "the committed file is larger than the configured " + _options.MaxStoreBytes + "-byte bound.");
            }

            byte[] raw = File.ReadAllBytes(_storePath);
            cancellationToken.ThrowIfCancellationRequested();
            return DecodeSnapshot(raw);
        }

        private void CleanupOrphanTempFiles()
        {
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(_directory, _fileName + ".tmp-*", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    File.Delete(candidates[i]);
                }
                catch (IOException)
                {
                    // An uncommitted temp file cannot make the committed store
                    // unreadable. A later open retries the cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above: do not turn temp cleanup into data loss.
                }
            }
        }

        private OutboxSnapshot DecodeSnapshot(byte[] raw)
        {
            try
            {
                return DecodeSnapshotCore(raw);
            }
            catch (OutboxStoreCorruptException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is EndOfStreamException ||
                ex is ArgumentException ||
                ex is FormatException ||
                ex is OverflowException ||
                ex is InvalidOperationException)
            {
                throw new OutboxStoreCorruptException(_storePath, "the snapshot could not be decoded.", ex);
            }
        }

        private OutboxSnapshot DecodeSnapshotCore(byte[] raw)
        {
            if (raw.Length < HeaderBytes)
            {
                throw new OutboxStoreCorruptException(_storePath, "the file is shorter than the 56-byte header.");
            }

            using (var stream = new MemoryStream(raw, writable: false))
            using (var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true))
            {
                byte[] magic = ReadExact(reader, MagicBytes.Length, "magic");
                if (!magic.AsSpan().SequenceEqual(MagicBytes))
                {
                    throw new OutboxStoreCorruptException(_storePath, "the magic is not MJOUTBX1.");
                }

                uint version = reader.ReadUInt32();
                if (version != FormatVersion)
                {
                    throw new OutboxStoreCorruptException(
                        _storePath,
                        "unsupported format version " + version + "; expected " + FormatVersion + ".");
                }

                uint flags = reader.ReadUInt32();
                if (flags != 0)
                {
                    throw new OutboxStoreCorruptException(_storePath, "unknown header flags 0x" + flags.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".");
                }

                uint bodyLength = reader.ReadUInt32();
                if (bodyLength != raw.Length - HeaderBytes)
                {
                    throw new OutboxStoreCorruptException(
                        _storePath,
                        "the body length does not match the file length.");
                }

                uint reserved = reader.ReadUInt32();
                if (reserved != 0)
                {
                    throw new OutboxStoreCorruptException(_storePath, "the reserved header word is not zero.");
                }

                byte[] expectedHash = ReadExact(reader, 32, "body checksum");
                byte[] body = ReadExact(reader, checked((int)bodyLength), "body");
                byte[] actualHash = ComputeSha256(body);
                if (!FixedTimeEquals(expectedHash, actualHash))
                {
                    throw new OutboxStoreCorruptException(_storePath, "the body SHA-256 does not match the header.");
                }

                return DecodeBody(body);
            }
        }

        private OutboxSnapshot DecodeBody(byte[] body)
        {
            using (var stream = new MemoryStream(body, writable: false))
            using (var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true))
            {
                uint entryCount = reader.ReadUInt32();
                uint resolutionCount = reader.ReadUInt32();
                if (entryCount > (uint)_options.MaxEntries)
                {
                    throw new OutboxStoreCorruptException(
                        _storePath,
                        "the entry count " + entryCount + " exceeds the configured bound of " + _options.MaxEntries + ".");
                }

                if (resolutionCount > (uint)_options.MaxResolutions)
                {
                    throw new OutboxStoreCorruptException(
                        _storePath,
                        "the resolution count " + resolutionCount + " exceeds the configured bound of " + _options.MaxResolutions + ".");
                }

                var snapshot = new OutboxSnapshot();
                var keys = new HashSet<IdempotencyKey>();
                for (uint i = 0; i < entryCount; i++)
                {
                    OutboxEntry entry = ReadEntry(reader);
                    if (!keys.Add(entry.Key))
                    {
                        throw new OutboxStoreCorruptException(_storePath, "duplicate unresolved outbox key " + entry.Key + ".");
                    }

                    snapshot.Entries.Add(entry);
                }

                var resolutionKeys = new HashSet<IdempotencyKey>();
                for (uint i = 0; i < resolutionCount; i++)
                {
                    IdempotencyKey key = ReadKey(reader);
                    OutboxResolution resolution = ReadResolution(reader);
                    if (!resolutionKeys.Add(key))
                    {
                        throw new OutboxStoreCorruptException(_storePath, "duplicate resolved outbox key " + key + ".");
                    }

                    if (keys.Contains(key))
                    {
                        throw new OutboxStoreCorruptException(_storePath, "outbox key " + key + " is both unresolved and resolved.");
                    }

                    snapshot.Resolutions.Add(key, resolution);
                }

                if (stream.Position != stream.Length)
                {
                    throw new OutboxStoreCorruptException(_storePath, "the body has trailing bytes.");
                }

                return snapshot;
            }
        }

        private OutboxEntry ReadEntry(BinaryReader reader)
        {
            string keyAccount = ReadString(reader, MaxStringBytes, "entry account");
            string keyScope = ReadString(reader, MaxStringBytes, "entry scope");
            string keyRequest = ReadString(reader, MaxStringBytes, "entry request");
            ushort kindValue = reader.ReadUInt16();
            if (!Enum.IsDefined(typeof(CommandKind), (int)kindValue))
            {
                throw new OutboxStoreCorruptException(_storePath, "unknown command kind " + kindValue + ".");
            }

            ushort payloadSchema = reader.ReadUInt16();
            if (payloadSchema != CanonicalPayloadDigest.FormatVersion)
            {
                throw new OutboxStoreCorruptException(_storePath, "unsupported canonical payload schema " + payloadSchema + ".");
            }

            ulong expectedState = reader.ReadUInt64();
            byte flags = reader.ReadByte();
            if ((flags & ~(EntryRoundFlag | EntryDecisionFlag | EntryControlFlag | EntryOwnerFlag)) != 0)
            {
                throw new OutboxStoreCorruptException(_storePath, "unknown entry metadata flags 0x" + flags.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            RoundId? roundId = (flags & EntryRoundFlag) != 0 ? new RoundId(ReadString(reader, MaxStringBytes, "round id")) : (RoundId?)null;
            DecisionId? decisionId = (flags & EntryDecisionFlag) != 0 ? new DecisionId(ReadString(reader, MaxStringBytes, "decision id")) : (DecisionId?)null;
            ControlEpoch? controlEpoch = (flags & EntryControlFlag) != 0 ? new ControlEpoch(reader.ReadUInt64()) : (ControlEpoch?)null;
            OwnerEpoch? ownerEpoch = (flags & EntryOwnerFlag) != 0 ? new OwnerEpoch(reader.ReadUInt64()) : (OwnerEpoch?)null;

            uint payloadLength = reader.ReadUInt32();
            if (payloadLength == 0 || payloadLength > (uint)_options.MaxPayloadBytes)
            {
                throw new OutboxStoreCorruptException(_storePath, "payload length " + payloadLength + " is outside the configured bound.");
            }

            byte[] payloadBytes = ReadExact(reader, checked((int)payloadLength), "canonical payload");
            byte[] storedDigest = ReadExact(reader, PayloadDigest.SizeBytes, "payload digest");

            string sessionId = ReadString(reader, MaxStringBytes, "session id");
            string roomId = ReadString(reader, MaxStringBytes, "room id");

            string scopeId = ReadString(reader, MaxStringBytes, "scope id");
            string scopeAccount = ReadString(reader, MaxStringBytes, "scope account");
            byte roleValue = reader.ReadByte();
            if (!Enum.IsDefined(typeof(ProtocolRole), (int)roleValue))
            {
                throw new OutboxStoreCorruptException(_storePath, "unknown scope role " + roleValue + ".");
            }

            string scopeRoom = ReadString(reader, MaxStringBytes, "scope room");
            ulong issuedAt = reader.ReadUInt64();
            ulong expiresAt = reader.ReadUInt64();

            BusinessPayload payload;
            try
            {
                payload = BusinessPayload.Retain(payloadBytes);
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, "the canonical payload is invalid.", ex);
            }

            if (!payload.Digest.AsSpan().SequenceEqual(new ReadOnlySpan<byte>(storedDigest)))
            {
                throw new OutboxStoreCorruptException(_storePath, "the stored payload digest does not match the payload bytes.");
            }

            try
            {
                var scope = new RequestScope(
                    new RequestScopeId(scopeId),
                    scopeAccount,
                    (ProtocolRole)roleValue,
                    new RoomId(scopeRoom),
                    issuedAt,
                    expiresAt);
                var command = new ProtocolCommand(
                    (CommandKind)kindValue,
                    payload,
                    new RequestId(keyRequest),
                    new RequestScopeId(keyScope),
                    new StateVersion(expectedState),
                    roundId,
                    decisionId,
                    controlEpoch,
                    ownerEpoch,
                    payloadSchema);
                var entry = new OutboxEntry(command, scope, new SessionId(sessionId), new RoomId(roomId));
                if (ContainsResumeToken(entry))
                {
                    throw new OutboxStoreCorruptException(
                        _storePath,
                        "a resume-token-bearing command is not allowed in the plaintext outbox.");
                }

                if (!string.Equals(entry.Key.AccountId, keyAccount, StringComparison.Ordinal) ||
                    !string.Equals(entry.Key.ScopeId.Value, keyScope, StringComparison.Ordinal) ||
                    !string.Equals(entry.Key.RequestId.Value, keyRequest, StringComparison.Ordinal) ||
                    !string.Equals(entry.OwnerScope.ScopeId.Value, scopeId, StringComparison.Ordinal) ||
                    !string.Equals(entry.OwnerScope.AccountId, keyAccount, StringComparison.Ordinal) ||
                    !string.Equals(entry.OwnerScope.RoomId.Value, roomId, StringComparison.Ordinal))
                {
                    throw new OutboxStoreCorruptException(_storePath, "the entry key and scope metadata disagree.");
                }

                return entry;
            }
            catch (OutboxStoreCorruptException)
            {
                throw;
            }
            catch (CommandValidationException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, "the stored command payload is invalid.", ex);
            }
            catch (CanonicalEncodingException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, "the stored canonical payload is invalid.", ex);
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, "the entry metadata is invalid.", ex);
            }
        }

        private IdempotencyKey ReadKey(BinaryReader reader)
        {
            string account = ReadString(reader, MaxStringBytes, "resolution account");
            string scope = ReadString(reader, MaxStringBytes, "resolution scope");
            string request = ReadString(reader, MaxStringBytes, "resolution request");
            try
            {
                return new IdempotencyKey(account, new RequestScopeId(scope), new RequestId(request));
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution key is invalid.", ex);
            }
        }

        private OutboxResolution ReadResolution(BinaryReader reader)
        {
            byte dispositionValue = reader.ReadByte();
            byte statusValue = reader.ReadByte();
            if (!Enum.IsDefined(typeof(OutboxDisposition), (int)dispositionValue) ||
                !Enum.IsDefined(typeof(ReceiptStatus), (int)statusValue))
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution has an unknown disposition or receipt status.");
            }

            OutboxDisposition disposition = (OutboxDisposition)dispositionValue;
            ReceiptStatus status = (ReceiptStatus)statusValue;
            if (disposition == OutboxDisposition.Unspecified || status == ReceiptStatus.Unspecified)
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution is not definitive.");
            }

            string reason = ReadString(reader, MaxReasonBytes, "resolution reason");
            ulong evaluatedValue = reader.ReadUInt64();
            byte flags = reader.ReadByte();
            if ((flags & ~(ResolutionCommandVersionFlag | ResolutionEventSeqFlag | ResolutionCommittedAtFlag)) != 0)
            {
                throw new OutboxStoreCorruptException(_storePath, "unknown resolution flags 0x" + flags.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            StateVersion? commandVersion = (flags & ResolutionCommandVersionFlag) != 0 ? new StateVersion(reader.ReadUInt64()) : (StateVersion?)null;
            EventSeq? eventSeq = (flags & ResolutionEventSeqFlag) != 0 ? new EventSeq(reader.ReadUInt64()) : (EventSeq?)null;
            ulong? committedAt = (flags & ResolutionCommittedAtFlag) != 0 ? reader.ReadUInt64() : (ulong?)null;
            string sha = ReadString(reader, 64, "resolution digest");
            if (!IsHexSha256(sha))
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution digest is not a 64-char SHA-256 hex string.");
            }

            byte fromLookupValue = reader.ReadByte();
            if (fromLookupValue != 0 && fromLookupValue != 1)
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution lookup flag must be 0 or 1.");
            }

            if (committedAt.HasValue && committedAt.Value == 0)
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution committed time must be positive.");
            }

            OutboxResolution resolution;
            try
            {
                if (disposition == OutboxDisposition.Conflict)
                {
                    if (status != ReceiptStatus.Unknown || evaluatedValue != 0 || commandVersion.HasValue || eventSeq.HasValue || committedAt.HasValue || fromLookupValue != 0)
                    {
                        throw new OutboxStoreCorruptException(_storePath, "a conflict resolution carries invalid fields.");
                    }

                    resolution = OutboxResolution.Conflict(reason, sha);
                }
                else
                {
                    resolution = OutboxResolution.FromReceipt(
                        status,
                        reason,
                        new StateVersion(evaluatedValue),
                        commandVersion,
                        eventSeq,
                        committedAt,
                        sha,
                        fromLookupValue == 1);
                }
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution fields are inconsistent.", ex);
            }

            if (resolution.Disposition != disposition ||
                !string.Equals(resolution.CanonicalPayloadSha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                throw new OutboxStoreCorruptException(_storePath, "the resolution disposition or digest is inconsistent.");
            }

            return resolution;
        }

        private void WriteSnapshot(OutboxSnapshot snapshot, CancellationToken cancellationToken)
        {
            byte[] bytes = EncodeSnapshot(snapshot);
            if (bytes.Length > _options.MaxStoreBytes)
            {
                throw new OutboxStoreFullException(
                    _storePath,
                    "the encoded snapshot is " + bytes.Length + " bytes, above the configured " + _options.MaxStoreBytes + "-byte bound.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string tempPath = _storePath + ".tmp-" + Guid.NewGuid().ToString("N");
            bool replaced = false;
            try
            {
                FileOutboxFaultInjection.Notify(FileOutboxWritePhase.BeforeTempWrite, tempPath);
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    FileOutboxFaultInjection.Notify(FileOutboxWritePhase.AfterTempWrite, tempPath);
                    stream.Flush(true);
                    FileOutboxFaultInjection.Notify(FileOutboxWritePhase.AfterTempFlush, tempPath);
                }

                cancellationToken.ThrowIfCancellationRequested();
                FileOutboxFaultInjection.Notify(FileOutboxWritePhase.BeforeReplace, tempPath);
                if (File.Exists(_storePath))
                {
                    File.Replace(tempPath, _storePath, null);
                }
                else
                {
                    File.Move(tempPath, _storePath);
                }

                replaced = true;
                FileOutboxFaultInjection.Notify(FileOutboxWritePhase.AfterReplace, _storePath);
            }
            finally
            {
                if (!replaced && File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // The uncommitted temp file is ignored on the next open.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Same as above.
                    }
                }
            }
        }

        private byte[] EncodeSnapshot(OutboxSnapshot snapshot)
        {
            if (snapshot.Entries.Count > _options.MaxEntries)
            {
                throw new OutboxStoreFullException(_storePath, "the unresolved-entry bound was reached.");
            }

            if (snapshot.Resolutions.Count > _options.MaxResolutions)
            {
                throw new OutboxStoreFullException(_storePath, "the resolved-key bound was reached.");
            }

            var entries = new List<OutboxEntry>(snapshot.Entries);
            entries.Sort((left, right) => CompareKeys(left.Key, right.Key));
            var resolutionKeys = new List<IdempotencyKey>(snapshot.Resolutions.Keys);
            resolutionKeys.Sort(CompareKeys);

            byte[] body;
            using (var bodyStream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bodyStream, StrictUtf8, leaveOpen: true))
                {
                    writer.Write((uint)entries.Count);
                    writer.Write((uint)resolutionKeys.Count);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        WriteEntry(writer, entries[i]);
                    }

                    for (int i = 0; i < resolutionKeys.Count; i++)
                    {
                        IdempotencyKey key = resolutionKeys[i];
                        WriteKey(writer, key);
                        WriteResolution(writer, snapshot.Resolutions[key]);
                    }
                }

                body = bodyStream.ToArray();
            }

            if (body.Length > _options.MaxStoreBytes - HeaderBytes)
            {
                throw new OutboxStoreFullException(
                    _storePath,
                    "the encoded body is " + body.Length + " bytes, above the configured snapshot bound.");
            }

            byte[] checksum = ComputeSha256(body);
            using (var output = new MemoryStream(HeaderBytes + body.Length))
            {
                using (var writer = new BinaryWriter(output, StrictUtf8, leaveOpen: true))
                {
                    writer.Write(MagicBytes);
                    writer.Write(FormatVersion);
                    writer.Write(0u);
                    writer.Write((uint)body.Length);
                    writer.Write(0u);
                    writer.Write(checksum);
                    writer.Write(body);
                }

                return output.ToArray();
            }
        }

        private static void WriteEntry(BinaryWriter writer, OutboxEntry entry)
        {
            ValidateStoreEntry(entry);
            WriteString(writer, entry.Key.AccountId);
            WriteString(writer, entry.ScopeId.Value);
            WriteString(writer, entry.RequestId.Value);
            writer.Write((ushort)entry.Kind);
            writer.Write(entry.PayloadSchemaVersion);
            writer.Write(entry.ExpectedStateVersion.Value);

            byte flags = 0;
            if (entry.RoundId.HasValue)
            {
                flags |= EntryRoundFlag;
            }

            if (entry.DecisionId.HasValue)
            {
                flags |= EntryDecisionFlag;
            }

            if (entry.ControlEpoch.HasValue)
            {
                flags |= EntryControlFlag;
            }

            if (entry.OwnerEpoch.HasValue)
            {
                flags |= EntryOwnerFlag;
            }

            writer.Write(flags);
            if (entry.RoundId.HasValue)
            {
                WriteString(writer, entry.RoundId.Value.Value);
            }

            if (entry.DecisionId.HasValue)
            {
                WriteString(writer, entry.DecisionId.Value.Value);
            }

            if (entry.ControlEpoch.HasValue)
            {
                writer.Write(entry.ControlEpoch.Value.Value);
            }

            if (entry.OwnerEpoch.HasValue)
            {
                writer.Write(entry.OwnerEpoch.Value.Value);
            }

            byte[] payload = entry.CopyCanonicalPayload();
            writer.Write((uint)payload.Length);
            writer.Write(payload);
            writer.Write(entry.Digest.AsSpan().ToArray());

            WriteString(writer, entry.SessionId.Value);
            WriteString(writer, entry.RoomId.Value);

            // The full server-issued scope, including its lifetime, is needed
            // to rebuild the exact request identity. This is not a ticket.
            WriteString(writer, entry.OwnerScope.ScopeId.Value);
            WriteString(writer, entry.OwnerScope.AccountId);
            writer.Write((byte)entry.OwnerScope.Role);
            WriteString(writer, entry.OwnerScope.RoomId.Value);
            writer.Write(entry.OwnerScope.IssuedAtMs);
            writer.Write(entry.OwnerScope.ExpiresAtMs);
        }

        private static void WriteKey(BinaryWriter writer, IdempotencyKey key)
        {
            WriteString(writer, key.AccountId);
            WriteString(writer, key.ScopeId.Value);
            WriteString(writer, key.RequestId.Value);
        }

        private static void WriteResolution(BinaryWriter writer, OutboxResolution resolution)
        {
            ValidateResolution(resolution);
            writer.Write((byte)resolution.Disposition);
            writer.Write((byte)resolution.ReceiptStatus);
            WriteString(writer, resolution.ReasonCode);
            writer.Write(resolution.EvaluatedStateVersion.Value);

            byte flags = 0;
            if (resolution.CommandVersion.HasValue)
            {
                flags |= ResolutionCommandVersionFlag;
            }

            if (resolution.EventSeq.HasValue)
            {
                flags |= ResolutionEventSeqFlag;
            }

            if (resolution.CommittedAtMs.HasValue)
            {
                flags |= ResolutionCommittedAtFlag;
            }

            writer.Write(flags);
            if (resolution.CommandVersion.HasValue)
            {
                writer.Write(resolution.CommandVersion.Value.Value);
            }

            if (resolution.EventSeq.HasValue)
            {
                writer.Write(resolution.EventSeq.Value.Value);
            }

            if (resolution.CommittedAtMs.HasValue)
            {
                writer.Write(resolution.CommittedAtMs.Value);
            }

            WriteString(writer, resolution.CanonicalPayloadSha256);
            writer.Write(resolution.FromReceiptLookup ? (byte)1 : (byte)0);
        }

        private static void ValidateStoreEntry(OutboxEntry entry)
        {
            if (entry.PayloadLength <= 0 || entry.PayloadLength > CanonicalPayloadDigest.MaxPayloadBytes)
            {
                throw new ArgumentException("Outbox payload is too large to persist.", nameof(entry));
            }

            if (entry.PayloadSchemaVersion != CanonicalPayloadDigest.FormatVersion)
            {
                throw new ArgumentException("Unsupported canonical payload schema.", nameof(entry));
            }

            if (entry.Digest.IsEmpty || entry.Digest.AsSpan().Length != PayloadDigest.SizeBytes)
            {
                throw new ArgumentException("Outbox entry is missing its canonical digest.", nameof(entry));
            }

            if (entry.OwnerScope == null)
            {
                throw new ArgumentException("Outbox entry is missing its owner scope.", nameof(entry));
            }

            if (!string.Equals(entry.Key.AccountId, entry.OwnerScope.AccountId, StringComparison.Ordinal) ||
                !string.Equals(entry.Key.ScopeId.Value, entry.OwnerScope.ScopeId.Value, StringComparison.Ordinal) ||
                !string.Equals(entry.RoomId.Value, entry.OwnerScope.RoomId.Value, StringComparison.Ordinal))
            {
                throw new ArgumentException("Outbox key, room and owner scope disagree.", nameof(entry));
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException("Outbox string is not valid UTF-16.", nameof(value), ex);
            }

            if (bytes.Length > MaxStringBytes)
            {
                throw new ArgumentException("Outbox string exceeds the " + MaxStringBytes + "-byte bound.", nameof(value));
            }

            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        private string ReadString(BinaryReader reader, int maxBytes, string field)
        {
            uint length = reader.ReadUInt32();
            if (length > (uint)maxBytes)
            {
                throw new OutboxStoreCorruptException(_storePath, field + " exceeds the " + maxBytes + "-byte bound.");
            }

            byte[] bytes = ReadExact(reader, checked((int)length), field);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new OutboxStoreCorruptException(_storePath, field + " is not valid UTF-8.", ex);
            }
        }

        private byte[] ReadExact(BinaryReader reader, int length, string field)
        {
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new OutboxStoreCorruptException(
                    _storePath,
                    "the file ends while reading " + field + " (" + length + " bytes expected).");
            }

            return bytes;
        }

        private static bool EntriesEqual(OutboxEntry left, OutboxEntry right)
        {
            if (!left.Key.Equals(right.Key) ||
                left.Kind != right.Kind ||
                left.ScopeId != right.ScopeId ||
                left.RequestId != right.RequestId ||
                left.PayloadSchemaVersion != right.PayloadSchemaVersion ||
                left.ExpectedStateVersion != right.ExpectedStateVersion ||
                left.RoundId != right.RoundId ||
                left.DecisionId != right.DecisionId ||
                left.ControlEpoch != right.ControlEpoch ||
                left.OwnerEpoch != right.OwnerEpoch ||
                left.Digest != right.Digest ||
                left.SessionId != right.SessionId ||
                left.RoomId != right.RoomId ||
                left.PayloadLength != right.PayloadLength)
            {
                return false;
            }

            if (left.OwnerScope.ScopeId != right.OwnerScope.ScopeId ||
                !string.Equals(left.OwnerScope.AccountId, right.OwnerScope.AccountId, StringComparison.Ordinal) ||
                left.OwnerScope.Role != right.OwnerScope.Role ||
                left.OwnerScope.RoomId != right.OwnerScope.RoomId ||
                left.OwnerScope.IssuedAtMs != right.OwnerScope.IssuedAtMs ||
                left.OwnerScope.ExpiresAtMs != right.OwnerScope.ExpiresAtMs)
            {
                return false;
            }

            byte[] leftPayload = left.CopyCanonicalPayload();
            byte[] rightPayload = right.CopyCanonicalPayload();
            return leftPayload.AsSpan().SequenceEqual(rightPayload);
        }

        private static bool ResolutionsEqual(OutboxResolution left, OutboxResolution right)
        {
            return left.Disposition == right.Disposition &&
                   left.ReceiptStatus == right.ReceiptStatus &&
                   string.Equals(left.ReasonCode, right.ReasonCode, StringComparison.Ordinal) &&
                   left.EvaluatedStateVersion == right.EvaluatedStateVersion &&
                   left.CommandVersion == right.CommandVersion &&
                   left.EventSeq == right.EventSeq &&
                   left.CommittedAtMs == right.CommittedAtMs &&
                   string.Equals(left.CanonicalPayloadSha256, right.CanonicalPayloadSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareKeys(IdempotencyKey left, IdempotencyKey right)
        {
            int result = string.CompareOrdinal(left.AccountId, right.AccountId);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ScopeId.Value, right.ScopeId.Value);
            return result != 0 ? result : string.CompareOrdinal(left.RequestId.Value, right.RequestId.Value);
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }

            return diff == 0;
        }

        private sealed class OutboxSnapshot
        {
            public List<OutboxEntry> Entries { get; } = new List<OutboxEntry>();

            public Dictionary<IdempotencyKey, OutboxResolution> Resolutions { get; } =
                new Dictionary<IdempotencyKey, OutboxResolution>();

            public OutboxEntry? FindEntry(IdempotencyKey key)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (Entries[i].Key.Equals(key))
                    {
                        return Entries[i];
                    }
                }

                return null;
            }

            public OutboxResolution? FindResolution(IdempotencyKey key)
            {
                return Resolutions.TryGetValue(key, out OutboxResolution? resolution) ? resolution : null;
            }
        }
    }
}

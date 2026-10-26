using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

[assembly: InternalsVisibleTo("Mahjong.Client.Tests")]
[assembly: InternalsVisibleTo("Mahjong.Outbox.CrashWorker")]

namespace Mahjong.Client
{
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
    /// Internal fault-injection seam used by the test-only crash worker to stop
    /// a commit at a deterministic phase. It is not a production hook and does
    /// not alter the IOutboxStore contract.
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

            FileOutboxFormat.ValidateEntryForWrite(entry, _options);
            cancellationToken.ThrowIfCancellationRequested();
            await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (FileStream processLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
                {
                    FileOutboxSnapshot snapshot = ReadSnapshot(cancellationToken);
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
                    FileOutboxSnapshot snapshot = ReadSnapshot(cancellationToken);
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

            FileOutboxFormat.ValidateResolutionForWrite(resolution);
            cancellationToken.ThrowIfCancellationRequested();
            await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (FileStream processLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
                {
                    FileOutboxSnapshot snapshot = ReadSnapshot(cancellationToken);
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

        private FileOutboxSnapshot ReadSnapshot(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupOrphanTempFiles();

            if (Directory.Exists(_storePath))
            {
                throw new OutboxStoreCorruptException(_storePath, "the committed path is a directory, not a store file.");
            }

            if (!File.Exists(_storePath))
            {
                return new FileOutboxSnapshot();
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
            return FileOutboxFormat.Decode(raw, _options, _storePath);
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

        private void WriteSnapshot(FileOutboxSnapshot snapshot, CancellationToken cancellationToken)
        {
            byte[] bytes = FileOutboxFormat.Encode(snapshot, _options, _storePath);
            if (bytes.Length > _options.MaxStoreBytes)
            {
                throw new OutboxStoreFullException(
                    _storePath,
                    "the encoded snapshot is " + bytes.Length + " bytes, above the configured " + _options.MaxStoreBytes + "-byte bound.");
            }

            // The writer and reader share validators, but this round-trip check
            // is the final guard that a successful commit is readable by a
            // fresh instance. It runs before any temp file is created.
            try
            {
                FileOutboxFormat.Decode(bytes, _options, _storePath);
            }
            catch (OutboxStoreCorruptException ex)
            {
                throw new InvalidOperationException(
                    "The encoded outbox snapshot failed its pre-commit round-trip check; the previous snapshot was not replaced.",
                    ex);
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
    }
}

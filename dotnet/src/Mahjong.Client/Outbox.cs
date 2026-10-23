using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

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
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// SHA-256 over the exact business bytes supplied by the trusted transport/host. It is not
    /// computed from a re-serialized DTO, so a client retrying byte-for-byte keeps the same
    /// receipt key while a different payload is a CONFLICT.
    /// </summary>
    public readonly struct TablePayloadSha256 : IEquatable<TablePayloadSha256>
    {
        public const int SizeBytes = 32;
        private readonly byte[]? _bytes;

        public TablePayloadSha256(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != SizeBytes)
            {
                throw new ArgumentException("SHA-256 digest must be 32 bytes.", nameof(bytes));
            }

            _bytes = new byte[SizeBytes];
            Array.Copy(bytes, _bytes, SizeBytes);
        }

        public bool IsEmpty => _bytes == null;

        public ReadOnlySpan<byte> AsSpan() =>
            _bytes == null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_bytes);

        public byte[] ToArray()
        {
            if (_bytes == null)
            {
                return Array.Empty<byte>();
            }

            var copy = new byte[SizeBytes];
            Array.Copy(_bytes, copy, SizeBytes);
            return copy;
        }

        public string ToHex()
        {
            if (_bytes == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(SizeBytes * 2);
            for (int i = 0; i < _bytes.Length; i++)
            {
                builder.Append(_bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public bool Equals(TablePayloadSha256 other)
        {
            if (_bytes == null || other._bytes == null)
            {
                return _bytes == null && other._bytes == null;
            }

            int diff = 0;
            for (int i = 0; i < SizeBytes; i++)
            {
                diff |= _bytes[i] ^ other._bytes[i];
            }

            return diff == 0;
        }

        public override bool Equals(object? obj) => obj is TablePayloadSha256 other && Equals(other);

        public override int GetHashCode()
        {
            if (_bytes == null)
            {
                return 0;
            }

            int hash = 17;
            for (int i = 0; i < _bytes.Length; i++)
            {
                hash = unchecked((hash * 31) + _bytes[i]);
            }

            return hash;
        }

        public static bool operator ==(TablePayloadSha256 left, TablePayloadSha256 right) => left.Equals(right);

        public static bool operator !=(TablePayloadSha256 left, TablePayloadSha256 right) => !left.Equals(right);

        public static TablePayloadSha256 Compute(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                throw new ArgumentException("Payload bytes must not be empty.", nameof(bytes));
            }

            using (SHA256 sha = SHA256.Create())
            {
                return new TablePayloadSha256(sha.ComputeHash(bytes.ToArray()));
            }
        }

        public static TablePayloadSha256 Compute(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return Compute(new ReadOnlySpan<byte>(bytes));
        }
    }

    public enum TableScopeRole : byte
    {
        Player = 0,
        Spectator = 1,
        Lobby = 2
    }

    /// <summary>
    /// A host-issued request scope. It is the only source of account/role/room authorization;
    /// an envelope's client-supplied values are never trusted in its place.
    /// </summary>
    public sealed class TableScopeRecord
    {
        public TableScopeRecord(
            RequestScopeId scopeId,
            string accountId,
            TableScopeRole role,
            string? roomId,
            ulong issuedAtUtcMs,
            ulong expiresAtUtcMs,
            ulong? revokedAtUtcMs = null)
        {
            if (scopeId.IsEmpty)
            {
                throw new ArgumentException("A scope requires an id.", nameof(scopeId));
            }

            AccountId = ProtocolText.RequireId(accountId, nameof(accountId));
            if (role != TableScopeRole.Player && role != TableScopeRole.Spectator && role != TableScopeRole.Lobby)
            {
                throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown scope role.");
            }

            if (role == TableScopeRole.Lobby)
            {
                if (!string.IsNullOrEmpty(roomId))
                {
                    throw new ArgumentException("A lobby scope must not bind a room.", nameof(roomId));
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    throw new ArgumentException("A table scope must bind a room.", nameof(roomId));
                }
            }

            if (expiresAtUtcMs <= issuedAtUtcMs)
            {
                throw new ArgumentOutOfRangeException(nameof(expiresAtUtcMs), "Scope expiry must follow issue time.");
            }

            if (revokedAtUtcMs.HasValue && revokedAtUtcMs.Value < issuedAtUtcMs)
            {
                throw new ArgumentOutOfRangeException(nameof(revokedAtUtcMs), "Revocation cannot precede issue.");
            }

            ScopeId = scopeId;
            Role = role;
            RoomId = roomId;
            IssuedAtUtcMs = issuedAtUtcMs;
            ExpiresAtUtcMs = expiresAtUtcMs;
            RevokedAtUtcMs = revokedAtUtcMs;
        }

        public RequestScopeId ScopeId { get; }

        public string AccountId { get; }

        public TableScopeRole Role { get; }

        public string? RoomId { get; }

        public ulong IssuedAtUtcMs { get; }

        public ulong ExpiresAtUtcMs { get; }

        public ulong? RevokedAtUtcMs { get; }

        public bool IsRevoked => RevokedAtUtcMs.HasValue;

        public bool IsExpiredAt(ulong utcNowMs) => utcNowMs >= ExpiresAtUtcMs;

        public bool IsUsableAt(ulong utcNowMs) => !IsRevoked && !IsExpiredAt(utcNowMs);

        public bool MatchesRoom(string roomId) =>
            Role == TableScopeRole.Lobby ? string.IsNullOrEmpty(RoomId) : string.Equals(RoomId, roomId, StringComparison.Ordinal);
    }

    public sealed class TableScopeLookup
    {
        private TableScopeLookup(TableScopeRecord? scope, string reasonCode, string detail)
        {
            Scope = scope;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public TableScopeRecord? Scope { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool Found => Scope != null;

        public static TableScopeLookup Missing() =>
            new TableScopeLookup(null, ProtocolErrorCode.Unauthenticated, "The request scope does not exist.");

        public static TableScopeLookup FromScope(TableScopeRecord scope) =>
            new TableScopeLookup(scope, string.Empty, string.Empty);
    }

    /// <summary>
    /// Host-issued scope authority. Lookup is asynchronous for the pre-commit admission check;
    /// the commit boundary re-reads the same scope through
    /// <see cref="ITableTransactionalScopeReader"/> inside the store transaction.
    /// </summary>
    public interface IRequestScopeAuthority
    {
        ValueTask<TableScopeLookup> LookupAsync(RequestScopeId scopeId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Synchronous scope read that MUST be evaluated inside the same transaction/lock as the
    /// batch write. A separate pre-check is only an optimization and never the linearization
    /// point.
    /// </summary>
    public interface ITableTransactionalScopeReader
    {
        /// <summary>
        /// Acquires the scope row lock and returns the immutable scope. The caller must hold the
        /// lease until the commit is applied or rolled back; revoke blocks for that duration.
        /// </summary>
        TableScopeReadLease BeginScopeForCommit(RequestScopeId scopeId);
    }

    public sealed class TableScopeReadLease : IDisposable
    {
        private readonly Action _release;

        public TableScopeReadLease(TableScopeRecord? scope, Action release)
        {
            Scope = scope;
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public TableScopeRecord? Scope { get; }

        public void Dispose() => _release();
    }

    public sealed class TableDurableReceipt
    {
        public TableDurableReceipt(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            string api,
            TablePayloadSha256 payloadSha256,
            Receipt receipt,
            string? detail,
            ulong createdAtUtcMs,
            ulong expiresAtUtcMs)
        {
            AccountId = ProtocolText.RequireId(accountId, nameof(accountId));
            if (scopeId.IsEmpty)
            {
                throw new ArgumentException("A receipt requires a scope id.", nameof(scopeId));
            }

            if (requestId.IsEmpty)
            {
                throw new ArgumentException("A receipt requires a request id.", nameof(requestId));
            }

            Api = RequireApi(api);
            PayloadSha256 = payloadSha256.IsEmpty
                ? throw new ArgumentException("A receipt requires a payload digest.", nameof(payloadSha256))
                : payloadSha256;
            Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
            if (!receipt.IsDefinitive)
            {
                throw new ArgumentException("A receipt must have a definitive status.", nameof(receipt));
            }

            if (expiresAtUtcMs <= createdAtUtcMs)
            {
                throw new ArgumentOutOfRangeException(nameof(expiresAtUtcMs), "Receipt expiry must follow creation.");
            }

            Detail = detail ?? string.Empty;
            CreatedAtUtcMs = createdAtUtcMs;
            ExpiresAtUtcMs = expiresAtUtcMs;
            ScopeId = scopeId;
            RequestId = requestId;
        }

        public string AccountId { get; }

        public RequestScopeId ScopeId { get; }

        public RequestId RequestId { get; }

        public string Api { get; }

        public TablePayloadSha256 PayloadSha256 { get; }

        public Receipt Receipt { get; }

        public string Detail { get; }

        public ulong CreatedAtUtcMs { get; }

        public ulong ExpiresAtUtcMs { get; }

        public bool IsAccepted => Receipt.Status == ReceiptStatus.Applied || Receipt.Status == ReceiptStatus.Recorded;

        public TableCommandReceipt ToCommandReceipt()
        {
            if (Receipt.Status == ReceiptStatus.Rejected)
            {
                return TableCommandReceipt.Rejected(Receipt.EvaluatedStateVersion, Receipt.ReasonCode, RequestId);
            }

            return TableCommandReceipt.Applied(
                Receipt.EvaluatedStateVersion,
                Receipt.CommandVersion!.Value,
                Receipt.EventSeq!.Value,
                RequestId,
                Receipt.ReasonCode);
        }

        public bool Matches(string api, TablePayloadSha256 digest)
        {
            return string.Equals(Api, api, StringComparison.Ordinal) && PayloadSha256 == digest;
        }

        public static string RequireApi(string api)
        {
            if (string.IsNullOrWhiteSpace(api))
            {
                throw new ArgumentException("API name cannot be blank.", nameof(api));
            }

            if (api.Length > 64)
            {
                throw new ArgumentException("API name exceeds 64 characters.", nameof(api));
            }

            for (int i = 0; i < api.Length; i++)
            {
                char value = api[i];
                bool valid = (value >= 'A' && value <= 'Z') ||
                             (value >= '0' && value <= '9') ||
                             value == '_' || value == '.' || value == '/';
                if (!valid)
                {
                    throw new ArgumentException("API name must use stable uppercase characters.", nameof(api));
                }
            }

            return api;
        }
    }

    /// <summary>
    /// The exact caller command stored next to its events. Recovery re-executes this command
    /// through Domain; it is not a saved candidate state pretending to be one event.
    /// </summary>
    public sealed class TableCommandJournalRecord
    {
        private readonly byte[] _commandPayload;

        public TableCommandJournalRecord(
            StateVersion commandVersion,
            StateVersion evaluatedStateVersion,
            RequestId requestId,
            string accountId,
            RequestScopeId scopeId,
            string api,
            TablePayloadSha256 payloadSha256,
            byte[] commandPayload,
            int schemaVersion = 1)
        {
            if (commandVersion.Value != evaluatedStateVersion.Value + 1)
            {
                throw new ArgumentException("commandVersion must equal evaluatedStateVersion + 1.", nameof(commandVersion));
            }

            if (requestId.IsEmpty)
            {
                throw new ArgumentException("A command journal requires a request id.", nameof(requestId));
            }

            if (commandPayload == null || commandPayload.Length == 0)
            {
                throw new ArgumentException("A command journal requires encoded command bytes.", nameof(commandPayload));
            }

            if (schemaVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema version must be positive.");
            }

            CommandVersion = commandVersion;
            EvaluatedStateVersion = evaluatedStateVersion;
            RequestId = requestId;
            AccountId = ProtocolText.RequireId(accountId, nameof(accountId));
            ScopeId = scopeId;
            Api = TableDurableReceipt.RequireApi(api);
            PayloadSha256 = payloadSha256.IsEmpty
                ? throw new ArgumentException("A command journal requires a payload digest.", nameof(payloadSha256))
                : payloadSha256;
            _commandPayload = new byte[commandPayload.Length];
            Array.Copy(commandPayload, _commandPayload, commandPayload.Length);
            SchemaVersion = schemaVersion;
        }

        public StateVersion CommandVersion { get; }

        public StateVersion EvaluatedStateVersion { get; }

        public RequestId RequestId { get; }

        public string AccountId { get; }

        public RequestScopeId ScopeId { get; }

        public string Api { get; }

        public TablePayloadSha256 PayloadSha256 { get; }

        public int SchemaVersion { get; }

        public byte[] CommandPayload
        {
            get
            {
                var copy = new byte[_commandPayload.Length];
                Array.Copy(_commandPayload, copy, _commandPayload.Length);
                return copy;
            }
        }

        public TablePayloadSha256 CommandPayloadSha256 => TablePayloadSha256.Compute(_commandPayload);

        internal ReadOnlySpan<byte> CommandPayloadSpan => new ReadOnlySpan<byte>(_commandPayload);
    }

    public sealed class TableSettlementOutboxRecord
    {
        private readonly byte[] _payload;

        public TableSettlementOutboxRecord(
            string matchId,
            int resultVersion,
            byte[] payload,
            TablePayloadSha256 payloadSha256,
            ulong createdAtUtcMs,
            ulong? publishedAtUtcMs = null)
        {
            MatchId = ProtocolText.RequireId(matchId, nameof(matchId));
            if (resultVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(resultVersion), resultVersion, "Result version must be non-negative.");
            }

            if (payload == null || payload.Length == 0)
            {
                throw new ArgumentException("An outbox record requires a payload.", nameof(payload));
            }

            if (payloadSha256.IsEmpty)
            {
                throw new ArgumentException("An outbox record requires a payload digest.", nameof(payloadSha256));
            }

            _payload = new byte[payload.Length];
            Array.Copy(payload, _payload, payload.Length);
            if (TablePayloadSha256.Compute(_payload) != payloadSha256)
            {
                throw new ArgumentException("Outbox digest does not match its payload.", nameof(payloadSha256));
            }

            ResultVersion = resultVersion;
            PayloadSha256 = payloadSha256;
            CreatedAtUtcMs = createdAtUtcMs;
            PublishedAtUtcMs = publishedAtUtcMs;
        }

        public string MatchId { get; }

        public int ResultVersion { get; }

        public TablePayloadSha256 PayloadSha256 { get; }

        public ulong CreatedAtUtcMs { get; }

        public ulong? PublishedAtUtcMs { get; }

        public byte[] Payload
        {
            get
            {
                var copy = new byte[_payload.Length];
                Array.Copy(_payload, copy, _payload.Length);
                return copy;
            }
        }

        internal ReadOnlySpan<byte> PayloadSpan => new ReadOnlySpan<byte>(_payload);

        public TableSettlementOutboxRecord MarkPublished(ulong utcMs) =>
            new TableSettlementOutboxRecord(MatchId, ResultVersion, _payload, PayloadSha256, CreatedAtUtcMs, utcMs);
    }

    /// <summary>
    /// Self-contained recovery checkpoint. The payloads are encoded values, never live Domain
    /// objects. Hashes are recomputed by the store and again by recovery.
    /// </summary>
    public sealed class TableCheckpoint
    {
        private readonly byte[] _statePayload;
        private readonly byte[] _controlPayload;
        private readonly byte[] _timingPayload;
        private readonly byte[]? _cheatPayload;
        private readonly byte[]? _committedFactsPayload;
        private readonly byte[]? _committedHistoryPayload;
        private readonly byte[]? _committedCommandBindingsPayload;

        public TableCheckpoint(
            string roomId,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            ulong stateFingerprint,
            int rulesetVersion,
            byte[] statePayload,
            TablePayloadSha256 statePayloadSha256,
            long controlVersion,
            byte[] controlPayload,
            TablePayloadSha256 controlPayloadSha256,
            byte[] timingPayload,
            TablePayloadSha256 timingPayloadSha256,
            byte[]? cheatPayload,
            TablePayloadSha256? cheatPayloadSha256,
            byte[]? committedFactsPayload,
            TablePayloadSha256? committedFactsPayloadSha256,
            ulong createdAtUtcMs,
            string bootId,
            int schemaVersion = 1,
            long timingVersion = 0L,
            long capturedMonotonicMillis = 0L,
            byte[]? committedHistoryPayload = null,
            TablePayloadSha256? committedHistoryPayloadSha256 = null,
            byte[]? committedCommandBindingsPayload = null,
            TablePayloadSha256? committedCommandBindingsPayloadSha256 = null)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (rulesetVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rulesetVersion), rulesetVersion, "Ruleset version must be positive.");
            }

            if (schemaVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema version must be positive.");
            }

            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            if (string.IsNullOrWhiteSpace(bootId))
            {
                throw new ArgumentException("BootId cannot be blank.", nameof(bootId));
            }

            if (timingVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timingVersion), timingVersion, "Timing version must be non-negative.");
            }

            if (capturedMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capturedMonotonicMillis), capturedMonotonicMillis, "Captured monotonic time must be non-negative.");
            }

            _statePayload = CopyAndVerify(statePayload, statePayloadSha256, nameof(statePayload));
            _controlPayload = CopyAndVerify(controlPayload, controlPayloadSha256, nameof(controlPayload));
            _timingPayload = CopyAndVerify(timingPayload, timingPayloadSha256, nameof(timingPayload));
            if (cheatPayload != null)
            {
                if (!cheatPayloadSha256.HasValue)
                {
                    throw new ArgumentException("Cheat payload requires a digest.", nameof(cheatPayloadSha256));
                }

                _cheatPayload = CopyAndVerify(cheatPayload, cheatPayloadSha256.Value, nameof(cheatPayload));
            }

            if (committedFactsPayload != null)
            {
                if (!committedFactsPayloadSha256.HasValue)
                {
                    throw new ArgumentException("Committed facts payload requires a digest.", nameof(committedFactsPayloadSha256));
                }

                _committedFactsPayload = CopyAndVerify(
                    committedFactsPayload,
                    committedFactsPayloadSha256.Value,
                    nameof(committedFactsPayload));
            }

            if (committedHistoryPayload != null)
            {
                if (!committedHistoryPayloadSha256.HasValue)
                {
                    throw new ArgumentException("Committed history payload requires a digest.", nameof(committedHistoryPayloadSha256));
                }

                _committedHistoryPayload = CopyAndVerify(
                    committedHistoryPayload,
                    committedHistoryPayloadSha256.Value,
                    nameof(committedHistoryPayload));
            }

            if (committedCommandBindingsPayload != null)
            {
                if (!committedCommandBindingsPayloadSha256.HasValue)
                {
                    throw new ArgumentException(
                        "Committed command bindings payload requires a digest.",
                        nameof(committedCommandBindingsPayloadSha256));
                }

                _committedCommandBindingsPayload = CopyAndVerify(
                    committedCommandBindingsPayload,
                    committedCommandBindingsPayloadSha256.Value,
                    nameof(committedCommandBindingsPayload));
            }

            RoomId = roomId;
            StateVersion = stateVersion;
            LastEventSeq = lastEventSeq;
            StateFingerprint = stateFingerprint;
            RulesetVersion = rulesetVersion;
            StatePayloadSha256 = statePayloadSha256;
            ControlVersion = controlVersion;
            ControlPayloadSha256 = controlPayloadSha256;
            TimingPayloadSha256 = timingPayloadSha256;
            CheatPayloadSha256 = cheatPayloadSha256;
            CommittedFactsPayloadSha256 = committedFactsPayloadSha256;
            CreatedAtUtcMs = createdAtUtcMs;
            BootId = bootId;
            SchemaVersion = schemaVersion;
            TimingVersion = timingVersion;
            CapturedMonotonicMillis = capturedMonotonicMillis;
            CommittedHistoryPayloadSha256 = committedHistoryPayloadSha256;
            CommittedCommandBindingsPayloadSha256 = committedCommandBindingsPayloadSha256;
        }

        public string RoomId { get; }

        public StateVersion StateVersion { get; }

        public EventSeq LastEventSeq { get; }

        public ulong StateFingerprint { get; }

        public int RulesetVersion { get; }

        public int SchemaVersion { get; }

        public TablePayloadSha256 StatePayloadSha256 { get; }

        public TablePayloadSha256 ControlPayloadSha256 { get; }

        public TablePayloadSha256 TimingPayloadSha256 { get; }

        public TablePayloadSha256? CheatPayloadSha256 { get; }

        public TablePayloadSha256? CommittedFactsPayloadSha256 { get; }

        public long TimingVersion { get; }

        public long CapturedMonotonicMillis { get; }

        public TablePayloadSha256? CommittedHistoryPayloadSha256 { get; }

        public TablePayloadSha256? CommittedCommandBindingsPayloadSha256 { get; }

        public long ControlVersion { get; }

        public ulong CreatedAtUtcMs { get; }

        public string BootId { get; }

        public byte[] StatePayload => Copy(_statePayload);

        public byte[] ControlPayload => Copy(_controlPayload);

        public byte[] TimingPayload => Copy(_timingPayload);

        public byte[]? CheatPayload => _cheatPayload == null ? null : Copy(_cheatPayload);

        public byte[]? CommittedFactsPayload =>
            _committedFactsPayload == null ? null : Copy(_committedFactsPayload);

        public byte[]? CommittedHistoryPayload =>
            _committedHistoryPayload == null ? null : Copy(_committedHistoryPayload);

        public byte[]? CommittedCommandBindingsPayload =>
            _committedCommandBindingsPayload == null ? null : Copy(_committedCommandBindingsPayload);

        internal ReadOnlySpan<byte> StatePayloadSpan => new ReadOnlySpan<byte>(_statePayload);

        internal ReadOnlySpan<byte> ControlPayloadSpan => new ReadOnlySpan<byte>(_controlPayload);

        internal ReadOnlySpan<byte> TimingPayloadSpan => new ReadOnlySpan<byte>(_timingPayload);

        private static byte[] CopyAndVerify(byte[] payload, TablePayloadSha256 expected, string name)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new ArgumentException("Checkpoint payload cannot be empty.", name);
            }

            if (expected.IsEmpty)
            {
                throw new ArgumentException("Checkpoint payload requires a digest.", name);
            }

            var copy = Copy(payload);
            if (TablePayloadSha256.Compute(copy) != expected)
            {
                throw new ArgumentException("Checkpoint payload digest does not match its bytes.", name);
            }

            return copy;
        }

        private static byte[] Copy(byte[] payload)
        {
            var copy = new byte[payload.Length];
            Array.Copy(payload, copy, payload.Length);
            return copy;
        }
    }

    public sealed class TableStateRow
    {
        public TableStateRow(
            string roomId,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            ulong stateFingerprint,
            TablePayloadSha256 stateHash,
            int rulesetVersion,
            long controlVersion,
            ulong updatedAtUtcMs,
            long timingVersion = 0L)
        {
            RoomId = roomId;
            StateVersion = stateVersion;
            LastEventSeq = lastEventSeq;
            StateFingerprint = stateFingerprint;
            StateHash = stateHash;
            RulesetVersion = rulesetVersion;
            ControlVersion = controlVersion;
            UpdatedAtUtcMs = updatedAtUtcMs;
            TimingVersion = timingVersion;
        }

        public string RoomId { get; }

        public StateVersion StateVersion { get; }

        public EventSeq LastEventSeq { get; }

        public ulong StateFingerprint { get; }

        public TablePayloadSha256 StateHash { get; }

        public int RulesetVersion { get; }

        public long ControlVersion { get; }

        public ulong UpdatedAtUtcMs { get; }

        public long TimingVersion { get; }
    }

    /// <summary>
    /// One atomic durable command boundary. A player-scoped command always carries a receipt;
    /// an internal tick/AI command is explicitly marked and still carries its events/command
    /// journal. A REJECTED batch has no events, no command journal and no state payload.
    /// </summary>
    public sealed class TableDurableCommitRequest
    {
        private readonly ReadOnlyCollection<TableEventRecord> _events;
        private readonly ReadOnlyCollection<TableSettlementOutboxRecord> _outbox;

        public TableDurableCommitRequest(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedOwnerEpoch,
            StateVersion expectedStateVersion,
            TableDurableReceipt? receipt,
            bool internalCommand,
            IReadOnlyList<TableEventRecord>? events,
            TableCommandJournalRecord? commandJournal,
            IReadOnlyList<TableSettlementOutboxRecord>? outbox,
            TableCheckpoint? checkpoint,
            byte[]? nextStatePayload,
            ulong nextStateFingerprint,
            bool terminal,
            TableTimingCheckpoint? timing = null,
            long timingVersion = 0L,
            byte[]? committedFactsPayload = null,
            TablePayloadSha256? committedFactsPayloadSha256 = null)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(ownerInstance))
            {
                throw new ArgumentException("Owner instance cannot be blank.", nameof(ownerInstance));
            }

            ReceiptStatus? status = receipt?.Receipt.Status;
            bool accepted = internalCommand ||
                            status == ReceiptStatus.Applied ||
                            status == ReceiptStatus.Recorded;
            bool rejected = !internalCommand && status == ReceiptStatus.Rejected;
            bool rejectedWithState = rejected && events != null && events.Count > 0;

            if (!internalCommand && receipt == null)
            {
                throw new ArgumentException("A player-scoped commit requires a receipt.", nameof(receipt));
            }

            if (receipt != null && receipt.Receipt.EvaluatedStateVersion != expectedStateVersion)
            {
                throw new ArgumentException("Receipt evaluated version must equal the expected state version.", nameof(receipt));
            }

            if (rejected && !rejectedWithState)
            {
                if (events != null && events.Count != 0)
                {
                    throw new ArgumentException("A REJECTED batch cannot contain events.", nameof(events));
                }

                if (commandJournal != null)
                {
                    throw new ArgumentException("A REJECTED batch cannot contain a command journal.", nameof(commandJournal));
                }

                if (outbox != null && outbox.Count != 0)
                {
                    throw new ArgumentException("A REJECTED batch cannot contain outbox records.", nameof(outbox));
                }

                if (checkpoint != null || nextStatePayload != null)
                {
                    throw new ArgumentException("A REJECTED batch cannot change state or write a checkpoint.");
                }

                if (timing != null || timingVersion != 0)
                {
                    throw new ArgumentException("A REJECTED batch cannot change timing.");
                }
            }
            else if (accepted || rejectedWithState)
            {
                if (timing == null)
                {
                    throw new ArgumentException("An accepted command requires a post-command timing snapshot.", nameof(timing));
                }

                if (timingVersion < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(timingVersion), timingVersion, "Accepted timing version must be positive.");
                }
                if (events == null || events.Count == 0)
                {
                    throw new ArgumentException("An accepted command requires at least one event.", nameof(events));
                }

                if (commandJournal == null)
                {
                    throw new ArgumentException("An accepted command requires a command journal record.", nameof(commandJournal));
                }

                if (nextStatePayload == null || nextStatePayload.Length == 0)
                {
                    throw new ArgumentException("An accepted command requires a state payload.", nameof(nextStatePayload));
                }

                if (nextStateFingerprint == 0)
                {
                    throw new ArgumentException("An accepted command requires a nonzero state fingerprint.", nameof(nextStateFingerprint));
                }

                ulong expectedSeq = (ulong)events.Count;
                if (events[events.Count - 1].Seq.Value < expectedSeq)
                {
                    throw new ArgumentException("Event sequence cannot precede the batch size.", nameof(events));
                }

                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Ordinal != i)
                    {
                        throw new ArgumentException("Event ordinals must be contiguous from zero.", nameof(events));
                    }

                    if (events[i].CommandVersion != expectedStateVersion.Next())
                    {
                        throw new ArgumentException("Events must share expectedStateVersion + 1.", nameof(events));
                    }
                }

                if (commandJournal.CommandVersion != expectedStateVersion.Next())
                {
                    throw new ArgumentException("Command journal version must equal expectedStateVersion + 1.", nameof(commandJournal));
                }

                if (checkpoint != null)
                {
                    if (checkpoint.StateVersion != commandJournal.CommandVersion ||
                        checkpoint.LastEventSeq != events[events.Count - 1].Seq)
                    {
                        throw new ArgumentException("Checkpoint version/seq must equal the committed batch.", nameof(checkpoint));
                    }
                }
            }
            else if (!internalCommand)
            {
                throw new ArgumentException("A command batch must be accepted or rejected.", nameof(receipt));
            }

            if (outbox != null && outbox.Count > 0 && !terminal)
            {
                throw new ArgumentException("Settlement outbox is final-only.", nameof(outbox));
            }

            var eventCopy = new List<TableEventRecord>(events == null ? 0 : events.Count);
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    eventCopy.Add(events[i] ?? throw new ArgumentException("Event cannot be null.", nameof(events)));
                }
            }

            var outboxCopy = new List<TableSettlementOutboxRecord>(outbox == null ? 0 : outbox.Count);
            if (outbox != null)
            {
                for (int i = 0; i < outbox.Count; i++)
                {
                    outboxCopy.Add(outbox[i] ?? throw new ArgumentException("Outbox record cannot be null.", nameof(outbox)));
                }
            }

            RoomId = roomId;
            OwnerInstance = ownerInstance;
            ExpectedOwnerEpoch = expectedOwnerEpoch;
            ExpectedStateVersion = expectedStateVersion;
            Receipt = receipt;
            InternalCommand = internalCommand;
            RejectedWithState = rejectedWithState;
            _events = new ReadOnlyCollection<TableEventRecord>(eventCopy);
            CommandJournal = commandJournal;
            _outbox = new ReadOnlyCollection<TableSettlementOutboxRecord>(outboxCopy);
            Checkpoint = checkpoint;
            NextStatePayload = nextStatePayload == null ? null : Copy(nextStatePayload);
            NextStateFingerprint = nextStateFingerprint;
            Terminal = terminal;
            Timing = timing;
            TimingVersion = timingVersion;
            CommittedFactsPayload = committedFactsPayload == null
                ? null
                : Copy(committedFactsPayload);
            CommittedFactsPayloadSha256 = committedFactsPayloadSha256;
        }

        public string RoomId { get; }

        public string OwnerInstance { get; }

        public OwnerEpoch ExpectedOwnerEpoch { get; }

        public StateVersion ExpectedStateVersion { get; }

        public TableDurableReceipt? Receipt { get; }

        public bool InternalCommand { get; }

        /// <summary>
        /// True only for a committed P1.4 forced-state rejection: the receipt says REJECTED but
        /// the private forced state/events/journal are persisted in the same transaction.
        /// </summary>
        public bool RejectedWithState { get; }

        public IReadOnlyList<TableEventRecord> Events => _events;

        public TableCommandJournalRecord? CommandJournal { get; }

        public IReadOnlyList<TableSettlementOutboxRecord> Outbox => _outbox;

        public TableCheckpoint? Checkpoint { get; }

        public byte[]? NextStatePayload { get; }

        public ulong NextStateFingerprint { get; }

        public bool Terminal { get; }

        public TableTimingCheckpoint? Timing { get; }

        public long TimingVersion { get; }

        public byte[]? CommittedFactsPayload { get; }

        public TablePayloadSha256? CommittedFactsPayloadSha256 { get; }

        public StateVersion CommandVersion => ExpectedStateVersion.Next();

        private static byte[] Copy(byte[] values)
        {
            var copy = new byte[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }
    }

    public enum TableCommitDisposition : byte
    {
        Committed = 0,
        Replayed = 1,
        Conflict = 2,
        StaleOwner = 3,
        StaleState = 4,
        ScopeRejected = 5,
        Failed = 6,
        OutcomeUnknown = 7,
        Cancelled = 8
    }

    public sealed class TableDurableCommitResult
    {
        private TableDurableCommitResult(
            TableCommitDisposition disposition,
            string reasonCode,
            string detail,
            TableDurableReceipt? receipt)
        {
            if (disposition != TableCommitDisposition.Committed &&
                disposition != TableCommitDisposition.Replayed &&
                string.IsNullOrEmpty(reasonCode))
            {
                throw new ArgumentException("A non-committed result requires a reason code.", nameof(reasonCode));
            }

            Disposition = disposition;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            Receipt = receipt;
        }

        public TableCommitDisposition Disposition { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public TableDurableReceipt? Receipt { get; }

        public bool IsCommitted => Disposition == TableCommitDisposition.Committed;

        public bool IsReplayed => Disposition == TableCommitDisposition.Replayed;

        public static TableDurableCommitResult Committed(TableDurableReceipt? receipt) =>
            new TableDurableCommitResult(TableCommitDisposition.Committed, string.Empty, string.Empty, receipt);

        public static TableDurableCommitResult Replayed(TableDurableReceipt receipt) =>
            new TableDurableCommitResult(TableCommitDisposition.Replayed, string.Empty, "Receipt replayed.", receipt);

        public static TableDurableCommitResult Conflict(string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.Conflict, ProtocolErrorCode.RequestIdConflict, detail, null);

        public static TableDurableCommitResult StaleOwner(string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.StaleOwner, TableRuntimeReasonCode.StaleOwnerEpoch, detail, null);

        public static TableDurableCommitResult StaleState(string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.StaleState, TableRuntimeReasonCode.StaleStateVersion, detail, null);

        public static TableDurableCommitResult ScopeRejected(string reasonCode, string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.ScopeRejected, reasonCode, detail, null);

        public static TableDurableCommitResult Failed(string reasonCode, string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.Failed, reasonCode, detail, null);

        public static TableDurableCommitResult OutcomeUnknown(string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.OutcomeUnknown, TableRuntimeReasonCode.CommitOutcomeUnknown, detail, null);

        public static TableDurableCommitResult Cancelled(string detail) =>
            new TableDurableCommitResult(TableCommitDisposition.Cancelled, TableRuntimeReasonCode.CommitCancelled, detail, null);
    }

    public sealed class TableLeaseSnapshot
    {
        public TableLeaseSnapshot(
            string roomId,
            string ownerInstance,
            OwnerEpoch ownerEpoch,
            ulong expiresAtUtcMs,
            ulong updatedAtUtcMs)
        {
            RoomId = roomId;
            OwnerInstance = ownerInstance;
            OwnerEpoch = ownerEpoch;
            ExpiresAtUtcMs = expiresAtUtcMs;
            UpdatedAtUtcMs = updatedAtUtcMs;
        }

        public string RoomId { get; }

        public string OwnerInstance { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ulong ExpiresAtUtcMs { get; }

        public ulong UpdatedAtUtcMs { get; }

        public bool IsExpiredAt(ulong utcNowMs) => utcNowMs >= ExpiresAtUtcMs;
    }

    public enum TableLeaseAcquireStatus : byte
    {
        Acquired = 0,
        Held = 1,
        Failed = 2
    }

    public sealed class TableLeaseAcquireResult
    {
        private TableLeaseAcquireResult(TableLeaseAcquireStatus status, TableLeaseSnapshot? lease, string detail)
        {
            Status = status;
            Lease = lease;
            Detail = detail ?? string.Empty;
        }

        public TableLeaseAcquireStatus Status { get; }

        public TableLeaseSnapshot? Lease { get; }

        public string Detail { get; }

        public bool Acquired => Status == TableLeaseAcquireStatus.Acquired;

        public static TableLeaseAcquireResult Success(TableLeaseSnapshot lease) =>
            new TableLeaseAcquireResult(TableLeaseAcquireStatus.Acquired, lease, string.Empty);

        public static TableLeaseAcquireResult Held(TableLeaseSnapshot lease, string detail) =>
            new TableLeaseAcquireResult(TableLeaseAcquireStatus.Held, lease, detail);

        public static TableLeaseAcquireResult Failed(string detail) =>
            new TableLeaseAcquireResult(TableLeaseAcquireStatus.Failed, null, detail);
    }

    public enum TableLeaseRenewStatus : byte
    {
        Renewed = 0,
        StaleOwner = 1,
        Expired = 2,
        Failed = 3
    }

    public sealed class TableLeaseRenewResult
    {
        private TableLeaseRenewResult(TableLeaseRenewStatus status, TableLeaseSnapshot? lease, string detail)
        {
            Status = status;
            Lease = lease;
            Detail = detail ?? string.Empty;
        }

        public TableLeaseRenewStatus Status { get; }

        public TableLeaseSnapshot? Lease { get; }

        public string Detail { get; }

        public bool Renewed => Status == TableLeaseRenewStatus.Renewed;

        public static TableLeaseRenewResult Success(TableLeaseSnapshot lease) =>
            new TableLeaseRenewResult(TableLeaseRenewStatus.Renewed, lease, string.Empty);

        public static TableLeaseRenewResult Stale(string detail) =>
            new TableLeaseRenewResult(TableLeaseRenewStatus.StaleOwner, null, detail);

        public static TableLeaseRenewResult Expired(string detail) =>
            new TableLeaseRenewResult(TableLeaseRenewStatus.Expired, null, detail);

        public static TableLeaseRenewResult Failed(string detail) =>
            new TableLeaseRenewResult(TableLeaseRenewStatus.Failed, null, detail);
    }

    /// <summary>
    /// Fence for every checkpoint write. The store validates owner/epoch/lease, state version,
    /// control version and timing version inside the same lock/transaction. A pre-read in the
    /// actor is never the linearization point.
    /// </summary>
    public sealed class TableCheckpointFence
    {
        public TableCheckpointFence(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedOwnerEpoch,
            StateVersion expectedStateVersion,
            long expectedControlVersion,
            long expectedTimingVersion)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(ownerInstance))
            {
                throw new ArgumentException("Owner instance cannot be blank.", nameof(ownerInstance));
            }

            if (expectedControlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedControlVersion), expectedControlVersion, "Control version must be non-negative.");
            }

            if (expectedTimingVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedTimingVersion), expectedTimingVersion, "Timing version must be non-negative.");
            }

            RoomId = roomId;
            OwnerInstance = ownerInstance;
            ExpectedOwnerEpoch = expectedOwnerEpoch;
            ExpectedStateVersion = expectedStateVersion;
            ExpectedControlVersion = expectedControlVersion;
            ExpectedTimingVersion = expectedTimingVersion;
        }

        public string RoomId { get; }

        public string OwnerInstance { get; }

        public OwnerEpoch ExpectedOwnerEpoch { get; }

        public StateVersion ExpectedStateVersion { get; }

        public long ExpectedControlVersion { get; }

        public long ExpectedTimingVersion { get; }
    }

    /// <summary>
    /// Durable timing observation. Every accepted command, decision open, control checkpoint,
    /// save-checkpoint and recovery installation advances <see cref="TimingVersion"/> and
    /// stores the full post-event reserves/open decisions with capture metadata.
    /// </summary>
    public sealed class TableTimingJournalRecord
    {
        public TableTimingJournalRecord(
            string roomId,
            long timingVersion,
            StateVersion stateVersion,
            long controlVersion,
            string bootId,
            ulong capturedAtUtcMs,
            long capturedMonotonicMillis,
            TableTimingCheckpoint timing)
        {
            RoomId = roomId;
            if (timingVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(timingVersion), timingVersion, "Timing version starts at 1.");
            }

            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            if (string.IsNullOrWhiteSpace(bootId))
            {
                throw new ArgumentException("BootId cannot be blank.", nameof(bootId));
            }

            if (capturedMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capturedMonotonicMillis), capturedMonotonicMillis, "Captured monotonic time must be non-negative.");
            }

            TimingVersion = timingVersion;
            StateVersion = stateVersion;
            ControlVersion = controlVersion;
            BootId = bootId;
            CapturedAtUtcMs = capturedAtUtcMs;
            CapturedMonotonicMillis = capturedMonotonicMillis;
            Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        }

        public string RoomId { get; }

        public long TimingVersion { get; }

        public StateVersion StateVersion { get; }

        public long ControlVersion { get; }

        public string BootId { get; }

        public ulong CapturedAtUtcMs { get; }

        public long CapturedMonotonicMillis { get; }

        public TableTimingCheckpoint Timing { get; }
    }

    public sealed class TableTimingAppendRequest
    {
        public TableTimingAppendRequest(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedOwnerEpoch,
            StateVersion expectedStateVersion,
            long expectedControlVersion,
            long expectedTimingVersion,
            TableTimingCheckpoint timing)
        {
            RoomId = roomId;
            OwnerInstance = ownerInstance;
            ExpectedOwnerEpoch = expectedOwnerEpoch;
            ExpectedStateVersion = expectedStateVersion;
            ExpectedControlVersion = expectedControlVersion;
            ExpectedTimingVersion = expectedTimingVersion;
            Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        }

        public string RoomId { get; }

        public string OwnerInstance { get; }

        public OwnerEpoch ExpectedOwnerEpoch { get; }

        public StateVersion ExpectedStateVersion { get; }

        public long ExpectedControlVersion { get; }

        public long ExpectedTimingVersion { get; }

        public TableTimingCheckpoint Timing { get; }
    }

    public sealed class TableCommittedFactsSnapshot
    {
        private readonly byte[] _payload;

        public TableCommittedFactsSnapshot(byte[] payload, TablePayloadSha256 sha256)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new ArgumentException("Committed facts payload cannot be empty.", nameof(payload));
            }

            if (sha256.IsEmpty || TablePayloadSha256.Compute(payload) != sha256)
            {
                throw new ArgumentException("Committed facts digest does not match.", nameof(sha256));
            }

            _payload = (byte[])payload.Clone();
            Sha256 = sha256;
        }

        public TablePayloadSha256 Sha256 { get; }

        public byte[] Payload => (byte[])_payload.Clone();
    }

    public interface ITableStore
    {
        ValueTask CreateRoomAsync(TableCheckpoint initialCheckpoint, CancellationToken cancellationToken);

        ValueTask<TableStateRow?> LoadStateAsync(string roomId, CancellationToken cancellationToken);

        ValueTask<TableCheckpoint?> LoadCheckpointAsync(string roomId, CancellationToken cancellationToken);

        ValueTask<TableTimingJournalRecord?> LoadTimingAsync(string roomId, CancellationToken cancellationToken);

        ValueTask<TableCommittedFactsSnapshot?> LoadCommittedFactsAsync(
            string roomId,
            CancellationToken cancellationToken);

        ValueTask<TableDurableCommitResult> AppendTimingAsync(
            TableTimingAppendRequest request,
            CancellationToken cancellationToken);

        ValueTask<TableDurableCommitResult> SaveCheckpointAsync(
            TableCheckpoint checkpoint,
            TableCheckpointFence fence,
            CancellationToken cancellationToken);

        /// <summary>
        /// Durably journals one control-authority change and its matching control checkpoint.
        /// It has its own control-version CAS and never advances state_version/event_seq.
        /// </summary>
        ValueTask<TableDurableCommitResult> CommitControlAsync(
            TableControlCommitRequest request,
            TableCheckpoint checkpoint,
            CancellationToken cancellationToken);

        ValueTask<IReadOnlyList<TableEventRecord>> LoadEventsAfterAsync(
            string roomId,
            EventSeq afterSeq,
            CancellationToken cancellationToken);

        ValueTask<IReadOnlyList<TableCommandJournalRecord>> LoadCommandsAfterAsync(
            string roomId,
            StateVersion afterVersion,
            CancellationToken cancellationToken);

        /// <summary>
        /// The one atomic boundary: lease fence, state CAS, scope recheck, events, command
        /// journal, receipt, terminal outbox and optional checkpoint commit together or not at all.
        /// </summary>
        ValueTask<TableDurableCommitResult> CommitAsync(
            TableDurableCommitRequest request,
            CancellationToken cancellationToken);

        ValueTask AppendRecoveryAuditAsync(TableRecoveryAuditRecord audit, CancellationToken cancellationToken);

        ValueTask<IReadOnlyList<TableRecoveryAuditRecord>> LoadRecoveryAuditsAsync(
            string roomId,
            CancellationToken cancellationToken);
    }

    public interface IReceiptStore
    {
        ValueTask<TableDurableReceipt?> TryGetAsync(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            CancellationToken cancellationToken);
    }

    public interface ITableLease
    {
        ValueTask<TableLeaseAcquireResult> AcquireAsync(
            string roomId,
            string ownerInstance,
            TimeSpan ttl,
            CancellationToken cancellationToken);

        ValueTask<TableLeaseRenewResult> RenewAsync(
            string roomId,
            string ownerInstance,
            OwnerEpoch expectedEpoch,
            TimeSpan ttl,
            CancellationToken cancellationToken);

        ValueTask<TableLeaseSnapshot?> GetAsync(string roomId, CancellationToken cancellationToken);
    }

    public interface ISettlementOutbox
    {
        ValueTask<TableSettlementOutboxRecord?> TryGetAsync(
            string matchId,
            int resultVersion,
            CancellationToken cancellationToken);

        ValueTask<IReadOnlyList<TableSettlementOutboxRecord>> ListUnpublishedAsync(
            CancellationToken cancellationToken);

        ValueTask<bool> MarkPublishedAsync(
            string matchId,
            int resultVersion,
            ulong publishedAtUtcMs,
            CancellationToken cancellationToken);
    }

    public enum TableRecoveryAuditKind : byte
    {
        RecoveryAdjusted = 0,
        RecoveryFailed = 1,
        LeaseAcquired = 2
    }

    public sealed class TableRecoveryAuditRecord
    {
        public TableRecoveryAuditRecord(
            string roomId,
            TableRecoveryAuditKind kind,
            ulong recordedAtUtcMs,
            string previousBootId,
            string currentBootId,
            long downtimeMillis,
            long graceMillis,
            string previousOwnerInstance,
            OwnerEpoch previousOwnerEpoch,
            OwnerEpoch currentOwnerEpoch,
            string detail)
        {
            RoomId = roomId;
            Kind = kind;
            RecordedAtUtcMs = recordedAtUtcMs;
            PreviousBootId = previousBootId;
            CurrentBootId = currentBootId;
            DowntimeMillis = downtimeMillis;
            GraceMillis = graceMillis;
            PreviousOwnerInstance = previousOwnerInstance;
            PreviousOwnerEpoch = previousOwnerEpoch;
            CurrentOwnerEpoch = currentOwnerEpoch;
            Detail = detail;
        }

        public string RoomId { get; }

        public TableRecoveryAuditKind Kind { get; }

        public ulong RecordedAtUtcMs { get; }

        public string PreviousBootId { get; }

        public string CurrentBootId { get; }

        public long DowntimeMillis { get; }

        public long GraceMillis { get; }

        public string PreviousOwnerInstance { get; }

        public OwnerEpoch PreviousOwnerEpoch { get; }

        public OwnerEpoch CurrentOwnerEpoch { get; }

        public string Detail { get; }
    }

    public interface ITableRecoveryStateSink
    {
        /// <summary>
        /// Installs verified cold-recovery state into the owning host/projection layer. The sink
        /// must throw if it cannot install the exact state; the actor then stays non-Serving.
        /// </summary>
        void Install(TableRecoveryAuxState state);
    }

    public sealed class TableRecoveryAuxState
    {
        private readonly ReadOnlyCollection<TableEventRecord> _history;

        public TableRecoveryAuxState(
            string roomId,
            StateVersion stateVersion,
            long timingVersion,
            long controlVersion,
            Mahjong.Domain.Cheat.CheatState? cheat,
            TableProjectionContextData? projectionFacts,
            IReadOnlyList<TableEventRecord>? committedHistory)
        {
            RoomId = roomId;
            StateVersion = stateVersion;
            TimingVersion = timingVersion;
            ControlVersion = controlVersion;
            Cheat = cheat;
            ProjectionFacts = projectionFacts;
            _history = new ReadOnlyCollection<TableEventRecord>(
                committedHistory == null
                    ? new List<TableEventRecord>()
                    : new List<TableEventRecord>(committedHistory));
        }

        public string RoomId { get; }

        public StateVersion StateVersion { get; }

        public long TimingVersion { get; }

        public long ControlVersion { get; }

        public Mahjong.Domain.Cheat.CheatState? Cheat { get; }

        public TableProjectionContextData? ProjectionFacts { get; }

        public IReadOnlyList<TableEventRecord> CommittedHistory => _history;
    }

    /// <summary>
    /// Durable dependencies wired into one actor. The four ports are logical faces of one
    /// atomic store/transaction; a deployment must not wire independent databases/writers.
    /// </summary>
    public sealed class TableDurableContext
    {
        public TableDurableContext(
            ITableStore store,
            IReceiptStore receipts,
            ITableLease lease,
            ISettlementOutbox outbox,
            IRequestScopeAuthority scopes,
            string ownerInstance,
            TimeSpan leaseTtl,
            string bootId,
            int rulesetVersion = 1,
            string? matchId = null)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
            Lease = lease ?? throw new ArgumentNullException(nameof(lease));
            Outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            Scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            OwnerInstance = ProtocolText.RequireId(ownerInstance, nameof(ownerInstance));
            if (leaseTtl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(leaseTtl), "Lease TTL must be positive.");
            }

            if (rulesetVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rulesetVersion), rulesetVersion, "Ruleset version must be positive.");
            }

            BootId = ProtocolText.RequireId(bootId, nameof(bootId));
            MatchId = string.IsNullOrWhiteSpace(matchId) ? null : ProtocolText.RequireId(matchId, nameof(matchId));
            LeaseTtl = leaseTtl;
            RulesetVersion = rulesetVersion;
        }

        public ITableStore Store { get; }

        public IReceiptStore Receipts { get; }

        public ITableLease Lease { get; }

        public ISettlementOutbox Outbox { get; }

        public IRequestScopeAuthority Scopes { get; }

        public string OwnerInstance { get; }

        public TimeSpan LeaseTtl { get; }

        public string BootId { get; }

        public int RulesetVersion { get; }

        public string? MatchId { get; }

        /// <summary>How long a committed receipt remains queryable. Must not be shorter than scopes.</summary>
        public TimeSpan ReceiptRetention { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Optional real cheat-ledger snapshot source owned by the host. P3.4 never invents a
        /// ledger; when absent the checkpoint has no cheat payload and strict recovery fails.
        /// </summary>
        public Func<Mahjong.Domain.Cheat.CheatLedger?>? CheatLedgerProvider { get; set; }

        /// <summary>
        /// Optional real CheatState provider. When set it is preferred over the ledger-only
        /// provider so total private-substate version/fingerprint are persisted and verified.
        /// </summary>
        public Func<Mahjong.Domain.Cheat.CheatState?>? CheatStateProvider { get; set; }

        /// <summary>
        /// Trusted P3.2 fact capture for the exact round/state/events request. It must return
        /// the same real data types consumed by strict projection; null/state mismatch blocks
        /// checkpoint/recovery rather than fabricating fixtures.
        /// </summary>
        public Func<TableProjectionContextRequest, TableProjectionContextData?>? ProjectionFactsProvider { get; set; }

        /// <summary>
        /// Required when recovery carries cheat/private/facts data. Without it the actor cannot
        /// install the verified recovery state and must not enter Serving.
        /// </summary>
        public ITableRecoveryStateSink? RecoveryStateSink { get; set; }

        /// <summary>When true, recovery must carry host committed facts or fail closed.</summary>
        public bool RequireCommittedFacts { get; set; } = true;
    }
}

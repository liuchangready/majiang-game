using System;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Definitive business receipt status. UNKNOWN is intentionally present
    /// but is never a persisted definitive receipt: it means the client,
    /// transport or a query cannot confirm whether a command committed.
    /// </summary>
    public enum ReceiptStatus
    {
        Unspecified = 0,
        Applied = 1,
        Recorded = 2,
        Rejected = 3,
        Unknown = 4
    }

    public sealed class Receipt
    {
        public Receipt(
            ReceiptStatus status,
            string? reasonCode,
            StateVersion evaluatedStateVersion,
            StateVersion? commandVersion,
            EventSeq? eventSeq,
            ulong? committedAtMs,
            PayloadDigest? canonicalPayloadDigest,
            bool replayed = false)
        {
            if (status == ReceiptStatus.Unspecified)
            {
                throw new ArgumentException("Receipt status must be explicit.", nameof(status));
            }

            string reason = reasonCode ?? string.Empty;
            if (reason.Length > 0 && !ProtocolErrorCode.IsWellFormed(reason))
            {
                throw new ArgumentException("Receipt reason must be a stable uppercase code.", nameof(reasonCode));
            }

            switch (status)
            {
                case ReceiptStatus.Applied:
                case ReceiptStatus.Recorded:
                    if (!commandVersion.HasValue)
                    {
                        throw new ArgumentException(status + " requires the original command_version.", nameof(commandVersion));
                    }

                    if (!eventSeq.HasValue || eventSeq.Value.Value == 0)
                    {
                        throw new ArgumentException(status + " requires at least one event and its last event_seq.", nameof(eventSeq));
                    }

                    if (commandVersion.Value.Value != evaluatedStateVersion.Value + 1)
                    {
                        throw new ArgumentException(
                            "command_version must equal evaluated_state_version + 1 for an accepted state-changing command.",
                            nameof(commandVersion));
                    }

                    break;
                case ReceiptStatus.Rejected:
                    if (commandVersion.HasValue || eventSeq.HasValue)
                    {
                        throw new ArgumentException("REJECTED must not carry command_version or event_seq.", nameof(commandVersion));
                    }

                    if (reason.Length == 0)
                    {
                        throw new ArgumentException("REJECTED requires a business reason code.", nameof(reasonCode));
                    }

                    break;
                case ReceiptStatus.Unknown:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown receipt status.");
            }

            if (committedAtMs.HasValue && committedAtMs.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(committedAtMs), "Committed time must be positive when present.");
            }

            Status = status;
            ReasonCode = reason;
            EvaluatedStateVersion = evaluatedStateVersion;
            CommandVersion = commandVersion;
            EventSeq = eventSeq;
            CommittedAtMs = committedAtMs;
            CanonicalPayloadDigest = canonicalPayloadDigest;
            Replayed = replayed;
        }

        public ReceiptStatus Status { get; }

        public string ReasonCode { get; }

        public StateVersion EvaluatedStateVersion { get; }

        /// <summary>Equals the resulting table state version; null only for REJECTED/UNKNOWN.</summary>
        public StateVersion? CommandVersion { get; }

        /// <summary>Last event sequence of the original command; null only for REJECTED/UNKNOWN.</summary>
        public EventSeq? EventSeq { get; }

        public ulong? CommittedAtMs { get; }

        public PayloadDigest? CanonicalPayloadDigest { get; }

        /// <summary>This delivery was an exact idempotent replay; original fields are unchanged.</summary>
        public bool Replayed { get; }

        public bool IsDefinitive =>
            Status == ReceiptStatus.Applied ||
            Status == ReceiptStatus.Recorded ||
            Status == ReceiptStatus.Rejected;

        public string CanonicalPayloadSha256 => CanonicalPayloadDigest.HasValue ? CanonicalPayloadDigest.Value.ToHex() : string.Empty;

        public static Receipt Applied(
            StateVersion evaluatedStateVersion,
            StateVersion commandVersion,
            EventSeq lastEventSeq,
            string? reasonCode = null,
            ulong? committedAtMs = null,
            PayloadDigest? digest = null)
        {
            return new Receipt(
                ReceiptStatus.Applied,
                reasonCode,
                evaluatedStateVersion,
                commandVersion,
                lastEventSeq,
                committedAtMs,
                digest);
        }

        public static Receipt Recorded(
            StateVersion evaluatedStateVersion,
            StateVersion commandVersion,
            EventSeq lastEventSeq,
            string? reasonCode = null,
            ulong? committedAtMs = null,
            PayloadDigest? digest = null)
        {
            return new Receipt(
                ReceiptStatus.Recorded,
                reasonCode,
                evaluatedStateVersion,
                commandVersion,
                lastEventSeq,
                committedAtMs,
                digest);
        }

        public static Receipt Rejected(
            StateVersion evaluatedStateVersion,
            string reasonCode,
            PayloadDigest? digest = null)
        {
            return new Receipt(
                ReceiptStatus.Rejected,
                reasonCode,
                evaluatedStateVersion,
                null,
                null,
                null,
                digest);
        }

        public static Receipt Unknown(PayloadDigest? digest = null)
        {
            return new Receipt(
                ReceiptStatus.Unknown,
                null,
                new StateVersion(0),
                null,
                null,
                null,
                digest);
        }

        /// <summary>
        /// Returns the original definitive fields with only the delivery flag
        /// changed. It must never recompute versions or clear original values.
        /// </summary>
        public Receipt AsReplay()
        {
            if (!IsDefinitive)
            {
                throw new InvalidOperationException("UNKNOWN cannot be replayed as a definitive receipt.");
            }

            return new Receipt(
                Status,
                ReasonCode,
                EvaluatedStateVersion,
                CommandVersion,
                EventSeq,
                CommittedAtMs,
                CanonicalPayloadDigest,
                replayed: true);
        }
    }

    public enum ReceiptLookupStatus
    {
        Unspecified = 0,
        Found = 1,
        Unknown = 2,
        NotFound = 3
    }

    /// <summary>
    /// RECEIPT_QUERY result. UNKNOWN and NOT_FOUND never prove that a command
    /// was not committed: receipts can be lost or cleaned while the side effect
    /// exists. Reconciliation must not auto-resubmit with a new request id.
    /// </summary>
    public sealed class ReceiptLookupResult
    {
        public ReceiptLookupResult(ReceiptLookupStatus status, Receipt? receipt, PayloadDigest? digest)
        {
            if (status == ReceiptLookupStatus.Found)
            {
                if (receipt == null || !receipt.IsDefinitive)
                {
                    throw new ArgumentException("FOUND requires a definitive receipt.", nameof(receipt));
                }
            }
            else if (status != ReceiptLookupStatus.Unknown && status != ReceiptLookupStatus.NotFound)
            {
                throw new ArgumentException("Lookup status must be explicit.", nameof(status));
            }
            else if (receipt != null)
            {
                throw new ArgumentException("UNKNOWN/NOT_FOUND must not carry a definitive receipt.", nameof(receipt));
            }

            Status = status;
            Receipt = receipt;
            Digest = digest;
        }

        public ReceiptLookupStatus Status { get; }

        public Receipt? Receipt { get; }

        public PayloadDigest? Digest { get; }

        public bool IsDefinitive => Status == ReceiptLookupStatus.Found;

        public bool ProvesNotCommitted => false;

        public bool RequiresReconciliation =>
            Status == ReceiptLookupStatus.Unknown || Status == ReceiptLookupStatus.NotFound;

        public static ReceiptLookupResult Found(Receipt receipt)
        {
            return new ReceiptLookupResult(ReceiptLookupStatus.Found, receipt, receipt.CanonicalPayloadDigest);
        }

        public static ReceiptLookupResult Unknown(PayloadDigest? digest = null)
        {
            return new ReceiptLookupResult(ReceiptLookupStatus.Unknown, null, digest);
        }

        public static ReceiptLookupResult NotFound(PayloadDigest? digest = null)
        {
            return new ReceiptLookupResult(ReceiptLookupStatus.NotFound, null, digest);
        }
    }

    public readonly struct ReceiptQueryRequest
    {
        public ReceiptQueryRequest(RequestScopeId scopeId, RequestId requestId)
        {
            if (scopeId.IsEmpty)
            {
                throw new ArgumentException("Receipt query requires a scope id.", nameof(scopeId));
            }

            if (requestId.IsEmpty)
            {
                throw new ArgumentException("Receipt query requires a request id.", nameof(requestId));
            }

            ScopeId = scopeId;
            RequestId = requestId;
        }

        public RequestScopeId ScopeId { get; }

        public RequestId RequestId { get; }
    }

    public enum IdempotencyDecision
    {
        Unspecified = 0,
        New = 1,
        Replay = 2,
        Conflict = 3
    }

    /// <summary>
    /// Idempotency is keyed by (accountId, requestScopeId, requestId) and the
    /// canonical payload digest. A missing existing digest is a new key; equal
    /// digests are an exact replay; different digests are a conflict.
    /// </summary>
    public static class ReceiptIdempotency
    {
        public static IdempotencyDecision Classify(PayloadDigest? existingDigest, PayloadDigest incomingDigest)
        {
            if (!existingDigest.HasValue)
            {
                return IdempotencyDecision.New;
            }

            return existingDigest.Value == incomingDigest
                ? IdempotencyDecision.Replay
                : IdempotencyDecision.Conflict;
        }

        public static Receipt ResolveReplay(Receipt original)
        {
            if (original == null)
            {
                throw new ArgumentNullException(nameof(original));
            }

            return original.AsReplay();
        }
    }
}

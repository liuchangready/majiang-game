using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>Lifecycle of one table actor. It is mutated only by the single mailbox consumer.</summary>
    public enum TableActorLifecycle : byte
    {
        Serving = 0,
        Recovering = 1,
        LeaseLost = 2,
        Closed = 3,
        Faulted = 4
    }

    /// <summary>
    /// Outcome of one serialized operation. <see cref="Accepted"/> means a real Domain
    /// transition committed and the version advanced exactly once. <see cref="Rejected"/> is a
    /// business/domain refusal with no state or version change. <see cref="Failed"/> is an
    /// infrastructure failure (commit port, missing event, version mismatch) and also changes
    /// nothing. <see cref="MailboxFull"/> and <see cref="Closed"/> mean the operation was never
    /// admitted. <see cref="Configured"/> is a non-state-changing in-memory operation such as
    /// opening a decision or installing a recovery baseline. P3.3 control changes use their
    /// own <see cref="TableControlStatus"/> and <c>ControlVersion</c>; P3.4 must checkpoint the
    /// decision clock/recovery baseline, so Configured is not a durability claim.
    /// </summary>
    public enum TableOperationStatus : byte
    {
        Accepted = 0,
        Rejected = 1,
        Failed = 2,
        MailboxFull = 3,
        Closed = 4,
        Configured = 5,
        Replayed = 6,
        Conflict = 7
    }

    public enum TablePublicationKind : byte
    {
        Applied = 0,
        Recovered = 1,
        LeaseLost = 2,
        ControlChanged = 3
    }

    /// <summary>
    /// P3.1 table configuration. The mailbox bound is split into a business capacity and a
    /// small control reserve: client commands/AI results use the business capacity, while
    /// deadline ticks, decision opening, recovery, lease-loss, snapshot reads and close use the
    /// reserve so a full business queue cannot silently discard an authority-critical message.
    /// The total queue is still bounded by <see cref="MailboxCapacity"/> + <see cref="ControlReserve"/>.
    /// </summary>
    public sealed class TableActorOptions
    {
        public string RoomId { get; set; } = "room";

        public int MailboxCapacity { get; set; } = 64;

        /// <summary>Additional bounded slots for control messages. Must be at least one.</summary>
        public int ControlReserve { get; set; } = 16;

        /// <summary>Recent event window returned by the authority snapshot. Full replay is P3.2/P3.4.</summary>
        public int SnapshotEventWindow { get; set; } = 64;

        public OwnerEpoch InitialOwnerEpoch { get; set; } = new OwnerEpoch(1);

        /// <summary>Initial durable timing version. The first durable write advances it by one.</summary>
        public long InitialTimingVersion { get; set; } = 1L;

        /// <summary>Initial seat controllers. P3.3 owns runtime takeover/reclaim changes.</summary>
        public IReadOnlyDictionary<int, SeatControllerKind>? InitialControllers { get; set; }

        public DecisionClockConfig? DecisionClockConfig { get; set; }

        public DisconnectPolicy? DisconnectPolicy { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(RoomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(RoomId));
            }

            if (MailboxCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MailboxCapacity), MailboxCapacity, "Mailbox capacity must be positive.");
            }

            if (ControlReserve < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(ControlReserve), ControlReserve, "Control reserve must be positive.");
            }

            if (SnapshotEventWindow < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(SnapshotEventWindow), SnapshotEventWindow, "Snapshot event window cannot be negative.");
            }

            if (InitialOwnerEpoch.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(InitialOwnerEpoch), "Owner epoch starts at 1.");
            }

            if (InitialTimingVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(InitialTimingVersion), InitialTimingVersion, "Timing version must be non-negative.");
            }

            if (InitialControllers != null)
            {
                foreach (KeyValuePair<int, SeatControllerKind> entry in InitialControllers)
                {
                    if (entry.Key < 0 || entry.Key > 3)
                    {
                        throw new ArgumentOutOfRangeException(nameof(InitialControllers), entry.Key, "Seat must be 0..3.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// One authorized player command plus the identity fields the actor re-checks inside its
    /// mailbox. Domain owns the command body; P3.1 does not accept a generic reducer.
    /// </summary>
    public sealed class TableCommandEnvelope
    {
        public TableCommandEnvelope(
            RoundCommand command,
            RequestId requestId,
            StateVersion expectedStateVersion,
            OwnerEpoch ownerEpoch,
            ControlEpoch controlEpoch,
            DecisionId? decisionId = null,
            long recoveryGeneration = 0L,
            RequestScopeId? scopeId = null,
            string? api = null,
            TablePayloadSha256? payloadSha256 = null)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            if (requestId.IsEmpty)
            {
                throw new ArgumentException("A table command requires a request id.", nameof(requestId));
            }

            if (recoveryGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recoveryGeneration),
                    recoveryGeneration,
                    "Recovery generation must be non-negative.");
            }

            bool hasScope = scopeId.HasValue && !scopeId.Value.IsEmpty;
            bool hasApi = !string.IsNullOrEmpty(api);
            bool hasDigest = payloadSha256.HasValue && !payloadSha256.Value.IsEmpty;
            if (hasScope != hasApi || hasScope != hasDigest)
            {
                throw new ArgumentException(
                    "A durable command identity requires scopeId, api and payloadSha256 together.");
            }

            RequestId = requestId;
            ExpectedStateVersion = expectedStateVersion;
            OwnerEpoch = ownerEpoch;
            ControlEpoch = controlEpoch;
            DecisionId = decisionId;
            RecoveryGeneration = recoveryGeneration;
            ScopeId = hasScope ? scopeId : (RequestScopeId?)null;
            Api = hasApi ? TableDurableReceipt.RequireApi(api!) : null;
            PayloadSha256 = hasDigest ? payloadSha256 : (TablePayloadSha256?)null;
        }

        public RoundCommand Command { get; }

        public RequestId RequestId { get; }

        public StateVersion ExpectedStateVersion { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch ControlEpoch { get; }

        public DecisionId? DecisionId { get; }

        /// <summary>
        /// Recovery generation the command was admitted under. After a control-baseline
        /// recovery the actor requires this to equal its current generation, so a command that
        /// was already in flight before recovery cannot mutate the new baseline.
        /// </summary>
        public long RecoveryGeneration { get; }

        /// <summary>Host-issued scope; never a client-selected authorization field.</summary>
        public RequestScopeId? ScopeId { get; }

        /// <summary>Stable API name used with the original business-byte digest.</summary>
        public string? Api { get; }

        /// <summary>SHA-256 over the exact business bytes, computed by the trusted host.</summary>
        public TablePayloadSha256? PayloadSha256 { get; }

        public bool HasDurableIdentity => ScopeId.HasValue && Api != null && PayloadSha256.HasValue;
    }

    /// <summary>
    /// One committed room event. <see cref="CommandVersion"/> is the new state version shared by
    /// every event of one accepted command and <see cref="Ordinal"/> is the contiguous
    /// 0..N-1 index inside that command. <see cref="Seq"/> is the independent room event sequence.
    /// </summary>
    public sealed class TableEventRecord
    {
        private readonly byte[]? _payload;

        public TableEventRecord(
            EventSeq seq,
            StateVersion commandVersion,
            int ordinal,
            RoundEventType type,
            int seat,
            TileId? tile,
            string? detail,
            int eventSchema = 1,
            byte[]? payload = null)
        {
            if (seq.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seq), "Event sequence starts at 1.");
            }

            if (commandVersion.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandVersion), "Command version starts at 1.");
            }

            if (ordinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Event ordinal must be non-negative.");
            }

            if (seat < -1 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be -1 or 0..3.");
            }

            if (eventSchema < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(eventSchema), eventSchema, "Event schema must be positive.");
            }

            if (payload != null && payload.Length > 1024 * 1024)
            {
                throw new ArgumentException("Event payload exceeds the 1 MiB cap.", nameof(payload));
            }

            Seq = seq;
            CommandVersion = commandVersion;
            Ordinal = ordinal;
            Type = type;
            Seat = seat;
            Tile = tile;
            Detail = detail;
            EventSchema = eventSchema;
            _payload = payload == null ? null : Copy(payload);
        }

        public EventSeq Seq { get; }

        public StateVersion CommandVersion { get; }

        public int Ordinal { get; }

        public RoundEventType Type { get; }

        public int Seat { get; }

        public TileId? Tile { get; }

        public string? Detail { get; }

        /// <summary>Strict schema of the optional durable event payload.</summary>
        public int EventSchema { get; }

        /// <summary>
        /// Optional strict committed-facts payload. P3.1's compact fields remain the business
        /// identity; this payload is never a saved whole candidate and is bounded/defensively
        /// copied. P3.4 persists and replays the command journal separately.
        /// </summary>
        public byte[]? Payload => _payload == null ? null : Copy(_payload);

        public bool HasPayload => _payload != null;

        private static byte[] Copy(byte[] value)
        {
            var copy = new byte[value.Length];
            Array.Copy(value, copy, value.Length);
            return copy;
        }
    }

    /// <summary>
    /// Minimal P3.1 receipt. It proves only the table transition identity. The full
    /// scope/payload/receipt-query contract is P3.4; persistence is P5.2.
    /// </summary>
    public sealed class TableCommandReceipt
    {
        public TableCommandReceipt(
            ReceiptStatus status,
            string reasonCode,
            StateVersion evaluatedStateVersion,
            StateVersion? commandVersion,
            EventSeq? lastEventSeq,
            RequestId requestId)
        {
            if (status == ReceiptStatus.Unspecified || status == ReceiptStatus.Unknown)
            {
                throw new ArgumentException("A table receipt must be a definitive status.", nameof(status));
            }

            if (requestId.IsEmpty)
            {
                throw new ArgumentException("A table receipt requires a request id.", nameof(requestId));
            }

            string reason = reasonCode ?? string.Empty;
            if (reason.Length > 0 && !ProtocolErrorCode.IsWellFormed(reason))
            {
                throw new ArgumentException("Reason must be a stable uppercase code.", nameof(reasonCode));
            }

            switch (status)
            {
                case ReceiptStatus.Applied:
                case ReceiptStatus.Recorded:
                    if (!commandVersion.HasValue || !lastEventSeq.HasValue)
                    {
                        throw new ArgumentException("An accepted receipt requires command_version and last event_seq.");
                    }

                    if (evaluatedStateVersion.Value == ulong.MaxValue ||
                        commandVersion.Value.Value != evaluatedStateVersion.Value + 1)
                    {
                        throw new ArgumentException(
                            "command_version must be evaluated_state_version + 1.",
                            nameof(commandVersion));
                    }

                    if (lastEventSeq.Value.Value == 0)
                    {
                        throw new ArgumentException("An accepted command requires at least one event.", nameof(lastEventSeq));
                    }

                    break;
                case ReceiptStatus.Rejected:
                    if (commandVersion.HasValue || lastEventSeq.HasValue)
                    {
                        throw new ArgumentException("REJECTED must not carry command_version or event_seq.");
                    }

                    if (reason.Length == 0)
                    {
                        throw new ArgumentException("REJECTED requires a reason code.", nameof(reasonCode));
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported receipt status.");
            }

            Status = status;
            ReasonCode = reason;
            EvaluatedStateVersion = evaluatedStateVersion;
            CommandVersion = commandVersion;
            LastEventSeq = lastEventSeq;
            RequestId = requestId;
        }

        public ReceiptStatus Status { get; }

        public string ReasonCode { get; }

        public StateVersion EvaluatedStateVersion { get; }

        public StateVersion? CommandVersion { get; }

        public EventSeq? LastEventSeq { get; }

        public RequestId RequestId { get; }

        public bool IsAccepted => Status == ReceiptStatus.Applied || Status == ReceiptStatus.Recorded;

        public static TableCommandReceipt Applied(
            StateVersion evaluatedStateVersion,
            StateVersion commandVersion,
            EventSeq lastEventSeq,
            RequestId requestId,
            string? reasonCode = null)
        {
            return new TableCommandReceipt(
                ReceiptStatus.Applied,
                reasonCode ?? string.Empty,
                evaluatedStateVersion,
                commandVersion,
                lastEventSeq,
                requestId);
        }

        public static TableCommandReceipt Rejected(
            StateVersion evaluatedStateVersion,
            string reasonCode,
            RequestId requestId)
        {
            return new TableCommandReceipt(
                ReceiptStatus.Rejected,
                reasonCode,
                evaluatedStateVersion,
                null,
                null,
                requestId);
        }
    }

    /// <summary>Immutable result returned to one admitted mailbox caller.</summary>
    public sealed class TableOperationResult
    {
        private readonly ReadOnlyCollection<TableEventRecord> _events;

        private TableOperationResult(
            TableOperationStatus status,
            string reasonCode,
            string detail,
            StateVersion evaluatedStateVersion,
            TableCommandReceipt? receipt,
            IReadOnlyList<TableEventRecord>? events,
            TableDurableReceipt? durableReceipt = null,
            bool replayed = false)
        {
            if (status == TableOperationStatus.Accepted && receipt == null)
            {
                throw new ArgumentException("An accepted operation requires a receipt.", nameof(receipt));
            }

            if (status == TableOperationStatus.Replayed && (receipt == null || durableReceipt == null))
            {
                throw new ArgumentException("A replayed operation requires its original durable receipt.", nameof(receipt));
            }

            if (status != TableOperationStatus.Accepted &&
                status != TableOperationStatus.Replayed &&
                receipt != null)
            {
                throw new ArgumentException("Only an accepted/replayed operation carries a receipt.", nameof(receipt));
            }

            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            EvaluatedStateVersion = evaluatedStateVersion;
            Receipt = receipt;
            DurableReceipt = durableReceipt;
            Replayed = replayed;
            _events = new ReadOnlyCollection<TableEventRecord>(
                events == null ? new List<TableEventRecord>() : new List<TableEventRecord>(events));
            Admitted = status != TableOperationStatus.MailboxFull && status != TableOperationStatus.Closed;
        }

        public TableOperationStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool Admitted { get; }

        public StateVersion EvaluatedStateVersion { get; }

        public TableCommandReceipt? Receipt { get; }

        /// <summary>The original durable receipt for a replay; null on a first-time result.</summary>
        public TableDurableReceipt? DurableReceipt { get; }

        /// <summary>True only when this delivery replayed a previously committed receipt.</summary>
        public bool Replayed { get; }

        public StateVersion? CommandVersion => Receipt?.CommandVersion;

        public EventSeq? LastEventSeq => Receipt?.LastEventSeq;

        public IReadOnlyList<TableEventRecord> Events => _events;

        public bool IsAccepted => Status == TableOperationStatus.Accepted;

        public bool IsSuccess =>
            Status == TableOperationStatus.Accepted ||
            (Status == TableOperationStatus.Replayed && Receipt != null && Receipt.IsAccepted);

        public static TableOperationResult Accepted(
            TableCommandReceipt receipt,
            IReadOnlyList<TableEventRecord> events)
        {
            return Accepted(receipt, events, null);
        }

        public static TableOperationResult Accepted(
            TableCommandReceipt receipt,
            IReadOnlyList<TableEventRecord> events,
            TableDurableReceipt? durableReceipt)
        {
            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (events == null || events.Count == 0)
            {
                throw new ArgumentException("An accepted operation requires at least one event.", nameof(events));
            }

            return new TableOperationResult(
                TableOperationStatus.Accepted,
                receipt.ReasonCode,
                string.Empty,
                receipt.EvaluatedStateVersion,
                receipt,
                events,
                durableReceipt);
        }

        public static TableOperationResult FromReplay(
            TableDurableReceipt durableReceipt,
            IReadOnlyList<TableEventRecord>? events = null)
        {
            if (durableReceipt == null)
            {
                throw new ArgumentNullException(nameof(durableReceipt));
            }

            TableCommandReceipt receipt = durableReceipt.ToCommandReceipt();
            return new TableOperationResult(
                TableOperationStatus.Replayed,
                receipt.ReasonCode,
                "Exact receipt replay; no Domain evaluation or side effect was repeated.",
                receipt.EvaluatedStateVersion,
                receipt,
                events,
                durableReceipt,
                replayed: true);
        }

        public static TableOperationResult Conflict(string detail)
        {
            return new TableOperationResult(
                TableOperationStatus.Conflict,
                ProtocolErrorCode.RequestIdConflict,
                detail,
                new StateVersion(0),
                null,
                null);
        }

        public static TableOperationResult Rejected(
            StateVersion evaluatedStateVersion,
            string reasonCode,
            string detail)
        {
            return new TableOperationResult(
                TableOperationStatus.Rejected,
                reasonCode,
                detail,
                evaluatedStateVersion,
                null,
                null);
        }

        public static TableOperationResult RejectedCommitted(TableDurableReceipt durableReceipt)
        {
            if (durableReceipt == null)
            {
                throw new ArgumentNullException(nameof(durableReceipt));
            }

            return new TableOperationResult(
                TableOperationStatus.Rejected,
                durableReceipt.Receipt.ReasonCode,
                durableReceipt.Detail,
                durableReceipt.Receipt.EvaluatedStateVersion,
                null,
                null,
                durableReceipt);
        }

        public static TableOperationResult Failed(
            StateVersion evaluatedStateVersion,
            string reasonCode,
            string detail)
        {
            return new TableOperationResult(
                TableOperationStatus.Failed,
                reasonCode,
                detail,
                evaluatedStateVersion,
                null,
                null);
        }

        public static TableOperationResult Configured(StateVersion evaluatedStateVersion, string detail)
        {
            return new TableOperationResult(
                TableOperationStatus.Configured,
                string.Empty,
                detail,
                evaluatedStateVersion,
                null,
                null);
        }

        internal static TableOperationResult AdmissionFailure(TableOperationStatus status, string reasonCode, string detail)
        {
            if (status != TableOperationStatus.MailboxFull && status != TableOperationStatus.Closed)
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "Not an admission failure.");
            }

            return new TableOperationResult(status, reasonCode, detail, new StateVersion(0), null, null);
        }
    }

    /// <summary>One authoritative tick entry: a deadline timeout or an AI result disposition.</summary>
    public sealed class TableTickEntry
    {
        public TableTickEntry(
            TickEntryKind kind,
            int seat,
            string decisionId,
            LegalActionOption? action,
            TickRejectionCode rejectionCode,
            string detail,
            TableOperationResult? transition)
        {
            Kind = kind;
            Seat = seat;
            DecisionId = decisionId ?? string.Empty;
            Action = action;
            RejectionCode = rejectionCode;
            Detail = detail ?? string.Empty;
            Transition = transition;
        }

        public TickEntryKind Kind { get; }

        public int Seat { get; }

        public string DecisionId { get; }

        public LegalActionOption? Action { get; }

        public TickRejectionCode RejectionCode { get; }

        public string Detail { get; }

        /// <summary>Domain/commit result for an accepted timeout or AI action; null for an AI rejection.</summary>
        public TableOperationResult? Transition { get; }
    }

    /// <summary>Result of one tick or AI-completion message, preserving timeout-before-AI order.</summary>
    public sealed class TableTickResult
    {
        private readonly ReadOnlyCollection<TableTickEntry> _entries;

        public TableTickResult(
            TableOperationStatus status,
            string reasonCode,
            string detail,
            IReadOnlyList<TableTickEntry>? entries,
            StateVersion finalStateVersion)
        {
            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            _entries = new ReadOnlyCollection<TableTickEntry>(
                entries == null ? new List<TableTickEntry>() : new List<TableTickEntry>(entries));
            FinalStateVersion = finalStateVersion;
            Admitted = status != TableOperationStatus.MailboxFull && status != TableOperationStatus.Closed;
        }

        public TableOperationStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool Admitted { get; }

        public IReadOnlyList<TableTickEntry> Entries => _entries;

        public StateVersion FinalStateVersion { get; }

        public static TableTickResult Processed(
            IReadOnlyList<TableTickEntry> entries,
            StateVersion finalStateVersion)
        {
            return new TableTickResult(
                TableOperationStatus.Accepted,
                string.Empty,
                string.Empty,
                entries,
                finalStateVersion);
        }

        public static TableTickResult Rejected(
            TableOperationStatus status,
            string reasonCode,
            string detail,
            StateVersion finalStateVersion)
        {
            return new TableTickResult(status, reasonCode, detail, null, finalStateVersion);
        }
    }

    /// <summary>
    /// Authority-side snapshot for ports and P3.2 baseline construction. It deliberately does
    /// not contain hands, wall order, seeds or private cheat state; per-seat/public projections
    /// are P3.2 and must be built from the domain state, never by stripping this type.
    /// </summary>
    public sealed class TableSnapshot
    {
        private readonly ReadOnlyCollection<long> _scores;
        private readonly ReadOnlyCollection<TableEventRecord> _recentEvents;

        public TableSnapshot(
            string roomId,
            TableActorLifecycle lifecycle,
            string lifecycleReason,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            OwnerEpoch ownerEpoch,
            string roundId,
            RoundPhase phase,
            int currentSeat,
            bool isFinished,
            IReadOnlyList<long> scores,
            ulong stateFingerprint,
            IReadOnlyList<TableEventRecord>? recentEvents)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (currentSeat < 0 || currentSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(currentSeat), currentSeat, "Seat must be 0..3.");
            }

            if (scores == null || scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four entries.", nameof(scores));
            }

            var scoreCopy = new long[4];
            for (int i = 0; i < 4; i++)
            {
                scoreCopy[i] = scores[i];
            }

            _scores = new ReadOnlyCollection<long>(scoreCopy);
            _recentEvents = new ReadOnlyCollection<TableEventRecord>(
                recentEvents == null
                    ? new List<TableEventRecord>()
                    : new List<TableEventRecord>(recentEvents));
            RoomId = roomId;
            Lifecycle = lifecycle;
            LifecycleReason = lifecycleReason ?? string.Empty;
            StateVersion = stateVersion;
            LastEventSeq = lastEventSeq;
            OwnerEpoch = ownerEpoch;
            RoundId = roundId;
            Phase = phase;
            CurrentSeat = currentSeat;
            IsFinished = isFinished;
            StateFingerprint = stateFingerprint;
        }

        public string RoomId { get; }

        public TableActorLifecycle Lifecycle { get; }

        public string LifecycleReason { get; }

        public StateVersion StateVersion { get; }

        public EventSeq LastEventSeq { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public string RoundId { get; }

        public RoundPhase Phase { get; }

        public int CurrentSeat { get; }

        public bool IsFinished { get; }

        public IReadOnlyList<long> Scores => _scores;

        public ulong StateFingerprint { get; }

        public IReadOnlyList<TableEventRecord> RecentEvents => _recentEvents;
    }

    /// <summary>
    /// Published only after the commit port reports success (or after an already-committed
    /// recovery baseline/lease-loss control event). P3.2 replaces the authority snapshot here
    /// with per-stream projections but keeps the "commit first, then publish" ordering.
    /// </summary>
    public sealed class TablePublication
    {
        private readonly ReadOnlyCollection<TableEventRecord> _events;
        private readonly ReadOnlyCollection<TableControlEvent> _controlEvents;

        public TablePublication(
            TablePublicationKind kind,
            TableSnapshot snapshot,
            IReadOnlyList<TableEventRecord>? events,
            TableCommandReceipt? receipt)
            : this(kind, snapshot, events, receipt, null, null)
        {
        }

        public TablePublication(
            TablePublicationKind kind,
            TableSnapshot snapshot,
            IReadOnlyList<TableEventRecord>? events,
            TableCommandReceipt? receipt,
            TableControlSnapshot? control,
            IReadOnlyList<TableControlEvent>? controlEvents)
        {
            Kind = kind;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _events = new ReadOnlyCollection<TableEventRecord>(
                events == null ? new List<TableEventRecord>() : new List<TableEventRecord>(events));
            _controlEvents = new ReadOnlyCollection<TableControlEvent>(
                controlEvents == null ? new List<TableControlEvent>() : new List<TableControlEvent>(controlEvents));
            Receipt = receipt;
            Control = control;
            if (kind == TablePublicationKind.Applied && (receipt == null || _events.Count == 0))
            {
                throw new ArgumentException("An APPLIED publication requires a receipt and events.");
            }

            if (kind == TablePublicationKind.ControlChanged &&
                (control == null || _controlEvents.Count == 0 || receipt != null || _events.Count != 0))
            {
                throw new ArgumentException(
                    "A CONTROL_CHANGED publication requires control state/events and no Domain receipt/events.");
            }
        }

        public TablePublicationKind Kind { get; }

        public TableSnapshot Snapshot { get; }

        public IReadOnlyList<TableEventRecord> Events => _events;

        public TableCommandReceipt? Receipt { get; }

        /// <summary>Control authority at/after this publication. Null on pre-P3.3 call sites.</summary>
        public TableControlSnapshot? Control { get; }

        public IReadOnlyList<TableControlEvent> ControlEvents => _controlEvents;
    }

    /// <summary>
    /// A recovery baseline that has already been made durable outside P3.1. P3.4 owns the
    /// checkpoint/lease contract; this type only lets the actor install a verified baseline
    /// serially and republish it to P3.2.
    /// </summary>
    public sealed class TableRecoveryBaseline
    {
        private readonly ReadOnlyCollection<TableEventRecord> _committedEvents;

        public TableRecoveryBaseline(
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            OwnerEpoch ownerEpoch)
            : this(round, stateVersion, lastEventSeq, ownerEpoch, null, null)
        {
        }

        public TableRecoveryBaseline(
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            OwnerEpoch ownerEpoch,
            TableControlRecoveryBaseline? control,
            TableRecoveryTiming? timing,
            long timingVersion = 0L,
            CheatState? cheat = null,
            byte[]? committedFactsPayload = null,
            IReadOnlyList<TableEventRecord>? committedEvents = null,
            bool strictHistory = false)
        {
            Round = round ?? throw new ArgumentNullException(nameof(round));
            StateVersion = stateVersion;
            LastEventSeq = lastEventSeq;
            OwnerEpoch = ownerEpoch;
            Control = control;
            Timing = timing ?? TableRecoveryTiming.None;
            TimingVersion = timingVersion;
            Cheat = cheat;
            StrictHistory = strictHistory;
            CommittedFactsPayload = committedFactsPayload == null
                ? null
                : (byte[])committedFactsPayload.Clone();
            _committedEvents = new ReadOnlyCollection<TableEventRecord>(
                committedEvents == null
                    ? new List<TableEventRecord>()
                    : new List<TableEventRecord>(committedEvents));
            if (Control == null && Timing.Decisions.Count > 0)
            {
                throw new ArgumentException(
                    "Restoring decision timing requires an exact control baseline; otherwise the old control epochs cannot be fenced.",
                    nameof(timing));
            }
        }

        public RoundStateMachine Round { get; }

        public StateVersion StateVersion { get; }

        public EventSeq LastEventSeq { get; }

        public OwnerEpoch OwnerEpoch { get; }

        /// <summary>Exact control authority/checkpoint version; null keeps the current registry (P3.1 legacy path).</summary>
        public TableControlRecoveryBaseline? Control { get; }

        /// <summary>Explicit restored decisions. Empty means all in-flight decisions are abandoned.</summary>
        public TableRecoveryTiming Timing { get; }

        public long TimingVersion { get; }

        /// <summary>Real cheat aggregate rebuilt from persisted ledger entries, when modeled.</summary>
        public CheatState? Cheat { get; }

        /// <summary>Host-owned committed facts persisted with the checkpoint; installed via the sink.</summary>
        public byte[]? CommittedFactsPayload { get; }

        /// <summary>Full committed event history, not a SnapshotEventWindow.</summary>
        public IReadOnlyList<TableEventRecord> CommittedEvents => _committedEvents;

        public bool StrictHistory { get; }
    }

    /// <summary>Tick input. Deadline time always comes from the actor clock, not the client.</summary>
    public sealed class TableTickRequest
    {
        private readonly ReadOnlyCollection<AiDecisionSubmission> _aiSubmissions;
        private readonly ReadOnlyCollection<AiDecision> _aiDecisions;

        public TableTickRequest(IReadOnlyList<AiDecisionSubmission>? aiSubmissions = null)
            : this(aiSubmissions, null)
        {
        }

        /// <summary>
        /// Raw <paramref name="aiSubmissions"/> are the P3.1 legacy batch form and are accepted
        /// only before any recovery generation exists. After a strict recovery, batch AI must
        /// use <paramref name="aiDecisions"/> so the actor can verify the observation object was
        /// captured in the current generation.
        /// </summary>
        public TableTickRequest(
            IReadOnlyList<AiDecisionSubmission>? aiSubmissions,
            IReadOnlyList<AiDecision>? aiDecisions)
        {
            _aiSubmissions = new ReadOnlyCollection<AiDecisionSubmission>(
                aiSubmissions == null
                    ? new List<AiDecisionSubmission>()
                    : new List<AiDecisionSubmission>(aiSubmissions));
            _aiDecisions = new ReadOnlyCollection<AiDecision>(
                aiDecisions == null
                    ? new List<AiDecision>()
                    : new List<AiDecision>(aiDecisions));
        }

        public IReadOnlyList<AiDecisionSubmission> AiSubmissions => _aiSubmissions;

        /// <summary>Actor-source-checked AI results for batch submission.</summary>
        public IReadOnlyList<AiDecision> AiDecisions => _aiDecisions;

        public static TableTickRequest WithAiDecisions(IReadOnlyList<AiDecision> decisions)
        {
            return new TableTickRequest(null, decisions);
        }

        public static TableTickRequest Empty { get; } = new TableTickRequest();
    }
}

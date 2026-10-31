using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using ContractBaseline = Mahjong.Contracts.StreamBaseline;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ContractReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using ContractScope = Mahjong.Contracts.RequestScope;

namespace Mahjong.Client
{
    /// <summary>
    /// Connection/reconnect states. Ready means a full baseline for the current
    /// binding has been applied; it does not mean every sent command has a
    /// definitive receipt.
    /// </summary>
    public enum TableConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Handshaking = 2,
        Synchronizing = 3,
        Ready = 4,
        Reconnecting = 5,
        Faulted = 6,
        Closed = 7
    }

    public sealed class TableSessionOpenRequest
    {
        public TableSessionOpenRequest(string ticket, string clientBuild)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (clientBuild == null)
            {
                throw new ArgumentNullException(nameof(clientBuild));
            }

            Ticket = ticket;
            ClientBuild = clientBuild;
        }

        public string Ticket { get; }

        public string ClientBuild { get; }
    }

    /// <summary>
    /// Server-issued identity and stream binding for one connection. A new
    /// WELCOME creates a new binding; frames carrying an older binding epoch
    /// are stale and must never overwrite the new baseline.
    /// </summary>
    public sealed class TableSessionBinding
    {
        public TableSessionBinding(
            SessionId sessionId,
            RoomId roomId,
            string tableId,
            ProtocolRole role,
            int seatId,
            ConnectionEpoch connectionEpoch,
            OwnerEpoch ownerEpoch,
            BindingEpoch bindingEpoch,
            ContractScope scope,
            ContractBaseline baseline)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (role == ProtocolRole.Player && (seatId < ProtocolConstants.MinSeat || seatId > ProtocolConstants.MaxSeat))
            {
                throw new ArgumentOutOfRangeException(nameof(seatId), seatId, "A player binding needs seat 0..3.");
            }

            if (role != ProtocolRole.Player && seatId != ProtocolConstants.NoSeat)
            {
                throw new ArgumentOutOfRangeException(nameof(seatId), seatId, "Non-player bindings use seat -1.");
            }

            SessionId = sessionId;
            RoomId = roomId;
            TableId = tableId ?? string.Empty;
            Role = role;
            SeatId = seatId;
            ConnectionEpoch = connectionEpoch;
            OwnerEpoch = ownerEpoch;
            BindingEpoch = bindingEpoch;
            Scope = scope;
            Baseline = baseline;
        }

        public SessionId SessionId { get; }

        public RoomId RoomId { get; }

        public string TableId { get; }

        public ProtocolRole Role { get; }

        public int SeatId { get; }

        public ConnectionEpoch ConnectionEpoch { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public BindingEpoch BindingEpoch { get; }

        public ContractScope Scope { get; }

        public ContractBaseline Baseline { get; }
    }

    /// <summary>
    /// Immutable view of an unresolved outbox entry. The original request id,
    /// scope id and canonical business bytes stay unchanged for every lookup
    /// or exact retry.
    /// </summary>
    public sealed class PendingCommandSnapshot
    {
        public PendingCommandSnapshot(
            IdempotencyKey key,
            CommandKind kind,
            string digestSha256,
            int payloadLength,
            bool handedToTransport,
            bool deliveryUnknown,
            bool needsReconciliation,
            ReceiptLookupStatus? lastLookup,
            ContractReceiptStatus? lastReceipt,
            bool queryInFlight)
        {
            Key = key;
            Kind = kind;
            DigestSha256 = digestSha256 ?? string.Empty;
            PayloadLength = payloadLength;
            HandedToTransport = handedToTransport;
            DeliveryUnknown = deliveryUnknown;
            NeedsReconciliation = needsReconciliation;
            LastLookup = lastLookup;
            LastReceipt = lastReceipt;
            QueryInFlight = queryInFlight;
        }

        public IdempotencyKey Key { get; }

        public CommandKind Kind { get; }

        public string RequestId => Key.RequestId.Value;

        public string ScopeId => Key.ScopeId.Value;

        public string DigestSha256 { get; }

        public int PayloadLength { get; }

        public bool HandedToTransport { get; }

        /// <summary>True when a prior process may have sent the command but cannot prove it.</summary>
        public bool DeliveryUnknown { get; }

        public bool NeedsReconciliation { get; }

        public ReceiptLookupStatus? LastLookup { get; }

        public ContractReceiptStatus? LastReceipt { get; }

        public bool QueryInFlight { get; }
    }

    public sealed class CommandSubmission
    {
        public CommandSubmission(PendingCommandSnapshot command, bool sent, bool wasDuplicate)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Sent = sent;
            WasDuplicate = wasDuplicate;
        }

        public PendingCommandSnapshot Command { get; }

        /// <summary>True only when this call handed bytes to the transport.</summary>
        public bool Sent { get; }

        /// <summary>True when the same (scopeId, requestId) and payload already existed.</summary>
        public bool WasDuplicate { get; }
    }

    public sealed class TableSnapshotChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Legacy constructor retained for compatibility with fake sessions and
        /// existing consumers. It publishes no visible-event batch.
        /// </summary>
        public TableSnapshotChangedEventArgs(
            ContractCursor cursor,
            bool isFullBaseline,
            PlayerSnapshot? playerSnapshot,
            PublicSnapshot? publicSnapshot,
            RoomUpdate? roomSnapshot)
            : this(cursor, isFullBaseline, playerSnapshot, publicSnapshot, roomSnapshot, TableVisibleEventBatch.Empty)
        {
        }

        public TableSnapshotChangedEventArgs(
            ContractCursor cursor,
            bool isFullBaseline,
            PlayerSnapshot? playerSnapshot,
            PublicSnapshot? publicSnapshot,
            RoomUpdate? roomSnapshot,
            TableVisibleEventBatch visibleEventBatch)
        {
            Cursor = cursor;
            IsFullBaseline = isFullBaseline;
            PlayerSnapshot = playerSnapshot;
            PublicSnapshot = publicSnapshot;
            RoomSnapshot = roomSnapshot;
            VisibleEventBatch = visibleEventBatch ?? throw new ArgumentNullException(nameof(visibleEventBatch));
        }

        public ContractCursor Cursor { get; }

        public bool IsFullBaseline { get; }

        public PlayerSnapshot? PlayerSnapshot { get; }

        public PublicSnapshot? PublicSnapshot { get; }

        public RoomUpdate? RoomSnapshot { get; }

        /// <summary>
        /// Typed, atomically confirmed visible events for this exact snapshot
        /// publication. Always non-null; the legacy constructor yields
        /// <see cref="TableVisibleEventBatch.Empty"/>.
        /// </summary>
        public TableVisibleEventBatch VisibleEventBatch { get; }

        /// <summary>
        /// Convenience view of <see cref="TableVisibleEventBatch.Events"/>. On a
        /// baseline these events are reconciliation data, not new animation.
        /// Use <see cref="TableVisibleEventBatch.PlayableEvents"/> for the
        /// incremental-only feed.
        /// </summary>
        public IReadOnlyList<TableVisibleEvent> VisibleEvents => VisibleEventBatch.Events;
    }

    public sealed class TableReceiptResolvedEventArgs : EventArgs
    {
        public TableReceiptResolvedEventArgs(PendingCommandSnapshot command, OutboxResolution resolution)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        }

        public PendingCommandSnapshot Command { get; }

        public OutboxResolution Resolution { get; }
    }

    /// <summary>
    /// UI/LocalHost-facing session baseline. The interface deliberately exposes
    /// wire projections and client-local state only; it has no Domain or
    /// TableRuntime types, so Unity and the local host can compile against it.
    ///
    /// Calls are single-threaded at the platform boundary. The platform layer
    /// owns main-thread dispatch of <see cref="SnapshotChanged"/> and
    /// <see cref="ReceiptResolved"/>.
    /// </summary>
    public interface IGameSession
    {
        TableConnectionState State { get; }

        /// <summary>
        /// Optional pre-publish admission gate. It is evaluated immediately
        /// before a SYNC_RESPONSE baseline mutates the cache or enters Ready.
        /// Returning false drops that baseline, so it cannot publish a Ready
        /// snapshot or admit commands. The runner installs a gate that enforces
        /// the current connection attempt's absolute deadline and cancellation;
        /// direct/local callers may leave it null.
        /// </summary>
        Func<CancellationToken, bool>? BaselineAdmissionCheck { get; set; }

        TableSessionBinding? Binding { get; }

        /// <summary>Last applied stream cursor; null before the first baseline.</summary>
        ContractCursor? ConfirmedCursor { get; }

        PlayerSnapshot? PlayerSnapshot { get; }

        PublicSnapshot? PublicSnapshot { get; }

        RoomUpdate? RoomSnapshot { get; }

        IReadOnlyList<PendingCommandSnapshot> PendingCommands { get; }

        int EarlyBufferItemCount { get; }

        long EarlyBufferByteCount { get; }

        long EarlyBufferOverflowCount { get; }

        string? LastError { get; }

        event EventHandler<TableSnapshotChangedEventArgs>? SnapshotChanged;

        event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;

        Task OpenAsync(TableSessionOpenRequest request, CancellationToken cancellationToken);

        /// <summary>Receives and processes exactly one inbound envelope.</summary>
        Task PumpOnceAsync(CancellationToken cancellationToken);

        /// <summary>Marks the connection transiently lost and keeps unresolved outbox entries.</summary>
        Task DisconnectAsync(CancellationToken cancellationToken);

        Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken);

        /// <summary>
        /// Sends RECEIPT_QUERY for every unresolved entry that still needs
        /// reconciliation. It never resends the original command.
        /// </summary>
        Task ReconcilePendingAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Re-sends the exact original request id, scope and canonical bytes.
        /// A caller must reconcile first; an UNKNOWN outcome is never retried
        /// automatically and never receives a new id.
        /// </summary>
        Task<CommandSubmission> RetryPendingAsync(IdempotencyKey key, CancellationToken cancellationToken);

        Task CloseAsync(string reasonCode, CancellationToken cancellationToken);
    }
}

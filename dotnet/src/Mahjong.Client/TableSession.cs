using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using ContractBaseline = Mahjong.Contracts.StreamBaseline;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ContractRole = Mahjong.Contracts.ProtocolRole;
using ContractReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using ContractScope = Mahjong.Contracts.RequestScope;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WireReceipt = Mahjong.Contracts.Wire.Receipt;
using WireScope = Mahjong.Contracts.Wire.RequestScope;

namespace Mahjong.Client
{
    /// <summary>
    /// Early P5.4a client session skeleton.
    ///
    /// It owns the reconnect state machine, the persist-before-send outbox and
    /// the bounded pre-baseline buffer. It deliberately has no WSS, disk,
    /// Unity main-thread or Domain/TableRuntime dependency; those bindings live
    /// in the platform layer or a later milestone.
    /// </summary>
    public sealed class TableSession : IGameSession, ITableSessionFrameIngress
    {
        private readonly ITableChannel _channel;
        private readonly IOutboxStore _outbox;
        private readonly TableSessionOptions _options;
        private readonly TableConnectionStateMachine _connection = new TableConnectionStateMachine();
        private readonly Dictionary<IdempotencyKey, TrackedCommand> _commands = new Dictionary<IdempotencyKey, TrackedCommand>();
        private readonly Dictionary<string, IdempotencyKey> _receiptQueries = new Dictionary<string, IdempotencyKey>(StringComparer.Ordinal);

        private EarlyPushBuffer _earlyBuffer;
        private TableSessionBinding? _binding;
        private ContractScope? _scope;
        private ContractCursor? _confirmedCursor;
        private PlayerSnapshot? _playerSnapshot;
        private PublicSnapshot? _publicSnapshot;
        private RoomUpdate? _roomSnapshot;
        private ServerLimits? _serverLimits;
        private bool _syncInFlight;
        private string? _activeSyncCorrelationId;
        private ulong? _confirmedBindingEpoch;
        private string? _confirmedBindingSessionId;
        private ulong? _confirmedStateVersion;
        private ulong? _confirmedEventSeq;
        private bool _outboxLoaded;
        private ulong _localBindingEpoch;
        private long _syncCounter;
        private long _queryCounter;
        private long _helloCounter;
        private long _earlyOverflowCount;
        private string? _lastError;

        public TableSession(ITableChannel channel, IOutboxStore outbox, TableSessionOptions? options = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _options = options ?? new TableSessionOptions();
            _options.Validate();
            _earlyBuffer = new EarlyPushBuffer(_options.MaxEarlyBufferItems, _options.MaxEarlyBufferBytes);
        }

        public TableConnectionState State => _connection.State;

        public Func<CancellationToken, bool>? BaselineAdmissionCheck { get; set; }

        public TableSessionBinding? Binding => _binding;

        public ContractCursor? ConfirmedCursor => _confirmedCursor;

        public PlayerSnapshot? PlayerSnapshot => _playerSnapshot;

        public PublicSnapshot? PublicSnapshot => _publicSnapshot;

        public RoomUpdate? RoomSnapshot => _roomSnapshot;

        public IReadOnlyList<PendingCommandSnapshot> PendingCommands
        {
            get
            {
                var result = new List<PendingCommandSnapshot>(_commands.Count);
                foreach (TrackedCommand tracked in _commands.Values.OrderBy(c => c.Entry.ScopeId.Value, StringComparer.Ordinal).ThenBy(c => c.Entry.RequestId.Value, StringComparer.Ordinal))
                {
                    result.Add(ToSnapshot(tracked));
                }

                return result;
            }
        }

        public int EarlyBufferItemCount => _earlyBuffer.Count;

        public long EarlyBufferByteCount => _earlyBuffer.ByteCount;

        public long EarlyBufferOverflowCount => _earlyOverflowCount;

        public string? LastError => _lastError;

        public event EventHandler<TableSnapshotChangedEventArgs>? SnapshotChanged;

        public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;

        public async Task OpenAsync(TableSessionOpenRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (State == TableConnectionState.Connecting ||
                State == TableConnectionState.Handshaking ||
                State == TableConnectionState.Synchronizing ||
                State == TableConnectionState.Ready)
            {
                throw new InvalidOperationException("Session is already open or opening (state " + State + ").");
            }

            if (State == TableConnectionState.Closed || State == TableConnectionState.Faulted)
            {
                throw new InvalidOperationException("Session is " + State + " and cannot be reopened; create a new session.");
            }

            if (!_outboxLoaded)
            {
                await LoadOutboxAsync(cancellationToken).ConfigureAwait(false);
                _outboxLoaded = true;
            }

            // A transient failure before the first WELCOME leaves us in
            // Reconnecting with no binding. Reconnectability must therefore be
            // derived from the connection state, not from _binding.
            bool transportReconnect = State == TableConnectionState.Reconnecting;
            bool resumeIdentity = _binding != null;
            _connection.Fire(transportReconnect ? TableConnectionTrigger.BeginReconnect : TableConnectionTrigger.BeginConnect);

            try
            {
                await _channel.OpenAsync(
                    new TableChannelConnectRequest(
                        request.Ticket,
                        request.ClientBuild,
                        resumeIdentity && _binding != null ? _binding.SessionId.Value : null),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled attempt must not strand the machine in
                // Connecting/Reconnecting; the caller may retry OpenAsync.
                if (State == TableConnectionState.Connecting || State == TableConnectionState.Reconnecting)
                {
                    HandleTransportFailure(new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false));
                }

                throw;
            }
            catch (Exception ex)
            {
                HandleTransportFailure(new TableChannelClosedException(ProtocolErrorCode.InternalError, false, ex));
                throw;
            }

            _localBindingEpoch = checked(_localBindingEpoch + 1);
            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _receiptQueries.Clear();
            ResetPendingCommandLookups(markUnknownIfHanded: true);
            _earlyBuffer.Clear();
            _earlyBuffer = new EarlyPushBuffer(_options.MaxEarlyBufferItems, _options.MaxEarlyBufferBytes);
            _lastError = null;

            string correlation = "hello-" + NextCounter(ref _helloCounter);
            Envelope hello = ClientWireMapper.NewHello(
                request.Ticket,
                request.ClientBuild,
                resumeIdentity,
                resumeIdentity && _binding != null ? _binding.SessionId.Value : null,
                _localBindingEpoch,
                correlation);

            try
            {
                await SendEnvelopeAsync(hello, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (State == TableConnectionState.Connecting || State == TableConnectionState.Reconnecting)
                {
                    HandleTransportFailure(new TableChannelClosedException(ProtocolErrorCode.InternalError, false));
                }

                throw;
            }

            _connection.Fire(TableConnectionTrigger.TransportOpened);
        }

        public async Task PumpOnceAsync(CancellationToken cancellationToken)
        {
            if (State == TableConnectionState.Closed || State == TableConnectionState.Faulted)
            {
                throw new InvalidOperationException("Session is " + State + " and cannot pump.");
            }

            ReadOnlyMemory<byte> frame;
            try
            {
                frame = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException ex)
            {
                HandleTransportFailure(ex);
                return;
            }

            await ApplyFrameAsyncCore(frame, cancellationToken).ConfigureAwait(false);
        }

        async Task<ReadOnlyMemory<byte>> ITableSessionFrameIngress.ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            return await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task ITableSessionFrameIngress.ApplyFrameAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken)
        {
            // A late frame from a receive that ignored cancellation must not
            // revive a terminal session.
            if (State == TableConnectionState.Closed || State == TableConnectionState.Faulted)
            {
                return;
            }

            await ApplyFrameAsyncCore(frame, cancellationToken).ConfigureAwait(false);
        }

        Task ITableSessionFrameIngress.ApplyReceiveFaultAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception == null)
            {
                return Task.CompletedTask;
            }

            if (exception is TableChannelClosedException closed)
            {
                HandleTransportFailure(closed);
                return Task.CompletedTask;
            }

            Fault(
                ProtocolErrorCode.InternalError,
                "Channel receive failed: " + exception.GetType().Name + ": " + exception.Message);
            return Task.CompletedTask;
        }

        private async Task ApplyFrameAsyncCore(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            byte[] raw = frame.ToArray();
            WireDecodeResult decoded;
            try
            {
                decoded = WireCodec.Decode(raw);
            }
            catch (WireProtocolException ex)
            {
                Fault(ex.Code, ex.Message);
                return;
            }

            if (!decoded.IsOk || decoded.Envelope == null)
            {
                Fault(decoded.ErrorCode, "Inbound envelope rejected with status " + decoded.Status + ".");
                return;
            }

            Envelope envelope = decoded.Envelope;
            switch (envelope.BodyCase)
            {
                case Envelope.BodyOneofCase.Welcome:
                    await HandleWelcomeAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;
                case Envelope.BodyOneofCase.SyncResponse:
                    await HandleSyncResponseAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;
                case Envelope.BodyOneofCase.PlayerUpdate:
                case Envelope.BodyOneofCase.PublicUpdate:
                    await HandlePushAsync(envelope, raw.Length, cancellationToken).ConfigureAwait(false);
                    break;
                case Envelope.BodyOneofCase.Receipt:
                    await HandleReceiptAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;
                case Envelope.BodyOneofCase.ReceiptLookup:
                    await HandleReceiptLookupAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;
                case Envelope.BodyOneofCase.Close:
                    HandleInboundClose(envelope.Close);
                    break;
                case Envelope.BodyOneofCase.Error:
                    HandleInboundError(envelope.Error);
                    break;
                case Envelope.BodyOneofCase.Heartbeat:
                    // Heartbeat liveness belongs to the transport; this skeleton
                    // intentionally has no timer and never advances state on it.
                    break;
                default:
                    // Unknown or irrelevant message families are ignored; the
                    // wire codec already guarded the frozen envelope pairing.
                    break;
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (State == TableConnectionState.Closed || State == TableConnectionState.Faulted)
            {
                return;
            }

            try
            {
                await _channel.CloseAsync(
                    new TableChannelCloseReason(ProtocolErrorCode.LocalClosed, false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException)
            {
                // Already closed; mark the session transiently lost below.
            }

            HandleTransportFailure(new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false));
        }

        public async Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (State != TableConnectionState.Ready || _binding == null || _scope == null)
            {
                throw new InvalidOperationException("A command can be submitted only from a ready, bound session.");
            }

            // Role admission happens before persistence and before anything is
            // handed to the transport. A spectator must never durably enqueue a
            // player seat action.
            EnsureCommandAdmitted(command.Kind);

            if (!command.ScopeId.HasValue || command.ScopeId.Value.IsEmpty)
            {
                throw new ArgumentException("Only scoped side-effecting commands are accepted.", nameof(command));
            }

            if (command.ScopeId.Value != _scope.ScopeId)
            {
                throw new ArgumentException("The command scope id does not match the current server-issued scope.", nameof(command));
            }

            var entry = new OutboxEntry(command, _scope, _binding.SessionId, _binding.RoomId);
            if (_commands.TryGetValue(entry.Key, out TrackedCommand? existing))
            {
                if (!existing.Entry.Matches(command))
                {
                    throw new InvalidOperationException(
                        "Request id " + entry.RequestId + " already exists with different canonical bytes or metadata.");
                }

                return new CommandSubmission(ToSnapshot(existing), false, true);
            }

            // Persist first. A crash after this line may leave an unresolved
            // entry, but a crash before it can never leave a sent command
            // without a durable record.
            await _outbox.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
            var tracked = new TrackedCommand(entry);
            _commands.Add(entry.Key, tracked);

            bool sent = false;
            try
            {
                sent = await SendTrackedAsync(tracked, cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException)
            {
                // The entry is durable and remains unresolved. The caller can
                // reconnect and reconcile; it must not create a new request id.
                return new CommandSubmission(ToSnapshot(tracked), false, false);
            }

            return new CommandSubmission(ToSnapshot(tracked), sent, false);
        }

        public async Task ReconcilePendingAsync(CancellationToken cancellationToken)
        {
            if (State != TableConnectionState.Ready)
            {
                throw new InvalidOperationException("Receipt reconciliation requires a ready baseline.");
            }

            await SendQueriesAsync(resetPreviousLookup: true, cancellationToken).ConfigureAwait(false);
        }

        public async Task<CommandSubmission> RetryPendingAsync(IdempotencyKey key, CancellationToken cancellationToken)
        {
            if (State != TableConnectionState.Ready)
            {
                throw new InvalidOperationException("Retry requires a ready baseline.");
            }

            if (!_commands.TryGetValue(key, out TrackedCommand? tracked))
            {
                throw new InvalidOperationException("Outbox key " + key + " is not unresolved.");
            }

            // A player outbox recovered by a spectator session stays queryable
            // for reconciliation, but its seat action must never be resent.
            EnsureCommandAdmitted(tracked.Entry.Kind);

            if (tracked.QueryInFlight)
            {
                throw new InvalidOperationException("A receipt lookup is still in flight; wait for its result.");
            }

            if (!tracked.LastLookup.HasValue)
            {
                throw new InvalidOperationException("Reconcile the unknown outcome before retrying; no request id is ever regenerated.");
            }

            // Same request id, same scope and the exact retained payload bytes.
            bool sent = await SendTrackedAsync(tracked, cancellationToken).ConfigureAwait(false);
            tracked.LastLookup = null;
            tracked.LastReceipt = null;
            return new CommandSubmission(ToSnapshot(tracked), sent, false);
        }

        public async Task CloseAsync(string reasonCode, CancellationToken cancellationToken)
        {
            if (State == TableConnectionState.Closed)
            {
                return;
            }

            try
            {
                await _channel.CloseAsync(
                    new TableChannelCloseReason(string.IsNullOrEmpty(reasonCode) ? ProtocolErrorCode.LocalClosed : reasonCode, true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException)
            {
                // Close is best effort.
            }

            _earlyBuffer.Clear();
            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _receiptQueries.Clear();
            ResetPendingCommandLookups(markUnknownIfHanded: true);

            _connection.Fire(TableConnectionTrigger.Close);
        }

        private async Task LoadOutboxAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<OutboxEntry> entries = await _outbox.LoadUnresolvedAsync(cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < entries.Count; i++)
            {
                OutboxEntry entry = entries[i];
                if (_commands.ContainsKey(entry.Key))
                {
                    continue;
                }

                // A freshly started process cannot know whether this entry was
                // sent, so it is treated as an unknown outcome and must be
                // reconciled before any exact retry.
                var tracked = new TrackedCommand(entry)
                {
                    HandedToTransport = true,
                    DeliveryUnknown = true,
                    NeedsReconciliation = true
                };
                _commands.Add(entry.Key, tracked);
            }
        }

        private async Task HandleWelcomeAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (State != TableConnectionState.Handshaking)
            {
                return;
            }

            Welcome? welcome = envelope.Welcome;
            if (welcome == null)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME body is missing.");
                return;
            }

            if (welcome.ProtocolMajor != ProtocolConstants.Major ||
                welcome.ProtocolMinor > ProtocolConstants.Minor ||
                welcome.RulesetVersion != ProtocolConstants.RulesetVersion ||
                welcome.ProjectionVersion != ProtocolConstants.ProjectionVersion)
            {
                Fault(ProtocolErrorCode.ProtocolMajorUnsupported, "WELCOME versions do not match the frozen protocol.");
                return;
            }

            if (string.IsNullOrEmpty(welcome.SessionId) || string.IsNullOrEmpty(welcome.RoomId))
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME must carry session_id and room_id.");
                return;
            }

            if (!string.IsNullOrEmpty(envelope.SessionId) &&
                !string.Equals(envelope.SessionId, welcome.SessionId, StringComparison.Ordinal))
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME session id does not match the envelope.");
                return;
            }

            if (!string.IsNullOrEmpty(envelope.RoomId) &&
                !string.Equals(envelope.RoomId, welcome.RoomId, StringComparison.Ordinal))
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME room id does not match the envelope.");
                return;
            }

            if (welcome.Scope == null || welcome.Baseline == null)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME must carry a scope and a baseline.");
                return;
            }

            ContractRole role;
            ContractScope scope;
            try
            {
                role = ClientWireMapper.ToContractRole(welcome.Role);
                scope = ClientWireMapper.ToContract(welcome.Scope);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME scope or role is invalid: " + ex.Message);
                return;
            }

            if (scope.RoomId.Value != welcome.RoomId)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME scope room does not match the welcome room.");
                return;
            }

            if (scope.Role != role)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME role does not match the issued scope role.");
                return;
            }

            ulong bindingEpoch = welcome.BindingEpoch > 0 ? welcome.BindingEpoch : _localBindingEpoch;
            if (bindingEpoch < _localBindingEpoch)
            {
                bindingEpoch = _localBindingEpoch;
            }

            if (bindingEpoch == 0)
            {
                bindingEpoch = 1;
            }

            WireCursor baselineCursor = welcome.Baseline.Cursor ?? new WireCursor();
            try
            {
                _binding = new TableSessionBinding(
                    new SessionId(welcome.SessionId),
                    new RoomId(welcome.RoomId),
                    welcome.TableId,
                    role,
                    welcome.SeatId,
                    new ConnectionEpoch(welcome.ConnectionEpoch),
                    new OwnerEpoch(welcome.OwnerEpoch),
                    new BindingEpoch(bindingEpoch),
                    scope,
                    new ContractBaseline(
                        new StateVersion(welcome.Baseline.StateVersion),
                        new EventSeq(welcome.Baseline.EventSeq),
                        ClientWireMapper.ToContract(baselineCursor)));
            }
            catch (ArgumentException ex)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "WELCOME binding is invalid: " + ex.Message);
                return;
            }

            _scope = scope;
            _serverLimits = welcome.Limits;
            _earlyBuffer = CreateEarlyBuffer(_serverLimits);
            _earlyBuffer.Clear();
            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _receiptQueries.Clear();
            // Lookup replies from the previous connection can no longer arrive;
            // resolve them as unknown and let the new baseline re-query.
            ResetPendingCommandLookups(markUnknownIfHanded: true);
            _lastError = null;
            _connection.Fire(TableConnectionTrigger.WelcomeAccepted);
            await RequestFullBaselineAsync("CLIENT_INITIAL_BASELINE", force: false, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleSyncResponseAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (!MatchesBinding(envelope) || _binding == null)
            {
                return;
            }

            if (State != TableConnectionState.Synchronizing || !_syncInFlight)
            {
                // A late duplicate baseline must not jump a newer cursor backwards.
                return;
            }

            // A SYNC_RESPONSE is only valid for the request that is currently
            // in flight. This rejects an old correlation still travelling after
            // an overflow-forced request replaced it, and it refuses anonymous
            // responses that cannot be associated with any request at all.
            if (string.IsNullOrEmpty(envelope.CorrelationId) ||
                string.IsNullOrEmpty(_activeSyncCorrelationId) ||
                !string.Equals(envelope.CorrelationId, _activeSyncCorrelationId, StringComparison.Ordinal))
            {
                return;
            }

            SyncResponse? response = envelope.SyncResponse;
            if (response == null || !response.IsFullSnapshot)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE must contain a full baseline.");
                return;
            }

            SyncTarget expectedTarget = ClientWireMapper.ToSyncTarget(_binding.Role);
            if (response.Target != expectedTarget)
            {
                Fault(ProtocolErrorCode.PrivateDataInPublicStream, "SYNC_RESPONSE target does not match the bound role.");
                return;
            }

            ContractCursor? cursor = null;
            PlayerSnapshot? player = null;
            PublicSnapshot? publicSnapshot = null;
            RoomUpdate? room = null;
            ulong? baselineStateVersion = null;
            ulong? baselineEventSeq = null;

            switch (response.BaselineCase)
            {
                case SyncResponse.BaselineOneofCase.Player:
                    if (response.Player == null || response.Player.Cursor == null)
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "Player SYNC_RESPONSE is missing its cursor.");
                        return;
                    }

                    if (!IsPlayerSnapshotConsistent(response.Player, response.Player.Cursor))
                    {
                        // A different session/seat is another stream's private
                        // baseline. Drop it and keep waiting for our own reply.
                        return;
                    }

                    ContractCursor playerCursor = ClientWireMapper.ToContract(response.Player.Cursor);
                    if (!VersionsAgree(response.StateVersion, response.EventSeq, response.Player.StateVersion, response.Player.EventSeq))
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE version fields disagree with the player snapshot.");
                        return;
                    }

                    if (envelope.Cursor != null && !CursorMatches(envelope.Cursor, playerCursor))
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE envelope cursor disagrees with the player snapshot.");
                        return;
                    }

                    cursor = playerCursor;
                    player = response.Player;
                    baselineStateVersion = response.Player.StateVersion;
                    baselineEventSeq = response.Player.EventSeq;
                    break;
                case SyncResponse.BaselineOneofCase.Public:
                    if (response.Public == null || response.Public.Cursor == null)
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "Public SYNC_RESPONSE is missing its cursor.");
                        return;
                    }

                    if (!IsPublicSnapshotConsistent(response.Public, response.Public.Cursor))
                    {
                        return;
                    }

                    ContractCursor publicCursor = ClientWireMapper.ToContract(response.Public.Cursor);
                    if (!VersionsAgree(response.StateVersion, response.EventSeq, response.Public.StateVersion, response.Public.EventSeq))
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE version fields disagree with the public snapshot.");
                        return;
                    }

                    if (envelope.Cursor != null && !CursorMatches(envelope.Cursor, publicCursor))
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE envelope cursor disagrees with the public snapshot.");
                        return;
                    }

                    cursor = publicCursor;
                    publicSnapshot = response.Public;
                    baselineStateVersion = response.Public.StateVersion;
                    baselineEventSeq = response.Public.EventSeq;
                    break;
                case SyncResponse.BaselineOneofCase.Room:
                    if (response.Room == null)
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "Room SYNC_RESPONSE is missing its room update.");
                        return;
                    }

                    // Room updates do not carry a per-stream cursor; they are
                    // not used for table PUSH ordering.
                    room = response.Room;
                    break;
                default:
                    Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE baseline is missing.");
                    return;
            }

            if (cursor.HasValue)
            {
                // Binding identity for a floor is (session_id, binding_epoch).
                // A new WELCOME binding may legitimately reset the stream and
                // advertises the new lower bound; within one binding a baseline
                // can only move the cursor and the authoritative state/event
                // versions forward. The two version families are independent:
                // state_version is not stream_version and event_seq is not a
                // projection counter, so only like-for-like floors apply.
                ContractCursor cursorFloor = CurrentBaselineFloor();
                if (!CursorAtOrAbove(cursor.Value, cursorFloor))
                {
                    return;
                }

                if (!baselineStateVersion.HasValue || !baselineEventSeq.HasValue)
                {
                    Fault(ProtocolErrorCode.MalformedMessage, "SYNC_RESPONSE baseline is missing its version fields.");
                    return;
                }

                if (baselineStateVersion.Value < CurrentStateVersionFloor() ||
                    baselineEventSeq.Value < CurrentEventSeqFloor())
                {
                    return;
                }
            }

            // Everything above validated first; check before doing any work.
            if (BaselineAdmissionCheck != null && !BaselineAdmissionCheck(cancellationToken))
            {
                _syncInFlight = false;
                _activeSyncCorrelationId = null;
                return;
            }

            // Build/validate candidates only in locals above; the cache and the
            // confirmed cursor/version/binding are committed as one unit only
            // after the final admission check. A second-check rejection must
            // leave an existing accepted baseline untouched, not clear it.
            if (BaselineAdmissionCheck != null && !BaselineAdmissionCheck(cancellationToken))
            {
                _syncInFlight = false;
                _activeSyncCorrelationId = null;
                return;
            }

            // Final admission passed: commit snapshot, confirmed cursor and
            // versions together, then publish Ready/SnapshotChanged once.
            if (player != null)
            {
                _playerSnapshot = player;
                _confirmedStateVersion = player.StateVersion;
                _confirmedEventSeq = player.EventSeq;
            }

            if (publicSnapshot != null)
            {
                _publicSnapshot = publicSnapshot;
                _confirmedStateVersion = publicSnapshot.StateVersion;
                _confirmedEventSeq = publicSnapshot.EventSeq;
            }

            if (room != null)
            {
                _roomSnapshot = room;
            }

            if (cursor.HasValue)
            {
                _confirmedCursor = cursor;
                _confirmedBindingEpoch = _binding.BindingEpoch.Value;
                _confirmedBindingSessionId = _binding.SessionId.Value;
            }

            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _connection.Fire(TableConnectionTrigger.BaselineApplied);
            RaiseSnapshotChanged(cursor ?? ContractCursor.Empty, true);

            if (!cursor.HasValue)
            {
                // Lobby/room baselines carry no stream cursor.
                return;
            }

            await DrainEarlyBufferAsync(cancellationToken).ConfigureAwait(false);
            if (State == TableConnectionState.Ready)
            {
                // Baseline first, then reconcile unresolved side effects.
                await SendQueriesAsync(resetPreviousLookup: false, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task HandlePushAsync(Envelope envelope, int encodedSize, CancellationToken cancellationToken)
        {
            if (!MatchesBinding(envelope))
            {
                return;
            }

            if (State != TableConnectionState.Ready)
            {
                if (State == TableConnectionState.Synchronizing && !_earlyBuffer.TryAdd(envelope, encodedSize))
                {
                    _earlyOverflowCount++;
                    _earlyBuffer.Clear();
                    _connection.Fire(TableConnectionTrigger.ResyncRequired);
                    await RequestFullBaselineAsync("EARLY_BUFFER_OVERFLOW", force: true, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!TryReadPush(envelope, out ContractCursor from, out ContractCursor cursor, out bool isFullSnapshot, out PlayerSnapshot? player, out PublicSnapshot? publicSnapshot))
            {
                Fault(ProtocolErrorCode.MalformedMessage, "PUSH is missing its cursor or snapshot.");
                return;
            }

            CursorRelation relation = ContractCursor.Classify(_confirmedCursor ?? ContractCursor.Empty, from, cursor, isFullSnapshot);
            switch (relation)
            {
                case CursorRelation.Baseline:
                case CursorRelation.Advance:
                case CursorRelation.Refresh:
                    // A full snapshot flag selects reset semantics inside
                    // Contracts.StreamCursor.Classify, but it must not bypass
                    // this binding's monotonic floors. The authoritative
                    // state/event versions move independently of the cursor,
                    // so every accepted push has to satisfy both.
                    if (!CursorAtOrAbove(cursor, CurrentBaselineFloor()) ||
                        !PushVersionsAtOrAboveFloor(player, publicSnapshot))
                    {
                        return;
                    }

                    ApplyPush(cursor, isFullSnapshot, player, publicSnapshot);
                    return;
                case CursorRelation.Stale:
                    return;
                case CursorRelation.Gap:
                    if (!_earlyBuffer.TryAdd(envelope, encodedSize))
                    {
                        _earlyOverflowCount++;
                        _earlyBuffer.Clear();
                        _connection.Fire(TableConnectionTrigger.ResyncRequired);
                        await RequestFullBaselineAsync("EARLY_BUFFER_OVERFLOW", force: true, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    _connection.Fire(TableConnectionTrigger.ResyncRequired);
                    await RequestFullBaselineAsync("STREAM_GAP", force: false, cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    return;
            }
        }

        private async Task DrainEarlyBufferAsync(CancellationToken cancellationToken)
        {
            while (State == TableConnectionState.Ready && _earlyBuffer.Count > 0)
            {
                // Drop frames that can never become applicable. A full baseline
                // is not automatically stale: it may legitimately cross a
                // cursor gap, so it must be judged by the same binding's
                // cursor/version floors instead of its full flag. Old,
                // duplicate and malformed baselines are discarded here; a
                // baseline that is newer in cursor but regresses an
                // authoritative version is left for the recovery path below,
                // which refuses to apply it and asks for a fresh correlated
                // baseline.
                for (int i = _earlyBuffer.Count - 1; i >= 0; i--)
                {
                    Envelope buffered = _earlyBuffer.Items[i].Envelope;
                    if (!MatchesBinding(buffered) ||
                        !TryReadPush(buffered, out _, out ContractCursor bufferedCursor, out bool bufferedFull, out _, out _))
                    {
                        _earlyBuffer.RemoveAt(i);
                        continue;
                    }

                    if (bufferedFull)
                    {
                        // A full snapshot is applicable only when it advances at
                        // least one cursor component while staying at or above
                        // the confirmed floors. Equality is a duplicate, not a
                        // newer baseline.
                        if (!CursorAtOrAbove(bufferedCursor, CurrentBaselineFloor()) ||
                            bufferedCursor == _confirmedCursor!.Value)
                        {
                            _earlyBuffer.RemoveAt(i);
                        }

                        continue;
                    }

                    if (bufferedCursor.ProjectionSeq.Value <= _confirmedCursor!.Value.ProjectionSeq.Value)
                    {
                        _earlyBuffer.RemoveAt(i);
                    }
                }

                if (_earlyBuffer.Count == 0)
                {
                    return;
                }

                int selected = -1;
                for (int i = 0; i < _earlyBuffer.Count; i++)
                {
                    Envelope buffered = _earlyBuffer.Items[i].Envelope;
                    if (!TryReadPush(buffered, out ContractCursor from, out ContractCursor cursor, out bool isFull, out PlayerSnapshot? bufferedPlayer, out PublicSnapshot? bufferedPublic))
                    {
                        continue;
                    }

                    CursorRelation relation = ContractCursor.Classify(_confirmedCursor!.Value, from, cursor, isFull);
                    if ((relation == CursorRelation.Baseline ||
                         relation == CursorRelation.Advance ||
                         relation == CursorRelation.Refresh) &&
                        CursorAtOrAbove(cursor, CurrentBaselineFloor()) &&
                        PushVersionsAtOrAboveFloor(bufferedPlayer, bufferedPublic))
                    {
                        selected = i;
                        _earlyBuffer.RemoveAt(i);
                        ApplyPush(cursor, isFull, bufferedPlayer, bufferedPublic);
                        break;
                    }
                }

                if (selected >= 0)
                {
                    continue;
                }

                // No buffered frame can continue the cursor. A full baseline is
                // the only safe recovery; do not guess or apply out of order.
                _earlyBuffer.Clear();
                _connection.Fire(TableConnectionTrigger.ResyncRequired);
                await RequestFullBaselineAsync("EARLY_BUFFER_GAP", force: true, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        private void ApplyPush(ContractCursor cursor, bool isFullSnapshot, PlayerSnapshot? player, PublicSnapshot? publicSnapshot)
        {
            if (player != null)
            {
                _playerSnapshot = player;
                _confirmedStateVersion = player.StateVersion;
                _confirmedEventSeq = player.EventSeq;
            }

            if (publicSnapshot != null)
            {
                _publicSnapshot = publicSnapshot;
                _confirmedStateVersion = publicSnapshot.StateVersion;
                _confirmedEventSeq = publicSnapshot.EventSeq;
            }

            _confirmedCursor = cursor;
            if (_binding != null)
            {
                _confirmedBindingEpoch = _binding.BindingEpoch.Value;
                _confirmedBindingSessionId = _binding.SessionId.Value;
            }

            RaiseSnapshotChanged(cursor, isFullSnapshot);
        }

        private bool TryReadPush(
            Envelope envelope,
            out ContractCursor from,
            out ContractCursor cursor,
            out bool isFullSnapshot,
            out PlayerSnapshot? player,
            out PublicSnapshot? publicSnapshot)
        {
            from = ContractCursor.Empty;
            cursor = ContractCursor.Empty;
            isFullSnapshot = false;
            player = null;
            publicSnapshot = null;

            switch (envelope.BodyCase)
            {
                case Envelope.BodyOneofCase.PlayerUpdate:
                {
                    PlayerUpdate? update = envelope.PlayerUpdate;
                    if (update == null || update.Cursor == null || update.Snapshot == null || _binding == null || _binding.Role != ContractRole.Player)
                    {
                        return false;
                    }

                    cursor = ClientWireMapper.ToContract(update.Cursor);
                    if (!IsPlayerSnapshotConsistent(update.Snapshot, update.Cursor))
                    {
                        return false;
                    }

                    from = update.FromCursor == null ? ContractCursor.Empty : ClientWireMapper.ToContract(update.FromCursor);
                    isFullSnapshot = update.IsFullSnapshot;
                    player = update.Snapshot;
                    break;
                }

                case Envelope.BodyOneofCase.PublicUpdate:
                {
                    PublicUpdate? update = envelope.PublicUpdate;
                    if (update == null || update.Cursor == null || update.Snapshot == null || _binding == null || _binding.Role != ContractRole.Spectator)
                    {
                        return false;
                    }

                    cursor = ClientWireMapper.ToContract(update.Cursor);
                    if (!IsPublicSnapshotConsistent(update.Snapshot, update.Cursor))
                    {
                        return false;
                    }

                    from = update.FromCursor == null ? ContractCursor.Empty : ClientWireMapper.ToContract(update.FromCursor);
                    isFullSnapshot = update.IsFullSnapshot;
                    publicSnapshot = update.Snapshot;
                    break;
                }

                default:
                    return false;
            }

            if (envelope.Cursor != null && !CursorMatches(envelope.Cursor, cursor))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Cursor-floor contract. A binding identity is exactly
        /// (session_id, binding_epoch).
        ///
        /// 1. A WELCOME creates/refreshes that identity; until a baseline is
        ///    applied for it, the cursor advertised by WELCOME is the floor.
        ///    A server may therefore reset a stream only by issuing a new
        ///    binding (a new session id or binding epoch).
        /// 2. Inside one identity, a SYNC_RESPONSE baseline whose cursor is
        ///    below the confirmed cursor is stale and is ignored. It can never
        ///    roll the cache back.
        /// 3. A SYNC_RESPONSE also has to match the correlation of the request
        ///    currently in flight; a response to a superseded request (for
        ///    example after an early-buffer overflow forced a newer request)
        ///    is ignored even if its cursor would otherwise be acceptable.
        /// </summary>
        private ContractCursor CurrentBaselineFloor()
        {
            if (_binding == null)
            {
                return ContractCursor.Empty;
            }

            if (HasConfirmedBinding())
            {
                return _confirmedCursor!.Value;
            }

            return _binding.Baseline.Cursor;
        }

        /// <summary>
        /// The confirmed binding is exactly (session_id, binding_epoch). The
        /// authoritative state/event versions are scoped to the same identity:
        /// a new WELCOME binding may advertise lower counters and thereby
        /// reset them, but inside one binding they can only advance.
        /// </summary>
        private bool HasConfirmedBinding()
        {
            return _binding != null &&
                   _confirmedCursor.HasValue &&
                   _confirmedBindingEpoch == _binding.BindingEpoch.Value &&
                   string.Equals(_confirmedBindingSessionId, _binding.SessionId.Value, StringComparison.Ordinal);
        }

        private ulong CurrentStateVersionFloor()
        {
            if (_binding == null)
            {
                return 0;
            }

            if (HasConfirmedBinding() && _confirmedStateVersion.HasValue)
            {
                return _confirmedStateVersion.Value;
            }

            return _binding.Baseline.StateVersion.Value;
        }

        private ulong CurrentEventSeqFloor()
        {
            if (_binding == null)
            {
                return 0;
            }

            if (HasConfirmedBinding() && _confirmedEventSeq.HasValue)
            {
                return _confirmedEventSeq.Value;
            }

            return _binding.Baseline.EventSeq.Value;
        }

        private static bool CursorAtOrAbove(ContractCursor cursor, ContractCursor floor)
        {
            return cursor.StreamVersion.Value >= floor.StreamVersion.Value &&
                   cursor.ProjectionSeq.Value >= floor.ProjectionSeq.Value;
        }

        private bool PushVersionsAtOrAboveFloor(PlayerSnapshot? player, PublicSnapshot? publicSnapshot)
        {
            ulong stateVersion;
            ulong eventSeq;
            if (player != null)
            {
                stateVersion = player.StateVersion;
                eventSeq = player.EventSeq;
            }
            else if (publicSnapshot != null)
            {
                stateVersion = publicSnapshot.StateVersion;
                eventSeq = publicSnapshot.EventSeq;
            }
            else
            {
                return false;
            }

            return stateVersion >= CurrentStateVersionFloor() &&
                   eventSeq >= CurrentEventSeqFloor();
        }

        private static bool CursorMatches(WireCursor wire, ContractCursor cursor)
        {
            return wire.StreamVersion == cursor.StreamVersion.Value &&
                   wire.ProjectionSeq == cursor.ProjectionSeq.Value;
        }

        private static bool VersionsAgree(
            ulong responseStateVersion,
            ulong responseEventSeq,
            ulong snapshotStateVersion,
            ulong snapshotEventSeq)
        {
            // The schema declares all four fields as non-optional uint64. Zero
            // is therefore a real value, not "field omitted": the response and
            // its authoritative snapshot must carry the same numbers.
            return responseStateVersion == snapshotStateVersion &&
                   responseEventSeq == snapshotEventSeq;
        }

        private bool IsPlayerSnapshotConsistent(PlayerSnapshot snapshot, WireCursor expectedCursor)
        {
            if (_binding == null || snapshot.Cursor == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(snapshot.SessionId) ||
                !string.Equals(snapshot.SessionId, _binding.SessionId.Value, StringComparison.Ordinal))
            {
                return false;
            }

            if (!CursorMatches(expectedCursor, ClientWireMapper.ToContract(snapshot.Cursor)))
            {
                return false;
            }

            // Private data is addressed to exactly one seat. A snapshot for a
            // different seat must never reach this binding's cache.
            return snapshot.PrivateView != null && snapshot.PrivateView.Seat == _binding.SeatId;
        }

        private bool IsPublicSnapshotConsistent(PublicSnapshot snapshot, WireCursor expectedCursor)
        {
            if (_binding == null || snapshot.Cursor == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(snapshot.SessionId) ||
                !string.Equals(snapshot.SessionId, _binding.SessionId.Value, StringComparison.Ordinal))
            {
                return false;
            }

            return CursorMatches(expectedCursor, ClientWireMapper.ToContract(snapshot.Cursor));
        }

        private async Task HandleReceiptAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            WireReceipt? receipt = envelope.Receipt;
            if (receipt == null || string.IsNullOrEmpty(envelope.RequestId) || envelope.Scope == null)
            {
                return;
            }

            TrackedCommand? tracked = FindTracked(envelope.Scope.ScopeId, envelope.RequestId);
            if (tracked == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(envelope.Scope.AccountId) &&
                !string.Equals(envelope.Scope.AccountId, tracked.Entry.Key.AccountId, StringComparison.Ordinal))
            {
                return;
            }

            string digest = receipt.CanonicalPayloadSha256 ?? string.Empty;
            if (digest.Length > 0 && !DigestEquals(digest, tracked.Entry.DigestSha256))
            {
                await ResolveTrackedAsync(
                    tracked,
                    OutboxResolution.Conflict(ProtocolErrorCode.RequestIdConflict, tracked.Entry.DigestSha256),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (receipt.Status)
            {
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Unknown:
                    tracked.LastReceipt = ContractReceiptStatus.Unknown;
                    return;
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Unspecified:
                    return;
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Applied:
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Recorded:
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Rejected:
                    break;
                default:
                    Fault(ProtocolErrorCode.MalformedMessage, "Unknown receipt status.");
                    return;
            }

            if ((tracked.Entry.Kind == CommandKind.ClaimControl ||
                 tracked.Entry.Kind == CommandKind.ReleaseControl) &&
                string.Equals(receipt.ReasonCode, "LOCAL_CONTROL", StringComparison.Ordinal) &&
                (receipt.Status == global::Mahjong.Contracts.Wire.ReceiptStatus.Recorded ||
                 receipt.Status == global::Mahjong.Contracts.Wire.ReceiptStatus.Applied))
            {
                tracked.LastReceipt = ContractReceiptStatus.Recorded;
                await ResolveTrackedAsync(
                    tracked,
                    OutboxResolution.ControlApplied(
                        new StateVersion(receipt.EvaluatedStateVersion),
                        tracked.Entry.DigestSha256,
                        fromLookup: false),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            OutboxResolution resolution;
            try
            {
                resolution = BuildResolution(receipt, tracked.Entry.DigestSha256, fromLookup: false);
            }
            catch (ArgumentException ex)
            {
                Fault(ProtocolErrorCode.MalformedMessage, "Malformed receipt: " + ex.Message);
                return;
            }

            tracked.LastReceipt = resolution.ReceiptStatus;
            await ResolveTrackedAsync(tracked, resolution, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleReceiptLookupAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(envelope.CorrelationId) ||
                !_receiptQueries.TryGetValue(envelope.CorrelationId, out IdempotencyKey key))
            {
                return;
            }

            _receiptQueries.Remove(envelope.CorrelationId);
            if (!_commands.TryGetValue(key, out TrackedCommand? tracked))
            {
                return;
            }

            tracked.QueryInFlight = false;
            ReceiptLookup? lookup = envelope.ReceiptLookup;
            if (lookup == null)
            {
                return;
            }

            string echoedDigest = lookup.CanonicalPayloadSha256 ?? string.Empty;
            if (echoedDigest.Length > 0 && !DigestEquals(echoedDigest, tracked.Entry.DigestSha256))
            {
                return;
            }

            switch (lookup.Status)
            {
                case LookupStatus.Found:
                {
                    WireReceipt? receipt = lookup.Receipt;
                    if (receipt == null)
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "FOUND receipt lookup is missing its receipt.");
                        return;
                    }

                    string receiptDigest = receipt.CanonicalPayloadSha256 ?? string.Empty;
                    if (receiptDigest.Length > 0 && !DigestEquals(receiptDigest, tracked.Entry.DigestSha256))
                    {
                        await ResolveTrackedAsync(
                            tracked,
                            OutboxResolution.Conflict(ProtocolErrorCode.RequestIdConflict, tracked.Entry.DigestSha256),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if ((tracked.Entry.Kind == CommandKind.ClaimControl || tracked.Entry.Kind == CommandKind.ReleaseControl) && string.Equals(receipt.ReasonCode, "LOCAL_CONTROL", StringComparison.Ordinal) && receipt.Status == global::Mahjong.Contracts.Wire.ReceiptStatus.Recorded && !string.IsNullOrEmpty(receiptDigest) && DigestEquals(receiptDigest, tracked.Entry.DigestSha256))
                    {
                        tracked.LastLookup = ReceiptLookupStatus.Found;
                        await ResolveTrackedAsync(tracked, OutboxResolution.ControlApplied(new StateVersion(receipt.EvaluatedStateVersion), tracked.Entry.DigestSha256, true), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (receipt.Status == global::Mahjong.Contracts.Wire.ReceiptStatus.Unknown)
                    {
                        tracked.LastLookup = ReceiptLookupStatus.Unknown;
                        return;
                    }
                    OutboxResolution resolution;
                    try
                    {
                        resolution = BuildResolution(receipt, tracked.Entry.DigestSha256, fromLookup: true);
                    }
                    catch (ArgumentException ex)
                    {
                        Fault(ProtocolErrorCode.MalformedMessage, "Malformed receipt lookup: " + ex.Message);
                        return;
                    }

                    tracked.LastLookup = ReceiptLookupStatus.Found;
                    await ResolveTrackedAsync(tracked, resolution, cancellationToken).ConfigureAwait(false);
                    return;
                }
                case LookupStatus.Unknown:
                    // NOT definitive and never proves that the side effect is absent.
                    tracked.LastLookup = ReceiptLookupStatus.Unknown;
                    return;
                case LookupStatus.NotFound:
                    // A missing receipt is not proof that the command did not
                    // commit. Keep the entry unresolved and never auto-retry.
                    tracked.LastLookup = ReceiptLookupStatus.NotFound;
                    return;
                default:
                    Fault(ProtocolErrorCode.MalformedMessage, "Unknown receipt lookup status.");
                    return;
            }
        }

        private async Task SendQueriesAsync(bool resetPreviousLookup, CancellationToken cancellationToken)
        {
            var list = new List<TrackedCommand>();
            foreach (TrackedCommand tracked in _commands.Values)
            {
                if (!tracked.NeedsReconciliation || tracked.QueryInFlight)
                {
                    continue;
                }

                if (resetPreviousLookup && tracked.LastLookup.HasValue)
                {
                    tracked.LastLookup = null;
                }

                if (tracked.LastLookup.HasValue)
                {
                    continue;
                }

                list.Add(tracked);
            }

            list.Sort((left, right) =>
            {
                int scope = string.CompareOrdinal(left.Entry.ScopeId.Value, right.Entry.ScopeId.Value);
                return scope != 0 ? scope : string.CompareOrdinal(left.Entry.RequestId.Value, right.Entry.RequestId.Value);
            });

            for (int i = 0; i < list.Count; i++)
            {
                TrackedCommand tracked = list[i];
                string correlation = "receipt-query-" + NextCounter(ref _queryCounter);
                Envelope query = ClientWireMapper.NewEnvelope(MessageType.ReceiptQuery, correlation);
                if (_binding != null)
                {
                    query.SessionId = _binding.SessionId.Value;
                    query.RoomId = _binding.RoomId.Value;
                    query.SeatId = _binding.SeatId;
                    query.BindingEpoch = _binding.BindingEpoch.Value;
                }

                query.ReceiptQuery = new ReceiptQuery
                {
                    RequestScopeId = tracked.Entry.ScopeId.Value,
                    RequestId = tracked.Entry.RequestId.Value
                };

                _receiptQueries[correlation] = tracked.Entry.Key;
                tracked.QueryInFlight = true;
                try
                {
                    await SendEnvelopeAsync(query, cancellationToken).ConfigureAwait(false);
                }
                catch (TableChannelClosedException)
                {
                    tracked.QueryInFlight = false;
                    _receiptQueries.Remove(correlation);
                    return;
                }
                catch
                {
                    // Cancellation or an unexpected transport failure must not
                    // leave a permanent "lookup in flight" bit behind.
                    tracked.QueryInFlight = false;
                    _receiptQueries.Remove(correlation);
                    throw;
                }
            }
        }

        private async Task RequestFullBaselineAsync(string reasonCode, bool force, CancellationToken cancellationToken)
        {
            if (_binding == null)
            {
                return;
            }

            if (!force && _syncInFlight)
            {
                return;
            }

            if (State == TableConnectionState.Ready)
            {
                _connection.Fire(TableConnectionTrigger.ResyncRequired);
            }

            if (State != TableConnectionState.Synchronizing)
            {
                return;
            }

            _syncInFlight = true;
            string correlation = "sync-" + NextCounter(ref _syncCounter);
            _activeSyncCorrelationId = correlation;
            Envelope envelope = ClientWireMapper.NewEnvelope(MessageType.SyncRequest, correlation);
            envelope.SessionId = _binding.SessionId.Value;
            envelope.RoomId = _binding.RoomId.Value;
            envelope.SeatId = _binding.SeatId;
            envelope.BindingEpoch = _binding.BindingEpoch.Value;
            envelope.SyncRequest = new SyncRequest
            {
                Target = ClientWireMapper.ToSyncTarget(_binding.Role),
                KnownCursor = ClientWireMapper.ToWire(CurrentBaselineFloor()),
                ReasonCode = reasonCode,
                RequestFullBaseline = true
            };

            try
            {
                await SendEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException)
            {
                _syncInFlight = false;
                _activeSyncCorrelationId = null;
            }
            catch
            {
                _syncInFlight = false;
                _activeSyncCorrelationId = null;
                throw;
            }
        }

        private async Task<bool> SendTrackedAsync(TrackedCommand tracked, CancellationToken cancellationToken)
        {
            if (_binding == null)
            {
                throw new InvalidOperationException("Cannot send an outbox command without a binding.");
            }

            Envelope envelope = BuildCommandEnvelope(tracked.Entry, _binding);
            try
            {
                await SendEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // SendAsync is the handoff point. Any failure here - including
                // caller cancellation after the durable save - may have left
                // the channel with all or part of the frame. The original
                // request id, scope and bytes stay in the outbox as an unknown
                // outcome that must be reconciled, never regenerated.
                tracked.NeedsReconciliation = true;
                tracked.DeliveryUnknown = true;
                throw;
            }

            tracked.HandedToTransport = true;
            // A sent command without a definitive receipt is unresolved, even
            // while this connection is still healthy.
            tracked.NeedsReconciliation = true;
            return true;
        }

        private Envelope BuildCommandEnvelope(OutboxEntry entry, TableSessionBinding binding)
        {
            Envelope envelope = ClientWireMapper.NewEnvelope(MessageType.Command, null);
            envelope.RequestId = entry.RequestId.Value;
            envelope.Scope = ClientWireMapper.ToWire(entry.OwnerScope);
            envelope.SessionId = entry.SessionId.Value;
            envelope.RoomId = entry.RoomId.Value;
            envelope.SeatId = binding.SeatId;
            envelope.BindingEpoch = binding.BindingEpoch.Value;
            envelope.ControlEpoch = entry.ControlEpoch.HasValue ? entry.ControlEpoch.Value.Value : 0;
            envelope.OwnerEpoch = entry.OwnerEpoch.HasValue ? entry.OwnerEpoch.Value.Value : binding.OwnerEpoch.Value;
            envelope.Command = new Command
            {
                Type = (CommandType)(int)entry.Kind,
                CanonicalPayload = ByteString.CopyFrom(entry.CopyCanonicalPayload()),
                PayloadSchemaVersion = entry.PayloadSchemaVersion,
                ExpectedStateVersion = entry.ExpectedStateVersion.Value,
                ExpectedRoundId = entry.RoundId.HasValue ? entry.RoundId.Value.Value : string.Empty,
                ExpectedDecisionId = entry.DecisionId.HasValue ? entry.DecisionId.Value.Value : string.Empty
            };

            return envelope;
        }

        private async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            byte[] bytes = WireCodec.Encode(envelope);
            try
            {
                await _channel.SendAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException ex)
            {
                HandleTransportFailure(ex);
                throw;
            }
        }

        private async Task ResolveTrackedAsync(TrackedCommand tracked, OutboxResolution resolution, CancellationToken cancellationToken)
        {
            PendingCommandSnapshot snapshot = ToSnapshot(tracked);
            await _outbox.ResolveAsync(tracked.Entry.Key, resolution, cancellationToken).ConfigureAwait(false);

            _commands.Remove(tracked.Entry.Key);
            tracked.NeedsReconciliation = false;
            tracked.QueryInFlight = false;
            RemoveQueriesFor(tracked.Entry.Key);
            ReceiptResolved?.Invoke(this, new TableReceiptResolvedEventArgs(snapshot, resolution));
        }

        private void RemoveQueriesFor(IdempotencyKey key)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, IdempotencyKey> pair in _receiptQueries)
            {
                if (pair.Value.Equals(key))
                {
                    stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                _receiptQueries.Remove(stale[i]);
            }
        }

        private TrackedCommand? FindTracked(string scopeId, string requestId)
        {
            foreach (TrackedCommand tracked in _commands.Values)
            {
                if (string.Equals(tracked.Entry.ScopeId.Value, scopeId, StringComparison.Ordinal) &&
                    string.Equals(tracked.Entry.RequestId.Value, requestId, StringComparison.Ordinal))
                {
                    return tracked;
                }
            }

            return null;
        }

        private OutboxResolution BuildResolution(WireReceipt receipt, string expectedDigest, bool fromLookup)
        {
            ContractReceiptStatus status = MapReceiptStatus(receipt.Status);
            StateVersion evaluated = new StateVersion(receipt.EvaluatedStateVersion);
            StateVersion? commandVersion = receipt.HasCommandVersion ? new StateVersion(receipt.CommandVersion) : (StateVersion?)null;
            EventSeq? eventSeq = receipt.HasEventSeq ? new EventSeq(receipt.EventSeq) : (EventSeq?)null;
            ulong? committedAt = receipt.HasCommittedAtMs ? receipt.CommittedAtMs : (ulong?)null;
            string digest = string.IsNullOrEmpty(receipt.CanonicalPayloadSha256) ? expectedDigest : receipt.CanonicalPayloadSha256;
            return OutboxResolution.FromReceipt(
                status,
                receipt.ReasonCode,
                evaluated,
                commandVersion,
                eventSeq,
                committedAt,
                digest,
                fromLookup);
        }

        private static ContractReceiptStatus MapReceiptStatus(global::Mahjong.Contracts.Wire.ReceiptStatus status)
        {
            switch (status)
            {
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Applied:
                    return ContractReceiptStatus.Applied;
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Recorded:
                    return ContractReceiptStatus.Recorded;
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Rejected:
                    return ContractReceiptStatus.Rejected;
                case global::Mahjong.Contracts.Wire.ReceiptStatus.Unknown:
                    return ContractReceiptStatus.Unknown;
                default:
                    return ContractReceiptStatus.Unspecified;
            }
        }

        private void EnsureCommandAdmitted(CommandKind kind)
        {
            if (_binding == null)
            {
                throw new InvalidOperationException("No binding is established.");
            }

            if (!IsCommandAdmittedForRole(_binding.Role, kind))
            {
                throw new InvalidOperationException(
                    "Role " + _binding.Role + " is not permitted to submit or retry " + kind + ".");
            }
        }

        /// <summary>
        /// Role admission is deliberately narrower than the wire enum: a
        /// spectator may use read-only/spectator control commands only, never a
        /// seat action, cheat action or match mutation. Player bindings keep the
        /// full command set because scope/epoch/version checks gate each action.
        /// </summary>
        private static bool IsCommandAdmittedForRole(ContractRole role, CommandKind kind)
        {
            if (role == ContractRole.Player)
            {
                return true;
            }

            if (role == ContractRole.Spectator)
            {
                switch (kind)
                {
                    case CommandKind.JoinSpectator:
                    case CommandKind.ResumeSpectator:
                    case CommandKind.ResumePublic:
                    case CommandKind.ResumeCursor:
                    case CommandKind.QueryPublicRoundResult:
                    case CommandKind.GetRoomInfo:
                    case CommandKind.ListRooms:
                        return true;
                    default:
                        return false;
                }
            }

            if (role == ContractRole.Lobby)
            {
                switch (kind)
                {
                    case CommandKind.CreateRoom:
                    case CommandKind.JoinRoom:
                    case CommandKind.ListRooms:
                    case CommandKind.ResumeSession:
                    case CommandKind.LeaveRoom:
                    case CommandKind.GetRoomInfo:
                    case CommandKind.JoinSpectator:
                    case CommandKind.ResumeSpectator:
                    case CommandKind.ResumePublic:
                    case CommandKind.ResumeCursor:
                    case CommandKind.QueryPublicRoundResult:
                        return true;
                    default:
                        return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Clears lookup state that can no longer receive a reply. The original
        /// request ids and payload bytes stay untouched; only the transport-bound
        /// query bookkeeping is reset so the next baseline re-queries.
        /// </summary>
        private void ResetPendingCommandLookups(bool markUnknownIfHanded)
        {
            foreach (TrackedCommand tracked in _commands.Values)
            {
                tracked.QueryInFlight = false;
                tracked.LastLookup = null;
                tracked.LastReceipt = null;
                if (markUnknownIfHanded && tracked.HandedToTransport)
                {
                    tracked.NeedsReconciliation = true;
                    tracked.DeliveryUnknown = true;
                }
            }
        }

        private bool MatchesBinding(Envelope envelope)
        {
            if (_binding == null)
            {
                return false;
            }

            if (envelope.BindingEpoch != _binding.BindingEpoch.Value)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(envelope.SessionId) &&
                !string.Equals(envelope.SessionId, _binding.SessionId.Value, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(envelope.RoomId) &&
                !string.Equals(envelope.RoomId, _binding.RoomId.Value, StringComparison.Ordinal))
            {
                return false;
            }

            if (_binding.Role == ContractRole.Player &&
                envelope.SeatId >= ProtocolConstants.MinSeat &&
                envelope.SeatId != _binding.SeatId)
            {
                return false;
            }

            return true;
        }

        private EarlyPushBuffer CreateEarlyBuffer(ServerLimits? limits)
        {
            int items = _options.MaxEarlyBufferItems;
            long bytes = _options.MaxEarlyBufferBytes;
            if (limits != null)
            {
                if (limits.MaxEarlyBufferItems > 0)
                {
                    items = Math.Min(items, checked((int)Math.Min(limits.MaxEarlyBufferItems, int.MaxValue)));
                }

                if (limits.MaxEarlyBufferBytes > 0)
                {
                    bytes = Math.Min(bytes, limits.MaxEarlyBufferBytes > long.MaxValue ? long.MaxValue : (long)limits.MaxEarlyBufferBytes);
                }
            }

            return new EarlyPushBuffer(items, bytes);
        }

        private void HandleInboundClose(Close close)
        {
            if (close == null)
            {
                return;
            }

            _lastError = string.IsNullOrEmpty(close.Code) ? ProtocolErrorCode.ProtocolViolation : close.Code;
            _earlyBuffer.Clear();
            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _receiptQueries.Clear();
            // A protocol CLOSE is a transport loss exactly like a channel
            // failure: no lookup reply will arrive, and a sent command's
            // outcome is unknown until the next baseline re-queries it.
            ResetPendingCommandLookups(markUnknownIfHanded: true);
            if (State != TableConnectionState.Closed && State != TableConnectionState.Faulted)
            {
                _connection.Fire(close.Permanent ? TableConnectionTrigger.PermanentFailure : TableConnectionTrigger.TransientFailure);
            }
        }

        private void HandleInboundError(ProtocolError error)
        {
            if (error == null)
            {
                return;
            }

            _lastError = (string.IsNullOrEmpty(error.Code) ? ProtocolErrorCode.InternalError : error.Code) +
                         (string.IsNullOrEmpty(error.DetailCode) ? string.Empty : "/" + error.DetailCode);
            if (!error.Retryable && State != TableConnectionState.Closed && State != TableConnectionState.Faulted)
            {
                _earlyBuffer.Clear();
                _syncInFlight = false;
                _activeSyncCorrelationId = null;
                _receiptQueries.Clear();
                ResetPendingCommandLookups(markUnknownIfHanded: true);
                _connection.Fire(TableConnectionTrigger.PermanentFailure);
            }
        }

        private void HandleTransportFailure(TableChannelClosedException exception)
        {
            _lastError = exception.Code;
            _earlyBuffer.Clear();
            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _receiptQueries.Clear();
            ResetPendingCommandLookups(markUnknownIfHanded: true);

            if (State == TableConnectionState.Closed || State == TableConnectionState.Faulted)
            {
                return;
            }

            _connection.Fire(exception.Permanent ? TableConnectionTrigger.PermanentFailure : TableConnectionTrigger.TransientFailure);
        }

        private void Fault(string code, string message)
        {
            _lastError = code + ": " + message;
            _earlyBuffer.Clear();
            _syncInFlight = false;
            _activeSyncCorrelationId = null;
            _receiptQueries.Clear();
            ResetPendingCommandLookups(markUnknownIfHanded: true);
            if (State != TableConnectionState.Closed && State != TableConnectionState.Faulted)
            {
                _connection.Fire(TableConnectionTrigger.PermanentFailure);
            }
        }

        private void RaiseSnapshotChanged(ContractCursor cursor, bool isFullBaseline)
        {
            SnapshotChanged?.Invoke(this, new TableSnapshotChangedEventArgs(
                cursor,
                isFullBaseline,
                _playerSnapshot,
                _publicSnapshot,
                _roomSnapshot));
        }

        private static PendingCommandSnapshot ToSnapshot(TrackedCommand tracked)
        {
            return new PendingCommandSnapshot(
                tracked.Entry.Key,
                tracked.Entry.Kind,
                tracked.Entry.DigestSha256,
                tracked.Entry.PayloadLength,
                tracked.HandedToTransport,
                tracked.DeliveryUnknown,
                tracked.NeedsReconciliation,
                tracked.LastLookup,
                tracked.LastReceipt,
                tracked.QueryInFlight);
        }

        private static bool DigestEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string NextCounter(ref long counter)
        {
            counter = checked(counter + 1);
            return counter.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class TrackedCommand
        {
            public TrackedCommand(OutboxEntry entry)
            {
                Entry = entry;
            }

            public OutboxEntry Entry { get; }

            public bool HandedToTransport { get; set; }

            public bool DeliveryUnknown { get; set; }

            public bool NeedsReconciliation { get; set; }

            public ReceiptLookupStatus? LastLookup { get; set; }

            public ContractReceiptStatus? LastReceipt { get; set; }

            public bool QueryInFlight { get; set; }
        }
    }
}

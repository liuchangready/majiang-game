using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.TableRuntime;
using Microsoft.Extensions.Logging;


namespace Mahjong.TableService
{
    /// <summary>
    /// One /v1/table WebSocket session. Owns the V5 framing discipline (one complete binary
    /// message = one Envelope, fragments accumulated to EndOfMessage, oversize/concat/text
    /// refused), the HELLO/WELCOME admission flow, serialized command routing through the
    /// per-room actor, heartbeat/idle liveness, drain awareness and the frozen close-code map.
    /// Logging is redacted: ticket strings, payload bytes and full identifiers never reach logs.
    /// </summary>
    public sealed class TableSessionHandler
    {
        private readonly WebSocket _ws;
        private readonly string _admissionRoom;
        private readonly TableServiceOptions _options;
        private readonly DrainState _drain;
        private readonly TableTicketValidator _validator;
        private readonly ITicketConsumptionRegistry _consumption;
        private readonly ITableSessionDirectory _sessions;
        private readonly ITableHostRegistry _hosts;
        private readonly ILogger _logger;
        private readonly long _connectionEpoch;

        // Connection state (written once by admission; read-only afterwards).
        private TableConnectionIdentity? _identity;
        private RoomHost? _host;
        private WireTableStreamSink? _sink;
        private TableStreamHandle? _handle;
        private string? _sessionShort;
        private string? _registeredSessionId;
        private volatile bool _displaced;

        // Outbound: serialized byte writes + terminal close sentinel.
        private readonly Channel<OutboundItem> _outbound =
            Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        // Ingress token bucket (commands + sync + receipt queries).
        private readonly object _rateGate = new object();
        private double _tokens;
        private long _lastRefillTicks = -1;
        private int _consecutiveOverRate;

        private long _lastInboundMs;
        private volatile bool _closeRequested;
        private readonly object _closeGate = new object();
        private CloseDirective? _pendingClose;

        /// <summary>0 = close handshake not yet started; 1 = started exactly once.</summary>
        private int _closeSent;

        /// <summary>True once this session's transport CloseOutput actually completed.</summary>
        private volatile bool _closeOutputSent;

        public TableSessionHandler(
            WebSocket webSocket,
            string admissionRoom,
            TableServiceOptions options,
            DrainState drain,
            TableTicketValidator validator,
            ITicketConsumptionRegistry consumption,
            ITableSessionDirectory sessions,
            ITableHostRegistry hosts,
            ILogger logger,
            long connectionEpoch)
        {
            _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
            _admissionRoom = admissionRoom;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _drain = drain ?? throw new ArgumentNullException(nameof(drain));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _consumption = consumption ?? throw new ArgumentNullException(nameof(consumption));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _logger = logger;
            _connectionEpoch = connectionEpoch;
            _tokens = options.CommandBurstCapacity;
        }

        private sealed class OutboundItem
        {
            public OutboundItem(byte[] bytes)
            {
                Bytes = bytes;
            }

            public OutboundItem(CloseDirective close)
            {
                Close = close;
            }

            public byte[]? Bytes { get; }

            public CloseDirective? Close { get; }
        }

        private readonly struct CloseDirective
        {
            public CloseDirective(string code, bool permanent, int wsCloseCode)
            {
                Code = code;
                Permanent = permanent;
                WebSocketCloseCode = wsCloseCode;
            }

            public string Code { get; }

            public bool Permanent { get; }

            public int WebSocketCloseCode { get; }
        }

        private sealed class AdmissionClosedException : Exception
        {
            public AdmissionClosedException(CloseDirective directive) : base(directive.Code)
            {
                Directive = directive;
            }

            public CloseDirective Directive { get; }
        }

        private sealed class SessionFaultException : Exception
        {
            public SessionFaultException(CloseDirective directive) : base(directive.Code)
            {
                Directive = directive;
            }

            public CloseDirective Directive { get; }
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public async Task RunAsync(CancellationToken serverStopping)
        {
            try
            {
                TableConnectionIdentity identity = await AdmitAsync(serverStopping).ConfigureAwait(false);
                _identity = identity;
                _sessionShort = Short(identity.SessionId);
                var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
                Task sender = SendLoopAsync(linked.Token);
                Task liveness = LivenessLoopAsync(linked.Token);
                try
                {
                    await ReceiveLoopAsync(linked).ConfigureAwait(false);
                }
                finally
                {
                    // Flush any queued close handshake while the sender's token is still
                    // live: the peer must always see the close handshake, never an abrupt
                    // abort (e.g. the peer's own CLOSE envelope ends the receive loop and
                    // cancellation would otherwise beat the sender to the drain). Only one
                    // close is ever sent (idempotent guard inside SendCloseAndCloseAsync).
                    await FlushCloseIfQueuedAsync().ConfigureAwait(false);
                    linked.Cancel();
                    try
                    {
                        await sender.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    try
                    {
                        await liveness.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            catch (AdmissionClosedException admission)
            {
                await SendCloseAndCloseAsync(admission.Directive, serverStopping).ConfigureAwait(false);
            }
            catch (Exception transport) when (
                transport is WebSocketException ||
                transport is System.IO.IOException ||
                transport is ObjectDisposedException)
            {
                // Peer transport abort before or between phases (e.g. the peer vanished
                // during the admission read): nobody is left to handshake a close with,
                // and the same abrupt-transport reasoning as the receive loop applies.
            }
            finally
            {
                await TeardownAsync().ConfigureAwait(false);
                await DrainCloseHandshakeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Half-close drain. Once this session has sent its transport close, the peer must
        /// get the chance to actually complete the handshake: a slow or stalled reader
        /// reaches the close bytes only at its own read rate, and returning immediately
        /// lets the host abort the connection — destroying the close bytes still sitting in
        /// the peer's pipeline and replacing the mapped close code with an abrupt reset.
        /// This drains the inbound direction (ignoring late data frames) until the peer's
        /// close frame arrives or the grace budget ends; every session resource is already
        /// released before this runs, so the wait holds only the socket.
        /// </summary>
        private async Task DrainCloseHandshakeAsync()
        {
            if (!_closeOutputSent || _ws.State == WebSocketState.Aborted)
            {
                return;
            }

            try
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                grace.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(500, _options.CloseGraceMs)));
                var buffer = new byte[16 * 1024];
                while (_ws.State == WebSocketState.CloseSent)
                {
                    ValueWebSocketReceiveResult received =
                        await _ws.ReceiveAsync(buffer.AsMemory(), grace.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    // Late data frames (an in-flight flood, queued pings) are ignored; the
                    // peer's close frame is the only exit.
                }
            }
            catch (Exception)
            {
                // Peer gone, or the grace expired — the transport abort is then the honest
                // end for a peer that never completed the handshake it was given.
            }
        }

        private async Task TeardownAsync()
        {
            TableConnectionIdentity? identity = _identity;
            RoomHost? host = _host;
            TableStreamHandle? handle = _handle;
            try
            {
                if (host != null && handle != null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await host.Actor.UnsubscribeStreamAsync(handle, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort teardown; the actor frees streams on actor close.
            }

            if (_registeredSessionId != null)
            {
                // Covers both ended sessions and admissions that failed after registration.
                _sessions.Unregister(_registeredSessionId);
            }

            if (identity != null)
            {
                _consumption.Release(identity.SessionId);
                if (identity.IsPlayer && host != null && !_displaced)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await host.Actor.DisconnectAsync(
                            identity.Seat,
                            host.OwnerEpoch,
                            new ControlEpoch(0),
                            "TRANSPORT_CLOSED",
                            timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // P3.3 grace policy governs the seat; a failed notify must not throw
                        // out of connection teardown.
                    }
                }

                LogRedacted("session closed", "session", _sessionShort, "room", identity.RoomId);
            }
        }

        private async Task SendLoopAsync(CancellationToken stopping)
        {
            // Merged drain of the reply channel and (after admission) the actor stream channel.
            // Both waits are held as Tasks so the loser of a WhenAny race stays awaitable —
            // a ValueTask must never be consumed twice.
            Task<bool>? replyTask = null;
            Task<bool>? streamTask = null;
            bool streamCompleted = false;
            try
            {
                while (true)
                {
                    // A reserved close directive jumps the queue: once the close decision is
                    // made, queued frames are moot and the close handshake must not depend on
                    // the peer draining the backlog first. Parking below is safe against a
                    // concurrent reservation only while both channels are empty — and then the
                    // sentinel TryWrite inside EnqueueClose succeeds and wakes the wait.
                    CloseDirective? reserved = TakePendingClose();
                    if (reserved.HasValue)
                    {
                        await SendCloseAndCloseAsync(reserved.Value, stopping).ConfigureAwait(false);
                        return;
                    }

                    if (replyTask == null)
                    {
                        replyTask = _outbound.Reader.WaitToReadAsync(stopping).AsTask();
                    }

                    if (_sink != null && streamTask == null && !streamCompleted)
                    {
                        streamTask = _sink.Reader.WaitToReadAsync(stopping).AsTask();
                    }

                    if (streamTask != null)
                    {
                        Task completed = await Task.WhenAny(replyTask, streamTask).ConfigureAwait(false);
                        if (completed == streamTask)
                        {
                            bool hasFrame = await streamTask.ConfigureAwait(false);
                            streamTask = null;
                            if (!hasFrame)
                            {
                                // Stream completed (actor termination); replies may still flow and
                                // the termination callback queued the matching close directive.
                                streamCompleted = true;
                                continue;
                            }

                            while (_sink!.Reader.TryRead(out byte[]? frame))
                            {
                                await _ws.SendAsync(
                                    frame,
                                    WebSocketMessageType.Binary,
                                    WebSocketMessageFlags.EndOfMessage,
                                    stopping).ConfigureAwait(false);
                            }

                            continue;
                        }
                    }

                    bool hasReply = await replyTask.ConfigureAwait(false);
                    replyTask = null;
                    if (!hasReply)
                    {
                        return;
                    }

                    bool closed = await DrainRepliesAsync(stopping).ConfigureAwait(false);
                    if (closed)
                    {
                        return;
                    }
                }
            }
            finally
            {
                _outbound.Writer.TryComplete();
            }
        }

        /// <summary>Drains currently-queued reply items. True when the connection must end.</summary>
        private async Task<bool> DrainRepliesAsync(CancellationToken stopping)
        {
            while (_outbound.Reader.TryRead(out OutboundItem? item))
            {
                if (item.Close != null)
                {
                    await SendCloseAndCloseAsync(item.Close.Value, stopping).ConfigureAwait(false);
                    return true;
                }

                await _ws.SendAsync(
                    item.Bytes!,
                    WebSocketMessageType.Binary,
                    WebSocketMessageFlags.EndOfMessage,
                    stopping).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Post-teardown flush: sends a queued close directive the cancelled sender never
        /// drained. No-op when the channel is empty (the close already went out) or when no
        /// close was requested.
        /// </summary>
        private async Task FlushCloseIfQueuedAsync()
        {
            if (!_closeRequested)
            {
                return;
            }

            while (_outbound.Reader.TryRead(out OutboundItem? item))
            {
                if (item.Close != null)
                {
                    await SendCloseAndCloseAsync(item.Close.Value, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }

            // The sentinel never made it into a saturated channel; the reservation is the
            // authority on the pending close.
            CloseDirective? reserved = TakePendingClose();
            if (reserved.HasValue)
            {
                await SendCloseAndCloseAsync(reserved.Value, CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>Actor-side stream death (slow consumer, stale fence, actor fault).</summary>
        private void OnStreamTerminatedFromActor(TableStreamTermination termination)
        {
            LogRedacted(
                "stream terminated",
                "kind", termination.Kind.ToString(),
                "reason", termination.ReasonCode);
            EnqueueClose(Directive(TerminationCloseCode(termination.Kind)));
        }

        private static string TerminationCloseCode(TableStreamTerminationKind kind)
        {
            return kind switch
            {
                TableStreamTerminationKind.SlowConsumer => ProtocolErrorCode.SlowConsumer,
                TableStreamTerminationKind.MessageTooLarge => ProtocolErrorCode.MessageTooLarge,
                TableStreamTerminationKind.StaleOwner or
                TableStreamTerminationKind.LeaseLost => ProtocolErrorCode.LeaseLost,
                TableStreamTerminationKind.ActorClosed or
                TableStreamTerminationKind.ActorFaulted or
                TableStreamTerminationKind.ProjectionFailed or
                TableStreamTerminationKind.EncoderFailed => ProtocolErrorCode.InternalError,
                _ => ProtocolErrorCode.LocalClosed
            };
        }

        /// <summary>顶号: a newer valid ticket consumption re-bound this session's seat.</summary>
        private void OnDisplacedByNewConnection()
        {
            _displaced = true;
            LogRedacted("session displaced", "room", _admissionRoom);
            EnqueueClose(Directive(ProtocolErrorCode.DisplacedByNewConnection));
        }

        /// <summary>Server-side liveness: application heartbeat pings plus the idle-dead fence.</summary>
        private async Task LivenessLoopAsync(CancellationToken stopping)
        {
            Interlocked.Exchange(ref _lastInboundMs, Environment.TickCount64);
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(250, _options.HeartbeatIntervalMs / 2)));
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                long now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastInboundMs) > _options.IdleDeadMs)
                {
                    EnqueueClose(Directive(ProtocolErrorCode.HeartbeatTimeout));
                    return;
                }

                if (_identity != null)
                {
                    Enqueue(BuildHeartbeatPing());
                }
            }
        }

        // ------------------------------------------------------------------
        // Admission (HELLO -> WELCOME)
        // ------------------------------------------------------------------

        private async Task<TableConnectionIdentity> AdmitAsync(CancellationToken stopping)
        {
            if (_drain.IsDraining)
            {
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.ServerShutdown));
            }

            // 1. First message must arrive within the handshake timeout. Framing faults
            // (text frame, oversize, peer transport close) are still admissions: the peer
            // must always receive the close handshake, never an abrupt abort.
            byte[] message;
            using (var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.HandshakeTimeoutMs)))
            {
                try
                {
                    message = await ReadSingleMessageAsync(
                        handshakeTimeout.Token,
                        stopping,
                        earlyPhase: true).ConfigureAwait(false);
                }
                catch (SessionFaultException fault)
                {
                    throw new AdmissionClosedException(fault.Directive);
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    throw new AdmissionClosedException(Directive(ProtocolErrorCode.HandshakeTimeout));
                }
            }

            // 2. Decode (strict single envelope + frozen codec rules).
            Envelope envelope = DecodeOrThrow(message);

            // 3. Only HELLO is legal first; a polite client goodbye during the handshake
            // is acknowledged with the frozen LOCAL_CLOSED (1000) semantics.
            if (envelope.MessageType == MessageType.Close)
            {
                throw new AdmissionClosedException(new CloseDirective(
                    ProtocolErrorCode.LocalClosed,
                    permanent: true,
                    (int)WebSocketCloseStatus.NormalClosure));
            }

            if (envelope.MessageType != MessageType.Hello || envelope.BodyCase != Envelope.BodyOneofCase.Hello)
            {
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.ProtocolViolation));
            }

            Hello hello = envelope.Hello;

            // 4. Version negotiation through the frozen compatibility matrix.
            if (hello.ProtocolMajor != ProtocolConstants.Major)
            {
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.ProtocolMajorUnsupported));
            }

            if (hello.RulesetVersion != ProtocolConstants.RulesetVersion)
            {
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.RulesetVersionUnsupported));
            }

            if (hello.ProjectionVersion != ProtocolConstants.ProjectionVersion)
            {
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.ProjectionVersionUnsupported));
            }

            // 5. Ticket validation against the route room (the client can never self-select a
            // room outside the ticket). Refusal is always close 4401; precise codes live in
            // the redacted audit log only.
            var expectation = new TableTicketExpectation(
                _admissionRoom,
                role: null,
                seatId: null,
                accountId: null);
            ValidatedTableTicket ticket;
            try
            {
                ticket = _validator.Validate(hello.Ticket, expectation, DateTimeOffset.UtcNow);
            }
            catch (TableTicketValidationException ex)
            {
                LogRedacted("ticket rejected", "code", ex.Code, "room", _admissionRoom);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.InvalidToken));
            }

            // 6. One-time consumption + seat uniqueness (顶号 displacement decided here).
            string sessionId = Guid.NewGuid().ToString("N");
            TicketConsumptionOutcome consumed = _consumption.Consume(ticket, sessionId, DateTimeOffset.UtcNow);
            if (!consumed.IsAccepted)
            {
                LogRedacted("ticket consumption refused", "status", consumed.Status.ToString(), "room", _admissionRoom);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.InvalidToken));
            }

            if (consumed.DisplacedSessionId != null)
            {
                // 顶号: the newest valid consumption of the same (account, room, seat) closes
                // the superseded live session. It skips its own seat disconnect at teardown,
                // so the actor never puts the seat into grace during a live handover.
                _sessions.Displace(consumed.DisplacedSessionId);
            }

            // Register before WELCOME: the close directive is only queued here (the send loop
            // starts after admission returns), so a displacement racing this admission cannot
            // interleave with the single direct WELCOME write below.
            _sessions.Register(sessionId, OnDisplacedByNewConnection);
            _registeredSessionId = sessionId;

            // 7. Per-table host binding (bounded; admission-stopped hosts refuse).
            RoomHost host;
            try
            {
                host = _hosts.GetOrAdd(_admissionRoom);
            }
            catch (TableServiceCapacityException)
            {
                _consumption.Release(sessionId);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.ServerBusy));
            }

            if (host.AdmissionStopped)
            {
                _consumption.Release(sessionId);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.RoomClosed));
            }

            // 8. Server-issued request scope (expires with the ticket, never later).
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expiresMs = ticket.ExpiresAt.ToUnixTimeMilliseconds();
            TableScopeRecord scope = host.Scopes.Issue(
                ticket.AccountId,
                ticket.Role == TableAdmissionRole.Player ? TableScopeRole.Player : TableScopeRole.Spectator,
                _admissionRoom,
                (ulong)nowMs,
                (ulong)expiresMs);

            var identity = new TableConnectionIdentity(
                sessionId,
                _admissionRoom,
                ticket.Role,
                ticket.SeatId ?? ProtocolConstants.NoSeat,
                ticket.AccountId,
                scope.ScopeId.Value,
                nowMs,
                expiresMs,
                ticket.Jti);

            // 9. Atomic baseline + subscription through the actor (never a host-side read).
            var sink = new WireTableStreamSink(capacity: 64);
            // The callback must exist before the actor can pump: SubscribeStreamAsync starts
            // the pump and an immediate termination (encoder/projection fault) would otherwise
            // fire into a null callback and be lost.
            sink.Terminated = OnStreamTerminatedFromActor;
            var subscribe = new TableStreamSubscribeRequest(
                new TableStreamIdentity(
                    new SessionId(sessionId),
                    ticket.Role == TableAdmissionRole.Player
                        ? TableStreamAudience.ForSeat(identity.Seat)
                        : TableStreamAudience.Spectator,
                    new BindingEpoch(1),
                    new SyncEpoch(1)),
                host.OwnerEpoch,
                recoveryGeneration: 0L,
                sink);
            TableStreamControlResult subscribed =
                await host.Actor.SubscribeStreamAsync(subscribe, stopping).ConfigureAwait(false);
            if (!subscribed.IsAccepted || subscribed.Handle == null)
            {
                LogRedacted(
                    "stream subscribe refused",
                    "status", subscribed.Status.ToString(),
                    "reason", subscribed.ReasonCode,
                    "detail", subscribed.Detail);
                _consumption.Release(sessionId);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.InternalError));
            }

            if (sink.Termination != null)
            {
                // The pump accepted the stream and immediately terminated it; the mapped close
                // code keeps the client-visible semantics identical to a post-WELCOME death.
                LogRedacted(
                    "stream died during admission",
                    "kind", sink.Termination.Kind.ToString(),
                    "reason", sink.Termination.ReasonCode);
                _consumption.Release(sessionId);
                throw new AdmissionClosedException(Directive(TerminationCloseCode(sink.Termination.Kind)));
            }

            _host = host;
            sink.Bind(subscribed.Handle);
            _sink = sink;
            _handle = subscribed.Handle;
            host.TrackSink(sink);

            // 10. WELCOME.
            Envelope welcome = BuildWelcome(identity, host, scope, subscribed);
            await _ws.SendAsync(
                WireCodec.Encode(welcome),
                WebSocketMessageType.Binary,
                WebSocketMessageFlags.EndOfMessage,
                stopping).ConfigureAwait(false);

            LogRedacted(
                "session admitted",
                "room", _admissionRoom,
                "role", identity.Role.ToString(),
                "seat", identity.Seat.ToString(CultureInfo.InvariantCulture),
                "kid", Short(ticket.Kid));
            return identity;
        }

        // ------------------------------------------------------------------
        // Receive + dispatch
        // ------------------------------------------------------------------

        private async Task ReceiveLoopAsync(CancellationTokenSource linked)
        {
            CancellationToken stopping = linked.Token;
            while (!stopping.IsCancellationRequested && !_closeRequested && _ws.State == WebSocketState.Open)
            {
                byte[] message;
                try
                {
                    message = await ReadSingleMessageAsync(stopping, stopping, earlyPhase: false).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SessionFaultException fault)
                {
                    EnqueueClose(fault.Directive);
                    return;
                }
                catch (Exception transport) when (
                    transport is WebSocketException ||
                    transport is System.IO.IOException ||
                    transport is ObjectDisposedException)
                {
                    // Abrupt peer transport abort (reset/EOF mid-frame, or the socket torn
                    // down under us): there is no peer left to handshake a close with, so
                    // end the loop and let the lifecycle finally release the session
                    // cleanly — surfacing it would land as an unhandled application fault
                    // (Kestrel fail-level noise) on a routine client disconnect.
                    return;
                }

                Interlocked.Exchange(ref _lastInboundMs, Environment.TickCount64);

                Envelope envelope;
                try
                {
                    envelope = Decode(message);
                }
                catch (SessionFaultException fault) when (
                    fault.Directive.Code == ProtocolErrorCode.MalformedMessage &&
                    IsNonceInvalidHeartbeat(message))
                {
                    // A structurally valid heartbeat with a bad nonce is application-level
                    // noise, not a wire violation: answer with an ERROR envelope and keep
                    // the session. Every other malformed frame still closes.
                    EnqueueError(ProtocolErrorCode.MalformedMessage, "heartbeat", "NONCE_INVALID");
                    continue;
                }
                catch (SessionFaultException fault)
                {
                    EnqueueClose(fault.Directive);
                    return;
                }

                switch (envelope.MessageType)
                {
                    case MessageType.Heartbeat:
                        DispatchHeartbeat(envelope);
                        break;
                    case MessageType.Command:
                        if (await DispatchCommandAsync(envelope, stopping).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    case MessageType.SyncRequest:
                        await DispatchSyncAsync(envelope, stopping).ConfigureAwait(false);
                        break;
                    case MessageType.ReceiptQuery:
                        await DispatchReceiptQueryAsync(envelope, stopping).ConfigureAwait(false);
                        break;
                    case MessageType.Close:
                        EnqueueClose(new CloseDirective(
                            ProtocolErrorCode.LocalClosed,
                            permanent: true,
                            (int)WebSocketCloseStatus.NormalClosure));
                        return;
                    case MessageType.Hello:
                        EnqueueError(ProtocolErrorCode.AlreadyBound, "hello", "SESSION_ALREADY_BOUND");
                        break;
                    default:
                        EnqueueError(ProtocolErrorCode.UnknownMessageType, "message_type", "DIRECTION_INVALID");
                        break;
                }
            }
        }

        private void DispatchHeartbeat(Envelope envelope)
        {
            Heartbeat heartbeat = envelope.Heartbeat;
            if (heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
            {
                string nonce = heartbeat.Ping.Nonce;
                if (!IsHeartbeatNonce(nonce))
                {
                    EnqueueError(ProtocolErrorCode.MalformedMessage, "heartbeat.ping.nonce", "NONCE_INVALID");
                    return;
                }

                Enqueue(BuildHeartbeatPong(nonce));
            }

            // Client pongs count as inbound liveness (the idle fence resets on every read).
        }

        /// <summary>Returns true when the connection must terminate.</summary>
        private async Task<bool> DispatchCommandAsync(Envelope envelope, CancellationToken stopping)
        {
            TableConnectionIdentity identity = _identity!;
            if (_drain.IsDraining)
            {
                EnqueueError(ProtocolErrorCode.ServerBusy, "connection", "DRAINING", retryable: true, retryAfterMs: 1000);
                return false;
            }

            if (!ConsumeRateToken())
            {
                int over = Interlocked.Increment(ref _consecutiveOverRate);
                EnqueueError(ProtocolErrorCode.ServerBusy, "connection", "RATE_LIMITED", retryable: true, retryAfterMs: 250);
                if (over >= 3)
                {
                    EnqueueClose(Directive(ProtocolErrorCode.SlowConsumer));
                    return true;
                }

                return false;
            }

            Interlocked.Exchange(ref _consecutiveOverRate, 0);
            RoomHost host = _host!;
            TableCommandRoute route;
            try
            {
                route = TableCommandMapper.Map(identity, envelope, host.OwnerEpoch);
            }
            catch (Exception)
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "command", "PAYLOAD_INVALID");
                return false;
            }

            if (route is TableCommandRoute.PreActorRejection rejection)
            {
                EnqueueError(rejection.ErrorCode, rejection.FieldPath, rejection.DetailCode, retryable: rejection.Retryable);
                return false;
            }

            byte[] payloadBytes = envelope.Command.CanonicalPayload.ToByteArray();
            var digest = TablePayloadSha256.Compute(payloadBytes);
            string digestHex = TableCommandMapper.ToHexLowercase(digest);

            if (route is TableCommandRoute.Control control)
            {
                TableControlResult controlResult;
                try
                {
                    controlResult = control.Reclaim
                        ? await host.Actor.ReclaimControlAsync(
                            control.Seat, control.OwnerEpoch, control.ExpectedControlEpoch, control.Reason, stopping)
                            .ConfigureAwait(false)
                        : await host.Actor.RequestTakeoverAsync(
                            control.Seat, control.OwnerEpoch, control.ExpectedControlEpoch, control.Reason, stopping)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    EnqueueError(ProtocolErrorCode.InternalError, "command", "CONTROL_FAULT");
                    return false;
                }

                if (controlResult.Status == TableControlStatus.Changed || controlResult.Admitted)
                {
                    Enqueue(BuildControlReceipt(
                        envelope,
                        Contracts.Wire.ReceiptStatus.Applied,
                        ReasonOrFallback(controlResult.ReasonCode),
                        (ulong)controlResult.EvaluatedStateVersion.Value));
                }
                else
                {
                    Enqueue(BuildControlReceipt(
                        envelope,
                        Contracts.Wire.ReceiptStatus.Rejected,
                        ReasonOrFallback(controlResult.ReasonCode),
                        (ulong)controlResult.EvaluatedStateVersion.Value));
                }

                return false;
            }

            TableOperationResult result;
            if (route is TableCommandRoute.BusinessCommand business)
            {
                result = await host.Actor.SubmitAsync(business.Envelope, stopping).ConfigureAwait(false);
            }
            else
            {
                result = await host.Actor.SubmitCheatAsync(
                    ((TableCommandRoute.CheatCommand)route).Envelope, stopping).ConfigureAwait(false);
            }

            switch (result.Status)
            {
                case TableOperationStatus.Accepted:
                case TableOperationStatus.Replayed:
                case TableOperationStatus.Rejected:
                    Enqueue(BuildReceipt(envelope, TableCommandMapper.MapReceipt(result, digestHex)));
                    break;
                case TableOperationStatus.MailboxFull:
                    EnqueueError(ProtocolErrorCode.ServerBusy, "connection", "MAILBOX_FULL", retryable: true, retryAfterMs: 250);
                    break;
                case TableOperationStatus.Closed:
                    EnqueueError(ProtocolErrorCode.ServerBusy, "connection", "TABLE_CLOSED");
                    break;
                case TableOperationStatus.Conflict:
                    EnqueueError(ProtocolErrorCode.RequestIdConflict, "request_id", "REQUEST_KEY_CONFLICT");
                    break;
                default:
                    EnqueueError(ProtocolErrorCode.InternalError, "command", "OPERATION_FAILED");
                    break;
            }

            return false;
        }

        private async Task DispatchSyncAsync(Envelope envelope, CancellationToken stopping)
        {
            SyncRequest request = envelope.SyncRequest;
            if (request.Target != SyncTarget.PlayerStream && request.Target != SyncTarget.PublicStream)
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "sync_request.target", "SYNC_TARGET_INVALID");
                return;
            }

            bool wantsPlayer = request.Target == SyncTarget.PlayerStream;
            if (wantsPlayer != _identity!.IsPlayer)
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "sync_request.target", "SYNC_TARGET_MISMATCH");
                return;
            }

            if (!ConsumeRateToken())
            {
                EnqueueError(ProtocolErrorCode.ServerBusy, "connection", "RATE_LIMITED", retryable: true, retryAfterMs: 250);
                return;
            }


            RoomHost host = _host!;
            Contracts.StreamCursor cursor = request.KnownCursor == null
                ? new Contracts.StreamCursor(new StreamVersion(0), new ProjectionSeq(0))
                : new Contracts.StreamCursor(
                    new StreamVersion(request.KnownCursor.StreamVersion),
                    new ProjectionSeq(request.KnownCursor.ProjectionSeq));

            // The authoritative baseline always flows through the subscribed stream
            // (PLAYER_UPDATE / PUBLIC_UPDATE with is_full_snapshot), and the frozen wire
            // validator requires every SYNC_RESPONSE to carry that baseline itself
            // (is_full_snapshot=true plus the matching baseline oneof): an ack-only reply
            // throws inside Enqueue's EnsureStrict. The only honest source of the baseline
            // is the actor's own stream frame — the host never invents projections — so the
            // sink observation is armed BEFORE the actor call. ProcessSyncStream enqueues the
            // full-snapshot frame into the actor's registration channel and its pump forwards
            // it into this sink asynchronously, so the observation is awaited with a bounded
            // timeout. The frame itself keeps flowing through the bounded sink (observation
            // never swallows), keeping the actor-side outbox bound the single trusted
            // backpressure authority.
            WireTableStreamSink sink = _sink!;
            sink.ArmFullSnapshotCapture();
            TableStreamFrame? baseline;
            try
            {
                TableStreamControlResult sync = await host.Actor.SyncStreamAsync(
                    new TableStreamSyncRequest(_handle!, cursor),
                    stopping).ConfigureAwait(false);

                if (!sync.IsAccepted && sync.Status != TableStreamControlStatus.BaselineQueued)
                {
                    // Disarm only: observation never swallows the frame, and a refused sync
                    // queued nothing, so there is nothing to release.
                    sink.DisarmFullSnapshotCapture();
                    EnqueueError(
                        ProtocolErrorCode.StaleStreamVersion,
                        "sync_request.known_cursor",
                        ReasonOrFallback(sync.ReasonCode));
                    return;
                }

                baseline = sync.Status == TableStreamControlStatus.BaselineQueued
                    ? await sink.WaitForCapturedFullSnapshotAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)
                    : null;
                if (baseline == null)
                {
                    // InSync: the stream was already current and the actor queued no
                    // baseline, yet the frozen contract still demands a full baseline in
                    // the reply. Force the authoritative re-baseline with a zero cursor;
                    // the client deduplicates the redundant full snapshot by cursor.
                    TableStreamControlResult forced = await host.Actor.SyncStreamAsync(
                        new TableStreamSyncRequest(
                            _handle!,
                            new Contracts.StreamCursor(new StreamVersion(0), new ProjectionSeq(0))),
                        stopping).ConfigureAwait(false);
                    baseline = await sink.WaitForCapturedFullSnapshotAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Disarm only: observation never swallows frames, so whatever the actor
                // queued already flowed through the bounded sink.
                sink.DisarmFullSnapshotCapture();
                throw;
            }

            if (baseline == null || baseline.EncodedPayload == null)
            {
                EnqueueError(ProtocolErrorCode.InternalError, "sync", "SYNC_BASELINE_UNAVAILABLE");
                return;
            }

            WireDecodeResult decoded = WireCodec.Decode(baseline.EncodedPayload);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                EnqueueError(ProtocolErrorCode.InternalError, "sync", "SYNC_BASELINE_UNAVAILABLE");
                return;
            }

            Envelope baselineEnvelope = decoded.Envelope;
            var response = new SyncResponse
            {
                Target = request.Target,
                IsFullSnapshot = true
            };
            if (wantsPlayer)
            {
                Contracts.Wire.PlayerSnapshot? player = baselineEnvelope.PlayerUpdate?.Snapshot;
                if (player == null)
                {
                    EnqueueError(ProtocolErrorCode.InternalError, "sync", "SYNC_BASELINE_UNAVAILABLE");
                    return;
                }

                response.Player = player;
                response.StateVersion = player.StateVersion;
                response.EventSeq = player.EventSeq;
            }
            else
            {
                Contracts.Wire.PublicSnapshot? publicSnapshot = baselineEnvelope.PublicUpdate?.Snapshot;
                if (publicSnapshot == null)
                {
                    EnqueueError(ProtocolErrorCode.InternalError, "sync", "SYNC_BASELINE_UNAVAILABLE");
                    return;
                }

                response.Public = publicSnapshot;
                response.StateVersion = publicSnapshot.StateVersion;
                response.EventSeq = publicSnapshot.EventSeq;
            }

            Envelope reply = BuildEnvelope(
                MessageType.SyncResponse, response, correlationId: envelope.CorrelationId);
            // The frozen client cross-checks a present envelope cursor against the baseline
            // snapshot cursor; the codec stamped the frame cursor on the baseline envelope.
            reply.Cursor = baselineEnvelope.Cursor;
            // The reply carries the baseline itself; the actor's own frame flows to the
            // client through the bounded sink as always (the frozen client ignores a
            // redundant full snapshot by cursor). No host-side duplicate copy is injected
            // here: the sink bound must stay the single trusted backpressure authority.
            Enqueue(reply);
        }

        private async Task DispatchReceiptQueryAsync(Envelope envelope, CancellationToken stopping)
        {
            ReceiptQuery query = envelope.ReceiptQuery;
            if (!ConsumeRateToken())
            {
                EnqueueError(ProtocolErrorCode.ServerBusy, "connection", "RATE_LIMITED", retryable: true, retryAfterMs: 250);
                return;
            }

            RoomHost host = _host!;
            RequestId requestId;
            try
            {
                requestId = new RequestId(query.RequestId);
            }
            catch (ArgumentException)
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "receipt_query.request_id", "REQUEST_ID_INVALID");
                return;
            }

            RequestScopeId scopeId;
            try
            {
                scopeId = new RequestScopeId(query.RequestScopeId);
            }
            catch (ArgumentException)
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "receipt_query.request_scope_id", "SCOPE_ID_INVALID");
                return;
            }

            if (!string.Equals(scopeId.Value, _identity!.ScopeId, StringComparison.Ordinal))
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "receipt_query.request_scope_id", "SCOPE_MISMATCH");
                return;
            }

            ReceiptLookupResult lookup = await host.Actor.QueryReceiptAsync(
                _identity.AccountId,
                scopeId,
                requestId,
                stopping).ConfigureAwait(false);

            var wire = new ReceiptLookup();
            switch (lookup.Status)
            {
                case ReceiptLookupStatus.Found:
                    wire.Status = LookupStatus.Found;
                    wire.Receipt = MapLookupReceipt(lookup.Receipt!);
                    break;
                case ReceiptLookupStatus.Unknown:
                    wire.Status = LookupStatus.Unknown;
                    break;
                default:
                    wire.Status = LookupStatus.NotFound;
                    break;
            }

            Enqueue(BuildEnvelope(MessageType.ReceiptLookup, wire, correlationId: envelope.CorrelationId));
        }

        private static Contracts.Wire.Receipt MapLookupReceipt(Contracts.Receipt receipt)
        {
            var wire = new Contracts.Wire.Receipt
            {
                Status = receipt.Status == Contracts.ReceiptStatus.Rejected
                    ? Contracts.Wire.ReceiptStatus.Rejected
                    : Contracts.Wire.ReceiptStatus.Applied,
                ReasonCode = receipt.ReasonCode,
                EvaluatedStateVersion = receipt.EvaluatedStateVersion.Value,
                Replayed = receipt.Replayed
            };
            if (receipt.CanonicalPayloadDigest.HasValue)
            {
                wire.CanonicalPayloadSha256 = receipt.CanonicalPayloadDigest.Value.ToHex();
            }

            if (receipt.CommandVersion.HasValue)
            {
                wire.CommandVersion = receipt.CommandVersion.Value.Value;
            }

            if (receipt.EventSeq.HasValue)
            {
                wire.EventSeq = receipt.EventSeq.Value.Value;
            }

            return wire;
        }

        // ------------------------------------------------------------------
        // Framing
        // ------------------------------------------------------------------

        private async Task<byte[]> ReadSingleMessageAsync(CancellationToken timeoutToken, CancellationToken stopping, bool earlyPhase)
        {
            byte[] buffer = new byte[16 * 1024];
            using var accumulator = new System.IO.MemoryStream();

            while (true)
            {
                // A pending ReceiveAsync must never be cancelled: cancelling it aborts the
                // socket and destroys the close handshake. The deadline races the receive
                // instead, and on timeout the close handshake is completed here while the
                // receive task is still in hand to drain the peer's close response.
                ValueTask<ValueWebSocketReceiveResult> pending = _ws.ReceiveAsync(buffer.AsMemory(), stopping);
                ValueWebSocketReceiveResult received;
                if (pending.IsCompletedSuccessfully)
                {
                    received = pending.Result;
                }
                else
                {
                    Task<ValueWebSocketReceiveResult> receiveTask = pending.AsTask();
                    Task winner = await Task.WhenAny(
                        receiveTask,
                        Task.Delay(Timeout.Infinite, timeoutToken)).ConfigureAwait(false);
                    if (winner != receiveTask)
                    {
                        await CompleteTimeoutCloseAsync(
                            receiveTask,
                            ProtocolErrorCode.HandshakeTimeout,
                            stopping).ConfigureAwait(false);
                        throw new AdmissionClosedException(Directive(ProtocolErrorCode.HandshakeTimeout));
                    }

                    try
                    {
                        received = receiveTask.Result;
                    }
                    catch (AggregateException aggregate)
                        when (aggregate.InnerException is OperationCanceledException)
                    {
                        // Session teardown cancelled the receive; the connection is already
                        // being closed by its owner.
                        throw new OperationCanceledException(stopping);
                    }
                }

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new SessionFaultException(new CloseDirective(
                        ProtocolErrorCode.LocalClosed,
                        permanent: true,
                        (int)WebSocketCloseStatus.NormalClosure));
                }

                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    // Text frames are never part of the V5 wire contract.
                    throw new SessionFaultException(Directive(ProtocolErrorCode.ProtocolViolation));
                }

                if (received.Count > 0)
                {
                    accumulator.Write(buffer, 0, received.Count);
                    if (accumulator.Length > _options.MaxInboundMessageBytes)
                    {
                        throw new SessionFaultException(Directive(ProtocolErrorCode.MessageTooLarge));
                    }

                    if (earlyPhase && accumulator.Length > _options.MaxEarlyBufferBytes)
                    {
                        throw new SessionFaultException(Directive(ProtocolErrorCode.MessageTooLarge));
                    }
                }

                if (received.EndOfMessage)
                {
                    return accumulator.ToArray();
                }
            }
        }

        /// <summary>
        /// Closes the send side with the deadline's directive and drains the peer's close
        /// response, so a timed-out handshake still ends with a complete close handshake.
        /// </summary>
        private async Task CompleteTimeoutCloseAsync(
            Task<ValueWebSocketReceiveResult> pendingReceive,
            string code,
            CancellationToken stopping)
        {
            try
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                grace.CancelAfter(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(
                    (WebSocketCloseStatus)ProtocolErrorCode.ToWebSocketCloseCode(code),
                    code,
                    grace.Token).ConfigureAwait(false);
                await pendingReceive.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The peer may already be gone; the close decision was still authoritative.
            }
        }

        private Envelope DecodeOrThrow(byte[] message)
        {
            try
            {
                return Decode(message);
            }
            catch (SessionFaultException fault)
            {
                throw new AdmissionClosedException(fault.Directive);
            }
        }

        private static Envelope Decode(byte[] message)
        {
            if (message.Length == 0)
            {
                throw new SessionFaultException(Directive(ProtocolErrorCode.ProtocolViolation));
            }

            if (ProtocolCompatibility.HasLegacyV4Magic(message))
            {
                // V4 bytes never mix into the V5 wire. The frozen Contracts close-code
                // mapper has no entry for LEGACY_V4_REJECTED (its default is 1011); the V5
                // service pins wire-mixing refusals in the 4400 protocol family with the
                // precise code kept as the close reason.
                throw new SessionFaultException(new CloseDirective(
                    ProtocolErrorCode.LegacyV4Rejected,
                    permanent: true,
                    4400));
            }

            WireDecodeResult decoded;
            try
            {
                decoded = WireCodec.Decode(message);
            }
            catch (WireMalformedException)
            {
                // Truncated fields / trailing garbage never parse as one envelope.
                throw new SessionFaultException(Directive(ProtocolErrorCode.MalformedMessage));
            }
            catch (WireMessageTooLargeException)
            {
                throw new SessionFaultException(Directive(ProtocolErrorCode.MessageTooLarge));
            }
            catch (WireProtocolException ex)
            {
                throw new SessionFaultException(Directive(ex.Code));
            }

            if (!decoded.IsOk || decoded.Envelope == null)
            {
                throw new SessionFaultException(Directive(decoded.ErrorCode));
            }

            // One WebSocket message = one Envelope. Bare protobuf cannot locate a
            // first-message boundary (Google.Protobuf merges concatenated same-type
            // messages silently), so on the pre-trust HELLO the decoded envelope is
            // re-encoded with the frozen codec: canonical output must reproduce the
            // message byte-for-byte, while a merged concatenation re-encodes strictly
            // smaller. Encode failures skip the check — preserved unknown fields keep
            // their compatibility semantics and are never a refusal reason.
            if (decoded.Envelope.MessageType == MessageType.Hello)
            {
                try
                {
                    if (!WireCodec.Encode(decoded.Envelope).AsSpan().SequenceEqual(message))
                    {
                        throw new SessionFaultException(Directive(ProtocolErrorCode.ProtocolViolation));
                    }
                }
                catch (WireProtocolException)
                {
                    // Includes WireMalformedException; unknown-field compatibility wins
                    // over concat detection and is never a refusal reason.
                }
            }

            return decoded.Envelope;
        }

        /// <summary>
        /// True only when the raw frame lenient-parses as a heartbeat whose nonce the frozen
        /// validator rejects. Used to downgrade that single case to an ERROR envelope; every
        /// other malformed frame still closes the session.
        /// </summary>
        private static bool IsNonceInvalidHeartbeat(byte[] message)
        {
            Envelope lenient;
            try
            {
                lenient = Envelope.Parser.ParseFrom(message);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                return false;
            }

            if (lenient.MessageType != MessageType.Heartbeat)
            {
                return false;
            }

            try
            {
                WireCodec.ValidateHeartbeat(lenient.Heartbeat);
                return false;
            }
            catch (WireProtocolException)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // Outbound envelope builders
        // ------------------------------------------------------------------

        private Envelope BuildEnvelope(
            MessageType messageType,
            Google.Protobuf.IMessage body,
            string? correlationId,
            TableConnectionIdentity? identity = null)
        {
            TableConnectionIdentity resolved = identity ?? _identity!;
            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = messageType,
                SessionId = resolved.SessionId,
                RoomId = resolved.RoomId,
                SeatId = resolved.Seat,
                BindingEpoch = 1
            };
            if (correlationId != null)
            {
                envelope.CorrelationId = correlationId;
            }

            switch (messageType)
            {
                case MessageType.Welcome: envelope.Welcome = (Welcome)body; break;
                case MessageType.Receipt: envelope.Receipt = (Contracts.Wire.Receipt)body; break;
                case MessageType.SyncResponse: envelope.SyncResponse = (SyncResponse)body; break;
                case MessageType.ReceiptLookup: envelope.ReceiptLookup = (ReceiptLookup)body; break;
                case MessageType.Error: envelope.Error = (ProtocolError)body; break;
                case MessageType.Close: envelope.Close = (Close)body; break;
                case MessageType.Heartbeat: envelope.Heartbeat = (Heartbeat)body; break;
                default: throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported reply body.");
            }

            return envelope;
        }

        private Envelope BuildWelcome(
            TableConnectionIdentity identity,
            RoomHost host,
            TableScopeRecord scope,
            TableStreamControlResult subscribed)
        {
            Contracts.StreamCursor cursor = subscribed.Cursor ??
                new Contracts.StreamCursor(new StreamVersion(0), new ProjectionSeq(0));
            var welcome = new Welcome
            {
                SessionId = identity.SessionId,
                RoomId = identity.RoomId,
                TableId = identity.RoomId,
                Role = identity.IsPlayer ? Contracts.Wire.Role.Player : Contracts.Wire.Role.Spectator,
                SeatId = identity.Seat,
                ConnectionEpoch = (ulong)_connectionEpoch,
                OwnerEpoch = host.OwnerEpoch.Value,
                BindingEpoch = 1,
                Scope = new Contracts.Wire.RequestScope
                {
                    ScopeId = scope.ScopeId.Value,
                    AccountId = scope.AccountId,
                    Role = identity.IsPlayer ? Contracts.Wire.Role.Player : Contracts.Wire.Role.Spectator,
                    RoomId = scope.RoomId ?? identity.RoomId,
                    IssuedAtMs = (ulong)identity.ScopeIssuedAtMs,
                    ExpiresAtMs = (ulong)identity.ScopeExpiresAtMs
                },
                Limits = new ServerLimits
                {
                    MaxInboundMessageBytes = (uint)_options.MaxInboundMessageBytes,
                    MaxCommandPayloadBytes = (uint)_options.MaxCommandPayloadBytes,
                    MaxSnapshotBytes = (uint)_options.MaxSnapshotBytes,
                    MaxInFlightRequests = (uint)_options.MaxInFlightRequests,
                    HeartbeatIntervalMs = (uint)_options.HeartbeatIntervalMs,
                    IdleDeadMs = (uint)_options.IdleDeadMs,
                    MaxEarlyBufferItems = (uint)_options.MaxEarlyBufferItems,
                    MaxEarlyBufferBytes = (ulong)_options.MaxEarlyBufferBytes
                },
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                Baseline = new Contracts.Wire.StreamBaseline
                {
                    StateVersion = (ulong)subscribed.StateVersion.Value,
                    EventSeq = 0,
                    Cursor = ProjectionWireMapper.MapCursor(cursor)
                }
            };
            return BuildEnvelope(MessageType.Welcome, welcome, correlationId: null, identity);
        }

        private void EnqueueError(string code, string fieldPath, string detailCode, bool retryable = false, uint? retryAfterMs = null)
        {
            var error = new ProtocolError
            {
                Code = code,
                Retryable = retryable,
                FieldPath = fieldPath,
                DetailCode = detailCode
            };
            if (retryAfterMs.HasValue)
            {
                error.RetryAfterMs = retryAfterMs.Value;
            }

            Enqueue(BuildEnvelope(MessageType.Error, error, correlationId: null));
        }

        private Envelope BuildReceipt(Envelope request, Contracts.Wire.Receipt receipt)
        {
            Envelope reply = BuildEnvelope(MessageType.Receipt, receipt, correlationId: request.CorrelationId);
            reply.RequestId = request.RequestId;
            return reply;
        }

        private Envelope BuildControlReceipt(Envelope request, Contracts.Wire.ReceiptStatus status, string reasonCode, ulong evaluatedStateVersion)
        {
            var receipt = new Contracts.Wire.Receipt
            {
                Status = status,
                ReasonCode = reasonCode,
                EvaluatedStateVersion = evaluatedStateVersion
            };
            return BuildReceipt(request, receipt);
        }

        private static Envelope BuildHeartbeatPing()
        {
            string nonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
            var heartbeat = new Heartbeat { Ping = new Ping { Nonce = nonce } };
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Heartbeat,
                Heartbeat = heartbeat
            };
        }

        private Envelope BuildHeartbeatPong(string nonce)
        {
            if (_identity == null)
            {
                return new Envelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = MessageType.Heartbeat,
                    Heartbeat = new Heartbeat { Pong = new Pong { Nonce = nonce } }
                };
            }

            return BuildEnvelope(MessageType.Heartbeat, new Heartbeat { Pong = new Pong { Nonce = nonce } }, correlationId: null);
        }

        private void Enqueue(Envelope envelope)
        {
            if (_closeRequested)
            {
                // A close decision is authoritative: nothing may follow it on the wire
                // (a post-CloseOutput send would fault the sender).
                return;
            }

            WireCodec.EnsureStrict(envelope);
            if (!_outbound.Writer.TryWrite(new OutboundItem(WireCodec.Encode(envelope))))
            {
                // Outbound bound exceeded: the client cannot keep up even with the reply path;
                // drop the connection rather than queue unbounded.
                EnqueueClose(Directive(ProtocolErrorCode.SlowConsumer));
            }
        }

        private void EnqueueClose(CloseDirective directive)
        {
            if (_closeRequested)
            {
                return;
            }

            lock (_closeGate)
            {
                if (_closeRequested)
                {
                    return;
                }

                // The pending slot is the delivery guarantee: the sentinel TryWrite can fail
                // on a channel already saturated by a stalled reader (256 frames deep), and
                // every later Enqueue no-ops once _closeRequested is set — without the slot
                // the close would sit behind a backlog the peer only starts reading after it
                // began waiting for the close itself.
                _pendingClose = directive;
                _closeRequested = true;
                _outbound.Writer.TryWrite(new OutboundItem(directive));
            }
        }

        /// <summary>Takes the reserved close directive, if one is waiting to be sent.</summary>
        private CloseDirective? TakePendingClose()
        {
            lock (_closeGate)
            {
                CloseDirective? pending = _pendingClose;
                _pendingClose = null;
                return pending;
            }
        }

        private async Task SendCloseAndCloseAsync(CloseDirective directive, CancellationToken stopping)
        {
            if (Interlocked.Exchange(ref _closeSent, 1) != 0)
            {
                return;
            }

            _closeRequested = true;
            try
            {
                // One shared grace budget for the whole close handshake (envelope + transport
                // CloseOutput): the decision must reach the peer even when the outbound
                // pipeline is stuffed and a trickle reader reopens the TCP window only at
                // its read rate. A too-short cap silently loses the handshake and leaves the
                // client an abrupt reset instead of the mapped close code; a peer that never
                // drains still ends at the budget.
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                grace.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(500, _options.CloseGraceMs)));
                if (_identity != null)
                {
                    var close = new Close
                    {
                        Code = directive.Code,
                        Permanent = directive.Permanent || ProtocolErrorCode.IsPermanentClose(directive.Code),
                        WebsocketCloseCode = (uint)directive.WebSocketCloseCode
                    };
                    Envelope envelope = BuildEnvelope(MessageType.Close, close, correlationId: null);
                    WireCodec.EnsureStrict(envelope);
                    await _ws.SendAsync(
                        WireCodec.Encode(envelope),
                        WebSocketMessageType.Binary,
                        WebSocketMessageFlags.EndOfMessage,
                        grace.Token).ConfigureAwait(false);
                }

                await _ws.CloseOutputAsync(
                    (WebSocketCloseStatus)directive.WebSocketCloseCode,
                    directive.Code,
                    grace.Token).ConfigureAwait(false);
                _closeOutputSent = true;
            }
            catch (Exception)
            {
                // The socket may already be gone; the close decision was still authoritative.
            }
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        private static CloseDirective Directive(string code)
        {
            return new CloseDirective(
                code,
                ProtocolErrorCode.IsPermanentClose(code),
                (int)ProtocolErrorCode.ToWebSocketCloseCode(code));
        }

        private bool ConsumeRateToken()
        {
            lock (_rateGate)
            {
                long now = Environment.TickCount64;
                if (_lastRefillTicks < 0)
                {
                    _lastRefillTicks = now;
                }

                double refillSeconds = (now - _lastRefillTicks) / 1000.0;
                if (refillSeconds > 0)
                {
                    _tokens = Math.Min(_options.CommandBurstCapacity, _tokens + refillSeconds * _options.CommandRatePerSecond);
                    _lastRefillTicks = now;
                }

                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }

        private static bool IsHeartbeatNonce(string nonce)
        {
            if (nonce.Length == 0 || nonce.Length > WireLimits.MaxHeartbeatNonceChars)
            {
                return false;
            }

            foreach (char c in nonce)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReasonOrFallback(string reasonCode)
        {
            return ProtocolErrorCode.IsWellFormed(reasonCode) ? reasonCode : ProtocolErrorCode.InternalError;
        }

        private static string Short(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private void LogRedacted(string message, params string?[] pairs)
        {
            if (!_logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            var state = new List<KeyValuePair<string, object?>>(pairs.Length / 2);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                state.Add(new KeyValuePair<string, object?>(pairs[i]!, pairs[i + 1]));
            }

            _logger.Log(LogLevel.Information, default, state, null, (_, _) => message);
        }
    }
}

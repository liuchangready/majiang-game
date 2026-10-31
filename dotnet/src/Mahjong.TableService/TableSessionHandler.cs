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
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Microsoft.Extensions.Logging;


namespace Mahjong.TableService
{
    /// <summary>
    /// One /v1/table WebSocket session. Owns the V5 framing discipline (one complete binary
    /// message = one Envelope, fragments accumulated to EndOfMessage, oversize/concat/text
    /// refused, duplicated envelope-level single-value fields detected as merged message
    /// boundaries at every phase while unknown fields stay compatible), the
    /// HELLO/WELCOME admission flow as a two-phase admission transaction (provisional
    /// consumption/subscription and a single final commit where ?????? displacement happens),
    /// serialized command routing through the per-room actor, heartbeat/idle liveness, drain
    /// awareness and the frozen close-code map. Every socket write is serialized through the
    /// send loop: a close decision never races a frame send, and a pending inbound receive is
    /// always owned, bounded in its timeout and released with its budget. Logging is redacted:
    /// ticket strings, payload bytes and full identifiers never reach logs.
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

        /// <summary>Tickets for the uncompromised WELCOME-send failure rollback (D-1-03).
        /// Set between seat-handover commit and WELCOME delivery; cleared on success.</summary>
                private volatile bool _welcomeSent;

        /// <summary>D-1-10: the scope a failed admission must revoke. Set when the scope is
        /// issued, cleared once the admission genuinely succeeded.</summary>
        private RequestScopeId? _issuedScopeId;

        // Outbound: serialized byte writes + terminal close sentinel. The queued BYTE volume
        // (receipts, errors, heartbeats and SYNC_RESPONSE baselines) is bounded in addition to
        // the frame count: a client that stops reading is closed as a slow consumer instead of
        // accumulating megabytes without a ledger (D-1-07).
        /// <summary>
        /// Wakes the sender wait loops and the liveness timer without touching the socket:
        /// cancelling a parked WebSocket READ (which shares the linked token) aborts the
        /// transport and would lose a still-queued close, so the lifecycle child wake is a
        /// SEPARATE token from the receiver token.
        /// </summary>
        private readonly CancellationTokenSource _lifecycleWake = new();

        /// <summary>
        /// The receiver-linked CTS of the supervised lifecycle, hoisted to a field because
        /// the terminal teardown finally sits outside its using scope. An ABANDONED parked
        /// read shares this token: cancelling it while the read is still pending aborts the
        /// transport and destroys a close handshake the peer has not consumed yet, so the
        /// cancel is deferred until after the terminal close delivery + drain budget.
        /// </summary>
        private CancellationTokenSource? _receiveLinked;

        /// <summary>
        /// The supervised receive-loop task, hoisted for the same reason: the peer's close
        /// echo is consumed by its parked read (a second concurrent ReceiveAsync is
        /// illegal), so the close-grace window waits on THIS task before the transport is
        /// released.
        /// </summary>
        private Task? _receiveLoop;

        private readonly Channel<OutboundItem> _outbound =
            Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        private readonly object _outboundBytesGate = new object();
        private int _outboundBytes;

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

        /// <summary>The CLOSE envelope was placed on the wire successfully (D-1-05).</summary>
        private int _closeEnvelopeSent;

        /// <summary>The last authoritative close decision, retained for the failure-safe flush.</summary>
        private CloseDirective? _lastCloseDirective;

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
                // Manual ownership (never `using`): the terminal teardown below must still
                // be able to CANCEL the receiver token after the close-grace window — a
                // using-scoped source is disposed when the try block ends, and cancelling
                // a disposed source throws.
                CancellationTokenSource linked = _receiveLinked =
                    CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
                CancellationTokenSource wake = CancellationTokenSource.CreateLinkedTokenSource(
                    linked.Token, _lifecycleWake.Token);
                try
                {
                    Task sender = SendLoopAsync(wake.Token);
                    Task liveness = LivenessLoopAsync(wake.Token);
                    Task receive = _receiveLoop = ReceiveLoopAsync(linked);
                    try
                    {
                        // Supervised post-admission lifecycle (D-1-05): whichever child ends
                        // first owns the end of the session; no child can linger unobserved, and
                        // no mid-send cancellation may destroy a close decision that still had to
                        // be delivered.
                        //
                        // CRITICAL: a pending socket READ must never be released through token
                        // cancellation early: cancelling a parked WebSocket ReceiveAsync ABORTS
                        // the underlying transport in the managed stack, which destroys a still
                        // undelivered close handshake. The pending receive is instead waited for
                        // (bounded) and, when still parked, ABANDONED while its eventual
                        // completion is observed quietly; the teardown's own abort releases it.
                        Task ended = await Task.WhenAny(sender, liveness, receive).ConfigureAwait(false);
                        if (ended != receive)
                        {
                            // The sender/liveness ended first (its fault reserved a close): give
                            // the sender a bounded window to deliver the queued/reserved close
                            // itself; its own per-send budget guarantees the exit.
                            if (!await WaitTaskDoneAsync(
                                    sender,
                                    TimeSpan.FromMilliseconds(SendBudgetMs() + 500)).ConfigureAwait(false))
                            {
                                LogRedacted("sender lingered past close budget");
                            }

                            // The parked read is abandoned IMMEDIATELY when the close decision is
                            // already in delivery: no extra wait window is allowed to stretch the
                            // session past the close budgets (the peer's own close answer, when
                            // any, is read by the same parked task or by the teardown's bounded
                            // drain; the abort releases everything else).
                            if (!receive.IsCompleted)
                            {
                                LogRedacted("receive abandoned parked");
                                _ = ObserveChildFiredAsync(receive);
                            }
                            else
                            {
                                await ObserveChildAsync(receive).ConfigureAwait(false);
                            }

                            _lifecycleWake.Cancel();
                            await ObserveChildAsync(sender).ConfigureAwait(false);
                            await ObserveChildAsync(liveness).ConfigureAwait(false);
                        }
                        else
                        {
                            // The receive loop ended: give the sender a bounded window to deliver
                            // the queued/reserved close itself (bounded by its own consistent
                            // per-send budget), so no mid-send cancellation is ever needed.
                            if (!await WaitTaskDoneAsync(
                                    sender,
                                    TimeSpan.FromMilliseconds(SendBudgetMs() + 500)).ConfigureAwait(false))
                            {
                                LogRedacted("sender lingered past close budget");
                            }

                            _lifecycleWake.Cancel();
                            await ObserveChildAsync(sender).ConfigureAwait(false);
                            await ObserveChildAsync(liveness).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        // Everything above ended through its own budgets and gates; the
                        // children are released WITHOUT cancelling the receiver token here:
                        // an abandoned parked read still shares it, and cancelling a pending
                        // read aborts the transport before the terminal close delivery +
                        // drain below ever ran (measured loss: the peer's buffered close
                        // frames destroyed by the abort RST). A COMPLETED receive has no
                        // pending read to lose, so its token cancels eagerly; a parked one
                        // is released in the teardown after the drain budget.
                        if (receive.IsCompleted)
                        {
                            linked.Cancel();
                        }
                    }

                    // Whatever path ended the receive loop (incl. a close the sender could not
                    // drain because the socket died), the socket now has exactly one owner for
                    // writes: the terminal close delivery is the LAST attempt, never a race.
                    await FlushTerminalCloseAsync().ConfigureAwait(false);
                }
                finally
                {
                    // The supervised children are ended and observed above; the wake source
                    // (their token hub) dies with this scope. The receiver source outlives
                    // it — the teardown below still cancels it after the grace window.
                    wake.Dispose();
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
            catch (OperationCanceledException)
            {
                // Server-side cancellation of an in-flight send/stop: the close handshake
                // cannot be delivered anymore; teardown below still runs.
            }
            finally
            {
                await TeardownAsync().ConfigureAwait(false);
                // The peer's close echo (when the peer sends one) is consumed by the
                // session's OWN parked read — a second concurrent ReceiveAsync on the
                // same socket is illegal, so the classic drain below can only run once
                // that read is done. While the read lives, the peer keeps the full
                // close-grace budget to consume the delivered close frames (and echo);
                // releasing the transport through a cancel here would RST-destroy the
                // buffered close frames a loaded peer has not read yet (measured: the
                // displaced session's 4403 arrived as a bare connection end).
                Task? parkedReceive = _receiveLoop;
                if (_closeOutputSent && parkedReceive != null && !parkedReceive.IsCompleted)
                {
                    await WaitTaskDoneAsync(
                        parkedReceive,
                        TimeSpan.FromMilliseconds(Math.Max(500, _options.CloseGraceMs)))
                        .ConfigureAwait(false);
                }

                await DrainCloseHandshakeAsync().ConfigureAwait(false);
                // The grace window above is over: whatever read is STILL parked now rides
                // the receiver token's release (its completion stays observed by the
                // quiet observer hooked at the abandonment site), and the drain/abort
                // sequence finishes the socket with no close handshake left to lose.
                CancellationTokenSource? receiverLinked = _receiveLinked;
                _receiveLinked = null;
                receiverLinked?.Cancel();
                receiverLinked?.Dispose();
                // Unified bounded shutdown: no leftover socket + pending tasks outlive this
                // session. Abort is safe on every state and is a no-op after a complete
                // handshake. The accepted socket's disposal belongs to the same ownership
                // block that owns its entire lifecycle.
                try
                {
                    if (_ws.State != WebSocketState.Closed)
                    {
                        _ws.Abort();
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    _ws.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Upper bound for ONE socket write of this session, including the terminal close: a
        /// peer that never drains releases the session on this budget instead of wedging the
        /// whole lifecycle forever (D-1-05 consistent termination budget).
        /// </summary>
        private int SendBudgetMs()
        {
            return Math.Max(500, _options.CloseGraceMs);
        }

        /// <summary>Awaits a task's completion or the deadline. True = completed in time.</summary>
        private static async Task<bool> WaitTaskDoneAsync(Task task, TimeSpan budget)
        {
            if (task.IsCompleted)
            {
                return true;
            }

            Task won = await Task.WhenAny(task, Task.Delay(budget)).ConfigureAwait(false);
            return won == task;
        }

        /// <summary>Awaits a send/liveness child to completion; transport faults propagate as
        /// the normal abrupt-peer end, cancellation ends silently.</summary>
        private static async Task ObserveChildAsync(Task child)
        {
            try
            {
                await child.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is WebSocketException ||
                exception is System.IO.IOException ||
                exception is ObjectDisposedException ||
                exception is OperationCanceledException ||
                exception is SessionFaultException)
            {
                // drained above: the socket is dead or stopping; the socket write owner is
                // free again. Everything unusual still bubbles to the main catch.
            }
        }

        /// <summary>
        /// Observes an ABANDONED child's eventual completion without waiting for it now (the
        /// receive stays parked on a silent peer): the teardown's own abort releases it and
        /// this quiet observer swallows the transport noise so nothing stays unobserved.
        /// </summary>
        private static async Task ObserveChildFiredAsync(Task child)
        {
            try
            {
                await child.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The abandoned task's transport is being torn down; nothing else to do.
            }
        }
        /// <summary>
        /// Half-close drain. Once this session has sent its transport close, the peer must
        /// get the chance to actually complete the handshake: a slow or stalled reader
        /// reaches the close bytes only at its own read rate, and returning immediately
        /// lets the host abort the connection ???destroying the close bytes still sitting in
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
                // Peer gone, or the grace expired ???the transport abort is then the honest
                // end for a peer that never completed the handshake it was given.
            }
        }

        private async Task TeardownAsync()
        {
            LogRedacted("teardown enter state=" + _ws.State + " closeOut=" + _closeOutputSent);
            TableConnectionIdentity? identity = _identity;
            RoomHost? host = _host;
            TableStreamHandle? handle = _handle;
            if (!_welcomeSent && _issuedScopeId.HasValue && host != null)
            {
                // D-1-10 / D-1-03: an admission that failed before its WELCOME revokes the
                // scope it consumed instead of leaving a still-valid side-effect surface for
                // the failed identity. Best effort; the scope expires with the ticket anyway.
                try
                {
                    await host.Scopes.RevokeScopeAsync(
                        _issuedScopeId.Value,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                _issuedScopeId = null;
            }

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
                // The seat-disconnect fence is the CONSUMPTION OWNERSHIP (D-1-08): only the
                // session that still owns the seat binding at teardown issues
                // TRANSPORT_CLOSED, and it disconnects with the seat's CURRENT authoritative
                // control epoch (never a constant zero). A displaced session skips the
                // disconnect so the handover never puts the seat into grace, and an old
                // displaced connection can never disconnect a session that took the seat
                // over. The rollback below is unconditional and idempotent: it finalizes the
                // jti/assignment records, frees the seat/spectator resources and marks the
                // seat handable.
                if (identity.IsPlayer &&
                    host != null &&
                    !_displaced &&
                    _consumption.TryGetSeatOwner(identity.RoomId, identity.Seat, out string? seatOwner) &&
                    string.Equals(seatOwner, identity.SessionId, StringComparison.Ordinal))
                {
                    ControlAuthority? seatAuthority = await TryGetSeatAuthorityAsync(host, identity.Seat).ConfigureAwait(false);
                    if (seatAuthority != null && seatAuthority.Value.Connected)
                    {
                        try
                        {
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await host.Actor.DisconnectAsync(
                                identity.Seat,
                                host.OwnerEpoch,
                                new ControlEpoch((ulong)seatAuthority.Value.ControlEpoch),
                                "TRANSPORT_CLOSED",
                                timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // P3.3 grace policy governs the seat; a failed notify must not throw
                            // out of connection teardown.
                        }
                    }
                }

                _consumption.Release(identity.SessionId);

                LogRedacted("session closed", "session", _sessionShort, "room", identity.RoomId);
            }
        }

        private readonly struct ControlAuthority
        {
            public ControlAuthority(long controlEpoch, bool connected)
            {
                ControlEpoch = controlEpoch;
                Connected = connected;
            }

            public long ControlEpoch { get; }

            public bool Connected { get; }
        }

        /// <summary>Reads the seat's current authoritative control epoch best-effort.</summary>
        private static async Task<ControlAuthority?> TryGetSeatAuthorityAsync(RoomHost host, int seat)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                TableControlSnapshot control = await host.Actor
                    .GetControlSnapshotAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (seat < 0 || seat >= control.Seats.Count)
                {
                    return null;
                }

                TableSeatControlView state = control.Seats[seat];
                return new ControlAuthority(state.ControlEpoch, state.IsConnected);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task SendLoopAsync(CancellationToken stopping)
        {
            // Merged drain of the reply channel and (after admission) the actor stream channel.
            // Both waits are held as Tasks so the loser of a WhenAny race stays awaitable ???            // a ValueTask must never be consumed twice.
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
                    // concurrent reservation only while both channels are empty ???and then the
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

                            // D-1-07: the byte reservation of a dequeued stream frame stays in
                            // the sink's budget until the actual transport write finished; the
                            // release path covers success, fault and cancellation alike.
                            while (_sink!.Reader.TryRead(out WireStreamFrameLease? lease))
                            {
                                try
                                {
                                    await SendFrameOrFailAsync(
                                        lease.Payload,
                                        WebSocketMessageType.Binary,
                                        stopping).ConfigureAwait(false);
                                }
                                finally
                                {
                                    lease.Release();
                                }
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

        /// <summary>Drains currently-queued reply items. True when the connection must end.
        /// D-1-07: a reply's byte reservation is dropped from the ledger only after its actual
        /// send (success, fault, cancellation), not when it was dequeued.</summary>
        private async Task<bool> DrainRepliesAsync(CancellationToken stopping)
        {
            while (_outbound.Reader.TryRead(out OutboundItem? item))
            {
                if (item.Close != null)
                {
                    await SendCloseAndCloseAsync(item.Close.Value, stopping).ConfigureAwait(false);
                    return true;
                }

                try
                {
                    await SendFrameOrFailAsync(
                        item.Bytes!,
                        WebSocketMessageType.Binary,
                        stopping).ConfigureAwait(false);
                }
                finally
                {
                    ReportRepliedBytes(item);
                }
            }

            return false;
        }

        /// <summary>
        /// Single serialized writer for ONE WebSocket frame with the session's consistent
        /// terminate budget: a peer that never drains cannot wedge the session forever even if
        /// the actor/stream backpressure chain has not closed yet (D-1-05). The budget engages
        /// as a LAST resort — normally the bounded queues + actor slow-consumer fence fire
        /// long before this.
        /// </summary>
        private async Task SendFrameOrFailAsync(byte[] frame, WebSocketMessageType type, CancellationToken stopping)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            budget.CancelAfter(TimeSpan.FromMilliseconds(SendBudgetMs()));
            try
            {
                await _ws.SendAsync(
                    frame,
                    type,
                    WebSocketMessageFlags.EndOfMessage,
                    budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                // The write budget expired without any peer progress: a session that cannot
                // push ONE frame within the termination budget cannot receive anything more.
                LogRedacted("write budget expired");
                throw new SessionFaultException(Directive(ProtocolErrorCode.SlowConsumer));
            }
        }

        /// <summary>
        /// Post-lifecycle flush (D-1-05): delivers a close decision its own writers could
        /// not deliver. Runs only after the sender lifecycle has fully completed, so no other
        /// socket writer exists. The last authoritative decision is retained exactly for this
        /// path — a close taken away by a cancelled/budget-exhausted writer is retried here
        /// ONCE against the same decision instead of being lost.
        /// </summary>
        private async Task FlushTerminalCloseAsync()
        {
            CloseDirective? reserved = TakePendingClose();
            if (!reserved.HasValue)
            {
                reserved = _lastCloseDirective;
            }

            if (!reserved.HasValue)
            {
                return;
            }

            await SendCloseAndCloseAsync(reserved.Value, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Actor-side stream death (slow consumer, stale fence, actor fault).</summary>
        private void OnStreamTerminatedFromActor(TableStreamTermination termination)
        {
            // The kind/reason pair is a redacted internal classification (never a ticket,
            // payload or full identifier), so it is safe on the single-line console log.
            LogRedacted("stream terminated " + termination.Kind + " " + termination.ReasonCode);
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

        /// <summary>??????: a newer valid ticket consumption re-bound this session's seat.</summary>
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
        // Admission (HELLO -> WELCOME) as a two-phase admission transaction
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

            // 6/7. Room runtime binding BEFORE the consumption transaction: a capacity,
            // lease or runtime-source failure consumes nothing and evicts nobody (D-1-10).
            string sessionId = Guid.NewGuid().ToString("N");
            _sessionShort = Short(sessionId);
            RoomHost host;
            try
            {
                // Durable admission is REAL async work (bootstrap/cold-recovery database
                // I/O): await it on the receive chain — never block this thread on it.
                host = await _hosts.GetOrAddAsync(_admissionRoom).ConfigureAwait(false);
            }
            catch (TableServiceCapacityException)
            {
                LogRedacted("admission refused", "code", "SERVER_BUSY", "room", _admissionRoom);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.ServerBusy));
            }

            if (host.AdmissionStopped)
            {
                // A lease-lost/faulted/closed table is mapped to the LeaseLost close family
                // (4503), and nothing about the ticket was consumed or evicted.
                LogRedacted(
                    "admission refused",
                    "code", string.IsNullOrEmpty(host.AdmissionsStoppedReason) ? "LEASE_LOST" : "LEASE_LOST",
                    "room", _admissionRoom);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.LeaseLost));
            }

            // D-1-10: the polled registry flag above is only a fast path. The AUTHORITATIVE
            // admission gate is the trusted actor's own lifecycle, read inside the same
            // admission transaction: an actor that entered LeaseLost/Faulted/Closing after the
            // last ticker observation but before this HELLO must refuse HERE (4503), never
            // after a ticket, a scope or a subscription was consumed into a dead table.
            TableSnapshot? authority = null;
            try
            {
                using var authorityTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var authorityLinked =
                    CancellationTokenSource.CreateLinkedTokenSource(stopping, authorityTimeout.Token);
                authority = await host.Actor.GetSnapshotAsync(authorityLinked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                authority = null;
            }
            catch (Exception)
            {
                authority = null;
            }

            if (authority == null || authority.Lifecycle != TableActorLifecycle.Serving)
            {
                string lifecycle = authority == null ? "ACTOR_UNAVAILABLE" : authority.Lifecycle.ToString();
                LogRedacted("admission refused", "code", "LEASE_LOST", "lifecycle", lifecycle, "room", _admissionRoom);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.LeaseLost));
            }

            // 6. One-time consumption + seat uniqueness reserves ???PROVISIONAL. Every refusal
            // here has zero side effects.
            TicketConsumptionOutcome consumed = _consumption.Consume(ticket, sessionId, DateTimeOffset.UtcNow);
            if (!consumed.IsAccepted)
            {
                LogRedacted("ticket consumption refused", "status", consumed.Status.ToString(), "room", _admissionRoom);
                throw new AdmissionClosedException(Directive(ProtocolErrorCode.InvalidToken));
            }

            // From here every failure rolls the transaction back through Release(sessionId).
            try
            {
                // Register BEFORE the commit (the displaced side must always be reachable by
                // the committing side ???the handover race the review pinned).
                _sessions.Register(sessionId, OnDisplacedByNewConnection);
                _registeredSessionId = sessionId;

                // 8. Server-issued request scope (expires with the ticket, never later).
                // P5.4b-A: issued through the room's narrow scope port, so a durable host
                // writes the same database row its commit path and receipt reconciliation
                // re-read. A durable issuance failure rolls the admission back below.
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long expiresMs = ticket.ExpiresAt.ToUnixTimeMilliseconds();
                var scopeRequest = new TableScopeRecord(
                    new RequestScopeId(Guid.NewGuid().ToString("N")),
                    ticket.AccountId,
                    ticket.Role == TableAdmissionRole.Player ? TableScopeRole.Player : TableScopeRole.Spectator,
                    _admissionRoom,
                    (ulong)nowMs,
                    (ulong)expiresMs);
                TableScopeRecord scope = await host.Scopes
                    .IssueScopeAsync(scopeRequest, stopping)
                    .ConfigureAwait(false);
                // D-1-10: recorded so a failed admission revokes instead of leaking a scope.
                _issuedScopeId = scope.ScopeId;

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
                _host = host;
                var sink = new WireTableStreamSink(
                    capacity: 64,
                    host.Options.MaxSnapshotBytes,
                    host.Options.MaxSnapshotBytes);
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
                    recoveryGeneration: host.RecoveryGeneration,
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
                    // Redacted operational identity only (status/reason/detail codes —
                    // never payloads): a faulted room pump must be diagnosable from the
                    // service log alone.
                    Console.Error.WriteLine(
                        "tablesvc: stream subscribe refused room=" + _admissionRoom +
                        " status=" + subscribed.Status +
                        " reason=" + subscribed.ReasonCode +
                        " detail=" + subscribed.Detail);
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
                    throw new AdmissionClosedException(Directive(TerminationCloseCode(sink.Termination.Kind)));
                }

                sink.Bind(subscribed.Handle);
                _sink = sink;
                _handle = subscribed.Handle;
                host.TrackSink(sink);

                // 6b. FINAL COMMIT: the seat/integrity handover linearization. It happens after
                // room capacity/ownership and the whole subscription are LIVE, so no failure
                // of THIS admission's own setup can evict an already-working connection and
                // only one connection passes the fence (D-1-03). The displacement of the
                // previous owner is deliberately NOT fired here: it only fires after this
                // session's WELCOME is actually on the wire, so a failed WELCOME can never
                // evict an old, healthily connected session — instead the seat handover is
                // rolled back atomically below.
                TableTicketCommitOutcome commit = _consumption.CommitAdmission(sessionId);
                if (commit.Status != TicketCommitStatus.Committed)
                {
                    LogRedacted(
                        "admission commit refused",
                        "status", commit.Status.ToString(),
                        "room", _admissionRoom);
                    throw new AdmissionClosedException(Directive(ProtocolErrorCode.InvalidToken));
                }

                // 10. WELCOME with its own bounded budget (D-1-05): a peer that stops reading
                // releases the admission instead of wedging the handler on the raw stopping
                // token.
                _welcomeSent = false;
                Envelope welcome = BuildWelcome(identity, host, scope, subscribed);
                using (var welcomeBudget = CancellationTokenSource.CreateLinkedTokenSource(stopping))
                {
                    welcomeBudget.CancelAfter(TimeSpan.FromMilliseconds(SendBudgetMs()));
                    try
                    {
                        await _ws.SendAsync(
                            WireCodec.Encode(welcome),
                            WebSocketMessageType.Binary,
                            WebSocketMessageFlags.EndOfMessage,
                            welcomeBudget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                    {
                        throw new AdmissionClosedException(Directive(ProtocolErrorCode.SlowConsumer));
                    }
                }

                _welcomeSent = true;
                _issuedScopeId = null;

                // Only after the WELCOME is delivered does THIS admission resolve the seat
                // handover (D-1-03 round 5): EVICT-ON-WELCOME is the registry's own
                // authoritative decision, and exactly ONE live owner per seat survives it.
                // DisplacedOther takes the previous live connection down; SelfSuperseded
                // means a NEWER handover already delivered its own WELCOME — THIS late
                // connection lost the seat and must be closed (4403) instead of registered.
                if (identity.IsPlayer)
                {
                    TableEvictionDecision eviction = _consumption.EvictOnWelcome(sessionId);
                    if (eviction.Kind == TableEvictionKind.SelfSuperseded)
                    {
                        LogRedacted(
                            "welcome lost to a newer seat handover",
                            "room", _admissionRoom);
                        throw new AdmissionClosedException(
                            Directive(ProtocolErrorCode.DisplacedByNewConnection));
                    }

                    if (eviction.Kind == TableEvictionKind.DisplacedOther)
                    {
                        try
                        {
                            _sessions.Displace(eviction.SessionId!);
                        }
                        catch (Exception displacementFault)
                        {
                            LogRedacted(
                                "displacement failed",
                                "code", Short(displacementFault.GetType().Name),
                                "room", _admissionRoom);
                        }
                    }
                }

                // 8b. Seat handover bookkeeping through the CURRENT authority epoch (D-1-08):
                // a reconnecting player seat is registered as CONNECTED but never reclaims a
                // takeover automatically; the explicit claim is the only control-taking path.
                if (identity.IsPlayer)
                {
                    await RegisterSeatConnectionAsync(host, identity, stopping).ConfigureAwait(false);
                }

                LogRedacted(
                    "session admitted",
                    "room", _admissionRoom,
                    "role", identity.Role.ToString(),
                    "seat", identity.Seat.ToString(CultureInfo.InvariantCulture),
                    "kid", Short(ticket.Kid));
                return identity;
            }
            catch (Exception admissionFault)
            {
                if (admissionFault is not AdmissionClosedException)
                {
                    LogRedacted("admission failed", "code", Short(admissionFault.GetType().Name), "room", _admissionRoom);
                }

                // D-1-03: this admission committed its seat handover but never delivered its
                // WELCOME — the failure is a THIS-connection failure and must not evict the
                // previous owner. The registry removes THIS session's own not-yet-welcomed
                // chain entry, so the seat reverts to the last WELCOME-successful owner no
                // matter how newer/older commits interleaved; then this admission's resources
                // are released. Consumed-ticket evidence is still retained by the registry's
                // own release semantics (a failed path never re-enables a replay).
                if (!_welcomeSent)
                {
                    try
                    {
                        _consumption.RollbackSeatHandover(sessionId);
                    }
                    catch (Exception rollbackFault)
                    {
                        LogRedacted(
                            "seathandover rollback failed",
                            "code", Short(rollbackFault.GetType().Name),
                            "room", _admissionRoom);
                    }
                }

                // Any exception between the reservation and WELCOME rolls the whole
                // transaction back: no hanging seats, subscriptions or sessions.
                _consumption.Release(sessionId);
                throw;
            }
        }

        /// <summary>
        /// Registers the seat as CONNECTED at the actor without auto-reclaiming control: the
        /// explicit reconnection/claim flow is the only control-taking path (D-1-08). The
        /// registration is fenced by the actor's own current control epoch for that seat, so a
        /// stale/foreign seat is simply refused and never changes authority state.
        /// </summary>
        private async Task RegisterSeatConnectionAsync(
            RoomHost host,
            TableConnectionIdentity identity,
            CancellationToken stopping)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                TableControlSnapshot control = await host.Actor
                    .GetControlSnapshotAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (identity.Seat < 0 || identity.Seat >= control.Seats.Count)
                {
                    return;
                }

                TableSeatControlView seatState = control.Seats[identity.Seat];
                await host.Actor.ReconnectAsync(
                    identity.Seat,
                    host.OwnerEpoch,
                    new ControlEpoch((ulong)seatState.ControlEpoch),
                    "WS_ADMITTED",
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Registering network presence must never fail an admission; the seat's
                // control state is governed by the control flow itself.
            }
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
                    // Mid-session reads have NO read timeout of their own: a silent peer is
                    // closed by the liveness fence, and a supervised shutdown ends this loop
                    // through the linked token. Conflating that wake with a HANDSHAKE timeout
                    // would steal the reserved close code (D-1-05).
                    message = await ReadSingleMessageAsync(
                        CancellationToken.None,
                        stopping,
                        earlyPhase: false).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogRedacted("receive exit canceled closeRequested=" + _closeRequested);
                    return;
                }
                catch (SessionFaultException fault)
                {
                    LogRedacted("receive exit fault " + fault.Directive.Code);
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
                    // cleanly ???surfacing it would land as an unhandled application fault
                    // (Kestrel fail-level noise) on a routine client disconnect.
                    LogRedacted("receive exit " + transport.GetType().Name + " wsCode=" +
                        (transport is WebSocketException wse2 ? wse2.WebSocketErrorCode.ToString() : "-"));
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
            string? authoritativeRoundId = null;
            bool offerChoice = envelope.Command.Type == Contracts.Wire.CommandType.SubmitSleeveBloomChoice ||
                               envelope.Command.Type == Contracts.Wire.CommandType.SubmitSleeveBloomOffTurnChoice ||
                               envelope.Command.Type == Contracts.Wire.CommandType.SubmitBeamSwapChoice;
            if (offerChoice)
            {
                // D-1-02: the offer choices carry no round id in the frozen contract. The
                // round bound to the mapped envelope is resolved from the actor's own
                // authoritative snapshot at this trusted boundary; the actor still re-fences
                // everything it admits against its own live state.
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeout.Token);
                    TableSnapshot snapshot = await host.Actor
                        .GetSnapshotAsync(bounded.Token)
                        .ConfigureAwait(false);
                    authoritativeRoundId = snapshot.RoundId;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    EnqueueError(ProtocolErrorCode.InternalError, "command.round_id", "ROUND_AUTHORITY_UNAVAILABLE");
                    return false;
                }
            }

            TableCommandRoute route;
            try
            {
                // D-1-02: the trusted offer candidate context rides the connection's own sink
                // (identity-checked actor frames only), so a raw BeamSwap target slot resolves
                // through the actor's projected pending offer instead of a guessed range.
                WireTableStreamSink? sink = _sink;
                Func<uint, TableBeamSwapSlotResolution?>? resolver =
                    sink == null ? null : raw => sink.ResolveBeamSwapTargetSlot(raw);

                // D-1-01 (b): the trusted response-window eligibility source for discards and
                // riichi discards is the room's own decision state (the committed Domain round
                // at the command's expected state version).
                TableRoomDecisionState decisionState = host.DecisionState;
                Func<int, Mahjong.Domain.TileId, ulong, TableCommandRouteResponseEligibility?>? eligibility =
                    (seat, tile, expectedVersion) =>
                        decisionState.DeriveResponseEligibility(seat, tile, expectedVersion);
                // P5.4b-B2-T: an abortive draw is mapped only when the committed round proves
                // one of the frozen reasons. The actor/calculator re-validates it; an
                // unprovable action fails closed before the actor with zero side effects.
                Func<TableSettlementAction, int, SettlementAbortiveDrawReason?>? abortiveReason =
                    (settlementAction, seat) =>
                    {
                        if (settlementAction != TableSettlementAction.AbortiveDraw)
                        {
                            return null;
                        }

                        try
                        {
                            return TableServiceAbortiveDraw.TryDerive(
                                host.Actor.GetRoundStateForHost(), seat);
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    };
                route = TableCommandMapper.Map(
                    identity, envelope, host.OwnerEpoch, host.RecoveryGeneration, _options,
                    authoritativeRoundId, resolver, eligibility, abortiveReason);
            }
            catch (ResponseEligibilityUnavailableException)
            {
                EnqueueError(
                    ProtocolErrorCode.InternalError,
                    "command",
                    TableCommandMapper.ResponseEligibilityDetail,
                    retryable: true);
                return false;
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
            // B1-BLOCKER-01 (second root cause): the frozen Client keys its durable outbox by
            // Contract.CanonicalPayloadDigest (the domain-separated MJC1 idempotency digest),
            // not by a bare SHA-256 of the bytes. The wire receipt must echo THAT digest or
            // every APPLIED answer is resolved locally as a Conflict. The actor keeps its own
            // raw TablePayloadSha256 for durable idempotency; this is the wire-facing value.
            string wireDigestHex = CanonicalPayloadDigest.Compute(payloadBytes).ToHex();

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
            if (route is TableCommandRoute.Settlement settlement)
            {
                result = await host.Actor
                    .SubmitSettlementAsync(settlement.Request, stopping)
                    .ConfigureAwait(false);
            }
            else if (route is TableCommandRoute.BusinessCommand business)
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
                    Enqueue(BuildReceipt(envelope, TableCommandMapper.MapReceipt(result, wireDigestHex)));
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
            // is the actor's own stream frame ???the host never invents projections ???so the
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

            // D-1-07: the negotiated snapshot limit is executed on the sync path. The actor
            // already refused a frame above its own encode cap; this host-side bound is the
            // configured limit ???a baseline larger than the negotiated limit is never served
            // (mapped to an internal error instead of silently sending an over-limit
            // snapshot).
            if (baseline.EncodedPayload.Length > _options.MaxSnapshotBytes)
            {
                sink.DisarmFullSnapshotCapture();
                EnqueueError(ProtocolErrorCode.InternalError, "sync", "SNAPSHOT_TOO_LARGE");
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
            // D-1-07: the negotiated limits cover the SYNC reply as it is actually SENT — the
            // wrapped Envelope (baseline inside) is encoded once and counted against the reply
            // budget, so a wrapper that would exceed the channel budget is refused here with
            // an explicit error instead of silently replacing the connection with a close.
            byte[] replyBytes = WireCodec.Encode(reply);
            if (replyBytes.Length > _options.MaxReplyQueuedBytes)
            {
                sink.DisarmFullSnapshotCapture();
                EnqueueError(ProtocolErrorCode.InternalError, "sync", "SNAPSHOT_TOO_LARGE");
                return;
            }

            // The reply carries the baseline itself; the actor's own frame flows to the
            // client through the bounded sink as always (the frozen client ignores a
            // redundant full snapshot by cursor). No host-side duplicate copy is injected
            // here: the sink bound must stay the single trusted backpressure authority.
            EnqueueEncoded(replyBytes);
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

            if (!await IsAuthorizedReceiptScopeAsync(host, _identity!, scopeId, stopping).ConfigureAwait(false))
            {
                EnqueueError(ProtocolErrorCode.MalformedMessage, "receipt_query.request_scope_id", "SCOPE_MISMATCH");
                return;
            }

            // The presented scope is either the connection's own scope or a historical scope
            // of the SAME account in the SAME room/role; the actor re-fences the account
            // binding itself, so a foreign query stays a NotFound with zero leakage (D-1-09).
            ReceiptLookupResult lookup = await host.Actor.QueryReceiptAsync(
                _identity!.AccountId,
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

            // Echo the queried request id and correlation so the lookup answer keeps the
            // caller's identity even when it concerns a HISTORICAL scope. Deliberately no
            // Scope: the queried scope may be an old (possibly expired) admission scope, and
            // attaching this connection's current scope here would forge the old scope's
            // owner identity. The response carries the authoritative receipt payload only.
            Envelope reply = BuildEnvelope(
                MessageType.ReceiptLookup, wire, correlationId: envelope.CorrelationId);
            reply.RequestId = requestId.Value;
            Enqueue(reply);
        }

        /// <summary>
        /// D-1-09: a receipt query is authorized when the presented scope is the connection's
        /// own scope OR a historical scope owned by the SAME account in the SAME room with the
        /// SAME role (a reconnect on a fresh scope can still reconcile its own deterministic
        /// past request ids). Scope expiry does NOT revoke the lookup; Unknown/NotFound is an
        /// UNKNOWN-RESULT answer, never proof that the request is durably uncommitted and
        /// never a safe-redo basis — a client may retry its own action, but the service never
        /// derives "safe to redo" from an unknown lookup; a foreign/other-role/other-room
        /// scope is refused.
        /// </summary>
        private async Task<bool> IsAuthorizedReceiptScopeAsync(
            RoomHost host,
            TableConnectionIdentity identity,
            RequestScopeId scopeId,
            CancellationToken stopping)
        {
            if (string.Equals(scopeId.Value, identity.ScopeId, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                TableScopeLookup lookup = await host.Scopes.LookupAsync(scopeId, stopping).ConfigureAwait(false);
                if (!lookup.Found || lookup.Scope == null)
                {
                    return false;
                }

                TableScopeRecord scope = lookup.Scope;
                return string.Equals(scope.AccountId, identity.AccountId, StringComparison.Ordinal) &&
                       string.Equals(scope.RoomId, identity.RoomId, StringComparison.Ordinal) &&
                       scope.Role == (identity.IsPlayer ? TableScopeRole.Player : TableScopeRole.Spectator);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
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

        /// <summary>
        /// Reads ONE complete WebSocket message. The pending inbound receive is ALWAYS owned:
        /// it rides this method's own linked cancellation source, the timeout path completes
        /// its close handshake with a single bounded budget (output close + receive grace +
        /// bounded abort), and no task is left unobserved on any exit (D-1-05).
        /// </summary>
        private async Task<byte[]> ReadSingleMessageAsync(CancellationToken timeoutToken, CancellationToken stopping, bool earlyPhase)
        {
            byte[] buffer = new byte[16 * 1024];
            using var accumulator = new System.IO.MemoryStream();

            while (true)
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                ValueTask<ValueWebSocketReceiveResult> pending = _ws.ReceiveAsync(buffer.AsMemory(), receiveCts.Token);
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
                        // Timeout: complete the close handshake with ONE bounded budget and
                        // observe the pending receive within the SAME budget (bounded
                        // cancellation aborts the socket and releases the receive).
                        await CompleteTimeoutCloseAsync(
                            receiveTask,
                            receiveCts,
                            ProtocolErrorCode.HandshakeTimeout).ConfigureAwait(false);
                        throw new AdmissionClosedException(Directive(ProtocolErrorCode.HandshakeTimeout));
                    }

                    try
                    {
                        received = await receiveTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Session teardown cancelled the receive; the connection is already
                        // being closed by its owner.
                        throw new OperationCanceledException(receiveCts.Token);
                    }
                    // Any other receive fault (WebSocketException/IOException/??? is unwrapped
                    // by this await (never an AggregateException) and handled by the same
                    // transport filters as every other receive site.
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
        /// Closes the send side with the deadline's directive and gives the pending receive a
        /// BOUNDED completion budget: output close first (full grace), then the receive is
        /// cancelled at a bounded sub-budget and its completion is observed (never leaked).
        /// The close directive this way IS delivered, and no receive task outlives the call.
        /// </summary>
        private async Task CompleteTimeoutCloseAsync(
            Task<ValueWebSocketReceiveResult> pendingReceive,
            CancellationTokenSource receiveCts,
            string code)
        {
            long closeBudgetMs = Math.Max(500, _options.CloseGraceMs);
            try
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                grace.CancelAfter(TimeSpan.FromMilliseconds(closeBudgetMs));
                Interlocked.CompareExchange(ref _closeSent, 1, 0);
                await _ws.CloseOutputAsync(
                    (WebSocketCloseStatus)ProtocolErrorCode.ToWebSocketCloseCode(code),
                    code,
                    grace.Token).ConfigureAwait(false);
                _closeOutputSent = true;
            }
            catch (Exception)
            {
                // The peer may already be gone; the close decision was still authoritative.
            }
            finally
            {
                // The pending receive is owned and bounded: the abort releases it deterministically.
                try
                {
                    receiveCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(200, closeBudgetMs / 2)));
                    Task winner = await Task.WhenAny(
                        pendingReceive,
                        Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, closeBudgetMs)))).ConfigureAwait(false);
                    if (winner == pendingReceive)
                    {
                        await pendingReceive.ConfigureAwait(false);
                    }
                    else
                    {
                        // The receive never completes on its own (a silent peer holding the
                        // transport): the bounded abort is the honest end. The task's
                        // completion (fault/cancel) is observed so no task is leaked.
                        _ = pendingReceive.ContinueWith(
                            static (Task<ValueWebSocketReceiveResult> t, object? _) => { _ = t.Exception; },
                            (object?)null,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
                catch (Exception)
                {
                }
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

            // One WebSocket message = one Envelope ???at EVERY phase (D-1-04), verified on the
            // message boundary itself instead of a re-encode byte comparison:
            //  * two complete Envelopes in one binary message always duplicate at least one
            //    envelope-level single-value field (message_type/session_id/room_id/... or the
            //    body oneof itself) once protobuf merge consumed them ???that duplication is
            //    refused for HELLO/COMMAND/HEARTBEAT/SYNC/QUERY alike;
            //  * schema-legal future unknown fields (no duplicate of a known single-value
            //    field number) stay byte-compatible and are accepted in any position, so a
            //    client that sends a legal message with preserved unknown fields is never
            //    punished for its forward compatibility;
            //  * the Wire schema itself is never touched and nothing ever relies on the
            //    canonical re-encode of the parsed envelope being byte-identical (that would
            //    break legal field re-ordering with the same future fields).
            if (ContainsDuplicateKnownSingleValueField(message))
            {
                throw new SessionFaultException(Directive(ProtocolErrorCode.ProtocolViolation));
            }

            return decoded.Envelope;
        }

        /// <summary>Envelope top-level field numbers that can never be schema-legal twice.</summary>
        private static readonly HashSet<int> EnvelopeSingleValueFieldNumbers = new HashSet<int>
        {
            1, // protocol_major
            2, // protocol_minor
            3, // ruleset_version
            4, // projection_version
            5, // message_type
            6, // correlation_id
            7, // request_id
            8, // scope
            9, // room_id
            10, // session_id
            11, // seat_id
            12, // binding_epoch
            13, // control_epoch
            14, // owner_epoch
            15, // cursor
            16, // from_cursor
            40, // hello
            41, // welcome
            42, // command
            43, // receipt
            44, // player_snapshot
            45, // player_snapshot_query
            46, // public_snapshot
            47, // public_snapshot_query
            48, // room_update
            49, // sync_request
            50, // sync_response
            51, // receipt_query
            52, // receipt_lookup
            53, // heartbeat
            54, // error
            55, // close
            56, // player_update
            57 // public_update
        };

        /// <summary>
        /// Scans ONLY the top-level wire fields (each nested payload is skipped by its own
        /// declared length) and reports a violation when one of the single-value field
        /// numbers above appears twice.
        ///
        /// D-1-04 (round 5): the scan interprets tags EXACTLY like the frozen codec's
        /// ReadTag — an at-most-10-byte varint TRUNCATED to uint32, then field number and
        /// wire type split on that 32-bit value. The previous 64-bit interpretation let a
        /// high-varint tag (e.g. (1UL&lt;&lt;34)|(100UL&lt;&lt;3), which the codec truncates to the
        /// legal unknown field 100) cast to a NEGATIVE field number and short-circuit the
        /// whole scan as "no violation" — everything behind it, including the tail of a
        /// concatenated second Envelope, went unswept and the concatenation was accepted.
        ///
        /// Fail-closed framing: every path where this scan cannot honestly frame the message
        /// (malformed or overlong varint, tag 0, wire types 6/7, stray or unbalanced
        /// end-group) is a framing the frozen codec itself rejects — bytes that reach this
        /// scanner have already passed Decode, so hitting one here means the scan's remaining
        /// view cannot be trusted and the message is refused, never accepted as "scan
        /// complete, no duplicate". Schema-legal unknown GROUP fields are skipped completely
        /// and safely — iterative, O(1) state, NO artificial depth cap (each nesting level
        /// costs at least two wire bytes, so the depth is bounded by the frozen inbound
        /// message cap and the scan always reaches the real end of the message or a genuine
        /// framing error). Fields INSIDE a group are not top-level envelope fields and are
        /// not counted.
        /// </summary>
        private static bool ContainsDuplicateKnownSingleValueField(byte[] message)
        {
            var seen = new HashSet<int>();
            int index = 0;
            int groupDepth = 0;
            while (index < message.Length)
            {
                if (!TryReadVarint(message, ref index, out ulong rawTag))
                {
                    return true; // malformed or overlong varint: a codec-rejected framing
                }

                // Mirror the frozen codec's ReadTag exactly: 32-bit truncation FIRST, then
                // the field-number/wire-type split on that 32-bit value.
                uint tag = (uint)rawTag;
                if (tag == 0)
                {
                    return true; // the codec throws InvalidProtocolBufferException (invalid tag)
                }

                int fieldNumber = (int)(tag >> 3); // uint >> 3 is never negative
                int wireType = (int)(tag & 0x7);
                if (groupDepth > 0)
                {
                    // Inside an unknown group: nothing here is a top-level envelope field;
                    // only the group envelope itself may close it.
                    switch (wireType)
                    {
                        case 0:
                            if (!TrySkipVarint(message, ref index))
                            {
                                return true;
                            }

                            break;
                        case 1:
                            index += 8;
                            if (index > message.Length)
                            {
                                return true;
                            }

                            break;
                        case 2:
                            if (!TryReadVarint(message, ref index, out ulong innerLength) ||
                                innerLength > (ulong)(message.Length - index))
                            {
                                return true;
                            }

                            index += (int)innerLength;
                            break;
                        case 5:
                            index += 4;
                            if (index > message.Length)
                            {
                                return true;
                            }

                            break;
                        case 3:
                            groupDepth++;

                            // D-1-04: NO artificial depth cap. A nesting depth the scan refuses
                            // to follow is exactly the old fail-open: the scan stopped
                            // mid-message and everything behind it (including the tail of a
                            // concatenated second Envelope) went unswept. The walkthrough is
                            // iterative (no recursion, O(1) state), each extra nesting level
                            // costs at least two wire bytes, so the depth is bounded by the
                            // frozen inbound message cap and the scan ALWAYS reaches the real
                            // end of the message or hits a genuine framing error above.
                            break;
                        case 4:
                            groupDepth--;
                            if (groupDepth < 0)
                            {
                                return true; // unbalanced end group: a codec-rejected framing
                            }

                            break;
                        default: // 6, 7 are never legal on this wire
                            return true;
                    }

                    continue;
                }

                switch (wireType)
                {
                    case 0: // varint
                        if (!TrySkipVarint(message, ref index))
                        {
                            return true;
                        }

                        break;
                    case 1: // 64-bit
                        index += 8;
                        if (index > message.Length)
                        {
                            return true;
                        }

                        break;
                    case 2: // length-delimited
                        if (!TryReadVarint(message, ref index, out ulong length) ||
                            length > (ulong)(message.Length - index))
                        {
                            return true;
                        }

                        index += (int)length;
                        break;
                    case 5: // 32-bit
                        index += 4;
                        if (index > message.Length)
                        {
                            return true;
                        }

                        break;
                    case 3: // group start (schema-legal unknown field): tracked and skipped, no depth cap (D-1-04)
                        groupDepth++;
                        break;
                    case 4: // stray end group at top level: the frozen codec never accepts it
                        return true;
                    default: // 6, 7 are never legal on this wire
                        return true;
                }

                if (EnvelopeSingleValueFieldNumbers.Contains(fieldNumber) && !seen.Add(fieldNumber))
                {
                    return true;
                }
            }

            // The boolean answers "is this message a violation". An unclosed group never
            // survives the codec, so a sweep that ends inside one is reported as a framing
            // violation; only a clean full sweep with every group balanced honestly reports
            // "no duplicate".
            return groupDepth != 0;
        }

        private static bool TryReadVarint(byte[] bytes, ref int index, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (shift < 64 && index < bytes.Length)
            {
                byte b = bytes[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return shift < 64;
                }

                shift += 7;
            }

            return false;
        }

        private static bool TrySkipVarint(byte[] bytes, ref int index)
        {
            int shift = 0;
            while (shift < 64 && index < bytes.Length)
            {
                byte b = bytes[index++];
                if ((b & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
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

        private static string Short(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= 8 ? value : value.Substring(0, 8);
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
            // D-1-11 / B1-BLOCKER-01: the frozen Client resolves a receipt by the
            // server-issued request scope, and drops any receipt whose envelope has no
            // scope. Rebuild the scope from this connection's AUTHORITATIVE admission
            // identity (the same server-issued scope the request was fenced against), never
            // by aliasing the client's mutable request.Scope object. Account/role/room/times
            // therefore cannot drift or be forged through the echoed envelope.
            reply.Scope = BuildAuthoritativeScope(_identity!);
            return reply;
        }

        /// <summary>Builds a fresh wire scope from the connection's server-issued admission
        /// identity. The envelope owns an independent object; no client-supplied or shared
        /// mutable scope instance is exposed to the outbound queue.</summary>
        private static Contracts.Wire.RequestScope BuildAuthoritativeScope(TableConnectionIdentity identity)
        {
            return new Contracts.Wire.RequestScope
            {
                ScopeId = identity.ScopeId,
                AccountId = identity.AccountId,
                Role = identity.IsPlayer ? Contracts.Wire.Role.Player : Contracts.Wire.Role.Spectator,
                RoomId = identity.RoomId,
                IssuedAtMs = (ulong)identity.ScopeIssuedAtMs,
                ExpiresAtMs = (ulong)identity.ScopeExpiresAtMs
            };
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

        private Envelope BuildHeartbeatPing()
        {
            // B1 heartbeat binding: a server-generated PING is liveness control for the
            // CURRENT admitted connection, so it must carry the same authoritative
            // session/room/seat/binding identity as every other envelope. The frozen
            // Client rejects a bound heartbeat whose envelope does not match its WELCOME
            // (HeartbeatTableChannel.MatchesBinding); a bare envelope therefore faulted
            // every healthy session at the first server PING. Reuse the one envelope
            // builder instead of duplicating (and drifting from) the identity fields.
            string nonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
            var heartbeat = new Heartbeat { Ping = new Ping { Nonce = nonce } };
            return BuildEnvelope(MessageType.Heartbeat, heartbeat, correlationId: null);
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
            WireCodec.EnsureStrict(envelope);
            EnqueueEncoded(WireCodec.Encode(envelope));
        }

        /// <summary>Queues already-encoded reply bytes under the reply ledger (D-1-07).</summary>
        private void EnqueueEncoded(byte[] bytes)
        {
            if (_closeRequested)
            {
                // A close decision is authoritative: nothing may follow it on the wire
                // (a post-CloseOutput send would fault the sender).
                return;
            }

            // D-1-07: the reply queue carries a byte ledger as well as the frame bound. When
            // the negotiated reply budget would be exceeded, the connection is closed as a
            // slow consumer instead of growing without a limit; small normal traffic is
            // never affected.
            lock (_outboundBytesGate)
            {
                if (_outboundBytes + bytes.Length > _options.MaxReplyQueuedBytes)
                {
                    EnqueueCloseOutsideLedger(Directive(ProtocolErrorCode.SlowConsumer));
                    return;
                }

                _outboundBytes += bytes.Length;
                if (!_outbound.Writer.TryWrite(new OutboundItem(bytes)))
                {
                    _outboundBytes -= bytes.Length;
                    // Outbound bound exceeded: the client cannot keep up even with the reply path;
                    // drop the connection rather than queue unbounded.
                    EnqueueCloseOutsideLedger(Directive(ProtocolErrorCode.SlowConsumer));
                }
            }
        }

        /// <summary>Drops a reply's bytes from the ledger once the send loop took it.</summary>
        private void ReportRepliedBytes(OutboundItem item)
        {
            if (item.Bytes == null)
            {
                return;
            }

            lock (_outboundBytesGate)
            {
                _outboundBytes -= item.Bytes.Length;
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
                // every later Enqueue no-ops once _closeRequested is set ???without the slot
                // the close would sit behind a backlog the peer only starts reading after it
                // began waiting for the close itself.
                _pendingClose = directive;
                _lastCloseDirective = directive;
                _closeRequested = true;
                LogRedacted("close reserved " + directive.Code);
                _outbound.Writer.TryWrite(new OutboundItem(directive));
            }
        }

        /// <summary>
        /// Close reservation that does NOT touch the byte ledger (only used when the reply
        /// budget was just exceeded or the channel rejected an item); the close sentinel
        /// follows the reserved-slot guarantee.
        /// </summary>
        private void EnqueueCloseOutsideLedger(CloseDirective directive)
        {
            EnqueueClose(directive);
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
            Interlocked.Exchange(ref _closeSent, 1);
            // The last authoritative decision is retained for the failure-safe flush (D-1-05).
            _lastCloseDirective = directive;
            _closeRequested = true;
            LogRedacted("close sending " + directive.Code + " state=" + _ws.State + " closeOut=" + _closeOutputSent);
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
                if (_identity != null && Volatile.Read(ref _closeEnvelopeSent) == 0)
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
                    Volatile.Write(ref _closeEnvelopeSent, 1);
                }

                await _ws.CloseOutputAsync(
                    (WebSocketCloseStatus)directive.WebSocketCloseCode,
                    directive.Code,
                    grace.Token).ConfigureAwait(false);
                _closeOutputSent = true;
                LogRedacted("close delivered code=" + directive.Code);
            }
            catch (Exception transport) when (
                transport is WebSocketException ||
                transport is System.IO.IOException ||
                transport is OperationCanceledException ||
                transport is ObjectDisposedException)
            {
                // The socket may already be gone (or the close budget expired with a peer that
                // never drained); the close decision was still authoritative and stays
                // retained for the single post-lifecycle flush. A dead transport keeps the
                // send-side state machine from spinning.
                LogRedacted("close send fault " + transport.GetType().Name + (transport is WebSocketException wse ? " wsErr=" + wse.WebSocketErrorCode.ToString() : string.Empty));
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

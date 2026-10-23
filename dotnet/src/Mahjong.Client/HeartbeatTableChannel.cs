using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;

namespace Mahjong.Client
{
    /// <summary>Heartbeat policy before a WELCOME negotiates server values.</summary>
    public sealed class HeartbeatChannelOptions
    {
        public bool AutoTimer { get; set; } = true;

        public int DefaultHeartbeatIntervalMs { get; set; } = 10_000;

        public int DefaultIdleDeadMs { get; set; } = 30_000;

        public int MinimumHeartbeatIntervalMs { get; set; } = 50;

        public int MaxTimerSleepMs { get; set; } = 1_000;

        internal void Validate()
        {
            if (DefaultHeartbeatIntervalMs <= 0 || DefaultIdleDeadMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(DefaultHeartbeatIntervalMs), "Default heartbeat values must be positive.");
            }

            if (MinimumHeartbeatIntervalMs <= 0 || MaxTimerSleepMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumHeartbeatIntervalMs), "Heartbeat timer values must be positive.");
            }

            if (DefaultIdleDeadMs < DefaultHeartbeatIntervalMs)
            {
                throw new ArgumentOutOfRangeException(nameof(DefaultIdleDeadMs), "Idle deadline must not be shorter than the heartbeat interval.");
            }
        }
    }

    /// <summary>Read-only liveness view for diagnostics and tests.</summary>
    public sealed class HeartbeatSnapshot
    {
        public HeartbeatSnapshot(
            bool bound,
            int negotiatedIntervalMs,
            int negotiatedIdleDeadMs,
            string? pendingPingNonce,
            long? lastRoundTripMs,
            int pingSentCount,
            int pongReceivedCount,
            long lastInboundAgeMs,
            long lastPingAgeMs,
            bool timedOut)
        {
            Bound = bound;
            NegotiatedIntervalMs = negotiatedIntervalMs;
            NegotiatedIdleDeadMs = negotiatedIdleDeadMs;
            PendingPingNonce = pendingPingNonce;
            LastRoundTripMs = lastRoundTripMs;
            PingSentCount = pingSentCount;
            PongReceivedCount = pongReceivedCount;
            LastInboundAgeMs = lastInboundAgeMs;
            LastPingAgeMs = lastPingAgeMs;
            TimedOut = timedOut;
        }

        public bool Bound { get; }

        public int NegotiatedIntervalMs { get; }

        public int NegotiatedIdleDeadMs { get; }

        public string? PendingPingNonce { get; }

        public long? LastRoundTripMs { get; }

        public int PingSentCount { get; }

        public int PongReceivedCount { get; }

        public long LastInboundAgeMs { get; }

        public long LastPingAgeMs { get; }

        public bool TimedOut { get; }
    }

    /// <summary>
    /// Control-layer heartbeat decorator around the opaque table channel.
    ///
    /// It consumes only Wire HEARTBEAT envelopes, echoes valid 16-hex PING
    /// nonces, requires a matching PONG for an outstanding client PING, and
    /// enforces the WELCOME-negotiated interval/idle deadline. Heartbeats are
    /// rejected until a WELCOME establishes the binding and must carry the
    /// same session/room/seat/binding_epoch; they can never bridge a new
    /// binding. All other envelopes pass through untouched.
    /// </summary>
    public sealed class HeartbeatTableChannel : ITableChannel, IDisposable, IAsyncDisposable
    {
        private readonly ITableChannel _inner;
        private readonly ITableClientClock _clock;
        private readonly HeartbeatChannelOptions _options;
        private readonly SemaphoreSlim _openGate = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();

        private HeartbeatSession? _session;
        private bool _disposed;

        public HeartbeatTableChannel(ITableChannel inner, ITableClientClock? clock = null, HeartbeatChannelOptions? options = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _clock = clock ?? new SystemTableClientClock();
            _options = options ?? new HeartbeatChannelOptions();
            _options.Validate();
        }

        public bool IsOpen => _inner.IsOpen;

        public HeartbeatSnapshot? Snapshot
        {
            get
            {
                HeartbeatSession? session = Volatile.Read(ref _session);
                if (session == null)
                {
                    return null;
                }

                lock (session.Gate)
                {
                    return session.State.Snapshot(session.Bound, _clock.MonotonicMilliseconds, session.Terminal != null);
                }
            }
        }

        public async Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(HeartbeatTableChannel));
                }

                HeartbeatSession? previous;
                lock (_gate)
                {
                    previous = _session;
                    _session = null;
                }

                if (previous != null)
                {
                    previous.Cancel();
                }

                await _inner.OpenAsync(request, cancellationToken).ConfigureAwait(false);

                var session = new HeartbeatSession(_clock, _options);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        session.Cancel();
                        throw new ObjectDisposedException(nameof(HeartbeatTableChannel));
                    }

                    _session = session;
                }

                if (_options.AutoTimer)
                {
                    _ = Task.Run(() => RunTimerAsync(session));
                }
            }
            finally
            {
                _openGate.Release();
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            HeartbeatSession? session = Volatile.Read(ref _session);
            if (session == null || session.Terminal != null)
            {
                throw session?.Terminal ?? new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
            }

            try
            {
                await _inner.SendAsync(wireEnvelope, cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException ex)
            {
                Fail(session, ex.Code, ex.Permanent);
                throw;
            }
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            HeartbeatSession? session = Volatile.Read(ref _session);
            if (session == null)
            {
                throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cts.Token))
            {
                while (true)
                {
                    if (session.Terminal != null)
                    {
                        throw session.Terminal;
                    }

                    ReadOnlyMemory<byte> bytes;
                    try
                    {
                        bytes = await _inner.ReceiveAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }

                        throw session.Terminal ?? new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false);
                    }

                    if (!ReferenceEquals(Volatile.Read(ref _session), session))
                    {
                        throw new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false);
                    }

                    bool passThrough = await HandleInboundAsync(session, bytes, linked.Token).ConfigureAwait(false);
                    if (passThrough)
                    {
                        return bytes;
                    }
                }
            }
        }

        public async Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            HeartbeatSession? session = Volatile.Read(ref _session);
            if (session != null)
            {
                session.Cancel();
            }

            await _inner.CloseAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            HeartbeatSession? session;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                session = _session;
                _session = null;
            }

            if (session != null)
            {
                session.Cancel();
            }

            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            HeartbeatSession? session;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                session = _session;
                _session = null;
            }

            if (session != null)
            {
                session.Cancel();
            }

            if (_inner is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private async Task<bool> HandleInboundAsync(
            HeartbeatSession session,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            WireDecodeResult decoded;
            try
            {
                decoded = WireCodec.Decode(bytes.Span);
            }
            catch (WireProtocolException ex)
            {
                throw Fail(session, ex.Code, true);
            }

            Envelope? envelope = decoded.Envelope;
            if (envelope != null && envelope.BodyCase == Envelope.BodyOneofCase.Heartbeat)
            {
                if (!decoded.IsOk)
                {
                    throw Fail(session, decoded.ErrorCode, true);
                }

                await HandleHeartbeatAsync(session, envelope, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (envelope != null && envelope.BodyCase == Envelope.BodyOneofCase.Welcome)
            {
                AdoptWelcome(session, envelope);
            }

            long now = _clock.MonotonicMilliseconds;
            lock (session.Gate)
            {
                session.State.OnInbound(now);
            }

            return true;
        }

        private async Task HandleHeartbeatAsync(
            HeartbeatSession session,
            Envelope envelope,
            CancellationToken cancellationToken)
        {
            Heartbeat? heartbeat = envelope.Heartbeat;
            if (heartbeat == null)
            {
                throw Fail(session, ProtocolErrorCode.MalformedMessage, true);
            }

            lock (session.Gate)
            {
                if (!session.Bound)
                {
                    // A heartbeat before WELCOME cannot be associated with any
                    // session identity; accepting it would let control traffic
                    // bypass the binding handshake.
                    throw FailLocked(session, ProtocolErrorCode.ProtocolViolation, true);
                }

                if (!session.MatchesBinding(envelope))
                {
                    throw FailLocked(session, ProtocolErrorCode.ProtocolViolation, true);
                }
            }

            long now = _clock.MonotonicMilliseconds;
            switch (heartbeat.BeatCase)
            {
                case Heartbeat.BeatOneofCase.Ping:
                {
                    string nonce = heartbeat.Ping.Nonce;
                    string? pongNonce;
                    lock (session.Gate)
                    {
                        session.State.OnPing(nonce, now);
                        pongNonce = nonce;
                    }

                    await SendPongAsync(session, pongNonce, cancellationToken).ConfigureAwait(false);
                    return;
                }

                case Heartbeat.BeatOneofCase.Pong:
                {
                    bool matched;
                    lock (session.Gate)
                    {
                        matched = session.State.OnPong(heartbeat.Pong.Nonce, now);
                    }

                    if (!matched)
                    {
                        throw Fail(session, ProtocolErrorCode.ProtocolViolation, true);
                    }

                    return;
                }

                default:
                    throw Fail(session, ProtocolErrorCode.MalformedMessage, true);
            }
        }

        private async Task SendPongAsync(HeartbeatSession session, string nonce, CancellationToken cancellationToken)
        {
            Envelope envelope = session.NewHeartbeatEnvelope();
            envelope.Heartbeat = new Heartbeat { Pong = new Pong { Nonce = nonce } };
            byte[] encoded;
            try
            {
                encoded = WireCodec.Encode(envelope);
            }
            catch (WireProtocolException ex)
            {
                throw Fail(session, ex.Code, true);
            }

            try
            {
                await _inner.SendAsync(encoded, cancellationToken).ConfigureAwait(false);
            }
            catch (TableChannelClosedException ex)
            {
                Fail(session, ex.Code, ex.Permanent);
                throw;
            }
        }

        /// <summary>
        /// Test seam: evaluate heartbeat timers at the current clock value
        /// without waiting for a real timer tick. Returns the next suggested
        /// delay in milliseconds, or -1 when the heartbeat session stopped.
        /// </summary>
        internal async Task<int> PollHeartbeatAsync()
        {
            HeartbeatSession? session = Volatile.Read(ref _session);
            if (session == null)
            {
                return -1;
            }

            return await PollTimerOnceAsync(session).ConfigureAwait(false);
        }

        private async Task RunTimerAsync(HeartbeatSession session)
        {
            while (!session.Cts.IsCancellationRequested)
            {
                int delay;
                try
                {
                    delay = await PollTimerOnceAsync(session).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    Fail(session, WebSocketChannelReason.TransportLost, false);
                    return;
                }

                if (delay < 0)
                {
                    return;
                }

                try
                {
                    await Task.Delay(Math.Max(1, delay), session.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task<int> PollTimerOnceAsync(HeartbeatSession session)
        {
            if (session.Cts.IsCancellationRequested)
            {
                return -1;
            }

            long now = _clock.MonotonicMilliseconds;
            int delay;
            byte[]? encodedPing = null;
            lock (session.Gate)
            {
                if (!session.Bound)
                {
                    delay = 50;
                }
                else
                {
                    if (session.State.IdleDeadlineReached(now))
                    {
                        FailLocked(session, ProtocolErrorCode.HeartbeatTimeout, false);
                        return -1;
                    }

                    if (session.State.PingDue(now))
                    {
                        string nonce = NewNonce();
                        session.State.BeginPing(nonce, now);
                        Envelope ping = session.NewHeartbeatEnvelope();
                        ping.Heartbeat = new Heartbeat { Ping = new Ping { Nonce = nonce } };
                        encodedPing = WireCodec.Encode(ping);
                    }

                    delay = (int)Math.Min(
                        session.State.TimeUntilNextWake(now),
                        _options.MaxTimerSleepMs);
                }
            }

            if (encodedPing != null)
            {
                // Outside the lock so a slow socket never blocks inbound nonce
                // matching.
                await SendPingAsync(session, encodedPing).ConfigureAwait(false);
            }

            return delay;
        }

        private async Task SendPingAsync(HeartbeatSession session, byte[] encoded)
        {
            try
            {
                await _inner.SendAsync(encoded, session.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timer/session is shutting down.
            }
            catch (TableChannelClosedException ex)
            {
                Fail(session, ex.Code, ex.Permanent);
            }
        }

        private void AdoptWelcome(HeartbeatSession session, Envelope envelope)
        {
            Welcome? welcome = envelope.Welcome;
            if (welcome == null || string.IsNullOrEmpty(welcome.SessionId) || string.IsNullOrEmpty(welcome.RoomId))
            {
                return;
            }

            long now = _clock.MonotonicMilliseconds;
            lock (session.Gate)
            {
                if (session.Bound && welcome.BindingEpoch <= session.BindingEpoch)
                {
                    // A duplicate or stale WELCOME must not move control
                    // traffic onto an older binding.
                    return;
                }

                session.Bind(welcome, now);
            }
        }

        private TableChannelClosedException Fail(HeartbeatSession session, string code, bool permanent)
        {
            lock (session.Gate)
            {
                return FailLocked(session, code, permanent);
            }
        }

        private TableChannelClosedException FailLocked(HeartbeatSession session, string code, bool permanent)
        {
            if (session.Terminal != null)
            {
                return session.Terminal;
            }

            session.Terminal = new TableChannelClosedException(code, permanent);
            session.Cts.Cancel();
            _ = CloseInnerAfterFailureAsync(session, code, permanent);
            return session.Terminal;
        }

        private async Task CloseInnerAfterFailureAsync(HeartbeatSession session, string code, bool permanent)
        {
            // A stale heartbeat session must never close a newer connection
            // opened through the same decorator instance.
            if (!ReferenceEquals(Volatile.Read(ref _session), session))
            {
                return;
            }

            try
            {
                await _inner.CloseAsync(new TableChannelCloseReason(code, permanent), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The inner channel is already failing; aborting is enough.
            }
        }

        private static string NewNonce()
        {
            byte[] bytes = new byte[8];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            const string Hex = "0123456789abcdef";
            var builder = new StringBuilder(16);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(Hex[bytes[i] >> 4]);
                builder.Append(Hex[bytes[i] & 0x0f]);
            }

            return builder.ToString();
        }

        private sealed class HeartbeatSession
        {
            private readonly HeartbeatChannelOptions _options;

            public HeartbeatSession(ITableClientClock clock, HeartbeatChannelOptions options)
            {
                _options = options;
                State = new HeartbeatState(options.DefaultHeartbeatIntervalMs, options.DefaultIdleDeadMs, clock.MonotonicMilliseconds);
            }

            public object Gate { get; } = new object();

            public CancellationTokenSource Cts { get; } = new CancellationTokenSource();

            public HeartbeatState State { get; }

            public bool Bound { get; private set; }

            public string? SessionId { get; private set; }

            public string? RoomId { get; private set; }

            public int SeatId { get; private set; } = ProtocolConstants.NoSeat;

            public ulong BindingEpoch { get; private set; }

            public TableChannelClosedException? Terminal { get; set; }

            public void Cancel()
            {
                try
                {
                    Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already cancelled.
                }
            }

            public void Bind(Welcome welcome, long now)
            {
                SessionId = welcome.SessionId;
                RoomId = welcome.RoomId;
                SeatId = welcome.SeatId;
                BindingEpoch = welcome.BindingEpoch;
                int interval = _options.DefaultHeartbeatIntervalMs;
                int idle = _options.DefaultIdleDeadMs;
                if (welcome.Limits != null)
                {
                    if (welcome.Limits.HeartbeatIntervalMs > 0)
                    {
                        interval = checked((int)Math.Min(welcome.Limits.HeartbeatIntervalMs, int.MaxValue));
                    }

                    if (welcome.Limits.IdleDeadMs > 0)
                    {
                        idle = checked((int)Math.Min(welcome.Limits.IdleDeadMs, int.MaxValue));
                    }
                }

                State.Configure(
                    Math.Max(interval, _options.MinimumHeartbeatIntervalMs),
                    Math.Max(idle, Math.Max(interval, _options.MinimumHeartbeatIntervalMs)),
                    now);
                Bound = true;
            }

            public bool MatchesBinding(Envelope envelope)
            {
                return Bound &&
                       !string.IsNullOrEmpty(SessionId) &&
                       !string.IsNullOrEmpty(RoomId) &&
                       envelope.BindingEpoch == BindingEpoch &&
                       string.Equals(envelope.SessionId, SessionId, StringComparison.Ordinal) &&
                       string.Equals(envelope.RoomId, RoomId, StringComparison.Ordinal) &&
                       envelope.SeatId == SeatId;
            }

            public Envelope NewHeartbeatEnvelope()
            {
                Envelope envelope = ClientWireMapper.NewEnvelope(MessageType.Heartbeat, null);
                envelope.SessionId = SessionId ?? string.Empty;
                envelope.RoomId = RoomId ?? string.Empty;
                envelope.SeatId = SeatId;
                envelope.BindingEpoch = BindingEpoch;
                return envelope;
            }
        }

        internal sealed class HeartbeatState
        {
            private const int MaxOutstandingPings = 4;

            private readonly int _minimumIntervalMs;
            private readonly Dictionary<string, long> _outstanding =
                new Dictionary<string, long>(StringComparer.Ordinal);

            private int _intervalMs;
            private int _idleDeadMs;
            private long _lastInboundMs;
            private long _lastPingMs;
            private string? _latestNonce;
            private long? _lastRoundTripMs;
            private int _pingSentCount;
            private int _pongReceivedCount;

            public HeartbeatState(int intervalMs, int idleDeadMs, long now)
            {
                _minimumIntervalMs = 1;
                _intervalMs = Math.Max(intervalMs, 1);
                _idleDeadMs = Math.Max(idleDeadMs, _intervalMs);
                _lastInboundMs = now;
                _lastPingMs = now;
            }

            public void Configure(int intervalMs, int idleDeadMs, long now)
            {
                _intervalMs = Math.Max(intervalMs, _minimumIntervalMs);
                _idleDeadMs = Math.Max(idleDeadMs, _intervalMs);
                _lastInboundMs = now;
                _lastPingMs = now;
                _outstanding.Clear();
                _latestNonce = null;
            }

            public void OnInbound(long now)
            {
                _lastInboundMs = now;
            }

            public void OnPing(string nonce, long now)
            {
                _lastInboundMs = now;
            }

            public bool OnPong(string nonce, long now)
            {
                if (!_outstanding.TryGetValue(nonce, out long sentAt))
                {
                    return false;
                }

                _outstanding.Remove(nonce);
                if (string.Equals(_latestNonce, nonce, StringComparison.Ordinal))
                {
                    _latestNonce = null;
                }

                _lastRoundTripMs = Math.Max(0, now - sentAt);
                _lastInboundMs = now;
                _pongReceivedCount++;
                return true;
            }

            public bool PingDue(long now)
            {
                return now - _lastPingMs >= _intervalMs;
            }

            public void BeginPing(string nonce, long now)
            {
                if (_outstanding.Count >= MaxOutstandingPings)
                {
                    string? oldest = null;
                    long oldestAt = long.MaxValue;
                    foreach (KeyValuePair<string, long> pair in _outstanding)
                    {
                        if (pair.Value < oldestAt)
                        {
                            oldestAt = pair.Value;
                            oldest = pair.Key;
                        }
                    }

                    if (oldest != null)
                    {
                        _outstanding.Remove(oldest);
                    }
                }

                _outstanding[nonce] = now;
                _latestNonce = nonce;
                _lastPingMs = now;
                _pingSentCount++;
            }

            public bool IdleDeadlineReached(long now)
            {
                return now - _lastInboundMs >= _idleDeadMs;
            }

            public long TimeUntilNextWake(long now)
            {
                long next = Math.Min(_lastInboundMs + _idleDeadMs, _lastPingMs + _intervalMs);
                long delta = next - now;
                return delta <= 0 ? 1 : delta;
            }

            public HeartbeatSnapshot Snapshot(bool bound, long now, bool timedOut)
            {
                return new HeartbeatSnapshot(
                    bound,
                    _intervalMs,
                    _idleDeadMs,
                    _latestNonce,
                    _lastRoundTripMs,
                    _pingSentCount,
                    _pongReceivedCount,
                    Math.Max(0, now - _lastInboundMs),
                    Math.Max(0, now - _lastPingMs),
                    timedOut);
            }
        }
    }
}

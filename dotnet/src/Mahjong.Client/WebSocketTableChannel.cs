using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

namespace Mahjong.Client
{
    /// <summary>
    /// Client-local transport reasons. These are not wire codes and never
    /// replace a business receipt: they only explain why the channel stopped.
    /// </summary>
    public static class WebSocketChannelReason
    {
        public const string TransportLost = "TRANSPORT_LOST";
        public const string TlsHandshakeFailed = "TLS_HANDSHAKE_FAILED";
        public const string TruncatedMessage = "TRUNCATED_MESSAGE";
        public const string ReceiveTimeout = "RECEIVE_TIMEOUT";
        public const string StaleConnection = "STALE_CONNECTION";
    }

    /// <summary>
    /// Client WebSocket transport for the V5 wire envelope.
    ///
    /// Responsibilities are deliberately transport-only:
    /// one complete binary WebSocket message is exactly one envelope; fragments
    /// are reassembled until EndOfMessage; text/empty/oversized/truncated input
    /// is rejected explicitly. The channel never decodes business messages,
    /// retries commands, persists receipts or decides binding identity. The
    /// session owns those semantics.
    /// </summary>
    public sealed class WebSocketTableChannel : ITableChannel, IDisposable, IAsyncDisposable
    {
        private const int ReceiveChunkBytes = 16 * 1024;

        private readonly WebSocketTableChannelOptions _options;
        private readonly IWebSocketConnectionFactory _factory;
        private readonly SemaphoreSlim _openGate = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();

        private Connection? _connection;
        private long _generation;
        private bool _disposed;
        private int _admittedPayloadCopies;
        private int _payloadCopiesInFlight;
        private int _maxPayloadCopiesInFlight;

        /// <summary>Test/diagnostic evidence that payload copies happen after bounded admission.</summary>
        internal int AdmittedPayloadCopies => Volatile.Read(ref _admittedPayloadCopies);

        internal int PayloadCopiesInFlight => Volatile.Read(ref _payloadCopiesInFlight);

        internal int MaxPayloadCopiesInFlight => Volatile.Read(ref _maxPayloadCopiesInFlight);

        public WebSocketTableChannel(WebSocketTableChannelOptions options)
            : this(options, new ClientWebSocketConnectionFactory())
        {
        }

        internal WebSocketTableChannel(WebSocketTableChannelOptions options, IWebSocketConnectionFactory factory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public bool IsOpen
        {
            get
            {
                Connection? connection = Volatile.Read(ref _connection);
                if (connection == null || connection.Retired)
                {
                    return false;
                }

                try
                {
                    return connection.Socket.State == WebSocketState.Open;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public async Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrEmpty(request.Ticket))
            {
                throw new ArgumentException("A table ticket is required for the HELLO handshake.", nameof(request));
            }

            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(WebSocketTableChannel));
                }

                Connection? previous;
                lock (_gate)
                {
                    previous = _connection;
                    _connection = null;
                }

                if (previous != null)
                {
                    // Reopen supersedes the old generation immediately, before
                    // the new socket is created/published. Marking it stale
                    // here avoids a window where an old sender sees no current
                    // connection and reports LOCAL_CLOSED instead of
                    // STALE_CONNECTION.
                    Retire(previous, WebSocketChannelReason.StaleConnection, false);
                }

                var connection = new Connection(
                    _factory.Create(),
                    Interlocked.Increment(ref _generation),
                    _options.MaxPendingSends);

                lock (_gate)
                {
                    if (_disposed)
                    {
                        connection.Retire(ProtocolErrorCode.LocalClosed, true);
                        throw new ObjectDisposedException(nameof(WebSocketTableChannel));
                    }

                    _connection = connection;
                }

                using (var connectCts = CreateTimeoutSource(cancellationToken, connection.Lifetime.Token, _options.ConnectTimeout))
                {
                    try
                    {
                        // The ticket is intentionally not a URL, header or log
                        // value. It is sent later inside the session's HELLO.
                        await connection.Socket.ConnectAsync(_options.Endpoint!, connectCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Retire(connection, ProtocolErrorCode.LocalClosed, false);
                            throw;
                        }

                        if (!IsCurrent(connection))
                        {
                            Retire(connection, WebSocketChannelReason.StaleConnection, false);
                            throw new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false);
                        }

                        Retire(connection, ProtocolErrorCode.HandshakeTimeout, false);
                        throw new TableChannelClosedException(ProtocolErrorCode.HandshakeTimeout, false);
                    }
                    catch (Exception ex)
                    {
                        bool tls = IsTlsFailure(ex);
                        string code = tls ? WebSocketChannelReason.TlsHandshakeFailed : WebSocketChannelReason.TransportLost;
                        bool permanent = tls;
                        Retire(connection, code, permanent);
                        throw new TableChannelClosedException(code, permanent, ex);
                    }
                }

                if (!IsCurrent(connection))
                {
                    Retire(connection, WebSocketChannelReason.StaleConnection, false);
                    throw new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false);
                }
            }
            finally
            {
                _openGate.Release();
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            if (wireEnvelope.Length == 0)
            {
                throw new TableChannelClosedException(ProtocolErrorCode.MalformedMessage, true);
            }

            if (wireEnvelope.Length > _options.MaxMessageBytes)
            {
                throw new TableChannelClosedException(ProtocolErrorCode.MessageTooLarge, true);
            }

            Connection? connection = Volatile.Read(ref _connection);
            if (connection == null)
            {
                throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
            }

            if (connection.Retired)
            {
                throw ClosedFor(connection, ProtocolErrorCode.LocalClosed);
            }

            bool slotTaken = false;
            bool gateTaken = false;
            bool copyTracked = false;
            using (var sendCts = CreateTimeoutSource(cancellationToken, connection.Lifetime.Token, _options.SendTimeout))
            {
                try
                {
                    // Admission is bounded: when MaxPendingSends callers are
                    // already waiting, this caller waits instead of growing an
                    // unbounded queue. The payload is copied only after a slot
                    // is granted, so queued callers do not each hold a private
                    // copy of a large message.
                    try
                    {
                        await connection.SendSlots.WaitAsync(sendCts.Token).ConfigureAwait(false);
                        slotTaken = true;
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Retire(connection, ProtocolErrorCode.LocalClosed, false);
                            throw;
                        }

                        throw FailSend(connection, ProtocolErrorCode.SlowConsumer);
                    }

                    if (!IsCurrent(connection))
                    {
                        throw ClosedFor(connection, ProtocolErrorCode.SlowConsumer);
                    }

                    byte[] payload = wireEnvelope.ToArray();
                    copyTracked = true;
                    TrackPayloadCopy();

                    await connection.SendGate.WaitAsync(sendCts.Token).ConfigureAwait(false);
                    gateTaken = true;

                    if (!IsCurrent(connection))
                    {
                        throw ClosedFor(connection, ProtocolErrorCode.SlowConsumer);
                    }

                    await connection.Socket.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        sendCts.Token).ConfigureAwait(false);

                    if (!IsCurrent(connection))
                    {
                        throw ClosedFor(connection, ProtocolErrorCode.SlowConsumer);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Retire(connection, ProtocolErrorCode.LocalClosed, false);
                        throw;
                    }

                    throw FailSend(connection, ProtocolErrorCode.SlowConsumer);
                }
                catch (TableChannelClosedException ex)
                {
                    RetireIfCurrent(connection, ex.Code, ex.Permanent);
                    throw;
                }
                catch (WebSocketException ex)
                {
                    Retire(connection, WebSocketChannelReason.TransportLost, false);
                    throw new TableChannelClosedException(WebSocketChannelReason.TransportLost, false, ex);
                }
                catch (ObjectDisposedException ex)
                {
                    RetireIfCurrent(connection, WebSocketChannelReason.TransportLost, false);
                    throw ClosedFor(connection, WebSocketChannelReason.TransportLost, ex);
                }
                finally
                {
                    if (gateTaken)
                    {
                        connection.SendGate.Release();
                    }

                    if (slotTaken)
                    {
                        connection.SendSlots.Release();
                    }

                    if (copyTracked)
                    {
                        ReleasePayloadCopy();
                    }
                }
            }
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            Connection? connection = Volatile.Read(ref _connection);
            if (connection == null)
            {
                throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
            }

            if (connection.Retired)
            {
                throw ClosedFor(connection, ProtocolErrorCode.LocalClosed);
            }

            // Exactly one receive owner per connection. A second concurrent
            // caller is a programming error, not a protocol event.
            if (Interlocked.CompareExchange(ref connection.ReceiveOwner, 1, 0) != 0)
            {
                throw new InvalidOperationException("Only one ReceiveAsync owner is allowed per connection.");
            }

            try
            {
                if (!IsCurrent(connection))
                {
                    throw ClosedFor(connection, WebSocketChannelReason.StaleConnection);
                }

                using (var receiveCts = CreateTimeoutSource(cancellationToken, connection.Lifetime.Token, _options.ReceiveMessageTimeout))
                {
                    try
                    {
                        ReadOnlyMemory<byte> message = await ReceiveOneMessageAsync(connection, receiveCts.Token).ConfigureAwait(false);
                        if (!IsCurrent(connection))
                        {
                            // Bytes read from a retired socket must never reach a
                            // newer binding.
                            throw ClosedFor(connection, WebSocketChannelReason.StaleConnection);
                        }

                        return message;
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Retire(connection, ProtocolErrorCode.LocalClosed, false);
                            throw;
                        }

                        if (!IsCurrent(connection))
                        {
                            throw ClosedFor(connection, WebSocketChannelReason.StaleConnection);
                        }

                        Retire(connection, WebSocketChannelReason.ReceiveTimeout, false);
                        throw new TableChannelClosedException(WebSocketChannelReason.ReceiveTimeout, false);
                    }
                    catch (TableChannelClosedException ex)
                    {
                        RetireIfCurrent(connection, ex.Code, ex.Permanent);
                        throw;
                    }
                    catch (WebSocketException ex)
                    {
                        Retire(connection, WebSocketChannelReason.TransportLost, false);
                        throw new TableChannelClosedException(WebSocketChannelReason.TransportLost, false, ex);
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Retire(connection, WebSocketChannelReason.TransportLost, false);
                        throw new TableChannelClosedException(WebSocketChannelReason.TransportLost, false, ex);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref connection.ReceiveOwner, 0);
            }
        }

        public async Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            if (reason == null)
            {
                throw new ArgumentNullException(nameof(reason));
            }

            Connection? connection = Volatile.Read(ref _connection);
            if (connection == null || connection.Retired)
            {
                return;
            }

            WebSocketCloseStatus status = MapOutboundClose(reason.Code);
            try
            {
                using (var closeCts = CreateTimeoutSource(cancellationToken, connection.Lifetime.Token, _options.CloseTimeout))
                {
                    await connection.Socket.CloseOutputAsync(status, reason.Code, closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A failed close handshake is still a close. Abort below.
            }

            Retire(connection, reason.Code, reason.Permanent);
        }

        public void Dispose()
        {
            Connection? connection;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                connection = _connection;
            }

            if (connection != null)
            {
                Retire(connection, ProtocolErrorCode.LocalClosed, true);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default(ValueTask);
        }

        private async Task<ReadOnlyMemory<byte>> ReceiveOneMessageAsync(Connection connection, CancellationToken cancellationToken)
        {
            byte[] chunk = ArrayPool<byte>.Shared.Rent(ReceiveChunkBytes);
            try
            {
                using (var message = new MemoryStream())
                {
                    while (true)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await connection.Socket
                                .ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (WebSocketException ex) when (message.Length > 0)
                        {
                            // The peer disappeared in the middle of a message.
                            // Return no partial envelope; the session reconnects
                            // and reconciles through a baseline.
                            throw new TableChannelClosedException(WebSocketChannelReason.TruncatedMessage, false, ex);
                        }

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            CloseMapping mapping = MapInboundClose(result);
                            throw new TableChannelClosedException(mapping.Code, mapping.Permanent);
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            throw new TableChannelClosedException(ProtocolErrorCode.ProtocolViolation, true);
                        }

                        if (result.MessageType != WebSocketMessageType.Binary)
                        {
                            throw new TableChannelClosedException(ProtocolErrorCode.ProtocolViolation, true);
                        }

                        if (message.Length + result.Count > _options.MaxMessageBytes)
                        {
                            // Never truncate and never allocate past the bound.
                            throw new TableChannelClosedException(ProtocolErrorCode.MessageTooLarge, true);
                        }

                        if (result.Count > 0)
                        {
                            message.Write(chunk, 0, result.Count);
                        }

                        if (result.EndOfMessage)
                        {
                            if (message.Length == 0)
                            {
                                throw new TableChannelClosedException(ProtocolErrorCode.MalformedMessage, true);
                            }

                            return message.ToArray();
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        private void RetireIfCurrent(Connection connection, string code, bool permanent)
        {
            if (IsCurrent(connection))
            {
                Retire(connection, code, permanent);
            }
        }

        private bool IsCurrent(Connection connection)
        {
            return ReferenceEquals(Volatile.Read(ref _connection), connection) && !connection.Retired;
        }

        private bool IsSuperseded(Connection connection)
        {
            Connection? current = Volatile.Read(ref _connection);
            return current != null && !ReferenceEquals(current, connection);
        }

        /// <summary>
        /// Reason for a send that can no longer use its captured connection.
        /// A retired-but-not-replaced connection reports its own retire reason
        /// (for example SLOW_CONSUMER or LOCAL_CLOSED); only a connection that
        /// was replaced by a newer OpenAsync reports STALE_CONNECTION.
        /// </summary>
        private TableChannelClosedException ClosedFor(Connection connection, string fallback, Exception? inner = null)
        {
            if (IsSuperseded(connection))
            {
                return inner == null
                    ? new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false)
                    : new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false, inner);
            }

            string code = connection.RetireCode ?? fallback;
            bool permanent = connection.RetireCode != null && connection.RetirePermanent;
            return inner == null
                ? new TableChannelClosedException(code, permanent)
                : new TableChannelClosedException(code, permanent, inner);
        }

        private TableChannelClosedException FailSend(Connection connection, string fallback)
        {
            if (IsSuperseded(connection))
            {
                return new TableChannelClosedException(WebSocketChannelReason.StaleConnection, false);
            }

            if (!connection.Retired)
            {
                Retire(connection, fallback, false);
            }

            // If another sender retired first, its reason is the cause this
            // queued sender actually observed. Never invent STALE_CONNECTION
            // merely because the shared socket was retired concurrently.
            return ClosedFor(connection, fallback);
        }

        private void TrackPayloadCopy()
        {
            Interlocked.Increment(ref _admittedPayloadCopies);
            int inFlight = Interlocked.Increment(ref _payloadCopiesInFlight);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxPayloadCopiesInFlight);
                if (inFlight <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maxPayloadCopiesInFlight, inFlight, observed) != observed);
        }

        private void ReleasePayloadCopy()
        {
            Interlocked.Decrement(ref _payloadCopiesInFlight);
        }

        private void Retire(Connection connection, string code, bool permanent)
        {
            connection.Retire(code, permanent);
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }

            try
            {
                connection.Socket.Abort();
            }
            catch (Exception)
            {
                // Abort is best effort.
            }

            try
            {
                connection.Socket.Dispose();
            }
            catch (Exception)
            {
                // Disposal is best effort.
            }

            // The semaphores are intentionally not disposed: a blocked waiter
            // may still be unwinding and disposing under it would turn an
            // orderly close into ObjectDisposedException.
        }

        private static CancellationTokenSource CreateTimeoutSource(
            CancellationToken caller,
            CancellationToken lifetime,
            TimeSpan timeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
            if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(timeout);
            }

            return cts;
        }

        private static bool IsTlsFailure(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is AuthenticationException)
                {
                    return true;
                }
            }

            return false;
        }

        private static WebSocketCloseStatus MapOutboundClose(string code)
        {
            uint mapped = ProtocolErrorCode.ToWebSocketCloseCode(code);
            return (WebSocketCloseStatus)mapped;
        }

        private static CloseMapping MapInboundClose(WebSocketReceiveResult result)
        {
            int status = result.CloseStatus.HasValue ? (int)result.CloseStatus.Value : 1006;
            switch (status)
            {
                case 1000:
                    return new CloseMapping(ProtocolErrorCode.LocalClosed, false);
                case 1001:
                    return new CloseMapping(ProtocolErrorCode.ServerShutdown, true);
                case 1009:
                    return new CloseMapping(ProtocolErrorCode.MessageTooLarge, true);
                case 4400:
                    return new CloseMapping(ProtocolErrorCode.ProtocolViolation, true);
                case 4401:
                    return new CloseMapping(ProtocolErrorCode.Unauthenticated, true);
                case 4403:
                    return new CloseMapping(ProtocolErrorCode.DisplacedByNewConnection, true);
                case 4409:
                    return new CloseMapping(ProtocolErrorCode.RequestIdConflict, true);
                case 4429:
                    return new CloseMapping(ProtocolErrorCode.ServerOverloaded, false);
                case 4503:
                    return new CloseMapping(ProtocolErrorCode.ServerShutdown, true);
                case 1011:
                    return new CloseMapping(ProtocolErrorCode.InternalError, false);
                default:
                    return new CloseMapping(WebSocketChannelReason.TransportLost, false);
            }
        }

        private readonly struct CloseMapping
        {
            public CloseMapping(string code, bool permanent)
            {
                Code = code;
                Permanent = permanent;
            }

            public string Code { get; }

            public bool Permanent { get; }
        }

        private sealed class Connection
        {
            private int _retired;
            private string? _retireCode;
            private bool _retirePermanent;

            public Connection(IWebSocketConnection socket, long generation, int maxPendingSends)
            {
                Socket = socket;
                Generation = generation;
                SendSlots = new SemaphoreSlim(maxPendingSends, maxPendingSends);
            }

            public IWebSocketConnection Socket { get; }

            public long Generation { get; }

            public CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();

            public SemaphoreSlim SendGate { get; } = new SemaphoreSlim(1, 1);

            public SemaphoreSlim SendSlots { get; }

            public int ReceiveOwner;

            public bool Retired => Volatile.Read(ref _retired) != 0;

            public string? RetireCode => Volatile.Read(ref _retireCode);

            public bool RetirePermanent => Volatile.Read(ref _retirePermanent);

            public void Retire(string code, bool permanent)
            {
                bool first;
                lock (this)
                {
                    first = _retired == 0;
                    if (first)
                    {
                        _retireCode = code;
                        _retirePermanent = permanent;
                        Volatile.Write(ref _retired, 1);
                    }
                }

                if (!first)
                {
                    return;
                }

                try
                {
                    Lifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already cancelled/disposed; nothing left to do.
                }
            }
        }
    }
}

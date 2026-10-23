using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

namespace Mahjong.Client
{
    public sealed class TableSessionRunnerOptions
    {
        public string ClientBuild { get; set; } = "v5-client";

        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(500);

        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>
        /// Number of failed attempts tolerated after the first connection loss.
        /// 0 means stop on the first failure. The counter resets only after a
        /// full baseline reaches Ready.
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 8;

        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

        internal void Validate()
        {
            if (string.IsNullOrEmpty(ClientBuild))
            {
                throw new ArgumentException("ClientBuild must not be empty.", nameof(ClientBuild));
            }

            if (InitialBackoff <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(InitialBackoff), InitialBackoff, "Initial backoff must be positive.");
            }

            if (MaxBackoff < InitialBackoff)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxBackoff), MaxBackoff, "Max backoff must be at least the initial backoff.");
            }

            if (BackoffMultiplier < 1.0 || double.IsNaN(BackoffMultiplier) || double.IsInfinity(BackoffMultiplier))
            {
                throw new ArgumentOutOfRangeException(nameof(BackoffMultiplier), BackoffMultiplier, "Backoff multiplier must be finite and >= 1.");
            }

            if (MaxReconnectAttempts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts), MaxReconnectAttempts, "Reconnect limit must be >= 0.");
            }

            if (HandshakeTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), HandshakeTimeout, "Handshake timeout must be positive.");
            }
        }
    }

    public enum TableSessionRunStop
    {
        Closed = 0,
        Faulted = 1,
        ReconnectExhausted = 2,
        TicketRejected = 3,
        Canceled = 4,
        UnexpectedError = 5
    }

    public sealed class TableSessionRunResult
    {
        public TableSessionRunResult(TableSessionRunStop stop, string reasonCode, int connectionAttempts, int failedAttempts)
        {
            Stop = stop;
            ReasonCode = reasonCode ?? string.Empty;
            ConnectionAttempts = connectionAttempts;
            FailedAttempts = failedAttempts;
        }

        public TableSessionRunStop Stop { get; }

        public string ReasonCode { get; }

        public int ConnectionAttempts { get; }

        public int FailedAttempts { get; }
    }

    /// <summary>
    /// Drives an existing <see cref="IGameSession"/> through
    /// OpenAsync/PumpOnceAsync only.
    ///
    /// It owns connection attempts, exponential backoff and handshake
    /// deadlines. It never resends a business command: after a reconnect the
    /// session applies a baseline and emits RECEIPT_QUERY for every unresolved
    /// outbox entry, preserving the original request id and bytes. Permanent
    /// transport/ticket failures stop the runner instead of retrying.
    ///
    /// Event delivery stays synchronous on the runner caller; the Unity
    /// platform layer is responsible for marshalling SnapshotChanged /
    /// ReceiptResolved onto the main thread. This assembly has no Unity,
    /// Domain or TableRuntime reference.
    /// </summary>
    public sealed class TableSessionRunner
    {
        private readonly IGameSession _session;
        private readonly ITableTicketProvider _tickets;
        private readonly TableSessionRunnerOptions _options;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly ITableClientClock _clock;
        private long? _attemptDeadlineMs;

        public TableSessionRunner(
            IGameSession session,
            ITableTicketProvider tickets,
            TableSessionRunnerOptions? options = null)
            : this(session, tickets, options ?? new TableSessionRunnerOptions(), null, null)
        {
        }

        internal TableSessionRunner(
            IGameSession session,
            ITableTicketProvider tickets,
            TableSessionRunnerOptions options,
            Func<TimeSpan, CancellationToken, Task>? delay,
            ITableClientClock? clock = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
            _clock = clock ?? new SystemTableClientClock();
            _session.BaselineAdmissionCheck = CanApplyAttemptBaseline;
        }

        public async Task<TableSessionRunResult> RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await RunAsyncCore(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _attemptDeadlineMs = null;
            }
        }

        private async Task<TableSessionRunResult> RunAsyncCore(CancellationToken cancellationToken)
        {
            int failedAttempts = 0;
            int connectionAttempts = 0;
            int retryIndex = 0;
            bool attemptedConnect = false;
            string? lastFailure = null;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await TryDisconnectAsync().ConfigureAwait(false);
                    return new TableSessionRunResult(TableSessionRunStop.Canceled, ProtocolErrorCode.LocalClosed, connectionAttempts, failedAttempts);
                }

                TableConnectionState state = _session.State;
                if (state == TableConnectionState.Closed)
                {
                    return new TableSessionRunResult(TableSessionRunStop.Closed, lastFailure ?? ProtocolErrorCode.LocalClosed, connectionAttempts, failedAttempts);
                }

                if (state == TableConnectionState.Faulted)
                {
                    return new TableSessionRunResult(
                        TableSessionRunStop.Faulted,
                        lastFailure ?? _session.LastError ?? ProtocolErrorCode.InternalError,
                        connectionAttempts,
                        failedAttempts);
                }

                if (state == TableConnectionState.Disconnected || state == TableConnectionState.Reconnecting)
                {
                    if (failedAttempts > _options.MaxReconnectAttempts)
                    {
                        return new TableSessionRunResult(
                            TableSessionRunStop.ReconnectExhausted,
                            lastFailure ?? ProtocolErrorCode.HeartbeatTimeout,
                            connectionAttempts,
                            failedAttempts);
                    }

                    if (attemptedConnect)
                    {
                        try
                        {
                            await _delay(Backoff(retryIndex), cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await TryDisconnectAsync().ConfigureAwait(false);
                            return new TableSessionRunResult(TableSessionRunStop.Canceled, ProtocolErrorCode.LocalClosed, connectionAttempts, failedAttempts);
                        }

                        retryIndex++;
                    }

                    attemptedConnect = true;
                    connectionAttempts++;
                    string ticket;
                    try
                    {
                        ticket = await _tickets.GetTicketAsync(
                            new TableTicketRequest(
                                connectionAttempts,
                                _session.Binding?.SessionId.Value,
                                lastFailure),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (TableTicketException ex)
                    {
                        lastFailure = ex.Code;
                        failedAttempts++;
                        if (ex.Permanent)
                        {
                            return new TableSessionRunResult(TableSessionRunStop.TicketRejected, ex.Code, connectionAttempts, failedAttempts);
                        }

                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        await TryDisconnectAsync().ConfigureAwait(false);
                        return new TableSessionRunResult(TableSessionRunStop.Canceled, ProtocolErrorCode.LocalClosed, connectionAttempts, failedAttempts);
                    }
                    catch (Exception ex)
                    {
                        return new TableSessionRunResult(TableSessionRunStop.UnexpectedError, ex.GetType().Name, connectionAttempts, failedAttempts);
                    }

                    if (string.IsNullOrEmpty(ticket))
                    {
                        return new TableSessionRunResult(
                            TableSessionRunStop.TicketRejected,
                            ProtocolErrorCode.Unauthenticated,
                            connectionAttempts,
                            failedAttempts);
                    }

                    _attemptDeadlineMs = NewHandshakeDeadline();
                    try
                    {
                        using (var openCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            long remainingMs = _attemptDeadlineMs.Value - _clock.MonotonicMilliseconds;
                            if (remainingMs <= 0)
                            {
                                throw new OperationCanceledException(openCts.Token);
                            }

                            // Bound the connect/HELLO step by the same absolute
                            // Open->Ready deadline, not only the later pump.
                            openCts.CancelAfter(TimeSpan.FromMilliseconds(remainingMs));
                            await _session.OpenAsync(
                                new TableSessionOpenRequest(ticket, _options.ClientBuild),
                                openCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _attemptDeadlineMs = null;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            await TryDisconnectAsync().ConfigureAwait(false);
                            return new TableSessionRunResult(TableSessionRunStop.Canceled, ProtocolErrorCode.LocalClosed, connectionAttempts, failedAttempts);
                        }

                        lastFailure = ProtocolErrorCode.HandshakeTimeout;
                        failedAttempts++;
                        continue;
                    }
                    catch (TableChannelClosedException ex)
                    {
                        _attemptDeadlineMs = null;
                        lastFailure = ex.Code;
                        failedAttempts++;
                        if (ex.Permanent || _session.State == TableConnectionState.Faulted)
                        {
                            return new TableSessionRunResult(TableSessionRunStop.Faulted, ex.Code, connectionAttempts, failedAttempts);
                        }

                        continue;
                    }
                    catch (Exception ex)
                    {
                        _attemptDeadlineMs = null;
                        return new TableSessionRunResult(TableSessionRunStop.UnexpectedError, ex.GetType().Name, connectionAttempts, failedAttempts);
                    }

                    if (HandshakeDeadlineExpired(_attemptDeadlineMs))
                    {
                        lastFailure = ProtocolErrorCode.HandshakeTimeout;
                        failedAttempts++;
                        _attemptDeadlineMs = null;
                        await TryDisconnectAsync().ConfigureAwait(false);
                        continue;
                    }

                    // OpenAsync handed HELLO to the transport. WELCOME and the
                    // baseline are still pending, so Ready is not assumed here.
                    continue;
                }

                bool awaitingBaseline = _session.State != TableConnectionState.Ready;
                try
                {
                    using (var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        if (awaitingBaseline)
                        {
                            if (!_attemptDeadlineMs.HasValue)
                            {
                                // A runner may start against an already-open
                                // handshaking session; anchor the deadline now.
                                _attemptDeadlineMs = NewHandshakeDeadline();
                            }

                            long remainingMs = _attemptDeadlineMs.Value - _clock.MonotonicMilliseconds;
                            if (remainingMs <= 0)
                            {
                                lastFailure = ProtocolErrorCode.HandshakeTimeout;
                                failedAttempts++;
                                _attemptDeadlineMs = null;
                                await TryDisconnectAsync().ConfigureAwait(false);
                                continue;
                            }

                            // The deadline is absolute for Open -> Ready. A
                            // stream of early/non-baseline pushes therefore
                            // cannot extend the handshake by resetting a timer.
                            pumpCts.CancelAfter(TimeSpan.FromMilliseconds(remainingMs));
                        }

                        await _session.PumpOnceAsync(pumpCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await TryDisconnectAsync().ConfigureAwait(false);
                        return new TableSessionRunResult(TableSessionRunStop.Canceled, ProtocolErrorCode.LocalClosed, connectionAttempts, failedAttempts);
                    }

                    if (awaitingBaseline || HandshakeDeadlineExpired(_attemptDeadlineMs))
                    {
                        // A cancelled/deadline pump during the handshake is
                        // never a successful Ready transition, even if the
                        // session reached Ready just before cancellation.
                        lastFailure = ProtocolErrorCode.HandshakeTimeout;
                        failedAttempts++;
                        _attemptDeadlineMs = null;
                        await TryDisconnectAsync().ConfigureAwait(false);
                        continue;
                    }

                    if (_session.State == TableConnectionState.Ready)
                    {
                        retryIndex = 0;
                        continue;
                    }

                    lastFailure = ProtocolErrorCode.HandshakeTimeout;
                    failedAttempts++;
                    _attemptDeadlineMs = null;
                    await TryDisconnectAsync().ConfigureAwait(false);
                    continue;
                }
                catch (TableChannelClosedException ex)
                {
                    lastFailure = ex.Code;
                    failedAttempts++;
                    _attemptDeadlineMs = null;
                    if (ex.Permanent || _session.State == TableConnectionState.Faulted)
                    {
                        return new TableSessionRunResult(TableSessionRunStop.Faulted, ex.Code, connectionAttempts, failedAttempts);
                    }

                    continue;
                }
                catch (Exception ex)
                {
                    _attemptDeadlineMs = null;
                    return new TableSessionRunResult(TableSessionRunStop.UnexpectedError, ex.GetType().Name, connectionAttempts, failedAttempts);
                }

                if (_session.State == TableConnectionState.Faulted)
                {
                    _attemptDeadlineMs = null;
                    return new TableSessionRunResult(
                        TableSessionRunStop.Faulted,
                        lastFailure ?? _session.LastError ?? ProtocolErrorCode.InternalError,
                        connectionAttempts,
                        failedAttempts);
                }

                if (_session.State == TableConnectionState.Reconnecting)
                {
                    lastFailure = _session.LastError ?? WebSocketChannelReason.TransportLost;
                    failedAttempts++;
                    _attemptDeadlineMs = null;
                    continue;
                }

                // The baseline may complete successfully after the absolute
                // deadline (for example a slow connect followed by a fast
                // WELCOME/SYNC). Such a completion is a timeout, not a success:
                // do not reset the retry budget or leave the session Ready.
                if (HandshakeDeadlineExpired(_attemptDeadlineMs))
                {
                    lastFailure = ProtocolErrorCode.HandshakeTimeout;
                    failedAttempts++;
                    _attemptDeadlineMs = null;
                    await TryDisconnectAsync().ConfigureAwait(false);
                    continue;
                }

                if (_session.State == TableConnectionState.Ready)
                {
                    // A full baseline for the current binding, completed before
                    // the absolute deadline, is the only successful handshake.
                    failedAttempts = 0;
                    retryIndex = 0;
                    _attemptDeadlineMs = null;
                }
            }
        }

        private bool CanApplyAttemptBaseline(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return !HandshakeDeadlineExpired(_attemptDeadlineMs);
        }

        private long NewHandshakeDeadline()
        {
            return _clock.MonotonicMilliseconds +
                   (long)Math.Ceiling(_options.HandshakeTimeout.TotalMilliseconds);
        }

        private bool HandshakeDeadlineExpired(long? deadlineMs)
        {
            return deadlineMs.HasValue && _clock.MonotonicMilliseconds >= deadlineMs.Value;
        }

        private TimeSpan Backoff(int retryIndex)
        {
            double milliseconds = _options.InitialBackoff.TotalMilliseconds *
                                  Math.Pow(_options.BackoffMultiplier, Math.Max(0, retryIndex));
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds > _options.MaxBackoff.TotalMilliseconds)
            {
                milliseconds = _options.MaxBackoff.TotalMilliseconds;
            }

            if (milliseconds < _options.InitialBackoff.TotalMilliseconds)
            {
                milliseconds = _options.InitialBackoff.TotalMilliseconds;
            }

            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private async Task TryDisconnectAsync()
        {
            TableConnectionState state = _session.State;
            if (state == TableConnectionState.Closed ||
                state == TableConnectionState.Faulted ||
                state == TableConnectionState.Disconnected)
            {
                return;
            }

            try
            {
                await _session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: cancellation must not strand the caller.
            }
        }
    }
}

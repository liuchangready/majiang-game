#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI.Session;
using UnityEngine;

namespace Mahjong.Unity.App
{
    public enum ShutdownOutcome
    {
        Completed = 0,
        PumpBarrierTimeout = 1,
        CloseTimeout = 2,
        AlreadyIncomplete = 3,
        CloseFault = 4,
        PumpFault = 5
    }

    public sealed class ShutdownResult
    {
        public ShutdownOutcome Outcome { get; }
        public bool Quarantined { get; }
        public bool MenuAllowed { get; }
        public ShutdownOutcome? PriorOutcome { get; }

        public ShutdownResult(ShutdownOutcome outcome, bool quarantined, bool menuAllowed, ShutdownOutcome? priorOutcome = null)
        {
            Outcome = outcome;
            Quarantined = quarantined;
            MenuAllowed = menuAllowed;
            PriorOutcome = priorOutcome;
        }

        public static ShutdownResult Completed() => new ShutdownResult(ShutdownOutcome.Completed, false, true);
        public static ShutdownResult PumpBarrierTimeout() => new ShutdownResult(ShutdownOutcome.PumpBarrierTimeout, true, false);
        public static ShutdownResult CloseTimeout() => new ShutdownResult(ShutdownOutcome.CloseTimeout, true, false);
        public static ShutdownResult CloseFault() => new ShutdownResult(ShutdownOutcome.CloseFault, true, false);
        public static ShutdownResult PumpFault() => new ShutdownResult(ShutdownOutcome.PumpFault, true, false);
        public static ShutdownResult AlreadyIncomplete(ShutdownOutcome prior, bool quarantined) =>
            new ShutdownResult(ShutdownOutcome.AlreadyIncomplete, quarantined, false, prior);
    }

    public sealed class SessionCoordinator : ISessionView, ISessionViewLifecycleFence, ITableCommandSink
    {
        private readonly IGameSession? _session;
        private readonly TableSessionOperationQueue? _operationQueue;
        private readonly ITableChannel? _channel;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly AppConfig _config;

        // Test-only barrier around the owner capture. Production passes null
        // and always uses TableSessionOperationQueue.CaptureAsync; the overload
        // lets directed tests hold an accepted batch across a rebind without
        // creating a second coordinator/UI or reading live session state.
        private readonly Func<Task<TableSessionQueueSnapshot?>>? _captureProvider;

        private readonly object _stateGate = new object();
        private SessionViewState _currentViewState;
        private long _currentGeneration;
        private long _acceptedSequence;
        private SessionAuthority? _acceptedAuthority;

        private readonly CancellationTokenSource _runCts = new CancellationTokenSource();
        private Task? _pumpTask;
        private Task<ShutdownResult>? _shutdownTask;
        private ShutdownResult? _terminal;
        private bool _quarantined;

        public event EventHandler<SessionViewState>? Changed;
        public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;

        public SessionViewState Current
        {
            get
            {
                lock (_stateGate) return _currentViewState;
            }
        }

        public long CurrentGeneration => _currentGeneration;
        public bool IsQuarantined => _quarantined;
        public ShutdownResult? TerminalResult => _terminal;
        public CancellationToken RunToken => _runCts.Token;

        public bool CanSubmit
        {
            get
            {
                lock (_stateGate)
                {
                    return _terminal == null && !_quarantined && _operationQueue != null && _operationQueue.State == TableConnectionState.Ready;
                }
            }
        }

        public long Generation => _currentGeneration;

        /// <summary>
        /// Highest snapshot acceptance sequence assigned before owner capture.
        /// It advances even while the view publication is still pending.
        /// </summary>
        public long AcceptedSequence => Interlocked.Read(ref _acceptedSequence);

        /// <summary>Newest accepted authorization identity, before capture.</summary>
        public SessionAuthority? AcceptedAuthority
        {
            get
            {
                lock (_stateGate) return _acceptedAuthority;
            }
        }

        public SessionCoordinator(
            IGameSession? session,
            TableSessionOperationQueue? operationQueue,
            ITableChannel? channel,
            MainThreadDispatcher dispatcher,
            AppConfig config,
            long generation = 1,
            Func<Task<TableSessionQueueSnapshot?>>? captureProvider = null)
        {
            _session = session;
            _operationQueue = operationQueue;
            _channel = channel;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _config = config ?? new AppConfig();
            _currentGeneration = generation;
            _captureProvider = captureProvider;

            // Synchronize dispatcher generation with session generation
            _dispatcher.CurrentGeneration = _currentGeneration;
            _dispatcher.Overflowed += OnDispatcherOverflowed;

            _currentViewState = new SessionViewState(
                _currentGeneration,
                _operationQueue?.State ?? _session?.State ?? TableConnectionState.Closed,
                _operationQueue?.Binding ?? _session?.Binding,
                null,
                null,
                null,
                Array.Empty<PendingCommandSnapshot>(),
                _operationQueue?.LastError ?? _session?.LastError);

            if (_session != null)
            {
                _session.SnapshotChanged += OnSessionSnapshotChanged;
                _session.ReceiptResolved += OnSessionReceiptResolved;
            }
        }

        public void SetPumpTask(Task pumpTask)
        {
            _pumpTask = pumpTask;
        }

        private void OnDispatcherOverflowed()
        {
            lock (_stateGate)
            {
                _quarantined = true;
                _runCts.Cancel();
                _currentViewState = new SessionViewState(
                    _currentGeneration,
                    TableConnectionState.Closed,
                    _operationQueue?.Binding ?? _session?.Binding,
                    _currentViewState.PlayerSnapshot,
                    _currentViewState.PublicSnapshot,
                    _currentViewState.RoomSnapshot,
                    _currentViewState.PendingCommands,
                    "UI_DISPATCH_OVERFLOW");
            }
        }

        private void OnSessionSnapshotChanged(object? sender, TableSnapshotChangedEventArgs e)
        {
            lock (_stateGate)
            {
                if (_terminal != null || _quarantined) return;
            }

            // Copy the snapshot at the instant the Client accepted it. The
            // session may publish a later binding/snapshot before the owner
            // capture below completes; an old event must never be paired with
            // that later identity.
            PlayerSnapshot? player = e.PlayerSnapshot?.Clone();
            PublicSnapshot? publicSnapshot = e.PublicSnapshot?.Clone();
            RoomUpdate? room = e.RoomSnapshot?.Clone();
            TableVisibleEventBatch batch = e.VisibleEventBatch ?? TableVisibleEventBatch.Empty;
            long acceptedSequence = Interlocked.Increment(ref _acceptedSequence);
            if (batch.Kind != TableVisibleEventBatchKind.None)
            {
                SessionAuthority authority = SessionAuthority.FromAccepted(
                    acceptedSequence,
                    batch,
                    player,
                    publicSnapshot,
                    _session?.State ?? _operationQueue?.State ?? TableConnectionState.Ready);
                lock (_stateGate)
                {
                    _acceptedAuthority = authority;
                }
            }

            long gen = _currentGeneration;
            if (_operationQueue != null)
            {
                _ = EnqueueCaptureAndPublishAsync(
                    gen,
                    e.IsFullBaseline,
                    e.Cursor,
                    player,
                    publicSnapshot,
                    room,
                    batch,
                    acceptedSequence);
            }
            else
            {
                TableSessionBinding? binding = _session?.Binding;
                if (!BatchMatchesAccepted(batch, binding, player, publicSnapshot))
                {
                    Debug.LogWarning("[SessionCoordinator] Dropped snapshot whose typed batch identity no longer matches the accepted binding/snapshot.");
                    return;
                }

                var viewState = new SessionViewState(
                    gen,
                    _session?.State ?? TableConnectionState.Ready,
                    binding,
                    player,
                    publicSnapshot,
                    room,
                    _session?.PendingCommands ?? Array.Empty<PendingCommandSnapshot>(),
                    _session?.LastError,
                    e.IsFullBaseline,
                    e.Cursor,
                    batch,
                    acceptedSequence);

                Publish(gen, viewState);
            }
        }

        private async Task EnqueueCaptureAndPublishAsync(
            long gen,
            bool isFullBaseline,
            Mahjong.Contracts.StreamCursor? cursor,
            PlayerSnapshot? player,
            PublicSnapshot? publicSnapshot,
            RoomUpdate? room,
            TableVisibleEventBatch batch,
            long acceptedSequence)
        {
            try
            {
                if (_operationQueue == null) return;
                var capture = _captureProvider != null
                    ? await _captureProvider().ConfigureAwait(false)
                    : await _operationQueue.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_stateGate)
                {
                    if (_terminal != null || _quarantined || gen != _currentGeneration) return;
                }

                TableSessionBinding? binding = capture?.Binding ?? _session?.Binding;
                if (!BatchMatchesAccepted(batch, binding, player, publicSnapshot))
                {
                    Debug.LogWarning("[SessionCoordinator] Dropped a delayed snapshot that a newer binding/epoch superseded; no stale event was rebased.");
                    return;
                }

                var viewState = new SessionViewState(
                    gen,
                    capture?.State ?? _operationQueue.State,
                    binding,
                    player,
                    publicSnapshot,
                    room,
                    capture?.PendingCommands ?? (IReadOnlyList<PendingCommandSnapshot>)(_session?.PendingCommands ?? Array.Empty<PendingCommandSnapshot>()),
                    capture?.LastError ?? _operationQueue.LastError,
                    isFullBaseline,
                    cursor,
                    batch,
                    acceptedSequence);

                Publish(gen, viewState);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Capture exception: {ex.Message}");
            }
        }

        private static bool BatchMatchesAccepted(
            TableVisibleEventBatch batch,
            TableSessionBinding? binding,
            PlayerSnapshot? player,
            PublicSnapshot? publicSnapshot)
        {
            if (batch == null || batch.Kind == TableVisibleEventBatchKind.None)
            {
                // Legacy fake session: no typed batch identity to cross-check.
                return true;
            }

            if (binding == null)
            {
                return false;
            }

            if (!string.Equals(batch.SessionId, binding.SessionId.Value, StringComparison.Ordinal) ||
                !string.Equals(batch.RoomId, binding.RoomId.Value, StringComparison.Ordinal) ||
                batch.Role != binding.Role ||
                batch.Seat != binding.SeatId ||
                batch.BindingEpoch != binding.BindingEpoch.Value ||
                batch.ConnectionEpoch != binding.ConnectionEpoch.Value ||
                batch.OwnerEpoch != binding.OwnerEpoch.Value ||
                !string.Equals(batch.ScopeId, binding.Scope.ScopeId.Value, StringComparison.Ordinal))
            {
                return false;
            }

            // The Client intentionally retains the old PlayerSnapshot when the
            // binding becomes Spectator. Validate against the snapshot owned by
            // the accepted batch role, never another role's cache.
            PlayerSnapshot? rolePlayer = batch.Role == ProtocolRole.Player ? player : null;
            PublicSnapshot? rolePublic = batch.Role == ProtocolRole.Spectator ? publicSnapshot : null;
            ulong? stateVersion = rolePlayer?.StateVersion ?? rolePublic?.StateVersion;
            ulong? eventSeq = rolePlayer?.EventSeq ?? rolePublic?.EventSeq;
            string? roundId = rolePlayer?.RoundId ?? rolePublic?.RoundId;
            if (stateVersion.HasValue && stateVersion.Value != batch.StateVersion)
            {
                return false;
            }

            if (eventSeq.HasValue && eventSeq.Value != batch.EventSeq)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(roundId) &&
                !string.IsNullOrEmpty(batch.RoundId) &&
                !string.Equals(roundId, batch.RoundId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private void OnSessionReceiptResolved(object? sender, TableReceiptResolvedEventArgs e)
        {
            if (e == null) return;
            long gen;
            lock (_stateGate)
            {
                if (_terminal != null || _quarantined) return;
                gen = _currentGeneration;
            }

            _dispatcher.Post(() =>
            {
                lock (_stateGate)
                {
                    if (_terminal != null || _quarantined || gen != _currentGeneration)
                    {
                        return;
                    }
                }

                ReceiptResolved?.Invoke(this, e);
            }, gen);
        }

        public void Publish(long generation, SessionViewState state)
        {
            if (state == null) return;

            lock (_stateGate)
            {
                if (_terminal != null || _quarantined)
                {
                    return;
                }

                if (generation != _currentGeneration)
                {
                    return;
                }

                // A late capture may carry an older acceptance sequence than a
                // newer accepted revocation/binding/round. It must never roll
                // the authoritative view or AcceptedAuthority back to the old
                // identity, even when Current still holds an even older view.
                if (state.AcceptanceSequence <= 0)
                {
                    _currentViewState = state;
                }
                else
                {
                    long accepted = Interlocked.Read(ref _acceptedSequence);
                    if (state.AcceptanceSequence >= accepted)
                    {
                        if (state.AcceptanceSequence > accepted)
                        {
                            Interlocked.Exchange(ref _acceptedSequence, state.AcceptanceSequence);
                        }

                        _currentViewState = state;
                        _acceptedAuthority = SessionAuthority.FromView(state);
                    }
                }
            }

            _dispatcher.Post(() =>
            {
                lock (_stateGate)
                {
                    if (_terminal != null || _quarantined || generation != _currentGeneration)
                    {
                        return;
                    }
                }

                Changed?.Invoke(this, state);
            }, generation);
        }

        public async Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            TableSessionOperationQueue? queue;
            lock (_stateGate)
            {
                if (_terminal != null || _quarantined)
                {
                    throw new InvalidOperationException($"SessionCoordinator is closed or quarantined (Outcome={_terminal?.Outcome}, Quarantined={_quarantined}).");
                }

                queue = _operationQueue;
                if (queue == null)
                {
                    throw new InvalidOperationException("No active TableSessionOperationQueue to submit command.");
                }
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCts.Token);
            return await queue.SubmitAsync(command, linkedCts.Token).ConfigureAwait(false);
        }

        public Task<ShutdownResult> ShutdownAsync(string reason = "APP_QUIT")
        {
            lock (_stateGate)
            {
                if (_terminal != null)
                {
                    return Task.FromResult(ShutdownResult.AlreadyIncomplete(_terminal.Outcome, _terminal.Quarantined));
                }

                if (_shutdownTask != null)
                {
                    return _shutdownTask;
                }

                _shutdownTask = RunShutdownOnceAsync(reason);
                return _shutdownTask;
            }
        }

        private async Task<ShutdownResult> RunShutdownOnceAsync(string reason)
        {
            try
            {
                _runCts.Cancel();

                // Step 1: Pump barrier timeout & fault check
                if (_pumpTask != null)
                {
                    if (!_pumpTask.IsCompleted)
                    {
                        Task pumpBarrier = await Task.WhenAny(_pumpTask, Task.Delay(_config.pumpBarrierTimeoutMs)).ConfigureAwait(false);
                        if (pumpBarrier != _pumpTask)
                        {
                            lock (_stateGate)
                            {
                                _quarantined = true;
                                _terminal = ShutdownResult.PumpBarrierTimeout();
                                CleanupSubscriptions();
                                return _terminal;
                            }
                        }
                    }

                    if (_pumpTask.IsFaulted)
                    {
                        lock (_stateGate)
                        {
                            _quarantined = true;
                            _terminal = ShutdownResult.PumpFault();
                            CleanupSubscriptions();
                            return _terminal;
                        }
                    }
                }

                // Step 2: Independent close CTS
                using var closeCts = new CancellationTokenSource();
                Task closeTask;
                try
                {
                    if (_operationQueue != null)
                    {
                        closeTask = _operationQueue.CloseAsync(reason, closeCts.Token);
                    }
                    else if (_session != null)
                    {
                        closeTask = _session.CloseAsync(reason, closeCts.Token);
                    }
                    else
                    {
                        closeTask = Task.CompletedTask;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Session close threw synchronously: {ex.Message}");
                    lock (_stateGate)
                    {
                        _quarantined = true;
                        _terminal = ShutdownResult.CloseFault();
                        CleanupSubscriptions();
                        return _terminal;
                    }
                }

                Task closeWait = await Task.WhenAny(closeTask, Task.Delay(_config.closeTimeoutMs)).ConfigureAwait(false);
                if (closeWait != closeTask)
                {
                    // Timeout: must explicitly cancel close CTS
                    closeCts.Cancel();
                    // Bounded observation window
                    await Task.WhenAny(closeTask, Task.Delay(_config.cancelObservationMs)).ConfigureAwait(false);

                    lock (_stateGate)
                    {
                        _quarantined = true;
                        _terminal = ShutdownResult.CloseTimeout();
                        CleanupSubscriptions();
                        return _terminal;
                    }
                }

                // Verify closeTask did not fault or cancel
                try
                {
                    await closeTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Session close faulted: {ex.Message}");
                    lock (_stateGate)
                    {
                        _quarantined = true;
                        _terminal = ShutdownResult.CloseFault();
                        CleanupSubscriptions();
                        return _terminal;
                    }
                }

                if (closeTask.IsCanceled || closeTask.IsFaulted)
                {
                    lock (_stateGate)
                    {
                        _quarantined = true;
                        _terminal = ShutdownResult.CloseFault();
                        CleanupSubscriptions();
                        return _terminal;
                    }
                }

                // Step 3: Dispose channel ONLY IF cleanly closed
                DisposeChannel();

                lock (_stateGate)
                {
                    _terminal = ShutdownResult.Completed();
                    CleanupSubscriptions();
                    return _terminal;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Shutdown unexpected exception: {ex.Message}");
                lock (_stateGate)
                {
                    _quarantined = true;
                    if (_terminal == null)
                    {
                        _terminal = ShutdownResult.CloseFault();
                    }
                    CleanupSubscriptions();
                    return _terminal;
                }
            }
        }

        private void CleanupSubscriptions()
        {
            if (_session != null)
            {
                _session.SnapshotChanged -= OnSessionSnapshotChanged;
                _session.ReceiptResolved -= OnSessionReceiptResolved;
            }
            _dispatcher.Overflowed -= OnDispatcherOverflowed;
            _currentGeneration++;
        }

        private void DisposeChannel()
        {
            if (_channel is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Exception disposing channel: {ex.Message}");
                }
            }
        }
    }
}

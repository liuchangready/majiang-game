#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
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

    public sealed class SessionCoordinator : ISessionView, ITableCommandSink
    {
        private readonly IGameSession? _session;
        private readonly TableSessionOperationQueue? _operationQueue;
        private readonly ITableChannel? _channel;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly AppConfig _config;

        private readonly object _stateGate = new object();
        private SessionViewState _currentViewState;
        private long _currentGeneration;

        private readonly CancellationTokenSource _runCts = new CancellationTokenSource();
        private Task? _pumpTask;
        private Task<ShutdownResult>? _shutdownTask;
        private ShutdownResult? _terminal;
        private bool _quarantined;

        public event EventHandler<SessionViewState>? Changed;

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

        public SessionCoordinator(
            IGameSession? session,
            TableSessionOperationQueue? operationQueue,
            ITableChannel? channel,
            MainThreadDispatcher dispatcher,
            AppConfig config,
            long generation = 1)
        {
            _session = session;
            _operationQueue = operationQueue;
            _channel = channel;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _config = config ?? new AppConfig();
            _currentGeneration = generation;

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

            long gen = _currentGeneration;
            if (_operationQueue != null)
            {
                _ = EnqueueCaptureAndPublishAsync(gen, e);
            }
            else
            {
                var viewState = new SessionViewState(
                    gen,
                    _session?.State ?? TableConnectionState.Ready,
                    _session?.Binding,
                    e.PlayerSnapshot?.Clone(),
                    e.PublicSnapshot?.Clone(),
                    e.RoomSnapshot?.Clone(),
                    _session?.PendingCommands ?? Array.Empty<PendingCommandSnapshot>(),
                    _session?.LastError,
                    e.IsFullBaseline,
                    e.Cursor);

                Publish(gen, viewState);
            }
        }

        private async Task EnqueueCaptureAndPublishAsync(long gen, TableSnapshotChangedEventArgs e)
        {
            try
            {
                if (_operationQueue == null) return;
                var capture = await _operationQueue.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_stateGate)
                {
                    if (_terminal != null || _quarantined || gen != _currentGeneration) return;
                }

                var viewState = new SessionViewState(
                    gen,
                    _operationQueue.State,
                    _operationQueue.Binding,
                    e.PlayerSnapshot?.Clone(),
                    e.PublicSnapshot?.Clone(),
                    e.RoomSnapshot?.Clone(),
                    capture?.PendingCommands ?? (IReadOnlyList<PendingCommandSnapshot>)(_session?.PendingCommands ?? Array.Empty<PendingCommandSnapshot>()),
                    _operationQueue.LastError,
                    e.IsFullBaseline,
                    e.Cursor);

                Publish(gen, viewState);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Capture exception: {ex.Message}");
            }
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

                _currentViewState = state;
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

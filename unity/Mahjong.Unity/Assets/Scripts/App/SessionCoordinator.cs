using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
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
        AlreadyIncomplete = 3
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
        public static ShutdownResult AlreadyIncomplete(ShutdownOutcome prior, bool quarantined) =>
            new ShutdownResult(ShutdownOutcome.AlreadyIncomplete, quarantined, false, prior);
    }

    public sealed class SessionCoordinator : ISessionView
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

        private void OnSessionSnapshotChanged(object? sender, TableSnapshotChangedEventArgs e)
        {
            if (_terminal != null || _quarantined) return;

            long gen = _currentGeneration;
            var viewState = new SessionViewState(
                gen,
                _operationQueue?.State ?? _session?.State ?? TableConnectionState.Ready,
                _operationQueue?.Binding ?? _session?.Binding,
                e.PlayerSnapshot,
                e.PublicSnapshot,
                e.RoomSnapshot,
                _operationQueue?.CaptureAsync(CancellationToken.None).Result?.PendingCommands ??
                    (IReadOnlyList<PendingCommandSnapshot>)(_session?.PendingCommands ?? Array.Empty<PendingCommandSnapshot>()),
                _operationQueue?.LastError ?? _session?.LastError);

            Publish(gen, viewState);
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
                Changed?.Invoke(this, state);
            }, generation);
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
            _runCts.Cancel();

            // Step 1: Pump barrier timeout
            if (_pumpTask != null && !_pumpTask.IsCompleted)
            {
                Task pumpBarrier = await Task.WhenAny(_pumpTask, Task.Delay(_config.pumpBarrierTimeoutMs)).ConfigureAwait(false);
                if (pumpBarrier != _pumpTask)
                {
                    lock (_stateGate)
                    {
                        _quarantined = true;
                        _terminal = ShutdownResult.PumpBarrierTimeout();
                        return _terminal;
                    }
                }
            }

            // Step 2: Independent close CTS
            using var closeCts = new CancellationTokenSource();
            Task closeTask;
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
                    return _terminal;
                }
            }

            // Step 3: Dispose channel if applicable
            DisposeChannel();

            lock (_stateGate)
            {
                _terminal = ShutdownResult.Completed();
                return _terminal;
            }
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

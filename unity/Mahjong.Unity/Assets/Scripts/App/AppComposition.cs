#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Unity.Platform;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using UnityEngine;

namespace Mahjong.Unity.App
{
    public sealed class AppComposition
    {
        public AppConfig Config { get; }
        public UnityAppPaths Paths { get; }
        public UiThemeProvider ThemeProvider { get; }
        public ITableChannelFactory ChannelFactory { get; }
        public MainThreadDispatcher Dispatcher { get; }

        private readonly object _gate = new object();
        private SessionCoordinator? _sessionCoordinator;
        private Task<SessionCoordinator>? _inFlightOpen;
        private CancellationTokenSource _sessionCts = new CancellationTokenSource();
        private readonly CancellationTokenSource _appQuitCts = new CancellationTokenSource();
        private bool _isAppQuitting;
        private bool _isSessionShuttingDown;
        private bool _isQuarantined;
        private long _generationCounter = 0;

        public SessionCoordinator? SessionCoordinator
        {
            get
            {
                lock (_gate) { return _sessionCoordinator; }
            }
        }

        public ISessionView? SessionView
        {
            get
            {
                lock (_gate) { return _sessionCoordinator; }
            }
        }

        public AppComposition(
            AppConfig config,
            MainThreadDispatcher dispatcher,
            UiThemeProvider? themeProvider = null,
            ITableChannelFactory? channelFactory = null,
            string? persistentDataPath = null)
        {
            Config = config ?? new AppConfig();
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            Paths = new UnityAppPaths(persistentDataPath);
            ThemeProvider = themeProvider ?? UiThemeProvider.Instance;
            ChannelFactory = channelFactory ?? new UnavailableLocalTableChannelFactory();
        }

        public Task<SessionCoordinator> OpenLocalSessionAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_isAppQuitting)
                {
                    return Task.FromException<SessionCoordinator>(new OperationCanceledException("AppComposition is quitting."));
                }

                if (_isQuarantined || (_sessionCoordinator != null && _sessionCoordinator.IsQuarantined))
                {
                    return Task.FromException<SessionCoordinator>(new InvalidOperationException("Previous coordinator is quarantined; replacement blocked."));
                }

                if (_sessionCoordinator != null)
                {
                    return Task.FromResult(_sessionCoordinator);
                }

                if (_inFlightOpen != null && !_inFlightOpen.IsCompleted)
                {
                    return _inFlightOpen;
                }

                _isSessionShuttingDown = false;
                _inFlightOpen = OpenLocalSessionInternalAsync(cancellationToken);
                return _inFlightOpen;
            }
        }

        private async Task<SessionCoordinator> OpenLocalSessionInternalAsync(CancellationToken cancellationToken)
        {
            long gen;
            CancellationTokenSource linkedCts;
            lock (_gate)
            {
                gen = Interlocked.Increment(ref _generationCounter);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token, _appQuitCts.Token);
            }

            using (linkedCts)
            {
                var token = linkedCts.Token;
                ITableChannel? channel = null;
                SessionCoordinator? coordinator = null;
                try
                {
                    token.ThrowIfCancellationRequested();

                    channel = await ChannelFactory.CreateAsync(TableTransportKind.Local, token).ConfigureAwait(false);

                    lock (_gate)
                    {
                        if (_isAppQuitting || _isSessionShuttingDown || gen != _generationCounter)
                        {
                            throw new OperationCanceledException("Session open aborted due to shutdown or new generation.");
                        }
                    }

                    Paths.EnsureDirectoriesCreated();
                    var outbox = new FileOutboxStore(Paths.OutboxDirectory);
                    var session = new TableSession(channel, outbox, new TableSessionOptions());
                    var queue = new TableSessionOperationQueue(session);
                    coordinator = new SessionCoordinator(session, queue, channel, Dispatcher, Config, gen);

                    var pumpToken = coordinator.RunToken;
                    var pumpTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!pumpToken.IsCancellationRequested)
                            {
                                await queue.PumpOnceAsync(pumpToken).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Clean cancellation on shutdown
                        }

                        try
                        {
                            var unfinishedField = typeof(TableSessionOperationQueue).GetField("_unfinishedReceive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (unfinishedField?.GetValue(queue) is Task unfinishedTask && !unfinishedTask.IsCompleted)
                            {
                                await unfinishedTask.ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // Ignore cancellation/faults on pump shutdown
                        }
                    }, pumpToken);
                    coordinator.SetPumpTask(pumpTask);

                    await queue.OpenAsync(new TableSessionOpenRequest("local", "local"), token).ConfigureAwait(false);

                    lock (_gate)
                    {
                        if (_isAppQuitting || _isSessionShuttingDown || gen != _generationCounter)
                        {
                            throw new OperationCanceledException("Session open aborted due to shutdown or new generation.");
                        }
                        _sessionCoordinator = coordinator;
                    }
                    return coordinator;
                }
                catch
                {
                    if (coordinator != null)
                    {
                        try
                        {
                            var shutResult = await coordinator.ShutdownAsync("OPEN_FAILED").ConfigureAwait(false);
                            if (shutResult.Quarantined || coordinator.IsQuarantined)
                            {
                                lock (_gate)
                                {
                                    _sessionCoordinator = coordinator;
                                    _isQuarantined = true;
                                }
                            }
                        }
                        catch
                        {
                            if (coordinator.IsQuarantined)
                            {
                                lock (_gate)
                                {
                                    _sessionCoordinator = coordinator;
                                    _isQuarantined = true;
                                }
                            }
                        }
                    }
                    else if (channel is IDisposable disposable)
                    {
                        try { disposable.Dispose(); } catch { }
                    }
                    throw;
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_inFlightOpen != null && _inFlightOpen.IsCompleted)
                        {
                            _inFlightOpen = null;
                        }
                    }
                }
            }
        }

        public async Task<ShutdownResult> ShutdownAsync(string reason = "APP_QUIT")
        {
            Task<SessionCoordinator>? inFlight;
            SessionCoordinator? coordinator;
            bool isQuit = string.Equals(reason, "APP_QUIT", StringComparison.OrdinalIgnoreCase);

            lock (_gate)
            {
                if (isQuit)
                {
                    _isAppQuitting = true;
                    try { _appQuitCts.Cancel(); } catch { }
                }
                _isSessionShuttingDown = true;
                try { _sessionCts.Cancel(); } catch { }

                Interlocked.Increment(ref _generationCounter);

                inFlight = _inFlightOpen;
                coordinator = _sessionCoordinator;
            }

            if (inFlight != null && !inFlight.IsCompleted)
            {
                int timeoutMs = Math.Max(Config.pumpBarrierTimeoutMs, Config.closeTimeoutMs) + Config.cancelObservationMs;
                var timeoutTask = Task.Delay(timeoutMs);
                var completed = await Task.WhenAny(inFlight, timeoutTask).ConfigureAwait(false);
                if (completed == timeoutTask)
                {
                    lock (_gate)
                    {
                        _isQuarantined = true;
                    }
                    return ShutdownResult.PumpBarrierTimeout();
                }
            }

            lock (_gate)
            {
                coordinator = _sessionCoordinator;
            }

            if (coordinator != null)
            {
                ShutdownResult result = await coordinator.ShutdownAsync(reason).ConfigureAwait(false);
                lock (_gate)
                {
                    if (result.Outcome == ShutdownOutcome.Completed)
                    {
                        if (ReferenceEquals(_sessionCoordinator, coordinator))
                        {
                            _sessionCoordinator = null;
                        }
                        if (!isQuit)
                        {
                            _isSessionShuttingDown = false;
                            _sessionCts = new CancellationTokenSource();
                        }
                    }
                    else
                    {
                        _isQuarantined = true;
                    }
                }
                return result;
            }

            lock (_gate)
            {
                if (!isQuit)
                {
                    _isSessionShuttingDown = false;
                    _sessionCts = new CancellationTokenSource();
                }
            }

            return ShutdownResult.Completed();
        }
    }
}

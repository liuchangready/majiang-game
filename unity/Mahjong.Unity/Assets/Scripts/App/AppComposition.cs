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
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private bool _isShuttingDown;
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
                if (_isShuttingDown)
                {
                    return Task.FromException<SessionCoordinator>(new OperationCanceledException("AppComposition is shutting down."));
                }

                if (_sessionCoordinator != null)
                {
                    if (_sessionCoordinator.IsQuarantined)
                    {
                        return Task.FromException<SessionCoordinator>(new InvalidOperationException("Previous coordinator is quarantined; replacement blocked."));
                    }
                    return Task.FromResult(_sessionCoordinator);
                }

                if (_inFlightOpen != null && !_inFlightOpen.IsCompleted)
                {
                    return _inFlightOpen;
                }

                _inFlightOpen = OpenLocalSessionInternalAsync(cancellationToken);
                return _inFlightOpen;
            }
        }

        private async Task<SessionCoordinator> OpenLocalSessionInternalAsync(CancellationToken cancellationToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            var token = linkedCts.Token;

            ITableChannel? channel = null;
            SessionCoordinator? coordinator = null;
            try
            {
                if (_isShuttingDown)
                {
                    throw new OperationCanceledException(token);
                }

                long gen = Interlocked.Increment(ref _generationCounter);

                // ChannelFactory will throw LocalTableUnavailableException in P4.2-core default
                channel = await ChannelFactory.CreateAsync(TableTransportKind.Local, token).ConfigureAwait(false);

                if (_isShuttingDown)
                {
                    throw new OperationCanceledException(token);
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
                    if (_isShuttingDown)
                    {
                        // If shutdown arrived while opening, abort publication
                        throw new OperationCanceledException(token);
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
                    _inFlightOpen = null;
                }
            }
        }

        public async Task<ShutdownResult> ShutdownAsync(string reason = "APP_QUIT")
        {
            Task<SessionCoordinator>? inFlight;
            SessionCoordinator? coordinator;
            lock (_gate)
            {
                _isShuttingDown = true;
                try { _shutdownCts.Cancel(); } catch { }
                inFlight = _inFlightOpen;
                coordinator = _sessionCoordinator;
            }

            if (inFlight != null)
            {
                try { await inFlight.ConfigureAwait(false); } catch { }
            }

            lock (_gate)
            {
                coordinator = _sessionCoordinator;
            }

            if (coordinator != null)
            {
                ShutdownResult result = await coordinator.ShutdownAsync(reason).ConfigureAwait(false);
                if (result.Outcome == ShutdownOutcome.Completed)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_sessionCoordinator, coordinator))
                        {
                            _sessionCoordinator = null;
                        }
                    }
                }
                return result;
            }

            return ShutdownResult.Completed();
        }
    }
}

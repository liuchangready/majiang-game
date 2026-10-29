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

        /// <summary>
        /// Whether the wired channel factory can back a real local table session.
        /// The production default is <see cref="LocalTableChannelFactory"/> over the
        /// accepted pure .NET Mahjong.LocalHost and reports true. The explicit
        /// <see cref="UnavailableLocalTableChannelFactory"/> remains only for the
        /// unavailable-error test path and reports false. Explicitly injected test
        /// factories also report true so the UI state machine and lifecycle error
        /// paths can be exercised; that is never production evidence of a real host.
        /// </summary>
        public bool SupportsLocalStart
        {
            get
            {
                lock (_gate)
                {
                    if (_isAppQuitting || _isSessionShuttingDown || _isQuarantined || _quarantinedShutdownResult != null)
                    {
                        return false;
                    }

                    if (_activeShutdownTask != null && !_activeShutdownTask.IsCompleted)
                    {
                        return false;
                    }

                    if (_sessionCoordinator != null && _sessionCoordinator.IsQuarantined)
                    {
                        return false;
                    }

                    return !(ChannelFactory is UnavailableLocalTableChannelFactory);
                }
            }
        }

        /// <summary>
        /// True once this composition reached a terminal failure (quarantine, close
        /// fault or bounded timeout). A terminal composition is never reusable in
        /// this process and must not advertise local-start capability.
        /// </summary>
        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    return _isQuarantined ||
                           _quarantinedShutdownResult != null ||
                           (_sessionCoordinator != null && _sessionCoordinator.IsQuarantined);
                }
            }
        }

        /// <summary>Terminal shutdown result, or null while the composition is healthy.</summary>
        public ShutdownResult? TerminalResult
        {
            get
            {
                lock (_gate) { return _quarantinedShutdownResult; }
            }
        }

        /// <summary>
        /// True while a shutdown or application quit is in progress. New starts must
        /// be rejected rather than entering a closing owner.
        /// </summary>
        public bool IsShutdownInProgress
        {
            get
            {
                lock (_gate)
                {
                    return _isAppQuitting ||
                           _isSessionShuttingDown ||
                           (_activeShutdownTask != null && !_activeShutdownTask.IsCompleted);
                }
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

            // Capture Application.persistentDataPath (or the explicit absolute test
            // root) here, on the Unity main thread, before any worker opens a host.
            Paths = new UnityAppPaths(persistentDataPath);
            ThemeProvider = themeProvider ?? UiThemeProvider.Instance;

            // Production default: a real pure .NET local host. The outbox lives at
            // Paths.OutboxDirectory while the host owns Paths.LocalSaveDirectory, so
            // the two persistence domains can never alias each other.
            ChannelFactory = channelFactory ?? new LocalTableChannelFactory(Config, Paths);
        }

        private static readonly Task<ShutdownResult> CachedCompletedTask = Task.FromResult(ShutdownResult.Completed());
        private Task<ShutdownResult>? _activeShutdownTask;
        private ShutdownResult? _quarantinedShutdownResult;
        private Task<ShutdownResult>? _quarantinedTask;

        public Task<SessionCoordinator> OpenLocalSessionAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_isAppQuitting)
                {
                    return Task.FromException<SessionCoordinator>(new OperationCanceledException("AppComposition is quitting."));
                }

                if (_isQuarantined || _quarantinedShutdownResult != null || (_sessionCoordinator != null && _sessionCoordinator.IsQuarantined))
                {
                    return Task.FromException<SessionCoordinator>(new InvalidOperationException("Previous coordinator is quarantined; replacement blocked."));
                }

                if (_isSessionShuttingDown || (_activeShutdownTask != null && !_activeShutdownTask.IsCompleted))
                {
                    return Task.FromException<SessionCoordinator>(new InvalidOperationException("Cannot open session while session shutdown is in progress."));
                }

                if (_sessionCoordinator != null)
                {
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
                    // Pre-cancel must not reach the factory: no ticket, directory,
                    // file, lease or actor may exist for an already-cancelled open.
                    token.ThrowIfCancellationRequested();

                    channel = await ChannelFactory.CreateAsync(TableTransportKind.Local, token).ConfigureAwait(false);

                    lock (_gate)
                    {
                        if (_isAppQuitting || _isSessionShuttingDown || _isQuarantined || _quarantinedShutdownResult != null || gen != _generationCounter)
                        {
                            throw new OperationCanceledException("Session open aborted due to shutdown or new generation.");
                        }
                    }

                    // A real factory issues an opaque one-use ticket for this attempt;
                    // the explicit scripted/unavailable test channels carry no ticket
                    // source and keep the neutral test build id. Production never hits
                    // the fallback because LocalTableChannelFactory always supplies one.
                    string ticket = (channel as ILocalTableTicketSource)?.SessionTicket ?? "local";
                    string clientBuild = (channel as ILocalTableTicketSource) != null
                        ? LocalTableChannelFactory.ClientBuild
                        : "unity-p42b-test";

                    Paths.EnsureDirectoriesCreated();
                    var outbox = new FileOutboxStore(Paths.OutboxDirectory);
                    var session = new TableSession(channel, outbox, new TableSessionOptions());
                    var queue = new TableSessionOperationQueue(session);
                    coordinator = new SessionCoordinator(session, queue, channel, Dispatcher, Config, gen);

                    // The real channel rejects ReceiveAsync before its first open.
                    // Open the transport/HELLO first, then admit the receive pump;
                    // starting it earlier turns a benign "not open yet" into a
                    // transport fault on a real LocalTableChannel (a scripted test
                    // channel that parks forever hides that ordering bug).
                    await queue.OpenAsync(new TableSessionOpenRequest(ticket, clientBuild), token).ConfigureAwait(false);

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

                    lock (_gate)
                    {
                        if (_isAppQuitting || _isSessionShuttingDown || _isQuarantined || _quarantinedShutdownResult != null || gen != _generationCounter)
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
                                    _quarantinedShutdownResult = shutResult;
                                    _quarantinedTask = Task.FromResult(shutResult);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            if (coordinator.IsQuarantined)
                            {
                                var shutResult = ShutdownResult.CloseFault();
                                lock (_gate)
                                {
                                    _sessionCoordinator = coordinator;
                                    _isQuarantined = true;
                                    _quarantinedShutdownResult = shutResult;
                                    _quarantinedTask = Task.FromResult(shutResult);
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

        public Task<ShutdownResult> ShutdownAsync(string reason = "APP_QUIT")
        {
            bool isQuit = string.Equals(reason, "APP_QUIT", StringComparison.OrdinalIgnoreCase);

            lock (_gate)
            {
                if (_quarantinedTask != null)
                {
                    return _quarantinedTask;
                }

                if (isQuit)
                {
                    _isAppQuitting = true;
                    try { _appQuitCts.Cancel(); } catch { }
                }

                if (_activeShutdownTask != null && !_activeShutdownTask.IsCompleted)
                {
                    return _activeShutdownTask;
                }

                if (_isAppQuitting && _sessionCoordinator == null && (_inFlightOpen == null || _inFlightOpen.IsCompleted))
                {
                    return CachedCompletedTask;
                }

                if (_sessionCoordinator == null && (_inFlightOpen == null || _inFlightOpen.IsCompleted))
                {
                    return CachedCompletedTask;
                }

                _isSessionShuttingDown = true;
                try { _sessionCts.Cancel(); } catch { }
                Interlocked.Increment(ref _generationCounter);

                _activeShutdownTask = ExecuteShutdownAsync(reason, isQuit);
                return _activeShutdownTask;
            }
        }

        private async Task<ShutdownResult> ExecuteShutdownAsync(string reason, bool isQuit)
        {
            try
            {
                Task<SessionCoordinator>? inFlight;
                SessionCoordinator? coordinator;

                lock (_gate)
                {
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
                        var quarantined = ShutdownResult.PumpBarrierTimeout();
                        lock (_gate)
                        {
                            _isQuarantined = true;
                            _quarantinedShutdownResult = quarantined;
                            _quarantinedTask = Task.FromResult(quarantined);
                        }
                        return quarantined;
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
                            if (!isQuit && !_isAppQuitting)
                            {
                                _isSessionShuttingDown = false;
                                _sessionCts = new CancellationTokenSource();
                                _activeShutdownTask = null;
                            }
                        }
                        else
                        {
                            _isQuarantined = true;
                            _quarantinedShutdownResult = result;
                            _quarantinedTask = Task.FromResult(result);
                        }
                    }
                    return result;
                }

                lock (_gate)
                {
                    if (!isQuit && !_isAppQuitting)
                    {
                        _isSessionShuttingDown = false;
                        _sessionCts = new CancellationTokenSource();
                        _activeShutdownTask = null;
                    }
                }

                return ShutdownResult.Completed();
            }
            catch (Exception)
            {
                var quarantined = ShutdownResult.CloseFault();
                lock (_gate)
                {
                    _isQuarantined = true;
                    _quarantinedShutdownResult = quarantined;
                    _quarantinedTask = Task.FromResult(quarantined);
                }
                return quarantined;
            }
        }
    }
}

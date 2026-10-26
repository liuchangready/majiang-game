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

        private SessionCoordinator? _sessionCoordinator;
        private long _generationCounter = 0;

        public SessionCoordinator? SessionCoordinator => _sessionCoordinator;
        public ISessionView? SessionView => _sessionCoordinator;

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

        public async Task<SessionCoordinator> OpenLocalSessionAsync(CancellationToken cancellationToken)
        {
            long gen = Interlocked.Increment(ref _generationCounter);

            // ChannelFactory will throw LocalTableUnavailableException in P4.2-core
            ITableChannel channel = await ChannelFactory.CreateAsync(TableTransportKind.Local, cancellationToken).ConfigureAwait(false);

            // Once P4.1 is integrated, real channel is provided
            Paths.EnsureDirectoriesCreated();
            var outbox = new FileOutboxStore(Paths.OutboxDirectory);
            var session = new TableSession(channel, outbox, new TableSessionOptions());
            var queue = new TableSessionOperationQueue(session);
            var coordinator = new SessionCoordinator(session, queue, channel, Dispatcher, Config, gen);

            _sessionCoordinator = coordinator;
            return coordinator;
        }

        public async Task<ShutdownResult> ShutdownAsync(string reason = "APP_QUIT")
        {
            if (_sessionCoordinator != null)
            {
                ShutdownResult result = await _sessionCoordinator.ShutdownAsync(reason).ConfigureAwait(false);
                if (result.Outcome == ShutdownOutcome.Completed)
                {
                    _sessionCoordinator = null;
                }
                return result;
            }

            return ShutdownResult.Completed();
        }
    }
}

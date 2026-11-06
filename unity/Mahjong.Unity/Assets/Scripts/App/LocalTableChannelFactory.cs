#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.LocalHost;
using Mahjong.Unity.Platform;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Platform-facing ticket source attached to a real local channel. The ticket is
    /// an opaque one-process, one-use handle; it is never logged, persisted in the
    /// outbox or exposed to UI code.
    /// </summary>
    public interface ILocalTableTicketSource
    {
        string SessionTicket { get; }
    }

    /// <summary>
    /// Local match control seam implemented by <see cref="LocalTableChannelFactory"/>
    /// when the opt-in full-match driver enabled match mode. Round completion is
    /// recorded through the accepted <see cref="LocalMatchCoordinator"/>; the
    /// normal single-round composition never touches this surface.
    /// </summary>
    public interface ILocalMatchControl
    {
        /// <summary>Current durable match snapshot, or null before the first open.</summary>
        LocalMatchSnapshot? CurrentMatch { get; }

        /// <summary>
        /// Commits the terminal result of the currently open round into the match
        /// state, without closing the session-owned channel. The caller must close
        /// or shut down that session before opening the next round.
        /// </summary>
        Task<LocalMatchSnapshot> RecordRoundCompletionAsync(CancellationToken cancellationToken);

        /// <summary>Hidden-tile-free runtime diagnostics for the local PlayMode fixture.</summary>
        string DescribeRuntimeForTest();
    }

    public interface ILocalRoomViewerFactory
    {
        Task<ITableChannel> CreateRoomViewerAsync(LocalRoomViewerRole role, int seatId, CancellationToken cancellationToken);
        Task CloseRoomAsync(CancellationToken cancellationToken);
    }

    public enum LocalRoomViewerRole : byte
    {
        Player = 0,
        Spectator = 1
    }

    /// <summary>
    /// Production local table composition root. It is the only place in the Unity
    /// project that calls the accepted pure .NET <see cref="LocalTableHost"/> facade.
    ///
    /// <para>
    /// Resource acquisition happens exactly once, when the channel's first
    /// <c>OpenAsync</c> runs. Creating the factory/ticket/channel allocates no
    /// directory, file, lease or host actor; a pre-cancelled request therefore
    /// creates none of them and propagates the caller's
    /// <see cref="OperationCanceledException"/> unchanged.
    /// </para>
    ///
    /// <para>
    /// The room id is stable across process restarts and the absolute local save
    /// directory is captured on the Unity main thread. The client outbox is a
    /// different directory supplied by <see cref="AppComposition"/>.
    /// </para>
    ///
    /// <para>
    /// Opt-in full-match mode is a process-level switch set by
    /// <see cref="LocalFullMatchPlayerDriver"/> before the application composition
    /// is created. When enabled, every channel is one round of the accepted
    /// <see cref="LocalMatchCoordinator"/> match instead of a standalone local
    /// host; the normal application never enables it.
    /// </para>
    /// </summary>
    public sealed class LocalTableChannelFactory : ITableChannelFactory, ILocalMatchControl, ILocalRoomViewerFactory
    {
        public const string DefaultRoomId = "local-main";
        public const string ClientBuild = "unity-p42b-localhost";

        private static int _matchModeEnabled;
        private static int _privacyRoomModeEnabled;
        private static string? _playerActionScenario;

        private readonly string _roomId;
        private readonly string _ownerInstanceId;
        private readonly string _saveDirectory;
        private readonly TimeSpan _leaseDuration;
        private readonly SemaphoreSlim _matchGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _roomGate = new SemaphoreSlim(1, 1);

        private LocalMatchCoordinator? _match;
        private LocalTableRoom? _room;

        public LocalTableChannelFactory(
            AppConfig config,
            UnityAppPaths paths,
            string? roomId = null,
            string? ownerInstanceId = null,
            TimeSpan? leaseDuration = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (paths == null) throw new ArgumentNullException(nameof(paths));

            _roomId = string.IsNullOrWhiteSpace(roomId) ? DefaultRoomId : roomId!;
            _ownerInstanceId = string.IsNullOrWhiteSpace(ownerInstanceId)
                ? "unity-local-" + Guid.NewGuid().ToString("N")
                : ownerInstanceId!;
            _saveDirectory = paths.LocalSaveDirectory;
            _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(10);
        }

        /// <summary>
        /// True only when the opt-in full-match driver selected match composition for
        /// this process. Normal launches leave it false and keep the accepted
        /// single-round local host path exactly unchanged.
        /// </summary>
        public static bool MatchModeEnabled => Volatile.Read(ref _matchModeEnabled) == 1;

        /// <summary>
        /// Process-level opt-in. Must run before <c>AppComposition</c> is created; the
        /// full-match driver calls it from a RuntimeInitializeOnLoadMethod. It is not a
        /// product setting and is never persisted.
        /// </summary>
        public static void EnableMatchMode()
        {
            Interlocked.Exchange(ref _matchModeEnabled, 1);
        }

        /// <summary>
        /// Test/process-isolation reset for the opt-in switch. PlayMode fixtures
        /// must restore the normal single-round composition after their own test.
        /// </summary>
        public static void DisableMatchModeForTest()
        {
            Interlocked.Exchange(ref _matchModeEnabled, 0);
        }

        /// <summary>
        /// Explicit process-level test opt-in for one frozen C04 Player action.
        /// Only the four named scenarios are accepted. The default application
        /// never sets this switch and retains the ordinary random local deal.
        /// </summary>
        public static void EnablePlayerActionScenario(string scenarioId)
        {
            // Validate the closed scenario set and fixture construction before
            // AppComposition can acquire any channel resources.
            LocalPlayerActionScenario.ValidateScenario(scenarioId);
            if (MatchModeEnabled)
                throw new InvalidOperationException("A C04 action fixture cannot be combined with full-match mode.");
            Interlocked.Exchange(ref _playerActionScenario, scenarioId);
        }

        public static void DisablePlayerActionScenarioForTest()
        {
            Interlocked.Exchange(ref _playerActionScenario, null);
        }

        public static void EnableC04PrivacyRoomMode()
        {
            if (MatchModeEnabled) throw new InvalidOperationException("C04 privacy room mode cannot be combined with full-match mode.");
            Interlocked.Exchange(ref _privacyRoomModeEnabled, 1);
        }

        public static void DisableC04PrivacyRoomModeForTest()
        {
            Interlocked.Exchange(ref _privacyRoomModeEnabled, 0);
        }

        /// <summary>Stable room id used by the single local production table.</summary>
        public string RoomId => _roomId;

        /// <summary>Absolute directory the pure .NET host owns; never the outbox.</summary>
        public string SaveDirectory => _saveDirectory;

        /// <summary>
        /// Diagnostic/read-only lifecycle probe: true while this factory still owns a cached
        /// room (its lease, actor and driver). A single-viewer owner releases it on a clean
        /// session shutdown; explicit multi-viewer rooms stay owned until every viewer leaves.
        /// </summary>
        public bool IsRoomOpen => Volatile.Read(ref _room) != null;

        public LocalMatchSnapshot? CurrentMatch
        {
            get
            {
                LocalMatchCoordinator? match = Volatile.Read(ref _match);
                return match?.Snapshot;
            }
        }

        public string DescribeRuntimeForTest()
        {
            LocalMatchCoordinator? match = Volatile.Read(ref _match);
            return match?.DescribeRuntimeForTest() ?? "match=null";
        }

        public Task<ITableChannel> CreateAsync(
            TableTransportKind kind,
            CancellationToken cancellationToken)
        {
            // Pre-cancel is a pure failure: no options/validation, no ticket, no
            // directory, no lease, no actor. Preserve the caller's token.
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<ITableChannel>(cancellationToken);
            }

            if (kind != TableTransportKind.Local)
            {
                return Task.FromException<ITableChannel>(
                    new NotSupportedException("Only the local transport is wired in P4.2-B."));
            }

            if (MatchModeEnabled)
            {
                if (!string.IsNullOrEmpty(Volatile.Read(ref _playerActionScenario)))
                    return Task.FromException<ITableChannel>(new InvalidOperationException(
                        "A C04 action fixture cannot be combined with full-match mode."));
                return CreateMatchRoundAsync(cancellationToken);
            }

            return CreateCoreAsync(cancellationToken);
        }

        private async Task<ITableChannel> CreateMatchRoundAsync(CancellationToken cancellationToken)
        {
            await _matchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LocalMatchCoordinator? match = Volatile.Read(ref _match);
                if (match == null)
                {
                    var options = new LocalMatchHostOptions
                    {
                        SaveDirectory = _saveDirectory,
                        MatchId = _roomId,
                        RulesetVersion = 1,
                        OwnerInstanceId = _ownerInstanceId,
                        LeaseDuration = _leaseDuration
                    };

                    match = await LocalMatchCoordinator.OpenAsync(options, cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _match, match);
                }

                // OpenRoundAsync returns the already-open round when the current
                // session was not completed; after RecordRoundCompletionAsync it
                // creates a new durable actor with the next roundId.
                LocalMatchRound round = await match.OpenRoundAsync(cancellationToken).ConfigureAwait(false);
                return new LocalTableTicketChannel(round.Channel, round.Ticket);
            }
            finally
            {
                _matchGate.Release();
            }
        }

        public async Task<LocalMatchSnapshot> RecordRoundCompletionAsync(CancellationToken cancellationToken)
        {
            if (!MatchModeEnabled)
            {
                throw new InvalidOperationException("Match mode is not enabled for this process.");
            }

            await _matchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LocalMatchCoordinator? match = Volatile.Read(ref _match);
                if (match == null)
                {
                    throw new InvalidOperationException("No local match has been opened.");
                }

                return await match.RecordRoundCompletionAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _matchGate.Release();
            }
        }

        private async Task<ITableChannel> CreateCoreAsync(CancellationToken cancellationToken)
        {
            LocalTableRoom room = await GetOrOpenRoomAsync(cancellationToken).ConfigureAwait(false);
            LocalTableConnectionTicket ticket = room.CreatePlayerTicket(0);
            ITableChannel channel = await room.ConnectAsync(ticket, cancellationToken).ConfigureAwait(false);
            return new LocalTableTicketChannel(channel, ticket.Value);
        }

        public async Task<ITableChannel> CreateRoomViewerAsync(
            LocalRoomViewerRole role,
            int seatId,
            CancellationToken cancellationToken)
        {
            LocalTableRoom room = await GetOrOpenRoomAsync(cancellationToken).ConfigureAwait(false);
            LocalTableConnectionTicket ticket = role == LocalRoomViewerRole.Spectator
                ? room.CreateSpectatorTicket()
                : room.CreatePlayerTicket(seatId);
            ITableChannel channel = await room.ConnectAsync(ticket, cancellationToken).ConfigureAwait(false);
            return new LocalTableTicketChannel(channel, ticket.Value);
        }

        public async Task CloseRoomAsync(CancellationToken cancellationToken)
        {
            await _roomGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LocalTableRoom? room = _room;
                _room = null;
                if (room != null) await room.DisposeAsync().ConfigureAwait(false);
            }
            finally { _roomGate.Release(); }
        }

        private async Task<LocalTableRoom> GetOrOpenRoomAsync(CancellationToken cancellationToken)
        {
            await _roomGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_room != null) return _room;
                var options = new LocalTableHostOptions
                {
                    SaveDirectory = _saveDirectory,
                    RoomId = _roomId,
                    RulesetVersion = 1,
                    OwnerInstanceId = _ownerInstanceId,
                    LeaseDuration = _leaseDuration,
                    AutoFlushInterval = TimeSpan.FromMilliseconds(500),
                    SeatPlan = Volatile.Read(ref _privacyRoomModeEnabled) == 1
                        ? LocalTableSeatPlan.AllFourHumanSeats
                        : LocalTableSeatPlan.SingleHumanSeatZero
                };
                string? scenario = Volatile.Read(ref _playerActionScenario);
                if (!string.IsNullOrEmpty(scenario)) LocalPlayerActionScenario.ConfigureHostOptions(options, scenario!);
                cancellationToken.ThrowIfCancellationRequested();
                _room = await LocalTableHost.OpenRoomAsync(options, cancellationToken).ConfigureAwait(false);
                return _room;
            }
            finally { _roomGate.Release(); }
        }

        private sealed class LocalTableTicketChannel : ITableChannel, ILocalTableTicketSource
        {
            private readonly ITableChannel _inner;

            public LocalTableTicketChannel(ITableChannel inner, string sessionTicket)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                SessionTicket = sessionTicket ?? throw new ArgumentNullException(nameof(sessionTicket));
            }

            public string SessionTicket { get; }

            public bool IsOpen => _inner.IsOpen;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken) =>
                _inner.SendAsync(wireEnvelope, cancellationToken);

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                ReadOnlyMemory<byte> wire = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                C04PrivacyProbe.RecordInbound(wire);
                return wire;
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}

#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.LocalHost;
using Mahjong.Unity.Platform;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// App-level selector exposed to presentation tests. It deliberately has no Domain,
    /// round, tile, ledger or receipt member; the LocalHost assembly maps it to its one
    /// frozen controlled scenario.
    /// </summary>
    public enum C04PlayerCheatScenario
    {
        AuthorizedThreeCheats = 1,
        InsufficientBalance = 2
    }

    /// <summary>
    /// Opt-in local composition used only by the C04-G3 Player probe and its PlayMode
    /// fixture. It exposes exactly one existing <see cref="ITableChannelFactory"/> and
    /// hands out real <see cref="LocalTableHost"/> channels whose initial round comes from
    /// the frozen <see cref="LocalPlayerCheatScenario"/> enum. It never accepts a Domain
    /// round, tile map, cheat state, ledger, receipt or event from the caller.
    ///
    /// <para>
    /// Creating the factory allocates no directory, ticket, file, lease, actor or thread.
    /// The first channel <c>OpenAsync</c> is the only resource acquisition point, matching
    /// the accepted production local factory. Normal launches never construct this type.
    /// </para>
    /// </summary>
    public sealed class C04PlayerCheatChannelFactory : ITableChannelFactory
    {
        public const string ClientBuild = "unity-c04-g3-player-cheats";

        private readonly string _saveDirectory;
        private readonly string _roomId;
        private readonly string _ownerInstanceId;
        private readonly C04PlayerCheatScenario _scenario;
        private readonly LocalPlayerCheatScenarioKind _localScenario;
        private readonly TimeSpan _leaseDuration;

        public C04PlayerCheatChannelFactory(
            UnityAppPaths paths,
            C04PlayerCheatScenario scenario,
            string? ownerInstanceId = null,
            TimeSpan? leaseDuration = null)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (!Enum.IsDefined(typeof(C04PlayerCheatScenario), scenario))
            {
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown C04 cheat scenario.");
            }

            _saveDirectory = paths.LocalSaveDirectory;
            _roomId = LocalPlayerCheatScenario.DefaultRoomId;
            _ownerInstanceId = string.IsNullOrWhiteSpace(ownerInstanceId)
                ? "unity-c04-g3-" + Guid.NewGuid().ToString("N")
                : ownerInstanceId!;
            _scenario = scenario;
            _localScenario = (LocalPlayerCheatScenarioKind)(int)scenario;
            _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(10);
        }

        public C04PlayerCheatScenario Scenario => _scenario;

        public string RoomId => _roomId;

        public string SaveDirectory => _saveDirectory;

        public LocalPlayerCheatScenarioFacts Facts => LocalPlayerCheatScenario.GetFacts(_localScenario);

        public static uint[] ProposedTileMarkTiles(C04PlayerCheatScenario scenario)
        {
            return LocalPlayerCheatScenario
                .GetFacts((LocalPlayerCheatScenarioKind)(int)scenario)
                .ProposedTileMarkTileIds
                .ToArray();
        }

        public LocalTableHostOptions CreateOptions()
        {
            return LocalPlayerCheatScenario.CreateOptions(
                _saveDirectory,
                _roomId,
                _localScenario,
                _ownerInstanceId);
        }

        public Task<ITableChannel> CreateAsync(
            TableTransportKind kind,
            CancellationToken cancellationToken)
        {
            // A pre-cancelled request is a pure failure: no options, ticket, directory,
            // lease, actor or thread. Preserve the caller's token unchanged.
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<ITableChannel>(cancellationToken);
            }

            if (kind != TableTransportKind.Local)
            {
                return Task.FromException<ITableChannel>(
                    new NotSupportedException("The C04 cheat probe only wires the local transport."));
            }

            return CreateCoreAsync(cancellationToken);
        }

        private async Task<ITableChannel> CreateCoreAsync(CancellationToken cancellationToken)
        {
            LocalTableHostOptions options = CreateOptions();
            options.LeaseDuration = _leaseDuration;
            cancellationToken.ThrowIfCancellationRequested();

            ITableChannel channel = await LocalTableHost
                .CreateChannelAsync(options, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            string ticket = LocalTableHost.IssueTicket(options);
            return new C04PlayerCheatTicketChannel(channel, ticket);
        }

        private sealed class C04PlayerCheatTicketChannel : ITableChannel, ILocalTableTicketSource
        {
            private readonly ITableChannel _inner;

            public C04PlayerCheatTicketChannel(ITableChannel inner, string sessionTicket)
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

            public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
                _inner.ReceiveAsync(cancellationToken);

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}

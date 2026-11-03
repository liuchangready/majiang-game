#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.LocalHost;
using Mahjong.Unity.Platform;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Thread-safe capture of the exact serialized frames that cross the real production
    /// channel. It never decodes, rewrites or synthesizes a frame; the probe only assigns
    /// captured request ids/versions to the bytes the channel actually sent or received.
    /// </summary>
    public sealed class C04RawFrameCapture
    {
        private readonly object _gate = new object();
        private readonly List<byte[]> _sent = new List<byte[]>();
        private readonly List<byte[]> _received = new List<byte[]>();

        public int SentCount
        {
            get { lock (_gate) { return _sent.Count; } }
        }

        public int ReceivedCount
        {
            get { lock (_gate) { return _received.Count; } }
        }

        public void RecordSent(ReadOnlyMemory<byte> frame)
        {
            byte[] copy = frame.ToArray();
            lock (_gate) { _sent.Add(copy); }
        }

        public void RecordReceived(ReadOnlyMemory<byte> frame)
        {
            byte[] copy = frame.ToArray();
            lock (_gate) { _received.Add(copy); }
        }

        public byte[][] SentFrom(int start)
        {
            lock (_gate)
            {
                int from = Math.Max(0, Math.Min(start, _sent.Count));
                var result = new byte[_sent.Count - from][];
                for (int i = from; i < _sent.Count; i++)
                {
                    result[i - from] = _sent[i];
                }

                return result;
            }
        }

        public byte[][] ReceivedFrom(int start)
        {
            lock (_gate)
            {
                int from = Math.Max(0, Math.Min(start, _received.Count));
                var result = new byte[_received.Count - from][];
                for (int i = from; i < _received.Count; i++)
                {
                    result[i - from] = _received[i];
                }

                return result;
            }
        }
    }

    /// <summary>
    /// Read-only physical/ledger projection of the exact persisted local authority envelope,
    /// exposed to the presentation fixture without leaking Domain types or write access.
    /// </summary>
    public sealed class C04PhysicalSnapshot
    {
        public string stateVersion = string.Empty;
        public string eventSeq = string.Empty;
        public string cheatFingerprint = string.Empty;
        public string tilesSha256 = string.Empty;
        public string audit = string.Empty;
        public int ledgerEntryCount;
        public long balance;
        public int tileCount;
        public int uniqueCount;
    }

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
        private readonly C04RawFrameCapture? _capture;

        public C04PlayerCheatChannelFactory(
            UnityAppPaths paths,
            C04PlayerCheatScenario scenario,
            string? ownerInstanceId = null,
            TimeSpan? leaseDuration = null,
            C04RawFrameCapture? capture = null)
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
            _capture = capture;
        }

        public C04PlayerCheatScenario Scenario => _scenario;

        /// <summary>Exact channel transcript sink; null for ordinary component fixtures.</summary>
        public C04RawFrameCapture? Capture => _capture;

        public string RoomId => _roomId;

        public string SaveDirectory => _saveDirectory;

        public LocalPlayerCheatScenarioFacts Facts => LocalPlayerCheatScenario.GetFacts(_localScenario);

        /// <summary>
        /// Read-only capture of the committed local authority state. It never opens a host,
        /// takes a lease or mutates the save; it is used only for fixture assertions.
        /// </summary>
        public async Task<C04PhysicalSnapshot> ReadPhysicalSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            LocalTableRecoveryCapture capture = await LocalTableHost
                .CaptureRecoveryAsync(_saveDirectory, cancellationToken)
                .ConfigureAwait(false);
            LocalTableRecoveryFacts facts = capture.Facts;
            string audit = LocalPlayerActionScenario.ReadPhysicalAudit(
                capture.AuthoritySaveBytes,
                capture.CheatStatePayload);
            return new C04PhysicalSnapshot
            {
                stateVersion = facts.StateVersion,
                eventSeq = facts.EventSeq,
                cheatFingerprint = facts.CheatFingerprint,
                tilesSha256 = facts.Physical.TilesSha256,
                audit = audit,
                ledgerEntryCount = facts.Ledger.EntryCount,
                balance = facts.Ledger.Balances[0],
                tileCount = facts.Physical.TileCount,
                uniqueCount = facts.Physical.UniqueCount
            };
        }

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
            return new C04PlayerCheatTicketChannel(channel, ticket, _capture);
        }

        private sealed class C04PlayerCheatTicketChannel : ITableChannel, ILocalTableTicketSource
        {
            private readonly ITableChannel _inner;
            private readonly C04RawFrameCapture? _capture;

            public C04PlayerCheatTicketChannel(
                ITableChannel inner,
                string sessionTicket,
                C04RawFrameCapture? capture)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                SessionTicket = sessionTicket ?? throw new ArgumentNullException(nameof(sessionTicket));
                _capture = capture;
            }

            public string SessionTicket { get; }

            public bool IsOpen => _inner.IsOpen;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                _capture?.RecordSent(wireEnvelope);
                return _inner.SendAsync(wireEnvelope, cancellationToken);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                _capture?.RecordReceived(frame);
                return frame;
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}

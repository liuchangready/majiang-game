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
    /// </summary>
    public sealed class LocalTableChannelFactory : ITableChannelFactory
    {
        public const string DefaultRoomId = "local-main";
        public const string ClientBuild = "unity-p42b-localhost";

        private readonly string _roomId;
        private readonly string _ownerInstanceId;
        private readonly string _saveDirectory;
        private readonly TimeSpan _leaseDuration;

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
            _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(10);
        }

        /// <summary>Stable room id used by the single local production table.</summary>
        public string RoomId => _roomId;

        /// <summary>Absolute directory the pure .NET host owns; never the outbox.</summary>
        public string SaveDirectory => _saveDirectory;

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

            return CreateCoreAsync(cancellationToken);
        }

        private async Task<ITableChannel> CreateCoreAsync(CancellationToken cancellationToken)
        {
            var options = new LocalTableHostOptions
            {
                SaveDirectory = _saveDirectory,
                RoomId = _roomId,
                RulesetVersion = 1,
                OwnerInstanceId = _ownerInstanceId,
                LeaseDuration = _leaseDuration,
                AutoFlushInterval = TimeSpan.FromMilliseconds(500)
            };

            cancellationToken.ThrowIfCancellationRequested();

            ITableChannel channel = await LocalTableHost
                .CreateChannelAsync(options, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // One real one-use ticket per attempt. It is consumed by the host when
            // HELLO arrives and expires unused after 30 seconds.
            string ticket = LocalTableHost.IssueTicket(options);
            return new LocalTableTicketChannel(channel, ticket);
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

            public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
                _inner.ReceiveAsync(cancellationToken);

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.TableRuntime;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// A real Kestrel host on a loopback, OS-assigned (owned) port, built through the
    /// production <see cref="TableServiceWebHost.Build"/> composition. Tests never point at a
    /// fake transport: admission, framing, drain and TLS all run against the real listener.
    /// </summary>
    public sealed class TableServiceTestHost : IAsyncDisposable
    {
        private TableServiceHostRuntime? _runtime;

        public TableServiceOptions Options { get; private set; } = null!;

        public IReadOnlyList<TableTestSigner> Signers { get; private set; } = Array.Empty<TableTestSigner>();

        public TableTestSigner Signer => Signers[0];

        public int Port { get; private set; }

        public string WsUrl => "ws://127.0.0.1:" + Port + TableServiceWebHost.TablePath;

        public string BaseUrl => "http://127.0.0.1:" + Port;

        /// <summary>
        /// The exact room host the production admission path created. Used only to inspect the
        /// same durable store/outbox the actor committed through; it is never a second authority.
        /// </summary>
        public RoomHost GetRoomHost(string roomId)
        {
            if (_runtime == null)
            {
                throw new InvalidOperationException("The test host is not running.");
            }

            var registry = _runtime.Services.GetRequiredService<ITableHostRegistry>();
            if (!registry.TryGet(roomId, out RoomHost? host) || host == null)
            {
                throw new InvalidOperationException("No production room host exists for '" + roomId + "'.");
            }

            return host;
        }

        public static async Task<TableServiceTestHost> StartAsync(
            Action<TableServiceOptions>? configure = null,
            ITableRoundSource? roundSource = null,
            ITableClock? clock = null)
        {
            var signer = new TableTestSigner("test-key-1");
            return await StartAsync(new[] { signer }, configure, roundSource, clock).ConfigureAwait(false);
        }

        public static async Task<TableServiceTestHost> StartAsync(
            IReadOnlyList<TableTestSigner> signers,
            Action<TableServiceOptions>? configure = null,
            ITableRoundSource? roundSource = null,
            ITableClock? clock = null)
        {
            if (signers == null || signers.Count == 0)
            {
                throw new ArgumentException("A test host needs at least one signer.", nameof(signers));
            }

            var options = new TableServiceOptions
            {
                HandshakeTimeoutMs = 2000,
                HeartbeatIntervalMs = 500,
                IdleDeadMs = 60_000,
                RoomTickIntervalMs = 50,
                LeaseTtlSeconds = 5
            };
            configure?.Invoke(options);

            var tuples = new List<(string Pem, string? Kid)>(signers.Count);
            foreach (TableTestSigner signer in signers)
            {
                tuples.Add((signer.PublicKeyPem, signer.Kid));
            }

            var keys = new StaticTableTicketKeyProvider(tuples, DateTimeOffset.UtcNow);
            TableServiceHostRuntime runtime =
                TableServiceWebHost.Build(options, keys, IPAddress.Loopback, 0, roundSource, clock);
            await runtime.StartAsync().ConfigureAwait(false);
            return new TableServiceTestHost
            {
                _runtime = runtime,
                Options = options,
                Signers = signers,
                Port = runtime.GetBoundPort()
            };
        }

        /// <summary>Admits a player end-to-end and returns the client positioned after WELCOME.</summary>
        public async Task<TableTestClient> ConnectPlayerAsync(
            string room,
            string accountId,
            int seat,
            TableTestTicket? ticket = null)
        {
            TableTestTicket effective = ticket ?? new TableTestTicket(accountId, room, TableAdmissionRole.Player, seat);
            TableTestClient client = await TableTestClient
                .ConnectAsync(WsUrl + "?room=" + Uri.EscapeDataString(room))
                .ConfigureAwait(false);
            await client.AdmitAsync(Signer.Sign(effective)).ConfigureAwait(false);
            return client;
        }

        /// <summary>
        /// Admits a player over a tiny-receive-buffer raw socket so a downstream stall is
        /// deterministic (loopback autotuning would otherwise absorb megabytes undrained).
        /// </summary>
        public async Task<TableTestClient> ConnectPlayerOverThrottledSocketAsync(
            string room,
            string accountId,
            int seat,
            int receiveBufferSize)
        {
            var ticket = new TableTestTicket(accountId, room, TableAdmissionRole.Player, seat);
            TableTestClient client = await TableTestClient
                .ConnectOverThrottledSocketAsync(WsUrl + "?room=" + Uri.EscapeDataString(room), receiveBufferSize)
                .ConfigureAwait(false);
            await client.AdmitAsync(Signer.Sign(ticket)).ConfigureAwait(false);
            return client;
        }

        public async Task<TableTestClient> ConnectSpectatorAsync(
            string room,
            string accountId,
            TableTestTicket? ticket = null)
        {
            TableTestTicket effective = ticket ?? new TableTestTicket(accountId, room, TableAdmissionRole.Spectator, null);
            TableTestClient client = await TableTestClient
                .ConnectAsync(WsUrl + "?room=" + Uri.EscapeDataString(room))
                .ConfigureAwait(false);
            await client.AdmitAsync(Signer.Sign(effective)).ConfigureAwait(false);
            return client;
        }

        /// <summary>Unauthenticated handshake probe that surfaces exact HTTP status codes.</summary>
        public async Task<(int Status, string RetryAfter)> ProbeHandshakeAsync(string query)
        {
            using var handler = new HttpClientHandler();
            using var http = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + TableServiceWebHost.TablePath + query);
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation(
                "Sec-WebSocket-Key",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            using HttpResponseMessage response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            return ((int)response.StatusCode, response.Headers.RetryAfter?.ToString() ?? string.Empty);
        }

        public async Task<int> GetAsync(string path)
        {
            using var http = new HttpClient();
            using HttpResponseMessage response = await http
                .GetAsync(BaseUrl + path)
                .ConfigureAwait(false);
            return (int)response.StatusCode;
        }

        public async Task<(int Status, string Body)> PostAsync(string path, string? adminToken)
        {
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
            if (adminToken != null)
            {
                request.Headers.TryAddWithoutValidation("X-Admin-Token", adminToken);
            }

            using HttpResponseMessage response = await http
                .SendAsync(request)
                .ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        public async ValueTask DisposeAsync()
        {
            if (_runtime != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _runtime.StopAsync(timeout.Token).ConfigureAwait(false);
                if (_runtime.Services is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (_runtime.Services is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                _runtime = null;
            }
        }
    }

    /// <summary>Helper for tests that need a second, independent signing key.</summary>
    public static class TableTestKeyFactory
    {
        public static TableTestSigner Create(string kid)
        {
            return new TableTestSigner(kid);
        }
    }

    /// <summary>Client-side transport guard so a hung server fails a test instead of hanging it.</summary>
    internal static class TestTimeout
    {
        public static TimeSpan Default = TimeSpan.FromSeconds(10);

        public static CancellationTokenSource Linked(TimeSpan? timeout = null)
        {
            return new CancellationTokenSource(timeout ?? Default);
        }
    }

    /// <summary>Marker namespace import for WebSocket close status comparisons.</summary>
    internal static class CloseStatusExtensions
    {
        public static int ToCode(this WebSocketCloseStatus? status)
        {
            return (int)(status ?? 0);
        }
    }
}

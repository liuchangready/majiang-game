using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Reads the TableService's deliberately public, read-only projection when the lobby is
    /// deployed with its in-memory store. It carries no admission authority and never reads
    /// player snapshots, events or private table state.
    /// </summary>
    public sealed class HttpRoomProjectionReader : IRoomProjectionReader, IRoomProjectionRetirer, IAsyncDisposable
    {
        private readonly Uri _baseUri;
        private readonly HttpClient _client;
        private readonly X509Certificate2? _trustedRoot;
        private readonly string _serviceKey;

        public HttpRoomProjectionReader(
            string baseUrl,
            string? trustedRootCertificatePath = null,
            string? serviceKey = null,
            HttpMessageHandler? handler = null)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                (parsed.Scheme != Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(serviceKey)))
            {
                throw new ArgumentException("A credentialed TableService URL must use HTTPS.", nameof(baseUrl));
            }

            _baseUri = parsed;
            _serviceKey = serviceKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(trustedRootCertificatePath))
            {
                _trustedRoot = new X509Certificate2(trustedRootCertificatePath);
            }

            if (handler == null)
            {
                var sockets = new SocketsHttpHandler();
                sockets.AllowAutoRedirect = false;
                if (_trustedRoot != null)
                {
                    sockets.SslOptions.RemoteCertificateValidationCallback = ValidateTableServiceCertificate;
                }

                handler = sockets;
            }

            _client = new HttpClient(handler, disposeHandler: true);
            _client.Timeout = TimeSpan.FromSeconds(3);
        }

        public async Task<PublicRoomProjectionBatch> ReadAsync(
            IReadOnlyList<string> roomIds,
            CancellationToken cancellationToken)
        {
            if (roomIds == null)
            {
                throw new ArgumentNullException(nameof(roomIds));
            }

            var items = new List<PublicRoomProjection>(roomIds.Count);
            foreach (string roomId in roomIds)
            {
                string path = "internal/rooms/" + Uri.EscapeDataString(roomId) + "/projection";
                using HttpResponseMessage response = await _client.GetAsync(
                    new Uri(_baseUri, path), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = document.RootElement;
                items.Add(new PublicRoomProjection(
                    root.GetProperty("roomId").GetString()!,
                    root.GetProperty("lifecycle").GetString()!,
                    root.GetProperty("seatsOccupied").GetInt32(),
                    root.GetProperty("spectatorCount").GetInt32(),
                    root.GetProperty("publicVersion").GetUInt64(),
                    root.GetProperty("rulesetVersion").GetUInt32(),
                    root.GetProperty("updatedAt").GetDateTimeOffset()));
            }

            return new PublicRoomProjectionBatch(items);
        }

        public async Task RetireFinishedRoomAsync(string roomId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_serviceKey))
            {
                throw new InvalidOperationException("The room-retirement service credential is not configured.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_baseUri, "internal/rooms/" + Uri.EscapeDataString(roomId) + "/retire"));
            request.Headers.TryAddWithoutValidation("X-Table-Admission-Key", _serviceKey);
            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    throw new HttpRequestException("TableService refused retirement because the room is not terminal.");
                }

                response.EnsureSuccessStatusCode();
                throw new HttpRequestException("TableService refused terminal room retirement.");
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _trustedRoot?.Dispose();
            return ValueTask.CompletedTask;
        }

        private bool ValidateTableServiceCertificate(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors policyErrors)
        {
            if (certificate == null ||
                (policyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable |
                                 SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            {
                return false;
            }

            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(_trustedRoot!);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using X509Certificate2 leaf = new X509Certificate2(certificate);
            return customChain.Build(leaf);
        }
    }
}

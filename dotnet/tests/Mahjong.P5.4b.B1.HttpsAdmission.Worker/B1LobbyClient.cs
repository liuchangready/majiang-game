using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// Strict HTTPS client for the production LobbyService. The server
    /// certificate is validated per connection against the run's isolated root
    /// (name/validity/chain); there is no accept-all callback and no machine
    /// trust change. All positive table tickets originate here (Lobby), never
    /// from this worker.
    /// </summary>
    public sealed class B1LobbyClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly X509Certificate2 _trustedRoot;

        public B1LobbyClient(Uri baseAddress, X509Certificate2 trustedRoot)
        {
            _trustedRoot = trustedRoot ?? throw new ArgumentNullException(nameof(trustedRoot));
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.Zero
            };
            handler.SslOptions.RemoteCertificateValidationCallback = ValidatePeer;
            _http = new HttpClient(handler)
            {
                BaseAddress = baseAddress,
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        public string? LastRejectionReason { get; private set; }

        private bool ValidatePeer(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors policyErrors)
        {
            bool accepted = StrictRootTrust.Validate(
                _trustedRoot,
                certificate,
                chain,
                policyErrors,
                out string? reason);
            if (!accepted)
            {
                LastRejectionReason = reason;
            }

            return accepted;
        }

        public async Task<LobbyHttpResult> GetAsync(string path, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LobbyHttpResult> GetAuthorizedAsync(
            string path,
            string sessionToken,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + sessionToken);
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LobbyHttpResult> PostAsync(
            string path,
            object? body,
            string? sessionToken,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (sessionToken != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + sessionToken);
            }

            if (idempotencyKey != null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            if (body != null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json");
            }

            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<LobbyHttpResult> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool replayed = response.Headers.TryGetValues("Idempotent-Replayed", out var values);
            return new LobbyHttpResult(
                (int)response.StatusCode,
                body,
                replayed,
                response.Headers.TryGetValues("Content-Type", out var contentType)
                    ? string.Join(",", contentType)
                    : string.Empty);
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        public sealed class LobbyHttpResult
        {
            public LobbyHttpResult(int statusCode, string body, bool replayed, string contentType)
            {
                StatusCode = statusCode;
                Body = body;
                Replayed = replayed;
                ContentType = contentType;
            }

            public int StatusCode { get; }

            public string Body { get; }

            public bool Replayed { get; }

            public string ContentType { get; }

            public JsonElement Json
            {
                get
                {
                    using JsonDocument document = JsonDocument.Parse(Body);
                    return document.RootElement.Clone();
                }
            }

            /// <summary>Only the stable error code, never the raw response body.</summary>
            public string? ErrorCode
            {
                get
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(Body);
                        if (document.RootElement.ValueKind == JsonValueKind.Object &&
                            document.RootElement.TryGetProperty("code", out JsonElement code) &&
                            code.ValueKind == JsonValueKind.String)
                        {
                            return code.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                    }

                    return null;
                }
            }
        }
    }
}

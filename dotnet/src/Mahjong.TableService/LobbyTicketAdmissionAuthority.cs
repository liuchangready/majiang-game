using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.TableService
{
    public interface ITableTicketAdmissionAuthority
    {
        Task<bool> ConfirmCurrentAssignmentAsync(
            ValidatedTableTicket ticket,
            string compactTicket,
            CancellationToken cancellationToken);
    }

    public sealed class LocalTicketAdmissionAuthority : ITableTicketAdmissionAuthority
    {
        public Task<bool> ConfirmCurrentAssignmentAsync(
            ValidatedTableTicket ticket,
            string compactTicket,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>
    /// Online MVP admission check. TableService first validates the compact signature and
    /// locally reserves its one-time JTI, then Lobby atomically confirms the still-current
    /// assignment and consumes that issuance record before TableService sends WELCOME.
    /// </summary>
    public sealed class LobbyTicketAdmissionAuthority : ITableTicketAdmissionAuthority, IDisposable
    {
        private readonly Uri _endpoint;
        private readonly string _sharedKey;
        private readonly HttpClient _client;
        private readonly X509Certificate2? _trustedRoot;

        public LobbyTicketAdmissionAuthority(TableServiceOptions options, HttpMessageHandler? testHandler = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _endpoint = new Uri(options.LobbyAdmissionBaseUrl!.TrimEnd('/') + "/internal/table-admissions/consume");
            _sharedKey = options.LobbyAdmissionSharedKey!;
            HttpMessageHandler handler = testHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false };
            if (testHandler == null && !string.IsNullOrWhiteSpace(options.LobbyAdmissionTlsRootCertificatePath))
            {
                _trustedRoot = new X509Certificate2(options.LobbyAdmissionTlsRootCertificatePath);
                ((SocketsHttpHandler)handler).SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
            }

            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        public async Task<bool> ConfirmCurrentAssignmentAsync(
            ValidatedTableTicket ticket,
            string compactTicket,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new AdmissionRequest(
                    compactTicket,
                    ticket.RoomId,
                    ticket.AssignmentId,
                    ticket.AccountId,
                    ticket.Role.ToString(),
                    ticket.SeatId))
            };
            request.Headers.TryAddWithoutValidation("X-Table-Admission-Key", _sharedKey);
            using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return true;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden ||
                response.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return false;
        }

        public void Dispose()
        {
            _client.Dispose();
            _trustedRoot?.Dispose();
        }

        private bool ValidateCertificate(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors policyErrors)
        {
            if (certificate == null || _trustedRoot == null ||
                (policyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable |
                                 SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            {
                return false;
            }

            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(_trustedRoot);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var leaf = new X509Certificate2(certificate);
            return customChain.Build(leaf);
        }

        private sealed record AdmissionRequest(
            string Ticket,
            string RoomId,
            string AssignmentId,
            string AccountId,
            string Role,
            int? SeatId);
    }
}

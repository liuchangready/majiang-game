using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.TableService;
using Xunit;

namespace Mahjong.TableService.Tests
{
    public sealed class DeploymentConfigurationTests
    {
        [Fact]
        public void RemoteBindRequiresTlsCertificate()
        {
            var options = new TableServiceOptions { BindAddress = "0.0.0.0" };

            Assert.Throws<ArgumentException>(() => options.Validate());
        }

        [Fact]
        public void LoopbackRemainsAvailableWithoutCertificate()
        {
            var options = new TableServiceOptions { BindAddress = "127.0.0.1" };

            options.Validate();
        }

        [Fact]
        public void RemoteBindAcceptsConfiguredCertificate()
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var options = new TableServiceOptions
            {
                BindAddress = "0.0.0.0",
                ServerCertificate = certificate
            };

            options.Validate();
        }

        [Fact]
        public void RequiredLobbyAdmissionNeedsHttpsAndStrongSharedCredential()
        {
            var missing = new TableServiceOptions { RequireLobbyAdmission = true };
            Assert.Throws<ArgumentException>(() => missing.Validate());

            var weak = new TableServiceOptions
            {
                RequireLobbyAdmission = true,
                LobbyAdmissionBaseUrl = "http://127.0.0.1:5183",
                LobbyAdmissionSharedKey = "short"
            };
            Assert.Throws<ArgumentException>(() => weak.Validate());

            var valid = new TableServiceOptions
            {
                RequireLobbyAdmission = true,
                LobbyAdmissionBaseUrl = "https://127.0.0.1:5183",
                LobbyAdmissionSharedKey = new string('k', 48)
            };
            valid.Validate();
        }

        [Fact]
        public async Task LobbyAdmissionRedirectFailsClosedAndDoesNotFollowLocation()
        {
            var options = new TableServiceOptions
            {
                RequireLobbyAdmission = true,
                LobbyAdmissionBaseUrl = "https://127.0.0.1:5183",
                LobbyAdmissionSharedKey = new string('k', 48)
            };
            options.Validate();
            var handler = new RedirectCountingHandler();
            using var authority = new LobbyTicketAdmissionAuthority(options, handler);
            var ticket = new ValidatedTableTicket(
                "issuer", "account", "audience", "room", "assignment", TableAdmissionRole.Player, 0,
                "jti", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 5, 1, 1, "kid");

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                authority.ConfirmCurrentAssignmentAsync(ticket, "compact-ticket", default));
            Assert.Equal(1, handler.RequestCount);
        }

        private sealed class RedirectCountingHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri("https://attacker.invalid/collect") }
                });
            }
        }
    }
}

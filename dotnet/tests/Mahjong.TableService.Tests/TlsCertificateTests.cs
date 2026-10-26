using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Isolated in-memory WSS: a test-only root CA signs a leaf served by the real Kestrel,
    /// and the client validates through a per-connection callback with CustomRootTrust — the
    /// machine trust store is never touched and the callback replicates strict validation
    /// (chain to the injected root AND name AND freshness); it never returns true blindly.
    /// </summary>
    public sealed class TlsCertificateTests
    {
        [Fact]
        public async Task WssWithIsolatedChain_AdmitsThroughTheFrozenHandshake()
        {
            using X509Certificate2 root = CreateTestRoot(DateTimeOffset.UtcNow.AddMinutes(-10), out DateTimeOffset _);
            using X509Certificate2 leaf = CreateLeaf(root, "localhost", includeLoopback: true);
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.ServerCertificate = leaf);

            string url = "wss://127.0.0.1:" + host.Port + TableServiceWebHost.TablePath + "?room=room-tls";
            TableTestClient client = await TableTestClient.ConnectAsync(
                url,
                StrictCallback(root));
            try
            {
                string ticket = host.Signer.Sign(new TableTestTicket(
                    "acct-tls", "room-tls", TableAdmissionRole.Player, 0));
                await client.SendAsync(TableWire.Hello(ticket));
                TableWireMessage message = await client.ReceiveAsync(TimeSpan.FromSeconds(15));
                Assert.False(message.IsClose, "closed: " + message.WsCloseReason);
                Assert.Equal(Envelope.BodyOneofCase.Welcome, message.Envelope!.BodyCase);
                Assert.Equal(Contracts.Wire.Role.Player, message.Envelope.Welcome.Role);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task WrongTrustedRoot_IsRejected()
        {
            using X509Certificate2 realRoot = CreateTestRoot(DateTimeOffset.UtcNow.AddMinutes(-10), out DateTimeOffset _);
            using X509Certificate2 otherRoot = CreateTestRoot(DateTimeOffset.UtcNow.AddMinutes(-10), out DateTimeOffset _);
            using X509Certificate2 leaf = CreateLeaf(realRoot, "localhost", includeLoopback: true);
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.ServerCertificate = leaf);

            string url = "wss://127.0.0.1:" + host.Port + TableServiceWebHost.TablePath + "?room=room-tls";
            await Assert.ThrowsAnyAsync<Exception>(() => TableTestClient.ConnectAsync(
                url,
                StrictCallback(otherRoot)));
        }

        [Fact]
        public async Task NameMismatch_IsRejected()
        {
            using X509Certificate2 root = CreateTestRoot(DateTimeOffset.UtcNow.AddMinutes(-10), out DateTimeOffset _);
            // The leaf names some other host entirely; we connect to 127.0.0.1.
            using X509Certificate2 leaf = CreateLeaf(root, "real-table-service.example", includeLoopback: false);
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.ServerCertificate = leaf);

            string url = "wss://127.0.0.1:" + host.Port + TableServiceWebHost.TablePath + "?room=room-tls";
            await Assert.ThrowsAnyAsync<Exception>(() => TableTestClient.ConnectAsync(
                url,
                StrictCallback(root)));
        }

        [Fact]
        public async Task ExpiredLeaf_IsRejected()
        {
            // The issuer's notBefore bounds its leaves: backdate the root so a leaf with a
            // long-past validity window can be issued at all.
            using X509Certificate2 root = CreateTestRoot(
                DateTimeOffset.UtcNow.AddDays(-40), out DateTimeOffset _);
            using X509Certificate2 leaf = CreateLeaf(
                root, "localhost", includeLoopback: true,
                DateTimeOffset.UtcNow.AddDays(-30),
                DateTimeOffset.UtcNow.AddDays(-1));
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.ServerCertificate = leaf);

            string url = "wss://127.0.0.1:" + host.Port + TableServiceWebHost.TablePath + "?room=room-tls";
            await Assert.ThrowsAnyAsync<Exception>(() => TableTestClient.ConnectAsync(
                url,
                StrictCallback(root)));
        }

        /// <summary>
        /// Strict validation against an injected root only: chain build under CustomRootTrust
        /// plus name/expiry/purpose checks. The OS chain check always reports
        /// RemoteCertificateChainErrors for an untrusted test root — that one error is exactly
        /// what the CustomRootTrust build re-validates, so it alone is masked; name mismatch
        /// and any other policy error stay fatal. Production peers keep doing the same
        /// against their own trusted roots — no trust bypass exists here.
        /// </summary>
        private static RemoteCertificateValidationCallback StrictCallback(X509Certificate2 trustedRoot)
        {
            return (sender, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate == null)
                {
                    return false;
                }

                if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                {
                    return false;
                }

                using X509Chain custom = new X509Chain();
                custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                custom.ChainPolicy.CustomTrustStore.Add(trustedRoot);
                custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return custom.Build(new X509Certificate2(certificate));
            };
        }

        private static X509Certificate2 CreateTestRoot(
            DateTimeOffset notBefore,
            out DateTimeOffset notAfter)
        {
            notAfter = DateTimeOffset.UtcNow.AddYears(2);
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=TableService P5.1 Test Root",
                key,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using X509Certificate2 selfSigned = request.CreateSelfSigned(
                notBefore,
                notAfter);
            // Re-import so the returned certificate owns its key independent of the builder.
            return new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
        }

        private static X509Certificate2 CreateLeaf(
            X509Certificate2 issuer,
            string subjectName,
            bool includeLoopback,
            DateTimeOffset? notBefore = null,
            DateTimeOffset? notAfter = null)
        {
            DateTimeOffset from = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-10);
            DateTimeOffset to = notAfter ?? DateTimeOffset.UtcNow.AddDays(30);
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=" + subjectName,
                key,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(subjectName);
            if (includeLoopback)
            {
                san.AddIpAddress(System.Net.IPAddress.Loopback);
            }

            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
            using X509Certificate2 publicLeaf = request.Create(
                issuer,
                from,
                to,
                new byte[] { 0x50, 0x35, 0x31, 0x01 });
            using X509Certificate2 withKey = publicLeaf.CopyWithPrivateKey(key);
            return new X509Certificate2(withKey.Export(X509ContentType.Pfx));
        }
    }
}

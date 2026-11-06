using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mahjong.P5_4b.B1.HttpsAdmission.Worker;
using Xunit;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Tests
{
    public sealed class StrictRootTrustTests
    {
        [Fact]
        public void ValidLeafUnderThePinnedRootIsAccepted()
        {
            using TestChain chain = TestChain.Create(validLeaf: true);
            bool accepted = StrictRootTrust.Validate(
                chain.Root,
                chain.Leaf,
                null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                out string? reason);
            Assert.True(accepted, reason);
        }

        [Fact]
        public void UntrustedRootIsRejected()
        {
            using TestChain chain = TestChain.Create(validLeaf: true);
            using TestChain other = TestChain.Create(validLeaf: true);
            bool accepted = StrictRootTrust.Validate(
                other.Root,
                chain.Leaf,
                null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                out string? reason);
            Assert.False(accepted);
            Assert.NotNull(reason);
        }

        [Fact]
        public void HostNameMismatchIsRejected()
        {
            using TestChain chain = TestChain.Create(validLeaf: true);
            bool accepted = StrictRootTrust.Validate(
                chain.Root,
                chain.Leaf,
                null,
                SslPolicyErrors.RemoteCertificateNameMismatch |
                SslPolicyErrors.RemoteCertificateChainErrors,
                out string? reason);
            Assert.False(accepted);
            Assert.Contains("policy error", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExpiredLeafIsRejected()
        {
            using TestChain chain = TestChain.Create(validLeaf: false);
            bool accepted = StrictRootTrust.Validate(
                chain.Root,
                chain.Leaf,
                null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                out string? reason);
            Assert.False(accepted);
            Assert.NotNull(reason);
        }

        [Fact]
        public void MissingPeerCertificateIsRejected()
        {
            using TestChain chain = TestChain.Create(validLeaf: true);
            bool accepted = StrictRootTrust.Validate(
                chain.Root,
                null,
                null,
                SslPolicyErrors.None,
                out string? reason);
            Assert.False(accepted);
            Assert.Contains("no peer certificate", reason, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class TestChain : IDisposable
        {
            private TestChain(X509Certificate2 root, X509Certificate2 leaf)
            {
                Root = root;
                Leaf = leaf;
            }

            public X509Certificate2 Root { get; }

            public X509Certificate2 Leaf { get; }

            public static TestChain Create(bool validLeaf)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var rootRequest = new CertificateRequest(
                    "CN=B1 Root " + Guid.NewGuid().ToString("N"),
                    rootKey,
                    HashAlgorithmName.SHA256);
                rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                rootRequest.CertificateExtensions.Add(
                    new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                using X509Certificate2 rootWithKey = rootRequest.CreateSelfSigned(
                    now.AddDays(-40),
                    now.AddDays(30));
                var root = new X509Certificate2(rootWithKey.Export(X509ContentType.Cert));

                using ECDsa leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var leafRequest = new CertificateRequest(
                    "CN=localhost",
                    leafKey,
                    HashAlgorithmName.SHA256);
                leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                leafRequest.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                        true));
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(System.Net.IPAddress.Loopback);
                leafRequest.CertificateExtensions.Add(san.Build());
                DateTimeOffset notBefore = validLeaf ? now.AddHours(-1) : now.AddDays(-30);
                DateTimeOffset notAfter = validLeaf ? now.AddDays(2) : now.AddDays(-1);
                using X509Certificate2 publicLeaf = leafRequest.Create(
                    rootWithKey,
                    notBefore,
                    notAfter,
                    RandomNumberGenerator.GetBytes(8));
                using X509Certificate2 leafWithKey = publicLeaf.CopyWithPrivateKey(leafKey);
                var leaf = new X509Certificate2(leafWithKey.Export(X509ContentType.Pfx));
                return new TestChain(root, leaf);
            }

            public void Dispose()
            {
                Root.Dispose();
                Leaf.Dispose();
            }
        }
    }
}

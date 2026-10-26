using System;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.Client.Wss.Tests
{
    /// <summary>
    /// In-memory per-test certificate authority. Keys and certificates live only
    /// inside the test process: nothing is written to or read from the machine
    /// certificate store, and every instance mints a uniquely named root so two
    /// tests can never accidentally trust each other's authority.
    /// </summary>
    internal sealed class TestCertificateAuthority : IDisposable
    {
        private readonly RSA _rootKey;
        private readonly RSA _leafKey;

        private TestCertificateAuthority(
            RSA rootKey,
            RSA leafKey,
            X509Certificate2 root,
            X509Certificate2 serverLeaf)
        {
            _rootKey = rootKey;
            _leafKey = leafKey;
            Root = root;
            ServerLeaf = serverLeaf;
        }

        /// <summary>Public-only root used as the client's sole custom trust anchor.</summary>
        public X509Certificate2 Root { get; }

        /// <summary>Server leaf carrying its private key, presented by the test endpoint.</summary>
        public X509Certificate2 ServerLeaf { get; }

        /// <summary>Root plus a currently valid server leaf for the given loopback identity.</summary>
        public static TestCertificateAuthority CreateServerAuthority(string? leafDnsName, IPAddress? leafAddress)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return CreateServerAuthority(leafDnsName, leafAddress, now.AddHours(-1), now.AddDays(2));
        }

        public static TestCertificateAuthority CreateServerAuthority(
            string? leafDnsName,
            IPAddress? leafAddress,
            DateTimeOffset leafNotBefore,
            DateTimeOffset leafNotAfter)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RSA rootKey = RSA.Create(2048);
            RSA leafKey = RSA.Create(2048);
            X509Certificate2? root = null;
            X509Certificate2? serverLeaf = null;
            try
            {
                string rootName = "CN=Mahjong V5 Loopback Test Root " + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                var rootRequest = new CertificateRequest(
                    rootName,
                    rootKey,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
                rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    critical: true));
                rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
                // The root validity window must cover the requested leaf window,
                // including deliberately expired leaves in the negative tests.
                DateTimeOffset rootNotBefore = leafNotBefore.AddDays(-1);
                DateTimeOffset rootNotAfter = leafNotAfter > now
                    ? leafNotAfter.AddDays(1)
                    : now.AddDays(30);
                X509Certificate2 rootWithKey = rootRequest.CreateSelfSigned(rootNotBefore, rootNotAfter);
                try
                {
                    root = new X509Certificate2(rootWithKey.Export(X509ContentType.Cert));
                }
                catch
                {
                    rootWithKey.Dispose();
                    throw;
                }

                string leafSubject = "CN=" + (leafDnsName ?? (leafAddress != null ? leafAddress.ToString() : "mahjong-v5-test-server"));
                var leafRequest = new CertificateRequest(
                    leafSubject,
                    leafKey,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
                leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: false));
                leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    critical: false));
                var names = new SubjectAlternativeNameBuilder();
                if (leafDnsName != null)
                {
                    names.AddDnsName(leafDnsName);
                }

                if (leafAddress != null)
                {
                    names.AddIpAddress(leafAddress);
                }

                leafRequest.CertificateExtensions.Add(names.Build());

                using (X509Certificate2 unsignedLeaf = leafRequest.Create(
                    rootWithKey,
                    leafNotBefore,
                    leafNotAfter,
                    RandomNumberGenerator.GetBytes(12)))
                using (X509Certificate2 leafWithKey = unsignedLeaf.CopyWithPrivateKey(leafKey))
                {
                    // Re-import so SslStream gets a usable private key handle on
                    // Windows (same pattern as TestCertificates.CreateSelfSigned).
                    serverLeaf = new X509Certificate2(leafWithKey.Export(X509ContentType.Pfx));
                }

                rootWithKey.Dispose();
                return new TestCertificateAuthority(rootKey, leafKey, root, serverLeaf);
            }
            catch
            {
                root?.Dispose();
                serverLeaf?.Dispose();
                rootKey.Dispose();
                leafKey.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Root.Dispose();
            ServerLeaf.Dispose();
            _rootKey.Dispose();
            _leafKey.Dispose();
        }
    }

    /// <summary>
    /// Strict certificate policy for the positive WSS trust-chain test. A peer
    /// certificate is accepted only when all of the following hold:
    /// a certificate was presented; the requested host name matches the
    /// certificate (platform name check); the current time is inside the leaf
    /// validity window; and the leaf builds a completely clean chain to exactly
    /// one trust anchor - this test's own root via
    /// <see cref="X509ChainTrustMode.CustomRootTrust"/>. The machine store is
    /// never consulted for trust, never modified, and validation is never
    /// disabled: any other outcome is a hard reject with a recorded reason.
    /// </summary>
    internal static class StrictTestChainPolicy
    {
        private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

        /// <returns>null when the certificate must be accepted, otherwise the rejection reason.</returns>
        public static string? Evaluate(
            X509Certificate2 trustedRoot,
            X509Certificate? certificate,
            X509Chain? handshakeChain,
            SslPolicyErrors policyErrors)
        {
            if (certificate is not X509Certificate2 leaf)
            {
                return "no peer certificate was presented";
            }

            if (policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return "hostname does not match the certificate subject alternative names";
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < leaf.NotBefore || now > leaf.NotAfter)
            {
                return "certificate validity window does not include the current time";
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            // The per-test CA publishes no CRL/OCSP endpoints; revocation is not
            // weakened anywhere else (flags stay NoFlag, status must be NoError).
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthOid));
            if (handshakeChain != null)
            {
                foreach (X509ChainElement element in handshakeChain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            if (!chain.Build(leaf))
            {
                return "the certificate chain does not build to the trusted test root";
            }

            foreach (X509ChainStatus status in chain.ChainStatus)
            {
                if (status.Status != X509ChainStatusFlags.NoError)
                {
                    return "chain status is not clean: " + status.Status;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Test-only connection factory: creates real <see cref="ClientWebSocket"/>
    /// instances whose certificate callback enforces <see cref="StrictTestChainPolicy"/>
    /// against this test's own root. Only channels constructed with this factory
    /// use it - the production default factory and the default OS validation are
    /// untouched, which the existing self-signed rejection test keeps proving.
    /// </summary>
    internal sealed class TrustedTestRootConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly X509Certificate2 _trustedRoot;
        private int _accepted;
        private int _rejected;

        public TrustedTestRootConnectionFactory(X509Certificate2 trustedRoot)
        {
            _trustedRoot = trustedRoot;
        }

        internal int AcceptedCertificates => Volatile.Read(ref _accepted);

        internal int RejectedCertificates => Volatile.Read(ref _rejected);

        internal string? LastRejectionReason { get; private set; }

        internal string? LastAcceptedThumbprint { get; private set; }

        public IWebSocketConnection Create()
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = Validate;
            return new PreconfiguredClientWebSocketConnection(socket);
        }

        private bool Validate(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors policyErrors)
        {
            string? failure = StrictTestChainPolicy.Evaluate(_trustedRoot, certificate, chain, policyErrors);
            if (failure != null)
            {
                LastRejectionReason = failure;
                Interlocked.Increment(ref _rejected);
                return false;
            }

            LastAcceptedThumbprint = (certificate as X509Certificate2)?.Thumbprint;
            Interlocked.Increment(ref _accepted);
            return true;
        }
    }

    /// <summary>
    /// <see cref="IWebSocketConnection"/> adapter around a caller-configured
    /// <see cref="ClientWebSocket"/>; the production
    /// <c>ClientWebSocketConnection</c> always creates its own socket.
    /// </summary>
    internal sealed class PreconfiguredClientWebSocketConnection : IWebSocketConnection
    {
        private readonly ClientWebSocket _socket;

        public PreconfiguredClientWebSocketConnection(ClientWebSocket socket)
        {
            _socket = socket;
        }

        public WebSocketState State => _socket.State;

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return _socket.ConnectAsync(endpoint, cancellationToken);
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return _socket.ReceiveAsync(buffer, cancellationToken);
        }

        public Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken)
        {
            return _socket.CloseOutputAsync(closeStatus, description, cancellationToken);
        }

        public void Abort()
        {
            _socket.Abort();
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}

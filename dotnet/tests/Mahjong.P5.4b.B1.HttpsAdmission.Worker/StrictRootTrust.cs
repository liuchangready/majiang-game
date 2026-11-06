using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// Strict per-connection certificate policy for the B1 controlled HTTPS/WSS
    /// chain. A peer is accepted only when:
    ///   * it presented a certificate;
    ///   * the platform host-name check passed (a name mismatch is fatal);
    ///   * the current time is inside the leaf validity window;
    ///   * the chain builds to exactly the run's custom root
    ///     (X509ChainTrustMode.CustomRootTrust, never the machine store);
    ///   * every chain element status is clean and the EKU is serverAuth.
    ///
    /// The machine trust store is never consulted or modified, and there is no
    /// "return true" fallback. Revocation is NoCheck because the disposable test
    /// root publishes no CRL/OCSP endpoint; every other check stays strict.
    /// </summary>
    public static class StrictRootTrust
    {
        private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

        public static bool Validate(
            X509Certificate2 trustedRoot,
            X509Certificate? certificate,
            X509Chain? handshakeChain,
            SslPolicyErrors policyErrors,
            out string? reason)
        {
            reason = null;
            if (trustedRoot == null)
            {
                throw new ArgumentNullException(nameof(trustedRoot));
            }

            if (certificate is not X509Certificate2 leaf)
            {
                reason = "no peer certificate was presented";
                return false;
            }

            // The test root is not in the machine store, so the handshake reports
            // RemoteCertificateChainErrors by construction. That single flag is
            // re-evaluated below against the explicit custom root. Name mismatch,
            // not-available, and every other platform policy error stay fatal.
            if ((policyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                reason = "platform policy error: " + policyErrors;
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < leaf.NotBefore.ToUniversalTime() || now > leaf.NotAfter.ToUniversalTime())
            {
                reason = "certificate validity window does not include the current time";
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
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
                reason = "the certificate chain does not build to the custom test root";
                return false;
            }

            foreach (X509ChainStatus status in chain.ChainStatus)
            {
                if (status.Status != X509ChainStatusFlags.NoError)
                {
                    reason = "chain status is not clean: " + status.Status;
                    return false;
                }
            }

            // A custom-root chain must actually terminate at the pinned root.
            bool sawPinnedRoot = false;
            foreach (X509ChainElement element in chain.ChainElements)
            {
                if (string.Equals(
                        element.Certificate.Thumbprint,
                        trustedRoot.Thumbprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    sawPinnedRoot = true;
                }
            }

            if (!sawPinnedRoot)
            {
                reason = "the built chain does not terminate at the pinned custom root";
                return false;
            }

            return true;
        }
    }
}

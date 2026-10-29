using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Mahjong.Contracts;

namespace Mahjong.TableService.PersistenceWorker
{
    /// <summary>
    /// The run's TEST-ONLY ES256 ticket signer. Unlike the accepted TableTestSigner (which
    /// mints a fresh keypair per instance), this signer PERSISTS its keypair to the run's
    /// private artifacts directory so independent worker invocations and host processes
    /// across a kill/restart chain all sign and validate against the SAME key. The host only
    /// ever receives the public PEM file; the private half never leaves the run's own
    /// artifacts and never reaches a log.
    /// </summary>
    public sealed class WorkerTicketSigner
    {
        private const string PrivateHeader = "-----BEGIN PRIVATE KEY-----";

        private const string PrivateFooter = "-----END PRIVATE KEY-----";

        private readonly ECDsa _key;

        private WorkerTicketSigner(string kid, ECDsa key)
        {
            Kid = kid;
            _key = key;
        }

        public string Kid { get; }

        /// <summary>
        /// The kid every minted ticket header must carry: the RFC 7638 JWK thumbprint —
        /// the SAME derivation the production entry point applies when it registers an
        /// operator PEM (Program.LoadTicketKeys registers kid=null, and the static provider
        /// then derives base64url(SHA-256(canonical JWK JSON))). A cross-process ticket
        /// signed with a free-form label kid can never resolve against that registry, so
        /// this signer derives its kid from the key material itself and the header always
        /// matches, byte for byte.
        /// </summary>
        private static string KidOf(ECDsa key)
        {
            ECParameters parameters = key.ExportParameters(false);
            string json = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" +
                          Base64Url(parameters.Q.X) + "\",\"y\":\"" +
                          Base64Url(parameters.Q.Y) + "\"}";
            return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        public string PublicPem => Pem("PUBLIC KEY", _key.ExportSubjectPublicKeyInfo());

        public string PrivatePem => Pem("PRIVATE KEY", _key.ExportPkcs8PrivateKey());

        public static WorkerTicketSigner Create()
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new WorkerTicketSigner(KidOf(key), key);
        }

        public static async Task<WorkerTicketSigner> LoadAsync(string directory)
        {
            string pem = await File.ReadAllTextAsync(Path.Combine(directory, "private.pem")).ConfigureAwait(false);
            string base64 = pem
                .Replace(PrivateHeader, string.Empty)
                .Replace(PrivateFooter, string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim();
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
            }
            catch (Exception)
            {
                key.Dispose();
                throw;
            }

            // kid.txt is informational evidence only; the header kid is always re-derived
            // from the key material so it can never drift from the production registry.
            return new WorkerTicketSigner(KidOf(key), key);
        }

        /// <summary>
        /// Mints a REAL frozen-contract PLAYER ticket: compact JWS, canonical base64url,
        /// the exact claim allowlist the read-only TableTicketValidator enforces, IEEE P1363
        /// ES256 signature. Mirrors the accepted TableTestSigner.Sign byte for byte.
        /// </summary>
        public string SignTicket(string accountId, string roomId, int seatId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long iat = now;
            long exp = iat + 240;
            // One-time jti per signature — never a process-local counter: the E2E runner
            // mints tickets from a FRESH worker process per stage, so a per-process
            // sequence collides across stages (every process restarts at jti-1) while the
            // live host retains consumed jtis for the ticket validity window. The host
            // would then refuse an honest new admission as a DuplicateJti replay. A
            // random 128-bit id mirrors what a real lobby issuer mints.
            string jti = "jti-" + Guid.NewGuid().ToString("N");
            string assignment = "asgn-" + jti;

            var payload = new StringBuilder();
            payload.Append("{\"iss\":\"mahjong-lobby\"");
            payload.Append(",\"sub\":\"").Append(accountId).Append('"');
            payload.Append(",\"aud\":\"mahjong-table\"");
            payload.Append(",\"room_id\":\"").Append(roomId).Append('"');
            payload.Append(",\"assignment_id\":\"").Append(assignment).Append('"');
            payload.Append(",\"role\":\"PLAYER\"");
            payload.Append(",\"seat_id\":").Append(seatId.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"jti\":\"").Append(jti).Append('"');
            payload.Append(",\"iat\":").Append(iat);
            payload.Append(",\"exp\":").Append(exp);
            payload.Append(",\"protocol_major\":").Append(ProtocolConstants.Major);
            payload.Append(",\"ruleset_version\":").Append(ProtocolConstants.RulesetVersion);
            payload.Append(",\"projection_version\":").Append(ProtocolConstants.ProjectionVersion);
            payload.Append('}');

            string header = "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + Kid + "\"}";
            string headerSegment = Base64Url(Encoding.ASCII.GetBytes(header));
            string payloadSegment = Base64Url(Encoding.ASCII.GetBytes(payload.ToString()));
            byte[] signature = _key.SignData(
                Encoding.ASCII.GetBytes(headerSegment + "." + payloadSegment),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return headerSegment + "." + payloadSegment + "." + Base64Url(signature);
        }

        private static string Pem(string label, byte[] der)
        {
            string base64 = Convert.ToBase64String(der);
            var builder = new StringBuilder();
            builder.Append("-----BEGIN ").Append(label).Append("-----\n");
            for (int i = 0; i < base64.Length; i += 64)
            {
                builder.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
            }

            builder.Append("-----END ").Append(label).Append("-----\n");
            return builder.ToString();
        }

        private static string Base64Url(ReadOnlySpan<byte> bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Contracts;

namespace Mahjong.TableService.Tests
{
    /// <summary>One mintable ticket description. Unset fields get valid defaults.</summary>
    public sealed record TableTestTicket(
        string AccountId,
        string RoomId,
        TableAdmissionRole Role,
        int? SeatId,
        string? Jti = null,
        string? AssignmentId = null,
        string? Issuer = null,
        string? Audience = null,
        long? IssuedAtSeconds = null,
        long? ExpiresAtSeconds = null,
        TimeSpan? Ttl = null,
        long? NotBeforeSeconds = null,
        uint ProtocolMajor = ProtocolConstants.Major,
        uint RulesetVersion = ProtocolConstants.RulesetVersion,
        uint ProjectionVersion = ProtocolConstants.ProjectionVersion);

    /// <summary>
    /// Test-only ES256 signer that emits REAL frozen-contract ticket bytes: compact JWS,
    /// strict canonical base64url, the exact claim allowlist the read-only
    /// <see cref="TableTicketValidator"/> enforces, and IEEE P1363 ES256 signatures. It never
    /// runs in production code paths; the host under test validates its output through the
    /// production validator only. The private key lives here so the service never sees one.
    /// </summary>
    public sealed class TableTestSigner : IDisposable
    {
        public const string DefaultIssuer = "mahjong-lobby";
        public const string DefaultAudience = "mahjong-table";

        private static int _jtiCounter;

        private readonly ECDsa _key;

        public TableTestSigner(string kid)
        {
            Kid = string.IsNullOrWhiteSpace(kid) ? throw new ArgumentException("kid required") : kid;
            _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }

        public string Kid { get; }

        /// <summary>PEM PUBLIC KEY block, exactly what operator key files carry.</summary>
        public string PublicKeyPem
        {
            get
            {
                byte[] spki = _key.ExportSubjectPublicKeyInfo();
                string base64 = Convert.ToBase64String(spki);
                var lines = new List<string> { "-----BEGIN PUBLIC KEY-----" };
                for (int i = 0; i < base64.Length; i += 64)
                {
                    lines.Add(base64.Substring(i, Math.Min(64, base64.Length - i)));
                }

                lines.Add("-----END PUBLIC KEY-----");
                return string.Join("\n", lines);
            }
        }

        /// <summary>Computes the RFC 7638 JWK thumbprint kid, matching the validator's derivation.</summary>
        public string JwkThumbprintKid
        {
            get
            {
                ECParameters parameters = _key.ExportParameters(false);
                string json = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" +
                              Base64Url(parameters.Q.X) + "\",\"y\":\"" +
                              Base64Url(parameters.Q.Y) + "\"}";
                return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            }
        }

        public string Sign(TableTestTicket ticket)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long iat = ticket.IssuedAtSeconds ?? now;
            long exp = ticket.ExpiresAtSeconds ?? iat + (long)(ticket.Ttl ?? TimeSpan.FromSeconds(240)).TotalSeconds;
            string jti = ticket.Jti ?? "jti-" + System.Threading.Interlocked.Increment(ref _jtiCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string issuer = ticket.Issuer ?? DefaultIssuer;
            string audience = ticket.Audience ?? DefaultAudience;
            string assignment = ticket.AssignmentId ?? ("asgn-" + jti);
            string role = ticket.Role == TableAdmissionRole.Player ? "PLAYER" : "SPECTATOR";

            var payload = new StringBuilder();
            payload.Append("{\"iss\":\"").Append(issuer).Append('"');
            payload.Append(",\"sub\":\"").Append(ticket.AccountId).Append('"');
            payload.Append(",\"aud\":\"").Append(audience).Append('"');
            payload.Append(",\"room_id\":\"").Append(ticket.RoomId).Append('"');
            payload.Append(",\"assignment_id\":\"").Append(assignment).Append('"');
            payload.Append(",\"role\":\"").Append(role).Append('"');
            if (ticket.Role == TableAdmissionRole.Player)
            {
                payload.Append(",\"seat_id\":").Append(ticket.SeatId ?? 0);
            }

            payload.Append(",\"jti\":\"").Append(jti).Append('"');
            payload.Append(",\"iat\":").Append(iat);
            payload.Append(",\"exp\":").Append(exp);
            if (ticket.NotBeforeSeconds.HasValue)
            {
                payload.Append(",\"nbf\":").Append(ticket.NotBeforeSeconds.Value);
            }

            payload.Append(",\"protocol_major\":").Append(ticket.ProtocolMajor);
            payload.Append(",\"ruleset_version\":").Append(ticket.RulesetVersion);
            payload.Append(",\"projection_version\":").Append(ticket.ProjectionVersion);
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

        /// <summary>
        /// Signs arbitrary raw header/payload JSON — used by negative tests that must break
        /// exactly one frozen contract rule (unknown claim, duplicate claim, bad alg, padding).
        /// </summary>
        public string SignRaw(string headerJson, string payloadJson, bool padSegments = false)
        {
            string headerSegment = Base64Url(Encoding.ASCII.GetBytes(headerJson));
            string payloadSegment = Base64Url(Encoding.ASCII.GetBytes(payloadJson));
            if (padSegments)
            {
                headerSegment = Convert.ToBase64String(Encoding.ASCII.GetBytes(headerJson)).Replace('+', '-').Replace('/', '_');
                payloadSegment = Convert.ToBase64String(Encoding.ASCII.GetBytes(payloadJson)).Replace('+', '-').Replace('/', '_');
            }

            byte[] signature = _key.SignData(
                Encoding.ASCII.GetBytes(headerSegment + "." + payloadSegment),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            string signatureSegment = Base64Url(signature);
            if (padSegments)
            {
                signatureSegment = Convert.ToBase64String(signature).Replace('+', '-').Replace('/', '_');
            }

            return headerSegment + "." + payloadSegment + "." + signatureSegment;
        }

        public void Dispose()
        {
            _key.Dispose();
        }

        internal static string Base64Url(ReadOnlySpan<byte> bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}

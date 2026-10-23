using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mahjong.LobbyService
{
    public interface ITableTicketSigner
    {
        IssuedTableTicket Issue(TableTicketClaims claims);

        TableTicketClaims Validate(string compact, TableTicketExpectation expectation, DateTimeOffset now);

        JwksDocument GetJwks(DateTimeOffset now);

        string CurrentKid(DateTimeOffset now);

        void Rotate(DateTimeOffset now);
    }

    public sealed class TicketValidationException : Exception
    {
        public TicketValidationException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    public sealed record TicketPublicKey(string Kid, ECParameters Parameters, DateTimeOffset RetireAt);

    public sealed class TicketKeyRing
    {
        private readonly object _gate = new object();
        private readonly List<KeyEntry> _keys = new List<KeyEntry>();

        public string AddEphemeralSigningKey(DateTimeOffset now)
        {
            using (ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                ECParameters parameters = ecdsa.ExportParameters(true);
                return AddKey(parameters, hasPrivate: true, kid: null, now, makeSigning: true);
            }
        }

        public string AddSigningKeyFromPem(string pem, string? kid, DateTimeOffset now)
        {
            using (ECDsa ecdsa = ECDsa.Create())
            {
                ecdsa.ImportFromPem(pem);
                ECParameters parameters = ecdsa.ExportParameters(true);
                return AddKey(parameters, hasPrivate: true, kid, now, makeSigning: true);
            }
        }

        public string AddValidationKeyFromPem(string pem, string? kid, DateTimeOffset now)
        {
            using (ECDsa ecdsa = ECDsa.Create())
            {
                ecdsa.ImportFromPem(pem);
                ECParameters parameters = ecdsa.ExportParameters(false);
                return AddKey(parameters, hasPrivate: false, kid, now, makeSigning: false);
            }
        }

        public string Rotate(DateTimeOffset now, TimeSpan overlap)
        {
            if (overlap < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(overlap));
            }

            lock (_gate)
            {
                DateTimeOffset retireAt = now + overlap;
                foreach (KeyEntry key in _keys.Where(k => k.IsSigning))
                {
                    key.IsSigning = false;
                    if (key.RetireAt > retireAt)
                    {
                        key.RetireAt = retireAt;
                    }
                }

                KeyEntry newKey = CreateEphemeralEntry(now);
                _keys.Add(newKey);
                return newKey.Kid;
            }
        }

        public void Retire(string kid, DateTimeOffset now)
        {
            lock (_gate)
            {
                KeyEntry key = RequireKey(kid);
                key.RetireAt = now;
                key.IsSigning = false;
            }
        }

        public string ActiveKid(DateTimeOffset now)
        {
            lock (_gate)
            {
                KeyEntry? key = _keys
                    .Where(k => k.IsSigning && k.RetireAt > now && k.HasPrivate)
                    .OrderByDescending(k => k.CreatedAt)
                    .FirstOrDefault();
                if (key == null)
                {
                    throw new InvalidOperationException("No active table-ticket signing key is configured.");
                }

                return key.Kid;
            }
        }

        public TicketPublicKey FindValidationKey(string kid, DateTimeOffset now)
        {
            lock (_gate)
            {
                KeyEntry? key = _keys.FirstOrDefault(k =>
                    string.Equals(k.Kid, kid, StringComparison.Ordinal) && k.RetireAt > now);
                if (key == null)
                {
                    throw new TicketValidationException("TICKET_KEY_UNKNOWN", "The ticket key id is unknown or retired.");
                }

                return new TicketPublicKey(key.Kid, key.PublicParameters, key.RetireAt);
            }
        }

        public ECParameters? FindSigningParameters(DateTimeOffset now, out string kid)
        {
            lock (_gate)
            {
                KeyEntry? key = _keys
                    .Where(k => k.IsSigning && k.RetireAt > now && k.HasPrivate)
                    .OrderByDescending(k => k.CreatedAt)
                    .FirstOrDefault();
                if (key == null)
                {
                    kid = string.Empty;
                    return null;
                }

                kid = key.Kid;
                return key.PrivateParameters;
            }
        }

        public IReadOnlyList<TicketPublicKey> ListValidationKeys(DateTimeOffset now)
        {
            lock (_gate)
            {
                return _keys
                    .Where(k => k.RetireAt > now)
                    .OrderBy(k => k.CreatedAt)
                    .Select(k => new TicketPublicKey(k.Kid, k.PublicParameters, k.RetireAt))
                    .ToList();
            }
        }

        private string AddKey(
            ECParameters parameters,
            bool hasPrivate,
            string? kid,
            DateTimeOffset now,
            bool makeSigning)
        {
            ValidateP256(parameters);
            string computed = ComputeKid(parameters);
            if (kid != null && !string.Equals(kid, computed, StringComparison.Ordinal) && !IsSafeKid(kid))
            {
                throw new ArgumentException("Ticket key id must be 8-128 URL-safe characters.", nameof(kid));
            }

            string effectiveKid = string.IsNullOrEmpty(kid) ? computed : kid;
            lock (_gate)
            {
                if (_keys.Any(k => string.Equals(k.Kid, effectiveKid, StringComparison.Ordinal)))
                {
                    throw new ArgumentException("Duplicate ticket key id: " + effectiveKid, nameof(kid));
                }

                if (makeSigning)
                {
                    foreach (KeyEntry existing in _keys.Where(k => k.IsSigning))
                    {
                        existing.IsSigning = false;
                    }
                }

                var entry = new KeyEntry(
                    effectiveKid,
                    CopyPrivate(parameters),
                    CopyPublic(parameters),
                    hasPrivate,
                    makeSigning,
                    now,
                    DateTimeOffset.MaxValue);
                _keys.Add(entry);
                return effectiveKid;
            }
        }

        private KeyEntry CreateEphemeralEntry(DateTimeOffset now)
        {
            using (ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                ECParameters parameters = ecdsa.ExportParameters(true);
                ValidateP256(parameters);
                return new KeyEntry(
                    ComputeKid(parameters),
                    CopyPrivate(parameters),
                    CopyPublic(parameters),
                    hasPrivate: true,
                    isSigning: true,
                    now,
                    DateTimeOffset.MaxValue);
            }
        }

        private KeyEntry RequireKey(string kid)
        {
            KeyEntry? key = _keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
            if (key == null)
            {
                throw new ArgumentException("Unknown ticket key id: " + kid, nameof(kid));
            }

            return key;
        }

        private static void ValidateP256(ECParameters parameters)
        {
            if (!parameters.Curve.IsNamed ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("Only ECDSA P-256 table-ticket keys are accepted.");
            }

            if (parameters.Q.X == null || parameters.Q.X.Length != 32 ||
                parameters.Q.Y == null || parameters.Q.Y.Length != 32)
            {
                throw new ArgumentException("ECDSA P-256 public coordinates must be 32 bytes.");
            }
        }

        private static ECParameters CopyPrivate(ECParameters source)
        {
            return new ECParameters
            {
                Curve = source.Curve,
                D = source.D == null ? null : (byte[])source.D.Clone(),
                Q = new ECPoint
                {
                    X = (byte[])source.Q.X!.Clone(),
                    Y = (byte[])source.Q.Y!.Clone()
                }
            };
        }

        private static ECParameters CopyPublic(ECParameters source)
        {
            return new ECParameters
            {
                Curve = source.Curve,
                Q = new ECPoint
                {
                    X = (byte[])source.Q.X!.Clone(),
                    Y = (byte[])source.Q.Y!.Clone()
                }
            };
        }

        public static string ComputeKid(ECParameters parameters)
        {
            ValidateP256(parameters);
            string x = LobbyBase64Url.Encode(parameters.Q.X);
            string y = LobbyBase64Url.Encode(parameters.Q.Y);
            string canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return LobbyBase64Url.Encode(hash);
        }

        private static bool IsSafeKid(string kid)
        {
            if (kid.Length < 8 || kid.Length > 128)
            {
                return false;
            }

            for (int i = 0; i < kid.Length; i++)
            {
                char c = kid[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class KeyEntry
        {
            public KeyEntry(
                string kid,
                ECParameters privateParameters,
                ECParameters publicParameters,
                bool hasPrivate,
                bool isSigning,
                DateTimeOffset createdAt,
                DateTimeOffset retireAt)
            {
                Kid = kid;
                PrivateParameters = privateParameters;
                PublicParameters = publicParameters;
                HasPrivate = hasPrivate;
                IsSigning = isSigning;
                CreatedAt = createdAt;
                RetireAt = retireAt;
            }

            public string Kid { get; }

            public ECParameters PrivateParameters { get; }

            public ECParameters PublicParameters { get; }

            public bool HasPrivate { get; }

            public bool IsSigning { get; set; }

            public DateTimeOffset CreatedAt { get; }

            public DateTimeOffset RetireAt { get; set; }
        }
    }

    public sealed class JwtTableTicketSigner : ITableTicketSigner
    {
        private const string ExpectedAlgorithm = "ES256";
        private const string ExpectedType = "JWT";
        private const int MaxTicketChars = 4096;

        private static readonly HashSet<string> HeaderClaims = new HashSet<string>(StringComparer.Ordinal)
        {
            "alg", "typ", "kid"
        };

        private static readonly HashSet<string> PayloadClaims = new HashSet<string>(StringComparer.Ordinal)
        {
            "iss", "sub", "aud", "room_id", "assignment_id", "role", "seat_id", "jti",
            "iat", "exp", "nbf", "protocol_major", "ruleset_version", "projection_version"
        };

        private readonly TicketKeyRing _keys;
        private readonly LobbyOptions _options;

        public JwtTableTicketSigner(TicketKeyRing keys, LobbyOptions options)
        {
            _keys = keys;
            _options = options;
        }

        public IssuedTableTicket Issue(TableTicketClaims claims)
        {
            if (claims == null)
            {
                throw new ArgumentNullException(nameof(claims));
            }

            ValidateClaimShape(claims);
            ECParameters? signing = _keys.FindSigningParameters(claims.IssuedAt, out string kid);
            if (signing == null)
            {
                throw new InvalidOperationException("No active table-ticket signing key is configured.");
            }

            byte[] header = WriteHeader(kid);
            byte[] payload = WritePayload(claims);
            string headerPart = LobbyBase64Url.Encode(header);
            string payloadPart = LobbyBase64Url.Encode(payload);
            byte[] signingInput = Encoding.ASCII.GetBytes(headerPart + "." + payloadPart);

            using (ECDsa ecdsa = ECDsa.Create(signing.Value))
            {
                byte[] signature = ecdsa.SignData(
                    signingInput,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (signature.Length != 64)
                {
                    throw new InvalidOperationException("ES256 signature must be 64 bytes.");
                }

                string compact = headerPart + "." + payloadPart + "." + LobbyBase64Url.Encode(signature);
                if (compact.Length > MaxTicketChars)
                {
                    throw new InvalidOperationException("Table ticket exceeds the compact-size cap.");
                }

                return new IssuedTableTicket(
                    compact,
                    claims.Jti,
                    kid,
                    claims.AssignmentId,
                    claims.RoomId,
                    claims.Role,
                    claims.SeatId,
                    claims.IssuedAt,
                    claims.ExpiresAt,
                    claims.ProtocolMajor,
                    claims.RulesetVersion,
                    claims.ProjectionVersion);
            }
        }

        public TableTicketClaims Validate(string compact, TableTicketExpectation expectation, DateTimeOffset now)
        {
            if (expectation == null)
            {
                throw new ArgumentNullException(nameof(expectation));
            }

            if (string.IsNullOrWhiteSpace(compact) || compact.Length > MaxTicketChars)
            {
                throw Invalid("TICKET_MALFORMED", "Table ticket is empty or exceeds the compact-size cap.");
            }

            string[] parts = compact.Split('.');
            if (parts.Length != 3)
            {
                throw Invalid("TICKET_MALFORMED", "A compact JWS must have exactly three segments.");
            }

            byte[] headerBytes = DecodeBase64Url(parts[0], "header");
            byte[] payloadBytes = DecodeBase64Url(parts[1], "payload");
            byte[] signature = DecodeBase64Url(parts[2], "signature");

            Dictionary<string, JsonElement> header = ParseStrictObject(headerBytes, "header", HeaderClaims);
            string alg = RequireString(header, "alg");
            string typ = RequireString(header, "typ");
            string kid = RequireString(header, "kid");
            if (!string.Equals(alg, ExpectedAlgorithm, StringComparison.Ordinal))
            {
                throw Invalid("TICKET_ALGORITHM_REJECTED", "Only ES256 table tickets are accepted.");
            }

            if (!string.Equals(typ, ExpectedType, StringComparison.Ordinal))
            {
                throw Invalid("TICKET_TYPE_REJECTED", "The JWT typ must be JWT.");
            }

            if (kid.Length == 0 || kid.Length > 128)
            {
                throw Invalid("TICKET_KEY_UNKNOWN", "The ticket key id is invalid.");
            }

            TicketPublicKey key = _keys.FindValidationKey(kid, now);
            using (ECDsa ecdsa = ECDsa.Create(key.Parameters))
            {
                bool valid;
                try
                {
                    valid = ecdsa.VerifyData(
                        Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
                catch (CryptographicException)
                {
                    valid = false;
                }

                if (!valid)
                {
                    throw Invalid("TICKET_SIGNATURE_INVALID", "The table ticket signature is invalid.");
                }
            }

            Dictionary<string, JsonElement> payload = ParseStrictObject(payloadBytes, "payload", PayloadClaims);
            string issuer = RequireString(payload, "iss");
            string accountId = RequireString(payload, "sub");
            string audience = RequireString(payload, "aud");
            string roomId = RequireString(payload, "room_id");
            string assignmentId = RequireString(payload, "assignment_id");
            string roleText = RequireString(payload, "role");
            string jti = RequireString(payload, "jti");
            long issuedAt = RequireInt64(payload, "iat");
            long expiresAt = RequireInt64(payload, "exp");
            long? notBefore = OptionalInt64(payload, "nbf");
            uint protocolMajor = RequireUInt32(payload, "protocol_major");
            uint rulesetVersion = RequireUInt32(payload, "ruleset_version");
            uint projectionVersion = RequireUInt32(payload, "projection_version");
            LobbyRole role = ParseRole(roleText);
            int? seatId = role == LobbyRole.Player ? RequireSeat(payload) : null;
            if (role == LobbyRole.Spectator && payload.ContainsKey("seat_id"))
            {
                throw Invalid("TICKET_SEAT_INVALID", "A spectator ticket must not carry a seat_id.");
            }

            if (!string.Equals(issuer, _options.TicketIssuer, StringComparison.Ordinal))
            {
                throw Invalid("TICKET_ISSUER_MISMATCH", "The table ticket issuer is not trusted by this lobby.");
            }

            if (!string.Equals(audience, _options.TicketAudience, StringComparison.Ordinal))
            {
                throw Invalid("TICKET_AUDIENCE_MISMATCH", "The table ticket audience is not this table service.");
            }

            ValidateId(accountId, "sub");
            ValidateId(roomId, "room_id");
            ValidateId(assignmentId, "assignment_id");
            ValidateId(jti, "jti");

            if (!string.Equals(expectation.RoomId, roomId, StringComparison.Ordinal) ||
                !string.Equals(expectation.AssignmentId, assignmentId, StringComparison.Ordinal) ||
                expectation.Role != role ||
                expectation.SeatId != seatId ||
                (expectation.AccountId != null &&
                 !string.Equals(expectation.AccountId, accountId, StringComparison.Ordinal)))
            {
                throw Invalid("TICKET_CLAIM_MISMATCH", "The table ticket does not match the requested room, assignment, role or seat.");
            }

            if (!string.Equals(Convert.ToString(protocolMajor, CultureInfo.InvariantCulture), "1", StringComparison.Ordinal) ||
                protocolMajor != Mahjong.Contracts.ProtocolConstants.Major ||
                rulesetVersion != Mahjong.Contracts.ProtocolConstants.RulesetVersion ||
                projectionVersion != Mahjong.Contracts.ProtocolConstants.ProjectionVersion)
            {
                throw Invalid("TICKET_VERSION_MISMATCH", "The table ticket protocol/ruleset/projection version is not supported.");
            }

            long nowSeconds = now.ToUnixTimeSeconds();
            long skew = _options.ClockSkewSeconds;
            if (expiresAt <= issuedAt)
            {
                throw Invalid("TICKET_TTL_INVALID", "The table ticket expiry must be after its issue time.");
            }

            if (expiresAt - issuedAt > _options.TicketMaxTtlSeconds)
            {
                throw Invalid("TICKET_TTL_INVALID", "The table ticket TTL exceeds the configured maximum.");
            }

            if (issuedAt > nowSeconds + skew)
            {
                throw Invalid("TICKET_NOT_YET_VALID", "The table ticket was issued in the future.");
            }

            if (notBefore.HasValue && notBefore.Value > nowSeconds + skew)
            {
                throw Invalid("TICKET_NOT_YET_VALID", "The table ticket is not valid yet.");
            }

            if (expiresAt <= nowSeconds - skew)
            {
                throw Invalid("TICKET_EXPIRED", "The table ticket has expired.");
            }

            return new TableTicketClaims(
                issuer,
                accountId,
                audience,
                roomId,
                assignmentId,
                role,
                seatId,
                jti,
                DateTimeOffset.FromUnixTimeSeconds(issuedAt),
                DateTimeOffset.FromUnixTimeSeconds(expiresAt),
                protocolMajor,
                rulesetVersion,
                projectionVersion);
        }

        public JwksDocument GetJwks(DateTimeOffset now)
        {
            IReadOnlyList<TicketPublicKey> keys = _keys.ListValidationKeys(now);
            var views = keys.Select(key => new JwksKey(
                "EC",
                "sig",
                ExpectedAlgorithm,
                key.Kid,
                "P-256",
                LobbyBase64Url.Encode(key.Parameters.Q.X),
                LobbyBase64Url.Encode(key.Parameters.Q.Y))).ToList();
            return new JwksDocument(views);
        }

        public string CurrentKid(DateTimeOffset now)
        {
            return _keys.ActiveKid(now);
        }

        public void Rotate(DateTimeOffset now)
        {
            TimeSpan overlap = TimeSpan.FromSeconds(Math.Max(_options.TicketTtlSeconds, _options.TicketMaxTtlSeconds) +
                                                   _options.ClockSkewSeconds + 5);
            _keys.Rotate(now, overlap);
        }

        private void ValidateClaimShape(TableTicketClaims claims)
        {
            if (!string.Equals(claims.Issuer, _options.TicketIssuer, StringComparison.Ordinal) ||
                !string.Equals(claims.Audience, _options.TicketAudience, StringComparison.Ordinal))
            {
                throw new ArgumentException("Ticket issuer/audience must match lobby configuration.", nameof(claims));
            }

            ValidateId(claims.AccountId, "sub");
            ValidateId(claims.RoomId, "room_id");
            ValidateId(claims.AssignmentId, "assignment_id");
            ValidateId(claims.Jti, "jti");
            if (claims.Role == LobbyRole.Player)
            {
                if (!claims.SeatId.HasValue ||
                    claims.SeatId.Value < Mahjong.Contracts.ProtocolConstants.MinSeat ||
                    claims.SeatId.Value > Mahjong.Contracts.ProtocolConstants.MaxSeat)
                {
                    throw new ArgumentException("A player ticket requires seat 0..3.", nameof(claims));
                }
            }
            else if (claims.Role == LobbyRole.Spectator)
            {
                if (claims.SeatId.HasValue)
                {
                    throw new ArgumentException("A spectator ticket must not carry a seat.", nameof(claims));
                }
            }
            else
            {
                throw new ArgumentException("A table ticket role must be PLAYER or SPECTATOR.", nameof(claims));
            }

            if (claims.ExpiresAt <= claims.IssuedAt ||
                (claims.ExpiresAt - claims.IssuedAt).TotalSeconds > _options.TicketMaxTtlSeconds)
            {
                throw new ArgumentException("Ticket TTL is outside the configured bounds.", nameof(claims));
            }
        }

        private static byte[] WriteHeader(string kid)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("alg", ExpectedAlgorithm);
                    writer.WriteString("typ", ExpectedType);
                    writer.WriteString("kid", kid);
                    writer.WriteEndObject();
                }

                return stream.ToArray();
            }
        }

        private static byte[] WritePayload(TableTicketClaims claims)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("iss", claims.Issuer);
                    writer.WriteString("sub", claims.AccountId);
                    writer.WriteString("aud", claims.Audience);
                    writer.WriteString("room_id", claims.RoomId);
                    writer.WriteString("assignment_id", claims.AssignmentId);
                    writer.WriteString("role", claims.Role == LobbyRole.Player ? "PLAYER" : "SPECTATOR");
                    if (claims.SeatId.HasValue)
                    {
                        writer.WriteNumber("seat_id", claims.SeatId.Value);
                    }

                    writer.WriteString("jti", claims.Jti);
                    writer.WriteNumber("iat", claims.IssuedAt.ToUnixTimeSeconds());
                    writer.WriteNumber("exp", claims.ExpiresAt.ToUnixTimeSeconds());
                    writer.WriteNumber("protocol_major", claims.ProtocolMajor);
                    writer.WriteNumber("ruleset_version", claims.RulesetVersion);
                    writer.WriteNumber("projection_version", claims.ProjectionVersion);
                    writer.WriteEndObject();
                }

                return stream.ToArray();
            }
        }

        private static Dictionary<string, JsonElement> ParseStrictObject(
            byte[] bytes,
            string name,
            HashSet<string> allowed)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow
                       }))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw Invalid("TICKET_MALFORMED", "The ticket " + name + " must be a JSON object.");
                    }

                    var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (!allowed.Contains(property.Name))
                        {
                            throw Invalid("TICKET_UNKNOWN_CLAIM", "The ticket " + name + " contains an unsupported claim.");
                        }

                        if (!result.TryAdd(property.Name, property.Value.Clone()))
                        {
                            throw Invalid("TICKET_DUPLICATE_CLAIM", "The ticket " + name + " contains a duplicate claim.");
                        }
                    }

                    return result;
                }
            }
            catch (JsonException)
            {
                throw Invalid("TICKET_MALFORMED", "The ticket " + name + " is not valid JSON.");
            }
        }

        private static string RequireString(Dictionary<string, JsonElement> claims, string name)
        {
            if (!claims.TryGetValue(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                throw Invalid("TICKET_MALFORMED", "The ticket claim " + name + " must be a string.");
            }

            string? text = value.GetString();
            if (text == null || text.Length == 0)
            {
                throw Invalid("TICKET_MALFORMED", "The ticket claim " + name + " must not be empty.");
            }

            return text;
        }

        private static long RequireInt64(Dictionary<string, JsonElement> claims, string name)
        {
            if (!claims.TryGetValue(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt64(out long number))
            {
                throw Invalid("TICKET_MALFORMED", "The ticket claim " + name + " must be an integer.");
            }

            return number;
        }

        private static long? OptionalInt64(Dictionary<string, JsonElement> claims, string name)
        {
            if (!claims.ContainsKey(name))
            {
                return null;
            }

            return RequireInt64(claims, name);
        }

        private static uint RequireUInt32(Dictionary<string, JsonElement> claims, string name)
        {
            long value = RequireInt64(claims, name);
            if (value < 0 || value > uint.MaxValue)
            {
                throw Invalid("TICKET_MALFORMED", "The ticket claim " + name + " is outside uint32.");
            }

            return (uint)value;
        }

        private static LobbyRole ParseRole(string role)
        {
            if (string.Equals(role, "PLAYER", StringComparison.Ordinal))
            {
                return LobbyRole.Player;
            }

            if (string.Equals(role, "SPECTATOR", StringComparison.Ordinal))
            {
                return LobbyRole.Spectator;
            }

            throw Invalid("TICKET_ROLE_INVALID", "The ticket role must be PLAYER or SPECTATOR.");
        }

        private static int RequireSeat(Dictionary<string, JsonElement> claims)
        {
            long seat = RequireInt64(claims, "seat_id");
            if (seat < Mahjong.Contracts.ProtocolConstants.MinSeat ||
                seat > Mahjong.Contracts.ProtocolConstants.MaxSeat)
            {
                throw Invalid("TICKET_SEAT_INVALID", "The ticket seat must be 0..3.");
            }

            return (int)seat;
        }

        private static void ValidateId(string value, string name)
        {
            if (value.Length == 0 || value.Length > 128)
            {
                throw Invalid("TICKET_ID_INVALID", "The ticket claim " + name + " has an invalid length.");
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == ':';
                if (!valid)
                {
                    throw Invalid("TICKET_ID_INVALID", "The ticket claim " + name + " has unsupported characters.");
                }
            }
        }

        private static byte[] DecodeBase64Url(string text, string name)
        {
            if (text.Length == 0 || text.Length % 4 == 1)
            {
                throw Invalid("TICKET_MALFORMED", "The ticket " + name + " is not valid base64url.");
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!valid)
                {
                    throw Invalid("TICKET_MALFORMED", "The ticket " + name + " is not valid base64url.");
                }
            }

            try
            {
                byte[] bytes = LobbyBase64Url.Decode(text);
                if (!string.Equals(LobbyBase64Url.Encode(bytes), text, StringComparison.Ordinal))
                {
                    throw Invalid("TICKET_MALFORMED", "The ticket " + name + " is not canonical base64url.");
                }

                return bytes;
            }
            catch (FormatException)
            {
                throw Invalid("TICKET_MALFORMED", "The ticket " + name + " is not valid base64url.");
            }
        }

        private static TicketValidationException Invalid(string code, string message)
        {
            return new TicketValidationException(code, message);
        }
    }

    public sealed record JwksDocument(IReadOnlyList<JwksKey> Keys);

    public sealed record JwksKey(
        [property: System.Text.Json.Serialization.JsonPropertyName("kty")] string KeyType,
        [property: System.Text.Json.Serialization.JsonPropertyName("use")] string Use,
        [property: System.Text.Json.Serialization.JsonPropertyName("alg")] string Algorithm,
        [property: System.Text.Json.Serialization.JsonPropertyName("kid")] string KeyId,
        [property: System.Text.Json.Serialization.JsonPropertyName("crv")] string Curve,
        [property: System.Text.Json.Serialization.JsonPropertyName("x")] string X,
        [property: System.Text.Json.Serialization.JsonPropertyName("y")] string Y);
}

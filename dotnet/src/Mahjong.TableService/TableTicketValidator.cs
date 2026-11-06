using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Contracts;

namespace Mahjong.TableService
{
    /// <summary>Canonical unpadded base64url (RFC 4648 §5, no padding), same alphabet the frozen ticket format uses.</summary>
    public static class TableBase64Url
    {
        public static string Encode(ReadOnlySpan<byte> bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Decode(string text)
        {
            if (text == null)
            {
                throw new FormatException("Invalid base64url input.");
            }

            if (text.Length == 0 || text.Length % 4 == 1)
            {
                throw new FormatException("Invalid base64url length.");
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!valid)
                {
                    throw new FormatException("Invalid base64url character.");
                }
            }

            string padded = text.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            return Convert.FromBase64String(padded);
        }
    }

    /// <summary>Stable ticket rejection. Codes mirror the frozen ticket contract one-for-one.</summary>
    public sealed class TableTicketValidationException : Exception
    {
        public TableTicketValidationException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>One read-only validation key (public half only; this service never holds a signing key).</summary>
    public sealed class TableTicketKey
    {
        public TableTicketKey(string kid, ECParameters publicParameters, DateTimeOffset retireAt)
        {
            if (string.IsNullOrWhiteSpace(kid))
            {
                throw new ArgumentException("A ticket key requires a kid.", nameof(kid));
            }

            Kid = kid;
            PublicParameters = publicParameters;
            RetireAt = retireAt;
        }

        public string Kid { get; }

        public ECParameters PublicParameters { get; }

        public DateTimeOffset RetireAt { get; }
    }

    /// <summary>
    /// TableService's own read-only validation-key source. Production wires JWKS/PEM here;
    /// an empty provider keeps admission fail-closed (every ticket is rejected) and a
    /// production host refuses to start with no keys configured.
    /// </summary>
    public interface ITableTicketKeyProvider
    {
        IReadOnlyList<TableTicketKey> ListValidationKeys(DateTimeOffset now);

        TableTicketKey? FindValidationKey(string kid, DateTimeOffset now);
    }

    /// <summary>Static PEM-backed key provider for operator-supplied public keys.</summary>
    public sealed class StaticTableTicketKeyProvider : ITableTicketKeyProvider
    {
        private readonly List<TableTicketKey> _keys;

        public StaticTableTicketKeyProvider(IEnumerable<(string Pem, string? Kid)> pemKeys, DateTimeOffset now)
        {
            if (pemKeys == null)
            {
                throw new ArgumentNullException(nameof(pemKeys));
            }

            _keys = new List<TableTicketKey>();
            foreach ((string pem, string? kid) in pemKeys)
            {
                using (var ecdsa = ECDsa.Create())
                {
                    ecdsa.ImportFromPem(pem);
                    ECParameters parameters = ecdsa.ExportParameters(false);
                    string keyId = kid ??
                        // kid = base64url(SHA-256 over the canonical JWK JSON), same derivation
                        // the frozen ticket signer uses when it mints a key id.
                        TableBase64Url.Encode(ComputeJwkThumbprint(parameters));
                    _keys.Add(new TableTicketKey(keyId, parameters, DateTimeOffset.MaxValue));
                }
            }
        }

        public IReadOnlyList<TableTicketKey> ListValidationKeys(DateTimeOffset now)
        {
            var live = new List<TableTicketKey>();
            foreach (TableTicketKey key in _keys)
            {
                if (key.RetireAt > now)
                {
                    live.Add(key);
                }
            }

            return live;
        }

        public TableTicketKey? FindValidationKey(string kid, DateTimeOffset now)
        {
            foreach (TableTicketKey key in _keys)
            {
                if (key.RetireAt > now && string.Equals(key.Kid, kid, StringComparison.Ordinal))
                {
                    return key;
                }
            }

            return null;
        }

        internal static byte[] ComputeJwkThumbprint(ECParameters parameters)
        {
            string json = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" +
                          TableBase64Url.Encode(parameters.Q.X) +
                          "\",\"y\":\"" +
                          TableBase64Url.Encode(parameters.Q.Y) +
                          "\"}";
            return SHA256.HashData(Encoding.UTF8.GetBytes(json));
        }
    }

    /// <summary>
    /// Hosting-side ticket expectation. The room comes from the controlled route (the client
    /// can never self-select it); assignment ids are not predictable by the table host, so an
    /// absent expectation accepts any well-formed assignment while every other claim stays
    /// strictly frozen.
    /// </summary>
    public sealed class TableTicketExpectation
    {
        public TableTicketExpectation(string roomId, TableAdmissionRole? role, int? seatId, string? accountId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("A ticket expectation requires a room id.", nameof(roomId));
            }

            RoomId = roomId;
            Role = role;
            SeatId = seatId;
            AccountId = accountId;
        }

        public string RoomId { get; }

        public TableAdmissionRole? Role { get; }

        public int? SeatId { get; }

        public string? AccountId { get; }
    }

    /// <summary>Validated ticket payload (read-only view over the frozen claim set).</summary>
    public sealed class ValidatedTableTicket
    {
        public ValidatedTableTicket(
            string issuer,
            string accountId,
            string audience,
            string roomId,
            string assignmentId,
            TableAdmissionRole role,
            int? seatId,
            string jti,
            DateTimeOffset issuedAt,
            DateTimeOffset expiresAt,
            uint protocolMajor,
            uint rulesetVersion,
            uint projectionVersion,
            string kid)
        {
            Issuer = issuer;
            AccountId = accountId;
            Audience = audience;
            RoomId = roomId;
            AssignmentId = assignmentId;
            Role = role;
            SeatId = seatId;
            Jti = jti;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
            ProtocolMajor = protocolMajor;
            RulesetVersion = rulesetVersion;
            ProjectionVersion = projectionVersion;
            Kid = kid;
        }

        public string Issuer { get; }

        public string AccountId { get; }

        public string Audience { get; }

        public string RoomId { get; }

        public string AssignmentId { get; }

        public TableAdmissionRole Role { get; }

        public int? SeatId { get; }

        public string Jti { get; }

        public DateTimeOffset IssuedAt { get; }

        public DateTimeOffset ExpiresAt { get; }

        public uint ProtocolMajor { get; }

        public uint RulesetVersion { get; }

        public uint ProjectionVersion { get; }

        public string Kid { get; }
    }

    /// <summary>
    /// TableService's own read-only ES256/JWKS ticket validator. It implements the exact
    /// frozen ticket contract — compact JWS, strict canonical base64url, strict claim
    /// allowlist (no unknown, no duplicates), alg/typ/kid checks, IEEE P1363 ES256 signature,
    /// issuer/audience exact match, id charset, PLAYER-only seat 0..3, spectator without
    /// seat_id, frozen protocol/ruleset/projection versions, TTL cap and clock-skewed
    /// time checks. It never signs, never calls the Lobby runtime and never holds a private
    /// key; admission is refused whenever no validation key matches.
    /// </summary>
    public sealed class TableTicketValidator
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

        private readonly ITableTicketKeyProvider _keys;
        private readonly TableServiceOptions _options;

        public TableTicketValidator(ITableTicketKeyProvider keys, TableServiceOptions options)
        {
            _keys = keys ?? throw new ArgumentNullException(nameof(keys));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public ValidatedTableTicket Validate(string compact, TableTicketExpectation expectation, DateTimeOffset now)
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

            // Fail-closed key lookup: an unknown or retired kid never falls back to another key.
            TableTicketKey? key = _keys.FindValidationKey(kid, now);
            if (key == null)
            {
                throw Invalid("TICKET_KEY_UNKNOWN", "No validation key matches the ticket kid.");
            }

            using (ECDsa ecdsa = ECDsa.Create(key.PublicParameters))
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

            TableAdmissionRole role = ParseRole(roleText);
            int? seatId = role == TableAdmissionRole.Player ? RequireSeat(payload) : null;
            if (role == TableAdmissionRole.Spectator && payload.ContainsKey("seat_id"))
            {
                throw Invalid("TICKET_SEAT_INVALID", "A spectator ticket must not carry a seat_id.");
            }

            if (!string.Equals(issuer, _options.TicketIssuer, StringComparison.Ordinal))
            {
                throw Invalid("TICKET_ISSUER_MISMATCH", "The table ticket issuer is not trusted by this table service.");
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
                (expectation.Role.HasValue && expectation.Role.Value != role) ||
                (expectation.SeatId.HasValue && expectation.SeatId.Value != seatId) ||
                (expectation.AccountId != null &&
                 !string.Equals(expectation.AccountId, accountId, StringComparison.Ordinal)))
            {
                throw Invalid("TICKET_CLAIM_MISMATCH", "The table ticket does not match the requested room, role or seat.");
            }

            if (protocolMajor != ProtocolConstants.Major ||
                rulesetVersion != ProtocolConstants.RulesetVersion ||
                projectionVersion != ProtocolConstants.ProjectionVersion)
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

            return new ValidatedTableTicket(
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
                projectionVersion,
                kid);
        }

        private static TableTicketValidationException Invalid(string code, string message)
        {
            return new TableTicketValidationException(code, message);
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

        private static TableAdmissionRole ParseRole(string role)
        {
            if (string.Equals(role, "PLAYER", StringComparison.Ordinal))
            {
                return TableAdmissionRole.Player;
            }

            if (string.Equals(role, "SPECTATOR", StringComparison.Ordinal))
            {
                return TableAdmissionRole.Spectator;
            }

            throw Invalid("TICKET_ROLE_INVALID", "The ticket role must be PLAYER or SPECTATOR.");
        }

        private static int RequireSeat(Dictionary<string, JsonElement> claims)
        {
            long seat = RequireInt64(claims, "seat_id");
            if (seat < ProtocolConstants.MinSeat || seat > ProtocolConstants.MaxSeat)
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
                byte[] bytes = TableBase64Url.Decode(text);
                if (!string.Equals(TableBase64Url.Encode(bytes), text, StringComparison.Ordinal))
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
    }
}

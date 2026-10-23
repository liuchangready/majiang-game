using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.LobbyService
{
    /// <summary>RFC 4648 base64url without padding, implemented on BCL Convert.</summary>
    public static class LobbyBase64Url
    {
        public static string Encode(ReadOnlySpan<byte> bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Decode(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
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
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            return Convert.FromBase64String(padded);
        }
    }

    public interface ILobbyClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemLobbyClock : ILobbyClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public static class LobbyJson
    {
        public static JsonSerializerOptions Options { get; } = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
            return options;
        }

        public static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        public static T Deserialize<T>(string json)
        {
            T? value = JsonSerializer.Deserialize<T>(json, Options);
            if (value == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "Request body is empty or invalid.");
            }

            return value;
        }
    }

    public static class LobbyText
    {
        private const string IdAlphabet =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.:-";

        public static string RequireId(string? value, string name, int maxChars = 128)
        {
            string text = RequireText(value, name, maxChars);
            if (text.Length == 0 || !IsId(text))
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", name + " is not a valid identifier.");
            }

            return text;
        }

        public static string RequireTitle(string? value)
        {
            string text = RequireText(value, "title", 64);
            if (text.Length == 0)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "Room title must not be empty.");
            }

            return text;
        }

        public static string RequireDisplayName(string? value)
        {
            string text = RequireText(value, "displayName", 32);
            if (text.Length == 0)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "Display name must not be empty.");
            }

            return text;
        }

        public static string RequireRegion(string? value)
        {
            string text = RequireText(value, "region", 16);
            if (text.Length == 0)
            {
                return "default";
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!valid)
                {
                    throw new LobbyException(400, "MALFORMED_REQUEST", "Region contains unsupported characters.");
                }
            }

            return text.ToLowerInvariant();
        }

        public static string RequireRequestId(string? value)
        {
            string text = RequireText(value, "Idempotency-Key", 128);
            if (text.Length == 0)
            {
                throw new LobbyException(400, "IDEMPOTENCY_KEY_REQUIRED", "An Idempotency-Key header is required for this operation.");
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsControl(text[i]) || char.IsWhiteSpace(text[i]))
                {
                    throw new LobbyException(400, "MALFORMED_REQUEST", "Idempotency-Key contains whitespace or control characters.");
                }
            }

            return text;
        }

        public static string RequireDeviceId(string? value)
        {
            return RequireText(value, "deviceId", 256);
        }

        public static string RequireText(string? value, string name, int maxChars)
        {
            if (value == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", name + " is required.");
            }

            string text = value.Trim();
            if (text.Length > maxChars)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", name + " exceeds " + maxChars + " characters.");
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsControl(text[i]))
                {
                    throw new LobbyException(400, "MALFORMED_REQUEST", name + " contains control characters.");
                }
            }

            return text;
        }

        public static string RequireTicket(string? value)
        {
            string text = RequireText(value, "ticket", 4096);
            if (text.Length == 0)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "Table ticket must not be empty.");
            }

            return text;
        }

        private static bool IsId(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (IdAlphabet.IndexOf(value[i], StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static class LobbyIds
    {
        public static string NewPrefixed(string prefix)
        {
            return prefix + NewUuidV7Hex();
        }

        public static string NewToken()
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return LobbyBase64Url.Encode(bytes);
        }

        public static string HashToken(string token)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string HashDevice(string deviceId)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string NewUuidV7Hex()
        {
            Span<byte> bytes = stackalloc byte[16];
            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bytes[0] = (byte)(unixMs >> 40);
            bytes[1] = (byte)(unixMs >> 32);
            bytes[2] = (byte)(unixMs >> 24);
            bytes[3] = (byte)(unixMs >> 16);
            bytes[4] = (byte)(unixMs >> 8);
            bytes[5] = (byte)unixMs;
            RandomNumberGenerator.Fill(bytes.Slice(6));
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public static class LobbyDigest
    {
        /// <summary>
        /// Length-prefixed canonical request digest. It is intentionally not
        /// JSON: property order and whitespace must never change idempotency.
        /// </summary>
        public static string Compute(string operation, params string?[] fields)
        {
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                Append(hash, operation);
                foreach (string? field in fields)
                {
                    Append(hash, field ?? string.Empty);
                }

                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
        }

        private static void Append(IncrementalHash hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    public readonly struct LobbyCursor
    {
        private LobbyCursor(DateTimeOffset createdAt, string roomId)
        {
            CreatedAt = createdAt;
            RoomId = roomId;
        }

        public DateTimeOffset CreatedAt { get; }

        public string RoomId { get; }

        public static string Encode(DateTimeOffset createdAt, string roomId)
        {
            string raw = createdAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ":" + roomId;
            return LobbyBase64Url.Encode(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryDecode(string? cursor, out LobbyCursor value)
        {
            value = default;
            if (string.IsNullOrEmpty(cursor))
            {
                return false;
            }

            try
            {
                byte[] bytes = LobbyBase64Url.Decode(cursor);
                string raw = new UTF8Encoding(false, true).GetString(bytes);
                int separator = raw.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0 || separator == raw.Length - 1)
                {
                    return false;
                }

                if (!long.TryParse(raw.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long unixMs))
                {
                    return false;
                }

                string roomId = raw.Substring(separator + 1);
                if (roomId.Length == 0 || roomId.Length > 128)
                {
                    return false;
                }

                value = new LobbyCursor(DateTimeOffset.FromUnixTimeMilliseconds(unixMs), roomId);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}

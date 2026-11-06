using Mahjong.P5_4b.B1.HttpsAdmission.Worker;
using Xunit;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Tests
{
    public sealed class RedactionTests
    {
        [Theory]
        [InlineData("{\"\\u0070assword\":\"s3cr3t\"}")]
        [InlineData("{\"session\\u0054oken\":\"abc123\"}")]
        [InlineData("token abc123")]
        [InlineData("abc123")]
        [InlineData("ABC123")]
        [InlineData("{\"nested\":{\"password\":\"short\"}}")]
        [InlineData("Authorization: Bearer abcDEF1234567890")]
        [InlineData("-----BEGIN PRIVATE KEY-----MIIE-----END PRIVATE KEY-----")]
        [InlineData("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJndWVzdCJ9.c2lnbmF0dXJlLXZhbHVl")]
        public void SensitiveInputsAreNotEchoed(string input)
        {
            string redacted = Program.Redact(input);

            Assert.DoesNotContain("s3cr3t", redacted, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abc123", redacted, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abcDEF1234567890", redacted, System.StringComparison.Ordinal);
            Assert.DoesNotContain("MIIE", redacted, System.StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", redacted, System.StringComparison.Ordinal);
            Assert.DoesNotContain("short", redacted, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("[redacted]", redacted);
        }

        [Theory]
        [InlineData("status=abc123")]
        [InlineData("reason=abc123")]
        [InlineData("stage abc123")]
        [InlineData("code=abc123")]
        [InlineData("room=abc123")]
        [InlineData("state=abc123")]
        [InlineData("seat=abc123")]
        [InlineData("role=abc123")]
        [InlineData("version=abc123")]
        [InlineData("count=abc123")]
        [InlineData("code=eyJhbGciOiJFUzI1NiJ9")]
        [InlineData("room=eyJhbGciOiJFUzI1NiJ9")]
        public void UnknownFieldValuesAreRedacted(string input)
        {
            Assert.Equal("[redacted]", Program.Redact(input));
        }

        [Theory]
        [InlineData("HTTP 409")]
        [InlineData("REQUEST_ID_CONFLICT")]
        [InlineData("CLIENT_RECEIPT_SCOPE_MISSING")]
        [InlineData("stage suite")]
        [InlineData("status=APPLIED")]
        [InlineData("code=REQUEST_ID_CONFLICT")]
        [InlineData("reason=SCOPE_MISMATCH")]
        [InlineData("stage=suite")]
        [InlineData("state=Faulted")]
        [InlineData("seat=0")]
        [InlineData("role=PLAYER")]
        [InlineData("version=1")]
        [InlineData("count=2")]
        [InlineData("room=room_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        public void SafeStableDiagnosticsRemainReadable(string input)
        {
            Assert.Equal(input, Program.Redact(input));
        }
    }
}

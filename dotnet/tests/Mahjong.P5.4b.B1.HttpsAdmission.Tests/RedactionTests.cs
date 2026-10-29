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
        [InlineData("HTTP 409")]
        [InlineData("REQUEST_ID_CONFLICT")]
        [InlineData("CLIENT_RECEIPT_SCOPE_MISSING")]
        [InlineData("stage suite")]
        [InlineData("status=APPLIED")]
        public void SafeStableDiagnosticsRemainReadable(string input)
        {
            Assert.Equal(input, Program.Redact(input));
        }
    }
}

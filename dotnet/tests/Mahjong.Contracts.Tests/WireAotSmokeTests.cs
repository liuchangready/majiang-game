using System;
using Mahjong.Contracts.Wire.AotSmoke;
using Xunit;

namespace Mahjong.Protocol.Tests
{
    public sealed class WireAotSmokeTests
    {
        [Fact]
        public void GeneratedCodecLoadsAndRoundTripsWithoutReflectionSerialization()
        {
            string result = WireAotProbe.Run();

            Assert.StartsWith(WireAotProbe.PassPrefix, result, StringComparison.Ordinal);
            Assert.Contains("codec=netstandard2.1", result, StringComparison.Ordinal);
            Assert.Contains("messages=18", result, StringComparison.Ordinal);
            Assert.Contains("legacyV4=rejected", result, StringComparison.Ordinal);
            Assert.Contains("assembly=Mahjong.Contracts.Wire", result, StringComparison.Ordinal);
        }
    }
}

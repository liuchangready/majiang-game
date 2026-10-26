using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.Protocol.Tests
{
    public sealed class AccessCapabilitiesTests
    {
        [Fact]
        public void DefaultDeclaresCurrentV5ProtocolAndFourPlayerProductScope()
        {
            var caps = new AccessCapabilities();

            Assert.Equal(ProtocolVersion.Current, caps.ProtocolVersion);
            Assert.Equal(1u, caps.ProtocolVersion.Major);
            Assert.Equal(0u, caps.ProtocolVersion.Minor);
            Assert.NotEqual(new ProtocolVersion(3, 0), caps.ProtocolVersion);

            Assert.Equal(4, AccessCapabilities.CurrentPlayerCount);
            Assert.Equal(new[] { 4 }, caps.SupportedPlayerCounts.ToArray());

            Assert.False(caps.RemoteTransport);
            Assert.False(caps.Persistence);
            Assert.False(caps.Resumption);
        }

        [Fact]
        public void V4LegacyAndNonCurrentProtocolVersionsAreRejected()
        {
            ProtocolVersion[] rejected =
            {
                default(ProtocolVersion),      // 0.0, major 0 bypasses ProtocolVersion's constructor
                new ProtocolVersion(2, 0),     // future major
                new ProtocolVersion(3, 0),     // V4 AccessCapabilities default
                new ProtocolVersion(8, 0),     // V4 business version used by real V4 endpoints
                new ProtocolVersion(1, 1)      // this capability contract is frozen at 1.0
            };

            foreach (ProtocolVersion version in rejected)
            {
                var exception = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new AccessCapabilities(version, new[] { 4 }));

                Assert.Equal("protocolVersion", exception.ParamName);
                Assert.Contains("frozen V5 protocol version 1.0", exception.Message);
            }
        }

        [Fact]
        public void NullSupportedPlayerCountsIsRejected()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new AccessCapabilities(ProtocolVersion.Current, null!));

            Assert.Equal("supportedPlayerCounts", exception.ParamName);
        }

        [Fact]
        public void EmptySupportedPlayerCountsIsRejected()
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new AccessCapabilities(ProtocolVersion.Current, Array.Empty<int>()));

            Assert.Equal("supportedPlayerCounts", exception.ParamName);
            Assert.Contains("cannot be empty", exception.Message);
        }

        [Fact]
        public void OutOfRangePlayerCountsAreRejected()
        {
            foreach (int count in new[] { 0, -1, AccessCapabilities.CurrentPlayerCount + 1 })
            {
                var exception = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new AccessCapabilities(ProtocolVersion.Current, new[] { count }));

                Assert.Equal("supportedPlayerCounts", exception.ParamName);
                Assert.Contains("between 1 and " + AccessCapabilities.CurrentPlayerCount, exception.Message);
            }
        }

        [Fact]
        public void SupportedCountsAreCopiedDeduplicatedAndOrderPreserving()
        {
            var source = new List<int> { 4, 2, 4, 1, 2 };
            var caps = new AccessCapabilities(ProtocolVersion.Current, source);

            Assert.Equal(new[] { 4, 2, 1 }, caps.SupportedPlayerCounts.ToArray());

            source.Clear();
            source.Add(1);

            Assert.Equal(new[] { 4, 2, 1 }, caps.SupportedPlayerCounts.ToArray());
        }

        [Fact]
        public void ExposedSupportedCountsCannotBeMutated()
        {
            var caps = new AccessCapabilities(ProtocolVersion.Current, new[] { 4, 2 });
            IList<int> view = Assert.IsAssignableFrom<IList<int>>(caps.SupportedPlayerCounts);

            Assert.Throws<NotSupportedException>(() => view.Add(1));
            Assert.Throws<NotSupportedException>(() => view[0] = 2);
            Assert.Throws<NotSupportedException>(() => view.Clear());

            Assert.Equal(new[] { 4, 2 }, caps.SupportedPlayerCounts.ToArray());
        }

        [Fact]
        public void EqualityIgnoresDeclarationOrderAndHashMatches()
        {
            var first = new AccessCapabilities(
                ProtocolVersion.Current, new[] { 2, 4 }, remoteTransport: true, persistence: false, resumption: true);
            var second = new AccessCapabilities(
                ProtocolVersion.Current, new List<int> { 4, 2, 2 }, remoteTransport: true, persistence: false, resumption: true);

            Assert.NotSame(first, second);
            Assert.True(first.Equals(second));
            Assert.True(second.Equals(first));
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
            Assert.False(first.Equals(null));
            Assert.False(first.Equals("not-access-capabilities"));
        }

        [Fact]
        public void EveryCapabilityFieldParticipatesInEqualityIndependently()
        {
            var baseline = new AccessCapabilities(ProtocolVersion.Current, new[] { 4 });

            AccessCapabilities[] variants =
            {
                new AccessCapabilities(ProtocolVersion.Current, new[] { 4 }, remoteTransport: true),
                new AccessCapabilities(ProtocolVersion.Current, new[] { 4 }, persistence: true),
                new AccessCapabilities(ProtocolVersion.Current, new[] { 4 }, resumption: true),
                new AccessCapabilities(ProtocolVersion.Current, new[] { 2, 4 })
            };

            foreach (AccessCapabilities variant in variants)
            {
                Assert.False(baseline.Equals(variant));
                Assert.False(variant.Equals(baseline));
            }
        }

        [Fact]
        public void EveryFlagCombinationIsReportedIndependently()
        {
            for (int bits = 0; bits < 8; bits++)
            {
                bool remoteTransport = (bits & 1) != 0;
                bool persistence = (bits & 2) != 0;
                bool resumption = (bits & 4) != 0;

                var caps = new AccessCapabilities(
                    ProtocolVersion.Current, new[] { 4 }, remoteTransport, persistence, resumption);

                Assert.Equal(remoteTransport, caps.RemoteTransport);
                Assert.Equal(persistence, caps.Persistence);
                Assert.Equal(resumption, caps.Resumption);
            }
        }

        [Fact]
        public void FalsePersistenceIsReportedRatherThanHiddenByRemoteTransport()
        {
            var remoteWithoutPersistence = new AccessCapabilities(
                ProtocolVersion.Current, new[] { 4 }, remoteTransport: true, persistence: false, resumption: true);

            Assert.True(remoteWithoutPersistence.RemoteTransport);
            Assert.False(remoteWithoutPersistence.Persistence);
            Assert.True(remoteWithoutPersistence.Resumption);
        }
    }
}

using System;
using System.Collections.Generic;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Frozen protocol constants for V5. V4 used MJ-TCP/1 ("MJTC" header, JSON
    /// payload, business version 8); none of those values are negotiated here.
    /// </summary>
    public static class ProtocolConstants
    {
        public const uint Major = 1;
        public const uint Minor = 0;
        public const uint RulesetVersion = 1;
        public const uint ProjectionVersion = 1;
        public const uint EventSchemaVersion = 1;
        public const uint CommandPayloadSchemaVersion = 1;

        public const uint LegacyV4WireVersion = 1;
        public const uint LegacyV4BusinessVersion = 8;

        public const int MinSeat = 0;
        public const int MaxSeat = 3;
        public const int NoSeat = -1;

        public const int MaxIdChars = 128;
        public const int MaxReasonCodeChars = 64;

        private static readonly byte[] LegacyV4MagicBytes = { (byte)'M', (byte)'J', (byte)'T', (byte)'C' };

        /// <summary>The four bytes of the V4 frame header. Never a V5 handshake prefix.</summary>
        public static ReadOnlySpan<byte> LegacyV4Magic => LegacyV4MagicBytes;
    }

    /// <summary>An immutable major.minor protocol version.</summary>
    public readonly struct ProtocolVersion : IEquatable<ProtocolVersion>, IComparable<ProtocolVersion>
    {
        public ProtocolVersion(uint major, uint minor)
        {
            if (major == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(major), major, "Protocol major 0 is reserved for unspecified.");
            }

            Major = major;
            Minor = minor;
        }

        public uint Major { get; }

        public uint Minor { get; }

        public static ProtocolVersion Current => new ProtocolVersion(ProtocolConstants.Major, ProtocolConstants.Minor);

        public bool Equals(ProtocolVersion other)
        {
            return Major == other.Major && Minor == other.Minor;
        }

        public override bool Equals(object? obj)
        {
            return obj is ProtocolVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Major * 397) ^ (int)Minor;
            }
        }

        public int CompareTo(ProtocolVersion other)
        {
            int major = Major.CompareTo(other.Major);
            return major != 0 ? major : Minor.CompareTo(other.Minor);
        }

        public override string ToString()
        {
            return Major.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Everything a connecting client claims before WELCOME is issued.</summary>
    public readonly struct VersionHandshake
    {
        public VersionHandshake(
            ProtocolVersion clientVersion,
            uint rulesetVersion,
            uint projectionVersion,
            bool requiresMinorFeatures,
            bool claimsLegacyV4Wire)
        {
            if (rulesetVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rulesetVersion), rulesetVersion, "Ruleset version 0 is not supported.");
            }

            if (projectionVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(projectionVersion), projectionVersion, "Projection version 0 is not supported.");
            }

            ClientVersion = clientVersion;
            RulesetVersion = rulesetVersion;
            ProjectionVersion = projectionVersion;
            RequiresMinorFeatures = requiresMinorFeatures;
            ClaimsLegacyV4Wire = claimsLegacyV4Wire;
        }

        public ProtocolVersion ClientVersion { get; }

        public uint RulesetVersion { get; }

        public uint ProjectionVersion { get; }

        /// <summary>
        /// True when the client depends on semantics introduced after the server's
        /// minor. A higher minor with only optional/ignorable fields is accepted;
        /// a higher minor that requires unknown behavior is rejected.
        /// </summary>
        public bool RequiresMinorFeatures { get; }

        /// <summary>True when the peer claims V4 framing/versions. Always rejected.</summary>
        public bool ClaimsLegacyV4Wire { get; }
    }

    public enum CompatibilityStatus
    {
        Unspecified = 0,
        Compatible = 1,
        MajorUnsupported = 2,
        MinorUnsupported = 3,
        RulesetUnsupported = 4,
        ProjectionUnsupported = 5,
        LegacyV4Rejected = 6
    }

    public readonly struct CompatibilityDecision
    {
        internal CompatibilityDecision(bool allowed, CompatibilityStatus status, string code, string detail)
        {
            Allowed = allowed;
            Status = status;
            Code = code;
            Detail = detail;
        }

        public bool Allowed { get; }

        public CompatibilityStatus Status { get; }

        /// <summary>Stable protocol error code, empty when allowed.</summary>
        public string Code { get; }

        public string Detail { get; }
    }

    /// <summary>A frozen, human-readable row of the V5 compatibility matrix.</summary>
    public sealed class CompatibilityRule
    {
        public CompatibilityRule(string scenario, string outcome, string code)
        {
            Scenario = scenario;
            Outcome = outcome;
            Code = code;
        }

        public string Scenario { get; }

        public string Outcome { get; }

        /// <summary>Stable error/close code, or empty when the row is accepted.</summary>
        public string Code { get; }
    }

    /// <summary>
    /// P0.2 compatibility matrix, executable. The matrix is intentionally
    /// one-way: V4 peers are never sniffed, downgraded or mixed into V5 rooms.
    /// </summary>
    public static class ProtocolCompatibility
    {
        private static readonly CompatibilityRule[] FrozenMatrix =
        {
            new CompatibilityRule("protocolMajor differs", "Reject; no downgrade", ProtocolErrorCode.ProtocolMajorUnsupported),
            new CompatibilityRule("peer claims V4 MJTC/business 8", "Reject as legacy V4", ProtocolErrorCode.LegacyV4Rejected),
            new CompatibilityRule("protocolMinor higher, optional-only", "Accept; ignore unknown optional fields", string.Empty),
            new CompatibilityRule("protocolMinor higher, required semantics", "Reject", ProtocolErrorCode.ProtocolMinorUnsupported),
            new CompatibilityRule("protocolMinor lower", "Accept; server sends only shared required fields", string.Empty),
            new CompatibilityRule("rulesetVersion differs", "Reject", ProtocolErrorCode.RulesetVersionUnsupported),
            new CompatibilityRule("projectionVersion differs", "Reject", ProtocolErrorCode.ProjectionVersionUnsupported),
            new CompatibilityRule("unknown message type", "Safe ERROR; connection may stay open", ProtocolErrorCode.UnknownMessageType),
            new CompatibilityRule("unknown event type", "Preserve raw; never a domain fact; require full baseline", ProtocolErrorCode.StreamGap),
            new CompatibilityRule("message exceeds limit", "Reject; never truncate", ProtocolErrorCode.MessageTooLarge)
        };

        public static IReadOnlyList<CompatibilityRule> Matrix =>
            Array.AsReadOnly(FrozenMatrix);

        public static bool HasLegacyV4Magic(ReadOnlySpan<byte> bytes)
        {
            return bytes.Length >= 4 && bytes.Slice(0, 4).SequenceEqual(ProtocolConstants.LegacyV4Magic);
        }

        public static CompatibilityDecision Evaluate(VersionHandshake client, ProtocolVersion serverVersion)
        {
            if (client.ClaimsLegacyV4Wire)
            {
                return Reject(
                    CompatibilityStatus.LegacyV4Rejected,
                    ProtocolErrorCode.LegacyV4Rejected,
                    "V4 MJ-TCP/1 and V5 WSS/Protobuf are different wires and are never mixed.");
            }

            if (client.ClientVersion.Major != serverVersion.Major)
            {
                return Reject(
                    CompatibilityStatus.MajorUnsupported,
                    ProtocolErrorCode.ProtocolMajorUnsupported,
                    "Client major " + client.ClientVersion.Major + " is not server major " + serverVersion.Major + ".");
            }

            if (client.ClientVersion.Minor > serverVersion.Minor && client.RequiresMinorFeatures)
            {
                return Reject(
                    CompatibilityStatus.MinorUnsupported,
                    ProtocolErrorCode.ProtocolMinorUnsupported,
                    "Client requires features from minor " + client.ClientVersion.Minor + " but server supports " + serverVersion.Minor + ".");
            }

            if (client.RulesetVersion != ProtocolConstants.RulesetVersion)
            {
                return Reject(
                    CompatibilityStatus.RulesetUnsupported,
                    ProtocolErrorCode.RulesetVersionUnsupported,
                    "Ruleset version " + client.RulesetVersion + " cannot join a rulesetVersion=" + ProtocolConstants.RulesetVersion + " room.");
            }

            if (client.ProjectionVersion != ProtocolConstants.ProjectionVersion)
            {
                return Reject(
                    CompatibilityStatus.ProjectionUnsupported,
                    ProtocolErrorCode.ProjectionVersionUnsupported,
                    "Projection version " + client.ProjectionVersion + " cannot share a stream with projectionVersion=" + ProtocolConstants.ProjectionVersion + ".");
            }

            return new CompatibilityDecision(true, CompatibilityStatus.Compatible, string.Empty, "Compatible V5 handshake.");
        }

        public static CompatibilityDecision EvaluateCurrent(VersionHandshake client)
        {
            return Evaluate(client, ProtocolVersion.Current);
        }

        private static CompatibilityDecision Reject(CompatibilityStatus status, string code, string detail)
        {
            return new CompatibilityDecision(false, status, code, detail);
        }
    }
}

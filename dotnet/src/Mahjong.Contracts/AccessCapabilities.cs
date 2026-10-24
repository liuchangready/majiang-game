using System;
using System.Collections.Generic;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Immutable declaration of what one endpoint can actually do. This is a
    /// declaration only: it neither negotiates a connection nor implements
    /// transport, persistence or resumption.
    ///
    /// The protocol version is the frozen V5 capability version
    /// (<see cref="ProtocolVersion.Current"/>). V4's AccessCapabilities class
    /// defaulted to protocolVersion=3 and its real endpoints used business
    /// version 8; neither value is a V5 capability or a V5 negotiation result.
    /// The current V5 product supports four-player riichi only. The
    /// supported-player-count set remains part of the contract shape, but a
    /// value in it is not a claim that three-player rules exist.
    /// </summary>
    public sealed class AccessCapabilities : IEquatable<AccessCapabilities>
    {
        /// <summary>
        /// The player count of the current V5 product. The contract can
        /// represent 1..CurrentPlayerCount seat counts, but only this
        /// four-player value is a current product support claim;
        /// three-player rules remain out of scope.
        /// </summary>
        public const int CurrentPlayerCount = ProtocolConstants.MaxSeat + 1;

        private readonly IReadOnlyList<int> _supportedPlayerCounts;

        /// <summary>
        /// Declares the current V5 local contract baseline: protocol 1.0, four
        /// players, and no runtime capability claimed. Transport, persistence
        /// and resumption default to false because no V5 endpoint is wired in
        /// this batch; an endpoint owner must opt in only with evidence.
        /// </summary>
        public AccessCapabilities()
            : this(ProtocolVersion.Current, new[] { CurrentPlayerCount })
        {
        }

        /// <summary>
        /// Creates a capability declaration. The player-count input is copied
        /// and de-duplicated while preserving first-seen order; the exposed
        /// list is read-only.
        /// </summary>
        public AccessCapabilities(
            ProtocolVersion protocolVersion,
            IEnumerable<int> supportedPlayerCounts,
            bool remoteTransport = false,
            bool persistence = false,
            bool resumption = false)
        {
            if (!protocolVersion.Equals(ProtocolVersion.Current))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(protocolVersion),
                    protocolVersion,
                    "AccessCapabilities declares only the frozen V5 protocol version " +
                    ProtocolVersion.Current +
                    "; V4 legacy protocolVersion values (3/8) are not V5 capabilities.");
            }

            if (supportedPlayerCounts == null)
            {
                throw new ArgumentNullException(nameof(supportedPlayerCounts));
            }

            var counts = new List<int>();
            foreach (int count in supportedPlayerCounts)
            {
                // 1..4 are structurally possible seat counts. Accepting one
                // here does not establish product support for a 1/2/3-player
                // rules mode; the current product claim remains {4}.
                if (count < 1 || count > CurrentPlayerCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(supportedPlayerCounts),
                        count,
                        "Supported player counts must be between 1 and " + CurrentPlayerCount + ".");
                }

                if (!counts.Contains(count))
                {
                    counts.Add(count);
                }
            }

            if (counts.Count == 0)
            {
                throw new ArgumentException("supportedPlayerCounts cannot be empty.", nameof(supportedPlayerCounts));
            }

            ProtocolVersion = protocolVersion;
            _supportedPlayerCounts = Array.AsReadOnly(counts.ToArray());
            RemoteTransport = remoteTransport;
            Persistence = persistence;
            Resumption = resumption;
        }

        public ProtocolVersion ProtocolVersion { get; }

        /// <summary>
        /// Declared player counts in first-seen order. The list is a private
        /// snapshot and cannot be mutated by callers.
        /// </summary>
        public IReadOnlyList<int> SupportedPlayerCounts => _supportedPlayerCounts;

        /// <summary>True only when a remote transport is actually wired for this endpoint.</summary>
        public bool RemoteTransport { get; }

        /// <summary>True only when durable persistence is actually implemented for this endpoint.</summary>
        public bool Persistence { get; }

        /// <summary>True only when an interrupted session can actually be resumed.</summary>
        public bool Resumption { get; }

        public bool Equals(AccessCapabilities? other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (!ProtocolVersion.Equals(other.ProtocolVersion) ||
                RemoteTransport != other.RemoteTransport ||
                Persistence != other.Persistence ||
                Resumption != other.Resumption ||
                _supportedPlayerCounts.Count != other._supportedPlayerCounts.Count)
            {
                return false;
            }

            for (int i = 0; i < _supportedPlayerCounts.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < other._supportedPlayerCounts.Count; j++)
                {
                    if (other._supportedPlayerCounts[j] == _supportedPlayerCounts[i])
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is AccessCapabilities other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + ProtocolVersion.GetHashCode();
                hash = (hash * 31) + RemoteTransport.GetHashCode();
                hash = (hash * 31) + Persistence.GetHashCode();
                hash = (hash * 31) + Resumption.GetHashCode();

                // Supported counts are a set, so their hash must not depend on
                // declaration order.
                int countsHash = 0;
                for (int i = 0; i < _supportedPlayerCounts.Count; i++)
                {
                    countsHash += _supportedPlayerCounts[i];
                }

                return (hash * 31) + countsHash;
            }
        }

        public override string ToString()
        {
            return "AccessCapabilities(protocolVersion=" + ProtocolVersion +
                   ", supportedPlayerCounts=[" + string.Join(", ", _supportedPlayerCounts) +
                   "], remoteTransport=" + RemoteTransport +
                   ", persistence=" + Persistence +
                   ", resumption=" + Resumption + ")";
        }
    }
}

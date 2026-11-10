using System;
using System.Collections.Generic;

namespace Mahjong.LocalHost
{
    /// <summary>Stable AI profile ids understood by the local table driver.</summary>
    public static class LocalAiProfiles
    {
        /// <summary>The historical local driver heuristic. Kept as the default for compatibility.</summary>
        public const string LegacyHeuristic = "legacy-heuristic";

        /// <summary>Ordinary actions only, delegated to <c>BasicAiStrategy</c>.</summary>
        public const string Basic = "baseline-basic";

        /// <summary>
        /// Ordinary actions delegated to <c>BasicAiStrategy</c>, with seeded random selection
        /// among the authority's authorized cheat candidates.
        /// </summary>
        public const string RandomCheatBasic = "baseline-basic-random-cheat";

        public static bool IsKnown(string? profileId)
        {
            return string.Equals(profileId, LegacyHeuristic, StringComparison.Ordinal) ||
                   string.Equals(profileId, Basic, StringComparison.Ordinal) ||
                   string.Equals(profileId, RandomCheatBasic, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Local AI configuration. The seed is explicit so the random cheat branch can be replayed;
    /// it never winds the shuffle or changes any rule. Cheat legality, cost and authorization
    /// stay with the Domain authority.
    /// </summary>
    public sealed class LocalAiOptions
    {
        public string ProfileId { get; set; } = LocalAiProfiles.LegacyHeuristic;

        /// <summary>Explicit seed for the random cheat branch (SplitMix64).</summary>
        public ulong Seed { get; set; } = 1UL;

        /// <summary>When false the new profile behaves exactly like Basic (no cheat candidate chosen).</summary>
        public bool AllowCheats { get; set; }

        /// <summary>Strategy-level per-round cheat budget.</summary>
        public int MaxSpendPerRound { get; set; } = 60;

        /// <summary>Points the strategy must keep unspent.</summary>
        public int ReservePoints { get; set; }

        internal void Validate()
        {
            if (!LocalAiProfiles.IsKnown(ProfileId))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "Unknown AI profile '" + ProfileId + "'.");
            }

            if (MaxSpendPerRound < 0 || ReservePoints < 0)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "AI cheat budget and reserve must be non-negative.");
            }
        }
    }

    /// <summary>One sanitized AI decision observation for validation evidence.</summary>
    public sealed class LocalAiDecisionRecord
    {
        public LocalAiDecisionRecord(
            string roundId,
            int seat,
            string phase,
            string decisionKind,
            string optionId,
            string capabilityId,
            string status,
            string reasonCode,
            string detail,
            long stateVersion)
        {
            RoundId = roundId ?? string.Empty;
            Seat = seat;
            Phase = phase ?? string.Empty;
            DecisionKind = decisionKind ?? string.Empty;
            OptionId = optionId ?? string.Empty;
            CapabilityId = capabilityId ?? string.Empty;
            Status = status ?? string.Empty;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            StateVersion = stateVersion;
        }

        public string RoundId { get; }

        public int Seat { get; }

        public string Phase { get; }

        /// <summary><c>ordinary</c>, <c>cheat</c> or <c>cheat-fallback</c>.</summary>
        public string DecisionKind { get; }

        public string OptionId { get; }

        /// <summary><c>mahjong</c> or a stable cheat capability id.</summary>
        public string CapabilityId { get; }

        public string Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public long StateVersion { get; }
    }

    /// <summary>Receives sanitized decision records; never hidden hands, wall order or seeds.</summary>
    public interface ILocalAiDecisionObserver
    {
        void OnDecision(LocalAiDecisionRecord record);
    }
}

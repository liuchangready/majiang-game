using System;
using System.Collections.Generic;
using Mahjong.Ai;

namespace Mahjong.LocalHost
{
    /// <summary>Stable AI profile ids understood by the local table driver.</summary>
    public static class LocalAiProfiles
    {
        /// <summary>The historical local driver heuristic. Kept as the default for compatibility.</summary>
        public const string LegacyHeuristic = AiProfileIds.LegacyHeuristic;

        /// <summary>Ordinary actions only, delegated to <c>BasicAiStrategy</c>.</summary>
        public const string Basic = AiProfileIds.Basic;

        /// <summary>
        /// Ordinary actions delegated to <c>BasicAiStrategy</c>, with seeded random selection
        /// among the authority's authorized cheat candidates.
        /// </summary>
        public const string RandomCheatBasic = AiProfileIds.RandomCheatBasic;

        /// <summary>
        /// Ordinary actions delegated to <c>BasicAiStrategy</c>, with deterministic staged
        /// scoring over authorized cheat candidates using visible hand/table facts.
        /// </summary>
        public const string ScoredCheatBasic = AiProfileIds.ScoredCheatBasic;

        public static bool IsKnown(string? profileId) => AiProfileIds.IsBuiltIn(profileId);
    }

    /// <summary>
    /// Local AI configuration. Supports both legacy single-profile configuration and
    /// M1-04 per-seat <see cref="CharacterAiProfile"/> assembly (<see cref="SeatProfiles"/>)
    /// plus an optional <see cref="TakeoverProfile"/>. Cheat legality, cost and
    /// authorization stay with the Domain authority.
    /// </summary>
    public sealed class LocalAiOptions
    {
        public string ProfileId { get; set; } = LocalAiProfiles.LegacyHeuristic;

        /// <summary>Explicit base seed for isolated per-seat random streams (SplitMix64).</summary>
        public ulong Seed { get; set; } = 1UL;

        /// <summary>When false the single-profile cheat strategies behave like Basic (no paid cheat chosen).</summary>
        public bool AllowCheats { get; set; }

        /// <summary>Strategy-level per-round cheat budget for single-profile mode.</summary>
        public int MaxSpendPerRound { get; set; } = 60;

        /// <summary>Points the strategy must keep unspent in single-profile mode.</summary>
        public int ReservePoints { get; set; }

        /// <summary>
        /// Whether a disconnected human's takeover seat may initiate new paid cheats.
        /// Defaults to false, matching AiCheatPolicy and the AI design's takeover rule.
        /// </summary>
        public bool TakeoverMaySpend { get; set; }

        /// <summary>
        /// Optional four-seat profile assembly (seats 0..3). When null, all seats inherit
        /// the single-profile properties above with isolated per-seat strategy and RNG state.
        /// </summary>
        public IReadOnlyList<CharacterAiProfile>? SeatProfiles { get; set; }

        /// <summary>
        /// Optional explicit takeover profile applied when a human seat enters Takeover.
        /// When null, takeover defaults to a dedicated non-spending Basic profile unless
        /// <see cref="TakeoverMaySpend"/> (or seat policy) explicitly opts in.
        /// </summary>
        public CharacterAiProfile? TakeoverProfile { get; set; }

        /// <summary>
        /// Fallback strategy id used when a seat profile's strategy id or model version is
        /// missing or incompatible (<c>baseline-basic</c> or <c>legacy-heuristic</c>).
        /// </summary>
        public string FallbackProfileId { get; set; } = LocalAiProfiles.Basic;

        internal void Validate()
        {
            if (MaxSpendPerRound < 0 || ReservePoints < 0)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "AI cheat budget and reserve must be non-negative.");
            }

            if (SeatProfiles != null)
            {
                if (SeatProfiles.Count != 4)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.InvalidOptions,
                        "SeatProfiles must contain exactly 4 seats.");
                }

                for (int seat = 0; seat < 4; seat++)
                {
                    if (SeatProfiles[seat] == null)
                    {
                        throw new LocalTableException(
                            LocalTableErrorKind.InvalidOptions,
                            "SeatProfiles[" + seat + "] must not be null.");
                    }
                }
            }
        }

        public CharacterAiProfile[] ResolveSeatProfiles()
        {
            if (SeatProfiles != null)
            {
                var resolved = new CharacterAiProfile[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    resolved[seat] = SeatProfiles[seat];
                }

                return resolved;
            }

            var singlePolicy = new AiCheatPolicy(
                enabled: AllowCheats,
                maxSpendPerRound: MaxSpendPerRound,
                reservePoints: ReservePoints,
                takeoverMaySpend: TakeoverMaySpend);
            string fallback = ResolveFallbackProfileId();
            var seats = new CharacterAiProfile[4];
            for (int seat = 0; seat < 4; seat++)
            {
                ulong seatSeed = DeriveSeatSeed(Seed, seat);
                string profileId = string.IsNullOrWhiteSpace(ProfileId) ? string.Empty : ProfileId;
                seats[seat] = new CharacterAiProfile(
                    string.IsNullOrWhiteSpace(profileId) ? "missing-profile" : profileId,
                    profileId,
                    CharacterStyle.Neutral,
                    singlePolicy,
                    modelVersion: null,
                    fallbackStrategyId: null,
                    seed: seatSeed);
            }

            return seats;
        }

        internal string ResolveFallbackProfileId()
        {
            if (string.Equals(FallbackProfileId, LocalAiProfiles.LegacyHeuristic, StringComparison.Ordinal))
            {
                return LocalAiProfiles.LegacyHeuristic;
            }

            return LocalAiProfiles.Basic;
        }

        internal LocalAiOptions Clone()
        {
            CharacterAiProfile[]? clonedSeats = null;
            if (SeatProfiles != null)
            {
                clonedSeats = new CharacterAiProfile[SeatProfiles.Count];
                for (int i = 0; i < SeatProfiles.Count; i++)
                {
                    clonedSeats[i] = SeatProfiles[i];
                }
            }

            return new LocalAiOptions
            {
                ProfileId = ProfileId,
                Seed = Seed,
                AllowCheats = AllowCheats,
                MaxSpendPerRound = MaxSpendPerRound,
                ReservePoints = ReservePoints,
                TakeoverMaySpend = TakeoverMaySpend,
                SeatProfiles = clonedSeats,
                TakeoverProfile = TakeoverProfile,
                FallbackProfileId = FallbackProfileId
            };
        }

        internal static ulong DeriveSeatSeed(ulong baseSeed, int seat)
        {
            ulong z = baseSeed + (0x9E3779B97F4A7C15UL * (ulong)(seat + 1));
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
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
            : this(
                roundId,
                seat,
                phase,
                decisionKind,
                optionId,
                capabilityId,
                status,
                reasonCode,
                detail,
                stateVersion,
                string.Empty,
                string.Empty)
        {
        }

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
            long stateVersion,
            string profileId,
            string strategyId)
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
            ProfileId = profileId ?? string.Empty;
            StrategyId = strategyId ?? string.Empty;
        }

        public string RoundId { get; }

        public int Seat { get; }

        public string Phase { get; }

        /// <summary><c>ordinary</c>, <c>cheat</c>, <c>cheat-fallback</c> or <c>strategy-fallback</c>.</summary>
        public string DecisionKind { get; }

        public string OptionId { get; }

        /// <summary><c>mahjong</c> or a stable cheat capability id.</summary>
        public string CapabilityId { get; }

        public string Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public long StateVersion { get; }

        public string ProfileId { get; }

        public string StrategyId { get; }
    }

    /// <summary>Receives sanitized decision records; never hidden hands, wall order or seeds.</summary>
    public interface ILocalAiDecisionObserver
    {
        void OnDecision(LocalAiDecisionRecord record);
    }
}

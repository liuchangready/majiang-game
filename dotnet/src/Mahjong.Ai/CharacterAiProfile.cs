using System;
using System.Globalization;
using System.Text;

namespace Mahjong.Ai
{
    /// <summary>
    /// Built-in AI profile/strategy identifiers shared between <c>Mahjong.Ai</c> and hosts.
    /// </summary>
    public static class AiProfileIds
    {
        public const string LegacyHeuristic = "legacy-heuristic";
        public const string Basic = "baseline-basic";
        public const string RandomCheatBasic = "baseline-basic-random-cheat";
        public const string ScoredCheatBasic = "scored-cheat-basic";
        public const string SupportedHeuristicModelVersion = "heuristic-v1";

        public static bool IsBuiltIn(string? profileId)
        {
            return string.Equals(profileId, LegacyHeuristic, StringComparison.Ordinal) ||
                   string.Equals(profileId, Basic, StringComparison.Ordinal) ||
                   string.Equals(profileId, RandomCheatBasic, StringComparison.Ordinal) ||
                   string.Equals(profileId, ScoredCheatBasic, StringComparison.Ordinal);
        }

        public static bool IsCompatibleHeuristicModelVersion(string? modelVersion)
        {
            if (string.IsNullOrWhiteSpace(modelVersion))
            {
                return true;
            }

            return string.Equals(modelVersion, SupportedHeuristicModelVersion, StringComparison.Ordinal) ||
                   string.Equals(modelVersion, "1", StringComparison.Ordinal) ||
                   string.Equals(modelVersion, "v1", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Five-dimensional character style conditioning vector (Section 3.3 of the AI design):
    /// <c>[Aggression, MeldTendency, FlushTendency, Defense, CheatAggression]</c>, each in [0, 1].
    /// Purely heuristic/conditioning metadata; never enters authority state or command hashes.
    /// </summary>
    public sealed class CharacterStyle : IEquatable<CharacterStyle?>
    {
        public CharacterStyle(
            double aggression = 0.5,
            double meldTendency = 0.5,
            double flushTendency = 0.5,
            double defense = 0.5,
            double cheatAggression = 0.5)
        {
            Aggression = ValidateUnit(aggression, nameof(aggression));
            MeldTendency = ValidateUnit(meldTendency, nameof(meldTendency));
            FlushTendency = ValidateUnit(flushTendency, nameof(flushTendency));
            Defense = ValidateUnit(defense, nameof(defense));
            CheatAggression = ValidateUnit(cheatAggression, nameof(cheatAggression));
        }

        public static CharacterStyle Neutral { get; } = new CharacterStyle();

        public double Aggression { get; }

        public double MeldTendency { get; }

        public double FlushTendency { get; }

        public double Defense { get; }

        public double CheatAggression { get; }

        public bool Equals(CharacterStyle? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null &&
                   BitConverter.DoubleToInt64Bits(Aggression) == BitConverter.DoubleToInt64Bits(other.Aggression) &&
                   BitConverter.DoubleToInt64Bits(MeldTendency) == BitConverter.DoubleToInt64Bits(other.MeldTendency) &&
                   BitConverter.DoubleToInt64Bits(FlushTendency) == BitConverter.DoubleToInt64Bits(other.FlushTendency) &&
                   BitConverter.DoubleToInt64Bits(Defense) == BitConverter.DoubleToInt64Bits(other.Defense) &&
                   BitConverter.DoubleToInt64Bits(CheatAggression) == BitConverter.DoubleToInt64Bits(other.CheatAggression);
        }

        public override bool Equals(object? obj) => obj is CharacterStyle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = BitConverter.DoubleToInt64Bits(Aggression).GetHashCode();
                hash = (hash * 31) ^ BitConverter.DoubleToInt64Bits(MeldTendency).GetHashCode();
                hash = (hash * 31) ^ BitConverter.DoubleToInt64Bits(FlushTendency).GetHashCode();
                hash = (hash * 31) ^ BitConverter.DoubleToInt64Bits(Defense).GetHashCode();
                hash = (hash * 31) ^ BitConverter.DoubleToInt64Bits(CheatAggression).GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(CharacterStyle? left, CharacterStyle? right) =>
            left is null ? right is null : left.Equals(right);

        public static bool operator !=(CharacterStyle? left, CharacterStyle? right) => !(left == right);

        public override string ToString()
        {
            var sb = new StringBuilder("CharacterStyle(");
            sb.Append("agg=").Append(Aggression.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",meld=").Append(MeldTendency.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",flush=").Append(FlushTendency.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",def=").Append(Defense.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",cheat=").Append(CheatAggression.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(')');
            return sb.ToString();
        }

        private static double ValidateUnit(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be in [0.0, 1.0].");
            }

            return value;
        }
    }

    /// <summary>
    /// Per-seat AI assembly profile: binds a profile identifier, strategy identifier,
    /// character style parameters, <see cref="AiCheatPolicy"/>, optional model version,
    /// fallback strategy id, and optional isolated random seed. Immutable and safe to share
    /// across round reconstructions; mutable spend/RNG state lives in the per-seat strategy
    /// instance constructed from this profile.
    /// </summary>
    public sealed class CharacterAiProfile : IEquatable<CharacterAiProfile?>
    {
        public CharacterAiProfile(string profileId)
            : this(profileId, profileId, null, null, null, null, null)
        {
        }

        public CharacterAiProfile(
            string profileId,
            string? strategyId,
            CharacterStyle? style = null,
            AiCheatPolicy? cheatPolicy = null,
            string? modelVersion = null,
            string? fallbackStrategyId = null,
            ulong? seed = null)
        {
            ProfileId = AiGuard.RequireText(profileId, nameof(profileId));
            StrategyId = strategyId;
            Style = style ?? CharacterStyle.Neutral;
            CheatPolicy = cheatPolicy ?? new AiCheatPolicy();
            ModelVersion = string.IsNullOrWhiteSpace(modelVersion) ? null : modelVersion;
            FallbackStrategyId = string.IsNullOrWhiteSpace(fallbackStrategyId)
                ? null
                : fallbackStrategyId;
            Seed = seed;
        }

        public string ProfileId { get; }

        /// <summary>
        /// Strategy implementation identifier (for example <c>scored-cheat-basic</c>).
        /// May be null/blank/unknown to represent an unavailable strategy that triggers
        /// host fallback without blocking round recovery.
        /// </summary>
        public string? StrategyId { get; }

        public CharacterStyle Style { get; }

        public AiCheatPolicy CheatPolicy { get; }

        /// <summary>
        /// Optional model version tag. Heuristic strategies accept null or
        /// <see cref="AiProfileIds.SupportedHeuristicModelVersion"/>; incompatible tags
        /// trigger configured heuristic/Basic fallback.
        /// </summary>
        public string? ModelVersion { get; }

        /// <summary>
        /// Fallback strategy identifier when <see cref="StrategyId"/> or
        /// <see cref="ModelVersion"/> is missing or incompatible. Defaults to
        /// <see cref="AiProfileIds.Basic"/>.
        /// </summary>
        public string? FallbackStrategyId { get; }

        /// <summary>Optional per-seat random seed override.</summary>
        public ulong? Seed { get; }

        public static CharacterAiProfile CreateLegacyHeuristic(
            string profileId = AiProfileIds.LegacyHeuristic,
            AiCheatPolicy? cheatPolicy = null,
            ulong? seed = null)
        {
            return new CharacterAiProfile(
                profileId,
                AiProfileIds.LegacyHeuristic,
                CharacterStyle.Neutral,
                cheatPolicy ?? new AiCheatPolicy(enabled: false),
                modelVersion: null,
                fallbackStrategyId: AiProfileIds.LegacyHeuristic,
                seed: seed);
        }

        public static CharacterAiProfile CreateBasic(
            string profileId = AiProfileIds.Basic,
            CharacterStyle? style = null,
            AiCheatPolicy? cheatPolicy = null,
            ulong? seed = null)
        {
            return new CharacterAiProfile(
                profileId,
                AiProfileIds.Basic,
                style,
                cheatPolicy ?? new AiCheatPolicy(enabled: false),
                modelVersion: null,
                fallbackStrategyId: AiProfileIds.Basic,
                seed: seed);
        }

        public static CharacterAiProfile CreateRandomCheatBasic(
            string profileId = AiProfileIds.RandomCheatBasic,
            AiCheatPolicy? cheatPolicy = null,
            CharacterStyle? style = null,
            ulong? seed = null)
        {
            return new CharacterAiProfile(
                profileId,
                AiProfileIds.RandomCheatBasic,
                style,
                cheatPolicy ?? new AiCheatPolicy(enabled: true),
                modelVersion: null,
                fallbackStrategyId: AiProfileIds.Basic,
                seed: seed);
        }

        public static CharacterAiProfile CreateScoredBasic(
            string profileId = AiProfileIds.ScoredCheatBasic,
            CharacterStyle? style = null,
            AiCheatPolicy? cheatPolicy = null,
            string? modelVersion = null,
            string? fallbackStrategyId = null,
            ulong? seed = null)
        {
            return new CharacterAiProfile(
                profileId,
                AiProfileIds.ScoredCheatBasic,
                style,
                cheatPolicy ?? new AiCheatPolicy(enabled: true),
                modelVersion: modelVersion,
                fallbackStrategyId: fallbackStrategyId,
                seed: seed);
        }

        public static CharacterAiProfile CreateTakeoverDefault(
            string profileId = "takeover-basic",
            bool takeoverMaySpend = false,
            int maxSpendPerRound = 60,
            int reservePoints = 0)
        {
            return new CharacterAiProfile(
                profileId,
                AiProfileIds.Basic,
                CharacterStyle.Neutral,
                new AiCheatPolicy(
                    enabled: takeoverMaySpend,
                    maxSpendPerRound: maxSpendPerRound,
                    reservePoints: reservePoints,
                    takeoverMaySpend: takeoverMaySpend),
                modelVersion: null,
                fallbackStrategyId: AiProfileIds.Basic,
                seed: null);
        }

        public CharacterAiProfile WithCheatPolicy(AiCheatPolicy cheatPolicy)
        {
            if (cheatPolicy == null)
            {
                throw new ArgumentNullException(nameof(cheatPolicy));
            }

            return new CharacterAiProfile(
                ProfileId,
                StrategyId,
                Style,
                cheatPolicy,
                ModelVersion,
                FallbackStrategyId,
                Seed);
        }

        public bool Equals(CharacterAiProfile? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other is not null &&
                   string.Equals(ProfileId, other.ProfileId, StringComparison.Ordinal) &&
                   string.Equals(StrategyId, other.StrategyId, StringComparison.Ordinal) &&
                   Style.Equals(other.Style) &&
                   CheatPolicy.Equals(other.CheatPolicy) &&
                   string.Equals(ModelVersion, other.ModelVersion, StringComparison.Ordinal) &&
                   string.Equals(FallbackStrategyId, other.FallbackStrategyId, StringComparison.Ordinal) &&
                   Seed == other.Seed;
        }

        public override bool Equals(object? obj) => obj is CharacterAiProfile other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(ProfileId);
                hash = (hash * 31) ^ (StrategyId == null ? 0 : StringComparer.Ordinal.GetHashCode(StrategyId));
                hash = (hash * 31) ^ Style.GetHashCode();
                hash = (hash * 31) ^ CheatPolicy.GetHashCode();
                hash = (hash * 31) ^ (ModelVersion == null ? 0 : StringComparer.Ordinal.GetHashCode(ModelVersion));
                hash = (hash * 31) ^ (FallbackStrategyId == null ? 0 : StringComparer.Ordinal.GetHashCode(FallbackStrategyId));
                hash = (hash * 31) ^ (Seed.HasValue ? Seed.Value.GetHashCode() : 0);
                return hash;
            }
        }
    }
}

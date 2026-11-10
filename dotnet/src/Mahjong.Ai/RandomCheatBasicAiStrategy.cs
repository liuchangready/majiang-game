using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace Mahjong.Ai
{
    /// <summary>
    /// Explicit, reproducible random source for AI-only choices. The interface is deliberately
    /// tiny so a caller (or a test) can inject a fixed sequence instead of relying on
    /// <see cref="System.Random"/> or an ambient clock. It has no authority meaning: it never
    /// changes a rule, a deck, a hidden tile or a settlement.
    /// </summary>
    public interface IAiRandom
    {
        /// <summary>Returns a value in [0, <paramref name="maxExclusive"/>).</summary>
        int Next(int maxExclusive);
    }

    /// <summary>
    /// SplitMix64 random source. It is fully determined by the supplied seed, has no hidden
    /// global state and produces the same sequence on every platform/runtime.
    /// </summary>
    public sealed class SeededAiRandom : IAiRandom
    {
        private ulong _state;

        public SeededAiRandom(ulong seed)
        {
            _state = seed;
        }

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxExclusive),
                    maxExclusive,
                    "The random bound must be positive.");
            }

            ulong value = NextUInt64();
            return (int)(value % (ulong)maxExclusive);
        }

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// One authorized cheat candidate as an AI may see it. It carries no wall order, no
    /// opponent hand, no tile-position map and no authority callback; the runtime maps the
    /// chosen <see cref="OptionId"/> back to a command it already prepared from the same
    /// authoritative snapshot.
    /// </summary>
    public sealed class AiCheatOption
    {
        public AiCheatOption(
            string optionId,
            string capabilityId,
            int cost,
            bool isSafeDefault,
            bool isOfferChoice)
        {
            OptionId = AiGuard.RequireText(optionId, nameof(optionId));
            CapabilityId = AiGuard.RequireText(capabilityId, nameof(capabilityId));
            if (cost < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cost), cost, "A cheat cost cannot be negative.");
            }

            Cost = cost;
            IsSafeDefault = isSafeDefault;
            IsOfferChoice = isOfferChoice;
        }

        public string OptionId { get; }

        public string CapabilityId { get; }

        public int Cost { get; }

        /// <summary>True for a no-op/first/keep option that cannot make the seat worse.</summary>
        public bool IsSafeDefault { get; }

        /// <summary>True when an already-paid offer must be resolved before the next action.</summary>
        public bool IsOfferChoice { get; }
    }

    /// <summary>
    /// Frozen cheat window shown to the AI. The only inputs are the seat's own point balance
    /// and authorized candidates; there is intentionally no state object, offer internals,
    /// wall tile or hidden hand.
    /// </summary>
    public sealed class AiCheatOpportunity
    {
        private readonly ReadOnlyCollection<AiCheatOption> _options;

        public AiCheatOpportunity(
            int seat,
            string roundId,
            long stateVersion,
            long controlEpoch,
            int cheatPoints,
            bool isOfferChoice,
            IReadOnlyList<AiCheatOption> options)
        {
            AiGuard.CheckSeat(seat, nameof(seat));
            RoundId = AiGuard.RequireText(roundId, nameof(roundId));
            AiGuard.CheckNonNegative(stateVersion, nameof(stateVersion));
            AiGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            if (cheatPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cheatPoints), cheatPoints, "Cheat points cannot be negative.");
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var copy = new AiCheatOption[options.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < options.Count; i++)
            {
                AiCheatOption option = options[i] ??
                    throw new ArgumentException("Cheat options cannot contain null.", nameof(options));
                if (!ids.Add(option.OptionId))
                {
                    throw new ArgumentException("Cheat option ids must be unique.", nameof(options));
                }

                copy[i] = option;
            }

            Seat = seat;
            StateVersion = stateVersion;
            ControlEpoch = controlEpoch;
            CheatPoints = cheatPoints;
            IsOfferChoice = isOfferChoice;
            _options = new ReadOnlyCollection<AiCheatOption>(copy);
        }

        public int Seat { get; }

        public string RoundId { get; }

        public long StateVersion { get; }

        public long ControlEpoch { get; }

        public int CheatPoints { get; }

        /// <summary>When true the seat must resolve an outstanding offer; "none" is not safe.</summary>
        public bool IsOfferChoice { get; }

        public IReadOnlyList<AiCheatOption> Options => _options;
    }

    /// <summary>
    /// New AI profile: every ordinary action is exactly <see cref="BasicAiStrategy"/>, and a
    /// legal, affordable, authorized cheat candidate may be selected at random. Randomness is
    /// supplied through <see cref="IAiRandom"/> (normally <see cref="SeededAiRandom"/>), so a
    /// seed replays the same selection sequence.
    ///
    /// Cheats are only offered by the authority projection; this class never builds a command,
    /// reads a wall/hand or decides legality. With no candidate the ordinary path is
    /// byte-for-byte the Basic strategy's choice. If the authority later rejects a cheat, the
    /// driver falls back to that ordinary path; no fee or physical move is fabricated here.
    /// </summary>
    public sealed class RandomCheatBasicAiStrategy : IAiStrategy
    {
        /// <summary>Stable profile/algorithm id; do not reuse for a different policy.</summary>
        public const string AlgorithmIdValue = "baseline-basic-random-cheat";

        /// <summary>Stable algorithm version.</summary>
        public const int AlgorithmVersionValue = 1;

        private readonly BasicAiStrategy _basic;
        private readonly IAiRandom _random;
        private readonly AiCheatPolicy _policy;
        private readonly int[] _spentBySeat = new int[4];
        private string? _budgetRoundId;

        public RandomCheatBasicAiStrategy(
            IAiRandom random,
            AiCheatPolicy? policy = null,
            BasicAiStrategy? basic = null)
        {
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _policy = policy ?? new AiCheatPolicy();
            _basic = basic ?? new BasicAiStrategy();
        }

        public string AlgorithmId => AlgorithmIdValue;

        public int AlgorithmVersion => AlgorithmVersionValue;

        public IAiRandom Random => _random;

        public AiCheatPolicy CheatPolicy => _policy;

        /// <summary>Ordinary actions are delegated unchanged to <see cref="BasicAiStrategy"/>.</summary>
        public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken)
        {
            return _basic.Decide(observation, cancellationToken);
        }

        /// <summary>
        /// Returns the chosen option id, or null for "do not use a cheat now". When
        /// <see cref="AiCheatOpportunity.IsOfferChoice"/> is true there is no skip: an already
        /// paid offer must be resolved, and the authority supplies at least one safe choice.
        /// </summary>
        public string? ChooseCheat(AiCheatOpportunity opportunity)
        {
            if (opportunity == null)
            {
                throw new ArgumentNullException(nameof(opportunity));
            }

            if (!_policy.Enabled)
            {
                return null;
            }

            if (!string.Equals(_budgetRoundId, opportunity.RoundId, StringComparison.Ordinal))
            {
                _budgetRoundId = opportunity.RoundId;
                Array.Clear(_spentBySeat, 0, _spentBySeat.Length);
            }

            int spent = _spentBySeat[opportunity.Seat];
            var eligible = new List<AiCheatOption>(opportunity.Options.Count);
            for (int i = 0; i < opportunity.Options.Count; i++)
            {
                AiCheatOption option = opportunity.Options[i];
                if (option.Cost > opportunity.CheatPoints || spent + option.Cost > _policy.MaxSpendPerRound)
                {
                    continue;
                }

                if (!string.Equals(option.CapabilityId, "none", StringComparison.Ordinal) &&
                    !_policy.Allows(option.CapabilityId))
                {
                    continue;
                }

                eligible.Add(option);
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            if (!opportunity.IsOfferChoice)
            {
                // Weight 1 for "do nothing": legal windows are common, and a cheat should be a
                // deliberate random branch rather than a guaranteed tax on every draw.
                if (_random.Next(eligible.Count + 1) == 0)
                {
                    return null;
                }
            }

            AiCheatOption selected = eligible[_random.Next(eligible.Count)];
            if (selected.Cost > 0)
            {
                // Reserve the policy budget at selection time. If the authority rejects the
                // command, the reservation is conservatively retained: the AI can under-spend
                // but can never exceed maxSpendPerRound.
                _spentBySeat[opportunity.Seat] = spent + selected.Cost;
            }

            return selected.OptionId;
        }

        public override string ToString() =>
            "RandomCheatBasicAiStrategy/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(CultureInfo.InvariantCulture) +
            "/cheats=" + (_policy.Enabled ? "enabled" : "disabled");
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Mahjong.Domain;

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
        private readonly ReadOnlyCollection<TileId> _tileIds;

        public AiCheatOption(
            string optionId,
            string capabilityId,
            int cost,
            bool isSafeDefault,
            bool isOfferChoice,
            IReadOnlyList<TileId>? tileIds = null,
            AiBeamSwapSlotFacts? targetSlot = null,
            bool targetMarked = false,
            TileId? targetTileId = null)
        {
            OptionId = AiGuard.RequireText(optionId, nameof(optionId));
            CapabilityId = AiGuard.RequireText(capabilityId, nameof(capabilityId));
            if (cost < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cost), cost, "A cheat cost cannot be negative.");
            }

            if (targetMarked)
            {
                if (!targetTileId.HasValue)
                {
                    throw new ArgumentException("targetTileId is required when targetMarked is true.", nameof(targetTileId));
                }
            }
            else if (targetTileId.HasValue)
            {
                throw new ArgumentException("targetTileId must be null when targetMarked is false.", nameof(targetTileId));
            }

            TileId[] copy;
            if (tileIds == null || tileIds.Count == 0)
            {
                copy = Array.Empty<TileId>();
            }
            else
            {
                copy = new TileId[tileIds.Count];
                for (int i = 0; i < tileIds.Count; i++)
                {
                    copy[i] = tileIds[i];
                }
            }

            Cost = cost;
            IsSafeDefault = isSafeDefault;
            IsOfferChoice = isOfferChoice;
            _tileIds = new ReadOnlyCollection<TileId>(copy);
            TargetSlot = targetSlot;
            TargetMarked = targetMarked;
            TargetTileId = targetTileId;
        }

        public string OptionId { get; }

        public string CapabilityId { get; }

        public int Cost { get; }

        /// <summary>True for a no-op/first/keep option that cannot make the seat worse.</summary>
        public bool IsSafeDefault { get; }

        /// <summary>True when an already-paid offer must be resolved before the next action.</summary>
        public bool IsOfferChoice { get; }

        /// <summary>Authorized visible physical tiles bound to this option (own hand or revealed sleeve candidate).</summary>
        public IReadOnlyList<TileId> TileIds => _tileIds;

        /// <summary>Frozen target slot for a beam-swap option, or null.</summary>
        public AiBeamSwapSlotFacts? TargetSlot { get; }

        /// <summary>Whether the target slot is revealed by an active own tile-mark group.</summary>
        public bool TargetMarked { get; }

        /// <summary>Target physical tile id when <see cref="TargetMarked"/> is true; always null for unmarked slots.</summary>
        public TileId? TargetTileId { get; }
    }

    /// <summary>
    /// Frozen cheat window shown to the AI. The only inputs are the seat's own point balance
    /// and authorized candidates; there is intentionally no state object, offer internals,
    /// wall tile or hidden hand.
    /// </summary>
    public sealed class AiCheatOpportunity
    {
        private readonly ReadOnlyCollection<AiCheatOption> _options;
        private readonly ReadOnlyCollection<TileId> _markableHandTileIds;
        private readonly ReadOnlyCollection<AiTileMarkGroupFacts> _activeTileMarkGroups;
        private readonly ReadOnlyCollection<TileId> _beamEligibleHandTileIds;
        private readonly ReadOnlyCollection<AiBeamSwapCandidateFacts> _beamCandidates;

        public AiCheatOpportunity(
            int seat,
            string roundId,
            long stateVersion,
            long controlEpoch,
            int cheatPoints,
            bool isOfferChoice,
            IReadOnlyList<AiCheatOption> options,
            string? decisionId = null,
            long? decisionDeadlineMillis = null,
            string? offerId = null,
            string? windowId = null,
            long? offerDeadlineMillis = null,
            long recoveryGeneration = 0L,
            AiSleeveBloomDrawKind? sleeveDrawKind = null,
            TileId? sleeveFirstTileId = null,
            TileId? sleeveSecondTileId = null,
            TileId? sleeveThirdTileId = null,
            AiBeamSwapWindow? beamWindow = null,
            TileId? beamDrawnTileId = null,
            IReadOnlyList<TileId>? beamEligibleHandTileIds = null,
            IReadOnlyList<AiBeamSwapCandidateFacts>? beamCandidates = null,
            IReadOnlyList<TileId>? markableHandTileIds = null,
            IReadOnlyList<AiTileMarkGroupFacts>? activeTileMarkGroups = null)
        {
            AiGuard.CheckSeat(seat, nameof(seat));
            RoundId = AiGuard.RequireText(roundId, nameof(roundId));
            AiGuard.CheckNonNegative(stateVersion, nameof(stateVersion));
            AiGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            if (cheatPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cheatPoints), cheatPoints, "Cheat points cannot be negative.");
            }

            if (decisionDeadlineMillis.HasValue)
            {
                AiGuard.CheckNonNegative(decisionDeadlineMillis.Value, nameof(decisionDeadlineMillis));
            }

            if (offerDeadlineMillis.HasValue)
            {
                AiGuard.CheckNonNegative(offerDeadlineMillis.Value, nameof(offerDeadlineMillis));
            }

            AiGuard.CheckNonNegative(recoveryGeneration, nameof(recoveryGeneration));

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
            DecisionId = string.IsNullOrWhiteSpace(decisionId) ? null : decisionId;
            DeadlineMillis = decisionDeadlineMillis;
            OfferId = string.IsNullOrWhiteSpace(offerId) ? null : offerId;
            WindowId = string.IsNullOrWhiteSpace(windowId) ? null : windowId;
            OfferDeadlineMillis = offerDeadlineMillis;
            RecoveryGeneration = recoveryGeneration;
            SleeveDrawKind = sleeveDrawKind;
            SleeveFirstTileId = sleeveFirstTileId;
            SleeveSecondTileId = sleeveSecondTileId;
            SleeveThirdTileId = sleeveThirdTileId;
            BeamWindow = beamWindow;
            BeamDrawnTileId = beamDrawnTileId;
            _markableHandTileIds = CopyTiles(markableHandTileIds);
            _activeTileMarkGroups = CopyGroups(activeTileMarkGroups);
            _beamEligibleHandTileIds = CopyTiles(beamEligibleHandTileIds);
            _beamCandidates = CopyBeamCandidates(beamCandidates);
        }

        public int Seat { get; }

        public string RoundId { get; }

        public long StateVersion { get; }

        public long ControlEpoch { get; }

        public int CheatPoints { get; }

        /// <summary>When true the seat must resolve an outstanding offer; "none" is not safe.</summary>
        public bool IsOfferChoice { get; }

        public IReadOnlyList<AiCheatOption> Options => _options;

        public string? DecisionId { get; }

        public long? DeadlineMillis { get; }

        public string? OfferId { get; }

        public string? WindowId { get; }

        public long? OfferDeadlineMillis { get; }

        public long RecoveryGeneration { get; }

        public AiSleeveBloomDrawKind? SleeveDrawKind { get; }

        public TileId? SleeveFirstTileId { get; }

        public TileId? SleeveSecondTileId { get; }

        public TileId? SleeveThirdTileId { get; }

        public AiBeamSwapWindow? BeamWindow { get; }

        public TileId? BeamDrawnTileId { get; }

        public IReadOnlyList<TileId> MarkableHandTileIds => _markableHandTileIds;

        public IReadOnlyList<AiTileMarkGroupFacts> ActiveTileMarkGroups => _activeTileMarkGroups;

        public IReadOnlyList<TileId> BeamEligibleHandTileIds => _beamEligibleHandTileIds;

        public IReadOnlyList<AiBeamSwapCandidateFacts> BeamCandidates => _beamCandidates;

        private static ReadOnlyCollection<TileId> CopyTiles(IReadOnlyList<TileId>? tiles)
        {
            if (tiles == null || tiles.Count == 0)
            {
                return new ReadOnlyCollection<TileId>(Array.Empty<TileId>());
            }

            var copy = new TileId[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                copy[i] = tiles[i];
            }

            return new ReadOnlyCollection<TileId>(copy);
        }

        private static ReadOnlyCollection<AiTileMarkGroupFacts> CopyGroups(IReadOnlyList<AiTileMarkGroupFacts>? groups)
        {
            if (groups == null || groups.Count == 0)
            {
                return new ReadOnlyCollection<AiTileMarkGroupFacts>(Array.Empty<AiTileMarkGroupFacts>());
            }

            var copy = new AiTileMarkGroupFacts[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                AiTileMarkGroupFacts group = groups[i] ??
                    throw new ArgumentException("Tile-mark groups cannot contain null.", nameof(groups));
                copy[i] = group.DeepCopy();
            }

            return new ReadOnlyCollection<AiTileMarkGroupFacts>(copy);
        }

        private static ReadOnlyCollection<AiBeamSwapCandidateFacts> CopyBeamCandidates(
            IReadOnlyList<AiBeamSwapCandidateFacts>? candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return new ReadOnlyCollection<AiBeamSwapCandidateFacts>(Array.Empty<AiBeamSwapCandidateFacts>());
            }

            var copy = new AiBeamSwapCandidateFacts[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                AiBeamSwapCandidateFacts candidate = candidates[i] ??
                    throw new ArgumentException("Beam candidates cannot contain null.", nameof(candidates));
                copy[i] = candidate.DeepCopy();
            }

            return new ReadOnlyCollection<AiBeamSwapCandidateFacts>(copy);
        }
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
            long spendable = (long)opportunity.CheatPoints - _policy.ReservePoints;
            if (spendable < 0)
            {
                spendable = 0;
            }

            var eligible = new List<AiCheatOption>(opportunity.Options.Count);
            for (int i = 0; i < opportunity.Options.Count; i++)
            {
                AiCheatOption option = opportunity.Options[i];
                if (option.Cost > 0)
                {
                    long afterSpend = (long)spent + option.Cost;
                    if (afterSpend > _policy.MaxSpendPerRound ||
                        (long)option.Cost > spendable)
                    {
                        continue;
                    }
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

        /// <summary>
        /// Rehydrates the per-round spend from authoritative evidence (for example the cheat
        /// ledger after a local save/reopen). A different round starts a fresh budget; the same
        /// round keeps the supplied spend so a reconstructed driver cannot spend it again.
        /// </summary>
        public void RestoreRoundSpend(string roundId, int seat, int spentPoints)
        {
            if (roundId == null)
            {
                throw new ArgumentNullException(nameof(roundId));
            }

            AiGuard.RequireText(roundId, nameof(roundId));
            AiGuard.CheckSeat(seat, nameof(seat));
            if (spentPoints < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spentPoints),
                    spentPoints,
                    "Spent points cannot be negative.");
            }

            if (!string.Equals(_budgetRoundId, roundId, StringComparison.Ordinal))
            {
                _budgetRoundId = roundId;
                Array.Clear(_spentBySeat, 0, _spentBySeat.Length);
            }

            _spentBySeat[seat] = Math.Min(spentPoints, _policy.MaxSpendPerRound);
        }

        public override string ToString() =>
            "RandomCheatBasicAiStrategy/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(CultureInfo.InvariantCulture) +
            "/cheats=" + (_policy.Enabled ? "enabled" : "disabled");
    }
}

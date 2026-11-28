using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.Ai
{
    /// <summary>
    /// Optional own-seat &amp; public-table visible context supplied by a host (such as
    /// <c>LocalTableDriver</c>) when scoring cheat decisions. Contains strictly the viewing
    /// seat's own hand, own character id, and public table facts; never wall order, dead
    /// wall tiles, opponent hands, or unmarked slot tiles.
    /// </summary>
    public sealed class ScoredCheatVisibleContext
    {
        private readonly ReadOnlyCollection<TileId> _ownHandTileIds;
        private readonly ReadOnlyCollection<AiVisibleMeld> _ownMelds;
        private readonly ReadOnlyCollection<AiVisibleMeld> _visibleMelds;
        private readonly ReadOnlyCollection<AiVisibleDiscard> _visibleDiscards;
        private readonly ReadOnlyCollection<TileId> _visibleDoraIndicators;
        private readonly ReadOnlyCollection<long> _scores;

        public ScoredCheatVisibleContext(
            IReadOnlyList<TileId> ownHandTileIds,
            TileId? ownDrawnTileId = null,
            IReadOnlyList<AiVisibleMeld>? ownMelds = null,
            IReadOnlyList<AiVisibleMeld>? visibleMelds = null,
            IReadOnlyList<AiVisibleDiscard>? visibleDiscards = null,
            IReadOnlyList<TileId>? visibleDoraIndicators = null,
            IReadOnlyList<long>? scores = null,
            bool isRiichi = false,
            int remainingWallCount = 0,
            int dealerSeat = 0,
            int roundWind = 1,
            string? characterId = null)
        {
            if (ownHandTileIds == null)
            {
                throw new ArgumentNullException(nameof(ownHandTileIds));
            }

            var handCopy = new TileId[ownHandTileIds.Count];
            for (int i = 0; i < ownHandTileIds.Count; i++)
            {
                handCopy[i] = ownHandTileIds[i];
            }

            _ownHandTileIds = new ReadOnlyCollection<TileId>(handCopy);
            OwnDrawnTileId = ownDrawnTileId;
            _ownMelds = CopyList(ownMelds);
            _visibleMelds = CopyList(visibleMelds);
            _visibleDiscards = CopyList(visibleDiscards);
            _visibleDoraIndicators = CopyList(visibleDoraIndicators);
            _scores = CopyList(scores);
            IsRiichi = isRiichi;
            RemainingWallCount = remainingWallCount < 0 ? 0 : remainingWallCount;
            DealerSeat = dealerSeat;
            RoundWind = roundWind;
            CharacterId = string.IsNullOrWhiteSpace(characterId) ? PlayerCharacterIds.Player : characterId!;
        }

        public IReadOnlyList<TileId> OwnHandTileIds => _ownHandTileIds;
        public TileId? OwnDrawnTileId { get; }
        public IReadOnlyList<AiVisibleMeld> OwnMelds => _ownMelds;
        public IReadOnlyList<AiVisibleMeld> VisibleMelds => _visibleMelds;
        public IReadOnlyList<AiVisibleDiscard> VisibleDiscards => _visibleDiscards;
        public IReadOnlyList<TileId> VisibleDoraIndicators => _visibleDoraIndicators;
        public IReadOnlyList<long> Scores => _scores;
        public bool IsRiichi { get; }
        public int RemainingWallCount { get; }
        public int DealerSeat { get; }
        public int RoundWind { get; }
        public string CharacterId { get; }

        private static ReadOnlyCollection<T> CopyList<T>(IReadOnlyList<T>? source)
        {
            if (source == null || source.Count == 0)
            {
                return new ReadOnlyCollection<T>(Array.Empty<T>());
            }

            var copy = new T[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                copy[i] = source[i];
            }

            return new ReadOnlyCollection<T>(copy);
        }
    }

    /// <summary>
    /// Diagnostic evaluation record for one authorized cheat option, sorted in the
    /// strategy's deterministic preference order.
    /// </summary>
    public sealed class ScoredCheatOptionEvaluation
    {
        internal ScoredCheatOptionEvaluation(
            AiCheatOption option,
            bool isEligible,
            string filterReason,
            double score,
            double expectedOrActualGain,
            double costPenalty,
            double riskPenalty,
            AiRisk risk,
            int shantenBefore,
            int shantenAfter,
            double valueDelta)
        {
            Option = option;
            IsEligible = isEligible;
            FilterReason = filterReason;
            Score = score;
            ExpectedOrActualGain = expectedOrActualGain;
            CostPenalty = costPenalty;
            RiskPenalty = riskPenalty;
            Risk = risk;
            ShantenBefore = shantenBefore;
            ShantenAfter = shantenAfter;
            ValueDelta = valueDelta;
        }

        public AiCheatOption Option { get; }
        public string OptionId => Option.OptionId;
        public string CapabilityId => Option.CapabilityId;
        public int Cost => Option.Cost;
        public bool IsSafeDefault => Option.IsSafeDefault;
        public bool IsOfferChoice => Option.IsOfferChoice;
        public bool IsEligible { get; }
        public string FilterReason { get; }
        public double Score { get; }
        public double ExpectedOrActualGain { get; }
        public double CostPenalty { get; }
        public double RiskPenalty { get; }
        public AiRisk Risk { get; }
        public int ShantenBefore { get; }
        public int ShantenAfter { get; }
        public int ShantenImprovement => ShantenBefore - ShantenAfter;
        public double ValueDelta { get; }
    }

    /// <summary>
    /// M1-04 scored cheat AI strategy:
    /// <list type="bullet">
    /// <item>Ordinary actions delegate unchanged to <see cref="BasicAiStrategy"/>.</item>
    /// <item>Cheat decisions strictly separate optional paid activation (<c>IsOfferChoice == false</c>,
    /// skips unless net score &gt; 0 and policy/budget/reserve gates pass) from already-paid offer
    /// resolution (<c>IsOfferChoice == true</c>, never skips and never blocked by budget/toggles).</item>
    /// <item>Activation scores expected benefit using only visible hand/table counts (no hidden wall
    /// or opponent hand peeking); paid offers score actual visible candidates (including marked
    /// beam-swap slots and red-five/dora/shanten gains while keeping unmarked slots opaque).</item>
    /// <item>Deterministic total tie-break order: Score desc -&gt; IsSafeDefault -&gt; Cost asc -&gt;
    /// Risk.Penalty asc -&gt; ShantenImprovement desc -&gt; ValueDelta desc -&gt; Capability priority -&gt;
    /// TargetMarked -&gt; TargetSlot -&gt; TileIds asc -&gt; OptionId ordinal asc.</item>
    /// </list>
    /// </summary>
    public sealed class ScoredCheatBasicAiStrategy : IAiStrategy
    {
        public const string AlgorithmIdValue = AiProfileIds.ScoredCheatBasic;
        public const int AlgorithmVersionValue = 1;

        private const string CapabilityTileMark = "tile_mark";
        private const string CapabilitySleeveBloom = "sleeve_bloom";
        private const string CapabilityBeamSwap = "beam_swap";

        private readonly BasicAiStrategy _basic;
        private readonly CharacterAiProfile _profile;
        private readonly AiCheatPolicy _policy;
        private readonly CharacterStyle _style;
        private readonly int[] _spentBySeat = new int[4];
        private string? _budgetRoundId;

        public ScoredCheatBasicAiStrategy(
            AiCheatPolicy? policy = null,
            CharacterStyle? style = null,
            BasicAiStrategy? basic = null)
            : this(
                new CharacterAiProfile(
                    AlgorithmIdValue,
                    AlgorithmIdValue,
                    style ?? CharacterStyle.Neutral,
                    policy ?? new AiCheatPolicy()),
                basic)
        {
        }

        public ScoredCheatBasicAiStrategy(
            CharacterAiProfile profile,
            BasicAiStrategy? basic = null)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _policy = _profile.CheatPolicy;
            _style = _profile.Style;
            _basic = basic ?? new BasicAiStrategy();
        }

        public string AlgorithmId => AlgorithmIdValue;

        public int AlgorithmVersion => AlgorithmVersionValue;

        public CharacterAiProfile Profile => _profile;

        public AiCheatPolicy CheatPolicy => _policy;

        public CharacterStyle Style => _style;

        /// <summary>Ordinary actions are delegated unchanged to <see cref="BasicAiStrategy"/>.</summary>
        public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken)
        {
            return _basic.Decide(observation, cancellationToken);
        }

        /// <summary>
        /// Rehydrates per-round spend from authoritative ledger (<c>GetAiCheatSpentThisRound</c>).
        /// A new <paramref name="roundId"/> clears prior round counters naturally.
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

        /// <summary>
        /// Chooses an authorized cheat option id, or null to skip an optional activation.
        /// For <c>IsOfferChoice == true</c>, never returns null when at least one option exists.
        /// </summary>
        public string? ChooseCheat(
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext = null)
        {
            if (opportunity == null)
            {
                throw new ArgumentNullException(nameof(opportunity));
            }

            if (!string.Equals(_budgetRoundId, opportunity.RoundId, StringComparison.Ordinal))
            {
                _budgetRoundId = opportunity.RoundId;
                Array.Clear(_spentBySeat, 0, _spentBySeat.Length);
            }

            if (opportunity.Options.Count == 0)
            {
                return null;
            }

            if (!opportunity.IsOfferChoice && !_policy.Enabled)
            {
                return null;
            }

            IReadOnlyList<ScoredCheatOptionEvaluation> ranked = EvaluateCheatOptionsCore(
                opportunity,
                visibleContext);

            ScoredCheatOptionEvaluation? best = null;
            for (int i = 0; i < ranked.Count; i++)
            {
                if (ranked[i].IsEligible)
                {
                    best = ranked[i];
                    break;
                }
            }

            if (best == null)
            {
                return null;
            }

            if (!opportunity.IsOfferChoice && best.Score <= 0.0)
            {
                return null;
            }

            if (best.Cost > 0)
            {
                _spentBySeat[opportunity.Seat] = _spentBySeat[opportunity.Seat] + best.Cost;
            }

            return best.OptionId;
        }

        /// <summary>
        /// Returns all options in <paramref name="opportunity"/> scored and ordered by the
        /// strategy's deterministic preference order (eligible options first, then ineligible).
        /// Does not mutate per-round spend counters.
        /// </summary>
        public IReadOnlyList<ScoredCheatOptionEvaluation> EvaluateCheatOptions(
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext = null)
        {
            if (opportunity == null)
            {
                throw new ArgumentNullException(nameof(opportunity));
            }

            return EvaluateCheatOptionsCore(opportunity, visibleContext);
        }

        public override string ToString() =>
            "ScoredCheatBasicAiStrategy/profile=" + _profile.ProfileId +
            "/cheats=" + (_policy.Enabled ? "enabled" : "disabled");

        private IReadOnlyList<ScoredCheatOptionEvaluation> EvaluateCheatOptionsCore(
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext)
        {
            int spentThisRound = string.Equals(_budgetRoundId, opportunity.RoundId, StringComparison.Ordinal)
                ? _spentBySeat[opportunity.Seat]
                : 0;
            long spendable = Math.Max(0L, (long)opportunity.CheatPoints - _policy.ReservePoints);
            long remainingRoundBudget = Math.Max(0L, (long)_policy.MaxSpendPerRound - spentThisRound);
            long effectiveBudget = Math.Min(spendable, remainingRoundBudget);

            List<TileId> visibleHand = CollectVisibleOwnHand(opportunity, visibleContext);
            int[] visibleTypeCounts = BuildVisibleTypeCounts(visibleHand, opportunity, visibleContext);
            int currentShanten = visibleHand.Count > 0
                ? AiShantenCalculator.CalculateBestPostDiscardShanten(visibleHand)
                : 8;

            double riskWeight = (1.5 - _policy.RiskTolerance) * (0.7 + 0.6 * _style.Defense);
            double costScale = 1.0 * (1.3 - 0.6 * _style.CheatAggression);

            var evaluations = new List<ScoredCheatOptionEvaluation>(opportunity.Options.Count);
            for (int i = 0; i < opportunity.Options.Count; i++)
            {
                AiCheatOption option = opportunity.Options[i];
                if (!TryCheckEligibility(option, opportunity, spentThisRound, spendable, out string filterReason))
                {
                    evaluations.Add(new ScoredCheatOptionEvaluation(
                        option,
                        isEligible: false,
                        filterReason: filterReason,
                        score: double.NegativeInfinity,
                        expectedOrActualGain: 0.0,
                        costPenalty: 0.0,
                        riskPenalty: 0.0,
                        risk: new AiRisk(),
                        shantenBefore: currentShanten,
                        shantenAfter: currentShanten,
                        valueDelta: 0.0));
                    continue;
                }

                ScoredCheatOptionEvaluation eval = EvaluateSingleOption(
                    option,
                    opportunity,
                    visibleContext,
                    visibleHand,
                    visibleTypeCounts,
                    currentShanten,
                    effectiveBudget,
                    riskWeight,
                    costScale);
                evaluations.Add(eval);
            }

            evaluations.Sort(CompareEvaluations);
            return evaluations.AsReadOnly();
        }

        private bool TryCheckEligibility(
            AiCheatOption option,
            AiCheatOpportunity opportunity,
            int spentThisRound,
            long spendable,
            out string filterReason)
        {
            // Already-paid offers must NEVER be filtered by policy toggles, master switch,
            // reserve points, or round spend cap.
            if (opportunity.IsOfferChoice)
            {
                filterReason = string.Empty;
                return true;
            }

            if (!_policy.Enabled)
            {
                filterReason = "POLICY_DISABLED";
                return false;
            }

            if (!string.Equals(option.CapabilityId, "none", StringComparison.Ordinal) &&
                !_policy.Allows(option.CapabilityId))
            {
                filterReason = "CAPABILITY_DISABLED";
                return false;
            }

            if (option.Cost > 0)
            {
                if ((long)option.Cost > opportunity.CheatPoints)
                {
                    filterReason = "INSUFFICIENT_POINTS";
                    return false;
                }

                if ((long)option.Cost > spendable)
                {
                    filterReason = "BELOW_RESERVE";
                    return false;
                }

                if ((long)spentThisRound + option.Cost > _policy.MaxSpendPerRound)
                {
                    filterReason = "ROUND_BUDGET_EXCEEDED";
                    return false;
                }
            }

            filterReason = string.Empty;
            return true;
        }

        private ScoredCheatOptionEvaluation EvaluateSingleOption(
            AiCheatOption option,
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext,
            List<TileId> visibleHand,
            int[] visibleTypeCounts,
            int currentShanten,
            long effectiveBudget,
            double riskWeight,
            double costScale)
        {
            if (opportunity.IsOfferChoice)
            {
                if (string.Equals(option.CapabilityId, CapabilitySleeveBloom, StringComparison.Ordinal))
                {
                    return EvaluateSleeveOfferChoice(
                        option,
                        opportunity,
                        visibleContext,
                        visibleHand,
                        currentShanten,
                        riskWeight);
                }

                if (string.Equals(option.CapabilityId, CapabilityBeamSwap, StringComparison.Ordinal))
                {
                    return EvaluateBeamOfferChoice(
                        option,
                        visibleContext,
                        visibleHand,
                        visibleTypeCounts,
                        currentShanten,
                        riskWeight);
                }
            }

            if (string.Equals(option.CapabilityId, CapabilityTileMark, StringComparison.Ordinal))
            {
                return EvaluateTileMarkActivation(
                    option,
                    opportunity,
                    visibleContext,
                    visibleHand,
                    currentShanten,
                    effectiveBudget,
                    riskWeight,
                    costScale);
            }

            if (string.Equals(option.CapabilityId, CapabilitySleeveBloom, StringComparison.Ordinal))
            {
                return EvaluateSleeveActivation(
                    option,
                    opportunity,
                    visibleContext,
                    visibleHand,
                    visibleTypeCounts,
                    currentShanten,
                    effectiveBudget,
                    riskWeight,
                    costScale);
            }

            if (string.Equals(option.CapabilityId, CapabilityBeamSwap, StringComparison.Ordinal))
            {
                return EvaluateBeamActivation(
                    option,
                    opportunity,
                    visibleContext,
                    visibleHand,
                    visibleTypeCounts,
                    currentShanten,
                    effectiveBudget,
                    riskWeight,
                    costScale);
            }

            // Unknown capability or "none": zero score, safe fallback if marked safe default.
            return new ScoredCheatOptionEvaluation(
                option,
                isEligible: true,
                filterReason: string.Empty,
                score: 0.0,
                expectedOrActualGain: 0.0,
                costPenalty: ComputeCostPenalty(option.Cost, effectiveBudget, costScale),
                riskPenalty: 0.0,
                risk: new AiRisk(),
                shantenBefore: currentShanten,
                shantenAfter: currentShanten,
                valueDelta: 0.0);
        }

        private ScoredCheatOptionEvaluation EvaluateTileMarkActivation(
            AiCheatOption option,
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext,
            List<TileId> visibleHand,
            int currentShanten,
            long effectiveBudget,
            double riskWeight,
            double costScale)
        {
            int activeGroups = opportunity.ActiveTileMarkGroups.Count;
            double valueSum = 0.0;
            for (int i = 0; i < option.TileIds.Count; i++)
            {
                TileId tile = option.TileIds[i];
                double val = visibleHand.Count > 0
                    ? AiCheatHeuristics.TileValue(tile, visibleHand)
                    : 0.0;
                val += RedAndDoraBonus(tile, visibleContext);
                valueSum += val;
            }

            double earlyGroupBonus = activeGroups == 0 ? 2.5 : -2.5 * activeGroups;
            double expectedGain = 4.0 + 0.15 * valueSum + earlyGroupBonus;
            double costPenalty = ComputeCostPenalty(option.Cost, effectiveBudget, costScale) * 1.4;
            var risk = new AiRisk(
                exposure: Math.Min(1.0, 0.10 + 0.05 * activeGroups),
                suspicion: Math.Min(1.0, 0.08 + 0.04 * activeGroups));
            double riskPenalty = risk.Penalty * 10.0 * riskWeight;
            double netScore = expectedGain - costPenalty - riskPenalty;

            return new ScoredCheatOptionEvaluation(
                option,
                isEligible: true,
                filterReason: string.Empty,
                score: netScore,
                expectedOrActualGain: expectedGain,
                costPenalty: costPenalty,
                riskPenalty: riskPenalty,
                risk: risk,
                shantenBefore: currentShanten,
                shantenAfter: currentShanten,
                valueDelta: valueSum);
        }

        private ScoredCheatOptionEvaluation EvaluateSleeveActivation(
            AiCheatOption option,
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext,
            List<TileId> visibleHand,
            int[] visibleTypeCounts,
            int currentShanten,
            long effectiveBudget,
            double riskWeight,
            double costScale)
        {
            TileId? drawnTile = option.TileIds.Count > 0
                ? option.TileIds[0]
                : (visibleContext?.OwnDrawnTileId ?? opportunity.SleeveFirstTileId);
            if (!drawnTile.HasValue || visibleHand.Count == 0)
            {
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: -10.0,
                    expectedOrActualGain: 0.0,
                    costPenalty: ComputeCostPenalty(option.Cost, effectiveBudget, costScale),
                    riskPenalty: 0.0,
                    risk: new AiRisk(0.25, 0.20),
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            // Already winning (Tsumo available): never waste points replacing the winning draw!
            if (currentShanten < 0)
            {
                var winRisk = new AiRisk(0.25, 0.20);
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: -100.0,
                    expectedOrActualGain: -100.0,
                    costPenalty: ComputeCostPenalty(option.Cost, effectiveBudget, costScale),
                    riskPenalty: winRisk.Penalty * 18.0 * riskWeight,
                    risk: winRisk,
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            List<TileId> handWithoutDrawn = RemoveTileOnce(visibleHand, drawnTile.Value);
            double drawnValue = AiCheatHeuristics.TileValue(drawnTile.Value, visibleHand) +
                                RedAndDoraBonus(drawnTile.Value, visibleContext);
            double expectedGain = ComputeExpectedReplacementGain(
                handWithoutDrawn,
                visibleHand,
                visibleTypeCounts,
                currentShanten,
                drawnValue,
                visibleContext);

            // Octopus has a 10% chance of a 3rd candidate on normal draws only, slightly boosting EV.
            // Null or Rinshan draw kind must never receive the normal-draw 3rd-card bonus.
            bool isOctopusNormal = string.Equals(
                    visibleContext?.CharacterId,
                    PlayerCharacterIds.Octopus,
                    StringComparison.Ordinal) &&
                opportunity.SleeveDrawKind == AiSleeveBloomDrawKind.Normal;
            if (isOctopusNormal)
            {
                expectedGain *= 1.15;
            }

            // Style conditioning: higher aggression / tenpai proximity increases willingness.
            double readinessMultiplier = currentShanten == 0
                ? 1.50
                : currentShanten == 1
                    ? 1.25
                    : currentShanten >= 3
                        ? 0.55
                        : 0.95;
            expectedGain *= readinessMultiplier * (0.85 + 0.30 * _style.Aggression);

            double costPenalty = ComputeCostPenalty(option.Cost, effectiveBudget, costScale);
            var risk = new AiRisk(
                exposure: opportunity.SleeveDrawKind == AiSleeveBloomDrawKind.Rinshan ? 0.30 : 0.22,
                suspicion: 0.20);
            double riskPenalty = risk.Penalty * 10.0 * riskWeight;
            double netScore = expectedGain - costPenalty - riskPenalty;

            return new ScoredCheatOptionEvaluation(
                option,
                isEligible: true,
                filterReason: string.Empty,
                score: netScore,
                expectedOrActualGain: expectedGain,
                costPenalty: costPenalty,
                riskPenalty: riskPenalty,
                risk: risk,
                shantenBefore: currentShanten,
                shantenAfter: currentShanten,
                valueDelta: expectedGain);
        }

        private ScoredCheatOptionEvaluation EvaluateBeamActivation(
            AiCheatOption option,
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext,
            List<TileId> visibleHand,
            int[] visibleTypeCounts,
            int currentShanten,
            long effectiveBudget,
            double riskWeight,
            double costScale)
        {
            if (visibleHand.Count <= 1)
            {
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: -10.0,
                    expectedOrActualGain: 0.0,
                    costPenalty: ComputeCostPenalty(option.Cost, effectiveBudget, costScale),
                    riskPenalty: 0.0,
                    risk: new AiRisk(0.35, 0.30),
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            if (currentShanten < 0)
            {
                var winRisk = new AiRisk(0.35, 0.30);
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: -100.0,
                    expectedOrActualGain: -100.0,
                    costPenalty: ComputeCostPenalty(option.Cost, effectiveBudget, costScale),
                    riskPenalty: winRisk.Penalty * 20.0 * riskWeight,
                    risk: winRisk,
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            TileId? drawn = visibleContext?.OwnDrawnTileId ?? opportunity.BeamDrawnTileId;
            List<TileId> eligibleHand = CollectBeamEligibleHand(visibleHand, drawn);
            if (eligibleHand.Count == 0)
            {
                eligibleHand = visibleHand;
            }

            IReadOnlyList<TileId> worstList = AiCheatHeuristics.LeastValuableHandTiles(eligibleHand, 1);
            TileId worstTile = worstList[0];
            double worstValue = AiCheatHeuristics.TileValue(worstTile, visibleHand) +
                                RedAndDoraBonus(worstTile, visibleContext);
            List<TileId> handWithoutWorst = RemoveTileOnce(visibleHand, worstTile);

            double singleSlotEv = ComputeExpectedReplacementGain(
                handWithoutWorst,
                visibleHand,
                visibleTypeCounts,
                currentShanten,
                worstValue,
                visibleContext);

            // BeamSwap gives multiple wall slots (up to 8) + keep-original floor, improving odds.
            double expectedGain = singleSlotEv * 1.35;
            double readinessMultiplier = currentShanten == 0
                ? 1.40
                : currentShanten == 1
                    ? 1.20
                    : currentShanten >= 3
                        ? 0.55
                        : 0.90;
            expectedGain *= readinessMultiplier * (0.85 + 0.30 * _style.Aggression);

            double costPenalty = ComputeCostPenalty(option.Cost, effectiveBudget, costScale);
            var risk = new AiRisk(exposure: 0.35, suspicion: 0.30);
            double riskPenalty = risk.Penalty * 11.0 * riskWeight;
            double netScore = expectedGain - costPenalty - riskPenalty;

            return new ScoredCheatOptionEvaluation(
                option,
                isEligible: true,
                filterReason: string.Empty,
                score: netScore,
                expectedOrActualGain: expectedGain,
                costPenalty: costPenalty,
                riskPenalty: riskPenalty,
                risk: risk,
                shantenBefore: currentShanten,
                shantenAfter: currentShanten,
                valueDelta: expectedGain);
        }

        private ScoredCheatOptionEvaluation EvaluateSleeveOfferChoice(
            AiCheatOption option,
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext,
            List<TileId> visibleHand,
            int currentShanten,
            double riskWeight)
        {
            TileId? candidateTile = option.TileIds.Count > 0
                ? option.TileIds[0]
                : (option.IsSafeDefault ? opportunity.SleeveFirstTileId : null);
            if (!candidateTile.HasValue)
            {
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: option.IsSafeDefault ? 0.0 : -1000.0,
                    expectedOrActualGain: 0.0,
                    costPenalty: 0.0,
                    riskPenalty: 0.0,
                    risk: new AiRisk(),
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            TileId? firstTile = opportunity.SleeveFirstTileId ?? visibleContext?.OwnDrawnTileId;
            List<TileId> candidateHand;
            if (visibleHand.Count > 0 && firstTile.HasValue)
            {
                candidateHand = RemoveTileOnce(visibleHand, firstTile.Value);
                candidateHand.Add(candidateTile.Value);
            }
            else if (visibleHand.Count > 0)
            {
                candidateHand = new List<TileId>(visibleHand) { candidateTile.Value };
            }
            else
            {
                candidateHand = new List<TileId> { candidateTile.Value };
            }

            int candShanten = AiShantenCalculator.CalculateBestPostDiscardShanten(candidateHand);
            double firstVal = firstTile.HasValue && visibleHand.Count > 0
                ? AiCheatHeuristics.TileValue(firstTile.Value, visibleHand) + RedAndDoraBonus(firstTile.Value, visibleContext)
                : 0.0;
            double candVal = AiCheatHeuristics.TileValue(candidateTile.Value, candidateHand) +
                             RedAndDoraBonus(candidateTile.Value, visibleContext) +
                             FlushAffinityBonus(candidateTile.Value, candidateHand);

            double shantenScore = -candShanten * 100.0 + (candShanten < 0 ? 200.0 : 0.0);
            double valueDelta = candVal - firstVal;

            AiRisk risk;
            if (option.IsSafeDefault || string.Equals(option.OptionId, "sleeve-first", StringComparison.Ordinal))
            {
                risk = new AiRisk(0.0, 0.0);
            }
            else if (string.Equals(option.OptionId, "sleeve-third", StringComparison.Ordinal))
            {
                risk = new AiRisk(0.35, 0.25);
            }
            else
            {
                risk = new AiRisk(0.25, 0.20);
            }

            // Modest risk deduction in paid-offer choice: never overrides genuine shanten/tile gains,
            // but cleanly prefers IsSafeDefault (sleeve-first) when candidate values tie.
            double riskPenalty = risk.Penalty * 1.5 * riskWeight;
            double score = shantenScore + candVal - riskPenalty;

            return new ScoredCheatOptionEvaluation(
                option,
                isEligible: true,
                filterReason: string.Empty,
                score: score,
                expectedOrActualGain: shantenScore + candVal,
                costPenalty: 0.0,
                riskPenalty: riskPenalty,
                risk: risk,
                shantenBefore: currentShanten,
                shantenAfter: candShanten,
                valueDelta: valueDelta);
        }

        private ScoredCheatOptionEvaluation EvaluateBeamOfferChoice(
            AiCheatOption option,
            ScoredCheatVisibleContext? visibleContext,
            List<TileId> visibleHand,
            int[] visibleTypeCounts,
            int currentShanten,
            double riskWeight)
        {
            if (option.IsSafeDefault || option.TileIds.Count == 0)
            {
                double baseScore = visibleHand.Count > 0 ? (-currentShanten * 100.0 + (currentShanten < 0 ? 200.0 : 0.0)) : 0.0;
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: baseScore,
                    expectedOrActualGain: baseScore,
                    costPenalty: 0.0,
                    riskPenalty: 0.0,
                    risk: new AiRisk(0.0, 0.0),
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            TileId giveTile = option.TileIds[0];
            double giveValue = visibleHand.Count > 0
                ? AiCheatHeuristics.TileValue(giveTile, visibleHand) +
                  RedAndDoraBonus(giveTile, visibleContext) +
                  FlushAffinityBonus(giveTile, visibleHand)
                : 0.0;
            List<TileId> handWithoutGive = visibleHand.Count > 0
                ? RemoveTileOnce(visibleHand, giveTile)
                : new List<TileId>();

            double slotExposure = option.TargetSlot.HasValue &&
                                  option.TargetSlot.Value.Kind != AiBeamSwapSlotKind.WallSlot
                ? 0.40
                : 0.30;
            var risk = new AiRisk(slotExposure, 0.25);

            // Marked slot: exact target tile is authorized & revealed!
            if (option.TargetMarked && option.TargetTileId.HasValue)
            {
                TileId takeTile = option.TargetTileId.Value;
                var newHand = new List<TileId>(handWithoutGive) { takeTile };
                int newShanten = AiShantenCalculator.CalculateBestPostDiscardShanten(newHand);
                double takeValue = AiCheatHeuristics.TileValue(takeTile, newHand) +
                                   RedAndDoraBonus(takeTile, visibleContext) +
                                   FlushAffinityBonus(takeTile, newHand);
                double shantenScore = -newShanten * 100.0 + (newShanten < 0 ? 200.0 : 0.0);
                double valueDelta = takeValue - giveValue;
                double riskPenalty = risk.Penalty * 2.0 * riskWeight;
                double score = shantenScore + valueDelta - riskPenalty;

                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: score,
                    expectedOrActualGain: shantenScore + valueDelta,
                    costPenalty: 0.0,
                    riskPenalty: riskPenalty,
                    risk: risk,
                    shantenBefore: currentShanten,
                    shantenAfter: newShanten,
                    valueDelta: valueDelta);
            }

            // Unmarked slot: target tile is STRICTLY UNKNOWN (TargetTileId == null).
            // Evaluate expected replacement over visible unseen tile distribution only!
            double baseLine = visibleHand.Count > 0
                ? (-currentShanten * 100.0 + (currentShanten < 0 ? 200.0 : 0.0))
                : 0.0;
            if (currentShanten < 0 || visibleHand.Count == 0)
            {
                double winPenalty = risk.Penalty * 8.0 * riskWeight;
                return new ScoredCheatOptionEvaluation(
                    option,
                    isEligible: true,
                    filterReason: string.Empty,
                    score: baseLine - winPenalty - 10.0,
                    expectedOrActualGain: baseLine,
                    costPenalty: 0.0,
                    riskPenalty: winPenalty,
                    risk: risk,
                    shantenBefore: currentShanten,
                    shantenAfter: currentShanten,
                    valueDelta: 0.0);
            }

            double expectedDelta = ComputeExpectedSignedReplacementGain(
                handWithoutGive,
                visibleHand,
                visibleTypeCounts,
                currentShanten,
                giveValue,
                visibleContext);
            double blindRiskPenalty = risk.Penalty * 4.5 * riskWeight;
            double blindScore = baseLine + expectedDelta - blindRiskPenalty;

            return new ScoredCheatOptionEvaluation(
                option,
                isEligible: true,
                filterReason: string.Empty,
                score: blindScore,
                expectedOrActualGain: baseLine + expectedDelta,
                costPenalty: 0.0,
                riskPenalty: blindRiskPenalty,
                risk: risk,
                shantenBefore: currentShanten,
                shantenAfter: currentShanten,
                valueDelta: expectedDelta);
        }

        private double ComputeExpectedReplacementGain(
            List<TileId> handWithoutTile,
            List<TileId> fullHand,
            int[] visibleTypeCounts,
            int currentShanten,
            double replacedTileValue,
            ScoredCheatVisibleContext? visibleContext)
        {
            double weightedGain = 0.0;
            int totalUnseen = 0;

            for (int typeIndex = 0; typeIndex < TileId.TypeCount; typeIndex++)
            {
                int unseen = 4 - visibleTypeCounts[typeIndex];
                if (unseen <= 0)
                {
                    continue;
                }

                totalUnseen += unseen;
                var candidateTile = new TileId(typeIndex * 4);
                handWithoutTile.Add(candidateTile);
                int newShanten = AiShantenCalculator.CalculateBestPostDiscardShanten(handWithoutTile);
                int deltaShanten = currentShanten - newShanten;
                double newVal = AiCheatHeuristics.TileValue(candidateTile, handWithoutTile) +
                                RedAndDoraBonus(candidateTile, visibleContext) +
                                FlushAffinityBonus(candidateTile, handWithoutTile);
                handWithoutTile.RemoveAt(handWithoutTile.Count - 1);

                double gain = (deltaShanten * 90.0) + ((newVal - replacedTileValue) * 1.1);
                if (gain > 0.0)
                {
                    weightedGain += unseen * gain;
                }
            }

            return totalUnseen > 0 ? weightedGain / totalUnseen : 0.0;
        }

        private double ComputeExpectedSignedReplacementGain(
            List<TileId> handWithoutTile,
            List<TileId> fullHand,
            int[] visibleTypeCounts,
            int currentShanten,
            double replacedTileValue,
            ScoredCheatVisibleContext? visibleContext)
        {
            double weightedGain = 0.0;
            int totalUnseen = 0;

            for (int typeIndex = 0; typeIndex < TileId.TypeCount; typeIndex++)
            {
                int unseen = 4 - visibleTypeCounts[typeIndex];
                if (unseen <= 0)
                {
                    continue;
                }

                totalUnseen += unseen;
                var candidateTile = new TileId(typeIndex * 4);
                handWithoutTile.Add(candidateTile);
                int newShanten = AiShantenCalculator.CalculateBestPostDiscardShanten(handWithoutTile);
                int deltaShanten = currentShanten - newShanten;
                double newVal = AiCheatHeuristics.TileValue(candidateTile, handWithoutTile) +
                                RedAndDoraBonus(candidateTile, visibleContext) +
                                FlushAffinityBonus(candidateTile, handWithoutTile);
                handWithoutTile.RemoveAt(handWithoutTile.Count - 1);

                double gain = (deltaShanten * 40.0) + ((newVal - replacedTileValue) * 0.65);
                weightedGain += unseen * gain;
            }

            return totalUnseen > 0 ? weightedGain / totalUnseen : 0.0;
        }

        private double ComputeCostPenalty(int cost, long effectiveBudget, double costScale)
        {
            if (cost <= 0)
            {
                return 0.0;
            }

            double budgetPressure = effectiveBudget <= 0
                ? 2.0
                : 1.0 + 0.5 * (1.0 - Math.Min(1.0, (double)(effectiveBudget - cost) / Math.Max(1.0, effectiveBudget)));
            return cost * 0.65 * budgetPressure * costScale;
        }

        private double RedAndDoraBonus(TileId tile, ScoredCheatVisibleContext? visibleContext)
        {
            double bonus = tile.IsRedFive ? 4.0 : 0.0;
            if (visibleContext != null)
            {
                for (int i = 0; i < visibleContext.VisibleDoraIndicators.Count; i++)
                {
                    if (DoraTypeFromIndicator(visibleContext.VisibleDoraIndicators[i]) == tile.TypeIndex)
                    {
                        bonus += 3.0;
                    }
                }
            }

            return bonus;
        }

        private double FlushAffinityBonus(TileId tile, IReadOnlyList<TileId> hand)
        {
            double delta = _style.FlushTendency - 0.5;
            if (Math.Abs(delta) < 1e-9 || !tile.IsNumberTile || hand.Count == 0)
            {
                return 0.0;
            }

            int sameSuit = 0;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].Suit == tile.Suit)
                {
                    sameSuit++;
                }
            }

            return sameSuit >= 5 ? delta * 8.0 : 0.0;
        }

        private static int DoraTypeFromIndicator(TileId indicator)
        {
            if (indicator.IsNumberTile)
            {
                int suitBase = (int)indicator.Suit * 9;
                int nextRank = (indicator.Rank % 9);
                return suitBase + nextRank;
            }

            if (indicator.Suit == TileSuit.Wind)
            {
                return 27 + ((indicator.TypeIndex - 27 + 1) % 4);
            }

            return 31 + ((indicator.TypeIndex - 31 + 1) % 3);
        }

        private static List<TileId> CollectVisibleOwnHand(
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext)
        {
            var result = new List<TileId>(14);
            var seen = new HashSet<int>();

            void AddUnique(TileId tile)
            {
                if (seen.Add(tile.Value))
                {
                    result.Add(tile);
                }
            }

            if (visibleContext != null)
            {
                for (int i = 0; i < visibleContext.OwnHandTileIds.Count; i++)
                {
                    AddUnique(visibleContext.OwnHandTileIds[i]);
                }

                if (visibleContext.OwnDrawnTileId.HasValue)
                {
                    AddUnique(visibleContext.OwnDrawnTileId.Value);
                }
            }

            if (result.Count > 0)
            {
                return result;
            }

            for (int i = 0; i < opportunity.BeamEligibleHandTileIds.Count; i++)
            {
                AddUnique(opportunity.BeamEligibleHandTileIds[i]);
            }

            if (opportunity.BeamDrawnTileId.HasValue)
            {
                AddUnique(opportunity.BeamDrawnTileId.Value);
            }

            for (int i = 0; i < opportunity.MarkableHandTileIds.Count; i++)
            {
                AddUnique(opportunity.MarkableHandTileIds[i]);
            }

            for (int g = 0; g < opportunity.ActiveTileMarkGroups.Count; g++)
            {
                AiTileMarkGroupFacts group = opportunity.ActiveTileMarkGroups[g];
                for (int t = 0; t < group.Tiles.Count; t++)
                {
                    AiMarkedTileFacts tile = group.Tiles[t];
                    if (tile.Area == AiTileMarkArea.OwnHand && tile.Seat == opportunity.Seat)
                    {
                        AddUnique(new TileId(tile.TileId));
                    }
                }
            }

            if (!opportunity.IsOfferChoice)
            {
                for (int i = 0; i < opportunity.Options.Count; i++)
                {
                    AiCheatOption opt = opportunity.Options[i];
                    if (string.Equals(opt.CapabilityId, CapabilityTileMark, StringComparison.Ordinal) ||
                        string.Equals(opt.CapabilityId, CapabilitySleeveBloom, StringComparison.Ordinal))
                    {
                        for (int j = 0; j < opt.TileIds.Count; j++)
                        {
                            AddUnique(opt.TileIds[j]);
                        }
                    }
                }
            }
            else if (opportunity.SleeveFirstTileId.HasValue)
            {
                AddUnique(opportunity.SleeveFirstTileId.Value);
            }

            return result;
        }

        private static List<TileId> CollectBeamEligibleHand(List<TileId> visibleHand, TileId? drawn)
        {
            var eligible = new List<TileId>(visibleHand.Count);
            for (int i = 0; i < visibleHand.Count; i++)
            {
                if (!drawn.HasValue || visibleHand[i] != drawn.Value)
                {
                    eligible.Add(visibleHand[i]);
                }
            }

            return eligible;
        }

        private static int[] BuildVisibleTypeCounts(
            List<TileId> visibleHand,
            AiCheatOpportunity opportunity,
            ScoredCheatVisibleContext? visibleContext)
        {
            var counts = new int[TileId.TypeCount];
            var seenPhysical = new HashSet<int>();

            void CountTile(TileId tile)
            {
                if (seenPhysical.Add(tile.Value))
                {
                    counts[tile.TypeIndex]++;
                }
            }

            for (int i = 0; i < visibleHand.Count; i++)
            {
                CountTile(visibleHand[i]);
            }

            if (opportunity.SleeveFirstTileId.HasValue)
            {
                CountTile(opportunity.SleeveFirstTileId.Value);
            }

            if (opportunity.SleeveSecondTileId.HasValue)
            {
                CountTile(opportunity.SleeveSecondTileId.Value);
            }

            if (opportunity.SleeveThirdTileId.HasValue)
            {
                CountTile(opportunity.SleeveThirdTileId.Value);
            }

            for (int i = 0; i < opportunity.BeamCandidates.Count; i++)
            {
                AiBeamSwapCandidateFacts cand = opportunity.BeamCandidates[i];
                if (cand.Marked && cand.TileId.HasValue)
                {
                    CountTile(new TileId(cand.TileId.Value));
                }
            }

            for (int g = 0; g < opportunity.ActiveTileMarkGroups.Count; g++)
            {
                AiTileMarkGroupFacts group = opportunity.ActiveTileMarkGroups[g];
                for (int t = 0; t < group.Tiles.Count; t++)
                {
                    AiMarkedTileFacts tile = group.Tiles[t];
                    if (tile.Area != AiTileMarkArea.NotVisible)
                    {
                        CountTile(new TileId(tile.TileId));
                    }
                }
            }

            if (visibleContext != null)
            {
                for (int i = 0; i < visibleContext.OwnMelds.Count; i++)
                {
                    IReadOnlyList<TileId> tiles = visibleContext.OwnMelds[i].Tiles;
                    for (int j = 0; j < tiles.Count; j++)
                    {
                        CountTile(tiles[j]);
                    }
                }

                for (int i = 0; i < visibleContext.VisibleMelds.Count; i++)
                {
                    IReadOnlyList<TileId> tiles = visibleContext.VisibleMelds[i].Tiles;
                    for (int j = 0; j < tiles.Count; j++)
                    {
                        CountTile(tiles[j]);
                    }
                }

                for (int i = 0; i < visibleContext.VisibleDiscards.Count; i++)
                {
                    CountTile(visibleContext.VisibleDiscards[i].Tile);
                }

                for (int i = 0; i < visibleContext.VisibleDoraIndicators.Count; i++)
                {
                    CountTile(visibleContext.VisibleDoraIndicators[i]);
                }
            }

            return counts;
        }

        private static List<TileId> RemoveTileOnce(List<TileId> source, TileId target)
        {
            var copy = new List<TileId>(source.Count);
            bool removed = false;
            for (int i = 0; i < source.Count; i++)
            {
                if (!removed && source[i] == target)
                {
                    removed = true;
                    continue;
                }

                copy.Add(source[i]);
            }

            return copy;
        }

        /// <summary>
        /// Strict total deterministic tie-break order:
        /// 1. Eligible before ineligible;
        /// 2. Higher Score;
        /// 3. IsSafeDefault (true before false);
        /// 4. Lower Cost;
        /// 5. Lower Risk.Penalty;
        /// 6. Higher ShantenImprovement;
        /// 7. Higher ValueDelta;
        /// 8. Capability priority (tile_mark &lt; sleeve_bloom &lt; beam_swap);
        /// 9. TargetMarked (true before false);
        /// 10. TargetSlot (Kind asc, Index asc);
        /// 11. Physical TileIds lexicographical asc;
        /// 12. OptionId ordinal asc.
        /// </summary>
        private static int CompareEvaluations(ScoredCheatOptionEvaluation left, ScoredCheatOptionEvaluation right)
        {
            int eligibleCmp = right.IsEligible.CompareTo(left.IsEligible);
            if (eligibleCmp != 0)
            {
                return eligibleCmp;
            }

            int scoreCmp = right.Score.CompareTo(left.Score);
            if (scoreCmp != 0)
            {
                return scoreCmp;
            }

            int safeCmp = right.IsSafeDefault.CompareTo(left.IsSafeDefault);
            if (safeCmp != 0)
            {
                return safeCmp;
            }

            int costCmp = left.Cost.CompareTo(right.Cost);
            if (costCmp != 0)
            {
                return costCmp;
            }

            int riskCmp = left.Risk.Penalty.CompareTo(right.Risk.Penalty);
            if (riskCmp != 0)
            {
                return riskCmp;
            }

            int shantenCmp = right.ShantenImprovement.CompareTo(left.ShantenImprovement);
            if (shantenCmp != 0)
            {
                return shantenCmp;
            }

            int valueCmp = right.ValueDelta.CompareTo(left.ValueDelta);
            if (valueCmp != 0)
            {
                return valueCmp;
            }

            int capCmp = CapabilityPriority(left.CapabilityId).CompareTo(CapabilityPriority(right.CapabilityId));
            if (capCmp != 0)
            {
                return capCmp;
            }

            int markedCmp = right.Option.TargetMarked.CompareTo(left.Option.TargetMarked);
            if (markedCmp != 0)
            {
                return markedCmp;
            }

            int slotCmp = CompareSlots(left.Option.TargetSlot, right.Option.TargetSlot);
            if (slotCmp != 0)
            {
                return slotCmp;
            }

            int tilesCmp = CompareTileLists(left.Option.TileIds, right.Option.TileIds);
            if (tilesCmp != 0)
            {
                return tilesCmp;
            }

            return string.CompareOrdinal(left.OptionId, right.OptionId);
        }

        private static int CapabilityPriority(string capabilityId)
        {
            if (string.Equals(capabilityId, CapabilityTileMark, StringComparison.Ordinal))
            {
                return 1;
            }

            if (string.Equals(capabilityId, CapabilitySleeveBloom, StringComparison.Ordinal))
            {
                return 2;
            }

            if (string.Equals(capabilityId, CapabilityBeamSwap, StringComparison.Ordinal))
            {
                return 3;
            }

            return 4;
        }

        private static int CompareSlots(AiBeamSwapSlotFacts? left, AiBeamSwapSlotFacts? right)
        {
            if (!left.HasValue && !right.HasValue)
            {
                return 0;
            }

            if (!left.HasValue)
            {
                return -1;
            }

            if (!right.HasValue)
            {
                return 1;
            }

            int kindCmp = left.Value.Kind.CompareTo(right.Value.Kind);
            return kindCmp != 0 ? kindCmp : left.Value.Index.CompareTo(right.Value.Index);
        }

        private static int CompareTileLists(IReadOnlyList<TileId> left, IReadOnlyList<TileId> right)
        {
            int min = Math.Min(left.Count, right.Count);
            for (int i = 0; i < min; i++)
            {
                int cmp = left[i].Value.CompareTo(right[i].Value);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return left.Count.CompareTo(right.Count);
        }
    }
}

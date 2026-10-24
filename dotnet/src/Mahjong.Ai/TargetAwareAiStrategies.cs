using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Port of V4 <c>TargetAwareAi.SuitGoalAi</c> (ENG-016): a pure visible-tile
    /// 清一色 heuristic for one number suit.
    ///
    /// Branch order is the V4 source of record:
    /// <list type="number">
    /// <item>first Ron, then first Tsumo;</item>
    /// <item>a legal discard of the exact physical drawn tile when already in riichi;</item>
    /// <item>a Pon of the target suit when the target score strictly improves, maximum
    /// (score, first hand tile typeIndex, copyIndex), first on a full tie;</item>
    /// <item>a Chi whose complete meld is all target suit and strictly improves, maximum
    /// (score, called typeIndex, copyIndex), first on a full tie;</item>
    /// <item>a discard, maximum (score after removing that physical tile, typeIndex,
    /// copyIndex), first on a full tie;</item>
    /// <item>V4 fallback: BasicStrategy chooses from the complete candidate list; if its
    /// choice is forbidden, the first non-forbidden candidate in the original list order.</item>
    /// </list>
    /// The score is only a ranking heuristic. It is never a rule, fee, legality decision or
    /// authority hash input.
    /// </summary>
    public sealed class SuitGoalAi
    {
        public const string AlgorithmIdValue = "target-aware-suit-goal";
        public const int AlgorithmVersionValue = 1;

        private static readonly BasicAiStrategy Basic = new BasicAiStrategy();

        public SuitGoalAi(TileSuit targetSuit)
        {
            TargetSuit = targetSuit;
        }

        /// <summary>The target number suit. V4 also accepted any <c>Suit</c> value; no extra
        /// restriction is added here, but the strategy is defined for Man/Pin/Sou.</summary>
        public TileSuit TargetSuit { get; }

        /// <summary>
        /// Safe entry point. It verifies that the context belongs to exactly this observation
        /// window and candidate list before using any fact.
        /// </summary>
        public AiStrategicChoice? TryChoose(
            AiObservation observation,
            TargetAwareAiContext context,
            CancellationToken cancellationToken = default)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!context.MatchesObservation(observation))
            {
                throw new TargetAwareAiContextException(
                    "The target-aware context does not belong to this observation window. " +
                    "A context from another decision, state version, control epoch or candidate list cannot be reused.");
            }

            return TryChoose(
                observation.LegalActions,
                context,
                observation.MyHandTileIds,
                observation.MyDrawnTileId,
                observation.MyMelds,
                observation.DealerSeat,
                observation.RoundWind,
                observation.Scores,
                cancellationToken);
        }

        /// <summary>
        /// V4-shaped nullable entry point over raw inputs. It preserves the source's empty-list
        /// semantics: an empty legal list returns null before any decision branch.
        /// </summary>
        public AiStrategicChoice? TryChoose(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            IReadOnlyList<AiVisibleMeld> myMelds,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken = default)
        {
            TargetAwareAiMath.ValidateRawInputs(
                legalActions,
                context,
                myClosedTiles,
                myMelds,
                dealerSeat,
                roundWind,
                scores);
            cancellationToken.ThrowIfCancellationRequested();
            if (legalActions.Count == 0)
            {
                // V4: legalActions.isEmpty() -> null.
                return null;
            }

            context.ValidateCandidates(legalActions);
            var allClosed = TargetAwareAiView.AllClosedTiles(myClosedTiles, myDrawnTile);
            var baseTiles = TargetAwareAiView.BaseTiles(allClosed, myMelds, context.Seat);
            TargetAwareAiView.ValidateMeldFacts(
                context.MeldFacts,
                allClosed,
                baseTiles,
                context.LastDiscard);

            return DecideCore(
                legalActions,
                context,
                myClosedTiles,
                myDrawnTile,
                baseTiles,
                dealerSeat,
                roundWind,
                scores,
                cancellationToken);
        }

        public override string ToString() =>
            "SuitGoalAi/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(CultureInfo.InvariantCulture) +
            "/target=" + TargetSuit.ToString();

        private AiStrategicChoice? DecideCore(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            IReadOnlyList<TileId> baseTiles,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken)
        {
            AiStrategicChoice? win = TargetAwareAiMath.WinOrNull(legalActions, cancellationToken);
            if (win != null)
            {
                return win;
            }

            AiStrategicChoice? tsumogiri = TargetAwareAiMath.RiichiTsumogiri(
                legalActions,
                context.BasicContext.IsRiichi,
                myDrawnTile,
                cancellationToken);
            if (tsumogiri != null)
            {
                return tsumogiri;
            }

            int before = TargetAwareAiMath.SuitScore(baseTiles, TargetSuit);

            // Pon first. Only the target suit and only a strict score improvement.
            CandidateRank? bestPon = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption action = legalActions[i];
                if (action.Kind != LegalActionKind.Pon)
                {
                    continue;
                }

                TargetAwarePonFact fact = TargetAwareAiMath.GetPonFact(context, action);
                TileId? called = context.LastDiscard;
                if (!called.HasValue || called.Value.Suit != TargetSuit)
                {
                    continue;
                }

                int score = TargetAwareAiMath.SuitScore(
                    TargetAwareAiView.AfterPon(baseTiles, fact),
                    TargetSuit);
                if (score <= before)
                {
                    continue;
                }

                TileId first = fact.Tiles[0];
                var rank = new CandidateRank(action, score, first.TypeIndex, first.CopyIndex);
                if (!bestPon.HasValue || rank.IsBetterThan(bestPon.Value))
                {
                    bestPon = rank;
                }
            }

            if (bestPon.HasValue)
            {
                return new AiStrategicChoice(bestPon.Value.Action, bestPon.Value.Score);
            }

            // Chi next. The complete meld must be target suit and strictly improve.
            CandidateRank? bestChi = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption action = legalActions[i];
                if (action.Kind != LegalActionKind.Chi)
                {
                    continue;
                }

                TargetAwareChiFact fact = TargetAwareAiMath.GetChiFact(context, action);
                if (!TargetAwareAiMath.AllTilesHaveSuit(fact.Tiles, TargetSuit))
                {
                    continue;
                }

                int score = TargetAwareAiMath.SuitScore(
                    TargetAwareAiView.AfterChi(baseTiles, fact),
                    TargetSuit);
                if (score <= before)
                {
                    continue;
                }

                var rank = new CandidateRank(
                    action,
                    score,
                    fact.CalledTile.TypeIndex,
                    fact.CalledTile.CopyIndex);
                if (!bestChi.HasValue || rank.IsBetterThan(bestChi.Value))
                {
                    bestChi = rank;
                }
            }

            if (bestChi.HasValue)
            {
                return new AiStrategicChoice(bestChi.Value.Action, bestChi.Value.Score);
            }

            // Discards. Removing a physical tile uses the first equal physical id, never every
            // copy of the logical type.
            CandidateRank? bestDiscard = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption action = legalActions[i];
                if (action.Kind != LegalActionKind.Discard)
                {
                    continue;
                }

                TileId tile = TargetAwareAiMath.RequireTile(action, "Discard");
                int score = TargetAwareAiMath.SuitScore(
                    TargetAwareAiView.RemoveFirst(baseTiles, tile),
                    TargetSuit);
                var rank = new CandidateRank(action, score, tile.TypeIndex, tile.CopyIndex);
                if (!bestDiscard.HasValue || rank.IsBetterThan(bestDiscard.Value))
                {
                    bestDiscard = rank;
                }
            }

            if (bestDiscard.HasValue)
            {
                return new AiStrategicChoice(bestDiscard.Value.Action, bestDiscard.Value.Score);
            }

            bool Forbidden(LegalActionOption action)
            {
                switch (action.Kind)
                {
                    case LegalActionKind.Chi:
                        TargetAwareChiFact chi = TargetAwareAiMath.GetChiFact(context, action);
                        if (!TargetAwareAiMath.AllTilesHaveSuit(chi.Tiles, TargetSuit))
                        {
                            return true;
                        }

                        return TargetAwareAiMath.SuitScore(
                            TargetAwareAiView.AfterChi(baseTiles, chi),
                            TargetSuit) <= before;

                    case LegalActionKind.Pon:
                        TargetAwarePonFact pon = TargetAwareAiMath.GetPonFact(context, action);
                        TileId? called = context.LastDiscard;
                        if (!called.HasValue || called.Value.Suit != TargetSuit)
                        {
                            return true;
                        }

                        return TargetAwareAiMath.SuitScore(
                            TargetAwareAiView.AfterPon(baseTiles, pon),
                            TargetSuit) <= before;

                    case LegalActionKind.Kan:
                        return true;

                    default:
                        return false;
                }
            }

            return TargetAwareAiMath.Fallback(
                Basic,
                legalActions,
                context.BasicContext,
                myClosedTiles,
                myDrawnTile,
                dealerSeat,
                roundWind,
                scores,
                Forbidden,
                cancellationToken);
        }
    }

    /// <summary>
    /// Port of V4 <c>TargetAwareAi.HonorTripletGoalAi</c> (ENG-016): keep and Pon the missing
    /// target honor triplets (大三元/大四喜 style), then rank discards by the honor score.
    ///
    /// V4 constructor rules are preserved: the target set must be non-empty and every type
    /// index must be in 27..33. A Pon is considered only when the public last discard is a
    /// target honor and the visible base (own closed hand plus own public melds) holds fewer
    /// than three copies of that type. The source has no strict-improvement condition on this
    /// Pon branch.
    /// </summary>
    public sealed class HonorTripletGoalAi
    {
        public const string AlgorithmIdValue = "target-aware-honor-triplet-goal";
        public const int AlgorithmVersionValue = 1;

        private static readonly BasicAiStrategy Basic = new BasicAiStrategy();

        private readonly ReadOnlyCollection<int> _requiredHonors;
        private readonly HashSet<int> _requiredLookup;

        public HonorTripletGoalAi(IReadOnlyList<int> requiredHonors)
        {
            if (requiredHonors == null)
            {
                throw new ArgumentNullException(nameof(requiredHonors));
            }

            var lookup = new HashSet<int>();
            for (int i = 0; i < requiredHonors.Count; i++)
            {
                int typeIndex = requiredHonors[i];
                if (typeIndex < 27 || typeIndex > 33)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(requiredHonors),
                        typeIndex,
                        "Target honors must be type indices 27..33.");
                }

                lookup.Add(typeIndex);
            }

            if (lookup.Count == 0)
            {
                throw new ArgumentException(
                    "At least one target honor is required.",
                    nameof(requiredHonors));
            }

            var sorted = new List<int>(lookup);
            sorted.Sort();
            _requiredLookup = lookup;
            _requiredHonors = new ReadOnlyCollection<int>(sorted);
        }

        /// <summary>Sorted, de-duplicated target honor type indices (27..33).</summary>
        public IReadOnlyList<int> RequiredHonors => _requiredHonors;

        public AiStrategicChoice? TryChoose(
            AiObservation observation,
            TargetAwareAiContext context,
            CancellationToken cancellationToken = default)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!context.MatchesObservation(observation))
            {
                throw new TargetAwareAiContextException(
                    "The target-aware context does not belong to this observation window. " +
                    "A context from another decision, state version, control epoch or candidate list cannot be reused.");
            }

            return TryChoose(
                observation.LegalActions,
                context,
                observation.MyHandTileIds,
                observation.MyDrawnTileId,
                observation.MyMelds,
                observation.DealerSeat,
                observation.RoundWind,
                observation.Scores,
                cancellationToken);
        }

        public AiStrategicChoice? TryChoose(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            IReadOnlyList<AiVisibleMeld> myMelds,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken = default)
        {
            TargetAwareAiMath.ValidateRawInputs(
                legalActions,
                context,
                myClosedTiles,
                myMelds,
                dealerSeat,
                roundWind,
                scores);
            cancellationToken.ThrowIfCancellationRequested();
            if (legalActions.Count == 0)
            {
                return null;
            }

            context.ValidateCandidates(legalActions);
            var allClosed = TargetAwareAiView.AllClosedTiles(myClosedTiles, myDrawnTile);
            var baseTiles = TargetAwareAiView.BaseTiles(allClosed, myMelds, context.Seat);
            TargetAwareAiView.ValidateMeldFacts(
                context.MeldFacts,
                allClosed,
                baseTiles,
                context.LastDiscard);

            return DecideCore(
                legalActions,
                context,
                myClosedTiles,
                myDrawnTile,
                baseTiles,
                dealerSeat,
                roundWind,
                scores,
                cancellationToken);
        }

        public override string ToString() =>
            "HonorTripletGoalAi/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(CultureInfo.InvariantCulture) +
            "/honors=" + string.Join(",", _requiredHonors);

        private AiStrategicChoice? DecideCore(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            IReadOnlyList<TileId> baseTiles,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken)
        {
            AiStrategicChoice? win = TargetAwareAiMath.WinOrNull(legalActions, cancellationToken);
            if (win != null)
            {
                return win;
            }

            AiStrategicChoice? tsumogiri = TargetAwareAiMath.RiichiTsumogiri(
                legalActions,
                context.BasicContext.IsRiichi,
                myDrawnTile,
                cancellationToken);
            if (tsumogiri != null)
            {
                return tsumogiri;
            }

            // V4: only a gap in a target honor is worth a Pon. The count is taken from the
            // base (own closed hand plus own public melds), not only from the closed hand.
            TileId? called = context.LastDiscard;
            if (called.HasValue && _requiredLookup.Contains(called.Value.TypeIndex) &&
                TargetAwareAiView.CountType(baseTiles, called.Value.TypeIndex) < 3)
            {
                var withCalled = new List<TileId>(baseTiles.Count + 1);
                for (int i = 0; i < baseTiles.Count; i++)
                {
                    withCalled.Add(baseTiles[i]);
                }

                withCalled.Add(called.Value);
                int score = TargetAwareAiMath.HonorScore(withCalled, _requiredHonors);

                CandidateRank? bestPon = null;
                for (int i = 0; i < legalActions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LegalActionOption action = legalActions[i];
                    if (action.Kind != LegalActionKind.Pon)
                    {
                        continue;
                    }

                    TargetAwarePonFact fact = TargetAwareAiMath.GetPonFact(context, action);
                    TileId first = fact.Tiles[0];
                    var rank = new CandidateRank(action, score, first.TypeIndex, first.CopyIndex);
                    if (!bestPon.HasValue || rank.IsBetterThan(bestPon.Value))
                    {
                        bestPon = rank;
                    }
                }

                if (bestPon.HasValue)
                {
                    return new AiStrategicChoice(bestPon.Value.Action, bestPon.Value.Score);
                }
            }

            CandidateRank? bestDiscard = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption action = legalActions[i];
                if (action.Kind != LegalActionKind.Discard)
                {
                    continue;
                }

                TileId tile = TargetAwareAiMath.RequireTile(action, "Discard");
                int score = TargetAwareAiMath.HonorScore(
                    TargetAwareAiView.RemoveFirst(baseTiles, tile),
                    _requiredHonors);
                var rank = new CandidateRank(action, score, tile.TypeIndex, tile.CopyIndex);
                if (!bestDiscard.HasValue || rank.IsBetterThan(bestDiscard.Value))
                {
                    bestDiscard = rank;
                }
            }

            if (bestDiscard.HasValue)
            {
                return new AiStrategicChoice(bestDiscard.Value.Action, bestDiscard.Value.Score);
            }

            bool Forbidden(LegalActionOption action)
            {
                switch (action.Kind)
                {
                    case LegalActionKind.Chi:
                        return true;

                    case LegalActionKind.Pon:
                        TileId? ponCalled = context.LastDiscard;
                        return !ponCalled.HasValue ||
                               !_requiredLookup.Contains(ponCalled.Value.TypeIndex);

                    case LegalActionKind.Kan:
                        return true;

                    default:
                        return false;
                }
            }

            return TargetAwareAiMath.Fallback(
                Basic,
                legalActions,
                context.BasicContext,
                myClosedTiles,
                myDrawnTile,
                dealerSeat,
                roundWind,
                scores,
                Forbidden,
                cancellationToken);
        }
    }

    /// <summary>
    /// Port of V4 <c>TargetAwareAi.KokushiGoalAi</c> (ENG-016): collect the thirteen kokushi
    /// types, keep one pair, penalise extra copies, discard non-terminals first, skip the nine
    /// terminals abortive draw and forbid Chi/Pon/Kan.
    ///
    /// V4 scored only <c>view.myAllClosedTiles</c>; own public melds are deliberately not part
    /// of the kokushi score. The score is a heuristic only.
    /// </summary>
    public sealed class KokushiGoalAi
    {
        public const string AlgorithmIdValue = "target-aware-kokushi-goal";
        public const int AlgorithmVersionValue = 1;

        private static readonly BasicAiStrategy Basic = new BasicAiStrategy();
        private static readonly int[] KokushiIndexValues =
        {
            0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33
        };

        private static readonly ReadOnlyCollection<int> KokushiIndexList =
            new ReadOnlyCollection<int>(KokushiIndexValues);

        private KokushiGoalAi()
        {
        }

        public static KokushiGoalAi Instance { get; } = new KokushiGoalAi();

        /// <summary>The thirteen V4 kokushi type indices, in source order.</summary>
        public static IReadOnlyList<int> KokushiIndices => KokushiIndexList;

        public AiStrategicChoice? TryChoose(
            AiObservation observation,
            TargetAwareAiContext context,
            CancellationToken cancellationToken = default)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!context.MatchesObservation(observation))
            {
                throw new TargetAwareAiContextException(
                    "The target-aware context does not belong to this observation window. " +
                    "A context from another decision, state version, control epoch or candidate list cannot be reused.");
            }

            return TryChoose(
                observation.LegalActions,
                context,
                observation.MyHandTileIds,
                observation.MyDrawnTileId,
                observation.MyMelds,
                observation.DealerSeat,
                observation.RoundWind,
                observation.Scores,
                cancellationToken);
        }

        public AiStrategicChoice? TryChoose(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            IReadOnlyList<AiVisibleMeld> myMelds,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken = default)
        {
            TargetAwareAiMath.ValidateRawInputs(
                legalActions,
                context,
                myClosedTiles,
                myMelds,
                dealerSeat,
                roundWind,
                scores);
            cancellationToken.ThrowIfCancellationRequested();
            if (legalActions.Count == 0)
            {
                return null;
            }

            context.ValidateCandidates(legalActions);
            var allClosed = TargetAwareAiView.AllClosedTiles(myClosedTiles, myDrawnTile);
            var baseTiles = TargetAwareAiView.BaseTiles(allClosed, myMelds, context.Seat);
            TargetAwareAiView.ValidateMeldFacts(
                context.MeldFacts,
                allClosed,
                baseTiles,
                context.LastDiscard);

            return DecideCore(
                legalActions,
                context,
                myClosedTiles,
                myDrawnTile,
                allClosed,
                dealerSeat,
                roundWind,
                scores,
                cancellationToken);
        }

        public override string ToString() =>
            "KokushiGoalAi/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(CultureInfo.InvariantCulture);

        private AiStrategicChoice? DecideCore(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            IReadOnlyList<TileId> allClosed,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken)
        {
            AiStrategicChoice? win = TargetAwareAiMath.WinOrNull(legalActions, cancellationToken);
            if (win != null)
            {
                return win;
            }

            AiStrategicChoice? tsumogiri = TargetAwareAiMath.RiichiTsumogiri(
                legalActions,
                context.BasicContext.IsRiichi,
                myDrawnTile,
                cancellationToken);
            if (tsumogiri != null)
            {
                return tsumogiri;
            }

            // V4 kokushi discards are scored from myAllClosedTiles only, never from melds.
            CandidateRank? bestDiscard = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption action = legalActions[i];
                if (action.Kind != LegalActionKind.Discard)
                {
                    continue;
                }

                TileId tile = TargetAwareAiMath.RequireTile(action, "Discard");
                int score = TargetAwareAiMath.KokushiScore(
                    TargetAwareAiView.RemoveFirst(allClosed, tile));
                var rank = new CandidateRank(action, score, tile.TypeIndex, tile.CopyIndex);
                if (!bestDiscard.HasValue || rank.IsBetterThan(bestDiscard.Value))
                {
                    bestDiscard = rank;
                }
            }

            if (bestDiscard.HasValue)
            {
                return new AiStrategicChoice(bestDiscard.Value.Action, bestDiscard.Value.Score);
            }

            bool Forbidden(LegalActionOption action)
            {
                switch (action.Kind)
                {
                    case LegalActionKind.Other:
                        // V4 had a dedicated DeclareAbortiveDraw action. In V5 the Other
                        // kind is generic, so only an explicitly classified nine-terminals
                        // abortive draw is forbidden here; an unrelated Other candidate is
                        // not silently treated as one.
                        return context.BasicContext.TryGetFacts(action, out BasicCandidateFacts facts) &&
                               facts.AbortiveDraw == BasicAbortiveDrawKind.NineTerminals;
                    case LegalActionKind.Chi:
                    case LegalActionKind.Pon:
                    case LegalActionKind.Kan:
                        return true;
                    default:
                        return false;
                }
            }

            return TargetAwareAiMath.Fallback(
                Basic,
                legalActions,
                context.BasicContext,
                myClosedTiles,
                myDrawnTile,
                dealerSeat,
                roundWind,
                scores,
                Forbidden,
                cancellationToken);
        }
    }

    /// <summary>One candidate's V4 <c>maxWithOrNull</c> ordering key.</summary>
    internal readonly struct CandidateRank
    {
        internal CandidateRank(LegalActionOption action, int score, int typeIndex, int copyIndex)
        {
            Action = action;
            Score = score;
            TypeIndex = typeIndex;
            CopyIndex = copyIndex;
        }

        internal LegalActionOption Action { get; }

        internal int Score { get; }

        internal int TypeIndex { get; }

        internal int CopyIndex { get; }

        /// <summary>
        /// Kotlin's <c>maxWithOrNull</c> replaces the current maximum only when the comparator
        /// reports strictly less. Therefore a fully equal later candidate must not replace the
        /// first one. TypeIndex and CopyIndex are ascending sort keys in V4, so the greatest
        /// value wins (not the smallest tile id).
        /// </summary>
        internal bool IsBetterThan(CandidateRank other)
        {
            if (Score != other.Score)
            {
                return Score > other.Score;
            }

            if (TypeIndex != other.TypeIndex)
            {
                return TypeIndex > other.TypeIndex;
            }

            return CopyIndex > other.CopyIndex;
        }
    }

    internal static class TargetAwareAiMath
    {
        internal static void ValidateRawInputs(
            IReadOnlyList<LegalActionOption> legalActions,
            TargetAwareAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            IReadOnlyList<AiVisibleMeld> myMelds,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores)
        {
            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (myClosedTiles == null)
            {
                throw new ArgumentNullException(nameof(myClosedTiles));
            }

            if (myMelds == null)
            {
                throw new ArgumentNullException(nameof(myMelds));
            }

            if (scores == null)
            {
                throw new ArgumentNullException(nameof(scores));
            }

            AiGuard.CheckSeat(dealerSeat, nameof(dealerSeat));
            if (roundWind < 1 || roundWind > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(roundWind), roundWind, "Round wind must be 1..3.");
            }

            if (scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four seats.", nameof(scores));
            }

            if (context.BasicContext.Seat != context.Seat)
            {
                throw new TargetAwareAiContextException(
                    "Basic context must belong to the target-aware context seat.");
            }
        }

        internal static AiStrategicChoice? WinOrNull(
            IReadOnlyList<LegalActionOption> legalActions,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Ron)
                {
                    return new AiStrategicChoice(legalActions[i], 0d);
                }
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Tsumo)
                {
                    return new AiStrategicChoice(legalActions[i], 0d);
                }
            }

            return null;
        }

        internal static AiStrategicChoice? RiichiTsumogiri(
            IReadOnlyList<LegalActionOption> legalActions,
            bool isRiichi,
            TileId? myDrawnTile,
            CancellationToken cancellationToken)
        {
            if (!isRiichi || !myDrawnTile.HasValue)
            {
                return null;
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption action = legalActions[i];
                if (action.Kind == LegalActionKind.Discard &&
                    action.Tile.HasValue &&
                    action.Tile.Value == myDrawnTile.Value)
                {
                    return new AiStrategicChoice(action, 0d);
                }
            }

            return null;
        }

        internal static AiStrategicChoice? Fallback(
            BasicAiStrategy basic,
            IReadOnlyList<LegalActionOption> legalActions,
            BasicAiContext basicContext,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            Func<LegalActionOption, bool> forbidden,
            CancellationToken cancellationToken)
        {
            // The reference order is important: BasicStrategy sees the COMPLETE candidate
            // list, then the target chooses the first candidate that its own filter allows.
            // Running BasicStrategy on a pre-filtered list would not match V4.
            AiStrategicChoice? basicChoice = basic.TryChoose(
                legalActions,
                basicContext,
                myClosedTiles,
                myDrawnTile,
                dealerSeat,
                roundWind,
                scores,
                cancellationToken);
            if (basicChoice != null && !forbidden(basicChoice.Action))
            {
                return basicChoice;
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!forbidden(legalActions[i]))
                {
                    return new AiStrategicChoice(legalActions[i], 0d);
                }
            }

            return null;
        }

        internal static TargetAwareChiFact GetChiFact(
            TargetAwareAiContext context,
            LegalActionOption action)
        {
            if (!context.TryGetMeldFact(action, out TargetAwareMeldFact fact) ||
                !(fact is TargetAwareChiFact chi))
            {
                throw new TargetAwareAiContextException(
                    "Missing Chi fact for candidate " + (action.OptionId ?? "<none>") + ".");
            }

            return chi;
        }

        internal static TargetAwarePonFact GetPonFact(
            TargetAwareAiContext context,
            LegalActionOption action)
        {
            if (!context.TryGetMeldFact(action, out TargetAwareMeldFact fact) ||
                !(fact is TargetAwarePonFact pon))
            {
                throw new TargetAwareAiContextException(
                    "Missing Pon fact for candidate " + (action.OptionId ?? "<none>") + ".");
            }

            return pon;
        }

        internal static bool AllTilesHaveSuit(
            IReadOnlyList<TileId> tiles,
            TileSuit suit)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].Suit != suit)
                {
                    return false;
                }
            }

            return true;
        }

        internal static TileId RequireTile(LegalActionOption action, string role)
        {
            if (!action.Tile.HasValue)
            {
                throw new TargetAwareAiContextException(
                    "A " + role + " candidate must carry a physical tile (option=" +
                    (action.OptionId ?? "<none>") + ").");
            }

            return action.Tile.Value;
        }

        internal static int SuitScore(IReadOnlyList<TileId> tiles, TileSuit suit)
        {
            var ranks = new int[10];
            int targetCount = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                if (tile.Suit == suit)
                {
                    ranks[tile.Rank]++;
                    targetCount++;
                }
            }

            int mentsu = 0;
            for (int r = 1; r <= 9; r++)
            {
                if (ranks[r] >= 3)
                {
                    mentsu++;
                }
            }

            for (int r = 1; r <= 7; r++)
            {
                if (ranks[r] >= 1 && ranks[r + 1] >= 1 && ranks[r + 2] >= 1)
                {
                    mentsu++;
                }
            }

            int taatsu = 0;
            for (int r = 1; r <= 8; r++)
            {
                if (ranks[r] >= 1 && ranks[r + 1] >= 1)
                {
                    taatsu++;
                }
            }

            for (int r = 1; r <= 7; r++)
            {
                if (ranks[r] >= 1 && ranks[r + 2] >= 1)
                {
                    taatsu++;
                }
            }

            return (100 * targetCount) + (12 * mentsu) + (4 * taatsu);
        }

        internal static int NumberMentsuCount(IReadOnlyList<TileId> tiles)
        {
            var man = new int[10];
            var sou = new int[10];
            var pin = new int[10];
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                switch (tile.Suit)
                {
                    case TileSuit.Man:
                        man[tile.Rank]++;
                        break;
                    case TileSuit.Sou:
                        sou[tile.Rank]++;
                        break;
                    case TileSuit.Pin:
                        pin[tile.Rank]++;
                        break;
                    default:
                        break;
                }
            }

            return CountMentsu(man) + CountMentsu(sou) + CountMentsu(pin);
        }

        private static int CountMentsu(int[] ranks)
        {
            int mentsu = 0;
            for (int r = 1; r <= 9; r++)
            {
                if (ranks[r] >= 3)
                {
                    mentsu++;
                }
            }

            for (int r = 1; r <= 7; r++)
            {
                if (ranks[r] >= 1 && ranks[r + 1] >= 1 && ranks[r + 2] >= 1)
                {
                    mentsu++;
                }
            }

            return mentsu;
        }

        internal static int HonorScore(
            IReadOnlyList<TileId> tiles,
            IReadOnlyList<int> requiredHonors)
        {
            var counts = new int[34];
            for (int i = 0; i < tiles.Count; i++)
            {
                counts[tiles[i].TypeIndex]++;
            }

            int completeKinds = 0;
            int sumMin3 = 0;
            for (int i = 0; i < requiredHonors.Count; i++)
            {
                int count = counts[requiredHonors[i]];
                if (count >= 3)
                {
                    completeKinds++;
                }

                sumMin3 += Math.Min(count, 3);
            }

            return (100 * completeKinds) + (15 * sumMin3) + (3 * NumberMentsuCount(tiles));
        }

        internal static int KokushiScore(IReadOnlyList<TileId> tiles)
        {
            var counts = new int[34];
            for (int i = 0; i < tiles.Count; i++)
            {
                counts[tiles[i].TypeIndex]++;
            }

            int distinct = 0;
            int totalExtra = 0;
            bool hasPair = false;
            for (int i = 0; i < KokushiGoalAi.KokushiIndices.Count; i++)
            {
                int count = counts[KokushiGoalAi.KokushiIndices[i]];
                if (count >= 1)
                {
                    distinct++;
                }

                if (count >= 2)
                {
                    hasPair = true;
                }

                if (count > 1)
                {
                    totalExtra += count - 1;
                }
            }

            int extraCopies = Math.Max(0, totalExtra - 1);
            return (100 * distinct) + (hasPair ? 15 : 0) - (20 * extraCopies);
        }
    }
}

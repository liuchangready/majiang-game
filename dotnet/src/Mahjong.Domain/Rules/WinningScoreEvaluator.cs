using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    public sealed class DoraIndicatorHit
    {
        public DoraIndicatorHit(TileId indicator, TileId target, IEnumerable<TileId> tiles)
        {
            Indicator = indicator;
            Target = target;
            Tiles = Array.AsReadOnly(tiles.ToArray());
        }

        public TileId Indicator { get; }

        public TileId Target { get; }

        public IReadOnlyList<TileId> Tiles { get; }
    }

    public sealed class DoraBreakdown
    {
        public DoraBreakdown(
            int indicatorCount,
            int uraCount,
            int redCount,
            IEnumerable<TileId>? indicatorTiles = null,
            IEnumerable<TileId>? uraTiles = null,
            IEnumerable<TileId>? redTiles = null,
            IEnumerable<DoraIndicatorHit>? indicatorHits = null,
            IEnumerable<DoraIndicatorHit>? uraHits = null)
        {
            IndicatorCount = indicatorCount;
            UraCount = uraCount;
            RedCount = redCount;
            IndicatorTiles = Array.AsReadOnly((indicatorTiles ?? Array.Empty<TileId>()).ToArray());
            UraTiles = Array.AsReadOnly((uraTiles ?? Array.Empty<TileId>()).ToArray());
            RedTiles = Array.AsReadOnly((redTiles ?? Array.Empty<TileId>()).ToArray());
            IndicatorHits = Array.AsReadOnly((indicatorHits ?? Array.Empty<DoraIndicatorHit>()).ToArray());
            UraHits = Array.AsReadOnly((uraHits ?? Array.Empty<DoraIndicatorHit>()).ToArray());
            TotalHan = checked(indicatorCount + uraCount + redCount);
        }

        public int IndicatorCount { get; }

        public int UraCount { get; }

        public int RedCount { get; }

        public int TotalHan { get; }

        public int Total => TotalHan;

        public IReadOnlyList<TileId> IndicatorTiles { get; }

        public IReadOnlyList<TileId> UraTiles { get; }

        public IReadOnlyList<TileId> RedTiles { get; }

        public IReadOnlyList<DoraIndicatorHit> IndicatorHits { get; }

        public IReadOnlyList<DoraIndicatorHit> UraHits { get; }
    }

    public sealed class ScoreBreakdown
    {
        public ScoreBreakdown(
            IEnumerable<YakuHit> yaku,
            int yakumanCount,
            DoraBreakdown dora,
            FuBreakdown fu,
            ScoreResult score,
            long honbaAddition,
            long riichiAddition,
            long totalPoints,
            bool uraEligible,
            bool isDealer,
            bool isTsumo)
        {
            if (yaku == null)
            {
                throw new ArgumentNullException(nameof(yaku));
            }

            Yaku = Array.AsReadOnly(yaku.ToArray());
            YakumanCount = yakumanCount;
            Dora = dora ?? throw new ArgumentNullException(nameof(dora));
            Fu = fu ?? throw new ArgumentNullException(nameof(fu));
            Score = score ?? throw new ArgumentNullException(nameof(score));
            HonbaAddition = honbaAddition;
            RiichiAddition = riichiAddition;
            TotalPoints = totalPoints;
            UraEligible = uraEligible;
            IsDealer = isDealer;
            IsTsumo = isTsumo;
        }

        public IReadOnlyList<YakuHit> Yaku { get; }

        public int YakumanCount { get; }

        public DoraBreakdown Dora { get; }

        public FuBreakdown Fu { get; }

        public ScoreResult Score { get; }

        public long HonbaAddition { get; }

        public long RiichiAddition { get; }

        public long TotalPoints { get; }

        public bool UraEligible { get; }

        public bool IsDealer { get; }

        public bool IsTsumo { get; }

        public PaymentPlan PaymentPlan => Score.PaymentPlan(IsDealer, IsTsumo);

        public int TotalYakuHan => Yaku.Sum(y => y.Han);
    }

    public sealed class Candidate
    {
        public Candidate(
            Decomposition decomposition,
            YakuResult yakuResult,
            int fu,
            ScoreResult scoreResult,
            long totalPoints)
        {
            Decomposition = decomposition ?? throw new ArgumentNullException(nameof(decomposition));
            YakuResult = yakuResult ?? throw new ArgumentNullException(nameof(yakuResult));
            Fu = fu;
            ScoreResult = scoreResult ?? throw new ArgumentNullException(nameof(scoreResult));
            TotalPoints = totalPoints;
        }

        public Decomposition Decomposition { get; }

        public YakuResult YakuResult { get; }

        public int Fu { get; }

        public ScoreResult ScoreResult { get; }

        public long TotalPoints { get; }
    }

    /// <summary>
    /// Port of V4 <c>WinningScoreEvaluator</c>. Candidate tie-breaking is byte-for-byte the
    /// V4 comparator: total points, yakuman count (non-yakuman counts as zero), total yaku
    /// han, then fu; the first candidate wins an exact tie.
    /// </summary>
    public static class WinningScoreEvaluator
    {
        public static Candidate? Evaluate(
            PlayerHand hand,
            WinContext context,
            bool? isDealer = null,
            int extraHan = 0)
        {
            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (extraHan < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(extraHan), extraHan, "extraHan cannot be negative.");
            }

            bool dealer = isDealer ?? (context.SeatWind == 1);
            PlayerHand evalHand = PrepareEvaluationHand(hand, context.WinTile);
            IReadOnlyList<Decomposition> decompositions = HandDecomposer.Decompose(evalHand.AllClosedTiles);
            if (decompositions.Count == 0)
            {
                return null;
            }

            var candidates = new List<Candidate>();
            foreach (Decomposition decomposition in decompositions)
            {
                YakuResult yaku = YakuEvaluator.EvaluateForDecomposition(decomposition, evalHand, context);
                if (!yaku.IsYakuman && yaku.TotalHan <= 0)
                {
                    continue;
                }

                int fu;
                ScoreResult score;
                if (yaku.IsYakuman)
                {
                    fu = decomposition.Pattern == DecompPattern.Chiitoitsu ? 25 : 30;
                    score = ScoreCalculator.Calculate(fu, 0, yaku.YakumanCount);
                }
                else
                {
                    fu = FuCalculator.Calculate(
                        decomposition,
                        context.WinTile,
                        context.IsTsumo,
                        context.IsClosed,
                        context.SeatWind,
                        context.RoundWind,
                        evalHand.Melds);
                    int effectiveHan = checked(yaku.TotalHan + extraHan);
                    score = ScoreCalculator.Calculate(fu, effectiveHan, 0);
                }

                long totalPoints = score.TotalPoints(dealer, context.IsTsumo);
                candidates.Add(new Candidate(decomposition, yaku, fu, score, totalPoints));
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            Candidate best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                Candidate candidate = candidates[i];
                if (Compare(candidate, best) > 0)
                {
                    best = candidate;
                }
            }

            return best;
        }

        public static Candidate? Evaluate(
            PlayerHand hand,
            WinContext context,
            IEnumerable<TileId> doraIndicators,
            IEnumerable<TileId>? uraDoraIndicators = null,
            bool? isDealer = null)
        {
            if (doraIndicators == null)
            {
                throw new ArgumentNullException(nameof(doraIndicators));
            }

            PlayerHand evalHand = PrepareEvaluationHand(hand, context.WinTile);
            TileId[] ura = context.IsRiichi
                ? (uraDoraIndicators ?? Array.Empty<TileId>()).ToArray()
                : Array.Empty<TileId>();
            int doraCount = CountDora(evalHand, doraIndicators, ura);
            return Evaluate(hand, context, isDealer, doraCount);
        }

        public static int CountDora(
            PlayerHand hand,
            IEnumerable<TileId> doraIndicators,
            IEnumerable<TileId>? uraDoraIndicators = null)
        {
            return BreakdownDora(hand, doraIndicators, uraDoraIndicators).TotalHan;
        }

        public static DoraBreakdown BreakdownDora(
            PlayerHand hand,
            IEnumerable<TileId> doraIndicators,
            IEnumerable<TileId>? uraDoraIndicators = null)
        {
            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            if (doraIndicators == null)
            {
                throw new ArgumentNullException(nameof(doraIndicators));
            }

            TileId[] allTiles = hand.AllClosedTiles.Concat(hand.Melds.SelectMany(m => m.Tiles)).ToArray();
            TileId[] redTiles = allTiles.Where(t => t.IsRedFive).ToArray();

            var indicatorHits = new List<DoraIndicatorHit>();
            var indicatorTiles = new List<TileId>();
            foreach (TileId indicator in doraIndicators)
            {
                int targetType = TileFacts.DoraIndicatorTarget(indicator.TypeIndex);
                TileId[] matched = allTiles.Where(t => t.TypeIndex == targetType).ToArray();
                indicatorHits.Add(new DoraIndicatorHit(
                    indicator,
                    TileId.FromTypeIndex(targetType, 0),
                    matched));
                indicatorTiles.AddRange(matched);
            }

            var uraHits = new List<DoraIndicatorHit>();
            var uraTiles = new List<TileId>();
            if (uraDoraIndicators != null)
            {
                foreach (TileId indicator in uraDoraIndicators)
                {
                    int targetType = TileFacts.DoraIndicatorTarget(indicator.TypeIndex);
                    TileId[] matched = allTiles.Where(t => t.TypeIndex == targetType).ToArray();
                    uraHits.Add(new DoraIndicatorHit(
                        indicator,
                        TileId.FromTypeIndex(targetType, 0),
                        matched));
                    uraTiles.AddRange(matched);
                }
            }

            return new DoraBreakdown(
                indicatorTiles.Count,
                uraTiles.Count,
                redTiles.Length,
                indicatorTiles,
                uraTiles,
                redTiles,
                indicatorHits,
                uraHits);
        }

        public static ScoreBreakdown? Explain(
            PlayerHand hand,
            WinContext context,
            IEnumerable<TileId> doraIndicators,
            IEnumerable<TileId>? uraDoraIndicators = null,
            bool? isDealer = null,
            int honba = 0,
            int riichiSticks = 0,
            int? loser = null)
        {
            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (honba < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(honba), honba, "honba cannot be negative.");
            }

            if (riichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(riichiSticks), riichiSticks, "riichiSticks cannot be negative.");
            }

            if (loser.HasValue && (loser.Value < 0 || loser.Value > 3))
            {
                throw new ArgumentOutOfRangeException(nameof(loser), loser.Value, "loser must be 0..3.");
            }

            bool dealer = isDealer ?? (context.SeatWind == 1);
            PlayerHand evalHand = PrepareEvaluationHand(hand, context.WinTile);
            TileId[] ura = context.IsRiichi
                ? (uraDoraIndicators ?? Array.Empty<TileId>()).ToArray()
                : Array.Empty<TileId>();
            DoraBreakdown dora = BreakdownDora(evalHand, doraIndicators, ura);
            Candidate? candidate = Evaluate(hand, context, doraIndicators, uraDoraIndicators, dealer);
            if (candidate == null)
            {
                return null;
            }

            FuBreakdown fu = FuCalculator.CalculateBreakdown(
                candidate.Decomposition,
                context.WinTile,
                context.IsTsumo,
                context.IsClosed,
                context.SeatWind,
                context.RoundWind,
                evalHand.Melds);

            long honbaAddition = checked(300L * honba);
            long riichiAddition = checked(1000L * riichiSticks);
            long totalPoints = checked(candidate.TotalPoints + honbaAddition + riichiAddition);

            return new ScoreBreakdown(
                candidate.YakuResult.YakuList,
                candidate.YakuResult.IsYakuman ? candidate.YakuResult.YakumanCount : 0,
                dora,
                fu,
                candidate.ScoreResult,
                honbaAddition,
                riichiAddition,
                totalPoints,
                context.IsRiichi,
                dealer,
                context.IsTsumo);
        }

        public static bool CanWinWithYaku(
            PlayerHand hand,
            WinContext context,
            bool? isDealer = null)
        {
            return Evaluate(hand, context, isDealer) != null;
        }

        private static PlayerHand PrepareEvaluationHand(PlayerHand hand, TileId winTile)
        {
            if (hand.AllClosedTiles.Count % 3 == 1)
            {
                return new PlayerHand(hand.ClosedTiles, hand.Melds, winTile);
            }

            return hand;
        }

        private static int Compare(Candidate left, Candidate right)
        {
            int result = left.TotalPoints.CompareTo(right.TotalPoints);
            if (result != 0)
            {
                return result;
            }

            int leftYakuman = left.YakuResult.IsYakuman ? left.YakuResult.YakumanCount : 0;
            int rightYakuman = right.YakuResult.IsYakuman ? right.YakuResult.YakumanCount : 0;
            result = leftYakuman.CompareTo(rightYakuman);
            if (result != 0)
            {
                return result;
            }

            result = left.YakuResult.TotalHan.CompareTo(right.YakuResult.TotalHan);
            if (result != 0)
            {
                return result;
            }

            return left.Fu.CompareTo(right.Fu);
        }
    }
}

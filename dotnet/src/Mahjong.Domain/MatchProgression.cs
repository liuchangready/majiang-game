using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    /// <summary>
    /// Pure V4 match progression: dealer repeat, honba, wind extension, bankruptcy,
    /// agari-yame, final deposit award and tie-break ranking. No uma/oka, double yakuman,
    /// nagashi mangan or sanma behaviour exists here because V4 does not implement it.
    /// </summary>
    public sealed class MatchProgression
    {
        public MatchProgression(MatchRuleConfig? config = null)
        {
            Config = config ?? new MatchRuleConfig();
        }

        public MatchRuleConfig Config { get; }

        public bool IsBankrupt(long score)
        {
            return Config.BankruptcyInclusive
                ? score <= Config.BankruptcyThreshold
                : score < Config.BankruptcyThreshold;
        }

        public ProgressionResult NextRound(ProgressionInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            ValidateInputs(input);

            // 1. Bankruptcy is checked before any win/draw progression.
            if (Config.AllowBankruptcy && AnyBankrupt(input.Scores))
            {
                return AwardRiichiSticksAndFinish(
                    input.Scores,
                    input.RemainingRiichiSticks,
                    MatchFinishReason.Bankruptcy);
            }

            bool dealerContinues = IsDealerContinuing(input.CurrentDealer, input.Outcome);
            bool exhaustive = input.Outcome.Kind == RoundOutcomeKind.ExhaustiveDraw;

            int regularFinalWind = Config.Winds.Count > 0
                ? Config.Winds[Config.Winds.Count - 1]
                : (Config.Length == MatchLength.Tonpuusen ? 1 : 2);
            int finalRoundNumber = Config.HandsPerWind - 1;
            bool isNormalFinalRound =
                input.CurrentRoundWind == regularFinalWind &&
                input.CurrentRoundNumber == finalRoundNumber;
            bool isWestExtension = input.CurrentRoundWind > regularFinalWind;

            // 2. In west extension, reaching the return score ends the match immediately.
            if (isWestExtension && AnyReachedTarget(input.Scores))
            {
                return AwardRiichiSticksAndFinish(
                    input.Scores,
                    input.RemainingRiichiSticks,
                    MatchFinishReason.WestCompletion);
            }

            // 3. Final-round dealer continuation / agari-yame.
            if (isNormalFinalRound && dealerContinues)
            {
                bool canAgariYame = CheckAgariYameEligible(input.CurrentDealer, input.Scores);
                if (canAgariYame && input.DealerDeclaredAgariYame)
                {
                    return AwardRiichiSticksAndFinish(
                        input.Scores,
                        input.RemainingRiichiSticks,
                        MatchFinishReason.AgariYame);
                }

                return new ProgressionResult.NextRound(
                    input.CurrentRoundWind,
                    input.CurrentRoundNumber,
                    input.CurrentDealer,
                    checked(input.CurrentHonba + 1),
                    canAgariYame);
            }

            // 4. Final-round dealer rotation: finish or enter west extension.
            if (isNormalFinalRound && !dealerContinues)
            {
                if (AnyReachedTarget(input.Scores) || !Config.AllowWestExtension)
                {
                    return AwardRiichiSticksAndFinish(
                        input.Scores,
                        input.RemainingRiichiSticks,
                        MatchFinishReason.NormalCompletion);
                }

                return new ProgressionResult.NextRound(
                    input.CurrentRoundWind + 1,
                    0,
                    (input.CurrentDealer + 1) % 4,
                    exhaustive ? checked(input.CurrentHonba + 1) : 0,
                    false);
            }

            // 5. West-extension rotation.
            if (isWestExtension)
            {
                if (dealerContinues)
                {
                    return new ProgressionResult.NextRound(
                        input.CurrentRoundWind,
                        input.CurrentRoundNumber,
                        input.CurrentDealer,
                        checked(input.CurrentHonba + 1),
                        false);
                }

                if (input.CurrentRoundNumber < Config.HandsPerWind - 1)
                {
                    return new ProgressionResult.NextRound(
                        input.CurrentRoundWind,
                        input.CurrentRoundNumber + 1,
                        (input.CurrentDealer + 1) % 4,
                        exhaustive ? checked(input.CurrentHonba + 1) : 0,
                        false);
                }

                return AwardRiichiSticksAndFinish(
                    input.Scores,
                    input.RemainingRiichiSticks,
                    MatchFinishReason.WestCompletion);
            }

            // 6. Normal (non-final, non-west) progression.
            if (dealerContinues)
            {
                return new ProgressionResult.NextRound(
                    input.CurrentRoundWind,
                    input.CurrentRoundNumber,
                    input.CurrentDealer,
                    checked(input.CurrentHonba + 1),
                    false);
            }

            int nextDealer = (input.CurrentDealer + 1) % 4;
            int nextHonba = exhaustive ? checked(input.CurrentHonba + 1) : 0;
            int advanced = input.CurrentRoundNumber + 1;
            if (advanced >= Config.HandsPerWind)
            {
                return new ProgressionResult.NextRound(
                    input.CurrentRoundWind + 1,
                    0,
                    nextDealer,
                    nextHonba,
                    false);
            }

            return new ProgressionResult.NextRound(
                input.CurrentRoundWind,
                advanced,
                nextDealer,
                nextHonba,
                false);
        }

        public bool IsDealerContinuing(int dealer, RoundOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case RoundOutcomeKind.DealerWin:
                    return true;
                case RoundOutcomeKind.NonDealerWin:
                    return false;
                case RoundOutcomeKind.Ron:
                case RoundOutcomeKind.Tsumo:
                    return outcome.WinnerSeat == dealer;
                case RoundOutcomeKind.ExhaustiveDraw:
                    for (int i = 0; i < outcome.TenpaiSeats.Count; i++)
                    {
                        if (outcome.TenpaiSeats[i] == dealer)
                        {
                            return true;
                        }
                    }

                    return false;
                case RoundOutcomeKind.AbortiveDraw:
                    return true;
                default:
                    throw new ArgumentException("Unknown outcome kind " + outcome.Kind + ".", nameof(outcome));
            }
        }

        public bool IsNonDealerWinning(int dealer, RoundOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case RoundOutcomeKind.DealerWin:
                    return false;
                case RoundOutcomeKind.NonDealerWin:
                    return true;
                case RoundOutcomeKind.Ron:
                case RoundOutcomeKind.Tsumo:
                    return outcome.WinnerSeat != dealer;
                default:
                    return false;
            }
        }

        /// <summary>Agari-yame requires the option, the return score and a strictly sole first place.</summary>
        public bool CheckAgariYameEligible(int dealer, IReadOnlyList<long> scores)
        {
            if (!Config.AllowAgariYame)
            {
                return false;
            }

            long dealerScore = scores[dealer];
            if (dealerScore < Config.TargetScore)
            {
                return false;
            }

            for (int i = 0; i < scores.Count; i++)
            {
                if (i != dealer && scores[i] >= dealerScore)
                {
                    return false;
                }
            }

            return true;
        }

        public IReadOnlyList<RankingEntry> ComputeRankings(IReadOnlyList<long> scores)
        {
            var seats = new int[scores.Count];
            for (int i = 0; i < seats.Length; i++)
            {
                seats[i] = i;
            }

            Array.Sort(seats, (left, right) =>
            {
                int byScore = scores[right].CompareTo(scores[left]);
                if (byScore != 0)
                {
                    return byScore;
                }

                int leftPriority = PriorityOf(left);
                int rightPriority = PriorityOf(right);
                int byPriority = leftPriority.CompareTo(rightPriority);
                return byPriority != 0 ? byPriority : left.CompareTo(right);
            });

            var rankings = new List<RankingEntry>(seats.Length);
            for (int i = 0; i < seats.Length; i++)
            {
                rankings.Add(new RankingEntry(i + 1, seats[i], scores[seats[i]]));
            }

            return new ReadOnlyCollection<RankingEntry>(rankings);
        }

        public ProgressionResult.MatchFinished AwardRiichiSticksAndFinish(
            IReadOnlyList<long> scores,
            int remainingRiichiSticks,
            MatchFinishReason reason)
        {
            if (scores == null)
            {
                throw new ArgumentNullException(nameof(scores));
            }

            if (scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain four entries.", nameof(scores));
            }

            if (remainingRiichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingRiichiSticks),
                    remainingRiichiSticks,
                    "Remaining sticks must be non-negative.");
            }

            var finalScores = new long[4];
            for (int i = 0; i < 4; i++)
            {
                finalScores[i] = scores[i];
            }

            IReadOnlyList<RankingEntry> pre = ComputeRankings(scores);
            int firstSeat = pre[0].Seat;
            int? awarded = null;
            if (Config.AwardRemainingRiichiSticksToFirst && remainingRiichiSticks > 0)
            {
                finalScores[firstSeat] = checked(finalScores[firstSeat] + (long)remainingRiichiSticks * 1000L);
                awarded = firstSeat;
            }

            return new ProgressionResult.MatchFinished(
                new ReadOnlyCollection<long>(finalScores),
                ComputeRankings(finalScores),
                reason,
                awarded);
        }

        private int PriorityOf(int seat)
        {
            for (int i = 0; i < Config.TieBreakSeats.Count; i++)
            {
                if (Config.TieBreakSeats[i] == seat)
                {
                    return i;
                }
            }

            return seat;
        }

        private bool AnyBankrupt(IReadOnlyList<long> scores)
        {
            for (int i = 0; i < scores.Count; i++)
            {
                if (IsBankrupt(scores[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private bool AnyReachedTarget(IReadOnlyList<long> scores)
        {
            for (int i = 0; i < scores.Count; i++)
            {
                if (scores[i] >= Config.TargetScore)
                {
                    return true;
                }
            }

            return false;
        }

        private void ValidateInputs(ProgressionInput input)
        {
            V5Guard.CheckSeat(input.CurrentDealer, nameof(input.CurrentDealer));
            int lastWind = Config.Winds.Count > 0 ? Config.Winds[Config.Winds.Count - 1] : 1;
            int maxWind = Config.AllowWestExtension ? lastWind + 1 : lastWind;
            int highestAllowed = Math.Max(3, maxWind);
            if (input.CurrentRoundWind < 1 || input.CurrentRoundWind > highestAllowed)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input),
                    input.CurrentRoundWind,
                    "Round wind must be in 1.." + highestAllowed + ".");
            }

            if (input.CurrentRoundNumber < 0 || input.CurrentRoundNumber >= Config.HandsPerWind)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input),
                    input.CurrentRoundNumber,
                    "Round number must be in 0.." + (Config.HandsPerWind - 1) + ".");
            }

            if (input.CurrentHonba < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(input), input.CurrentHonba, "Honba must be non-negative.");
            }
        }
    }
}

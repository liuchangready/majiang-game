using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    public sealed class SettlementInput
    {
        public SettlementInput(IEnumerable<long> scores, int dealer, int honba, int riichiSticks)
        {
            if (scores == null)
            {
                throw new ArgumentNullException(nameof(scores));
            }

            long[] materialized = scores.ToArray();
            if (materialized.Length != 4)
            {
                throw new ArgumentException("Scores must contain exactly 4 entries, got " + materialized.Length + ".", nameof(scores));
            }

            if (dealer < 0 || dealer > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(dealer), dealer, "dealer must be 0..3.");
            }

            if (honba < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(honba), honba, "honba cannot be negative.");
            }

            if (riichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(riichiSticks), riichiSticks, "riichiSticks cannot be negative.");
            }

            Scores = Array.AsReadOnly(materialized);
            Dealer = dealer;
            Honba = honba;
            RiichiSticks = riichiSticks;
        }

        public IReadOnlyList<long> Scores { get; }

        public int Dealer { get; }

        public int Honba { get; }

        public int RiichiSticks { get; }
    }

    public sealed class SettlementResult
    {
        public SettlementResult(IEnumerable<long> deltas, IEnumerable<long> newScores, int remainingRiichiSticks)
        {
            if (deltas == null)
            {
                throw new ArgumentNullException(nameof(deltas));
            }

            if (newScores == null)
            {
                throw new ArgumentNullException(nameof(newScores));
            }

            long[] deltaArray = deltas.ToArray();
            long[] scoreArray = newScores.ToArray();
            if (deltaArray.Length != 4 || scoreArray.Length != 4)
            {
                throw new ArgumentException("Settlement results must contain exactly 4 seats.");
            }

            Deltas = Array.AsReadOnly(deltaArray);
            NewScores = Array.AsReadOnly(scoreArray);
            RemainingRiichiSticks = remainingRiichiSticks;
        }

        public IReadOnlyList<long> Deltas { get; }

        public IReadOnlyList<long> NewScores { get; }

        public int RemainingRiichiSticks { get; }
    }

    /// <summary>
    /// Port of V4 <c>SettlementService</c> with checked long arithmetic. Every method is a
    /// pure calculation over immutable inputs; a rejected input throws before any allocation
    /// of a result and cannot mutate the caller's score collection.
    /// </summary>
    public static class SettlementService
    {
        public static SettlementResult Ron(SettlementInput input, int winner, int loser, ScoreResult score)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (score == null)
            {
                throw new ArgumentNullException(nameof(score));
            }

            if (winner < 0 || winner > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(winner), winner, "winner must be 0..3.");
            }

            if (loser < 0 || loser > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(loser), loser, "loser must be 0..3.");
            }

            if (winner == loser)
            {
                throw new ArgumentException("Winner and loser cannot be the same player in ron.");
            }

            bool isDealer = winner == input.Dealer;
            long basePayment = isDealer ? score.DealerRonPayment() : score.NonDealerRonPayment();
            long honbaPayment = checked(300L * input.Honba);
            long totalLoserPay = checked(basePayment + honbaPayment);
            long riichiPoints = checked(1000L * input.RiichiSticks);

            var deltas = new long[4];
            deltas[loser] = checked(-totalLoserPay);
            deltas[winner] = checked(totalLoserPay + riichiPoints);

            return Complete(input, deltas, 0);
        }

        public static SettlementResult Tsumo(SettlementInput input, int winner, ScoreResult score)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (score == null)
            {
                throw new ArgumentNullException(nameof(score));
            }

            if (winner < 0 || winner > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(winner), winner, "winner must be 0..3.");
            }

            bool isDealer = winner == input.Dealer;
            long honbaPerPayer = checked(100L * input.Honba);
            long riichiPoints = checked(1000L * input.RiichiSticks);
            var deltas = new long[4];

            if (isDealer)
            {
                long basePayment = score.DealerTsumoPayment();
                long payEach = checked(basePayment + honbaPerPayer);
                long totalCollected = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (i != winner)
                    {
                        deltas[i] = checked(-payEach);
                        totalCollected = checked(totalCollected + payEach);
                    }
                }

                deltas[winner] = checked(totalCollected + riichiPoints);
            }
            else
            {
                NonDealerTsumoAmounts payment = score.NonDealerTsumoPayment();
                long dealerPay = checked(payment.Dealer + honbaPerPayer);
                long otherPay = checked(payment.Other + honbaPerPayer);
                long totalCollected = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (i != winner)
                    {
                        long pay = i == input.Dealer ? dealerPay : otherPay;
                        deltas[i] = checked(-pay);
                        totalCollected = checked(totalCollected + pay);
                    }
                }

                deltas[winner] = checked(totalCollected + riichiPoints);
            }

            return Complete(input, deltas, 0);
        }

        public static SettlementResult ExhaustiveDraw(SettlementInput input, IEnumerable<int> tenpai)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (tenpai == null)
            {
                throw new ArgumentNullException(nameof(tenpai));
            }

            bool[] tenpaiSeats = new bool[4];
            int count = 0;
            foreach (int seat in tenpai)
            {
                if (seat >= 0 && seat <= 3 && !tenpaiSeats[seat])
                {
                    tenpaiSeats[seat] = true;
                    count++;
                }
            }

            var deltas = new long[4];
            if (count == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    deltas[i] = tenpaiSeats[i] ? 3000 : -1000;
                }
            }
            else if (count == 2)
            {
                for (int i = 0; i < 4; i++)
                {
                    deltas[i] = tenpaiSeats[i] ? 1500 : -1500;
                }
            }
            else if (count == 3)
            {
                for (int i = 0; i < 4; i++)
                {
                    deltas[i] = tenpaiSeats[i] ? 1000 : -3000;
                }
            }

            return Complete(input, deltas, input.RiichiSticks);
        }

        public static SettlementResult AbortiveDraw(SettlementInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            return Complete(input, new long[4], input.RiichiSticks);
        }

        private static SettlementResult Complete(SettlementInput input, long[] deltas, int remainingRiichiSticks)
        {
            var newScores = new long[4];
            long oldTotal = checked(input.Scores.Sum() + checked(1000L * input.RiichiSticks));
            long newTotalWithSticks = checked(1000L * remainingRiichiSticks);
            for (int i = 0; i < 4; i++)
            {
                newScores[i] = checked(input.Scores[i] + deltas[i]);
                if (deltas[i] != checked(newScores[i] - input.Scores[i]))
                {
                    throw new InvalidOperationException("Delta mismatch at seat " + i + ".");
                }

                newTotalWithSticks = checked(newTotalWithSticks + newScores[i]);
            }

            if (oldTotal != newTotalWithSticks)
            {
                throw new InvalidOperationException(
                    "Points conservation violated: oldTotal=" + oldTotal + ", newTotal=" + newTotalWithSticks + ".");
            }

            return new SettlementResult(deltas, newScores, remainingRiichiSticks);
        }
    }
}

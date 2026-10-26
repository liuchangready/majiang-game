using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Mahjong.Domain.Rules
{
    public static class ScoreCapCodes
    {
        public const string Mangan = "MANGAN";
        public const string Haneman = "HANEMAN";
        public const string Baiman = "BAIMAN";
        public const string Sanbaiman = "SANBAIMAN";
        public const string KazoeYakuman = "KAZOE_YAKUMAN";
        public const string Yakuman = "YAKUMAN";
    }

    public enum PaymentKind
    {
        DealerTsumo,
        NonDealerTsumo,
        DealerRon,
        NonDealerRon
    }

    public enum PayerRole
    {
        Dealer,
        NonDealer,
        Discarder
    }

    public sealed class PaymentComponent
    {
        public PaymentComponent(PayerRole payerRole, long amount, int count)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "amount cannot be negative.");
            }

            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must be >= 1.");
            }

            PayerRole = payerRole;
            Amount = amount;
            Count = count;
        }

        public PayerRole PayerRole { get; }

        public long Amount { get; }

        public int Count { get; }

        public long Subtotal => checked(Amount * Count);
    }

    public sealed class PaymentPlan
    {
        public PaymentPlan(PaymentKind kind, IEnumerable<PaymentComponent> components, long total)
        {
            if (components == null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            PaymentComponent[] materialized = components.ToArray();
            if (materialized.Length == 0)
            {
                throw new ArgumentException("components cannot be empty.", nameof(components));
            }

            long subtotal = materialized.Aggregate(0L, (sum, component) => checked(sum + component.Subtotal));
            if (total != subtotal)
            {
                throw new ArgumentException(
                    "PaymentPlan.total " + total + " must equal component subtotals " + subtotal + ".",
                    nameof(total));
            }

            if (total < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(total), total, "total cannot be negative.");
            }

            Kind = kind;
            Components = Array.AsReadOnly(materialized);
            Total = total;
        }

        public PaymentKind Kind { get; }

        public IReadOnlyList<PaymentComponent> Components { get; }

        public long Total { get; }

        public string KindCode
        {
            get
            {
                switch (Kind)
                {
                    case PaymentKind.DealerTsumo:
                        return "DEALER_TSUMO";
                    case PaymentKind.NonDealerTsumo:
                        return "NON_DEALER_TSUMO";
                    case PaymentKind.DealerRon:
                        return "DEALER_RON";
                    case PaymentKind.NonDealerRon:
                        return "NON_DEALER_RON";
                    default:
                        throw new InvalidOperationException("Unsupported payment kind: " + Kind);
                }
            }
        }
    }

    /// <summary>
    /// Integer-only riichi scoring. All base points and payments are <see cref="long"/>.
    /// The raw formula is kept with <see cref="BigInteger"/> only as an exact diagnostic;
    /// payments and caps use checked long arithmetic and throw on overflow.
    /// </summary>
    public sealed class ScoreResult
    {
        public ScoreResult(
            int fu,
            int han,
            long basePoints,
            bool isYakuman,
            int yakumanMultiplier,
            long rawBasePoints,
            string rawBasePointsExact,
            string? capCode)
        {
            if (fu < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fu), fu, "fu cannot be negative.");
            }

            if (han < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(han), han, "han cannot be negative.");
            }

            if (basePoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(basePoints), basePoints, "basePoints cannot be negative.");
            }

            Fu = fu;
            Han = han;
            BasePoints = basePoints;
            IsYakuman = isYakuman;
            YakumanMultiplier = yakumanMultiplier;
            RawBasePoints = rawBasePoints;
            RawBasePointsExact = rawBasePointsExact ?? throw new ArgumentNullException(nameof(rawBasePointsExact));
            CapCode = capCode;
        }

        public int Fu { get; }

        public int Han { get; }

        public long BasePoints { get; }

        public bool IsYakuman { get; }

        public int YakumanMultiplier { get; }

        public long RawBasePoints { get; }

        public string RawBasePointsExact { get; }

        public string? CapCode { get; }

        public long DealerTsumoPayment() => ScoreCalculator.RoundUpTo100(checked(BasePoints * 2));

        public NonDealerTsumoAmounts NonDealerTsumoPayment()
        {
            return new NonDealerTsumoAmounts(
                ScoreCalculator.RoundUpTo100(checked(BasePoints * 2)),
                ScoreCalculator.RoundUpTo100(BasePoints));
        }

        public long DealerRonPayment() => ScoreCalculator.RoundUpTo100(checked(BasePoints * 6));

        public long NonDealerRonPayment() => ScoreCalculator.RoundUpTo100(checked(BasePoints * 4));

        public PaymentPlan PaymentPlan(bool isDealer, bool isTsumo)
        {
            if (isTsumo && isDealer)
            {
                long each = DealerTsumoPayment();
                return new PaymentPlan(
                    PaymentKind.DealerTsumo,
                    new[] { new PaymentComponent(PayerRole.NonDealer, each, 3) },
                    checked(each * 3));
            }

            if (isTsumo)
            {
                NonDealerTsumoAmounts payment = NonDealerTsumoPayment();
                return new PaymentPlan(
                    PaymentKind.NonDealerTsumo,
                    new[]
                    {
                        new PaymentComponent(PayerRole.Dealer, payment.Dealer, 1),
                        new PaymentComponent(PayerRole.NonDealer, payment.Other, 2)
                    },
                    checked(payment.Dealer + (payment.Other * 2)));
            }

            if (isDealer)
            {
                long pay = DealerRonPayment();
                return new PaymentPlan(
                    PaymentKind.DealerRon,
                    new[] { new PaymentComponent(PayerRole.Discarder, pay, 1) },
                    pay);
            }

            long nonDealerPay = NonDealerRonPayment();
            return new PaymentPlan(
                PaymentKind.NonDealerRon,
                new[] { new PaymentComponent(PayerRole.Discarder, nonDealerPay, 1) },
                nonDealerPay);
        }

        public long TotalPoints(bool isDealer, bool isTsumo)
        {
            return PaymentPlan(isDealer, isTsumo).Total;
        }
    }

    public readonly struct NonDealerTsumoAmounts
    {
        public NonDealerTsumoAmounts(long dealer, long other)
        {
            Dealer = dealer;
            Other = other;
        }

        public long Dealer { get; }

        public long Other { get; }
    }

    public static class ScoreCalculator
    {
        public const int MaxSupportedHan = 1000;

        public static ScoreResult Calculate(int fu, int han, int yakumanCount = 0)
        {
            if (fu < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fu), fu, "fu cannot be negative.");
            }

            if (han < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(han), han, "han cannot be negative.");
            }

            if (yakumanCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(yakumanCount), yakumanCount, "yakumanCount cannot be negative.");
            }

            if (han > MaxSupportedHan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(han),
                    han,
                    "han above " + MaxSupportedHan + " is rejected to keep exact integer diagnostics bounded.");
            }

            if (yakumanCount > 0)
            {
                long yakumanBase = checked(8000L * yakumanCount);
                return new ScoreResult(
                    fu,
                    0,
                    yakumanBase,
                    true,
                    yakumanCount,
                    yakumanBase,
                    yakumanBase.ToString(CultureInfo.InvariantCulture),
                    ScoreCapCodes.Yakuman);
            }

            BigInteger rawExact = BigInteger.Multiply(
                new BigInteger(fu),
                BigInteger.Pow(2, 2 + han));
            string rawExactText = rawExact.ToString(CultureInfo.InvariantCulture);

            long rawBasePoints = rawExact > long.MaxValue ? long.MaxValue : (long)rawExact;

            long basePoints;
            string? capCode;
            if (han >= 13)
            {
                basePoints = 8000;
                capCode = ScoreCapCodes.KazoeYakuman;
            }
            else if (han >= 11)
            {
                basePoints = 6000;
                capCode = ScoreCapCodes.Sanbaiman;
            }
            else if (han >= 8)
            {
                basePoints = 4000;
                capCode = ScoreCapCodes.Baiman;
            }
            else if (han >= 6)
            {
                basePoints = 3000;
                capCode = ScoreCapCodes.Haneman;
            }
            else if (han == 5 || (han == 4 && fu >= 40) || (han == 3 && fu >= 70))
            {
                basePoints = 2000;
                capCode = ScoreCapCodes.Mangan;
            }
            else if (rawExact > new BigInteger(2000))
            {
                basePoints = 2000;
                capCode = ScoreCapCodes.Mangan;
            }
            else
            {
                basePoints = rawBasePoints;
                capCode = null;
            }

            return new ScoreResult(
                fu,
                han,
                basePoints,
                false,
                0,
                rawBasePoints,
                rawExactText,
                capCode);
        }

        public static long RoundUpTo100(long value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "value cannot be negative.");
            }

            long remainder = value % 100;
            if (remainder == 0)
            {
                // Already a multiple of 100. This includes long.MaxValue - 7, whose
                // mathematical ceiling is itself; it must not be mistaken for overflow.
                return value;
            }

            long increment = 100 - remainder;
            if (value > long.MaxValue - increment)
            {
                throw new OverflowException("Rounding " + value + " up to a hundred would overflow Int64.");
            }

            return value + increment;
        }
    }
}

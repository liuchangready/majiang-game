using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public sealed class ScoringContractTests
    {
        private static readonly string[] FrozenYakuCodes =
        {
            "RIICHI", "IPPATSU", "MENZEN_TSUMO", "TANYAO", "PINFU", "IIPEIKO",
            "YAKUHAI_HAKU", "YAKUHAI_HATSU", "YAKUHAI_CHUN", "YAKUHAI_BAKAZE", "YAKUHAI_JIKAZE",
            "HAITEI", "HOUTEI", "RINSHAN", "CHANKAN",
            "DOUBLE_RIICHI", "CHANTA", "ITTSU", "SAN_SHOKU_DOUJUN", "SAN_SHOKU_DOUKOU",
            "TOITOI", "SAN_ANKOU", "SAN_KANTSU", "SHOUSANGEN", "HONROUTOU", "CHIITOITSU",
            "HONITSU", "JUNCHAN", "RYANPEIKOU", "CHINITSU",
            "KOKUSHI", "SUUANKOU", "DAISANGEN", "SHOUSUUSHII", "DAISUUSHII", "TSUUIISOU",
            "RYUUIISOU", "CHINROUTOU", "CHURENPOUTOU", "SUUKANTSU", "TENHOU", "CHIIHOU"
        };

        [Fact]
        public void YakuCatalogMatchesFrozenFortyTwoCodes()
        {
            Assert.Equal(42, Yaku.All.Count);
            Assert.Equal(42, FrozenYakuCodes.Length);
            Assert.Equal(FrozenYakuCodes.OrderBy(x => x), Yaku.All.Select(y => y.Code).OrderBy(x => x));
            Assert.Equal(42, Yaku.All.Select(y => y.Code).Distinct().Count());
            Assert.Equal(42, Yaku.All.Select(y => y.DisplayName).Distinct().Count());

            Assert.Equal(1, Yaku.Riichi.HanClosed);
            Assert.Equal(0, Yaku.Riichi.HanOpen);
            Assert.Equal(1, Yaku.Tanyao.HanOpen);
            Assert.Equal(6, Yaku.Chinitsu.HanClosed);
            Assert.Equal(5, Yaku.Chinitsu.HanOpen);
            Assert.True(Yaku.Kokushi.IsYakuman);
            Assert.Equal(0, Yaku.Kokushi.HanClosed);
            Assert.Equal("四暗刻", Yaku.FromCode("SUUANKOU")!.DisplayName);
            Assert.Null(Yaku.FromCode("NO_SUCH_YAKU"));
            Assert.True(Yaku.IsKnownCode("CHINITSU"));
        }

        [Fact]
        public void HandDecompositionRecognizesStandardChiitoitsuAndKokushi()
        {
            PlayerHand standard = new TestHand()
                .Seq(0, 1, 2).Seq(3, 4, 5).Seq(15, 16, 17).Trip(21).Pair(22)
                .Build();
            Assert.True(HandDecomposer.CanWin(standard.ClosedTiles));

            PlayerHand chiitoitsu = new TestHand()
                .Pair(0).Pair(2).Pair(13).Pair(24).Pair(27).Pair(29).Pair(31)
                .Build();
            Assert.True(HandDecomposer.CanWin(chiitoitsu.ClosedTiles));

            PlayerHand kokushi = new TestHand()
                .Add(0).Add(0).Add(8).Add(9).Add(17).Add(18).Add(26)
                .Add(27).Add(28).Add(29).Add(30).Add(31).Add(32).Add(33)
                .Build();
            Assert.True(HandDecomposer.CanWin(kokushi.ClosedTiles));

            PlayerHand incomplete = new TestHand()
                .Seq(0, 1, 2).Seq(3, 4, 5).Seq(15, 16, 17).Trip(21).Add(23)
                .Build();
            Assert.False(HandDecomposer.CanWin(incomplete.ClosedTiles));

            PlayerHand tenpai = new TestHand()
                .Seq(0, 1, 2).Seq(3, 4, 5).Seq(15, 16, 17).Trip(21).Add(22)
                .Build();
            Assert.Contains(22, HandDecomposer.CalculateWaits(tenpai.ClosedTiles));
        }

        [Theory]
        [InlineData(30, 1, 240L, 1000L, 1500L, null)]
        [InlineData(30, 2, 480L, 2000L, 2900L, null)]
        [InlineData(30, 3, 960L, 3900L, 5800L, null)]
        [InlineData(30, 4, 1920L, 7700L, 11600L, null)]
        [InlineData(30, 5, 2000L, 8000L, 12000L, "MANGAN")]
        [InlineData(40, 4, 2000L, 8000L, 12000L, "MANGAN")]
        [InlineData(70, 3, 2000L, 8000L, 12000L, "MANGAN")]
        [InlineData(60, 3, 1920L, 7700L, 11600L, null)]
        [InlineData(30, 6, 3000L, 12000L, 18000L, "HANEMAN")]
        [InlineData(30, 7, 3000L, 12000L, 18000L, "HANEMAN")]
        [InlineData(30, 8, 4000L, 16000L, 24000L, "BAIMAN")]
        [InlineData(30, 10, 4000L, 16000L, 24000L, "BAIMAN")]
        [InlineData(30, 11, 6000L, 24000L, 36000L, "SANBAIMAN")]
        [InlineData(30, 12, 6000L, 24000L, 36000L, "SANBAIMAN")]
        [InlineData(30, 13, 8000L, 32000L, 48000L, "KAZOE_YAKUMAN")]
        [InlineData(30, 25, 8000L, 32000L, 48000L, "KAZOE_YAKUMAN")]
        public void ScoreTiersAndRonPaymentsMatchV4(
            int fu,
            int han,
            long expectedBase,
            long expectedNonDealerRon,
            long expectedDealerRon,
            string? expectedCap)
        {
            ScoreResult result = ScoreCalculator.Calculate(fu, han);
            Assert.Equal(expectedBase, result.BasePoints);
            Assert.Equal(expectedCap, result.CapCode);
            Assert.Equal(expectedNonDealerRon, result.NonDealerRonPayment());
            Assert.Equal(expectedDealerRon, result.DealerRonPayment());
            Assert.False(result.IsYakuman);
            Assert.Equal(fu, result.Fu);
            Assert.Equal(han, result.Han);
        }

        [Fact]
        public void TsomoPaymentsAndPaymentPlansMatchV4()
        {
            ScoreResult mangan = ScoreCalculator.Calculate(30, 5);
            Assert.Equal(4000L, mangan.DealerTsumoPayment());
            NonDealerTsumoAmounts nonDealer = mangan.NonDealerTsumoPayment();
            Assert.Equal(4000L, nonDealer.Dealer);
            Assert.Equal(2000L, nonDealer.Other);

            PaymentPlan dealerPlan = mangan.PaymentPlan(isDealer: true, isTsumo: true);
            Assert.Equal(PaymentKind.DealerTsumo, dealerPlan.Kind);
            Assert.Equal("DEALER_TSUMO", dealerPlan.KindCode);
            Assert.Single(dealerPlan.Components);
            Assert.Equal(PayerRole.NonDealer, dealerPlan.Components[0].PayerRole);
            Assert.Equal(4000L, dealerPlan.Components[0].Amount);
            Assert.Equal(3, dealerPlan.Components[0].Count);
            Assert.Equal(12000L, dealerPlan.Total);

            PaymentPlan nonDealerPlan = mangan.PaymentPlan(isDealer: false, isTsumo: true);
            Assert.Equal(PaymentKind.NonDealerTsumo, nonDealerPlan.Kind);
            Assert.Equal(2, nonDealerPlan.Components.Count);
            Assert.Equal(8000L, nonDealerPlan.Total);
            Assert.Equal(8000L, mangan.TotalPoints(isDealer: false, isTsumo: true));

            Assert.Equal(12000L, ScoreCalculator.Calculate(30, 5).PaymentPlan(true, false).Total);
            Assert.Equal(8000L, ScoreCalculator.Calculate(30, 5).PaymentPlan(false, false).Total);
        }

        [Fact]
        public void YakumanAndMultiYakumanPaymentsMatchV4()
        {
            ScoreResult one = ScoreCalculator.Calculate(0, 0, 1);
            Assert.True(one.IsYakuman);
            Assert.Equal(8000L, one.BasePoints);
            Assert.Equal(32000L, one.NonDealerRonPayment());
            Assert.Equal(48000L, one.DealerRonPayment());
            Assert.Equal(16000L, one.DealerTsumoPayment());
            Assert.Equal(16000L, one.NonDealerTsumoPayment().Dealer);
            Assert.Equal(8000L, one.NonDealerTsumoPayment().Other);
            Assert.Equal("YAKUMAN", one.CapCode);

            ScoreResult two = ScoreCalculator.Calculate(0, 0, 2);
            Assert.Equal(16000L, two.BasePoints);
            Assert.Equal(64000L, two.NonDealerRonPayment());
            Assert.Equal(96000L, two.DealerRonPayment());
            Assert.Equal(2, two.YakumanMultiplier);
        }

        [Fact]
        public void FuBreakdownsMatchV4IncludingDoubleWindAndKans()
        {
            FuBreakdown chiitoitsu = FuCalculator.CalculateBreakdown(
                new Decomposition(Array.Empty<MentsuGroup>(), -1, DecompPattern.Chiitoitsu),
                TileId.FromTypeIndex(16, 0), true, true, 1, 1);
            Assert.Equal(25, chiitoitsu.FinalFu);
            Assert.Equal(25, chiitoitsu.Subtotal);
            Assert.Equal("七对子 25符", chiitoitsu.SpecialNote);

            FuBreakdown kokushi = FuCalculator.CalculateBreakdown(
                new Decomposition(Array.Empty<MentsuGroup>(), 0, DecompPattern.Kokushi),
                TileId.FromTypeIndex(0, 0), false, true, 1, 1);
            Assert.Equal(30, kokushi.FinalFu);
            Assert.Equal("国士无双 30符", kokushi.SpecialNote);

            FuBreakdown pinfuTsumo = FuCalculator.CalculateBreakdown(
                Decomp(22, Seq(0, 1, 2), Seq(21, 22, 23), Seq(15, 16, 17), Seq(10, 11, 12)),
                TileId.FromTypeIndex(12, 0), true, true, 2, 1);
            Assert.Equal(20, pinfuTsumo.FinalFu);
            Assert.Equal("平和自摸 20符", pinfuTsumo.SpecialNote);

            var openMelds = new[]
            {
                new Meld(MeldType.Chi, new[] { T(3), T(4), T(5) }, 1, T(3)),
                new Meld(MeldType.Chi, new[] { T(15), T(16), T(17) }, 1, T(15))
            };
            FuBreakdown openPinfu = FuCalculator.CalculateBreakdown(
                Decomp(10, Seq(0, 1, 2), Seq(19, 20, 21)),
                TileId.FromTypeIndex(21, 0), false, false, 1, 2, openMelds);
            Assert.Equal(20, openPinfu.Subtotal);
            Assert.Equal(30, openPinfu.FinalFu);
            Assert.Equal("荣和 30符保底", openPinfu.SpecialNote);

            var closedKan = new Meld(
                MeldType.ClosedKan,
                new[] { T(33), T(33, 1), T(33, 2), T(33, 3) },
                0,
                T(33));
            FuBreakdown complex = FuCalculator.CalculateBreakdown(
                Decomp(19, Trip(0), Trip(16), Seq(12, 13, 14), Seq(3, 4, 5)),
                TileId.FromTypeIndex(4, 0), true, false, 1, 1, new[] { closedKan });
            Assert.Equal(68, complex.Subtotal);
            Assert.Equal(70, complex.FinalFu);
            Assert.Contains(complex.Lines, l => l.Description == "暗刻1m" && l.Fu == 8);
            Assert.Contains(complex.Lines, l => l.Description == "暗刻8s" && l.Fu == 4);
            Assert.Contains(complex.Lines, l => l.Description == "暗杠中" && l.Fu == 32);
            Assert.Contains(complex.Lines, l => l.Description == "听牌嵌张" && l.Fu == 2);
            Assert.Contains(complex.Lines, l => l.Description == "自摸" && l.Fu == 2);

            FuBreakdown doubleWind = FuCalculator.CalculateBreakdown(
                Decomp(27, Seq(0, 1, 2), Seq(21, 22, 23), Seq(15, 16, 17), Seq(10, 11, 12)),
                TileId.FromTypeIndex(12, 0), false, true, 1, 1);
            Assert.Equal(4, doubleWind.Lines.Single(l => l.Description.StartsWith("雀头", StringComparison.Ordinal)).Fu);
            Assert.Equal(34, doubleWind.Subtotal);
            Assert.Equal(40, doubleWind.FinalFu);

            var kans = new[]
            {
                new Meld(MeldType.OpenKan, new[] { T(9), T(9, 1), T(9, 2), T(9, 3) }, 1, T(9)),
                new Meld(MeldType.ClosedKan, new[] { T(26), T(26, 1), T(26, 2), T(26, 3) }, 0, T(26)),
                new Meld(MeldType.AddedKan, new[] { T(2), T(2, 1), T(2, 2), T(2, 3) }, 1, T(2))
            };
            FuBreakdown kanFu = FuCalculator.CalculateBreakdown(
                Decomp(22, Seq(0, 1, 2), Seq(21, 22, 23), Seq(15, 16, 17), Seq(10, 11, 12)),
                TileId.FromTypeIndex(12, 0), false, false, 2, 2, kans);
            Assert.Equal(76, kanFu.Subtotal);
            Assert.Equal(80, kanFu.FinalFu);

            FuBreakdown penchan = FuCalculator.CalculateBreakdown(
                Decomp(22, Seq(0, 1, 2), Seq(21, 22, 23), Seq(15, 16, 17), Seq(10, 11, 12)),
                TileId.FromTypeIndex(2, 0), false, true, 2, 2);
            Assert.Equal(32, penchan.Subtotal);
            Assert.Equal(40, penchan.FinalFu);

            FuBreakdown single = FuCalculator.CalculateBreakdown(
                Decomp(22, Seq(0, 1, 2), Seq(21, 22, 23), Seq(15, 16, 17), Seq(10, 11, 12)),
                TileId.FromTypeIndex(22, 0), false, true, 2, 2);
            Assert.Equal(32, single.Subtotal);
            Assert.Equal(40, single.FinalFu);
        }

        [Fact]
        public void DoraStacksIndicatorUraAndRedWithoutChangingYakuRules()
        {
            var redFiveMan = new TestHand().Add(4, 3).Add(4, 0).Seq(18, 19, 20).Seq(15, 16, 17).Build();
            Assert.Equal(1, WinningScoreEvaluator.CountDora(redFiveMan, Array.Empty<TileId>()));
            DoraBreakdown withIndicator = WinningScoreEvaluator.BreakdownDora(
                redFiveMan,
                new[] { TileId.FromTypeIndex(3, 0) });
            Assert.Equal(2, withIndicator.IndicatorCount);
            Assert.Equal(1, withIndicator.RedCount);
            Assert.Equal(3, withIndicator.TotalHan);

            var repeated = new TestHand().Add(4).Add(4).Pon(20).Build();
            Assert.Equal(
                7,
                WinningScoreEvaluator.CountDora(
                    repeated,
                    new[]
                    {
                        TileId.FromTypeIndex(3, 0),
                        TileId.FromTypeIndex(3, 1),
                        TileId.FromTypeIndex(19, 0)
                    }));

            var redMeld = new TestHand().Add(22, 3).ChiRedFive(11).Build();
            DoraBreakdown meld = WinningScoreEvaluator.BreakdownDora(
                redMeld,
                new[] { TileId.FromTypeIndex(12, 0) });
            Assert.Equal(1, meld.IndicatorCount);
            Assert.Equal(2, meld.RedCount);
            Assert.Equal(3, meld.TotalHan);

            var uraHand = new TestHand().Add(19, 0).Add(16, 0).Build();
            DoraBreakdown ura = WinningScoreEvaluator.BreakdownDora(
                uraHand,
                new[] { TileId.FromTypeIndex(18, 0), TileId.FromTypeIndex(15, 0) },
                new[] { TileId.FromTypeIndex(18, 0), TileId.FromTypeIndex(15, 0) });
            Assert.Equal(2, ura.IndicatorCount);
            Assert.Equal(2, ura.UraCount);
            Assert.Equal(4, ura.TotalHan);
        }

        [Fact]
        public void WinningEvaluatorRegularInterpretationBeatsSevenPairs()
        {
            TestHand builder = new TestHand()
                .Add(0).Add(0).Add(1).Add(1).Add(2).Add(2)
                .Add(21).Add(21).Add(22).Add(22).Add(23).Add(23)
                .Add(15).Add(15);
            PlayerHand hand = builder.Build();
            TileId winning = TileId.FromTypeIndex(23, 1);
            Candidate result = WinningScoreEvaluator.Evaluate(
                hand,
                Ctx(winning, tsumo: false, closed: true, seat: 2, round: 1),
                isDealer: false) ?? throw new InvalidOperationException("expected a win");

            Assert.Equal(DecompPattern.Regular, result.Decomposition.Pattern);
            Assert.Equal(4, result.YakuResult.TotalHan);
            Assert.Equal(30, result.Fu);
            Assert.Equal(7700L, result.TotalPoints);
            Assert.Contains(result.YakuResult.YakuList, y => y.Code == "PINFU");
            Assert.Contains(result.YakuResult.YakuList, y => y.Code == "RYANPEIKOU");
        }

        [Fact]
        public void DoraAndRedFiveAloneNeverMakeAnOpenNoYakuHandLegal()
        {
            TestHand openHand = new TestHand()
                .Seq(3, 4, 5).Seq(21, 22, 23).Pair(9).Seq(9, 10, 11)
                .Chi(0, 1, 2);
            PlayerHand hand = openHand.Build();
            WinContext context = Ctx(TileId.FromTypeIndex(22, 0), tsumo: false, closed: false, seat: 2, round: 1);
            Assert.Null(WinningScoreEvaluator.Evaluate(hand, context, isDealer: false, extraHan: 10));

            TestHand redHand = new TestHand()
                .Add(22, 3).Add(23).Add(24).Seq(3, 4, 5).Seq(9, 10, 11).Pair(15)
                .Chi(0, 1, 2);
            PlayerHand red = redHand.Build();
            Assert.Null(WinningScoreEvaluator.Evaluate(
                red,
                Ctx(TileId.FromTypeIndex(15, 0), tsumo: false, closed: false, seat: 2, round: 1),
                isDealer: false));
        }

        [Fact]
        public void UraDoraIsOnlyIncludedWhenRiichi()
        {
            TestHand builder = new TestHand()
                .Seq(0, 1, 2).Seq(21, 22, 23).Seq(15, 16, 17).Seq(10, 11, 12).Pair(22);
            PlayerHand hand = builder.Build();
            TileId win = TileId.FromTypeIndex(12, 0);
            TileId[] dora = { TileId.FromTypeIndex(20, 0) };
            TileId[] ura = { TileId.FromTypeIndex(9, 0) };

            Candidate nonRiichi = WinningScoreEvaluator.Evaluate(
                hand,
                Ctx(win, tsumo: false, closed: true, seat: 2, round: 1),
                dora,
                ura,
                isDealer: false) ?? throw new InvalidOperationException();
            Assert.Equal(2, nonRiichi.ScoreResult.Han);

            Candidate riichi = WinningScoreEvaluator.Evaluate(
                hand,
                Ctx(win, tsumo: false, closed: true, seat: 2, round: 1, riichi: true),
                dora,
                ura,
                isDealer: false) ?? throw new InvalidOperationException();
            Assert.Equal(4, riichi.ScoreResult.Han);
        }

        [Fact]
        public void SettlementMatchesV4AndNeverMutatesInput()
        {
            ScoreResult score = ScoreCalculator.Calculate(30, 3);
            var scores = new List<long> { 25000, 25000, 25000, 25000 };
            var input = new SettlementInput(scores, 0, 2, 3);

            SettlementResult nonDealerRon = SettlementService.Ron(input, 1, 2, score);
            Assert.Equal(new long[] { 0, 7500, -4500, 0 }, nonDealerRon.Deltas.ToArray());
            Assert.Equal(0, nonDealerRon.RemainingRiichiSticks);

            SettlementResult dealerRon = SettlementService.Ron(input, 0, 2, score);
            Assert.Equal(new long[] { 9400, 0, -6400, 0 }, dealerRon.Deltas.ToArray());

            SettlementResult nonDealerTsumo = SettlementService.Tsumo(input, 1, score);
            Assert.Equal(new long[] { -2200, 7600, -1200, -1200 }, nonDealerTsumo.Deltas.ToArray());

            SettlementResult dealerTsumo = SettlementService.Tsumo(input, 0, score);
            Assert.Equal(new long[] { 9600, -2200, -2200, -2200 }, dealerTsumo.Deltas.ToArray());

            var noHonba = new SettlementInput(scores, 0, 0, 0);
            Assert.Equal(new long[] { 0, 3900, -3900, 0 }, SettlementService.Ron(noHonba, 1, 2, score).Deltas.ToArray());
            Assert.Equal(new long[] { -2000, 4000, -1000, -1000 }, SettlementService.Tsumo(noHonba, 1, score).Deltas.ToArray());

            for (int mask = 0; mask < 16; mask++)
            {
                var tenpai = Enumerable.Range(0, 4).Where(seat => (mask & (1 << seat)) != 0).ToArray();
                SettlementResult draw = SettlementService.ExhaustiveDraw(input, tenpai);
                int n = tenpai.Length;
                long[] expected = Enumerable.Range(0, 4).Select(seat =>
                {
                    if (n == 0 || n == 4)
                    {
                        return 0L;
                    }

                    return tenpai.Contains(seat) ? 3000L / n : -3000L / (4 - n);
                }).ToArray();
                Assert.Equal(expected, draw.Deltas.ToArray());
                Assert.Equal(3, draw.RemainingRiichiSticks);
                Assert.Equal(scores.Sum() + 3000, draw.NewScores.Sum() + (draw.RemainingRiichiSticks * 1000L));
            }

            Assert.Equal(new long[] { 0, 0, 0, 0 }, SettlementService.AbortiveDraw(input).Deltas.ToArray());
            Assert.Equal(new long[] { 25000, 25000, 25000, 25000 }, scores);
        }

        [Theory]
        [InlineData(0L, 0L)]
        [InlineData(1L, 100L)]
        [InlineData(99L, 100L)]
        [InlineData(100L, 100L)]
        [InlineData(101L, 200L)]
        [InlineData(9223372036854775799L, 9223372036854775800L)]
        [InlineData(9223372036854775800L, 9223372036854775800L)]
        public void RoundUpTo100UsesRemainderAndHandlesTheInt64Boundary(long value, long expected)
        {
            Assert.Equal(expected, ScoreCalculator.RoundUpTo100(value));
        }

        [Fact]
        public void RoundUpTo100ThrowsOnlyWhenTheNeededIncrementDoesNotFit()
        {
            const long maxMinusSeven = 9223372036854775800L; // 100 * 92233720368547758
            const long maxMinusSix = 9223372036854775801L;

            Assert.Equal(maxMinusSeven, ScoreCalculator.RoundUpTo100(maxMinusSeven));
            Assert.Equal(maxMinusSeven, ScoreCalculator.RoundUpTo100(maxMinusSeven - 1));
            Assert.Throws<OverflowException>(() => ScoreCalculator.RoundUpTo100(maxMinusSix));
        }

        [Fact]
        public void RejectedInputsThrowBeforeAnyStateOrPaymentChange()
        {
            var scores = new List<long> { 25000, 25000, 25000, 25000 };
            var input = new SettlementInput(scores, 0, 2, 3);
            ScoreResult score = ScoreCalculator.Calculate(30, 3);

            Assert.Throws<ArgumentException>(() => SettlementService.Ron(input, 1, 1, score));
            Assert.Throws<ArgumentOutOfRangeException>(() => SettlementService.Ron(input, 4, 1, score));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SettlementInput(scores, 4, 0, 0));
            Assert.Equal(new long[] { 25000, 25000, 25000, 25000 }, scores);

            Assert.Throws<ArgumentException>(() => new PlayerHand(new[] { T(0), T(0) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WinContext(T(0), true, true, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScoreCalculator.Calculate(30, 1001));
            Assert.Throws<OverflowException>(() => ScoreCalculator.RoundUpTo100(long.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScoreCalculator.Calculate(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScoreCalculator.Calculate(30, -1));
        }

        private static WinContext Ctx(
            TileId win,
            bool tsumo,
            bool closed,
            int seat,
            int round,
            bool riichi = false)
        {
            return new WinContext(win, tsumo, closed, seat, round, isRiichi: riichi);
        }

        private static Decomposition Decomp(int jantai, params MentsuGroup[] mentsu)
        {
            return new Decomposition(mentsu, jantai, DecompPattern.Regular);
        }

        private static MentsuGroup Seq(int a, int b, int c)
        {
            return new MentsuGroup(new[] { a, b, c }, isSequence: true);
        }

        private static MentsuGroup Trip(int tile)
        {
            return new MentsuGroup(new[] { tile, tile, tile }, isSequence: false);
        }

        private static TileId T(int typeIndex, int copy = 0)
        {
            return TileId.FromTypeIndex(typeIndex, copy);
        }
    }

    internal sealed class TestHand
    {
        private readonly HashSet<int> _used = new HashSet<int>();
        private readonly List<TileId> _closed = new List<TileId>();
        private readonly List<Meld> _melds = new List<Meld>();

        public TestHand Add(int typeIndex, int? copy = null)
        {
            _closed.Add(Allocate(typeIndex, copy));
            return this;
        }

        public TestHand Seq(int a, int b, int c)
        {
            Add(a);
            Add(b);
            Add(c);
            return this;
        }

        public TestHand Trip(int tile)
        {
            Add(tile);
            Add(tile);
            Add(tile);
            return this;
        }

        public TestHand Pair(int tile)
        {
            Add(tile);
            Add(tile);
            return this;
        }

        public TestHand Chi(params int[] types)
        {
            TileId[] tiles = types.Select(t => Allocate(t)).ToArray();
            _melds.Add(new Meld(MeldType.Chi, tiles, 1, tiles[0]));
            return this;
        }

        public TestHand ChiRedFive(int startType)
        {
            TileId[] tiles =
            {
                Allocate(startType),
                Allocate(startType + 1),
                Allocate(startType + 2, 3)
            };
            _melds.Add(new Meld(MeldType.Chi, tiles, 1, tiles[0]));
            return this;
        }

        public TestHand Pon(int tile)
        {
            TileId[] tiles = { Allocate(tile), Allocate(tile), Allocate(tile) };
            _melds.Add(new Meld(MeldType.Pon, tiles, 1, tiles[0]));
            return this;
        }

        public TestHand Kan(MeldType type, int tile)
        {
            TileId[] tiles = { Allocate(tile), Allocate(tile), Allocate(tile), Allocate(tile) };
            _melds.Add(new Meld(type, tiles, type == MeldType.ClosedKan ? 0 : 1, tiles[0]));
            return this;
        }

        public PlayerHand Build()
        {
            return new PlayerHand(_closed, _melds);
        }

        private TileId Allocate(int typeIndex, int? copy = null)
        {
            int chosen = copy ?? Enumerable.Range(0, 4).First(c => !_used.Contains((typeIndex * 4) + c));
            int id = (typeIndex * 4) + chosen;
            if (!_used.Add(id))
            {
                throw new ArgumentException("duplicate physical tile " + id);
            }

            return TileId.FromTypeIndex(typeIndex, chosen);
        }
    }
}

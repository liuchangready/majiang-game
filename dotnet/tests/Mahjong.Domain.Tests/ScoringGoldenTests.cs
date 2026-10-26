using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// Reads the JSON produced by the V4 test-only exporter and diffs every expected value
    /// against the C# Domain implementation. No expected value in this file is derived from
    /// the C# implementation itself.
    /// </summary>
    public sealed class ScoringGoldenTests
    {
        private static readonly Lazy<JsonDocument> Golden = new Lazy<JsonDocument>(LoadGolden);

        [Fact]
        public void GoldenFileCarriesRealV4ProvenanceAndAllSections()
        {
            JsonElement root = Golden.Value.RootElement;
            Assert.Equal("v5-scoring-golden-v1", root.GetProperty("schema").GetString());
            Assert.Equal(1, root.GetProperty("rulesetVersion").GetInt32());
            Assert.Equal(42, Yaku.All.Count);

            string commit = root.GetProperty("v4Commit").GetString()!;
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(c => Uri.IsHexDigit(c)), "v4Commit must be a real 40-hex git commit");
            Assert.Equal(10_000, root.GetProperty("randomSamples").GetArrayLength());
            Assert.True(root.GetProperty("winSamples").GetArrayLength() >= 40);
            Assert.True(root.GetProperty("scoreSamples").GetArrayLength() >= 147);
            Assert.True(root.GetProperty("fuSamples").GetArrayLength() >= 9);
            Assert.True(root.GetProperty("doraSamples").GetArrayLength() >= 6);
            Assert.True(root.GetProperty("settlementSamples").GetArrayLength() >= 24);
            Assert.True(root.GetProperty("noWinSamples").GetArrayLength() >= 4);
            Assert.Equal("exported", root.GetProperty("extractionStatus").GetString());

            JsonElement generation = root.GetProperty("generationResult");
            Assert.Equal(root.GetProperty("winSamples").GetArrayLength(), generation.GetProperty("winSamples").GetInt32());
            Assert.Equal(root.GetProperty("noWinSamples").GetArrayLength(), generation.GetProperty("noWinSamples").GetInt32());
            Assert.Equal(root.GetProperty("fuSamples").GetArrayLength(), generation.GetProperty("fuSamples").GetInt32());
            Assert.Equal(root.GetProperty("doraSamples").GetArrayLength(), generation.GetProperty("doraSamples").GetInt32());
            Assert.Equal(root.GetProperty("settlementSamples").GetArrayLength(), generation.GetProperty("settlementSamples").GetInt32());
            Assert.Equal(root.GetProperty("scoreSamples").GetArrayLength(), generation.GetProperty("scoreSamples").GetInt32());
            Assert.Equal(root.GetProperty("randomSamples").GetArrayLength(), generation.GetProperty("randomSamples").GetInt32());
            Assert.Equal(10_238, generation.GetProperty("totalSamples").GetInt32());
            Assert.Equal("PASSED", generation.GetProperty("exporterTestResult").GetString());

            // The exporter asserts this on the V4 side; this second read proves the JSON itself
            // contains all 42 frozen codes rather than merely claiming coverage.
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement sample in root.GetProperty("winSamples").EnumerateArray())
            {
                foreach (JsonElement yaku in sample.GetProperty("expected").GetProperty("yaku").EnumerateArray())
                {
                    codes.Add(yaku.GetProperty("code").GetString()!);
                }
            }

            Assert.Equal(42, codes.Count);
        }

        [Fact]
        public void CuratedWinAndNoWinSamplesMatchV4()
        {
            JsonElement root = Golden.Value.RootElement;
            RunSamples(root.GetProperty("winSamples"), CompareWinSample, "winSamples");
            RunSamples(root.GetProperty("noWinSamples"), CompareNoWinSample, "noWinSamples");
        }

        [Fact]
        public void ScoreTableSamplesMatchV4()
        {
            RunSamples(Golden.Value.RootElement.GetProperty("scoreSamples"), CompareScoreSample, "scoreSamples");
        }

        [Fact]
        public void FuSamplesMatchV4()
        {
            RunSamples(Golden.Value.RootElement.GetProperty("fuSamples"), CompareFuSample, "fuSamples");
        }

        [Fact]
        public void DoraSamplesMatchV4()
        {
            RunSamples(Golden.Value.RootElement.GetProperty("doraSamples"), CompareDoraSample, "doraSamples");
        }

        [Fact]
        public void SettlementSamplesMatchV4()
        {
            RunSamples(Golden.Value.RootElement.GetProperty("settlementSamples"), CompareSettlementSample, "settlementSamples");
        }

        [Fact]
        public void TenThousandRandomSamplesDifferentialAndDistribution()
        {
            JsonElement root = Golden.Value.RootElement;
            JsonElement samples = root.GetProperty("randomSamples");
            JsonElement meta = root.GetProperty("randomMeta");
            Assert.Equal(10_000, samples.GetArrayLength());
            Assert.Equal(10_000, meta.GetProperty("iterations").GetInt32());
            Assert.Equal(20261023L, meta.GetProperty("seed").GetInt64());

            var failures = new List<string>();
            var actualYaku = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int actualWin = 0;
            int actualNoWin = 0;
            int actualOpen = 0;
            int actualClosed = 0;
            int actualTsumo = 0;
            int actualRon = 0;
            int actualRemoved = 0;
            int actualYakuman = 0;

            foreach (JsonElement sample in samples.EnumerateArray())
            {
                string id = sample.GetProperty("id").GetString()!;
                try
                {
                    bool won = CompareRandomSample(sample, actualYaku, ref actualYakuman, out bool tsumo, out bool open, out bool removed);
                    if (won)
                    {
                        actualWin++;
                    }
                    else
                    {
                        actualNoWin++;
                    }

                    if (tsumo)
                    {
                        actualTsumo++;
                    }
                    else
                    {
                        actualRon++;
                    }

                    if (open)
                    {
                        actualOpen++;
                    }
                    else
                    {
                        actualClosed++;
                    }

                    if (removed)
                    {
                        actualRemoved++;
                    }
                }
                catch (Exception ex)
                {
                    if (failures.Count < 50)
                    {
                        failures.Add(id + ": " + ex.Message);
                    }
                }
            }

            Assert.True(failures.Count == 0, "random sample mismatches (first 50):\n" + string.Join("\n", failures));
            Assert.Equal(10_000, actualWin + actualNoWin);
            Assert.Equal(meta.GetProperty("win").GetInt32(), actualWin);
            Assert.Equal(meta.GetProperty("noWin").GetInt32(), actualNoWin);
            Assert.Equal(meta.GetProperty("openHands").GetInt32(), actualOpen);
            Assert.Equal(meta.GetProperty("closedHands").GetInt32(), actualClosed);
            Assert.Equal(meta.GetProperty("tsumo").GetInt32(), actualTsumo);
            Assert.Equal(meta.GetProperty("ron").GetInt32(), actualRon);
            Assert.Equal(meta.GetProperty("removedWinTile").GetInt32(), actualRemoved);
            Assert.Equal(meta.GetProperty("yakumanSamples").GetInt32(), actualYakuman);

            JsonElement expectedYaku = meta.GetProperty("yakuCounts");
            foreach (JsonProperty property in expectedYaku.EnumerateObject())
            {
                Assert.True(actualYaku.TryGetValue(property.Name, out int actual),
                    "random yaku count missing from actual run: " + property.Name);
                Assert.Equal(property.Value.GetInt32(), actual);
            }
        }

        private static void RunSamples(JsonElement array, Action<JsonElement> check, string section)
        {
            var failures = new List<string>();
            foreach (JsonElement sample in array.EnumerateArray())
            {
                try
                {
                    check(sample);
                }
                catch (Exception ex)
                {
                    if (failures.Count < 20)
                    {
                        failures.Add(sample.GetProperty("id").GetString() + ": " + ex.Message);
                    }
                }
            }

            Assert.True(failures.Count == 0, section + " mismatches (first 20):\n" + string.Join("\n", failures));
        }

        // Binding tests derive mutated samples from the same fixture and must run through the
        // exact production comparison paths above, not a second copy of the assertions.
        internal static JsonElement BindingGoldenRoot => Golden.Value.RootElement;

        internal static void CompareSampleForBindingTests(string section, JsonElement sample)
        {
            switch (section)
            {
                case "winSamples":
                    CompareWinSample(sample);
                    break;
                case "noWinSamples":
                    CompareNoWinSample(sample);
                    break;
                case "fuSamples":
                    CompareFuSample(sample);
                    break;
                case "doraSamples":
                    CompareDoraSample(sample);
                    break;
                case "settlementSamples":
                    CompareSettlementSample(sample);
                    break;
                case "scoreSamples":
                    CompareScoreSample(sample);
                    break;
                case "randomSamples":
                {
                    int yakumanCount = 0;
                    CompareRandomSample(
                        sample,
                        new SortedDictionary<string, int>(StringComparer.Ordinal),
                        ref yakumanCount,
                        out _,
                        out _,
                        out _);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(section), section, "unknown scoring golden section");
            }
        }

        private static void CompareWinSample(JsonElement sample)
        {
            JsonElement input = sample.GetProperty("input");
            PlayerHand hand = ReadHand(input);
            WinContext context = ReadContext(input.GetProperty("context"));
            TileId[] dora = ReadTileIds(input.GetProperty("doraIndicators"));
            TileId[] ura = ReadTileIds(input.GetProperty("uraDoraIndicators"));
            int honba = input.GetProperty("honba").GetInt32();
            int sticks = input.GetProperty("riichiSticks").GetInt32();
            JsonElement expected = sample.GetProperty("expected");
            // The exported status is part of the production contract: a win sample whose
            // expected status says anything else must fail here instead of being ignored.
            Assert.Equal("win", expected.GetProperty("status").GetString());
            long expectedHonba = expected.GetProperty("honbaAddition").GetInt64();
            long expectedRiichi = expected.GetProperty("riichiAddition").GetInt64();

            ScoreBreakdown result = WinningScoreEvaluator.Explain(
                hand,
                context,
                dora,
                ura,
                isDealer: context.SeatWind == 1,
                honba: honba,
                riichiSticks: sticks,
                loser: null) ?? throw new InvalidOperationException("expected a win but got null");

            CompareYaku(expected, result);
            Assert.Equal(expected.GetProperty("totalHan").GetInt32(), result.TotalYakuHan);
            Assert.Equal(expected.GetProperty("scoreHan").GetInt32(), result.Score.Han);
            Assert.Equal(expected.GetProperty("isYakuman").GetBoolean(), result.Score.IsYakuman);
            Assert.Equal(expected.GetProperty("yakumanCount").GetInt32(), result.YakumanCount);
            CompareDora(expected.GetProperty("dora"), result.Dora);
            CompareFu(expected.GetProperty("fu"), result.Fu);
            CompareScore(expected.GetProperty("score"), result.Score);
            ComparePaymentPlan(expected.GetProperty("paymentPlan"), result.PaymentPlan);
            Assert.Equal(expectedHonba, result.HonbaAddition);
            Assert.Equal(expectedRiichi, result.RiichiAddition);
            Assert.Equal(expected.GetProperty("totalPoints").GetInt64(), result.TotalPoints);
        }

        private static void CompareNoWinSample(JsonElement sample)
        {
            JsonElement input = sample.GetProperty("input");
            Assert.Equal("noWin", sample.GetProperty("expected").GetProperty("status").GetString());
            PlayerHand hand = ReadHand(input);
            WinContext context = ReadContext(input.GetProperty("context"));
            int extraHan = input.GetProperty("extraHan").GetInt32();
            Candidate? result = WinningScoreEvaluator.Evaluate(
                hand,
                context,
                isDealer: context.SeatWind == 1,
                extraHan: extraHan);
            Assert.Null(result);
        }

        private static void CompareScoreSample(JsonElement sample)
        {
            JsonElement input = sample.GetProperty("input");
            int fu = input.GetProperty("fu").GetInt32();
            int han = input.GetProperty("han").GetInt32();
            int yakuman = input.GetProperty("yakumanCount").GetInt32();
            ScoreResult actual = ScoreCalculator.Calculate(fu, han, yakuman);
            JsonElement expected = sample.GetProperty("expected");

            Assert.Equal(expected.GetProperty("basePoints").GetInt64(), actual.BasePoints);
            Assert.Equal(expected.GetProperty("rawBasePoints").GetInt64(), actual.RawBasePoints);
            Assert.Equal(expected.GetProperty("rawBasePointsExact").GetString(), actual.RawBasePointsExact);
            Assert.Equal(NullableString(expected.GetProperty("capCode")), actual.CapCode);
            Assert.Equal(expected.GetProperty("isYakuman").GetBoolean(), actual.IsYakuman);
            Assert.Equal(expected.GetProperty("yakumanMultiplier").GetInt32(), actual.YakumanMultiplier);

            JsonElement payments = expected.GetProperty("payments");
            Assert.Equal(payments.GetProperty("dealerTsumo").GetInt64(), actual.DealerTsumoPayment());
            Assert.Equal(payments.GetProperty("nonDealerTsumoDealer").GetInt64(), actual.NonDealerTsumoPayment().Dealer);
            Assert.Equal(payments.GetProperty("nonDealerTsumoOther").GetInt64(), actual.NonDealerTsumoPayment().Other);
            Assert.Equal(payments.GetProperty("dealerRon").GetInt64(), actual.DealerRonPayment());
            Assert.Equal(payments.GetProperty("nonDealerRon").GetInt64(), actual.NonDealerRonPayment());

            JsonElement plans = expected.GetProperty("paymentPlans");
            ComparePaymentPlan(plans.GetProperty("dealerTsumo"), actual.PaymentPlan(true, true));
            ComparePaymentPlan(plans.GetProperty("nonDealerTsumo"), actual.PaymentPlan(false, true));
            ComparePaymentPlan(plans.GetProperty("dealerRon"), actual.PaymentPlan(true, false));
            ComparePaymentPlan(plans.GetProperty("nonDealerRon"), actual.PaymentPlan(false, false));
        }

        private static void CompareFuSample(JsonElement sample)
        {
            JsonElement input = sample.GetProperty("input");
            Decomposition decomposition = ReadDecomposition(input.GetProperty("decomposition"));
            TileId winTile = new TileId(input.GetProperty("winTile").GetInt32());
            bool tsumo = input.GetProperty("isTsumo").GetBoolean();
            bool closed = input.GetProperty("isClosed").GetBoolean();
            int seat = input.GetProperty("seatWind").GetInt32();
            int round = input.GetProperty("roundWind").GetInt32();
            Meld[] melds = input.GetProperty("openMelds").EnumerateArray().Select(ReadMeld).ToArray();
            FuBreakdown actual = FuCalculator.CalculateBreakdown(
                decomposition,
                winTile,
                tsumo,
                closed,
                seat,
                round,
                melds);
            CompareFu(sample.GetProperty("expected"), actual);
        }

        private static void CompareDoraSample(JsonElement sample)
        {
            JsonElement input = sample.GetProperty("input");
            TileId[] closed = ReadTileIds(input.GetProperty("closedTiles"));
            TileId? drawn = input.GetProperty("drawnTile").ValueKind == JsonValueKind.Null
                ? (TileId?)null
                : new TileId(input.GetProperty("drawnTile").GetInt32());
            Meld[] melds = input.GetProperty("melds").EnumerateArray().Select(ReadMeld).ToArray();
            PlayerHand hand = new PlayerHand(closed, melds, drawn);
            DoraBreakdown actual = WinningScoreEvaluator.BreakdownDora(
                hand,
                ReadTileIds(input.GetProperty("doraIndicators")),
                ReadTileIds(input.GetProperty("uraDoraIndicators")));
            CompareDora(sample.GetProperty("expected"), actual);
        }

        private static void CompareSettlementSample(JsonElement sample)
        {
            JsonElement input = sample.GetProperty("input");
            var scores = input.GetProperty("scores").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            int dealer = input.GetProperty("dealer").GetInt32();
            int honba = input.GetProperty("honba").GetInt32();
            int sticks = input.GetProperty("riichiSticks").GetInt32();
            var settlementInput = new SettlementInput(scores, dealer, honba, sticks);
            ScoreResult score = ScoreCalculator.Calculate(
                input.GetProperty("fu").GetInt32(),
                input.GetProperty("han").GetInt32());
            string kind = input.GetProperty("settlementKind").GetString()!;
            SettlementResult actual;
            switch (kind)
            {
                case "ron":
                    actual = SettlementService.Ron(
                        settlementInput,
                        input.GetProperty("winner").GetInt32(),
                        input.GetProperty("loser").GetInt32(),
                        score);
                    break;
                case "tsumo":
                    actual = SettlementService.Tsumo(
                        settlementInput,
                        input.GetProperty("winner").GetInt32(),
                        score);
                    break;
                case "exhaustiveDraw":
                    actual = SettlementService.ExhaustiveDraw(
                        settlementInput,
                        input.GetProperty("tenpai").EnumerateArray().Select(e => e.GetInt32()));
                    break;
                case "abortive":
                    actual = SettlementService.AbortiveDraw(settlementInput);
                    break;
                default:
                    throw new InvalidOperationException("unknown settlement kind: " + kind);
            }

            JsonElement expected = sample.GetProperty("expected");
            Assert.Equal(expected.GetProperty("deltas").EnumerateArray().Select(e => e.GetInt64()).ToArray(), actual.Deltas.ToArray());
            Assert.Equal(expected.GetProperty("newScores").EnumerateArray().Select(e => e.GetInt64()).ToArray(), actual.NewScores.ToArray());
            Assert.Equal(expected.GetProperty("remainingRiichiSticks").GetInt32(), actual.RemainingRiichiSticks);
        }

        private static bool CompareRandomSample(
            JsonElement sample,
            SortedDictionary<string, int> yakuCounts,
            ref int yakumanCount,
            out bool tsumo,
            out bool open,
            out bool removed)
        {
            TileId[] closed = ReadTileIds(sample.GetProperty("closed"));
            TileId? drawn = sample.GetProperty("drawn").ValueKind == JsonValueKind.Null
                ? (TileId?)null
                : new TileId(sample.GetProperty("drawn").GetInt32());
            Meld[] melds = sample.GetProperty("melds").EnumerateArray().Select(ReadMeld).ToArray();
            PlayerHand hand = new PlayerHand(closed, melds, drawn);
            WinContext context = ReadContext(sample.GetProperty("context"));
            TileId[] dora = ReadTileIds(sample.GetProperty("dora"));
            TileId[] ura = ReadTileIds(sample.GetProperty("ura"));
            tsumo = context.IsTsumo;
            open = melds.Any(m => m.IsOpen);
            removed = sample.GetProperty("removedWinTile").GetBoolean();

            JsonElement expected = sample.GetProperty("expected");
            string status = expected.GetProperty("status").GetString()!;
            ScoreBreakdown? result = WinningScoreEvaluator.Explain(
                hand,
                context,
                dora,
                ura,
                isDealer: context.SeatWind == 1);
            if (status == "noWin")
            {
                Assert.Null(result);
                return false;
            }

            Assert.Equal("win", status);
            Assert.NotNull(result);
            ScoreBreakdown actual = result!;
            JsonElement yaku = expected.GetProperty("yaku");
            Assert.Equal(yaku.GetArrayLength(), actual.Yaku.Count);
            int index = 0;
            foreach (JsonElement entry in yaku.EnumerateArray())
            {
                Assert.Equal(entry.GetProperty("code").GetString(), actual.Yaku[index].Code);
                Assert.Equal(entry.GetProperty("han").GetInt32(), actual.Yaku[index].Han);
                index++;
            }

            foreach (YakuHit hit in actual.Yaku)
            {
                yakuCounts[hit.Code] = yakuCounts.TryGetValue(hit.Code, out int count) ? count + 1 : 1;
            }

            if (actual.Score.IsYakuman)
            {
                yakumanCount++;
            }

            Assert.Equal(expected.GetProperty("yakuHan").GetInt32(), actual.TotalYakuHan);
            Assert.Equal(expected.GetProperty("scoreHan").GetInt32(), actual.Score.Han);
            Assert.Equal(expected.GetProperty("isYakuman").GetBoolean(), actual.Score.IsYakuman);
            Assert.Equal(expected.GetProperty("yakumanCount").GetInt32(), actual.YakumanCount);
            Assert.Equal(expected.GetProperty("fu").GetInt32(), actual.Fu.FinalFu);
            Assert.Equal(expected.GetProperty("basePoints").GetInt64(), actual.Score.BasePoints);
            Assert.Equal(NullableString(expected.GetProperty("capCode")), actual.Score.CapCode);
            Assert.Equal(expected.GetProperty("totalPoints").GetInt64(), actual.PaymentPlan.Total);
            return true;
        }

        private static PlayerHand ReadHand(JsonElement input)
        {
            TileId[] closed = ReadTileIds(input.GetProperty("closedTiles"));
            TileId? drawn = input.GetProperty("drawnTile").ValueKind == JsonValueKind.Null
                ? (TileId?)null
                : new TileId(input.GetProperty("drawnTile").GetInt32());
            Meld[] melds = input.GetProperty("melds").EnumerateArray().Select(ReadMeld).ToArray();
            return new PlayerHand(closed, melds, drawn);
        }

        private static Meld ReadMeld(JsonElement element)
        {
            MeldType type = element.GetProperty("type").GetString() switch
            {
                "CHI" => MeldType.Chi,
                "PON" => MeldType.Pon,
                "OPEN_KAN" => MeldType.OpenKan,
                "CLOSED_KAN" => MeldType.ClosedKan,
                "ADDED_KAN" => MeldType.AddedKan,
                var value => throw new InvalidOperationException("unknown meld type: " + value)
            };
            TileId[] tiles = ReadTileIds(element.GetProperty("tiles"));
            int fromPlayer = element.GetProperty("fromPlayer").GetInt32();
            TileId called = new TileId(element.GetProperty("calledTile").GetInt32());
            return new Meld(type, tiles, fromPlayer, called);
        }

        private static Decomposition ReadDecomposition(JsonElement element)
        {
            DecompPattern pattern = element.GetProperty("pattern").GetString() switch
            {
                "REGULAR" => DecompPattern.Regular,
                "CHIITOITSU" => DecompPattern.Chiitoitsu,
                "KOKUSHI" => DecompPattern.Kokushi,
                var value => throw new InvalidOperationException("unknown decomposition pattern: " + value)
            };
            int jantai = element.GetProperty("jantai").GetInt32();
            MentsuGroup[] mentsu = element.GetProperty("mentsu").EnumerateArray().Select(m =>
            {
                int[] indices = m.GetProperty("typeIndices").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                return new MentsuGroup(indices, m.GetProperty("isSequence").GetBoolean());
            }).ToArray();
            return new Decomposition(mentsu, jantai, pattern);
        }

        private static WinContext ReadContext(JsonElement element)
        {
            return new WinContext(
                new TileId(element.GetProperty("winTile").GetInt32()),
                element.GetProperty("isTsumo").GetBoolean(),
                element.GetProperty("isClosed").GetBoolean(),
                element.GetProperty("seatWind").GetInt32(),
                element.GetProperty("roundWind").GetInt32(),
                element.GetProperty("isRiichi").GetBoolean(),
                element.GetProperty("isDoubleRiichi").GetBoolean(),
                element.GetProperty("isIppatsu").GetBoolean(),
                element.GetProperty("isRinshan").GetBoolean(),
                element.GetProperty("isHaitei").GetBoolean(),
                element.GetProperty("isHoutei").GetBoolean(),
                element.GetProperty("isChankan").GetBoolean(),
                element.GetProperty("isTenhou").GetBoolean(),
                element.GetProperty("isChiihou").GetBoolean());
        }

        private static TileId[] ReadTileIds(JsonElement element)
        {
            return element.EnumerateArray().Select(e => new TileId(e.GetInt32())).ToArray();
        }

        private static void CompareYaku(JsonElement expected, ScoreBreakdown actual)
        {
            JsonElement yaku = expected.GetProperty("yaku");
            Assert.Equal(yaku.GetArrayLength(), actual.Yaku.Count);
            int index = 0;
            foreach (JsonElement entry in yaku.EnumerateArray())
            {
                Assert.Equal(entry.GetProperty("code").GetString(), actual.Yaku[index].Code);
                Assert.Equal(entry.GetProperty("displayName").GetString(), actual.Yaku[index].DisplayName);
                Assert.Equal(entry.GetProperty("han").GetInt32(), actual.Yaku[index].Han);
                index++;
            }
        }

        private static void CompareDora(JsonElement expected, DoraBreakdown actual)
        {
            Assert.Equal(expected.GetProperty("indicatorCount").GetInt32(), actual.IndicatorCount);
            Assert.Equal(expected.GetProperty("uraCount").GetInt32(), actual.UraCount);
            Assert.Equal(expected.GetProperty("redCount").GetInt32(), actual.RedCount);
            Assert.Equal(expected.GetProperty("totalHan").GetInt32(), actual.TotalHan);
            Assert.Equal(ReadTileIds(expected.GetProperty("indicatorTiles")), actual.IndicatorTiles.ToArray());
            Assert.Equal(ReadTileIds(expected.GetProperty("uraTiles")), actual.UraTiles.ToArray());
            Assert.Equal(ReadTileIds(expected.GetProperty("redTiles")), actual.RedTiles.ToArray());
        }

        private static void CompareFu(JsonElement expected, FuBreakdown actual)
        {
            JsonElement lines = expected.GetProperty("lines");
            Assert.Equal(lines.GetArrayLength(), actual.Lines.Count);
            int index = 0;
            foreach (JsonElement line in lines.EnumerateArray())
            {
                Assert.Equal(line.GetProperty("description").GetString(), actual.Lines[index].Description);
                Assert.Equal(line.GetProperty("fu").GetInt32(), actual.Lines[index].Fu);
                index++;
            }

            Assert.Equal(expected.GetProperty("subtotal").GetInt32(), actual.Subtotal);
            Assert.Equal(expected.GetProperty("roundedUpTo").GetInt32(), actual.RoundedUpTo);
            Assert.Equal(expected.GetProperty("finalFu").GetInt32(), actual.FinalFu);
            Assert.Equal(NullableString(expected.GetProperty("specialNote")), actual.SpecialNote);
        }

        private static void CompareScore(JsonElement expected, ScoreResult actual)
        {
            Assert.Equal(expected.GetProperty("fu").GetInt32(), actual.Fu);
            Assert.Equal(expected.GetProperty("han").GetInt32(), actual.Han);
            Assert.Equal(expected.GetProperty("basePoints").GetInt64(), actual.BasePoints);
            Assert.Equal(expected.GetProperty("rawBasePoints").GetInt64(), actual.RawBasePoints);
            Assert.Equal(expected.GetProperty("rawBasePointsExact").GetString(), actual.RawBasePointsExact);
            Assert.Equal(NullableString(expected.GetProperty("capCode")), actual.CapCode);
            Assert.Equal(expected.GetProperty("isYakuman").GetBoolean(), actual.IsYakuman);
            Assert.Equal(expected.GetProperty("yakumanMultiplier").GetInt32(), actual.YakumanMultiplier);
        }

        private static void ComparePaymentPlan(JsonElement expected, PaymentPlan actual)
        {
            Assert.Equal(expected.GetProperty("kind").GetString(), actual.KindCode);
            JsonElement components = expected.GetProperty("components");
            Assert.Equal(components.GetArrayLength(), actual.Components.Count);
            int index = 0;
            foreach (JsonElement component in components.EnumerateArray())
            {
                Assert.Equal(component.GetProperty("payerRole").GetString(), RoleCode(actual.Components[index].PayerRole));
                Assert.Equal(component.GetProperty("amount").GetInt64(), actual.Components[index].Amount);
                Assert.Equal(component.GetProperty("count").GetInt32(), actual.Components[index].Count);
                index++;
            }

            Assert.Equal(expected.GetProperty("total").GetInt64(), actual.Total);
        }

        private static string RoleCode(PayerRole role)
        {
            return role switch
            {
                PayerRole.Dealer => "DEALER",
                PayerRole.NonDealer => "NON_DEALER",
                PayerRole.Discarder => "DISCARDER",
                _ => throw new InvalidOperationException("unknown payer role")
            };
        }

        private static string? NullableString(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Null ? null : element.GetString();
        }

        private static JsonDocument LoadGolden()
        {
            string path = FindGoldenPath();
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        private static string FindGoldenPath([CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(overrideRoot!, "tests", "v5-golden", "scoring-v1.json");
                if (File.Exists(overridden))
                {
                    return overridden;
                }
            }

            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(sourceFile) ?? string.Empty })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(directory.FullName, "tests", "v5-golden", "scoring-v1.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException(
                "tests/v5-golden/scoring-v1.json was not found from " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() + "; set MAHJONG_REPO_ROOT if the test binaries live outside the repository.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// Differential evidence for the new authoritative entry point against the frozen
    /// V4-exported scoring/round samples. The entry only accepts physically coherent tables;
    /// samples whose dora/ura/win-tile ids duplicate a hand tile or whose flags cannot be
    /// represented by the current round state are skipped and counted, never silently
    /// rewritten. This is not claimed as full 10,238-case coverage of the entry.
    /// </summary>
    public sealed class RoundSettlementGoldenTests
    {
        private static readonly Lazy<JsonDocument> Scoring = new Lazy<JsonDocument>(
            () => LoadGolden("scoring-v1.json"));
        private static readonly Lazy<JsonDocument> Round = new Lazy<JsonDocument>(
            () => LoadGolden("round-v1.json"));
        private static readonly PersistedUtcTime FixedUtc =
            new PersistedUtcTime(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));

        [Fact]
        public void RepresentableV4ScoringWinSamplesFlowThroughNewEntryAndExecute()
        {
            var failures = new List<string>();
            JsonElement samples = Scoring.Value.RootElement.GetProperty("randomSamples");
            int checkedSamples = 0;
            int skipped = 0;
            int index = 0;
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                index++;
                if (checkedSamples >= 300)
                {
                    break;
                }

                if (sample.GetProperty("expected").GetProperty("status").GetString() != "win")
                {
                    continue;
                }

                if (!TryMap(sample, out MappedSample mapped, out string skipReason))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    RoundStateMachine round = BuildRound(mapped, out RoundSettlementFacts facts);
                    RoundSettlementCalculation calc = mapped.IsTsumo
                        ? RoundSettlementCalculator.CalculateTsumo(round, facts)
                        : RoundSettlementCalculator.CalculateRon(round, mapped.Winner, facts);

                    AssertBreakdown(mapped.Expected, calc);
                    long[] expectedFinals = ExpectedFinals(
                        calc.Breakdown!.PaymentPlan,
                        mapped.Winner,
                        mapped.Dealer,
                        mapped.Loser);
                    Assert.Equal(expectedFinals, calc.Settlement.FinalScores.ToArray());
                    Assert.Equal(mapped.Expected.GetProperty("totalPoints").GetInt64(), calc.Payments.Deltas[mapped.Winner]);

                    RoundTransition applied = calc.Execute(round);
                    Assert.True(applied.IsAccepted, applied.Error?.ToString());
                    Assert.True(applied.State!.IsFinished);
                    Assert.Equal(expectedFinals, applied.State.Result!.FinalScores.ToArray());
                    checkedSamples++;
                }
                catch (Exception ex)
                {
                    if (failures.Count < 20)
                    {
                        failures.Add(mapped.Id + ": " + ex.Message);
                    }
                }
            }

            Console.WriteLine(
                "[N2-GOLDEN] scoring win checked=" + checkedSamples +
                " skipped=" + skipped + " scanned=" + index);
            Assert.True(failures.Count == 0, string.Join("\n", failures));
            Assert.True(checkedSamples >= 100, "Only " + checkedSamples + " mappable win samples were checked.");        }

        [Fact]
        public void RepresentableV4ScoringNoWinSamplesAreRejectedByNewEntry()
        {
            var failures = new List<string>();
            JsonElement samples = Scoring.Value.RootElement.GetProperty("randomSamples");
            int checkedSamples = 0;
            int skipped = 0;
            int index = 0;
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                index++;
                if (checkedSamples >= 100)
                {
                    break;
                }

                if (sample.GetProperty("expected").GetProperty("status").GetString() != "noWin")
                {
                    continue;
                }

                if (!TryMap(sample, out MappedSample mapped, out _))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    RoundStateMachine round = BuildRound(mapped, out RoundSettlementFacts facts);
                    Assert.Throws<RoundSettlementRejectedException>(
                        () => mapped.IsTsumo
                            ? RoundSettlementCalculator.CalculateTsumo(round, facts)
                            : RoundSettlementCalculator.CalculateRon(round, mapped.Winner, facts));
                    checkedSamples++;
                }
                catch (Exception ex)
                {
                    if (failures.Count < 20)
                    {
                        failures.Add(mapped.Id + ": " + ex.Message);
                    }
                }
            }

            Console.WriteLine(
                "[N2-GOLDEN] scoring noWin checked=" + checkedSamples +
                " skipped=" + skipped + " scanned=" + index);
            Assert.True(failures.Count == 0, string.Join("\n", failures));
            Assert.True(checkedSamples >= 50, "Only " + checkedSamples + " mappable no-win samples were checked.");
        }

        [Fact]
        public void V4RoundTerminalSettlementFieldsMatchNewEntry()
        {
            JsonElement section = Round.Value.RootElement.GetProperty("roundTerminal");
            JsonElement input = section.GetProperty("input");
            JsonElement settlement = section.GetProperty("settlement");
            JsonElement expected = section.GetProperty("expected");

            RoundSpec spec = new RoundSpec(
                input.GetProperty("roundId").GetString()!,
                input.GetProperty("dealer").GetInt32(),
                1,
                0,
                0,
                ReadLongs(input.GetProperty("scores")),
                input.GetProperty("riichiSticks").GetInt32());
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            plan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            RoundStateMachine round = TablePlan.Build(spec, plan);
            var facts = new RoundSettlementFacts(
                settlement.GetProperty("resultId").GetString()!,
                new PersistedUtcTime(DateTimeOffset.Parse(
                    settlement.GetProperty("completedAtUtc").GetString()!,
                    CultureInfo.InvariantCulture)),
                settlement.GetProperty("completedAtMonotonicMillis").GetInt64(),
                new[] { new RoundSettlementSeatFacts(0) });

            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateTsumo(round, facts);
            Assert.Equal(ReadLongs(expected.GetProperty("resultFinalScores")), calc.Settlement.FinalScores.ToArray());
            Assert.Equal(ReadLongs(expected.GetProperty("resultDeltas")), calc.Payments.Deltas.ToArray());
            Assert.Equal(expected.GetProperty("resultOutcome").GetString(), calc.Outcome.Kind.ToString());

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            RoundStateMachine finished = applied.State!;
            Assert.Equal(expected.GetProperty("versionAfter").GetInt64(), finished.Version);
            Assert.Equal(expected.GetProperty("phaseAfter").GetString(), PhaseCode(finished.Phase));
            Assert.Equal(
                ReadLongs(expected.GetProperty("resultFinalScores")),
                finished.Result!.FinalScores.ToArray());
            Assert.Equal(
                ReadLongs(expected.GetProperty("resultDeltas")),
                finished.Result.TotalDeltas.ToArray());
            Assert.Equal(settlement.GetProperty("resultId").GetString(), finished.Result.ResultId);

            string[] eventTypes = expected.GetProperty("eventTypes")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Contains("TsumoWin", eventTypes);
            Assert.Contains("RoundEnded", eventTypes);
            Assert.Contains(finished.Result.Events, e => e.Type == RoundEventType.TsumoWin);
            Assert.Contains(finished.Result.Events, e => e.Type == RoundEventType.RoundEnded);
            // V4's ScoreSettled event has no C# Domain counterpart in this frozen head; the
            // authoritative payment/delta data is carried by RoundResult/Payments instead.
            Assert.DoesNotContain(finished.Result.Events, e => e.Type.ToString() == "ScoreSettled");
        }

        [Fact]
        public void V4RoundChankanFieldsMatchNewEntryOnRepresentableSubset()
        {
            JsonElement section = Round.Value.RootElement.GetProperty("chankanRon");
            JsonElement input = section.GetProperty("input");
            JsonElement expected = section.GetProperty("expected");

            RoundSpec spec = new RoundSpec(
                input.GetProperty("roundId").GetString()!,
                input.GetProperty("kanserSeat").GetInt32(),
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            var pon = new Meld(
                MeldType.Pon,
                new[] { new TileId(17), new TileId(18), new TileId(19) },
                3,
                new TileId(17));
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true,
                DrawnTile = new TileId(16),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            plan.AddHand(0, 16, 0, 1, 2, 3, 5, 6, 7, 9, 10, 11);
            plan.AddMeld(0, pon);
            plan.AddHand(1, 4, 8, 12, 20, 24, 28, 76, 80, 84, 40, 41, 13, 21);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            RoundTransition kan = round.Execute(
                RoundCommand.AddedKan(round.RoundId, 0, new TileId(16), new[] { 1 }));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            round = kan.State!;

            var facts = new RoundSettlementFacts(
                expected.GetProperty("resultId").GetString()!,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)),
                1000,
                new[] { new RoundSettlementSeatFacts(1, isChankan: true, discardHistory: Array.Empty<TileId>()) });
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateRon(round, 1, facts);

            Assert.Equal(ReadLongs(expected.GetProperty("finalScores")), calc.Settlement.FinalScores.ToArray());
            Assert.Equal(ReadLongs(expected.GetProperty("totalDeltas")), calc.Payments.Deltas.ToArray());
            Assert.Equal(1, calc.Outcome.WinnerSeat);
            Assert.Equal(0, calc.Outcome.LoserSeat);

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            RoundStateMachine finished = applied.State!;
            Assert.Equal(expected.GetProperty("versionAfterRon").GetInt64(), finished.Version);
            Assert.Equal(RoundEndReason.ChankanRon, finished.Result!.EndReason);
            Assert.Equal(ReadLongs(expected.GetProperty("finalScores")), finished.Result.FinalScores.ToArray());
            Assert.Equal(ReadLongs(expected.GetProperty("totalDeltas")), finished.Result.TotalDeltas.ToArray());
            Assert.DoesNotContain(finished.Result.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.DoesNotContain(finished.Result.Events, e => e.Type == RoundEventType.RinshanDrawn);

            string[] eventTypes = expected.GetProperty("eventTypes")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Contains("RonWin", eventTypes);
            Assert.Contains("RoundEnded", eventTypes);
            Assert.Contains(finished.Result.Events, e => e.Type == RoundEventType.RonWin);
            Assert.Contains(finished.Result.Events, e => e.Type == RoundEventType.RoundEnded);
            Assert.DoesNotContain(finished.Result.Events, e => e.Type.ToString() == "ScoreSettled");
        }

        private static RoundStateMachine BuildRound(MappedSample mapped, out RoundSettlementFacts facts)
        {
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                FillRemainingIntoWall = true
            };
            var used = new HashSet<int>();
            foreach (TileId tile in mapped.Closed)
            {
                used.Add(tile.Value);
            }

            foreach (Meld meld in mapped.Melds)
            {
                foreach (TileId tile in meld.Tiles)
                {
                    used.Add(tile.Value);
                }
            }

            used.Add(mapped.WinTile.Value);
            foreach (TileId tile in mapped.Dora)
            {
                used.Add(tile.Value);
            }

            foreach (TileId tile in mapped.Ura)
            {
                used.Add(tile.Value);
            }

            bool directDealerTsumo = mapped.IsTsumo && !mapped.RouteDraw;
            if (!directDealerTsumo)
            {
                plan.AddHand(mapped.Winner, mapped.Closed.Select(t => t.Value).ToArray());
            }

            foreach (Meld meld in mapped.Melds)
            {
                plan.AddMeld(mapped.Winner, meld);
            }

            if (mapped.Dora.Length > 0)
            {
                plan.AddDora(mapped.Dora.Select(t => t.Value).ToArray());
            }

            if (mapped.Ura.Length > 0)
            {
                plan.AddUra(mapped.Ura.Select(t => t.Value).ToArray());
            }

            var spec = new RoundSpec(
                "golden-" + mapped.Id,
                mapped.Dealer,
                mapped.RoundWind,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);

            if (mapped.IsTsumo)
            {
                if (mapped.RouteDraw)
                {
                    plan.AddHand(mapped.Dealer, PickUnused(used, 14));
                    plan.AddWallTop(mapped.WinTile.Value);
                    RoundStateMachine dealt = TablePlan.Build(spec, plan);
                    RoundTransition discard = dealt.Execute(
                        RoundCommand.Discard(dealt.RoundId, mapped.Dealer, new TileId(PickDiscardTile(dealt))));
                    if (!discard.IsAccepted)
                    {
                        throw new InvalidOperationException("Route discard rejected: " + discard.Error);
                    }

                    facts = BuildFacts(mapped);
                    return discard.State!;
                }

                plan.DrawnTile = mapped.WinTile;
                plan.DrawnSeat = mapped.Dealer;
                plan.DrawnSource = DrawSource.Wall;
                int[] hand = mapped.Closed.Select(t => t.Value).Concat(new[] { mapped.WinTile.Value }).ToArray();
                plan.AddHand(mapped.Dealer, hand);
                facts = BuildFacts(mapped);
                return TablePlan.Build(spec, plan);
            }

            int[] dealerHand = new[] { mapped.WinTile.Value }.Concat(PickUnused(used, 13)).ToArray();
            plan.AddHand(mapped.Dealer, dealerHand);
            RoundStateMachine ronRound = TablePlan.Build(spec, plan);
            RoundTransition window = ronRound.Execute(
                RoundCommand.Discard(
                    ronRound.RoundId,
                    mapped.Dealer,
                    mapped.WinTile,
                    new[] { mapped.Winner }));
            if (!window.IsAccepted)
            {
                throw new InvalidOperationException("Ron discard rejected: " + window.Error);
            }

            facts = BuildFacts(mapped);
            return window.State!;
        }

        private static RoundSettlementFacts BuildFacts(MappedSample mapped)
        {
            return new RoundSettlementFacts(
                "golden-result-" + mapped.Id,
                FixedUtc,
                0,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        mapped.Winner,
                        mapped.Melds,
                        mapped.IsRiichi,
                        mapped.IsDoubleRiichi,
                        mapped.IsIppatsu,
                        isTenpai: false,
                        discardHistory: Array.Empty<TileId>())
                });
        }

        private static int PickDiscardTile(RoundStateMachine round)
        {
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, round.CurrentSeat);
            if (hand.Count == 0)
            {
                throw new InvalidOperationException("No hand tile available for route discard.");
            }

            return hand[0].Value;
        }

        private static int[] PickUnused(HashSet<int> used, int count)
        {
            var picked = new List<int>(count);
            for (int value = 0; value < TileId.Count && picked.Count < count; value++)
            {
                if (!used.Contains(value))
                {
                    used.Add(value);
                    picked.Add(value);
                }
            }

            if (picked.Count != count)
            {
                throw new InvalidOperationException("Not enough unused physical tiles.");
            }

            return picked.ToArray();
        }

        private static bool TryMap(
            JsonElement sample,
            out MappedSample mapped,
            out string reason)
        {
            mapped = null!;
            reason = string.Empty;
            JsonElement expected = sample.GetProperty("expected");
            string status = expected.GetProperty("status").GetString()!;
            if (status != "win" && status != "noWin")
            {
                reason = "unknown status";
                return false;
            }

            JsonElement context = sample.GetProperty("context");
            if (sample.GetProperty("drawn").ValueKind != JsonValueKind.Null)
            {
                reason = "non-null drawn field";
                return false;
            }

            if (context.GetProperty("isChankan").GetBoolean() ||
                context.GetProperty("isRinshan").GetBoolean() ||
                context.GetProperty("isHaitei").GetBoolean() ||
                context.GetProperty("isHoutei").GetBoolean() ||
                context.GetProperty("isTenhou").GetBoolean() ||
                context.GetProperty("isChiihou").GetBoolean())
            {
                reason = "situational flag outside the direct fixture";
                return false;
            }

            bool riichi = context.GetProperty("isRiichi").GetBoolean();
            bool doubleRiichi = context.GetProperty("isDoubleRiichi").GetBoolean();
            bool ippatsu = context.GetProperty("isIppatsu").GetBoolean();
            if ((doubleRiichi && !riichi) || (ippatsu && !riichi))
            {
                reason = "inconsistent riichi flags";
                return false;
            }

            int roundWind = context.GetProperty("roundWind").GetInt32();
            if (roundWind < 1 || roundWind > 3)
            {
                reason = "round wind outside RoundSpec";
                return false;
            }

            Meld[] melds;
            try
            {
                melds = sample.GetProperty("melds").EnumerateArray().Select(ReadMeld).ToArray();
            }
            catch (Exception)
            {
                reason = "unrepresentable meld";
                return false;
            }

            bool derivedClosed = melds.All(m => !m.IsOpen);
            if (context.GetProperty("isClosed").GetBoolean() != derivedClosed)
            {
                reason = "context isClosed differs from meld shape";
                return false;
            }

            if (riichi && melds.Any(m => m.IsOpen))
            {
                reason = "riichi with open meld";
                return false;
            }

            if (ippatsu && melds.Length > 0)
            {
                reason = "ippatsu cannot coexist with a call/kan meld";
                return false;
            }

            var closed = ReadTileIdArray(sample.GetProperty("closed")).ToList();
            var winTile = new TileId(context.GetProperty("winTile").GetInt32());
            int index = closed.FindIndex(t => t == winTile);
            if (index >= 0)
            {
                closed.RemoveAt(index);
            }

            int expectedClosed = 13 - (3 * melds.Length);
            if (closed.Count != expectedClosed)
            {
                reason = "closed tile count is not representable";
                return false;
            }

            TileId[] dora = ReadTileIdArray(sample.GetProperty("dora"));
            TileId[] ura = ReadTileIdArray(sample.GetProperty("ura"));
            if (dora.Length > 5)
            {
                reason = "too many dora indicators";
                return false;
            }

            if (riichi)
            {
                if (ura.Length != dora.Length)
                {
                    reason = "ura list is not derivable from the frozen dead wall";
                    return false;
                }
            }
            else if (ura.Length != 0)
            {
                reason = "non-riichi sample carries ura";
                return false;
            }

            var physical = new HashSet<int>();
            foreach (TileId tile in closed)
            {
                if (!physical.Add(tile.Value))
                {
                    reason = "duplicate closed tile";
                    return false;
                }
            }

            foreach (Meld meld in melds)
            {
                foreach (TileId tile in meld.Tiles)
                {
                    if (!physical.Add(tile.Value))
                    {
                        reason = "duplicate meld tile";
                        return false;
                    }
                }
            }

            if (!physical.Add(winTile.Value))
            {
                reason = "win tile duplicates an owned tile";
                return false;
            }

            foreach (TileId tile in dora.Concat(ura))
            {
                if (!physical.Add(tile.Value))
                {
                    reason = "dora/ura duplicates an owned tile";
                    return false;
                }
            }

            bool isTsumo = context.GetProperty("isTsumo").GetBoolean();
            int seatWind = context.GetProperty("seatWind").GetInt32();
            int winner;
            int dealer;
            bool routeDraw;
            if (isTsumo)
            {
                if (seatWind == 1)
                {
                    winner = 0;
                    dealer = 0;
                    routeDraw = false;
                }
                else if (seatWind == 2)
                {
                    winner = 1;
                    dealer = 0;
                    routeDraw = true;
                }
                else
                {
                    reason = "tsumo seat wind not exercised by direct fixture";
                    return false;
                }
            }
            else
            {
                if (seatWind < 2 || seatWind > 4)
                {
                    reason = "dealer-ron seat wind not exercised by direct fixture";
                    return false;
                }

                dealer = 0;
                winner = seatWind - 1;
                routeDraw = false;
            }

            foreach (Meld meld in melds)
            {
                if (meld.Type == MeldType.ClosedKan)
                {
                    if (meld.FromPlayer != -1)
                    {
                        reason = "closed kan with a caller";
                        return false;
                    }
                }
                else if (meld.FromPlayer < 0 ||
                         meld.FromPlayer > 3 ||
                         meld.FromPlayer == winner ||
                         (meld.Type == MeldType.Chi && meld.FromPlayer != ((winner + 3) % 4)))
                {
                    reason = "meld caller is not the frozen ownership shape";
                    return false;
                }
            }

            mapped = new MappedSample(
                sample.GetProperty("id").GetString()!,
                closed,
                melds,
                winTile,
                dora,
                ura,
                isTsumo,
                riichi,
                doubleRiichi,
                ippatsu,
                seatWind,
                roundWind,
                winner,
                dealer,
                0,
                routeDraw,
                expected);
            return true;
        }

        private static void AssertBreakdown(JsonElement expected, RoundSettlementCalculation calc)
        {
            Assert.NotNull(calc.Breakdown);
            ScoreBreakdown actual = calc.Breakdown!;
            JsonElement yaku = expected.GetProperty("yaku");
            Assert.Equal(yaku.GetArrayLength(), actual.Yaku.Count);
            int index = 0;
            foreach (JsonElement entry in yaku.EnumerateArray())
            {
                Assert.Equal(entry.GetProperty("code").GetString(), actual.Yaku[index].Code);
                Assert.Equal(entry.GetProperty("han").GetInt32(), actual.Yaku[index].Han);
                index++;
            }

            Assert.Equal(expected.GetProperty("yakuHan").GetInt32(), actual.TotalYakuHan);
            Assert.Equal(expected.GetProperty("scoreHan").GetInt32(), actual.Score.Han);
            Assert.Equal(expected.GetProperty("isYakuman").GetBoolean(), actual.Score.IsYakuman);
            Assert.Equal(expected.GetProperty("yakumanCount").GetInt32(), actual.YakumanCount);
            Assert.Equal(expected.GetProperty("fu").GetInt32(), actual.Fu.FinalFu);
            Assert.Equal(expected.GetProperty("basePoints").GetInt64(), actual.Score.BasePoints);
            Assert.Equal(NullableString(expected.GetProperty("capCode")), actual.Score.CapCode);
            Assert.Equal(expected.GetProperty("totalPoints").GetInt64(), actual.PaymentPlan.Total);
        }

        private static long[] ExpectedFinals(
            PaymentPlan plan,
            int winner,
            int dealer,
            int loser)
        {
            var scores = new long[] { 25000, 25000, 25000, 25000 };
            foreach (PaymentComponent component in plan.Components)
            {
                if (component.PayerRole == PayerRole.Discarder)
                {
                    scores[loser] -= component.Subtotal;
                }
                else if (component.PayerRole == PayerRole.Dealer)
                {
                    scores[dealer] -= component.Subtotal;
                }
                else
                {
                    for (int seat = 0; seat < 4; seat++)
                    {
                        if (seat != winner && seat != dealer)
                        {
                            scores[seat] -= component.Amount;
                        }
                    }
                }
            }

            scores[winner] += plan.Total;
            return scores;
        }

        private static TileId[] ReadTileIdArray(JsonElement element)
        {
            return element.EnumerateArray().Select(e => new TileId(e.GetInt32())).ToArray();
        }

        private static string PhaseCode(RoundPhase phase)
        {
            return phase == RoundPhase.RoundEnd ? "ROUND_END" : phase.ToString().ToUpperInvariant();
        }

        private static int[] ReadTileIds(JsonElement element)
        {
            return element.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        }

        private static long[] ReadLongs(JsonElement element)
        {
            return element.EnumerateArray().Select(e => e.GetInt64()).ToArray();
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
            return new Meld(
                type,
                ReadTileIds(element.GetProperty("tiles")).Select(v => new TileId(v)),
                element.GetProperty("fromPlayer").GetInt32(),
                new TileId(element.GetProperty("calledTile").GetInt32()));
        }

        private static string? NullableString(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Null ? null : element.GetString();
        }

        private static JsonDocument LoadGolden(string fileName)
        {
            string path = FindGoldenPath(fileName);
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        private static string FindGoldenPath(
            string fileName,
            [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(overrideRoot!, "tests", "v5-golden", fileName);
                if (File.Exists(overridden))
                {
                    return overridden;
                }
            }

            foreach (string start in new[]
                     {
                         AppContext.BaseDirectory,
                         Directory.GetCurrentDirectory(),
                         Path.GetDirectoryName(sourceFile) ?? string.Empty
                     })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(directory.FullName, "tests", "v5-golden", fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException(
                "tests/v5-golden/" + fileName + " was not found from " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() + "; set MAHJONG_REPO_ROOT.");
        }

        private sealed class MappedSample
        {
            internal MappedSample(
                string id,
                List<TileId> closed,
                Meld[] melds,
                TileId winTile,
                TileId[] dora,
                TileId[] ura,
                bool isTsumo,
                bool isRiichi,
                bool isDoubleRiichi,
                bool isIppatsu,
                int seatWind,
                int roundWind,
                int winner,
                int dealer,
                int loser,
                bool routeDraw,
                JsonElement expected)
            {
                Id = id;
                Closed = closed;
                Melds = melds;
                WinTile = winTile;
                Dora = dora;
                Ura = ura;
                IsTsumo = isTsumo;
                IsRiichi = isRiichi;
                IsDoubleRiichi = isDoubleRiichi;
                IsIppatsu = isIppatsu;
                SeatWind = seatWind;
                RoundWind = roundWind;
                Winner = winner;
                Dealer = dealer;
                Loser = loser;
                RouteDraw = routeDraw;
                Expected = expected;
            }

            internal string Id { get; }

            internal List<TileId> Closed { get; }

            internal Meld[] Melds { get; }

            internal TileId WinTile { get; }

            internal TileId[] Dora { get; }

            internal TileId[] Ura { get; }

            internal bool IsTsumo { get; }

            internal bool IsRiichi { get; }

            internal bool IsDoubleRiichi { get; }

            internal bool IsIppatsu { get; }

            internal int SeatWind { get; }

            internal int RoundWind { get; }

            internal int Winner { get; }

            internal int Dealer { get; }

            internal int Loser { get; }

            internal bool RouteDraw { get; }

            internal JsonElement Expected { get; }
        }
    }
}

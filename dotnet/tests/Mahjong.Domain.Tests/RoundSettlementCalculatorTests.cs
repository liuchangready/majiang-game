using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public sealed class RoundSettlementCalculatorTests
    {
        private static readonly PersistedUtcTime FixedUtc =
            new PersistedUtcTime(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));

        [Fact]
        public void DealerTsumo_ComputesFrozenScoreAndExecutesThroughStateMachine()
        {
            RoundSpec spec = new RoundSpec(
                "n2-dealer-tsumo",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                DrawnTile = new TileId(24),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            plan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            var facts = new RoundSettlementFacts(
                "res-dealer-tsumo",
                FixedUtc,
                1000,
                new[] { new RoundSettlementSeatFacts(0) });

            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateTsumo(round, facts);

            Assert.NotNull(calc.Breakdown);
            Assert.Equal(12000, calc.Breakdown!.PaymentPlan.Total);
            Assert.Equal(5, calc.Breakdown.Score.Han);
            Assert.Equal(40, calc.Breakdown.Fu.FinalFu);
            Assert.Equal(new long[] { 37000, 21000, 21000, 21000 }, calc.Settlement.FinalScores.ToArray());
            Assert.Equal(new long[] { 12000, -4000, -4000, -4000 }, calc.Payments.Deltas.ToArray());
            Assert.Equal(0, calc.Settlement.RemainingRiichiSticks);
            Assert.Equal(RoundActionKind.DeclareTsumo, calc.Command.Action);
            Assert.Equal("n2-dealer-tsumo", calc.Command.RoundId);
            Assert.Equal(round.Version, calc.SourceVersion);
            Assert.Equal(round.SourceFingerprint, calc.SourceFingerprint);

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            Assert.NotNull(applied.State);
            Assert.True(applied.State!.IsFinished);
            Assert.Equal(RoundPhase.RoundEnd, applied.State.Phase);
            Assert.Equal(1, applied.State.Version);
            Assert.Equal(RoundEndReason.Tsumo, applied.State.Result!.EndReason);
            Assert.Equal("res-dealer-tsumo", applied.State.Result.ResultId);
            Assert.Equal(new long[] { 37000, 21000, 21000, 21000 }, applied.State.Result.FinalScores.ToArray());
        }

        [Fact]
        public void NonDealerTsumo_DrawRoute_HonbaAndRiichiSticksConserve()
        {
            RoundSpec spec = new RoundSpec(
                "n2-nondealer-tsumo",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 2,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 3);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4
            };
            // The same V4-scored 5-han 40-fu hand as the dealer case, but seat 1 wins.
            plan.AddHand(1, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            plan.RemoveTile(1, new TileId(19));
            plan.AddHand(0, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113);
            plan.AddWallTop(19);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            // Dealer discards; no eligible caller, so the state machine advances and seat 1
            // draws the prepared wall tile into its hand with real DrawnTileEvidence.
            RoundTransition discard = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, new TileId(100)));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            round = discard.State!;
            Assert.Equal(1, round.CurrentSeat);
            Assert.True(round.Tiles.DrawnTile.HasValue);
            Assert.Equal(new TileId(19), round.Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(1, round.Tiles.DrawnTile!.Value.Seat);

            var facts = new RoundSettlementFacts(
                "res-nondealer-tsumo",
                FixedUtc,
                2000,
                new[] { new RoundSettlementSeatFacts(1, discardHistory: Array.Empty<TileId>()) });

            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateTsumo(round, facts);

            Assert.NotNull(calc.Breakdown);
            // 5 han 30 fu non-dealer tsumo: dealer 4000, each non-dealer 2000, plus
            // 2 honba (200 each) and three riichi sticks awarded to the winner.
            Assert.Equal(8000, calc.Breakdown!.PaymentPlan.Total);
            Assert.Equal(new long[] { 20800, 36600, 22800, 22800 }, calc.Settlement.FinalScores.ToArray());
            Assert.Equal(new long[] { -4200, 11600, -2200, -2200 }, calc.Payments.Deltas.ToArray());
            Assert.Equal(0, calc.Settlement.RemainingRiichiSticks);

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            Assert.Equal(RoundEndReason.Tsumo, applied.State!.Result!.EndReason);
            Assert.Equal(new long[] { 20800, 36600, 22800, 22800 }, applied.State.Result.FinalScores.ToArray());
        }

        [Fact]
        public void Ron_DealerDiscard_NonDealerWinner_ExecutesAndMatchesV4Payment()
        {
            RoundSpec spec = new RoundSpec(
                "n2-ron",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true
            };
            plan.AddHand(1, 92, 96, 100, 12, 20, 40, 44, 48, 17, 21, 24, 13, 14);
            plan.AddHand(0, 16, 0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 52, 56, 60);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            RoundTransition discard = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, new TileId(16), new[] { 1 }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            round = discard.State!;
            Assert.Equal(RoundPhase.ResponseWindow, round.Phase);

            var facts = new RoundSettlementFacts(
                "res-ron",
                FixedUtc,
                3000,
                new[] { new RoundSettlementSeatFacts(1, discardHistory: Array.Empty<TileId>()) });
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateRon(round, 1, facts);

            Assert.NotNull(calc.Breakdown);
            Assert.Equal(2600, calc.Breakdown!.PaymentPlan.Total);
            Assert.Equal(1, calc.WinnerSeat);
            Assert.Equal(0, calc.LoserSeat);
            Assert.Equal(2, calc.SeatWind);
            Assert.False(calc.IsDealer);
            Assert.Equal(new long[] { 22400, 27600, 25000, 25000 }, calc.Settlement.FinalScores.ToArray());
            Assert.Equal(RoundActionKind.DeclareRon, calc.Command.Action);

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            Assert.Equal(RoundEndReason.Ron, applied.State!.Result!.EndReason);
            Assert.Equal(new long[] { 22400, 27600, 25000, 25000 }, applied.State.Result.FinalScores.ToArray());
        }

        [Fact]
        public void ChankanRon_UsesWindowWithoutCommittingKan()
        {
            RoundSpec spec = new RoundSpec(
                "n2-chankan",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

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
            // Tanyao hand waiting on 5m via 4m/6m; no 5m in hand, so the dealer can hold
            // the complete 5m pon plus the fourth physical tile.
            plan.AddHand(1, 4, 8, 12, 20, 24, 28, 76, 80, 84, 40, 41, 13, 21);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            RoundTransition kan = round.Execute(
                RoundCommand.AddedKan(round.RoundId, 0, new TileId(16), new[] { 1 }));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            round = kan.State!;
            Assert.Equal(RoundPhase.ChankanWindow, round.Phase);
            Assert.Equal(0, round.Tiles.KanCount);
            Assert.Equal(3, round.Tiles.CountAt(TileZone.Meld, 0));
            Assert.Equal(TileZone.Hand, round.Tiles.PositionOf(new TileId(16)).Zone);

            var facts = new RoundSettlementFacts(
                "res-chankan",
                FixedUtc,
                4000,
                new[] { new RoundSettlementSeatFacts(1, isChankan: true, discardHistory: Array.Empty<TileId>()) });
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateRon(round, 1, facts);

            Assert.NotNull(calc.Breakdown);
            Assert.Contains(calc.Breakdown!.Yaku, y => y.Code == "CHANKAN");
            Assert.Contains(calc.Breakdown.Yaku, y => y.Code == "TANYAO");
            Assert.Equal(2600, calc.Breakdown.PaymentPlan.Total);
            Assert.Equal(new long[] { 22400, 27600, 25000, 25000 }, calc.Settlement.FinalScores.ToArray());

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            RoundStateMachine finished = applied.State!;
            Assert.Equal(RoundEndReason.ChankanRon, finished.Result!.EndReason);
            Assert.DoesNotContain(finished.Result.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.DoesNotContain(finished.Result.Events, e => e.Type == RoundEventType.RinshanDrawn);
            Assert.Equal(TileZone.Hand, finished.Tiles.PositionOf(new TileId(16)).Zone);
            Assert.Equal(3, finished.Tiles.CountAt(TileZone.Meld, 0));
            Assert.Equal(0, finished.Tiles.KanCount);
        }
        [Fact]
        public void ExhaustiveDraw_DealerTenpai_Pays3000_RetainsSticks_AndRenchan()
        {
            RoundSpec spec = new RoundSpec(
                "n2-exhaustive-draw",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 1,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 2);

            var plan = new TablePlan(RoundPhase.Draw)
            {
                WallTiles = 0
            };
            // Dealer is tenpai on the 5s pair.
            plan.AddHand(0, 4, 8, 12, 16, 20, 24, 21, 25, 29, 76, 80, 84, 52);
            // Three non-tenpai hands made only of distinct terminal/honor types; adding one
            // tile cannot produce four groups plus a pair.
            plan.AddHand(1, 0, 32, 72, 104, 36, 68, 108, 112, 116, 120, 124, 128, 44);
            plan.AddHand(2, 1, 33, 73, 105, 37, 69, 109, 113, 117, 121, 125, 129, 48);
            plan.AddHand(3, 2, 34, 74, 106, 38, 70, 110, 114, 118, 122, 126, 130, 40);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            var facts = new RoundSettlementFacts(
                "res-exhaustive",
                FixedUtc,
                5000,
                new[]
                {
                    new RoundSettlementSeatFacts(0, isTenpai: true),
                    new RoundSettlementSeatFacts(1, isTenpai: false),
                    new RoundSettlementSeatFacts(2, isTenpai: false),
                    new RoundSettlementSeatFacts(3, isTenpai: false)
                });

            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateExhaustiveDraw(round, facts);

            Assert.Null(calc.Breakdown);
            Assert.Equal(new long[] { 28000, 24000, 24000, 24000 }, calc.Settlement.FinalScores.ToArray());
            Assert.Equal(new long[] { 3000, -1000, -1000, -1000 }, calc.Payments.Deltas.ToArray());
            Assert.Equal(2, calc.Settlement.RemainingRiichiSticks);
            Assert.Equal(RoundOutcomeKind.ExhaustiveDraw, calc.Outcome.Kind);
            Assert.Equal(new[] { 0 }, calc.Outcome.TenpaiSeats.ToArray());
            Assert.Equal(RoundActionKind.DeclareExhaustiveDraw, calc.Command.Action);

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            RoundResult result = applied.State!.Result!;
            Assert.Equal(RoundEndReason.ExhaustiveDraw, result.EndReason);
            Assert.Equal(new long[] { 28000, 24000, 24000, 24000 }, result.FinalScores.ToArray());
            Assert.Equal(2, result.RemainingRiichiSticks);

            ProgressionResult progression = new MatchProgression().NextRound(
                new ProgressionInput(
                    0,
                    1,
                    0,
                    1,
                    result.FinalScores,
                    result.Outcome,
                    result.RemainingRiichiSticks));
            var next = Assert.IsType<ProgressionResult.NextRound>(progression);
            Assert.Equal(0, next.DealerSeat);
            Assert.Equal(0, next.RoundNumber);
            Assert.Equal(2, next.HonbaSticks);
        }

        [Fact]
        public void AbortiveDraw_HasNoPaymentsAndRetainsRiichiSticks()
        {
            RoundSpec spec = new RoundSpec(
                "n2-abortive",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 1,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 2);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true
            };
            plan.AddHand(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
            plan.AddDiscard(0, 108);
            plan.AddDiscard(1, 109);
            plan.AddDiscard(2, 110);
            plan.AddDiscard(3, 111);
            RoundStateMachine round = TablePlan.Build(spec, plan);
            var facts = new RoundSettlementFacts(
                "res-abortive",
                FixedUtc,
                6000,
                new[]
                {
                    new RoundSettlementSeatFacts(0, firstDiscard: new TileId(108), isFirstTurnUninterrupted: true),
                    new RoundSettlementSeatFacts(1, firstDiscard: new TileId(109), isFirstTurnUninterrupted: true),
                    new RoundSettlementSeatFacts(2, firstDiscard: new TileId(110), isFirstTurnUninterrupted: true),
                    new RoundSettlementSeatFacts(3, firstDiscard: new TileId(111), isFirstTurnUninterrupted: true)
                },
                SettlementAbortiveDrawReason.FourWinds);

            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateAbortiveDraw(round, facts);

            Assert.Null(calc.Breakdown);
            Assert.Equal(new long[] { 25000, 25000, 25000, 25000 }, calc.Settlement.FinalScores.ToArray());
            Assert.Equal(new long[] { 0, 0, 0, 0 }, calc.Payments.Deltas.ToArray());
            Assert.Equal(2, calc.Settlement.RemainingRiichiSticks);
            Assert.Equal(RoundOutcomeKind.AbortiveDraw, calc.Outcome.Kind);
            Assert.Equal("FOUR_WINDS", calc.Outcome.Reason);

            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            Assert.Equal(RoundEndReason.AbortiveDraw, applied.State!.Result!.EndReason);
        }

        [Fact]
        public void ExhaustiveDraw_UsesMeldAwareTenpaiFromConcealedTiles()
        {
            RoundSpec spec = new RoundSpec(
                "n2-exhaustive-open",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);
            var pon = new Meld(
                MeldType.Pon,
                new[] { new TileId(17), new TileId(18), new TileId(19) },
                3,
                new TileId(17));
            var plan = new TablePlan(RoundPhase.Draw)
            {
                WallTiles = 0
            };
            // One meld, ten concealed tiles, waiting on the 5s pair.
            plan.AddHand(0, 0, 1, 2, 8, 9, 10, 72, 73, 74, 52);
            plan.AddMeld(0, pon);
            plan.AddHand(1, 32, 104, 36, 68, 108, 112, 116, 120, 124, 128, 44, 48, 40);
            plan.AddHand(2, 33, 105, 37, 69, 109, 113, 117, 121, 125, 129, 45, 49, 41);
            plan.AddHand(3, 34, 106, 38, 70, 110, 114, 118, 122, 126, 130, 46, 50, 42);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            var facts = new RoundSettlementFacts(
                "res-exhaustive-open",
                FixedUtc,
                5500,
                new[]
                {
                    new RoundSettlementSeatFacts(0, new[] { pon }, isTenpai: true),
                    new RoundSettlementSeatFacts(1, isTenpai: false),
                    new RoundSettlementSeatFacts(2, isTenpai: false),
                    new RoundSettlementSeatFacts(3, isTenpai: false)
                });
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateExhaustiveDraw(round, facts);
            Assert.Equal(new long[] { 28000, 24000, 24000, 24000 }, calc.Settlement.FinalScores.ToArray());
            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            Assert.Equal(RoundEndReason.ExhaustiveDraw, applied.State!.Result!.EndReason);
        }

        [Fact]
        public void AbortiveDraw_RequiresTrustedReasonAndSeasonalEvidence()
        {
            RoundSpec spec = new RoundSpec(
                "n2-abortive-negative",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true
            };
            plan.AddHand(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            var missing = new RoundSettlementFacts(
                "res-abortive-missing",
                FixedUtc,
                9700,
                new[] { new RoundSettlementSeatFacts(0) });
            RoundSettlementRejectedException noReason = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(round, missing));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMissing, noReason.Code);

            var wrongReason = new RoundSettlementFacts(
                "res-abortive-wrong",
                FixedUtc,
                9701,
                new[] { new RoundSettlementSeatFacts(0) },
                SettlementAbortiveDrawReason.FourKans);
            RoundSettlementRejectedException mismatch = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(round, wrongReason));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, mismatch.Code);
        }

        [Fact]
        public void MultiWinnerRon_ClosestSeatWins_AndLoserCandidateIsNotApplied()
        {
            RoundSpec spec = new RoundSpec(
                "n2-multi-ron",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true
            };
            plan.AddHand(0, 16, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111);
            // Two disjoint tanyao hands with the same 4m/6m wait on 5m.
            plan.AddHand(1, 4, 8, 12, 20, 24, 28, 76, 80, 84, 40, 41, 13, 21);
            plan.AddHand(2, 5, 9, 14, 22, 25, 29, 77, 81, 85, 42, 43, 15, 23);
            plan.AddDora(6);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            RoundTransition discard = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, new TileId(16), new[] { 1, 2 }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            round = discard.State!;

            var facts1 = new RoundSettlementFacts(
                "res-multi-1",
                FixedUtc,
                7000,
                new[] { new RoundSettlementSeatFacts(1, discardHistory: Array.Empty<TileId>()) });
            var facts2 = new RoundSettlementFacts(
                "res-multi-2",
                FixedUtc,
                7001,
                new[] { new RoundSettlementSeatFacts(2, discardHistory: Array.Empty<TileId>()) });
            RoundSettlementCalculation calc1 = RoundSettlementCalculator.CalculateRon(round, 1, facts1);
            RoundSettlementCalculation calc2 = RoundSettlementCalculator.CalculateRon(round, 2, facts2);
            Assert.Equal(2600, calc1.Breakdown!.PaymentPlan.Total);
            Assert.Equal(2600, calc2.Breakdown!.PaymentPlan.Total);

            RoundTransition first = calc1.Execute(round);
            Assert.True(first.IsAccepted, first.Error?.ToString());
            Assert.False(first.State!.IsFinished);
            Assert.Single(first.State.Window!.Claims);

            // Each accepted claim advances the authoritative version. A second candidate must
            // be re-derived from the new window state; reusing the pre-claim calculation would
            // (correctly) be rejected as stale.
            RoundSettlementCalculation calc2AfterFirst =
                RoundSettlementCalculator.CalculateRon(first.State, 2, facts2);
            RoundTransition second = calc2AfterFirst.Execute(first.State);
            Assert.True(second.IsAccepted, second.Error?.ToString());
            RoundStateMachine finished = second.State!;
            Assert.True(finished.IsFinished);
            Assert.Equal(1, finished.Result!.Outcome.WinnerSeat);
            Assert.Equal(new long[] { 22400, 27600, 25000, 25000 }, finished.Result.FinalScores.ToArray());
            Assert.DoesNotContain(finished.Result.Events, e => e.Type == RoundEventType.TsumoWin);
        }

        [Fact]
        public void StaleAndForeignSourcesAreRejectedWithoutMutation()
        {
            RoundSpec spec = new RoundSpec(
                "n2-source-binding",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

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
                "res-source",
                FixedUtc,
                8000,
                new[] { new RoundSettlementSeatFacts(0) });
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateTsumo(round, facts);

            RoundTransition advancedTransition = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, new TileId(16)));
            Assert.True(advancedTransition.IsAccepted, advancedTransition.Error?.ToString());
            RoundStateMachine advanced = advancedTransition.State!;
            long advancedVersion = advanced.Version;
            ulong advancedFingerprint = advanced.SourceFingerprint;

            RoundSettlementRejectedException stale = Assert.Throws<RoundSettlementRejectedException>(
                () => calc.Execute(advanced));
            Assert.Equal(RoundSettlementRejectionCode.StaleSourceVersion, stale.Code);
            Assert.Equal(advancedVersion, advanced.Version);
            Assert.Equal(advancedFingerprint, advanced.SourceFingerprint);
            Assert.False(advanced.IsFinished);

            var foreignPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4
            };
            foreignPlan.AddHand(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
            RoundStateMachine foreign = TablePlan.Build(spec, foreignPlan);
            RoundSettlementRejectedException foreignError = Assert.Throws<RoundSettlementRejectedException>(
                () => calc.Execute(foreign));
            Assert.Equal(RoundSettlementRejectionCode.ForeignSourceState, foreignError.Code);
            Assert.Equal(0, foreign.Version);
        }

        [Fact]
        public void TerminalRoundAndNoYakuAreRejected()
        {
            RoundSpec spec = new RoundSpec(
                "n2-terminal",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

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
                "res-terminal",
                FixedUtc,
                9000,
                new[] { new RoundSettlementSeatFacts(0) });
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateTsumo(round, facts);
            RoundTransition applied = calc.Execute(round);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());

            RoundSettlementRejectedException terminal = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(applied.State!, facts));
            Assert.Equal(RoundSettlementRejectionCode.RoundFinished, terminal.Code);

            var noYakuSpec = new RoundSpec(
                "n2-no-yaku",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);
            var noYakuPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true
            };
            noYakuPlan.AddHand(1, 0, 4, 8, 12, 17, 20, 32, 36, 40, 72, 76, 80, 19);
            noYakuPlan.AddHand(0, 16, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111);
            RoundStateMachine noYakuRound = TablePlan.Build(noYakuSpec, noYakuPlan);
            RoundTransition discard = noYakuRound.Execute(
                RoundCommand.Discard(noYakuRound.RoundId, 0, new TileId(16), new[] { 1 }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            var noYakuFacts = new RoundSettlementFacts(
                "res-no-yaku",
                FixedUtc,
                9001,
                new[] { new RoundSettlementSeatFacts(1, discardHistory: Array.Empty<TileId>()) });
            RoundSettlementRejectedException noYaku = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(discard.State!, 1, noYakuFacts));
            Assert.Equal(RoundSettlementRejectionCode.NoWinningHand, noYaku.Code);
        }

        [Fact]
        public void WrongWindowSeatsAndFlagsAreRejected()
        {
            RoundSpec spec = new RoundSpec(
                "n2-window-negative",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8, FillRemainingIntoWall = true
            };
            plan.AddHand(1, 92, 96, 100, 12, 20, 40, 44, 48, 17, 21, 24, 13, 14);
            plan.AddHand(0, 16, 0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 52, 56, 60);
            RoundStateMachine round = TablePlan.Build(spec, plan);
            var facts = new RoundSettlementFacts(
                "res-window-negative",
                FixedUtc,
                9100,
                new[] { new RoundSettlementSeatFacts(1, discardHistory: Array.Empty<TileId>()) });

            RoundSettlementRejectedException noWindow = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(round, 1, facts));
            Assert.Equal(RoundSettlementRejectionCode.WrongWindow, noWindow.Code);

            RoundTransition discard = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, new TileId(16), new[] { 1 }));
            RoundStateMachine window = discard.State!;

            RoundSettlementRejectedException notEligible = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(window, 2, facts));
            Assert.Equal(RoundSettlementRejectionCode.WinnerNotEligible, notEligible.Code);

            RoundSettlementRejectedException ownDiscard = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(window, 0, facts));
            Assert.Equal(RoundSettlementRejectionCode.WinnerIsDiscarder, ownDiscard.Code);

            var wrongChankanFacts = new RoundSettlementFacts(
                "res-wrong-chankan",
                FixedUtc,
                9101,
                new[] { new RoundSettlementSeatFacts(1, isChankan: true, discardHistory: Array.Empty<TileId>()) });
            RoundSettlementRejectedException wrongChankan = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(window, 1, wrongChankanFacts));
            Assert.Equal(RoundSettlementRejectionCode.WinFlagMismatch, wrongChankan.Code);
        }

        [Fact]
        public void MeldEvidenceMustMatchPhysicalMeldGroup()
        {
            RoundSpec spec = new RoundSpec(
                "n2-meld-negative",
                dealerSeat: 0,
                roundWind: 1,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

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
            RoundStateMachine round = TablePlan.Build(spec, plan);

            var wrongTiles = new Meld(
                MeldType.Pon,
                new[] { new TileId(17), new TileId(18), new TileId(19) },
                0,
                new TileId(17));
            var wrongTilesFacts = new RoundSettlementFacts(
                "res-meld-1",
                FixedUtc,
                9200,
                new[] { new RoundSettlementSeatFacts(0, new[] { wrongTiles }) });
            RoundSettlementRejectedException wrongSeat = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(round, wrongTilesFacts));
            Assert.Equal(RoundSettlementRejectionCode.MeldSeatMismatch, wrongSeat.Code);

            var missingGroup = new Meld(
                MeldType.Pon,
                new[] { new TileId(16), new TileId(17), new TileId(18) },
                0,
                new TileId(16));
            var missingFacts = new RoundSettlementFacts(
                "res-meld-2",
                FixedUtc,
                9201,
                new[] { new RoundSettlementSeatFacts(0, new[] { missingGroup }) });
            RoundSettlementRejectedException mismatch = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(round, missingFacts));
            Assert.Equal(RoundSettlementRejectionCode.MeldGroupMismatch, mismatch.Code);

            var openRiichiFacts = new RoundSettlementFacts(
                "res-open-riichi",
                FixedUtc,
                9202,
                new[] { new RoundSettlementSeatFacts(0, new[] { pon }, isRiichi: true) });
            RoundSettlementRejectedException openRiichi = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(round, openRiichiFacts));
            Assert.Equal(RoundSettlementRejectionCode.RiichiStateInvalid, openRiichi.Code);
        }

        [Fact]
        public void DoraIndicatorMustOccupyTheFrozenDeadWallSlot()
        {
            RoundSpec spec = new RoundSpec(
                "n2-dora-negative",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);

            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            plan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            plan.AddDora(100);
            TilePositionMap original = plan.BuildMap();
            var indicator = new TileId(100);
            TileId displaced = original.TileAt(TilePosition.DeadWall(5));
            var moved = new List<TilePlacement>();
            foreach (TilePlacement placement in original.AsPlacements())
            {
                TilePosition position = placement.Position;
                if (placement.Tile == indicator)
                {
                    position = TilePosition.DeadWall(5);
                }
                else if (placement.Tile == displaced)
                {
                    position = TilePosition.DeadWall(4);
                }

                moved.Add(new TilePlacement(placement.Tile, position));
            }

            TilePositionMap broken = TilePositionMap.FromPlacements(
                moved,
                0,
                4,
                0,
                null,
                0,
                null,
                new[] { indicator },
                0,
                new DrawnTileEvidence(new TileId(19), 0, DrawSource.Wall));
            RoundStateMachine round = RoundStateMachine.CreateDealt(spec, broken, RoundPhase.CheatWindow);
            var facts = new RoundSettlementFacts(
                "res-dora-negative",
                FixedUtc,
                9300,
                new[] { new RoundSettlementSeatFacts(0) });

            RoundSettlementRejectedException dora = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(round, facts));
            Assert.Equal(RoundSettlementRejectionCode.DoraIndicatorMismatch, dora.Code);
        }

        [Fact]
        public void CheckedOverflowAndWrongHandFactsAreRejectedWithoutMutation()
        {
            RoundSpec spec = new RoundSpec(
                "n2-overflow",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { long.MaxValue, 1, 1, 1 },
                riichiSticks: 0);

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
                "res-overflow",
                FixedUtc,
                9400,
                new[] { new RoundSettlementSeatFacts(0) });

            RoundSettlementRejectedException overflow = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(round, facts));
            Assert.Equal(RoundSettlementRejectionCode.ArithmeticOverflow, overflow.Code);
            Assert.Equal(0, round.Version);
            Assert.False(round.IsFinished);

            var shortPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            shortPlan.AddHand(0, 16, 17, 18, 19, 20);
            RoundStateMachine shortRound = TablePlan.Build(spec, shortPlan);
            RoundSettlementRejectedException count = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(shortRound, facts));
            Assert.Equal(RoundSettlementRejectionCode.HandCountMismatch, count.Code);
            Assert.Equal(0, shortRound.Version);
        }

        [Fact]
        public void StateMachineConservationGateStillRejectsForgedSettlement()
        {
            RoundSpec spec = new RoundSpec(
                "n2-forged",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            plan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            RoundStateMachine round = TablePlan.Build(spec, plan);

            var forged = new RoundSettlement(
                "forged-result",
                RoundOutcome.Tsumo(0),
                new long[] { 0, 0, 0, 0 },
                0,
                FixedUtc,
                9500);
            RoundTransition applied = round.Execute(RoundCommand.Tsumo(round.RoundId, 0, forged));
            Assert.False(applied.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.SettlementConservationViolation, applied.Error!.Code);
            Assert.Equal(0, round.Version);
            Assert.False(round.IsFinished);
        }

        [Fact]
        public void PublicModelsAreImmutableCopies()
        {
            var seats = new List<RoundSettlementSeatFacts>
            {
                new RoundSettlementSeatFacts(0)
            };
            var facts = new RoundSettlementFacts("res-immutable", FixedUtc, 9600, seats);
            seats.Add(new RoundSettlementSeatFacts(1));
            Assert.Single(facts.Seats);

            RoundSpec spec = new RoundSpec(
                "n2-immutable",
                dealerSeat: 0,
                roundWind: 3,
                roundNumber: 0,
                honbaSticks: 0,
                scores: new long[] { 25000, 25000, 25000, 25000 },
                riichiSticks: 0);
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 4,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            plan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            RoundStateMachine round = TablePlan.Build(spec, plan);
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateTsumo(round, facts);

            Assert.Throws<NotSupportedException>(
                () => ((IList<long>)calc.Settlement.FinalScores).Add(0));
            Assert.Throws<NotSupportedException>(
                () => ((IList<long>)calc.Payments.Deltas).Add(0));
            Assert.Throws<NotSupportedException>(
                () => ((IList<Meld>)facts.Seats[0].Melds).Add(null!));
        }

        [Fact]
        public void AbortiveFourWinds_RequiresMatchingFirstDiscardsAndNoCalls()
        {
            RoundSpec spec = new RoundSpec(
                "n2-four-winds-negative",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);

            var missingPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            missingPlan.AddHand(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
            RoundStateMachine missingRound = TablePlan.Build(spec, missingPlan);
            RoundSettlementRejectedException missing = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(
                    missingRound,
                    FourSeatAbortiveFacts("res-four-winds-missing", SettlementAbortiveDrawReason.FourWinds)));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, missing.Code);

            var wrongPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            wrongPlan.AddHand(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
            wrongPlan.AddDiscard(0, 108);
            wrongPlan.AddDiscard(1, 109);
            wrongPlan.AddDiscard(2, 110);
            wrongPlan.AddDiscard(3, 112);
            var wrongFacts = new RoundSettlementFacts(
                "res-four-winds-wrong",
                FixedUtc,
                9800,
                new[]
                {
                    new RoundSettlementSeatFacts(0, firstDiscard: new TileId(108), isFirstTurnUninterrupted: true),
                    new RoundSettlementSeatFacts(1, firstDiscard: new TileId(109), isFirstTurnUninterrupted: true),
                    new RoundSettlementSeatFacts(2, firstDiscard: new TileId(110), isFirstTurnUninterrupted: true),
                    new RoundSettlementSeatFacts(3, firstDiscard: new TileId(112), isFirstTurnUninterrupted: true)
                },
                SettlementAbortiveDrawReason.FourWinds);
            RoundStateMachine wrongRound = TablePlan.Build(spec, wrongPlan);
            RoundSettlementRejectedException wrong = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(wrongRound, wrongFacts));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, wrong.Code);

            var callPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            callPlan.AddHand(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
            callPlan.AddMeld(0, new Meld(
                MeldType.Pon,
                new[] { new TileId(17), new TileId(18), new TileId(19) },
                3,
                new TileId(17)));
            RoundStateMachine callRound = TablePlan.Build(spec, callPlan);
            RoundSettlementRejectedException call = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(
                    callRound,
                    FourSeatAbortiveFacts("res-four-winds-call", SettlementAbortiveDrawReason.FourWinds)));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, call.Code);
        }

        [Fact]
        public void AbortiveFourKans_RequiresKansAcrossAtLeastTwoSeats()
        {
            RoundSpec spec = new RoundSpec(
                "n2-four-kans",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);

            var sameSeatMelds = new[]
            {
                new Meld(MeldType.ClosedKan, new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) }, -1, new TileId(0)),
                new Meld(MeldType.ClosedKan, new[] { new TileId(4), new TileId(5), new TileId(6), new TileId(7) }, -1, new TileId(4)),
                new Meld(MeldType.ClosedKan, new[] { new TileId(8), new TileId(9), new TileId(10), new TileId(11) }, -1, new TileId(8)),
                new Meld(MeldType.ClosedKan, new[] { new TileId(12), new TileId(13), new TileId(14), new TileId(15) }, -1, new TileId(12))
            };
            var sameSeat = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                KanCount = 4
            };
            sameSeat.AddHand(0, 52, 53);
            foreach (Meld meld in sameSeatMelds)
            {
                sameSeat.AddMeld(0, meld);
            }

            var sameFacts = new RoundSettlementFacts(
                "res-four-kans-same",
                FixedUtc,
                9810,
                new[]
                {
                    new RoundSettlementSeatFacts(0, sameSeatMelds),
                    new RoundSettlementSeatFacts(1),
                    new RoundSettlementSeatFacts(2),
                    new RoundSettlementSeatFacts(3)
                },
                SettlementAbortiveDrawReason.FourKans);
            RoundStateMachine sameRound = TablePlan.Build(spec, sameSeat);
            RoundSettlementRejectedException sameError = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(sameRound, sameFacts));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, sameError.Code);

            var seat0Melds = new[]
            {
                new Meld(MeldType.ClosedKan, new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) }, -1, new TileId(0)),
                new Meld(MeldType.ClosedKan, new[] { new TileId(4), new TileId(5), new TileId(6), new TileId(7) }, -1, new TileId(4))
            };
            var seat1Melds = new[]
            {
                new Meld(MeldType.ClosedKan, new[] { new TileId(12), new TileId(13), new TileId(14), new TileId(15) }, -1, new TileId(12)),
                new Meld(MeldType.ClosedKan, new[] { new TileId(16), new TileId(17), new TileId(18), new TileId(19) }, -1, new TileId(16))
            };
            var twoSeats = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                KanCount = 4
            };
            twoSeats.AddHand(0, 8, 9, 10, 32, 36, 40, 44);
            twoSeats.AddHand(1, 20, 21, 22, 23, 24, 25, 26);
            foreach (Meld meld in seat0Melds)
            {
                twoSeats.AddMeld(0, meld);
            }

            foreach (Meld meld in seat1Melds)
            {
                twoSeats.AddMeld(1, meld);
            }

            var twoFacts = new RoundSettlementFacts(
                "res-four-kans-two",
                FixedUtc,
                9811,
                new[]
                {
                    new RoundSettlementSeatFacts(0, seat0Melds),
                    new RoundSettlementSeatFacts(1, seat1Melds),
                    new RoundSettlementSeatFacts(2),
                    new RoundSettlementSeatFacts(3)
                },
                SettlementAbortiveDrawReason.FourKans);
            RoundStateMachine twoRound = TablePlan.Build(spec, twoSeats);
            RoundSettlementCalculation calc = RoundSettlementCalculator.CalculateAbortiveDraw(twoRound, twoFacts);
            Assert.Equal(new long[] { 25000, 25000, 25000, 25000 }, calc.Settlement.FinalScores.ToArray());
            Assert.Equal(RoundOutcomeKind.AbortiveDraw, calc.Outcome.Kind);
            RoundTransition applied = calc.Execute(twoRound);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());
            Assert.Equal(RoundEndReason.AbortiveDraw, applied.State!.Result!.EndReason);
        }

        [Fact]
        public void AbortiveNineTerminals_UsesCurrentSeatAndRejectsLateOrOtherSeat()
        {
            RoundSpec spec = new RoundSpec(
                "n2-nine-terminals",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            var validPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            validPlan.AddHand(0, 0, 32, 72, 104, 36, 68, 108, 112, 116, 4, 8, 12, 16);
            RoundStateMachine validRound = TablePlan.Build(spec, validPlan);
            var validFacts = new RoundSettlementFacts(
                "res-nine-valid",
                FixedUtc,
                9820,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        0,
                        isFirstTurnUninterrupted: true,
                        discardHistory: Array.Empty<TileId>())
                },
                SettlementAbortiveDrawReason.NineTerminals);
            RoundSettlementCalculation valid = RoundSettlementCalculator.CalculateAbortiveDraw(validRound, validFacts);
            Assert.Equal(RoundOutcomeKind.AbortiveDraw, valid.Outcome.Kind);
            RoundTransition applied = valid.Execute(validRound);
            Assert.True(applied.IsAccepted, applied.Error?.ToString());

            var otherSeatPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            otherSeatPlan.AddHand(0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
            otherSeatPlan.AddHand(1, 0, 32, 36, 68, 72, 104, 108, 112, 116, 120, 124, 128, 132);
            var otherFacts = new RoundSettlementFacts(
                "res-nine-other",
                FixedUtc,
                9821,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        0,
                        isFirstTurnUninterrupted: true,
                        discardHistory: Array.Empty<TileId>())
                },
                SettlementAbortiveDrawReason.NineTerminals);
            RoundStateMachine otherRound = TablePlan.Build(spec, otherSeatPlan);
            RoundSettlementRejectedException other = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(otherRound, otherFacts));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, other.Code);

            var latePlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            latePlan.AddHand(0, 0, 32, 72, 104, 36, 68, 108, 112, 116, 4, 8, 12, 16);
            latePlan.AddDiscard(0, 20);
            var lateFacts = new RoundSettlementFacts(
                "res-nine-late",
                FixedUtc,
                9822,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        0,
                        isFirstTurnUninterrupted: true,
                        discardHistory: new[] { new TileId(20) })
                },
                SettlementAbortiveDrawReason.NineTerminals);
            RoundStateMachine lateRound = TablePlan.Build(spec, latePlan);
            RoundSettlementRejectedException late = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateAbortiveDraw(lateRound, lateFacts));
            Assert.Equal(RoundSettlementRejectionCode.AbortiveReasonMismatch, late.Code);
        }

        [Fact]
        public void MeldShapeRejectsHonorChiAndUndefinedType()
        {
            RoundSpec spec = new RoundSpec(
                "n2-meld-shape",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            var honorChi = new Meld(
                MeldType.Chi,
                new[] { new TileId(108), new TileId(112), new TileId(116) },
                3,
                new TileId(108));
            var honorPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                DrawnTile = new TileId(53),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            honorPlan.AddHand(0, 4, 8, 12, 40, 44, 48, 76, 80, 84, 52, 53);
            honorPlan.AddMeld(0, honorChi);
            var honorFacts = new RoundSettlementFacts(
                "res-honor-chi",
                FixedUtc,
                9830,
                new[] { new RoundSettlementSeatFacts(0, new[] { honorChi }) });
            RoundStateMachine honorRound = TablePlan.Build(spec, honorPlan);
            RoundSettlementRejectedException honor = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(honorRound, honorFacts));
            Assert.Equal(RoundSettlementRejectionCode.MeldTypeMismatch, honor.Code);

            var undefined = new Meld(
                (MeldType)255,
                new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) },
                3,
                new TileId(0));
            var undefinedPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                DrawnTile = new TileId(53),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            undefinedPlan.AddHand(0, 4, 8, 12, 40, 44, 48, 76, 80, 84, 52, 53);
            undefinedPlan.AddMeld(0, undefined);
            var undefinedFacts = new RoundSettlementFacts(
                "res-undefined-meld",
                FixedUtc,
                9831,
                new[] { new RoundSettlementSeatFacts(0, new[] { undefined }) });
            RoundStateMachine undefinedRound = TablePlan.Build(spec, undefinedPlan);
            RoundSettlementRejectedException undefinedError = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(undefinedRound, undefinedFacts));
            Assert.Equal(RoundSettlementRejectionCode.MeldTypeMismatch, undefinedError.Code);
        }

        [Fact]
        public void HistoricalWinFlagsMustNotContradictTheMapOrKanState()
        {
            RoundSpec spec = new RoundSpec(
                "n2-history-flags",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);

            var tenhouPon = new Meld(
                MeldType.Pon,
                new[] { new TileId(0), new TileId(1), new TileId(2) },
                3,
                new TileId(0));
            var tenhouBadPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                DrawnTile = new TileId(53),
                DrawnSeat = 0,
                DrawnSource = DrawSource.DealExtra,
                KanCount = 1
            };
            tenhouBadPlan.AddHand(0, 4, 8, 12, 40, 44, 48, 76, 80, 84, 52, 53);
            tenhouBadPlan.AddMeld(0, tenhouPon);
            var tenhouBadFacts = new RoundSettlementFacts(
                "res-tenhou-bad",
                FixedUtc,
                9840,
                new[] { new RoundSettlementSeatFacts(0, new[] { tenhouPon }, isTenhou: true) });
            RoundStateMachine tenhouBadRound = TablePlan.Build(spec, tenhouBadPlan);
            RoundSettlementRejectedException tenhou = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(tenhouBadRound, tenhouBadFacts));
            Assert.Equal(RoundSettlementRejectionCode.WinFlagMismatch, tenhou.Code);

            var chiihouBadPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            chiihouBadPlan.AddHand(1, 16, 17, 18, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            chiihouBadPlan.AddHand(0, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112);
            chiihouBadPlan.AddWallTop(19);
            chiihouBadPlan.AddDiscard(1, 120);
            RoundStateMachine chiihouRound = TablePlan.Build(spec, chiihouBadPlan);
            RoundTransition chiihouDiscard = chiihouRound.Execute(
                RoundCommand.Discard(chiihouRound.RoundId, 0, new TileId(100)));
            Assert.True(chiihouDiscard.IsAccepted, chiihouDiscard.Error?.ToString());
            var chiihouFacts = new RoundSettlementFacts(
                "res-chiihou-bad",
                FixedUtc,
                9841,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        1,
                        isChiihou: true,
                        isFirstTurnUninterrupted: true,
                        discardHistory: new[] { new TileId(120) })
                });
            RoundSettlementRejectedException chiihou = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(chiihouDiscard.State!, chiihouFacts));
            Assert.Equal(RoundSettlementRejectionCode.WinFlagMismatch, chiihou.Code);

            var rinshanKan = new Meld(
                MeldType.ClosedKan,
                new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) },
                -1,
                new TileId(0));
            var rinshanPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                DrawnTile = new TileId(53),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Rinshan,
                KanCount = 1
            };
            rinshanPlan.AddHand(0, 4, 8, 12, 40, 44, 48, 76, 80, 84, 52, 53);
            rinshanPlan.AddMeld(0, rinshanKan);
            var rinshanFacts = new RoundSettlementFacts(
                "res-ippatsu-rinshan",
                FixedUtc,
                9842,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        0,
                        new[] { rinshanKan },
                        isRiichi: true,
                        isIppatsu: true,
                        isRinshan: true)
                });
            RoundStateMachine rinshanRound = TablePlan.Build(spec, rinshanPlan);
            RoundSettlementRejectedException ippatsu = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateTsumo(rinshanRound, rinshanFacts));
            Assert.Equal(RoundSettlementRejectionCode.WinFlagMismatch, ippatsu.Code);

            var tenhouGoodPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.DealExtra
            };
            tenhouGoodPlan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            RoundStateMachine tenhouGoodRound = TablePlan.Build(spec, tenhouGoodPlan);
            RoundSettlementCalculation tenhouGood = RoundSettlementCalculator.CalculateTsumo(
                tenhouGoodRound,
                new RoundSettlementFacts(
                    "res-tenhou-good",
                    FixedUtc,
                    9843,
                    new[] { new RoundSettlementSeatFacts(0, isTenhou: true) }));
            Assert.Contains(tenhouGood.Breakdown!.Yaku, y => y.Code == "TENHOU");

            var chiihouGoodPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            chiihouGoodPlan.AddHand(1, 16, 17, 18, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            chiihouGoodPlan.AddHand(0, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112);
            chiihouGoodPlan.AddWallTop(19);
            RoundStateMachine chiihouGoodRound = TablePlan.Build(spec, chiihouGoodPlan);
            RoundTransition chiihouGoodDiscard = chiihouGoodRound.Execute(
                RoundCommand.Discard(chiihouGoodRound.RoundId, 0, new TileId(100)));
            Assert.True(chiihouGoodDiscard.IsAccepted, chiihouGoodDiscard.Error?.ToString());
            RoundSettlementCalculation chiihouGood = RoundSettlementCalculator.CalculateTsumo(
                chiihouGoodDiscard.State!,
                new RoundSettlementFacts(
                    "res-chiihou-good",
                    FixedUtc,
                    9844,
                    new[]
                    {
                        new RoundSettlementSeatFacts(
                            1,
                            isChiihou: true,
                            isFirstTurnUninterrupted: true,
                            discardHistory: Array.Empty<TileId>())
                    }));
            Assert.Contains(chiihouGood.Breakdown!.Yaku, y => y.Code == "CHIIHOU");
        }

        [Fact]
        public void RonFuritenAndMissingHistoryAreRejectedButTsumoIsNotBlocked()
        {
            RoundSpec spec = new RoundSpec(
                "n2-furiten",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            var plan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true
            };
            plan.AddHand(1, 92, 96, 100, 12, 20, 40, 44, 48, 17, 21, 24, 13, 14);
            plan.AddHand(0, 16, 0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 52, 56, 60);
            plan.AddDiscard(1, 19);
            RoundStateMachine round = TablePlan.Build(spec, plan);
            RoundTransition discard = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, new TileId(16), new[] { 1 }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            RoundStateMachine window = discard.State!;

            var missing = new RoundSettlementFacts(
                "res-furiten-missing",
                FixedUtc,
                9850,
                new[] { new RoundSettlementSeatFacts(1) });
            RoundSettlementRejectedException missingError = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(window, 1, missing));
            Assert.Equal(RoundSettlementRejectionCode.FuritenEvidenceMissing, missingError.Code);

            var furiten = new RoundSettlementFacts(
                "res-furiten",
                FixedUtc,
                9851,
                new[]
                {
                    new RoundSettlementSeatFacts(1, discardHistory: new[] { new TileId(19) })
                });
            RoundSettlementRejectedException furitenError = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(window, 1, furiten));
            Assert.Equal(RoundSettlementRejectionCode.FuritenRon, furitenError.Code);

            var temporary = new RoundSettlementFacts(
                "res-furiten-temporary",
                FixedUtc,
                9852,
                new[]
                {
                    new RoundSettlementSeatFacts(
                        1,
                        isTemporaryFuriten: true,
                        discardHistory: Array.Empty<TileId>())
                });
            RoundSettlementRejectedException tempError = Assert.Throws<RoundSettlementRejectedException>(
                () => RoundSettlementCalculator.CalculateRon(window, 1, temporary));
            Assert.Equal(RoundSettlementRejectionCode.FuritenRon, tempError.Code);

            var tsumoPlan = new TablePlan(RoundPhase.CheatWindow)
            {
                WallTiles = 8,
                FillRemainingIntoWall = true,
                DrawnTile = new TileId(19),
                DrawnSeat = 0,
                DrawnSource = DrawSource.Wall
            };
            tsumoPlan.AddHand(0, 16, 17, 18, 19, 20, 24, 12, 13, 14, 21, 22, 23, 52, 53);
            RoundStateMachine tsumoRound = TablePlan.Build(spec, tsumoPlan);
            RoundSettlementCalculation tsumo = RoundSettlementCalculator.CalculateTsumo(
                tsumoRound,
                new RoundSettlementFacts(
                    "res-furiten-tsumo",
                    FixedUtc,
                    9853,
                    new[]
                    {
                        new RoundSettlementSeatFacts(
                            0,
                            isRiichi: true,
                            isRiichiFuriten: true)
                    }));
            Assert.NotNull(tsumo.Breakdown);
        }

        private static RoundSettlementFacts FourSeatAbortiveFacts(
            string resultId,
            SettlementAbortiveDrawReason reason)
        {
            return new RoundSettlementFacts(
                resultId,
                FixedUtc,
                9900,
                new[]
                {
                    new RoundSettlementSeatFacts(0),
                    new RoundSettlementSeatFacts(1),
                    new RoundSettlementSeatFacts(2),
                    new RoundSettlementSeatFacts(3)
                },
                reason);
        }
    }
    internal sealed class TablePlan
    {
        private readonly List<TilePlacement> _placements = new List<TilePlacement>();
        private readonly HashSet<int> _usedTiles = new HashSet<int>();
        private readonly HashSet<int> _usedPositions = new HashSet<int>();
        private readonly int[] _nextDiscard = new int[4];

        internal TablePlan(RoundPhase phase)
        {
            Phase = phase;
        }

        internal RoundPhase Phase { get; }

        internal int WallTiles { get; set; }

        internal TileId? DrawnTile { get; set; }

        internal int DrawnSeat { get; set; }

        internal DrawSource DrawnSource { get; set; } = DrawSource.Wall;

        internal int KanCount { get; set; }

        internal bool FillRemainingIntoWall { get; set; }

        internal TileId? LastDiscardTile { get; set; }

        internal int LastDiscardSeat { get; set; }

        private readonly List<TileId> _dora = new List<TileId>();
        private readonly List<TileId> _ura = new List<TileId>();
        private readonly List<TileId> _wallTop = new List<TileId>();
        private readonly List<(int Seat, TileId Tile)> _explicitDiscards =
            new List<(int Seat, TileId Tile)>();
        private readonly List<TileId>[] _hands =
        {
            new List<TileId>(), new List<TileId>(), new List<TileId>(), new List<TileId>()
        };
        private readonly List<Meld>[] _melds =
        {
            new List<Meld>(), new List<Meld>(), new List<Meld>(), new List<Meld>()
        };

        internal void AddHand(int seat, params int[] tileValues)
        {
            foreach (int value in tileValues)
            {
                _hands[seat].Add(new TileId(value));
            }
        }

        internal void AddMeld(int seat, Meld meld)
        {
            _melds[seat].Add(meld);
        }

        internal void AddDora(params int[] tileValues)
        {
            foreach (int value in tileValues)
            {
                _dora.Add(new TileId(value));
            }
        }

        internal void AddUra(params int[] tileValues)
        {
            foreach (int value in tileValues)
            {
                _ura.Add(new TileId(value));
            }
        }

        internal void AddWallTop(params int[] tileValues)
        {
            foreach (int value in tileValues)
            {
                _wallTop.Add(new TileId(value));
            }
        }

        internal void AddDiscard(int seat, int value)
        {
            _explicitDiscards.Add((seat, new TileId(value)));
        }

        internal void RemoveTile(int seat, TileId tile)
        {
            if (!_hands[seat].Remove(tile))
            {
                throw new InvalidOperationException("Seat " + seat + " does not hold " + tile + ".");
            }
        }

        internal void ClearHand(int seat)
        {
            _hands[seat].Clear();
        }

        internal static RoundStateMachine Build(RoundSpec spec, TablePlan plan)
        {
            TilePositionMap map = plan.BuildMap();
            return RoundStateMachine.CreateDealt(spec, map, plan.Phase);
        }

        internal TilePositionMap BuildMap()        {
            if (WallTiles < 0 || WallTiles > 122)
            {
                throw new InvalidOperationException("WallTiles must be 0..122.");
            }

            if (!FillRemainingIntoWall && _wallTop.Count > WallTiles)
            {
                throw new InvalidOperationException("WallTop exceeds WallTiles.");
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (_hands[seat].Count > 14)
                {
                    throw new InvalidOperationException("Seat " + seat + " has more than 14 hand tiles.");
                }

                for (int i = 0; i < _hands[seat].Count; i++)
                {
                    Place(_hands[seat][i], TilePosition.Hand(seat, i));
                }

                int baseSlot = 0;
                foreach (Meld meld in _melds[seat])
                {
                    for (int i = 0; i < meld.Tiles.Count; i++)
                    {
                        Place(meld.Tiles[i], TilePosition.Meld(seat, baseSlot + i));
                    }

                    baseSlot += 4;
                    if (baseSlot > 16)
                    {
                        throw new InvalidOperationException("Seat " + seat + " has too many melds.");
                    }
                }
            }

            foreach ((int seat, TileId tile) in _explicitDiscards)
            {
                Place(tile, TilePosition.Discard(seat, _nextDiscard[seat]++));
            }

            for (int i = 0; i < _dora.Count; i++)
            {
                Place(_dora[i], TilePosition.DeadWall(4 + (2 * i)));
            }

            for (int i = 0; i < _ura.Count; i++)
            {
                Place(_ura[i], TilePosition.DeadWall(5 + (2 * i)));
            }

            for (int i = 0; i < _wallTop.Count; i++)
            {
                Place(_wallTop[i], TilePosition.Wall(i));
            }

            for (int slot = 0; slot < 14; slot++)
            {
                TilePosition position = TilePosition.DeadWall(slot);
                if (!_usedPositions.Contains(PositionKey(position)))
                {
                    Place(NextUnused(), position);
                }
            }

            if (FillRemainingIntoWall)
            {
                int remaining = TileId.Count - _usedTiles.Count;
                WallTiles = Math.Min(122, _wallTop.Count + remaining);
            }

            for (int i = _wallTop.Count; i < WallTiles; i++)
            {
                Place(NextUnused(), TilePosition.Wall(i));
            }

            while (true)
            {
                int value = NextUnusedValue();
                if (value < 0)
                {
                    break;
                }

                int seat = value % 4;
                Place(
                    new TileId(value),
                    TilePosition.Discard(seat, _nextDiscard[seat]++));
            }

            LastDiscardEvidence? lastDiscard = null;
            if (LastDiscardTile.HasValue)
            {
                TilePosition position = TilePosition.Discard(LastDiscardSeat, 0);
                for (int i = 0; i < _placements.Count; i++)
                {
                    if (_placements[i].Tile == LastDiscardTile.Value)
                    {
                        position = _placements[i].Position;
                        break;
                    }
                }

                lastDiscard = new LastDiscardEvidence(LastDiscardTile.Value, position);
            }

            var doraIndicators = _dora.ToArray();
            var drawn = DrawnTile.HasValue
                ? new DrawnTileEvidence(DrawnTile.Value, DrawnSeat, DrawnSource)
                : (DrawnTileEvidence?)null;
            return TilePositionMap.FromPlacements(
                _placements,
                wallPointer: 0,
                wallEndExclusive: WallTiles,
                rinshanPointer: 0,
                lastDiscard: lastDiscard,
                revision: 0,
                rinshanDraw: null,
                doraIndicators: doraIndicators,
                kanCount: KanCount,
                drawnTile: drawn);
        }

        private void Place(TileId tile, TilePosition position)
        {
            if (!_usedTiles.Add(tile.Value))
            {
                throw new InvalidOperationException("Physical tile " + tile + " was placed twice.");
            }

            int key = PositionKey(position);
            if (!_usedPositions.Add(key))
            {
                throw new InvalidOperationException("Position " + position + " was placed twice.");
            }

            _placements.Add(new TilePlacement(tile, position));
        }

        private TileId NextUnused()
        {
            int value = NextUnusedValue();
            if (value < 0)
            {
                throw new InvalidOperationException("No unused physical tile remains.");
            }

            return new TileId(value);
        }

        private int NextUnusedValue()
        {
            for (int value = 0; value < TileId.Count; value++)
            {
                if (!_usedTiles.Contains(value))
                {
                    return value;
                }
            }

            return -1;
        }

        private static int PositionKey(TilePosition position)
        {
            return (((int)position.Zone * 4 + (position.Seat + 1)) * 256) + position.Index;
        }
    }
}

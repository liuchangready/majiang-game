using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Ai.Tests
{
    public sealed class ScoredCheatBasicAiStrategyTests
    {
        [Fact]
        public void CharacterStyleAndProfileValidateInputsAndKeepImmutability()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterStyle(aggression: -0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterStyle(defense: 1.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterStyle(cheatAggression: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterStyle(flushTendency: double.PositiveInfinity));

            Assert.Throws<ArgumentException>(() => new CharacterAiProfile(" "));
            var missingStrategy = new CharacterAiProfile("p-missing", null);
            Assert.Null(missingStrategy.StrategyId);
            Assert.Null(missingStrategy.FallbackStrategyId);

            var explicitFallback = new CharacterAiProfile("p-explicit-fb", null, fallbackStrategyId: AiProfileIds.LegacyHeuristic);
            Assert.Equal(AiProfileIds.LegacyHeuristic, explicitFallback.FallbackStrategyId);

            var basePolicy = new AiCheatPolicy(enabled: true, maxSpendPerRound: 30);
            var profile = CharacterAiProfile.CreateScoredBasic(
                profileId: "oct-scored",
                style: new CharacterStyle(aggression: 0.8, cheatAggression: 0.9),
                cheatPolicy: basePolicy,
                modelVersion: AiProfileIds.SupportedHeuristicModelVersion,
                seed: 777UL);
            CharacterAiProfile updated = profile.WithCheatPolicy(new AiCheatPolicy(enabled: false));
            Assert.True(profile.CheatPolicy.Enabled);
            Assert.False(updated.CheatPolicy.Enabled);
            Assert.Equal("oct-scored", updated.ProfileId);
            Assert.Equal(777UL, updated.Seed);
        }

        [Fact]
        public void OrdinaryDecisionDelegatesUnchangedToBasicStrategy()
        {
            var discard1m = new LegalActionOption(LegalActionKind.Discard, 0, new TileId(0));
            var discard9p = new LegalActionOption(LegalActionKind.Discard, 0, new TileId(68));
            var context = new BasicAiContext(0, false, null, Array.Empty<BasicCandidateFacts>());
            var obs = new AiObservation(
                "sess-1",
                new DecisionKey("round-1", 0, "dec-1"),
                1,
                0,
                30_000,
                RoundPhase.CheatWindow,
                0,
                1,
                0,
                0,
                0,
                70,
                new[] { new TileId(0), new TileId(4), new TileId(8), new TileId(68) },
                new TileId(68),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                Array.Empty<TileId>(),
                new long[] { 25_000, 25_000, 25_000, 25_000 },
                new[] { discard1m, discard9p },
                context);

            var basic = new BasicAiStrategy();
            var scored = new ScoredCheatBasicAiStrategy(
                CharacterAiProfile.CreateScoredBasic(
                    style: new CharacterStyle(aggression: 1.0, meldTendency: 1.0, flushTendency: 1.0, defense: 0.0, cheatAggression: 1.0),
                    cheatPolicy: new AiCheatPolicy(enabled: true)));

            AiStrategicChoice expected = basic.Decide(obs, CancellationToken.None);
            AiStrategicChoice actual = scored.Decide(obs, CancellationToken.None);
            Assert.Equal(expected.Action.Kind, actual.Action.Kind);
            Assert.Equal(expected.Action.Tile, actual.Action.Tile);
            Assert.Equal(expected.Action.OptionId, actual.Action.OptionId);
        }

        [Fact]
        public void ShantenCalculatorHandlesWinningTenpaiAndReducedHands()
        {
            // Complete 14-tile hand: 123m 456m 789m 111s 22s
            var winning = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(12), new TileId(16), new TileId(20),
                new TileId(24), new TileId(28), new TileId(32),
                new TileId(72), new TileId(73), new TileId(74),
                new TileId(76), new TileId(77)
            };
            Assert.Equal(-1, AiShantenCalculator.CalculateShanten(winning));
            Assert.Equal(-1, AiShantenCalculator.CalculateBestPostDiscardShanten(winning));

            // 13-tile tenpai hand (waiting on 2s): 123m 456m 789m 111s 2s
            var tenpai = winning.Take(13).ToArray();
            Assert.Equal(0, AiShantenCalculator.CalculateShanten(tenpai));

            // 13-tile chiitoitsu tenpai: 6 pairs + 1 single
            var chiitoiTenpai = new[]
            {
                new TileId(0), new TileId(1),
                new TileId(8), new TileId(9),
                new TileId(16), new TileId(17),
                new TileId(36), new TileId(37),
                new TileId(44), new TileId(45),
                new TileId(72), new TileId(73),
                new TileId(124)
            };
            Assert.Equal(0, AiShantenCalculator.CalculateShanten(chiitoiTenpai));
        }

        [Fact]
        public void SleeveBloomOfferSelectsWinningOrShantenImprovingAndRedFiveOverPlainFive()
        {
            // Hand: 123m 456m 789m 111s 2s (tenpai waiting on 2s=77) + first=East(108).
            // Second=2s(77) wins immediately (Tsumo), Third=5p(52) does not -> sleeve-second must win!
            var base13 = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(12), new TileId(16), new TileId(20),
                new TileId(24), new TileId(28), new TileId(32),
                new TileId(72), new TileId(73), new TileId(74),
                new TileId(76)
            };
            var first = new TileId(108); // East
            var second = new TileId(77); // 2s -> Tsumo!
            var third = new TileId(52);  // 5p

            var hand14 = base13.Concat(new[] { first }).ToArray();
            var context = new ScoredCheatVisibleContext(hand14, ownDrawnTileId: first, characterId: PlayerCharacterIds.Octopus);
            var offer = new AiCheatOpportunity(
                seat: 1,
                roundId: "round-sleeve-win",
                stateVersion: 5,
                controlEpoch: 0,
                cheatPoints: 110,
                isOfferChoice: true,
                options: new[]
                {
                    new AiCheatOption("sleeve-first", "sleeve_bloom", 0, true, true, new[] { first }),
                    new AiCheatOption("sleeve-second", "sleeve_bloom", 0, false, true, new[] { second }),
                    new AiCheatOption("sleeve-third", "sleeve_bloom", 0, false, true, new[] { third })
                },
                sleeveDrawKind: AiSleeveBloomDrawKind.Normal,
                sleeveFirstTileId: first,
                sleeveSecondTileId: second,
                sleeveThirdTileId: third);

            // Even with cheats disabled, zero balance, reserve above points, and cap exhausted, paid offer still resolves!
            var constrained = new ScoredCheatBasicAiStrategy(
                new AiCheatPolicy(
                    enabled: false,
                    maxSpendPerRound: 0,
                    reservePoints: 120,
                    capabilityToggles: new Dictionary<string, bool> { ["sleeve_bloom"] = false }));
            Assert.Equal("sleeve-second", constrained.ChooseCheat(offer, context));

            // When first = normal 5m (16) and third = red 5m (19) with identical shanten, red 5m wins!
            var redHand13 = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(36), new TileId(40), new TileId(44),
                new TileId(72), new TileId(76), new TileId(80),
                new TileId(84), new TileId(85),
                new TileId(12), new TileId(20) // 4m, 6m -> waiting for 5m
            };
            var normalFive = new TileId(16);
            var junk = new TileId(120); // North
            var redFive = new TileId(19); // Red 5m
            var redOffer = new AiCheatOpportunity(
                seat: 1,
                roundId: "round-red-5m",
                stateVersion: 6,
                controlEpoch: 0,
                cheatPoints: 110,
                isOfferChoice: true,
                options: new[]
                {
                    new AiCheatOption("sleeve-first", "sleeve_bloom", 0, true, true, new[] { normalFive }),
                    new AiCheatOption("sleeve-second", "sleeve_bloom", 0, false, true, new[] { junk }),
                    new AiCheatOption("sleeve-third", "sleeve_bloom", 0, false, true, new[] { redFive })
                },
                sleeveDrawKind: AiSleeveBloomDrawKind.Normal,
                sleeveFirstTileId: normalFive,
                sleeveSecondTileId: junk,
                sleeveThirdTileId: redFive);
            var redContext = new ScoredCheatVisibleContext(redHand13.Concat(new[] { normalFive }).ToArray(), ownDrawnTileId: normalFive);
            Assert.Equal("sleeve-third", new ScoredCheatBasicAiStrategy(new AiCheatPolicy(enabled: true)).ChooseCheat(redOffer, redContext));
        }

        [Fact]
        public void SleeveActivationSkipsWhenWinningAndPrefersHighExpectationWhenTenpaiWithJunkDraw()
        {
            var strategy = new ScoredCheatBasicAiStrategy(
                CharacterAiProfile.CreateScoredBasic(
                    style: new CharacterStyle(aggression: 0.8, cheatAggression: 0.8),
                    cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 60)));

            // Case 1: Already winning hand -> sleeve-activate MUST NOT spend 10 points!
            var winning = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(12), new TileId(16), new TileId(20),
                new TileId(24), new TileId(28), new TileId(32),
                new TileId(72), new TileId(73), new TileId(74),
                new TileId(76), new TileId(77)
            };
            var winOpp = new AiCheatOpportunity(
                seat: 0,
                roundId: "round-act-1",
                stateVersion: 1,
                controlEpoch: 0,
                cheatPoints: 120,
                isOfferChoice: false,
                options: new[] { new AiCheatOption("sleeve-activate", "sleeve_bloom", 10, false, false, new[] { new TileId(77) }) },
                sleeveDrawKind: AiSleeveBloomDrawKind.Normal);
            Assert.Null(strategy.ChooseCheat(winOpp, new ScoredCheatVisibleContext(winning, ownDrawnTileId: new TileId(77))));

            // Case 2: 1-shanten / tenpai hand with isolated useless drawn tile (North 120) -> activates!
            // Hand: 123m 456m 789m 111s 2s + drawn North(120) -> replacing North with 2s/1s/3s/etc. wins or widens ukeire.
            var good13 = winning.Take(13).ToArray();
            var junkDraw = new TileId(120);
            var actHand = good13.Concat(new[] { junkDraw }).ToArray();
            var actOpp = new AiCheatOpportunity(
                seat: 0,
                roundId: "round-act-2",
                stateVersion: 2,
                controlEpoch: 0,
                cheatPoints: 120,
                isOfferChoice: false,
                options: new[] { new AiCheatOption("sleeve-activate", "sleeve_bloom", 10, false, false, new[] { junkDraw }) },
                sleeveDrawKind: AiSleeveBloomDrawKind.Normal);
            Assert.Equal("sleeve-activate", strategy.ChooseCheat(actOpp, new ScoredCheatVisibleContext(actHand, ownDrawnTileId: junkDraw, characterId: PlayerCharacterIds.Octopus)));

            // Case 2b (D-01-01): Normal vs Rinshan vs null SleeveDrawKind must differ for octopus; null never gets Normal bonus.
            var rinshanOpp = new AiCheatOpportunity(
                seat: 0,
                roundId: "round-act-2b-rinshan",
                stateVersion: 2,
                controlEpoch: 0,
                cheatPoints: 120,
                isOfferChoice: false,
                options: new[] { new AiCheatOption("sleeve-activate", "sleeve_bloom", 10, false, false, new[] { junkDraw }) },
                sleeveDrawKind: AiSleeveBloomDrawKind.Rinshan);
            var nullKindOpp = new AiCheatOpportunity(
                seat: 0,
                roundId: "round-act-2b-null",
                stateVersion: 2,
                controlEpoch: 0,
                cheatPoints: 120,
                isOfferChoice: false,
                options: new[] { new AiCheatOption("sleeve-activate", "sleeve_bloom", 10, false, false, new[] { junkDraw }) },
                sleeveDrawKind: null);
            var octCtx = new ScoredCheatVisibleContext(actHand, ownDrawnTileId: junkDraw, characterId: PlayerCharacterIds.Octopus);
            double normalGain = strategy.EvaluateCheatOptions(actOpp, octCtx).Single().ExpectedOrActualGain;
            double rinshanGain = strategy.EvaluateCheatOptions(rinshanOpp, octCtx).Single().ExpectedOrActualGain;
            double nullGain = strategy.EvaluateCheatOptions(nullKindOpp, octCtx).Single().ExpectedOrActualGain;
            Assert.True(normalGain > rinshanGain, $"Normal={normalGain} should exceed Rinshan={rinshanGain}");
            Assert.True(normalGain > nullGain, $"Normal={normalGain} should exceed null drawKind={nullGain}");
            Assert.Equal(nullGain, rinshanGain, precision: 6);

            // Case 3: Reserve gate blocks activation when spendable < 10
            var tightReserve = new ScoredCheatBasicAiStrategy(
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 60, reservePoints: 115));
            Assert.Null(tightReserve.ChooseCheat(actOpp, new ScoredCheatVisibleContext(actHand, ownDrawnTileId: junkDraw)));
        }

        [Fact]
        public void BeamSwapOfferPrefersMarkedWinningSwapAndIsInvariantToHiddenUnmarkedSlots()
        {
            // Hand: 123m 456m 789m 111s 2s (tenpai) + drawn East(108). Give East(108) for marked 2s(77) -> Tsumo!
            var eligible = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(12), new TileId(16), new TileId(20),
                new TileId(24), new TileId(28), new TileId(32),
                new TileId(72), new TileId(73), new TileId(74),
                new TileId(76)
            };
            var drawn = new TileId(108); // East
            var hand = eligible.Concat(new[] { drawn }).ToArray();

            var slotMarked = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 10);
            var slotUnmarkedA = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 11);
            var slotUnmarkedB = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 12);

            // Marked slot holds 2s(77). Wait: we give 1m(0) or 2s? Wait: eligible excludes drawn East(108),
            // so hand has 123m 456m 789m 111s 2s(76) and drawn East(108).
            // Wait: if we give 2s(76) and take 2s(77), hand still has 123m 456m 789m 111s 2s + East(108).
            // Better: give 1s(72) or give 9m(32) when 9m(32) is isolated and 2s(77) completes 22s!
            // Let's make eligible have isolated North(120) and drawn 2s(76), with marked slot holding 2s(77)!
            var eligibleWithJunk = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(12), new TileId(16), new TileId(20),
                new TileId(24), new TileId(28), new TileId(32),
                new TileId(72), new TileId(73), new TileId(74),
                new TileId(120) // isolated North
            };
            var drawn2s = new TileId(76);
            var marked2s = new TileId(77);

            AiCheatOpportunity BuildBeamOpp(int seat) => new AiCheatOpportunity(
                seat: seat,
                roundId: "round-beam-priv",
                stateVersion: 10,
                controlEpoch: 0,
                cheatPoints: 110,
                isOfferChoice: true,
                options: new[]
                {
                    new AiCheatOption("beam-keep", "beam_swap", 0, true, true),
                    new AiCheatOption("beam-swap-120-WallSlot-11", "beam_swap", 0, false, true, new[] { new TileId(120) }, slotUnmarkedA, false, null),
                    new AiCheatOption("beam-swap-120-WallSlot-12", "beam_swap", 0, false, true, new[] { new TileId(120) }, slotUnmarkedB, false, null),
                    new AiCheatOption("beam-swap-120-WallSlot-10", "beam_swap", 0, false, true, new[] { new TileId(120) }, slotMarked, true, marked2s),
                    new AiCheatOption("beam-swap-0-WallSlot-10", "beam_swap", 0, false, true, new[] { new TileId(0) }, slotMarked, true, marked2s)
                },
                beamWindow: AiBeamSwapWindow.Wall,
                beamDrawnTileId: drawn2s,
                beamEligibleHandTileIds: eligibleWithJunk,
                beamCandidates: new[]
                {
                    new AiBeamSwapCandidateFacts(slotMarked, marked: true, tileId: marked2s.Value),
                    new AiBeamSwapCandidateFacts(slotUnmarkedA, marked: false, tileId: null),
                    new AiBeamSwapCandidateFacts(slotUnmarkedB, marked: false, tileId: null)
                });

            var strategy = new ScoredCheatBasicAiStrategy(new AiCheatPolicy(enabled: true));
            AiCheatOpportunity oppA = BuildBeamOpp(1);
            AiCheatOpportunity oppB = BuildBeamOpp(1);

            string? chosenA = strategy.ChooseCheat(oppA);
            IReadOnlyList<ScoredCheatOptionEvaluation> evalA = strategy.EvaluateCheatOptions(oppA);
            IReadOnlyList<ScoredCheatOptionEvaluation> evalB = strategy.EvaluateCheatOptions(oppB);

            // Giving North(120) for marked 2s(77) achieves Tsumo (-1 shanten) -> must win!
            Assert.Equal("beam-swap-120-WallSlot-10", chosenA);
            Assert.Equal(evalA.Count, evalB.Count);
            for (int i = 0; i < evalA.Count; i++)
            {
                Assert.Equal(evalA[i].OptionId, evalB[i].OptionId);
                Assert.Equal(evalA[i].Score, evalB[i].Score);
            }

            // Negative case: when hand is ALREADY winning, beam-keep (IsSafeDefault) beats swapping into unmarked/worse slots!
            var alreadyWinEligible = new[]
            {
                new TileId(0), new TileId(4), new TileId(8),
                new TileId(12), new TileId(16), new TileId(20),
                new TileId(24), new TileId(28), new TileId(32),
                new TileId(72), new TileId(73), new TileId(74),
                new TileId(76)
            };
            var winBeamOpp = new AiCheatOpportunity(
                seat: 1,
                roundId: "round-beam-win",
                stateVersion: 11,
                controlEpoch: 0,
                cheatPoints: 110,
                isOfferChoice: true,
                options: new[]
                {
                    new AiCheatOption("beam-keep", "beam_swap", 0, true, true),
                    new AiCheatOption("beam-swap-0-WallSlot-11", "beam_swap", 0, false, true, new[] { new TileId(0) }, slotUnmarkedA, false, null),
                    new AiCheatOption("beam-swap-76-WallSlot-10", "beam_swap", 0, false, true, new[] { new TileId(76) }, slotMarked, true, new TileId(120))
                },
                beamWindow: AiBeamSwapWindow.Wall,
                beamDrawnTileId: marked2s,
                beamEligibleHandTileIds: alreadyWinEligible,
                beamCandidates: new[]
                {
                    new AiBeamSwapCandidateFacts(slotMarked, marked: true, tileId: 120),
                    new AiBeamSwapCandidateFacts(slotUnmarkedA, marked: false, tileId: null)
                });
            Assert.Equal("beam-keep", strategy.ChooseCheat(winBeamOpp));
        }

        [Fact]
        public void DeterministicTieBreakOrderPrefersSafeDefaultThenSlotAndTileIdsIndependentOfInputOrder()
        {
            var strategy = new ScoredCheatBasicAiStrategy(new AiCheatPolicy(enabled: true));

            // 1. Sleeve tie: two tiles with identical shanten & TileValue (e.g. 4p=48 and 6p=56 in empty/neutral hand)
            // -> IsSafeDefault ("sleeve-first") must win regardless of option order!
            var first = new TileId(48);
            var second = new TileId(56);
            var optFirst = new AiCheatOption("sleeve-first", "sleeve_bloom", 0, true, true, new[] { first });
            var optSecond = new AiCheatOption("sleeve-second", "sleeve_bloom", 0, false, true, new[] { second });

            var oppOrder1 = new AiCheatOpportunity(
                0, "r-tie-1", 1, 0, 100, true,
                new[] { optSecond, optFirst },
                sleeveFirstTileId: first,
                sleeveSecondTileId: second);
            var oppOrder2 = new AiCheatOpportunity(
                0, "r-tie-1", 1, 0, 100, true,
                new[] { optFirst, optSecond },
                sleeveFirstTileId: first,
                sleeveSecondTileId: second);

            Assert.Equal("sleeve-first", strategy.ChooseCheat(oppOrder1));
            Assert.Equal("sleeve-first", strategy.ChooseCheat(oppOrder2));

            // 2. Unmarked beam swap tie: two unmarked slots with identical hand tile -> lower TargetSlot.Index wins
            // regardless of input list order!
            var eligible = new[] { new TileId(120), new TileId(0), new TileId(4), new TileId(8), new TileId(12), new TileId(16), new TileId(20), new TileId(24), new TileId(28), new TileId(32), new TileId(72), new TileId(73), new TileId(74) };
            var slot5 = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 5);
            var slot2 = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 2);
            var swap5 = new AiCheatOption("beam-swap-120-WallSlot-5", "beam_swap", 0, false, true, new[] { new TileId(120) }, slot5, false, null);
            var swap2 = new AiCheatOption("beam-swap-120-WallSlot-2", "beam_swap", 0, false, true, new[] { new TileId(120) }, slot2, false, null);
            var beamKeep = new AiCheatOption("beam-keep", "beam_swap", 0, true, true);

            var beamTieA = new AiCheatOpportunity(
                0, "r-tie-2", 2, 0, 100, true,
                new[] { swap5, beamKeep, swap2 },
                beamWindow: AiBeamSwapWindow.Wall,
                beamDrawnTileId: new TileId(76),
                beamEligibleHandTileIds: eligible,
                beamCandidates: new[]
                {
                    new AiBeamSwapCandidateFacts(slot5, false, null),
                    new AiBeamSwapCandidateFacts(slot2, false, null)
                });
            var beamTieB = new AiCheatOpportunity(
                0, "r-tie-2", 2, 0, 100, true,
                new[] { swap2, swap5, beamKeep },
                beamWindow: AiBeamSwapWindow.Wall,
                beamDrawnTileId: new TileId(76),
                beamEligibleHandTileIds: eligible,
                beamCandidates: new[]
                {
                    new AiBeamSwapCandidateFacts(slot2, false, null),
                    new AiBeamSwapCandidateFacts(slot5, false, null)
                });

            string? chosenTieA = strategy.ChooseCheat(beamTieA);
            string? chosenTieB = strategy.ChooseCheat(beamTieB);
            Assert.Equal("beam-swap-120-WallSlot-2", chosenTieA);
            Assert.Equal(chosenTieA, chosenTieB);

            // 3. TileMark tie: identical tile values (two copies of 1m: copy 0 vs copy 1) -> lower TileId wins!
            var markHand = new[] { new TileId(0), new TileId(1), new TileId(4), new TileId(8), new TileId(12) };
            var grpLow = new AiCheatOption("tile-mark-0-4-8-12", "tile_mark", 1, false, false, new[] { new TileId(0), new TileId(4), new TileId(8), new TileId(12) });
            var grpHigh = new AiCheatOption("tile-mark-1-4-8-12", "tile_mark", 1, false, false, new[] { new TileId(1), new TileId(4), new TileId(8), new TileId(12) });
            var markOpp = new AiCheatOpportunity(
                0, "r-tie-3", 3, 0, 120, false,
                new[] { grpHigh, grpLow },
                markableHandTileIds: markHand);
            Assert.Equal("tile-mark-0-4-8-12", strategy.ChooseCheat(markOpp));
        }

        [Fact]
        public void BudgetRestoreAndNewRoundResetWorkPerSeatWithoutCrossSeatBleed()
        {
            var strategy = new ScoredCheatBasicAiStrategy(
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 10));

            // Seat 0 already spent 10 in round-1; seat 1 spent 0 in round-1.
            strategy.RestoreRoundSpend("round-1", 0, 10);
            strategy.RestoreRoundSpend("round-1", 1, 0);

            var markTiles = new[] { new TileId(0), new TileId(4), new TileId(8), new TileId(12) };
            var opt = new AiCheatOption("tile-mark-0-4-8-12", "tile_mark", 1, false, false, markTiles);

            var oppSeat0 = new AiCheatOpportunity(0, "round-1", 1, 0, 110, false, new[] { opt }, markableHandTileIds: markTiles);
            var oppSeat1 = new AiCheatOpportunity(1, "round-1", 1, 0, 120, false, new[] { opt }, markableHandTileIds: markTiles);

            Assert.Null(strategy.ChooseCheat(oppSeat0));
            Assert.Equal("tile-mark-0-4-8-12", strategy.ChooseCheat(oppSeat1));

            // New roundId resets round spend naturally for seat 0.
            var oppSeat0NewRound = new AiCheatOpportunity(0, "round-2", 0, 0, 120, false, new[] { opt }, markableHandTileIds: markTiles);
            Assert.Equal("tile-mark-0-4-8-12", strategy.ChooseCheat(oppSeat0NewRound));
        }
    }
}

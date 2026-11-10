using System;
using System.Collections.Generic;
using System.Threading;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    public sealed class RandomCheatBasicAiStrategyTests
    {
        [Fact]
        public void SeededRandomIsDeterministicAndBounded()
        {
            var left = new SeededAiRandom(42UL);
            var right = new SeededAiRandom(42UL);
            for (int i = 0; i < 100; i++)
            {
                int a = left.Next(7);
                int b = right.Next(7);
                Assert.Equal(a, b);
                Assert.InRange(a, 0, 6);
            }
        }

        [Fact]
        public void CumulativePerRoundBudgetIsNeverExceeded()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(21UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 20));
            AiCheatOpportunity opportunity = Opportunity(
                points: 120,
                isOfferChoice: true,
                new AiCheatOption("mark", "tile_mark", 1, false, true),
                new AiCheatOption("sleeve", "sleeve_bloom", 10, false, true),
                new AiCheatOption("beam", "beam_swap", 10, false, true));

            int spent = 0;
            for (int i = 0; i < 20; i++)
            {
                string? optionId = strategy.ChooseCheat(opportunity);
                if (optionId == null)
                {
                    continue;
                }

                spent += optionId == "mark" ? 1 : 10;
            }

            Assert.True(spent <= 20, "strategy exceeded its per-round budget: " + spent);

            AiCheatOpportunity nextRound = new AiCheatOpportunity(
                0,
                "round-2",
                8,
                1,
                120,
                true,
                new[] { new AiCheatOption("beam", "beam_swap", 10, false, true) });
            Assert.Equal("beam", strategy.ChooseCheat(nextRound));
        }

        [Fact]
        public void NoAuthorizedCandidateYieldsNoCheat()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(5UL),
                new AiCheatPolicy(enabled: true));
            AiCheatOpportunity empty = Opportunity(points: 120, isOfferChoice: false);
            Assert.Null(strategy.ChooseCheat(empty));
        }

        [Fact]
        public void SameSeedProducesTheSameCheatSequence()
        {
            AiCheatOpportunity opportunity = Opportunity(
                points: 120,
                isOfferChoice: false,
                new AiCheatOption("mark", "tile_mark", 1, false, false),
                new AiCheatOption("sleeve", "sleeve_bloom", 10, false, false),
                new AiCheatOption("beam", "beam_swap", 10, false, false));
            var left = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(77UL),
                new AiCheatPolicy(enabled: true));
            var right = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(77UL),
                new AiCheatPolicy(enabled: true));
            for (int i = 0; i < 40; i++)
            {
                Assert.Equal(left.ChooseCheat(opportunity), right.ChooseCheat(opportunity));
            }
        }

        [Fact]
        public void ReservePointsAreRetainedForPositiveCostCandidates()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(31UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 60, reservePoints: 5));
            AiCheatOption mark = new AiCheatOption("mark", "tile_mark", 1, false, true);

            Assert.Null(strategy.ChooseCheat(Opportunity(points: 5, isOfferChoice: true, mark)));
            Assert.Equal(
                "mark",
                strategy.ChooseCheat(Opportunity(points: 6, isOfferChoice: true, mark)));
        }

        [Fact]
        public void ZeroCostOfferResolutionSurvivesReserveAndEmptyBalance()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(32UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 60, reservePoints: 5));
            Assert.Equal(
                "first",
                strategy.ChooseCheat(Opportunity(
                    points: 0,
                    isOfferChoice: true,
                    new AiCheatOption("first", "sleeve_bloom", 0, true, true))));
        }

        [Fact]
        public void SpendThatWouldLandExactlyOnReserveIsAllowedButTheNextCostIsNot()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(33UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 10, reservePoints: 5));
            Assert.Equal(
                "ten",
                strategy.ChooseCheat(Opportunity(
                    points: 15,
                    isOfferChoice: true,
                    new AiCheatOption("ten", "tile_mark", 10, false, true))));
            Assert.Null(strategy.ChooseCheat(Opportunity(
                points: 15,
                isOfferChoice: true,
                new AiCheatOption("one", "tile_mark", 1, false, true))));
        }

        [Fact]
        public void BudgetAdditionDoesNotOverflow()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(34UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: int.MaxValue));
            Assert.Equal(
                "max",
                strategy.ChooseCheat(Opportunity(
                    points: int.MaxValue,
                    isOfferChoice: true,
                    new AiCheatOption("max", "tile_mark", int.MaxValue, false, true))));
            Assert.Null(strategy.ChooseCheat(Opportunity(
                points: int.MaxValue,
                isOfferChoice: true,
                new AiCheatOption("one", "tile_mark", 1, false, true))));
        }

        [Fact]
        public void RestoredRoundSpendIsNotResetForTheSameRoundButANewRoundStartsFresh()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(35UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 10));
            strategy.RestoreRoundSpend("round-1", 0, 10);
            Assert.Null(strategy.ChooseCheat(Opportunity(
                points: 120,
                isOfferChoice: true,
                new AiCheatOption("one", "tile_mark", 1, false, true))));

            strategy.RestoreRoundSpend("round-1", 0, 10);
            Assert.Null(strategy.ChooseCheat(Opportunity(
                points: 120,
                isOfferChoice: true,
                new AiCheatOption("one", "tile_mark", 1, false, true))));

            strategy.RestoreRoundSpend("round-2", 0, 0);
            Assert.Equal(
                "one",
                strategy.ChooseCheat(Opportunity(
                    points: 120,
                    isOfferChoice: true,
                    new AiCheatOption("one", "tile_mark", 1, false, true))));
        }

        [Fact]
        public void DisabledPolicyNeverChoosesACheat()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(1UL),
                new AiCheatPolicy(enabled: false));
            AiCheatOpportunity opportunity = Opportunity(
                points: 120,
                isOfferChoice: false,
                new AiCheatOption("tile-mark", "tile_mark", 1, false, false),
                new AiCheatOption("beam-activate", "beam_swap", 10, false, false));

            for (int i = 0; i < 50; i++)
            {
                Assert.Null(strategy.ChooseCheat(opportunity));
            }
        }

        [Fact]
        public void UnaffordableAndDisabledCapabilitiesAreFiltered()
        {
            var policy = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 10,
                capabilityToggles: new Dictionary<string, bool> { ["beam_swap"] = false });
            var strategy = new RandomCheatBasicAiStrategy(new SeededAiRandom(9UL), policy);
            AiCheatOpportunity opportunity = Opportunity(
                points: 5,
                isOfferChoice: true,
                new AiCheatOption("sleeve", "sleeve_bloom", 10, false, true),
                new AiCheatOption("beam", "beam_swap", 1, false, true),
                new AiCheatOption("mark", "tile_mark", 6, false, true));

            // Only the tile mark survives (cost 6 <= 5 is false too), so no eligible option.
            Assert.Null(strategy.ChooseCheat(opportunity));

            AiCheatOpportunity affordable = Opportunity(
                points: 10,
                isOfferChoice: true,
                new AiCheatOption("mark", "tile_mark", 1, true, true));
            Assert.Equal("mark", strategy.ChooseCheat(affordable));
        }

        [Fact]
        public void OfferChoiceNeverSkipsAndReturnsAnAuthorizedOption()
        {
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(123UL),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 60));
            AiCheatOpportunity opportunity = Opportunity(
                points: 120,
                isOfferChoice: true,
                new AiCheatOption("first", "sleeve_bloom", 0, true, true),
                new AiCheatOption("second", "sleeve_bloom", 0, false, true));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 50; i++)
            {
                string? choice = strategy.ChooseCheat(opportunity);
                Assert.NotNull(choice);
                Assert.Contains(choice!, new[] { "first", "second" });
                seen.Add(choice!);
            }

            Assert.Equal(2, seen.Count);
        }

        [Fact]
        public void OrdinaryDecisionDelegatesToBasicStrategy()
        {
            AiObservation observation = Observation();
            var basic = new BasicAiStrategy();
            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(3UL),
                new AiCheatPolicy(enabled: true));

            AiStrategicChoice expected = basic.Decide(observation, CancellationToken.None);
            AiStrategicChoice actual = strategy.Decide(observation, CancellationToken.None);
            Assert.Equal(expected.Action.Kind, actual.Action.Kind);
            Assert.Equal(expected.Action.Tile, actual.Action.Tile);
            Assert.Equal(expected.Action.OptionId, actual.Action.OptionId);
        }

        private static AiCheatOpportunity Opportunity(
            int points,
            bool isOfferChoice,
            params AiCheatOption[] options)
        {
            return new AiCheatOpportunity(
                0,
                "round-1",
                7,
                1,
                points,
                isOfferChoice,
                options);
        }

        private static AiObservation Observation()
        {
            var discard = new LegalActionOption(LegalActionKind.Discard, 0, new TileId(0));
            var context = new BasicAiContext(0, false, null, Array.Empty<BasicCandidateFacts>());
            return new AiObservation(
                "session-1",
                new DecisionKey("round-1", 0, "decision-1"),
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
                new[] { new TileId(0) },
                new TileId(0),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                Array.Empty<TileId>(),
                new long[] { 25_000, 25_000, 25_000, 25_000 },
                new[] { discard },
                context);
        }
    }
}

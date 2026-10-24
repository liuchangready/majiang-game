using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Behaviour matrix for the ENG-013 port of V4
    /// <c>engine/src/main/kotlin/com/majiang/ai/AIStrategy.kt</c> <c>BasicAIStrategy</c>.
    ///
    /// The tests assert observable decisions, not an internal score. They pin the V4 branch
    /// order, list-order ties, physical copy identity, hand-removal-once, the two wind
    /// sources, the score threshold, wait-type counting (not remaining physical copies),
    /// typed metadata binding and the real ReasonAsync -> Submit -> Replay path.
    /// </summary>
    public class BasicAiStrategyTests
    {
        private const string RoundId = "round-basic-1";
        private const string DecisionId = "turn-round-basic-1-9-seat1";
        private const int Seat = 1;
        private const long Epoch = 0L;
        private const long Deadline = 23000L;

        private static readonly BasicAiStrategy Basic = new BasicAiStrategy();
        private static readonly TrivialAiStrategy Trivial = new TrivialAiStrategy();
        private static readonly TsumogiriAiStrategy Tsumogiri = new TsumogiriAiStrategy();

        // Pin five: typeIndex 22, physical ids 88..91; copy 3 (91) is the red five.
        private static readonly TileId PinFiveNormal = T(22, 0);
        private static readonly TileId PinFiveRed = T(22, 3);

        // ---------------------------------------------------------------------
        // Branch priority: Ron > Tsumo > abortive > riichi > closed kan > pon >
        // response Pass > riichi tsumogiri > disposability > Pass / first legal
        // ---------------------------------------------------------------------

        [Fact]
        public void BasicSelectsRonBeforeEveryOtherBranch()
        {
            LegalActionOption pass = Pass();
            LegalActionOption tsumo = Tsumo();
            LegalActionOption abortive = Other("abortive");
            LegalActionOption riichi = Riichi(T(4), "riichi-5m");
            LegalActionOption discard = Discard(T(4), "discard-5m");
            LegalActionOption ron = Ron("ron");

            var actions = new[] { discard, tsumo, abortive, riichi, pass, ron };
            BasicAiContext context = Context(new[] { AbortiveFact(abortive) });

            AiStrategicChoice choice = Choose(actions, context);

            Assert.Same(ron, choice.Action);
            Assert.Equal(0d, choice.HeuristicScore);
        }

        [Fact]
        public void BasicSelectsTheFirstRonAndFirstTsumoInListOrder()
        {
            LegalActionOption firstRon = Ron("ron-1");
            LegalActionOption secondRon = Ron("ron-2");
            LegalActionOption firstTsumo = Tsumo("tsumo-1");
            LegalActionOption secondTsumo = Tsumo("tsumo-2");

            var withRons = new[] { firstRon, secondRon, firstTsumo, secondTsumo };
            Assert.Same(firstRon, Choose(withRons, Context()).Action);

            var withoutRons = new[] { firstTsumo, secondTsumo };
            Assert.Same(firstTsumo, Choose(withoutRons, Context()).Action);
        }

        [Fact]
        public void BasicSelectsTsumoWhenRonAbsent()
        {
            LegalActionOption abortive = Other("abortive");
            LegalActionOption tsumo = Tsumo();
            LegalActionOption riichi = Riichi(T(4), "riichi-5m");
            LegalActionOption discard = Discard(T(4), "discard-5m");

            var actions = new[] { abortive, tsumo, riichi, discard };
            BasicAiContext context = Context(new[] { AbortiveFact(abortive) });

            Assert.Same(tsumo, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicSelectsExplicitNineTerminalsAbortiveBeforeRiichi()
        {
            LegalActionOption riichi = Riichi(T(4), "riichi-5m");
            LegalActionOption abortive = Other("abortive");

            var actions = new[] { riichi, abortive };
            BasicAiContext context = Context(new[] { AbortiveFact(abortive) });

            Assert.Same(abortive, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicDoesNotTreatAnOtherCandidateAsNineTerminalsWithoutExplicitMetadata()
        {
            LegalActionOption other = Other("other-not-abortive");
            LegalActionOption discard = Discard(T(4), "discard-5m");

            var actions = new[] { other, discard };
            BasicAiContext context = Context(new[] { new BasicCandidateFacts(other, abortiveDraw: null) });

            Assert.Same(discard, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicDeclaresRiichiAtExactlyOneThousandPoints()
        {
            IReadOnlyList<TileId> hand = RiichiWaitHand();
            LegalActionOption riichi = Riichi(T(22), "riichi-5p");
            LegalActionOption discard = Discard(T(22), "discard-5p");

            var actions = new[] { riichi, discard };
            var observation = Observation(
                actions,
                Context(),
                hand,
                scores: Scores(1000L));

            AiStrategicChoice choice = Basic.Decide(observation, CancellationToken.None);

            Assert.Same(riichi, choice.Action);
        }

        [Fact]
        public void BasicSkipsRiichiBelowOneThousandAndFallsToDiscard()
        {
            IReadOnlyList<TileId> hand = RiichiWaitHand();
            LegalActionOption riichi = Riichi(T(22), "riichi-5p");
            LegalActionOption discard = Discard(T(22), "discard-5p");

            var actions = new[] { riichi, discard };
            var observation = Observation(
                actions,
                Context(),
                hand,
                scores: Scores(999L));

            AiStrategicChoice choice = Basic.Decide(observation, CancellationToken.None);

            Assert.Same(discard, choice.Action);
            Assert.Equal(LegalActionKind.Discard, choice.Action.Kind);
        }

        [Fact]
        public void BasicRiichiThresholdReadsTheViewerSeatScore()
        {
            IReadOnlyList<TileId> hand = RiichiWaitHand();
            LegalActionOption riichi = Riichi(T(22), "riichi-5p");
            LegalActionOption discard = Discard(T(22), "discard-5p");
            var actions = new[] { riichi, discard };

            // Seat 1 has 1000; a bug reading scores[0] would see 0, skip riichi and take the
            // discard instead.
            var observation = Observation(
                actions,
                Context(),
                hand,
                scores: new long[] { 0L, 1000L, 0L, 0L });

            Assert.Same(riichi, Basic.Decide(observation, CancellationToken.None).Action);
        }

        [Fact]
        public void BasicRiichiChoosesTheDiscardWithTheMostWaitTypesNotTheFirstRiichi()
        {
            // Discarding 1m leaves 23456789m + 123p + 55p, which waits on 1m/4m/7m (three
            // types). Discarding 5p leaves the four completed melds plus a lone 5p, one type.
            IReadOnlyList<TileId> hand = RiichiWaitHand();
            LegalActionOption riichi5p = Riichi(T(22), "riichi-5p");
            LegalActionOption riichi1m = Riichi(T(0), "riichi-1m");

            var actions = new[] { riichi5p, riichi1m };
            AiStrategicChoice choice = Choose(actions, Context(), hand);

            Assert.Same(riichi1m, choice.Action);
            Assert.Equal(3d, choice.HeuristicScore);
        }

        [Fact]
        public void BasicRiichiTieKeepsTheFirstCandidateInListOrder()
        {
            // Two identical 123m sequences, two identical 123p sequences and a 5s pair.
            // Discarding 1m waits on 1m/4m (two types); discarding 1p waits on 1p/4p (two
            // types). The tie must keep the first riichi candidate in list order.
            IReadOnlyList<TileId> hand = Tiles(
                T(0, 0), T(1, 0), T(2, 0),
                T(0, 1), T(1, 1), T(2, 1),
                T(18, 0), T(19, 0), T(20, 0),
                T(18, 1), T(19, 1), T(20, 1),
                T(13, 0), T(13, 1));
            LegalActionOption riichi1p = Riichi(T(18, 0), "riichi-1p");
            LegalActionOption riichi1m = Riichi(T(0, 0), "riichi-1m");

            var actions = new[] { riichi1p, riichi1m };
            AiStrategicChoice choice = Choose(actions, Context(), hand);

            Assert.Same(riichi1p, choice.Action);
            Assert.Equal(2d, choice.HeuristicScore);

            var reversed = new[] { riichi1m, riichi1p };
            Assert.Same(riichi1m, Choose(reversed, Context(), hand).Action);
        }

        [Fact]
        public void BasicRiichiRemovesOnlyOnePhysicalCopyOfTheDiscard()
        {
            // 123m 456m 789m 3p 4p 5p(0) 5p(1) 6p. Discarding one 5p leaves 3p4p5p6p, which
            // waits on 3p and 6p (two types). Removing every same-type copy would leave
            // 3p4p6p (twelve tiles) and zero waits, so the first 3p riichi would wrongly win.
            IReadOnlyList<TileId> hand = Tiles(
                T(0), T(1), T(2),
                T(3), T(4), T(5),
                T(6), T(7), T(8),
                T(20), T(21), T(22, 0), T(22, 1), T(23));
            LegalActionOption riichi3p = Riichi(T(20), "riichi-3p");
            LegalActionOption riichi5p = Riichi(T(22, 0), "riichi-5p");

            var actions = new[] { riichi3p, riichi5p };
            AiStrategicChoice choice = Choose(actions, Context(), hand);

            Assert.Same(riichi5p, choice.Action);
            Assert.Equal(2d, choice.HeuristicScore);
        }

        [Fact]
        public void BasicRiichiWaitTypeCountDoesNotReadRemainingWallCount()
        {
            IReadOnlyList<TileId> hand = RiichiWaitHand();
            LegalActionOption riichi5p = Riichi(T(22), "riichi-5p");
            LegalActionOption riichi1m = Riichi(T(0), "riichi-1m");
            var actions = new[] { riichi5p, riichi1m };

            var emptyWall = Observation(actions, Context(), hand, remainingWallCount: 0);
            var fullWall = Observation(actions, Context(), hand, remainingWallCount: 136);

            Assert.Same(riichi1m, Basic.Decide(emptyWall, CancellationToken.None).Action);
            Assert.Same(riichi1m, Basic.Decide(fullWall, CancellationToken.None).Action);
        }

        [Fact]
        public void BasicRiichiWaitCountTreatsSameTypeCopiesAsOneTypeAndKeepsTheSelectedInstance()
        {
            // Both riichi candidates discard a physical pin five; the physical copies have the
            // same tile type, so both leave two wait types. The first candidate must win and
            // the exact instance (including its OptionId) must be returned.
            IReadOnlyList<TileId> hand = Tiles(
                T(0), T(1), T(2),
                T(3), T(4), T(5),
                T(6), T(7), T(8),
                T(20), T(21), T(22, 0), T(22, 3), T(23));
            LegalActionOption red = Riichi(T(22, 3), "riichi-pin5-red");
            LegalActionOption normal = Riichi(T(22, 0), "riichi-pin5-normal");

            var actions = new[] { red, normal };
            AiStrategicChoice choice = Choose(actions, Context(), hand);

            Assert.Same(red, choice.Action);
            Assert.Equal("riichi-pin5-red", choice.Action.OptionId);
        }

        [Fact]
        public void BasicClosedKanWinsBeforePonAndResponsePass()
        {
            LegalActionOption closed = Kan("closed-kan");
            LegalActionOption pon = Pon("pon");
            LegalActionOption pass = Pass();

            var actions = new[] { pon, closed, pass };
            BasicAiContext context = Context(
                new[] { new BasicCandidateFacts(closed, BasicKanKind.Closed) },
                lastDiscard: T(31));

            Assert.Same(closed, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicSelectsTheFirstClosedKanInListOrder()
        {
            LegalActionOption firstClosed = Kan("closed-kan-1");
            LegalActionOption secondClosed = Kan("closed-kan-2");
            var actions = new[] { firstClosed, secondClosed };
            BasicAiContext context = Context(new[]
            {
                new BasicCandidateFacts(firstClosed, BasicKanKind.Closed),
                new BasicCandidateFacts(secondClosed, BasicKanKind.Closed)
            });

            Assert.Same(firstClosed, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicNeverTreatsOpenOrAddedKanAsClosed()
        {
            LegalActionOption open = Kan("open-kan");
            LegalActionOption added = Kan("added-kan");
            LegalActionOption pass = Pass();

            var actions = new[] { open, added, pass };
            BasicAiContext context = Context(new[]
            {
                new BasicCandidateFacts(open, BasicKanKind.Open),
                new BasicCandidateFacts(added, BasicKanKind.Added)
            });

            Assert.Same(pass, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicOpenKanFallsThroughToDiscardWhenNoPassExists()
        {
            LegalActionOption open = Kan("open-kan");
            LegalActionOption discard = Discard(T(4), "discard-5m");

            var actions = new[] { open, discard };
            BasicAiContext context = Context(
                new[] { new BasicCandidateFacts(open, BasicKanKind.Open) });

            Assert.Same(discard, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicPonsOnlyTheFirstCandidateWhenTheLastDiscardIsAYakuhaiDragon()
        {
            LegalActionOption firstPon = Pon("pon-1");
            LegalActionOption secondPon = Pon("pon-2");
            LegalActionOption pass = Pass();

            var actions = new[] { firstPon, secondPon, pass };
            BasicAiContext context = Context(lastDiscard: T(32)); // Hatsu

            Assert.Same(firstPon, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicPonsWhenTheLastDiscardIsTheViewersSeatWind()
        {
            // Seat 1 with dealer 0 has seat wind 2 (South); round wind 3 (West) is different.
            LegalActionOption pon = Pon();
            LegalActionOption pass = Pass();

            var actions = new[] { pon, pass };
            BasicAiContext context = Context(lastDiscard: T(28)); // South

            Assert.Same(pon, Choose(actions, context, roundWind: 3).Action);
        }

        [Fact]
        public void BasicPonsWhenTheLastDiscardIsTheRoundWind()
        {
            LegalActionOption pon = Pon();
            LegalActionOption pass = Pass();

            var actions = new[] { pon, pass };
            BasicAiContext context = Context(lastDiscard: T(27)); // East

            Assert.Same(pon, Choose(actions, context, roundWind: 1).Action);
        }

        [Fact]
        public void BasicDeclinesPonForNonYakuhaiWindAndPasses()
        {
            LegalActionOption pon = Pon();
            LegalActionOption pass = Pass();

            var actions = new[] { pon, pass };
            // Seat wind is South (2), round wind East (1); West (3) is not yakuhai.
            BasicAiContext context = Context(lastDiscard: T(29));

            Assert.Same(pass, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicDeclinesPonForANumberDiscardAndPasses()
        {
            LegalActionOption pon = Pon();
            LegalActionOption pass = Pass();

            var actions = new[] { pon, pass };
            BasicAiContext context = Context(lastDiscard: T(4));

            Assert.Same(pass, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicLastDiscardNullMeansNoPublicDiscardAndNoPon()
        {
            LegalActionOption pon = Pon();
            LegalActionOption pass = Pass();

            var actions = new[] { pon, pass };
            BasicAiContext context = Context(lastDiscard: null);

            Assert.Same(pass, Choose(actions, context).Action);
        }

        [Fact]
        public void BasicResponsePassPrecedesDiscardWhenAChiChoiceExists()
        {
            LegalActionOption chi = Chi("chi");
            LegalActionOption discard = Discard(T(4), "discard-5m");
            LegalActionOption pass = Pass();

            var actions = new[] { chi, discard, pass };

            Assert.Same(pass, Choose(actions, Context()).Action);
        }

        [Fact]
        public void BasicPassBesideOnlyDiscardDoesNotPreemptTheDiscard()
        {
            LegalActionOption pass = Pass();
            LegalActionOption discard = Discard(T(4), "discard-5m");

            var actions = new[] { pass, discard };

            Assert.Same(discard, Choose(actions, Context()).Action);
        }

        [Fact]
        public void BasicNormalisesAClosedOnlyHandPlusDrawnTileForDisposability()
        {
            // V4 PlayerView kept myClosedTiles and myDrawnTile separate, while the V5 runtime
            // stores the drawn tile in the hand zone. Both representations must produce V4's
            // myAllClosedTiles exactly once.
            LegalActionOption threeMan = Discard(T(2), "discard-3m");
            LegalActionOption sixMan = Discard(T(5), "discard-6m");
            var actions = new[] { threeMan, sixMan };

            IReadOnlyList<TileId> closedOnly = Tiles(T(2), T(5));
            IReadOnlyList<TileId> alreadyIncludesDrawn = Tiles(T(2), T(5), T(1));

            // Drawn 2m makes 3m less disposable (-10) but does not neighbour 6m, so 6m wins.
            // If the drawn tile were not appended, both would tie at zero and 3m would win.
            Assert.Same(
                sixMan,
                Choose(actions, Context(), closedOnly, T(1)).Action);
            Assert.Same(
                sixMan,
                Choose(actions, Context(), alreadyIncludesDrawn, T(1)).Action);
        }

        [Fact]
        public void BasicRiichiTsumogiriChoosesTheExactPhysicalDrawnCopy()
        {
            LegalActionOption normal = Discard(PinFiveNormal, "discard-pin5-normal");
            LegalActionOption red = Discard(PinFiveRed, "discard-pin5-red");
            var actions = new[] { normal, red };

            IReadOnlyList<TileId> hand = Tiles(T(8), PinFiveNormal, PinFiveRed, T(0));
            BasicAiContext context = Context(isRiichi: true);

            AiStrategicChoice choice = Choose(actions, context, hand, PinFiveRed);

            Assert.Same(red, choice.Action);
            Assert.Equal("discard-pin5-red", choice.Action.OptionId);
        }

        [Fact]
        public void BasicTsumogiriOnlyAppliesWhenTheViewerIsInRiichi()
        {
            LegalActionOption normal = Discard(PinFiveNormal, "discard-pin5-normal");
            LegalActionOption red = Discard(PinFiveRed, "discard-pin5-red");
            var actions = new[] { normal, red };

            IReadOnlyList<TileId> hand = Tiles(T(8), PinFiveNormal, PinFiveRed, T(0));

            // Not in riichi: both copies have identical disposability, so the first candidate
            // wins instead of the drawn red copy.
            Assert.Same(
                normal,
                Choose(actions, Context(isRiichi: false), hand, PinFiveRed).Action);

            // In riichi: the exact drawn red copy wins.
            Assert.Same(
                red,
                Choose(actions, Context(isRiichi: true), hand, PinFiveRed).Action);
        }

        [Fact]
        public void BasicDiscardsAnIsolatedNonYakuhaiHonorBeforeAYakuhaiHonor()
        {
            LegalActionOption east = Discard(T(27), "discard-east");
            LegalActionOption north = Discard(T(30), "discard-north");
            var actions = new[] { east, north };
            IReadOnlyList<TileId> hand = Tiles(T(27), T(30));

            // East is the round wind (5); North is not yakuhai (15).
            Assert.Same(north, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicTerminalOutranksAYakuhaiHonorForDisposal()
        {
            LegalActionOption oneMan = Discard(T(0), "discard-1m");
            LegalActionOption east = Discard(T(27), "discard-east");
            var actions = new[] { east, oneMan };
            IReadOnlyList<TileId> hand = Tiles(T(27), T(0));

            // Isolated terminal 1m = 10 + edge 3 = 13; yakuhai East = 5.
            Assert.Same(oneMan, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicNonYakuhaiHonorOutranksAnIsolatedTerminal()
        {
            LegalActionOption north = Discard(T(30), "discard-north");
            LegalActionOption nineSou = Discard(T(17), "discard-9s");
            var actions = new[] { nineSou, north };
            IReadOnlyList<TileId> hand = Tiles(T(17), T(30));

            // Non-yakuhai honor = 15; terminal 9s = 10 + 3 = 13.
            Assert.Same(north, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicKeepsAPairAndDiscardsAnIsolatedTile()
        {
            LegalActionOption pinFive = Discard(PinFiveNormal, "discard-pin5");
            LegalActionOption nineSou = Discard(T(17), "discard-9s");
            var actions = new[] { pinFive, nineSou };
            IReadOnlyList<TileId> hand = Tiles(PinFiveNormal, T(22, 1), T(17));

            Assert.Same(nineSou, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicPairPenaltyKeepsThePairInsteadOfATiedSingleton()
        {
            // East pair = -20; 3m singleton = 0. If the pair penalty were missing both would
            // be zero and the first candidate (the pair) would be discarded.
            LegalActionOption eastPair = Discard(T(27), "discard-east");
            LegalActionOption threeMan = Discard(T(2), "discard-3m");
            var actions = new[] { eastPair, threeMan };
            IReadOnlyList<TileId> hand = Tiles(T(27, 0), T(27, 1), T(2));

            Assert.Same(threeMan, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicKeepsATripletAndDiscardsAPair()
        {
            LegalActionOption eastPair = Discard(T(27), "discard-east");
            LegalActionOption hakuTriplet = Discard(T(31), "discard-haku");
            var actions = new[] { hakuTriplet, eastPair };
            IReadOnlyList<TileId> hand = Tiles(
                T(27, 0), T(27, 1),
                T(31, 0), T(31, 1), T(31, 2));

            // Pair = -20; triplet = -20 - 40 = -60. The higher score is discarded.
            Assert.Same(eastPair, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicCountsAnotherPhysicalCopyOfTheSameTypeAsANeighbour()
        {
            // 5m pair: -20 for the pair plus -10 for the other physical 5m copy at distance
            // zero = -30. East pair has no number neighbour = -20. If the same-type copy were
            // not a neighbour both would be -20 and the first candidate (5m) would win.
            LegalActionOption fiveMan = Discard(T(4, 0), "discard-5m");
            LegalActionOption east = Discard(T(27), "discard-east");
            var actions = new[] { fiveMan, east };
            IReadOnlyList<TileId> hand = Tiles(
                T(4, 0), T(4, 1),
                T(27, 0), T(27, 1));

            Assert.Same(east, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicNeighbourPenaltyAppliesWithinTwoRanks()
        {
            LegalActionOption threeMan = Discard(T(2), "discard-3m");
            LegalActionOption sixMan = Discard(T(5), "discard-6m");
            var actions = new[] { sixMan, threeMan };
            IReadOnlyList<TileId> hand = Tiles(T(2), T(5), T(7));

            // 6m has 8m at distance two (-10); 3m has no neighbour within two ranks (0).
            Assert.Same(threeMan, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicEdgeTileBonusAppliesToRankTwoAndEight()
        {
            LegalActionOption twoMan = Discard(T(1), "discard-2m");
            LegalActionOption fiveMan = Discard(T(4), "discard-5m");
            var actions = new[] { fiveMan, twoMan };
            IReadOnlyList<TileId> hand = Tiles(T(1), T(4));

            Assert.Same(twoMan, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicFirstTieKeepsListOrder()
        {
            LegalActionOption threeMan = Discard(T(2), "discard-3m");
            LegalActionOption sixMan = Discard(T(5), "discard-6m");
            var actions = new[] { sixMan, threeMan };
            IReadOnlyList<TileId> hand = Tiles(T(2), T(5));

            // These are three ranks apart, so both score zero; the first candidate wins.
            Assert.Same(sixMan, Choose(actions, Context(), hand).Action);
        }

        [Fact]
        public void BasicSeatWindYakuhaiUsesTheDealerRelativeSeat()
        {
            LegalActionOption south = Discard(T(28), "discard-south");
            LegalActionOption east = Discard(T(27), "discard-east");
            var actions = new[] { south, east };
            IReadOnlyList<TileId> hand = Tiles(T(28), T(27));

            // Seat 1, dealer 0 => seat wind South (5). Round wind West (3) is not East.
            // East is therefore a non-yakuhai honor (15). If the seat wind were computed
            // wrongly, South would also score 15 and the first candidate would win.
            Assert.Same(east, Choose(actions, Context(), hand, roundWind: 3).Action);
        }

        [Fact]
        public void BasicRoundWindYakuhaiIsRecognised()
        {
            LegalActionOption east = Discard(T(27), "discard-east");
            LegalActionOption north = Discard(T(30), "discard-north");
            var actions = new[] { east, north };
            IReadOnlyList<TileId> hand = Tiles(T(27), T(30));

            // East is the round wind (5); North is not (15). If East were not recognised
            // as yakuhai both would tie at 15 and the first candidate (East) would win.
            Assert.Same(north, Choose(actions, Context(), hand, roundWind: 1).Action);
        }

        [Fact]
        public void BasicDragonIsAlwaysYakuhaiRegardlessOfWinds()
        {
            LegalActionOption haku = Discard(T(31), "discard-haku");
            LegalActionOption north = Discard(T(30), "discard-north");
            var actions = new[] { haku, north };
            IReadOnlyList<TileId> hand = Tiles(T(31), T(30));

            Assert.Same(north, Choose(actions, Context(), hand, roundWind: 3).Action);
        }

        [Fact]
        public void BasicRiichiPrecedesClosedKanAndPon()
        {
            LegalActionOption closed = Kan("closed-kan");
            LegalActionOption pon = Pon();
            LegalActionOption riichi = Riichi(T(0), "riichi-1m");
            var actions = new[] { closed, pon, riichi };
            BasicAiContext context = Context(
                new[] { new BasicCandidateFacts(closed, BasicKanKind.Closed) },
                lastDiscard: T(31));

            Assert.Same(riichi, Choose(actions, context, RiichiWaitHand()).Action);
        }

        [Fact]
        public void BasicFallbackReturnsPassWhenNothingHigherApplies()
        {
            LegalActionOption pass = Pass();
            Assert.Same(pass, Choose(new[] { pass }, Context()).Action);
        }

        [Fact]
        public void BasicFallbackReturnsFirstLegalCandidateWhenNoPassExists()
        {
            LegalActionOption open = Kan("open-kan");
            LegalActionOption riichi = Riichi(T(0), "riichi-1m");
            var actions = new[] { open, riichi };
            BasicAiContext context = Context(
                new[] { new BasicCandidateFacts(open, BasicKanKind.Open) });

            // Score 999 skips the riichi branch; the open kan is not a closed kan; there is
            // no pon, pass or discard. V4's final fallback is the first legal candidate.
            Assert.Same(
                open,
                Choose(actions, context, RiichiWaitHand(), scores: Scores(999L)).Action);
        }

        [Fact]
        public void BasicFallbackCanReturnRiichiWhenTheScoreGateSkippedIt()
        {
            LegalActionOption riichi = Riichi(T(0), "riichi-1m");
            var actions = new[] { riichi };

            Assert.Same(
                riichi,
                Choose(actions, Context(), RiichiWaitHand(), scores: Scores(999L)).Action);
        }

        // ---------------------------------------------------------------------
        // Observation / typed metadata binding
        // ---------------------------------------------------------------------

        [Fact]
        public void ObservationRejectsBasicContextForAnotherSeat()
        {
            var context = new BasicAiContext(2, false, null, Array.Empty<BasicCandidateFacts>());
            var actions = new[] { Discard(T(4), "discard-5m") };

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Observation(actions, context));

            Assert.Contains("viewing seat", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ObservationRejectsFactsForACandidateThatIsNotInTheList()
        {
            LegalActionOption ghost = Other("ghost");
            var context = Context(new[] { new BasicCandidateFacts(ghost, abortiveDraw: null) });
            var actions = new[] { Discard(T(4), "discard-5m") };

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Observation(actions, context));

            Assert.Contains("not part of this candidate list", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ObservationRejectsAKanCandidateWithoutExplicitPhysicalKind()
        {
            LegalActionOption open = Kan("open-kan");
            var actions = new[] { open };

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Observation(actions, Context()));

            Assert.Contains("kan kind", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ObservationRejectsAnOtherCandidateWithoutAbortiveClassification()
        {
            LegalActionOption other = Other("other");
            var actions = new[] { other };

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Observation(actions, Context()));

            Assert.Contains("abortive-draw classification", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BasicContextRejectsFactsForAnotherSeat()
        {
            LegalActionOption other = new LegalActionOption(
                LegalActionKind.Other,
                2,
                optionId: "other-seat2");
            var fact = new BasicCandidateFacts(other, abortiveDraw: null);

            Assert.Throws<ArgumentException>(
                () => new BasicAiContext(Seat, false, null, new[] { fact }));
        }

        [Fact]
        public void BasicContextRejectsDuplicateFactsForOneCandidate()
        {
            LegalActionOption other = Other("other");
            var fact = new BasicCandidateFacts(other, abortiveDraw: null);

            Assert.Throws<ArgumentException>(
                () => new BasicAiContext(Seat, false, null, new[] { fact, fact }));
        }

        [Fact]
        public void BasicCandidateFactsRejectsANonKanNonOtherCandidate()
        {
            Assert.Throws<ArgumentException>(
                () => new BasicCandidateFacts(Discard(T(4), "discard-5m")));
        }

        [Fact]
        public void BasicCandidateFactsRejectsAKanWithoutAPhysicalKind()
        {
            Assert.Throws<ArgumentException>(
                () => new BasicCandidateFacts(Kan("closed-kan")));
        }

        [Fact]
        public void BasicCandidateFactsRejectsAbortiveMetadataOnAKan()
        {
            Assert.Throws<ArgumentException>(
                () => new BasicCandidateFacts(
                    Kan("closed-kan"),
                    BasicKanKind.Closed,
                    BasicAbortiveDrawKind.NineTerminals));
        }

        [Fact]
        public void BasicCandidateFactsRejectsUnknownKanKindValues()
        {
            foreach (int raw in new[] { -1, 3, 999 })
            {
                BasicKanKind invalid = (BasicKanKind)raw;
                ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new BasicCandidateFacts(Kan("kan-" + raw), invalid));
                Assert.Equal("KanKind", exception.ParamName);
            }
        }

        [Fact]
        public void BasicCandidateFactsRejectsUnknownAbortiveDrawValues()
        {
            foreach (int raw in new[] { -1, 1, 999 })
            {
                BasicAbortiveDrawKind invalid = (BasicAbortiveDrawKind)raw;
                ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new BasicCandidateFacts(
                        Other("other-" + raw),
                        abortiveDraw: invalid));
                Assert.Equal("AbortiveDraw", exception.ParamName);
            }
        }

        [Fact]
        public void BasicCandidateFactsAcceptsOnlyDefinedKanAndAbortiveValues()
        {
            foreach (BasicKanKind kind in new[]
            {
                BasicKanKind.Closed,
                BasicKanKind.Open,
                BasicKanKind.Added
            })
            {
                var facts = new BasicCandidateFacts(Kan("kan-" + kind), kind);
                Assert.Equal(kind, facts.KanKind);
                Assert.Null(facts.AbortiveDraw);
                Assert.False(facts.IsAbortiveDraw);
            }

            var abortive = new BasicCandidateFacts(
                Other("other-abortive"),
                abortiveDraw: BasicAbortiveDrawKind.NineTerminals);
            Assert.Equal(BasicAbortiveDrawKind.NineTerminals, abortive.AbortiveDraw);
            Assert.True(abortive.IsAbortiveDraw);

            var notAbortive = new BasicCandidateFacts(
                Other("other-not-abortive"),
                abortiveDraw: null);
            Assert.Null(notAbortive.AbortiveDraw);
            Assert.False(notAbortive.IsAbortiveDraw);
        }

        [Fact]
        public void ObservationDefensivelyCopiesTheBasicContextFactList()
        {
            LegalActionOption closed = Kan("closed-kan");
            var facts = new List<BasicCandidateFacts>
            {
                new BasicCandidateFacts(closed, BasicKanKind.Closed)
            };
            var context = new BasicAiContext(Seat, false, null, facts);
            var actions = new List<LegalActionOption> { closed, Pass() };
            AiObservation observation = Observation(actions, context);

            facts.Clear();
            actions.Clear();

            Assert.Single(context.CandidateFacts);
            Assert.Same(closed, Basic.Decide(observation, CancellationToken.None).Action);
        }

        [Fact]
        public void ObservationWithoutBasicContextKeepsTheExistingBaselineStrategiesWorking()
        {
            LegalActionOption pass = Pass();
            LegalActionOption ron = Ron("ron");
            var actions = new[] { pass, ron };
            AiObservation observation = Observation(actions);

            Assert.Null(observation.BasicContext);
            Assert.Same(ron, Trivial.Decide(observation, CancellationToken.None).Action);
            Assert.Same(pass, Tsumogiri.Decide(observation, CancellationToken.None).Action);
        }

        [Fact]
        public void BasicDecideWithoutContextFailsWithADiagnosableMessage()
        {
            AiObservation observation = Observation(new[] { Discard(T(4), "discard-5m") });

            BasicAiContextException exception = Assert.Throws<BasicAiContextException>(
                () => Basic.Decide(observation, CancellationToken.None));

            Assert.Contains("BasicAiContext", exception.Message, StringComparison.Ordinal);
            Assert.Contains("riichi", exception.Message, StringComparison.Ordinal);
            Assert.Contains("last discard", exception.Message, StringComparison.Ordinal);
            Assert.Contains("kan", exception.Message, StringComparison.Ordinal);
            Assert.Contains("abortive", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BasicTryChooseRejectsForeignSeatRon()
        {
            var context = new BasicAiContext(0, false, null, Array.Empty<BasicCandidateFacts>());
            LegalActionOption foreignRon = new LegalActionOption(
                LegalActionKind.Ron,
                2,
                optionId: "foreign-ron");

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => TryRaw(new[] { foreignRon }, context));

            Assert.Contains("context seat", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BasicTryChooseRejectsForeignSeatDiscard()
        {
            var context = new BasicAiContext(0, false, null, Array.Empty<BasicCandidateFacts>());
            LegalActionOption foreignDiscard = new LegalActionOption(
                LegalActionKind.Discard,
                2,
                T(4),
                "foreign-discard");

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => TryRaw(new[] { foreignDiscard }, context));

            Assert.Contains("context seat", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BasicTryChooseRejectsOrphanFacts()
        {
            LegalActionOption ghost = Other("ghost");
            BasicAiContext context = Context(new[]
            {
                new BasicCandidateFacts(ghost, abortiveDraw: null)
            });

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => TryRaw(new[] { Pass() }, context));

            Assert.Contains("not part of this candidate list", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BasicTryChooseRejectsFactWithSameOptionIdButDifferentTile()
        {
            LegalActionOption bound = Other("shared");
            BasicAiContext context = Context(new[]
            {
                new BasicCandidateFacts(bound, abortiveDraw: null)
            });
            var differentTile = new LegalActionOption(
                LegalActionKind.Other,
                Seat,
                T(5),
                "shared");

            Assert.Throws<ArgumentException>(
                () => TryRaw(new[] { differentTile }, context));
        }

        [Fact]
        public void BasicTryChooseRejectsFactWithSameOptionIdButDifferentKind()
        {
            LegalActionOption bound = Other("shared");
            BasicAiContext context = Context(new[]
            {
                new BasicCandidateFacts(bound, abortiveDraw: null)
            });
            var differentKind = Kan("shared");

            Assert.Throws<ArgumentException>(
                () => TryRaw(new[] { differentKind }, context));
        }

        [Fact]
        public void BasicTryChooseRejectsFactWithSameOptionIdButDifferentSeat()
        {
            LegalActionOption bound = Other("shared");
            BasicAiContext context = Context(new[]
            {
                new BasicCandidateFacts(bound, abortiveDraw: null)
            });
            var differentSeat = new LegalActionOption(
                LegalActionKind.Other,
                2,
                optionId: "shared");

            Assert.Throws<ArgumentException>(
                () => TryRaw(new[] { differentSeat }, context));
        }

        [Fact]
        public void BasicTryChooseRejectsNullAndDuplicateCandidates()
        {
            var nulls = new LegalActionOption[] { null! };
            Assert.Throws<ArgumentException>(
                () => TryRaw(nulls, Context()));

            var duplicates = new[] { Pass("duplicate"), Pass("duplicate") };
            Assert.Throws<ArgumentException>(
                () => TryRaw(duplicates, Context()));
        }

        [Fact]
        public void BasicTryChoosePreservesTheCorrectEmptyListAndCancellationSemantics()
        {
            Assert.Null(TryRaw(Array.Empty<LegalActionOption>(), Context()));

            using var source = new CancellationTokenSource();
            source.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(
                () => Basic.TryChoose(
                    Array.Empty<LegalActionOption>(),
                    Context(),
                    DefaultHand(),
                    null,
                    0,
                    1,
                    ScoresAll(),
                    source.Token));
        }

        [Fact]
        public void BasicTryChooseReturnsTheOriginalCandidateInstance()
        {
            LegalActionOption pass = Pass("pass");
            LegalActionOption ron = Ron("ron");
            var actions = new[] { pass, ron };

            AiStrategicChoice? choice = TryRaw(actions, Context());

            Assert.NotNull(choice);
            Assert.Same(ron, choice!.Action);
            Assert.Equal("ron", choice.Action.OptionId);
        }

        [Fact]
        public void BasicContextSurfaceExposesNoHiddenDomainState()
        {
            string[] forbiddenNames =
            {
                "GameState", "RoundState", "FullState", "Wall", "DeadWall", "Seed",
                "Opponent", "Hidden", "UraDora", "TilePositions", "TilePositionMap"
            };

            foreach (PropertyInfo property in typeof(BasicAiContext).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (string forbidden in forbiddenNames)
                {
                    Assert.False(
                        property.Name.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0,
                        "BasicAiContext exposes forbidden hidden-state property " + property.Name + ".");
                }
            }

            foreach (PropertyInfo property in typeof(BasicCandidateFacts).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (string forbidden in forbiddenNames)
                {
                    Assert.False(
                        property.Name.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0,
                        "BasicCandidateFacts exposes forbidden hidden-state property " + property.Name + ".");
                }
            }

            Assert.Equal("Mahjong.Ai", typeof(BasicAiContext).Namespace);
            Assert.Equal("Mahjong.Ai", typeof(BasicCandidateFacts).Namespace);
        }

        [Fact]
        public void BasicToStringIsDecisionIndependentAndCarriesNoTileData()
        {
            string before = Basic.ToString();
            Choose(new[] { Discard(T(4), "discard-5m") }, Context());
            string after = Basic.ToString();

            Assert.Equal(before, after);
            Assert.Equal("BasicAiStrategy/algorithm=baseline-basic/version=1", before);
            Assert.DoesNotContain("T88", before, StringComparison.Ordinal);
            Assert.DoesNotContain("T91", before, StringComparison.Ordinal);
        }

        [Fact]
        public void BasicAlgorithmIdentityIsStableAndDistinctFromTheOtherBaselines()
        {
            Assert.Equal("baseline-basic", Basic.AlgorithmId);
            Assert.Equal(1, Basic.AlgorithmVersion);
            Assert.NotEqual(Trivial.AlgorithmId, Basic.AlgorithmId);
            Assert.NotEqual(Tsumogiri.AlgorithmId, Basic.AlgorithmId);
        }

        // ---------------------------------------------------------------------
        // Cancellation
        // ---------------------------------------------------------------------

        [Fact]
        public void BasicTryChooseHonoursCancellationBeforeTheEmptyListShortCircuit()
        {
            using var source = new CancellationTokenSource();
            source.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => Basic.TryChoose(
                    Array.Empty<LegalActionOption>(),
                    Context(),
                    DefaultHand(),
                    null,
                    0,
                    1,
                    ScoresAll(),
                    source.Token));
        }

        [Fact]
        public void BasicDecideHonoursCancellationBeforeContextValidation()
        {
            AiObservation observation = Observation(new[] { Discard(T(4), "discard-5m") });
            using var source = new CancellationTokenSource();
            source.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => Basic.Decide(observation, source.Token));
        }

        [Fact]
        public async Task ReasonAsyncHonoursCancellationBeforeBasicContextValidation()
        {
            AiObservation observation = Observation(new[] { Discard(T(4), "discard-5m") });
            using var source = new CancellationTokenSource();
            source.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new AiDecisionService(Basic).ReasonAsync(observation, source.Token));
        }

        // ---------------------------------------------------------------------
        // Real service wiring: ReasonAsync -> CommandIntent -> Submit / Replay
        // ---------------------------------------------------------------------

        [Fact]
        public async Task ReasonAsyncBasicIntentIsAcceptedThenLateStaleEpochAndRoundAreRejected()
        {
            LegalActionOption normal = Discard(PinFiveNormal, "discard-pin5-normal");
            LegalActionOption red = Discard(PinFiveRed, "discard-pin5-red");
            var actions = new[] { normal, red };
            IReadOnlyList<TileId> hand = Tiles(T(8), PinFiveNormal, PinFiveRed, T(0));
            BasicAiContext context = Context(isRiichi: true);
            AiObservation observation = Observation(actions, context, hand, PinFiveRed);
            var service = new AiDecisionService(Basic);

            AiDecision decision = await service.ReasonAsync(
                observation,
                TestContext.Current.CancellationToken);

            Assert.Same(observation, decision.Observation);
            Assert.Same(red, decision.Intent.Action);
            Assert.Equal(BasicAiStrategy.AlgorithmIdValue, decision.Intent.AlgorithmId);
            Assert.Equal(BasicAiStrategy.AlgorithmVersionValue, decision.Intent.AlgorithmVersion);
            Assert.Equal(0d, decision.HeuristicScore);

            AiSubmissionResult accepted = service.Submit(decision, Authority(observation), 1000L);
            Assert.True(accepted.IsAccepted, accepted.Detail);
            Assert.NotNull(accepted.Submission);

            // Domain arbiter consumes the accepted intent unchanged.
            var clock = new DecisionClock();
            clock.Open(decision.Intent.DecisionKey, 0L);
            var control = new ControlRegistry();
            var pending = new PendingDecision(
                decision.Intent.DecisionKey,
                decision.Intent.ControlEpoch,
                observation.LegalActions,
                observation.MyDrawnTileId);
            TickResult tick = new DeadlineArbiter(clock, control).ProcessTick(
                new TickInput(
                    observation.RoundId,
                    1000L,
                    new[] { pending },
                    new[] { accepted.Submission! }));
            Assert.Empty(tick.Timeouts);
            Assert.Empty(tick.RejectedAi);
            Assert.Single(tick.AcceptedAi);
            Assert.Same(red, tick.AcceptedAi[0].Action);

            AiSubmissionResult staleEpoch = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    observation.Seat,
                    observation.RoundId,
                    observation.DecisionKey,
                    observation.ControlEpoch + 1L,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleEpoch, staleEpoch.Code);
            Assert.Null(staleEpoch.Submission);

            AiSubmissionResult staleRound = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    observation.Seat,
                    "round-other",
                    new DecisionKey("round-other", observation.Seat, observation.DecisionId),
                    observation.ControlEpoch,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleRound, staleRound.Code);
            Assert.Null(staleRound.Submission);

            AiSubmissionResult late = service.Submit(decision, Authority(observation), Deadline);
            Assert.Equal(TickRejectionCode.DeadlineExpired, late.Code);
            Assert.Null(late.Submission);

            AiSubmissionResult fresh = service.Submit(
                decision,
                Authority(observation),
                Deadline - 1L);
            Assert.True(fresh.IsAccepted, fresh.Detail);
        }

        [Fact]
        public async Task ReplayBasicIntentDoesNotRerunTheStrategyAndRejectsTampering()
        {
            LegalActionOption normal = Discard(PinFiveNormal, "discard-pin5-normal");
            LegalActionOption red = Discard(PinFiveRed, "discard-pin5-red");
            var actions = new[] { normal, red };
            IReadOnlyList<TileId> hand = Tiles(T(8), PinFiveNormal, PinFiveRed, T(0));
            BasicAiContext context = Context(isRiichi: true);
            AiObservation observation = Observation(actions, context, hand, PinFiveRed);
            var service = new AiDecisionService(Basic);

            AiDecision decision = await service.ReasonAsync(
                observation,
                TestContext.Current.CancellationToken);

            AiObservation replayObservation = Observation(actions, context, hand, PinFiveRed);
            AiDecision replay = AiDecisionService.Replay(decision.Intent, replayObservation);

            Assert.Same(decision.Intent, replay.Intent);
            Assert.Equal(decision.AuthorityHash, replay.AuthorityHash);
            Assert.Equal(0d, replay.HeuristicScore);
            Assert.True(replayObservation.ContainsLegalAction(replay.Intent.Action));

            AiSubmissionResult accepted = service.Submit(
                replay,
                Authority(replayObservation),
                1000L);
            Assert.True(accepted.IsAccepted, accepted.Detail);

            AiObservation narrowed = Observation(
                new[] { normal },
                context,
                hand,
                PinFiveRed);
            Assert.Throws<ArgumentException>(
                () => AiDecisionService.Replay(decision.Intent, narrowed));
        }

        [Fact]
        public void BasicHeuristicScoreDoesNotEnterTheAuthorityHash()
        {
            LegalActionOption discard = Discard(T(4), "discard-5m");
            var actions = new[] { discard };
            AiObservation observation = Observation(actions, Context());
            var intent = new CommandIntent(
                observation,
                discard,
                BasicAiStrategy.AlgorithmIdValue,
                BasicAiStrategy.AlgorithmVersionValue);

            var zero = new AiDecision(observation, intent, 0d);
            var large = new AiDecision(observation, intent, 9876.5d);

            Assert.Equal(zero.AuthorityHash, large.AuthorityHash);
            Assert.True(zero.Intent.CanonicalBytes().Length > 0);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static TileId T(int typeIndex, int copy = 0) =>
            TileId.FromTypeIndex(typeIndex, copy);

        private static IReadOnlyList<TileId> Tiles(params TileId[] tiles) => tiles;

        private static IReadOnlyList<TileId> DefaultHand()
        {
            var hand = new List<TileId>(13);
            for (int i = 0; i < 13; i++)
            {
                hand.Add(new TileId(20 + i));
            }

            return hand;
        }

        private static IReadOnlyList<TileId> RiichiWaitHand() => Tiles(
            T(0), T(1), T(2),
            T(3), T(4), T(5),
            T(6), T(7), T(8),
            T(18), T(19), T(20),
            T(22, 0), T(22, 1));

        private static IReadOnlyList<long> ScoresAll() =>
            new long[] { 25000L, 25000L, 25000L, 25000L };

        private static IReadOnlyList<long> Scores(long seat1) =>
            new long[] { 25000L, seat1, 25000L, 25000L };

        private static LegalActionOption Discard(TileId tile, string optionId) =>
            new LegalActionOption(LegalActionKind.Discard, Seat, tile, optionId);

        private static LegalActionOption Pass(string optionId = "pass") =>
            new LegalActionOption(LegalActionKind.Pass, Seat, optionId: optionId);

        private static LegalActionOption Ron(string optionId = "ron") =>
            new LegalActionOption(LegalActionKind.Ron, Seat, optionId: optionId);

        private static LegalActionOption Tsumo(string? optionId = null) =>
            new LegalActionOption(LegalActionKind.Tsumo, Seat, optionId: optionId);

        private static LegalActionOption Riichi(TileId tile, string optionId) =>
            new LegalActionOption(LegalActionKind.Riichi, Seat, tile, optionId);

        private static LegalActionOption Kan(string optionId, TileId? tile = null) =>
            new LegalActionOption(LegalActionKind.Kan, Seat, tile, optionId);

        private static LegalActionOption Pon(string optionId = "pon") =>
            new LegalActionOption(LegalActionKind.Pon, Seat, optionId: optionId);

        private static LegalActionOption Chi(string optionId) =>
            new LegalActionOption(LegalActionKind.Chi, Seat, optionId: optionId);

        private static LegalActionOption Other(string optionId) =>
            new LegalActionOption(LegalActionKind.Other, Seat, optionId: optionId);

        private static BasicCandidateFacts AbortiveFact(LegalActionOption candidate) =>
            new BasicCandidateFacts(candidate, abortiveDraw: BasicAbortiveDrawKind.NineTerminals);

        private static BasicAiContext Context(
            IReadOnlyList<BasicCandidateFacts>? facts = null,
            bool isRiichi = false,
            TileId? lastDiscard = null)
        {
            return new BasicAiContext(
                Seat,
                isRiichi,
                lastDiscard,
                facts ?? Array.Empty<BasicCandidateFacts>());
        }

        private static AiStrategicChoice Choose(
            IReadOnlyList<LegalActionOption> actions,
            BasicAiContext context,
            IReadOnlyList<TileId>? hand = null,
            TileId? drawn = null,
            int dealerSeat = 0,
            int roundWind = 1,
            IReadOnlyList<long>? scores = null)
        {
            AiStrategicChoice? choice = Basic.TryChoose(
                actions,
                context,
                hand ?? DefaultHand(),
                drawn,
                dealerSeat,
                roundWind,
                scores ?? ScoresAll(),
                CancellationToken.None);
            Assert.NotNull(choice);
            return choice!;
        }

        private static AiStrategicChoice? TryRaw(
            IReadOnlyList<LegalActionOption> actions,
            BasicAiContext context,
            IReadOnlyList<TileId>? hand = null,
            TileId? drawn = null,
            int dealerSeat = 0,
            int roundWind = 1,
            IReadOnlyList<long>? scores = null)
        {
            return Basic.TryChoose(
                actions,
                context,
                hand ?? DefaultHand(),
                drawn,
                dealerSeat,
                roundWind,
                scores ?? ScoresAll(),
                CancellationToken.None);
        }

        private static AiObservation Observation(
            IReadOnlyList<LegalActionOption> actions,
            BasicAiContext? context = null,
            IReadOnlyList<TileId>? hand = null,
            TileId? drawn = null,
            IReadOnlyList<long>? scores = null,
            int seat = Seat,
            int dealerSeat = 0,
            int roundWind = 1,
            int remainingWallCount = 69)
        {
            return new AiObservation(
                "session-basic-1",
                new DecisionKey(RoundId, seat, DecisionId),
                Epoch,
                7L,
                Deadline,
                RoundPhase.Discard,
                dealerSeat,
                roundWind,
                1,
                0,
                0,
                remainingWallCount,
                hand ?? DefaultHand(),
                drawn,
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                new[] { new AiVisibleDiscard((seat + 1) % 4, T(15)) },
                new[] { T(25) },
                scores ?? ScoresAll(),
                actions,
                context);
        }

        private static AiAuthoritySnapshot Authority(AiObservation observation)
        {
            return new AiAuthoritySnapshot(
                observation.Seat,
                observation.RoundId,
                observation.DecisionKey,
                observation.ControlEpoch,
                true,
                false,
                observation.DecisionDeadlineMillis,
                observation.LegalActions);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Behaviour matrix for the ENG-016 port of V4
    /// <c>engine/src/main/kotlin/com/majiang/ai/TargetAwareAIStrategies.kt</c>.
    ///
    /// The tests pin the source branch order, integer scores, strict SuitGoal improvement
    /// checks, HonorTriplet gap rule, Kokushi extra-copy penalty, physical hand removal,
    /// first-on-full-tie and maximum typeIndex/copyIndex ordering, the BasicStrategy fallback
    /// over the complete candidate list, decision-window binding and input immutability.
    /// </summary>
    public class TargetAwareAiStrategiesTests
    {
        private const string SessionId = "session-target-aware-1";
        private const string RoundId = "round-target-aware-1";
        private const string DecisionId = "turn-round-target-aware-1-9-seat1";
        private const int Seat = 1;
        private const long Epoch = 3L;
        private const long StateVersion = 11L;
        private static readonly CancellationToken TestToken = CancellationToken.None;

        // ---------------------------------------------------------------------
        // Context and candidate binding
        // ---------------------------------------------------------------------

        [Fact]
        public void ContextRejectsObservationWithoutBasicContext()
        {
            LegalActionOption pon = Pon(T(4, 2), "pon-5m");
            AiObservation observation = Observation(
                new[] { pon },
                hand: Tiles(T(4, 0), T(4, 1)),
                lastDiscard: T(4, 2),
                omitBasicContext: true);

            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>()));
        }

        [Fact]
        public void ContextRejectsMissingChiOrPonFact()
        {
            LegalActionOption pon = Pon(T(4, 2), "pon-5m");
            AiObservation observation = Observation(
                new[] { pon },
                hand: Tiles(T(4, 0), T(4, 1)),
                lastDiscard: T(4, 2));

            TargetAwareAiContextException error = Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>()));

            Assert.Contains("Missing", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ContextRejectsFactForCandidateOutsideTheFrozenListEvenWhenOptionIdMatches()
        {
            // Same seat, kind, physical tile and OptionId; only the candidate instance is
            // different. The full candidate identity must still be in the frozen list.
            LegalActionOption frozen = Pon(T(4, 2), "same-option");
            LegalActionOption foreign = Pon(T(5, 2), "same-option");
            AiObservation observation = Observation(
                new[] { frozen },
                hand: Tiles(T(4, 0), T(4, 1)),
                lastDiscard: T(4, 2));

            TargetAwarePonFact foreignFact = PonFact(foreign, T(5, 2), T(5, 0), T(5, 1));
            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(observation, new[] { foreignFact }));

            // Same seat and OptionId, but a different candidate kind: the fact must not be
            // applied to the frozen Pon candidate.
            LegalActionOption chiWithSameId = new LegalActionOption(
                LegalActionKind.Chi,
                Seat,
                T(4, 2),
                "same-option");
            AiObservation ponOnly = Observation(
                new[] { frozen },
                hand: Tiles(T(4, 0), T(4, 1)),
                lastDiscard: T(4, 2));
            TargetAwareChiFact chiFact = ChiFact(
                chiWithSameId,
                T(4, 2),
                T(3),
                T(4, 2),
                T(5));
            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(ponOnly, new[] { chiFact }));
        }

        [Fact]
        public void ContextRejectsFactWithForeignSeat()
        {
            LegalActionOption pon = Pon(T(4, 2), "pon-5m");
            AiObservation observation = Observation(
                new[] { pon },
                hand: Tiles(T(4, 0), T(4, 1)),
                lastDiscard: T(4, 2));
            LegalActionOption foreign = new LegalActionOption(
                LegalActionKind.Pon,
                2,
                T(4, 2),
                "pon-5m");
            TargetAwarePonFact fact = PonFact(foreign, T(4, 2), T(4, 0), T(4, 1));

            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(observation, new[] { fact }));
        }

        [Fact]
        public void ContextRejectsMalformedChiComposition()
        {
            LegalActionOption chi = Chi(T(4, 2), "chi-5m");
            AiObservation observation = Observation(
                new[] { chi },
                hand: Tiles(T(0), T(2)),
                lastDiscard: T(4, 2));

            // 1m,3m,5m is not a consecutive sequence. The fact constructor is the first
            // boundary and must reject it.
            Assert.Throws<TargetAwareAiContextException>(
                () => ChiFact(chi, T(4, 2), T(0), T(2), T(4, 2)));

            // The called tile must be part of the complete meld.
            Assert.Throws<TargetAwareAiContextException>(
                () => ChiFact(chi, T(4, 2), T(0), T(1), T(2)));

            // Repeated ranks are not a sequence even though the rank span is two.
            Assert.Throws<TargetAwareAiContextException>(
                () => ChiFact(chi, T(4, 2), T(4, 0), T(4, 1), T(4, 2)));
        }

        [Fact]
        public void ContextRejectsChiFactThatClaimsATileOutsideTheVisibleHand()
        {
            LegalActionOption chi = Chi(T(3), "chi-4m");
            AiObservation observation = Observation(
                new[] { chi },
                hand: Tiles(T(2), T(3)),
                lastDiscard: T(3));

            // 2m,3m,4m is a valid Chi, but 2m is not in the visible hand.
            TargetAwareChiFact fact = ChiFact(chi, T(3), T(1), T(2), T(3));
            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(observation, new[] { fact }));
        }

        [Fact]
        public void ContextRejectsMalformedPonComposition()
        {
            LegalActionOption pon = Pon(T(4, 2), "pon-5m");

            // Different logical type.
            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwarePonFact(pon, new[] { T(4, 0), T(5, 0) }, T(4, 2)));

            // The called physical tile cannot also be a hand tile.
            LegalActionOption calledZero = Pon(T(4, 0), "pon-5m-copy0");
            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwarePonFact(calledZero, new[] { T(4, 0), T(4, 1) }, T(4, 0)));
        }

        [Fact]
        public void ContextRejectsDuplicateFactsForOneCandidate()
        {
            LegalActionOption pon = Pon(T(4, 2), "pon-5m");
            AiObservation observation = Observation(
                new[] { pon },
                hand: Tiles(T(4, 0), T(4, 1)),
                lastDiscard: T(4, 2));
            TargetAwarePonFact fact = PonFact(pon, T(4, 2), T(4, 0), T(4, 1));

            Assert.Throws<TargetAwareAiContextException>(
                () => new TargetAwareAiContext(observation, new[] { fact, fact }));
        }

        [Fact]
        public void ContextCopiesFactsAndExposesReadOnlyCollections()
        {
            LegalActionOption chi = Chi(T(4), "chi-5m");
            var legal = new[] { chi };
            var hand = new[] { T(2), T(3) };
            AiObservation observation = Observation(
                legal,
                hand: hand,
                lastDiscard: T(4));
            var meld = new[] { T(2), T(3), T(4) };
            var fact = new TargetAwareChiFact(chi, meld, T(4));
            var context = new TargetAwareAiContext(observation, new[] { fact });

            meld[0] = T(0);
            hand[0] = T(8);
            legal[0] = Pon(T(6), "mutated");

            Assert.Equal(T(2), context.MeldFacts[0].Tiles[0]);
            Assert.Equal(T(3), context.MeldFacts[0].Tiles[1]);
            Assert.Equal(chi, context.Candidates[0]);
            Assert.Single(context.Candidates);

            Assert.Throws<NotSupportedException>(
                () => ((IList<TileId>)context.MeldFacts[0].Tiles).Add(T(9)));
            Assert.Throws<NotSupportedException>(
                () => ((IList<TargetAwareMeldFact>)context.MeldFacts).Add(fact));
            Assert.Throws<NotSupportedException>(
                () => ((IList<LegalActionOption>)context.Candidates).Add(chi));
        }

        [Fact]
        public void PureEntryRejectsOldDecisionId()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            AiObservation stale = Observation(
                new[] { discard },
                hand: Tiles(T(0)),
                decisionId: "another-decision");

            Assert.Throws<TargetAwareAiContextException>(
                () => new SuitGoalAi(TileSuit.Man).TryChoose(stale, context, TestToken));
        }

        [Fact]
        public void PureEntryRejectsOldStateVersion()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            AiObservation stale = Observation(
                new[] { discard },
                hand: Tiles(T(0)),
                stateVersion: StateVersion + 1);

            Assert.Throws<TargetAwareAiContextException>(
                () => new HonorTripletGoalAi(new[] { 31 }).TryChoose(stale, context, TestToken));
        }

        [Fact]
        public void PureEntryRejectsOldControlEpoch()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            AiObservation stale = Observation(
                new[] { discard },
                hand: Tiles(T(0)),
                epoch: Epoch + 1);

            Assert.Throws<TargetAwareAiContextException>(
                () => KokushiGoalAi.Instance.TryChoose(stale, context, TestToken));
        }

        [Fact]
        public void PureEntryRejectsDifferentCandidateListInTheSameWindow()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var changed = Observation(
                new[] { discard, Discard(T(1), "discard-2m") },
                hand: Tiles(T(0), T(1)));

            Assert.Throws<TargetAwareAiContextException>(
                () => new SuitGoalAi(TileSuit.Man).TryChoose(changed, context, TestToken));
        }

        [Fact]
        public void PureEntryRejectsNullObservationOrContext()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            Assert.Throws<ArgumentNullException>(
                () => new SuitGoalAi(TileSuit.Man).TryChoose(null!, context, TestToken));
            Assert.Throws<ArgumentNullException>(
                () => new SuitGoalAi(TileSuit.Man).TryChoose(observation, null!, TestToken));
        }

        // ---------------------------------------------------------------------
        // Shared win and riichi branches
        // ---------------------------------------------------------------------

        [Fact]
        public void AllStrategiesChooseRonBeforeTsumoEvenWhenTsumoIsFirst()
        {
            LegalActionOption tsumo = Tsumo("tsumo");
            LegalActionOption ron = Ron("ron");
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(
                new[] { tsumo, discard, ron },
                hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            Assert.Same(ron, new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!.Action);
            Assert.Same(ron, new HonorTripletGoalAi(new[] { 31 }).TryChoose(observation, context, TestToken)!.Action);
            Assert.Same(ron, KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!.Action);
        }

        [Fact]
        public void AllStrategiesChooseTsumoWhenRonIsAbsent()
        {
            LegalActionOption tsumo = Tsumo("tsumo");
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(
                new[] { discard, tsumo },
                hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            Assert.Same(tsumo, new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!.Action);
            Assert.Same(tsumo, new HonorTripletGoalAi(new[] { 31 }).TryChoose(observation, context, TestToken)!.Action);
            Assert.Same(tsumo, KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!.Action);
        }

        [Fact]
        public void AllStrategiesHonorRiichiTsumogiriForTheExactPhysicalDrawnTile()
        {
            TileId drawn = T(26, 0); // 9p
            LegalActionOption drawnDiscard = Discard(drawn, "discard-drawn");
            LegalActionOption other = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(
                new[] { other, drawnDiscard },
                hand: Tiles(T(0), T(1), T(2)),
                drawn: drawn,
                isRiichi: true);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            Assert.Same(drawnDiscard, new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!.Action);
            Assert.Same(drawnDiscard, new HonorTripletGoalAi(new[] { 31 }).TryChoose(observation, context, TestToken)!.Action);
            Assert.Same(drawnDiscard, KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!.Action);
        }

        [Fact]
        public void RiichiTsumogiriDoesNotMatchADifferentCopyOfTheSameType()
        {
            // Drawn red 5p (copy 3), but only the normal 5p copy is legal. The exact physical
            // identity must not match, so the strategy continues to its ordinary branch.
            TileId drawn = T(22, 3);
            LegalActionOption normalFive = Discard(T(22, 0), "discard-5p-normal");
            LegalActionOption oneMan = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(
                new[] { normalFive, oneMan },
                hand: Tiles(T(0), T(1), T(2)),
                drawn: drawn,
                isRiichi: true);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Equal(LegalActionKind.Discard, choice.Action.Kind);
            Assert.Same(normalFive, choice.Action);
            Assert.Equal(324d, choice.HeuristicScore); // ordinary SuitGoal discard branch, not tsumogiri
        }

        // ---------------------------------------------------------------------
        // SuitGoalAi
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData(TileSuit.Man)]
        [InlineData(TileSuit.Pin)]
        [InlineData(TileSuit.Sou)]
        public void SuitGoalDiscardsOffSuitFirstForEachNumberSuit(TileSuit target)
        {
            int baseType = target == TileSuit.Man ? 0 : target == TileSuit.Pin ? 18 : 9;
            TileId t1 = T(baseType);
            TileId t2 = T(baseType + 1);
            TileId t3 = T(baseType + 2);
            TileId t5 = T(baseType + 4);

            TileId offA = target == TileSuit.Pin ? T(0) : T(22);
            TileId offB = target == TileSuit.Sou ? T(0) : T(17);
            var hand = Tiles(t1, t2, t3, t5, offA, offB);
            var actions = new[]
            {
                Discard(t1, "d1"),
                Discard(t2, "d2"),
                Discard(t3, "d3"),
                Discard(t5, "d5"),
                Discard(offA, "off-a"),
                Discard(offB, "off-b")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = new SuitGoalAi(target).TryChoose(observation, context, TestToken)!;
            Assert.Equal(LegalActionKind.Discard, choice.Action.Kind);
            Assert.NotEqual(target, choice.Action.Tile!.Value.Suit);
        }

        [Fact]
        public void SuitGoalChoosesMaximumTypeIndexOnEqualDiscardScore()
        {
            // Both East and North are off-suit. Discarding either leaves the target score
            // unchanged, so the second V4 key (typeIndex) must pick North (30 > 27), not the
            // first candidate and not the smallest tile id.
            TileId east = T(27);
            TileId north = T(30);
            var hand = Tiles(T(0), T(1), T(2), east, north);
            var actions = new[]
            {
                Discard(east, "discard-east"),
                Discard(north, "discard-north")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(actions[1], choice.Action);
        }

        [Fact]
        public void SuitGoalChoosesMaximumCopyIndexOnEqualDiscardScore()
        {
            // Normal 5m copy 0 and red 5m copy 3 have the same logical type. The V4 comparator
            // uses copyIndex ascending and maxWith, so the red five (copy 3) wins the tie.
            TileId normal = T(4, 0);
            TileId red = T(4, 3);
            var hand = Tiles(normal, red, T(18));
            var actions = new[]
            {
                Discard(normal, "discard-normal-5m"),
                Discard(red, "discard-red-5m")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(actions[1], choice.Action);
            Assert.Equal(3, choice.Action.Tile!.Value.CopyIndex);
        }

        [Fact]
        public void SuitGoalPonRemovesOnlyParticipatingCopiesAndAddsTheCompleteMeld()
        {
            // Base: 1m 2m 3m 5m(0) 5m(1) 5m(2). The Pon fact uses 5m(0) and 5m(1) with the
            // called 5m(3). Correct afterPon removes exactly two copies and adds the three
            // meld tiles -> four 5m, score 740 (> 640). Removing all three 5m would leave the
            // score at 640 and the Pon branch would not fire.
            TileId called = T(4, 3);
            LegalActionOption pon = Pon(called, "pon-5m");
            LegalActionOption discard = Discard(T(18), "discard-1p");
            AiObservation observation = Observation(
                new[] { pon, discard },
                hand: Tiles(T(0), T(1), T(2), T(4, 0), T(4, 1), T(4, 2), T(18)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[] { PonFact(pon, called, T(4, 0), T(4, 1)) });

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(pon, choice.Action);
            Assert.Equal(740d, choice.HeuristicScore);
        }

        [Fact]
        public void SuitGoalChiRemovesOnlyParticipatingCopiesAndAddsTheCompleteMeld()
        {
            // Base: 2m(0) 2m(1) 3m 5m 6m 7m. Chi 2m+3m, called 4m. Correct afterChi leaves
            // 2m(1), adds 2m(0) 3m 4m -> 2m 2m 3m 4m 5m 6m 7m, score 752 (> 636). Adding
            // only the two hand tiles would leave the base unchanged and fail the strict
            // improvement check; removing both 2m copies would also change the shape.
            TileId called = T(3); // 4m
            LegalActionOption chi = Chi(called, "chi-2m3m4m");
            LegalActionOption discard = Discard(T(18), "discard-1p");
            AiObservation observation = Observation(
                new[] { chi, discard },
                hand: Tiles(T(1, 0), T(1, 1), T(2), T(4, 0), T(5), T(6), T(18)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[] { ChiFact(chi, called, T(1, 0), T(2), T(3)) });

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(chi, choice.Action);
            Assert.Equal(784d, choice.HeuristicScore);
        }

        [Fact]
        public void SuitGoalPrioritizesPonOverChiAndDiscard()
        {
            // Both the target-suit Pon and the target-suit Chi claim the same 5m discard and
            // both improve; V4 checks Pon first.
            TileId called = T(4, 3);
            LegalActionOption pon = Pon(called, "pon-5m");
            LegalActionOption chi = Chi(called, "chi-4m5m6m");
            LegalActionOption discard = Discard(T(18), "discard-1p");
            AiObservation observation = Observation(
                new[] { chi, pon, discard },
                hand: Tiles(T(3), T(5), T(4, 0), T(4, 1), T(4, 2), T(18)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[]
                {
                    ChiFact(chi, called, T(3), T(4, 3), T(5)),
                    PonFact(pon, called, T(4, 0), T(4, 1))
                });

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(pon, choice.Action);
        }

        [Fact]
        public void SuitGoalKeepsTheFirstFullyEqualPonCandidateInListOrder()
        {
            // Same score, same first hand tile type/copy, same called tile: maxWithOrNull keeps
            // the earlier candidate. Only the second physical tile and option id differ.
            TileId called = T(4, 3);
            LegalActionOption first = Pon(called, "pon-first");
            LegalActionOption second = Pon(called, "pon-second");
            AiObservation observation = Observation(
                new[] { first, second },
                hand: Tiles(T(0), T(1), T(2), T(4, 0), T(4, 1), T(4, 2)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[]
                {
                    PonFact(first, called, T(4, 0), T(4, 1)),
                    PonFact(second, called, T(4, 0), T(4, 2))
                });

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(first, choice.Action);
        }

        [Fact]
        public void SuitGoalDoesNotChiAnOffSuitMeld()
        {
            TileId called = T(20); // 3p
            LegalActionOption chi = Chi(called, "chi-pin");
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(
                new[] { chi, discard },
                hand: Tiles(T(18), T(19), T(0), T(1)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[] { ChiFact(chi, called, T(18), T(19), T(20)) });

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Equal(LegalActionKind.Discard, choice.Action.Kind);
        }

        [Fact]
        public void SuitGoalFallsBackAfterBasicChoosesAForbiddenAction()
        {
            // No discard branch. BasicStrategy chooses the closed Kan; SuitGoal forbids Kan,
            // so the first non-forbidden candidate (Pass) is returned.
            LegalActionOption kan = Kan("kan");
            LegalActionOption pass = Pass("pass");
            AiObservation observation = Observation(
                new[] { kan, pass },
                hand: Tiles(T(0)),
                basicFacts: new[] { KanFact(kan) });
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(pass, choice.Action);
        }

        [Fact]
        public void SuitGoalReturnsNullWhenEveryCandidateIsForbidden()
        {
            LegalActionOption kan = Kan("kan");
            AiObservation observation = Observation(
                new[] { kan },
                hand: Tiles(T(0)),
                basicFacts: new[] { KanFact(kan) });
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            Assert.Null(new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken));
        }

        [Fact]
        public void SuitGoalIncludesOwnPublicMeldsInTheVisibleBase()
        {
            var meld = new AiVisibleMeld(Seat, new[] { T(0), T(1), T(2) });
            LegalActionOption discard = Discard(T(18), "discard-1p");
            AiObservation observation = Observation(
                new[] { discard },
                hand: Tiles(T(18)),
                melds: new[] { meld });
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            Assert.Same(discard, choice.Action);
            Assert.Equal(324d, choice.HeuristicScore); // 3 target tiles, one sequence, three taatsu
        }

        // ---------------------------------------------------------------------
        // HonorTripletGoalAi
        // ---------------------------------------------------------------------

        [Fact]
        public void HonorGoalRejectsEmptyOrOutOfRangeTargetSets()
        {
            Assert.Throws<ArgumentException>(() => new HonorTripletGoalAi(Array.Empty<int>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HonorTripletGoalAi(new[] { 26 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HonorTripletGoalAi(new[] { 34 }));
        }

        [Fact]
        public void HonorGoalDeDuplicatesTheTargetSetLikeAKotlinSet()
        {
            var goal = new HonorTripletGoalAi(new[] { 31, 31, 32 });
            Assert.Equal(new[] { 31, 32 }, goal.RequiredHonors);
        }

        [Fact]
        public void HonorGoalKeepsTargetHonorsOverATerminal()
        {
            TileId dragonOne = T(31);
            TileId dragonTwo = T(32, 0);
            TileId dragonTwoCopy = T(32, 1);
            TileId terminal = T(18); // 1p
            var hand = Tiles(dragonOne, dragonTwo, dragonTwoCopy, T(0), T(1), T(2), terminal);
            var actions = new[]
            {
                Discard(dragonOne, "d-dragon-1"),
                Discard(dragonTwo, "d-dragon-2"),
                Discard(dragonTwoCopy, "d-dragon-2-copy"),
                Discard(T(0), "d-1m"),
                Discard(T(1), "d-2m"),
                Discard(T(2), "d-3m"),
                Discard(terminal, "d-terminal")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            AiStrategicChoice choice = goal.TryChoose(observation, context, TestToken)!;
            Assert.Same(actions[6], choice.Action);
            Assert.Equal(TileSuit.Pin, choice.Action.Tile!.Value.Suit);
        }

        [Fact]
        public void HonorGoalPonsATargetHonorOnlyWhenTheVisibleBaseHasAGap()
        {
            TileId called = T(33, 2);
            LegalActionOption pon = Pon(called, "pon-red-dragon");
            LegalActionOption discard = Discard(T(18), "discard-1p");
            AiObservation observation = Observation(
                new[] { pon, discard },
                hand: Tiles(T(33, 0), T(33, 1), T(18)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[] { PonFact(pon, called, T(33, 0), T(33, 1)) });
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            Assert.Same(pon, goal.TryChoose(observation, context, TestToken)!.Action);
        }

        [Fact]
        public void HonorGoalDoesNotPonWhenTheVisibleBaseAlreadyHasThreeCopies()
        {
            TileId called = T(33, 3);
            LegalActionOption pon = Pon(called, "pon-red-dragon");
            LegalActionOption discard = Discard(T(18), "discard-1p");
            AiObservation observation = Observation(
                new[] { pon, discard },
                hand: Tiles(T(33, 0), T(33, 1), T(33, 2), T(18)),
                lastDiscard: called);
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[] { PonFact(pon, called, T(33, 0), T(33, 1)) });
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            AiStrategicChoice choice = goal.TryChoose(observation, context, TestToken)!;
            Assert.Equal(LegalActionKind.Discard, choice.Action.Kind);
        }

        [Fact]
        public void HonorGoalUsesMaximumTypeIndexAndCopyIndexForEqualDiscards()
        {
            TileId east = T(27, 0);
            TileId northOne = T(30, 0);
            TileId northTwo = T(30, 2);
            var hand = Tiles(east, northOne, northTwo, T(0), T(1), T(2));
            var actions = new[]
            {
                Discard(east, "d-east"),
                Discard(northOne, "d-north-0"),
                Discard(northTwo, "d-north-2")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            AiStrategicChoice choice = goal.TryChoose(observation, context, TestToken)!;
            Assert.Same(actions[2], choice.Action);
            Assert.Equal(2, choice.Action.Tile!.Value.CopyIndex);
        }

        [Fact]
        public void HonorGoalIncludesOwnPublicMeldsInTheNumberMentsuScore()
        {
            var meld = new AiVisibleMeld(Seat, new[] { T(0), T(1), T(2) });
            LegalActionOption discard = Discard(T(27), "discard-east");
            AiObservation observation = Observation(
                new[] { discard },
                hand: Tiles(T(27)),
                melds: new[] { meld });
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            AiStrategicChoice choice = goal.TryChoose(observation, context, TestToken)!;
            Assert.Same(discard, choice.Action);
            Assert.Equal(3d, choice.HeuristicScore); // one number mentsu from the own meld
        }

        [Fact]
        public void HonorGoalFallbackForbidsChiKanAndOffTargetPon()
        {
            TileId called = T(22, 2); // 5p, not a target honor
            LegalActionOption chi = Chi(called, "chi-4p5p6p");
            LegalActionOption kan = Kan("kan");
            LegalActionOption pon = Pon(called, "pon-5p");
            LegalActionOption pass = Pass("pass");
            AiObservation observation = Observation(
                new[] { chi, kan, pon, pass },
                hand: Tiles(T(21), T(23), T(22, 0), T(22, 1)),
                lastDiscard: called,
                basicFacts: new[] { KanFact(kan) });
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[]
                {
                    ChiFact(chi, called, T(21), T(22, 2), T(23)),
                    PonFact(pon, called, T(22, 0), T(22, 1))
                });
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            AiStrategicChoice choice = goal.TryChoose(observation, context, TestToken)!;
            Assert.Same(pass, choice.Action);
        }

        [Fact]
        public void HonorGoalReturnsNullWhenEveryCandidateIsForbidden()
        {
            TileId called = T(22, 2);
            LegalActionOption chi = Chi(called, "chi-4p5p6p");
            LegalActionOption kan = Kan("kan");
            LegalActionOption pon = Pon(called, "pon-5p");
            AiObservation observation = Observation(
                new[] { chi, kan, pon },
                hand: Tiles(T(21), T(23), T(22, 0), T(22, 1)),
                lastDiscard: called,
                basicFacts: new[] { KanFact(kan) });
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[]
                {
                    ChiFact(chi, called, T(21), T(22, 2), T(23)),
                    PonFact(pon, called, T(22, 0), T(22, 1))
                });
            var goal = new HonorTripletGoalAi(new[] { 31, 32, 33 });

            Assert.Null(goal.TryChoose(observation, context, TestToken));
        }

        // ---------------------------------------------------------------------
        // KokushiGoalAi
        // ---------------------------------------------------------------------

        [Fact]
        public void KokushiDiscardsANonTerminalBeforeAUniqueTerminal()
        {
            var hand = new List<TileId>
            {
                T(0), T(8), T(9), T(17), T(18), T(26),
                T(27), T(28), T(29), T(30), T(31), T(32), T(33),
                T(4)
            };
            var actions = new List<LegalActionOption>();
            for (int i = 0; i < hand.Count; i++)
            {
                actions.Add(Discard(hand[i], "d-" + hand[i].Value));
            }

            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!;
            Assert.Equal(T(4), choice.Action.Tile);
        }

        [Fact]
        public void KokushiPenalizesExtraCopiesAndUsesMaximumTypeIndexOnTie()
        {
            // 11 distinct kokushi types, plus a pair of 1m and 9m, plus 5m. Discarding 5m
            // leaves the extra-copy penalty; discarding one 9m removes one extra copy and
            // ties with discarding one 1m, then typeIndex picks 9m over 1m.
            var hand = new List<TileId>
            {
                T(0, 0), T(8, 0), T(9), T(17), T(18), T(26),
                T(27), T(28), T(29), T(30), T(31),
                T(0, 1), T(8, 1), T(4)
            };
            var actions = new List<LegalActionOption>();
            for (int i = 0; i < hand.Count; i++)
            {
                actions.Add(Discard(hand[i], "d-" + hand[i].Value));
            }

            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!;
            Assert.Equal(T(8, 1), choice.Action.Tile);
            Assert.Equal(1115d, choice.HeuristicScore);
        }

        [Fact]
        public void KokushiSkipsNineTerminalsAndForbidsChiPonKan()
        {
            TileId called = T(22, 2);
            LegalActionOption abortive = Other("abortive");
            LegalActionOption chi = Chi(called, "chi-4p5p6p");
            LegalActionOption pon = Pon(called, "pon-5p");
            LegalActionOption kan = Kan("kan");
            LegalActionOption pass = Pass("pass");
            AiObservation observation = Observation(
                new[] { abortive, chi, pon, kan, pass },
                hand: Tiles(T(21), T(23), T(22, 0), T(22, 1)),
                lastDiscard: called,
                basicFacts: new[]
                {
                    new BasicCandidateFacts(abortive, abortiveDraw: BasicAbortiveDrawKind.NineTerminals),
                    KanFact(kan)
                });
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[]
                {
                    ChiFact(chi, called, T(21), T(22, 2), T(23)),
                    PonFact(pon, called, T(22, 0), T(22, 1))
                });

            AiStrategicChoice choice = KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!;
            Assert.Same(pass, choice.Action);
        }

        [Fact]
        public void KokushiReturnsNullWhenOnlyForbiddenActionsRemain()
        {
            TileId called = T(22, 2);
            LegalActionOption abortive = Other("abortive");
            LegalActionOption pon = Pon(called, "pon-5p");
            AiObservation observation = Observation(
                new[] { abortive, pon },
                hand: Tiles(T(22, 0), T(22, 1)),
                lastDiscard: called,
                basicFacts: new[]
                {
                    new BasicCandidateFacts(abortive, abortiveDraw: BasicAbortiveDrawKind.NineTerminals)
                });
            var context = new TargetAwareAiContext(
                observation,
                new TargetAwareMeldFact[] { PonFact(pon, called, T(22, 0), T(22, 1)) });

            Assert.Null(KokushiGoalAi.Instance.TryChoose(observation, context, TestToken));
        }

        [Fact]
        public void KokushiScoresOnlyTheClosedHandAndIgnoresOwnPublicMelds()
        {
            // The closed hand has all 13 kokushi types plus 5m. The own meld contains another
            // 1m copy; if it were included, hasPair would become true and the score would be
            // 1315 instead of 1300.
            var meld = new AiVisibleMeld(Seat, new[] { T(0, 1), T(1), T(2) });
            var hand = Tiles(
                T(0, 0), T(8), T(9), T(17), T(18), T(26),
                T(27), T(28), T(29), T(30), T(31), T(32), T(33),
                T(4));
            LegalActionOption discard = Discard(T(4), "discard-5m");
            AiObservation observation = Observation(
                new[] { discard },
                hand: hand,
                melds: new[] { meld });
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!;
            Assert.Same(discard, choice.Action);
            Assert.Equal(1300d, choice.HeuristicScore);
        }

        [Fact]
        public void KokushiChoosesMaximumTypeIndexForEqualNonTerminalDiscards()
        {
            // Twelve unique kokushi types plus 5m and 5p. Both non-terminals leave the same
            // score; typeIndex must pick 5p (22) over 5m (4).
            var hand = Tiles(
                T(0), T(8), T(9), T(17), T(18), T(26),
                T(27), T(28), T(29), T(30), T(31), T(32),
                T(4), T(22));
            var actions = new[]
            {
                Discard(T(4), "discard-5m"),
                Discard(T(22), "discard-5p")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());

            AiStrategicChoice choice = KokushiGoalAi.Instance.TryChoose(observation, context, TestToken)!;
            Assert.Same(actions[1], choice.Action);
        }

        // ---------------------------------------------------------------------
        // Empty/cancel/null, immutability and authority-boundary evidence
        // ---------------------------------------------------------------------

        [Fact]
        public void EmptyRawCandidateListReturnsNullForAllThreeStrategies()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var empty = Array.Empty<LegalActionOption>();
            long[] scores = { 25000L, 25000L, 25000L, 25000L };

            Assert.Null(new SuitGoalAi(TileSuit.Man).TryChoose(
                empty, context, observation.MyHandTileIds, observation.MyDrawnTileId,
                observation.MyMelds, observation.DealerSeat, observation.RoundWind, scores, TestToken));
            Assert.Null(new HonorTripletGoalAi(new[] { 31 }).TryChoose(
                empty, context, observation.MyHandTileIds, observation.MyDrawnTileId,
                observation.MyMelds, observation.DealerSeat, observation.RoundWind, scores, TestToken));
            Assert.Null(KokushiGoalAi.Instance.TryChoose(
                empty, context, observation.MyHandTileIds, observation.MyDrawnTileId,
                observation.MyMelds, observation.DealerSeat, observation.RoundWind, scores, TestToken));
        }

        [Fact]
        public void RawEntryRejectsNullInputs()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var legal = new[] { discard };
            long[] scores = { 25000L, 25000L, 25000L, 25000L };
            var strategy = new SuitGoalAi(TileSuit.Man);

            Assert.Throws<ArgumentNullException>(() => strategy.TryChoose(
                null!, context, observation.MyHandTileIds, null, observation.MyMelds, 0, 1, scores, TestToken));
            Assert.Throws<ArgumentNullException>(() => strategy.TryChoose(
                legal, null!, observation.MyHandTileIds, null, observation.MyMelds, 0, 1, scores, TestToken));
            Assert.Throws<ArgumentNullException>(() => strategy.TryChoose(
                legal, context, null!, null, observation.MyMelds, 0, 1, scores, TestToken));
            Assert.Throws<ArgumentNullException>(() => strategy.TryChoose(
                legal, context, observation.MyHandTileIds, null, null!, 0, 1, scores, TestToken));
            Assert.Throws<ArgumentNullException>(() => strategy.TryChoose(
                legal, context, observation.MyHandTileIds, null, observation.MyMelds, 0, 1, null!, TestToken));
        }

        [Fact]
        public void CanceledTokenStopsEveryStrategyBeforeWork()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var canceled = new CancellationToken(canceled: true);

            Assert.Throws<OperationCanceledException>(
                () => new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, canceled));
            Assert.Throws<OperationCanceledException>(
                () => new HonorTripletGoalAi(new[] { 31 }).TryChoose(observation, context, canceled));
            Assert.Throws<OperationCanceledException>(
                () => KokushiGoalAi.Instance.TryChoose(observation, context, canceled));
            Assert.Throws<OperationCanceledException>(() => new SuitGoalAi(TileSuit.Man).TryChoose(
                observation.LegalActions,
                context,
                observation.MyHandTileIds,
                observation.MyDrawnTileId,
                observation.MyMelds,
                observation.DealerSeat,
                observation.RoundWind,
                observation.Scores,
                canceled));
        }

        [Fact]
        public void StrategyDoesNotMutateHandOrObservationCollections()
        {
            var hand = Tiles(T(0), T(1), T(2), T(22));
            var actions = new[]
            {
                Discard(T(22), "discard-5p"),
                Discard(T(0), "discard-1m")
            };
            AiObservation observation = Observation(actions, hand: hand);
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            var beforeHand = new List<TileId>(observation.MyHandTileIds);

            new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken);

            Assert.Equal(beforeHand, observation.MyHandTileIds);
            Assert.Equal(2, observation.LegalActions.Count);
        }

        [Fact]
        public void HeuristicScoreDoesNotEnterTheAuthorityHash()
        {
            LegalActionOption discard = Discard(T(0), "discard-1m");
            AiObservation observation = Observation(new[] { discard }, hand: Tiles(T(0)));
            var context = new TargetAwareAiContext(observation, Array.Empty<TargetAwareMeldFact>());
            AiStrategicChoice choice = new SuitGoalAi(TileSuit.Man).TryChoose(observation, context, TestToken)!;
            var intent = new CommandIntent(
                observation,
                choice.Action,
                SuitGoalAi.AlgorithmIdValue,
                SuitGoalAi.AlgorithmVersionValue);

            var zero = new AiDecision(observation, intent, 0d);
            var large = new AiDecision(observation, intent, 9876.5d);

            Assert.Equal(zero.AuthorityHash, large.AuthorityHash);
            Assert.Equal(zero.Intent.CanonicalBytes(), large.Intent.CanonicalBytes());
        }

        [Fact]
        public void TargetAwareSurfaceExposesNoHiddenStateProperties()
        {
            string[] forbidden =
            {
                "GameState", "RoundState", "FullState", "Wall", "DeadWall", "Seed",
                "Opponent", "Hidden", "UraDora", "TilePositions", "TilePositionMap"
            };

            Type[] types =
            {
                typeof(TargetAwareAiContext),
                typeof(TargetAwareMeldFact),
                typeof(TargetAwareChiFact),
                typeof(TargetAwarePonFact)
            };

            foreach (Type type in types)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (string name in forbidden)
                    {
                        Assert.False(
                            property.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0,
                            type.Name + " exposes forbidden hidden-state property " + property.Name + ".");
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static TileId T(int typeIndex, int copy = 0) =>
            TileId.FromTypeIndex(typeIndex, copy);

        private static IReadOnlyList<TileId> Tiles(params TileId[] tiles) => tiles;

        private static LegalActionOption Discard(TileId tile, string optionId) =>
            new LegalActionOption(LegalActionKind.Discard, Seat, tile, optionId);

        private static LegalActionOption Chi(TileId called, string optionId) =>
            new LegalActionOption(LegalActionKind.Chi, Seat, called, optionId);

        private static LegalActionOption Pon(TileId called, string optionId) =>
            new LegalActionOption(LegalActionKind.Pon, Seat, called, optionId);

        private static LegalActionOption Kan(string optionId, TileId? tile = null) =>
            new LegalActionOption(LegalActionKind.Kan, Seat, tile, optionId);

        private static LegalActionOption Pass(string optionId = "pass") =>
            new LegalActionOption(LegalActionKind.Pass, Seat, optionId: optionId);

        private static LegalActionOption Ron(string optionId = "ron") =>
            new LegalActionOption(LegalActionKind.Ron, Seat, optionId: optionId);

        private static LegalActionOption Tsumo(string optionId = "tsumo") =>
            new LegalActionOption(LegalActionKind.Tsumo, Seat, optionId: optionId);

        private static LegalActionOption Other(string optionId) =>
            new LegalActionOption(LegalActionKind.Other, Seat, optionId: optionId);

        private static BasicCandidateFacts KanFact(
            LegalActionOption candidate,
            BasicKanKind kind = BasicKanKind.Closed) =>
            new BasicCandidateFacts(candidate, kanKind: kind);

        private static TargetAwareChiFact ChiFact(
            LegalActionOption candidate,
            TileId called,
            params TileId[] meldTiles) =>
            new TargetAwareChiFact(candidate, meldTiles, called);

        private static TargetAwarePonFact PonFact(
            LegalActionOption candidate,
            TileId called,
            params TileId[] handTiles) =>
            new TargetAwarePonFact(candidate, handTiles, called);

        private static AiObservation Observation(
            IReadOnlyList<LegalActionOption> actions,
            IReadOnlyList<TileId>? hand = null,
            TileId? drawn = null,
            IReadOnlyList<AiVisibleMeld>? melds = null,
            TileId? lastDiscard = null,
            bool isRiichi = false,
            IReadOnlyList<BasicCandidateFacts>? basicFacts = null,
            bool omitBasicContext = false,
            string decisionId = DecisionId,
            long epoch = Epoch,
            long stateVersion = StateVersion,
            int seat = Seat,
            IReadOnlyList<long>? scores = null)
        {
            BasicAiContext? basic = omitBasicContext
                ? null
                : new BasicAiContext(
                    seat,
                    isRiichi,
                    lastDiscard,
                    basicFacts ?? Array.Empty<BasicCandidateFacts>());

            return new AiObservation(
                SessionId,
                new DecisionKey(RoundId, seat, decisionId),
                epoch,
                stateVersion,
                23000L,
                RoundPhase.Discard,
                0,
                1,
                1,
                0,
                0,
                69,
                hand ?? Tiles(T(0), T(1), T(2), T(3), T(4), T(5), T(6), T(7), T(8), T(9), T(10), T(11), T(12)),
                drawn,
                melds ?? Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                new[] { T(25) },
                scores ?? new long[] { 25000L, 25000L, 25000L, 25000L },
                actions,
                basic);
        }
    }
}

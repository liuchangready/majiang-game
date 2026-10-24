using System;
using System.Collections.Generic;
using Mahjong.Ai;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Phase matrix and strategy-entry tests for <see cref="AiDecisionContext"/>.
    ///
    /// The real V4 classes were probed for the behaviour this port must preserve:
    /// <c>candidate(id)</c> is an exact, case-sensitive <c>firstOrNull</c>;
    /// <c>safeDefault()</c> is the first flagged candidate; the candidate list is an
    /// unmodifiable copy; duplicate ids throw; a null id throws. The probe log is in the
    /// task's evidence; see also <see cref="AiFrozenContextTests"/>.
    ///
    /// Unlike V4, the V5 snapshot supports IDLE, SHUFFLE_PREPARATION and offer windows
    /// without fabricating an ordinary <c>DecisionKey</c>. These tests exercise all four
    /// phases, empty candidate sets, and both ordinary and cheat candidate ids through the
    /// same strategy entry point.
    /// </summary>
    public class AiFrozenContextPhaseTests
    {
        [Fact]
        public void OrdinaryActionCarriesObservationAndOrdinaryCandidates()
        {
            LegalActionOption[] actions =
            {
                FrozenFixtures.Option("opt-A"),
                FrozenFixtures.Option("opt-b", LegalActionKind.Pass, null)
            };
            var key = new DecisionKey("r1", FrozenFixtures.Seat, "d1");
            AiObservation observation = FrozenFixtures.Observation(key, 3, 9, 5000, actions);
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                actions,
                observation: observation);
            var candidates = new[]
            {
                FrozenFixtures.Candidate("opt-A"),
                FrozenFixtures.Candidate("opt-b", true)
            };

            var context = new AiDecisionContext(snapshot, candidates);

            Assert.Same(observation, context.OrdinaryObservation);
            Assert.Equal(2, context.OrdinaryLegalOptions.Count);
            Assert.Equal("opt-A", context.Candidate("opt-A")!.OptionId);
            Assert.Equal("opt-b", context.SafeDefault()!.OptionId);
        }

        [Fact]
        public void ShufflePreparationWorksWithoutAnOrdinaryDecision()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.ShufflePreparation,
                    decisionId: "prep-r1-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(preparation: FrozenFixtures.Preparation()));
            var candidates = new[]
            {
                FrozenFixtures.Candidate("prep-skip", true, AiPhase.ShufflePreparation),
                FrozenFixtures.Candidate("prep-kokushi", false, AiPhase.ShufflePreparation, "shuffle", 30)
            };

            var context = new AiDecisionContext(snapshot, candidates);

            Assert.Null(context.OrdinaryObservation);
            Assert.Empty(context.OrdinaryLegalOptions);
            Assert.Equal(AiPhase.ShufflePreparation, context.Phase);
            Assert.Equal("prep-r1-1", context.DecisionId);
            Assert.Equal("prep-skip", context.SafeDefault()!.OptionId);
            Assert.Equal("prep-kokushi", context.Candidate("prep-kokushi")!.OptionId);
        }

        [Fact]
        public void SleeveBloomOfferWorksWithoutAnOrdinaryDecision()
        {
            AiSleeveBloomOfferFacts offer = FrozenFixtures.BloomOffer();
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: offer)));
            var candidates = new[]
            {
                FrozenFixtures.Candidate("bloom-first", false, AiPhase.OfferChoice, "sleeve_bloom"),
                FrozenFixtures.Candidate("bloom-second", true, AiPhase.OfferChoice, "sleeve_bloom")
            };

            var context = new AiDecisionContext(snapshot, candidates);

            Assert.Null(context.OrdinaryObservation);
            Assert.Empty(context.OrdinaryLegalOptions);
            Assert.Equal(AiPhase.OfferChoice, context.Phase);
            Assert.Equal("bloom-1", context.Stamp.OfferId);
            Assert.Equal("bloom-second", context.SafeDefault()!.OptionId);
            Assert.Equal("bloom-first", context.Candidate("bloom-first")!.OptionId);
        }

        [Fact]
        public void BeamSwapOfferWorksWithoutAnOrdinaryDecision()
        {
            AiBeamSwapOfferFacts offer = FrozenFixtures.BeamOffer();
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "beam-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    beamSwap: FrozenFixtures.BeamSwap(pendingOffer: offer)));
            var candidates = new[]
            {
                FrozenFixtures.Candidate("beam-swap", false, AiPhase.OfferChoice, "beam_swap"),
                FrozenFixtures.Candidate("beam-keep", true, AiPhase.OfferChoice, "beam_swap")
            };

            var context = new AiDecisionContext(snapshot, candidates);

            Assert.Null(context.OrdinaryObservation);
            Assert.Equal(AiPhase.OfferChoice, context.Phase);
            Assert.Equal("beam-keep", context.SafeDefault()!.OptionId);
        }

        [Fact]
        public void OffTurnOrdinaryActionHasNoOrdinaryDecisionButCarriesTheWindow()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction, windowId: "w1"),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        canApplyOffTurn: true,
                        offTurnWindowId: "w1")));
            var candidates = new[]
            {
                FrozenFixtures.Candidate("bloom-offturn", false, AiPhase.OrdinaryAction, "sleeve_bloom")
            };

            var context = new AiDecisionContext(snapshot, candidates);

            Assert.Null(context.OrdinaryObservation);
            Assert.Empty(context.OrdinaryLegalOptions);
            Assert.Equal("w1", context.Stamp.WindowId);
            Assert.Equal("w1", context.CheatView.SleeveBloom!.OffTurnWindowId);
            Assert.Equal("bloom-offturn", context.Candidate("bloom-offturn")!.OptionId);
        }

        [Fact]
        public void IdleHasNoDecisionNoCandidatesAndNoPrivateFacts()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    roundId: null,
                    decisionId: null,
                    phase: AiPhase.Idle,
                    deadlineMillis: null),
                takeover: true);
            var context = new AiDecisionContext(snapshot, Array.Empty<AiCandidate>());

            Assert.Equal(AiPhase.Idle, context.Phase);
            Assert.Null(context.RoundId);
            Assert.Null(context.DecisionId);
            Assert.Null(context.DeadlineMillis);
            Assert.Empty(context.Candidates);
            Assert.Null(context.Candidate("anything"));
            Assert.Null(context.SafeDefault());
            Assert.Null(context.OrdinaryObservation);
            Assert.False(context.CheatView.HasPrivateFacts);
            Assert.True(context.Takeover);
            Assert.True(((IList<AiCandidate>)context.Candidates).IsReadOnly);
            Assert.Throws<NotSupportedException>(() => ((IList<AiCandidate>)context.Candidates).Add(
                FrozenFixtures.Candidate("x")));
        }

        [Fact]
        public void ContextAllowsAnEmptyCandidateList()
        {
            var context = new AiDecisionContext(
                FrozenFixtures.Snapshot(FrozenFixtures.Stamp()),
                Array.Empty<AiCandidate>());

            Assert.Empty(context.Candidates);
            Assert.Null(context.SafeDefault());
        }

        [Fact]
        public void ContextRejectsDuplicateOptionId()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            Assert.Throws<ArgumentException>(() => new AiDecisionContext(
                snapshot,
                new[]
                {
                    FrozenFixtures.Candidate("dup"),
                    FrozenFixtures.Candidate("dup", true)
                }));
        }

        [Fact]
        public void ContextRejectsANullCandidateElement()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            Assert.Throws<ArgumentException>(() => new AiDecisionContext(
                snapshot,
                new AiCandidate[] { null! }));
        }

        [Fact]
        public void ContextAllowsCaseDistinctOptionIds()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            var context = new AiDecisionContext(
                snapshot,
                new[]
                {
                    FrozenFixtures.Candidate("Case"),
                    FrozenFixtures.Candidate("case", true)
                });

            Assert.Equal(2, context.Candidates.Count);
            Assert.Equal("Case", context.Candidate("Case")!.OptionId);
            Assert.Equal("case", context.Candidate("case")!.OptionId);
            Assert.Null(context.Candidate("CASE"));
        }

        [Fact]
        public void ContextLookupIsExactAndCaseSensitive()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            var context = new AiDecisionContext(
                snapshot,
                new[]
                {
                    FrozenFixtures.Candidate("opt-A"),
                    FrozenFixtures.Candidate("opt-a")
                });

            Assert.NotNull(context.Candidate("opt-A"));
            Assert.NotNull(context.Candidate("opt-a"));
            Assert.Null(context.Candidate("OPT-A"));
            Assert.Null(context.Candidate("OPT-a"));
            Assert.Null(context.Candidate("opt-A "));
        }

        [Fact]
        public void ContextNullLookupThrows()
        {
            var context = new AiDecisionContext(
                FrozenFixtures.Snapshot(FrozenFixtures.Stamp()),
                Array.Empty<AiCandidate>());

            Assert.Throws<ArgumentNullException>(() => context.Candidate(null!));
        }

        [Fact]
        public void SafeDefaultReturnsTheFirstFlaggedCandidate()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            var context = new AiDecisionContext(
                snapshot,
                new[]
                {
                    FrozenFixtures.Candidate("first"),
                    FrozenFixtures.Candidate("safe-1", true),
                    FrozenFixtures.Candidate("safe-2", true)
                });

            Assert.Equal("safe-1", context.SafeDefault()!.OptionId);
        }

        [Fact]
        public void SafeDefaultIsNullWhenNoCandidateIsFlagged()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            var context = new AiDecisionContext(
                snapshot,
                new[] { FrozenFixtures.Candidate("a"), FrozenFixtures.Candidate("b") });

            Assert.Null(context.SafeDefault());
        }

        [Fact]
        public void ContextCopiesCandidateListAndIgnoresLaterSourceMutation()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp());
            var source = new List<AiCandidate>
            {
                FrozenFixtures.Candidate("a"),
                FrozenFixtures.Candidate("b", true)
            };
            var context = new AiDecisionContext(snapshot, source);

            source.Clear();
            source.Add(FrozenFixtures.Candidate("later"));

            Assert.Equal(2, context.Candidates.Count);
            Assert.Equal("a", context.Candidates[0].OptionId);
            Assert.Equal("b", context.Candidates[1].OptionId);
            Assert.Null(context.Candidate("later"));
        }

        [Fact]
        public void ContextExposesTheFrozenIdentityAndPolicyInputs()
        {
            AiDecisionStamp stamp = FrozenFixtures.Stamp(
                phase: AiPhase.OrdinaryAction,
                offerId: null,
                windowId: "w1",
                deadlineMillis: 5000);
            AiCandidateLimits limits = FrozenFixtures.Limits();
            AiCheatPolicy policy = FrozenFixtures.Policy(true);
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                stamp,
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        canApplyOffTurn: true,
                        offTurnWindowId: "w1")),
                limits: limits,
                policy: policy,
                takeover: true);
            var context = new AiDecisionContext(snapshot, Array.Empty<AiCandidate>());

            Assert.Same(snapshot, context.Snapshot);
            Assert.Same(stamp, context.Stamp);
            Assert.Equal(FrozenFixtures.Seat, context.Seat);
            Assert.Equal(AiPhase.OrdinaryAction, context.Phase);
            Assert.Equal(3, context.ControlEpoch);
            Assert.Equal(5000, context.DeadlineMillis);
            Assert.Same(limits, context.Limits);
            Assert.Same(policy, context.Policy);
            Assert.True(context.Takeover);
            Assert.Equal("w1", context.Stamp.WindowId);
        }

        [Fact]
        public void StrategySeesTheContextAndReturnsOnlyAnOptionId()
        {
            var context = new AiDecisionContext(
                FrozenFixtures.Snapshot(FrozenFixtures.Stamp()),
                new[] { FrozenFixtures.Candidate("chosen") });
            var strategy = new ReturningStrategy("chosen");

            string? result = strategy.Choose(context);

            Assert.Equal("chosen", result);
            Assert.Same(context, strategy.Seen);
            Assert.Null(strategy.CheatPolicy);
        }

        [Fact]
        public void StrategyCanDeclareAPolicyAndReturnNullForTheSchedulerFallback()
        {
            var context = new AiDecisionContext(
                FrozenFixtures.Snapshot(FrozenFixtures.Stamp()),
                Array.Empty<AiCandidate>());
            AiCheatPolicy policy = FrozenFixtures.Policy(true);
            var strategy = new ReturningStrategy(null, policy);

            Assert.Null(strategy.Choose(context));
            Assert.Same(policy, strategy.CheatPolicy);
        }

        [Fact]
        public void ContextDoesNotExposeAWriteableCandidateBackingList()
        {
            var context = new AiDecisionContext(
                FrozenFixtures.Snapshot(FrozenFixtures.Stamp()),
                new[] { FrozenFixtures.Candidate("a") });

            Assert.IsNotType<List<AiCandidate>>(context.Candidates);
            Assert.IsNotType<AiCandidate[]>(context.Candidates);
            Assert.True(((IList<AiCandidate>)context.Candidates).IsReadOnly);
        }

        private sealed class ReturningStrategy : AiDecisionStrategy
        {
            private readonly string? _optionId;

            internal ReturningStrategy(string? optionId, AiCheatPolicy? policy = null)
            {
                _optionId = optionId;
                CheatPolicy = policy;
            }

            public AiCheatPolicy? CheatPolicy { get; }

            internal AiDecisionContext? Seen { get; private set; }

            public string? Choose(AiDecisionContext context)
            {
                Seen = context;
                return _optionId;
            }
        }
    }
}

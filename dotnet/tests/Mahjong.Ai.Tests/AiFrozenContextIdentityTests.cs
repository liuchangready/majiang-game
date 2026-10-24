using System;
using Mahjong.Ai;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Identity and structural-binding tests for <see cref="AiSeatSnapshot"/>. V4 trusted its
    /// projection: <c>PlayerDecision</c>, the four cheat views and the stamp could disagree
    /// and the snapshot still existed. V5 rejects every cross-wired field before freezing.
    /// The V4 lookup/copy rules themselves were probed against the compiled classes (see
    /// <see cref="AiFrozenContextTests"/>); this file covers the new structural gate.
    /// </summary>
    public class AiFrozenContextIdentityTests
    {
        [Fact]
        public void ForeignSeatOrdinaryOptionIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                new[] { FrozenFixtures.Option("foreign", seat: 2) }));
        }

        [Fact]
        public void DuplicateOrdinaryOptionIdIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                new[]
                {
                    FrozenFixtures.Option("same"),
                    FrozenFixtures.Option("same", LegalActionKind.Pass, null)
                }));
        }

        [Fact]
        public void CaseDifferentOrdinaryOptionIdsAreAllowed()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                new[]
                {
                    FrozenFixtures.Option("X"),
                    FrozenFixtures.Option("x", LegalActionKind.Pass, null)
                });

            Assert.Equal(2, snapshot.OrdinaryLegalOptions.Count);
            Assert.Equal("X", snapshot.OrdinaryLegalOptions[0].OptionId);
            Assert.Equal("x", snapshot.OrdinaryLegalOptions[1].OptionId);
        }

        [Fact]
        public void ObservationSeatMismatchIsRejected()
        {
            LegalActionOption[] snapshotActions = { FrozenFixtures.Option("opt") };
            LegalActionOption[] observationActions = { FrozenFixtures.Option("opt", seat: 2) };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", 2, "d1"), 3, 9, 5000, observationActions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), snapshotActions, observation: observation));
        }

        [Fact]
        public void ObservationRoundMismatchIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r2", FrozenFixtures.Seat, "d1"), 3, 9, 5000, actions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), actions, observation: observation));
        }

        [Fact]
        public void ObservationDecisionMismatchIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d2"), 3, 9, 5000, actions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), actions, observation: observation));
        }

        [Fact]
        public void ObservationControlEpochMismatchIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d1"), 4, 9, 5000, actions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), actions, observation: observation));
        }

        [Fact]
        public void ObservationStateVersionMismatchIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d1"), 3, 8, 5000, actions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), actions, observation: observation));
        }

        [Fact]
        public void ObservationDeadlineMismatchIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d1"), 3, 9, 6000, actions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), actions, observation: observation));
        }

        [Fact]
        public void ObservationCandidateListMismatchIsRejected()
        {
            LegalActionOption[] snapshotOptions = { FrozenFixtures.Option("opt") };
            LegalActionOption[] observationOptions =
            {
                FrozenFixtures.Option("opt", LegalActionKind.Pass, null)
            };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d1"), 3, 9, 5000, observationOptions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(), snapshotOptions, observation: observation));
        }

        [Fact]
        public void PreparationMissingInPreparationPhaseIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.ShufflePreparation,
                    decisionId: "prep-r1-1",
                    deadlineMillis: 5000)));
        }

        [Fact]
        public void ConfirmedPreparationInPreparationPhaseIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.ShufflePreparation,
                    decisionId: "prep-r1-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(preparation: FrozenFixtures.Preparation(confirmed: true))));
        }

        [Fact]
        public void PreparationRoundMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.ShufflePreparation,
                    decisionId: "prep-r1-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(preparation: FrozenFixtures.Preparation(roundId: "r2"))));
        }

        [Fact]
        public void PreparationDeadlineMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.ShufflePreparation,
                    decisionId: "prep-r1-1",
                    deadlineMillis: 6000),
                cheat: FrozenFixtures.CheatView(preparation: FrozenFixtures.Preparation())));
        }

        [Fact]
        public void UnconfirmedPreparationOutsidePreparationPhaseIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(preparation: FrozenFixtures.Preparation(confirmed: false))));
        }

        [Fact]
        public void PreparationPhaseWithOrdinaryOptionsIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.ShufflePreparation,
                    decisionId: "prep-r1-1",
                    deadlineMillis: 5000),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(preparation: FrozenFixtures.Preparation())));
        }

        [Fact]
        public void IdleWithOrdinaryOptionsIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    roundId: null,
                    decisionId: null,
                    phase: AiPhase.Idle,
                    deadlineMillis: null),
                options: new[] { FrozenFixtures.Option("opt") }));
        }

        [Fact]
        public void IdleWithPendingOfferIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    roundId: null,
                    decisionId: null,
                    phase: AiPhase.Idle,
                    deadlineMillis: null),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer()))));
        }

        [Fact]
        public void IdleWithDeadlineIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    roundId: null,
                    decisionId: null,
                    phase: AiPhase.Idle,
                    deadlineMillis: 1)));
        }

        [Fact]
        public void OrdinaryActionWithPendingOfferIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer()))));
        }

        [Fact]
        public void OfferChoiceWithoutAnOfferIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    deadlineMillis: 5000)));
        }

        [Fact]
        public void OfferChoiceWithTwoPendingOffersIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer()),
                    beamSwap: FrozenFixtures.BeamSwap(pendingOffer: FrozenFixtures.BeamOffer()))));
        }

        [Fact]
        public void StampOfferIdMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "other",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer()))));
        }

        [Fact]
        public void SleeveBloomViewDecisionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        decisionId: "d0",
                        pendingOffer: FrozenFixtures.BloomOffer(decisionId: "d0")))));
        }

        [Fact]
        public void SleeveBloomViewControlEpochMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        controlEpoch: 4,
                        pendingOffer: FrozenFixtures.BloomOffer(controlEpoch: 4)))));
        }

        [Fact]
        public void SleeveBloomOfferDeadlineMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        pendingOffer: FrozenFixtures.BloomOffer(deadlineMillis: 6000)))));
        }

        [Fact]
        public void SleeveBloomOfferWindowMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    windowId: "w2",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        pendingOffer: FrozenFixtures.BloomOffer(
                            mode: AiSleeveBloomOfferMode.OffTurn,
                            windowId: "w1"),
                        offTurnWindowId: "w1"))));
        }

        [Fact]
        public void SleeveBloomOfferRequiresMatchingStampWindow()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    windowId: null,
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        pendingOffer: FrozenFixtures.BloomOffer(
                            mode: AiSleeveBloomOfferMode.OffTurn,
                            windowId: "w1"),
                        offTurnWindowId: "w1"))));
        }

        [Fact]
        public void SleeveBloomStateVersionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(stateVersion: 8))));
        }

        [Fact]
        public void TileMarkControlEpochMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(tileMark: FrozenFixtures.TileMark(controlEpoch: 4))));
        }

        [Fact]
        public void TileMarkStateVersionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(tileMark: FrozenFixtures.TileMark(stateVersion: 8))));
        }

        [Fact]
        public void TileMarkDecisionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(tileMark: FrozenFixtures.TileMark(decisionId: "d0"))));
        }

        [Fact]
        public void BeamSwapStateVersionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(beamSwap: FrozenFixtures.BeamSwap(stateVersion: 8))));
        }

        [Fact]
        public void BeamSwapControlEpochMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(beamSwap: FrozenFixtures.BeamSwap(controlEpoch: 4))));
        }

        [Fact]
        public void BeamSwapDecisionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                cheat: FrozenFixtures.CheatView(
                    beamSwap: FrozenFixtures.BeamSwap(
                        decisionId: "d0",
                        pendingOffer: FrozenFixtures.BeamOffer(decisionId: "d0")))));
        }

        [Fact]
        public void BeamSwapOfferIdMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "other",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    beamSwap: FrozenFixtures.BeamSwap(
                        pendingOffer: FrozenFixtures.BeamOffer()))));
        }

        [Fact]
        public void BeamSwapOfferDeadlineMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "beam-1",
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    beamSwap: FrozenFixtures.BeamSwap(
                        pendingOffer: FrozenFixtures.BeamOffer(deadlineMillis: 6000)))));
        }

        [Fact]
        public void OrdinaryActionWithStampOfferIdIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction, offerId: "o")));
        }

        [Fact]
        public void IdleWithObservationIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d1"), 3, 9, 5000, actions);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    roundId: null,
                    decisionId: null,
                    phase: AiPhase.Idle,
                    deadlineMillis: null),
                observation: observation));
        }

        [Fact]
        public void OfferChoiceRequiresStampOfferAndDeadline()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: null,
                    deadlineMillis: 5000),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer()))));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    deadlineMillis: null),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer()))));
        }

        [Fact]
        public void UnknownAiPhaseIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: (AiPhase)9)));
        }
    }
}

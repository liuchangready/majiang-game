using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Ai;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Structural and defensive-copy tests for the ENG-017 context baseline:
    /// <see cref="AiCheatView"/> and its small fact types, plus
    /// <see cref="AiSeatSnapshot"/>. V4's <c>AiCheatView.deepCopy</c> and
    /// <c>AiSeatSnapshot.deepCopy</c> were probed against the real compiled V4 classes
    /// (<c>D:/majiang/engine/build/classes/kotlin/main</c>): the empty view deep-copies to a
    /// distinct instance with all four nested views still null, and the V4 context copies its
    /// candidate list. This file ports those observable rules and adds the V5 requirement
    /// that every nested collection is copied and exposed only through a read-only view.
    /// </summary>
    public class AiSeatSnapshotTests
    {
        [Fact]
        public void CheatViewDefaultsAreEmptyAndZeroPoints()
        {
            var view = new AiCheatView();

            Assert.Equal(0, view.CheatPoints);
            Assert.Null(view.Preparation);
            Assert.Null(view.TileMark);
            Assert.Null(view.SleeveBloom);
            Assert.Null(view.BeamSwap);
            Assert.False(view.HasPrivateFacts);
        }

        [Fact]
        public void CheatViewRejectsNegativePoints()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiCheatView(-1));
        }

        [Fact]
        public void CheatViewDeepCopyIsIndependentAndPreservesFacts()
        {
            var preparation = FrozenFixtures.Preparation();
            var mark = FrozenFixtures.TileMark();
            var bloom = FrozenFixtures.SleeveBloom();
            var beam = FrozenFixtures.BeamSwap();
            var view = new AiCheatView(23, preparation, mark, bloom, beam);

            AiCheatView copy = view.DeepCopy();

            Assert.NotSame(view, copy);
            Assert.Equal(23, copy.CheatPoints);
            Assert.NotSame(preparation, copy.Preparation);
            Assert.NotSame(mark, copy.TileMark);
            Assert.NotSame(bloom, copy.SleeveBloom);
            Assert.NotSame(beam, copy.BeamSwap);
            Assert.Equal(preparation.RoundId, copy.Preparation!.RoundId);
            Assert.Equal(mark.DecisionId, copy.TileMark!.DecisionId);
            Assert.Equal(bloom.DecisionId, copy.SleeveBloom!.DecisionId);
            Assert.Equal(beam.DecisionId, copy.BeamSwap!.DecisionId);
        }

        [Fact]
        public void PreparationFactsDeepCopyAndSourceMutationDoNotLeak()
        {
            var selection = new AiShuffleSelectionFacts(AiShuffleTarget.Kokushi, true);
            var facts = new AiShufflePreparationFacts(
                "r1",
                AiShufflePreparationStatus.Selecting,
                1000,
                5000,
                2000,
                false,
                selection,
                30,
                70);

            selection = new AiShuffleSelectionFacts(AiShuffleTarget.Daisangen, false);
            AiShufflePreparationFacts copy = facts.DeepCopy();

            Assert.NotSame(facts, copy);
            Assert.NotSame(facts.Selection, copy.Selection);
            Assert.Equal(AiShuffleTarget.Kokushi, facts.Selection!.Target);
            Assert.True(facts.Selection.SeeThrough);
            Assert.Equal(40, facts.Selection.Cost);
            Assert.Equal(AiShuffleTarget.Daisangen, selection.Target);
            Assert.Equal("r1", copy.RoundId);
        }

        [Fact]
        public void PreparationFactsValidateTimesAndFixedCost()
        {
            Assert.Throws<ArgumentException>(() => new AiShufflePreparationFacts(
                " ", AiShufflePreparationStatus.Selecting, 1, 2, 1, false, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", AiShufflePreparationStatus.Selecting, -1, 2, 1, false, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", AiShufflePreparationStatus.Selecting, 10, 9, 1, false, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", AiShufflePreparationStatus.Selecting, 1, 2, -1, false, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", AiShufflePreparationStatus.Selecting, 1, 2, 1, false, null, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", AiShufflePreparationStatus.Selecting, 1, 2, 1, false, null, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", AiShufflePreparationStatus.Selecting, 1, 2, 1, false, null, 0, 0, false, 9));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiShufflePreparationFacts(
                "r1", (AiShufflePreparationStatus)9, 1, 2, 1, false, null, 0, 0));
        }

        [Fact]
        public void FactTextUsesKotlinBlankSemantics()
        {
            string fileSeparator = ((char)0x001C).ToString();
            string nextLine = ((char)0x0085).ToString();

            Assert.Throws<ArgumentException>(() => new AiShufflePreparationFacts(
                fileSeparator,
                AiShufflePreparationStatus.Selecting,
                1,
                1,
                1,
                false,
                null,
                0,
                0));

            var accepted = new AiShufflePreparationFacts(
                nextLine,
                AiShufflePreparationStatus.Selecting,
                1,
                1,
                1,
                false,
                null,
                0,
                0);
            Assert.Equal(nextLine, accepted.RoundId);
        }

        [Fact]
        public void TileMarkFactsCopyNestedCollectionsAndSourceMutationDoesNotLeak()
        {
            var selectable = new List<int> { 1, 2 };
            var groupTiles = new List<AiMarkedTileFacts>
            {
                new AiMarkedTileFacts(10, AiTileMarkArea.OwnHand, 1),
                new AiMarkedTileFacts(11, AiTileMarkArea.OwnHand, 1),
                new AiMarkedTileFacts(12, AiTileMarkArea.OwnHand, 1),
                new AiMarkedTileFacts(13, AiTileMarkArea.OwnHand, 1)
            };
            var groups = new List<AiTileMarkGroupFacts>
            {
                new AiTileMarkGroupFacts("g1", "r1", 1, 4, 2, false, groupTiles)
            };
            var mark = new AiTileMarkFacts(
                true,
                null,
                10,
                4,
                12,
                selectable,
                "d1",
                3,
                9,
                groups);

            selectable.Add(99);
            selectable.Clear();
            groupTiles.Clear();
            groups.Clear();

            Assert.Equal(new[] { 1, 2 }, mark.SelectableTileIds);
            Assert.Single(mark.ActiveGroups);
            Assert.Equal(4, mark.ActiveGroups[0].Tiles.Count);
            Assert.Equal(10, mark.ActiveGroups[0].Tiles[0].TileId);
            Assert.NotSame(groupTiles, mark.ActiveGroups[0].Tiles);
        }

        [Fact]
        public void TileMarkFactsCollectionsAreReadOnly()
        {
            AiTileMarkFacts mark = FrozenFixtures.TileMark();

            Assert.True(((IList<int>)mark.SelectableTileIds).IsReadOnly);
            Assert.True(((IList<AiTileMarkGroupFacts>)mark.ActiveGroups).IsReadOnly);
            Assert.Throws<NotSupportedException>(() => ((IList<int>)mark.SelectableTileIds).Add(5));
            Assert.Throws<NotSupportedException>(() => ((IList<AiTileMarkGroupFacts>)mark.ActiveGroups).Clear());
        }

        [Fact]
        public void TileMarkLimitsAndGroupShapeAreValidated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiTileMarkFacts(
                true, null, 0, 0, 12, Array.Empty<int>(), "d1", 0, 0, Array.Empty<AiTileMarkGroupFacts>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiTileMarkFacts(
                true, null, 10, 0, 11, Array.Empty<int>(), "d1", 0, 0, Array.Empty<AiTileMarkGroupFacts>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiTileMarkFacts(
                true, null, 10, 13, 12, Array.Empty<int>(), "d1", 0, 0, Array.Empty<AiTileMarkGroupFacts>()));
            Assert.Throws<ArgumentException>(() => new AiTileMarkFacts(
                true, null, 10, 0, 12, new[] { 0, 0 }, "d1", 0, 0, Array.Empty<AiTileMarkGroupFacts>()));
            Assert.Throws<ArgumentException>(() => new AiTileMarkFacts(
                true, null, 10, 0, 12, Array.Empty<int>(), " ", 0, 0, Array.Empty<AiTileMarkGroupFacts>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiTileMarkFacts(
                true, null, 10, 0, 12, Array.Empty<int>(), "d1", -1, 0, Array.Empty<AiTileMarkGroupFacts>()));

            var oneGroup = new AiTileMarkGroupFacts("g", "r", 1, 4, 0, false, new[]
            {
                new AiMarkedTileFacts(10, AiTileMarkArea.OwnHand, 0),
                new AiMarkedTileFacts(11, AiTileMarkArea.OwnHand, 0),
                new AiMarkedTileFacts(12, AiTileMarkArea.OwnHand, 0),
                new AiMarkedTileFacts(13, AiTileMarkArea.OwnHand, 0)
            });
            Assert.Throws<ArgumentException>(() => new AiTileMarkFacts(
                true, null, 10, 0, 12, Array.Empty<int>(), "d1", 0, 0, new[] { oneGroup }));
            Assert.Throws<ArgumentException>(() => new AiTileMarkFacts(
                true, null, 10, 4, 12, new[] { 10 }, "d1", 0, 0, new[] { oneGroup }));
            Assert.Throws<ArgumentException>(() => new AiTileMarkFacts(
                true, null, 10, 8, 12, Array.Empty<int>(), "d1", 0, 0,
                new[] { oneGroup, oneGroup }));

            Assert.Throws<ArgumentOutOfRangeException>(() => new AiTileMarkGroupFacts(
                "g", "r", 1, 5, 0, false, new[]
                {
                    new AiMarkedTileFacts(0, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(1, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(2, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(3, AiTileMarkArea.OwnHand, 0)
                }));
            Assert.Throws<ArgumentException>(() => new AiTileMarkGroupFacts(
                "g", "r", 1, 4, 0, false, new[]
                {
                    new AiMarkedTileFacts(0, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(1, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(2, AiTileMarkArea.OwnHand, 0)
                }));
            Assert.Throws<ArgumentException>(() => new AiTileMarkGroupFacts(
                "g", "r", 1, 4, 0, false, new[]
                {
                    new AiMarkedTileFacts(0, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(0, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(2, AiTileMarkArea.OwnHand, 0),
                    new AiMarkedTileFacts(3, AiTileMarkArea.OwnHand, 0)
                }));
        }

        [Fact]
        public void MarkedTileAreaRulesAreEnforced()
        {
            var river = new AiRiverPositionFacts("r1", 1, 0);
            Assert.Throws<ArgumentNullException>(() => new AiMarkedTileFacts(
                1, AiTileMarkArea.Discard, 1, null));
            Assert.Throws<ArgumentException>(() => new AiMarkedTileFacts(
                1, AiTileMarkArea.Discard, 1, new AiRiverPositionFacts("r1", 2, 0)));
            Assert.Throws<ArgumentException>(() => new AiMarkedTileFacts(
                1, AiTileMarkArea.OwnHand, null));
            Assert.Throws<ArgumentException>(() => new AiMarkedTileFacts(
                1, AiTileMarkArea.ExposedIndicator, 1));
            Assert.Throws<ArgumentException>(() => new AiMarkedTileFacts(
                1, AiTileMarkArea.NotVisible, null, river));

            var valid = new AiMarkedTileFacts(1, AiTileMarkArea.Discard, 1, river);
            Assert.Equal(1, valid.Seat);
            Assert.Equal(0, valid.RiverPosition!.DiscardIndex);
            Assert.Equal(2, new AiMarkedTileFacts(2, AiTileMarkArea.ExposedIndicator).TileId);
        }

        [Fact]
        public void SleeveBloomOfferValidationRules()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(firstTile: 0, secondTile: 0));
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(drawKind: AiSleeveBloomDrawKind.DealExtra));
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(
                drawKind: AiSleeveBloomDrawKind.Rinshan,
                mode: AiSleeveBloomOfferMode.OffTurn,
                windowId: "w1"));
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(
                mode: AiSleeveBloomOfferMode.OffTurn,
                windowId: null));
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(
                mode: AiSleeveBloomOfferMode.Immediate,
                windowId: "w1"));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrozenFixtures.BloomOffer(deadlineMillis: -1));
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(offerId: " "));
            Assert.Throws<ArgumentException>(() => FrozenFixtures.BloomOffer(decisionId: " "));
        }

        [Fact]
        public void SleeveBloomFactsBindPendingOfferIdentity()
        {
            AiSleeveBloomOfferFacts offer = FrozenFixtures.BloomOffer();
            Assert.Throws<ArgumentException>(() => new AiSleeveBloomFacts(
                true, null, 10, AiSleeveBloomDrawKind.Normal, "d2", 3, 9, offer));
            Assert.Throws<ArgumentException>(() => new AiSleeveBloomFacts(
                true, null, 10, AiSleeveBloomDrawKind.Normal, "d1", 4, 9, offer));
            Assert.Throws<ArgumentException>(() => new AiSleeveBloomFacts(
                true, null, 10, AiSleeveBloomDrawKind.Rinshan, "d1", 3, 9, offer));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSleeveBloomFacts(
                true, null, 9, null, "d1", 3, 9));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSleeveBloomFacts(
                true, null, 10, (AiSleeveBloomDrawKind)9, "d1", 3, 9));

            var valid = new AiSleeveBloomFacts(
                true, null, 10, AiSleeveBloomDrawKind.Normal, "d1", 3, 9, offer);
            Assert.NotNull(valid.PendingOffer);
            Assert.NotSame(offer, valid.PendingOffer);
        }

        [Fact]
        public void BeamSwapOfferCopiesCandidatesAndHandTiles()
        {
            var candidates = new List<AiBeamSwapCandidateFacts>
            {
                new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0), true, 0),
                new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.DeadWallSlot, 1), false)
            };
            var hand = new List<int> { 10, 11 };
            var offer = new AiBeamSwapOfferFacts(
                "beam-1",
                AiBeamSwapWindow.Wall,
                candidates,
                hand,
                5000,
                "d1",
                3);

            candidates.Clear();
            hand.Clear();

            Assert.Equal(2, offer.Candidates.Count);
            Assert.Equal(new[] { 10, 11 }, offer.SelectableHandTileIds);
            Assert.True(((IList<AiBeamSwapCandidateFacts>)offer.Candidates).IsReadOnly);
            Assert.True(((IList<int>)offer.SelectableHandTileIds).IsReadOnly);
            Assert.Throws<NotSupportedException>(() => ((IList<int>)offer.SelectableHandTileIds).Add(12));
        }

        [Fact]
        public void BeamSwapOfferRejectsInvalidCandidateAndHandShapes()
        {
            Assert.Throws<ArgumentException>(() => new AiBeamSwapOfferFacts(
                "b", AiBeamSwapWindow.Wall, Array.Empty<AiBeamSwapCandidateFacts>(), new[] { 1 }, 1, "d1", 0));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapOfferFacts(
                "b", AiBeamSwapWindow.Wall,
                new[] { new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0), false) },
                Array.Empty<int>(), 1, "d1", 0));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapOfferFacts(
                "b", AiBeamSwapWindow.Wall,
                new[]
                {
                    new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0), false),
                    new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0), false)
                },
                new[] { 1 }, 1, "d1", 0));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapOfferFacts(
                "b", AiBeamSwapWindow.Wall,
                new[]
                {
                    new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0), true, 5),
                    new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.DeadWallSlot, 0), true, 5)
                },
                new[] { 1 }, 1, "d1", 0));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapOfferFacts(
                "b", AiBeamSwapWindow.Wall,
                new[] { new AiBeamSwapCandidateFacts(new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0), false) },
                new[] { 1, 1 }, 1, "d1", 0));
        }

        [Fact]
        public void BeamSwapCandidateMarkedRule()
        {
            var slot = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.ReplacementSlot, 2);
            Assert.Throws<ArgumentException>(() => new AiBeamSwapCandidateFacts(slot, true));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapCandidateFacts(slot, false, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiBeamSwapCandidateFacts(slot, true, 136));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiBeamSwapSlotFacts(
                AiBeamSwapSlotKind.WallSlot, -1));

            var marked = new AiBeamSwapCandidateFacts(slot, true, 5);
            Assert.True(marked.Marked);
            Assert.Equal(5, marked.TileId);
        }

        [Fact]
        public void BeamSwapFactsBindPendingOfferIdentity()
        {
            AiBeamSwapOfferFacts offer = FrozenFixtures.BeamOffer();
            Assert.Throws<ArgumentException>(() => new AiBeamSwapFacts(
                true, null, 10, AiBeamSwapWindow.Wall, "d2", 3, 9, offer));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapFacts(
                true, null, 10, AiBeamSwapWindow.Wall, "d1", 4, 9, offer));
            Assert.Throws<ArgumentException>(() => new AiBeamSwapFacts(
                true, null, 10, AiBeamSwapWindow.DeadWall, "d1", 3, 9, offer));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiBeamSwapFacts(
                true, null, 9, null, "d1", 3, 9));

            var valid = new AiBeamSwapFacts(
                true, null, 10, AiBeamSwapWindow.Wall, "d1", 3, 9, offer);
            Assert.NotSame(offer, valid.PendingOffer);
        }

        [Fact]
        public void SnapshotCopiesOrdinaryOptionsAndCheatView()
        {
            var options = new List<LegalActionOption>
            {
                FrozenFixtures.Option("opt-A"),
                FrozenFixtures.Option("opt-b", LegalActionKind.Pass, null)
            };
            AiCheatView cheat = FrozenFixtures.CheatView(
                preparation: FrozenFixtures.Preparation(confirmed: true));
            var snapshot = new AiSeatSnapshot(
                FrozenFixtures.Stamp(),
                options,
                cheat,
                FrozenFixtures.Limits(),
                FrozenFixtures.Policy(),
                false);

            options.Clear();
            Assert.Equal(2, snapshot.OrdinaryLegalOptions.Count);
            Assert.Equal("opt-A", snapshot.OrdinaryLegalOptions[0].OptionId);
            Assert.NotSame(cheat, snapshot.CheatView);
            Assert.NotSame(cheat.Preparation, snapshot.CheatView.Preparation);
            Assert.True(((IList<LegalActionOption>)snapshot.OrdinaryLegalOptions).IsReadOnly);
        }

        [Fact]
        public void SnapshotWithoutPrivateFactsHasNullFactsAndEmptyOptions()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp(
                phase: AiPhase.Idle,
                roundId: null,
                decisionId: null,
                deadlineMillis: null));

            Assert.Empty(snapshot.OrdinaryLegalOptions);
            Assert.Null(snapshot.OrdinaryObservation);
            Assert.False(snapshot.CheatView.HasPrivateFacts);
            Assert.Null(snapshot.CheatView.Preparation);
            Assert.Null(snapshot.CheatView.TileMark);
            Assert.Null(snapshot.CheatView.SleeveBloom);
            Assert.Null(snapshot.CheatView.BeamSwap);
        }

        [Fact]
        public void SnapshotDeepCopyDoesNotAliasMutableInputs()
        {
            var options = new List<LegalActionOption> { FrozenFixtures.Option("x") };
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp(), options);
            options.Clear();

            AiSeatSnapshot copy = snapshot.DeepCopy();

            Assert.NotSame(snapshot, copy);
            Assert.Single(copy.OrdinaryLegalOptions);
            Assert.Equal("x", copy.OrdinaryLegalOptions[0].OptionId);
        }

        [Fact]
        public void SnapshotRejectsNullArguments()
        {
            AiDecisionStamp stamp = FrozenFixtures.Stamp();
            var options = Array.Empty<LegalActionOption>();
            AiCheatView cheat = new AiCheatView();
            AiCandidateLimits limits = FrozenFixtures.Limits();
            AiCheatPolicy policy = FrozenFixtures.Policy();

            Assert.Throws<ArgumentNullException>(() => new AiSeatSnapshot(
                null!, options, cheat, limits, policy, false));
            Assert.Throws<ArgumentNullException>(() => new AiSeatSnapshot(
                stamp, null!, cheat, limits, policy, false));
            Assert.Throws<ArgumentNullException>(() => new AiSeatSnapshot(
                stamp, options, null!, limits, policy, false));
            Assert.Throws<ArgumentNullException>(() => new AiSeatSnapshot(
                stamp, options, cheat, null!, policy, false));
            Assert.Throws<ArgumentNullException>(() => new AiSeatSnapshot(
                stamp, options, cheat, limits, null!, false));
            Assert.Throws<ArgumentNullException>(() => new AiDecisionContext(null!, Array.Empty<AiCandidate>()));
            Assert.Throws<ArgumentNullException>(() => new AiDecisionContext(
                FrozenFixtures.Snapshot(FrozenFixtures.Stamp(
                    phase: AiPhase.Idle,
                    roundId: null,
                    decisionId: null,
                    deadlineMillis: null)),
                null!));
        }

        [Fact]
        public void SnapshotRejectsNullOrdinaryOption()
        {
            Assert.Throws<ArgumentException>(() => new AiSeatSnapshot(
                FrozenFixtures.Stamp(),
                new LegalActionOption[] { null! },
                new AiCheatView(),
                FrozenFixtures.Limits(),
                FrozenFixtures.Policy(),
                false));
        }

        [Fact]
        public void PublicFactCollectionsCannotBeCastToWritableCollections()
        {
            AiTileMarkFacts mark = FrozenFixtures.TileMark();
            AiBeamSwapOfferFacts offer = FrozenFixtures.BeamOffer();

            Assert.IsNotType<List<int>>(mark.SelectableTileIds);
            Assert.IsNotType<List<AiTileMarkGroupFacts>>(mark.ActiveGroups);
            Assert.IsNotType<List<AiBeamSwapCandidateFacts>>(offer.Candidates);
            Assert.IsNotType<List<int>>(offer.SelectableHandTileIds);
            Assert.IsNotType<int[]>(offer.SelectableHandTileIds);
            Assert.True(((IList<int>)offer.SelectableHandTileIds).IsReadOnly);
        }
    }

    /// <summary>
    /// Shared fixture builders. They construct only the public AI contract; no domain state,
    /// authority object or mutable projection is reachable from them.
    /// </summary>
    internal static class FrozenFixtures
    {
        internal const int Seat = 1;

        internal static AiPrivateFactScope Scope(
            int seat = Seat,
            string sessionId = "s1",
            string? roundId = "r1")
        {
            return new AiPrivateFactScope(seat, sessionId, roundId);
        }

        internal static AiSeatFacts SeatFacts(
            IReadOnlyList<TileId>? closed = null,
            TileId? drawn = null,
            int seat = Seat,
            string sessionId = "s1",
            string? roundId = "r1")
        {
            return new AiSeatFacts(
                Scope(seat, sessionId, roundId),
                closed ?? new[] { new TileId(1), new TileId(2) },
                drawn);
        }

        internal static TileId[] Tiles(params int[] ids)
        {
            var tiles = new TileId[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                tiles[i] = new TileId(ids[i]);
            }

            return tiles;
        }

        internal static AiDecisionStamp Stamp(
            int seat = Seat,
            string? roundId = "r1",
            string? decisionId = "d1",
            long controlEpoch = 3,
            AiPhase phase = AiPhase.OrdinaryAction,
            long stateVersion = 9,
            string? offerId = null,
            string? windowId = null,
            long? deadlineMillis = 5000,
            string sessionId = "s1")
        {
            return new AiDecisionStamp(
                seat,
                sessionId,
                roundId,
                decisionId,
                controlEpoch,
                phase,
                stateVersion,
                offerId,
                windowId,
                deadlineMillis);
        }

        internal static AiDecisionStamp OffTurnStamp(
            int seat = Seat,
            string? roundId = "r1",
            string? decisionId = "d1",
            long controlEpoch = 3,
            long stateVersion = 9,
            string sessionId = "s1")
        {
            return Stamp(
                seat,
                roundId,
                decisionId,
                controlEpoch,
                AiPhase.OrdinaryAction,
                stateVersion,
                null,
                "w1",
                5000,
                sessionId);
        }

        internal static AiSeatSnapshot OffTurnSnapshot(AiSeatFacts? seatFacts = null)
        {
            return Snapshot(
                OffTurnStamp(),
                cheat: CheatView(
                    sleeveBloom: SleeveBloom(
                        canApplyOffTurn: true,
                        offTurnWindowId: "w1")),
                seatFacts: seatFacts);
        }

        internal static AiCandidate Candidate(
            string optionId,
            bool safeDefault = false,
            AiPhase phase = AiPhase.OrdinaryAction,
            string capabilityId = "mahjong",
            int cost = 0)
        {
            return new AiCandidate(
                optionId,
                capabilityId,
                phase,
                cost,
                new AiRisk(),
                new AiEffect(),
                true,
                null,
                safeDefault);
        }

        internal static AiCandidateLimits Limits()
        {
            return new AiCandidateLimits(
                Array.Empty<string>(),
                60,
                40,
                0,
                6,
                0.5);
        }

        internal static AiCheatPolicy Policy(bool enabled = false)
        {
            return new AiCheatPolicy(enabled);
        }

        internal static LegalActionOption Option(
            string? optionId,
            LegalActionKind kind = LegalActionKind.Discard,
            int? tileId = 20,
            int seat = Seat)
        {
            TileId? tile = tileId.HasValue ? new TileId(tileId.Value) : (TileId?)null;
            return new LegalActionOption(kind, seat, tile, optionId);
        }

        internal static AiObservation Observation(
            DecisionKey key,
            long controlEpoch,
            long stateVersion,
            long deadlineMillis,
            IReadOnlyList<LegalActionOption> actions,
            string sessionId = "s1",
            IReadOnlyList<TileId>? hand = null,
            TileId? drawn = null)
        {
            return new AiObservation(
                sessionId,
                key,
                controlEpoch,
                stateVersion,
                deadlineMillis,
                RoundPhase.Discard,
                0,
                1,
                1,
                0,
                0,
                70,
                hand ?? new[] { new TileId(0) },
                drawn,
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleDiscard>(),
                Array.Empty<TileId>(),
                new long[] { 25000, 25000, 25000, 25000 },
                actions);
        }

        internal static AiShufflePreparationFacts Preparation(
            string roundId = "r1",
            bool confirmed = false,
            long opensAtMillis = 1000,
            long deadlineMillis = 5000,
            long serverNowMillis = 2000,
            AiShuffleSelectionFacts? selection = null,
            int reservedPoints = 0,
            int availablePoints = 40)
        {
            return new AiShufflePreparationFacts(
                roundId,
                AiShufflePreparationStatus.Selecting,
                opensAtMillis,
                deadlineMillis,
                serverNowMillis,
                confirmed,
                selection,
                reservedPoints,
                availablePoints);
        }

        internal static AiShuffleSelectionFacts Selection(AiShuffleTarget target = AiShuffleTarget.Kokushi)
        {
            return new AiShuffleSelectionFacts(target);
        }

        internal static AiTileMarkFacts TileMark(
            string decisionId = "d1",
            long controlEpoch = 3,
            long stateVersion = 9,
            IReadOnlyList<int>? selectableTileIds = null,
            IReadOnlyList<AiTileMarkGroupFacts>? activeGroups = null)
        {
            var groups = activeGroups ?? Array.Empty<AiTileMarkGroupFacts>();
            return new AiTileMarkFacts(
                true,
                null,
                10,
                groups.Count * 4,
                12,
                selectableTileIds ?? (groups.Count == 0 ? new[] { 1, 2 } : Array.Empty<int>()),
                decisionId,
                controlEpoch,
                stateVersion,
                groups);
        }

        internal static AiTileMarkGroupFacts MarkGroup(
            string createdRoundId = "r1",
            int ownerSeat = Seat,
            params int[] tileIds)
        {
            if (tileIds.Length != 4)
            {
                throw new ArgumentException("A mark group fixture needs four tiles.", nameof(tileIds));
            }

            var marked = new AiMarkedTileFacts[4];
            for (int i = 0; i < 4; i++)
            {
                marked[i] = new AiMarkedTileFacts(tileIds[i], AiTileMarkArea.OwnHand, ownerSeat);
            }

            return new AiTileMarkGroupFacts("g1", createdRoundId, 1, 4, 2, false, marked);
        }

        internal static AiSleeveBloomOfferFacts BloomOffer(
            string offerId = "bloom-1",
            int firstTile = 0,
            int secondTile = 1,
            AiSleeveBloomDrawKind drawKind = AiSleeveBloomDrawKind.Normal,
            long deadlineMillis = 5000,
            string decisionId = "d1",
            long controlEpoch = 3,
            AiSleeveBloomOfferMode mode = AiSleeveBloomOfferMode.Immediate,
            string? windowId = null)
        {
            return new AiSleeveBloomOfferFacts(
                offerId,
                firstTile,
                secondTile,
                drawKind,
                deadlineMillis,
                decisionId,
                controlEpoch,
                mode,
                windowId);
        }

        internal static AiSleeveBloomFacts SleeveBloom(
            string decisionId = "d1",
            long controlEpoch = 3,
            long stateVersion = 9,
            AiSleeveBloomOfferFacts? pendingOffer = null,
            bool canApplyOffTurn = false,
            string? offTurnWindowId = null,
            AiSleeveBloomDrawKind? drawKind = AiSleeveBloomDrawKind.Normal)
        {
            return new AiSleeveBloomFacts(
                true,
                null,
                10,
                drawKind,
                decisionId,
                controlEpoch,
                stateVersion,
                pendingOffer,
                canApplyOffTurn,
                null,
                offTurnWindowId);
        }

        internal static AiBeamSwapOfferFacts BeamOffer(
            string offerId = "beam-1",
            string decisionId = "d1",
            long controlEpoch = 3,
            long deadlineMillis = 5000,
            AiBeamSwapWindow window = AiBeamSwapWindow.Wall)
        {
            return new AiBeamSwapOfferFacts(
                offerId,
                window,
                new[]
                {
                    new AiBeamSwapCandidateFacts(
                        new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0),
                        true,
                        0)
                },
                new[] { 1, 2 },
                deadlineMillis,
                decisionId,
                controlEpoch);
        }

        internal static AiBeamSwapFacts BeamSwap(
            string decisionId = "d1",
            long controlEpoch = 3,
            long stateVersion = 9,
            AiBeamSwapOfferFacts? pendingOffer = null,
            AiBeamSwapWindow? window = AiBeamSwapWindow.Wall)
        {
            return new AiBeamSwapFacts(
                true,
                null,
                10,
                window,
                decisionId,
                controlEpoch,
                stateVersion,
                pendingOffer);
        }

        internal static AiCheatView CheatView(
            int points = 12,
            AiShufflePreparationFacts? preparation = null,
            AiTileMarkFacts? tileMark = null,
            AiSleeveBloomFacts? sleeveBloom = null,
            AiBeamSwapFacts? beamSwap = null,
            AiPrivateFactScope? scope = null)
        {
            return new AiCheatView(
                points,
                preparation,
                tileMark,
                sleeveBloom,
                beamSwap,
                scope ?? Scope());
        }

        internal static AiSeatSnapshot Snapshot(
            AiDecisionStamp stamp,
            IReadOnlyList<LegalActionOption>? options = null,
            AiCheatView? cheat = null,
            AiObservation? observation = null,
            AiCandidateLimits? limits = null,
            AiCheatPolicy? policy = null,
            bool takeover = false,
            AiSeatFacts? seatFacts = null)
        {
            return new AiSeatSnapshot(
                stamp,
                options ?? Array.Empty<LegalActionOption>(),
                cheat ?? new AiCheatView(),
                limits ?? Limits(),
                policy ?? Policy(),
                takeover,
                observation,
                seatFacts);
        }
    }
}

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Identity and structural-binding tests for <see cref="AiSeatSnapshot"/>. V4 trusted its
    /// projection: <c>PlayerDecision</c>, the four cheat views and the stamp could disagree
    /// and the snapshot still existed. V5 rejects every cross-wired field before freezing.
    /// The V4 lookup/copy rules themselves were probed against the compiled classes (see
    /// <see cref="AiSeatSnapshotTests"/>); this file covers the new structural gate.
    /// </summary>
    public class AiSeatSnapshotIdentityTests
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
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer())),
                seatFacts: FrozenFixtures.SeatFacts(closed: Array.Empty<TileId>(), roundId: null)));
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
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer())),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                    beamSwap: FrozenFixtures.BeamSwap(pendingOffer: FrozenFixtures.BeamOffer())),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer())),
                seatFacts: FrozenFixtures.SeatFacts()));
        }

        [Fact]
        public void SleeveBloomViewDecisionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        decisionId: "d0",
                        pendingOffer: FrozenFixtures.BloomOffer(decisionId: "d0"))),
                seatFacts: FrozenFixtures.SeatFacts()));
        }

        [Fact]
        public void SleeveBloomViewControlEpochMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(
                        controlEpoch: 4,
                        pendingOffer: FrozenFixtures.BloomOffer(controlEpoch: 4))),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                        pendingOffer: FrozenFixtures.BloomOffer(deadlineMillis: 6000))),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                        offTurnWindowId: "w1")),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                        offTurnWindowId: "w1")),
                seatFacts: FrozenFixtures.SeatFacts()));
        }

        [Fact]
        public void SleeveBloomStateVersionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(stateVersion: 8))));
        }

        [Fact]
        public void TileMarkControlEpochMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: FrozenFixtures.TileMark(controlEpoch: 4))));
        }

        [Fact]
        public void TileMarkStateVersionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: FrozenFixtures.TileMark(stateVersion: 8))));
        }

        [Fact]
        public void TileMarkDecisionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: FrozenFixtures.TileMark(decisionId: "d0"))));
        }

        [Fact]
        public void BeamSwapStateVersionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(beamSwap: FrozenFixtures.BeamSwap(stateVersion: 8))));
        }

        [Fact]
        public void BeamSwapControlEpochMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(beamSwap: FrozenFixtures.BeamSwap(controlEpoch: 4))));
        }

        [Fact]
        public void BeamSwapDecisionMismatchIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    beamSwap: FrozenFixtures.BeamSwap(
                        decisionId: "d0",
                        pendingOffer: FrozenFixtures.BeamOffer(decisionId: "d0"))),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                        pendingOffer: FrozenFixtures.BeamOffer())),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                        pendingOffer: FrozenFixtures.BeamOffer(deadlineMillis: 6000))),
                seatFacts: FrozenFixtures.SeatFacts()));
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
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer())),
                seatFacts: FrozenFixtures.SeatFacts()));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.OfferChoice,
                    offerId: "bloom-1",
                    deadlineMillis: null),
                cheat: FrozenFixtures.CheatView(
                    sleeveBloom: FrozenFixtures.SleeveBloom(pendingOffer: FrozenFixtures.BloomOffer())),
                seatFacts: FrozenFixtures.SeatFacts()));
        }

        [Fact]
        public void ObservationSessionMismatchIsRejected()
        {
            LegalActionOption[] actions = { FrozenFixtures.Option("opt") };
            AiObservation observation = FrozenFixtures.Observation(
                new DecisionKey("r1", FrozenFixtures.Seat, "d1"),
                3,
                9,
                5000,
                actions,
                sessionId: "FOREIGN-SESSION");

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(sessionId: "s1"),
                actions,
                observation: observation));
        }

        [Fact]
        public void PrivateFactsWithoutScopeAreRejected()
        {
            AiTileMarkFacts mark = FrozenFixtures.TileMark(
                activeGroups: new[] { FrozenFixtures.MarkGroup("r1", FrozenFixtures.Seat, 10, 11, 12, 13) });
            var cheat = new AiCheatView(23, tileMark: mark);

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: cheat));
        }

        [Fact]
        public void OwnHandMarkMustBelongToTheViewingSeat()
        {
            AiTileMarkGroupFacts group = FrozenFixtures.MarkGroup("r1", 0, 10, 11, 12, 13);
            AiTileMarkFacts mark = FrozenFixtures.TileMark(activeGroups: new[] { group });

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 1),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: mark)));
        }

        [Fact]
        public void OpponentHandMarkCannotPointAtTheViewingSeat()
        {
            var group = new AiTileMarkGroupFacts("g1", "r1", 1, 4, 2, false, new[]
            {
                new AiMarkedTileFacts(10, AiTileMarkArea.OpponentHand, 1),
                new AiMarkedTileFacts(11, AiTileMarkArea.OpponentHand, 2),
                new AiMarkedTileFacts(12, AiTileMarkArea.OpponentHand, 2),
                new AiMarkedTileFacts(13, AiTileMarkArea.OpponentHand, 2)
            });
            AiTileMarkFacts mark = FrozenFixtures.TileMark(activeGroups: new[] { group });

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 1),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: mark)));
        }

        [Fact]
        public void ReusedPrivateViewAtAnotherSeatIsRejected()
        {
            AiTileMarkFacts mark = FrozenFixtures.TileMark(
                activeGroups: new[] { FrozenFixtures.MarkGroup("r1", 0, 10, 11, 12, 13) });
            AiSeatSnapshot owner0 = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 0),
                options: new[] { FrozenFixtures.Option("opt", seat: 0) },
                cheat: FrozenFixtures.CheatView(
                    tileMark: mark,
                    scope: FrozenFixtures.Scope(seat: 0)));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 1),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: owner0.CheatView));
        }

        [Fact]
        public void ReusedPrivateViewAtAnotherSessionOrRoundIsRejected()
        {
            AiTileMarkFacts mark = FrozenFixtures.TileMark(
                activeGroups: new[] { FrozenFixtures.MarkGroup("r1", 0, 10, 11, 12, 13) });
            AiSeatSnapshot owner = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 0, sessionId: "s1", roundId: "r1"),
                options: new[] { FrozenFixtures.Option("opt", seat: 0) },
                cheat: FrozenFixtures.CheatView(
                    tileMark: mark,
                    scope: FrozenFixtures.Scope(seat: 0, sessionId: "s1", roundId: "r1")));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 0, sessionId: "s2", roundId: "r2"),
                options: new[] { FrozenFixtures.Option("opt", seat: 0) },
                cheat: owner.CheatView));
        }

        [Fact]
        public void PrivateScopeSeatSessionAndRoundMismatchesAreRejected()
        {
            AiTileMarkFacts mark = FrozenFixtures.TileMark(
                activeGroups: new[] { FrozenFixtures.MarkGroup("r1", FrozenFixtures.Seat, 10, 11, 12, 13) });

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(seat: 1),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    tileMark: mark,
                    scope: FrozenFixtures.Scope(seat: 2))));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(sessionId: "s1"),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    tileMark: mark,
                    scope: FrozenFixtures.Scope(sessionId: "S1"))));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(roundId: "r1"),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    tileMark: mark,
                    scope: FrozenFixtures.Scope(roundId: "r2"))));
        }

        [Fact]
        public void PrivateScopeDeepCopyPreservesTheBinding()
        {
            AiCheatView view = FrozenFixtures.CheatView(
                tileMark: FrozenFixtures.TileMark(),
                scope: FrozenFixtures.Scope(seat: 2, sessionId: "S-2", roundId: "R-2"));
            AiCheatView copy = view.DeepCopy();

            Assert.NotSame(view.Scope, copy.Scope);
            Assert.Equal(2, copy.Scope!.Seat);
            Assert.Equal("S-2", copy.Scope.SessionId);
            Assert.Equal("R-2", copy.Scope.RoundId);
            Assert.Equal(2, copy.Scope.Seat);
            Assert.Equal("S-2", copy.Scope.SessionId);
            Assert.Equal("R-2", copy.Scope.RoundId);
        }

        [Fact]
        public void HistoricalMarkGroupIsAllowedUnderTheCurrentScope()
        {
            AiTileMarkGroupFacts group = FrozenFixtures.MarkGroup("r0", FrozenFixtures.Seat, 10, 11, 12, 13);
            AiTileMarkFacts mark = FrozenFixtures.TileMark(activeGroups: new[] { group });
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(
                    tileMark: mark,
                    scope: FrozenFixtures.Scope(roundId: "r1")));

            Assert.Single(snapshot.CheatView.TileMark!.ActiveGroups);
            Assert.Equal("r0", snapshot.CheatView.TileMark.ActiveGroups[0].CreatedRoundId);
        }

        [Fact]
        public void CurrentRoundOwnHandMarkMustBeInTheFrozenHand()
        {
            AiTileMarkGroupFacts group = FrozenFixtures.MarkGroup("r1", FrozenFixtures.Seat, 10, 11, 12, 13);
            AiTileMarkFacts mark = FrozenFixtures.TileMark(activeGroups: new[] { group });
            AiSeatFacts seatFacts = FrozenFixtures.SeatFacts(
                FrozenFixtures.Tiles(10, 11, 12, 14));

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: mark),
                seatFacts: seatFacts));
        }

        [Fact]
        public void DiscardMarkRiverMustUseTheCurrentRound()
        {
            var group = new AiTileMarkGroupFacts("g1", "r1", 1, 4, 2, false, new[]
            {
                new AiMarkedTileFacts(10, AiTileMarkArea.Discard, 1, new AiRiverPositionFacts("r0", 1, 0)),
                new AiMarkedTileFacts(11, AiTileMarkArea.Discard, 1, new AiRiverPositionFacts("r0", 1, 1)),
                new AiMarkedTileFacts(12, AiTileMarkArea.Discard, 1, new AiRiverPositionFacts("r0", 1, 2)),
                new AiMarkedTileFacts(13, AiTileMarkArea.Discard, 1, new AiRiverPositionFacts("r0", 1, 3))
            });
            AiTileMarkFacts mark = FrozenFixtures.TileMark(activeGroups: new[] { group });

            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(),
                options: new[] { FrozenFixtures.Option("opt") },
                cheat: FrozenFixtures.CheatView(tileMark: mark)));
        }

        [Fact]
        public void EmptyPrivateViewWithoutScopeIsAllowedInIdle()
        {
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.Idle,
                    roundId: null,
                    decisionId: null,
                    deadlineMillis: null),
                cheat: new AiCheatView());

            Assert.Null(snapshot.CheatView.Scope);
            Assert.False(snapshot.CheatView.HasPrivateFacts);
        }

        [Fact]
        public void IdleWithOrphanWindowIsRejected()
        {
            Assert.Throws<ArgumentException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(
                    phase: AiPhase.Idle,
                    roundId: null,
                    decisionId: null,
                    deadlineMillis: null,
                    windowId: "stale-w")));
        }

        [Fact]
        public void UnknownAiPhaseIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrozenFixtures.Snapshot(
                FrozenFixtures.Stamp(phase: (AiPhase)9)));
        }
    }
}

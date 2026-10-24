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
    public class AiFrozenContextTests
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
            AiSeatSnapshot snapshot = FrozenFixtures.Snapshot(FrozenFixtures.Stamp(phase: AiPhase.OrdinaryAction));

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
                FrozenFixtures.Snapshot(stamp), null!));
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
            string sessionId = "s1")
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
                new[] { new TileId(0) },
                null,
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
            return new AiTileMarkFacts(
                true,
                null,
                10,
                0,
                12,
                selectableTileIds ?? new[] { 1, 2 },
                decisionId,
                controlEpoch,
                stateVersion,
                activeGroups ?? Array.Empty<AiTileMarkGroupFacts>());
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
            AiBeamSwapFacts? beamSwap = null)
        {
            return new AiCheatView(points, preparation, tileMark, sleeveBloom, beamSwap);
        }

        internal static AiSeatSnapshot Snapshot(
            AiDecisionStamp stamp,
            IReadOnlyList<LegalActionOption>? options = null,
            AiCheatView? cheat = null,
            AiObservation? observation = null,
            AiCandidateLimits? limits = null,
            AiCheatPolicy? policy = null,
            bool takeover = false)
        {
            return new AiSeatSnapshot(
                stamp,
                options ?? Array.Empty<LegalActionOption>(),
                cheat ?? new AiCheatView(),
                limits ?? Limits(),
                policy ?? Policy(),
                takeover,
                observation);
        }
    }
}

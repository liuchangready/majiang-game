using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    public sealed class M103AiCandidateContractTests
    {
        [Fact]
        public void SleeveBloomOfferFactsSupportsOptionalThirdCandidateAndCopiesIt()
        {
            var offer = new AiSleeveBloomOfferFacts(
                "offer-3",
                10,
                20,
                AiSleeveBloomDrawKind.Normal,
                5000L,
                "dec-1",
                2L,
                thirdTileId: 30,
                thirdCanTsumo: true,
                thirdCanKan: false);

            Assert.Equal(30, offer.ThirdTileId);
            Assert.True(offer.ThirdCanTsumo);
            Assert.False(offer.ThirdCanKan);

            AiSleeveBloomOfferFacts copy = offer.DeepCopy();
            Assert.NotSame(offer, copy);
            Assert.Equal(30, copy.ThirdTileId);
            Assert.True(copy.ThirdCanTsumo);
            Assert.False(copy.ThirdCanKan);

            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSleeveBloomOfferFacts(
                "offer-bad", 10, 20, AiSleeveBloomDrawKind.Normal, 5000L, "dec-1", 2L, thirdTileId: 136));
            Assert.Throws<ArgumentException>(() => new AiSleeveBloomOfferFacts(
                "offer-dup1", 10, 20, AiSleeveBloomDrawKind.Normal, 5000L, "dec-1", 2L, thirdTileId: 10));
            Assert.Throws<ArgumentException>(() => new AiSleeveBloomOfferFacts(
                "offer-dup2", 10, 20, AiSleeveBloomDrawKind.Normal, 5000L, "dec-1", 2L, thirdTileId: 20));
        }

        [Fact]
        public void AiCheatOptionEnforcesUnmarkedSlotPrivacyAndDefensiveTileCopy()
        {
            var slot = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 3);
            Assert.Throws<ArgumentException>(() => new AiCheatOption(
                "beam-swap-bad", "beam_swap", 0, false, true,
                new[] { new TileId(10) }, slot, targetMarked: false, targetTileId: new TileId(50)));
            Assert.Throws<ArgumentException>(() => new AiCheatOption(
                "beam-swap-missing", "beam_swap", 0, false, true,
                new[] { new TileId(10) }, slot, targetMarked: true, targetTileId: null));

            var sourceTiles = new List<TileId> { new TileId(1), new TileId(2), new TileId(3), new TileId(4) };
            var markOption = new AiCheatOption(
                "tile-mark-1-2-3-4", "tile_mark", 1, false, false, tileIds: sourceTiles);
            sourceTiles.Clear();

            Assert.Equal(new[] { new TileId(1), new TileId(2), new TileId(3), new TileId(4) }, markOption.TileIds);
            Assert.Null(markOption.TargetSlot);
            Assert.False(markOption.TargetMarked);
            Assert.Null(markOption.TargetTileId);

            var unmarkedBeam = new AiCheatOption(
                "beam-swap-10-WallSlot-3", "beam_swap", 0, false, true,
                new[] { new TileId(10) }, slot, targetMarked: false, targetTileId: null);
            Assert.Equal(slot, unmarkedBeam.TargetSlot);
            Assert.False(unmarkedBeam.TargetMarked);
            Assert.Null(unmarkedBeam.TargetTileId);

            var markedBeam = new AiCheatOption(
                "beam-swap-10-WallSlot-4", "beam_swap", 0, false, true,
                new[] { new TileId(10) }, new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 4),
                targetMarked: true, targetTileId: new TileId(55));
            Assert.True(markedBeam.TargetMarked);
            Assert.Equal(new TileId(55), markedBeam.TargetTileId);
        }

        [Fact]
        public void AiCheatOpportunityCopiesCapturedIdentityAndFactsWithoutLeakingUnmarkedTiles()
        {
            var slot0 = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 0);
            var slot1 = new AiBeamSwapSlotFacts(AiBeamSwapSlotKind.WallSlot, 1);
            var beamCandidates = new List<AiBeamSwapCandidateFacts>
            {
                new AiBeamSwapCandidateFacts(slot0, marked: false, tileId: null),
                new AiBeamSwapCandidateFacts(slot1, marked: true, tileId: 77)
            };
            var eligibleHand = new List<TileId> { new TileId(4), new TileId(8) };
            var options = new[]
            {
                new AiCheatOption("beam-keep", "beam_swap", 0, true, true),
                new AiCheatOption("beam-swap-4-WallSlot-0", "beam_swap", 0, false, true, new[] { new TileId(4) }, slot0, false, null),
                new AiCheatOption("beam-swap-4-WallSlot-1", "beam_swap", 0, false, true, new[] { new TileId(4) }, slot1, true, new TileId(77))
            };

            var opportunity = new AiCheatOpportunity(
                seat: 2,
                roundId: "round-m103",
                stateVersion: 12,
                controlEpoch: 3,
                cheatPoints: 110,
                isOfferChoice: true,
                options: options,
                decisionId: "dec-2",
                decisionDeadlineMillis: 4500L,
                offerId: "beam-offer-1",
                windowId: null,
                offerDeadlineMillis: null,
                recoveryGeneration: 2L,
                beamWindow: AiBeamSwapWindow.Wall,
                beamDrawnTileId: new TileId(19),
                beamEligibleHandTileIds: eligibleHand,
                beamCandidates: beamCandidates);

            beamCandidates.Clear();
            eligibleHand.Clear();

            Assert.Equal("dec-2", opportunity.DecisionId);
            Assert.Equal(4500L, opportunity.DeadlineMillis);
            Assert.Equal("beam-offer-1", opportunity.OfferId);
            Assert.Equal(2L, opportunity.RecoveryGeneration);
            Assert.Equal(AiBeamSwapWindow.Wall, opportunity.BeamWindow);
            Assert.Equal(new TileId(19), opportunity.BeamDrawnTileId);
            Assert.Equal(2, opportunity.BeamEligibleHandTileIds.Count);
            Assert.Equal(2, opportunity.BeamCandidates.Count);
            Assert.False(opportunity.BeamCandidates[0].Marked);
            Assert.Null(opportunity.BeamCandidates[0].TileId);
            Assert.True(opportunity.BeamCandidates[1].Marked);
            Assert.Equal(77, opportunity.BeamCandidates[1].TileId);

            var strategy = new RandomCheatBasicAiStrategy(
                new SeededAiRandom(99UL),
                new AiCheatPolicy(enabled: true));
            for (int i = 0; i < 30; i++)
            {
                string? selected = strategy.ChooseCheat(opportunity);
                Assert.NotNull(selected);
                Assert.Contains(opportunity.Options, option => option.OptionId == selected);
            }
        }
    }
}

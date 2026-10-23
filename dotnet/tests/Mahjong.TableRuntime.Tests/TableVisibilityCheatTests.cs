using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableVisibilityCheatTests
    {
        [Fact]
        public void owner_private_view_carries_marks_sleeve_offer_and_beam_candidates()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            state = V32Fixtures.WithLedger(
                state,
                new CheatLedgerEntry("mark-0-r1", 0, -5, 995, "TILE_MARK"),
                new CheatLedgerEntry("beam-1-r1", 1, -10, 990, "BEAM_SWAP"));

            TileMarkTransition mark = state.TileMarks.AddGroup(new TileMarkGroup(
                "g0",
                0,
                new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) },
                round.RoundId,
                1));
            Assert.True(mark.Success);
            state = state.WithTileMarks(mark.State);

            var sleeveOffer = new SleeveBloomOffer(
                "bloom-0-r1",
                0,
                SleeveBloomDrawKind.Normal,
                new TileId(4),
                new TileId(5),
                1,
                SleeveBloomOfferMode.Immediate);
            state = state.WithSleeveBloom(state.SleeveBloom.WithImmediateOffer(sleeveOffer));

            var beamOffer = new BeamSwapOffer(
                "beam-1-r1",
                1,
                BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(new TileId(6), BeamSwapSlot.Wall(54)) },
                new[] { new TileId(7) },
                new TileId(8),
                1);
            state = state.WithBeamSwap(state.BeamSwap.WithOffer(beamOffer));

            TableProjectionContext context = V32Fixtures.Context(round);
            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, state, 0, context);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(round, state, 1, context);
            PublicSnapshotView publicSnapshot = TableVisibilityProjection.ProjectPublicSnapshot(round, context);

            Assert.Equal(995L, seat0.Private.Cheat!.CheatPoints);
            Assert.True(seat0.Private.Cheat.HasTileMark);
            Assert.True(seat0.Private.Cheat.HasSleeveBloom);
            Assert.False(seat0.Private.Cheat.HasBeamSwap);
            Assert.Single(seat0.Private.Cheat.Ledger);
            Assert.Equal("mark-0-r1", seat0.Private.Cheat.Ledger[0].EntryId);

            Assert.Equal(990L, seat1.Private.Cheat!.CheatPoints);
            Assert.False(seat1.Private.Cheat.HasTileMark);
            Assert.False(seat1.Private.Cheat.HasSleeveBloom);
            Assert.True(seat1.Private.Cheat.HasBeamSwap);
            Assert.Single(seat1.Private.Cheat.Ledger);
            Assert.Equal("beam-1-r1", seat1.Private.Cheat.Ledger[0].EntryId);

            TableOwnerPrivateView owner0 = TableVisibilityProjection.ProjectOwnerPrivate(round, state, 0, context);
            TableOwnerPrivateView owner1 = TableVisibilityProjection.ProjectOwnerPrivate(round, state, 1, context);

            Assert.Single(owner0.Cheat.MarkGroups);
            Assert.Equal(new uint[] { 0, 1, 2, 3 }, owner0.Cheat.MarkGroups[0].TileIds.ToArray());
            Assert.NotNull(owner0.Cheat.ImmediateOffer);
            Assert.Equal(4u, owner0.Cheat.ImmediateOffer!.FirstTileId);
            Assert.Equal(5u, owner0.Cheat.ImmediateOffer.SecondTileId);
            Assert.Null(owner0.Cheat.BeamSwapOffer);

            Assert.Empty(owner1.Cheat.MarkGroups);
            Assert.Null(owner1.Cheat.ImmediateOffer);
            Assert.NotNull(owner1.Cheat.BeamSwapOffer);
            Assert.Equal(6u, owner1.Cheat.BeamSwapOffer!.Candidates[0].TileId);
            Assert.Equal(7u, owner1.Cheat.BeamSwapOffer.EligibleHandTileIds[0]);

            string publicDump = VisibilitySurface.Dump(publicSnapshot);
            Assert.False(VisibilitySurface.HasField(publicDump, "CheatPoints"));
            Assert.False(VisibilitySurface.HasField(publicDump, "Ledger"));
            Assert.False(VisibilitySurface.HasField(publicDump, "MarkGroups"));
            Assert.False(VisibilitySurface.HasField(publicDump, "SleeveBloom"));
            Assert.False(VisibilitySurface.HasField(publicDump, "BeamSwap"));

            string owner0Dump = VisibilitySurface.Dump(owner0);
            Assert.DoesNotContain("beam-1-r1", owner0Dump);
            Assert.DoesNotContain("BEAM_SWAP", owner0Dump);
            string owner1Dump = VisibilitySurface.Dump(owner1);
            Assert.DoesNotContain("mark-0-r1", owner1Dump);
            Assert.DoesNotContain("TILE_MARK", owner1Dump);
        }

        [Fact]
        public void tile_mark_success_and_rejection_keep_owner_projection_and_no_side_effects()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, 0);
            TileId[] tiles = hand.Take(4).ToArray();
            CheatTurnContext context = V32Fixtures.CheatContext(round);

            CheatTransition applied = CheatSubmission.ApplyTileMark(
                state,
                round.Tiles,
                context,
                V32Fixtures.Economy(),
                0,
                "mark-req",
                "g1",
                tiles);

            Assert.True(applied.Success);
            Assert.NotNull(applied.State);
            Assert.Equal(CheatErrorCode.None, applied.Error);
            Assert.Contains(applied.Events, item => item.Kind == CheatEventKind.CheatPointsChanged);
            Assert.Contains(applied.Events, item => item.Kind == CheatEventKind.TileMarkApplied);

            TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(
                round,
                applied.State!,
                0,
                V32Fixtures.Context(round));
            Assert.Single(owner.Cheat.MarkGroups);
            Assert.Equal(995L, owner.Cheat.Balance);
            Assert.Single(owner.Cheat.Ledger);

            CheatTurnContext wrongSeat = V32Fixtures.CheatContext(round, currentPlayer: 1);
            CheatTransition rejected = CheatSubmission.ApplyTileMark(
                state,
                round.Tiles,
                wrongSeat,
                V32Fixtures.Economy(),
                1,
                "mark-req-2",
                "g2",
                tiles);

            Assert.False(rejected.Success);
            Assert.Null(rejected.State);
            Assert.Equal(CheatErrorCode.TileNotOwned, rejected.Error);

            TableOwnerPrivateView unchanged = TableVisibilityProjection.ProjectOwnerPrivate(
                round,
                state,
                0,
                V32Fixtures.Context(round));
            Assert.Equal(1000L, unchanged.Cheat.Balance);
            Assert.Empty(unchanged.Cheat.MarkGroups);
            Assert.Empty(unchanged.Cheat.Ledger);
        }

        [Fact]
        public void sleeve_bloom_success_rejection_and_resolution_are_owner_only()
        {
            RoundStateMachine dealt = P31Fixtures.Dealt("round-1");
            TilePositionMap normalMap = V32Fixtures.WithNormalDrawnTile(dealt, 0);
            RoundStateMachine round = RoundStateMachine.CreateDealt(dealt.Spec, normalMap, RoundPhase.CheatWindow);
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            CheatTurnContext context = V32Fixtures.CheatContext(round, drawKind: SleeveBloomDrawKind.Normal);

            CheatTransition applied = CheatSubmission.ActivateSleeveBloom(
                state,
                normalMap,
                context,
                V32Fixtures.Economy(),
                V32Fixtures.SleeveConfig(),
                0,
                "bloom-req");

            Assert.True(applied.Success);
            Assert.NotNull(applied.State);
            Assert.NotNull(applied.State!.SleeveBloom.ImmediateOffer);

            TableProjectionContext projection = V32Fixtures.Context(round);
            TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(
                round,
                applied.State!,
                0,
                projection);
            Assert.NotNull(owner.Cheat.ImmediateOffer);
            Assert.Equal(10L, owner.Cheat.Ledger.Sum(entry => -entry.Amount));

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                round,
                applied.State!,
                0,
                projection);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(
                round,
                applied.State!,
                1,
                projection);
            Assert.True(seat0.Private.Cheat!.HasSleeveBloom);
            Assert.False(seat1.Private.Cheat!.HasSleeveBloom);
            Assert.False(VisibilitySurface.HasField(VisibilitySurface.Dump(seat1), "ImmediateOffer"));

            CheatTurnContext wrongDraw = V32Fixtures.CheatContext(
                round,
                drawKind: SleeveBloomDrawKind.Rinshan);
            CheatTransition rejected = CheatSubmission.ActivateSleeveBloom(
                state,
                normalMap,
                wrongDraw,
                V32Fixtures.Economy(),
                V32Fixtures.SleeveConfig(),
                0,
                "bloom-req-2");
            Assert.False(rejected.Success);
            Assert.Null(rejected.State);
            Assert.Equal(CheatErrorCode.DrawKindMismatch, rejected.Error);

            SleeveBloomOffer offer = applied.State!.SleeveBloom.ImmediateOffer!;
            CheatTransition resolved = CheatSubmission.ChooseSleeveBloom(
                applied.State!,
                normalMap,
                context,
                0,
                offer.OfferId,
                SleeveBloomKeep.First);
            Assert.True(resolved.Success);
            Assert.NotNull(resolved.State);
            Assert.Null(resolved.State!.SleeveBloom.ImmediateOffer);
            Assert.Contains(resolved.Events, item => item.Kind == CheatEventKind.SleeveBloomResolved);

            TableOwnerPrivateView after = TableVisibilityProjection.ProjectOwnerPrivate(
                round,
                resolved.State!,
                0,
                projection);
            Assert.Null(after.Cheat.ImmediateOffer);
            Assert.Equal(990L, after.Cheat.Balance);
        }

        [Fact]
        public void beam_swap_success_rejection_and_resolution_are_owner_only()
        {
            RoundStateMachine dealt = P31Fixtures.Dealt("round-1");
            TilePositionMap normalMap = V32Fixtures.WithNormalDrawnTile(dealt, 0);
            RoundStateMachine round = RoundStateMachine.CreateDealt(dealt.Spec, normalMap, RoundPhase.CheatWindow);
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            CheatTurnContext context = V32Fixtures.CheatContext(round, drawKind: SleeveBloomDrawKind.Normal);

            CheatTransition applied = CheatSubmission.ActivateBeamSwap(
                state,
                normalMap,
                context,
                V32Fixtures.Economy(),
                V32Fixtures.SleeveConfig(),
                "beam-req",
                0);

            Assert.True(applied.Success);
            Assert.NotNull(applied.State);
            Assert.NotNull(applied.State!.BeamSwap.Offer);

            TableProjectionContext projection = V32Fixtures.Context(round);
            TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(
                round,
                applied.State!,
                0,
                projection);
            Assert.NotNull(owner.Cheat.BeamSwapOffer);
            Assert.NotEmpty(owner.Cheat.BeamSwapOffer!.Candidates);
            Assert.NotEmpty(owner.Cheat.BeamSwapOffer.EligibleHandTileIds);

            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(
                round,
                applied.State!,
                0,
                projection);
            PlayerSnapshotView seat1 = TableVisibilityProjection.ProjectPlayerSnapshot(
                round,
                applied.State!,
                1,
                projection);
            Assert.True(seat0.Private.Cheat!.HasBeamSwap);
            Assert.False(seat1.Private.Cheat!.HasBeamSwap);
            Assert.False(VisibilitySurface.HasField(VisibilitySurface.Dump(seat1), "BeamSwapOffer"));

            CheatTurnContext wrongSeat = V32Fixtures.CheatContext(
                round,
                currentPlayer: 1,
                drawKind: SleeveBloomDrawKind.Normal);
            CheatTransition rejected = CheatSubmission.ActivateBeamSwap(
                state,
                normalMap,
                wrongSeat,
                V32Fixtures.Economy(),
                V32Fixtures.SleeveConfig(),
                "beam-req-2",
                2);
            Assert.False(rejected.Success);
            Assert.Null(rejected.State);
            Assert.Equal(CheatErrorCode.NotYourTurn, rejected.Error);

            BeamSwapOffer offer = applied.State!.BeamSwap.Offer!;
            CheatTransition resolved = CheatSubmission.ChooseBeamSwap(
                applied.State!,
                normalMap,
                context,
                0,
                offer.OfferId,
                BeamSwapAction.KeepOriginal,
                null,
                null);
            Assert.True(resolved.Success);
            Assert.NotNull(resolved.State);
            Assert.Null(resolved.State!.BeamSwap.Offer);
            Assert.Contains(resolved.Events, item => item.Kind == CheatEventKind.BeamSwapResolved);

            TableOwnerPrivateView after = TableVisibilityProjection.ProjectOwnerPrivate(
                round,
                resolved.State!,
                0,
                projection);
            Assert.Null(after.Cheat.BeamSwapOffer);
            Assert.Equal(990L, after.Cheat.Balance);
        }
    }
}

using System;
using System.Collections.Generic;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>G-039/G-040: beam swap freezing, equal-amount swaps, drift, keep-original and expiry.</summary>
    public class BeamSwapTests
    {
        private const int Seat = 1;
        private const string RequestId = "req-1";
        private const string OfferId = "beam-1-req-1";
        private static readonly TileId Man6 = TileId.FromTypeIndex(5, 0);

        private static CheatTurnContext WallContext() =>
            CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

        private static CheatTransition ActivateWall(CheatState state, TilePositionMap map, string requestId = RequestId) =>
            CheatSubmission.ActivateBeamSwap(
                state, map, WallContext(), CheatTestBench.Economy(), CheatTestBench.Sleeve(), requestId, Seat);

        [Fact]
        public void ActivateWall_FreezesEightLiveWindowCandidates_WithoutMovingAnything()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out TileId drawnTile, out _);
            CheatState state = CheatTestBench.InitialState();

            CheatTransition result = ActivateWall(state, map);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map); // activation freezes only

            BeamSwapOffer offer = result.State!.BeamSwap.Offer!;
            Assert.Equal(OfferId, offer.OfferId);
            Assert.Equal(BeamSwapWindow.Wall, offer.Window);
            Assert.Equal(8, offer.Candidates.Count); // [pointer, pointer+8) = slots 54..61
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                Assert.Equal(BeamSwapSlotKind.WallSlot, offer.Candidates[i].Slot.Kind);
                Assert.Equal(54 + i, offer.Candidates[i].Slot.Index);
                Assert.True(map.TileAt(TilePosition.Wall(54 + i)).Equals(offer.Candidates[i].Tile));
            }

            Assert.Equal(13, offer.EligibleHandTileIds.Count); // fourteen minus the drawn tile
            Assert.True(offer.DrawnTileId.Equals(drawnTile));
            Assert.Equal(1, offer.CreatedTurnNumber);
            Assert.Equal(1, result.State.BeamSwap.UsedOnTurn);
            Assert.Equal(30, result.State.Ledger.BalanceOf(Seat));
            Assert.Equal("beam-1-req-1", result.State.Ledger.Entries[0].EntryId);
        }

        [Fact]
        public void Swap_WithWallSlot_MovesExactlyTwoTiles()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState activated = ActivateWall(CheatTestBench.InitialState(), map).State!;
            BeamSwapOffer offer = activated.BeamSwap.Offer!;
            TileId handTile = offer.EligibleHandTileIds[0];
            TilePosition handSlot = map.PositionOf(handTile);
            TileId wallTile = map.TileAt(TilePosition.Wall(54));

            CheatTransition result = CheatSubmission.ChooseBeamSwap(
                activated, map, WallContext(), Seat, OfferId, BeamSwapAction.Swap, handTile, BeamSwapSlot.Wall(54));

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Map);
            TilePositionMap moved = result.Map!;
            Assert.True(moved.PositionOf(wallTile).Equals(handSlot)); // wall tile took the hand slot
            Assert.True(moved.PositionOf(handTile).Equals(TilePosition.Wall(54))); // hand tile took the wall slot
            Assert.Equal(map.WallPointer, moved.WallPointer);
            CheatTestBench.AssertConserved(moved);

            Assert.Null(result.State!.BeamSwap.Offer);
            Assert.Equal(30, result.State.Ledger.BalanceOf(Seat)); // no refund
            Assert.Equal(BeamSwapAction.Swap, result.Events[0].Action);
        }

        [Fact]
        public void Swap_InvalidSelections_AreRejectedWithOfferRetained()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out TileId drawnTile, out _);
            CheatState activated = ActivateWall(CheatTestBench.InitialState(), map).State!;
            TileId closedTile = activated.BeamSwap.Offer!.EligibleHandTileIds[0];

            // the drawn tile itself is not a closed hand tile
            CheatTransition drawn = CheatSubmission.ChooseBeamSwap(
                activated, map, WallContext(), Seat, OfferId, BeamSwapAction.Swap, drawnTile, BeamSwapSlot.Wall(54));
            Assert.False(drawn.Success);
            Assert.Equal(CheatErrorCode.InvalidSelection, drawn.Error);

            // slot just outside the frozen window
            CheatTransition outside = CheatSubmission.ChooseBeamSwap(
                activated, map, WallContext(), Seat, OfferId, BeamSwapAction.Swap, closedTile, BeamSwapSlot.Wall(63));
            Assert.False(outside.Success);
            Assert.Equal(CheatErrorCode.CandidateNotFound, outside.Error);

            // KEEP_ORIGINAL takes no arguments
            CheatTransition keepWithArgs = CheatSubmission.ChooseBeamSwap(
                activated, map, WallContext(), Seat, OfferId, BeamSwapAction.KeepOriginal, closedTile, BeamSwapSlot.Wall(54));
            Assert.False(keepWithArgs.Success);
            Assert.Equal(CheatErrorCode.InvalidSelection, keepWithArgs.Error);

            // SWAP without a selection
            CheatTransition swapBare = CheatSubmission.ChooseBeamSwap(
                activated, map, WallContext(), Seat, OfferId, BeamSwapAction.Swap, null, null);
            Assert.False(swapBare.Success);
            Assert.Equal(CheatErrorCode.InvalidSelection, swapBare.Error);

            foreach (CheatTransition rejected in new[] { drawn, outside, keepWithArgs, swapBare })
            {
                Assert.Null(rejected.State);
                Assert.Null(rejected.Map);
            }

            Assert.NotNull(activated.BeamSwap.Offer); // plain rejections retain the frozen offer
            Assert.Equal(30, activated.Ledger.BalanceOf(Seat));
        }

        [Fact]
        public void KeepOriginal_ClearsOffer_MovesNothing_KeepsFee()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState activated = ActivateWall(CheatTestBench.InitialState(), map).State!;

            CheatTransition result = CheatSubmission.ChooseBeamSwap(
                activated, map, WallContext(), Seat, OfferId, BeamSwapAction.KeepOriginal, null, null);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map);
            Assert.Null(result.State!.BeamSwap.Offer);
            Assert.Equal(30, result.State.Ledger.BalanceOf(Seat));
            Assert.Equal(BeamSwapAction.KeepOriginal, result.Events[0].Action);
        }

        [Fact]
        public void StaleOffer_TurnOrDrawDrift_ClearsOfferWithoutRefund()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out TileId drawnTile, out _);
            CheatState activated = ActivateWall(CheatTestBench.InitialState(), map).State!;
            TileId closedTile = activated.BeamSwap.Offer!.EligibleHandTileIds[0];

            // the offer no longer belongs to the current turn
            CheatTurnContext nextTurn = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2);
            CheatTransition staleTurn = CheatSubmission.ChooseBeamSwap(
                activated, map, nextTurn, Seat, OfferId, BeamSwapAction.Swap, closedTile, BeamSwapSlot.Wall(54));
            Assert.False(staleTurn.Success);
            Assert.Equal(CheatErrorCode.StaleOffer, staleTurn.Error);
            Assert.NotNull(staleTurn.State);
            Assert.Null(staleTurn.State!.BeamSwap.Offer);
            Assert.Equal(CheatEventKind.BeamSwapOfferCleared, staleTurn.Events[0].Kind);
            Assert.Equal(30, staleTurn.State.Ledger.BalanceOf(Seat)); // no refund

            // reactivate on turn 2, then let the drawn evidence vanish before choosing
            CheatTurnContext turn2 = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2);
            CheatState reactivated = CheatSubmission.ActivateBeamSwap(
                staleTurn.State, map, turn2, CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-2", Seat).State!;
            Assert.Equal("beam-1-req-2", reactivated.BeamSwap.Offer!.OfferId);
            TilePositionMap quiet = map.Discard(Seat, drawnTile).RequireState();

            CheatTransition staleMap = CheatSubmission.ChooseBeamSwap(
                reactivated, quiet, turn2, Seat, "beam-1-req-2", BeamSwapAction.Swap, closedTile, BeamSwapSlot.Wall(54));
            Assert.False(staleMap.Success);
            Assert.Equal(CheatErrorCode.StaleOffer, staleMap.Error);
            Assert.NotNull(staleMap.State);
            Assert.Null(staleMap.State!.BeamSwap.Offer);
            Assert.Equal(20, staleMap.State.Ledger.BalanceOf(Seat)); // two fees kept, zero refunded
        }

        [Fact]
        public void ExpireBeamSwap_ResolvesAsKeepOriginal()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState activated = ActivateWall(CheatTestBench.InitialState(), map).State!;

            CheatTransition expired = CheatSubmission.ExpireBeamSwap(activated, WallContext(), Seat);
            Assert.True(expired.Success);
            Assert.Null(expired.Map);
            Assert.Null(expired.State!.BeamSwap.Offer);
            Assert.Equal(30, expired.State.Ledger.BalanceOf(Seat));
            Assert.Equal(BeamSwapAction.KeepOriginal, expired.Events[0].Action);

            CheatTransition again = CheatSubmission.ExpireBeamSwap(expired.State, WallContext(), Seat);
            Assert.False(again.Success);
            Assert.Equal(CheatErrorCode.NoPendingOffer, again.Error);
            Assert.Null(again.State);
        }

        [Fact]
        public void ActivationRejections_FailWithoutSideEffects()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState fresh = CheatTestBench.InitialState();
            CheatEconomyConfig economy = CheatTestBench.Economy();

            void Reject(string name, CheatTransition result, CheatErrorCode expected)
            {
                Assert.False(result.Success, name + " should be rejected: " + result.Message);
                Assert.Equal(expected, result.Error);
                Assert.Null(result.State);
                Assert.Null(result.Map);
            }

            Reject("riichi locked",
                CheatSubmission.ActivateBeamSwap(
                    fresh, map,
                    CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, riichi: new[] { false, true, false, false }),
                    economy, CheatTestBench.Sleeve(), RequestId, Seat),
                CheatErrorCode.RiichiLocked);

            Reject("deal extra",
                CheatSubmission.ActivateBeamSwap(
                    fresh, CheatTestBench.CraftedOpening(),
                    CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra),
                    economy, CheatTestBench.Sleeve(), RequestId, 0),
                CheatErrorCode.DealExtraDraw);

            Reject("wrong phase",
                CheatSubmission.ActivateBeamSwap(
                    fresh, map, CheatTestBench.Context(Seat, RoundPhase.Discard, SleeveBloomDrawKind.Normal),
                    economy, CheatTestBench.Sleeve(), RequestId, Seat),
                CheatErrorCode.WrongPhase);

            Reject("response window",
                CheatSubmission.ActivateBeamSwap(
                    fresh, map,
                    CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, responseWindow: true),
                    economy, CheatTestBench.Sleeve(), RequestId, Seat),
                CheatErrorCode.ResponseWindowOpen);

            Reject("insufficient funds",
                CheatSubmission.ActivateBeamSwap(
                    CheatTestBench.InitialState(5), map, WallContext(), economy, CheatTestBench.Sleeve(), RequestId, Seat),
                CheatErrorCode.InsufficientFunds);

            Reject("cost must be ten",
                CheatSubmission.ActivateBeamSwap(
                    fresh, map, WallContext(),
                    new CheatEconomyConfig(true, 40, new Dictionary<string, int> { { CheatEconomyConfig.BeamSwapCostKey, 5 } }),
                    CheatTestBench.Sleeve(), RequestId, Seat),
                CheatErrorCode.CostMustBeTen);

            Reject("beam disabled",
                CheatSubmission.ActivateBeamSwap(
                    fresh, map, WallContext(), economy, CheatTestBench.Sleeve(enabled: false), RequestId, Seat),
                CheatErrorCode.Disabled);

            Reject("draw kind mismatch",
                CheatSubmission.ActivateBeamSwap(
                    fresh, CheatTestBench.OpeningAfterRinshanDraw(Seat, out _, out _), WallContext(),
                    economy, CheatTestBench.Sleeve(), RequestId, Seat),
                CheatErrorCode.DrawKindMismatch);

            Assert.Equal(40, fresh.Ledger.BalanceOf(Seat));
            Assert.Empty(fresh.Ledger.Entries);
        }

        [Fact]
        public void OneTrickPerTurn_AndMutualExclusion_AreEnforced()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = WallContext();

            CheatTransition first = ActivateWall(state, map);
            Assert.True(first.Success);

            // beam offer still pending: re-activation is rejected
            CheatTransition duplicate = ActivateWall(first.State!, map, "req-2");
            Assert.False(duplicate.Success);
            Assert.Equal(CheatErrorCode.OfferAlreadyPending, duplicate.Error);

            // sleeve offer pending: beam activation is blocked in the other direction too
            CheatTransition sleeve = CheatSubmission.ActivateSleeveBloom(
                CheatTestBench.InitialState(), map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), Seat, "req-9");
            Assert.True(sleeve.Success, sleeve.Message);
            CheatTransition cross = ActivateWall(sleeve.State!, map, "req-8");
            Assert.False(cross.Success);
            Assert.Equal(CheatErrorCode.OfferAlreadyPending, cross.Error);

            // after resolving, the same turn is exhausted for both tricks
            CheatTransition resolved = CheatSubmission.ChooseBeamSwap(
                first.State!, map, context, Seat, OfferId, BeamSwapAction.KeepOriginal, null, null);
            Assert.True(resolved.Success);
            CheatTransition exhausted = ActivateWall(resolved.State!, map, "req-3");
            Assert.False(exhausted.Success);
            Assert.Equal(CheatErrorCode.AlreadyUsedThisTurn, exhausted.Error);
            CheatTransition sleeveExhausted = CheatSubmission.ActivateSleeveBloom(
                resolved.State!, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), Seat, "req-4");
            Assert.False(sleeveExhausted.Success);
            Assert.Equal(CheatErrorCode.AlreadyUsedThisTurn, sleeveExhausted.Error);
        }

        [Fact]
        public void DeadWallWindow_FreezesRinshanBackHalfAndReplacement_ExcludesDora()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterRinshanDraw(Seat, out TileId drawnTile, out TileId nextRinshanTile);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);

            CheatTransition result = CheatSubmission.ActivateBeamSwap(
                state, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), RequestId, Seat);

            Assert.True(result.Success, result.Message);
            BeamSwapOffer offer = result.State!.BeamSwap.Offer!;
            Assert.Equal(BeamSwapWindow.DeadWall, offer.Window);

            // rinshan slots 1..3, back half 5..13 (slot 4 is the revealed dora indicator), one replacement
            Assert.Equal(13, offer.Candidates.Count);
            Assert.Equal(BeamSwapSlot.DeadWall(1), offer.Candidates[0].Slot);
            Assert.True(offer.Candidates[0].Tile.Equals(nextRinshanTile));
            for (int i = 3; i <= 11; i++)
            {
                Assert.Equal(BeamSwapSlot.DeadWall(i + 2), offer.Candidates[i].Slot);
            }

            Assert.Equal(BeamSwapSlot.Replacement(0), offer.Candidates[12].Slot);
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                Assert.False(offer.Candidates[i].Slot.Equals(BeamSwapSlot.DeadWall(4)), "the dora indicator is never a candidate");
            }

            CheatState activated = result.State!;
            TileId closedTile = offer.EligibleHandTileIds[0];

            CheatTransition swapRinshan = CheatSubmission.ChooseBeamSwap(
                activated, map, context, Seat, OfferId, BeamSwapAction.Swap, closedTile, BeamSwapSlot.DeadWall(1));
            Assert.True(swapRinshan.Success, swapRinshan.Message);
            TilePosition handSlot = map.PositionOf(closedTile);
            Assert.True(swapRinshan.Map!.PositionOf(nextRinshanTile).Equals(handSlot));
            Assert.True(swapRinshan.Map!.PositionOf(closedTile).Equals(TilePosition.DeadWall(1)));
            CheatTestBench.AssertConserved(swapRinshan.Map!);
        }

        [Fact]
        public void DeadWallWindow_SwapWithReplacementSlot_Works()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterRinshanDraw(Seat, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);

            CheatState activated = CheatSubmission.ActivateBeamSwap(
                state, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), RequestId, Seat).State!;
            TileId closedTile = activated.BeamSwap.Offer!.EligibleHandTileIds[0];
            TileId replacement = map.TileAt(TilePosition.DeadWallReplacement(0));

            CheatTransition result = CheatSubmission.ChooseBeamSwap(
                activated, map, context, Seat, OfferId, BeamSwapAction.Swap, closedTile, BeamSwapSlot.Replacement(0));

            Assert.True(result.Success, result.Message);
            Assert.True(result.Map!.PositionOf(replacement).Equals(map.PositionOf(closedTile)));
            Assert.True(result.Map!.PositionOf(closedTile).Equals(TilePosition.DeadWallReplacement(0)));
            CheatTestBench.AssertConserved(result.Map!);
        }
    }
}

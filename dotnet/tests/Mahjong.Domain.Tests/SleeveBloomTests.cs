using System;
using System.Collections.Generic;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>G-035/G-036: immediate sleeve bloom activation, choice, drift and expiry.</summary>
    public class SleeveBloomTests
    {
        private const string RequestId = "req-1";
        private const string OfferId = "bloom-1-req-1";

        private static CheatTransition Activate(CheatState state, TilePositionMap map, CheatTurnContext context, CheatEconomyConfig? economy = null)
        {
            return CheatSubmission.ActivateSleeveBloom(
                state, map, context, economy ?? CheatTestBench.Economy(), CheatTestBench.Sleeve(), 1, RequestId);
        }

        private static CheatTransition Choose(CheatState state, TilePositionMap map, CheatTurnContext context, SleeveBloomKeep keep)
        {
            return CheatSubmission.ChooseSleeveBloom(state, map, context, 1, OfferId, keep);
        }

        [Fact]
        public void Activate_FreezesDrawnAndFirstLiveWallTile_WithoutMovingAnything()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out TileId drawnTile, out TileId nextWallTile);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

            CheatTransition result = Activate(state, map, context);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map); // activation freezes candidates only; no tile moves yet

            CheatState next = result.State!;
            Assert.Equal(30, next.Ledger.BalanceOf(1));
            Assert.Single(next.Ledger.Entries);
            Assert.Equal("bloom-1-req-1", next.Ledger.Entries[0].EntryId);
            Assert.Equal(1, next.SleeveBloom.UsedOnTurn);

            SleeveBloomOffer? offer = next.SleeveBloom.ImmediateOffer;
            Assert.NotNull(offer);
            Assert.Equal(OfferId, offer.OfferId);
            Assert.Equal(SleeveBloomOfferMode.Immediate, offer.Mode);
            Assert.Equal(SleeveBloomDrawKind.Normal, offer.DrawKind);
            Assert.True(offer.FirstTile.Equals(drawnTile));
            Assert.True(offer.SecondTile.Equals(nextWallTile));
            Assert.Equal(1, offer.CreatedTurnNumber);
        }

        [Fact]
        public void ChooseFirst_KeepsDrawnTile_ClearsOffer_KeepsFee()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState activated = Activate(state, map, context).State!;

            CheatTransition result = Choose(activated, map, context, SleeveBloomKeep.First);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map); // FIRST keeps the original draw: nothing moves
            Assert.Null(result.State!.SleeveBloom.ImmediateOffer);
            Assert.Equal(30, result.State.Ledger.BalanceOf(1)); // no refund
            Assert.Equal(CheatEventKind.SleeveBloomResolved, result.Events[0].Kind);
            Assert.Equal(SleeveBloomKeep.First, result.Events[0].Keep);
        }

        [Fact]
        public void ChooseSecond_SwapsDrawnWithFrozenWallTile_Exactly()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out TileId drawnTile, out TileId nextWallTile);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState activated = Activate(state, map, context).State!;
            int pointerBefore = map.WallPointer;

            CheatTransition result = Choose(activated, map, context, SleeveBloomKeep.Second);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Map);
            TilePositionMap moved = result.Map!;
            Assert.Equal(pointerBefore, moved.WallPointer); // hole at 53 stays open, pointer untouched

            // exact equal-amount movement: the frozen wall tile is in the hand slot, the drawn tile in slot 54
            Assert.True(moved.PositionOf(nextWallTile).Zone == TileZone.Hand);
            Assert.True(moved.PositionOf(drawnTile).Equals(TilePosition.Wall(54)));
            CheatTestBench.AssertConserved(moved);

            CheatState next = result.State!;
            Assert.Null(next.SleeveBloom.ImmediateOffer);
            Assert.Equal(30, next.Ledger.BalanceOf(1)); // no refund on either branch
            Assert.Equal(CheatEventKind.SleeveBloomResolved, result.Events[0].Kind);
            Assert.Equal(SleeveBloomKeep.Second, result.Events[0].Keep);
        }

        [Fact]
        public void ChooseSecond_WhenCandidateLeftItsSlot_SettlesStaleAndClearsOffer()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out TileId nextWallTile);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState activated = Activate(state, map, context).State!;

            // another seat's closed tile swaps with the frozen wall candidate; drawn evidence untouched
            TileId otherHand = map.TilesInZone(TileZone.Hand, 2)[0];
            TilePositionMap drifted = map.SwapHandWithWall(2, otherHand, 54).RequireState();
            Assert.True(drifted.PositionOf(nextWallTile).Zone == TileZone.Hand);

            // V4 golden (sb-choose-stale-*): immediate drift settles stale — the reply is
            // still a rejection but carries the offer-cleared state; no move, no refund
            CheatTransition result = Choose(activated, drifted, context, SleeveBloomKeep.Second);

            Assert.False(result.Success);
            Assert.Equal(CheatErrorCode.StaleOffer, result.Error);
            Assert.NotNull(result.State);
            Assert.Null(result.State!.SleeveBloom.ImmediateOffer);
            Assert.Null(result.Map);
            Assert.Equal(CheatEventKind.SleeveBloomOfferCleared, result.Events[0].Kind);
            Assert.Equal("Stale", result.Events[0].Reason);
            Assert.Equal(30, result.State.Ledger.BalanceOf(1)); // fee kept
        }

        [Fact]
        public void RinshanDraw_SecondChoice_SwapsWithNextRinshanTile()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterRinshanDraw(1, out TileId drawnTile, out TileId nextRinshanTile);
            Assert.Equal(1, map.RinshanPointer);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);

            CheatTransition activation = Activate(state, map, context);
            Assert.True(activation.Success, activation.Message);
            Assert.True(activation.State!.SleeveBloom.ImmediateOffer!.DrawKind == SleeveBloomDrawKind.Rinshan);
            Assert.True(activation.State.SleeveBloom.ImmediateOffer.SecondTile.Equals(nextRinshanTile));

            CheatTransition result = Choose(activation.State, map, context, SleeveBloomKeep.Second);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Map);
            TilePositionMap moved = result.Map!;
            Assert.True(moved.PositionOf(nextRinshanTile).Zone == TileZone.Hand);
            Assert.True(moved.PositionOf(drawnTile).Equals(TilePosition.DeadWall(1)));
            Assert.Equal(1, moved.RinshanPointer);
            Assert.True(moved.DrawnTile!.Value.Tile.Equals(nextRinshanTile));
            CheatTestBench.AssertConserved(moved);
            Assert.Equal(30, result.State!.Ledger.BalanceOf(1));
        }

        [Fact]
        public void ActivationRejections_FailWithoutSideEffects()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out _);
            CheatEconomyConfig economy = CheatTestBench.Economy();
            CheatState fresh = CheatTestBench.InitialState();

            void Reject(string name, CheatTransition result, CheatErrorCode expected)
            {
                Assert.False(result.Success, name + " should be rejected: " + result.Message);
                Assert.Equal(expected, result.Error);
                Assert.Null(result.State);
                Assert.Null(result.Map);
            }

            // deal-extra draw cannot host the trick
            Reject("deal extra",
                CheatSubmission.ActivateSleeveBloom(
                    fresh, CheatTestBench.CraftedOpening(),
                    CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra),
                    economy, CheatTestBench.Sleeve(), 0, RequestId),
                CheatErrorCode.DealExtraDraw);

            Reject("not your turn", Activate(fresh, map, CheatTestBench.Context(2, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal)), CheatErrorCode.NotYourTurn);
            Reject("wrong phase", Activate(fresh, map, CheatTestBench.Context(1, RoundPhase.Discard, SleeveBloomDrawKind.Normal)), CheatErrorCode.WrongPhase);
            Reject("response window",
                Activate(fresh, map, CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, responseWindow: true)),
                CheatErrorCode.ResponseWindowOpen);

            Reject("draw kind mismatch",
                CheatSubmission.ActivateSleeveBloom(
                    fresh, map, CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                    economy, CheatTestBench.Sleeve(), 1, RequestId),
                CheatErrorCode.DrawKindMismatch);

            Reject("insufficient funds",
                Activate(CheatTestBench.InitialState(5), map, CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal)),
                CheatErrorCode.InsufficientFunds);

            Reject("cost must be ten",
                CheatSubmission.ActivateSleeveBloom(
                    fresh, map, CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                    new CheatEconomyConfig(true, 40, new Dictionary<string, int> { { CheatEconomyConfig.SleeveBloomCostKey, 5 } }),
                    CheatTestBench.Sleeve(), 1, RequestId),
                CheatErrorCode.CostMustBeTen);

            Reject("sleeve disabled",
                CheatSubmission.ActivateSleeveBloom(
                    fresh, map, CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                    economy, CheatTestBench.Sleeve(enabled: false), 1, RequestId),
                CheatErrorCode.Disabled);

            // the tracked drawn tile belongs to the dealer, not seat 1
            Reject("foreign drawn tile",
                CheatSubmission.ActivateSleeveBloom(
                    fresh, CheatTestBench.CraftedOpening(), CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                    economy, CheatTestBench.Sleeve(), 1, RequestId),
                CheatErrorCode.DrawnTileNotInHand);

            Assert.Equal(40, fresh.Ledger.BalanceOf(1));
            Assert.Empty(fresh.Ledger.Entries);
        }

        [Fact]
        public void OneTrickPerTurn_SecondActivationIsRejected()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

            CheatTransition first = Activate(state, map, context);
            Assert.True(first.Success);

            // still pending: an activation attempt hits OfferAlreadyPending
            CheatTransition second = Activate(first.State!, map, context);
            Assert.False(second.Success);
            Assert.Equal(CheatErrorCode.OfferAlreadyPending, second.Error);

            CheatTransition resolved = Choose(first.State!, map, context, SleeveBloomKeep.First);
            Assert.True(resolved.Success);

            // resolved but this turn already used the sleeve: AlreadyUsedThisTurn
            CheatTransition third = Activate(resolved.State!, map, context);
            Assert.False(third.Success);
            Assert.Equal(CheatErrorCode.AlreadyUsedThisTurn, third.Error);
            Assert.Null(third.State);
        }

        [Fact]
        public void BeamOfferPending_BlocksSleeveActivation()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

            var candidate = new BeamSwapCandidate(map.TileAt(TilePosition.Wall(54)), BeamSwapSlot.Wall(54));
            var beamOffer = new BeamSwapOffer(
                "beam-1-x", 1, BeamSwapWindow.Wall,
                new[] { candidate },
                new[] { map.TilesInZone(TileZone.Hand, 1)[0] },
                map.DrawnTile!.Value.Tile,
                1);
            CheatState withBeam = state.WithBeamSwap(state.BeamSwap.WithOffer(beamOffer));

            CheatTransition result = Activate(withBeam, map, context);
            Assert.False(result.Success);
            Assert.Equal(CheatErrorCode.OtherOfferPending, result.Error);
            Assert.Null(result.State);
        }

        [Fact]
        public void ExpireImmediate_ClearsOfferAsFirst_WithoutRefund()
        {
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(1, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(1, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState activated = Activate(state, map, context).State!;

            CheatTransition expired = CheatSubmission.ExpireSleeveBloomImmediate(activated, context, 1);
            Assert.True(expired.Success);
            Assert.Null(expired.Map);
            Assert.Null(expired.State!.SleeveBloom.ImmediateOffer);
            Assert.Equal(30, expired.State.Ledger.BalanceOf(1));
            Assert.Equal(SleeveBloomKeep.First, expired.Events[0].Keep);

            CheatTransition again = CheatSubmission.ExpireSleeveBloomImmediate(expired.State, context, 1);
            Assert.False(again.Success);
            Assert.Equal(CheatErrorCode.NoPendingOffer, again.Error);
            Assert.Null(again.State);
        }
    }

    /// <summary>G-037/G-038: off-turn sleeve bloom windows, reservations, delivery, deadline and staleness.</summary>
    public class SleeveBloomOffTurnTests
    {
        private const int Seat = 2;
        private const string RequestId = "req-1";
        private const string OfferId = "bloom-offturn-2-req-1";
        private static readonly TileId Man5 = TileId.FromTypeIndex(4, 0);
        private static readonly TileId Man6 = TileId.FromTypeIndex(5, 0);
        private static readonly TileId Man7 = TileId.FromTypeIndex(6, 0);

        private static CheatTurnContext OffTurnContext(long now = 1_000, RoundPhase phase = RoundPhase.Discard) =>
            CheatTestBench.Context(0, phase, SleeveBloomDrawKind.DealExtra, now: now);

        /// <summary>The owner's own normal draw: the only context that delivers a reservation.</summary>
        private static CheatTurnContext DrawContext() =>
            CheatTestBench.Context(Seat, RoundPhase.Draw, SleeveBloomDrawKind.Normal);

        /// <summary>Initial state with an off-turn window already issued to seat 2 after its discard.</summary>
        private static CheatState WithWindow(CheatState state, out string windowId)
        {
            SleeveBloomState sleeve = state.SleeveBloom.WithOpenedWindow(Seat, 1, out windowId);
            return state.WithSleeveBloom(sleeve);
        }

        private static CheatTransition Activate(CheatState state, TilePositionMap map, CheatTurnContext context, string? windowId = null, string requestId = RequestId)
        {
            return CheatSubmission.ActivateOffTurnSleeveBloom(
                state, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(),
                Seat, requestId, windowId ?? "bloom-window-" + Seat + "-1-0");
        }

        [Fact]
        public void Activate_FreezesFirstTwoAvailableWallTiles_WithDeadline()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatTurnContext context = OffTurnContext();

            CheatTransition result = Activate(state, map, context, windowId);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map); // freezing does not move tiles
            Assert.Equal(windowId, state.SleeveBloom.WindowIdFor(Seat));

            CheatState next = result.State!;
            Assert.Equal(30, next.Ledger.BalanceOf(Seat));
            Assert.Equal("bloom-offturn-2-req-1", next.Ledger.Entries[0].EntryId);
            Assert.Equal(-1, next.SleeveBloom.UsedOnTurn); // off-turn path never consumes the per-turn marker

            SleeveBloomOffer? offer = next.SleeveBloom.OffTurnOfferFor(Seat);
            Assert.NotNull(offer);
            Assert.Equal(OfferId, offer.OfferId);
            Assert.Equal(SleeveBloomOfferMode.OffTurn, offer.Mode);
            Assert.True(offer.FirstTile.Equals(Man5)); // live-wall slots 53 and 54
            Assert.True(offer.SecondTile.Equals(Man6));
            Assert.Equal(11_000, offer.DeadlineMillis); // now 1000 + offTurnChoiceMillis 10000
            Assert.Equal(1_000, offer.CreatedAtMillis);
        }

        [Fact]
        public void WindowGuards_MissingAndStaleWindowsAreRejected()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState noWindow = CheatTestBench.InitialState();
            CheatTurnContext context = OffTurnContext();

            CheatTransition none = Activate(noWindow, map, context);
            Assert.False(none.Success);
            Assert.Equal(CheatErrorCode.NoWindow, none.Error);
            Assert.Null(none.State);

            CheatState stale = WithWindow(noWindow, out _);
            CheatTransition mismatch = Activate(stale, map, context, "bloom-window-2-1-999");
            Assert.False(mismatch.Success);
            Assert.Equal(CheatErrorCode.WindowMismatch, mismatch.Error);
            Assert.Null(mismatch.State);
        }

        [Fact]
        public void TurnAndPhaseGuards_AreRejected()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);

            CheatTransition ownTurn = Activate(state, map, CheatTestBench.Context(Seat, RoundPhase.Discard, SleeveBloomDrawKind.DealExtra), windowId);
            Assert.False(ownTurn.Success);
            Assert.Equal(CheatErrorCode.NotYourTurn, ownTurn.Error);

            CheatTransition roundEnd = Activate(state, map, OffTurnContext(phase: RoundPhase.RoundEnd), windowId);
            Assert.False(roundEnd.Success);
            Assert.Equal(CheatErrorCode.WrongPhase, roundEnd.Error);
            Assert.Null(roundEnd.State);
        }

        [Fact]
        public void PendingOfferOrReservation_BlocksActivation()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatTurnContext context = OffTurnContext();

            CheatTransition first = Activate(state, map, context, windowId);
            Assert.True(first.Success);

            CheatTransition duplicate = Activate(first.State!, map, context, windowId, "req-2");
            Assert.False(duplicate.Success);
            Assert.Equal(CheatErrorCode.OfferAlreadyPending, duplicate.Error);

            CheatState windowed = WithWindow(CheatTestBench.InitialState(), out string reservedWindow);
            CheatState reserved = windowed.WithSleeveBloom(
                windowed.SleeveBloom.WithReservation(new SleeveBloomReservation(Seat, "old", Man5, null, 0)));
            CheatTransition blocked = Activate(reserved, map, context, reservedWindow);
            Assert.False(blocked.Success);
            Assert.Equal(CheatErrorCode.ReservedPending, blocked.Error);
            Assert.Null(blocked.State);
        }

        [Fact]
        public void ChooseSecond_LocksReservation_ReleasesOtherCandidateInPlace()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatTurnContext context = OffTurnContext();
            CheatState activated = Activate(state, map, context, windowId).State!;

            CheatTransition result = CheatSubmission.ChooseOffTurnSleeveBloom(activated, map, context, Seat, OfferId, SleeveBloomKeep.Second);

            Assert.True(result.Success, result.Message);
            Assert.Null(result.Map); // choice locks; delivery moves later

            SleeveBloomReservation? reservation = result.State!.SleeveBloom.ReservationFor(Seat);
            Assert.NotNull(reservation);
            Assert.True(reservation.Tile.Equals(Man6));
            Assert.True(result.State.SleeveBloom.IsProtectedTile(Man6));
            Assert.False(result.State.SleeveBloom.IsProtectedTile(Man5)); // released in place
            Assert.Null(result.State.SleeveBloom.OffTurnOfferFor(Seat));
            Assert.Equal(CheatEventKind.SleeveBloomResolved, result.Events[0].Kind);
            Assert.Equal(30, result.State.Ledger.BalanceOf(Seat));
        }

        [Fact]
        public void Delivery_FirstReservation_BecomesTheNextNormalDraw()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;
            CheatState reserved = CheatSubmission
                .ChooseOffTurnSleeveBloom(activated, map, OffTurnContext(), Seat, OfferId, SleeveBloomKeep.First)
                .State!;

            // the dealer lets its fourteenth tile go; seat 2's next draw is the delivery
            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();
            Assert.False(quiet.DrawnTile.HasValue);

            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(reserved, quiet, DrawContext(), Seat);

            Assert.True(delivery.Success, delivery.Message);
            Assert.NotNull(delivery.Map);
            TilePositionMap delivered = delivery.Map!;
            Assert.True(delivered.PositionOf(Man5).Equals(TilePosition.Hand(Seat, 13)));
            Assert.True(delivered.DrawnTile!.Value.Tile.Equals(Man5));
            Assert.Equal(Seat, delivered.DrawnTile.Value.Seat);
            Assert.Equal(DrawSource.Wall, delivered.DrawnTile.Value.Source);
            Assert.Equal(53, delivered.WallPointer); // V4 removeAt：指针不回退不推进（index==wallPointer）
            CheatTestBench.AssertConserved(delivered);

            // P3.1 边界：投递自有 owner-scoped 事件（同一物理摸牌的公有 DRAW 事件由局状态机发布）
            CheatEvent deliveryEvent = Assert.Single(delivery.Events);
            Assert.Equal(CheatEventKind.SleeveBloomReservationDelivered, deliveryEvent.Kind);
            Assert.Equal(Seat, deliveryEvent.Seat);
            Assert.Equal(OfferId, deliveryEvent.OfferId);

            CheatState next = delivery.State!;
            Assert.Null(next.SleeveBloom.ReservationFor(Seat));
            Assert.Null(next.SleeveBloom.WindowIdFor(Seat)); // window closes with the delivery
        }

        [Fact]
        public void Delivery_SecondReservation_KeepsEarlierHoleReentrant()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;
            CheatState reserved = CheatSubmission
                .ChooseOffTurnSleeveBloom(activated, map, OffTurnContext(), Seat, OfferId, SleeveBloomKeep.Second)
                .State!;

            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();

            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(reserved, quiet, DrawContext(), Seat);

            Assert.True(delivery.Success, delivery.Message);
            TilePositionMap delivered = delivery.Map!;
            Assert.True(delivered.PositionOf(Man6).Zone == TileZone.Hand);
            Assert.True(delivered.PositionOf(Man5).Equals(TilePosition.Wall(53))); // untouched
            Assert.Equal(53, delivered.WallPointer); // V4 removeAt：index>pointer 时指针不动
            Assert.True(delivered.PositionOf(Man7).Equals(TilePosition.Wall(54))); // 预留槽后实体前移一格
            CheatTestBench.AssertConserved(delivered);
        }

        [Fact]
        public void DeliveryWithoutQuietMap_IsRefused()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;
            CheatState reserved = CheatSubmission
                .ChooseOffTurnSleeveBloom(activated, map, OffTurnContext(), Seat, OfferId, SleeveBloomKeep.First)
                .State!;

            // the map still tracks the dealer's drawn tile: no delivery can happen now
            CheatTransition blocked = CheatSubmission.DeliverSleeveBloomReservation(reserved, map, DrawContext(), Seat);
            Assert.False(blocked.Success);
            Assert.Equal(CheatErrorCode.DeliveryNotPossible, blocked.Error);
            Assert.Null(blocked.State);
            Assert.Null(blocked.Map);

            // no reservation at all
            CheatTransition missing = CheatSubmission.DeliverSleeveBloomReservation(state, map, DrawContext(), Seat);
            Assert.False(missing.Success);
            Assert.Equal(CheatErrorCode.ReservationMissing, missing.Error);
        }

        [Fact]
        public void Delivery_OffContextMoments_AreRefusedWithoutSideEffects()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;
            CheatState reserved = CheatSubmission
                .ChooseOffTurnSleeveBloom(activated, map, OffTurnContext(), Seat, OfferId, SleeveBloomKeep.First)
                .State!;

            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();
            string locations = CheatTestBench.Locations(quiet);

            // another seat's turn: the reservation delivers as the owner's own draw only
            CheatTransition foreign = CheatSubmission.DeliverSleeveBloomReservation(reserved, quiet, OffTurnContext(), Seat);
            Assert.False(foreign.Success);
            Assert.Equal(CheatErrorCode.NotYourTurn, foreign.Error);

            // round end: the terminal phase cannot deliver
            CheatTransition ending = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, quiet, CheatTestBench.Context(Seat, RoundPhase.RoundEnd, SleeveBloomDrawKind.Normal), Seat);
            Assert.False(ending.Success);
            Assert.Equal(CheatErrorCode.WrongPhase, ending.Error);

            // an open response window freezes the draw
            CheatTransition windowed = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, quiet,
                CheatTestBench.Context(Seat, RoundPhase.Draw, SleeveBloomDrawKind.Normal, responseWindow: true), Seat);
            Assert.False(windowed.Success);
            Assert.Equal(CheatErrorCode.ResponseWindowOpen, windowed.Error);

            // a rinshan replacement draw is not the delivery moment
            CheatTransition rinshan = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, quiet, CheatTestBench.Context(Seat, RoundPhase.Draw, SleeveBloomDrawKind.Rinshan), Seat);
            Assert.False(rinshan.Success);
            Assert.Equal(CheatErrorCode.DrawKindMismatch, rinshan.Error);

            foreach (CheatTransition refusal in new[] { foreign, ending, windowed, rinshan })
            {
                Assert.Null(refusal.State);
                Assert.Null(refusal.Map);
            }

            // zero side effects: reservation intact, the protected tile untouched, no ledger move
            Assert.NotNull(reserved.SleeveBloom.ReservationFor(Seat));
            Assert.True(reserved.SleeveBloom.IsProtectedTile(Man5));
            Assert.Equal(locations, CheatTestBench.Locations(quiet));
            Assert.Equal(30, reserved.Ledger.BalanceOf(Seat));
        }

        [Fact]
        public void Delivery_ThenContinuedPlay_StaysConserved()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;
            CheatState reserved = CheatSubmission
                .ChooseOffTurnSleeveBloom(activated, map, OffTurnContext(), Seat, OfferId, SleeveBloomKeep.First)
                .State!;

            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();
            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(reserved, quiet, DrawContext(), Seat);
            Assert.True(delivery.Success, delivery.Message);
            CheatTestBench.AssertConserved(delivery.Map!);

            // the delivered tile behaves like any hand tile: discard, then the table keeps moving
            TilePositionMap discarded = delivery.Map!.Discard(Seat, Man5).RequireState();
            CheatTestBench.AssertConserved(discarded);

            // the next seat's normal draw skips the delivery hole and stays conserved
            TilePositionMap seat3 = discarded.DrawFromWall(3).RequireState();
            CheatTestBench.AssertConserved(seat3);
            TilePositionMap seat3Out = seat3.Discard(3, seat3.DrawnTile!.Value.Tile).RequireState();

            // a rinshan replacement after the delivery is unaffected by the sleeve hole
            TilePositionMap rinshan = seat3Out.DrawReplacement(0).RequireState();
            CheatTestBench.AssertConserved(rinshan);
            TilePositionMap rinshanOut = rinshan.Discard(0, rinshan.DrawnTile!.Value.Tile).RequireState();

            // a full go-around later the delivered seat can still beam-swap on its own draw
            TilePositionMap seat1 = rinshanOut.DrawFromWall(1).RequireState();
            TilePositionMap seat1Out = seat1.Discard(1, seat1.DrawnTile!.Value.Tile).RequireState();
            TilePositionMap seat2Draw = seat1Out.DrawFromWall(Seat).RequireState();
            CheatTestBench.AssertConserved(seat2Draw);

            CheatTurnContext beamContext = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2);
            CheatState beamed = CheatSubmission.ActivateBeamSwap(
                delivery.State!, seat2Draw, beamContext,
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-2", Seat).State!;
            BeamSwapOffer beamOffer = beamed.BeamSwap.Offer!;
            CheatTransition swapped = CheatSubmission.ChooseBeamSwap(
                beamed, seat2Draw, beamContext, Seat, beamOffer.OfferId,
                BeamSwapAction.Swap, beamOffer.EligibleHandTileIds[0], beamOffer.Candidates[0].Slot);
            Assert.True(swapped.Success, swapped.Message);
            CheatTestBench.AssertConserved(swapped.Map!);

            // the activation charges are the only ledger moves (delivery itself is free)
            Assert.Equal(20, swapped.State!.Ledger.BalanceOf(Seat));
        }

        [Fact]
        public void Delivery_ThenSameDrawBeam_CandidateWindowMatchesV4RemoveAt()
        {
            // Round-4 阻断复现：交付后同次摸牌发动偷梁换柱。V4 交付 removeAt 后指针不动，
            // ApplicationCoordinator 按该指针起八槽冻结候选——候选实体必须含仍在墙首的
            // 未摸牌、不含已交付实体（修复前 C# 指针跳到预留槽后，候选漏墙首、多纳墙尾）。
            TilePositionMap map = CheatTestBench.CraftedOpening(); // 指针 53，Man5@53 Man6@54 Man7@55
            TileId firstUndrawn = map.TileAt(TilePosition.Wall(53));
            CheatState baseState = CheatTestBench.InitialState();
            CheatState reserved = baseState.WithSleeveBloom(baseState.SleeveBloom.WithReservation(
                new SleeveBloomReservation(Seat, "bloom-offturn-2-x", Man6, null, 0)));

            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();

            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(reserved, quiet, DrawContext(), Seat);
            Assert.True(delivery.Success, delivery.Message);
            TilePositionMap delivered = delivery.Map!;
            Assert.Equal(quiet.WallPointer, delivered.WallPointer); // removeAt：指针不动
            Assert.Equal(quiet.WallEndExclusive - 1, delivered.WallEndExclusive); // 墙尾收缩一格
            Assert.Equal(TileZone.Hand, delivered.PositionOf(Man6).Zone);
            Assert.True(delivered.PositionOf(Man7).Equals(TilePosition.Wall(54))); // 后继前移一格
            CheatTestBench.AssertConserved(delivered);

            // 同一次摸牌的 CheatWindow：beam 候选=交付后 [pointer, pointer+8) 的实际实体
            CheatTurnContext beamContext = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState beamed = CheatSubmission.ActivateBeamSwap(
                delivery.State!, delivered, beamContext,
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-2", Seat).State!;
            BeamSwapOffer offer = beamed.BeamSwap.Offer!;
            Assert.Equal(8, offer.Candidates.Count);
            bool containsFirstUndrawn = false;
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                Assert.True(offer.Candidates[i].Tile.Equals(delivered.TileAt(TilePosition.Wall(delivered.WallPointer + i))));
                containsFirstUndrawn |= offer.Candidates[i].Tile.Equals(firstUndrawn);
                Assert.False(offer.Candidates[i].Tile.Equals(Man6)); // 已交付实体不在墙
            }
            Assert.True(containsFirstUndrawn, "beam candidates must include the wall-front undrawn tile");

            // beam 选择：对换窗口首槽，136 守恒、指针不动
            TileId handTile = offer.EligibleHandTileIds[0];
            TilePosition handPos = delivered.PositionOf(handTile);
            TileId windowFront = delivered.TileAt(TilePosition.Wall(delivered.WallPointer));
            CheatTransition swapped = CheatSubmission.ChooseBeamSwap(
                beamed, delivered, beamContext, Seat, offer.OfferId,
                BeamSwapAction.Swap, handTile, offer.Candidates[0].Slot);
            Assert.True(swapped.Success, swapped.Message);
            Assert.True(swapped.Map!.PositionOf(windowFront).Equals(handPos));
            Assert.True(swapped.Map.PositionOf(handTile).Equals(TilePosition.Wall(delivered.WallPointer)));
            Assert.Equal(delivered.WallPointer, swapped.Map.WallPointer);
            CheatTestBench.AssertConserved(swapped.Map);

            // 后续摸牌照常：下一席摸到的正是换入窗口首槽的实体
            TilePositionMap nextDraw = swapped.Map.DrawFromWall((Seat + 1) % 4).RequireState();
            CheatTestBench.AssertConserved(nextDraw);
            Assert.Equal(TileZone.Hand, nextDraw.PositionOf(handTile).Zone);
        }

        [Fact]
        public void Delivery_ConsecutiveReservations_StayConservedAndCompacted()
        {
            // 两席各预留一张活墙牌：首个交付压缩墙（后继预留实体前移一格），第二个照常交付
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState baseState = CheatTestBench.InitialState();
            CheatState reserved = baseState.WithSleeveBloom(baseState.SleeveBloom
                .WithReservation(new SleeveBloomReservation(2, "bloom-offturn-2-x", Man5, null, 0))
                .WithReservation(new SleeveBloomReservation(3, "bloom-offturn-3-x", Man6, null, 0)));

            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();

            CheatTransition first = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, quiet, CheatTestBench.Context(2, RoundPhase.Draw, SleeveBloomDrawKind.Normal), 2);
            Assert.True(first.Success, first.Message);
            TilePositionMap afterFirst = first.Map!;
            Assert.Equal(TileZone.Hand, afterFirst.PositionOf(Man5).Zone);
            Assert.True(afterFirst.PositionOf(Man6).Equals(TilePosition.Wall(53))); // 后继预留实体前移
            Assert.Equal(quiet.WallEndExclusive - 1, afterFirst.WallEndExclusive);
            CheatTestBench.AssertConserved(afterFirst);

            // 真实时序：席 2 弃掉投递牌后，席 3 才到自己的普通摸牌投递
            CheatTransition second = CheatSubmission.DeliverSleeveBloomReservation(
                first.State!, afterFirst.Discard(2, Man5).RequireState(),
                CheatTestBench.Context(3, RoundPhase.Draw, SleeveBloomDrawKind.Normal), 3);
            Assert.True(second.Success, second.Message);
            TilePositionMap afterSecond = second.Map!;
            Assert.Equal(TileZone.Hand, afterSecond.PositionOf(Man6).Zone);
            Assert.True(afterSecond.PositionOf(Man7).Equals(TilePosition.Wall(53))); // 再次前移
            Assert.Equal(afterFirst.WallEndExclusive - 1, afterSecond.WallEndExclusive);
            CheatTestBench.AssertConserved(afterSecond);
            Assert.Null(first.State!.SleeveBloom.ReservationFor(2));
            Assert.Null(second.State!.SleeveBloom.ReservationFor(3));
        }

        [Fact]
        public void Delivery_AtWallTail_ShrinksEndWithoutShift()
        {
            // 墙尾耗尽边界：预留最后一张活墙牌（四次岭上把墙尾耗近），removeAt 无后继
            // 可前移——墙尾缩一、指针不动、136 守恒。
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState baseState = CheatTestBench.InitialState();
            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap quiet = map.Discard(0, dealerDrawn).RequireState();

            for (int i = 0; i < 4; i++)
            {
                quiet = quiet.DrawReplacement(0).RequireState();
                quiet = quiet.Discard(0, quiet.DrawnTile!.Value.Tile).RequireState();
            }

            TileId tailTile = quiet.TileAt(TilePosition.Wall(quiet.WallEndExclusive - 1));
            CheatState reserved = baseState.WithSleeveBloom(baseState.SleeveBloom.WithReservation(
                new SleeveBloomReservation(Seat, "bloom-offturn-2-x", tailTile, null, 0)));

            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(reserved, quiet, DrawContext(), Seat);
            Assert.True(delivery.Success, delivery.Message);
            TilePositionMap delivered = delivery.Map!;
            Assert.Equal(quiet.WallPointer, delivered.WallPointer);
            Assert.Equal(quiet.WallEndExclusive - 1, delivered.WallEndExclusive);
            Assert.Equal(TileZone.Hand, delivered.PositionOf(tailTile).Zone);
            CheatTestBench.AssertConserved(delivered);
        }

        [Fact]
        public void DeadlineExpired_ForcesFirstSettlement_ButStillRejects()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;

            CheatTransition result = CheatSubmission.ChooseOffTurnSleeveBloom(
                activated, map, OffTurnContext(now: 11_000), Seat, OfferId, SleeveBloomKeep.Second);

            // V4 DEADLINE_EXPIRED: the forced FIRST settlement installs, the reply is still a rejection
            Assert.False(result.Success);
            Assert.Equal(CheatErrorCode.DeadlineExpired, result.Error);
            Assert.NotNull(result.State);
            Assert.Null(result.Map);

            SleeveBloomReservation? reservation = result.State!.SleeveBloom.ReservationFor(Seat);
            Assert.NotNull(reservation);
            Assert.True(reservation.Tile.Equals(Man5)); // FIRST, not the requested SECOND
            Assert.Null(result.State.SleeveBloom.OffTurnOfferFor(Seat));
            Assert.Equal(SleeveBloomKeep.First, result.Events[0].Keep);
            Assert.Equal(30, result.State.Ledger.BalanceOf(Seat));
        }

        [Fact]
        public void StaleCandidates_ArePlainRejections_RetainingThePendingOffer()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;

            // the first candidate is drawn away by seat 1 before the owner chooses
            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            TilePositionMap after = map.Discard(0, dealerDrawn).RequireState().DrawFromWall(1).RequireState();
            Assert.True(after.PositionOf(Man5).Zone == TileZone.Hand);

            // V4 golden (sbo-choose-stale-*): off-turn drift is a plain rejection — the
            // offer stays pending until the deadline sweep forces FIRST
            CheatTransition result = CheatSubmission.ChooseOffTurnSleeveBloom(
                activated, after, OffTurnContext(), Seat, OfferId, SleeveBloomKeep.Second);

            Assert.False(result.Success);
            Assert.Equal(CheatErrorCode.StaleOffer, result.Error);
            Assert.Null(result.State);
            Assert.Null(result.Map);
            Assert.NotNull(activated.SleeveBloom.OffTurnOfferFor(Seat)); // plain rejection retains the offer
            Assert.Equal(30, activated.Ledger.BalanceOf(Seat)); // no refund
        }

        [Fact]
        public void Sweep_SettlesEveryExpiredOfferAsFirst_AndReplaysWhenNothingExpired()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatTurnContext context = OffTurnContext();

            CheatState state = CheatTestBench.InitialState();
            state = state.WithSleeveBloom(state.SleeveBloom.WithOpenedWindow(2, 1, out string window2));
            state = state.WithSleeveBloom(state.SleeveBloom.WithOpenedWindow(3, 1, out string window3));
            CheatState active2 = Activate(state, map, context, window2).State!;
            CheatTransition active3 = CheatSubmission.ActivateOffTurnSleeveBloom(
                active2, map, context, CheatTestBench.Economy(), CheatTestBench.Sleeve(), 3, "req-1", window3);
            Assert.True(active3.Success, active3.Message);
            CheatState both = active3.State!;

            CheatTransition early = CheatSubmission.ExpireOffTurnSleeveBlooms(both, OffTurnContext(now: 5_000));
            Assert.True(early.Success);
            Assert.False(early.Changed); // replay: nothing expired yet

            CheatTransition late = CheatSubmission.ExpireOffTurnSleeveBlooms(both, OffTurnContext(now: 20_000));
            Assert.True(late.Success);
            Assert.True(late.Changed);
            Assert.Null(late.State!.SleeveBloom.OffTurnOfferFor(2));
            Assert.Null(late.State.SleeveBloom.OffTurnOfferFor(3));
            Assert.True(late.State.SleeveBloom.ReservationFor(2)!.Tile.Equals(Man5));
            // seat 3 froze the next two unprotected slots (55/56): seat 2's offer already protects 53/54
            TileId seat3First = both.SleeveBloom.OffTurnOfferFor(3)!.FirstTile;
            Assert.False(seat3First.Equals(Man5));
            Assert.True(late.State.SleeveBloom.ReservationFor(3)!.Tile.Equals(seat3First));
            Assert.Equal(2, late.Events.Count);
        }

        [Fact]
        public void SettleOffTurnForDraw_ConvertsOfferToFirstReservation_WithoutCharge()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = WithWindow(CheatTestBench.InitialState(), out string windowId);
            CheatState activated = Activate(state, map, OffTurnContext(), windowId).State!;

            CheatTransition settled = CheatSubmission.SettleOffTurnForDraw(activated, Seat);
            Assert.True(settled.Success);
            Assert.Null(settled.Map);
            Assert.True(settled.State!.SleeveBloom.ReservationFor(Seat)!.Tile.Equals(Man5));
            Assert.Equal(30, settled.State.Ledger.BalanceOf(Seat)); // conversion never re-charges

            CheatTransition idle = CheatSubmission.SettleOffTurnForDraw(settled.State, 1);
            Assert.True(idle.Success);
            Assert.False(idle.Changed); // replay: no offer for the seat
        }
    }
}

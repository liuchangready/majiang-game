using System;
using System.Collections.Generic;

namespace Mahjong.Domain.Cheat
{
    /// <summary>
    /// Caller-supplied round context for one cheat transition. The domain never reads a
    /// clock, a rule engine or the round state machine: the current player, phase, response
    /// window, riichi flags, turn number, round identity, last draw kind and the current
    /// wall-clock milliseconds are all injected here.
    /// </summary>
    public sealed class CheatTurnContext
    {
        private readonly bool[] _riichiDeclared;

        public CheatTurnContext(
            int currentPlayer,
            RoundPhase phase,
            bool responseWindowOpen,
            IReadOnlyList<bool> riichiDeclared,
            int turnNumber,
            string roundId,
            int roundOrdinal,
            SleeveBloomDrawKind lastDrawKind,
            long nowMillis,
            string? bootId = null)
        {
            if (currentPlayer < 0 || currentPlayer > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(currentPlayer), currentPlayer, "Current player must be 0..3.");
            }

            if (riichiDeclared == null || riichiDeclared.Count != 4)
            {
                throw new ArgumentException("Riichi flags must contain exactly four entries.", nameof(riichiDeclared));
            }

            if (turnNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(turnNumber), turnNumber, "Turn number must be non-negative.");
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("Round id cannot be blank.", nameof(roundId));
            }

            if (roundOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(roundOrdinal), roundOrdinal, "Round ordinal must be non-negative.");
            }

            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Now must be non-negative.");
            }

            _riichiDeclared = new[] { riichiDeclared[0], riichiDeclared[1], riichiDeclared[2], riichiDeclared[3] };
            CurrentPlayer = currentPlayer;
            Phase = phase;
            ResponseWindowOpen = responseWindowOpen;
            TurnNumber = turnNumber;
            RoundId = roundId;
            RoundOrdinal = roundOrdinal;
            LastDrawKind = lastDrawKind;
            NowMillis = nowMillis;
            BootId = bootId ?? string.Empty;
        }

        public int CurrentPlayer { get; }

        public RoundPhase Phase { get; }

        public bool ResponseWindowOpen { get; }

        public bool RiichiDeclared(int seat) => _riichiDeclared[seat];

        public int TurnNumber { get; }

        public string RoundId { get; }

        public int RoundOrdinal { get; }

        public SleeveBloomDrawKind LastDrawKind { get; }

        public long NowMillis { get; }

        public string BootId { get; }
    }

    /// <summary>
    /// Pure P1.4 cheat transitions (V4 ApplicationCoordinator cheat paths plus
    /// TileMarkSubmissionHandler / SleeveBloomSubmissionHandler / BeamSwapSubmissionHandler
    /// validation order). Every method is a total function of its inputs: nothing reads a
    /// clock, a random source or a global id generator, and every rejection leaves the
    /// source state and map untouched. The charge, the cheat state change and — where the
    /// trick moves tiles — the physical map change are produced as one atomic result.
    ///
    /// Out of domain scope (P3.2): decision ids, control epochs, per-decision deadlines and
    /// controller availability; the projection that feeds <see cref="CheatState.ViewFor"/>
    /// to clients.
    /// </summary>
    public static class CheatSubmission
    {
        // === 落汗 TileMark ===

        /// <summary>
        /// Applies a tile mark group: exactly four distinct tiles of the actor's own hand,
        /// during the actor's CHEAT_WINDOW or DISCARD turn without a response window or
        /// riichi, capacity three groups / twelve tiles per seat, no seat-local overlap.
        /// Cost is the configured TILE_MARK cost (must be positive); no tile moves.
        /// </summary>
        public static CheatTransition ApplyTileMark(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            CheatEconomyConfig economy,
            int seat,
            string requestId,
            string groupId,
            IReadOnlyList<TileId> tiles)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (economy == null) throw new ArgumentNullException(nameof(economy));
            if (requestId == null) throw new ArgumentNullException(nameof(requestId));
            if (groupId == null) throw new ArgumentNullException(nameof(groupId));

            if (!economy.Enabled) return CheatTransition.Rejected(CheatErrorCode.Disabled, "Cheat economy is disabled.");
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");
            if (requestId.Length == 0) return CheatTransition.Rejected(CheatErrorCode.InvalidRequest, "Request id cannot be blank.");

            if (context.CurrentPlayer != seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "Tile mark requires the actor's turn.");

            // Cross-trick gates (V4 TileMarkSubmissionHandler): the actor's own pending
            // sleeve bloom or beam swap choice pre-empts the mark, ahead of phase/riichi.
            if (state.SleeveBloom.ImmediateOffer?.Seat == seat || state.SleeveBloom.OffTurnOfferFor(seat) != null)
            {
                return CheatTransition.Rejected(CheatErrorCode.OtherOfferPending, "A sleeve bloom choice is pending.");
            }

            if (state.BeamSwap.Offer?.Seat == seat)
            {
                return CheatTransition.Rejected(CheatErrorCode.OtherOfferPending, "A beam swap choice is pending.");
            }

            if (context.Phase != RoundPhase.CheatWindow && context.Phase != RoundPhase.Discard)
            {
                return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "Tile mark requires CHEAT_WINDOW or DISCARD.");
            }

            if (context.ResponseWindowOpen) return CheatTransition.Rejected(CheatErrorCode.ResponseWindowOpen, "A response window is open.");
            if (context.RiichiDeclared(seat)) return CheatTransition.Rejected(CheatErrorCode.RiichiLocked, "Tile mark is riichi locked.");

            if (tiles == null || tiles.Count != 4)
            {
                return CheatTransition.Rejected(CheatErrorCode.InvalidSelection, "Tile mark requires exactly four tiles.");
            }

            var seen = new bool[TileId.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                if (seen[tile.Value])
                {
                    return CheatTransition.Rejected(CheatErrorCode.InvalidSelection, "Tile mark contains duplicate tile " + tile + ".");
                }

                seen[tile.Value] = true;
                TilePosition position = map.PositionOf(tile);
                if (position.Zone != TileZone.Hand || position.Seat != seat)
                {
                    return CheatTransition.Rejected(CheatErrorCode.TileNotOwned, "Tile " + tile + " is not in seat " + seat + "'s hand.");
                }
            }

            if (!economy.TryGetCost(CheatEconomyConfig.TileMarkCostKey, out int cost) || cost <= 0)
            {
                return CheatTransition.Rejected(CheatErrorCode.CostNotConfigured, "TILE_MARK cost must be configured and positive.");
            }

            CheatState? charged = Charge(state, economy, seat, EntryId("mark", seat, requestId), cost, "TILE_MARK", out CheatEvent chargeEvent, out CheatTransition? chargeFailure);
            if (charged == null)
            {
                return chargeFailure!;
            }

            var group = new TileMarkGroup(groupId, seat, tiles, context.RoundId, context.RoundOrdinal);
            TileMarkTransition marks = charged.TileMarks.AddGroup(group);
            if (!marks.Success)
            {
                return CheatTransition.Rejected(
                    marks.Error switch
                    {
                        TileMarkError.GroupLimitExceeded => CheatErrorCode.MarkCapacityExceeded,
                        TileMarkError.TileLimitExceeded => CheatErrorCode.MarkCapacityExceeded,
                        TileMarkError.TileOverlap => CheatErrorCode.AlreadyMarked,
                        TileMarkError.GroupIdConflict => CheatErrorCode.GroupIdConflict,
                        TileMarkError.NoRoundRegistered => CheatErrorCode.MarkRoundMismatch,
                        TileMarkError.RoundAlreadySettled => CheatErrorCode.MarkRoundMismatch,
                        TileMarkError.RoundIdMismatch => CheatErrorCode.MarkRoundMismatch,
                        TileMarkError.RoundOrdinalMismatch => CheatErrorCode.MarkRoundMismatch,
                        _ => CheatErrorCode.MarkRoundMismatch
                    },
                    "Tile mark state rejected the group: " + marks.Error);
            }

            if (ReferenceEquals(marks.State, charged.TileMarks) && ReferenceEquals(charged.Ledger, state.Ledger))
            {
                return CheatTransition.Replayed(state, Array.Empty<CheatEvent>());
            }

            CheatState next = charged.WithTileMarks(marks.State);
            return CheatTransition.Applied(next, null, new[] { chargeEvent, CheatEvent.TileMarkApplied(seat, groupId) });
        }

        // === 袖里藏花 SleeveBloom — 即时路径 ===

        /// <summary>
        /// Legacy map-only activation entry: no frozen normal-wall evidence is supplied, so the
        /// second candidate is derived from the live map exactly as before. Kept as its own
        /// method name (rather than an overload) so reflection-based probes stay unambiguous.
        /// </summary>
        public static CheatTransition ActivateSleeveBloom(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            CheatEconomyConfig economy,
            SleeveBloomConfig sleeveConfig,
            int seat,
            string requestId) =>
            ActivateSleeveBloomWithFrozenInput(
                state, map, context, economy, sleeveConfig, seat, requestId,
                SleeveBloomActivationInput.Unspecified);

        /// <summary>
        /// Activates the immediate sleeve bloom on the actor's own draw: current player,
        /// CHEAT_WINDOW without response window, drawn tile held in hand, not the deal-extra
        /// draw, no other pending offer, and neither sleeve nor beam already used this turn.
        /// Cost is exactly 10; no tile moves at activation — the choice moves them.
        ///
        /// For a normal draw, an explicitly frozen next-wall candidate is replayed instead of
        /// re-searched: a missing, off-wall, out-of-bounds, reserved or drifted candidate rejects
        /// with <see cref="CheatErrorCode.NoWallCandidate"/> and zero state/map/event side effects.
        /// <see cref="SleeveBloomActivationInput.Unspecified"/> preserves the legacy map-only path.
        /// </summary>
        public static CheatTransition ActivateSleeveBloomWithFrozenInput(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            CheatEconomyConfig economy,
            SleeveBloomConfig sleeveConfig,
            int seat,
            string requestId,
            SleeveBloomActivationInput activationInput)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (economy == null) throw new ArgumentNullException(nameof(economy));
            if (sleeveConfig == null) throw new ArgumentNullException(nameof(sleeveConfig));
            if (activationInput == null) throw new ArgumentNullException(nameof(activationInput));

            if (!sleeveConfig.Enabled || !economy.Enabled) return CheatTransition.Rejected(CheatErrorCode.Disabled, "Sleeve bloom is disabled.");
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");
            if (string.IsNullOrEmpty(requestId)) return CheatTransition.Rejected(CheatErrorCode.InvalidRequest, "Request id cannot be blank.");
            if (!economy.TryGetCost(CheatEconomyConfig.SleeveBloomCostKey, out int cost) || cost != 10)
            {
                return CheatTransition.Rejected(CheatErrorCode.CostMustBeTen, "SLEEVE_BLOOM cost must be exactly 10.");
            }

            if (context.CurrentPlayer != seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "Immediate sleeve bloom requires the actor's turn.");
            if (context.Phase != RoundPhase.CheatWindow) return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "Immediate sleeve bloom requires CHEAT_WINDOW.");
            if (context.ResponseWindowOpen) return CheatTransition.Rejected(CheatErrorCode.ResponseWindowOpen, "A response window is open.");

            if (!map.DrawnTile.HasValue) return CheatTransition.Rejected(CheatErrorCode.NoDrawnTile, "No drawn tile is tracked.");
            TileId drawnTile = map.DrawnTile.Value.Tile;
            if (map.DrawnTile.Value.Seat != seat || map.PositionOf(drawnTile).Zone != TileZone.Hand ||
                map.PositionOf(drawnTile).Seat != seat)
            {
                return CheatTransition.Rejected(CheatErrorCode.DrawnTileNotInHand, "The drawn tile is not in the actor's hand.");
            }

            SleeveBloomDrawKind drawKind = DrawKindOf(map);
            if (drawKind == SleeveBloomDrawKind.DealExtra) return CheatTransition.Rejected(CheatErrorCode.DealExtraDraw, "The deal-extra draw cannot host sleeve bloom.");
            if (drawKind != context.LastDrawKind) return CheatTransition.Rejected(CheatErrorCode.DrawKindMismatch, "Context draw kind disagrees with the map evidence.");

            SleeveBloomState sleeve = state.SleeveBloom;
            if (sleeve.ImmediateOffer != null || sleeve.OffTurnOfferFor(seat) != null)
            {
                return CheatTransition.Rejected(CheatErrorCode.OfferAlreadyPending, "A sleeve bloom offer is already pending.");
            }

            if (state.BeamSwap.Offer != null) return CheatTransition.Rejected(CheatErrorCode.OtherOfferPending, "A beam swap offer is already pending.");
            if (sleeve.UsedOnTurn == context.TurnNumber || state.BeamSwap.UsedOnTurn == context.TurnNumber)
            {
                return CheatTransition.Rejected(CheatErrorCode.AlreadyUsedThisTurn, "A trick was already used this turn.");
            }

            TileId secondTile;
            if (drawKind == SleeveBloomDrawKind.Normal)
            {
                if (activationInput.IsFrozen)
                {
                    CheatTransition? frozenFailure = ResolveFrozenNormalWallCandidate(
                        map, sleeve, activationInput, out secondTile);
                    if (frozenFailure != null)
                    {
                        return frozenFailure;
                    }
                }
                else
                {
                    int secondIndex = FindFirstAvailableWallIndex(map, sleeve);
                    if (secondIndex < 0)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "No available live-wall tile for the second candidate.");
                    }

                    secondTile = map.TileAt(TilePosition.Wall(secondIndex));
                }
            }
            else
            {
                CheatTransition? failure = CheckRinshanSwapFeasible(map, drawnTile);
                if (failure != null)
                {
                    return failure;
                }

                secondTile = map.TileAt(TilePosition.DeadWall(map.RinshanPointer));
            }

            CheatState? charged = Charge(state, economy, seat, EntryId("bloom", seat, requestId), cost, "SLEEVE_BLOOM", out CheatEvent chargeEvent, out CheatTransition? chargeFailure);
            if (charged == null)
            {
                return chargeFailure!;
            }

            var offer = new SleeveBloomOffer(
                EntryId("bloom", seat, requestId),
                seat,
                drawKind,
                drawnTile,
                secondTile,
                context.TurnNumber,
                SleeveBloomOfferMode.Immediate,
                bootId: context.BootId);
            CheatState next = charged
                .WithSleeveBloom(sleeve.WithImmediateOffer(offer).WithUsedOnTurn(context.TurnNumber));
            return CheatTransition.Applied(next, null, new[] { chargeEvent });
        }

        /// <summary>
        /// Resolves the immediate offer: FIRST keeps the original draw without moving any
        /// tile; SECOND swaps the drawn tile with the frozen second candidate (live wall for
        /// normal draws, next rinshan for rinshan draws). The offer clears either way and
        /// the fee is never refunded. Drift between the frozen offer and the live turn or
        /// map settles the offer as stale (V4 SLEEVE_BLOOM_STALE_OFFER): the reply is still
        /// a rejection but carries the offer-cleared state, and no tile moves.
        /// </summary>
        public static CheatTransition ChooseSleeveBloom(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            int seat,
            string offerId,
            SleeveBloomKeep keep)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");
            if (context.CurrentPlayer != seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "The immediate offer belongs to the current player.");
            if (context.Phase != RoundPhase.CheatWindow) return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "Sleeve bloom choice requires CHEAT_WINDOW.");
            if (context.ResponseWindowOpen) return CheatTransition.Rejected(CheatErrorCode.ResponseWindowOpen, "A response window is open.");

            SleeveBloomOffer? offer = state.SleeveBloom.ImmediateOffer;
            if (offer == null) return CheatTransition.Rejected(CheatErrorCode.NoPendingOffer, "No immediate sleeve bloom offer is pending.");
            if (offer.Mode != SleeveBloomOfferMode.Immediate) return CheatTransition.Rejected(CheatErrorCode.OfferMismatch, "The pending offer is not immediate.");
            if (offer.Seat != seat || offer.OfferId != offerId)
            {
                return CheatTransition.Rejected(CheatErrorCode.OfferMismatch, "Offer id does not match the pending offer.");
            }

            if (offer.CreatedTurnNumber != context.TurnNumber)
            {
                return ClearStaleSleeveOffer(state, seat, offer.OfferId, "The offer no longer belongs to the current turn.");
            }

            if (!map.DrawnTile.HasValue || !map.DrawnTile.Value.Tile.Equals(offer.FirstTile))
            {
                return ClearStaleSleeveOffer(state, seat, offer.OfferId, "The drawn tile drifted from the frozen candidate.");
            }

            TileId drawnTile = map.DrawnTile.Value.Tile;
            if (map.PositionOf(drawnTile).Zone != TileZone.Hand || map.PositionOf(drawnTile).Seat != seat)
            {
                return ClearStaleSleeveOffer(state, seat, offer.OfferId, "The drawn tile is not in the actor's hand.");
            }

            TilePositionMap? nextMap = null;
            if (keep == SleeveBloomKeep.Second)
            {
                if (offer.DrawKind == SleeveBloomDrawKind.Normal)
                {
                    int candidateIndex = WallSlotOfTile(map, offer.SecondTile);
                    if (candidateIndex < 0 || candidateIndex >= map.WallEndExclusive ||
                        map.PositionOf(offer.SecondTile).Zone != TileZone.Wall ||
                        candidateIndex != FindFirstAvailableWallIndex(map, state.SleeveBloom))
                    {
                        return ClearStaleSleeveOffer(state, seat, offer.OfferId, "The frozen second candidate drifted from the live wall.");
                    }

                    TransitionResult move = map.SwapHandWithWall(seat, drawnTile, candidateIndex);
                    if (!move.IsSuccess)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.PhysicalMoveFailed, "Wall swap failed: " + move.Error!.Message);
                    }

                    nextMap = move.State;
                }
                else
                {
                    CheatTransition? failure = CheckRinshanSwapFeasible(map, drawnTile);
                    if (failure != null)
                    {
                        return failure.Error == CheatErrorCode.CandidateDrift
                            ? ClearStaleSleeveOffer(state, seat, offer.OfferId, "The drawn tile is no longer the latest rinshan draw.")
                            : failure;
                    }

                    if (!map.TileAt(TilePosition.DeadWall(map.RinshanPointer)).Equals(offer.SecondTile))
                    {
                        return ClearStaleSleeveOffer(state, seat, offer.OfferId, "The frozen rinshan candidate drifted.");
                    }

                    TransitionResult move = map.SwapDrawnWithNextRinshan(seat, drawnTile);
                    if (!move.IsSuccess)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.PhysicalMoveFailed, "Rinshan swap failed: " + move.Error!.Message);
                    }

                    nextMap = move.State;
                }
            }

            CheatState next = state.WithSleeveBloom(state.SleeveBloom.WithClearedImmediateOffer());
            return CheatTransition.Applied(
                next,
                nextMap,
                new[] { CheatEvent.SleeveBloomResolved(seat, offerId, keep) });
        }

        /// <summary>Timeout fallback for the immediate offer: keep FIRST, clear the offer, no refund.</summary>
        public static CheatTransition ExpireSleeveBloomImmediate(CheatState state, CheatTurnContext context, int seat)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");

            SleeveBloomOffer? offer = state.SleeveBloom.ImmediateOffer;
            if (offer == null || offer.Seat != seat)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoPendingOffer, "No immediate sleeve bloom offer for the seat.");
            }

            CheatState next = state.WithSleeveBloom(state.SleeveBloom.WithClearedImmediateOffer());
            return CheatTransition.Applied(next, null, new[] { CheatEvent.SleeveBloomResolved(seat, offer.OfferId, SleeveBloomKeep.First) });
        }

        // === 袖里藏花 — 他家回合（off-turn）路径 ===

        /// <summary>
        /// Activates an off-turn sleeve bloom from a window issued after the actor's own
        /// discard: the actor is not the current player, the phase is one of the five open
        /// phases, the request's window id matches the seat's issued window, no offer or
        /// reservation is pending, at least two live-wall tiles are available, and the fee
        /// is exactly 10. The first two available live-wall tiles freeze as candidates.
        /// </summary>
        public static CheatTransition ActivateOffTurnSleeveBloom(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            CheatEconomyConfig economy,
            SleeveBloomConfig sleeveConfig,
            int seat,
            string requestId,
            string requestWindowId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (economy == null) throw new ArgumentNullException(nameof(economy));
            if (sleeveConfig == null) throw new ArgumentNullException(nameof(sleeveConfig));

            if (!sleeveConfig.Enabled || !economy.Enabled) return CheatTransition.Rejected(CheatErrorCode.Disabled, "Sleeve bloom is disabled.");
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");
            if (string.IsNullOrEmpty(requestId)) return CheatTransition.Rejected(CheatErrorCode.InvalidRequest, "Request id cannot be blank.");
            if (!economy.TryGetCost(CheatEconomyConfig.SleeveBloomCostKey, out int cost) || cost != 10)
            {
                return CheatTransition.Rejected(CheatErrorCode.CostMustBeTen, "SLEEVE_BLOOM cost must be exactly 10.");
            }

            if (context.CurrentPlayer == seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "Off-turn sleeve bloom requires another player's turn.");
            if (!IsOffTurnPhase(context.Phase))
            {
                return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "Off-turn sleeve bloom is closed in phase " + context.Phase + ".");
            }

            string? issuedWindowId = state.SleeveBloom.WindowIdFor(seat);
            if (string.IsNullOrEmpty(issuedWindowId) || string.IsNullOrEmpty(requestWindowId))
            {
                return CheatTransition.Rejected(CheatErrorCode.NoWindow, "No sleeve bloom window was issued to the seat.");
            }

            if (issuedWindowId != requestWindowId)
            {
                return CheatTransition.Rejected(CheatErrorCode.WindowMismatch, "The request window id is stale.");
            }

            if (state.SleeveBloom.ImmediateOffer != null || state.SleeveBloom.OffTurnOfferFor(seat) != null)
            {
                return CheatTransition.Rejected(CheatErrorCode.OfferAlreadyPending, "A sleeve bloom offer is already pending.");
            }

            if (state.SleeveBloom.ReservationFor(seat) != null)
            {
                return CheatTransition.Rejected(CheatErrorCode.ReservedPending, "A sleeve bloom reservation is already pending.");
            }

            if (state.BeamSwap.Offer != null) return CheatTransition.Rejected(CheatErrorCode.OtherOfferPending, "A beam swap offer is already pending.");

            IReadOnlyList<int> available = AvailableWallIndices(map, state.SleeveBloom);
            if (available.Count < 2)
            {
                return CheatTransition.Rejected(CheatErrorCode.WallEmpty, "Fewer than two live-wall tiles are available.");
            }

            TileId firstTile = map.TileAt(TilePosition.Wall(available[0]));
            TileId secondTile = map.TileAt(TilePosition.Wall(available[1]));

            CheatState? charged = Charge(state, economy, seat, EntryId("bloom-offturn", seat, requestId), cost, "SLEEVE_BLOOM", out CheatEvent chargeEvent, out CheatTransition? chargeFailure);
            if (charged == null)
            {
                return chargeFailure!;
            }

            var offer = new SleeveBloomOffer(
                EntryId("bloom-offturn", seat, requestId),
                seat,
                SleeveBloomDrawKind.Normal,
                firstTile,
                secondTile,
                context.TurnNumber,
                SleeveBloomOfferMode.OffTurn,
                issuedWindowId,
                context.NowMillis + sleeveConfig.OffTurnChoiceMillis,
                context.NowMillis,
                context.BootId);
            CheatState next = charged.WithSleeveBloom(charged.SleeveBloom.WithOffTurnOffer(offer));
            return CheatTransition.Applied(next, null, new[] { chargeEvent });
        }

        /// <summary>
        /// Chooses FIRST/SECOND on an off-turn offer. The kept candidate becomes a locked
        /// reservation (delivered physically on the owner's next normal draw); the other tile
        /// is released in place. After the caller-supplied deadline the offer settles as
        /// FIRST and the reply is still a rejection (V4 DEADLINE_EXPIRED semantics). Stale
        /// candidates are a plain rejection (V4): the offer stays pending until the deadline
        /// sweep settles it.
        /// </summary>
        public static CheatTransition ChooseOffTurnSleeveBloom(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            int seat,
            string offerId,
            SleeveBloomKeep keep)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");

            SleeveBloomOffer? offer = state.SleeveBloom.OffTurnOfferFor(seat);
            if (offer == null) return CheatTransition.Rejected(CheatErrorCode.NoPendingOffer, "No off-turn sleeve bloom offer for the seat.");
            if (offer.OfferId != offerId) return CheatTransition.Rejected(CheatErrorCode.OfferMismatch, "Offer id does not match the pending offer.");

            if (context.NowMillis >= offer.DeadlineMillis)
            {
                if (state.SleeveBloom.TryResolveOffTurnOffer(seat, SleeveBloomKeep.First, out SleeveBloomState expired, out SleeveBloomReservation? _))
                {
                    return CheatTransition.RejectedWithState(
                        CheatErrorCode.DeadlineExpired,
                        "The off-turn deadline passed; the offer settled as FIRST.",
                        state.WithSleeveBloom(expired),
                        new[] { CheatEvent.SleeveBloomResolved(seat, offer.OfferId, SleeveBloomKeep.First) });
                }
            }

            int firstSlot = WallSlotOfTile(map, offer.FirstTile);
            int secondSlot = WallSlotOfTile(map, offer.SecondTile);
            bool firstLive = firstSlot >= 0 && map.PositionOf(offer.FirstTile).Zone == TileZone.Wall;
            bool secondLive = secondSlot >= 0 && map.PositionOf(offer.SecondTile).Zone == TileZone.Wall;
            if (!firstLive || !secondLive)
            {
                // V4 (golden sbo-choose-stale-*): off-turn drift is a plain rejection — the
                // offer stays pending until the deadline sweep forces FIRST.
                return CheatTransition.Rejected(CheatErrorCode.StaleOffer, "The off-turn candidates left the live wall.");
            }

            if (!state.SleeveBloom.TryResolveOffTurnOffer(seat, keep, out SleeveBloomState next, out SleeveBloomReservation? reservation) ||
                reservation == null)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoPendingOffer, "The off-turn offer vanished while resolving.");
            }

            CheatState applied = state.WithSleeveBloom(next);
            return CheatTransition.Applied(applied, null, new[] { CheatEvent.SleeveBloomResolved(seat, offer.OfferId, keep) });
        }

        /// <summary>
        /// Authority sweep: settles every off-turn offer past its deadline as FIRST. No
        /// expired offers returns the same state unchanged.
        /// </summary>
        public static CheatTransition ExpireOffTurnSleeveBlooms(
            CheatState state,
            CheatTurnContext context,
            Func<SleeveBloomOffer, bool>? forceExpire = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (context == null) throw new ArgumentNullException(nameof(context));

            SleeveBloomState sleeve = state.SleeveBloom;
            CheatState next = state;
            var events = new List<CheatEvent>();
            for (int i = 0; i < sleeve.OffTurnOffers.Count; i++)
            {
                SleeveBloomOffer offer = sleeve.OffTurnOffers[i];
                if (context.NowMillis < offer.DeadlineMillis &&
                    (forceExpire == null || !forceExpire(offer)))
                {
                    continue;
                }

                if (next.SleeveBloom.TryResolveOffTurnOffer(offer.Seat, SleeveBloomKeep.First, out SleeveBloomState resolved, out SleeveBloomReservation? _))
                {
                    next = next.WithSleeveBloom(resolved);
                    events.Add(CheatEvent.SleeveBloomResolved(offer.Seat, offer.OfferId, SleeveBloomKeep.First));
                }
            }

            if (events.Count == 0)
            {
                return CheatTransition.Replayed(state, Array.Empty<CheatEvent>());
            }

            return CheatTransition.Applied(next, null, events);
        }

        /// <summary>
        /// Converts the seat's off-turn offer into a FIRST reservation immediately before its
        /// next normal draw (V4 <c>settleSleeveBloomOfferForDraw</c>); no charge.
        /// </summary>
        public static CheatTransition SettleOffTurnForDraw(CheatState state, int seat)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");

            if (!state.SleeveBloom.TrySettleOffTurnForDraw(seat, out SleeveBloomState next))
            {
                return CheatTransition.Replayed(state, Array.Empty<CheatEvent>());
            }

            SleeveBloomOffer offer = state.SleeveBloom.OffTurnOfferFor(seat)!;
            return CheatTransition.Applied(state.WithSleeveBloom(next), null, new[] { CheatEvent.SleeveBloomResolved(seat, offer.OfferId, SleeveBloomKeep.First) });
        }

        /// <summary>
        /// Delivers the seat's own reservation as its next normal draw (V4
        /// <c>settleSleeveBloomOfferForDraw</c> delivery): the locked tile leaves the live
        /// wall into the hand, the hole it occupied closes per V4 pointer rules, the
        /// drawn-tile evidence records a wall source, the reservation and the seat's window
        /// clear. Delivery eligibility is checked here, not left to the future runtime: the
        /// owner must be the current player, the phase must be the normal-draw phase (never
        /// a round-end, preparation or discard moment), no response window may be open, the
        /// turn's draw kind must be normal (rinshan and deal-extra draws never deliver), and
        /// the seat must not already hold a drawn tile.
        ///
        /// P3.1 integration boundary: the delivery emits its own owner-scoped
        /// <see cref="CheatEventKind.SleeveBloomReservationDelivered"/> event so every
        /// applied cheat change carries at least one cheat event. The public draw event for
        /// the same physical draw is published by the round state machine; the runtime
        /// delivers both in one batch, so this method intentionally emits no CheatPoints
        /// event — the fee was already charged at activation and nothing is refunded here.
        /// </summary>
        public static CheatTransition DeliverSleeveBloomReservation(CheatState state, TilePositionMap map, CheatTurnContext context, int seat)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");

            SleeveBloomReservation? reservation = state.SleeveBloom.ReservationFor(seat);
            if (reservation == null) return CheatTransition.Rejected(CheatErrorCode.ReservationMissing, "No sleeve bloom reservation for the seat.");
            if (context.CurrentPlayer != seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "The reservation is delivered as the owner's own draw.");
            if (context.Phase != RoundPhase.Draw) return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "The reservation is delivered on the owner's normal draw only.");
            if (context.ResponseWindowOpen) return CheatTransition.Rejected(CheatErrorCode.ResponseWindowOpen, "A response window is open.");
            if (context.LastDrawKind != SleeveBloomDrawKind.Normal) return CheatTransition.Rejected(CheatErrorCode.DrawKindMismatch, "Only the normal draw delivers a reservation.");
            if (map.DrawnTile.HasValue) return CheatTransition.Rejected(CheatErrorCode.DeliveryNotPossible, "The seat still holds a drawn tile.");

            int wallSlot = WallSlotOfTile(map, reservation.Tile);
            if (wallSlot < 0 || map.PositionOf(reservation.Tile).Zone != TileZone.Wall)
            {
                return CheatTransition.Rejected(CheatErrorCode.StaleOffer, "The reserved tile is no longer in the live wall.");
            }

            int handSlot = -1;
            for (int i = 0; i < TilePositionMap.HandSlotCount; i++)
            {
                if (map.IsPositionFree(TilePosition.Hand(seat, i)))
                {
                    handSlot = i;
                    break;
                }
            }

            if (handSlot < 0) return CheatTransition.Rejected(CheatErrorCode.DeliveryNotPossible, "The seat's hand is already full.");

            // V4 TileMovement.drawNormal 的预留交付是 removeAt(index)：预留槽之后的活墙
            // 实体整体前移一格、墙尾收缩一格；仅当预留槽在摸牌指针之前才回退一格指针，
            // 绝不推进。指针不动使交付当席同次摸牌的 CheatWindow 候选窗 [pointer,
            // pointer+8) 与 ApplicationCoordinator 的 V4 冻结窗覆盖同一批实体（含仍在
            // 墙首的未摸牌、不含已交付实体）。
            int nextPointer = wallSlot < map.WallPointer ? map.WallPointer - 1 : map.WallPointer;
            int nextWallEnd = map.WallEndExclusive - 1;

            IReadOnlyList<TilePlacement> source = map.AsPlacements();
            var placements = new TilePlacement[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                TilePlacement placement = source[i];
                TilePosition position = placement.Position;
                if (placement.Tile.Equals(reservation.Tile))
                {
                    placements[i] = new TilePlacement(placement.Tile, TilePosition.Hand(seat, handSlot));
                }
                else if (position.Zone == TileZone.Wall && position.Index > wallSlot && position.Index < map.WallEndExclusive)
                {
                    placements[i] = new TilePlacement(placement.Tile, TilePosition.Wall(position.Index - 1));
                }
                else
                {
                    placements[i] = placement;
                }
            }

            TilePositionMap nextMap = TilePositionMap.FromPlacements(
                placements,
                nextPointer,
                nextWallEnd,
                map.RinshanPointer,
                map.LastDiscard,
                map.Revision + 1,
                map.LastRinshanDraw,
                map.DoraIndicators,
                map.KanCount,
                new DrawnTileEvidence(reservation.Tile, seat, DrawSource.Wall));

            SleeveBloomState nextSleeve = state.SleeveBloom
                .WithoutReservation(seat)
                .WithClearedWindow(seat);
            return CheatTransition.Applied(
                state.WithSleeveBloom(nextSleeve),
                nextMap,
                new[] { CheatEvent.SleeveBloomReservationDelivered(seat, reservation.OfferId) });
        }

        // === 偷梁换柱 BeamSwap ===

        /// <summary>
        /// Activates the beam swap on the actor's own draw: current player, CHEAT_WINDOW
        /// without response window or riichi, drawn tile held, not deal-extra, no other
        /// pending offer, no trick used this turn. The normal draw freezes live-wall
        /// candidates inside [wallPointer, wallPointer+8); a rinshan draw freezes
        /// unconsumed rinshan slots, unrevealed back-half dead-wall slots and replacements.
        /// Cost is exactly 10; no tile moves at activation.
        /// </summary>
        public static CheatTransition ActivateBeamSwap(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            CheatEconomyConfig economy,
            SleeveBloomConfig sleeveConfig,
            string requestId,
            int seat)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (economy == null) throw new ArgumentNullException(nameof(economy));
            if (sleeveConfig == null) throw new ArgumentNullException(nameof(sleeveConfig));

            if (!sleeveConfig.Enabled || !economy.Enabled) return CheatTransition.Rejected(CheatErrorCode.Disabled, "Beam swap is disabled.");
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");
            if (string.IsNullOrEmpty(requestId)) return CheatTransition.Rejected(CheatErrorCode.InvalidRequest, "Request id cannot be blank.");
            if (!economy.TryGetCost(CheatEconomyConfig.BeamSwapCostKey, out int cost) || cost != 10)
            {
                return CheatTransition.Rejected(CheatErrorCode.CostMustBeTen, "BEAM_SWAP cost must be exactly 10.");
            }

            if (context.CurrentPlayer != seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "Beam swap requires the actor's turn.");
            if (context.Phase != RoundPhase.CheatWindow) return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "Beam swap requires CHEAT_WINDOW.");
            if (context.ResponseWindowOpen) return CheatTransition.Rejected(CheatErrorCode.ResponseWindowOpen, "A response window is open.");
            if (context.RiichiDeclared(seat)) return CheatTransition.Rejected(CheatErrorCode.RiichiLocked, "Beam swap is riichi locked.");

            if (!map.DrawnTile.HasValue) return CheatTransition.Rejected(CheatErrorCode.NoDrawnTile, "No drawn tile is tracked.");
            TileId drawnTile = map.DrawnTile.Value.Tile;
            if (map.DrawnTile.Value.Seat != seat || map.PositionOf(drawnTile).Zone != TileZone.Hand ||
                map.PositionOf(drawnTile).Seat != seat)
            {
                return CheatTransition.Rejected(CheatErrorCode.DrawnTileNotInHand, "The drawn tile is not in the actor's hand.");
            }

            List<TileId> eligibleHand = EligibleHandTiles(map, seat);
            if (eligibleHand.Count == 0)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "No closed hand tile is eligible for the swap.");
            }

            SleeveBloomDrawKind drawKind = DrawKindOf(map);
            if (drawKind == SleeveBloomDrawKind.DealExtra) return CheatTransition.Rejected(CheatErrorCode.DealExtraDraw, "The deal-extra draw cannot host beam swap.");
            if (drawKind != context.LastDrawKind) return CheatTransition.Rejected(CheatErrorCode.DrawKindMismatch, "Context draw kind disagrees with the map evidence.");

            if (state.BeamSwap.Offer != null || state.SleeveBloom.ImmediateOffer != null)
            {
                return CheatTransition.Rejected(CheatErrorCode.OfferAlreadyPending, "A trick offer is already pending.");
            }

            if (state.BeamSwap.UsedOnTurn == context.TurnNumber || state.SleeveBloom.UsedOnTurn == context.TurnNumber)
            {
                return CheatTransition.Rejected(CheatErrorCode.AlreadyUsedThisTurn, "A trick was already used this turn.");
            }

            BeamSwapWindow window = drawKind == SleeveBloomDrawKind.Normal ? BeamSwapWindow.Wall : BeamSwapWindow.DeadWall;
            var candidates = new List<BeamSwapCandidate>();
            if (window == BeamSwapWindow.Wall)
            {
                if (map.WallRemaining < 1 || map.WallPointer >= map.WallEndExclusive)
                {
                    return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "No live-wall slot remains in the beam window.");
                }

                int end = Math.Min(map.WallPointer + 8, map.WallEndExclusive);
                for (int i = map.WallPointer; i < end; i++)
                {
                    if (!map.TryGetTileAt(TilePosition.Wall(i), out TileId tile))
                    {
                        return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "Live-wall slot " + i + " is empty inside the beam window.");
                    }

                    if (map.PositionOf(tile).Zone != TileZone.Wall)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "Live-wall slot " + i + " is not a wall tile.");
                    }

                    if (state.SleeveBloom.IsProtectedTile(tile))
                    {
                        continue;
                    }

                    candidates.Add(new BeamSwapCandidate(tile, BeamSwapSlot.Wall(i)));
                }
            }
            else
            {
                for (int i = map.RinshanPointer; i <= 3; i++)
                {
                    if (!map.TryGetTileAt(TilePosition.DeadWall(i), out TileId tile))
                    {
                        return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "Dead-wall slot " + i + " is missing.");
                    }

                    if (map.PositionOf(tile).Zone == TileZone.DeadWall)
                    {
                        candidates.Add(new BeamSwapCandidate(tile, BeamSwapSlot.DeadWall(i)));
                    }
                }

                for (int i = 4; i < TilePositionMap.DeadWallSlotCount; i++)
                {
                    if (!map.TryGetTileAt(TilePosition.DeadWall(i), out TileId tile))
                    {
                        return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "Dead-wall slot " + i + " is missing.");
                    }

                    if (map.PositionOf(tile).Zone == TileZone.DeadWall && !ContainsTile(map.DoraIndicators, tile))
                    {
                        candidates.Add(new BeamSwapCandidate(tile, BeamSwapSlot.DeadWall(i)));
                    }
                }

                for (int i = 0; i < map.ReplacementCount; i++)
                {
                    if (!map.TryGetTileAt(TilePosition.DeadWallReplacement(i), out TileId tile))
                    {
                        return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "Replacement slot " + i + " is missing.");
                    }

                    if (map.PositionOf(tile).Zone == TileZone.DeadWallReplacement)
                    {
                        candidates.Add(new BeamSwapCandidate(tile, BeamSwapSlot.Replacement(i)));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return CheatTransition.Rejected(CheatErrorCode.CandidateUnavailable, "No beam swap candidate is available.");
            }

            CheatState? charged = Charge(state, economy, seat, EntryId("beam", seat, requestId), cost, "BEAM_SWAP", out CheatEvent chargeEvent, out CheatTransition? chargeFailure);
            if (charged == null)
            {
                return chargeFailure!;
            }

            var offer = new BeamSwapOffer(
                EntryId("beam", seat, requestId),
                seat,
                window,
                candidates,
                eligibleHand,
                drawnTile,
                context.TurnNumber);
            CheatState next = charged
                .WithBeamSwap(charged.BeamSwap.WithOffer(offer).WithUsedOnTurn(context.TurnNumber));
            return CheatTransition.Applied(next, null, new[] { chargeEvent });
        }

        /// <summary>
        /// Chooses SWAP or KEEP_ORIGINAL on the frozen offer. SWAP exchanges an eligible
        /// closed hand tile with the chosen candidate slot through the equal-swap map
        /// primitives; KEEP_ORIGINAL moves nothing and refunds nothing. Drift between the
        /// frozen offer and the live map clears the offer without moving tiles or refunding.
        /// </summary>
        public static CheatTransition ChooseBeamSwap(
            CheatState state,
            TilePositionMap map,
            CheatTurnContext context,
            int seat,
            string offerId,
            BeamSwapAction action,
            TileId? handTileId,
            BeamSwapSlot? targetSlot)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");

            if (context.CurrentPlayer != seat) return CheatTransition.Rejected(CheatErrorCode.NotYourTurn, "Beam swap requires the actor's turn.");
            if (context.Phase != RoundPhase.CheatWindow) return CheatTransition.Rejected(CheatErrorCode.WrongPhase, "Beam swap choice requires CHEAT_WINDOW.");

            BeamSwapOffer? offer = state.BeamSwap.Offer;
            if (offer == null) return CheatTransition.Rejected(CheatErrorCode.NoPendingOffer, "No beam swap offer is pending.");
            if (offer.Seat != seat || offer.OfferId != offerId)
            {
                return CheatTransition.Rejected(CheatErrorCode.OfferMismatch, "Offer id does not match the pending offer.");
            }

            if (offer.CreatedTurnNumber != context.TurnNumber)
            {
                return ClearStaleBeamOffer(state, seat, offer.OfferId, "The offer no longer belongs to the current turn.");
            }

            if (action == BeamSwapAction.KeepOriginal && (handTileId.HasValue || targetSlot.HasValue))
            {
                return CheatTransition.Rejected(CheatErrorCode.InvalidSelection, "KEEP_ORIGINAL takes no hand tile or slot.");
            }

            SleeveBloomDrawKind drawKind = DrawKindOf(map);
            bool drawKindConsistent = offer.Window == BeamSwapWindow.Wall
                ? drawKind == SleeveBloomDrawKind.Normal
                : drawKind == SleeveBloomDrawKind.Rinshan;
            if (!drawKindConsistent)
            {
                return ClearStaleBeamOffer(state, seat, offer.OfferId, "The draw source drifted from the frozen window.");
            }

            if (!map.DrawnTile.HasValue || !map.DrawnTile.Value.Tile.Equals(offer.DrawnTileId))
            {
                return ClearStaleBeamOffer(state, seat, offer.OfferId, "The drawn tile drifted from the frozen offer.");
            }

            TileId drawnTile = map.DrawnTile.Value.Tile;
            List<TileId> currentHand = EligibleHandTiles(map, seat);
            if (!SameTileSet(currentHand, offer.EligibleHandTileIds))
            {
                return ClearStaleBeamOffer(state, seat, offer.OfferId, "The closed hand drifted from the frozen offer.");
            }

            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                if (!CandidateStillValid(map, offer.Candidates[i]))
                {
                    return ClearStaleBeamOffer(state, seat, offer.OfferId, "A frozen candidate drifted from its slot.");
                }
            }

            TilePositionMap? nextMap = null;
            if (action == BeamSwapAction.Swap)
            {
                if (!handTileId.HasValue || !targetSlot.HasValue)
                {
                    return CheatTransition.Rejected(CheatErrorCode.InvalidSelection, "SWAP requires a hand tile and a target slot.");
                }

                bool eligible = false;
                for (int i = 0; i < offer.EligibleHandTileIds.Count; i++)
                {
                    if (offer.EligibleHandTileIds[i].Equals(handTileId.Value))
                    {
                        eligible = true;
                        break;
                    }
                }

                if (!eligible)
                {
                    return CheatTransition.Rejected(CheatErrorCode.InvalidSelection, "Hand tile is not in the frozen eligible set.");
                }

                BeamSwapCandidate? candidate = offer.FindCandidate(targetSlot.Value);
                if (candidate == null)
                {
                    return CheatTransition.Rejected(CheatErrorCode.CandidateNotFound, "Target slot is not a frozen candidate.");
                }

                TilePosition handPosition = map.PositionOf(handTileId.Value);
                if (handPosition.Zone != TileZone.Hand || handPosition.Seat != seat)
                {
                    return CheatTransition.Rejected(CheatErrorCode.TileNotOwned, "Hand tile is not in the actor's hand.");
                }

                if (handTileId.Value.Equals(drawnTile))
                {
                    return CheatTransition.Rejected(CheatErrorCode.InvalidSelection, "The drawn tile is not a closed hand tile.");
                }

                TransitionResult move;
                if (offer.Window == BeamSwapWindow.Wall)
                {
                    if (targetSlot.Value.Kind != BeamSwapSlotKind.WallSlot)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.SlotMismatch, "A wall swap requires a wall slot.");
                    }

                    if (targetSlot.Value.Index < map.WallPointer || targetSlot.Value.Index >= map.WallEndExclusive)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.SlotMismatch, "Wall slot is outside the live wall window.");
                    }

                    if (!map.TryGetTileAt(TilePosition.Wall(targetSlot.Value.Index), out TileId wallTile) ||
                        !wallTile.Equals(candidate.Tile) ||
                        map.PositionOf(wallTile).Zone != TileZone.Wall)
                    {
                        return CheatTransition.Rejected(CheatErrorCode.SlotMismatch, "The wall slot no longer holds the frozen candidate.");
                    }

                    move = map.SwapHandWithWall(seat, handTileId.Value, targetSlot.Value.Index);
                }
                else if (targetSlot.Value.Kind == BeamSwapSlotKind.DeadWallSlot)
                {
                    move = map.SwapHandWithDeadWall(seat, handTileId.Value, targetSlot.Value.Index);
                }
                else if (targetSlot.Value.Kind == BeamSwapSlotKind.ReplacementSlot)
                {
                    move = map.SwapHandWithReplacement(seat, handTileId.Value, targetSlot.Value.Index);
                }
                else
                {
                    return CheatTransition.Rejected(CheatErrorCode.SlotMismatch, "A dead-wall swap requires a dead-wall or replacement slot.");
                }

                if (!move.IsSuccess)
                {
                    return CheatTransition.Rejected(CheatErrorCode.PhysicalMoveFailed, "Beam swap move failed: " + move.Error!.Message);
                }

                nextMap = move.State;
            }

            CheatState next = state.WithBeamSwap(state.BeamSwap.WithoutOffer());
            return CheatTransition.Applied(
                next,
                nextMap,
                new[] { CheatEvent.BeamSwapResolved(seat, offerId, action) });
        }

        /// <summary>Timeout fallback: resolves as KEEP_ORIGINAL — the offer clears, nothing moves, nothing refunds.</summary>
        public static CheatTransition ExpireBeamSwap(CheatState state, CheatTurnContext context, int seat)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (seat < 0 || seat > 3) return CheatTransition.Rejected(CheatErrorCode.InvalidSeat, "Seat must be 0..3.");

            BeamSwapOffer? offer = state.BeamSwap.Offer;
            if (offer == null || offer.Seat != seat)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoPendingOffer, "No beam swap offer for the seat.");
            }

            CheatState next = state.WithBeamSwap(state.BeamSwap.WithoutOffer());
            return CheatTransition.Applied(next, null, new[] { CheatEvent.BeamSwapResolved(seat, offer.OfferId, BeamSwapAction.KeepOriginal) });
        }

        // === helpers ===

        /// <summary>
        /// V4 stale settlement for the immediate offer: the offer clears (no move, no
        /// refund) and the stale reply still carries the cleared state.
        /// </summary>
        private static CheatTransition ClearStaleSleeveOffer(CheatState state, int seat, string offerId, string message)
        {
            CheatState cleared = state.WithSleeveBloom(state.SleeveBloom.WithClearedImmediateOffer());
            return CheatTransition.RejectedWithState(
                CheatErrorCode.StaleOffer,
                message,
                cleared,
                new[] { CheatEvent.SleeveBloomOfferCleared(seat, offerId, "Stale") });
        }

        private static CheatTransition ClearStaleBeamOffer(CheatState state, int seat, string offerId, string message)
        {
            CheatState cleared = state.WithBeamSwap(state.BeamSwap.WithoutOffer());
            return CheatTransition.RejectedWithState(
                CheatErrorCode.StaleOffer,
                message,
                cleared,
                new[] { CheatEvent.BeamSwapOfferCleared(seat, offerId, "Stale") });
        }

        private static CheatState? Charge(
            CheatState state,
            CheatEconomyConfig economy,
            int seat,
            string entryId,
            int cost,
            string reason,
            out CheatEvent chargeEvent,
            out CheatTransition? failure)
        {
            chargeEvent = CheatEvent.CheatPointsChanged(seat, entryId, -cost, -1);
            CheatLedgerApplyResult result = state.Ledger.Apply(entryId, seat, -cost, reason);
            if (!result.Success)
            {
                CheatErrorCode code = result.Error switch
                {
                    CheatLedgerError.InsufficientFunds => CheatErrorCode.InsufficientFunds,
                    CheatLedgerError.EntryConflict => CheatErrorCode.LedgerConflict,
                    CheatLedgerError.BalanceOverflow => CheatErrorCode.LedgerOverflow,
                    CheatLedgerError.InvalidSeat => CheatErrorCode.InvalidSeat,
                    _ => CheatErrorCode.InvalidRequest
                };
                failure = CheatTransition.Rejected(code, "Ledger rejected the charge: " + result.Error);
                return null;
            }

            chargeEvent = CheatEvent.CheatPointsChanged(seat, entryId, -cost, result.Entry!.BalanceAfter);
            failure = null;
            return state.WithLedger(result.Ledger!);
        }

        private static string EntryId(string prefix, int seat, string requestId) => prefix + "-" + seat + "-" + requestId;

        private static bool IsOffTurnPhase(RoundPhase phase) =>
            phase == RoundPhase.Draw ||
            phase == RoundPhase.CheatWindow ||
            phase == RoundPhase.Discard ||
            phase == RoundPhase.ResponseWindow ||
            phase == RoundPhase.ChankanWindow;

        private static SleeveBloomDrawKind DrawKindOf(TilePositionMap map)
        {
            if (!map.DrawnTile.HasValue)
            {
                return SleeveBloomDrawKind.DealExtra;
            }

            return map.DrawnTile.Value.Source switch
            {
                DrawSource.Wall => SleeveBloomDrawKind.Normal,
                DrawSource.Rinshan => SleeveBloomDrawKind.Rinshan,
                _ => SleeveBloomDrawKind.DealExtra
            };
        }

        private static CheatTransition? CheckRinshanSwapFeasible(TilePositionMap map, TileId drawnTile)
        {
            if (map.RinshanPointer < 1 || map.RinshanPointer > 3)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "Rinshan pointer " + map.RinshanPointer + " is outside 1..3.");
            }

            if (map.KanCount >= 4)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "All four rinshan draws are consumed.");
            }

            if (!map.LastRinshanDraw.HasValue ||
                !map.LastRinshanDraw.Value.Tile.Equals(drawnTile) ||
                map.LastRinshanDraw.Value.DeadWallIndex != map.RinshanPointer - 1)
            {
                return CheatTransition.Rejected(CheatErrorCode.CandidateDrift, "The drawn tile is not the latest rinshan draw.");
            }

            if (!map.TryGetTileAt(TilePosition.DeadWall(map.RinshanPointer), out TileId nextRinshan) ||
                map.PositionOf(nextRinshan).Zone != TileZone.DeadWall)
            {
                return CheatTransition.Rejected(CheatErrorCode.NoWallCandidate, "No unconsumed rinshan tile remains.");
            }

            return null;
        }

        /// <summary>
        /// Replays the frozen normal-wall candidate against the authoritative map. The frozen
        /// flags and the live map must both say the candidate is available; neither side may
        /// override the other. When they disagree the transition rejects and never substitutes
        /// another wall tile.
        /// </summary>
        private static CheatTransition? ResolveFrozenNormalWallCandidate(
            TilePositionMap map,
            SleeveBloomState sleeve,
            SleeveBloomActivationInput activationInput,
            out TileId candidate)
        {
            candidate = default;

            if (!activationInput.NextWallTileId.HasValue || !activationInput.NextWallTileInWall)
            {
                return CheatTransition.Rejected(
                    CheatErrorCode.NoWallCandidate,
                    "The frozen next-wall candidate is missing or no longer in the wall.");
            }

            TileId frozen = activationInput.NextWallTileId.Value;
            TilePosition position = map.PositionOf(frozen);
            if (position.Zone != TileZone.Wall ||
                position.Index < 0 ||
                position.Index >= map.WallEndExclusive)
            {
                return CheatTransition.Rejected(
                    CheatErrorCode.NoWallCandidate,
                    "The frozen next-wall candidate " + frozen + " is outside the live wall bounds.");
            }

            if (sleeve.IsProtectedTile(frozen))
            {
                return CheatTransition.Rejected(
                    CheatErrorCode.NoWallCandidate,
                    "The frozen next-wall candidate " + frozen + " is reserved by another sleeve bloom selection.");
            }

            if (!SleeveBloomActivationInput.TryResolveFirstAvailableWallCandidate(
                    map, sleeve, out TileId authoritative, out int authoritativeIndex) ||
                !authoritative.Equals(frozen) ||
                authoritativeIndex != position.Index)
            {
                return CheatTransition.Rejected(
                    CheatErrorCode.NoWallCandidate,
                    "The frozen next-wall candidate " + frozen + " drifted from the live wall.");
            }

            if (!map.TryGetTileAt(TilePosition.Wall(position.Index), out TileId occupying) ||
                !occupying.Equals(frozen))
            {
                return CheatTransition.Rejected(
                    CheatErrorCode.NoWallCandidate,
                    "The frozen next-wall candidate " + frozen + " no longer occupies its wall slot.");
            }

            candidate = frozen;
            return null;
        }

        private static int FindFirstAvailableWallIndex(TilePositionMap map, SleeveBloomState sleeve)
        {
            return SleeveBloomActivationInput.TryResolveFirstAvailableWallCandidate(
                map, sleeve, out _, out int index)
                ? index
                : -1;
        }

        private static IReadOnlyList<int> AvailableWallIndices(TilePositionMap map, SleeveBloomState sleeve)
        {
            var indices = new List<int>();
            for (int i = 0; i < map.WallEndExclusive; i++)
            {
                if (!map.TryGetTileAt(TilePosition.Wall(i), out TileId tile))
                {
                    continue;
                }

                if (map.PositionOf(tile).Zone != TileZone.Wall)
                {
                    continue;
                }

                if (sleeve.IsProtectedTile(tile))
                {
                    continue;
                }

                indices.Add(i);
            }

            return indices;
        }

        private static int WallSlotOfTile(TilePositionMap map, TileId tile)
        {
            TilePosition position = map.PositionOf(tile);
            if (position.Zone != TileZone.Wall || position.Index >= map.WallEndExclusive)
            {
                return -1;
            }

            return position.Index;
        }

        private static List<TileId> EligibleHandTiles(TilePositionMap map, int seat)
        {
            IReadOnlyList<TileId> hand = map.TilesInZone(TileZone.Hand, seat);
            var tiles = new List<TileId>(hand.Count);
            TileId? drawn = map.DrawnTile.HasValue ? map.DrawnTile.Value.Tile : (TileId?)null;
            for (int i = 0; i < hand.Count; i++)
            {
                if (drawn.HasValue && hand[i].Equals(drawn.Value))
                {
                    continue;
                }

                tiles.Add(hand[i]);
            }

            return tiles;
        }

        private static bool SameTileSet(IReadOnlyList<TileId> left, IReadOnlyList<TileId> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            var present = new bool[TileId.Count];
            for (int i = 0; i < left.Count; i++)
            {
                present[left[i].Value] = true;
            }

            for (int i = 0; i < right.Count; i++)
            {
                if (!present[right[i].Value])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CandidateStillValid(TilePositionMap map, BeamSwapCandidate candidate)
        {
            switch (candidate.Slot.Kind)
            {
                case BeamSwapSlotKind.WallSlot:
                    return candidate.Slot.Index >= map.WallPointer &&
                           candidate.Slot.Index < map.WallEndExclusive &&
                           map.TryGetTileAt(TilePosition.Wall(candidate.Slot.Index), out TileId wallTile) &&
                           wallTile.Equals(candidate.Tile) &&
                           map.PositionOf(wallTile).Zone == TileZone.Wall;
                case BeamSwapSlotKind.DeadWallSlot:
                    return map.TryGetTileAt(TilePosition.DeadWall(candidate.Slot.Index), out TileId deadTile) &&
                           deadTile.Equals(candidate.Tile) &&
                           map.PositionOf(deadTile).Zone == TileZone.DeadWall &&
                           !ContainsTile(map.DoraIndicators, deadTile) &&
                           ((candidate.Slot.Index >= map.RinshanPointer && candidate.Slot.Index <= 3) ||
                            (candidate.Slot.Index >= 4 && candidate.Slot.Index <= 13));
                case BeamSwapSlotKind.ReplacementSlot:
                    return map.TryGetTileAt(TilePosition.DeadWallReplacement(candidate.Slot.Index), out TileId replacement) &&
                           replacement.Equals(candidate.Tile) &&
                           map.PositionOf(replacement).Zone == TileZone.DeadWallReplacement;
                default:
                    return false;
            }
        }

        private static bool ContainsTile(IReadOnlyList<TileId> tiles, TileId tile)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].Equals(tile))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

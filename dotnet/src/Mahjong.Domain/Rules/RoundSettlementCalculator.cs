using System;
using System.Collections.Generic;

namespace Mahjong.Domain.Rules
{
    /// <summary>
    /// Authoritative bridge between the frozen scoring layer and the current immutable
    /// <see cref="RoundStateMachine"/>.
    ///
    /// It does not implement a second scorer. It derives every fact the tile map can prove
    /// (hand ownership, physical tile identity, meld groups, drawn tile, discard/chankan
    /// window, dora indicators, ura eligibility), re-binds host-supplied meld history and
    /// riichi/tenpai facts to those physical tiles, then calls the existing
    /// <see cref="WinningScoreEvaluator"/> (which itself invokes <see cref="YakuEvaluator"/>)
    /// and <see cref="SettlementService"/>. The returned <see cref="RoundSettlement"/> is
    /// executable only through the current RoundCommand Tsumo/Ron/ExhaustiveDraw/AbortiveDraw
    /// factories and the existing Prepare/Install path.
    ///
    /// Deliberately absent here: reading a clock, generating result ids, shuffling, seeding,
    /// accepting caller-provided final scores/han/fu/payments, and any mutation of
    /// RoundTypes, RoundStateMachine or the existing Rules implementation.
    /// </summary>
    public static class RoundSettlementCalculator
    {
        private const int HandTileCount = 13;

        public static RoundSettlementCalculation CalculateTsumo(
            RoundStateMachine round,
            RoundSettlementFacts facts)
        {
            EnsureUsable(round);
            EnsureNotAbortiveFacts(facts);
            if (round.Window != null)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WrongWindow,
                    "A tsumo cannot be calculated while a response/chankan window is open.");
            }

            if (round.Phase != RoundPhase.Draw &&
                round.Phase != RoundPhase.CheatWindow &&
                round.Phase != RoundPhase.Discard &&
                round.Phase != RoundPhase.KanDraw)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WrongPhase,
                    "A tsumo is not legal in phase " + round.Phase + ".");
            }

            int winner = round.CurrentSeat;
            RoundSettlementSeatFacts seatFacts = RequireSeat(facts, winner);
            DrawnTileEvidence drawn = round.Tiles.DrawnTile ??
                throw Reject(
                    RoundSettlementRejectionCode.NoDrawnTile,
                    "Seat " + winner + " has no tracked drawn tile.");
            if (drawn.Seat != winner)
            {
                throw Reject(
                    RoundSettlementRejectionCode.DrawnTileMismatch,
                    "The tracked drawn tile belongs to seat " + drawn.Seat + ", not seat " + winner + ".");
            }

            return CalculateWin(round, facts, winner, seatFacts, drawn.Tile, true, -1, null);
        }

        public static RoundSettlementCalculation CalculateRon(
            RoundStateMachine round,
            int winnerSeat,
            RoundSettlementFacts facts)
        {
            EnsureUsable(round);
            EnsureNotAbortiveFacts(facts);
            if (winnerSeat < 0 || winnerSeat > 3)
            {
                throw Reject(
                    RoundSettlementRejectionCode.InvalidSeat,
                    "Ron winner seat must be 0..3, got " + winnerSeat + ".");
            }

            RoundResponseWindow window = round.Window ??
                throw Reject(
                    RoundSettlementRejectionCode.WrongWindow,
                    "Ron requires an open discard or chankan response window.");
            if (round.Phase != (window.IsChankan ? RoundPhase.ChankanWindow : RoundPhase.ResponseWindow))
            {
                throw Reject(
                    RoundSettlementRejectionCode.WindowKindMismatch,
                    "Window kind " + (window.IsChankan ? "CHANKAN" : "DISCARD") +
                    " does not match phase " + round.Phase + ".");
            }

            if (winnerSeat == window.FromSeat)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WinnerIsDiscarder,
                    "Seat " + winnerSeat + " cannot Ron its own discard/kan tile.");
            }

            if (!ContainsSeat(window.EligibleSeats, winnerSeat))
            {
                throw Reject(
                    RoundSettlementRejectionCode.WinnerNotEligible,
                    "Seat " + winnerSeat + " is not eligible in this response window.");
            }

            if (window.HasResponded(winnerSeat))
            {
                throw Reject(
                    RoundSettlementRejectionCode.ResponseAlreadyRecorded,
                    "Seat " + winnerSeat + " already responded in this window.");
            }

            if (window.IsChankan)
            {
                TransitionResult dryRun = round.Tiles.AddedKan(window.FromSeat, window.TargetTile);
                if (!dryRun.IsSuccess)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WrongWindow,
                        "Chankan target is not a legal added-kan tile: " +
                        (dryRun.Error == null ? "unknown" : dryRun.Error.ToString()));
                }
            }
            else
            {
                LastDiscardEvidence? last = round.Tiles.LastDiscard;
                if (!last.HasValue ||
                    last.Value.Tile != window.TargetTile ||
                    last.Value.Seat != window.FromSeat)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WrongWindow,
                        "Response window target is not the latest discard of seat " + window.FromSeat + ".");
                }

                TilePosition position = round.Tiles.PositionOf(window.TargetTile);
                if (position.Zone != TileZone.Discard || position.Seat != window.FromSeat)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldTileMismatch,
                        "Ron target " + window.TargetTile + " is not owned by seat " +
                        window.FromSeat + " in the discard zone.");
                }
            }

            RoundSettlementSeatFacts seatFacts = RequireSeat(facts, winnerSeat);
            return CalculateWin(
                round,
                facts,
                winnerSeat,
                seatFacts,
                window.TargetTile,
                false,
                window.FromSeat,
                window);
        }

        public static RoundSettlementCalculation CalculateExhaustiveDraw(
            RoundStateMachine round,
            RoundSettlementFacts facts)
        {
            EnsureUsable(round);
            EnsureNotAbortiveFacts(facts);
            if (round.Window != null)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WrongWindow,
                    "An exhaustive draw cannot be calculated while a response window is open.");
            }

            if (round.Phase != RoundPhase.Draw)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WrongPhase,
                    "An exhaustive draw is only legal in DRAW, not " + round.Phase + ".");
            }

            if (round.Tiles.WallRemaining != 0)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WallNotExhausted,
                    "Wall still has " + round.Tiles.WallRemaining + " tiles.");
            }

            if (facts.Seats.Count != 4)
            {
                throw Reject(
                    RoundSettlementRejectionCode.SeatFactMissing,
                    "An exhaustive draw requires trusted hand facts for all four seats.");
            }

            var tenpai = new List<int>();
            for (int seat = 0; seat < 4; seat++)
            {
                RoundSettlementSeatFacts seatFacts = RequireSeat(facts, seat);
                Meld[] melds = ValidateMelds(round.Tiles, seat, seatFacts.Melds);
                PlayerHand hand = BuildHand(round.Tiles, seat, melds, null);
                bool derivedTenpai = HandDecomposer.CalculateWaits(hand.AllClosedTiles).Count > 0;
                if (derivedTenpai != seatFacts.IsTenpai)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.TenpaiEvidenceMismatch,
                        "Seat " + seat + " tenpai evidence is " + seatFacts.IsTenpai +
                        " but the closed hand derives " + derivedTenpai + ".");
                }

                if (derivedTenpai)
                {
                    tenpai.Add(seat);
                }
            }

            var input = new SettlementInput(
                round.Scores,
                round.DealerSeat,
                round.HonbaSticks,
                round.RiichiSticks);
            SettlementResult payments;
            try
            {
                payments = SettlementService.ExhaustiveDraw(input, tenpai);
            }
            catch (OverflowException overflow)
            {
                throw Reject(RoundSettlementRejectionCode.ArithmeticOverflow, overflow.Message);
            }

            RoundOutcome outcome = RoundOutcome.ExhaustiveDraw(tenpai);
            var settlement = new RoundSettlement(
                facts.ResultId,
                outcome,
                payments.NewScores,
                payments.RemainingRiichiSticks,
                facts.CompletedAtUtc,
                facts.CompletedAtMonotonicMillis);
            RoundCommand command = RoundCommand.ExhaustiveDraw(round.RoundId, settlement);
            return new RoundSettlementCalculation(
                round.RoundId,
                round.Version,
                round.SourceFingerprint,
                RoundActionKind.DeclareExhaustiveDraw,
                settlement,
                command,
                null,
                payments,
                outcome,
                null,
                -1,
                -1,
                false,
                -1);
        }

        public static RoundSettlementCalculation CalculateAbortiveDraw(
            RoundStateMachine round,
            RoundSettlementFacts facts)
        {
            EnsureUsable(round);
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (!facts.AbortiveDrawReason.HasValue)
            {
                throw Reject(
                    RoundSettlementRejectionCode.AbortiveReasonMissing,
                    "An abortive draw requires an explicit trusted reason.");
            }

            if (round.Phase == RoundPhase.Stacking)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WrongPhase,
                    "An abortive draw cannot be calculated before the deal.");
            }

            if (round.Window != null ||
                round.Phase == RoundPhase.ResponseWindow ||
                round.Phase == RoundPhase.ChankanWindow)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WrongWindow,
                    "An abortive draw cannot be calculated while a response window is open.");
            }

            SettlementAbortiveDrawReason reason = facts.AbortiveDrawReason.Value;
            ValidateAbortiveReason(round, facts, reason);

            var input = new SettlementInput(
                round.Scores,
                round.DealerSeat,
                round.HonbaSticks,
                round.RiichiSticks);
            SettlementResult payments;
            try
            {
                payments = SettlementService.AbortiveDraw(input);
            }
            catch (OverflowException overflow)
            {
                throw Reject(RoundSettlementRejectionCode.ArithmeticOverflow, overflow.Message);
            }

            RoundOutcome outcome = RoundOutcome.AbortiveDraw(ReasonCode(reason));
            var settlement = new RoundSettlement(
                facts.ResultId,
                outcome,
                payments.NewScores,
                payments.RemainingRiichiSticks,
                facts.CompletedAtUtc,
                facts.CompletedAtMonotonicMillis);
            RoundCommand command = RoundCommand.AbortiveDraw(round.RoundId, settlement);
            return new RoundSettlementCalculation(
                round.RoundId,
                round.Version,
                round.SourceFingerprint,
                RoundActionKind.DeclareAbortiveDraw,
                settlement,
                command,
                null,
                payments,
                outcome,
                null,
                -1,
                -1,
                false,
                -1);
        }

        private static RoundSettlementCalculation CalculateWin(
            RoundStateMachine round,
            RoundSettlementFacts facts,
            int winner,
            RoundSettlementSeatFacts seatFacts,
            TileId winTile,
            bool isTsumo,
            int loser,
            RoundResponseWindow? window)
        {
            Meld[] melds = ValidateMelds(round.Tiles, winner, seatFacts.Melds);
            bool isChankan = window != null && window.IsChankan;
            ValidateSeatFlags(round, winner, seatFacts, isTsumo, isChankan, melds);

            PlayerHand hand = BuildHand(
                round.Tiles,
                winner,
                melds,
                isTsumo ? winTile : (TileId?)null);
            if (!isTsumo)
            {
                ValidateRonFuriten(round, winner, seatFacts, hand);
            }

            int seatWind = SeatWind(round, winner);
            bool isDealer = winner == round.DealerSeat;
            var context = new WinContext(
                winTile,
                isTsumo,
                hand.IsClosed,
                seatWind,
                round.RoundWind,
                seatFacts.IsRiichi,
                seatFacts.IsDoubleRiichi,
                seatFacts.IsIppatsu,
                seatFacts.IsRinshan,
                seatFacts.IsHaitei,
                seatFacts.IsHoutei,
                isChankan,
                seatFacts.IsTenhou,
                seatFacts.IsChiihou);

            TileId[] dora = ValidateDoraIndicators(round.Tiles);
            TileId[] ura = BuildUraIndicators(round.Tiles, dora, seatFacts.IsRiichi);

            ScoreBreakdown? breakdown;
            try
            {
                breakdown = WinningScoreEvaluator.Explain(
                    hand,
                    context,
                    dora,
                    ura,
                    isDealer,
                    round.HonbaSticks,
                    round.RiichiSticks,
                    isTsumo ? (int?)null : loser);
            }
            catch (OverflowException overflow)
            {
                throw Reject(RoundSettlementRejectionCode.ArithmeticOverflow, overflow.Message);
            }

            if (breakdown == null)
            {
                throw Reject(
                    RoundSettlementRejectionCode.NoWinningHand,
                    "The frozen evaluator found no winning decomposition or no yaku for seat " + winner + ".");
            }

            var input = new SettlementInput(
                round.Scores,
                round.DealerSeat,
                round.HonbaSticks,
                round.RiichiSticks);
            SettlementResult payments;
            try
            {
                payments = isTsumo
                    ? SettlementService.Tsumo(input, winner, breakdown.Score)
                    : SettlementService.Ron(input, winner, loser, breakdown.Score);
            }
            catch (OverflowException overflow)
            {
                throw Reject(RoundSettlementRejectionCode.ArithmeticOverflow, overflow.Message);
            }

            if (payments.RemainingRiichiSticks != 0)
            {
                throw Reject(
                    RoundSettlementRejectionCode.SettlementConservationViolation,
                    "A winning settlement must consume the riichi-stick deposit.");
            }

            if (payments.Deltas[winner] != breakdown.TotalPoints)
            {
                throw Reject(
                    RoundSettlementRejectionCode.SettlementConservationViolation,
                    "Displayed total " + breakdown.TotalPoints + " differs from the winner's actual transfer " +
                    payments.Deltas[winner] + ".");
            }

            RoundOutcome outcome = isTsumo
                ? RoundOutcome.Tsumo(winner)
                : RoundOutcome.Ron(winner, loser);
            var settlement = new RoundSettlement(
                facts.ResultId,
                outcome,
                payments.NewScores,
                payments.RemainingRiichiSticks,
                facts.CompletedAtUtc,
                facts.CompletedAtMonotonicMillis);
            RoundCommand command = isTsumo
                ? RoundCommand.Tsumo(round.RoundId, winner, settlement)
                : RoundCommand.Ron(round.RoundId, winner, settlement);
            return new RoundSettlementCalculation(
                round.RoundId,
                round.Version,
                round.SourceFingerprint,
                isTsumo ? RoundActionKind.DeclareTsumo : RoundActionKind.DeclareRon,
                settlement,
                command,
                breakdown,
                payments,
                outcome,
                context,
                winner,
                loser,
                isDealer,
                seatWind);
        }

        private static void ValidateSeatFlags(
            RoundStateMachine round,
            int winner,
            RoundSettlementSeatFacts seatFacts,
            bool isTsumo,
            bool isChankan,
            Meld[] melds)
        {
            if (seatFacts.IsDoubleRiichi && !seatFacts.IsRiichi)
            {
                throw Reject(
                    RoundSettlementRejectionCode.RiichiStateInvalid,
                    "Double riichi implies riichi.");
            }

            if (seatFacts.IsIppatsu && !seatFacts.IsRiichi)
            {
                throw Reject(
                    RoundSettlementRejectionCode.RiichiStateInvalid,
                    "Ippatsu implies riichi.");
            }

            if (seatFacts.IsRiichi && AnyOpenMeld(melds))
            {
                throw Reject(
                    RoundSettlementRejectionCode.RiichiStateInvalid,
                    "Riichi cannot be declared with an open meld.");
            }

            if (seatFacts.IsIppatsu &&
                (seatFacts.IsRinshan ||
                 round.Tiles.KanCount > 0 ||
                 round.Tiles.CountAt(TileZone.Meld) > 0 ||
                 HasEvent(round, RoundEventType.KanDeclared)))
            {
                throw Reject(
                    RoundSettlementRejectionCode.WinFlagMismatch,
                    "Ippatsu is cleared by a call/kan and cannot coexist with rinshan.");
            }

            if (seatFacts.IsTenhou && seatFacts.IsChiihou)
            {
                throw Reject(
                    RoundSettlementRejectionCode.WinFlagMismatch,
                    "A hand cannot be both tenhou and chiihou.");
            }

            if (seatFacts.IsTenhou)
            {
                DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
                if (!isTsumo ||
                    winner != round.DealerSeat ||
                    !drawn.HasValue ||
                    drawn.Value.Source != DrawSource.DealExtra)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Tenhou requires the dealer's initial 14-tile deal.");
                }

                if (round.Tiles.CountAt(TileZone.Meld) != 0 ||
                    round.Tiles.KanCount != 0 ||
                    round.Tiles.CountAt(TileZone.Discard) != 0 ||
                    HasEvent(round, RoundEventType.TileDiscarded) ||
                    HasEvent(round, RoundEventType.ResponseClaimed) ||
                    HasEvent(round, RoundEventType.KanDeclared))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Tenhou is impossible after any discard, call or kan on the table.");
                }
            }

            if (seatFacts.IsChiihou)
            {
                if (!isTsumo || winner == round.DealerSeat)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chiihou requires a non-dealer first-draw tsumo.");
                }

                if (AnyOpenMeld(melds))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chiihou requires a closed hand.");
                }

                if (round.Tiles.CountAt(TileZone.Meld) != 0 ||
                    round.Tiles.KanCount != 0)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chiihou is impossible after any call or kan on the table.");
                }

                if (!seatFacts.DiscardHistoryProvided)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chiihou requires the complete own discard history, including called-away tiles.");
                }

                if (seatFacts.DiscardHistory.Count != 0 ||
                    round.Tiles.CountAt(TileZone.Discard, winner) != 0)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chiihou requires that the winner has never discarded.");
                }

                if (!seatFacts.IsFirstTurnUninterrupted)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chiihou requires the winner's first uninterrupted turn.");
                }

                for (int i = 0; i < round.Events.Count; i++)
                {
                    RoundEvent e = round.Events[i];
                    if (e.Type == RoundEventType.ResponseClaimed ||
                        e.Type == RoundEventType.KanDeclared ||
                        (e.Type == RoundEventType.TileDiscarded && e.Seat == winner))
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.WinFlagMismatch,
                            "Chiihou is impossible after a call, kan or the winner's own discard.");
                    }
                }
            }

            if (isTsumo)
            {
                if (seatFacts.IsHoutei)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Houtei is a ron yaku, not a tsumo yaku.");
                }

                if (seatFacts.IsChankan)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chankan is a ron yaku, not a tsumo yaku.");
                }

                DrawnTileEvidence drawn = round.Tiles.DrawnTile!.Value;
                bool rinshan = drawn.Source == DrawSource.Rinshan;
                if (seatFacts.IsRinshan != rinshan)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Rinshan evidence is " + rinshan + " but the trusted record says " +
                        seatFacts.IsRinshan + ".");
                }

                bool haitei = !rinshan && round.Tiles.WallRemaining == 0;
                if (seatFacts.IsHaitei != haitei)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Haitei evidence is " + haitei + " but the trusted record says " +
                        seatFacts.IsHaitei + ".");
                }
            }
            else
            {
                if (seatFacts.IsRinshan || seatFacts.IsHaitei)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Rinshan and haitei are tsumo yaku, not ron yaku.");
                }

                if (seatFacts.IsTenhou || seatFacts.IsChiihou)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Tenhou and chiihou are tsumo yaku, not ron yaku.");
                }

                bool houtei = round.Tiles.WallRemaining == 0;
                if (seatFacts.IsHoutei != houtei)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Houtei evidence is " + houtei + " but the trusted record says " +
                        seatFacts.IsHoutei + ".");
                }

                if (seatFacts.IsChankan != isChankan)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.WinFlagMismatch,
                        "Chankan evidence is " + isChankan + " but the trusted record says " +
                        seatFacts.IsChankan + ".");
                }
            }
        }

        private static void ValidateRonFuriten(
            RoundStateMachine round,
            int winner,
            RoundSettlementSeatFacts seatFacts,
            PlayerHand hand)
        {
            if (seatFacts.IsTemporaryFuriten || seatFacts.IsRiichiFuriten)
            {
                throw Reject(
                    RoundSettlementRejectionCode.FuritenRon,
                    "The trusted record marks seat " + winner + " as temporary/riichi furiten.");
            }

            if (!seatFacts.DiscardHistoryProvided)
            {
                throw Reject(
                    RoundSettlementRejectionCode.FuritenEvidenceMissing,
                    "Ron requires the complete own discard history; missing history is not treated as empty.");
            }

            IReadOnlyList<TileId> history = seatFacts.DiscardHistory;
            IReadOnlyList<TileId> currentRiver = round.Tiles.TilesInZone(TileZone.Discard, winner);
            var seenHistory = new HashSet<int>();
            for (int i = 0; i < currentRiver.Count; i++)
            {
                if (!ContainsTile(history, currentRiver[i]))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.FuritenEvidenceMissing,
                        "Own discard history omits current river tile " + currentRiver[i] + ".");
                }
            }

            for (int i = 0; i < history.Count; i++)
            {
                if (!seenHistory.Add(history[i].Value))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.FuritenEvidenceMissing,
                        "Own discard history contains duplicate physical tile " + history[i] + ".");
                }

                TilePosition position = round.Tiles.PositionOf(history[i]);
                if (!position.IsAssigned ||
                    (position.Zone != TileZone.Discard && position.Zone != TileZone.Meld) ||
                    (position.Zone == TileZone.Discard && position.Seat != winner) ||
                    (position.Zone == TileZone.Meld && position.Seat == winner))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.FuritenEvidenceMissing,
                        "Discard-history tile " + history[i] + " is not in a legal discard/called-away position.");
                }
            }

            IReadOnlyCollection<int> waits = HandDecomposer.CalculateWaits(hand.AllClosedTiles);
            for (int i = 0; i < history.Count; i++)
            {
                int discardedType = history[i].TypeIndex;
                foreach (int wait in waits)
                {
                    if (wait == discardedType)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.FuritenRon,
                            "Own discard " + history[i] + " intersects the theoretical waits of the hand.");
                    }
                }
            }
        }

        private static bool HasEvent(RoundStateMachine round, RoundEventType type)
        {
            for (int i = 0; i < round.Events.Count; i++)
            {
                if (round.Events[i].Type == type)
                {
                    return true;
                }
            }

            return false;
        }

        private static PlayerHand BuildHand(
            TilePositionMap tiles,
            int seat,
            Meld[] melds,
            TileId? drawnTile)
        {
            IReadOnlyList<TileId> handTiles = tiles.TilesInZone(TileZone.Hand, seat);
            var closed = new List<TileId>(handTiles.Count);
            bool removedDrawn = false;
            for (int i = 0; i < handTiles.Count; i++)
            {
                TileId tile = handTiles[i];
                if (drawnTile.HasValue && !removedDrawn && tile == drawnTile.Value)
                {
                    removedDrawn = true;
                    continue;
                }

                closed.Add(tile);
            }

            if (drawnTile.HasValue && !removedDrawn)
            {
                throw Reject(
                    RoundSettlementRejectionCode.DrawnTileMismatch,
                    "Tracked drawn tile " + drawnTile.Value + " is not in seat " + seat + "'s hand.");
            }

            int expectedClosed = HandTileCount - (3 * melds.Length);
            if (closed.Count != expectedClosed)
            {
                throw Reject(
                    RoundSettlementRejectionCode.HandCountMismatch,
                    "Seat " + seat + " has " + closed.Count + " closed tiles; with " + melds.Length +
                    " meld(s) the frozen shape requires " + expectedClosed + ".");
            }

            var seen = new HashSet<int>();
            for (int i = 0; i < closed.Count; i++)
            {
                if (!seen.Add(closed[i].Value))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.DuplicatePhysicalTile,
                        "Closed hand contains duplicate physical tile " + closed[i] + ".");
                }
            }

            for (int i = 0; i < melds.Length; i++)
            {
                for (int j = 0; j < melds[i].Tiles.Count; j++)
                {
                    if (!seen.Add(melds[i].Tiles[j].Value))
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.DuplicatePhysicalTile,
                            "Meld tile " + melds[i].Tiles[j] + " duplicates another owned tile.");
                    }
                }
            }

            try
            {
                return new PlayerHand(closed, melds, drawnTile);
            }
            catch (ArgumentException invalid)
            {
                throw Reject(RoundSettlementRejectionCode.HandShapeInvalid, invalid.Message);
            }
        }

        private static Meld[] ValidateMelds(
            TilePositionMap tiles,
            int seat,
            IReadOnlyList<Meld> melds)
        {
            if (melds.Count > 4)
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldGroupMismatch,
                    "Seat " + seat + " has more than four melds.");
            }

            List<MeldSlotGroup> groups = ReadMeldGroups(tiles, seat);
            if (groups.Count != melds.Count)
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldGroupMismatch,
                    "Meld-zone groups (" + groups.Count + ") do not match trusted meld count (" +
                    melds.Count + ") for seat " + seat + ".");
            }

            var used = new bool[groups.Count];
            var result = new Meld[melds.Count];
            for (int i = 0; i < melds.Count; i++)
            {
                Meld meld = melds[i];
                ValidateMeldShape(meld);
                int expected = meld.Type == MeldType.Chi || meld.Type == MeldType.Pon ? 3 : 4;
                if (meld.Tiles.Count != expected)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldGroupMismatch,
                        "Meld " + meld.Type + " must carry " + expected + " tiles.");
                }

                int found = -1;
                for (int group = 0; group < groups.Count; group++)
                {
                    if (!used[group] &&
                        groups[group].Tiles.Count == expected &&
                        SamePhysicalTiles(groups[group].Tiles, meld.Tiles))
                    {
                        found = group;
                        break;
                    }
                }

                if (found < 0)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldGroupMismatch,
                        "Trusted meld " + meld.Type + " is not backed by an exact Meld-zone group for seat " + seat + ".");
                }

                used[found] = true;
                ValidateMeldOwnership(meld, seat);
                result[i] = meld;
            }

            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i])
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldGroupMismatch,
                        "Meld-zone group for seat " + seat + " has no trusted meld record.");
                }
            }

            return result;
        }

        private static List<MeldSlotGroup> ReadMeldGroups(TilePositionMap tiles, int seat)
        {
            var groups = new List<MeldSlotGroup>();
            for (int baseSlot = 0; baseSlot < 16; baseSlot += 4)
            {
                var slots = new List<TileId>();
                int count = 0;
                while (count < 4 &&
                       tiles.TryGetTileAt(TilePosition.Meld(seat, baseSlot + count), out TileId tile))
                {
                    slots.Add(tile);
                    count++;
                }

                if (count == 0)
                {
                    continue;
                }

                for (int extra = count + 1; extra < 4; extra++)
                {
                    if (tiles.TryGetTileAt(TilePosition.Meld(seat, baseSlot + extra), out _))
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.MeldGroupMismatch,
                            "Meld-zone group at seat " + seat + " slot " + baseSlot + " has a gap.");
                    }
                }

                if (count != 3 && count != 4)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldGroupMismatch,
                        "Meld-zone group at seat " + seat + " slot " + baseSlot +
                        " has " + count + " tiles; only 3 or 4 are legal.");
                }

                groups.Add(new MeldSlotGroup(slots));
            }

            return groups;
        }

        private static void ValidateMeldShape(Meld meld)
        {
            if (!Enum.IsDefined(typeof(MeldType), meld.Type))
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldTypeMismatch,
                    "Undefined meld type value " + (int)meld.Type + ".");
            }

            int expected = meld.Type == MeldType.Chi || meld.Type == MeldType.Pon ? 3 : 4;
            if (meld.Tiles.Count != expected)
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldTypeMismatch,
                    "Meld " + meld.Type + " requires " + expected + " tiles, got " + meld.Tiles.Count + ".");
            }

            if (meld.Type == MeldType.Chi)
            {
                var indices = new int[meld.Tiles.Count];
                for (int i = 0; i < meld.Tiles.Count; i++)
                {
                    TileId tile = meld.Tiles[i];
                    if (!tile.IsNumberTile)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.MeldTypeMismatch,
                            "Chi cannot contain an honor tile: " + tile + ".");
                    }

                    indices[i] = tile.TypeIndex;
                }

                Array.Sort(indices);
                int suit = indices[0] / 9;
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] / 9 != suit)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.MeldTypeMismatch,
                            "Chi must contain tiles of one suit.");
                    }

                    if (i > 0 && indices[i] != indices[i - 1] + 1)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.MeldTypeMismatch,
                            "Chi tiles must be consecutive ranks.");
                    }
                }

                return;
            }

            int firstType = meld.Tiles[0].TypeIndex;
            for (int i = 1; i < meld.Tiles.Count; i++)
            {
                if (meld.Tiles[i].TypeIndex != firstType)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldTypeMismatch,
                        "Pon/kan tiles must share one tile type.");
                }
            }
        }

        private static void ValidateMeldOwnership(Meld meld, int seat)
        {
            if (!ContainsTile(meld.Tiles, meld.CalledTile))
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldTileMismatch,
                    "Called tile " + meld.CalledTile + " is not part of the meld.");
            }

            if (meld.Type == MeldType.ClosedKan)
            {
                if (meld.FromPlayer != -1)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.MeldSeatMismatch,
                        "A closed kan must use fromPlayer=-1.");
                }

                return;
            }

            if (meld.FromPlayer < 0 || meld.FromPlayer > 3)
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldSeatMismatch,
                    "An open meld must name a fromPlayer in 0..3.");
            }

            if (meld.FromPlayer == seat)
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldSeatMismatch,
                    "Seat " + seat + " cannot call its own tile.");
            }

            if (meld.Type == MeldType.Chi && meld.FromPlayer != ((seat + 3) % 4))
            {
                throw Reject(
                    RoundSettlementRejectionCode.MeldSeatMismatch,
                    "Chi can only be called from the previous seat.");
            }
        }

        private static TileId[] ValidateDoraIndicators(TilePositionMap tiles)
        {
            IReadOnlyList<TileId> indicators = tiles.DoraIndicators;
            if (indicators.Count > 5)
            {
                throw Reject(
                    RoundSettlementRejectionCode.DoraIndicatorMismatch,
                    "More than five dora indicators are recorded.");
            }

            var result = new TileId[indicators.Count];
            var seen = new HashSet<int>();
            for (int i = 0; i < indicators.Count; i++)
            {
                TileId indicator = indicators[i];
                if (!seen.Add(indicator.Value))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.DoraIndicatorMismatch,
                        "Dora indicator " + indicator + " is recorded twice.");
                }

                int slot = 4 + (2 * i);
                if (slot > 13 ||
                    !tiles.TryGetTileAt(TilePosition.DeadWall(slot), out TileId actual) ||
                    actual != indicator)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.DoraIndicatorMismatch,
                        "Dora indicator " + indicator + " is not at dead-wall slot " + slot + ".");
                }

                result[i] = indicator;
            }

            return result;
        }

        private static TileId[] BuildUraIndicators(
            TilePositionMap tiles,
            TileId[] doraIndicators,
            bool isRiichi)
        {
            if (!isRiichi || doraIndicators.Length == 0)
            {
                return Array.Empty<TileId>();
            }

            var ura = new TileId[doraIndicators.Length];
            for (int i = 0; i < doraIndicators.Length; i++)
            {
                int slot = 5 + (2 * i);
                if (slot > 13 ||
                    !tiles.TryGetTileAt(TilePosition.DeadWall(slot), out TileId actual))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.UraIndicatorUnavailable,
                        "Ura-dora slot " + slot + " is unavailable for a riichi hand.");
                }

                ura[i] = actual;
            }

            return ura;
        }

        private static void ValidateAbortiveReason(
            RoundStateMachine round,
            RoundSettlementFacts facts,
            SettlementAbortiveDrawReason reason)
        {
            if (reason == SettlementAbortiveDrawReason.FourWinds)
            {
                if (facts.Seats.Count != 4)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Four-winds abort requires trusted first-discard facts for all four seats.");
                }

                if (round.Tiles.CountAt(TileZone.Meld) != 0 ||
                    round.Tiles.KanCount != 0 ||
                    HasEvent(round, RoundEventType.ResponseClaimed) ||
                    HasEvent(round, RoundEventType.KanDeclared))
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Four-winds abort requires an uninterrupted first go with no calls or kans.");
                }

                int windType = -1;
                for (int seat = 0; seat < 4; seat++)
                {
                    RoundSettlementSeatFacts seatFacts = RequireSeat(facts, seat);
                    if (!seatFacts.FirstDiscard.HasValue || !seatFacts.IsFirstTurnUninterrupted)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.AbortiveReasonMismatch,
                            "Seat " + seat + " lacks a trusted first discard / uninterrupted first-go fact.");
                    }

                    TileId first = seatFacts.FirstDiscard.Value;
                    int type = first.TypeIndex;
                    if (type < 27 || type > 30)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.AbortiveReasonMismatch,
                            "Seat " + seat + " first discard " + first + " is not a wind.");
                    }

                    if (windType < 0)
                    {
                        windType = type;
                    }
                    else if (type != windType)
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.AbortiveReasonMismatch,
                            "Four-winds abort requires all four first discards to share one wind.");
                    }

                    TilePosition position = round.Tiles.PositionOf(first);
                    if (!position.IsAssigned ||
                        (position.Zone != TileZone.Discard && position.Zone != TileZone.Meld) ||
                        (position.Zone == TileZone.Discard && position.Seat != seat) ||
                        (position.Zone == TileZone.Meld && position.Seat == seat))
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.AbortiveReasonMismatch,
                            "First discard " + first + " is not in a legal discard or called-away meld slot.");
                    }
                }

                return;
            }

            if (reason == SettlementAbortiveDrawReason.FourKans)
            {
                if (facts.Seats.Count != 4)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Four-kans abort requires trusted meld facts for all four seats.");
                }

                if (round.Tiles.KanCount != 4)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Four-kans abort requires KanCount=4, got " + round.Tiles.KanCount + ".");
                }

                int totalKans = 0;
                int seatsWithKan = 0;
                for (int seat = 0; seat < 4; seat++)
                {
                    RoundSettlementSeatFacts seatFacts = RequireSeat(facts, seat);
                    Meld[] melds = ValidateMelds(round.Tiles, seat, seatFacts.Melds);
                    int seatKans = 0;
                    for (int i = 0; i < melds.Length; i++)
                    {
                        if (melds[i].IsKan)
                        {
                            seatKans++;
                        }
                    }

                    if (seatKans > 0)
                    {
                        seatsWithKan++;
                    }

                    totalKans += seatKans;
                }

                if (totalKans != 4 || seatsWithKan < 2)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Four-kans abort requires four kans owned by at least two different seats.");
                }

                return;
            }

            if (reason == SettlementAbortiveDrawReason.FourRiichi)
            {
                if (facts.Seats.Count != 4)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Four-riichi abort requires trusted facts for all four seats.");
                }

                for (int seat = 0; seat < 4; seat++)
                {
                    RoundSettlementSeatFacts seatFacts = RequireSeat(facts, seat);
                    Meld[] melds = ValidateMelds(round.Tiles, seat, seatFacts.Melds);
                    if (!seatFacts.IsRiichi || AnyOpenMeld(melds))
                    {
                        throw Reject(
                            RoundSettlementRejectionCode.AbortiveReasonMismatch,
                            "Four-riichi abort requires four closed riichi hands; seat " + seat + " is not one.");
                    }
                }

                return;
            }

            if (reason == SettlementAbortiveDrawReason.NineTerminals)
            {
                int declarer = round.CurrentSeat;
                RoundSettlementSeatFacts seatFacts = RequireSeat(facts, declarer);
                if (!seatFacts.IsFirstTurnUninterrupted)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Nine-terminals abort requires the declarer's first uninterrupted turn.");
                }

                if (!seatFacts.DiscardHistoryProvided || seatFacts.DiscardHistory.Count != 0)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Nine-terminals abort requires a complete, empty own discard history.");
                }

                if (round.Tiles.CountAt(TileZone.Meld) != 0 ||
                    round.Tiles.KanCount != 0 ||
                    round.Tiles.CountAt(TileZone.Discard, declarer) != 0)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Nine-terminals abort is impossible after a call, kan or the declarer's discard.");
                }

                Meld[] melds = ValidateMelds(round.Tiles, declarer, seatFacts.Melds);
                if (melds.Length != 0)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Nine-terminals abort requires a closed hand.");
                }

                IReadOnlyList<TileId> handTiles = round.Tiles.TilesInZone(TileZone.Hand, declarer);
                if (handTiles.Count != HandTileCount && handTiles.Count != HandTileCount + 1)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Nine-terminals declarer must hold 13 or 14 tiles.");
                }

                var terminalTypes = new HashSet<int>();
                for (int i = 0; i < handTiles.Count; i++)
                {
                    TileId tile = handTiles[i];
                    if (tile.IsTerminalOrHonor)
                    {
                        terminalTypes.Add(tile.TypeIndex);
                    }
                }

                if (terminalTypes.Count < 9)
                {
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Nine-terminals declarer holds only " + terminalTypes.Count +
                        " distinct terminal/honor types.");
                }

                return;
            }
        }

        private static string ReasonCode(SettlementAbortiveDrawReason reason)
        {
            switch (reason)
            {
                case SettlementAbortiveDrawReason.FourWinds:
                    return "FOUR_WINDS";
                case SettlementAbortiveDrawReason.FourKans:
                    return "FOUR_KANS";
                case SettlementAbortiveDrawReason.FourRiichi:
                    return "FOUR_RIIICHI";
                case SettlementAbortiveDrawReason.NineTerminals:
                    return "NINE_TERMINALS";
                default:
                    throw Reject(
                        RoundSettlementRejectionCode.AbortiveReasonMismatch,
                        "Unsupported abortive-draw reason " + reason + ".");
            }
        }

        private static int SeatWind(RoundStateMachine round, int seat)
        {
            return ((seat - round.DealerSeat + 4) % 4) + 1;
        }

        private static RoundSettlementSeatFacts RequireSeat(
            RoundSettlementFacts facts,
            int seat)
        {
            return facts.FindSeat(seat) ??
                   throw Reject(
                       RoundSettlementRejectionCode.SeatFactMissing,
                       "No trusted hand facts were supplied for seat " + seat + ".");
        }

        private static void EnsureUsable(RoundStateMachine round)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (round.IsFinished || round.Result != null)
            {
                throw Reject(
                    RoundSettlementRejectionCode.RoundFinished,
                    "Round " + round.RoundId + " is already finished and cannot be settled again.");
            }
        }

        private static void EnsureNotAbortiveFacts(RoundSettlementFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (facts.AbortiveDrawReason.HasValue)
            {
                throw Reject(
                    RoundSettlementRejectionCode.AbortiveReasonUnexpected,
                    "A win/exhaustive-draw calculation cannot carry an abortive-draw reason.");
            }
        }

        private static bool AnyOpenMeld(Meld[] melds)
        {
            for (int i = 0; i < melds.Length; i++)
            {
                if (melds[i].IsOpen)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsSeat(IReadOnlyList<int> seats, int seat)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i] == seat)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsTile(IReadOnlyList<TileId> tiles, TileId tile)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == tile)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SamePhysicalTiles(IReadOnlyList<TileId> left, IReadOnlyList<TileId> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            var set = new HashSet<int>();
            for (int i = 0; i < left.Count; i++)
            {
                set.Add(left[i].Value);
            }

            if (set.Count != left.Count)
            {
                return false;
            }

            for (int i = 0; i < right.Count; i++)
            {
                if (!set.Remove(right[i].Value))
                {
                    return false;
                }
            }

            return set.Count == 0;
        }

        private static RoundSettlementRejectedException Reject(
            RoundSettlementRejectionCode code,
            string message)
        {
            return new RoundSettlementRejectedException(code, message);
        }

        private sealed class MeldSlotGroup
        {
            internal MeldSlotGroup(IReadOnlyList<TileId> tiles)
            {
                Tiles = tiles;
            }

            internal IReadOnlyList<TileId> Tiles { get; }
        }
    }
}

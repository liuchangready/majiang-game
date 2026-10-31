using System;
using System.Collections.Generic;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// P5.4b-B2-T: the service's trusted N-2 host-fact source.
    ///
    /// The actor owns every provable settlement input: hand/meld tiles, discard history,
    /// riichi/double-riichi, rinshan/haitei/houtei/chankan, tenhou/chiihou and tenpai come
    /// from the exact committed Domain round. This port supplies only the facts the frozen
    /// round model does not carry:
    ///  - temporary/riichi furiten come from the actor's own committed
    ///    <see cref="TableFuritenSnapshot"/> (advanced on the actor consumer, installed after
    ///    the same durable commit, restored from the persisted projection facts);
    ///  - the ippatsu window is a pure replay of the committed riichi/draw/discard/call event
    ///    chain (the same fact the local host derives), never a default.
    ///
    /// A missing/mismatched round or furiten snapshot fails closed: the request is rejected
    /// as <c>SETTLEMENT_FACTS_UNAVAILABLE</c> instead of silently asserting "false" facts and
    /// allowing an illegal win. The port echoes the exact source reference; the actor's fact
    /// builder re-validates round id/version/fingerprint before any calculation.
    /// </summary>
    public sealed class TableServiceSettlementFactsPort : ITableSettlementFactsPort
    {
        private Func<RoundStateMachine?>? _roundProvider;
        private Func<TableFuritenSnapshot?>? _furitenProvider;

        /// <summary>
        /// Binds the port to the room's live actor readers. Called immediately after the actor
        /// is constructed; the actor never resolves facts during construction.
        /// </summary>
        internal void Bind(
            Func<RoundStateMachine?> roundProvider,
            Func<TableFuritenSnapshot?> furitenProvider)
        {
            _roundProvider = roundProvider ?? throw new ArgumentNullException(nameof(roundProvider));
            _furitenProvider = furitenProvider ?? throw new ArgumentNullException(nameof(furitenProvider));
        }

        public TableSettlementHostFacts Resolve(TableSettlementSourceRef source, TableSettlementIntent intent)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            RoundStateMachine? round = _roundProvider?.Invoke();
            if (round == null)
            {
                throw new InvalidOperationException(
                    "SERVICE_SETTLEMENT_ROUND_UNAVAILABLE: the authoritative round reader is not bound.");
            }

            if (!string.Equals(round.RoundId, source.RoundId, StringComparison.Ordinal) ||
                round.Version != source.Version ||
                round.SourceFingerprint != source.Fingerprint)
            {
                throw new InvalidOperationException(
                    "SERVICE_SETTLEMENT_SOURCE_MISMATCH: the host facts were requested for " +
                    source + " but the committed round is " + round.RoundId + "@" + round.Version);
            }

            TableFuritenSnapshot? furiten = _furitenProvider?.Invoke();
            if (furiten == null)
            {
                throw new InvalidOperationException(
                    "SERVICE_SETTLEMENT_FURITEN_UNAVAILABLE: no committed furiten snapshot is available.");
            }

            if (!string.Equals(furiten.RoundId, round.RoundId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SERVICE_SETTLEMENT_FURITEN_ROUND_MISMATCH: the furiten snapshot belongs to '" +
                    furiten.RoundId + "', not '" + round.RoundId + "'.");
            }

            return TableServiceSettlementFacts.Build(source, round, furiten);
        }
    }

    /// <summary>
    /// P5.4b-B2-T: derives an abortive-draw reason that the committed round can prove. The
    /// actor's frozen calculator re-validates the reason against the exact source before any
    /// terminal is written, so this observation can only narrow the action, never widen it.
    /// A reason that is not provable returns null and the mapper fails closed instead of
    /// guessing one.
    /// </summary>
    public static class TableServiceAbortiveDraw
    {
        public static SettlementAbortiveDrawReason? TryDerive(RoundStateMachine round, int seat)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (round.CurrentSeat != seat ||
                (round.Phase != RoundPhase.CheatWindow && round.Phase != RoundPhase.Discard))
            {
                return null;
            }

            if (IsNineTerminals(round, seat))
            {
                return SettlementAbortiveDrawReason.NineTerminals;
            }

            if (AllSeatsRiichi(round))
            {
                return SettlementAbortiveDrawReason.FourRiichi;
            }

            if (round.Tiles.KanCount == 4 && KansSpanAtLeastTwoSeats(round))
            {
                return SettlementAbortiveDrawReason.FourKans;
            }

            if (FourWindsAlreadyDiscarded(round))
            {
                return SettlementAbortiveDrawReason.FourWinds;
            }

            return null;
        }

        private static bool IsNineTerminals(RoundStateMachine round, int seat)
        {
            if (!IsFirstTurnUninterrupted(round) ||
                round.Tiles.CountAt(TileZone.Discard, seat) != 0 ||
                round.Tiles.CountAt(TileZone.Meld) != 0 ||
                round.Tiles.KanCount != 0)
            {
                return false;
            }

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count != 13 && hand.Count != 14)
            {
                return false;
            }

            var terminalTypes = new HashSet<int>();
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].IsTerminalOrHonor)
                {
                    terminalTypes.Add(hand[i].TypeIndex);
                }
            }

            return terminalTypes.Count >= 9;
        }

        private static bool AllSeatsRiichi(RoundStateMachine round)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                if (!round.IsRiichi(seat))
                {
                    return false;
                }
            }

            for (int i = 0; i < round.Melds.Count; i++)
            {
                if (round.Melds[i].Kind != RoundMeldKind.ClosedKan)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool KansSpanAtLeastTwoSeats(RoundStateMachine round)
        {
            int total = 0;
            int seats = 0;
            for (int seat = 0; seat < 4; seat++)
            {
                int own = 0;
                for (int i = 0; i < round.Melds.Count; i++)
                {
                    RoundMeld meld = round.Melds[i];
                    if (meld.Seat == seat &&
                        (meld.Kind == RoundMeldKind.OpenKan ||
                         meld.Kind == RoundMeldKind.ClosedKan ||
                         meld.Kind == RoundMeldKind.AddedKan))
                    {
                        own++;
                    }
                }

                total += own;
                if (own > 0)
                {
                    seats++;
                }
            }

            return total == 4 && seats >= 2;
        }

        private static bool FourWindsAlreadyDiscarded(RoundStateMachine round)
        {
            if (round.Tiles.CountAt(TileZone.Meld) != 0 || round.Tiles.KanCount != 0)
            {
                return false;
            }

            int windType = -1;
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                if (river.Count != 1)
                {
                    return false;
                }

                TileId first = river[0];
                if (first.TypeIndex < 27 || first.TypeIndex > 30)
                {
                    return false;
                }

                if (windType < 0)
                {
                    windType = first.TypeIndex;
                }
                else if (first.TypeIndex != windType)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFirstTurnUninterrupted(RoundStateMachine round)
        {
            if (round.Tiles.CountAt(TileZone.Meld) != 0 || round.Tiles.KanCount != 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.KanDeclared ||
                    value.Type == RoundEventType.ChiDeclared ||
                    value.Type == RoundEventType.PonDeclared ||
                    (value.Type == RoundEventType.ResponseClaimed &&
                     !string.Equals(value.Detail, "PASS", StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Builds the host-fact set from exactly one committed round plus the actor's committed
    /// furiten snapshot. Exactly four seat records are always emitted; no seat is defaulted.
    /// </summary>
    public static class TableServiceSettlementFacts
    {
        public static TableSettlementHostFacts Build(
            TableSettlementSourceRef source,
            RoundStateMachine round,
            TableFuritenSnapshot furiten)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (furiten == null)
            {
                throw new ArgumentNullException(nameof(furiten));
            }

            if (!source.Matches(round))
            {
                throw new InvalidOperationException(
                    "SERVICE_SETTLEMENT_SOURCE_MISMATCH: host facts are not bound to the committed round.");
            }

            if (!string.Equals(furiten.RoundId, round.RoundId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SERVICE_SETTLEMENT_FURITEN_ROUND_MISMATCH: furiten snapshot round '" +
                    furiten.RoundId + "' is not '" + round.RoundId + "'.");
            }

            var seats = new List<TableSettlementSeatHostFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                seats.Add(new TableSettlementSeatHostFacts(
                    seat,
                    isIppatsu: IsIppatsu(round, seat),
                    isTemporaryFuriten: furiten.IsTemporary(seat),
                    isRiichiFuriten: furiten.IsRiichi(seat)));
            }

            return new TableSettlementHostFacts(source, seats);
        }

        /// <summary>
        /// The ippatsu window, replayed from the exact committed event chain: a riichi
        /// declaration must exist; no real call/kan may have happened at or after it; the
        /// seat must not have discarded beyond the declaration discard; and at most one own
        /// draw may have followed the declaration. A missing declaration is a proved false,
        /// not a default — every input is in the round's own event log.
        /// </summary>
        public static bool IsIppatsu(RoundStateMachine round, int seat)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            int riichiAt = -1;
            int lastDiscard = -1;
            int lastCallOrKan = -1;
            int drawsAfterRiichi = 0;
            IReadOnlyList<RoundEvent> events = round.Events;
            for (int i = 0; i < events.Count; i++)
            {
                RoundEvent value = events[i];
                if (value.Type == RoundEventType.RiichiDeclared && value.Seat == seat)
                {
                    riichiAt = i;
                    drawsAfterRiichi = 0;
                }
                else if (value.Seat == seat && value.Type == RoundEventType.TileDrawn)
                {
                    if (riichiAt >= 0)
                    {
                        drawsAfterRiichi++;
                    }
                }
                else if (value.Seat == seat && value.Type == RoundEventType.TileDiscarded)
                {
                    lastDiscard = i;
                }
                else if (IsRealCallOrKan(value))
                {
                    lastCallOrKan = i;
                }
            }

            if (riichiAt < 0 || lastCallOrKan >= riichiAt)
            {
                return false;
            }

            int declarationDiscard = -1;
            for (int i = riichiAt + 1; i < events.Count; i++)
            {
                if (events[i].Type == RoundEventType.TileDiscarded && events[i].Seat == seat)
                {
                    declarationDiscard = i;
                    break;
                }
            }

            if (lastDiscard >= 0 && lastDiscard != declarationDiscard)
            {
                return false;
            }

            if (drawsAfterRiichi > 1)
            {
                return false;
            }

            return declarationDiscard < 0 || lastDiscard <= declarationDiscard;
        }

        private static bool IsRealCallOrKan(RoundEvent value)
        {
            if (value.Type == RoundEventType.KanDeclared ||
                value.Type == RoundEventType.ChiDeclared ||
                value.Type == RoundEventType.PonDeclared)
            {
                return true;
            }

            return value.Type == RoundEventType.ResponseClaimed &&
                   !string.Equals(value.Detail, "PASS", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// P5.4b-B2-T: trusted observation used only to widen the response-window eligible set.
    /// A seat whose closed hand plus the discarded tile is a complete hand must be inside the
    /// window; otherwise a ron opportunity would be silently dropped before the actor could
    /// ever see it. This is the Domain's own hand-shape predicate, not a second scorer; the
    /// actor still re-validates yaku, furiten and payments through the frozen calculator.
    /// </summary>
    public static class TableServiceRonEligibility
    {
        /// <summary>
        /// True when the committed draw turn's own hand is a complete yaku-bearing tsumo for
        /// this seat. This is the frozen evaluator's own predicate; the actor still recomputes
        /// every scoring fact and rejects an illegal declaration. It is used only to advertise
        /// an executable candidate, never to build a settlement body.
        /// </summary>
        public static bool CanTsumo(RoundStateMachine round, int seat)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if ((round.Phase != RoundPhase.CheatWindow && round.Phase != RoundPhase.Discard) ||
                round.CurrentSeat != seat)
            {
                return false;
            }

            DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
            if (!drawn.HasValue || drawn.Value.Seat != seat)
            {
                return false;
            }

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count != 14)
            {
                return false;
            }

            try
            {
                var playerHand = new Mahjong.Domain.Rules.PlayerHand(
                    hand,
                    BuildMelds(round, seat),
                    null);
                var context = new Mahjong.Domain.Rules.WinContext(
                    drawn.Value.Tile,
                    isTsumo: true,
                    isClosed: playerHand.IsClosed,
                    seatWind: ((seat - round.DealerSeat + 4) % 4) + 1,
                    roundWind: round.RoundWind,
                    isRiichi: round.IsRiichi(seat),
                    isIppatsu: TableServiceSettlementFacts.IsIppatsu(round, seat),
                    isRinshan: drawn.Value.Source == DrawSource.Rinshan,
                    isHaitei: drawn.Value.Source != DrawSource.Rinshan &&
                              round.Tiles.WallRemaining == 0,
                    isTenhou: IsTenhou(round, seat, drawn.Value),
                    isChiihou: IsChiihou(round, seat, drawn.Value));
                return Mahjong.Domain.Rules.WinningScoreEvaluator.CanWinWithYaku(playerHand, context);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static bool HandCanRon(RoundStateMachine round, int seat, TileId target, bool isChankan = false)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count == 0)
            {
                return false;
            }

            try
            {
                Mahjong.Domain.Rules.PlayerHand playerHand = new Mahjong.Domain.Rules.PlayerHand(
                    hand,
                    BuildMelds(round, seat),
                    null);
                var context = new Mahjong.Domain.Rules.WinContext(
                    target,
                    isTsumo: false,
                    isClosed: playerHand.IsClosed,
                    seatWind: ((seat - round.DealerSeat + 4) % 4) + 1,
                    roundWind: round.RoundWind,
                    isRiichi: round.IsRiichi(seat),
                    isIppatsu: TableServiceSettlementFacts.IsIppatsu(round, seat),
                    isHoutei: round.Tiles.WallRemaining == 0,
                    isChankan: isChankan);
                return Mahjong.Domain.Rules.WinningScoreEvaluator.CanWinWithYaku(playerHand, context);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the exact committed response window offers this seat a Ron that the
        /// frozen evaluator would accept as a yaku-bearing hand and the actor's own committed
        /// furiten snapshot does not forbid. The actor still re-verifies every fact and score;
        /// this is the advertisement/furiten observation, never a settlement body.
        /// </summary>
        public static bool CanRon(
            RoundStateMachine round,
            TableFuritenSnapshot? furiten,
            int seat)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            RoundResponseWindow? window = round.Window;
            if (window == null ||
                round.Phase != (window.IsChankan ? RoundPhase.ChankanWindow : RoundPhase.ResponseWindow) ||
                seat == window.FromSeat ||
                !ContainsSeat(window.EligibleSeats, seat) ||
                window.HasResponded(seat))
            {
                return false;
            }

            if (furiten == null ||
                !string.Equals(furiten.RoundId, round.RoundId, StringComparison.Ordinal) ||
                furiten.IsFuriten(seat))
            {
                return false;
            }

            return HandCanRon(round, seat, window.TargetTile, window.IsChankan);
        }

        private static bool IsTenhou(RoundStateMachine round, int seat, DrawnTileEvidence drawn)
        {
            if (seat != round.DealerSeat || drawn.Source != DrawSource.DealExtra)
            {
                return false;
            }

            if (round.Tiles.CountAt(TileZone.Discard) != 0 ||
                round.Tiles.CountAt(TileZone.Meld) != 0 ||
                round.Tiles.KanCount != 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (value.Type == RoundEventType.TileDiscarded || IsRealCallOrKan(value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsChiihou(RoundStateMachine round, int seat, DrawnTileEvidence drawn)
        {
            if (seat == round.DealerSeat ||
                round.Tiles.CountAt(TileZone.Discard, seat) != 0 ||
                round.Tiles.CountAt(TileZone.Meld) != 0 ||
                round.Tiles.KanCount != 0)
            {
                return false;
            }

            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEvent value = round.Events[i];
                if (IsRealCallOrKan(value) ||
                    (value.Type == RoundEventType.TileDiscarded && value.Seat == seat))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsRealCallOrKan(RoundEvent value)
        {
            return value.Type == RoundEventType.KanDeclared ||
                   value.Type == RoundEventType.ChiDeclared ||
                   value.Type == RoundEventType.PonDeclared ||
                   (value.Type == RoundEventType.ResponseClaimed &&
                    !string.Equals(value.Detail, "PASS", StringComparison.Ordinal));
        }

        private static Mahjong.Domain.Rules.Meld[] BuildMelds(RoundStateMachine round, int seat)
        {
            var melds = new List<Mahjong.Domain.Rules.Meld>();
            for (int i = 0; i < round.Melds.Count; i++)
            {
                RoundMeld meld = round.Melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                melds.Add(new Mahjong.Domain.Rules.Meld(
                    meld.Kind == RoundMeldKind.Chi ? Mahjong.Domain.Rules.MeldType.Chi
                        : meld.Kind == RoundMeldKind.Pon ? Mahjong.Domain.Rules.MeldType.Pon
                        : meld.Kind == RoundMeldKind.OpenKan ? Mahjong.Domain.Rules.MeldType.OpenKan
                        : meld.Kind == RoundMeldKind.ClosedKan ? Mahjong.Domain.Rules.MeldType.ClosedKan
                        : Mahjong.Domain.Rules.MeldType.AddedKan,
                    meld.Tiles,
                    meld.Kind == RoundMeldKind.ClosedKan ? -1 : meld.FromSeat,
                    meld.CalledTile));
            }

            return melds.ToArray();
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
    }
}

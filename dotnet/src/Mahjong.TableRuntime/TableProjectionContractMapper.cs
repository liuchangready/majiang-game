using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Explicit monotonic time context for turning an absolute offer deadline into a relative
    /// remaining duration. The mapper never reads a system clock; an actor/port must supply this
    /// value. Absolute monotonic values are never sent on the wire.
    /// </summary>
    public sealed class TableProjectionTimeContext
    {
        public TableProjectionTimeContext(long monotonicNowMs)
        {
            if (monotonicNowMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicNowMs), monotonicNowMs, "A monotonic time cannot be negative.");
            }

            MonotonicNowMs = monotonicNowMs;
        }

        public long MonotonicNowMs { get; }
    }

    /// <summary>
    /// A public, identity-free closed-kan fact. The public Contracts view intentionally omits a
    /// closed kan from its meld list; this explicit input supplies only the count so the wire can
    /// account for the meld without exposing the four physical tiles.
    /// </summary>
    public sealed class TablePublicConcealedMeldFact
    {
        public TablePublicConcealedMeldFact(int seat, uint tileCount = 4)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            if (tileCount != 4)
            {
                throw new ArgumentOutOfRangeException(nameof(tileCount), tileCount, "A closed kan occupies exactly four tiles.");
            }

            Seat = seat;
            TileCount = tileCount;
        }

        public int Seat { get; }

        public uint TileCount { get; }
    }

    /// <summary>
    /// Facts that the P3.2a typed event does not carry. They are explicit inputs from the
    /// authoritative actor/domain-event port. A missing required fact is a mapping rejection,
    /// never a zero/empty default.
    /// </summary>
    public sealed class TableProjectionEventFacts
    {
        public TableProjectionEventFacts(
            uint? turnNumber = null,
            uint? wallSize = null,
            uint? wallRemaining = null,
            MeldView? meld = null,
            int? fromSeat = null,
            bool? riichiDiscard = null,
            bool? tsumogiri = null,
            uint? discardTileId = null,
            bool? doubleRiichi = null,
            int? loserSeat = null,
            IReadOnlyList<YakuView>? yaku = null,
            uint? han = null,
            uint? fu = null,
            long? score = null,
            ScoreBreakdownView? breakdown = null,
            IReadOnlyList<int>? tenpaiPlayers = null,
            IReadOnlyList<PaymentView>? payments = null,
            IReadOnlyList<ScoreView>? newScores = null,
            IReadOnlyList<ScoreView>? finalScores = null,
            int? winnerSeat = null,
            uint? roundWind = null,
            uint? roundNumber = null,
            string? offerId = null,
            bool? keptFirst = null,
            uint? resultTileId = null,
            ProjectionBeamSwapResolution? beamSwapResolution = null,
            uint? handTileId = null,
            uint? targetSlot = null)
        {
            TurnNumber = turnNumber;
            WallSize = wallSize;
            WallRemaining = wallRemaining;
            Meld = meld;
            FromSeat = fromSeat;
            RiichiDiscard = riichiDiscard;
            Tsumogiri = tsumogiri;
            DiscardTileId = discardTileId;
            DoubleRiichi = doubleRiichi;
            LoserSeat = loserSeat;
            Yaku = yaku;
            Han = han;
            Fu = fu;
            Score = score;
            Breakdown = breakdown;
            TenpaiPlayers = tenpaiPlayers;
            Payments = payments;
            NewScores = newScores;
            FinalScores = finalScores;
            WinnerSeat = winnerSeat;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            OfferId = offerId;
            KeptFirst = keptFirst;
            ResultTileId = resultTileId;
            BeamSwapResolution = beamSwapResolution;
            HandTileId = handTileId;
            TargetSlot = targetSlot;
        }

        public uint? TurnNumber { get; }

        public uint? WallSize { get; }

        public uint? WallRemaining { get; }

        public MeldView? Meld { get; }

        public int? FromSeat { get; }

        public bool? RiichiDiscard { get; }

        public bool? Tsumogiri { get; }

        public uint? DiscardTileId { get; }

        public bool? DoubleRiichi { get; }

        public int? LoserSeat { get; }

        public IReadOnlyList<YakuView>? Yaku { get; }

        public uint? Han { get; }

        public uint? Fu { get; }

        public long? Score { get; }

        public ScoreBreakdownView? Breakdown { get; }

        public IReadOnlyList<int>? TenpaiPlayers { get; }

        public IReadOnlyList<PaymentView>? Payments { get; }

        public IReadOnlyList<ScoreView>? NewScores { get; }

        public IReadOnlyList<ScoreView>? FinalScores { get; }

        public int? WinnerSeat { get; }

        public uint? RoundWind { get; }

        public uint? RoundNumber { get; }

        public string? OfferId { get; }

        public bool? KeptFirst { get; }

        public uint? ResultTileId { get; }

        public ProjectionBeamSwapResolution? BeamSwapResolution { get; }

        public uint? HandTileId { get; }

        public uint? TargetSlot { get; }
    }

    /// <summary>
    /// Pure P3.2a result -> Contracts mapper. It consumes only the committed projection values
    /// and explicit supplemental facts; it does not read Domain state, actors, mailboxes,
    /// streams, clocks or system time, and it does not allocate a cursor.
    /// </summary>
    public static class TableProjectionContractMapper
    {
        public static PublicSnapshotView MapPublicSnapshot(
            PublicSnapshotView source,
            IReadOnlyList<ProtocolEventRecord>? typedSettlementEvents = null,
            IReadOnlyList<TablePublicConcealedMeldFact>? concealedMeldFacts = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            PublicMatchView view = RebuildPublicMatchView(
                source.View,
                typedSettlementEvents,
                concealedMeldFacts);
            return new PublicSnapshotView(
                source.SessionId,
                source.RoundId,
                source.Cursor,
                source.StateVersion,
                source.EventSeq,
                source.SessionVersion,
                source.RoundVersion,
                view);
        }

        public static PlayerSnapshotView MapPlayerSnapshot(
            PlayerSnapshotView source,
            TableOwnerPrivateView owner,
            TableProjectionTimeContext time,
            IReadOnlyList<ProtocolEventRecord>? typedSettlementEvents = null,
            IReadOnlyList<TablePublicConcealedMeldFact>? concealedMeldFacts = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            ValidateOwnerMatchesSnapshot(source, owner);
            PrivateCheatView cheat = MapOwnerCheat(
                owner.Cheat,
                time,
                source.Private.Cheat != null && source.Private.Cheat.HasPreparation);
            var ownerMelds = new List<MeldView>(owner.Melds.Count);
            for (int i = 0; i < owner.Melds.Count; i++)
            {
                TableOwnerMeldView meld = owner.Melds[i];
                ownerMelds.Add(new MeldView(meld.Type, meld.TileIds, meld.CalledTileId, meld.FromSeat));
            }

            var privateView = new PrivatePlayerView(
                owner.Seat,
                owner.ClosedTiles,
                owner.DrawnTileId,
                owner.Furiten,
                owner.DiscardHistory,
                source.Private.Decision,
                cheat,
                owner.SleeveTiles,
                ownerMelds);

            PublicMatchView publicView = RebuildPublicMatchView(
                source.Public,
                typedSettlementEvents,
                concealedMeldFacts);

            return new PlayerSnapshotView(
                source.SessionId,
                source.RoundId,
                source.Seat,
                source.Cursor,
                source.StateVersion,
                source.EventSeq,
                source.SessionVersion,
                source.RoundVersion,
                publicView,
                privateView);
        }

        /// <summary>
        /// Maps one already-audience-filtered P3.2a event plus the explicit missing facts to a
        /// typed Contracts record. Missing required facts throw <see cref="TableProjectionException"/>
        /// with <see cref="TableProjectionErrorCode.InvalidPayload"/>.
        /// </summary>
        public static ProtocolEventRecord MapEvent(
            TableProjectedEvent source,
            TableProjectionEventFacts? facts)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            ProjectedEventPayload payload = BuildEventPayload(source, facts);
            return ProtocolEventRecord.KnownTyped(
                source.Type,
                source.Visibility,
                ProtocolConstants.EventSchemaVersion,
                source.EventSeq,
                source.CommandVersion,
                source.EventOrdinal,
                payload);
        }

        public static PrivateCheatView MapOwnerCheat(
            TableOwnerCheatView owner,
            TableProjectionTimeContext time,
            bool hasPreparation = false)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            var ledger = new List<CheatLedgerEntryView>(owner.Ledger.Count);
            for (int i = 0; i < owner.Ledger.Count; i++)
            {
                TableOwnerLedgerEntryView entry = owner.Ledger[i];
                ledger.Add(new CheatLedgerEntryView(
                    entry.EntryId,
                    entry.Amount,
                    entry.BalanceAfter,
                    entry.ReasonCode));
            }

            TileMarkView? tileMark = owner.MarkGroups.Count == 0 ? null : MapTileMark(owner.MarkGroups);
            SleeveBloomView? immediate = owner.ImmediateOffer == null
                ? null
                : MapSleeveBloom(owner.ImmediateOffer, time);
            SleeveBloomView? offTurn = owner.OffTurnOffer == null
                ? null
                : MapSleeveBloom(owner.OffTurnOffer, time);
            SleeveBloomReservationView? reservation = owner.Reservation == null
                ? null
                : new SleeveBloomReservationView(
                    owner.Reservation.OfferId,
                    owner.Reservation.TileId,
                    owner.Reservation.WindowId);
            BeamSwapView? beam = owner.BeamSwapOffer == null
                ? null
                : MapBeamSwap(owner.BeamSwapOffer);

            return new PrivateCheatView(
                owner.Balance,
                ledger,
                tileMark != null,
                immediate != null || offTurn != null || reservation != null || owner.WindowId.Length > 0,
                beam != null,
                hasPreparation,
                tileMark,
                immediate,
                offTurn,
                reservation,
                owner.WindowId,
                beam,
                null);
        }

        private static ProjectedEventPayload BuildEventPayload(
            TableProjectedEvent source,
            TableProjectionEventFacts? facts)
        {
            switch (source.Type)
            {
                case ProtocolEventType.StackingStarted:
                    return ProjectedEventPayload.StackingStarted(RequireTurn(source, facts));
                case ProtocolEventType.WallBuilt:
                    return ProjectedEventPayload.WallBuilt(
                        RequireTurn(source, facts),
                        Require(facts?.WallSize, source.Type, "wallSize"));
                case ProtocolEventType.TileDealt:
                    return ProjectedEventPayload.TileDealt(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireSingleTile(source));
                case ProtocolEventType.DealingComplete:
                    return ProjectedEventPayload.DealingComplete(RequireTurn(source, facts));
                case ProtocolEventType.TileDrawn:
                    return ProjectedEventPayload.TileDrawn(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireSingleTile(source),
                        Require(facts?.WallRemaining, source.Type, "wallRemaining"));
                case ProtocolEventType.TileDiscarded:
                    return ProjectedEventPayload.TileDiscarded(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireSingleTile(source),
                        Require(facts?.RiichiDiscard, source.Type, "riichiDiscard"),
                        Require(facts?.Tsumogiri, source.Type, "tsumogiri"));
                case ProtocolEventType.ChiDeclared:
                    return ProjectedEventPayload.ChiDeclared(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireMeld(source, facts),
                        Require(facts?.FromSeat, source.Type, "fromSeat"));
                case ProtocolEventType.PonDeclared:
                    return ProjectedEventPayload.PonDeclared(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireMeld(source, facts),
                        Require(facts?.FromSeat, source.Type, "fromSeat"));
                case ProtocolEventType.KanDeclared:
                    return ProjectedEventPayload.KanDeclared(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        SelectKanMeld(source, facts),
                        KanTypeFor(source, facts));
                case ProtocolEventType.RinshanDraw:
                    return ProjectedEventPayload.RinshanDraw(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireSingleTile(source));
                case ProtocolEventType.RiichiDeclared:
                    return ProjectedEventPayload.RiichiDeclared(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        Require(facts?.DiscardTileId, source.Type, "discardTileId"),
                        Require(facts?.DoubleRiichi, source.Type, "doubleRiichi"));
                case ProtocolEventType.TsumoWin:
                    return ProjectedEventPayload.TsumoWin(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        RequireSingleTile(source),
                        RequireYaku(source, facts),
                        Require(facts?.Han, source.Type, "han"),
                        Require(facts?.Fu, source.Type, "fu"),
                        Require(facts?.Score, source.Type, "score"),
                        facts?.Breakdown);
                case ProtocolEventType.RonWin:
                    return ProjectedEventPayload.RonWin(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        Require(facts?.LoserSeat, source.Type, "loserSeat"),
                        RequireSingleTile(source),
                        RequireYaku(source, facts),
                        Require(facts?.Han, source.Type, "han"),
                        Require(facts?.Fu, source.Type, "fu"),
                        Require(facts?.Score, source.Type, "score"),
                        facts?.Breakdown,
                        source.IsChankan);
                case ProtocolEventType.DoraRevealed:
                    return ProjectedEventPayload.DoraRevealed(
                        RequireTurn(source, facts),
                        RequireSingleTile(source),
                        source.IsUraDora,
                        source.IsUraDora && source.Visibility == ProtocolEventVisibility.SettlementPublic);
                case ProtocolEventType.CheatPointsChanged:
                    return ProjectedEventPayload.CheatPointsChanged(
                        RequireTurn(source, facts),
                        RequireSeat(source),
                        Require(source.CheatAmount, source.Type, "cheatAmount"),
                        Require(source.CheatBalance, source.Type, "cheatBalance"));
                case ProtocolEventType.DrawGame:
                    if (source.PublicReasonCode.Length == 0)
                    {
                        throw Missing(source.Type, "publicReasonCode");
                    }

                    return ProjectedEventPayload.DrawGame(
                        RequireTurn(source, facts),
                        source.PublicReasonCode,
                        RequireList(facts?.TenpaiPlayers, source.Type, "tenpaiPlayers"));
                case ProtocolEventType.ScoreSettled:
                    return ProjectedEventPayload.ScoreSettled(
                        RequireTurn(source, facts),
                        RequireList(facts?.Payments, source.Type, "payments"),
                        RequireList(facts?.NewScores, source.Type, "newScores"));
                case ProtocolEventType.RoundEnded:
                    return ProjectedEventPayload.RoundEnded(
                        RequireTurn(source, facts),
                        Require(facts?.RoundWind, source.Type, "roundWind"),
                        Require(facts?.RoundNumber, source.Type, "roundNumber"));
                case ProtocolEventType.MatchEnded:
                    return ProjectedEventPayload.MatchEnded(
                        RequireTurn(source, facts),
                        RequireList(facts?.FinalScores, source.Type, "finalScores"),
                        Require(facts?.WinnerSeat, source.Type, "winnerSeat"));
                case ProtocolEventType.SleeveBloomResolved:
                    return ProjectedEventPayload.SleeveBloomResolved(
                        RequireOwnerSeat(source),
                        RequireText(facts?.OfferId, source.Type, "offerId"),
                        Require(facts?.KeptFirst, source.Type, "keptFirst"),
                        facts?.ResultTileId);
                case ProtocolEventType.BeamSwapResolved:
                    return ProjectedEventPayload.BeamSwapResolved(
                        RequireOwnerSeat(source),
                        RequireText(facts?.OfferId, source.Type, "offerId"),
                        Require(facts?.BeamSwapResolution, source.Type, "beamSwapResolution"),
                        facts?.HandTileId,
                        facts?.TargetSlot);
                default:
                    throw new TableProjectionException(
                        TableProjectionErrorCode.UnsupportedEvent,
                        "Event type " + source.Type + " has no Contracts payload mapping.");
            }
        }

        private static TableOwnerPrivateView ValidateOwnerMatchesSnapshot(
            PlayerSnapshotView snapshot,
            TableOwnerPrivateView owner)
        {
            if (owner.Seat != snapshot.Seat)
            {
                throw Invalid("Owner view seat " + owner.Seat + " does not match snapshot seat " + snapshot.Seat + ".");
            }

            RequireSameTiles(owner.ClosedTiles, snapshot.Private.ClosedTiles, "closedTiles");
            if (owner.DrawnTileId != snapshot.Private.DrawnTileId)
            {
                throw Invalid("Owner drawn tile does not match the player snapshot.");
            }

            if (owner.Furiten != snapshot.Private.Furiten)
            {
                throw Invalid("Owner furiten does not match the player snapshot.");
            }

            RequireSameTiles(owner.DiscardHistory, snapshot.Private.DiscardHistory, "discardHistory");
            if (snapshot.Private.Cheat != null && snapshot.Private.Cheat.CheatPoints != owner.Cheat.Balance)
            {
                throw Invalid("Owner cheat balance does not match the player snapshot.");
            }

            return owner;
        }

        private static void RequireSameTiles(
            IReadOnlyList<uint> ownerTiles,
            IReadOnlyList<uint> snapshotTiles,
            string name)
        {
            if (ownerTiles.Count != snapshotTiles.Count)
            {
                throw Invalid("Owner " + name + " count does not match the player snapshot.");
            }

            for (int i = 0; i < ownerTiles.Count; i++)
            {
                if (ownerTiles[i] != snapshotTiles[i])
                {
                    throw Invalid("Owner " + name + " differs from the player snapshot at index " + i + ".");
                }
            }
        }

        private static PublicMatchView RebuildPublicMatchView(
            PublicMatchView source,
            IReadOnlyList<ProtocolEventRecord>? typedSettlementEvents,
            IReadOnlyList<TablePublicConcealedMeldFact>? concealedMeldFacts)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            IReadOnlyList<ProtocolEventRecord> settlement = MergeSettlementEvents(
                source.SettlementEvents,
                typedSettlementEvents);
            PublicBoardView? board = source.Board == null
                ? null
                : WithConcealedMelds(source.Board, concealedMeldFacts);
            return new PublicMatchView(
                board,
                source.Session,
                source.Clocks,
                source.Paused,
                source.SettlementRoundId,
                settlement);
        }

        private static IReadOnlyList<ProtocolEventRecord> MergeSettlementEvents(
            IReadOnlyList<ProtocolEventRecord> source,
            IReadOnlyList<ProtocolEventRecord>? typed)
        {
            if (source.Count == 0)
            {
                if (typed != null && typed.Count > 0)
                {
                    throw Invalid("Typed settlement events were supplied for a snapshot that carries none.");
                }

                return Array.Empty<ProtocolEventRecord>();
            }

            var bySeq = new Dictionary<ulong, ProtocolEventRecord>();
            if (typed != null)
            {
                for (int i = 0; i < typed.Count; i++)
                {
                    ProtocolEventRecord record = typed[i];
                    if (record.ProjectionPayload == null)
                    {
                        throw Invalid("Typed settlement event " + record.Type + " has no Contracts payload.");
                    }

                    if (bySeq.ContainsKey(record.EventSeq.Value))
                    {
                        throw Invalid("Typed settlement events repeat event_seq " + record.EventSeq.Value + ".");
                    }

                    bySeq.Add(record.EventSeq.Value, record);
                }
            }
            else
            {
                // A previous mapper pass may already have typed the snapshot's events. Reusing
                // them is safe only when every one carries a body.
                for (int i = 0; i < source.Count; i++)
                {
                    if (source[i].ProjectionPayload == null)
                    {
                        throw Invalid("Settlement event " + source[i].EventSeq + " has no typed Contracts body.");
                    }
                }

                return source;
            }

            var result = new List<ProtocolEventRecord>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                ProtocolEventRecord expected = source[i];
                if (!bySeq.TryGetValue(expected.EventSeq.Value, out ProtocolEventRecord? actual))
                {
                    throw Invalid("Settlement event " + expected.EventSeq + " has no typed Contracts body.");
                }

                if (actual.Type != expected.Type ||
                    actual.CommandVersion != expected.CommandVersion ||
                    actual.EventOrdinal != expected.EventOrdinal)
                {
                    throw Invalid("Settlement event " + expected.EventSeq + " does not match its source identity.");
                }

                bool settlementType = TableEventProjectionInput.IsSettlementType(actual.Type) ||
                                      (actual.Type == ProtocolEventType.DoraRevealed &&
                                       actual.ProjectionPayload!.IsUraDora);
                if (!settlementType)
                {
                    throw Invalid("Event " + actual.Type + " is not a settlement event and cannot replace a settlement body.");
                }

                result.Add(actual);
            }

            if (bySeq.Count != source.Count)
            {
                throw Invalid("Typed settlement events contain entries that are not in the snapshot.");
            }

            return new ReadOnlyCollection<ProtocolEventRecord>(result);
        }

        private static PublicBoardView WithConcealedMelds(
            PublicBoardView source,
            IReadOnlyList<TablePublicConcealedMeldFact>? facts)
        {
            var bySeat = new Dictionary<int, List<ConcealedMeldView>>();
            var seats = new HashSet<int>();
            for (int i = 0; i < source.Players.Count; i++)
            {
                seats.Add(source.Players[i].Seat);
            }

            if (facts != null)
            {
                for (int i = 0; i < facts.Count; i++)
                {
                    TablePublicConcealedMeldFact fact = facts[i];
                    if (!seats.Contains(fact.Seat))
                    {
                        throw Invalid("A concealed-meld fact targets seat " + fact.Seat + ", which is not in the public board.");
                    }

                    if (!bySeat.TryGetValue(fact.Seat, out List<ConcealedMeldView>? list))
                    {
                        list = new List<ConcealedMeldView>();
                        bySeat.Add(fact.Seat, list);
                    }

                    list.Add(new ConcealedMeldView(ProtocolMeldType.ClosedKan, fact.TileCount, ProtocolConstants.NoSeat));
                }
            }

            var players = new List<PlayerPublicView>(source.Players.Count);
            for (int i = 0; i < source.Players.Count; i++)
            {
                PlayerPublicView player = source.Players[i];
                IReadOnlyList<ConcealedMeldView>? replacement = bySeat.TryGetValue(player.Seat, out List<ConcealedMeldView>? supplied)
                    ? supplied
                    : null;
                players.Add(new PlayerPublicView(
                    player.Seat,
                    player.HandCount,
                    player.Melds,
                    player.Discards,
                    player.Riichi,
                    replacement ?? player.ConcealedMelds));
            }

            return new PublicBoardView(
                source.Phase,
                source.RoundWind,
                source.RoundNumber,
                source.Honba,
                source.RiichiSticks,
                source.DealerSeat,
                source.CurrentSeat,
                source.WallRemaining,
                source.DoraIndicators,
                source.Scores,
                players,
                source.LastDiscard,
                source.Paused);
        }

        private static TileMarkView MapTileMark(IReadOnlyList<TableOwnerMarkGroupView> groups)
        {
            var mapped = new List<TileMarkGroupView>(groups.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                TableOwnerMarkGroupView group = groups[i];
                var tiles = new List<MarkedTileView>(group.TileIds.Count);
                for (int t = 0; t < group.TileIds.Count; t++)
                {
                    // P3.2a carries identity and group generation only. Area/seat/river position
                    // are not in the owner projection, so they are explicitly absent rather than
                    // defaulted to a fabricated position.
                    tiles.Add(new MarkedTileView(group.TileIds[t], string.Empty, ProtocolConstants.NoSeat, 0));
                }

                mapped.Add(new TileMarkGroupView(
                    group.GroupId,
                    tiles,
                    group.CreatedRoundId,
                    group.CreatedRoundOrdinal,
                    group.ExpiresAfterRoundOrdinal));
            }

            return new TileMarkView(Array.Empty<uint>(), mapped);
        }

        private static SleeveBloomView MapSleeveBloom(
            TableOwnerSleeveBloomOfferView offer,
            TableProjectionTimeContext time)
        {
            bool hasDeadline = offer.DeadlineMillis > 0;
            long remaining = hasDeadline ? Remaining(offer.DeadlineMillis, time.MonotonicNowMs) : 0;
            return new SleeveBloomView(
                offer.OfferId,
                offer.Mode == Mahjong.Domain.Cheat.SleeveBloomOfferMode.Immediate
                    ? ProjectionSleeveBloomMode.Immediate
                    : ProjectionSleeveBloomMode.OffTurn,
                offer.FirstTileId,
                offer.SecondTileId,
                hasDeadline,
                remaining,
                offer.WindowId,
                null,
                unchecked((uint)offer.CreatedTurnNumber),
                MapDrawKind(offer.DrawKind));
        }

        private static BeamSwapView MapBeamSwap(TableOwnerBeamSwapOfferView offer)
        {
            var candidates = new List<BeamSwapCandidateView>(offer.Candidates.Count);
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                TableOwnerBeamSwapCandidateView candidate = offer.Candidates[i];
                candidates.Add(new BeamSwapCandidateView(
                    unchecked((uint)candidate.SlotIndex),
                    MapSlotKind(candidate.SlotKind),
                    unchecked((uint)candidate.SlotIndex),
                    candidate.TileId));
            }

            return new BeamSwapView(
                offer.OfferId,
                candidates,
                offer.EligibleHandTileIds,
                false,
                0,
                offer.DrawnTileId);
        }

        private static long Remaining(long deadlineMillis, long nowMillis)
        {
            if (deadlineMillis <= nowMillis)
            {
                return 0;
            }

            return deadlineMillis - nowMillis;
        }

        private static ProjectionSleeveBloomDrawKind MapDrawKind(Mahjong.Domain.Cheat.SleeveBloomDrawKind kind)
        {
            switch (kind)
            {
                case Mahjong.Domain.Cheat.SleeveBloomDrawKind.DealExtra: return ProjectionSleeveBloomDrawKind.DealExtra;
                case Mahjong.Domain.Cheat.SleeveBloomDrawKind.Normal: return ProjectionSleeveBloomDrawKind.Normal;
                case Mahjong.Domain.Cheat.SleeveBloomDrawKind.Rinshan: return ProjectionSleeveBloomDrawKind.Rinshan;
                default: throw new TableProjectionException(TableProjectionErrorCode.InvalidPayload, "Unknown sleeve draw kind.");
            }
        }

        private static ProjectionBeamSwapSlotKind MapSlotKind(Mahjong.Domain.Cheat.BeamSwapSlotKind kind)
        {
            switch (kind)
            {
                case Mahjong.Domain.Cheat.BeamSwapSlotKind.WallSlot: return ProjectionBeamSwapSlotKind.Wall;
                case Mahjong.Domain.Cheat.BeamSwapSlotKind.DeadWallSlot: return ProjectionBeamSwapSlotKind.DeadWall;
                case Mahjong.Domain.Cheat.BeamSwapSlotKind.ReplacementSlot: return ProjectionBeamSwapSlotKind.Replacement;
                default: throw new TableProjectionException(TableProjectionErrorCode.InvalidPayload, "Unknown beam slot kind.");
            }
        }

        private static MeldView RequireMeld(TableProjectedEvent source, TableProjectionEventFacts? facts)
        {
            MeldView? meld = facts?.Meld;
            if (meld == null)
            {
                throw Missing(source.Type, "meld");
            }

            if (meld.TileIds.Count != source.TileIds.Count)
            {
                throw Invalid("Supplemental meld tiles do not match the projected " + source.Type + " tiles.");
            }

            for (int i = 0; i < meld.TileIds.Count; i++)
            {
                if (meld.TileIds[i] != source.TileIds[i])
                {
                    throw Invalid("Supplemental meld tile differs from the projected event tile at index " + i + ".");
                }
            }

            return meld;
        }

        private static MeldView? SelectKanMeld(TableProjectedEvent source, TableProjectionEventFacts? facts)
        {
            MeldView? meld = facts?.Meld;

            // The projected tile list, not the visibility enum, is the redaction boundary: a
            // spectator/other-seat closed kan has zero identities, while the owner stream keeps
            // all four. Never let supplemental facts reintroduce identities that were redacted.
            if (source.IsClosedKan && source.TileIds.Count == 0)
            {
                if (meld != null && meld.TileIds.Count > 0)
                {
                    throw Invalid("A public closed-kan event must not carry tile identities.");
                }

                return null;
            }

            if (meld == null)
            {
                if (source.IsClosedKan || source.TileIds.Count > 0)
                {
                    throw Missing(source.Type, "meld");
                }

                return null;
            }

            if (source.TileIds.Count == 4)
            {
                if (meld.TileIds.Count != 4)
                {
                    throw Invalid("A closed kan requires all four owner tile identities.");
                }

                for (int i = 0; i < 4; i++)
                {
                    if (meld.TileIds[i] != source.TileIds[i])
                    {
                        throw Invalid("Supplemental kan tile differs from the projected owner tile at index " + i + ".");
                    }
                }
            }
            else if (source.TileIds.Count == 1)
            {
                bool found = false;
                for (int i = 0; i < meld.TileIds.Count; i++)
                {
                    if (meld.TileIds[i] == source.TileIds[0])
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw Invalid("Supplemental kan meld does not contain the projected called tile.");
                }
            }

            return meld;
        }

        private static ProtocolMeldType KanTypeFor(TableProjectedEvent source, TableProjectionEventFacts? facts)
        {
            if (source.IsClosedKan)
            {
                return ProtocolMeldType.ClosedKan;
            }

            if (facts?.Meld != null)
            {
                ProtocolMeldType type = facts.Meld.Type;
                if (type == ProtocolMeldType.OpenKan || type == ProtocolMeldType.ClosedKan || type == ProtocolMeldType.AddedKan)
                {
                    return type;
                }
            }

            throw Missing(source.Type, "meld/kanType");
        }

        private static IReadOnlyList<YakuView> RequireYaku(TableProjectedEvent source, TableProjectionEventFacts? facts)
        {
            if (facts?.Yaku == null)
            {
                throw Missing(source.Type, "yaku");
            }

            return facts.Yaku;
        }

        private static uint RequireTurn(TableProjectedEvent source, TableProjectionEventFacts? facts)
        {
            if (source.Type == ProtocolEventType.SleeveBloomResolved ||
                source.Type == ProtocolEventType.BeamSwapResolved)
            {
                return 0;
            }

            return Require(facts?.TurnNumber, source.Type, "turnNumber");
        }

        private static int RequireSeat(TableProjectedEvent source)
        {
            if (source.Seat < ProtocolConstants.MinSeat || source.Seat > ProtocolConstants.MaxSeat)
            {
                throw Invalid(source.Type + " has no owner/player seat.");
            }

            return source.Seat;
        }

        private static int RequireOwnerSeat(TableProjectedEvent source)
        {
            if (source.Seat < ProtocolConstants.MinSeat || source.Seat > ProtocolConstants.MaxSeat)
            {
                throw Missing(source.Type, "ownerSeat");
            }

            return source.Seat;
        }

        private static uint RequireSingleTile(TableProjectedEvent source)
        {
            if (source.TileIds.Count != 1)
            {
                throw Invalid(source.Type + " requires exactly one projected tile identity.");
            }

            return source.TileIds[0];
        }

        private static T Require<T>(T? value, ProtocolEventType type, string name)
            where T : struct
        {
            if (!value.HasValue)
            {
                throw Missing(type, name);
            }

            return value.Value;
        }

        private static string RequireText(string? value, ProtocolEventType type, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw Missing(type, name);
            }

            return value;
        }

        private static IReadOnlyList<T> RequireList<T>(IReadOnlyList<T>? value, ProtocolEventType type, string name)
        {
            if (value == null)
            {
                throw Missing(type, name);
            }

            return value;
        }

        private static TableProjectionException Missing(ProtocolEventType type, string fact)
        {
            return new TableProjectionException(
                TableProjectionErrorCode.InvalidPayload,
                "Event " + type + " requires an explicit " + fact + " fact; the P3.2a projection does not carry it.");
        }

        private static TableProjectionException Invalid(string message)
        {
            return new TableProjectionException(TableProjectionErrorCode.InvalidPayload, message);
        }
    }
}

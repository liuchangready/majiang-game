using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Owner-only extension for facts that the current Contracts player snapshot cannot carry:
    /// own melds (including a closed kan's identities), sleeve tiles, mark groups, offers,
    /// reservations and beam candidates. It is never a public type and is built only for the
    /// requested seat. Contracts/Wire remain unchanged in P3.2a.
    /// </summary>
    public sealed class TableOwnerPrivateView
    {
        private readonly ReadOnlyCollection<uint> _closedTiles;
        private readonly ReadOnlyCollection<uint> _discardHistory;
        private readonly ReadOnlyCollection<uint> _sleeveTiles;
        private readonly ReadOnlyCollection<TableOwnerMeldView> _melds;

        public TableOwnerPrivateView(
            int seat,
            IReadOnlyList<uint>? closedTiles,
            uint? drawnTileId,
            bool furiten,
            IReadOnlyList<uint>? discardHistory,
            IReadOnlyList<uint>? sleeveTiles,
            IReadOnlyList<TableOwnerMeldView>? melds,
            TableOwnerCheatView cheat)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            _closedTiles = TableProjectionCopy.Tiles(closedTiles, nameof(closedTiles), 14);
            _discardHistory = TableProjectionCopy.Tiles(discardHistory, nameof(discardHistory), 200);
            _sleeveTiles = TableProjectionCopy.Tiles(sleeveTiles, nameof(sleeveTiles), 14);
            _melds = TableProjectionCopy.Items(melds, nameof(melds), 4);
            for (int i = 0; i < _melds.Count; i++)
            {
                if (_melds[i] == null)
                {
                    throw new ArgumentException("Owner melds cannot contain null.", nameof(melds));
                }
            }

            if (drawnTileId.HasValue)
            {
                TableProjectionCopy.Tile(drawnTileId.Value, nameof(drawnTileId));
            }

            Cheat = cheat ?? throw new ArgumentNullException(nameof(cheat));
            Seat = seat;
            DrawnTileId = drawnTileId;
            Furiten = furiten;
        }

        public int Seat { get; }

        public IReadOnlyList<uint> ClosedTiles => _closedTiles;

        public uint? DrawnTileId { get; }

        public bool Furiten { get; }

        public IReadOnlyList<uint> DiscardHistory => _discardHistory;

        /// <summary>Owner-only sleeve-tile identities; never part of a public board.</summary>
        public IReadOnlyList<uint> SleeveTiles => _sleeveTiles;

        public IReadOnlyList<TableOwnerMeldView> Melds => _melds;

        public TableOwnerCheatView Cheat { get; }
    }

    public sealed class TableOwnerMeldView
    {
        private readonly ReadOnlyCollection<uint> _tileIds;

        public TableOwnerMeldView(ProtocolMeldType type, IReadOnlyList<uint>? tileIds, uint? calledTileId, int fromSeat)
        {
            if (type == ProtocolMeldType.Unspecified)
            {
                throw new ArgumentException("Meld type must be explicit.", nameof(type));
            }

            _tileIds = TableProjectionCopy.Tiles(
                tileIds,
                nameof(tileIds),
                type == ProtocolMeldType.Chi || type == ProtocolMeldType.Pon ? 3 : 4);
            if (calledTileId.HasValue)
            {
                TableProjectionCopy.Tile(calledTileId.Value, nameof(calledTileId));
            }

            TableProjectionCopy.SeatOrNoSeat(fromSeat, nameof(fromSeat));
            Type = type;
            CalledTileId = calledTileId;
            FromSeat = fromSeat;
        }

        public ProtocolMeldType Type { get; }

        public IReadOnlyList<uint> TileIds => _tileIds;

        public uint? CalledTileId { get; }

        public int FromSeat { get; }

        public bool IsConcealed => Type == ProtocolMeldType.ClosedKan;
    }

    public sealed class TableOwnerCheatView
    {
        private readonly ReadOnlyCollection<TableOwnerLedgerEntryView> _ledger;
        private readonly ReadOnlyCollection<TableOwnerMarkGroupView> _markGroups;

        public TableOwnerCheatView(
            int balance,
            IReadOnlyList<TableOwnerLedgerEntryView>? ledger,
            IReadOnlyList<TableOwnerMarkGroupView>? markGroups,
            TableOwnerSleeveBloomOfferView? immediateOffer,
            TableOwnerSleeveBloomOfferView? offTurnOffer,
            TableOwnerSleeveBloomReservationView? reservation,
            string? windowId,
            TableOwnerBeamSwapOfferView? beamSwapOffer)
        {
            if (balance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(balance), balance, "Cheat balance cannot be negative.");
            }

            _ledger = TableProjectionCopy.Items(ledger, nameof(ledger), 4096);
            _markGroups = TableProjectionCopy.Items(markGroups, nameof(markGroups), 12);
            for (int i = 0; i < _ledger.Count; i++)
            {
                if (_ledger[i] == null)
                {
                    throw new ArgumentException("Owner ledger cannot contain null.", nameof(ledger));
                }
            }

            for (int i = 0; i < _markGroups.Count; i++)
            {
                if (_markGroups[i] == null)
                {
                    throw new ArgumentException("Owner mark groups cannot contain null.", nameof(markGroups));
                }
            }

            Balance = balance;
            ImmediateOffer = immediateOffer;
            OffTurnOffer = offTurnOffer;
            Reservation = reservation;
            WindowId = TableProjectionCopy.OptionalId(windowId);
            BeamSwapOffer = beamSwapOffer;
        }

        public int Balance { get; }

        public IReadOnlyList<TableOwnerLedgerEntryView> Ledger => _ledger;

        public IReadOnlyList<TableOwnerMarkGroupView> MarkGroups => _markGroups;

        public TableOwnerSleeveBloomOfferView? ImmediateOffer { get; }

        public TableOwnerSleeveBloomOfferView? OffTurnOffer { get; }

        public TableOwnerSleeveBloomReservationView? Reservation { get; }

        public string WindowId { get; }

        public TableOwnerBeamSwapOfferView? BeamSwapOffer { get; }
    }

    public sealed class TableOwnerLedgerEntryView
    {
        public TableOwnerLedgerEntryView(string entryId, long amount, long balanceAfter, string reasonCode)
        {
            EntryId = ProtocolText.RequireId(entryId, nameof(entryId));
            ReasonCode = reasonCode ?? string.Empty;
            Amount = amount;
            BalanceAfter = balanceAfter;
        }

        public string EntryId { get; }

        public long Amount { get; }

        public long BalanceAfter { get; }

        public string ReasonCode { get; }
    }

    public sealed class TableOwnerMarkGroupView
    {
        private readonly ReadOnlyCollection<uint> _tileIds;

        public TableOwnerMarkGroupView(
            string groupId,
            IReadOnlyList<uint>? tileIds,
            string createdRoundId,
            int createdRoundOrdinal,
            int expiresAfterRoundOrdinal)
        {
            GroupId = ProtocolText.RequireId(groupId, nameof(groupId));
            _tileIds = TableProjectionCopy.Tiles(tileIds, nameof(tileIds), 12);
            CreatedRoundId = ProtocolText.RequireId(createdRoundId, nameof(createdRoundId));
            CreatedRoundOrdinal = createdRoundOrdinal;
            ExpiresAfterRoundOrdinal = expiresAfterRoundOrdinal;
        }

        public string GroupId { get; }

        public IReadOnlyList<uint> TileIds => _tileIds;

        public string CreatedRoundId { get; }

        public int CreatedRoundOrdinal { get; }

        public int ExpiresAfterRoundOrdinal { get; }
    }

    public sealed class TableOwnerSleeveBloomOfferView
    {
        public TableOwnerSleeveBloomOfferView(
            string offerId,
            SleeveBloomDrawKind drawKind,
            uint firstTileId,
            uint secondTileId,
            int createdTurnNumber,
            SleeveBloomOfferMode mode,
            string? windowId,
            long deadlineMillis,
            long createdAtMillis)
        {
            OfferId = ProtocolText.RequireId(offerId, nameof(offerId));
            FirstTileId = TableProjectionCopy.Tile(firstTileId, nameof(firstTileId));
            SecondTileId = TableProjectionCopy.Tile(secondTileId, nameof(secondTileId));
            WindowId = TableProjectionCopy.OptionalId(windowId);
            DrawKind = drawKind;
            CreatedTurnNumber = createdTurnNumber;
            Mode = mode;
            DeadlineMillis = deadlineMillis;
            CreatedAtMillis = createdAtMillis;
        }

        public string OfferId { get; }

        public SleeveBloomDrawKind DrawKind { get; }

        public uint FirstTileId { get; }

        public uint SecondTileId { get; }

        public int CreatedTurnNumber { get; }

        public SleeveBloomOfferMode Mode { get; }

        public string WindowId { get; }

        public long DeadlineMillis { get; }

        public long CreatedAtMillis { get; }
    }

    public sealed class TableOwnerSleeveBloomReservationView
    {
        public TableOwnerSleeveBloomReservationView(string offerId, uint tileId, string? windowId, long createdAtMillis)
        {
            OfferId = ProtocolText.RequireId(offerId, nameof(offerId));
            TileId = TableProjectionCopy.Tile(tileId, nameof(tileId));
            WindowId = TableProjectionCopy.OptionalId(windowId);
            CreatedAtMillis = createdAtMillis;
        }

        public string OfferId { get; }

        public uint TileId { get; }

        public string WindowId { get; }

        public long CreatedAtMillis { get; }
    }

    public sealed class TableOwnerBeamSwapOfferView
    {
        private readonly ReadOnlyCollection<TableOwnerBeamSwapCandidateView> _candidates;
        private readonly ReadOnlyCollection<uint> _eligibleHandTileIds;

        public TableOwnerBeamSwapOfferView(
            string offerId,
            BeamSwapWindow window,
            IReadOnlyList<TableOwnerBeamSwapCandidateView>? candidates,
            IReadOnlyList<uint>? eligibleHandTileIds,
            uint drawnTileId,
            int createdTurnNumber)
        {
            OfferId = ProtocolText.RequireId(offerId, nameof(offerId));
            _candidates = TableProjectionCopy.Items(candidates, nameof(candidates), 16);
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i] == null)
                {
                    throw new ArgumentException("Beam candidates cannot contain null.", nameof(candidates));
                }
            }

            _eligibleHandTileIds = TableProjectionCopy.Tiles(eligibleHandTileIds, nameof(eligibleHandTileIds), 14);
            DrawnTileId = TableProjectionCopy.Tile(drawnTileId, nameof(drawnTileId));
            Window = window;
            CreatedTurnNumber = createdTurnNumber;
        }

        public string OfferId { get; }

        public BeamSwapWindow Window { get; }

        public IReadOnlyList<TableOwnerBeamSwapCandidateView> Candidates => _candidates;

        public IReadOnlyList<uint> EligibleHandTileIds => _eligibleHandTileIds;

        public uint DrawnTileId { get; }

        public int CreatedTurnNumber { get; }
    }

    public sealed class TableOwnerBeamSwapCandidateView
    {
        public TableOwnerBeamSwapCandidateView(uint tileId, BeamSwapSlotKind slotKind, int slotIndex)
        {
            TileId = TableProjectionCopy.Tile(tileId, nameof(tileId));
            SlotKind = slotKind;
            SlotIndex = slotIndex;
        }

        public uint TileId { get; }

        public BeamSwapSlotKind SlotKind { get; }

        public int SlotIndex { get; }
    }

    /// <summary>
    /// Pure, actor-callable P3.2a projection. It reads only immutable committed Domain state
    /// and explicit context, never a clock, random source or shared mutable object. Every
    /// returned object owns copied read-only collections. Cursor/epoch/binding allocation is
    /// deliberately absent; P3.2b supplies the cursor and owns subscription/recovery.
    /// </summary>
    public static class TableVisibilityProjection
    {
        public static PublicSnapshotView ProjectPublicSnapshot(
            RoundStateMachine round,
            TableProjectionContext context)
        {
            ContextIndex index = Validate(round, context);
            var audience = TableProjectionAudience.Spectator;
            var view = BuildPublicMatchView(round, context, audience, index);
            return new PublicSnapshotView(
                context.Identity.SessionId,
                new RoundId(round.RoundId),
                context.Identity.Cursor,
                context.Identity.StateVersion,
                context.Identity.EventSeq,
                context.Identity.SessionVersion,
                new RoundVersion(unchecked((ulong)round.Version)),
                view);
        }

        /// <summary>
        /// Recovery-only validation: proves that persisted P3.2 public/private facts match the
        /// recovered 136-tile round and (when cheat is modeled) owner-private meld/hand facts.
        /// It throws before Serving on any inconsistency; it never fills missing facts.
        /// </summary>
        public static void ValidateRecoveredFacts(
            RoundStateMachine round,
            TableProjectionContextData data,
            CheatState? cheat)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var identity = new TableProjectionIdentity(
                new SessionId("recovery"),
                new RoundId(round.RoundId),
                data.StateVersion,
                new EventSeq(0),
                new StreamCursor(new StreamVersion(0), new ProjectionSeq(0)),
                new SessionVersion(0));
            var context = new TableProjectionContext(
                identity,
                data.Policy,
                null,
                data.PublicFacts,
                data.PrivateFacts,
                null,
                null,
                data.Session);
            ContextIndex index = Validate(round, context);
            _ = index;
            if (cheat != null)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    _ = ProjectOwnerPrivate(round, cheat, seat, context);
                }
            }
        }

        /// <summary>
        /// Recovery-only event validation through the same P3.2 event projection and Contracts
        /// mapper used by live streams. Every committed history event must have exactly one fact;
        /// the fact type/version/ordinal must match the projected event, and MapEvent must accept
        /// all required per-type fields. A forced settlement policy validates terminal win facts
        /// without publishing them.
        /// </summary>
        public static void ValidateRecoveredEventFacts(
            IReadOnlyList<TableEventRecord> history,
            IReadOnlyList<TableProjectionEventFact> facts,
            TableProjectionPolicy policy)
        {
            ValidateRecoveredEventFacts(history, facts, policy, null);
        }

        /// <summary>
        /// Adds Domain-verifiable cross-checks when the recovered final round is supplied:
        /// each TileDrawn fact's wallRemaining is reconstructed backwards from the trusted
        /// checkpoint/final wall; RoundEnded roundWind/roundNumber must equal the immutable
        /// round spec; draw tenpai and win loser must equal the committed result. Histories
        /// containing kan/replacement draws are rejected because live-wall inverse evidence is
        /// not modeled rather than guessed.
        /// </summary>
        public static void ValidateRecoveredEventFacts(
            IReadOnlyList<TableEventRecord> history,
            IReadOnlyList<TableProjectionEventFact> facts,
            TableProjectionPolicy policy,
            RoundStateMachine? finalRound)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            int projectable = 0;
            for (int i = 0; i < history.Count; i++)
            {
                if (TableEventProjection.FromRoundEvent(history[i]) != null)
                {
                    projectable++;
                }
            }

            if (facts.Count != projectable)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "Recovered event facts do not cover the complete projectable committed history.");
            }

            var bySeq = new Dictionary<ulong, TableProjectionEventFact>(facts.Count);
            for (int i = 0; i < facts.Count; i++)
            {
                TableProjectionEventFact fact = facts[i] ??
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Recovered event facts contain null.");
                if (!bySeq.TryAdd(fact.EventSeq.Value, fact))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventOrderMismatch,
                        "Recovered event facts repeat event_seq " + fact.EventSeq.Value + ".");
                }
            }

            var expectedWallBySeq = new Dictionary<ulong, uint>();
            if (finalRound != null)
            {
                long wall = finalRound.Tiles.WallRemaining;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    TableEventRecord record = history[i];
                    if (record.Type == RoundEventType.KanDeclared ||
                        record.Type == RoundEventType.RinshanDrawn)
                    {
                        throw new TableProjectionException(
                            TableProjectionErrorCode.InvalidContext,
                            "Kan/replacement-draw wall evidence is not modeled; refusing to guess.");
                    }

                    if (record.Type == RoundEventType.TileDrawn)
                    {
                        if (wall < 0)
                        {
                            throw new TableProjectionException(
                                TableProjectionErrorCode.InvalidContext,
                                "Recovered wall remaining underflowed during event validation.");
                        }

                        expectedWallBySeq[record.Seq.Value] = unchecked((uint)wall);
                        wall++;
                    }
                }
            }

            var validationPolicy = new TableProjectionPolicy(
                policy.Paused,
                settlementBatch: true,
                revealSettlementUra: true);
            for (int i = 0; i < history.Count; i++)
            {
                TableEventRecord record = history[i];
                TableEventProjectionInput? input = TableEventProjection.FromRoundEvent(record);
                if (input == null)
                {
                    // CheatApplied is the one-command provenance bridge; it has no frozen
                    // protocol event and therefore no projection fact to validate.
                    continue;
                }

                if (!bySeq.TryGetValue(record.Seq.Value, out TableProjectionEventFact? fact))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Committed event " + record.Seq + " has no recovered fact.");
                }

                if (input.Type != fact.Type ||
                    input.CommandVersion != fact.CommandVersion ||
                    input.EventOrdinal != fact.EventOrdinal)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "Recovered event fact " + record.Seq + " does not bind its committed event.");
                }

                bool projected = false;
                for (int audienceIndex = -1; audienceIndex < 4; audienceIndex++)
                {
                    TableProjectionAudience audience = audienceIndex < 0
                        ? TableProjectionAudience.Spectator
                        : TableProjectionAudience.ForSeat(audienceIndex);
                    TableProjectedEvent? value = TableEventProjection.Project(input, audience, validationPolicy);
                    if (value != null)
                    {
                        _ = TableProjectionContractMapper.MapEvent(value, fact.Facts);
                        projected = true;
                    }
                }

                if (!projected)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Committed event " + record.Seq + " is hidden from every validated audience.");
                }

                if (finalRound != null)
                {
                    ValidateDomainEventValues(record, fact, finalRound, expectedWallBySeq);
                }
            }
        }

        private static void ValidateDomainEventValues(
            TableEventRecord record,
            TableProjectionEventFact fact,
            RoundStateMachine finalRound,
            IReadOnlyDictionary<ulong, uint> expectedWallBySeq)
        {
            switch (record.Type)
            {
                case RoundEventType.TileDrawn:
                    if (!fact.Facts.WallRemaining.HasValue ||
                        !expectedWallBySeq.TryGetValue(record.Seq.Value, out uint expectedWall) ||
                        fact.Facts.WallRemaining.Value != expectedWall)
                    {
                        throw new TableProjectionException(
                            TableProjectionErrorCode.InvalidPayload,
                            "TileDrawn wallRemaining contradicts the recovered wall at " + record.Seq + ".");
                    }

                    break;
                case RoundEventType.RoundEnded:
                    if (!fact.Facts.RoundWind.HasValue ||
                        fact.Facts.RoundWind.Value != unchecked((uint)finalRound.RoundWind) ||
                        !fact.Facts.RoundNumber.HasValue ||
                        fact.Facts.RoundNumber.Value != unchecked((uint)finalRound.RoundNumber))
                    {
                        throw new TableProjectionException(
                            TableProjectionErrorCode.InvalidPayload,
                            "RoundEnded roundWind/roundNumber contradicts the immutable round spec.");
                    }

                    break;
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw:
                    ValidateTenpai(record, fact, finalRound);
                    break;
                case RoundEventType.RonWin:
                    ValidateLoser(record, fact, finalRound);
                    break;
            }
        }

        private static void ValidateTenpai(
            TableEventRecord record,
            TableProjectionEventFact fact,
            RoundStateMachine finalRound)
        {
            RoundResult? result = finalRound.Result;
            if (result == null ||
                (result.Outcome.Kind != RoundOutcomeKind.ExhaustiveDraw &&
                 result.Outcome.Kind != RoundOutcomeKind.AbortiveDraw) ||
                fact.Facts.TenpaiPlayers == null ||
                !fact.Facts.TenpaiPlayers.OrderBy(value => value)
                    .SequenceEqual(result.Outcome.TenpaiSeats.OrderBy(value => value)))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidPayload,
                    "Draw tenpai facts contradict the committed result at " + record.Seq + ".");
            }
        }

        private static void ValidateLoser(
            TableEventRecord record,
            TableProjectionEventFact fact,
            RoundStateMachine finalRound)
        {
            RoundResult? result = finalRound.Result;
            if (result == null ||
                (result.Outcome.Kind != RoundOutcomeKind.Ron &&
                 result.Outcome.Kind != RoundOutcomeKind.Tsumo) ||
                fact.Facts.LoserSeat != result.Outcome.LoserSeat)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidPayload,
                    "Win loser facts contradict the committed result at " + record.Seq + ".");
            }
        }

        public static PlayerSnapshotView ProjectPlayerSnapshot(
            RoundStateMachine round,
            CheatState cheat,
            int seat,
            TableProjectionContext context)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            ContextIndex index = Validate(round, context);
            var audience = TableProjectionAudience.ForSeat(seat);
            var publicView = BuildPublicMatchView(round, context, audience, index);
            PrivatePlayerView privateView = BuildContractPrivate(round, cheat, seat, context, index);
            return new PlayerSnapshotView(
                context.Identity.SessionId,
                new RoundId(round.RoundId),
                seat,
                context.Identity.Cursor,
                context.Identity.StateVersion,
                context.Identity.EventSeq,
                context.Identity.SessionVersion,
                new RoundVersion(unchecked((ulong)round.Version)),
                publicView,
                privateView);
        }

        public static TableOwnerPrivateView ProjectOwnerPrivate(
            RoundStateMachine round,
            CheatState cheat,
            int seat,
            TableProjectionContext context)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            ContextIndex index = Validate(round, context);
            TableSeatPrivateFacts? privateFacts = index.PrivateFacts(seat);

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            TileId? drawn = round.Tiles.DrawnTile.HasValue && round.Tiles.DrawnTile.Value.Seat == seat
                ? round.Tiles.DrawnTile.Value.Tile
                : (TileId?)null;
            IReadOnlyList<uint> closed = CloseHand(hand, drawn);
            IReadOnlyList<uint> history = privateFacts != null
                ? privateFacts.DiscardHistory
                : ToTileValues(round.Tiles.TilesInZone(TileZone.Discard, seat));
            IReadOnlyList<uint> sleeve = ToTileValues(round.Tiles.TilesInZone(TileZone.Sleeve, seat));

            var melds = new List<TableOwnerMeldView>();
            TableSeatPublicFacts? publicFacts = index.PublicFacts(seat);
            if (publicFacts != null)
            {
                for (int i = 0; i < publicFacts.Melds.Count; i++)
                {
                    TableMeldFact meld = publicFacts.Melds[i];
                    melds.Add(new TableOwnerMeldView(meld.Type, meld.TileIds, meld.CalledTileId, meld.FromSeat));
                }
            }

            CheatPrivateView cheatView = RequireCheat(cheat).ViewFor(seat);
            TableOwnerCheatView ownerCheat = BuildOwnerCheat(cheatView, seat);
            return new TableOwnerPrivateView(
                seat,
                closed,
                drawn.HasValue ? unchecked((uint)drawn.Value.Value) : (uint?)null,
                privateFacts != null && privateFacts.Furiten,
                history,
                sleeve,
                melds,
                ownerCheat);
        }

        private static ContextIndex Validate(RoundStateMachine round, TableProjectionContext context)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!string.Equals(context.Identity.RoundId.Value, round.RoundId, StringComparison.Ordinal))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.RoundIdMismatch,
                    "Context round '" + context.Identity.RoundId + "' does not match committed round '" + round.RoundId + "'.");
            }

            if (context.Identity.StateVersion.Value < unchecked((ulong)round.Version))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.StaleStateVersion,
                    "Context state version " + context.Identity.StateVersion +
                    " predates committed round version " + round.Version + ".");
            }

            if (context.Identity.Cursor.StreamVersion.Value > context.Identity.StateVersion.Value)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.FutureCursor,
                    "Cursor stream version cannot exceed the committed state version.");
            }

            ulong lastSeq = 0;
            for (int i = 0; i < context.Events.Count; i++)
            {
                TableEventProjectionInput input = context.Events[i];
                if (input.EventSeq.Value <= lastSeq)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventOrderMismatch,
                        "Context events must be ordered by strictly increasing event_seq.");
                }

                lastSeq = input.EventSeq.Value;
                if (input.EventSeq.Value > context.Identity.EventSeq.Value)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "Event sequence exceeds the committed event watermark.");
                }

                if (input.CommandVersion.Value > context.Identity.StateVersion.Value)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "Event command_version exceeds the committed state version.");
                }
            }

            var index = new ContextIndex();
            for (int i = 0; i < context.PublicFacts.Count; i++)
            {
                TableSeatPublicFacts facts = context.PublicFacts[i];
                if (!index.AddPublic(facts))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.DuplicateSeat,
                        "Public facts repeat seat " + facts.Seat + ".");
                }
            }

            for (int i = 0; i < context.PrivateFacts.Count; i++)
            {
                TableSeatPrivateFacts facts = context.PrivateFacts[i];
                if (!index.AddPrivate(facts))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.DuplicateSeat,
                        "Private facts repeat seat " + facts.Seat + ".");
                }
            }

            for (int i = 0; i < context.Decisions.Count; i++)
            {
                TableSeatDecision decision = context.Decisions[i];
                if (!index.AddDecision(decision))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.DuplicateSeat,
                        "Decisions repeat seat " + decision.Seat + ".");
                }
            }

            for (int seat = 0; seat < 4; seat++)
            {
                ValidatePublicFacts(round, seat, index.PublicFacts(seat));
                ValidatePrivateFacts(round, seat, index.PrivateFacts(seat));
            }

            if (context.Session != null && context.Session.Finished && !round.IsFinished)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "A finished session cannot be projected from an active round.");
            }

            return index;
        }

        private static void ValidatePublicFacts(RoundStateMachine round, int seat, TableSeatPublicFacts? facts)
        {
            IReadOnlyList<TileId> domainDiscards = round.Tiles.TilesInZone(TileZone.Discard, seat);
            if (facts == null)
            {
                if (domainDiscards.Count > 0)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.MissingPublicFacts,
                        "Seat " + seat + " has discards but no public facts were supplied.");
                }

                if (round.Tiles.TilesInZone(TileZone.Meld, seat).Count > 0)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.MissingPublicFacts,
                        "Seat " + seat + " has melds but no public facts were supplied.");
                }

                return;
            }

            if (facts.Discards.Count != domainDiscards.Count)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.PublicFactsMismatch,
                    "Seat " + seat + " discard facts do not match the committed river.");
            }

            for (int i = 0; i < domainDiscards.Count; i++)
            {
                if (facts.Discards[i].TileId != domainDiscards[i].Value)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.PublicFactsMismatch,
                        "Seat " + seat + " discard order/tile does not match the committed river.");
                }
            }

            IReadOnlyList<TileId> domainMelds = round.Tiles.TilesInZone(TileZone.Meld, seat);
            var meldTiles = new HashSet<uint>();
            for (int i = 0; i < facts.Melds.Count; i++)
            {
                TableMeldFact meld = facts.Melds[i];
                for (int t = 0; t < meld.TileIds.Count; t++)
                {
                    if (!meldTiles.Add(meld.TileIds[t]))
                    {
                        throw new TableProjectionException(
                            TableProjectionErrorCode.PublicFactsMismatch,
                            "Meld facts for seat " + seat + " repeat a physical tile.");
                    }
                }
            }

            if (meldTiles.Count != domainMelds.Count)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.PublicFactsMismatch,
                    "Seat " + seat + " meld facts do not cover exactly the committed Meld zone.");
            }

            for (int i = 0; i < domainMelds.Count; i++)
            {
                if (!meldTiles.Contains(unchecked((uint)domainMelds[i].Value)))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.PublicFactsMismatch,
                        "Seat " + seat + " meld facts contain a tile outside the committed Meld zone.");
                }
            }

            LastDiscardEvidence? last = round.Tiles.LastDiscard;
            if (last.HasValue && last.Value.Seat == seat)
            {
                bool found = false;
                for (int i = 0; i < facts.Discards.Count; i++)
                {
                    if (facts.Discards[i].TileId == last.Value.Tile.Value)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found || round.Tiles.PositionOf(last.Value.Tile) != last.Value.Position)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.PublicFactsMismatch,
                        "Last-discard evidence is not represented by the committed river facts.");
                }
            }
        }

        private static void ValidatePrivateFacts(RoundStateMachine round, int seat, TableSeatPrivateFacts? facts)
        {
            if (facts == null)
            {
                return;
            }

            var history = new HashSet<uint>();
            for (int i = 0; i < facts.DiscardHistory.Count; i++)
            {
                history.Add(facts.DiscardHistory[i]);
            }

            IReadOnlyList<TileId> current = round.Tiles.TilesInZone(TileZone.Discard, seat);
            for (int i = 0; i < current.Count; i++)
            {
                if (!history.Contains(unchecked((uint)current[i].Value)))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.PrivateFactsMismatch,
                        "Seat " + seat + " discard history omits a current river tile.");
                }
            }
        }

        private static PublicMatchView BuildPublicMatchView(
            RoundStateMachine round,
            TableProjectionContext context,
            TableProjectionAudience audience,
            ContextIndex index)
        {
            PublicBoardView board = BuildBoard(round, context, index);
            PublicSessionSummary session = BuildSession(context);
            IReadOnlyList<ProtocolEventRecord> settlement = BuildSettlementEvents(context, audience);
            string settlementRoundId = context.Session != null && context.Session.SettlementRoundId.Length > 0
                ? context.Session.SettlementRoundId
                : (round.IsFinished ? round.RoundId : string.Empty);
            return new PublicMatchView(
                board,
                session,
                context.Clocks,
                context.Policy.Paused,
                settlementRoundId,
                settlement);
        }

        private static PublicBoardView BuildBoard(
            RoundStateMachine round,
            TableProjectionContext context,
            ContextIndex index)
        {
            IReadOnlyList<long> scores = round.Result != null ? round.Result.FinalScores : round.Scores;
            var scoreViews = new List<ScoreView>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                scoreViews.Add(new ScoreView(seat, scores[seat]));
            }

            var dora = new List<uint>();
            for (int i = 0; i < round.Tiles.DoraIndicators.Count; i++)
            {
                dora.Add(unchecked((uint)round.Tiles.DoraIndicators[i].Value));
            }

            var players = new List<PlayerPublicView>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                TableSeatPublicFacts? facts = index.PublicFacts(seat);
                var melds = new List<MeldView>();
                if (facts != null)
                {
                    for (int i = 0; i < facts.Melds.Count; i++)
                    {
                        TableMeldFact meld = facts.Melds[i];
                        if (meld.IsConcealed)
                        {
                            // A closed kan is public as an event, but its four identities are not
                            // representable in the public Contracts meld without leaking them.
                            continue;
                        }

                        melds.Add(new MeldView(meld.Type, meld.TileIds, meld.CalledTileId, meld.FromSeat));
                    }
                }

                var discards = new List<DiscardView>();
                if (facts != null)
                {
                    for (int i = 0; i < facts.Discards.Count; i++)
                    {
                        DiscardView discard = facts.Discards[i];
                        discards.Add(new DiscardView(
                            discard.TileId,
                            discard.TurnNumber,
                            discard.RiichiDiscard,
                            discard.Tsumogiri));
                    }
                }

                uint handCount = unchecked((uint)round.Tiles.CountAt(TileZone.Hand, seat));
                if (handCount > 14)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Seat " + seat + " hand count exceeds 14.");
                }

                players.Add(new PlayerPublicView(
                    seat,
                    handCount,
                    melds,
                    discards,
                    facts != null && facts.Riichi));
            }

            DiscardView? lastDiscard = null;
            LastDiscardEvidence? evidence = round.Tiles.LastDiscard;
            if (evidence.HasValue)
            {
                TableSeatPublicFacts? facts = index.PublicFacts(evidence.Value.Seat);
                if (facts != null)
                {
                    for (int i = 0; i < facts.Discards.Count; i++)
                    {
                        if (facts.Discards[i].TileId == evidence.Value.Tile.Value)
                        {
                            DiscardView source = facts.Discards[i];
                            lastDiscard = new DiscardView(
                                source.TileId,
                                source.TurnNumber,
                                source.RiichiDiscard,
                                source.Tsumogiri);
                            break;
                        }
                    }
                }
            }

            return new PublicBoardView(
                MapPhase(round.Phase),
                unchecked((uint)round.RoundWind),
                unchecked((uint)round.RoundNumber),
                unchecked((uint)round.HonbaSticks),
                unchecked((uint)round.RiichiSticks),
                round.DealerSeat,
                round.CurrentSeat,
                unchecked((uint)round.Tiles.WallRemaining),
                dora,
                scoreViews,
                players,
                lastDiscard,
                context.Policy.Paused);
        }

        private static PublicSessionSummary BuildSession(TableProjectionContext context)
        {
            // Match lifecycle is owned by TableSessionProjection, never by a single round.
            // A finished round is still just a round: without explicit match context the
            // summary must remain the neutral "not finished" value. Round final scores are
            // published on Board, and the round settlement stays in SettlementRoundId/events.
            if (context.Session == null)
            {
                return new PublicSessionSummary(false, string.Empty, Array.Empty<ScoreView>(), Array.Empty<int>());
            }

            return new PublicSessionSummary(
                context.Session.Finished,
                context.Session.FinishReason,
                ScoreViews(context.Session.FinalScores),
                context.Session.Rankings);
        }

        private static IReadOnlyList<ScoreView> ScoreViews(IReadOnlyList<long> scores)
        {
            if (scores.Count == 0)
            {
                return Array.Empty<ScoreView>();
            }

            var views = new List<ScoreView>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                views.Add(new ScoreView(seat, scores[seat]));
            }

            return views;
        }

        private static IReadOnlyList<ProtocolEventRecord> BuildSettlementEvents(
            TableProjectionContext context,
            TableProjectionAudience audience)
        {
            var result = new List<ProtocolEventRecord>();
            for (int i = 0; i < context.Events.Count; i++)
            {
                TableEventProjectionInput input = context.Events[i];
                if (!IsSettlementEvent(input))
                {
                    continue;
                }

                TableProjectedEvent? projected = TableEventProjection.Project(input, audience, context.Policy);
                if (projected != null)
                {
                    result.Add(projected.ToProtocolEventRecord());
                }
            }

            return new ReadOnlyCollection<ProtocolEventRecord>(result);
        }

        private static bool IsSettlementEvent(TableEventProjectionInput input)
        {
            if (input.Type == ProtocolEventType.DoraRevealed && input.IsUraDora)
            {
                return true;
            }

            return TableEventProjectionInput.IsSettlementType(input.Type);
        }

        private static PrivatePlayerView BuildContractPrivate(
            RoundStateMachine round,
            CheatState cheat,
            int seat,
            TableProjectionContext context,
            ContextIndex index)
        {
            TableSeatPrivateFacts? privateFacts = index.PrivateFacts(seat);
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            TileId? drawn = round.Tiles.DrawnTile.HasValue && round.Tiles.DrawnTile.Value.Seat == seat
                ? round.Tiles.DrawnTile.Value.Tile
                : (TileId?)null;
            IReadOnlyList<uint> closed = CloseHand(hand, drawn);
            IReadOnlyList<uint> history = privateFacts != null
                ? privateFacts.DiscardHistory
                : ToTileValues(round.Tiles.TilesInZone(TileZone.Discard, seat));

            TableSeatDecision? decision = index.Decision(seat);
            CheatPrivateView cheatView = RequireCheat(cheat).ViewFor(seat);
            PrivateCheatView contractCheat = BuildContractCheat(
                cheatView,
                privateFacts != null && privateFacts.HasPreparation,
                seat);

            return new PrivatePlayerView(
                seat,
                closed,
                drawn.HasValue ? unchecked((uint)drawn.Value.Value) : (uint?)null,
                privateFacts != null && privateFacts.Furiten,
                history,
                decision?.Decision,
                contractCheat);
        }

        private static PrivateCheatView BuildContractCheat(CheatPrivateView view, bool hasPreparation, int seat)
        {
            if (view.Seat != seat)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "Cheat view belongs to another seat.");
            }

            var ledger = new List<CheatLedgerEntryView>(view.Entries.Count);
            for (int i = 0; i < view.Entries.Count; i++)
            {
                CheatLedgerEntry entry = view.Entries[i];
                if (entry.Seat != seat)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Owner ledger contains another seat's entry.");
                }

                ledger.Add(new CheatLedgerEntryView(
                    entry.EntryId,
                    entry.Amount,
                    entry.BalanceAfter,
                    StableReason(entry.Reason)));
            }

            bool hasSleeve = view.ImmediateOffer != null ||
                             view.OffTurnOffer != null ||
                             view.Reservation != null ||
                             !string.IsNullOrEmpty(view.WindowId);
            return new PrivateCheatView(
                view.Balance,
                ledger,
                view.MarkGroups.Count > 0,
                hasSleeve,
                view.BeamSwapOffer != null,
                hasPreparation);
        }

        private static TableOwnerCheatView BuildOwnerCheat(CheatPrivateView view, int seat)
        {
            if (view.Seat != seat)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "Cheat view belongs to another seat.");
            }

            var ledger = new List<TableOwnerLedgerEntryView>(view.Entries.Count);
            for (int i = 0; i < view.Entries.Count; i++)
            {
                CheatLedgerEntry entry = view.Entries[i];
                if (entry.Seat != seat)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Owner ledger contains another seat's entry.");
                }

                ledger.Add(new TableOwnerLedgerEntryView(entry.EntryId, entry.Amount, entry.BalanceAfter, entry.Reason));
            }

            var marks = new List<TableOwnerMarkGroupView>(view.MarkGroups.Count);
            for (int i = 0; i < view.MarkGroups.Count; i++)
            {
                TileMarkGroup group = view.MarkGroups[i];
                if (group.OwnerSeat != seat)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Owner mark groups contain another seat's group.");
                }

                var tiles = new List<uint>(group.TileIds.Count);
                for (int t = 0; t < group.TileIds.Count; t++)
                {
                    tiles.Add(unchecked((uint)group.TileIds[t].Value));
                }

                marks.Add(new TableOwnerMarkGroupView(
                    group.GroupId,
                    tiles,
                    group.CreatedRoundId,
                    group.CreatedRoundOrdinal,
                    group.ExpiresAfterRoundOrdinal));
            }

            return new TableOwnerCheatView(
                view.Balance,
                ledger,
                marks,
                MapOffer(view.ImmediateOffer),
                MapOffer(view.OffTurnOffer),
                MapReservation(view.Reservation),
                view.WindowId,
                MapBeamOffer(view.BeamSwapOffer));
        }

        private static TableOwnerSleeveBloomOfferView? MapOffer(SleeveBloomOffer? offer)
        {
            if (offer == null)
            {
                return null;
            }

            return new TableOwnerSleeveBloomOfferView(
                offer.OfferId,
                offer.DrawKind,
                unchecked((uint)offer.FirstTile.Value),
                unchecked((uint)offer.SecondTile.Value),
                offer.CreatedTurnNumber,
                offer.Mode,
                offer.WindowId,
                offer.DeadlineMillis,
                offer.CreatedAtMillis);
        }

        private static TableOwnerSleeveBloomReservationView? MapReservation(SleeveBloomReservation? reservation)
        {
            if (reservation == null)
            {
                return null;
            }

            return new TableOwnerSleeveBloomReservationView(
                reservation.OfferId,
                unchecked((uint)reservation.Tile.Value),
                reservation.WindowId,
                reservation.CreatedAtMillis);
        }

        private static TableOwnerBeamSwapOfferView? MapBeamOffer(BeamSwapOffer? offer)
        {
            if (offer == null)
            {
                return null;
            }

            var candidates = new List<TableOwnerBeamSwapCandidateView>(offer.Candidates.Count);
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                BeamSwapCandidate candidate = offer.Candidates[i];
                candidates.Add(new TableOwnerBeamSwapCandidateView(
                    unchecked((uint)candidate.Tile.Value),
                    candidate.Slot.Kind,
                    candidate.Slot.Index));
            }

            var eligible = new List<uint>(offer.EligibleHandTileIds.Count);
            for (int i = 0; i < offer.EligibleHandTileIds.Count; i++)
            {
                eligible.Add(unchecked((uint)offer.EligibleHandTileIds[i].Value));
            }

            return new TableOwnerBeamSwapOfferView(
                offer.OfferId,
                offer.Window,
                candidates,
                eligible,
                unchecked((uint)offer.DrawnTileId.Value),
                offer.CreatedTurnNumber);
        }

        private static CheatState RequireCheat(CheatState? cheat)
        {
            return cheat ?? throw new ArgumentNullException(nameof(cheat));
        }

        private static IReadOnlyList<uint> CloseHand(IReadOnlyList<TileId> hand, TileId? drawn)
        {
            var closed = new List<uint>(hand.Count);
            for (int i = 0; i < hand.Count; i++)
            {
                if (drawn.HasValue && hand[i].Value == drawn.Value.Value)
                {
                    continue;
                }

                closed.Add(unchecked((uint)hand[i].Value));
            }

            return closed;
        }

        private static IReadOnlyList<uint> ToTileValues(IReadOnlyList<TileId> tiles)
        {
            var result = new List<uint>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                result.Add(unchecked((uint)tiles[i].Value));
            }

            return result;
        }

        private static ProtocolGamePhase MapPhase(RoundPhase phase)
        {
            switch (phase)
            {
                case RoundPhase.Stacking:
                    return ProtocolGamePhase.Stacking;
                case RoundPhase.Dealing:
                    return ProtocolGamePhase.Dealing;
                case RoundPhase.Draw:
                    return ProtocolGamePhase.Draw;
                case RoundPhase.CheatWindow:
                    return ProtocolGamePhase.CheatWindow;
                case RoundPhase.Discard:
                    return ProtocolGamePhase.Discard;
                case RoundPhase.ResponseWindow:
                    return ProtocolGamePhase.ResponseWindow;
                case RoundPhase.ChankanWindow:
                    return ProtocolGamePhase.ChankanWindow;
                case RoundPhase.KanDraw:
                    return ProtocolGamePhase.KanDraw;
                case RoundPhase.RoundEnd:
                    return ProtocolGamePhase.RoundEnd;
                default:
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Unknown round phase " + phase + ".");
            }
        }

        private static string StableReason(string reason)
        {
            return ProtocolErrorCode.IsWellFormed(reason) ? reason : "CHEAT_LEDGER";
        }

        private sealed class ContextIndex
        {
            private readonly Dictionary<int, TableSeatPublicFacts> _public = new Dictionary<int, TableSeatPublicFacts>();
            private readonly Dictionary<int, TableSeatPrivateFacts> _private = new Dictionary<int, TableSeatPrivateFacts>();
            private readonly Dictionary<int, TableSeatDecision> _decisions = new Dictionary<int, TableSeatDecision>();

            internal bool AddPublic(TableSeatPublicFacts facts)
            {
                if (_public.ContainsKey(facts.Seat))
                {
                    return false;
                }

                _public.Add(facts.Seat, facts);
                return true;
            }

            internal bool AddPrivate(TableSeatPrivateFacts facts)
            {
                if (_private.ContainsKey(facts.Seat))
                {
                    return false;
                }

                _private.Add(facts.Seat, facts);
                return true;
            }

            internal bool AddDecision(TableSeatDecision decision)
            {
                if (_decisions.ContainsKey(decision.Seat))
                {
                    return false;
                }

                _decisions.Add(decision.Seat, decision);
                return true;
            }

            internal TableSeatPublicFacts? PublicFacts(int seat)
            {
                return _public.TryGetValue(seat, out TableSeatPublicFacts? facts) ? facts : null;
            }

            internal TableSeatPrivateFacts? PrivateFacts(int seat)
            {
                return _private.TryGetValue(seat, out TableSeatPrivateFacts? facts) ? facts : null;
            }

            internal TableSeatDecision? Decision(int seat)
            {
                return _decisions.TryGetValue(seat, out TableSeatDecision? decision) ? decision : null;
            }
        }
    }
}

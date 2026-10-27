using System;
using System.Collections.Generic;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// The service-side trusted projection context port (the P3.2c caller duty): maps the
    /// actor's committed Domain round and event log into frozen projection facts.
    ///
    /// D-1-01: every fact here is DERIVED from trusted committed authority state - the round
    /// itself and its committed event chain - and never defaults to a fabricated value when
    /// the verifiable truth exists:
    ///  - riichi flags come from the round's own RiichiDeclared register;
    ///  - melds come from the round's authoritative meld records (closed-kan identities stay
    ///    public-fact free; they are owner-extension facts only);
    ///  - per-discard tsumogiri and riichi-discard flags are replayed per seat in chronological
    ///    order over the round's full committed event chain, so every seat with several
    ///    discards keeps the exact truth of EVERY river entry, not just the latest draw;
    ///  - the seat's private discard history is the same derived order as the visibility
    ///    projector already re-checks against the immutable map;
    ///  - the per-event turn number is the honest per-event turn ordinal (turn 1 before the
    ///    round's first discard, then +1 for every draw event after that first discard — a
    ///    deal-extra opening therefore numbers its first post-opening draw turn 2); facts the
    ///    Domain genuinely
    ///    does not model (furiten/preparation overlays, yaku/han/fu) still fail closed
    ///    through the existing projection paths, they are never silently asserted true;
    ///  - a kan event's meld shape comes from the Domain's own meld record matched by seat
    ///    and called tile, and the seat's open domain decision (decision id, control epoch,
    ///    honest candidates, deadline) is projected from the decision runtime state shared
    ///    with the platform tick driver — never from a client claim.
    /// </summary>
    public sealed class TableProjectionContextSource : ITableProjectionContextSource
    {
        /// <summary>
        /// The service-level cheat economy, shaped like the accepted TableRuntime fixtures
        /// (tile mark 5, sleeve bloom 10, beam swap 10). The actor owns this economy and
        /// overlays its own trusted cheat aggregate onto every capture, so the aggregate
        /// built here is only a validly shaped placeholder for the seat-stream guard.
        /// </summary>
        internal static readonly CheatEconomyConfig DefaultEconomy = CreateDefaultEconomy();

        private static CheatEconomyConfig CreateDefaultEconomy()
        {
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [CheatEconomyConfig.TileMarkCostKey] = 5,
                [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                [CheatEconomyConfig.BeamSwapCostKey] = 10
            };
            return new CheatEconomyConfig(true, 1000, costs);
        }

        /// <summary>The actor options require a sleeve config whenever an economy is set.</summary>
        internal static readonly SleeveBloomConfig DefaultSleeveConfig = new SleeveBloomConfig(true, 5000L);

        private readonly TableRoomDecisionState? _decisionState;

        /// <summary>Version-0 anchor (round captured Round-0). The trusted pointer from 0 to 1:
        /// the only carrier of the deal-extra pending-drawn evidence a discard's chain cannot
        /// express (the Domain removes that evidence with the seat's own first discard).</summary>
        private readonly RoundStateMachine? _initialRound;

        public TableProjectionContextSource(TableRoomDecisionState? decisionState = null, RoundStateMachine? initialRound = null)
        {
            _decisionState = decisionState;
            _initialRound = initialRound;
        }

        public TableProjectionContextData Capture(TableProjectionContextRequest request)
        {
            if (_decisionState != null)
            {
                // D-1-01: the trusted source is where the committed Domain round is exposed to
                // the service, so this is where the room's decision orchestration refreshes its
                // authoritative need (phase, current seat, response window, hand tiles) for the
                // tick driver's next opening pass.
                _decisionState.NoteCapture(
                    request.RoomId,
                    request.Round,
                    request.StateVersion.Value,
                    request.MonotonicMillis);
            }

            return new TableProjectionContextData(
                request.Round.RoundId,
                request.StateVersion,
                1UL,
                cheat: CheatState.Initialize(DefaultEconomy), // seat streams require a non-null trusted aggregate; the actor overlays its own
                BuildPublicFacts(this, request),
                BuildPrivateFacts(request.Round),
                new TableSessionProjection(false, null, null, null),
                new TableProjectionPolicy(),
                BuildEventFacts(request),
                concealedMeldFacts: null);
        }

        /// <summary>Per-seat replay state derived from the committed event chain.</summary>
        private sealed class DiscardReplay
        {
            public List<bool> Tsumogiri = new List<bool>();

            public List<bool> RiichiDiscard = new List<bool>();

            public List<uint> Turns = new List<uint>();
        }

        /// <summary>
        /// The provable tsumogiri identity of one discard, derived ONLY from this capture's
        /// own chain walk plus the version-0 anchor round the room was created from — never
        /// from state a discarded candidate's earlier capture could have poisoned (round 5
        /// D-1-01 b):
        ///  * an own pending drawn tile decides by identity (that exact tile is a tsumogiri);
        ///  * an own claim (pon/chi/kan) on the chain is POSITIVE evidence the seat's next
        ///    no-draw discard carries called tiles — a provable false, not a guess (round 5
        ///    D-1-01 a: the post-meld discard must project, never block);
        ///  * a seat with no own chain event at all opened on the deal itself: the anchor
        ///    round's pending drawn evidence (the Domain's deal-extra record) decides, and
        ///    absent that evidence the projection fails closed rather than guessing.
        /// </summary>
        private bool ResolveTsumogiri(int seat, TileId discarded, TileId? pendingDrawn, bool ownMeldSeen)
        {
            if (pendingDrawn.HasValue)
            {
                return pendingDrawn.Value.Equals(discarded);
            }

            if (ownMeldSeen)
            {
                return false;
            }

            if (_initialRound != null &&
                _initialRound.Tiles.DrawnTile is DrawnTileEvidence anchor &&
                anchor.Seat == seat)
            {
                return anchor.Tile.Equals(discarded);
            }

            throw new InvalidOperationException(
                "A committed discard seat " + seat + " tile " + discarded.Value +
                " has no chain-provable drawn evidence and no deal-extra anchor; the projection refuses to guess.");
        }

        /// <summary>
        /// One pure pass over the round's OWN event log — the single authoritative chain a
        /// capture derives from (round 5 D-1-01 a/b/d). The actor hands every capture the
        /// round state that already contains the events being projected (its durable facts
        /// capture runs pre-commit with the candidate round), so nothing is cached across
        /// captures: a candidate dropped at commit, and a same-version alternative captured
        /// afterwards, each derive their facts independently and identically.
        /// Per event position this yields:
        ///  - the honest turn ordinal: 1 until the round's first discard, then +1 for every
        ///    later draw event — so a deal-extra opening (no opening draw event) starts its
        ///    second turn with the first chain draw, while a normal deal's opening draw stays
        ///    turn 1 because no discard preceded it;
        ///  - the per-discard tsumogiri/riichi-discard truth (null for non-discard events);
        ///  - the river-survival mask: a pon/chi/open-kan claim physically takes the claimed
        ///    tile out of the discarder's river into the meld (the Domain's Discard zone no
        ///    longer holds it), so the replay must drop that discard entry too or the public
        ///    river facts would pair tiles with another discard's turn/tsumogiri facts.
        /// </summary>
        private void WalkChain(
            RoundStateMachine round,
            uint[] turnAt,
            DiscardFacts?[] discardAt,
            bool[] inRiver)
        {
            uint turn = 1;
            bool sawDiscard = false;
            var pendingDrawn = new TileId?[4];
            var riichiPending = new bool[4];
            var ownMeldSeen = new bool[4];
            var river = new List<int>();

            IReadOnlyList<RoundEvent> events = round.Events;
            for (int i = 0; i < events.Count; i++)
            {
                RoundEvent roundEvent = events[i];
                int seat = roundEvent.Seat;
                bool validSeat = seat >= 0 && seat <= 3;
                switch (roundEvent.Type)
                {
                    case RoundEventType.TileDrawn:
                    case RoundEventType.RinshanDrawn:
                        if (sawDiscard)
                        {
                            turn++;
                        }

                        turnAt[i] = turn;
                        if (validSeat && roundEvent.Tile.HasValue)
                        {
                            pendingDrawn[seat] = roundEvent.Tile.Value;
                        }

                        break;
                    case RoundEventType.TileDiscarded:
                        turnAt[i] = turn;
                        sawDiscard = true;
                        river.Add(i);
                        inRiver[i] = true;
                        if (validSeat && roundEvent.Tile.HasValue)
                        {
                            discardAt[i] = new DiscardFacts(
                                riichiPending[seat],
                                ResolveTsumogiri(seat, roundEvent.Tile.Value, pendingDrawn[seat], ownMeldSeen[seat]),
                                unchecked((uint)roundEvent.Tile.Value.Value));
                            pendingDrawn[seat] = null;
                            riichiPending[seat] = false;
                        }

                        break;
                    case RoundEventType.RiichiDeclared:
                        turnAt[i] = turn;
                        if (validSeat)
                        {
                            riichiPending[seat] = true;
                        }

                        break;
                    case RoundEventType.PonDeclared:
                    case RoundEventType.ChiDeclared:
                    case RoundEventType.KanDeclared:
                        turnAt[i] = turn;
                        if (validSeat)
                        {
                            ownMeldSeen[seat] = true;
                        }

                        // Only a claim on the open discard removes a river tile; a closed
                        // kan (or an added kan over an own pon) takes nothing from it.
                        if (roundEvent.Type == RoundEventType.KanDeclared &&
                            !string.Equals(roundEvent.Detail, "OPEN_KAN", StringComparison.Ordinal))
                        {
                            break;
                        }

                        ClaimRiverTile(river, events, roundEvent, inRiver);
                        break;
                    default:
                        turnAt[i] = turn;
                        break;
                }
            }
        }

        /// <summary>
        /// Takes the claimed tile out of the replayed river: the claim window only opens for
        /// the tile just discarded, so the claim's target must match the newest river entry
        /// that survives — any other shape is a chain divergence and fails closed.
        /// </summary>
        private static void ClaimRiverTile(
            List<int> river,
            IReadOnlyList<RoundEvent> events,
            RoundEvent claim,
            bool[] inRiver)
        {
            for (int j = river.Count - 1; j >= 0; j--)
            {
                int discardIndex = river[j];
                RoundEvent discarded = events[discardIndex];
                if (!claim.Tile.HasValue || !discarded.Tile.HasValue || !claim.Tile.Value.Equals(discarded.Tile.Value))
                {
                    continue;
                }

                river.RemoveAt(j);
                inRiver[discardIndex] = false;
                return;
            }

            throw new InvalidOperationException(
                "A committed " + claim.Type + " by seat " + claim.Seat + " claims tile " +
                (claim.Tile.HasValue ? claim.Tile.Value.Value.ToString() : "?") +
                " but no surviving river discard carries it; the projection refuses to guess.");
        }

        /// <summary>
        /// Locates a projected record inside the round's own event chain. The actor's
        /// records mirror the round's events one-to-one in commit order, so an ordered
        /// forward match on (type, seat, tile) is exact; a record the chain does not contain
        /// is a projection/chain divergence and fails closed — never a guessed alignment.
        /// </summary>
        private static int MatchRecordPosition(
            IReadOnlyList<RoundEvent> events,
            TableEventRecord record,
            int from)
        {
            for (int i = Math.Max(0, from); i < events.Count; i++)
            {
                RoundEvent candidate = events[i];
                if (candidate.Type != record.Type || candidate.Seat != record.Seat)
                {
                    continue;
                }

                if (candidate.Tile.HasValue != record.Tile.HasValue)
                {
                    continue;
                }

                if (!candidate.Tile.HasValue || candidate.Tile.Value.Equals(record.Tile!.Value))
                {
                    return i;
                }
            }

            throw new InvalidOperationException(
                "A projected " + record.Type + " record (seat " + record.Seat + ", seq " +
                record.Seq.Value + ") has no matching committed round event; the projection refuses to guess.");
        }

        /// <summary>
        /// Full-chain river replay: the same single pure chain walk every other fact derives
        /// from (round 5 D-1-01 a/b/d), so the public river view and the per-event facts can
        /// never disagree about a discard's tsumogiri/riichi identity.
        /// </summary>
        private DiscardReplay[] BuildReplay(TableProjectionContextRequest request)
        {
            var replay = new DiscardReplay[4];
            for (int seat = 0; seat < 4; seat++)
            {
                replay[seat] = new DiscardReplay();
            }

            IReadOnlyList<RoundEvent> events = request.Round.Events;
            var turnAt = new uint[events.Count];
            var discardAt = new DiscardFacts?[events.Count];
            var inRiver = new bool[events.Count];
            WalkChain(request.Round, turnAt, discardAt, inRiver);
            for (int i = 0; i < events.Count; i++)
            {
                // The replay list mirrors the Domain's physical Discard zone entry by
                // entry: claimed-away discards are masked out so river tile i pairs with
                // the i-th river-surviving chain discard (its own turn/tsumogiri truth).
                if (inRiver[i] && discardAt[i] is DiscardFacts facts && events[i].Seat >= 0 && events[i].Seat <= 3)
                {
                    replay[events[i].Seat].Tsumogiri.Add(facts.Tsumogiri);
                    replay[events[i].Seat].RiichiDiscard.Add(facts.RiichiDiscard);
                    replay[events[i].Seat].Turns.Add(turnAt[i]);
                }
            }

            return replay;
        }

        private static IReadOnlyList<TableSeatPublicFacts> BuildPublicFacts(
            TableProjectionContextSource source,
            TableProjectionContextRequest request)
        {
            DiscardReplay[] replay = source.BuildReplay(request);
            RoundStateMachine round = request.Round;
            var result = new List<TableSeatPublicFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                if (river.Count != replay[seat].Turns.Count)
                {
                    // The replay mirrors the physical river one-for-one (claims included);
                    // any residue is a projection/chain divergence, never a guess.
                    throw new InvalidOperationException(
                        "Seat " + seat + " physical river holds " + river.Count + " tiles but the " +
                        "committed chain replays " + replay[seat].Turns.Count + "; the projection refuses to guess.");
                }

                var discards = new List<DiscardView>(river.Count);
                for (int i = 0; i < river.Count; i++)
                {
                    TileId discarded = river[i];
                    bool tsumogiri = replay[seat].Tsumogiri[i];
                    bool riichiDiscard = replay[seat].RiichiDiscard[i];
                    // The wire turn number is the CHAIN turn (1 before a seat's first
                    // discard, +1 per new draw afterwards) walked from the same committed
                    // events — never the river index (round 5 D-1-01 d).
                    uint turn = replay[seat].Turns[i];
                    discards.Add(new DiscardView(
                        unchecked((uint)discarded.Value),
                        turn,
                        riichiDiscard,
                        tsumogiri));
                }

                result.Add(new TableSeatPublicFacts(
                    seat,
                    riichi: round.IsRiichi(seat),
                    melds: BuildMeldFacts(round, seat),
                    discards));
            }

            return result;
        }

        private static IReadOnlyList<TableMeldFact>? BuildMeldFacts(RoundStateMachine round, int seat)
        {
            if (round.Melds.Count == 0)
            {
                return null;
            }

            var melds = new List<TableMeldFact>(round.Melds.Count);
            foreach (RoundMeld meld in round.Melds)
            {
                if (meld.Seat != seat)
                {
                    continue;
                }

                var tiles = new List<uint>(meld.Tiles.Count);
                foreach (TileId tile in meld.Tiles)
                {
                    tiles.Add(unchecked((uint)tile.Value));
                }

                // The Domain places a closed kan's four physical tiles in the Meld zone like
                // any other meld, and the visibility projector requires the public meld facts
                // to cover that zone exactly — so the closed-kan fact stays present as its own
                // concealed meld (the board encoder itself redacts it per receiver and the
                // owner-only event fact carries the identities, round 5 D-1-01 c). A closed
                // kan has no public called tile and no claimed source seat.
                bool concealed = meld.Kind == RoundMeldKind.ClosedKan;
                melds.Add(new TableMeldFact(
                    meld.Kind == RoundMeldKind.Chi ? ProtocolMeldType.Chi
                        : meld.Kind == RoundMeldKind.Pon ? ProtocolMeldType.Pon
                        : meld.Kind == RoundMeldKind.OpenKan ? ProtocolMeldType.OpenKan
                        : concealed ? ProtocolMeldType.ClosedKan
                        : ProtocolMeldType.AddedKan,
                    tiles,
                    concealed ? null : unchecked((uint)meld.CalledTile.Value),
                    concealed ? ProtocolConstants.NoSeat : meld.FromSeat));
            }

            return melds.Count == 0 ? null : melds;
        }

        private static IReadOnlyList<TableSeatPrivateFacts> BuildPrivateFacts(RoundStateMachine round)
        {
            var result = new List<TableSeatPrivateFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                var history = new List<uint>(river.Count);
                for (int i = 0; i < river.Count; i++)
                {
                    history.Add(unchecked((uint)river[i].Value));
                }

                result.Add(new TableSeatPrivateFacts(
                    seat,
                    furiten: false, // the Domain does not model furiten; never asserted true
                    hasPreparation: false, // owner-preparation overlays stay actor-private facts
                    history));
            }

            return result;
        }

        private IReadOnlyList<TableProjectionEventFact> BuildEventFacts(
            TableProjectionContextRequest request)
        {
            RoundStateMachine round = request.Round;
            IReadOnlyList<RoundEvent> events = round.Events;
            IReadOnlyList<TableEventRecord> records = request.CommittedEvents;

            // One pure chain walk derives every position's turn ordinal and discard truth
            // (round 5 D-1-01 a/b/d) — no state is carried across captures, so a candidate
            // dropped at commit can never poison a same-version alternative captured after.
            var turnAt = new uint[events.Count];
            var discardAt = new DiscardFacts?[events.Count];
            WalkChain(round, turnAt, discardAt, new bool[events.Count]);

            // Reconstruct each draw's wall count by replaying the round's own draw events
            // backwards from the current wall total.
            var wallAt = new uint?[events.Count];
            long wall = round.Tiles.WallRemaining;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Type == RoundEventType.TileDrawn)
                {
                    wallAt[i] = unchecked((uint)wall);
                    wall++;
                }
            }

            // Pair each KanDeclared chain position with the seat's exact Domain kan meld in
            // chain order, so the projector never needs a fabricated meld shape.
            var meldAt = new MeldView?[events.Count];
            var kanPositionsBySeat = new Dictionary<int, List<int>>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Type == RoundEventType.KanDeclared &&
                    events[i].Seat >= 0 &&
                    events[i].Seat <= 3)
                {
                    if (!kanPositionsBySeat.TryGetValue(events[i].Seat, out List<int>? positions))
                    {
                        positions = new List<int>();
                        kanPositionsBySeat[events[i].Seat] = positions;
                    }

                    positions.Add(i);
                }
            }

            foreach (KeyValuePair<int, List<int>> entry in kanPositionsBySeat)
            {
                int seat = entry.Key;
                var kanMelds = new List<RoundMeld>();
                foreach (RoundMeld meld in round.Melds)
                {
                    if (meld.Seat == seat &&
                        (meld.Kind == RoundMeldKind.OpenKan ||
                         meld.Kind == RoundMeldKind.ClosedKan ||
                         meld.Kind == RoundMeldKind.AddedKan))
                    {
                        kanMelds.Add(meld);
                    }
                }

                for (int i = 0; i < entry.Value.Count; i++)
                {
                    if (i >= kanMelds.Count)
                    {
                        throw new InvalidOperationException(
                            "A committed kan event for seat " + seat +
                            " has no matching Domain kan meld record.");
                    }

                    RoundMeld meld = kanMelds[i];
                    var tiles = new List<uint>(meld.Tiles.Count);
                    foreach (TileId tile in meld.Tiles)
                    {
                        tiles.Add(unchecked((uint)tile.Value));
                    }

                    bool concealed = meld.Kind == RoundMeldKind.ClosedKan;
                    meldAt[entry.Value[i]] = new MeldView(
                        meld.Kind == RoundMeldKind.OpenKan ? ProtocolMeldType.OpenKan
                            : concealed ? ProtocolMeldType.ClosedKan
                            : ProtocolMeldType.AddedKan,
                        tiles,
                        // The owner-scoped closed-kan fact carries the four physical
                        // identities and nothing else: no called tile, no claimed source
                        // seat (the frozen owner-validation refuses both).
                        concealed ? null : unchecked((uint)meld.CalledTile.Value),
                        concealed ? ProtocolConstants.NoSeat : meld.FromSeat);
                }
            }

            // The actor's records mirror the round's own chain as its tail (the full chain,
            // or exactly the candidate's own new events). Associate every record with its
            // chain position by content in commit order and fail closed on any divergence —
            // never a guessed alignment, never an ordinal-namespace mix (round 5 D-1-01 a).
            int cursor = Math.Max(0, events.Count - records.Count);
            var facts = new List<TableProjectionEventFact>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                TableEventRecord record = records[i];
                int position = MatchRecordPosition(events, record, cursor);
                cursor = position + 1;
                TableProjectionEventFacts? factsValue = BuildOne(
                    request,
                    record,
                    wallAt[position],
                    turnAt[position],
                    discardAt[position],
                    meldAt[position]);
                if (factsValue != null)
                {
                    facts.Add(new TableProjectionEventFact(
                        MapType(record.Type),
                        record.Seq,
                        record.CommandVersion,
                        unchecked((uint)record.Ordinal),
                        factsValue));
                }
            }

            return facts;
        }

        private sealed class DiscardFacts
        {
            public DiscardFacts(bool riichiDiscard, bool tsumogiri, uint tileId)
            {
                RiichiDiscard = riichiDiscard;
                Tsumogiri = tsumogiri;
                TileId = tileId;
            }

            public bool RiichiDiscard { get; }

            public bool Tsumogiri { get; }

            public uint TileId { get; }
        }

        private static TableProjectionEventFacts? BuildOne(
            TableProjectionContextRequest request,
            TableEventRecord record,
            uint? wallRemaining,
            uint turnNumber,
            DiscardFacts? discardFacts,
            MeldView? meldFacts)
        {
            switch (record.Type)
            {
                case RoundEventType.TilesDealt:
                case RoundEventType.DoraRevealed:
                case RoundEventType.RinshanDrawn:
                    return new TableProjectionEventFacts(turnNumber: turnNumber);
                case RoundEventType.TileDrawn:
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        wallRemaining: wallRemaining ??
                            unchecked((uint)request.Round.Tiles.WallRemaining));
                case RoundEventType.TileDiscarded:
                    if (discardFacts == null)
                    {
                        // A committed discard without its chain-derived truth must not be
                        // published with guessed flags.
                        throw new InvalidOperationException(
                            "A committed discard event requires its chain-derived tsumogiri/riichi truth.");
                    }

                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        riichiDiscard: discardFacts.RiichiDiscard,
                        tsumogiri: discardFacts.Tsumogiri,
                        discardTileId: discardFacts.TileId);
                case RoundEventType.KanDeclared:
                    if (meldFacts == null)
                    {
                        // The Domain carries only the called tile; the full meld shape is a
                        // scoring/rules fact. Fail closed rather than guess.
                        throw new InvalidOperationException(
                            "KanDeclared projection requires the committed Domain meld record.");
                    }

                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        meld: meldFacts);
                case RoundEventType.TsumoWin:
                case RoundEventType.RonWin:
                    // Yaku/han/fu/score are scoring-authority facts. Fail closed.
                    throw new InvalidOperationException(
                        "Win projection requires scoring facts from a rules authority (P5.2+ boundary).");
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw:
                    RoundResult draw = request.Round.Result ??
                        throw new InvalidOperationException("A draw event requires the committed round result.");
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        tenpaiPlayers: draw.Outcome.TenpaiSeats);
                case RoundEventType.RoundEnded:
                    return new TableProjectionEventFacts(
                        turnNumber: turnNumber,
                        roundWind: unchecked((uint)request.Round.RoundWind),
                        roundNumber: unchecked((uint)request.Round.RoundNumber));
                default:
                    return null;
            }
        }

        private static ProtocolEventType MapType(RoundEventType type)
        {
            switch (type)
            {
                case RoundEventType.TilesDealt: return ProtocolEventType.DealingComplete;
                case RoundEventType.DoraRevealed: return ProtocolEventType.DoraRevealed;
                case RoundEventType.TileDrawn: return ProtocolEventType.TileDrawn;
                case RoundEventType.RinshanDrawn: return ProtocolEventType.RinshanDraw;
                case RoundEventType.TileDiscarded: return ProtocolEventType.TileDiscarded;
                case RoundEventType.KanDeclared: return ProtocolEventType.KanDeclared;
                case RoundEventType.TsumoWin: return ProtocolEventType.TsumoWin;
                case RoundEventType.RonWin: return ProtocolEventType.RonWin;
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw: return ProtocolEventType.DrawGame;
                case RoundEventType.RoundEnded: return ProtocolEventType.RoundEnded;
                default:
                    throw new InvalidOperationException("No frozen protocol event type for " + type + ".");
            }
        }
    }
}

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
    ///  - the per-event turn number is the honest per-event turn ordinal (turn 1 until the
    ///    first discard of the round, then +1 per new draw turn); facts the Domain genuinely
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

        /// <summary>Version-0 anchor (round captured Round-0). The trusted pointer from 0 to 1.</summary>
        private readonly RoundStateMachine? _initialRound;

        /// <summary>Per-round chain truth: the committed per-seat pending-drawn witnesses.</summary>
        private readonly Dictionary<string, IReadOnlyList<TileId>?> _drawnEvidenceBySource = new Dictionary<string, IReadOnlyList<TileId>?>(StringComparer.Ordinal);

        /// <summary>D-1-01 round 3: last captured round (capture happens per commit into the chain).</summary>
        private readonly object _chainGate = new object();
        private long _trackedVersion = -1;
        private RoundStateMachine? _trackedRound;
        private int _trackedEventCount;

        /// <summary>
        /// The per-seq committed-discard tsumogiri truth derived AT COMMIT TIME (the only
        /// moment the parent state's DrawnTileEvidence still exists): an in-chain derivation
        /// reconstructs everything the chain records (draw events); the committed-truth map
        /// covers what the chain cannot (the initial deal-extra tile the Domain records as
        /// evidence, removed silently by the seat's own first discard).
        /// </summary>
        private readonly Dictionary<string, bool> _committedTsumogiri = new Dictionary<string, bool>(StringComparer.Ordinal);

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

            // D-1-01 round 3: the capture boundary is the ONLY place where the parent state's
            // per-discard DrawnTileEvidence still exists; record the tsumogiri truth of every
            // newly committed discard BEFORE the fact is consumed by the projection below.
            TrackCommittedDiscards(request);

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
        }

        /// <summary>
        /// True when the committed chain can PROVE this discard was the seat's drawn tile
        /// (a committed TileDrawn/RinshanDrawn event for that seat exists between the seat's
        /// previous discard and this one). Anything else — above all a round's very first
        /// discard of an already-dealt round (the Domain's deal-EXTRA evidence) — is not
        /// chain-provable and must fail closed rather than publish a silent false.
        /// </summary>
        private static bool TsumogiriFromChain(TableProjectionContextRequest request, RoundEvent discardEvent)
        {
            IReadOnlyList<RoundEvent> events = request.Round.Events;
            int seat = discardEvent.Seat;
            for (int i = discardEvent.Ordinal - 1; i >= 0; i--)
            {
                RoundEvent previous = events[i];
                if (previous.Seat != seat)
                {
                    continue;
                }

                switch (previous.Type)
                {
                    case RoundEventType.TileDrawn:
                    case RoundEventType.RinshanDrawn:
                        return previous.Tile.HasValue && previous.Tile.Value.Equals(discardEvent.Tile!.Value);
                    case RoundEventType.TileDiscarded:
                        // An earlier own discard with no draw between: never a drawn tile.
                        return false;
                }
            }

            // No committed draw ever touched this seat: the discard came from the deal
            // itself — chain-unprovable (the trust map must have recorded it at commit).
            throw new InvalidOperationException(
                "A committed discard seat " + seat + " seq " + discardEvent.Ordinal +
                " has no committed drawn evidence or recorded at-commit tsumogiri truth; the projection refuses to guess.");
        }

        /// <summary>Event key inside the room's committed chain (seq is room-global and stable).</summary>
        private static string Key(string roundId, ulong chainOrdinal)
        {
            return roundId + "/" + chainOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Records the tsumogiri truth of every newly committed discard event D-1-01 round 3.
        /// Capture runs serially inside the actor per commit, so the previous captured round
        /// is the discarded state's PARENT for the normal commit cadence (version+1). The
        /// parent's own DrawnTileEvidence (wall draw, rinshan draw or deal-extra) is the
        /// authoritative pending-drawn the Domain kept exactly until this discard; that is
        /// the fact the frozen chain cannot express for the deal-extra case above all.
        /// </summary>
        private void TrackCommittedDiscards(TableProjectionContextRequest request)
        {
            RoundStateMachine round = request.Round;
            lock (_chainGate)
            {
                long version = round.Version;
                if (_trackedRound != null && version <= _trackedVersion)
                {
                    // Idempotent re-capture of the same committed state (stream baselines).
                    return;
                }

                RoundStateMachine? parent = null;
                if (_trackedRound == null)
                {
                    // First capture ever seen by THIS room's source. The natural parent is
                    // the version-0 anchor (the round the registry created the room from)
                    // when the room's first commit produced version exactly 1.
                    parent = version == 1 ? _initialRound : null;
                }
                else if (version == _trackedVersion + 1)
                {
                    parent = _trackedRound;
                }
                // version > tracked+1 with no anchor: a capture-skip gap; already-unknown
                // discards keep failing closed below instead of being silently re-published.

                if (parent != null)
                {
                    DrawnTileEvidence? parentDrawn = parent.Tiles.DrawnTile;
                    for (int i = _trackedRound == null ? 0 : _trackedEventCount; i < request.CommittedEvents.Count; i++)
                    {
                        TableEventRecord record = request.CommittedEvents[i];
                        if (record.Type != RoundEventType.TileDiscarded || !record.Tile.HasValue)
                        {
                            continue;
                        }

                        string key = Key(request.Round.RoundId, (ulong)record.Ordinal);
                        if (_committedTsumogiri.ContainsKey(key))
                        {
                            continue;
                        }

                        bool tsumogiri = parentDrawn.HasValue &&
                                        parentDrawn.Value.Seat == record.Seat &&
                                        parentDrawn.Value.Tile.Equals(record.Tile!.Value);
                        _committedTsumogiri[key] = tsumogiri;
                    }
                }

                _trackedRound = round;
                _trackedVersion = version;
                _trackedEventCount = request.CommittedEvents.Count;
            }
        }

        /// <summary>
        /// Full-chain replay (D-1-01): one pass over the round's own committed event chain in
        /// chronological order. A drawn tile stays the seat's "pending drawn" only until that
        /// seat's next discard; a riichi declaration stays pending until that seat's next
        /// discard. Both histories are kept per chronological discard so the earlier river
        /// entries keep their own exact identity. Round 3: a discard whose tsumogiri identity
        /// is not chain-provable is resolved ONLY from the at-commit truth map and never
        /// silently defaulted to false — a discard with no provenance at all fails closed.
        /// </summary>
        private DiscardReplay[] BuildReplay(TableProjectionContextRequest request)
        {
            var replay = new DiscardReplay[4];
            for (int seat = 0; seat < 4; seat++)
            {
                replay[seat] = new DiscardReplay();
            }

            var pendingDrawn = new TileId?[4];
            var riichiPending = new bool[4];
            IReadOnlyList<RoundEvent> events = request.Round.Events;
            for (int i = 0; i < events.Count; i++)
            {
                RoundEvent roundEvent = events[i];
                int seat = roundEvent.Seat;
                if (seat < 0 || seat > 3)
                {
                    continue;
                }

                switch (roundEvent.Type)
                {
                    case RoundEventType.TileDrawn:
                    case RoundEventType.RinshanDrawn:
                        if (roundEvent.Tile.HasValue)
                        {
                            pendingDrawn[seat] = roundEvent.Tile.Value;
                        }

                        break;
                    case RoundEventType.TileDiscarded:
                        if (!roundEvent.Tile.HasValue)
                        {
                            continue;
                        }

                        bool tsumogiri;
                        if (pendingDrawn[seat].HasValue && pendingDrawn[seat]!.Value.Equals(roundEvent.Tile.Value))
                        {
                            tsumogiri = true;
                        }
                        else
                        {
                            tsumogiri = _committedTsumogiri.TryGetValue(
                                Key(request.Round.RoundId, (ulong)roundEvent.Ordinal),
                                out bool recorded)
                                ? recorded
                                : TsumogiriFromChain(request, roundEvent);
                        }

                        replay[seat].Tsumogiri.Add(tsumogiri);
                        replay[seat].RiichiDiscard.Add(riichiPending[seat]);
                        pendingDrawn[seat] = null;
                        riichiPending[seat] = false;
                        break;
                    case RoundEventType.RiichiDeclared:
                        riichiPending[seat] = true;
                        break;
                    default:
                        // A claimed/re-opened window consumed the pending drawn evidence only
                        // through this seat's own discard, which the discard branch already
                        // cleared; other events do not touch per-discard truth.
                        break;
                }
            }

            // Chain integrity against the round's own registers: a riichi seat's river carries
            // exactly the pending riichi discard that the projected chain knows about. The
            // projector re-validates every fact against the immutable map below either way.
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
                var discards = new List<DiscardView>(river.Count);
                for (int i = 0; i < river.Count; i++)
                {
                    TileId discarded = river[i];
                    bool tsumogiri = replay[seat].Tsumogiri.Count > i && replay[seat].Tsumogiri[i];
                    bool riichiDiscard = replay[seat].RiichiDiscard.Count > i && replay[seat].RiichiDiscard[i];
                    discards.Add(new DiscardView(
                        unchecked((uint)discarded.Value),
                        unchecked((uint)i),
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

                if (meld.Kind == RoundMeldKind.ClosedKan)
                {
                    // A closed-kan identity is never placed in a public Contracts meld (only
                    // the owner extension may carry it).
                    continue;
                }

                var tiles = new List<uint>(meld.Tiles.Count);
                foreach (TileId tile in meld.Tiles)
                {
                    tiles.Add(unchecked((uint)tile.Value));
                }

                melds.Add(new TableMeldFact(
                    meld.Kind == RoundMeldKind.Chi ? ProtocolMeldType.Chi
                        : meld.Kind == RoundMeldKind.Pon ? ProtocolMeldType.Pon
                        : meld.Kind == RoundMeldKind.OpenKan ? ProtocolMeldType.OpenKan
                        : ProtocolMeldType.AddedKan,
                    tiles,
                    unchecked((uint)meld.CalledTile.Value),
                    meld.FromSeat));
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
            // Reconstruct each draw's wall count by replaying the committed draw events
            // backwards from the current wall total.
            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = request.Round.Tiles.WallRemaining;
            for (int i = request.CommittedEvents.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = request.CommittedEvents[i];
                if (record.Type == RoundEventType.TileDrawn)
                {
                    wallBySeq[record.Seq.Value] = unchecked((uint)wall);
                    wall++;
                }
            }

            var turnBySeq = new Dictionary<ulong, uint>();
            var discardBySeq = new Dictionary<ulong, DiscardFacts>();
            var meldBySeq = new Dictionary<ulong, MeldView>();
            BuildChainFacts(request, turnBySeq, discardBySeq, meldBySeq);

            var facts = new List<TableProjectionEventFact>(request.CommittedEvents.Count);
            for (int i = 0; i < request.CommittedEvents.Count; i++)
            {
                TableEventRecord record = request.CommittedEvents[i];
                uint? wallRemaining = wallBySeq.TryGetValue(record.Seq.Value, out uint value)
                    ? value
                    : (uint?)null;
                uint turnNumber = turnBySeq.TryGetValue(record.Seq.Value, out uint turn)
                    ? turn
                    : 1U;
                DiscardFacts? discardFacts = discardBySeq.TryGetValue(record.Seq.Value, out DiscardFacts? found)
                    ? found
                    : null;
                MeldView? meldFacts = meldBySeq.TryGetValue(record.Seq.Value, out MeldView? meld) ? meld : null;
                TableProjectionEventFacts? factsValue = BuildOne(
                    request,
                    record,
                    wallRemaining,
                    turnNumber,
                    discardFacts,
                    meldFacts);
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

        /// <summary>
        /// Walks the whole committed chain once and derives, per event sequence:
        ///  - the honest turn ordinal (1 through the opening draw turn, +1 for every later new
        ///    draw turn);
        ///  - the per-discard tsumogiri/riichi-discard flags from the same chain order the
        ///    river reconstruction uses (D-1-01 round 3: a chain-unprovable discard falls back
        ///    to the at-commit truth map — never to a silent false);
        ///  - the exact Domain meld record for a kan event, so the projector never needs a
        ///    fabricated meld shape.
        /// </summary>
        private void BuildChainFacts(
            TableProjectionContextRequest request,
            Dictionary<ulong, uint> turnBySeq,
            Dictionary<ulong, DiscardFacts> discardBySeq,
            Dictionary<ulong, MeldView> meldBySeq)
        {
            uint turn = 1;
            bool sawFirstDraw = false;
            var pendingDrawn = new TileId?[4];
            var riichiPending = new bool[4];
            var kanEventsBySeat = new Dictionary<int, List<ulong>>();

            for (int i = 0; i < request.CommittedEvents.Count; i++)
            {
                TableEventRecord record = request.CommittedEvents[i];
                ulong seq = record.Seq.Value;
                int seat = record.Seat;
                bool validSeat = seat >= 0 && seat <= 3;

                switch (record.Type)
                {
                    case RoundEventType.TileDrawn:
                    case RoundEventType.RinshanDrawn:
                        // D-1-01 round 3 (review's BuildChainFacts turn audit): a draw event
                        // OPENS the next turn, so its own turn ordinal is the NEW turn — the
                        // increment must happen BEFORE the assignment, not after (a drawn
                        // event that belonged to the previous turn made every subsequent
                        // event's turns off by one).
                        if (sawFirstDraw)
                        {
                            turn++;
                        }

                        turnBySeq[seq] = turn;
                        sawFirstDraw = true;
                        if (validSeat && record.Tile.HasValue)
                        {
                            pendingDrawn[seat] = record.Tile.Value;
                        }

                        break;
                    case RoundEventType.TileDiscarded:
                        turnBySeq[seq] = turn;
                        if (validSeat && record.Tile.HasValue)
                        {
                            TileId discarded = record.Tile.Value;
                            bool tsumogiri;
                            if (pendingDrawn[seat].HasValue && pendingDrawn[seat]!.Value.Equals(discarded))
                            {
                                tsumogiri = true;
                            }
                            else
                            {
                                tsumogiri = _committedTsumogiri.TryGetValue(
                                    Key(request.Round.RoundId, (ulong)record.Ordinal),
                                    out bool recorded)
                                    ? recorded
                                    : TsumogiriFromChain(request, new RoundEvent(
                                        record.Type,
                                        record.Ordinal,
                                        record.Seat,
                                        record.Tile,
                                        record.Detail));
                            }

                            bool riichi = riichiPending[seat];
                            pendingDrawn[seat] = null;
                            riichiPending[seat] = false;
                            discardBySeq[seq] = new DiscardFacts(
                                riichi,
                                tsumogiri,
                                unchecked((uint)discarded.Value));
                        }

                        break;
                    case RoundEventType.RiichiDeclared:
                        turnBySeq[seq] = turn;
                        if (validSeat)
                        {
                            riichiPending[seat] = true;
                        }

                        break;
                    case RoundEventType.KanDeclared:
                        turnBySeq[seq] = turn;
                        if (validSeat)
                        {
                            if (!kanEventsBySeat.TryGetValue(seat, out List<ulong>? perSeat))
                            {
                                perSeat = new List<ulong>();
                                kanEventsBySeat[seat] = perSeat;
                            }

                            perSeat.Add(seq);
                        }

                        break;
                    default:
                        turnBySeq[seq] = turn;
                        break;
                }
            }

            foreach (KeyValuePair<int, List<ulong>> entry in kanEventsBySeat)
            {
                int seat = entry.Key;
                var kanMelds = new List<RoundMeld>();
                foreach (RoundMeld meld in request.Round.Melds)
                {
                    if (meld.Seat == seat &&
                        (meld.Kind == RoundMeldKind.OpenKan ||
                         meld.Kind == RoundMeldKind.ClosedKan ||
                         meld.Kind == RoundMeldKind.AddedKan))
                    {
                        kanMelds.Add(meld);
                    }
                }

                for (int i = 0; i < entry.Value.Count && i < kanMelds.Count; i++)
                {
                    ulong seq = entry.Value[i];
                    RoundMeld meld = kanMelds[i];
                    var tiles = new List<uint>(meld.Tiles.Count);
                    foreach (TileId tile in meld.Tiles)
                    {
                        tiles.Add(unchecked((uint)tile.Value));
                    }

                    meldBySeq[seq] = new MeldView(
                        meld.Kind == RoundMeldKind.OpenKan ? ProtocolMeldType.OpenKan
                            : meld.Kind == RoundMeldKind.ClosedKan ? ProtocolMeldType.ClosedKan
                            : ProtocolMeldType.AddedKan,
                        tiles,
                        unchecked((uint)meld.CalledTile.Value),
                        meld.FromSeat);
                }
            }
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Stable P3.2a projection rejection codes. They are deliberately separate from the
    /// Domain transition error codes: a projection failure never changes authoritative state.
    /// </summary>
    public enum TableProjectionErrorCode : byte
    {
        None = 0,
        InvalidContext = 1,
        RoundIdMismatch = 2,
        StaleStateVersion = 3,
        FutureCursor = 4,
        EventVersionMismatch = 5,
        EventOrderMismatch = 6,
        DuplicateSeat = 7,
        MissingPublicFacts = 8,
        PublicFactsMismatch = 9,
        PrivateFactsMismatch = 10,
        DecisionSeatMismatch = 11,
        UnsupportedAudience = 12,
        UnsupportedEvent = 13,
        UnknownEventType = 14,
        InvalidPayload = 15
    }

    /// <summary>A deterministic, value-only projection failure. It never carries Domain state.</summary>
    public sealed class TableProjectionException : Exception
    {
        public TableProjectionException(TableProjectionErrorCode errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public TableProjectionErrorCode ErrorCode { get; }
    }

    /// <summary>
    /// One stream identity/version tuple supplied by the actor after commit. Cursor allocation
    /// is P3.2b: this component copies the cursor it is given and never advances it.
    /// </summary>
    public sealed class TableProjectionIdentity
    {
        public TableProjectionIdentity(
            SessionId sessionId,
            RoundId roundId,
            StateVersion stateVersion,
            EventSeq eventSeq,
            StreamCursor cursor,
            SessionVersion sessionVersion)
        {
            SessionId = sessionId;
            RoundId = roundId;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            Cursor = cursor;
            SessionVersion = sessionVersion;
        }

        public SessionId SessionId { get; }

        public RoundId RoundId { get; }

        public StateVersion StateVersion { get; }

        public EventSeq EventSeq { get; }

        public StreamCursor Cursor { get; }

        public SessionVersion SessionVersion { get; }
    }

    /// <summary>Non-identifying projection switches. They are explicit inputs, never globals.</summary>
    public sealed class TableProjectionPolicy
    {
        public TableProjectionPolicy(
            bool paused = false,
            bool settlementBatch = false,
            bool revealSettlementUra = false)
        {
            Paused = paused;
            SettlementBatch = settlementBatch;
            RevealSettlementUra = revealSettlementUra;
        }

        public bool Paused { get; }

        /// <summary>True only for the explicit settlement batch; spectators gate on this.</summary>
        public bool SettlementBatch { get; }

        /// <summary>True only when the settlement batch is explicitly authorized to reveal ura.</summary>
        public bool RevealSettlementUra { get; }
    }

    /// <summary>Match-level summary fields owned by the match/session layer, not by a round.</summary>
    public sealed class TableSessionProjection
    {
        private readonly ReadOnlyCollection<long> _finalScores;
        private readonly ReadOnlyCollection<int> _rankings;

        public TableSessionProjection(
            bool finished,
            string? finishReason,
            IReadOnlyList<long>? finalScores,
            IReadOnlyList<int>? rankings,
            string? settlementRoundId = null)
        {
            string reason = finishReason ?? string.Empty;
            if (reason.Length > 0 && !ProtocolErrorCode.IsWellFormed(reason))
            {
                throw new ArgumentException("Finish reason must be a stable uppercase code.", nameof(finishReason));
            }

            _finalScores = TableProjectionCopy.Scores(finalScores, nameof(finalScores));
            _rankings = TableProjectionCopy.Rankings(rankings, nameof(rankings));
            SettlementRoundId = TableProjectionCopy.OptionalId(settlementRoundId);
            Finished = finished;
            FinishReason = reason;
        }

        public bool Finished { get; }

        public string FinishReason { get; }

        public IReadOnlyList<long> FinalScores => _finalScores;

        public IReadOnlyList<int> Rankings => _rankings;

        /// <summary>Just-ended round shown by the snapshot; empty while no settlement is displayed.</summary>
        public string SettlementRoundId { get; }
    }

    /// <summary>
    /// Public facts that the current Domain state does not carry: meld type/origin, discard
    /// turn/flags and riichi status. Every tile identity is still checked against the immutable
    /// <see cref="Mahjong.Domain.TilePositionMap"/> by the projector.
    /// </summary>
    public sealed class TableSeatPublicFacts
    {
        private readonly ReadOnlyCollection<TableMeldFact> _melds;
        private readonly ReadOnlyCollection<DiscardView> _discards;

        public TableSeatPublicFacts(
            int seat,
            bool riichi,
            IReadOnlyList<TableMeldFact>? melds,
            IReadOnlyList<DiscardView>? discards)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            _melds = TableProjectionCopy.Items(melds, nameof(melds), 4);
            _discards = TableProjectionCopy.Items(discards, nameof(discards), 200);
            for (int i = 0; i < _melds.Count; i++)
            {
                if (_melds[i] == null)
                {
                    throw new ArgumentException("Meld facts cannot contain null.", nameof(melds));
                }
            }

            for (int i = 0; i < _discards.Count; i++)
            {
                if (_discards[i] == null)
                {
                    throw new ArgumentException("Discard facts cannot contain null.", nameof(discards));
                }
            }

            Seat = seat;
            Riichi = riichi;
        }

        public int Seat { get; }

        public bool Riichi { get; }

        public IReadOnlyList<TableMeldFact> Melds => _melds;

        public IReadOnlyList<DiscardView> Discards => _discards;
    }

    /// <summary>
    /// Meld metadata that <see cref="Mahjong.Domain.TilePositionMap"/> does not model. The
    /// physical tiles are supplied so the projector can prove they are exactly the seat's
    /// Meld-zone tiles; a <see cref="ProtocolMeldType.ClosedKan"/> identity is never placed in
    /// a public Contracts meld (only the owner extension may carry it).
    /// </summary>
    public sealed class TableMeldFact
    {
        private readonly ReadOnlyCollection<uint> _tileIds;

        public TableMeldFact(
            ProtocolMeldType type,
            IReadOnlyList<uint>? tileIds,
            uint? calledTileId,
            int fromSeat)
        {
            if (type == ProtocolMeldType.Unspecified)
            {
                throw new ArgumentException("Meld type must be explicit.", nameof(type));
            }

            _tileIds = TableProjectionCopy.Tiles(tileIds, nameof(tileIds), type == ProtocolMeldType.Chi || type == ProtocolMeldType.Pon ? 3 : 4);
            int expected = type == ProtocolMeldType.Chi || type == ProtocolMeldType.Pon ? 3 : 4;
            if (_tileIds.Count != expected)
            {
                throw new ArgumentException("Meld " + type + " requires " + expected + " tiles.", nameof(tileIds));
            }

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

    /// <summary>
    /// Owner-private facts not present in Domain: furiten and the preparation overlay. The
    /// discard history may include claimed tiles; current river tiles are cross-checked.
    /// </summary>
    public sealed class TableSeatPrivateFacts
    {
        private readonly ReadOnlyCollection<uint> _discardHistory;

        public TableSeatPrivateFacts(
            int seat,
            bool furiten,
            bool hasPreparation,
            IReadOnlyList<uint>? discardHistory,
            bool temporaryFuriten = false,
            bool riichiFuriten = false)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            _discardHistory = TableProjectionCopy.Tiles(discardHistory, nameof(discardHistory), 200);
            Seat = seat;
            Furiten = furiten;
            HasPreparation = hasPreparation;
            TemporaryFuriten = temporaryFuriten;
            RiichiFuriten = riichiFuriten;
        }

        public int Seat { get; }

        public bool Furiten { get; }

        /// <summary>Pass-on-ron furiten that lasts until the owner's next draw.</summary>
        public bool TemporaryFuriten { get; }

        /// <summary>Riichi pass-on-ron furiten that lasts until the round ends.</summary>
        public bool RiichiFuriten { get; }

        public bool HasPreparation { get; }

        public IReadOnlyList<uint> DiscardHistory => _discardHistory;
    }

    /// <summary>One owner decision, already frozen by P3.3/P3.1 and supplied explicitly.</summary>
    public sealed class TableSeatDecision
    {
        public TableSeatDecision(int seat, DecisionView decision)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
            if (decision.DeadlineRemainingMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decision),
                    decision.DeadlineRemainingMs,
                    "A decision deadline cannot be negative.");
            }

            Seat = seat;
        }

        public int Seat { get; }

        public DecisionView Decision { get; }
    }

    /// <summary>
    /// The complete immutable input to one stream projection. It is deliberately per stream:
    /// a public stream passes a spectator cursor, while each player stream passes that seat's
    /// cursor. The projector never mutates or derives a cursor.
    /// </summary>
    public sealed class TableProjectionContext
    {
        private readonly ReadOnlyCollection<ClockView> _clocks;
        private readonly ReadOnlyCollection<TableSeatPublicFacts> _publicFacts;
        private readonly ReadOnlyCollection<TableSeatPrivateFacts> _privateFacts;
        private readonly ReadOnlyCollection<TableSeatDecision> _decisions;
        private readonly ReadOnlyCollection<TableEventProjectionInput> _events;

        public TableProjectionContext(
            TableProjectionIdentity identity,
            TableProjectionPolicy policy,
            IReadOnlyList<ClockView>? clocks = null,
            IReadOnlyList<TableSeatPublicFacts>? publicFacts = null,
            IReadOnlyList<TableSeatPrivateFacts>? privateFacts = null,
            IReadOnlyList<TableSeatDecision>? decisions = null,
            IReadOnlyList<TableEventProjectionInput>? events = null,
            TableSessionProjection? session = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _clocks = TableProjectionCopy.Items(clocks, nameof(clocks), 4);
            _publicFacts = TableProjectionCopy.Items(publicFacts, nameof(publicFacts), 4);
            _privateFacts = TableProjectionCopy.Items(privateFacts, nameof(privateFacts), 4);
            _decisions = TableProjectionCopy.Items(decisions, nameof(decisions), 4);
            _events = TableProjectionCopy.Items(events, nameof(events), 4096);
            for (int i = 0; i < _clocks.Count; i++)
            {
                if (_clocks[i] == null)
                {
                    throw new ArgumentException("Clocks cannot contain null.", nameof(clocks));
                }
            }

            for (int i = 0; i < _publicFacts.Count; i++)
            {
                if (_publicFacts[i] == null)
                {
                    throw new ArgumentException("Public facts cannot contain null.", nameof(publicFacts));
                }
            }

            for (int i = 0; i < _privateFacts.Count; i++)
            {
                if (_privateFacts[i] == null)
                {
                    throw new ArgumentException("Private facts cannot contain null.", nameof(privateFacts));
                }
            }

            for (int i = 0; i < _decisions.Count; i++)
            {
                if (_decisions[i] == null)
                {
                    throw new ArgumentException("Decisions cannot contain null.", nameof(decisions));
                }
            }

            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i] == null)
                {
                    throw new ArgumentException("Events cannot contain null.", nameof(events));
                }
            }

            Session = session;
        }

        public TableProjectionIdentity Identity { get; }

        public TableProjectionPolicy Policy { get; }

        public IReadOnlyList<ClockView> Clocks => _clocks;

        public IReadOnlyList<TableSeatPublicFacts> PublicFacts => _publicFacts;

        public IReadOnlyList<TableSeatPrivateFacts> PrivateFacts => _privateFacts;

        public IReadOnlyList<TableSeatDecision> Decisions => _decisions;

        public IReadOnlyList<TableEventProjectionInput> Events => _events;

        public TableSessionProjection? Session { get; }
    }

    internal static class TableProjectionCopy
    {
        internal static void Seat(int value, string name)
        {
            if (value < ProtocolConstants.MinSeat || value > ProtocolConstants.MaxSeat)
            {
                throw new ArgumentOutOfRangeException(name, value, "Seat must be 0..3.");
            }
        }

        internal static void SeatOrNoSeat(int value, string name)
        {
            if (value != ProtocolConstants.NoSeat)
            {
                Seat(value, name);
            }
        }

        internal static uint Tile(uint value, string name)
        {
            if (value > 135)
            {
                throw new ArgumentOutOfRangeException(name, value, "Tile id must be 0..135.");
            }

            return value;
        }

        internal static string OptionalId(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return ProtocolText.RequireId(value, nameof(value));
        }

        internal static ReadOnlyCollection<T> Items<T>(IReadOnlyList<T>? values, string name, int maxCount)
            where T : class
        {
            if (values == null)
            {
                return new ReadOnlyCollection<T>(new List<T>());
            }

            if (values.Count > maxCount)
            {
                throw new ArgumentException(name + " exceeds " + maxCount + " entries.", name);
            }

            var copy = new List<T>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                copy.Add(values[i]);
            }

            return new ReadOnlyCollection<T>(copy);
        }

        internal static ReadOnlyCollection<uint> Tiles(IReadOnlyList<uint>? values, string name, int maxCount)
        {
            if (values == null)
            {
                return new ReadOnlyCollection<uint>(new List<uint>());
            }

            if (values.Count > maxCount)
            {
                throw new ArgumentException(name + " exceeds " + maxCount + " entries.", name);
            }

            var copy = new List<uint>(values.Count);
            var seen = new HashSet<uint>();
            for (int i = 0; i < values.Count; i++)
            {
                uint tile = Tile(values[i], name);
                if (!seen.Add(tile))
                {
                    throw new ArgumentException(name + " contains duplicate tile ids.", name);
                }

                copy.Add(tile);
            }

            return new ReadOnlyCollection<uint>(copy);
        }

        internal static ReadOnlyCollection<long> Scores(IReadOnlyList<long>? values, string name)
        {
            if (values == null || values.Count == 0)
            {
                return new ReadOnlyCollection<long>(new List<long>());
            }

            if (values.Count != 4)
            {
                throw new ArgumentException(name + " must contain zero or four entries.", name);
            }

            var copy = new long[4];
            for (int i = 0; i < 4; i++)
            {
                copy[i] = values[i];
            }

            return new ReadOnlyCollection<long>(copy);
        }

        internal static ReadOnlyCollection<int> Rankings(IReadOnlyList<int>? values, string name)
        {
            if (values == null || values.Count == 0)
            {
                return new ReadOnlyCollection<int>(new List<int>());
            }

            if (values.Count != 4)
            {
                throw new ArgumentException(name + " must contain zero or four seats.", name);
            }

            var copy = new int[4];
            var seen = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                int seat = values[i];
                Seat(seat, name);
                if (seen[seat])
                {
                    throw new ArgumentException(name + " contains a duplicate seat.", name);
                }

                seen[seat] = true;
                copy[i] = seat;
            }

            return new ReadOnlyCollection<int>(copy);
        }
    }
}

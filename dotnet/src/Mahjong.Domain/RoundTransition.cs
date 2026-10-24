using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    /// <summary>
    /// Stable rejection codes. Every rejection is a pure value: the source round and every
    /// nested collection remain untouched because commands only build candidate state.
    /// </summary>
    public enum RoundTransitionErrorCode : byte
    {
        None = 0,
        RoundFinished = 1,
        WrongPhase = 2,
        WrongPlayer = 3,
        InvalidSeat = 4,
        InvalidTile = 5,
        StaleSourceVersion = 6,
        RoundIdMismatch = 7,
        NoResponseWindow = 8,
        SeatNotEligible = 9,
        DuplicateResponse = 10,
        TileTransitionRejected = 11,
        SettlementOutcomeMismatch = 12,
        SettlementConservationViolation = 13,
        WallNotExhausted = 14,
        InvalidEligibleSeats = 15,
        InvalidChankan = 16,
        NoDrawnTile = 17,
        InvalidRoundSpec = 18,
        MatchCommandRejected = 19,
        ForeignSourceState = 20,
        InvalidMeld = 21,
        MeldNotAllowed = 22,
        RiichiAlreadyDeclared = 23,
        RiichiNotMenzen = 24,
        RiichiInsufficientPoints = 25,
        RiichiNotTenpai = 26,
        RiichiWallTooShort = 27,
        RiichiDiscardLocked = 28,
        KanLimitReached = 29
    }

    public sealed class RoundTransitionError
    {
        public RoundTransitionError(RoundTransitionErrorCode code, string message)
        {
            if (code == RoundTransitionErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "A rejection needs a non-None code.");
            }

            Code = code;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public RoundTransitionErrorCode Code { get; }

        public string Message { get; }

        public override string ToString() => Code + ": " + Message;
    }

    public enum RoundEventType : byte
    {
        RoundStarted = 0,
        TilesDealt = 1,
        DoraRevealed = 2,
        TileDrawn = 3,
        RinshanDrawn = 4,
        TileDiscarded = 5,
        ResponseWindowOpened = 6,
        ChankanWindowOpened = 7,
        ResponseClaimed = 8,
        KanDeclared = 9,
        TsumoWin = 10,
        RonWin = 11,
        ExhaustiveDraw = 12,
        AbortiveDraw = 13,
        WallExhausted = 14,
        RoundEnded = 15,
        RiichiDeclared = 16,
        ChiDeclared = 17,
        PonDeclared = 18
    }

    /// <summary>A committed domain event. <see cref="Ordinal"/> is contiguous per round.</summary>
    public sealed class RoundEvent
    {
        public RoundEvent(
            RoundEventType type,
            int ordinal,
            int seat,
            TileId? tile,
            string? detail)
        {
            if (ordinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Ordinal must be non-negative.");
            }

            if (seat < -1 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be -1 or 0..3.");
            }

            Type = type;
            Ordinal = ordinal;
            Seat = seat;
            Tile = tile;
            Detail = detail;
        }

        public RoundEventType Type { get; }

        public int Ordinal { get; }

        public int Seat { get; }

        public TileId? Tile { get; }

        public string? Detail { get; }

        public override string ToString() => Ordinal + ":" + Type + "/seat" + Seat + (Tile.HasValue ? "/" + Tile.Value : string.Empty);
    }

    /// <summary>
    /// A candidate transition produced by <see cref="RoundStateMachine.Prepare"/> and applied
    /// only by <see cref="RoundStateMachine.Install"/>. It carries the exact source version
    /// and source-state fingerprint, so a candidate prepared from a different state with the
    /// same roundId/version cannot be installed here.
    /// </summary>
    public sealed class RoundChange
    {
        internal RoundChange(
            string roundId,
            long sourceVersion,
            ulong sourceFingerprint,
            RoundStateMachine nextState,
            IReadOnlyList<RoundEvent> addedEvents,
            RoundResult? result)
        {
            RoundId = roundId;
            SourceVersion = sourceVersion;
            SourceFingerprint = sourceFingerprint;
            NextState = nextState;
            AddedEvents = new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>(addedEvents));
            Result = result;
        }

        public string RoundId { get; }

        public long SourceVersion { get; }

        /// <summary>Stable fingerprint of the exact authoritative source state.</summary>
        public ulong SourceFingerprint { get; }

        public RoundStateMachine NextState { get; }

        public IReadOnlyList<RoundEvent> AddedEvents { get; }

        public RoundResult? Result { get; }
    }

    public sealed class RoundPreparation
    {
        private RoundPreparation(RoundChange? change, RoundTransitionError? error)
        {
            Change = change;
            Error = error;
        }

        public bool IsReady => Change != null;

        public RoundChange? Change { get; }

        public RoundTransitionError? Error { get; }

        internal static RoundPreparation Ready(RoundChange change) => new RoundPreparation(change, null);

        internal static RoundPreparation Rejected(RoundTransitionErrorCode code, string message) =>
            new RoundPreparation(null, new RoundTransitionError(code, message));
    }

    /// <summary>Result of applying a prepared round change or a command.</summary>
    public sealed class RoundTransition
    {
        private RoundTransition(
            bool isAccepted,
            RoundStateMachine? state,
            IReadOnlyList<RoundEvent> events,
            long? commandVersion,
            RoundResult? result,
            RoundTransitionError? error)
        {
            IsAccepted = isAccepted;
            State = state;
            Events = events;
            CommandVersion = commandVersion;
            Result = result;
            Error = error;
        }

        public bool IsAccepted { get; }

        public RoundStateMachine? State { get; }

        public IReadOnlyList<RoundEvent> Events { get; }

        /// <summary>New state version; null for a rejection (a rejection never consumes a version).</summary>
        public long? CommandVersion { get; }

        public RoundResult? Result { get; }

        public RoundTransitionError? Error { get; }

        internal static RoundTransition Accepted(RoundChange change)
        {
            return new RoundTransition(
                true,
                change.NextState,
                change.AddedEvents,
                change.NextState.Version,
                change.Result,
                null);
        }

        internal static RoundTransition Rejected(RoundTransitionErrorCode code, string message)
        {
            return new RoundTransition(
                false,
                null,
                new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>()),
                null,
                null,
                new RoundTransitionError(code, message));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain.Rules
{
    /// <summary>
    /// Stable reasons a settlement calculation refused to produce an authoritative command.
    /// The calculator never mutates the round; a rejection is therefore always a pure value.
    /// </summary>
    public enum RoundSettlementRejectionCode : byte
    {
        None = 0,
        RoundFinished = 1,
        WrongPhase = 2,
        WrongWindow = 3,
        WindowKindMismatch = 4,
        WinnerNotCurrent = 5,
        WinnerIsDiscarder = 6,
        WinnerNotEligible = 7,
        ResponseAlreadyRecorded = 8,
        NoDrawnTile = 9,
        DrawnTileMismatch = 10,
        HandCountMismatch = 11,
        DuplicatePhysicalTile = 12,
        HandShapeInvalid = 13,
        MeldGroupMismatch = 14,
        MeldSeatMismatch = 15,
        MeldTileMismatch = 16,
        RiichiStateInvalid = 17,
        WinFlagMismatch = 18,
        DoraIndicatorMismatch = 19,
        UraIndicatorUnavailable = 20,
        SeatFactMissing = 21,
        TenpaiEvidenceMismatch = 22,
        NoWinningHand = 23,
        NoYaku = 24,
        WallNotExhausted = 25,
        AbortiveReasonMissing = 26,
        AbortiveReasonMismatch = 27,
        AbortiveReasonUnexpected = 28,
        ArithmeticOverflow = 29,
        SettlementConservationViolation = 30,
        SourceRoundMismatch = 31,
        StaleSourceVersion = 32,
        ForeignSourceState = 33,
        InvalidSeat = 34,
        FuritenRon = 35,
        FuritenEvidenceMissing = 36,
        MeldTypeMismatch = 37
    }

    /// <summary>
    /// Explicit domain rejection raised before a command is built or executed. The message is
    /// diagnostic only; callers must branch on <see cref="Code"/>, not on localized text.
    /// </summary>
    public sealed class RoundSettlementRejectedException : Exception
    {
        public RoundSettlementRejectedException(RoundSettlementRejectionCode code, string message)
            : base(message)
        {
            if (code == RoundSettlementRejectionCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "A rejection needs a non-None code.");
            }

            Code = code;
        }

        public RoundSettlementRejectionCode Code { get; }
    }

    /// <summary>V4 abortive-draw reasons. Numeric values are not persisted.</summary>
    public enum SettlementAbortiveDrawReason : byte
    {
        FourWinds = 0,
        FourKans = 1,
        FourRiichi = 2,
        NineTerminals = 3
    }

    /// <summary>
    /// Trusted, host-verified hand history for one seat. Closed/dora/drawn/winning-window facts
    /// are derived from the immutable <see cref="RoundStateMachine"/>; only facts that the
    /// current tile map cannot represent are accepted here:
    /// meld kinds/call ownership, riichi/double-riichi/ippatsu, tenhou/chiihou, tenpai,
    /// the complete discard history, and furiten/first-turn state. Every supplied meld is
    /// re-bound to the exact Meld-zone slots and physical tiles. A null
    /// <see cref="DiscardHistory"/> means "not supplied"; an empty collection means "proved
    /// empty". Ron and chiihou/nine-terminals validation never treats missing history as empty.
    /// </summary>
    public sealed class RoundSettlementSeatFacts
    {
        private readonly ReadOnlyCollection<Meld> _melds;
        private readonly ReadOnlyCollection<TileId> _discardHistory;

        public RoundSettlementSeatFacts(
            int seat,
            IEnumerable<Meld>? melds = null,
            bool isRiichi = false,
            bool isDoubleRiichi = false,
            bool isIppatsu = false,
            bool isRinshan = false,
            bool isHaitei = false,
            bool isHoutei = false,
            bool isChankan = false,
            bool isTenhou = false,
            bool isChiihou = false,
            bool isTenpai = false,
            IReadOnlyList<TileId>? discardHistory = null,
            TileId? firstDiscard = null,
            bool isFirstTurnUninterrupted = false,
            bool isTemporaryFuriten = false,
            bool isRiichiFuriten = false,
            bool isIppatsuInterruptedByCallOrKan = false)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            var copy = new List<Meld>();
            if (melds != null)
            {
                foreach (Meld meld in melds)
                {
                    if (meld == null)
                    {
                        throw new ArgumentException("Meld list cannot contain null.", nameof(melds));
                    }

                    copy.Add(meld);
                }
            }

            if (copy.Count > 4)
            {
                throw new ArgumentException("A seat cannot have more than four melds.", nameof(melds));
            }

            if (discardHistory == null)
            {
                _discardHistory = new ReadOnlyCollection<TileId>(new List<TileId>());
                DiscardHistoryProvided = false;
            }
            else
            {
                _discardHistory = new ReadOnlyCollection<TileId>(new List<TileId>(discardHistory));
                DiscardHistoryProvided = true;
            }

            Seat = seat;
            _melds = new ReadOnlyCollection<Meld>(copy);
            IsRiichi = isRiichi;
            IsDoubleRiichi = isDoubleRiichi;
            IsIppatsu = isIppatsu;
            IsRinshan = isRinshan;
            IsHaitei = isHaitei;
            IsHoutei = isHoutei;
            IsChankan = isChankan;
            IsTenhou = isTenhou;
            IsChiihou = isChiihou;
            IsTenpai = isTenpai;
            FirstDiscard = firstDiscard;
            IsFirstTurnUninterrupted = isFirstTurnUninterrupted;
            IsTemporaryFuriten = isTemporaryFuriten;
            IsRiichiFuriten = isRiichiFuriten;
            IsIppatsuInterruptedByCallOrKan = isIppatsuInterruptedByCallOrKan;
        }

        public int Seat { get; }

        public IReadOnlyList<Meld> Melds => _melds;

        public bool IsRiichi { get; }

        public bool IsDoubleRiichi { get; }

        public bool IsIppatsu { get; }

        public bool IsRinshan { get; }

        public bool IsHaitei { get; }

        public bool IsHoutei { get; }

        public bool IsChankan { get; }

        public bool IsTenhou { get; }

        public bool IsChiihou { get; }

        /// <summary>Only used by an exhaustive draw; ignored for win calculations.</summary>
        public bool IsTenpai { get; }

        /// <summary>False when the host did not supply the complete discard history.</summary>
        public bool DiscardHistoryProvided { get; }

        /// <summary>Complete own discard history, including tiles later called away.</summary>
        public IReadOnlyList<TileId> DiscardHistory => _discardHistory;

        /// <summary>First discard of the first uninterrupted go, for four-winds abort.</summary>
        public TileId? FirstDiscard { get; }

        /// <summary>Trusted: no call/kan interrupted the first go before this fact was recorded.</summary>
        public bool IsFirstTurnUninterrupted { get; }

        public bool IsTemporaryFuriten { get; }

        public bool IsRiichiFuriten { get; }

        /// <summary>
        /// Trusted timing fact: a call/kan occurred after the current riichi declaration and
        /// before this win. Old melds/kans that predate the riichi must not set this flag.
        /// </summary>
        public bool IsIppatsuInterruptedByCallOrKan { get; }
    }

    /// <summary>
    /// Caller-supplied record identity/time and the trusted per-seat facts. The calculator never
    /// reads a clock, creates an id or accepts claimed final scores, han, fu or payments.
    /// </summary>
    public sealed class RoundSettlementFacts
    {
        private readonly ReadOnlyCollection<RoundSettlementSeatFacts> _seats;

        public RoundSettlementFacts(
            string resultId,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis,
            IEnumerable<RoundSettlementSeatFacts> seats,
            SettlementAbortiveDrawReason? abortiveDrawReason = null)
        {
            if (string.IsNullOrWhiteSpace(resultId))
            {
                throw new ArgumentException("resultId cannot be blank.", nameof(resultId));
            }

            if (completedAtUtc == null)
            {
                throw new ArgumentNullException(nameof(completedAtUtc));
            }

            if (completedAtMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedAtMonotonicMillis),
                    completedAtMonotonicMillis,
                    "Monotonic time must be non-negative.");
            }

            if (seats == null)
            {
                throw new ArgumentNullException(nameof(seats));
            }

            var copy = new List<RoundSettlementSeatFacts>();
            var seen = new bool[4];
            foreach (RoundSettlementSeatFacts seat in seats)
            {
                if (seat == null)
                {
                    throw new ArgumentException("Seat facts cannot contain null.", nameof(seats));
                }

                if (seen[seat.Seat])
                {
                    throw new ArgumentException("Seat " + seat.Seat + " is recorded twice.", nameof(seats));
                }

                seen[seat.Seat] = true;
                copy.Add(seat);
            }

            if (copy.Count == 0)
            {
                throw new ArgumentException("At least one seat record is required.", nameof(seats));
            }

            copy.Sort((left, right) => left.Seat.CompareTo(right.Seat));
            ResultId = resultId;
            CompletedAtUtc = completedAtUtc;
            CompletedAtMonotonicMillis = completedAtMonotonicMillis;
            _seats = new ReadOnlyCollection<RoundSettlementSeatFacts>(copy);
            AbortiveDrawReason = abortiveDrawReason;
        }

        public string ResultId { get; }

        public PersistedUtcTime CompletedAtUtc { get; }

        public long CompletedAtMonotonicMillis { get; }

        public IReadOnlyList<RoundSettlementSeatFacts> Seats => _seats;

        public SettlementAbortiveDrawReason? AbortiveDrawReason { get; }

        public RoundSettlementSeatFacts? FindSeat(int seat)
        {
            for (int i = 0; i < _seats.Count; i++)
            {
                if (_seats[i].Seat == seat)
                {
                    return _seats[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Immutable result of one calculation. It carries the exact source round identity and
    /// fingerprint, the scoring breakdown, the V4 payment result, the executable current
    /// <see cref="RoundCommand"/> and the already-built <see cref="RoundSettlement"/>.
    ///
    /// The command itself still goes through <see cref="RoundStateMachine.Prepare"/> and
    /// <see cref="RoundStateMachine.Install"/> (via <see cref="Execute"/>), so the round's
    /// conservation/outcome second gate is never bypassed. <see cref="Execute"/> rejects a
    /// stale version/fingerprint before calling the state machine; a caller that extracts the
    /// raw command loses that pre-check because the current RoundCommand cannot carry a source
    /// fingerprint.
    /// </summary>
    public sealed class RoundSettlementCalculation
    {
        internal RoundSettlementCalculation(
            string sourceRoundId,
            long sourceVersion,
            ulong sourceFingerprint,
            RoundActionKind action,
            RoundSettlement settlement,
            RoundCommand command,
            ScoreBreakdown? breakdown,
            SettlementResult payments,
            RoundOutcome outcome,
            WinContext? winContext,
            int winnerSeat,
            int loserSeat,
            bool isDealer,
            int seatWind)
        {
            SourceRoundId = sourceRoundId;
            SourceVersion = sourceVersion;
            SourceFingerprint = sourceFingerprint;
            Action = action;
            Settlement = settlement;
            Command = command;
            Breakdown = breakdown;
            Payments = payments;
            Outcome = outcome;
            WinContext = winContext;
            WinnerSeat = winnerSeat;
            LoserSeat = loserSeat;
            IsDealer = isDealer;
            SeatWind = seatWind;
        }

        public string SourceRoundId { get; }

        public long SourceVersion { get; }

        public ulong SourceFingerprint { get; }

        public RoundActionKind Action { get; }

        public RoundSettlement Settlement { get; }

        public RoundCommand Command { get; }

        /// <summary>Null for exhaustive/abortive draws.</summary>
        public ScoreBreakdown? Breakdown { get; }

        public SettlementResult Payments { get; }

        public RoundOutcome Outcome { get; }

        /// <summary>Null for draws; the exact context sent to the frozen evaluator for wins.</summary>
        public WinContext? WinContext { get; }

        public int WinnerSeat { get; }

        public int LoserSeat { get; }

        public bool IsDealer { get; }

        public int SeatWind { get; }

        public string ResultId => Settlement.ResultId;

        /// <summary>
        /// Verifies that <paramref name="round"/> is still the exact authoritative source this
        /// calculation was derived from. No state is touched on failure.
        /// </summary>
        public void VerifySource(RoundStateMachine round)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (!string.Equals(round.RoundId, SourceRoundId, StringComparison.Ordinal))
            {
                throw new RoundSettlementRejectedException(
                    RoundSettlementRejectionCode.SourceRoundMismatch,
                    "Calculation belongs to round '" + SourceRoundId + "', not '" + round.RoundId + "'.");
            }

            if (round.Version != SourceVersion)
            {
                throw new RoundSettlementRejectedException(
                    RoundSettlementRejectionCode.StaleSourceVersion,
                    "Calculation source version " + SourceVersion + " is stale; current version is " +
                    round.Version + ".");
            }

            if (round.SourceFingerprint != SourceFingerprint)
            {
                throw new RoundSettlementRejectedException(
                    RoundSettlementRejectionCode.ForeignSourceState,
                    "Calculation was derived from a different authoritative state with the same roundId/version.");
            }
        }

        /// <summary>Re-verifies the source binding, then applies the command through Prepare/Install.</summary>
        public RoundTransition Execute(RoundStateMachine round)
        {
            VerifySource(round);
            return round.Execute(Command);
        }
    }
}

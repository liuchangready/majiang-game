using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    /// <summary>
    /// V4 <c>GamePhase</c> without the view-only <c>MATCH_END</c> overlay. A round never
    /// enters a match phase; <c>MatchSession</c> owns the match lifecycle.
    /// Numeric values are stable for persistence and must not be reordered.
    /// </summary>
    public enum RoundPhase : byte
    {
        Stacking = 0,
        Dealing = 1,
        Draw = 2,
        CheatWindow = 3,
        Discard = 4,
        ResponseWindow = 5,
        ChankanWindow = 6,
        KanDraw = 7,
        RoundEnd = 8
    }

    /// <summary>
    /// Approved round actions implemented by P1.3. V4's chi/pon/riichi/open-kan and the
    /// cheat actions are deliberately absent: they belong to the rules/cheat layers and
    /// must not be approximated here. Unsupported action types are rejected by the type
    /// system rather than silently accepted.
    /// </summary>
    public enum RoundActionKind : byte
    {
        StartStacking = 0,
        DrawTile = 1,
        DiscardTile = 2,
        DeclareTsumo = 3,
        DeclareRon = 4,
        Pass = 5,
        DeclareAddedKan = 6,
        DeclareExhaustiveDraw = 7,
        DeclareAbortiveDraw = 8
    }

    public enum RoundEndReason : byte
    {
        None = 0,
        Tsumo = 1,
        Ron = 2,
        ChankanRon = 3,
        ExhaustiveDraw = 4,
        AbortiveDraw = 5
    }

    /// <summary>
    /// UTC instant supplied by the caller for persisted records. Domain never reads a
    /// wall clock and this type refuses non-zero offsets so a persisted value cannot be
    /// mistaken for a monotonic runtime value.
    /// </summary>
    public sealed class PersistedUtcTime
    {
        public PersistedUtcTime(DateTimeOffset value)
        {
            if (value.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Persisted UTC time must have a zero offset.", nameof(value));
            }

            Value = value;
        }

        public DateTimeOffset Value { get; }

        public override string ToString() => Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Input to a single round. IDs, scores, sticks and tiles are all caller supplied.</summary>
    public sealed class RoundSpec
    {
        public RoundSpec(
            string roundId,
            int dealerSeat,
            int roundWind,
            int roundNumber,
            int honbaSticks,
            IReadOnlyList<long> scores,
            int riichiSticks)
        {
            RoundId = string.IsNullOrWhiteSpace(roundId)
                ? throw new ArgumentException("roundId cannot be blank.", nameof(roundId))
                : roundId;
            if (dealerSeat < 0 || dealerSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(dealerSeat), dealerSeat, "Dealer seat must be 0..3.");
            }

            if (roundWind < 1 || roundWind > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(roundWind), roundWind, "Round wind must be 1..3.");
            }

            if (roundNumber < 0 || roundNumber > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(roundNumber), roundNumber, "Round number must be 0..3.");
            }

            if (honbaSticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(honbaSticks), honbaSticks, "Honba must be non-negative.");
            }

            if (riichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(riichiSticks), riichiSticks, "Riichi sticks must be non-negative.");
            }

            Scores = V5Guard.CopyScores(scores, nameof(scores));
            DealerSeat = dealerSeat;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            HonbaSticks = honbaSticks;
            RiichiSticks = riichiSticks;
        }

        public string RoundId { get; }

        public int DealerSeat { get; }

        public int RoundWind { get; }

        public int RoundNumber { get; }

        public int HonbaSticks { get; }

        public IReadOnlyList<long> Scores { get; }

        public int RiichiSticks { get; }
    }

    /// <summary>
    /// Settlement computed by the scoring layer (P1.2) and applied atomically by the round
    /// lifecycle. P1.3 does not evaluate yaku/fu and must never invent payment values.
    /// Conservation is re-checked here before the round becomes terminal.
    /// </summary>
    public sealed class RoundSettlement
    {
        public RoundSettlement(
            string resultId,
            RoundOutcome outcome,
            IReadOnlyList<long> finalScores,
            int remainingRiichiSticks,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis)
        {
            ResultId = string.IsNullOrWhiteSpace(resultId)
                ? throw new ArgumentException("resultId cannot be blank.", nameof(resultId))
                : resultId;
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            if (outcome.Kind == RoundOutcomeKind.None)
            {
                throw new ArgumentException("A settlement requires a concrete outcome.", nameof(outcome));
            }

            FinalScores = V5Guard.CopyScores(finalScores, nameof(finalScores));
            if (remainingRiichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingRiichiSticks),
                    remainingRiichiSticks,
                    "Remaining riichi sticks must be non-negative.");
            }

            if (completedAtMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedAtMonotonicMillis),
                    completedAtMonotonicMillis,
                    "Monotonic time must be non-negative.");
            }

            RemainingRiichiSticks = remainingRiichiSticks;
            CompletedAtUtc = completedAtUtc ?? throw new ArgumentNullException(nameof(completedAtUtc));
            CompletedAtMonotonicMillis = completedAtMonotonicMillis;
        }

        public string ResultId { get; }

        public RoundOutcome Outcome { get; }

        public IReadOnlyList<long> FinalScores { get; }

        public int RemainingRiichiSticks { get; }

        public PersistedUtcTime CompletedAtUtc { get; }

        public long CompletedAtMonotonicMillis { get; }
    }

    /// <summary>Immutable result of a finished round. Events are the full round history.</summary>
    public sealed class RoundResult
    {
        internal RoundResult(
            string resultId,
            string roundId,
            RoundOutcome outcome,
            IReadOnlyList<long> startingScores,
            IReadOnlyList<long> finalScores,
            IReadOnlyList<long> totalDeltas,
            int startingRiichiSticks,
            int remainingRiichiSticks,
            IReadOnlyList<RoundEvent> events,
            RoundEndReason endReason,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis)
        {
            ResultId = resultId;
            RoundId = roundId;
            Outcome = outcome;
            StartingScores = startingScores;
            FinalScores = finalScores;
            TotalDeltas = totalDeltas;
            StartingRiichiSticks = startingRiichiSticks;
            RemainingRiichiSticks = remainingRiichiSticks;
            Events = events;
            EndReason = endReason;
            CompletedAtUtc = completedAtUtc;
            CompletedAtMonotonicMillis = completedAtMonotonicMillis;
        }

        public string ResultId { get; }

        public string RoundId { get; }

        public RoundOutcome Outcome { get; }

        public IReadOnlyList<long> StartingScores { get; }

        public IReadOnlyList<long> FinalScores { get; }

        public IReadOnlyList<long> TotalDeltas { get; }

        public int StartingRiichiSticks { get; }

        public int RemainingRiichiSticks { get; }

        public IReadOnlyList<RoundEvent> Events { get; }

        public RoundEndReason EndReason { get; }

        public PersistedUtcTime CompletedAtUtc { get; }

        public long CompletedAtMonotonicMillis { get; }
    }

    /// <summary>Immutable response window for a discard call or for a chankan opportunity.</summary>
    public sealed class RoundResponseWindow
    {
        private readonly ReadOnlyCollection<int> _eligibleSeats;
        private readonly ReadOnlyCollection<RoundResponseClaim> _claims;

        internal RoundResponseWindow(
            int fromSeat,
            TileId targetTile,
            bool isChankan,
            IReadOnlyList<int> eligibleSeats,
            IReadOnlyList<RoundResponseClaim> claims)
        {
            FromSeat = fromSeat;
            TargetTile = targetTile;
            IsChankan = isChankan;
            _eligibleSeats = new ReadOnlyCollection<int>(new List<int>(eligibleSeats));
            _claims = new ReadOnlyCollection<RoundResponseClaim>(new List<RoundResponseClaim>(claims));
        }

        public int FromSeat { get; }

        public TileId TargetTile { get; }

        public bool IsChankan { get; }

        public IReadOnlyList<int> EligibleSeats => _eligibleSeats;

        public IReadOnlyList<RoundResponseClaim> Claims => _claims;

        public bool HasResponded(int seat)
        {
            for (int i = 0; i < _claims.Count; i++)
            {
                if (_claims[i].Seat == seat)
                {
                    return true;
                }
            }

            return false;
        }

        public RoundResponseClaim? FindClaim(int seat)
        {
            for (int i = 0; i < _claims.Count; i++)
            {
                if (_claims[i].Seat == seat)
                {
                    return _claims[i];
                }
            }

            return null;
        }

        public bool IsComplete => _claims.Count == _eligibleSeats.Count;

        internal RoundResponseWindow WithClaim(RoundResponseClaim claim)
        {
            if (HasResponded(claim.Seat))
            {
                throw new InvalidOperationException("Seat " + claim.Seat + " already responded.");
            }

            var next = new List<RoundResponseClaim>(_claims) { claim };
            next.Sort((left, right) => left.Seat.CompareTo(right.Seat));
            return new RoundResponseWindow(FromSeat, TargetTile, IsChankan, _eligibleSeats, next);
        }
    }

    /// <summary>A recorded Pass or Ron claim. A Ron claim carries its scoring-layer settlement.</summary>
    public sealed class RoundResponseClaim
    {
        private RoundResponseClaim(int seat, bool isRon, RoundSettlement? settlement)
        {
            Seat = seat;
            IsRon = isRon;
            Settlement = settlement;
        }

        public int Seat { get; }

        public bool IsRon { get; }

        public RoundSettlement? Settlement { get; }

        public static RoundResponseClaim Pass(int seat)
        {
            V5Guard.CheckSeat(seat);
            return new RoundResponseClaim(seat, false, null);
        }

        public static RoundResponseClaim Ron(int seat, RoundSettlement settlement)
        {
            V5Guard.CheckSeat(seat);
            return new RoundResponseClaim(
                seat,
                true,
                settlement ?? throw new ArgumentNullException(nameof(settlement)));
        }
    }

    internal static class V5Guard
    {
        internal static void CheckSeat(int seat, string name = "seat")
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(name, seat, "Seat must be 0..3.");
            }
        }

        internal static IReadOnlyList<long> CopyScores(IReadOnlyList<long> scores, string name)
        {
            if (scores == null)
            {
                throw new ArgumentNullException(name);
            }

            if (scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four entries.", name);
            }

            var copy = new long[4];
            for (int i = 0; i < 4; i++)
            {
                copy[i] = scores[i];
            }

            return new ReadOnlyCollection<long>(copy);
        }

        internal static IReadOnlyList<int> CopySeats(IReadOnlyList<int> seats, string name)
        {
            if (seats == null)
            {
                throw new ArgumentNullException(name);
            }

            var copy = new List<int>(seats.Count);
            var seen = new bool[4];
            for (int i = 0; i < seats.Count; i++)
            {
                int seat = seats[i];
                CheckSeat(seat, name);
                if (seen[seat])
                {
                    throw new ArgumentException("Seat " + seat + " is duplicated.", name);
                }

                seen[seat] = true;
                copy.Add(seat);
            }

            copy.Sort();
            return new ReadOnlyCollection<int>(copy);
        }

        internal static long Sum(long[] values)
        {
            long sum = 0;
            for (int i = 0; i < values.Length; i++)
            {
                sum = checked(sum + values[i]);
            }

            return sum;
        }
    }
}

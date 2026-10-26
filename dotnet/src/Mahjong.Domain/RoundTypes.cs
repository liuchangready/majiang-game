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
    /// Approved round actions. Values 0..8 are the original P1.3 surface and are frozen for
    /// persistence. N-1a appends the player decision kinds that V4 exposes (chi, pon, open-kan
    /// claim, closed kan and riichi); the cheat actions remain outside this enum. Values must
    /// never be reordered or reused.
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
        DeclareAbortiveDraw = 8,
        DeclareChi = 9,
        DeclarePon = 10,
        DeclareOpenKan = 11,
        DeclareClosedKan = 12,
        DeclareRiichi = 13
    }

    /// <summary>
    /// V4 <c>MeldType</c> with stable persisted numbers. <see cref="RoundMeld.IsOpen"/> is the
    /// menzen test: only a closed kan leaves the hand concealed.
    /// </summary>
    public enum RoundMeldKind : byte
    {
        Chi = 1,
        Pon = 2,
        OpenKan = 3,
        ClosedKan = 4,
        AddedKan = 5
    }

    /// <summary>
    /// Immutable authoritative meld record. The physical tile map proves where each tile is;
    /// this record preserves the V4 meld type, caller and called tile that the map alone cannot
    /// distinguish (a three-tile group can be either chi or pon).
    /// </summary>
    public sealed class RoundMeld
    {
        private readonly ReadOnlyCollection<TileId> _tiles;

        public RoundMeld(
            RoundMeldKind kind,
            int seat,
            int fromSeat,
            TileId calledTile,
            IReadOnlyList<TileId> tiles)
        {
            if (!Enum.IsDefined(typeof(RoundMeldKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown meld kind.");
            }

            V5Guard.CheckSeat(seat);
            if (fromSeat < -1 || fromSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(fromSeat), fromSeat, "fromSeat must be -1 or 0..3.");
            }

            if (tiles == null)
            {
                throw new ArgumentNullException(nameof(tiles));
            }

            bool isKan = kind == RoundMeldKind.OpenKan ||
                         kind == RoundMeldKind.ClosedKan ||
                         kind == RoundMeldKind.AddedKan;
            int expected = isKan ? 4 : 3;
            if (tiles.Count != expected)
            {
                throw new ArgumentException(
                    kind + " requires exactly " + expected + " physical tiles.", nameof(tiles));
            }

            var copy = new TileId[expected];
            var seen = new HashSet<int>();
            for (int i = 0; i < expected; i++)
            {
                copy[i] = tiles[i];
                if (!seen.Add(tiles[i].Value))
                {
                    throw new ArgumentException("A meld cannot contain a duplicate physical tile.", nameof(tiles));
                }
            }

            if (kind == RoundMeldKind.Chi)
            {
                var ordered = new List<TileId>(copy);
                ordered.Sort();
                if (!ordered[0].IsNumberTile ||
                    ordered[0].Suit != ordered[1].Suit ||
                    ordered[1].Suit != ordered[2].Suit ||
                    ordered[1].Rank != ordered[0].Rank + 1 ||
                    ordered[2].Rank != ordered[1].Rank + 1)
                {
                    throw new ArgumentException("Chi tiles must form one consecutive number sequence.", nameof(tiles));
                }

                copy = ordered.ToArray();
            }
            else
            {
                for (int i = 1; i < expected; i++)
                {
                    if (!copy[i].SameType(copy[0]))
                    {
                        throw new ArgumentException("Pon/kan tiles must share one type.", nameof(tiles));
                    }
                }
            }

            bool containsCalled = false;
            for (int i = 0; i < copy.Length; i++)
            {
                if (copy[i] == calledTile)
                {
                    containsCalled = true;
                    break;
                }
            }

            if (!containsCalled)
            {
                throw new ArgumentException("The called tile must be part of the meld.", nameof(calledTile));
            }

            if (kind != RoundMeldKind.ClosedKan && fromSeat == seat)
            {
                throw new ArgumentException("An open meld cannot be called from the same seat.", nameof(fromSeat));
            }

            Kind = kind;
            Seat = seat;
            FromSeat = fromSeat;
            CalledTile = calledTile;
            _tiles = new ReadOnlyCollection<TileId>(copy);
        }

        public RoundMeldKind Kind { get; }

        public int Seat { get; }

        public int FromSeat { get; }

        public TileId CalledTile { get; }

        public IReadOnlyList<TileId> Tiles => _tiles;

        public bool IsOpen => Kind != RoundMeldKind.ClosedKan;

        public bool IsKan =>
            Kind == RoundMeldKind.OpenKan ||
            Kind == RoundMeldKind.ClosedKan ||
            Kind == RoundMeldKind.AddedKan;
    }

    /// <summary>Stable response kinds. Values are persisted in recovery payloads.</summary>
    public enum RoundClaimKind : byte
    {
        Pass = 0,
        Ron = 1,
        Chi = 2,
        Pon = 3,
        OpenKan = 4
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

    /// <summary>
    /// A recorded window response. Ron carries the scoring-layer settlement; chi/pon/open-kan
    /// carry the exact caller-owned physical tiles chosen for the meld. Pass carries neither.
    /// </summary>
    public sealed class RoundResponseClaim
    {
        private readonly ReadOnlyCollection<TileId> _meldTiles;

        private RoundResponseClaim(
            int seat,
            RoundClaimKind kind,
            RoundSettlement? settlement,
            IReadOnlyList<TileId>? meldTiles)
        {
            V5Guard.CheckSeat(seat);
            if (!Enum.IsDefined(typeof(RoundClaimKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown claim kind.");
            }

            if (kind == RoundClaimKind.Ron && settlement == null)
            {
                throw new ArgumentNullException(nameof(settlement), "A Ron claim requires a settlement.");
            }

            if (kind != RoundClaimKind.Ron && settlement != null)
            {
                throw new ArgumentException("Only a Ron claim can carry a settlement.", nameof(settlement));
            }

            int expected = kind == RoundClaimKind.Chi || kind == RoundClaimKind.Pon
                ? 2
                : kind == RoundClaimKind.OpenKan ? 3 : 0;
            if (expected == 0)
            {
                if (meldTiles != null && meldTiles.Count != 0)
                {
                    throw new ArgumentException("Pass/Ron cannot carry meld tiles.", nameof(meldTiles));
                }

                _meldTiles = new ReadOnlyCollection<TileId>(Array.Empty<TileId>());
            }
            else
            {
                if (meldTiles == null || meldTiles.Count != expected)
                {
                    throw new ArgumentException(kind + " requires exactly " + expected + " hand tiles.", nameof(meldTiles));
                }

                var copy = new TileId[expected];
                var seen = new HashSet<int>();
                for (int i = 0; i < expected; i++)
                {
                    copy[i] = meldTiles[i];
                    if (!seen.Add(copy[i].Value))
                    {
                        throw new ArgumentException("A claim cannot contain a duplicate physical tile.", nameof(meldTiles));
                    }
                }

                _meldTiles = new ReadOnlyCollection<TileId>(copy);
            }

            Seat = seat;
            Kind = kind;
            Settlement = settlement;
        }

        public int Seat { get; }

        public RoundClaimKind Kind { get; }

        public bool IsRon => Kind == RoundClaimKind.Ron;

        public RoundSettlement? Settlement { get; }

        public IReadOnlyList<TileId> MeldTiles => _meldTiles;

        public static RoundResponseClaim Pass(int seat) =>
            new RoundResponseClaim(seat, RoundClaimKind.Pass, null, null);

        public static RoundResponseClaim Ron(int seat, RoundSettlement settlement) =>
            new RoundResponseClaim(
                seat,
                RoundClaimKind.Ron,
                settlement ?? throw new ArgumentNullException(nameof(settlement)),
                null);

        public static RoundResponseClaim Chi(int seat, TileId handTileA, TileId handTileB) =>
            new RoundResponseClaim(seat, RoundClaimKind.Chi, null, new[] { handTileA, handTileB });

        public static RoundResponseClaim Pon(int seat, TileId handTileA, TileId handTileB) =>
            new RoundResponseClaim(seat, RoundClaimKind.Pon, null, new[] { handTileA, handTileB });

        public static RoundResponseClaim OpenKan(int seat, TileId handTileA, TileId handTileB, TileId handTileC) =>
            new RoundResponseClaim(seat, RoundClaimKind.OpenKan, null, new[] { handTileA, handTileB, handTileC });
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

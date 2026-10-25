using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain.Cheat
{
    /// <summary>Kind of draw the current trick rides on (V4 <c>SleeveBloomDrawKind</c>).</summary>
    public enum SleeveBloomDrawKind : byte
    {
        DealExtra = 0,
        Normal = 1,
        Rinshan = 2
    }

    /// <summary>Immediate (own turn) versus off-turn (issued after own discard) offers.</summary>
    public enum SleeveBloomOfferMode : byte
    {
        Immediate = 0,
        OffTurn = 1
    }

    /// <summary>The two frozen candidates of a pending sleeve bloom offer.</summary>
    public enum SleeveBloomKeep : byte
    {
        First = 0,
        Second = 1
    }

    /// <summary>Caller-supplied sleeve bloom configuration (V4 <c>SleeveBloomConfig</c>).</summary>
    public sealed class SleeveBloomConfig
    {
        public SleeveBloomConfig(bool enabled, long offTurnChoiceMillis)
        {
            if (offTurnChoiceMillis <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offTurnChoiceMillis), offTurnChoiceMillis, "Off-turn choice window must be positive.");
            }

            Enabled = enabled;
            OffTurnChoiceMillis = offTurnChoiceMillis;
        }

        public bool Enabled { get; }

        public long OffTurnChoiceMillis { get; }
    }

    /// <summary>
    /// One frozen sleeve bloom offer (V4 <c>SleeveBloomOffer</c>). Immediate offers ride on
    /// the current draw; off-turn offers are issued after the owner's discard, reference a
    /// window id and carry a caller-supplied deadline. No preview flags are modelled here:
    /// candidate previews belong to the P3.2 projection layer.
    /// </summary>
    public sealed class SleeveBloomOffer : IEquatable<SleeveBloomOffer>
    {
        public SleeveBloomOffer(
            string offerId,
            int seat,
            SleeveBloomDrawKind drawKind,
            TileId firstTile,
            TileId secondTile,
            int createdTurnNumber,
            SleeveBloomOfferMode mode,
            string? windowId = null,
            long deadlineMillis = 0,
            long createdAtMillis = 0)
        {
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new ArgumentException("Offer id cannot be blank.", nameof(offerId));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (firstTile.Equals(secondTile))
            {
                throw new ArgumentException("Sleeve bloom candidates must be distinct tiles.", nameof(secondTile));
            }

            if (createdTurnNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(createdTurnNumber), createdTurnNumber, "Created turn number must be non-negative.");
            }

            if (mode == SleeveBloomOfferMode.Immediate && drawKind == SleeveBloomDrawKind.DealExtra)
            {
                throw new ArgumentException("Immediate offers cannot ride on the deal-extra draw.", nameof(drawKind));
            }

            if (mode == SleeveBloomOfferMode.OffTurn)
            {
                if (drawKind != SleeveBloomDrawKind.Normal)
                {
                    throw new ArgumentException("Off-turn offers must ride on a normal draw.", nameof(drawKind));
                }

                if (string.IsNullOrWhiteSpace(windowId))
                {
                    throw new ArgumentException("Off-turn offers require a window id.", nameof(windowId));
                }

                if (deadlineMillis < 0 || createdAtMillis < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(deadlineMillis), deadlineMillis, "Off-turn timestamps must be non-negative.");
                }
            }

            OfferId = offerId;
            Seat = seat;
            DrawKind = drawKind;
            FirstTile = firstTile;
            SecondTile = secondTile;
            CreatedTurnNumber = createdTurnNumber;
            Mode = mode;
            WindowId = windowId;
            DeadlineMillis = deadlineMillis;
            CreatedAtMillis = createdAtMillis;
        }

        public string OfferId { get; }

        public int Seat { get; }

        public SleeveBloomDrawKind DrawKind { get; }

        public TileId FirstTile { get; }

        public TileId SecondTile { get; }

        public int CreatedTurnNumber { get; }

        public SleeveBloomOfferMode Mode { get; }

        public string? WindowId { get; }

        /// <summary>Caller-supplied absolute deadline; zero for immediate offers.</summary>
        public long DeadlineMillis { get; }

        public long CreatedAtMillis { get; }

        public bool Equals(SleeveBloomOffer? other) =>
            other != null &&
            OfferId == other.OfferId &&
            Seat == other.Seat &&
            DrawKind == other.DrawKind &&
            FirstTile.Equals(other.FirstTile) &&
            SecondTile.Equals(other.SecondTile) &&
            CreatedTurnNumber == other.CreatedTurnNumber &&
            Mode == other.Mode &&
            WindowId == other.WindowId &&
            DeadlineMillis == other.DeadlineMillis &&
            CreatedAtMillis == other.CreatedAtMillis;

        public override bool Equals(object? obj) => obj is SleeveBloomOffer other && Equals(other);

        public override int GetHashCode() =>
            (((OfferId.GetHashCode(StringComparison.Ordinal) * 31 + Seat) * 31 +
              FirstTile.Value) * 31 + SecondTile.Value) * 31 + CreatedTurnNumber;

        public override string ToString() =>
            OfferId + "[seat" + Seat + " " + Mode + " " + DrawKind + " first=" + FirstTile + " second=" + SecondTile + "]";
    }

    /// <summary>A locked live-wall tile awaiting delivery on the owner's next normal draw.</summary>
    public sealed class SleeveBloomReservation : IEquatable<SleeveBloomReservation>
    {
        public SleeveBloomReservation(int seat, string offerId, TileId tile, string? windowId, long createdAtMillis)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new ArgumentException("Offer id cannot be blank.", nameof(offerId));
            }

            if (createdAtMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(createdAtMillis), createdAtMillis, "Created-at must be non-negative.");
            }

            Seat = seat;
            OfferId = offerId;
            Tile = tile;
            WindowId = windowId;
            CreatedAtMillis = createdAtMillis;
        }

        public int Seat { get; }

        public string OfferId { get; }

        public TileId Tile { get; }

        public string? WindowId { get; }

        public long CreatedAtMillis { get; }

        public bool Equals(SleeveBloomReservation? other) =>
            other != null &&
            Seat == other.Seat &&
            OfferId == other.OfferId &&
            Tile.Equals(other.Tile) &&
            WindowId == other.WindowId &&
            CreatedAtMillis == other.CreatedAtMillis;

        public override bool Equals(object? obj) => obj is SleeveBloomReservation other && Equals(other);

        public override int GetHashCode() => (OfferId.GetHashCode(StringComparison.Ordinal) * 31 + Seat) * 31 + Tile.Value;

        public override string ToString() => OfferId + "[seat" + Seat + " tile=" + Tile + "]";
    }

    /// <summary>
    /// Immutable sleeve bloom state (V4 <c>CheatState.sleeveBloom</c>): the at-most-one
    /// immediate offer, per-seat off-turn offers and reservations, per-seat window ids and
    /// the per-turn usage marker. Protection (locked wall tiles) covers off-turn offer
    /// candidates and reservations only — immediate offers never lock tiles.
    /// </summary>
    public sealed class SleeveBloomState
    {
        private readonly SleeveBloomOffer[] _offTurnOffers;
        private readonly SleeveBloomReservation[] _reservations;
        private readonly string?[] _windowIds;

        private SleeveBloomState(
            SleeveBloomOffer? immediateOffer,
            SleeveBloomOffer[] offTurnOffers,
            SleeveBloomReservation[] reservations,
            string?[] windowIds,
            int windowSeq,
            int usedOnTurn,
            long version)
        {
            ImmediateOffer = immediateOffer;
            _offTurnOffers = offTurnOffers;
            _reservations = reservations;
            _windowIds = windowIds;
            WindowSeq = windowSeq;
            UsedOnTurn = usedOnTurn;
            Version = version;
        }

        public SleeveBloomState()
            : this(null, Array.Empty<SleeveBloomOffer>(), Array.Empty<SleeveBloomReservation>(), new string?[4], 0, -1, 0)
        {
        }

        /// <summary>
        /// Rehydrates an already validated checkpoint aggregate. All per-seat uniqueness,
        /// window and mode invariants are rechecked; a malformed payload throws instead of
        /// silently discarding offers, reservations or candidate windows.
        /// </summary>
        public static SleeveBloomState Restore(
            SleeveBloomOffer? immediateOffer,
            IReadOnlyList<SleeveBloomOffer> offTurnOffers,
            IReadOnlyList<SleeveBloomReservation> reservations,
            IReadOnlyList<string?> windowIds,
            int windowSeq,
            int usedOnTurn,
            long version)
        {
            if (version < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, "Sleeve bloom version must be non-negative.");
            }

            if (windowSeq < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSeq), windowSeq, "Window sequence must be non-negative.");
            }

            if (usedOnTurn < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(usedOnTurn), usedOnTurn, "Used-on-turn must be -1 or non-negative.");
            }

            if (offTurnOffers == null || reservations == null || windowIds == null || windowIds.Count != 4)
            {
                throw new ArgumentException("Sleeve bloom restore requires offers, reservations and four window ids.");
            }

            if (immediateOffer != null && immediateOffer.Mode != SleeveBloomOfferMode.Immediate)
            {
                throw new ArgumentException("The immediate offer slot cannot hold an off-turn offer.", nameof(immediateOffer));
            }

            var windows = new string?[4];
            for (int seat = 0; seat < 4; seat++)
            {
                string? id = windowIds[seat];
                if (id != null && string.IsNullOrWhiteSpace(id))
                {
                    throw new ArgumentException("Window ids cannot be blank strings.", nameof(windowIds));
                }

                windows[seat] = id;
            }

            var offers = new SleeveBloomOffer[offTurnOffers.Count];
            var offerSeats = new bool[4];
            for (int i = 0; i < offTurnOffers.Count; i++)
            {
                SleeveBloomOffer offer = offTurnOffers[i] ??
                    throw new ArgumentException("Off-turn offers cannot contain null.", nameof(offTurnOffers));
                if (offer.Mode != SleeveBloomOfferMode.OffTurn || offer.Seat < 0 || offer.Seat > 3 ||
                    offerSeats[offer.Seat])
                {
                    throw new ArgumentException("Off-turn offers must be one per seat and in off-turn mode.", nameof(offTurnOffers));
                }

                offerSeats[offer.Seat] = true;
                offers[i] = offer;
            }

            Array.Sort(offers, (left, right) => left.Seat.CompareTo(right.Seat));

            var reservationCopy = new SleeveBloomReservation[reservations.Count];
            var reservationSeats = new bool[4];
            for (int i = 0; i < reservations.Count; i++)
            {
                SleeveBloomReservation reservation = reservations[i] ??
                    throw new ArgumentException("Reservations cannot contain null.", nameof(reservations));
                if (reservation.Seat < 0 || reservation.Seat > 3 || reservationSeats[reservation.Seat])
                {
                    throw new ArgumentException("Reservations must be one per seat.", nameof(reservations));
                }

                reservationSeats[reservation.Seat] = true;
                reservationCopy[i] = reservation;
            }

            Array.Sort(reservationCopy, (left, right) => left.Seat.CompareTo(right.Seat));
            return new SleeveBloomState(immediateOffer, offers, reservationCopy, windows, windowSeq, usedOnTurn, version);
        }

        public SleeveBloomOffer? ImmediateOffer { get; }

        /// <summary>At most one off-turn offer per seat; ordered by seat.</summary>
        public IReadOnlyList<SleeveBloomOffer> OffTurnOffers => new ReadOnlyCollection<SleeveBloomOffer>(_offTurnOffers);

        public IReadOnlyList<SleeveBloomReservation> Reservations => new ReadOnlyCollection<SleeveBloomReservation>(_reservations);

        public SleeveBloomOffer? OffTurnOfferFor(int seat)
        {
            for (int i = 0; i < _offTurnOffers.Length; i++)
            {
                if (_offTurnOffers[i].Seat == seat)
                {
                    return _offTurnOffers[i];
                }
            }

            return null;
        }

        public SleeveBloomReservation? ReservationFor(int seat)
        {
            for (int i = 0; i < _reservations.Length; i++)
            {
                if (_reservations[i].Seat == seat)
                {
                    return _reservations[i];
                }
            }

            return null;
        }

        /// <summary>Window id issued to the seat after its latest discard; null when closed.</summary>
        public string? WindowIdFor(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            return _windowIds[seat];
        }

        public int WindowSeq { get; }

        /// <summary>Turn number on which the immediate path was last used; -1 when never.</summary>
        public int UsedOnTurn { get; }

        /// <summary>
        /// Monotonic generation of this sub-state: +1 on every content change (offers,
        /// reservations, windows, per-turn marker) and unchanged on no-op re-wraps. Feeds
        /// <see cref="CheatState.Version"/> so choices, deliveries and expiries stay
        /// observable even when the ledger and the marks stand still.
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// Issues a fresh off-turn window to the seat after its discard (V4
        /// <c>openSleeveBloomWindow</c>); issuing again overwrites the previous id, which
        /// immediately becomes stale. The sequence number is derived from state, never from
        /// a hidden counter.
        /// </summary>
        public SleeveBloomState WithOpenedWindow(int seat, int turnNumber, out string windowId)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (turnNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(turnNumber), turnNumber, "Turn number must be non-negative.");
            }

            windowId = "bloom-window-" + seat + "-" + turnNumber + "-" + WindowSeq;
            var nextWindows = (string?[])_windowIds.Clone();
            nextWindows[seat] = windowId;
            return Next(ImmediateOffer, CloneOffers(), CloneReservations(), nextWindows, WindowSeq + 1, UsedOnTurn);
        }

        public SleeveBloomState WithClearedWindow(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            var nextWindows = (string?[])_windowIds.Clone();
            nextWindows[seat] = null;
            return Next(ImmediateOffer, CloneOffers(), CloneReservations(), nextWindows, WindowSeq, UsedOnTurn);
        }

        public SleeveBloomState WithImmediateOffer(SleeveBloomOffer offer)
        {
            if (offer == null)
            {
                throw new ArgumentNullException(nameof(offer));
            }

            return Next(offer, CloneOffers(), CloneReservations(), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
        }

        public SleeveBloomState WithClearedImmediateOffer() =>
            Next(null, CloneOffers(), CloneReservations(), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);

        /// <summary>Installs or replaces the seat's off-turn offer (V4 overwrite semantics).</summary>
        public SleeveBloomState WithOffTurnOffer(SleeveBloomOffer offer)
        {
            if (offer == null)
            {
                throw new ArgumentNullException(nameof(offer));
            }

            var next = new List<SleeveBloomOffer>(_offTurnOffers);
            next.RemoveAll(existing => existing.Seat == offer.Seat);
            next.Add(offer);
            return Next(ImmediateOffer, SortedOffers(next), CloneReservations(), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
        }

        public SleeveBloomState WithoutOffTurnOffer(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            return Next(ImmediateOffer, OffersWithout(seat), CloneReservations(), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
        }

        public SleeveBloomState WithReservation(SleeveBloomReservation reservation)
        {
            if (reservation == null)
            {
                throw new ArgumentNullException(nameof(reservation));
            }

            return Next(ImmediateOffer, CloneOffers(), ReservationsWith(reservation), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
        }

        public SleeveBloomState WithoutReservation(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            return Next(ImmediateOffer, CloneOffers(), ReservationsWithout(seat), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
        }

        public SleeveBloomState WithUsedOnTurn(int turnNumber) =>
            Next(ImmediateOffer, CloneOffers(), CloneReservations(), (string?[])_windowIds.Clone(), WindowSeq, turnNumber);

        /// <summary>
        /// Converts the seat's off-turn offer into a FIRST reservation before its next normal
        /// draw (V4 <c>settleSleeveBloomOfferForDraw</c>). No charge, no duplicate booking;
        /// false when no offer exists.
        /// </summary>
        public bool TrySettleOffTurnForDraw(int seat, out SleeveBloomState next)
        {
            SleeveBloomOffer? offer = OffTurnOfferFor(seat);
            if (offer == null)
            {
                next = this;
                return false;
            }

            var reservation = new SleeveBloomReservation(seat, offer.OfferId, offer.FirstTile, offer.WindowId, offer.CreatedAtMillis);
            next = Next(ImmediateOffer, OffersWithout(seat), ReservationsWith(reservation), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
            return true;
        }

        /// <summary>
        /// Resolves the seat's off-turn offer: the kept candidate becomes a locked
        /// reservation while the other tile is released in place (no physical move yet).
        /// </summary>
        public bool TryResolveOffTurnOffer(int seat, SleeveBloomKeep keep, out SleeveBloomState next, out SleeveBloomReservation? reservation)
        {
            SleeveBloomOffer? offer = OffTurnOfferFor(seat);
            if (offer == null)
            {
                next = this;
                reservation = null;
                return false;
            }

            TileId chosen = keep == SleeveBloomKeep.First ? offer.FirstTile : offer.SecondTile;
            reservation = new SleeveBloomReservation(seat, offer.OfferId, chosen, offer.WindowId, offer.CreatedAtMillis);
            next = Next(ImmediateOffer, OffersWithout(seat), ReservationsWith(reservation), (string?[])_windowIds.Clone(), WindowSeq, UsedOnTurn);
            return true;
        }

        /// <summary>End-of-round cleanup: everything unlocked in place, nothing refunded.</summary>
        public SleeveBloomState Cleared() =>
            Next(null, Array.Empty<SleeveBloomOffer>(), Array.Empty<SleeveBloomReservation>(), new string?[4], WindowSeq, UsedOnTurn);

        /// <summary>Installs new content when it differs; identical content returns this instance un-versioned.</summary>
        private SleeveBloomState Next(
            SleeveBloomOffer? immediateOffer,
            SleeveBloomOffer[] offTurnOffers,
            SleeveBloomReservation[] reservations,
            string?[] windowIds,
            int windowSeq,
            int usedOnTurn)
        {
            bool unchanged = NullableEquals(ImmediateOffer, immediateOffer) &&
                OffersEqual(_offTurnOffers, offTurnOffers) &&
                ReservationsEqual(_reservations, reservations) &&
                WindowIdsEqual(_windowIds, windowIds) &&
                WindowSeq == windowSeq &&
                UsedOnTurn == usedOnTurn;
            return unchanged
                ? this
                : new SleeveBloomState(immediateOffer, offTurnOffers, reservations, windowIds, windowSeq, usedOnTurn, Version + 1);
        }

        private static bool NullableEquals(SleeveBloomOffer? left, SleeveBloomOffer? right) =>
            left == null ? right == null : right != null && left.Equals(right);

        private static bool OffersEqual(SleeveBloomOffer[] left, SleeveBloomOffer[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ReservationsEqual(SleeveBloomReservation[] left, SleeveBloomReservation[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool WindowIdsEqual(string?[] left, string?[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private SleeveBloomOffer[] OffersWithout(int seat)
        {
            var next = new List<SleeveBloomOffer>(_offTurnOffers);
            next.RemoveAll(existing => existing.Seat == seat);
            return SortedOffers(next);
        }

        private SleeveBloomReservation[] ReservationsWith(SleeveBloomReservation reservation)
        {
            var next = new List<SleeveBloomReservation>(_reservations);
            next.RemoveAll(existing => existing.Seat == reservation.Seat);
            next.Add(reservation);
            next.Sort((left, right) => left.Seat.CompareTo(right.Seat));
            return next.ToArray();
        }

        private SleeveBloomReservation[] ReservationsWithout(int seat)
        {
            var next = new List<SleeveBloomReservation>(_reservations);
            next.RemoveAll(existing => existing.Seat == seat);
            return next.ToArray();
        }

        private static SleeveBloomOffer[] SortedOffers(List<SleeveBloomOffer> offers)
        {
            offers.Sort((left, right) => left.Seat.CompareTo(right.Seat));
            return offers.ToArray();
        }

        /// <summary>True when the tile is locked by any off-turn offer or reservation.</summary>
        public bool IsProtectedTile(TileId tile)
        {
            for (int i = 0; i < _offTurnOffers.Length; i++)
            {
                if (_offTurnOffers[i].FirstTile.Equals(tile) || _offTurnOffers[i].SecondTile.Equals(tile))
                {
                    return true;
                }
            }

            for (int i = 0; i < _reservations.Length; i++)
            {
                if (_reservations[i].Tile.Equals(tile))
                {
                    return true;
                }
            }

            return false;
        }

        private SleeveBloomOffer[] CloneOffers()
        {
            var copy = new SleeveBloomOffer[_offTurnOffers.Length];
            Array.Copy(_offTurnOffers, copy, copy.Length);
            return copy;
        }

        private SleeveBloomReservation[] CloneReservations()
        {
            var copy = new SleeveBloomReservation[_reservations.Length];
            Array.Copy(_reservations, copy, copy.Length);
            return copy;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Mahjong.Domain.Cheat
{
    /// <summary>
    /// Stable transition error codes for the cheat layer. They mirror the V4 rejection
    /// reasons (TileMarkSubmissionHandler, SleeveBloomSubmissionHandler, BeamSwapSubmissionHandler);
    /// control-layer concerns (decision ids, control epochs, per-decision deadlines) are out
    /// of domain scope and rejected upstream in P3.2.
    /// </summary>
    public enum CheatErrorCode : byte
    {
        None = 0,
        Disabled = 1,
        CostNotConfigured = 2,
        CostMustBeTen = 3,
        InvalidSeat = 4,
        InvalidRequest = 5,
        NotYourTurn = 6,
        WrongPhase = 7,
        ResponseWindowOpen = 8,
        RiichiLocked = 9,
        NoDrawnTile = 10,
        DrawnTileNotInHand = 11,
        DrawKindMismatch = 12,
        DealExtraDraw = 13,
        OfferAlreadyPending = 14,
        OtherOfferPending = 15,
        ReservedPending = 16,
        AlreadyUsedThisTurn = 17,
        InsufficientFunds = 18,
        LedgerConflict = 19,
        NoWindow = 20,
        WindowMismatch = 21,
        NoPendingOffer = 22,
        OfferMismatch = 23,
        WallEmpty = 24,
        NoWallCandidate = 25,
        CandidateUnavailable = 26,
        DeadlineExpired = 27,
        StaleOffer = 28,
        InvalidSelection = 29,
        TileNotOwned = 30,
        AlreadyMarked = 31,
        MarkCapacityExceeded = 32,
        MarkRoundMismatch = 33,
        CandidateNotFound = 34,
        SlotMismatch = 35,
        HandDrift = 36,
        CandidateDrift = 37,
        PhysicalMoveFailed = 38,
        ReservationMissing = 39,
        DeliveryNotPossible = 40,
        LedgerOverflow = 41,
        GroupIdConflict = 42
    }

    /// <summary>
    /// One atomic cheat transition. On success the new cheat state and (when the trick
    /// physically moves tiles) the new map travel together with the emitted private events;
    /// on rejection both are null so the caller's source state is untouched. The one V4
    /// exception is the deadline-expired off-turn choice, which still installs its forced
    /// FIRST settlement: <see cref="Success"/> is false with <see cref="State"/> applied.
    /// </summary>
    public sealed class CheatTransition
    {
        private CheatTransition(
            bool success,
            bool changed,
            CheatErrorCode error,
            string? message,
            CheatState? state,
            TilePositionMap? map,
            IReadOnlyList<CheatEvent> events)
        {
            Success = success;
            Changed = changed;
            Error = error;
            Message = message;
            State = state;
            Map = map;
            Events = events;
        }

        public bool Success { get; }

        /// <summary>False when the transition replays to the identical state.</summary>
        public bool Changed { get; }

        public CheatErrorCode Error { get; }

        public string? Message { get; }

        /// <summary>Next cheat state when applied (including forced deadline settlements); otherwise null.</summary>
        public CheatState? State { get; }

        /// <summary>Next physical map when the trick moved tiles; otherwise null (no movement).</summary>
        public TilePositionMap? Map { get; }

        public IReadOnlyList<CheatEvent> Events { get; }

        public static CheatTransition Applied(CheatState state, TilePositionMap? map, IReadOnlyList<CheatEvent> events) =>
            new CheatTransition(true, true, CheatErrorCode.None, null, state, map, events);

        public static CheatTransition Replayed(CheatState state, IReadOnlyList<CheatEvent> events) =>
            new CheatTransition(true, false, CheatErrorCode.None, null, state, null, events);

        public static CheatTransition Rejected(CheatErrorCode error, string message) =>
            new CheatTransition(false, false, error, message, null, null, Array.Empty<CheatEvent>());

        /// <summary>V4 deadline handling: the forced FIRST settlement installs, the reply is still a rejection.</summary>
        public static CheatTransition RejectedWithState(CheatErrorCode error, string message, CheatState state, IReadOnlyList<CheatEvent> events) =>
            new CheatTransition(false, true, error, message, state, null, events);
    }

    /// <summary>
    /// Immutable aggregate of the P1.4 cheat layer: ledger, tile marks, sleeve bloom and
    /// beam swap. Every applied transition returns a new instance. <see cref="Version"/> is
    /// the sum of the four sub-state generations, so it advances once per applied cheat
    /// change — including sleeve/beam choices, reservations, deliveries and expiries that
    /// touch neither the ledger nor the marks — and stays put on no-op re-wraps. The
    /// fingerprint is a deterministic FNV-1a over every cheat content field (see
    /// <see cref="ComputeFingerprint"/>), suitable for inclusion in round source
    /// fingerprints.
    /// </summary>
    public sealed class CheatState
    {
        public CheatState(CheatLedger ledger, TileMarkState tileMarks, SleeveBloomState sleeveBloom, BeamSwapState beamSwap)
        {
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            TileMarks = tileMarks ?? throw new ArgumentNullException(nameof(tileMarks));
            SleeveBloom = sleeveBloom ?? throw new ArgumentNullException(nameof(sleeveBloom));
            BeamSwap = beamSwap ?? throw new ArgumentNullException(nameof(beamSwap));
            Version = ledger.Version + tileMarks.Version + sleeveBloom.Version + beamSwap.Version;
            Fingerprint = ComputeFingerprint(ledger, tileMarks, sleeveBloom, beamSwap);
        }

        public CheatLedger Ledger { get; }

        public TileMarkState TileMarks { get; }

        public SleeveBloomState SleeveBloom { get; }

        public BeamSwapState BeamSwap { get; }

        public long Version { get; }

        public ulong Fingerprint { get; }

        public static CheatState Initialize(CheatEconomyConfig economy)
        {
            if (economy == null)
            {
                throw new ArgumentNullException(nameof(economy));
            }

            return new CheatState(
                CheatLedger.Initialize(economy),
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
        }

        public CheatState WithLedger(CheatLedger ledger) =>
            new CheatState(ledger, TileMarks, SleeveBloom, BeamSwap);

        public CheatState WithTileMarks(TileMarkState tileMarks) =>
            new CheatState(Ledger, tileMarks, SleeveBloom, BeamSwap);

        public CheatState WithSleeveBloom(SleeveBloomState sleeveBloom) =>
            new CheatState(Ledger, TileMarks, sleeveBloom, BeamSwap);

        public CheatState WithBeamSwap(BeamSwapState beamSwap) =>
            new CheatState(Ledger, TileMarks, SleeveBloom, beamSwap);

        /// <summary>Seat-whitelisted private projection; other seats observe none of this.</summary>
        public CheatPrivateView ViewFor(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            var ownEntries = new List<CheatLedgerEntry>();
            for (int i = 0; i < Ledger.Entries.Count; i++)
            {
                if (Ledger.Entries[i].Seat == seat)
                {
                    ownEntries.Add(Ledger.Entries[i]);
                }
            }

            return new CheatPrivateView(
                seat,
                Ledger.BalanceOf(seat),
                new ReadOnlyCollection<CheatLedgerEntry>(ownEntries),
                TileMarks.GroupsForSeat(seat),
                SleeveBloom.ImmediateOffer != null && SleeveBloom.ImmediateOffer.Seat == seat ? SleeveBloom.ImmediateOffer : null,
                SleeveBloom.OffTurnOfferFor(seat),
                SleeveBloom.ReservationFor(seat),
                SleeveBloom.WindowIdFor(seat),
                BeamSwap.Offer != null && BeamSwap.Offer.Seat == seat ? BeamSwap.Offer : null);
        }

        public CheatPublicSnapshot PublicSnapshot => new CheatPublicSnapshot();

        /// <summary>
        /// Deterministic FNV-1a over all cheat content: the ledger balances and each entry's
        /// full payload (entry id, seat, amount, balance, reason); the mark registry's dealt
        /// round ids and settled round ids (the current round id is the last dealt entry);
        /// every mark group's id, owner, creation round and tiles; every sleeve/beam offer's
        /// id, mode, window id, turn and timestamps; the per-seat window ids; the
        /// reservations' full payloads; and the beam eligible-hand set with its frozen
        /// candidates. Every variable-length section is length-prefixed and every nullable
        /// object carries an explicit presence tag, so distinct source contents never share
        /// a byte stream. P3.2 stale-result detection is the consumer: a client echoes the
        /// fingerprint it acted on and the authority compares it with the current value — a
        /// mismatch means the private cheat state moved underneath the request and the reply
        /// must be treated as stale.
        /// </summary>
        private static ulong ComputeFingerprint(
            CheatLedger ledger,
            TileMarkState tileMarks,
            SleeveBloomState sleeveBloom,
            BeamSwapState beamSwap)
        {
            ulong hash = FnvOffsetBasis;
            hash = MixByte(hash, (byte)'C');

            hash = MixValue(hash, (ulong)ledger.Version);
            for (int i = 0; i < ledger.Balances.Count; i++)
            {
                hash = MixValue(hash, (ulong)(uint)ledger.Balances[i]);
            }

            hash = MixValue(hash, (ulong)(uint)ledger.Entries.Count);
            for (int i = 0; i < ledger.Entries.Count; i++)
            {
                CheatLedgerEntry entry = ledger.Entries[i];
                hash = MixString(hash, entry.EntryId);
                hash = MixValue(hash, (ulong)(uint)entry.Seat);
                hash = MixValue(hash, (ulong)(uint)entry.Amount);
                hash = MixValue(hash, (ulong)(uint)entry.BalanceAfter);
                hash = MixString(hash, entry.Reason);
            }

            hash = MixValue(hash, (ulong)tileMarks.Version);
            hash = MixValue(hash, (ulong)(uint)tileMarks.DealtRoundOrdinal);
            // the dealt/settled round registry: the current round id is the last dealt entry
            hash = MixValue(hash, (ulong)(uint)tileMarks.DealtRoundIds.Count);
            for (int i = 0; i < tileMarks.DealtRoundIds.Count; i++)
            {
                hash = MixString(hash, tileMarks.DealtRoundIds[i]);
            }

            hash = MixValue(hash, (ulong)(uint)tileMarks.SettledRoundIds.Count);
            for (int i = 0; i < tileMarks.SettledRoundIds.Count; i++)
            {
                hash = MixString(hash, tileMarks.SettledRoundIds[i]);
            }

            hash = MixValue(hash, (ulong)(uint)tileMarks.Groups.Count);
            for (int i = 0; i < tileMarks.Groups.Count; i++)
            {
                TileMarkGroup group = tileMarks.Groups[i];
                hash = MixString(hash, group.GroupId);
                hash = MixValue(hash, (ulong)(uint)group.OwnerSeat);
                hash = MixString(hash, group.CreatedRoundId);
                hash = MixValue(hash, (ulong)(uint)group.CreatedRoundOrdinal);
                for (int t = 0; t < group.TileIds.Count; t++)
                {
                    hash = MixValue(hash, (ulong)(uint)group.TileIds[t].Value);
                }
            }

            for (int seat = 0; seat < 4; seat++)
            {
                hash = MixString(hash, sleeveBloom.WindowIdFor(seat));
            }

            hash = MixValue(hash, (ulong)(uint)sleeveBloom.WindowSeq);
            hash = MixValue(hash, (ulong)(uint)(sleeveBloom.UsedOnTurn + 1));

            SleeveBloomOffer? immediate = sleeveBloom.ImmediateOffer;
            hash = MixValue(hash, immediate != null ? 1UL : 0UL);
            if (immediate != null)
            {
                hash = MixString(hash, immediate.OfferId);
                hash = MixValue(hash, (ulong)(uint)immediate.Seat);
                hash = MixValue(hash, (uint)immediate.Mode);
                hash = MixValue(hash, (uint)immediate.DrawKind);
                hash = MixValue(hash, (ulong)(uint)immediate.CreatedTurnNumber);
                // every constructor-legal source field is fingerprint content: the Immediate
                // overload accepts window/timestamp metadata and Equals covers it too
                hash = MixString(hash, immediate.WindowId);
                hash = MixValue(hash, (ulong)immediate.CreatedAtMillis);
                hash = MixValue(hash, (ulong)immediate.DeadlineMillis);
                hash = MixOfferTile(hash, immediate.FirstTile);
                hash = MixOfferTile(hash, immediate.SecondTile);
            }

            hash = MixValue(hash, (ulong)(uint)sleeveBloom.OffTurnOffers.Count);
            for (int i = 0; i < sleeveBloom.OffTurnOffers.Count; i++)
            {
                SleeveBloomOffer offer = sleeveBloom.OffTurnOffers[i];
                hash = MixString(hash, offer.OfferId);
                hash = MixValue(hash, (ulong)(uint)offer.Seat);
                hash = MixValue(hash, (uint)offer.Mode);
                hash = MixValue(hash, (uint)offer.DrawKind);
                hash = MixValue(hash, (ulong)(uint)offer.CreatedTurnNumber);
                hash = MixString(hash, offer.WindowId);
                hash = MixValue(hash, (ulong)offer.CreatedAtMillis);
                hash = MixValue(hash, (ulong)offer.DeadlineMillis);
                hash = MixOfferTile(hash, offer.FirstTile);
                hash = MixOfferTile(hash, offer.SecondTile);
            }

            hash = MixValue(hash, (ulong)(uint)sleeveBloom.Reservations.Count);
            for (int i = 0; i < sleeveBloom.Reservations.Count; i++)
            {
                SleeveBloomReservation reservation = sleeveBloom.Reservations[i];
                hash = MixString(hash, reservation.OfferId);
                hash = MixValue(hash, (ulong)(uint)reservation.Seat);
                hash = MixString(hash, reservation.WindowId);
                hash = MixValue(hash, (ulong)reservation.CreatedAtMillis);
                hash = MixOfferTile(hash, reservation.Tile);
            }

            hash = MixValue(hash, (ulong)(uint)(beamSwap.UsedOnTurn + 1));
            hash = MixValue(hash, beamSwap.Offer != null ? 1UL : 0UL);
            if (beamSwap.Offer != null)
            {
                hash = MixString(hash, beamSwap.Offer.OfferId);
                hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.Seat);
                hash = MixValue(hash, (uint)beamSwap.Offer.Window);
                hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.CreatedTurnNumber);
                hash = MixOfferTile(hash, beamSwap.Offer.DrawnTileId);
                hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.EligibleHandTileIds.Count);
                for (int i = 0; i < beamSwap.Offer.EligibleHandTileIds.Count; i++)
                {
                    hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.EligibleHandTileIds[i].Value);
                }

                hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.Candidates.Count);
                for (int i = 0; i < beamSwap.Offer.Candidates.Count; i++)
                {
                    hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.Candidates[i].Tile.Value);
                    hash = MixValue(hash, (ulong)(uint)beamSwap.Offer.Candidates[i].Slot.Index);
                    hash = MixValue(hash, (uint)beamSwap.Offer.Candidates[i].Slot.Kind);
                }
            }

            return hash;
        }

        /// <summary>Length-prefixed ordinal mix so distinct ids never collide by truncation.</summary>
        private static ulong MixString(ulong hash, string? value)
        {
            if (value == null)
            {
                return MixValue(hash, 0xFFFFFFFFUL);
            }

            hash = MixValue(hash, (ulong)(uint)value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                hash = MixByte(hash, (byte)(value[i] & 0xFF));
                hash = MixByte(hash, (byte)((uint)value[i] >> 8));
            }

            return hash;
        }

        private static ulong MixOfferTile(ulong hash, TileId? tile) =>
            tile.HasValue ? MixValue(hash, (ulong)(uint)tile.Value.Value) : MixValue(hash, 0xFFFFFFFFUL);

        private static ulong MixValue(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash = MixByte(hash, (byte)(value >> (i * 8)));
            }

            return hash;
        }

        private static ulong MixByte(ulong hash, byte value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }

        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;
    }

    /// <summary>
    /// Private cheat-layer events (RULE-CONTRACT §8.3 / D-11). V4 resolves both bloom and
    /// beam choices without events; V5 requires at least one event per state change, so the
    /// resolution steps carry these owner-scoped events. They must never enter a public
    /// snapshot: only the acting seat's projection may observe them.
    /// </summary>
    public enum CheatEventKind : byte
    {
        CheatPointsChanged = 0,
        TileMarkApplied = 1,
        SleeveBloomResolved = 2,
        SleeveBloomOfferCleared = 3,
        BeamSwapResolved = 4,
        BeamSwapOfferCleared = 5,
        SleeveBloomReservationDelivered = 6
    }

    public sealed class CheatEvent : IEquatable<CheatEvent>
    {
        private CheatEvent(
            CheatEventKind kind,
            int seat,
            string? entryId,
            int amount,
            int newBalance,
            string? offerId,
            string? groupId,
            SleeveBloomKeep? keep,
            BeamSwapAction? action,
            string? reason)
        {
            Kind = kind;
            Seat = seat;
            EntryId = entryId;
            Amount = amount;
            NewBalance = newBalance;
            OfferId = offerId;
            GroupId = groupId;
            Keep = keep;
            Action = action;
            Reason = reason;
        }

        public CheatEventKind Kind { get; }

        public int Seat { get; }

        public string? EntryId { get; }

        /// <summary>Signed point movement; zero when not applicable.</summary>
        public int Amount { get; }

        /// <summary>Balance after the movement; -1 when not applicable.</summary>
        public int NewBalance { get; }

        public string? OfferId { get; }

        public string? GroupId { get; }

        public SleeveBloomKeep? Keep { get; }

        public BeamSwapAction? Action { get; }

        /// <summary>Short clear reason ("Stale" / "Expired") for offer-cleared events.</summary>
        public string? Reason { get; }

        public static CheatEvent CheatPointsChanged(int seat, string entryId, int amount, int newBalance) =>
            new CheatEvent(CheatEventKind.CheatPointsChanged, seat, entryId, amount, newBalance, null, null, null, null, null);

        public static CheatEvent TileMarkApplied(int seat, string groupId) =>
            new CheatEvent(CheatEventKind.TileMarkApplied, seat, null, 0, -1, null, groupId, null, null, null);

        public static CheatEvent SleeveBloomResolved(int seat, string offerId, SleeveBloomKeep keep) =>
            new CheatEvent(CheatEventKind.SleeveBloomResolved, seat, null, 0, -1, offerId, null, keep, null, null);

        public static CheatEvent SleeveBloomOfferCleared(int seat, string offerId, string reason) =>
            new CheatEvent(CheatEventKind.SleeveBloomOfferCleared, seat, null, 0, -1, offerId, null, null, null, reason);

        /// <summary>Owner-scoped notice that a locked reservation physically landed in hand.</summary>
        public static CheatEvent SleeveBloomReservationDelivered(int seat, string offerId) =>
            new CheatEvent(CheatEventKind.SleeveBloomReservationDelivered, seat, null, 0, -1, offerId, null, null, null, null);

        public static CheatEvent BeamSwapResolved(int seat, string offerId, BeamSwapAction action) =>
            new CheatEvent(CheatEventKind.BeamSwapResolved, seat, null, 0, -1, offerId, null, null, action, null);

        public static CheatEvent BeamSwapOfferCleared(int seat, string offerId, string reason) =>
            new CheatEvent(CheatEventKind.BeamSwapOfferCleared, seat, null, 0, -1, offerId, null, null, null, reason);

        public bool Equals(CheatEvent? other) =>
            other != null &&
            Kind == other.Kind &&
            Seat == other.Seat &&
            EntryId == other.EntryId &&
            Amount == other.Amount &&
            NewBalance == other.NewBalance &&
            OfferId == other.OfferId &&
            GroupId == other.GroupId &&
            Keep == other.Keep &&
            Action == other.Action &&
            Reason == other.Reason;

        public override bool Equals(object? obj) => obj is CheatEvent other && Equals(other);

        public override int GetHashCode() =>
            ((((int)Kind * 31 + Seat) * 31 + Amount) * 31 + NewBalance) * 31 +
            (EntryId?.GetHashCode(StringComparison.Ordinal) ?? 0);

        public override string ToString()
        {
            string head = Kind + "[seat" + Seat.ToString(CultureInfo.InvariantCulture) + "]";
            switch (Kind)
            {
                case CheatEventKind.CheatPointsChanged:
                    return head + " " + EntryId + " " + Amount.ToString("+0;-0;0", CultureInfo.InvariantCulture) +
                           " -> " + NewBalance.ToString(CultureInfo.InvariantCulture);
                case CheatEventKind.TileMarkApplied:
                    return head + " " + GroupId;
                case CheatEventKind.SleeveBloomResolved:
                    return head + " " + OfferId + " keep=" + Keep;
                case CheatEventKind.BeamSwapResolved:
                    return head + " " + OfferId + " action=" + Action;
                case CheatEventKind.SleeveBloomReservationDelivered:
                    return head + " " + OfferId + " delivered";
                case CheatEventKind.SleeveBloomOfferCleared:
                case CheatEventKind.BeamSwapOfferCleared:
                    return head + " " + OfferId + " reason=" + Reason;
                default:
                    return head;
            }
        }
    }
}

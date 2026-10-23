using System.Collections.Generic;

namespace Mahjong.Domain.Cheat
{
    /// <summary>
    /// Owner-only projection of every private cheat fact for one seat (balance, own ledger
    /// entries, mark groups, pending offers, reservation, window). This is the only surface
    /// through which private cheat information leaves the state (V5-0-VISIBILITY-MATRIX §2/§6);
    /// spectators and other seats must never see hole cards, sleeve tiles, mark details or
    /// the private ledger. Produced by <see cref="CheatState.ViewFor"/>.
    /// </summary>
    public sealed class CheatPrivateView
    {
        internal CheatPrivateView(
            int seat,
            int balance,
            IReadOnlyList<CheatLedgerEntry> entries,
            IReadOnlyList<TileMarkGroup> markGroups,
            SleeveBloomOffer? immediateOffer,
            SleeveBloomOffer? offTurnOffer,
            SleeveBloomReservation? reservation,
            string? windowId,
            BeamSwapOffer? beamSwapOffer)
        {
            Seat = seat;
            Balance = balance;
            Entries = entries;
            MarkGroups = markGroups;
            ImmediateOffer = immediateOffer;
            OffTurnOffer = offTurnOffer;
            Reservation = reservation;
            WindowId = windowId;
            BeamSwapOffer = beamSwapOffer;
        }

        public int Seat { get; }

        public int Balance { get; }

        public IReadOnlyList<CheatLedgerEntry> Entries { get; }

        public IReadOnlyList<TileMarkGroup> MarkGroups { get; }

        public SleeveBloomOffer? ImmediateOffer { get; }

        public SleeveBloomOffer? OffTurnOffer { get; }

        public SleeveBloomReservation? Reservation { get; }

        public string? WindowId { get; }

        public BeamSwapOffer? BeamSwapOffer { get; }
    }

    /// <summary>
    /// Public cheat projection. Deliberately empty: balances, ledger entries, mark groups,
    /// offers, reservations, windows and wall contents are private (V5-0-VISIBILITY-MATRIX
    /// §2/§6) and may only flow through <see cref="CheatState.ViewFor"/>. P3.2 projection
    /// work must not extend this type with per-seat cheat content.
    /// </summary>
    public sealed class CheatPublicSnapshot
    {
        internal CheatPublicSnapshot()
        {
        }
    }
}

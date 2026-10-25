using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain.Cheat
{
    /// <summary>Beam swap window: normal-draw live wall versus rinshan dead wall (V4).</summary>
    public enum BeamSwapWindow : byte
    {
        Wall = 0,
        DeadWall = 1
    }

    /// <summary>The two beam swap resolutions; KEEP_ORIGINAL spends the fee without moving tiles.</summary>
    public enum BeamSwapAction : byte
    {
        Swap = 0,
        KeepOriginal = 1
    }

    public enum BeamSwapSlotKind : byte
    {
        WallSlot = 0,
        DeadWallSlot = 1,
        ReplacementSlot = 2
    }

    /// <summary>Target slot of a beam swap candidate; index is non-negative.</summary>
    public readonly struct BeamSwapSlot : IEquatable<BeamSwapSlot>
    {
        private BeamSwapSlot(BeamSwapSlotKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }

        public BeamSwapSlotKind Kind { get; }

        public int Index { get; }

        public static BeamSwapSlot Wall(int index) => New(BeamSwapSlotKind.WallSlot, index);

        public static BeamSwapSlot DeadWall(int index) => New(BeamSwapSlotKind.DeadWallSlot, index);

        public static BeamSwapSlot Replacement(int index) => New(BeamSwapSlotKind.ReplacementSlot, index);

        private static BeamSwapSlot New(BeamSwapSlotKind kind, int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Slot index must be non-negative.");
            }

            return new BeamSwapSlot(kind, index);
        }

        public bool Equals(BeamSwapSlot other) => Kind == other.Kind && Index == other.Index;

        public override bool Equals(object? obj) => obj is BeamSwapSlot other && Equals(other);

        public override int GetHashCode() => (int)Kind * 1000 + Index;

        public override string ToString() => Kind + "(" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

        public static bool operator ==(BeamSwapSlot left, BeamSwapSlot right) => left.Equals(right);

        public static bool operator !=(BeamSwapSlot left, BeamSwapSlot right) => !left.Equals(right);
    }

    /// <summary>One frozen beam swap candidate: physical tile plus its slot.</summary>
    public sealed class BeamSwapCandidate : IEquatable<BeamSwapCandidate>
    {
        public BeamSwapCandidate(TileId tile, BeamSwapSlot slot)
        {
            Tile = tile;
            Slot = slot;
        }

        public TileId Tile { get; }

        public BeamSwapSlot Slot { get; }

        public bool Equals(BeamSwapCandidate? other) =>
            other != null && Tile.Equals(other.Tile) && Slot.Equals(other.Slot);

        public override bool Equals(object? obj) => obj is BeamSwapCandidate other && Equals(other);

        public override int GetHashCode() => Tile.Value * 31 + Slot.GetHashCode();

        public override string ToString() => Tile + "@" + Slot;
    }

    /// <summary>
    /// The frozen beam swap offer (V4 <c>BeamSwapOffer</c>): candidates, eligible closed hand
    /// tiles, the drawn tile id and the creation turn are all snapshotted at activation so a
    /// later choice can detect drift against the live map.
    /// </summary>
    public sealed class BeamSwapOffer : IEquatable<BeamSwapOffer>
    {
        private readonly ReadOnlyCollection<BeamSwapCandidate> _candidates;
        private readonly ReadOnlyCollection<TileId> _eligibleHandTileIds;

        public BeamSwapOffer(
            string offerId,
            int seat,
            BeamSwapWindow window,
            IReadOnlyList<BeamSwapCandidate> candidates,
            IReadOnlyList<TileId> eligibleHandTileIds,
            TileId drawnTileId,
            int createdTurnNumber)
        {
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new ArgumentException("Offer id cannot be blank.", nameof(offerId));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (candidates == null || candidates.Count == 0)
            {
                throw new ArgumentException("Beam swap offers require at least one candidate.", nameof(candidates));
            }

            if (eligibleHandTileIds == null || eligibleHandTileIds.Count == 0)
            {
                throw new ArgumentException("Beam swap offers require at least one eligible hand tile.", nameof(eligibleHandTileIds));
            }

            if (createdTurnNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(createdTurnNumber), createdTurnNumber, "Created turn number must be non-negative.");
            }

            var candidateCopy = new BeamSwapCandidate[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                candidateCopy[i] = candidates[i] ?? throw new ArgumentException("Candidates cannot contain null.", nameof(candidates));
            }

            var handCopy = new TileId[eligibleHandTileIds.Count];
            for (int i = 0; i < eligibleHandTileIds.Count; i++)
            {
                handCopy[i] = eligibleHandTileIds[i];
            }

            OfferId = offerId;
            Seat = seat;
            Window = window;
            _candidates = new ReadOnlyCollection<BeamSwapCandidate>(candidateCopy);
            _eligibleHandTileIds = new ReadOnlyCollection<TileId>(handCopy);
            DrawnTileId = drawnTileId;
            CreatedTurnNumber = createdTurnNumber;
        }

        public string OfferId { get; }

        public int Seat { get; }

        public BeamSwapWindow Window { get; }

        public IReadOnlyList<BeamSwapCandidate> Candidates => _candidates;

        public IReadOnlyList<TileId> EligibleHandTileIds => _eligibleHandTileIds;

        public TileId DrawnTileId { get; }

        public int CreatedTurnNumber { get; }

        public BeamSwapCandidate? FindCandidate(BeamSwapSlot slot)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].Slot.Equals(slot))
                {
                    return _candidates[i];
                }
            }

            return null;
        }

        public bool Equals(BeamSwapOffer? other) =>
            other != null &&
            OfferId == other.OfferId &&
            Seat == other.Seat &&
            Window == other.Window &&
            DrawnTileId.Equals(other.DrawnTileId) &&
            CreatedTurnNumber == other.CreatedTurnNumber &&
            ListsEqual(_candidates, other._candidates) &&
            ListsEqual(_eligibleHandTileIds, other._eligibleHandTileIds);

        private static bool ListsEqual<T>(ReadOnlyCollection<T> left, ReadOnlyCollection<T> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!Equals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is BeamSwapOffer other && Equals(other);

        public override int GetHashCode() =>
            ((OfferId.GetHashCode(StringComparison.Ordinal) * 31 + Seat) * 31 +
             (int)Window) * 31 + DrawnTileId.Value;

        public override string ToString() =>
            OfferId + "[seat" + Seat + " " + Window + " candidates=" + _candidates.Count +
            " hand=" + _eligibleHandTileIds.Count + " drawn=" + DrawnTileId + "]";
    }

    /// <summary>Immutable beam swap state: the at-most-one pending offer and per-turn usage.</summary>
    public sealed class BeamSwapState
    {
        private BeamSwapState(BeamSwapOffer? offer, int usedOnTurn, long version)
        {
            Offer = offer;
            UsedOnTurn = usedOnTurn;
            Version = version;
        }

        public BeamSwapState()
            : this(null, -1, 0)
        {
        }

        /// <summary>
        /// Rehydrates an already validated checkpoint aggregate. The offer is the same frozen
        /// typed value used by the command path, so candidates/eligible hand tiles/drawn tile
        /// survive recovery without a summary flag or a re-derived candidate list.
        /// </summary>
        public static BeamSwapState Restore(BeamSwapOffer? offer, int usedOnTurn, long version)
        {
            if (usedOnTurn < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(usedOnTurn), usedOnTurn, "Used-on-turn must be -1 or non-negative.");
            }

            if (version < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, "Beam swap version must be non-negative.");
            }

            return new BeamSwapState(offer, usedOnTurn, version);
        }

        public BeamSwapOffer? Offer { get; }

        /// <summary>Turn number on which the beam swap was last activated; -1 when never.</summary>
        public int UsedOnTurn { get; }

        /// <summary>
        /// Monotonic generation of this sub-state: +1 when the pending offer or the per-turn
        /// marker changes, unchanged on no-op re-wraps. Feeds <see cref="CheatState.Version"/>.
        /// </summary>
        public long Version { get; }

        public BeamSwapState WithOffer(BeamSwapOffer offer)
        {
            if (offer == null)
            {
                throw new ArgumentNullException(nameof(offer));
            }

            return Offer != null && Offer.Equals(offer)
                ? this
                : new BeamSwapState(offer, UsedOnTurn, Version + 1);
        }

        public BeamSwapState WithoutOffer()
        {
            if (Offer == null)
            {
                return this;
            }

            return new BeamSwapState(null, UsedOnTurn, Version + 1);
        }

        public BeamSwapState WithUsedOnTurn(int turnNumber) =>
            UsedOnTurn == turnNumber
                ? this
                : new BeamSwapState(Offer, turnNumber, Version + 1);
    }
}

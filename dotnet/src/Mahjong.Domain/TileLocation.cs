using System;
using System.Globalization;

namespace Mahjong.Domain
{
    /// <summary>
    /// Physical regions tracked by V4. <see cref="DeadWallReplacement"/> corresponds to
    /// V4's separate <c>deadWallReplacements</c> container; V4's TilePool records those
    /// tiles as DEAD_WALL, but BeamSwap addresses replacement slots separately.
    /// </summary>
    public enum TileZone : byte
    {
        Unassigned = 0,
        Wall = 1,
        DeadWall = 2,
        DeadWallReplacement = 3,
        Hand = 4,
        Discard = 5,
        Meld = 6,
        Sleeve = 7
    }

    public static class TileZoneRules
    {
        public static bool RequiresSeat(TileZone zone)
        {
            return zone == TileZone.Hand
                || zone == TileZone.Discard
                || zone == TileZone.Meld
                || zone == TileZone.Sleeve;
        }

        public static bool IsSeatless(TileZone zone)
        {
            return zone == TileZone.Unassigned
                || zone == TileZone.Wall
                || zone == TileZone.DeadWall
                || zone == TileZone.DeadWallReplacement;
        }

        /// <summary>
        /// Upper bound for a slot index. Wall 0..121 and dead wall 0..13 follow V4;
        /// replacement slots are bounded by the four-kan limit; hand/meld/sleeve are
        /// bounded by the maximum physical containers. River has no V4 cap, so 0..135
        /// is used as the physical tile-count bound.
        /// </summary>
        public static int MaxIndex(TileZone zone)
        {
            switch (zone)
            {
                case TileZone.Unassigned:
                    return -1;
                case TileZone.Wall:
                    return 121;
                case TileZone.DeadWall:
                    return 13;
                case TileZone.DeadWallReplacement:
                    return 3;
                case TileZone.Hand:
                    return 13;
                case TileZone.Discard:
                    return 135;
                case TileZone.Meld:
                    return 15;
                case TileZone.Sleeve:
                    return 13;
                default:
                    throw new ArgumentOutOfRangeException(nameof(zone), zone, "Unknown tile zone.");
            }
        }
    }

    /// <summary>
    /// Exact physical slot: region, optional seat owner, and slot index.
    /// A position is immutable and always valid; invalid positions cannot be constructed.
    /// </summary>
    public readonly struct TilePosition : IEquatable<TilePosition>
    {
        private readonly byte _zone;
        private readonly sbyte _seat;
        private readonly short _index;

        private TilePosition(TileZone zone, int seat, int index)
        {
            if (zone < TileZone.Unassigned || zone > TileZone.Sleeve)
            {
                throw new ArgumentOutOfRangeException(nameof(zone), zone, "Unknown tile zone.");
            }

            if (zone == TileZone.Unassigned)
            {
                if (seat != -1 || index != -1)
                {
                    throw new ArgumentException("Unassigned position must use seat -1 and index -1.");
                }
            }
            else if (TileZoneRules.RequiresSeat(zone))
            {
                if (seat < 0 || seat > 3)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(seat),
                        seat,
                        "Zone " + zone.ToString() + " requires seat 0..3.");
                }

                if (index < 0 || index > TileZoneRules.MaxIndex(zone))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(index),
                        index,
                        "Zone " + zone.ToString() + " slot must be in 0.." +
                        TileZoneRules.MaxIndex(zone).ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            else
            {
                if (seat != -1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(seat),
                        seat,
                        "Zone " + zone.ToString() + " must not have a seat owner.");
                }

                if (index < 0 || index > TileZoneRules.MaxIndex(zone))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(index),
                        index,
                        "Zone " + zone.ToString() + " slot must be in 0.." +
                        TileZoneRules.MaxIndex(zone).ToString(CultureInfo.InvariantCulture) + ".");
                }
            }

            _zone = (byte)zone;
            _seat = (sbyte)seat;
            _index = (short)index;
        }

        public TileZone Zone => (TileZone)_zone;

        public int Seat => Zone == TileZone.Unassigned ? -1 : _seat;

        public int Index => Zone == TileZone.Unassigned ? -1 : _index;

        public bool IsAssigned => Zone != TileZone.Unassigned;

        public static TilePosition Unassigned => default;

        public static TilePosition Wall(int index) => new TilePosition(TileZone.Wall, -1, index);

        public static TilePosition DeadWall(int index) => new TilePosition(TileZone.DeadWall, -1, index);

        public static TilePosition DeadWallReplacement(int index) =>
            new TilePosition(TileZone.DeadWallReplacement, -1, index);

        public static TilePosition Hand(int seat, int index) => new TilePosition(TileZone.Hand, seat, index);

        public static TilePosition Discard(int seat, int index) => new TilePosition(TileZone.Discard, seat, index);

        public static TilePosition Meld(int seat, int index) => new TilePosition(TileZone.Meld, seat, index);

        public static TilePosition Sleeve(int seat, int index) => new TilePosition(TileZone.Sleeve, seat, index);

        public bool Equals(TilePosition other)
        {
            return _zone == other._zone && Seat == other.Seat && Index == other.Index;
        }

        public override bool Equals(object? obj) => obj is TilePosition other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _zone * 397;
                hash = (hash * 397) ^ Seat;
                hash = (hash * 397) ^ Index;
                return hash;
            }
        }

        public override string ToString()
        {
            if (Zone == TileZone.Unassigned)
            {
                return "UNASSIGNED";
            }

            if (TileZoneRules.RequiresSeat(Zone))
            {
                return Zone.ToString().ToUpperInvariant() + ":" +
                       Seat.ToString(CultureInfo.InvariantCulture) + ":" +
                       Index.ToString(CultureInfo.InvariantCulture);
            }

            return Zone.ToString().ToUpperInvariant() + ":" +
                   Index.ToString(CultureInfo.InvariantCulture);
        }

        public static bool operator ==(TilePosition left, TilePosition right) => left.Equals(right);

        public static bool operator !=(TilePosition left, TilePosition right) => !left.Equals(right);
    }

    public readonly struct TilePlacement : IEquatable<TilePlacement>
    {
        public TilePlacement(TileId tile, TilePosition position)
        {
            Tile = tile;
            Position = position;
        }

        public TileId Tile { get; }

        public TilePosition Position { get; }

        public bool Equals(TilePlacement other) => Tile.Equals(other.Tile) && Position.Equals(other.Position);

        public override bool Equals(object? obj) => obj is TilePlacement other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Tile.GetHashCode() * 397) ^ Position.GetHashCode();
            }
        }

        public override string ToString() => Tile.ToString() + "@" + Position.ToString();

        public static bool operator ==(TilePlacement left, TilePlacement right) => left.Equals(right);

        public static bool operator !=(TilePlacement left, TilePlacement right) => !left.Equals(right);
    }

    /// <summary>
    /// Public evidence that a discarded tile is still the one open to calls. It is bound
    /// to both the physical tile and its exact river slot; consuming the call clears it,
    /// so an older discard cannot become "latest" merely because the river slot was removed.
    /// </summary>
    public readonly struct LastDiscardEvidence : IEquatable<LastDiscardEvidence>
    {
        public LastDiscardEvidence(TileId tile, TilePosition position)
        {
            if (position.Zone != TileZone.Discard)
            {
                throw new ArgumentException("Last discard must be in a Discard position.", nameof(position));
            }

            Tile = tile;
            Position = position;
        }

        public TileId Tile { get; }

        public TilePosition Position { get; }

        public int Seat => Position.Seat;

        public bool Equals(LastDiscardEvidence other) => Tile.Equals(other.Tile) && Position.Equals(other.Position);

        public override bool Equals(object? obj) => obj is LastDiscardEvidence other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Tile.GetHashCode() * 397) ^ Position.GetHashCode();
            }
        }

        public override string ToString() => Tile + "@" + Position;

        public static bool operator ==(LastDiscardEvidence left, LastDiscardEvidence right) => left.Equals(right);

        public static bool operator !=(LastDiscardEvidence left, LastDiscardEvidence right) => !left.Equals(right);
    }

    /// <summary>
    /// Evidence that a specific physical tile was just drawn from a rinshan (dead-wall)
    /// slot. It is stored in the immutable map, so a caller cannot substitute an arbitrary
    /// hand tile for the actual kan draw.
    /// </summary>
    public readonly struct RinshanDrawEvidence : IEquatable<RinshanDrawEvidence>
    {
        public RinshanDrawEvidence(TileId tile, int seat, int deadWallIndex)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (deadWallIndex < 0 || deadWallIndex > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deadWallIndex),
                    deadWallIndex,
                    "Rinshan draw slot must be 0..3.");
            }

            Tile = tile;
            Seat = seat;
            DeadWallIndex = deadWallIndex;
        }

        public TileId Tile { get; }

        public int Seat { get; }

        public int DeadWallIndex { get; }

        public bool Equals(RinshanDrawEvidence other)
        {
            return Tile.Equals(other.Tile) && Seat == other.Seat && DeadWallIndex == other.DeadWallIndex;
        }

        public override bool Equals(object? obj) => obj is RinshanDrawEvidence other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Seat;
                hash = (hash * 397) ^ Tile.GetHashCode();
                hash = (hash * 397) ^ DeadWallIndex;
                return hash;
            }
        }

        public override string ToString() => Tile + "@rinshan" + DeadWallIndex + "/seat" + Seat;

        public static bool operator ==(RinshanDrawEvidence left, RinshanDrawEvidence right) => left.Equals(right);

        public static bool operator !=(RinshanDrawEvidence left, RinshanDrawEvidence right) => !left.Equals(right);
    }

    /// <summary>
    /// Evidence for the tile currently held as the seat's drawn tile. It covers normal
    /// draws, rinshan draws and the dealer's 14th tile, and is bound to the immutable map
    /// so BeamSwap cannot treat a drawn tile as a closed hand tile.
    /// </summary>
    public readonly struct DrawnTileEvidence : IEquatable<DrawnTileEvidence>
    {
        public DrawnTileEvidence(TileId tile, int seat, DrawSource source)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (source < DrawSource.Wall || source > DrawSource.DealExtra)
            {
                throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown draw source.");
            }

            Tile = tile;
            Seat = seat;
            Source = source;
        }

        public TileId Tile { get; }

        public int Seat { get; }

        public DrawSource Source { get; }

        public bool Equals(DrawnTileEvidence other)
        {
            return Tile.Equals(other.Tile) && Seat == other.Seat && Source == other.Source;
        }

        public override bool Equals(object? obj) => obj is DrawnTileEvidence other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Seat;
                hash = (hash * 397) ^ Tile.GetHashCode();
                hash = (hash * 397) ^ (int)Source;
                return hash;
            }
        }

        public override string ToString() => Tile + "@drawn/seat" + Seat + "/" + Source;

        public static bool operator ==(DrawnTileEvidence left, DrawnTileEvidence right) => left.Equals(right);

        public static bool operator !=(DrawnTileEvidence left, DrawnTileEvidence right) => !left.Equals(right);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    public enum DrawSource : byte
    {
        Wall = 0,
        Rinshan = 1,
        Reservation = 2,
        DealExtra = 3
    }

    public readonly struct RecordedDraw : IEquatable<RecordedDraw>
    {
        public RecordedDraw(int seat, TileId tile, DrawSource source)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (source < DrawSource.Wall || source > DrawSource.DealExtra)
            {
                throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown draw source.");
            }

            Seat = seat;
            Tile = tile;
            Source = source;
        }

        public int Seat { get; }

        public TileId Tile { get; }

        public DrawSource Source { get; }

        public bool Equals(RecordedDraw other)
        {
            return Seat == other.Seat && Tile.Equals(other.Tile) && Source == other.Source;
        }

        public override bool Equals(object? obj) => obj is RecordedDraw other && Equals(other);

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

        public override string ToString() =>
            "seat=" + Seat + " tile=" + Tile + " source=" + Source;

        public static bool operator ==(RecordedDraw left, RecordedDraw right) => left.Equals(right);

        public static bool operator !=(RecordedDraw left, RecordedDraw right) => !left.Equals(right);
    }

    /// <summary>
    /// Explicit, externally supplied random result plus the draws delivered from it.
    /// Domain never creates a seed, shuffles, reads a clock or generates an id. The wall
    /// order must be a complete 136-tile permutation; draw entries are an immutable log
    /// that can be replayed against the same order.
    /// </summary>
    public sealed class RecordedRandomDraw
    {
        private readonly TileId[] _wallOrder;
        private readonly RecordedDraw[] _draws;
        private readonly ReadOnlyCollection<TileId> _readOnlyWallOrder;
        private readonly ReadOnlyCollection<RecordedDraw> _readOnlyDraws;

        private RecordedRandomDraw(string recordId, TileId[] wallOrder, RecordedDraw[] draws)
        {
            RecordId = recordId;
            _wallOrder = wallOrder;
            _draws = draws;
            _readOnlyWallOrder = Array.AsReadOnly(_wallOrder);
            _readOnlyDraws = Array.AsReadOnly(_draws);
        }

        public string RecordId { get; }

        public IReadOnlyList<TileId> WallOrder => _readOnlyWallOrder;

        public IReadOnlyList<RecordedDraw> Draws => _readOnlyDraws;

        public static RecordedRandomDraw Create(string recordId, IReadOnlyList<TileId> wallOrder)
        {
            if (recordId == null)
            {
                throw new ArgumentNullException(nameof(recordId));
            }

            if (wallOrder == null)
            {
                throw new ArgumentNullException(nameof(wallOrder));
            }

            if (wallOrder.Count != TileId.Count)
            {
                throw new ArgumentException(
                    "Wall order must contain exactly 136 tiles, got " + wallOrder.Count + ".",
                    nameof(wallOrder));
            }

            var copy = new TileId[TileId.Count];
            var seen = new bool[TileId.Count];
            for (int i = 0; i < wallOrder.Count; i++)
            {
                TileId tile = wallOrder[i];
                if (seen[tile.Value])
                {
                    throw new ArgumentException(
                        "Wall order contains duplicate physical tile " + tile + ".",
                        nameof(wallOrder));
                }

                seen[tile.Value] = true;
                copy[i] = tile;
            }

            for (int i = 0; i < seen.Length; i++)
            {
                if (!seen[i])
                {
                    throw new ArgumentException(
                        "Wall order is missing physical tile T" + i + ".",
                        nameof(wallOrder));
                }
            }

            return new RecordedRandomDraw(recordId, copy, Array.Empty<RecordedDraw>());
        }

        /// <summary>Deterministic 0..135 order for tests and non-random fixtures.</summary>
        public static RecordedRandomDraw Standard(string recordId)
        {
            var tiles = new TileId[TileId.Count];
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i] = new TileId(i);
            }

            return Create(recordId, tiles);
        }

        public RecordedRandomDraw AppendDraw(int seat, TileId tile, DrawSource source)
        {
            var entry = new RecordedDraw(seat, tile, source);
            var next = new RecordedDraw[_draws.Length + 1];
            Array.Copy(_draws, next, _draws.Length);
            next[_draws.Length] = entry;
            return new RecordedRandomDraw(RecordId, _wallOrder, next);
        }

        public override string ToString() => RecordId + " (" + _draws.Length + " draws)";
    }
}

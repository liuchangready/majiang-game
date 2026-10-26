using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Mahjong.Domain
{
    /// <summary>
    /// V4 suit order. Numeric type order is Man 0-8, Sou 9-17, Pin 18-26,
    /// Wind 27-30, Dragon 31-33; see <see cref="TileId.TypeIndex"/>.
    /// </summary>
    public enum TileSuit : byte
    {
        Man = 0,
        Pin = 1,
        Sou = 2,
        Wind = 3,
        Dragon = 4
    }

    /// <summary>
    /// Immutable physical tile identity in the range 0..135.
    /// <c>typeIndex = id &gt;&gt; 2</c>, <c>copyIndex = id &amp; 3</c>.
    /// Red fives are the <c>copyIndex == 3</c> physical copy of each number five.
    /// </summary>
    public readonly struct TileId : IEquatable<TileId>, IComparable<TileId>
    {
        public const int MinValue = 0;
        public const int MaxValue = 135;
        public const int Count = 136;
        public const int TypeCount = 34;

        private static readonly TileId[] AllTiles = CreateAllCore();
        private static readonly ReadOnlyCollection<TileId> AllTilesReadOnly = Array.AsReadOnly(AllTiles);

        private readonly byte _value;

        public TileId(int value)
        {
            if (value < MinValue || value > MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "TileId must be in 0..135.");
            }

            _value = (byte)value;
        }

        public int Value => _value;

        /// <summary>34-dimensional logical type index: Man 0-8, Sou 9-17, Pin 18-26, Wind 27-30, Dragon 31-33.</summary>
        public int TypeIndex => _value >> 2;

        /// <summary>Physical copy index 0..3. Copy 3 of a number five is the red five.</summary>
        public int CopyIndex => _value & 3;

        public TileSuit Suit
        {
            get
            {
                int typeIndex = TypeIndex;
                if (typeIndex < 9)
                {
                    return TileSuit.Man;
                }

                if (typeIndex < 18)
                {
                    return TileSuit.Sou;
                }

                if (typeIndex < 27)
                {
                    return TileSuit.Pin;
                }

                return typeIndex < 31 ? TileSuit.Wind : TileSuit.Dragon;
            }
        }

        /// <summary>Number rank 1..9, wind rank 1..4, dragon rank 1..3 (white, green, red).</summary>
        public int Rank
        {
            get
            {
                int typeIndex = TypeIndex;
                if (typeIndex < 27)
                {
                    return (typeIndex % 9) + 1;
                }

                return typeIndex < 31 ? (typeIndex - 27) + 1 : (typeIndex - 31) + 1;
            }
        }

        public bool IsNumberTile => TypeIndex < 27;

        public bool IsHonor => TypeIndex >= 27;

        public bool IsTerminal => IsNumberTile && (Rank == 1 || Rank == 9);

        public bool IsTerminalOrHonor => IsTerminal || IsHonor;

        public bool IsSimple => !IsTerminalOrHonor;

        /// <summary>Exactly three physical tiles in the standard set: Man5c3, Sou5c3 and Pin5c3.</summary>
        public bool IsRedFive => CopyIndex == 3 && IsNumberTile && Rank == 5;

        public static IReadOnlyList<TileId> All => AllTilesReadOnly;

        public static TileId FromTypeIndex(int typeIndex, int copyIndex)
        {
            if (typeIndex < 0 || typeIndex >= TypeCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(typeIndex),
                    typeIndex,
                    "typeIndex must be in 0..33.");
            }

            if (copyIndex < 0 || copyIndex > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(copyIndex),
                    copyIndex,
                    "copyIndex must be in 0..3.");
            }

            return new TileId((typeIndex * 4) + copyIndex);
        }

        public static bool TryFromTypeIndex(int typeIndex, int copyIndex, out TileId tile)
        {
            if (typeIndex < 0 || typeIndex >= TypeCount || copyIndex < 0 || copyIndex > 3)
            {
                tile = default;
                return false;
            }

            tile = new TileId((typeIndex * 4) + copyIndex);
            return true;
        }

        public static bool TryFromValue(int value, out TileId tile)
        {
            if (value < MinValue || value > MaxValue)
            {
                tile = default;
                return false;
            }

            tile = new TileId(value);
            return true;
        }

        public bool SameType(TileId other) => TypeIndex == other.TypeIndex;

        public int CompareTo(TileId other) => _value.CompareTo(other._value);

        public bool Equals(TileId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is TileId other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => "T" + _value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(TileId left, TileId right) => left.Equals(right);

        public static bool operator !=(TileId left, TileId right) => !left.Equals(right);

        public static bool operator <(TileId left, TileId right) => left._value < right._value;

        public static bool operator >(TileId left, TileId right) => left._value > right._value;

        public static bool operator <=(TileId left, TileId right) => left._value <= right._value;

        public static bool operator >=(TileId left, TileId right) => left._value >= right._value;

        private static TileId[] CreateAllCore()
        {
            var tiles = new TileId[Count];
            for (int i = 0; i < Count; i++)
            {
                tiles[i] = new TileId(i);
            }

            return tiles;
        }
    }
}

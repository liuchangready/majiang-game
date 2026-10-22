using System;
using System.Globalization;

namespace Mahjong.Domain
{
    /// <summary>
    /// Deterministic field encoding of the physical position map.
    ///
    /// Encoding version 3 is a canonical byte stream:
    ///   version tag; for tile id 0..135 a tagged record (zone, seat+1, index+1);
    ///   tagged wall pointer, wall end, rinshan pointer and replacement count;
    ///   tagged last-discard evidence, revealed dora indicators, kan count,
    ///   drawn-tile evidence and rinshan-draw evidence.
    /// The stream is hashed with 64-bit FNV-1a. Tile-id order makes the result independent
    /// of how the placement map was built; no strings, clocks, random values or floating
    /// point values participate. Revision is intentionally excluded: it is history, not
    /// physical state.
    /// </summary>
    public static class TileStateHash
    {
        public const int EncodingVersion = 3;

        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Compute(TilePositionMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            ulong hash = OffsetBasis;
            AddByte(ref hash, EncodingVersion);

            for (int tileId = 0; tileId < TileId.Count; tileId++)
            {
                TilePosition position = map[tileId];
                AddByte(ref hash, 0x10);
                AddByte(ref hash, (byte)tileId);
                AddByte(ref hash, (byte)position.Zone);
                AddByte(ref hash, unchecked((byte)(position.Seat + 1)));
                AddUInt16(ref hash, unchecked((ushort)(position.Index + 1)));
            }

            AddByte(ref hash, 0x20);
            AddInt32(ref hash, map.WallPointer);
            AddByte(ref hash, 0x21);
            AddInt32(ref hash, map.WallEndExclusive);
            AddByte(ref hash, 0x22);
            AddInt32(ref hash, map.RinshanPointer);
            AddByte(ref hash, 0x23);
            AddInt32(ref hash, map.ReplacementCount);

            AddByte(ref hash, 0x24);
            LastDiscardEvidence? lastDiscard = map.LastDiscard;
            if (lastDiscard == null)
            {
                AddByte(ref hash, 0);
            }
            else
            {
                AddByte(ref hash, 1);
                LastDiscardEvidence evidence = lastDiscard.Value;
                AddByte(ref hash, (byte)evidence.Tile.Value);
                AddByte(ref hash, (byte)evidence.Seat);
                AddByte(ref hash, (byte)evidence.Position.Zone);
                AddByte(ref hash, unchecked((byte)(evidence.Position.Seat + 1)));
                AddUInt16(ref hash, unchecked((ushort)(evidence.Position.Index + 1)));
            }

            AddByte(ref hash, 0x25);
            AddInt32(ref hash, map.DoraIndicators.Count);
            for (int i = 0; i < map.DoraIndicators.Count; i++)
            {
                AddByte(ref hash, (byte)map.DoraIndicators[i].Value);
            }

            AddByte(ref hash, 0x27);
            AddInt32(ref hash, map.KanCount);

            AddByte(ref hash, 0x28);
            DrawnTileEvidence? drawnTile = map.DrawnTile;
            if (drawnTile == null)
            {
                AddByte(ref hash, 0);
            }
            else
            {
                AddByte(ref hash, 1);
                DrawnTileEvidence evidence = drawnTile.Value;
                AddByte(ref hash, (byte)evidence.Tile.Value);
                AddByte(ref hash, (byte)evidence.Seat);
                AddByte(ref hash, (byte)evidence.Source);
            }

            AddByte(ref hash, 0x26);
            RinshanDrawEvidence? rinshanDraw = map.LastRinshanDraw;
            if (rinshanDraw == null)
            {
                AddByte(ref hash, 0);
            }
            else
            {
                AddByte(ref hash, 1);
                RinshanDrawEvidence evidence = rinshanDraw.Value;
                AddByte(ref hash, (byte)evidence.Tile.Value);
                AddByte(ref hash, (byte)evidence.Seat);
                AddByte(ref hash, (byte)evidence.DeadWallIndex);
            }

            return hash;
        }

        public static string Format(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

        private static void AddByte(ref ulong hash, byte value)
        {
            unchecked
            {
                hash ^= value;
                hash *= Prime;
            }
        }

        private static void AddUInt16(ref ulong hash, ushort value)
        {
            AddByte(ref hash, (byte)(value >> 8));
            AddByte(ref hash, (byte)value);
        }

        private static void AddInt32(ref ulong hash, int value)
        {
            unchecked
            {
                uint bits = (uint)value;
                AddByte(ref hash, (byte)(bits >> 24));
                AddByte(ref hash, (byte)(bits >> 16));
                AddByte(ref hash, (byte)(bits >> 8));
                AddByte(ref hash, (byte)bits);
            }
        }
    }
}

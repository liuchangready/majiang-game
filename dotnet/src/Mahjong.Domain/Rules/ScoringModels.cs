using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    /// <summary>V4 meld kinds. Numeric order is irrelevant; the names are the frozen wire/domain codes.</summary>
    public enum MeldType
    {
        Chi,
        Pon,
        OpenKan,
        ClosedKan,
        AddedKan
    }

    /// <summary>
    /// Immutable meld. Physical tile identities are preserved; a meld never invents a copy.
    /// </summary>
    public sealed class Meld
    {
        public Meld(MeldType type, IEnumerable<TileId> tiles, int fromPlayer, TileId calledTile)
        {
            if (tiles == null)
            {
                throw new ArgumentNullException(nameof(tiles));
            }

            TileId[] materialized = tiles.ToArray();
            int expected = type == MeldType.Chi || type == MeldType.Pon ? 3 : 4;
            if (materialized.Length != expected)
            {
                throw new ArgumentException(
                    "Meld " + type + " requires " + expected + " tiles, got " + materialized.Length + ".",
                    nameof(tiles));
            }

            if (materialized.Distinct().Count() != materialized.Length)
            {
                throw new ArgumentException("Meld contains a duplicate physical tile.", nameof(tiles));
            }

            if (type == MeldType.Chi)
            {
                int[] indices = materialized.Select(t => t.TypeIndex).OrderBy(t => t).ToArray();
                if (indices[1] != indices[0] + 1 || indices[2] != indices[1] + 1 || indices[2] / 9 != indices[0] / 9)
                {
                    throw new ArgumentException("Chi must be three consecutive tiles in one suit.", nameof(tiles));
                }
            }
            else
            {
                int typeIndex = materialized[0].TypeIndex;
                if (materialized.Any(t => t.TypeIndex != typeIndex))
                {
                    throw new ArgumentException("Pon/kan tiles must share one tile type.", nameof(tiles));
                }
            }

            Type = type;
            Tiles = Array.AsReadOnly(materialized);
            FromPlayer = fromPlayer;
            CalledTile = calledTile;
        }

        public MeldType Type { get; }

        public IReadOnlyList<TileId> Tiles { get; }

        public int FromPlayer { get; }

        public TileId CalledTile { get; }

        /// <summary>A closed kan is concealed for yaku/fu purposes; every other meld is open.</summary>
        public bool IsOpen => Type != MeldType.ClosedKan;

        public bool IsKan => Type == MeldType.OpenKan || Type == MeldType.ClosedKan || Type == MeldType.AddedKan;

        public bool IsSequence => Type == MeldType.Chi;

        public bool IsTriplet => Type == MeldType.Pon;
    }

    /// <summary>
    /// Immutable hand view used by scoring. <see cref="AllClosedTiles"/> is closed tiles plus the
    /// optional drawn tile, matching V4 <c>PlayerHand.allClosedTiles</c>.
    /// </summary>
    public sealed class PlayerHand
    {
        private readonly TileId[] _closedTiles;
        private readonly Meld[] _melds;

        public PlayerHand(IEnumerable<TileId> closedTiles, IEnumerable<Meld>? melds = null, TileId? drawnTile = null)
        {
            if (closedTiles == null)
            {
                throw new ArgumentNullException(nameof(closedTiles));
            }

            _closedTiles = closedTiles.ToArray();
            _melds = melds == null ? Array.Empty<Meld>() : melds.ToArray();
            DrawnTile = drawnTile;
            ValidatePhysicalUniqueness();
        }

        public IReadOnlyList<TileId> ClosedTiles => Array.AsReadOnly(_closedTiles);

        public IReadOnlyList<Meld> Melds => Array.AsReadOnly(_melds);

        public TileId? DrawnTile { get; }

        public IReadOnlyList<TileId> AllClosedTiles
        {
            get
            {
                if (!DrawnTile.HasValue)
                {
                    return Array.AsReadOnly(_closedTiles);
                }

                var all = new TileId[_closedTiles.Length + 1];
                Array.Copy(_closedTiles, all, _closedTiles.Length);
                all[all.Length - 1] = DrawnTile.Value;
                return Array.AsReadOnly(all);
            }
        }

        public bool IsClosed => _melds.All(m => !m.IsOpen);

        public int TileCount => _closedTiles.Length + (_melds.Length * 3) + (DrawnTile.HasValue ? 1 : 0);

        public PlayerHand WithDrawnTile(TileId tile)
        {
            return new PlayerHand(_closedTiles, _melds, tile);
        }

        private void ValidatePhysicalUniqueness()
        {
            var seen = new HashSet<int>();
            foreach (TileId tile in _closedTiles)
            {
                if (!seen.Add(tile.Value))
                {
                    throw new ArgumentException("Hand contains duplicate physical tile " + tile.Value + ".");
                }
            }

            if (DrawnTile.HasValue && !seen.Add(DrawnTile.Value.Value))
            {
                throw new ArgumentException("Drawn tile duplicates a closed tile: " + DrawnTile.Value.Value + ".");
            }

            foreach (Meld meld in _melds)
            {
                foreach (TileId tile in meld.Tiles)
                {
                    if (!seen.Add(tile.Value))
                    {
                        throw new ArgumentException("Meld duplicates physical tile " + tile.Value + ".");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Win context. Wind values follow V4: 1=East, 2=South, 3=West, 4=North.
    /// </summary>
    public sealed class WinContext
    {
        public WinContext(
            TileId winTile,
            bool isTsumo,
            bool isClosed,
            int seatWind,
            int roundWind,
            bool isRiichi = false,
            bool isDoubleRiichi = false,
            bool isIppatsu = false,
            bool isRinshan = false,
            bool isHaitei = false,
            bool isHoutei = false,
            bool isChankan = false,
            bool isTenhou = false,
            bool isChiihou = false)
        {
            if (seatWind < 1 || seatWind > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(seatWind), seatWind, "seatWind must be 1..4.");
            }

            if (roundWind < 1 || roundWind > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(roundWind), roundWind, "roundWind must be 1..4.");
            }

            WinTile = winTile;
            IsTsumo = isTsumo;
            IsClosed = isClosed;
            SeatWind = seatWind;
            RoundWind = roundWind;
            IsRiichi = isRiichi;
            IsDoubleRiichi = isDoubleRiichi;
            IsIppatsu = isIppatsu;
            IsRinshan = isRinshan;
            IsHaitei = isHaitei;
            IsHoutei = isHoutei;
            IsChankan = isChankan;
            IsTenhou = isTenhou;
            IsChiihou = isChiihou;
        }

        public TileId WinTile { get; }

        public bool IsTsumo { get; }

        public bool IsClosed { get; }

        public int SeatWind { get; }

        public int RoundWind { get; }

        public bool IsRiichi { get; }

        public bool IsDoubleRiichi { get; }

        public bool IsIppatsu { get; }

        public bool IsRinshan { get; }

        public bool IsHaitei { get; }

        public bool IsHoutei { get; }

        public bool IsChankan { get; }

        public bool IsTenhou { get; }

        public bool IsChiihou { get; }
    }

    internal static class TileFacts
    {
        /// <summary>V4 dora indicator cycle, expressed on the 34 type indices.</summary>
        public static int DoraIndicatorTarget(int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= TileId.TypeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(typeIndex), typeIndex, "typeIndex must be 0..33.");
            }

            if (typeIndex < 27)
            {
                int suitBase = (typeIndex / 9) * 9;
                return suitBase + ((typeIndex - suitBase + 1) % 9);
            }

            if (typeIndex < 31)
            {
                return 27 + ((typeIndex - 27 + 1) % 4);
            }

            return 31 + ((typeIndex - 31 + 1) % 3);
        }

        /// <summary>V4 green set: Sou 2,3,4,6,8 plus Green Dragon.</summary>
        public static bool IsGreen(int typeIndex)
        {
            if (typeIndex == 32)
            {
                return true;
            }

            if (typeIndex < 9 || typeIndex > 17)
            {
                return false;
            }

            int rank = (typeIndex - 9) + 1;
            return rank == 2 || rank == 3 || rank == 4 || rank == 6 || rank == 8;
        }

        public static bool IsTerminal(int typeIndex)
        {
            return typeIndex >= 0 && typeIndex < 27 && (((typeIndex % 9) == 0) || ((typeIndex % 9) == 8));
        }

        public static bool IsTerminalOrHonor(int typeIndex)
        {
            return IsTerminal(typeIndex) || typeIndex >= 27;
        }
    }
}

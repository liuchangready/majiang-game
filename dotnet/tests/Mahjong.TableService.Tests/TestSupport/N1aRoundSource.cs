using System;
using System.Collections.Generic;
using Mahjong.Domain;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Deterministic N-1a dealt-round helper (the same construction shape the accepted
    /// Domain/TableRuntime fixtures use): explicit per-seat closed hands, one optional drawn
    /// tile, dead-wall and live-wall prefixes; every other physical tile is a deterministic
    /// filler. Rounds are created through <see cref="RoundStateMachine.CreateDealt"/>, so the
    /// Domain's own round constructor — not any fabricated state — owns the initial truth.
    /// </summary>
    public sealed class N1aTable
    {
        private readonly Dictionary<int, List<TileId>> _hands = new Dictionary<int, List<TileId>>();
        private readonly List<TileId> _deadWall = new List<TileId>();
        private readonly List<TileId> _wallPrefix = new List<TileId>();
        private int _drawnSeat = -1;
        private TileId? _drawn;
        private int _lastDiscardSeat = -1;
        private TileId? _lastDiscard;

        public int Dealer { get; set; }

        public N1aTable Hand(int seat, params TileId[] tiles)
        {
            if (!_hands.TryGetValue(seat, out List<TileId>? list))
            {
                list = new List<TileId>();
                _hands.Add(seat, list);
            }

            list.AddRange(tiles);
            return this;
        }

        public N1aTable Drawn(int seat, TileId tile)
        {
            _drawnSeat = seat;
            _drawn = tile;
            return this;
        }

        public N1aTable LastDiscard(int seat, TileId tile)
        {
            _lastDiscardSeat = seat;
            _lastDiscard = tile;
            return this;
        }

        public N1aTable DeadWall(params TileId[] tiles)
        {
            _deadWall.AddRange(tiles);
            return this;
        }

        public N1aTable WallPrefix(params TileId[] tiles)
        {
            _wallPrefix.AddRange(tiles);
            return this;
        }

        public RoundStateMachine Build(
            string roundId,
            RoundPhase phase = RoundPhase.CheatWindow,
            long[]? scores = null,
            int sticks = 0)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            var placed = new bool[TileId.Count];

            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value])
                {
                    throw new InvalidOperationException("tile " + tile + " specified twice");
                }

                used[tile.Value] = true;
                placed[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            var drawnTiles = new TileId[4];
            var hasDrawn = new bool[4];
            if (_drawn.HasValue)
            {
                drawnTiles[_drawnSeat] = _drawn.Value;
                hasDrawn[_drawnSeat] = true;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!_hands.TryGetValue(seat, out List<TileId>? hand))
                {
                    hand = new List<TileId>();
                }

                if (hand.Count > 13)
                {
                    throw new InvalidOperationException("seat " + seat + " specifies more than 13 closed tiles");
                }

                for (int i = 0; i < hand.Count; i++)
                {
                    Place(hand[i], TilePosition.Hand(seat, i));
                }
            }

            // Reserve drawn tiles before filling closed hands so the filler cannot consume them.
            for (int seat = 0; seat < 4; seat++)
            {
                if (!hasDrawn[seat])
                {
                    continue;
                }

                TileId tile = drawnTiles[seat];
                if (used[tile.Value])
                {
                    throw new InvalidOperationException("drawn tile " + tile + " is already placed");
                }

                used[tile.Value] = true;
            }

            if (_lastDiscard.HasValue)
            {
                Place(_lastDiscard.Value, TilePosition.Discard(_lastDiscardSeat, 0));
            }

            for (int i = 0; i < _deadWall.Count; i++)
            {
                Place(_deadWall[i], TilePosition.DeadWall(i));
            }

            for (int i = 0; i < _wallPrefix.Count; i++)
            {
                Place(_wallPrefix[i], TilePosition.Wall(i));
            }

            var cursor = 0;
            TileId NextUnused()
            {
                while (cursor < TileId.Count && used[cursor])
                {
                    cursor++;
                }

                if (cursor >= TileId.Count)
                {
                    throw new InvalidOperationException("fixture ran out of physical tiles");
                }

                used[cursor] = true;
                return new TileId(cursor);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                _hands.TryGetValue(seat, out List<TileId>? hand);
                int closed = hand?.Count ?? 0;
                for (int i = closed; i < 13; i++)
                {
                    TileId tile = NextUnused();
                    placements[tile.Value] = new TilePlacement(tile, TilePosition.Hand(seat, i));
                    placed[tile.Value] = true;
                }

                if (hasDrawn[seat])
                {
                    TileId tile = drawnTiles[seat];
                    placements[tile.Value] = new TilePlacement(tile, TilePosition.Hand(seat, 13));
                    placed[tile.Value] = true;
                }
            }

            for (int i = _deadWall.Count; i < 14; i++)
            {
                TileId tile = NextUnused();
                placements[tile.Value] = new TilePlacement(tile, TilePosition.DeadWall(i));
                placed[tile.Value] = true;
            }

            int wallStart = _wallPrefix.Count;
            int wallCount = 0;
            for (int i = 0; i < TileId.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                if (wallStart + wallCount >= TilePositionMap.LiveWallSlotCount)
                {
                    throw new InvalidOperationException("wall fixture exceeds 122 live slots");
                }

                placements[i] = new TilePlacement(new TileId(i), TilePosition.Wall(wallStart + wallCount));
                placed[i] = true;
                wallCount++;
            }

            for (int i = 0; i < TileId.Count; i++)
            {
                if (!placed[i])
                {
                    throw new InvalidOperationException("fixture left tile " + i + " unplaced");
                }
            }

            TileId dora = placements.Length > 0
                ? new TileId(Array.FindIndex(placements, p => p.Position == TilePosition.DeadWall(4)))
                : default;
            LastDiscardEvidence? lastDiscard = _lastDiscard.HasValue
                ? new LastDiscardEvidence(
                    _lastDiscard.Value,
                    TilePosition.Discard(_lastDiscardSeat, 0))
                : (LastDiscardEvidence?)null;
            DrawnTileEvidence? drawn = _drawn.HasValue
                ? new DrawnTileEvidence(_drawn.Value, _drawnSeat, DrawSource.Wall)
                : (DrawnTileEvidence?)null;

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                wallPointer: 0,
                wallEndExclusive: wallStart + wallCount,
                rinshanPointer: 0,
                lastDiscard: lastDiscard,
                revision: 0,
                rinshanDraw: null,
                doraIndicators: new[] { dora },
                kanCount: 0,
                drawnTile: drawn);

            var spec = new RoundSpec(
                roundId,
                Dealer,
                1,
                0,
                0,
                scores ?? new long[] { 25000, 25000, 25000, 25000 },
                sticks);
            return RoundStateMachine.CreateDealt(spec, map, phase);
        }
    }

    /// <summary>Test-only room source: every room id maps to one deterministic real-domain round.</summary>
    public sealed class FixedRoundSource : ITableRoundSource
    {
        private readonly Func<string, RoundStateMachine> _make;

        public FixedRoundSource(Func<string, RoundStateMachine> make)
        {
            _make = make;
        }

        public RoundStateMachine CreateRound(string roomId)
        {
            return _make(roomId);
        }
    }
}

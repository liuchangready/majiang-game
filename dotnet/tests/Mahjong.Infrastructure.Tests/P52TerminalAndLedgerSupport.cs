using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.Infrastructure.Tests
{
    /// <summary>
    /// Deterministic physical placements plus the explicit public/private/event facts needed by
    /// a real terminal settlement and a non-empty cheat-ledger checkpoint. These are test data,
    /// not a replacement for Domain rules or a saved whole candidate state.
    /// </summary>
    internal static class P52TerminalFixture
    {
        internal static RoundStateMachine ExhaustiveDrawReady()
        {
            var plan = new SettlementPlan(RoundPhase.Draw) { WallTiles = 0 };
            plan.AddDora(39);
            plan.AddHand(0,
                Tile(1, 0), Tile(2, 0), Tile(3, 0), Tile(2, 1), Tile(3, 1), Tile(4, 1),
                Tile(5, 0), Tile(6, 0), Tile(7, 0), Tile(19, 0), Tile(20, 0), Tile(21, 0),
                Tile(22, 0));
            plan.AddHand(1,
                Tile(10, 0), Tile(11, 0), Tile(12, 0), Tile(11, 1), Tile(12, 1), Tile(13, 1),
                Tile(14, 0), Tile(15, 0), Tile(16, 0), Tile(1, 1), Tile(2, 2), Tile(3, 2),
                Tile(4, 2));
            plan.AddHand(2,
                Tile(19, 1), Tile(20, 1), Tile(21, 1), Tile(20, 2), Tile(21, 2), Tile(22, 2),
                Tile(23, 0), Tile(24, 0), Tile(25, 0), Tile(10, 1), Tile(11, 2), Tile(12, 2),
                Tile(13, 2));
            plan.AddHand(3,
                Tile(0, 0), Tile(3, 3), Tile(6, 2), Tile(18, 0), Tile(21, 3), Tile(24, 3),
                Tile(9, 0), Tile(12, 3), Tile(15, 3), Tile(27, 0), Tile(28, 0), Tile(29, 0),
                Tile(30, 0));
            return plan.Build(new RoundSpec(
                "round-p52-terminal",
                0,
                1,
                0,
                0,
                new long[] { 25000L, 25000L, 25000L, 25000L },
                0));
        }

        internal static RoundCommand ExhaustiveDrawCommand(RoundStateMachine round)
        {
            var settlement = new RoundSettlement(
                "p52-terminal-result",
                RoundOutcome.ExhaustiveDraw(Array.Empty<int>()),
                new long[] { 25000L, 25000L, 25000L, 25000L },
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            return RoundCommand.ExhaustiveDraw(round.RoundId, settlement);
        }

        internal static CheatState NonEmptyCheatState()
        {
            var economy = new CheatEconomyConfig(
                true,
                40,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [CheatEconomyConfig.TileMarkCostKey] = 5,
                    [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                    [CheatEconomyConfig.BeamSwapCostKey] = 10
                });
            CheatLedger ledger = CheatLedger.Initialize(economy);
            CheatLedgerApplyResult applied = ledger.Apply("p52-ledger-1", 0, -10, "P52_TEST");
            if (!applied.Success || applied.Ledger == null)
            {
                throw new InvalidOperationException("The P5.2 ledger fixture entry was rejected.");
            }

            return new CheatState(
                applied.Ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
        }

        internal static byte[] BuildFacts(
            RoundStateMachine round,
            StateVersion stateVersion,
            CheatState? cheat,
            IReadOnlyList<TableEventRecord> history)
        {
            var publicFacts = new List<TableSeatPublicFacts>(4);
            var privateFacts = new List<TableSeatPrivateFacts>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                var discards = new List<DiscardView>(river.Count);
                var historyTiles = new List<uint>(river.Count);
                for (int i = 0; i < river.Count; i++)
                {
                    discards.Add(new DiscardView(unchecked((uint)river[i].Value), unchecked((uint)i), false, false));
                    historyTiles.Add(unchecked((uint)river[i].Value));
                }

                publicFacts.Add(new TableSeatPublicFacts(seat, false, Array.Empty<TableMeldFact>(), discards));
                privateFacts.Add(new TableSeatPrivateFacts(seat, false, false, historyTiles));
            }

            return TableProjectionFactsCodec.Encode(new TableProjectionContextData(
                round.RoundId,
                stateVersion,
                0UL,
                cheat,
                publicFacts,
                privateFacts,
                null,
                new TableProjectionPolicy(),
                BuildEventFacts(round, history),
                null));
        }

        internal static TableCheckpoint BuildCheckpoint(
            string roomId,
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            CheatState cheat,
            IReadOnlyList<TableEventRecord>? history,
            IReadOnlyList<TableCommandBinding>? bindings,
            TableTimingCheckpoint timing,
            long timingVersion,
            ulong createdUtcMs,
            string bootId)
        {
            return TableCheckpointFactory.Create(
                roomId,
                round,
                stateVersion,
                lastEventSeq,
                P52Fixture.Control(),
                timing,
                1,
                bootId,
                createdUtcMs,
                cheat: TableCheatLedgerCheckpoint.FromCheatState(cheat),
                committedFactsPayload: BuildFacts(round, stateVersion, cheat, history ?? Array.Empty<TableEventRecord>()),
                timingVersion: timingVersion,
                capturedMonotonicMillis: timing.CapturedMonotonicMillis,
                committedHistory: history,
                committedCommandBindings: bindings);
        }

        private static IReadOnlyList<TableProjectionEventFact> BuildEventFacts(
            RoundStateMachine finalRound,
            IReadOnlyList<TableEventRecord> history)
        {
            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = finalRound.Tiles.WallRemaining;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                TableEventRecord record = history[i];
                if (record.Type == RoundEventType.TileDrawn)
                {
                    if (wall < 0)
                    {
                        throw new InvalidOperationException("The fixture wall underflowed while building facts.");
                    }

                    wallBySeq[record.Seq.Value] = unchecked((uint)wall);
                    wall++;
                }
            }

            var facts = new List<TableProjectionEventFact>(history.Count);
            RoundResult? result = finalRound.Result;
            for (int i = 0; i < history.Count; i++)
            {
                TableEventRecord record = history[i];
                TableProjectionEventFacts? value;
                switch (record.Type)
                {
                    case RoundEventType.TilesDealt:
                    case RoundEventType.DoraRevealed:
                    case RoundEventType.RinshanDrawn:
                        value = new TableProjectionEventFacts();
                        break;
                    case RoundEventType.TileDrawn:
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)record.Ordinal),
                            wallRemaining: wallBySeq[record.Seq.Value]);
                        break;
                    case RoundEventType.TileDiscarded:
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)record.Ordinal),
                            riichiDiscard: false,
                            tsumogiri: false);
                        break;
                    case RoundEventType.ExhaustiveDraw:
                    case RoundEventType.AbortiveDraw:
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)record.Ordinal),
                            tenpaiPlayers: result?.Outcome.TenpaiSeats ?? Array.Empty<int>());
                        break;
                    case RoundEventType.RoundEnded:
                        value = new TableProjectionEventFacts(
                            turnNumber: unchecked((uint)record.Ordinal),
                            roundWind: unchecked((uint)finalRound.RoundWind),
                            roundNumber: unchecked((uint)finalRound.RoundNumber));
                        break;
                    case RoundEventType.TsumoWin:
                    case RoundEventType.RonWin:
                        value = new TableProjectionEventFacts(
                            loserSeat: result?.Outcome.LoserSeat,
                            yaku: Array.Empty<YakuView>(),
                            han: 1U,
                            fu: 30U,
                            score: result?.Outcome.WinnerSeat >= 0 ? 26000L : (long?)null);
                        break;
                    default:
                        value = null;
                        break;
                }

                if (value != null)
                {
                    facts.Add(new TableProjectionEventFact(
                        MapType(record.Type),
                        record.Seq,
                        record.CommandVersion,
                        unchecked((uint)record.Ordinal),
                        value));
                }
            }

            return facts;
        }

        private static ProtocolEventType MapType(RoundEventType type)
        {
            switch (type)
            {
                case RoundEventType.TilesDealt: return ProtocolEventType.DealingComplete;
                case RoundEventType.DoraRevealed: return ProtocolEventType.DoraRevealed;
                case RoundEventType.TileDrawn: return ProtocolEventType.TileDrawn;
                case RoundEventType.RinshanDrawn: return ProtocolEventType.RinshanDraw;
                case RoundEventType.TileDiscarded: return ProtocolEventType.TileDiscarded;
                case RoundEventType.ExhaustiveDraw:
                case RoundEventType.AbortiveDraw: return ProtocolEventType.DrawGame;
                case RoundEventType.RoundEnded: return ProtocolEventType.RoundEnded;
                case RoundEventType.TsumoWin: return ProtocolEventType.TsumoWin;
                case RoundEventType.RonWin: return ProtocolEventType.RonWin;
                default:
                    throw new InvalidOperationException("No protocol event type for " + type + ".");
            }
        }

        private static TileId Tile(int typeIndex, int copyIndex) =>
            TileId.FromTypeIndex(typeIndex, copyIndex);

        private sealed class SettlementPlan
        {
            private readonly List<TilePlacement> _placements = new List<TilePlacement>();
            private readonly HashSet<int> _usedTiles = new HashSet<int>();
            private readonly HashSet<int> _usedPositions = new HashSet<int>();
            private readonly int[] _nextDiscard = new int[4];
            private readonly List<TileId>[] _hands =
            {
                new List<TileId>(), new List<TileId>(), new List<TileId>(), new List<TileId>()
            };
            private readonly List<TileId> _dora = new List<TileId>();
            private readonly List<TileId> _wallTop = new List<TileId>();
            private readonly List<(int Seat, TileId Tile)> _discards = new List<(int Seat, TileId Tile)>();

            internal SettlementPlan(RoundPhase phase)
            {
                Phase = phase;
            }

            internal RoundPhase Phase { get; }

            internal int WallTiles { get; set; }

            internal bool FillRemainingIntoWall { get; set; }

            internal void AddHand(int seat, params TileId[] tiles) => _hands[seat].AddRange(tiles);

            internal void AddDora(params int[] tileValues)
            {
                foreach (int value in tileValues)
                {
                    _dora.Add(new TileId(value));
                }
            }

            internal void AddWallTop(params int[] tileValues)
            {
                foreach (int value in tileValues)
                {
                    _wallTop.Add(new TileId(value));
                }
            }

            internal void AddDiscard(int seat, int typeIndex, int copyIndex) =>
                _discards.Add((seat, TileId.FromTypeIndex(typeIndex, copyIndex)));

            internal RoundStateMachine Build(RoundSpec spec) =>
                RoundStateMachine.CreateDealt(spec, BuildMap(), Phase);

            private TilePositionMap BuildMap()
            {
                if (WallTiles < 0 || WallTiles > 122)
                {
                    throw new InvalidOperationException("WallTiles must be 0..122.");
                }

                for (int seat = 0; seat < 4; seat++)
                {
                    for (int i = 0; i < _hands[seat].Count; i++)
                    {
                        Place(_hands[seat][i], TilePosition.Hand(seat, i));
                    }
                }

                foreach ((int seat, TileId tile) in _discards)
                {
                    Place(tile, TilePosition.Discard(seat, _nextDiscard[seat]++));
                }

                for (int i = 0; i < _dora.Count; i++)
                {
                    Place(_dora[i], TilePosition.DeadWall(4 + (2 * i)));
                }

                for (int i = 0; i < _wallTop.Count; i++)
                {
                    Place(_wallTop[i], TilePosition.Wall(i));
                }

                for (int slot = 0; slot < 14; slot++)
                {
                    TilePosition position = TilePosition.DeadWall(slot);
                    if (!_usedPositions.Contains(PositionKey(position)))
                    {
                        Place(NextUnused(), position);
                    }
                }

                if (FillRemainingIntoWall)
                {
                    int remaining = TileId.Count - _usedTiles.Count;
                    WallTiles = Math.Min(122, _wallTop.Count + remaining);
                }

                for (int i = _wallTop.Count; i < WallTiles; i++)
                {
                    Place(NextUnused(), TilePosition.Wall(i));
                }

                while (true)
                {
                    int value = NextUnusedValue();
                    if (value < 0)
                    {
                        break;
                    }

                    int seat = value % 4;
                    Place(new TileId(value), TilePosition.Discard(seat, _nextDiscard[seat]++));
                }

                return TilePositionMap.FromPlacements(
                    _placements,
                    0,
                    WallTiles,
                    0,
                    null,
                    0,
                    null,
                    _dora.ToArray(),
                    0,
                    null);
            }

            private void Place(TileId tile, TilePosition position)
            {
                if (!_usedTiles.Add(tile.Value))
                {
                    throw new InvalidOperationException("Physical tile " + tile + " placed twice.");
                }

                int key = PositionKey(position);
                if (!_usedPositions.Add(key))
                {
                    throw new InvalidOperationException("Position " + position + " placed twice.");
                }

                _placements.Add(new TilePlacement(tile, position));
            }

            private TileId NextUnused()
            {
                int value = NextUnusedValue();
                if (value < 0)
                {
                    throw new InvalidOperationException("No unused physical tile remains.");
                }

                return new TileId(value);
            }

            private int NextUnusedValue()
            {
                for (int value = 0; value < TileId.Count; value++)
                {
                    if (!_usedTiles.Contains(value))
                    {
                        return value;
                    }
                }

                return -1;
            }

            private static int PositionKey(TilePosition position) =>
                (((int)position.Zone * 4 + (position.Seat + 1)) * 256) + position.Index;
        }
    }
}

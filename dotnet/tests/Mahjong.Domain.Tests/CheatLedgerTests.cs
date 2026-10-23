using System;
using System.Collections.Generic;
using System.Text;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// Shared P1.4 cheat fixtures: crafted walls, turn contexts and assertions. Lives in this
    /// file (the first expectedFiles entry) so the P1.4 delivery adds no paths beyond the
    /// agreed contract.
    /// </summary>
    internal static class CheatTestBench
    {
        public const string RoundId = "round-1";
        public const int RoundOrdinal = 1;

        /// <summary>Economy config: all three tricks cost 10 with the given starting balance.</summary>
        public static CheatEconomyConfig Economy(int initialPoints = 40)
        {
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { CheatEconomyConfig.TileMarkCostKey, 10 },
                { CheatEconomyConfig.SleeveBloomCostKey, 10 },
                { CheatEconomyConfig.BeamSwapCostKey, 10 }
            };
            return new CheatEconomyConfig(true, initialPoints, costs);
        }

        public static SleeveBloomConfig Sleeve(bool enabled = true) => new SleeveBloomConfig(enabled, 10_000);

        public static CheatTurnContext Context(
            int current,
            RoundPhase phase,
            SleeveBloomDrawKind drawKind,
            int turn = 1,
            long now = 1_000,
            bool responseWindow = false,
            bool[]? riichi = null)
        {
            return new CheatTurnContext(
                current,
                phase,
                responseWindow,
                riichi ?? new[] { false, false, false, false },
                turn,
                RoundId,
                RoundOrdinal,
                drawKind,
                now);
        }

        /// <summary>Cheat state with the test round already registered in the mark registry.</summary>
        public static CheatState InitialState(int initialPoints = 40)
        {
            CheatState state = CheatState.Initialize(Economy(initialPoints));
            TileMarkTransition registered = state.TileMarks.RegisterDealtRound(RoundId);
            Assert.True(registered.Success, registered.Error.ToString());
            return state.WithTileMarks(registered.State);
        }

        /// <summary>
        /// Crafted opening: live-wall slots 53/54/55 hold Man5c0/Man6c0/Man7c0 so every
        /// candidate position is deterministic; the dealer opens with its fourteenth tile.
        /// </summary>
        public static TilePositionMap CraftedOpening()
        {
            return TilePositionMap.CreateDealt(
                TestTables.Crafted(
                    (53, TileId.FromTypeIndex(4, 0)),
                    (54, TileId.FromTypeIndex(5, 0)),
                    (55, TileId.FromTypeIndex(6, 0))),
                0);
        }

        /// <summary>Dealer discards its fourteenth tile, then <paramref name="drawerSeat"/> makes a normal draw.</summary>
        public static TilePositionMap OpeningAfterNormalDraw(int drawerSeat, out TileId drawnTile, out TileId nextWallTile)
        {
            TilePositionMap map = CraftedOpening();
            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            map = map.Discard(0, dealerDrawn).RequireState();
            map = map.DrawFromWall(drawerSeat).RequireState();
            drawnTile = map.DrawnTile!.Value.Tile;
            nextWallTile = map.TileAt(TilePosition.Wall(map.WallPointer));
            return map;
        }

        /// <summary>Dealer discards, then <paramref name="drawerSeat"/> takes a rinshan draw (dead wall slot 0).</summary>
        public static TilePositionMap OpeningAfterRinshanDraw(int drawerSeat, out TileId drawnTile, out TileId nextRinshanTile)
        {
            TilePositionMap map = CraftedOpening();
            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            map = map.Discard(0, dealerDrawn).RequireState();
            map = map.DrawReplacement(drawerSeat).RequireState();
            drawnTile = map.DrawnTile!.Value.Tile;
            nextRinshanTile = map.TileAt(TilePosition.DeadWall(map.RinshanPointer));
            return map;
        }

        public static void AssertConserved(TilePositionMap map)
        {
            ConservationReport report = TilePositionMap.Validate(map);
            Assert.True(report.IsValid, report.ToString());
        }

        /// <summary>Compact deterministic view of all 136 tile locations for before/after comparisons.</summary>
        public static string Locations(TilePositionMap map)
        {
            var placements = new List<TilePlacement>(map.AsPlacements());
            placements.Sort((left, right) => left.Tile.Value.CompareTo(right.Tile.Value));
            var text = new StringBuilder();
            for (int i = 0; i < placements.Count; i++)
            {
                TilePosition position = placements[i].Position;
                text.Append(placements[i].Tile.Value)
                    .Append('@')
                    .Append((int)position.Zone)
                    .Append(':')
                    .Append(position.Seat)
                    .Append(':')
                    .Append(position.Index)
                    .Append(';');
            }

            return text.ToString();
        }

        public static void AssertSameLocations(TilePositionMap before, TilePositionMap after)
        {
            Assert.Equal(Locations(before), Locations(after));
        }
    }

    /// <summary>G-041: ledger idempotency, conflicts, overflow, overdraw and zero side effects.</summary>
    public class CheatLedgerTests
    {
        [Fact]
        public void Apply_ChargesExactlyOnceAndRecordsEntry()
        {
            CheatLedger ledger = CheatLedger.Initialize(CheatTestBench.Economy(40));

            CheatLedgerApplyResult result = ledger.Apply("skill-1", 0, -7, "TEST");

            Assert.True(result.Success);
            Assert.False(result.Replayed);
            Assert.Equal(new[] { 33, 40, 40, 40 }, result.Ledger!.Balances);
            Assert.Single(result.Ledger.Entries);
            Assert.Equal(1, result.Ledger.Version);
            Assert.Equal("skill-1", result.Entry!.EntryId);
            Assert.Equal(33, result.Entry.BalanceAfter);
        }

        [Fact]
        public void IdenticalPayload_ReplaysOriginalEntryWithoutStateChange()
        {
            CheatLedger ledger = CheatLedger.Initialize(CheatTestBench.Economy(40));
            ledger = ledger.Apply("skill-1", 0, -7, "TEST").Ledger!;

            CheatLedgerApplyResult replay = ledger.Apply("skill-1", 0, -7, "TEST");

            Assert.True(replay.Success);
            Assert.True(replay.Replayed);
            Assert.Equal(-7, replay.Entry!.Amount);
            Assert.Equal(33, replay.Entry.BalanceAfter);
            Assert.Equal(1, replay.Ledger!.Version);
            Assert.Equal(new[] { 33, 40, 40, 40 }, replay.Ledger.Balances);
        }

        [Fact]
        public void SameEntryId_DifferentPayload_IsConflictWithZeroSideEffects()
        {
            CheatLedger ledger = CheatLedger.Initialize(CheatTestBench.Economy(40));
            CheatLedger charged = ledger.Apply("skill-1", 0, -7, "TEST").Ledger!;

            CheatLedgerApplyResult conflict = charged.Apply("skill-1", 0, 6, "TEST");

            Assert.False(conflict.Success);
            Assert.Equal(CheatLedgerError.EntryConflict, conflict.Error);
            Assert.Null(conflict.Ledger);
            Assert.Equal(new[] { 33, 40, 40, 40 }, charged.Balances);
            Assert.Single(charged.Entries);
        }

        [Fact]
        public void Overdraw_Overflow_AndInvalidSeat_AreRejectedWithoutMutation()
        {
            CheatLedger ledger = CheatLedger.Initialize(CheatTestBench.Economy(10));

            Assert.False(ledger.Apply("over", 1, -11, "TEST").Success);
            Assert.Equal(CheatLedgerError.InsufficientFunds, ledger.Apply("over", 1, -11, "TEST").Error);
            Assert.False(ledger.Apply("big", 2, int.MaxValue, "TEST").Success);
            Assert.Equal(CheatLedgerError.BalanceOverflow, ledger.Apply("big", 2, int.MaxValue, "TEST").Error);
            Assert.False(ledger.Apply("seat", 4, -1, "TEST").Success);
            Assert.Equal(CheatLedgerError.InvalidSeat, ledger.Apply("seat", 4, -1, "TEST").Error);
            Assert.Equal(new[] { 10, 10, 10, 10 }, ledger.Balances);
            Assert.Empty(ledger.Entries);
        }

        [Fact]
        public void FromSnapshot_ReplaysEntriesOntoDeclaredBalances()
        {
            CheatLedger ledger = CheatLedger.Initialize(CheatTestBench.Economy(10));
            ledger = ledger.Apply("a", 0, -7, "TEST").Ledger!;
            ledger = ledger.Apply("b", 1, -3, "TEST").Ledger!;

            CheatLedger restored = CheatLedger.FromSnapshot(10, ledger.Balances, ledger.Entries);

            Assert.Equal(ledger.Balances, restored.Balances);
            Assert.Equal(ledger.Entries, restored.Entries);

            var drifted = new[] { 4, 10, 10, 10 };
            Assert.Throws<ArgumentException>(() => CheatLedger.FromSnapshot(10, drifted, ledger.Entries));
        }

        [Fact]
        public void Summarize_AggregatesGainSpendNet()
        {
            CheatLedger ledger = CheatLedger.Initialize(CheatTestBench.Economy(10));
            ledger = ledger.Apply("spend", 0, -7, "TEST").Ledger!;
            ledger = ledger.Apply("gain", 1, 5, "TEST").Ledger!;

            CheatLedgerSummary summary = ledger.Summarize();

            Assert.Equal(5, summary.TotalGained);
            Assert.Equal(7, summary.TotalSpent);
            Assert.Equal(-2, summary.Net);
        }
    }
}

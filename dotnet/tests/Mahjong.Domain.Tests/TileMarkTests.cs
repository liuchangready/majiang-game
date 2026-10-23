using System;
using System.Collections.Generic;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>Tile-mark state rules: rounds registry, group shape, capacity, expiry (G-033).</summary>
    public class TileMarkStateTests
    {
        private static TileMarkGroup Group(string id, int seat, params int[] tileValues)
        {
            return Group(id, seat, CheatTestBench.RoundId, CheatTestBench.RoundOrdinal, tileValues);
        }

        private static TileMarkGroup Group(string id, int seat, string roundId, int roundOrdinal, params int[] tileValues)
        {
            var tiles = new TileId[tileValues.Length];
            for (int i = 0; i < tileValues.Length; i++)
            {
                tiles[i] = new TileId(tileValues[i]);
            }

            return new TileMarkGroup(id, seat, tiles, roundId, roundOrdinal);
        }

        [Fact]
        public void RegisterDealtRound_AdvancesOrdinalAndRequiresSettlement()
        {
            var state = new TileMarkState();
            Assert.Equal(0, state.DealtRoundOrdinal);

            TileMarkTransition first = state.RegisterDealtRound("r1");
            Assert.True(first.Success);
            Assert.Equal(1, first.State.DealtRoundOrdinal);

            Assert.True(ReferenceEquals(first.State, first.State.RegisterDealtRound("r1").State), "same id must replay");

            TileMarkTransition second = first.State.RegisterDealtRound("r2");
            Assert.False(second.Success);
            Assert.Equal(TileMarkError.PreviousRoundNotSettled, second.Error);

            TileMarkTransition settled = first.State.CompleteRound("r1");
            Assert.True(settled.Success);
            TileMarkTransition next = settled.State.RegisterDealtRound("r2");
            Assert.True(next.Success);
            Assert.Equal(2, next.State.DealtRoundOrdinal);
        }

        [Fact]
        public void AddGroup_ShapeCapacityOverlapAndReplay()
        {
            var state = new TileMarkState();
            state = state.RegisterDealtRound(CheatTestBench.RoundId).State;

            TileMarkTransition added = state.AddGroup(Group("g1", 0, 0, 1, 2, 3));
            Assert.True(added.Success);
            Assert.Single(added.State.Groups);

            Assert.True(ReferenceEquals(added.State, added.State.AddGroup(Group("g1", 0, 0, 1, 2, 3)).State), "identical content replays");

            Assert.False(added.State.AddGroup(Group("g1", 0, 4, 5, 6, 7)).Success);
            Assert.Equal(TileMarkError.GroupIdConflict, added.State.AddGroup(Group("g1", 0, 4, 5, 6, 7)).Error);

            TileMarkState three = added.State
                .AddGroup(Group("g2", 0, 4, 5, 6, 7)).State
                .AddGroup(Group("g3", 0, 8, 9, 10, 11)).State;
            Assert.Equal(3, three.Groups.Count);

            Assert.False(three.AddGroup(Group("g4", 0, 12, 13, 14, 15)).Success);
            Assert.Equal(TileMarkError.GroupLimitExceeded, three.AddGroup(Group("g4", 0, 12, 13, 14, 15)).Error);

            Assert.False(added.State.AddGroup(Group("g2", 0, 0, 4, 5, 6)).Success);
            Assert.Equal(TileMarkError.TileOverlap, added.State.AddGroup(Group("g2", 0, 0, 4, 5, 6)).Error);

            TileMarkTransition crossSeat = added.State.AddGroup(Group("other", 1, 0, 1, 2, 3));
            Assert.True(crossSeat.Success, "different seats may mark the same tiles");
        }

        [Fact]
        public void AddGroup_RequiresCurrentUnsettledRound()
        {
            var state = new TileMarkState();
            Assert.False(state.AddGroup(Group("g1", 0, 0, 1, 2, 3)).Success);

            TileMarkState registered = state.RegisterDealtRound("r1").State;
            TileMarkState settled = registered.CompleteRound("r1").State;
            Assert.False(settled.AddGroup(Group("g1", 0, 0, 1, 2, 3)).Success);
            Assert.Equal(TileMarkError.RoundAlreadySettled, settled.AddGroup(Group("g1", 0, 0, 1, 2, 3)).Error);
        }

        [Fact]
        public void GroupsExpireThreeDealtRoundsAfterCreation()
        {
            var state = new TileMarkState();
            state = state.RegisterDealtRound("r1").State;
            state = state.AddGroup(Group("g1", 0, "r1", 1, 0, 1, 2, 3)).State;
            Assert.Single(state.Groups);
            Assert.Equal(4, state.Groups[0].ExpiresAfterRoundOrdinal);

            state = CompleteAndRegister(state, "r1", "r2");
            state = CompleteAndRegister(state, "r2", "r3");
            Assert.Single(state.CompleteRound("r3").State.Groups); // expires at ordinal 4, still live through round 3

            TileMarkState final = CompleteAndRegister(state, "r3", "r4").CompleteRound("r4").State;
            Assert.Empty(final.Groups);
            Assert.Equal(4, final.SettledRoundIds.Count);
        }

        private static TileMarkState CompleteAndRegister(TileMarkState state, string completeId, string nextId)
        {
            TileMarkTransition completed = state.CompleteRound(completeId);
            Assert.True(completed.Success, completed.Error.ToString());
            TileMarkTransition registered = completed.State.RegisterDealtRound(nextId);
            Assert.True(registered.Success, registered.Error.ToString());
            return registered.State;
        }

        [Fact]
        public void ClearOperations_KeepHistoryAndReplayNoOps()
        {
            var state = new TileMarkState();
            state = state.RegisterDealtRound(CheatTestBench.RoundId).State;
            state = state.AddGroup(Group("g1", 0, 0, 1, 2, 3)).State;
            state = state.AddGroup(Group("g2", 1, 4, 5, 6, 7)).State;

            TileMarkState cleared = state.ClearSeat(0).State;
            Assert.Single(cleared.Groups);
            Assert.Equal(1, cleared.Groups[0].OwnerSeat);
            Assert.Equal(1, cleared.DealtRoundOrdinal);

            Assert.True(ReferenceEquals(cleared, cleared.ClearSeat(0).State));

            var keep = new[] { 1, 2, 3 };
            TileMarkState except = state.ClearExcept(keep).State;
            Assert.Single(except.Groups);
            Assert.Equal(1, except.Groups[0].OwnerSeat);

            // keeping every seat drops nothing and returns the same instance
            Assert.True(ReferenceEquals(state, state.ClearExcept(new[] { 0, 1 }).State), "nothing dropped, same instance");

            TileMarkState emptied = state.ClearAll().State;
            Assert.Empty(emptied.Groups);
            Assert.True(ReferenceEquals(emptied, emptied.ClearAll().State), "no groups left, no change");
        }
    }

    /// <summary>G-032/G-033 engine level: tile-mark eligibility, atomic charge, rejections and replay.</summary>
    public class TileMarkEngineTests
    {
        private static IReadOnlyList<TileId> HandTiles(TilePositionMap map, int seat, params int[] ordinals)
        {
            IReadOnlyList<TileId> hand = map.TilesInZone(TileZone.Hand, seat);
            var tiles = new List<TileId>(ordinals.Length);
            for (int i = 0; i < ordinals.Length; i++)
            {
                tiles.Add(hand[ordinals[i]]);
            }

            return tiles;
        }

        [Fact]
        public void Success_ChargesOnce_DoesNotMoveTiles_AndEmitsPrivateEvents()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);
            IReadOnlyList<TileId> tiles = HandTiles(map, 0, 0, 1, 2, 3);

            CheatTransition result = CheatSubmission.ApplyTileMark(state, map, context, CheatTestBench.Economy(), 0, "req-1", "g1", tiles);

            Assert.True(result.Success, result.Message);
            Assert.True(result.Changed);
            Assert.Null(result.Map); // tile marks never move tiles
            Assert.Equal(2, result.Events.Count);
            Assert.Equal(CheatEventKind.CheatPointsChanged, result.Events[0].Kind);
            Assert.Equal(30, result.Events[0].NewBalance);
            Assert.Equal(CheatEventKind.TileMarkApplied, result.Events[1].Kind);

            CheatState next = result.State!;
            Assert.Equal(30, next.Ledger.BalanceOf(0));
            Assert.Single(next.Ledger.Entries);
            Assert.Equal("mark-0-req-1", next.Ledger.Entries[0].EntryId);
            Assert.Equal("TILE_MARK", next.Ledger.Entries[0].Reason);
            Assert.Single(next.TileMarks.GroupsForSeat(0));
            CheatTestBench.AssertConserved(map);
        }

        [Fact]
        public void PrivateView_ShowsGroupAndLedgerOnlyToOwner()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);

            CheatTransition result = CheatSubmission.ApplyTileMark(
                state, map, context, CheatTestBench.Economy(), 0, "req-1", "g1", HandTiles(map, 0, 0, 1, 2, 3));
            CheatState next = result.State!;

            CheatPrivateView owner = next.ViewFor(0);
            Assert.Equal(30, owner.Balance);
            Assert.Single(owner.Entries);
            Assert.Single(owner.MarkGroups);
            Assert.Equal("g1", owner.MarkGroups[0].GroupId);

            CheatPrivateView other = next.ViewFor(1);
            Assert.Equal(40, other.Balance);
            Assert.Empty(other.Entries);
            Assert.Empty(other.MarkGroups);
        }

        [Fact]
        public void Rejections_FailWithoutStateMapOrLedgerSideEffects()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatEconomyConfig economy = CheatTestBench.Economy();

            void Reject(string name, CheatTransition result, CheatErrorCode expected)
            {
                Assert.False(result.Success, name + " should be rejected but succeeded: " + result.Message);
                Assert.Equal(expected, result.Error);
                Assert.Null(result.State);
                Assert.Null(result.Map);
            }

            CheatState fresh = CheatTestBench.InitialState();
            CheatTurnContext actors = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);

            Reject("not your turn",
                CheatSubmission.ApplyTileMark(fresh, map, actors, economy, 1, "r", "g", HandTiles(map, 1, 0, 1, 2, 3)),
                CheatErrorCode.NotYourTurn);

            Reject("wrong phase",
                CheatSubmission.ApplyTileMark(
                    fresh, map,
                    CheatTestBench.Context(0, RoundPhase.ResponseWindow, SleeveBloomDrawKind.DealExtra),
                    economy, 0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.WrongPhase);

            Reject("response window open",
                CheatSubmission.ApplyTileMark(
                    fresh, map,
                    CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra, responseWindow: true),
                    economy, 0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.ResponseWindowOpen);

            Reject("riichi locked",
                CheatSubmission.ApplyTileMark(
                    fresh, map,
                    CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra, riichi: new[] { true, false, false, false }),
                    economy, 0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.RiichiLocked);

            Reject("foreign hand tile",
                CheatSubmission.ApplyTileMark(fresh, map, actors, economy, 0, "r", "g", HandTiles(map, 1, 0, 1, 2, 3)),
                CheatErrorCode.TileNotOwned);

            Reject("duplicate tile",
                CheatSubmission.ApplyTileMark(fresh, map, actors, economy, 0, "r", "g", HandTiles(map, 0, 0, 0, 1, 2)),
                CheatErrorCode.InvalidSelection);

            Reject("wrong tile count",
                CheatSubmission.ApplyTileMark(fresh, map, actors, economy, 0, "r", "g", HandTiles(map, 0, 0, 1, 2)),
                CheatErrorCode.InvalidSelection);

            Reject("insufficient funds",
                CheatSubmission.ApplyTileMark(
                    CheatTestBench.InitialState(5), map, actors, economy, 0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.InsufficientFunds);

            Reject("round not registered",
                CheatSubmission.ApplyTileMark(
                    CheatState.Initialize(economy), map, actors, economy, 0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.MarkRoundMismatch);

            Reject("disabled",
                CheatSubmission.ApplyTileMark(
                    fresh, map, actors,
                    new CheatEconomyConfig(false, 40, new Dictionary<string, int> { { CheatEconomyConfig.TileMarkCostKey, 10 } }),
                    0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.Disabled);

            Reject("cost not configured",
                CheatSubmission.ApplyTileMark(
                    fresh, map, actors,
                    new CheatEconomyConfig(true, 40, new Dictionary<string, int> { { CheatEconomyConfig.SleeveBloomCostKey, 10 } }),
                    0, "r", "g", HandTiles(map, 0, 0, 1, 2, 3)),
                CheatErrorCode.CostNotConfigured);

            Assert.Equal(40, fresh.Ledger.BalanceOf(0));
            Assert.Empty(fresh.Ledger.Entries);
        }

        [Fact]
        public void EngineReplay_SameRequest_ReturnsOriginalResultWithoutStateChange()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);
            IReadOnlyList<TileId> tiles = HandTiles(map, 0, 0, 1, 2, 3);

            CheatTransition first = CheatSubmission.ApplyTileMark(state, map, context, CheatTestBench.Economy(), 0, "req-1", "g1", tiles);
            Assert.True(first.Success);
            CheatState once = first.State!;

            CheatTransition replay = CheatSubmission.ApplyTileMark(once, map, context, CheatTestBench.Economy(), 0, "req-1", "g1", tiles);

            Assert.True(replay.Success);
            Assert.False(replay.Changed);
            Assert.True(ReferenceEquals(replay.State, once), "replay must return the identical instance");
            Assert.Single(once.Ledger.Entries);
            Assert.Single(once.TileMarks.Groups);
            Assert.Equal(30, once.Ledger.BalanceOf(0));
        }

        [Fact]
        public void SameRequest_DifferentGroupContent_IsConflictWithZeroSideEffects()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);
            CheatEconomyConfig economy = CheatTestBench.Economy();

            CheatTransition first = CheatSubmission.ApplyTileMark(
                state, map, context, economy, 0, "req-1", "g1", HandTiles(map, 0, 0, 1, 2, 3));
            Assert.True(first.Success);
            CheatState once = first.State!;

            CheatTransition conflict = CheatSubmission.ApplyTileMark(
                once, map, context, economy, 0, "req-1", "g1", HandTiles(map, 0, 4, 5, 6, 7));

            Assert.False(conflict.Success);
            Assert.Equal(CheatErrorCode.GroupIdConflict, conflict.Error);
            Assert.Null(conflict.State);
            Assert.Null(conflict.Map);
            Assert.Single(once.Ledger.Entries);
            Assert.Single(once.TileMarks.Groups);
            Assert.Equal(30, once.Ledger.BalanceOf(0));
        }

        [Fact]
        public void SeatCapacity_ThreeGroupsTwelveTiles_ThenRejected()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);
            CheatEconomyConfig economy = CheatTestBench.Economy();

            CheatTransition g1 = CheatSubmission.ApplyTileMark(state, map, context, economy, 0, "r1", "g1", HandTiles(map, 0, 0, 1, 2, 3));
            Assert.True(g1.Success, g1.Message);
            CheatTransition g2 = CheatSubmission.ApplyTileMark(g1.State!, map, context, economy, 0, "r2", "g2", HandTiles(map, 0, 4, 5, 6, 7));
            Assert.True(g2.Success, g2.Message);
            CheatTransition g3 = CheatSubmission.ApplyTileMark(g2.State!, map, context, economy, 0, "r3", "g3", HandTiles(map, 0, 8, 9, 10, 11));
            Assert.True(g3.Success, g3.Message);
            Assert.Equal(10, g3.State!.Ledger.BalanceOf(0));

            CheatTransition g4 = CheatSubmission.ApplyTileMark(
                g3.State!, map, context, economy, 0, "r4", "g4", HandTiles(map, 0, 12, 13, 4, 5));
            Assert.False(g4.Success);
            Assert.Equal(CheatErrorCode.MarkCapacityExceeded, g4.Error);
            Assert.Null(g4.State);
            Assert.Equal(10, g3.State.Ledger.BalanceOf(0));
            Assert.Equal(3, g3.State.TileMarks.Groups.Count);
        }

        [Fact]
        public void OverlappingTileBetweenGroups_IsRejectedAsAlreadyMarked()
        {
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatTurnContext context = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.DealExtra);
            CheatEconomyConfig economy = CheatTestBench.Economy();

            CheatTransition g1 = CheatSubmission.ApplyTileMark(state, map, context, economy, 0, "r1", "g1", HandTiles(map, 0, 0, 1, 2, 3));
            Assert.True(g1.Success);

            CheatTransition g2 = CheatSubmission.ApplyTileMark(
                g1.State!, map, context, economy, 0, "r2", "g2", HandTiles(map, 0, 3, 4, 5, 6));

            Assert.False(g2.Success);
            Assert.Equal(CheatErrorCode.AlreadyMarked, g2.Error);
            Assert.Null(g2.State);
            Assert.Single(g1.State!.TileMarks.Groups);
            Assert.Equal(30, g1.State.Ledger.BalanceOf(0));
        }
    }
}

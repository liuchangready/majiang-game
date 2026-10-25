using System.Linq;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// F-01 focused contract: the immediate sleeve bloom must be able to replay V4's frozen
    /// <c>nextWallTileId</c>/<c>nextWallTileInWall</c> pair. The legacy map-only entry is kept as
    /// a positive control; every frozen disagreement rejects with no ledger, offer, reservation,
    /// used-on-turn, map or event side effect and never substitutes another wall tile.
    /// </summary>
    public sealed class SleeveBloomFrozenInputTests
    {
        private const int Seat = 1;

        [Fact]
        public void Unspecified_IsDistinctFromAFrozenNullCandidate()
        {
            SleeveBloomActivationInput unspecified = SleeveBloomActivationInput.Unspecified;
            SleeveBloomActivationInput frozenNull = SleeveBloomActivationInput.Frozen(null, false);

            Assert.False(unspecified.IsFrozen);
            Assert.Null(unspecified.NextWallTileId);
            Assert.False(unspecified.NextWallTileInWall);

            Assert.True(frozenNull.IsFrozen);
            Assert.Null(frozenNull.NextWallTileId);
            Assert.False(frozenNull.NextWallTileInWall);
            Assert.NotSame(unspecified, frozenNull);
        }

        [Fact]
        public void LegacyMapOnly_WithoutFrozenInput_StillAllowsTheFirstLiveWallCandidate()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out TileId nextWall);

            CheatTransition actual = CheatSubmission.ActivateSleeveBloom(
                state, map, NormalContext(), CheatTestBench.Economy(), CheatTestBench.Sleeve(), Seat, "frozen-legacy");

            Assert.True(actual.Success, actual.Message);
            Assert.Equal(30, actual.State!.Ledger.BalanceOf(Seat));
            SleeveBloomOffer offer = actual.State.SleeveBloom.ImmediateOffer!;
            Assert.Equal(nextWall, offer.SecondTile);
            Assert.Equal(TilePosition.Wall(map.WallPointer), map.PositionOf(offer.SecondTile));
            Assert.Null(actual.Map);
        }

        [Fact]
        public void FrozenAuthoritativeCandidate_IsReplayedExactly()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out TileId nextWall);
            SleeveBloomActivationInput input = SleeveBloomActivationInput.FromAuthoritativeState(map, state.SleeveBloom);

            Assert.True(input.IsFrozen);
            Assert.True(input.NextWallTileInWall);
            Assert.Equal(nextWall, input.NextWallTileId);

            CheatTransition actual = CheatSubmission.ActivateSleeveBloomWithFrozenInput(
                state, map, NormalContext(), CheatTestBench.Economy(), CheatTestBench.Sleeve(),
                Seat, "frozen-ok", input);

            Assert.True(actual.Success, actual.Message);
            Assert.Equal(30, actual.State!.Ledger.BalanceOf(Seat));
            Assert.Equal(NormalContext().TurnNumber, actual.State.SleeveBloom.UsedOnTurn);
            Assert.Equal(nextWall, actual.State.SleeveBloom.ImmediateOffer!.SecondTile);
            Assert.Null(actual.Map);
        }

        [Fact]
        public void FrozenReservationAwareCandidate_SkipsTheProtectedTile()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out TileId firstWall);
            state = state.WithSleeveBloom(state.SleeveBloom.WithReservation(
                new SleeveBloomReservation(2, "foreign-reservation", firstWall, "window", 0)));

            SleeveBloomActivationInput input = SleeveBloomActivationInput.FromAuthoritativeState(map, state.SleeveBloom);
            Assert.True(input.IsFrozen);
            Assert.True(input.NextWallTileInWall);
            Assert.NotEqual(firstWall, input.NextWallTileId);

            CheatTransition actual = CheatSubmission.ActivateSleeveBloomWithFrozenInput(
                state, map, NormalContext(), CheatTestBench.Economy(), CheatTestBench.Sleeve(),
                Seat, "frozen-reserved-skip", input);

            Assert.True(actual.Success, actual.Message);
            Assert.Equal(input.NextWallTileId!.Value, actual.State!.SleeveBloom.ImmediateOffer!.SecondTile);
            Assert.NotEqual(firstWall, actual.State.SleeveBloom.ImmediateOffer.SecondTile);
        }

        [Fact]
        public void FrozenNullCandidate_RejectsWithoutSubstitutingOrMutating()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out _);

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(null, false), "frozen-null");

            AssertRejectedWithoutSideEffects(actual, state, map);
        }

        [Fact]
        public void FrozenCandidateClaimedInWallButPhysicallyOffWall_Rejects()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out TileId drawn, out _);
            Assert.Equal(TileZone.Hand, map.PositionOf(drawn).Zone);

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(drawn, true), "frozen-offwall-true");

            AssertRejectedWithoutSideEffects(actual, state, map);
        }

        [Fact]
        public void FrozenCandidateClaimedOffWallButPhysicallyInWall_Rejects()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out TileId firstWall);
            Assert.Equal(TileZone.Wall, map.PositionOf(firstWall).Zone);

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(firstWall, false), "frozen-inwall-false");

            AssertRejectedWithoutSideEffects(actual, state, map);
        }

        [Fact]
        public void FrozenCandidateReservedByAnOffTurnSelection_Rejects()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out TileId firstWall);
            state = state.WithSleeveBloom(state.SleeveBloom.WithReservation(
                new SleeveBloomReservation(2, "foreign-reservation", firstWall, "window", 0)));

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(firstWall, true), "frozen-reserved");

            AssertRejectedWithoutSideEffects(actual, state, map);
        }

        [Fact]
        public void FrozenDriftPrecedesInsufficientFunds_LikeV4Order()
        {
            CheatState state = CheatTestBench.InitialState(5);
            TilePositionMap map = NormalDrawMap(out _, out _);

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(null, false), "frozen-order");

            AssertRejectedWithoutSideEffects(actual, state, map);
            Assert.Equal(CheatErrorCode.NoWallCandidate, actual.Error);
            Assert.Equal(5, state.Ledger.BalanceOf(Seat));
        }

        [Fact]
        public void FrozenHoleBeforeTheWallPointer_IsTheAuthoritativeCandidate()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap live = NormalDrawMap(out _, out _);
            int holeIndex = live.WallPointer - 2;
            Assert.True(holeIndex >= 0);
            TileId moved = live.TileAt(TilePosition.Wall(live.WallPointer));
            var placements = live.AsPlacements()
                .Select(p => p.Tile.Equals(moved)
                    ? new TilePlacement(moved, TilePosition.Wall(holeIndex))
                    : p)
                .ToList();
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                live.WallPointer,
                live.WallEndExclusive,
                live.RinshanPointer,
                live.LastDiscard,
                live.Revision,
                live.LastRinshanDraw,
                live.DoraIndicators,
                live.KanCount,
                live.DrawnTile);
            CheatTestBench.AssertConserved(map);
            Assert.Equal(holeIndex, map.PositionOf(moved).Index);
            Assert.True(holeIndex < map.WallPointer);

            SleeveBloomActivationInput input = SleeveBloomActivationInput.FromAuthoritativeState(map, state.SleeveBloom);
            Assert.True(input.IsFrozen);
            Assert.Equal(moved, input.NextWallTileId);

            CheatTransition actual = ActivateFrozen(state, map, input, "frozen-hole");
            Assert.True(actual.Success, actual.Message);
            Assert.Equal(moved, actual.State!.SleeveBloom.ImmediateOffer!.SecondTile);
        }

        [Fact]
        public void FrozenCandidateInsideTheDeadWall_Rejects()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out _);
            TileId deadWallTile = map.TileAt(TilePosition.DeadWall(0));
            Assert.Equal(TileZone.DeadWall, map.PositionOf(deadWallTile).Zone);

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(deadWallTile, true), "frozen-deadwall");

            AssertRejectedWithoutSideEffects(actual, state, map);
        }

        [Fact]
        public void FrozenCandidateThatIsNotTheFirstAvailableWallTile_RejectsInsteadOfSwitching()
        {
            CheatState state = CheatTestBench.InitialState();
            TilePositionMap map = NormalDrawMap(out _, out TileId firstWall);
            TileId secondWall = map.TileAt(TilePosition.Wall(map.WallPointer + 1));
            Assert.NotEqual(firstWall, secondWall);

            CheatTransition actual = ActivateFrozen(
                state, map, SleeveBloomActivationInput.Frozen(secondWall, true), "frozen-not-first");

            AssertRejectedWithoutSideEffects(actual, state, map);
        }

        private static TilePositionMap NormalDrawMap(out TileId drawn, out TileId nextWall) =>
            CheatTestBench.OpeningAfterNormalDraw(Seat, out drawn, out nextWall);

        private static CheatTurnContext NormalContext() =>
            CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

        private static CheatTransition ActivateFrozen(
            CheatState state,
            TilePositionMap map,
            SleeveBloomActivationInput input,
            string requestId) =>
            CheatSubmission.ActivateSleeveBloomWithFrozenInput(
                state, map, NormalContext(), CheatTestBench.Economy(), CheatTestBench.Sleeve(),
                Seat, requestId, input);

        private static void AssertRejectedWithoutSideEffects(
            CheatTransition actual,
            CheatState state,
            TilePositionMap map)
        {
            long stateVersion = state.Version;
            long ledgerVersion = state.Ledger.Version;
            int balance = state.Ledger.BalanceOf(Seat);
            int ledgerEntries = state.Ledger.Entries.Count;
            long markVersion = state.TileMarks.Version;
            long sleeveVersion = state.SleeveBloom.Version;
            int usedOnTurn = state.SleeveBloom.UsedOnTurn;
            int beamUsedOnTurn = state.BeamSwap.UsedOnTurn;
            string immediateOffer = state.SleeveBloom.ImmediateOffer?.ToString() ?? "-";
            string offTurnOffers = string.Join("|", state.SleeveBloom.OffTurnOffers.Select(o => o.ToString()));
            string reservations = string.Join("|", state.SleeveBloom.Reservations.Select(r => r.ToString()));
            ulong mapHash = map.StateHash;
            int mapRevision = map.Revision;
            string locations = CheatTestBench.Locations(map);

            Assert.False(actual.Success);
            Assert.Equal(CheatErrorCode.NoWallCandidate, actual.Error);
            Assert.False(actual.Changed);
            Assert.Null(actual.State);
            Assert.Null(actual.Map);
            Assert.Empty(actual.Events);
            Assert.False(string.IsNullOrWhiteSpace(actual.Message));

            Assert.Equal(stateVersion, state.Version);
            Assert.Equal(ledgerVersion, state.Ledger.Version);
            Assert.Equal(balance, state.Ledger.BalanceOf(Seat));
            Assert.Equal(ledgerEntries, state.Ledger.Entries.Count);
            Assert.Equal(markVersion, state.TileMarks.Version);
            Assert.Equal(sleeveVersion, state.SleeveBloom.Version);
            Assert.Equal(usedOnTurn, state.SleeveBloom.UsedOnTurn);
            Assert.Equal(beamUsedOnTurn, state.BeamSwap.UsedOnTurn);
            Assert.Equal(immediateOffer, state.SleeveBloom.ImmediateOffer?.ToString() ?? "-");
            Assert.Equal(offTurnOffers, string.Join("|", state.SleeveBloom.OffTurnOffers.Select(o => o.ToString())));
            Assert.Equal(reservations, string.Join("|", state.SleeveBloom.Reservations.Select(r => r.ToString())));
            Assert.Equal(mapHash, map.StateHash);
            Assert.Equal(mapRevision, map.Revision);
            Assert.Equal(locations, CheatTestBench.Locations(map));
            CheatTestBench.AssertConserved(map);
        }
    }
}

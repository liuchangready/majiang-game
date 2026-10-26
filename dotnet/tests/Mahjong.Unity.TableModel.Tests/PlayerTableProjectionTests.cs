using System;
using System.Collections.Generic;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    public sealed class PlayerTableProjectionTests
    {
        private static PlayerTableViewState Project(PlayerSnapshot snapshot, ManualTableClock? clock = null, SessionViewState? view = null)
        {
            var mapper = new PlayerTableProjectionMapper(clock ?? new ManualTableClock());
            return mapper.Project(view ?? P43TestData.View(snapshot));
        }

        [Fact]
        public void MapsFourSeatsBoardClocksAndAuthorizedOwner()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            PlayerTableViewState view = Project(snapshot);

            Assert.Equal(P43TestData.SessionId, view.Identity.SessionId);
            Assert.Equal(P43TestData.RoomId, view.Identity.RoomId);
            Assert.Equal(P43TestData.RoundId, view.Identity.RoundId);
            Assert.Equal(0, view.Identity.Seat);
            Assert.Equal(PlayerTableRole.Player, view.Identity.Role);
            Assert.Equal(5UL, view.Identity.StateVersion);
            Assert.Equal(105UL, view.Identity.EventSeq);
            Assert.Equal(5UL, view.Identity.StreamVersion);
            Assert.Equal(10UL, view.Identity.ProjectionSeq);
            Assert.Equal(P43TestData.ScopeId, view.Identity.ScopeId);
            Assert.True(view.Identity.IsPrivateAuthorized);
            Assert.True(view.CanSubmit);
            Assert.Equal(4, view.Seats.Count);
            Assert.Equal(14U, view.Seat(0)!.HandCount);
            Assert.Equal(13U, view.Seat(1)!.HandCount);
            Assert.True(view.Seat(2)!.Riichi);
            Assert.Equal(4, view.Clocks.Count);
            Assert.Equal(8000L, view.Clock(0)!.DecisionDeadline!.ServerRemainingMilliseconds);
            Assert.Equal(PlayerGamePhase.Discard, view.Board.Phase);
            Assert.Equal(70U, view.Board.WallRemaining);
            Assert.Equal(25000, view.Board.Scores[0].Score);
            Assert.Equal("decision-p43", view.Own!.Decision!.DecisionId);
            Assert.Equal(7UL, view.Own.Decision.ControlEpoch);
            Assert.Equal(3, view.Own.Decision.Candidates.Count);
        }

        [Fact]
        public void SpectatorProjectionHasNoPrivateCheatOrCandidates()
        {
            PublicSnapshot publicSnapshot = P43TestData.PublicSnapshot();
            SessionViewState sessionView = P43TestData.View(
                null,
                publicSnapshot,
                seat: -1,
                role: ProtocolRole.Spectator);
            var mapper = new PlayerTableProjectionMapper(new ManualTableClock());
            PlayerTableViewState view = mapper.Project(sessionView);

            Assert.Equal(PlayerTableRole.Spectator, view.Identity.Role);
            Assert.Equal(-1, view.Identity.Seat);
            Assert.False(view.Identity.IsPrivateAuthorized);
            Assert.Null(view.Own);
            Assert.False(view.CanSubmit);
            Assert.Equal(4, view.Seats.Count);
            Assert.Equal(14U, view.Seat(0)!.HandCount);
            Assert.True(view.Seat(0)!.MeldsAreOwnerOnly == false);
        }

        [Fact]
        public void SpectatorBindingIgnoresPlayerSnapshotWithoutLeakingPrivate()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            SessionViewState sessionView = P43TestData.View(
                snapshot,
                seat: -1,
                role: ProtocolRole.Spectator);
            var mapper = new PlayerTableProjectionMapper(new ManualTableClock());
            PlayerTableViewState view = mapper.Project(sessionView);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.SpectatorPlayerSnapshotIgnored));
            Assert.False(view.CanSubmit);
        }

        [Fact]
        public void WrongPrivateSeatDropsPrivatePart()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PrivateView.Seat = 1;
            PlayerTableViewState view = Project(snapshot);

            Assert.Null(view.Own);
            Assert.False(view.Identity.IsPrivateAuthorized);
            Assert.True(view.HasGap(PlayerTableGapCodes.PrivateSeatMismatch));
            Assert.False(view.CanSubmit);
        }

        [Fact]
        public void WrongSessionDropsPrivatePart()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot(sessionId: "other-session");
            PlayerTableViewState view = Project(snapshot);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.PrivateSessionMismatch));
        }

        [Fact]
        public void NonReadyStateDropsPrivatePart()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            SessionViewState sessionView = P43TestData.View(snapshot, state: TableConnectionState.Reconnecting);
            PlayerTableViewState view = Project(snapshot, view: sessionView);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.PrivateViewNotReady));
            Assert.False(view.CanSubmit);
        }

        [Fact]
        public void RoomBindingMismatchDropsPrivateAndPublic()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            var room = new RoomUpdate
            {
                RoomId = "different-room",
                SessionId = P43TestData.SessionId,
                RoomVersion = 1,
                Lifecycle = RoomLifecycle.Playing
            };
            SessionViewState sessionView = P43TestData.View(snapshot, room: room);
            PlayerTableViewState view = Project(snapshot, view: sessionView);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.RoomBindingMismatch));
            Assert.False(view.Seat(0)!.HasPublicData);
        }

        [Fact]
        public void PublicRoundMismatchDropsPrivateAndPublic()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            PublicSnapshot publicSnapshot = P43TestData.PublicSnapshot(roundId: "round-other");
            SessionViewState sessionView = P43TestData.View(snapshot, publicSnapshot);
            PlayerTableViewState view = Project(snapshot, view: sessionView);

            Assert.Null(view.Own);
            Assert.False(view.Identity.IsPrivateAuthorized);
            Assert.True(view.HasGap(PlayerTableGapCodes.PublicRoundMismatch));
            Assert.False(view.Seat(0)!.HasPublicData);
        }

        [Fact]
        public void OpponentClosedKanIsIdentityFreePlaceholder()
        {
            PlayerTableViewState view = Project(P43TestData.PlayerSnapshot());
            IReadOnlyList<PlayerMeldState> melds = view.Seat(1)!.Melds;

            Assert.Equal(2, melds.Count);
            Assert.All(melds, meld =>
            {
                Assert.True(meld.IsPlaceholder);
                Assert.Equal(PlayerMeldKind.ClosedKan, meld.Kind);
                Assert.Empty(meld.TileIds);
                Assert.Equal(-1, meld.FromSeat);
            });
            Assert.NotEqual(melds[0].EntityKey, melds[1].EntityKey);
        }

        [Fact]
        public void OwnerSeatUsesOwnerMeldsAndDoesNotRenderPublicCopy()
        {
            PlayerTableViewState view = Project(P43TestData.PlayerSnapshot());
            IReadOnlyList<PlayerMeldState> melds = view.Seat(0)!.Melds;

            Assert.True(view.Seat(0)!.MeldsAreOwnerOnly);
            Assert.Same(view.Seat(0)!.Melds, view.OwnMelds);
            Assert.Equal(2, melds.Count);
            Assert.Contains(melds, meld => meld.Kind == PlayerMeldKind.Chi);
            PlayerMeldState closedKan = Assert.Single(melds, meld => meld.Kind == PlayerMeldKind.ClosedKan);
            Assert.Equal(new uint[] { 60, 61, 62, 63 }, closedKan.TileIds);
            Assert.All(melds, meld => Assert.False(meld.IsPlaceholder));

            var ownTiles = new HashSet<uint>(view.Own!.ClosedTiles);
            if (view.Own.DrawnTileId.HasValue)
            {
                Assert.True(ownTiles.Add(view.Own.DrawnTileId.Value));
            }

            foreach (uint tile in view.Own.SleeveTiles)
            {
                Assert.True(ownTiles.Add(tile));
            }

            foreach (PlayerMeldState meld in melds)
            {
                foreach (uint tile in meld.TileIds)
                {
                    Assert.True(ownTiles.Add(tile));
                }
            }
        }

        [Fact]
        public void ClosedTilesAndDrawnTileAreSeparateAndDisjoint()
        {
            PlayerTableViewState view = Project(P43TestData.PlayerSnapshot());

            Assert.Equal(13, view.Own!.ClosedTiles.Count);
            Assert.Equal(13U, view.Own.DrawnTileId);
            Assert.DoesNotContain(13U, view.Own.ClosedTiles);
            Assert.Equal(14U, view.Own.PhysicalHandTileCount);
            Assert.Equal(view.Seat(0)!.HandCount, view.Own.PhysicalHandTileCount);
        }

        [Fact]
        public void DuplicateDrawnAndClosedTileDropsPrivatePart()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PrivateView.DrawnTileId = 0;
            PlayerTableViewState view = Project(snapshot);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.PrivateTileDuplicated));
            Assert.False(view.Identity.IsPrivateAuthorized);
        }

        [Fact]
        public void SleeveOverlappingHandDropsPrivatePart()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PrivateView.SleeveTiles[0] = 0;
            PlayerTableViewState view = Project(snapshot);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.PrivateTileDuplicated));
        }

        [Fact]
        public void OwnerMeldOverlappingHandDropsPrivatePart()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PrivateView.OwnerMelds[1].TileIds[0] = 0;
            PlayerTableViewState view = Project(snapshot);

            Assert.Null(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.PrivateTileDuplicated));
        }

        [Fact]
        public void PublicHandCountMismatchIsRecordedAsGap()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PublicView.Board.Players[0].HandCount = 13;
            PlayerTableViewState view = Project(snapshot);

            Assert.NotNull(view.Own);
            Assert.True(view.HasGap(PlayerTableGapCodes.OwnHandCountMismatch));
        }

        [Fact]
        public void MissingSeatUsesNullCountAndExplicitGap()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PublicView.Board.Players.RemoveAt(3);
            PlayerTableViewState view = Project(snapshot);

            Assert.Equal(4, view.Seats.Count);
            Assert.False(view.Seat(3)!.HasPublicData);
            Assert.Null(view.Seat(3)!.HandCount);
            Assert.True(view.HasGap(PlayerTableGapCodes.MissingSeatProjection));
        }

        [Fact]
        public void ProjectionDeepCopiesAndDoesNotMutateSource()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            byte[] before = snapshot.ToByteArray();
            PlayerTableViewState view = Project(snapshot);

            Assert.Equal(before, snapshot.ToByteArray());
            int closedCount = view.Own!.ClosedTiles.Count;
            int discardCount = view.Seat(0)!.Discards.Count;
            snapshot.PrivateView.ClosedTiles.Clear();
            snapshot.PublicView.Board.Players[0].Discards.Clear();

            Assert.Equal(closedCount, view.Own.ClosedTiles.Count);
            Assert.Equal(discardCount, view.Seat(0)!.Discards.Count);
        }

        [Fact]
        public void CountdownUsesInjectedMonotonicClockWithoutChangingServerValue()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = Project(P43TestData.PlayerSnapshot(), clock);
            PlayerCountdown countdown = view.Own!.Decision!.Countdown;

            Assert.Equal(8000L, countdown.RemainingMilliseconds(clock.MonotonicMilliseconds));
            clock.Advance(1000);
            Assert.Equal(7000L, countdown.RemainingMilliseconds(clock.MonotonicMilliseconds));
            Assert.Equal(8000L, countdown.ServerRemainingMilliseconds);
            Assert.Equal(7000L, view.Clock(0)!.DecisionDeadline!.RemainingMilliseconds(clock.MonotonicMilliseconds));
        }

        [Fact]
        public void StructuralWireGapsAreExplicit()
        {
            PlayerTableViewState view = Project(P43TestData.PlayerSnapshot());

            Assert.True(view.HasGap(PlayerTableGapCodes.DeadWallIdentityNotPublic));
            Assert.True(view.HasGap(PlayerTableGapCodes.ControlStateNotRepresentable));
            Assert.True(view.HasGap(PlayerTableGapCodes.LegalActionMeldTypeNotRepresentable));
            Assert.True(view.HasGap(PlayerTableGapCodes.AbsoluteDeadlineNotRepresentable));
            Assert.True(view.HasGap(PlayerTableGapCodes.VisibleEventsNotExposedBySessionView));
            Assert.True(view.HasGap(PlayerTableGapCodes.ShuffleTargetCandidatesNotRepresentable));
            Assert.True(view.HasGap(PlayerTableGapCodes.BeamSlotKindNotInCommand));
        }
    }
}

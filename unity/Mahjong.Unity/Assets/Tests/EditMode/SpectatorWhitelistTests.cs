#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Spectator;
using Mahjong.Unity.UI.Session;
using ProtocolRole = Mahjong.Contracts.ProtocolRole;
using RequestScope = Mahjong.Contracts.RequestScope;
using RequestScopeId = Mahjong.Contracts.RequestScopeId;
using RoomId = Mahjong.Contracts.RoomId;
using SessionId = Mahjong.Contracts.SessionId;
using ConnectionEpoch = Mahjong.Contracts.ConnectionEpoch;
using OwnerEpoch = Mahjong.Contracts.OwnerEpoch;
using BindingEpoch = Mahjong.Contracts.BindingEpoch;
using StreamBaseline = Mahjong.Contracts.StreamBaseline;
using StateVersion = Mahjong.Contracts.StateVersion;
using EventSeq = Mahjong.Contracts.EventSeq;
using StreamCursor = Mahjong.Contracts.StreamCursor;
using StreamVersion = Mahjong.Contracts.StreamVersion;
using ProjectionSeq = Mahjong.Contracts.ProjectionSeq;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class SpectatorWhitelistTests
    {
        private SessionBindingView CreateBinding(string sessionId, string roomId = "room_101", ProtocolRole role = ProtocolRole.Spectator, int seat = -1)
        {
            var raw = new TableSessionBinding(
                new SessionId(sessionId),
                new RoomId(roomId),
                "table_1",
                role,
                seat,
                new ConnectionEpoch(1),
                new OwnerEpoch(1),
                new BindingEpoch(1),
                new RequestScope(new RequestScopeId("scope_1"), "account_1", role, new RoomId(roomId), 1000, 2000),
                new StreamBaseline(new StateVersion(1), new EventSeq(1), new StreamCursor(new StreamVersion(1), new ProjectionSeq(1))));
            return new SessionBindingView(raw);
        }

        [Test]
        public void Map_ValidPublicSnapshot_ExposesOnlyWhitelistedFacts()
        {
            var snap = new PublicSnapshot
            {
                SessionId = "sess_spectator_1",
                RoundId = "round_E1_0",
                View = new PublicMatchView
                {
                    Board = new PublicBoardView
                    {
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 1,
                        DealerSeat = 0,
                        CurrentSeat = 0,
                        WallRemaining = 68,
                        Paused = false,
                    }
                }
            };

            snap.View.Board.DoraIndicators.AddRange(new uint[] { 5, 14 });
            snap.View.Board.Scores.Add(new ScoreView { Seat = 0, Score = 25000 });
            snap.View.Board.Scores.Add(new ScoreView { Seat = 1, Score = 24000 });
            snap.View.Board.Scores.Add(new ScoreView { Seat = 2, Score = 26000 });
            snap.View.Board.Scores.Add(new ScoreView { Seat = 3, Score = 25000 });

            var p0 = new PlayerPublicView { Seat = 0, HandCount = 13, Riichi = false };
            p0.Discards.Add(new DiscardView { TileId = 1, RiichiDiscard = false, Tsumogiri = false });
            snap.View.Board.Players.Add(p0);

            var p1 = new PlayerPublicView { Seat = 1, HandCount = 10, Riichi = true };
            p1.Discards.Add(new DiscardView { TileId = 9, RiichiDiscard = true, Tsumogiri = true });
            var meld = new MeldView { Type = MeldType.Chi };
            meld.TileIds.AddRange(new uint[] { 11, 12, 13 });
            p1.Melds.Add(meld);
            p1.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 }); // 1 ankan
            snap.View.Board.Players.Add(p1);

            var p2 = new PlayerPublicView { Seat = 2, HandCount = 13, Riichi = false };
            snap.View.Board.Players.Add(p2);

            var p3 = new PlayerPublicView { Seat = 3, HandCount = 13, Riichi = false };
            snap.View.Board.Players.Add(p3);

            snap.View.Clocks.Add(new ClockView { DecisionSeat = 0, DeadlineRemainingMs = 5000, ReserveRemainingMs = 20000 });

            var binding = CreateBinding("sess_spectator_1");
            var model = SpectatorWhitelistMapper.Map(snap, binding);

            Assert.IsNotNull(model);
            Assert.AreEqual("sess_spectator_1", model!.SessionId);
            Assert.AreEqual(68, model.WallRemaining);
            Assert.AreEqual(1, model.RiichiSticks);
            Assert.IsFalse(model.IsPaused);
            Assert.AreEqual(2, model.DoraIndicators.Count);
            CollectionAssert.AreEqual(new uint[] { 5, 14 }, model.DoraIndicators);

            Assert.AreEqual(4, model.Seats.Count);

            // Seat 0
            Assert.AreEqual(0, model.Seats[0].SeatId);
            Assert.AreEqual(25000, model.Seats[0].Score);
            Assert.IsTrue(model.Seats[0].IsDealer);
            Assert.IsFalse(model.Seats[0].IsRiichi);
            Assert.AreEqual(13, model.Seats[0].HandCount);
            Assert.AreEqual(1, model.Seats[0].Discards.Count);
            Assert.AreEqual(0, model.Seats[0].ConcealedMeldCount);

            // Seat 1
            Assert.AreEqual(1, model.Seats[1].SeatId);
            Assert.AreEqual(24000, model.Seats[1].Score);
            Assert.IsFalse(model.Seats[1].IsDealer);
            Assert.IsTrue(model.Seats[1].IsRiichi);
            Assert.AreEqual(10, model.Seats[1].HandCount);
            Assert.AreEqual(1, model.Seats[1].ConcealedMeldCount);
            Assert.AreEqual(1, model.Seats[1].Melds.Count);
            Assert.AreEqual(MeldType.Chi, model.Seats[1].Melds[0].Type);
        }

        [Test]
        public void Map_SessionIdMismatch_ReturnsNullToProtectPrivacy()
        {
            var snap = new PublicSnapshot
            {
                SessionId = "sess_authorized_A",
                View = new PublicMatchView { Board = new PublicBoardView() }
            };

            var binding = CreateBinding("sess_mismatched_B");
            var model = SpectatorWhitelistMapper.Map(snap, binding);
            Assert.IsNull(model, "Mismatched session ID must result in null view model to clear display.");
        }

        [Test]
        public void Map_NullOrEmptyInputs_ReturnsNullSafely()
        {
            Assert.IsNull(SpectatorWhitelistMapper.Map(null, null));

            var snap = new PublicSnapshot
            {
                SessionId = "",
                View = null
            };
            Assert.IsNull(SpectatorWhitelistMapper.Map(snap, null));
        }

        [Test]
        public void Map_AnkanPlaceholder_CarriesZeroTileIdentities()
        {
            var snap = new PublicSnapshot
            {
                SessionId = "sess_1",
                View = new PublicMatchView
                {
                    Board = new PublicBoardView()
                }
            };
            var p0 = new PlayerPublicView { Seat = 0, HandCount = 11 };
            p0.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 });
            p0.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 }); // 2 ankan
            snap.View.Board.Players.Add(p0);

            var binding = CreateBinding("sess_1");
            var model = SpectatorWhitelistMapper.Map(snap, binding);
            Assert.IsNotNull(model);
            Assert.AreEqual(2, model!.Seats[0].ConcealedMeldCount);

            var property = typeof(SpectatorSeatModel).GetProperty("ConcealedTileIds");
            Assert.IsNull(property, "SpectatorSeatModel must never expose concealed tile IDs property.");
        }

        [Test]
        public void Map_DefensiveCopy_SourceMutationDoesNotAffectModel()
        {
            var snap = new PublicSnapshot
            {
                SessionId = "sess_1",
                View = new PublicMatchView
                {
                    Board = new PublicBoardView()
                }
            };
            snap.View.Board.DoraIndicators.Add(5);
            var p0 = new PlayerPublicView { Seat = 0 };
            p0.Discards.Add(new DiscardView { TileId = 1 });
            snap.View.Board.Players.Add(p0);

            var binding = CreateBinding("sess_1");
            var model = SpectatorWhitelistMapper.Map(snap, binding);
            Assert.IsNotNull(model);

            // Mutate source proto collections
            snap.View.Board.DoraIndicators.Add(99);
            p0.Discards.Add(new DiscardView { TileId = 99 });

            Assert.AreEqual(1, model!.DoraIndicators.Count, "Dora list must be defensively isolated.");
            Assert.AreEqual(1, model.Seats[0].Discards.Count, "Discards list must be defensively isolated.");
        }
    }
}

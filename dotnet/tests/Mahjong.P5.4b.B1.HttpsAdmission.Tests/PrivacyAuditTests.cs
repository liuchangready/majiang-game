using Mahjong.Contracts.Wire;
using Mahjong.P5_4b.B1.HttpsAdmission.Worker;
using Xunit;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Tests
{
    public sealed class PrivacyAuditTests
    {
        [Fact]
        public void UnsafeBatchFollowedBySafeBatchRemainsViolated()
        {
            PrivacyAuditState audit = new PrivacyAuditState();
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: true), Public(closedKan: true), null);
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: false), Public(closedKan: false), null);

            Assert.True(audit.Violated);
            Assert.Equal(2, audit.BatchesObserved);
        }

        [Fact]
        public void UnsafeUpdateAfterSafeBaselineFails()
        {
            PrivacyAuditState audit = new PrivacyAuditState();
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: false), Public(closedKan: false), null);
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: true), Public(closedKan: true), null);

            Assert.True(audit.Violated);
        }

        [Fact]
        public void LegalMultipleBatchesPass()
        {
            PrivacyAuditState audit = new PrivacyAuditState();
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: false), Public(closedKan: false), null);
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: false), Public(closedKan: false), null);
            audit.Observe("session-1", "room-1", "PLAYER", 0, Snapshot(closedKan: false), Public(closedKan: false), null);

            Assert.False(audit.Violated);
            Assert.Equal(3, audit.BatchesObserved);
        }

        [Fact]
        public void ClosedKanAsPublicMeldWithPhysicalIdsIsRejected()
        {
            PlayerSnapshot player = Snapshot(closedKan: true);
            PublicSnapshot publicSnapshot = Public(closedKan: true);

            bool accepted = PublicProjectionValidator.Validate(
                "session-1",
                "room-1",
                "PLAYER",
                0,
                player,
                publicSnapshot,
                null,
                out string? reason);

            Assert.False(accepted);
            Assert.NotNull(reason);
        }

        private static PlayerSnapshot Snapshot(bool closedKan)
        {
            var board = new PublicBoardView();
            var publicPlayer = new PlayerPublicView { Seat = 0 };
            if (closedKan)
            {
                var meld = new MeldView { Type = MeldType.ClosedKan, FromSeat = -1 };
                meld.TileIds.Add(new uint[] { 0, 1, 2, 3 });
                publicPlayer.Melds.Add(meld);
            }

            board.Players.Add(publicPlayer);
            return new PlayerSnapshot
            {
                SessionId = "session-1",
                PublicView = new PublicMatchView { Board = board },
                PrivateView = new PlayerPrivateView { Seat = 0 }
            };
        }

        private static PublicSnapshot Public(bool closedKan)
        {
            return new PublicSnapshot
            {
                SessionId = "session-1",
                View = Snapshot(closedKan).PublicView
            };
        }
    }
}

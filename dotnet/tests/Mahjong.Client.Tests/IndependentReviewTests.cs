using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Tests
{
    public class IndependentReviewTests
    {
        [Fact]
        public async Task StaleSyncCannotRollBackSameBinding()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerPush(11, 8, 13, 9, 14));
            await session.PumpOnceAsync(CancellationToken.None);
            var stale = TestWire.PlayerSync(11, 4, 9);
            stale.CorrelationId = "obsolete-sync";
            channel.Enqueue(stale);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.True(session.ConfirmedCursor!.Value.ProjectionSeq.Value >= 11,
                "Same-binding stale sync rolled the confirmed cursor back below 11");
        }

        [Fact]
        public async Task ProtocolCloseMustAllowReconciliationAfterReconnect()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            await session.SubmitAsync(TestWire.Command(), CancellationToken.None);
            await session.ReconcilePendingAsync(CancellationToken.None);
            channel.Enqueue(TestWire.Envelope(MessageType.Close, e => e.Close = new Close
                { Code = "TEST_TRANSIENT", Permanent = false }));
            await session.PumpOnceAsync(CancellationToken.None);
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "build"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(22, "scope-2", 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerSync(channel, 22, 8, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, channel.CountSent(MessageType.ReceiptQuery));
        }

        [Fact]
        public async Task FailureBeforeFirstWelcomeMustRemainReconnectable()
        {
            var session = new TableSession(new FakeTableChannel(), new InMemoryOutboxStore());
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "build"), CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            await session.OpenAsync(new TableSessionOpenRequest("ticket", "build"), CancellationToken.None);
            Assert.Equal(TableConnectionState.Handshaking, session.State);
        }

        [Fact]
        public async Task PushSnapshotMustMatchBoundSession()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            var push = TestWire.PlayerPush(11, 5, 10, 6, 11);
            push.PlayerUpdate.Snapshot.SessionId = "different-session";
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TestSession.SessionId, session.PlayerSnapshot!.SessionId);
        }

        [Fact]
        public async Task SpectatorMustRejectPlayerCommandBeforePersistenceAndSend()
        {
            var (session, channel, store) = await TestSession.ConnectedSpectatorAsync();
            try { await session.SubmitAsync(TestWire.Command(), CancellationToken.None); }
            catch (System.InvalidOperationException) { }
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(0, store.SaveCount);
        }
    }
}

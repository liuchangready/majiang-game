using System;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Operator drain: token-gated endpoint, exact refusal semantics (404 without token,
    /// 401 with the wrong token), new handshakes refused at HTTP 503 while draining, existing
    /// sessions keep operating, readiness flips, and undraining restores admission.
    /// </summary>
    public sealed class DrainLifecycleTests
    {
        private const string DrainToken = "drain-token-p51";

        [Fact]
        public async Task DrainEndpointIsInvisibleWithoutToken()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();

            (int status, string _) = await host.PostAsync(TableServiceWebHost.DrainPath, DrainToken);
            Assert.Equal(404, status);

            int ready = await host.GetAsync(TableServiceWebHost.ReadyPath);
            Assert.Equal(200, ready);
        }

        [Fact]
        public async Task DrainRefusesNewHandshakesButKeepsExistingSessions()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.AdminDrainToken = DrainToken);
            TableTestClient live = await host.ConnectPlayerAsync("room-drain", "acct-drain", 0);
            try
            {
                await live.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));

                (int unauthorized, _) = await host.PostAsync(
                    TableServiceWebHost.DrainPath + "?enabled=true", "wrong-token");
                Assert.Equal(401, unauthorized);

                (int ok, string body) = await host.PostAsync(
                    TableServiceWebHost.DrainPath + "?enabled=true", DrainToken);
                Assert.Equal(200, ok);
                Assert.Equal("draining", body);

                int ready = await host.GetAsync(TableServiceWebHost.ReadyPath);
                Assert.Equal(503, ready);

                (int handshake, string retryAfter) = await host.ProbeHandshakeAsync("?room=room-drain");
                Assert.Equal(503, handshake);
                Assert.False(string.IsNullOrEmpty(retryAfter));

                // Established sessions are untouched: the stream stays up and liveness
                // keeps being answered while draining.
                string nonce = TableWire.RandomNonce();
                await live.SendAsync(TableWire.HeartbeatPing(nonce));
                Envelope pong = await live.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Heartbeat &&
                                envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);

                // New work is refused at the session with the retryable drain error (the
                // same semantics DrainingConnectionRefusesNewCommandsAtTheSession pins);
                // the connection itself is not dropped.
                byte[] payload = TableWire.SubmitActionPayload(
                    "room-drain-r1", "decision-drain", 0, (uint)ProtocolActionType.Pass);
                await live.SendAsync(TableWire.Command(
                    live.Welcome!, TableWire.RandomId("req"),
                    Contracts.CommandKind.SubmitAction, payload));
                Envelope refused = await live.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(ProtocolErrorCode.ServerBusy, refused.Error.Code);
                Assert.Equal("DRAINING", refused.Error.DetailCode);
                Assert.True(refused.Error.Retryable);

                // The session survives the refusal.
                string nonce2 = TableWire.RandomNonce();
                await live.SendAsync(TableWire.HeartbeatPing(nonce2));
                Envelope pong2 = await live.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Heartbeat &&
                                envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(nonce2, pong2.Heartbeat.Pong.Nonce);

                // Undrain: admission and readiness recover.
                (int restored, string body2) = await host.PostAsync(
                    TableServiceWebHost.DrainPath + "?enabled=false", DrainToken);
                Assert.Equal(200, restored);
                Assert.Equal("serving", body2);
                int readyAgain = await host.GetAsync(TableServiceWebHost.ReadyPath);
                Assert.Equal(200, readyAgain);

                TableTestClient fresh = await host.ConnectPlayerAsync("room-drain", "acct-after", 1);
                Assert.Equal(Contracts.Wire.Role.Player, fresh.Welcome!.Role);
                await fresh.DisposeAsync();
            }
            finally
            {
                await live.DisposeAsync();
            }
        }

        [Fact]
        public async Task DrainingConnectionRefusesNewCommandsAtTheSession()
        {
            // A session admitted before the drain keeps its stream; a command issued while
            // draining gets the retryable SERVER_BUSY(DRAINING) transport error, not silence.
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.AdminDrainToken = DrainToken);
            TableTestClient live = await host.ConnectPlayerAsync("room-dd", "acct-dd", 0);
            try
            {
                await live.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate,
                    TimeSpan.FromSeconds(20));
                (int ok, _) = await host.PostAsync(TableServiceWebHost.DrainPath, DrainToken);
                Assert.Equal(200, ok);

                byte[] payload = TableWire.SubmitActionPayload(
                    "room-dd-r1", "decision-dd", 0, (uint)ProtocolActionType.Pass);
                await live.SendAsync(TableWire.Command(
                    live.Welcome!, TableWire.RandomId("req"),
                    Contracts.CommandKind.SubmitAction, payload));
                Envelope error = await live.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(ProtocolErrorCode.ServerBusy, error.Error.Code);
                Assert.Equal("DRAINING", error.Error.DetailCode);
                Assert.True(error.Error.Retryable);
            }
            finally
            {
                await live.DisposeAsync();
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// B1 heartbeat binding regression: a server-generated PING is control traffic for
    /// the CURRENT admitted connection, so its envelope must carry that connection's
    /// authoritative session/room/seat/binding epoch. Before the fix the server sent a
    /// bare envelope (empty session/room, seat 0, epoch 0), which the strict frozen
    /// Client correctly rejected with PROTOCOL_VIOLATION at the first 5 s PING.
    ///
    /// These tests pin the server side only: the Client's strict binding validation is
    /// deliberately NOT relaxed and is covered by HeartbeatTableChannelTests.
    /// </summary>
    public sealed class ServerHeartbeatBindingTests
    {
        private static readonly Regex NoncePattern = new Regex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

        [Fact]
        public async Task PlayerServerPing_CarriesAdmittedBinding_AndFreshNonce()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options =>
                {
                    options.HeartbeatIntervalMs = 500;
                    options.IdleDeadMs = 60_000;
                });
            TableTestClient client = await host.ConnectPlayerAsync("room-hb-player", "acct-hb-player", 2);
            Welcome welcome = client.Welcome!;

            Envelope first = await ReadServerPingAsync(client, TimeSpan.FromSeconds(10));
            AssertServerPing(first, welcome, "first player PING");
            string firstNonce = first.Heartbeat.Ping.Nonce;
            Assert.False(string.IsNullOrEmpty(first.SessionId));
            Assert.False(string.IsNullOrEmpty(first.RoomId));
            Assert.Equal(2, first.SeatId);
            Assert.Equal(welcome.BindingEpoch, first.BindingEpoch);

            // Echo exactly what the frozen Client echoes. The session must stay bound
            // and continue producing PINGs with fresh nonces on the same identity.
            await client.SendAsync(TableWire.HeartbeatPong(welcome, firstNonce));
            Envelope second = await ReadServerPingAsync(client, TimeSpan.FromSeconds(10));
            AssertServerPing(second, welcome, "second player PING");
            Assert.NotEqual(firstNonce, second.Heartbeat.Ping.Nonce);

            // The receive loop is still live after the PONG: a client PING is answered
            // with its exact nonce, so the PONG was accepted rather than closing the
            // session.
            string probe = TableWire.RandomNonce();
            await client.SendAsync(TableWire.HeartbeatPing(probe));
            Envelope pong = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(probe, pong.Heartbeat.Pong.Nonce);
            Assert.Equal(welcome.SessionId, pong.SessionId);
            Assert.Equal(welcome.RoomId, pong.RoomId);
            Assert.Equal(welcome.SeatId, pong.SeatId);
            Assert.Equal(welcome.BindingEpoch, pong.BindingEpoch);

            await client.DisposeAsync();
        }

        [Fact]
        public async Task SpectatorServerPing_CarriesAdmittedBindingAndNoSeat()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options =>
                {
                    options.HeartbeatIntervalMs = 500;
                    options.IdleDeadMs = 60_000;
                });
            TableTestClient spectator = await host.ConnectSpectatorAsync("room-hb-spec", "acct-hb-spec");
            Welcome welcome = spectator.Welcome!;

            Envelope first = await ReadServerPingAsync(spectator, TimeSpan.FromSeconds(10));
            AssertServerPing(first, welcome, "first spectator PING");
            Assert.Equal(ProtocolConstants.NoSeat, first.SeatId);
            Assert.Equal(ProtocolConstants.NoSeat, welcome.SeatId);
            Assert.NotEqual(0, first.SeatId);

            string firstNonce = first.Heartbeat.Ping.Nonce;
            await spectator.SendAsync(TableWire.HeartbeatPong(welcome, firstNonce));
            Envelope second = await ReadServerPingAsync(spectator, TimeSpan.FromSeconds(10));
            AssertServerPing(second, welcome, "second spectator PING");
            Assert.NotEqual(firstNonce, second.Heartbeat.Ping.Nonce);

            string probe = TableWire.RandomNonce();
            await spectator.SendAsync(TableWire.HeartbeatPing(probe));
            Envelope pong = await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(probe, pong.Heartbeat.Pong.Nonce);

            await spectator.DisposeAsync();
        }

        [Fact]
        public async Task ReboundConnection_PingsUseTheNewBinding_AndOldSessionIsRejected()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options =>
                {
                    options.HeartbeatIntervalMs = 500;
                    options.IdleDeadMs = 60_000;
                });
            TableTestClient first = await host.ConnectPlayerAsync(
                "room-hb-rebind",
                "acct-hb-rebind",
                0,
                new TableTestTicket(
                    "acct-hb-rebind",
                    "room-hb-rebind",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-hb-rebind-1"));
            Welcome firstWelcome = first.Welcome!;
            Envelope firstPing = await ReadServerPingAsync(first, TimeSpan.FromSeconds(10));
            AssertServerPing(firstPing, firstWelcome, "pre-rebind PING");

            // Same account/room/seat, newest valid ticket consumption wins (顶号).
            TableTestClient second = await host.ConnectPlayerAsync(
                "room-hb-rebind",
                "acct-hb-rebind",
                0,
                new TableTestTicket(
                    "acct-hb-rebind",
                    "room-hb-rebind",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-hb-rebind-2"));
            Welcome secondWelcome = second.Welcome!;
            Assert.NotEqual(firstWelcome.SessionId, secondWelcome.SessionId);
            Assert.Equal(firstWelcome.RoomId, secondWelcome.RoomId);
            Assert.Equal(firstWelcome.SeatId, secondWelcome.SeatId);
            Assert.Equal(firstWelcome.BindingEpoch, secondWelcome.BindingEpoch);

            // The displaced session is rejected with the frozen 4403 directive; it never
            // receives another PING carrying the NEW session identity.
            (int code, string reason, TableWireMessage[] drained) =
                await first.ExpectCloseAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(4403, code);
            Assert.Equal(ProtocolErrorCode.DisplacedByNewConnection, reason);
            foreach (TableWireMessage frame in drained)
            {
                Assert.True(
                    frame.Envelope == null ||
                    frame.Envelope.MessageType != MessageType.Heartbeat ||
                    frame.Envelope.SessionId != secondWelcome.SessionId,
                    "a displaced session received a heartbeat for the rebound identity");
            }

            Envelope reboundPing = await ReadServerPingAsync(second, TimeSpan.FromSeconds(10));
            AssertServerPing(reboundPing, secondWelcome, "post-rebind PING");
            Assert.NotEqual(firstPing.SessionId, reboundPing.SessionId);
            Assert.Equal(secondWelcome.SessionId, reboundPing.SessionId);
            string nonce = reboundPing.Heartbeat.Ping.Nonce;
            await second.SendAsync(TableWire.HeartbeatPong(secondWelcome, nonce));
            Envelope next = await ReadServerPingAsync(second, TimeSpan.FromSeconds(10));
            AssertServerPing(next, secondWelcome, "post-rebind second PING");
            Assert.NotEqual(nonce, next.Heartbeat.Ping.Nonce);

            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        private static void AssertServerPing(Envelope ping, Welcome welcome, string context)
        {
            Assert.True(
                ping.MessageType == MessageType.Heartbeat &&
                ping.Heartbeat != null &&
                ping.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping,
                context + " is not a heartbeat PING");
            Assert.Equal(welcome.SessionId, ping.SessionId);
            Assert.Equal(welcome.RoomId, ping.RoomId);
            Assert.Equal(welcome.SeatId, ping.SeatId);
            Assert.Equal(welcome.BindingEpoch, ping.BindingEpoch);
            Assert.True(
                NoncePattern.IsMatch(ping.Heartbeat.Ping.Nonce),
                context + " nonce is not 16 lowercase hex characters");
        }

        private static async Task<Envelope> ReadServerPingAsync(TableTestClient client, TimeSpan timeout)
        {
            long deadline = Stopwatch.GetTimestamp() +
                            (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(
                    Math.Max(0.001, (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
                TableWireMessage message = await client.ReceiveAsync(remaining);
                if (message.IsClose)
                {
                    throw new InvalidOperationException(
                        "Connection closed before the server PING: " +
                        message.WsCloseCode + " " + message.WsCloseReason);
                }

                Envelope envelope = message.Envelope!;
                if (envelope.MessageType == MessageType.Heartbeat &&
                    envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
                {
                    return envelope;
                }
            }

            throw new TimeoutException("The server did not emit a heartbeat PING in time.");
        }
    }
}

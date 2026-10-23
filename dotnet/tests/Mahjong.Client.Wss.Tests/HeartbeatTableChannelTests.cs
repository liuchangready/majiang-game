using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class HeartbeatTableChannelTests
    {
        [Fact]
        public async Task ValidPingIsEchoedAndNotReturnedToTheSession()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Inner.Enqueue(TestWire.HeartbeatPing(11, "0123456789abcdef"));
            fixture.Inner.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ReadOnlyMemory<byte> bytes = await channel.ReceiveAsync(cts.Token);

            Assert.Equal(MessageType.PlayerUpdate, TestWire.Decode(bytes).MessageType);
            Envelope pong = fixture.Inner.SentEnvelopes().Last();
            Assert.Equal(MessageType.Heartbeat, pong.MessageType);
            Assert.Equal(Heartbeat.BeatOneofCase.Pong, pong.Heartbeat.BeatCase);
            Assert.Equal("0123456789abcdef", pong.Heartbeat.Pong.Nonce);
            Assert.Equal(11UL, pong.BindingEpoch);
            Assert.Equal(TestWire.SessionId, pong.SessionId);
            Assert.Equal(TestWire.RoomId, pong.RoomId);
        }

        [Fact]
        public async Task HeartbeatBeforeWelcomeCannotBypassTheBinding()
        {
            var inner = new FakeInnerChannel();
            using var channel = new HeartbeatTableChannel(
                inner,
                new ManualTableClock(),
                new HeartbeatChannelOptions { AutoTimer = false });
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);
            inner.Enqueue(TestWire.HeartbeatPing(11, "0123456789abcdef"));

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task StaleWelcomeCannotMoveHeartbeatsToAnOlderBinding()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Inner.Enqueue(TestWire.Welcome(bindingEpoch: 5));
            fixture.Inner.Enqueue(TestWire.HeartbeatPing(5, "0123456789abcdef"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ReadOnlyMemory<byte> stale = await channel.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.Welcome, TestWire.Decode(stale).MessageType);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(cts.Token));

            Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task HeartbeatWithDifferentBindingIdentityIsRejected()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Inner.Enqueue(TestWire.HeartbeatPing(99, "0123456789abcdef"));

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task InvalidHeartbeatNonceIsRejectedAsMalformed()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            Envelope invalid = TestWire.HeartbeatPing(11, "0123456789abcdef");
            invalid.Heartbeat.Ping.Nonce = "not-16-hex";
            fixture.Inner.EnqueueRaw(invalid.ToByteArray());

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task PongMustMatchAnOutstandingClientPing()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Clock.Advance(100);
            int delay = await channel.PollHeartbeatAsync();
            Assert.True(delay > 0);
            Assert.NotNull(channel.Snapshot);
            string? nonce = channel.Snapshot!.PendingPingNonce;
            Assert.NotNull(nonce);

            fixture.Inner.Enqueue(TestWire.HeartbeatPong(11, "ffffffffffffffff"));
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task MatchingPongCompletesTheRoundTrip()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Clock.Advance(100);
            await channel.PollHeartbeatAsync();
            string nonce = channel.Snapshot!.PendingPingNonce!;

            fixture.Inner.Enqueue(TestWire.HeartbeatPong(11, nonce));
            fixture.Inner.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ReadOnlyMemory<byte> bytes = await channel.ReceiveAsync(cts.Token);

            Assert.Equal(MessageType.PlayerUpdate, TestWire.Decode(bytes).MessageType);
            HeartbeatSnapshot snapshot = channel.Snapshot!;
            Assert.Equal(1, snapshot.PongReceivedCount);
            Assert.Null(snapshot.PendingPingNonce);
            Assert.True(snapshot.LastRoundTripMs >= 0);
        }

        [Fact]
        public async Task NegotiatedIntervalAndIdleDeadlineAreAdopted()
        {
            var fixture = await OpenBoundAsync(heartbeatIntervalMs: 50, idleDeadMs: 150);
            using var channel = fixture.Channel;

            HeartbeatSnapshot snapshot = channel.Snapshot!;

            Assert.True(snapshot.Bound);
            Assert.Equal(50, snapshot.NegotiatedIntervalMs);
            Assert.Equal(150, snapshot.NegotiatedIdleDeadMs);
        }

        [Fact]
        public async Task NoClientPingIsSentBeforeWelcome()
        {
            var inner = new FakeInnerChannel();
            var clock = new ManualTableClock();
            using var channel = new HeartbeatTableChannel(
                inner,
                clock,
                new HeartbeatChannelOptions { AutoTimer = false });
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);

            clock.Advance(500);
            int delay = await channel.PollHeartbeatAsync();

            Assert.True(delay > 0);
            Assert.Empty(inner.SentEnvelopes());
        }

        [Fact]
        public async Task ClientPingUsesSixteenLowercaseHexAndOnlyOneOutstandingNonce()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Clock.Advance(100);

            await channel.PollHeartbeatAsync();
            Envelope ping = fixture.Inner.SentEnvelopes().Single();
            Assert.Equal(Heartbeat.BeatOneofCase.Ping, ping.Heartbeat.BeatCase);
            Assert.Equal(16, ping.Heartbeat.Ping.Nonce.Length);
            Assert.All(ping.Heartbeat.Ping.Nonce, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));

            await channel.PollHeartbeatAsync();
            Assert.Single(fixture.Inner.SentEnvelopes());
        }

        [Fact]
        public async Task ClientKeepsPingingWhenAPongIsLost()
        {
            var fixture = await OpenBoundAsync();
            using var channel = fixture.Channel;
            fixture.Clock.Advance(100);
            await channel.PollHeartbeatAsync();
            string firstNonce = fixture.Inner.SentEnvelopes().Single().Heartbeat.Ping.Nonce;

            fixture.Clock.Advance(100);
            await channel.PollHeartbeatAsync();

            Assert.Equal(2, fixture.Inner.SentEnvelopes().Count);
            fixture.Inner.Enqueue(TestWire.HeartbeatPong(11, firstNonce));
            fixture.Inner.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await channel.ReceiveAsync(cts.Token);

            Assert.Equal(1, channel.Snapshot!.PongReceivedCount);
            Assert.Equal(2, channel.Snapshot!.PingSentCount);
        }

        [Fact]
        public async Task IdleDeadlineStopsTheHeartbeatSession()
        {
            var fixture = await OpenBoundAsync(heartbeatIntervalMs: 50, idleDeadMs: 150);
            using var channel = fixture.Channel;
            fixture.Clock.Advance(151);

            int delay = await channel.PollHeartbeatAsync();

            Assert.Equal(-1, delay);
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));
            Assert.Equal(ProtocolErrorCode.HeartbeatTimeout, ex.Code);
            Assert.False(ex.Permanent);
            Assert.Equal(1, fixture.Inner.CloseCount);
        }

        [Fact]
        public async Task DisposeStopsTheHeartbeatSessionAndFurtherPings()
        {
            var fixture = await OpenBoundAsync();
            fixture.Channel.Dispose();

            int delay = await fixture.Channel.PollHeartbeatAsync();

            Assert.Equal(-1, delay);
            Assert.Empty(fixture.Inner.SentEnvelopes());
            await Assert.ThrowsAsync<TableChannelClosedException>(
                () => fixture.Channel.ReceiveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task DuplicateHigherWelcomeCannotRebindControlAwayFromAcceptedSession()
        {
            using var inner = new ScriptedTableChannel();
            using var heartbeat = new HeartbeatTableChannel(
                inner,
                new ManualTableClock(),
                new HeartbeatChannelOptions { AutoTimer = false });
            var session = new TableSession(heartbeat, new InMemoryOutboxStore());
            await session.OpenAsync(
                new TableSessionOpenRequest("ticket", "test"),
                CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(11UL, session.Binding!.BindingEpoch.Value);

            inner.Enqueue(TestWire.Welcome(bindingEpoch: 99));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(11UL, session.Binding!.BindingEpoch.Value);

            inner.Enqueue(TestWire.HeartbeatPing(11, "0123456789abcdef"));
            inner.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Contains(
                inner.SentEnvelopes(),
                envelope => envelope.MessageType == MessageType.Heartbeat && envelope.BindingEpoch == 11);
        }

        [Fact]
        public async Task IdentityChangingDuplicateWelcomeIsIgnoredByControl()
        {
            var inner = new FakeInnerChannel();
            using var channel = new HeartbeatTableChannel(
                inner,
                new ManualTableClock(),
                new HeartbeatChannelOptions { AutoTimer = false });
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);
            inner.Enqueue(TestWire.Welcome(bindingEpoch: 11));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            Envelope changed = TestWire.Welcome(bindingEpoch: 12);
            changed.SessionId = "other-session";
            changed.RoomId = "other-room";
            changed.Welcome.SessionId = "other-session";
            changed.Welcome.RoomId = "other-room";
            inner.Enqueue(changed);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            inner.Enqueue(TestWire.HeartbeatPing(11, "0123456789abcdef"));
            inner.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                ReadOnlyMemory<byte> bytes = await channel.ReceiveAsync(cts.Token);
                Assert.Equal(MessageType.PlayerUpdate, TestWire.Decode(bytes).MessageType);
            }

            Envelope pong = inner.SentEnvelopes().Last();
            Assert.Equal(MessageType.Heartbeat, pong.MessageType);
            Assert.Equal(11UL, pong.BindingEpoch);
        }

        [Fact]
        public async Task InvalidWelcomeAfterBindingDoesNotChangeControlIdentity()
        {
            var inner = new FakeInnerChannel();
            using var channel = new HeartbeatTableChannel(
                inner,
                new ManualTableClock(),
                new HeartbeatChannelOptions { AutoTimer = false });
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);
            inner.Enqueue(TestWire.Welcome(bindingEpoch: 11));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            Envelope invalid = TestWire.Welcome(bindingEpoch: 12);
            invalid.Welcome.SessionId = string.Empty;
            invalid.SessionId = string.Empty;
            inner.Enqueue(invalid);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            inner.Enqueue(TestWire.HeartbeatPing(11, "0123456789abcdef"));
            inner.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            Envelope pong = inner.SentEnvelopes().Last();
            Assert.Equal(MessageType.Heartbeat, pong.MessageType);
            Assert.Equal(11UL, pong.BindingEpoch);
        }

        [Fact]
        public async Task RealReopenAcceptsANewBinding()
        {
            var inner = new FakeInnerChannel();
            using var channel = new HeartbeatTableChannel(
                inner,
                new ManualTableClock(),
                new HeartbeatChannelOptions { AutoTimer = false });
            var request = new TableChannelConnectRequest("ticket-1", "test-build", null);
            await channel.OpenAsync(request, CancellationToken.None);
            inner.Enqueue(TestWire.Welcome(bindingEpoch: 11));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            await channel.OpenAsync(request, CancellationToken.None);
            inner.Enqueue(TestWire.Welcome(bindingEpoch: 12));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            inner.Enqueue(TestWire.HeartbeatPing(12, "0123456789abcdef"));
            inner.Enqueue(TestWire.PlayerPush(12, 5, 10, 6, 11));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            Envelope pong = inner.SentEnvelopes().Last();
            Assert.Equal(MessageType.Heartbeat, pong.MessageType);
            Assert.Equal(12UL, pong.BindingEpoch);
        }

        private static async Task<Fixture> OpenBoundAsync(uint heartbeatIntervalMs = 100, uint idleDeadMs = 300)
        {
            var inner = new FakeInnerChannel();
            var clock = new ManualTableClock();
            var channel = new HeartbeatTableChannel(
                inner,
                clock,
                new HeartbeatChannelOptions
                {
                    AutoTimer = false,
                    DefaultHeartbeatIntervalMs = 100,
                    DefaultIdleDeadMs = 300
                });
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);
            inner.Enqueue(TestWire.Welcome(
                bindingEpoch: 11,
                heartbeatIntervalMs: heartbeatIntervalMs,
                idleDeadMs: idleDeadMs));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await channel.ReceiveAsync(cts.Token);
            }

            return new Fixture(channel, inner, clock);
        }

        private sealed class Fixture : IDisposable
        {
            public Fixture(HeartbeatTableChannel channel, FakeInnerChannel inner, ManualTableClock clock)
            {
                Channel = channel;
                Inner = inner;
                Clock = clock;
            }

            public HeartbeatTableChannel Channel { get; }

            public FakeInnerChannel Inner { get; }

            public ManualTableClock Clock { get; }

            public void Dispose()
            {
                Channel.Dispose();
            }
        }
    }
}

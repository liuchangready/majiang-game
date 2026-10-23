using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class RealEndpointHeartbeatAndRunnerTests
    {
        [Fact]
        public async Task RealEndpointEchoesWirePingAndAcceptsMatchingClientPong()
        {
            await using var server = new LocalWebSocketServer();
            var clientPing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clientPong = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            const string ServerNonce = "fedcba9876543210";

            server.Handler = async (index, connection) =>
            {
                Envelope hello = await connection.ReadEnvelopeAsync(CancellationToken.None);
                Assert.Equal(MessageType.Hello, hello.MessageType);
                await connection.SendBinaryAsync(
                    TestWire.Encode(TestWire.Welcome(
                        bindingEpoch: 11,
                        heartbeatIntervalMs: 100,
                        idleDeadMs: 1000)),
                    CancellationToken.None);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    while (!clientPong.Task.IsCompleted)
                    {
                        Envelope envelope = await connection.ReadEnvelopeAsync(timeout.Token);
                        if (envelope.BodyCase == Envelope.BodyOneofCase.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
                        {
                            clientPing.TrySetResult(envelope.Heartbeat.Ping.Nonce);
                            await connection.SendBinaryAsync(
                                TestWire.Encode(TestWire.HeartbeatPong(11, envelope.Heartbeat.Ping.Nonce)),
                                CancellationToken.None);
                            await connection.SendBinaryAsync(
                                TestWire.Encode(TestWire.HeartbeatPing(11, ServerNonce)),
                                CancellationToken.None);
                            await connection.SendBinaryAsync(
                                TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)),
                                CancellationToken.None);
                        }
                        else if (envelope.BodyCase == Envelope.BodyOneofCase.Heartbeat &&
                                 envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong)
                        {
                            if (string.Equals(envelope.Heartbeat.Pong.Nonce, ServerNonce, StringComparison.Ordinal))
                            {
                                clientPong.TrySetResult(envelope.Heartbeat.Pong.Nonce);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Test teardown.
                }
                catch (EndOfStreamException)
                {
                    // Test teardown.
                }
            };

            var inner = new WebSocketTableChannel(new WebSocketTableChannelOptions(server.Endpoint)
            {
                AllowInsecureLocalWebSocket = true,
                ReceiveMessageTimeout = TimeSpan.FromSeconds(10)
            });
            using var channel = new HeartbeatTableChannel(
                inner,
                new SystemTableClientClock(),
                new HeartbeatChannelOptions
                {
                    AutoTimer = true,
                    DefaultHeartbeatIntervalMs = 100,
                    DefaultIdleDeadMs = 1000,
                    MaxTimerSleepMs = 20
                });
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);
            await inner.SendAsync(
                TestWire.Encode(ClientWireMapper.NewHello("ticket-1", "test-build", false, null, 1, "hello-1")),
                CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ReadOnlyMemory<byte> welcome = await channel.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.Welcome, TestWire.Decode(welcome).MessageType);

            ReadOnlyMemory<byte> business = await channel.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.PlayerUpdate, TestWire.Decode(business).MessageType);
            await clientPing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clientPong.Task.WaitAsync(TimeSpan.FromSeconds(5));

            HeartbeatSnapshot snapshot = channel.Snapshot!;
            Assert.True(snapshot.PongReceivedCount >= 1);
            Assert.Null(snapshot.PendingPingNonce);
        }

        [Fact]
        public async Task RealEndpointRunnerReconnectsAndRequestsAFreshTicket()
        {
            await using var server = new LocalWebSocketServer();
            var tickets = new System.Collections.Generic.List<string>();
            server.Handler = async (index, connection) =>
            {
                Envelope hello = await connection.ReadEnvelopeAsync(CancellationToken.None);
                Assert.Equal(MessageType.Hello, hello.MessageType);
                int epoch = 10 + index;
                await connection.SendBinaryAsync(
                    TestWire.Encode(TestWire.Welcome(
                        bindingEpoch: epoch,
                        heartbeatIntervalMs: 100,
                        idleDeadMs: 1000)),
                    CancellationToken.None);
                Envelope sync = await connection.ReadEnvelopeAsync(CancellationToken.None);
                Assert.Equal(MessageType.SyncRequest, sync.MessageType);
                await connection.SendBinaryAsync(
                    TestWire.Encode(TestWire.PlayerSync(epoch, sync.CorrelationId)),
                    CancellationToken.None);

                if (index == 1)
                {
                    // Wait until the client proves the baseline is live by
                    // sending a Wire heartbeat, then drop the TCP connection.
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await connection.ReadEnvelopeAsync(wait.Token);
                    }
                    catch (Exception)
                    {
                        // Fall through to the deliberate transport loss.
                    }

                    connection.Abort();
                    return;
                }

                using var hold = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await connection.ReadEnvelopeAsync(hold.Token);
                }
                catch (Exception)
                {
                    // Test teardown.
                }
            };

            var inner = new WebSocketTableChannel(new WebSocketTableChannelOptions(server.Endpoint)
            {
                AllowInsecureLocalWebSocket = true,
                ReceiveMessageTimeout = TimeSpan.FromSeconds(10)
            });
            var heartbeat = new HeartbeatTableChannel(
                inner,
                new SystemTableClientClock(),
                new HeartbeatChannelOptions
                {
                    AutoTimer = true,
                    DefaultHeartbeatIntervalMs = 100,
                    DefaultIdleDeadMs = 1000,
                    MaxTimerSleepMs = 20
                });
            var session = new TableSession(heartbeat, new InMemoryOutboxStore());
            var provider = new DelegateTableTicketProvider((request, cancellationToken) =>
            {
                lock (tickets)
                {
                    tickets.Add("ticket-" + request.Attempt);
                    return Task.FromResult(tickets[tickets.Count - 1]);
                }
            });
            var runner = new TableSessionRunner(
                session,
                provider,
                new TableSessionRunnerOptions
                {
                    InitialBackoff = TimeSpan.FromMilliseconds(50),
                    MaxBackoff = TimeSpan.FromMilliseconds(200),
                    MaxReconnectAttempts = 5,
                    HandshakeTimeout = TimeSpan.FromSeconds(4)
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<TableSessionRunResult> run = runner.RunAsync(cts.Token);
            await TestWire.WaitUntilAsync(
                () => session.State == TableConnectionState.Ready && server.AcceptedConnections >= 2,
                TimeSpan.FromSeconds(12),
                "real endpoint reconnect baseline");

            lock (tickets)
            {
                Assert.True(tickets.Count >= 2);
                Assert.NotEqual(tickets[0], tickets[1]);
            }

            cts.Cancel();
            TableSessionRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
        }
    }
}

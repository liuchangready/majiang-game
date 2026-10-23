using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class WebSocketTableChannelIntegrationTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task FragmentedBinaryMessageIsReassembledIntoExactlyOneEnvelope()
        {
            await using var server = new LocalWebSocketServer();
            byte[] encoded = TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11));
            server.Handler = async (index, connection) =>
            {
                int first = encoded.Length / 3;
                int second = (encoded.Length * 2) / 3;
                await connection.SendFragmentedBinaryAsync(
                    new List<byte[]>
                    {
                        encoded[..first],
                        encoded[first..second],
                        encoded[second..]
                    },
                    CancellationToken.None);
                await HoldAsync(connection);
            };

            using var channel = await OpenAsync(server);
            using var cts = new CancellationTokenSource(TestTimeout);
            ReadOnlyMemory<byte> received = await channel.ReceiveAsync(cts.Token);

            Assert.Equal(encoded, received.ToArray());
            Envelope envelope = TestWire.Decode(received);
            Assert.Equal(MessageType.PlayerUpdate, envelope.MessageType);
            Assert.Equal(6UL, envelope.PlayerUpdate.Cursor.StreamVersion);
        }

        [Fact]
        public async Task TwoConsecutiveMessagesAreNotMerged()
        {
            await using var server = new LocalWebSocketServer();
            byte[] first = TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11));
            byte[] second = TestWire.Encode(TestWire.PlayerPush(11, 6, 11, 7, 12));
            server.Handler = async (index, connection) =>
            {
                await connection.SendBinaryAsync(first, CancellationToken.None);
                await connection.SendBinaryAsync(second, CancellationToken.None);
                await HoldAsync(connection);
            };

            using var channel = await OpenAsync(server);
            using var cts = new CancellationTokenSource(TestTimeout);
            ReadOnlyMemory<byte> one = await channel.ReceiveAsync(cts.Token);
            ReadOnlyMemory<byte> two = await channel.ReceiveAsync(cts.Token);

            Assert.Equal(first, one.ToArray());
            Assert.Equal(second, two.ToArray());
            Assert.Equal(6UL, TestWire.Decode(one).PlayerUpdate.Cursor.StreamVersion);
            Assert.Equal(7UL, TestWire.Decode(two).PlayerUpdate.Cursor.StreamVersion);
        }

        [Fact]
        public async Task EmptyBinaryMessageIsRejectedAsMalformed()
        {
            await using var server = new LocalWebSocketServer();
            server.Handler = async (index, connection) =>
            {
                await connection.SendEmptyBinaryAsync(CancellationToken.None);
                await HoldAsync(connection);
            };

            using var channel = await OpenAsync(server);
            using var cts = new CancellationTokenSource(TestTimeout);
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(cts.Token));

            Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task TextMessageIsRejectedAsProtocolViolation()
        {
            await using var server = new LocalWebSocketServer();
            server.Handler = async (index, connection) =>
            {
                await connection.SendTextAsync("not-a-binary-envelope", CancellationToken.None);
                await HoldAsync(connection);
            };

            using var channel = await OpenAsync(server);
            using var cts = new CancellationTokenSource(TestTimeout);
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(cts.Token));

            Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task OversizedMessageIsRejectedWithoutPartialDelivery()
        {
            await using var server = new LocalWebSocketServer();
            byte[] payload = new byte[128];
            server.Handler = async (index, connection) =>
            {
                await connection.SendBinaryAsync(payload, CancellationToken.None);
                await HoldAsync(connection);
            };

            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                AllowInsecureLocalWebSocket = true,
                MaxMessageBytes = 64,
                ReceiveMessageTimeout = TestTimeout
            };
            using var channel = new WebSocketTableChannel(options);
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);

            using var cts = new CancellationTokenSource(TestTimeout);
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(cts.Token));

            Assert.Equal(ProtocolErrorCode.MessageTooLarge, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task TruncatedMessageAfterFragmentsIsTransportLoss()
        {
            await using var server = new LocalWebSocketServer();
            byte[] encoded = TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11));
            server.Handler = async (index, connection) =>
            {
                int split = encoded.Length / 2;
                await connection.SendRawFrameAsync(false, 0x2, encoded[..split], CancellationToken.None);
                connection.Abort();
            };

            using var channel = await OpenAsync(server);
            using var cts = new CancellationTokenSource(TestTimeout);
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(cts.Token));

            Assert.Equal(WebSocketChannelReason.TruncatedMessage, ex.Code);
            Assert.False(ex.Permanent);
        }

        [Fact]
        public async Task SlowTricklingMessageHitsTheReceiveTimeout()
        {
            await using var server = new LocalWebSocketServer();
            byte[] encoded = TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11));
            server.Handler = async (index, connection) =>
            {
                await connection.SendRawFrameAsync(false, 0x2, encoded[..8], CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(2));
            };

            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                AllowInsecureLocalWebSocket = true,
                ReceiveMessageTimeout = TimeSpan.FromMilliseconds(100)
            };
            using var channel = new WebSocketTableChannel(options);
            await channel.OpenAsync(new TableChannelConnectRequest("ticket-1", "test-build", null), CancellationToken.None);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.ReceiveTimeout, ex.Code);
            Assert.False(ex.Permanent);
        }

        [Fact]
        public async Task ServerCloseFrameMapsToPermanentProtocolViolation()
        {
            await using var server = new LocalWebSocketServer();
            server.Handler = async (index, connection) =>
            {
                await connection.SendCloseAsync(4400, CancellationToken.None);
                await Task.Delay(100);
            };

            using var channel = await OpenAsync(server);
            using var cts = new CancellationTokenSource(TestTimeout);
            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(cts.Token));

            Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task SecondConcurrentReceiveOwnerIsRejected()
        {
            await using var server = new LocalWebSocketServer();
            server.Handler = async (index, connection) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            };

            using var channel = await OpenAsync(server);
            using var firstCts = new CancellationTokenSource(TestTimeout);
            Task<ReadOnlyMemory<byte>> first = channel.ReceiveAsync(firstCts.Token);
            await Task.Delay(50);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            firstCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }

        [Fact]
        public async Task ConcurrentSendsAreSerializedIntoWholeMessages()
        {
            await using var server = new LocalWebSocketServer();
            const int MessageCount = 10;
            var received = new List<int>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Handler = async (index, connection) =>
            {
                try
                {
                    for (int i = 0; i < MessageCount; i++)
                    {
                        byte[] message = await connection.ReadMessageAsync(CancellationToken.None);
                        received.Add(message.Length);
                    }

                    done.TrySetResult(true);
                    await HoldAsync(connection);
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            };

            using var channel = await OpenAsync(server);
            var payloads = new List<byte[]>();
            for (int i = 0; i < MessageCount; i++)
            {
                payloads.Add(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, (ulong)(6 + i), (ulong)(11 + i))));
            }

            Task[] sends = new Task[MessageCount];
            for (int i = 0; i < MessageCount; i++)
            {
                byte[] payload = payloads[i];
                sends[i] = channel.SendAsync(payload, CancellationToken.None);
            }

            await Task.WhenAll(sends).WaitAsync(TestTimeout);
            await done.Task.WaitAsync(TestTimeout);

            Assert.Equal(MessageCount, received.Count);
            for (int i = 0; i < MessageCount; i++)
            {
                Assert.Equal(payloads[i].Length, received[i]);
            }
        }

        [Fact]
        public async Task ChannelCanReopenAndOldListenerSeesANewConnection()
        {
            await using var server = new LocalWebSocketServer();
            byte[] encoded = TestWire.Encode(TestWire.PlayerPush(12, 6, 11, 7, 12));
            server.Handler = async (index, connection) =>
            {
                if (index >= 2)
                {
                    await connection.SendBinaryAsync(encoded, CancellationToken.None);
                }

                await HoldAsync(connection);
            };

            using var channel = await OpenAsync(server);
            await channel.CloseAsync(new TableChannelCloseReason(ProtocolErrorCode.LocalClosed, false), CancellationToken.None);
            Assert.False(channel.IsOpen);

            await channel.OpenAsync(new TableChannelConnectRequest("ticket-2", "test-build", null), CancellationToken.None);
            using var cts = new CancellationTokenSource(TestTimeout);
            ReadOnlyMemory<byte> received = await channel.ReceiveAsync(cts.Token);

            Assert.Equal(encoded, received.ToArray());
            Assert.True(server.AcceptedConnections >= 2);
        }

        private static async Task<WebSocketTableChannel> OpenAsync(LocalWebSocketServer server)
        {
            var options = new WebSocketTableChannelOptions(server.Endpoint)
            {
                AllowInsecureLocalWebSocket = true,
                ReceiveMessageTimeout = TestTimeout
            };
            var channel = new WebSocketTableChannel(options);
            await channel.OpenAsync(
                new TableChannelConnectRequest("ticket-1", "test-build", null),
                CancellationToken.None);
            return channel;
        }

        private static async Task HoldAsync(WebSocketServerConnection connection)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await connection.ReadMessageAsync(cts.Token);
            }
            catch (Exception)
            {
                // The client closed or the hold timed out.
            }
        }
    }
}

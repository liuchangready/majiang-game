using System;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Wss.Tests
{
    public sealed class WebSocketTableChannelGuardTests
    {
        [Fact]
        public async Task CodexReopenGapMustReportStaleToOldSender()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options => options.SendTimeout = TimeSpan.FromSeconds(5));
            await channel.OpenAsync(Request(), CancellationToken.None);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.SendHandler = async (buffer, type, end, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            };
            Task oldSend = channel.SendAsync(new byte[] { 1 }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var creating = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCreate = new ManualResetEventSlim(false);
            factory.Configure = socket =>
            {
                creating.TrySetResult(true);
                if (!releaseCreate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("review barrier");
            };
            Task reopen = Task.Run(() => channel.OpenAsync(Request(), CancellationToken.None));
            await creating.Task.WaitAsync(TimeSpan.FromSeconds(2));
            TableChannelClosedException oldError;
            try
            {
                oldError = await Assert.ThrowsAsync<TableChannelClosedException>(() => oldSend);
            }
            finally
            {
                releaseCreate.Set();
                await reopen;
            }
            Assert.True(channel.IsOpen);
            Assert.Equal(WebSocketChannelReason.StaleConnection, oldError.Code);
        }

        [Fact]
        public async Task ConnectTimeoutIsReportedAsTransientHandshakeTimeout()
        {
            var factory = new FakeWebSocketConnectionFactory();
            factory.Prepare(connection =>
                connection.ConnectHandler = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken));
            using var channel = Create(factory, options => options.ConnectTimeout = TimeSpan.FromMilliseconds(80));

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.OpenAsync(Request(), CancellationToken.None));

            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, ex.Code);
            Assert.False(ex.Permanent);
            Assert.True(factory.Last.Aborted);
        }

        [Fact]
        public async Task TlsFailureIsPermanentAndDoesNotRetryForever()
        {
            var factory = new FakeWebSocketConnectionFactory();
            factory.Prepare(connection =>
                connection.ConnectHandler = cancellationToken =>
                    throw new WebSocketException("certificate rejected", new AuthenticationException("untrusted")));
            using var channel = Create(factory);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.OpenAsync(Request(), CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.TlsHandshakeFailed, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task SendAdmissionIsBoundedAndTimesOutWithSlowConsumer()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options =>
            {
                options.MaxPendingSends = 1;
                options.SendTimeout = TimeSpan.FromMilliseconds(100);
            });
            await channel.OpenAsync(Request(), CancellationToken.None);

            var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };

            Task first = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None);
            await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            TableChannelClosedException secondEx = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None));
            TableChannelClosedException firstEx = await Assert.ThrowsAsync<TableChannelClosedException>(() => first);

            // The first sender's timeout retired the shared connection; the
            // queued sender must report the backpressure cause it observed,
            // not STALE_CONNECTION merely because retire raced its admission.
            Assert.Equal(ProtocolErrorCode.SlowConsumer, secondEx.Code);
            Assert.Equal(ProtocolErrorCode.SlowConsumer, firstEx.Code);
            Assert.False(secondEx.Permanent);
            Assert.False(firstEx.Permanent);
        }

        [Fact]
        public async Task IndependentSenderTimeoutIsSlowConsumer()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options =>
            {
                options.MaxPendingSends = 1;
                options.SendTimeout = TimeSpan.FromMilliseconds(80);
            });
            await channel.OpenAsync(Request(), CancellationToken.None);

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };

            Task send = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(() => send);

            Assert.Equal(ProtocolErrorCode.SlowConsumer, ex.Code);
            Assert.False(ex.Permanent);
            Assert.False(channel.IsOpen);
            Assert.Equal(0, channel.PayloadCopiesInFlight);
        }

        [Fact]
        public async Task ExplicitCloseIsReportedAsLocalClosedToQueuedAndActiveSenders()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options =>
            {
                options.MaxPendingSends = 1;
                options.SendTimeout = TimeSpan.FromSeconds(2);
            });
            await channel.OpenAsync(Request(), CancellationToken.None);

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };

            Task first = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task second = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None);

            await channel.CloseAsync(
                new TableChannelCloseReason(ProtocolErrorCode.LocalClosed, false),
                CancellationToken.None);

            TableChannelClosedException firstEx = await Assert.ThrowsAsync<TableChannelClosedException>(() => first);
            TableChannelClosedException secondEx = await Assert.ThrowsAsync<TableChannelClosedException>(() => second);
            Assert.Equal(ProtocolErrorCode.LocalClosed, firstEx.Code);
            Assert.Equal(ProtocolErrorCode.LocalClosed, secondEx.Code);
            Assert.False(firstEx.Permanent);
            Assert.False(secondEx.Permanent);
        }

        [Fact]
        public async Task ReopenReportsStaleOnlyToOldSendersAndNewConnectionWorks()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options =>
            {
                options.MaxPendingSends = 1;
                options.SendTimeout = TimeSpan.FromSeconds(2);
            });
            await channel.OpenAsync(Request(), CancellationToken.None);

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };

            Task oldSend = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await channel.OpenAsync(Request(), CancellationToken.None);
            factory.Last.SendHandler = null;

            TableChannelClosedException oldEx = await Assert.ThrowsAsync<TableChannelClosedException>(() => oldSend);
            Assert.Equal(WebSocketChannelReason.StaleConnection, oldEx.Code);

            await channel.SendAsync(
                TestWire.Encode(TestWire.PlayerPush(11, 6, 11, 7, 12)),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(channel.IsOpen);
        }

        [Fact]
        public async Task PayloadCopiesAreBoundedBySendAdmission()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options =>
            {
                options.MaxPendingSends = 2;
                options.SendTimeout = TimeSpan.FromSeconds(5);
            });
            await channel.OpenAsync(Request(), CancellationToken.None);

            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };

            using var cts = new CancellationTokenSource();
            var sends = new Task[20];
            for (int i = 0; i < sends.Length; i++)
            {
                sends[i] = channel.SendAsync(
                    TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)),
                    cts.Token);
            }

            await TestWire.WaitUntilAsync(
                () => channel.MaxPayloadCopiesInFlight >= 2,
                TimeSpan.FromSeconds(2),
                "two admitted payload copies");
            Assert.Equal(2, channel.AdmittedPayloadCopies);
            Assert.True(channel.MaxPayloadCopiesInFlight <= 2);

            cts.Cancel();
            for (int i = 0; i < sends.Length; i++)
            {
                try
                {
                    await sends[i];
                }
                catch (Exception)
                {
                    // Cancellation/retire is expected.
                }
            }

            Assert.Equal(0, channel.PayloadCopiesInFlight);
        }

        [Fact]
        public async Task SendsAreSerializedByASingleGate()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory);
            await channel.OpenAsync(Request(), CancellationToken.None);

            int concurrent = 0;
            int maximum = 0;
            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref concurrent);
                InterlockedExtensions.Max(ref maximum, current);
                await Task.Delay(15, cancellationToken);
                Interlocked.Decrement(ref concurrent);
            };

            Task[] sends = new Task[10];
            for (int i = 0; i < sends.Length; i++)
            {
                sends[i] = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), CancellationToken.None);
            }

            await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, maximum);
        }

        [Fact]
        public async Task StalledMessageTimesOutInsteadOfBufferingForever()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options => options.ReceiveMessageTimeout = TimeSpan.FromMilliseconds(80));
            await channel.OpenAsync(Request(), CancellationToken.None);
            factory.Last.ReceiveHandler = async (buffer, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            };

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            Assert.Equal(WebSocketChannelReason.ReceiveTimeout, ex.Code);
            Assert.False(ex.Permanent);
        }

        [Fact]
        public async Task BytesFromRetiredConnectionCannotReachTheNewBinding()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory);
            await channel.OpenAsync(Request(), CancellationToken.None);

            var firstReceive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.ReceiveHandler = async (buffer, cancellationToken) =>
            {
                firstReceive.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false); // ignores cancellation on purpose
                buffer.Array![buffer.Offset] = 0x41;
                return new WebSocketReceiveResult(1, WebSocketMessageType.Binary, true);
            };

            Task<ReadOnlyMemory<byte>> oldReceive = channel.ReceiveAsync(CancellationToken.None);
            await firstReceive.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await channel.OpenAsync(Request(), CancellationToken.None);
            factory.Last.ReceiveHandler = (buffer, cancellationToken) =>
            {
                buffer.Array![buffer.Offset] = 0x42;
                return Task.FromResult(new WebSocketReceiveResult(1, WebSocketMessageType.Binary, true));
            };
            releaseFirst.TrySetResult(true);

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(() => oldReceive);
            Assert.Equal(WebSocketChannelReason.StaleConnection, ex.Code);

            ReadOnlyMemory<byte> fresh = await channel.ReceiveAsync(CancellationToken.None);
            Assert.Equal(new byte[] { 0x42 }, fresh.ToArray());
        }

        [Fact]
        public async Task EmptyMessageIsRejectedAsMalformed()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory);
            await channel.OpenAsync(Request(), CancellationToken.None);
            factory.Last.ReceiveHandler = (buffer, cancellationToken) =>
                Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Binary, true));

            TableChannelClosedException empty = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, empty.Code);
            Assert.True(empty.Permanent);
        }

        [Fact]
        public async Task TextMessageIsRejectedAsProtocolViolation()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory);
            await channel.OpenAsync(Request(), CancellationToken.None);
            factory.Last.ReceiveHandler = (buffer, cancellationToken) =>
                Task.FromResult(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));

            TableChannelClosedException text = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, text.Code);
            Assert.True(text.Permanent);
        }

        [Fact]
        public async Task OversizedFragmentIsRejectedBeforeItCanBeAccumulated()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options => options.MaxMessageBytes = 8);
            await channel.OpenAsync(Request(), CancellationToken.None);
            factory.Last.ReceiveHandler = (buffer, cancellationToken) =>
                Task.FromResult(new WebSocketReceiveResult(9, WebSocketMessageType.Binary, false));

            TableChannelClosedException ex = await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));

            Assert.Equal(ProtocolErrorCode.MessageTooLarge, ex.Code);
            Assert.True(ex.Permanent);
        }

        [Fact]
        public async Task DisposeClosesTheCurrentConnectionAndPreventsReopen()
        {
            var factory = new FakeWebSocketConnectionFactory();
            var channel = Create(factory);
            await channel.OpenAsync(Request(), CancellationToken.None);

            channel.Dispose();

            Assert.False(channel.IsOpen);
            Assert.True(factory.Last.Aborted);
            await Assert.ThrowsAsync<TableChannelClosedException>(
                () => channel.ReceiveAsync(CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => channel.OpenAsync(Request(), CancellationToken.None));
        }

        [Fact]
        public async Task CallerCancellationOfSendRetiresTheSocket()
        {
            var factory = new FakeWebSocketConnectionFactory();
            using var channel = Create(factory, options => options.SendTimeout = TimeSpan.FromSeconds(5));
            await channel.OpenAsync(Request(), CancellationToken.None);

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Last.SendHandler = async (buffer, type, endOfMessage, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };

            using var cts = new CancellationTokenSource();
            Task send = channel.SendAsync(TestWire.Encode(TestWire.PlayerPush(11, 5, 10, 6, 11)), cts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            Assert.False(channel.IsOpen);
            Assert.True(factory.Last.Aborted);
        }

        private static WebSocketTableChannel Create(
            FakeWebSocketConnectionFactory factory,
            Action<WebSocketTableChannelOptions>? configure = null)
        {
            var options = new WebSocketTableChannelOptions(new Uri("wss://localhost/"))
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                SendTimeout = TimeSpan.FromSeconds(2),
                ReceiveMessageTimeout = TimeSpan.FromSeconds(2)
            };
            configure?.Invoke(options);
            return new WebSocketTableChannel(options, factory);
        }

        private static TableChannelConnectRequest Request()
        {
            return new TableChannelConnectRequest("ticket-1", "test-build", null);
        }

        private static class InterlockedExtensions
        {
            public static void Max(ref int target, int value)
            {
                int current;
                do
                {
                    current = Volatile.Read(ref target);
                    if (value <= current)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref target, value, current) != current);
            }
        }
    }
}

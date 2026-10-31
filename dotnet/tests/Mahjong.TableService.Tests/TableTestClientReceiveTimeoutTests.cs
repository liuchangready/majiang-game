using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Real regressions for the TableTestClient read-timeout contract (D-A68-01/D-A68-06):
    /// a wait timeout may abandon only that wait, never the socket-level receive. Both a real
    /// loopback ClientWebSocket and a scripted WebSocket drive the actual helper methods; no
    /// algorithm is copied here.
    /// </summary>
    public sealed class TableTestClientReceiveTimeoutTests
    {
        private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task TimeoutLeavesRealSocketOpen_AndLateFrameIsReadableOnce()
        {
            await using var peer = await LoopbackWebSocketPeer.StartAsync();
            await using TableTestClient client = await TableTestClient.ConnectAsync(peer.Url);
            await peer.WaitForConnectionAsync();

            Assert.Equal(WebSocketState.Open, client.State);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReceiveAsync(FastTimeout));

            // The timeout must not have aborted the real ClientWebSocket.
            Assert.Equal(WebSocketState.Open, client.State);
            Assert.True(client.HasPendingReceive);
            Assert.Equal(1, client.UnderlyingReceiveCalls);

            await peer.SendEnvelopeAsync(TableWire.Hello("late-frame"));

            TableWireMessage message = await client.ReceiveAsync(ReadBudget);
            Assert.False(message.IsClose);
            Assert.Equal(MessageType.Hello, message.Envelope!.MessageType);
            Assert.Equal("late-frame", message.Envelope.Hello.Ticket);
            Assert.False(client.HasPendingReceive);

            // Exactly once: the same frame must not be replayed to the next wait.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReceiveAsync(FastTimeout));
            Assert.Equal(WebSocketState.Open, client.State);
        }

        [Fact]
        public async Task RepeatedTimeouts_KeepOneUnderlyingReceive()
        {
            await using var peer = await LoopbackWebSocketPeer.StartAsync();
            await using TableTestClient client = await TableTestClient.ConnectAsync(peer.Url);
            await peer.WaitForConnectionAsync();

            for (int i = 0; i < 4; i++)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => client.ReceiveAsync(FastTimeout));
                Assert.Equal(WebSocketState.Open, client.State);
                Assert.Equal(1, client.UnderlyingReceiveCalls);
            }

            await peer.SendEnvelopeAsync(TableWire.Hello("after-four-timeouts"));
            TableWireMessage message = await client.ReceiveAsync(ReadBudget);
            Assert.Equal("after-four-timeouts", message.Envelope!.Hello.Ticket);
        }

        [Fact]
        public async Task FragmentAcrossTimeouts_StaysCompleteAndDecodesOnce()
        {
            var transport = new ScriptedWebSocket();
            await using TableTestClient client = TableTestClient.AttachForTransportTests(transport);

            byte[] encoded = WireCodec.Encode(TableWire.Hello("fragmented-across-timeout"));
            int first = encoded.Length / 3;
            int second = (encoded.Length * 2) / 3;
            transport.QueueFrame(encoded[..first], endOfMessage: false);
            Task<TableWireMessage> firstWait = client.ReceiveAsync(FastTimeout);
            await transport.WaitForReceiveCallsAsync(2, ReadBudget);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReceiveAsync(FastTimeout));

            Assert.Equal(2, transport.ReceiveCalls);
            Assert.Equal(WebSocketState.Open, transport.State);
            Assert.True(client.HasPendingReceive);

            transport.QueueFrame(encoded[first..second], endOfMessage: false);
            transport.QueueFrame(encoded[second..], endOfMessage: true);

            TableWireMessage message = await client.ReceiveAsync(ReadBudget);
            Assert.Equal(MessageType.Hello, message.Envelope!.MessageType);
            Assert.Equal("fragmented-across-timeout", message.Envelope.Hello.Ticket);
            Assert.False(client.HasPendingReceive);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReceiveAsync(FastTimeout));
        }

        [Fact]
        public async Task ReadUntilTimeout_KeepsReplayOrderAndPendingFrame()
        {
            var transport = new ScriptedWebSocket();
            await using TableTestClient client = TableTestClient.AttachForTransportTests(transport);

            transport.QueueEnvelope(TableWire.Hello("first"));
            transport.QueueEnvelope(TableWire.Hello("middle"));

            // Consume two non-matching frames into the replay buffer, then time out with
            // the complete-message receive still parked.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReadUntilAsync(envelope => envelope.MessageType == MessageType.Receipt, FastTimeout));
            Assert.Equal(3, transport.ReceiveCalls);
            Assert.True(client.HasPendingReceive);

            transport.QueueEnvelope(TableWire.Hello("second"));

            Envelope second = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Hello && envelope.Hello.Ticket == "second",
                ReadBudget);
            Assert.Equal("second", second.Hello.Ticket);

            // The older frames are still replayed in wire order and exactly once.
            Envelope first = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Hello && envelope.Hello.Ticket == "first",
                ReadBudget);
            Assert.Equal("first", first.Hello.Ticket);

            Envelope middle = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Hello && envelope.Hello.Ticket == "middle",
                ReadBudget);
            Assert.Equal("middle", middle.Hello.Ticket);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReadUntilAsync(envelope => envelope.MessageType == MessageType.Receipt, FastTimeout));
            Assert.Equal(WebSocketState.Open, transport.State);
        }

        [Fact]
        public async Task WatermarkAndBaseline_SurviveTimeoutsAndReplay()
        {
            var transport = new ScriptedWebSocket();
            await using TableTestClient client = TableTestClient.AttachForTransportTests(transport);

            transport.QueueEnvelope(PublicSnapshotFrame(10));
            Envelope baseline = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.PublicUpdate,
                ReadBudget);
            Assert.Equal(10UL, baseline.PublicUpdate.Snapshot.StateVersion);
            Assert.Equal(10UL, client.ReadLastBaselineStateVersion);

            // A stale frame is dropped even while the wait times out.
            transport.QueueEnvelope(PublicSnapshotFrame(9));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReadUntilAsync(
                    envelope => envelope.PublicUpdate?.Snapshot?.StateVersion == 11UL,
                    FastTimeout));

            transport.QueueEnvelope(PublicSnapshotFrame(11));
            Envelope fresh = await client.ReadUntilAsync(
                envelope => envelope.PublicUpdate?.Snapshot?.StateVersion == 11UL,
                ReadBudget);
            Assert.Equal(11UL, fresh.PublicUpdate.Snapshot.StateVersion);
            Assert.Equal(11UL, client.ReadLastBaselineStateVersion);

            // A full baseline consumed by a predicate that then times out still advances
            // both the baseline and the watermark; a later wait can recover it from replay.
            transport.QueueEnvelope(PublicSnapshotFrame(12));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReadUntilAsync(_ => false, FastTimeout));
            Assert.Equal(12UL, client.ReadLastBaselineStateVersion);

            Envelope recovered = await client.ReadUntilAsync(
                envelope => envelope.PublicUpdate?.Snapshot?.StateVersion == 12UL,
                ReadBudget);
            Assert.Equal(12UL, recovered.PublicUpdate.Snapshot.StateVersion);

            transport.QueueEnvelope(PublicSnapshotFrame(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReadUntilAsync(
                    envelope => envelope.PublicUpdate?.Snapshot?.StateVersion == 10UL,
                    FastTimeout));
        }

        [Fact]
        public async Task CloseAndTransportFault_PropagateUnchanged()
        {
            var closeTransport = new ScriptedWebSocket();
            await using (TableTestClient closing = TableTestClient.AttachForTransportTests(closeTransport))
            {
                closeTransport.QueueClose(1011, "server-side-fault");
                TableWireMessage message = await closing.ReceiveAsync(ReadBudget);
                Assert.True(message.IsClose);
                Assert.Equal(1011, message.WsCloseCode);
                Assert.Equal("server-side-fault", message.WsCloseReason);

                closeTransport.QueueClose(1011, "server-side-fault");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => closing.ReadUntilAsync(_ => true, ReadBudget));
            }

            var faultTransport = new ScriptedWebSocket();
            await using (TableTestClient faulting = TableTestClient.AttachForTransportTests(faultTransport))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => faulting.ReceiveAsync(FastTimeout));
                faultTransport.QueueFault(new WebSocketException("transport broke"));

                WebSocketException fault = await Assert.ThrowsAsync<WebSocketException>(
                    () => faulting.ReceiveAsync(ReadBudget));
                Assert.Equal("transport broke", fault.Message);
                Assert.Equal(WebSocketState.Aborted, faultTransport.State);
            }
        }

        [Fact]
        public async Task OversizeAndUndecodableFrames_StillFail()
        {
            var oversizeTransport = new ScriptedWebSocket();
            await using (TableTestClient oversize = TableTestClient.AttachForTransportTests(oversizeTransport))
            {
                byte[] chunk = new byte[64 * 1024];
                for (int i = 0; i < 64; i++)
                {
                    oversizeTransport.QueueFrame(chunk, endOfMessage: false);
                }

                oversizeTransport.QueueFrame(new byte[1], endOfMessage: true);
                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => oversize.ReceiveAsync(TimeSpan.FromSeconds(20)));
                Assert.Contains("unbounded", error.Message, StringComparison.Ordinal);
            }

            var undecodableTransport = new ScriptedWebSocket();
            await using (TableTestClient undecodable = TableTestClient.AttachForTransportTests(undecodableTransport))
            {
                undecodableTransport.QueueFrame(new byte[] { 0xFF, 0xFF, 0xFF }, endOfMessage: true);
                Mahjong.Contracts.Wire.WireMalformedException error =
                    await Assert.ThrowsAsync<Mahjong.Contracts.Wire.WireMalformedException>(
                        () => undecodable.ReceiveAsync(ReadBudget));
                Assert.Contains("malformed", error.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Dispose_ObservesParkedReceiveWithoutBackgroundLeak()
        {
            var transport = new ScriptedWebSocket();
            TableTestClient client = TableTestClient.AttachForTransportTests(transport);
            Task<TableWireMessage> read = client.ReceiveAsync(TimeSpan.FromSeconds(30));
            await transport.WaitForReceiveCallsAsync(1, ReadBudget);
            Assert.True(client.HasPendingReceive);

            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(client.HasPendingReceive);
            Assert.Equal(WebSocketState.Aborted, transport.State);
            Assert.Equal(0, transport.ActiveReceives);
            await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));

            // Idempotent teardown: a second dispose returns and does not resurrect work.
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, transport.ActiveReceives);
        }

        [Fact]
        public async Task ConcurrentWaits_NeverIssueConcurrentReceive()
        {
            var transport = new ScriptedWebSocket();
            await using TableTestClient client = TableTestClient.AttachForTransportTests(transport);

            Task<TableWireMessage> first = client.ReceiveAsync(ReadBudget);
            await transport.WaitForReceiveCallsAsync(1, ReadBudget);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ReceiveAsync(TimeSpan.FromMilliseconds(200)));

            Assert.Equal(1, transport.MaxConcurrentReceives);
            Assert.Equal(1, client.UnderlyingReceiveCalls);

            transport.QueueEnvelope(TableWire.Hello("after-contention"));
            TableWireMessage message = await first;
            Assert.Equal("after-contention", message.Envelope!.Hello.Ticket);
        }

        private static Envelope PublicSnapshotFrame(ulong stateVersion)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PublicUpdate,
                PublicUpdate = new PublicUpdate
                {
                    IsFullSnapshot = true,
                    Snapshot = new PublicSnapshot
                    {
                        StateVersion = stateVersion,
                        View = new Mahjong.Contracts.Wire.PublicMatchView()
                    }
                }
            };
        }
    }

    /// <summary>
    /// A controllable real WebSocket transport. ReceiveAsync is the shared boundary driven by
    /// TableTestClient, so timeout behavior is tested through the helper, not reimplemented.
    /// Cancelling a caller token (the old helper's timeout path) aborts the socket exactly as
    /// ManagedWebSocket does; cancelling on Abort/Dispose releases the parked read.
    /// </summary>
    internal sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<ScriptedFrame> _frames = Channel.CreateUnbounded<ScriptedFrame>();
        private readonly CancellationTokenSource _lifetime = new();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;
        private int _receiveCalls;
        private int _activeReceives;
        private int _maxConcurrentReceives;

        public int ReceiveCalls => Volatile.Read(ref _receiveCalls);

        public int ActiveReceives => Volatile.Read(ref _activeReceives);

        public int MaxConcurrentReceives => Volatile.Read(ref _maxConcurrentReceives);

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeDescription;

        public override string? SubProtocol => null;

        public override WebSocketState State => _state;

        public void QueueEnvelope(Envelope envelope)
        {
            QueueFrame(WireCodec.Encode(envelope), endOfMessage: true);
        }

        public void QueueFrame(byte[] bytes, bool endOfMessage)
        {
            _frames.Writer.TryWrite(new ScriptedFrame(bytes, endOfMessage, null, false, default, null));
        }

        public void QueueClose(int code, string reason)
        {
            _frames.Writer.TryWrite(new ScriptedFrame(
                Array.Empty<byte>(),
                endOfMessage: true,
                fault: null,
                isClose: true,
                (WebSocketCloseStatus)code,
                reason));
        }

        public void QueueFault(Exception fault)
        {
            _frames.Writer.TryWrite(new ScriptedFrame(
                Array.Empty<byte>(),
                endOfMessage: true,
                fault,
                isClose: false,
                default,
                null));
        }

        public async Task WaitForReceiveCallsAsync(int expected, TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (ReceiveCalls < expected)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new TimeoutException(
                        "Expected " + expected + " ReceiveAsync calls, saw " + ReceiveCalls + ".");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeReceives);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxConcurrentReceives)) &&
                   Interlocked.CompareExchange(ref _maxConcurrentReceives, active, observed) != observed)
            {
            }

            Interlocked.Increment(ref _receiveCalls);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
                ScriptedFrame frame;
                try
                {
                    frame = await _frames.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                    {
                        // A per-call cancellation is what ManagedWebSocket turns into an
                        // Aborted socket; reproduce it so the old helper's bug stays visible.
                        _state = WebSocketState.Aborted;
                    }

                    throw;
                }

                if (frame.Fault != null)
                {
                    _state = WebSocketState.Aborted;
                    throw frame.Fault;
                }

                if (frame.IsClose)
                {
                    _state = WebSocketState.CloseReceived;
                    _closeStatus = frame.CloseStatus;
                    _closeDescription = frame.CloseReason;
                    return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }

                if (frame.Bytes.Length > buffer.Length)
                {
                    throw new InvalidOperationException("The scripted frame exceeded the receive buffer.");
                }

                frame.Bytes.AsMemory().CopyTo(buffer);
                return new ValueWebSocketReceiveResult(
                    frame.Bytes.Length,
                    WebSocketMessageType.Binary,
                    frame.EndOfMessage);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReceives);
            }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            ValueWebSocketReceiveResult result = await ReceiveAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            return Task.CompletedTask;
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _lifetime.Cancel();
            _frames.Writer.TryComplete();
        }

        public override void Dispose()
        {
            if (_state == WebSocketState.Open)
            {
                _state = WebSocketState.Closed;
            }

            _lifetime.Cancel();
            _frames.Writer.TryComplete();
        }

        private readonly struct ScriptedFrame
        {
            public ScriptedFrame(
                byte[] bytes,
                bool endOfMessage,
                Exception? fault,
                bool isClose,
                WebSocketCloseStatus closeStatus,
                string? closeReason)
            {
                Bytes = bytes;
                EndOfMessage = endOfMessage;
                Fault = fault;
                IsClose = isClose;
                CloseStatus = closeStatus;
                CloseReason = closeReason;
            }

            public byte[] Bytes { get; }

            public bool EndOfMessage { get; }

            public Exception? Fault { get; }

            public bool IsClose { get; }

            public WebSocketCloseStatus CloseStatus { get; }

            public string? CloseReason { get; }
        }
    }

    /// <summary>Minimal real loopback WebSocket peer: one /ws endpoint, frames sent by the test.</summary>
    internal sealed class LoopbackWebSocketPeer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource<WebSocket> _accepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? _socket;

        private LoopbackWebSocketPeer(WebApplication app)
        {
            _app = app;
            Url = string.Empty;
        }

        public string Url { get; private set; }

        public static async Task<LoopbackWebSocketPeer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            app.UseWebSockets();
            var peer = new LoopbackWebSocketPeer(app);
            app.Map("/ws", peer.HandleAsync);
            await app.StartAsync().ConfigureAwait(false);
            string address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            peer.Url = address.Replace("http://", "ws://") + "/ws";
            return peer;
        }

        public async Task WaitForConnectionAsync()
        {
            _socket = await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        public async Task SendEnvelopeAsync(Envelope envelope)
        {
            WebSocket socket = _socket ?? throw new InvalidOperationException("No peer connection.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.SendAsync(
                WireCodec.Encode(envelope),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                timeout.Token).ConfigureAwait(false);
        }

        private async Task HandleAsync(HttpContext context)
        {
            WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            _socket = socket;
            _accepted.TrySetResult(socket);
            try
            {
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "peer-done",
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}

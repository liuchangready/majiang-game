using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.Client.Wss.Tests
{
    internal sealed class FakeWebSocketConnection : IWebSocketConnection
    {
        public WebSocketState State { get; set; } = WebSocketState.None;

        public bool Aborted { get; private set; }

        public bool Disposed { get; private set; }

        public List<byte[]> Sent { get; } = new List<byte[]>();

        public List<(WebSocketCloseStatus Status, string? Description)> CloseOutputs { get; } =
            new List<(WebSocketCloseStatus Status, string? Description)>();

        public Func<CancellationToken, Task>? ConnectHandler { get; set; }

        public Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>>? ReceiveHandler { get; set; }

        public Func<ArraySegment<byte>, WebSocketMessageType, bool, CancellationToken, Task>? SendHandler { get; set; }

        public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            if (ConnectHandler != null)
            {
                await ConnectHandler(cancellationToken).ConfigureAwait(false);
            }

            State = WebSocketState.Open;
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (ReceiveHandler == null)
            {
                return InfiniteReceiveAsync(cancellationToken);
            }

            return ReceiveHandler(buffer, cancellationToken);
        }

        public Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (SendHandler != null)
            {
                return SendHandler(buffer, messageType, endOfMessage, cancellationToken);
            }

            byte[] copy = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            Sent.Add(copy);
            return Task.CompletedTask;
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken)
        {
            CloseOutputs.Add((closeStatus, description));
            State = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public void Abort()
        {
            Aborted = true;
            State = WebSocketState.Aborted;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        private static async Task<WebSocketReceiveResult> InfiniteReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }

    internal sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly Queue<FakeWebSocketConnection> _prepared = new Queue<FakeWebSocketConnection>();

        public List<FakeWebSocketConnection> Connections { get; } = new List<FakeWebSocketConnection>();

        /// <summary>Runs for every created connection before OpenAsync uses it.</summary>
        public Action<FakeWebSocketConnection>? Configure { get; set; }

        public FakeWebSocketConnection Last => Connections[Connections.Count - 1];

        public FakeWebSocketConnection Prepare(Action<FakeWebSocketConnection>? configure = null)
        {
            var connection = new FakeWebSocketConnection();
            configure?.Invoke(connection);
            _prepared.Enqueue(connection);
            return connection;
        }

        public IWebSocketConnection Create()
        {
            FakeWebSocketConnection connection = _prepared.Count > 0
                ? _prepared.Dequeue()
                : new FakeWebSocketConnection();
            Connections.Add(connection);
            Configure?.Invoke(connection);
            return connection;
        }
    }
}

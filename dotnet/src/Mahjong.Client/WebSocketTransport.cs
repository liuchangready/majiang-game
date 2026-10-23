using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.Client
{
    /// <summary>
    /// Narrow socket seam. It exists so timeout/race tests can substitute a
    /// deterministic network double without opening a real socket; production
    /// always uses <see cref="ClientWebSocketConnection"/>.
    /// </summary>
    internal interface IWebSocketConnection : IDisposable
    {
        WebSocketState State { get; }

        Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

        Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);

        Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken);

        Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken);

        void Abort();
    }

    internal interface IWebSocketConnectionFactory
    {
        IWebSocketConnection Create();
    }

    internal sealed class ClientWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        public IWebSocketConnection Create()
        {
            return new ClientWebSocketConnection();
        }
    }

    internal sealed class ClientWebSocketConnection : IWebSocketConnection
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();

        public WebSocketState State => _socket.State;

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return _socket.ConnectAsync(endpoint, cancellationToken);
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return _socket.ReceiveAsync(buffer, cancellationToken);
        }

        public Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken)
        {
            return _socket.CloseOutputAsync(closeStatus, description, cancellationToken);
        }

        public void Abort()
        {
            _socket.Abort();
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}

using System;
using System.IO;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// Real WSS ITableChannel built directly on ClientWebSocket. The production
    /// WireCodec/TableSession still owns every envelope; this class only moves
    /// opaque binary messages and applies the run's strict per-connection
    /// certificate policy. Fragments are accumulated to EndOfMessage and the
    /// message cap is enforced before returning a frame.
    /// </summary>
    public sealed class RealWssTableChannel : ITableChannel, IDisposable
    {
        private readonly Uri _endpoint;
        private readonly X509Certificate2 _trustedRoot;
        private readonly int _maxMessageBytes;
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private ClientWebSocket? _socket;
        private int _disposed;

        public RealWssTableChannel(Uri endpoint, X509Certificate2 trustedRoot, int maxMessageBytes = 1024 * 1024)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _trustedRoot = trustedRoot ?? throw new ArgumentNullException(nameof(trustedRoot));
            _maxMessageBytes = maxMessageBytes;
        }

        public bool IsOpen => _socket != null && _socket.State == WebSocketState.Open;

        public string? LastRejectionReason { get; private set; }

        public WebSocketCloseStatus? LastCloseStatus { get; private set; }

        public string? LastCloseDescription { get; private set; }

        public async Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ThrowIfDisposed();
            ClientWebSocket? previous = _socket;
            _socket = null;
            if (previous != null)
            {
                try
                {
                    previous.Abort();
                }
                catch (Exception)
                {
                }

                previous.Dispose();
            }

            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = ValidatePeer;
            try
            {
                await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
        }

        private bool ValidatePeer(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors policyErrors)
        {
            bool accepted = StrictRootTrust.Validate(
                _trustedRoot,
                certificate,
                chain,
                policyErrors,
                out string? reason);
            if (!accepted)
            {
                LastRejectionReason = reason ?? "strict trust rejected the peer";
            }

            return accepted;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            ClientWebSocket socket = RequireOpen();
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                    wireEnvelope,
                    WebSocketMessageType.Binary,
                    WebSocketMessageFlags.EndOfMessage,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            ClientWebSocket socket = RequireOpen();
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket
                    .ReceiveAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LastCloseStatus = socket.CloseStatus;
                    LastCloseDescription = socket.CloseStatusDescription;
                    throw new TableChannelClosedException(
                        "WS_CLOSE_" + (socket.CloseStatus.HasValue ? ((int)socket.CloseStatus.Value).ToString() : "UNKNOWN"),
                        permanent: true);
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.MalformedMessage, permanent: true);
                }

                if (result.Count > 0)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > _maxMessageBytes)
                    {
                        throw new TableChannelClosedException(ProtocolErrorCode.MessageTooLarge, permanent: true);
                    }
                }

                if (result.EndOfMessage)
                {
                    return message.ToArray();
                }
            }
        }

        public async Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            ClientWebSocket? socket = _socket;
            if (socket == null)
            {
                return;
            }

            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "b1-test-close",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best effort; the transport is disposed below.
            }

            try
            {
                socket.Abort();
            }
            catch (Exception)
            {
            }

            socket.Dispose();
            _socket = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ClientWebSocket? socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try
                {
                    socket.Abort();
                }
                catch (Exception)
                {
                }

                socket.Dispose();
            }

            _sendGate.Dispose();
        }

        private ClientWebSocket RequireOpen()
        {
            ThrowIfDisposed();
            ClientWebSocket? socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, permanent: true);
            }

            return socket;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(RealWssTableChannel));
            }
        }
    }
}

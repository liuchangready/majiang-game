using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts.Wire;

namespace Mahjong.Client.Wss.Tests
{
    internal static class TestCertificates
    {
        public static X509Certificate2 CreateSelfSigned()
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=localhost",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: false));

                using (X509Certificate2 certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(1)))
                {
                    // Re-import so SslStream gets a usable private key handle on
                    // Windows.
                    return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
                }
            }
        }
    }

    /// <summary>
    /// Minimal real loopback WebSocket endpoint used by integration tests. It
    /// speaks just enough RFC 6455 to exercise fragmentation, limits, close and
    /// TLS without taking an ASP.NET dependency.
    /// </summary>
    internal sealed class LocalWebSocketServer : IAsyncDisposable
    {
        public const int MaxFrameBytes = 4 * 1024 * 1024;

        private readonly TcpListener _listener;
        private readonly X509Certificate2? _certificate;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _acceptLoop;
        private readonly object _gate = new object();
        private readonly List<Exception> _errors = new List<Exception>();

        private int _accepted;

        public LocalWebSocketServer(bool tls = false)
        {
            _certificate = tls ? TestCertificates.CreateSelfSigned() : null;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public Uri Endpoint => new Uri((_certificate == null ? "ws" : "wss") + "://127.0.0.1:" + Port + "/");

        public int AcceptedConnections => Volatile.Read(ref _accepted);

        public Func<int, WebSocketServerConnection, Task> Handler { get; set; } =
            (index, connection) => Task.CompletedTask;

        public IReadOnlyList<Exception> Errors
        {
            get
            {
                lock (_gate)
                {
                    return _errors.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Accept loop shutdown is best effort.
            }

            _cts.Dispose();
            _certificate?.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                int index = Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => HandleClientAsync(index, client));
            }
        }

        private async Task HandleClientAsync(int index, TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                Stream stream = client.GetStream();
                try
                {
                    if (_certificate != null)
                    {
                        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                        await ssl.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = _certificate,
                                ClientCertificateRequired = false,
                                EnabledSslProtocols = SslProtocols.None,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                            },
                            _cts.Token).ConfigureAwait(false);
                        stream = ssl;
                    }

                    await HandshakeAsync(stream, _cts.Token).ConfigureAwait(false);
                    using (var connection = new WebSocketServerConnection(stream))
                    {
                        await Handler(index, connection).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Server shutdown.
                }
                catch (EndOfStreamException)
                {
                    // Normal peer disappearance.
                }
                catch (IOException)
                {
                    // Normal socket teardown.
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _errors.Add(ex);
                    }
                }
                finally
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch (Exception)
                    {
                        // Best effort.
                    }
                }
            }
        }

        private static async Task HandshakeAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] request = await ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            string text = Encoding.ASCII.GetString(request);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Expected a WebSocket GET request.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            if (!headers.TryGetValue("Sec-WebSocket-Key", out string? key) || string.IsNullOrEmpty(key))
            {
                throw new InvalidDataException("Missing Sec-WebSocket-Key.");
            }

            string accept;
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                accept = Convert.ToBase64String(hash);
            }

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken)
        {
            const int MaxHeaderBytes = 16 * 1024;
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[1024];
                while (buffer.Length < MaxHeaderBytes)
                {
                    int read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException();
                    }

                    buffer.Write(chunk, 0, read);
                    byte[] current = buffer.ToArray();
                    for (int i = 3; i < current.Length; i++)
                    {
                        if (current[i - 3] == '\r' && current[i - 2] == '\n' && current[i - 1] == '\r' && current[i] == '\n')
                        {
                            byte[] header = new byte[i + 1];
                            Array.Copy(current, header, header.Length);
                            return header;
                        }
                    }
                }
            }

            throw new InvalidDataException("HTTP header exceeded the test-server limit.");
        }
    }

    internal sealed class WebSocketServerConnection : IDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public WebSocketServerConnection(Stream stream)
        {
            _stream = stream;
        }

        public async Task<byte[]> ReadMessageAsync(CancellationToken cancellationToken)
        {
            using (var message = new MemoryStream())
            {
                int? messageOpcode = null;
                while (true)
                {
                    Frame frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                    if (frame.Opcode == 0x8)
                    {
                        await SendCloseAsync(1000, cancellationToken).ConfigureAwait(false);
                        throw new EndOfStreamException();
                    }

                    if (frame.Opcode == 0x9)
                    {
                        await SendRawFrameAsync(true, 0xA, frame.Payload, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (frame.Opcode == 0xA)
                    {
                        continue;
                    }

                    if (frame.Opcode == 0x2 || frame.Opcode == 0x1)
                    {
                        if (messageOpcode != null)
                        {
                            throw new InvalidDataException("New data frame before the previous message ended.");
                        }

                        messageOpcode = frame.Opcode;
                    }
                    else if (frame.Opcode == 0x0)
                    {
                        if (messageOpcode == null)
                        {
                            throw new InvalidDataException("Continuation frame without a data frame.");
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("Unexpected opcode " + frame.Opcode + ".");
                    }

                    message.Write(frame.Payload, 0, frame.Payload.Length);
                    if (frame.Fin)
                    {
                        return message.ToArray();
                    }
                }
            }
        }

        public async Task<Envelope> ReadEnvelopeAsync(CancellationToken cancellationToken)
        {
            byte[] bytes = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                throw new InvalidDataException("Client sent an invalid envelope: " + decoded.Status + "/" + decoded.ErrorCode);
            }

            return decoded.Envelope;
        }

        public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
        {
            return SendRawFrameAsync(true, 0x2, payload, cancellationToken);
        }

        public async Task SendFragmentedBinaryAsync(IReadOnlyList<byte[]> parts, CancellationToken cancellationToken)
        {
            if (parts.Count == 0)
            {
                await SendRawFrameAsync(true, 0x2, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                return;
            }

            for (int i = 0; i < parts.Count; i++)
            {
                bool final = i == parts.Count - 1;
                int opcode = i == 0 ? 0x2 : 0x0;
                await SendRawFrameAsync(final, opcode, parts[i], cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            return SendRawFrameAsync(true, 0x1, Encoding.UTF8.GetBytes(text), cancellationToken);
        }

        public Task SendEmptyBinaryAsync(CancellationToken cancellationToken)
        {
            return SendRawFrameAsync(true, 0x2, Array.Empty<byte>(), cancellationToken);
        }

        public Task SendCloseAsync(int code, CancellationToken cancellationToken)
        {
            var payload = new byte[2];
            payload[0] = (byte)((code >> 8) & 0xFF);
            payload[1] = (byte)(code & 0xFF);
            return SendRawFrameAsync(true, 0x8, payload, cancellationToken);
        }

        public async Task SendRawFrameAsync(bool final, int opcode, byte[] payload, CancellationToken cancellationToken)
        {
            byte[] frame = BuildFrame(final, opcode, payload);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Abort()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        public void Dispose()
        {
            Abort();
            _writeGate.Dispose();
        }

        private async Task<Frame> ReadFrameAsync(CancellationToken cancellationToken)
        {
            byte[] header = await ReadExactAsync(2, cancellationToken).ConfigureAwait(false);
            bool final = (header[0] & 0x80) != 0;
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                byte[] extended = await ReadExactAsync(2, cancellationToken).ConfigureAwait(false);
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                byte[] extended = await ReadExactAsync(8, cancellationToken).ConfigureAwait(false);
                length = 0;
                for (int i = 0; i < extended.Length; i++)
                {
                    length = (length << 8) | extended[i];
                }
            }

            if (length < 0 || length > LocalWebSocketServer.MaxFrameBytes)
            {
                throw new InvalidDataException("Frame length " + length + " exceeds the test-server limit.");
            }

            byte[]? mask = masked ? await ReadExactAsync(4, cancellationToken).ConfigureAwait(false) : null;
            byte[] payload = await ReadExactAsync((int)length, cancellationToken).ConfigureAwait(false);
            if (mask != null)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(payload[i] ^ mask[i % 4]);
                }
            }

            return new Frame(final, opcode, payload);
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await _stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }

            return buffer;
        }

        private static byte[] BuildFrame(bool final, int opcode, byte[] payload)
        {
            int headerLength = payload.Length < 126 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
            byte[] frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)((final ? 0x80 : 0) | (opcode & 0x0F));
            if (payload.Length < 126)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                frame[1] = 126;
                frame[2] = (byte)((payload.Length >> 8) & 0xFF);
                frame[3] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                frame[1] = 127;
                long length = payload.Length;
                for (int i = 0; i < 8; i++)
                {
                    frame[2 + i] = (byte)((length >> (8 * (7 - i))) & 0xFF);
                }
            }

            Array.Copy(payload, 0, frame, headerLength, payload.Length);
            return frame;
        }

        private readonly struct Frame
        {
            public Frame(bool final, int opcode, byte[] payload)
            {
                Fin = final;
                Opcode = opcode;
                Payload = payload;
            }

            public bool Fin { get; }

            public int Opcode { get; }

            public byte[] Payload { get; }
        }
    }
}

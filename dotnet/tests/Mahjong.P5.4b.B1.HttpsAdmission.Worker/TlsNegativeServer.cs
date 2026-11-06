using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// The real loopback TLS endpoint used for the three certificate negatives.
    /// It accepts a TCP connection and performs a genuine SslStream server
    /// handshake with the chosen leaf. The clients under test are the real
    /// strict HTTPS/WSS clients, so no certificate validation is bypassed on the
    /// client side.
    /// </summary>
    public sealed class TlsNegativeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly X509Certificate2 _certificate;
        private Task? _loop;

        private TlsNegativeServer(TcpListener listener, X509Certificate2 certificate)
        {
            _listener = listener;
            _certificate = certificate;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public int AcceptedConnections { get; private set; }

        public string? LastServerError { get; private set; }

        public static TlsNegativeServer Start(string pfxPath)
        {
            var certificate = new X509Certificate2(pfxPath, string.Empty);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new TlsNegativeServer(listener, certificate);
            server._loop = Task.Run(server.AcceptLoopAsync);
            return server;
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

                AcceptedConnections++;
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                    {
                        try
                        {
                            await ssl.AuthenticateAsServerAsync(
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = _certificate,
                                    ClientCertificateRequired = false
                                },
                                _cts.Token).ConfigureAwait(false);
                            // A client that reached here has (wrongly) accepted the
                            // negative certificate. Keep the socket open briefly so
                            // the probe can record the handshake outcome.
                            await Task.Delay(250, _cts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LastServerError = ex.GetType().Name;
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_loop != null)
            {
                try
                {
                    await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _certificate.Dispose();
            _cts.Dispose();
        }
    }
}
